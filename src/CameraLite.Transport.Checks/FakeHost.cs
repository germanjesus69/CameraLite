using System.IO.Pipes;
using System.Buffers.Binary;
using CameraLite.Transport;

namespace CameraLite.Transport.Checks;

// This companion is compiled only into the checks executable. Production Host
// has no operation that intentionally crashes or hangs its process.
internal static class FakeHost
{
    internal const string CredentialCanary = "checks-only-account-canary-7E24";
    internal static readonly DiscoveredDevice Discovered = new("ABCDEFGHIJ0123456789", "192.0.2.7", "Fixture camera");
    internal static async Task<int> RunAsync(string[] args)
    {
        using var control = new NamedPipeClientStream(".", args[0] + "-control", PipeDirection.InOut, PipeOptions.Asynchronous);
        using var video = new NamedPipeClientStream(".", args[0] + "-video", PipeDirection.Out, PipeOptions.Asynchronous);
        using var audio = new NamedPipeClientStream(".", args[0] + "-audio", PipeDirection.Out, PipeOptions.Asynchronous);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var responseGate = new SemaphoreSlim(1, 1);
        string scenario = "normal"; HostRequest? talkStart = null; int starts = 0, stops = 0;
        async Task Reply(HostRequest request, byte[]? data = null, bool success = true, int code = 0)
        {
            await responseGate.WaitAsync(stop.Token);
            try { await Wire.WriteJsonAsync(control, new HostResponse { Id = request.Id, Success = success, Data = data ?? [], Error = success ? null : "Fake operation failed.", Code = code }, stop.Token); }
            finally { responseGate.Release(); }
        }
        async Task Delayed(HostRequest request) { await Task.Delay(300, stop.Token); await Reply(request, [9, 2]); }
        try
        {
            await Task.WhenAll(control.ConnectAsync(stop.Token), video.ConnectAsync(stop.Token), audio.ConnectAsync(stop.Token));
            while (true)
            {
                var request = await Wire.ReadJsonAsync<HostRequest>(control, stop.Token);
                switch (request.Operation)
                {
                    case "connect":
                        if (Environment.CommandLine.Contains(CredentialCanary, StringComparison.Ordinal)) return 3;
                        scenario = request.Connection!.Uid; request.Connection = null;
                        await Reply(request);
                        // Fill the parent's output pipe while operation tokens
                        // expire, including a write whose length has begun.
                        if (scenario == "stall-read") await Task.Delay(500, stop.Token);
                        break;
                    case "control":
                        if (request.Type == 90) Environment.Exit(37);
                        else if (request.Type == 91) { }
                        else if (request.Type == 92) _ = Delayed(request);
                        else if (request.Type == 94)
                        {
                            byte[] invalidLength = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(invalidLength, 131073);
                            await responseGate.WaitAsync(stop.Token);
                            try { await control.WriteAsync(invalidLength, stop.Token); }
                            finally { responseGate.Release(); }
                        }
                        else if (request.Type == 95) await Reply(request, [(byte)starts, (byte)stops]);
                        else if (request.Type == 97) await Reply(request, success: false, code: -20009);
                        else await Reply(request, [9, 3]);
                        break;
                    case "discover": await Reply(request, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { Discovered })); break;
                    case "talk-start": starts++; talkStart = request; break;
                    case "talk-stop":
                        stops++;
                        if (talkStart != null) { await Reply(talkStart, success: false); talkStart = null; }
                        await Reply(request); break;
                    case "close":
                        if (scenario == "hang-close") break;
                        await Reply(request); return 0;
                    default: await Reply(request); break;
                }
            }
        }
        catch { return 0; }
    }
}
