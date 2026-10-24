using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedRoute.Core;

internal static class Ipv6RoutingChecks
{
    public static void Run(string conf, Action<bool, string> assert)
    {
        foreach (var address in new[] { "::", "::1", "fe80::1", "fd12::1", "fc00::1", "ff02::1", "::ffff:192.0.2.1", "fec0::1" })
            assert(!Ipv6Routing.IsPublicSource(IPAddress.Parse(address)), "Local/non-routable IPv6 is not public connectivity: " + address);
        assert(Ipv6Routing.IsPublicSource(IPAddress.Parse("2001:db8::1")), "Global-scope IPv6 remains eligible for a route check");
        var v4 = ConfParser.Parse(conf);
        var v6 = ConfParser.Parse(conf.Replace("Address = 10.8.0.2/32", "Address = 10.8.0.2/32, fd00:1234::2/128"));
        var noAllowedV6 = ConfParser.Parse(conf.Replace("Address = 10.8.0.2/32", "Address = 10.8.0.2/32, fd00:1234::2/128").Replace(", ::/0", ""));
        assert(!Ipv6Routing.ProfileSupportsIpv6(v4) && Ipv6Routing.ProfileSupportsIpv6(v6) && !Ipv6Routing.ProfileSupportsIpv6(noAllowedV6), "VPN IPv6 requires both an interface address and a peer IPv6 route; ULA inside VPN is valid");

        var cases = new List<object>();
        foreach (var mode in new[] { RouteMode.Auto, RouteMode.On })
        foreach (var direct in new[] { false, true })
        foreach (var vpn in new[] { false, true })
        {
            var name = $"{mode}/direct6={direct}/vpn6={vpn}";
            var settings = new ClientSettings { Mode = mode, Sites = [new("Selected", ["selected.example"], ["2001:db8:42::/48"])] };
            var json = CoreConfiguration.Build(vpn ? v6 : v4, settings, true, 12345, "synthetic", directIpv6Available: direct);
            var rules = json["route"]!["rules"]!.AsArray();
            var rejects = rules.Where(r => r?["action"]?.ToString() == "reject").ToArray();
            var defaultReject = mode == RouteMode.On ? !vpn : !direct && !vpn;
            assert(rejects.Length == (defaultReject || mode == RouteMode.Auto && !vpn ? 1 : 0), name + ": reject policy follows the actual outbound");
            assert(rejects.All(r => r!["no_drop"]!.GetValue<bool>() && rules.IndexOf(r) < rules.ToList().FindIndex(x => x?["action"]?.ToString() == "sniff")), name + ": unsupported IPv6 refuses before sniff, even after many requests");
            assert(json["inbounds"]![1]!["address"]!.AsArray().Any(a => a!.ToString().Contains(':')), name + ": IPv6 stays captured to prevent full-VPN bypass");
            assert(json["route"]!["final"]!.ToString() == (mode == RouteMode.On ? "vpn" : "direct"), name + ": default outbound is preserved");
            var domainResolve = rules.First(r => r?["action"]?.ToString() == "resolve" && r?["inbound"] == null)!;
            assert(domainResolve["override_destination"]!.GetValue<bool>() == !vpn, name + ": selected cached AAAA can be re-resolved on IPv4-only VPN");

            void Case(string destination, bool reject, string? outbound = null, string? domain = null, string inbound = "tun") => cases.Add(new
            {
                name = name + "/" + destination + "/" + inbound,
                rules = rules.DeepClone(), destination, domain = domain ?? "", inbound, reject, outbound
            });
            Case("[2001:db8:10::1]:443", defaultReject);
            Case("[2001:db8:42::1]:443", !vpn,
                mode == RouteMode.Auto && vpn ? "vpn" : null);
            Case("[fd12::1]:443", false);
            Case("[fe80::1]:443", false);
            Case("192.0.2.20:443", false);
            // FakeIP unmapping occurs before matchRule; the resulting FQDN must not match an IP-version rejection.
            Case("selected.example:443", false, domain: "selected.example");
            Case("fakeip4", false);
            Case("fakeip6", false);
            Case("[2001:db8:10::1]:443", false, inbound: "health");
            if (defaultReject)
            {
                // Negative control: the same rejection placed after sniff cannot reject a SYN early.
                var late = rules.DeepClone().AsArray();
                foreach (var rejection in late.Where(r => r?["action"]?.ToString() == "reject").ToArray())
                {
                    late.Remove(rejection); late.Add(rejection);
                }
                cases.Add(new { name = name + "/negative-control-late-reject", rules = (JsonNode)late,
                    destination = "[2001:db8:10::1]:443", domain = "", inbound = "tun", reject = false, outbound = (string?)null });
            }
            var probe = CoreConfiguration.Build(vpn ? v6 : v4, settings, false, 12345, "synthetic", directIpv6Available: direct);
            assert(probe["inbounds"]!.AsArray().All(i => i!["type"]!.ToString() != "tun"), name + ": candidate never adds system routes");
        }
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ipv6-routing-cases.json"), JsonSerializer.Serialize(cases));
    }

    public static async Task CheckNetworkChangeAsync(string conf, Action<bool, string> assert)
    {
        var engine = new NetworkEngine();
        var controller = new FailoverController(engine);
        await controller.ConfigureAsync(new ClientSettings { Mode = RouteMode.Auto, Profiles = [new(Guid.NewGuid(), "Synthetic", conf)] }, default);
        var before = engine.CaptureStarts;
        engine.NetworkChanged = true;
        await controller.TickAsync(default);
        assert(engine.CaptureStarts == before + 1 && controller.Status.Phase == ConnectionPhase.Connected
            && controller.Status.Events.Any(e => e.Code == "network_changed"), "Physical network change releases capture and rebuilds routing capabilities before reconnecting");
        await controller.RecoverAsync(default);
        engine.NetworkChanged = true;
        await controller.TickAsync(default);
        assert(engine.CaptureStarts == before + 1 && !engine.IsRunning, "Network change never reconnects a VPN which the user switched Off");
    }

    private sealed class NetworkEngine : ITunnelEngine
    {
        public bool IsRunning { get; private set; }
        public bool NetworkChanged { get; set; }
        public int CaptureStarts { get; private set; }
        public IReadOnlyList<ProbeResult> LastChecks => [];
        public Task StartAsync(VpnProfile p, ClientSettings s, bool capture, CancellationToken t)
        { IsRunning = true; NetworkChanged = false; if (capture) CaptureStarts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken t) { IsRunning = false; return Task.CompletedTask; }
        public Task<bool> CheckAsync(CancellationToken t) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
