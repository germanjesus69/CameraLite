using System.Buffers.Binary;
using System.Text.Json;

namespace CameraLite;

public sealed class Preferences
{
    public int Volume { get; set; } = 70;
    public bool Muted { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool HardwareDecoding { get; set; }
    public int BufferMilliseconds { get; set; } = 250;
    public string Microphone { get; set; } = "";
    public string LastCamera { get; set; } = "";
    public string SnapshotFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CameraLite");
    public static Preferences Load()
    {
        try
        {
            var p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(CameraStore.Folder, "preferences.json"))) ?? new();
            p.Volume = Math.Clamp(p.Volume, 0, 100); p.BufferMilliseconds = Math.Clamp(p.BufferMilliseconds, 100, 1500); return p;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(CameraStore.Folder);
        string path = Path.Combine(CameraStore.Folder, "preferences.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

internal static class AudioMeter
{
    internal static float Peak(ReadOnlySpan<byte> pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])));
        return Math.Min(1f, peak / 32768f);
    }
}
