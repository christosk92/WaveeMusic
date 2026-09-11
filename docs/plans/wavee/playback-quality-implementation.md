# Playback quality implementation

Approved design: 2026-09-06. This document records the implementation contract and verification checklist for the
playback work across Wavee and its sibling FluentGpu checkout. It does not assert that unchecked gates have passed.

## Behavior

Playback intent survives asynchronous loading, seeking, preparation and device recovery. A control command must not
wait behind network or codec work. Prepared audio contains the actual decoded PCM ring that playback will consume.
Ordinary track changes preserve the endpoint. Fades protect transport operations even when optional crossfade is off.

| Operation | Behavior |
| --- | --- |
| Pause/resume | 20 ms out/in, drain then stop, preserve endpoint and buffered source position |
| Manual next/previous | Fade old audio out over 50 ms promptly; promote prepared target or buffer; 50 ms in |
| Seek | 5 ms out/in; stop/reset device, invalidate stale work, acknowledge actual achieved position |
| Scrub | Audible local previews at most once per 100 ms, latest target wins, accurate commit on release |
| Paused navigation | Select/prepare the target without starting audio |
| Natural album boundary | Gapless, using decoded/trimmed frame lengths |
| Optional crossfade | Equal-power overlap for eligible music; never start an unready incoming voice |
| Device change | Park output with acknowledgement; negotiate actual format and rebuild before resuming latest intent |

Manual Next bypasses repeat-one; natural completion respects it. Previous retains the three-second restart rule.
Same known album with shuffle off suppresses overlap. Missing album/timing metadata and spoken/live sources use no
overlap. Crossfade settings retain the existing off default, stored 5 s duration, 0–12 s range and 0.5 s step.
Remote Connect scrubbing commits on release because Wavee does not control that device's audio pipeline.

## Ownership

```text
UI / media keys / local Connect commands
                 |
PlaybackController: selection, queue, intent, item generation
                 | short serialized mutations; async work outside lock
                 v
FluentMediaAudioHost: one control reader, operation lifecycle
        |                              |
        v                              v
Source/decoder workers            VoiceScheduler
owned PCM rings                   immutable frame plans
        |                              |
        +--------------+---------------+
                       v
Output thread: controls -> capacity -> bounded render -> submit
                       |
                       v
                     WASAPI

Output acknowledgements + off-thread played clock
        -> host -> projection -> bridge signals -> UI
```

Network/decoder completions only post messages; they cannot restore captured play intent. The host's observation
timer cannot independently mutate the scheduler. Audio rendering performs no allocation, locks, synchronous I/O,
resource disposal or device waiting. Output lifecycle calls serialize with output submission.

Identity domains are distinct: item generation, operation sequence, latest seek revision, negotiated format
generation and prepared transition token. A pause does not invalidate a valid next-track preparation.

## Contracts and UI ownership

The transport contract is engine-independent (`Wavee.Core`); UI and host implementations use the same types:

```csharp
public readonly record struct PlaybackCommandId(long ItemGeneration, long Sequence);
public readonly record struct PlaybackCommandReceipt(PlaybackCommandId Id);
public readonly record struct PlaybackSeekRequest(long PositionMs, SeekMode Mode, PlaybackSeekKind Kind);
public readonly record struct PlaybackSeekState(
    PlaybackCommandId Id, long TargetMs, long? ActualPositionMs, PlaybackOperationStatus Status);
public readonly record struct PlaybackTransportState(
    bool PlayWhenReady, PlaybackPhase Phase, bool OutputAdvancing, PlaybackSeekState? Seek)
{
    public long ItemGeneration { get; init; }
    public long? PositionUpperBoundMs { get; init; }
}
```

`IPlaybackState.Transport` is required. Seek requests return an acceptance receipt; the matching observable operation
reports Applied, Superseded or Failed. An unrelated command's higher sequence never acknowledges a seek. The UI
keeps a separate requested target and never rewrites actual playback position as proof that a seek completed.

The primary button reads `PlayWhenReady`; progress reads acknowledged output. Loading never disables pause or a
valid next/previous action. During a scrub the pointer owns the displayed target. Release holds that target until
the matching committed seek finishes. Escape restores the gesture's original position; a track change cancels the
gesture without seeking the new track. DVR targets clamp to the latest valid window.

