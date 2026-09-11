# Scroll feel investigation — 2026-09-10

**Status: investigation complete enough to act on; fixes not started.** Branch `release/0.2.9-perf` (worktree
`C:\WAVEE\wavee-0.2.9`), engine `feat/ultra-fast-engine` at `C:\WAVEE\fluent-gpu`.

The report: *"scroll performance feels sluggish even though the numbers say high fps; touchpad scrolling sometimes
gets blocked or does not work as intended; on the external monitor the app feels like 24 fps and the animations are
bad."* The ask: a thorough, data-grounded investigation, including how WinUI's DirectManipulation model differs.

Three measured mechanisms came out of it, two of them engine bugs in the production clock, one an OS-side gap that
still needs one more capture. The earlier suspicion that DirectManipulation chops continuous pans was tested and
**withdrawn** (§3.2). Tools built for this and kept in the repo:

- `ops/tools/scroll-input-probe/` — a standalone Win32 window recording, on one QPC clock, every stage a two-finger
  pan or wheel notch passes through BEFORE any engine: raw HID touchpad reports (WM_INPUT, INPUTSINK), DM hit-tests /
  status edges / content deltas, WM_POINTERWHEEL, cursor/foreground state, and the compositor tick. Options:
  `--engine-pump` (pump DM exactly like `Win32DirectManipulation`), `--dm-inertia` (the XAML configuration),
  `--content-rect` (a 1e6 px content rect), `--no-dm`, `--dm-auto`, `--no-mip`, `--seconds N`, `--out <dir>`.
  **Launch it detached** (`Start-Process`); run inline from a sandboxed tool it gets no input at all.
  Captures live under `ops/tools/scroll-input-probe/captures/` (gitignored).
- `ops/tools/scroll-capture.ps1` — runs the `FluentGpuDiag=true` build of Wavee with the engine's diagnostic gates
  (`FG_SCROLL_LOG`, `FG_SCROLL_TRACE`, `FG_FPS_LOG`, `FG_SCROLL_PERF`) and collects the engine-side pipeline into one
  folder. Build: `dotnet build src\apps\Wavee\Wavee.csproj -c Release -p:FluentGpuDiag=true -o
  src\apps\Wavee\bin\verify\diag`. **Its timings are not representative**: the diag arms re-run scene captures from
  scratch (`[fg-capture-parity]` fired 60+ times) and inflate `submit`; use it for *what happened*, the shipped build's
  own `scroll.frames` / `frame.slow` lines for *how long it took*.

---

## 1. Machine facts

| Fact | Value | Source |
|---|---|---|
| GPU | Qualcomm Adreno X1-85, UMA, tier=Weak, 128 MB VRAM | app log `[d3d12.adapter]` |
| Internal panel `\\.\DISPLAY1` | 2496×1664 @ **120 Hz**, primary | probe monitor rows |
| External AOC Q24G4 `\\.\DISPLAY8` | 2560×1440 @ **50 Hz**, DisplayLink (USB, indirect display driver). 2560×1440 offers **only 50 Hz** | `EnumDisplaySettings` |
| Touchpad | precision touchpad, 122–124 Hz digitizer (HID gap p50 8.0 ms) | probe `hid` rows |
| Compositor clock | `DCompositionWaitForCompositorClock` measured **120 Hz regardless of which monitor the window is on**; it also **bursts sub-millisecond returns** (streaks of 9, 15 and 21) around monitor/DPI changes and occasionally without one | probe `tick` rows |
| Running app | `C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe` (unpackaged) | `Get-Process` |

## 2. How the engine's scroll path is built (as-built, file:line today)

**Input.** `EnableMouseInPointer(true)` (`Win32Platform.cs:681`, return unchecked); no `WM_MOUSEWHEEL` handler —
everything is `WM_POINTERWHEEL` or DirectManipulation. Pump = `PeekMessage`-to-empty once per `RunFrame`
(`:1156-1168`) on the UI thread; every message goes to `ProcessInput` first. While DM is `Live`, wheel packets that
are not a positively identified physical mouse are swallowed (`:1339-1343`).

