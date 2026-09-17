<!-- Copied from the approved session plan on 2026-09-16; the living record of the implementation. Measurement traces: the ScrollProbe tool and run CSVs described below. -->

# Fix plan: Wavee scroll feel + the eight recording issues

## Context

On 2026-09-16 the user reported that scrolling in Wavee "steps" and jitters instead of feeling smooth, and sent a
14.5 s screen recording of a search → artist → album → playlist tour. A black-box measurement (synthetic wheel
input + 120 Hz screen capture of the live AOT build, no code read) produced six scroll findings, and a
frame-by-frame review of the recording produced eight UI defects. The user then asked for a detailed plan that
fixes all of it, informed by how other engines scroll. This plan covers both repos: the engine
(`C:\wavee\fluent-gpu`, where the scroll pipeline lives) and the app (`C:\wavee\wavee-0.3`).

Measured scroll symptoms (Chill Evening tracklist, 830 DIP viewport, 120 Hz, ARM64):

| # | symptom | measured |
|---|---|---|
| a | one notch = ~83 px over ~185 ms, ease-in-out; each new notch re-enters the ramp, velocity dips to ~2 px/frame then climbs back to ~9 | run19/run21 |
| b | the frame right after a wheel event is late (16–17 ms instead of 8.3) 3× more often than steady frames | 37 % vs 7–12 % |
| c | small-delta streams (touchpad-like, `|delta| < 120`) update the list every ~16.5 ms (60 Hz) and travel ~40 px per 120 units instead of 83 | run20 |
| d | every animation ends with 4–5 frames of sub-pixel re-rasterisation (pixels change, no whole-pixel shift) then one last 1 px step | run19 |
| e | wheel over the tracklist header row does not scroll the list | run17 |

The root causes were then located in the engine (see "Scroll: root causes"). Nothing here is speculative: each fix
maps to a measured symptom and a code path.

## What other engines do (used as design input)

- **Chromium** (`cc/animation/scroll_offset_animation_curve.cc`): wheel ticks animate with a cubic bezier; on a new
  tick mid-flight `UpdateTarget()` re-fits the curve so its initial slope equals the current velocity
  (`EaseInOutWithInitialSlope`) and bounds the new duration so velocity never has to dip
  (`EaseInOutBoundedSegmentDuration`). Ticks accumulate into one target. Precise (touchpad) deltas are applied 1:1,
  never animated. Composited scroll offsets are snapped to device pixels.
- **Firefox** (`layout/generic/ScrollAnimationMSDPhysics.cpp`): critically damped mass-spring-damper; on every new
  destination the model is re-seeded from the current position and velocity; the spring gets stiffer when event
  cadence slows ("slowdown" regime) so the last tick lands quickly; a gesture that pauses beyond
  `continuousMotionMaxDeltaMS` starts fresh.
- **Edge "smooth personality"**: one curve for wheel, keyboard and scrollbar with consistent velocity across stacked
  ticks (velocity continuity + accumulation), which is what makes a detented wheel read as one motion.
- **Windows precision touchpad / DirectManipulation**: the device stream is already smooth; the app must apply it
  1:1 at frame rate, not re-smooth or re-quantise it.

Design takeaways adopted below: velocity-continuous retarget with no re-entry into an ease-in, accumulation into one
target, cadence-aware stiffness so steady ticks give steady velocity, 1:1 precise deltas at the same per-notch
scale, device-pixel snapping of the scroll transform while text must be crisp, and input processed on the vblank
rather than whenever a packet lands.

## Scroll: root causes (engine, `C:\wavee\fluent-gpu`)

Pipeline: `WM_POINTERWHEEL` → `Win32Platform.HandlePointerWheel` (detented vs hi-res classifier) → `InputEventRing`
→ `InputDispatcher` → `ScrollInputRouter.Wheel` (notch → DIP) → `ScrollCommandPort` → `ScrollKernel.Tick` at
Paint phase 2.5 → `ScrollBody.Advance` (`ScrollPhysics.ChaseStep`) → `SceneScrollSink` → `ScrollContentTransform`
(no pixel snap) → `SceneRecorder` → `GlyphRenderer.SnapDy` (¼-row phase select).

| # | cause | where |
|---|---|---|
| a | `ApplyWheelNotch` starts a cold notch from `Velocity = 0` and the critically damped `ChaseStep` (half-life 40 ms) therefore has velocity `∝ t·e^{-yt}`: an ease-in-out. Velocity is preserved on a live retarget, but with a 40 ms half-life it has decayed to ~2.6 px/frame by the next notch, so every notch re-enters the ramp. Per-notch distance `max(48, 0.10·viewport)` = 83 DIP. | `src/FluentGpu.Engine/Scroll/ScrollKernel.cs:864-882`, `ScrollPhysics.cs:135-144`, `ScrollBody.cs:209-249`, `ScrollFeel.cs:36,48-55` |
| b | `WM_POINTERWHEEL` is not in `PacedInputWaitClassifier.IsDeferrable`, so a wheel packet breaks the paced wait mid-vblank and the frame is produced off-phase (slips one interval). The wheel glide is `Driven|Wheel`, not Drag/Ballistic, so `_frameBudget` is disarmed and `ReRealizeVirtuals` + `FlushRebindsToQuiescence` run unbounded on the boundary-crossing frame after the notch. | `src/FluentGpu.Windows/Pal/Win32Platform.cs:232-244,1617-1623`, `src/FluentGpu.Engine/Hosting/AppHost.cs:1765-1770,2134-2141,3485,3661-3663`, `ScrollKernel.cs:1137` |
| c | `notch % 120 != 0` latches the hi-res path for 200 ms; that path scales by the frozen constant `HiResUnitDip = 0.11` DIP/unit (one-machine calibration) instead of the notch scale 83/120 ≈ 0.69, does a `SetTimer` syscall per packet, and its 1:1 Drag is flushed only once per *produced* frame, so every production decline drops an update. | `Win32Platform.cs:291-295,1364-1446`, `ScrollInputRouter.cs:278-286`, `AppHost.cs:3472,3257` |
| d | Settle predicate `|Δ| < 0.5 DIP && |v| < 13 DIP/s` lets the chase creep through 1.44 → 0.93 → 0.60 → 0.39 px over 4–5 frames while `motionSoft == 0` forces a crisp re-snap of every glyph run each frame (phase re-select, AA changes), then `off = Target` jumps the last ~1 px. The content transform is deliberately never device-snapped. | `ScrollBody.cs:229-231`, `ScrollContentTransform.cs:16-37`, `src/FluentGpu.Windows/D3D12/GlyphRenderer.cs:1008-1023`, `src/FluentGpu.Engine/Render/SceneRecorder.cs:648-655,1562-1575` |
| e | `ResolveScrollTarget` = ancestor walk of the hit leaf, else a geometric DFS that requires the point *inside* the scroller's bounds. A header laid out above the list is neither. The only shipped remedy (`gate.scroll.wheel-through-sticky-overlay`) forwards to `ScrollBy(..., immediate)`: a hard jump, not a glide. | `src/FluentGpu.Engine/Input/InputDispatcher.cs:2826-2912`, `src/FluentGpu.VerticalSlice/Suites/ScrollSuite.cs:7126-7197` |

## Scroll: the fix (engine)

Rule kept from the v3 plan: one dt-stepped POD kernel, no env knobs, dt-invariant traces, zero alloc per tick.
`ChaseStep` stays the integrator; what changes is how the wheel *plans* each chase, how input is paced, how the
offset is written, and where the wheel is routed.

### S1. Wheel plan: cadence-planned chase, velocity-continuous, no ease-in, no creep (fixes a and d)

Validated by simulation (scratchpad `sim/wheelsim.py` reproduces today's trace: 2.9 → 6.6 → 8.7 DIP/frame, settle
208 ms, 7 sub-pixel tail frames, trough/peak 0.20 at 110 ms cadence). A first draft ("clamp y to |v/R|") was
simulated and rejected: it trails 120–160 DIP behind and takes 370–530 ms to land. The rule below is the one that
survived a 48-point sweep. It keeps `ChaseStep` as the integrator (so `gate.kernel.dt-invariance` is untouched) and
adds two pure functions in `ScrollPhysics.cs` next to `ChaseStep`:

- **Cold notch**: `vel = κ·R·yBase` (κ = 0.40; a same-direction fling or glide velocity is carried instead, capped
  at `0.95·|R|·yBase`, the exact no-overshoot bound of the ζ=1 solution). First frame ≈ 8.9 DIP at D = 83, monotone
  decay after frame 2, lands in ~150 ms.
