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
        var dark = 1;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        var caption = 0x00101010; var text = 0x00D9E4E8; var border = 0x00353535;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
