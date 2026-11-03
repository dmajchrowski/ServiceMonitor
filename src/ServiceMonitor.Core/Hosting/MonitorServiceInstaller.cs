using System.Diagnostics;
using System.ServiceProcess;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Security;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Core.Hosting;

public enum MonitorServiceState { NotInstalled, Stopped, Starting, Running, Stopping, Unknown }

/// <summary>Installs, updates, starts, stops and removes the background monitor (a Windows service running this same exe with --service).</summary>
public static class MonitorServiceInstaller
{
    public static string InstalledExePath => Path.Combine(AppInfo.InstallDirectory, AppInfo.ExeName);

    /// <summary>
    /// The name the monitor's service is installed under: <see cref="AppInfo.ServiceName"/>, or the name from before
    /// 1.0.9 until an update replaces it.
    /// </summary>
    public static string InstalledServiceName =>
        ServiceExists(AppInfo.ServiceName) || !ServiceExists(AppInfo.LegacyServiceName) ? AppInfo.ServiceName : AppInfo.LegacyServiceName;

    public static MonitorServiceState GetState()
    {
        try
        {
            using var sc = new ServiceController(InstalledServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => MonitorServiceState.Running,
                ServiceControllerStatus.Stopped => MonitorServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => MonitorServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => MonitorServiceState.Stopping,
                _ => MonitorServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            return MonitorServiceState.NotInstalled;
        }
    }

    /// <summary>Path of the exe the installed service runs, or null if not installed.</summary>
    public static string? GetServiceExePath()
    {
        var snap = new ServiceInspector().Query(InstalledServiceName);
        return snap.Exists ? snap.ExecutablePath : null;
    }

    /// <summary>
    /// Copies the running exe to Program Files (unless it already runs from there), registers the service with
    /// automatic start and restart-on-failure, creates a Start menu shortcut and starts the service. Replaces an install
    /// from before 1.0.9 on the way: its service, settings folder, program folder and shortcut.
    /// </summary>
    public static void InstallOrUpdate(IProgress<string>? progress = null)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program location.");
        var target = InstalledExePath;

        var exists = ServiceExists(AppInfo.ServiceName);
        if (exists || ServiceExists(AppInfo.LegacyServiceName))
        {
            progress?.Report("Stopping the monitor service…");
            Stop(InstalledServiceName, TimeSpan.FromSeconds(45));
        }
        if (ServiceExists(AppInfo.LegacyServiceName))
        {
            progress?.Report($"Replacing the {AppInfo.LegacyServiceName} service…");
            // Free its display name first: a deleted service keeps it until every handle to it is closed.
            Sc("config", AppInfo.LegacyServiceName, "DisplayName=", AppInfo.LegacyServiceName);
            Sc("delete", AppInfo.LegacyServiceName);
        }
        MoveLegacyData(progress);

        progress?.Report("Preparing data folder…");
        SecureFolder.EnsureLockedDown(AppInfo.DataDirectory);
        WindowsEventLog.EnsureSource();

        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report($"Copying program to {AppInfo.InstallDirectory}…");
            Directory.CreateDirectory(AppInfo.InstallDirectory);
            CopyWithRetry(source, target);
        }

