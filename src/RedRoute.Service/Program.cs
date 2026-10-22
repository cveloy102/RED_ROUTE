using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using RedRoute.Core;
using RedRoute.Platform;
using RedRoute.Service;

if (args.Contains("--recover"))
{
    // Independent administrator-only recovery entry point. No arbitrary service names or process paths.
    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
    EmergencyRecovery.Run();
    return 0;
}
if (args.Contains("--console"))
{
    using var cancel = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    await ServiceHost.RunAsync(cancel.Token); return 0;
}
ServiceBase.Run(new RedRouteWindowsService()); return 0;

sealed class RedRouteWindowsService : ServiceBase
{
    private CancellationTokenSource? cancel; private Task? run;
    public RedRouteWindowsService() { ServiceName = Protocol.ServiceName; CanStop = true; AutoLog = true; }
    protected override void OnStart(string[] args)
    {
        cancel = new(); run = ServiceHost.RunAsync(cancel.Token);
        _ = run.ContinueWith(_ => Environment.FailFast("RED_ROUTE service stopped unexpectedly."), TaskContinuationOptions.OnlyOnFaulted);
    }
    protected override void OnStop() { RequestAdditionalTime(25000); cancel?.Cancel(); try { run?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } cancel?.Dispose(); }
}
static class ServiceHost
{
    public static async Task RunAsync(CancellationToken stop)
    {
        SecureStore.PrepareServiceDirectory();
        var owner = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\RED_ROUTE", "OwnerSid", null) as string;
        if (string.IsNullOrWhiteSpace(owner)) throw new IOException("Установщик не зарегистрировал владельца RED_ROUTE.");
        await using var engine = new TunnelEngine();
        var controller = new FailoverController(engine);
        ClientSettings settings;
        try { settings = SecureStore.Load(true); } catch { settings = new(); }
        using var stateLock = new SemaphoreSlim(1, 1);
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var activeSync = new object();
        CancellationTokenSource? active = null;
        Task operation = Task.CompletedTask;
        Task Schedule(Func<CancellationToken, Task> action, bool preempt)
        {
            lock (activeSync)
            {
                if (!preempt && active != null) return Task.CompletedTask;
                active?.Cancel();
                var actionSource = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token);
                active = actionSource;
                operation = Task.Run(async () =>
                {
                    var held = false;
                    try
                    {
                        await stateLock.WaitAsync(actionSource.Token); held = true;
                        await action(actionSource.Token);
                    }
                    catch (OperationCanceledException) when (actionSource.IsCancellationRequested)
                    {
                        if (held) await engine.StopAsync(CancellationToken.None);
                    }
                    finally
                    {
                        if (held) stateLock.Release();
                        lock (activeSync) { if (ReferenceEquals(active, actionSource)) active = null; actionSource.Dispose(); }
                    }
                }, CancellationToken.None);
                return operation;
            }
        }
        var monitor = Task.Run(async () =>
        {
            try
            {
                await Schedule(t => controller.ConfigureAsync(settings, t), false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (await timer.WaitForNextTickAsync(stop))
                {
                    await Schedule(controller.TickAsync, false);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(true, false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { new SecurityIdentifier(owner), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            pipeSecurity.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, pipeSecurity);
                await pipe.WaitForConnectionAsync(stop);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var request = await PipeProtocol.ReadAsync<ServiceRequest>(pipe, deadline.Token);
                    ServiceResponse response;
                    if (request.Command == "status") response = new(true, Status: controller.Status);
                    else if (request.Command is "configure" or "recover")
                    {
                        var next = request.Command == "recover" ? System.Text.Json.JsonSerializer.Deserialize<ClientSettings>(System.Text.Json.JsonSerializer.Serialize(settings, Protocol.Json), Protocol.Json)! : request.Settings ?? throw new FormatException("Нет настроек.");
                        if (request.Command == "recover") next.Mode = RouteMode.Off;
                        next.Validate();
                        SecureStore.Save(next, true); settings = next;
                        // Recovery and mode changes cancel even an in-progress candidate probe.
                        _ = Schedule(t => request.Command == "recover" ? controller.RecoverAsync(t) : controller.ConfigureAsync(next, t), true);
                        response = new(true, Status: controller.Status);
                    }
                    else if (request.Command == "check") { _ = Schedule(controller.TickAsync, false); response = new(true, Status: controller.Status); }
                    else response = new(false, "Неизвестная команда.");
                    await PipeProtocol.WriteAsync(pipe, response, deadline.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or FormatException or System.Text.Json.JsonException) { }
            }
        }
        finally
        {
            cycle.Cancel();
            await monitor;
            await operation;
            await engine.StopAsync(CancellationToken.None);
        }
    }
}
