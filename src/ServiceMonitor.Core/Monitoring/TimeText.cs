using System.Globalization;

namespace ServiceMonitor.Core.Monitoring;

/// <summary>Consistent, human friendly time formatting for notifications and the UI.</summary>
public static class TimeText
{
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds} s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes:00} min";
        return span.Hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {span.Hours} h";
    }

    public static string Ago(TimeSpan span) => span.TotalSeconds < 5 ? "just now" : $"{Duration(span)} ago";

    /// <summary>"2026-10-31 14:03:22 (UTC-05:00)" in the computer's time zone.</summary>
    public static string Local(DateTimeOffset utc, TimeProvider? time = null)
    {
        var zone = (time ?? TimeProvider.System).LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $" (UTC{sign}{offset.Duration():hh\\:mm})";
    }

    public static string LocalTime(DateTimeOffset utc, TimeProvider? time = null) =>
        TimeZoneInfo.ConvertTime(utc, (time ?? TimeProvider.System).LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
