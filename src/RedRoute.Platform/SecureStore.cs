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
    public static void PrepareServiceDirectory()
    {
        var dir = Directory.CreateDirectory(ServiceDirectory);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Небезопасный каталог службы.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
    }
    public static ClientSettings Load(bool machine = false)
    {
        var path = Path.Combine(machine ? ServiceDirectory : UserDirectory, "settings.dpapi");
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
    {
        settings.Validate();
        var folder = machine ? ServiceDirectory : UserDirectory;
        Directory.CreateDirectory(folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Protocol.Json);
        try
        {
            var data = ProtectedData.Protect(bytes, "RED_ROUTE.settings.v1"u8.ToArray(), machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
            var temporary = Path.Combine(folder, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, Path.Combine(folder, "settings.dpapi"), true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
