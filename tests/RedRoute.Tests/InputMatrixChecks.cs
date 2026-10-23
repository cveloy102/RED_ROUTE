using RedRoute.Core;
using RedRoute.Setup;

internal static class InputMatrixChecks
{
    public static void Run(string config, Action<bool, string> assert)
    {
        static void Reject(Action action)
        {
            try { action(); } catch (FormatException) { return; }
            throw new Exception("Invalid input was accepted");
        }
        foreach (var name in new[] { "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts" })
        foreach (var value in new[] { "bad", "-1", "4294967296", "9-1", "1-2-3" })
        {
            Reject(() => ConfParser.Parse(config.Replace("Jc = 4", "Jc = 4\n" + name + " = " + value)));
            assert(true, "AWG import rejects invalid " + name + " range " + value);
        }
        Reject(() => ConfParser.Parse(config.Replace("H1 = 5", "H1 = 6")));
        assert(true, "AWG overlapping header ranges fail at import instead of tunnel startup");
        Reject(() => ConfParser.Parse(config.Replace("Jmin = 40", "Jmin = 71")));
        assert(true, "AWG reversed junk packet bounds are rejected");
        Reject(() => ConfParser.Parse(config.Replace("Jc = 4", "Jc = 4\nHeaderProtectionKey = " + Convert.ToBase64String(new byte[32]))));
        assert(true, "AWG header protection requires sufficient padding on all four packet types");
        Reject(() => Domains.NormalizeCidr("fe80::1%42/128"));
        assert(true, "Interface-scoped IPv6 cannot create an invalid routing rule");
        foreach (var domain in new[] { "xn--a.com", "xn--.com", "a..example", "a\u0000.example", "-bad.example", "a_b.example", "https://u:p@example.com", "http://[::1]", "http://127.1" })
        {
            Reject(() => Domains.Normalize(domain)); assert(true, "Malformed domain is consistently reported as a validation error");
        }
        var random = new Random(102);
        var accepted = 0; var rejected = 0;
        for (var i = 0; i < 2000; i++)
        {
            var sample = new string(Enumerable.Range(0, random.Next(1, 180)).Select(_ => (char)random.Next(0, 65536)).ToArray());
            try { _ = Domains.Normalize(sample); accepted++; } catch (FormatException) { rejected++; }
        }
        assert(accepted + rejected == 2000, "2000 deterministic Unicode inputs never escape domain validation with an unexpected exception");
        var fields = new[] { "MTU", "ListenPort", "Jc", "Jmin", "Jmax", "S1", "S3", "H1", "I1", "RandomTrailers", "RekeyTimeout" };
        var values = new[] { "", "0", "-1", "65535", "65536", "4294967295", "4294967296", "1-4", "9-1", "true", "NaN" };
        foreach (var field in fields)
        foreach (var value in values)
        {
            var lines = config.Split('\n').Where(l => !l.StartsWith(field + " =", StringComparison.OrdinalIgnoreCase));
            var sample = string.Join('\n', lines).Replace("[Peer]", field + " = " + value + "\n[Peer]");
            ParsedProfile profile;
            try { profile = ConfParser.Parse(sample); }
            catch (FormatException) { continue; }
            _ = CoreConfiguration.Build(profile, new ClientSettings { Mode = RouteMode.Auto }, false, 12345, "synthetic");
        }
        assert(true, "121 structured conf mutations are either rejected at import or generate configuration without exceptions");
        const string owner = "S-1-5-21-100-200-300-1001";
        const string other = "S-1-5-21-100-200-300-1002";
        assert(InstallOwner.Select(owner, other) == owner, "An upgrade preserves the existing owner instead of the administrator or another desktop account");
        assert(InstallOwner.Select(null, owner) == owner, "First setup grants control to the desktop user whose session launched the installer");
        assert(InstallOwner.Select("broken", owner) == owner && InstallOwner.Select("S-1-5-18", owner) == owner, "Invalid and SYSTEM installer owners cannot replace the desktop user");
        assert(InstallOwner.Select(null, "S-1-12-1-1-2-3-4") == "S-1-12-1-1-2-3-4", "Entra ID desktop accounts remain valid installation owners");
        try { InstallOwner.Select(null, null); throw new Exception("Missing owner accepted"); } catch (IOException) { assert(true, "Installer refuses to guess an owner when no interactive desktop is available"); }
        var original = new ClientSettings { Mode = RouteMode.Auto, Sites = [new("IP service", [], ["192.0.2.0/24"], "ip-service"), new("Own", ["own.example"], [])] };
        var refreshed = SettingsCommands.RefreshServices(original, id => id == "ip-service" ? new("Fresh", [], ["198.51.100.0/24"], id) : null);
        assert(refreshed.Sites[0].IpRanges[0] == "198.51.100.0/24" && refreshed.Sites[1] == original.Sites[1] && refreshed.Mode == RouteMode.Auto, "IP-only services refresh by ID while custom rules and mode remain unchanged");
        try
        {
            SettingsCommands.RefreshServices(original, id => new("Too large", Enumerable.Repeat("example.org", 30001).ToArray(), [], id));
            throw new Exception("Oversized rule update accepted");
        }
        catch (FormatException) { assert(original.Sites[0].IpRanges[0] == "192.0.2.0/24", "An oversized catalogue refresh cannot partially mutate the user's selected rules"); }
    }
}
