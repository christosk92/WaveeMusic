# Scrolling in Wavee — developer guide

Wavee scrolls on the FluentGpu engine's scroll system. The engine is the **authority** for how anything moves; this
guide covers what the app declares and how to diagnose it. Read the engine canon before changing behaviour:

- **Canon:** `..\fluent-gpu\docs\design\subsystems\scroll.md` (in the sibling engine checkout — motion model, input,
  posing, virtualization, effects, pacing, diagnostics, invariants, and the list of deleted band-aids).
- **Measuring feel:** `..\fluent-gpu\docs\guide\scroll-lab.md` (the Scroll Lab: record a session, read the metric
  verdicts, live-tune the feel).
- **Agent skills:** the app's `.claude/skills/wavee/scrolling.md`; the engine's `fluentgpu-scroll` skill.

## How it works, in one paragraph

Every input (wheel notch, touchpad contact, touch pan, scrollbar thumb, key, `ScrollTo`) makes the viewport's
`ScrollHandle` author a new immutable, closed-form motion plan. The engine's render thread evaluates that plan at the
predicted present time of each compositor tick, clamps it to the rows the UI thread has actually realized (so a row is
never shown blank), snaps it to the device-pixel grid, and poses the content plus every scroll-linked effect (sticky
headers, collapsing heroes, parallax, fades) as a GPU composite parameter — a pure scroll frame re-records nothing.
Measured row heights correct the plan's coordinate frame in the same call, so content never jumps. Wavee's job is only
to say *which* scroller, *which* restore key, *which* effects.

## What the app declares

| Need | Declaration | Example |
|---|---|---|
| A scrolling page | `ScrollView(body) with { ScrollKey = UseContext(Shell.PageScrollScope) + key, Handle = _scroll }` | `Entities/Artist.Page.cs`, `Entities/Episode.Page.cs`, `Entities/Concert.Page.cs` |
| A virtualized list | `ItemsView.CreateBound(…, new ListOptions { Scroll = new ScrollOptions { Handle, ScrollKey, AutoEdgeFade } })` | `Entities/Track.Table.cs` |
| Restore the position on revisit | `ScrollKey` — stable per CONTENT, composed under `Shell.PageScrollScope` (the tab) so two tabs keep two offsets | every content page |
| Move programmatically | `ScrollHandle.ScrollTo` / `ScrollBy` / `BringIntoView`; `ScrollMove.Glide` (default), `Immediate`, `Follow` | pivots, "jump to now playing" |
| Header forwards its wheel to its list | `WheelTarget = _listScroll` | Track.Table chrome |
| Pin a header | `.Sticky(top, scope, engaged: signal)` + `ScrollScope = scope` on the containing box | Track.Table chrome |
| Cut rows under a pinned band | `.StickyClip(inset, engaged: signal)` | Track.Table body |
| Collapse a hero into a band | `.Sticky(0f, scope).Collapse(distance, minH, CollapseAnchor.Leading)` | Track.Table hero |
| Slide / fade / reveal with scroll | `.Parallax(…)`, `.ParallaxY(fraction, over)`, `.Fade(…)`, `.Reveal(…)` | `Entities/Detail.UI.Hero.cs` |
| Stretch on overscroll | `.StretchFromTop()` (composes with a parallax) | `Entities/Artist.UI.cs` |
| Collapse AND stretch one hero | the two compose: the `Leading` collapse cuts at its presented edge by itself, so put NO `ClipToBounds` on the collapsing root (a box clip there cuts the photo's stretch above the root's top) | `Entities/Artist.UI.cs` `HeroBanner` |
| Exact targets for rows never on screen | `MeasureAll = true` (bounded — lyrics cap it at 400 lines) | `Shell/Lyrics.UI.cs` |
| Follow something without fighting the user | `ScrollTo(target, ScrollMove.Follow)` — ignored while a user drag/wheel/fling is live | lyrics |
| React to scroll in UI | `UseScroll(handle?)` / `UseScrollProgress(in0, in1)` in a bind or coarse memo; section spies stay pure (`Detail.ScrollSpy`) | detail pivots |

Rules that follow from the engine contract:

1. **Create each `ScrollHandle` once** per component (`readonly ScrollHandle _scroll = new();`). Component props freeze
   at mount — changing data reaches a scroller through its handle, a signal or a `Key` remount, never a new factory field.
