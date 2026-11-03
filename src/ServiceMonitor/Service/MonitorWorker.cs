using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Notifications;
using ServiceMonitor.Core.OAuth;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Service;

/// <summary>
/// The background monitor loop: reload settings when they change, check services, write status for the app,
/// and handle the monitor's own notices (started / stopping / heartbeat) and OAuth token keep-alive.
/// </summary>
public sealed class MonitorWorker(ILogger<MonitorWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TokenKeepAliveInterval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = TimeProvider.System;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly DateTimeOffset _bootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private volatile MonitorConfig _config = new();
    private DateTime _configStamp;
    private ActivityLog _activity = null!;
    private NotificationDispatcher _dispatcher = null!;
    private OAuthClient _oauth = null!;
    private MonitorEngine _engine = null!;
    private PersistentState _state = new();
    private DateTimeOffset _nextKeepAliveAttempt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WindowsEventLog.EnsureSource();
        _activity = new ActivityLog(logger, new WindowsEventLog(), _time);
        _activity.Seed(MonitorStatus.Load()?.Activity);
        _state = PersistentState.Load();

        LoadConfig(initial: true);

        var http = ChannelSender.CreateHttpClient();
        _oauth = new OAuthClient(http, _time);
        _dispatcher = new NotificationDispatcher(new ChannelSender(http, _oauth), _activity, () => _config);
        _engine = new MonitorEngine(new ServiceInspector(), new EventDiagnostics(), new TcpPortProbe(), _dispatcher, _activity, _time, _bootTime);

        using var watcher = new ScmEventWatcher();
        watcher.EventReceived += e =>
        {
            _engine.ReportScmEvent(e);
            if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
        };
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            _activity.Add(ActivityLevel.Warning, $"Live event log monitoring is unavailable, relying on polling only: {ex.Message}");
        }

        var watchedCount = _config.Services.Count(s => s.Enabled);
        _activity.Add(ActivityLevel.Info, $"{AppInfo.ProductName} {AppInfo.Version} started – watching {watchedCount} service(s) through {_config.Channels.Count(c => c.Enabled)} channel(s)",
            eventId: EventIds.MonitorStarted);

        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LoadConfig(initial: false);
                await _engine.PollAsync(_config, stoppingToken);

                if (first)
                {
                    first = false;
                    if (_config.Settings.NotifyOnMonitorStartStop && !_config.AlertsPaused(_time.GetUtcNow()))
                        _dispatcher.Enqueue(SystemNotifications.MonitorStarted(_config, _engine.GetStatus(_config), _bootTime, _time));
                }

                SendHeartbeatIfDue();
                await KeepTokensAliveIfDueAsync(stoppingToken);
                WriteStatus();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitoring cycle failed");
                _activity.Add(ActivityLevel.Error, $"Monitoring cycle failed: {ex.Message}");
            }

            try
            {
                var interval = TimeSpan.FromSeconds(Math.Clamp(_config.Settings.PollIntervalSeconds, 2, 3600));
                if (await _wake.WaitAsync(interval, stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1.5), stoppingToken); // let the Service Control Manager settle
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_dispatcher is not null)
        {
            _activity.Add(ActivityLevel.Warning, $"{AppInfo.ProductName} is stopping", eventId: EventIds.MonitorStopped);
            if (_config.Settings.NotifyOnMonitorStartStop && !_config.AlertsPaused(_time.GetUtcNow()))
                _dispatcher.Enqueue(SystemNotifications.MonitorStopping(_config, _time));
        }

        await base.StopAsync(cancellationToken);

        if (_dispatcher is not null)
        {
            await _dispatcher.FlushAsync(TimeSpan.FromSeconds(15));
            WriteStatus();
            await _dispatcher.DisposeAsync();
        }
    }

    private void LoadConfig(bool initial)
    {
        var stamp = ConfigStore.LastWriteTimeUtc();
        if (!initial && stamp == _configStamp) return;
        try
        {
            _config = ConfigStore.Load();
            _configStamp = stamp;
            if (!initial)
            {
                _activity.Add(ActivityLevel.Info,
                    $"Settings reloaded – watching {_config.Services.Count(s => s.Enabled)} service(s) through {_config.Channels.Count(c => c.Enabled)} channel(s)",
                    eventId: EventIds.ConfigReloaded);
            }
        }
        catch (Exception ex)
        {
            _configStamp = stamp; // don't retry a broken file every few seconds
            _activity.Add(ActivityLevel.Error, $"Could not read {AppInfo.ConfigFile}, keeping the previous settings: {ex.Message}");
        }
    }

    private void SendHeartbeatIfDue()
    {
        var settings = _config.Settings;
        if (settings.Heartbeat == HeartbeatFrequency.Off) return;
        var now = _time.GetLocalNow();
        if (now.Hour < settings.HeartbeatHour) return;
        if (settings.Heartbeat == HeartbeatFrequency.Weekly && now.DayOfWeek != settings.HeartbeatDay) return;
        if (_state.LastHeartbeat is { } last && last.ToOffset(now.Offset).Date == now.Date) return;
        if (_config.AlertsPaused(now)) return;

        _state.LastHeartbeat = now;
        _state.Save();
        _dispatcher.Enqueue(SystemNotifications.Heartbeat(_config, _engine.GetStatus(_config), _time));
        _activity.Add(ActivityLevel.Info, "Heartbeat sent");
    }

    /// <summary>
    /// Refreshes OAuth sign-ins once a day. Microsoft refresh tokens expire after 90 days without use and Google's after
    /// 6 months, and a monitor that only sends mail when something breaks could otherwise go quiet without anyone noticing.
    /// </summary>
    private async Task KeepTokensAliveIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now < _nextKeepAliveAttempt) return;
        if (_state.LastTokenKeepAlive is { } last && now - last < TokenKeepAliveInterval) return;

        var channels = _config.Channels.Where(c => c.Enabled && c.UsesOAuth).ToList();
        var allOk = true;
        foreach (var channel in channels)
        {
            try
            {
                await _oauth.GetValidTokenAsync(channel, _config.OAuth, forceRefresh: true, cancellationToken);
                _activity.Add(ActivityLevel.Info, $"{channel.Name}: sign-in refreshed");
            }
            catch (ReauthorizationRequiredException ex)
            {
                allOk = false;
                _activity.Add(ActivityLevel.Error, ex.Message, eventId: EventIds.ChannelNeedsAttention);
                _dispatcher.ReportChannelNeedsAttention(channel, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allOk = false;
                _activity.Add(ActivityLevel.Warning, $"{channel.Name}: could not refresh the sign-in, will retry in an hour: {ex.Message}");
            }
        }

        if (allOk)
        {
            _state.LastTokenKeepAlive = now;
            _state.Save();
        }
        else
        {
            _nextKeepAliveAttempt = now + TimeSpan.FromHours(1);
        }
    }

    private void WriteStatus()
    {
        try
        {
            new MonitorStatus
            {
                StartedAt = _startedAt,
                UpdatedAt = DateTimeOffset.Now,
                PollIntervalSeconds = _config.Settings.PollIntervalSeconds,
                AlertsPausedUntil = _config.AlertsPausedUntil,
                Services = _engine.GetStatus(_config),
                Channels = _dispatcher.ChannelStatus(_config).ToList(),
                Activity = _activity.Snapshot(150).ToList(),
            }.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not write status file: {Message}", ex.Message);
        }
    }
}
