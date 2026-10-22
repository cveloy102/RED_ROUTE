namespace RedRoute.Core;

public interface ITunnelEngine : IAsyncDisposable
{
    IReadOnlyList<ProbeResult> LastChecks { get; }
    Task StartAsync(VpnProfile profile, ClientSettings settings, bool captureTraffic, CancellationToken token);
    Task StopAsync(CancellationToken token);
    Task<bool> CheckAsync(CancellationToken token);
}

/// <summary>All methods are serialized by the service. A candidate is tested without installing routes.</summary>
public sealed class FailoverController(ITunnelEngine engine)
{
    private ClientSettings settings = new();
    private readonly Queue<EventEntry> history = new();
    private int current = -1, failures;
    private ConnectionPhase phase = ConnectionPhase.Off;
    private string message = "VPN выключен. Интернет работает напрямую.";
    private DateTimeOffset? lastSuccess;
    private EngineStatus snapshot = new(RouteMode.Off, ConnectionPhase.Off, null, "VPN выключен. Интернет работает напрямую.", null, 0, []);
    public EngineStatus Status => Volatile.Read(ref snapshot);
    public async Task ConfigureAsync(ClientSettings value, CancellationToken token)
    {
        value.Validate();
        await engine.StopAsync(token);
        settings = new ClientSettings { Mode = value.Mode, Profiles = [.. value.Profiles], Sites = [.. value.Sites], AutoFailover = value.AutoFailover, DatabaseUpdated = value.DatabaseUpdated, StartWithWindows = value.StartWithWindows };
        current = -1; failures = 0;
        if (settings.Mode == RouteMode.Off) { phase = ConnectionPhase.Off; Set("off", "VPN выключен. Интернет работает напрямую."); return; }
        await ConnectAsync(0, token);
    }
    public async Task TickAsync(CancellationToken token)
    {
        if (settings.Mode == RouteMode.Off) return;
        if (phase != ConnectionPhase.Connected) { await ConnectAsync(0, token); return; }
        if (await engine.CheckAsync(token)) { failures = 0; lastSuccess = DateTimeOffset.UtcNow; Publish(); return; }
        failures++;
        Set("probe_failed", $"Проверка VPN не прошла ({failures}/3).");
        if (failures < 3) return;
        var next = settings.AutoFailover ? (current + 1) % Math.Max(settings.Profiles.Count, 1) : current;
        await engine.StopAsync(token);
        phase = ConnectionPhase.Direct;
        Set("fail_open", "Связь с VPN потеряна. Интернет работает напрямую.");
        await ConnectAsync(Math.Max(next, 0), token);
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
    private void Set(string code, string text)
    {
        message = text; history.Enqueue(new(DateTimeOffset.UtcNow, code, text));
        while (history.Count > 150) history.Dequeue();
        Publish();
    }
    private void Publish() => Volatile.Write(ref snapshot, new(settings.Mode, phase,
        current >= 0 && current < settings.Profiles.Count ? settings.Profiles[current].Name : null,
        message, lastSuccess, failures, history.ToArray(), engine.LastChecks));
}