- **Live notch, gap ≤ 130 ms** (a stream): target accumulates; `hl = clamp(0.70·gap, 45, 90) ms`; **kick**
  `vel = max(vel, min(0.65·D/gap, 0.95·R·y))` so the second click never dips to 2 px; then `y = max(y, min(|vel|/R,
  yBase))` so the plan is never softer than the no-hump match (velocity never rises then falls inside a gap).
- **Live notch, gap > 130 ms**: independent clicks: stay at `hl = 45`, kick to the cold seed.
- **Reversal** (`delta·vel < 0` or `delta·R < 0`): rebase `Target = off + delta` (drop unconsumed lag), keep velocity,
  `hl = 45` → brakes through zero in one frame, moving back on the second.
- **Tick** (`WheelStep`): once `since > gap·1.2 + 8 ms` with no notch, stiffen to `hl = 32` with the step split at
  the exact switch instant (two `ChaseStep`s, dt-exact); a **displacement floor** of `160 DIP/s·dt` toward the
  target (only when velocity points at it before and after the step, never past it); **snap when |R| < 1 DIP**.
  160 DIP/s is exactly 2 device px per 120 Hz frame at scale 1.5, so the tail reads 3 3 2 2 2 2 (+≤1.5 px landing)
  instead of 1 1 1 0 1 0 1: zero sub-pixel-only frames, which is what fixes symptom (d) without touching the
  transform.

```csharp
// ScrollFeel.Shipping additions (positional record; Shipping is the only construction site)
WheelHalflifeMs: 45f,  WheelTailHalflifeMs: 32f,  WheelSlowHalflifeMs: 90f,
WheelSeedFraction: 0.40f,  WheelCadenceKick: 0.65f,  WheelHalflifePerGap: 0.70f,
WheelGapMinS: 0.025f,  WheelGapMaxS: 0.130f,  WheelGapSlackFrac: 0.20f,  WheelGapSlackS: 0.008f,
WheelFloorDipPerS: 160f,  WheelSnapEpsDip: 1.0f
// ScrollBody: public float WheelSinceS, WheelGapS;   (gap 0 = no cadence plan armed)

public static void WheelPlanNotch(ref float target, ref float vel, ref float halflifeMs, ref float sinceS, ref float gapS,
    float off, float delta, float maxOff, bool live, float carryVel, in ScrollFeel f)
{
    float yBase = 1.3862944f / (f.WheelHalflifeMs * 0.001f);
    if (!live)
    {
        target = Math.Clamp(off + delta, 0f, maxOff);
        float r = target - off;
        float seed = MathF.Abs(f.WheelSeedFraction * r * yBase);
        float carry = carryVel * r > 0f ? MathF.Min(MathF.Abs(carryVel), 0.95f * MathF.Abs(r) * yBase) : 0f;
        vel = r == 0f ? 0f : MathF.CopySign(MathF.Max(seed, carry), r);
        halflifeMs = f.WheelHalflifeMs; sinceS = 0f; gapS = 0f;
        return;
    }
    float g = sinceS; sinceS = 0f;
    float rOld = target - off;
    if (delta * vel < 0f || delta * rOld < 0f)            // reversal: drop the unconsumed lag, brake through zero
    { target = Math.Clamp(off + delta, 0f, maxOff); halflifeMs = f.WheelHalflifeMs; gapS = 0f; return; }
    target = Math.Clamp(target + delta, 0f, maxOff);
    float R = target - off, aR = MathF.Abs(R);
    if (aR < 1e-3f) { halflifeMs = f.WheelHalflifeMs; gapS = 0f; return; }   // 0/0 → NaN guard, mandatory
    if (g > f.WheelGapMaxS)                                // slow cadence: independent clicks, stay stiff
    {
        halflifeMs = f.WheelHalflifeMs; gapS = 0f;
        float want = f.WheelSeedFraction * aR * yBase;
        if (MathF.Abs(vel) < want) vel = MathF.CopySign(want, R);
        return;
    }
    g = MathF.Max(g, f.WheelGapMinS); gapS = g;
    float hl = Math.Clamp(f.WheelHalflifePerGap * g * 1000f, f.WheelHalflifeMs, f.WheelSlowHalflifeMs);
    float y = 1.3862944f / (hl * 0.001f);
    float kick = MathF.Min(f.WheelCadenceKick * MathF.Abs(delta) / g, 0.95f * aR * y);
    if (MathF.Abs(vel) < kick) vel = MathF.CopySign(kick, R);
    y = MathF.Max(y, MathF.Min(MathF.Abs(vel) / aR, yBase));   // never softer than the no-hump match
    halflifeMs = 1386.2944f / y;
}

public static bool WheelStep(ref float off, ref float vel, ref float halflifeMs, ref float sinceS, ref float gapS,
    float target, float dtSec, in ScrollFeel f)
{
    float r0 = target - off;
    if (MathF.Abs(r0) < f.WheelSnapEpsDip) { off = target; vel = 0f; gapS = 0f; return true; }
    float since0 = sinceS; sinceS += dtSec;
    float tSwitch = -1f;
    if (gapS > 0f && halflifeMs > f.WheelTailHalflifeMs)
    {
        float tExpect = gapS * (1f + f.WheelGapSlackFrac) + f.WheelGapSlackS;
        if (sinceS > tExpect) tSwitch = MathF.Max(0f, tExpect - since0);
    }
    float o = off, v = vel, v0 = vel, rest = dtSec;
    if (tSwitch > 0f) { ChaseStep(ref o, ref v, target, halflifeMs, tSwitch); rest -= tSwitch; }
    if (tSwitch >= 0f) { halflifeMs = f.WheelTailHalflifeMs; gapS = 0f; }
    ChaseStep(ref o, ref v, target, halflifeMs, rest);
    float d = o - off;
    if (v0 * r0 >= 0f && v * r0 >= 0f)                     // heading for the target before AND after: floor applies
    {
        float floor = f.WheelFloorDipPerS * dtSec;
        if (MathF.Abs(d) < floor) { d = MathF.CopySign(floor, r0); v = MathF.CopySign(f.WheelFloorDipPerS, r0); }
    }
    if (MathF.Abs(d) > MathF.Abs(r0)) d = r0;               // never past the target
    off += d; vel = v;
    if (MathF.Abs(target - off) < f.WheelSnapEpsDip) { off = target; vel = 0f; gapS = 0f; return true; }
    return false;
}
```

Wiring: `ScrollKernel.ApplyWheelNotch` (`ScrollKernel.cs:864-882`) computes `sameFlavourLive`/`maxOff` as now,
`carry = Activity is Ballistic or Driven ? b.Velocity : 0` (Drag velocity is stale), calls `WheelPlanNotch`, keeps
`TargetRaw = Target`, `CancelRestore`, `if (!sameFlavourLive) b.Awake = false`, the flag writes and
`DrivenZeta/Omega/SettleVel = 0`; the generic `b.Velocity = 0f` reset is deleted (it is the discontinuity).
`ScrollBody.Advance` Driven branch (`ScrollBody.cs:220-232`): `else if ((b.Flags & Wheel) != 0) settled =
ScrollPhysics.WheelStep(...)` before the existing ζ/ChaseStep block; hardStop clamp and Idle transition unchanged;
programmatic glides untouched.

Simulated results at D = 83 (device px, scale 1.5): cold notch 13 14 13 12 11 10 9 7 6 5 5 3 3 3 2 2 2 2 3S,
settle 150 ms, 0 sub-pixel frames; 12 notches at 110 ms: trough/peak 0.59 (today 0.20), lag 124 DIP, settle 233 ms
after the last notch, per-notch velocity step 1.08–1.11 after notch 3; flick 6 at 30 ms: settle 158 ms; reversal:
carry-on 1.3 DIP, moving back on the second frame; 60 Hz vs 120 Hz trajectories agree within 0.13 px. Trough/peak
≥ 0.6 together with settle ≤ 220 ms is provably unreachable for any causal velocity-continuous plan at 9 notches/s
(one notch is always outstanding); 0.59 / 233 ms is the chosen Pareto point (ρ = 0.70). **With S6 the notch is
120 DIP, so velocities scale by 1.45: re-run the simulation at D = 120 during implementation and re-tune κ and the
kick if the first frame exceeds ~14 DIP.**

Degenerate cases handled: target clamped onto the current offset (settles next tick, no NaN), notch on the snap
tick (live retarget, no dip), Ballistic fling into a wheel notch (carried, capped, no overshoot), notch during a
Programmatic glide (cold path, as today), at-edge notches (router still refuses them).

Acceptance (probe, 120 Hz, D = 120): first frame after a cold notch 6–14 px; at 9–10 notches/s the per-frame shift
never drops below 0.55× the steady median; no frame > 1.6× its neighbours except after a late frame; zero frames
with change but no whole-pixel shift after the last notch; settle ≤ 240 ms after the last notch.

