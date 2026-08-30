> Produced 2026-07-03 by a multi-agent (ultracode) investigation of the line-switch recording (Fable scouts -> Opus hypothesis verification -> Opus plan + completeness critique). Verdicts: A1 opacity clobber CONFIRMED, B1 boxy glow retire CONFIRMED, C2 auto-scroll gesture theft CONFIRMED (code defect), C1 blur-pin miss storm PLAUSIBLE (likeliest lag driver); refuted: A3 crisp-on-miss fallback, B2 clobber-as-white-snap, C3 resync step-up frame.

# Wavee Lyrics Panel — Technical Fix Plan (ISSUES A, B, C)

All paths absolute. App = `C:\wavee\fluent-gpu\app`, engine = `C:\wavee\fluent-gpu\src`. Every anchor below was re-verified against the working tree.

---

## 0. What we are NOT chasing (refuted mechanisms — do not re-litigate)

- **Blur pin-cache "crisp-on-miss" fallback is NOT the ISSUE-A wave (A3 REFUTED).** Lyrics rows are `BlurCachePolicy.Normal` (`LyricsView.cs:992`); a miss re-Gaussians *synchronously*, never crisp-inline. The crisp/hold path is gated to real user scroll (`ScrollState.UserScrollActive`), and programmatic auto-scroll is deliberately excluded (`SceneRecorder.cs:281`, `ScrollIntegrator.cs:587`). During auto-follow no lyrics blur is ever held or drawn crisp. **Do not touch `BlurCachePolicy`.**
- **The "crisp white" at line end is NOT the opacity clobber hitting the completed line (B2 REFUTED).** When the wipe completes the line is still `isKaraokeLive` (`LyricsView.cs:859,870`) so its opacity target is already 1.0 — clobbering to 1.0 is a no-op. The terminal look is entirely the glow envelope (ISSUE B). Fix A and B independently.
- **The Resync / dt≤0 step-up frame is NOT a lag cause (C3 REFUTED).** `FrameTimeSource.Resync`→`NextDeltaMs`=0→`ScrollIntegrator.Tick(0)` bails preserving state (`ScrollIntegrator.cs:303-307`); scroll physics is closed-form in dt so a dt=0 step advances by exactly 0. Resync **is** the no-leap fix. **Do not remove it** — that reintroduces the frame-1 scroll leap ("feels 24fps then 120fps").
- **Sigma *stepping* the applied radius per-dist is NOT the C1 fix and is prohibited** — it is the removed design that read as whole-list flicker (`LyricsView.cs:874-877`). The C1 fix in §2 quantizes the **cache key**, never the applied radius (see the sharp distinction there).

---

## 1. Root-cause summary (per issue, plain language)

**ISSUE A — the "visibility wave" (CONFIRMED, root = A1).** A lyric row's dimming is driven entirely by an opacity *spring*, but the row element never declares an element-side `Opacity`: the row BoxEl sets `Blur`/`ScaleX`/`ScaleY` as the springs' rest targets (`LyricsView.cs:985-987`) and silently omits `Opacity`, which then defaults to a static `1f` (`Element.cs:246`). A dimmed row's opacity spring settles within ~0.6 s and is freed from the anim slab. On the next line switch every visible row re-renders in place, and the reconciler *unconditionally re-asserts* `paint.Opacity = 1` (`Reconciler.cs:1950`, guarded only by `!IsBound`). The freed spring then re-seeds its start point from that just-clobbered paint (`AnimScheduler.cs:287,366`), departs from full brightness, and springs back down to the dim target over ~0.4 s. Net: on every slow line change, every settled inactive row jumps bright then re-dims — the whole-panel "other lines become more visible" wave. Blur and scale do *not* wave because their element rest values *are* declared. (A2 — the asymmetric 0.40 s lead / 0.75 s blur-trail springs — is a secondary amplifier that governs how long the wave lingers, but cannot itself *brighten* an above-line, so it is not the root.)

