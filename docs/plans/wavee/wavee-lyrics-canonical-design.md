<!--
Wavee Lyrics - CANONICAL technical design (right-sidebar).
Authored 2026-06-29. This is the single canonical design for the Wavee lyrics feature.

It supersedes the *mount-point* decision of docs/betterlyrics-parity-plan.md (which assumed a
center-column takeover) and re-scopes that plan to "panel mode". The two companion docs remain the
detailed authority for their domains and are referenced, not restated:

  - docs/betterlyrics-parity-plan.md          -> OWNS: BetterLyrics feature inventory, the verified
                                                 FluentGpu engine facts, the per-feature parity table,
                                                 and the full E1-E7 engine-work specs (src/ homes + approach).
  - docs/lyrics-aggregator-reranker-plan.md   -> OWNS: the data pipeline - multi-provider aggregator,
                                                 the content/timing reranker, provider matrix, and wiring.

This doc OWNS: the product definition (a WaveeMusic-style right rail), the shell + panel integration,
the panel-scoped rendering/animation design, the FINAL iterated engine-changes verdict, and the
integrated roadmap. When this doc and a companion disagree on the items above, this doc wins.

Grounding (file:line) is from a 2026-06-29 two-scout investigation of C:\WAVEE\WaveeMusic (the target UX)
and C:\WAVEE\fluent-gpu (the current Wavee app + engine). Claims marked [verify] are not yet confirmed
against source and gate a small decision.
-->

# Wavee Lyrics - Canonical Technical Design (Right Sidebar)

**Goal:** a right sidebar that shows synced lyrics, modeled on the WaveeMusic "Now Playing" right panel - a tabbed rail that slides in from the right, with a Lyrics tab that tracks playback line-by-line (and word-by-word when the source has syllable timing). Fed by the Wavee lyrics aggregator/reranker. Built on FluentGpu primitives with **no engine changes for v1**.

**Status:** decision-ready. Implement Phase 0->1 first; they ship a faithful WaveeMusic-class lyrics panel with zero engine work.

---

## 0. Decisions locked

| # | Decision | Rationale |
|---|---|---|
| D1 | **Right rail, not a center takeover.** Mount as a third child of the shell's sidebar+content row, *pushing* the content card (mirror the left sidebar). | The ask is explicitly "a right sidebar like WaveeMusic." Supersedes parity-plan SS6.7. |
| D2 | **Tabbed rail** (Lyrics first; Queue/Details are future tabs), default **300px**, min 200 / max 500, drag-resizable. | Exact WaveeMusic shape (`ShellPage.xaml:280-319`, `RightPanelView.xaml:24-25`). |
| D3 | **Panel mode = the tame configuration.** No procedural backdrops, 3D fan, vertical CJK, or audio spectrum. | WaveeMusic disables all of these in panel mode (`LyricsCanvasHost.xaml.cs:175-186`): pure-color overlay at 78%, every fluid/cover/spectrum/fog/rain/snow overlay off. This is the same "default-off" set the parity plan flagged. |
| D4 | **v1 needs zero engine changes.** | Line-level + hard-edge word karaoke + neighbour fade/scale + edge fade + click-to-seek are all shipped, verified primitives (parity plan SS3.1). |
| D5 | **Independent of the existing `Expanded` fullscreen overlay.** The rail is a new surface with its own open/mode/width state. | `Expanded` (`NowPlayingView` via `NowPlayingLayer`, gated `PlaybackBridge.Expanded`) is the fullscreen takeover; the rail is the Spotify-style side panel. Different affordances. |
| D6 | **Data via the aggregator/reranker** (companion doc), behind `SwitchableLyrics`. | Real synced feed is the one hard blocker; the companion doc resolves it. |
| D7 | **One new engine question: E8 (manual-scroll override).** v1 ships **auto-follow only**; the WaveeMusic "snap to current line" pill is v1.1. | A Component cannot eased-scroll a `ScrollEl` (parity plan SS6.5); the snap-back pill needs either a programmatic eased scroll-to or component wheel/drag access. Scoped, not hand-waved. |

---

## 1. Target UX (mirror of the WaveeMusic right panel)

What WaveeMusic does, distilled to the parts we replicate (citations are WaveeMusic source):