Gates (VerticalSlice `ScrollKernelSuite.cs`, next to `WheelAccumulateHardStopCheck` at `:372`):
`gate.kernel.wheel-cold-seed`, `wheel-cadence-flat` (min/max over notches 6..11 ≥ 0.55, Idle ≤ 240 ms, final = 12·D),
`wheel-second-click` (≥ 4 DIP on its own tick), `wheel-dt-invariance` (60 vs 120 Hz lattice ≤ 0.33 DIP),
`wheel-reversal`, `wheel-slow-cadence-stiff`, `wheel-edge-degenerate` (no NaN), `wheel-fling-carry`,
`wheel-no-subpixel-tail` (0 ticks with 0 < |d| < 0.667 DIP after the last notch, excluding the landing tick).

### S2. Pace wheel input on the vblank and budget the glide frame (fixes b, half of c)

- `Win32Platform.PacedInputWaitClassifier.IsDeferrable` (`Win32Platform.cs:232-244`): add `WM_POINTERWHEEL` and
  `WM_POINTERHWHEEL`. The ring already *sums* consecutive wheel deltas (`Pal.cs:274-291`), so a packet waiting for
  the tick loses nothing; the frame that consumes it is produced in phase.
- Hi-res branch (`Win32Platform.cs:1411-1446`): the per-packet `SetTimer` is the lift (gesture-end) timer, which is
  needed when the loop is idle; re-arm it at most once per frame (only when at least half the lift window has
  elapsed since the last arm) instead of on every 4 ms packet. Keep the 200 ms gesture gap.
- Safety of deferring the wheel: the paced wait runs only under `PlatformInputWakePolicy.CoalescePointerMotion`, which
  `WaitRequest` (`AppHost.cs:1765-1770`) selects only for display-rate waits (a live host). An idle host waits with
  `Immediate` policy, so the first notch of a gesture still wakes it at once; only notches arriving during a live
  glide wait for the tick (≤ 8.3 ms, in phase).
- `ScrollKernel.Tick` summary (`ScrollKernel.cs:1137`): rename `AnyDragOrBallistic` → `AnyLiveMotion` and include
  `Driven` bodies with `Wheel|Programmatic`; `AppHost.Paint` (`AppHost.cs:3485`) arms `_frameBudget` on it. The
  wheel glide then gets the same bounded `ReRealizeVirtuals` deadline a fling gets (`gate.virt.budgetSpreadsOverscan`
  already pins that spreading realize over frames keeps the visible rows populated).
- Always-on evidence: `FrameStats` already carries `VirtualRealizeMs` / `LayoutMs`; `_pacedUrgentBreaks` and
  `_productionDeclines` are the two counters that must fall to ~0 during a wheel glide. Expose both on `FrameStats`
  (P0 counters, no env switch) so the app's `scroll.frames` rollup can print them.

Acceptance: probe `steps`/`slow`: late-frame share within 30 ms of a wheel event ≤ the steady share (today 37 % vs
7–12 %); `pad` scenario updates at ~8.3 ms median (today 16.5).

### S3. One scale for every wheel device (fixes the other half of c)

`Win32Platform.HandlePointerWheel` hi-res branch (`Win32Platform.cs:1411-1416`) stops converting to DIP with
`HiResUnitDip = 0.11 · scale · UserTouchpadSpeed`. It emits the delta in **notch units** (`raw / 120f`) on the existing
`ScrollDelta` event, and `ScrollInputRouter` (`ScrollInputRouter.cs:278-286`) converts with the same
`ScrollFeel.PerNotchDip(viewportExtent)` the detented path uses, then posts the 1:1 Drag as today (Chromium: precise
deltas are applied, not animated). Delete `HiResUnitDip` and `UserTouchpadSpeed`; `SystemParams.WheelScrollLines`
already applies to both. 120 raw units therefore always travel one notch (83 DIP at this viewport) whether they
arrive as one packet or twenty.

Acceptance: probe `pad` (125 × −6 units) travels 6.25 × 83 ≈ 520 px (today 232); `fine` (60 × −30) travels
15 × 83 ≈ 1245 px (today 589).

### S4. The sub-pixel tail (fixes d): handled by S1's floor and snap, transform left alone

Device-grid rounding of the content transform was considered and **rejected**: commit `812c3ffee` (2026-08-14,
"even-timed, sub-pixel, crisp scrolling") removed exactly that quantisation because it measurably added velocity
jitter, and `ScrollContentTransform.cs:16-26` documents the decision. S1's displacement floor (≥ 2 device px per
frame while landing) and the 1-DIP snap remove the 4–5 change-without-motion frames without re-introducing it; the
settle re-record in `SceneRecorder.cs:1562-1575` then fires once, on the landing frame. The floor is a DIP constant
(2 px at scale 1.5; 1.33 px at 1.0, 2.67 at 2.0); if the 1.0-scale alternation shows in the probe, expose the floor
as device px per frame through the sink rather than by rounding the transform.

Acceptance: probe `steps`: zero-shift frames with residual > 0.3 after each notch = 0 (today 4–5); the landing step
≤ 1.5 px; `SceneRecordStats.UnsnappedGlyphSpans` = 0 on the frame after landing.

### S5. Route the wheel from a list header to its list, as a glide (fixes e)

Engine: `IScrollController` (`src/FluentGpu.Controls/IScrollController.cs`) gains `WheelNotch(float notches)`,
posting `ScrollInput.WheelNotch` with `PerNotchDip(viewport)` exactly like `ScrollInputRouter.Wheel` (a glide, not
`ScrollBy(immediate)`). `Element` gains `WheelTarget : IScrollController?`; `InputDispatcher.ResolveScrollTarget`
(`InputDispatcher.cs:2903`) checks the hit chain for a `WheelTarget` before the ancestor walk and routes there.
The existing gate `gate.scroll.wheel-through-sticky-overlay` is rewritten to assert a glide (multiple frames) via
`WheelTarget`, and `ScrollDemo.cs:26-35` uses it. App: the tracklist header rows set `WheelTarget` (see A-list).

### S6. Windows-like notch distance: three rows per notch (user decision)

Today `PerNotchDip = max(48, 0.10·viewport)` = 55 DIP (83 px) here, about 1.4 rows. The user chose Windows
semantics: `WheelScrollLines` (already read from `SystemParams`, default 3) × the scroller's own line height.

- `ScrollFrame` (the per-body geometry the kernel already receives via `ScrollInput.SetFrame`) gains `LineDip`.
  `Virtual.List` writes its item extent (`Virtual.List(N, 56f, …)` already knows it); non-virtual scrollers can set
  `Element.ScrollLineDip`; 0 means "no hint".
- `ScrollFeel` gains `PerNotchDip(viewportExtent, lineDip)`: `lineDip > 0 ? 3f * lineDip : max(WheelNotchMinDip,
  WheelNotchViewportFrac·viewport)` (the platform has already scaled the notch by `WheelScrollLines / 3`, so the
  3 here is the Windows baseline). Router (`ScrollInputRouter.cs:358-386`) and the hi-res path (S3) both call it.
- The tracklist rows are 40 DIP, so one notch = 120 DIP (180 px) and lands on a row boundary; with S4's
  device-pixel rounding the row grid stays aligned after any number of notches.
- Update `SPEC-INDEX.md:80` accordingly; add `gate.scroll.wheel-line-dip` (a list with `LineDip = 40` travels 120
  per notch; a plain scroller without a hint keeps the viewport rule).

### Scroll verification (engine, then app)

1. `dotnet build src/FluentGpu.slnx` Debug and Release; `dotnet run --project src/FluentGpu.VerticalSlice` → ALL
   CHECKS PASSED (new/updated gates: `gate.kernel.wheel-velocity-continuity`, `gate.kernel.wheel-cold-seed`,
   `gate.kernel.wheel-settle-device-pixel`, `gate.scroll.wheel-deferrable-pace`, `gate.scroll.wheel-through-sticky-overlay`,
   and `dt-invariance` / `alloc-zero-tick` / `one-write-per-tick` unchanged). `powershell -File docs\design\check-canon.ps1`.
2. `dotnet test` on `FluentGpu.Engine.Tests` and `FluentGpu.Windows.Tests` (add a `PacedInputWaitClassifierTests`
   fact for the two wheel messages, and a hi-res scale fact).
3. Publish the app AOT (`-Arch arm64`) and run the black-box probe from this session against the live window:
   `scrollprobe\bin\Release\net10.0\win-arm64\ScrollProbe.exe <hwnd> 560 210 440 800 out.csv <steps|slow|pad|fine|back|record> 1 snaps si`
   with the cursor over rows; compare the shift/dt traces to the acceptance numbers above, and check a saved
   snapshot shows the list (the terminal can sit on top of Wavee at that rectangle).
