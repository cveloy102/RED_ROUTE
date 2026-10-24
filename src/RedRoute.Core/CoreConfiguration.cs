using System.Text.Json.Nodes;

namespace RedRoute.Core;

public static class CoreConfiguration
{
    public static JsonObject Build(ParsedProfile profile, ClientSettings settings, bool captureTraffic, int healthPort, string password, string? directDnsServer = null, string? directInterface = null, string? dnsCachePath = null, bool directIpv6Available = false)
    {
        var vpnIpv6 = Ipv6Routing.ProfileSupportsIpv6(profile);
        var vpnStrategy = vpnIpv6 ? "prefer_ipv4" : "ipv4_only";
        var (host, port) = profile.Endpoint;
        JsonObject peer = new()
        {
            ["address"] = host, ["port"] = port, ["public_key"] = profile.Peer["PublicKey"],
            ["allowed_ips"] = Strings(profile.Peer["AllowedIPs"].Split(',').Select(Domains.NormalizeCidr))
        };
        if (profile.Peer.TryGetValue("PresharedKey", out var psk)) peer[profile.IsAmnezia ? "preshared_key" : "pre_shared_key"] = psk;
        if (profile.Peer.TryGetValue("PersistentKeepalive", out var keep)) peer["persistent_keepalive_interval"] = int.Parse(keep);
        JsonObject endpoint = new()
        {
            ["type"] = profile.IsAmnezia ? "awg" : "wireguard", ["tag"] = "vpn",
            ["address"] = Strings(profile.Addresses), ["private_key"] = profile.Interface["PrivateKey"],
            ["mtu"] = profile.Interface.TryGetValue("MTU", out var mtu) ? int.Parse(mtu) : 1280,
            ["peers"] = new JsonArray(peer), ["domain_resolver"] = "remote"
        };
        if (profile.Interface.TryGetValue("ListenPort", out var listenPort)) endpoint["listen_port"] = int.Parse(listenPort);
        if (profile.IsAmnezia)
        {
            endpoint["useIntegratedTun"] = false;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HeaderProtectionKey"] = "header_protection_key", ["ContentPaddingAddition"] = "content_padding_addition",
                ["RekeyAfterTime"] = "rekey_after_time", ["RekeyTimeout"] = "rekey_timeout", ["RejectAfterTime"] = "reject_after_time",
                ["KeepaliveTimeout"] = "keepalive_timeout", ["MaxHandshakeAttempts"] = "max_handshake_attempts",
                ["RandomTrailers"] = "random_trailers", ["DisableCookies"] = "disable_cookies"
            };
            foreach (var (key, value) in profile.Interface.Where(kv => ConfParser.AwgFields.Contains(kv.Key)))
            {
                var field = names.GetValueOrDefault(key, key.ToLowerInvariant());
                if (new[] { "jc", "jmin", "jmax", "s1", "s2", "s3", "s4" }.Contains(field)) endpoint[field] = int.Parse(value);
                else if (field is "random_trailers" or "disable_cookies") endpoint[field] = bool.Parse(value);
                else endpoint[field] = value;
            }
        }
        JsonArray inbounds = [new JsonObject
        {
            ["type"] = "mixed", ["tag"] = "health", ["listen"] = "127.0.0.1", ["listen_port"] = healthPort,
            ["users"] = new JsonArray(new JsonObject { ["username"] = "redroute", ["password"] = password })
        }];
        if (captureTraffic) inbounds.Add(new JsonObject
        {
            ["type"] = "tun", ["tag"] = "tun", ["interface_name"] = "RED_ROUTE",
            ["address"] = Strings(new[] { "172.30.255.1/30", "fdfe:dcba:9876::1/126" }),
            ["mtu"] = 1280, ["auto_route"] = true, ["strict_route"] = false, ["stack"] = "mixed"
        });
        // AWG's userspace netstack has no resolver of its own. Resolve domains in the routing core before dialing it.
        JsonArray rules = [new JsonObject { ["inbound"] = Strings(["health"]), ["action"] = "resolve", ["server"] = "remote", ["strategy"] = vpnStrategy },
            new JsonObject { ["inbound"] = Strings(["health"]), ["outbound"] = "vpn" }];
        // Reject an unusable public IPv6 path before sniff accepts TCP. Browsers can then try IPv4.
        // Keep the IPv6 TUN even for IPv4 profiles so full VPN cannot leak through a physical IPv6 route.
        // FakeIP is unmapped before rule matching and LAN/private IPv6 must remain available.
        var ipv6Ranges = settings.Sites.SelectMany(s => s.IpRanges).Select(Domains.NormalizeCidr).Where(s => s.Contains(':')).Distinct().ToArray();
        if (settings.Mode == RouteMode.Auto && vpnIpv6 && ipv6Ranges.Length > 0)
            rules.Add(PublicIpv6Rule(new JsonObject { ["ip_cidr"] = Strings(ipv6Ranges) }, "vpn"));
        if (captureTraffic && (settings.Mode == RouteMode.On ? !vpnIpv6 : !directIpv6Available && !vpnIpv6))
            rules.Add(PublicIpv6Rule(null));
        else if (captureTraffic && settings.Mode == RouteMode.Auto && !vpnIpv6 && ipv6Ranges.Length > 0)
            rules.Add(PublicIpv6Rule(new JsonObject { ["ip_cidr"] = Strings(ipv6Ranges) }));
        rules.Add(new JsonObject { ["action"] = "sniff" });
        rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        // Private LAN traffic stays local, except during explicit health probes above.
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });
        if (settings.Mode == RouteMode.Auto)
        {
            var domains = settings.Sites.SelectMany(s => s.Domains).Select(Domains.Normalize).Distinct().ToArray();
            var ips = settings.Sites.SelectMany(s => s.IpRanges).Select(Domains.NormalizeCidr).Distinct().ToArray();
            if (domains.Length > 0)
            {
                rules.Add(new JsonObject { ["domain_suffix"] = Strings(domains), ["action"] = "resolve", ["server"] = "remote", ["strategy"] = vpnStrategy, ["override_destination"] = !vpnIpv6 });
                rules.Add(new JsonObject { ["domain_suffix"] = Strings(domains), ["outbound"] = "vpn" });
            }
            if (ips.Length > 0) rules.Add(new JsonObject { ["ip_cidr"] = Strings(ips), ["outbound"] = "vpn" });
            // A dual-stack VPN may still be used on an IPv4-only physical connection. Preserve
            // selected IPv6 sites above, but re-resolve an unselected sniffed AAAA directly as IPv4.
            if (!directIpv6Available) rules.Add(new JsonObject { ["ip_version"] = 6, ["action"] = "resolve", ["server"] = "local", ["strategy"] = "ipv4_only", ["override_destination"] = true });
        }
        else if (settings.Mode == RouteMode.On) rules.Add(new JsonObject { ["action"] = "resolve", ["server"] = "remote", ["strategy"] = vpnStrategy, ["override_destination"] = !vpnIpv6 });
        // FakeIP keeps domains distinct even when unrelated sites share a CDN IP.
        JsonArray dnsRules = [];
        if (settings.Mode == RouteMode.Auto)
        {
            var selected = settings.Sites.SelectMany(s => s.Domains).Select(Domains.Normalize).Distinct().ToArray();
            if (selected.Length > 0) dnsRules.Add(new JsonObject { ["domain_suffix"] = Strings(selected), ["query_type"] = Strings(["A", "AAAA"]), ["server"] = "fakeip" });
            if (selected.Length > 0) dnsRules.Add(new JsonObject { ["domain_suffix"] = Strings(selected), ["server"] = "remote" });
        }
        var localDns = new JsonObject { ["type"] = directDnsServer == null ? "local" : "udp", ["tag"] = "local" };
        if (directDnsServer != null)
        {
            localDns["server"] = directDnsServer;
            if (!string.IsNullOrWhiteSpace(directInterface)) localDns["bind_interface"] = directInterface;
        }
        var result = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(localDns,
                    new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["detour"] = "vpn" },
                    new JsonObject { ["type"] = "fakeip", ["tag"] = "fakeip", ["inet4_range"] = "198.18.0.0/15", ["inet6_range"] = "fc00::/18" }),
                ["rules"] = dnsRules, ["final"] = settings.Mode == RouteMode.On ? "remote" : "local", ["independent_cache"] = true,
                ["strategy"] = settings.Mode == RouteMode.On ? vpnStrategy : directIpv6Available || vpnIpv6 ? "prefer_ipv4" : "ipv4_only"
            },
            ["inbounds"] = inbounds, ["endpoints"] = new JsonArray(endpoint),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "local" }),
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = settings.Mode == RouteMode.On ? "vpn" : "direct", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "local" }
        };
        if (captureTraffic && dnsCachePath != null)
            result["experimental"] = new JsonObject { ["cache_file"] = new JsonObject { ["enabled"] = true, ["path"] = dnsCachePath, ["cache_id"] = "redroute-dns-v1", ["store_fakeip"] = true } };
        return result;
    }
    private static JsonObject PublicIpv6Rule(JsonObject? additional, string? outbound = null)
    {
        JsonArray match = [new JsonObject { ["inbound"] = Strings(["tun"]), ["ip_version"] = 6 },
            new JsonObject { ["ip_is_private"] = true, ["invert"] = true }];
        if (additional != null) match.Add(additional);
        var rule = new JsonObject { ["type"] = "logical", ["mode"] = "and", ["rules"] = match };
        if (outbound != null) rule["outbound"] = outbound;
        else { rule["action"] = "reject"; rule["method"] = "default"; rule["no_drop"] = true; }
        return rule;
    }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
