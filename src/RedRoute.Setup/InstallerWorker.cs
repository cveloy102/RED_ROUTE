using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RedRoute.Setup;

internal sealed class InstallerWorker
{
    public event Action<double, string>? ProgressChanged;
    public string LogPath { get; private set; } = "";
    private volatile bool cancelled;
    private long total, position;
    private double percent;
    private bool backwards;
    private string phase = "Подготавливаем установку…";
    public void Cancel() => cancelled = true;
    public void ResetCancellation() => cancelled = false;
    public uint Install()
    {
        total = position = 0; percent = 0;
        var owner = InstallOwner.Resolve();
        var folder = Path.Combine(Path.GetTempPath(), "RED_ROUTE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var msi = Path.Combine(folder, "RED_ROUTE.msi");
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RED_ROUTE", "Installer"); Directory.CreateDirectory(logs);
        LogPath = Path.Combine(logs, "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        try
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("RED_ROUTE.Payload.msi") ?? throw new InvalidOperationException("Installer payload missing"))
            using (var destination = File.Create(msi))
            {
                var buffer = new byte[1024 * 1024]; int count;
                while ((count = resource.Read(buffer)) > 0)
                {
                    if (cancelled) return 1602;
                    destination.Write(buffer, 0, count);
                    ProgressChanged?.Invoke(resource.CanSeek ? resource.Position * 4.0 / resource.Length : 1, phase);
                }
            }
            ProgressChanged?.Invoke(5, phase);
            var callback = new Native.RecordHandler(OnMessage);
            var previousUi = Native.MsiSetInternalUI(2, IntPtr.Zero); // INSTALLUILEVEL_NONE
            Native.MsiEnableLog(0x3fff, LogPath, 2);
            var registered = Native.MsiSetExternalUIRecord(callback, 0x0507, IntPtr.Zero, out var previousCallback);
            if (registered != 0) throw new IOException("Cannot monitor Windows Installer");
            try { return Native.MsiInstallProduct(msi, "REBOOT=ReallySuppress REDROUTEOWNER=" + owner); }
            finally
            {
                Native.MsiSetExternalUIRecord(previousCallback, 0, IntPtr.Zero, out _); Native.MsiSetInternalUI(previousUi, IntPtr.Zero); Native.MsiEnableLog(0, null, 0); GC.KeepAlive(callback);
            }
        }
        finally
        {
            // Delete only the exact temporary payload created above; never recursively remove an installer directory.
            try
            {
                if (File.Exists(msi)) File.Delete(msi);
                if (Directory.Exists(folder)) Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Temp cleanup must not turn a successful installation into an error. */ }
        }
    }
    private int OnMessage(IntPtr context, uint type, uint record)
    {
        if (cancelled) return 2; // IDCANCEL: let MSI roll back normally.
        try
        {
            var category = type & 0xff000000;
            if (category == 0x08000000)
            {
                var action = Native.ReadString(record, 1);
                phase = action switch
                {
                    "InstallFiles" => "Копируем файлы RED_ROUTE…", "InstallServices" => "Настраиваем службу VPN…",
                    "StartServices" => "Запускаем службу подключения…", "RemoveExistingProducts" => "Обновляем установленную версию…",
                    "RegisterProduct" or "PublishProduct" => "Завершаем установку…", _ => phase
                };
            }
            else if (category == 0x0a000000)
            {
                var kind = Native.MsiRecordGetInteger(record, 1); var ticks = Native.MsiRecordGetInteger(record, 2);
                if (kind == 0) { total = Math.Max(ticks, 1); backwards = Native.MsiRecordGetInteger(record, 3) == 1; position = backwards ? total : 0; }
                else if (kind == 2) position += backwards ? -ticks : ticks;
                else if (kind == 3) total += ticks;
                if (total > 0) percent = Math.Max(percent, Math.Clamp((backwards ? total - position : position) * 90.0 / total + 5, 5, 95));
            }
            ProgressChanged?.Invoke(Math.Max(5, percent), phase);
        }
        catch { /* An observer must not abort a successful MSI transaction. */ }
        return 0;
    }
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int RecordHandler(IntPtr context, uint messageType, uint record);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] public static extern uint MsiInstallProduct(string path, string commandLine);
        [DllImport("msi.dll")] public static extern uint MsiSetInternalUI(uint level, IntPtr window);
        [DllImport("msi.dll")] public static extern uint MsiSetExternalUIRecord(RecordHandler? handler, uint filter, IntPtr context, out RecordHandler? previous);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] public static extern uint MsiEnableLog(uint mode, string? path, uint attributes);
        [DllImport("msi.dll")] public static extern int MsiRecordGetInteger(uint record, uint field);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);
        public static string ReadString(uint record, uint field) { var text = new StringBuilder(512); uint length = 511; return MsiRecordGetString(record, field, text, ref length) == 0 ? text.ToString() : ""; }
    }
}
