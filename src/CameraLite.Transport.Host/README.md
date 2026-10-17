# CameraLite native transport worker

This companion process isolates the legacy vendor SDK from the viewer. It is a
framework-dependent Windows x64 executable targeting .NET 10, with no NuGet or
UI dependency. It references only the shared CameraLite.Transport protocol.

Build with:

```
dotnet build src/CameraLite.Transport.Host/CameraLite.Transport.Host.csproj -c Release
```

Publish its executable, DLL, runtimeconfig, dependency manifest and shared
CameraLite.Transport assembly alongside the viewer. The parent passes a random
pipe base and its PID. Credentials arrive through the current-user named pipe;
they never appear in arguments, worker output, or worker-created files. The
worker verifies that the process serving all three pipes matches the given PID.

One worker owns one native session. It creates one control reader and one reader
per media type. Commands are synchronized; matching-response queries are
serialized, with a four-second reply deadline. A query timeout ends the worker
to prevent a late reply from matching a later query of the same type. Video/audio pipes reuse their
native receive buffers and await writes directly, without an intermediate frame
queue. A pipe blocked for two seconds ends the worker so the parent can recover.
Up to 32 requests can be dispatched concurrently; close and talk-stop remain
available at that bound.

Talk uses the verified avServStart2 ABI and a rooted stdcall authentication
callback for the already authenticated viewing session. It arms the server,
waits 100 ms, requests camera channel 1 and pauses listening. Stop cancels the
pending native login before acquiring its gate, stops audio sends before closing
the server, sends SPEAKERSTOP and restores listening. Starting talk again is
always an explicit parent request.
Optional CAMERALITE_TRACE=1 emits
only constant stage names, timing and return codes for diagnostics, never account
details. The parent retains at most sixteen such lines; tracing is off by default.

Close cancels connection, readers, queries and talk before waiting. Native media
readers finish before their AV index is stopped. The command send lock also
guards index release, and the talk send gate guards server release. There is no
global SDK deinitialization while SDK reader threads are running. A hung native
call can still prevent cooperative cleanup; the parent must enforce its
two-second shutdown deadline and kill the worker if necessary. Merely calling
avClientExit/avServExit is not proof that a native call has returned.

Exit codes: 0 cooperative close; 2 bad arguments; 3 named-pipe parent mismatch;
4 request concurrency bound; 5 pipe/protocol failure; 10 parent exited; 11 native
media/control failure or blocked media write; 12 control response could not be
written. Windows fault dialogs are suppressed. There is no crash-test operation
in the production protocol.

Limits: vendor replies do not include request identifiers; a query timeout
therefore requires a new connection. The transport retains native error codes for connect/control/talk failures
and uses generic messages that omit credentials. Camera availability, SDK ABI
compatibility, and native fault recovery require the parent's integration tests
and hardware checks. Ordinary hardware talk passed; talk following an abrupt
worker death remains an unresolved case, even while video/audio recover.
