using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace RedRoute.Platform;

public static class NetworkDiagnostics
{
    public static object[] Interfaces() => NetworkInterface.GetAllNetworkInterfaces().Select(n =>
    {
        var ip = n.GetIPProperties();
        return (object)new
        {
            n.Name, Type = n.NetworkInterfaceType.ToString(), State = n.OperationalStatus.ToString(),
            Dns = ip.DnsAddresses.Select(x => x.ToString()).ToArray(),
            Gateways = ip.GatewayAddresses.Select(x => x.Address.ToString()).ToArray(),
            Addresses = ip.UnicastAddresses.Select(x => new { Address = x.Address.ToString(), x.PrefixLength }).ToArray()
        };
    }).ToArray();
    public static async Task<object> RoutesAsync(CancellationToken token)
    {
        // Fixed read-only OS query; no user input is inserted into a shell command.
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        const string query = "Get-NetRoute -ErrorAction Stop | Where-Object { $_.InterfaceAlias -eq 'RED_ROUTE' -or $_.DestinationPrefix -in @('0.0.0.0/0','::/0','0.0.0.0/1','128.0.0.0/1','::/1','8000::/1') } | Select-Object InterfaceAlias,DestinationPrefix,NextHop,RouteMetric,InterfaceMetric,State | ConvertTo-Json -Compress";
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", query }) info.ArgumentList.Add(argument);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var process = Process.Start(info) ?? throw new IOException();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
            var result = await output; _ = await error;
            if (process.ExitCode != 0 || result.Length > 200000) return new { Error = "RouteQueryFailed" };
            return string.IsNullOrWhiteSpace(result) ? Array.Empty<object>() : JsonSerializer.Deserialize<JsonElement>(result);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException) { return new { Error = ex.GetType().Name }; }
    }
}