**ISSUE B — the "glow pop" (CONFIRMED, root = B1).** The glow is a duplicate sung-glyph run self-blurred at a *constant* per-line sigma (`baseSigma` 4 rail / 6 large, `LyricsView.cs:583-585`), so its halo grows to full line width at full radius as the wipe reaches ~90 % (the wide mid-line bloom the user noticed). Its retirement is carried by a **bare, strictly-linear 240 ms opacity ramp** on a raw signal (`GlowFadeMs=240` at `:71`; pre-fade `:515-523`; `DriveGlowFades` `:609-623`) at constant sigma, and the whole layer is dropped when sigma hits ~0 (`SceneRecorder.cs:363`) — so the halo dims uniformly and blinks off rather than melting. Worse, on short lines (the "Ma-i-a hi/hu/ho" chorus, ~300–500 ms) the pre-fade is hard-gated off while the line's own in-fade still runs (`_glowInLine != voiceLine`, `:515`); because `DriveGlowFades` runs *before* the pre-fade block the same frame, at in-fade completion it sets alpha=1 and the pre-fade immediately overwrites it with `remaining/240` — a one-frame discontinuity of up to ~0.75. That is the abrupt "glow disappears / snaps to crisp white." BetterLyrics never snaps: its glow ramp-out is pre-scheduled eased keyframes with a ~350 ms out-ramp clamped to line end (`LyricsAnimator.cs:337-342`, `Time.cs:7`).

**ISSUE C — intermittent scroll lag (two mechanisms).** *C1 (PLAUSIBLE, the likeliest explanation for the passive 12.5 fps recording).* Every dimmed row's DoF sigma is a spring, and sigma is folded **verbatim, unrounded** into the blur pin-cache key (`BlurPinKey.cs:96` — confirmed: `BitConverter.SingleToUInt32Bits`, no rounding, while W/H get `RoundGrid` at `:97-98` and the scale matrix M11..M22 is folded verbatim at `:83-85`). So while any of {sigma, scale} animates the key changes every frame → the pin never even mints (`BlurHashSeenLastFrame` false) → a full 2-pass Gaussian of ~7–9 text strips *every frame* for the settle window after each switch, on the Adreno UMA where a Gaussian is raw system-RAM bandwidth. Under `FG_RENDER_ASYNC` (default ON) the GPU-bound render thread presents irregularly and DropOldest discards frames → judder that reads as "sometimes it lags" (smooth between storms, because settled rows pin-hit under pure-translation auto-scroll). *C2 (CONFIRMED code defect, but gesture-scoped).* `ScrollActiveIntoView` has no user-activity gate (`LyricsView.cs:653-719`): it force-sets `Phase=WheelAnimating|PhaseProgrammatic` and zeroes fling velocity on every line change (`:704-709`), stealing a live user wheel/fling in the lyrics list. This only bites if the user manually scrolls the lyrics list during playback (which the passive recording does not establish) — but it is a real bug worth closing.

---

## 2. Fix specification (per issue)

### FIX A — kill the opacity clobber (ISSUE A)

**File:** `C:\wavee\fluent-gpu\src\apps\Wavee\Features\Player\LyricsView.cs`.

There are three ways to close the hole; they differ only in mount-frame behavior. **Recommended: A-hybrid** — it removes the wave *and* introduces no mount flash *and* keeps the leaving-active line dimming smoothly. It is a direct clone of the `_glowAlpha` idiom already proven in this same file (`LyricsView.cs:67-72`).

**A-hybrid (recommended) — bind row Opacity to a per-line signal seeded dim, never rewritten.**
1. Add a per-line array parallel to `_glowAlpha` (declare next to `:72`):
   `FloatSignal[] _rowOpacity = Array.Empty<FloatSignal>();`
   Size it wherever `_glowAlpha` is sized (same lifecycle, at doc load), and initialize **every element to the dim floor `0.16f`** — *not* `1f`. Signals are created at doc-load (reconcile edge), not in the hot phases — identical to `_glowAlpha`, so no phase 6–13 allocation.
2. Pass `_rowOpacity[index]` into `LyricLineView` via its constructor (same plumbing as `glowFade`).
3. On the row BoxEl (alongside `Blur`/`ScaleX`/`ScaleY` at `:985-987`) add:
   `Opacity = (Prop<float>)_rowOpacity[_index],`
4. **Never write `_rowOpacity[i]` after construction.** (This is the one footgun — comment it loudly. A per-frame write would re-fire the binding clobber *and* defeat skip-submit.)
5. Leave `UseSpring(AnimChannel.Opacity, opacity, lead, key)` (`:892`) and the `lead`/`trail` params (`:890-891`) **unchanged** — the spring still owns the in-flight value.

