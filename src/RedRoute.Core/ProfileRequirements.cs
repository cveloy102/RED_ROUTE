using System.Net;
using System.Net.Sockets;

namespace RedRoute.Core;

/// <summary>Checks a profile against what RED_ROUTE needs from the tunnel at runtime.</summary>
public static class ProfileRequirements
{
    // Private, loopback, link-local, CGNAT, multicast and reserved IPv4 space. Excluding these from AllowedIPs
    // ("exclude private IPs" profiles) still leaves every public destination inside the tunnel.
    private static readonly string[] NonPublicIpv4 =
        ["0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/3"];

    /// <summary>
    /// The health checks (cp.cloudflare.com, gstatic.com) and the encrypted DNS resolver are public IPv4
    /// addresses that must travel through the tunnel. WireGuard drops packets outside AllowedIPs, so a
    /// split-tunnel profile would never pass a check, and "Вкл" would lose traffic outside its ranges.
    /// </summary>
    public static bool CoversPublicIpv4(ParsedProfile profile)
    {
        var covered = Ranges(profile.Peer["AllowedIPs"].Split(',').Select(Domains.NormalizeCidr)).Concat(Ranges(NonPublicIpv4))
            .OrderBy(r => r.Start).ToArray();
        ulong next = 0;
        foreach (var (start, end) in covered)
        {
            if (start > next) return false;
            next = Math.Max(next, end + 1);
            if (next > uint.MaxValue) return true;
        }
        return false;
    }

    public static bool AllowedIpsContain(ParsedProfile profile, IPAddress address) =>
        profile.Peer["AllowedIPs"].Split(',').Select(Domains.NormalizeCidr).Any(cidr => Contains(cidr, address));

    // IPNetwork.Parse rejects host bits ("10.8.0.2/24"), which WireGuard profiles may legitimately contain.
    private static bool Contains(string cidr, IPAddress address)
    {
        var (network, prefix) = Split(cidr);
        if (network.AddressFamily != address.AddressFamily) return false;
        var a = network.GetAddressBytes(); var b = address.GetAddressBytes();
        for (var i = 0; i < a.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            var mask = (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }
    private static (IPAddress Address, int Prefix) Split(string cidr)
    {
        var parts = cidr.Split('/');
        return (IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A DNS server inside the tunnel's private network (for example AmneziaDNS or 10.8.0.1) is the server's own
    /// resolver and is used through the tunnel. Public resolvers keep the encrypted 1.1.1.1 default.
    /// </summary>
    public static IPAddress? TunnelDnsServer(ParsedProfile profile, bool ipv6)
    {
        if (!profile.Interface.TryGetValue("DNS", out var value)) return null;
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(entry, out var address) || address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) continue;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && !ipv6) continue;
            if (IsPrivate(address) && AllowedIpsContain(profile, address)) return address;
        }
        return null;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (address.GetAddressBytes()[0] & 0xfe) == 0xfc;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xf0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xc0) == 64);
    }

    private static IEnumerable<(ulong Start, ulong End)> Ranges(IEnumerable<string> cidrs)
    {
        foreach (var cidr in cidrs)
        {
            var (address, prefix) = Split(cidr);
            if (address.AddressFamily != AddressFamily.InterNetwork) continue;
            var bytes = address.GetAddressBytes();
            ulong start = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            var size = 1UL << (32 - prefix);
            start &= ~(size - 1) & uint.MaxValue;
            yield return (start, start + size - 1);
        }
    }
}
