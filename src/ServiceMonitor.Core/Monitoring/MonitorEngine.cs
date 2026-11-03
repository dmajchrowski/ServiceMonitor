using System.Collections.Concurrent;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Notifications;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Core.Monitoring;

public interface INotificationSink
{
    void Enqueue(Notification notification);
}

/// <summary>
/// Decides when a monitored service has a problem and what to tell people about it.
/// It is driven by <see cref="PollAsync"/> (every few seconds, and immediately when the Service Control Manager
/// logs an event) and keeps per-service state: when the problem started, whether people were told,
/// reminders, auto-restart attempts, recovery and flood protection.
/// </summary>
public sealed class MonitorEngine(
    IServiceInspector inspector,
    IEventDiagnostics diagnostics,
    IPortProbe probe,
    INotificationSink sink,
    ActivityLog activity,
    TimeProvider time,
    DateTimeOffset bootTime)
{
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingCrashMaxAge = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, WatchState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ScmEvent> _incomingEvents = new();
    private readonly List<ScmEvent> _pendingCrashes = [];

    /// <summary>Called from the event log watcher thread.</summary>
    public void ReportScmEvent(ScmEvent scmEvent)
    {
        if (ScmEvents.Crash.Contains(scmEvent.EventId)) _incomingEvents.Enqueue(scmEvent);
    }

    public async Task PollAsync(MonitorConfig config, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var watched = config.Services.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.ServiceName)).ToList();

        foreach (var removed in _states.Keys.Except(watched.Select(s => s.ServiceName), StringComparer.OrdinalIgnoreCase).ToList())
            _states.Remove(removed);

        var observations = new List<(MonitoredService Service, WatchState State, ServiceSnapshot Snapshot)>();
        foreach (var service in watched)
        {
            var state = GetState(service.ServiceName);
            try
            {
                observations.Add((service, state, inspector.Query(service.ServiceName)));
                state.QueryError = null;
            }
            catch (Exception ex)
            {
                if (state.QueryError != ex.Message)
                    activity.Add(ActivityLevel.Warning, $"Cannot read the service state: {ex.Message}", service.ServiceName);
                state.QueryError = ex.Message;
            }
        }

        await Task.WhenAll(observations
            .Where(o => o.Snapshot.State == ServiceRunState.Running && o.Service.PortCheck is { Enabled: true, Port: > 0 })
            .Select(o => ProbePortAsync(o.Service.PortCheck!, o.State, cancellationToken)));

        foreach (var (service, state, snapshot) in observations)
            Evaluate(config, service, state, snapshot, now);

        ProcessCrashEvents(config, now);
    }

    public List<ServiceStatusInfo> GetStatus(MonitorConfig config) =>
        config.Services.Select(service =>
        {
            if (!service.Enabled)
            {
                return new ServiceStatusInfo
                {
                    ServiceName = service.ServiceName, DisplayName = Name(service, null), State = "Not monitored",
                    Health = HealthLevel.Disabled, Detail = "Monitoring is turned off for this service",
                };
            }

            if (!_states.TryGetValue(service.ServiceName, out var state) || state.LastSnapshot is null)
            {
                return new ServiceStatusInfo
                {
                    ServiceName = service.ServiceName, DisplayName = Name(service, null), State = "Unknown",
                    Health = HealthLevel.Unknown, Detail = state?.QueryError ?? "Waiting for the first check",
                };
            }

            var snap = state.LastSnapshot;
            var health = state.Problem is { } p
                ? p.Kind == ProblemKind.Paused ? HealthLevel.Warning : HealthLevel.Problem
                : snap.State == ServiceRunState.Running ? HealthLevel.Ok
                : snap.IsPending ? HealthLevel.Pending
                : HealthLevel.Warning; // not running, but still inside the startup grace period
            return new ServiceStatusInfo
            {
                ServiceName = service.ServiceName,
                DisplayName = Name(service, snap),
                State = ServiceSnapshot.Describe(snap.State),
                Health = health,
                Detail = state.Detail,
                Since = state.Problem?.Since ?? state.StateSince,
            };
        }).ToList();

    private WatchState GetState(string serviceName)
    {
        if (!_states.TryGetValue(serviceName, out var state)) _states[serviceName] = state = new WatchState();
        return state;
    }

    private async Task ProbePortAsync(PortCheck check, WatchState state, CancellationToken cancellationToken)
    {
        var result = await probe.ProbeAsync(check.Host, check.Port, TimeSpan.FromSeconds(Math.Clamp(check.TimeoutSeconds, 1, 60)), cancellationToken);
        if (result.Success)
        {
            state.PortFailures = 0;
            state.PortError = null;
        }
        else
        {
            state.PortFailures++;
            state.PortError = result.Error;
        }
    }

    private void Evaluate(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap, DateTimeOffset now)
    {
        var settings = config.Settings;
        var paused = config.AlertsPaused(now);
        var name = Name(service, snap);

        if (state.LastState != snap.State)
        {
            var firstSight = state.LastState is null;
            state.LastState = snap.State;
            state.StateSince = now;
            if (firstSight && snap is { State: ServiceRunState.Running, ProcessId: > 0 }
                && inspector.GetProcessStartTime(snap.ProcessId) is { } started && started < now)
            {
                state.StateSince = started.ToUniversalTime();
            }
        }
        if (snap.State == ServiceRunState.Running)
        {
            state.EverSeenRunning = true;
            state.LastSeenRunning = now;
            state.LastProcessId = snap.ProcessId;
        }
        else
        {
            state.PortFailures = 0;
        }
        state.LastSnapshot = snap;
        PruneSendHistory(config, service, state, snap, now);

        var hangThreshold = TimeSpan.FromSeconds(Math.Max(10, service.HangThresholdSeconds ?? settings.HangThresholdSeconds));
        ProblemKind? kind;
        switch (snap.State)
        {
            case ServiceRunState.NotFound:
                kind = ProblemKind.Missing;
                break;
            case ServiceRunState.Stopped:
                kind = ProblemKind.Stopped;
                break;
            case ServiceRunState.Paused:
                kind = ProblemKind.Paused;
                break;
            case ServiceRunState.Running:
                kind = service.PortCheck is { Enabled: true, Port: > 0 } pc && state.PortFailures >= Math.Max(1, pc.FailuresBeforeAlert)
                    ? ProblemKind.NotResponding
                    : null;
                break;
            default: // Starting / Stopping / Pausing / Resuming
                if (now - state.StateSince < hangThreshold)
                {
                    if (state.Problem is null) state.Detail = $"{ServiceSnapshot.Describe(snap.State)} for {TimeText.Duration(now - state.StateSince)}";
                    return; // give it time
                }
                kind = ProblemKind.Hung;
                break;
        }

        if (kind is null)
        {
            if (state.Problem is not null) Recover(config, service, state, snap, now, paused);
            var portText = service.PortCheck is { Enabled: true, Port: > 0 } check
                ? state.PortFailures == 0 ? $" · port {check.Port} answering" : $" · port {check.Port} not answering ({state.PortFailures}×)"
                : "";
            state.Detail = $"Running since {TimeText.Local(state.StateSince, time)}{(snap.ProcessId > 0 ? $" · PID {snap.ProcessId}" : "")}{portText}";
            return;
        }

        if (state.Problem is null)
        {
            var inStartupGrace = now < bootTime + TimeSpan.FromSeconds(settings.StartupGraceSeconds);
            if (inStartupGrace && !state.EverSeenRunning && kind is ProblemKind.Stopped or ProblemKind.Paused or ProblemKind.Missing)
            {
                state.Detail = "Not running yet – Windows is still starting up, alerts begin after the startup grace period";
                return;
            }
            OpenProblem(config, service, state, snap, kind.Value, now, paused, name);
        }
        else
        {
            ContinueProblem(config, service, state, snap, kind.Value, now, paused, name);
        }

        if (state.Problem is { } problem) state.Detail = DescribeProblem(service, problem, now, settings);
    }

    private void OpenProblem(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap,
        ProblemKind kind, DateTimeOffset now, bool paused, string name)
    {
        var events = CollectEvents(config, snap, state, now);
        var problem = new ActiveProblem
        {
            Kind = kind,
            Since = kind == ProblemKind.NotResponding ? now : state.StateSince,
            DetectedAt = now,
            Headline = Classify(kind, snap, events, state, service),
            Events = events,
        };
        if (service.AutoRestart && kind == ProblemKind.Stopped)
            problem.NextRestartAt = now + TimeSpan.FromSeconds(Math.Max(0, config.Settings.RestartDelaySeconds));
        state.Problem = problem;

        activity.Add(kind == ProblemKind.Paused ? ActivityLevel.Warning : ActivityLevel.Error,
            $"{name} {problem.Headline}{(paused ? " (alerts are paused – nobody was notified)" : "")}",
            service.ServiceName, EventIds.ServiceProblem);

        if (!paused) SendProblem(config, service, state, snap, NotificationKind.Alert, now);
    }

    private void ContinueProblem(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap,
        ProblemKind kind, DateTimeOffset now, bool paused, string name)
    {
        var problem = state.Problem!;
        if (kind != problem.Kind)
        {
            var escalated = Rank(kind) > Rank(problem.Kind);
            problem.Kind = kind;
            if (escalated)
            {
                // e.g. hung → stopped, or not responding → crashed: tell people again.
                problem.Events = CollectEvents(config, snap, state, now);
                problem.Headline = Classify(kind, snap, problem.Events, state, service);
                problem.Notified = false;
                if (service.AutoRestart && kind == ProblemKind.Stopped && problem.NextRestartAt == default)
                    problem.NextRestartAt = now + TimeSpan.FromSeconds(Math.Max(0, config.Settings.RestartDelaySeconds));
                activity.Add(ActivityLevel.Error, $"{name} {problem.Headline}", service.ServiceName, EventIds.ServiceProblem);
            }
            else
            {
                problem.Headline = Classify(kind, snap, problem.Events, state, service);
            }
        }

        if (!paused)
        {
            if (!problem.Notified)
            {
                SendProblem(config, service, state, snap, NotificationKind.Alert, now);
            }
            else if (config.Settings.ReminderIntervalMinutes > 0
                     && now - problem.LastNotifiedAt >= TimeSpan.FromMinutes(config.Settings.ReminderIntervalMinutes))
            {
                SendProblem(config, service, state, snap, NotificationKind.Reminder, now);
            }
        }

        TryAutoRestart(config, service, state, snap, now, paused, name);
    }

    private void TryAutoRestart(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap,
        DateTimeOffset now, bool paused, string name)
    {
        var problem = state.Problem!;
        if (!service.AutoRestart || problem.Kind != ProblemKind.Stopped) return;

        if (state.RestartTask is { } task)
        {
            if (!task.IsCompleted) return;
            state.RestartTask = null;
            var result = task.IsCompletedSuccessfully ? task.Result : new ServiceActionResult(false, task.Exception?.GetBaseException().Message ?? "Cancelled");
            if (result.Success)
            {
                problem.RestartedByMonitor = true;
                activity.Add(ActivityLevel.Success, $"Restart attempt {problem.RestartAttempts} succeeded", service.ServiceName, EventIds.RestartAttempt);
                return; // the next check will see it running and report the recovery
            }
            problem.LastRestartError = result.Message;
            activity.Add(ActivityLevel.Warning, $"Restart attempt {problem.RestartAttempts} failed: {result.Message}", service.ServiceName, EventIds.RestartAttempt);
        }

        var maxAttempts = Math.Max(0, config.Settings.MaxRestartAttempts);
        if (problem.RestartAttempts >= maxAttempts)
        {
            if (!problem.GaveUpNotified && maxAttempts > 0 && !paused)
            {
                problem.GaveUpNotified = true;
                var n = NewNotification(config, service, snap, NotificationKind.Alert, Severity.Critical,
                    $"{name}: automatic restart failed",
                    $"The monitor tried to start the service {maxAttempts} time(s) and it is still not running. Someone needs to look at this server."
                    + (problem.LastRestartError is { } err ? $" Last error: {err}" : ""));
                AddServiceFacts(n, service, snap, state, problem, config.Settings, now);
                n.Events.AddRange(CollectEvents(config, snap, state, now));
                Send(config, service, state, snap, n, now);
            }
            return;
        }

        if (now < problem.NextRestartAt) return;
        problem.RestartAttempts++;
        problem.NextRestartAt = now + TimeSpan.FromSeconds(Math.Max(5, config.Settings.RestartDelaySeconds) * (problem.RestartAttempts + 1));
        activity.Add(ActivityLevel.Info, $"Trying to start {name} (attempt {problem.RestartAttempts} of {maxAttempts})", service.ServiceName, EventIds.RestartAttempt);
        state.RestartTask = inspector.StartAsync(service.ServiceName, RestartTimeout, CancellationToken.None);
    }

    private void Recover(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap, DateTimeOffset now, bool paused)
    {
        var problem = state.Problem!;
        state.Problem = null;
        if (state.RestartTask is { IsCompletedSuccessfully: true, Result.Success: true })
        {
            problem.RestartedByMonitor = true; // our start request is what brought it back
            activity.Add(ActivityLevel.Success, $"Restart attempt {problem.RestartAttempts} succeeded", service.ServiceName, EventIds.RestartAttempt);
        }
        state.RestartTask = null;
        var name = Name(service, snap);
        var downFor = now - problem.Since;
        var how = problem.Kind == ProblemKind.NotResponding
            ? "It is accepting connections again."
            : problem.RestartedByMonitor
                ? $"It was restarted automatically by {AppInfo.ProductName} (attempt {problem.RestartAttempts})."
                : "It was started by Windows service recovery or by a technician.";

        activity.Add(ActivityLevel.Success, $"{name} is running again after {TimeText.Duration(downFor)}", service.ServiceName, EventIds.ServiceRecovered);

        if (!problem.Notified || paused || !config.Settings.NotifyOnRecovery) return;
        var n = NewNotification(config, service, snap, NotificationKind.Recovery, Severity.Good,
            $"{name} is running again", $"The service is back up after {TimeText.Duration(downFor)}. {how}");
        n.AddFact("Service", $"{name} ({service.ServiceName})")
            .AddFact("Status", ServiceSnapshot.Describe(snap.State))
            .AddFact("Problem", $"{Capitalize(problem.Headline)} at {TimeText.Local(problem.Since, time)}")
            .AddFact("Recovered", TimeText.Local(now, time))
            .AddFact("Downtime", TimeText.Duration(downFor))
            .AddFact("Process ID", snap.ProcessId > 0 ? snap.ProcessId.ToString() : null);
        Send(config, service, state, snap, n, now);
    }

    private void SendProblem(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap,
        NotificationKind kind, DateTimeOffset now)
    {
        var problem = state.Problem!;
        var name = Name(service, snap);
        var severity = problem.Kind == ProblemKind.Paused ? Severity.Warning : Severity.Critical;
        var summary = kind == NotificationKind.Reminder
            ? $"Still not resolved after {TimeText.Duration(now - problem.Since)}. {ProblemExplanation(problem, service, state, snap)}"
            : ProblemExplanation(problem, service, state, snap);
        if (service.AutoRestart && problem.Kind == ProblemKind.Stopped && kind == NotificationKind.Alert && problem.RestartAttempts == 0)
            summary += $" Auto-restart is on: the monitor will try to start it in {config.Settings.RestartDelaySeconds} s (up to {config.Settings.MaxRestartAttempts} attempts).";

        var n = NewNotification(config, service, snap, kind, severity, $"{name} {problem.Headline}", summary);
        AddServiceFacts(n, service, snap, state, problem, config.Settings, now);
        if (kind == NotificationKind.Alert) n.Events.AddRange(problem.Events);

        if (Send(config, service, state, snap, n, now))
        {
            problem.Notified = true;
            problem.LastNotifiedAt = now;
        }
    }

    /// <summary>Queues a notification unless this service has been too noisy in the last hour.</summary>
    private bool Send(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap, Notification n, DateTimeOffset now)
    {
        var limit = config.Settings.MaxNotificationsPerServicePerHour;
        if (limit > 0 && state.SentTimes.Count >= limit)
        {
            if (!state.MutedNoticeSent)
            {
                state.MutedNoticeSent = true;
                var until = state.SentTimes.Peek() + TimeSpan.FromHours(1);
                var muted = NewNotification(config, service, snap, NotificationKind.Warning, Severity.Warning,
                    $"{Name(service, snap)} keeps changing state – notifications muted",
                    $"{limit} notifications were sent for this service in the last hour. Further notifications are muted until about {TimeText.LocalTime(until, time)} to avoid flooding your inbox. The service is currently {ServiceSnapshot.Describe(snap.State).ToLowerInvariant()}.");
                sink.Enqueue(muted);
                activity.Add(ActivityLevel.Warning, $"Notifications muted until {TimeText.LocalTime(until, time)} (flood protection)", service.ServiceName, EventIds.ServiceWarning);
            }
            return false;
        }

        state.SentTimes.Enqueue(now);
        sink.Enqueue(n);
        return true;
    }

    private void PruneSendHistory(MonitorConfig config, MonitoredService service, WatchState state, ServiceSnapshot snap, DateTimeOffset now)
    {
        while (state.SentTimes.Count > 0 && now - state.SentTimes.Peek() >= TimeSpan.FromHours(1)) state.SentTimes.Dequeue();
        if (!state.MutedNoticeSent || state.SentTimes.Count >= config.Settings.MaxNotificationsPerServicePerHour) return;

        state.MutedNoticeSent = false;
        if (config.AlertsPaused(now)) return;
        var n = NewNotification(config, service, snap, NotificationKind.Info, snap.State == ServiceRunState.Running ? Severity.Good : Severity.Warning,
            $"{Name(service, snap)} notifications resumed",
            $"Notifications for this service are no longer muted. It is currently {ServiceSnapshot.Describe(snap.State).ToLowerInvariant()}.");
        state.SentTimes.Enqueue(now);
        sink.Enqueue(n);
    }

    /// <summary>Crash events for services that are running again by the time we look: Windows recovery restarted them.</summary>
    private void ProcessCrashEvents(MonitorConfig config, DateTimeOffset now)
    {
        while (_incomingEvents.TryDequeue(out var e)) _pendingCrashes.Add(e);

        foreach (var e in _pendingCrashes.ToList())
        {
            var service = config.Services.FirstOrDefault(s => s.Enabled && _states.TryGetValue(s.ServiceName, out var st) && st.LastSnapshot is { } snap
                && (snap.DisplayName.Equals(e.DisplayName, StringComparison.OrdinalIgnoreCase) || s.ServiceName.Equals(e.DisplayName, StringComparison.OrdinalIgnoreCase)));
            if (service is null)
            {
                _pendingCrashes.Remove(e);
                continue;
            }

            var state = _states[service.ServiceName];
            if (state.Problem is not null)
            {
                _pendingCrashes.Remove(e); // already reported as a stop/crash
                continue;
            }
            if (state.LastState != ServiceRunState.Running)
            {
                if (now - e.Time.ToUniversalTime() > PendingCrashMaxAge) _pendingCrashes.Remove(e);
                continue; // still restarting; decide on a later check
            }

            _pendingCrashes.Remove(e);
            var snap = state.LastSnapshot!;
            var name = Name(service, snap);
            activity.Add(ActivityLevel.Warning, $"{name} crashed and Windows restarted it automatically", service.ServiceName, EventIds.ServiceWarning);
            if (!config.Settings.NotifyOnSelfHealedCrash || config.AlertsPaused(now)) continue;

            var n = NewNotification(config, service, snap, NotificationKind.Warning, Severity.Warning,
                $"{name} crashed and was restarted by Windows",
                "The service terminated unexpectedly and Windows' service recovery restarted it before the monitor saw it down. It is running again, but the crash may point to a problem that will come back.");
            n.AddFact("Service", $"{name} ({service.ServiceName})")
                .AddFact("Crash time", TimeText.Local(e.Time.ToUniversalTime(), time))
                .AddFact("Status", ServiceSnapshot.Describe(snap.State))
                .AddFact("Process ID", snap.ProcessId > 0 ? snap.ProcessId.ToString() : null);
            n.Events.AddRange(CollectEvents(config, snap, state, now));
            if (n.Events.Count == 0) n.Events.Add(new EventSummary(e.Time, "System", ScmEvents.Provider, e.EventId, e.Message));
            Send(config, service, state, snap, n, now);
        }
    }

    /// <summary>
    /// Event log entries that explain the current problem. Only events since the service was last seen running count,
    /// so an old crash doesn't make a later clean stop look like another crash.
    /// </summary>
    private List<EventSummary> CollectEvents(MonitorConfig config, ServiceSnapshot snap, WatchState state, DateTimeOffset now)
    {
        if (!config.Settings.IncludeEventLogDetails || !snap.Exists) return [];
        var since = state.LastSeenRunning is { } seen
            ? seen - TimeSpan.FromSeconds(5)
            : now - TimeSpan.FromMinutes(15); // first look at a service that is already down
        if (now - since > TimeSpan.FromHours(24)) since = now - TimeSpan.FromHours(24);
        try
        {
            return diagnostics.GetRecentEvents(snap, now - since).Where(e => e.Time.ToUniversalTime() >= since).ToList();
        }
        catch (Exception ex)
        {
            activity.Add(ActivityLevel.Warning, $"Could not read the event log: {ex.Message}", snap.ServiceName);
            return [];
        }
    }

    private static string Classify(ProblemKind kind, ServiceSnapshot snap, IReadOnlyList<EventSummary> events, WatchState state, MonitoredService service) => kind switch
    {
        ProblemKind.Missing => "is no longer installed",
        ProblemKind.Paused => "is paused",
        ProblemKind.Hung => $"is hung (stuck \"{ServiceSnapshot.Describe(snap.State)}\")",
        ProblemKind.NotResponding => $"is not responding on port {service.PortCheck?.Port}",
        _ when events.Any(e => e.IsCrash) => "crashed",
        _ when events.Any(e => e.IsStartFailure) => "failed to start",
        _ when snap.ExitCodeDescription is not null || events.Any(e => e.IsErrorExit) => "stopped with an error",
        _ when !state.EverSeenRunning => "is not running",
        _ => "stopped",
    };

    private string ProblemExplanation(ActiveProblem problem, MonitoredService service, WatchState state, ServiceSnapshot snap) => problem.Headline switch
    {
        "crashed" => "The service terminated unexpectedly. Event log details are included below.",
        "failed to start" => "Windows could not start the service (it timed out or reported an error).",
        "stopped with an error" => "The service stopped and reported an error.",
        "stopped" => "The service stopped. If nobody stopped it on purpose, it needs attention.",
        "is not running" => "The service is not running.",
        "is no longer installed" => "The service no longer exists on this computer. It may have been uninstalled or renamed.",
        "is paused" => "The service is paused and is not doing its work.",
        _ when problem.Kind == ProblemKind.Hung =>
            $"The service has been stuck in the \"{ServiceSnapshot.Describe(snap.State)}\" state for {TimeText.Duration(time.GetUtcNow() - state.StateSince)}. It usually has to be ended in Task Manager and started again.",
        _ when problem.Kind == ProblemKind.NotResponding =>
            $"Windows reports the service as running, but it has not accepted connections on {service.PortCheck?.Host}:{service.PortCheck?.Port} for {state.PortFailures} checks in a row. It is probably hung. ({state.PortError})",
        _ => "",
    };

    private string DescribeProblem(MonitoredService service, ActiveProblem problem, DateTimeOffset now, MonitorSettings settings)
    {
        var text = $"{Capitalize(problem.Headline)} {TimeText.Ago(now - problem.Since)}";
        if (service.AutoRestart && problem.Kind == ProblemKind.Stopped)
        {
            if (problem.RestartAttempts == 0) text += " · restart scheduled";
            else if (problem.RestartAttempts >= settings.MaxRestartAttempts && problem.LastRestartError is not null) text += $" · gave up after {problem.RestartAttempts} restart attempts";
            else text += $" · restart attempt {problem.RestartAttempts}/{settings.MaxRestartAttempts}";
            if (problem.LastRestartError is { } err) text += $": {err}";
        }
        return text;
    }

    private void AddServiceFacts(Notification n, MonitoredService service, ServiceSnapshot snap, WatchState state, ActiveProblem problem, MonitorSettings settings, DateTimeOffset now)
    {
        n.AddFact("Service", $"{Name(service, snap)} ({service.ServiceName})")
            .AddFact("Status", ServiceSnapshot.Describe(snap.State) + (state.EverSeenRunning && snap.State != ServiceRunState.Running ? " (was Running)" : ""))
            .AddFact("Since", $"{TimeText.Local(problem.Since, time)} ({TimeText.Ago(now - problem.Since)})")
            .AddFact("Exit code", snap.ExitCodeDescription)
            .AddFact("Start type", snap.StartType)
            .AddFact("Last process ID", state.LastProcessId > 0 ? state.LastProcessId.ToString() : null)
            .AddFact("Executable", snap.ExecutablePath);
        if (problem.Kind == ProblemKind.NotResponding)
            n.AddFact("Port check", $"{service.PortCheck?.Host}:{service.PortCheck?.Port} – {state.PortError}");
        if (problem.Kind == ProblemKind.Stopped)
        {
            n.AddFact("Auto-restart", !service.AutoRestart ? "Off"
                : problem.RestartAttempts == 0 ? "On – waiting to try"
                : $"{problem.RestartAttempts} of {settings.MaxRestartAttempts} attempts made" + (problem.LastRestartError is { } e ? $" – last error: {e}" : ""));
        }
    }

    private Notification NewNotification(MonitorConfig config, MonitoredService service, ServiceSnapshot snap,
        NotificationKind kind, Severity severity, string title, string summary) => new()
    {
        Kind = kind,
        Severity = severity,
        Title = title,
        Summary = summary,
        ServiceName = service.ServiceName,
        ServiceDisplayName = Name(service, snap),
        SiteName = config.SiteName,
        Time = time.GetLocalNow(),
    };

    private static int Rank(ProblemKind kind) => kind is ProblemKind.Stopped or ProblemKind.Missing ? 2 : 1;

    private static string Name(MonitoredService service, ServiceSnapshot? snap) =>
        snap is { Exists: true } && !string.IsNullOrWhiteSpace(snap.DisplayName) ? snap.DisplayName
        : !string.IsNullOrWhiteSpace(service.DisplayName) ? service.DisplayName : service.ServiceName;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private enum ProblemKind { Stopped, Paused, Hung, NotResponding, Missing }

    private sealed class ActiveProblem
    {
        public ProblemKind Kind;
        public string Headline = "";
        public DateTimeOffset Since;
        public DateTimeOffset DetectedAt;
        public List<EventSummary> Events = [];
        public bool Notified;
        public DateTimeOffset LastNotifiedAt;
        public int RestartAttempts;
        public DateTimeOffset NextRestartAt;
        public string? LastRestartError;
        public bool RestartedByMonitor;
        public bool GaveUpNotified;
    }

    private sealed class WatchState
    {
        public ServiceRunState? LastState;
        public DateTimeOffset StateSince;
        public bool EverSeenRunning;
        public DateTimeOffset? LastSeenRunning;
        public int LastProcessId;
        public ServiceSnapshot? LastSnapshot;
        public string? QueryError;
        public int PortFailures;
        public string? PortError;
        public ActiveProblem? Problem;
        public Task<ServiceActionResult>? RestartTask;
        public readonly Queue<DateTimeOffset> SentTimes = new();
        public bool MutedNoticeSent;
        public string Detail = "";
    }
}
