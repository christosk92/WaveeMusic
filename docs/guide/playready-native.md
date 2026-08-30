# PlayReady native integration

Protected video uses an in-process Win32 native DLL plus managed wrappers in the Windows API layer.

## Native DLL

Build and output layout: [`ops/tools/playready-native/README.md`](../../ops/tools/playready-native/README.md).

```cmd
cd ops\tools\playready-native
build.cmd arm64
```

Produces `out/{arch}/FluentGpu.PlayReady.Native.dll`.

## Managed code

| Area | Path |
|---|---|
| PlayReady seam | `src/FluentGpu.WindowsApi/Media/PlayReady/` |
| Gallery harness | `src/FluentGpu.WindowsApp/` (protected-video test entry points) |

Design context: [`docs/design/subsystems/media-pipeline.md`](../design/subsystems/media-pipeline.md) (DRM / protected playback section).

## Adaptive protected video

The protected source descriptor may carry a catalog of stable video representation IDs. The desktop ABI keeps the
original snapshot export intact and adds a size-guarded V2 snapshot with downloaded bytes, cumulative transfer time,
forward buffer duration, the active representation, and a representation-apply sequence. Older native DLLs continue to
play through the legacy ABI; the quality selector is hidden when V2 is unavailable.

`Auto` starts from the manifest's conservative representation, estimates throughput from completed downloads, and
switches only at a media-segment keyframe boundary. Downshifts are immediate; upgrades require buffer headroom and two
consecutive votes. Auto is capped by the rendered viewport height and, on metered connections, the app preference.
Selecting a resolution is a manual pin and deliberately overrides those Auto caps.

Only manifest-declared tracks and compatible representations are published. For Spotify this currently means H.264/MP4
video and AAC/MP4 audio; Opus/WebM profiles are rejected by the protected Media Foundation lane. Alternate-track menus
are shown only when the manifest contains more than one real program and the native backend reports track-switch support.

### The switch-boundary rule

A representation switch splices the target segment into the timeline **at its own presentation time**, at a segment
boundary that is **at or after the next undelivered sample and never forward of the feeder cursor**. Everything between
the playhead and the splice point stays in the old representation, so the timeline is contiguous and monotonic across a
switch; only the buffer past the splice is replaced. The stream buffer is spliced by time on **every** feeder append
(not just a switch), because the feeder also rewinds its cursor on a seek — appending a rewound segment at the tail is
what left the sample vector duplicated and non-monotonic. History behind the playhead is trimmed to a bounded window,
which is also the window a backward scrub can be served from without a refetch.

The boundary must never be computed forward of the playhead. It previously clamped up to the initial-burst segment count
— a constant captured before playback began — so an early switch replaced the buffer under the playhead with segments
seconds ahead: video went to a hole while audio and the clock ran on, and the picture froze on the last decoded frame.

### Seek ABI

| Export | Meaning |
|---|---|
| `FgPlayReadySeekEx(int64 positionMs, int32 mode)` | `mode` 0 = exact (`SetCurrentTime`), 1 = approximate (`SetCurrentTimeEx` + `MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE`) |
| `FgPlayReadySeek(int64 positionMs)` | exact; `FgPlayReadySeekEx(positionMs, 0)` |

Both return a request sequence that appears in the snapshot's `seekAppliedSeq` once the owning MTA thread has applied
it. Use approximate while a scrub thumb is moving (nearest keyframe, no preroll decode) and exact on the commit.

There is **one** seek slot, latest-wins. Seeks are deliberately never queued: Media Foundation applies queued seeks
FIFO, so a dragging thumb would leave the pipeline chasing positions the user has already left.

Three properties of the seek path are load-bearing:

* **Buffered targets never download.** Both the MTA loop (every 80 ms, against the *current* buffer) and the feeder
  check whether the target already lies in the buffered range of *both* tracks — video needs a keyframe at or before it,
  audio only coverage, both with contiguous coverage past it. If it does, the transport gate opens immediately and no
  fetch is issued.
* **An in-flight segment does not block a newer seek.** Segment fetches are polled while in flight and cancelled when a
  newer seek arrives or the session is torn down, instead of the feeder only noticing at the top of its loop.
* **The surface is never blanked on a seek, and a paused seek frame-steps.** In windowless swapchain mode
  `UpdateVideoStream(nullptr, nullptr, nullptr)` on the keep-alive tick repaints the latest frame, so the old picture
  simply holds. At rate 0 the MF video renderer does not pre-roll, so a seek issued while paused is followed by one
  `FrameStep(TRUE)` to force the new frame out.

### Download telemetry (media-segment-only)

The V2 snapshot's `bytesDownloaded` / `downloadElapsedMs` fields keep their names and offsets but accumulate **media
segments only, body transfer only**:

* init segments are excluded — they are small and RTT-dominated, and counting them depressed the throughput estimate at
  exactly the moment the ABR needed it (dash.js excludes them for the same reason);
* connect, TLS and time-to-first-byte are excluded — the request is issued with `HttpCompletionOption::ResponseHeadersRead`,
  so the measured interval is the response-body read alone;
* one `winrt::HttpClient` is shared by the whole session, so connections and TLS sessions are reused rather than
  re-handshaked per segment.

Every media-segment fetch writes one `[cenc-abr]` line to the always-written log
(`%LOCALAPPDATA%\FluentGpu\PlayReady\desktop-playready.log`) carrying track, representation index, segment index, bytes,
body transfer ms, header ms, derived kbps, and the stream's forward buffer in ms. Diagnostics here are never behind a
flag; the log line is the ledger the throughput estimate is built from.

The initial burst is 4 segments (~16 s at Spotify's 4 s segments). Two segments sat below the managed ABR's 12 s upgrade
gate, so a session could never accumulate the headroom that authorises an upshift.
