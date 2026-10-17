using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CameraLite;

internal enum HudAction { None, Mute, Volume, Talk, Pause, Snapshot, Quality, Fullscreen }

// Floating HUD above the native video window. LibVLC renders into its own HWND, so
// WinForms controls cannot be layered over it cleanly; this is a per-pixel-alpha
// window that never takes activation. It fades in on mouse movement and away when idle.
internal sealed class VideoOverlay : Form
{
    internal string CameraName = "", Stats = "", Elapsed = "", QualityText = "", TalkHint = "";
    internal bool Muted, FullscreenMode, TalkOpening, TalkLive, TalkEnabled, SnapshotEnabled, QualityEnabled, Paused, PauseEnabled;
    internal int Volume;
    internal float MicLevel;
    internal bool Holding;
    public event Action<HudAction>? Command;
    public event Action? TalkPressed, TalkReleased, ToggleRequested;
    public event Action<int, bool>? VolumeRequested;

    private readonly Form owner;
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 100 }, animation = new() { Interval = 30 };
    private readonly List<(Rectangle Bounds, HudAction Action, string Tip)> targets = [];
    private Rectangle area, sliderTrack;
    private float bars, barsTarget, toastAlpha, flash;
    private string toast = "";
    private DateTime toastUntil, lastMove = DateTime.MinValue;
    private Point lastCursor;
    private HudAction hover, pressed;
    private bool dragging, talkHeld, cursorHidden, drewContent;
    private IntPtr memoryDc, dib, previous, bits;
    private Size surface;

    internal VideoOverlay(Form owner)
    {
        this.owner = owner;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Text = "CameraLite overlay";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        clock.Tick += (_, _) => Tick(); animation.Tick += (_, _) => Animate();
    }
    protected override CreateParams CreateParams
    {
        // Layered, never activated, not in Alt+Tab.
        get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x8000000 | 0x80; return p; }
    }
    protected override bool ShowWithoutActivation => true;
    private float Dpi => owner.DeviceDpi / 96f;
    private int Px(float value) => (int)Math.Round(value * Dpi);

    internal void Track(Rectangle screenArea, bool show)
    {
        bool visible = show && screenArea.Width > 0 && screenArea.Height > 0 && owner.WindowState != FormWindowState.Minimized;
        if (!visible) { if (Visible) Hide(); clock.Stop(); return; }
        bool resized = screenArea.Size != area.Size, moved = screenArea.Location != area.Location; area = screenArea;
        if (!Visible) { Show(owner); clock.Start(); Reveal(); }
        else if (resized) Render();
        else if (moved) Location = area.Location; // The surface is unchanged; only move the window.
    }
    internal void Reveal()
    {
        lastMove = DateTime.UtcNow; barsTarget = 1;
        if (bars < 1) animation.Start(); else Render();
    }
    internal void Toast(string text) { toast = text; toastUntil = DateTime.UtcNow.AddSeconds(3.5); toastAlpha = 1; animation.Start(); Render(); }
    internal void Flash() { flash = 1; animation.Start(); }
    // A faded HUD with no badge, toast or flash is an unchanged transparent surface,
    // so state changes then skip the full-surface render (one last clear excepted).
    internal void Redraw() { if (Visible && (HasContent || drewContent)) Render(); }
    private bool HasContent => bars > 0 || barsTarget > 0 || toast.Length > 0 || flash > 0 || Paused || TalkOpening || TalkLive;
    internal Rectangle ScreenBoundsOf(HudAction action)
    {
        foreach (var target in targets) if (target.Action == action) return new(area.Left + target.Bounds.Left, area.Top + target.Bounds.Top, target.Bounds.Width, target.Bounds.Height);
        return new(area.Left + area.Width / 2, area.Bottom - Px(80), 1, 1);
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        bool keep = hover != HudAction.None || pressed != HudAction.None || dragging || talkHeld || Holding;
        bool inside = area.Contains(Cursor.Position);
        if (!keep && barsTarget > 0 && (now - lastMove > TimeSpan.FromSeconds(2.6) || !inside && now - lastMove > TimeSpan.FromMilliseconds(700))) { barsTarget = 0; animation.Start(); }
        if (toast.Length > 0 && now > toastUntil) animation.Start();
    }
    private void Animate()
    {
        bool busy = false;
        if (bars != barsTarget) { bars = barsTarget > bars ? Math.Min(barsTarget, bars + .28f) : Math.Max(barsTarget, bars - .18f); busy = true; }
        if (toast.Length > 0 && DateTime.UtcNow > toastUntil) { toastAlpha = Math.Max(0, toastAlpha - .16f); if (toastAlpha == 0) toast = ""; else busy = true; }
        if (flash > 0) { flash = Math.Max(0, flash - .24f); busy = true; }
        if (!busy) animation.Stop();
        if (bars == 0 && FullscreenMode && area.Contains(Cursor.Position) && !TalkLive) { cursorHidden = true; SetCursor(IntPtr.Zero); }
        Render();
    }

    private void Render()
    {
        if (!IsHandleCreated || area.Width <= 0 || area.Height <= 0) return;
        EnsureSurface(area.Size);
        using (var bitmap = new Bitmap(surface.Width, surface.Height, surface.Width * 4, PixelFormat.Format32bppPArgb, bits))
        using (var g = Graphics.FromImage(bitmap))
        {
            // Alpha 1 everywhere keeps the whole video clickable (move to reveal, double-click
            // for fullscreen) while staying invisible.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Draw(g, surface.Width, surface.Height);
        }
        drewContent = HasContent;
        var position = area.Location; var size = surface; var source = Point.Empty;
        var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref position, ref size, memoryDc, ref source, 0, ref blend, 2);
    }
    private void EnsureSurface(Size size)
    {
        if (size == surface && dib != IntPtr.Zero) return;
        ReleaseSurface();
        if (memoryDc == IntPtr.Zero) memoryDc = CreateCompatibleDC(IntPtr.Zero);
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
        dib = CreateDIBSection(memoryDc, ref header, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) throw new OutOfMemoryException("Overlay surface could not be created.");
        previous = SelectObject(memoryDc, dib); surface = size;
    }
    private void ReleaseSurface()
    {
        if (dib == IntPtr.Zero) return;
        SelectObject(memoryDc, previous); DeleteObject(dib); dib = IntPtr.Zero; bits = IntPtr.Zero; surface = Size.Empty;
    }

    private void Draw(Graphics g, int w, int h)
    {
        targets.Clear();
        if (flash > 0) { using var white = new SolidBrush(Color.FromArgb((int)(110 * flash), 255, 255, 255)); g.FillRectangle(white, 0, 0, w, h); }
        float a = Ease(bars);
        int margin = Px(20);
        if (a > 0)
        {
            int scrim = Px(FullscreenMode ? 120 : 96);
            using (var top = new LinearGradientBrush(new Rectangle(0, 0, w, scrim + 1), Color.FromArgb((int)(150 * a), 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical)) g.FillRectangle(top, 0, 0, w, scrim);
            // Live pill and camera name.
            var pillFont = HudFont(11, true); var nameFont = HudFont(15, true); var statsFont = HudFont(12.5f);
            string live = Paused ? "PAUSED" : "LIVE"; var liveSize = g.MeasureString(live, pillFont, PointF.Empty, StringFormat.GenericTypographic);
            int pillH = Px(24), dot = Px(7), pillW = (int)liveSize.Width + dot + Px(26);
            var pill = new RectangleF(margin, margin, pillW, pillH);
            Pill(g, pill, Color.FromArgb((int)(150 * a), 12, 14, 18));
            Fill(g, Color.FromArgb((int)(255 * a), Paused ? Theme.Amber : Theme.Accent), new RectangleF(pill.Left + Px(10), pill.Top + (pillH - dot) / 2f, dot, dot), true);
            Write(g, live, pillFont, Color.FromArgb((int)(255 * a), Theme.Text), new PointF(pill.Left + Px(10) + dot + Px(7), pill.Top + (pillH - liveSize.Height) / 2f));
            var nameSize = g.MeasureString(CameraName, nameFont, PointF.Empty, StringFormat.GenericTypographic);
            float statsWidth = g.MeasureString(StatsLine, statsFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            float nameRoom = Math.Max(0, w - margin * 2 - pillW - Px(12) - statsWidth - Px(24));
            Write(g, Ellipsize(g, CameraName, nameFont, nameRoom), nameFont, Color.FromArgb((int)(255 * a), 255, 255, 255), new PointF(pill.Right + Px(12), pill.Top + (pillH - nameSize.Height) / 2f));
            // Stream details on the right.
            var statsSize = g.MeasureString(StatsLine, statsFont, PointF.Empty, StringFormat.GenericTypographic);
            if (statsWidth < w - margin * 2 - pillW - Px(60))
                Write(g, StatsLine, statsFont, Color.FromArgb((int)(215 * a), 230, 235, 242), new PointF(w - margin - statsSize.Width, pill.Top + (pillH - statsSize.Height) / 2f));
            if (FullscreenMode) DrawControls(g, w, h, a);
        }
        int badgeTop = Px(18);
        if (Paused) { DrawPaused(g, w, badgeTop); badgeTop += Px(42); }
        if (TalkOpening || TalkLive) DrawTalk(g, w, badgeTop);
        if (toast.Length > 0 && toastAlpha > 0)
        {
            var font = HudFont(12.5f); var size = g.MeasureString(toast, font, PointF.Empty, StringFormat.GenericTypographic);
            float tw = Math.Min(w - Px(40), size.Width + Px(36)), th = Px(38);
            float bottom = FullscreenMode && a > 0 ? h - Px(108) : h - Px(24);
            var box = new RectangleF((w - tw) / 2, bottom - th, tw, th);
            Pill(g, box, Color.FromArgb((int)(225 * toastAlpha), 20, 24, 30));
            Write(g, Ellipsize(g, toast, font, tw - Px(36)), font, Color.FromArgb((int)(255 * toastAlpha), Theme.Text), new PointF(box.Left + Px(18), box.Top + (th - size.Height) / 2f));
        }
    }
    private string StatsLine => Stats.Length == 0 ? Elapsed : Elapsed.Length == 0 ? Stats : Stats + "   " + Elapsed;

    private void DrawControls(Graphics g, int w, int h, float a)
    {
        int scrim = Px(150);
        using (var bottom = new LinearGradientBrush(new Rectangle(0, h - scrim - 1, w, scrim + 1), Color.FromArgb(0, 0, 0, 0), Color.FromArgb((int)(170 * a), 0, 0, 0), LinearGradientMode.Vertical)) g.FillRectangle(bottom, 0, h - scrim, w, scrim);
        int item = Px(40), gap = Px(6), pad = Px(8), slider = Px(110), divider = Px(17);
        var labelFont = HudFont(13, true);
        string talkText = TalkLive ? "Talking" : TalkOpening ? "Opening…" : "Hold to talk";
        int talkWidth = (int)g.MeasureString("Hold to talk", labelFont, PointF.Empty, StringFormat.GenericTypographic).Width + Px(64);
        string quality = QualityText.Length == 0 ? "Quality" : QualityText;
        int qualityWidth = (int)g.MeasureString(quality, labelFont, PointF.Empty, StringFormat.GenericTypographic).Width + Px(40);
        int total = pad * 2 + item + slider + Px(44) + divider + talkWidth + divider + item + gap + item + gap + qualityWidth + gap + item;
        var bar = new Rectangle((w - total) / 2, h - Px(24) - item - pad * 2, total, item + pad * 2);
        Pill(g, bar, Color.FromArgb((int)(205 * a), 22, 26, 32), Px(14));
        using (var edge = new Pen(Color.FromArgb((int)(28 * a), 255, 255, 255))) { using var path = Theme.Rounded(new RectangleF(bar.Left + .5f, bar.Top + .5f, bar.Width - 1, bar.Height - 1), Px(14)); g.DrawPath(edge, path); }
        int x = bar.Left + pad, y = bar.Top + pad;
        IconButton(g, new(x, y, item, item), HudAction.Mute, Muted ? Glyphs.Mute : Glyphs.Volume, true, a, Muted ? "Unmute (M)" : "Mute (M)"); x += item;
        // Volume slider.
        sliderTrack = new Rectangle(x + Px(8), y + item / 2 - Px(2), slider - Px(16), Px(4));
        targets.Add((new Rectangle(x, y, slider, item), HudAction.Volume, ""));
        Fill(g, Color.FromArgb((int)(70 * a), 255, 255, 255), sliderTrack, false, Px(2));
        float knob = sliderTrack.Left + sliderTrack.Width * Volume / 100f;
        if (knob > sliderTrack.Left) Fill(g, Color.FromArgb((int)(255 * a), Muted ? Theme.Secondary : Theme.Accent), new RectangleF(sliderTrack.Left, sliderTrack.Top, knob - sliderTrack.Left, sliderTrack.Height), false, Px(2));
        float r = Px(hover == HudAction.Volume || dragging ? 8 : 7);
        Fill(g, Color.FromArgb((int)(255 * a), 255, 255, 255), new RectangleF(knob - r, y + item / 2f - r, r * 2, r * 2), true);
        x += slider;
        var smallFont = HudFont(12);
        Write(g, Volume + "%", smallFont, Color.FromArgb((int)(200 * a), 230, 235, 242), new PointF(x + Px(2), y + (item - g.MeasureString("0", smallFont, PointF.Empty, StringFormat.GenericTypographic).Height) / 2f));
        x += Px(44); Divider(g, x + divider / 2, y + Px(10), item - Px(20), a); x += divider;
        // Hold to talk.
        var talkBox = new Rectangle(x, y, talkWidth, item);
        targets.Add((talkBox, HudAction.Talk, TalkHint));
        Color talkFill = TalkLive ? Color.FromArgb((int)(255 * a), Theme.Danger) : !TalkEnabled ? Color.Empty : pressed == HudAction.Talk ? Color.FromArgb((int)(60 * a), 255, 255, 255) : hover == HudAction.Talk ? Color.FromArgb((int)(36 * a), 255, 255, 255) : Color.FromArgb((int)(20 * a), 255, 255, 255);
        if (!talkFill.IsEmpty) Pill(g, talkBox, talkFill, Px(10));
        int fore = TalkEnabled || TalkLive ? 255 : 95;
        var iconFont = HudIcons(15);
        var iconSize = g.MeasureString(Glyphs.Microphone, iconFont, PointF.Empty, StringFormat.GenericTypographic);
        var labelSize = g.MeasureString(talkText, labelFont, PointF.Empty, StringFormat.GenericTypographic);
        float tx = talkBox.Left + Px(16);
        Write(g, Glyphs.Microphone, iconFont, Color.FromArgb((int)(fore * a), 255, 255, 255), new PointF(tx, talkBox.Top + (item - iconSize.Height) / 2f));
        Write(g, talkText, labelFont, Color.FromArgb((int)(fore * a), 255, 255, 255), new PointF(tx + iconSize.Width + Px(9), talkBox.Top + (item - labelSize.Height) / 2f));
        if (TalkLive) Meter(g, new PointF(talkBox.Right - Px(26), talkBox.Top + item / 2f + Px(8)), a);
        x += talkWidth; Divider(g, x + divider / 2, y + Px(10), item - Px(20), a); x += divider;
        IconButton(g, new(x, y, item, item), HudAction.Pause, Paused ? Glyphs.Play : Glyphs.Pause, PauseEnabled, a, Paused ? "Resume (Space)" : "Pause (Space)"); x += item + gap;
        IconButton(g, new(x, y, item, item), HudAction.Snapshot, Glyphs.Camera, SnapshotEnabled, a, "Snapshot (F9)"); x += item + gap;
        // Quality menu.
        var qualityBox = new Rectangle(x, y, qualityWidth, item);
        targets.Add((qualityBox, HudAction.Quality, "Video quality"));
        if (QualityEnabled && (hover == HudAction.Quality || pressed == HudAction.Quality)) Pill(g, qualityBox, Color.FromArgb((int)((pressed == HudAction.Quality ? 60 : 36) * a), 255, 255, 255), Px(10));
        int qAlpha = QualityEnabled ? 255 : 95;
        var qSize = g.MeasureString(quality, labelFont, PointF.Empty, StringFormat.GenericTypographic);
        Write(g, quality, labelFont, Color.FromArgb((int)(qAlpha * a), 255, 255, 255), new PointF(qualityBox.Left + Px(14), qualityBox.Top + (item - qSize.Height) / 2f));
        var chevron = HudIcons(9); var cSize = g.MeasureString(Glyphs.ChevronDown, chevron, PointF.Empty, StringFormat.GenericTypographic);
        Write(g, Glyphs.ChevronDown, chevron, Color.FromArgb((int)(qAlpha * .75f * a), 255, 255, 255), new PointF(qualityBox.Right - Px(12) - cSize.Width, qualityBox.Top + (item - cSize.Height) / 2f + Px(1)));
        x += qualityWidth + gap;
        IconButton(g, new(x, y, item, item), HudAction.Fullscreen, Glyphs.ExitFullScreen, true, a, "Exit fullscreen (Esc)");
        // Tooltip for the hovered icon.
        foreach (var target in targets)
        {
            if (target.Action != hover || target.Tip.Length == 0 || pressed != HudAction.None) continue;
            var font = HudFont(11.5f); var size = g.MeasureString(target.Tip, font, PointF.Empty, StringFormat.GenericTypographic);
            var tip = new RectangleF(target.Bounds.Left + target.Bounds.Width / 2f - size.Width / 2 - Px(10), bar.Top - Px(36), size.Width + Px(20), Px(28));
            tip.X = Math.Clamp(tip.X, Px(8), w - tip.Width - Px(8));
            Pill(g, tip, Color.FromArgb((int)(235 * a), 22, 26, 32), Px(7));
            Write(g, target.Tip, font, Color.FromArgb((int)(255 * a), Theme.Text), new PointF(tip.Left + Px(10), tip.Top + (tip.Height - size.Height) / 2f));
        }
    }
    // Stays visible while paused, so a frozen picture is never mistaken for live video.
    private void DrawPaused(Graphics g, int w, int top)
    {
        var font = HudFont(12.5f, true); var icon = HudIcons(12);
        const string text = "Paused · press Space to resume";
        var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        var iconSize = g.MeasureString(Glyphs.Pause, icon, PointF.Empty, StringFormat.GenericTypographic);
        float pw = size.Width + iconSize.Width + Px(46), ph = Px(34);
        var pill = new RectangleF((w - pw) / 2, top, pw, ph);
        Pill(g, pill, Color.FromArgb(225, 22, 26, 32));
        Write(g, Glyphs.Pause, icon, Theme.Amber, new PointF(pill.Left + Px(18), pill.Top + (ph - iconSize.Height) / 2f));
        Write(g, text, font, Color.White, new PointF(pill.Left + Px(18) + iconSize.Width + Px(10), pill.Top + (ph - size.Height) / 2f));
    }
    private void DrawTalk(Graphics g, int w, int top)
    {
        var font = HudFont(12.5f, true);
        string text = TalkLive ? "Microphone live · release to stop" : "Opening talk…";
        var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        float pw = size.Width + Px(TalkLive ? 72 : 36), ph = Px(34);
        var pill = new RectangleF((w - pw) / 2, top, pw, ph);
        Pill(g, pill, TalkLive ? Color.FromArgb(240, Theme.Danger) : Color.FromArgb(220, 22, 26, 32));
        float x = pill.Left + Px(18);
        if (TalkLive) { Fill(g, Color.White, new RectangleF(x, pill.Top + (ph - Px(8)) / 2, Px(8), Px(8)), true); x += Px(16); }
        Write(g, text, font, Color.White, new PointF(x, pill.Top + (ph - size.Height) / 2f));
        if (TalkLive) Meter(g, new PointF(pill.Right - Px(30), pill.Top + ph / 2f + Px(8)), 1);
    }
    private void Meter(Graphics g, PointF origin, float a)
    {
        float lit = MathF.Sqrt(Math.Clamp(MicLevel, 0, 1));
        for (int i = 0; i < 4; i++)
        {
            float bh = Px(6 + i * 3), bw = Px(3);
            using var brush = new SolidBrush(Color.FromArgb((int)((lit > (i + 1) / 5f ? 255 : 90) * a), 255, 255, 255));
            g.FillRectangle(brush, origin.X + i * (bw + Px(2)), origin.Y - bh, bw, bh);
        }
    }
    private void IconButton(Graphics g, Rectangle bounds, HudAction action, string glyph, bool enabled, float a, string tip)
    {
        targets.Add((bounds, enabled ? action : HudAction.None, tip));
        if (enabled && (hover == action || pressed == action)) Pill(g, bounds, Color.FromArgb((int)((pressed == action ? 60 : 36) * a), 255, 255, 255), Px(10));
        var font = HudIcons(15); var size = g.MeasureString(glyph, font, PointF.Empty, StringFormat.GenericTypographic);
        Write(g, glyph, font, Color.FromArgb((int)((enabled ? 255 : 95) * a), 255, 255, 255), new PointF(bounds.Left + (bounds.Width - size.Width) / 2f, bounds.Top + (bounds.Height - size.Height) / 2f));
    }
    private void Divider(Graphics g, int x, int y, int height, float a) { using var pen = new Pen(Color.FromArgb((int)(40 * a), 255, 255, 255)); g.DrawLine(pen, x, y, x, y + height); }
    private static float Ease(float t) => t * t * (3 - 2 * t);
    private void Pill(Graphics g, RectangleF rect, Color color, float radius = -1) => Fill(g, color, rect, false, radius < 0 ? rect.Height / 2 : radius);
    private static void Fill(Graphics g, Color color, RectangleF rect, bool ellipse, float radius = 0)
    {
        using var brush = new SolidBrush(color);
        if (ellipse) g.FillEllipse(brush, rect); else { using var path = Theme.Rounded(rect, radius); g.FillPath(brush, path); }
    }
    private static void Write(Graphics g, string text, Font font, Color color, PointF at) { using var brush = new SolidBrush(color); g.DrawString(text, font, brush, at, StringFormat.GenericTypographic); }
    private static string Ellipsize(Graphics g, string text, Font font, float width)
    {
        if (width <= 0) return "";
        if (g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= width) return text;
        for (int n = text.Length - 1; n > 0; n--) { string s = text[..n].TrimEnd() + "…"; if (g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width <= width) return s; }
        return "";
    }
    private readonly Dictionary<(string, float, float), Font> fonts = [];
    private Font HudFont(float px, bool semibold = false) => FontFor(semibold ? "Segoe UI Semibold" : "Segoe UI", px);
    private Font HudIcons(float px) => FontFor(Theme.IconFamily, px);
    private Font FontFor(string family, float px)
    {
        if (!fonts.TryGetValue((family, px, Dpi), out var font)) fonts[(family, px, Dpi)] = font = new(family, px * Dpi, GraphicsUnit.Pixel);
        return font;
    }

    private HudAction HitTest(Point p)
    {
        if (!FullscreenMode || bars < .5f) return HudAction.None;
        foreach (var target in targets) if (target.Bounds.Contains(p)) return target.Action;
        return HudAction.None;
    }
    private void SetVolumeFromX(int x, bool commit)
    {
        int value = (int)Math.Round(Math.Clamp((x - sliderTrack.Left) / (float)Math.Max(1, sliderTrack.Width), 0, 1) * 100);
        VolumeRequested?.Invoke(value, commit);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = Cursor.Position;
        if (screen != lastCursor) { lastCursor = screen; cursorHidden = false; if (bars < 1 || barsTarget < 1) Reveal(); else lastMove = DateTime.UtcNow; }
        if (dragging) { SetVolumeFromX(e.X, false); return; }
        var hit = HitTest(e.Location);
        if (hit != hover) { hover = hit; Cursor = hit == HudAction.None ? Cursors.Default : Cursors.Hand; Render(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { if (hover != HudAction.None) { hover = HudAction.None; Render(); } base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        pressed = HitTest(e.Location);
        if (pressed == HudAction.Volume) { dragging = true; Capture = true; SetVolumeFromX(e.X, false); }
        else if (pressed == HudAction.Talk) { if (TalkEnabled) { talkHeld = true; Capture = true; TalkPressed?.Invoke(); } }
        Render();
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        var released = pressed; pressed = HudAction.None;
        if (dragging) { dragging = false; Capture = false; SetVolumeFromX(e.X, true); }
        else if (talkHeld) { talkHeld = false; Capture = false; TalkReleased?.Invoke(); }
        else if (released != HudAction.None && released == HitTest(e.Location)) Command?.Invoke(released);
        Render();
        base.OnMouseUp(e);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture && talkHeld) { talkHeld = false; pressed = HudAction.None; TalkReleased?.Invoke(); }
        if (!Capture) dragging = false;
        base.OnMouseCaptureChanged(e);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left && HitTest(e.Location) == HudAction.None) ToggleRequested?.Invoke(); base.OnMouseDoubleClick(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { VolumeRequested?.Invoke(Math.Clamp(Volume + Math.Sign(e.Delta) * 5, 0, 100), true); Reveal(); base.OnMouseWheel(e); }
    protected override void WndProc(ref Message m)
    {
        const int MouseActivate = 0x21, SetCursorMessage = 0x20;
        if (m.Msg == MouseActivate) { m.Result = 3; return; } // MA_NOACTIVATE
        if (m.Msg == SetCursorMessage && cursorHidden && FullscreenMode && bars == 0) { SetCursor(IntPtr.Zero); m.Result = 1; return; }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { clock.Dispose(); animation.Dispose(); foreach (var font in fonts.Values) font.Dispose(); }
        ReleaseSurface(); if (memoryDc != IntPtr.Zero) { DeleteDC(memoryDc); memoryDc = IntPtr.Zero; }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screenDc, ref Point position, ref Size size, IntPtr sourceDc, ref Point source, int key, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
