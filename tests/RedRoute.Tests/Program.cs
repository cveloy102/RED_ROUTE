using System.Diagnostics;
using System.Text.Json.Nodes;
using RedRoute.Core;
using RedRoute.Platform;

var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
var config = $"[Interface]\nPrivateKey = {key}\nAddress = 10.8.0.2/32\nJc = 4\nJmin = 40\nJmax = 70\nS1 = 1\nS2 = 2\nH1 = 5\nH2 = 6\nH3 = 7\nH4 = 8\n[Peer]\nPublicKey = {key}\nEndpoint = 192.0.2.1:51820\nAllowedIPs = 0.0.0.0/0, ::/0\nPersistentKeepalive = 25\n";
int passed = 0;
void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception ex) when (ex is FormatException or ArgumentException or IOException) { passed++; Console.WriteLine("PASS: " + name); return; } throw new Exception("FAIL: " + name); }
Assert(Domains.Normalize("https://YouTube.COM/watch?v=x") == "youtube.com", "URL normalization");
Reject(() => new ClientSettings { Profiles = null! }.Validate(), "Reject null IPC profile list");
Reject(() => new ClientSettings { Sites = [new("bad", null!, [])] }.Validate(), "Reject null IPC domain list");
Assert(new ServiceCatalog().Find("youtube.com")?.Domains.Contains("googlevideo.com") == true, "Bundled YouTube service includes playback domains");
Assert(Domains.Normalize("пример.рф").StartsWith("xn--"), "IDN normalization");
Reject(() => Domains.Normalize("localhost"), "Reject single-label hostname");
Reject(() => Domains.Normalize("https://user:secret@example.org"), "Reject URL credentials");
Reject(() => Domains.Normalize("127.0.0.1"), "IP must be an explicit IP rule");
Reject(() => Domains.NormalizeCidr("1.2.3.4/33"), "Reject invalid prefix");
Assert(Domains.NormalizeCidr("2001:db8::1") == "2001:db8::1/128", "IPv6 CIDR");
var parsed = ConfParser.Parse(config);
Assert(parsed.IsAmnezia && parsed.Endpoint.Port == 51820, "Parse AWG profile");
Reject(() => ConfParser.Parse(config.Replace("Jc = 4", "PostUp = powershell bad")), "Never execute conf hooks");
Reject(() => ConfParser.Parse(config + "[Peer]\nPublicKey=x"), "Reject multiple peers explicitly");
Reject(() => ConfParser.Parse(config.Replace(key, "not-a-key")), "Reject malformed keys");
Assert(ConfParser.Parse(config.Replace("192.0.2.1:51820", "[2001:db8::1]:51820")).Endpoint.Host == "2001:db8::1", "Parse IPv6 endpoint");
var settings = new ClientSettings { Mode = RouteMode.Auto, Profiles = [new(Guid.NewGuid(), "Основной", config), new(Guid.NewGuid(), "Резерв", config)], Sites = [new("Example", ["example.org"], [])] };
var generated = CoreConfiguration.Build(parsed, settings, true, 12345, "test-password");
Assert(generated["route"]!["final"]!.GetValue<string>() == "direct", "Auto uses direct default");
Assert(generated["endpoints"]![0]!["useIntegratedTun"]!.GetValue<bool>() == false, "AWG probe never installs a second system tunnel");
Assert(generated["route"]!["rules"]![0]!["outbound"]!.GetValue<string>() == "vpn", "Health proxy always traverses VPN");
Assert(generated["inbounds"]![1]!["strict_route"]!.GetValue<bool>() == false, "No kill switch");
Assert(CoreConfiguration.Build(parsed, settings, false, 12345, "secret")["inbounds"]!.AsArray().Count == 1, "Candidate check installs no capture routes");
settings.Mode = RouteMode.On;
Assert(CoreConfiguration.Build(parsed, settings, true, 12345, "secret")["route"]!["final"]!.GetValue<string>() == "vpn", "On uses VPN default");
settings.Mode = RouteMode.Auto;
var engine = new FakeEngine();
var controller = new FailoverController(engine);
engine.Checks.Enqueue(false); engine.Checks.Enqueue(true); engine.Checks.Enqueue(true);
await controller.ConfigureAsync(settings, default);
Assert(controller.Status.Phase == ConnectionPhase.Connected && controller.Status.Profile == "Резерв", "Unavailable primary falls through to reserve");
Assert(engine.Starts.SequenceEqual(new[] { "Основной:probe", "Резерв:probe", "Резерв:capture" }), "Only a proven candidate captures traffic");
engine.Checks.Enqueue(false); engine.Checks.Enqueue(false); engine.Checks.Enqueue(false);
engine.Checks.Enqueue(true); engine.Checks.Enqueue(true);
await controller.TickAsync(default); await controller.TickAsync(default); await controller.TickAsync(default);
Assert(controller.Status.Profile == "Основной" && controller.Status.Events.Any(e => e.Code == "fail_open"), "Three failures release routes before next profile");
await controller.RecoverAsync(default);
Assert(!engine.Running && controller.Status.Mode == RouteMode.Off, "Recovery stops capture and stays Off");
var unavailable = new FakeEngine { DefaultCheck = false }; var unavailableController = new FailoverController(unavailable);
await unavailableController.ConfigureAsync(settings, default);
Assert(!unavailable.Running && unavailableController.Status.Phase == ConnectionPhase.Direct, "All unavailable means direct internet");
using var framed = new MemoryStream(); await PipeProtocol.WriteAsync(framed, new ServiceRequest("status"), default); framed.Position = 0;
Assert((await PipeProtocol.ReadAsync<ServiceRequest>(framed, default)).Command == "status", "IPC framing round trip");
using var oversized = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
try { await PipeProtocol.ReadAsync<ServiceRequest>(oversized, default); throw new Exception("Oversize accepted"); } catch (IOException) { passed++; Console.WriteLine("PASS: Reject oversized IPC before allocation"); }
var destination = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks"); Directory.CreateDirectory(destination);
foreach (var (name, awg, mode, capture) in new[] { ("awg-auto", true, RouteMode.Auto, true), ("awg-probe", true, RouteMode.Auto, false), ("awg-on", true, RouteMode.On, true), ("wg-auto", false, RouteMode.Auto, true) })
{
    settings.Mode = mode;
    var profile = awg ? parsed : ConfParser.Parse(config.Split('\n').Where(l => !ConfParser.AwgFields.Any(k => l.StartsWith(k + " ="))).Aggregate("", (a, b) => a + b + "\n"));
    var path = Path.Combine(destination, name + ".json"); await File.WriteAllTextAsync(path, CoreConfiguration.Build(profile, settings, capture, 12345, "synthetic-test-only").ToJsonString());
    var executable = Path.Combine(Environment.CurrentDirectory, "artifacts", "engine", "amnezia-box.exe");
    if (File.Exists(executable))
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "check", "-c", path }) info.ArgumentList.Add(a);
        using var child = Process.Start(info)!; var error = await child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync();
        Assert(child.ExitCode == 0, "Official core accepts " + name + ": " + error);
    }
}
Console.WriteLine($"{passed} checks passed.");

sealed class FakeEngine : ITunnelEngine
{
    public IReadOnlyList<ProbeResult> LastChecks => [];
    public Queue<bool> Checks { get; } = new(); public List<string> Starts { get; } = []; public bool Running { get; private set; } public bool DefaultCheck { get; init; } = true;
    public Task StartAsync(VpnProfile p, ClientSettings s, bool capture, CancellationToken t) { Starts.Add(p.Name + (capture ? ":capture" : ":probe")); Running = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken t) { Running = false; return Task.CompletedTask; }
    public Task<bool> CheckAsync(CancellationToken t) => Task.FromResult(Checks.TryDequeue(out var result) ? result : DefaultCheck);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
