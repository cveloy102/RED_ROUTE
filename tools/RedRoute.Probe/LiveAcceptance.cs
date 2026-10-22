using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedRoute.Core;
using RedRoute.Platform;

internal static class LiveAcceptance
{
    public static async Task RunAsync(bool dnsOnly = false)
    {
        var original = SecureStore.Load(true);
        if (original.Profiles.Count == 0) throw new IOException("A real saved profile is required");
        var results = new List<object>(); var clock = Stopwatch.StartNew();
        var userBytes = File.ReadAllBytes(Path.Combine(SecureStore.UserDirectory, "settings.dpapi"));
        var good = original.Profiles[0] with { Id = Guid.NewGuid(), Name = "Acceptance-good" };
        var bad = good with { Id = Guid.NewGuid(), Name = "Acceptance-unreachable", Config = Regex.Replace(good.Config, @"(?im)^\s*Endpoint\s*=.*$", "Endpoint = 192.0.2.1:9") };
        ClientSettings Config(RouteMode mode, params VpnProfile[] profiles) => new() { Mode = mode, Profiles = profiles.ToList(), Sites = [], AutoFailover = true };
        void Pass(bool condition, string name)
        {
            results.Add(new { Name = name, Passed = condition, Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2) });
            Console.WriteLine((condition ? "PASS: " : "FAIL: ") + name);
            if (!condition) throw new IOException("Acceptance failed: " + name);
        }
        async Task<EngineStatus> Status() => (await PipeProtocol.CallAsync(new("status"))).Status ?? throw new IOException("No status");
        async Task<EngineStatus> Wait(Func<EngineStatus, bool> predicate, int seconds = 60)
        {
            var limit = Stopwatch.StartNew();
            while (limit.Elapsed.TotalSeconds < seconds)
            {
                try { var status = await Status(); if (predicate(status)) return status; }
                catch (OperationCanceledException) { }
                await Task.Delay(200);
            }
            throw new System.TimeoutException("Expected service state was not reached");
        }
        async Task<EngineStatus> Configure(ClientSettings value, ConnectionPhase phase = ConnectionPhase.Connected)
        {
            var before = (await Status()).Events.LastOrDefault()?.Time ?? DateTimeOffset.MinValue;
            if (!(await PipeProtocol.CallAsync(new("configure", value))).Success) throw new IOException("Configure refused");
            return await Wait(s => s.Mode == value.Mode && s.Phase == phase && s.Events.LastOrDefault()?.Time > before);
        }
        async Task<string> Fetch(string url, IPAddress? pinned = null)
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
            if (pinned != null) handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(pinned, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            return (await client.GetStringAsync(url)).Trim();
        }
        bool NoTunnel() => !NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == "RED_ROUTE" && n.OperationalStatus == OperationalStatus.Up);
        async Task<IPAddress> SelectedDnsAddress(string domain)
        {
            var query = new List<byte> { 0x52, 0x52, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var part in domain.Split('.')) { query.Add((byte)part.Length); query.AddRange(System.Text.Encoding.ASCII.GetBytes(part)); }
            query.AddRange([0, 0, 1, 0, 1]);
            using var udp = new UdpClient(AddressFamily.InterNetwork); udp.Connect("172.30.255.2", 53);
            await udp.SendAsync(query.ToArray()); var response = (await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer;
            int position = 12;
            void SkipName() { while (response[position] != 0) { if ((response[position] & 0xc0) == 0xc0) { position += 2; return; } position += response[position] + 1; } position++; }
            SkipName(); position += 4;
            var answers = (response[6] << 8) | response[7];
            for (var i = 0; i < answers; i++)
            {
                SkipName(); var type = (response[position] << 8) | response[position + 1]; var length = (response[position + 8] << 8) | response[position + 9]; position += 10;
                if (type == 1 && length == 4) return new IPAddress(response.AsSpan(position, 4));
                position += length;
            }
            throw new IOException("Test DNS response has no A answer");
        }
        async Task<string> VpnEgress()
        {
            // Read only the private health listener credentials; never print runtime/profile data.
            using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(SecureStore.ServiceDirectory, "runtime.json")));
            var health = runtime.RootElement.GetProperty("inbounds").EnumerateArray().First(i => i.GetProperty("tag").GetString() == "health");
            var user = health.GetProperty("users")[0];
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + health.GetProperty("listen_port").GetInt32()) { Credentials = new NetworkCredential(user.GetProperty("username").GetString(), user.GetProperty("password").GetString()) }, UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            return (await client.GetStringAsync("https://api.ipify.org")).Trim();
        }
        Process[] Installed(string name, string relativePath) => Process.GetProcessesByName(name).Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RED_ROUTE", relativePath), StringComparison.OrdinalIgnoreCase); }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }).ToArray();
        try
        {
            await Configure(Config(RouteMode.Off, good), ConnectionPhase.Off);
            var baseline = await Fetch("https://api.ipify.org");
            var directTrace = (await Fetch("https://www.cloudflare.com/cdn-cgi/trace")).Split('\n').First(s => s.StartsWith("ip="));
            Pass(NoTunnel(), "Off: no active RED_ROUTE tunnel");
            await Configure(Config(RouteMode.On, good));
            var vpn = await Fetch("https://api.ipify.org");
            Pass(!NoTunnel() && vpn == await VpnEgress(), "On: system egress equals independently authenticated VPN health-proxy egress");
            Console.WriteLine("Direct baseline differs from VPN egress: " + (baseline != vpn) + ". Equal addresses can indicate upstream router VPN routing.");
            if (dnsOnly)
            {
                var cachedConfig = Config(RouteMode.Auto, good); cachedConfig.Sites.Add(new("Acceptance-cached", ["api.ipify.org", "example.org"], [])); await Configure(cachedConfig);
                var cached = await SelectedDnsAddress("api.ipify.org");
                Pass(await Fetch("https://api.ipify.org", cached) == vpn, "Cached DNS address works before final-build crash");
                var crashedAt = DateTimeOffset.UtcNow;
                var children = Installed("amnezia-box", @"service\engine\amnezia-box.exe"); Pass(children.Length == 1, "Final-build crash targets only installed RED_ROUTE core");
                children[0].Kill(); children[0].WaitForExit(5000); foreach (var child in children) child.Dispose();
                await Wait(s => s.Phase == ConnectionPhase.Connected && s.Events.Any(e => e.Code == "connected" && e.Time > crashedAt), 35);
                Pass(await SelectedDnsAddress("www.example.org") != cached, "Final build preserves allocation cursor after crash");
                Pass(await Fetch("https://api.ipify.org", cached) == vpn, "Final build preserves previously cached DNS address after crash");
                return;
            }
            var empty = Config(RouteMode.Auto, good); await Configure(empty);
            Pass(await Fetch("https://api.ipify.org") == baseline, "Auto empty list: internet stays direct");
            var selected = Config(RouteMode.Auto, good); selected.Sites.Add(new("Acceptance-domain", ["api.ipify.org"], [])); await Configure(selected);
            Pass(await Fetch("https://api.ipify.org") == vpn, "Auto domain: selected destination uses VPN");
            Pass((await Fetch("https://www.cloudflare.com/cdn-cgi/trace")).Split('\n').First(s => s.StartsWith("ip=")) == directTrace, "Auto domain: unlisted destination stays direct");
            var cores = Installed("amnezia-box", @"service\engine\amnezia-box.exe"); Pass(cores.Length == 1, "Exactly one installed VPN core captures traffic"); var corePid = cores[0].Id; foreach (var p in cores) p.Dispose();
            selected.Profiles[0] = good with { Name = "Acceptance-renamed" }; selected.Profiles.Add(good with { Id = Guid.NewGuid(), Name = "Acceptance-reserve" }); selected.AutoFailover = false;
            await Configure(selected);
            cores = Installed("amnezia-box", @"service\engine\amnezia-box.exe"); Pass(cores.Length == 1 && cores[0].Id == corePid, "Rename/add reserve/toggle failover: core PID unchanged"); foreach (var p in cores) p.Dispose();
            await Configure(Config(RouteMode.Off, good), ConnectionPhase.Off);
            var realIp = (await Dns.GetHostAddressesAsync("api.ipify.org")).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var ipRule = Config(RouteMode.Auto, good); ipRule.Sites.Add(new("Acceptance-IP", [], [realIp + "/32"])); await Configure(ipRule);
            Pass(await Fetch("https://api.ipify.org", realIp) == vpn, "Auto IP rule: selected IP uses VPN with correct TLS hostname");
            var fallback = Config(RouteMode.Auto, bad, good); await Configure(fallback);
            Pass((await Status()).Profile == good.Name, "Unavailable primary: real connection falls through to reserve");
            await Configure(Config(RouteMode.Off, good), ConnectionPhase.Off);
            var noFallback = Config(RouteMode.Auto, bad, good); noFallback.AutoFailover = false; await Configure(noFallback, ConnectionPhase.Direct);
            Pass(NoTunnel() && await Fetch("https://api.ipify.org") == baseline, "Failover disabled: failed primary leaves internet direct");
            await Configure(Config(RouteMode.On, bad), ConnectionPhase.Direct);
            Pass(NoTunnel() && await Fetch("https://api.ipify.org") == baseline, "All profiles unavailable: no tunnel and direct internet");
            var crashConfig = Config(RouteMode.Auto, good); crashConfig.Sites.Add(new("Acceptance-cached-domain", ["api.ipify.org", "example.org"], []));
            await Configure(crashConfig);
            Pass((await Status()).Phase == ConnectionPhase.Connected, "Connection recovers after replacing unavailable profile");
            var cachedAddress = await SelectedDnsAddress("api.ipify.org");
            Pass(await Fetch("https://api.ipify.org", cachedAddress) == vpn, "Selected DNS answer works before core crash");
            var failureTime = DateTimeOffset.UtcNow;
            cores = Installed("amnezia-box", @"service\engine\amnezia-box.exe"); Pass(cores.Length == 1, "Core crash test targets only installed RED_ROUTE core");
            cores[0].Kill(); cores[0].WaitForExit(5000); foreach (var p in cores) p.Dispose();
            await Wait(s => s.Phase == ConnectionPhase.Connected && s.Events.Any(e => e.Code == "connected" && e.Time > failureTime), 35);
            Pass((DateTimeOffset.UtcNow - failureTime).TotalSeconds < 30, "Core crash: monitor reconnects without three failed cycles");
            Pass(await SelectedDnsAddress("www.example.org") != cachedAddress, "New DNS allocation after crash cannot reuse previously delivered address");
            Pass(await Fetch("https://api.ipify.org", cachedAddress) == vpn, "Previously cached DNS answer remains usable after core crash");
            // Exercise cancellations while a bad candidate handshake is still pending.
            for (var i = 0; i < 8; i++)
            {
                var next = i % 2 == 0 ? Config(RouteMode.On, bad) : Config(RouteMode.Off, good);
                if (!(await PipeProtocol.CallAsync(new("configure", next))).Success) throw new IOException("Rapid command refused");
            }
            await Wait(s => s.Mode == RouteMode.Off && s.Phase == ConnectionPhase.Off, 15);
            Pass(NoTunnel(), "Rapid On/Off: final Off cancels candidate and releases capture");
            await Configure(Config(RouteMode.On, good));
            await PipeProtocol.CallAsync(new("recover")); await Wait(s => s.Mode == RouteMode.Off && s.Phase == ConnectionPhase.Off);
            Pass(NoTunnel() && await Fetch("https://api.ipify.org") == baseline, "Recovery command: routes released and direct internet restored");
            var recoveryEvent = (await Status()).Events.Last().Time;
            var staleUpdate = Config(RouteMode.On, bad); staleUpdate.Sites.Add(new("Background", ["background.example.org"], []));
            if (!(await PipeProtocol.CallAsync(new("update-rules", staleUpdate))).Success) throw new IOException("Background update refused");
            await Wait(s => s.Events.LastOrDefault()?.Time > recoveryEvent && s.Phase == ConnectionPhase.Off);
            Pass(NoTunnel() && (await Status()).Mode == RouteMode.Off && SecureStore.Load(true).Profiles[0].Id == good.Id, "Stale background update after recovery cannot enable VPN or replace profiles");
            await Configure(Config(RouteMode.On, good));
            var services = Installed("RED_ROUTE.Service", @"service\RED_ROUTE.Service.exe"); Pass(services.Length == 1, "Service crash test targets only installed RED_ROUTE service");
            services[0].Kill(); services[0].WaitForExit(5000); foreach (var p in services) p.Dispose();
            await Task.Delay(1000);
            Pass(NoTunnel() && await Fetch("https://api.ipify.org") == baseline, "Service crash: child job releases tunnel and internet remains direct");
            await Wait(s => s.Mode == RouteMode.On && s.Phase == ConnectionPhase.Connected);
            Pass((await Status()).Events.Count(e => e.Code == "connected") > 1, "Automatic service restart: saved mode reconnects and previous event history survives");
            Pass(File.ReadAllBytes(Path.Combine(SecureStore.UserDirectory, "settings.dpapi")).SequenceEqual(userBytes), "User profiles/rules/settings were never modified by tests");
        }
        finally
        {
            using var service = new ServiceController(Protocol.ServiceName); service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            await Wait(_ => true, 20);
            await Configure(original, original.Mode == RouteMode.Off ? ConnectionPhase.Off : ConnectionPhase.Connected);
            Console.WriteLine("Original service settings restored: " + original.Mode);
            var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", dnsOnly ? "cached-dns-regression.json" : "live-acceptance.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
