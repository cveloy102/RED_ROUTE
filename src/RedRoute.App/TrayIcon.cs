using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RedRoute.Core;

namespace RedRoute.App;

internal static class TrayIcon
{
    public static Icon Create(RouteMode mode, ConnectionPhase phase, bool forMenu = false)
    {
        using var bitmap = new Bitmap(32, 32); using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var lightTaskbar = forMenu ? !ThemeManager.IsDark : Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int light && light == 1;
        using var pen = new Pen(lightTaskbar ? Color.FromArgb(30, 30, 30) : Color.WhiteSmoke, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (phase is ConnectionPhase.Direct or ConnectionPhase.Error)
        {
            g.DrawPolygon(pen, [new Point(16, 3), new Point(30, 28), new Point(2, 28)]); g.DrawLine(pen, 16, 11, 16, 19); using var dot = new SolidBrush(pen.Color); g.FillEllipse(dot, 14.5f, 23, 3, 3);
        }
        else if (phase == ConnectionPhase.Connecting) g.DrawArc(pen, 5, 5, 22, 22, 0, 280);
        else if (mode == RouteMode.Off) { g.DrawArc(pen, 5, 5, 22, 22, -50, 280); g.DrawLine(pen, 16, 2, 16, 15); }
        else if (mode == RouteMode.On) { g.DrawPolygon(pen, [new Point(16, 3), new Point(27, 7), new Point(25, 22), new Point(16, 29), new Point(7, 22), new Point(5, 7)]); g.DrawLines(pen, [new Point(10, 16), new Point(14, 20), new Point(22, 11)]); }
        else { g.DrawLine(pen, 16, 29, 16, 17); g.DrawLines(pen, [new Point(5, 6), new Point(5, 12), new Point(16, 20), new Point(27, 12), new Point(27, 6)]); g.DrawLines(pen, [new Point(1, 10), new Point(5, 5), new Point(10, 9)]); g.DrawLines(pen, [new Point(22, 9), new Point(27, 5), new Point(31, 10)]); }
        var handle = bitmap.GetHicon(); try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); } finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
