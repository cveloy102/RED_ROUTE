using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using RedRoute.Core;
using RedRoute.Platform;

namespace RedRoute.Service;

internal static class EmergencyRecovery
{
    public static void Run()
    {
        SecureStore.PrepareServiceDirectory();
        using var service = new ServiceController(Protocol.ServiceName);
        try
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            // Query SCM for the exact service PID and verify the installed executable before termination.
            if (!QueryServiceStatusEx(service.ServiceHandle.DangerousGetHandle(), 0, out var state, Marshal.SizeOf<ServiceStatusProcess>(), out _) || state.ProcessId == 0) throw new IOException("Не найден процесс службы.");
            using var process = Process.GetProcessById((int)state.ProcessId);
            if (!string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase) || process.SessionId != 0)
                throw new IOException("Путь процесса службы не совпал с установленным RED_ROUTE.");
            process.Kill(true); process.WaitForExit(10000);
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
        }
        ClientSettings settings;
        try { settings = SecureStore.Load(true); }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or FormatException)
        {
            var original = Path.Combine(SecureStore.ServiceDirectory, "settings.dpapi");
            if (File.Exists(original)) File.Copy(original, original + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
            settings = new();
        }
        settings.Mode = RouteMode.Off; SecureStore.Save(settings, true);
        var runtime = Path.Combine(SecureStore.ServiceDirectory, "runtime.json"); if (File.Exists(runtime)) File.Delete(runtime);
        DnsFlushResolverCache(); service.Start();
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess { public uint Type, State, Controls, Win32Exit, SpecificExit, CheckPoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess buffer, int size, out int needed);
    [DllImport("dnsapi.dll")] private static extern bool DnsFlushResolverCache();
}
