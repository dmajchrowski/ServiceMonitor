using System.Reflection;

namespace ServiceMonitor.Core;

/// <summary>Product identity and well-known locations shared by the configuration app and the background service.</summary>
public static class AppInfo
{
    public const string ProductName = "Service Monitor";
    /// <summary>Until 1.0.8 the product was called "Windows Service Monitor" ("Windows" is Microsoft's trademark).</summary>
    public const string LegacyProductName = "Windows Service Monitor";
    public const string ServiceName = "ServiceMonitor";
    /// <summary>The service's name before 1.0.9; an update replaces it (MonitorServiceInstaller).</summary>
    public const string LegacyServiceName = "WinServiceMonitor";
    public const string ServiceDescription =
        "Watches selected Windows services and sends email, chat and push notifications when they stop, crash or hang.";
    public const string EventLogSource = "Service Monitor";
    public const string ExeName = "ServiceMonitor.exe";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string MachineName { get; } = Environment.MachineName;

    /// <summary>Where the app installs itself: C:\Program Files\Service Monitor.</summary>
    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    /// <summary>Where versions before the rename installed themselves; removed when the monitor is updated.</summary>
    public static string LegacyInstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), LegacyProductName);

    /// <summary>C:\ProgramData\ServiceMonitor.</summary>
    public static string CurrentDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServiceMonitor");

    /// <summary>The settings folder before 1.0.9; an update moves it to <see cref="CurrentDataDirectory"/>.</summary>
    public static string LegacyDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsServiceMonitor");

    private static string? _dataDirectoryOverride;

    /// <summary>
    /// The settings folder. An install from before 1.0.9 keeps using its old folder until the update moves it, so this
    /// window and a monitor that is still the old version always read the same settings.
    /// </summary>
    public static string DataDirectory => _dataDirectoryOverride
        ?? (!Directory.Exists(CurrentDataDirectory) && Directory.Exists(LegacyDataDirectory) ? LegacyDataDirectory : CurrentDataDirectory);

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string StatusFile => Path.Combine(DataDirectory, "status.json");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string TokensDirectory => Path.Combine(DataDirectory, "tokens");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Lets tests point all storage at a temporary folder.</summary>
    internal static void OverrideDataDirectory(string path) => _dataDirectoryOverride = path;
}
