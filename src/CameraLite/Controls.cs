using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace CameraLite;

internal enum ButtonKind { Secondary, Primary, Ghost, Nav }

internal sealed class ActionButton : Button
{
    private ButtonKind kind;
    private bool alert, hovered, pressed;
    private string glyph;
    private float level = -1;
    private const TextFormatFlags Centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
    internal ActionButton(string text, string glyph = "", ButtonKind kind = ButtonKind.Secondary, int width = 0)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand; Margin = new(0);
        Text = text; this.glyph = glyph; this.kind = kind; Font = Theme.Font(10, kind is ButtonKind.Primary or ButtonKind.Secondary); Height = 38;
        Width = width > 0 ? width : PreferredWidth();
        if (text.Length == 0) AccessibleName = glyph;
    }
    internal static ActionButton Icon(string glyph, string name, ButtonKind kind = ButtonKind.Ghost) => new("", glyph, kind, 38) { AccessibleName = name };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal ButtonKind Kind { get => kind; set { kind = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Alert { get => alert; set { if (alert != value) { alert = value; Invalidate(); } } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal string Glyph { get => glyph; set { if (glyph != value) { glyph = value; Invalidate(); } } }
    // Microphone level shown inside the button while talking; negative hides it.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal float Level { set { float next = value < 0 ? -1 : Math.Clamp(value, 0, 1); if (Math.Abs(next - level) > .01f || next < 0 != level < 0) { level = next; Invalidate(); } } }
    internal int PreferredWidth()
    {
        int text = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int icon = glyph.Length == 0 ? 0 : Theme.S(this, 18) + (text > 0 ? Theme.S(this, 9) : 0);
        return Math.Max(Theme.S(this, 38), text + icon + Theme.S(this, text > 0 ? 36 : 20));
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var back = Theme.BackOf(Parent); g.Clear(back); g.SmoothingMode = SmoothingMode.AntiAlias;
        bool on = Enabled; Color fill, fore;
        if (alert) { fill = pressed ? Theme.Mix(Theme.Danger, Color.Black, .15f) : Theme.Danger; fore = Color.White; }
        else switch (kind)
        {
            case ButtonKind.Primary:
                fill = !on ? Theme.Mix(back, Theme.Raised, .8f) : pressed ? Theme.Mix(Theme.Accent, Color.Black, .14f) : hovered ? Theme.AccentHover : Theme.Accent;
                fore = on ? Theme.OnAccent : Theme.Muted; break;
            case ButtonKind.Secondary:
                fill = !on ? Theme.Mix(back, Theme.Raised, .55f) : pressed ? Theme.Pressed : hovered ? Theme.Hover : Theme.Raised;
                fore = on ? Theme.Text : Theme.Muted; break;
            default:
                fill = on && pressed ? Theme.Pressed : on && hovered ? Theme.Hover : Color.Empty;
                fore = !on ? Theme.Muted : kind == ButtonKind.Nav && !hovered ? Theme.Secondary : Theme.Text; break;
        }
        float radius = Theme.S(this, 8);
        if (!fill.IsEmpty) Theme.FillRounded(g, fill, new RectangleF(0, 0, Width, Height), radius);
        int icon = glyph.Length > 0 ? Theme.S(this, 18) : 0, gap = glyph.Length > 0 && Text.Length > 0 ? Theme.S(this, 9) : 0;
        int text = Text.Length > 0 ? TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
        int meter = level >= 0 ? Theme.S(this, 30) : 0;
        int total = icon + gap + text + meter, x = kind == ButtonKind.Nav ? Theme.S(this, 12) : Math.Max(Theme.S(this, 8), (Width - total) / 2);
        if (icon > 0) TextRenderer.DrawText(g, glyph, Theme.Icons(Text.Length == 0 ? 12 : 11), new Rectangle(x, 0, icon, Height), fore, Centered);
        x += icon + gap;
        if (text > 0)
        {
            int room = Math.Max(0, Math.Min(text, Width - x - meter - Theme.S(this, 8)));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, room + 1, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            x += room;
        }
        if (meter > 0) DrawMeter(g, x + Theme.S(this, 10), fore);
        if (Focused && ShowFocusCues) Theme.DrawRounded(g, Theme.Accent, new RectangleF(1, 1, Width - 2, Height - 2), radius - 1, 1.5f);
    }
    private void DrawMeter(Graphics g, int x, Color color)
    {
        float lit = MathF.Sqrt(level);
        for (int i = 0; i < 4; i++)
        {
            int h = Theme.S(this, 6 + i * 3), w = Theme.S(this, 3);
            using var brush = new SolidBrush(lit > (i + 1) / 5f ? color : Color.FromArgb(90, color));
            g.FillRectangle(brush, x + i * (w + Theme.S(this, 2)), (Height + Theme.S(this, 15)) / 2 - h, w, h);
        }
    }
}

// Compact dropdown: a button that opens a dark menu. Field style is for dialogs.
internal sealed class ChoiceButton : Button
{
    internal readonly List<object> Items = [];
    private int selected = -1;
    private bool hovered, pressed;
    internal bool Field;
    internal bool OpenUpward;
    public event EventHandler? SelectedIndexChanged;
    internal ChoiceButton(bool field = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand; Margin = new(0);
        Field = field; Font = Theme.Font(10, !field); Height = 38; Width = 120;
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal int SelectedIndex
    {
        get => selected;
        set
        {
            int next = Items.Count == 0 ? -1 : Math.Clamp(value, -1, Items.Count - 1);
            if (next == selected) return;
            selected = next; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal object? SelectedItem => selected >= 0 && selected < Items.Count ? Items[selected] : null;
    internal string SelectedText => SelectedItem?.ToString() ?? "";
    internal void Reset() { Items.Clear(); selected = -1; Invalidate(); }
    internal int PreferredWidth() => Items.Select(i => TextRenderer.MeasureText(i?.ToString() ?? "", Font, Size.Empty, TextFormatFlags.NoPadding).Width).DefaultIfEmpty(40).Max() + Theme.S(this, 52);
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Items.Count == 0) return;
        var menu = Menus.Create(); menu.MinimumSize = new(Width, 0);
        for (int i = 0; i < Items.Count; i++) { int index = i; Menus.Add(menu, Items[i]?.ToString() ?? "", "", () => SelectedIndex = index, check: i == selected); }
        int gap = Theme.S(this, 4);
        if (OpenUpward) menu.Show(this, new Point(0, -gap), ToolStripDropDownDirection.AboveRight);
        else menu.Show(this, new Point(0, Height + gap), ToolStripDropDownDirection.BelowRight);
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up && selected > 0) { SelectedIndex = selected - 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Down && selected < Items.Count - 1) { SelectedIndex = selected + 1; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = Theme.S(this, Field ? 6 : 8);
        var rect = new RectangleF(0, 0, Width, Height);
        Color fore = Enabled ? Theme.Text : Theme.Muted;
        if (Field)
        {
            Theme.FillRounded(g, Enabled && (hovered || pressed) ? Theme.Mix(Theme.Raised, Theme.Hover, .5f) : Theme.Raised, rect, radius);
            Theme.DrawRounded(g, Focused ? Theme.Accent : Theme.Border, new RectangleF(.5f, .5f, Width - 1, Height - 1), radius);
            int pad = Theme.S(this, 12), arrow = Theme.S(this, 16);
            TextRenderer.DrawText(g, SelectedText, Font, new Rectangle(pad, 0, Width - pad * 2 - arrow, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Glyphs.ChevronDown, Theme.Icons(8), new Rectangle(Width - pad - arrow, 0, arrow, Height), Theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (Enabled && (hovered || pressed)) Theme.FillRounded(g, pressed ? Theme.Pressed : Theme.Hover, rect, radius);
        int text = TextRenderer.MeasureText(g, SelectedText, Font, Size.Empty, TextFormatFlags.NoPadding).Width, chevron = Theme.S(this, 12), gap = Theme.S(this, 8);
        int x = Math.Max(Theme.S(this, 10), (Width - text - gap - chevron) / 2);
        int room = Math.Max(0, Math.Min(text, Width - x - gap - chevron - Theme.S(this, 8)));
        TextRenderer.DrawText(g, SelectedText, Font, new Rectangle(x, 0, room + 1, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Glyphs.ChevronDown, Theme.Icons(8), new Rectangle(x + room + gap, 0, chevron, Height), Enabled ? Theme.Secondary : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) Theme.DrawRounded(g, Theme.Accent, new RectangleF(1, 1, Width - 2, Height - 2), radius - 1, 1.5f);
    }
}

internal sealed class Slider : Control
{
    private int value;
    private bool hovered, dragging;
    internal int Minimum = 0, Maximum = 100, Step = 5;
    public event EventHandler? ValueChanged, ValueCommitted;
    internal Slider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; Height = 38; Width = 110; Cursor = Cursors.Hand;
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal int Value
    {
        get => value;
        set { int next = Math.Clamp(value, Minimum, Maximum); if (next == this.value) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }
    private Rectangle Track { get { int pad = Theme.S(this, 8); return new(pad, Height / 2 - Theme.S(this, 2), Width - pad * 2, Theme.S(this, 4)); } }
    private void SetFromX(int x) { var t = Track; Value = Minimum + (int)Math.Round((x - t.Left) * (double)(Maximum - Minimum) / Math.Max(1, t.Width)); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
        var t = Track; float fraction = (value - Minimum) / (float)Math.Max(1, Maximum - Minimum);
        float cx = t.Left + t.Width * fraction;
        Theme.FillRounded(g, Theme.Border, t, t.Height / 2f);
        if (cx > t.Left) Theme.FillRounded(g, Enabled ? Theme.Accent : Theme.Muted, new RectangleF(t.Left, t.Top, cx - t.Left, t.Height), t.Height / 2f);
        float r = Theme.S(this, hovered || dragging ? 8 : 7);
        using var thumb = new SolidBrush(Enabled ? Theme.Text : Theme.Muted); g.FillEllipse(thumb, cx - r, Height / 2f - r, r * 2, r * 2);
        if (Focused && ShowFocusCues) { using var ring = new Pen(Theme.Accent, 1.5f); g.DrawEllipse(ring, cx - r - 3, Height / 2f - r - 3, r * 2 + 6, r * 2 + 6); }
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; Focus(); dragging = true; Capture = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragging) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (dragging) { dragging = false; Capture = false; Invalidate(); ValueCommitted?.Invoke(this, EventArgs.Empty); } base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += Math.Sign(e.Delta) * Step; ValueCommitted?.Invoke(this, EventArgs.Empty);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int? next = e.KeyCode switch { Keys.Left or Keys.Down => value - Step, Keys.Right or Keys.Up => value + Step, Keys.PageDown => value - Step * 2, Keys.PageUp => value + Step * 2, Keys.Home => Minimum, Keys.End => Maximum, _ => null };
        if (next != null) { Value = next.Value; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End) ValueCommitted?.Invoke(this, EventArgs.Empty); base.OnKeyUp(e); }
}

internal sealed class Toggle : CheckBox
{
    private bool hovered;
    internal Toggle(string text, bool on)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text; Checked = on; AutoSize = false; Height = 30; Cursor = Cursors.Hand; Font = Theme.Font(10);
    }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = Theme.S(this, 38), h = Theme.S(this, 20), y = (Height - h) / 2;
        var track = new RectangleF(1, y, w, h);
        Color fill = Checked ? (hovered ? Theme.AccentHover : Theme.Accent) : hovered ? Theme.Pressed : Theme.Border;
        Theme.FillRounded(g, Enabled ? fill : Theme.Raised, track, h / 2f);
        float knob = h - Theme.S(this, 6), kx = Checked ? track.Right - knob - Theme.S(this, 3) : track.Left + Theme.S(this, 3);
        using var brush = new SolidBrush(Checked ? Theme.OnAccent : Theme.Text); g.FillEllipse(brush, kx, y + (h - knob) / 2f, knob, knob);
        if (Focused && ShowFocusCues) Theme.DrawRounded(g, Theme.Accent, RectangleF.Inflate(track, 2.5f, 2.5f), h / 2f + 2, 1.5f);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(w + Theme.S(this, 14), 0, Width - w - Theme.S(this, 14), Height), Enabled ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// A borderless TextBox inside a rounded field that highlights on focus.
internal sealed class FieldBox : Panel
{
    internal readonly TextBox Box = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Theme.Font(10) };
    internal FieldBox(string text = "")
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 38; Box.Text = text; Controls.Add(Box); Cursor = Cursors.IBeam;
        Box.GotFocus += (_, _) => Invalidate(); Box.LostFocus += (_, _) => Invalidate();
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal string Value { get => Box.Text; set => Box.Text = value; }
    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); int pad = Theme.S(this, 12);
        Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - pad * 2), Box.PreferredHeight);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = Theme.S(this, 6);
        Theme.FillRounded(g, Theme.Raised, new RectangleF(0, 0, Width, Height), radius);
        Theme.DrawRounded(g, Box.Focused ? Theme.Accent : Theme.Border, new RectangleF(.5f, .5f, Width - 1, Height - 1), radius);
    }
}

internal enum CameraState { None, Connecting, Live, Offline }

// Camera cards: name, connection type and live state, with a menu on hover.
internal sealed class CameraList : Control
{
    internal readonly List<CameraConfig> Items = [];
    private int selected = -1, hover = -1, scroll;
    private bool hoverMenu, locked;
    private string? activeId;
    private CameraState activeState;
    public event EventHandler? SelectedIndexChanged, ItemActivated;
    public event Action<CameraConfig, Point>? MenuRequested;
    internal CameraList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Locked { get => locked; set { if (locked != value) { locked = value; Invalidate(); } } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal int SelectedIndex
    {
        get => selected;
        set
        {
            int next = Items.Count == 0 ? -1 : Math.Clamp(value, -1, Items.Count - 1);
            if (next == selected) { Invalidate(); return; }
            selected = next; Reveal(next); Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal CameraConfig? SelectedItem => selected >= 0 && selected < Items.Count ? Items[selected] : null;
    internal void SetState(string? id, CameraState state) { if (activeId != id || activeState != state) { activeId = id; activeState = state; Invalidate(); } }
    internal void Reload() { if (selected >= Items.Count) selected = Items.Count - 1; ClampScroll(); Invalidate(); }
    private int Row => Theme.S(this, 60);
    private int Gap => Theme.S(this, 4);
    private Rectangle RowBounds(int i) => new(0, i * (Row + Gap) - scroll, Width, Row);
    private Rectangle MenuBounds(Rectangle row) { int size = Theme.S(this, 30); return new(row.Right - size - Theme.S(this, 8), row.Top + (row.Height - size) / 2, size, size); }
    private int HitRow(Point p) { for (int i = 0; i < Items.Count; i++) if (RowBounds(i).Contains(p)) return i; return -1; }
    private void ClampScroll() { int content = Items.Count * (Row + Gap) - Gap; scroll = Math.Clamp(scroll, 0, Math.Max(0, content - Height)); }
    private void Reveal(int i)
    {
        if (i < 0) return;
        var row = RowBounds(i);
        if (row.Top < 0) scroll += row.Top; else if (row.Bottom > Height) scroll += row.Bottom - Height;
        ClampScroll();
    }
    private bool Selectable(int i) => !locked || i == selected;
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Items.Count == 0)
        {
            TextRenderer.DrawText(g, "No cameras yet", Theme.Font(9.5f), new Rectangle(Theme.S(this, 12), Theme.S(this, 6), Width, Theme.S(this, 24)), Theme.Muted, TextFormatFlags.NoPadding);
            return;
        }
        for (int i = 0; i < Items.Count; i++)
        {
            var row = RowBounds(i);
            if (row.Bottom < 0 || row.Top > Height) continue;
            var camera = Items[i];
            bool isSelected = i == selected, isHover = i == hover && Selectable(i), dim = !Selectable(i);
            float radius = Theme.S(this, 8);
            if (isSelected) Theme.FillRounded(g, Theme.Raised, row, radius);
            else if (isHover) Theme.FillRounded(g, Theme.Mix(Theme.Chrome, Theme.Raised, .6f), row, radius);
            if (isSelected) Theme.FillRounded(g, Theme.Accent, new RectangleF(row.Left, row.Top + row.Height / 2f - Theme.S(this, 9), Theme.S(this, 3), Theme.S(this, 18)), Theme.S(this, 2));
            int tile = Theme.S(this, 34), tx = row.Left + Theme.S(this, 12), ty = row.Top + (row.Height - tile) / 2;
            Theme.FillRounded(g, isSelected ? Theme.Mix(Theme.Raised, Theme.Accent, .16f) : Theme.Mix(Theme.Chrome, Theme.Raised, .85f), new RectangleF(tx, ty, tile, tile), Theme.S(this, 8));
            TextRenderer.DrawText(g, Glyphs.Video, Theme.Icons(11), new Rectangle(tx, ty, tile, tile), dim ? Theme.Muted : isSelected ? Theme.Accent : Theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            bool showMenu = (isSelected || isHover) && !dim;
            int textX = tx + tile + Theme.S(this, 12), textW = row.Right - textX - (showMenu ? Theme.S(this, 42) : Theme.S(this, 10));
            int mid = row.Top + row.Height / 2;
            TextRenderer.DrawText(g, camera.Name, Theme.Font(10, true), new Rectangle(textX, mid - Theme.S(this, 20), textW, Theme.S(this, 20)), dim ? Theme.Muted : Theme.Text, TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            var detailRect = new Rectangle(textX, mid + Theme.S(this, 2), textW, Theme.S(this, 18));
            if (camera.Id == activeId && activeState != CameraState.None)
            {
                Color dot = activeState switch { CameraState.Live => Theme.Accent, CameraState.Connecting => Theme.Amber, _ => Theme.Red };
                string state = activeState switch { CameraState.Live => "Live", CameraState.Connecting => "Connecting…", _ => "Offline" };
                int d = Theme.S(this, 6);
                using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, detailRect.Left, detailRect.Top + (Theme.S(this, 15) - d) / 2f, d, d);
                TextRenderer.DrawText(g, state, Theme.Font(8.5f), new Rectangle(detailRect.Left + d + Theme.S(this, 6), detailRect.Top, detailRect.Width - d, detailRect.Height), dot, TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            else TextRenderer.DrawText(g, Describe(camera), Theme.Font(8.5f), detailRect, dim ? Theme.Muted : Theme.Secondary, TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (showMenu)
            {
                var menu = MenuBounds(row);
                if (i == hover && hoverMenu) Theme.FillRounded(g, Theme.Pressed, menu, Theme.S(this, 6));
                TextRenderer.DrawText(g, Glyphs.More, Theme.Icons(10), menu, Theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        int contentHeight = Items.Count * (Row + Gap) - Gap;
        if (contentHeight > Height)
        {
            float bar = Math.Max(Theme.S(this, 24), Height * (float)Height / contentHeight), top = (Height - bar) * scroll / (float)(contentHeight - Height);
            Theme.FillRounded(g, Theme.Border, new RectangleF(Width - Theme.S(this, 3), top, Theme.S(this, 3), bar), 1.5f);
        }
        if (Focused && ShowFocusCues && SelectedItem != null) Theme.DrawRounded(g, Theme.Accent, RectangleF.Inflate(RowBounds(selected), -1, -1), Theme.S(this, 8), 1.5f);
    }
    internal static string Describe(CameraConfig camera) => camera.Kind switch { CameraKind.Tutk => "HDCameraPro · UID", CameraKind.Onvif => "ONVIF camera", _ => "Video stream" };
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = HitRow(e.Location); bool menu = row >= 0 && MenuBounds(RowBounds(row)).Contains(e.Location);
        if (row != hover || menu != hoverMenu) { hover = row; hoverMenu = menu; Cursor = row >= 0 && Selectable(row) ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverMenu = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        int row = HitRow(e.Location);
        if (row >= 0 && Selectable(row))
        {
            SelectedIndex = row;
            var bounds = RowBounds(row); var menu = MenuBounds(bounds);
            if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(Items[row], PointToScreen(e.Location));
            else if (e.Button == MouseButtons.Left && menu.Contains(e.Location)) MenuRequested?.Invoke(Items[row], PointToScreen(new Point(menu.Left, menu.Bottom + Theme.S(this, 4))));
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int row = HitRow(e.Location);
        if (e.Button == MouseButtons.Left && row >= 0 && row == selected && !MenuBounds(RowBounds(row)).Contains(e.Location)) ItemActivated?.Invoke(this, EventArgs.Empty);
        base.OnMouseDoubleClick(e);
    }
    protected override void OnMouseWheel(MouseEventArgs e) { scroll -= Math.Sign(e.Delta) * Theme.S(this, 48); ClampScroll(); Invalidate(); base.OnMouseWheel(e); }
    protected override void OnResize(EventArgs e) { ClampScroll(); base.OnResize(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up && !locked && selected > 0) { SelectedIndex = selected - 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Down && !locked && selected < Items.Count - 1) { SelectedIndex = selected + 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && SelectedItem != null) { ItemActivated?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        else if ((e.KeyCode == Keys.Apps || e.KeyCode == Keys.F10 && e.Shift) && SelectedItem is { } camera)
        {
            var row = RowBounds(selected); MenuRequested?.Invoke(camera, PointToScreen(new Point(row.Left + Theme.S(this, 48), row.Bottom))); e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}

internal enum StageMode { Empty, Idle, Busy, Problem }

// The video area when no picture is showing: one clear message and one action.
internal sealed class StageView : Control
{
    private StageMode mode = StageMode.Idle;
    private string heading = "Ready when you are", detail = "";
    private readonly ActionButton action = new("Connect", Glyphs.Play, ButtonKind.Primary) { Visible = false };
    private readonly System.Windows.Forms.Timer spin = new() { Interval = 33 }, fade = new() { Interval = 33 };
    private Rectangle icon;
    private float angle, toastAlpha;
    private string toast = "";
    private DateTime toastShown;
    public event EventHandler? ActionClicked;
    internal StageView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Stage; Dock = DockStyle.Fill; Controls.Add(action);
        action.Click += (_, _) => ActionClicked?.Invoke(this, EventArgs.Empty);
        spin.Tick += (_, _) => { angle = (angle + 10) % 360; Invalidate(Rectangle.Inflate(icon, 4, 4)); };
        fade.Tick += (_, _) =>
        {
            double age = (DateTime.UtcNow - toastShown).TotalMilliseconds;
            toastAlpha = age < 3600 ? 1 : (float)Math.Max(0, 1 - (age - 3600) / 400);
            if (toastAlpha <= 0) { fade.Stop(); toast = ""; }
            Invalidate(ToastBounds());
        };
    }
    internal string Heading => heading;
    internal string Detail => detail;
    internal void Show(StageMode mode, string heading, string detail, string? actionText = null, string actionGlyph = "")
    {
        this.mode = mode; this.heading = heading; this.detail = detail;
        action.Visible = actionText != null;
        if (actionText != null) { action.Text = actionText; action.Glyph = actionGlyph; action.Width = action.PreferredWidth() + Theme.S(this, 8); action.Height = Theme.S(this, 40); }
        spin.Enabled = mode == StageMode.Busy && Visible;
        PerformLayout(); Invalidate();
    }
    internal void Toast(string text) { toast = text; toastShown = DateTime.UtcNow; toastAlpha = 1; fade.Start(); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e) { spin.Enabled = mode == StageMode.Busy && Visible; base.OnVisibleChanged(e); }
    private int DetailWidth => Math.Max(Theme.S(this, 200), Math.Min(Width - Theme.S(this, 64), Theme.S(this, 460)));
    private (int Top, int Heading, int Detail) Block()
    {
        int size = Theme.S(this, 64), headingH = TextRenderer.MeasureText(heading, Theme.Font(15, true)).Height;
        int detailH = detail.Length == 0 ? 0 : TextRenderer.MeasureText(detail, Theme.Font(10), new Size(DetailWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter).Height;
        int total = size + Theme.S(this, 22) + headingH + (detailH > 0 ? Theme.S(this, 6) + detailH : 0) + (action.Visible ? Theme.S(this, 24) + action.Height : 0);
        return ((Height - total) / 2, headingH, detailH);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var (top, headingH, detailH) = Block(); int size = Theme.S(this, 64);
        icon = new((Width - size) / 2, top, size, size);
        int y = icon.Bottom + Theme.S(this, 22) + headingH + (detailH > 0 ? Theme.S(this, 6) + detailH : 0) + Theme.S(this, 24);
        action.Location = new((Width - action.Width) / 2, y);
    }
    protected override void OnResize(EventArgs e) { PerformLayout(); base.OnResize(e); }
    private Rectangle ToastBounds()
    {
        if (toast.Length == 0) return Rectangle.Empty;
        var size = TextRenderer.MeasureText(toast, Theme.Font(9.5f)); int w = Math.Min(Width - Theme.S(this, 32), size.Width + Theme.S(this, 32)), h = Theme.S(this, 36);
        return new((Width - w) / 2, Height - h - Theme.S(this, 24), w, h);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.Stage); g.SmoothingMode = SmoothingMode.AntiAlias;
        var (_, headingH, detailH) = Block();
        Color tone = mode == StageMode.Problem ? Theme.Red : Theme.Accent;
        if (mode == StageMode.Busy)
        {
            var ring = RectangleF.Inflate(icon, -Theme.S(this, 10), -Theme.S(this, 10));
            using var track = new Pen(Theme.Line, Theme.S(this, 3)); g.DrawEllipse(track, ring);
            using var arc = new Pen(Theme.Accent, Theme.S(this, 3)) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawArc(arc, ring, angle, 100);
        }
        else
        {
            using var circle = new SolidBrush(Theme.Mix(Theme.Stage, tone, .13f)); g.FillEllipse(circle, icon);
            string glyph = mode switch { StageMode.Empty => Glyphs.Add, StageMode.Problem => Glyphs.Warning, _ => Glyphs.Video };
            TextRenderer.DrawText(g, glyph, Theme.Icons(20), icon, tone, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        int y = icon.Bottom + Theme.S(this, 22);
        TextRenderer.DrawText(g, heading, Theme.Font(15, true), new Rectangle(Theme.S(this, 16), y, Width - Theme.S(this, 32), headingH), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        if (detailH > 0) TextRenderer.DrawText(g, detail, Theme.Font(10), new Rectangle((Width - DetailWidth) / 2, y + headingH + Theme.S(this, 6), DetailWidth, detailH), Theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        if (toast.Length > 0 && toastAlpha > 0)
        {
            var bounds = ToastBounds();
            Theme.FillRounded(g, Color.FromArgb((int)(255 * toastAlpha), Theme.Raised), bounds, bounds.Height / 2f);
            TextRenderer.DrawText(g, toast, Theme.Font(9.5f), Rectangle.Inflate(bounds, -Theme.S(this, 14), 0), Theme.Mix(Theme.Stage, Theme.Text, toastAlpha), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { spin.Dispose(); fade.Dispose(); } base.Dispose(disposing); }
}

// Logo and name at the top of the sidebar.
internal sealed class BrandMark : Control
{
    internal BrandMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
        int size = Theme.S(this, 26), x = Theme.S(this, 6), y = (Height - size) / 2;
        Theme.FillRounded(g, Theme.Accent, new RectangleF(x, y, size, size), Theme.S(this, 8));
        float lens = size * .5f, dot = size * .16f;
        using (var dark = new SolidBrush(Theme.OnAccent)) g.FillEllipse(dark, x + (size - lens) / 2, y + (size - lens) / 2, lens, lens);
        using (var light = new SolidBrush(Theme.Accent)) g.FillEllipse(light, x + size / 2f - dot / 2 + lens * .12f, y + size / 2f - dot / 2 - lens * .12f, dot, dot);
        TextRenderer.DrawText(g, "CameraLite", Theme.Font(12, true), new Rectangle(x + size + Theme.S(this, 10), 0, Width, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// Dark replacement for MessageBox, so prompts match the app.
internal static class Prompt
{
    internal static bool Ask(IWin32Window? owner, string title, string message, string confirm, bool danger = false) => Run(owner, title, message, confirm, "Cancel", danger) == DialogResult.OK;
    internal static void Tell(IWin32Window? owner, string title, string message) => Run(owner, title, message, "OK", null, false);
    private static DialogResult Run(IWin32Window? owner, string title, string message, string confirm, string? cancel, bool danger)
    {
        using var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = owner == null, StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent, BackColor = Theme.Chrome, ForeColor = Theme.Text, Font = Theme.Font(10) };
        Theme.Window(form);
        var text = new Label { Text = message, AutoSize = true, MaximumSize = new(400, 0), ForeColor = Theme.Text, Location = new(24, 22) };
        var ok = new ActionButton(confirm, "", danger ? ButtonKind.Secondary : ButtonKind.Primary) { DialogResult = DialogResult.OK, Alert = danger };
        ok.Width = Math.Max(ok.Width, 96);
        form.Controls.Add(text); form.Controls.Add(ok); form.AcceptButton = ok;
        int buttonsTop = 22 + text.PreferredHeight + 26;
        form.ClientSize = new(448, buttonsTop + 38 + 20);
        ok.Location = new(form.ClientSize.Width - 24 - ok.Width, buttonsTop);
        if (cancel != null)
        {
            var no = new ActionButton(cancel, "", ButtonKind.Secondary) { DialogResult = DialogResult.Cancel }; no.Width = Math.Max(no.Width, 96);
            no.Location = new(ok.Left - 10 - no.Width, buttonsTop); form.Controls.Add(no); form.CancelButton = no;
        }
        else form.CancelButton = ok;
        form.Shown += (_, _) => (cancel != null && danger ? (Control)form.CancelButton! : ok).Focus();
        return owner == null ? form.ShowDialog() : form.ShowDialog(owner);
    }
}
