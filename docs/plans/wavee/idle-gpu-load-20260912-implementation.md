# Idle GPU and playback load verification — 2026-09-12

Continues `handoff-20260912b-idle-gpu.md`. Both working trees remain uncommitted; earlier blur and playlist-error work is preserved.

## Findings and implementation

1. Rebuilt Release VerticalSlice first: **1519 checks passed**.
2. Normal repeated flips on the real render thread did not reproduce the reported permanent orphan. Twelve 1 Hz
   remounts reached four tracks/one orphan and settled between every flip; five seconds afterward presented nothing.
   The 48-flip stress variant also passed, including 300 ms UI stalls and repeated publications during animation.
3. The orphan timeout was independently broken. A real orphan with looping opacity/Dy tracks survived the old
   engine's four-second watchdog: two tracks, one orphan, 120 additional presents, no cleanup deadline. The fixed
   engine reclaimed it at 2001 ms, with zero tracks/orphans. Ready/deadline-expired orphans can request UI work;
   `RecommendedWaitMs` bounds visible idle waits by the existing two-second wall backstop. Minimized windows still sleep.
4. Forced reclamation ran after the animation tick and left dead-node tracks waiting for a future tick. Node teardown
   now retires its tracks through `FreeSlot`, including key storage and parked-count bookkeeping.
5. Four UI tracks are **not** evidence of four active render animations. Render-owned copies advance independently;
   both the entering and exiting Dy/opacity pairs remain in the UI desired set until feedback imports. A new gate
   proves four completed UI rows can coexist with `renderer.HasActive == false`; feedback clears all four.
6. Under playback, Liked Songs with its lyrics rail consumed **56.3% GPU** in a settled 12-sample run. The 30-second
   repaint census recorded **3601 full / 0 partial** submits with only **2.04% mean damage**, all rejected by the
   backend. Its static heart stencil globally vetoed partial repaint. The scanner now admits balanced stencil scopes
   only when stencil and layer scopes do not nest inside each other. Acrylic and cross-kind nesting remain vetoed.
   The on-device baseline was 8/10 passing, two new stencil cases inconclusive; the fixed probe was **10/10 byte-identical**,
   no dropped instances, including nested/fractional stencil damage and sibling blur.
7. Navigation during continuous lyric updates exposed two damage-history retention bugs. One shared dirty timestamp
   let a new descendant update retain an old ancestor self-content mark forever, causing **100% damage every frame**.
   Independent contribution lifetimes fix that. A third snapshot used during a handover could also be abandoned by
   first-fit slot selection, pinning the retention floor forever. The writer now refreshes the oldest initialized
   writable snapshot; generation-checked claims and consumer ownership remain unchanged. Its new regression test
   fails against the old engine.

The core scheduling change uses the existing predicates and timeout:

```csharp
if (_scene.OrphanCount > 0 && (!_anim.RenderOwnsCompositor || HasReclaimableOrphan()))
    r |= WakeReasons.Orphans;

// Within RecommendedWaitMs, visible render-owned exits clamp the existing wait:
int dueIn = (int)Math.Ceiling(Math.Max(1.0, OrphanSettleTimeoutMs - ageMs));
w = w < 0 ? dueIn : Math.Min(w, dueIn);

// Node retirement cannot depend on another UI tick:
while ((slot = _slab.HeadOnNode(index)) >= 0) FreeSlot(slot);
```

Always-on `[wake]` diagnostics now distinguish UI desired tracks from render motion, actual present deltas and pending
feedback. `[repaint]` reports partial/full submit counts, backend rejection, empty-damage mismatch and mean damage.
Wavee's log filter admits that line at its normal file threshold. Neither requires an environment switch.

Detailed engine code and probe design: `../fluent-gpu/docs/plans/idle-gpu-render-reclaim-implementation.md`
(sibling repository; the relative spelling here describes repository layout).

## Verification record

Intermediate verification before the final damage-history changes:

- Engine and app Debug/Release builds: zero errors; full rebuilds surfaced existing analyzer warnings.
- VerticalSlice Debug/Release: 1526 checks passed.
- Engine tests: 307 passed. Windows tests: 227 passed.
- Wavee tests: 8016 passed, one skipped.
- Release Pester: 330 passed, one skipped.
- Canon drift check passed.

Final engine verification after the damage-history and stencil changes:

- Debug/Release full builds: zero errors; existing analyzer warnings remain.
- VerticalSlice Debug/Release: **1534 checks passed**. The old stencil framing gate now expects admission of its
  balanced layer-disjoint scope; cross-kind nesting is still rejected by behavioral damage gates.
