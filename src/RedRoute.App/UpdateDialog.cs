using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RedRoute.Platform;

namespace RedRoute.App;

internal sealed class UpdateDialog
{
    private readonly AppDialog dialog;
    private readonly ReleaseUpdate release;
    private readonly GitHubUpdates updater;
    private readonly Action<string> install;
    private readonly CancellationTokenSource cancel = new();
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock state = new() { Margin = new Thickness(0, 18, 0, 0) };
    private readonly Button start;
    public Window PreviewWindow => dialog;
    public UpdateDialog(Window owner, ReleaseUpdate release, GitHubUpdates updater, Action<string> install)
    {
        this.release = release; this.updater = updater; this.install = install;
        dialog = new AppDialog(owner, "Обновление RED_ROUTE", 540);
        dialog.Describe($"Доступна версия {release.Version.ToString(3)} · {release.Size / 1048576.0:0.#} МБ\n\nПрофили и список сайтов сохранятся. Во время замены службы VPN ненадолго переподключится.");
        if (!string.IsNullOrWhiteSpace(release.Notes)) dialog.Body.Children.Add(new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = release.Notes, FontSize = 13 } });
        state.Text = "Установщик будет скачан с GitHub и проверен перед запуском.";
        dialog.Body.Children.Add(state); dialog.Body.Children.Add(progress);
        start = dialog.AddActions("Скачать и обновить", () => _ = DownloadAsync());
        dialog.Closed += (_, _) => cancel.Cancel();
    }
    public void Preview(double percent) { progress.Value = percent; state.Text = $"Скачиваем обновление… {percent:0} %"; start.IsEnabled = false; }
    private async Task DownloadAsync()
    {
        start.IsEnabled = false;
        try
        {
            var file = await updater.DownloadAsync(release, Path.Combine(SecureStore.UserDirectory, "Updates"), new Progress<double>(value =>
            {
                progress.Value = value; state.Text = value < 95 ? $"Скачиваем обновление… {value / 95 * 100:0} %" : value < 100 ? "Проверяем установщик…" : "Обновление готово к установке";
            }), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            install(file); dialog.DialogResult = true;
        }
        catch (OperationCanceledException) { if (dialog.IsVisible) state.Text = "Загрузка отменена. Текущая версия продолжает работать."; }
        catch (System.ComponentModel.Win32Exception ex) { state.Text = ex.NativeErrorCode == 1223 ? "Запуск обновления отменён. Текущая версия продолжает работать." : "Не удалось открыть установщик. Повторите попытку."; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException) { state.Text = "Не удалось скачать или проверить обновление. Повторите попытку позже."; }
        finally { start.IsEnabled = true; }
    }
    public void ShowDialog() => dialog.ShowDialog();
}
