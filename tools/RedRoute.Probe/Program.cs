using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RedRoute.Core;
using RedRoute.Platform;

if (args.Contains("--routing-lab")) { await RoutingLab.RunAsync(); return; }
if (args.Contains("--routing-audit"))
{
    var saved = SecureStore.Load(); var original = saved.Mode;
    var snapshots = new Dictionary<RouteMode, Dictionary<string, string>>();
    try
    {
        foreach (var mode in new[] { RouteMode.Off, RouteMode.Auto, RouteMode.On })
        {
            saved.Mode = mode;
            if (!(await PipeProtocol.CallAsync(new("configure", saved))).Success) throw new IOException("Configure failed");
            var ready = false;
            for (var i = 0; i < 60; i++)
            {
                var s = (await PipeProtocol.CallAsync(new("status"))).Status;
                if (s?.Mode == mode && (mode == RouteMode.Off ? s.Phase == ConnectionPhase.Off : s.Phase == ConnectionPhase.Connected)) { ready = true; break; }
                await Task.Delay(1000);
            }
            if (!ready) throw new IOException("Requested mode did not become ready; audit aborted");
            snapshots[mode] = new();
            foreach (var url in new[] { "https://2ip.ru/", "https://api.ipify.org", "https://www.cloudflare.com/cdn-cgi/trace" })
            {
                using var handler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
                var clock = Stopwatch.StartNew();
                try
                {
                    var body = await client.GetStringAsync(url);
                    var ips = System.Text.RegularExpressions.Regex.Matches(body, @"\b(?:\d{1,3}\.){3}\d{1,3}\b").Select(m => m.Value).Where(s => IPAddress.TryParse(s, out _)).Distinct().ToArray();
                    snapshots[mode][url] = string.Join(",", ips);
                    Console.WriteLine($"{mode}: {new Uri(url).Host}: {clock.ElapsedMilliseconds} ms; address count={ips.Length}");
                    if (mode != RouteMode.Off && snapshots[RouteMode.Off].TryGetValue(url, out var baseline)) Console.WriteLine("Same public address as Off: " + (baseline == snapshots[mode][url]));
                }
                catch (HttpRequestException) { Console.WriteLine($"{mode}: {new Uri(url).Host}: HTTP failure after {clock.ElapsedMilliseconds} ms"); }
                catch (OperationCanceledException) { Console.WriteLine($"{mode}: {new Uri(url).Host}: timeout"); }
            }
        }
        foreach (var url in snapshots[RouteMode.Auto].Keys)
            if (snapshots[RouteMode.On].TryGetValue(url, out var full)) Console.WriteLine($"Auto equals full VPN for {new Uri(url).Host}: {snapshots[RouteMode.Auto][url] == full}");
    }
    finally { saved.Mode = original; await PipeProtocol.CallAsync(new("configure", saved)); Console.WriteLine("Restored mode: " + original); }
    return;
}
if (args.Contains("--inspect"))
{
    var saved = SecureStore.Load();
    var current = (await PipeProtocol.CallAsync(new("status"))).Status;
    Console.WriteLine($"Saved mode: {saved.Mode}; service mode: {current?.Mode}; phase: {current?.Phase}");
    foreach (var site in saved.Sites)
        Console.WriteLine($"Rule: {site.Name}; domains={site.Domains.Length}; networks={site.IpRanges.Length}; matches 2ip.ru={site.Domains.Any(d => d == "2ip.ru" || "2ip.ru".EndsWith("." + d))}");
    Console.WriteLine("System HTTP proxy enabled: " + (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxyEnable", 0)));
    return;
}
// Explicit diagnostic: never creates a TUN or changes system routes. Does not print configuration or keys.
bool realProfile = args.Contains("--saved-profile") || args.Contains("--installed-check") || args.Contains("--installed-auto-check");
Console.WriteLine(args.Contains("--installed-check") || args.Contains("--installed-auto-check") ? "Installed tunnel check; original mode is restored in finally." : "Diagnostic mode: no TUN, no route changes.");
var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var settings = realProfile ? SecureStore.Load() : new ClientSettings();
Console.WriteLine("Settings loaded; profiles: " + settings.Profiles.Count);
if (args.Contains("--installed-check") || args.Contains("--installed-auto-check"))
{
    var originalMode = settings.Mode;
    var auto = args.Contains("--installed-auto-check");
    var temporaryRule = new SiteRule("Temporary diagnostic", ["api.ipify.org"], []);
    using var directHandler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
    using var directClient = new HttpClient(directHandler) { Timeout = TimeSpan.FromSeconds(15) };
    var before = (await directClient.GetStringAsync("https://api.ipify.org")).Trim();
    var directTrace = auto ? (await directClient.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace")).Split('\n').FirstOrDefault(l => l.StartsWith("ip=")) : null;
    try
    {
        settings.Mode = auto ? RouteMode.Auto : RouteMode.On;
        if (auto) settings.Sites.Add(temporaryRule);
        var applied = await PipeProtocol.CallAsync(new("configure", settings));
        if (!applied.Success) throw new IOException("Configuration was not accepted");
        EngineStatus? active = null;
        for (var i = 0; i < 45; i++)
        {
            await Task.Delay(1000);
            active = (await PipeProtocol.CallAsync(new("status"))).Status;
            if (active?.Phase == ConnectionPhase.Connected) break;
        }
        if (active?.Phase != ConnectionPhase.Connected) throw new IOException("Full tunnel did not become healthy");
        Console.WriteLine("Installed service: Connected; VPN health checks passed.");
        using var response = await directClient.GetAsync("https://cp.cloudflare.com/generate_204");
        Console.WriteLine("HTTPS through system routes: " + (int)response.StatusCode);
        var after = (await directClient.GetStringAsync("https://api.ipify.org")).Trim();
        Console.WriteLine("Public egress changed through VPN: " + !before.Equals(after, StringComparison.Ordinal));
        if (auto)
        {
            var afterTrace = (await directClient.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace")).Split('\n').FirstOrDefault(l => l.StartsWith("ip="));
            Console.WriteLine("Unlisted destination stayed direct: " + (directTrace == afterTrace));
        }
        await Task.Delay(12000);
        var monitored = (await PipeProtocol.CallAsync(new("status"))).Status;
        Console.WriteLine("Monitoring after 12 seconds: " + monitored?.Phase);
    }
    finally
    {
        settings.Mode = originalMode;
        if (auto) settings.Sites.Remove(temporaryRule);
        await PipeProtocol.CallAsync(new("configure", settings));
        for (var i = 0; i < 10; i++) { await Task.Delay(500); if ((await PipeProtocol.CallAsync(new("status"))).Status?.Mode == originalMode) break; }
        Console.WriteLine("Original mode restored: " + originalMode);
    }
    return;
}
var profile = realProfile ? ConfParser.Parse(settings.Profiles[0].Config) : ConfParser.Parse($"[Interface]\nPrivateKey={key}\nAddress=10.8.0.2/32\nJc=4\n[Peer]\nPublicKey={key}\nEndpoint=192.0.2.1:51820\nAllowedIPs=0.0.0.0/0");
if (realProfile && !IPAddress.TryParse(profile.Endpoint.Host, out _))
{
    var endpoint = profile.Endpoint;
    var address = (await Dns.GetHostAddressesAsync(endpoint.Host)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
    profile.Peer["Endpoint"] = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{endpoint.Port}" : $"{address}:{endpoint.Port}";
    Console.WriteLine("Server hostname resolved (address hidden).");
}
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
settings.Mode = RouteMode.Auto;
var json = CoreConfiguration.Build(profile, settings, false, port, password);
json["log"]!["level"] = "debug";
Console.WriteLine("Probe configuration created.");
if (!realProfile) { json["route"]!["rules"]![0]!["server"] = "local"; json["route"]!["rules"]![1]!["outbound"] = "direct"; }
var folder = Path.Combine(SecureStore.UserDirectory, "diagnostic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "runtime.json"); await File.WriteAllTextAsync(path, json.ToJsonString());
var info = new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory, "artifacts", "engine", "amnezia-box.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var arg in new[] { "run", "-c", path }) info.ArgumentList.Add(arg);
var logs = new List<string>();
using var process = Process.Start(info)!;
Console.WriteLine("Probe engine started.");
process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (logs) logs.Add(e.Data); };
process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (logs) logs.Add(e.Data); };
process.BeginOutputReadLine(); process.BeginErrorReadLine();
try
{
    for (int i = 0; i < 50; i++)
    {
        if (process.HasExited)
        {
            Console.WriteLine("Engine exited at startup: " + process.ExitCode);
            await Task.Delay(100);
            lock (logs) foreach (var line in logs.TakeLast(5))
            {
                var safe = line;
                foreach (var secret in profile.Interface.Where(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).Concat(profile.Peer.Where(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value))) safe = safe.Replace(secret, "[key]");
                safe = safe.Replace(profile.Endpoint.Host, "[server]");
                Console.WriteLine(safe);
            }
            return;
        }
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(300)); break; }
        catch (Exception ex) when (ex is SocketException or TimeoutException) { await Task.Delay(100); }
    }
    using (var tcp = new TcpClient())
    {
        await tcp.ConnectAsync(IPAddress.Loopback, port); using var stream = tcp.GetStream();
        using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, handshakeTimeout.Token); var reply = new byte[2]; await stream.ReadExactlyAsync(reply, handshakeTimeout.Token);
        Console.WriteLine("SOCKS auth-method: " + Convert.ToHexString(reply));
        var auth = new List<byte> { 1, 8 }; auth.AddRange("redroute"u8.ToArray()); auth.Add((byte)password.Length); auth.AddRange(Encoding.UTF8.GetBytes(password));
        await stream.WriteAsync(auth.ToArray(), handshakeTimeout.Token); await stream.ReadExactlyAsync(reply, handshakeTimeout.Token); Console.WriteLine("SOCKS auth-result: " + Convert.ToHexString(reply));
    }
    foreach (var scheme in new[] { "socks5", "http" })
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"{scheme}://127.0.0.1:{port}") { Credentials = new NetworkCredential("redroute", password) }, UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        var timer = Stopwatch.StartNew();
        try { using var response = await client.GetAsync("https://cp.cloudflare.com/generate_204"); Console.WriteLine($"{scheme}: HTTP {(int)response.StatusCode}, {timer.ElapsedMilliseconds}ms"); }
        catch (Exception ex) { Console.WriteLine($"{scheme}: {ex.GetType().Name}, {timer.ElapsedMilliseconds}ms"); for (var e = ex; e != null; e = e.InnerException) { var message = e.Message.Replace(password, "[redacted]"); foreach (var secret in profile.Interface.Values.Concat(profile.Peer.Where(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value)).Where(s => s.Length > 15)) message = message.Replace(secret, "[redacted]"); Console.WriteLine(message); } }
    }
    // Log only known diagnostic classifications; never print raw engine output from a real profile.
    lock (logs) foreach (var term in new[] { "authentication", "invalid", "handshake", "connection refused", "dns", "network unreachable", "no route", "deadline", "not supported" })
        if (logs.Any(l => l.Contains(term, StringComparison.OrdinalIgnoreCase))) Console.WriteLine("Engine diagnostic category: " + term);
    await Task.Delay(100);
    lock (logs) foreach (var line in logs.TakeLast(12))
    {
        var safe = line;
        foreach (var secret in profile.Interface.Where(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).Concat(profile.Peer.Where(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value))) safe = safe.Replace(secret, "[key]");
        safe = safe.Replace(profile.Endpoint.Host, "[server]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\b\d{1,3}(\.\d{1,3}){3}\b", "[ip]");
        Console.WriteLine(safe);
    }
}
finally
{
    if (!process.HasExited) { await process.StandardInput.WriteLineAsync("RED_ROUTE_STOP"); await process.StandardInput.FlushAsync(); try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); } catch (TimeoutException) { process.Kill(true); } }
    File.Delete(path); Directory.Delete(folder);
}
