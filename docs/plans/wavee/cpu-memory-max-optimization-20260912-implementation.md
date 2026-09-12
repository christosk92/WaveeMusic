# Maximum CPU and memory optimization — implementation plan

Date: 2026-09-12. Status: first implementation batch verified; later phases remain planned.
Scope: public Wavee and its sibling FluentGpu checkout. Author: final GPT-6-Astra high review requested by the user.

### Implementation receipt (2026-09-12)

- Implemented identity gain/channel/transport skips, visible-meter demand with coherent RT-to-control handoff,
  event-only audio management, and sparse copied snapshot measurements. Audio buffering, limiter and lyric cadence
  are unchanged. Meter demand follows activation and host replacement through an auto-tracked effect.
- Added the cached-detail live-ready retry: an incomplete route opened before authentication retries its normal
  catalog load once the backend is ready, without discarding its cached content or fetching on every store change.
- User-requested SIMD: settled non-unity `GainStage` uses portable `Vector128<float>`; identity is still skipped,
  ramps are unchanged, partial aliases retain scalar ordering. ARM64 NativeAOT benchmark (dynamic code false,
  hardware acceleration true), six alternating A/B rounds, 16 million samples per round: 480-frame stereo median
  scalar/vector ratio **3.403 separate buffers, 2.135 in-place**. All PCM comparisons passed; these cases allocated
  zero bytes. Unity control ratio 1.038. This is a stage microbenchmark, NOT an aggregate Wavee CPU improvement.
  Reproduction: sibling `.tmp-simd-bench/GainBench.csproj`; raw ignored results `.tmp-simd-bench/results.csv`.
