using CameraLite.Transport;

namespace CameraLite.Transport.Host;

internal sealed class WorkerSession(Stream videoPipe, Stream audioPipe, Action fatal) : IAsyncDisposable
{
    private static void Trace(string stage) { if (Environment.GetEnvironmentVariable("CAMERALITE_TRACE") == "1") Console.Error.WriteLine("CLT:" + Environment.TickCount64 + ":" + stage); }
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new(), controlSendLock = new(), queryLock = new();
    private readonly SemaphoreSlim queryGate = new(1, 1), talkGate = new(1, 1), talkSendGate = new(1, 1);
    private byte talkChannel = 1;
    private int sid = -1, index = -1, talkIndex = -1;
    private Task? connectTask, videoReader, audioReader, controlReader;
    private Task<byte[]>? closeTask, talkStartTask;
    private CancellationTokenSource? talkCancellation;
    private PendingQuery? pendingQuery;
    private bool speakerRequested, listeningSuspended;
    private sealed record PendingQuery(uint Type, TaskCompletionSource<byte[]> Reply);

    internal async Task<byte[]> ConnectAsync(CameraConnection connection)
    {
        if (connection.Uid.Length is < 1 or > 64 || connection.Username.Length > 128 || connection.Password.Length > 128 || connection.SdkFolder.Length > 4096 || connection.TalkChannel is < 1 or > 31)
            throw new CameraTransportException("Invalid camera connection settings.");
        Task connectionWork;
        talkChannel = connection.TalkChannel;
        lock (stateLock)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (connectTask != null) throw new CameraTransportException("This worker already owns a camera connection.");
            connectTask = connectionWork = Task.Run(() =>
            {
                NativeSdk.Load(connection.SdkFolder);
                lifetime.Token.ThrowIfCancellationRequested();
                int allocated = NativeSdk.GetSessionId(); NativeSdk.Check(allocated, "Allocating the camera connection failed.");
                Volatile.Write(ref sid, allocated);
                lifetime.Token.ThrowIfCancellationRequested();
                using var abort = lifetime.Token.Register(AbortConnection);
                int connected = NativeSdk.Connect(connection.Uid, allocated); NativeSdk.Check(connected, "Connecting to the camera failed.");
                Volatile.Write(ref sid, connected);
                lifetime.Token.ThrowIfCancellationRequested();
                int resend = 0;
                int authenticated = NativeSdk.StartClient(connected, connection.Username, connection.Password, 20, out _, 0, ref resend);
                NativeSdk.Check(authenticated, "Camera authentication failed.");
                Volatile.Write(ref index, authenticated);
                lifetime.Token.ThrowIfCancellationRequested();
                SendControl(255, new byte[2]); SendControl(511, new byte[8]); SendControl(768, new byte[8]);
                lifetime.Token.ThrowIfCancellationRequested();
                videoReader = Task.Run(ReadVideoAsync);
                audioReader = Task.Run(ReadAudioAsync);
                controlReader = Task.Factory.StartNew(ReadControlAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            });
        }
        await connectionWork;
        return [];
    }

