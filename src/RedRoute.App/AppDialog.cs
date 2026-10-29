using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace RedRoute.App;

internal sealed class AppDialog : Window
{
    public StackPanel Body { get; } = new() { Margin = new Thickness(24) };
    private readonly DockPanel root = new();
    public AppDialog(Window owner, string title, double width = 480)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = title; Width = width; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; ShowInTaskbar = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var caption = new Grid { Height = 32 }; caption.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        var captionText = new TextBlock { Text = "RED_ROUTE", FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; captionText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); caption.Children.Add(captionText);
        var close = new Button { Content = "\uE8BB", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("TitleButton") };
        System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть диалог");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => DialogResult = false;
        caption.Children.Add(close); DockPanel.SetDock(caption, Dock.Top); root.Children.Add(caption);
        Body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        root.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var frame = new Border { BorderThickness = new Thickness(1), Child = root }; frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); Content = frame;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
    }
    public void Describe(string message) { var text = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 18) }; text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(text); }
    public Button AddActions(string accept, Action action, bool acceptDefault = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 0, 24, 24) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = !acceptDefault, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = accept, IsDefault = acceptDefault, Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => action(); row.Children.Add(cancel); row.Children.Add(ok);
        DockPanel.SetDock(row, Dock.Bottom); root.Children.Insert(root.Children.Count - 1, row);
        if (!acceptDefault) Loaded += (_, _) => cancel.Focus();
        return ok;
    }
    public static bool Confirm(Window owner, string title, string message, string action)
    {
        var dialog = new AppDialog(owner, title); dialog.Describe(message);
        dialog.AddActions(action, () => dialog.DialogResult = true, false);
        return dialog.ShowDialog() == true;
    }
    public static string? Prompt(Window owner, string title, string value)
    {
        var dialog = new AppDialog(owner, title);
        var input = new TextBox { Text = value, MaxLength = 200 };
        dialog.Body.Children.Add(input); dialog.AddActions("Сохранить", () => dialog.DialogResult = true);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
