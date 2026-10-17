using System.Buffers;
using System.Threading.Channels;

namespace CameraLite;

// Compressed video frames depend on earlier frames. Never silently drop them.
// Frames may be backed by pooled arrays; the stream returns them once consumed.
internal sealed class FrameStream : Stream
{
    private readonly record struct Frame(byte[] Data, int Length, bool Pooled);
    internal const int MaxFrameBytes = 2_000_000;
    private readonly Channel<Frame> frames;
    private readonly CancellationTokenSource stop = new();
    private Frame current = new([], 0, false);
    private int offset, ended;
    internal FrameStream(int capacity = 8) => frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
    internal int Backlog => frames.Reader.Count;
    internal Task PushAsync(byte[] frame, CancellationToken ct) => PushAsync(new Frame(frame, frame.Length, false), ct);
    // Copies the frame into a pooled array, so the caller may reuse its buffer.
    internal Task PushCopyAsync(ReadOnlySpan<byte> frame, CancellationToken ct)
    {
        if (frame.Length == 0) return Task.CompletedTask;
        if (frame.Length > MaxFrameBytes) throw new IOException("Camera frame exceeds the video buffer limit.");
        byte[] data = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(data);
        return PushAsync(new Frame(data, frame.Length, true), ct);
    }
    private async Task PushAsync(Frame frame, CancellationToken ct)
    {
        if (frame.Length == 0) return;
        if (frame.Length > MaxFrameBytes) { Release(frame); throw new IOException("Camera frame exceeds the video buffer limit."); }
        // Fast path: no timer or linked token per frame while the decoder keeps up.
        if (Volatile.Read(ref ended) == 0 && frames.Writer.TryWrite(frame)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        deadline.CancelAfter(2000);
        try { await frames.Writer.WriteAsync(frame, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !stop.IsCancellationRequested) { Release(frame); throw new IOException("Video decoder stopped consuming frames. Reconnect to recover."); }
        catch { Release(frame); throw; }
    }
    private static void Release(Frame frame) { if (frame.Pooled) ArrayPool<byte>.Shared.Return(frame.Data); }
    internal void End()
    {
        if (Interlocked.Exchange(ref ended, 1) != 0) return;
        stop.Cancel(); frames.Writer.TryComplete();
        while (frames.Reader.TryRead(out var frame)) Release(frame);
    }
    public override int Read(byte[] buffer, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(start); ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (start > buffer.Length - count) throw new ArgumentException("Invalid stream read range.");
        if (count == 0) return 0;
        if (Volatile.Read(ref ended) != 0) { ReleaseCurrent(); return 0; }
        if (offset >= current.Length)
        {
            ReleaseCurrent();
            try { current = frames.Reader.ReadAsync(stop.Token).AsTask().GetAwaiter().GetResult(); offset = 0; }
            catch (OperationCanceledException) { return 0; }
            catch (ChannelClosedException) { return 0; }
        }
        int length = Math.Min(count, current.Length - offset);
        current.Data.AsSpan(offset, length).CopyTo(buffer.AsSpan(start, length)); offset += length; return length;
    }
    // Only the reader thread touches the current frame.
    private void ReleaseCurrent() { Release(current); current = new([], 0, false); offset = 0; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long l) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { End(); base.Dispose(disposing); }
}
