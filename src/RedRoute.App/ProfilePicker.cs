using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RedRoute.App;

internal sealed class ProfilePicker
{
    private sealed record Entry(string Path, bool Folder) { public string Label => (Folder ? "▸  " : "    ") + (System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path); }
    private readonly AppDialog dialog;
    private readonly TextBox address = new();
    private readonly TextBlock message = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly ListBox files = new() { Height = 280, SelectionMode = SelectionMode.Extended, DisplayMemberPath = "Label" };
    private string folder = "";
    private readonly Func<string[], System.Threading.Tasks.Task<string?>>? import;
    private readonly Button accept;
    private bool accepting, closed;
    public string[] FileNames { get; private set; } = [];
    internal Window PreviewWindow => dialog;
    public ProfilePicker(Window owner, string? initialDirectory = null, Func<string[], System.Threading.Tasks.Task<string?>>? import = null)
    {
        this.import = import;
        dialog = new AppDialog(owner, "Добавить профиль VPN", 620);
        dialog.Closing += (_, e) => { if (accepting) e.Cancel = true; };
        dialog.Closed += (_, _) => closed = true;
        dialog.Describe("Выберите файл .conf из AmneziaWG или WireGuard. Можно выделить несколько файлов с Ctrl.");
        message.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(message, System.Windows.Automation.AutomationLiveSetting.Polite);
        dialog.Body.Children.Add(message);
        var shortcuts = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        AddShortcut(shortcuts, "Загрузки", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddShortcut(shortcuts, "Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddShortcut(shortcuts, "Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var drives = new Button { Content = "Диски", Margin = new Thickness(0, 0, 6, 6) };
        drives.Click += (_, _) => { files.ItemsSource = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new Entry(d.RootDirectory.FullName, true)).ToArray(); address.Text = ""; };
        shortcuts.Children.Add(drives); dialog.Body.Children.Add(shortcuts);
        var navigation = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var up = new Button { Content = "↑", ToolTip = "На папку выше", Margin = new Thickness(0, 0, 8, 0) };
        up.Click += (_, _) => Navigate(Directory.GetParent(folder)?.FullName ?? folder);
        DockPanel.SetDock(up, Dock.Left); navigation.Children.Add(up); navigation.Children.Add(address);
        address.ToolTip = "Путь к папке или файлу .conf. Нажмите Enter.";
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Navigate(address.Text.Trim().Trim('"')); } };
        dialog.Body.Children.Add(navigation); dialog.Body.Children.Add(files);
        files.MouseDoubleClick += async (_, _) => { if (files.SelectedItem is Entry entry) { if (entry.Folder) Navigate(entry.Path); else await AcceptAsync(); } };
        files.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; if (files.SelectedItem is Entry { Folder: true } entry) Navigate(entry.Path); else await AcceptAsync(); } };
        accept = dialog.AddActions("Добавить профиль", async () => await AcceptAsync(), false);
        Navigate(initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }
    private void AddShortcut(Panel row, string name, string path)
    {
        var button = new Button { Content = name, Margin = new Thickness(0, 0, 6, 6) };
        button.Click += (_, _) => Navigate(path); row.Children.Add(button);
    }
    internal void Navigate(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var selectedFile = File.Exists(path) ? path : null;
            if (selectedFile != null) path = Path.GetDirectoryName(path)!;
            var entries = Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Select(d => new Entry(d, true))
                .Concat(Directory.EnumerateFiles(path, "*.conf").OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Select(f => new Entry(f, false))).ToArray();
            folder = path; address.Text = path; files.ItemsSource = entries;
            if (selectedFile != null) files.SelectedItem = entries.FirstOrDefault(e => e.Path.Equals(selectedFile, StringComparison.OrdinalIgnoreCase));
            message.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Explain("Не удалось открыть папку. Проверьте путь или выберите другую."); }
    }
    private void Explain(string text) { message.Text = text; message.BringIntoView(); }
    internal Button AcceptButton => accept;
    internal string Message => message.Text;
    private async System.Threading.Tasks.Task AcceptAsync()
    {
        if (accepting) return;
        FileNames = files.SelectedItems.Cast<Entry>().Where(e => !e.Folder).Select(e => e.Path).ToArray();
        if (FileNames.Length == 0) { Explain("Выберите один или несколько файлов .conf."); return; }
        accepting = true; accept.IsEnabled = false;
        try
        {
            Explain("Проверяем профиль…");
            var error = import == null ? null : await import(FileNames);
            if (error != null) { Explain(error); return; }
            if (!closed) { accepting = false; dialog.DialogResult = true; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        { Explain(ex is FormatException ? ex.Message : "Не удалось добавить профиль. Текущие настройки сохранены. Выберите другой файл или повторите попытку."); }
        finally { accepting = false; accept.IsEnabled = true; }
    }
    public bool ShowDialog() => dialog.ShowDialog() == true;
}
