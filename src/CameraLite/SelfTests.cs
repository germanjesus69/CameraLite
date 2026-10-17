using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace CameraLite;
internal static class SelfTests
{
    private static readonly List<object> results = [];
    public static async Task<int> Run()
    {
        Directory.CreateDirectory("work");
        await Test("Compressed frame queue preserves order under backpressure", async () =>
        {
            using var stream = new FrameStream(2); using var deadline = new CancellationTokenSource(3000);
            await stream.PushAsync([1, 2, 3], deadline.Token); await stream.PushAsync([4, 5], deadline.Token);
            var blocked = stream.PushAsync([6, 7], deadline.Token); Check(!blocked.IsCompleted);
            byte[] read = new byte[7]; int offset = 0;
            while (offset < read.Length) { offset += stream.Read(read, offset, Math.Min(2, read.Length - offset)); await Task.Yield(); }
            await blocked; Check(read.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }));
        });
        await Test("Stream shutdown releases a blocked decoder and producer", async () =>
        {
            using var empty = new FrameStream(1);
            var reader = Task.Run(() => empty.Read(new byte[4], 0, 4)); await Task.Delay(30); empty.End(); Check(await reader == 0);
            using var full = new FrameStream(1); await full.PushAsync([1], CancellationToken.None);
            var producer = full.PushAsync([2], CancellationToken.None); full.End();
            bool cancelled = false; try { await producer; } catch (OperationCanceledException) { cancelled = true; } Check(cancelled);
        });
        await Test("Pooled frame copies keep order and let the producer reuse its buffer", async () =>
        {
            using var stream = new FrameStream(4); using var deadline = new CancellationTokenSource(3000);
            byte[] source = [1, 2, 3]; await stream.PushCopyAsync(source, deadline.Token);
            source[0] = 9; source[1] = 8; await stream.PushCopyAsync(source.AsSpan(0, 2), deadline.Token);
            byte[] read = new byte[5]; int offset = 0;
            while (offset < read.Length) offset += stream.Read(read, offset, read.Length - offset);
            Check(read.SequenceEqual(new byte[] { 1, 2, 3, 9, 8 }) && stream.Backlog == 0);
            await stream.PushCopyAsync(source, deadline.Token); stream.End(); Check(stream.Backlog == 0 && stream.Read(read, 0, 5) == 0);
        });
        await Test("Keyframe detection for H.264, H.265 and MJPEG after frame loss", () =>
        {
            byte[] sps = Convert.FromHexString("0000000167640028AC3B503C0113F2C20000030002000003006508");
            Check(TutkCamera.IsKeyframe(78, sps) && TutkCamera.IsKeyframe(78, [0, 0, 1, 0x65, 0x88]) && TutkCamera.IsKeyframe(78, [0, 0, 0, 1, 0x09, 0xf0, 0, 0, 1, 0x65]));
            Check(!TutkCamera.IsKeyframe(78, [0, 0, 0, 1, 0x41, 0x9a]) && !TutkCamera.IsKeyframe(78, [0, 0, 0, 1, 0x09, 0xf0, 0, 0, 1, 0x41]) && !TutkCamera.IsKeyframe(78, [0, 0, 1]));
            Check(TutkCamera.IsKeyframe(80, [0, 0, 0, 1, 0x40, 0x01]) && TutkCamera.IsKeyframe(80, [0, 0, 1, 0x26, 0x01]) && !TutkCamera.IsKeyframe(80, [0, 0, 1, 0x02, 0x01]));
            Check(TutkCamera.IsKeyframe(79, [0xff, 0xd8]));
            return Task.CompletedTask;
        });
        await Test("Microphone metering and G.711 handle negative full scale", () =>
        {
            Check(AudioMeter.Peak([0, 0, 0, 128]) == 1f && AudioMeter.Peak(new byte[320]) == 0);
            Check(TutkCamera.EncodeG711(new byte[320], false).All(b => b == 0xff));
            Check(!RetryPolicy.CanRetry("Camera authentication failed (TUTK -20009).") && RetryPolicy.CanRetry("Connection timed out."));
            return Task.CompletedTask;
        });
        await Test("Credential protection round trip", () => { var c = new CameraConfig(); c.SetPassword("fixture-pass"); Check(c.Password() == "fixture-pass" && !c.PasswordEncrypted.Contains("fixture-pass")); return Task.CompletedTask; });
        await Test("Unreadable saved password asks for re-entry and is not retried", () =>
        {
            foreach (string stored in new[] { Convert.ToBase64String([1, 2, 3, 4]), "not base64" })
            {
                bool asked = false;
                try { _ = new CameraConfig { PasswordEncrypted = stored }.Password(); } catch (SavedPasswordException ex) { asked = !RetryPolicy.CanRetry(ex.Message); }
                Check(asked);
            }
            return Task.CompletedTask;
        });
        await Test("Reconnect budget resets only after a stream stayed live", () =>
        {
            var now = DateTime.UtcNow;
            Check(!RetryPolicy.IsNewOutage(null, now) && !RetryPolicy.IsNewOutage(now.AddSeconds(-20), now) && RetryPolicy.IsNewOutage(now - RetryPolicy.StableAfter, now));
            return Task.CompletedTask;
        });
        await Test("Stream URL validation and escaped authentication", () =>
        {
            Check(CameraUri.OnvifAddress("192.168.1.5:8000").AbsoluteUri == "http://192.168.1.5:8000/onvif/device_service");
            Check(CameraUri.WithCredentials("rtsp://127.0.0.1/live", "test", "p@ss").Contains("p%40ss"));
            bool rejected = false; try { CameraUri.ValidateStream("rtsp://user:pass@host/live"); } catch (ArgumentException) { rejected = true; } Check(rejected);
            return Task.CompletedTask;
        });
        await Test("ONVIF backchannel chooses sendonly audio and rejects unsupported codecs", () =>
        {
            var track = Backchannel.ParseTrack("m=audio 0 RTP/AVP 0\r\na=control:listen\r\na=recvonly\r\nm=audio 0 RTP/AVP 97\r\na=control:talk\r\na=rtpmap:97 PCMA/8000\r\na=sendonly\r\n", new Uri("rtsp://127.0.0.1/live/"));
            Check(track.Control.AbsoluteUri == "rtsp://127.0.0.1/live/talk" && track.ALaw && track.PayloadType == 97);
            bool rejected = false; try { Backchannel.ParseTrack("m=audio 0 RTP/AVP 96\na=control:talk\na=sendonly\na=rtpmap:96 OPUS/48000/2", new Uri("rtsp://host/live")); } catch (IOException) { rejected = true; } Check(rejected);
            return Task.CompletedTask;
        });
        await Test("RTSP digest RFC vector", () =>
        {
            string auth = Backchannel.Authorization("Digest realm=\"testrealm@host.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\"", "GET", "/dir/index.html", "Mufasa", "Circle Of Life", 1);
            Check(auth.Contains("670fd8c2df070c60b045671b8b24ff02")); return Task.CompletedTask;
        });
        await Test("G.711 RTP header, timestamps and silence encoding", () =>
        {
            var packet = Backchannel.EncodePacket(new byte[320], 8, true, 0x1234, 0x12345678, 0x87654321);
            Check(packet.Length == 172 && packet[0] == 0x80 && packet[1] == 8 && BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) == 0x1234 && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) == 0x12345678 && packet.Skip(12).All(b => b == 0xd5));
            var mu = Backchannel.EncodePacket(new byte[320], 0, false, 0, 0, 0); Check(mu.Skip(12).All(b => b == 0xff)); return Task.CompletedTask;
        });
        await Test("AAC ADTS framing", () => { byte[] encoded = TutkCamera.AacWithAdts(new byte[100], 8000); int size = ((encoded[3] & 3) << 11) | (encoded[4] << 3) | (encoded[5] >> 5); Check(size == 107 && ((encoded[2] >> 2) & 15) == 11); return Task.CompletedTask; });
        await Test("ONVIF SOAP profiles and authenticated stream URI discovery", OnvifFixture);
        await Test("ONVIF SOAP faults sent with HTTP 400 report the camera's reason", OnvifFaultFixture);
        await Test("RTSP authenticated DESCRIBE / SETUP / PLAY / RTP / TEARDOWN", RtspFixture);
        await Test("TUTK missing SDK returns actionable failure", () => { bool rejected = false; try { TutkNative.Load(Path.Combine(Path.GetTempPath(), "cameralite-no-sdk")); } catch (IOException ex) { rejected = ex.Message.Contains("IOTCAPIs.dll"); } Check(rejected); return Task.CompletedTask; });
        File.WriteAllText("work/test-results.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return results.Any(r => JsonSerializer.Serialize(r).Contains("\"passed\":false")) ? 1 : 0;
    }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static async Task Test(string name, Func<Task> run)
    {
        try { await run().WaitAsync(TimeSpan.FromSeconds(20)); results.Add(new { name, passed = true }); }
        catch (Exception ex) { results.Add(new { name, passed = false, error = ex.ToString() }); }
    }
    private static async Task<(string Header, byte[] Body)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(); byte[] single = new byte[1];
        do { await stream.ReadExactlyAsync(single, ct); bytes.Add(single[0]); if (bytes.Count > 64000) throw new IOException(); }
        while (bytes.Count < 4 || !bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }));
        string header = Encoding.ASCII.GetString(bytes.ToArray()); int length = 0;
        foreach (string line in header.Split("\r\n")) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, ct); return (header, body);
    }
    private static async Task OnvifFixture()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; int validated = 0;
        using var deadline = new CancellationTokenSource(15000);
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token); using var stream = socket.GetStream();
                var request = await ReadRequest(stream, deadline.Token); var xml = Discovery.XmlDocumentSafe(Encoding.UTF8.GetString(request.Body));
                string Value(string name) => xml.Descendants().First(e => e.Name.LocalName == name).Value;
                var nonce = Convert.FromBase64String(Value("Nonce"));
                Check(Value("Username") == "fixture" && Value("Password") == Convert.ToBase64String(SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(Value("Created") + "fixture-pass")).ToArray()))); validated++;
                string body = i switch
                {
                    0 => $"<GetCapabilitiesResponse><Capabilities><Media><XAddr>http://127.0.0.1:{port}/media</XAddr></Media></Capabilities></GetCapabilitiesResponse>",
                    1 => "<GetProfilesResponse><Profiles token=\"low\"><Name>Low</Name><VideoEncoderConfiguration><Resolution><Width>640</Width><Height>360</Height></Resolution></VideoEncoderConfiguration></Profiles><Profiles token=\"high\"><Name>High</Name><VideoEncoderConfiguration><Resolution><Width>1920</Width><Height>1080</Height></Resolution></VideoEncoderConfiguration></Profiles></GetProfilesResponse>",
                    _ => $"<GetStreamUriResponse><MediaUri><Uri>rtsp://0.0.0.0:554/{Value("ProfileToken")}</Uri></MediaUri></GetStreamUriResponse>"
                };
                byte[] response = Encoding.UTF8.GetBytes("<Envelope><Body>" + body + "</Body></Envelope>");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/soap+xml\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(response, deadline.Token);
            }
        });
        using var client = new OnvifClient($"http://127.0.0.1:{port}/device", "fixture", "fixture-pass");
        var profiles = await client.ProfilesAsync(deadline.Token); await server;
        Check(validated == 4 && profiles.Count == 2 && profiles[0].Width == 1920 && new Uri(profiles[0].Uri).Host == "127.0.0.1" && new Uri(profiles[0].Uri).AbsolutePath == "/high" && profiles[1].Width == 640);
    }
    private static async Task OnvifFaultFixture()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(15000);
        var server = Task.Run(async () =>
        {
            foreach (string subcode in new[] { "ter:NotAuthorized", "ter:ActionNotSupported" })
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token); using var stream = socket.GetStream();
                await ReadRequest(stream, deadline.Token);
                byte[] response = Encoding.UTF8.GetBytes($"<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault><s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>{subcode}</s:Value></s:Subcode></s:Code><s:Reason><s:Text xml:lang=\"en\">Fixture reason</s:Text></s:Reason></s:Fault></s:Body></s:Envelope>");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 400 Bad Request\r\nContent-Type: application/soap+xml\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(response, deadline.Token);
            }
        });
        async Task<string> Failure()
        {
            using var client = new OnvifClient($"http://127.0.0.1:{port}/device", "fixture", "fixture-pass");
            try { await client.ProfilesAsync(deadline.Token); } catch (IOException ex) { return ex.Message; }
            return "";
        }
        string unauthorized = await Failure(), unsupported = await Failure(); await server;
        Check(unauthorized.Contains("username/password") && unsupported == "ONVIF: Fixture reason");
    }
    private static async Task RtspFixture()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; bool authenticated = false, audio = false, teardown = false;
        using var deadline = new CancellationTokenSource(15000);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token); using var stream = socket.GetStream();
            for (int i = 0; i < 4; i++)
            {
                var request = await ReadRequest(stream, deadline.Token); Check(request.Header.Contains("Require: www.onvif.org/ver20/backchannel"));
                int cseq = int.Parse(request.Header.Split("\r\n").First(l => l.StartsWith("CSeq:"))[5..]);
                string fields = "", body = "", status = "200 OK";
                if (i == 0) { status = "401 Unauthorized"; fields = "WWW-Authenticate: Basic realm=\"fixture\"\r\n"; }
                else
                {
                    authenticated = request.Header.Contains("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:fixture-pass"))); Check(authenticated);
                    if (i == 1) { Check(request.Header.StartsWith("DESCRIBE ")); body = $"v=0\r\nm=audio 0 RTP/AVP 0\r\na=control:rtsp://127.0.0.1:{port}/talk\r\na=rtpmap:0 PCMU/8000\r\na=sendonly\r\n"; }
                    if (i == 2) { Check(request.Header.StartsWith("SETUP ")); fields = "Session: test;timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=2-3\r\n"; }
                    if (i == 3) { Check(request.Header.StartsWith("PLAY ") && request.Header.Contains("Session: test")); fields = "Session: test\r\n"; }
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"RTSP/1.0 {status}\r\nCSeq: {cseq}\r\n{fields}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}"), deadline.Token);
            }
            byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, deadline.Token); Check(prefix[0] == '$' && prefix[1] == 2);
            byte[] packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(2))]; await stream.ReadExactlyAsync(packet, deadline.Token);
            Check(packet.Length == 172 && packet[0] == 0x80 && packet[1] == 0 && packet.Skip(12).All(b => b == 0xff)); audio = true;
            var last = await ReadRequest(stream, deadline.Token); teardown = last.Header.StartsWith("TEARDOWN ");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nContent-Length: 0\r\n\r\n"), deadline.Token);
        });
        var client = new Backchannel($"rtsp://127.0.0.1:{port}/live", "fixture", "fixture-pass");
        await client.OpenAsync(deadline.Token); await client.SendPcm(new byte[320], deadline.Token); await client.DisposeAsync(); await server;
        Check(authenticated && audio && teardown);
    }
}