Why this works, frame-exact: `b.Opacity.IsBound` is now true, so the reconciler's unconditional re-assert at `Reconciler.cs:1950` is **skipped** on every re-render (no more clobber-to-1). The bound effect (`Reconciler.cs:984`, `runNow: true`) writes `paint.Opacity` **only at mount and only when the signal changes** — and we change it never — so after mount the anim slab is the sole writer of `paint.Opacity`. Result: (a) far rows never depart from 1 → **no wave**; (b) the leaving-active line's freed spring re-seeds from `CurrentValue = paint.Opacity =` its *settled* 1.0 (not a clobbered value) and springs **smoothly** 1.0→0.44; (c) at panel-open, rows fade **up** from 0.16 to their targets over 0.40 s (a gentle reveal, not a bright flash); (d) mid-song overscan rows mount at 0.16 ≈ their far-dim rest, so no edge flash. At rest no signal is written and reconcile does not touch opacity, so settled frames stay byte-identical (skip-submit intact) — exactly the `_glowAlpha` contract.

**A-fallback (minimal one-liner) — element rest value.** If binding is deemed too much surface, instead add `Opacity = opacity,` (the plain computed rest value) to the row BoxEl at `:985-987`, restoring the row's own documented invariant (`:980-984`). Reconcile then re-asserts the *dim target* instead of 1 → the wave is fully gone, and there is **no mount flash** (mount writes the dim target directly). Its one disclosed residual: on a *slow* line change the leaving-active line's freed opacity spring re-seeds from the just-re-asserted dim target and therefore **steps 1.0→0.44 in one frame** rather than animating. This is the identical mechanism the *scale* channel already has today (element-declared at `:986-987`) — but the opacity delta (0.56) is far larger than the scale delta (0.05), so the step is more visible, and it lands at the handoff, next to ISSUE B's complaint zone. Prefer A-hybrid to avoid introducing that pop.

**A-primary (do NOT use as-is).** Binding to a signal initialized to `1f` (the previous draft) removes the wave and smooths leaving-active, **but re-introduces a bright mount flash**: the bound effect writes `paint.Opacity = 1f` at every genuine mount, so all visible rows flash bright-then-settle together at panel-open, and any not-yet-settled edge row flashes when the 95 ms auto-scroll pulls it in. A-hybrid is A-primary with the seed corrected from `1f`→`0.16f`, which is strictly better. Listed only so nobody re-derives the `1f` seed.

**Do not** try to "fix" A by shortening the 0.75 s blur trail (that is a C1 lever; A2 is only an amplifier). Keep all emphasis channels springs (perceptual-motion contract).

### FIX B — make the glow melt, not blink (ISSUE B)

**File:** `LyricsView.cs`, glow drivers at **510-523** (pre-fade), **594-623** (`BeginGlowFades`/`DriveGlowFades`), **573-587** (constant-sigma writer), **632-651** (`FinishGlowOut`); constant at **71** (`GlowFadeMs`).

Five coordinated changes — all pure scalar math on the existing bound `_glowAlpha` signal plus the `gp.BlurSigma` paint write already done at `:583-585`. No engine change, no allocation, and (like today) writes only while a fade is in flight so rest frames stay byte-identical.

