using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Codecs;
using NAudio.Wave;

namespace CameraLite;

public sealed record AudioTrack(Uri Control, int PayloadType, bool ALaw);
public sealed class Backchannel : IAsyncDisposable
{
    private readonly Uri uri;
    private readonly string username, password;
    private readonly TcpClient tcp = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private NetworkStream? stream;
    private string? challenge, session;
    private int cseq, nonceCount;
    private byte channel;
    private ushort sequence = (ushort)Random.Shared.Next(65536);
    private uint timestamp = (uint)Random.Shared.NextInt64(uint.MaxValue), ssrc = (uint)Random.Shared.NextInt64(uint.MaxValue);
    private WaveInEvent? microphone;
    private Task? sender, keepAlive;
    private AudioTrack? track;
    public event Action<string>? Failed;
    private float microphonePeak;
    public float MicrophonePeak => Volatile.Read(ref microphonePeak);
    public Backchannel(string url, string username, string password) { uri = CameraUri.ValidateStream(url); this.username = username; this.password = password; }
    public async Task OpenAsync(CancellationToken ct)
    {
        if (uri.Scheme != "rtsp") throw new IOException("Talk requires a camera with an RTSP ONVIF audio backchannel.");
        await tcp.ConnectAsync(uri.Host, uri.IsDefaultPort ? 554 : uri.Port, ct);
        tcp.NoDelay = true;
        stream = tcp.GetStream();
        var describe = await Request("DESCRIBE", uri, "Accept: application/sdp\r\n", ct);
        var baseUri = describe.Headers.TryGetValue("Content-Base", out var contentBase) ? new Uri(contentBase) : uri;
        track = ParseTrack(describe.Body, baseUri);
        var setup = await Request("SETUP", track.Control, "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n", ct);
        if (!setup.Headers.TryGetValue("Session", out var sessionHeader)) throw new IOException("Camera did not return a backchannel session.");
        session = sessionHeader.Split(';')[0];
        if (!setup.Headers.TryGetValue("Transport", out var transport) || !transport.Contains("TCP", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Camera does not support a TCP audio backchannel.");
        var channels = Regex.Match(transport, @"interleaved=(\d+)-(\d+)", RegexOptions.IgnoreCase);
        if (!channels.Success || !byte.TryParse(channels.Groups[1].Value, out channel)) throw new IOException("Invalid audio transport returned by camera.");
        await Request("PLAY", uri, "Range: npt=0.000-\r\n", ct);
    }
    internal static AudioTrack ParseTrack(string sdp, Uri baseUri)
    {
        var sections = Regex.Split(sdp.Replace("\r", ""), "(?m)^m=");
        foreach (string section in sections.Skip(1))
        {
            var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!lines[0].StartsWith("audio ") || !lines.Contains("a=sendonly")) continue;
            string? control = lines.FirstOrDefault(l => l.StartsWith("a=control:"))?[10..];
            if (string.IsNullOrWhiteSpace(control) || control == "*") continue;
            var payloads = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(3).ToHashSet();
            foreach (string line in lines.Where(l => l.StartsWith("a=rtpmap:")))
            {
                var match = Regex.Match(line, @"^a=rtpmap:(\d+) (PCMU|PCMA)/8000(?:/1)?$", RegexOptions.IgnoreCase);
                if (match.Success && payloads.Contains(match.Groups[1].Value)) return new(ResolveControl(baseUri, control), int.Parse(match.Groups[1].Value), match.Groups[2].Value.Equals("PCMA", StringComparison.OrdinalIgnoreCase));
            }
            if (payloads.Contains("0")) return new(ResolveControl(baseUri, control), 0, false);
            if (payloads.Contains("8")) return new(ResolveControl(baseUri, control), 8, true);
        }
        throw new IOException("Camera did not offer a G.711 PCMU/PCMA 8 kHz talk channel. Its proprietary talk protocol may need the vendor SDK.");
    }
    private static Uri ResolveControl(Uri baseUri, string control)
    {
        if (Uri.TryCreate(control, UriKind.Absolute, out var absolute)) return absolute;
        if (control.StartsWith('/')) return new Uri(baseUri, control);
        return new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + control);
    }
    public void StartMicrophone(int deviceNumber)
    {
        if (track == null) throw new InvalidOperationException("Talk session is not open.");
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        microphone = new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = new WaveFormat(8000, 16, 1), BufferMilliseconds = 20, NumberOfBuffers = 3 };
        microphone.DataAvailable += (_, e) => { if (stop.IsCancellationRequested) return; Volatile.Write(ref microphonePeak, AudioMeter.Peak(e.Buffer.AsSpan(0, e.BytesRecorded))); queue.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray()); };
        microphone.RecordingStopped += (_, e) => { if (e.Exception != null && !stop.IsCancellationRequested) { stop.Cancel(); Failed?.Invoke("Microphone stopped: " + e.Exception.Message); } };
        sender = Task.Run(async () =>
        {
            try { await foreach (var pcm in queue.Reader.ReadAllAsync(stop.Token)) await SendPcm(pcm, stop.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Failed?.Invoke("Talk stopped: " + ex.Message); stop.Cancel(); }
        });
        keepAlive = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { await Task.Delay(15000, stop.Token); await Request("GET_PARAMETER", uri, "", stop.Token); } }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Failed?.Invoke("Talk connection lost: " + ex.Message); stop.Cancel(); }
        });
        microphone.StartRecording();
    }
    internal async Task SendPcm(byte[] pcm, CancellationToken ct)
    {
        if (track == null || stream == null) throw new InvalidOperationException();
        byte[] packet = EncodePacket(pcm, track.PayloadType, track.ALaw, sequence++, timestamp, ssrc);
        timestamp += (uint)(pcm.Length / 2);
        byte[] frame = new byte[packet.Length + 4];
        frame[0] = (byte)'$'; frame[1] = channel;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), checked((ushort)packet.Length));
        packet.CopyTo(frame, 4);
        await gate.WaitAsync(ct);
        try { await stream.WriteAsync(frame, ct); } finally { gate.Release(); }
    }
    internal static byte[] EncodePacket(byte[] pcm, int payload, bool aLaw, ushort seq, uint time, uint source)
    {
        byte[] packet = new byte[12 + pcm.Length / 2];
        packet[0] = 0x80; packet[1] = (byte)payload;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), time);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), source);
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
            packet[12 + i] = aLaw ? ALawEncoder.LinearToALawSample(sample) : MuLawEncoder.LinearToMuLawSample(sample);
        }
        return packet;
    }
    private sealed record Reply(int Status, Dictionary<string, string> Headers, string Body);
    private async Task<Reply> Request(string method, Uri target, string headers, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(8000);
            for (int retry = 0; retry < 2; retry++)
            {
                string authorization = challenge != null ? Authorization(challenge, method, target.AbsoluteUri, username, password, ++nonceCount) : "";
                string request = $"{method} {target.AbsoluteUri} RTSP/1.0\r\nCSeq: {++cseq}\r\nUser-Agent: CameraLite/0.1\r\nRequire: www.onvif.org/ver20/backchannel\r\n{(session != null ? "Session: " + session + "\r\n" : "")}{(authorization.Length > 0 ? "Authorization: " + authorization + "\r\n" : "")}{headers}\r\n";
                await stream!.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
                var reply = await ReadReply(deadline.Token);
                if (reply.Status == 401 && retry == 0 && reply.Headers.TryGetValue("WWW-Authenticate", out var auth)) { challenge = auth; continue; }
                if (reply.Status == 551) throw new IOException("Camera does not support ONVIF two-way talk.");
                if (reply.Status != 200) throw new IOException($"Camera rejected {method} (RTSP {reply.Status}).");
                return reply;
            }
            throw new IOException("Camera rejected talk authentication.");
        }
        finally { gate.Release(); }
    }
    internal static string Authorization(string challenge, string method, string uri, string user, string password, int count)
    {
        if (challenge.StartsWith("Basic", StringComparison.OrdinalIgnoreCase)) return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        if (!challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsupported RTSP authentication scheme.");
        var fields = Regex.Matches(challenge[6..], "(\\w+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]+))").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("realm", out var realm) || !fields.TryGetValue("nonce", out var nonce)) throw new IOException("Invalid camera authentication challenge.");
        string algorithm = fields.GetValueOrDefault("algorithm", "MD5");
        if (algorithm is not ("MD5" or "MD5-sess" or "SHA-256")) throw new IOException("Unsupported RTSP digest algorithm: " + algorithm);
        string Hash(string text) => Convert.ToHexStringLower(algorithm == "SHA-256" ? SHA256.HashData(Encoding.UTF8.GetBytes(text)) : MD5.HashData(Encoding.UTF8.GetBytes(text)));
        string cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), nc = count.ToString("x8");
        string ha1 = Hash(user + ":" + realm + ":" + password);
        if (algorithm == "MD5-sess") ha1 = Hash(ha1 + ":" + nonce + ":" + cnonce);
        string ha2 = Hash(method + ":" + uri);
        bool qop = fields.TryGetValue("qop", out var qops);
        if (qop && !qops!.Split(',').Any(q => q.Trim() == "auth")) throw new IOException("Camera requires an unsupported authentication mode.");
        string response = Hash(qop ? $"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}" : $"{ha1}:{nonce}:{ha2}");
        string Safe(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "");
        return $"Digest username=\"{Safe(user)}\", realm=\"{Safe(realm)}\", nonce=\"{Safe(nonce)}\", uri=\"{Safe(uri)}\", response=\"{response}\", algorithm={algorithm}" + (qop ? $", qop=auth, nc={nc}, cnonce=\"{cnonce}\"" : algorithm == "MD5-sess" ? $", cnonce=\"{cnonce}\"" : "") + (fields.TryGetValue("opaque", out var opaque) ? $", opaque=\"{Safe(opaque)}\"" : "");
    }
    private async Task<Reply> ReadReply(CancellationToken ct)
    {
        var header = new List<byte>();
        byte[] one = new byte[1];
        while (true)
        {
            await stream!.ReadExactlyAsync(one, ct);
            if (header.Count == 0 && one[0] == '$')
            {
                byte[] frame = new byte[3]; await stream.ReadExactlyAsync(frame, ct);
                byte[] skipped = new byte[BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1))];
                await stream.ReadExactlyAsync(skipped, ct); continue;
            }
            header.Add(one[0]);
            if (header.Count > 32768) throw new IOException("Camera sent oversized RTSP headers.");
            int end = header.Count;
            if (end >= 4 && header[end - 4] == 13 && header[end - 3] == 10 && header[end - 2] == 13 && header[end - 1] == 10) break;
        }
        string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(lines[0].Split(' ').ElementAtOrDefault(1), out int status)) throw new IOException("Invalid RTSP response.");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); if (colon > 0) fields[line[..colon]] = line[(colon + 1)..].Trim(); }
        int length = fields.TryGetValue("Content-Length", out var text) ? int.Parse(text) : 0;
        if (length < 0 || length > 1_000_000) throw new IOException("Camera sent an oversized RTSP body.");
        byte[] body = new byte[length]; await stream!.ReadExactlyAsync(body, ct);
        return new(status, fields, Encoding.UTF8.GetString(body));
    }
    public async ValueTask DisposeAsync()
    {
        microphone?.StopRecording(); microphone?.Dispose(); microphone = null;
        stop.Cancel();
        if (sender != null) await sender;
        if (keepAlive != null) await keepAlive;
        if (session != null) { try { using var timeout = new CancellationTokenSource(1500); await Request("TEARDOWN", uri, "", timeout.Token); } catch { } }
        tcp.Dispose(); stop.Dispose(); gate.Dispose();
    }
}
