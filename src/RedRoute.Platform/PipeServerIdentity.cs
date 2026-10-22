using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RedRoute.Platform;

internal static class PipeServerIdentity
{
    public static void Verify(NamedPipeClientStream pipe)
    {
        // Prevent another local account from pre-creating the pipe and collecting plaintext profiles.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("Не удалось проверить службу RED_ROUTE.");
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) throw new IOException("Не удалось проверить процесс службы.");
        try
        {
            var path = new StringBuilder(32768); var length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new IOException("Не удалось определить путь службы.");
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "service", "RED_ROUTE.Service.exe"));
            if (!string.Equals(Path.GetFullPath(path.ToString()), expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Подключение к неподтверждённой службе отклонено.");
        }
        finally { CloseHandle(process); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
