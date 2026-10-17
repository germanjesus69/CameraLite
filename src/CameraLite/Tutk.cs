using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CameraLite.Transport;
using LibVLCSharp.Shared;
using NAudio.Codecs;
using NAudio.Wave;

namespace CameraLite;

// In-process SDK probes for the --sdk-check and --sdk-session-check maintenance
// commands only. Viewing, talk and LAN discovery load the SDK in the transport worker.
internal static class TutkNative
{
    private static readonly object loadLock = new();
    private static string? loadedFolder;
    private static IntPtr iotc, av;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Init(ushort port);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OneInt(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Close(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Version(out uint version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Connect([MarshalAs(UnmanagedType.LPStr)] string uid, int sid);
    internal static NoArgs GetSid = null!;
    internal static OneInt AbortConnect = null!;
    internal static Close CloseSid = null!;
    internal static Connect ConnectUid = null!;
    private static T Export<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public static void Load(string folder)
    {
        lock (loadLock) LoadCore(folder);
    }
    private static void LoadCore(string folder)
    {
        string full = Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? Path.Combine(AppContext.BaseDirectory, "vendor-sdk") : folder);
        string bundled = Path.Combine(AppContext.BaseDirectory, "vendor-sdk");
        if (!File.Exists(Path.Combine(full, "IOTCAPIs.dll")) && Path.GetFileName(full).Equals("vendor-sdk", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(full))?.StartsWith("CameraLite", StringComparison.OrdinalIgnoreCase) == true
            && File.Exists(Path.Combine(bundled, "IOTCAPIs.dll"))) full = Path.GetFullPath(bundled);
        if (loadedFolder != null)
        {
            if (!full.Equals(loadedFolder, StringComparison.OrdinalIgnoreCase)) throw new IOException("Restart CameraLite before selecting a different vendor SDK folder.");
            return;
        }
        if (!File.Exists(Path.Combine(full, "IOTCAPIs.dll")) || !File.Exists(Path.Combine(full, "AVAPIs.dll")))
            throw new IOException("This HDCameraPro UID uses TUTK P2P. Select a folder containing the camera vendor’s compatible Windows x64 IOTCAPIs.dll and AVAPIs.dll. The APK only contains Android ARM libraries. Alternatively, connect both devices to the same router and try ONVIF discovery.");
        try
        {
            iotc = NativeLibrary.Load(Path.Combine(full, "IOTCAPIs.dll")); av = NativeLibrary.Load(Path.Combine(full, "AVAPIs.dll"));
            var initialize = Export<Init>(iotc, "IOTC_Initialize2"); var avInitialize = Export<OneInt>(av, "avInitialize");
            GetSid = Export<NoArgs>(iotc, "IOTC_Get_SessionID"); CloseSid = Export<Close>(iotc, "IOTC_Session_Close");
            AbortConnect = Export<OneInt>(iotc, "IOTC_Connect_Stop_BySID"); ConnectUid = Export<Connect>(iotc, "IOTC_Connect_ByUID_Parallel");
            int initialized = initialize(0);
            if (initialized is not (0 or -3)) throw new IOException("TUTK initialization failed: " + initialized);
            Check(avInitialize(8), "AV initialization"); loadedFolder = full;
        }
        catch (BadImageFormatException) { throw new IOException("The vendor SDK must be Windows x64; Android and Windows x86 binaries cannot be loaded."); }
        catch (EntryPointNotFoundException ex) { throw new IOException("Vendor SDK does not expose the legacy AV API used by this APK: " + ex.Message); }
    }
    internal static void Check(int result, string operation) { if (result < 0) throw new IOException($"{operation} failed (TUTK {result}). Check camera UID, password, availability and SDK compatibility."); }
    internal static object Probe(string folder)
    {
        Load(folder);
        Export<Version>(iotc, "IOTC_Get_Version")(out uint iotcVersion);
        uint avVersion = unchecked((uint)Export<NoArgs>(av, "avGetAVApiVer")());
        string Format(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
        int avCleanup = Export<NoArgs>(av, "avDeInitialize")();
        int iotcCleanup = Export<NoArgs>(iotc, "IOTC_DeInitialize")();
        if (avCleanup != 0 || iotcCleanup != 0) throw new IOException("SDK cleanup failed.");
        return new { loaded = true, architecture = "x64", iotcVersion = Format(iotcVersion), avVersion = Format(avVersion), initialized = true, cleanup = true, hardwareConnectionVerified = false };
    }
    internal static async Task<object> ProbeSession(string folder, string uid)
    {
        Load(folder);
        int sid = GetSid(); Check(sid, "Allocating SDK test connection");
        using var deadline = new CancellationTokenSource();
        var timeout = Task.Run(async () => { try { await Task.Delay(12000, deadline.Token); AbortConnect(sid); } catch (OperationCanceledException) { } });
        int result = -1;
        try { result = await Task.Run(() => ConnectUid(uid, sid)); }
        finally { deadline.Cancel(); await timeout; CloseSid(result >= 0 ? result : sid); }
        return new { transportConnected = result >= 0, returnCode = result, credentialsUsed = false, videoStarted = false, microphoneStarted = false };
    }
}
public sealed class TutkCamera : IAsyncDisposable
{
    private CameraSession? transport;
    private int talkIndex = -1, audioCodec;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<int> firstVideo = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? videoReader, audioReader, encoderReader, microphoneSender;
    private CancellationTokenSource? microphoneStop;
    private long lastFrameTicks, frameCount, receivedBytes, lostFrames;
    private int videoCodec, outputVolume = 70, talkFailure;
    private float microphonePeak;
    private readonly FrameStream video = new(), compressedAudio = new();
    private readonly MediaPlayer audioPlayer;
    private readonly LibVLC vlc;
    private StreamMediaInput? videoInput, audioInput;
    private Media? videoMedia, audioMedia;
    private BufferedWaveProvider? audioBuffer;
    private WaveOutEvent? speaker;
    private WaveInEvent? microphone;
    private Process? encoder;
    private bool muted;
    private bool speakerRequested;
    private bool listeningSuspended;
    private Task? connectionTask;
    public event Action<string>? Status;
    public event Action<string>? Failed;
    public event Action<string>? TalkFailed;
    public float MicrophonePeak => Volatile.Read(ref microphonePeak);
    public DateTime LastFrameUtc => new(Interlocked.Read(ref lastFrameTicks), DateTimeKind.Utc);
    public long FrameCount => Interlocked.Read(ref frameCount);
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public long LostFrames => Interlocked.Read(ref lostFrames);
    internal int VideoBacklog => video.Backlog;
    internal int WorkerProcessId => transport?.WorkerProcessId ?? -1;
    public string VideoCodec => videoCodec switch { 78 => "H.264", 79 => "MJPEG", 80 => "H.265", _ => "Video" };
    public string AudioCodec => audioCodec switch { 134 or 135 => "AAC", 137 => "G.711 μ", 138 => "G.711 A", 140 => "PCM", 0 => "Waiting for audio", _ => "Audio " + audioCodec };
    public TutkCamera(LibVLC vlc) { this.vlc = vlc; audioPlayer = new(vlc); }
    private static string HostPath => Path.Combine(AppContext.BaseDirectory, "transport", "CameraLite.Transport.Host.exe");
    // A folder without the libraries falls back to the bundled ones.
    private static string SdkFolder(string folder)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "vendor-sdk");
        return !File.Exists(Path.Combine(folder, "IOTCAPIs.dll")) && File.Exists(Path.Combine(bundled, "IOTCAPIs.dll")) ? bundled : folder;
    }
    // LAN search also runs in a short-lived worker, so the native SDK never loads into the viewer.
    public static async Task<List<DiscoveredCamera>> DiscoverAsync(string sdkFolder, CancellationToken ct)
    {
        var devices = await CameraSession.DiscoverAsync(SdkFolder(sdkFolder), HostPath, ct);
        return devices.Select(d => new DiscoveredCamera(string.IsNullOrWhiteSpace(d.Name) ? "HDCameraPro camera" : d.Name, d.Address, "TUTK", d.Uid)).ToList();
    }
    public async Task<Media> ConnectAsync(CameraConfig camera, CancellationToken ct, int caching = 250)
    {
        using var connectionCancel = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); ct = connectionCancel.Token;
        Status?.Invoke("Connecting to the camera…");
        var connection = new CameraConnection { Uid = camera.Address, Username = camera.Username, Password = camera.Password(), SdkFolder = SdkFolder(camera.SdkFolder) };
        var open = CameraSession.OpenAsync(connection, HostPath, ct);
        connectionTask = open; transport = await open;
        videoReader = Task.Run(ReadVideo);
        audioReader = Task.Run(ReadAudio);
        int codec = await firstVideo.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        videoInput = new StreamMediaInput(video);
        videoMedia = new(vlc, videoInput);
        videoMedia.AddOption(codec == 80 ? ":demux=hevc" : codec == 79 ? ":demux=mjpeg" : ":demux=h264");
        videoMedia.AddOption(":live-caching=" + caching);
        return videoMedia;
    }
    public async Task<bool> SetQualityAsync(int profile, CancellationToken ct)
    {
        if (transport == null) throw new IOException("The camera is not connected.");
        return await transport.SetQualityAsync((byte)(profile switch { 0 => 1, 1 => 3, _ => 5 }), ct);
    }
    public void Mute(bool value) { muted = value; SetVolume(outputVolume); }
    public void SetVolume(int value)
    {
        outputVolume = Math.Clamp(value, 0, 100); audioPlayer.Volume = outputVolume; audioPlayer.Mute = muted;
        if (speaker != null) speaker.Volume = muted || listeningSuspended ? 0 : outputVolume / 100f;
        if (listeningSuspended) audioPlayer.Mute = true;
    }
    private async Task ReadVideo()
    {
        bool awaitingKeyframe = true; int skipped = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await transport!.ReadVideoAsync(stop.Token); using var frame = packet.Frame;
                int n = frame.Length;
                if (packet.Kind == Wire.LostFrameKind) { Interlocked.Increment(ref lostFrames); awaitingKeyframe = true; continue; }
                if (n > 0)
                {
                    int codec = frame.Codec;
                    if (codec is not (78 or 79 or 80)) throw new IOException("Unsupported TUTK video codec: " + codec);
                    videoCodec = codec; Interlocked.Exchange(ref lastFrameTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref frameCount); Interlocked.Add(ref receivedBytes, n);
                    if (awaitingKeyframe)
                    {
                        // Never stall indefinitely if a stream marks keyframes unusually.
                        if (!IsKeyframe(codec, frame.Data.Span) && ++skipped < 150) continue;
                        awaitingKeyframe = false; skipped = 0;
                    }
                    firstVideo.TrySetResult(codec); await video.PushCopyAsync(frame.Data.Span, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            firstVideo.TrySetException(ex);
            if (!stop.IsCancellationRequested) Failed?.Invoke(ex.Message);
        }
        finally { video.End(); }
    }
    // Annex B scan: H.264 IDR/SPS, H.265 IRAP/VPS/SPS; every MJPEG frame stands alone.
    internal static bool IsKeyframe(int codec, ReadOnlySpan<byte> frame)
    {
        if (codec == 79) return true;
        for (int i = 0; i + 3 < frame.Length; i++)
        {
            if (frame[i] != 0 || frame[i + 1] != 0) continue;
            int header = frame[i + 2] == 1 ? i + 3 : frame[i + 2] == 0 && frame[i + 3] == 1 ? i + 4 : -1;
            if (header < 0 || header >= frame.Length) continue;
            if (codec == 80)
            {
                int type = (frame[header] >> 1) & 0x3f;
                if (type is (>= 16 and <= 21) or 32 or 33) return true;
                if (type < 16) return false;
            }
            else
            {
                int type = frame[header] & 0x1f;
                if (type is 5 or 7) return true;
                if (type is >= 1 and <= 4) return false;
            }
            i = header;
        }
        return false;
    }
    // The worker gives up when the audio pipe stays full, which also ends video.
    // Frames are therefore always read; ones that cannot be played are dropped.
    private async Task ReadAudio()
    {
        bool playable = true;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var frame = await transport!.ReadAudioAsync(stop.Token);
                int n = frame.Length; if (n == 0) continue; audioCodec = frame.Codec;
                if (!playable) continue;
                try { playable = await PlayAudio(frame); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    playable = false; compressedAudio.End();
                    if (!stop.IsCancellationRequested) Status?.Invoke("Camera audio cannot play on this PC (" + ex.Message + "); video remains available.");
                }
            }
        }
        catch (OperationCanceledException) { } catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke(ex.Message); }
        finally { compressedAudio.End(); }
    }
    private async Task<bool> PlayAudio(MediaFrame frame)
    {
        int n = frame.Length;
        if (audioCodec is 137 or 138 or 140)
        {
            if (speaker == null)
            {
                audioBuffer = new(new WaveFormat(8000, 16, 1)) { BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true };
                var output = new WaveOutEvent { DesiredLatency = 100, Volume = muted || listeningSuspended ? 0 : outputVolume / 100f };
                try { output.Init(audioBuffer); output.Play(); } catch { output.Dispose(); throw; }
                speaker = output;
            }
            byte[] pcm = audioCodec == 140 ? frame.Data.ToArray() : new byte[n * 2];
            if (audioCodec != 140) for (int i = 0; i < n; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), audioCodec == 137 ? MuLawDecoder.MuLawToLinearSample(frame.Data.Span[i]) : ALawDecoder.ALawToLinearSample(frame.Data.Span[i]));
            audioBuffer!.AddSamples(pcm, 0, pcm.Length);
            return true;
        }
        if (audioCodec is 134 or 135)
        {
            byte[] data = frame.Data.ToArray();
            if (audioCodec == 134) data = AacWithAdts(data, 16000);
            await compressedAudio.PushAsync(data, stop.Token);
            if (audioInput == null)
            {
                audioInput = new(compressedAudio); audioMedia = new(vlc, audioInput);
                audioMedia.AddOption(":demux=aac"); audioMedia.AddOption(":live-caching=150"); audioPlayer.Volume = outputVolume; audioPlayer.Mute = muted || listeningSuspended; audioPlayer.Play(audioMedia);
            }
            return true;
        }
        Status?.Invoke("Unsupported camera audio codec " + audioCodec + "; video remains available.");
        return false;
    }
    internal static byte[] AacWithAdts(byte[] payload, int sampleRate)
    {
        int rateIndex = sampleRate == 8000 ? 11 : 8, size = payload.Length + 7;
        byte[] result = new byte[size]; result[0] = 0xff; result[1] = 0xf1;
        result[2] = (byte)((1 << 6) | (rateIndex << 2)); result[3] = (byte)(0x40 | (size >> 11));
        result[4] = (byte)(size >> 3); result[5] = (byte)(((size & 7) << 5) | 0x1f); result[6] = 0xfc;
        payload.CopyTo(result, 7); return result;
    }
    public async Task StartTalkAsync(int microphoneDevice, CancellationToken ct, bool captureMicrophone = true)
    {
        if (talkIndex >= 0) return;
        int codec = audioCodec;
        if (codec is not (134 or 135 or 137 or 138 or 140)) throw new IOException("Wait for the camera audio format before talking. AAC, G.711 and PCM are supported.");
        Interlocked.Exchange(ref talkFailure, 0);
        microphoneStop = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var token = microphoneStop.Token;
        if (transport == null) throw new IOException("The camera is not connected.");
        await transport.StartTalkAsync(token); talkIndex = 0; speakerRequested = true;
        listeningSuspended = true; audioPlayer.Mute = true; if (speaker != null) speaker.Volume = 0;
        token.ThrowIfCancellationRequested();
        if (!captureMicrophone) return;
        byte[] frameInfo = new byte[16]; frameInfo[0] = (byte)codec; frameInfo[2] = 2;
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        if (codec is 134 or 135)
        {
            string ffmpeg = File.Exists(@"C:\FFmpeg\bin\ffmpeg.exe") ? @"C:\FFmpeg\bin\ffmpeg.exe" : "ffmpeg.exe";
            var process = new Process { StartInfo = new(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            encoder = process;
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", "8000", "-ac", "1", "-i", "pipe:0", "-c:a", "aac", "-b:a", "16k", "-flush_packets", "1", "-f", "adts", "pipe:1" }) process.StartInfo.ArgumentList.Add(arg);
            try { process.Start(); process.BeginErrorReadLine(); }
            catch (System.ComponentModel.Win32Exception) { throw new IOException("AAC talk needs FFmpeg. Install ffmpeg.exe on PATH or in C:\\FFmpeg\\bin."); }
            encoderReader = Task.Run(async () =>
            {
                try
                {
                    var output = process.StandardOutput.BaseStream;
                    byte[] header = new byte[7];
                    while (!token.IsCancellationRequested)
                    {
                        await output.ReadExactlyAsync(header, token);
                        if (header[0] != 0xff || (header[1] & 0xf0) != 0xf0) throw new IOException("Invalid microphone AAC frame.");
                        int size = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);
                        if (size < 7 || size > 8191) throw new IOException("Invalid microphone AAC size.");
                        byte[] payload = new byte[size - 7]; await output.ReadExactlyAsync(payload, token);
                        byte[] data = codec == 134 ? payload : header.Concat(payload).ToArray();
                        token.ThrowIfCancellationRequested();
                        await transport.WriteTalkAudioAsync(data, frameInfo, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!token.IsCancellationRequested) FailTalk(ex.Message); }
            });
        }
        microphoneSender = Task.Run(async () =>
        {
            try
            {
                await foreach (var pcm in queue.Reader.ReadAllAsync(token))
                {
                    if (codec is 134 or 135) await encoder!.StandardInput.BaseStream.WriteAsync(pcm, token);
                    else
                    {
                        byte[] data = codec == 140 ? pcm : EncodeG711(pcm, codec == 138);
                        await transport.WriteTalkAudioAsync(data, frameInfo, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) FailTalk(ex.Message); }
        });
        microphone = new() { DeviceNumber = microphoneDevice, WaveFormat = new WaveFormat(8000, 16, 1), BufferMilliseconds = 20, NumberOfBuffers = 3 };
        microphone.DataAvailable += (_, e) =>
        {
            if (token.IsCancellationRequested) return;
            Volatile.Write(ref microphonePeak, AudioMeter.Peak(e.Buffer.AsSpan(0, e.BytesRecorded)));
            queue.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
        };
        microphone.RecordingStopped += (_, e) => { if (e.Exception != null && !token.IsCancellationRequested) FailTalk(e.Exception.Message); };
        token.ThrowIfCancellationRequested(); microphone.StartRecording();
    }
    internal static byte[] EncodeG711(byte[] pcm, bool aLaw)
    {
        byte[] data = new byte[pcm.Length / 2];
        for (int i = 0; i < data.Length; i++)
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
            data[i] = aLaw ? ALawEncoder.LinearToALawSample(value) : MuLawEncoder.LinearToMuLawSample(value);
        }
        return data;
    }
    internal async Task SendSilentTalkFrameForCheck(CancellationToken ct)
    {
        if (transport == null || talkIndex < 0) throw new IOException("The talk check channel is closed.");
        byte[] data = audioCodec switch { 137 => Enumerable.Repeat((byte)0xff, 160).ToArray(), 138 => Enumerable.Repeat((byte)0xd5, 160).ToArray(), 140 => new byte[320], _ => throw new IOException("Silent hardware check requires G.711 or PCM.") };
        byte[] info = new byte[16]; info[0] = (byte)audioCodec; info[2] = 2;
        await transport.WriteTalkAudioAsync(data, info, ct);
    }
    private void FailTalk(string error)
    {
        if (Interlocked.Exchange(ref talkFailure, 1) != 0) return;
        microphoneStop?.Cancel(); TalkFailed?.Invoke("Talk stopped: " + error);
    }
    public async Task StopTalkAsync()
    {
        microphoneStop?.Cancel();
        microphone?.StopRecording(); microphone?.Dispose(); microphone = null;
        if (encoder != null) { try { if (!encoder.HasExited) encoder.Kill(true); } catch (InvalidOperationException) { } }
        if (microphoneSender != null) { await microphoneSender; microphoneSender = null; }
        if (encoderReader != null) { await encoderReader; encoderReader = null; }
        encoder?.Dispose(); encoder = null; microphoneStop?.Dispose(); microphoneStop = null;
        Volatile.Write(ref microphonePeak, 0);
        int talk = talkIndex; talkIndex = -1;
        if (transport != null && (talk >= 0 || speakerRequested))
        {
            try { using var cleanup = new CancellationTokenSource(3500); await transport.StopTalkAsync(cleanup.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        speakerRequested = false;
        if (listeningSuspended)
        {
            listeningSuspended = false;
            audioBuffer?.ClearBuffer(); SetVolume(outputVolume);
        }
    }
    public async ValueTask DisposeAsync()
    {
        PrepareStop(); await StopTalkAsync();
        if (connectionTask != null) { try { await connectionTask; } catch { } }
        if (transport != null) { await transport.DisposeAsync(); transport = null; }
        if (videoReader != null) await videoReader;
        if (audioReader != null) await audioReader;
        audioPlayer.Stop(); audioPlayer.Dispose(); speaker?.Stop(); speaker?.Dispose();
        audioMedia?.Dispose(); audioInput?.Dispose(); videoMedia?.Dispose(); videoInput?.Dispose();
        video.Dispose(); compressedAudio.Dispose(); stop.Dispose();
    }
    public void PrepareStop() { stop.Cancel(); video.End(); compressedAudio.End(); }
}
