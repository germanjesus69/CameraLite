using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CameraLite.Transport;

namespace CameraLite.Transport.Host;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint serverProcessId);

    private static async Task<int> Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        if (args.Length != 2 || !int.TryParse(args[1], out int parentId) || parentId <= 0 || args[0].Length is < 1 or > 180) return 2;
        using var stop = new CancellationTokenSource();
        using var control = new NamedPipeClientStream(".", args[0] + "-control", PipeDirection.InOut, PipeOptions.Asynchronous);
        using var video = new NamedPipeClientStream(".", args[0] + "-video", PipeDirection.Out, PipeOptions.Asynchronous);
        using var audio = new NamedPipeClientStream(".", args[0] + "-audio", PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            using Process parent = Process.GetProcessById(parentId);
            _ = WatchParentAsync(parent, stop.Token);
            using var connectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await Task.WhenAll(control.ConnectAsync(connectionDeadline.Token), video.ConnectAsync(connectionDeadline.Token), audio.ConnectAsync(connectionDeadline.Token));
            if (!OwnedByParent(control, parentId) || !OwnedByParent(video, parentId) || !OwnedByParent(audio, parentId)) return 3;
            await using var session = new WorkerSession(video, audio, () => Environment.Exit(11));
            var responseGate = new SemaphoreSlim(1, 1);
            var requests = new HashSet<Task>();
            async Task RespondAsync(HostRequest request)
            {
                HostResponse response;
                try
                {
                    byte[] result = request.Operation switch
                    {
                        "connect" => await session.ConnectAsync(request.Connection ?? throw new InvalidDataException()),
                        "discover" => await session.DiscoverAsync(request.SdkFolder),
                        "control" => await session.ControlAsync(request.Type, request.Data, request.ExpectedResponse),
                        "talk-start" => await session.StartTalkAsync(),
                        "talk-stop" => await session.StopTalkAsync(),
                        "talk-audio" => await session.WriteTalkAudioAsync(request.Data, request.Info),
                        "close" => await session.CloseAsync(),
                        _ => throw new InvalidDataException()
                    };
                    response = new() { Id = request.Id, Success = true, Data = result };
                }
                catch (CameraTransportException ex) { response = new() { Id = request.Id, Error = ex.Message, Code = ex.NativeCode }; }
                catch (OperationCanceledException) { response = new() { Id = request.Id, Error = "The camera operation was canceled." }; }
                catch (TimeoutException) { response = new() { Id = request.Id, Error = "The camera did not respond in time." }; }
                catch { response = new() { Id = request.Id, Error = "The camera transport operation failed." }; }
                finally { request.Connection = null; }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await responseGate.WaitAsync(deadline.Token);
                    try { await Wire.WriteJsonAsync(control, response, deadline.Token); }
                    finally { responseGate.Release(); }
                }
                catch { Environment.Exit(12); }
            }
            while (true)
            {
                HostRequest request = await Wire.ReadJsonAsync<HostRequest>(control, stop.Token);
                requests.RemoveWhere(task => task.IsCompleted);
                if (requests.Count >= 32 && request.Operation is not ("close" or "talk-stop")) return 4;
                Task handling = RespondAsync(request);
                requests.Add(handling);
                if (request.Operation == "close") { await handling; break; }
            }
            stop.Cancel();
            return 0;
        }
        catch { stop.Cancel(); return 5; }
    }

    private static bool OwnedByParent(NamedPipeClientStream pipe, int parentId) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id) && id == (uint)parentId;

    private static async Task WatchParentAsync(Process parent, CancellationToken ct)
    {
        try { await parent.WaitForExitAsync(ct); if (!ct.IsCancellationRequested) Environment.Exit(10); }
        catch (OperationCanceledException) { }
        catch { if (!ct.IsCancellationRequested) Environment.Exit(10); }
    }
}