4. `ops/tools/perf-tour.ps1` gate unchanged: no completed UI frame over 8.3 ms on the scrolled routes.
5. Hand check by the user on the real touchpad and mouse, with `record` mode capturing the trace.

## Docs to update (engine)

`docs/design/SPEC-INDEX.md:80` (wheel distance row says 15 %, as-built 10 %), `docs/design/subsystems/input-a11y.md`
§3 (wheel messages are deferrable) and §7B (wheel plan, hi-res scale), `docs/plans/scroll-v3-plan-2026-08-17.md`
§5.3 (classifier no longer owns a DIP constant), `layout.md` §6 (offset rounding in the crisp regime).

## Recording defects: root causes and fixes

Engine-side (E) and app-side (A) lanes are disjoint files. Each pure rule gets xunit facts in `src/apps/Wavee.Tests`
or a VerticalSlice gate; no test reads source text. Bullets go to `CHANGELOG.md` under a new `## [Unreleased]`
`### Fixed`; issue numbers are added before release (user: "ignore for now"; the release `issue coverage` check is
soft, `issue refs` only fails on a mismatch).

### E1. Marquee parks mid-scroll (Shell title cut off, partial glyph, stale fade)

Cause: `src/FluentGpu.Controls/Marquee.cs:181-186` calls `SetNodeParked(host, paused)` on hover-leave, freezing
`TranslateX` wherever it is (usually `-tailDist`).
Fix: delete the park effect; when `paused` becomes true seed a one-shot `HomeTrack(fromX = ScrollX.Peek(), sty)`
keyframe track back to 0 (duration `clamp(dist / (Speed·4) · 1000, 120, 450)` ms, `SmoothOut`), otherwise
`BuildTrack` as today. Edge fade at rest then resolves from translate 0 (right cue only). Gate in
`VerticalSlice/Suites/AnimSuite.cs`: `M4a` pure `HomeTrack` shape, `M4b` behavioural hover-leave glides to |x| < 0.5 with
left fade band 0.
App call site (`Shell/Shell.PlayerBar.UI.cs:224-231, 267-272`, user decision "always scroll slowly"):
`Trigger = TriggerMode.Always, CycleMs = 14_000, EndPauseMs = 3_000, StartDelayMs = 2_000`, PingPong kept,
`scrollWhen` dropped (`titleHover` stays for the link ink). Rewrite the comment at `:225`.

### E2. Search-box ghost completion survives blur

Cause: `src/FluentGpu.Controls/AutoSuggestBox.cs:552-554` `showGhost` ignores the existing `focused` signal
(`:275`, written at `:540-544`). Fix: `bool showGhost = focused.Value && hi < 0 && caretAtEnd.Value && …`.
Gate `gate.controls.autosuggest-ghost` in `ControlsSuite.cs` next to `AutoSuggestProgrammaticFocusChecks` (:10219):
ghost count 0 before focus, 1 focused, 0 after blur.

### E3. Colour emoji (user decision: in scope)

Cause: `TextLayoutEngine.ResolveRunFace` (`:852-905`) already isolates emoji into a Segoe UI Emoji sub-run, but
`GlyphRenderer.GetGlyphByGid` (`src/FluentGpu.Windows/D3D12/GlyphRenderer.cs:1133-1160`) rasterises coverage only;
no `TranslateColorGlyphRun` anywhere.
Fix, all inside `GlyphRenderer.cs` (layout, measure and the run-cache key are colour-blind and stay unchanged):
1. QI `IDWriteFactory2` at init (`:414-416`); `IsColorFace(face)` via `IDWriteFontFace2.IsColorFont()` cached per face.
2. `ColorLayersFor(face, gid, size)` cached on `(faceId << 16 | gid)`: `TranslateColorGlyphRun` on a one-glyph run;
   `DWRITE_E_NOCOLOR` → empty; else one `ColorLayer(Gid, DxEm, DyEm, ColorF, Foreground = paletteIndex == 0xFFFF)`
   per layer glyph (COLR v0 layers are plain outlines in the same face).
3. `ShapeInto` (`:1108-1123`): for a colour face with layers, emit one quad per layer through the existing
   `GetGlyphByGid` alpha-atlas path and push `Foreground ? spanColor : layer.Color` into the run's per-quad `Colors`
   (the span-run mechanism `Replay` already honours at `:1039`); `LayoutRun` passes the colour scratch always
   (the "retain only when some A > 0" rule keeps plain runs at `Colors = null`). Gradient runs stay monochrome.
4. If Factory2 returns `DWRITE_E_NOCOLOR` for Segoe UI Emoji on this Windows 11 build, step to
   `IDWriteFactory4::TranslateColorGlyphRun(…, COLR | TRUETYPE | CFF, …)` with the same plumbing. COLR v1 paint
   trees are out of scope (flat v0 look is the target).
Zero-alloc: everything new is on the run-cache miss path; replay is byte-identical. Gate: engine-free
`FluentGpu.Engine/Text/ColorGlyphBake.cs` (`LayerColor`, `RetainColors`) with `TextSuite` checks `T-color-1/2`, plus a
gallery `--screenshot` scene containing "Now playing 🎵🔥" for the real pixels.

### A1. Search tabs paint a beat before the results

Cause: `Entities/Search.Page.cs:180-182` swaps `FacetRowSkeleton()` → `FacetRow()` in Render the moment
`_all.Knows(Chips)`, while the body swaps inside `SkelRegionEl` (reconciler effect + `StaggerRows` 40 ms/row).
Fix: pure gate in `Entities/Search.cs` beside `ShowChipSkeleton` (:252):
`ChipRowSkeletal(hasChipSource, chipsPending, firstBodyAnswered) => ShowChipSkeleton(...) || !firstBodyAnswered`;
`PageHost` latches `_bodyAnswered |= BodyReadiness() != Pending` (reset with `_hadChips` on query change), gives both
rows `Key`s and the real row `Enter = new EnterExit(Dy: 8, Opacity: 0)` so it arrives with the body. Facts appended
to `SearchChipSkeletonPolicyTests.cs` (5 cases incl. "chips landed, body not answered → still skeletal").

### A2. Now-playing bar reflows on track start

Cause: heart (`Shell.PlayerBar.UI.cs:303`), lyrics (`:407`), video split (`:411-443`, `VideoSlotReserved = active &&
ShowQueue`) and overflow (`:455`) are added on `active`; `right` is `Shrink = 0` with no width so each arrival steals
from `centre` (`Grow = 1`) while `BarMoveMotion` (Bounds 167 ms) animates every cluster; row children flip shape at `:489`.
Fix: slot presence is a function of the tier only, state lights a slot's face.
- `Shell/Shell.PlayerBar.cs` `PlayerBarRules`: `[Flags] RightSlot`, `RightSlots(in L)`, `VideoSlotReserved(in L) => L.ShowQueue`,
  `OverflowSlotReserved(in L)` (menu non-empty for the idle case), `SlotWidth`, `RightWidth(in L)`,
  `SlotFaceVisible(slot, state, hasVideo)`, `LikeFaceVisible(in L, state)`.
- `Shell/Shell.cs` `PlayerBarLayout`: `RightW` computed in `ForTier`; `VolumeSliderW = 96`, `SplitChevronW = 20` move here.
- `Shell.PlayerBar.UI.cs`: one `Slot(key, w, h, lit, face)` factory (`Opacity = lit ? 1 : 0`, `HitTestVisible = lit`,
  `Transition = MotionTok.ControlNormal`, never `Animate`); right cluster built from `RightSlots`; `right` gets
  `Width = L.RightW`; row children always `[left, centre, right]`; `BarItemMotion` keeps only the remote-device line
  and time labels.
```
player-row (Direction=0, Gap=RowGap)
├─ left   Width=LeftW  Shrink=0      [art 48][meta Grow=1][like Slot 32 face·=Active]
├─ centre Grow=1 Shrink=1            [transport prev|primary 40|next] / [elapsed][seek Grow=1][remaining]
└─ right  Width=RightW Shrink=0      [shuffle][repeat][volume][slider 96][lyrics·][video· 32+20][queue][devices][more]
   Wide: RightW = 7·32 + 96 + 52 + 8·2 = 388   (· = face follows state; width never changes)
```
Facts: new `PlayerBarSlotReservationTests` in `ShellPlayerBarUiRulesTests.cs` (right width is the tier sum and has no
state input; overflow slot never moves with playback across all tiers; faces follow state; the 300-DIP floor leaves the
seek bar ≥ 48). Update `The_inline_video_slot_rides_the_queue_tier` to the one-argument form.

