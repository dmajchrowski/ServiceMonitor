using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace ServiceMonitor.Core.Windows;

public enum ServiceRunState
{
    NotFound = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

public sealed record ServiceSnapshot(
    string ServiceName,
    string DisplayName,
    ServiceRunState State,
    int Win32ExitCode = 0,
    int ServiceSpecificExitCode = 0,
    int ProcessId = 0,
    string StartType = "",
    string ImagePath = "")
{
    public bool Exists => State != ServiceRunState.NotFound;

    public bool IsPending => State is ServiceRunState.StartPending or ServiceRunState.StopPending
        or ServiceRunState.ContinuePending or ServiceRunState.PausePending;

    public string? ExecutablePath => ImagePathParser.GetExecutable(ImagePath);

    /// <summary>Human readable exit code, or null when the service exited cleanly.</summary>
    public string? ExitCodeDescription
    {
        get
        {
            const int ErrorServiceSpecificError = 1066, ErrorServiceNeverStarted = 1077;
            if (Win32ExitCode is 0 or ErrorServiceNeverStarted) return null;
            if (Win32ExitCode == ErrorServiceSpecificError)
                return $"Service-specific error {ServiceSpecificExitCode} (0x{ServiceSpecificExitCode:X})";
            return $"{Win32ExitCode} – {new Win32Exception(Win32ExitCode).Message}";
        }
    }

    public static string Describe(ServiceRunState state) => state switch
    {
        ServiceRunState.NotFound => "Not installed",
        ServiceRunState.StartPending => "Starting",
        ServiceRunState.StopPending => "Stopping",
        ServiceRunState.ContinuePending => "Resuming",
        ServiceRunState.PausePending => "Pausing",
        _ => state.ToString(),
    };
}

public sealed record ServiceListItem(string ServiceName, string DisplayName, ServiceRunState State, string StartType);

public sealed record ServiceActionResult(bool Success, string Message);

public interface IServiceInspector
{
    ServiceSnapshot Query(string serviceName);
    IReadOnlyList<ServiceListItem> ListServices();
    Task<ServiceActionResult> StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>When the service's process started, so "Running since" is accurate the first time a service is seen.</summary>
    DateTimeOffset? GetProcessStartTime(int processId) => null;
}

/// <summary>Reads service state straight from the Service Control Manager, including exit codes and the process id.</summary>
public sealed class ServiceInspector : IServiceInspector
{
    public ServiceSnapshot Query(string serviceName)
    {
        var scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot connect to the Service Control Manager");
        try
        {
            var svc = Native.OpenService(scm, serviceName, Native.SERVICE_QUERY_STATUS | Native.SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == Native.ERROR_SERVICE_DOES_NOT_EXIST) return new ServiceSnapshot(serviceName, serviceName, ServiceRunState.NotFound);
                throw new Win32Exception(error, $"Cannot open service '{serviceName}'");
            }
            try
            {
                if (!Native.QueryServiceStatusEx(svc, Native.SC_STATUS_PROCESS_INFO, out var status,
                        Marshal.SizeOf<Native.SERVICE_STATUS_PROCESS>(), out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot query service '{serviceName}'");
                }

                var (displayName, startType, imagePath) = QueryConfig(svc);
                return new ServiceSnapshot(
                    serviceName,
                    string.IsNullOrEmpty(displayName) ? serviceName : displayName,
                    (ServiceRunState)status.dwCurrentState,
                    status.dwWin32ExitCode,
                    status.dwServiceSpecificExitCode,
                    status.dwProcessId,
                    startType,
                    imagePath);
            }
            finally
            {
                Native.CloseServiceHandle(svc);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }

    private static (string DisplayName, string StartType, string ImagePath) QueryConfig(IntPtr svc)
    {
        Native.QueryServiceConfig(svc, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return ("", "", "");
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!Native.QueryServiceConfig(svc, buffer, needed, out _)) return ("", "", "");
            var config = Marshal.PtrToStructure<Native.QUERY_SERVICE_CONFIG>(buffer);
            var startType = config.dwStartType switch
            {
                0 => "Boot",
                1 => "System",
                2 => IsDelayedAutoStart(svc) ? "Automatic (Delayed Start)" : "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => config.dwStartType.ToString(),
            };
            return (Marshal.PtrToStringUni(config.lpDisplayName) ?? "", startType, Marshal.PtrToStringUni(config.lpBinaryPathName) ?? "");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsDelayedAutoStart(IntPtr svc)
    {
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            return Native.QueryServiceConfig2(svc, Native.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, buffer, 16, out _)
                   && Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public IReadOnlyList<ServiceListItem> ListServices()
    {
        var result = new List<ServiceListItem>();
        foreach (var sc in ServiceController.GetServices())
        {
            using (sc)
            {
                try
                {
                    var startType = sc.StartType switch
                    {
                        ServiceStartMode.Automatic => "Automatic",
                        ServiceStartMode.Manual => "Manual",
                        ServiceStartMode.Disabled => "Disabled",
                        var other => other.ToString(),
                    };
                    result.Add(new ServiceListItem(sc.ServiceName, sc.DisplayName, (ServiceRunState)(int)sc.Status, startType));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Service removed while enumerating, or access denied on a protected service.
                }
            }
        }
        return result.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public DateTimeOffset? GetProcessStartTime(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public Task<ServiceActionResult> StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running) return new ServiceActionResult(true, "Already running");
                if (sc.Status is ServiceControllerStatus.Stopped) sc.Start();

                var started = DateTime.UtcNow;
                while (DateTime.UtcNow - started < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running) return new ServiceActionResult(true, "Service started");
                    if (sc.Status == ServiceControllerStatus.Stopped && DateTime.UtcNow - started > TimeSpan.FromSeconds(3))
                    {
                        var snap = Query(serviceName);
                        return new ServiceActionResult(false, "The service stopped again right after starting"
                            + (snap.ExitCodeDescription is { } code ? $" (exit code {code})" : ""));
                    }
                    Thread.Sleep(500);
                }
                return new ServiceActionResult(false, $"The service did not reach Running within {timeout.TotalSeconds:0} s (state: {sc.Status})");
            }
            catch (InvalidOperationException ex)
            {
                return new ServiceActionResult(false, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Win32Exception ex)
            {
                return new ServiceActionResult(false, ex.Message);
            }
        }, cancellationToken);

    private static class Native
    {
        public const int SC_MANAGER_CONNECT = 0x0001;
        public const int SERVICE_QUERY_CONFIG = 0x0001;
        public const int SERVICE_QUERY_STATUS = 0x0004;
        public const int SC_STATUS_PROCESS_INFO = 0;
        public const int SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
        public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
            public int dwProcessId;
            public int dwServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct QUERY_SERVICE_CONFIG
        {
            public int dwServiceType;
            public int dwStartType;
            public int dwErrorControl;
            public IntPtr lpBinaryPathName;
            public IntPtr lpLoadOrderGroup;
            public int dwTagId;
            public IntPtr lpDependencies;
            public IntPtr lpServiceStartName;
            public IntPtr lpDisplayName;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenService(IntPtr scManager, string serviceName, int desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out SERVICE_STATUS_PROCESS buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryServiceConfig2(IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}

public static class ImagePathParser
{
    /// <summary>Extracts the executable from a service ImagePath, which may be quoted, unquoted with spaces, or contain arguments.</summary>
    public static string? GetExecutable(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var path = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            path = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + path[11..];
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];

        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            return end > 1 ? path[1..end] : path.Trim('"');
        }

        var exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return path[..(exe + 4)];
        var space = path.IndexOf(' ');
        return space > 0 ? path[..space] : path;
    }
}
