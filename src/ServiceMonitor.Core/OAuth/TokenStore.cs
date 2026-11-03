using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Core.OAuth;

public sealed class StoredToken
{
    public string Provider { get; set; } = "";
    public string Account { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public DateTimeOffset SignedInAt { get; set; }
    public DateTimeOffset LastRefreshedAt { get; set; }
}

/// <summary>
/// One DPAPI-encrypted file per channel under %ProgramData%\ServiceMonitor\tokens. Both the configuration app
/// and the background service read and refresh tokens, so access is serialized with a lock file.
/// </summary>
public static class TokenStore
{
    private static string PathFor(string channelId) => Path.Combine(AppInfo.TokensDirectory, $"{Sanitize(channelId)}.token");

    public static StoredToken? Load(string channelId)
    {
        var path = PathFor(channelId);
        if (!File.Exists(path)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(SecretProtector.UnprotectBytes(File.ReadAllBytes(path)));
            return JsonSerializer.Deserialize<StoredToken>(json, ConfigStore.JsonOptions);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    public static void Save(string channelId, StoredToken token)
    {
        SecureFolder.Ensure(AppInfo.DataDirectory);
        Directory.CreateDirectory(AppInfo.TokensDirectory);
        var bytes = SecretProtector.ProtectBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token, ConfigStore.JsonOptions)));
        SecureFolder.WriteAllBytesAtomic(PathFor(channelId), bytes);
    }

    public static void Delete(string channelId)
    {
        try
        {
            File.Delete(PathFor(channelId));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Removes tokens for channels that no longer exist in the configuration.</summary>
    public static void DeleteOrphans(IEnumerable<string> liveChannelIds)
    {
        if (!Directory.Exists(AppInfo.TokensDirectory)) return;
        var keep = liveChannelIds.Select(Sanitize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(AppInfo.TokensDirectory, "*.token"))
        {
            if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }

    public static async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        SecureFolder.Ensure(AppInfo.DataDirectory);
        Directory.CreateDirectory(AppInfo.TokensDirectory);
        var path = Path.Combine(AppInfo.TokensDirectory, ".lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken); // DeleteOnClose race with another process
            }
        }
    }

    private static string Sanitize(string id) => string.Concat(id.Where(char.IsLetterOrDigit));
}
