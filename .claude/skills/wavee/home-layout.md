---
name: wavee-home-layout
description: Use when changing Home — the page under src/apps/Wavee/Home/ (HomeScreen, the facet row, the zone planners/renderers, the daylist card, the customizer) — or its layout document (home-layout.json v2, LayoutFile.cs).
---

# Home

Home is a ground-up rebuild, now on its **seventh pass — the prototype's design
on the shared controls** (`docs/plans/wavee/home-rebuild-implementation.md`, last
section; the fifth pass set the stock-control foundation, the sixth and seventh
extend the shared controls, never Home-private copies). Every earlier
hand-rolled surface (a hand pivot, hand chapter bands with pips/parallax, a
width-query forms framework, private card templates + hover recipes, a hand
skeleton, a virtualized `ItemsView` of zones with custom lanes/extents) was
rejected and replaced with the same stock pieces the app's other native pages
(Artist, Album, Browse, Search, Show) already use: a plain `ScrollView` column of
`PagedShelf`/`GridEl` sections built from `Controls.ShelfCard`/`MediaRow`/
`ModuleHeader`/`SettingsCard`/`Ui.Card`. **Read the plan's fifth-, sixth- and
seventh-pass sections before touching anything below** — the fifth has the zone →
control table, the sticky-header mechanics and the deletion list; the seventh has
the current page tree, the daylist wireframe and the root-cause table.

## Where things live

All under `src/apps/Wavee/Home/` (namespace `Wavee.HomeUi`):

