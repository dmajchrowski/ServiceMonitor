using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ServiceMonitor.Core.Monitoring;

public enum ActivityLevel { Info, Success, Warning, Error }

public sealed record ActivityEntry(DateTimeOffset Time, ActivityLevel Level, string Message, string? Service = null);

/// <summary>Event IDs written to the Windows Application log, so RMM tools can alert on them too.</summary>
public static class EventIds
{
    public const int MonitorStarted = 100;
    public const int MonitorStopped = 101;
    public const int ConfigReloaded = 102;
    public const int ServiceProblem = 200;
    public const int ServiceRecovered = 201;
    public const int ServiceWarning = 202;
    public const int RestartAttempt = 203;
    public const int NotificationSent = 300;
    public const int NotificationFailed = 301;
    public const int ChannelNeedsAttention = 302;
}

public interface IWindowsEventLog
{
    void Write(ActivityLevel level, int eventId, string message);
}

public sealed class WindowsEventLog : IWindowsEventLog
{
    /// <summary>Registers the event source. Requires administrator rights; done by the installer and the service.</summary>
    public static void EnsureSource()
    {
        try
        {
            if (!EventLog.SourceExists(AppInfo.EventLogSource)) EventLog.CreateEventSource(AppInfo.EventLogSource, "Application");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException)
        {
        }
    }

    public void Write(ActivityLevel level, int eventId, string message)
    {
        var type = level switch
        {
            ActivityLevel.Error => EventLogEntryType.Error,
            ActivityLevel.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
        try
        {
            EventLog.WriteEntry(AppInfo.EventLogSource, message.Length > 30000 ? message[..30000] : message, type, eventId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
        }
    }
}

/// <summary>Recent activity shown in the app's Activity page. Also mirrors entries to the log file and the Windows event log.</summary>
public sealed class ActivityLog(ILogger logger, IWindowsEventLog? eventLog = null, TimeProvider? time = null, int capacity = 300)
{
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Lock _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void Add(ActivityLevel level, string message, string? service = null, int? eventId = null)
    {
        var entry = new ActivityEntry(_time.GetLocalNow(), level, message, service);
        lock (_lock)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > capacity) _entries.RemoveLast();
        }

        var logLevel = level switch
        {
            ActivityLevel.Error => LogLevel.Error,
            ActivityLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(logLevel, "{Service}{Message}", service is null ? "" : $"[{service}] ", message);
        if (eventId is { } id) eventLog?.Write(level, id, service is null ? message : $"{service}: {message}");
    }

    public IReadOnlyList<ActivityEntry> Snapshot(int max = 150)
    {
        lock (_lock) return _entries.Take(max).ToList();
    }

    /// <summary>Restores history from the previous run so the Activity page survives restarts.</summary>
    public void Seed(IEnumerable<ActivityEntry>? previous)
    {
        if (previous is null) return;
        lock (_lock)
        {
            foreach (var entry in previous.OrderByDescending(e => e.Time))
            {
                if (_entries.Count >= capacity) break;
                _entries.AddLast(entry);
            }
        }
    }
}
