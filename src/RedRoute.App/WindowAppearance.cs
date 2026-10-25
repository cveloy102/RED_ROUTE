using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RedRoute.App;

internal static class WindowAppearance
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = ThemeManager.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        static int ColorRef(string key)
        {
            var color = ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource(key)).Color;
            return color.R | color.G << 8 | color.B << 16;
        }
        var caption = ColorRef("BackgroundBrush"); var text = ColorRef("InkBrush"); var border = ColorRef("BorderBrush");
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
