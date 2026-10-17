using System.Text.RegularExpressions;

namespace CameraLite;

public sealed class CameraDialog : Form
{
    private readonly FieldBox name = new("My camera"), address = new(), user = new("admin"), password = new(), low = new(), sdk = new();
    private readonly ChoiceButton kind = new(true);
    private readonly ListBox discovered = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label hint = new() { AutoSize = true, ForeColor = Theme.Secondary }, searchState = new() { AutoSize = true, ForeColor = Theme.Secondary };
    private readonly Label addressLabel = Theme.Label("Camera UID", 9.5f, Theme.Secondary), advanced = Theme.Label("", 9.5f, Theme.Accent);
    private readonly ActionButton add = new("Save camera", Glyphs.Check, ButtonKind.Primary), search = new("Find cameras on this network", Glyphs.Search);
    private readonly FlowLayoutPanel content = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly Panel lowRow, sdkRow, results;
    private bool showAdvanced;
    private readonly CancellationTokenSource stop = new();
    private readonly CameraConfig? original;
    public CameraConfig? Camera { get; private set; }
    public CameraDialog(CameraConfig? camera = null)
    {
        original = camera;
        Text = camera == null ? "Add camera" : "Edit camera";
        Theme.Window(this);
        int S(int value) => Theme.S(this, value);
        int width = S(520);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new(width + S(56), S(612)); StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Font(10); BackColor = Theme.Chrome; ForeColor = Theme.Text;
        kind.Items.AddRange(["IP camera (ONVIF)", "Video stream URL (RTSP / HTTP)", "HDCameraPro camera (UID)"]);
        kind.SelectedIndex = (int)(camera?.Kind ?? (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "vendor-sdk")) ? CameraKind.Tutk : CameraKind.Onvif));
        password.Box.UseSystemPasswordChar = true;
        content.Padding = new(S(28), S(20), S(28), S(12)); Theme.DarkScrollbars(content);
        Panel Row(Control field, Label caption, int height = 38)
        {
            var row = new Panel { Width = width, Height = S(24) + S(height), Margin = new(0, 0, 0, S(14)) };
            caption.Location = new(0, 0); field.SetBounds(0, S(24), width, S(height));
            row.Controls.AddRange([caption, field]); content.Controls.Add(row); return row;
        }
        Row(name, Theme.Label("Name", 9.5f, Theme.Secondary));
        Row(kind, Theme.Label("Connection", 9.5f, Theme.Secondary));
        var addressRow = Row(address, addressLabel); addressRow.Margin = new(0, 0, 0, S(6));
        hint.MaximumSize = new(width, 0); hint.Font = Theme.Font(9); hint.Margin = new(0, 0, 0, S(16)); content.Controls.Add(hint);
        var account = new Panel { Width = width, Height = S(62), Margin = new(0, 0, 0, S(14)) };
        int half = (width - S(12)) / 2;
        var userLabel = Theme.Label("Username", 9.5f, Theme.Secondary); var passwordLabel = Theme.Label("Password", 9.5f, Theme.Secondary);
        userLabel.Location = new(0, 0); user.SetBounds(0, S(24), half, S(38));
        passwordLabel.Location = new(half + S(12), 0); password.SetBounds(half + S(12), S(24), half, S(38));
        account.Controls.AddRange([userLabel, user, passwordLabel, password]); content.Controls.Add(account);
        lowRow = Row(low, Theme.Label("Low quality stream URL (optional)", 9.5f, Theme.Secondary));
        advanced.Cursor = Cursors.Hand; advanced.Margin = new(0, 0, 0, S(12)); content.Controls.Add(advanced);
        advanced.Click += (_, _) => { showAdvanced = !showAdvanced; UpdateHints(); };
        var sdkPanel = new Panel();
        var browse = new ActionButton("Browse", Glyphs.Folder) { Width = S(96) };
        sdk.SetBounds(0, 0, width - S(106), S(38)); browse.SetBounds(width - S(96), 0, S(96), S(38)); sdkPanel.Controls.AddRange([sdk, browse]);
        sdkRow = Row(sdkPanel, Theme.Label("Vendor library folder", 9.5f, Theme.Secondary));
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = "Compatible Windows x64 TUTK SDK folder" }; if (picker.ShowDialog(this) == DialogResult.OK) sdk.Value = picker.SelectedPath; };
        content.Controls.Add(new Panel { Width = width, Height = 1, BackColor = Theme.Line, Margin = new(0, S(4), 0, S(18)) });
        search.Width = width; search.Margin = new(0, 0, 0, S(8)); content.Controls.Add(search);
        searchState.MaximumSize = new(width, 0); searchState.Font = Theme.Font(9); searchState.Margin = new(0, 0, 0, S(8));
        searchState.Text = "Both devices need to be on the same router."; content.Controls.Add(searchState);
        results = new CardPanel { Width = width, Height = S(150), Padding = new(S(4)), Margin = new(0), Visible = false };
        discovered.BackColor = Theme.Raised; discovered.ForeColor = Theme.Text; discovered.ItemHeight = S(46); discovered.DrawItem += DrawDiscovered;
        Theme.DarkScrollbars(discovered); results.Controls.Add(discovered); content.Controls.Add(results);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(70), BackColor = Theme.Raised };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });
        var cancel = new ActionButton("Cancel") { DialogResult = DialogResult.Cancel }; cancel.Width = Math.Max(cancel.Width, S(96));
        footer.Controls.AddRange([add, cancel]);
        add.Location = new(ClientSize.Width - S(28) - add.Width, (footer.Height - add.Height) / 2); cancel.Location = new(add.Left - S(10) - cancel.Width, add.Top);
        Controls.Add(content); Controls.Add(footer);
        AcceptButton = add; CancelButton = cancel;
        bool passwordLost = false;
        if (camera != null)
        {
            name.Value = camera.Name; address.Value = camera.Address; user.Value = camera.Username; sdk.Value = camera.SdkFolder;
            try { password.Value = camera.Password(); } catch (SavedPasswordException) { passwordLost = true; }
            low.Value = camera.Kind == CameraKind.DirectStream && camera.Profiles.Count > 1 ? camera.Profiles[1].Uri : "";
            // Keep a working custom SDK folder; otherwise fall back to the bundled libraries.
            bool customSdk = camera.SdkFolder.Length > 0 && File.Exists(Path.Combine(camera.SdkFolder, "IOTCAPIs.dll"));
            if (!customSdk && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "vendor-sdk"))) sdk.Value = Path.Combine(AppContext.BaseDirectory, "vendor-sdk");
        }
        else if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "vendor-sdk"))) sdk.Value = Path.Combine(AppContext.BaseDirectory, "vendor-sdk");
        kind.SelectedIndexChanged += (_, _) => UpdateHints(); UpdateHints();
        search.Click += async (_, _) => await Discover();
        discovered.SelectedIndexChanged += (_, _) => { if (discovered.SelectedItem is DiscoveredCamera found) { kind.SelectedIndex = found.Kind == "TUTK" ? 2 : 0; address.Value = found.Kind == "TUTK" ? found.Uid : found.Address; if (name.Value is "" or "My camera") name.Value = found.Name; } };
        add.Click += async (_, _) => await SaveCamera();
        FormClosed += (_, _) => stop.Cancel();
        Shown += (_, _) =>
        {
            if (!passwordLost) { name.Box.Focus(); return; }
            password.Box.Focus(); Prompt.Tell(this, "Camera", "The saved password can’t be read by this Windows account. Enter the camera password again, then save.");
        };
    }
    private void DrawDiscovered(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || discovered.Items[e.Index] is not DiscoveredCamera camera) return;
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Theme.Raised)) g.FillRectangle(back, e.Bounds);
        if (e.State.HasFlag(DrawItemState.Selected)) Theme.FillRounded(g, Theme.Hover, Rectangle.Inflate(e.Bounds, -2, -2), Theme.S(this, 6));
        int pad = Theme.S(this, 12), mid = e.Bounds.Top + e.Bounds.Height / 2;
        TextRenderer.DrawText(g, camera.Name, Theme.Font(10, true), new Rectangle(e.Bounds.Left + pad, mid - Theme.S(this, 19), e.Bounds.Width - pad * 2, Theme.S(this, 19)), Theme.Text, TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, camera.Address + (camera.Kind == "TUTK" ? " · HDCameraPro UID" : " · ONVIF"), Theme.Font(9), new Rectangle(e.Bounds.Left + pad, mid + Theme.S(this, 2), e.Bounds.Width - pad * 2, Theme.S(this, 18)), Theme.Secondary, TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
    private void UpdateHints()
    {
        lowRow.Visible = kind.SelectedIndex == 1;
        advanced.Visible = kind.SelectedIndex == 2; sdkRow.Visible = kind.SelectedIndex == 2 && showAdvanced;
        advanced.Text = (showAdvanced ? "Hide" : "Show") + " advanced settings";
        addressLabel.Text = kind.SelectedIndex switch { 0 => "Camera IP or ONVIF URL", 1 => "Video stream URL", _ => "Camera UID" };
        hint.Text = kind.SelectedIndex switch
        {
            0 => "Enter the camera IP (optionally with :port) or its ONVIF device URL. The camera supplies its own quality profiles.",
            1 => "Enter the complete stream URL without credentials. A second, lower-quality URL enables quality switching. Talk needs an ONVIF RTSP audio backchannel.",
            _ => "The 20-character UID shown in HDCameraPro, or use Find cameras below. The included libraries support this camera's video, audio and talk."
        };
        search.Text = kind.SelectedIndex == 2 ? "Find HDCameraPro cameras on this network" : "Find ONVIF cameras on this network";
    }
    private async Task Discover()
    {
        search.Enabled = false; search.Text = "Searching for 5 seconds…"; searchState.ForeColor = Theme.Secondary; searchState.Text = "Listening for replies…";
        try
        {
            string sdkFolder = sdk.Value;
            var found = kind.SelectedIndex == 2
                ? await TutkCamera.DiscoverAsync(sdkFolder, stop.Token)
                : await Discovery.SearchAsync(stop.Token);
            discovered.Items.Clear(); foreach (var camera in found) discovered.Items.Add(camera);
            if (found.Count > 0 && !results.Visible)
            {
                int room = Screen.FromControl(this).WorkingArea.Height - Height;
                results.Visible = true; Height += Math.Min(room, results.Height + Theme.S(this, 8));
            }
            results.Visible = found.Count > 0;
            searchState.Text = found.Count > 0 ? $"Found {found.Count}. Select one, enter its password and save." : kind.SelectedIndex == 2 ? "No camera replied. Enter the UID from HDCameraPro manually and check that the camera is online." : "No ONVIF camera replied. Check that both devices use the same router and that ONVIF is enabled on the camera.";
        }
        catch (Exception ex) { if (!IsDisposed) { searchState.ForeColor = Theme.Red; searchState.Text = ex.Message; } }
        finally { if (!IsDisposed) { search.Enabled = true; UpdateHints(); } }
    }
    private async Task SaveCamera()
    {
        add.Enabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(name.Value)) throw new ArgumentException("Give this camera a name.");
            var result = new CameraConfig { Id = original?.Id ?? Guid.NewGuid().ToString(), Name = name.Value.Trim(), Kind = (CameraKind)kind.SelectedIndex, Address = address.Value.Trim(), Username = user.Value, SdkFolder = sdk.Value.Trim(), SelectedProfile = original?.SelectedProfile ?? 0 };
            result.SetPassword(password.Value);
            switch (result.Kind)
            {
                case CameraKind.Onvif:
                    result.Address = CameraUri.OnvifAddress(result.Address).AbsoluteUri;
                    add.Text = "Checking camera…";
                    using (var onvif = new OnvifClient(result.Address, result.Username, password.Value)) result.Profiles = await onvif.ProfilesAsync(stop.Token);
                    break;
                case CameraKind.DirectStream:
                    result.Address = CameraUri.ValidateStream(result.Address).AbsoluteUri;
                    result.Profiles.Add(new("High / main stream", result.Address));
                    if (!string.IsNullOrWhiteSpace(low.Value)) result.Profiles.Add(new("Low / substream", CameraUri.ValidateStream(low.Value).AbsoluteUri));
                    break;
                case CameraKind.Tutk:
                    if (!Regex.IsMatch(result.Address, "^[A-Za-z0-9]{20}$")) throw new ArgumentException("Enter the complete 20-character UID from HDCameraPro, without dots or spaces. PPCS UIDs use a different protocol and are not supported.");
                    result.Profiles = [new("HD", ""), new("Standard", ""), new("Low bandwidth", "")];
                    break;
            }
            Camera = result; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { if (!IsDisposed) Prompt.Tell(this, "Camera", ex.Message); }
        finally { if (!IsDisposed) { add.Enabled = true; add.Text = "Save camera"; } }
    }
}

// Rounded surface for grouped content such as search results.
internal sealed class CardPanel : Panel
{
    internal CardPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.BackOf(Parent)); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float radius = Theme.S(this, 8);
        Theme.FillRounded(g, Theme.Raised, new RectangleF(0, 0, Width, Height), radius);
        Theme.DrawRounded(g, Theme.Border, new RectangleF(.5f, .5f, Width - 1, Height - 1), radius);
    }
}