```text
PlayerBar
+-- TrackIdentity: selected track / artwork signals
+-- TransportControls: intent glyph + source/queue capabilities
+-- SeekBar: actual position / gesture target / matching acknowledgement / live rail
+-- PlaybackActivity: delayed phase + intent visibility
+-- Existing queue, volume and device controls
```

```text
+--------------------------------------------------------------------------------+
| [ART] Track title             Shuffle  |<  [ || ]  >|  Repeat       Queue  Vol |
|       Artist                                                                  |
|                          1:24  ========o-------------------------  4:08         |
+--------------------------------------------------------------------------------+

+--------------------------------------------------------------------------------+
| [ART] Selected next track     Shuffle  |<  [ || ]  >|  Repeat       Queue  Vol |
|       Artist                                                                  |
|                          0:00  ----------------------------------  3:42         |
|                                Loading...                                     |
+--------------------------------------------------------------------------------+

+--------------------------------------------------------------------------------+
| [ART] Track title             Shuffle  |<  [ || ]  >|  Repeat       Queue  Vol |
|       Artist                                    +--------+                    |
|                                                 |  2:17  |                    |
|                                                 +---+----+                    |
|                          2:17  =====================o------------  4:08         |
+--------------------------------------------------------------------------------+

+---------------------------------------------------------------+
| Playback                                                      |
| Crossfade songs                                    [ ON ]     |
| Duration     0 s -----------o---------------- 12 s   [ 5.0 s ]  |
| Tracks from the same album play without overlap               |
| when shuffle is off.                                          |
+---------------------------------------------------------------+
```

Use current responsive tiers, FluentGpu house controls and motion tokens. Thumb movement remains a transform bind;
changing state flows through signals/context, never a frozen component constructor value. Activity is delayed by the
existing fast control-motion duration and hidden when play intent is withdrawn.

The playback diagnostics page exposes a refreshable audio snapshot and includes it in Copy diagnostics:

```text
+-----------------------------------------------------------------------+
| Audio output                                              [ Refresh ] |
| Transport                 Playing / play requested                    |
| Device format             48,000 Hz / 2 channels                       |
| Device padding            80 ms                                       |
| Current PCM               500 ms                                      |
| Prepared next             500 ms / ready                               |
| Rendered / submitted / played frames    148800 / 148320 / 144480        |
| Render epoch / unexpected underruns     1 / 0                          |
| Last command / application latency      3:12 / 118.4 ms                 |
|                                                   [ Copy diagnostics ]|
+-----------------------------------------------------------------------+
```

Numbers above illustrate the fields, not measured performance. The actual page retains its existing runtime,
module and update sections. Position ticks refresh the submitted-frame bound; interpolation never relies on an
old capacity snapshot. Item generation and playback ownership changes invalidate pending seek UI, including a
new queue item that repeats the same URI.

## PCM preparation, output and timing

PCM rings hold 1,000 ms, refill toward 500 ms below a 250 ms watermark. Startup/seek readiness is the greater of
100 ms or device capacity plus two blocks, capped at the ring's configured refill target (normally 500 ms). This cap
avoids waiting for a depth the producer does not fill on unusually large device buffers. Prepared next requires
500 ms and successful body attachment for
Spotify. Confirmed EOF with a nonempty shorter tail satisfies readiness. No timeout admits an empty ring to Playing.

The host receives `FastStartPlan` as one operation: body attachment proceeds concurrently with decoder opening and
ring filling. A prepared lease transfers decoder, ring, producer, DSP and format identity to the mixer without
constructing a new empty ring. There is one producer per source, bounded to two audible voices plus one preparation.

Read-stream misses are not EOF. The cancellation-aware reader uses an availability version to close the lost-wake
race; body/fetch completion, cancellation and permanent failure wake the wait:

```csharp
while (true)
{
    cancellationToken.ThrowIfCancellationRequested();
    long version = source.DataVersion;
    int count = source.TryRead(destination, out bool wouldBlock);
    if (count > 0 || !wouldBlock) return count;
    source.WaitForData(version, cancellationToken);
}
```

Structural mixer commands are acknowledged and never silently dropped. Continuous parameters and pending seek
targets coalesce. Retire resources only after render detachment and producer quiescence acknowledgements. A stuck
worker is diagnosed/quarantined; never dispose underneath it or create unbounded replacement workers.

