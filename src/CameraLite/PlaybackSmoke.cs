using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
namespace CameraLite;
internal static class PlaybackSmoke
{
    internal static int Run(string path, string snapshot)
    {
        Core.Initialize();
        using var vlc = new LibVLC("--quiet", "--no-audio", "--no-video-title-show");
        using var player = new MediaPlayer(vlc);
        using var form = new Form { Size = new(640, 420), ShowInTaskbar = false, Opacity = 0 };
        using var view = new VideoView { Dock = DockStyle.Fill, MediaPlayer = player };
        form.Controls.Add(view); form.Show();
        using var media = new Media(vlc, path, FromType.FromPath);
        player.Play(media);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        bool captured = false;
        while (DateTime.UtcNow < deadline && !captured)
        {
            Application.DoEvents();
            if (player.Time > 350) captured = player.TakeSnapshot(0, snapshot, 640, 0);
            Thread.Sleep(15);
        }
        bool muted = player.Mute = true;
        player.Stop();
        File.WriteAllText("work/playback-test.json", System.Text.Json.JsonSerializer.Serialize(new { decoded = captured, snapshot, mute = muted }));
        return captured && muted ? 0 : 1;
    }
}
