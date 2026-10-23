using System.Security.Cryptography;
using System.Text;
using RedRoute.Core;
using RedRoute.Platform;

internal static class StorageChecks
{
    public static void Run(string config, Action<bool, string> assert)
    {
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "storage-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.dpapi");
        var settings = new ClientSettings { Mode = RouteMode.Auto, Profiles = [new(Guid.NewGuid(), "Synthetic", config)], Sites = [new("Synthetic", ["example.org"], [])] };
        assert(SecureStore.LoadFrom(path).Mode == RouteMode.Off, "First start with no settings defaults to Off");
        SecureStore.SaveTo(settings, folder);
        var encrypted = File.ReadAllBytes(path); var plain = Encoding.UTF8.GetString(encrypted);
        assert(!plain.Contains("PrivateKey", StringComparison.Ordinal) && !plain.Contains(config, StringComparison.Ordinal), "Saved settings contain encrypted data rather than plaintext VPN configuration");
        var loaded = SecureStore.LoadFrom(path);
        assert(loaded.Mode == RouteMode.Auto && loaded.Profiles[0].Config == config && loaded.Sites[0].Domains[0] == "example.org", "DPAPI round trip preserves mode, profiles and rules");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { SecureStore.SaveTo(new ClientSettings(), folder); throw new Exception("Locked settings unexpectedly replaced"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { assert(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Failed settings replacement cleans temporary files"); }
        }
        assert(File.ReadAllBytes(path).SequenceEqual(encrypted), "A sharing or disk error cannot destroy the previous settings file");
        SecureStore.SaveTo(new ClientSettings { Mode = RouteMode.On, Profiles = settings.Profiles }, folder);
        assert(SecureStore.LoadFrom(path).Mode == RouteMode.On, "Atomic settings replacement succeeds after a transient write failure");
        File.WriteAllBytes(path, [1, 2, 3]);
        try { SecureStore.LoadFrom(path); throw new Exception("Damaged DPAPI accepted"); }
        catch (CryptographicException) { assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "Corrupt settings are reported without overwriting the original file"); }
        using (var file = File.Create(path)) file.SetLength(Protocol.MaximumMessageBytes + 1);
        try { SecureStore.LoadFrom(path); throw new Exception("Oversized settings accepted"); }
        catch (IOException) { assert(true, "Oversized settings are rejected before decryption"); }
        File.WriteAllBytes(path, encrypted);
        var invalid = new ClientSettings { Sites = [new("Invalid", ["localhost"], [])] };
        try { SecureStore.SaveTo(invalid, folder); throw new Exception("Invalid settings saved"); }
        catch (FormatException) { assert(File.ReadAllBytes(path).SequenceEqual(encrypted), "Validation failure leaves working encrypted settings untouched"); }
    }
}
