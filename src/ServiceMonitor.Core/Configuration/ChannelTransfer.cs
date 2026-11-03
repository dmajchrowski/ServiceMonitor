using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Core.Configuration;

/// <summary>What an export file says about itself before the password is entered.</summary>
public sealed record ChannelExportInfo(int Count, string Machine, DateTimeOffset Exported);

/// <summary>
/// Channel export files (*.smchannels): the channels with their passwords, tokens and webhook URLs, so the same channels
/// can be set up on another server or after a reinstall. Saved secrets are DPAPI-encrypted for this computer only
/// (<see cref="SecretProtector"/>), so the file carries them encrypted with a password instead: AES-256-GCM with a key
/// derived by PBKDF2-SHA256. Sign-ins (Google, Microsoft) are not included; those channels need signing in again.
/// </summary>
public static class ChannelTransfer
{
    public const string FileExtension = ".smchannels";
    public const string FileFilter = "Service Monitor channels (*.smchannels)|*.smchannels";
    public const int MinimumPasswordLength = 8;

    private const string Format = "ServiceMonitor.Channels";
    private const int Iterations = 600_000;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(Format + "/1");

    public static string Export(IEnumerable<ChannelConfig> channels, string password)
    {
        var plain = channels.Select(c =>
        {
            var copy = JsonSerializer.Deserialize<ChannelConfig>(JsonSerializer.Serialize(c, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;
            copy.TransformSecrets(SecretProtector.Unprotect);
            return copy;
        }).ToList();
        var data = JsonSerializer.SerializeToUtf8Bytes(plain, ConfigStore.JsonOptions);

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(DeriveKey(password, salt, Iterations), tag.Length))
            aes.Encrypt(nonce, data, cipher, tag, AssociatedData);
        CryptographicOperations.ZeroMemory(data);

        var file = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["product"] = $"{AppInfo.ProductName} {AppInfo.Version}",
            ["machine"] = AppInfo.MachineName,
            ["exported"] = DateTimeOffset.Now,
            ["channels"] = plain.Count,
            ["encryption"] = new JsonObject
            {
                ["cipher"] = "AES-256-GCM",
                ["kdf"] = "PBKDF2-SHA256",
                ["iterations"] = Iterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
            },
            ["data"] = Convert.ToBase64String(cipher),
        };
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Checks that <paramref name="fileText"/> is a channel export, without the password.</summary>
    /// <exception cref="InvalidDataException">It is not one.</exception>
    public static ChannelExportInfo ReadInfo(string fileText)
    {
        var file = Parse(fileText);
        return new ChannelExportInfo(
            file["channels"]?.GetValue<int>() ?? 0,
            file["machine"]?.GetValue<string>() ?? "",
            file["exported"]?.GetValue<DateTimeOffset>() ?? default);
    }

    /// <summary>Decrypts an export and encrypts its secrets for this computer.</summary>
    /// <exception cref="InvalidDataException">Not a channel export, a wrong password or a damaged file.</exception>
    public static List<ChannelConfig> Import(string fileText, string password)
    {
        var file = Parse(fileText);
        byte[] data;
        try
        {
            var e = file["encryption"]!;
            var salt = Convert.FromBase64String(e["salt"]!.GetValue<string>());
            var nonce = Convert.FromBase64String(e["nonce"]!.GetValue<string>());
            var tag = Convert.FromBase64String(e["tag"]!.GetValue<string>());
            var cipher = Convert.FromBase64String(file["data"]!.GetValue<string>());
            data = new byte[cipher.Length];
            using var aes = new AesGcm(DeriveKey(password, salt, e["iterations"]!.GetValue<int>()), tag.Length);
            aes.Decrypt(nonce, cipher, tag, data, AssociatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("That password is not the one the file was exported with.");
        }
        catch (Exception ex) when (ex is FormatException or NullReferenceException or InvalidOperationException or ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("The file is damaged.", ex);
        }

        try
        {
            var channels = JsonSerializer.Deserialize<List<ChannelConfig>>(data, ConfigStore.JsonOptions) ?? [];
            foreach (var c in channels) c.TransformSecrets(SecretProtector.Protect);
            return channels;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file holds channels this version does not know.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Adds imported channels to <paramref name="channels"/>. One with the same name as an existing channel replaces it
    /// in place and keeps its id, so a sign-in saved for that channel stays with it.
    /// </summary>
    public static (int Added, int Replaced) Merge(IList<ChannelConfig> channels, IEnumerable<ChannelConfig> imported)
    {
        int added = 0, replaced = 0;
        foreach (var channel in imported)
        {
            var index = channels.ToList().FindIndex(c => c.Name.Equals(channel.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                channel.Id = channels[index].Id;
                channels[index] = channel;
                replaced++;
            }
            else
            {
                if (channels.Any(c => c.Id == channel.Id)) channel.Id = Guid.NewGuid().ToString("N");
                channels.Add(channel);
                added++;
            }
        }
        return (added, replaced);
    }

    private static JsonNode Parse(string fileText)
    {
        try
        {
            var file = JsonNode.Parse(fileText);
            if (file?["format"]?.GetValue<string>() == Format && file["version"]?.GetValue<int>() == 1) return file;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }
        throw new InvalidDataException("This is not a Service Monitor channel export.");
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}