Output first submits retained partial PCM, inspects writable capacity, renders at most a 10 ms block, and retains
any unaccepted remainder. Rendered, submitted and played frames are separate. A successful device release advances
submitted frames; the off-thread device clock advances played frames, bounded by submitted content. Silence caused
by starvation does not silently consume track duration.

Pause drains its fade tail then stops without reset. Seek stops and resets to remove old queued audio, then installs
fresh decoder/ring state at an acknowledged position. Transport attenuation is after master DSP and constrained to
[0,1]. Use per-frame smoothstep interpolation, retargeting from the current gain.

`VoiceScheduler` owns joins: known outgoing length L, start S, overlap F gives incoming start S+L-F; gapless uses
S+L. Unknown length joins on confirmed producer EOF/final ring frame. Split output blocks at joins. A late crossfade
preparation degrades to gapless; a late EOF preparation buffers. Atomic transition commit/cancel and token matching
prevent a manual skip racing a natural boundary from advancing twice. Codec trimming happens exactly once.

Manual Next also reuses a ring already installed for a future natural transition. The transition gate stays alive
until the outgoing fade drains and `PcmAudioSession.PromoteScheduledVoiceAsync` acknowledges adoption. The operation
retains that exact ring and returns its consumed position if the natural boundary crossed during the fade, so those
samples are not replayed. The host suppresses a second natural Started notification for the manually selected item.

Recovery parks output, captures played position/latest intent, opens a replacement endpoint off RT and uses its
actual format. Old-format preparation is invalidated before resuming. Ordinary navigation does not reopen WASAPI.
Finite seekable sources restore their achieved position. An endless radio stream reconnects at its live edge and
preserves the latest play/pause intent; it continues to reject ordinary seeks without interrupting output.

## Verification

Use a finite buffered test endpoint with an independent hardware clock, partial writes, pause freeze, reset flush,
device invalidation and captured PCM. Production-host integration tests inject the backend and never launch the app
or open a real endpoint. Test real behavior rather than source text.

- [x] Empty/delayed/short-EOF startup and prepared-ring ownership transfer.
- [x] Controls stay responsive during blocked preparation/body fetch.
- [x] Pause/resume tails, rapid reversals, paused loads/seeks/navigation.
- [x] Repeated Next, repeat-one semantics, history and boundary race deduplication.
- [x] Seek flush, actual-position acknowledgement, supersession, failure and seek-target identity cancellation.
- [x] Engine PCM gapless joins, trimming and crossfade curve regression suite.
- [x] Partial output writes preserve every sample once and in order.
- [x] Source cancellation and acknowledged resource retirement.
- [x] Simulated device replacement preserves position/intent; format negotiation covers 44.1/48/96 kHz.
- [x] Existing video/module/live/DVR tests and remote seek authority tests.
- [ ] Interactive scrub/tooltip/Escape pass and sustained real-device audio allocation/clipping measurements.
- [x] Debug and Release builds: public Wavee and FluentGpu, in isolated verification worktrees.
- [x] Full Wavee.Tests, playback integration tests, engine and Windows tests.
- [ ] Fully green VerticalSlice: one pre-existing toast edge-inset failure, reproduced on a clean baseline.
- [x] Release Pester tests; check-canon for updated engine contract docs.
- [ ] User-run listening pass: sustained tones, live albums, loud overlaps, slow network, device changes.

Verification uses isolated public-source worktrees in `C:\wavee\playback-verification`, based on Wavee
`30e5e7579554b7abaf24ae22fa914f7fd5d37b18` and FluentGpu `1f906c906`. The shared Wavee checkout also contains an
unrelated, in-progress catalog/library migration whose compilation errors prevent using it as a playback-only gate.
Only the explicit playback file manifests and selective composition, test-project and queue edits are applied to
the verification trees. The catalog work later overlapped `PlaybackSession`, `PlaybackController` and
`PlaybackProjection` and the bridge's catalog subscriptions; verification retains their playback-only versions while the original files preserve both
tasks' edits. All implementation edits remain in the original sibling checkouts.

Verification results (2026-09-06):

