# CameraLite 0.4.1 preview

Fixes from a full code review of 0.4.0. The portable package is now
self-contained (viewer and worker each include the .NET 10 runtime).

- Talk: the one-minute limit now always stops talk. Before, if opening talk
  took a second or more, the limit silently stopped sending while the
  microphone, the camera speaker and the "Microphone live" badge stayed on.
- Alt talk: any other key pressed with Alt (Alt+Tab, Alt+F4, AltGr, Alt+Space)
  ends or prevents talk, and Alt-started talk also stops if the Alt release is
  missed. Alt+M and Alt+Space no longer mute or pause.
- Camera audio: if audio can't play (no output device, unsupported codec), its
  frames are still drained. Before, the full audio pipe made the worker exit and
  video dropped every few seconds.
- Reconnect: a connection that stayed live for a minute gets all three retries
  again, so a viewer left open for days keeps reconnecting.
- A read-only preferences.json no longer stops the app from starting.
- Narrow windows: the bottom bar switches to a compact layout and the minimum
  window width follows its content, so Hold to talk never covers Pause or Snapshot.
- A saved password that this Windows account can't decrypt (for example after
  moving to another PC) now asks for re-entry instead of crashing Edit, and is
  not retried.
- Snapshots are reported as saved only when the file exists. Pause and
  snapshot capture run off the UI thread; F9 while paused copies the frozen frame.
- Changing quality during a reconnect keeps the chosen quality.
- ONVIF SOAP faults returned with HTTP 400/500 show the camera's reason, and
  credential faults show the username/password hint.
- Settings: the microphone hint now says Alt instead of Space.
- Find HDCameraPro cameras runs the TUTK LAN search in a short-lived worker
  instead of loading the native SDK into the viewer. Unused in-process SDK
  bindings were removed.
- The fullscreen overlay skips redraws while fully faded, moves without
  redrawing, and animates at half the frame rate.
- Tests: the talk ABI regression check now covers the worker's real binding.
  New checks cover saved-password failure, retry reset, ONVIF faults, LAN
  search parsing and worker-based discovery.

## Verification

Release build has zero warnings/errors. 17 app checks and 17 transport checks
pass. The fullscreen UI smoke test passes, and the bottom bar was checked at
the minimum window width with no overlap. These fixes have not yet been re-run
on the H10 camera.

# CameraLite 0.4.0 preview

Visual redesign of 0.3.0: sidebar camera cards, a fading video overlay with
fullscreen controls, Pause (Space) and hold-Alt talk. The camera connection,
transport worker, quality, sound and talk behaviour are unchanged from 0.3.0.

# CameraLite 0.3.0 preview

Known unresolved case: talk immediately following an abrupt worker crash can
time out even though viewing has recovered. Normal talk startup/stop succeeds.
The crash-recovery report records fullRecoveryPassed=false for this reason.

- Added CameraLite.Transport and an isolated native worker for live TUTK sessions.
  A native SDK crash or hang can be ended independently of the viewer. Automatic
  reconnection creates a fresh worker and leaves microphone talk off.
- Separate bounded pipes carry video, camera audio and control. Frames retain
  order and pooled memory, with explicit lost-frame markers and keyframe recovery.
- Quality changes require acknowledgement plus matching camera readback.
- Commands, talk and cleanup have bounded deadlines. Framed IPC writes finish
  safely through a caller cancellation; damaged messages invalidate the worker.
  Late native command replies cannot be reused after a query timeout.
- Talk clears a residual camera talk output before opening its new listener.
  The blocking control reader and talk listener use dedicated threads; startup
  waits for the listener thread to enter before the camera request. The short
  initialization delay remains a compatibility heuristic, not a ready event.
- Camera credentials stay out of worker arguments and logs. Current-user pipe
  access, server-process verification and parent monitoring contain each worker.
- Targeted REA/Ghidra decompilation of fifteen native routines is preserved in
  the companion library package, together with editable source and a findings report.
  TUTK's native transport engine remains a dependency; it was not fully rewritten.

## Verification

Release build has zero warnings/errors. 15 app checks and 14 transport checks pass.
Actual H10 video/audio and all three quality presets work; 40-second live pacing
measured 25 received fps with no reported lost frames or decoder backlog.
Repeated talk-channel open/close and quick release pass. The actual UI stays open,
replaces a deliberately terminated worker, reconnects and leaves talk off.
Opening talk again after that abrupt failure is not reliably recovered yet.
These checks do not open the PC microphone. Short synthetic silence packets
verify the outbound audio API; hearing your
PC microphone in the headset remains a manual check. Remote access was not tested.

