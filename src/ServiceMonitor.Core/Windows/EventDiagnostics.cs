using System.Diagnostics.Eventing.Reader;
using System.Text;

namespace ServiceMonitor.Core.Windows;

public sealed record EventSummary(DateTimeOffset Time, string Log, string Source, int EventId, string Message)
{
    public bool IsCrash => (Source == ScmEvents.Provider && EventId is 7031 or 7034)
                           || (Log == "Application" && EventId is 1000 or 1026);
    public bool IsStartFailure => Source == ScmEvents.Provider && EventId is 7000 or 7009 or 7011 or 7022;
    public bool IsErrorExit => Source == ScmEvents.Provider && EventId is 7023 or 7024;
}

public interface IEventDiagnostics
{
    /// <summary>Recent events that explain why a service went down: SCM errors, crashes and .NET unhandled exceptions.</summary>
    IReadOnlyList<EventSummary> GetRecentEvents(ServiceSnapshot service, TimeSpan lookback, int max = 6);
}

public static class ScmEvents
{
    public const string Provider = "Service Control Manager";

    /// <summary>7000 failed to start, 7009/7011 timeouts, 7022 hung on starting, 7023/7024 terminated with error,
    /// 7031/7034 terminated unexpectedly, 7036 entered running/stopped state.</summary>
    public static readonly int[] Interesting = [7000, 7009, 7011, 7022, 7023, 7024, 7031, 7034];
    public static readonly int[] Crash = [7031, 7034];
}

public sealed class EventDiagnostics : IEventDiagnostics
{
    private const int MaxMessageLength = 1800;

    public IReadOnlyList<EventSummary> GetRecentEvents(ServiceSnapshot service, TimeSpan lookback, int max = 6)
    {
        var ms = (long)lookback.TotalMilliseconds;
        var results = new List<EventSummary>();

        var scmIds = string.Join(" or ", ScmEvents.Interesting.Select(id => $"EventID={id}"));
        Collect(results, "System",
            $"*[System[Provider[@Name='{ScmEvents.Provider}'] and ({scmIds}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]",
            message => Contains(message, service.DisplayName) || Contains(message, service.ServiceName));

        var exeName = Path.GetFileName(service.ExecutablePath ?? "");
        if (exeName.Length > 0)
        {
            // 1000 Application Error, 1002 Application Hang, 1026 .NET Runtime unhandled exception.
            Collect(results, "Application",
                $"*[System[(EventID=1000 or EventID=1002 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= {ms}]]]",
                message => Contains(message, exeName));
        }

        return results.OrderByDescending(e => e.Time).Take(max).ToList();
    }

    private static bool Contains(string message, string value) =>
        value.Length > 0 && message.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static void Collect(List<EventSummary> into, string log, string xpath, Func<string, bool> match)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
            for (var count = 0; count < 200 && reader.ReadEvent() is { } record; count++)
            {
                using (record)
                {
                    var message = Describe(record);
                    if (!match(message)) continue;
                    into.Add(new EventSummary(
                        record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now,
                        log, record.ProviderName ?? "", record.Id, Trim(message)));
                }
            }
        }
        catch (EventLogException)
        {
            // Log unavailable or access denied: diagnostics are best effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string Describe(EventRecord record)
    {
        string? text = null;
        try
        {
            text = record.FormatDescription();
        }
        catch (EventLogException)
        {
        }
        if (!string.IsNullOrWhiteSpace(text)) return text;
        return string.Join(" | ", record.Properties.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string Trim(string message)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines.Take(30))
        {
            if (sb.Length + line.Length > MaxMessageLength) break;
            sb.AppendLine(line.TrimEnd());
        }
        if (lines.Length > 30 || sb.Length >= MaxMessageLength) sb.AppendLine("…");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A Service Control Manager event observed live (used to react immediately and to catch crashes Windows restarted).</summary>
public sealed record ScmEvent(int EventId, string DisplayName, DateTimeOffset Time, string Message);

/// <summary>Subscribes to Service Control Manager events in the System log.</summary>
public sealed class ScmEventWatcher : IDisposable
{
    private EventLogWatcher? _watcher;

    public event Action<ScmEvent>? EventReceived;

    public void Start()
    {
        var ids = string.Join(" or ", ScmEvents.Interesting.Append(7036).Select(id => $"EventID={id}"));
        var query = new EventLogQuery("System", PathType.LogName, $"*[System[Provider[@Name='{ScmEvents.Provider}'] and ({ids})]]");
        _watcher = new EventLogWatcher(query);
        _watcher.EventRecordWritten += (_, args) =>
        {
            if (args.EventRecord is not { } record) return;
            using (record)
            {
                var displayName = record.Properties.Count > 0 ? record.Properties[0].Value?.ToString() ?? "" : "";
                EventReceived?.Invoke(new ScmEvent(record.Id, displayName,
                    record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now, EventDiagnostics.Describe(record)));
            }
        };
        _watcher.Enabled = true;
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.Enabled = false;
        _watcher.Dispose();
        _watcher = null;
    }
}