**DirectManipulation here vs WinUI.** WinUI gives DM a composition-thread viewport with real content extents and
OS inertia, so a busy UI thread never stalls the pan. This engine uses DM as an **input source only**
(`Win32DirectManipulation.cs`): manual-update mode, no `TRANSLATION_INERTIA` (`UseOsInertiaStopFallback = false`,
config `:77-88`), no content rect (`:106-108`), ONE `Update` per produced frame from `PumpScroll`
(`Win32Platform.cs:1185-1190`, inside `AppHost.Paint` after the production gate), deltas re-emitted as
`ScrollBegin/ScrollDelta/ScrollEnd` for the engine's own kernel; inertia is engine-owned (`ScrollKernel.ApplyContactEnd
:645-722`). `Live` (`:207`) = engaged/pending or RUNNING; INERTIA is not Live.

**Frame production.** `RunFrame` builds one `FrameClock` from the compositor tick (`RefreshLattice`), pumps once,
dispatches, then `ProductionGateBlocks` (`AppHost.cs:1947-1953`) allows one produced frame per compositor tick. The
tick is `DCompositionWaitForCompositorClock(0, null, 100)` on the `fgpu-vblank` thread
(`Win32CompositorClock.cs:186`) — the **DWM-global** clock. Without an available clock the host falls to
`HostWaitKind.SoftwarePace` (`AppHost.cs:2152-2162`): a wall-clock wait of `floor(refreshMs) − 1` ms
(`DeriveAsyncPaceMs`, `:1896`) — 7 ms at 120 Hz — with no vblank phase at all. The clock becomes unavailable on
`WAIT_FAILED ×3` or **16 consecutive sub-millisecond returns** (`FastStreakLimit`, `:44, :215`), permanently for
the session except `Reprobe()` on a display change (`Win32Platform.cs:1655-1658`). The window's own refresh period
*is* known (`DisplayInfo.ForWindow`, feeds `budgetMs`) but does not pace anything.

**Kernel.** Mouse notch → `Driven` chase, half-life 40 ms; touchpad → one `FrameDelta` per frame 1:1; fling `k ≈ 3.0/s`.
`FrameBudget` arms only for `Drag`/`Ballistic` (`AppHost.cs:3437`): mouse-wheel glides realize unbudgeted.

**Present.** FRAME_COUNT 3, `SetMaximumFrameLatency(2)`, latency waitable + frame fence waited on the **render
thread** (`D3D12Device.cs:1501-1512`; async mode, so `fenceWait` is render-thread time, not UI time). Every scroll
frame is a **full-window repaint**: 7 414 of 7 414 scroll-active frames in the in-vivo capture carried a full-damage
token (`F0 100%` 4 716, `F:DetachedContent` 1 162 = live image crossfades, `F:MissingRemovalExtent` 598,
`F:StructuralInvalidation` 457, `F:ImageContent` 446).

## 3. Measurements

### 3.1 The 50 Hz monitor — production clock bug (confirmed, shipped build)

`%LOCALAPPDATA%\Wavee\logs\wavee-20260910.log`, pid 20052, window on DISPLAY8 (`budgetMs=20.0` — the engine knows
the refresh):

| line | frames | wall | fps | presented | missedVblanks |
|---|---|---|---|---|---|
| nav.frames home navId=36 | 481 | 4000 ms | **120.3** | **480** | 0 |
| nav.frames pl navId=37 | 481 | 4000 ms | 120.3 | 479 | 0 |
| nav.frames liked navId=40 | 474 | 4000 ms | 118.5 | 482 | 0 |
| scroll.frames pl navId=37 | 396 | 4092 ms | 96.8 | 489 | 0 |

**~120 frames produced and presented per second into a 50 Hz panel.** 2.4 productions per shown refresh: the
displayed position advances by 2 then 3 samples in alternation (2:3 pull-down) — the "even cadence, uneven motion"
judder the threading canon already documented for the 1.5–2.6× overproduction case (`threading-render-seam.md:862-905`).
It is also 2.4× the GPU work per shown frame on a Weak GPU that is already near saturation (§3.3). The **explicit
30 fps ambient cap** (`AmbientPowerPolicy.cs:41-43`, deliberately not `HalfRefresh`) is *not* a stray 5:3 pattern
stacked on top, as an earlier pass of this doc claimed: `AmbientFrameWaitMs` already rounds the cap to a whole number
of the window's own refreshes, so on a 50 Hz panel the applied rate is 25 fps (every 2nd refresh), not a raw 30. The
overproduction and judder above are entirely the *non-ambient* production path (§3.1's 120/481 numbers are `nav.frames`/
`scroll.frames`, not ambient loops); `RefreshLattice.PresentQpc` / DM's composition lead assuming the 120 Hz grid is
the whole of "feels like 24 fps on the external monitor". The frames themselves are cheap (avgFrameMs 0.6–1.3).

### 3.2 The touchpad — what DM actually does (probe run 2, 90 s; pass A3, 60 s; in-vivo capture A, 90 s)

Probe run 2 first suggested DM ends manipulations mid-gesture (148 RUNNING→INERTIA edges with "fingers still
down"). Pass A3 with the engine's exact pump policy and finger-level correlation **withdraws that**:

- A slow continuous two-finger pan ran as **one RUNNING segment of 5 095 ms**, uninterrupted.
- The 24 RUNNING→INERTIA edges in that pass were flicks: RUNNING 40–110 ms at 9 000–18 000 units/s, then INERTIA
  with only 3–5 HID reports in ~200 ms (a 124 Hz pad sends ~25 when fingers are down) — the fingers were **off the
  pad**; the "fingersDown" heuristic (≥3 two-finger reports) was too weak. The next stroke landed 200–300 ms later
  and re-entered RUNNING without a new hit-test (1 hit-test for 24 strokes).
- In vivo (`fg-scroll.log`): 37 gestures, RUNNING 50–125 ms, gaps 200–300 ms — the same 3 Hz flick rhythm, each
  handled as ScrollBegin … ScrollEnd. No strikes, no fallback, no wheel lines: the DM recovery ladder never engaged.

So DM **does** enter INERTIA on lift with no inertia configured (the engine comment "DM never reports INERTIA" is
wrong on this hardware) — but the engine's handling of that edge as the lift is *correct*. Two loose ends remain:
`Live` goes false during INERTIA so DM's re-engage on the next stroke is served by the 250 ms idle pump, and
`_haveBaseline` is reset per stroke. Neither is the "blocked" report.

**The blocked strokes** (runs 2, 5 and 6; run 5/6 log cursor + foreground + pointer-message count per window, and the
cursor was over the probe, foreground, every time). Most "blocked" windows are fingers held still while RUNNING
(yTravel ≤ 16 units — nothing to deliver) or fingers resting before the pan starts (the OS waits for movement, then a
hit-test engages in ~10 ms; p95 hit→RUNNING 0.9–1.6 s is purely rest time). The genuine cases:

- Run 5, 26.6–27.9 s: a fresh two-finger landing **while DM was still in INERTIA from a flick 1.2 s earlier**, panned
  552 units over 1.36 s, delivered as **43 WM_POINTERUPDATE** messages and **no DM hit-test** — the OS routed the
  contact as plain pointer input, DM went INERTIA→READY only at the lift.
- Run 2, 30.8 / 31.1 / 32.1 s: three consecutive fresh landings (530 / 416 / 24 units) with no hit-test and no wheel,
  DM READY, right after a 759 ms two-finger rest.

INERTIA-with-fingers-resting is DM's normal "stopped, contacts still down" state and lasts as long as the rest (p50
270–290 ms, max 1.4–2.5 s in every run, pump policy or not — the control run 6 with a per-tick Update showed the same
distribution as the engine-policy runs). The strand happens when the *next* contact lands into that state. This is
OS-side behaviour with a plausible engine mitigation (§5.3).

### 3.3 The 120 Hz panel — what "sluggish" is made of

**Shipped build** (pid 20052, 2.5 h session): only 14 slow scroll frames, flush-dominated (component re-render,
flush p50 8.8 ms), submit ≈ 1.2 ms. Per scroll frame: `fenceWait` p50 6.6 ms (render thread), `gpu` p50 4.2 / p90
6.3 / max 7.0 ms against an 8.33 ms refresh — **the GPU is at 75–85 % of the frame on every scroll frame** because
every frame is a full-window repaint (§2). Any extra GPU work (album-art upload, blur, a crossfade) tips a frame over
into a missed vblank; `session.frames` shows `overRefresh=84` for the session.

**In-vivo diag capture** (`%TEMP%\wavee-scroll-capture-A`): `[fps]` line per scroll frame, 7 414 of them.

| metric | p50 | p90 | p99 | max |
|---|---|---|---|---|
| loop ms | 0.7 | 13.2 | 16.8 | 87.8 |
| `lag` (published − acked frames) | 1 | 1 | 16 | 53 |
| `declD` (production-gate declines) | 0 | 1 | 6 | 26 |
| presentInterval ms (trace `latency.f4`) | 8.34 | 16.1 | 38.8 | — |
| `[fps] wait` kind | `swpace` **5 316** (72 %), `tick` 2 098 | | | |

The slow-frame tail is a diag artifact (§ tools), but the last row is not:

### 3.4 The compositor clock latched off mid-session (confirmed, in vivo)

At **t = 32.4 s** of the capture the main host's wait kind switched from `tick` to `swpace` and **stayed there for the
remaining 58 s**, every scroll frame included. `swpace` = `_window.DisplayClock.Available == false` → the clock was
marked unavailable. The `[compositor-clock] unavailable reason=…` line is emitted through `Diag.Line`, which the app
sinks at **Debug** level (`WaveeLog.DiagSink`, `GpuForensic` whitelist) — below the Info file gate, so it never
reached the log. (Fixed in this worktree: `[compositor-clock]` lines are now Warning.)

The probe shows why: `DCompositionWaitForCompositorClock` returns sub-millisecond in bursts — run 2: a streak of
**9** at 18.6 s and **21** at 63.7 s, both 100–340 ms before a `dpi-changed` event (the window crossing monitors);
pass A3: a streak of **15** at 42.6 s with no monitor event. The engine's `FastStreakLimit = 16` treats one such
burst as "no compositor clock, remote session" and latches **permanently** (only a later display change re-probes).
From then on production is a 7 ms wall-clock timer with no vblank phase: positions sampled at 7 ms intervals shown
on an 8.33 ms grid → a beat pattern, plus the `ProductionGateBlocks` decline path no longer applies. This is the
mechanism behind "sluggish *sometimes*" on the laptop panel, and dragging the window between monitors — which the
user does — is exactly the trigger.

### 3.5 Engine correctness finding on the side

`[fg-capture-parity]` (diag-only guard) fired 60+ times during the capture: "an incremental capture diverged from a
from-scratch one (n#33 BrushAnim (value) / n#2847 NodePaint / referencedImageIds count 82 vs 83). Some store write
mutated a captured column without NoteCaptureChanged/NoteBulkMutation." In Release the guard is compiled out and the
incremental capture is trusted — so a stale brush/paint/image reference can reach the render thread. Separate ticket.

## 4. Ranked causes

1. **Compositor-clock fast-streak latch** (§3.4). Engine, `Win32CompositorClock.cs:44, :215, :267`. A DWM tick
   burst (measured 9/15/21 sub-ms returns around monitor changes and spontaneously) permanently drops production to a
   7 ms timer. Silent in the shipped log. Explains intermittent sluggishness on any monitor and gets triggered by the
   user's monitor moves.
2. **Production paced by the DWM-global clock, not the window's monitor** (§3.1). Engine, `Win32CompositorClock.cs:186`,
   `ProductionGateBlocks`. 120 fps produced and presented into a 50 Hz panel; 2:3 judder, 2.4× GPU work. (The
   ambient cap is not a contributor here — see the §3.1 correction and §6 fix 2.) Explains the external-monitor
   report entirely.
3. **Full-window repaint on every scroll frame on a Weak GPU** (§2, §3.3). GPU at 75–85 % of the refresh per frame;
   `F:DetachedContent` (live crossfades) and the `F0` full route on scroll. This is the throughput ceiling; the
   unlanded `scroll-perf-fix-plan.md` / scroll-v3 Phase 4 span-reuse work is the fix.
4. **Contacts landing during a stale DM INERTIA get stranded as pointer messages** (§3.2). Four strokes in ~5 min of
   probing, cursor over the window. OS-side; the engine keeps DM parked in INERTIA (not Live, idle pump only) instead
   of stopping it, which is what the unused `UseOsInertiaStopFallback` arm does (`Stop()` at the INERTIA edge →
   READY at once → the next landing gets a fresh hit-test). Mitigation candidate, needs the cell-F probe run.
5. **DM INERTIA comments** — "DM never reports INERTIA" is false on this hardware (every run). Tidy while touching.
6. **Wheel glides unbudgeted; pump is the UI thread** (`AppHost.cs:3437, :3606, :3608`). Real, second order.
7. **App-side per-scroll cost** — `RenderCensus` always on in shipped sessions (`Program.cs:535`), the swipe belt
   re-render once any touch has been seen, lyrics per-frame work with the rail open, the 5 s `MemorySampler`, query
   delivery storms at 20 Hz during hydration. Real, but none produces a blocked touchpad or a monitor-specific judder.

## 5. Next steps

1. **Engine fix, clock robustness** (`..\fluent-gpu`): never latch on a fast streak. Debounce instead — a return
   less than ~2 ms after the previous accepted tick is the same vblank, drop it, and count it for diagnostics only;
   keep `wait-failed` as the only permanent latch; add a periodic re-probe (e.g. every 5 s) so any latch is
   self-healing. Log the verdict once per transition.
2. **Engine fix, per-window pacing**: pace production on the window's own output — `IDXGIOutput::WaitForVBlank` for
   the swapchain's containing output, or the frame-latency waitable as the production gate — and derive
   `RefreshLattice`, DM's composition lead and `missedVblanks` from that clock. (The old idea of "make `HalfRefresh`
   the ambient mode" is superseded — the host-wide ambient cap and its rate modes are gone; see §6 fix 3 for the
   per-source cadence model that replaced them.)
3. **Stranded-landing mitigation test**: add a `--stop-at-inertia` probe option (dm-probe cell F: `Stop()` deferred to
   the next pump at the RUNNING→INERTIA edge) and repeat the rest-then-pan / flick-then-land drill; if landings during
   the former INERTIA window now hit-test, flip the engine's `UseOsInertiaStopFallback` on and keep INERTIA out of
   `Live`. Run the probe with `--no-dm` beside the app for HID ground truth in the next in-vivo capture.
4. **Throughput**: land the span-reuse narrowing for moving scroll content (Phase 4) so a scroll frame is not a
   full-window pass on this GPU; turn `RenderCensus` off in shipped sessions.
5. Acceptance: `[fps] wait` never `swpace` with a working compositor; production fps == panel Hz on DISPLAY8; gpu ms
   per scroll frame well under the refresh; zero probe blocked windows with the cursor over the app.

## 6. Fixes landed (2026-09-10)

Four of the mechanisms above are fixed in this worktree's engine (`feat/ultra-fast-engine`):

1. **The compositor clock never latches off permanently any more** (§3.4, §5 item 1). A fast-return streak no longer
   sets a permanent unavailable flag by itself — only a genuinely missing export (the clock API absent, or repeated
   `WAIT_FAILED`) is permanent. An instant/failed return degrades to a synthesized lattice tick instead of stopping
   production outright, so a burst of sub-millisecond returns around a monitor/DPI change is absorbed rather than
   dropping the whole session to the 7 ms wall-clock fallback. Verify with `[compositor-clock] fast-burst` /
   `synthesized` / `hardware-restored` lines and `lattice … mode=decimate|pass` in the log; on this machine `[fps]
   wait` must never read `swpace`.
2. **Production is paced to the window's own monitor, not the DWM-global clock** (§3.1, §4 item 2, §5 item 2). The
   compositor clock decimates its published ticks to the containing window's own refresh period whenever that
   monitor is slower than the DWM clock it samples — so DISPLAY8 (50 Hz) production tracks 50 Hz instead of the
   120 Hz DWM tick. Verify with `nav.frames` on the 50 Hz monitor: fps should read ≈ 50 and `presented` ≈ 200 over a
   4 s window (not the ~480/120.3 numbers in §3.1's table).
3. **The host-wide ambient cap is gone; pacing is per animation source.** `AppHost.AmbientAnimationFps`,
   `AmbientRate`/`AmbientRateMode` (`Uncapped`/`HalfRefresh`/`ExplicitFps`), `AnimIsAmbient()`, `LatencySensitiveWake`,
   the scroll/mount grace windows and `FG_ANIM_FPS` are all deleted — there is no more frame-loop heuristic
   classifying "is this frame ambient". Every slab animation row now carries a `Cadence`
   (`AnimEngine.Keyframes`/`UseKeyframes` take an optional `Cadence`; `null` means `Cadence.Display` for one-shots and
   the scheduler's `DefaultLoopHz` for `loop: true`), the host wakes at `min(AnimEngine.NextDueMs)` quantized to a
   whole number of the window's own refreshes, and `HostWaitKind.Cadence` (`cadence` in `[fps] wait` lines) is the
   wait token that used to be `Ambient`. `AmbientPowerPolicy` (`src/apps/Wavee/App/AmbientPowerPolicy.cs`) now only
   sets `host.Animation.DefaultLoopHz` from AC power and `host.InactiveFrameIntervalMs` once — this is what made the
   scroll/mount grace windows unnecessary: a scroll running the loop at 120 fps no longer speeds up an unrelated 30 Hz
   shimmer, because the shimmer's row is only ever due at its own cadence regardless of how often the frame loop
   itself wakes. Diag tripwire: `[anim.cadence] displayRate-loops=<n>` fires every 30 s while a `loop: true` row is
   still running at display rate instead of a bounded Hz.

4. **The present queue no longer pre-pays a frame of input lag** (found AFTER the three above, from the operator
   report "120 fps but scrolling still feels heavy, delayed by a couple of ms"). Two changes, both in the D3D12
   backend and the render loop:
   - `SetMaximumFrameLatency` drops from **2 to 1**. Depth 2 was chosen (2026-08) to buy a frame of CPU/GPU slack so
     a frame costing slightly over one refresh would not quantize to half rate, on the stated assumption that "the
     second queued frame only materializes under backpressure". On this machine backpressure is **permanent**: the
     GPU costs ~5 ms of the 8.33 ms refresh on every scroll frame (§3.3), so the render thread sat 5–8.6 ms per frame
     inside the latency waitable and the frame on the glass had been produced two vblanks earlier; DWM composes one
     later ⇒ ≈25 ms finger-to-photon while the frame counter read a healthy 120 fps. The buffer count stays 3 (that
     is the CPU-written bank depth, a memory decision — now deliberately decoupled from the queue depth).
   - **The render loop waits for the present slot BEFORE it chooses which published frame to present**
     (`IGpuDevice.WaitForPresentSlot`, called ahead of `SceneFramePublisher.TryAcquire`). The historical order
     acquired first and blocked inside submit, so the acquired frame aged by the whole wait; the production gate is
     one frame per compositor tick (not per present), so a newer frame really can arrive during that wait. The
     waitable is a semaphore, so one wait is a credit exactly one `Present` spends (`LatencyCreditHeld`), and a turn
     that presents nothing never reserves a slot. This is the order Windows Terminal's `AtlasEngine` and makepad use.

   Expected: ~1–1.5 refreshes less finger-to-photon latency, with no change to the frame rate — this is a latency fix,
   not a throughput fix, and it is the one that should address "delayed by a couple of ms". The trade is that a frame
   which genuinely overruns its refresh now shows as one missed vblank instead of being absorbed by the queue, which
   is why §5 item 4 (scroll-content span reuse, still open) matters for the GPU cost per scroll frame.

### Verification run (2026-09-10, automated, unattended)

Two NativeAOT arm64 publishes of the SAME app source, differing only in the fix-4 engine change (the "before" arm is
the 20:34 publish: phases 1-3 with `SetMaximumFrameLatency(2)` and the wait inside submit). Five interleaved runs
(warm-up, before, after, before, after) each drove an identical drill through `WM_COPYDATA`: settle, move to
`DISPLAY1`, open the 1,494-track playlist, 424 wheel notches over 21 s, back to home, close. Everything below
comes from always-on log lines; the harness measures nothing itself.

Arms are self-identifying in the log: the after arm emits the new
`[d3d12.present] maxFrameLatency=1 buffers=3 waitable=yes slotWait=pre-acquire`, the before arm emits nothing there.

| scroll.frames, mean of 4 windows/arm | before (latency 2) | after (latency 1) |
|---|---|---|
| fps | 120.1 | 120.2 |
| UI frames / presented | 1234 / 1234 | 1230 / 1230 |
| missedVblanks | 32.0 | 33.8 |
| overBudget | 0.25 | 0.00 |
| worst frame: gpu ms | 3.48 | 3.45 |
| worst frame: total ms | 6.12 | 4.53 |
| worst frame: fenceWait ms | 6.20 | 4.85 |

**The one real risk of depth 1 did not materialise**: throughput is unchanged (120.1 vs 120.2, individual windows
120.0-120.2), and no window lost presents. The worst-frame `fenceWait` and total frame time trend lower in the after
arm, which is the expected shape (the latency wait now happens before the acquire, outside `SubmitDrawList`, so it no
longer counts in `LastFenceWaitMs`) - but these are single worst-frame outliers with a wide spread
(before 2.5/3.1/6.4/12.8, after 2.6/2.9/6.9/7.0) and are NOT offered as a measurement.

**The latency reduction itself is not measured here.** It needs present-to-display timing, i.e. elevated PresentMon
(`presentmon --process_name Wavee.exe --output_file x.csv` from an admin shell; unelevated it fails with
"access denied", and this box's account is not in *Performance Log Users*) or an in-app equivalent. Everything needed
for the in-app form is ALREADY in `PresentStats` (`PresentRefreshCount`, `SyncRefreshCount`, `SyncQpc`,
`RefreshPeriodQpc`) - whose own doc comment, interestingly, already claimed "a maximum frame latency of 1" while the
code shipped 2; that claim is now true.

Fix 1 and 3 in the same runs: `[compositor-clock]` produced one `lattice` line per run and **never** `unavailable`,
`synthesized` or `fast-burst`; `[anim.cadence] displayRate-loops` never fired in any of the seven runs; a settled home
page idles at **4.3 fps** in every arm, both of which contain phases 1-3. (The `fps=119.3` for
`route=home` in the operator's 21:00 session is NOT the pre-cadence number - that session had the same phase-3
build. The difference is input: these runs take no input inside the measurement window, that session did.)

**Playback + 45 s of true idle** (no input at all, `run-idle.ps1`): the loop settles at **46-50 UI frames/s**, not the
panel's 120 - so the per-source cadence is engaging. It is above `DefaultLoopHz` (30) because several independent
sources (the 30 Hz loop rows plus the playhead / equalizer `UseInterval` timers) each contribute their own wake and
the host wakes at the earliest due time; harmonising those onto a shared grid is the remaining lever, not a bug.
This does not fully explain the flat 120 fps seen in the 21:00 session, which had input and an open right rail - the
tripwire's silence says no `loop: true` row runs at display rate, so if it recurs it is a wake bit, and the wake-reason
census is the instrument for it.

**Fix 2 could not be exercised**: `DISPLAY2` (the AOC, renumbered by the OS since this document called it DISPLAY8) is no longer the 50 Hz cross-adapter DisplayLink panel this
document measured. It now reports `hz=99.981` with `topology=render-adapter-owns-output`. Moving the window there did
prove the plumbing: `[compositor-clock] reprobe` fired on the monitor change and the lattice period followed the window
from `8.33` to `10.00` ms. Mode stayed `pass` because 10.00/8.33 = 1.20 is under the stated 1.25 decimation threshold,
so production stayed at 120 fps on a ~100 Hz panel (a 6:5 mismatch). Whether 1.2 should decimate is now an open
question with evidence behind it: the threshold's comment argues 1.2 "would drop one frame in six for nothing", but on
this panel one frame in six is not displayed either way. Deciding it needs to know what DWM does with this monitor -
again present-to-display timing. The decimation arithmetic itself is covered by
`CompositorTickFilterTests` (including the 50 Hz window on a 120 Hz beat producing exactly 50 even slots per second).

### Literature

The clock-robustness (fix 1) and cadence (fix 3) designs draw on prior art in the reference checkouts under
`C:\WAVEE` (`zed`/GPUI, `egui` + fastpotify, `iced`, `makepad`, Windows Terminal, `flutter-scroll`,
`chromium-cc-input`, `gecko-dev`). Three rules recur across all of them and are the ones actually taken:

- **Never let a vsync/compositor-clock source latch off from a transient signal.** Terminal and GPUI both treat a
  burst of anomalous timer returns as noise to filter, not as a permanent "no clock here" verdict — the failure mode
  that produced this doc's §3.4 finding.
- **Snap timestamps to a constant lattice and re-snap a frame that lands late**, rather than free-running off
  whatever the last raw sample was (chromium-cc-input's frame-time snapping, gecko's refresh-driver tick alignment) —
  the basis for fix 2's decimate-to-monitor-refresh behavior.
- **Pace per source, plus a window-state throttle, instead of a global frame classifier.** GPUI's
  `Animation::with_max_fps` and egui's/Terminal's per-widget/per-source due-time model both drop the single
  "is this an ambient frame" heuristic in favor of asking each animation when it next needs a frame and doing less
  work while the window is not the active one — exactly fix 3's `Cadence` + `InactiveFrameIntervalMs` shape.
