using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace CameraLite.Transport;

/// <summary>A single camera session. Native TUTK code runs only in its disposable worker process.</summary>
public sealed class CameraSession : IAsyncDisposable
{
    private readonly NamedPipeServerStream control, video, audio;
    private readonly Process worker;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writer = new(1), qualityGate = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostResponse>> pending = new();
    private Task? replies;
    private int nextId, disposed;
    private readonly string pipeName;
    private readonly ConcurrentQueue<string> diagnostics = new();
    public int WorkerProcessId => worker.Id;
    public bool IsAlive { get { try { return Volatile.Read(ref disposed) == 0 && !worker.HasExited; } catch (InvalidOperationException) { return false; } } }
    private CameraSession(string hostPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The compatible TUTK worker requires Windows x64.");
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("A 64-bit process is required.");
        if (!File.Exists(hostPath)) throw new FileNotFoundException("Camera transport worker is missing.", hostPath);
        pipeName = "CameraLite-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream Pipe(string suffix, PipeDirection direction) => new(pipeName + suffix, direction, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        control = Pipe("-control", PipeDirection.InOut); video = Pipe("-video", PipeDirection.In); audio = Pipe("-audio", PipeDirection.In);
        var start = new ProcessStartInfo(Path.GetFullPath(hostPath)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(hostPath))! };
        start.ArgumentList.Add(pipeName); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        worker = new Process { StartInfo = start, EnableRaisingEvents = true };
        worker.ErrorDataReceived += (sender, e) => { if (e.Data?.StartsWith("CLT:", StringComparison.Ordinal) == true) { diagnostics.Enqueue(e.Data); while (diagnostics.Count > 16) diagnostics.TryDequeue(out _); } };
        worker.Exited += (_, _) => FailPending(new CameraTransportException("Camera transport worker exited. Reconnect to recover."));
    }
    public static async Task<CameraSession> OpenAsync(CameraConnection connection, string hostPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.Uid) || connection.Uid.Length > 64 || connection.Username.Length > 128 || connection.Password.Length > 128 || connection.TalkChannel is < 1 or > 31)
            throw new ArgumentException("Camera UID or account field is invalid.", nameof(connection));
        var session = new CameraSession(hostPath);
        try
        {
            await session.StartWorkerAsync(ct).ConfigureAwait(false);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct); connect.CancelAfter(TimeSpan.FromSeconds(35));
            await session.CommandAsync(new HostRequest { Operation = "connect", Connection = connection }, connect.Token).ConfigureAwait(false);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <summary>Searches the local network for cameras. The SDK runs only in a short-lived worker.</summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(string sdkFolder, string hostPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sdkFolder);
        if (sdkFolder.Length > 4096) throw new ArgumentException("Camera SDK folder is invalid.", nameof(sdkFolder));
        var session = new CameraSession(hostPath);
        try
        {
            await session.StartWorkerAsync(ct).ConfigureAwait(false);
            var response = await session.CommandAsync(new HostRequest { Operation = "discover", SdkFolder = sdkFolder }, ct).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<List<DiscoveredDevice>>(response.Data) ?? [];
        }
        catch (System.Text.Json.JsonException) { throw new CameraTransportException("The camera search returned an invalid reply."); }
        finally { await session.DisposeAsync().ConfigureAwait(false); }
    }
    private async Task StartWorkerAsync(CancellationToken ct)
    {
        if (!worker.Start()) throw new CameraTransportException("Could not start camera transport worker.");
        worker.BeginErrorReadLine();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        var pipes = Task.WhenAll(control.WaitForConnectionAsync(startup.Token), video.WaitForConnectionAsync(startup.Token), audio.WaitForConnectionAsync(startup.Token));
        // Observe early worker death without waiting out the pipe deadline.
        Task exited = worker.WaitForExitAsync(startup.Token);
        if (await Task.WhenAny(pipes, exited).ConfigureAwait(false) != pipes) { ct.ThrowIfCancellationRequested(); throw new CameraTransportException("Camera transport worker could not start."); }
        await pipes.ConfigureAwait(false);
        replies = ReadRepliesAsync();
    }
    private async Task ReadRepliesAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var response = await Wire.ReadJsonAsync<HostResponse>(control, lifetime.Token).ConfigureAwait(false);
                if (pending.TryRemove(response.Id, out var completion)) completion.TrySetResult(response);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            Invalidate(new CameraTransportException("Camera transport connection closed. Reconnect to recover."));
        }
    }
    private void FailPending(Exception error)
    {
        foreach (var item in pending) if (pending.TryRemove(item.Key, out var completion)) completion.TrySetException(error);
    }
    private void Invalidate(Exception error)
    {
        FailPending(error);
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        control.Dispose(); video.Dispose(); audio.Dispose();
        try { if (!worker.HasExited) worker.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    private async Task<HostResponse> CommandAsync(HostRequest request, CancellationToken ct)
    {
        if (lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(CameraSession));
        ct.ThrowIfCancellationRequested();
        request.Id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[request.Id] = completion;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(request.Operation switch { "connect" => 35, "discover" => 15, "control" => 8, "talk-start" => 12, "talk-stop" => 3, "talk-audio" => 2, _ => 3 }));
            await writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                // Finish a frame already begun even if the caller releases Talk.
                // A partial length/body cannot be followed by another valid request.
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                writeDeadline.CancelAfter(2000);
                try { await Wire.WriteJsonAsync(control, request, writeDeadline.Token).ConfigureAwait(false); }
                catch { Invalidate(new CameraTransportException("Camera transport write failed. Reconnect to recover.")); throw; }
            }
            finally { writer.Release(); }
            var response = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!response.Success) throw new CameraTransportException(response.Error ?? "Camera command failed.", response.Code);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var error = new CameraTransportException("Camera transport operation timed out. Reconnect to recover." + (diagnostics.IsEmpty ? "" : " " + string.Join(" | ", diagnostics)));
            Invalidate(error); throw error;
        }
        finally { pending.TryRemove(request.Id, out _); }
    }
    public async Task<byte[]> ControlAsync(uint type, byte[] data, uint expectedResponse = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > Wire.MaxControlBytes) throw new ArgumentOutOfRangeException(nameof(data));
        return (await CommandAsync(new HostRequest { Operation = "control", Type = type, Data = data, ExpectedResponse = expectedResponse }, ct).ConfigureAwait(false)).Data;
    }
    public async Task<bool> SetQualityAsync(byte quality, CancellationToken ct = default)
    {
        if (quality is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(quality));
        await qualityGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] payload = new byte[8]; payload[4] = quality;
            var ack = await ControlAsync(800, payload, 801, ct).ConfigureAwait(false);
            if (ack.Length == 0 || ack[0] != 0) return false;
            var actual = await ControlAsync(802, new byte[8], 803, ct).ConfigureAwait(false);
            return actual.Length >= 5 && actual[0] == 0 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(actual.AsSpan(1)) == quality;
        }
        finally { qualityGate.Release(); }
    }
    public async Task<(int Kind, MediaFrame Frame)> ReadVideoAsync(CancellationToken ct = default)
    {
        var packet = await Wire.ReadMediaAsync(video, Wire.MaxFrameBytes, ct).ConfigureAwait(false);
        if (packet.Kind is not (Wire.VideoKind or Wire.LostFrameKind)) { packet.Frame.Dispose(); throw new InvalidDataException("Unexpected video packet."); }
        return packet;
    }
    public async Task<MediaFrame> ReadAudioAsync(CancellationToken ct = default)
    {
        var packet = await Wire.ReadMediaAsync(audio, Wire.MaxAudioBytes, ct).ConfigureAwait(false);
        if (packet.Kind != Wire.AudioKind) { packet.Frame.Dispose(); throw new InvalidDataException("Unexpected audio packet."); }
        return packet.Frame;
    }
    public async Task StartTalkAsync(CancellationToken ct = default)
    {
        try { await CommandAsync(new HostRequest { Operation = "talk-start" }, ct).ConfigureAwait(false); }
        catch
        {
            try { using var cleanup = new CancellationTokenSource(3000); await StopTalkAsync(cleanup.Token).ConfigureAwait(false); }
            catch { Invalidate(new CameraTransportException("Camera talk cleanup timed out. Reconnect to recover.")); }
            throw;
        }
    }
    public async Task StopTalkAsync(CancellationToken ct = default)
    {
        try { await CommandAsync(new HostRequest { Operation = "talk-stop" }, ct).ConfigureAwait(false); }
        catch { Invalidate(new CameraTransportException("Camera talk closure could not be confirmed. Reconnect to recover.")); throw; }
    }
    public async Task WriteTalkAudioAsync(byte[] data, byte[] info, CancellationToken ct = default)
    {
        if (data.Length > 8192 || info.Length != 16) throw new ArgumentException("Invalid talk audio packet.");
        await CommandAsync(new HostRequest { Operation = "talk-audio", Data = data, Info = info }, ct).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            if (control.IsConnected && !worker.HasExited)
            {
                using var close = new CancellationTokenSource(1800);
                try { await CommandAsync(new HostRequest { Operation = "close" }, close.Token).ConfigureAwait(false); } catch { }
            }
        }
        catch (InvalidOperationException) { }
        lifetime.Cancel(); control.Dispose(); video.Dispose(); audio.Dispose();
        FailPending(new ObjectDisposedException(nameof(CameraSession)));
        try { if (!worker.HasExited) worker.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        if (replies != null) { try { await replies.ConfigureAwait(false); } catch { } }
        try { using var exit = new CancellationTokenSource(1500); await worker.WaitForExitAsync(exit.Token).ConfigureAwait(false); } catch (InvalidOperationException) { } catch (OperationCanceledException) { }
        worker.Dispose(); lifetime.Dispose();
        // Gates may still be unwinding canceled operations; do not dispose them underneath waiters.
    }
}