- **A tabbed right rail.** Tabs: Queue / Lyrics / Friends / Details (`RightPanelMode.cs:3-15`). We ship **Lyrics** first and design the rail so Queue/Details slot in later. Tab header is a scrollable segmented strip with a pop-out/detach button (`RightPanelTabPager.xaml:20-124`) - **detach is out of scope for Wavee v1.**
- **Toggle + close-if-same.** A toolbar toggle bound to `IsRightPanelOpen`; `ToggleRightPanel(mode)` closes the rail if the same mode is already showing, else switches mode and opens (`ShellViewModel.cs:835-848`). Visibility = `IsRightPanelOpen && !IsRightPanelDetached` (`:236-237`).
- **Slide + fade in.** Show = Opacity 0->1 + TranslateX 40->0 over **250ms**; Hide = reverse over **200ms** (`RightPanelView.xaml:82-89`). Width default **300**, min **200**, max **500**, resizable via a 4px left-edge gripper (`RightPanelView.xaml:24-25,128-144`).
- **Lyrics tab internals** (`LyricsCanvasHost.xaml`): a deferred-loaded canvas (`x:Load=False` until first shown), plus overlays - a **"snap to current line" pill** shown only when the user scrolls away (`:26-36`), a **loading ProgressRing**, a **9-bar loading shimmer** that mimics lyric layout (spacing 18; bar heights 20, with 28 for the centered active line; opacities 0.32/0.46/0.62/0.8 rising to 1.0 at the active line - `:52-70`), and a **no-lyrics fallback** text. (WaveeMusic's AI panel is out of scope.)
- **Lyrics line treatment** (shared `LyricsAnimator`/`LyricsStyleSettings`): vertical stack, active line parked at **50%** of height, off-current scale **0.75** vs active **1.0**, unplayed opacity **30%** vs played **100%**, phonetic/translation **60%**; active line **24px Bold**, neighbours render at ~18px (0.75x), secondary text 12px. Click a line to seek. Word-by-word when syllable data is present.
- **Position cadence.** A **250ms** timer snapshots `IPlaybackStateService.Position` and the canvas interpolates between ticks for smooth motion (`LyricsCanvasHost.xaml.cs:53,80-81,193-196`).
- **Panel chrome.** Card background, 1px border, 8px corner radius; composition shadow (blur 32, offset (4,-2), opacity 0.3 - `ShellPage.xaml:306-308`); a **140px bottom fade** over the content (`RightPanelView.xaml:170-175`); acrylic tab header; a **pure-color overlay at 78%** behind the lyrics in panel mode.

The crucial takeaway: **WaveeMusic's lyrics tab is the same Win2D engine as BetterLyrics, run in its calm, effects-off configuration.** That is exactly the "Phase 1-2, zero-engine-work" target the parity plan already proved reachable.

---

## 2. Shell integration (new)

### 2.1 Where it mounts
The current shell (`WaveeShell.cs:160-327`) is a 4-row column:

```
Row 1  TitleBar        (56px)            WaveeShell.cs:188-193
Row 2  ShellToolbar    (~48px)           :194
Row 3  Sidebar+Content (Grow=1)          :195-294   <-- a ZStack: a row [left sidebar, content card] + resize-grip overlay
Row 4  PlayerBar       (72px)            :295
```

Row 3's inner row (`Direction=0`) has two children today: the left sidebar pane (56px compact / 240-460px expanded) and the content card (`Grow=1, Shrink=1, MinHeight=0`). **Add a third child after the content card** - the right rail:

```
[ left sidebar ]  [ content card  Grow=1 Shrink=1 ]  [ RIGHT RAIL  Shrink=0  Width=railWidth ]
```

The rail is `Shrink=0` (fixed width, never squeezed); the content card already carries `Shrink=1, MinHeight=0` and reflows to make room. This is the WaveeMusic behavior (a Grid Auto-column that pushes the content) and the same tiling the left sidebar uses - **push, not overlay.**

### 2.2 Open / close animation (zero engine work)
Mirror the left sidebar's `SidebarReflow`:
- **Width**: animate the rail's `Width` 0 <-> 300 via a `LayoutTransition` on `Size` (Spring/Tween ~250ms, `MotionTok.ControlNormal`). The content card reflows smoothly - exactly what `SidebarReflow` does for the left pane (`WaveeSidebar.cs:52-53`).
- **Content entrance**: inside the rail, fade + slide the content with `this.UseEntrance(offsetPx)` or `UseTransition(Opacity,0,1,250) + UseTransition(TranslateX, +24, 0, 250)` - matching WaveeMusic's Opacity 0->1 + TranslateX 40->0 at 250ms (`RightPanelView.xaml:82-89`).

Transient note: animating width re-wraps the lyric text during the 250ms open. That is a bounded, one-shot cost on ~20-80 short lines and is identical to what the left sidebar already does each toggle - **accepted.** (If ever a concern, the fallback is snap-width + slide-content-only.)

### 2.3 New UI state
Add rail state alongside the existing playback signals. Two clean homes; pick one:
- **(Recommended)** a small `ShellUi` context (new) holding `RailOpen : Signal<bool>`, `RailMode : Signal<RailMode>` (`Lyrics`/`Queue`/`Details`), `RailWidth : FloatSignal`. Keeps `PlaybackBridge` about *playback*, not chrome.
- Or hang them off `PlaybackBridge` next to `Expanded` (`PlaybackBridge.cs:60`) for expedience.

Semantics copy WaveeMusic's `ToggleRightPanel(mode)`: if `RailOpen && RailMode==mode` -> close; else set mode + open.

### 2.4 The toggle
Add a control cluster to the **PlayerBar right section** (`PlayerBar.cs:103-150`) - Spotify-faithful placement: a **"Lyrics"** button (mic glyph) and optionally a **"Now playing view"** / **"Queue"** button. "Lyrics" calls `ToggleRail(RailMode.Lyrics)`. (WaveeMusic instead puts the toggle in the top toolbar; either works - the ShellToolbar at `WaveeShell.cs:194` is the alternative home.) The PlayerBar already isolates high-frequency updates into sub-components, so adding low-frequency toggle buttons costs nothing.

---

## 3. Panel architecture (new; mirrors WaveeMusic)

```
RightRail : Component                      // Wavee/Features/Player/RightRail.cs
  - reads ShellUi (RailOpen, RailMode, RailWidth)
  - if !RailOpen -> render nothing (rail width animates to 0)
  - else: a column
       [ TabHeader ]                       // segmented strip: Lyrics (+ Queue/Details later)
       [ Content region  Grow=1 MinHeight=0 ]
            switch RailMode -> Embed.Comp(() => new LyricsView(...))   // lazy: only the active tab mounts
       [ bottom EdgeFade 140px ]
  - resize grip on the LEFT edge (4px) -> drags RailWidth in [200,500]   (mirror SidebarResizeGrip)
```

- **Tab model.** `enum RailMode { Lyrics, Queue, Details }`. Only the active tab's component is mounted (the inactive tabs are not in the tree) - the FluentGpu equivalent of WaveeMusic's `x:Load=False` deferral. Lyrics is the only tab implemented in v1; the header can show just "Lyrics" until others land.
- **Chrome mapping** (FluentGpu primitives):
  - Card: a `BoxEl` with `Fill = WaveeColors.Sidebar`, `WaveeRadius.Card` corners, `Elevation.Card` shadow (the content card already uses these).
  - Bottom fade: an `EdgeFade` layer (first-class per-edge feather) sized ~140px, or `ScrollEl.EdgeCues` on the lyrics scroller.
  - Tab header: an Acrylic node behind the segmented buttons (matches WaveeMusic's frosted header).
  - Lyrics background overlay: a translucent `BoxEl` (~78% panel color) behind the lyric column - the panel-mode "pure color overlay."
- **Lyrics tab states** (inside `LyricsView`): `loading` -> the 9-bar shimmer (build from a column of `BoxEl`s with an animated `Opacity`/gradient sweep; the left sidebar already uses a skeleton on async load); `empty` -> centered "No lyrics available"; `loaded` -> the synced view (SS4).

---

## 4. Lyrics view - rendering & animation (panel-scoped)

This refines parity-plan SS6 for panel width and **corrects the shell scout's naive sketch**: do **not** read `PositionMs.Value` inside `LyricsView.Render()` - that re-renders the whole panel at tick frequency and breaks the zero-alloc discipline. Use the two-component split below.

### 4.1 Component split (the load-bearing discipline)
```
LyricsView : Component                     // re-renders only on LOW-frequency change (track, activeLine, doc)
  var ui  = UseContext(ShellUi.Slot)
  var b   = UseContext(PlaybackBridge.Slot)
  var svc = UseContext(Services.Slot)
  var doc = UseAsyncResource(ct => svc.Lyrics.GetLyricsAsync(track.Id, ct), seed:null, deps:[track.Id])
  var vm  = UseMemo(() => BuildVm(doc, settings), [doc])          // render-VM: see parity-plan SS6.1
  _activeLine : Signal<int>                                       // resolver output (value-gated)
  scroll      : UseSpring(TranslateY, -vm.Lines[_activeLine].centerY + band, deps:[_activeLine])
  renders: backdrop overlay + edge-fade viewport + the translated line column
  mounts:  LyricsTicker  ONLY while (ui.RailOpen && ui.RailMode==Lyrics && b.IsPlaying)

LyricsTicker : ReactiveComponent           // 0x0, never re-renders; gated mount
  Setup(): t = UseContextSignal(FrameClock.Tick);
           UseSignalEffect(() => { _ = t.Value; Owner.OnFrame(); });
           return BoxEl{Width=0,Height=0,HitTestVisible=false};
```
`OnFrame()` (zero-alloc): compute smooth `nowMs`, resolve the active line, write `_activeLine` **value-gated** (unchanged frame = true no-op -> the host elides the present). Mount-gating on `RailOpen && Lyrics && IsPlaying` keeps the GPU idle at rest - the `SeekTicker` precedent (parity plan SS6.2). This replaces WaveeMusic's 250ms `DispatcherQueueTimer`; FluentGpu drives it off `FrameClock.Tick` and the smooth clock comes from the shared `UseSmoothPositionMs(bridge)` hook factored out of `SeekBar.cs:56-101` (so the seek bar and lyrics share one clock anchor - no drift).

### 4.2 Auto-follow scroll (v1) + manual override (v1.1)
- **Auto-follow:** translate the rail's own content column via `UseSpring(TranslateY, target, deps:[_activeLine])`, `target = -line.centerY + viewportH*0.5`. Do **not** use `ScrollEl`'s offset (Input-owned; a Component can't eased-scroll it) - own the node (parity plan SS6.5). Seed only on `_activeLine` change (springs retarget velocity-continuously, so it stays smooth without per-frame seeding).
- **Manual override + "snap to line" pill (v1.1 = E8):** WaveeMusic shows the pill when the user scrolls away and re-follows on tap. Implementing this needs either (a) a programmatic eased `ScrollEl.ScrollTo(offset, easing)` exposed to components, or (b) component-level wheel/drag handlers on the owned column to drive an offset signal + suppress the auto-follow spring while interacting. [verify] whether `InputDispatcher` exposes pointer/wheel handlers to components; if not, that affordance is E8. **v1 ships auto-follow only** (no pill) - a complete, correct experience.

### 4.3 Per-line treatment at panel width
| Aspect | Value (matches WaveeMusic panel) | FluentGpu binding |
|---|---|---|
| Active focal position | 50% of viewport height | scroll target math above |
| Active vs neighbour scale | 1.0 vs 0.75 | `ScaleX`+`ScaleY`, origin = line center, seed on `_activeLine` |
| Played vs unplayed opacity | 100% vs 30%; secondary 60% | `Opacity` per stacked child |
| Active <-> inactive color | palette-driven | `TextEl.Color` literal + **`BrushTransitionMs=300f`** (NOT `Element.Transition`; color must be an unbound literal that differs across renders - parity plan R3) |
| Neighbour depth blur | optional | **v1: omit** (scale+opacity already read as depth). If wanted, ONE shared blur, not per-line (parity plan R5/E6). |
| Typography | active 24 Bold / neighbour ~18 / secondary 12 | `TextEl{Size,Weight}`, palette colors |
| Wrapping | wrap at ~rail width - 24px padding | `TextEl.Wrap = Wrap`, width from `RailWidth` |
| 2D fan | off by default (BL/WM default) | `Rotation` if ever enabled |

### 4.4 Word-by-word (when the source has syllable timing)
- **v1 (hard-edge karaoke, zero engine work):** two stacked color copies per active line (unplayed beneath, played on top); the played copy carries `AnimChannel.ClipR`, driven by the media clock via `Drive(mediaClockRef, lineStart, lineEnd)` so the recorder sweeps a rectangular scissor across its glyphs. Build `ClipR` keyframes from `IFontSystem.GetRangeRects`. Register **one** app-lifetime media clock and reuse the `drivenRef` for every line (avoids the append-only `DrivenClockTable` leak - parity plan R11). Honest compromise: hard edge, not WaveeMusic's soft gradient (that's E1).
- **Per-word motion (zero engine work):** one `BoxEl`+`TextEl` per word in a flex row; scale-pop via `UseSpring(Scale, ExpressiveSpring)` overshoot and float via `UseKeyframes(TranslateY, ..., CompositeOp.Add)`, gated to syllables >=700ms (BL's threshold). Per-*letter* motion is E3 - **not** needed at panel width; per-word reads correctly.
- Predicates and char-timing synthesis port verbatim from the parity plan (`GetIsPlaying`/`GetPlayProgress` + NaN guard; uniform char timing within a syllable).

### 4.5 Click-to-seek, marquee, backdrop
- **Click-to-seek:** each line `BoxEl` is hit-testable -> `b.Player.SeekAsync(line.StartMs)`.
- **Marquee:** the shipped `Marquee` control for any non-wrapping overflow line.
- **Backdrop:** v1 uses the **pure-color overlay** (WaveeMusic panel default), not a blurred album backdrop - cheaper and faithful. (The blurred-art backdrop is a parity-plan Phase-1 option for the *fullscreen* `Expanded` view, not the rail.)

The full BetterLyrics-transition -> FluentGpu-channel mapping (timing, triggers, the seed-on-deps vs Drive discipline) is parity-plan SS6.3 - **that table is the authority; do not restate it in code reviews.**

---

## 5. Data pipeline (summary; companion doc owns it)

The rail reads `Services.Lyrics : ILyricsProvider` (`Services.cs:45`) via `UseAsyncResource(track.Id)`. Behind that interface sits the aggregator/reranker (companion doc `lyrics-aggregator-reranker-plan.md`), in brief:

- **Fan out** to AMLL TTML DB (by Spotify id, word-by-word, clean), Spotify-native color-lyrics (line, the trusted reference), and LRCLIB (line, clean) by default; QQ/Kugou/NetEase/Musixmatch/Apple opt-in.
- **Normalize** every format to `LyricsDocument`, trim headers/credits, consume `[offset:]`.
- **Rerank**: score each candidate against the Spotify-native reference (text agreement + median timing offset correction + sync-tier preference), so the *best* lyric wins - not the first. This is Wavee's structural advantage over BetterLyrics' metadata-only first-hit selection.
- **Wire** via `SwitchableLyrics` so the provider goes live after login without rebuilding the UI tree; resolve the full `Track` (for ISRC + reranker metadata) from the store, not just the trackId.

The lyrics **view** does not care whether the backend is fake or live - it always reads `svc.Lyrics`. Until the live provider lands, the rail builds against the fake's lines (`FakeData.cs:523-538`).

---

## 6. Engine changes - FINAL (iterated)

The right-rail / panel-mode decision **shrinks** the engine work the parity plan enumerated. Iterated verdict:

### 6.1 v1 sidebar: ZERO engine changes
Everything in SS2-SS4 (line-level view, auto-follow, neighbour fade/scale, color crossfade, edge fade, click-to-seek, hard-edge word karaoke, per-word motion, the rail open/close) uses shipped, verified primitives. Build it now.

### 6.2 Dropped from scope by this design
| Item | Why dropped |
|---|---|
| **E7** (procedural fluid/fog/rain/snow, 3D fan, vertical CJK, audio FFT) | WaveeMusic disables all of these in panel mode (`LyricsCanvasHost.xaml.cs:175-186`). Not on screen in the rail - **out of scope entirely.** |
| **E5** (index-based media clock) | No virtualization at panel scale (20-80 lines). Reuse one app-lifetime clock. |
| **E6** (per-rect blur) | v1 uses no per-line blur; if added, one shared blur. |
| **E4** (glyph stroke/bloom) | Default strokeWidth=0 in BL/WM; glow approximable. Defer. |

### 6.3 Optional fidelity tier (only to reach pixel-perfect word-by-word like WaveeMusic's engine)
Ordered by leverage. None blocks v1; each is independently shippable behind its own VerticalSlice gate. Full specs (src/ homes honoring TerraFX-free `Controls` + zero-alloc phases 6-13, and the GPU approach) live in **parity-plan SS5** - that doc is the authority; below is the iterated priority.

1. **E1 - soft, clock-driven glyph fill-wipe (L).** The single highest-leverage item: it turns the v1 hard-edge `ClipR` karaoke into WaveeMusic's soft sweeping gradient. New `DrawGlyphRunGradient` op (Engine) + glyph PS (Windows); drive the split by the playback-ms `Drive` path. Reuses `GetRangeRects`. When this lands, Phase-2 karaoke swaps hard->soft with **no control rewrite.**
2. **E2 - `AnimChannel.Color` + per-instance glyph color (L).** Continuous per-syllable color ramps and the active-line played/unplayed split. Lower priority than E1: at panel width the `BrushTransitionMs` active<->inactive crossfade covers the common case; E2 only matters for continuous ramps.
3. **E3 - per-glyph transform addressing (M-XL).** Per-*letter* float/scale at line scale. **Defer** - per-word motion (zero engine work) is faithful at 300px. Commit only if per-letter is an explicit requirement.

### 6.4 New item this design introduces
- **E8 - manual-scroll override / programmatic eased scroll (S-M).** For the WaveeMusic "snap to current line" pill (SS4.2). Either expose `ScrollEl.ScrollTo(offset, easing)` to components, or surface component wheel/drag handlers. **v1.1**, after the auto-follow core ships. [verify] current input-handler availability first - it may already be reachable.

**Headline:** the rail makes the engine ask essentially *"E1 if/when we want pixel-perfect word-by-word; otherwise nothing."*

---

## 7. Roadmap - integrated (shell + render + data)

Each phase is independently shippable and gated by: `dotnet build` clean + `dotnet run --project src/FluentGpu.VerticalSlice` -> ALL CHECKS PASSED (zero-alloc phases 6-13 green - the lyrics view must add **no** per-frame allocation) + `--screenshot` for fidelity.

### Phase 0 - Foundations (backend + shell scaffold, no UI risk)
- **Data:** companion-doc Phase 0 - model enrichment (`LyricsDocument`/`LyricLine` end-ms/translation/word-by-word; `Track.Isrc`/`HasSpotifyLyrics`), `SwitchableLyrics`, ISRC/`has_lyrics` projection.
- **Shell:** `ShellUi` context (`RailOpen`/`RailMode`/`RailWidth`); add the rail as the third child of `WaveeShell.cs:195-294` (renders nothing while closed); the PlayerBar toggle cluster; the open/close width+entrance animation (mirror `SidebarReflow`).
- **Clock:** factor `UseSmoothPositionMs(bridge)` from `SeekBar.cs:56-101`.
- **Acceptance:** rail opens/closes smoothly and pushes the content card; toggle + close-if-same work; fake lyrics still resolve; no per-frame alloc.

### Phase 1 - Lyrics tab, line-level (zero engine work)
- `Wavee/Features/Player/LyricsView.cs` + `LyricsTicker` (gated mount); render VM; current-line resolver; auto-follow `UseSpring(TranslateY)`; neighbour scale/opacity; color crossfade (`BrushTransitionMs`); edge-fade viewport; pure-color overlay; click-to-seek; marquee; **loading shimmer + no-lyrics + loaded** states.
- Wire to fake data first, then real via the aggregator.
- **Acceptance:** active line tracks playback and scrolls smoothly; neighbours fade/scale; colors crossfade; GPU idles when the rail is closed or paused; VerticalSlice green.

### Phase 2 - Word-level + manual scroll
- Hard-edge karaoke (`ClipR` `Drive`, one shared clock) + per-word pop/float (>=700ms gating).
- Manual-scroll override + "snap to line" pill (**E8** if input handlers aren't already reachable).
- **Acceptance:** karaoke wipe stays in sync across seek/pause; words pop/float on onset; manual scroll + snap-back work; VerticalSlice green.

### Phase 3 - Fidelity + more tabs (optional)
- **3a - E1** (soft gradient wipe): swap hard->soft, no control rewrite. **3b - E2** (color channel). **3c - E3** only if per-letter is required.
- Additional rail tabs (Queue reusing the `NowPlayingView` queue-rail pattern at `NowPlayingView.cs:177-208`; Details).
- **Acceptance:** each engine item has a VerticalSlice + `--screenshot` gate; new tabs mount lazily.

---

## 8. Risk scorecard (rail-specific + carried)

| # | Item | Status | Mitigation |
|---|---|---|---|
| K1 | Rail width-reflow re-wraps text during the 250ms open | low (transient, bounded; left sidebar does the same) | accept; fallback = snap-width + slide-content |
| K2 | Manual scroll + snap pill needs an engine affordance | **verify** | v1 = auto-follow only; E8 for the pill once input handlers are confirmed |
| K3 | Ticker re-renders the panel per tick (the naive sketch) | designed out | isolated `LyricsTicker` + value-gated `_activeLine`; never read `PositionMs.Value` in `Render()` |
| K4 | Soft gradient karaoke / continuous color ramp | gap | v1 hard-edge `ClipR` + `BrushTransitionMs`; E1/E2 close it |
| K5 | No real lyrics feed | blocked until companion Phase 0-1 | aggregator/reranker behind `SwitchableLyrics`; build against fake first |
| K6 | Driven-clock leak under any future line virtualization | confirmed (append-only table) | register one app-lifetime media clock, reuse `drivenRef`; E5 only if lines re-mount |
| K7 | Panel-width typography/wrapping differs from fullscreen | low | size from `RailWidth`; wrap on; active 24 / neighbour 18 per WM |
| K8 | Remote-device position drift | needs-verify | suppress *syllable* karaoke unless we're the active device; line-level is fine |

**Where 1:1 is genuinely hard:** the soft continuous gradient wipe (E1) and continuous per-syllable color ramps (E2). Everything else in the WaveeMusic *panel* configuration reaches full or close with no engine work.

---

## 9. File map (where to change what)

| Concern | File(s) |
|---|---|
| Rail mount (3rd child of Row-3 row) | `Wavee/Features/Shell/WaveeShell.cs:195-294` |
| Rail UI state | new `Wavee/App/ShellUi.cs` (or extend `PlaybackBridge.cs`) |
| Toggle cluster | `Wavee/Features/Shell/PlayerBar.cs:103-150` |
| Rail container + tabs + chrome | new `Wavee/Features/Player/RightRail.cs` |
| Lyrics view + ticker | new `Wavee/Features/Player/LyricsView.cs`, `LyricsTicker.cs` |
| Smooth clock hook | factor from `Wavee/Features/Shell/SeekBar.cs:56-101` |
| Playback signals | `Wavee/App/PlaybackBridge.cs:14-126` (CurrentTrack/PositionMs/DurationMs/IsPlaying/TrackPalette/Queue) |
| Lyrics provider seam + wiring | `Wavee/App/Services.cs:45,170` (+ companion doc `Wavee/Backend/Lyrics/`) |
| Animation hooks | `src/FluentGpu.Engine/Hooks/RenderContext.cs` (UseSpring:540 / UseTransition:543 / UseKeyframes:546 / UseDrivenAnimation:549), `Animation/AnimTypes.cs:18` |
| Text + scroll | `src/FluentGpu.Engine/Scene/Element.cs` (TextEl:408 / SpanTextEl:513 / ScrollEl:602-682), `Ui.ScrollView` (`Factories.cs:35-36`) |
| Engine items E1-E3 | per parity-plan SS5 (Engine ops in `src/FluentGpu.Engine/Render/DrawList.cs`; PS in `src/FluentGpu.Windows/D3D12/GlyphRenderer.cs`) |

**Companion authorities:** rendering facts + feature inventory + E-specs -> `docs/betterlyrics-parity-plan.md`; data pipeline + reranker -> `docs/lyrics-aggregator-reranker-plan.md`. WaveeMusic UX reference -> `C:\WAVEE\WaveeMusic` (`ShellPage.xaml`, `RightPanelView.xaml`, `LyricsCanvasHost.xaml`, `ShellViewModel.cs`).

---

**Bottom line:** build the rail (Phase 0) and the line-level Lyrics tab (Phase 1) - that is a faithful WaveeMusic-class lyrics panel with **no engine changes**. Add hard-edge word karaoke + manual scroll (Phase 2). Treat E1 (soft gradient wipe) as the one high-value engine item that, when it lands, upgrades the karaoke to pixel-perfect with no control rewrite. Do not promise the soft wipe, continuous color ramps, per-letter motion, or any procedural backdrop until their named primitive ships - and front-load the real lyrics feed, because without it the rail renders four fake lines.