The separate worker measured about 39 MiB working set; the viewer measured about
140–160 MiB during recovery. Process isolation trades that additional worker for
recoverable native failures. This is not a claim that every resource metric fell.

# CameraLite 0.2.1

Code review and optimisation update. Same features and controls as 0.2.0.

- Cleaner recovery from network loss: after a lost or incomplete camera
  frame, the app waits for the next keyframe instead of decoding frames that
  depend on missing data (smeared or grey picture). Streams also start on a
  keyframe. The status line shows a lost-frame count when any occur.
- Video frames reuse pooled memory instead of allocating a new buffer per
  frame, reducing garbage-collection work during long sessions.
- Fewer redraw allocations: fonts are reused, the microphone meter repaints
  only on change, and the audio tooltip updates only when the codec changes.
- Editing a camera no longer replaces a working custom vendor-SDK folder with
  the bundled one.
- Faster RTSP talk-reply parsing for ONVIF/direct cameras.

## Verification on this PC

- Release build: zero warnings/errors; 15 protocol/regression checks pass,
  including new keyframe-detection and pooled-frame checks.
- Actual H10: 1920×1080 H.264 decoded, G.711 μ-law audio received, all three
  quality commands acknowledged (HD 1920×1080; Standard and Low 640×360).
- 40-second sustained run: camera sends 24.5 frames/s, matching its declared
  25 fps; no frames queued behind the decoder and no lost frames on the LAN.
- Three talk-channel open/close checks and a quick-release check pass. No PC
  microphone was opened and no audio was transmitted.

# CameraLite 0.2.0

This update focuses on the supplied H10 camera's working video/audio connection,
the Space/microphone crash, and connection stability.

- Rebuilt native Windows interface with camera cards, clear connection state,
  actual decoded resolution, receive rate and session time.
- Automatic retries after connection failure, stream ending or a video stall;
  retries wait 2, 4 and 8 seconds and remain cancellable. Incorrect camera
  passwords stop retries. Reconnection never starts the microphone.
- Replaced the legacy SDK's unsafe null-string talk call with its callback API,
  with the callback retained for the lifetime of native use. The talk server
  starts before the camera is asked to connect, avoiding the Windows SDK's
  channel-reset race. Listening pauses during talk and resumes on release.
- Talk stops on release, lost focus, quality changes, disconnect, closing,
  microphone/encoder errors, or the one-minute limit. PCM is queued outside the
  microphone callback; camera/network writes cannot block that callback.
- Ordered, bounded compressed-frame buffers replace silent frame dropping.
  Decoder stalls produce an error for reconnection. Shutdown releases blocked
  stream readers and producers before disposing the player.
- Software decoding and Windows GDI video output avoid the graphics-driver
  crash path observed in Windows events. Hardware decoding remains optional.
- Camera volume, mute and microphone choice are saved. Playback buffer and
  reconnection preferences are configurable. PNG snapshots use F9.
- Existing saved cameras and Windows-protected passwords remain usable.

## Verification on this PC

- Release build: zero warnings/errors; 13 protocol/regression checks pass.
- Generated H.264 clip decoding, snapshot and mute pass.
- Fullscreen and window restoration pass.
- Actual H10: 1920×1080 H.264 decoded, G.711 μ-law audio received, all three
  quality commands acknowledged. Resolution stayed 1920×1080 during the short
  profile checks; these are camera encoder presets, not guaranteed resizing.
- Three consecutive native talk-channel open/close checks pass. Cancelling a
  talk start after 100 ms is handled without crashing.
- Actual UI: video remained live through the stall-monitor interval; three
  Space-key talk start/stop checks, automatic recovery without restarting talk,
disconnect and reconnect all pass.
- Approximately 85 MiB working set idle and 146 MiB during the live UI check.
  These are measurements on this PC, not maximum-memory guarantees.

The hardware checks open the camera's return-audio channel but intentionally do
not open a PC microphone or transmit audio. Hearing PC microphone audio in the
wireless headset remains the final human check. The user confirmed that phone
talk through HDCameraPro reaches the powered-on headset.

## Scope

One live camera at a time, viewing, camera sound, microphone talk, quality
presets, fullscreen and snapshots. The app does not implement camera SD-card
playback, Wi-Fi provisioning, motion alerts, PTZ or a cloud account service.
The included TUTK SDK is a legacy private-use dependency; see the third-party
notices and provenance manifest.
