namespace CameraLite;

internal sealed class SettingsDialog : Form
{
    internal SettingsDialog(Preferences preferences, IReadOnlyList<string> microphones)
    {
        Text = "Settings"; FormBorderStyle = FormBorderStyle.FixedDialog;
        Theme.Window(this);
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; BackColor = Theme.Chrome; ForeColor = Theme.Text; Font = Theme.Font(10);
        int S(int value) => Theme.S(this, value);
        int x = S(28), width = S(472), y = S(20);
        void Section(string title) { var label = Theme.Label(title, 8, Theme.Muted, true); label.Location = new(x, y + S(6)); Controls.Add(label); y += S(32); }
        Label Hint(string text, int indent = 0)
        {
            var label = new Label { Text = text, AutoSize = true, MaximumSize = new(width - indent, 0), ForeColor = Theme.Secondary, Font = Theme.Font(9), Location = new(x + indent, y) };
            Controls.Add(label); y += label.PreferredHeight + S(10); return label;
        }
        T Place<T>(T control, int height, int gap = 10) where T : Control { control.SetBounds(x, y, width, S(height)); Controls.Add(control); y += S(height) + S(gap); return control; }

        Section("CONNECTION");
        var reconnect = Place(new Toggle("Reconnect automatically if the stream drops", preferences.AutoReconnect), 30, 18);
        Section("VIDEO");
        var hardware = Place(new Toggle("Use hardware video decoding", preferences.HardwareDecoding), 30, 2);
        Hint("Software decoding is the stable default on this PC.", S(52));
        y += S(4);
        var bufferLabel = Theme.Label("Playback buffer", 9.5f, Theme.Secondary); bufferLabel.Location = new(x, y); Controls.Add(bufferLabel); y += S(24);
        var buffer = Place(new ChoiceButton(true), 38, 6);
        buffer.Items.AddRange(["150 ms · fastest response", "250 ms · balanced", "500 ms · unstable networks", "1000 ms · smoother remote view"]);
        int[] durations = [150, 250, 500, 1000]; buffer.SelectedIndex = Math.Max(0, Array.IndexOf(durations, preferences.BufferMilliseconds));
        Hint("Decoder and buffer changes apply on the next connection.");
        y += S(8);
        Section("MICROPHONE");
        var microphone = Place(new ChoiceButton(true), 38, 6);
        microphone.Items.AddRange(microphones.Count > 0 ? microphones : ["No microphone found"]);
        microphone.SelectedIndex = Math.Max(0, microphones.ToList().IndexOf(preferences.Microphone)); microphone.Enabled = microphones.Count > 0;
        Hint("Used for Hold to talk. It is only opened while you hold the button or Alt, and nothing is recorded.");
        y += S(8);
        Section("SNAPSHOTS");
        var folder = new FieldBox(preferences.SnapshotFolder); folder.SetBounds(x, y, width - S(106), S(38)); Controls.Add(folder);
        var browse = new ActionButton("Browse", Glyphs.Folder) { Width = S(96), Height = S(38), Location = new(x + width - S(96), y) }; Controls.Add(browse);
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = folder.Value }; if (picker.ShowDialog(this) == DialogResult.OK) folder.Value = picker.SelectedPath; };
        y += S(38) + S(28);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(70), BackColor = Theme.Raised };
        footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });
        var save = new ActionButton("Save", Glyphs.Check, ButtonKind.Primary); save.Width = Math.Max(save.Width, S(104));
        var cancel = new ActionButton("Cancel") { DialogResult = DialogResult.Cancel }; cancel.Width = Math.Max(cancel.Width, S(96));
        footer.Controls.AddRange([save, cancel]);
        ClientSize = new(width + x * 2, y + footer.Height);
        save.Location = new(ClientSize.Width - x - save.Width, (footer.Height - save.Height) / 2);
        cancel.Location = new(save.Left - S(10) - cancel.Width, save.Top);
        Controls.Add(footer);
        save.Click += (_, _) =>
        {
            try
            {
                if (!Path.IsPathFullyQualified(folder.Value)) throw new ArgumentException("Choose a full snapshot folder path, for example C:\\Users\\you\\Pictures\\CameraLite.");
                preferences.AutoReconnect = reconnect.Checked; preferences.HardwareDecoding = hardware.Checked;
                preferences.BufferMilliseconds = durations[Math.Max(0, buffer.SelectedIndex)]; preferences.SnapshotFolder = Path.GetFullPath(folder.Value);
                if (microphones.Count > 0) preferences.Microphone = microphone.SelectedText;
                preferences.Save();
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Prompt.Tell(this, "Settings", ex.Message); }
        };
        AcceptButton = save; CancelButton = cancel;
    }
}
