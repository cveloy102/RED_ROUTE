using System.Net;
using System.Net.Sockets;
using RedRoute.Core;

internal static class ReadinessChecks
{
    public static async Task RunAsync(Action<bool,string> assert)
    {
        var ready = new EngineReadiness();
        var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            assert(!ready.IsReady, "An already accepting health port does not prove engine/TUN readiness");
            var wait = ready.WaitAsync(default);
            foreach(var line in new[]{"INFO: RED_ROUTE_READY", "RED_ROUTE_READY extra", "", "RED_ROUTE_STOP"}) ready.Observe(line);
            assert(!wait.IsCompleted, "Unrelated or partial log lines cannot report a fully started engine");
            ready.Observe(EngineReadiness.Signal); await wait;
            assert(ready.IsReady, "Only the complete startup protocol signal releases readiness waiters");
        }
        finally { listener.Stop(); }
        var pending = new EngineReadiness(); using var cancelled = new CancellationTokenSource();
        var cancelledWait = pending.WaitAsync(cancelled.Token); cancelled.Cancel();
        try { await cancelledWait; throw new Exception("Readiness ignored cancellation"); }
        catch(OperationCanceledException) { assert(!pending.IsReady, "Cancelling startup leaves readiness false and lets recovery proceed"); }
    }
}
