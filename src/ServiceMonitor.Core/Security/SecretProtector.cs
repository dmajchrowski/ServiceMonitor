using System.Security.Cryptography;
using System.Text;

namespace ServiceMonitor.Core.Security;

/// <summary>
/// Encrypts secrets (passwords, tokens, webhook URLs) with Windows DPAPI in machine scope so that both the
/// configuration app (run by an administrator) and the background service (LocalSystem) can read them,
/// but they are useless if the files are copied to another computer. The data folder is additionally
/// locked down to SYSTEM and Administrators by <see cref="SecureFolder"/>.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "WindowsServiceMonitor/v1/secret"u8.ToArray();

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        // Values typed by hand into config.json (e.g. by a deployment script) are accepted as plaintext.
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Config copied from another machine: the secret has to be entered again.
            return string.Empty;
        }
    }

    public static byte[] ProtectBytes(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);

    public static byte[] UnprotectBytes(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);
}
