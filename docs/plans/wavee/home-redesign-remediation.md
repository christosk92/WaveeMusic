# Home redesign remediation — anti-patterns, root causes, and the idiomatic rebuild

Audit date 2026-09-25. Scope: `src/apps/Wavee/Home/*.cs`, the Shell wiring for Home (`Shell/Shell.cs`,
`Shell/Shell.Host.cs`, `Shell/Shell.Palette.cs`, `Entities/Home.Host.cs`), the engine additions made for Home
(Segmented presets, `PagedShelf` `ShelfController`/`leadSpan`, `ProgressBar` stretch, `UseScrollThreshold`,
`ToggleButton.Controlled`, `AccentSet`), the tests under `Wavee.Tests/HomeUi/`, and the data pipeline behind the zone
planner (measured against the owner's two live captures; shapes only, no data copied here).

Rules applied: `CLAUDE.md` (no legacy paths, no band-aids, no source-text tests, props freeze at mount, every fix
references its issue), the engine's `fluentgpu` skill rules 1–14, `docs/guide/pitfalls.md`, `control-fidelity.md` §6
("could the caller write `Parts[PartX] = …`? then they must"), `component-props-contract.md` (four delivery
mechanisms; the retained-shelf authoring section), `docs/design/subsystems/{layout,scroll,virtualization}.md`,
`docs/guide/scrolling.md` ("no app-side workarounds for engine scroll behaviour"), and the owner's standing notes
(fix cost at the source; a redesign is a ground-up presentation rebuild over the kept data pipeline; delete outright).

Reference pages used as the idiom baseline: `Entities/Artist.Page.cs` (plain `ScrollView` column, one `ScrollHandle`,
`.Sticky(…, engaged:)` sentinel, `Demand()` in an effect), `Entities/Artist.Reader.cs` (`ItemsView.CreateBound` spine
with a sticky persistent prefix), `Entities/Album.Page.cs` / `Show.Page.cs` (`Entities.Ensure` + `EnsureEdge` demand,
`SkelRegionEl` with `Pending:`/`Failed:` thunks over table readiness — `Detail.UI.cs:905-919`), and the engine
gallery's `ExternalChapterHeader` (`CollectionsMenusPages.cs:430`).

---

## 0. The verdict in one paragraph

The new Home is a faithful transcription of a 1440×900 pixel sheet into code: it measures its own width, looks the
number up in hand-written tier tables (`GridFit`, `PodcastLayout`), hands every template an explicit `width`, and
remounts shelves when the tier flips — while the engine underneath it already solves every one of those problems
(`PagedShelf` self-fits from `minCardW`/`maxCardW` and can size cards to their arranged cell; `GridEl.MinColWidth`
is `repeat(auto-fill, minmax())` computed at layout time; `AspectRatio` derives the missing extent; flex `Grow`
shares a row). The loading path is idiomatic in name (`Loadable` + `Skel.Region`) but hand-driven by a state-machine
effect that writes signals it reads, a timer that sequences a fade the engine's `Exit` channel does natively, and a
`--fake` timing branch in production code. Whole-page re-renders are wired to `Playback.Current`, `Recents.Changed`
and every facet-phase flip because "live" values are read in `Render()` instead of bound per node. Wrapper boxes
carry `Key`/`Margin`/`Shrink`/`Animate`/sticky around component embeds because the engine silently drops
base-`Element` props on a `ComponentEl` and has no `Parts[PartRoot]` use in the app. ~5,100 lines of the old Home
presentation are still in the tree "for the deletion agent". And the planner's zone model is built for a feed the
live server does not send: the decoder drops `header_image_url_desktop` for every non-daylist playlist (so
`WideTiles` and the Discover-Weekly lead can never fire), and the 15–20 one-card `HomeFeedBaselineSectionData`
sections that make up half of every Home answer are folded into a single four-row "More for you" card because the
app never issues the `feedBaselineLookup` query that gives each of them its five preview tracks.

The remediation is not a patch pass. Per the owner's rule it is a second ground-up presentation pass on the same
kept data pipeline, plus four small engine changes that remove the reasons the wrappers and width math existed, plus
the two data-pipeline fixes that make the planner's model true.

---

## 1. Findings

Severity: **S1** breaks a binding rule or user-visible behaviour · **S2** violates an idiom with a performance/maintenance cost · **S3** hygiene.

| # | Sev | Where | Anti-pattern | Rule it violates | Idiomatic replacement |
|---|---|---|---|---|---|
| F1 | S1 | `Screen.UI.cs:117,266-270,469,493` | `UseMeasuredWidth(4)` → `ContentW()` → `ZoneEnv.ContentWidth`/`PodcastEnv.Width` (fallback `ZoneMetrics.ContentWidth = 1316`). The whole page re-renders on every 4-DIP width step and every zone recomputes its columns app-side. | pitfalls "measured-bounds feedback loop"; layout is the engine's job (`layout.md` GridColCount, `FillRowVirtualLayout.Fit`) | No width in the app at all: shelves self-fit (`minCardW`/`maxCardW`, `cardWidthAgnostic: true`, `measured: true`); grids are `Ui.AutoGrid(minColWidth, gap, NaN, …)`; the daylist/podcast splits are flex `Grow`/`Basis` + `Wrap`. Delete `_width`, `ContentW`, `ZoneEnv.ContentWidth`, `PodcastEnv.Width`. |
| F2 | S1 | `Metrics.cs:233-260` `GridFit.Columns6/8/3/4Recents`, `CoverWidth`, `LeadSpan` | Hand-rolled breakpoint tables duplicating the engine's fit algorithm (`VirtualLayout.cs:477-494`) and auto-fill (`FlexLayout.cs:1426-1436`). | same | Delete `GridFit`. The lead-span half-row rule moves into the engine (E11). |
| F3 | S2 | `Metrics.cs:262-284` `ShelfPaging` | Re-states `FillRowVirtualLayout`'s paging math app-side; **dead** (0 production callers, 11 test references). | dead code; no duplicate engine rules | Delete + `ZoneMetricsTests` paging facts. |
| F4 | S2 | `Metrics.cs:139-228` `ZoneMetrics.Extent/HeaderExtent/TailExtent` | Virtualization pre-pass estimates for a `ZoneListLayout` that was never built (the list is a plain `ScrollView`). Dead. | dead code | Replaced by a width-free `ZoneRows.EstimateExtent(zone)` seed for `RepeatLayout.Extents` (§3.1). |
| F5 | S1 | `Items.UI.cs:159-238` `GridItem(card, width…)`, `WideLead(width,height)`, `WideTile(width)`, `DaylistCard(width…)`; `Podcasts.UI.cs:123,211,276` | Templates take a `width` and set `Width = width` on their root; a template cannot fill its cell, so every caller must compute the cell first. | "a template fills its slot" — `PagedShelf` `cardWidthAgnostic`; `GridEl` cells stretch | Templates are width-free: root `AlignSelf = Stretch, MinWidth = 0`; cover = `AspectRatio = 1` box; `DecodePx` from a constant bucket, not the width. Code in §3.3. |
| F6 | S1 | `Podcasts.Rules.cs:27-44`, `Podcasts.UI.cs:98-116` | `LeadWidthRatio = 428/1316`, `RowsWidth`, `ColumnWidth` — proportional width math from the sheet. | same as F1 | `HStack { Wrap = true }` with lead `Basis 320, Grow 1` + rows `Basis 480, Grow 2`; lead art `AspectRatio = 428/241`. Delete `PodcastLayout` (keep nothing; `SeeAllLabel` becomes a loc key). |
| F7 | S2 | `Items.UI.cs:418-475` `EpisodeRow(progressTrackWidth)` + `EpisodeCaption.ProgressWidth` | A progress hairline whose track and fill widths are computed from the caller's column width. | same | Track `Grow = 1`; fill/rest as two flex children with `Grow = fraction` / `Grow = 1 - fraction` (pure layout percentage). Delete `ProgressWidth`; keep `ProgressFraction`. |
| F8 | S1 | `Zones.UI.cs:319-333, 481-484` `ShelfHost` (`Key = "shelf:…:cols:leadSpan"`, `Margin.Top = -12`) | Remounts the shelf when the tier changes (throws away page/scroll/focus) to work around frozen `perPageOverride`/`maxColumns`; and a −12 DIP margin to cancel the shelf's `LiftClearance` for hover-lift cards Home does not have. | props-contract §4 (remount only on identity change); no band-aids | Never pin columns (E-owned fit); `ShelfLift.None` (E10) removes the clearance; `Parts[PagedShelf.PartRoot]` carries `ScrollScope`. Delete `ShelfHost`. |
| F9 | S1 | `SectionScreen.UI.cs:158,179-195` | `Responsive.Of(width => Grid(width, ready), fallback: 1316)` + `ItemsView.Create` (frozen template): columns computed at the **first** measured width and never reflowed ("known limitation, accepted"). | F1/F2; `ItemsView` has `RepeatLayout.GridFit(minCellWidth)` | `ItemsView.CreateBound<HomeCard>(BoundItems.From(_cards), …, RepeatLayout.GridFit(176f, 16f))`. §3.6. |
| F10 | S2 | `Zones.UI.cs:283,392,415,513`; `Podcasts.UI.cs:208,254,273` | `Ui.UniformGrid(cols…)` with `cols` from `GridFit`, or a hard `2`/`4`. | F2 | `Ui.AutoGrid(minColWidth, ZoneMetrics.GridGap, float.NaN, cells)`. |
| F11 | S3 | `Items.UI.cs:316` `HistoryItem` `Width = 320`; `Facet.UI.cs:453` `EmptyFacet` `Width = 480` | Fixed widths inside cells / centred blocks. | F5 | `HistoryItem` fills its grid cell; `EmptyFacet` `MaxWidth = 480, AlignSelf = Center`. |
| F12 | S1 | `Screen.UI.cs:161-225` `RunDataLoop` | A `UseSignalEffect` that reads `_switch` **and writes** `_switch`/`_load` (`:212,:223`), calls `Plan()` twice per publication (`:188` to count zones, `:253` to publish), and stamps versions by hand (`StampOf`). | reactivity "backwards write" tripwire; derive, don't re-publish | The screen model is a `UseComputed<ScreenModel?>` over the table signals; `SkelRegionEl(Pending:, Failed:, Content:)` reads it — the `Detail.UI.cs:905` idiom. §3.1. |
| F13 | S1 | `Screen.UI.cs:137-144` 83 ms `UseTimeout` + `FacetPhase.FadingOut`/`OutDone` | A timer sequences "old page fades out, then the new one mounts". | engine `Exit`/`Enter` channels + keyed reconcile do this; no timers as band-aids | Keyed facet content root with `Exit` (83 ms opacity) and `Enter` + `Stagger`; publish immediately on `Landed`. `FadingOut`/`OutDone` deleted from `FacetSwitch`. |
| F14 | S1 | `Screen.UI.cs:134`; `Facet.UI.cs:149,253` | Signal writes during `Render()` (`_selectedFacet.Value = …`, `selIndex.Value = selectedWord`). | rule 6 "never write a signal during render" | `_selectedFacet` deleted (consumers read `Switch.Value.Target`); the Segmented index is synced in a `UseEffect`. |
| F15 | S1 | `Screen.UI.cs:172-175` | `if (Platform.Args.Fake && …) concluded = false;` — a demo-latency branch inside the production data loop. | no behaviour switches in UI; fix at the source | `Entities.Fake.Home` delays `LiveAttemptConcluded` itself (`FakeHomeFacetTiming` moves into the fake fetch host). |
| F16 | S1 | `Screen.UI.cs:473-475` (`IsNowPlaying`, `RecentsWhen` read `Playback.Current.Value` in `Render`), `:513-516` (`RecentsCache` reads `Recents.Changed.Value`), `Zones.UI.cs:241` (`Library.IsSaved`) | Live values read in `HomeScreen.Render()` — every track change and every Recents write re-renders the **entire** page. | rule 1/3: show dynamic values via a bound prop; state lives as low as possible | Per-card binds: `BorderColor = Prop.Of(() => Playback.Current.Value.Equals(t) ? Tok.AccentDefault : ColorF.Transparent)`, badge `Visible = Prop.Of(…)` around the existing bound `Controls.Equalizer(IReadSignal<bool> playing, …)` (`Platform/Controls.cs:534`); Recents lives in the `RecentGrid` zone component; saved state binds the library signal; the daylist Play uses `Controls.PlayButton(Func<ColorF> accent, …)` (`Controls.Cta.cs:132`). |
| F17 | S2 | `Screen.UI.cs:465-498` | `ZoneEnv`/`PodcastEnv` rebuilt each render with 10 closures; `BuildZoneRows` allocates a `List` + 3 nested `BoxEl`s per zone (`Key`+`Animate` host → `DimWrap` → zone column). | per-render churn; wrapper boxes | Each zone is a `ZoneRow` component (`Embed.Comp(new ZoneProps(zone), …)`) — no env record; one dim host for the whole list; stagger via `Stagger`/`EntranceOptions`. |
| F18 | S1 | `Screen.UI.cs:432` `Facet.DimWrap` per zone | N wrappers each reading `state.Value` in render → page re-render per phase flip. | bind hot scalars (rule 5) | One host: `Opacity = Prop.Of(() => FacetDimPlan.Of(_switch.Value.Phase).Opacity)`, `HitTestVisible = Prop.Of(() => !_switch.Value.Dim)` (E15 makes it bindable). |
| F19 | S1 | `Screen.UI.cs:354-362` (plain `ScrollView` column) | ~10 zones × up to 20 cards all mounted: outside a virtual list the reconciler pins every `ImageEl` at `ImagePriority.Visible` (`Reconciler.cs:1997`), so ~100 covers decode at top priority on first paint; nothing is recycled. The plan (Appendix A) and the old Home (`Home.Page.cs:810 HomeLandingLayout : IMeasuredVirtualLayout`) both virtualized. | virtualization.md; the plan | `ItemsView.Create(zoneCount, i => ZoneRow(i), RepeatLayout.Extents(_extentOf, 320f), …)` with `ScrollOptions{ScrollKey, Handle}`, `CacheExtentPx`, `Entrance.StaggerColdRealize`. Wave-0 spike gates sticky-in-realized-row and nested shelf page retention (E17/E18). §3.1. |
| F20 | S2 | `Zones.UI.cs:154-164` (sticky host box around `Embed.Comp(ChapterHeaderProps…)`), `Platform/Controls.cs:51` `NoShrink`, `Concert.UI.cs:342-347` `FilterToken` host, `Zones.UI.cs:483` `ShelfHost` | Wrapper `BoxEl`s exist only to carry `Key`/`Shrink`/`Margin`/`Animate`/`ScrollEffects` around a component embed. Root cause: `Margin/Shrink/Grow/Animate` are `BoxEl`-only, and base-`Element` props (`ScrollEffects`, `Visible`, `Enter/Exit`) set on a `ComponentEl` are **silently dropped** — `MountComponent` (`Reconciler.cs:1108`) and the reuse branch (`:856`) never `WriteColumns` for the anchor. | control-fidelity §6 (Parts is the door); props-contract | Layout props → `Parts[Control.PartRoot] = b => b with { Shrink = 0f, Animate = … }` (the sanctioned door; every control here exposes `PartRoot`). Base props → E14 (anchor applies them). `ChapterHeader` becomes a plain element function (§3.2). Delete `NoShrink`, `ShelfHost`, the `FilterToken` host. |
| F21 | S1 | `PagedShelf.cs:502-508` `BindCard` | Shelf cards never wire `RowScope.OnInteraction`/`OnFocusChanged`; the card's own `Focusable`/`OnClick` fights `ItemsView`'s roving tab stop → no arrow-key navigation between cards (found in E7). | `SelectorVisualsBound.cs:22-27` contract | E9: the slot root consumes the scope; `PagedShelf.Create(…, onInvoke:)`; card templates drop `Focusable`/`OnClick`/`Role`. |
| F22 | S2 | `Model.cs:86-98` `SectionReader.s_rows/s_sections/s_scope` | Process-global memo dictionaries keyed by table slot with a scope-identity reset. | statics; memo belongs where the reader lives | Delete: planning runs once per version change inside the screen memo (F12); `SectionReader.Read/Of` allocate fresh arrays. `SeeAll` reads `new Section(slot).Id.Text` directly. |
| F23 | S2 | `Shell.Host.cs:57-83` `s_homeRouteArg`, `PublishHomeArg` (8 call sites); `Shell.cs:527-535` `SameSlot` `if (a.Kind == RouteKind.Home)` | A Home-only static dictionary of signals inside the Shell, and a hard-coded route special case in a table-driven system. | statics; one table, no third list | Route table gains `PlaceByArg` beside `KeyedByArg` (Home: slot ignores Arg, place honours it). The facet effect reads `Shell.Current` filtered by tab — or, if the Wave-0 spike proves `Flow.KeepAlive` re-runs `view` on a same-key token change, `Props(route.Tab, route.Arg)` is re-pushed and nothing else is needed. §3.10. |
| F24 | S2 | `Facet.UI.cs:171-189` | `Segmented.PartItem` closure counter to recover the item index and install per-item `OnHoverMove`/`OnPointerExit`/`OnFocusChanged` (3 closures × N items per render), relying on an undocumented apply order. | control-fidelity §6; no undocumented control internals | E13: `SegmentedOptions.OnItemHoverChanged`/`OnItemFocusChanged` + `Style.ItemGap`. |
| F25 | S2 | `Zones.UI.cs:174-181` `ChapterHeaderCore` `UseRef(new Signal<int>)` mirror + effect | `PipsPager` needs a mutable `Signal<int>`; `ShelfController.Page` is read-only, so a mirror signal is synced by effect (the gallery does the same — the engine gap is the precedent). | controlled-input contract | E12 `PipsPager.Controlled(count, selectedIndex, onSelect)`. `ChapterHeader` needs no hooks → plain function. |
| F26 | S2 | `Zones.UI.cs:213-218` | Chapter header always paints `Fill = Tok.FillCardDefault` ("the stuck header needs an opaque backdrop") — a permanent card fill instead of a `:stuck` restyle. | scroll.md §7 `engaged:` is the `:stuck` edge | `.Sticky(44, scope, engaged: stuck)` + `Fill = Prop.Of(() => stuck.Value ? Tok.SolidBackgroundBase : ColorF.Transparent)`, `BrushTransitionMs = Motion.ControlFast`. |
| F27 | S1 | `Facet.UI.cs:229-290` `FacetCompactBand` (`Opacity 0`, `HitTestVisible=false` when hidden), `FacetRow` always mounted | Both pivot and band are always in the tree; opacity is not presence, so the a11y requirement ("only one of pivot/band") is unmet; the engine has no a11y-presence flag. | `Visible` is presence (rule 5); design 06 §2.4 | `Flow.Show(() => shown.Value, band)` with `Enter`/`Exit` fades on the band's root; the pivot row gets `Visible = Prop.Of(() => !shown.Value)`-equivalent via the same signal (it is scrolled away anyway). |
| F28 | S2 | `Screen.UI.cs:93-108` `s_titles`, `Items.UI.cs`, `Facet.UI.cs`, `Podcasts.Rules.cs:66`, `Zones.Rules.cs:31-41`, `Time.cs`/`EpisodeCaption.cs` `Format`, `Customize.UI.cs:109-191`, `Shell.Palette.cs:105-111`, `Model.cs:196-201` | ~60 English literals with `// loc:` comments. The app already has the generated `Strings` class over `assets/loc/en-US.json` (`Strings.Nav.Home`, `Strings.Menu.MoveUp` are used **in the same files**). | app loc discipline; `localizing-the-control-kit.md` | `home.*` keys in `en-US.json`/`nl.json`/`ko-KR.json`; `Loc.Get(Strings.Home.X)` in render, `Loc.Bind` in binds, `Loc.Format` for the "when" ladder. §3.11. |
| F29 | S1 | `Screen.UI.cs:115`, `Customize.UI.cs:40-47` | `LayoutStore.ForApp().Load()` — file I/O inside `Render()` (`_layoutDoc ??=`) and a second private copy in the customizer; the two never sync, so Home shows a stale layout after customizing until the tab remounts. | no I/O in render; one source of truth (the sidebar pattern) | One `HomeLayout` service (`Signal<LayoutDoc>` + `Commit`) constructed in `Services`, provided at the root; both screens read it. |
| F30 | S2 | `Screen.UI.cs:392-408` `SeedModel()` | A fixed 4-zone seed (Daylist + Recents 7 + two 6-card shelves) that does not resemble the live first paint (the owner's All feed has **no** daylist section, a 20-card Recents, one ~19-card shelf, 4–5 generic shelves). | skeleton = "the same UI over blank data" | Seed = the facet's last published zone shape when known, else a generic `[RecentGrid 8, CoverShelf 6, CoverShelf 6]` (no Daylist). |
| F31 | S1 | `Spotify.Decode.Home.cs:446-453` | `header_image_url_desktop` is parsed for every playlist card but written only `if (daylist)` — so `HomeCard.HeaderImageUrl` is null for Release Radar / "Soundtrack your …" / Discover Weekly, and the planner's `WideEditorial` role and `IsDiscoverWeekly` lead can never fire on live data. | the planner models a feed the decoder does not produce | D1: write `Playlist.HeaderImage` for every card that carries the attribute; decode test. |
| F32 | S1 | `ZonePlanner.cs:149-176` `ClusterFold` + `Spotify.Api.cs Queries` (no `feedBaselineLookup`) | Every `HomeFeedBaselineSectionData` section arrives with ONE card; the desktop client enriches each with 5 preview tracks via `feedBaselineLookup` (hash `a950fb7c…`). Wavee never asks, so 15–20 baseline sections per facet collapse into one "More for you" card (11 dropped) and `music-following-chip` (16 untitled one-album sections) renders as nothing useful. | the planner's model vs the live feed | D2: `Queries.FeedBaselineLookup`, `Decode.FeedBaselineLookup`, `FetchEdge.HomePreviews` (one request per facet answer), `Relation.SectionPreviewTracks`, planner uses previews as cluster rows; Following = a release list. §3.12. |
| F33 | S2 | `ZonePlanner.cs:147` `ClusterFold.Prefixes = ["More like ", "For fans of "]`, `PodcastPlanner.cs:33-45` `PodcastPhrases` | English title matching; the owner's own feed carries Dutch titles ("Het is weekend!"). | locale-agnostic planning | With D2 every baseline section is its own cluster (no title parsing needed for grouping); podcast phrases stay documented as best-effort with a locale-neutral fallback (already the case) — logged in Q4. |
| F34 | S1 | `Entities/Home.Page.cs` (1627), `Home.UI.cs` (1378), `Home.Cards.UI.cs` (997), `Home.Artists.UI.cs` (770), `Home.Customizer.cs` (353), the presentation halves of `Home.Rules.cs`/`Home.cs`/`Home.Host.cs` (`LandingPageFor`/`SectionPageFor`/`CustomizerPageFor` "left in place (unused) for the deletion agent", `Home.Host.cs:367-369`), `.claude/skills/wavee/home-layout.md` (describes v1) | Legacy presentation left in the tree after go-live. | "No legacy paths. Replace outright; delete obsolete code." | Delete (§4). |
| F35 | S3 | `Facets.cs:378-382` `CompactBand.Next`, `Reveal.cs:22` `ScreenLoadState`, `FacetSwitch.Interact/Dismiss` (0 callers), `FacetPivot.Enabled` (0 callers), `Screen.UI.cs:227` `PlanZoneCount` | Dead duplicates of engine rules / unused pure rules kept "for testability". | dead code; no duplicate engine rules | Delete (`ScrollThreshold.Next` is the rule; `LoadState` is the enum). `Interact` is wired (pointer-down/scroll cancels a background swap) or removed with its rule — this plan wires it. |
| F36 | S3 | `Items.UI.cs:203,314,352,545,614`, `Podcasts.UI.cs:130,373,382`, `Zones.UI.cs:449` | Sheet magic numbers (`8f`, `-2f`, `20f`, `160f`, `0.4f`, `980f`) inline; a second type ramp (`ZoneMetrics.Type12…40`) beside `Design.Type`; `CardRadius = 8`/`CoverRadius = 4` beside `Radii.Card`/`Radii.Control`. | tokens over literals; one design vocabulary | Keep `ZoneMetrics` for genuine design constants (heights, motion), route radii/spacing through `Radii`/`Spacing`, add the four Home text styles to `Design.Type` (`HomeTitle14`, `HomeCaption12`, `HomeChapter20`, `HomeHero28`). |
| F37 | S3 | `SectionScreen.UI.cs:235` `Cursor = CursorId.Hand`; `Items.UI.cs:413` `NavTile` uses `Interaction.Card` (press scale) | Contradicts the file's own "fill-only hover, no hand cursor, no scale" rule. | consistency with the templates' convention | `NavTile` uses the shared fill-only recipe; the section card drops `Cursor`. |
| F38 | S2 | `Items.UI.cs:577-622` `DaylistTimer` | A 1 Hz `UseState` re-render of the ring + five segments + five labels to update one text run; the `char[8]` pooling is theatre when `new string` follows it. | rule 3: dynamic text is a bound `Text` | `Text = _countdown` (a `Signal<string>` written by `UseInterval`); the daypart bar reads a `Daypart` signal bound per segment `Fill`. |
| F39 | S2 | `Screen.UI.cs:487-489` `OnCardMore: null, OnGenreTag: null, BrowseChartTiles: null` | Inert affordances rendered as live buttons/links. | never ship a dead control | Wire: More → `Controls.MoreButton(requestsContext: true)` with the playlist uri; tags → `Shell.GoTo(Search, arg: tag)`; charts → 3 tiles from the Browse-owned chart deck (or drop the tiles from the sheet — owner call, default wire). |
| F40 | S2 | `Wavee.Tests/HomeUi/ZoneMetricsTests.cs`, `PodcastLayoutTests.cs`, `CompactBandTests.cs` | Assert sheet pixels at 1316 (`206`, `150.5`, `428`, `1332`) and breakpoint tables (`1100/900/700`), or a dead mirror of an engine rule. | tests pin decisions, not sheets | Delete with their subjects. Survivors listed in §5. |
| F41 | S3 | `SectionScreen.UI.cs:143-152` `SkelRegionEl(... Group: this ...)` and `HomeScreen`'s `Skel.Region` | Two different loading spines on two Home screens (`SkelRegionEl` thunks vs `Loadable`). | one idiom per app | Both use `SkelRegionEl` thunks over table readiness (the Detail idiom); `Loadable` is reserved for `UseResource`-shaped async values. |

---

## 2. Engine and shared-control changes (`C:\wavee\fluent-gpu`)

Every item: API, files, gates (`VerticalSlice` `Check(...)` in the owning suite + `Engine.Tests` where pure), docs
(`docs/guide/…` for usage, the owning `docs/design/subsystems/*.md` for the contract, `check-canon.ps1` after). The
rule of §6 applies: nothing below is a styling knob; each is a behaviour, a slot, a callback or a token bundle.

### E9 — `PagedShelf` cards join the `ItemsView` keyboard model (F21)

`src/FluentGpu.Controls/PagedShelf.cs` — `BindCard` becomes the slot root that consumes the `RowScope`:

```csharp
// PagedShelfCore<T>
Element BindCard(BoundItemScope<T> scope) => new BoxEl
{
    Direction = 1,
    Padding = _lift == ShelfLift.Elevate && (_measured || _rows == 1)
        ? new Edges4(0f, LiftClearance, 0f, ShadowClearance) : default,
    HoverElevatePaint = _lift == ShelfLift.Elevate && HoverElevate,
    // The ItemsView owns the single roving tab stop: it toggles this root's Focusable imperatively (SetSlotTabStop),
    // so the template never declares it. Press/Enter/Space → OnInteraction → the owner's onInvoke; focus → current.
    Focusable = false, Role = AutomationRole.Button,
    OnClick = () => scope.Row.OnInteraction(ItemContainerTrigger.Tap, KeyModifiers.None),
    OnFocusChanged = scope.Row.OnFocusChanged,
    FocusVisual = FocusVisual.TwoTone,          // gate.focus.ring.shelf-cell (already planned in E7)
    Children = [Embed.Comp(() => new ShelfCardSlot(this, scope))],
};
```

`Create<T>` gains `Action<T, int>? onInvoke = null` (re-pushed in `ShelfProps<T>`, ignored by the equality gate like
the other delegates; `_latest.OnInvoke` is what `OnItemInteraction` calls for `Tap`/`EnterKey`; `SpaceKey` calls
`onSecondary` if given, else `onInvoke`). `ListOptions<T>.IsItemInvokedEnabled = true` when `onInvoke != null`.
Arrow keys already move `current` through `ItemsView.OnRootKey`; the span-aware `ItemRect` from E7 gives Left/Right.

Gates (`ControlsSuite.PagedShelf.cs`): `gate.shelf.keyboard.arrows` (Right from card 0 focuses card 1's slot root),
`gate.shelf.keyboard.invoke` (Enter on the current card calls `onInvoke(item, index)` once), `gate.shelf.keyboard.tab-stop`
(exactly one focusable slot root at rest), `gate.shelf.keyboard.page-follow` (moving current past the page edge pages
the shelf — reuse `_pagerGoTo`), `gate.shelf.keyboard.alloc` (0 hot-phase bytes while arrowing).
Docs: `virtualization.md` §3.4 (bound rows own the scope), `component-props-contract.md` "Retained shelf authoring"
(the card template must not be focusable; `onInvoke` is the keyboard/pointer invoke), `docs/guide/components-elements-layout.md`.

### E10 — `ShelfLift` (F8)

```csharp
public enum ShelfLift : byte { Elevate, None }
PagedShelf.Create<T>(…, ShelfLift lift = ShelfLift.Elevate, …)   // frozen mount config, like `rows`/`snap`
```
`None`: no `LiftClearance`/`ShadowClearance` padding on the slot root, `HoverElevatePaint = false`, so a headerless
shelf's `PartRoot` sits flush under an external header. Behaviour config (the `Expander.Options.AnimateContentResize`
precedent), not a Parts style. Gate: `gate.shelf.lift.none-flush` (root top == first card top), `gate.shelf.lift.elevate-unchanged`
(byte-identical tree with the default). Docs: PagedShelf "HEADERLESS SHELVES" note rewritten — the −12 margin advice is deleted.

### E11 — lead span half-row rule (F2)

`src/FluentGpu.Engine/Scene/VirtualLayout.cs` `FillRowVirtualLayout`:
```csharp
// A lead that would take half the visible row or more reads as a broken shelf, not a lead: honour the span only
// while at least span+1 ordinary cells remain beside it. The app passes leadSpan: 2 unconditionally.
int EffectiveLeadSpan => Rows == 1 && LeadSpan > 1 && PerPage > 2 * LeadSpan ? LeadSpan : 1;
```
Tests: `Engine.Tests/VirtualLayoutLeadSpanTests.cs` (`perPage 6 → 2`, `5 → 2`, `4 → 1`, `3 → 1`, page count and
`FirstItemOfPage` follow the effective span); gate `gate.shelf.lead.half-row`. Docs: `virtualization.md` E7 paragraph.
(The sheet wanted 8-up leads only at ≥ 6 columns; `5 > 4` shows one at 5. Accepted: "pixel numbers are a target".)

### E12 — `PipsPager.Controlled` (F25)

```csharp
public static Element Controlled(int count, int selectedIndex, Action<int> onSelect, TemplateParts? parts = null,
                                 int maxVisiblePips = 5, …same tail as Create…)
```
Value-controlled sibling of `Create` (the `ToggleButton.Controlled` precedent): no internal selection signal; a click calls
`onSelect(i)`; `onReselect` semantics fold into `onSelect` (a click on the selected pip still calls it — the shelf uses
it to re-snap). Gate: `gate.pips.controlled.{no-state,reselect}`. Docs: components guide, controls.md pager entry.
`ShelfController` is unchanged; `ChapterHeader` binds `controller.Page` through a `Prop` (§3.2).

### E13 — `Segmented` item edges and gap (F24)

```csharp
public sealed record SegmentedOptions {
    …
    public Action<int>? OnItemHoverChanged { get; init; }   // index, -1 = none; fires on pointer enter/leave only
    public Action<int>? OnItemFocusChanged { get; init; }   // index, -1 = none; fires on focus edges only
}
public sealed record Style { …; public float ItemGap { get; init; } = 0f; }   // token bundle, not a part style
```
Wired where `OnClick`/`OnKeyDown` already are (`Segmented.cs:263-266`); `ItemGap` is the root's `Gap`. Gates:
`gate.segmented.item-hover-edge` (enter → i, leave → -1, no duplicate fires), `gate.segmented.item-focus-edge`,
`gate.segmented.item-gap` (24 DIP between word boxes), `gate.segmented.byte-identical` (no options ⇒ unchanged tree).
Docs: the E3 paragraph in `components-elements-layout.md` gains the two callbacks; the "presets via Parts" rule stands.

### E14 — a `ComponentEl` applies its base-`Element` props to its anchor (F20)

`Reconciler.MountComponent` (`:1108`) and the `ComponentEl` reuse branch (`:856`) call a new `WriteAnchorColumns(node, ce, old)`
that applies exactly the base-`Element` subset (`ScrollEffects`, `ScrollScope`, `Visible`, `Transition`, `WhileHover/Pressed/Focus`,
`Enter`, `Exit`, `Stagger`, `Layout`, `WheelTarget`, `RelativeTo`, `MorphId`) to the anchor node — the same code
`WriteColumns` already runs for every other element type ("baked … for every element type", `:4595`). Layout-shape
props stay `BoxEl`-only; the sanctioned door for those on a control is `Parts[PartRoot]`. DEBUG: `ReuseGuard.AnchorPropDropped`
is deleted once this lands (there is no longer a dropped case). Gates: `gate.reconcile.componentel.sticky` (a `.Sticky(0)`
on `Embed.Comp(...)` pins), `gate.reconcile.componentel.visible` (`Visible = false` collapses the anchor), `gate.reconcile.componentel.exit`
(a keyed `Embed.Comp` with `Exit` orphan-fades), `gate.reconcile.componentel.alloc`. Docs: `reconciler-hooks.md` §0bis,
`component-props-contract.md` (a new "Base props on the embed" paragraph), pitfalls row "I set `.Sticky` on a component and nothing happened".

### E15 — `BoxEl.HitTestVisible : Prop<bool>` (F18)

Bindable like `Visible`; a resolved `false` clears `NodeFlags.HitTestVisible` on the node without a re-render. FGRP007
requires the initializer (`= true`). Gate: `gate.hit.bindable` (a bound flip is compositor-only: `FrameStats.Rendered == false`).
Docs: `input-a11y.md` hit-test section, components guide table.

### E17 / E18 — Wave-0 spike gates for the virtualized zone list (F19)

Written **before** any app code moves, as `VerticalSlice` checks in `ScrollEffectsSuite`/`ControlsSuite`:
- `gate.scroll-effects.sticky-in-realized-row`: a `.Sticky(44, scope: rowKey)` header inside a realized `ItemsView.Create`
  row (not the persistent prefix) pins while its row is in view and releases at the row's end. If it fails, the fix is
  in `ScrollEffectEval`/the item-band recorder (one transform owner: the sticky offset composes over the row's
  virtual translation), and it is engine work in Wave 1 — never an app-side workaround.
- `gate.shelf.controller.rebind-restores-page` (E18): `ShelfController.Bind` after a remount re-applies `PageState`
  (`GoTo(Page.Peek())` before the first publish) so a shelf recycled out of the window and back returns to its page.
- `gate.items.nested-shelf-realize-alloc`: realizing a row that contains a `PagedShelf` allocates only on the
  reconcile edge (steady frames 0 bytes).

### Shared app control changes (`src/apps/Wavee/Platform/Controls*.cs`)

- Delete `Controls.NoShrink` (F20). Callers use `parts: Controls.RootNoShrink` — a `static readonly TemplateParts`
  with `[ToggleButton.PartRoot] = b => b with { Shrink = 0f }`.
- `Controls.Artwork(url, w, h, corner)` keeps its fixed-size overload for rows (48/56 art) and gains the fluid form
  `Controls.Cover(url, float aspect, float corner, float decodePx)` = an `AspectRatio` box with an `ImageEl` inside
  (used by every Home card, the section grid and — later — the Browse tiles).
- `Concert.UI.cs:342-347` `FilterToken`: `ToggleButton.Controlled(label, selected, _ => onClick(), parts: s_tokenParts) with { Key = "filter-token:" + label }`
  where `s_tokenParts[PartRoot] = b => b with { Shrink = 0f, Animate = s_tokenReflow }`. No host box.

---

## 3. App changes per file

### 3.0 Component tree after remediation

```
HomeScreen (Home/Screen.UI.cs)                    per tab: _switch, _scroll, _shown (HashSet<string>), _controllers → gone (per zone)
├─ Palette.ShellTint                              wash ← WashPick(model.Zones[0])  (bound, unchanged)
├─ SkelRegionEl(Pending: !ready && !failed, Failed: failed, Content: FacetContent, ShimmerSource: Seed)
│   └─ BoxEl Key="facet:"+facet  Enter(250ms, dy 8)  Exit(83ms opacity)            ← the swap IS the reconcile
│       └─ BoxEl (dim host)  Opacity=Prop(FacetDimPlan)  HitTestVisible=Prop(!Dim)  OnPointerDown → Interact
│           └─ ItemsView.Create(n+2, RowAt, RepeatLayout.Extents(ExtentOf, 320), ListOptions{ ScrollKey, Handle, CacheExtentPx 900, Entrance.StaggerColdRealize })
│               ├─ [0] FacetRow (Segmented pivot preset · FollowingToggle · BusyBar · FailureBar)
│               ├─ [1..n] Embed.Comp(new ZoneProps(zone, facet), () => new ZoneRow())  Key="zone:"+zone.Key
│               │      ├─ ChapterHeader(…)  .Sticky(44, scope: zone.Key, engaged: stuck)     ← plain element, no hooks
│               │      └─ body: DaylistCard | AutoGrid(RowItem…) | PagedShelf(self-fit, lift None, onInvoke) | AutoGrid(ListRow/NavTile/EpisodeRow…) | PagedShelf(ClusterCard…)
│               └─ [n+1] Tail: CustomizeLink (All only)
└─ Flow.Show(() => bandShown.Value, FacetCompactBand)   Enter 167 / Exit 83     ← presence, not opacity
```

Wireframe at 1440×900 is unchanged from the implementation plan Appendix B; at 1000 wide the same tree yields 5-up
shelves, a 3-column Recents grid and a 2-column cluster shelf page with **no code path knowing the number 1000**.

### 3.1 `Home/Screen.UI.cs` — `HomeScreen`

Keep: `PageFor`, the facet history/deep-link contract, `Prefetch`, `RememberFollowing`, `SelectFacet` (minus the
FadingOut branch), `BuildWash`, `EmptyFacetFor`, `FirstLoadFailed`. Everything else below is the replacement.

```csharp
public sealed class HomeScreen : Component
{
    public sealed record Props(int Tab);

    readonly Signal<FacetSwitchState> _switch = new(FacetSwitchState.Initial(""));
    readonly Signal<FacetWord[]> _words = new([]);
    readonly HashSet<string> _shown = new(StringComparer.Ordinal);      // FacetCache "revealed this session"
    readonly Dictionary<string, bool> _followingMemory = new(StringComparer.Ordinal);
    readonly ScrollHandle _scroll = new();
    readonly object _tintOwner = new();
    readonly Func<int, float> _extentOf;                                // cached: part of the layout's identity
    readonly RepeatLayout _zoneLayout;                                  // hoisted: RepeatLayout.Extents is stateful
    ScreenModel? _model;                                                // the last computed model (read by _extentOf)

    public HomeScreen()
    {
        _extentOf = i => ZoneRows.EstimateExtent(_model, i);
        _zoneLayout = RepeatLayout.Extents(_extentOf, estimatedExtent: 320f);
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        int tab = p.Tab;
        var layout = UseRequiredContext(HomeLayout.Slot);              // F29: one app-level document signal

        // ── route arg → facet (deep link, Back/Forward, palette). Reads Shell.Current filtered by tab: no Home-only
        //    static in the Shell (F23). A tab switch/restore re-publishes the same facet → Select is a no-op.
        UseEffect(() =>
        {
            var r = Shell.Current.Value;
            if (r.Kind != Shell.RouteKind.Home || r.Tab != tab) return;
            string resolved = FacetRoute.FacetOf(Entities.Strings.Resolve(r.Arg), _words.Value);
            if (resolved != _switch.Peek().Target) SelectFacetCore(resolved, pushHistory: false, tab);
        });

        // ── demand (the Artist/Album/Show idiom: Demand() in an effect, never in the model memo)
        UseEffect(() => Demand(_switch.Value.Target));

        // ── the screen model: a DERIVED value over the table signals. No publish, no stamps, no writes.
        var facetSig = UseComputed(() => _switch.Value.Published);
        var model = UseComputed(() => Compute(facetSig.Value, layout.Doc.Value));
        var verdict = UseComputed(() => Verdict(facetSig.Value, model.Value));   // ScreenLoadState

        // ── "shown this session" + words: written by an effect on the Ready edge (never in render)
        UseEffect(() =>
        {
            var m = model.Value;
            if (m is null) return;
            _shown.Add(m.Facet);
            _words.SetIfChanged(FacetPivot.Words(m.Chips, Loc.Get(Strings.Home.FacetAll)));
            _model = m;
        });

        // ── the landing edge: Loading/Refreshing → Idle once the target's model exists (F13: no timer, no FadingOut)
        UseEffect(() =>
        {
            var s = _switch.Value; var m = model.Value;
            if (s.Phase is not (FacetPhase.Loading or FacetPhase.Refreshing)) return;
            var h = Entities.HomeFeed(s.Target);
            if (!h.LiveAttemptConcluded) return;
            if (h.SectionCount == 0 && !h.Knows(HomeFields.Sections) && Spotify.Status.Value == Spotify.SessionPhase.Online)
            {
                _switch.Value = FacetSwitch.Fail(s, s.Target, out string revertTo);
                RevertRoute(revertTo, tab);
                return;
            }
            _switch.Value = FacetSwitch.Landed(s, s.Target, (float)_scroll.Offset.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out _);
        });

        var ctx = new FacetContext(_words, _switch, _scroll,
            Select: t => SelectFacet(t, tab), Prefetch: t => Prefetch(t), FollowingMemory: id => _followingMemory.TryGetValue(id, out bool on) && on);

        return Ctx.Provide(FacetCtx.Slot, ctx, new BoxEl
        {
            Grow = 1f, Direction = 1, ZStack = true,
            Children =
            [
                BuildWash(model),
                new SkelRegionEl(
                    Pending: () => verdict.Value == ScreenLoadState.Pending,
                    Failed:  () => verdict.Value == ScreenLoadState.Failed,
                    Content: () => FacetContent(model.Value ?? Seed(facetSig.Value), tab),
                    ShimmerSource: () => FacetContent(Seed(facetSig.Value), tab),
                    OnFailed: () => FirstLoadFailed(tab),
                    Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: this, SmoothResize: false),
                Flow.Show(() => _bandShown.Value, Embed.Comp(() => new FacetCompactBand())),
            ],
        });
    }
```

`Compute` is the old `PublishModel` minus the writes; it runs only when a signal it read changed (the Home row's
`Version`/`SectionVersion`, the `SectionCards` edge, the `HomePreviews` edge, the layout doc, `CultureEpoch` via
`Loc.Get`):

```csharp
    ScreenModel? Compute(string facet, LayoutDoc layout)
    {
        var h = Entities.HomeFeed(facet);
        _ = Home.Changed.Value; _ = Entities.Current.Edges.SectionCards.Changed.Value; _ = Entities.Current.Edges.SectionPreviewTracks.Changed.Value;
        _ = h.Version; _ = h.SectionVersion;
        if (!h.Knows(HomeFields.Sections)) return null;
        var inputs = SectionReader.Read(h);                                   // fresh arrays; no static memo (F22)
        var titles = ZoneTitles.Localized();                                  // Loc.Get per title → re-plans on culture change
        IReadOnlyList<Zone> zones = Facets.IsPodcasts(facet)
            ? PodcastPlanner.Plan(inputs, titles, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : ZonePlanner.Plan(inputs, facet, titles, facet.Length == 0 ? kind => layout.IsHidden(kind) : null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (zones.Count == 0) zones = [Zone.Empty(facet)];
        return new ScreenModel(facet, zones, SectionReader.Greeting(h), SectionReader.Chips(h));
    }

    ScreenLoadState Verdict(string facet, ScreenModel? m)
    {
        var h = Entities.HomeFeed(facet);
        var phase = Spotify.Status.Value;
        return ScreenLoad.Of(m?.Zones.Count ?? 0, h.Knows(HomeFields.Sections), h.LiveAttemptConcluded,
            phase == Spotify.SessionPhase.Online, phase is >= Spotify.SessionPhase.Offline and <= Spotify.SessionPhase.Minting);
    }

    void Demand(string facet)
    {
        var h = Entities.HomeFeed(facet);
        if (!h.Knows(HomeFields.Sections)) Home.EnsureFeed(h);
        else if (Entities.Current.Edges.HomePreviews.State(h.Slot) == EdgeState.Unknown && h.HasBaselineSections)
            Entities.EnsureEdge(FetchEdge.HomePreviews, h.Slot);              // D2: one lookup per facet answer
    }
```

The facet content is one keyed subtree; the swap is the reconciler's keyed replace with `Exit`/`Enter` — the 83 ms
fade-out and the 250 ms stagger-in are declarative, and `ZoneEnter`/`_swapAtMs` are deleted:

```csharp
    Element FacetContent(ScreenModel m, int tab)
    {
        string scope = UseContext(Shell.PageScrollScope);
        int n = m.Zones.Count;
        bool tail = m.Facet.Length == 0;
        return new BoxEl
        {
            Key = "facet:" + m.Facet, Grow = 1f, Direction = 1,
            Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Transition = MotionTokenDef.Eased(ZoneMetrics.MotionXFastMs, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Direction = 1,
                    Opacity = Prop.Of(() => FacetDimPlan.Of(_switch.Value.Phase).Opacity),        // compositor-only (F18)
                    HitTestVisible = Prop.Of(() => !_switch.Value.Dim),                            // E15
                    OnPointerDown = () => _switch.Value = FacetSwitch.Interact(_switch.Peek()),    // F35: wired
                    Children =
                    [
                        ItemsView.Create(n + (tail ? 2 : 1), i => RowAt(m, i, tab), _zoneLayout, new ListOptions
                        {
                            Grow = 1f, SelectionMode = ItemsSelectionMode.None,
                            KeyOf = i => i == 0 ? "facet-row" : i <= n ? "zone:" + m.Zones[i - 1].Key : "tail",
                            CacheExtentPx = 900f, RepaintBoundary = true,
                            Scroll = new ScrollOptions { ScrollKey = scope + "home:" + m.Facet, Handle = _scroll },
                            Entrance = new EntranceOptions { StaggerColdRealize = ZoneMetrics.EnterStaggerMs, ItemFadeFrom = 0f, ItemFlipFrom = new(0f, 8f) },
                        }),
                    ],
                },
            ],
        };
    }

    Element RowAt(ScreenModel m, int i, int tab)
    {
        if (i == 0) return Embed.Comp(() => new FacetRow());
        if (i > m.Zones.Count) return new BoxEl { Padding = ZoneMetrics.TailPad, Children = [CustomizeScreen.CustomizeLink()] };
        var zone = m.Zones[i - 1];
        return zone.Kind == ZoneKind.EmptyFacet
            ? EmptyFacetFor(m.Facet, tab)
            : Embed.Comp(new ZoneProps(zone, m.Facet), static () => new ZoneRow()) with { Key = "zone:" + zone.Key };
    }
```

The seed is facet-aware and never shows a Daylist the feed may not have (F30):

```csharp
    ScreenModel Seed(string facet)
    {
        if (_lastShape.TryGetValue(facet, out var kinds)) return ScreenModel.Blank(facet, kinds);   // written by the model effect
        return ScreenModel.Blank(facet, Facets.IsPodcasts(facet)
            ? [ZoneKind.EpisodeLead, ZoneKind.ContinueEpisodes, ZoneKind.ShowGrid]
            : [ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf]);
    }
```

`ZoneRows.EstimateExtent(model, i)` is a width-free seed (`RepeatLayout.Extents` measures the real row): `RecentGrid`
= `ChapterHeaderHeight + 2 rows × RowItemHeight`, shelves = `ChapterHeaderHeight + maxCardW + text block`, grids =
`ChapterHeaderHeight + ceil(n / 3) × row`. It is the only place a per-zone number survives from the old `Extent`.

Deleted from this file: `_load`, `_publishedStamp`, `StampOf`, `PublishModel`, `RunDataLoop`, `PlanZoneCount`,
`Plan`, `_width`, `ContentW`, `_controllers`, `ControllerFor`, `_layoutStore`/`_layoutDoc`, `_swapAtMs`, `RecentsCache`,
`BuildZoneEnv`, `BuildPodcastEnv`, `SeedModel`, `BuildZoneRows`, the `Platform.Args.Fake` branch (F15), `s_titles`.

### 3.2 `Home/Zones.UI.cs` — `ZoneRow`, `ChapterHeader`, self-fitting bodies

`ZoneEnv` is deleted. A zone is a component so its shelf controller, its `:stuck` signal and its own subscriptions
(Recents, now-playing) live with it:

```csharp
public sealed record ZoneProps(Zone Zone, string Facet);   // Zone is a record: a re-plan with equal content gates

public sealed class ZoneRow : Component
{
    readonly ShelfController _controller = new();          // stable for the row's lifetime (props-contract: a plain capture)
    readonly Signal<bool> _stuck = new(false);

    public override Element Render()
    {
        var p = UseProps<ZoneProps>();
        var zone = p.Zone;
        return new BoxEl
        {
            Direction = 1, ScrollScope = zone.Key, Gap = ZoneMetrics.ChapterHeaderBelow,
            Children = zone.Kind switch
            {
                ZoneKind.Daylist => [Zones.DaylistCard(zone)],
                ZoneKind.RecentGrid => [Header(zone, seeAll: Zones.GoRecents, pager: null, Strings.Home.ListeningHistory), Embed.Comp(new RecentGridProps(zone), static () => new RecentGrid())],
                ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf or ZoneKind.ShowGrid
                    => [Header(zone, Zones.SeeAll(zone), _controller), Zones.CoverShelf(zone, _controller, twoLineCaption: zone.Kind != ZoneKind.ShowGrid, eightUp: zone.Kind is ZoneKind.RadioShelf or ZoneKind.ShowGrid)],
                ZoneKind.WideTiles => [Header(zone, Zones.SeeAll(zone), _controller), Zones.WideTiles(zone, _controller)],
                ZoneKind.ReleaseList => [Header(zone, Zones.SeeAll(zone), null), Zones.ReleaseList(zone)],
                ZoneKind.ClusterCards or ZoneKind.PodcastGroups => [Header(zone, null, _controller), Zones.ClusterShelf(zone, _controller)],
                ZoneKind.BrowseTiles => [Header(zone, Zones.SeeAll(zone), null), Zones.BrowseTiles(zone)],
                _ => Podcasts.Body(zone, this, _controller),      // EpisodeLead / ContinueEpisodes / VideoTiles / EpisodeRows
            },
        };
    }

    Element Header(Zone zone, Action? seeAll, ShelfController? pager, string seeAllKey = Strings.Home.SeeAll)
        => Zones.ChapterHeader(zone.Title, zone.Subtitle, seeAll, pager, zone.Key, _stuck, seeAllKey);
}
```

`ChapterHeader` is a plain element function (no hooks after E12); sticky rides its own root; the `:stuck` fill is bound:

```csharp
public static Element ChapterHeader(string? title, string? subtitle, Action? onSeeAll, ShelfController? pager,
    string zoneKey, Signal<bool> stuck, string seeAllKey)
{
    var trailing = new List<Element>(4);
    if (onSeeAll is not null) trailing.Add(HyperlinkButton.Create(Loc.Get(seeAllKey), onSeeAll));
    if (pager is not null)
    {
        trailing.Add(new BoxEl { Width = 1f, Height = 20f, Shrink = 0f, Fill = Tok.StrokeDividerDefault });
        trailing.Add(new BoxEl { Children = [Prop.Of(() => PipsPager.Controlled(pager.PageCount.Value, pager.Page.Value, pager.GoTo))] });  // see note
        trailing.Add(IconButton.Create(Icons.ChevronLeft, pager.Prev, isEnabled: pager.CanPrev, style: s_chevron));   // IconButton takes IReadSignal<bool> isEnabled (E12b, one-line overload)
        trailing.Add(IconButton.Create(Icons.ChevronRight, pager.Next, isEnabled: pager.CanNext, style: s_chevron));
    }
    return new BoxEl
    {
        Key = "chapter:" + zoneKey,
        Direction = 0, Height = ZoneMetrics.ChapterHeaderHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Fill = Prop.Of(() => stuck.Value ? Tok.SolidBackgroundBase : ColorF.Transparent), BrushTransitionMs = Motion.ControlFast,
        Children =
        [
            new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, Children = TitleColumn(title, subtitle) },
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f, Children = trailing.ToArray() },
        ],
    }.Sticky(ZoneMetrics.CompactBandHeight, scope: zoneKey, engaged: stuck);
}
```

Note on the pager row: a `PipsPager` whose `count`/`selectedIndex` are **values** needs a tiny component to re-render
on page change; keep a 10-line `ShelfPips : Component` (`UseProps<(ShelfController)>`, reads `Page.Value`/`PageCount.Value`,
returns `PipsPager.Controlled(...)`). `IconButton.Create` gains an `IReadSignal<bool> isEnabled` overload (engine, one
line, `gate.iconbutton.enabled-bound`) so the chevrons never re-render.

Shelves never pin columns and never know a width:

```csharp
public static Element CoverShelf(Zone zone, ShelfController controller, bool twoLineCaption, bool eightUp)
{
    var items = ShelfLead.Merge(zone.Items, zone.Lead);
    return PagedShelf.Create(items,
        cardAt: (card, index, _) => index == 0 && zone.Lead is not null
            ? Items.WideLead(card, twoLineCaption)
            : Items.GridItem(card, twoLineCaption),
        pager: ShelfPager.None, controller: controller, lift: ShelfLift.None,
        minCardW: eightUp ? 140f : 176f, maxCardW: eightUp ? 176f : 236f,      // 8/6-up at 1316 exactly; the engine fits everywhere else
        gap: ZoneMetrics.GridGap, snap: ShelfSnap.Page, leadSpan: zone.Lead is null ? 1 : 2,   // E11 clamps the half-row rule
        cardWidthAgnostic: true, measured: true,
        keyOf: static (c, i) => c.IsBlank ? "blank:" + i : c.Uri,
        onInvoke: static (c, _) => Zones.OpenCard(c),
        parts: s_shelfParts);   // [PagedShelf.PartRoot] = b => b with { ScrollScope = … }  — set per zone via a small cache keyed by zone.Key
}
```

`WideTiles`: `minCardW: 380f, maxCardW: 520f`, `Items.WideTile(card)`. `ClusterShelf` (F32): a `PagedShelf` of
`ClusterCard`s (`minCardW: 380f, maxCardW: 520f`, `snap: Page`) so 15–20 baseline clusters page instead of
overflowing. Grids:

```csharp
public static Element ReleaseList(Zone zone) => Ui.AutoGrid(560f, Spacing.M, ZoneMetrics.ListRowHeight, Rows(zone.Items, 6, Items.ListRow));
public static Element BrowseTiles(Zone zone)  => Ui.AutoGrid(300f, ZoneMetrics.GridGap, ZoneMetrics.NavTileHeight, Tiles(zone.Items));
// RecentGrid (in the RecentGrid component, which owns the Recents subscription + week summary):
Ui.AutoGrid(300f, ZoneMetrics.GridGap, ZoneMetrics.RowItemHeight, [...rows, Items.HistoryItem(week)])
```

`DaylistCard` keeps its geometry through flex: `Direction = 0, Wrap = true`; text `Basis 380, Grow 1`; art
`Basis 360, Grow 0.8, MaxWidth 540, AspectRatio 540/296` — at 1316 the art column is ~540, below ~760 the art wraps under
the text at full width (the sheet's "stacked" case) with no `980`/`700` in the code.

### 3.3 `Home/Items.UI.cs` — templates fill their cell

```csharp
/// A square cover that fills its cell: the width comes from the arranged slot, the height from the aspect.
static Element Cover(string? url, float corner, Action? onPlay, float playDiameter, float aspect = 1f) => new BoxEl
{
    AlignSelf = FlexAlign.Stretch, AspectRatio = aspect, ClipToBounds = true, Corners = CornerRadius4.All(corner),
    ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
    Children = onPlay is null
        ? [Controls.Cover(url, aspect, corner, decodePx: ZoneMetrics.CoverDecodePx)]
        : [Controls.Cover(url, aspect, corner, decodePx: ZoneMetrics.CoverDecodePx), HoverPlay(onPlay, playDiameter)],
};

public static Element GridItem(HomeCard card, bool twoLineCaption)
{
    bool round = card.Kind == HomeCardKind.Artist;
    var target = card.Target;
    return new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Corners = CornerRadius4.All(Radii.Card),
        // No Focusable/OnClick/Role here: the shelf's slot root owns invoke + focus (E9). Pointer hover still restyles.
        Children =
        [
            new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, AspectRatio = 1f,
                Corners = round ? Radii.FullAll : CornerRadius4.All(Radii.Control),
                BorderWidth = 2f,
                BorderColor = Prop.Of(() => Playback.Current.Value.Equals(target) ? Tok.AccentDefault : ColorF.Transparent),  // F16
                Children = [Cover(card.ImageUrl, round ? Radii.Full : Radii.Control, () => Zones.PlayCard(card), ZoneMetrics.HoverPlayGrid)],
            },
            new BoxEl
            {
                Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = card.Subtitle is { Length: > 0 } sub
                    ? [Design.Type.HomeTitle14(card.Title), Design.Type.HomeCaption12(sub, maxLines: twoLineCaption ? 2 : 1)]
                    : [Design.Type.HomeTitle14(card.Title)],
            },
        ],
    }.Interactive(s_cardHover);
}
```

`WideLead(card, twoLineCaption)` = the same with `aspect: 2f` (two cells + gap ≈ 2.08, the sheet's 428×206 within 2 %),
`WideTile(card)` `aspect: 16f/9f`, `VideoTile` likewise. `RowItem`/`ListRow`/`NavTile`/`EpisodeRow` keep fixed art
(48/56) and `Grow = 1` text columns — they already fill; only `EpisodeRow`'s hairline changes:

```csharp
// F7: the fill is a flex share, not a computed width.
float f = EpisodeCaption.ProgressFraction(episode.ResumeMs, episode.DurationMs);
new BoxEl { Direction = 0, Height = ZoneMetrics.EpisodeProgressHairline, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(1f), Fill = Tok.StrokeControlDefault,
    Children = [ new BoxEl { Grow = f, Basis = 0f, MinWidth = 2f, Fill = Tok.AccentDefault, Corners = CornerRadius4.All(1f) },
                 new BoxEl { Grow = 1f - f, Basis = 0f } ] }
```

`DaylistTimer` (F38) keeps `UseInterval` but writes two signals (`_countdown: Signal<string>`, `_daypart: Signal<Daypart>`)
and binds `Text`/`Fill`/`Color` — no re-render per tick. The `char[8]` buffer goes; `DaypartRules.FormatCountdown`
returns the string (one allocation per second is not a hot path).

### 3.4 `Home/Podcasts.UI.cs`

`PodcastEnv` is deleted (the header comes from `Zones.ChapterHeader`; `nowMs` is read once per `ZoneRow` render, as today). `EpisodeLead`:

```csharp
static Element EpisodeLeadBody(Zone zone, ShelfController _) => new BoxEl
{
    Direction = 0, Wrap = true, Gap = ZoneMetrics.GridGap, AlignItems = FlexAlign.Start,
    Children =
    [
        zone.Lead is { } lead ? Items.EpisodeLeadCard(lead) with { Basis = 320f, Grow = 1f, MinWidth = 0f } : new BoxEl(),
        new BoxEl { Basis = 480f, Grow = 2f, MinWidth = 0f, Direction = 1, Children = zone.Items.Select(Items.EpisodeRow).ToArray() },
    ],
};
```
(`Select` is written out as a loop in the real file — no LINQ in render paths by convention.) `ContinueEpisodes`/`EpisodeRows`
= `Ui.AutoGrid(400f / 560f, …)`; `VideoTiles` = `Ui.AutoGrid(300f, …)` of `Items.VideoTile(card)` (aspect 16:9);
`PodcastGroups` = the cluster shelf. The duplicated `s_cardHover`/`TitleLine`/`CaptionLine` are deleted — `Items` exports them.

### 3.5 `Home/Facet.UI.cs`

- `FacetRow`: `Segmented.Create(items, _selIndex, Select, new SegmentedOptions { ItemRole = Tab, WrapFocus = true, Style = s_pivotStyle with { ItemGap = 24f }, Parts = s_pivotParts, OnItemHoverChanged = i => _hover.Value = i, OnItemFocusChanged = i => _focus.Value = i })`.
  The index sync is an effect: `UseEffect(() => { var (w, _) = FacetPivot.Resolve(ctx.Words.Value, ctx.Switch.Value.Target); _selIndex.SetIfChanged(w); });`.
  The prefetch dwell stays one `UseTimeout` keyed on `DepKey.From(active)`. `s_pivotParts` keeps only `PartSelectionPillSlot` hidden + `PartLabel` line height.
- `FacetCompactBand`: no opacity/hit-test juggling; its root carries `Enter = new EnterExit(Opacity: 0f, Active: true)`,
  `Exit = new EnterExit(Opacity: 0f, Active: true)` with the 167/83 ms tokens; `HomeScreen` mounts it under `Flow.Show`
  (F27). `UseScrollThreshold` moves to `HomeScreen` (one hook, `_bandShown`), passed through `FacetContext`.
- `FollowingToggle`: `ToggleButton.Controlled(…, parts: Controls.RootNoShrink)` — no `NoShrink` box; presence via
  `Flow.Show(() => hasSub, toggle)` with the same Enter/Exit fades instead of an always-mounted `Opacity 0` box.
- `BusyBar`/`FailureBar`/`EmptyFacet`/`DimWrap`: `DimWrap` deleted (the dim host lives in `HomeScreen`); the others
  unchanged except `EmptyFacet` `MaxWidth` (F11) and loc keys.

### 3.6 `Home/SectionScreen.UI.cs`

```csharp
readonly Signal<IReadOnlyList<HomeCard>> _cards = new(SeedCards);
readonly BoundItemsSource<HomeCard> _source;      // BoundItems.From(_cards) in the ctor
…
Element Grid() => ItemsView.CreateBound(_source, item => Items.SectionCard(item), RepeatLayout.GridFit(176f, ZoneMetrics.GridGap), new ListOptions<HomeCard>
{
    Grow = 1f, SelectionMode = ItemsSelectionMode.None, IsItemInvokedEnabled = true,
    OnInvoked = i => OpenCard(_cards.Peek()[i]),
    Scroll = new ScrollOptions { ScrollKey = _scope + "home:section:" + _p!.Uri },
    OnVisibleRange = _onVisibleRange,
});
```
`Items.SectionCard(BoundItemScope<HomeCard>)` is the bound twin of `GridItem` (`item.Image(c => c.ImageUrl)`,
`item.Text(c => c.Title)`, `item.Show(c => !c.IsBlank)` on the caption) — the template runs once per slot, paging appends
by writing `_cards`. `Responsive.Of`, `GridFit`, `_countSig`, the `Cursor = Hand`, and the private card go.

### 3.7 `Home/Customize.UI.cs` + `Home/LayoutFile.cs`

`HomeLayout` service (new, `Home/LayoutFile.cs` §5): `sealed class HomeLayout { Signal<LayoutDoc> Doc; void Dispatch(Func<LayoutDoc, LayoutDoc>); static Context<HomeLayout> Slot; }`
constructed once in `Services` (`LayoutStore.ForApp().Load()` at boot, `Commit` on dispatch) and provided at the app
root beside `SidebarPreferences`. `CustomizeScreen` reads `UseRequiredContext(HomeLayout.Slot)`; `ZoneRow` visibility
toggles and reorder call `Dispatch`. The customizer's `ZoneRow` keeps the `ToggleSwitch` sync-effect (that one is the
documented controlled-signal freeze) but drops the `_store` field.

### 3.8 `Home/Model.cs`, `ZonePlanner.cs`, `PodcastPlanner.cs`

- `SectionReader`: delete `RowCache`/`SectionCache`/`s_scope`/`EnsureScope`; `Read` and `Of` build fresh arrays; add
  `SectionInput.Preview : IReadOnlyList<HomeCard>` read from `Edges.SectionPreviewTracks` (D2).
- `ZoneTitles.Localized()` builds the record from `Loc.Get(Strings.Home.Zone.*)` (subscribes the computing memo).
- `ClusterFold`: one cluster per baseline section: `Over` = the section title if present (server-localized — no
  prefix parsing), `Name` = the card title, `Rows` = `Preview` (≤ `ClusterMaxRowsPerCard`), header card = the card
  (opens the playlist/album; rows play the track in that context). Untitled sections with an album card
  (`music-following-chip`) are planned as `ReleaseList` rows (album · artist · date · play) under
  `ZoneTitles.FromArtistsYouFollow` — the Following facet becomes the release feed the server actually sends.
  `Prefixes`, the singleton fold and "More for you" are deleted.
- `Zone.Empty(facet)` / `ScreenModel.Blank(facet, kinds)` factories replace the inline seed construction.

### 3.9 `Home/Facets.cs`

`FacetPhase` loses `FadingOut`; `FacetSwitch.Select(Fresh|Stale)` returns `Idle` with `Published = target` (the swap is
immediate; Stale also enters `Refreshing`); `Landed(Loading)` → `Idle`/`Published = target`; `OutDone` deleted;
`Interact` kept and wired (§3.1); `Dismiss` kept (the InfoBar's close). `CompactBand` and `ZoneEnter` deleted.
`FacetSwitchTests` table updated accordingly (§5).

### 3.10 Shell (`Shell.cs`, `Shell.Host.cs`, `Shell.Palette.cs`)

- Route table: add `PlaceByArg` next to `KeyedByArg` (`Home: KeyedByArg=false, PlaceByArg=true`); `SameSlot` reads the
  column (`return !Row(a.Kind).PlaceByArg || a.Arg == b.Arg;`) — the `if (a.Kind == RouteKind.Home)` branch goes.
- Delete `s_homeRouteArg`, `HomeArgCell`, `RouteArg`, `PublishHomeArg` and its 8 call sites; `HomeScreen` reads
  `Shell.Current` (§3.1). Wave-0 spike: if `Flow.KeepAlive` re-runs `view` on a same-key token change
  (`ControlFlow.cs` KeepAliveOptions doc: "a same-key token change" is an activation edge), `PageFor` re-pushes
  `Props(route.Tab, route.Arg)` and even the `Shell.Current` read is unnecessary — prefer that.
- Palette rows: loc keys (`Strings.Palette.HomeAll`…).

### 3.11 Localization

The generator already runs over `src/apps/Wavee/assets/loc/en-US.json` (`Wavee.csproj:218-231`, `FluentGpuLocBase`)
and emits `Strings.*`; `Strings.Nav.Home`, `Strings.Menu.MoveUp`, `Strings.Home.Customizer.Title` are used today. The
existing `"home"` object (`en-US.json:563-580`) already carries `goodMorning`/`goodAfternoon`/`goodEvening`,
`madeForYou`, `jumpBackIn`, `recents`, `radio`, and `recentlyPlayed`/`shuffle`/`retry` exist elsewhere — **reuse
those keys**; the first task of the loc agent is a targeted grep per literal before minting. Keys that are new
(added under the existing `home` object, mirrored into `nl.json`/`ko-KR.json`):

```json
"home": {
  "…existing keys stay…",
  "facetAll": "All", "following": "Following", "seeAll": "See all", "seeAllN": "See all {n}", "listeningHistory": "Listening history",
  "plays": "{n} plays", "save": "Save", "more": "More",
  "couldntLoad": "Couldn't load {what}", "couldntLoadHome": "Couldn't load Home", "checkConnection": "Check your connection and try again.",
  "nextDaylistIn": "Next daylist in {t}", "customizeBody": "Show, hide and reorder Home's sections.", "resetDefault": "Reset to default",
  "zone": { "becauseYouLike": "Because you like", "moreForYou": "More for you", "radioAndMixes": "Radio and mixes", "browse": "Browse", "newEpisodes": "New episodes", "continueListening": "Continue listening", "videosYouMightLike": "Videos you might like", "episodesYouMightLike": "Episodes you might like", "becauseYouListenTo": "Because you listen to", "fromArtistsYouFollow": "From artists you follow", "yourShows": "Your shows", "showsYouMightLike": "Shows you might like" },
  "layout": { "daylist": "Daylist", "newMusic": "New music", "releases": "New releases", "becauseYouLike": "Because you like…" },
  "daypart": { "early": "Early", "morning": "Morning", "afternoon": "Afternoon", "evening": "Evening", "night": "Night" },
  "when": { "playingNow": "Playing now", "minAgo": "{n} min ago", "hAgo": "{n} h ago", "yesterday": "Yesterday", "today": "Today" },
  "release": { "album": "Album", "playlist": "Playlist", "artist": "Artist", "song": "Song", "episode": "Episode", "podcast": "Podcast", "audiobook": "Audiobook" },
  "empty": { "audiobooksTitle": "No audiobooks yet", "audiobooksBody": "Audiobooks you save will show up here.", "audiobooksAction": "Browse audiobooks", "podcastsTitle": "No podcasts yet", "podcastsBody": "Shows and episodes you follow will show up here.", "podcastsAction": "Browse podcasts", "genericTitle": "Nothing here yet", "genericBody": "Your recommendations will show up here soon.", "genericAction": "Browse" }
},
"palette": { "homeAll": "Home: All", "homeMusic": "Home: Music", "homePodcasts": "Home: Podcasts", "homeAudiobooks": "Home: Audiobooks", "homeFollowingOn": "Home: Following on", "homeFollowingOff": "Home: Following off" }
```

`Strings.Detail.Today`/`Yesterday` already exist; `EpisodeCaption`/`WhenCaption` use them rather than a Home twin
(the file headers' "not reused on purpose" reasoning is reversed: one key per phrase, app-wide).

`WhenCaption.Format`/`EpisodeCaption.Date` become `Format(WhenResult, Func<string,string> loc)`-shaped: `Classify`
stays pure and tested; the UI passes `Loc.Format`. `ReleaseListRules.TypeLabel` returns the key.

### 3.12 Data pipeline

**D1 — header images (F31).** `Spotify.Decode.Home.cs:446-453`: hoist `p.HeaderImage = c.HeaderImage;` out of the
`if (daylist)` block (the daylist-only fields stay inside: `Terms`, `Expires`/`Created`). `HomeCard.HeaderImageUrl`
(`Home.cs:1636`) is unchanged. Test `HomeDecodeTests.NonDaylistPlaylist_KeepsHeaderImage` on a synthetic fixture card
with `format: release-radar` + attribute 5. `ZonePlannerTests.WideEditorial_FiresOnHeaderedGeneric` over the same
fixture (6/10 headered is a near miss today — the threshold stays ≥ 2/3; the fixture uses 7/10).

**D2 — `feedBaselineLookup` (F32).** The desktop client's flow per facet is `home` → `feedBaselineLookup(uris of every
`HomeFeedBaselineSectionData` card)` → `fetchExtractedColors`. Only the lookup is planned here (colors already arrive
in `home`; `lookupChildEntities` is the NPV's).

1. `Spotify/Spotify.Api.cs` `Queries`:
   ```csharp
   public static readonly Query FeedBaselineLookup = new("feedBaselineLookup",
       "a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc", false);
   ```
   `Vars` gains `WriteStrings(string name, ReadOnlySpan<string> values)`; `Api.FeedBaselineLookup(ReadOnlySpan<string> uris, Staging into, ct)`
   mirrors `Home(...)`: `Pathfinder(Queries.FeedBaselineLookup, vars.Finish(), ct)` then `Decode.FeedBaselineLookup(bytes, into)`.
2. `Spotify/Spotify.Decode.Home.cs` (new fold `PreviewFold`): for each `data.lookup[]` entry: `_uri` → the owning
   `Section` rows are resolved at commit by uri (a card uri may sit in several baseline sections across facets — the
   same run closes every section that carries it); each `previewItems.items[].data` (`Track`) mints a thin `Track`
   row through `Staging.Tracks` (`Authority.Seed`: `Title = name`, `Image = albumOfTrack.coverArt.sources` widest ≤ 300,
   `Accent = coverArt.extractedColors.colorDark`) — the recently-played band's sparse-mention path
   (`Spotify.Decode.Home.cs:30-33`). The run closes `s.Edges.Close(Relation.SectionPreviewTracks, in sectionId, tracks)`
   with payload `PreviewEdge(byte Rank)`. Absent `canvas`/`previews` are optional; `EpisodeOrChapterResponseWrapper`
   entries are skipped (episodes carry no previews).
3. Edges: `Relation.SectionPreviewTracks` (`Edges.Staging.cs`), `EdgeTable<PreviewEdge> SectionPreviewTracks` on the
   scope (parent `Sections`, like `SectionCards`); `Section.PreviewSlots`/`PreviewVersion` handle accessors.
4. Fetch planner: `FetchEdge.HomePreviews` (parent = `Homes`, one request per facet row) →
   `FetchRoute.Pathfinder(PathfinderOp.FeedBaselineLookup, 0)`; the request builder reads the row's baseline sections'
   card uris (`Home.BaselineCardUris(slot)`, deduped, ≤ 50) — the same "one request closes many section runs" shape
   `home` itself has. Readiness on `Edges.HomePreviews.State(homeSlot)`; `Home.HasBaselineSections`. Demanded from
   `HomeScreen.Demand` once `Knows(Sections)` (§3.1); `Entities.Refresh(Homes, …)` on a stale facet re-asks both.
5. Planner: `SectionInput.Preview`; `ClusterFold` per §3.8; `PageDedupe` ignores preview rows (they are navigation
   inside a card, like the podcast footer thumbs).
6. Tests (synthetic fixtures only — `Fixtures/spotify/feed-baseline-lookup.json` with two playlists + one album,
   five fake tracks each, `Fixtures/spotify/home-baseline.json` with three baseline sections + one following-style
   album section): `FeedBaselineLookupDecodeTests` (`Section.PreviewSlots.Length == 5`, ranks 0..4, track title/image/
   accent land, a uri shared by two sections closes both, an episode wrapper is skipped), `ZonePlannerTests`
   (`Baseline_BecomesOneClusterPerSection`, `Following_AlbumsBecomeReleaseList`), `HomeUi/ClusterFoldTests` rewritten,
   `ApiWaste/HomeWasteTests` (All open: 1 `home` + 1 `feedBaselineLookup`; second open: 0; a facet with no baseline
   sections: no lookup).

---

## 4. Deletions

| What | Why |
|---|---|
| `Entities/Home.Page.cs`, `Home.UI.cs`, `Home.Cards.UI.cs`, `Home.Artists.UI.cs`, `Home.Customizer.cs`; the presentation halves of `Home.Rules.cs` (`HomeLandingProjection`, `HomeFacetStrip`, hero/module/artist layouts, timeline merge, layout doc/reducer/wire, `HomeLandingRules`), `Home.cs` (`HomeComposer`, `HomeFeedView`, `HomeGroup*`, `HomeSectionView`, `HomeRevealGate`, `HomeFeedReadiness`, `HomeModuleTitles`, `SelectedFacet`), `Home.Host.cs` (`LandingPageFor`, `SectionPageFor`, `CustomizerPageFor`, `HomeLayoutStore`, `HomePreferences`, `MarkRevealed`/`HasRevealed`, `EnsureTopContent` + `TopContentState` where Home/Fake are the only users), and their tests (`HomeComposerTests`, `HomeLayoutStoreTests`, `HomeLandingProjectionTests`, `HomeLayoutTests`, `HomeReturnTests`, every test over a deleted type). **Keep, moved to Browse-owned files unchanged:** `HomeBrowseCards`, `HomeModules.SectionGrid`/`DrillHeader`, `HomeModuleLayout`, `FoldDeck`/`HomeFoldTile` — `Browse.Page.cs`/`Browse.UI.cs` render with them (the implementation plan already said so). | F34 — the plan's go-live deletion that never happened |
| `Home/Metrics.cs`: `ContentWidth`, `*At1316`, `ShelfPageStrideAt1316`, `DaylistImageColumn`, `DaylistArtRatioBelow980`, `DaylistStackBreak`, `HistoryItemWidth`, `ListRowDateColumn` stays (a column, not a page width), `WideTileWidth/Height` → one `WideTileAspect`, `VideoTile*` → `VideoAspect`, `EpisodeLead*` → `LeadAspect`, `ShowSquareWidth`, `EmptyFacetBlockWidth` → `EmptyFacetMaxWidth`, `Extent`, `HeaderExtent`, `TailExtent`; `GridFit`; `ShelfPaging` | F1–F4 |
| `Home/Podcasts.Rules.cs` (whole file; `SeeAllLabel` → loc) | F6 |
| `Home/Zones.UI.cs`: `ZoneEnv`, `ShelfHost`, `ChapterHeaderCore`/`ChapterHeaderProps`, `ClusterCard` (moves to Items) | F8, F17, F20, F25 |
| `Home/Podcasts.UI.cs`: `PodcastEnv`, `EpisodeGrid` width math, duplicate `s_cardHover`/`TitleLine`/`CaptionLine` | F6, F17 |
| `Home/Screen.UI.cs`: see §3.1 | F12–F19, F22, F29, F30 |
| `Home/Facets.cs`: `FacetPhase.FadingOut`, `FacetSwitch.OutDone`, `ZoneEnter`, `CompactBand` | F13, F35 |
| `Home/Facet.UI.cs`: the `PartItem` counter, `DimWrap`, the opacity/hit-test presence hacks | F18, F24, F27 |
| `Home/Reveal.cs`: `ScreenLoadState` → use `LoadState` | F35 |
| `Home/EpisodeCaption.cs` `ProgressWidth` | F7 |
| `Home/Model.cs`: `SectionReader` caches | F22 |
| `Platform/Controls.cs` `NoShrink`; `Concert.UI.cs` `FilterToken` host box | F20 |
| `Shell.Host.cs` `s_homeRouteArg`/`HomeArgCell`/`RouteArg`/`PublishHomeArg`; `Shell.cs` `SameSlot` Home branch | F23 |
| `.claude/skills/wavee/home-layout.md` (rewrite for `LayoutFile` v2 + `HomeLayout` service) | F34 |
| Tests: `ZoneMetricsTests.cs`, `PodcastLayoutTests.cs`, `CompactBandTests.cs`; `ClusterFoldTests` rewritten | F40 |
| Engine: PagedShelf "HEADERLESS SHELVES −12" note; `ExternalChapterHeader`'s mirror-signal comment (uses `PipsPager.Controlled`) | E10, E12 |

---

## 5. Tests

Survive unchanged: `CardNavTests`, `DaypartTests`, `EpisodeCaptionTests` (minus `ProgressWidth`), `FacetCacheTests`,
`FacetDimPlanTests`, `FacetPivotTests` (minus `Enabled`), `FacetRouteTests`, `LayoutFileTests`, `PageDedupeTests`,
`PodcastPlannerTests`, `ReleaseDetectTests`, `ScreenLoadTests`, `SectionRolesTests`, `SectionScreenRulesTests`,
`WashPickTests`, `WeekSummaryTests`, `WhenCaptionTests` (`Classify`; `Format` via a loc stub), `ZonesRulesTests`,
`ZonePlannerTests` (plus the D1/D2 cases), `HomeUiFixtures`/`PodcastFx`.

Changed: `FacetSwitchTests` (no `FadingOut`; `Select(Fresh)` → `Idle` published; `Interact` cancels a Refreshing swap),
`ClusterFoldTests` (one cluster per baseline section with preview rows), `ZoneEnterTests` deleted (engine stagger).

New: `FeedBaselineLookupDecodeTests`, `HomeDecodeTests.NonDaylistPlaylist_KeepsHeaderImage`, `HomeWasteTests`
(request counts), `HomeLayoutServiceTests` (one document, dispatch commits, both screens see one signal),
`ShellRoutesTests.PlaceByArg_Home` (same slot, different place), engine gates listed in §2.

No test reads production source; every UI decision that needs a test is a pure class (`ScreenLoad`, `FacetDimPlan`,
`FacetSwitch`, `ZoneRows.EstimateExtent`).

---

## 6. Waves

Parallel subagents (sonnet) on disjoint files; only the orchestrator builds, tests and launches. Gate at every wave:
app `dotnet build Wavee.slnx` Debug **and** Release clean; `dotnet test src/apps/Wavee.Tests` Debug **and** Release
green; for engine waves `dotnet build src/FluentGpu.slnx` Debug + Release, `dotnet run --project src/FluentGpu.VerticalSlice`
"ALL CHECKS PASSED", `dotnet test src/FluentGpu.Engine.Tests` both configs, `check-canon.ps1` exit 0. Screenshots:
`--fake` at 1440×900 and 1000×700, dark + light, All at rest / scrolled 400 / Podcasts / Music→Following / mid-switch /
failure; compared against the canvas boards and the previous wave's shots (sandbox-free launch per the capture memory;
stop by PID only).

| Wave | Engine (`fluent-gpu`) | App (`waveemusic`) | Orchestrator gate |
|---|---|---|---|
| 0 spikes | E17 sticky-in-realized-row gate, E18 rebind-restores-page gate, `Flow.KeepAlive` same-key `view` re-run gate (`gate.keepalive.same-key-view`), `gate.items.nested-shelf-realize-alloc` | — | VerticalSlice; the results decide §3.1's list shape (virtualized) and §3.10's arg delivery (re-pushed props vs `Shell.Current`). A failing E17 becomes Wave-1 engine work, never an app workaround. |
| 1 engine | E9 (`PagedShelf` keyboard + `onInvoke`), E10 (`ShelfLift`), E11 (lead half-row), E12 (`PipsPager.Controlled`, `IconButton` bound enabled), E13 (`Segmented` edges + `ItemGap`), E14 (`ComponentEl` base props), E15 (`HitTestVisible` bindable) — one agent per control file; gallery samples updated (`CollectionsMenusPages.cs` external header uses `Controlled`; `BasicInputPages.cs` Segmented sample uses the callbacks) | D1 decoder fix + test; D2 query/decode/edge/planner + tests (agents: `Spotify.Api.cs` · `Spotify.Decode.Home.cs`+`Edges` · `Fetch.*` · `ZonePlanner.cs`+`Model.cs`); loc JSON keys (one agent, three files) | engine gates D+R, VerticalSlice, Engine.Tests D+R, canon; app build D+R + tests D+R (the app still compiles against the old UI; `Wavee.csproj` picks up the new `Strings.Home.*`) |
| 2 app presentation | — | Disjoint files: A `Items.UI.cs` · B `Zones.UI.cs` (+ `ZoneRow`, `RecentGrid`) · C `Podcasts.UI.cs` · D `Facet.UI.cs` · E `SectionScreen.UI.cs` · F `Customize.UI.cs` + `LayoutFile.cs` (`HomeLayout` service) + `Services` wiring · G `Facets.cs` + `Reveal.cs` + `Metrics.cs` cuts · H `Platform/Controls*.cs` (`Cover`, `RootNoShrink`, `NoShrink` delete) + `Concert.UI.cs` FilterToken | build D+R, tests D+R; `--fake` screenshots at 1440 and 1000: no tier flip, shelves fit, grids reflow, no −12 seam under headers |
| 3 screen + shell | — | `Screen.UI.cs` rewrite (§3.1) · `Shell.cs`/`Shell.Host.cs`/`Shell.Palette.cs` (§3.10) · `Entities.Fake.Home.cs` latency moves into the fake fetch host (F15) | build/tests D+R; screenshots: facet switch dim + bar (fake latency), keyed Exit/Enter swap, band via `Flow.Show`, deep link `wavee://open?route=home&arg=podcasts-chip`, Back/Forward walk facets; Diagnostics ▸ Tiles counters 0; `scroll.burst` Smooth end to end; ReuseGuard silent; `--fg diag` shows ≤ viewport+overscan `[img] request` lines at first paint (the F19 proof); keyboard: arrows move between shelf cards, Enter opens, focus ring on every card, pivot arrows don't select |
| 4 deletion | — | §4 in one change (old Home files + tests + skill doc rewrite + `home-layout.md`), `home-canvas-pub/` stays untracked | build/tests D+R; grep gates: no `Wavee.HomeUi` reference outside `Home/` except `Home.Host.cs` routes; no `GridFit`/`ShelfPaging`/`ContentWidth`/`NoShrink`/`RouteArg`/`FadingOut` symbol anywhere |
| 5 live parity | — | live account run (owner): All / Music / Following / Podcasts against the two captures' section shapes: `WideTiles` present for the headered generic section, one cluster card per baseline section with 4 preview rows, Following = release list; CHANGELOG bullets with `(#n)`; `docs/plans/wavee/home-redesign-implementation.md` gets a "superseded by remediation" banner | owner screenshot review; `HomeWasteTests` counts stated in the PR |

Issue references: one GitHub issue per workstream (engine E9–E15, app remediation, data pipeline D1/D2) via the
`github-triage` skill before Wave 1; every commit body `Fixes #n`.

---

## Appendix L — loc keys landed in Wave 1 (use these in Wave 2; no new literals)

Generator: plain key → `Loc.Get(Strings.X.Y)`; an ICU `{name}` key → typed `Strings.X.Y(name)` (+ `Strings.X.YKey`).

| Phrase | Member |
|---|---|
| Made for you / Jump back in / New releases | `Strings.Home.MadeForYou` / `Strings.Home.JumpBackIn` / `Strings.Home.NewReleases` (existing) |
| Recently played | `Strings.Sidebar.Section.RecentlyPlayed` (existing) |
| Retry | `Strings.Common.Retry` (existing) |
| Today / Yesterday | `Strings.Detail.Today` / `Strings.Detail.Yesterday` (existing) |
| Move up / Move down | `Strings.Menu.MoveUp` / `Strings.Menu.MoveDown` (existing) |
| Customize Home (title, link, palette) | `Strings.Home.Customizer.Title` (existing; value is "Customize home") |
| N items (section screen) | `Strings.Home.SectionItems(count)` (existing) |
| All / Following | `Strings.Home.FacetAll` / `Strings.Home.Following` |
| Couldn't load {what} / Couldn't load Home / Check your connection… | `Strings.Home.CouldntLoad(what)` / `Strings.Home.CouldntLoadHome` / `Strings.Home.CheckConnection` |
| See all N / Listening history / {n} plays / Save / More | `Strings.Home.SeeAllN(n)` / `Strings.Home.ListeningHistory` / `Strings.Home.Plays(n)` / `Strings.Home.Save` / `Strings.Home.More` |
| Next daylist in {t} | `Strings.Home.NextDaylistIn(t)` |
| Show, hide and reorder… / Reset to default | `Strings.Home.CustomizeBody` / `Strings.Home.ResetDefault` |
| Zone titles | `Strings.Home.Zone.{BecauseYouLike, MoreForYou, RadioAndMixes, Browse, NewEpisodes, ContinueListening, VideosYouMightLike, EpisodesYouMightLike, BecauseYouListenTo, FromArtistsYouFollow, YourShows, ShowsYouMightLike}` |
| Customize zone labels | `Strings.Home.Layout.{Daylist, NewMusic, BecauseYouLike}` (+ the reused keys above) |
| Dayparts | `Strings.Home.Daypart.{Early, Morning, Afternoon, Evening, Night}` |
| When captions | `Strings.Home.When.PlayingNow`, `Strings.Home.When.MinAgo(n)`, `Strings.Home.When.HAgo(n)` |
| Release type badges | `Strings.Home.Release.{Album, Playlist, Artist, Song, Episode, Podcast, Audiobook}` |
| Empty facets | `Strings.Home.Empty.{AudiobooksTitle/Body/Action, PodcastsTitle/Body/Action, GenericTitle/Body/Action}` |
| Palette | `Strings.Palette.{HomeAll, HomeMusic, HomePodcasts, HomeAudiobooks, HomeFollowingOn, HomeFollowingOff}` |

---

## Appendix W0 — Wave 0 spike results (2026-09-26; `ZoneListSpikeChecks.cs`, suite `zone-list-spikes`)

| Gate | Result | Consequence |
|---|---|---|
| `gate.scroll-effects.sticky-in-realized-row` (BoxEl header, and sticky on a component's RENDERED ROOT with `scope: rowKey`) | PASS | Virtualized zone list with sticky chapter headers works today; no E17 engine work. |
| `…sticky-in-realized-row.componentel` (`.Sticky` on the `Embed.Comp` itself) | OPEN → E14 | `Reconciler.Mount` returns at `MountComponent` before `WriteColumns` (the only `BakeScrollEffects` caller); the reuse branch never writes anchor columns. Fix = `WriteAnchorColumns` in both (§E14). There is no `ReuseGuard.AnchorPropDropped` to delete. |
| `gate.shelf.controller.rebind-restores-page` | OPEN → E18 | `PagedShelf` ctor binds the controller but `_page` starts at 0 and the first `Render` publishes 0 over the retained page. Fix: seed `_page` from `controller.Page.Peek()` after `Bind`, and on first realize `ScrollTo(page*stride, ScrollMove.Immediate)` (not the animated `GoToPage`). |
| `gate.keepalive.same-key-view` | PASS | **Arg delivery = re-pushed props**: `PageFor` → `Embed.Comp(new Props(route.Tab, route.Arg), …)`; delete `s_homeRouteArg`/`HomeArgCell`/`RouteArg`/`PublishHomeArg` + 8 call sites. `TransitionFor` (Shell.UI.cs:846) must return null for Home→Home or a facet switch gets a page entrance. |
| `gate.items.nested-shelf-realize-alloc` (+ scroll-tick) | PASS | Steady frames 0 B with nested shelves; realize edge ~205 KB/3 shelf rows. One-time 8.7 KB slice-arena growth on first clip (not a Wave-1 item). |

App facts for §3.1 (the zone list): (1) `ListOptions.CacheExtentPx` does not exist — drop it from the snippet; (2) `ListOptions.KeyOf` is inert on the template path — the row's `Key` must be on the element the template returns (the `Embed.Comp(...) with { Key = "zone:"+… }`); (3) the ItemContainer content lane is a row-direction single-child wrapper, so a zone row's root needs `Grow = 1f, Shrink = 1f, MinWidth = 0f` or it lays out 0 wide.

---

## Appendix D — pending text for `fluent-gpu/docs/guide/components-elements-layout.md` (orchestrator merges after Wave 1)

- **E12** (near the IconButton/ToggleButton.Controlled cheat sheet ~l.264): `IconButton.Create(glyph, onClick, IReadSignal<bool> isEnabled, …)` — bound-enabled overload, tracks the signal with no parent re-render. `PipsPager.Controlled(count, selectedIndex, onSelect, parts, maxVisiblePips, …)` — value-controlled (the `ToggleButton.Controlled` precedent), no internal signal; a click on the already-selected pip still calls `onSelect`. A caller that needs pips to follow a signal without a parent re-render wraps the call in its own small component that reads the signal (`ShelfPips`).
- E9/E10/E11 and E14/E15 agents report their paragraphs here when they land.
- **E14/E15**: base-`Element` props (`ScrollEffects`/`ScrollScope`/`Visible`/`Enter`/`Exit`/`Layout`/`Stagger`/`While*`/`WheelTarget`/`RelativeTo`/`MorphId`) set on an `Embed.Comp(...)` now apply to its anchor (sticky, presence, orphan-fade exits all work on a component). Layout-shape props stay `BoxEl`-only → `Parts[PartRoot]`. `HitTestVisible : Prop<bool>` (BoxEl) — bindable like `Visible`/`Fill`; a bound `false` clears hit-testing only (still laid out/painted) with no re-render or re-paint.
