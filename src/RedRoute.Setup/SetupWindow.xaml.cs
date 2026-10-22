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
    public SetupWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        VersionText.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        worker.ProgressChanged += (percent, phase) => Dispatcher.BeginInvoke(() => { Progress.Value = percent; PercentText.Text = $"{percent:0} %"; PhaseText.Text = phase; });
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) Loaded += (_, _) => RenderPreview();
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
        installing = true; MainButton.IsEnabled = false; ProgressArea.Visibility = Visibility.Visible;
        Heading.Text = "Устанавливаем RED_ROUTE"; Description.Text = "Это окно останется открытым до завершения. Обычно установка занимает меньше минуты.";
        Note.Text = "Не выключайте компьютер. Настройки и профили сохранятся при обновлении.";
        try
        {
            var code = await Task.Run(worker.Install);
            if (code is 0 or 3010)
            {
                finished = true; Heading.Text = "RED_ROUTE установлен"; Description.Text = "Всё готово. Можно открыть приложение и добавить профиль подключения.";
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
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (installing) { worker.Cancel(); CancelButton.IsEnabled = false; PhaseText.Text = "Отменяем установку…"; } else Close(); }
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();
    private void Closing_Window(object? sender, CancelEventArgs e)
    {
        if (!installing) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "Отменить установку RED_ROUTE?", "RED_ROUTE", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { worker.Cancel(); CancelButton.IsEnabled = false; PhaseText.Text = "Отменяем установку…"; }
    }
}
