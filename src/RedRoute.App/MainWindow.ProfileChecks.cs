using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RedRoute.Core;

namespace RedRoute.App;

public partial class MainWindow
{
    private async Task ProfileImportSmokeAsync()
    {
        var previous = settings.Profiles;
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", "profile-import-fixture");
        Directory.CreateDirectory(folder);
        var key = Convert.ToBase64String(new byte[32]);
        var primary = $"[Interface]\nPrivateKey={key}\nAddress=192.0.2.1/32\n[Peer]\nPublicKey={key}\nEndpoint=127.0.0.1:9\nAllowedIPs=0.0.0.0/0\n";
        var second = primary.Replace("[Peer]", "Jc=4\nH1=101\nH2=201\nH3=301\nH4=401\nRandomTrailers=on\nDisableCookies=on\n[Peer]") + "PersistentKeepalive=25-35\n";
        var valid = Path.Combine(folder, "Mother-synthetic.conf");
        var invalid = Path.Combine(folder, "Invalid-synthetic.conf");
        await File.WriteAllTextAsync(valid, second);
        await File.WriteAllTextAsync(invalid, second.Replace("RandomTrailers=on", "RandomTrailers=maybe"));
        async Task TestModal(string path, bool success, string? expectedMessage, Func<string[], Task<string?>>? callback = null)
        {
            var picker = new ProfilePicker(this, folder, callback ?? ImportProfileFilesAsync);
            picker.PreviewWindow.MaxHeight = 420;
            Exception? failure = null;
            picker.PreviewWindow.ContentRendered += async (_, _) =>
            {
                try
                {
                    picker.Navigate(path); picker.PreviewWindow.UpdateLayout();
                    var point = picker.AcceptButton.TransformToAncestor(picker.PreviewWindow).Transform(new Point(0, 0));
                    if (point.Y < 0 || point.Y + picker.AcceptButton.ActualHeight > picker.PreviewWindow.ActualHeight)
                        throw new InvalidOperationException("Import action must remain visible on a short display");
                    picker.AcceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (picker.PreviewWindow.IsVisible && !picker.AcceptButton.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
                    if (success && picker.PreviewWindow.IsVisible) throw new InvalidOperationException("Valid second profile did not close the import window");
                    if (!success && (!picker.PreviewWindow.IsVisible || expectedMessage == null || !picker.Message.Contains(expectedMessage)))
                        throw new InvalidOperationException("Rejected import must stay open with an explicit reason");
                }
                catch (Exception ex) { failure = ex; }
                finally { if (picker.PreviewWindow.IsVisible) picker.PreviewWindow.Close(); }
            };
            var accepted = picker.ShowDialog();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (failure != null) throw failure;
            if (accepted != success) throw new InvalidOperationException("Import dialog returned the wrong result");
        }
        try
        {
            settings.Profiles = [new(Guid.NewGuid(), "Primary", primary)]; UpdateLists();
            await TestModal(valid, true, null);
            if (settings.Profiles.Count != 2 || ProfileItems.Items.Count != 2) throw new InvalidOperationException("Second profile missing from the actual profile list");
            await TestModal(valid, false, "уже добавлен");
            await TestModal(invalid, false, "RandomTrailers");
            if (settings.Profiles.Count != 2) throw new InvalidOperationException("Rejected import changed existing profiles");
            var pending = new TaskCompletionSource<string?>(); var calls = 0;
            var busyPicker = new ProfilePicker(this, folder, _ => { calls++; return pending.Task; });
            Exception? busyFailure = null;
            busyPicker.PreviewWindow.ContentRendered += async (_, _) =>
            {
                try
                {
                    busyPicker.Navigate(valid);
                    busyPicker.AcceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    busyPicker.AcceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    busyPicker.PreviewWindow.Close();
                    if (calls != 1 || !busyPicker.PreviewWindow.IsVisible) throw new InvalidOperationException("Double click/close must not race an import in progress");
                }
                catch (Exception ex) { busyFailure = ex; }
                finally { pending.TrySetResult(null); }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            };
            if (!busyPicker.ShowDialog() || busyFailure != null) throw busyFailure ?? new InvalidOperationException("Pending import did not complete safely");

        }
        finally { settings.Profiles = previous; UpdateLists(); Feedback.Visibility = Visibility.Collapsed; }
    }
}
