# Scrolling in Wavee

Wavee owns **declarations** — which viewport, which restore key, which effects, which handle — and the engine owns
**every mechanism**: motion curves, posing at present time, virtualization, measured anchoring, pacing. Engine canon:
`..\fluent-gpu\docs\design\subsystems\scroll.md`; engine skill: `..\fluent-gpu\.claude\skills\fluentgpu-scroll\`;
developer guide here: `docs/guide/scrolling.md`.

## The rule that matters most

**No app-side workarounds for engine scroll behaviour.** A jump, a blank row, a lagging touchpad, a floaty fling, a
sticky header that shimmers — these are engine defects. Do not add an app timer, a "wait until the list settles"
latch, a manual offset nudge, a second follow state machine, a throttle "while scrolling", or a hand-written transform
on a content node. Reproduce it (the engine's Scroll Lab, or Diagnostics ▸ Scroll at Trace + the CSV), then fix it in
`..\fluent-gpu` or file the engine gap. The rework deleted exactly those app workarounds (`Track.Table.HeroRoot`, the
lyrics' bespoke follow glide, `Diagnostics.ScrollTrace.cs`) — don't grow them back. (Same stance as the memory note
"Pages simple, engine patched".)

## A scroller: one handle, one restore key

```csharp
readonly ScrollHandle _scroll = new();          // ONE per component instance, created once (props freeze at mount)

Element scroll = ScrollView(body) with
{
    Key = "artist-scroll:" + routeKey,           // a new identity remounts (and restores its own key)
    ScrollKey = UseContext(Shell.PageScrollScope) + routeKey,
    Handle = _scroll,                            // optional — supply it when something outside the subtree drives it
    Grow = 1f,
};
```

- **`ScrollKey`** is a stable per-CONTENT identity; the engine saves the shown offset under it on unmount and restores
  it on the next mount (latched until the extent can hold it — no scroll-to-top flash). The engine keys by the string
  alone, so every content page composes **`Shell.PageScrollScope`** (`Shell/Shell.cs` — `"tab<n>/"`, provided per
  keep-alive slot in `Shell.UI.cs`) onto its key: the same album open in two tabs keeps two offsets. One-per-window
  surfaces (rails, sidebar, dialogs, tool pages) keep bare keys.
- **`ScrollHandle`** (engine `Scroll/Runtime/ScrollHandle.cs`) is the only way to move a scroller: `ScrollTo(offset,
  ScrollMove.Glide|Immediate|Follow)`, `ScrollBy`, `BringIntoView(top, extent, align, move, margin)`, and read
  `Offset`/`Motion`/`AtStart`/`AtEnd`/`ExtentSignal`/`ViewportSignal` signals. A move on an unbound handle is latched
  until the viewport mounts. For a realized node use `scene.BringIntoView(node, align, move)`
  (`SceneScrollExtensions`).
- **Virtualized lists** go through `ItemsView.CreateBound(…, new ListOptions { Scroll = new ScrollOptions { Handle =
  _listScroll, ScrollKey = ListScrollKey, AutoEdgeFade = … } })` (`Entities/Track.Table.cs`). Flat rows are
  `ListRowEl`-cheap; the engine realizes the whole velocity-sized window every frame — never cap or stagger rows.
- **A header that should scroll its list** sets `WheelTarget = _listScroll` (Track.Table's chrome) — the notch becomes a
  normal glide on the list.
- **`AutoEdgeFade` scrollers paint nothing themselves — put the background on the PARENT.** The engine composites a
  viewport's edge fade as an analytic feather distributed onto the items below it (no offscreen group surface; a
  shelf's feather rides the page scroll for free — `..\fluent-gpu\docs\design\subsystems\gpu-renderer.md` §13.1e). A
  `Fill` (or border / gradient / shadow) on the ScrollEl / VirtualListEl / ItemsView viewport ITSELF overlaps its content
  inside the fade band, so that fade must render as a group surface every change (retained, but still an offscreen pass
  and an effect-budget slot). Author the fill on the parent box instead — same pixels, the cheap route. Checked
  2026-09-24: no Wavee scroller sets `AutoEdgeFade` and a `Fill` on the same node.
- **Reading scroll in UI:** `UseScroll(handle?)` (nearest scroller through `ScrollCtx`) / `UseScrollProgress(in0, in1)`.
  Read them in a bind or a coarse memo — a `Render()` that reads `Offset.Value` re-renders every scrolled frame. If the
  value only moves pixels, it should be a scroll effect (below), not a bind.
- **Which section am I in** (pivot scroll-spy) is a pure, engine-free decision: `Detail.ScrollSpy.ActiveSectionOf`
  (`Entities/Detail.ScrollSpy.cs`, tested by `ScrollSpySectionTests`) / `Detail.BandLayout.ActiveSection`, fed from the
  handle's offset.

## Sticky, clip, collapse, parallax — declared, posed by the engine

All of these are `ScrollEffect` rows the engine evaluates on the render thread against the snapped content position
(no app work per frame, no relayout):

```csharp
// Entities/Track.Table.cs — the playlist/album hero system
new BoxEl { ScrollScope = TableScope, … }                         // names the sticky "containing block"
Detail.Hero(…).Sticky(0f, TableScope)
              .Collapse(Detail.VerticalLayout.CollapseDistance(heroH), Detail.VerticalLayout.CompactIdentityHeight,
                        CollapseAnchor.Leading);                   // presented height shrinks; input follows it
