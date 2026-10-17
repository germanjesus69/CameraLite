# Transport checks

Run on Windows x64 with .NET 10:

```
dotnet run --project src/CameraLite.Transport.Checks -c Release
```

The checks executable launches itself as a fake companion worker. It does not
load vendor libraries, use saved camera credentials, capture microphone input,
or contact a camera. Intentional exit and stall behavior exists only in this
checks companion, never in CameraLite.Transport.Host.

The suite covers malformed/oversized/truncated wire data, media cancellation,
pooled-frame disposal, native error-code preservation and the SDK's 1020-byte
control payload limit. Process checks verify that an abrupt worker exit leaves
the parent alive, malformed responses invalidate promptly, late replies do not
complete a newer request, canceled talk triggers stop, and disposal kills a hung
worker and releases pending callers within its deadline.

A backpressure case caps itself at 160 control requests of at most 1020 bytes
while the fake worker pauses reads for 500 ms. Caller cancellation expires while
the pipe is full, then a subsequent command verifies that JSON message framing
remains usable. Completing a frame already begun is necessary for this case;
canceling a partly written frame would corrupt the following message.

Latest run: 17 passed, 0 failed. The malformed-response regression was observed
failing before the client caught InvalidDataException and invalidated its pipes;
it passes after that fix. This suite verifies managed transport boundaries and
process isolation behavior, the worker's talk-server binding and LAN search
parsing (compiled from the worker source, without loading vendor libraries) and
worker-based discovery; real SDK behaviour, stream decoding and headset operation
are covered by separate hardware checks.
