using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RedRoute.Setup;

internal static class InstallOwner
{
    internal static string Select(string? existing, string? desktop)
    {
        foreach (var value in new[] { existing, desktop })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                var sid = new SecurityIdentifier(value);
                if (sid.IsAccountSid() || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal)) return sid.Value;
            }
            catch (ArgumentException) { }
        }
        throw new IOException("Не удалось определить пользователя RED_ROUTE. Запустите установщик из обычного рабочего стола Windows.");
    }
    public static string Resolve()
    {
        var existing = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\RED_ROUTE", "OwnerSid", null) as string;
        string? desktop = null;
        var shell = GetShellWindow();
        if (shell != IntPtr.Zero)
        {
            GetWindowThreadProcessId(shell, out var pid);
            var process = OpenProcess(0x1000, false, pid);
            if (process != IntPtr.Zero)
            {
                try
                {
                    if (OpenProcessToken(process, 8, out var token))
                        using (token)
                        using (var identity = new WindowsIdentity(token.DangerousGetHandle())) desktop = identity.User?.Value;
                }
                finally { CloseHandle(process); }
            }
        }
        // Never grant control to the administrator whose credentials were supplied to UAC.
        return Select(existing, desktop);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
