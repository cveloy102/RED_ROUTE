using System.Drawing;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace RedRoute.App;

internal static class TrayTheme
{
    internal static System.Drawing.Color Color(string key)
    {
        var c = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
        return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
    }
    internal sealed class Palette : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => Color("PanelBrush");
        public override System.Drawing.Color ImageMarginGradientBegin => Color("PanelBrush");
        public override System.Drawing.Color ImageMarginGradientMiddle => Color("PanelBrush");
        public override System.Drawing.Color ImageMarginGradientEnd => Color("PanelBrush");
        public override System.Drawing.Color MenuItemSelected => Color("SelectedBrush");
        public override System.Drawing.Color MenuItemBorder => Color("AccentBrush");
        public override System.Drawing.Color MenuBorder => Color("BorderBrush");
        public override System.Drawing.Color SeparatorDark => Color("BorderBrush");
        public override System.Drawing.Color SeparatorLight => Color("PanelBrush");
        public override System.Drawing.Color CheckBackground => Color("SelectedBrush");
        public override System.Drawing.Color CheckSelectedBackground => Color("SelectedBrush");
    }
    internal sealed class Renderer : Forms.ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Palette()) { RoundedEdges = false; }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color(e.Item.Enabled ? "InkBrush" : "MutedBrush"); base.OnRenderItemText(e);
        }
    }
}
