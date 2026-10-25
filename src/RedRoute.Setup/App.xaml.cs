using System.Windows;
using RedRoute.App;
namespace RedRoute.Setup;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e) { ThemeManager.Start(e.Args.Contains("--smoke-test")); base.OnStartup(e); }
    protected override void OnExit(ExitEventArgs e) { ThemeManager.Stop(); base.OnExit(e); }
}
