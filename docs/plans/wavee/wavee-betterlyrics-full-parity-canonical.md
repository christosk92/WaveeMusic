<!--
Wavee × BetterLyrics — FULL-PARITY CANONICAL technical design.
Authored 2026-06-29. Lead architect. This is the single canonical design for FULL BetterLyrics effect
parity in the Wavee FluentGpu app — the maximalist tier, every effect IN SCOPE.

It EXPANDS (does not duplicate) three existing docs, which remain authorities for their slices:
  - docs/wavee-lyrics-canonical-design.md       -> OWNS: the right-rail shell integration (ShellUi, mount,
                                                    open/close, tabs, chrome). Kept verbatim; this doc adds the
                                                    SECOND surface (fullscreen Expanded) and maps effects across both.
  - docs/betterlyrics-parity-plan.md            -> OWNS: the original BetterLyrics inventory + E1-E7 first cut and
                                                    the per-feature parity table. This doc supersedes its "out of
                                                    scope / default-off" verdicts (D3, E7, §6.2) now that the bar is
                                                    full parity, and corrects two false claims (see §6).
  - docs/lyrics-aggregator-reranker-plan.md     -> OWNS: the data pipeline (aggregator, reranker, providers, wiring).
                                                    Referenced in §4; NOT redone here.

This doc OWNS: the surface split (rail vs fullscreen), the complete effect inventory at full parity, the full
FluentGpu implementation per effect cluster with every new engine primitive specified (op/payload/shader/channel/
src-home/effort), the consolidated engine-work table, the front-loaded roadmap that ships the signature effects
EARLY, and the honest hard-limits scorecard driven by the verified adversarial verdicts.

Honesty discipline (binding, from CLAUDE.md): "near-zero-allocation" not "zero" (zero per-frame managed alloc only
on render phases 6-13; bounded Gen0 at the reconcile/record edge). Name the real limits with their verdict. Every
file:line anchor below was re-verified against live src/ and app/ this session (2026-06-29).
-->

# Wavee × BetterLyrics — FULL Effect Parity (Canonical Design)

> **SUPERSEDE NOTE — char pop removed 2026-08-03.** Refuted by frame-verified Apple Music footage: there is **no glyph
> magnification** anywhere in the reference animation. The engine's `LayoutRunGradient` used to couple `GlyphWipe.Lift > 0`
> to a ≈1.12× per-glyph scale pop; that coupling is deleted, and `Lift` is now **dy-only** (an unsung glyph sits `Lift` DIP
> below its baseline and rises to it as the feather band sweeps it; a sung glyph never moves). BetterLyrics' own
> `1.0→1.15→1.0` syllable pop, inventoried in §2.2 below, remains an accurate description of *BetterLyrics* — it is simply
> no longer a Wavee target. Every "scale-pop" mention below is superseded on that point only; the float/glow/wipe
> descriptions stand. Authority: the frame-verified Apple-Music behavioral spec (2026-08-03 lyrics-parity campaign) +
> the `FluentGpu.Foundation.GlyphWipe` XML contract.

**Lead architect's brief, restated.** The bar is the **full BetterLyrics look**, not the tamed WaveeMusic panel: procedural shader backdrops (fluid / fog / rain / snow), the soft sweeping gradient karaoke wipe, per-character float + glow + scale-pop *(pop superseded — see the note above)*, continuous per-syllable color, the 2D and 3D fan, the spectrum visualizer, vertical CJK. **Everything is in scope.** The only acceptable cuts are *true* engine or physical limits — and each such cut is named in §6 with its verified verdict.

This document is decision-ready. It is the new canonical design for the lyrics feature's *maximalist* tier; the right-rail product doc remains canonical for the rail shell it owns.

---

## 1. Executive summary, surface split, and the real engine cost

### 1.1 The two-surface decision (the single most important architectural call)

The full BetterLyrics effect set **cannot** all live in a 300px right rail. A rail that is `Shrink=0, Width≈300` (docs/wavee-lyrics-canonical-design.md §2.1) physically cannot show a fluid/fog backdrop, a spectrum visualizer, or a 3D perspective fan and read as anything but noise. So the design uses **two distinct surfaces**, and maps each effect to where it belongs:

| Surface | What it is | Host (verified) | Effects it carries |
|---|---|---|---|
| **Rail** (compact) | The Spotify-style right sidebar lyrics tab — the calm, readable, always-available view. | `RightRail`→`LyricsView`, third child of `WaveeShell.cs:195-294`'s sidebar+content row (docs/wavee-lyrics-canonical-design.md §2). | Line-level scale/opacity/2D-fan, scroll-follow, **word karaoke (soft wipe)**, **per-character float/glow/~~scale-pop~~** *(pop superseded 2026-08-03 — refuted)*, continuous per-syllable color, neighbour fade/scale, edge-fade vignette, multi-layer (translation/phonetic). |
| **Fullscreen "Expanded"** (maximalist) | The immersive full-now-playing takeover, lyrics replacing the hero column. The full BetterLyrics canvas. | `NowPlayingView` via `NowPlayingLayer`, a top ZStack layer in `WaveeShell.cs:317` gated on `PlaybackBridge.Expanded` (`Signal<bool>`, `PlaybackBridge.cs:60`; layer `NowPlayingView.cs:21-30`). | **Everything the rail has, PLUS** procedural backdrops (fluid/fog/rain/snow), blurred rotating album cover, the spectrum visualizer, the 3D perspective fan, bass-reactive breathing, the full edge-feather + compositor stack, vertical CJK. |

**The effect→surface map, explicitly:**

```
RAIL  = line-level (scale/opacity/2D-fan/scroll)          [shipped channels, zero engine work]
      + word karaoke soft wipe                            [E1]
      + per-character float / glow / scale-pop            [E3 per-glyph transform; glow via E1+blur]
        (scale-pop SUPERSEDED 2026-08-03 — refuted; float = GlyphWipe.Lift, dy-only)
      + continuous per-syllable color                     [E2]
      + neighbour fade/scale + edge-fade vignette         [shipped]
      + multi-layer (primary/translation/phonetic)        [control composition, zero engine work]

FULLSCREEN = RAIL  (all of the above, at large type)
      + procedural backdrops fluid/fog/rain/snow          [E7-proc: LayerKind.ProceduralBg + 4 HLSL]
      + blurred rotating crossfading album cover          [shipped primitives + cached pre-blur RT]
      + spectrum visualizer (bars / curve / ring)         [E7-fft + geometry]
      + bass-reactive breathing                           [shipped ScaleX/Y, driven by E7-fft]
      + 3D perspective fan                                [E-3D: LayerKind.Transform3D]  (genuinely hard)
      + vertical CJK orientation                          [E-CJK Tier 1: per-glyph stacking via E3] (genuinely hard)
```

Both surfaces are driven by **one** shared lyrics render-VM and **one** shared smooth-clock anchor (`UseSmoothPositionMs`, factored from `SeekBar.cs:56-101`), so they never drift relative to each other or to the seek bar. The rail mounts its ticker only while `RailOpen && RailMode==Lyrics && IsPlaying`; the fullscreen surface mounts its ticker only while `Expanded && IsPlaying` (the `NowPlayingLayer` already early-returns an inert box when collapsed, `NowPlayingView.cs:28`). The GPU idles at rest on both.

### 1.2 The honest headline: how much engine work full parity really takes

The line-level view is **zero engine work** and ships first. The full signature is **L–XL of bounded, well-scoped engine work**, concentrated in a small number of primitives, each landing behind its own VerticalSlice gate. There is **no** rewrite of the renderer, the reconciler, or the threading seam. The GPU is, in several decisive places, *already capable* and the work is "feed it non-uniformly":

- **Per-glyph color and per-glyph transform need NO GPU-struct or shader change.** `GlyphInstance` already carries per-instance `R,G,B,A` and a full per-instance 2×3 affine `M11..Dy`, and the vertex shader applies them per-instance (`GlyphRenderer.cs:17-26` struct, `:192-205` VS). `Replay` collapses every glyph in a run to one uniform `world`+`color` today (`:566-580`), and the per-glyph color seam is **already proven** via the span path's parallel `ColorF[] colors` array (`:571`). So E2/E3 are "non-uniform feeding of `Replay`," not GPU work. *(Verdicts `text-glyph/glyph-instance-per-color-xform`, `line-anim-compositor/glyph-per-instance` — both SUPPORTED.)*
- **The soft karaoke wipe is the existing gradient PS multiplied by the existing R8 coverage.** The gradient pixel shader already evaluates a ≤4-stop linear gradient along a local axis via `seg()`/`lerp()` with per-segment `saturate` (`GradientPipeline.cs:73-94`); the glyph PS already does `coverage × color × opacity` premultiplied (`GlyphRenderer.cs:207-211`). The wipe fuses them: substitute the gradient `col.rgb` for the flat `i.color.rgb`, keep `× R8 coverage`. *(Verdict `text-glyph/gradient-ps-maps-to-glyph` — SUPPORTED; the only nuance is the new pipeline must bind the glyph atlas + per-glyph UVs, so it is a *fusion* of two existing shaders, not one shader unchanged.)*
- **Procedural backdrops ride the existing layer machinery, not a new DrawOp.** `LayerKind.EdgeFade=3` was the most recent of four layer kinds added on the `PushLayer`/`PopLayer` pair; a 5th (`ProceduralBg=4`) is the identical extension pattern through the `SubmitWithLayers` if/else dispatch — no exhaustive switch, no new pooled-RT lifecycle. *(Verdict `backdrop-spectrum/proc-bg-layerkind` — SUPPORTED.)*

The genuinely hard items — and they are **hard, not blocked-forever** — are the **3D perspective fan** (no `Matrix4x4`/perspective anywhere in `src/`; needs a new `LayerKind.Transform3D`) and **vertical CJK layout** (the DirectWrite path is horizontal-only). Both are default-off in BetterLyrics, both are bounded new-primitive work in a clean seam, and both are specified concretely in §3 and scored in §6.

The two corrections to the prior docs (§6 details): the **ComputeSharp C#→HLSL transpiler is NOT vendored** — the four backdrop shaders are **hand-authored HLSL** compiled at runtime via the existing `ShaderCompiler.Compile`→`D3DCompile` path (`ShaderCompiler.cs:12`), which is the convention every existing pipeline uses; and **there is no audio FFT/spectrum/bass-energy source anywhere** — it must be added at the `IAudioHost` seam (`AudioHost.cs:31`), which is blocked until the real audio host lands (the same true data-availability limit as the lyrics feed).

---

## 2. Full BetterLyrics effect inventory (first-hand, grouped)

This is the complete target, grouped by cluster. Each line names the parameters that drive the BetterLyrics renderer (source refs are BetterLyrics). This expands docs/betterlyrics-parity-plan.md §2 — it is the maximalist superset, with nothing dropped.

