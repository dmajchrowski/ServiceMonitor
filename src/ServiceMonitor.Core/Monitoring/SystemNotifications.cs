using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Notifications;

namespace ServiceMonitor.Core.Monitoring;

/// <summary>Notifications about the monitor itself: started, stopping, heartbeat and test messages.</summary>
public static class SystemNotifications
{
    public static Notification MonitorStarted(MonitorConfig config, IReadOnlyList<ServiceStatusInfo> services, DateTimeOffset bootTime, TimeProvider time)
    {
        var uptime = time.GetUtcNow() - bootTime;
        var watched = services.Count(s => s.Health != HealthLevel.Disabled);
        var summary = uptime < TimeSpan.FromMinutes(15)
            ? $"The computer started at {TimeText.Local(bootTime, time)} (up {TimeText.Duration(uptime)}). {AppInfo.ProductName} is watching {Plural(watched, "service")}."
            : $"{AppInfo.ProductName} started and is watching {Plural(watched, "service")}.";
        var n = new Notification
        {
            Kind = NotificationKind.Info,
            Severity = Severity.Info,
            Title = $"Monitoring started on {AppInfo.MachineName}",
            Summary = summary,
            SiteName = config.SiteName,
            Time = time.GetLocalNow(),
        };
        AddServiceList(n, services);
        return n;
    }

    public static Notification MonitorStopping(MonitorConfig config, TimeProvider time) => new()
    {
        Kind = NotificationKind.Warning,
        Severity = Severity.Warning,
        Title = $"Monitoring stopped on {AppInfo.MachineName}",
        Summary = $"{AppInfo.ProductName} is stopping – either its service was stopped or Windows is shutting down or restarting. " +
                  "Until it runs again, nobody will be notified about service problems on this computer.",
        SiteName = config.SiteName,
        Time = time.GetLocalNow(),
    };

    public static Notification Heartbeat(MonitorConfig config, IReadOnlyList<ServiceStatusInfo> services, TimeProvider time)
    {
        var watched = services.Where(s => s.Health != HealthLevel.Disabled).ToList();
        var bad = watched.Count(s => s.Health is HealthLevel.Problem or HealthLevel.Warning);
        var n = new Notification
        {
            Kind = NotificationKind.Heartbeat,
            Severity = bad == 0 ? Severity.Good : Severity.Warning,
            Title = bad == 0
                ? watched.Count == 1 ? "The monitored service is running" : $"All {watched.Count} monitored services are running"
                : $"{bad} of {watched.Count} monitored services need attention",
            Summary = $"{(config.Settings.Heartbeat == HeartbeatFrequency.Weekly ? "Weekly" : "Daily")} check-in from {AppInfo.ProductName}. " +
                      "This message also confirms that notifications from this computer are getting through.",
            SiteName = config.SiteName,
            Time = time.GetLocalNow(),
        };
        AddServiceList(n, services);
        return n;
    }

    public static Notification Test(MonitorConfig config, string channelName) => new()
    {
        Kind = NotificationKind.Test,
        Severity = Severity.Info,
        Title = "Test notification",
        Summary = $"This is a test from {AppInfo.ProductName} on {AppInfo.MachineName} through the '{channelName}' channel. " +
                  "If you can read this, alerts about stopped, crashed or hung services will reach you here.",
        SiteName = config.SiteName,
        Facts =
        [
            new("Computer", AppInfo.MachineName),
            new("Site", string.IsNullOrWhiteSpace(config.SiteName) ? "(not set)" : config.SiteName),
            new("Monitored services", config.Services.Count(s => s.Enabled) is var c and > 0
                ? string.Join(", ", config.Services.Where(s => s.Enabled).Select(s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.ServiceName : s.DisplayName))
                : "(none selected yet)"),
        ],
    };

    private static void AddServiceList(Notification n, IReadOnlyList<ServiceStatusInfo> services)
    {
        foreach (var s in services.Where(s => s.Health != HealthLevel.Disabled))
        {
            var mark = s.Health switch { HealthLevel.Ok => "✔", HealthLevel.Problem => "✖", HealthLevel.Warning => "⚠", _ => "…" };
            n.AddFact(s.DisplayName, $"{mark} {s.State}" + (s.Health == HealthLevel.Ok ? "" : $" – {s.Detail}"));
        }
    }

    private static string Plural(int count, string word) => count == 1 ? $"1 {word}" : $"{count} {word}s";
}