### A3. Bio shows `&#34;`

Cause: `Entities/Artist.Rules.cs:192-203` decodes six named entities in UTF-8; `StripHtml` (`:134-146`) decodes none;
the full decoder is private in `Platform/Controls.Art.cs:893-918`.
Fix: new engine-free `Platform/HtmlEntities.cs` (`Decode(ReadOnlySpan<char>|<byte>, out consumed)` for `&#NN;`,
`&#xHH;`, the named set incl. typographic quotes/dashes; `DecodeAll`; `EncodeUtf8`; decoding never grows the buffer so
`Lead`'s in-place UTF-8 path keeps its callers' buffers valid). `StripHtml` and `Lead` call it; `Controls.Art.cs`
`DecodeEntities` delegates to it. Facts: new `HtmlEntitiesTests.cs` (numeric/hex/named, malformed stays literal,
UTF-8 never grows) + `ArtistTests` parity fact `StripHtml_and_Lead_decode_the_same_entities` with `&#34;` and `&#8217;`.

### A4. Verified check detached from the name

Cause: `Entities/Album.Page.cs:959-963` name `TextEl` has `Grow = 1, Basis = 0`.
Fix: `Grow = 0, Shrink = 1, MinWidth = 0, Wrap = NoWrap, MaxLines = 1, Trim = CharacterEllipsis`; check (or the
12×12 reservation when unverified) `Shrink = 0` right after it; parent row `MinWidth = 0`. Check `Artist.UI.cs:354`
for the same pattern. No pure rule; verified by run.

### A5. Tab label switches one frame before the page

Cause: `Shell/Shell.Host.cs:660-674` commits `Motion`, `Current` and `SyncActiveTab` in one flush while the old page
runs its 90 ms exit leg (`PageMotion.ExitDurationMs`, `Design.cs:1628`).
Fix: `Shell/Shell.Chrome.cs` pure `TabLabelStaging.LabelRoute(tabRoute, shown)` / `DelayMs(instantCut, reducedMotion)`;
`Shell.UI.cs` `ContentHost` writes `Shown` via `UseTimeout(…, DelayMs, DepKey.From(current))`; `BuildTabItems`
labels from `LabelRoute(tab.Route, Shown.Peek())`; `TabStripItemsVersion`/`TitleBarTabsVersion` fold `Shown`. Facts:
new `TabLabelStagingTests.cs` (6 cases).

### A6. Track table drops the shimmer on a two-row partial page

Cause: `Entities/Track.Table.cs:912-917` `RowsPending()` is `State == Unknown` only.
Fix: `Entities/Track.Rules.cs` `TableRules.RowsPending(state, count, total)`: Unknown → true; Partial → true until
`count ≥ min(max(total, count), FirstPageRows = 12)`; Complete/Failed → false. Table and its reveal-ramp arm
(`:830`) use it. Facts: 6 cases in `TrackTableRulesTests`.

### A7. About-the-artist and Featured-on pop in one after another

Cause: `Album.Page.cs:566-582` `TrailingHost` gates only on the tracklist; the band's relations are asked at
`FetchPriority.Prefetch` (`:623, :684, :689`).
Fix: `Entities/Album.cs` `PageRules.TrailingReserved(demanded, tracklist, aboutReady, featuredReady, deadlinePassed)`
with `TrailingDeadlineMs = 400`; `TrailingHost` arms one `UseTimeout` per album once demanded, reads
`Artists.Changed` / `AlbumRecommendations.Changed`, and asks recommendations + the lead artist's
`Identity|Stats|Bio` at `FetchPriority.Visible` (merch/more-by/similar stay Prefetch). `FrameSlots` untouched (the
comment at `:224-226` holds). Facts: replace the `TrailingReserved` facts in `AlbumPageRulesTests` (7 cases).

### Work split (parallel subagents on disjoint files; only the orchestrator builds, tests, launches)

| lane | repo | files |
|---|---|---|
| S1+S6 kernel | engine | `Scroll/ScrollKernel.cs`, `ScrollBody.cs`, `ScrollPhysics.cs`, `ScrollFeel.cs`, `ScrollInputRouter.cs`, `Controls/Virtual*.cs` (LineDip), `VerticalSlice/Suites/ScrollKernelSuite.cs` |
| S2+S3 platform | engine | `Windows/Pal/Win32Platform.cs`, `Hosting/AppHost.cs`, `Engine.Tests/PacedInputWaitClassifierTests.cs`, `VerticalSlice/Suites/ScrollSuite.cs` |
| S5 routing | engine | `Input/InputDispatcher.cs`, `Dsl/Element.cs`, `Controls/IScrollController.cs`, `WindowsApp/Pages/ScrollDemo.cs` |
| E1 | engine | `Controls/Marquee.cs`, `VerticalSlice/Suites/AnimSuite.cs` |
| E2 | engine | `Controls/AutoSuggestBox.cs`, `VerticalSlice/Suites/ControlsSuite.cs` |
| E3 | engine | `Windows/D3D12/GlyphRenderer.cs`, `Engine/Text/ColorGlyphBake.cs`, `VerticalSlice/Suites/TextSuite.cs` |
| A1 | app | `Entities/Search.cs`, `Search.Page.cs`, `Tests/SearchChipSkeletonPolicyTests.cs` |
| A2 (+E1 call site, +S5 header `WheelTarget`) | app | `Shell/Shell.cs`, `Shell.PlayerBar.cs`, `Shell.PlayerBar.UI.cs`, `Tests/ShellPlayerBarUiRulesTests.cs`; tracklist header element (`Entities/Track.Table.cs` header row only, coordinated with A6) |
| A3 | app | `Platform/HtmlEntities.cs`, `Entities/Artist.Rules.cs`, `Platform/Controls.Art.cs`, tests |
| A4+A7 | app | `Entities/Album.cs`, `Album.Page.cs`, `Tests/AlbumPageRulesTests.cs` |
| A5 | app | `Shell/Shell.Chrome.cs`, `Shell.UI.cs`, `Tests/TabLabelStagingTests.cs` |
| A6 | app | `Entities/Track.Rules.cs`, `Track.Table.cs`, `Tests/TrackTableRulesTests.cs` |

Order: engine lanes first (the app pins the engine by path), engine gates green, then app lanes, then the app
CHANGELOG (orchestrator, once). The plan itself is copied to
`docs/plans/wavee/scroll-feel-and-recording-defects-2026-09-16-implementation.md` with the code above, as the repo
rule requires.

## Verification (orchestrator)

1. Engine: `dotnet build src/FluentGpu.slnx` Debug and Release, `dotnet run --project src/FluentGpu.VerticalSlice`
   → ALL CHECKS PASSED, `dotnet test` on Engine.Tests and Windows.Tests, `docs\design\check-canon.ps1`.
2. App: `dotnet build Wavee.slnx` Debug and Release, `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` (6.6k+ green
   plus the new facts), `dotnet run --project src/apps/Wavee -- --fake` for the visual checks: search reveal, player
   bar on play, marquee, About card, colour emoji in a description, ghost text after blur, album open from a playlist.
3. Scroll: publish AOT arm64, run the ScrollProbe scenarios (`steps`, `slow`, `pad`, `fine`, `back`, then `record`
   while the user scrolls by hand) against the live window with a snapshot check; compare to the acceptance numbers
   in S1–S4; `ops/tools/perf-tour.ps1` verdict unchanged (no UI frame over 8.3 ms on scrolled routes).
4. Packaged-run hygiene: no `%LOCALAPPDATA%\Wavee` left from unpackaged runs before testing a packaged build.

## Verification record — 2026-09-16 (first landing)

Engine (both `C:\wavee\fluent-gpu`, where the lanes first landed, and the app's pinned worktree
`C:\WAVEE\fluent-gpu-pin` — see `EngineRoot.local.props`; the lane patch was ported to the pin with `git apply --3way`,
one conflict in `ScrollInputRouter.cs` resolved by keeping the pin's release-velocity rewrite and adding the
notch-unit scaling):

- `dotnet build src/FluentGpu.slnx` Debug and Release: clean (TreatWarningsAsErrors).
- `dotnet run --project src/FluentGpu.VerticalSlice`: ALL CHECKS PASSED, 1565 checks on the pin (1561 on the main
  checkout), including the nine new `gate.kernel.wheel-*` gates, `gate.scroll.wheel-line-dip`,
  `gate.scroll.wheel-through-sticky-overlay` (glide via `WheelTarget`), `M4a/M4b` marquee, `T-color-1/2`,
  `gate.controls.autosuggest-ghost`.
- `FluentGpu.Windows.Tests` 515/515; `FluentGpu.Engine.Tests` 434/434 on two of three runs (one run had a single
  failure the re-runs did not reproduce; not attributed).
