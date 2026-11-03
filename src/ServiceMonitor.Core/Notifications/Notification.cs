using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Core.Notifications;

public enum NotificationKind { Alert, Warning, Recovery, Reminder, Info, Heartbeat, Test }

public enum Severity { Critical, Warning, Good, Info }

/// <summary>A channel-neutral message. Each channel renders it in its own format (HTML email, Teams card, push...).</summary>
public sealed class Notification
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public NotificationKind Kind { get; init; }
    public Severity Severity { get; init; }

    /// <summary>Short headline, e.g. "Contoso Billing Service crashed".</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences explaining what happened and what (if anything) was done.</summary>
    public string Summary { get; init; } = "";

    public string? ServiceName { get; init; }
    public string? ServiceDisplayName { get; init; }
    public string MachineName { get; init; } = AppInfo.MachineName;
    public string SiteName { get; init; } = "";
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    public List<KeyValuePair<string, string>> Facts { get; init; } = [];
    public List<EventSummary> Events { get; init; } = [];

    public string Tag => Kind switch
    {
        NotificationKind.Alert => "ALERT",
        NotificationKind.Warning => "WARNING",
        NotificationKind.Recovery => "RESOLVED",
        NotificationKind.Reminder => "STILL DOWN",
        NotificationKind.Heartbeat => "OK",
        NotificationKind.Test => "TEST",
        _ => "INFO",
    };

    /// <summary>"Contoso (SERVER01)" or just "SERVER01".</summary>
    public string Origin => string.IsNullOrWhiteSpace(SiteName) ? MachineName : $"{SiteName.Trim()} ({MachineName})";

    public string Subject => $"[{Tag}] {Origin}: {Title}";

    public string Emoji => Severity switch
    {
        Severity.Critical => "🔴",
        Severity.Warning => "🟠",
        Severity.Good => "🟢",
        _ => "🔵",
    };

    public string ColorHex => Severity switch
    {
        Severity.Critical => "#C42B1C",
        Severity.Warning => "#C26A00",
        Severity.Good => "#0F7B0F",
        _ => "#005FB8",
    };

    public Notification AddFact(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Facts.Add(new(name, value));
        return this;
    }
}
