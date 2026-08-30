<!--
BetterLyrics -> FluentGpu "Wavee" synced-lyrics parity blueprint.
Generated 2026-06-29 by a multi-agent investigation workflow (29 agents: 7 understand / 3 map / 18 adversarial-verify / 1 synthesize).
Provenance note: 3 of 4 BetterLyrics phase-1 structured readers stalled mid-stream (transient API error);
the BetterLyrics feature inventory below was reconstructed by the mapping agents reading source directly.
Three load-bearing facts were independently re-verified against source after generation:
  - BetterLyrics karaoke fill = 4-stop soft-fade horizontal gradient  (LyricsLineRendererBase.cs:173-204)  [confirmed]
  - FluentGpu AnimChannel has no Color lane                           (AnimTypes.cs:18)                     [confirmed]
  - DrawOp is a closed 14-op set (no glyph-gradient / app-shader op)  (DrawList.cs:7-20)                    [confirmed]
-->
# Porting BetterLyrics → FluentGpu "Wavee": Synced-Lyrics Control — Engineering Blueprint

**Status:** decision-ready. **Authority for engine facts:** the adversarial verdicts in the brief, cross-checked here against live `src/` and `app/` source. Where a verdict refutes a convenience assumption, this plan treats the corrected behavior as binding.

> **SUPERSEDE NOTE — char pop removed 2026-08-03 — refuted by frame-verified Apple Music footage.** There is **no glyph
> magnification** in the reference animation, so the syllable/per-word scale-pop is no longer a Wavee target and the
> engine's forced coupling of `GlyphWipe.Lift > 0` to a ≈1.12× per-glyph pop (`GlyphRenderer.LayoutRunGradient`) is
> deleted. `Lift` is now **dy-only**: an unsung glyph sits `Lift` DIP below its baseline and rises smoothly to it as the
> feather band sweeps it; a sung glyph never moves. BetterLyrics' own `1.0→1.15→1.0` pop, inventoried in §2.3, is still
> an accurate description of *BetterLyrics*. Every "scale-pop" row below is superseded on that point only. Authority:
> the frame-verified Apple-Music behavioral spec (2026-08-03 lyrics-parity campaign) + the `FluentGpu.Foundation.GlyphWipe` XML contract.

---

## 1. Executive summary & overall parity verdict

**Can FluentGpu reach 1:1 with BetterLyrics? Partially — and the split is clean and defensible.**

There are two distinct products hiding inside "BetterLyrics parity," and they have very different verdicts:

- **A faithful Spotify/Apple-Music-class synced-lyrics view** — active-line highlight, velocity-continuous auto-scroll that keeps the focal line in a band, distance-scaled fade/scale/blur/2D-fan of neighbouring lines, palette-driven line colors, top/bottom edge-fade vignette, blurred album-art backdrop, marquee for overflowing lines, line-level *and* word-level karaoke via a crisp wipe or discrete recolor — **is reachable today with ZERO engine changes**, mostly at **close-to-full parity**. Every primitive it needs is shipped and verified (driven clock → `ClipR`/`TranslateY`/`BlurSigma`; `BrushTransitionMs` color crossfade; `EdgeFade`; the built `Marquee`; the `SeekBar` smooth-clock idiom; on-demand frame loop that idles when paused). This is the recommended **Phase 1–2 deliverable**.

- **BetterLyrics' *signature* maximalist render** — the **soft, continuously-sweeping gradient karaoke wipe across glyphs**, **continuous per-syllable color ramps driven by playback-ms**, **per-letter float/scale/glow at full line scale**, **text stroke/outline**, **glyph-shaped bloom**, **3D perspective fan**, **vertical CJK orientation**, and **procedural fluid/fog/rain/snow shader backdrops** — **is NOT reachable without bounded, well-scoped engine work**, because the engine's text/glyph layer is deliberately per-run-flat (one `Affine2D`, one `ColorF`, R8 coverage) and the `DrawOp` set is closed with no app shader seam.

