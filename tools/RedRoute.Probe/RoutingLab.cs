using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RedRoute.Core;
using RedRoute.Platform;

internal static class RoutingLab
{
    public static async Task RunAsync()
    {
        var settings = SecureStore.Load(); var original = settings.Mode;
        var dns = DirectNetwork.Dns() ?? throw new IOException("No physical DNS was found");
        var profile = ConfParser.Parse(settings.Profiles[0].Config);
        var endpoint = profile.Endpoint;
        if (!IPAddress.TryParse(endpoint.Host, out var endpointIp)) endpointIp = (await Dns.GetHostAddressesAsync(endpoint.Host)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        profile.Peer["Endpoint"] = endpointIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endpointIp}]:{endpoint.Port}" : $"{endpointIp}:{endpoint.Port}";
        var folder = Path.Combine(SecureStore.UserDirectory, "diagnostic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "runtime.json");
        try
        {
            settings.Mode = RouteMode.Off; await PipeProtocol.CallAsync(new("configure", settings));
            var stopped = false;
            for (var i = 0; i < 50; i++) { if ((await PipeProtocol.CallAsync(new("status"))).Status?.Phase == ConnectionPhase.Off) { stopped = true; break; } await Task.Delay(200); }
            if (!stopped) throw new IOException("Installed tunnel did not stop; laboratory test aborted");
            foreach (var fixedDns in new[] { false, true })
            {
                settings.Mode = RouteMode.Auto;
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var json = CoreConfiguration.Build(profile, settings, true, port, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), fixedDns ? dns.Server : null, fixedDns ? dns.Interface : null);
                json["log"]!["level"] = "debug";
                await File.WriteAllTextAsync(file, json.ToJsonString());
                var info = new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory, "artifacts", "engine", "amnezia-box.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "run", "-c", file }) info.ArgumentList.Add(arg);
                using var child = Process.Start(info)!;
                void Log(object _, DataReceivedEventArgs e)
                {
                    if (e.Data is { } line && new[] { "api.ipify.org", "2ip.ru", "cdn-cgi", "cloudflare.com" }.Any(line.Contains))
                        Console.WriteLine(Regex.Replace(line, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[ip]"));
                }
                child.OutputDataReceived += Log; child.ErrorDataReceived += Log; child.BeginOutputReadLine(); child.BeginErrorReadLine();
                try
                {
                    await Task.Delay(1000);
                    if (child.HasExited) throw new IOException("Laboratory engine failed to start");
                    Console.WriteLine("LAB: explicit physical DNS = " + fixedDns);
                    foreach (var url in new[] { "https://api.ipify.org", "https://www.cloudflare.com/cdn-cgi/trace" })
                    {
                        using var handler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
                        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }; var clock = Stopwatch.StartNew();
                        try { using var response = await client.GetAsync(url); Console.WriteLine($"LAB {new Uri(url).Host}: HTTP {(int)response.StatusCode}; {clock.ElapsedMilliseconds} ms"); }
                        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Console.WriteLine($"LAB {new Uri(url).Host}: {ex.GetType().Name}; {clock.ElapsedMilliseconds} ms"); }
                    }
                }
                finally
                {
                    if (!child.HasExited) { await child.StandardInput.WriteLineAsync("RED_ROUTE_STOP"); await child.StandardInput.FlushAsync(); try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { child.Kill(true); await child.WaitForExitAsync(); } }
                }
            }
        }
        finally { File.Delete(file); Directory.Delete(folder); settings.Mode = original; await PipeProtocol.CallAsync(new("configure", settings)); Console.WriteLine("Restored mode: " + original); }
    }
}
