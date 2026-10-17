# CameraLite.Transport 0.1.0 preview

Known hardware limitation: normal talk channels open and close, but talk after
an abruptly terminated worker can time out while video/audio recover. The
current build contains this failure; it does not fully recover that native
camera/SDK state. See the included hardware and recovery reports.

A focused .NET camera library for the HDCameraPro/TUTK camera used by CameraLite.
CameraLite 0.3.0 uses this library for live video, camera sound, quality changes,
and microphone talk. It has no VLC, NAudio, WinForms or NuGet dependencies.

The managed library and worker are new source. The worker still loads the
compatible Windows x64 IOTCAPIs.dll and AVAPIs.dll. This release does not replace
TUTK's proprietary UID rendezvous, NAT traversal, relay or packet engine. It is
not a binary-compatible replacement for those DLLs. Selected native routines
were decompiled to establish calling conventions, cancellation and frame behavior;
the pseudocode and full REA evidence are included under analysis in the library
package. No camera firmware was modified.

## Behavior

- One camera and native SDK instance per disposable worker process. A native
  access violation terminates the worker; it cannot directly corrupt the viewer's
  address space. CameraLite notices the closed stream and uses its reconnect policy.
- Separate pipes for control, video and camera audio. Ordered compressed frames
  use pooled memory. Media writes have a two-second stall limit, so a decoder
  that stops consuming cannot accumulate an unlimited playback buffer.
- Cancellation and deadlines bound connection, camera commands, talk and shutdown.
  Native command sends are serialized; a timed-out camera reply requires a fresh
  worker because the native protocol has no request identifiers.
- Quality requests require both an acknowledgement and a matching readback.
  The H10 uses presets 1, 3 and 5; actual picture dimensions come from its encoded
  stream, not from the preset name or capability-channel count.
- Talk uses a rooted callback and avServStart2, opens the listener before asking
  the camera to connect, pauses listening and restores it on release. Reconnection
  starts with talk closed. The app owns microphone capture/encoding and its hold
  controls; this library never opens a microphone itself.
  It clears residual camera talk state before a fresh start and uses dedicated
  threads for the blocking control reader and listener startup. The short startup
  delay is still a compatibility heuristic, not a documented native ready event.
- Random, current-user-only named pipes carry credentials in memory. Credentials
  are not placed in worker arguments or test reports. The worker verifies its
  pipe server process and exits if its parent dies.

Process isolation adds a small worker rather than reducing every memory figure:
about 39 MiB worker working set was observed on this PC. The viewer itself was
about 140–160 MiB in live recovery checks. These are separate process measurements,
not a maximum or an apples-to-apples performance guarantee.

## Using it

Reference CameraLite.Transport.csproj or lib/net10.0/CameraLite.Transport.dll.
Place the complete worker output in a host folder. Windows x64 and .NET 10 are
required for the native worker; CameraLite also requires the Desktop runtime.

Call CameraSession.OpenAsync with a CameraConnection (UID, Username, Password,
SdkFolder) and the full path of CameraLite.Transport.Host.exe. Read video and
audio concurrently for the lifetime of the session. ReadVideoAsync returns a
packet kind and an IDisposable MediaFrame; ReadAudioAsync returns a MediaFrame.
Consume or copy Data before disposing each frame. Only one reader may consume
each media stream. Video kind 3 reports a lost/incomplete compressed frame;
wait for the next codec keyframe before resuming decoding.
CameraConnection.TalkChannel defaults to 1 and accepts channels 1..31; select a
different value only when that camera supports it. This H10's advertised channel
2 was also verified to open its return-audio connection.

SetQualityAsync accepts documented quality values 1..5. StartTalkAsync opens
the camera's return-audio channel. WriteTalkAudioAsync sends encoded frames with
the SDK's 16-byte frame metadata. The measured H10 uses 8 kHz mono G.711 μ-law
(codec 137, 160 bytes per 20 ms, metadata byte 2 = 2). StopTalkAsync closes talk.
Always dispose the session. ControlAsync is the low-level escape hatch for known
camera commands, with a maximum 1020-byte payload enforced from this SDK's code.

The separate CameraLite source ZIP contains the application adapter in
src/CameraLite/Tutk.cs for decoding, hold-to-talk, mute and volume integration.
See src/CameraLite.Transport.Host/README.md for worker protocol.

## Build and verify

    dotnet build src/CameraLite.Transport.Host/CameraLite.Transport.Host.csproj -c Release
    dotnet run --project src/CameraLite.Transport.Checks/CameraLite.Transport.Checks.csproj -c Release

The checks use an isolated fake worker and synthetic packets. Real-camera checks
in CameraLite verify video/audio, presets, repeated talk-channel startup, quick
release and video recovery after worker termination; talk recovery still fails
in the documented forced-crash case. Checks do not open a PC microphone and
transmit only brief synthetic silence packets. Remote Internet operation and a full night-long run were
not tested. The original vendor SDK's service compatibility and license apply.