- Best next SIMD candidates are constant ungated mixer envelopes and non-neutral stereo channel gain. Recursive
  EQ/limiter state, resampler phase history, dependent layout traversal and object-based syllable scanning require
  different treatment. Do not vectorize across these dependencies or silently introduce FMA/reassociation.
  Guidance: [Microsoft SIMD and intrinsics](https://learn.microsoft.com/en-us/dotnet/standard/simd).
- Final verification: **1536 VerticalSlice checks passed in Debug and Release**, **362 engine tests passed in
  each configuration**, **227 Windows tests passed**, **8040 app tests passed / one skipped**, and **330 Pester
  tests passed / one skipped**. App and engine Debug/Release builds passed; existing warnings remain.
  Earlier full-suite zero-allocation checks intermittently failed (For and touch/pinch); isolated suites and
  final rebuilt full suites passed without relaxing those assertions. Their intermittent cause is not established.
- Published and launched native ARM64 Release, PID 12872. Populated PURE album shows 13 tracks. Ten-second
  artwork capture: 183 samples, zero changes. This verifies that settled cover, not every track transition or
  the original cold-start cache race. No audio underruns appeared in the playback/load logs.

Native observations below use one active process 3D counter instance, 12 logical CPUs, and no simultaneous
builds. Working set is MiB, not a retained-heap measurement. These workloads are not matched before/after trials.

| Workload | Samples | CPU | CPU-ms/s | GPU mean | Working set MiB |
|---|---:|---:|---:|---:|---:|
| Playback, lyrics closed (visually verified) | 20 | 0.288% | 34.6 | 0.395% | 450.9 |
| Playback, lyrics visible | 20 | 1.138% | 136.5 | 7.819% | 451.0 |
| Lyrics plus oscillating scroll, 429 events in 20 seconds | 20 | 1.317% | 158.0 | 28.651% | 452.3 |
| Paused (verified before minimizing), minimized | 12 | 0.000% | 0.0 | 0.000% | 458.7 |

Receipts: app `.tmp-msbuild/batch-audio-only-verified.txt`, `batch-lyrics-verified.txt`,
`batch-lyrics-scroll.txt`, `batch-scroll-events.txt`, `batch-paused-minimized-verified.txt`, and
`batch-artwork.txt`. The earlier `batch-audio-only.txt` still had lyrics open, and
`batch-paused-minimized.txt` minimized before the pause deep link was handled: exclude their misleading labels.
Playback was resumed and the window restored after the final measurement. Zero measured CPU over 12 seconds
is not proof of literally no work. Neither aggregate memory savings nor maximum optimization is established:
roughly 451 MiB playback working set and scrolling GPU cost remain explicit follow-up work.

The remaining proposal below is not a claim that P4–P7 or adaptive image/glyph residency have shipped.

The first implementation batch should remove unused audio analysis and identity DSP passes, then eliminate
redundant management wakeups. The next batch should reduce retained snapshot memory and publication work while
keeping syllable lyrics at display cadence. Adaptive glyph and image residency follows, with actual allocated-byte
accounting and explicit protection against cache churn. These are concrete optimization opportunities even when
Task Manager rounds the process CPU percentage down.

This plan is deliberately more ambitious than accepting the present working set. It does not attach an invented
aggregate CPU percentage or working-set saving to unimplemented changes. Each phase has an observable work/byte
budget and a correctness gate; native measurements decide which subsequent optimization has the greatest return.

## 1. Baseline, evidence, and invariants

Read together with [the audit comparison](cpu-audit-comparison-20260912.md) and
[the completed idle/render work](idle-gpu-load-20260912-implementation.md). The repositories are both heavily dirty;
their existing changes are the baseline, not material to revert. This plan owns only this new document. Production
implementation must first re-read both `CLAUDE.md` files, engine `AGENTS.md`, and the engine `fluentgpu` skill.

The artwork clock correction is already being published and verified independently: image presentation uses the
shared cache's unclamped wall time and its explicit sample timestamp. Paused immersive drift now disables its
timer. Do not describe flashing artwork, the old full-repaint stencil veto, stale ancestor damage, stale snapshot
retention floors, or the already-corrected pause-intent race as remaining baseline defects. Do not count their
savings again. Re-measure after the final native build containing those fixes.

| Evidence | What it establishes | What it does not establish |
|---|---|---|
| Earlier native ARM64 audio-only: 15 samples, 0.324% CPU, 0.514% GPU, 498.9 MB WS | Workload-specific pre-clock-fix observation on 12 logical CPUs | A minimum, target, or post-fix result |
| Earlier native ARM64 Liked Songs + syllable lyrics: 20 samples, 1.428% CPU, 7.306% GPU, 546.5 MB WS | A display-rate lyric workload costs more than audio alone | That lyric cadence is excessive or should be lowered |
| Latest root-reported post-clock native audio-only: 15 samples, 0.22969% CPU, 0.44298% GPU, 399.18 MB WS, **empty cached-album view** | Observation of this exact partly hydrated scene | A matched comparison to the earlier populated Liked Songs/playlist scenes, or optimization savings |
| Separate diagnostic JIT heap: 86,766,685 bytes, 221,845 objects | Managed retained types and candidate owners | The NativeAOT heap, private commit, or total process memory |
| Three glyph upload banks observed shrinking from 12 to 3 MiB | 9 MiB less tracked upload reserve | 9 MiB less total WS, or a future saving to claim again |
| Latest root-reported verification at plan preparation: 317 engine tests; 1536 VerticalSlice checks in Debug/Release; 227 Windows tests; 8030 app tests + one skip | Existing implementation's reported regression baseline | Validation of the proposed work below |

CPU accounting: `aggregate CPU ms/s = normalized CPU percent / 100 * logicalProcessors * 1000`.
Thus 0.324% on 12 logical CPUs is 38.88 CPU-ms/s, and 1.428% is 171.36 CPU-ms/s. Always report both values.
The earlier Debug no-tear fixture produced zero reader iterations under concurrent compilation and passed its
isolated rerun; keep a minimum-observation validity condition, and run performance captures without compilation.

Binding invariants throughout:

- Keep 120 Hz syllable progression on a 120 Hz display, its authoritative integer audio timeline, pause/seek
  ordering, zero backwards clock jumps, and immediate state edges. Display-rate animation is legitimate work.
- No text softness, reduced subpixel phases, missing glyphs, smaller artwork, weaker blur, reduced motion,
  shorter decoder cushions, disabled limiter, or removed video capability is implicitly authorized by this plan.
- The output DSP path remains allocation-free and never waits for decoding, UI, a lock, or cache maintenance.
  Device waits and producer signaling remain outside the pure DSP tripwire.
- The UI owns authored scene mutations. The renderer owns COM/GPU objects and recording scratch. A published
  snapshot is immutable until its exact generation/phase is exclusively reclaimed. CPU pins and completed GPU
  fences are independent lifetime obligations.
- New behavior is the normal implementation, with no environment-variable switches or retained legacy path.
  Tests execute behavior; they never read production source text.
- Component mount configuration stays mount configuration. New changing demand/pressure values travel through
  signals, explicit live props, context, or lifetime-bound capabilities; never through a frozen factory field.

Contract ownership remains with engine `docs/design/SPEC-INDEX.md` and its named owners. Audio changes belong to
`docs/plans/media-playback-api-spec.md` section 7.9 and `docs/design/subsystems/media-pipeline.md`; snapshot changes
to `docs/design/subsystems/threading-render-seam.md` section 0/3.4 and `scene-memory.md`; text to `text.md`; GPU
resource policy to `gpu-renderer.md` and `budgets.md`. New cross-cutting surfaces require index/owner updates and
the canon gate during implementation. This app plan is not a second definition of existing seam structs.

## 2. Work packages and order

Repository roots are `C:\wavee\fluent-gpu` and `C:\wavee\waveemusic`. File shorthand below resolves as follows:
portable engine `Media/...`, `Hosting/...`, `Scene/...`, `Render/...` and `Seams/...` are under
`src/FluentGpu.Engine/`; `Windows/...` denotes `src/FluentGpu.Windows/...` with that leading label replaced;
app `Backend/...`, `App/...`, `Diagnostics/...`, `Features/...` and `SpotifyLive/...` are under `src/apps/Wavee/`.
Engine test filenames are under `src/FluentGpu.Engine.Tests/`, app tests under `src/apps/Wavee.Tests/`.
Paths already beginning with `src/` or `docs/` are relative to their stated repository root.

| Phase | Concrete result | Primary owner/files | Depends on |
|---|---|---|---|
| P0 | Reproducible post-fix native baseline; per-owner work and memory census | Engine `Hosting/{FrameDiagnostics,WakeDiagnostics}.cs`, `Windows/D3D12/{D3D12Device,D3D12MemoryDiagnostics}.cs`; app diagnostics | Existing clock build |
| P1 | Zero level-analysis samples with no visible meter; exact neutral DSP bypass | Engine `Media/Playback/{MediaEffects.cs,Audio/PcmAudioPlayer.cs,Audio/DspStages.cs,Audio/TransportRamp.cs}`; app audio capability + deck | P0 counters |
| P2 | No periodic management work when nothing is pending; producer/control wake policy by actual obligations | Engine `Audio/{AudioFeedThread,RingAudioSource,PcmAudioPlayer}.cs`; app `SpotifyLive/Audio/FluentMediaAudioHost.cs` | P1, race fixtures |
| P3 | Snapshot text-cache storage proportional to measured text rows | Engine `Scene/SceneRecordingSnapshot.cs`, `Render/SceneRecorder.cs` | P0 bytes |
| P4 | Stable-topology lyric capture proportional to changed rows and resource changes | Engine `Scene/{SceneStore,SceneRecordingSnapshot}.cs`, `Hosting/Threading/{SceneRenderFrame,SceneFramePublisher}.cs` | P3, capture census |
| P5 | Adaptive glyph atlas capacity and byte-bounded shaped-run pools without text changes | Engine `Seams/Text/GlyphAtlasStore.cs`, `Windows/D3D12/GlyphRenderer.cs` | P0 occupancy, P3/P4 stable |
| P6 | Coordinated cache budgets and safe cold-resource reclamation, including no-present idle | Engine images/renderer; app `Backend/Residency/*`, `App/Services.cs` | P0 accounting, P5 policy |
| P7 | Allocation/root clean-up, measured SIMD or retained-render follow-ups | Owner of measured hotspot | P1–P6 measurements |

Implementation can use disjoint agents after the orchestrator assigns ownership, as repository instructions
require, but only the orchestrator builds, runs tests, publishes, or launches. Avoid parallel edits to
`PcmAudioPlayer.cs`, `SceneRecordingSnapshot.cs`, `GlyphRenderer.cs`, or canon index files. Finish and measure each
semantic batch before stacking another change on the same baseline.

### First implementation batch — ready to execute after approval

Do this as three independently reviewable patches, with a native measurement checkpoint after each. This batch
does not depend on redesigning producer/control scheduling, snapshot journals, or atlas capacity.

| Patch | Exact bounded change | Production files | Functional evidence |
|---|---|---|---|
| A | Add numeric scan/pass counters; identity fast paths for settled unity gain, neutral channel and settled unity transport | Engine `Media/Playback/Audio/{DspStages,PcmAudioPlayer}.cs`; existing reporting owner | Extend `AudioGraphTests.cs` and live-effects fixtures: byte-identical PCM, ramp endpoints, aliasing, pending writes, no RT allocations |
| B | Explicit visible-meter leases and coherent RMS/peak handoff; stop unused analysis | Engine `Media/Playback/{MediaEffects.cs,Audio/PcmAudioPlayer.cs}`; app `Backend/AudioHost.cs`, `SpotifyLive/Audio/FluentMediaAudioHost.cs`, `App/{PlaybackBridge,Services}.cs`, `Features/Player/Deck/{DeckHost,DeckClock}.cs` | New lease/publication unit tests; deck activation/source-switch integration; signal gate regression; PCM unchanged with demand off/on |
| C | Signal manager seeks/retirement/lifecycle; remove its fixed timeout and duplicate dedicated-producer low-water wake | Engine `Media/Playback/Audio/AudioFeedThread.cs` | Extend `AudioFeedRaceTests.cs`: requests before/during wait, full retire drain, seek target, concurrent stop/dispose, manual pump preserved |

Patch C leaves the producer's own bounded wait and the current control cadence in place until their progress
protocols are implemented in P2. It can therefore remove a known empty management loop without coupling that
change to the more complex seek-flush/readiness redesign. In `FeedOnce`, set the manager low-water flag only
for a ring without a dedicated producer; the ring's own `WakeProducer()` remains unchanged.

The first before/after experiment uses audio-only visible, audio-only minimized, paused, and one visible meter,
all at the same endpoint sample rate. Capture the counters and ETW CPU windows described in P0. Decision rules:

- Required exact improvements: Patch A identity cases process zero samples in the skipped stages; Patch B scans
  zero meter samples without a visible demand; Patch C has zero manager timeout wakes and no duplicate
  low-water management wake with all live rings independently produced.
- Required quality gates: zero new PCM differences, xruns/frames lost, missed seeks/retirements, or level
  publication tears. Any violation stops that patch from landing regardless of apparent CPU savings.
- Adopt a provisional regression guard of **5% aggregate CPU-ms/s** over the post-fix baseline for the same
  scenario, provided the change also exceeds the baseline run-to-run range. This is a chosen review threshold,
  not an expected speedup. A result crossing it triggers repeat capture and attribution; do not average it away.
- Claim an aggregate CPU improvement only when all three paired runs improve and the median difference exceeds
  baseline variability. Otherwise report the exact removed work and the CPU result as unresolved. No arbitrary
  percentage improvement is required to recognize a proven deletion of redundant work.
- No allocation-rate increase during steady playback and no retained-byte increase beyond the explicitly
  counted fixed lease/counter/snapshot bookkeeping. First-block and resume latency must remain within the
  baseline measured range; investigate an outlier rather than masking it by a longer warmup.

After these patches, publish one small results table before beginning P3 or changing the remaining timers.
The next immediately actionable memory patch is P3's sparse text measurements, with byte accounting and the
existing full/incremental snapshot parity suite; it does not require the P4 structural journal.

## 3. P0 — measure work and retained bytes at their owner

Reuse existing diagnostics before adding instrumentation. `FrameStats` already reports `CapturedNodes`,
`ComponentsRendered`, `MeasureCount`, `ArrangeCount`, `TextShapes`, `BindingFires`, `BindingWrites`, and hot-phase
allocations. `CapturedNodes` means copied rows, not all visited rows; it cannot prove capture is O(changes).
`D3D12Device.DiagResourceTotals`, `VideoMemorySnapshot`, `DiagDumpLive(string)`, and existing GPU census already
provide tracked resource and DXGI views. `GlyphAtlasStore` exposes `Size`, `NonZeroTexels`, `ShelfRow`, dirty rows,
and staging shortfall. Extend these owners, rather than creating a second profiler or governor.

Add plain numeric counters, sampled in the existing diagnostic reporting window:

- Audio: output blocks/frames; meter frames/samples scanned; each DSP stage's processed/bypassed frames; manager
  wakes by signal/timeout and useful work; producer wakes/refills/zero-progress pumps; control wakes and actual
  position/effect publications; sink wait timeout versus signaled completion; xrun count and frames lost.
- Publication: capture duration, topology visits, resource-reference visits, copied rows/estimated bytes,
  pin/unpin operations, full versus incremental captures and reason, resource-set changes, per-slot baseline age.
- Rendering: record CPU duration, span bytes copied/patched/re-recorded, glyph shape/raster misses, submit/present
  count and damage route. Keep CPU time separate from waiting for a fence/vblank.
- Memory: live/retained/capacity bytes per snapshot column and side table; CPU atlas mirror; glyph/run arrays
  active versus pooled; image pixel buffers in use versus retained; native arena committed versus reserved;
  active/idle/retired GPU allocations and their completed-fence eligibility; per-thread stack/handle census.

Counters must not format strings or allocate per frame/block. Keep per-thread numeric accumulation on the owner
thread; publish immutable numeric snapshots at an existing coarse diagnostic boundary. Cross-thread sampling
must use the repository's snapshot mechanism or atomic reads, not enumerate a live dictionary. Instrumentation
should introduce zero additional periodic timers. Time capture/record once per phase, not per node.

Publish one memory table with these separate columns:

| Category | Required measurement | Accounting rule |
|---|---|---|
| Managed | Live object bytes, GC committed bytes, allocation bytes/s, collection/pause counts | Live and committed differ; JIT comparison is labeled |
| Native CPU | Owned committed allocations, stacks, loaded modules, mapped views | Reserve is not resident memory; identify image/file mappings |
| GPU resources | Actual allocation sizes, resource class, heap type, active/free/retired state | Logical texture dimensions alone miss driver alignment/heap slack |
| DXGI | Local/nonlocal current usage and budget, adapter/UMA identity | A residency/accounting view, not an additive extra heap |
| Process | Private bytes/commit, WS, private WS, shared WS | Compare trends in matched workloads; do not sum overlapping categories |

On UMA, upload/default resources and CPU mappings compete for physical memory and can appear in overlapping
accounting views. Use resource identity/heap ownership to reconcile them; do not add GC heap + tracked GPU + DXGI
usage and call the result process RAM. Microsoft documents architecture-dependent memory pools and cache
properties in [D3D12 Resource Heaps](https://microsoft.github.io/DirectX-Specs/d3d/ResourceHeaps.html).

Use ETW/WPA CPU Usage (Sampled) for on-CPU attribution and CPU Usage (Precise) for switches/wake sources.
Sleeping stacks in `dotnet-sampled-thread-time` are not CPU hotspots. Keep native executable/PDB identity with
the trace and verify symbol resolution. NativeAOT diagnostic capabilities depend on the build/platform;
probe the actual binary instead of asserting blanket EventPipe support or absence.
References: [WPA CPU analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis),
[NativeAOT diagnostics](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics).

## 4. P1 — remove unused audio analysis and mathematically neutral passes

### 4.1 Demand-driven RMS/peak

Observed code: `PcmAudioSession.RenderBlock(int)` calls `TapBlock(ReadOnlySpan<float>, int)` after the transport
envelope for every rendered block. `TapBlock` traverses all interleaved samples, squares them into a double sum,
finds peak, and takes a square root. `Advance` calls `PublishVisualizer` while playing. The public effects surface
always retains a `Visualizer` signal. Wavee's actual level consumers are `DeckModels` VU, Winamp and WMP models;
`Levels(PlaybackBridge)` reads by `Peek` from their own ticker. There is no subscriber-count proof of demand.

Add an explicit disposable demand lease to the public effects/capability surface. An illustrative implementation
of the new bookkeeping inside existing `AudioEffects` is:

```csharp
// New members, implemented in MediaEffects.cs. Allocation occurs once per consumer activation.
private int _visualizerConsumers;
internal bool VisualizerRequested => Volatile.Read(ref _visualizerConsumers) != 0;

public IDisposable AcquireVisualizer()
{
    Interlocked.Increment(ref _visualizerConsumers);
    return new VisualizerLease(this);
}

private sealed class VisualizerLease(AudioEffects owner) : IDisposable
{
    private AudioEffects? _owner = owner;
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null) Interlocked.Decrement(ref owner._visualizerConsumers);
    }
}

// Existing RenderBlock, after the transport envelope:
if (_liveEffects is AudioEffects effects && effects.VisualizerRequested)
    TapBlock(buf, frames);
```

Add `AcquireVisualizer()` to `IAudioEffects`, with an inert singleton lease in `NullAudioEffects`. Add
`AcquireLevels()` to Wavee's existing `IAudioLevelSource`, forward through `FluentMediaAudioHost` to `_effects`,
and relay it through the same `PlaybackBridge` capability selection that supplies `Levels`. Other public
implementers must compile against the new interface; search these explicit public roots before implementation.
The actual capability assignment sites are in `App/Services.cs` for initial pre-login setup, pre-login rebuild,
and go-live replacement. Replace those plain `Playback.Levels` assignments with one coherent capability/revision
update so the signal and lease factory always refer to the same current host. Logout must release old ownership.

`DeckHost`/`DeckClock` own the lease through a mounted effect using `UseIsActive()`, selected preset,
`ShellUi.RailOpen`, local source availability, and the same presentation need that drives the deck. Release on
hidden/parked/minimized/unmount and source replacement. Resume acquires exactly one lease. A preset switch is
already a keyed remount. A source switch requires a reactive capability revision, because merely rereading a
frozen host reference or `Peek()` inside the effect would miss it. Do not acquire from `DeckModels.Levels` on
every pull. Keep enough demand through the meter's existing decay/settling period; settling must not freeze.

The sketch above is only the demand counter, not a complete cross-thread frame handoff. Replace the current
plain `_tapRms/_tapPeak/_tapDirty` exchange with a coherent scalar snapshot using a proven single-writer
publication pattern. A practical allocation-free implementation packs the two float bit patterns into one
`long` and uses `Interlocked.Exchange`/`Interlocked.Read`, plus a generation/sequence for freshness. The control
thread remains the sole writer of `Visualizer`; UI consumers still pull without reacting to audio-thread writes.
On release/reacquire, discard stale generations and publish one silence/reset edge off RT as necessary. An
acquire racing a release or track replacement must not resurrect a prior session's level frame.

Work budget: with no meter consumer, **zero TapBlock calls and zero meter samples scanned** after the current
block boundary. At 48 kHz stereo that removes analysis of 96,000 samples/s; at 192 kHz stereo, 384,000 samples/s.
The eliminated read traffic is at least 384,000 or 1,536,000 bytes/s respectively, before cache effects. These
are exact operation counts, not predicted CPU savings. Visible meters retain current fidelity and cadence.

Gates: lease reference counting and idempotent disposal; two consumers; hidden/restore; source and track change;
acquire/release concurrent with render; coherent paired level values; no consumer gives zero scans; a known sine
and impulse retain existing RMS/peak values; PCM is identical with demand on/off; zero RT allocations.

### 4.2 Exact identity DSP paths

`GainStage.Process(ReadOnlySpan<float>, Span<float>, int, in BlockCtx)` currently multiplies every sample even
when the fully advanced gain is exactly one. Add this branch after its existing `_gain.Advance(frames)`:

```csharp
float start = _gain.Advance(frames);
float end = _gain.Current;
if (start == 1f && end == 1f)
{
    // Exact same storage: no work. CopyTo also handles overlapping, shifted spans correctly.
    if (!src.Overlaps(dst, out int offset) || offset != 0)
        src[..n].CopyTo(dst);
    return frames;
}
```

Retain the preceding explicit-bypass branch and ramp clock advancement. Do not use an epsilon to skip near-unity
gain; that changes PCM. Do not silently extend existing `Overlaps` assumptions to shifted spans. Test separate,
identical, and shifted buffers explicitly, with graph aliasing policy documented.

`ChannelStage.Process` traverses stereo even at settled balance zero and mono disabled. Capture the pre-block
balance returned by `Advance`, and bypass only when pre/post balance are both zero and `_mono == false`.
Preserve the current ramp/evaluation semantics; smoothing changes deserve a separate audio correctness change.
The existing `Bypassed` property is not honored here: decide and gate that contract explicitly while touching
the method, rather than making an unnoticed behavioral change. Other channel counts must retain mono semantics.

`TransportRamp` already has `EndFrame` and `At(long)`. Skip its per-frame loop only after the envelope has reached
constant unity; the existing signature permits the following exact branch without a second ramp implementation:

```csharp
bool settledUnity = ctx.StartFrame >= _transport.EndFrame
    && _transport.At(ctx.StartFrame) == 1f;
if (!settledUnity)
{
    for (int frame = 0; frame < frames; frame++)
    {
        float gain = _transport.At(ctx.StartFrame + frame);
        for (int channel = 0; channel < _format.Channels; channel++)
            buf[frame * _format.Channels + channel] *= gain;
    }
}
```

Keep pending-write handling before rendering: an endpoint accepting only part of a block must neither rerender
nor reapply the envelope to retained samples. Keep fade-tail submission and pause acknowledgement ordering.

Work budget: each skipped identity read/write pass avoids `sampleRate * channels` multiplies and at least
`sampleRate * channels * 8` logical bytes/s of float reads+writes. At 48 kHz stereo that is 96,000 multiplies
and 768,000 logical bytes/s per pass. Count actual active passes; normalization/volume may make gain non-unity.
Do not skip the limiter because its gain was one last block, or flat EQ because its current gains are zero:
limiter detection and IIR histories/ramping may still be required. Audit stateful stages separately.

Gates: unity/zero/non-unity gains, ramps entering/leaving unity, mute/unmute, stereo/mono/multichannel,
normalization, EQ enabled/disabled, transport pause/resume/seek inside partial blocks, crossfade/gapless,
and no extra xrun or sample discontinuity. Capture golden output before changing code.

## 5. P2 — schedule each audio obligation once

### 5.1 Remove the empty manager's 20 ms poll

The live thread roster is output, management, control clock, and one producer per live voice. Management
`WorkerLoop` calls `WorkerPumpOnce()` every 20 ms. That method services a seek mailbox and retire stack, but
skips `PumpAhead()` for every ring with `HasDedicatedProducer`. Live producers therefore make the management
poll demonstrably redundant during normal playback. Retain `WorkerPumpOnce()` as the deterministic fixture seam.

First change only the manager. `EnqueueRetire` already publishes the intrusive stack then signals `_workerWake`.
`RequestSeek(long)` currently only stores the mailbox: add publish-then-signal. Verify install/start/stop/fault
paths also wake any work they create. In production all live rings have dedicated producers; headless manual
pumping is not a reason to poll an idle production manager.

```csharp
// Existing public method; wake AFTER publishing the last-wins request.
public void RequestSeek(long frame)
{
    Volatile.Write(ref _pendingSeekFrame, frame);
    if (!_disposed) _workerWake.Set();
}

// WorkerLoop, once all production work creators use the same wake protocol:
while (_run)
{
    try { WorkerPumpOnce(); }
    catch (Exception e) { RecordFault(ref _workerFaults, e); }
    if (_run) _workerWake.WaitOne();
}
```

This is a design delta, not a complete disposal patch: the existing `_disposed` check alone does not prove a
concurrent event cannot be disposed between check and `Set`. Keep wake handles alive until every producer of
that wake has detached/acknowledged shutdown, or encapsulate the established disposed-wake handling. Complete
and test stop/join ownership, including `_outputWake` and any new control wake. A failed join retains resources;
do not free them to make a leak counter look good.

An `AutoResetEvent` set between draining and waiting is retained; coalescing requests is safe only because the
consumer drains the mailbox/stack to a fixed point. Remove low-water manager signaling when every ring's own
producer already receives it; leave explicit manager work signaling intact. Keep the retirement identity by
ring reference and render acknowledgement, not a reusable voice ID alone.

Budget: **zero manager timeout wakeups/s**, from a configured upper cadence of 50/s. Signal wakes must correspond
to real seek/retirement/lifecycle work. This is not a claim that exactly 50 OS switches/s were measured.

### 5.2 Park full/finished producers; preserve progress on every edge

`RingAudioSource.Produce` also waits only 20 ms, even when its ring is full or EOF has been reached. `WakeProducer`
already exists for low water, seek and cancellation. `PumpAhead()` exits while `_flushRequest` is set; currently
`RtConsumeFlush()` clears that flag without waking the producer, so simply replacing its timeout with infinity
can deadlock a seek. Fix these obligations before changing the wait.

Use a pure wait-decision type with explicit states: NeedsDecode, WaitingForFlush, AtTarget, AtEof, Stopping,
and TemporaryNoProgress. Introduce no second decoder owner. AtTarget/AtEof wait on the producer event; flush
acknowledgement, queued seek and cancellation wake it. For a public source returning zero without EOF and without
a readiness capability, use a bounded retry deadline only while that condition exists. Existing blocking reads
stay on the isolated producer; an incoming blocked source must never stall the active voice.

Low-water latching needs a real lost-wake test. The producer can refill and the output consumer can drain again
before the output observes a high-water sample. The current RT-owned `_belowLowWater` latch cannot be assumed
to rearm automatically in that sequence. Prefer a producer-armed request protocol with an atomic armed bit:
producer arms before rechecking fill, consumer clears-and-signals when low, producer cancels the arm when it
decides to keep decoding. Its ordering and fill recheck must cover every interleaving. Do not add an unsynchronized
second writer to the current latch. Signal outside the pure DSP region, as the existing feed does.

Replace `WaitUntilReadyAsync(int minimumFrames, CancellationToken)`'s 2 ms `Task.Delay` loop with a retained
readiness notification for that threshold/seek generation. Creation is cold-path; producer completion schedules
continuations asynchronously. Cancellation affects that waiter, not another caller's readiness; stale seek
generations cannot complete the new seek. This removes short-lived timer work during every preparation/rebuffer.

Budget: zero producer timeout wakeups while full, paused with its target buffered, or at EOF. During uninterrupted
500 ms-ahead playback with a half-target low-water trigger, useful refills should be approximately four/s per
active voice; treat this as a model, not a fixed timing assertion. Network chunking, rate conversion, catch-up,
or partial reads change the count. Preserve the configured 500 ms ahead / 1000 ms ring and burst cap of three.

Gates: publish-before-wait; refill/drain without an observed high-water sample; seek flush during pause; repeated
last-wins seeks; canceled readiness; source returns zero then progresses; source EOF; producer failure; blocked
incoming voice; stop during read and wait; device rate switch; no use-after-dispose, no missed retirement.

### 5.3 Separate effect changes, transport deadlines, and position observation

`AudioFeedThread.ClockLoop` sleeps 15 ms regardless of playback state. `PcmAudioSession.Advance` calls
`ReconcileEffects()` each turn, including pause. `EqTopologySignature(Equalizer)` scans enabled/band count/type/
frequency/Q, then gains are separately scanned. Wavee `FluentMediaAudioHost.StartTicker()` adds a 200 ms timer
whose `Tick()` also handles gapless/crossfade preparation, projection, xrun drain and state transitions.

Do not lengthen all of these clocks at once. Implement in this order:

1. Give `AudioEffects`/`Equalizer` an explicit change revision/notification owned by their signal mutation
   surface. `Equalizer.Apply` replaces the band array even when Enabled is already true; it must publish a
   topology revision. Subscribe/detach band parameter changes through the existing reactive runtime mechanism,
   with clear thread ownership. A fingerprint hash is not a collision-free revision. Keep supported direct
   signal writes functional; a setter wrapper nobody uses is insufficient.
2. Reconcile effect state only on a new revision. Build coefficient/topology payloads off RT, publish through
   the existing graph/control handoff, and preserve graph consume-gated retirement. Do not make the control
   observer write mutable EQ arrays while the output thread is processing them. Resolve existing sharing at
   this seam as part of the revision design; no additional concurrent writer is acceptable.
3. Replace the clock's blind `Sleep(15)` with an interruptible wait on commands/state/effect work and the next
   actual deadline. Keep active-playback position cadence unchanged initially. After a pause fade and endpoint
   drain are acknowledged, no recurring position/effect work is needed. Opening, buffering, device recovery,
   starvation and scheduled joins have separate progress deadlines and signals; each must be enumerated.
4. Fold Wavee's 5 Hz host maintenance onto session events plus its actual next preparation/join/projection
   deadline. Preserve `AudioHostSignalGate` and reentrant publication ordering. Its host gate must not surround
   asynchronous session operations. Schedule transport joins in the integer sample domain; do not move a
   sample-sensitive transition to a coarse UI timer.
5. Only after these edges work, test whether active control position publication can be reduced independently
   of the lyric display clock. The latter already interpolates an anchor. Retain immediate pause/seek/track
   edges and experimentally establish acceptable anchor drift. This is an evidence-gated follow-up, not an
   authorized reduction of lyric update cadence.

Add the same policy to the single-thread `PcmAudioSession` feeder mode so a fallback path does not retain a
perpetual poll. Keep output WASAPI event pacing; paused `WaitForOutput` must eventually become an interruptible
control wait after the audible tail has drained, rather than returning every block timeout on a stopped sink.
Device-invalidated/rebuild notifications must wake it. Do not remove the endpoint timeout while it is still
the only guaranteed recovery stimulus; replace that obligation explicitly first.

Budget: settled paused/minimized audio has **zero periodic manager, producer, effect-reconciliation, position,
or output-block wakeups**, apart from separately documented required device/network/recovery obligations.
Normal control has zero topology scans between effect revisions. A 15 ms configured cadence is approximately
66.7 turns/s; the app timer adds a configured five/s. Counters establish how many are actually eliminated.

## 6. P3 — sparse snapshot text measurements

`SceneRecordingSnapshot` currently owns a dense `TextMeasureCache[] _measurement`, grows it to the scene index
high-water, fills it through `SceneStore.TryGetMeasureCache`, and exposes `MeasureCacheRef(NodeHandle)`.
The UI store already uses sparse measurement storage. `SceneRecorder` only reads
`MeasureCacheRef(node).ResolveForWidth(b.W)`. It needs the correct recorded metrics, so deleting the cache is
incorrect. `TextMeasureCache` currently contains two entries, not the historical eight-entry design example.

Use existing `SnapshotColumn<T>` first, avoiding a new sparse container. Proposed delta:

```csharp
// SceneRecordingSnapshot: replaces the dense array.
private readonly SnapshotColumn<TextMeasureCache> _measurement = new();

// At the existing CopyNode measurement site (after any row removal):
if (source.TryGetMeasureCache(node, out var measurement))
    _measurement.Set(index) = measurement;

// New read-only recorder accessor; missing rows retain the existing default semantics.
internal TextMeasureEntry ResolveMeasureForWidth(NodeHandle node, float width)
    => _measurement.TryGet((int)node.Raw.Index, out var measurement)
        ? measurement.ResolveForWidth(width) : default;

// SceneRecorder's existing metrics lookup becomes:
TextMeasureEntry mc = scene.ResolveMeasureForWidth(node, b.W);
```

Register the column in `BeginSparseCapture`, `EndSparseCapture`, and `ClearSparseRows`, and remove the dense
`Grow(ref _measurement, count)`. Keep the UI layout's mutable `SceneStore.MeasureCacheRef` unchanged.
Snapshot `TryGet` returns a value copy; benchmark that against an internal ref-readonly sparse accessor before
complicating the API. Use existing structural capacity preparation or add `SnapshotColumn.Reserve(int)` at a
non-hot preparation boundary so new dictionary/free-list capacities cannot allocate in steady capture. Tests
must include the first new text row after a warmed non-text scene, not only a fully warmed table.

Measured JIT candidate: three dense arrays were 1,030,696 bytes each, versus 151,576 bytes for the UI's sparse
cache. The illustrative difference `3 * (1,030,696 - 151,576) = 2,637,360 bytes` is **not a forecast**: the sparse
snapshot index/capacity and actual reachable text counts differ from UI storage. Report measured before/after
owned bytes, including dictionaries, free lists, reserved slots and array headers. Required structural budget:
measurement values grow with measured reachable text rows, not every scene slot.

Gates: two-width ring/fallback semantics; non-text high-water; newly measured row; text→nontext/recycled generation;
park/unpark; no measure invalidation; full/incremental snapshot parity; renderer-held snapshot unchanged during
UI relayout; zero hot allocations. Glyph output and text baseline/line-height must be unchanged at all tested DPIs.

After this lands, evaluate replacing the two-entry cache with one resolved immutable `TextMeasureEntry` per text
row. That is a separate reduction: capture must resolve using the exact bounds the recorder uses, including
animation/reflow changes. Only adopt it after parity proves both width cases and fallback behavior.

## 7. P4 — retain stable capture topology and reference sets at lyric cadence

Current incremental capture already avoids most column copies. `CaptureCore` still walks reachability, walks
the previous captured list, and then scans every captured row again for images/span runs. At roughly 3500 nodes
and 120 captures/s, one full walk is about 420,000 row visits/s. This is a code-derived potential workload,
not a sampled CPU hotspot or a promise that all walks are eliminated.

Keep the first change narrow: maintain a compact list of captured image-bearing and span-bearing nodes, updated
when rows are copied/removed. Rebuild reference sets from that list rather than every scene row. Protect all
three image identities: current, derived, and outgoing swap. A record-only glyph wipe does not change those
identities. Image readiness/reveal timestamps can change without the scene's identity list changing:
`ImageRecordingSnapshot.Capture(ImageCache?, ReadOnlySpan<int>)` must still refresh the referenced images as
required by the shared wall-clock/reveal contract. Retaining the ID set is not permission to freeze metadata.

Then add a strict unchanged-reachability fast path to `CaptureIncremental(SceneStore, extraRoots, lastCapturedSeq)`.
Keep its existing lineage/bulk-mutation/floor checks. Add an exact structural/reachability revision distinct from
paint/record dirty state; increment on allocation/free, attach/detach/move, parking, root, orphan, overlays,
drag roots, and popup extra-root changes. Compare extra-root identities by content/generation, never span address.
If the revision matches the slot baseline, retain its captured set and update only journaled changed rows.
If any proof is absent, perform the existing full reachability walk.

The current capture journal records changed indices/stamps; implementation must establish whether it supports
enumerating the delta without scanning every live slot. If not, add a bounded append-only change journal with
per-slot baseline cursors and dedup epochs, and use the existing floor/fallback pattern when overwritten.
Do not turn a cheap scan into an unbounded mutation log. Correct full capture is the fallback on overflow,
epoch wrap, unknown bulk write, changed roots, or stale baseline.

Resource reference updates can use counts per ID and per-row prior identities: subtract old references before
applying the changed row, add new references, and pin/unpin only zero↔nonzero transitions. Removing one of fifty
nodes sharing an image must not unpin the shared image. Counts belong to each exclusively writable snapshot,
not a mutable global set shared with the renderer. Strings, span runs, immutable paths, image IDs and outgoing
swap holds each follow their existing owner; do not blindly merge their unlike lifetime contracts.

For a stable lyric wipe, the intended path is:

```text
Audio integer clock -> existing lyric display ticker -> glyph-wipe signal/binding
  -> journal changed paint rows
  -> claim oldest writable snapshot generation
  -> update changed rows; retain unchanged topology and reference identities
  -> refresh time-varying image metadata / animation inputs
  -> publish immutable snapshot
  -> render existing draw spans plus changed glyph-wipe commands at display cadence
```

Preserve independent self/descendant dirty lifetimes, the oldest writable snapshot selection, removal extent
carry, `RenderSubmissionContinuity`, renderer-held claims, and fence-protected image retirement. Never skip
publication merely because no component rendered: bindings, time-dependent image state, feedback, damage,
popups and animation descriptions can all owe pixels. A separate read-only static tree per page is not needed.

Budgets: in a fixture with constant topology, fixed resource identities and K changed lyric rows, copied rows
remain O(K); **full topology visits and full resource-row scans are zero after warmup**, while any remaining
bounded per-image/per-scroll work is reported. No hidden O(high-water) clear may replace the removed walk.
On 120 Hz lyric windows: no extra component renders/layout/shapes attributable to the wipe; no added allocation;
record/publish p95/p99 and actual presents remain at least as good as the post-clock-fix baseline.

Gates: full-versus-incremental differential snapshots/DrawLists; skipped publications and unknown gaps; a reader
retaining a slot for many UI turns; all three slots used then returned; continuous child dirty marks; shared
image removal; outgoing swap fading; span text edit/selection; popup lifetime; parked tab; root replacement;
generation recycle; ledger overflow/wrap; randomized mutations. Reuse `IncrementalCaptureTests`, snapshot
ownership/image-lifetime fixtures, and the existing damage/clock parity checks. Run ARM64 contention gates with
explicit minimum reader iterations and deterministic barriers.

## 8. P5 — adaptive glyph and text-cache memory without changing glyph quality

The renderer's `ATLAS = 4096` allocates a 16 MiB R8 CPU mirror and a nominal 16 MiB GPU texture. Its four vertical
subpixel phases are deliberate: scrolling text remains crisp. `GlyphAtlasStore` has an append-only shelf packer;
`NonZeroTexels` measures ink, **not usable free packing area**. Tiny ink coverage does not prove a smaller atlas
fits. Measure occupied shelf area, wasted shelf height/gutters, failed pack dimensions, active distinct glyphs,
font/size/phases, resets and misses. Mixed scripts, icons, large lyrics, DPI/zoom changes are mandatory workloads.

Implement a pure atlas-capacity policy in the text subsystem, consumed by `GlyphRenderer` at safe boundaries:

- Start with a smaller atlas only after cold-shell + common-lyrics traces establish fit and a no-regression
  first-paint path. Candidate 2048² is 4 MiB per R8 mirror/texture: **12 MiB less per copy** versus 4096² if it fits.
  This is capacity arithmetic, not an unconditional 24 MiB WS promise.
- Grow promptly on projected packing pressure or an actual full-pack request, up to at least the current 4096
  capacity. Preflight/prewarm required entries before emitting GPU instances; do not accept new blank tails or
  text flashes as the price of a smaller default. A growth that cannot safely complete now keeps the valid old
  generation and its existing recovery behavior; it never reuses UVs under recorded instances.
- Shrink only after a long cold interval/pressure event and proven repack fit with headroom. Use hysteresis and
  a minimum residence interval. A warm mixed-script/zoom cycle must not repeatedly shrink, rerasterize and grow.
  CPU time saved by P1/P2 is not a license to spend it on recurring atlas churn.
- Make atlas dimensions explicit throughout resource description, row pitch, upload-band arithmetic, UV
  normalization, shader constants and icon UVs. `GlyphAtlasStore(int size)` already owns dimension validation;
  construct a new store for resize, rather than mutate its readonly mirror while another operation uses it.
- Advance a renderer-owned monotonically changing realization generation across store replacement. A new
  `GlyphAtlasStore` begins its own epoch at zero; replacing it without changing the wider generation could make
  stale cache entries appear current. Invalidate glyph/icon/run caches and repaint damage on the same boundary.
- Resource/descriptor replacement is render-owned and behind all relevant last-use fences. Allocate a new
  descriptor or prove old references retired before rewriting it. Count temporary old+new allocations. CPU
  mirror replacement must stay out of the allocation-free frame phases; introduce an explicit cold maintenance
  boundary if necessary, rather than weakening the tripwire.

Retain the implemented 3 MiB warm upload reserve. Its new capacity derives from actual atlas width times staging
rows and bank depth; do not independently multiply the old saving again. Reuse `GlyphStagingPolicy` for staging
floor/growth/backlog behavior, including exact apron/row-band tests.

Shaped text/run caches should be capped in bytes, not only counts/age. `GlyphRenderer` currently has `_runCache`,
`_quadPool` with 14 buckets and eight arrays per bucket, and periodic age-based trim. Track `ShapedGlyph` array
capacity bytes, per-run color arrays, dictionary capacity and active references. At most eight arrays per bucket
can still retain a large byte total; apply a global pooled-byte cap and age/pressure trim using the existing
pool. Arrays in a live run or referenced by an in-flight instance preparation are ineligible. Verify array
return is exactly once on eviction/reset/device loss. Report miss/raster cost as well as retained bytes.

Gates: all subpixel phases unchanged; icon catalog, Latin/CJK/Arabic/combining marks/emoji, lyrics at largest
supported size, DPI/zoom ladder, cache full mid-frame, failed allocation, resize while old frames in flight,
device loss/recovery, repeated growth/shrink under pressure. Pixel comparison must show no changed glyph shapes,
positioning, gamma, color or baseline; no additional dropped glyphs/visible missing frames are acceptable.

## 9. P6 — coordinated cache budgets and no-present maintenance

Reuse Wavee's `MemoryGovernor.Register(int, string, Func<long>)` / `Trim(MemoryPressure)` and its copy-on-write
registry. Current priorities already register image trim, detail cache, and pinned entity shedding in
`App/Services.cs`. Reuse engine `GpuMemoryBudgets.For(bool weak)` and `PixelBufferPool` rather than minting
parallel caches. Existing weak defaults are 16 MiB pixel reserve, 40 MiB image cache and 8 MiB derived cache;
strong defaults 32/64/16 MiB. They are independent budgets, not actual allocations or a desired fixed WS.

Add a pure decision function for effective byte caps using measured active pins, recent demand, OS pressure and
DXGI budget headroom. Keep tier ceilings as bounds while making idle reserve adaptive. For example, target
unpinned image reserve can be a bounded recent working set; never lower the cap below pinned committed bytes.
Choose reserve floors/headroom from observed navigation miss curves, document them as policy constants, and
test weak/strong inputs directly. Avoid an unsubstantiated universal process target such as 200 MB.

Detailed order:

1. Release completed image-decode CPU buffers promptly to `PixelBufferPool`; expose in-use versus retained
   bytes. Existing `Trim()` drops retained buffers; consider `TrimToBytes(long)` as a new owner method to keep
   the next visible decode warm. Keep decode admission bounded by both job count and anticipated decoded bytes.
2. Evict unpinned derived/prefetch images before frequently revisited visible-sized art. Cancel obsolete decode
   and bake jobs on recycle/park, but generation-check completion so late work cannot repin a removed source.
   Charge committed allocation bytes, including bucket/alignment, and keep outgoing swap references alive.
3. Trim free glyph/run arrays, free image texture capacity, completed retired resources, and idle layer targets
   through their owners. A governor callback posts a bounded maintenance request to the render thread; UI must
   never dispose COM or enumerate the renderer's pools.
4. Address frame-count-only eviction. Staging/layer policies currently age on submitted frames; when the app
   becomes completely idle, no more submissions means some cold reserves never reach their threshold. Add one
   next-maintenance wall deadline when reclaimable reserves exist, servicing it without submit/present. Cancel
   the deadline when there is nothing to reclaim. Keep frame/fence safety distinct from wall-clock policy:
   elapsed time makes a resource eligible, **completed fences** make release safe.
5. A minimized host should not subscribe to the display clock merely to age caches. One necessary maintenance
   wake may trim safe cold resources and then return to indefinite idle; it must not restart rendering. On
   restore, reuse valid retained resources and repaint only when required. Measure restore latency explicitly.
6. The app detail/entity governor must retain current track/queue, saved heads and displayed details. Reuse
   `Services.BuildPinSet()` on the UI thread. Under pressure, coordinate list/model/entity budgets to avoid
   duplicate projections and recently visited detail retention where ownership permits sharing immutable data.
   Do not lower existing entity limits before measuring live references and cache-miss/network consequences.

For GPU allocation accounting, distinguish committed-resource size from logical payload, and placed-resource
heaps from the views inside them. If a freed resource leaves a pooled heap resident, report reusable bytes as
reusable; do not claim the heap was returned to the OS. Resource deletion is actual only when both CPU snapshot
pins and GPU use are retired. A `MemoryGovernor` callback returning queued bytes is not reporting bytes freed.

Separate known cache-correctness defect, diagnosed during this session: opening cached album
`spotify:album:71TimGdYvnolc8o298RVxs` before the live backend attached showed its cached header with empty tracks.
The initial navigation preceded live-backend readiness by approximately 1.7 seconds. Root traced a route-only
`DetailPage` resource key, an ignored offline hydration outcome in `StoreLibrarySource.GetAlbumAsync`, and no
live-edge retry; navigating home and back yielded the expected 13 tracks. This has not been attributed to memory
trimming and is not fixed by this plan. Track its fix separately, and add its sequence to P6's regression corpus:
cached partial data -> backend becomes live -> current view obtains tracks without manual remount. A pressure
trim must preserve hydration level, pending/error state and retry eligibility; it must never turn incomplete
cached data into a permanently authoritative empty result. Before/after memory runs use equivalently hydrated
scenes; the lighter empty-album sample above is not evidence of a successful optimization.

Gates: no pinned image/entity loss, priority order and idempotence, unregister during trim, shared-device multiple
windows, in-flight fence retention, no-submit maintenance, pressure during decode, memory recovery, repeated
navigate/back cycles, restore after long minimize, and no new static idle timer once reclaimable reserves empty.

## 10. P7 — bounded allocations, roots, and remaining measured hotspots

After P1–P6, rank remaining CPU-ms/s and retained bytes by owner. Investigate these concrete candidates:

- The diagnostic heap's roughly 8.46 MB of 32 KiB byte arrays has **unknown ownership**. Capture roots/allocation
  stacks before assigning it to networking, decoding, SQLite, logging, or a pool. Count active versus idle arrays,
  pending operations and retained queue entries. Bound the real owner rather than trimming `ArrayPool.Shared`
  globally or forcing GC. Do not inspect excluded private runtime sources to complete this inventory.
- `NodePaint` arrays appearing four times can be UI plus three snapshots, not a leak. Reuse the existing
  `SceneStore.TrimExcessCapacity()` tail-trim discipline; snapshot buffers need their own safe exclusive-slot
  trim on sustained high-water slack. Generational indices cannot be compacted by moving live nodes.
  Keep high-water growth outside hot phases; pressure trim invalidates affected incremental baselines.
- Retained snapshot side-table `Remove` intentionally keeps reusable value payloads. Free lists holding arrays,
  orphan child lists, span decorations and previous-capture lists need an idle byte budget, not just an entry
  count. Clear reference-containing dead payloads only when doing so does not create per-frame reallocation.
- Inspect public decoder/resampler/mixer buffer lifetimes, duplicate PCM staging and prepared-next ownership.
  Keep ring duration. A 48 kHz stereo float ring needs at least 384,000 payload bytes per second of capacity;
  a 192 kHz ring needs 1,536,000 before power-of-two rounding. Release retired rings/preparations promptly, cap
  simultaneous prepared items, and report actual `PcmRing` capacity rather than deriving it only from duration.
- `FluentMediaAudioHost.Tick`, `PlaybackBridge`, and deck anchor handling may repeat equal projections. Change-gate
  scalar/metadata publications at their owner; keep the first/final pause and seek publication. Extend the
  already-fixed `SeekBar` signal-effect pattern only where profiling shows redundant component recreation.
- If capture improvements leave recording dominant, inspect remaining full command hash/span-copy costs at
  lyric cadence. Prefer segment-level retained proof at existing recorder spans with complete invalidation for
  glyph wipe, baked geometry, images, clipping and damage. A weak hash or a generic changed flag is not a safe
  replacement for `RenderSubmissionContinuity` proof. Quantify bytes copied before designing a new opcode seam.
- SIMD only the still-measured sample hot loops: master multiplication, interleaved mixing, peak/sum reduction
  when demanded, and eligible resampler kernels. Use `Vector<T>` or an ARM64 `Vector128`/AdvSimd specialization
  with a scalar fallback and tail handling. Reordered floating-point reductions require stated tolerance for
  meter output; audio PCM paths retain the golden quality contract. Recursive IIR state does not permit naive
  vectorization across time. No x86-only win is evidence of an ARM64 improvement.
- Consider fusing remaining stateless passes only after individual bypasses land. Preserve stage order,
  normalization/limiter behavior and transport position; meter must observe the same post-envelope samples.
  Fewer buffer traversals are useful only if branch cost and code size do not erase the gain on ARM64.

All long-lived dictionaries, queues, pools and caches touched by this work get an explicit byte or capacity
bound, owner, eviction/failure policy, and counter. Exceptions for live/pinned working sets are visible in the
census rather than silently violating a target. Do not add periodic `GC.Collect`, `EmptyWorkingSet`, or per-tick
pool clearing: those can lower one memory snapshot while worsening latency, page faults and audio reliability.

## 11. Verification and acceptance

### 11.1 Orchestrator-only regression gates

Run the required gates once per completed implementation batch, expanding only for actual failures/new risks:

```powershell
# C:\wavee\fluent-gpu
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice
dotnet run --project src/FluentGpu.VerticalSlice -c Release
dotnet test src/FluentGpu.Engine.Tests/FluentGpu.Engine.Tests.csproj
dotnet test src/FluentGpu.Windows.Tests/FluentGpu.Windows.Tests.csproj
# When engine docs/design contracts changed:
powershell -File docs/design/check-canon.ps1

# C:\wavee\waveemusic
dotnet build Wavee.slnx
dotnet build Wavee.slnx -c Release
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
Invoke-Pester -Path ops/release/tests
```

Audio tests belong primarily in engine/app unit-test projects, following existing playback fixtures, not in
source-scanning tests. Add behavioral fixtures for new policies and concurrency protocols. Existing desktop
pixel probes cover a separate obligation: rerun real D3D12 repaint identity and render-owned clock/idle probes
for snapshot/atlas changes. No plan code snippet counts as a passed test.

### 11.2 Native ARM64 workload matrix

Use the orchestrator's authorized NativeAOT launch/publish workflow and identify binary, symbols, architecture,
adapter, driver, sample rate/channels, physical window dimensions, DPI/zoom, refresh rate, power source and active
GPU counter instances. Warm for at least 60 seconds after navigation/cache activity, then measure a 60-second
steady window; repeat three times in alternating baseline/change order. Use longer windows for low-rate idle
events. Capture startup/restore separately so warmup does not conceal regressions.

| Workload | Action | Required observations |
|---|---|---|
| Paused visible | Lyrics closed; no input after fade settles | No periodic audio work; no unchanged presents; memory reserve ages once |
| Paused minimized | Minimize, wait, restore, seek while paused | No display-clock subscription; no producer/manager/control spin; prompt restore |
| Audio-only visible | Local decode, lyrics closed, static page | Meter scans zero; output/decoder work attributed; expected coarse playhead UI only |
| Audio-only minimized | Continue same track | Audio remains uninterrupted; visual work stops; buffer cushions unchanged |
| Lyrics rail | Known syllable track, Liked Songs then playlist | 120 Hz cadence; zero extra layout/shaping from wipe; reduced capture visits |
| Expanded lyrics | Same track, play/pause/resume and seeks | Correct word/syllable positions; no artwork clock rewind; paused drift settles |
| Meter decks | VU, Winamp, WMP; hide/restore/change source | Lease follows visibility; correct levels; no stale frame; no background scans |
| Scroll/navigation | Repeated list/lyrics scroll; 100 route cycles | No high-water staircase after bounded caches settle; no text softness or blank art |
| Audio stress | 44.1/48/96/192 kHz where available; gapless/crossfade, repeated seeks, rate/device switch | Same PCM/transition rules; zero additional xruns; bounded workers/preparations |
| Cache pressure | Mixed scripts/icons/zoom; memory pressure; device recovery | Correct atlas growth/repack; bounded reserve; no thrash or missing glyphs |
| Long session | At least 30 minutes incl. tracks, pause, navigation, logout/login | No retained session/lease/thread/handle growth; finite memory plateau |

Use the already-documented test track `spotify:track:3ZFwuJwUpIl0GeXsvF1ELf` where the current log confirms
syllable lyrics. For activation automation, preserve the existing WM_COPYDATA cookie and UTF-16 length excluding
the terminal NUL. Do not diagnose driver mistakes as player failures. Avoid concurrent build/probe/network-load
experiments during ordinary comparisons; explicit stress is a separately labeled workload.

For each window report aggregate CPU-ms/s and normalized percent; useful/empty wakes by thread; allocation
bytes/s and GC pauses; actual presents and cadence distribution; capture/record p50/p95/p99; xruns/frames lost;
managed/private/WS and per-owner memory bytes; atlas/cache misses; trim/grow counts; first-frame/restore latency.
Show variability and raw trace paths. A small percentage change below run-to-run variation is inconclusive;
deterministically removed sample passes/wakes still establish reduced work without fabricating a speedup.

### 11.3 Completion budgets

The program is complete when the following behaviors are verified, and remaining measured hotspots have a
documented owner and a reasoned next decision:

- No unused meter analysis; no unity transport/gain or neutral channel traversal in their proven identity cases.
- No periodic idle audio manager/producer/effect/position/output work once all actual transport/device obligations
  settle; no UI/render work sustained by inactive lyrics, drift, or meters. Network keepalive and genuine recovery
  work are separately counted. Windows guidance supports avoiding unnecessary background timers and vsync waits;
  playing audio remains legitimate background work.
  [Windows power guidance](https://learn.microsoft.com/en-us/windows/apps/develop/performance/power)
- Stable-topology lyric capture has no full reachability/resource scan after warmup, with exact fallback on every
  structural or unknown change; unchanged measurement/layout/shaping work stays zero in the fixture.
- Snapshot text values and caches scale with their real users, all owned reserves have byte accounting, and idle
  pressure maintenance can complete without manufacturing rendered frames.
- No audio/visual correctness regression, no extra drops/stalls, no reduced lyric cadence, and a stable long-run
  memory plateau. Lower retained memory must not come with repeated decode/rasterization or worse restore latency.
- Publish measured per-phase improvements and any remaining uncertainty. Do not add theoretical atlas/snapshot/
  upload capacities into an aggregate working-set claim. If a policy saves memory but regresses responsiveness,
  revise its headroom/hysteresis before calling the phase finished.

## 12. Separate options that change quality or responsiveness

These are outside the no-quality-loss implementation above: reducing lyric FPS, reducing four subpixel phases,
lower-resolution artwork, changing blur/downsampling quality, shrinking decode-ahead duration, raising device
latency, releasing the whole paused session so resume must decode/reconnect, or dropping offscreen retained
navigation state aggressively. They may offer additional savings, but require a concrete measured tradeoff and
an explicit product choice. None is an assumed shortcut to satisfying this request.