**Honesty note (this repo's discipline):** I will *not* call the gap "near-zero." The line-level view has a near-zero parity gap; the per-glyph/karaoke-soft-wipe/color-ramp layer has a **real, named gap that requires new engine primitives**. The good news is that BetterLyrics itself ships most of the maximalist effects **off by default** (`Is3DLyricsEnabled=false`, `IsFanLyricsEnabled=false`, `IsLyricsBrethingEffectEnabled=false`, fog/rain/snow are opt-in backdrops — `LyricsEffectSettings.cs:7-115`), so "1:1 with default BetterLyrics" is materially closer than "1:1 with everything enabled."

**Headline effort:**
- **Phase 1–2 (line-level + hard-edge/discrete karaoke + per-word motion + theming + backdrop): M.** No engine changes. Ships a beautiful, correct, synced view.
- **Full signature parity: L–XL of engine work**, concentrated in three text-layer additions (soft gradient glyph wipe, `AnimChannel.Color` + per-instance glyph color, per-glyph transform) plus a leak fix and a blur-cost fix. Everything else (3D, vertical, procedural backdrops, audio FFT) is genuinely out-of-core and matches BetterLyrics' own "optional toggle" status.

**Hard blocker independent of rendering:** there is **no real Spotify lyrics feed**. The only `ILyricsProvider` is `FakePlaybackProvider` returning 4 hardcoded lines (`FakeData.cs:523-538`), and it is passed as the lyrics arg even on the real path (`Services.cs:170`). A `SpotifyLiveLyrics` client is required before any of this is more than a demo.

---

## 2. BetterLyrics feature inventory (what we must replicate)

Grouped; each with the parameters that drive the renderer. Source refs are BetterLyrics.

### 2.1 Layout & multi-layer text
- **Three text layers per line**: Primary=original, Secondary=translation, Tertiary=phonetic/romanization (`RenderLyricsLine.cs:19-195`). Layer draw order `[Tertiary, Primary, Secondary]` (phonetic on top). Per-layer opacity (percent/100): Phonetic 60, Played-original 100, Unplayed-original 30, Translated 60 (`LyricsStyleSettings.cs:10-127`).
- **Alignment** per line from `AgentId` → Left/Center/Right (duet singers); `CenterPosition.X` is the scale/rotation pivot (`HorizontalLyricsLayoutStrategy.cs:29-234`).
- **Wrap** (`AutoWrap=true`), font sizes (Original 24, Translated/Phonetic 12), weight `Bold`, CJK/Western font families, line-spacing factors (overall 0.5, inner 0.1), `PlayingLineTopOffset=50%` (focal band).
- **Lane system**: overlapping lines (TTML x-bg backgrounds, duets) packed into lanes, 50ms tolerance; lane 0 wins (`LyricsSynchronizer`/`CalculateLanes`).
- **Vertical orientation** (`VerticalLyricsLayoutStrategy`): top-to-bottom columns, X-as-scroll (optional mode).

### 2.2 Per-line animation (the ~15 `ValueTransition`s, `RenderLyricsLine.cs:439-459`)
Driven by `LyricsAnimator.UpdateLines` (`LyricsAnimator.cs:17-383`) which maps `(isThisLinePlaying, distanceFromPlayingLine, palette, settings)` → targets each frame:
- **Scroll offset** (`OffsetTransition` + global `_canvasScrollTransition`): ease the column so the focal line's `CenterPosition.Y` sits at the 50% band. `LyricsScrollDuration=500ms`, `Quad/Out`, per-line distance-scaled duration+delay (`scrollDuration = canvasTransDuration + distanceFactor*(scrollTop/BottomDuration - canvasTransDuration)`).
- **Scale** `0.75 ↔ 1.0` by `distanceFactor` (out-of-sight shrink). `defaultScale=0.75`, `highlightedScale=1.0`.
- **Blur** `0 → 5σ * distanceFactor` (depth-of-field on inactive lines).
- **Fan angle** `0 → fanAngleRad * distanceFactor` (2D), `FanLyricsAngle=30`, default off.
- **Opacity ×4**: Played/Unplayed primary, Secondary, Tertiary; whole-line fade-out `(1-distanceFactor)*base`.
- **Color ×4** (AppColor, 0.3s): Played/Unplayed Fill, Played/Unplayed Stroke; only the active line shows played≠unplayed colors, off-lines are a single grayed color (`NowPlayingPalette.cs:6-26`, `LyricsAnimator.cs:191-205`).
- **Marquee X-offset** per layer (no-wrap overflow), the three layers phase-locked.
- **3D parallax** (`Is3DLyricsEnabled`, default off): `Matrix4x4` with `M34=1/depth`, `Lyrics3DDepth=800`.
- **Breathing** (default off): bass-energy scale pulse.

### 2.3 Word-by-word / per-character karaoke
- **Sync predicates** (every node, `BaseRenderLyrics.cs:5-17`): `GetIsPlaying(t) = StartMs<=t && t<EndMs` (half-open, EndMs-exclusive); `GetPlayProgress(t)=Clamp((t-StartMs)/DurationMs,0,1)` with a **NaN guard** (zero-duration → NaN). `IsPlayingLastFrame` is a one-frame edge latch the animator mutates to fire keyframes once.
- **Karaoke fill** (`LyricsLineRendererBase.cs:79-272`): per text region a **4-stop horizontal gradient** with a moving split = `playedWidth/regionWidth`, played→sharp edge→**soft fade**→unplayed; fade width `=(1/charCount)*0.5*firstCharProgress`. Continuous, clock-driven.
- **Per-char float** `0→peak→0`, fires once on syllable start, `LyricsFloatAnimationDuration=450ms`, per-char begin delay; amount `lineHeight*0.1` (auto).
- **Per-char/long-syllable glow** `0→glowPx`, cropped to the played portion, GaussianBlur additive; gated to syllables ≥`700ms`; amount `lineHeight*0.2` (auto).
- **Syllable scale-pop** `1.0→1.15→1.0`, ~350ms, pivot = syllable center; gated to syllables ≥700ms. *(Accurate for BetterLyrics; **NOT a Wavee target** — char pop removed 2026-08-03, refuted.)*
- **Char timing** is *synthesized uniformly* inside a syllable (`avgCharDuration = syllable.DurationMs/length`) even for real word-by-word data (`RenderLyricsChar.cs:11-58`).

### 2.4 Effects surface (`LyricsEffectSettings.cs:7-115`)
WordByWord mode (Auto/Always/Never), blur, fade-out, edge-feather, out-of-sight, glow (scope/threshold 700/amount 8/auto), shadow (off), scale (threshold 700/amount 115%/auto), float (amount 8/dur 450/auto), scroll (Quad/Out, 500/500/500ms + delays), fan (30°, off), 3D (off), breathing (off). **Toggling scale/glow triggers a full RELOAD** because `EnsureRenderLyricsLinesPreservedAnimation` pads trailing-long-syllable `EndMs` by +350ms (`NowPlayingCanvas.xaml.cs:1128-1173`).

### 2.5 Backdrop
- Blurred + slowly rotating + crossfading album cover (`GaussianBlur ~100`, oversized to screen diagonal).
- Fluid procedural gradient (ComputeSharp D2D1 noise-warp shader, 4 accents).
- Fog / rain / snow (ComputeSharp D2D1 shaders).
- Audio spectrum visualizer (bars / curve / around-album ring) — needs real-time FFT.

### 2.6 Data / sync
- **Parse**: LRC/TTML/QRC/KRC → `LyricsData`; `EnsureSyllables` (fabricate uniform per-char syllables for plain LRC) + `EnsureEndMs` (line EndMs = last syllable EndMs or next-line StartMs) (`LyricsContentParser.*`).
- **Clock**: local dt-integration while playing; resync to OS position only past `TimelineSyncThreshold`; +5000ms = seek→relayout; per-source `PositionOffset` (`NowPlayingCanvas.xaml.cs:1048-1200`).
- **Current-line resolver** (`LyricsSynchronizer.cs:16-74`): **next-line StartMs is the boundary, not EndMs**; cached last-index fast path; lane-aware; +1000ms lookahead early-exit. *Distinct* from each node's own `GetIsPlaying`.
- **Palette**: 12-field `NowPlayingPalette` from album art (off-thread Impressionist OctTree/KMeans), mapped to per-line fill/stroke colors + 4 background accents.
- **Frame cadence**: Win2D `CanvasAnimatedControl` owns the loop; `Update(dt)` then `Draw`; `dt = 1/FPS` (60–480Hz); every `.Update(dt)` is dt-driven with a while-loop overflow consumer (`ValueTransition.cs:7-263`).

---

## 3. FluentGpu capability & gap summary (verified)

### 3.1 What the engine already gives us
- **AnimValue slab** — one POD row per `(NodeHandle, AnimChannel)`, reconciler-owned free-list, **zero managed alloc in frame phases 6–13**. Channels (verified `AnimTypes.cs:18`): `TranslateX/Y, ScaleX/Y, Rotation, Opacity, SizeW/H, StrokeTrimStart/End, ClipL/T/R/B, LayoutW/H, BlurSigma, BrushFade, HoverFade, PressFade`. Sampling laws: analytical spring (velocity-continuous retarget), eased two-point, **multi-segment keyframes** (per-segment cubic-bezier, loop, per-row `delayMs`), and the **driven** path.
- **Driven animation** (verified `AnimScheduler.Timeline.cs:52-81`) — `Drive(node, ch, keys, drivenRef, domainMin, domainMax)`; progress `u = clamp01((Clocks.Sample(ref) - min)/(max-min))`, **time-independent** (stays synced across seek/pause). `Clocks.Register(Func<float>)` registers a value source (e.g. playback ms).
- **Animated self-blur** (`AnimChannel.BlurSigma`) → `PushBlurLayer` separable Gaussian (verified `SceneRecorder.cs:317,336`).
- **Declarative Element surface** — `Transition`, `WhileHover/Pressed/Focus`, `Enter/Exit` (incl. `Blur`), `Layout`/FLIP, `Stagger` (per-child delay = index×stagger), `MorphId`.
- **Color crossfade** — `TextEl.Color` + **`BrushTransitionMs`** (a float; default NaN=snap) → `BrushFade` side-table, recorder lerps old↔new in linear-premultiplied (verified flow `Reconciler.cs:2287-2310 → AnimScheduler.cs:215 → SceneRecorder.cs:821-828`). One resting color per node; 3 brush channels (Fill/Border/Text).
- **Text** — DirectWrite itemize→BiDi→shape→wrap→R8 atlas→instanced quads. Per-RUN one `Affine2D`+one `ColorF`. **Per-RANGE color/weight/size** via `SpanTextEl`/`SpanRunId`. Geometry queries: `IFontSystem.GetRangeRects/HitTestText/GetCaret`.
- **Draw ops** — closed `DrawOp` 14-op set (verified `DrawList.cs:7-20`): rounded-rect fill/stroke, glyph run, clip (rect scissor + tier-2 rounded clamp for RoundRect pipeline only), image (with crossfade), shadow (rounded-box), gradient rect/stroke (≤4 stops, **rect only**), push/pop layer (Acrylic/Opacity/Blur/EdgeFade), arc, polyline (≤4 pt), tab. Per-node compositor transform/opacity/clip, no relayout.
- **EdgeFade** — first-class per-edge alpha-feather vignette following rounded corners.
- **Marquee** — shipped control: looping `TranslateX` keyframes, edge fade, `CycleMs` phase-lock, honors reduced-motion.
- **Smooth scroll seam** — `ScrollAnimator` armed `Target`; but a `Component` cannot reach it for an eased `scrollTo` — translate your own content node instead.
- **On-demand frame loop** — wake-reason driven, vsync-locked present, idles at 0% CPU when no active rows/tickers and not minimized (verdict #4 supported). Gate a ticker to mount only while needed.
- **Component/hooks** — `Component`/`ReactiveComponent`, `UseState/UseSignal/UseFloatSignal`, `UseEffect`, **`UseAsyncResource(loader, seed, deps)`** (verified `Component.cs:56-60`), `UseSpring/UseTransition/UseKeyframes/UseDrivenAnimation`, `UseContextSignal(FrameClock.Tick)`.
- **Wavee data signals** — `PlaybackBridge` (verified `PlaybackBridge.cs:27-60`): `CurrentTrack`, `PositionMs` (~1Hz), `PositionFrac`, `DurationMs`, `IsPlaying`, `TrackPalette` (`Signal<Palette?>`), `Expanded`. Lyrics model `LyricSyllable/LyricLine/LyricsDocument` + `ILyricsProvider` (verified `Playback.cs:76-83`). `SeekBar` smooth-clock interpolation idiom (verified `SeekBar.cs:56-101`).

### 3.2 Verified gaps (ground truth)
1. **No `AnimChannel.Color`** — color motion is *only* the 2-endpoint `BrushTransition` crossfade on re-render, or a discrete `SpanRunId` re-mint (which **re-shapes the line**). No continuous playback-ms color ramp. (verified `AnimTypes.cs:18`; verdicts #2/#5/#9-color)
2. **No per-glyph addressing** — a glyph run is ONE node/ONE transform/ONE color. Per-character motion ⇒ one node per glyph (un-budgeted at scale, loses shaping). The canonical per-char-color path is per-instance glyph color in one run — **unbuilt**. (verdict #3)
3. **No gradient-on-glyph / soft glyph clip** — karaoke wipe is either discrete span recolor or a **hard** rectangular `ClipR` wipe; no soft gradient edge. (verdicts #6/#14)
4. **No glyph stroke/outline, no glyph-shaped shadow/glow** — `GetGlyphRunOutline`/`FillPath` are spec-only; `DrawShadow` is rounded-box; oversized glyphs render nothing. (verdict #15)
5. **No perspective transform** — `Affine2D` is 6-param 2D; `Transform3D` layer unimplemented. (verdict #10/#16)
6. **No vertical CJK text layout** — horizontal LTR+RTL/BiDi only. (verdict #11)
7. **No app shader seam / no procedural-backdrop op** — `DrawOp` closed, no `VisualKind.Custom`, no D2D1 effect leaf. (verdicts #17/#18)
8. **No audio FFT/spectrum/bass-energy signal** — `WaveeEqualizer` is decorative/hardcoded. (verdict #12)
9. **Driven clock leaks** — `DrivenClockTable.Register` is append-only (verified `AnimTypes.cs:70`); per-line `Drive` under a re-mounting list leaks one closure each. (verdict #8)
10. **Per-line blur is full-canvas** — each `BlurSigma` group is a full-canvas 2-pass Gaussian; ~20 lines ≈ 40 full-canvas passes/frame; the per-rect bucket is an explicit deferred optimization. (verdicts #1/#13)
11. **No real lyrics feed; lean data model** — `LyricLine` has no line `EndMs`, no translation/phonetic, no `AgentId`/lane, no `IsWordByWord` flag; provider is the fake even under `--real-backend` (`Services.cs:170`). (verdict in data theme)

---

## 4. Parity mapping table (feature → mechanism → effort → risk → status → rating)

Effort S/M/L/XL; Status folds the verdicts; Rating = full/close/approximate/blocked. "Engine" = needs §5 work.

### 4.1 Per-line animation
| BetterLyrics feature | FluentGpu mechanism | Effort | Risk | Verified status | Rating |
|---|---|---|---|---|---|
| Auto-scroll line-follow (`_canvasScrollTransition`) | `UseSpring(TranslateY, -focalOffset, deps:activeLine)` on own content column (NOT `ScrollEl` offset) | M | low | supported (verdict #4; memory note) | **full** |
| Per-line scale 0.75↔1.0 | `ScaleX`+`ScaleY`, `TransformOrigin`=CenterPos, seed on activeLine change | S | low | supported | **full** |
| Per-line 2D fan angle | `AnimChannel.Rotation` about origin | S | low | supported (verdict #16) | **full** |
| Per-line blur 0→5σ (DoF) | `BlurSigma` per line **OR** one shared backdrop blur | M | **med** | partially-supported — full-canvas cost (verdicts #1/#13) | **close** (with shared-blur compromise) or Engine |
| 4-way opacity (played/unplayed/2nd/3rd) | OpacityGroup container + `Opacity` per child node | M | low | supported | **close** |
| Line fill color (active↔inactive, palette) | `TextEl.Color` literal + **`BrushTransitionMs=300f`** (NOT `Element.Transition`) | M | low | supported — *mechanism-name corrected* (verdicts #2/#5) | **close** |
| Played≠unplayed **split colors** on active line | two stacked color copies + karaoke split | L | high | only discrete/hard today | **approximate** → Engine for continuous |
| Stroke color tween | — | — | — | no glyph stroke (verdict #15) | **blocked** → Engine |
| Marquee X-offset (overflow) | shipped `Marquee.Of`, shared `CycleMs` | S | low | supported | **full** |
| 3D perspective fan / parallax | — | XL | high | blocked (verdict #10/#16) | **blocked** (BL default-off) |
| Breathing (bass pulse) | `ScaleX/Y` driven — *transform* full | M | med | driver (FFT) missing (verdict #12) | **approximate** (BL default-off) |

### 4.2 Word-by-word / per-character
| BetterLyrics feature | FluentGpu mechanism | Effort | Risk | Verified status | Rating |
|---|---|---|---|---|---|
| Karaoke fill — **hard** wipe | two stacked copies + `ClipR` `Drive(mediaClockRef, lineStart, lineEnd)` | M | med | **supported** (verdict #6) | **approximate** (hard edge) |
| Karaoke fill — discrete recolor | per-syllable `SpanTextEl`, re-mint on boundary | M | med | supported; re-shapes line per boundary (verdict #14) | **approximate** (flat flip) |
| Karaoke fill — **soft gradient** wipe | gradient-on-glyph / per-instance clock color | L | high | blocked today (verdicts #6/#14) | **blocked** → Engine |
| Per-char float (0→peak→0, delay) | `Keyframes(TranslateY, CompositeOp.Add)` on per-glyph node | L | med | supported small-scale; un-budgeted at line scale (verdict #3) | **approximate** (per-word full; per-letter Engine) |
| ~~Syllable scale-pop 1→1.15~~ **(superseded 2026-08-03 — refuted, removed)** | `UseSpring(Scale, ExpressiveSpring)` overshoot, per-word node | M | low | supported | **not a target** |
| Per-char glow (cropped, additive) | blurred duplicate + `BlurSigma` + `ClipR` | L | med | supported as halo, not additive bloom (verdict #7) | **approximate** |
| Char timing synthesis (avgCharDuration), NaN guard | component logic | S | low | ports verbatim | **full** |

### 4.3 Effects / backdrop
| BetterLyrics feature | FluentGpu mechanism | Effort | Risk | Verified status | Rating |
|---|---|---|---|---|---|
| Edge-fade vignette (top/bottom dissolve) | `EdgeFadeSpec` on viewport / `ScrollEl.AutoEdgeFade` | S | low | supported | **full** |
| Blurred rotating album backdrop | `ImageEl` + looping `Rotation` + Acrylic/BlurHash | M | med | heavy blur clamped ~60px (verdict #9) | **close** |
| Fluid / fog / rain / snow procedural | — | L (each rides one new op) | high | blocked — closed op set (verdicts #17/#18) | **blocked** (BL backdrop opt-in) |
| Spectrum visualizer (bars) | N `FillRoundRect` animated | L | high | bars approx; curve/ring need path tessellator + FFT | **approximate** |
| Text outline / drop shadow on glyphs | faux multi-draw / blurred copy | XL | high | blocked (verdict #15) | **blocked** → Engine |
| Vertical CJK orientation | — | L | med | blocked (verdict #11) | **blocked** (optional mode) |

### 4.4 Data / sync (all component-level, port cleanly)
| BetterLyrics feature | FluentGpu mechanism | Effort | Risk | Status | Rating |
|---|---|---|---|---|---|
| `GetIsPlaying`/`GetPlayProgress` + `IsPlayingLastFrame` latch | C# methods on render VM | S | low | supported | **full** |
| Current-line resolver (next-StartMs boundary, lanes) | component logic (binary search) | S | low | supported; lanes need richer model | **close** |
| Local-interpolated clock + resync threshold | `UseSmoothPositionMs` (factor `SeekBar.cs:56-101`) | S | low | supported | **close** |
| Frame cadence (dt-driven, idle-when-paused) | `FrameClock.Tick` ticker gated on `Expanded && IsPlaying` | S | low | supported (verdict #4) | **close** |
| EndMs +350ms long-syllable padding | render-VM arithmetic | S | low | supported | **full** |
| `ValueTransition<T>` (scalar) | AnimValue slab (absolute-time sampled) | S | low | supported | **full** |
| `LyricsAnimator.UpdateLines` (per-frame target rewrite) | **seed-on-deps** (not per-frame) + Drive for continuous | M | med | supported with zero-alloc discipline (verdict #8) | **close** |
| Palette → line colors | `bridge.TrackPalette` (4-field) + luminance map | M | med | exists; richer 12-field is app-derived | **approximate** |
| Real synced-lyrics feed | `SpotifyLiveLyrics : ILyricsProvider` | M–L | **high** | **blocked today** (`Services.cs:170`) | **blocked** → backend |

---

## 5. Required engine work (only what verification proved necessary)

Each item names its `src/` home, honors **TerraFX-free `FluentGpu.Controls`** (TerraFX enters only via `FluentGpu.Windows`) and **zero managed alloc in frame phases 6–13** (all seeding at the reconcile edge; per-instance arrays baked at record time, not per-frame in phases 6–13). Items are ordered by leverage. **None of §5 is required for the Phase 1–2 line-level view** — they unlock the maximalist signature only.

### E1 — Soft, clock-driven glyph fill-wipe (the karaoke signature) — **L**
**Why:** the soft sweeping gradient edge is the one BetterLyrics look with no primitive; today only a hard `ClipR` wipe (verdict #6) or a discrete span recolor (verdict #14) exists.
**Where:** new `DrawGlyphRunGradient` payload + `DrawOp` value in `FluentGpu.Engine/Render/DrawList.cs`; emit from `SceneRecorder` Text case; the pixel shader lands in `FluentGpu.Windows/D3D12/GlyphRenderer.cs`.
**Approach:** sample a small (≤4-stop) gradient *along the run's local-x axis* through the existing R8 coverage in the glyph PS (coverage × gradient(localX) × opacity, premultiplied). Drive the split position by the playback-ms `Drive` path (the gradient stops are a uniform updated at record time from the active line's `GetPlayProgress` × per-region rects from `GetRangeRects`). This is portable: the *op* is in Engine, the *PS* is in Windows. Reuses `GetRangeRects` for region geometry.

### E2 — `AnimChannel.Color` (18th channel) + per-instance glyph color re-record — **L**
**Why:** continuous per-syllable color ramps and the active-line played/unplayed *split* colors cannot tween on the slab (no Color lane — verified `AnimTypes.cs:18`; verdicts #2/#5). Today's only color motion is the 2-endpoint `BrushFade`.
**Where:** `FluentGpu.Engine/Animation` (enum + `Accum.Fold` premultiplied-color fold + Compose write into a `NodePaint` color slot) and the per-instance color write in `SceneRecorder` → `GlyphRenderer.Replay`.
**Approach:** the brief's capability map and verdicts confirm `GlyphInstance` *already carries per-instance R,G,B,A*, so the GPU side is nearly free; the work is (a) a Color row that folds 8 premultiplied-linear lanes, and (b) a phase-7 per-instance color write from a per-syllable color source. Compose stays alloc-free (folds into a fixed `NodePaint` field); the per-instance color array is a record-time bake (reconcile edge), not a phase 6–13 allocation. Pairs with E1 for "gradient + animated color."

### E3 — Per-glyph transform addressing in `DrawGlyphRun` — **M–XL**
**Why:** per-*letter* float/scale/glow at line scale via per-glyph nodes is un-budgeted and loses shaping/kerning (verdict #3). The GPU `GlyphInstance` already has per-instance `M11..Dy`; nothing addresses them.
**Where:** a `DrawGlyphRun` variant + `SceneRecorder` + `GlyphRenderer.Replay` (feed per-instance transforms) + a per-glyph anim addressing scheme `(node, glyphIndex, channel)` in `FluentGpu.Engine/Animation`.
**Approach:** keep ONE shaped, correctly-kerned run; bake a per-glyph offset/scale array at record time from a sub-keyed AnimValue side-table. **Scope decision:** per-*word* motion needs NO engine work (verdict-confirmed cheap via one `BoxEl`/word) — only commit E3 if per-*letter* motion at full line scale is a hard requirement.

### E4 — Glyph outline/stroke (+ glyph-shaped shadow/glow) — **XL** (outline) / **L** (glow-via-composition is already approximable)
**Why:** `LyricsFontStrokeWidth` and true bloom have no primitive (verdict #15). Glow is *approximable today* (blurred duplicate, verdict #7), so only **stroke/outline** is genuinely blocked.
**Where:** `GetGlyphRunOutline` → SDF/offset-mask atlas in `GlyphRenderer` + a `DrawGlyphStroke` op + a stroke pipeline in `FluentGpu.Windows/D3D12`.
**Approach:** bake an offset stroke mask into an R8 mask atlas keyed with the run cache; new op samples it and fills (flat or, with E1, gradient) behind the coverage quads. The same mask, Gaussian-blurred into a temp RT, yields a glyph-shaped shadow/glow. **Defer unless stroke is required** — BetterLyrics default `LyricsFontStrokeWidth=0`.

### E5 — Index-based `SignalSource` (MediaClockMs) replacing the closure clock — **M**
**Why:** `DrivenClockTable.Register` is append-only (verified `AnimTypes.cs:70`); per-line `Drive` under a virtualized re-mounting list leaks one `Func<float>` per mount (verdict #8).
**Where:** `FluentGpu.Engine/Animation`.
**Approach:** resolve the media clock once at reconcile by index. **Avoidable** for a bounded visible window by registering ONE app-lifetime clock and reusing the `drivenRef` across all lines (verdict #8 corrected approach) — commit E5 only if lyrics virtualize *and* re-mount lines per scroll.

### E6 — Per-rect + downsampled self-blur (progressive-blur cost) — **M**
**Why:** each `BlurSigma` group is a full-canvas 2-pass Gaussian; ~20 progressively-blurred lines ≈ 40 full-canvas passes/frame (verdicts #1/#13; the code names the per-rect bucket a deferred optimization).
**Where:** `FluentGpu.Windows/D3D12/OpacityLayerCompositor.cs`.
**Approach:** lease a layer-DeviceRect-sized (bucket-quantized) RT, scissor the two passes to the thin line rect, downsample like the acrylic path. **Avoidable** by using ONE shared backdrop/DoF blur + a sharp active line (verdicts #1/#13 corrected approach) — that is the budget-safe default and is the recommended Phase-2 choice. Commit E6 only for independently-animated per-line sigma.

### E7 — Out-of-core (defer; matches BetterLyrics' optional toggles)
Fullscreen procedural-shader op (`DrawProceduralBg` for fluid/fog/rain/snow; the ComputeSharp C#→HLSL transpiler is already vendored), arbitrary path tessellator (spectrum curve/ring), 3D perspective transform layer (`PushLayer{Transform3D}`), vertical CJK text layout, and an audio FFT/bass-energy source on the audio seam (`AudioHost`/`IPlaybackState`). Each verified blocked (verdicts #10/#11/#12/#16/#17/#18); each is default-off or backdrop-optional in BetterLyrics. **Not on the critical path.**

---

## 6. Proposed Wavee lyrics control architecture

### 6.1 Data model — the render-lyric graph
Wavee's source model is lean (`LyricLine` has only `StartMs`+`Text`+`Syllables` — verified `Playback.cs:76-78`). The control owns a derived **render VM**, mirroring BetterLyrics' two-layer split:

```
LyricsVm                       // built once per LyricsDocument (UseMemo on track.Id)
  ├─ float scrollEpoch         // bumps on layout/font change → JumpTo vs ease
  └─ LineVm[]                  // one per LyricLine
       ├─ long StartMs
       ├─ long EndMs           // DERIVED = next line StartMs (synchronizer boundary);
       │                       //   + 350ms if last syllable ≥700ms & scale/glow on (EndMs padding)
       ├─ string Primary       // (Secondary/Tertiary only if model is enriched)
       ├─ HAlign align         // from AgentId when present, else settings default
       ├─ int lane             // 0 until model carries lanes
       ├─ float centerY        // layout anchor (focal-scroll target)
       ├─ NodeHandle node      // captured via OnRealized for Drive/seed
       ├─ bool playingLastFrame// one-frame edge latch
       └─ SyllableVm[]         // the REAL timing unit
            ├─ long StartMs,EndMs
            ├─ (charStart,charEnd) range into Primary
            └─ CharVm[]        // synthesized: charStartMs = syl.Start + (i-start)*avgCharDur
                 └─ RectF layoutRect   // from IFontSystem.GetRangeRects
```

Predicates port verbatim (`BaseRenderLyrics.cs:5-17`): `GetIsPlaying`, `GetPlayProgress` (with the **NaN guard**). The current-line resolver ports `LyricsSynchronizer.cs:16-74` (next-StartMs boundary, cached last index, +1000ms lookahead). Char timing is uniform-within-syllable (`RenderLyricsChar.cs`).

**Model enrichment (additive, backend):** add `EndMs` (or keep derived), `SecondaryText`/`TertiaryText`, `AgentId`/`LaneIndex`, `IsWordByWord` to `LyricLine`/`LyricsDocument`. Until a real feed supplies them, the VM derives EndMs and degrades to lane-0/primary-only.

### 6.2 Component / hooks structure
```
LyricsView : Component                         // re-renders only on LOW-freq state
  ├─ var b   = UseContext(PlaybackBridge.Slot)
  ├─ var doc = UseAsyncResource(ct => Services.Lyrics.GetLyricsAsync(track.Id, ct),
  │                             seed:null, deps:[track.Id])        // Component.cs:56-60
  ├─ var vm  = UseMemo(() => BuildVm(doc, settings), [doc, settingsEpoch])
  ├─ _nowMs       : FloatSignal      // smooth playback ms (written by ticker)
  ├─ _activeLine  : Signal<int>      // resolver output (drives scroll + focus)
  ├─ scroll: UseSpring(TranslateY, -vm.Lines[_activeLine].centerY + band, deps:[_activeLine])
  ├─ renders the line column (BoxEl per LineVm) + EdgeFade viewport + backdrop
  └─ mounts LyricsTicker  // ONLY while b.Expanded && b.IsPlaying

LyricsTicker : ReactiveComponent               // 0×0, never re-renders; gated mount
  └─ Setup(): var t = UseContextSignal(FrameClock.Tick);
              UseSignalEffect(() => { _ = t.Value; Owner.OnFrame(); });
              return new BoxEl{Width=0,Height=0,HitTestVisible=false};
```
`OnFrame()` (zero-alloc): compute `nowMs` (SeekBar interpolation), resolve `activeLineIndex`, write `_nowMs`/`_activeLine` (value-gated so an unchanged frame is a true no-op → host skip-submit gate elides the present). Mount-gating on `Expanded && IsPlaying` keeps the GPU idle at rest (verdict #4) — exactly the `SeekTicker` precedent.

### 6.3 Animation-channel table (BetterLyrics transition → FluentGpu binding, trigger, timing)
| BetterLyrics transition | FluentGpu channel / binding | Trigger | Timing |
|---|---|---|---|
| `OffsetTransition` / `_canvasScrollTransition` | `UseSpring(TranslateY)` on content column | deps:`activeLine` change | spring (Quad/Out 500ms via `UseTransition` for exact feel); JumpTo on `scrollEpoch` |
| `ScaleTransition` 0.75↔1 | `ScaleX`+`ScaleY`, origin=CenterPos | activeLine change | seed-on-change |
| `BlurAmountTransition` | `BlurSigma` per line **or** one shared backdrop blur | activeLine change | seed-on-change |
| `AngleTransition` (2D fan) | `Rotation` about origin | activeLine change | seed-on-change |
| Played/Unplayed/2nd/3rd `OpacityTransition` | `Opacity` per stacked child in OpacityGroup | activeLine / isPlaying change | seed-on-change |
| 4× fill/stroke `ColorTransition` | `TextEl.Color` literal + `BrushTransitionMs=300f` (fill); **E2** for continuous; stroke = **E4** | re-render with new literal color | 300ms linear |
| Karaoke fill split | `ClipR` `Drive(mediaClockRef, lineStart, lineEnd)` on played copy (hard); **E1** for soft | continuous (clock) | time-independent |
| Per-char `FloatTransition` 0→peak→0 | `Keyframes(TranslateY,[(0,0),(0.5,-a),(1,0)],450ms, CompositeOp.Add)` per word/glyph node | syllable-start edge | 450ms, per-row `delayMs` |
| ~~Per-syllable scale-pop~~ **(superseded 2026-08-03 — refuted, removed)** | `UseSpring(Scale, ExpressiveSpring)` from 1.15 | syllable-start edge | ~350ms overshoot |
| Per-char `GlowTransition` | blurred duplicate `BlurSigma`+`ClipR` (approx); **E4** for true | long-syllable (≥700ms) | 350ms |
| `*XOffsetTransition` (marquee) | shipped `Marquee`, shared `CycleMs` | overflow detected | looping wall-clock |

**Critical discipline (verdict #8):** BetterLyrics' `LyricsAnimator.UpdateLines` rewrites targets *every frame*; FluentGpu's zero-alloc model forbids per-frame `Animate/Keyframes/Drive` seeding (each allocs a `Keyframe[]`). Map each "set target" → a **deps-keyed seed** (springs retarget velocity-continuously, so the visual is equivalent) and each "continuous progress" → the **`Drive` path** or a **value-gated ticker→FloatSignal bind**. BetterLyrics already gates its block on `isLayoutChanged||isPrimaryPlayingLineChanged||isSecondaryLinePlayingChanged||isArtThemeColorsChanged` — map those exact edges to deps.

### 6.4 Render / draw approach
- **Line column:** `BoxEl` per `LineVm` in a flex column; the column is translated by the scroll spring. Each line wraps up to 3 stacked `TextEl` (layer order Tertiary/Primary/Secondary). For non-karaoke lines, one flat-tinted `TextEl`.
- **Karaoke (Phase 2 default):** two stacked color copies (unplayed beneath, played on top). The **played copy node carries the `ClipRect`**; animate `AnimChannel.ClipR` via `Drive` so the recorder sweeps a rectangular scissor across that node's own glyphs (verified: clip pushed before the node's own glyph emit). Build `ClipR` keyframes from `GetRangeRects` (node-local px, 0→lineWidth). **Hard edge** — honest compromise vs BetterLyrics' soft fade until E1.
- **Per-word motion (Phase 3a):** one `BoxEl`+`TextEl` per word in a flex row; animate ~~`ScaleX/Y` (pop)~~ *(char pop removed 2026-08-03 — refuted)*, `TranslateY` (`CompositeOp.Add` float). Cheap for visible lines. Per-letter only with E3.
- **Glow:** colored blurred duplicate behind the crisp copy, `BlurSigma 0→glow` + `Opacity`, cropped by `ClipR`. Recommend per-word or single active-line glow (cost — verdict #7).
- **Edge-fade vignette:** `EdgeFadeSpec.Vertical(band)` on the lyrics viewport (first-class, `EdgeFade` layer).
- **Backdrop:** album art as full-bleed `ImageEl` behind an Acrylic node (blurs the canvas, verdict #9) or a cheap heavy wash via the **BlurHash LQIP** tile; slow `Rotation` loop; track-change `DrawImage` crossfade. Heavy ~100 blur is clamped to ~60px effective (verdict #9) — accept, or raise the acrylic downsample clamp.

### 6.5 Virtualization & scroll anchoring
Typical lyrics are 20–80 lines → **do NOT virtualize for v1**; use a plain translated column. A `Component` cannot reach `ScrollAnimator` for an eased `scrollTo`, and `ScrollEl`'s offset is Input-owned — so **translate the control's own content node** via `UseSpring(TranslateY)` (this also avoids the engine inertia fighting the auto-anchor). Focal-offset math: `target = -line.centerY + viewportH * PlayingLineTopOffsetFactor(0.5)`. For long transcripts only, `Virtual.Measured` (`IMeasuredVirtualLayout`, variable wrapped-line heights) — and then E5 to avoid the clock leak under re-mount.

### 6.6 Per-frame playback-position → playing-state pipeline
1. **Clock:** factor `SeekBar.cs:56-101` into a shared `UseSmoothPositionMs(bridge)` hook (anchor `_tickWallMs/_tickPosMs` on each ~1Hz `PositionMs`; `est = pos + (TickCount64 - anchor)`; clamp). One shared anchor keeps the seek bar and lyrics in lockstep.
2. **Tick:** `LyricsTicker.OnFrame()` reads the smooth ms → writes `_nowMs`.
3. **Resolve:** binary-search lines (next-StartMs boundary) → `_activeLine` (value-gated).
4. **Seed-on-change:** `_activeLine` change re-renders `LyricsView` → re-seeds scroll spring + per-line Scale/Blur/Opacity/Rotation targets (zero per-frame seeding).
5. **Continuous:** karaoke `ClipR` rides the `Drive` clock (register the media clock **once**, reuse the `drivenRef` for every line — avoids the §E5 leak).

### 6.7 Mount point & data source
- **Mount:** create `Wavee/Features/Player/LyricsView.cs` (the folder does not yet exist — a clean home). Surface it as a **toggle inside `NowPlayingView`** that replaces the center hero column (Spotify behavior) — `NowPlayingView` already embeds `SeekBar` via `Embed.Comp` under the `Expanded`-gated `NowPlayingLayer` (verified `NowPlayingView.cs:22-31,131`), so a `LyricsTicker` mounted here is automatically gated to run only while expanded. Add a "Lyrics" affordance to `PlayerBar`'s overflow.
- **Data source (blocker):** build `SpotifyLiveLyrics : ILyricsProvider` over `spclient color-lyrics/v2/track/{id}` (modeled on the existing live spclient client), map the response to `LyricsDocument`, and **register it in `Services.CreateReal`** replacing the `player` arg at `Services.cs:170`. Make it `SwitchableLyrics` (like `SwitchablePlayer`) so it goes live on connect without remounting views. Until then the UI builds against `FakeData.Lyrics` (`FakeData.cs:523-538`).

---

## 7. Risks, gaps & where 1:1 is hard (honest scorecard)

| # | Item | Verdict | Honest status | Mitigation / explicit compromise |
|---|---|---|---|---|
| R1 | Soft gradient karaoke wipe | blocked today | **Ship hard-edge `ClipR` wipe** (verdict #6 supported) for Phase 2 | E1 for the true soft sweep; hard edge is a real, crisp, synced approximation |
| R2 | Continuous per-syllable color ramp | blocked | discrete `SpanTextEl` recolor or 2-endpoint `BrushTransitionMs` flip | E2 (`AnimChannel.Color`); discrete recolor is visually acceptable at word granularity |
| R3 | Color crossfade *trigger* | supported but mis-named | **Use `BrushTransitionMs=300f`, NOT `Element.Transition`**; color must be an UNBOUND literal that differs across re-renders (verdicts #2/#5) | documented in the channel table; bound colors snap |
| R4 | Per-letter motion at line scale | partially-supported | per-glyph nodes work for a few glyphs, **un-budgeted at line scale, loses shaping** (verdict #3) | **Per-WORD motion (no engine work)** for v1; E3 only if per-letter is required |
| R5 | Progressive per-line blur | partially-supported | ~20 lines = ~40 full-canvas passes/frame (verdicts #1/#13) | **One shared backdrop/DoF blur + sharp active line** (budget-safe default); E6 only for independent per-line sigma |
| R6 | Glyph stroke/outline | blocked | no primitive (verdict #15); BL default `strokeWidth=0` | omit stroke (default) or faux multi-draw; E4 if required |
| R7 | Glyph glow/bloom | supported as halo | normal-alpha halo, not additive bloom (verdict #7) | per-word/active-line halo; E4 for true bloom |
| R8 | 3D fan / vertical CJK / fluid-fog-rain-snow | blocked | closed transform/op set (verdicts #10/#11/#16/#17/#18) | **defer — all default-off / backdrop-optional in BL**; 2D fan is full parity |
| R9 | Breathing / spectrum | driver missing | no audio FFT (verdict #12) | defer; `WaveeEqualizer` is decorative only |
| R10 | Heavy album backdrop blur | partially-supported | acrylic clamped ~60px effective; canvas-only source (verdict #9) | draw art full-bleed behind acrylic, or BlurHash LQIP; raise downsample clamp if needed |
| R11 | Driven clock leak under virtualization | confirmed leak | `Register` append-only (verdict #8) | **register ONE app-lifetime media clock, reuse `drivenRef`**; E5 only if per-line re-mount |
| R12 | No real lyrics feed | blocked | fake even under `--real-backend` (`Services.cs:170`) | build `SpotifyLiveLyrics` (backend, isolated); syllable timing may be line-only from the source → degrade to synthesized syllables |
| R13 | Remote-device sync drift | needs-verification | `PositionMs` is server-clock-aged in viewer mode | suppress *syllable* karaoke unless `WeAreActive`; line-level stays fine |
| R14 | Per-frame re-seed trips alloc gate | confirmed | seeds allocate `Keyframe[]` (verdict #8) | **seed-on-deps + Drive/ticker for continuous** — the load-bearing discipline of §6.3 |

**Where 1:1 is genuinely hard (no compromise reaches pixel-identical):** the soft continuous gradient wipe (R1) and continuous per-syllable color ramps (R2) without E1/E2; per-letter motion across a full line without E3; text stroke (R6) without E4. Everything else reaches full or close, or is a BetterLyrics optional toggle.

---

## 8. Phased implementation roadmap

Each phase is independently shippable. Engine gaps that unblock multiple features are front-loaded *as optional accelerators*, but **Phase 1–2 deliver a genuinely good synced view with zero engine changes** — so the project de-risks immediately and the engine work is incremental polish, not a prerequisite.

### Phase 0 — Real data source + clock plumbing (backend, no UI risk)
- `SpotifyLiveLyrics : ILyricsProvider` over `color-lyrics/v2/track/{id}`; map to `LyricsDocument`; register in `Services.CreateReal` (replace `player` at `Services.cs:170`); wrap in `SwitchableLyrics`.
- Additive model enrichment (`EndMs`/`SecondaryText`/`TertiaryText`/`AgentId`/`IsWordByWord`) where the feed supplies them.
- Factor `UseSmoothPositionMs(bridge)` from `SeekBar.cs:56-101`.
- **Acceptance:** `dotnet build src/FluentGpu.slnx` clean; `--real-backend` returns real timed lines for a track with lyrics; seek bar + lyrics share one clock anchor (no drift).

### Phase 1 — Line-level synced view (ZERO engine work)
- `Wavee/Features/Player/LyricsView.cs` + `LyricsTicker`; mount as a toggle in `NowPlayingView` (gated on `Expanded && IsPlaying`).
- Render VM, predicates (`GetIsPlaying`/`GetPlayProgress` + NaN guard), current-line resolver (next-StartMs), EndMs derivation + 350ms padding.
- Auto-scroll via `UseSpring(TranslateY)` on own column; distance-scaled per-line `ScaleX/Y`, `Opacity`, `Rotation` (2D fan), `BlurSigma` **as one shared backdrop blur** (R5 compromise); `EdgeFade` vignette; blurred-art backdrop (BlurHash/acrylic).
- Palette → line colors via `bridge.TrackPalette` + luminance; active↔inactive color via `BrushTransitionMs=300f` literals (R3); `Marquee` for overflow.
- **Acceptance:** build clean; on the fake's 4 lines and a real track, the active line tracks playback, scrolls smoothly, neighbours fade/scale/blur, colors crossfade; GPU idles when paused/collapsed; `dotnet run --project src/FluentGpu.VerticalSlice` → ALL CHECKS PASSED (zero-alloc phases 6–13 green — the lyrics view must add **no** per-frame allocation: seed-on-deps + value-gated ticker).

### Phase 2 — Word-level karaoke (hard-edge) + per-word motion (ZERO engine work)
- Two-stacked-copies + `ClipR` `Drive(mediaClockRef, lineStart, lineEnd)` on the played copy (register the clock **once**, reuse `drivenRef` — R11). Region x from `GetRangeRects`.
- ~~Per-word scale-pop (`UseSpring` overshoot)~~ *(superseded 2026-08-03 — refuted, removed)* + float (`Keyframes` `TranslateY`, `CompositeOp.Add`), long-syllable (≥700ms) gating; per-word halo glow (active line only).
- **Acceptance:** build clean; karaoke wipe sweeps in sync across seek/pause/buffer (hard edge, honestly noted); words ~~pop~~/float on syllable onset *(pop removed 2026-08-03 — refuted; float only)*; VerticalSlice green (no phase 6–13 alloc; per-syllable seeds at the reconcile edge only).

### Phase 3 — Engine accelerators (unlock the signature; each independently shippable)
- **3a — E1 (soft gradient glyph wipe):** `DrawGlyphRunGradient` op + PS. **Acceptance:** new VerticalSlice gate asserts the op/gradient stops headlessly + a `--screenshot` soft-edge diff; Phase-2 karaoke swaps hard→soft with no control rewrite.
- **3b — E2 (`AnimChannel.Color` + per-instance glyph color):** **Acceptance:** VerticalSlice gate exercises a Color row under the alloc tripwire (zero phase 6–13 alloc); continuous per-syllable color ramp replaces the discrete flip.
- **3c — E5 (index-based media clock) + E6 (per-rect blur)** *only if* virtualization / independent per-line blur are pursued. **Acceptance:** VerticalSlice gate reconciles a re-mounting virtual lyric list with bounded clock registrations; blur gate asserts line-rect (not full-canvas) RT.
- **3d — E3 (per-glyph transform)** *only if* per-letter motion is required. **Acceptance:** new gate reconciles+lays out a 60–100-glyph run with per-glyph transforms within the reconcile-edge budget (the gate that verdict #3 found missing).

### Phase 4 — Out-of-core (defer; matches BetterLyrics' default-off toggles)
E4 (stroke/bloom), E7 (procedural-shader backdrops via the vendored ComputeSharp transpiler, path tessellator for spectrum, 3D perspective layer, vertical CJK, audio FFT). Each gated behind its own VerticalSlice/`--screenshot` check; none blocks the shipping synced view.

---

**Bottom line for the implementer:** Build Phases 0–2 first — they deliver a faithful, synced, beautiful lyrics view with **no engine changes** and at close-to-full parity for everything BetterLyrics ships by default. Treat Phase 3 (E1, E2, optionally E3) as the bounded, well-scoped text-layer work that closes the last honest gap to the BetterLyrics *signature*. Do not promise 1:1 on the soft wipe, continuous color ramp, per-letter motion, stroke, 3D, vertical, or procedural backdrops until their named §5 primitive lands — and front-load the real lyrics feed (Phase 0), because without it everything else renders four fake lines.

Key file:line anchors for the implementer: model `Playback.cs:76-83`; provider wiring to fix `Services.cs:170`; fake data `FakeData.cs:523-538`; mount `NowPlayingView.cs:22-31,131`; clock idiom `SeekBar.cs:56-101`; bridge signals `PlaybackBridge.cs:27-60`; channels `AnimTypes.cs:18`; driven clock `AnimScheduler.Timeline.cs:52-81`; draw ops `DrawList.cs:7-20`; blur path `SceneRecorder.cs:317,336`; async load `Component.cs:56-60`. BetterLyrics authority: `LyricsAnimator.cs:17-383`, `BaseRenderLyrics.cs:5-17`, `LyricsSynchronizer.cs:16-74`, `LyricsLineRendererBase.cs:79-272`, `RenderLyricsChar.cs:11-58`, `NowPlayingCanvas.xaml.cs:1048-1200`.