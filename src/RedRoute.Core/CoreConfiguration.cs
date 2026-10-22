using System.Text.Json.Nodes;

namespace RedRoute.Core;

public static class CoreConfiguration
{
    public static JsonObject Build(ParsedProfile profile, ClientSettings settings, bool captureTraffic, int healthPort, string password)
    {
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
        JsonArray rules = [new JsonObject { ["inbound"] = Strings(["health"]), ["outbound"] = "vpn" },
            new JsonObject { ["action"] = "sniff" }, new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }];
        // Private LAN traffic stays local, except during explicit health probes above.
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });
        if (settings.Mode == RouteMode.Auto)
        {
            var domains = settings.Sites.SelectMany(s => s.Domains).Select(Domains.Normalize).Distinct().ToArray();
            var ips = settings.Sites.SelectMany(s => s.IpRanges).Select(Domains.NormalizeCidr).Distinct().ToArray();
            if (domains.Length > 0) rules.Add(new JsonObject { ["domain_suffix"] = Strings(domains), ["outbound"] = "vpn" });
            if (ips.Length > 0) rules.Add(new JsonObject { ["ip_cidr"] = Strings(ips), ["outbound"] = "vpn" });
        }
        // FakeIP keeps domains distinct even when unrelated sites share a CDN IP.
        JsonArray dnsRules = [];
        if (settings.Mode == RouteMode.Auto)
        {
            var selected = settings.Sites.SelectMany(s => s.Domains).Select(Domains.Normalize).Distinct().ToArray();
            if (selected.Length > 0) dnsRules.Add(new JsonObject { ["domain_suffix"] = Strings(selected), ["query_type"] = Strings(["A", "AAAA"]), ["server"] = "fakeip" });
            if (selected.Length > 0) dnsRules.Add(new JsonObject { ["domain_suffix"] = Strings(selected), ["server"] = "remote" });
        }
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" },
                    new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["detour"] = "vpn" },
                    new JsonObject { ["type"] = "fakeip", ["tag"] = "fakeip", ["inet4_range"] = "198.18.0.0/15", ["inet6_range"] = "fc00::/18" }),
                ["rules"] = dnsRules, ["final"] = settings.Mode == RouteMode.On ? "remote" : "local", ["independent_cache"] = true,
                ["strategy"] = profile.Addresses.Any(a => a.Contains(':')) ? "prefer_ipv4" : "ipv4_only"
            },
            ["inbounds"] = inbounds, ["endpoints"] = new JsonArray(endpoint),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "local" }),
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = settings.Mode == RouteMode.On ? "vpn" : "direct", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "local" }
        };
    }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
