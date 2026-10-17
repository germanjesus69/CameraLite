namespace CameraLite;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--camera-check")) { Environment.ExitCode = HardwareSmoke.Run().GetAwaiter().GetResult(); return; }
        if (args.Contains("--talk-check")) { Environment.ExitCode = HardwareSmoke.Run(true).GetAwaiter().GetResult(); return; }
        if (args.Contains("--session-ui-check"))
        {
            using var form = new MainForm(false, true) { ShowInTaskbar = false, Opacity = 0 };
            form.Shown += async (_, _) =>
            {
                try { File.WriteAllText("work/session-ui-test.json", System.Text.Json.JsonSerializer.Serialize(await form.VerifySessionUi())); }
                catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText("work/session-ui-test.json", System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message })); }
                finally { form.Close(); }
            };
            Application.Run(form); return;
        }
        if (args.Contains("--sdk-check"))
        {
            int i = Array.IndexOf(args, "--sdk-check");
            try { File.WriteAllText("work/tutk-sdk-test.json", System.Text.Json.JsonSerializer.Serialize(TutkNative.Probe(args[i + 1]))); }
            catch (Exception ex) { File.WriteAllText("work/tutk-sdk-test.json", System.Text.Json.JsonSerializer.Serialize(new { loaded = false, error = ex.ToString() })); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--tutk-discover"))
        {
            int i = Array.IndexOf(args, "--tutk-discover");
            try { File.WriteAllText("work/tutk-discovered-cameras.json", System.Text.Json.JsonSerializer.Serialize(TutkCamera.DiscoverAsync(args[i + 1], CancellationToken.None).GetAwaiter().GetResult())); }
            catch (Exception ex) { File.WriteAllText("work/tutk-discovery-error.json", System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message })); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--sdk-session-check"))
        {
            int i = Array.IndexOf(args, "--sdk-session-check");
            var devices = System.Text.Json.JsonSerializer.Deserialize<List<DiscoveredCamera>>(File.ReadAllText("work/tutk-discovered-cameras.json"))!;
            var target = devices.Single(d => d.Address == "192.168.178.77");
            File.WriteAllText("work/tutk-session-test.json", System.Text.Json.JsonSerializer.Serialize(TutkNative.ProbeSession(args[i + 1], target.Uid).GetAwaiter().GetResult()));
            return;
        }
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTests.Run().GetAwaiter().GetResult(); return; }
        if (args.Contains("--discover"))
        {
            var found = Discovery.SearchAsync(CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText("work/camera-discovery.json", System.Text.Json.JsonSerializer.Serialize(found));
            return;
        }
        if (args.Contains("--playback-test")) { int i = Array.IndexOf(args, "--playback-test"); Environment.ExitCode = PlaybackSmoke.Run(args[i + 1], args[i + 2]); return; }
        if (args.Contains("--render-dialog"))
        {
            using var dialog = new CameraDialog(); dialog.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(args[Array.IndexOf(args, "--render-dialog") + 1]); dialog.Close(); return;
        }
        if (args.Contains("--ui-smoke"))
        {
            using var form = new MainForm(true); form.Show(); Application.DoEvents();
            var original = form.Bounds; form.ToggleFullscreen(); Application.DoEvents();
            bool fullscreen = form.FormBorderStyle == FormBorderStyle.None && form.Bounds == Screen.FromControl(form).Bounds;
            form.ToggleFullscreen(); Application.DoEvents(); bool restored = form.Bounds == original && form.FormBorderStyle == FormBorderStyle.Sizable;
            var process = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText("work/ui-test.json", System.Text.Json.JsonSerializer.Serialize(new { fullscreen, restored, idleWorkingSetMiB = Math.Round(process.WorkingSet64 / 1048576.0, 1), privateMiB = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1) }));
            form.Close(); Environment.ExitCode = fullscreen && restored ? 0 : 1; return;
        }
        try
        {
            var form = new MainForm(args.Contains("--render"));
            if (args.Contains("--render"))
            {
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(args[Array.IndexOf(args, "--render") + 1]);
                form.Close();
                return;
            }
            Application.Run(form);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "CameraLite", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
