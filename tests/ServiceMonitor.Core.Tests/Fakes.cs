using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Notifications;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Core.Tests;

internal sealed class FakeInspector : IServiceInspector
{
    public Dictionary<string, ServiceSnapshot> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> StartCalls { get; } = [];
    public Func<string, ServiceActionResult> OnStart { get; set; } = _ => new ServiceActionResult(true, "Service started");

    public void Set(string name, ServiceRunState state, int pid = 1234, int exitCode = 0) =>
        Services[name] = new ServiceSnapshot(name, name + " Display", state, exitCode, 0, state == ServiceRunState.Running ? pid : 0,
            "Automatic", $@"C:\Program Files\{name}\{name}.exe");

    public ServiceSnapshot Query(string serviceName) =>
        Services.TryGetValue(serviceName, out var s) ? s : new ServiceSnapshot(serviceName, serviceName, ServiceRunState.NotFound);

    public IReadOnlyList<ServiceListItem> ListServices() => [];

    public Task<ServiceActionResult> StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        StartCalls.Add(serviceName);
        var result = OnStart(serviceName);
        if (result.Success) Set(serviceName, ServiceRunState.Running, pid: 5678);
        return Task.FromResult(result);
    }
}

internal sealed class FakeDiagnostics : IEventDiagnostics
{
    public List<EventSummary> Events { get; } = [];
    public IReadOnlyList<EventSummary> GetRecentEvents(ServiceSnapshot service, TimeSpan lookback, int max = 6) => Events;
}

internal sealed class FakeProbe : IPortProbe
{
    public bool Up { get; set; } = true;
    public Task<ProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(Up ? new ProbeResult(true) : new ProbeResult(false, "connection refused"));
}

internal sealed class ListSink : INotificationSink
{
    public List<Notification> Sent { get; } = [];
    public void Enqueue(Notification notification) => Sent.Add(notification);
}

/// <summary>Wires the engine to fakes. The clock starts well after boot so the startup grace period is over unless a test says otherwise.</summary>
internal sealed class EngineHarness
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    public FakeInspector Inspector { get; } = new();
    public FakeDiagnostics Diagnostics { get; } = new();
    public FakeProbe Probe { get; } = new();
    public ListSink Sink { get; } = new();
    public MonitorConfig Config { get; } = new();
    public ActivityLog Activity { get; }
    public MonitorEngine Engine { get; }

    public EngineHarness(TimeSpan? uptime = null, params string[] services)
    {
        Activity = new ActivityLog(NullLogger.Instance, time: Time);
        var boot = Time.GetUtcNow() - (uptime ?? TimeSpan.FromHours(5));
        Engine = new MonitorEngine(Inspector, Diagnostics, Probe, Sink, Activity, Time, boot);
        foreach (var s in services.DefaultIfEmpty("Svc"))
        {
            Config.Services.Add(new MonitoredService { ServiceName = s, DisplayName = s + " Display" });
            Inspector.Set(s, ServiceRunState.Running);
        }
    }

    public MonitoredService Service(string name = "Svc") => Config.Services.First(s => s.ServiceName == name);

    public Task PollAsync() => Engine.PollAsync(Config, CancellationToken.None);

    public async Task AdvanceAndPollAsync(TimeSpan by)
    {
        Time.Advance(by);
        await PollAsync();
    }
}