Chrome(…).Sticky(Detail.VerticalLayout.CompactIdentityHeight, TableScope, engaged: _compactInteractive);
content.StickyClip(stickyInset, engaged: _bodyClipEngaged);        // rows guillotined under the pinned band

// Entities/Detail.UI.Hero.cs — the expanded hero rides away and fades; the compact band reveals
presentation.Parallax(0.0, cd, 0f, -cd).Fade(VerticalLayout.ExpandedFadeStart(cd), cd, 1f, 0f);
compactBand.Reveal(VerticalLayout.CompactRevealStart(cd), cd - VerticalLayout.CompactRevealStart(cd), dy);

// Entities/Artist.UI.cs — the stretchy artist photo
photo.StretchFromTop().ParallaxY(ArtistHeroLayout.PhotoParallaxFraction, photoH);
```

- **Scope:** `.Sticky(top, scope)` releases at the end of the nearest ancestor whose `ScrollScope` is `scope`; a null
  scope means the node's parent. Name scopes as constants on the owning component (`TableScope`, `TableBlockScope`,
  `TrailScope`).
- **`engaged:`** is a `Signal<bool>` the engine writes on the UI thread, before the frame publishes, **only when the pin
  engages or releases** — use it for UI DECISIONS (Track.Table's compact band takes input only while engaged; the clip
  line's feather gates on it). Never for pixels — a "stuck" look is a `.Fade` at the same offset.
- **`Collapse`**: `Leading` = the children stay and are cut at the presented edge (Wavee's heroes — their presentation
  carries its own parallax); `Trailing` = the children ride the presented bottom. Hit-testing follows the presented
  height, so rows below a collapsed hero take the input without any app code.
- **`StretchFromTop`** composes with a parallax on the same node (the engine folds a node's transform rows into one
  matrix).

## Lyrics: Follow + MeasureAll (`Shell/Lyrics.UI.cs`)

- The line list sets `MeasureAll = lines.Count <= Surface.MeasureAllCap` (400, `Shell/Lyrics.cs`): every line is
  realized and measured, so the follow target for a line that was never on screen is its REAL offset, not an estimate.
- Following playback is `_scroll.ScrollTo(target, ScrollMove.Follow)` — the engine's velocity-continuous glide that is
  **ignored while a user-driven plan (wheel/drag/fling/thumb) is live**, so a user grab always wins and the next resync
  re-posts. The lyrics code decides WHEN to follow (a pure decision); it never animates the offset itself.

## Diagnostics

- **Diagnostics ▸ Scroll card** (`Screens/Diagnostics.Scroll.cs`): the engine probe level **Off · Summary · Trace**
  (persisted `diag.scrollProbeLevel`, default Summary, applied at boot), the **feel profile** (the engine's
  `FeelProfiles`: Standard · Glide; persisted `diag.scrollFeelProfile`), and **Export CSV** →
  `logs/scroll-<yyyyMMdd-HHmmss>.csv` (the analyze.py format, engine schema 3: a trailing `vp` column names the viewport
  on every frame/plan/coverage/extent row, frames carry the pre-clamp `plan=` position, plans their `dest=`, and the
  UI/render rings are clipped to their common window; record at **Trace** for plan, coverage and touchpad/touch input
  rows).
  Decisions are `ScrollDiagRules` (tested: `ScrollDiagRulesTests`).
- **Diagnostics ▸ Tiles card** (`Screens/Diagnostics.Tiles.cs`): the always-on `AppHost.LastTileCensus`; a nonzero
  `ExposedMissing`, `DegradedSlices`, `CoverageClamps` or resident bytes over budget is a warning (all must be 0 / within
  budget). `StaleTiles` (a VALID, un-re-rastered tile whose pixels no longer match what the stream wants) is also a
  warning row. The **GPU pass timing** toggle (`AppHost.GpuPassTimingEnabled`) is a session knob — never persisted, off on
  every launch. Decisions: `TileDiagRules` (`TileDiagRulesTests`).
- **Diagnostics ▸ Evidence card** (`Screens/Diagnostics.Evidence.cs`): the engine's evidence ledgers surfaced for a
  developer — the stale-tile invariant, `PixelQuery`'s pixel-under-cursor pick, and **Export bundle** (all ledgers +
  `keyed.tsv` + the tile census zipped via `Screens/EvidenceBundle.cs`). Developer-only; never wakes the window.
  `wavee://diag?cmd=…` verbs (`DeepLinkKind.Diag`; replies land in `logs\evidence\replies.tsv`, bundles listed in
  `index.txt`):

  | `cmd=` | Does |
  |---|---|
  | `bundle` | exports the full evidence bundle (raster/walk/composite ledgers, `items.tsv`, `pixel.tsv`, `keyed.tsv`, the tile census) |
  | `pixel` | runs `PixelQuery` at a window pixel and replies with the hit list + `PixelHit.InFeatherBand` |
  | `scroll` | exports a `ScrollProbe` CSV (schema 3) |
  | `vps` | reports `ViewportState` for every live scroller |
  | `probe` | sets the probe level without opening Diagnostics |

  Driver scripts: `ops/tools/evidence/` — `Start-VerifyWavee.ps1` (a second, profile-scoped instance beside the
  owner's), `Send-WaveeDiag.ps1` (sends a `wavee://diag` verb), `Invoke-Scenario.ps1` (band / edgecue / soak / backsteps
  scenarios), `Read-Bundle.py` (unpacks and summarizes a bundle).
