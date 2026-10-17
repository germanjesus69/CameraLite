using System.Text.Json;
using LibVLCSharp.Shared;

namespace CameraLite;

internal static class HardwareSmoke
{
    // Uses the saved account without exposing it. Opens the return-audio channel
    // without opening a PC microphone. The short talk check sends synthetic silence.
    internal static async Task<int> Run(bool shortTalkCheck = false)
    {
        Directory.CreateDirectory("work");
        var result = new Dictionary<string, object> { ["microphoneCaptured"] = false, ["audioTransmitted"] = false };
        Core.Initialize(); using var vlc = new LibVLC("--quiet", "--no-audio", "--vout=dummy", "--avcodec-hw=none");
        using var player = new MediaPlayer(vlc) { EnableHardwareDecoding = false };
        await using var device = new TutkCamera(vlc);
        try
        {
            var camera = CameraStore.Load().First(c => c.Kind == CameraKind.Tutk);
            using var deadline = new CancellationTokenSource(120000);
            device.Mute(true); device.SetVolume(0);
            var media = await device.ConnectAsync(camera, deadline.Token);
            media.AddOption(":avcodec-hw=none"); player.Play(media);
            await Task.Delay(6000, deadline.Token);
            result["videoFramesReceived"] = device.FrameCount; result["videoCodec"] = device.VideoCodec;
            result["videoFramesDecoded"] = media.Statistics.DecodedVideo;
            uint width = 0, height = 0; player.Size(0, ref width, ref height); result["decodedWidth"] = width; result["decodedHeight"] = height;
            result["audioCodec"] = device.AudioCodec;
            result["qualityConfirmed"] = await device.SetQualityAsync(camera.SelectedProfile, deadline.Token);
            var profiles = new List<object>();
            try
            {
                for (int profile = 0; profile < 3; profile++)
                {
                    bool confirmed = await device.SetQualityAsync(profile, deadline.Token); await Task.Delay(1300, deadline.Token);
                    width = height = 0; player.Size(0, ref width, ref height); profiles.Add(new { profile, confirmed, width, height });
                }
            }
            finally { await device.SetQualityAsync(camera.SelectedProfile, deadline.Token); }
            result["qualityProfiles"] = profiles;
            // Sustained pacing: if the decoder plays slower than the camera sends,
            // the receive and decode rates diverge and latency grows over time.
            await Task.Delay(3000, deadline.Token);
            long frames0 = device.FrameCount; int decoded0 = media.Statistics.DecodedVideo, shown0 = media.Statistics.DisplayedPictures;
            var watch = System.Diagnostics.Stopwatch.StartNew(); int maxBacklog = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(shortTalkCheck ? 2 : 40)) { await Task.Delay(250, deadline.Token); maxBacklog = Math.Max(maxBacklog, device.VideoBacklog); }
            double seconds = watch.Elapsed.TotalSeconds;
            result["sustainedSeconds"] = Math.Round(seconds, 1);
            result["receivedFps"] = Math.Round((device.FrameCount - frames0) / seconds, 2);
            result["decodedFps"] = Math.Round((media.Statistics.DecodedVideo - decoded0) / seconds, 2);
            result["displayedFps"] = Math.Round((media.Statistics.DisplayedPictures - shown0) / seconds, 2);
            result["maxQueuedFrames"] = maxBacklog; result["lostFrames"] = device.LostFrames;
            int opened = 0;
            for (int i = 0; i < 3; i++)
            {
                using var talkDeadline = new CancellationTokenSource(12000);
                await device.StartTalkAsync(0, talkDeadline.Token, false); opened++;
                if (shortTalkCheck) { await device.SendSilentTalkFrameForCheck(talkDeadline.Token); result["audioTransmitted"] = true; result["syntheticSilenceOnly"] = true; }
                await device.StopTalkAsync(); await Task.Delay(200);
            }
            result["talkChannelsOpenedAndClosed"] = opened;
            using (var release = new CancellationTokenSource(100))
            {
                bool cancelled = false;
                try { await device.StartTalkAsync(0, release.Token, false); }
                catch (OperationCanceledException) { cancelled = true; }
                finally { await device.StopTalkAsync(); }
                result["quickTalkReleaseHandled"] = cancelled;
            }
            result["passed"] = device.FrameCount > 0 && opened == 3;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.Message; }
        finally { device.PrepareStop(); player.Stop(); }
        File.WriteAllText(shortTalkCheck ? "work/talk-recovery-test.json" : "work/hardware-test.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result["passed"].Equals(true) ? 0 : 1;
    }
}
