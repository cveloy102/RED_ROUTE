using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using RedRoute.Core;
using RedRoute.Platform;

/// <summary>Regression checks for defects found in the 1.0.1 audit.</summary>
internal static class AuditFixChecks
{
    public static async Task RunAsync(string config, Action<bool, string> assert)
    {
        bool Rejects(Action action) { try { action(); return false; } catch (FormatException) { return true; } }

        // Legacy IPv4 forms used to be stored as a different network.
        assert(Rejects(() => Domains.NormalizeCidr("192.168.1/24")), "Three-octet IPv4 is rejected instead of becoming 192.168.0.1/24");
        assert(Rejects(() => Domains.NormalizeCidr("10")) && Rejects(() => Domains.NormalizeCidr("0x7f.1")) && Rejects(() => Domains.NormalizeCidr("010.0.0.1")),
            "Integer, hexadecimal and leading-zero IPv4 forms are rejected");
        assert(Rejects(() => Domains.NormalizeCidr("10.0.0.0/+8")) && Rejects(() => Domains.NormalizeCidr("10.0.0.0/ 8")), "Prefix length must be plain digits");
        assert(Domains.NormalizeCidr(" 192.168.1.0/24 ") == "192.168.1.0/24" && Domains.NormalizeCidr("8.8.8.8") == "8.8.8.8/32"
            && Domains.NormalizeCidr("2001:DB8::/32") == "2001:db8::/32" && Domains.NormalizeCidr("::ffff:1.2.3.4") == "::ffff:1.2.3.4/128",
            "Canonical IPv4/IPv6 rules are still accepted");
        assert(Domains.Normalize("*.Example.com") == "example.com" && Domains.Normalize(".example.com") == "example.com", "Wildcard domain input means the domain and its subdomains");

        // wg-quick accepts a comment after a section header.
        assert(ConfParser.Parse(config.Replace("[Interface]", "[Interface] # main").Replace("[Peer]", "[Peer]   # server")).Endpoint.Port == 51820,
            "Section headers may carry a trailing comment");
        try { ConfParser.Parse(config.Replace("[Peer]", "[Peers]")); assert(false, "Unknown section rejected"); }
        catch (FormatException ex) { assert(ex.Message.Contains("Неизвестный раздел"), "An unknown section is named instead of a misleading duplicate-section error"); }
        var ipv4Only = config.Replace("AllowedIPs = 0.0.0.0/0, ::/0", "AllowedIPs = 0.0.0.0/0");
        assert(ConfParser.Parse(ipv4Only.Replace("[Peer]", "MTU = 1200\n[Peer]")).Interface["MTU"] == "1200", "IPv4-only profile accepts an MTU below 1280");
        assert(Rejects(() => ConfParser.Parse(config.Replace("Address = 10.8.0.2/32", "Address = 10.8.0.2/32, fd00::2/128").Replace("[Peer]", "MTU = 1200\n[Peer]"))),
            "IPv6 profile still requires MTU 1280");

        // Split-tunnel profiles cannot pass health checks through WireGuard's AllowedIPs filter.
        ParsedProfile With(string allowed) => ConfParser.Parse(config.Replace("AllowedIPs = 0.0.0.0/0, ::/0", "AllowedIPs = " + allowed));
        const string excludePrivate = "0.0.0.0/5, 8.0.0.0/7, 11.0.0.0/8, 12.0.0.0/6, 16.0.0.0/4, 32.0.0.0/3, 64.0.0.0/3, 96.0.0.0/6, 100.0.0.0/10, 100.128.0.0/9, 101.0.0.0/8, 102.0.0.0/7, 104.0.0.0/5, 112.0.0.0/5, 120.0.0.0/6, 124.0.0.0/7, 126.0.0.0/8, 128.0.0.0/3, 160.0.0.0/5, 168.0.0.0/8, 169.0.0.0/9, 169.128.0.0/10, 169.192.0.0/11, 169.224.0.0/12, 169.240.0.0/13, 169.248.0.0/14, 169.252.0.0/15, 169.255.0.0/16, 170.0.0.0/7, 172.0.0.0/12, 172.32.0.0/11, 172.64.0.0/10, 172.128.0.0/9, 173.0.0.0/8, 174.0.0.0/7, 176.0.0.0/4, 192.0.0.0/9, 192.128.0.0/11, 192.160.0.0/13, 192.169.0.0/16, 192.170.0.0/15, 192.172.0.0/14, 192.176.0.0/12, 192.192.0.0/10, 193.0.0.0/8, 194.0.0.0/7, 196.0.0.0/6, 200.0.0.0/5, 208.0.0.0/4, ::/0";
        assert(ProfileRequirements.CoversPublicIpv4(With("0.0.0.0/0")) && ProfileRequirements.CoversPublicIpv4(With("0.0.0.0/1, 128.0.0.0/1"))
            && ProfileRequirements.CoversPublicIpv4(With(excludePrivate)), "Full-tunnel and exclude-private profiles cover every public IPv4 destination");
        assert(!ProfileRequirements.CoversPublicIpv4(With("10.8.0.0/24")) && !ProfileRequirements.CoversPublicIpv4(With("0.0.0.0/1"))
            && !ProfileRequirements.CoversPublicIpv4(With("::/0")), "Split-tunnel profiles are recognized");
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var split = Path.Combine(folder, "split.conf");
        await File.WriteAllTextAsync(split, config.Replace("AllowedIPs = 0.0.0.0/0, ::/0", "AllowedIPs = 10.8.0.0/24"));
        try { await ProfileImport.ReadAsync(new ClientSettings(), [split]); assert(false, "Split-tunnel import rejected"); }
        catch (FormatException ex) { assert(ex.Message.Contains("AllowedIPs"), "Importing a split-tunnel profile explains AllowedIPs instead of failing every check later"); }

        // The profile's own private resolver is used through the tunnel; public resolvers keep encrypted DNS.
        string RemoteDns(string text) { var dns = CoreConfiguration.Build(ConfParser.Parse(text), new ClientSettings { Mode = RouteMode.On }, true, 1, "p")["dns"]!["servers"]![1]!; return dns["type"] + " " + dns["server"]; }
        assert(RemoteDns(config.Replace("[Peer]", "DNS = 10.8.0.1, 1.1.1.1\n[Peer]")) == "udp 10.8.0.1", "Private in-tunnel DNS from the profile is used");
        assert(RemoteDns(config.Replace("[Peer]", "DNS = 1.1.1.1, 8.8.8.8\n[Peer]")) == "https 1.1.1.1" && RemoteDns(config) == "https 1.1.1.1", "Public or missing DNS keeps DNS-over-HTTPS");
        assert(RemoteDns(config.Replace("[Peer]", "DNS = fd00::53\n[Peer]")) == "https 1.1.1.1", "IPv6 DNS is not used for an IPv4-only tunnel");

        // A single lost IPv6 probe must not rebuild a working tunnel.
        var detector = new Ipv6ChangeDetector();
        assert(!detector.Observe(false, true) && !detector.Observe(false, true) && detector.Observe(false, true), "IPv6 policy changes only after three consecutive differing probes");
        detector.Reset();
        assert(!detector.Observe(false, true) && !detector.Observe(true, true) && !detector.Observe(false, true) && !detector.Observe(false, true), "A successful probe resets the IPv6 mismatch count");

        // Engine failures are explained without addresses.
        var summary = EngineLog.Summarize("\u001b[31mFATAL\u001b[0m[0000] start service: initialize inbound[1]: listen tcp 127.0.0.1:41234: bind: Only one usage of each socket address");
        assert(summary != null && summary.Contains("start service") && !summary.Contains("127.0.0.1") && !summary.Contains("41234"), "Fatal engine line is reduced to a category without addresses: " + summary);
        assert(EngineLog.Summarize("ERROR[0001] dns: exchange failed for vpn.example.com: \"secret\"") is { } dns && !dns.Contains("example.com") && !dns.Contains("secret")
            && EngineLog.Summarize("INFO[0000] started") == null && EngineLog.Summarize("[2001:db8::1]:443 FATAL x") is { } v6 && !v6.Contains("2001"),
            "Hostnames, quoted values and IPv6 literals are removed; informational lines are ignored");
        var failing = new ThrowingEngine(new EngineException("Ядро VPN завершилось при запуске: start service"));
        var controller = new FailoverController(failing);
        await controller.ConfigureAsync(new ClientSettings { Mode = RouteMode.On, Profiles = [new(Guid.NewGuid(), "A", config)] }, default);
        assert(controller.Status.Events.Any(e => e.Code == "engine_error" && e.Message.Contains("start service")), "A sanitized engine error reaches the event log");

        // Network fingerprint ignores rotating IPv6 identifiers and gateway-less virtual adapters.
        string Print(string temp, bool withVirtual, string gw = "192.168.1.1") => DirectNetwork.Fingerprint(new[]
        {
            ("eth", (IEnumerable<(IPAddress, int)>)[(IPAddress.Parse("192.168.1.20"), 24), (IPAddress.Parse("2001:db8:1:2::10"), 64), (IPAddress.Parse(temp), 128), (IPAddress.Parse("fe80::1234"), 64)],
                (IEnumerable<IPAddress>)[IPAddress.Parse(gw)], (IEnumerable<IPAddress>)[IPAddress.Parse("192.168.1.1")]),
        }.Concat(withVirtual ? [("vEthernet (WSL)", (IEnumerable<(IPAddress, int)>)[(IPAddress.Parse("172.20.0.1"), 20)], (IEnumerable<IPAddress>)[], (IEnumerable<IPAddress>)[])] : []));
        assert(Print("2001:db8:1:2:aaaa::1", false) == Print("2001:db8:1:2:bbbb::2", true), "Temporary IPv6 rotation and a gateway-less virtual adapter do not change the network fingerprint");
        assert(Print("2001:db8:1:2:aaaa::1", false) != Print("2001:db8:1:2:aaaa::1", false, "192.168.1.254"), "A changed default gateway still changes the fingerprint");

        // Service directory: only SYSTEM/Administrators own trusted files; runtime config never reuses an existing file's ACL.
        assert(SecureStore.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) && SecureStore.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
            && !SecureStore.IsTrustedOwner(WindowsIdentity.GetCurrent().User) && !SecureStore.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)),
            "Only SYSTEM and Administrators are trusted owners of the service directory");
        var planted = Path.Combine(folder, "runtime.json");
        await File.WriteAllTextAsync(planted, "planted");
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var plantedAcl = new FileSecurity(); plantedAcl.SetAccessRuleProtection(true, false);
        plantedAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        plantedAcl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(planted).SetAccessControl(plantedAcl);
        SecureStore.WriteNewFile(planted, "{\"private\":true}");
        var rules = new FileInfo(planted).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        assert(File.ReadAllText(planted) == "{\"private\":true}" && !rules.Any(r => r.IdentityReference.Equals(everyone) && !r.IsInherited)
            && !new FileInfo(planted).GetAccessControl().AreAccessRulesProtected, "Runtime configuration replaces a planted file instead of inheriting its readable ACL");

        // The verified update stays locked until the elevated installer starts.
        var installer = Path.Combine(folder, "RED_ROUTE-9.9.9-Setup.exe"); var bytes = RandomNumberGenerator.GetBytes(4096); await File.WriteAllBytesAsync(installer, bytes);
        var release = new ReleaseUpdate(new Version(9, 9, 9), "", new Uri("https://github.com/x"), new Uri("https://github.com/x"), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using (GitHubUpdates.OpenVerified(installer, release))
        {
            var locked = false;
            try { using var write = new FileStream(installer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { locked = true; }
            try { File.Move(installer, installer + ".moved"); } catch (IOException) { locked &= true; }
            assert(locked && File.Exists(installer), "Verified installer cannot be rewritten or replaced while it is being launched");
        }
        try { using var _ = GitHubUpdates.OpenVerified(installer, release with { Sha256 = new string('0', 64) }); assert(false, "Wrong hash rejected"); }
        catch (IOException) { assert(true, "An installer with a different SHA-256 is not opened for launch"); }
        Directory.Delete(folder, true);
    }

    private sealed class ThrowingEngine(Exception error) : ITunnelEngine
    {
        public bool IsRunning => false;
        public IReadOnlyList<ProbeResult> LastChecks => [];
        public Task StartAsync(VpnProfile p, ClientSettings s, bool capture, CancellationToken t) => Task.FromException(error);
        public Task StopAsync(CancellationToken t) => Task.CompletedTask;
        public Task<bool> CheckAsync(CancellationToken t) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
