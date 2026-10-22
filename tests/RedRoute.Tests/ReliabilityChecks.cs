using System.Text.Json.Nodes;
using RedRoute.Core;
using RedRoute.Platform;

internal static class ReliabilityChecks
{
    public static async Task RunAsync(string config, Action<bool, string> assert)
    {
        ClientSettings Settings(RouteMode mode = RouteMode.Auto, bool failover = true) => new()
        {
            Mode = mode, AutoFailover = failover,
            Profiles = [new(Guid.NewGuid(), "Primary", config), new(Guid.NewGuid(), "Reserve", config)],
            Sites = [new("Example", ["example.org"], [])]
        };
        foreach (var mode in Enum.GetValues<RouteMode>())
        foreach (var failover in new[] { false, true })
        {
            var engine = new ControlledEngine(); var controller = new FailoverController(engine); var settings = Settings(mode, failover);
            await controller.ConfigureAsync(settings, default);
            assert(mode == RouteMode.Off ? !engine.IsRunning && engine.Starts.Count == 0 : engine.IsRunning && controller.Status.Phase == ConnectionPhase.Connected, $"{mode}, failover={failover}: expected initial state");
            await controller.RecoverAsync(default); await controller.TickAsync(default);
            assert(controller.Status.Mode == RouteMode.Off && !engine.IsRunning, $"{mode}, failover={failover}: recovery stays Off on next monitor tick");
        }
        foreach (var mode in Enum.GetValues<RouteMode>())
        {
            var current = Settings(mode); var stale = Settings(RouteMode.On); stale.Sites = [new("Fresh", ["fresh.example.org"], [])];
            var update = SettingsCommands.UpdateRules(current, stale);
            assert(update.Mode == mode && update.Profiles.SequenceEqual(current.Profiles) && update.Sites.SequenceEqual(stale.Sites), $"{mode}: background rule update preserves current mode and profiles");
        }
        foreach (var mode in new[] { RouteMode.Auto, RouteMode.On })
        {
            var engine = new ControlledEngine(); var controller = new FailoverController(engine); var settings = Settings(mode);
            await controller.ConfigureAsync(settings, default); var starts = engine.Starts.Count;
            settings.Profiles[0] = settings.Profiles[0] with { Name = "Renamed" }; settings.AutoFailover = false; settings.StartWithWindows = true;
            await controller.ConfigureAsync(settings, default);
            assert(engine.Starts.Count == starts && controller.Status.Profile == "Renamed", $"{mode}: metadata and failover settings do not interrupt working tunnel");
            settings.Profiles.Reverse(); await controller.ConfigureAsync(settings, default);
            assert(engine.Starts.Count == starts && controller.Status.Profile == "Renamed", $"{mode}: changing primary order keeps current working connection");
            settings.Sites.Add(new("New", ["new.example.org"], [])); await controller.ConfigureAsync(settings, default);
            assert(engine.Starts.Count == starts + (mode == RouteMode.Auto ? 2 : 0), $"{mode}: only effective routing changes restart capture");
            starts = engine.Starts.Count; settings.Sites.Reverse(); await controller.ConfigureAsync(settings, default);
            assert(engine.Starts.Count == starts, $"{mode}: rule order does not restart capture");
            engine.Checks.Enqueue(false); await controller.TickAsync(default);
            engine.Checks.Enqueue(true); await controller.TickAsync(default);
            assert(controller.Status.ConsecutiveFailures == 0 && controller.Status.Message.StartsWith("VPN работает") && controller.Status.Events.Any(e => e.Code == "probe_recovered"), $"{mode}: successful probe clears stale failure status");
            engine.Crash(); await controller.TickAsync(default);
            assert(controller.Status.Phase == ConnectionPhase.Connected && controller.Status.Events.Any(e => e.Code == "fail_open") && controller.Status.ConsecutiveFailures == 0, $"{mode}: dead engine releases routes immediately, without waiting three cycles");
            settings.Profiles.Clear(); await controller.ConfigureAsync(settings, default);
            assert(!engine.IsRunning && controller.Status.Phase == ConnectionPhase.Direct, $"{mode}: removing all profiles releases capture");
            settings.Profiles.Add(new(Guid.NewGuid(), "Restored", config)); await controller.ConfigureAsync(settings, default);
            assert(engine.IsRunning && controller.Status.Profile == "Restored", $"{mode}: connection can recover after adding a profile");
        }
        foreach (var failover in new[] { false, true })
        {
            var engine = new ControlledEngine(); engine.Checks.Enqueue(false);
            var controller = new FailoverController(engine); await controller.ConfigureAsync(Settings(failover: failover), default);
            assert(failover ? controller.Status.Profile == "Reserve" : controller.Status.Phase == ConnectionPhase.Direct && !engine.IsRunning, $"Unavailable primary, failover={failover}: reserve policy respected");
        }
        {
            var engine = new ControlledEngine(); engine.Checks.Enqueue(true); engine.Checks.Enqueue(false);
            var controller = new FailoverController(engine); await controller.ConfigureAsync(Settings(), default);
            assert(controller.Status.Profile == "Reserve" && controller.Status.Events.Any(e => e.Code == "activation_failed"), "Failure after route activation releases capture before trying reserve");
        }
        {
            var engine = new ControlledEngine { FailAll = true }; var controller = new FailoverController(engine);
            await controller.ConfigureAsync(Settings(), default); engine.FailAll = false; await controller.TickAsync(default);
            assert(controller.Status.Phase == ConnectionPhase.Connected, "All-unavailable state reconnects when a server becomes healthy");
            engine.StopError = true;
            try { await controller.InterruptedAsync("test_cleanup_failure"); } catch (IOException) { }
            assert(controller.Status.Phase == ConnectionPhase.Direct && controller.Status.Profile == null, "Failed cleanup cannot leave a stale Connected status");
        }
        {
            var engine = new ControlledEngine { CancelStart = true }; var controller = new FailoverController(engine);
            using var source = new CancellationTokenSource(); source.Cancel();
            try { await controller.ConfigureAsync(Settings(), source.Token); } catch (OperationCanceledException) { await controller.InterruptedAsync("operation_cancelled"); }
            assert(!engine.IsRunning && controller.Status.Phase == ConnectionPhase.Direct, "Cancelled connection leaves direct state and stops candidate");
            await controller.RecoverAsync(default);
            assert(controller.Status.Mode == RouteMode.Off, "Off recovery preempts a cancelled candidate");
        }
        {
            var settings = Settings(); settings.Sites.Clear();
            var json = CoreConfiguration.Build(ConfParser.Parse(config), settings, true, 12345, "synthetic");
            assert(json["route"]!["final"]!.GetValue<string>() == "direct" && !json["dns"]!["rules"]!.AsArray().Any(), "Auto with empty list keeps public traffic and DNS direct");
            assert(json["route"]!["rules"]!.AsArray().Where(r => r?["outbound"]?.GetValue<string>() == "vpn").All(r => r?["inbound"]?[0]?.GetValue<string>() == "health"), "Empty Auto list has no accidental public VPN route");
            var cached = CoreConfiguration.Build(ConfParser.Parse(config), settings, true, 12345, "synthetic", dnsCachePath: "synthetic-cache.db");
            assert(cached["experimental"]!["cache_file"]!["store_fakeip"]!.GetValue<bool>(), "Capturing core persists cached DNS mappings across restarts");
            assert(CoreConfiguration.Build(ConfParser.Parse(config), settings, false, 12345, "synthetic", dnsCachePath: "synthetic-cache.db")["experimental"] == null, "Candidate core never locks the live DNS cache");
        }
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "checks", "journal"); Directory.CreateDirectory(directory);
            var journal = new ServiceEventJournal(directory); var engine = new ControlledEngine();
            var controller = new FailoverController(engine, saveHistory: journal.Save); await controller.ConfigureAsync(Settings(), default);
            assert(journal.Load().Any(e => e.Code == "connected"), "Connection events survive controller/service restart");
            assert(!File.ReadAllText(Path.Combine(directory, "connection-events.json")).Contains(ConfParser.Parse(config).Interface["PrivateKey"]), "Persistent journal excludes VPN keys");
            File.WriteAllText(Path.Combine(directory, "connection-events.json"), "broken-json");
            assert(journal.Load().Count == 0, "Damaged diagnostic journal does not block startup");
        }
        {
            var engine = new ControlledEngine(); var controller = new FailoverController(engine, saveHistory: _ => throw new IOException("Simulated full diagnostic disk"));
            await controller.ConfigureAsync(Settings(), default);
            assert(engine.IsRunning && controller.Status.Phase == ConnectionPhase.Connected, "Diagnostic storage failure cannot interrupt VPN or hide current status");
        }
    }
    private sealed class ControlledEngine : ITunnelEngine
    {
        public bool IsRunning { get; private set; }
        public bool FailAll { get; set; }
        public bool StopError { get; set; }
        public bool CancelStart { get; init; }
        public List<bool> Starts { get; } = [];
        public Queue<bool> Checks { get; } = new();
        public IReadOnlyList<ProbeResult> LastChecks { get; private set; } = [];
        public Task StartAsync(VpnProfile profile, ClientSettings settings, bool capture, CancellationToken token)
        {
            if (CancelStart) token.ThrowIfCancellationRequested();
            IsRunning = true; Starts.Add(capture); return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken token) { IsRunning = false; if (StopError) { StopError = false; throw new IOException("Simulated cleanup failure"); } return Task.CompletedTask; }
        public Task<bool> CheckAsync(CancellationToken token)
        {
            var result = IsRunning && !FailAll && (!Checks.TryDequeue(out var check) || check);
            LastChecks = [new("synthetic", result, 0, result ? "HTTP 204" : IsRunning ? "Timeout" : "ProcessNotRunning")];
            return Task.FromResult(result);
        }
        public void Crash() => IsRunning = false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
