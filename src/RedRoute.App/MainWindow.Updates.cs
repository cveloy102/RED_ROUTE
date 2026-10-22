using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RedRoute.Platform;

namespace RedRoute.App;

public partial class MainWindow
{
    private static readonly Version InstalledVersion = Assembly.GetExecutingAssembly().GetName().Version!;
    private readonly GitHubUpdates updater = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private ReleaseUpdate? availableUpdate;
    private bool checkingUpdates;
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private async System.Threading.Tasks.Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates || smoke) return; checkingUpdates = true;
        try
        {
            if (manual) UpdateStatus.Text = "Проверяем GitHub Releases…";
            availableUpdate = await updater.CheckAsync(InstalledVersion, lifetime.Token);
            UpdateButton.Visibility = availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateButton.Content = availableUpdate == null ? "" : $"Есть обновление {availableUpdate.Version.ToString(3)}";
            UpdateStatus.Text = availableUpdate == null ? "Установлена последняя доступная версия." : $"Доступна версия {availableUpdate.Version.ToString(3)}. Нажмите кнопку под номером версии.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        { if (manual) UpdateStatus.Text = "Не удалось проверить обновления. Повторите позже."; }
        finally { checkingUpdates = false; }
    }
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate == null) return;
        var dialog = new UpdateDialog(this, availableUpdate, updater, file =>
        {
            using var process = Process.GetCurrentProcess();
            var info = new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas" };
            info.ArgumentList.Add("--update"); info.ArgumentList.Add("--parent-pid"); info.ArgumentList.Add(Environment.ProcessId.ToString());
            info.ArgumentList.Add("--parent-start"); info.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString());
            if (Process.Start(info) == null) throw new IOException("Installer did not start");
            // Leave the VPN service running until Windows Installer performs its orderly upgrade.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Exit));
        });
        dialog.ShowDialog();
    }
}
