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
        var journal = new ServiceEventJournal();
        var controller = new FailoverController(engine, journal.Load(), journal.Save);
        ClientSettings settings;
        try { settings = SecureStore.Load(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep a copy: the next configure command overwrites the unreadable file.
            var original = Path.Combine(SecureStore.ServiceDirectory, "settings.dpapi");
            try { if (File.Exists(original)) File.Copy(original, original + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            settings = new();
        }
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var operations = new OperationQueue(cycle.Token, controller.InterruptedAsync);
        Task Schedule(Func<CancellationToken, Task> action, bool preempt) => operations.Schedule(action, preempt);
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
        ServiceResponse Handle(ServiceRequest request)
        {
            if (request.Command == "status") return new(true, Status: controller.Status);
            if (request.Command == "check") { _ = Schedule(controller.TickAsync, false); return new(true, Status: controller.Status); }
            if (request.Command is not ("configure" or "recover" or "update-rules" or "update-profiles" or "select-primary")) return new(false, "Неизвестная команда.");
            ClientSettings next;
            try
            {
                next = request.Command == "recover" ? System.Text.Json.JsonSerializer.Deserialize<ClientSettings>(System.Text.Json.JsonSerializer.Serialize(settings, Protocol.Json), Protocol.Json)! : request.Settings ?? throw new FormatException("Нет настроек.");
                if (request.Command == "update-rules") next = SettingsCommands.UpdateRules(settings, next);
                if (request.Command is "update-profiles" or "select-primary") next = SettingsCommands.UpdateProfiles(settings, next);
                if (request.Command == "recover") next.Mode = RouteMode.Off;
                next.Validate();
            }
            catch (FormatException ex) { return new(false, ex.Message); }
            try { SecureStore.Save(next, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // Recovery must still release the tunnel even when the settings file cannot be written.
                if (request.Command == "recover") { settings = next; _ = Schedule(controller.RecoverAsync, true); }
                return new(false, "Служба не смогла сохранить настройки (" + ex.GetType().Name + ")." + (request.Command == "recover" ? " VPN выключен до перезапуска службы." : ""));
            }
            settings = next;
            // Recovery and mode changes cancel even an in-progress candidate probe.
            _ = Schedule(t => request.Command == "recover" ? controller.RecoverAsync(t) : controller.ConfigureAsync(next, t, request.Command == "select-primary"), true);
            return new(true, Status: controller.Status);
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try { pipe = NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, pipeSecurity); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another process may hold the name. Keep routing alive and retry instead of crashing the service.
                    try { await Task.Delay(TimeSpan.FromSeconds(2), stop); } catch (OperationCanceledException) { break; }
                    continue;
                }
                using (pipe)
                {
                    try { await pipe.WaitForConnectionAsync(stop); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (IOException) { continue; }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop); deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    try
                    {
                        var request = await PipeProtocol.ReadAsync<ServiceRequest>(pipe, deadline.Token);
                        await PipeProtocol.WriteAsync(pipe, Handle(request), deadline.Token);
                    }
                    // One malformed or failed request must never stop the service and drop the tunnel.
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
            }
        }
        finally
        {
            cycle.Cancel();
            await monitor;
            await operations.ShutdownAsync();
            await engine.StopAsync(CancellationToken.None);
        }
    }
}
