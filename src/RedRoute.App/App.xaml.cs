using System;
using System.Linq;
using System.Threading;
using System.Windows;
using RedRoute.Core;
using RedRoute.Platform;

namespace RedRoute.App;

public partial class App : Application
{
    private Mutex? singleton;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeRun = e.Args.Contains("--smoke-test") || e.Args.Contains("--preview");
        // Event handlers are async void. An unexpected exception must not silently close the tray application
        // (the VPN service keeps running and the user would lose the only control surface).
        if (!smokeRun) DispatcherUnhandledException += (_, args) => { args.Handled = true; ReportUnexpected(args.Exception); };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        ThemeManager.Start(e.Args.Contains("--smoke-test") || e.Args.Contains("--preview"));
        if (e.Args.Contains("--recover"))
        {
            await Recovery.RunAsync(); Shutdown(); return;
        }
        var preview = e.Args.Contains("--preview");
        var smoke = e.Args.Contains("--smoke-test") || preview;
        if (!smoke)
        {
            singleton = new Mutex(true, "Local\\RED_ROUTE.App", out var created);
            if (!created) { MessageBox.Show("RED_ROUTE уже работает. Откройте его через значок в трее.", "RED_ROUTE"); Shutdown(); return; }
        }
        try
        {
            var settings = smoke ? new ClientSettings() : SecureStore.Load();
            var window = new MainWindow(settings, smoke, preview); MainWindow = window;
            if (!e.Args.Contains("--tray") || smoke) window.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("Не удалось прочитать настройки RED_ROUTE. Они сохранены без изменений. Для отключения VPN используйте ярлык «Восстановить интернет».", "RED_ROUTE", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }
    private bool reporting;
    private void ReportUnexpected(Exception exception)
    {
        if (exception is OutOfMemoryException) Environment.FailFast("RED_ROUTE UI out of memory", exception);
        try
        {
            // Type and stack only: exception messages may contain paths or profile data.
            var folder = System.IO.Path.Combine(SecureStore.UserDirectory, "Logs"); System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "ui-errors.log"), $"{DateTimeOffset.Now:O} {exception.GetType().FullName}{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        if (reporting) return; reporting = true;
        try { MessageBox.Show("Действие не выполнено из-за непредвиденной ошибки (" + exception.GetType().Name + "). RED_ROUTE продолжает работать; подробности сохранены в папке Logs.", "RED_ROUTE", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { reporting = false; }
    }
    protected override void OnExit(ExitEventArgs e) { ThemeManager.Stop(); singleton?.Dispose(); base.OnExit(e); }
}
