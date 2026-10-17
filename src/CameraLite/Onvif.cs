using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CameraLite;

public sealed record DiscoveredCamera(string Name, string Address, string Kind = "ONVIF", string Uid = "")
{
    public override string ToString() => $"{Name} — {Address}";
}
public static class Discovery
{
    public static async Task<List<DiscoveredCamera>> SearchAsync(CancellationToken ct)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address).Distinct().ToArray();
        var results = await Task.WhenAll(addresses.Select(a => ProbeInterface(a, ct)));
        return results.SelectMany(r => r).DistinctBy(r => r.Address).ToList();
    }
    private static async Task<List<DiscoveredCamera>> ProbeInterface(IPAddress address, CancellationToken ct)
    {
        var found = new List<DiscoveredCamera>();
        try
        {
            using var socket = new UdpClient(new IPEndPoint(address, 0));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            string probe = $"""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl"><s:Header><a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID><a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To><a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action></s:Header><s:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body></s:Envelope>""";
            var data = Encoding.UTF8.GetBytes(probe);
            await socket.SendAsync(data, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702), ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (!deadline.IsCancellationRequested)
            {
                var reply = await socket.ReceiveAsync(deadline.Token);
                var document = XmlDocumentSafe(Encoding.UTF8.GetString(reply.Buffer));
                foreach (var match in document.Descendants().Where(x => x.Name.LocalName == "ProbeMatch"))
                {
                    var urls = match.Descendants().FirstOrDefault(x => x.Name.LocalName == "XAddrs")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    foreach (string url in urls)
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                            found.Add(new("Camera at " + reply.RemoteEndPoint.Address, url));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (XmlException) { }
        return found;
    }
    internal static XDocument XmlDocumentSafe(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        return XDocument.Load(reader);
    }
}
public sealed class OnvifClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string username, password;
    private readonly Uri device;
    private const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    public OnvifClient(string address, string username, string password)
    {
        device = CameraUri.OnvifAddress(address);
        this.username = username; this.password = password;
        http = new(new HttpClientHandler { Credentials = new NetworkCredential(username, password), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    }
    private string SecurityHeader()
    {
        if (username.Length == 0) return "";
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        byte[] digest = SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(created + password)).ToArray());
        return $"""<wsse:Security s:mustUnderstand="1" xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"><wsse:UsernameToken><wsse:Username>{Escape(username)}</wsse:Username><wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{Convert.ToBase64String(digest)}</wsse:Password><wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</wsse:Nonce><wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security>""";
    }
    internal static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? "";
    private async Task<XDocument> Soap(Uri target, string action, string body, CancellationToken ct)
    {
        string xml = $"""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:tt="http://www.onvif.org/ver10/schema"><s:Header>{SecurityHeader()}</s:Header><s:Body>{body}</s:Body></s:Envelope>""";
        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        request.Content = new StringContent(xml, Encoding.UTF8);
        request.Content.Headers.ContentType = new("application/soap+xml");
        request.Content.Headers.ContentType.Parameters.Add(new("action", '"' + action + '"'));
        using var response = await http.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        var rejected = new IOException("Camera rejected the username/password. Check its ONVIF account and clock.");
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw rejected;
        // SOAP 1.2 faults arrive with HTTP 400/500, so read the fault before the status.
        XDocument? document = null;
        try { document = Discovery.XmlDocumentSafe(text); }
        catch (XmlException) when (!response.IsSuccessStatusCode) { }
        var fault = document?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault != null)
        {
            if (fault.Descendants().Any(e => e.Name.LocalName == "Value" && (e.Value.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) || e.Value.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase)))) throw rejected;
            throw new IOException("ONVIF: " + (fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? "request rejected"));
        }
        response.EnsureSuccessStatusCode();
        return document!;
    }
    public async Task<List<StreamProfile>> ProfilesAsync(CancellationToken ct)
    {
        var capabilities = await Soap(device, "http://www.onvif.org/ver10/device/wsdl/GetCapabilities", """<tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl"><tds:Category>Media</tds:Category></tds:GetCapabilities>""", ct);
        string? endpoint = capabilities.Descendants().FirstOrDefault(e => e.Name.LocalName == "Media")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value;
        if (endpoint == null) throw new IOException("Camera did not advertise the ONVIF Media service. Try its RTSP URL instead.");
        var media = NormalizeUri(endpoint);
        if (!string.Equals(media.Host, device.Host, StringComparison.OrdinalIgnoreCase)) throw new IOException("Camera advertised a media service on a different host. Enter that ONVIF address explicitly.");
        var profiles = await Soap(media, MediaNs + "/GetProfiles", $"<trt:GetProfiles xmlns:trt=\"{MediaNs}\"/>", ct);
        var found = new List<StreamProfile>();
        foreach (var profile in profiles.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            string? token = profile.Attribute("token")?.Value;
            if (token == null) continue;
            string name = profile.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? token;
            var encoder = profile.Elements().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
            int.TryParse(encoder?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Width")?.Value, out int width);
            int.TryParse(encoder?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Height")?.Value, out int height);
            var stream = await Soap(media, MediaNs + "/GetStreamUri", $"""<trt:GetStreamUri xmlns:trt="{MediaNs}"><trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup><trt:ProfileToken>{Escape(token)}</trt:ProfileToken></trt:GetStreamUri>""", ct);
            string? uri = stream.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
            if (uri != null)
            {
                var normalized = NormalizeUri(uri);
                if (!string.IsNullOrEmpty(normalized.UserInfo)) normalized = new UriBuilder(normalized) { UserName = "", Password = "" }.Uri;
                CameraUri.ValidateStream(normalized.AbsoluteUri);
                found.Add(new(name, normalized.AbsoluteUri, width, height, token));
            }
        }
        if (found.Count == 0) throw new IOException("No video stream profiles were returned.");
        return found.OrderByDescending(p => (long)p.Width * p.Height).ToList();
    }
    private Uri NormalizeUri(string text)
    {
        var uri = new Uri(text);
        return uri.Host is "0.0.0.0" or "localhost" or "127.0.0.1" ? new UriBuilder(uri) { Host = device.Host }.Uri : uri;
    }
    public void Dispose() => http.Dispose();
}
