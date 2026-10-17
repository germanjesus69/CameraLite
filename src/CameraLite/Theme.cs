using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CameraLite;

internal static class Theme
{
    // One calm, dark palette. Chrome frames the video; the stage stays near-black.
    internal static readonly Color Chrome = Color.FromArgb(21, 24, 29), Stage = Color.FromArgb(11, 13, 16), Raised = Color.FromArgb(31, 36, 43),
        Hover = Color.FromArgb(40, 47, 56), Pressed = Color.FromArgb(50, 58, 69), Border = Color.FromArgb(46, 53, 63), Line = Color.FromArgb(33, 38, 45),
        Text = Color.FromArgb(233, 237, 242), Secondary = Color.FromArgb(155, 165, 179), Muted = Color.FromArgb(98, 108, 122),
        Accent = Color.FromArgb(76, 211, 176), AccentHover = Color.FromArgb(104, 224, 193), OnAccent = Color.FromArgb(9, 28, 23),
        Red = Color.FromArgb(255, 107, 118), Danger = Color.FromArgb(222, 70, 82), Amber = Color.FromArgb(242, 184, 75);

    internal static readonly string IconFamily = InstalledOr("Segoe Fluent Icons", "Segoe MDL2 Assets");
    private static readonly Dictionary<(string, float), Font> fonts = [];
    internal static Font Font(float size, bool semibold = false) => Get(semibold ? "Segoe UI Semibold" : "Segoe UI", size);
    internal static Font Icons(float size) => Get(IconFamily, size);
    private static Font Get(string family, float size)
    {
        lock (fonts) { if (!fonts.TryGetValue((family, size), out var font)) fonts[(family, size)] = font = new(family, size); return font; }
    }
    private static string InstalledOr(string preferred, string fallback)
    {
        using var probe = new Font(preferred, 10); return probe.Name == preferred ? preferred : fallback;
    }
    internal static Label Label(string text, float size = 10, Color? color = null, bool semibold = false) => new() { Text = text, ForeColor = color ?? Text, Font = Font(size, semibold), AutoSize = true, BackColor = Color.Transparent };

    internal static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    internal static void FillRounded(Graphics g, Color color, RectangleF rect, float radius) { using var path = Rounded(rect, radius); using var brush = new SolidBrush(color); g.FillPath(brush, path); }
    internal static void DrawRounded(Graphics g, Color color, RectangleF rect, float radius, float width = 1) { using var path = Rounded(rect, radius); using var pen = new Pen(color, width); g.DrawPath(pen, path); }
    internal static Color Mix(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
    // Custom-painted controls clear to the first opaque ancestor colour, so rounded
    // corners never show stale pixels from the shared double buffer.
    internal static Color BackOf(Control? control)
    {
        for (; control != null; control = control.Parent) if (control.BackColor.A == 255) return control.BackColor;
        return Chrome;
    }
    internal static int S(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96d);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int length);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? app, string? idList);
    private static readonly bool windows11 = Environment.OSVersion.Version.Build >= 22000;
    internal static bool RoundedPopups => windows11;
    private static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
    private static void Attribute(IntPtr window, int attribute, int value) { try { DwmSetWindowAttribute(window, attribute, ref value, sizeof(int)); } catch (DllNotFoundException) { } }
    // Dark title bar in the app's chrome colour, so the caption blends into the window.
    internal static void Window(Form form)
    {
        form.HandleCreated += (_, _) =>
        {
            Attribute(form.Handle, 20, 1);
            if (!windows11) return;
            Attribute(form.Handle, 35, ColorRef(Chrome)); Attribute(form.Handle, 36, ColorRef(Secondary)); Attribute(form.Handle, 34, ColorRef(Line));
        };
    }
    internal static void Popup(IntPtr window)
    {
        if (!windows11) return;
        Attribute(window, 33, 2); Attribute(window, 34, ColorRef(Border));
    }
    internal static void DarkScrollbars(Control control) => control.HandleCreated += (_, _) => { try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch (DllNotFoundException) { } };

    internal static ToolTip ToolTips()
    {
        var tips = new ToolTip { OwnerDraw = true, AutoPopDelay = 6000, InitialDelay = 450, ReshowDelay = 100 };
        var font = Font(9);
        tips.Popup += (_, e) => { var size = TextRenderer.MeasureText(tips.GetToolTip(e.AssociatedControl), font); e.ToolTipSize = new(size.Width + 20, size.Height + 12); };
        tips.Draw += (_, e) =>
        {
            e.Graphics.Clear(Raised);
            using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, e.Bounds, Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        return tips;
    }
}

internal static class Glyphs
{
    internal const string Play = "", Pause = "", Stop = "", Cancel = "", Volume = "", Mute = "", Microphone = "",
        Camera = "", FullScreen = "", ExitFullScreen = "", Settings = "", Add = "", Edit = "",
        Delete = "", More = "", Pictures = "", ChevronDown = "", Video = "", Warning = "",
        Refresh = "", Search = "", Folder = "", Check = "";
}

// Dark context menus. On Windows 11 the popup gets system rounded corners and border.
internal static class Menus
{
    internal static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Theme.Font(10), BackColor = Theme.Raised, ForeColor = Theme.Text, Padding = new(4, 5, 4, 5), ShowImageMargin = true, ShowCheckMargin = false };
        menu.HandleCreated += (_, _) => Theme.Popup(menu.Handle);
        // Menus are built per use. Actions and disposal run after the menu has unwound,
        // so an action may open a modal dialog safely.
        menu.Closed += (_, _) => Later(menu.Dispose);
        return menu;
    }
    internal static ToolStripMenuItem Add(ContextMenuStrip menu, string text, string glyph, Action click, bool enabled = true, bool danger = false, bool check = false)
    {
        var item = new ToolStripMenuItem(text) { Tag = new MenuGlyph(glyph, danger), Enabled = enabled, Checked = check, Padding = new(0, 5, 18, 5) };
        item.Click += (_, _) => Later(click); menu.Items.Add(item); return item;
    }
    private static void Later(Action action) { if (SynchronizationContext.Current is { } context) context.Post(_ => action(), null); else action(); }
    internal static void Separator(ContextMenuStrip menu) => menu.Items.Add(new ToolStripSeparator());
    internal sealed record MenuGlyph(string Glyph, bool Danger);
    private sealed class DarkMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Raised);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (Theme.RoundedPopups) return;
            using var pen = new Pen(Theme.Border); e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRounded(e.Graphics, Theme.Hover, new RectangleF(2, 0, e.Item.Width - 4, e.Item.Height), 5);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Theme.Line); int y = e.Item.Height / 2; e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var info = e.Item.Tag as MenuGlyph;
            Color color = !e.Item.Enabled ? Theme.Muted : info?.Danger == true ? Theme.Red : Theme.Text;
            string glyph = e.Item is ToolStripMenuItem { Checked: true } ? Glyphs.Check : info?.Glyph ?? "";
            if (glyph.Length > 0)
            {
                var icon = new Rectangle(0, 0, e.TextRectangle.Left, e.Item.Height);
                TextRenderer.DrawText(e.Graphics, glyph, Theme.Icons(10), icon, glyph == Glyphs.Check && e.Item.Enabled ? Theme.Accent : color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }
    }
}
