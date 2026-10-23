using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedRoute.Core;
using RedRoute.Platform;

internal static class SiteDiagnostics
{
    private static readonly string[] Targets = ["4pda.to", "music.yandex.ru", "example.org"];
    public static async Task RunAsync(bool compare, bool browserHold = false, RouteMode comparisonMode = RouteMode.Off)
    {
        var original = SecureStore.Load(true);
        var userFile = Path.Combine(SecureStore.UserDirectory, "settings.dpapi");
        var userBytes = File.Exists(userFile) ? File.ReadAllBytes(userFile) : null;
        Console.WriteLine("Saved mode: " + original.Mode + "; rules: " + original.Sites.Count);
        var snapshots = new List<object>();
        try
        {
            snapshots.Add(await SnapshotAsync("current", original));
            if (compare)
            {
                var off = Clone(original); off.Mode = comparisonMode;
                await ConfigureAsync(off);
                snapshots.Add(await SnapshotAsync(comparisonMode.ToString(), original));
                if (browserHold)
                {
                    Console.WriteLine(comparisonMode + " ready; Enter to restore (45 second watchdog).");
                    try { await Task.Run(() => Console.ReadLine()).WaitAsync(TimeSpan.FromSeconds(45)); }
                    catch (TimeoutException) { }
                }
                await ConfigureAsync(Clone(original));
                snapshots.Add(await SnapshotAsync("restored", original));
            }
        }
        finally
        {
            if (compare)
            {
                await ConfigureAsync(Clone(original));
                // Let the open UI observe the restored mode before restoring its exact original file.
                await Task.Delay(3000);
                if (userBytes != null) File.WriteAllBytes(userFile, userBytes);
                Console.WriteLine("Original mode restored; user settings preserved.");
            }
            var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "site-diagnostics"); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "latest.json"), JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static ClientSettings Clone(ClientSettings s) => JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(s, Protocol.Json), Protocol.Json)!;
    private static async Task ConfigureAsync(ClientSettings s)
    {
        var response = await PipeProtocol.CallAsync(new("configure", s));
        if (!response.Success) throw new IOException("Service rejected diagnostic mode");
        var limit = Stopwatch.StartNew();
        while (limit.Elapsed < TimeSpan.FromSeconds(70))
        {
            var status = (await PipeProtocol.CallAsync(new("status"))).Status;
            if (status?.Mode == s.Mode && (s.Mode == RouteMode.Off ? status.Phase == ConnectionPhase.Off : status.Phase is ConnectionPhase.Connected or ConnectionPhase.Direct)) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Diagnostic mode did not settle");
    }
    private static async Task<object> SnapshotAsync(string label, ClientSettings settings)
    {
        var status = (await PipeProtocol.CallAsync(new("status"))).Status;
        Console.WriteLine(label + ": " + status?.Mode + "/" + status?.Phase);
        var runtimeFile = Path.Combine(SecureStore.ServiceDirectory, "runtime.json");
        string? localDns = null; string? bind = null;
        if (File.Exists(runtimeFile))
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(runtimeFile))!;
            var dns = root["dns"]!["servers"]!.AsArray().FirstOrDefault(s => s?["tag"]?.GetValue<string>() == "local");
            localDns = dns?["server"]?.GetValue<string>(); bind = dns?["bind_interface"]?.GetValue<string>();
        }
        var results = new List<object>();
        foreach (var target in Targets)
        {
            var matches = settings.Sites.Where(s => s.Domains.Any(d => target == d || target.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))).Select(s => s.Name).ToArray();
            IPAddress[] addresses = [];
            string? dnsError = null;
            try { addresses = await Dns.GetHostAddressesAsync(target).WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (Exception e) when (e is SocketException or TimeoutException) { dnsError = e.GetType().Name; }
            var ips = settings.Sites.Where(s => s.IpRanges.Any(cidr => addresses.Any(a => Contains(cidr, a)))).Select(s => s.Name).ToArray();
            var families = addresses.Select(a => a.AddressFamily.ToString()).Distinct().ToArray();
            var fake = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes()[0] == 198 && (a.GetAddressBytes()[1] is 18 or 19));
            var timer = Stopwatch.StartNew(); string http;
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.Zero };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            long bytesRead = 0;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var response = await client.GetAsync("https://" + target + "/", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                http = "HTTP " + (int)response.StatusCode;
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[16384];
                while (bytesRead < 4 * 1024 * 1024)
                {
                    var count = await body.ReadAsync(buffer, timeout.Token);
                    if (count == 0) break;
                    bytesRead += count;
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                http = e is HttpRequestException h ? h.HttpRequestError.ToString() : e is OperationCanceledException ? "Timeout" : "IOError";
                for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
                    http += "/" + (inner is SocketException se ? se.SocketErrorCode.ToString() : inner.GetType().Name);
            }
            var httpMs = timer.ElapsedMilliseconds;
            string? rawDns = null; string? boundDns = null;
            if (IPAddress.TryParse(localDns, out var server))
            {
                rawDns = await QueryAsync(target, server, null);
                boundDns = await QueryAsync(target, server, bind);
            }
            Console.WriteLine(target + ": " + http + " " + httpMs + "ms; bytes=" + bytesRead + "; DNS=" + (dnsError ?? string.Join(",", families)) + "; fake=" + fake + "; domains=" + string.Join(",", matches) + "; IP rules=" + string.Join(",", ips) + "; UDP DNS=" + rawDns + "/" + boundDns);
            results.Add(new { Target = target, Http = http, Ms = httpMs, BytesRead = bytesRead, DnsError = dnsError, Families = families, FakeIp = fake, DomainRules = matches, IpRules = ips, Dns = rawDns, BoundDns = boundDns });
        }
        return new { Stage = label, Mode = status?.Mode.ToString(), Phase = status?.Phase.ToString(), BindInterface = bind, Results = results };
    }
    private static bool Contains(string cidr, IPAddress address)
    {
        var parts = cidr.Split('/'); if (!IPAddress.TryParse(parts[0], out var network) || network.AddressFamily != address.AddressFamily) return false;
        var a = address.GetAddressBytes(); var b = network.GetAddressBytes(); var bits = parts.Length == 2 ? int.Parse(parts[1]) : a.Length * 8;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8) { var mask = 255 << Math.Max(0, 8 - bits); if ((a[i] & mask) != (b[i] & mask)) return false; }
        return true;
    }
    private static async Task<string> QueryAsync(string host, IPAddress server, string? bind)
    {
        var bytes = new List<byte>(new byte[12]); var id = RandomNumberGenerator.GetInt32(65536); bytes[0] = (byte)(id >> 8); bytes[1] = (byte)id; bytes[2] = 1; bytes[5] = 1;
        foreach (var label in host.Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        using var udp = new UdpClient(server.AddressFamily); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (bind != null && server.AddressFamily == AddressFamily.InterNetwork)
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == bind);
                if (adapter?.GetIPProperties().GetIPv4Properties() is { } ipv4) udp.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(ipv4.Index));
            }
            await udp.SendAsync(bytes.ToArray(), new IPEndPoint(server, 53), timeout.Token);
            var reply = await udp.ReceiveAsync(timeout.Token); var data = reply.Buffer;
            return data.Length < 12 ? "ShortReply" : "Rcode" + (data[3] & 15) + "/Answers" + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return e is SocketException s ? s.SocketErrorCode.ToString() : "Timeout"; }
    }
}
