# Wavee memory-floor handoff — 2026-09-12d (tooltip clock, edge-fade scratch, factorial probe)

Continuation of `handoff-20260912c-memory-floor.md`. Everything in `C:\wavee\waveemusic` and `C:\wavee\fluent-gpu` is
still **uncommitted user work**. Run `git status --short` in both before touching anything; never reset, stash,
checkout, commit or discard. Only the orchestrator builds, tests, publishes, launches or monitors.

## What this session did

### 1. Realistic driver-memory factorial probe — DONE, 39 native runs

`src/FluentGpu.WindowsApp/Probes/DriverMemoryFloorProbe.Factorial.cs` gained a `stencil` family (one full-window
tessellated rounded-rect `PushStencilClip`), and `.tmp-msbuild/run-driver-floor-factorial.ps1` +
`summarize-driver-floor-factorial.ps1` run and summarize it. Native ARM64, 1770×1140 physical at scale 1.5, composited,
3 buffers, latency 1. Each arm: 16 common mixed frames → 5 s idle → 240 selected frames → return-minimal → 5 s idle →
240 rewarm frames → 5 s idle. Three repetitions per arm; **every DXGI delta had zero spread across repetitions.**

Pass 1 (before this session's fixes), `selected-<family>-retired` minus `common-mixed-retired-idle-end`, medians, MiB:

| family   | route    | ΔDXGI | Δtracked | Δuntracked | ΔWS  | after idle | after rewarm+idle |
|----------|----------|------:|---------:|-----------:|-----:|-----------:|------------------:|
| mixed    | direct   |  -4.5 |     -4.5 |        0.0 |  0.6 | 0.0 | 0.0 |
| mixed    | retained |  -4.5 |     -4.5 |        0.0 |  0.7 | 0.0 | 0.0 |
| images   | direct   |  13.8 |     13.8 |        0.0 | 31.2 | 0.0 | 0.0 |
| images   | retained |  13.8 |     13.8 |        0.0 | 31.2 | 0.0 | 0.0 |
| opacity  | direct   |  11.4 |     10.9 |        0.5 | 16.7 | 0.0 | 0.0 |
| opacity  | retained |  27.4 |     10.9 |   **16.5** | 33.1 | 0.0 | 0.0 |
| blur     | direct   |  11.4 |     10.9 |        0.5 | 20.8 | 0.0 | 0.0 |
| blur     | retained |  11.4 |     10.9 |        0.5 | 20.8 | 0.0 | 0.0 |
| edgefade | direct   |  51.8 | **51.5** |        0.2 | 57.0 | 0.0 | 0.0 |
| edgefade | retained |  51.8 | **51.5** |        0.2 | 57.2 | 0.0 | 0.0 |
| stencil  | direct   |   3.5 |      3.2 |        0.3 |  9.3 | 0.0 | 0.0 |
| stencil  | retained |   3.5 |      3.2 |        0.3 |  9.4 | 0.0 | 0.0 |

Readings:
- The route (direct vs retained/partial) costs nothing by itself; retained stencil is admitted and costs the same as
  direct.
- **Nothing is ever returned inside the renderer-only probe**: after return-minimal, 5 s idle and rewarm every arm keeps
  its high-water. The pool has no idle trim of its own; in the app, E1 UI cold maintenance does that after 30 s. That
  is the "render-owned retirement" question from the previous handoff: the probe has no AppHost, so it cannot exercise
  E1, and the evidence does not show any *additional* need beyond E1.
- Pass 1's untracked floor is 36.0 MiB in every arm (device + swapchain + driver), unchanged by any workload except
  opacity-retained.
- Two defects fell out, below.

Receipts: `.tmp-msbuild/factorial-pass1/driver-floor-factorial-<family>-<route>-<run>.stderr.log` (pass 1, plus the
`classic-direct` control), `.tmp-msbuild/driver-floor-factorial-{edgefade,opacity}-*-<run>.stderr.log` (pass 2 on the
fixed probe `memory-floor-native-probes-factorial2`).

### 2. Edge-fade strip scratch was 56 MiB for ~137 kpx of strips — FIXED (engine)

`OpacityLayerCompositor.StripPack` stacked the four edge strips vertically at the widest strip's width, so a full-window
four-edge fade at 1770×1140 with 24-px bands asked for 1770×4464 and bucketed to 1792×8192×4 = exactly 56.0 MiB
(`rt: free=56.0/1` in the receipt). New `FluentGpu.Render.EdgeFadeStripPack` (engine, TerraFX-free) is the one placement
contract: wide bands stack on a column, tall bands sit side by side on a shelf; lease size, copy destination and the
restore shader's source offsets all call `Measure`/`Place`. Six pure tests in `FluentGpu.Windows.Tests/EdgeFadeStripPackTests.cs`.

Pass 2 on the republished probe: edgefade ΔDXGI 51.8 → **23.6 MiB**, `rt: free=28.0/1` (1770×2280 request, bucketed to
1792×4096). The remaining 1.8× is `LayerTargetBucket`'s power-of-two ladder above `LinearCeiling = 2048` rows. Two ways
to get the last ~12 MiB, not done: raise `LinearCeiling` (affects every pool consumer) or place the D/F halves side by
side (3540×1140 → 18 MiB, needs a second shader constant in `_edgeStripPso`). `--repaint-identity` on the real GPU:
11/11 identical including `edge-fade-strip-straddle` (0 px vs full redraw).

In the live app every track list and the sidebar carry edge fades, so this is a real-app saving whose size depends on
the fade's box; it is NOT yet measured on Wavee (see "blocked").

### 3. ToolTipClock frame poller — FIXED (engine)

`ToolTipClock` was `UseContext(FrameClock.Tick)` + an invisible Opacity 1→1 tween polled per frame. It is now one
`UseTimeout` on the host timer queue: no frame-clock subscriber, no track, no re-render. Same delays (800 ms show,
5 s dwell, 1 s safe-zone, `Motion.ControlFaster` for CommandBarFlyout close, keyed re-arm for MenuFlyout cascades).
Gates added in `OverlaySuite.ToolTipTimerChecks`: `gate.tooltip.timer-quiet` (one timer entry armed; 0 component renders
and 0 frame-clock pollers across 10 pumped frames while pending AND while open+settled; closed at 799 ms, open at
801 ms; auto-dismissed after 5 s), `gate.tooltip.timer-leave-cancels`, `gate.menu.cascade-timer-rearm`,
`gate.cbf.close-timer`. The four legacy tooltip gates (`e4popup.7*`) now drive the countdown with `Paint` after
advancing the manual clock, because a pending-not-due timer sets no wake bit (that is the point).

Live evidence of the defect, candidate session `pid=10144`, 12 s scroll window:
`[wake] ... kept: ... frameClockPoller=463 ...` — the poller held 463 frames awake. Expect `frameClockPoller` absent and
`pollers=0` in the same scenario on the new candidate.

### 4. Truthful allocation accounting for render targets — DONE (engine)

`D3D12MemoryDiagnostics.AllocationBytes(device, desc)` (device-reported `GetResourceAllocationInfo`) now sizes every
`Track` for opacity-layer, acrylic and baked-blur targets, the glyph atlas and the stencil DSV; unknown → the pixel
formula labelled `.AllocationUnknown` exactly like images. Swapchain back buffers still use `W*H*4` (no desc of ours to
query). Effect on the probe: tracked rose by 0.5 MiB in the opacity arms — so RT padding is real but tiny.

### 5. The opacity-retained 16 MiB — DIAGNOSED to a frame, NOT fixed

Timeline in `driver-floor-factorial-opacity-retained-1.stderr.log` (pass 2): untracked is 35.8 MiB through frames 1
(FullDirect, two pool RTs created), 2 (FullIntoCanvas) and 3 (Partial, `OpacityGroups = 2`), then **51.8 MiB by frame
10 with no new tracked resource** (`tracked_resources` stays 34), and it never returns. Direct-route opacity, and every
other family on the retained route, stay flat. So it is driver-internal memory triggered by steady partial frames that
carry two nested full-window opacity groups. The one thing that path does which no other arm does is clear the layer
targets with a partial RECT (`OpacityLayerCompositor.Acquire` → `ClearRenderTargetView(rtv, clear, 1, clearRect)` at
~line 967; the scratch RECT is filled in `D3D12Device` near line 3755). Hypothesis: the Adreno driver attaches a
decompression/shadow surface (2 × ~8 MiB) to an RT that receives rect-scoped clears. Experiment for next session: a
probe-only diagnostic seam that makes the canvas-route opacity clear full-target (or a scissored clear-quad draw) and
rerun `opacity retained` ×3; if untracked stays at 35.8 the hypothesis holds and the fix is to replace the rect clear
on that path. Real-app relevance: Wavee's lyrics surface and hover fades open opacity groups during partial repaint.

### 6. Real-app memory evidence from the candidate's full session (read this before the next matched runs)

`%LOCALAPPDATA%\Wavee\logs\wavee-20260912.log`, `pid=10144`, 1607 `mem.sample` lines (2 h 10 min):
- **Idle-playing managed growth**: on the album page with playback and lyrics, components constant (324), gen2 grew
  from 70.7 to 132.3 MiB over ~47 min (≈1.3 MiB/min) with fragmentation climbing to 93 MiB, then a compaction dropped
  the heap from 156 to 66 MiB and the climb restarted. Image count crept +1 every ~2 min (101 → 125).
- **Navigation is the step function**: three large-page visits added +244/+265/+188 image cache entries and +48/+10/+24
  MiB of LOH; the session ended at 825 images, LOH 107 MiB, gen2 165 MiB, heap 275 MiB, WS 630 MiB (peak 803), tracked
  GPU 106.5 MiB. Managed heap, not GPU, is the dominant and growing term.
- Neither is covered by the engine fixes above. The plan's C4 (LOH churn) and D2 (image pipeline budget) sections are
  the owners; the idle-playing growth needs a same-PID allocation-stack capture during pure playback (what allocates
  ~1.3 MiB/min with nothing on screen changing).

## Gates run (all green)

Engine (`C:\wavee\fluent-gpu`): Debug + Release build 0 errors; Engine.Tests 405/405; Windows.Tests 241/241 (+6);
VerticalSlice 1540/1540 Debug and Release (+4 tooltip gates; the Release run crashed ONCE with a NullReferenceException
in `HooksSuite.ResourceChecks` line 2105 — a 3 s `SpinUntil` timing out under a concurrent AOT publish, passed on the
quiet rerun); `check-canon.ps1` clean (33 docs); `--repaint-identity` 11/11 identical.
Wavee: Debug + Release build 0 errors (pre-existing test-project analyzer warnings only); Wavee.Tests 8045 passed,
1 skipped. CHANGELOG `[Unreleased]` has bullets for the tooltip clock and the edge-fade scratch (#118).

Logs: `.tmp-msbuild/tooltip-*.log`, `.tmp-msbuild/pass2-*.log`.

## Candidates and where they live

| Binary | Path | SHA256 |
|---|---|---|
| Old baseline | `src\apps\Wavee\bin\publish-aot-symbols\Wavee.memory-baseline.exe` | `0361F6D6…F834061` |
| D1/E1 candidate (was PID 10144) | `…\publish-aot-symbols\Wavee.exe` and a copy `Wavee.d1e1-candidate.exe` | `AE56A472…7A57CC9C` |
| **This session's candidate** (tooltip + edge-fade + accounting) | `src\apps\Wavee\bin\publish-aot-tooltip\Wavee.exe` (+ `modules\`, notices, PDB) | `2293B6E9E09B577BDB8F867A65BAA3076B1C756558994C63046D2893AB773D19` |

The candidate was published to a SIDE folder because `publish-aot-symbols\Wavee.exe` was locked by the user's running
instance. Direct `dotnet publish -o <side>` needs the engine projects prebuilt with `/p:DebugType=portable` first
(otherwise MSB3030 on the engine PDBs); the exact chain is in `.tmp-msbuild/pass2-*` logs and in this session's memory.

## Live Wavee re-verification (handoff items 3 and 7) — DONE for the candidate above

The user closed PID 10144 and the runs below happened on the same profile, which now auto-resumes playback at launch
(both variants show playback GPU load in their "idle" windows; the pairs stay matched).

Three alternating matched pairs, fresh process each, 60 s settle + 60 s measure per route, WM_CLOSE at the end
(`.tmp-msbuild/memory-floor-matched60-{baseline,candidate}-{1,2,3}.result.log`), MiB:

| run | variant | home WS | album WS | album private | peak WS | album GPU % | album CPU % |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | baseline  | 618 | 653 | 593 | 694 | 6.2 | 0.80 |
| 1 | candidate | 518 | 518 | 443 | 532 | 4.5 | 0.26 |
| 2 | baseline  | 526 | 527 | 472 | 545 | 4.5 | 0.49 |
| 2 | candidate | 506 | 506 | 437 | 525 | 4.6 | 0.25 |
| 3 | baseline  | 530 | 532 | 479 | 565 | 4.8 | 0.44 |
| 3 | candidate | 508 | 514 | 448 | 525 | 4.3 | 0.53 |

Medians: album WS 532 → 514, private 479 → 443, peak 565 → 525; tracked GPU on the album page 75.1 → ~61 MiB
(`mem.sample` `gpu bytes=`). Baseline run 1 is a high outlier (its own wake census shows `frameClockPoller=476`, i.e. a
tooltip pending while the cursor rested over the window); every candidate run is below every baseline run anyway.
This is a real but modest process-memory improvement; it is not the memory floor fixed.

Scenario pass on the candidate (`.tmp-msbuild/candidate-scenarios-60.ps1`, receipts `scen-tooltip-*`, pid 12004):

| scenario | GPU mean % | CPU % | WS MiB |
|---|---:|---:|---:|
| album, playback auto-resumed, 30 s settle | 4.20 | 0.21 | 467 |
| playback + lyrics panel open (track intro, no active line) | 0.82 | 0.84 | 449 |
| playback, lyrics closed | 0.53 | 0.39 | 441 |
| after 12 s oscillating wheel scroll (255 events), 5 s later | 0.53 | 0.42 | 445 |
| minimized during playback | 0.00 | 0.19 | 446 |
| restored (restore step 666 ms incl. screenshot) | 0.52 | 0.36 | 447 |

Artwork watch: 216 samples over 12 s, 0 changes. `nav.frames` album: first frame 109 ms, 169 frames / 4 s, 5 over
budget, 0 stalls. Lyrics clock: 3601 frames, 1 zero-advance frame, 3 snaps. Thread sample (no lyrics, 30 s):
`fgpu-ui` 11.5 ms/s, `fgpu-render` 10.9 ms/s, `AudioProducer` 9.4 ms/s.

**Tooltip poller gone**: no `[wake]` window of any candidate process names a tooltip poller; the scroll window shows
`pollers=0` at its end.

**Pass 3 — the third candidate (`2293B6E9…` + lyrics motion demand + scrollbar dwell timer + `pollersSeen` census),
SHA256 `D378BFEF72458A52D7DFE7D3E4568B579CC2D497A8047FD5057E3F83A87A6EE9`, same side folder, receipts `scen-cand3-*`, pid 4296:**

| scenario | GPU mean % | CPU % | WS MiB | wake census for the window |
|---|---:|---:|---:|---|
| album, paused, 30 s settle | 0.00 | 0.03 | 414 | `fps=0.5 … sole: timer=15 | pollersSeen=0` |
| lyrics open, intro (0:30-1:00) | 0.79 | 0.43 | 414 | `pollersSeen=1:LyricsFrameStepper×1` then `pollersSeen=0` — one frame to decide, then quiescent |
| lyrics open, verse (1:20-1:50) | 7.39 | 1.13 | 401 | `fps=101 … pollersSeen=1:LyricsFrameStepper×2940` — the wipe steps per produced frame by design; screenshot shows the correct active line at 1:21 |
| lyrics closed | 0.54 | 0.30 | 420 | `pollersSeen=0` |
| 12 s oscillating scroll (255 events) + 5 s | 0.54 | 0.42 | 421 | `scrollAnim=1858 … pollersSeen=0` — the anonymous 244-frame poller is gone |
| minimized | 0.00 | 0.24 | 425 | `pollersSeen=0` |
| restored (702 ms step incl. screenshot) | 0.54 | 0.40 | 388 | `pollersSeen=0` |

Artwork 214 samples / 0 changes. Lyrics clock 2970 frames, 0 snaps (zeroAdvance 130, maxStep 209 ms — the media
clock is now sampled only when a step runs, so gaps show as larger steps, not as resyncs). Threads (no lyrics):
`fgpu-render` 11.5 ms/s, `fgpu-ui` 9.4 ms/s. Gates for this candidate: engine Debug/Release clean, Engine.Tests
405/405, Windows.Tests 241/241, VerticalSlice 1546/1546 both configs, canon clean; Wavee Debug/Release clean,
Wavee.Tests 8062 passed / 1 skipped.

Open from this pass: while minimized the loop still produced ~500 frames per 30 s (`sole: frameNeeded=426`,
`minimized=…`) with GPU at 0 — worth a look at what marks `frameNeeded` while iconic. The "Drag and drop your files
here" overlay is NOT Wavee: the string exists in neither repo; some other program on this machine reacts to the
synthetic `mouse_event` click.

**Two more pollers found by the census in pass 2, both fixed in pass 3:**
- `pollers=1:LyricsFrameStepper` — with the lyrics panel open during the intro: `fps=138.2 run=4146 rendered=61
  recordOnly=4085 … sole: frameClockPoller=3407`. `LyricsView.cs` ~3128 mounts the stepper on `playing`, not on
  motion. Fix: a pure `LyricsMotionDemand` decision + one-shot re-arm to the next syllable/line deadline.
- In the scroll window, after the lyrics were closed: `frameClockPoller=500 | sole: frameClockPoller=244` with
  `pollers=0` by the end — `ScrollBarConsciousTicker` (`ScrollBar.cs` ~560) polls the frame clock through the
  scrollbar's ~2 s idle-hide dwell. Fix: dwell → `UseTimeout`, settle without a frame-clock subscriber.
- Artifact to check by hand: after the synthetic `-Click` on the lyrics button a "Drag and drop your files here"
  overlay appeared mid-window and stayed (`scen-tooltip-lyrics.png`, `scen-tooltip-no-lyrics.png`). No log line
  mentions it and the string is not in `en-US.json`; it may be a drag-enter overlay triggered by `mouse_event` at
  the window edge. Not investigated.

The commands used, for the next candidate:

```powershell
# matched 60/60 s runs, three alternating pairs (fresh process each, WM_CLOSE at the end):
foreach ($i in 1..3) {
  & C:\wavee\waveemusic\.tmp-msbuild\memory-floor-matched-run-60.ps1 -Variant baseline  -ExeName Wavee.memory-baseline.exe -Repetition $i
  & C:\wavee\waveemusic\.tmp-msbuild\memory-floor-matched-run-60.ps1 -Variant candidate -ExeName Wavee.exe -Repetition $i `
      -PublishDir C:\wavee\waveemusic\src\apps\Wavee\bin\publish-aot-tooltip
}
```
Invoke it with `&` from a PowerShell prompt (not `powershell -File`, see the session memory). Then the scenario runs from the previous handoff with `idle-gpu-live.ps1`
(playback with lyrics open/closed, scroll 12 s with `-ScrollSeconds 12`, `watch-artwork.ps1`, minimize/restore, idle),
and read `[wake]` for `pollers=0` and no `frameClockPoller=` during the scroll, and `mem.sample`'s `gpu … top=` for
`OpacityLayer.Pool` / edge-fade scratch sizes.

## Remaining, in order

1. Live re-verification above (needs the user).
2. Opacity-retained rect-clear experiment (§5).
3. Idle-playing managed growth capture (§6) — likely the largest real-app term.
4. Optional: edge-fade D/F halves side by side or a finer bucket ladder (§2).
5. Not done from the previous handoff: the render-owned keyed Enter/Exit orphan reproducer and the `animTracks=4`
   explanation; nothing in this session's evidence changed their priority.
