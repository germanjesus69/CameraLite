using System.Runtime.InteropServices;
using CameraLite.Transport;

namespace CameraLite.Transport.Host;

// These are the verified legacy Windows x64 SDK signatures. The worker owns one
// SDK session. A native fault or hung SDK thread can therefore be ended by the
// parent without bringing down the viewer process.
internal static class NativeSdk
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int InitializeDelegate(ushort port);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NoArgsDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OneIntDelegate(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseDelegate(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ConnectDelegate([MarshalAs(UnmanagedType.LPStr)] string uid, int sid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ClientStartDelegate(int sid, [MarshalAs(UnmanagedType.LPStr)] string user, [MarshalAs(UnmanagedType.LPStr)] string password, uint timeout, out uint serverType, byte channel, ref int resend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SendControlDelegate(int index, uint type, byte[] data, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReceiveControlDelegate(int index, out uint type, byte[] data, int size, uint timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReceiveFrameDelegate(int index, byte[] data, int size, out int actual, out int expected, byte[] info, int infoSize, out int infoActual, out uint number);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReceiveAudioDelegate(int index, byte[] data, int size, byte[] info, int infoSize, out uint number);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AuthenticationDelegate(IntPtr user, IntPtr password);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ServerStartDelegate(int sid, AuthenticationDelegate authenticate, uint timeout, uint serviceType, byte channel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ExitChannelDelegate(int sid, byte channel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SendAudioDelegate(int index, byte[] data, int length, byte[] info, int infoLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LanSearchDelegate([In, Out] byte[] buffer, int count, int waitMs);

    internal static NoArgsDelegate GetSessionId = null!;
    internal static OneIntDelegate AbortConnect = null!;
    internal static CloseDelegate CloseSession = null!, StopClient = null!, StopServer = null!;
    internal static ConnectDelegate Connect = null!;
    internal static ClientStartDelegate StartClient = null!;
    internal static SendControlDelegate SendControl = null!;
    internal static ReceiveControlDelegate ReceiveControl = null!;
    internal static ReceiveFrameDelegate ReceiveFrame = null!;
    internal static ReceiveAudioDelegate ReceiveAudio = null!;
    internal static SendAudioDelegate SendAudio = null!;
    internal static ExitChannelDelegate ExitClient = null!, ExitServer = null!;
    internal static LanSearchDelegate LanSearch = null!;
    internal static ServerStartDelegate StartServer = null!;
    // Rooted for the complete native server lifetime. Viewing authentication has
    // already succeeded on this SID before the talk channel is opened.
    private static readonly AuthenticationDelegate AcceptAuthenticatedCamera = (_, _) => 1;
    private static IntPtr iotcLibrary, avLibrary;
    internal static bool Loaded { get; private set; }

    private static T Export<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    internal static void Load(string folder)
    {
        if (Loaded) return;
        string full = Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? Path.Combine(AppContext.BaseDirectory, "vendor-sdk") : folder);
        string bundled = Path.Combine(AppContext.BaseDirectory, "vendor-sdk");
        if (!File.Exists(Path.Combine(full, "IOTCAPIs.dll")) && Path.GetFileName(full).Equals("vendor-sdk", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(full))?.StartsWith("CameraLite", StringComparison.OrdinalIgnoreCase) == true
            && File.Exists(Path.Combine(bundled, "IOTCAPIs.dll"))) full = Path.GetFullPath(bundled);
        if (!File.Exists(Path.Combine(full, "IOTCAPIs.dll")) || !File.Exists(Path.Combine(full, "AVAPIs.dll")))
            throw new CameraTransportException("The compatible Windows x64 camera SDK was not found.");
        try
        {
            iotcLibrary = NativeLibrary.Load(Path.Combine(full, "IOTCAPIs.dll"));
            avLibrary = NativeLibrary.Load(Path.Combine(full, "AVAPIs.dll"));
            var initialize = Export<InitializeDelegate>(iotcLibrary, "IOTC_Initialize2");
            var initializeAv = Export<OneIntDelegate>(avLibrary, "avInitialize");
            GetSessionId = Export<NoArgsDelegate>(iotcLibrary, "IOTC_Get_SessionID");
            AbortConnect = Export<OneIntDelegate>(iotcLibrary, "IOTC_Connect_Stop_BySID");
            CloseSession = Export<CloseDelegate>(iotcLibrary, "IOTC_Session_Close");
            Connect = Export<ConnectDelegate>(iotcLibrary, "IOTC_Connect_ByUID_Parallel");
            StartClient = Export<ClientStartDelegate>(avLibrary, "avClientStart2");
            StopClient = Export<CloseDelegate>(avLibrary, "avClientStop");
            SendControl = Export<SendControlDelegate>(avLibrary, "avSendIOCtrl");
            ReceiveControl = Export<ReceiveControlDelegate>(avLibrary, "avRecvIOCtrl");
            ReceiveFrame = Export<ReceiveFrameDelegate>(avLibrary, "avRecvFrameData2");
            ReceiveAudio = Export<ReceiveAudioDelegate>(avLibrary, "avRecvAudioData");
            LanSearch = Export<LanSearchDelegate>(iotcLibrary, "IOTC_Lan_Search2");
            StartServer = Export<ServerStartDelegate>(avLibrary, "avServStart2");
            StopServer = Export<CloseDelegate>(avLibrary, "avServStop");
            SendAudio = Export<SendAudioDelegate>(avLibrary, "avSendAudioData");
            ExitClient = Export<ExitChannelDelegate>(avLibrary, "avClientExit");
            ExitServer = Export<ExitChannelDelegate>(avLibrary, "avServExit");
            int initialized = initialize(0);
            if (initialized is not (0 or -3)) Check(initialized, "Camera SDK initialization failed.");
            Check(initializeAv(8), "Camera audio/video SDK initialization failed.");
            Loaded = true;
        }
        catch (BadImageFormatException) { throw new CameraTransportException("The camera SDK must contain compatible Windows x64 libraries."); }
        catch (DllNotFoundException) { throw new CameraTransportException("The compatible camera SDK could not be loaded."); }
        catch (EntryPointNotFoundException) { throw new CameraTransportException("The camera SDK does not expose the required legacy AV API."); }
    }

    internal static int OpenTalk(int sid, byte channel = 1) => StartServer(sid, AcceptAuthenticatedCamera, 10, 0, channel);
    // st_LanSearchInfo2 in this SDK: UID[21], IP[16], pad, ushort port,
    // DeviceName[129], reserved byte; natural alignment is 2, size 170.
    internal const int LanSearchStride = 170, LanSearchCapacity = 32;
    internal static List<DiscoveredDevice> Discover(int waitMs)
    {
        byte[] buffer = new byte[LanSearchStride * LanSearchCapacity];
        int found = LanSearch(buffer, LanSearchCapacity, waitMs);
        Check(found, "Searching the network for cameras failed.");
        // Unused entries stay zeroed and fail validation, as in the verified 0.3.0 search.
        return ParseLanSearch(buffer, LanSearchCapacity);
    }
    internal static List<DiscoveredDevice> ParseLanSearch(byte[] buffer, int count)
    {
        var result = new List<DiscoveredDevice>();
        for (int i = 0; i < count && (i + 1) * LanSearchStride <= buffer.Length; i++)
        {
            int offset = i * LanSearchStride;
            string Read(int start, int length) => System.Text.Encoding.ASCII.GetString(buffer, offset + start, length).Split('\0')[0];
            string uid = Read(0, 21), ip = Read(21, 16), name = Read(40, 129);
            if (!System.Text.RegularExpressions.Regex.IsMatch(uid, "^[A-Za-z0-9]{20}$") || !System.Net.IPAddress.TryParse(ip, out _)) continue;
            result.Add(new(uid, ip, name.Trim()));
        }
        return result;
    }
    internal static void Check(int result, string message)
    {
        if (result < 0) throw new CameraTransportException(message, result);
    }
}
