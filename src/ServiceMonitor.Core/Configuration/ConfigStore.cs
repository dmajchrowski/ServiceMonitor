using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Core.Configuration;

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowOutOfOrderMetadataProperties = true, // hand-edited files may put "type" anywhere
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MonitorConfig Load(string? path = null)
    {
        path ??= AppInfo.ConfigFile;
        var json = SecureFolder.TryReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new MonitorConfig();
        return Deserialize(json);
    }

    public static MonitorConfig Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<MonitorConfig>(json, JsonOptions) ?? new MonitorConfig();
        config.Services ??= [];
        config.Channels ??= [];
        config.Settings ??= new MonitorSettings();
        config.OAuth ??= new OAuthAppSettings();
        return config;
    }

    public static string Serialize(MonitorConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public static void Save(MonitorConfig config, string? path = null)
    {
        path ??= AppInfo.ConfigFile;
        SecureFolder.Ensure(Path.GetDirectoryName(path)!);
        SecureFolder.WriteAllTextAtomic(path, Serialize(config));
    }

    public static MonitorConfig Clone(MonitorConfig config) => Deserialize(Serialize(config));

    public static DateTime LastWriteTimeUtc(string? path = null)
    {
        path ??= AppInfo.ConfigFile;
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }
}
