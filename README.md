# CameraLite 0.4.1 preview

0.4.1 fixes the issues found in a full code review of the 0.4.0 redesign; see
RELEASE_NOTES.md. The portable package is now self-contained: it runs on
Windows x64 without installing .NET.

Known limitation: after forcibly terminating a worker with talk open, video and
camera audio reconnect, but the next talk attempt can time out. Normal repeated
talk open/close works. This build contains the failure instead of closing the
viewer; it does not yet solve that camera/SDK recovery case.

A native Windows x64 camera viewer focused on stability and low resource use.
It uses C# / Windows Forms and LibVLC, with one live camera at a time.

Video, camera audio, quality and talk use the new CameraLite.Transport library.
Its native TUTK work runs in a separate process, so native crashes and stuck
calls can be recovered without closing the viewer. The existing compatible
TUTK DLLs still provide camera connectivity. See RELEASE_NOTES.md for checks.

## Open the app

Extract the zip and open CameraLite.exe. Keep the DLLs, libvlc, vendor-sdk and
transport folders beside it. Close any other CameraLite instance first. No .NET
installation is needed; the runtime is included.

Saved cameras/passwords from the previous version are reused automatically.
Select your camera, then Connect. No camera registration or cloud account is
required by the app; UID transport uses the TUTK vendor connection service.

## Layout

- Left: your cameras. + adds one; the ⋯ button or a right-click on a camera
  offers Connect, Edit and Delete; double-click connects.
- Centre: the video. When idle it shows one message and one action (Connect,
  Try again or Add camera).
- Over the video: camera name, LIVE and the stream details (resolution, codec,
  fps, bitrate, time) appear when you move the mouse and fade after ~2.5 s.
  App messages appear briefly as a small toast instead of a status bar.
- Bottom bar: Connect, Quality, sound and volume, Hold to talk, Snapshot and
  Fullscreen. Hover any icon for its keyboard shortcut.

## Controls

- Quality: HD, Standard or Low bandwidth for this H10. The app waits for a
  camera acknowledgement and reports an unconfirmed request honestly.
  Actual resolution is shown in the video overlay. Measured on this H10: HD is
  1920×1080; Standard and Low bandwidth are both 640×360. HD is the same
  camera setting that HDCameraPro labels "4k". No higher live resolution was
  found among the tested quality values and channels.
- Sound / mute: toggle camera audio; M is the keyboard shortcut.
- Volume: adjust camera sound from 0–100% (the mouse wheel works on the slider
  and over the video). Mute and volume are remembered.
- Hold to talk / hold Alt: hold to send your PC microphone to the
  camera/headset; release to stop. Talk starts after Alt alone has been held
  for a moment. Pressing another key with Alt (Alt+Tab, Alt+F4, AltGr) is a
  shortcut: it never starts talk and ends talk that Alt started.
  Power on the headset first. Listening pauses during talk and a red
  "Microphone live" badge shows over the video.
  Switching to another app, changing quality, disconnecting, closing or
  the one-minute limit also stops talk. No microphone recordings are saved.
- Pause / Space: freeze the picture; press Space again to return to live view.
  The camera stream and its sound keep running underneath, so pausing never
  drops the connection. A "Paused" badge stays on the video while frozen.
- Snapshot / F9: save a PNG to Pictures\CameraLite by default (while paused,
  the frozen frame is saved). Snapshots in the sidebar opens that folder.
- Fullscreen / F11 / double-click the video: the video fills the screen.
  Moving the mouse shows floating controls (sound, volume, talk, pause,
  snapshot, quality, exit); they and the cursor hide when idle. Esc returns to
  the window.
- Settings: reconnect automatically, optional hardware decoding, playback
  buffer, microphone and snapshot folder. Decoder/buffer changes apply on the
  next connection. Software decoding is the stable default on this PC.

Temporary connection failures, stream endings or detected video stalls trigger
up to three automatic retries after 2, 4 and 8 seconds. A connection that stayed
live for a minute gets all three retries again for its next outage.
Cancel/Disconnect stops retries. The microphone never starts automatically when
reconnecting.

## Add another camera

For the H10, select HDCameraPro camera UID (TUTK), use Find cameras while both
PC and camera are on the same router, or enter the complete 20-character UID.
Enter the camera username and password in the app. Connection libraries are
included; Advanced settings allows a compatible custom Windows x64 SDK folder.

ONVIF cameras can be discovered on the local router or added by IP / ONVIF
URL. Saving authenticates and loads the camera's available profiles.
Direct streams accept RTSP, RTSPS, HTTP or HTTPS URLs. An optional second
stream URL provides another quality profile. Keep passwords in the dedicated
password field rather than in a URL.

ONVIF/direct talk requires the camera's RTSP audio backchannel and G.711
PCMU/PCMA at 8 kHz. UID talk supports the camera's G.711/PCM/AAC format.
AAC talk needs an existing ffmpeg.exe on PATH or C:\FFmpeg\bin; this H10 uses
G.711 and does not need FFmpeg for talk.

Remote UID access depends on the legacy TUTK SDK/service and network conditions.
Direct IP/ONVIF access requires network reachability, such as a VPN to the
camera network. Remote operation was not verified in this update.

## Local data

Cameras live in %LOCALAPPDATA%\CameraLite\cameras.json. Passwords use Windows
DPAPI for the current user. Names, addresses and UIDs are ordinary local text.
Preferences are in preferences.json in the same folder. No password or UID is
included in the release's test reports or screenshots.

## Verification

17 application protocol/regression tests and 17 transport checks pass. Actual
camera checks cover video/audio, acknowledged and read-back quality, repeated
talk-channel startup, quick release, and viewer recovery after killing its worker.
Talk after that forced crash remains an explicitly failing recovery check.
PC microphone capture and headset audibility must still be checked by holding
Alt in the app. Hardware checks never open the PC microphone; the talk/recovery
checks send only short synthetic silence packets to verify the outbound API.
The camera checks above were run on 0.4.0; the 0.4.1 fixes pass the automated
checks but have not yet been re-run on the camera.

Viewer working set measured around 140–160 MiB during live recovery checks; the
worker measured around 39 MiB separately. These are observations on this PC.
The portable package includes the x64 VLC runtime and the .NET 10 runtime,
for both the viewer and its worker.

## Build

    dotnet build src/CameraLite/CameraLite.csproj -c Release
    dotnet src/CameraLite/bin/Release/net10.0-windows/CameraLite.dll --self-test
    dotnet run --project src/CameraLite.Transport.Checks -c Release
    powershell -ExecutionPolicy Bypass -File package-portable.ps1

The last command writes the self-contained package and its zip to dist\. It
includes vendor-sdk\ when that folder is present beside the script; the TUTK
SDK binaries are not part of this repository.

Tests use synthetic local ONVIF/RTSP services and silent PCM fixtures; they do
not access a physical microphone. The --camera-check, --talk-check and
--session-ui-check maintenance commands access the saved camera and its talk
channel. The latter two send short synthetic silence packets; none opens a PC
microphone or sends captured speech.

See THIRD_PARTY_NOTICES.md for dependency licensing/provenance. APK analysis
notes are kept outside this repository. The private build includes legacy TUTK
IOTC 1.13.7.0 and AV 1.5.6.0 libraries from the pinned 14W42P1_RC3 SDK archive.