2. **`engaged:` signals are for decisions, not pixels.** They flip exactly when a pin engages/releases; drive input
   hand-offs and feather gates from them, and put any "stuck" visual in a `.Fade` row.
3. **Never move content yourself** — no manual `Transform` on a scroll content node, no offset nudges after a measure, no
   timers waiting for a list to "settle". Structural changes above the viewport are the engine's (`ItemsView`/`LazyGrid`
   call `ShiftFrame`).
4. **No app-side workarounds for engine scroll behaviour.** If it jumps, blanks, lags or feels wrong, capture it and fix
   it in the engine (or file the gap) — see below.
5. **An `AutoEdgeFade` scroller paints nothing of its own — author its background on the PARENT.** The engine distributes
   a viewport's edge fade as an analytic feather onto the items below it (no offscreen group; nested shelf fades ride the
   page scroll for free — engine `gpu-renderer.md` §13.1e). A `Fill` / border / gradient / shadow on the ScrollEl,
   VirtualListEl or ItemsView viewport itself overlaps its content inside the band, which forces the fade back onto a
   group surface (and an effect-budget slot). Same pixels either way; the parent fill is the cheap route. (Audited
   2026-09-24: no Wavee scroller combines the two.)

## Diagnosing

1. **Diagnostics ▸ Scroll** — set the probe level (**Summary** is the default and keeps the `scroll.burst` log line;
   **Trace** records every input, plan and pose, and is required for touchpad/touch input rows), pick a feel profile
   (Standard · Glide — the engine's presets; Standard is the default), reproduce, then **Export CSV**
   (`logs/scroll-<timestamp>.csv`, loadable in `C:\WAVEE\wheel-curve-probe\analyze.py`; the trailing `vp` column
   separates the viewports — sidebar, rail, page, lyrics — that the file interleaves).
2. **Read the log** (`%LOCALAPPDATA%\Wavee\logs`): `scroll.frames` per burst (frame rollup + present cadence) and
   `scroll.burst` (the engine's verdict: `Smooth`, or `Clamped` = would-be blank row, `Jumped` = a late extent
   correction, `Late` = uneven presents, `Uneven` = jitter). The engine's `[render.pace]` line (once a second while
   motion is live: presents by kind, skipped and MISSED ticks, slot wait, present-queue depth, governor, GPU ms) is routed
   into the log file by `Platform.Host.cs` `RouteFor` — an engine `Diag.Line` prefix that is not routed there never
   reaches the file.
3. **Diagnostics ▸ Tiles** — the retained-tile census: `ExposedMissing`, `DegradedSlices`, `CoverageClamps` and now
   `StaleTiles` must be 0; the GPU pass timing toggle shows where GPU time goes per frame (session-only, never
   persisted).
4. **Diagnostics ▸ Evidence** (developer-only) — the engine's evidence ledgers: the stale-tile invariant, a pixel-under-
   cursor pick (`PixelQuery`), and **Export bundle**, which zips every ledger plus `keyed.tsv` and the tile census
   (`Screens/EvidenceBundle.cs`). The same data is reachable without opening the app via `wavee://diag?cmd=…`
   (`bundle` / `pixel` / `scroll` / `vps` / `probe`; never wakes the window) and the `ops/tools/evidence/` scripts
   (`Start-VerifyWavee.ps1` runs a second, profile-scoped instance beside the owner's; `Send-WaveeDiag.ps1` sends a
   verb; `Invoke-Scenario.ps1` drives band / edgecue / soak / backsteps scenarios; `Read-Bundle.py` reads a bundle).
5. **Reproduce in the engine's Scroll Lab** (`dotnet run --project ..\fluent-gpu\src\FluentGpu.ScrollLab -c Release`),
   record a session, and read the metric that is red; then write the failing engine gate and fix the mechanism there.

Code: `Screens/Diagnostics.Scroll.cs` (`ScrollDiagRules`, `ScrollSettings`, the card), `Screens/Diagnostics.Tiles.cs`
(`TileDiagRules`, the card), `Screens/Diagnostics.Evidence.cs` (the Evidence card), `Screens/EvidenceBundle.cs` (the
bundle export), `Screens/Diagnostics.Host.cs` (`NavigationFrameWatch` — the log lines). Tests: `ScrollDiagRulesTests`,
`TileDiagRulesTests`, `ScrollSpySectionTests`, `EvidenceReportTests`.