- `docs\design\check-canon.ps1`: Canon OK.

Deviations from the plan text, all deliberate:
- Kernel gates at D = 120 DIP (three 40-DIP rows): cold-notch Idle ≤ 185 ms (plan 165), cadence settle ≤ 260 ms
  (plan 240) — the extra 37 DIP per notch costs two ζ=1 tail frames; constants kept as simulated.
- `Element.WheelTarget` is typed `FluentGpu.Scroll.IWheelTarget` (Engine cannot reference Controls);
  `IScrollController : IWheelTarget`, and `ScrollController`/`ItemsViewController` implement it.
- Header wheel routing only fires for notch-carrying events; DIP-only synthetic Wheel events keep the router contract.
- Hi-res packets carry notch units in `ScrollDelta/X` tagged by `WheelNotch/X`; the router latches the unit system per
  gesture (`IsNotchUnits`). Headless DIP producers are unchanged.
- `ColorGlyphBake.cs` lives in `Engine/Seams/Text/` (the text seam folder), not `Engine/Text/`.
- The autosuggest ghost gate focuses by pointer and then types: a Tab focus selects all (ghost hidden by design), a
  pointer focus on a non-empty document parks the caret where the press landed.
- Reserved player-bar slots fade through an opacity-only `LayoutTransition` (`BarFaceMotion`); `Element.Transition`
  alone snaps a live static Opacity change (the reconciler synthesises it only for Enter/Exit).
- `AlbumPendingGateTests.cs` (pinned the replaced two-argument trailing gate) deleted; the seven-case theory in
  `AlbumPageRulesTests` replaces it.
- A Failed tracklist edge leaves the reveal ramp undecided (A6 follow-up), so a retry still gets its reveal.
- The album page's own header (outer `ScrollView` owns scrolling) has no `IWheelTarget` seam yet; only the
  playlist/flat tracklist header routes the wheel. Open item.

App (`C:\wavee\wavee-0.3`, against the pin): `dotnet build Wavee.slnx` Debug and Release clean; `Wavee.Tests`
9103 passed, 6 skipped, 0 failed. CHANGELOG `## [Unreleased]` written without issue numbers (user: ignore for now).

Pending at the time of writing: the Release arm64 AOT publish for the on-screen probe run and the visual pass
(`--fake`), both of which need the user's running Wavee instance closed (unpackaged runs share
`%LOCALAPPDATA%\Wavee`).

## Wave 2 — real-mode scroll (CDN images): causes and lanes

User report after the first landing (run as pid 64740 in real mode): the wheel model is smooth, but scrolling
with covers downloading is not. The always-on log for that run: liked (text rows) 481 frames, 3 missed vblanks;
album 224 frames, 26 missed of 352 presents, 21.8 MB UI-thread allocation, gc 5/2/1 in 1.9 s; artist 114 frames,
21 missed of 169, 22.6 MB, gc 3/2/1. `frame.churn` census: `TableRowContent×13 a=937K`, `ToolTip×75 a=288K`,
`LazyGrid×1 a=452K`, `NowPlayingOverlayHost×13 a=141K`, `SectionsHost/StackHost` up to 600K — per frame, while
`avgFrameMs` stays 0.5 ms. `--fake` mode never pays any of it.

Two root causes, both confirmed in code:

A. **Publication fan-out on every frame.** `Shell.Host.cs:322-332` runs `Entities.Publish()` after every rendered
   frame and every posted answer; `Palette.Host.cs:124-136` re-arms the cover-palette pump and publishes on each
   batch. In real mode something is dirty almost every frame, so every component that reads a table's `Changed`
   counter re-renders each frame: `TableRowContent` (`Present` subscribes Tracks/Albums/Artists/TrackArtists),
   `SectionsHost` (12 counters, no value gate), `StackHost` (6), `ReleasePanelHost`, `FacePileHost`, `PageHost`,
   `LikedArt`, and `LazyGrid` via `DiscoGridHost.Count`. `NowPlayingOverlayHost`/`ShelfCardHost`/`ToolTip` re-render
   because their props records compare delegates and `Element`s by reference (fresh every parent render).
   Allocation per row: ~12 `with {}` clones of the 123-member `BoxEl`, three uncached string formats, 3–5 closures.

B. **Image landing cost.** Every applied cover is rented from `PixelBufferPool` and copied again on the UI thread
   (`AppHost.cs:2830-2836`; 256 KB–1 MiB, LOH) because `DecodeScheduler.Pump` returns the decode buffer one line
   later; on the render thread each new texture gets its own `CreateCommittedResource(UPLOAD)` + Map + memcpy inside
   the present turn with no pool and no byte budget (`ImageTextureStore.cs:507-529, 700-705`); the image pump shares
   the 3 ms realize deadline measured from frame start so mid-fling frames apply zero images (`AppHost.cs:3941`,
   `DecodeScheduler.cs:212`); all requests are `ImagePriority.Visible` (no overscan prefetch, `Reconciler.cs:5378`);
   `DiskImageCache` never trims (`_approxBytes` seed bug, `:97-98`) and writes last-access time per hit; the shared
   `ArrayPool` is capped at 4×4 arrays (`Wavee.csproj:60-61`) so response buffers go back to the GC.

Lanes (engine lanes edit the PIN worktree `C:\WAVEE\fluent-gpu-pin`):

| lane | files | change |
|---|---|---|
| W2-E1 image apply | `Media/Images/DecodeScheduler.cs`, `Hosting/AppHost.cs` (pixel sink + pump slice), `ImageUploadQueue` | hand the decode buffer's ownership to the upload queue (no UI-thread rent+copy; the queue returns it to the same pool); give the pump its own slice at phase 7.5 (`now + 1.5 ms`, min) instead of the realize deadline |
| W2-E2 upload heaps | `D3D12/ImageTextureStore.cs`, `D3D12/D3D12Device.cs` | ring of reusable upload heaps by power-of-two bucket; per-turn byte budget in `DrainImageJobs` (defer the rest to the next turn) |
| W2-E3 overscan priority | `Reconciler/Reconciler.cs` (image request sites) | rows realized outside the visible range request with `ImagePriority.Prefetch` (visible rows keep `Visible`) |
| W2-E4 disk cache | `Media/Images/DiskImageCache.cs` | seed `_approxBytes` from the directory once (background), fix the trim condition, drop the per-hit last-access write (LRU by an in-memory touch map flushed on trim) |
| W2-E5 grid + chart | `Controls/LazyGrid.cs`, `Controls/Charts/DensityPlot.cs` | `_win` split into a realized-window key (render reads) and a visible-range signal (effect reads); `IReadSignal<int>` count overload; `UseMeasuredWidth(WidthQuantum)` |
| W2-A1 publish cadence | `Shell/Shell.Host.cs`, `Entities/Palette.Host.cs`, new `Shell/PublishCadence.cs` + tests | pure `PublishCadence.ShouldPublish(scrollActive, framesSinceLast, msSinceLast)`: while a scroll is live publish every 4th frame or after 50 ms, else every frame; palette pump does not self-re-arm while scrolling (resumes on scroll end) |
| W2-A2 value gates | `Entities/Album.Page.cs` (SectionsHost, ReleasePanelHost, PageHost), `Entities/Album.UI.cs` (StackHost, FacePileHost), `Shell/Sidebar.UI.Rows.cs` (LikedArt) | replace raw `Changed` reads with `RowStamp`/`UseComputed` value gates (the `PageHost.HasPanel` idiom) |
| W2-A3 row cost | `Entities/Track.Table.cs` (TableRowContent), `Entities/Track.UI.cs`, `Entities/Track.Rules.cs` | `FormatCache` for duration/plays/date, closures hoisted to per-slot fields, `Present` gate no longer sums unrelated artist versions; full `CreateBound` template rewrite deferred |
| W2-A4 art props | `Platform/Controls.Art.cs`, `Platform/Controls.cs`, `Platform/Design.cs` | data-only `Equals` for `OverlayProps`/`ShelfCardProps`/`CardData`; `Named` → `ToolTip.WrapStable` with mount-stable factories in the overlay and card hosts; `CoverShimmer` keyed by slot with the URL as a prop (no remount on recycle); `ArtUrl` cached per id; `WatchedPlaceholder` prop cached per url |

Verification: engine Debug+Release, VerticalSlice, tests; app Debug+Release, `Wavee.Tests`; then the same
always-on rollups on the same routes: target missedVblanks ≤ 1 % and hotAllocKB per scroll second ≤ 2 MB on
album/artist, gc2 = 0 inside a scroll.

## Verification record — wave 2 (2026-09-16, afternoon)

