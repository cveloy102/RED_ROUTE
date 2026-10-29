namespace RedRoute.Core;

public interface ITunnelEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    bool NetworkChanged => false;
    IReadOnlyList<ProbeResult> LastChecks { get; }
    Task StartAsync(VpnProfile profile, ClientSettings settings, bool captureTraffic, CancellationToken token);
    Task StopAsync(CancellationToken token);
    Task<bool> CheckAsync(CancellationToken token);
}

/// <summary>All methods are serialized by the service. A candidate is tested without installing routes.</summary>
public sealed class FailoverController
{
    private readonly ITunnelEngine engine;
    private readonly Action<IReadOnlyList<EventEntry>>? saveHistory;
    private ClientSettings settings = new();
    private readonly Queue<EventEntry> history = new();
    private int current = -1, failures;
    private ConnectionPhase phase = ConnectionPhase.Off;
    private string message = "VPN выключен. Интернет работает напрямую.";
    private DateTimeOffset? lastSuccess;
    private EngineStatus snapshot = new(RouteMode.Off, ConnectionPhase.Off, null, "VPN выключен. Интернет работает напрямую.", null, 0, []);
    public FailoverController(ITunnelEngine engine, IEnumerable<EventEntry>? previousEvents = null, Action<IReadOnlyList<EventEntry>>? saveHistory = null)
    {
        this.engine = engine; this.saveHistory = saveHistory;
        foreach (var entry in (previousEvents ?? []).TakeLast(150)) history.Enqueue(entry);
    }
    public EngineStatus Status => Volatile.Read(ref snapshot);
    public async Task ConfigureAsync(ClientSettings value, CancellationToken token, bool connectPrimary = false)
    {
        value.Validate();
        var active = current >= 0 && current < settings.Profiles.Count ? settings.Profiles[current] : null;
        var primaryChanged = settings.Profiles.FirstOrDefault()?.Id != value.Profiles.FirstOrDefault()?.Id;
        var nextIndex = active == null ? -1 : value.Profiles.FindIndex(p => p.Id == active.Id && p.Config == active.Config);
        var keepTunnel = !primaryChanged && !(connectPrimary && nextIndex != 0) && phase == ConnectionPhase.Connected && engine.IsRunning && value.Mode == settings.Mode && nextIndex >= 0
            && (value.Mode != RouteMode.Auto || EffectiveSites(value).SequenceEqual(EffectiveSites(settings)));
        settings = new ClientSettings { Mode = value.Mode, Profiles = [.. value.Profiles], Sites = [.. value.Sites], AutoFailover = value.AutoFailover, DatabaseUpdated = value.DatabaseUpdated, StartWithWindows = value.StartWithWindows };
        if (keepTunnel)
        {
            current = nextIndex;
            if (failures == 0) message = $"VPN работает · {settings.Profiles[current].Name}";
            Set("settings_updated", "Настройки обновлены без перезапуска VPN.", false); return;
        }
        await engine.StopAsync(token);
        current = -1; failures = 0;
        if (settings.Mode == RouteMode.Off) { phase = ConnectionPhase.Off; Set("off", "VPN выключен. Интернет работает напрямую."); return; }
        await ConnectAsync(0, token);
    }
    public async Task TickAsync(CancellationToken token)
    {
        if (settings.Mode == RouteMode.Off) return;
        if (phase != ConnectionPhase.Connected) { await ConnectAsync(0, token); return; }
        if (engine.NetworkChanged)
        {
            await InterruptedAsync("network_changed");
            await ConnectAsync(0, token);
            return;
        }
        var healthy = await engine.CheckAsync(token);
        if (engine.NetworkChanged)
        {
            await InterruptedAsync("network_changed");
            await ConnectAsync(0, token);
            return;
        }
        if (healthy)
        {
            var hadFailure = failures > 0; failures = 0; lastSuccess = DateTimeOffset.UtcNow;
            message = $"VPN работает · {settings.Profiles[current].Name}";
            if (hadFailure) Set("probe_recovered", "Проверка VPN снова проходит. Соединение работает.", false); else Publish();
            return;
        }
        failures++;
        Set("probe_failed", $"Проверка VPN не прошла ({failures}/3). " + string.Join("; ", engine.LastChecks.Select(c => $"{c.Target}: {c.Result}, {c.Milliseconds} мс")));
        if (engine.IsRunning && failures < 3) return;
        var next = settings.AutoFailover ? (current + 1) % Math.Max(settings.Profiles.Count, 1) : current;
        await engine.StopAsync(token);
        phase = ConnectionPhase.Direct;
        Set("fail_open", "Связь с VPN потеряна. Интернет работает напрямую.");
        await ConnectAsync(Math.Max(next, 0), token);
    }
    public async Task InterruptedAsync(string code)
    {
        try { await engine.StopAsync(CancellationToken.None); }
        finally
        {
            current = -1; failures = 0; phase = settings.Mode == RouteMode.Off ? ConnectionPhase.Off : ConnectionPhase.Direct;
            Set(code, settings.Mode == RouteMode.Off ? "VPN выключен. Интернет работает напрямую." : "Подключение прервано. Интернет работает напрямую. Повторим подключение.");
        }
    }
    public async Task RecoverAsync(CancellationToken token)
    {
        settings.Mode = RouteMode.Off;
        await engine.StopAsync(token);
        current = -1; failures = 0; phase = ConnectionPhase.Off;
        Set("recovered", "Обычный интернет восстановлен. VPN выключен.");
    }
    private async Task ConnectAsync(int start, CancellationToken token)
    {
        if (settings.Profiles.Count == 0) { phase = ConnectionPhase.Direct; Set("no_profiles", "Добавьте файл .conf. Интернет работает напрямую."); return; }
        var count = settings.AutoFailover ? settings.Profiles.Count : 1;
        for (var n = 0; n < count; n++)
        {
            current = (start + n) % settings.Profiles.Count;
            phase = ConnectionPhase.Connecting;
            Set("connecting", $"Проверяем профиль «{settings.Profiles[current].Name}». Пока интернет работает напрямую.");
            try
            {
                await engine.StopAsync(token);
                await engine.StartAsync(settings.Profiles[current], settings, false, token);
                if (!await engine.CheckAsync(token)) { Set("candidate_failed", $"Профиль «{settings.Profiles[current].Name}» не прошёл проверку."); continue; }
                await engine.StopAsync(token);
                await engine.StartAsync(settings.Profiles[current], settings, true, token);
                if (!await engine.CheckAsync(token)) { Set("activation_failed", "Проверка после включения маршрутов не прошла."); continue; }
                lastSuccess = DateTimeOffset.UtcNow; failures = 0; phase = ConnectionPhase.Connected;
                Set("connected", $"VPN работает · {settings.Profiles[current].Name}"); return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Exception messages may contain profile secrets or raw engine configuration.
                Set("engine_error", $"Не удалось подключить профиль. Код: {ex.GetType().Name}.");
            }
            finally
            {
                if (phase != ConnectionPhase.Connected) await engine.StopAsync(CancellationToken.None);
            }
        }
        current = -1; phase = ConnectionPhase.Direct;
        Set("all_unavailable", "VPN недоступен. Интернет работает напрямую. Повторим подключение.");
    }
    private static IEnumerable<string> EffectiveSites(ClientSettings value) => value.Sites.SelectMany(s => s.Domains.Select(d => "d:" + Domains.Normalize(d))
        .Concat(s.IpRanges.Select(ip => "i:" + Domains.NormalizeCidr(ip)))).Distinct().Order(StringComparer.Ordinal);
    private void Set(string code, string text, bool replaceMessage = true)
    {
        if (replaceMessage) message = text;
        history.Enqueue(new(DateTimeOffset.UtcNow, code, text));
        while (history.Count > 150) history.Dequeue();
        Publish();
        try { saveHistory?.Invoke(history.ToArray()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Diagnostic storage cannot interrupt routing. */ }
    }
    private void Publish() => Volatile.Write(ref snapshot, new(settings.Mode, phase,
        current >= 0 && current < settings.Profiles.Count ? settings.Profiles[current].Name : null,
        message, lastSuccess, failures, history.ToArray(), engine.LastChecks));
}
