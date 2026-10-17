using System.Diagnostics;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using NAudio.Wave;

namespace CameraLite;

public sealed class MainForm : Form
{
    private readonly LibVLC vlc;
    private readonly MediaPlayer player;
    private readonly VideoView video;
    private readonly List<CameraConfig> cameras;
    private readonly Preferences preferences;
    private readonly CameraList list = new() { Dock = DockStyle.Fill };
    private readonly ChoiceButton quality = new() { OpenUpward = true };
    private readonly ActionButton connect = new("Connect", Glyphs.Play, ButtonKind.Primary), talk = new("Hold to talk", Glyphs.Microphone),
        mute = ActionButton.Icon(Glyphs.Volume, "Mute"), pause = ActionButton.Icon(Glyphs.Pause, "Pause"), snapshot = ActionButton.Icon(Glyphs.Camera, "Snapshot"), fullscreen = ActionButton.Icon(Glyphs.FullScreen, "Fullscreen"),
        add = ActionButton.Icon(Glyphs.Add, "Add camera"), snapshots = new("Snapshots", Glyphs.Pictures, ButtonKind.Nav), settings = new("Settings", Glyphs.Settings, ButtonKind.Nav);
    private readonly Slider volume = new();
    private readonly Label volumeLabel = Theme.Label("70%", 9, Theme.Secondary);
    private readonly StageView empty = new();
    private readonly VideoOverlay? overlay;
    private readonly Panel sidebar = new() { Dock = DockStyle.Left }, sideLine = new() { Dock = DockStyle.Left, Width = 1 }, bar = new() { Dock = DockStyle.Bottom }, stage = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer telemetry = new() { Interval = 1000 }, meterTimer = new() { Interval = 80 };
    private readonly SemaphoreSlim sessionGate = new(1), talkGate = new(1);
    private readonly ToolTip tips = Theme.ToolTips();
    private readonly List<string> microphoneNames = [];
    private CancellationTokenSource? connectionStop, talkStop;
    private TutkCamera? tutk;
    private Backchannel? backchannel;
    private Media? media;
    private CameraConfig? active;
    private Task? talkOperation, talkCleanup, sessionOperation, frameCapture;
    private TaskCompletionSource<string>? streamFailure;
    private bool connecting, connected, muted, filling, talking, talkLive, inFullscreen, shuttingDown, canClose, cleaning, changingQuality, statisticsObserved, paused, spaceDown, altDown, altChord, altTalk;
    // Pause freezes the picture only: the stream keeps decoding underneath, so the
    // stall detector and the transport never see a stopped reader.
    private readonly PictureBox frozen = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Visible = false };
    private string? frozenPath;
    // Alt alone starts talk after a short hold. Any other key with Alt makes a
    // shortcut such as Alt+Tab or Alt+F4, which ends Alt talk and never starts it.
    private readonly System.Windows.Forms.Timer talkArm = new() { Interval = 200 };
    private DateTime connectedAt, openedAt, lastDecodedAt;
    private int lastDisplayed;
    private long previousFrames, previousBytes;
    private string? muteTip, audioCodec;
    private string lastStatus = "";
    private Rectangle savedBounds;
    private FormWindowState savedState;
    private readonly bool preview;
    private readonly bool verification;

    public MainForm() : this(false) { }
    internal MainForm(bool preview, bool verification = false)
    {
        this.preview = preview;
        this.verification = verification;
        preferences = preview ? new() : Preferences.Load();
        if (verification) { preferences.Muted = true; preferences.Volume = 0; preferences.HardwareDecoding = false; preferences.AutoReconnect = true; }
        cameras = preview ? [new() { Name = "H10 camera", Kind = CameraKind.Tutk, Address = "PREVIEW", Profiles = [new("HD", ""), new("Standard", ""), new("Low bandwidth", "")] }] : CameraStore.Load();
        Text = "CameraLite"; StartPosition = FormStartPosition.CenterScreen;
        Theme.Window(this);
        Size = new(Theme.S(this, 1280), Theme.S(this, 800)); MinimumSize = new(Theme.S(this, 1000), Theme.S(this, 640));
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BackColor = Theme.Chrome; ForeColor = Theme.Text; Font = Theme.Font(10); KeyPreview = true;
        Core.Initialize(); vlc = new("--no-video-title-show", "--quiet", "--no-osd", "--vout=wingdi");
        player = new(vlc) { EnableHardwareDecoding = preferences.HardwareDecoding, EnableKeyInput = false, EnableMouseInput = false };
        video = new() { Dock = DockStyle.Fill, MediaPlayer = player, BackColor = Color.Black, Visible = false };
        player.Playing += (_, _) => Ui(() => { if (connected && !cleaning) { list.SetState(active?.Id, CameraState.Live); ApplySound(); UpdateControls(); overlay?.Reveal(); } });
        player.EncounteredError += (_, _) => Ui(() => SignalFailure("The video decoder could not open the stream."));
        player.EndReached += (_, _) => Ui(() => SignalFailure("The camera stream ended."));
        if (!preview && !verification)
        {
            overlay = new(this);
            overlay.Command += action =>
            {
                if (action == HudAction.Mute) ToggleMute();
                else if (action == HudAction.Snapshot) _ = TakeSnapshot();
                else if (action == HudAction.Pause) _ = TogglePause();
                else if (action == HudAction.Quality) ShowQualityMenu();
                else if (action == HudAction.Fullscreen) ToggleFullscreen();
            };
            overlay.TalkPressed += BeginTalk; overlay.TalkReleased += () => _ = StopTalk();
            overlay.ToggleRequested += ToggleFullscreen;
            overlay.VolumeRequested += (value, commit) => { volume.Value = value; if (commit) SavePreferences(); };
        }
        BuildLayout();
        foreach (var camera in cameras.Where(c => c.Kind == CameraKind.Tutk))
            camera.Profiles = [new("HD", ""), new("Standard", ""), new("Low bandwidth", "")];
        list.Items.AddRange(cameras); list.Reload();
        LoadMicrophones();
        volume.Value = preferences.Volume; muted = preferences.Muted; ApplySound();
        list.SelectedIndexChanged += (_, _) =>
        {
            FillProfiles();
            if (list.SelectedItem is CameraConfig camera) { preferences.LastCamera = camera.Id; SavePreferences(); }
        };
        list.ItemActivated += async (_, _) => { if (sessionOperation == null) await StartSession(); };
        list.MenuRequested += ShowCameraMenu;
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete && list.SelectedItem is { } camera && sessionOperation == null) { RemoveCamera(camera); e.Handled = true; } };
        add.Click += (_, _) => EditCamera(null);
        connect.Click += async (_, _) => { if (sessionOperation != null) await StopSession(); else await StartSession(); };
        empty.ActionClicked += async (_, _) => { if (list.SelectedItem == null) EditCamera(null); else if (sessionOperation == null) await StartSession(); };
        mute.Click += (_, _) => ToggleMute();
        volume.ValueChanged += (_, _) => { preferences.Volume = volume.Value; ApplySound(); };
        volume.ValueCommitted += (_, _) => SavePreferences();
        quality.SelectedIndexChanged += async (_, _) => await ChangeQuality();
        talk.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginTalk(); };
        talk.MouseUp += async (_, _) => await StopTalk();
        talk.MouseCaptureChanged += async (_, _) => { if (!talk.Capture) await StopTalk(); };
        fullscreen.Click += (_, _) => ToggleFullscreen(); snapshot.Click += (_, _) => _ = TakeSnapshot();
        snapshots.Click += async (_, _) => { await StopTalk(); OpenSnapshots(); };
        settings.Click += async (_, _) => await OpenSettings();
        KeyDown += OnKeyDown;
        KeyUp += async (_, e) =>
        {
            if (e.KeyCode == Keys.Space) { spaceDown = false; e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Menu) { e.Handled = e.SuppressKeyPress = true; await ReleaseAlt(); }
        };
        talkArm.Tick += (_, _) => { talkArm.Stop(); if (altDown && !altChord && !talking && (ModifierKeys & Keys.Alt) != 0) { BeginTalk(); altTalk = talking; } };
        pause.Click += (_, _) => _ = TogglePause();
        telemetry.Tick += (_, _) => RefreshTelemetry(); telemetry.Start();
        meterTimer.Tick += (_, _) =>
        {
            // A missed Alt key-up (for example on the secure desktop) must not leave the microphone live.
            if (altTalk && (ModifierKeys & Keys.Alt) == 0) _ = ReleaseAlt();
            float level = talking ? tutk?.MicrophonePeak ?? backchannel?.MicrophonePeak ?? 0 : 0;
            talk.Level = talkLive ? level : -1;
            if (overlay != null && talkLive) { overlay.MicLevel = level; overlay.Redraw(); }
        };
        FormClosing += OnClosing;
        Move += (_, _) => SyncOverlay(); Resize += (_, _) => SyncOverlay(); stage.Resize += (_, _) => SyncOverlay(); VisibleChanged += (_, _) => SyncOverlay();
        if (list.Items.Count > 0) { int selected = cameras.FindIndex(c => c.Id == preferences.LastCamera); list.SelectedIndex = Math.Max(0, selected); }
        else FillProfiles();
        if (CameraStore.LoadError != null && !preview) SetStatus(CameraStore.LoadError);
        UpdateControls();
    }

    private void BuildLayout()
    {
        int S(int value) => Theme.S(this, value);
        sidebar.Width = S(248); sidebar.BackColor = Theme.Chrome; sidebar.Padding = new(S(12), S(8), S(12), S(12));
        sideLine.BackColor = Theme.Line;
        var brand = new BrandMark { Dock = DockStyle.Top, Height = S(56) };
        var heading = new Panel { Dock = DockStyle.Top, Height = S(40) };
        var caption = Theme.Label("CAMERAS", 8, Theme.Muted, true); caption.Location = new(S(8), S(13));
        add.Size = new(S(30), S(30));
        heading.Resize += (_, _) => add.Location = new(heading.Width - add.Width, S(6));
        heading.Controls.AddRange([caption, add]);
        var nav = new Panel { Dock = DockStyle.Bottom, Height = S(97) };
        snapshots.Dock = settings.Dock = DockStyle.Top; snapshots.Height = settings.Height = S(38);
        nav.Controls.Add(settings); nav.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(4) }); nav.Controls.Add(snapshots);
        nav.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(16) }); nav.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line });
        sidebar.Controls.Add(list); sidebar.Controls.Add(heading); sidebar.Controls.Add(brand); sidebar.Controls.Add(nav);

        stage.BackColor = Theme.Stage; stage.Controls.Add(frozen); stage.Controls.Add(video); stage.Controls.Add(empty);
        bar.Height = S(68); bar.BackColor = Theme.Chrome;
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line });
        connect.Text = "Disconnect"; connect.Width = connect.PreferredWidth() + S(4); connect.Text = "Connect";
        talk.Text = "Hold to talk"; talk.Width = talk.PreferredWidth() + S(12);
        volume.Width = S(110); volume.Height = S(38);
        bar.Controls.AddRange([connect, quality, mute, volume, volumeLabel, talk, pause, snapshot, fullscreen]);
        bar.Layout += (_, _) => LayoutBar();
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Stage };
        body.Controls.Add(stage); body.Controls.Add(bar);
        Controls.Add(body); Controls.Add(sideLine); Controls.Add(sidebar);

        tips.SetToolTip(connect, "Connect to the selected camera, or stop the connection and its retries.");
        tips.SetToolTip(quality, "Video quality");
        tips.SetToolTip(volume, "Camera volume. Scroll to adjust.");
        tips.SetToolTip(snapshot, "Save the current frame as PNG (F9)");
        tips.SetToolTip(pause, "Pause the picture (Space)");
        tips.SetToolTip(fullscreen, "Fullscreen (F11)");
        tips.SetToolTip(add, "Add camera");
        tips.SetToolTip(snapshots, "Open the snapshot folder");
        tips.SetToolTip(settings, "Connection, video, microphone and snapshot settings");
    }
    private void LayoutBar()
    {
        int S(int value) => Theme.S(this, value);
        int h = S(38), y = 1 + (bar.Height - 1 - h) / 2, pad = S(16);
        connect.SetBounds(pad, y, connect.Width, h);
        quality.SetBounds(connect.Right + S(8), y, quality.Width, h);
        fullscreen.SetBounds(bar.Width - pad - h, y, h, h);
        snapshot.SetBounds(fullscreen.Left - S(4) - h, y, h, h);
        pause.SetBounds(snapshot.Left - S(4) - h, y, h, h);
        int left = quality.Right + S(24), right = pause.Left - S(24);
        // Narrow windows drop the volume percentage and shorten the slider, so the
        // group never covers Pause or Snapshot. UpdateMinimumWidth keeps this fitting.
        bool compact = right - left < MiddleWidth(false);
        int middle = MiddleWidth(compact);
        int x = Math.Clamp((bar.Width - middle) / 2, left, Math.Max(left, right - middle));
        mute.SetBounds(x, y, h, h);
        volume.SetBounds(mute.Right + S(2), y, S(compact ? 80 : 110), h);
        volumeLabel.Visible = !compact;
        volumeLabel.Location = new(volume.Right + S(6), y + (h - volumeLabel.Height) / 2);
        talk.SetBounds(x + middle - talk.Width, y, talk.Width, h);
    }
    // Mute, volume (with its percentage unless compact) and Hold to talk.
    private int MiddleWidth(bool compact)
    {
        int S(int value) => Theme.S(this, value);
        return S(38) + S(2) + (compact ? S(80) : S(110) + S(6) + S(40)) + S(16) + talk.Width;
    }
    // The window never gets narrower than the bottom bar in its compact layout.
    private void UpdateMinimumWidth()
    {
        if (inFullscreen) return;
        int S(int value) => Theme.S(this, value);
        int barWidth = S(16) + connect.Width + S(8) + quality.Width + S(24) + MiddleWidth(true) + S(24) + 3 * S(38) + 2 * S(4) + S(16);
        int width = sidebar.Width + sideLine.Width + barWidth + Width - ClientSize.Width;
        MinimumSize = new(Math.Min(Math.Max(S(1000), width), Screen.FromControl(this).WorkingArea.Width), MinimumSize.Height);
    }
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && !canClose) action(); }); } catch (InvalidOperationException) { }
    }
    // Messages appear as a short toast over the video or the idle stage.
    private void SetStatus(string text)
    {
        lastStatus = text;
        if (overlay?.Visible == true) overlay.Toast(text);
        else if (!verification) empty.Toast(text);
    }
    private void SavePreferences() { if (!preview && !verification) { try { preferences.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Preferences could not be saved."); } } }
    private void SaveCameras()
    {
        if (preview) return;
        try { CameraStore.Save(cameras); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Cameras could not be saved: " + ex.Message); }
    }
    private void LoadMicrophones()
    {
        microphoneNames.Clear();
        try { for (int i = 0; i < WaveIn.DeviceCount; i++) microphoneNames.Add(WaveIn.GetCapabilities(i).ProductName); }
        catch (Exception) { SetStatus("Windows could not list microphones. Viewing still works."); }
        tips.SetToolTip(talk, microphoneNames.Count == 0 ? "No microphone found." : "Hold to talk (hold Alt). Uses " + MicrophoneName + ". Release to stop.");
    }
    private int MicrophoneIndex() => Math.Max(0, microphoneNames.IndexOf(preferences.Microphone));
    private string MicrophoneName => microphoneNames.Count == 0 ? "" : microphoneNames[MicrophoneIndex()];
    private void FillProfiles()
    {
        filling = true; quality.Reset();
        if (list.SelectedItem is CameraConfig camera)
        {
            foreach (var profile in camera.Profiles) quality.Items.Add(profile);
            if (quality.Items.Count > 0) { camera.SelectedProfile = Math.Clamp(camera.SelectedProfile, 0, quality.Items.Count - 1); quality.SelectedIndex = camera.SelectedProfile; }
        }
        quality.Width = Math.Clamp(quality.PreferredWidth(), Theme.S(this, 84), Theme.S(this, 210));
        filling = false; UpdateMinimumWidth(); bar.PerformLayout();
        if (sessionOperation == null) { list.SetState(null, CameraState.None); ShowStageIdle(); }
        UpdateControls();
    }
    private void ShowStageIdle()
    {
        if (list.SelectedItem is not CameraConfig camera)
        {
            if (cameras.Count == 0) empty.Show(StageMode.Empty, "Add your first camera", "HDCameraPro UID cameras, ONVIF cameras and RTSP/HTTP streams are supported.", "Add camera", Glyphs.Add);
            else empty.Show(StageMode.Empty, "Select a camera", "Choose a camera on the left to view it.");
            return;
        }
        empty.Show(StageMode.Idle, camera.Name, "Ready to connect · " + CameraList.Describe(camera), "Connect", Glyphs.Play);
    }
    private void UpdateControls()
    {
        bool session = sessionOperation != null || connecting || connected;
        list.Locked = session || shuttingDown;
        add.Enabled = !session && !shuttingDown;
        connect.Enabled = list.SelectedItem != null && !shuttingDown;
        string label = connected ? "Disconnect" : session ? "Cancel" : "Connect";
        if (connect.Text != label) { connect.Text = label; connect.Glyph = connected ? Glyphs.Stop : session ? Glyphs.Cancel : Glyphs.Play; connect.Kind = session ? ButtonKind.Secondary : ButtonKind.Primary; }
        quality.Enabled = !connecting && !changingQuality && quality.Items.Count > 1 && !shuttingDown;
        talk.Enabled = connected && player.IsPlaying && microphoneNames.Count > 0 && !changingQuality && !cleaning && talkCleanup == null;
        snapshot.Enabled = pause.Enabled = connected && (player.IsPlaying || paused);
        pause.Glyph = paused ? Glyphs.Play : Glyphs.Pause;
        tips.SetToolTip(pause, paused ? "Resume live view (Space)" : "Pause the picture (Space)");
        UpdateOverlay();
    }
    private void UpdateOverlay()
    {
        if (overlay == null) return;
        overlay.CameraName = active?.Name ?? ""; overlay.Muted = muted; overlay.Volume = preferences.Volume; overlay.FullscreenMode = inFullscreen;
        overlay.TalkEnabled = talk.Enabled || talking; overlay.TalkLive = talkLive; overlay.TalkOpening = talking && !talkLive; overlay.TalkHint = "Hold to talk (hold Alt)";
        overlay.Paused = paused; overlay.PauseEnabled = pause.Enabled;
        overlay.SnapshotEnabled = snapshot.Enabled; overlay.QualityEnabled = quality.Enabled; overlay.QualityText = quality.SelectedText;
        SyncOverlay(); overlay.Redraw();
    }
    private void SyncOverlay()
    {
        if (overlay == null || IsDisposed) return;
        bool show = video.Visible && connected && !cleaning && Visible && WindowState != FormWindowState.Minimized;
        overlay.Track(show ? stage.RectangleToScreen(stage.ClientRectangle) : Rectangle.Empty, show);
    }
    private void ShowCameraMenu(CameraConfig camera, Point at)
    {
        bool session = sessionOperation != null || connecting || connected;
        var menu = Menus.Create();
        if (session && active?.Id == camera.Id) Menus.Add(menu, connected ? "Disconnect" : "Cancel connection", Glyphs.Stop, () => _ = StopSession());
        else Menus.Add(menu, "Connect", Glyphs.Play, () => _ = StartSession(), enabled: !session);
        Menus.Separator(menu);
        Menus.Add(menu, "Edit…", Glyphs.Edit, () => EditCamera(camera), enabled: !session);
        Menus.Add(menu, "Delete…", Glyphs.Delete, () => RemoveCamera(camera), enabled: !session, danger: true);
        menu.Show(at);
    }
    private void ShowQualityMenu()
    {
        if (overlay == null || !quality.Enabled || quality.Items.Count == 0) return;
        var menu = Menus.Create();
        for (int i = 0; i < quality.Items.Count; i++) { int index = i; Menus.Add(menu, quality.Items[i]?.ToString() ?? "", "", () => quality.SelectedIndex = index, check: i == quality.SelectedIndex); }
        var anchor = overlay.ScreenBoundsOf(HudAction.Quality);
        overlay.Holding = true; menu.Closed += (_, _) => overlay.Holding = false;
        menu.Show(new Point(anchor.Left, anchor.Top - Theme.S(this, 6)), ToolStripDropDownDirection.AboveRight);
    }
    private void EditCamera(CameraConfig? camera)
    {
        using var dialog = new CameraDialog(camera);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Camera == null) return;
        if (camera != null) { int index = cameras.IndexOf(camera); cameras[index] = dialog.Camera; list.Items[index] = dialog.Camera; list.Reload(); list.SelectedIndex = index; }
        else { cameras.Add(dialog.Camera); list.Items.Add(dialog.Camera); list.Reload(); list.SelectedIndex = list.Items.Count - 1; }
        SaveCameras(); FillProfiles(); SetStatus(dialog.Camera.Name + " saved.");
    }
    private void RemoveCamera(CameraConfig camera)
    {
        if (!Prompt.Ask(this, "Delete camera", $"Remove “{camera.Name}” from this PC? Its saved password is removed too.", "Delete", true)) return;
        int index = cameras.IndexOf(camera);
        cameras.Remove(camera); list.Items.Remove(camera); list.Reload(); SaveCameras();
        list.SelectedIndex = list.Items.Count == 0 ? -1 : Math.Min(index, list.Items.Count - 1);
        FillProfiles();
    }
    private async Task OpenSettings()
    {
        await StopTalk(); LoadMicrophones();
        using var dialog = new SettingsDialog(preferences, microphoneNames);
        if (dialog.ShowDialog(this) == DialogResult.OK) { LoadMicrophones(); UpdateControls(); }
    }
    private async Task StartSession()
    {
        if (sessionOperation != null || list.SelectedItem is not CameraConfig camera || preview) return;
        connectionStop = new(); active = camera;
        sessionOperation = SessionLoop(camera, connectionStop.Token); UpdateControls();
        try { await sessionOperation; }
        finally { sessionOperation = null; connectionStop.Dispose(); connectionStop = null; active = null; UpdateControls(); }
    }
    private async Task StopSession()
    {
        connectionStop?.Cancel(); talkStop?.Cancel();
        if (sessionOperation != null) await sessionOperation;
    }
    private async Task SessionLoop(CameraConfig camera, CancellationToken ct)
    {
        string reason = ""; string? failure = null; bool wasLive = false;
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    if (!preferences.AutoReconnect || !RetryPolicy.CanRetry(reason)) break;
                    connecting = true; list.SetState(camera.Id, CameraState.Connecting);
                    empty.Show(StageMode.Busy, "Connection lost", $"{reason} Retrying in {RetryPolicy.DelaySeconds(attempt)} s · attempt {attempt} of 3"); UpdateControls();
                    await Task.Delay(TimeSpan.FromSeconds(RetryPolicy.DelaySeconds(attempt)), ct);
                }
                streamFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
                DateTime? liveSince = null;
                await sessionGate.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested(); connecting = true; connected = false; list.SetState(camera.Id, CameraState.Connecting); UpdateControls();
                    empty.Show(StageMode.Busy, (attempt == 0 ? "Connecting to " : "Reconnecting to ") + camera.Name, "You can cancel at any time. The microphone stays off.");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(50000);
                    if (camera.Kind == CameraKind.Onvif)
                    {
                        using var client = new OnvifClient(camera.Address, camera.Username, camera.Password());
                        camera.Profiles = await client.ProfilesAsync(deadline.Token); FillProfiles(); SaveCameras();
                    }
                    if (camera.Kind == CameraKind.Tutk)
                    {
                        var device = new TutkCamera(vlc); tutk = device;
                        var failure0 = streamFailure;
                        device.Status += text => Ui(() => { if (tutk == device && !cleaning && connected) SetStatus(text); });
                        device.Failed += text => failure0.TrySetResult(text);
                        device.TalkFailed += text => Ui(() => { if (tutk == device) { SetStatus(text); _ = StopTalk(); } });
                        device.Mute(muted); device.SetVolume(preferences.Volume);
                        media = await device.ConnectAsync(camera, deadline.Token, preferences.BufferMilliseconds);
                    }
                    else CreateNetworkMedia(camera);
                    if (!preferences.HardwareDecoding) media!.AddOption(":avcodec-hw=none");
                    player.EnableHardwareDecoding = preferences.HardwareDecoding;
                    connected = true; liveSince = openedAt = connectedAt = lastDecodedAt = DateTime.UtcNow; lastDisplayed = 0; statisticsObserved = false; previousBytes = previousFrames = 0;
                    if (overlay != null) { overlay.Stats = ""; overlay.Elapsed = ""; }
                    if (!player.Play(media!)) throw new IOException("Video decoder could not start.");
                    ApplySound(); video.Visible = true; empty.Visible = false; wasLive = true;
                    connecting = false; UpdateControls();
                    if (tutk != null)
                    {
                        bool confirmed = await tutk.SetQualityAsync(camera.SelectedProfile, deadline.Token);
                        if (!confirmed) SetStatus(quality.SelectedText + " requested; waiting for the camera to confirm.");
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { reason = ex is OperationCanceledException ? "Connection timed out." : ex.Message; streamFailure.TrySetResult(reason); }
                finally { sessionGate.Release(); }
                reason = await streamFailure.Task.WaitAsync(ct);
                bool recovered = RetryPolicy.IsNewOutage(liveSince, DateTime.UtcNow);
                await CleanupSession();
                // A stream that stayed up failed in a new outage, which gets the full retry budget again.
                if (recovered) attempt = 0;
                if (attempt == 3) break;
            }
            failure = reason;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            await CleanupSession(); connecting = connected = false;
            if (failure != null)
            {
                lastStatus = failure; list.SetState(camera.Id, CameraState.Offline);
                empty.Show(StageMode.Problem, wasLive ? "Connection lost" : "Couldn’t connect to " + camera.Name, failure, "Try again", Glyphs.Refresh);
            }
            else { lastStatus = "Disconnected."; list.SetState(null, CameraState.None); ShowStageIdle(); }
            UpdateControls();
        }
    }
    private void CreateNetworkMedia(CameraConfig camera)
    {
        if (camera.Profiles.Count == 0) throw new IOException("The camera has no video profiles.");
        var profile = camera.Profiles[Math.Clamp(camera.SelectedProfile, 0, camera.Profiles.Count - 1)];
        media = new(vlc, CameraUri.WithCredentials(profile.Uri, camera.Username, camera.Password()), FromType.FromLocation);
        media.AddOption(":network-caching=" + preferences.BufferMilliseconds); media.AddOption(":rtsp-tcp"); media.AddOption(":no-video-title-show");
    }
    private void SignalFailure(string reason)
    {
        if (connected && !cleaning && !changingQuality && connectionStop?.IsCancellationRequested == false) streamFailure?.TrySetResult(reason);
    }
    private async Task CleanupSession()
    {
        await sessionGate.WaitAsync();
        try
        {
            cleaning = true; connected = false;
            // A frame capture still uses the player; let it finish before stopping.
            if (frameCapture is { } capture) await Task.WhenAny(capture);
            Resume(); SyncOverlay(); await StopTalk();
            var retiring = tutk; tutk = null; retiring?.PrepareStop();
            await Task.Run(() => player.Stop());
            if (retiring != null) await Task.Run(async () => await retiring.DisposeAsync()); else media?.Dispose();
            media = null; video.Visible = false; empty.Visible = true; audioCodec = null;
        }
        finally { cleaning = false; sessionGate.Release(); SyncOverlay(); }
    }
    private async Task ChangeQuality()
    {
        if (filling || quality.SelectedIndex < 0 || list.SelectedItem is not CameraConfig camera) return;
        int selected = quality.SelectedIndex, previous = camera.SelectedProfile;
        if (!connected) { camera.SelectedProfile = selected; SaveCameras(); return; }
        changingQuality = true; UpdateControls(); await sessionGate.WaitAsync();
        try
        {
            // The session ended while waiting: keep the choice for the next connection.
            if (!connected || connectionStop == null) { camera.SelectedProfile = selected; SaveCameras(); return; }
            await StopTalk();
            if (tutk != null)
            {
                bool confirmed = await tutk.SetQualityAsync(selected, connectionStop.Token);
                SetStatus(confirmed ? "Quality: " + quality.SelectedText : quality.SelectedText + " requested; the camera has not confirmed it yet.");
            }
            else
            {
                await Task.Run(() => player.Stop()); media?.Dispose(); camera.SelectedProfile = selected; CreateNetworkMedia(camera);
                if (!preferences.HardwareDecoding) media!.AddOption(":avcodec-hw=none");
                if (!player.Play(media!)) throw new IOException("The selected stream could not start.");
                openedAt = lastDecodedAt = DateTime.UtcNow; lastDisplayed = 0;
            }
            camera.SelectedProfile = selected; SaveCameras();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { camera.SelectedProfile = previous; filling = true; quality.SelectedIndex = previous; filling = false; SetStatus(ex.Message); }
        finally { changingQuality = false; sessionGate.Release(); UpdateControls(); }
    }
    private void ToggleMute() { muted = !muted; preferences.Muted = muted; ApplySound(); SavePreferences(); }
    private void ApplySound()
    {
        player.Volume = preferences.Volume; player.Mute = muted; tutk?.SetVolume(preferences.Volume); tutk?.Mute(muted);
        mute.Glyph = muted ? Glyphs.Mute : Glyphs.Volume; volumeLabel.Text = preferences.Volume + "%";
        string tip = (muted ? "Unmute (M)" : "Mute (M)") + (audioCodec == null ? "" : " · camera audio " + audioCodec);
        if (tip != muteTip) { muteTip = tip; tips.SetToolTip(mute, tip); }
        UpdateOverlay();
    }
    private void BeginTalk()
    {
        if (talking || talkCleanup != null || !talk.Enabled || active == null || shuttingDown) return;
        talking = true; talkStop = new(); talk.Text = "Opening…"; UpdateControls();
        talkOperation = StartTalk(talkStop.Token);
    }
    private async Task StartTalk(CancellationToken ct)
    {
        try
        {
            int microphoneDevice = MicrophoneIndex();
            if (tutk != null) await tutk.StartTalkAsync(microphoneDevice, ct, !verification);
            else
            {
                var camera = active!; var channel = new Backchannel(camera.Profiles[camera.SelectedProfile].Uri, camera.Username, camera.Password()); backchannel = channel;
                channel.Failed += text => Ui(() => { if (backchannel == channel) { SetStatus(text); _ = StopTalk(); } });
                await channel.OpenAsync(ct); ct.ThrowIfCancellationRequested(); if (!verification) channel.StartMicrophone(microphoneDevice);
            }
            ct.ThrowIfCancellationRequested();
            talkLive = true; talk.Text = "Talking"; talk.Alert = true; talk.Level = 0; meterTimer.Start(); UpdateOverlay();
            _ = TalkTimeout(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message); Ui(() => { _ = StopTalk(); }); }
    }
    // The one-minute limit counts from when the microphone went live. Only
    // StopTalk cancels ct, so reaching the limit always runs the full cleanup.
    private async Task TalkTimeout(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); await StopTalk(); SetStatus("Talk stopped after one minute. Hold to talk again."); }
        catch (OperationCanceledException) { }
    }
    private async Task StopTalk()
    {
        talking = false; talkStop?.Cancel();
        if (talkCleanup != null) { await talkCleanup; return; }
        if (talkOperation == null && talkStop == null && backchannel == null) return;
        talkCleanup = CleanupTalk(); UpdateControls();
        try { await talkCleanup; }
        finally { talkCleanup = null; UpdateControls(); }
    }
    private async Task CleanupTalk()
    {
        await talkGate.WaitAsync();
        try
        {
            if (talkOperation != null) { await talkOperation; talkOperation = null; }
            if (backchannel != null) { await backchannel.DisposeAsync(); backchannel = null; }
            if (tutk != null) await tutk.StopTalkAsync();
            talkStop?.Dispose(); talkStop = null;
        }
        catch (Exception ex) { SetStatus("Talk stopped: " + ex.Message); }
        finally
        {
            meterTimer.Stop(); talkLive = altTalk = false; talk.Level = -1; talk.Text = "Hold to talk"; talk.Alert = false;
            if (overlay != null) overlay.MicLevel = 0;
            UpdateOverlay(); talkGate.Release();
        }
    }
    private void RefreshTelemetry()
    {
        if (!connected || cleaning || changingQuality) return;
        try
        {
            var stats = media?.Statistics;
            int displayed = stats?.DisplayedPictures ?? 0;
            DateTime now = DateTime.UtcNow;
            if (displayed != lastDisplayed) { lastDisplayed = displayed; lastDecodedAt = now; statisticsObserved = true; }
            if (now - openedAt > TimeSpan.FromSeconds(15) && (!player.IsPlaying || statisticsObserved && now - lastDecodedAt > TimeSpan.FromSeconds(15))) SignalFailure("No new video frames received for 15 seconds.");
            uint width = 0, height = 0; player.Size(0, ref width, ref height);
            string detail = width > 0 ? $"{width}×{height}" : "";
            if (tutk != null)
            {
                long frames = tutk.FrameCount, bytes = tutk.ReceivedBytes, lost = tutk.LostFrames;
                detail = (detail.Length > 0 ? detail + "  ·  " : "") + $"{tutk.VideoCodec}  ·  {Math.Max(0, frames - previousFrames)} fps  ·  {Math.Max(0, bytes - previousBytes) * 8 / 1000} kb/s" + (lost > 0 ? $"  ·  {lost} lost" : "");
                previousFrames = frames; previousBytes = bytes;
                if (now - tutk.LastFrameUtc > TimeSpan.FromSeconds(15)) SignalFailure("Camera stopped sending video.");
                if (tutk.AudioCodec != audioCodec) { audioCodec = tutk.AudioCodec; ApplySound(); }
            }
            var elapsed = now - connectedAt;
            if (overlay != null) { overlay.Stats = detail; overlay.Elapsed = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"; overlay.Redraw(); }
        }
        catch (ObjectDisposedException) { }
    }
    // libVLC writes a snapshot synchronously (up to its 500 ms timeout) and reports
    // success even when no file was written, so frames are captured off the UI
    // thread, one at a time, and checked on disk.
    private async Task<T> CaptureFrame<T>(Func<T> capture)
    {
        var work = Task.Run(capture); frameCapture = work;
        try { return await work; }
        finally { frameCapture = null; }
    }
    private bool SnapshotTo(string path) => player.TakeSnapshot(0, path, 0, 0) && File.Exists(path);
    private async Task TakeSnapshot()
    {
        if (!connected || !player.IsPlaying || frameCapture != null) return;
        try
        {
            Directory.CreateDirectory(preferences.SnapshotFolder);
            string name = string.Concat((active?.Name ?? "Camera").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(preferences.SnapshotFolder, name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            // While paused, save the frame that is on screen rather than the live one.
            if (paused && frozenPath != null) File.Copy(frozenPath, path);
            else if (!await CaptureFrame(() => SnapshotTo(path))) { SetStatus("No frame could be saved yet. Try again in a moment."); return; }
            overlay?.Flash(); SetStatus("Snapshot saved · " + Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { SetStatus("Snapshot could not be saved: " + ex.Message); }
    }
    private async Task TogglePause()
    {
        if (paused) { Resume(); overlay?.Reveal(); return; }
        if (!connected || !player.IsPlaying || frameCapture != null) return;
        string path = Path.Combine(Path.GetTempPath(), $"CameraLite-paused-{Environment.ProcessId}.png");
        Bitmap? frame;
        try
        {
            frame = await CaptureFrame(() =>
            {
                if (!SnapshotTo(path)) return null;
                using var stream = File.OpenRead(path); using var image = Image.FromStream(stream); return new Bitmap(image);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { SetStatus("The picture could not be paused: " + ex.Message); return; }
        // The session may have ended while the frame was captured.
        if (frame == null || !connected || cleaning) { frame?.Dispose(); DeleteQuietly(path); if (connected) SetStatus("No frame to pause yet. Try again in a moment."); return; }
        frozen.Image = frame; frozenPath = path; paused = true; frozen.Visible = true; frozen.BringToFront();
        UpdateControls(); overlay?.Reveal();
    }
    private void Resume()
    {
        if (!paused) return;
        paused = false; frozen.Visible = false;
        var image = frozen.Image; frozen.Image = null; image?.Dispose();
        if (frozenPath != null) { DeleteQuietly(frozenPath); frozenPath = null; }
        UpdateControls();
    }
    private static void DeleteQuietly(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private void OpenSnapshots()
    {
        try { Directory.CreateDirectory(preferences.SnapshotFolder); Process.Start(new ProcessStartInfo(preferences.SnapshotFolder) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool typing = ActiveControl is TextBox;
        if (altDown && e.KeyCode != Keys.Menu)
        {
            // Alt is part of a shortcut such as Alt+F4 or Alt+Enter, so it never talks.
            altChord = true; talkArm.Stop();
            if (altTalk) { altTalk = false; _ = StopTalk(); }
        }
        if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape && inFullscreen) { ToggleFullscreen(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.F9) { _ = TakeSnapshot(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.M && !e.Control && !e.Alt && !typing) { ToggleMute(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Space && !e.Alt && !typing) { if (!spaceDown) { spaceDown = true; _ = TogglePause(); } e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Menu)
        {
            // Ctrl+Alt (AltGr) and Shift+Alt start shortcuts, not talk.
            if (!altDown) { altDown = true; altChord = e.Control || e.Shift; if (!altChord) talkArm.Start(); }
            e.Handled = e.SuppressKeyPress = true;
        }
    }
    // Ends talk that Alt started, and resets Alt tracking.
    private Task ReleaseAlt()
    {
        altDown = altChord = false; talkArm.Stop();
        if (!altTalk) return Task.CompletedTask;
        altTalk = false; return StopTalk();
    }
    // Talk stops when another application takes focus; the app's own menus,
    // dialogs and video overlay do not interrupt it.
    protected override void WndProc(ref Message m)
    {
        // Releasing Alt alone would otherwise put the window into menu mode.
        if (m.Msg == 0x112 && ((int)m.WParam & 0xFFF0) == 0xF100 && m.LParam == IntPtr.Zero) return;
        base.WndProc(ref m);
        if (m.Msg == 0x1C && m.WParam == IntPtr.Zero) { spaceDown = false; _ = ReleaseAlt(); _ = StopTalk(); }
        // The system menu (Alt+Space) swallows the Alt key-up.
        else if (m.Msg == 0x212) _ = ReleaseAlt();
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (canClose) return;
        e.Cancel = true; if (shuttingDown) return;
        shuttingDown = true; UpdateControls(); telemetry.Stop(); talkStop?.Cancel(); connectionStop?.Cancel();
        try { await StopSession(); await StopTalk(); SavePreferences(); overlay?.Dispose(); player.Dispose(); video.Dispose(); vlc.Dispose(); }
        finally { telemetry.Dispose(); meterTimer.Dispose(); talkArm.Dispose(); tips.Dispose(); Resume(); canClose = true; Close(); }
    }
    internal void ToggleFullscreen()
    {
        if (!inFullscreen)
        {
            savedBounds = Bounds; savedState = WindowState; WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds; sidebar.Visible = sideLine.Visible = bar.Visible = false;
        }
        else { FormBorderStyle = FormBorderStyle.Sizable; Bounds = savedBounds; WindowState = savedState; sidebar.Visible = sideLine.Visible = bar.Visible = true; }
        inFullscreen = !inFullscreen; fullscreen.Glyph = inFullscreen ? Glyphs.ExitFullScreen : Glyphs.FullScreen;
        UpdateMinimumWidth();
        tips.SetToolTip(fullscreen, inFullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (F11)");
        UpdateOverlay(); overlay?.Reveal();
    }
    internal async Task<object> VerifySessionUi()
    {
        if (!verification) throw new InvalidOperationException("Verification mode is required.");
        async Task WaitForVideo()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(50);
            while ((!connected || !player.IsPlaying) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!connected || !player.IsPlaying) throw new IOException(lastStatus);
            await sessionGate.WaitAsync(); sessionGate.Release();
        }
        var session = StartSession(); int opened = 0, silencePackets = 0;
        try
        {
            await WaitForVideo(); await Task.Delay(17000);
            bool stableVideo = connected && player.IsPlaying;
            ActiveControl = connect;
            for (int i = 0; i < 3; i++)
            {
                BeginTalk();
                if (talkOperation != null) await talkOperation;
                if (talking && talkLive) { opened++; await tutk!.SendSilentTalkFrameForCheck(CancellationToken.None); silencePackets++; }
                await StopTalk();
            }
            BeginTalk();
            if (talkOperation != null) await talkOperation;
            if (talking && talkLive) { await tutk!.SendSilentTalkFrameForCheck(CancellationToken.None); silencePackets++; await Task.Delay(100); }
            int oldWorker = tutk?.WorkerProcessId ?? -1;
            if (oldWorker <= 0) throw new IOException("The camera worker was not started: " + lastStatus);
            using (var worker = Process.GetProcessById(oldWorker)) worker.Kill(true);
            DateTime recoveryDeadline = DateTime.UtcNow.AddSeconds(45);
            while ((tutk?.WorkerProcessId == oldWorker || !connected || !player.IsPlaying) && DateTime.UtcNow < recoveryDeadline) await Task.Delay(100);
            await WaitForVideo();
            bool recoveredWithoutTalk = connected && !talking && !talkLive;
            bool workerReplaced = tutk?.WorkerProcessId > 0 && tutk.WorkerProcessId != oldWorker;
            UpdateControls();
            ActiveControl = connect;
            bool talkEnabledAfterRecovery = talk.Enabled;
            BeginTalk();
            bool talkAttemptedAfterRecovery = talking;
            if (talkOperation != null) await talkOperation;
            bool talkWorkedAfterRecovery = talking && talkLive;
            if (talkWorkedAfterRecovery) { await tutk!.SendSilentTalkFrameForCheck(CancellationToken.None); silencePackets++; }
            string talkRecoveryStatus = talkWorkedAfterRecovery ? "opened" : lastStatus;
            await StopTalk();
            await StopSession(); await session;
            bool disconnected = !connected && sessionOperation == null;
            session = StartSession(); await WaitForVideo(); bool reconnected = connected;
            var process = Process.GetCurrentProcess();
            var result = new { passedCoreChecks = stableVideo && opened == 3 && recoveredWithoutTalk && workerReplaced && disconnected && reconnected,
                fullRecoveryPassed = stableVideo && opened == 3 && recoveredWithoutTalk && workerReplaced && talkWorkedAfterRecovery && disconnected && reconnected,
                stableVideo, talkStartsAndStops = opened, recoveredWithoutTalk, workerReplaced, talkEnabledAfterRecovery, talkAttemptedAfterRecovery, talkWorkedAfterRecovery, talkRecoveryStatus, disconnected, reconnected, workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1), microphoneCaptured = false, audioTransmitted = silencePackets > 0, syntheticSilenceOnly = true, syntheticSilencePacketsSent = silencePackets };
            if (!result.passedCoreChecks) throw new IOException(System.Text.Json.JsonSerializer.Serialize(result));
            return result;
        }
        finally { await StopSession(); await session; }
    }
}

internal static class RetryPolicy
{
    // A connection that stayed live this long counts as recovered.
    internal static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);
    internal static int DelaySeconds(int attempt) => Math.Min(8, 1 << Math.Clamp(attempt, 1, 3));
    internal static bool CanRetry(string error) => !error.Contains("-20009", StringComparison.Ordinal) && !error.Contains("rejected", StringComparison.OrdinalIgnoreCase) && !error.Contains("incompatible", StringComparison.OrdinalIgnoreCase) && !error.Contains("IOTCAPIs.dll", StringComparison.OrdinalIgnoreCase) && error != SavedPasswordException.Text;
    internal static bool IsNewOutage(DateTime? liveSince, DateTime now) => liveSince is { } since && now - since >= StableAfter;
}
