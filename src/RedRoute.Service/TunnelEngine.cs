using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RedRoute.Core;
using RedRoute.Platform;

namespace RedRoute.Service;

public sealed class TunnelEngine : ITunnelEngine
{
    private Process? process;
    private int healthPort;
    private string password = "";
    private string? networkFingerprint;
    private string? probedNetworkProfile;
    private bool configuredIpv6, effectiveIpv6, measuredIpv6, ipv6Changed;
    private DateTimeOffset lastIpv6Probe;
    private ProbeResult[] ipv6Checks = [];
    private IntPtr job;
    private readonly string runtimeFile = Path.Combine(SecureStore.ServiceDirectory, "runtime.json");
    private readonly string executable = Path.Combine(AppContext.BaseDirectory, "engine", "amnezia-box.exe");
    public IReadOnlyList<ProbeResult> LastChecks { get; private set; } = [];
    public bool IsRunning => process != null && !process.HasExited;
    public bool NetworkChanged => ipv6Changed || networkFingerprint != null && networkFingerprint != DirectNetwork.Fingerprint();
    public async Task StartAsync(VpnProfile profile, ClientSettings settings, bool captureTraffic, CancellationToken token)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Ядро VPN не установлено.");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); healthPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var parsed = ConfParser.Parse(profile.Config);
        var (host, port) = parsed.Endpoint;
        if (!IPAddress.TryParse(host, out var endpointAddress))
        {
            var addresses = await Dns.GetHostAddressesAsync(host, token);
            endpointAddress = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault() ?? throw new IOException("Не найден адрес сервера.");
        }
        parsed.Peer["Endpoint"] = endpointAddress.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endpointAddress}]:{port}" : $"{endpointAddress}:{port}";
        var directDns = DirectNetwork.Dns();
        var directIpv6 = DirectNetwork.HasPublicIpv6Route();
        networkFingerprint = DirectNetwork.Fingerprint();
        configuredIpv6 = Ipv6Routing.ProfileSupportsIpv6(parsed);
        var profileBytes = Encoding.UTF8.GetBytes(profile.Config);
        string probeKey;
        try { probeKey = Convert.ToHexString(SHA256.HashData(profileBytes)) + ":" + networkFingerprint; }
        finally { CryptographicOperations.ZeroMemory(profileBytes); }
        // A profile's IPv6 address does not prove that its server can reach IPv6 Internet.
        // Only reuse a measured result from the immediately preceding route-free candidate.
        effectiveIpv6 = configuredIpv6 && (!captureTraffic || probedNetworkProfile == probeKey && measuredIpv6);
        ipv6Changed = false;
        var json = CoreConfiguration.Build(parsed, settings, captureTraffic, healthPort, password, directDns?.Server, directDns?.Interface, Path.Combine(SecureStore.ServiceDirectory, "dns-cache.db"), directIpv6, effectiveIpv6).ToJsonString();
        await File.WriteAllTextAsync(runtimeFile, json, token);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add("run"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(runtimeFile);
        process = Process.Start(info) ?? throw new IOException("Не удалось запустить ядро.");
        var readiness = new EngineReadiness();
        // Raw engine logs can contain destinations; drain without retaining them in the diagnostic report.
        process.OutputDataReceived += (_, e) => readiness.Observe(e.Data); process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        job = Native.CreateKillJob(process.Handle);
        for (var i = 0; i < 150; i++)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("Ядро VPN завершилось при запуске.");
            // The health listener can open before the TUN routes/DNS have been installed.
            if (!readiness.IsReady) { await Task.Delay(100, token); continue; }
            using var tcp = new TcpClient();
            var ready = false;
            try { await tcp.ConnectAsync(IPAddress.Loopback, healthPort, token); ready = true; } catch (SocketException) { }
            if (ready)
            {
                if (!captureTraffic)
                {
                    ipv6Checks = configuredIpv6 ? await ProbeAsync(true, token) : [];
                    measuredIpv6 = ipv6Checks.Any(c => c.Success);
                    effectiveIpv6 = measuredIpv6;
                    probedNetworkProfile = probeKey;
                    lastIpv6Probe = DateTimeOffset.UtcNow;
                }
                return;
            }
            await Task.Delay(100, token);
        }
        throw new TimeoutException("Ядро не ответило при запуске.");
    }
    public async Task<bool> CheckAsync(CancellationToken token)
    {
        if (process is null || process.HasExited) { LastChecks = [new("VPN engine", false, 0, "ProcessNotRunning")]; return false; }
        var checks = await ProbeAsync(false, token);
        // Recheck a working IPv6 path each cycle; retry an unavailable one less often.
        if (configuredIpv6 && DateTimeOffset.UtcNow - lastIpv6Probe >= TimeSpan.FromSeconds(effectiveIpv6 ? 10 : 60))
        {
            ipv6Checks = await ProbeAsync(true, token);
            measuredIpv6 = ipv6Checks.Any(c => c.Success);
            lastIpv6Probe = DateTimeOffset.UtcNow;
            ipv6Changed = measuredIpv6 != effectiveIpv6;
        }
        LastChecks = [.. checks, .. ipv6Checks];
        return checks.Any(c => c.Success);
    }
    private async Task<ProbeResult[]> ProbeAsync(bool ipv6, CancellationToken token)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{healthPort}") { Credentials = new NetworkCredential(ipv6 ? "redroute-ipv6" : "redroute", password) },
            UseProxy = true, AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(ipv6 ? 3 : 5) };
        var results = await Task.WhenAll(new[] { "https://cp.cloudflare.com/generate_204", "https://www.gstatic.com/generate_204" }.Select(async url =>
        {
            var timer = Stopwatch.StartNew();
            var target = (ipv6 ? "IPv6: " : "") + new Uri(url).Host;
            try { using var request = new HttpRequestMessage(HttpMethod.Get, url); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); return new ProbeResult(target, response.StatusCode == HttpStatusCode.NoContent, timer.ElapsedMilliseconds, "HTTP " + (int)response.StatusCode); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { return new ProbeResult(target, false, timer.ElapsedMilliseconds, ex is HttpRequestException http ? http.HttpRequestError.ToString() : ex is OperationCanceledException ? "Timeout" : "IOError"); }
        }));
        token.ThrowIfCancellationRequested();
        return results;
    }
    public async Task StopAsync(CancellationToken token)
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("RED_ROUTE_STOP");
                    await process.StandardInput.FlushAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { }
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            // Always release the kill-on-close job, including failed/time-limited shutdowns.
            if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
            process?.Dispose(); process = null; password = ""; networkFingerprint = null; ipv6Changed = false;
            try { if (File.Exists(runtimeFile)) File.Delete(runtimeFile); }
            finally { Native.DnsFlushResolverCache(); }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, int length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("dnsapi.dll")] public static extern bool DnsFlushResolverCache();
        public static IntPtr CreateKillJob(IntPtr processHandle)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(handle, processHandle))
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
                throw new IOException("Не удалось включить аварийное освобождение туннеля.");
            }
            return handle;
        }
    }
}
