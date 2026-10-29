using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RedRoute.Core;

internal static class NativeCoreLab
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var engine = Path.Combine(Environment.CurrentDirectory, "artifacts", "engine", "amnezia-box.exe");
        if (!File.Exists(engine)) throw new FileNotFoundException("Native integration tests require the packaged engine", engine);
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "native-lab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var clientKeys = await KeysAsync(engine); var serverKeys = await KeysAsync(engine);
        var tunnelHttpPort = 8080; await using var direct = new LoopbackHttp("DIRECT-LAB");
        var shared = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        foreach (var protocol in new[] { "wireguard", "awg-legacy", "awg-advanced" })
        {
            var serverUdp = Port(true); var clientUdp = Port(true); var serverHealth = Port();
            var awg = protocol == "wireguard" ? "" : "Jc = 4\nJmin = 40\nJmax = 70\nS1 = 16\nS2 = 24\nH1 = 101-110\nH2 = 201-210\nH3 = 301-310\nH4 = 401-410\n";
            if (protocol == "awg-advanced") awg += $"S3 = 32\nS4 = 40\nHeaderProtectionKey = {shared}\nContentPaddingAddition = 0-32\nRekeyAfterTime = 110-120\nRekeyTimeout = 4-5\nRejectAfterTime = 170-180\nKeepaliveTimeout = 9-10\nMaxHandshakeAttempts = 18-20\nRandomTrailers = on\nDisableCookies = on\n";
            string Profile(bool server) => $"[Interface]\nPrivateKey = {(server ? serverKeys.Private : clientKeys.Private)}\nAddress = 192.0.2.{(server ? 2 : 1)}/32, 2001:db8::{(server ? 2 : 1)}/128\nListenPort = {(server ? serverUdp : clientUdp)}\n{awg}[Peer]\nPublicKey = {(server ? clientKeys.Public : serverKeys.Public)}\nPresharedKey = {shared}\nEndpoint = 127.0.0.1:{(server ? clientUdp : serverUdp)}\nAllowedIPs = 0.0.0.0/0, ::/0\nPersistentKeepalive = 1\n";
            var ipc = $"private_key={Convert.ToHexString(Convert.FromBase64String(serverKeys.Private)).ToLowerInvariant()}\nlisten_port={serverUdp}\n";
            var names = new Dictionary<string, string> { ["HeaderProtectionKey"] = "header_protection_key", ["ContentPaddingAddition"] = "content_padding_addition", ["RekeyAfterTime"] = "rekey_after_time", ["RekeyTimeout"] = "rekey_timeout", ["RejectAfterTime"] = "reject_after_time", ["KeepaliveTimeout"] = "keepalive_timeout", ["MaxHandshakeAttempts"] = "max_handshake_attempts", ["RandomTrailers"] = "random_trailers", ["DisableCookies"] = "disable_cookies" };
            foreach (var line in awg.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(" = ", 2); var field = names.GetValueOrDefault(parts[0], parts[0].ToLowerInvariant());
                var value = field == "header_protection_key" ? Convert.ToHexString(Convert.FromBase64String(parts[1])).ToLowerInvariant() : parts[1];
                if (field is "random_trailers" or "disable_cookies") value = ConfParser.ParseBoolean(value, field).ToString().ToLowerInvariant();
                ipc += field + "=" + value + "\n";
            }
            ipc += $"public_key={Convert.ToHexString(Convert.FromBase64String(clientKeys.Public)).ToLowerInvariant()}\npreshared_key={Convert.ToHexString(Convert.FromBase64String(shared)).ToLowerInvariant()}\nendpoint=127.0.0.1:{clientUdp}\nallowed_ip=192.0.2.1/32\nallowed_ip=2001:db8::1/128\n";
            await using var serverProcess = await CoreProcess.StartPeerAsync(folder, protocol, ipc, tunnelHttpPort);
            foreach (var mode in new[] { RouteMode.Auto, RouteMode.On })
            foreach (var ipv6 in new[] { false, true })
            {
                var scenario = protocol + "/" + mode + (ipv6 ? "/dual-stack" : "/IPv4-only");
                var healthPort = Port(); var trafficPort = Port();
                var settings = new ClientSettings { Mode = mode, Sites = [new("Selected", ["selected.test", "selected-v6.test"], [])] };
                var profile = Profile(false);
                if (protocol == "awg-advanced") profile = profile.Replace("PersistentKeepalive = 1", "PersistentKeepalive = 1-2");
                if (!ipv6) profile = profile.Replace("192.0.2.1/32, 2001:db8::1/128", "192.0.2.1/32");
                var config = CoreConfiguration.Build(ConfParser.Parse(profile), settings, false, healthPort, "lab-password");
                var traffic = config["inbounds"]![0]!.DeepClone(); traffic["tag"] = "traffic"; traffic["listen_port"] = trafficPort; config["inbounds"]!.AsArray().Add(traffic);
                LocalDns(config);
                assert(config["inbounds"]!.AsArray().All(n => n!["type"]!.GetValue<string>() != "tun"), protocol + "/" + mode + ": integration lab never creates a system tunnel");
                await using var clientProcess = await CoreProcess.StartAsync(engine, folder, protocol + "-" + mode + (ipv6 ? "-dual" : "-v4"), config, healthPort);
                assert(clientProcess.FullyReady, scenario + ": actual engine emits readiness after full startup, independently of health port availability");
                using var proxy = new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{trafficPort}") { Credentials = new NetworkCredential("redroute", "lab-password"), BypassProxyOnLocal = false }, UseProxy = true, AllowAutoRedirect = false };
                using var http = new HttpClient(proxy) { Timeout = TimeSpan.FromSeconds(8) };
                var response = await http.GetStringAsync($"http://selected.test:{tunnelHttpPort}/probe");
                assert(response == "VPN-LAB", protocol + "/" + mode + ": selected domain traverses an actual encrypted client/server connection");
                if (ipv6)
                {
                    response = await http.GetStringAsync($"http://selected-v6.test:{tunnelHttpPort}/probe");
                    assert(response == "VPN-LAB", scenario + ": IPv6 traffic traverses the encrypted userspace tunnel");
                    using var v6Handler = new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{healthPort}") { Credentials = new NetworkCredential("redroute-ipv6", "lab-password") }, UseProxy = true };
                    using var v6Health = new HttpClient(v6Handler) { Timeout = TimeSpan.FromSeconds(3) };
                    assert(await v6Health.GetStringAsync($"http://health-v6.test:{tunnelHttpPort}/probe") == "VPN-LAB", scenario + ": independent IPv6 health authentication traverses VPN even for an unlisted domain");
                    if (mode == RouteMode.On)
                    {
                        assert(await http.GetStringAsync($"http://selected.test:{tunnelHttpPort}/disable-ipv6") == "V6-DISABLED", scenario + ": test peer fails IPv6 HTTP while profile still advertises IPv6");
                        try { await v6Health.GetStringAsync($"http://health-v6.test:{tunnelHttpPort}/probe"); throw new Exception("Broken IPv6 was reported healthy"); }
                        catch (HttpRequestException) { assert(true, scenario + ": IPv6 check fails independently of profile addresses"); }
                        assert(await http.GetStringAsync($"http://selected.test:{tunnelHttpPort}/probe") == "VPN-LAB", scenario + ": IPv4 remains healthy during an IPv6-only outage");
                        assert(await http.GetStringAsync($"http://selected.test:{tunnelHttpPort}/enable-ipv6") == "V6-ENABLED" && await v6Health.GetStringAsync($"http://health-v6.test:{tunnelHttpPort}/probe") == "VPN-LAB", scenario + ": IPv6 health detects recovery without replacing the profile");
                    }
                }
                using (var cached = new HttpRequestMessage(HttpMethod.Get, $"http://[2001:db8::2]:{tunnelHttpPort}/probe"))
                {
                    cached.Headers.Host = "selected.test";
                    using var cachedResponse = await http.SendAsync(cached);
                    assert(await cachedResponse.Content.ReadAsStringAsync() == "VPN-LAB", scenario + ": sniffed domain with a cached real AAAA reaches VPN without a physical IPv6 route");
                }
                var large = await http.GetStringAsync($"http://selected.test:{tunnelHttpPort}/large");
                assert(large == string.Concat(Enumerable.Repeat("RED_ROUTE", 16384)), protocol + "/" + mode + ": a multi-packet 144 KiB response is reassembled without corruption");
                var parallel = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => http.GetStringAsync($"http://selected.test:{tunnelHttpPort}/probe")));
                assert(parallel.All(body => body == "VPN-LAB"), protocol + "/" + mode + ": ten parallel encrypted requests complete correctly");
                response = await http.GetStringAsync($"http://127.0.0.1:{direct.Port}/probe");
                assert(response == "DIRECT-LAB", protocol + "/" + mode + ": local network remains direct");
                if (mode == RouteMode.Auto)
                {
                    response = await http.GetStringAsync($"http://direct.test:{direct.Port}/probe");
                    assert(response == "DIRECT-LAB", protocol + ": unselected domain uses direct routing in Auto");
                    using var cachedDirect = new HttpRequestMessage(HttpMethod.Get, $"http://[2001:db8:100::10]:{direct.Port}/probe");
                    cachedDirect.Headers.Host = "direct.test";
                    using var directResponse = await http.SendAsync(cachedDirect);
                    assert(await directResponse.Content.ReadAsStringAsync() == "DIRECT-LAB", scenario + ": unselected cached AAAA resolves locally to IPv4 and never enters VPN");
                }
                // SOCKS authentication is a real boundary: unauthenticated local callers cannot borrow the tunnel.
                using var unauthorizedHandler = new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{healthPort}"), UseProxy = true };
                using var unauthorized = new HttpClient(unauthorizedHandler) { Timeout = TimeSpan.FromSeconds(2) };
                try { await unauthorized.GetStringAsync($"http://192.0.2.2:{tunnelHttpPort}/probe"); throw new Exception("Unauthenticated health proxy accepted"); }
                catch (HttpRequestException) { assert(true, protocol + "/" + mode + ": health proxy refuses unauthenticated clients"); }
            }
        }
    }
    private static void LocalDns(JsonObject config)
    {
        // Both peers are on loopback, so binding to the machine's physical default adapter is inappropriate.
        config["route"]!["auto_detect_interface"] = false;
        foreach (var tag in new[] { "local", "remote" })
        {
            var servers = config["dns"]!["servers"]!.AsArray();
            for (var i = 0; i < servers.Count; i++)
                if (servers[i]!["tag"]!.GetValue<string>() == tag)
                    servers[i] = new JsonObject { ["type"] = "hosts", ["tag"] = tag, ["predefined"] = new JsonObject { ["selected.test"] = "192.0.2.2", ["selected-v6.test"] = "2001:db8::2", ["health-v6.test"] = "2001:db8::2", ["direct.test"] = "127.0.0.1" } };
        }
    }
    private static int Port(bool udp = false)
    {
        if (udp) { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static async Task<(string Private, string Public)> KeysAsync(string engine)
    {
        var info = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("generate"); info.ArgumentList.Add("wg-keypair");
        using var child = Process.Start(info)!; var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (child.ExitCode != 0) throw new IOException("Synthetic test key generation failed");
        var keys = (await output).Split('\n').Where(l => l.Contains(": ")).Select(l => l.Trim().Split(": ", 2)).ToDictionary(p => p[0], p => p[1]);
        _ = await errors; return (keys["PrivateKey"], keys["PublicKey"]);
    }
    private sealed class CoreProcess(Process child, Task<string> output, Task<string> errors, string logFile, EngineReadiness? readiness = null) : IAsyncDisposable
    {
        public bool FullyReady => readiness?.IsReady == true;
        private Task<string> Errors => errors;
        public static async Task<CoreProcess> StartPeerAsync(string folder, string name, string ipc, int httpPort)
        {
            var file = Path.Combine(folder, name + "-server.json");
            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(new { IPC = ipc, Port = httpPort }));
            var info = new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "redroute-lab-server.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(file); var child = Process.Start(info)!;
            try
            {
                var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (ready != "READY") throw new IOException("Userspace server did not initialize");
                return new CoreProcess(child, child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync(), Path.Combine(folder, name + "-server.log"));
            }
            catch { if (!child.HasExited) child.Kill(true); child.Dispose(); throw; }
        }
        public static async Task<CoreProcess> StartAsync(string executable, string folder, string name, JsonObject config, int port)
        {
            var file = Path.Combine(folder, name + ".json"); await File.WriteAllTextAsync(file, config.ToJsonString());
            config["log"]!["level"] = "debug"; await File.WriteAllTextAsync(file, config.ToJsonString());
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            info.ArgumentList.Add("run"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(file);
            var process = Process.Start(info)!;
            var ready = new EngineReadiness();
            async Task<string> DrainOutput()
            {
                var text = new StringBuilder();
                while (await process.StandardOutput.ReadLineAsync() is { } line) { ready.Observe(line); text.AppendLine(line); }
                return text.ToString();
            }
            var instance = new CoreProcess(process, DrainOutput(), process.StandardError.ReadToEndAsync(), Path.Combine(folder, name + ".log"), ready);
            try
            {
                for (var i = 0; i < 150; i++)
                {
                    if (process.HasExited) throw new IOException("Native lab startup failed: " + (await instance.Errors)[..Math.Min(1200, (await instance.Errors).Length)]);
                    if (!ready.IsReady) { await Task.Delay(100); continue; }
                    using var tcp = new TcpClient();
                    try { await tcp.ConnectAsync(IPAddress.Loopback, port); return instance; } catch (SocketException) { }
                    await Task.Delay(50);
                }
                throw new TimeoutException("Native lab startup timeout");
            }
            catch { await instance.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!child.HasExited)
                {
                    try { await child.StandardInput.WriteLineAsync("RED_ROUTE_STOP"); await child.StandardInput.FlushAsync(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { if (!child.HasExited) child.Kill(true); }
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                await File.WriteAllTextAsync(logFile, await output + await errors);
            }
            finally { child.Dispose(); }
        }
    }
    private sealed class LoopbackHttp : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task work;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public LoopbackHttp(string body)
        {
            listener.Start();
            work = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token); using var stream = client.GetStream();
                        var buffer = new byte[8192]; var count = 0;
                        while (count < buffer.Length)
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(count), stop.Token); if (n == 0) break; count += n;
                            if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                        }
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), stop.Token);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
            });
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await work; stop.Dispose(); }
    }
}
