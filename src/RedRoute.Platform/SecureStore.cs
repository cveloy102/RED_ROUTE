using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RedRoute.Core;

namespace RedRoute.Platform;

public static class SecureStore
{
    public static string UserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RED_ROUTE");
    public static string ServiceDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RED_ROUTE");
    public static void PrepareServiceDirectory() => PrepareProtectedDirectory(ServiceDirectory);

    internal static DirectorySecurity ProtectedAcl()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    internal static bool IsTrustedOwner(SecurityIdentifier? owner) => owner != null
        && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>
    /// ProgramData lets ordinary users create folders. A directory (or file inside it) created before the service
    /// first ran may carry an attacker's owner and protected ACL, or an open handle. Such a directory is moved aside
    /// and recreated; trusted leftovers are reset to the inherited protected ACL.
    /// </summary>
    internal static void PrepareProtectedDirectory(string path)
    {
        if (Directory.Exists(path) && !IsTrustedTree(path))
            Directory.Move(path, path + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) dir = ProtectedAcl().CreateDirectory(path);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Небезопасный каталог службы.");
        dir.SetAccessControl(ProtectedAcl());
        foreach (var file in dir.EnumerateFiles())
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(false, false);
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            file.SetAccessControl(acl);
        }
        // The engine configuration contains a private key. A crash can leave it behind until the next start.
        var runtime = Path.Combine(path, "runtime.json");
        if (File.Exists(runtime)) File.Delete(runtime);
    }

    private static bool IsTrustedTree(string path)
    {
        var dir = new DirectoryInfo(path);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        if (!IsTrustedOwner(dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) return false;
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            // The service creates only plain files here.
            if (entry is DirectoryInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (!IsTrustedOwner(((FileInfo)entry).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) return false;
        }
        return true;
    }

    /// <summary>Writes a file that only the service account may read, never reusing an existing file's ACL.</summary>
    public static void WriteNewFile(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) writer.Write(text);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ClientSettings Load(bool machine = false)
        => LoadFrom(Path.Combine(machine ? ServiceDirectory : UserDirectory, "settings.dpapi"), machine);
    internal static ClientSettings LoadFrom(string path, bool machine = false)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > Protocol.MaximumMessageBytes) throw new IOException("Слишком большой файл настроек.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), "RED_ROUTE.settings.v1"u8.ToArray(), machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
        try
        {
            var settings = JsonSerializer.Deserialize<ClientSettings>(bytes, Protocol.Json) ?? throw new IOException("Не удалось прочитать настройки.");
            settings.Validate(); return settings;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void Save(ClientSettings settings, bool machine = false)
        => SaveTo(settings, machine ? ServiceDirectory : UserDirectory, machine);
    internal static void SaveTo(ClientSettings settings, string folder, bool machine = false)
    {
        settings.Validate();
        Directory.CreateDirectory(folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Protocol.Json);
        string? temporary = null;
        try
        {
            var data = ProtectedData.Protect(bytes, "RED_ROUTE.settings.v1"u8.ToArray(), machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
            temporary = Path.Combine(folder, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, Path.Combine(folder, "settings.dpapi"), true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
