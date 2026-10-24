using System.Net;
using System.Net.Sockets;

namespace RedRoute.Core;

public static class Ipv6Routing
{
    // ULA is valid inside a VPN, but does not establish public connectivity on a physical adapter.
    public static bool IsPublicSource(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6
        && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast
        && !address.IsIPv4MappedToIPv6 && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6Any)
        && (address.GetAddressBytes()[0] & 0xfe) != 0xfc;

    public static bool ProfileSupportsIpv6(ParsedProfile profile) => profile.Addresses.Any(a => a.Contains(':'))
        && profile.Peer["AllowedIPs"].Split(',').Any(a => a.Contains(':'));
}
