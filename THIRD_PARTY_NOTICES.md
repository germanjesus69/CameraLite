# Third-party components

CameraLite source is an independent implementation. The viewer package does not
contain camera APK binaries or Android app icons. The separate analysis/library
package contains decompiler pseudocode and REA evidence for the requested private
investigation; these are labelled separately from new application source.

The Windows package includes unmodified NuGet runtime files from:

- LibVLCSharp / LibVLCSharp.WinForms 3.10.2: LGPL-2.1-or-later.
  https://github.com/videolan/libvlcsharp/tree/3.10.2
- VideoLAN.LibVLC.Windows 3.0.24: LibVLC and VLC runtime plugins. Core LibVLC is
  LGPL-2.1-or-later; individual plugins and dependencies have their own licenses,
  including GPL components. License texts are included in `licenses`.
  Corresponding VideoLAN release sources:
  https://download.videolan.org/pub/videolan/vlc/3.0.24/
  https://www.videolan.org/legal.html
- NAudio 2.2.1: MIT. https://github.com/naudio/NAudio/tree/v2.2.1

- Microsoft .NET 10 runtime and Windows Desktop runtime (bundled in the
  self-contained portable package for the viewer and its worker): MIT. License
  and third-party notices are included in `licenses` as dotnet-runtime-* and
  windowsdesktop-runtime-*. https://github.com/dotnet/dotnet

Dynamic libraries remain separate and replaceable.
An existing FFmpeg installation is optionally invoked for experimental AAC talk;
FFmpeg is not bundled. This private user build includes the requested Windows
x64 IOTCAPIs.dll and AVAPIs.dll from the archived TUTK 14W42P1_RC3 SDK, with the
original headers/readme and provenance manifest. These proprietary SDK files
retain ThroughTek's copyright and their own applicable terms; CameraLite does
not grant a commercial SDK license or vendor service entitlement. The archive
source is https://github.com/nblavoie/wyzecam-api/tree/master/wyzecam-sdk .

Analysis-only tools (not part of the app): REA (MIT), Ghidra (Apache-2.0),
jadx-headless-mcp / JADX (Apache-2.0), and Eclipse Temurin JDK 21.
