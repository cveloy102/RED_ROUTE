using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RedRoute.Core;

namespace RedRoute.Platform;

public static class DirectNetwork
{
    // Exclude our adapter so creating/removing a TUN cannot trigger a reconnect loop.
    public static string Fingerprint() => string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.Name != "RED_ROUTE" && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderBy(n => n.Id).Select(n =>
        {
            var p = n.GetIPProperties();
            return n.Id + ":" + n.OperationalStatus + ":" + string.Join(",", p.UnicastAddresses.Select(a => a.Address.ToString()).Order())
                + ":" + string.Join(",", p.GatewayAddresses.Select(a => a.Address.ToString()).Order())
                + ":" + string.Join(",", p.DnsAddresses.Select(a => a.ToString()).Order());
        }));

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