    // A discovery worker owns no camera connection and is closed right after the search.
    internal async Task<byte[]> DiscoverAsync(string sdkFolder)
    {
        if (sdkFolder.Length > 4096) throw new CameraTransportException("Invalid camera SDK folder.");
        lock (stateLock)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (connectTask != null) throw new CameraTransportException("This worker already owns a camera connection.");
        }
        var found = await Task.Run(() => { NativeSdk.Load(sdkFolder); return NativeSdk.Discover(5000); });
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(found);
    }

    internal async Task<byte[]> ControlAsync(uint type, byte[] data, uint expectedResponse)
    {
        if (data.Length > Wire.MaxControlBytes) throw new CameraTransportException("Invalid camera command size.");
        EnsureConnected();
        await queryGate.WaitAsync(lifetime.Token);
        PendingQuery? query = null;
        try
        {
            EnsureConnected();
            if (expectedResponse != 0)
            {
                query = new(expectedResponse, new(TaskCreationOptions.RunContinuationsAsynchronously));
                lock (queryLock) pendingQuery = query;
            }
            try { await Task.Run(() => SendControl(type, data)).WaitAsync(TimeSpan.FromSeconds(4), lifetime.Token); }
            catch (TimeoutException) { fatal(); throw; }
            if (query == null) return [];
            try { return await query.Reply.Task.WaitAsync(TimeSpan.FromSeconds(4), lifetime.Token); }
            catch (TimeoutException)
            {
                // Native IOCTRL replies have no request ID. Once a reply is
                // late, reusing this session could match it to the next query.
                fatal();
                throw;
            }
        }
        finally
        {
            lock (queryLock) { if (ReferenceEquals(pendingQuery, query)) pendingQuery = null; }
            queryGate.Release();
        }
    }

    private void SendControl(uint type, byte[] data, bool duringClose = false)
    {
        lock (controlSendLock)
        {
            if (!duringClose) lifetime.Token.ThrowIfCancellationRequested();
            int client = Volatile.Read(ref index);
            if (client < 0) throw new CameraTransportException("The camera is not connected.");
            NativeSdk.Check(NativeSdk.SendControl(client, type, data, data.Length), "Sending the camera command failed.");
        }
    }

    private async Task ReadControlAsync()
    {
        byte[] buffer = new byte[65536];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int n = NativeSdk.ReceiveControl(Volatile.Read(ref index), out uint type, buffer, buffer.Length, 200);
                if (n is -20011 or -20012) { if (n == -20012) await Task.Delay(15, lifetime.Token); continue; }
                NativeSdk.Check(n, "Receiving camera commands failed.");
                if (n > buffer.Length) throw new InvalidDataException();
                lock (queryLock)
                {
                    if (pendingQuery is { } query && query.Type == type) query.Reply.TrySetResult(buffer.AsSpan(0, n).ToArray());
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!lifetime.IsCancellationRequested) fatal(); }
    }

    private async Task ReadVideoAsync()
    {
        byte[] buffer = new byte[Wire.MaxFrameBytes], info = new byte[16];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Array.Clear(info);
                int n = NativeSdk.ReceiveFrame(Volatile.Read(ref index), buffer, buffer.Length, out _, out _, info, info.Length, out _, out uint number);
                if (n == -20012) { await Task.Delay(15, lifetime.Token); continue; }
                if (n is -20013 or -20014) { await WriteMediaAsync(videoPipe, Wire.LostFrameKind, ReadOnlyMemory<byte>.Empty, info, number); continue; }
                NativeSdk.Check(n, "Receiving camera video failed.");
                if (n > buffer.Length) throw new InvalidDataException();
                if (n > 0) await WriteMediaAsync(videoPipe, Wire.VideoKind, buffer.AsMemory(0, n), info, number);
            }
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) fatal(); }
        catch { if (!lifetime.IsCancellationRequested) fatal(); }
    }

    private async Task ReadAudioAsync()
    {
        byte[] buffer = new byte[Wire.MaxAudioBytes], info = new byte[16];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Array.Clear(info);
                int n = NativeSdk.ReceiveAudio(Volatile.Read(ref index), buffer, buffer.Length, info, info.Length, out uint number);
                if (n == -20012) { await Task.Delay(20, lifetime.Token); continue; }
                if (n is -20013 or -20014) continue;
                NativeSdk.Check(n, "Receiving camera audio failed.");
                if (n > buffer.Length) throw new InvalidDataException();
                if (n > 0) await WriteMediaAsync(audioPipe, Wire.AudioKind, buffer.AsMemory(0, n), info, number);
            }
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) fatal(); }
        catch { if (!lifetime.IsCancellationRequested) fatal(); }
    }

    private async Task WriteMediaAsync(Stream pipe, int kind, ReadOnlyMemory<byte> data, byte[] info, uint number)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await Wire.WriteMediaAsync(pipe, kind, data, info, number, deadline.Token);
    }

    internal Task<byte[]> StartTalkAsync()
    {
        lock (stateLock)
        {
            EnsureConnected();
            if (talkStartTask is { IsCompleted: false }) return talkStartTask;
            if (Volatile.Read(ref talkIndex) >= 0) return Task.FromResult(Array.Empty<byte>());
            talkCancellation?.Dispose();
            talkCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            talkStartTask = StartTalkCoreAsync(talkCancellation.Token);
            return talkStartTask;
        }
    }

    private async Task<byte[]> StartTalkCoreAsync(CancellationToken ct)
    {
        await talkGate.WaitAsync(ct);
        Task<int>? server = null;
        try
        {
            EnsureConnected(); ct.ThrowIfCancellationRequested();
            Trace("talk reset send");
            // A previous process may have died while the camera's talk client was
            // active. Clear that output before opening a new talk listener.
            await Task.Run(() => SendControl(849, new byte[8]));
            Trace("talk reset done");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server = Task.Factory.StartNew(() =>
            {
                ct.ThrowIfCancellationRequested(); Trace("listener entered channel " + talkChannel); entered.TrySetResult();
                int result = NativeSdk.OpenTalk(Volatile.Read(ref sid), talkChannel); Trace("listener returned " + result); return result;
            },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            using var abort = ct.Register(AbortTalk);
            await entered.Task.WaitAsync(ct);
            await Task.Delay(100, ct);
            speakerRequested = true;
            Trace("speaker start send");
            await Task.Run(() => SendControl(848, [talkChannel, 0, 0, 0, 0, 0, 0, 0]));
            Trace("speaker start done");
            ct.ThrowIfCancellationRequested();
            listeningSuspended = true;
            await Task.Run(() => SendControl(769, new byte[8]));
            Trace("listening paused");
            int opened = await server;
            Volatile.Write(ref talkIndex, opened);
            ct.ThrowIfCancellationRequested();
            NativeSdk.Check(opened, "Opening the camera talk output failed.");
            return [];
        }
        catch
        {
            AbortTalk();
            if (server != null)
            {
                int opened = await server;
                if (opened >= 0) Volatile.Write(ref talkIndex, opened);
            }
            await StopTalkCoreAsync();
            throw;
        }
        finally { talkGate.Release(); }
    }

    internal async Task<byte[]> StopTalkAsync()
    {
        CancelTalk(); // Cancellation precedes the gate, including an in-flight native login.
        await talkGate.WaitAsync();
        try { await StopTalkCoreAsync(); return []; }
        finally { talkGate.Release(); }
    }

    private async Task StopTalkCoreAsync()
    {
        await talkSendGate.WaitAsync();
        try
        {
            int talk = Interlocked.Exchange(ref talkIndex, -1);
            if (talk >= 0) await Task.Run(() => NativeSdk.StopServer(talk));
            if (speakerRequested)
            {
                speakerRequested = false;
                try { await Task.Run(() => SendControl(849, new byte[8], duringClose: true)); } catch (CameraTransportException) { }
            }
            if (listeningSuspended)
            {
                listeningSuspended = false;
                if (!lifetime.IsCancellationRequested)
                    try { await Task.Run(() => SendControl(768, new byte[8])); } catch (CameraTransportException) { }
            }
        }
        finally { talkSendGate.Release(); }
    }

    internal async Task<byte[]> WriteTalkAudioAsync(byte[] data, byte[] info)
    {
        if (data.Length is < 1 or > 8192 || info.Length != 16) throw new CameraTransportException("Invalid microphone audio packet.");
        await talkSendGate.WaitAsync(lifetime.Token);
        try
        {
            EnsureConnected();
            if (Volatile.Read(ref talkIndex) < 0 || talkCancellation?.IsCancellationRequested != false)
                throw new CameraTransportException("The camera talk output is closed.");
            int talk = Volatile.Read(ref talkIndex);
            await Task.Run(() => NativeSdk.Check(NativeSdk.SendAudio(talk, data, data.Length, info, info.Length), "Sending microphone audio failed."));
            return [];
        }
        finally { talkSendGate.Release(); }
    }

    private void EnsureConnected()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref index) < 0 || connectTask is not { IsCompletedSuccessfully: true })
            throw new CameraTransportException("The camera is not connected.");
    }
    private void AbortConnection()
    {
        int session = Volatile.Read(ref sid);
        if (session >= 0 && NativeSdk.Loaded) { NativeSdk.AbortConnect(session); NativeSdk.ExitClient(session, 0); }
    }
    private void AbortTalk()
    {
        int session = Volatile.Read(ref sid);
        if (session >= 0 && NativeSdk.Loaded) NativeSdk.ExitServer(session, talkChannel);
    }
    private void CancelTalk()
    {
        lock (stateLock) talkCancellation?.Cancel();
        AbortTalk();
    }

    internal Task<byte[]> CloseAsync()
    {
        lock (stateLock) return closeTask ??= CloseCoreAsync();
    }
    private async Task<byte[]> CloseCoreAsync()
    {
        lifetime.Cancel(); CancelTalk(); AbortConnection();
        if (connectTask != null) { try { await connectTask; } catch { } }
        await StopTalkAsync();
        lock (queryLock) pendingQuery?.Reply.TrySetCanceled();
        await Task.WhenAll(new[] { videoReader, audioReader, controlReader }.OfType<Task>());
        // Read calls have finished. Taking the send lock also prevents an
        // in-flight native command from sharing an index being released here.
        await Task.Run(() =>
        {
            lock (controlSendLock)
            {
                int client = Interlocked.Exchange(ref index, -1);
                if (client >= 0) NativeSdk.StopClient(client);
                int session = Interlocked.Exchange(ref sid, -1);
                if (session >= 0 && NativeSdk.Loaded) NativeSdk.CloseSession(session);
            }
        });
        return [];
    }
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        // Other dispatched requests may still be observing cancellation. Their
        // managed gates are retained until this short-lived worker process exits.
        talkCancellation?.Dispose();
    }
}
