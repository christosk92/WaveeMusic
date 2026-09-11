# Operation ultra-fast GPU engine — app-side (Wavee repo) progress

Tracks the APP half of `docs/plans/operation-ultra-fast-gpu-engine` (source plan: the user's
`comapre-all-our-changes-reflective-scone.md`, section "Operation ultra-fast GPU engine", P5 — "App: the
virtualized track row on the new API"). The engine half (P0–P4, all done, all primitives this phase needs) is
tracked separately in `..\fluent-gpu\docs\plans\operation-ultra-fast-progress.md`; read that first for
`Element.Visible`, bound spans, `BoundItemScope<T>`, `FormatCache`, recycle-snap transitions, `PersonPicture.Bound`,
`ToolTip.Wrap`, and incremental layout — everything this file's remaining slices build on.

## Status

P5 was re-scoped into five sequential slices. **Slices 1–5 are DONE.** Slice 3 filled every remaining
`TrackRowTemplate` cell. Slice 4 cut `BoundRowContent.Render` over to `TrackRowTemplate.Build` and deleted the
old `RowGrid`/`ShimmerRow`/`BoundTitle`/`RampReveal` path plus `DetailRevealRamp` — the virtualized list no
longer chunks VALUES or TYPES; a skeleton is `TitleState == Loading` on the same bound template. Slice 5
landed with slice 2. Fetch windows are gone from `Features/Detail` (a page demands its whole model).

## Slice 1 — the per-row VALUE + its bound source (DONE)

**What landed**, all in `src/apps/Wavee/`:

1. **`RowPresentation` extracted to its own file**, `Features/Detail/RowPresentation.cs` — previously a `private`
   record struct nested inside `TrackList` (`Features/Detail/DetailTracks.cs`, engine-bound, GPU-heavy,
   NOT compiled into `Wavee.Tests`). Now a top-level `readonly record struct RowPresentation(...)` in `namespace
   Wavee`, Wavee.Core + BCL only, so `Wavee.Tests` can source-include it directly — the SAME pattern
   `DetailTrackProjection.cs` already uses (`Wavee.Tests.csproj`'s `<Compile Include=".../DetailTrackProjection.cs"
   />`). Grew from 10 fields to 13, exactly as the plan specifies:
   `Track, DisplayIndex, TrackRow.State, MarqueeDisabled, ShowTrackArtist, ShowListMetadata, Go, AddedBy,
   TitleState, HasVideo, PlaysState (TrackFactState), IsSkeleton, IsExpanded`.
   - `IsSkeleton = !RowRevealed(displayIndex) || TitleState == TrackTitleState.Loading` — the exact two conditions
     `BoundRowContent.Render()` already checked before this slice, now carried on the value.
   - `IsExpanded` = `MembershipDiff.RowKeyMatches(_expandedRow.Value, track, displayIndex)` — mirrors `_expandedRow`
     once per projection instead of once per cell.
   - `PlaysState` = the same projection `TrackList.PlaysStateFor` already hands the grid, now also on the value.
   - `RowPresentation.Empty` (out-of-range fallback: no track, non-interactive, `IsSkeleton = true`) and
     `RowPresentation.Skeleton(int displayIndex)` (`Empty with { DisplayIndex = displayIndex }`) — both static
     factories the plan names.
   - Value equality is free (a plain C# `readonly record struct`) — `RowPresentationTests` pins that two
     independently-built values over identical inputs compare equal, which is what a LATER slice's gated
     `Memo<RowPresentation>` needs for "an activity-only republish is silent."
2. **`TrackRow.State` split into its own file**, `Components/TrackRow.State.cs` — `RowPresentation`'s one non-BCL
   field type. `TrackRow` (`Components/TrackRow.cs`) is now `internal static partial class TrackRow`; the `State`
   record struct moved out verbatim (`internal readonly record struct State(bool IsNow, bool IsPlaying, bool
   IsBuffering, bool IsTop, bool Saved)`), leaving a one-line pointer comment where it used to live
   (`TrackRow.cs` ~line 182). This is TrackRow's ONLY engine-free fragment — the rest of the class is
   FluentGpu.Dsl/Controls/Animation throughout and cannot be source-included.
3. **`TrackList.Presentation(TrackRowsSnapshot snapshot, int displayIndex)`** (`DetailTracks.cs`, new instance
   method right after `TrackAt`) — the projection itself, moved VERBATIM out of `BoundRowContent`'s old inline
   `UseComputed` block (nothing re-derived, nothing changed: same `ResourceKey`/`FacetKind` lookup, same
   `TrackMetadataReadiness.Title`, same `TrackRow.StateOf`, same `AddedByProfile`). Still engine-bound (reads
   `_bridge`/`_lib`/`_play`/`_queryDemand`/`_expandedRow`), so it stays in `DetailTracks.cs`, not the new file.
4. **`TrackList._rowPresentations : BoundItemsSource<RowPresentation>?`** (new field, alongside the existing
   `_rowItems : BoundItemsSource<Track>?`) — built the same way `rowItems` already is:
   `BoundItems.Project(rowsSnapshot, snap => View(snap).Length, (snap, i) => Presentation(snap, i),
   RowPresentation.Empty)`, `UseMemo`'d beside `rowItems`, assigned to the field right after `_rowShape`.
   Deliberately a SEPARATE source from `rowItems`, not a re-projection through it — `BoundItems.Project` reads the
   snapshot signal itself, so sharing costs nothing and this stays additive next to `rowItems` (whose Track-typed
   consumers — `ExpandableRowSlot`'s drag payload, `WrapRowSwipe`, `FactsOptionsFor`, `VerticalItemContent`,
   `RowOrRecContent`'s kind computed, `ItemText`/`IsItemEnabled` — are UNCHANGED and still read `_rowItems`).
5. **Wired into all three `CreateBound` sites** by wiring the ONE method they all funnel through:
   `TrackList.BoundRow(RowScope scope, IReadSignal<Track> item, float rowH, int trackStart, hoverPaused)`
   (`DetailTracks.cs` ~line 2740) now also builds `_rowPresentations!.BindItem(scope.Index, trackStart)` and passes
   it into `BoundRowContent`'s constructor. All three `ItemsView.CreateBound` call sites (`DetailTracks.cs` ~1060
   the recs-capable branch via `RowOrRecContent`→`ExpandableSlot`→`ExpandableRowSlot`→`BoundRow`; ~1093 the plain
   branch via `ExpandableSlot` directly; ~1573 the vertical branch via `VerticalItemContent`→`ExpandableSlot`) reach
   `BoundRow` through this one chain, so editing it there was sufficient — no per-site changes needed.
6. **`BoundRowContent` reads the new item, rendering unchanged** (`DetailTracks.cs`, the `sealed class
   BoundRowContent : Component` nested in `TrackList`): constructor gained `IReadSignal<RowPresentation>
   presentation` (stored as `_presentation`), the old `IReadSignal<TrackRowsSnapshot> _state` field/param was
   DELETED (nothing else read it once its one use — the inline projection — moved to `TrackList.Presentation`).
   `Render()`'s old ~30-line inline `UseComputed(() => { ...build RowPresentation... })` hook was deleted; the
   method now reads `_presentation.Value` directly (a plain signal read after the `!revealedNow` early return,
   exactly as lazy as the old computed was). Every line after that — the shimmer branch, the marquee/plain title
   choice, `RowGrid`, the ramp-reveal wrapper — is byte-identical to before this slice. This is deliberate per the
   coordinator's brief: slice 1 is data plumbing only, not a rendering change.

**Files touched:** `src/apps/Wavee/Features/Detail/RowPresentation.cs` (new), `src/apps/Wavee/Components/
TrackRow.State.cs` (new), `src/apps/Wavee/Components/TrackRow.cs` (class → `partial`, `State` moved out),
`src/apps/Wavee/Features/Detail/DetailTracks.cs` (`_rowPresentations` field, `Presentation()` method, `rowItems`
block gains `rowPresentations`, `BoundRow`/`BoundRowContent` wiring), `src/apps/Wavee.Tests/
Wavee.Tests.csproj` (two new `<Compile Include>` lines beside `DetailTrackProjection.cs`), `src/apps/Wavee.Tests/
RowPresentationTests.cs` (new, 8 tests).

**No app-visible behaviour changed.** `TrackRow.Grid`/`RowGrid`/`BoundRowSkin`/`ExpandableRowSlot`/the reveal ramp/
`ShimmerRow`/`WaveeEqualizer` are all untouched — that is slices 2–5.

### Verification

- `dotnet build src/apps/Wavee/Wavee.csproj -c Release` — **0 warnings, 0 errors** (`TreatWarningsAsErrors` is on
  for this project; confirmed clean both immediately after the extraction and again as a final sanity check).
- `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj -c Release --blame-hang --blame-hang-timeout 3m
  --blame-hang-dump-type none` — **7575 passed, 1 skipped (a real module-process integration test, always skipped
  headlessly), 1 failed, 7577 total.** The one failure,
  `LocalMediaProviderTests.ConnectMask_PublishesSpotifyRowsVerbatim_MasksTheRest_AndKeepsEveryUid` (a 10-second
  `TaskCanceledException` inside `QueryPublication.Until`), is unrelated to this slice — it touches local-media
  masking, nothing this slice's files — and **passes individually** (`--filter
  FullyQualifiedName=Wavee.Tests.LocalMediaProviderTests.ConnectMask_...`, 117 ms): a load flake under the full
  7.5k-test run, per CLAUDE.md's documented rule, not a regression.
- `RowPresentationTests` alone: **8/8 passed** (`--filter FullyQualifiedName~RowPresentationTests`).
- A concurrent engine session (`..\fluent-gpu`, uncommitted, unrelated: the P4 `FG_LAYOUT_VERIFY` DEBUG parity
  oracle from that repo's own progress doc's "Open items") transiently broke both the app AND the Wavee.Tests
  build several times while this slice's builds were being verified (`FlexLayout.Verify.cs` CS0649/`DragController.
  cs`/`InputDispatcher.cs`/`Reconciler.cs`/`ConnectedAnimation.cs` CS0131 — none of them files this slice touched).
  Per this task's own instruction ("if an app build fails with errors in engine files you did not touch, wait a
  minute and retry"), each was waited out and retried until the engine repo settled; the final two verification
  runs above are both clean against the settled engine state. Nothing in `..\fluent-gpu` was read, edited, or
  waited on beyond retrying `dotnet build`/`dotnet test`.

## Slice 2 — `TrackRowTemplate.Build`, the highest-allocation cells (number/heart/art/title) (DONE)

**What landed**, all in `src/apps/Wavee/`:

1. **`Components/TrackRowTemplate.cs`** (new) — `internal static class TrackRowTemplate { internal static Element
   Build(BoundItemScope<RowPresentation> item, TrackList.RowShape shape, RowHandlers h) }`. Builds the SAME
   `GridEl` shape `TrackRow.Grid` does (`Columns = shape.Tracks`, `ColGap`/`RowHeight`/`Padding` all read off
   `shape`/`TrackRow`'s own tier helpers), with the same `CellKey`-keyed children so the header stays aligned.
   Wired to nothing yet — no call site references `Build`; it exists standalone, verified by compiling and by
   `TrackRowTemplateTests`. The cutover into `BoundRowContent.Render()` is still slice 4, unchanged from the
   original plan.
   - **Number cell**: the rest/transport `ZStack` from `TrackRow.NumberCell`, rebuilt as siblings gated by
     `item.Show` instead of a per-render ternary — number (`item.Number`), chart glyph (`item.Text`/`item.Color`
     through the new `TrackRowGlyphs.ChartText`), top-track star, all three mutually-exclusive via their own
     `Show` predicates (mirrors the old `isBuffering ? … : isNow ? … : isTop ? … : chart ?? number` chain
     bullet-for-bullet); `item.ShowWhen(IsNow && !IsBuffering, () => WaveeEqualizer.Of(item.Signal(p =>
     p.State.IsPlaying), …))` and `item.ShowWhen(IsBuffering, TrackRow.Spinner)` for the two rare/expensive
     branches. Transport layer: `item.Text`/`item.Color` for the play/pause glyph, `OnClick =
     item.Invoke(p => h.Play(p.DisplayIndex))`, layer `Visible = item.Show(p => !p.Track.IsNotYetOut() &&
     !p.IsSkeleton)`.
   - **Heart cell**: both glyphs always mounted, `Visible = item.Show(p => !p.State.Saved)` /
     `item.Show(p => p.State.Saved)`; the filled glyph carries `Enter = HeartPop` (a new `static readonly
     EnterExit`, same visual as `TrackRow.Heart`'s old `HeartPopIn` but expressed on `Element.Enter` — the P1
     presence-channel Enter seed, `virtualization.md` §5.5 — instead of a keyed-child `LayoutTransition`). No
     `LikeEdge`/`UseRef` bookkeeping in the template: the false→true `Visible` edge on a live recycle already
     seeds `Enter`, and `Reconciler.SuppressBoundTransitions` snaps it instead on a genuine recycle. NOT deleting
     `TrackRow.LikeEdge`/`BoundRowContent`'s `likePrev` hook this slice — that stays live until slice 4 actually
     cuts `BoundRowContent.Render()` over to this template; deleting it now would strand dead code with no
     caller change to justify it.
   - **Art cell**: an always-mounted placeholder `BoxEl` (`Fill = item.Color(p =>
     Surfaces.PlaceholderFor(ImageSource.UrlFor(p.Track.Image, false)))`) under an always-mounted `ImageEl`
     (`Source = item.Image(...)`, same URL resolver) — both static `Width`/`Height`/`Corners`/`DecodePx` off
     `shape.Art` (template-static, per P3: a density change is a tier-keyed list remount, never a per-row bind).
     No new `Surfaces` helper needed — `Surfaces.PlaceholderFor(string? url)` already existed for exactly this.
   - **Title cell**: `titleCol`'s `Opacity` bound (`item.Opacity(p => p.Track.IsNotYetOut() ? 0.45f : 1f)`, same
     dimming as `TrackRow.Grid`'s `notYetOut` branch); plain title bound `Text`/`Color`, `Visible` presence-gated
     to "not now-playing-with-marquee AND title ready" (the common case, so it is a `Show`, not a `ShowWhen`);
     `item.ShowWhen` for the marquee (now-playing + marquee enabled) and for the retry line (`TitleState !=
     Ready`, verbatim `BoundRowContent`'s old inline Offline/Unavailable + Retry-button `BoxEl`, moved not
     rewritten). Metadata line (artist/album subline) is NOT built here — it is part of slice 3's scope, so the
     title cell today never shows it (a visual gap only reachable once slice 4 actually renders this template).
2. **`Features/Detail/RowHandlers.cs`** (new, Wavee.Core + BCL only — no FluentGpu) — `RowHandlers` (`Play:
   Action<int>`, `ToggleLike`/`ToggleExpanded`/`RequestContext: Action<RowPresentation>`, `Go: Action<string,
   string?>`, `RetryMetadata: Action`) and `TrackRowGlyphs.ChartText(ChartEntry?)`, the pure chart-glyph text
   selection the number cell's chart lane binds through (mirrors `TrackRow.ChartGlyph`'s three-way mapping).
   Split out of `TrackRowTemplate.cs` and placed next to `RowPresentation.cs` for the SAME reason slice 1 split
   `RowPresentation`/`TrackRow.State` out of their FluentGpu-bound homes: so `Wavee.Tests` can source-include
   them directly. `RowHandlers` is not wired to any real `TrackList` callback yet (no factory method on
   `TrackList` builds one) — that wiring is part of slice 4's cutover, once there is a real call site to resolve
   the delegates from.
3. **`TrackList.RowShape`** (`Features/Detail/DetailTracks.cs`) changed from the nested-default `private` to
   `internal` — `TrackRowTemplate.Build` needs it as a parameter from outside `TrackList`, same reasoning as
   slice 1's `RowPresentation` extraction. No other change to `DetailTracks.cs` this slice (no call site wired,
   nothing else cut over).
4. **Slice 5 folded in**: `Components/Equalizer.cs`'s `WaveeEqualizer.Of` gained two signal-based overloads
   (`Of(IReadSignal<bool> playing, ColorF/Func<ColorF> color, …)`); the existing plain-`bool` overloads now
   delegate through a new `ConstBoolSignal : IReadSignal<bool>` adapter instead of having their own
   implementation. `EqHost.Render()` reads `p.Playing.Value` (subscribing) instead of a frozen ctor bool, its
   phase-reset `UseEffect` is now `DepKey`-gated on `animate` (`UseEffect(fn, animate)`, `deps` overload) instead
   of running once at mount, and the `Key = animate ? "eq-play" : "eq-pause"` line is DELETED — the host is now
   a persistent component across a play↔pause flip (a `Visible`/prop patch, not a remount), exactly per the plan.
   Both legacy plain-`bool` callers (`TrackRow.NumberCell`'s eager path, the card now-playing overlays) are
   unaffected — same public API, same visual behaviour, verified by the full `dotnet test` run below (no test
   changed for this, existing eager-row coverage caught nothing broken).
5. **`TrackRowTemplateTests`** (new, `src/apps/Wavee.Tests/TrackRowTemplateTests.cs`) — 8 tests, but NOT what the
   original slice-2 scope asked for. See "Test-coverage decision" immediately below for why and what to do about
   it next.

**Deliberately NOT done this slice** (kept for slice 3/4, per the brief): the metadata/artist/album/added-by/
date/plays/tempo/duration/video/more/expand cells are static `TODO(slice 3)` placeholders (`new BoxEl()`, each
still correctly gated behind the SAME `ColumnSet` flag `TrackRow.Grid` gates it behind, so the grid's column
COUNT — and hence its `TrackSize[]` alignment with the header — matches exactly at every tier/density).
`BoundRowContent` is untouched; nothing calls `TrackRowTemplate.Build` yet. `TrackRow.LikeEdge`'s
`BoundRowContent` call site is untouched (see the heart-cell note above).

### Test-coverage decision: `TrackRowTemplateTests` is engine-free, not element-tree

The slice-2 brief asked for a test proving, per `ColumnSet` tier/density: bound/static channel kinds per cell key
stable, `Visible` bound on exactly the presence nodes, no `Embed.Comp` outside `ShowWhen`. That requires walking
the REAL `Element` tree `TrackRowTemplate.Build` returns — `GridEl`/`BoxEl`/`ImageEl`/`Marquee`/`WaveeEqualizer`,
all FluentGpu types `Wavee.Tests` cannot source-include the way `RowPresentation`/`TrackRow.State` were (they are
BCL+Wavee.Core only; `TrackRowTemplate.cs` is FluentGpu-bound throughout, like the rest of `TrackRow.cs`).

I tried the brief's own fallback (`InternalsVisibleTo("Wavee.Tests")` + a `Wavee.Tests.csproj` `ProjectReference`
to `Wavee.csproj`) and reverted it: **`Wavee.Tests.csproj` already source-includes ~217 files out of `src/apps/
Wavee/**` via `<Compile Include>`** (the same pattern slice 1 used for `RowPresentation.cs`, but pervasive —
`DetailTrackTableRules.cs`, `DetailTrackProjection.cs`, `HomeArtistRowLayout.cs`, dozens more). Adding a
`ProjectReference` to `Wavee.csproj` makes every one of those types exist TWICE in `Wavee.Tests`'s compilation —
once as the locally-compiled source file, once inside the referenced `Wavee.dll` — which is `CS0433` (ambiguous
type) on essentially every existing test file that touches any of those 217 types. Untangling that (removing the
duplicate `Compile Include`s in favour of the assembly reference, file by file, re-verifying each still compiles
standalone) is a real, separate, sizeable cleanup — explicitly flagged as a "check with the coordinator first"
risk by whoever wrote the slice-2 brief's "Open items" section, and confirmed here to be as large as feared.

**What I did instead**: split the two genuinely engine-free pieces of slice 2's design — `RowHandlers` (a plain
delegate bundle) and `TrackRowGlyphs.ChartText` (the pure chart-glyph selection) — into `Features/
Detail/RowHandlers.cs` (Wavee.Core + BCL only), source-included into `Wavee.Tests.csproj` next to
`RowPresentation.cs`. `TrackRowTemplateTests` covers those directly (delegate wiring, the `RowHandlers` value
shape, `TrackRowGlyphs.ChartText`'s four-way status mapping) and documents, in its own file header, exactly why
the structural/channel-kind assertions are deferred. **Recommendation for whoever picks up slice 3 or 4**: slice
4 is the one that actually needs to inspect the real element tree (it is the cutover, and the highest-risk step
— this is where a shape mismatch would actually ship), so it is the natural point to also do the `Wavee.Tests`
`Compile Include` → `ProjectReference` migration, OR to accept an engine-side / `VerticalSlice`-hosted structural
test instead of a `Wavee.Tests` one (the engine repo's own `BoundTemplateSuite` pattern, `virtualization.md` §5.5,
already tests bound-template shape invariants without this problem, since it never had the dual-inclusion setup).

### Verification

- `dotnet build src/apps/Wavee/Wavee.csproj -c Release` — **0 warnings, 0 errors**.
- `dotnet build src/apps/Wavee/Wavee.csproj -c Debug` — **0 warnings, 0 errors**.
- `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj -c Release --blame-hang --blame-hang-timeout 3m
  --blame-hang-dump-type none` — **7584 passed, 1 skipped (the always-skipped module-process integration test),
  0 failed, 7585 total.** No flake this run (slice 1's note about a concurrent engine session transiently
  breaking builds did not recur; `..\fluent-gpu` was not touched).
- `TrackRowTemplateTests` alone: **8/8 passed** (`--filter FullyQualifiedName~TrackRowTemplateTests`).

## Engine gaps noted so far

None. Every primitive slice 2 needed (`Show`/`ShowWhen`/`Text`/`Number`/`Color`/`Opacity`/`Image`/`Signal`/
`Invoke`, `Element.Enter`/`Visible`, `UseInterval(enabled:)`, `UseEffect(fn, DepKey)`) already existed exactly as
documented in `BoundItemScope.cs`/`Element.cs`/`DepKey.cs`/`Component.cs` — no app-side workaround needed.

## Slices 3 and 4 (DONE)

### Slice 3 — remaining bound cells (DONE)

`TrackRowTemplate.Build` now has every cell (metadata spans, artist/album, added-by, date, plays, tempo, duration,
video/more, expand). The header comment that called this a keyed alternate is stale no longer — this is the live
path after slice 4.

### Slice 4 — `BoundRowContent` cutover + ramp deletion (DONE)

`BoundRowContent.Render()` is `TrackRowTemplate.Build(new BoundItemScope<RowPresentation>(_scope, _presentation),
shape, Handlers)`. Deleted: `RowGrid`, `ShimmerRow`/`RowsShimmer`, `BoundTitle`/`BoundTitlePlain`, `RampReveal`,
`TrackRow.LikeEdge`, `DetailRevealRamp` (`_reveal`/`_rampActive`/`TickerClock`/`RowRevealed`).
`RowPresentation.IsSkeleton` is `TitleState == Loading` only — no reveal ramp, no VALUE chunking.
`TrackList.Handlers` wires Play/ToggleLike/ToggleExpanded/Go/RetryMetadata to the existing callbacks; context
stays the row skin's `WithContextMenu` + `ClickRequestsContext`. `TrackRow.Grid`/`Row` remain for eager surfaces.
Page-level loading still uses a 12-row `TrackRow.Grid` band (`LoadingRowBand`). `DetailPageReadiness` is unchanged.

### Slice 5 — `WaveeEqualizer` simplification (DONE, folded into slice 2)

Landed early because the bound number cell needed it — see slice 2's write-up above for exactly what changed in
`src/apps/Wavee/Components/Equalizer.cs`. Nothing left to do here.

## Probe instructions (unchanged from the task brief — none of the five slices have been run through it yet)

Slice 4 has landed — the probe is now the right measurement:

```powershell
dotnet build src/apps/Wavee/Wavee.csproj -c Release -o src/apps/Wavee/bin/verify3/Release/net10.0 -p:EventSourceSupport=true
```
then (Bash, since the harness reads env vars):
```
WAVEE_TRACKLIST_SCROLL_PROBE=1 FG_RENDER_CENSUS=1 FG_RENDER_CENSUS_MIN=8 FG_LAYOUT_DIAG=1 \
  src/apps/Wavee/bin/verify3/Release/net10.0/Wavee.exe --fake
```
Exits itself in ~40 s; capture stdout+stderr to a file. Read for "painted frames=… over 8.33ms=…", "FrameMs:
p50/p95/max", "alloc/frame", the "Worst 10 frames" table, and `[render-census]` lines. The CSV lands in
`%LOCALAPPDATA%\Wavee\logs\tracklist-scroll-probe.csv` (columns include `hotPhaseAllocBytes`, `uiAllocBytes`,
`comps`, `measure`/`arrange`/`textMiss`).

**Baseline (before ANY of P5, from the task brief, itself sourced from the engine progress doc's P4 numbers):**
recycle frames (`comps=5`) 4.3–5.5 ms, ~250 KB UI allocation/frame, one 41 ms reveal frame.
**Target:** recycle frames `hotPhaseAllocBytes == 0`, `comps == 0` (no component renders on a recycle), frame
≤ 3 ms; reveal frame ≤ 8 ms with no `BoundRowContent`/`RowOrRecContent` renders in the census.

If the probe reports "wheel routing FAILED"/`endOff=0`, delete `%LOCALAPPDATA%\Wavee\WaveeMusic\session.json` and
rerun (a restored artist route steals the wheel in the probe).

## Engine gaps noted so far

None. `BoundItemScope<T>(RowScope, IReadSignal<T>)` exists as a readonly record struct.

## Follow-ups for the test / modules agents (this slice did not edit those trees)

- Drop `<Compile Include>` of the deleted `ArtistShelfDemand.cs` and `DetailRevealRamp.cs` from `Wavee.Tests.csproj`.
- `DetailRevealRampTests.cs` and `SidebarRebuildGatingTests` ArtistShelfDemand cases must go or be rewritten.
- `Features/Modules/ModulePage.cs` `RampedRows` still references `DetailRevealRamp` (compile break).
