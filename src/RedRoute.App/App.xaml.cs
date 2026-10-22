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
        if (e.Args.Contains("--recover"))
        {
            await Recovery.RunAsync(); Shutdown(); return;
        }
        var smoke = e.Args.Contains("--smoke-test");
        if (!smoke)
        {
            singleton = new Mutex(true, "Local\\RED_ROUTE.App", out var created);
            if (!created) { MessageBox.Show("RED_ROUTE уже работает. Откройте его через значок в трее.", "RED_ROUTE"); Shutdown(); return; }
        }
        try
        {
            var settings = smoke ? new ClientSettings() : SecureStore.Load();
            var window = new MainWindow(settings, smoke); MainWindow = window;
            if (!e.Args.Contains("--tray") || smoke) window.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("Не удалось прочитать настройки RED_ROUTE. Они сохранены без изменений. Для отключения VPN используйте ярлык «Восстановить интернет».", "RED_ROUTE", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { singleton?.Dispose(); base.OnExit(e); }
}
