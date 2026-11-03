using System.Security.AccessControl;
using System.Security.Principal;

namespace ServiceMonitor.Core.Security;

/// <summary>Creates the data folder so that only SYSTEM and local Administrators can read it.</summary>
public static class SecureFolder
{
    public static void Ensure(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists) return;

        try
        {
            dir.Create(LockedDown());
        }
        catch (UnauthorizedAccessException)
        {
            // Not elevated (e.g. unit tests): fall back to an ordinary folder.
            Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    /// Like <see cref="Ensure"/>, and also locks down a folder that already exists with other permissions, e.g. one a
    /// deployment script created to drop a config.json in. The files in it take the same permissions.
    /// </summary>
    public static void EnsureLockedDown(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            Ensure(path);
            return;
        }
        try
        {
            dir.SetAccessControl(LockedDown());
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static DirectorySecurity LockedDown()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>Writes a file atomically so a concurrent reader never sees a half-written file.</summary>
    public static void WriteAllTextAtomic(string path, string contents) =>
        WriteAllBytesAtomic(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytesAtomic(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, contents);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50); // reader has the file open; retry briefly
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static string? TryReadAllText(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }
}
