using System.Reflection;

namespace ServiceMonitor.Core.Configuration;

/// <summary>
/// OAuth client registrations baked in at publish time so technicians never have to type them:
/// <c>publish.ps1 -MicrosoftClientId ... -GoogleClientId ... -GoogleClientSecret ...</c>
/// (passed to MSBuild and stored as assembly metadata of the main executable).
/// </summary>
public static class BuildDefaults
{
    public static string MicrosoftClientId => Get("MicrosoftClientId");
    public static string MicrosoftTenant => Get("MicrosoftTenant");
    public static string GoogleClientId => Get("GoogleClientId");
    public static string GoogleClientSecret => Get("GoogleClientSecret");

    private static readonly Lazy<Dictionary<string, string>> Values = new(() =>
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in new[] { Assembly.GetEntryAssembly(), typeof(BuildDefaults).Assembly })
        {
            if (asm is null) continue;
            foreach (var meta in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.IsNullOrWhiteSpace(meta.Value)) values.TryAdd(meta.Key, meta.Value.Trim());
            }
        }
        return values;
    });

    private static string Get(string key) => Values.Value.TryGetValue(key, out var v) ? v : "";
}
