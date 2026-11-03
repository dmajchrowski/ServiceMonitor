using System.Text.Json;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Core.Monitoring;

public enum HealthLevel { Ok, Problem, Warning, Pending, Unknown, Disabled }

/// <summary>Written by the background service (status.json) and displayed live by the configuration app.</summary>
public sealed class MonitorStatus
{
    public string Version { get; set; } = AppInfo.Version;
    public string MachineName { get; set; } = AppInfo.MachineName;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int PollIntervalSeconds { get; set; }
    public DateTimeOffset? AlertsPausedUntil { get; set; }
    public List<ServiceStatusInfo> Services { get; set; } = [];
    public List<ChannelStatusInfo> Channels { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];

    /// <summary>The service writes the file every poll; a stale file means the monitor is not running.</summary>
    public bool IsFresh(DateTimeOffset now) => now - UpdatedAt < TimeSpan.FromSeconds(Math.Max(30, PollIntervalSeconds * 3));

    public static MonitorStatus? Load(string? path = null)
    {
        var json = SecureFolder.TryReadAllText(path ?? AppInfo.StatusFile);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MonitorStatus>(json, ConfigStore.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppInfo.StatusFile;
        SecureFolder.WriteAllTextAtomic(path, JsonSerializer.Serialize(this, ConfigStore.JsonOptions));
    }
}

public sealed class ServiceStatusInfo
{
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = "";
    public HealthLevel Health { get; set; }
    public string Detail { get; set; } = "";
    public DateTimeOffset? Since { get; set; }
}

public sealed class ChannelStatusInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
    public string LastError { get; set; } = "";
}

/// <summary>Small bits of state that must survive a restart of the monitor (state.json).</summary>
public sealed class PersistentState
{
    public DateTimeOffset? LastHeartbeat { get; set; }
    public DateTimeOffset? LastTokenKeepAlive { get; set; }

    public static PersistentState Load()
    {
        var json = SecureFolder.TryReadAllText(AppInfo.StateFile);
        if (string.IsNullOrWhiteSpace(json)) return new PersistentState();
        try
        {
            return JsonSerializer.Deserialize<PersistentState>(json, ConfigStore.JsonOptions) ?? new PersistentState();
        }
        catch (JsonException)
        {
            return new PersistentState();
        }
    }

    public void Save() => SecureFolder.WriteAllTextAtomic(AppInfo.StateFile, JsonSerializer.Serialize(this, ConfigStore.JsonOptions));
}