        var binPath = $"\"{target}\" --service";
        progress?.Report(exists ? "Updating the service registration…" : "Registering the Windows service…");
        if (exists)
            Sc("config", AppInfo.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", AppInfo.ProductName);
        else
            Sc("create", AppInfo.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", AppInfo.ProductName);
        Sc("description", AppInfo.ServiceName, AppInfo.ServiceDescription);
        // If the monitor itself ever crashes, Windows restarts it (10 s, 30 s, then every 60 s).
        Sc("failure", AppInfo.ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000");
        Sc("failureflag", AppInfo.ServiceName, "1");

        progress?.Report("Creating Start menu shortcut…");
        TryCreateStartMenuShortcut(target);
        RemoveLegacyInstall();

        progress?.Report("Starting the monitor service…");
        Start(TimeSpan.FromSeconds(45));
    }

    public static void Uninstall(bool removeData, IProgress<string>? progress = null)
    {
        foreach (var name in new[] { AppInfo.ServiceName, AppInfo.LegacyServiceName }.Where(ServiceExists))
        {
            progress?.Report("Stopping the monitor service…");
            Stop(name, TimeSpan.FromSeconds(45));
            progress?.Report("Removing the Windows service…");
            Sc("delete", name);
        }
        TryDelete(StartMenuShortcutPath);
        TryDelete(LegacyStartMenuShortcutPath);
        if (!removeData) return;
        foreach (var dir in new[] { AppInfo.CurrentDataDirectory, AppInfo.LegacyDataDirectory }.Where(Directory.Exists))
        {
            progress?.Report("Deleting settings and sign-ins…");
            Directory.Delete(dir, recursive: true);
        }
    }

    public static void Start(TimeSpan timeout)
    {
        using var sc = new ServiceController(InstalledServiceName);
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public static void Stop(TimeSpan timeout) => Stop(InstalledServiceName, timeout);

    private static void Stop(string serviceName, TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.CanStop && sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    private static bool ServiceExists(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the settings folder from before 1.0.9 (C:\ProgramData\WindowsServiceMonitor) to its new place. The monitor is
    /// stopped by now. Saved passwords stay readable: their encryption does not depend on the folder.
    /// </summary>
    private static void MoveLegacyData(IProgress<string>? progress)
    {
        var legacy = AppInfo.LegacyDataDirectory;
        var current = AppInfo.CurrentDataDirectory;
        if (!Directory.Exists(legacy) || Directory.Exists(current)) return;
        progress?.Report($"Moving settings to {current}…");
        try
        {
            Directory.Move(legacy, current); // same drive: a rename, which keeps the folder's permissions
        }
        catch (IOException)
        {
            // A file in it is still open (e.g. an old window in another session): copy, and leave the old folder.
            SecureFolder.Ensure(current);
            CopyDirectory(legacy, current);
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    public static void Restart(TimeSpan timeout)
    {
        Stop(timeout);
        Start(timeout);
    }

    private static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppInfo.ProductName + ".lnk");

    private static string LegacyStartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppInfo.LegacyProductName + ".lnk");

    /// <summary>
    /// Removes what a version from before the rename left behind: its Start menu shortcut, and its program folder when
    /// that holds nothing but the old exe. A folder installed by the setup (it has an uninstaller) is left to the setup,
    /// which removes it on its next run.
    /// </summary>
    private static void RemoveLegacyInstall()
    {
        TryDelete(LegacyStartMenuShortcutPath);
        var dir = AppInfo.LegacyInstallDirectory;
        if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir)
                .Any(f => !Path.GetFileName(f).Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase))) return;
        TryDelete(Path.Combine(dir, AppInfo.ExeName));
        try { Directory.Delete(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryCreateStartMenuShortcut(string target)
    {
        try
        {
            var script = "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:WSM_LNK); $s.TargetPath = $env:WSM_TARGET; " +
                         "$s.WorkingDirectory = Split-Path $env:WSM_TARGET; $s.Description = $env:WSM_DESC; $s.Save()";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            };
            psi.Environment["WSM_LNK"] = StartMenuShortcutPath;
            psi.Environment["WSM_TARGET"] = target;
            psi.Environment["WSM_DESC"] = AppInfo.ServiceDescription;
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A missing shortcut is cosmetic.
        }
    }

    private static void CopyWithRetry(string source, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500); // the old service process can take a moment to exit
            }
            catch (IOException ex)
            {
                throw new IOException($"Could not replace {target}. Close any other open copy of {AppInfo.ProductName} and try again. ({ex.Message})", ex);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not run sc.exe");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"sc.exe {args[0]} failed ({p.ExitCode}): {output.Trim()}");
    }
}
