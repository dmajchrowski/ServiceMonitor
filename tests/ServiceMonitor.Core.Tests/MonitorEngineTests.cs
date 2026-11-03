using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Notifications;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Core.Tests;

public class MonitorEngineTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Running_service_sends_nothing()
    {
        var h = new EngineHarness();
        await h.PollAsync();
        await h.AdvanceAndPollAsync(Poll);
        Assert.Empty(h.Sink.Sent);
        Assert.Equal(HealthLevel.Ok, h.Engine.GetStatus(h.Config).Single().Health);
    }

    [Fact]
    public async Task Stop_then_start_sends_alert_and_recovery()
    {
        var h = new EngineHarness();
        await h.PollAsync();

        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);

        var alert = Assert.Single(h.Sink.Sent);
        Assert.Equal(NotificationKind.Alert, alert.Kind);
        Assert.Equal(Severity.Critical, alert.Severity);
        Assert.Equal("Svc Display stopped", alert.Title);
        Assert.Contains(alert.Facts, f => f.Key == "Status" && f.Value == "Stopped (was Running)");
        Assert.Equal(HealthLevel.Problem, h.Engine.GetStatus(h.Config).Single().Health);

        h.Inspector.Set("Svc", ServiceRunState.Running);
        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(3));

        Assert.Equal(2, h.Sink.Sent.Count);
        var recovery = h.Sink.Sent[1];
        Assert.Equal(NotificationKind.Recovery, recovery.Kind);
        Assert.Equal("Svc Display is running again", recovery.Title);
        Assert.Contains("3 min", recovery.Summary);
    }

    [Fact]
    public async Task Crash_events_classify_the_stop_as_a_crash_and_are_attached()
    {
        var h = new EngineHarness();
        await h.PollAsync();
        h.Diagnostics.Events.Add(new EventSummary(h.Time.GetUtcNow(), "System", ScmEvents.Provider, 7034,
            "The Svc Display service terminated unexpectedly."));
        h.Diagnostics.Events.Add(new EventSummary(h.Time.GetUtcNow(), "Application", ".NET Runtime", 1026,
            "Application: Svc.exe\nException Info: System.InvalidOperationException"));

        h.Inspector.Set("Svc", ServiceRunState.Stopped, exitCode: 1067);
        await h.AdvanceAndPollAsync(Poll);

        var alert = Assert.Single(h.Sink.Sent);
        Assert.Equal("Svc Display crashed", alert.Title);
        Assert.Equal(2, alert.Events.Count);
        Assert.Contains(alert.Facts, f => f.Key == "Exit code" && f.Value.StartsWith("1067"));
    }

    [Fact]
    public async Task An_old_crash_does_not_make_a_later_clean_stop_look_like_a_crash()
    {
        var h = new EngineHarness();
        h.Diagnostics.Events.Add(new EventSummary(h.Time.GetUtcNow(), "System", ScmEvents.Provider, 7034, "terminated unexpectedly"));
        await h.PollAsync();
        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(3)); // still running; the crash event is now old

        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);

        var alert = Assert.Single(h.Sink.Sent);
        Assert.Equal("Svc Display stopped", alert.Title);
        Assert.Empty(alert.Events);
    }

    [Fact]
    public async Task Start_timeout_events_classify_as_failed_to_start()
    {
        var h = new EngineHarness();
        h.Diagnostics.Events.Add(new EventSummary(h.Time.GetUtcNow(), "System", ScmEvents.Provider, 7009, "A timeout was reached"));
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.PollAsync();
        Assert.Equal("Svc Display failed to start", Assert.Single(h.Sink.Sent).Title);
    }

    [Fact]
    public async Task Service_already_stopped_when_monitoring_begins_is_reported_as_not_running()
    {
        var h = new EngineHarness();
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.PollAsync();
        Assert.Equal("Svc Display is not running", Assert.Single(h.Sink.Sent).Title);
    }

    [Fact]
    public async Task Startup_grace_period_suppresses_services_that_have_not_started_yet()
    {
        var h = new EngineHarness(uptime: TimeSpan.FromSeconds(30)); // grace = 180 s
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.PollAsync();
        await h.AdvanceAndPollAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(h.Sink.Sent);
        Assert.Equal(HealthLevel.Warning, h.Engine.GetStatus(h.Config).Single().Health);

        await h.AdvanceAndPollAsync(TimeSpan.FromSeconds(100)); // now past the grace period
        Assert.Equal("Svc Display is not running", Assert.Single(h.Sink.Sent).Title);
    }

    [Fact]
    public async Task Startup_grace_does_not_hide_a_crash_of_a_service_that_was_running()
    {
        var h = new EngineHarness(uptime: TimeSpan.FromSeconds(30));
        await h.PollAsync(); // seen running
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Single(h.Sink.Sent);
    }

    [Fact]
    public async Task Stuck_in_start_pending_is_reported_as_hung_after_the_threshold()
    {
        var h = new EngineHarness();
        h.Config.Settings.HangThresholdSeconds = 120;
        h.Inspector.Set("Svc", ServiceRunState.StartPending);
        await h.PollAsync();
        await h.AdvanceAndPollAsync(TimeSpan.FromSeconds(100));
        Assert.Empty(h.Sink.Sent);
        Assert.Equal(HealthLevel.Pending, h.Engine.GetStatus(h.Config).Single().Health);

        await h.AdvanceAndPollAsync(TimeSpan.FromSeconds(30));
        var alert = Assert.Single(h.Sink.Sent);
        Assert.Equal("Svc Display is hung (stuck \"Starting\")", alert.Title);

        // The start finally times out and the service stops: escalate with a new alert.
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Equal(2, h.Sink.Sent.Count);
        Assert.Equal(NotificationKind.Alert, h.Sink.Sent[1].Kind);
    }

    [Fact]
    public async Task Per_service_hang_threshold_overrides_the_default()
    {
        var h = new EngineHarness();
        h.Service().HangThresholdSeconds = 20;
        h.Inspector.Set("Svc", ServiceRunState.StopPending);
        await h.PollAsync();
        await h.AdvanceAndPollAsync(TimeSpan.FromSeconds(25));
        Assert.Contains("is hung", Assert.Single(h.Sink.Sent).Title);
    }

    [Fact]
    public async Task Reminders_repeat_while_the_service_stays_down()
    {
        var h = new EngineHarness();
        h.Config.Settings.ReminderIntervalMinutes = 30;
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.PollAsync();
        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(29));
        Assert.Single(h.Sink.Sent);
        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(2, h.Sink.Sent.Count);
        Assert.Equal(NotificationKind.Reminder, h.Sink.Sent[1].Kind);
        Assert.Equal("STILL DOWN", h.Sink.Sent[1].Tag);
    }

    [Fact]
    public async Task Paused_alerts_are_tracked_silently_and_sent_when_the_pause_ends()
    {
        var h = new EngineHarness();
        h.Config.AlertsPausedUntil = h.Time.GetUtcNow().AddMinutes(30);
        await h.PollAsync();
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Empty(h.Sink.Sent);

        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(31));
        Assert.Equal(NotificationKind.Alert, Assert.Single(h.Sink.Sent).Kind);
    }

    [Fact]
    public async Task Problem_fixed_during_a_pause_sends_no_recovery()
    {
        var h = new EngineHarness();
        h.Config.AlertsPausedUntil = h.Time.GetUtcNow().AddMinutes(30);
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.PollAsync();
        h.Inspector.Set("Svc", ServiceRunState.Running);
        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(5));
        await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(40));
        Assert.Empty(h.Sink.Sent);
    }

    [Fact]
    public async Task Recovery_notice_can_be_turned_off()
    {
        var h = new EngineHarness();
        h.Config.Settings.NotifyOnRecovery = false;
        await h.PollAsync();
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);
        h.Inspector.Set("Svc", ServiceRunState.Running);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Single(h.Sink.Sent);
    }

    [Fact]
    public async Task Auto_restart_starts_the_service_after_the_delay_and_reports_who_fixed_it()
    {
        var h = new EngineHarness();
        h.Service().AutoRestart = true;
        h.Config.Settings.RestartDelaySeconds = 30;
        await h.PollAsync();

        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Contains("Auto-restart is on", h.Sink.Sent[0].Summary);
        Assert.Empty(h.Inspector.StartCalls);

        await h.AdvanceAndPollAsync(TimeSpan.FromSeconds(31)); // restart attempt
        Assert.Equal(["Svc"], h.Inspector.StartCalls);

        await h.AdvanceAndPollAsync(Poll); // sees it running
        Assert.Equal(2, h.Sink.Sent.Count);
        var recovery = h.Sink.Sent.Last();
        Assert.Equal(NotificationKind.Recovery, recovery.Kind);
        Assert.Contains("restarted automatically", recovery.Summary);
    }

    [Fact]
    public async Task Auto_restart_gives_up_after_max_attempts_and_says_so()
    {
        var h = new EngineHarness();
        h.Service().AutoRestart = true;
        h.Config.Settings.RestartDelaySeconds = 5;
        h.Config.Settings.MaxRestartAttempts = 2;
        h.Config.Settings.ReminderIntervalMinutes = 0;
        h.Inspector.OnStart = _ => new ServiceActionResult(false, "The service did not start (1053)");
        await h.PollAsync();
        h.Inspector.Set("Svc", ServiceRunState.Stopped);

        for (var i = 0; i < 20; i++) await h.AdvanceAndPollAsync(Poll);

        Assert.Equal(2, h.Inspector.StartCalls.Count);
        var gaveUp = h.Sink.Sent.Last();
        Assert.Equal("Svc Display: automatic restart failed", gaveUp.Title);
        Assert.Contains("1053", gaveUp.Summary);
        Assert.Equal(2, h.Sink.Sent.Count); // alert + gave up
    }

    [Fact]
    public async Task Auto_restart_is_not_attempted_when_disabled()
    {
        var h = new EngineHarness();
        await h.PollAsync();
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        for (var i = 0; i < 10; i++) await h.AdvanceAndPollAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(h.Inspector.StartCalls);
    }

    [Fact]
    public async Task Port_check_detects_a_running_but_unresponsive_service()
    {
        var h = new EngineHarness();
        h.Service().PortCheck = new PortCheck { Port = 7980, FailuresBeforeAlert = 3 };
        await h.PollAsync();

        h.Probe.Up = false;
        await h.AdvanceAndPollAsync(Poll);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Empty(h.Sink.Sent);
        await h.AdvanceAndPollAsync(Poll);
        var alert = Assert.Single(h.Sink.Sent);
        Assert.Equal("Svc Display is not responding on port 7980", alert.Title);

        h.Probe.Up = true;
        await h.AdvanceAndPollAsync(Poll);
        Assert.Equal(NotificationKind.Recovery, h.Sink.Sent[1].Kind);
        Assert.Contains("accepting connections again", h.Sink.Sent[1].Summary);
    }

    [Fact]
    public async Task Missing_service_is_reported()
    {
        var h = new EngineHarness();
        h.Inspector.Services.Clear();
        await h.PollAsync();
        // Falls back to the display name remembered in the configuration.
        Assert.Equal("Svc Display is no longer installed", Assert.Single(h.Sink.Sent).Title);
    }

    [Fact]
    public async Task Crash_that_Windows_restarted_before_we_looked_is_reported_as_a_warning()
    {
        var h = new EngineHarness();
        await h.PollAsync();
        h.Engine.ReportScmEvent(new ScmEvent(7031, "Svc Display", h.Time.GetUtcNow(), "The Svc Display service terminated unexpectedly."));
        await h.AdvanceAndPollAsync(Poll);

        var warning = Assert.Single(h.Sink.Sent);
        Assert.Equal(NotificationKind.Warning, warning.Kind);
        Assert.Equal("Svc Display crashed and was restarted by Windows", warning.Title);
        Assert.Single(warning.Events); // falls back to the SCM event itself
    }

    [Fact]
    public async Task Crash_event_for_a_service_we_already_saw_down_is_not_reported_twice()
    {
        var h = new EngineHarness();
        await h.PollAsync();
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        h.Engine.ReportScmEvent(new ScmEvent(7034, "Svc Display", h.Time.GetUtcNow(), "terminated unexpectedly"));
        await h.AdvanceAndPollAsync(Poll);
        h.Inspector.Set("Svc", ServiceRunState.Running);
        await h.AdvanceAndPollAsync(Poll);
        await h.AdvanceAndPollAsync(Poll);
        Assert.Equal([NotificationKind.Alert, NotificationKind.Recovery], h.Sink.Sent.Select(n => n.Kind));
    }

    [Fact]
    public async Task Crash_events_for_unmonitored_services_are_ignored()
    {
        var h = new EngineHarness();
        await h.PollAsync();
        h.Engine.ReportScmEvent(new ScmEvent(7034, "Some Other Service", h.Time.GetUtcNow(), "terminated unexpectedly"));
        await h.AdvanceAndPollAsync(Poll);
        Assert.Empty(h.Sink.Sent);
    }

    [Fact]
    public async Task Flapping_service_is_muted_after_the_hourly_limit()
    {
        var h = new EngineHarness();
        h.Config.Settings.MaxNotificationsPerServicePerHour = 4;
        await h.PollAsync();
        for (var i = 0; i < 6; i++)
        {
            h.Inspector.Set("Svc", ServiceRunState.Stopped);
            await h.AdvanceAndPollAsync(Poll);
            h.Inspector.Set("Svc", ServiceRunState.Running);
            await h.AdvanceAndPollAsync(Poll);
        }

        Assert.Equal(5, h.Sink.Sent.Count); // 4 regular + 1 "muted" notice
        Assert.Contains("notifications muted", h.Sink.Sent.Last().Title);

        await h.AdvanceAndPollAsync(TimeSpan.FromHours(1));
        Assert.Contains("notifications resumed", h.Sink.Sent.Last().Title);
    }

    [Fact]
    public async Task Disabled_and_removed_services_are_not_checked()
    {
        var h = new EngineHarness(null, "A", "B");
        h.Service("B").Enabled = false;
        h.Inspector.Set("A", ServiceRunState.Stopped);
        h.Inspector.Set("B", ServiceRunState.Stopped);
        await h.PollAsync();
        Assert.Equal("A Display is not running", Assert.Single(h.Sink.Sent).Title);

        h.Config.Services.RemoveAll(s => s.ServiceName == "A");
        await h.AdvanceAndPollAsync(TimeSpan.FromHours(2));
        Assert.Single(h.Sink.Sent);
        Assert.Equal(HealthLevel.Disabled, h.Engine.GetStatus(h.Config).Single().Health);
    }

    [Fact]
    public async Task Site_name_appears_in_the_subject()
    {
        var h = new EngineHarness();
        h.Config.SiteName = "Contoso";
        h.Inspector.Set("Svc", ServiceRunState.Stopped);
        await h.PollAsync();
        Assert.Equal($"[ALERT] Contoso ({AppInfo.MachineName}): Svc Display is not running", h.Sink.Sent[0].Subject);
    }
}
