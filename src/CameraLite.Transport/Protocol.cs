using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace CameraLite.Transport;

public sealed class CameraConnection
{
    public required string Uid { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string SdkFolder { get; init; }
    public byte TalkChannel { get; init; } = 1;
    public override string ToString() => "Camera connection (credentials omitted)";
}

// Public for the companion worker. Applications should use CameraSession.
public sealed class HostRequest
{
    public int Id { get; set; }
    public string Operation { get; set; } = "";
    public CameraConnection? Connection { get; set; }
    public uint Type { get; set; }
    public uint ExpectedResponse { get; set; }
    public byte[] Data { get; set; } = [];
    public byte[] Info { get; set; } = [];
    public string SdkFolder { get; set; } = "";
}
// A camera that answered the SDK's LAN search.
public sealed record DiscoveredDevice(string Uid, string Address, string Name);
public sealed class HostResponse
{
    public int Id { get; set; }
    public bool Success { get; set; }
    public byte[] Data { get; set; } = [];
    public string? Error { get; set; }
    public int Code { get; set; }
}

public sealed class CameraTransportException(string message, int code = 0) : IOException(code != 0 && !message.Contains($"TUTK {code}", StringComparison.Ordinal) ? $"{message} (TUTK {code})." : message)
{
    public int NativeCode { get; } = code;
}

public sealed class MediaFrame : IDisposable
{
    private byte[]? buffer;
    internal MediaFrame(byte[] data, int length, byte[] info, uint number) { buffer = data; Length = length; Info = info; Number = number; }
    public int Length { get; }
    public ReadOnlyMemory<byte> Data => (buffer ?? throw new ObjectDisposedException(nameof(MediaFrame))).AsMemory(0, Length);
    public byte[] Info { get; }
    public uint Number { get; }
    public int Codec => BinaryPrimitives.ReadUInt16LittleEndian(Info);
    public void Dispose() { var data = Interlocked.Exchange(ref buffer, null); if (data != null) ArrayPool<byte>.Shared.Return(data); }
}

public static class Wire
{
    public const int MaxFrameBytes = 2_000_000;
    public const int MaxAudioBytes = 65536;
    public const int MaxControlBytes = 1020;
    public const int LostFrameKind = 3;
    public const int VideoKind = 1, AudioKind = 2;
    private const uint Magic = 0x31544c43;
    public static async Task WriteMediaAsync(Stream stream, int kind, ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> info, uint number, CancellationToken ct)
    {
        if (data.Length > MaxFrameBytes || info.Length != 16) throw new InvalidDataException("Invalid media packet size.");
        byte[] header = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), kind);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), number);
        info.CopyTo(header.AsMemory(16));
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
    }
    public static async Task<(int Kind, MediaFrame Frame)> ReadMediaAsync(Stream stream, int maximum, CancellationToken ct)
    {
        byte[] header = new byte[32]; await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw new InvalidDataException("Invalid transport header.");
        int kind = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (length < 0 || length > maximum || kind is not (VideoKind or AudioKind or LostFrameKind)) throw new InvalidDataException("Invalid transport packet.");
        byte[] data = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            await stream.ReadExactlyAsync(data.AsMemory(0, length), ct).ConfigureAwait(false);
            return (kind, new MediaFrame(data, length, header[16..], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))));
        }
        catch { ArrayPool<byte>.Shared.Return(data); throw; }
    }
    // Bounded UTF-8 JSON messages avoid unbounded ReadLine allocations on a damaged pipe.
    public static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > 131072) throw new InvalidDataException("Transport message too large.");
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        await stream.WriteAsync(size, ct).ConfigureAwait(false); await stream.WriteAsync(data, ct).ConfigureAwait(false);
    }
    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, ct).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (count is < 2 or > 131072) throw new InvalidDataException("Invalid transport message length.");
        byte[] data = new byte[count]; await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty transport message.");
    }
}