- **Log lines** (`Screens/Diagnostics.Host.cs`, `NavigationFrameWatch`, into `%LOCALAPPDATA%\Wavee\logs`):
  - `scroll.frames` — one per scroll burst of ≥ 5 frames (a burst ends after 500 ms without scroll-active frames): the
    route, navId, the frame rollup with present cadence, and the sidebar re-plan census. Warning when it stalled.
  - `scroll.burst` — while the probe is at Summary or Trace: the engine's own `BurstSummary.FormatLine()`
    (`notches presents clamps jumps late jitter(avg/max) costMs(avg/max) tiles(avg/max) exposedMissing verdict=`).
    Warning unless `verdict=Smooth`. `Clamped` = a would-be blank row; `Jumped` = an extent correction a frame late;
    `Late` = uneven presents; `Uneven` = jitter over 0.20.
  - `[render.pace]` — the engine render thread's once-a-second pacing line while motion is live (presents by kind,
    skipped/`missed=` ticks, slot wait, `depth=`, governor, `gpuMs=`); it reaches the file because `Platform.Host.cs`
    `RouteFor` routes the prefix (an unrouted engine `Diag.Line` prefix never reaches `wavee-*.log`).
  - `[tiles.stale]`, `[d3d12.scratch]`, `[evidence]` — the engine's evidence lines, routed AlwaysOn: a newly stale
    slice (names node, `role=`, `seg=`, tiles, want/have), an inline group's scratch surface refused mid-raster, and
    a frame-capture failure.
- Engine `--fg` switches work on Wavee's command line too (`--fg gpu-timing,fps`); there are no environment variables.

## Checklist for a scroll change in the app

1. Is it a declaration (key, handle, effect, scope, `MeasureAll`, `WheelTarget`)? Then it's app code.
2. Is it a behaviour (how it moves, when rows appear, what the finger feels)? Then it's the engine — record it, gate it,
   fix it there.
3. Any new pure decision (a spy, a follow policy, a card verdict) is an engine-free class with a `Wavee.Tests` unit test
   — never a source-text test.