- Engine tests: **308 passed**. Windows tests: **227 passed**.
- Real-device identity: **10/10 identical**, zero mismatch, inconclusive cases or dropped instances.
- Render-owned stress: **48 flips, 107 samples, zero failures**, peak four tracks/one orphan, settled zero/zero.
- Final timeout probe: **2015 ms**, deadline armed, reclaimed, zero tracks/orphans, 60 additional presents.
- Negative controls: four dirty-lifetime gates failed on the old engine; continuous child damage was 100% there
  and **3.08%** with the fix. New slot-retention test also failed on the old engine.
- Canon drift check passed.

App Debug/Release builds passed; Release tests: **8016 passed, one skipped**. Native ARM64 publish succeeded.

### First native load pass (before follow-up fixes)

Same 1792×1151 physical window, process 25540, one active 3D counter instance, no concurrent build/probe:

| Workload | Samples | Mean / peak GPU | Normalized CPU | Working set |
|---|---:|---:|---:|---:|
| Liked Songs + syllable lyrics | 15 | 11.78 / 24.39% | 1.22% | 603 MB |
| Playlist + syllable lyrics | 12 | 8.43 / 9.43% | 1.43% | 531 MB |
| Alternating playlist wheel + playback/lyrics | 15 | 44.26 / 59.00% | 1.94% | 541 MB |
| Expanded lyrics | 12 | 41.77 / 60.31% | 1.52% | 625 MB |

The scroll driver delivered 389 wheel events over 18 seconds, reversing every two seconds. One captured scroll
window recorded 120.4 fps, zero over-budget frames, zero 33/100 ms stalls, 326 presents and one missed vblank.
Warm lyric clock windows recorded about 3600 frames/30 seconds, zero snaps, and maximum steps of 9–11 ms.
This verifies cadence, not an independently measured audio-to-light latency.

These were **intermediate, insufficient** results, as the user correctly noted. Follow-up diagnostics found:

- Pausing after expanded lyrics left the ticker running. Audio paused and the local projection published false,
  but a host PositionTick overwrote it with playing=true five milliseconds later. The ticker was obeying bad state.
- Hundreds of canvas rebuilds were labeled PublishGap even after adopted frames had been proven byte-identical and
  elided. Render-consumer continuity must carry that proof to the next actual submission.
- Raw damage coverage counted off-target area before replay clipping. A 100×10100 rectangle with only 100×100 visible
  forced FullDirect on the old policy; the regression test fails there. Coverage admission now follows clipping.
- Native memory had about 125 MB managed heap and 87 MB explicitly tracked GPU resources; DXGI reported additional
  residency not explained by these counters. Do not add those overlapping numbers or promise an arbitrary WS target.

### Follow-up implementation and gates

`AudioHostSignalGate` serializes intent edges and host signal publication. `Pause` publishes an explicit Paused
signal synchronously; reports carrying playing/buffering intent cannot undo it while the physical session operation
is pending. No asynchronous operation is held under its lock. Projection callbacks release their state lock before
notification; reentrant and concurrent publication tests cover the ordering. Ten cases pass, including the real
`NowPlayingProjection` receiving a late PositionTick sampled after pause. The lyric ticker itself is unchanged.

`SeekBar` now observes PositionMs in its existing signal-effect mechanism, instead of rebuilding the component for
each transport sample. Paused scrubbing and normal playhead updates must still be verified live.

`RenderSubmissionContinuity` carries proof of visually unchanged elisions to the next real GPU submission without
weakening unknown-gap rejection. `RepaintPolicy` clips before deciding coverage, and D3D's pre-scan raw-coverage veto
is removed too. The added `offscreen-damage-visible-tail` real-device case passes: **11/11 byte-identical**, zero
mismatches, inconclusive cases or dropped instances.

Glyph upload banks retain their 1024-row cold reserve, then shrink to 256 after the existing 120/600 clean submitted
frame threshold, changing tracked reserve from 12 to 3 MiB. Upload demand is frame-local (not historical peak), and
real uploads reset the clean-frame counter. Five tests cover threshold, repeated scroll bursts, no tiny-upload
regrowth after a cold peak, and pixel-perfect overflow drain. Resource replacement remains fence-protected.

Follow-up gates: engine Debug/Release **1536** checks, **313** engine tests, **227** Windows tests; app Release
**8026 passed / one skipped**. Canon check passes. Full rebuilds still surface pre-existing analyzer warnings.
Native runtime measurements follow after publish; no whole-working-set reduction is inferred from buffer sizes.

## Artwork and paused-stage follow-up

The user reported artwork flashing during audio-only playback. A real log window showed 2234 full submissions
among 2311, with render motion active despite zero UI animation tracks, orphans and frame-clock subscribers.
The image cache used clamped/resynced animation delta while rendering extrapolated wall time; a sparse publication
could rewind a reveal. The shared cache now owns an unclamped wall clock and captures its sample timestamp with
image metadata. Both regular and idle completion pumps sample first. Popouts share the same epoch. Image reveals
finish while hidden, without drawing; authored animation pause behavior is unchanged. Four behavioral tests cover
sparse publication, restore ordering, late completion and shared-cache ownership. Final native verification below
must use this shared-clock revision, not the earlier per-host draft caught during review.

