using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Hosting;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.ViewModels;

public sealed partial class ServiceCardViewModel(string serviceName) : ObservableObject
{
    public string ServiceName { get; } = serviceName;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private HealthLevel _health;
    [ObservableProperty] private string _detail = "";
}

public sealed partial class TestResultViewModel(string channel, bool success, string message) : ObservableObject
{
    public string Channel { get; } = channel;
    public bool Success { get; } = success;
    public string Message { get; } = message;
}

public sealed partial class OverviewViewModel(MainViewModel main) : PageViewModel(main, "Overview", "")
{
    private readonly ServiceInspector _inspector = new();

    public ObservableCollection<ServiceCardViewModel> Cards { get; } = [];
    public ObservableCollection<TestResultViewModel> TestResults { get; } = [];

    [ObservableProperty] private string _heroTitle = "";
    [ObservableProperty] private string _heroText = "";
    [ObservableProperty] private HealthLevel _health = HealthLevel.Unknown;
    [ObservableProperty] private bool _stepServicesDone;
    [ObservableProperty] private bool _stepChannelsDone;
    [ObservableProperty] private bool _stepTestDone;
    [ObservableProperty] private bool _stepMonitorDone;
    [ObservableProperty] private string? _pausedText;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _showUpdateButton;
    [ObservableProperty] private bool _showInstall;
    [ObservableProperty] private bool _showStart;
    [ObservableProperty] private bool _showRunningActions;

    public bool AllStepsDone => StepServicesDone && StepChannelsDone && StepTestDone && StepMonitorDone;

    public override void OnShown() => Refresh();

    public void Refresh()
    {
        var config = Main.Config;
        var status = Main.Status;
        var live = Main.IsMonitorRunning && status is not null && status.IsFresh(DateTimeOffset.Now);

        var infos = live
            ? status!.Services
            : config.Services.Select(s => QueryDirect(s.ServiceName, s.DisplayName, s.Enabled)).ToList();
        // Show services in the order of the (possibly unsaved) configuration.
        var ordered = config.Services
            .Select(s => infos.FirstOrDefault(i => i.ServiceName.Equals(s.ServiceName, StringComparison.OrdinalIgnoreCase))
                         ?? QueryDirect(s.ServiceName, s.DisplayName, s.Enabled))
            .ToList();
        Sync(ordered);

        var watched = ordered.Where(i => i.Health != HealthLevel.Disabled).ToList();
        var problems = watched.Count(i => i.Health is HealthLevel.Problem or HealthLevel.Warning);

        StepServicesDone = config.Services.Any(s => s.Enabled);
        StepChannelsDone = config.Channels.Any(c => c.Enabled);
        StepTestDone = StepTestDone || (status?.Channels.Any(c => c.LastSuccess is not null) ?? false);
        StepMonitorDone = live;
        OnPropertyChanged(nameof(AllStepsDone));
        ShowUpdateButton = Main.IsMonitorInstalled && Main.IsDifferentCopyInstalled;
        ShowInstall = Main.ServiceState == MonitorServiceState.NotInstalled;
        ShowStart = Main.ServiceState == MonitorServiceState.Stopped;
        ShowRunningActions = Main.ServiceState == MonitorServiceState.Running;

        PausedText = config.AlertsPausedUntil is { } until && until > DateTimeOffset.Now
            ? $"Alerts are paused until {until.ToLocalTime():ddd HH:mm}. Problems are still tracked, but nobody is notified."
            : null;

        switch (Main.ServiceState)
        {
            case MonitorServiceState.NotInstalled:
                Health = HealthLevel.Unknown;
                HeroTitle = "Background monitoring is not set up yet";
                HeroText = "Choose the services to watch and where alerts should go, then click Install & start. " +
                           "The monitor runs as a Windows service, so it keeps watching after you sign out and starts again after a reboot.";
                break;
            case MonitorServiceState.Stopped:
                Health = HealthLevel.Warning;
                HeroTitle = "Monitoring is stopped";
                HeroText = "Nobody will be notified if a service stops. Click Start to resume monitoring.";
                break;
            case MonitorServiceState.Running when !live:
                Health = HealthLevel.Warning;
                HeroTitle = "The monitor is starting or not reporting";
                HeroText = "The background service is running but has not reported recently. If this persists, click Restart.";
                break;
            case MonitorServiceState.Running:
                Health = problems > 0 ? HealthLevel.Problem : HealthLevel.Ok;
                HeroTitle = watched.Count == 0 ? "Monitoring – no services selected"
                    : problems == 0 ? $"All {watched.Count} monitored service{(watched.Count == 1 ? " is" : "s are")} running"
                    : $"{problems} of {watched.Count} monitored services need attention";
                HeroText = $"Background monitor running since {status!.StartedAt.ToLocalTime():g}. " +
                           $"Checking every {status.PollIntervalSeconds} s and instantly on Windows service events.";
                break;
            default:
                Health = HealthLevel.Pending;
                HeroTitle = Main.MonitorStateText;
                HeroText = "";
                break;
        }
    }

