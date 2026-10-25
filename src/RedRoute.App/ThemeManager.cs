using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RedRoute.App;

internal enum ThemeChoice { System, Light, Dark }

// UI-only preference: changing appearance never sends a VPN configuration command.
internal static class ThemeManager
{
    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;
    public static bool IsDark { get; private set; }
    private static readonly string PreferenceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RED_ROUTE", "appearance.json");
    public static void Start(bool preview = false)
    {
        if (!preview)
        {
            try
            {
                Choice = Load(PreferenceFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Choice = ThemeChoice.System; }
        }
        if (preview)
        {
            if (Environment.GetCommandLineArgs().Contains("--theme-light")) Choice = ThemeChoice.Light;
            if (Environment.GetCommandLineArgs().Contains("--theme-dark")) Choice = ThemeChoice.Dark;
        }
        Apply();
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
    }
    internal static ThemeChoice Parse(string json)
    {
        try { return Enum.TryParse<ThemeChoice>(JsonSerializer.Deserialize<string>(json), out var choice) && Enum.IsDefined(choice) ? choice : ThemeChoice.System; }
        catch (JsonException) { return ThemeChoice.System; }
    }
    internal static ThemeChoice Load(string path) => new FileInfo(path) is { Exists: true, Length: < 1024 } ? Parse(File.ReadAllText(path)) : ThemeChoice.System;
    internal static void Save(string path, ThemeChoice choice)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(choice.ToString())); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Stop() => SystemEvents.UserPreferenceChanged -= OnSystemChanged;
    public static void Select(ThemeChoice choice, bool persist)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (persist)
        {
            Save(PreferenceFile, choice);
        }
        Choice = choice; Apply();
    }
    private static void OnSystemChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        var app = Application.Current;
        if (Choice == ThemeChoice.System && app != null && !app.Dispatcher.HasShutdownStarted)
            app.Dispatcher.BeginInvoke(new Action(Apply));
    }
    internal static bool Resolve(ThemeChoice choice, bool systemLight) => choice == ThemeChoice.Dark || choice == ThemeChoice.System && !systemLight;
    private static bool SystemLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int value || value != 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return true; }
    }
    private static void Apply()
    {
        IsDark = Resolve(Choice, SystemLight());
        var resources = Application.Current.Resources;
        var keys = new[] { "BackgroundBrush", "PanelBrush", "InkBrush", "MutedBrush", "AccentBrush", "AccentInkBrush", "BorderBrush", "ScrollBrush", "HoverBrush", "SelectedBrush", "TrackBrush", "SuccessBrush" };
        var colors = IsDark
            ? new[] { "#101010", "#181818", "#E8E4D9", "#AAA79F", "#F0443E", "#101010", "#45433F", "#77736C", "#292929", "#39201D", "#282828", "#8CDBAD" }
            : new[] { "#F5F3ED", "#FFFFFF", "#24221F", "#625D55", "#B92C27", "#FFFFFF", "#C5BFB5", "#8B857B", "#EAE5DC", "#F8DED8", "#DDD7CD", "#24643E" };
        for (var i = 0; i < keys.Length; i++) resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        foreach (var window in Application.Current.Windows.Cast<Window>()) WindowAppearance.Apply(window);
    }
}
