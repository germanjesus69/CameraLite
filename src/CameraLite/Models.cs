using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CameraLite;

public enum CameraKind { Onvif, DirectStream, Tutk }
public sealed record StreamProfile(string Name, string Uri, int Width = 0, int Height = 0, string Token = "")
{
    public override string ToString() => Width > 0 ? $"{Name} · {Width}×{Height}" : Name;
}
public sealed class CameraConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "My camera";
    public CameraKind Kind { get; set; }
    public string Address { get; set; } = "";
    public string Username { get; set; } = "admin";
    public string PasswordEncrypted { get; set; } = "";
    public string SdkFolder { get; set; } = "";
    public List<StreamProfile> Profiles { get; set; } = [];
    public int SelectedProfile { get; set; }
    public string Password()
    {
        if (PasswordEncrypted.Length == 0) return "";
        // DPAPI only opens data for the Windows account that saved it, so a
        // cameras.json restored on another PC or account needs the password again.
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(PasswordEncrypted), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { throw new SavedPasswordException(); }
    }
    public void SetPassword(string value) => PasswordEncrypted = value.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public override string ToString() => Name;
}
public sealed class SavedPasswordException() : IOException(Text)
{
    internal const string Text = "The saved password can’t be read by this Windows account. Edit the camera and enter its password again.";
}
public static class CameraStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraLite");
    public static List<CameraConfig> Load()
    {
        string path = Path.Combine(Folder, "cameras.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<CameraConfig>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { LoadError = "Saved cameras could not be read. The original file is kept as cameras.json.invalid."; File.Copy(path, path + ".invalid", true); return []; }
    }
    public static void Save(IEnumerable<CameraConfig> cameras)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, "cameras.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(cameras, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public static string? LoadError { get; private set; }
}
public static class CameraUri
{
    public static Uri ValidateStream(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("rtsp" or "rtsps" or "http" or "https"))
            throw new ArgumentException("Enter a full rtsp://, rtsps://, http:// or https:// camera stream URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Use the username/password fields instead of putting credentials in the URL.");
        return uri;
    }
    public static Uri OnvifAddress(string value)
    {
        value = value.Trim();
        if (!value.Contains("://")) value = "http://" + value;
        var uri = new Uri(value);
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Enter the camera IP/host or an HTTP ONVIF device URL.");
        return uri.AbsolutePath == "/" ? new Uri(uri, "/onvif/device_service") : uri;
    }
    public static string WithCredentials(string url, string username, string password)
    {
        var uri = new Uri(url);
        if (username.Length == 0) return url;
        return new UriBuilder(uri) { UserName = Uri.EscapeDataString(username), Password = Uri.EscapeDataString(password) }.Uri.AbsoluteUri;
    }
}
