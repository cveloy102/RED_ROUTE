using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RedRoute.Core;

namespace RedRoute.Platform;

public static class DirectNetwork
{
    // Exclude our adapter so creating/removing a TUN cannot trigger a reconnect loop.
    // Only adapters that can carry the default route matter. Virtual switches (Hyper-V/WSL/Docker) without a
    // gateway and rotating temporary IPv6 interface identifiers must not tear down a working tunnel.
    public static string Fingerprint() => Fingerprint(NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.Name != "RED_ROUTE" && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.OperationalStatus == OperationalStatus.Up)
        .Select(n =>
        {
            var p = n.GetIPProperties();
            return (n.Id, p.UnicastAddresses.Select(a => (a.Address, a.PrefixLength)), p.GatewayAddresses.Select(a => a.Address), (IEnumerable<IPAddress>)p.DnsAddresses);
        }));

    internal static string Fingerprint(IEnumerable<(string Id, IEnumerable<(IPAddress Address, int PrefixLength)> Unicast, IEnumerable<IPAddress> Gateways, IEnumerable<IPAddress> Dns)> adapters)
        => string.Join("|", adapters
            .Where(n => n.Gateways.Any(g => !g.Equals(IPAddress.Any) && !g.Equals(IPAddress.IPv6Any)))
            .OrderBy(n => n.Id, StringComparer.Ordinal).Select(n =>
                n.Id + ":" + string.Join(",", n.Unicast.Where(a => !a.Address.IsIPv6LinkLocal).Select(a => Network(a.Address, a.PrefixLength)).Distinct().Order(StringComparer.Ordinal))
                + ":" + string.Join(",", n.Gateways.Select(a => a.ToString()).Order(StringComparer.Ordinal))
                + ":" + string.Join(",", n.Dns.Select(a => a.ToString()).Order(StringComparer.Ordinal))));

    // IPv4 keeps the full address. IPv6 keeps only the routed prefix (at most /64): privacy addresses rotate
    // their interface identifier daily while the network itself is unchanged.
    private static string Network(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bits = Math.Clamp(prefixLength, 0, 64);
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < 16; i++)
        {
            var keep = Math.Clamp(bits - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes) + "/" + bits;
    }

    // Called before TUN creation. UDP Connect performs local route/source selection without sending data.
    // A DNS answer or an IPv6-enabled adapter alone is not evidence of an external IPv6 route.
    public static bool HasPublicIpv6Route()
    {
        var sources = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Name != "RED_ROUTE"
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
            .Select(a => a.Address).Where(Ipv6Routing.IsPublicSource).ToArray();
        foreach (var source in sources)
        {
            foreach (var target in new[] { "2606:4700:4700::1111", "2001:4860:4860::8888" })
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(source, 0));
                    socket.Connect(new IPEndPoint(IPAddress.Parse(target), 53));
                    return true;
                }
                catch (SocketException) { }
            }
        }
        return false;
    }

    // Capture the real adapter's DNS before TUN creation. Enumerating "local" DNS after
    // creation can include RED_ROUTE's own DNS and recursively query the tunnel.
    public static (string Server, string Interface)? Dns()
    {
        GetBestInterface(0x01010101, out var preferred);
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Name != "RED_ROUTE"
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => new { Adapter = n, Properties = n.GetIPProperties() })
            .Where(n => n.Properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
            .OrderByDescending(n => n.Adapter.Supports(NetworkInterfaceComponent.IPv4) && n.Properties.GetIPv4Properties()?.Index == preferred);
        foreach (var item in adapters)
        {
            var server = item.Properties.DnsAddresses
                .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
            if (server != null) return (server.ToString(), IPAddress.IsLoopback(server) ? "" : item.Adapter.Name);
        }
        return null;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
}
