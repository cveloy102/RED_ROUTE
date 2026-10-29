using RedRoute.Core;
using RedRoute.Platform;

internal static class ProfileImportChecks
{
    internal static async Task RunAsync(string config, Action<bool, string> assert)
    {
        foreach (var key in new[] { "RandomTrailers", "DisableCookies" })
        foreach (var (text, expected) in new[] { ("on", true), ("OFF", false), ("true", true), ("False", false), ("1", true), ("0", false) })
        {
            var parsed = ConfParser.Parse(config.Replace("[Peer]", key + " = " + text + "\n[Peer]"));
            var endpoint = CoreConfiguration.Build(parsed, new(), false, 12345, "synthetic")["endpoints"]![0]!;
            assert(endpoint[key == "RandomTrailers" ? "random_trailers" : "disable_cookies"]!.GetValue<bool>() == expected,
                "AWG boolean " + key + "=" + text + " reaches native JSON as a boolean");
        }
        foreach (var interval in new[] { "25-35", "0-0", "65535-65535" })
        {
            var parsed = ConfParser.Parse(config.Replace("PersistentKeepalive = 25", "PersistentKeepalive = " + interval));
            var peer = CoreConfiguration.Build(parsed, new(), false, 12345, "synthetic")["endpoints"]![0]!["peers"]![0]!;
            assert(peer["persistent_keepalive_interval"]!.GetValue<string>() == interval, "AWG keepalive range preserved end-to-end: " + interval);
        }
        foreach (var interval in new[] { "35-25", "-1", "1-65536", "1-2-3" })
        {
            try { ConfParser.Parse(config.Replace("PersistentKeepalive = 25", "PersistentKeepalive = " + interval)); throw new Exception("Invalid interval accepted"); }
            catch (FormatException) { assert(true, "Invalid AWG keepalive range rejected: " + interval); }
        }
        var recovered = new ClientSettings { Mode = RouteMode.Off, Profiles = [new(Guid.NewGuid(), "Original", config)], Sites = [new("Keep", ["keep.example"], [])] };
        var stale = new ClientSettings { Mode = RouteMode.On, Profiles = [.. recovered.Profiles, new(Guid.NewGuid(), "Second", config)], AutoFailover = false };
        var updated = SettingsCommands.UpdateProfiles(recovered, stale);
        assert(updated.Mode == RouteMode.Off && updated.Profiles.Count == 2 && updated.Sites.SequenceEqual(recovered.Sites), "Profile import arriving after recovery cannot reenable the tunnel or replace routing rules");
        stale.Profiles.Clear();
        assert(SettingsCommands.UpdateProfiles(new ClientSettings { Mode = RouteMode.On }, stale).Mode == RouteMode.Off, "Removing the last profile always leaves the VPN Off");
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "profile-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Second.conf");
        var second = config.Replace("192.0.2.1:51820", "192.0.2.2:51820").Replace("[Peer]", "RandomTrailers = on\nDisableCookies = on\n[Peer]").Replace("PersistentKeepalive = 25", "PersistentKeepalive = 25-35");
        await File.WriteAllTextAsync(path, second);
        var original = new ClientSettings { Mode = RouteMode.Auto, Profiles = [new(Guid.NewGuid(), "Primary", config)] };
        var result = await ProfileImport.ReadAsync(original, [path]);
        assert(result.Added == 1 && result.Profiles.Count == 2 && original.Profiles.Count == 1 && result.Profiles[0] == original.Profiles[0], "Importing an advanced second profile validates atomically without changing the original list");
        var saved = new ClientSettings { Mode = original.Mode, Profiles = result.Profiles.ToList() };
        result = await ProfileImport.ReadAsync(saved, [path]);
        assert(result.Added == 0 && result.Duplicates == 1 && result.Profiles.Count == 2, "Reimporting the second file reports a duplicate explicitly");
        var equivalent = Path.Combine(folder, "Equivalent.conf");
        await File.WriteAllTextAsync(equivalent, "# same profile\n" + second.Replace(" = on", " = True"));
        result = await ProfileImport.ReadAsync(saved, [equivalent]);
        assert(result.Duplicates == 1 && result.Added == 0, "Comments and equivalent boolean spellings cannot create duplicate connections");
        var bad = Path.Combine(folder, "Broken.conf"); await File.WriteAllTextAsync(bad, second.Replace("RandomTrailers = on", "RandomTrailers = maybe"));
        try { await ProfileImport.ReadAsync(original, [path, bad]); throw new Exception("Invalid batch accepted"); }
        catch (FormatException) { assert(original.Profiles.Count == 1, "One bad file cannot partially import a multi-file batch or corrupt the existing profile"); }
        var full = new ClientSettings { Profiles = Enumerable.Range(0, 20).Select(i => new VpnProfile(Guid.NewGuid(), "Profile " + i, config)).ToList() };
        try { await ProfileImport.ReadAsync(full, [path]); throw new Exception("Profile limit exceeded"); }
        catch (FormatException) { assert(full.Profiles.Count == 20, "Profile limit failure preserves all existing connections"); }
    }
}
