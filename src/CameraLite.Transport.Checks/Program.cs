using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CameraLite.Transport;

namespace CameraLite.Transport.Checks;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static int failed;
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2) return await FakeHost.RunAsync(args);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return 2;
        await Check("media roundtrip and pooled-frame disposal", MediaRoundtrip);
        await Check("media malformed headers and sizes", MediaMalformed);
        await Check("media truncated header and payload", MediaTruncated);
        await Check("media canceled payload read", MediaCancellation);
        await Check("JSON roundtrip and credential-safe description", JsonRoundtrip);
        await Check("JSON malformed, oversized and truncated frames", JsonMalformed);
        await Check("worker abrupt exit preserves parent", AbruptExit);
        await Check("malformed worker reply invalidates promptly", MalformedReply);
        await Check("pending cancellation ignores late response", PendingCancellation);
        await Check("canceled blocked writes preserve JSON framing", BlockedWriteCancellation);
        await Check("control size limit and native error preservation", ControlBounds);
        await Check("media cancellation leaves control usable", PipeReadCancellation);
        await Check("late-canceled talk sends stop", TalkCancellation);
        await Check("hung worker disposal and pending request are bounded", BoundedDisposal);
        await Check("worker talk binding passes a rooted accept callback", TalkBinding);
        await Check("LAN search entries are parsed and validated", LanSearchParsing);
        await Check("discovery runs in a disposable worker", WorkerDiscovery);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = Results.Count - failed, failed, checks = Results }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static async Task Check(string name, Func<Task> test)
    {
        var clock = Stopwatch.StartNew();
        try { await test().WaitAsync(TimeSpan.FromSeconds(12)); Results.Add(new { name, passed = true, milliseconds = clock.ElapsedMilliseconds }); }
        catch (Exception ex) { failed++; Results.Add(new { name, passed = false, errorType = ex.GetType().Name, milliseconds = clock.ElapsedMilliseconds }); }
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Check failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected failure was not observed.");
    }
    private static async Task TransportFails(Task action)
    {
        try { await action; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
        throw new InvalidOperationException("Expected transport failure was not observed.");
    }

    private static async Task<byte[]> Packet(byte[]? data = null, int kind = Wire.VideoKind)
    {
        using var memory = new MemoryStream(); byte[] info = new byte[16]; info[0] = 78;
        await Wire.WriteMediaAsync(memory, kind, data ?? [1, 2, 3, 4], info, 0xfedcba98, CancellationToken.None);
        return memory.ToArray();
    }
    private static async Task MediaRoundtrip()
    {
        using var stream = new MemoryStream(await Packet());
        var packet = await Wire.ReadMediaAsync(stream, Wire.MaxFrameBytes, CancellationToken.None);
        Require(packet.Kind == Wire.VideoKind && packet.Frame.Codec == 78 && packet.Frame.Number == 0xfedcba98);
        Require(packet.Frame.Data.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        packet.Frame.Dispose(); packet.Frame.Dispose();
        await Throws<ObjectDisposedException>(() => { _ = packet.Frame.Data; return Task.CompletedTask; });
        using var empty = new MemoryStream(await Packet([], Wire.LostFrameKind));
        var loss = await Wire.ReadMediaAsync(empty, Wire.MaxFrameBytes, CancellationToken.None);
        using (loss.Frame) Require(loss.Kind == Wire.LostFrameKind && loss.Frame.Length == 0);
    }
    private static async Task MediaMalformed()
    {
        foreach (int variant in Enumerable.Range(0, 4))
        {
            byte[] bytes = await Packet();
            if (variant == 0) bytes[0] ^= 0xff;
            if (variant == 1) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 99);
            if (variant == 2) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), -1);
            if (variant == 3) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Wire.MaxFrameBytes + 1);
            using var stream = new MemoryStream(bytes);
            await Throws<InvalidDataException>(async () => { var frame = await Wire.ReadMediaAsync(stream, Wire.MaxFrameBytes, CancellationToken.None); frame.Frame.Dispose(); });
        }
        using var output = new MemoryStream();
        await Throws<InvalidDataException>(() => Wire.WriteMediaAsync(output, Wire.VideoKind, new byte[Wire.MaxFrameBytes + 1], new byte[16], 0, CancellationToken.None));
        Require(output.Length == 0);
        await Throws<InvalidDataException>(() => Wire.WriteMediaAsync(output, Wire.VideoKind, new byte[4], new byte[15], 0, CancellationToken.None));
    }
    private static async Task MediaTruncated()
    {
        byte[] full = await Packet();
        foreach (int length in new[] { 0, 15, 31, 34 })
        {
            using var stream = new MemoryStream(full[..length]);
            await Throws<EndOfStreamException>(async () => { var packet = await Wire.ReadMediaAsync(stream, Wire.MaxFrameBytes, CancellationToken.None); packet.Frame.Dispose(); });
        }
    }
    private static async Task MediaCancellation()
    {
        byte[] full = await Packet();
        using var stream = new HeaderThenBlockedStream(full[..32]);
        using var canceled = new CancellationTokenSource(80);
        await Throws<OperationCanceledException>(async () => { var packet = await Wire.ReadMediaAsync(stream, Wire.MaxFrameBytes, canceled.Token); packet.Frame.Dispose(); });
    }
    private static async Task JsonRoundtrip()
    {
        using var memory = new MemoryStream();
        await Wire.WriteJsonAsync(memory, new HostResponse { Id = 19, Success = true, Data = [5, 4, 3] }, CancellationToken.None);
        memory.Position = 0;
        var response = await Wire.ReadJsonAsync<HostResponse>(memory, CancellationToken.None);
        Require(response.Id == 19 && response.Success && response.Data.SequenceEqual(new byte[] { 5, 4, 3 }));
        Require(!Connection("description").ToString().Contains(FakeHost.CredentialCanary, StringComparison.Ordinal));
    }
    private static async Task JsonMalformed()
    {
        foreach (int length in new[] { -1, 0, 1, 131073 })
        {
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
            using var stream = new MemoryStream(prefix);
            await Throws<InvalidDataException>(async () => { _ = await Wire.ReadJsonAsync<HostResponse>(stream, CancellationToken.None); });
        }
        using (var malformed = JsonBytes("xx")) await Throws<JsonException>(async () => { _ = await Wire.ReadJsonAsync<HostResponse>(malformed, CancellationToken.None); });
        using (var nullMessage = JsonBytes("null")) await Throws<InvalidDataException>(async () => { _ = await Wire.ReadJsonAsync<HostResponse>(nullMessage, CancellationToken.None); });
        using (var truncated = new MemoryStream(new byte[] { 4, 0, 0, 0, 123, 125 })) await Throws<EndOfStreamException>(async () => { _ = await Wire.ReadJsonAsync<HostResponse>(truncated, CancellationToken.None); });
        using var output = new MemoryStream();
        await Throws<InvalidDataException>(() => Wire.WriteJsonAsync(output, new HostRequest { Data = new byte[100000] }, CancellationToken.None));
        Require(output.Length == 0);
    }
    private static MemoryStream JsonBytes(string value)
    {
        byte[] body = Encoding.UTF8.GetBytes(value), result = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, body.Length); body.CopyTo(result, 4); return new MemoryStream(result);
    }

    private static CameraConnection Connection(string scenario) => new() { Uid = scenario, Username = "fake-user", Password = FakeHost.CredentialCanary, SdkFolder = "fake-sdk" };
    private static Task<CameraSession> Open(string scenario) => CameraSession.OpenAsync(Connection(scenario), Environment.ProcessPath!);
    private static bool Gone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; }
    }
    private static async Task AbruptExit()
    {
        await using var session = await Open("normal"); int pid = session.WorkerProcessId;
        var clock = Stopwatch.StartNew();
        await TransportFails(session.ControlAsync(90, []));
        Require(clock.Elapsed < TimeSpan.FromSeconds(2));
        await Task.Delay(50); Require(Gone(pid));
    }
    private static async Task PendingCancellation()
    {
        await using var session = await Open("normal");
        using var cancel = new CancellationTokenSource(80);
        var clock = Stopwatch.StartNew();
        await Throws<OperationCanceledException>(async () => { _ = await session.ControlAsync(92, [], ct: cancel.Token); });
        Require(clock.Elapsed < TimeSpan.FromSeconds(1));
        byte[] next = await session.ControlAsync(93, []); Require(next.SequenceEqual(new byte[] { 9, 3 }));
        await Task.Delay(350);
        Require((await session.ControlAsync(93, [])).SequenceEqual(next));
    }
    private static async Task MalformedReply()
    {
        await using var session = await Open("normal"); int pid = session.WorkerProcessId;
        using var deadline = new CancellationTokenSource(2000);
        var clock = Stopwatch.StartNew();
        await TransportFails(session.ControlAsync(94, [], ct: deadline.Token));
        Require(clock.Elapsed < TimeSpan.FromMilliseconds(800));
        await Task.Delay(50); Require(Gone(pid));
    }
    private static async Task PipeReadCancellation()
    {
        await using var session = await Open("normal");
        using var cancel = new CancellationTokenSource(80);
        await Throws<OperationCanceledException>(async () => { using var frame = await session.ReadAudioAsync(cancel.Token); });
        Require((await session.ControlAsync(93, [])).Length == 2);
    }
    private static async Task BlockedWriteCancellation()
    {
        await using var session = await Open("stall-read");
        using var cancel = new CancellationTokenSource(80);
        // A fixed 160-request cap fills the 64 KiB pipe even with the native
        // command payload limit. FakeHost pauses reads for a bounded 500 ms.
        Task[] requests = Enumerable.Range(0, 160).Select(_ => TransportFails(session.ControlAsync(93, new byte[Wire.MaxControlBytes], ct: cancel.Token))).ToArray();
        await Task.WhenAll(requests);
        Require((await session.ControlAsync(93, [])).SequenceEqual(new byte[] { 9, 3 }));
    }
    private static async Task ControlBounds()
    {
        await using var session = await Open("normal");
        await Throws<ArgumentOutOfRangeException>(async () => { _ = await session.ControlAsync(93, new byte[Wire.MaxControlBytes + 1]); });
        Require((await session.ControlAsync(93, new byte[Wire.MaxControlBytes])).Length == 2);
        try { _ = await session.ControlAsync(97, []); }
        catch (CameraTransportException ex) { Require(ex.NativeCode == -20009); return; }
        throw new InvalidOperationException("The native failure was not preserved.");
    }
    private static async Task TalkCancellation()
    {
        await using var session = await Open("normal");
        using var cancel = new CancellationTokenSource(80);
        var clock = Stopwatch.StartNew();
        await Throws<OperationCanceledException>(() => session.StartTalkAsync(cancel.Token));
        Require(clock.Elapsed < TimeSpan.FromSeconds(1));
        byte[] state = await session.ControlAsync(95, []);
        Require(state.Length == 2 && state[0] == 1 && state[1] >= 1);
    }
    private static async Task BoundedDisposal()
    {
        var session = await Open("hang-close"); int pid = session.WorkerProcessId;
        Task pending = session.ControlAsync(91, []);
        await Task.Delay(50); var clock = Stopwatch.StartNew();
        await session.DisposeAsync();
        await TransportFails(pending);
        Require(clock.Elapsed < TimeSpan.FromMilliseconds(3600));
        Require(Gone(pid));
    }
    // Regression for the 0.2.0 talk crash: avServStart with null strings faulted
    // inside the SDK, so the worker passes an accept callback through avServStart2.
    private static Task TalkBinding()
    {
        var original = Host.NativeSdk.StartServer;
        try
        {
            Host.NativeSdk.StartServer = (sid, authenticate, timeout, type, channel) => { Require(sid == 42 && authenticate(IntPtr.Zero, IntPtr.Zero) == 1 && timeout == 10 && type == 0 && channel == 1); return 7; };
            Require(Host.NativeSdk.OpenTalk(42, 1) == 7);
        }
        finally { Host.NativeSdk.StartServer = original; }
        static System.Runtime.InteropServices.CallingConvention Convention<T>() => typeof(T).GetCustomAttributes(typeof(System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute), false).Cast<System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute>().Single().CallingConvention;
        Require(Convention<Host.NativeSdk.AuthenticationDelegate>() == System.Runtime.InteropServices.CallingConvention.StdCall && Convention<Host.NativeSdk.ServerStartDelegate>() == System.Runtime.InteropServices.CallingConvention.Cdecl);
        return Task.CompletedTask;
    }
    private static Task LanSearchParsing()
    {
        byte[] buffer = new byte[Host.NativeSdk.LanSearchStride * 3];
        void Entry(int index, string uid, string ip, string name)
        {
            int offset = index * Host.NativeSdk.LanSearchStride;
            Encoding.ASCII.GetBytes(uid).CopyTo(buffer, offset); Encoding.ASCII.GetBytes(ip).CopyTo(buffer, offset + 21); Encoding.ASCII.GetBytes(name).CopyTo(buffer, offset + 40);
        }
        Entry(0, "SHORTUID", "192.0.2.5", "Rejected");
        Entry(1, "ABCDEFGHIJ0123456789", "192.0.2.6", "Porch");
        var found = Host.NativeSdk.ParseLanSearch(buffer, 3);
        Require(found.Count == 1 && found[0] == new DiscoveredDevice("ABCDEFGHIJ0123456789", "192.0.2.6", "Porch"));
        return Task.CompletedTask;
    }
    private static async Task WorkerDiscovery()
    {
        var found = await CameraSession.DiscoverAsync("fake-sdk", Environment.ProcessPath!);
        Require(found.Count == 1 && found[0] == FakeHost.Discovered);
    }
}

internal sealed class HeaderThenBlockedStream(byte[] header) : Stream
{
    private int offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (offset < header.Length) { int count = Math.Min(buffer.Length, header.Length - offset); header.AsMemory(offset, count).CopyTo(buffer); offset += count; return count; }
        await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