Engine (pin): Debug + Release clean; VerticalSlice ALL CHECKS PASSED (1574 checks, incl. `gate.img.overscan-lane.*`,
`gate.lazygrid.render`, LazyGrid/DensityPlot checks); Engine.Tests 440/440 (new DiskImageCacheTests), Windows.Tests
515/515; canon OK. Lanes W2-E1..E5 landed as designed with these deviations: the decode-buffer handoff is a
call-scoped loan (`DecodeScheduler.TryTakeDecodeBuffer`) because the pixel-sink delegates could not change without
touching ~10 test fakes; the pump's minimum slice constant lives on `DecodeScheduler`; overscan rows use
`ImagePriority.Overscan` (the lane that exists), with `ImageCache.Pin(handle, priority)` threaded so a pin no longer
force-promotes, and `ImageCache.Promote` restarting dropped requests when a row scrolls into view; the device holds at
most one carried upload job per turn and exposes `DeferredImageUploads/Bytes` (not yet on `FrameStats`).

App: Debug build, `Wavee.Tests`, Release build — see the console record of this session; the Release arm64 AOT
publish of 12:07 carries all of wave 2 (lane W2-A5 landed minutes before the compiler ran).

First real-mode run of the 12:07 build (pid 54860), same always-on rollups as the morning baseline:

| route | frames | allocKB | gc (0/1/2) | missed / presented |
|---|---|---|---|---|
| artist 3NRF… (morning) | 114 | 22596 | 3/2/1 | 21 / 169 |
| artist 3NRF… (12:07 build) | 528 | 6434 | 3/1/0 | 43 / 923 |
| artist 5CiG… (12:07) | 526 / 530 | 4906 / 4688 | 2/1/0, 1/0/0 | 3 / 800, 20 / 719 |
| playlist 4An9… (12:07) | 388 | 28953 | 4/2/1 | 69 / 585 |
| playlist 37i9… (12:07) | 980 | 21429 | 8/4/2 | 108 / 1731 |

Artist routes: allocation down 3.5–4.8×, no gen-2 collections, missed vblanks 0.4–4.7 %. Playlist routes are the
remaining hot spot; `TableRowContent` has left the census, and the new top allocators there are the right rail
(`PaneView×1 a=324K`, `PaneSlot×2`, `ToolTip×33`), the lyrics panel (`LineRow×42 a=487K`), the playlist facts cards
(`ArtistsCardHost/BlendCardHost/TempoCardHost` + `ToolTip×36`) and the playlist `PageHost` (`a=978K` on one frame).
The `search` navigation also missed 170 of 300 presents (pre-existing; `flush/reactive` 7.8 ms mount frames).

Open item reported by the user during this run: an artist page's Top tracks band stayed skeletal. The log shows,
right after that navigation, `ap channel failed (epoch 1)` (connection timeout) and three `server-clock probe failed …
401 Unauthorized`; the top-tracks query is a Pathfinder call that needs a live session, so this is connectivity, not
a rendering regression. The trap noted in memory (Fetch never clears `Asked` on a route that does not carry the
field) would keep it skeletal after reconnect until the page is reopened — worth verifying separately.

Follow-ups (wave 3 candidates): value-gate `PaneView`/`PaneSlot`, the playlist facts cards, the playlist `PageHost`
and `FacetHost` (era-band memo reads `Albums.Changed`); move the remaining `Controls.Named` sites to `WrapStable`;
lyrics `LineRow` re-render footprint; `Concert.Page.cs` to the `IReadSignal<int>` LazyGrid count; expose the device's
deferred-upload counters on `FrameStats`; the `UseImage` hook still requests at `Visible`; sync the wave-2 engine
changes from the pin back into `C:\wavee\fluent-gpu` (wave 1 exists in both trees, wave 2 only in the pin).

## Wave 3 — the remaining allocators, the fetch seal, unavailable rows, the sticky play error

Lanes: W3-E1 (fetcher pool + FrameStats deferred-upload counters), W3-A1 (fetch failure semantics: `FetchOutcome.Unfilled`,
`Fetch.Answer` un-asks refused groups and clears Inflight, `Fetch.Refuse/Resume` on session Online via
`Spotify.Library.SyncNow`, `Fetch.Refresh`/`Entities.Refresh`, `ArtistReadiness.ChartFailed` + 10 s recheck + a real
Retry), W3-A2 (sidebar pane: `RailHost`, `CountHost`, `PlanDiff` value compares), W3-A3 (playlist page stamps, facts
panel settle gate, card hosts on `WrapStable`, `BlendLabels`), W3-A4 (lyrics `LineRow` shape memo + opacity spring
retarget, `FacetHost` year fold, Concert grid count signal), W3-A6 (terminal per-entity 4xx → ruled-unavailable track,
`Track.Unplayable`, `RowVerb`, error-state skipping; orchestrator wired `NextAllowedByContext`/`PrevAllowedByContext`
from the reducer to the bar).

Engine (pin): Debug + Release clean, VerticalSlice 1574 checks pass, Windows.Tests 515/515, Engine.Tests 455 with one
allocation-measuring test flaking under parallel execution per run (a different one each time; each passes alone).
App: Debug clean; `Wavee.Tests` see the session record (three new facts needed premise corrections: a cached
non-capturing lambda, a lyrics ladder floor, an xunit argument order).

Known follow-ups: `SidebarRowDiff` (Sidebar.cs) still compares `MosaicTiles` by reference and is now unused by the
pane; `ItemsView` re-renders per publish via `DisclosureOptions.Version`; the 401-beside-200 case recovers via the
Retry vacancy / recheck, not via `Resume` (status is not carried into `Answer`); `Fetch.Resume` runs only when
`LibrarySyncRules` allows a sync; `UploadHeapCreates/Reuses` census not surfaced; wave 2/3 engine changes live only
in the pin worktree.

### Wave 3, second half — relinking and the dead-row advance

W3-A7: `Queue.NextPlayable` (pure, bounded walk that skips unplayable rows both ways, wraps under repeat-context),
`Playback.AutoSkip` (terminal faults `Unavailable|DrmRequired|DecodeFailed` on a `LoadOrigin.Advance` load skip forward,
max 3 consecutive, never under repeat-one; session faults still park), `State.LoadWhy/AutoSkips`, `Effects.SkippedUnavailable`,
host signals `LastSkippedUnavailable`/`SkippedUnavailableCount` (toast wiring is a follow-up). W3-A8: `TrackV4(payload,
entityUri, s)` stages the requested identity as an alias of the canonical row (fields + artists run + audio entity),
`Track.ForDisplay` redirects title/art/artists/duration reads in the grid, queue and detail notice, `Audio.Allowed`/
`CountryAllowed` gate `Choose` on `Api.Market` (alternatives walked when the main file list is restricted). Orchestrator:
queue rows read "Unavailable" for `Unplayable` tracks; `ExtendedMetadataRules.ResolvedAsUnavailable` also rules a 2xx
entity with no extension_data. App Debug clean; `Wavee.Tests` 9251 passed.


## Wave 4 — the artist recording (`artist_bullshit.mp4`), the player-bar video slot, navigation cost, the scroll trace

Recording: 1812×1142, 30 fps, 19 s (2026-09-16 18:39), reviewed at 2 fps plus full-rate crops around the two album
navigations. Log: pid 67772, the wave-3 publish.

### Defects found and their causes

