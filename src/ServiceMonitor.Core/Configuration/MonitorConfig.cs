namespace ServiceMonitor.Core.Configuration;

/// <summary>Everything the configuration app edits and the background service obeys. Stored as config.json.</summary>
public sealed class MonitorConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Customer / site name shown in every notification, e.g. "Contoso - Head Office".</summary>
    public string SiteName { get; set; } = "";

    public List<MonitoredService> Services { get; set; } = [];
    public List<ChannelConfig> Channels { get; set; } = [];
    public MonitorSettings Settings { get; set; } = new();
    public OAuthAppSettings OAuth { get; set; } = new();

    /// <summary>Maintenance mode: while set and in the future, problems are tracked but nobody is notified.</summary>
    public DateTimeOffset? AlertsPausedUntil { get; set; }

    public bool AlertsPaused(DateTimeOffset now) => AlertsPausedUntil is { } until && until > now;
}

public sealed class MonitoredService
{
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Try to start the service again when it stops or crashes.</summary>
    public bool AutoRestart { get; set; }

    /// <summary>Overrides <see cref="MonitorSettings.HangThresholdSeconds"/> for this service.</summary>
    public int? HangThresholdSeconds { get; set; }

    /// <summary>Optional TCP check that catches a service that is "Running" but no longer answering.</summary>
    public PortCheck? PortCheck { get; set; }
}

public sealed class PortCheck
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int TimeoutSeconds { get; set; } = 5;
    public int FailuresBeforeAlert { get; set; } = 3;
}

public enum HeartbeatFrequency { Off, Daily, Weekly }

public sealed class MonitorSettings
{
    /// <summary>How often service states are checked. Crash events from the Windows event log trigger an immediate check.</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>A service stuck in Starting/Stopping for longer than this is reported as hung.</summary>
    public int HangThresholdSeconds { get; set; } = 120;

    /// <summary>Repeat the alert while a service stays down. 0 = never.</summary>
    public int ReminderIntervalMinutes { get; set; } = 60;

    public bool NotifyOnRecovery { get; set; } = true;
    public bool NotifyOnMonitorStartStop { get; set; } = true;

    /// <summary>Report crashes even when Windows' own recovery restarted the service before we noticed it was down.</summary>
    public bool NotifyOnSelfHealedCrash { get; set; } = true;

    /// <summary>After a reboot, give automatic services this long to start before reporting them as not running.</summary>
    public int StartupGraceSeconds { get; set; } = 180;

    public int RestartDelaySeconds { get; set; } = 30;
    public int MaxRestartAttempts { get; set; } = 3;

    /// <summary>Attach recent Service Control Manager, Application Error and .NET Runtime events to alerts.</summary>
    public bool IncludeEventLogDetails { get; set; } = true;

    /// <summary>Flood protection for a service that keeps crashing and restarting.</summary>
    public int MaxNotificationsPerServicePerHour { get; set; } = 12;

    /// <summary>"Still watching" summary that proves the notification path works.</summary>
    public HeartbeatFrequency Heartbeat { get; set; } = HeartbeatFrequency.Off;
    public int HeartbeatHour { get; set; } = 8;
    public DayOfWeek HeartbeatDay { get; set; } = DayOfWeek.Monday;
}

/// <summary>
/// OAuth app registrations used for "Sign in with Microsoft / Google". Leave empty to use the values
/// baked into the build (see BuildDefaults); fill in to override on a single machine.
/// </summary>
public sealed class OAuthAppSettings
{
    public string MicrosoftClientId { get; set; } = "";
    public string MicrosoftTenant { get; set; } = "";
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";

    public string EffectiveMicrosoftClientId => Or(MicrosoftClientId, BuildDefaults.MicrosoftClientId);
    public string EffectiveMicrosoftTenant => Or(MicrosoftTenant, Or(BuildDefaults.MicrosoftTenant, "common"));
    public string EffectiveGoogleClientId => Or(GoogleClientId, BuildDefaults.GoogleClientId);
    public string EffectiveGoogleClientSecret => Or(GoogleClientSecret, BuildDefaults.GoogleClientSecret);

    public bool MicrosoftConfigured => EffectiveMicrosoftClientId.Length > 0;
    public bool GoogleConfigured => EffectiveGoogleClientId.Length > 0 && EffectiveGoogleClientSecret.Length > 0;

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
