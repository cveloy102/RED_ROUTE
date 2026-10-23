using RedRoute.Core;

internal static class FailoverMatrixChecks
{
    public static async Task RunAsync(string config, Action<bool, string> assert)
    {
        ClientSettings Settings(RouteMode mode, int profiles, bool failover) => new()
        {
            Mode = mode, AutoFailover = failover,
            Profiles = Enumerable.Range(0, profiles).Select(i => new VpnProfile(Guid.NewGuid(), "Server " + i, config)).ToList(),
            Sites = [new("Selected", ["example.org"], [])]
        };
        foreach (var mode in Enum.GetValues<RouteMode>())
        foreach (var count in new[] { 0, 1, 3 })
        foreach (var reserves in new[] { false, true })
        foreach (var healthy in new[] { false, true })
        {
            var engine = new Engine { Healthy = healthy }; var controller = new FailoverController(engine);
            await controller.ConfigureAsync(Settings(mode, count, reserves), default);
            var connected = mode != RouteMode.Off && count > 0 && healthy;
            var expected = mode == RouteMode.Off ? ConnectionPhase.Off : connected ? ConnectionPhase.Connected : ConnectionPhase.Direct;
            var label = $"{mode}, {count} profiles, reserves={reserves}, healthy={healthy}";
            assert(controller.Status.Phase == expected && engine.Capturing == connected, "Mode/availability matrix: " + label);
            if (!healthy) assert(engine.CaptureStarts == 0, "Unproven profiles never capture traffic: " + label);
            await controller.RecoverAsync(default); await controller.TickAsync(default);
            assert(controller.Status.Mode == RouteMode.Off && !engine.IsRunning && !engine.Capturing, "Recovery is persistent and leaves no active tunnel: " + label);
        }
        foreach (var mode in new[] { RouteMode.Auto, RouteMode.On })
        foreach (var fault in new[] { "start-probe", "check-probe", "start-capture", "check-capture", "stop" })
        {
            var engine = new Engine { Fault = fault }; var controller = new FailoverController(engine);
            await using var queue = new OperationQueue(default, controller.InterruptedAsync);
            await queue.Schedule(t => controller.ConfigureAsync(Settings(mode, 3, true), t), true);
            if (controller.Status.Phase != ConnectionPhase.Connected) await queue.Schedule(controller.TickAsync, false);
            assert(engine.FaultThrown && controller.Status.Phase == ConnectionPhase.Connected, mode + ": service survives one " + fault + " failure and establishes a working connection");
            await queue.Schedule(controller.RecoverAsync, true);
            assert(!engine.IsRunning && !engine.Capturing && controller.Status.Mode == RouteMode.Off, mode + ": recovery still works after " + fault);
        }
        {
            var engine = new Engine(); var controller = new FailoverController(engine);
            await controller.ConfigureAsync(Settings(RouteMode.Auto, 1, true), default); var starts = engine.CaptureStarts;
            try { await controller.ConfigureAsync(new ClientSettings { Mode = (RouteMode)999 }, default); throw new Exception("Invalid settings accepted"); }
            catch (FormatException) { assert(engine.IsRunning && controller.Status.Phase == ConnectionPhase.Connected && engine.CaptureStarts == starts, "Rejected settings cannot interrupt the existing working connection"); }
        }
    }
    private sealed class Engine : ITunnelEngine
    {
        public bool Healthy { get; init; } = true;
        public string? Fault { get; init; }
        public bool FaultThrown { get; private set; }
        public bool IsRunning { get; private set; }
        public bool Capturing { get; private set; }
        public int CaptureStarts { get; private set; }
        public IReadOnlyList<ProbeResult> LastChecks => [new("synthetic", Healthy && IsRunning, 0, "Synthetic")];
        private void Throw(string stage)
        {
            if (!FaultThrown && Fault == stage) { FaultThrown = true; throw new IOException("Synthetic " + stage); }
        }
        public Task StartAsync(VpnProfile profile, ClientSettings settings, bool captureTraffic, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); IsRunning = true; Capturing = captureTraffic;
            if (captureTraffic) CaptureStarts++;
            Throw(captureTraffic ? "start-capture" : "start-probe"); return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken token) { IsRunning = Capturing = false; Throw("stop"); return Task.CompletedTask; }
        public Task<bool> CheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Throw(Capturing ? "check-capture" : "check-probe"); return Task.FromResult(Healthy && IsRunning); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
