using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RedRoute.Core;
using RedRoute.Platform;
using Forms = System.Windows.Forms;

namespace RedRoute.App;

public partial class MainWindow : Window
{
    private readonly ClientSettings settings;
    private readonly bool smoke;
    private readonly ServiceCatalog catalog = new();
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource lifetime = new();
    private EngineStatus? status;
    private bool exiting, polling, refreshingCatalog;
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private RouteMode? requestedMode;
    private DateTimeOffset modeRequestDeadline;
    private DateTimeOffset catalogAttempt = DateTimeOffset.MinValue;
    private sealed record SiteRow(SiteRule Rule) { public string Name => Rule.Name; public string Description => Rule.ServiceId == null ? Rule.IpRanges.Length > 0 ? "IP-адреса и подсети" : "Сайт и его поддомены" : $"Сервис · доменов: {Rule.Domains.Length}, подсетей: {Rule.IpRanges.Length}"; }
    private sealed record ProfileRow(VpnProfile Profile, int Position) { public string Title => $"{Position + 1}. {Profile.Name}"; public string Description => Position == 0 ? "Основной · подключаемся первым" : "Резерв · при недоступности предыдущего"; }
    private sealed record SuggestionRow(CatalogChoice Choice, bool Added) { public bool CanAdd => !Added; public string ActionText => Added ? "✓ Добавлено" : "+ Добавить"; }
    private bool choosingSuggestion;
    private void ShowSuggestions()
    {
        if (Suggestions == null || choosingSuggestion) return;
        var addedDomains = settings.Sites.SelectMany(s => s.Domains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedServices = settings.Sites.Where(s => s.ServiceId != null).Select(s => s.ServiceId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = catalog.Search(DomainInput.Text).Select(choice => new SuggestionRow(choice,
            choice.IsService ? addedServices.Contains(choice.Rule.ServiceId!) : addedDomains.Contains(choice.Title))).ToArray();
        Suggestions.ItemsSource = matches; SearchEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPopup.IsOpen = true;
    }
    private void Domain_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => ShowSuggestions();
    private void Domain_MouseDown(object sender, MouseButtonEventArgs e) => ShowSuggestions();
    private void Domain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && (DomainInput.IsKeyboardFocusWithin || smoke)) ShowSuggestions();
    }
    private async void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SuggestionRow row || row.Added) return;
        if (!TryAddRule(row.Choice.Rule)) return;
        choosingSuggestion = true;
        try { DomainInput.Clear(); SearchPopup.IsOpen = false; await ApplyAsync(); Notice($"Добавлен{(row.Choice.IsService ? " сервис" : " домен")} «{row.Choice.Title}»."); }
        finally { choosingSuggestion = false; }
    }
    private bool TryAddRule(SiteRule rule)
    {
        try { new ClientSettings { Sites = [.. settings.Sites, rule] }.Validate(); settings.Sites.Add(rule); return true; }
        catch (FormatException ex) { Notice(ex.Message); return false; }
    }
    private void Suggestions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; DomainInput.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && Suggestions.SelectedItem is SuggestionRow row) { e.Handled = true; Suggestion_Click(new Button { DataContext = row }, new RoutedEventArgs()); }
    }
    public MainWindow(ClientSettings settings, bool smoke, bool preview = false)
    {
        InitializeComponent(); this.settings = settings; this.smoke = smoke;
        VersionText.Text = InstalledVersion.ToString(3);
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        tray = new Forms.NotifyIcon { Text = "RED_ROUTE · VPN выключен", Icon = TrayIcon.Create(RouteMode.Off, ConnectionPhase.Off), Visible = !smoke };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenWindow);
        UpdateLists(); UpdateModeButtons(); ShowPage("home");
        StartupCheck.IsChecked = settings.StartWithWindows; FailoverCheck.IsChecked = settings.AutoFailover;
        UpdateTray();
        timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) =>
        {
            if (smoke) { if (!preview) await SmokeTestAsync(); return; }
            timer.Start(); await PollAsync(); _ = CheckUpdatesAsync(false); updateTimer.Start();
        };
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(false);
        if (!smoke) timer.Start();
    }
    private void OpenWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void TitleMin_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void TitleMax_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();
    private void ShowPage(string name)
    {
        HomePage.Visibility = name == "home" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = name == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = name == "help" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, active) in new[] { (HomeTab, name == "home"), (ProfilesTab, name == "profiles"), (HelpTab, name == "help") }) button.BorderBrush = active ? Brush("#F0443E") : Brush("#353535");
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void Home_Click(object sender, RoutedEventArgs e) => ShowPage("home");
    private void Profiles_Click(object sender, RoutedEventArgs e) => ShowPage("profiles");
    private void Help_Click(object sender, RoutedEventArgs e) => ShowPage("help");
    private async void On_Click(object sender, RoutedEventArgs e) => await SetModeAsync(RouteMode.On);
    private async void Off_Click(object sender, RoutedEventArgs e) => await SetModeAsync(RouteMode.Off);
    private async void Auto_Click(object sender, RoutedEventArgs e) => await SetModeAsync(RouteMode.Auto);
    private async Task SetModeAsync(RouteMode mode)
    {
        if (mode != RouteMode.Off && settings.Profiles.Count == 0) { ShowPage("profiles"); Notice("Сначала добавьте файл .conf с настройками VPN."); return; }
        settings.Mode = mode; UpdateModeButtons();
        await ApplyAsync();
    }
    private void UpdateModeButtons()
    {
        foreach (var (button, mode) in new[] { (OnButton, RouteMode.On), (OffButton, RouteMode.Off), (AutoButton, RouteMode.Auto) })
        {
            button.BorderBrush = Brush(settings.Mode == mode ? "#F0443E" : "#353535");
            button.Background = Brush(settings.Mode == mode ? "#271918" : "#181818");
        }
    }
    private void UpdateLists()
    {
        SiteItems.ItemsSource = settings.Sites.Select(s => new SiteRow(s)).ToArray();
        ProfileItems.ItemsSource = settings.Profiles.Select((p, i) => new ProfileRow(p, i)).ToArray();
        EmptySites.Visibility = settings.Sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DatabaseText.Text = settings.DatabaseUpdated is { } date ? $"База обновлена: {date.LocalDateTime:g}" : "База сервисов ещё не загружена";
    }
    private void Notice(string text) { Feedback.Text = text; Feedback.Visibility = Visibility.Visible; }
    private async Task ApplyAsync(bool preserveMode = false)
    {
        if (smoke) { UpdateLists(); return; }
        var held = false;
        try
        {
            await applyGate.WaitAsync(lifetime.Token); held = true;
            SecureStore.Save(settings);
            if (!preserveMode) { requestedMode = settings.Mode; modeRequestDeadline = DateTimeOffset.UtcNow.AddSeconds(30); }
            var response = await PipeProtocol.CallAsync(new(preserveMode ? "update-rules" : "configure", settings), lifetime.Token);
            if (!response.Success) { Notice(response.Error ?? "Не удалось применить настройки."); return; }
            Notice("Настройки сохранены. Состояние подключения обновится автоматически.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Notice("Настройки сохранены локально. Служба недоступна: установите RED_ROUTE через установщик или запустите восстановление.");
        }
        finally { if (held) applyGate.Release(); UpdateLists(); UpdateModeButtons(); }
    }
    private async Task PollAsync()
    {
        if (polling || smoke || exiting) return; polling = true;
        try
        {
            var response = await PipeProtocol.CallAsync(new("status"), lifetime.Token);
            if (response.Status is { } s)
            {
                status = s;
                if (applyGate.CurrentCount == 1 && (requestedMode == null || requestedMode == s.Mode || DateTimeOffset.UtcNow >= modeRequestDeadline))
                {
                    requestedMode = null;
                    if (settings.Mode != s.Mode)
                    {
                        settings.Mode = s.Mode; UpdateModeButtons();
                        try { SecureStore.Save(settings); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { Notice("Текущий режим работает, но не удалось сохранить его локально."); }
                    }
                }
                StatusTitle.Text = s.Message;
                StatusTitle.Foreground = Brush(s.Phase == ConnectionPhase.Connected ? "#8CDBAD" : "#E8E4D9");
                StatusDetail.Text = s.Phase switch
                {
                    ConnectionPhase.Connected when s.Mode == RouteMode.On => "Все сайты и приложения работают через VPN.",
                    ConnectionPhase.Connected => "Через VPN — только сайты и сервисы из списка. Остальное напрямую.",
                    ConnectionPhase.Connecting => "Проверяем связь с сервером. До подключения интернет работает напрямую.",
                    _ => "Интернет работает напрямую. Ничего не блокируем."
                };
                DiagnosticText.Text = $"Состояние: {s.Message}\nПоследняя успешная проверка: {(s.LastSuccess is { } date ? date.LocalDateTime.ToString("G") : "ещё не выполнялась")}\nНеудачных проверок подряд: {s.ConsecutiveFailures}";
                if (s.Checks is { Count: > 0 }) DiagnosticText.Text += "\n\n" + string.Join("\n", s.Checks.Select(c => $"{c.Target}: {(c.Success ? "доступен" : "ошибка")} · {c.Milliseconds} мс · {c.Result}"));
                EventsBox.Text = string.Join(Environment.NewLine, s.Events.Reverse().Select(item => $"{item.Time.LocalDateTime:HH:mm:ss} [{item.Code}] {item.Message}"));
                UpdateTray();
            }
            if (DateTimeOffset.UtcNow - catalogAttempt > TimeSpan.FromHours(1) && (catalog.Updated == null || DateTimeOffset.UtcNow - catalog.Updated > TimeSpan.FromDays(1)))
                _ = RefreshCatalogAsync(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            StatusTitle.Text = "Служба RED_ROUTE недоступна";
            StatusDetail.Text = "Установите приложение через установщик. Если интернет пропал — нажмите восстановление ниже.";
            status = null; UpdateTray();
        }
        finally { polling = false; }
    }
    private void UpdateTray()
    {
        var phase = status?.Phase ?? (settings.Mode == RouteMode.Off ? ConnectionPhase.Off : ConnectionPhase.Error);
        var mode = status?.Mode ?? settings.Mode;
        var previous = tray.Icon; tray.Icon = TrayIcon.Create(mode, phase); previous?.Dispose();
        var caption = phase switch { ConnectionPhase.Connected => mode == RouteMode.On ? "Вкл · весь интернет через VPN" : "Авто · только из списка", ConnectionPhase.Connecting => "Подключение · пока напрямую", ConnectionPhase.Off => "Выкл · интернет напрямую", _ => "VPN недоступен · интернет напрямую" };
        tray.Text = ("RED_ROUTE · " + caption)[..Math.Min(63, ("RED_ROUTE · " + caption).Length)];
        var menu = new Forms.ContextMenuStrip { BackColor = System.Drawing.Color.FromArgb(24, 24, 24), ForeColor = System.Drawing.Color.FromArgb(232, 228, 217), ShowImageMargin = true };
        menu.Items.Add(new Forms.ToolStripMenuItem(caption) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var (m, title) in new[] { (RouteMode.On, "Вкл — весь интернет через VPN"), (RouteMode.Off, "Выкл — интернет напрямую"), (RouteMode.Auto, "Авто — через VPN только из списка") })
        {
            using var icon = TrayIcon.Create(m, ConnectionPhase.Connected, true);
            var item = new Forms.ToolStripMenuItem(title, icon.ToBitmap()) { Checked = mode == m };
            item.Click += async (_, _) => await Dispatcher.InvokeAsync(async () => await SetModeAsync(m)).Task.Unwrap(); menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть RED_ROUTE", null, (_, _) => Dispatcher.Invoke(OpenWindow));
        menu.Items.Add("Восстановить обычный интернет", null, async (_, _) => await Dispatcher.InvokeAsync(RecoverAsync).Task.Unwrap());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Закрыть окно — оставить VPN", null, (_, _) => Dispatcher.Invoke(Hide));
        menu.Items.Add("Выключить VPN и выйти", null, async (_, _) => await Dispatcher.InvokeAsync(async () => { if (await RecoverAsync()) Exit(); }).Task.Unwrap());
        var old = tray.ContextMenuStrip;
        // Do not replace an open native menu while the user is selecting a command.
        if (old?.Visible == true) { menu.Dispose(); return; }
        tray.ContextMenuStrip = menu;
        if (old != null) { foreach (Forms.ToolStripItem item in old.Items) item.Image?.Dispose(); old.Dispose(); }
    }
    private async void AddSite_Click(object sender, RoutedEventArgs e) => await AddDomainAsync();
    private async void Domain_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Down && SearchPopup.IsOpen) { Suggestions.Focus(); if (Suggestions.Items.Count > 0) Suggestions.SelectedIndex = 0; e.Handled = true; }
        else if (e.Key == Key.Enter) { e.Handled = true; await AddDomainAsync(); }
    }
    private async Task AddDomainAsync()
    {
        try
        {
            var domain = Domains.Normalize(DomainInput.Text);
            var service = catalog.Find(domain);
            SiteRule rule = new(domain, [domain], []);
            if (service != null && MessageBox.Show(this, $"Добавить весь сервис «{service.Name}» вместе со связанными сайтами?\n\n«Нет» — добавить только {domain}.", "RED_ROUTE", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) is var result)
            {
                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes) rule = service;
            }
            if (settings.Sites.Any(s => (rule.ServiceId != null && s.ServiceId == rule.ServiceId) || s.Domains.Contains(domain))) { Notice("Этот сайт или сервис уже есть в списке."); return; }
            if (!TryAddRule(rule)) return;
            choosingSuggestion = true; DomainInput.Clear(); SearchPopup.IsOpen = false; choosingSuggestion = false; await ApplyAsync();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { Notice(ex.Message); }
    }
    private async void RemoveSite_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is SiteRow row) { settings.Sites.Remove(row.Rule); await ApplyAsync(); } }
    private async void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfilePicker(this);
        if (!dialog.ShowDialog()) return;
        try
        {
            var imported = new List<VpnProfile>();
            foreach (var path in dialog.FileNames)
            {
                if (new FileInfo(path).Length > 65536) throw new FormatException("Слишком большой .conf.");
                var config = await File.ReadAllTextAsync(path); _ = ConfParser.Parse(config);
                if (settings.Profiles.Any(p => p.Config.Trim() == config.Trim()) || imported.Any(p => p.Config.Trim() == config.Trim())) continue;
                imported.Add(new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(path), config));
            }
            var candidate = new ClientSettings { Profiles = settings.Profiles.Concat(imported).ToList(), Sites = settings.Sites }; candidate.Validate();
            settings.Profiles.AddRange(imported); await ApplyAsync(); Notice($"Добавлено профилей: {imported.Count}. Первый в списке — основной.");
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { Notice(ex is FormatException ? ex.Message : "Не удалось прочитать файл профиля."); }
    }
    private async void MakePrimary_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is ProfileRow row) { settings.Profiles.Remove(row.Profile); settings.Profiles.Insert(0, row.Profile); await ApplyAsync(); } }
    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await MoveProfileAsync(sender, -1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await MoveProfileAsync(sender, 1);
    private async Task MoveProfileAsync(object sender, int step)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row) return;
        var target = row.Position + step; if (target < 0 || target >= settings.Profiles.Count) return;
        settings.Profiles.RemoveAt(row.Position); settings.Profiles.Insert(target, row.Profile); await ApplyAsync();
    }
    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row) return;
        var name = Prompt("Название профиля", row.Profile.Name); if (string.IsNullOrWhiteSpace(name)) return;
        if (name.Length > 80 || name.Any(char.IsControl)) { Notice("Название должно содержать от 1 до 80 символов."); return; }
        settings.Profiles[row.Position] = row.Profile with { Name = name }; await ApplyAsync();
    }
    private async void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row) return;
        if (!AppDialog.Confirm(this, "Удалить профиль?", $"Профиль «{row.Profile.Name}» будет удалён из RED_ROUTE. Исходный файл .conf останется."
            + (settings.Profiles.Count == 1 ? " Это последний профиль: VPN будет выключен." : row.Position == 0 ? " Основным станет следующий профиль в списке." : ""), "Удалить профиль")) return;
        settings.Profiles.Remove(row.Profile); if (settings.Profiles.Count == 0) settings.Mode = RouteMode.Off; await ApplyAsync();
    }
    private async void Failover_Click(object sender, RoutedEventArgs e) { settings.AutoFailover = FailoverCheck.IsChecked == true; await ApplyAsync(); }
    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        AdvancedPanel.Visibility = AdvancedPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (AdvancedPanel.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { UpdateLayout(); AdvancedPanel.BringIntoView(); }));
    }
    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync(true);
    private async Task RefreshCatalogAsync(bool manual)
    {
        if (refreshingCatalog || smoke) return; refreshingCatalog = true; catalogAttempt = DateTimeOffset.UtcNow;
        try
        {
            if (manual) Notice("Обновляем базу сервисов…");
            await catalog.RefreshAsync(settings.Sites, lifetime.Token);
            for (var i = 0; i < settings.Sites.Count; i++)
            {
                var old = settings.Sites[i]; if (old.ServiceId != null && old.Domains.FirstOrDefault() is { } first && catalog.Find(first) is { } updated && updated.ServiceId == old.ServiceId) settings.Sites[i] = updated;
            }
            settings.DatabaseUpdated = catalog.Updated; SecureStore.Save(settings); UpdateLists();
            if (settings.Mode != RouteMode.Off) await ApplyAsync(preserveMode: true);
            if (manual) Notice("База обновлена. Собственные правила сохранены.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or FormatException)
        { if (manual) Notice("Не удалось обновить базу. Продолжаем использовать предыдущую версию."); }
        finally { refreshingCatalog = false; }
    }
    private async void AddIp_Click(object sender, RoutedEventArgs e)
    {
        var input = Prompt("IP-адрес или подсеть", ""); if (input == null) return;
        try { var cidr = Domains.NormalizeCidr(input); if (!settings.Sites.Any(s => s.IpRanges.Contains(cidr))) { settings.Sites.Add(new(cidr, [], [cidr])); await ApplyAsync(); } }
        catch (FormatException ex) { Notice(ex.Message); }
    }
    private async void ExportRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Правила RED_ROUTE (*.json)|*.json", FileName = "RED_ROUTE-rules.json" }; if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(settings.Sites, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true })); Notice("Правила сохранены. Профили и ключи в файл не включены."); } catch (IOException) { Notice("Не удалось сохранить правила."); }
    }
    private async void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Правила RED_ROUTE (*.json)|*.json" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new FormatException();
            var sites = JsonSerializer.Deserialize<List<SiteRule>>(await File.ReadAllTextAsync(dialog.FileName), Protocol.Json) ?? throw new FormatException();
            new ClientSettings { Sites = sites }.Validate();
            if (MessageBox.Show(this, $"Заменить текущий список на {sites.Count} правил из файла?", "RED_ROUTE", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            settings.Sites = sites; await ApplyAsync();
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or NullReferenceException) { Notice("Не удалось прочитать правила. Текущий список сохранён."); }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            settings.StartWithWindows = StartupCheck.IsChecked == true;
            if (settings.StartWithWindows) key.SetValue("RED_ROUTE", $"\"{Environment.ProcessPath}\" --tray"); else key.DeleteValue("RED_ROUTE", false);
            SecureStore.Save(settings);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Notice("Не удалось изменить автозапуск."); }
    }
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        try { var response = await PipeProtocol.CallAsync(new("check"), lifetime.Token); Notice(response.Success ? "Проверка запрошена. Результат появится в событиях." : response.Error ?? "Проверка недоступна."); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { Notice("Служба недоступна. Используйте восстановление интернета."); }
        await PollAsync();
    }
    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Отчёт RED_ROUTE (*.json)|*.json", FileName = $"RED_ROUTE-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            var report = new
            {
                App = "RED_ROUTE", Version = "0.1.8", Time = DateTimeOffset.Now, OS = Environment.OSVersion.VersionString,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                Status = status, Profiles = settings.Profiles.Select((p, i) => new { Position = i + 1, p.Name, Protocol = ConfParser.Parse(p.Config).IsAmnezia ? "AmneziaWG" : "WireGuard" }),
                SiteRuleCount = settings.Sites.Count, DatabaseUpdated = settings.DatabaseUpdated,
                Interfaces = NetworkDiagnostics.Interfaces(), Routes = await NetworkDiagnostics.RoutesAsync(lifetime.Token),
                EnginePresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "service", "engine", "amnezia-box.exe"))
            };
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true })); Notice("Отчёт сохранён. Его можно передать для помощи.");
        }
        catch (Exception ex) when (ex is IOException or FormatException) { Notice("Не удалось сохранить отчёт."); }
    }
    private async void Recover_Click(object sender, RoutedEventArgs e) => await RecoverAsync();
    private async Task<bool> RecoverAsync()
    {
        settings.Mode = RouteMode.Off;
        if (!smoke) SecureStore.Save(settings);
        UpdateModeButtons();
        if (smoke || await Recovery.RunAsync()) { Notice("Команда восстановления выполнена. VPN выключен."); await PollAsync(); return true; }
        return false;
    }
    private string? Prompt(string title, string value)
    {
        return AppDialog.Prompt(this, title, value);
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    private void Exit() { exiting = true; lifetime.Cancel(); timer.Stop(); updateTimer.Stop(); SearchPopup.IsOpen = false; tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); Close(); System.Windows.Application.Current.Shutdown(); }
    private async Task SmokeTestAsync()
    {
        DomainInput.Text = "https://example.org/path"; await AddDomainAsync();
        if (settings.Sites.Count != 1 || settings.Sites[0].Domains[0] != "example.org") throw new InvalidOperationException("Domain UI failed");
        DomainInput.Text = "localhost"; await AddDomainAsync(); if (settings.Sites.Count != 1) throw new InvalidOperationException("Validation failed");
        // Exercise only presentation here. Never call recovery or a service command in a UI smoke test.
        DomainInput.Clear(); settings.Mode = RouteMode.Off; UpdateModeButtons(); ShowPage("home"); Feedback.Visibility = Visibility.Collapsed;
        Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui"));
        Advanced_Click(this, new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var advancedLocation = AdvancedPanel.TransformToAncestor(MainScroll).Transform(new Point(0, 0));
        if (advancedLocation.Y < -1 || advancedLocation.Y + AdvancedPanel.ActualHeight > MainScroll.ActualHeight + 1)
            throw new InvalidOperationException("Advanced settings must be visible after opening");
        AdvancedPanel.Visibility = Visibility.Collapsed; MainScroll.ScrollToTop();
        UpdateLayout();
        void Render(Window window, string name)
        {
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", name + ".png")); png.Save(stream);
        }
        DomainInput.Focus(); ShowSuggestions(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!SearchPopup.IsOpen || Suggestions.Items.Count < 20000) throw new InvalidOperationException("Empty search must show the complete catalogue");
        void RenderPopup(string name)
        {
            var popup = (FrameworkElement)SearchPopup.Child; popup.UpdateLayout();
            var image = new RenderTargetBitmap((int)popup.ActualWidth, (int)popup.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(popup);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", name + ".png")); png.Save(output);
        }
        RenderPopup("catalog-all");
        DomainInput.Text = "dis"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!SearchPopup.IsOpen || !Suggestions.Items.Cast<SuggestionRow>().Any(s => s.Choice.IsService && s.Choice.Rule.ServiceId == "discord") || !Suggestions.Items.Cast<SuggestionRow>().Any(s => !s.Choice.IsService && s.Choice.Title == "discord.com")) throw new InvalidOperationException("Service/domain suggestions failed");
        RenderPopup("suggestions-popup");
        var suggested = Suggestions.Items.Cast<SuggestionRow>().First(s => s.Choice.IsService && s.Choice.Rule.ServiceId == "discord");
        Suggestion_Click(new Button { DataContext = suggested }, new RoutedEventArgs());
        if (settings.Sites.Count != 2 || settings.Sites.Last().ServiceId != "discord" || DomainInput.Text.Length != 0 || SearchPopup.IsOpen) throw new InvalidOperationException("Service suggestion selection failed");
        ShowSuggestions();
        if (!Suggestions.Items.Cast<SuggestionRow>().Any(s => s.Choice.Rule.ServiceId == "discord" && s.Added)) throw new InvalidOperationException("Already added services must remain visible");
        SearchPopup.IsOpen = false; settings.Sites.RemoveAt(1); UpdateLists(); Feedback.Visibility = Visibility.Collapsed;
        availableUpdate = new RedRoute.Platform.ReleaseUpdate(new Version(0, 2, 0), "Новая версия приложения. Профили и правила сохранятся.", new Uri("https://github.com/cveloy102/RED_ROUTE/releases/tag/v0.2.0"), new Uri("https://github.com/cveloy102/RED_ROUTE/releases/download/v0.2.0/RED_ROUTE-0.2.0-Setup.exe"), 150000000, new string('0', 64));
        UpdateButton.Content = "Есть обновление 0.2.0"; UpdateButton.Visibility = Visibility.Visible; Render(this, "update-available");
        var updatePreview = new UpdateDialog(this, availableUpdate, updater, _ => throw new InvalidOperationException("Smoke test must never run an installer"));
        updatePreview.Preview(47); updatePreview.PreviewWindow.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Render(updatePreview.PreviewWindow, "update-progress"); updatePreview.PreviewWindow.Close();
        availableUpdate = null; UpdateButton.Visibility = Visibility.Collapsed;
        var sampleFolder = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", "sample-profiles"); Directory.CreateDirectory(sampleFolder);
        File.WriteAllText(Path.Combine(sampleFolder, "Основной.conf"), "# UI preview only; not a VPN profile");
        var picker = new ProfilePicker(this, sampleFolder).PreviewWindow;
        picker.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Render(picker, "profile-import"); picker.Close();
        var confirmation = new AppDialog(this, "Удалить профиль?"); confirmation.Describe("Профиль «Основной» будет удалён из RED_ROUTE. Исходный файл .conf останется.");
        confirmation.AddActions("Удалить профиль", () => confirmation.Close(), false);
        confirmation.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Render(confirmation, "profile-delete"); confirmation.Close();
        foreach (var (name, page) in new[] { ("main", "home"), ("profiles", "profiles"), ("help", "help") })
        {
            ShowPage(page); UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", name + ".png")); png.Save(stream);
        }
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", "result.txt"), "PASS: native WPF rendering, domain normalization, invalid domain rejection, service suggestions/selection, complete dropdown catalogue, already-added entries, themed update/import/deletion dialogs. No service, recovery, or network commands executed.");
        Exit();
    }
}