### 2.1 Per-line animation (the neighbour treatment)
Driven by `LyricsAnimator.UpdateLines` (`LyricsAnimator.cs:17-383`), which recomputes targets every frame from `(isThisLinePlaying, distanceFromPlayingLine, palette, settings, nowMs)` and gates the rewrite on a 5-flag change set (`:110`). `distanceFactor = Clamp(|line.center − primary.center| / spaceBefore-or-after, 0, 1)` (`:52-58`).
- **Scroll-follow**: ease the column so the focal line's center lands at the `PlayingLineTopOffset` band (default 50%); `LyricsScrollDuration=500ms`, Quad/Out, per-direction distance-scaled duration+delay (`:129-150`).
- **Scale** `0.75 ↔ 1.0` by `distanceFactor` (`ScaleTransition`, `:` defaults `defaultScale=0.75`, `highlightedScale=1.0`).
- **Blur** `0 → 5σ·distanceFactor` (depth-of-field on inactive lines; `BlurAmountTransition`).
- **2D fan angle** `0 → fanAngleRad·distanceFactor·(below?+1:−1)` about the line center (`AngleTransition`, `FanLyricsAngle=30°`, default off).
- **Opacity ×4**: played-primary, unplayed-primary, secondary, tertiary; whole-line fade `(1−distanceFactor)·base` (`:` #6-#9).
- **3D parallax** (`Is3DLyricsEnabled`, default off): a real `Matrix4x4` with `M34=1/depth`, `Lyrics3DDepth=800`, sensor/manual X/Y/Z angles (`LyricsRenderer.cs:230-250`).
- **Breathing** (default off): bass-energy uniform scale pulse, applied as a CPU `Matrix3x2.CreateScale` on the drawing session before draw (`EffectRendererBase.cs:34`), asymmetric smoothing.

### 2.2 Per-character / karaoke (the signature)
- **Sync predicates** (every node, `BaseRenderLyrics.cs:5-17`): `GetIsPlaying(t)=StartMs<=t<EndMs` (half-open); `GetPlayProgress(t)=Clamp((t−StartMs)/DurationMs,0,1)` with a **NaN guard**; `IsPlayingLastFrame` is a one-frame edge latch the animator mutates to fire pulses once.
- **Karaoke fill — the soft sweeping gradient wipe** (`LyricsLineRendererBase.cs:173-204`): per text region, a **4-stop horizontal gradient** masked through the white glyph fill, with a moving split = `playedWidth/regionWidth`. Stops: `stop0@0=played, stop1@progress=played, stop2@progress+fade·firstCharProg=unplayed, stop3@1+fade=unplayed`, `fade=0.5/charCount` (the soft leading edge = half one char's share). Continuous, clock-driven. A **parallel** 4-stop gradient runs through the stroke mask sharing the same split (`:185-197`).
- **Continuous per-syllable color**: played≠unplayed fill/stroke colors on the active line; the ~6 `*ColorTransition` channels (AppColor, 0.3s, `LyricsAnimator.cs:191-205`). Off-lines are a single grayed color.
- **Per-char float** `0→peak→0`, fires once on syllable start; `LyricsFloatAnimationDuration=450ms`, per-char begin delay; amount `lineHeight·0.1` (`:286-292`).
- **Syllable scale-pop** `1.0→1.15→1.0`, Sine, ~350ms, pivot = syllable center; gated to syllables ≥700ms (`:316-329`). *(Accurate for BetterLyrics; **NOT a Wavee target** — char pop removed 2026-08-03, refuted by frame-verified Apple Music footage.)*
- **Per-char glow / bloom** `0→glowPx`, a Gaussian blur of the **played sub-region** of the char drawn enlarged beneath the crisp glyph (additive-style bloom via over-draw), cropped to `progressPlayed`; gated to syllables ≥700ms; amount `lineHeight·0.2` (`LyricsLineRendererBase.cs:261-269`, `RenderLyricsChar.cs:42-43`).
- **Char timing** is synthesized uniformly inside a syllable (`avgCharDuration = syllable.DurationMs/length`), even for real word-by-word data (`RenderLyricsChar.cs:11-58`).

### 2.3 Multi-layer text & layout
- **Three stacked text layers per line**: Tertiary (phonetic, bottom), Primary (original, middle = the karaoke layer), Secondary (translation, top); draw order `[Tertiary, Primary, Secondary]`; per-layer opacity 60/100-or-30/60 (`LyricsLineRendererBase.cs:25-30,46,64,99-100`).
- **Glyph stroke/outline** (`LyricsFontStrokeWidth`, default 0): a second white mask from the glyph *outline geometry* (`GetGlyphRunOutline`, round join/caps), with its own 4-stop gradient, composited **stroke under fill** (`RenderLyricsLine.cs:294-312,380-383`; `RenderLyricsRegion.cs:24-28`).
- **Lane system** (duets / TTML x-bg): overlapping lines packed into lanes via greedy interval-packing (50ms tolerance, lane-0-wins, `CalculateLanes`); Left/Center/Right alignment from `AgentId` (`CalculateAlignments`); the resolver picks the lowest-lane line covering `now` (`LyricsSynchronizer.cs:39-49`). Alignment sets the scale/rotation/fan pivot (`HorizontalLyricsLayoutStrategy.cs:175-183`).
- **Vertical CJK orientation** (`VerticalLyricsLayoutStrategy`): right-to-left vertical columns, X-as-scroll-axis (optional mode).
- **Marquee** X-offset per layer for non-wrapping overflow; the three layers phase-locked.
- **Edge-fade vignette**: a whole-scene alpha mask (center white + 4 linear edge bands + 4 radial corners) via `CreateLayer(brush)` (`EdgeFadeMaskRenderer.cs`); plus a lyrics-only scroll-axis fade.

### 2.4 Backdrops
- **Blurred + slowly rotating + crossfading album cover** (`CoverBackgroundRenderer`): `GaussianBlur≈100` DIP, oversized to the screen diagonal (so rotation never shows corners), `baseSpeed=0.6`, 0.7s linear crossfade on track change, optional breathing + parallax.
- **Fluid** procedural gradient (`FluidBackgroundRenderer`/`FluidBackgroundEffect`): a domain-warped value-noise mesh-gradient blending 4 palette accents, optional LightWave chromatic overlay + Valve triangular-PDF dither + HSV blend. Default ON with dithering.
- **Fog**, **Raindrop** (on-glass), **Snow** procedural shaders.
- **Pure-color overlay** (palette × opacity) behind everything.
- **Composite order** (`DrawCore`, back→front): pure-color → cover → fluid → spectrum → snow → fog → raindrop → lyrics, optionally wrapped in the edge-feather mask; all premultiplied SourceOver except the spectrum's additive glow.

### 2.5 Spectrum visualizer
- **Three modes** (`SpectrumRenderer.cs`): **Bar** (linear rects), **Curve** (Catmull-Rom cubic Béziers, factor 0.1666), **AroundAlbumArt** (bars on a rounded-rect perimeter, normals pushed outward, closed loop). Amplitude scale `0.05·viewHeight`. Fill: linear gradient transparent→`SpectrumColor` (or radial for the ring); glow = blur 16 + additive blend.
- **Audio FFT source** (`SpectrumAnalyzer.cs`): WASAPI loopback → 2048-point Hamming FFT → per-bin perceptual compensation curve (20 Hz→1.0 … 4 kHz→3.5 … 20 kHz→12.0) → peak-tracking auto-gain → symmetric stereo layout → downsample to `BarCount` (default 64; overlay default 32) → asymmetric smoothing (attack `·0.92+target·0.08`, release `·0.98`). **Bass energy** = sum of the lowest 5 bins clamped [0,1], drives breathing on every backdrop + the lyrics.

---

## 3. Complete FluentGpu implementation (per cluster, with every new engine primitive)

Each new primitive names: the DrawOp/payload or LayerKind/channel, the shader (and whether via the ComputeSharp transpiler — **never; it is not vendored** — or hand-authored HLSL), the `src/` home (honoring **TerraFX-free `Controls`**; HLSL/PSO/atlas only in `FluentGpu.Windows`; **zero managed alloc in render phases 6-13**), and an effort size (S/M/L/XL).

**The one architectural through-line (from BetterLyrics, and FluentGpu already is this):** glyphs are baked once to a pure-white R8 coverage mask; *all* color and the karaoke split come from a gradient/color composited *through* that mask. The R8 coverage atlas (`GlyphRenderer.cs:128`, `DXGI_FORMAT_R8_UNORM`) **is** BetterLyrics' white mask. So the karaoke signature is not a new paradigm — it is the gradient PS that already exists, masked by the R8 coverage that already exists.

### Cluster A — Text / glyph layer (the karaoke signature, color, per-letter motion, stroke, glow)

#### A1 — Soft sweeping gradient karaoke wipe — **L** — *new op + glyph-gradient PS*
**Why a new op is genuinely required:** `DrawOp` is closed (14 members, `DrawList.cs:7-20`); `DrawGlyphRunCmd` carries exactly one flat `ColorF` (`:80-82`); gradient is rect/stroke only (`DrawGradientRect=8`, `DrawGradientStroke=11`), never sampled through R8 coverage. *(Verdict `text-glyph/gradient-ps-maps-to-glyph` — SUPPORTED: a new op is genuinely required, not already-present.)*

**(a) New opcode + payload — `src/FluentGpu.Engine/Render/DrawList.cs`.** Append `DrawGlyphRunGradient = 15` (append-only — the int IS the wire tag, `:351`; never renumber). New payload, a `DrawGlyphRunCmd` superset carrying the gradient:
```csharp
// A glyph run whose fill color is a ≤4-stop linear gradient sampled along the RUN-LOCAL axis (Axis: 0=X L→R, 1=Y T→B),
// masked by the R8 coverage. The karaoke wipe: stops C0..C3 at offsets O0..O3 are the played→soft-edge→unplayed ramp;
// the recorder updates the offsets each record from the active line's clock (Split = GetPlayProgress × regionFrac).
public readonly record struct DrawGlyphRunGradientCmd(
    RectF Bounds, StringId Text, StringId Family, float FontSize, int Weight, int Wrap, int Trim, int MaxLines,
    float CharSpacing, float LineHeight, int LineStacking, int LineBounds, Affine2D Transform, float Opacity,
    ColorF C0, ColorF C1, ColorF C2, ColorF C3, float O0, float O1, float O2, float O3, int StopCount,
    float AxisStart, float AxisEnd,   // run-local DIP extent of the gradient axis (region x0..x1 within the run)
    int Axis = 0, int SpanRunId = 0, int InMotion = 0, int StrokeFlag = 0, float StrokeWidth = 0f);  // StrokeFlag/Width feed A5
```
Add a `DrawGlyphRunGradient(...)` writer mirroring the existing `DrawGlyphRun` writer (`WriteOp`+`WritePayload<T>`, `:348-363`) and the skip-stride case in the headless/size walk (mirror the existing `DecodeOne`/`StreamHasLayer` stride for `DrawGlyphRun`). POD `MemoryMarshal.Write` into the reused byte buffer — no managed alloc, identical to every existing op.

**(b) Recorder emit — `src/FluentGpu.Engine/Render/SceneRecorder.cs`, Text case (~`:491-519`).** When the node carries a karaoke-gradient spec (a sparse side-table `KaraokeWipe { ColorF Played, Unplayed; float Split; float FadeFrac; float AxisStart, AxisEnd; byte Axis }` — see A1.split), emit `DrawGlyphRunGradient` instead of `DrawGlyphRun`. Fold the 4 stops as BetterLyrics does:
```
O0 = 0;                                 C0 = Played
O1 = Split;                             C1 = Played
O2 = min(Split + Fade·firstCharProg, 1); C2 = Unplayed     // the SOFT leading edge
O3 = 1;                                 C3 = Unplayed
StopCount = 4
```
FluentGpu's PS uses `saturate()` per segment (`seg()`, `GradientPipeline.cs:73`), so `O3=1.0` with `C3=unplayed` already yields a flat plateau — BetterLyrics' `1+fade` terminal (which only defeats Win2D's gradient-brush edge clamp) is unnecessary. **A true simplification, not a cut.** All stop math is stack-local (zero alloc). `AxisStart/AxisEnd` come from the region's `GetRangeRects` (`Text.cs:77`; one fragment for a non-wrapped line, per-fragment for wrapped — A1.wrap).

**(c) GPU — `src/FluentGpu.Windows/D3D12/GlyphRenderer.cs`.** **Extend the existing glyph pipeline with a gradient mode** (do not duplicate the atlas/run-cache as a second pipeline). Add a small per-DRAW constant block (the 4 stops + offsets + axis + the run's local `AxisStart/AxisEnd`) bound via root constants, and a shader permutation `PSMainGradient` selected by a second PSO. The VS already computes `lp` (local point, `:195`); pass `lp.x` (Axis=0) or `lp.y` (Axis=1) to the PS as a varying. The gradient PS is the fusion of the two shipped shaders:
```hlsl
float4 PSMainGradient(VSOut i) : SV_Target {
    float a = gAtlas.Sample(gSamp, i.uv).r;                          // R8 coverage = BetterLyrics' white mask (GlyphRenderer PSMain :209)
    float axisLen = max(gGrad.axisEnd - gGrad.axisStart, 1e-4);
    float t = saturate((i.localAxis - gGrad.axisStart) / axisLen);   // run-local position 0..1 along the wipe axis
    float4 col = gGrad.c0;                                            // identical to GradientPipeline.cs:91-94
    if (gGrad.stopCount >= 1.5) col = lerp(col, gGrad.c1, seg(gGrad.o0, gGrad.o1, t));
    if (gGrad.stopCount >= 2.5) col = lerp(col, gGrad.c2, seg(gGrad.o1, gGrad.o2, t));
    if (gGrad.stopCount >= 3.5) col = lerp(col, gGrad.c3, seg(gGrad.o2, gGrad.o3, t));
    float aOut = col.a * a * i.opacity;
    return float4(col.rgb * aOut, aOut);                             // premultiplied — same posture as PSMain (:211)
}
```
Because the gradient is evaluated in **run-local DIP space (pre-`world`)**, the wipe is correct under any per-line scale/scroll/fan transform for free — the affine maps local→device *after* the gradient is sampled. The `LayoutRun` path is unchanged (same `RunKey`, same cached quads, same `Replay`); only the PSO + per-draw constants differ. **The run cache, atlas, and zero shaping cost are all reused.**

*Batching:* `Replay` appends `GlyphInstance`s into one shared list drained by one `DrawInstanced`. Gradient stops are per-region (per active line), so the wipe run is its **own** small batch: flush the flat-glyph batch, set the gradient PSO + root constants, draw the active line's instances, restore. At panel/fullscreen scale only 1–3 lines wipe simultaneously (current line + maybe a duet lane) → 1–3 extra draws/frame, negligible. The constants block is written at record time (render thread), via `MemoryMarshal.Write` into the persistently-mapped upload buffer (the gradient pipeline already double-buffers per frame-in-flight, `GradientPipeline.cs:33,203-208`) — no phase 6-13 alloc.

**(A1.split) The split clock (zero-alloc, time-independent across seek/pause).** Drive `Split` via the **existing `Drive` path** (`AnimScheduler.Timeline.cs:52-81`), not per-frame seeding. Add `AnimChannel.WipeSplit` as a **side-table channel** (the BrushFade/HoverFade pattern, `AnimScheduler.cs:239-250`): `IsSideTableChannel` returns true, `WriteSideTable` calls `_scene.SetKaraokeSplit(node, v)`. `Drive` advances it as `u = clamp01((Clocks.Sample(ref) − domainMin)/span)` — purely a function of the sampled clock, **no dt term** (`Timeline.cs:75-81`), so it stays synced across seek/pause. Side-table channels skip PASS2 (Accum/Compose) entirely (`:122,128`) — the cheapest correct path. For non-uniform char widths, bake a small per-line keyframe table mapping clock-ms → cumulative-width-fraction at char boundaries (built once from `GetRangeRects`); `Drive(node, WipeSplit, keys, mediaClockRef, lineStart, lineEnd)` reproduces BetterLyrics' width-weighted split exactly. The `keys` array is allocated once per line at the reconcile edge (`_keysBySlot` insert, `Timeline.cs:66`) — fine; never per-frame. **Register the media clock ONCE app-lifetime and reuse the `drivenRef` for every line** — `DrivenClockTable.Register` is append-only (`AnimTypes.cs:67-72`, confirmed leak), so this is the load-bearing R11 discipline, not optional. *(Verdict `text-glyph/no-color-lane-scalar-slab`, `line-anim-compositor/edgefade-blur-drive` — both SUPPORTED, with the noted nuance: feed `Drive` a stable/reused keyframe array, not a fresh one per frame.)*

**(A1.wrap) Wrapped lines:** `GetRangeRects` returns one `RectF` per line fragment for `[start,end)` (`DirectWriteFontSystem.cs:81-85`). A wrapped lyric line = N fragments = N regions, each its own `AxisStart/AxisEnd` and split sub-range. The render VM computes per-fragment ranges once at the reconcile edge (the `SpanRunRects` baked-snapshot idiom, `SpanText.cs:51-75`, is the precedent), then emits one `DrawGlyphRunGradient` per fragment.

**Acceptance:** new VerticalSlice gate asserts the `DrawGlyphRunGradient` op + 4 stops + `Split` headlessly under the alloc tripwire (zero phase 6-13 alloc — the wipe rides `Drive` + a side-table write); `--screenshot` soft-edge diff vs a hard `ClipR` reference. When this lands, any Phase-1 hard-edge karaoke swaps to soft with **no control rewrite** (the control sets `KaraokeWipe` instead of `ClipR`).

#### A2 — Continuous per-syllable color — **M** — *no GPU change; non-uniform `Replay` feed*
**Status — the seam is ALREADY BUILT and proven.** `GlyphInstance` carries per-instance `R,G,B,A` (`:22`); `Replay` already varies color per glyph via the parallel `colors[]` array (`:571`); the span path bakes `colors[i]` from span styles on the miss path (`:615`, `colors = new ColorF[n]` at the cache miss only). Per-glyph solid color is fully wired end-to-end **today**. *(Verdict `text-glyph/glyph-instance-per-color-xform` — SUPPORTED.)* The only gap: no *animated* source feeds it, and a span-style change re-mints `SpanRunId` and reshapes the whole line.

**Implementation — animated per-glyph color WITHOUT reshaping.** The played/unplayed *split* colors are already A1's gradient (no per-instance work needed). A2 is only for continuous per-syllable ramps that are NOT a positional wipe (each syllable fades through an accent as it's sung):
- New sparse side-table `GlyphColorRamp { ColorF[] PerGlyph; int Count }` keyed by node — `src/FluentGpu.Engine/Scene/Columns.cs` (+ `SceneStore.cs` ColdSlab + accessor), TerraFX-free POD. The array is rented from a pooled buffer at the reconcile edge; the value-gated ticker mutates it in place each frame (a mutation, **not** an allocation).
- `LayoutRun`/`Replay` gain an optional `ReadOnlySpan<ColorF> perGlyphColorOverride` that takes precedence over the span `colors[]` (a one-line `Replay` change mirroring the existing `colors` null-check, `:571`). Because the override is read at `Replay` (GPU-expansion time), it **never touches `RunKey`** → no reshape, no cache miss — the key fact.
- Glyph→syllable mapping is computed once per line in the render VM; each frame the ticker writes `PerGlyph[i] = lerpLinearPremul(unplayedAccent, playedAccent, syllableProgress(i, nowMs))` into the existing buffer (linear-light premultiplied, reusing the recorder's `BrushFade` `ColorF` lerp helper, `Columns.cs:472-479`).

**Acceptance:** VerticalSlice exercises a per-glyph color array under the alloc tripwire (the array is reconcile-edge/pooled; the per-frame write is a buffer mutation — assert 0 phase 6-13 bytes); `--screenshot` of a line where syllables carry distinct colors mid-ramp.

#### A3 — `AnimChannel.Color` (folded, premultiplied) — node-level color animation — **L** — *new channel*
**Why it's still needed (even with A2):** A2 gives per-*glyph* color. The line-level played/unplayed/secondary/tertiary color transitions BetterLyrics runs are *node-level* and today have only the 2-endpoint `BrushFade` crossfade (`AnimScheduler.cs:215-216` → recorder lerp). `BrushFade` is a one-shot 0→1 to a new resting color; it cannot do a continuous, clock-or-velocity-driven color trajectory, retarget mid-flight with velocity continuity, or be a spring. For true parity with BetterLyrics' color ValueTransitions, a real folded Color channel is the honest mechanism.

**The hard part (verified):** `AnimValue` is a single `float Position` (`AnimValue.cs:95`); `Accum` is all scalar floats (`AnimScheduler.cs:379-385`); `Compose` writes no color (`:157-195`); there is no `ColorF` slot in the row or accumulator. A naive "4 scalar lanes" is **wrong** — independent scalar lerps of premultiplied components ≠ correct premultiplied-linear interpolation across varying alpha. *(Verdict `text-glyph/no-color-lane-scalar-slab`, `line-anim-compositor/no-3d-no-color-closed-ops` — both SUPPORTED: the single-float substrate genuinely cannot reuse the scalar Accum directly. The comment at `AnimValue.cs:91` "+Color in Phase 4" confirms it is an unlanded future addition.)*

**Implementation — a Color row backed by a side-store, folded once in Compose** (do NOT widen the 64-byte `AnimValue` row — it would bloat every scalar track):
- **Channel:** append `AnimChannel.Color` (byte enum, append-only, `AnimTypes.cs:18`).
- **Endpoints side-store:** a slot-keyed `Dictionary<int,(ColorF from, ColorF to)> _colorBySlot` paralleling `_keysBySlot` (`Timeline.cs`). The slab row's single `float Position` becomes the **0→1 interpolation parameter** — so the analytical spring / eased / keyframe machinery drives color *progress* unchanged. A spring on color = a spring on the progress scalar; velocity-continuous retarget works (capture the current interpolated color as the new `from`, set `to`, reset progress; the spring carries velocity on the progress scalar).
- **Fold:** `AnimChannel.Color` is NOT a scalar `Accum` fold. In PASS2's per-row walk, when `ch == Color`, read `(from,to)` from `_colorBySlot`, compute `c = lerpLinearPremul(from, to, r.Position)`, write to a new `Accum.Col` (`ColorF`, the one non-scalar Accum field) + a `hasColor` flag + a 2-bit `ColorTarget` (Text/Fill/Border) from the row flags. In `Compose`, `if (acc.hasColor) p.TextColor = acc.Col` (or `p.Fill`/border by target). `PaintDirty`. **Compose stays alloc-free** — folds into a fixed `NodePaint` field; the `_colorBySlot` insert is a reconcile-edge seed.
- **CurrentValue:** extend the switch (`AnimScheduler.cs:357-376`) so a fresh Color spring starts from the live `NodePaint` color (progress 0, `from = current`).
- **Seed API:** `SeedColor(node, target, from, to, durationMs, ease)` → 0-alloc two-point path (mirror `SeedEased`, `:223-234`); `SpringColor(node, target, to, spring)` → retarget the progress spring; hook `UseColorAnimation(target, to, …)` deps-gated (mirror `UseSpring`, `RenderContext.cs:540`). Never per-frame.
- **lerpLinearPremul** reuses the exact helper the recorder's `BrushFade` uses (`Columns.cs:472-479`) so the math is identical and already validated.

This keeps the hot slab scalar (no row bloat), reuses 100% of the dynamics, adds one `ColorF` field to the 16-byte `Accum` + one `Compose` write. **src/ home:** all in `src/FluentGpu.Engine/Animation/` (TerraFX-free) + the `UseColorAnimation` hook in `Hooks/RenderContext.cs`.

**Acceptance:** VerticalSlice exercises a Color row under the alloc tripwire (0 phase 6-13 bytes) and asserts premultiplied-linear midpoint correctness (a 50% lerp opaque-red→transparent-blue matches the `BrushFade` midpoint — same helper); `--screenshot` of a continuous line-color ramp.

#### A4 — Per-glyph transform (per-LETTER float / scale-pop) — **L** (VM-bake) / **XL** (per-glyph springs) — *no GPU change; non-uniform `Replay` feed*

> **Superseded in part (2026-08-03): the SCALE half is gone.** Char pop removed — refuted by frame-verified Apple Music
> footage; `GlyphWipe.Lift` is now dy-only. The per-letter FLOAT this section specifies shipped as exactly that: the
> wipe's own per-glyph `Dy` (`GlyphRenderer.LayoutRunGradient` → `ReplayGradient`), no `Sx/Sy` and no `GlyphXform`
> side-table. The general per-glyph-transform design below stands as a *future* primitive, not as lyrics work.
**Status:** `GlyphInstance` carries a full per-instance 2×3 affine, applied per-instance in the VS (`:23,192-198`); `Replay` stamps one uniform `world` (`:577`). The capacity exists; the feed does not. *(Verdict `text-glyph/glyph-instance-per-color-xform`, `line-anim-compositor/glyph-per-instance` — both SUPPORTED.)*

**Implementation — per-glyph transform array fed to `Replay`** (the symmetric twin of A2 on the transform fields):
- **`Replay` change:** add `ReadOnlySpan<GlyphXform> perGlyph` where `GlyphXform { float Tx, Ty, Sx, Sy; }` (translate + scale about the glyph's own center; full 2×3 if per-letter rotation is ever needed). For glyph `i`, compose `world` with the glyph-local transform `L_i = Translate(cx,cy)·Scale(Sx,Sy)·Translate(-cx,-cy)·Translate(Tx,Ty)` where `(cx,cy)` is the glyph quad center (`s.DstX+DstW/2, s.DstY+DstH/2`), then `M_i = world·L_i`, stamp `M_i`. ~8 FLOPs/glyph at record time. When `perGlyph` is null, the existing uniform stamp is the fast path (unchanged).
- **Addressing (chosen, L):** a per-node **side-table of per-glyph scalars** computed in the value-gated `OnFrame` ticker — the render VM owns per-syllable timing; `OnFrame` writes `Tx/Ty/Sx/Sy` per glyph into a pooled buffer. The float/pop curves (Sine ease, down→up float, the keyframe pop) are deterministic functions of `(nowMs, charStart, charEnd)` — no per-glyph spring state needed for BetterLyrics parity. **0 phase 6-13 alloc** (pooled buffer mutated in place).
- **The XL path, only if per-glyph springs are required:** a sub-keyed AnimValue slab `(node, glyphIndex, channel)`. A 60–100-glyph line × 4 channels = up to 400 rows/line stresses the slab free-list and reconcile-edge seed budget. **Avoid unless per-letter spring physics is an explicit requirement** — BetterLyrics uses dt-driven eased keyframes, which the VM-bake reproduces exactly.
- **Color-consistency invariant** (BetterLyrics' key property — per-glyph transforms must NOT disturb the karaoke color): satisfied because A1's gradient is sampled in run-local space at the glyph's **rest** local-x. Pass `restLocalAxis` as a second varying (unaffected by the per-glyph transform); the crisp glyph draws at the transformed position, but the gradient *coordinate* uses rest position. One extra float varying.

**Why one shaped run, not one node per glyph:** the prior parity-plan warned per-glyph *nodes* lose shaping/kerning and are un-budgeted (verdict #3). This keeps **ONE shaped, correctly-kerned, cached run** (`RunKey` untouched) and applies per-glyph motion at GPU-expansion time. Kerning, ligatures, BiDi all survive — strictly better than node-per-glyph, and the only budget-safe way to do per-letter motion at line scale. **src/ home:** `Engine/Render/SceneRecorder.cs` + `Engine/Scene/Columns.cs` (`GlyphXform` side-table); `Windows/D3D12/GlyphRenderer.cs` `Replay` param.

**Acceptance:** VerticalSlice reconciles+lays out a 60–100-glyph run with per-glyph transforms within the reconcile-edge budget (the gate verdict #3 found missing), 0 phase 6-13 alloc; `--screenshot` of a line mid-float with staggered per-letter rise.

#### A5 — Glyph stroke / outline — **XL** (true offset mask) / **M** (faux multi-draw) — *new atlas + stroke flag*
**Status:** no glyph stroke primitive; `GetGlyphRunOutline`/`FillPath` are spec-only; `DrawShadow` is a rounded-box; the R8 atlas holds *fill* coverage only.

**Implementation (true offset mask, XL):**
- **Stroke mask bake — `src/FluentGpu.Windows/D3D12/GlyphRenderer.cs`.** On the miss path, in addition to fill coverage, rasterize a **stroke coverage** glyph: DirectWrite `IDWriteFontFace::GetGlyphRunOutline` → `ID2D1PathGeometry`, widen via a round-join `ID2D1StrokeStyle`, rasterize to an R8 coverage bitmap, pack into a **second region of the atlas** (or a parallel stroke atlas) keyed by `(GlyphKey, strokeWidthQ)`. Stroke width quantizes into a new `RunKey` field `strokeWidthQ` (a stroke-width change is a cache miss, like any shaping input). Cached and reused exactly like the fill mask. **This is the XL cost** — a geometry-realization + rasterization path in the baker and a second atlas budget (the generational-reset logic, `:118`, extends to it).
- **Op:** fold a `StrokeFlag>0` + `StrokeWidth` into `DrawGlyphRunGradientCmd` (already declared in A1) and emit the stroke pass as a separate sub-batch with the stroke atlas bound (fewer opcodes than a separate `DrawGlyphRunStroke`). The stroke is the same run shaped identically, just a different mask.
- **GPU:** the stroke pass binds the **stroke atlas** as `gAtlas` and runs the **same** `PSMainGradient` — `coverage_stroke × strokeGradient(localX)`. Drawn **before** the fill pass (stroke under fill, BetterLyrics order). Shares A1's `Split`; different `C0..C3` (stroke played/unplayed). So stroke = "the wipe PS, fed the stroke mask + stroke colors."

**The honest cut option (M):** stroke is **default-off** in BetterLyrics (`LyricsFontStrokeWidth=0`). If the geometry-realization atlas is too heavy for the timeline, a **faux stroke** (4–8 offset copies of the fill mask in the stroke color, under the fill) reaches ~80% fidelity at **M** with zero new atlas — visibly inferior for thick strokes/round joins, indistinguishable at 1–2px. **Recommend: ship faux-stroke (M) first behind the same flag; upgrade to true offset-mask (XL) only if a design calls for thick outlines.** Both are named, neither is hand-waved.

**Acceptance:** VerticalSlice asserts the stroke flag + stroke mask atlas entry headlessly; `--screenshot` of a stroked line vs reference; stroke-under-fill order verified by the emit sequence.

#### A6 — Glow / bloom (per-char, cropped to played portion) — **M** (composition) / **XL** (true additive) — *composes A1 + existing blur + clip*
**Target:** a Gaussian blur of the played sub-region of the char, drawn enlarged beneath the crisp glyph (additive-style bloom), cropped to `progressPlayed`; blur amount = animated `GlowTransition`; gated to syllables ≥700ms.

**Status:** FluentGpu has `LayerKind.Blur` (`DrawList.cs:47-52`): subtree → pooled offscreen RT → separable Gaussian → composite once, animated by `AnimChannel.BlurSigma`. The compositor RTs are full-canvas (`OpacityLayerCompositor.cs:17`), so naive per-char blur layers are unbudgeted (verdict #7).

**Implementation (chosen, M — composition bloom via a blurred duplicate run):** for the **active line only** (the only line that glows), emit the glyph run **twice**: once into a `PushLayer{Blur, sigma=GlowTransition}` (the blurred copy, drawn enlarged by scaling `world` ~1.1× about the line center, cropped to the played portion via `ClipR` driven by the **same split clock** as A1 — zero extra clock state), then the crisp run on top. Because only the active line glows, this is **one** full-canvas blur layer/frame — within budget (the R5 "one shared blur" discipline). The blurred copy is the *colored* glyph (it samples A1's gradient or A2's per-glyph color), so the bloom is correctly tinted, matching BetterLyrics' "blur of the color-composited region." Drawn under the crisp glyph → reads as additive-style bloom. **No new op** beyond A1 — it composes A1 + existing `BlurSigma` + `ClipR`.

**The true-additive path (XL, folds into A5's atlas):** a glyph-shaped glow baked like the stroke mask (a blurred coverage mask), drawn with additive blend (`SrcBlend=ONE, DstBlend=ONE`) in the glow color — a new PSO permutation + a blurred-coverage atlas entry. Only if the composition bloom's per-line full-canvas blur is a measured bottleneck, or if per-*char* independently-animated glow is required.

**Acceptance:** `--screenshot` of active-line glow growing with the wipe; VerticalSlice asserts ≤1 blur layer for the lyrics view (cost gate). Honest note: composition bloom is a blurred-copy-under-crisp — *visually* BetterLyrics' bloom; literal additive blend is the XL upgrade.

#### A7 — Multi-layer text (primary / translation / phonetic) — **S, no engine work** — *control composition*
A flex column per line, three children: Tertiary `TextEl` (phonetic, bottom), Primary (the karaoke layer — A1 gradient when active, flat when inactive), Secondary `TextEl` (translation, top). Per-layer opacity = each child's `AnimChannel.Opacity` (shipped channel), seeded on `_activeLine`/`isPlaying` change. **Only Primary, only while active, gets A1's gradient wipe**; Secondary/Tertiary are always flat (BetterLyrics never gives them the karaoke treatment, `LyricsLineRendererBase.cs:58,76`). The NaN guard (`opacity<=0 || IsNaN → skip`) ports verbatim. **src/ home:** control composition only — `Wavee/Features/Player/LyricsView.cs`.

### Cluster B — Procedural backdrops + spectrum (fullscreen only)

#### B1 — The engine primitive: `LayerKind.ProceduralBg` (NOT a new DrawOp) — **M (one-time)**
**Decision: ride `PushLayer`/`PopLayer`, add a 5th `LayerKind`.** A standalone `DrawProceduralBg` op would touch all six op-switch sites and need its own pooled-RT lifecycle. `LayerKind.EdgeFade=3` (the most recent kind) is the line-by-line template: it added one enum value, extra `PushLayerCmd` fields, one writer, one `SceneRecorder` branch, one compositor method, one headless assertion. `ProceduralBg=4` is the same shape — added through the `SubmitWithLayers` if/else dispatch (`D3D12Device.cs:943,972`) and the `StreamHasLayer` kind-agnostic `case DrawOp.PushLayer: return true` (`:1034`). *(Verdict `backdrop-spectrum/proc-bg-layerkind` — SUPPORTED, with the honest note: a procedural backdrop is a **generator** (input-less fill), not "render the subtree then post-process," so it needs a NEW render-side compose/shader entry on the compositor — that is additional `FluentGpu.Windows` render-side code, allocation-free, no invariant breach.)*

**The generator wrinkle, two sub-options:**
- **(1a) Generator-as-fill (chosen for fog/rain/snow/cover-tint):** `PushProceduralBgLayer(deviceRect, kind, …)` immediately followed by `PopLayer` with **no subtree** — the compositor runs the procedural PS directly into the current target over `deviceRect` (premultiplied SourceOver), no RT lease. Matches how all four BetterLyrics effects actually draw (over the canvas, no subtree). The Push/Pop pair keeps the op on the existing layer-walk + balance check.
- **(1b) Generator-into-leased-RT (chosen for Fluid):** lease a group RT, run the PS into it, composite at `GroupAlpha` — needed only for Fluid's per-effect `OpacityEffect<1`. Reuses the `OpacityLayerCompositor` `Acquire`/`Composite`/`Release` lease.

**The `ProceduralBgSpec` POD (uniform carrier) — `src/FluentGpu.Engine/Foundation/Effects.cs`** (next to `AcrylicSpec`/`EdgeFadeSpec`, TerraFX-free):
```csharp
public enum ProceduralBgKind : int { Fluid = 0, Fog = 1, Raindrop = 2, Snow = 3 }
public readonly record struct ProceduralBgSpec(
    ProceduralBgKind Kind, float Opacity,               // Fluid only (others = 1)
    ColorF Accent1, ColorF Accent2, ColorF Accent3, ColorF Accent4,  // Fluid only
    float P0, float P1, float P2, float P3, float P4,   // per-kind packed params (see each shader's table)
    bool BreathingEnabled, float BreathingIntensity);   // bass-driven scale — composited, not in-shader
```
A sparse `ColdSlab<ProceduralBgSpec>` column + `SetProceduralBg`/`TryGetProceduralBg` clones the `_acrylics` slab (`SceneStore.cs:113`). `PushLayerCmd` gains `int ProcKind, float P0..P4, float ProcOpacity` (the accents + time go in the per-frame constant buffer, not the hot POD — see B2). **src/ homes:** op/spec/recorder/headless in `FluentGpu.Engine`; the four shaders + compositor in `FluentGpu.Windows`; the app-facing DSL (`BoxEl.ProceduralBackdrop = new BackdropSpec{...}`) writes the POD spec from `FluentGpu.Controls` (which stays TerraFX-free — it only writes a struct). **The boundary holds exactly as the existing four layer kinds hold it.**

#### B2 — Uniform plumbing: `time`, resolution, accents (zero phase 6-13 alloc) — *one FrameInfo field*
**`time` — add it at the seam.** `FrameInfo` has no time field (verified `Rhi.cs:9`: `FrameInfo(Size2 SizePx, float Scale, ColorF Clear, RectF Damage)`). BetterLyrics' entire animation is `time += Δt` fed to every shader. Extend `FrameInfo` with `float TimeSec` (a default-able value field — the only engine-core change for time). At the two `new FrameInfo(...)` sites in `AppHost`, pass an accumulated `_timeSec += dtSec` (monotonic seconds since app start; precision drift after hours is the same honest non-issue BetterLyrics accepts). `SubmitDrawList(... in FrameInfo ctx ...)` already flows `ctx` into `SubmitWithLayers`; the compositor reads `ctx.TimeSec`. **No per-frame managed alloc** — it's a value field on the already-passed struct. *(Verdict `backdrop-spectrum/proc-bg-layerkind` — SUPPORTED: FrameInfo.time is the one genuine gap, accurately identified, contained additive change.)*

**Constant buffer (record-time bake, alloc-free).** The compositor holds a persistent upload CB (one ring slot per frame-in-flight, allocated at `Init`). Per backdrop, at record time on the render thread, it `MemoryMarshal.Write`s a POD into the mapped CB — **no managed alloc** (exactly like the existing compositors set root constants):
```hlsl
cbuffer ProcBg : register(b0) {
    float4 res_time;   // .xy = device-px resolution (ctx.SizePx, DPI-applied), .z = time sec, .w = kind
    float4 params;     // P0..P3
    float4 params2;    // P4, opacity, (breathingScale applied as a composite transform, not here), unused
    float4 accent1, accent2, accent3, accent4;   // Fluid only; (R,G,B)/255 straight
};
```
- **Resolution** = `ctx.SizePx` (already DPI-scaled) — the PS works in device pixels and auto-scales detail with DPI, like BetterLyrics.
- **Accents** come from the app: palette color extraction → the app already has `bridge.TrackPalette : Signal<Palette?>` (`PlaybackBridge.cs`). A Component re-renders the backdrop node with new literal accent colors when the palette signal changes (value-gated, ~once per track), crossfading over ~0.3s **at the reconcile edge** with the engine's `BrushTransitionMs` equivalent. The recorder copies them into the per-node accent slot; the compositor bakes them into the CB at record time. **Crossfade is a reconcile-edge concern (cheap, per-track), not a phase 6-13 concern.**
- **Breathing** = the node's own `AnimChannel.ScaleX/ScaleY` (seeded from the bass-energy signal at the reconcile edge), NOT a shader uniform — exactly BetterLyrics' CPU `Matrix3x2.CreateScale`-before-draw. Rides the existing transform pipeline, no shader change.

#### B3..B6 — The four shaders (hand-authored HLSL via `D3DCompile`) — **M each** (Raindrop M–L)
**The transpiler is NOT vendored.** *(Verdict `backdrop-spectrum/computesharp-not-vendored` — SUPPORTED: no ComputeSharp PackageReference; the two "ComputeSharp" mentions in `src/` are doc-comments only (`DWriteItemizer.cs:14`, `Interop/Placeholder.cs:4`); no `.hlsl`/`.dxil`/`.cso` files exist.)* Each shader is a `const string` HLSL block compiled at runtime via `ShaderCompiler.Compile(source, entry, target)`→`D3DCompile` at `vs_5_1`/`ps_5_1` — the convention `OpacityLayerCompositor.cs` uses for `Hlsl`/`BlurHlsl`/`EdgeFadeHlsl` (`:85,97,127`). The port is mechanical (the BetterLyrics shader spec is already near-HLSL). All four live in **`src/FluentGpu.Windows/D3D12/ProceduralBackdropCompositor.cs`** (clone `OpacityLayerCompositor.cs`) — **the only place HLSL + `D3DCompile` + TerraFX live for backdrops.**

| Shader | Algorithm summary | Uniforms | Per-pixel cost (honest) | Parity |
|---|---|---|---|---|
| **Fluid** (B3) | `uv→tuv`; domain-warp #1 (value-noise rotate); domain-warp #2 (traveling sine ripples); 4-color blend via `smoothstep`; optional LightWave (~9 transcendentals), optional Valve dither, optional HSV. `a=1` (1b applies `ProcOpacity` as group alpha). | res, time, accent1..4, P0=useHSV(0), P1=lightWave(1), P2=dither(1) | **lightest.** ~1 noise + ripple sin/cos + optional LightWave. Static-mode (frozen accents) amortizes to a blit. <0.3 ms at 4K (estimate). | **full** |
| **Fog** (B4) | 2 sub-samples/pixel; flip Y; view ray; two `Rot`s; 2-octave fBm `GenNoise`; `pow(n,0.717)`; accumulate premultiplied white; `/2`. | time, res | **moderate.** ~24 transcendentals + a `pow`/pixel, 1 pass. | **full** |
| **Raindrop** (B5) | OpenSimplex2-with-derivatives (`Os2…Part` ×2); cheap RNG; `RaindropSurface` (SDF dome → height+normal); static + rolling beads; lighting (specular `pow(...,24)`, edge shadow, grayscale lit-glass + masked alpha). Emits grayscale+alpha over the backdrop. | time, res, P0=speed(1.0), P1=size(1.0), P2=density(0.40), P3=lightAngle(135°), P4=shadowIntensity(0) | **THE expensive one** — dozens of transcendentals/pixel, 1 pass. First to stutter on weak iGPU / 4K-high-Hz. **Honest knob:** render the leased RT at a fixed 1080p cap and upscale (1-line RT-size change); ship without it first. | **full** (+ named perf knob) |
| **Snow** (B6) | Fixed **6×11=66-iteration** nested loop of jittered grid cells; per-cell fall/sway/random flake; additive accumulation gated by `density`. | time, res, P0=density(0.10), P1=speed(1.0) | **constant ~200 transcendentals/pixel** (the gate changes accumulation, not loop count). Second-heaviest. Same optional 1080p-RT upscale. | **full** (+ named perf knob) |

**Composite position** maps the BetterLyrics `DrawCore` order to sibling Z-order in the fullscreen surface (§3 Cluster D below): Fluid just above the cover; fog/rain/snow on top of the stack, below lyrics.

#### B7 — Blurred rotating album cover — **M, no procedural shader** — *shipped primitives + cached pre-blur*
All existing primitives:
- **Blur σ≈100:** the live blur paths clamp (acrylic ~60px; dynamic `LayerKind.Blur` caps support at ±3σ/32 taps → effective σ≈10 for σ=100, `OpacityLayerCompositor.cs:108`). **Verdict: pre-blur the cover ONCE into a cached RT on track change** (BetterLyrics itself caches the blurred+scaled cover in `EnsureCachedLayer`) — one expensive blur per track change, then a cheap rotated blit per frame. This is the right answer and **not a real cut**, just an implementation note. The retained-cache infra already exists (`AcrylicCompositor` retains a blurred-backdrop RT keyed by `PushLayerCmd.LayerId`, `D3D12Device.cs:398`) — reuse it verbatim.
- **Oversize to screen diagonal:** bake the cover's `DrawImage` `Rect` to `sqrt(W²+H²)` centered so rotation never shows corners. Record-time geometry.
- **Rotation** (`baseSpeed=0.6`): `AnimChannel.Rotation` driven by a looping keyframe (seeded once; the slab samples it dt-deterministically). **Full parity** — continuous rotation is native.
- **Crossfade on track change (0.7s):** the existing `DrawImageCmd.CrossFade` field (`DrawList.cs:96`) + two `ImageEl`s, the top one's opacity driven by a 0.7s transition seeded at track change. **Full/close.**
- **Breathing + parallax:** breathing = `ScaleX/Y` from bass energy (B8); **parallax (3D tilt) = omitted** (no `Transform3D`; default-off in BetterLyrics — see §6).

#### B8 — Audio FFT source + spectrum geometry — **M (FFT + Bar mode)** / **+L (Curve/Ring tessellator)** — *new audio seam; blocked on real audio host*
**No audio FFT/spectrum/bass-energy source exists anywhere** — `WaveeEqualizer`/`EqBar` are hardcoded `Patterns[][]` with zero audio input (`Equalizer.cs:32-37`). *(Verdict `backdrop-spectrum/no-audio-fft-seam` — SUPPORTED: a grep across `src/`+`app/` finds no FFT/WASAPI/spectrum; the only hits are ColorPicker's saturation box and OAuth loopback URIs.)*

**The clean home: tap PCM at the real `IAudioHost`, FFT there, surface a coalesced spectrum signal.** The seam exists and names where PCM will be available: `IAudioHost` (`AudioHost.cs:31-43`) with the deferred decode→mixer→WASAPI behind it; `SilentAudioHost` is the default; `AudioHostSignal`/`AudioHostSignalKind` (`:23-27`) are the boxing-free POD report pattern a spectrum channel follows. Wavee **is** the player, so the better source is the player's own PCM (a mixer-output tap, post-volume), not WASAPI loopback. Run a 2048-point Hamming FFT on the audio thread — the BetterLyrics `SpectrumAnalyzer` recipe (perceptual curve, peak-tracking auto-gain, symmetric stereo layout, asymmetric smoothing, bass = lowest-5-bins). NAudio must **NOT** be added (AOT/no-extra-NuGet discipline); a fallback WASAPI loopback (B8b) is hand-authored Win32 interop in `FluentGpu.WindowsApi` (which already holds OS-services pillars + `Media/` SMTC and is NOT in the TerraFX-free closure) — `IAudioClient`/`IAudioCaptureClient` are present in the shipped TerraFX.Interop.Windows.dll.

**The signal surface (alloc-clean), `src/apps/Wavee/Backend/` or `FluentGpu.WindowsApi/Media/`:**
```csharp
public interface IAudioSpectrum {
    int CopyBars(Span<float> dest);   // caller's reusable buffer; host copies smoothed bars — no per-frame alloc
    float BassEnergy { get; }         // [0,1], drives breathing on every backdrop + lyrics
    bool IsCapturing { get; }
}
```
The FFT runs on the audio thread into a lock-guarded double-buffer; the UI reads it at the reconcile edge (a low-frequency ticker reads `BassEnergy` → seeds the breathing `ScaleX/Y` target; the spectrum geometry reads `CopyBars(buffer)` into a reused array, baked at record time). **No phase 6-13 alloc.** *(Honest scope note from the verdict: `IAudioSpectrum`/`CopyBars`/`BassEnergy` do not yet exist — they are genuinely to-be-built, but buildable because `IAudioHost` is an open interface and the POD report pattern is established.)*

**Spectrum geometry (FluentGpu):**
- **Bar mode (FULL parity, ZERO new engine work):** N animated `FillRoundRect` (or `DrawGradientRect` for the transparent→color gradient — the op exists, ≤4 stops). Seeded at the reconcile edge from `CopyBars`, baked at record time. Recommended default.
- **Curve / Ring modes:** the engine has **no general path tessellator** — `DrawPolylineStroke` is ≤4 points (`DrawList.cs:131`, `PointCount` ≤4), `DrawTabShape` is a bespoke SDF. **Approximate with chained short polylines** (each ≤4 pts, compute point+normal CPU-side) — visually close, more instances. Pixel-perfect curve/ring needs a `DrawPath` op + a stroke/fill tessellator (**+L**, genuinely new — see §6).
- **Glow:** `LayerKind.Blur` (separable Gaussian) over the spectrum region approximates the blur-16 additive glow. **Close** — the engine composites premultiplied SourceOver, not pure additive `Add` (a tiny additive-blend PSO closes it if the glow looks weak; visually marginal).

**Acceptance:** Bar mode `--screenshot` + zero-alloc gate with a spectrum mounted; the FFT source is gated behind the real audio host (named blocker).

### Cluster C — Per-line animation, scroll, blur, lanes (mostly free)

The master rule (BetterLyrics per-frame `UpdateLines` → FluentGpu seed-on-deps): FluentGpu forbids per-frame `Animate`/`Keyframes`/`Drive` seeding (each allocs a `Keyframe[]` / grows a dict — trips the gate). Map BetterLyrics' three shapes:
1. **"Set target each frame on an edge-gated value"** (scroll/scale/blur/fan/opacity/color) → **deps-keyed seed** (`UseSpring(channel, target, deps:[activeLine, paletteEpoch, layoutEpoch])`). A spring retargets velocity-continuously (`Spring()` rebases x0/v0 from the live value, `AnimScheduler.cs:277-283`), so deps-seeded == per-frame-seeded visually. Seed at the reconcile edge (excluded from the gate); **0** in phases 6-13. (BetterLyrics' own 5-flag gate, `LyricsAnimator.cs:110`, already fires these only on change.)
2. **"Continuous clock-driven progress"** (karaoke split, marquee) → the **`Drive` path** (value-gated, time-independent across seek/pause). Seed once per line; **0** in phases 6-13.
3. **"One-shot pulse on a rising/falling edge"** (per-char float, ~~syllable pop~~ *(removed 2026-08-03 — refuted)*, glow) → **edge-detected seed in the value-gated `OnFrame`** (detect the `IsPlayingLastFrame` edge, seed `Keyframes`/`Spring` once on the few syllables crossing onset this tick — at the effect boundary, never the steady hot path).

*(Verdict `line-anim-compositor/edgefade-blur-drive` — SUPPORTED: `Drive` is value-gated by `Clocks.Sample` with no dt term; `SeedEased`/`Get`-retarget reuse the existing slot 0-alloc. Honest nuance: a one-shot `Drive` seed with a fresh `Keyframe[]` allocates that array at the *call site* (seed time), not in the advance loop — `Drive` once with a stable/reused keyframe array.)*

| Effect | Mechanism | Engine work | Effort | Parity |
|---|---|---|---|---|
| Per-line scale 0.75↔1.0 | `ScaleX`+`ScaleY`, origin = line center, seed on `activeLine` | none (shipped) | S | **full** |
| Per-line opacity ×4 | `Opacity` per stacked child (A7) | none | S | **full** |
| **2D fan** angle | `AnimChannel.Rotation` about center, `angle=fanRad·distF·(below?±1)` | none | S | **full** |
| Scroll-follow | `UseSpring(TranslateY, −line.centerY + viewportH·0.5, deps:[activeLine])` on the **content column's own node** (NOT `ScrollEl` — Input-owned, no component eased scroll). Velocity-continuous on retarget; `UseTransition(…, Quad/Out, 500ms)` for exact feel; snap on `layoutEpoch`. | none | M | **full** |
| Per-line blur (DoF) — **default A** | ONE shared blur wrapping the non-focal region; active line sharp on top. 2 passes total. | none | S | close (one σ, reads as DoF) |
| Per-line blur — **full-parity C (E6)** | per-rect scissored, downsampled self-blur: lease a layer-`DeviceRect`-sized RT, scissor the 2 Gaussian passes to the thin line rect, downsample ¼ (the acrylic path's trick). A 24px band at ¼ res ≈ 1/40th a full-canvas pass. Op + recorder unchanged; only the D3D12 leaf's RT sizing + scissor change. The code names this "a perf follow-up" (`SceneRecorder.cs:310-311`). | yes (Windows: `OpacityLayerCompositor.cs`) | M | **full** (per-line σ at fullscreen) |
| LANE packing (duets) | render-VM greedy interval-packing (`CalculateLanes`, 50ms, lane-0-wins) + L/C/R alignment from `AgentId` + lowest-lane resolver. Lane-N lines = offset/aligned siblings; pivot = `TransformOrigin` per alignment. | none (render-VM) | S | **full** |

*Per-line blur cost (verified, `SceneRecorder.cs:317,336`):* each `BlurSigma>0` node becomes a **full-canvas** separable 2-pass Gaussian into a pooled RT. BetterLyrics blurs every off-focal line (~20 × 2 = ~40 passes/frame) — the budget risk. **Ship A (shared blur) first; land C (E6) for true per-line σ at fullscreen, then flip the per-line seed on.**

### Cluster D — Edge-fade vignette + the compositor stack (the fullscreen surface)

#### D1 — Edge-fade vignette — **S, no engine work, FULL parity**
`LayerKind.EdgeFade` is first-class (`DrawList.cs:54-60`) → `PushEdgeFadeLayer` (`:302`), emitted by `SceneRecorder` via `TryResolveEdgeFade` (`:315-333`); the feather follows rounded corners, supports per-edge bands + falloff + optional pre-blur — exactly BetterLyrics' construction. *(Verdict `line-anim-compositor/edgefade-blur-drive` — SUPPORTED.)* Whole-scene feathering = wrap the lyrics viewport in an `EdgeFadeSpec` with all 4 bands; lyrics-only scroll-axis fade = `EdgeFadeSpec` on the content node, fading top/bottom (horizontal) or left/right (vertical CJK). Maps 1:1.

#### D2 — The compositor Z-stack (the `DrawCore` order, mostly free)
FluentGpu composites in painter order (the recorder walks front-to-back; `SubmitWithLayers` replays in stream order). The fullscreen surface builds a column of sibling backdrop nodes in BetterLyrics' z-order:
```
BoxEl{Fill = palette × 0.78}                       (pure-color overlay)           — full, free
ImageEl (oversized, rotating, crossfading)         (cover, behind cached pre-blur)— full (B7)
PushProceduralBgLayer{Fluid}                         (fluid)                        — B1/B3
spectrum rect/polyline instances                    (spectrum)                     — B8
PushProceduralBgLayer{Snow} / {Fog} / {Raindrop}     (snow/fog/raindrop)            — B1/B4-B6
the lyrics line column (Clusters A + C)             (lyrics)                       — the payload
─ all wrapped in one PushEdgeFadeLayer              (whole-scene edge feather)     — full, free (D1)
```
The compositor **order itself is free** — sibling Z-order + a few layer groups (Acrylic for the cover blur, EdgeFade wrapping the stack). Default blend is premultiplied SourceOver everywhere (the engine's canon `BGRA8_UNORM` premultiplied contract — matches BetterLyrics' default `ds.DrawImage`); the one exception is the spectrum's additive glow (B8, a small honest difference).

### Cluster E — The two genuinely hard items

#### E-3D — 3D perspective fan — **L–XL** — *new `LayerKind.Transform3D`*
**The hard boundary (verified):** there is **no perspective transform anywhere in `src/`** — a grep for `Transform3D|Matrix4x4|M34|Perspective` returns zero matches; the only transform primitive is the 2D 6-param `Affine2D` (`Geometry.cs:163`). *(Verdict `line-anim-compositor/no-3d-no-color-closed-ops` — SUPPORTED, stronger than claimed: not even `Geometry.cs` has a 4×4.)* The 2D fan (Cluster C, `Rotation`) is **full parity, free**; the **3D perspective parallax** (`Is3DLyricsEnabled`, `M34=1/depth`, `Lyrics3DDepth=800`) is the hard one.

**Implementation — `PushLayer{Transform3D}` (a new LayerKind), the clean general fix:**
- **Engine (TerraFX-free):** add `LayerKind.Transform3D` + the 16-float `Matrix4x4` (or 9 params: 3 angles + depth + center) onto `PushLayerCmd` in `DrawList.cs`; add a `PushTransform3DLayer(...)` writer mirroring `PushEdgeFadeLayer`; emit from `SceneRecorder` from a new `Transform3DSpec` sparse column.
- **Windows leaf:** in `OpacityLayerCompositor.cs` (already runs Opacity/Blur/EdgeFade groups into pooled RTs), add a 4th path: render the subtree to a group RT (as today), then **composite that RT through a perspective-projected quad** — the composite VS multiplies the quad's 4 corners by the `Matrix4x4` (one extra PSO = the existing composite shader + a 4×4 uniform). Exactly Win2D's `Transform3DEffect`-on-a-CommandList model. Wire one `if (L.Kind == Transform3D)` branch in `SubmitWithLayers`.
- **Cost honesty:** the fan applies ONE shared 3D matrix to the **whole lyric column** (BetterLyrics applies it canvas-wide, not per-line), so it's **one** Transform3D layer/frame, not N. That bounds the cost.
- **Angles:** drive the matrix uniform directly from low-frequency signals at record time (recompute the matrix in the recorder from 3 `FloatSignal`s — sensor/manual, not keyframed pulses); no new AnimChannel needed.
- **Reject** the per-glyph affine fake (affine can't do true perspective foreshortening / vanishing point; loses correctness on wide lines).

**Effort: L–XL.** Default-off in BetterLyrics → the last cluster to implement, but fully specifiable and **NOT blocked-forever** — bounded new-primitive work in the well-defined layer-compositor seam.

#### E-CJK — Vertical CJK orientation — **M (Tier 1)** / **L (Tier 2)** — *per-glyph stacking (rides A4) or DirectWrite vertical*
**The hard boundary (verified):** FluentGpu has **no vertical text layout** — the "vertical" hits in `TextLayoutEngine.cs`/`DirectWriteFontSystem.cs` are vertical *metrics* (ascent/descent); CJK appears only as font-fallback coverage in horizontal runs (`TextLayoutEngine.cs:75,344`). No `DWRITE_READING_DIRECTION` vertical / writing-mode / glyph-stacking path; the layout itemizes→shapes→wraps horizontally LTR/RTL+BiDi only.

**Tier 1 (M, recommended first) — per-glyph vertical stacking, NO new op.** Rides the verified fact that `GlyphInstance` is per-instance-transform-capable (A4): keep horizontal DirectWrite shaping (correct shaping/rasterization), then at record time bake a per-glyph transform that **stacks glyphs vertically** (advance down, not right) and optionally rotates Latin runs 90° (tate-chu-yoko). This is the **A4 per-glyph transform** primitive, fed a vertical-stacking layout from the render VM. **src/ home:** `GlyphRenderer.cs` `Replay` (the A4 param) + `SceneRecorder.cs`; the DrawList op is unchanged (the per-glyph data rides the A4 side-table). **Honest limit:** this stacks shaped horizontal clusters vertically — it handles the common ideograph case (each ideograph is square, advances down) but NOT full DirectWrite vertical metrics / `vert`/`vrt2` OpenType punctuation substitution. For Wavee lyrics (overwhelmingly ideographs + occasional Latin) it reads correctly. A1's gradient already supports `Axis=1` (vertical wipe) for when this lands.

**Tier 2 (L, full fidelity) — proper DirectWrite vertical reading direction.** `SetReadingDirection(TOP_TO_BOTTOM)` + `SetFlowDirection(RIGHT_TO_LEFT)` in `TextLayoutEngine.Layout` gets true vertical metrics + OpenType `vert` substitution. **Cost:** the layout engine assumes a horizontal `Asc/Desc`/`lineHeight` model throughout (`:274-278`); adding a vertical axis means a parallel measure path (line bands → columns, advance → vertical, `GetRangeRects` → vertical rects) — a substantial layout-engine fork. TerraFX-free boundary intact (all in `FluentGpu.Windows`; the `IFontSystem` seam gains a portable `WritingMode` enum).

**Effort: M (Tier 1) / L (Tier 2).** Tier 1 covers the in-scope CJK lyric case and rides the A4 primitive the per-letter motion already wants. A real text-engine fork, but bounded.

### Consolidated engine-work table

| # | Item | What it unlocks | Engine work | Effort | Verified status (verdict) |
|---|---|---|---|---|---|
| **A1** | `DrawGlyphRunGradient` op + glyph-gradient PS | The soft sweeping karaoke wipe (the signature) | Engine (op/payload/recorder/`WipeSplit` side-table channel) + Windows (PSO + per-draw stops) | **L** | SUPPORTED — gradient PS × R8 coverage; new op genuinely required |
| **A2** | per-glyph color override in `Replay` | Continuous per-syllable color | Engine (recorder + `GlyphColorRamp` side-table); **no GPU change** | **M** | SUPPORTED — per-glyph color seam already proven |
| **A3** | `AnimChannel.Color` (side-store + Compose fold) | Node-level animated/spring color | Engine only (channel + `_colorBySlot` + `Accum.Col` + Compose + `SeedColor`/hook) | **L** | SUPPORTED — single-float substrate; side-store progress pattern required |
| **A4** | per-glyph transform in `Replay` | Per-letter float / ~~scale-pop~~; vertical-CJK Tier 1 substrate | Engine (recorder + `GlyphXform` side-table); **no GPU change** | **L** / XL (springs) | SUPPORTED — per-instance affine already applied in VS. **Pop superseded 2026-08-03** (refuted); the float shipped as `GlyphWipe.Lift`, dy-only |
| **A5** | offset-stroke R8 mask atlas + stroke flag | Glyph stroke/outline | Windows (atlas bake + stroke PSO) + Engine (flag on A1's cmd) | **XL** / **M** (faux) | gap confirmed; default-off in BL |
| **A6** | blurred-duplicate run (composes A1+blur+clip) | Per-char glow / bloom | none beyond A1 (Engine: twice-emit active line) / XL (additive) | **M** / XL | gap confirmed; composition bloom = visually BL's |
| **A7** | control composition | Multi-layer primary/translation/phonetic | none | **S** | — |
| **B1** | `LayerKind.ProceduralBg=4` + `ProceduralBgSpec` | The procedural-backdrop primitive (shared) | Engine (enum + POD + sparse column + recorder branch) + Windows (compositor clone + dispatch) | **M** (one-time) | SUPPORTED — EdgeFade=3 precedent; generator needs a new compose entry |
| **B2** | `FrameInfo.TimeSec` + record-time CB bake | time/resolution/accents to the shaders | Engine (one FrameInfo field) + Windows (persistent CB) | (in B1) | SUPPORTED — FrameInfo.time is the one gap |
| **B3** | Fluid HLSL | Fluid backdrop | Windows (hand-HLSL) | **M** | SUPPORTED — hand-HLSL via D3DCompile; transpiler NOT vendored |
| **B4** | Fog HLSL | Fog backdrop | Windows | **M** | SUPPORTED |
| **B5** | Raindrop HLSL | Raindrop backdrop | Windows | **M–L** | SUPPORTED (+ named 1080p-RT perf knob) |
| **B6** | Snow HLSL | Snow backdrop | Windows | **M** | SUPPORTED (+ named perf knob) |
| **B7** | cached pre-blur RT + oversize/rotate/crossfade | Blurred rotating album cover | none (shipped primitives + retained-cache reuse) | **M** | σ=100 via cached RT (not a real cut); 3D parallax omitted |
| **B8** | `IAudioSpectrum` seam + FFT + bar geometry | Spectrum + bass-energy (drives ALL breathing) | app/`WindowsApi` audio seam (FFT); Engine geometry = shipped for Bars | **M** / +L (curve/ring tessellator) | SUPPORTED — no FFT exists; blocked on real audio host |
| **E6** | per-rect scissored downsampled blur | True per-line σ DoF at fullscreen | Windows (`OpacityLayerCompositor` RT sizing + scissor) | **M** | SUPPORTED — code names it a perf follow-up |
| **E-3D** | `LayerKind.Transform3D=…` + perspective-quad composite | 3D perspective fan | Engine (`PushLayerCmd` matrix + writer + `Transform3DSpec`) + Windows (perspective-quad PSO) | **L–XL** | SUPPORTED — no 4×4 anywhere; genuinely hard, bounded |
| **E-CJK** | per-glyph vertical stacking (T1, rides A4) / DWrite vertical (T2) | Vertical CJK | Windows (A4 feed / layout fork) | **M** / **L** | SUPPORTED — horizontal-only layout; genuinely hard, bounded |
| **+L** | `DrawPath` op + path tessellator | Pixel-perfect spectrum curve/ring | Engine + Windows | **L** | gap confirmed; chained polylines reach *close* |

**Every new op/payload/channel/recorder/side-table is in `FluentGpu.Engine` (the `VerticalSlice` closure stays TerraFX-free and headless-asserts the ops); every PS/atlas/PSO is in `FluentGpu.Windows`. The render thread continues to own every `ComPtr` — no threading-seam change.**

---

## 4. The data dependency (not redone here)

The entire feature is blocked on a **real synced lyrics feed** — the only `ILyricsProvider` today is the fake returning 4 hardcoded lines (`FakeData.cs:523-538`), passed even on the real path (`Services.cs:170`). This is the same true data-availability limit as the audio FFT (B8). The aggregator/reranker — multi-provider fan-out (AMLL TTML, Spotify-native, LRCLIB by default; QQ/Kugou/NetEase/Musixmatch/Apple opt-in), normalization, the content/timing reranker that picks the *best* lyric (not the first), and `SwitchableLyrics` wiring — is **fully owned by `docs/lyrics-aggregator-reranker-plan.md`** and is **not** redone here. The model enrichment that the maximalist tier needs (`EndMs`, `SecondaryText`/`Romanization` for the multi-layer A7, `AgentId`/`LaneIndex` for lane packing in Cluster C, `IsWordByWord`) is additive and specified in that doc's §4. Until the live provider lands, both surfaces build against the fake's lines and degrade gracefully (lane-0, primary-only). The lyrics **view** does not care whether the backend is fake or live — it always reads `svc.Lyrics`.

---

## 5. Front-loaded roadmap (the fancy effects ship EARLY)

Each phase is independently shippable and gated by: `dotnet build src/FluentGpu.slnx` clean + `dotnet run --project src/FluentGpu.VerticalSlice` → **ALL CHECKS PASSED** (zero-alloc phases 6-13 green — the lyrics view must add **no** per-frame allocation) + `--screenshot` for fidelity (feed a fixed `FrameInfo.TimeSec` for deterministic backdrop diffs; the fullscreen surface auto-opens via the existing `WAVEE_NOWPLAYING_OPEN` flag, `WaveeApp.cs:128`). The `VerticalSlice` transitive closure stays TerraFX-free (the shaders live only in `FluentGpu.Windows`).

**The signature effects appear in Phases 1–2, not deferred to a "someday" phase.** The line-level view is the foundation, but the soft-wipe karaoke (A1) and the first procedural backdrop (B1+B3) land *early*, because the engine work is bounded and the verdicts confirm it's buildable now.

### Phase 0 — Foundations (backend + both shell scaffolds; no UI risk)
- **Data:** the aggregator-doc Phase 0 — model enrichment + `SwitchableLyrics` + ISRC/`has_lyrics` projection.
- **Shell:** the rail (`ShellUi`, third child of `WaveeShell.cs:195-294`, open/close) per docs/wavee-lyrics-canonical-design.md §2; confirm the fullscreen `NowPlayingLayer` hook (`bridge.Expanded`, already mounted at `WaveeShell.cs:317`).
- **Clock:** factor `UseSmoothPositionMs(bridge)` from `SeekBar.cs:56-101` (one anchor shared by rail, fullscreen, and the seek bar).
- **VM:** the shared `LyricsVm`/`LineVm`/`SyllableVm`/`CharVm` graph (aggregator-doc §6.1) + the current-line resolver (next-StartMs boundary) + lane packing (Cluster C).
- **Acceptance:** build clean; rail opens/pushes; fullscreen toggles; fake lyrics resolve in both; no per-frame alloc.

### Phase 1 — Line-level synced view, BOTH surfaces (zero engine work)
- `Wavee/Features/Player/LyricsView.cs` + `LyricsTicker` (gated mount); auto-follow `UseSpring(TranslateY)` on the owned column; neighbour `ScaleX/Y` + `Opacity` + 2D fan `Rotation`; color crossfade via `BrushTransitionMs=300f` literals; `EdgeFade` vignette (D1); click-to-seek; marquee; multi-layer primary/translation/phonetic (A7); loading-shimmer / no-lyrics / loaded states.
- Fullscreen surface adds the **pure-color overlay + blurred rotating crossfading cover (B7)** — all shipped primitives (cached pre-blur on track change).
- **Acceptance:** active line tracks playback and scrolls smoothly on both surfaces; neighbours fade/scale/fan; cover rotates + crossfades; GPU idles when closed/paused; VerticalSlice green.

### Phase 2 — The karaoke SIGNATURE + per-character motion (the fancy effects, early)
- **A1 — soft sweeping gradient karaoke wipe:** the `DrawGlyphRunGradient` op + `PSMainGradient` PSO + `WipeSplit` side-table channel, driven by the `Drive` media clock (register once, reuse `drivenRef`). On both surfaces.
- **A2 — continuous per-syllable color:** per-glyph color override fed to `Replay` (no GPU change).
- **A4 — per-character float / ~~scale-pop~~:** per-glyph transform fed to `Replay` (VM-bake, L path). *(Char pop removed 2026-08-03 — refuted by frame-verified Apple Music footage; the float shipped as `GlyphWipe.Lift`'s per-glyph `Dy`, dy-only.)*
- **A6 — glow/bloom:** the blurred-duplicate active-line run (composes A1 + `BlurSigma` + `ClipR`).
- **Acceptance:** the wipe sweeps softly in sync across seek/pause; syllables ramp color; letters float on onset *(rise into the baseline — no pop)*; the active line glows growing with the wipe; new VerticalSlice gates assert the `DrawGlyphRunGradient` op + stops + per-glyph arrays headlessly under the alloc tripwire (0 phase 6-13 bytes); `--screenshot` soft-edge + per-glyph diffs.

### Phase 3 — Procedural backdrops + spectrum (fullscreen maximalist)
- **B1+B2 — the `LayerKind.ProceduralBg` primitive + `FrameInfo.TimeSec` + record-time CB bake.**
- **B3 — Fluid** (the default-on backdrop, accents from `TrackPalette`), then **B4 Fog / B6 Snow / B5 Raindrop** (each a `const string` HLSL + a recipe enum value — S/each after B1).
- **B8 — the spectrum:** Bar mode (zero new engine work) once the FFT source lands; bass-energy → breathing `ScaleX/Y` on every backdrop + the lyrics. (FFT is gated on the real audio host — ship Bar geometry + breathing wiring against a stub; light up when PCM is available.)
- **A3 — `AnimChannel.Color`** (node-level color trajectories beyond the 2-endpoint `BrushFade`) where continuous line-color motion is wanted.
- **Acceptance:** each backdrop renders deterministically at a fixed `TimeSec` (`--screenshot` per kind); the zero-alloc gate stays green with a backdrop + spectrum mounted (the CB write is a `MemoryMarshal.Write` into a pre-mapped buffer; bars/accents are reconcile-edge seeds); headless asserts the `ProceduralBg` opcode + `ProcKind` + baked uniforms + Push/Pop balance.

### Phase 4 — Full-fidelity polish + the genuinely hard items
- **A5 — glyph stroke** (faux M first; true offset-mask XL if a design calls for thick outlines).
- **E6 — per-rect downsampled blur** → flip the per-line continuous σ DoF on at fullscreen.
- **B8 +L — path tessellator** for pixel-perfect spectrum curve/ring (chained polylines ship *close* in Phase 3).
- **E-3D — the 3D perspective fan** (`LayerKind.Transform3D`, one shared matrix for the column). Default-off in BL → last.
- **E-CJK — vertical CJK** (Tier 1 per-glyph stacking via A4; Tier 2 DWrite-vertical only if a CJK-market requirement demands true vertical typography).
- **Acceptance:** each item behind its own VerticalSlice + `--screenshot` gate; none regresses the zero-alloc gate.

**Why this order delivers the fancy effects early:** Phase 2 ships the *signature* (soft wipe + per-char + color + glow) — the thing that makes it look like BetterLyrics — on top of a Phase-1 view that's already a faithful synced view. Phase 3 ships a procedural backdrop and the spectrum. The two genuinely hard, default-off items (3D fan, vertical CJK) are last, but specified and bounded, not hand-waved into a perpetual "someday."

---

## 6. Honest scorecard — what is genuinely hard / physical limits (driven by the verdicts)

The corpus discipline: name the real limits, do not soften or inflate. Every refuted/partial verdict shows up here with its correction. The full effect set reaches **full parity** except where a true engine/physical limit is named.

### 6.1 Two prior-doc claims were FALSE and are corrected here (binding)
1. **"The ComputeSharp C#→HLSL transpiler is already vendored"** (asserted in betterlyrics-parity-plan.md E7 / §8-Phase4). **FALSE.** *(Verdict `backdrop-spectrum/computesharp-not-vendored` — SUPPORTED.)* It is not a PackageReference and not vendored; the two `src/` mentions are doc-comments only; no `.hlsl`/`.dxil`/`.cso` files exist. **Correction:** the four backdrop shaders are **hand-authored HLSL** compiled at runtime via `ShaderCompiler.Compile`→`D3DCompile` (`ShaderCompiler.cs:12`), the convention every existing pipeline uses (`OpacityLayerCompositor.cs:85,97,127`). This is **not a blocker** — it's mechanical porting (the BetterLyrics shaders are already near-HLSL) and the correct, invariant-preserving choice (vendoring a transpiler would risk new deps).
2. **"Panel mode = the tame configuration; no procedural backdrops / 3D / spectrum"** (wavee-lyrics-canonical-design.md D3; betterlyrics-parity-plan.md E7 "out of scope"). **Superseded by the new bar.** These move from "out of scope / default-off" to "in scope, implemented per Cluster B / E," now placed on the **fullscreen** surface (the rail keeps the tame line+karaoke+per-char config — the surface split, §1.1, is what makes both true at once).

### 6.2 The two genuinely HARD items (named, not hand-waved — bounded engine work, not blocked-forever)
1. **3D perspective fan / parallax (E-3D).** `Affine2D` is 6-param 2D; there is **no** `Matrix4x4`/`M34`/perspective anywhere in `src/` (`Geometry.cs:163`; grep returns zero). *(Verdict `line-anim-compositor/no-3d-no-color-closed-ops` — SUPPORTED.)* Requires a new `LayerKind.Transform3D` compositing the subtree's group RT through a perspective-projected quad in `OpacityLayerCompositor` — **L–XL**. Bounded (one shared matrix for the column, not per-line; fits the existing layer-compositor seam), default-off in BetterLyrics. The 2D fan is **full parity, free**.
2. **Vertical CJK orientation (E-CJK).** The DirectWrite layout engine is horizontal-only (the "vertical" code is vertical *metrics*; CJK is font-fallback in horizontal runs — `TextLayoutEngine.cs:75,344`). Tier 1 (per-glyph vertical stacking via the A4 primitive) is **M** and covers the ideograph-heavy lyric case; Tier 2 (true DirectWrite vertical reading-direction) is **L** and is a real layout-engine fork. Bounded, but genuinely hard.

### 6.3 Items that are HARD but the GPU is already capable (the work is wiring, not GPU)
- **Soft karaoke wipe (A1):** the gradient PS × R8 coverage; *(verdict SUPPORTED)*. Needs a new op + a fused PSO — L, not a paradigm shift.
- **Continuous per-syllable color (A2) + per-letter transform (A4):** `GlyphInstance` already carries per-instance RGBA + 2×3 affine, applied in the VS; *(two verdicts SUPPORTED)*. The work is non-uniform `Replay` feeding (M / L) — no GPU-struct or shader change.
- **`AnimChannel.Color` (A3):** the slab is single-float; *(verdict SUPPORTED — a naive 4-scalar-lane color is wrong)*. The correct mechanism is a `ColorF` endpoints side-store driving a 0→1 progress scalar + an `Accum.Col` fold + a Compose write — L, reuses 100% of the dynamics.
- **Procedural backdrops (B1):** ride `LayerKind.ProceduralBg` on the existing `PushLayer` pair (the EdgeFade=3 precedent); *(verdict SUPPORTED, with the honest note that a generator needs a NEW render-side compose entry — additional allocation-free `FluentGpu.Windows` code, no invariant breach)*. M one-time + M/each shader.

### 6.4 Items resolved as implementation notes (NOT real cuts)
- **σ=100 album-cover blur** — the live blur paths tap-cap (~σ10–60), but the **cached pre-blur RT on track change** (which BetterLyrics itself does) reaches σ=100 exactly. Resolved, not cut (B7).
- **Spectrum additive glow** — the engine composites premultiplied SourceOver, not pure additive `Add`. A tiny additive-blend PSO closes it; visually marginal. Ship SourceOver; add the PSO if the glow looks weak (B8).
- **Spectrum Curve/Ring** — no general path tessellator (`DrawPolylineStroke` ≤4 pts; `DrawTabShape` bespoke). **Chained polylines reach *close*;** a `DrawPath` op + tessellator (+L) reaches pixel-perfect. Ship close; tessellator optional (B8).
- **Per-line continuous-σ blur at fullscreen** — full-canvas today (`SceneRecorder.cs:317,336`); the per-rect scissored downsampled blur (E6, M) the code already names a follow-up reaches full parity. Ship one-shared-blur DoF first.

### 6.5 The one true DATA limit (named, not hand-waved)
- **The audio FFT (and therefore the spectrum and ALL bass-breathing) is blocked until the real audio host lands** — no FFT/spectrum/bass-energy source exists; `WaveeEqualizer` is decorative *(verdict `backdrop-spectrum/no-audio-fft-seam` — SUPPORTED)*. The clean home is the `IAudioHost` seam (`AudioHost.cs:31`, open interface, POD report pattern established), but the PCM lives behind its deferred real implementation. The **same true data-availability limit as the lyrics feed** (§4). The spectrum *geometry* (Bar mode) and the breathing *transform* (`ScaleX/Y`) are fully buildable against a stub today; they light up when PCM is available.

### 6.6 Allocation discipline (the binding invariant, stated honestly)
Every per-frame write across both surfaces is a **mutation of a pre-allocated/pooled buffer or a side-table scalar** — **zero managed allocation in render phases 6-13**. Every array bake (region rects, char→syllable maps, keyframes, per-glyph color/transform buffers, accent crossfade, bass smoothing) happens at the **reconcile edge** (phase ≤5, where bounded Gen0 is allowed and the gate excludes it). This is satisfiable because A1/A6 ride `Drive`+side-tables, A2/A4 ride pooled-buffer mutation, A3 rides `SeedColor` (0-alloc two-point), A7 rides existing `Opacity`, B2 bakes the CB via `MemoryMarshal.Write` into a pre-mapped buffer, and B8's bars/bass are reconcile-edge seeds. It is **near-zero-allocation** in the repo's exact sense — zero per-frame managed alloc on phases 6-13, bounded Gen0 at the reconcile/record edge — not a claim of zero everywhere.

---

## 7. Where to change what (file map; companions own their slices)

| Concern | File(s) |
|---|---|
| Surface split — rail shell | docs/wavee-lyrics-canonical-design.md §2 (authority) → `Wavee/Features/Shell/WaveeShell.cs:195-294`, new `Wavee/App/ShellUi.cs`, `Wavee/Features/Player/RightRail.cs` |
| Surface split — fullscreen | `Wavee/Features/Shell/NowPlayingView.cs:21-30` (`NowPlayingLayer`, gated `bridge.Expanded`); mounted `WaveeShell.cs:317`; `PlaybackBridge.cs:60` |
| Lyrics view + ticker (both surfaces) | new `Wavee/Features/Player/LyricsView.cs`, `LyricsTicker.cs` |
| Shared smooth clock | factor from `Wavee/Features/Shell/SeekBar.cs:56-101` |
| A1 wipe op + payload + writer | `src/FluentGpu.Engine/Render/DrawList.cs` (`DrawGlyphRunGradient=15` + cmd) |
| A1 PSO + per-draw stops | `src/FluentGpu.Windows/D3D12/GlyphRenderer.cs` (`PSMainGradient`); dispatch `D3D12Device.cs` |
| A1 recorder + `KaraokeWipe` side-table | `src/FluentGpu.Engine/Render/SceneRecorder.cs` (~`:491-519`), `Scene/Columns.cs` + `Scene/SceneStore.cs` (`SetKaraokeSplit`) |
| A1 split clock channel | `src/FluentGpu.Engine/Animation/AnimScheduler.cs` (`WipeSplit` side-table, `:239-250`); `Timeline.cs` `Drive` |
| A2 per-glyph color | `src/FluentGpu.Engine/Render/SceneRecorder.cs` + `Scene/Columns.cs` (`GlyphColorRamp`); `Windows/D3D12/GlyphRenderer.cs` `Replay`/`LayoutRun` override param (`:566`) |
| A3 Color channel | `src/FluentGpu.Engine/Animation/AnimTypes.cs:18` (`Color`), `AnimScheduler.cs` (`_colorBySlot`, `Accum.Col`, Compose, `CurrentValue`, `SeedColor`/`SpringColor`); `Hooks/RenderContext.cs` (`UseColorAnimation`) |
| A4 per-glyph transform | `src/FluentGpu.Engine/Render/SceneRecorder.cs` + `Scene/Columns.cs` (`GlyphXform`); `Windows/D3D12/GlyphRenderer.cs` `Replay` param + per-glyph affine compose |
| A5 stroke | `src/FluentGpu.Engine/Render/DrawList.cs` (stroke flag on A1's cmd); `Windows/D3D12/GlyphRenderer.cs` (offset-stroke R8 mask via `GetGlyphRunOutline` + stroke atlas + stroke PSO) |
| A6 glow | composes A1 + `BlurSigma`/`ClipR` — `src/FluentGpu.Engine/Render/SceneRecorder.cs` (twice-emit active line) |
| A7 multi-layer | control composition — `Wavee/Features/Player/LyricsView.cs` |
| B1 ProceduralBg op + spec | `src/FluentGpu.Engine/Render/DrawList.cs` (`LayerKind.ProceduralBg=4` + `PushLayerCmd` fields + writer); `Foundation/Effects.cs` (`ProceduralBgSpec`); `Scene/SceneStore.cs` (ColdSlab); `Render/SceneRecorder.cs` (branch); `Headless/Rhi/HeadlessGpuDevice.cs` (assertion) |
| B2 time + CB | `src/FluentGpu.Engine/Seams/Rhi/Rhi.cs:9` (`FrameInfo.TimeSec`); `AppHost` (`_timeSec` accumulate); CB in the compositor |
| B3-B6 shaders + compositor | new `src/FluentGpu.Windows/D3D12/ProceduralBackdropCompositor.cs` (clone `OpacityLayerCompositor.cs`); dispatch `D3D12Device.cs:943,972,1034`; DSL surface `src/FluentGpu.Controls` writes the POD |
| B7 cover | shipped primitives — `Wavee/Features/Player/LyricsView.cs` (ImageEl + `Rotation` + `CrossFade` + cached pre-blur RT via Acrylic/`LayerId`) |
| B8 FFT + spectrum | `src/apps/Wavee/Backend/` or `src/FluentGpu.WindowsApi/Media/` (`IAudioSpectrum` over `IAudioHost`, `AudioHost.cs:31`); Bar geometry in `Wavee/Features/Player/LyricsView.cs` |
| E6 per-rect blur | `src/FluentGpu.Windows/D3D12/OpacityLayerCompositor.cs` (RT sizing + scissor) |
| E-3D | `src/FluentGpu.Engine/Render/DrawList.cs` (`LayerKind.Transform3D` + matrix on `PushLayerCmd` + writer); `Scene/Columns.cs` (`Transform3DSpec`); `Render/SceneRecorder.cs`; `Windows/D3D12/OpacityLayerCompositor.cs` (perspective-quad PSO) |
| E-CJK | Tier 1: rides A4 (`GlyphRenderer.cs` `Replay` + `SceneRecorder.cs`) / Tier 2: `src/FluentGpu.Windows/DirectWrite/TextLayoutEngine.cs` (vertical reading direction) + `IFontSystem` `WritingMode` |
| Data pipeline | docs/lyrics-aggregator-reranker-plan.md (authority) → `Wavee/Backend/Lyrics/`, fix `Services.cs:170` |

**Companion authorities:** rail shell → `docs/wavee-lyrics-canonical-design.md`; original inventory + per-feature table → `docs/betterlyrics-parity-plan.md` (this doc supersedes its scope verdicts and corrects the two false claims in §6.1); data pipeline + reranker → `docs/lyrics-aggregator-reranker-plan.md`. BetterLyrics reference: `LyricsAnimator.cs`, `LyricsLineRendererBase.cs`, `LyricsSynchronizer.cs`, `RenderLyricsChar.cs`, `FluidBackgroundEffect.cs`, `SpectrumAnalyzer.cs`, `CoverBackgroundRenderer.cs`, `NowPlayingCanvas.xaml.cs`.

---

**Bottom line.** The full BetterLyrics look is reachable, and the surface split is what makes it honest: the rail carries the readable line+karaoke+per-char tier, the fullscreen Expanded surface carries the maximalist backdrops + spectrum + 3D. The line-level view ships first with zero engine work; the **signature** (soft gradient karaoke wipe, continuous per-syllable color, per-letter float/glow, ~~pop~~ — char pop removed 2026-08-03, refuted by frame-verified Apple Music footage) lands in **Phase 2**, not deferred — because the GPU is already per-instance-capable and the wipe is the gradient PS masked by the R8 coverage that already exists. Procedural backdrops + spectrum land in **Phase 3** behind one `LayerKind.ProceduralBg` primitive and hand-authored HLSL (the transpiler is **not** vendored). The two genuinely hard items — the 3D perspective fan and vertical CJK — are bounded new-primitive work in clean seams, default-off in BetterLyrics, and land last; they are named, not hand-waved. The two true data limits (the real lyrics feed and the audio FFT) are blockers behind their respective seams, named honestly. Total full-parity engine cost: **L–XL, concentrated and well-scoped**, with **zero per-frame managed allocation in render phases 6-13** held throughout.
