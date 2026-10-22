using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using RedRoute.Core;
using RedRoute.Platform;

namespace RedRoute.App;

internal static class Recovery
{
    public static async Task<bool> RunAsync()
    {
        try { var saved = SecureStore.Load(); saved.Mode = RouteMode.Off; SecureStore.Save(saved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException) { }
        try
        {
            var result = await PipeProtocol.CallAsync(new("recover"));
            if (result.Success)
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    var state = (await PipeProtocol.CallAsync(new("status"))).Status;
                    if (state?.Mode == RouteMode.Off && state.Phase == ConnectionPhase.Off) return true;
                    await Task.Delay(150);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "service", "RED_ROUTE.Service.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException();
            using var process = Process.Start(new ProcessStartInfo(executable, "--recover") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (process == null) return false;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException();
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("Не удалось завершить восстановление. Повторите через ярлык «RED_ROUTE — восстановить интернет» и разрешите действие Windows.", "RED_ROUTE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