| File | What |
|---|---|
| `HomeScreen.cs` | the page root, `Shell.RouteKind.Home`'s factory: `ZStack` over the shell tint, a `ScrollView` column (facet row, the sticky-clipped zone column), the data half (`Props`/`Prefetch`/`Compute`/`Verdict`/`Demand`/`SelectFacet`/`Seed`/`BuildWash`). **No greeting headline** — the facet row IS the page title; the greeting lives only in the daylist eyebrow. `Seed` (All) = Daylist + RecentGrid + a cover shelf with a lead. The facet dim host binds Opacity/HitTest only (no pointer handlers — see Hover) |
| `Facets.cs` | the facet pivot's pure data (facet ids, ordering) |
| `Facet.Rules.cs` | `FacetDimPlan` — the switch-motion opacity/duration table (dims via `Design.Motion.Fast`, the app's 167 ms rung, not a Home-local token) |
| `Facet.UI.cs` | the facet row = the page title: `SelectorBar.Create(..., style: Design.FacetTitleStyle)` (engine E28 `SelectorBarStyle`: 28/36 display, no pill, subtle hover plate; `SelectorH` 40) + the follow toggle + the 3-px indeterminate progress bar + the failed-facet `InfoBar` |
| `Model.cs` | `Zone`/`ZoneKind`/`ZoneCluster` — the pure presentation shape a page renders, built from the section reader over the entity tables |
| `ZonePlanner.cs` | `SectionRoles` (per-section role classification: Recents, Daylist, Personal, Radio, Cluster, WideEditorial, Releases, Browse, Generic) + `ZonePlanner.Plan`/`PlanAll` (bands → ordered `Zone[]`); `RecentsCap` = 8 |
| `Zones.Rules.cs` | pure per-zone rules `Zones.UI.cs` renders from: `ReleaseListRules` (type badge + trailing date column), `ShelfLead` (merges a zone's separately-carried lead card back into its item list) |
| `Zones.UI.cs` | every zone body over stock controls — `ModuleHeader`/`PagedShelf` for cover shelves (verbatim `Browse.Page.cs`'s pager/pips idiom), `GridEl` of `Controls.MediaRow` for Recents/Release/Cluster/Episode rows, `GridEl` of `SettingsCard` for Browse/Charts tiles, `Controls.Vacancy` for empty/failed. Headers pass `open: null` (no chevron after the subtitle; See all / Listening history carry the drill); tools = See all · divider · ‹ pips ›; a 2-span lead is never circular (`CoverShape.IsCircular`). `ShelfChapter` and `RecentGrid` carry a `SkeletonProxy` = their own static tree builder (see Skeletons) |
| `Daylist.UI.cs` / `DaylistSource.cs` | the daylist card and its data source. A padless `Ui.Card` (`ClipToBounds`, 8-DIP corners, MinHeight 320) around a ZStack: the header art (`Controls.CoverFill` of `DaylistArt.Of`) across the WHOLE card, the artist hero's horizontal veil (`Palette.ArtistHeroVeil` in its NaN stretch arm), then the copy column on the left (`SpaceBetween`: eyebrow with the greeting, `Design.Type.HeroTitle` ≤ 2 lines, tags, meta, actions on one row, the clock at the foot) at `DaylistForm.CopyWidth(inner)` (half the content width within [340, 560]; the whole width below `SplitMin` 532) inside `Daylist.CopyPadding`. No art url ⇒ no art, no veil. `DaylistClock` = stock `ProgressRing` + two-line countdown, five stock determinate `ProgressBar`s (done/current/future) + daypart labels. `DaylistCard` has a `SkeletonProxy` (same tree, placeholder art slot, no veil) |
| `Time.cs` | `DaypartRules` (the five-segment daypart timeline the daylist card's `DaylistClock` renders + `FormatCountdown`/`Countdown`), `RecentsWeek`/`WeekSummary` (the history tile's 7-day strip, restored in the sixth pass), `WhenCaption` (Recently-played / episode-row "when" ladder), `DaylistNext` (the next-window arrival caption) |
| `EpisodeCaption.cs` | episode-row caption formatting |
| `PodcastPlanner.cs` | the Podcasts facet's own regroup (a different algorithm from `ZonePlanner`) — rendered by `Zones.UI.cs` now, no separate `Podcasts.UI.cs` |
| `Items.Rules.cs` | small pure item-shaping rules `Zones.UI.cs` calls into |
| `RecentsCells.cs` | the Recently-played grid's cell shape (kept as-is across the fifth pass) |
| `Reveal.cs` | `ScreenLoad` (a facet's `Loadable<ScreenModel>` state) and `WashPick` (shell wash accent source order: daylist accent → first cover accent → fallback) — card open/play routing lives OFF this file now (below) |
| `SectionScreen.Rules.cs` / `SectionScreen.UI.cs` | the "See all"/charts drill page for a Home or Browse section (`Shell.RouteKind.HomeSection`/`BrowseSection`); a bound `ItemsView` over `RepeatLayout.GridFit` (its own grid, unrelated to Home's own zone column). It demands the section WHOLE (`SectionFields.Identity \| Whole`, one `Home.EnsureSection`) and renders what the row holds, skeleton while the walk is in flight; the QUERY layer walks `homeSection`/`browseSection` to the server's end (`Spotify/Spotify.Api.Browse.cs`). No near-tail hook, no append, no page-side paging — never add one back |
| `Customize.UI.cs` | `CustomizeScreen` — the full-page customizer (`Shell.RouteKind.HomeCustomize`), a `Reorderable` list of `ZoneRow`s |
| `LayoutFile.cs` | the v2 layout document (below) |

**Deleted in the fifth pass, not coming back**: `Forms.cs` (the width-query forms
framework), `HomeTok.cs`/`HomeText.cs` (Home's private token/text vocabulary —
use the app's own `Spacing`/`Radii`/`Design.Type`/`Design.Motion` instead, see
`Platform/Design.cs`), `Cards.UI.cs` (private card templates — use
`Entities/Browse.Cards.cs`'s `HomeCards`/`HomeCardNav`, the same ones Browse and
Search ride), `Chapter.UI.cs` (hand chapter bands — `ModuleHeader` now), `Skeleton.UI.cs`
(hand skeleton — `SkelRegionEl`/`Skel.Region` now), `Podcasts.UI.cs` (folded into
`Zones.UI.cs`), `FacetPivot.UI.cs` (replaced by the ~80-line `Facet.UI.cs`),
`Zones.Rules.cs`'s `ZoneRows`/`ZoneEnter` (the virtualized-list row-extent seed and
entrance stagger — there is no more `ItemsView` of zones to seed), `Reveal.cs`'s
`CardNav` (superseded by the app-wide `HomeCardNav`). (`Time.cs`'s `RecentsWeek`/
`WeekSummary` were deleted here and restored in the sixth pass for the history tile.)

**Card open/play routing is NOT a Home-local concern any more.** Every Home card
tap goes through the same `HomeCardNav`/`HomeCards` that Browse and Search use
(`Entities/Home.Rules.cs` §12 for the CORE half — `RouteFor`/`SectionRoute`/
`OneCardOpensCard` — and `Entities/Browse.Cards.cs` for the UI half —
`Open`/`Play`/`DragOf`/`MenuOf`/`ShelfCell`/`PlainText`). Do not reintroduce a
Home-private card-nav type.

`Entities/Home.cs` and `Home.Rules.cs` still hold what the page reads OFF THE
TABLES: `HomeCard`/`HomeCardKind` (the entity handle), `HomeSectionView` (the
lossless per-section ledger), `HomeCardText`/`HomeCardAccent`, the section
routing rules, `HomeBrowseCards`. (Section paging is the query layer's: `SectionPaging` + `BrowseWalk`.)

## The layout document (home-layout.json v2)

`LayoutFile.cs` is a document, reducer, wire and store — no reference to the old
pre-rebuild `HomeLayoutDoc`/`HomeModuleSpec`/`HomeLayoutReducer`/`HomeLayoutStore`/
`HomeLayoutWire`/`HomePreferences`, only the same folder and the same atomic-write
mechanics (`%LOCALAPPDATA%\Wavee\WaveeMusic\home-layout.json`, `.tmp` →
`File.Replace(...,.bak)`, source-generated `System.Text.Json`). A v1 file is an
accepted break: read as a version mismatch and replaced by the v2 default, never
migrated.

- `LayoutZone` (`Daylist`, `Recents`, `MadeForYou`, `NewMusic`, `Releases`,
  `BecauseYouLike`, `JumpBackIn`, `Radio`, `Browse`) is the persisted, reorderable
  identity — append only, never rename. It is distinct from `ZoneKind`
  (`Home/Model.cs`): several `LayoutZone`s can render as the same `ZoneKind`.
- `LayoutDoc` (`Version`, `Zones: IReadOnlyList<LayoutEntry>`, `UpdatedAtMs`) is the
  in-memory shape; `LayoutEntry(Kind, Visible)` is one row.
- `LayoutCommands.{Toggle, Move, Reset}` build a `LayoutDoc → LayoutDoc` reducer
  function; there is no polymorphic command hierarchy.
- `LayoutStore` is the file half: `Load()`/`Commit(doc)`, fault-classified
  (`LayoutReadFault`, `LayoutSaveFault`), fail-soft (corrupt/too-new/unreadable →
  default in memory, file untouched, writes blocked).
- `HomeLayout` is the one process-lifetime owner (`HomeLayout.Slot`, a
  `Context<HomeLayout>` with no default — a screen mounted without a provider
  fails loud): a `Signal<LayoutDoc>` plus `Dispatch(reduce)`, which reduces,
  publishes, and commits off the UI thread. Both `HomeScreen` and `CustomizeScreen`
  read the SAME instance via `UseRequiredContext(HomeLayout.Slot)`, so a customize
  edit is visible on Home the same frame it commits — no second private copy, no
  file I/O in `Render()`.

## Sticky headers — the app's own no-background idiom

Home reuses the pattern already in `Browse.Page.cs`/`Artist.Page.cs`: a sticky
header paints NOTHING; the content under it clips to the header's lower edge with
`.StickyClip(inset)` plus a top `EdgeFadeSpec(EdgeMask.Top, band) { WhileStuck =
true }`, so it dissolves into the header instead of being guillotined. The facet
row pins at the page top (`.Sticky(0)`); each zone's `ModuleHeader` pins at
`facetRowH` scoped to that zone (`ScrollScope = zone.Key`) so the next zone's
header pushes the previous one out at its section end. No acrylic, no compact
band, no parallax.

## Skeletons

The cold load is derived from the real tree (`SkelRegionEl` + `Seed`), but the
engine `SkeletonDeriver` does not render components: any `Embed.Comp` inside a
skeleton-derived tree without a `SkeletonProxy` collapses into ONE 160×10 bar
(the seventh pass's "greeting + thin bars" cold load). Give every such component
a `SkeletonProxy` that returns its real tree — the same static builder `Render`
calls — so nested proxies (e.g. `PagedShelf`'s card proxy) are reached.

## Hover

Never put pointer handlers (`OnPointerDown` etc.) on a page-wide wrapper: it
becomes an interactive hover scope over the whole column, and every card→gap→card
move flips it and re-walks hover over the column (the seventh pass's hover-lag
root cause). Cards hover **fill-only**, app-wide via the shared `Controls.Art.cs`
recipe: an 83 ms subtle backplate, no lift, no shadow; the 1.02 cover zoom only on
non-square covers. Do not add a Home-private hover recipe.

## How to add a zone

1. Add the `ZoneKind`/`LayoutZone` (append only) in `Home/Model.cs`/`LayoutFile.cs`.
2. Teach `SectionRoles.Of` (`ZonePlanner.cs`) to classify the sections that produce
   it, and `ZonePlanner.Plan`/`PlanAll` to place it.
3. Render it in `Zones.UI.cs` over the stock control from the plan's zone → control
   table (a `PagedShelf`, a `GridEl` of `MediaRow`/`SettingsCard`/`Ui.Card`, never a
   hand-rolled cell) — add any small pure shaping rule it needs to `Zones.Rules.cs`.
4. Add it to `LayoutZones.DefaultOrder`/`KindName`/`TryParse` (`LayoutFile.cs`) so
   the customizer can toggle/reorder it; old documents pick a new kind up
   automatically as visible (no migration).
5. Add a label in the customizer (`Customize.UI.cs`).

Do not hardcode a new zone into a static row table, do not invent a new width-query
form or a Home-private card template — it goes through the planner and renders
with the stock controls, like every other zone.
