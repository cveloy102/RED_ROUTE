using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RedRoute.App;

namespace RedRoute.Setup;

public partial class SetupWindow : Window
{
    private bool installing, finished;
    private readonly InstallerWorker worker = new();
    private readonly bool updating = Environment.GetCommandLineArgs().Contains("--update");
    public SetupWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        VersionText.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        if (updating)
        {
            Title = "Обновление RED_ROUTE"; CaptionTitle.Text = Title;
            Heading.Text = "Обновление VPN-клиента"; Description.Text = "Обновим файлы приложения и службы. Профили, сайты и выбранный режим сохранятся.";
            Note.Text = "VPN ненадолго переподключится во время замены службы."; MainButton.Content = "Обновить";
        }
        worker.ProgressChanged += (percent, phase) => Dispatcher.BeginInvoke(() => { Progress.Value = percent; PercentText.Text = $"{percent:0} %"; PhaseText.Text = phase; });
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) Loaded += (_, _) => RenderPreview();
        else if (updating) Loaded += (_, _) => Main_Click(this, new RoutedEventArgs());
    }
    private void RenderPreview()
    {
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui"); Directory.CreateDirectory(folder);
        foreach (var state in new[] { "welcome", "progress", "success" })
        {
            if (state == "progress") { Heading.Text = "Устанавливаем RED_ROUTE"; Description.Text = "Это окно останется открытым до завершения."; ProgressArea.Visibility = Visibility.Visible; Progress.Value = 47; PercentText.Text = "47 %"; PhaseText.Text = "Копируем файлы RED_ROUTE…"; MainButton.IsEnabled = false; }
            if (state == "success") { Heading.Text = "RED_ROUTE установлен"; Description.Text = "Всё готово. Можно открыть приложение и добавить профиль подключения."; Progress.Value = 100; PercentText.Text = "100 %"; PhaseText.Text = "Установка завершена"; LaunchCheck.Visibility = Visibility.Visible; MainButton.IsEnabled = true; MainButton.Content = "Готово"; CancelButton.Visibility = Visibility.Collapsed; }
            UpdateLayout(); var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(folder, "setup-" + state + ".png")); png.Save(stream);
        }
        Close();
    }
    private async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (finished)
        {
            if (LaunchCheck.IsChecked == true)
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RED_ROUTE", "RED_ROUTE.exe");
                try
                {
                    // Delegate launch to the existing desktop shell, rather than inheriting installer's administrator token.
                    var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new Win32Exception();
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    try { shell.ShellExecute(path, "", "", "open", 1); }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                }
                catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
                { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            }
            Close(); return;
        }
        installing = true; worker.ResetCancellation(); MainButton.IsEnabled = false; ProgressArea.Visibility = Visibility.Visible;
        Heading.Text = updating ? "Обновляем RED_ROUTE" : "Устанавливаем RED_ROUTE"; Description.Text = "Это окно останется открытым до завершения. Обычно установка занимает меньше минуты.";
        Note.Text = "Не выключайте компьютер. Настройки и профили сохранятся при обновлении.";
        try
        {
            if (updating) await WaitForPreviousAppAsync();
            var code = await Task.Run(worker.Install);
            if (code is 0 or 3010)
            {
                finished = true; Heading.Text = updating ? "RED_ROUTE обновлён" : "RED_ROUTE установлен"; Description.Text = updating ? "Всё готово. Ваши профили и сайты сохранены." : "Всё готово. Можно открыть приложение и добавить профиль подключения.";
                Progress.Value = 100; PercentText.Text = "100 %"; PhaseText.Text = "Установка завершена";
                LaunchCheck.Visibility = Visibility.Visible; MainButton.Content = "Готово"; CancelButton.Visibility = Visibility.Collapsed;
                Note.Text = code == 3010 ? "Для завершения обновления Windows рекомендует перезагрузку." : "Профили подключения находятся на вкладке «Профили».";
            }
            else
            {
                Heading.Text = code == 1602 ? "Установка отменена" : "Не удалось установить RED_ROUTE";
                Description.Text = code == 1602 ? "Можно повторить установку, когда будет удобно." : $"Windows Installer вернул код {code}. Подробности записаны в журнал.";
                Note.Text = worker.LogPath; MainButton.Content = "Повторить"; CancelButton.Content = "Закрыть";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Heading.Text = "Не удалось начать установку"; Description.Text = "Проверьте свободное место и запустите установщик ещё раз."; Note.Text = worker.LogPath; MainButton.Content = "Повторить";
        }
        finally { installing = false; MainButton.IsEnabled = true; CancelButton.IsEnabled = true; }
    }
    private static async Task WaitForPreviousAppAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var pidFlag = Array.IndexOf(args, "--parent-pid"); var startFlag = Array.IndexOf(args, "--parent-start");
        if (pidFlag < 0 || startFlag < 0 || pidFlag + 1 >= args.Length || startFlag + 1 >= args.Length
            || !int.TryParse(args[pidFlag + 1], out var pid) || !long.TryParse(args[startFlag + 1], out var start)) return;
        try
        {
            using var parent = Process.GetProcessById(pid);
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RED_ROUTE", "RED_ROUTE.exe");
            if (parent.StartTime.ToUniversalTime().Ticks != start || !string.Equals(parent.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return;
            try { await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (System.TimeoutException) { throw new IOException("Закройте RED_ROUTE и повторите обновление."); }
        }
        catch (ArgumentException) { /* Parent has already exited. */ }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (installing) { worker.Cancel(); CancelButton.IsEnabled = false; PhaseText.Text = "Отменяем установку…"; } else Close(); }
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();
    private void Closing_Window(object? sender, CancelEventArgs e)
    {
        if (!installing) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "Отменить установку RED_ROUTE?", "RED_ROUTE", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { worker.Cancel(); CancelButton.IsEnabled = false; PhaseText.Text = "Отменяем установку…"; }
    }
}