The immersive backdrop's 30 Hz decorative timer previously continued while paused, costing about 22% GPU in one
native sample even after the lyric ticker correctly unmounted. `StageDriftClock` holds the last sampled phase;
`runDrift = drift && playing && art.Length > 0` disables the interval while paused. Four tests cover start, resume,
repeated cycles and reset. The 37/53-second drift periods and active-playback presentation are unchanged.

The requested high code audit / medium web-research comparison is in
`cpu-audit-comparison-20260912.md`; the final high-agent implementation plan is separate. Audio polling, unused
meter/DSP passes, atlas capacity and sparse snapshot changes remain proposed, not implemented here.

### Latest verification and native observations

Shared-clock revision: engine Debug/Release builds succeeded; isolated VerticalSlice runs both passed **1536**
checks; **317** engine tests passed. App Debug/Release builds succeeded, **8030 tests passed / one skipped**.
Existing analyzer warnings remain. Loaded concurrent runs had shelf-binding/toggle timing failures; isolated
runs passed. Real render stress: **48 flips / 107 samples / zero failures**, final tracks/orphans/presents quiet.
The preceding pixel probe was **11/11 identical**, zero inconclusive/mismatched/dropped instances. Canon is clean.

Native ARM64 PID **15176**, current shared-clock publish, no builds/probes during counters:

| Window | Samples | Mean GPU | Normalized CPU (12 logical) | WS |
|---|---:|---:|---:|---:|
| Audio only, lyrics closed, cached album with empty rows | 15 | 0.443% | 0.230% | 399.2 MB |
| Paused expanded lyrics, foreground changed during capture | 12 | 2.136% | 0.085% | 476.8 MB |
| Paused minimized | 10 | 0.000% | 0.000% at process timer resolution | 476.7 MB |

The empty album scene is not comparable to a fully populated Liked Songs workload. Visible paused-stage GPU is
**unresolved**, not a clean idle result. Another application became foreground during the screen capture, so that
image does not verify Wavee's visible state; no content from that application belongs to this investigation.
Automation stopped with Wavee paused/minimized. No claim that maximum CPU/memory optimization is finished.

Artwork screen sampling: Metallica stayed unchanged for 224 samples; a Green Day transition changed as expected,
had additional early disturbances up to 5.9 s (not proven fade rewinds), then a subsequent settled 218-sample
window was completely unchanged. Together with the clock tests this verifies the corrected mechanism and settled
stability, but does not prove every transient image path pixel-perfect.

### Cached album empty at startup — diagnosed, not patched

User reported PURE (`spotify:album:71TimGdYvnolc8o298RVxs`) showing its cached header and "Nothing here yet".
The startup route appeared at log timestamp 1789209542976, roughly 1.7 seconds before live backend installation
at 1789209544690. `DetailPage`'s route-keyed initial resource can read through `OfflineEntityHydrator`;
`StoreLibrarySource.GetAlbumAsync` awaits but does not inspect the outcome. Subsequent bulk refresh is deliberately
`HydrationLevel.None`, so it cannot fetch missing membership. There is no live-ready retry on this page path.
Navigating home then back produced the real **13 tracks**; the live envelope log confirms 13 and the screenshot
shows populated rows. This is a loading/readiness defect, not evidence of an empty album or memory-trim corruption.
No production change to this hydration path has been made. The maximum-optimization plan records its correctness
guard separately; a future fix must use the existing hydration façade and avoid a bulk-refresh fetch loop.

## Live workload procedure

Use the existing activation intake (`WM_COPYDATA`, cookie `0x46474143`, UTF-16 byte length **excluding** terminator).
Including a terminal NUL corrupts the route/track key; this was a driver error, not a production transport change.
Play `spotify:track:3ZFwuJwUpIl0GeXsvF1ELf`; the existing log identifies a Kugou syllable document for that track.
Open lyrics, compare Liked Songs and playlist navigation, wheel the list and lyrics separately, expand lyrics, then
pause and measure restored/minimized idle. Do not attribute a 120 Hz lyric clock to an idle subscriber leak.

Read process-specific GPU 3D counters per instance before summing; this machine's observed active instance is
`phys_0_eng_0`. Record CPU normalized across logical processors, working set, GPU samples, and matching log windows.
Avoid concurrent builds or GPU probes during performance measurements. Record render presents separately from UI
reconcile/layout counts. Never claim the original healthy-flip incident reproduced: the evidence above proves the
timeout failure and the load-related repaint failures independently.
