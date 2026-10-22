using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RedRoute.Platform;

public static class DirectNetwork
{
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