| Gate | Result |
| --- | --- |
| Wavee.Tests | 7,732 passed, 1 existing skip |
| FluentGpu.Engine.Tests | 221 passed, serialized test collections |
| FluentGpu.Windows.Tests | 166 passed, including headless WASAPI format/clock tests |
| Playback integration | 23 passed, including real MP3/FLAC decoding, partial-input startup and live device recovery |
| Full VerticalSlice | 1,290 passed, 1 unrelated toast edge-inset failure |
| Clean engine baseline, overlay suite | Same toast edge-inset failure at `1f906c906` |
| Release Pester | 304 passed, 1 existing skip |
| Debug/Release builds | Wavee and FluentGpu both passed with final code; zero errors, test/harness analyzer warnings remain |
| Engine canon check | Passed, 33 documents |

The toast failure reports `top@0=408 top@120=408 lift=0` in both the clean baseline and modified engine. Its control
and gate files are untouched. Serializing the engine test collections avoids an unrelated global UI dispatcher
timing failure seen under parallel test load; the full suite still runs. Hardware listening, Bluetooth behavior and
the latency targets below remain unmeasured. FLAC accurate seeks currently decode/discard to the requested frame,
which is correct but can be slower for long files; no new seek index or resampler is introduced here.

Ordinary MP3 decoding uses a borrowed forward-only codec view to prevent NLayer's unknown-duration scan from
fetching an entire untagged file before the first PCM block. The outer source retains its seek capability. An
explicit seek opens the indexed decoder view; indexing an untagged remote file can still require additional I/O.
Device padding in diagnostics is a cached estimate from submitted-minus-played frames, so refreshing the page
never calls into an endpoint that is being retired.

`C:\wavee\playback-verification\playback-manifest.json` records the exact file sets and selective overlaps.
Build logs, test logs and the clean-baseline comparison are retained beside it; TRX results are in each verification
test project's `TestResults` folder. The final 66 whole-file Wavee copies and 27 engine copies are checked against
their original-source hashes; the six overlapping Wavee files are handled selectively as described above.

Real codec fixtures exposed two MP3 defects: the adapter passed frame/sample units to NLayer's byte-position API,
and pinned NLayer 1.15 miscomputed MPEG-2/2.5 Layer III frame sizes, skipping frames. The same decoder's official
3.0 release contains the frame-size correction and the forward-only read fix needed by live radio. This is a public
dependency update, accompanied by decoded-audio and encoder-trim coverage, rather than a new decoder implementation.

Warm local wired/shared-mode targets: p95 pause/resume <150 ms, cached committed seek <250 ms. These are measured
acceptance targets, not promises about Bluetooth acoustic latency. Log acceptance/application/played-ack separately,
PCM depth, padding, preparation state, operation/token/format IDs and unexpected underruns vs planned silence. Use
`Stopwatch.GetElapsedTime` for Stopwatch timestamps. A GC interval is correlation, not a claimed cause.

## Research basis

- [Apple Music album crossfade behavior](https://support.apple.com/en-ie/guide/music-windows/muse5e9ec085/windows).
- [Spotify gapless, crossfade and Automix distinctions](https://support.spotify.com/us/article/tracks-transitions/).
- [Media3 intent and readiness separation](https://developer.android.com/media/media3/exoplayer/listening-to-player-events).
- [WASAPI reset contract](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-reset).
- [mpv WASAPI output](https://raw.githubusercontent.com/mpv-player/mpv/master/audio/out/ao_wasapi.c).
- [Real-time callback discipline](https://portaudio.com/docs/v19-doxydocs/writing_a_callback.html).
- [NLayer position and time units](https://github.com/naudio/NLayer/blob/master/NLayer/MpegFile.cs): its seek
  position is decoded PCM bytes; codec adapters must convert both requested and achieved frame positions.
- [NLayer 3.0 frame-size correction](https://github.com/naudio/NLayer/blob/v3.0.0/NLayer/Decoder/MpegFrame.cs) and
  [official release notes](https://github.com/naudio/NLayer/blob/master/RELEASE_NOTES.md), including the forward-only
  read regression in 2.0.1 and its correction in 3.0.

Transport fade defaults are Wavee choices. Beat matching, private runtime changes, codec replacement and a separate
normalization/resampler redesign are outside this change. The related endpoint/timeline regression is tracked in
[issue #65](https://github.com/christosk92/WaveeMusic/issues/65).
