using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Hosting;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Notifications;
using ServiceMonitor.Core.OAuth;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.ViewModels;

public enum ToastLevel { Info, Success, Warning, Error }

public abstract partial class PageViewModel(MainViewModel main, string title, string glyph) : ObservableObject
{
    public MainViewModel Main { get; } = main;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnShown() { }

    /// <summary>Called after the configuration was replaced (discard / reload).</summary>
    public virtual void OnConfigReplaced() { }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _toastTimer;
    private string _savedJson;

    public MainViewModel()
    {
        SecureFolder.Ensure(AppInfo.DataDirectory);
        _config = ConfigStore.Load();
        if (ReprotectPlaintextSecrets(_config)) ConfigStore.Save(_config);
        _savedJson = ConfigStore.Serialize(_config);

        Http = ChannelSender.CreateHttpClient();
        OAuth = new OAuthClient(Http);
        Sender = new ChannelSender(Http, OAuth);

        Overview = new OverviewViewModel(this);
        Services = new ServicesViewModel(this);
        Channels = new ChannelsViewModel(this);
        Settings = new SettingsViewModel(this);
        Activity = new ActivityViewModel(this);
        Pages = [Overview, Services, Channels, Settings, Activity];
        _selectedPage = Overview;

        RefreshMonitorState();
        Overview.Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };
    }

    public HttpClient Http { get; }
    public OAuthClient OAuth { get; }
    public ChannelSender Sender { get; }

    public OverviewViewModel Overview { get; }
    public ServicesViewModel Services { get; }
    public ChannelsViewModel Channels { get; }
    public SettingsViewModel Settings { get; }
    public ActivityViewModel Activity { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    public string MachineName => AppInfo.MachineName;
    public string Version => AppInfo.Version;

    [ObservableProperty] private MonitorConfig _config;
    [ObservableProperty] private PageViewModel _selectedPage;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private MonitorStatus? _status;
    [ObservableProperty] private MonitorServiceState _serviceState;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private ToastLevel _toastLevel;

    public bool IsMonitorRunning => ServiceState == MonitorServiceState.Running;
    public bool IsMonitorInstalled => ServiceState != MonitorServiceState.NotInstalled;

    /// <summary>The installed service runs an older/different copy of the program than this window.</summary>
    public bool IsDifferentCopyInstalled
    {
        get
        {
#if DEBUG
            // Screenshot mode runs a debug build, which always differs from the installed release.
            if (Environment.GetEnvironmentVariable("WSM_SNAPSHOT_DIR") is not null) return false;
#endif
            var installed = SafeServicePath();
            return installed is not null && Environment.ProcessPath is { } me
                   && !string.Equals(Path.GetFullPath(installed), Path.GetFullPath(me), StringComparison.OrdinalIgnoreCase)
                   && File.Exists(installed) && FileVersionDiffers(installed, me);
        }
    }

    public string MonitorStateText => ServiceState switch
    {
        MonitorServiceState.Running when Status is { } s && !s.IsFresh(DateTimeOffset.Now) => "Running (not responding)",
        MonitorServiceState.Running => "Monitoring",
        MonitorServiceState.NotInstalled => "Not installed",
        MonitorServiceState.Stopped => "Stopped",
        MonitorServiceState.Starting => "Starting…",
        MonitorServiceState.Stopping => "Stopping…",
        _ => "Unknown",
    };

    partial void OnSelectedPageChanged(PageViewModel value) => value.OnShown();

    partial void OnServiceStateChanged(MonitorServiceState value)
    {
        OnPropertyChanged(nameof(IsMonitorRunning));
        OnPropertyChanged(nameof(IsMonitorInstalled));
        OnPropertyChanged(nameof(MonitorStateText));
        OnPropertyChanged(nameof(IsDifferentCopyInstalled));
    }

    partial void OnStatusChanged(MonitorStatus? value) => OnPropertyChanged(nameof(MonitorStateText));

    public void Navigate(PageViewModel page) => SelectedPage = page;

    public void ShowToast(string message, ToastLevel level = ToastLevel.Info)
    {
        ToastLevel = level;
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(level == ToastLevel.Error ? 12 : 6);
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;

    private void Tick()
    {
        RefreshMonitorState();
        IsDirty = ConfigStore.Serialize(Config) != _savedJson;
        Overview.Refresh();
        if (SelectedPage == Activity) Activity.Refresh();
    }

    public void RefreshMonitorState()
    {
        ServiceState = MonitorServiceInstaller.GetState();
        Status = ServiceState == MonitorServiceState.NotInstalled ? null : MonitorStatus.Load();
    }

    [RelayCommand]
    public void Save()
    {
        try
        {
            foreach (var s in Config.Services.Where(s => s.PortCheck is { Port: <= 0 })) s.PortCheck = null;
            ConfigStore.Save(Config);
            _savedJson = ConfigStore.Serialize(Config);
            IsDirty = false;
            TokenStore.DeleteOrphans(Config.Channels.Select(c => c.Id));
            ShowToast(IsMonitorRunning
                ? "Saved. The monitor applies the changes within a few seconds."
                : "Saved. Start background monitoring on the Overview page to begin watching.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Could not save: {ex.Message}", ToastLevel.Error);
        }
    }

    [RelayCommand]
    private void Discard()
    {
        Config = ConfigStore.Load();
        _savedJson = ConfigStore.Serialize(Config);
        IsDirty = false;
        foreach (var page in Pages) page.OnConfigReplaced();
        ShowToast("Changes discarded.");
    }

    /// <summary>Saves one setting that should apply immediately (e.g. pausing alerts) without saving other pending edits.</summary>
    public void SaveImmediately(Action<MonitorConfig> change)
    {
        var onDisk = ConfigStore.Load();
        change(onDisk);
        ConfigStore.Save(onDisk);
        change(Config);
        // Keep the "unsaved changes" baseline in step with what is now on disk.
        var baseline = ConfigStore.Deserialize(_savedJson);
        change(baseline);
        _savedJson = ConfigStore.Serialize(baseline);
        IsDirty = ConfigStore.Serialize(Config) != _savedJson;
    }

    public async Task RunBusyAsync(string text, Func<IProgress<string>, Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try
        {
            await work(new Progress<string>(m => BusyText = m));
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            RefreshMonitorState();
        }
    }

    public bool ConfirmCloseWithUnsavedChanges()
    {
        IsDirty = ConfigStore.Serialize(Config) != _savedJson;
        if (!IsDirty) return true;
        var answer = MessageBox.Show("You have unsaved changes. Save them before closing?", AppInfo.ProductName,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) Save();
        return !IsDirty || answer == MessageBoxResult.No;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {url}: {ex.Message}", AppInfo.ProductName);
        }
    }

    [RelayCommand]
    private static void OpenDataFolder() => OpenUrl(AppInfo.DataDirectory);

    [RelayCommand]
    private static void OpenLogsFolder()
    {
        Directory.CreateDirectory(AppInfo.LogsDirectory);
        OpenUrl(AppInfo.LogsDirectory);
    }

    /// <summary>Encrypts secrets that were typed into config.json by hand (e.g. by a deployment script).</summary>
    private static bool ReprotectPlaintextSecrets(MonitorConfig config)
    {
        var before = ConfigStore.Serialize(config);
        static string P(string v) => v.Length == 0 || v.StartsWith("dpapi:", StringComparison.Ordinal) ? v : SecretProtector.Protect(v);
        foreach (var c in config.Channels) c.TransformSecrets(P);
        return ConfigStore.Serialize(config) != before;
    }

    private static string? SafeServicePath()
    {
        try { return MonitorServiceInstaller.GetServiceExePath(); }
        catch (Exception) { return null; }
    }

    private static bool FileVersionDiffers(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length != fb.Length || FileVersionInfo.GetVersionInfo(a).FileVersion != FileVersionInfo.GetVersionInfo(b).FileVersion;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