| # | seen | cause | fix |
|---|---|---|---|
| R1 | Discography cards: the hover plate / expanded outline extends ~20 DIP below the meta line | `CardShell` sets `Grow = 1`; the LazyGrid cell wrapper is a column of `cellW + DiscoCardChrome + DiscoRowGap`, so the card's `Height` was only the flex basis and it grew into the row gap | `CardData.Height` pins the shell (`Grow = Shrink = 0`); DiscoCell passes `CardW + DiscoCardChrome` |
| R2 | Expanded drawer of a 1-track single: a grey stub pill under the row, at the panel's centre | `Controls.SelectionBar(0, …, minCount: 0)` mounted the standalone chrome around an empty command lane | live `_sel.SelectedCount` (subscribed via `_sel.Version`), default `minCount` 1 |
| R3 | Single's video card meta "0 songs · 3 min · 2020" beside a "1 Song" tile | `AlbumV4` claims `Identity` (incl. `TrackCount`) up front while the count grows per disc row; the pathfinder header page then `CommitAlbums`-overwrote the column with 0 while `Known` merged additively | AlbumV4 withholds the bit at count 0; `CommitAlbums` writes `TrackCount` only when the row claims it; `PageRules.SongCount(known, members)` at both meta sites |
| R4 | "Go to album" tooltip still open over the album page it opened | pages are `Flow.KeepAlive` destinations: the owner stays mounted and under the still pointer, no leave edge, the safe-zone poll keeps it for the 5 s dwell | engine `ToolTip.CloseOpen()` (+ `gate.tooltip.closeOpen`); `Shell.Host.RouteCommitted()` calls it on every commit |
| R5 | Player bar: an empty gap between the lyrics button and the queue button | the video split slot was reserved by tier (wave 1's "no reflow on track start") and unlit without a video | user decision: the slot exists iff the track has a video; `RightSlots/RightWidth(in L, hasVideo)`, `RightWMax` for the floor invariant, the cluster eases via `BarMoveMotion` |
| R6 | The Razors Edge page jumps ~390 DIP in one 33 ms video frame right after opening | consistent with a hi-res wheel flick applied 1:1 (Windows semantics, 3 rows per notch); the log shows a scroll burst there (45 frames) and no restore/programmatic scroll; not a defect, noted for the measurement below | — |

### Navigation cost (log, the same session)

- Mount frames are node-count bound: the playlist page mounted 1684 nodes in one flush (13 ms, 3.4 MB, `TableSlot×34`
  ≈ 94 KB and 160 µs per row); the artist page's 20 ms frame was `LazyGrid×3 c=18.6 ms` / `DiscoCell×19 c=18.1 ms
  a=1.3 MB` — the eager per-card chrome (`NowPlayingOverlayHost×24 a=507K`, `ToolTip×53 a=688K`). Fixed by the lazy
  card chrome (`GridCardHost`, `CardChromeRules.Mounted(hot, relates)`).
- Every 7–9 ms single-component render spike (`PageHost r=7.67`, `RailHost r=8.60`, `CoverShimmer×8 r=5.76`,
  `ChartRow×12 r=6.08`) sits in a frame with `gc=1/1/1` or `2/2/1`: gen1/gen2 collections landing inside a render,
  driven by 10–16 MB of allocation per navigation. The lazy chrome removes ~35 KB per card; the remaining lever is the
  1 KB `BoxEl` record (123 members) times the node count — an engine redesign, listed as a follow-up.
- Route-swap frames with `mounts=0` still flush 15–17 ms (`upd=75 wr=12`, nothing attributed by the census): the
  KeepAlive park/unpark of a page-sized subtree. Follow-up: census the reconciler's own phases on those frames.
- `missedVblanks` in `nav.frames` was idle pollution (301 "missed" while reading an album page) — dropped from the nav
  rollup; the engine counter is unchanged and stays meaningful in `scroll.frames`.
- Fence waits of 5–12 ms with `gpu` 3–8 ms on hero/material mount frames (`blurGroups=3`, full repaint): the GPU side of
  a page open; not touched this wave.

### The scroll trace ("scrolling still sometimes feels bad — measure it")

Engine: `ScrollBody.TickDeltaMain` (written around every advance), `ScrollFrameSummary.MaxAbsDeltaDip` +
`WheelNotches`, `FrameStats.ScrollLiveMotion / ScrollDeltaDip / WheelNotches`. App: `Screens/Diagnostics.ScrollTrace.cs`
(`ScrollTraceRules.Analyse/Format/Describe`, `ScrollTraceRing`), one `scroll.trace` line per burst after `scroll.frames`
(Warning when `liveMissed` or `liveZero` > 0). Figures: `live`, `liveMissed` (held refreshes BETWEEN two live frames —
the idle gap after a landing is not a hitch), `liveZero`, `dips` (< 0.55× the trailing 3-frame median, recovering next
frame), `late` (> 1.5 refreshes), `jitter` (median relative step change, notch frames excluded), `notches`,
`notchGapMs`, `dtP95`, `dtMax`, then `shifts=` with `*` notch, `!` held refresh, `~` late, `.` no motion. Facts:
`ScrollTraceRulesTests` (12). Reference: `ops/tools/scroll-reference.html` — Chromium's rAF scroll deltas through the same
rules, for the side-by-side the user asked for.

### Verification record — wave 4 (2026-09-16, evening)

- Engine pin: Debug and Release build clean; VerticalSlice ALL CHECKS PASSED (1575 checks, incl. the new
  `gate.tooltip.closeOpen`).
- App: Debug and Release build clean; `Wavee.Tests` 9279 passed, 0 failed, 6 skipped (new: `ScrollTraceRulesTests`,
  `CardChromeRulesTests`, `SongCount` facts, the AlbumV4 count facts, the two-argument player-bar slot facts).
- Published: `bin\publish-next\win-arm64\Wavee.exe` (the running instance locks `bin\Release\…\publish`).
- Not yet measured on the live build: the first `scroll.trace` lines and the reference page comparison are the user's
  next hand scroll.


### Wave 4, first live traces (20:18) — what they said and what changed

The user's first session on the trace build (pid 70616) logged eight `scroll.trace` lines. Two findings, both fixed
before the second publish:

1. **Every drag frame read 0.0.** The user scrolls with a precise stream (touchpad / free-spin wheel): `notches=0`,
   long `Drag` phases, then a `Ballistic` coast (`65.9 64.2 62.6 …`). The trace measured displacement around the tick's
   *advance*, but a drag delta lands in `ApplyDragDelta` while the command port drains, BEFORE the advance loop — so
   the whole drag phase printed as zeros and `liveZero`/`liveMissed` were meaningless. `ScrollBody.SummaryMain` now
   holds the position the previous summary saw (seeded in `MarkActive`, before the enrolling command moves the body);
   `UpdateSummary` computes `TickDeltaMain = PositionMain − SummaryMain` over the active and touched bodies. Gates:
   ALL CHECKS PASSED (1575; one flaky `gate.resource.keep-previous-data` failure passed on rerun).
2. **The sidebar rail re-rendered about every fifth scroll frame** (`RailHost×1 a=305K`, ~24/s, in step with the
   50 ms publish cadence): 38 MB over a 4.6 s playlist burst, gen0 ×7, gen1 ×2. The rail re-renders only when
   `PaneView.PublishStage` bumps `_railVersion`, i.e. when a re-plan ran because `PlanDep` moved — ten possible
   inputs. `Shell/Sidebar.Census.cs` (`SidebarReplanCause`, `SidebarReplanCensus`) records which inputs moved per
   build and how many publishes bumped the rail; `scroll.frames` drains it per burst (`sidebarReplans= railBumps=
   wholesale= causes=`). The next burst names the culprit; the fix follows from the name.

Also seen: `TableRowContent×12 a≈420–840K` in one census frame per second (the rows re-render together, most likely
the per-second `Entities.Now` read in `TableRowContent` — a follow-up, ~1 MB/s), and a 122 ms first frame after a cold
start (`flush=105 rx=3.2 layout=14`, `textMiss=134`: cold text/GPU warm-up, once).

Published (second time this wave): `bin\Release\net10.0\win-arm64\publish\Wavee.exe` — the user's instance now
runs from `publish-next`, so the folders swapped again. Tests: 9283 passed.


### Wave 4, evening — three regressions the user reported while measuring

| # | report | cause | fix |
|---|---|---|---|
| R7 | "Go to song radio" starts the radio at once; 0.2.9 kept the current track and skipped the duplicate seed | `Playback.StartRadio` did `PlayContext(RemotePlan.StationUri(seed))` (gap G-251 / decision D34 never landed) | seed → `inspiredby-mix/v2/seed_to_playlist` (`RequestKind.RadioSeed`, `Decode.RadioPlaylistUri`) → context-resolve of the playlist → `RemotePlan.RadioParks` decides: idle/foreign → the normal `Resolved`/`StartContext` path; a live local deck → `ParkRadio` (`RemotePlan.RadioAfterCurrent`: history · deck · kept queue · radio rows as NextUp with the deck's recording dropped) + `Input.SwitchContext` (`DoSwitchContext`: context, cursor re-validated, autoplay/paging reset, ArmNext, no load) + `ContextPages`. `Queue.RadioToast` raises "Radio started / Open playlist" or "Couldn't start radio"; `started` is false while a remote device owns playback |
| R8 | Queue panel: the now-playing title should open the context | no click on the identity | `NowPlayingTarget`: the context's route (`Shell.For`) when it has a page, else the track's album; `RouteKind.NotFound` explicitly (the enum's zero is Home) |
| R9 | "I LOST the Spotify remote active state" | `Spotify.Session.cs` AP `ReadTimeout = 90_000` < the AP's 120 s ping → `ap channel failed … net_io_readfailure` every 90–210 s (43 + 199 + 188 in today's logs), re-login, dealer reconnect, `put-state NewDevice` on every flap | `ApPingIntervalMs = 120_000`, `ApReadTimeoutMs = 2 × ping + 60 s` (`ApChannelRulesTests`); the `/melody/v1/time` probe (always 401) now sends bearer + client-token |