    private ServiceStatusInfo QueryDirect(string name, string displayName, bool enabled)
    {
        if (!enabled) return new ServiceStatusInfo { ServiceName = name, DisplayName = displayName, State = "Not monitored", Health = HealthLevel.Disabled };
        try
        {
            var snap = _inspector.Query(name);
            return new ServiceStatusInfo
            {
                ServiceName = name,
                DisplayName = snap.Exists ? snap.DisplayName : displayName,
                State = ServiceSnapshot.Describe(snap.State),
                Health = snap.State == ServiceRunState.Running ? HealthLevel.Ok : snap.IsPending ? HealthLevel.Pending : HealthLevel.Problem,
                Detail = Main.IsMonitorRunning ? "Waiting for the monitor to pick up this service" : "Live state – background monitoring is not running",
            };
        }
        catch (Exception ex)
        {
            return new ServiceStatusInfo { ServiceName = name, DisplayName = displayName, State = "Unknown", Health = HealthLevel.Unknown, Detail = ex.Message };
        }
    }

    private void Sync(List<ServiceStatusInfo> infos)
    {
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!infos.Any(x => x.ServiceName.Equals(Cards[i].ServiceName, StringComparison.OrdinalIgnoreCase))) Cards.RemoveAt(i);
        }
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            var card = Cards.FirstOrDefault(c => c.ServiceName.Equals(info.ServiceName, StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                card = new ServiceCardViewModel(info.ServiceName);
                Cards.Insert(Math.Min(i, Cards.Count), card);
            }
            else if (Cards.IndexOf(card) != i)
            {
                Cards.Move(Cards.IndexOf(card), Math.Min(i, Cards.Count - 1));
            }
            card.DisplayName = string.IsNullOrWhiteSpace(info.DisplayName) ? info.ServiceName : info.DisplayName;
            card.State = info.State;
            card.Health = info.Health;
            card.Detail = info.Detail;
        }
    }

    // ------------------------------------------------------------------ monitor service control

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!Main.Config.Services.Any(s => s.Enabled) || !Main.Config.Channels.Any(c => c.Enabled))
        {
            var answer = MessageBox.Show(
                "You have not chosen any services and/or notification channels yet. Install the monitor anyway?",
                AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        if (Main.IsDirty) Main.Save();
        try
        {
            await Main.RunBusyAsync("Installing…", progress => Task.Run(() => MonitorServiceInstaller.InstallOrUpdate(progress)));
            Main.ShowToast($"Background monitoring is running. Installed to {AppInfo.InstallDirectory} with a Start menu shortcut.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            Main.ShowToast($"Install failed: {ex.Message}", ToastLevel.Error);
        }
        Refresh();
    }

    [RelayCommand]
    private Task StartMonitorAsync() => ControlAsync("Starting the monitor…", () => MonitorServiceInstaller.Start(TimeSpan.FromSeconds(45)), "Monitoring started.");

    [RelayCommand]
    private Task StopMonitorAsync()
    {
        var answer = MessageBox.Show("While the monitor is stopped, nobody is notified about service problems. Stop it?",
            AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes
            ? ControlAsync("Stopping the monitor…", () => MonitorServiceInstaller.Stop(TimeSpan.FromSeconds(45)), "Monitoring stopped.")
            : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RestartMonitorAsync() =>
        ControlAsync("Restarting the monitor…", () => MonitorServiceInstaller.Restart(TimeSpan.FromSeconds(45)), "Monitor restarted.");

    private async Task ControlAsync(string busy, Action action, string done)
    {
        try
        {
            if (Main.IsDirty) Main.Save();
            await Main.RunBusyAsync(busy, _ => Task.Run(action));
            Main.ShowToast(done, ToastLevel.Success);
        }
        catch (Exception ex)
        {
            Main.ShowToast(ex.InnerException?.Message ?? ex.Message, ToastLevel.Error);
        }
        Refresh();
    }

    // ------------------------------------------------------------------ maintenance mode

    [RelayCommand]
    private void PauseAlerts(string minutes)
    {
        var until = minutes == "tomorrow"
            ? new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(8))
            : DateTimeOffset.Now.AddMinutes(int.Parse(minutes));
        Main.SaveImmediately(c => c.AlertsPausedUntil = until);
        Main.ShowToast($"Alerts paused until {until:ddd HH:mm}.", ToastLevel.Warning);
        Refresh();
    }

    [RelayCommand]
    private void ResumeAlerts()
    {
        Main.SaveImmediately(c => c.AlertsPausedUntil = null);
        Main.ShowToast("Alerts resumed.", ToastLevel.Success);
        Refresh();
    }

    // ------------------------------------------------------------------ test

    [RelayCommand]
    private async Task SendTestToAllAsync()
    {
        var channels = Main.Config.Channels.Where(c => c.Enabled).ToList();
        if (channels.Count == 0)
        {
            Main.ShowToast("Add a notification channel first.", ToastLevel.Warning);
            Main.Navigate(Main.Channels);
            return;
        }

        IsTesting = true;
        TestResults.Clear();
        try
        {
            var tasks = channels.Select(async channel =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var result = await Main.Sender.SendAsync(channel, Main.Config, SystemNotifications.Test(Main.Config, channel.Name), cts.Token);
                    return new TestResultViewModel(channel.Name, true, result);
                }
                catch (Exception ex)
                {
                    return new TestResultViewModel(channel.Name, false, ex.Message);
                }
            }).ToList();
            foreach (var result in await Task.WhenAll(tasks)) TestResults.Add(result);
            if (TestResults.All(r => r.Success)) StepTestDone = true;
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(AllStepsDone));
        }
    }

    [RelayCommand]
    private void GoToServices() => Main.Navigate(Main.Services);

    [RelayCommand]
    private void GoToChannels() => Main.Navigate(Main.Channels);
}