1. **Ease every ramp (replace linear with Sine-Out).** In `DriveGlowFades` (`:609-623`) and the pre-fade (`:515-523`), map linear progress `t∈[0,1]` through `ease(t) = MathF.Sin(t * MathF.PI * 0.5f)` (BetterLyrics' default Sine/EaseOut, `EasingHelper.cs:12`). Removes the "boxy" feel.

2. **Co-decay glow sigma with the retire (the melt).** Today sigma is held constant (`:583-585`) and the layer is dropped whole at the end. During the out/pre-fade window drive
   `gp.BlurSigma = baseSigma * easedAlpha`
   so the halo *tightens* as it dims (matching BetterLyrics easing the GaussianBlur amount to 0, `LyricsAnimator.cs:337-342`). **Keep σ > 0.01 until alpha reaches exactly 0** so the layer-drop (`SceneRecorder.cs:363`) still coincides with invisibility — never let σ hit 0 while alpha > 0 (halo would vanish early), nor alpha hit 0 with σ > 0.01 (halo lingers). `FinishGlowOut` (`:632-651`) stays a no-op-on-pixels cleanup.

3. **Lengthen + clamp the out-window to line end, off the media clock.** Add `const float GlowOutMs = 320f;` (BetterLyrics uses `Time.AnimationDuration = 350`, `Time.cs:7`; 300–350 is right). Drive the out-ramp off time-to-line-end on the media clock (`remaining = pEnd - nowMs`, `:520`), not wall time:
   `alphaOut = ease(Clamp(remaining / GlowOutMs, 0, 1))`.
   This guarantees the glow reaches 0 exactly at the last-syllable `EndMs`, never lingering onto the next line.

4. **Eliminate the short-line snap.** Remove the hard gate `_glowInLine != voiceLine` (`:515`) and make the envelope monotone-continuous by construction:
   `inFade = ease(Clamp(elapsedInLine / GlowFadeMs, 0, 1))`
   `alpha = min(inFade, alphaOut)`
   For a line shorter than `GlowFadeMs + GlowOutMs` the two ramps overlap and `min` yields a smooth triangular envelope that peaks below 1 — no jump-up-then-down. (This is the allocation-free, monotone-by-construction equivalent of BetterLyrics' `CalculateSegmentDuration` in:out = 2:1 re-split, `LyricsAnimator.cs:427-437`.) Keep the `voiceChanged` `BeginGlowFades` cross-fade (`:594-605`) as the seek/early-advance fallback, routed through the same `ease()`.

5. **Taper the mid-line bloom — treat as possibly-necessary, not purely cosmetic.** The user described the *growing* full-width bloom as the setup for the pop; if items 1–4 leave a conspicuous wide bloom right before it melts, the re-recording will still read glow-heavy. As part of this fix, taper the bloom as the wipe completes so it is dimmest just before retiring, e.g. scale the glow alpha (or `baseSigma`) by `(1 - k·max(0, split - splitTaperStart))` with `splitTaperStart ≈ 0.75`, `k` tuned so the halo is ~60–70 % at `split→1`. This mirrors the reference's per-char played-crop glow (which never covers the whole line at once, `LyricsLineRendererBase.cs:261-271`) without adopting the full per-char rework. Land it in the same commit as 1–4 but behind a single tunable constant so it can be dialed to zero if the melt alone proves sufficient. Full per-char parity is a larger rework — out of scope here.

Target behavior: at line completion the halo **melts** (radius tightens + dims together) over an eased ~320 ms ending exactly at the last syllable; short chorus lines compress to a smooth triangle instead of snapping; the "crisp white" end state becomes the end of an eased melt.

### FIX C — bound the scroll cost and stop the control-theft (ISSUE C)

**C2 (CONFIRMED defect, low-risk — add the missing user-activity gate).**
**File:** `LyricsView.cs`, `ScrollActiveIntoView`, before the retarget block at **696-713**. Consult `ScrollState.UserScrollActive` (`Columns.cs:270`, already computed by the integrator as "moving && !Programmatic" across wheel/fling/touch, and currently never read here):

```csharp
// Do not fight a live user gesture in the lyrics list.
if (sc.UserScrollActive) { _resumeAtWallMs = wallMs + ResumeDelayMs; return; }
if (wallMs < _resumeAtWallMs) return;            // grace window after the user stops
// … existing velocity-continuous retarget block (703-713) …
```

- `const float ResumeDelayMs = 1500f;` (BetterLyrics debounces user scroll for 3000 ms, `NowPlayingCanvas.xaml.cs:886-890`; 1500 keeps auto-follow snappier). Add `_resumeAtWallMs` as an instance field.
- **Single-writer invariant:** the fix only *refrains* from `ArmScroll`/`PendingTargetY`; it must never write `sc.OffsetY` itself (the integrator owns the offset). When the grace expires, resume through the normal Programmatic path so the spring chains from the current offset. Leave the fresh-wheel-wins path in `ScrollBy` (`InputDispatcher.cs:3085-3092`) untouched.

**C1 (the elimination — quantize the blur-pin *key*, behind an FG flag).**
This is the confirmed per-frame miss-storm mechanism and the elimination the previous draft wrongly foreclosed. Two verbatim-folded fields defeat the key every frame while a row's springs are in flight: `BlurSigma` (`BlurPinKey.cs:96`) and the scale matrix M11..M22 (`:84-85`). Round **both** to a coarse grid in the key so the cache identity is *stable across consecutive frames* — the pin then mints after one frame and hits for the rest of the bucket — while the **applied, rendered-on-miss** sigma/scale remain the true continuous spring values and the settle frame (`InMotion==0`) re-mints exactly at rest.

**File:** `C:\wavee\fluent-gpu\src\FluentGpu.Engine\Render\BlurPinKey.cs`, behind `FG_BLUR_KEY_QUANTIZE` (read once at startup like the other `FG_*` flags; **default 0 = today's exact per-frame behavior**).

1. `FoldSeed` (`:96`) — bucket sigma using the existing biased `RoundGrid` helper (its +1/512 bias at `:80` exists precisely to stop boundary straddle under float wobble):
   `const float SigmaBucket = 0.5f;`
   `Fold4(ref h, (uint)(int)RoundGrid(L.BlurSigma / SigmaBucket));`
2. `Reb` (`:84-85`) — fold the scale/rotation components on a coarse grid instead of verbatim (W/H at `:97-98` are already integer-`RoundGrid`'d, so size and scale stabilize coherently):
   `const float ScaleBucket = 0.01f;`
   quantize each of `M11,M12,M21,M22` as `RoundGrid(m / ScaleBucket) * ScaleBucket` before folding.

**Why this is NOT the prohibited stepped-sigma design:** that design quantized the **applied radius** on **every visible row simultaneously**, so a one-index active advance stepped every row's σ+size on the *same* frame → synchronized whole-list flicker (`LyricsView.cs:874-877`). This change quantizes only the **cache key (identity)**. The animation the user sees is still the continuous spring; a MISS always renders the exact current value; each row crosses bucket boundaries at *independent* times (different current σ/scale and targets), so there is no synchronized step; and the resting pin is pixel-exact (settle re-mint). The only visible consequence is that a *hit* composites a pin minted ≤ one bucket (≤0.5 σ / ≤1 % scale) away from the instantaneous value, on a **dimmed (0.16–0.55 opacity), already-blurred** peripheral line — sub-perceptual. Storm cost drops from ~7–9 strips × 2 Gaussians × ~45 frames per switch to ~7–9 strips × a handful of bucket-crossings per switch.

**Complementary app-side mitigation (always safe, independent of the flag).** Shorten the blur trail from 0.75 s toward the lead at `LyricsView.cs:891`: `trail = SpringParams.FromResponse(0.55f, 1.0f);`. This shrinks the settle window regardless of the key change and keeps the soft-focus feel; ship it even if the engine flag is held back. Note `DofSigma(dist)=5·min(dist/5,1)` saturates at dist≥5 (`:20-23`), so only dist 1–4 rows ever animate sigma — the storm is already bounded to the near band; do not widen it.

**Instrument to confirm C1 dominates the passive recording** before/after: `FG_FPS_LOG` (`LastGpuFenceWaitMs`, `AppHost.cs:478-480`) + `Diag d3d12 blurCacheMiss` (`D3D12Device.cs:613`) over a scripted line-switch sequence; A/B with `FG_RENDER_ASYNC=0` to confirm the async DropOldest path is what surfaces the GPU-bound frames as judder. Expect `blurCacheMiss` per switch to fall by ~1–2 orders of magnitude with `FG_BLUR_KEY_QUANTIZE=1`.

---

## 3. Ordering, landing sequence, escape hatches

Land in this order; each step is independently shippable and verifiable.

1. **FIX A (A-hybrid)** — top user-visible win, app-side only, no engine change, mirrors the proven `_glowAlpha` idiom. No flag; revert = remove the array/binding.
2. **FIX B (glow melt + bloom taper)** — app-side only, medium risk (timing logic). No flag; revert = restore the linear ramps + `GlowFadeMs`. Bloom-taper behind its own tunable constant.
3. **FIX C2 (scroll user-gate)** — app-side, low risk, closes a real defect. No flag; `ResumeDelayMs` is the knob.
4. **FIX C1** — ship the app-side trail-shorten first (safe), then land the engine key-quantize behind `FG_BLUR_KEY_QUANTIZE` (default 0), enabling it only after the instrumentation in §2 confirms the miss-storm and after a green race/screenshot pass. This is the C1 *elimination*; the trail-shorten alone is only mitigation.

**Flag conventions.** App-side A/B/C2 and the trail-shorten are constants/logic needing no flags — keep each in its own commit for trivial revert. The only engine change (C1 key-quantize) is a cross-cutting change to a canon contract and **must** sit behind `FG_BLUR_KEY_QUANTIZE=0`-default. Because it edits the BlurPinKey contract, reconcile the owning doc `design/subsystems/backdrop-effects-animation.md` (§FA-2a) and run `docs\design\check-canon.ps1` (exit 0) before landing. Reuse the existing `FG_RENDER_ASYNC=0` hatch (`AppHost.cs:218-225`) as the async A/B lever during diagnosis. The app-side A/B/C2 changes touch no canon tokens.

---

## 4. Risks and what could regress

- **FIX A (A-hybrid):** the row is interactive but declares no `HoverOpacity`/`PressedOpacity` (NaN), which compose separately from the bound base Opacity (`Reconciler.cs:1951-1952`), so hover/press are unaffected. The `_glowAlpha` precedent proves the path. Primary footgun: **any** future write to `_rowOpacity[i]` silently reinstates the clobber and breaks skip-submit — comment it. Cosmetic note to disclose so it is not re-reported as a bug: panel-open now fades lines up from 0.16 over 0.40 s (a reveal, intended).
- **FIX B:** the eased+compressed envelope must reach 0 by line end — driving the out-ramp off media-clock `remaining` guarantees this; verify no path leaves σ>0.01 with alpha=0 or σ=0 with alpha>0, and no negative-duration math on very short lines (the `min()` form is monotone by construction). The bloom-taper `k`/`splitTaperStart` are visual tuning the user signs off; keep them behind constants.
- **FIX C2:** the resume-delay must not permanently disable auto-follow after one user scroll — the `_resumeAtWallMs` timeout handles that. Must never write `OffsetY`.
- **FIX C1 (key-quantize):** applies engine-wide to every self-blur pin, but static-σ/static-scale blurs (Mica, etc.) round to a stable bucket → identical key to today → no behavior change; only animated-σ/scale blurs are affected, of which lyrics DoF is the main one. A MISS always renders exactly and the settle frame re-mints exactly, so there is no permanent staleness. Residual risk is a sub-perceptual within-bucket lag of the composited halo on dimmed rows; the flag default-0 and the screenshot gate cover it. Do **not** reintroduce applied-radius stepping, and do **not** extend the `SceneRecorder` self-blur defer to programmatic auto-scroll (that was `bd393e3` BUG1, the whole-panel DoF dropout) — if ever attempted it needs its own flag.
- **Cross-issue independence:** A touches only opacity (not the blur storm); B's σ co-decay adds at most one retiring-strip Gaussian per line (negligible for C). A, B, C are independent.

---

## 5. Validation

**Visual re-recording (what the user should look for):**
- **A:** on a *slow* verse switch, the other inactive lines must **not** brighten/sharpen — only line A dims and line B brightens (clean handoff). With A-hybrid the leaving line dims *smoothly* and there is no bright pop anywhere; on opening the panel lines fade up gently. Watch the exact "Ma-i-a ha-ha" → "Alo, salut…" transition.
- **B:** at line completion the halo must **melt** (radius tightens + dims together) over ~320 ms ending at the last syllable — never blink off in one frame; the mid-line bloom should read calmer than before. Watch the short chorus lines ("Ma-i-a hi/hu/ho") where the snap was worst.
- **C:** manually scroll the lyrics list during playback — it must **not** yank back on the next line change until you stop for ~1.5 s (C2). For C1, watch for judder in the settle window after each switch and compare `FG_FPS_LOG` GPU-fence times / `blurCacheMiss` counts with `FG_BLUR_KEY_QUANTIZE` off vs on.

**Existing gates:**
- `dotnet build src/FluentGpu.slnx` clean, then `dotnet run --project src/FluentGpu.VerticalSlice` → **"ALL CHECKS PASSED"** with zero-alloc phases 6–13 green. A's `_rowOpacity` signals allocate at doc-load (reconcile edge, before phases 6–13), matching the `_glowAlpha` precedent — no allocation in the gated hot phases. B/C2 are scalar math. The C1 key change is `stackalloc`-only on the record hot path (`BlurPinKey` is already documented zero-heap, `:22`). Confirm `_rowOpacity` is never written per-frame.
- `--screenshot` does **not** exercise the lyrics panel (known gap) so it will not directly cover these fixes, but the C1 key-quantize's blur-pin path and the reconciler/anim-slab seams A relies on are covered generically by the slice's ~60 golden checks; run `--screenshot` on the gallery to confirm the engine key change produces no static-blur regression. Adding a scripted lyrics line-switch golden (assert `blurCacheMiss` bounded per switch) is a recommended follow-up, out of scope for the fix.
- `docs\design\check-canon.ps1` (exit 0) after reconciling `backdrop-effects-animation.md` §FA-2a for the C1 key change; the app-side A/B/C2 changes alter no canon tokens.