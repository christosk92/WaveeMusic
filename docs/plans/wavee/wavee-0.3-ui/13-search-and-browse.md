# Search (empty, suggest, results) and Browse (directory, category pages) - 0.3 visual fidelity contract

> 0.2.9 sources (all paths relative to `src/apps/Wavee/`; after Wave 0 the same relative paths live under `src/apps/_old/Wavee/`):
> `Features/Search/SearchPage.cs` (1151), `Features/Search/SearchHero.cs` (156), `Features/Search/SearchGenreTiles.cs` (180),
> `Features/Search/SearchFacetGrid.cs` (165), `Features/Search/OmnibarSuggestQuery.cs` (138), `Features/Search/SearchRoutes.cs` (28),
> `Features/Search/SearchChipSkeletonPolicy.cs` (11), `Design/SearchHighlight.cs` (81),
> `Features/Browse/BrowsePage.cs` (730), `Features/Browse/BrowseDirectory.cs` (363), `Features/Browse/BrowseTiles.cs` (358),
> `Features/Browse/BrowseTaxonomy.cs` (181), `Features/Browse/BrowseDirectorySeeds.cs` (96), `Features/Browse/BrowsePageLayout.cs` (69),
> `Features/Browse/BrowseDirectoryPage.cs` (67), `Features/Browse/BrowsePageStore.cs` (64), `Features/Browse/BrowsePageHost.cs` (60),
> `Features/Browse/BrowseDirectoryStore.cs` (54), `Features/Browse/BrowseRoutes.cs` (41), `Features/Browse/BrowseMastheadMetrics.cs` (39);
> plus the omnibar half of `Features/Shell/ShellToolbar.cs` (lines 146-617 of 617) and `Features/Shell/ShellMastheadBand.cs` (131).
> **4,502 lines total; 2,880 of them non-comment, non-blank.**
> 0.3 target: `Entities/Search.cs` (CORE), `Entities/Search.UI.cs`, `Entities/Search.Page.cs`, `Entities/Browse.cs` (CORE),
> `Entities/Browse.UI.cs` and **`Entities/Browse.Page.cs`** - the Browse page home this chapter asked for, **settled by
> arbitration 2026-09-12 and now in plan section 2's tree with owner P** (§9). Wave 5 owner P.

Cross-chapter references (do not re-specify here): `00-design-system.md` (Spacing / Radii / Tok / type ramp / cover palette /
motion curves / CTAs), `01-track-row.md` (`MediaCard.Row`, save button, menus, drag chip), `02-cards-and-controls.md`
(`MediaCard.GridCard` / `MediaCard.Shelf`, `PagedShelf`, `FollowButton`, `EmptyState` / `ErrorState`),
`10-home.md` + `11-home-cards-and-modules.md` (`HomeModules.FoldDeck`, `HomeFoldTile`, `HomeModules.SectionGrid`,
`HomeBrowseCards`, `HomeSectionAppendPreloader`), `18-shell-frame.md` (window layout, omnibar box chrome, `ShellMastheadBand`,
`DrillTrail`, page transitions), `19-shell-overlays.md` (menus / overlay service).

---

## 0. The non-negotiables

1. **An empty query is the Browse directory, not an empty Search page.** `NavRouteNormalizer.Apply` rewrites
   `("search", null|"" |whitespace)` to `Route("browse")` on every committed navigation and on both restore paths
   (`Features/Shell/NavRouteNormalizer.cs:20-21`). The user clears the omnibar and lands on Browse; they never see a
   "No results found" page with a blank title. `SearchPage` is results-only (`Features/Browse/BrowseDirectoryPage.cs:12-14`).
2. **The query echo is the committed query, lowercased, in the display face.** `WaveeType.SurfaceDisplay(q.ToLowerInvariant())`
   - `Ui.TitleLarge` 40/52 at Weight 400, `Segoe UI Variable Display`, CharSpacing −12/1000 em
   (`Features/Search/SearchPage.cs:111`, `Design/WaveeType.cs:146-151`). One line, character ellipsis, `Shrink=1` never `Grow`.
3. **The facet row reserves its real shape from frame one.** While the first response of a query is genuinely in flight and no
   chip source has ever been seen, `ChipBarSkeleton` paints eleven placeholder pills at widths
   `[40,68,76,74,88,68,96,116,76,76,76]` - the known facet superset, which wraps to two rows at typical pane widths
   (`Features/Search/SearchPage.cs:227-247`, gated by `SearchChipSkeletonPolicy.ShouldShowSkeleton`,
   `Features/Search/SearchChipSkeletonPolicy.cs:10`). A lone "All" tab that jumps to eleven when data lands is the regression
   this exists to prevent.
4. **The selected facet's underline grows from its left edge.** 3 DIP tall, `Radii.Full`, `Tok.AccentDefault`,
   `TransformOriginX = 0f`, `Enter = new EnterExit(Sx: 0f, Active: true)`, `Transition = MotionTokenDef.Eased(260f, Easing.SmoothOut)`
   (`Features/Search/SearchPage.cs:311-320`, constants at `:36-37`).
5. **Switching facets slides the body, and only on a real switch.** `_slideArmed` is false on the first render, so mounting the
   page never slides; afterwards a chip change animates the keyed facet body with `MotionRecipes.PageSlideForward` (chip index
   increasing) or `PageSlideBack` (`Features/Search/SearchPage.cs:59-75`).
6. **The loading skeleton has the SHAPE of the facet it stands in for.** Three geometries, never one: All = a 228-DIP hero card
   over a wrapping card grid; Albums/Playlists/Genres = a wrapping 168-DIP card grid; everything else = ten 64-DIP art+two-line
   rows (`Features/Search/SearchPage.cs:136-219`). `smoothResize: false` on the facet body - a page-level region whose two
   branches differ by thousands of DIP must land its height at once (`:79-87`).
7. **The top result is a 228-DIP card, not a row.** Copy left, a 300x260 cover cropped off the right edge at `Rotation = -2.5f`,
   `OffsetX = 40f`, `OffsetY = -16f`, clipped by the card, with a radial wash of the cover's own graded accent behind it
   (`Features/Search/SearchHero.cs:106-146`).
8. **Every search hit is the same unplated `MediaCard.Row`.** All-tab list, Best-matches grid, Songs grid and the
   Podcasts / Episodes / Audiobooks / Profiles / Authors facet lists route through `SearchAllList.HitRow` with
   `plated: false` (`Features/Search/SearchPage.cs:759-785`), so a podcast row looks identical wherever it came from.
   Only `large: true` (the fallback hero skin) differs. **Two facets are deliberately NOT that row and the chapter says so
   where they live:** Artists renders `SearchPage.ResultRow` (a thinner 60-DIP row, `:420, 601-622` - see W10 and §9), and
   Genres renders `BrowseTiles.Link` cells (`:409-410`). Those are the only two exceptions in the file.
   **A TRACK hit's 48-DIP art disappears entirely when the app setting `WaveeSettings.HideTrackArtwork` is on**
   (`showArtwork: !isTrack || !hideTrackArtwork`, `:784`, read reactively through `AppearancePrefs.TrackArtworkHidden`,
   `Design/AppearancePrefs.cs:14-18`) - a non-track hit always keeps its art. See §3's "track-artwork policy" row.
9. **A track hit gets the FULL track menu and a depositable drag payload.** `SearchAllList.TrackOf` scans the page's own
   `SearchResults.Tracks` for the hit's uri, so a song right-clicked in search offers Go to album / Go to artist / credits -
   never a strictly smaller menu than the same song on a detail page (`Features/Search/SearchPage.cs:689-704, 791-798`).
10. **Browse's five cell densities, one per band, cheapest to most expressive.** Word (36-DIP BodyStrong pill) -> Name
    (32-DIP Body pill + colour pip) -> Link (padded 14px secondary + pip) -> Bar (52-DIP card + corner wash + tick) ->
    Peek (88-DIP card + corner wash + tick + hanging cover). `Features/Browse/BrowseTiles.cs:17-20, 56-179`.
11. **A mood bar / more card is the house card plate with the colour demoted.** `Tok.FillCardDefault` -> `Tok.FillCardSecondary`
    on hover at the SAME `Elevation.Card`, 1px `Tok.StrokeCardDefault`, with the raw category colour appearing only as a
    right-edge radial at 0.20 opacity (0.34 on hover) plus a 3-DIP left tick. Never a full-bleed colour field, never a hover lift
    (`Features/Browse/BrowseTiles.cs:94-133, 139-202, 244-250`).
12. **The More card's art hangs out of the frame.** 80x80, corner-aligned bottom-right with `Margin = (0,0,-24,-24)`,
    `Rotation = -8f`, `Shadow = Elevation.Card`, clipped by the card root; on hover it slides `(-6, +2)` and rotates `+3°` over
    `MotionTok.ControlNormal` (250 ms, `Easing.FluentStandard`) (`Features/Browse/BrowseTiles.cs:211-232`).
13. **The Browse directory assembles itself band by band.** Each band carries `Animate = WaveeEntrance.Row(index)` with `index`
    starting at 1 - a capped 40 ms stagger, opacity-only, 8 DIP rise, 2px blur, 400 ms SmoothOut
    (`Features/Browse/BrowseDirectory.cs:243, 130-131`; `Design/WaveeMotion.cs:112-126`). The page-level `Skel.Region` uses
    `reveal: SkelReveal.None` so the page has exactly one entrance, not two (`Features/Browse/BrowseDirectory.cs:92-93`).
14. **The Browse masthead paints nothing and the directory cuts itself under it.** `ClipBelow(84)` with a 24-DIP top `EdgeFade`
    that arms only once the clip engages (`Features/Browse/BrowseDirectoryPage.cs:41-49`,
    `Features/Browse/BrowseMastheadMetrics.cs:11-12, 24-31`). Content dissolves into the band; it is never guillotined by it and
    it never softens at rest.
15. **A genre search result IS a browse-category link** - same `BrowseTiles.Link` cell, same `BrowseLayout.LinkColumns` count,
    same `BrowseLayout.StarGrid` track math (`Features/Search/SearchGenreTiles.cs:15-21, 72-85`). Not a wall of coloured plates.
    One difference from the directory's own Genres band, and it is deliberate: `SearchGenreTiles.Columns` builds its
    `BrowseTileModel` with `Color: null` (`SearchGenreTiles.cs:81`), so **every search-genre pip is `Tok.AccentDefault`** -
    `SearchGenre.Accent` (`Wavee.Core/Library/Library.cs:49`) is fetched and then dropped. "The genre's accent still means
    something on the page it opens, which is where it stays" (`SearchGenreTiles.cs:20-21`).
16. **The suggestion popup only says "No results found" for a confirmed empty answer.** `SuggestState` distinguishes
    Idle / Pending / Results / Empty / Failed; Pending shows an indeterminate progress bar over the PREVIOUS answer's rows, and
    Failed offers Retry (`Features/Search/OmnibarSuggestQuery.cs:8-22, 55-58`, `Features/Shell/ShellToolbar.cs:398-435`).

---

## 1. Anatomy

### 1.1 - 0.2.9 composition: Search results page

```
ContentHost route "search"                                    Features/Shell/ContentHost.cs:258-260   keep-alive slot "page:search"
└─ SearchPage(Route)                                          Features/Search/SearchPage.cs:27-127    Component; _query = route.Arg.Trim() FROZEN at mount
   ├─ Ctx.Provide(LazyScroll.Slot, pageScroll)                 :93                                    page scroll offset for lazy children
   ├─ header column  Gap S, Pad (L,M,L,S)                      :102-118
   │  ├─ WaveeType.SurfaceDisplay(q.ToLowerInvariant())        :111-115                               the query echo, 1 line, ellipsis
   │  └─ ChipBar(results)                                      :249-280                               wrapping facet tab row, AlignItems End
   │     ├─ ChipBarSkeleton()                                  :227-247                               11 placeholder pills while first load pends
   │     └─ FacetTab(i, name, total, selected) x N             :282-324                               label + count + 3-DIP underline
   └─ ScrollView(resultBody)  ScrollKey "search:<facet>"       :119-124                               per-facet scroll memory
      └─ resultBody  Pad (L,S,L, 72+24)                        :64-91
         └─ BoxEl Key "facet-body:<facet>"  Animate=PageSlide  :70-89                                 the slide target
            └─ Skel.Region(results, SearchShimmer, ResultsFor) :84-87                                 reveal StaggerRows (None for Tracks), smoothResize false
               ├─ SearchShimmer(facet)                         :136-219                               ShimmerAll / ShimmerCardGrid / ShimmerRows
               ├─ ErrorState.Build(results.Error)              :86                                    failed arm
               └─ ResultsFor(r, chip, q, svc, go)              :392-423                               facet dispatch
                  ├─ All        -> AllView                     :425-468
                  │  ├─ SearchHero(hits[0])   Key "hero:<uri>" Features/Search/SearchHero.cs:17-148   228-DIP top-result card
                  │  ├─ SearchHitsGrid (hits[1..])             SearchPage.cs:945-1043                 N x N PagedShelf of rows, pips+chevrons
                  │  ├─ SearchAllList  (when TopHits is empty) :636-905                               hero row + interleaved fallback rows
                  │  ├─ SearchMediaGrid (playlist rail)        :1046-1111                             148-188 DIP GridCard shelf + TickHeader
                  │  ├─ SearchGenreTiles(q, go)  Key "genres:" Features/Search/SearchGenreTiles.cs:22 own fetch, BrowseTiles.Link star grid
                  │  └─ SearchRelatedQueries(q, go)            SearchGenreTiles.cs:101-169            wrapping lowercase query links
                  ├─ Tracks     -> SongsGrid -> SearchSongsGrid SearchPage.cs:523-530, 910-940        AutoGrid(280, 12, 64) of HitRow cells
                  ├─ Albums     -> AlbumGrid  -> SearchFacetGrid :536-540, SearchFacetGrid.cs:25-165  LazyGrid over a VirtualCollection
                  ├─ Playlists  -> PlaylistGrid -> SearchFacetGrid :542-546
                  ├─ Artists    -> FlatList(ResultRow x N)     :420, 599-622                          60-DIP circular-art rows + TypePill
                  ├─ Genres     -> SearchGenreTiles.Grid(header:false) :409-410
                  ├─ Podcasts   -> HitsList(ShowHits(r.Shows))  :401-402, 570-582
                  ├─ Episodes   -> HitsList(EpisodeHits(...))   :403-404, 584-596
                  ├─ Audiobooks -> HitsList(r.Audiobooks)       :399-400
                  ├─ Profiles   -> HitsList(r.Profiles)         :405-406
                  ├─ Authors    -> HitsList(r.Authors)          :407-408
                  └─ (any dedicated facet with 0 of everything) -> EmptyState.Build(NoResults, NoResultsSub(q))  :412-413
```

`SearchAllList` - the shared row factory, reached by every branch above:

```
SearchAllList : Component                       SearchPage.cs:636-905   reads Model from Context (SearchAllList.Props, :649)
├─ Model(Results, Go, PlayTrack, PlayContext, PlayKnownTrack, Filter?, EmptyTitle?, Hits?)   :638-648
├─ Build(r, ...)          :709-735   hits[0] large + hits[1..] small, Gap S; else FallbackRows; else EmptyState
├─ BuildHits(hits, ...)   :746-756   explicit facet list, all small, Gap S
├─ HitRow(h, ..., large)  :759-785   -> MediaCard.Row(plated:false) with search extras
├─ FallbackRows(r, ...)   :800-835   no TopHits: top artist|album|playlist large, 8 tracks, artists spliced at i==2 / i==5, cap 14, +4 albums, +4 playlists
├─ HitMenu / EntityDrag   :689-704   track hit resolves the full Track via TrackOf, else uri-only card menu / entity payload
├─ SaveButton(saved, tog) :890-896   32x32 circle, Accept/Add glyph
└─ OpenFor(kind, ...)     :876-886   artist:/album:/pl:/show: routes; Episode plays; Genre -> SearchRoutes.OpenGenre
```

### 1.2 - 0.2.9 composition: the omnibar suggestion flyout (chapter 18 owns the field chrome)

```
MergedChromeRow (shell)                                  Features/Shell/ShellToolbar.cs (chapter 18)
├─ OmnibarSuggestStore  (one per shell)                  :151-160   OmnibarSuggestQuery + Version signal + Highlight cursor
└─ FluentRichOmnibar(text, go, store, ...)               :164-334
   ├─ UseEffect(typed)  -> query.Begin(typed)             :205-209   UNDEBOUNCED keystroke edge -> Pending immediately
   ├─ UseDebouncedValue(generation, 150ms)                :215-222   the fetch edge, keyed on GENERATION not text
   ├─ completion (inline ghost)                           :246-251   SearchSuggestions.GhostFor(typed, queries)
   ├─ InvokeSelection(i) / MoveSelection(d)               :265-312   6 query rows + 10 item rows; wraps through -1
   └─ AutoSuggestBox.Create(..., presenter)               :328-332   32 DIP field, cornerRadius 0 -> Radii.Control, maxFillWidth 480
      └─ OmnibarSuggestionsPopup                          :339-617
         ├─ ProgressBar.Indeterminate(width)  (Pending)   :434
         ├─ QueryRow x <=6    (40 DIP, glyph + 700/400 split highlight)   :449-462, 586-614
         ├─ Divider           (1 px, margin 16/4)         :556-561
         ├─ RichRow x <=10    (58 DIP, 44 art, Play/Heart/More/TypePill)  :464-523
         └─ Notice(width, text, trailing?)                :439-447   Empty -> "No results found"; Failed -> + Retry button
```

### 1.3 - 0.2.9 composition: Browse directory

```
ContentHost route "browse"                               Features/Shell/ContentHost.cs:254-256   keep-alive slot "page:browse-home"
└─ BrowseDirectoryPage                                   Features/Browse/BrowseDirectoryPage.cs:15-67
   ├─ Ctx.Provide(LazyScroll.Slot, pageScroll)           :51
   ├─ Ctx.Provide(BrowseDirectory.Props, browseModel)    :52       Model(OnOpenCategory, OnOpenFeature)
   └─ ScrollView  ScrollKey "browse"                     :53-65
      ├─ spacer BoxEl Height = BodyTop (100)             :58       the masthead reserve, OUT of the clipped node
      └─ directory  Pad (36,0,36, 72+24), Gap L          :41-49
         │  .ClipBelow(84, v => underBand.Value = v)     :49       cut at the band's lower edge
         │  EdgeFade(Top, 24) when underBand             :45-47
         └─ BrowseDirectory                              Features/Browse/BrowseDirectory.cs:28-363
            └─ Skel.Region(cats, Skeleton, Body)         :92-96    reveal None, smoothResize false
               ├─ Skeleton() = Body(seeds).Skeletonized(true)      :352-362
               ├─ EmptyState.Build(Strings.Browse.Unavailable)     :95
               ├─ ErrorState.Build(err, onRetry: cats.Refresh)     :96
               └─ Body(categories, model, chartsBandAt)            :102-141   Gap L, gutterless
                  walks BrowseTaxonomy.BandOrder  (Top, Charts, ForYou, Genres, MoodActivity, More)   :128-132
                  ├─ Top band          Animate Row(1)    :249  BandLabel + WrapRow(BrowseTiles.Word, gap 8)
                  ├─ Charts band       Animate Row(2)    :263-279  Skel.Region(charts) -> HomeModules.FoldDeck (own header, no BandLabel)
                  ├─ For you band      Animate Row(3)    :250  BandLabel + WrapRow(BrowseTiles.Name, gap 8)
                  ├─ Genres band       Animate Row(4)    :251  BandLabel + Responsive.Of(LinkGrid, fb 900)
                  ├─ Mood & activity   Animate Row(5)    :252  BandLabel + Responsive.Of(BarGrid, fb 900)
                  └─ More band         Animate Row(6)    :253  BandLabel + Responsive.Of(MoreGrid, fb 900)
```

### 1.4 - 0.2.9 composition: Browse category page

```
ContentHost route "browse:<pageUri>"                     Features/Shell/ContentHost.cs:271-273   keep-alive slot "page:browse"
└─ BrowsePageHost(Route)                                 Features/Browse/BrowsePageHost.cs:17-60
   └─ Ctx.Provide(BrowsePage.Props, model)  +  Embed.Comp(BrowsePage) Key "browse-page:<pageUri>"   :53-58
      └─ BrowsePage                                      Features/Browse/BrowsePage.cs:27-730
         ├─ UseEffect -> mastheadStore.Publish(route, ShellMastheadState(title, null, toolsVisible, toolsLoading, toolsAction))  :188-193
         │     ^ renders OUTSIDE this page, in ShellMastheadBand (Features/Shell/ShellMastheadBand.cs:21-131)
         ├─ outer column  Gap L, Pad FamilyBodyPad(L) = (36,100,36,16)   :201-205
         └─ Skel.Region(page, ShimmerBody, BodyBelow)    :218-225   reveal FadeOnly, smoothResize false, explicit shimmerSource
            ├─ ShimmerBody(model)                        :258-268   ShimmerRelated + 2 x ShimmerShelf; NO PagedShelf, NO Virtual
            ├─ FramedContent(EmptyBody)                  :372-381, 613-621  ScrollView + EmptyState + ExploreAll
            ├─ FramedContent(ErrorState.Build(err))      :224
            └─ BodyBelow -> BrowsePageLayout.Of(effective).Mode    :323-343
               ├─ Shelves / FlattenTwoStacked -> ShelvesBody       :346-367
               │  └─ ScrollView  ScrollKey "browse:<pageUri>", bottom pad 96
               │     ├─ per section: CategoryBlock (Related/CategoryGrid)  :678-693  DrillHeader + Responsive LinkGrid
               │     ├─ per section: Shelf -> PagedShelf of MediaCard.Shelf :638-676  Key "browse-shelf:<uri>"
               │     └─ ExploreAll link                             :698-705
               └─ FlattenOne / FlattenTwoConcat -> FlattenBody     :388-479
                  ├─ pinned column: non-shelf CategoryBlocks + ExploreAll   :448-459
                  └─ grid slot Grow 1 MinHeight 0
                     ├─ Responsive.Of(HomeModules.SectionGrid(cards, key, w, ...), fb 1100, grow 1)  :472-473
                     └─ HomeSectionAppendPreloader  Key "browse-flatten-append:<uri>:<cursor>"       :434-439
```

### 1.5 - The same trees in 0.3 terms

Props freeze at mount. The column below says exactly how each node receives changing data.

| 0.2.9 node | 0.3 home | form | inputs | how data reaches it |
|---|---|---|---|---|
| `SearchPage` | `Entities/Search.Page.cs` | `sealed partial class SearchPage : Component` nested in `readonly partial struct Search` | `Search` handle built from the route arg at mount | the handle's SLOT is frozen; content arrives through `Entities.Current.Search.Changed` read via `UseSignal` - never re-ctor |
| query echo | `Search.Page.cs` | static `Element QueryEcho(StringId q)` | `StringId` | the committed query is immutable per slot; no signal needed |
| `ChipBar` / `FacetTab` | `Search.UI.cs` | static `Element FacetRow(Search s, Signal<int> chip)` | handle + a `Signal<int>` owned by the page | chip index is a Signal; counts read `s.ChipTotal(facet)` each render under the table signal |
| `ChipBarSkeleton` | `Search.UI.cs` | static `Element FacetRowSkeleton()` | none | pure |
| `SearchShimmer` / `Shimmer*` | `Search.UI.cs` | static `Element Shimmer(SearchFacet f)` | facet enum | pure |
| `ResultsFor` dispatch | `Search.Page.cs` | static `Element Body(Search s, int chip)` | handle + chip | re-runs under the table signal |
| `SearchHero` | `Search.UI.cs` | static `Element TopResult(Search s, int hitIndex)` - **not a Component** | handle + index | the 0.2.9 ctor arg (`SearchTopHit`) is exactly the props-freeze trap; in 0.3 read the hit out of the edge each render. If it must stay a Component, it takes `Key = "hero:" + hit.UriId` (a remount) |
| `SearchAllList` | `Search.UI.cs` | static `Element HitRow(Search s, int i, bool large)` | handle + index | 0.2.9 passed its Model by `Ctx.Provide`; 0.3 passes the handle by argument - no context needed |
| `SearchHitsGrid` | `Search.UI.cs` | static `Element BestMatches(Search s, float width)` inside `Responsive.Of` | handle + measured width | column count from width (hysteresis field on the page component) |
| `SearchSongsGrid` | `Search.UI.cs` | static `Element SongsGrid(Search s)` | handle | `AutoGrid` needs no measure |
| `SearchFacetGrid` | `Search.UI.cs` | static `Element FacetGrid(Search s, SearchFacet f)` over `ItemsView.CreateBound` | handle + facet | the 0.2.9 `VirtualCollection` + `Seed` + re-pushed props disappear: the edge IS the collection and its `State`/`Total` columns are the paging state (plan §4.3) |
| `SearchMediaGrid` | `Search.UI.cs` | static `Element PlaylistRail(Search s)` | handle | |
| `SearchGenreTiles` | `Search.UI.cs` | static `Element GenreGrid(Search s, float width)` | handle + width | its own 0.2.9 fetch folds into the page's one `Entities.Ensure` demand |
| `SearchRelatedQueries` | `Search.UI.cs` | static `Element RelatedQueries(Search s)` | handle | same |
| `SearchChrome.TickHeader` | `Platform/Design.cs` | static `Element TickHeader(StringId, Action?)` | | shared with Browse's band tick |
| `OmnibarSuggestQuery` | `Entities/Search.cs` **CORE** | `sealed class SuggestQuery` verbatim | | engine-free today; port byte-for-byte |
| `OmnibarSuggestStore` + `FluentRichOmnibar` + `OmnibarSuggestionsPopup` | `Shell/Shell.UI.cs` / `Shell/Shell.Host.cs` | Components (the field owns focus/popup lifetime) | `Signal<string>` text, the store | chapter 18 owns the field; the ROW factories move to `Search.UI.cs` |
| `BrowseDirectoryPage` | `Entities/Browse.Page.cs` (**settled 2026-09-12; in the tree, owner P**) | `sealed partial class DirectoryPage : Component` | none | |
| `BrowseDirectory` | `Browse.UI.cs` | static `Element Directory(ReadOnlySpan<int> categorySlots, BrowseNav nav)` | slot span + a nav record | 0.2.9 used `Ctx.Provide(BrowseDirectory.Props)`; 0.3 passes the nav struct by argument |
| `BrowseTiles.Word/Name/Link/Bar/Peek` | `Browse.UI.cs` | five static `Element` factories over a `BrowseCat` handle | handle (+ `cardW` for Peek) | |
| `BrowseLayout` (columns, heights, StarGrid) | `Browse.cs` **CORE** | static class, verbatim | | pure, testable |
| `BrowseTaxonomy` + `ChartPages` + `ChartSections` | `Browse.cs` **CORE** | verbatim | | |
| `BrowseDirectorySeeds` | `Browse.cs` **CORE** | verbatim | | must stay test-visible (`BrowseTaxonomyTests` pins the per-band counts) |
| `BrowsePageHost` | `Browse.Page.cs` | route parse + handle resolve | `Route` | |
| `BrowsePage` | `Browse.Page.cs` | `sealed partial class CategoryPage : Component` | `BrowsePage` handle | the 0.2.9 `Embed.Comp(...) with { Key = "browse-page:" + pageUri }` remount stays - a different page uri is a different subtree |
| `BrowsePageLayout` | `Browse.cs` **CORE** | verbatim | | |
| `BrowseMastheadMetrics` | `Browse.cs` **CORE** | verbatim | | |
| `BrowseRoutes` / `SearchRoutes` | `Shell/Shell.cs` **CORE** | verbatim | | `Shell.cs` is the routes file (plan §4.11) |
| `BrowseDirectoryStore` / `BrowsePageStore` | **deleted** | - | - | the tables themselves survive page eviction; `Store.cs` persists them. See §7 |

**Where a remount (`Key`) is still required in 0.3:** the facet body (`"facet-body:<facet>"` - the slide recipe is attached to
the keyed node and a key change is what makes it an enter/exit pair), the category page (`"browse-page:<pageUri>"`), and every
`Responsive.Of` grid whose column count is baked into a child key (`"browse-link-grid:<cols>"`, `"browse-mood-grid:<cols>"`,
`"browse-more-grid:<cols>"`, `"search-genres-grid:<cols>"`). Everything else that used a Key in 0.2.9 (`"hero:<uri>"`,
`"genres:<q>"`, `"related:<q>"`, `"facet-grid:<facet>:<q>"`, `"best:<uri>:<n>"`, `"all-pl:<uri>:<n>"`,
`"songs-grid:<uri>:<n>"`, `"media-shelf:<n>:<first>"`, `"hits-shelf:<n>:<cols>:<rows>:<pager>:<first>:<hideArt>"`) exists only
to force a Component whose ctor args froze - those disappear with the Components.

---

## 2. Wireframes

Scale: **1 monospace char ≈ 8 DIP**, 1 text row ≈ 20 DIP. "Pane" = the width the page component receives; the window width that
produces it depends on the sidebar (240 DIP open / 56 rail) and the right rail - **UNVERIFIED here, chapter 18 owns the shell
insets**; the pane figure is what the wireframe is drawn to.

### W1 - Search results, All facet, fully loaded @ pane 960

```
 |<----------------------------------------- pane 960 -------------------------------------------->|
 +--------------------------------------------------------------------------------------------------+
 |                                                                                                  |  <- Pad top 12 (Spacing.M)
 |  sleep                                                                                           |  SurfaceDisplay 40/52/400, lowercased, 1 line
 |                                                                                                  |  Gap 8 (Spacing.S)
 |  All   Playlists 75  Songs 67  Episodes 55  Genres 8  Podcasts 61  Albums 91  Audiobooks 50      |  FacetTab row, Wrap, AlignItems End
 |  ====                                                                                            |  <- 3 DIP accent underline under selected
 |  Artists 81  Authors 50  Profiles 50                                                             |  wrapped second row
 |  ______________________________________________________________________________________________  |  Pad bottom 8
 | <ScrollView ScrollKey "search:0">                                                                |
 |  +--------------------------------------------------------------------------------------+       |  resultBody Pad (16,8,16,96)
 |  | Top result . Playlist                                             .-------------.     | 228   |  SearchHero card, Radii.Card 8,
 |  |                                                                  /              /|    |       |  FillCardDefault + 1px StrokeCardDefault
 |  | Sleep                                                           /   cover      / |    |       |  radial accent wash centred (0.88, 0.40)
 |  |                                                                /   300x260    /  |    |       |  rotated -2.5deg, OffsetX +40, OffsetY -16
 |  | Gentle ambient piano . Spotify                                /_____________ /   |    |       |  PageHero 28/36/600, max 2 lines
 |  | (matched title) (Lyrics match)                               |              |    |    |       |  Chip: Radii.Full, 1px StrokeControlDefault
 |  | [ > Play ] [ Open page ] [ + ] [...]                         |              |    |    |       |  Play = WaveeCta.Play(accent) 36 DIP pill
 |  +--------------------------------------------------------------------------------------+       |
 |                                                                                          Gap 16  |
 |  | Best matches                                                        < >  o o . o o    |       |  TickHeader 3x14 + RailHeader 20/28/600
 |  +------------------------------------+------------------------------------+-------------+       |  SearchHitsGrid: 3 cols x 3 rows
 |  |[art] Title                    [+]  |[art] Title                    [+]  |[art] ...    | 64    |  MediaCard.Row(plated:false) cells, gap 12
 |  |      Artist . Album                |      Artist                        |             |       |
 |  +------------------------------------+------------------------------------+-------------+       |
 |  |[art] Title                    [+]  |[o]   Artist name          [Follow] |[art] ...    | 64    |
 |  +------------------------------------+------------------------------------+-------------+       |
 |  |[art] Title                    [+]  |[art] Podcast name        [Follow]  |[art] ...    | 64    |
 |  +------------------------------------+------------------------------------+-------------+       |
 |                                                                                          Gap 16  |
 |  | Playlists  >                                                        < >  o . o o      |       |  TickHeader with chevron -> SelectFacet(Playlists)
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                 |       |  SearchMediaGrid: GridCard 148-188 DIP
 |  | cover   | | cover   | | cover   | | cover   | | cover   | | cover   |                 | 220   |  ShelfHeight(148) = 148 + 72
 |  |         | |         | |         | |         | |         | |         |                 |       |
 |  | Title   | | Title   | | Title   | | Title   | | Title   | | Title   |                 |       |
 |  | Owner   | | Owner   | | Owner   | | Owner   | | Owner   | | Owner   |                 |       |
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                 |       |
 |                                                                                          Gap 16  |
 |  | |tick| Genres                                                                         |       |  TickHeader (no chevron), Gap 12 to the grid
 |  |  [#] Sleep            |  [#] Rain sounds       |  [#] Noise for sleep                  |       |  BrowseTiles.Link, 3-col StarGrid
 |  |  [#] White Noise      |  [#] Lullabies         |  [#] Sleep & Meditation               |       |  colGap 12, rowGap 8; pip ALWAYS AccentDefault
 |                                                                                          Gap 16  |
 |  | |tick| Related searches                                                                |       |  TickHeader (no chevron), Gap 8 to the links
 |  |  sleep music     sleep music for deep sleeping     sleep meditation     slaap          |       |  links: ModuleHeader 20/28 Weight 400 lowercase
 |  +--------------------------------------------------------------------------------------+       |
 |                                                                             Pad bottom 96        |  PlayerDock.Reserve 72 + Spacing.XXL 24
 +--------------------------------------------------------------------------------------------------+
```

### W2 - Search results, All facet, loading @ pane 960 (`SearchShimmer(All)` + `ChipBarSkeleton`)

```
 +--------------------------------------------------------------------------------------------------+
 |  sleep                                                                                           |  the query echo is REAL from frame one
 |                                                                                                  |
 |  [====] [======] [======] [=====] [=======] [=====] [========] [==========] [=====] [=====]      |  11 pills, 16 DIP tall, Radii.Control 4
 |  [=====]                                                                                         |  FillSubtleSecondary, pad (12,8,12,4)
 |                                                                                                  |  each followed by a 3-DIP transparent spacer
 |  +--------------------------------------------------------------------------------------+       |
 |  |                                                                  +--------------+      | 228   |  ShimmerAll hero: FillCardDefault plate,
 |  | [=====]                    90x12                                 |              |      |       |  1px StrokeCardDefault, Radii.Card
 |  | [==================]      260x28                                 |  300x260     |      |       |  copy Justify End, pad (16,16,20,16)
 |  | [============]            170x16                                 | FillSubtle   |      |       |  art FillSubtleTertiary, AlignSelf Center
 |  | (=======)                 100x32 Radii.Full                      |  Tertiary    |      |       |
 |  +--------------------------------------------------------------------------------------+       |
 |                                                                                          Gap 16  |
 |  +-------+ +-------+ +-------+ +-------+ +-------+                                               |  ShimmerCardGrid: 12 cards, 168 wide
 |  |168x168| |168x168| |168x168| |168x168| |168x168|                                               |  Wrap row, Gap 12, Radii.Card
 |  |       | |       | |       | |       | |       |                                               |
 |  |[====] | |[====] | |[====] | |[====] | |[====] |   140x14 FillSubtleSecondary                  |
 |  |[==]   | |[==]   | |[==]   | |[==]   | |[==]   |    92x12 FillSubtleTertiary                   |
 |  +-------+ +-------+ +-------+ +-------+ +-------+                                               |
 |  (wraps to a second row of 5, then 2)                                                            |
 +--------------------------------------------------------------------------------------------------+
```

### W3 - Facet chip row, real, wrapped @ pane 960 and @ pane 560

```
 pane 960 (row content width 960 - 2*16 gutters is NOT applied here; the chip bar sits in the header column, Pad (16,12,16,8))

  All    Playlists 75   Songs 67   Episodes 55   Genres 8   Podcasts 61   Albums 91   Audiobooks 50
  ====
  |<-->| each tab: Pad (12,8,12,4)  Gap 4 between label and count
  Artists 81   Authors 50   Profiles 50
  ^ Wrap = true, AlignItems = End -> every tab's underline row sits on one baseline

 pane 560

  All    Playlists 75   Songs 67   Episodes 55
  ====
  Genres 8   Podcasts 61   Albums 91
  Audiobooks 50   Artists 81   Authors 50
  Profiles 50

 tab anatomy (Features/Search/SearchPage.cs:282-324):
  +-----------------------+
  | 12 |label| 4 |count|12|   label: Ui.Body 14/20; selected -> Tok.TextPrimary, else Tok.TextSecondary (HoverColor TextSecondary)
  |  8 above, 4 below     |   count: Ui.Caption 12/16, Tok.TextTertiary; omitted when total == 0 and for "All"
  +=======================+   selected: 3 DIP, Radii.Full, Tok.AccentDefault, AlignSelf Stretch (full tab width)
                              unselected: 3 DIP transparent spacer -> every tab is the same height
```

### W4 - Top-result hero anatomy @ pane 960 (`Features/Search/SearchHero.cs:79-146`)

```
  card: Height 228 (fixed), Grow 1, AlignSelf Stretch, ClipToBounds, Corners Radii.CardAll (8)
        Fill Tok.FillCardDefault, BorderWidth 1, BorderColor Tok.StrokeCardDefault, ZStack
        Role Button, OnClick = open, Draggable, .Interactive(Interaction.Card).WithMenu(menu)

  +====================================================================================================+
  | [layer 0] radial wash: GradientShape.Radial, RadialCenter (0.88, 0.40), RadialRadius (1.2, 0.8)     |
  |           stop 0.00 = ChromeAccent(scheme) with A = 0.55                                            |
  |           stop 0.58 = ChromeAccent(scheme) with A = 0.00       (absent when the cover is ungradeable)|
  | [layer 1] row: copy (Grow 1, MinWidth 0) | cover (Shrink 0)                                         |
  |                                                                                                     |
  |   Pad (16, 16, 20, 16), Direction 1, Gap 8, Justify End          .- - - - - - - -.                  |
  |                                                                 |                 |  <- OffsetY -16 |
  |   Top result . Playlist         <- Eyebrow 12/16/600 +30/1000,  |   300 x 260     |     Rotation    |
  |                                    Tok.AccentTextPrimary        |   Radii.Card 8  |     -2.5 deg    |
  |   Sleep                         <- PageHero 28/36/600,          |  HitTestVisible |     OffsetX +40 |
  |                                    max 2 lines, ellipsis        |     = false     |     clipped by  |
  |   Gentle ambient . Spotify      <- RichText.OfRow(subtitle, 12, |                 |     the card    |
  |                                    TextSecondary, AccentText-   `- - - - - - - - -'                 |
  |                                    Primary) - anchors navigate                                      |
  |   (matched title) (Lyrics match)   chips: Pad (8,4,8,4), Radii.Full, transparent, 1px               |
  |                                    StrokeControlDefault, Eyebrow TextSecondary. Wrap, Gap 8         |
  |   [ > Play ] [ Open page ] [+] [...]   Gap 8, AlignItems Center                                     |
  +=====================================================================================================+
      ^ Play      = WaveeCta.Play(accent)  -> 36 DIP pill, Radii.Full, Pad (18,6,18,7), Bold, glyph Play
        Open page = WaveeCta.Pill(Strings.Search.OpenPage, ButtonAppearance.Standard)  (only when canOpen && !isTrack)
        trailing  = FollowButton (Followable) | SaveButton 32x32 (Track) | omitted
        "..."     = MediaCard.MoreInline(true) 36 DIP, ClickRequestsContext, wrapped in ToolTip(Strings.Common.More)
```

Action visibility (`Features/Search/SearchHero.cs:43-46, 65-72`):

| Hit kind | Play | Open page | trailing | `...` (menu) | chips |
|---|---|---|---|---|---|
| Track | yes | no (open == play) | SaveButton | yes (full track menu via `TrackOf`, else the uri card menu) | MatchedTitle / LyricsMatch / AccessLabel |
| Artist, Album, Playlist | yes | yes | FollowButton when `Followable` | yes (container grammar) | same |
| Podcast, Audiobook | yes | yes | FollowButton when `Followable` | yes - but the **SHOW arm**: Play · Open · Pin · Share only (`Actions/Menus.cs:552`, `ShowCard`) | same |
| Episode | yes | yes | none | **no** - `Menus.Card` returns null for an episode uri (no episode detail route, no deposit seam; `Menus.cs:541-543`) | same |
| Genre | no (`canPlay` false) | yes | none | **no** - `EntityUri.KindOf` gives no `ActionTarget`, so `Menus.Card` returns null | same |
| User (profile), Author | no | no | none | **no** (same reason) | same |

The `...` column is not decoration: `SearchHero` only appends the button **when a menu actually resolved**
(`if (menu is not null)`, `SearchHero.cs:72`), so the hero for a genre / profile / author / episode has no `...` at all and
no `.WithMenu` either - right-clicking it does nothing. The same rule applies to every `HitRow` (`SearchPage.cs:781`).

`canPlay` = kind not in {User, Genre, Author} (`SearchHero.cs:44`); `canOpen` = kind in {Artist, Album, Playlist, Podcast,
Audiobook, Genre, Episode} (`:45-46`). The hero's own hover/press is `Interaction.Card` on the plate, and the Play / Open-page
pills carry the `WaveeMotion.ScaleStandard` tier (hover 1.04, press 0.96) that `WaveeCta.Pill` attaches
(`Design/WaveeCta.cs:104-106`).

### W5 - Best matches (`SearchHitsGrid`) at three column counts

```
 content width w = pane - 32 (resultBody gutters).  nominal cols = clamp( floor((w+12) / 292), 1, 3 )
 drop is hysteretic: at cols c, shrink to nominal only when w < c*280 + (c-1)*12 - 24
   c=3 -> drops below w 840 ;  c=2 -> drops below w 548
 rows = cols, except cols == 1 -> rows = 3 (SingleColRows)
 maxColumns = min(cols, max(1, ceil(n / rows)))  - SearchPage.cs:998; with 4 hits at 3 cols the page is 2 wide, not 3
 page height reservation = rows * rowH, rowH = 64, or 128 when ANY hit on the page is an Audiobook
 the shelf Key embeds _hideTrackArtwork (SearchPage.cs:1021-1022): flipping the track-artwork setting REMOUNTS this shelf

 w 928 (pane 960): 3 cols x 3 rows = 9 per page
 +--------------------+--------------------+--------------------+
 |  cell 1            |  cell 2            |  cell 3            |   each cell = a column BoxEl (MinWidth 0, Stretch)
 +--------------------+--------------------+--------------------+   containing one MediaCard.Row(large:false, plated:false)
 |  cell 4            |  cell 5            |  cell 6            |   cell gap 12 both axes, edgeFade 16, snap Page
 +--------------------+--------------------+--------------------+
 |  cell 7            |  cell 8            |  cell 9            |
 +--------------------+--------------------+--------------------+
     header row:  |tick| Best matches                    < >  o . o o

 w 668 (pane 700): 2 cols x 2 rows = 4 per page
 w 368 (pane 400): 1 col x 3 rows = 3 per page   <- deliberately 3, never a 1-cell pager
```

### W6 - Songs facet @ pane 960 (`SearchSongsGrid`, `Features/Search/SearchPage.cs:910-940`)

```
  AutoGrid(minColWidth 280, gap 12, rowHeight 64) - CSS repeat(auto-fill, minmax(280, 1fr))
  at w 928: floor((928+12)/292) = 3 columns, cell width (928 - 24)/3 = 301

  +-----------------------------+-----------------------------+-----------------------------+
  |[48] Track title        [+]  |[48] Track title        [+]  |[48] Track title        [+]  | 64
  |     Artist, Artist          |     Artist                  |     Artist                  |
  +-----------------------------+-----------------------------+-----------------------------+
  |  ... up to 50 rows (SearchPageSize), scrolling with the page - no nested pager, no pips  |
  +-----------------------------------------------------------------------------------------+

  Skel.Region reveal for THIS facet is SkelReveal.None (SearchPage.cs:85) - every other facet uses StaggerRows.
```

### W7 - Albums / Playlists facet @ pane 960 (`SearchFacetGrid`, LazyGrid over a VirtualCollection)

```
  LazyGrid(minColWidth 180, gap Spacing.L 16, rowExtra CardChrome 50 + RowGap 20 = 70, overscanRows 4)
  cols  = max(1, floor((w + 16) / 196))          at w 928 -> 4
  cellW = max(90, (w - (cols-1)*16) / cols)      at w 928 -> (928 - 48)/4 = 220
  rowH  = cellW + 70 = 290 ;  card Height = cellW + 50 = 270

  +------------------+   +------------------+   +------------------+   +------------------+
  |                  |   |                  |   |                  |   |                  |
  |   cover 220 sq   |   |   cover 220 sq   |   |   cover 220 sq   |   |   cover 220 sq   |  <- MediaCard.GridCard
  |                  |   |                  |   |                  |   |                  |     square, Radii.Card
  |                  |   |                  |   |                  |   |                  |
  | Album title      |   | Album title      |   | Album title      |   | Album title      |  50 DIP chrome:
  | Artist           |   | Artist           |   | Artist           |   | Artist           |  title + one meta line
  +------------------+   +------------------+   +------------------+   +------------------+
        gap 16                 gap 16                 gap 16                     row gap 20

  unrealised cell (page not landed yet) - Features/Search/SearchFacetGrid.cs:152-164:
  +------------------+   Key "search-card:placeholder", Height cellW + 50, Pad (8,8,8,12), Radii.Card
  | +--------------+ |   ImageEl AspectRatio 1, Placeholder Tok.FillSubtleSecondary, Radii.Card
  | |              | |   bar 1: Height 13, Stretch, MaxWidth 150, Radii 4, FillSubtleSecondary
  | +--------------+ |   bar 2: Height 11, Width 92,  Radii 4, FillSubtleSecondary
  | [============]   |   -> exactly a real card's box, so a landing page never shifts the rows around it
  | [======]         |
  +------------------+

  Page 0 is SEEDED from the search page's own results (SearchFacetGrid.cs:70-72): mounting costs no request.
  Wire page size 50 == SearchPage.SearchPageSize, so chunk 0 fills exactly (SearchFacetGrid.cs:92).
```

### W8 - Genres facet / All-tab genre section @ pane 960 and 560

```
  BrowseLayout.LinkColumns(w):  w > 720 -> 3 ;  w > 380 -> 2 ;  else 1     (fixed bands, never a floor-divide)
  StarGrid(cols, colGap Spacing.M 12, rowGap Spacing.S 8), Key "search-genres-grid:<cols>"

  w 928 -> 3 columns
  +--------------------------+ +--------------------------+ +--------------------------+
  |[#] Sleep                 | |[#] Rain sounds           | |[#] Noise for sleep       |   cell = BrowseTiles.Link
  +--------------------------+ +--------------------------+ +--------------------------+   Pad (4,4,4,4) -> (XS,4,XS,4)
  |[#] White Noise           | |[#] Lullabies             | |[#] Sleep & Meditation    |   Corners Radii.Control 4
  +--------------------------+ +--------------------------+ +--------------------------+   HoverFill FillControlSecondary
       ^ 8x8 pip, Corners 2, Fill = Tok.AccentDefault ALWAYS on Search (SearchGenreTiles.cs:81 passes Color: null,
         so WaveePalette.ToColor never runs here; the directory's own Genres band DOES carry the category colour)
         label: Ui.Body 14/20 .Secondary(), HoverColor Tok.AccentTextPrimary, 1 line, ellipsis, Gap 8

  w 560 -> 2 columns.  w 360 -> 1 column (AlignSelf Stretch backfills).

  All-tab section adds a header row above: |tick| Genres      (SearchGenreTiles.cs:60-64, Gap 12)
  Dedicated-facet Genres tab passes header: false (SearchPage.cs:410).

  LOADING (All-tab section only): its own Skel.Region over its own fetch, shimmered from EIGHT seed cells -
  "Alternative / Jazz / Hip-Hop / Classical / R&B / Electronic / Indie / Metal" (SearchGenreTiles.cs:87-97),
  rendered through the SAME Link cells and skeletonized, so the bars are sized from those real text runs.
  smoothResize: false and a non-zero shimmer height are both load-bearing (:41-45, 51).
```

### W9 - Flat hit-list facets @ pane 960 (Podcasts / Episodes / Audiobooks / Profiles / Authors)

```
  BuildHits -> a column of MediaCard.Row(large:false, plated:false), Gap 8 (SearchPage.cs:746-756)

  ordinary hit (Podcast / Profile / Author / Episode):                        Height 64
  +--------------------------------------------------------------------------------------------+
  | [48] | Show name                                                              [ Follow ]    |  art 48 sq, Radii.Control 4
  |      | Publisher                                                                            |  (circular -> radius 24 when RoundImage)
  +--------------------------------------------------------------------------------------------+  Gap 12, Pad (8,0,8,0)
     title: WaveeType.TrackTitle 14/600, 1 line, ellipsis                                          Corners Radii.ControlAll
     subtitle: RichText.OfRow(sub, 12, TextSecondary, AccentTextPrimary) - anchors navigate         Fill transparent
     hover: Tok.FillSubtleSecondary ; press: Tok.FillSubtleTertiary                                 (plated:false)

  episode hit adds a 2-line detail caption (h.Detail = ep.Description), Tok.TextTertiary, wrap:  Height auto, MinHeight 64
     -> and the PADDING changes with it: a row with a detail line is Edges4.All(8), not (8,0,8,0) (MediaCard.cs:1044-1046)

  audiobook hit (detailBelowArt: true, SearchPage.cs:779):                     Height auto, MinHeight 72
     -> the stacked shape needs a Meta or a Detail to exist: `belowArt = detailBelowArt && (hasMeta || hasDetail)`
        (MediaCard.cs:971). An audiobook hit carrying neither renders as an ordinary flat 64-DIP row.
  +--------------------------------------------------------------------------------------------+
  | [48] | Book title                                                          [ Follow ]       |  row 1 = art + text + trailing
  |      | Author                                                                               |
  |------|--------------------------------------------------------------------------------------|  Pad 8 all round, Gap 8
  | Meta line (Caption 12/600 TextPrimary)                                                      |  row 2 = meta + detail,
  | Blurb, up to two wrapped lines (Caption 12 TextSecondary)                                   |  BELOW the art, not beside it
  +--------------------------------------------------------------------------------------------+
  -> this is why SearchHitsGrid reserves AudiobookRowH 128 for a page containing any audiobook (SearchPage.cs:952-957)

  eyebrow (small rows only, SearchPage.cs:773-774):
    MatchedLyrics -> Strings.Search.LyricsMatch  in Tok.AccentTextPrimary
    else AccessLabel ("Included in Premium") in WaveeColors.PremiumText
         (light: Tok.SystemFillSuccess; dark: #1DB954 - Design/WaveeTokens.cs:120)

  hover-only control on EVERY hit row: MediaCard's own play FAB over the art - 30 DIP on a small row, 44 on `large`
  (MediaCard.cs:969, LazyOverlay), armed by HoverMotionGate so a stationary post-navigation hover re-resolve never
  lights it (MediaCard.cs:964, 1037-1038). BlocksDragArm, so pressing it is never a drag.

  no-art states on the same row:
    · track hit + `WaveeSettings.HideTrackArtwork` on -> the 48-DIP art block is NOT rendered at all (the row keeps its
      64-DIP height and the text starts at the padding edge) - SearchPage.cs:784, Design/AppearancePrefs.cs:14-18
    · a playlist cover with 4+ mosaic tiles -> Surfaces.Artwork paints a 2x2 MOSAIC, not one cover
      (Design/Surfaces.cs:241-245); 1-3 tiles collapse to the first tile
    · no image at all -> the seeded gradient placeholder (Surfaces.Artwork's WatchedPlaceholder), never an empty box
```

### W10 - Artists facet @ pane 960 (`ResultRow`, the one row that is NOT `MediaCard.Row`)

```
  FlatList, Gap 8 (SearchPage.cs:599); row at SearchPage.cs:601-622

  +--------------------------------------------------------------------------------------------+
  | ( 48 ) | Artist name                                                          ( Artist )    |  Height 60, Gap 12, Pad (8,0,8,0)
  |  circ  | Artist                                                                             |  Corners Radii.ControlAll
  +--------------------------------------------------------------------------------------------+  Fill transparent
     art: 48x48, Corners 24 (circular), clipped                                                    HoverFill FillSubtleSecondary
     title: TextEl 14/20/600 Tok.TextPrimary, 1 line, ellipsis                                     PressedFill FillSubtleTertiary
     sub:   TextEl 12/16 Tok.TextSecondary, 1 line, ellipsis, Gap 1 between the two
     pill:  TypePill - Pad (8,4,8,4), Radii.Full, Tok.FillSubtleSecondary, Eyebrow Tok.TextTertiary
```

### W11 - Search empty / error / offline

```
  empty (a dedicated facet with no tracks/artists/albums/playlists)          SearchPage.cs:412-413
  +--------------------------------------------------------------------------------------------+
  |                                                                                            |
  |                             No results found                     <- EmptyState.Build:       |
  |                                                                     WaveeType.PageHero      |
  |            Nothing matched "sleep". Try a different spelling or     28/36/600, wrapped      |
  |            keyword.                                                 caption = TrackMeta     |
  |                                                                     (Caption 12/16 .Secondary)|
  +--------------------------------------------------------------------------------------------+
     loc: search.noResults / search.noResultsSub("{query}")
     per-facet empties (HitsList): search.noAudiobookResults / noPodcastResults / noEpisodeResults
                                   / noProfileResults / noAuthorResults        (no subtitle)
     Genres facet empty -> an EMPTY BOX, no sentence. TWO code paths reach it: the All-tab section's own
       `onEmpty: () => new BoxEl()` (SearchGenreTiles.cs:48-49) and the dedicated facet's
       `Grid(genres)` early-return `if (genres is not { Count: > 0 }) return new BoxEl()` (:57, reached from
       SearchPage.cs:410). `search.noGenreResults` is defined in en-US.json:624 and referenced NOWHERE - dead key.

  empty, the OTHER three (a facet whose own list is empty while some sibling list is not)   SearchPage.cs:525, 538, 544
  +--------------------------------------------------------------------------------------------+
  |                             No results found            <- EmptyState.Build(NoResults) with |
  |                                                            NO subtitle - the :412-413 guard  |
  +--------------------------------------------------------------------------------------------+  did not fire because
     Songs   (r.Tracks.Count == 0)                                                              another list has rows
     Albums  (r.Albums.Count == 0)
     Playlists (r.Playlists.Count == 0)
     -> so the SAME headline appears with and without the `Nothing matched "<q>"` caption depending on which facet
        emptied. 0.3 should pick one (the captioned one) rather than reproduce the split.

  failed (transport / HTTP / parse)                                          SearchPage.cs:86
  +--------------------------------------------------------------------------------------------+
  |                          Something went wrong                    <- ErrorState.Build(err):  |
  |                          <common.errorSubtitle>                     EmptyState grammar,     |
  |                                                                     NO retry button here    |
  +--------------------------------------------------------------------------------------------+
     NOTE: the search page passes no onRetry, so the error state is terminal until the facet is switched.
     Browse passes one (BrowseDirectory.cs:96, `onRetry: cats.Refresh`).

  the two SECTION-level failures on the All tab (neither replaces the page):
     Genres section failed  -> ErrorState.Compact(err)   SearchGenreTiles.cs:50   (the Subtitle-rung compact arm,
                                                                                   inside the section's own slot)
     Related searches failed -> `() => new BoxEl()`      SearchGenreTiles.cs:123  - SILENT. A dead typeahead op
                                                                                   removes the section with no trace.
     Both of these sections also render NOTHING while pending in the Related case (`() => new BoxEl()`, :119) - only
     the Genres section reserves its shape.
```

### W12 - Facet switch in progress (slide)

```
  frame 0 (chip clicked, chip index 0 -> 2):
  +--------------------------------------------------------------------------------------------+
  |  All  Playlists 75  Songs 67  ...                                                          |
  |  ~~~~~~~~~~~~~~~~>  underline does NOT travel; the old tab's underline is removed and the   |
  |                     new tab's underline ENTERS from Sx 0 growing left-to-right over 260 ms  |
  +--------------------------------------------------------------------------------------------+
  |  [ outgoing facet body ]  ->  Dx -8, Opacity 1 -> 0   |  [ incoming ] <- Dx +8, Opacity 0->1 |
  |  MotionRecipes.PageSlideForward: Tween(250 ms, Easing.SmoothOut), channels Position|Opacity |
  |  (chip index decreasing -> PageSlideBack: the two Dx signs swap)                            |
  +--------------------------------------------------------------------------------------------+
  frame ~1: the incoming body is the new facet's SHIMMER (its own resource is Pending), then the
  shimmer cross-dissolves to content with the facet's own Skel reveal. Two nested motions by design.
```

### W13 - Omnibar suggestion popup, Results @ field width 480

```
  anchor: the AutoSuggestBox field, 32 DIP tall, Radii.Control 4, maxFillWidth 480 (ShellToolbar.cs:326-332)
  popup width = max(measured anchor width, 400) unless allowNarrow (icon-mode flyout)   :369-370
     measured anchor width of 0 (first frame, never measured) falls back to 720, NOT to 400   :369
  plate = PopupChrome.Static (acrylic + 1px border + Radii.Overlay + shadow + clip) - chapter 19

  +--------------------------------------------------------+  <- Pad (0,2,0,2)
  | [Q] sleep music                                        |  QueryRow: MinHeight 40, Pad (12,0,8,0),
  | [Q] sleep music for deep sleeping                      |  Margin (4,2,4,2), Corners Radii.Control
  | [Q] sleep meditation                                   |  glyph Icons.Search 16, Theme.IconFont,
  | [Q] slaap                                              |  TextSecondary, right margin 12
  |--------------------------------------------------------|  <- Divider 1 px, Margin (16,4,16,4),
  | [ 44 ] Sleep                       [>] [...] (Playlist)|     Tok.StrokeDividerDefault
  |        Spotify                                         |  RichRow: Height 58, Gap 12,
  | ( 44 ) Sleeping With The...    [>] [v] [...] (Song)    |  Pad (12,0,10,0), Margin (4,2,4,2)
  | [ 44 ] Sleep Token                 [>] [...] (Artist)  |  art 44, radius 22 circular / 5 square
  +--------------------------------------------------------+
     max 6 query rows, then max 10 item rows (ShellToolbar.cs:386, 395)
     rows scroll: ScrollEl MaxHeight 560, ContentSized, Margin (-1,0,-1,0)          :412-426
     trailing cluster, Gap 2 (:480-484):
       [>]   IconButton 28 circle, Icons.Play 14 TextSecondary      - unless kind is User or Genre
       [v]   TrackRow.Heart(saved, toggle)                          - Track only
       [...] MoreButton 28 circle, Icons.More 16, ClickRequestsContext - when acts+overlay present and canPlay
       pill  TypePill: Pad (9,2,9,2), Corners 10, FillSubtleSecondary, Eyebrow TextTertiary
     selection / hover fill: Tok.FillSubtleSecondary ; pressed: Tok.FillSubtleTertiary
     query-row match highlight (:606-614): matched run Weight 700 Tok.TextPrimary, the rest Weight 400
       Tok.TextSecondary, split at the FIRST OrdinalIgnoreCase IndexOf; no pill, no background.
       No match (IndexOf < 0) -> ONE plain 14px Tok.TextPrimary run, Grow 1, ellipsised (:594-598).
     rich-row subtitle falls back to the kind's own TypePill label when the server sends none (:514) - a row is
       never one-lined.
     the icon buttons ride WaveeMotion.ScaleEmphatic (hover 1.07 / press 0.92) + Interaction.Subtle (:534-554).
     RIGHT-CLICK is attached to EVERY rich row - Genre and User included (:520-522) - while the `...` button needs
       `canPlay` (:477-478). So a genre row has no visible `...` but still opens Menus.Card on right-click.
     SearchSuggestionItem carries `IsExplicit` (Wavee.Core/Library/Library.cs:15) and NOTHING on this row renders it -
       there is no E badge anywhere in the popup. A gap, not a spec (see §9).
```

### W14 - Omnibar popup, Pending / Empty / Failed

```
  Pending (including the whole 150 ms debounce window):
  +--------------------------------------------------------+
  |========================================================|  <- ProgressBar.Indeterminate(width)
  | [Q] <the PREVIOUS answer's rows stay put>              |     the field never blanks on a keystroke
  | ...                                                    |
  +--------------------------------------------------------+
  with no previous rows: a bare 40-DIP box under the bar - NO sentence (ShellToolbar.cs:406-407)

  Empty (a confirmed empty answer, the only state allowed to say it):
  +--------------------------------------------------------+
  |  No results found                                      |  Notice: MinHeight 40, Pad (24,0,24,0),
  +--------------------------------------------------------+  TextEl 14 Tok.TextPrimary, Grow 1

  Failed:
  +--------------------------------------------------------+
  |  Couldn't load suggestions                   [ Retry ] |  Notice: Pad (24,0,12,0), Gap 12,
  +--------------------------------------------------------+  Button.Subtle(common.retry)
     loc: search.suggestFailed, common.retry
```

### W15 - Browse directory, fully loaded @ pane 960

```
 +--------------------------------------------------------------------------------------------------+
 |                                                                                                  |  32 = FrameTop (Spacing.XXXL)
 |  Browse                                                                                          |  ShellMastheadBand overlay - paints NOTHING
 |  ____________________________________________________________________________________ 84 = Reserve   SurfaceDisplay 40/52/400
 |                                                                                                  |  16 (Spacing.L) -> BodyTop 100
 | <ScrollView ScrollKey "browse">  spacer 100  then the clipped directory, Pad (36,0,36,96)         |
 |                                                                                                  |
 |  Top                                                        <- BandLabel = Eyebrow 12/16/600 +30/1000, Tok.TextSecondary
 |  ( Music )  ( Podcasts )  ( Audiobooks )  ( Live Events )   <- BrowseTiles.Word, 36 DIP pills, Gap 8, Wrap
 |                                                                                                  |  Gap 16 between bands
 |  |tick| Charts                                        < >  o . o                                 |  FoldDeck's OWN header (no BandLabel);
 |                                                                                                  |  the two Charts-mapped CATEGORIES never
 |                                                                                                  |  render as tiles - Body swallows the
 |                                                                                                  |  Charts bucket and injects the deck
 |                                                                                                  |  instead (BrowseDirectory.cs:130)
 |  +--------------------------------------+ +--------------------------------------+               |  HomeFoldTile, 440-9999 wide, 176 tall,
 |  |  Featured Charts        [cover stack]| |  Weekly Song Charts    [cover stack]  |               |  gap 12, 1 row, max 2 columns
 |  +--------------------------------------+ +--------------------------------------+               |
 |                                                                                                  |
 |  For you                                                                                         |
 |  (# Discover) (# EQUAL) (# Fresh Finds) (# GLOW) (# Made For You) (# New Releases)               |  BrowseTiles.Name, 32 DIP, pip + Body
 |  (# RADAR) (# Spotify Singles) (# Tastemakers) (# Trending)                                      |  Gap 8, Wrap
 |                                                                                                  |
 |  Genres                                                                                          |
 |   [#] Afro           |  [#] Alternative     |  [#] Ambient                                        |  BrowseTiles.Link, 3 cols
 |   [#] Arab           |  [#] Blues           |  [#] Caribbean                                      |  colGap 12, rowGap 8
 |   ... 25 entries, alphabetised, 9 rows                                                            |
 |                                                                                                  |
 |  Mood & activity                                                                                 |
 |  +-------------+ +-------------+ +-------------+ +-------------+ +-------------+ +-------------+  |  BrowseTiles.Bar, 52 DIP,
 |  ||  At Home   | ||  Chill     | ||Cooking &   | ||  Fitness   | ||  Focus     | ||In the car  |  |  6 cols at body 888
 |  +-------------+ +-------------+ +-------------+ +-------------+ +-------------+ +-------------+  |  colGap 4, rowGap 4
 |  ... 14 entries, alphabetised                                                                     |
 |                                                                                                  |
 |  More                                                                                            |
 |  +-------------------+ +-------------------+ +-------------------+ +-------------------+          |  BrowseTiles.Peek, 88 DIP,
 |  || Category name    | || Category name    | || Category name    | || Category name    |          |  4 cols (capped), gap 12/12
 |  ||               .--| ||               .--| ||               .--| ||               .--|          |  hanging art 80, -8 deg - ONLY when
 |  +------------------|--+------------------|--+------------------|--+------------------|-+         |  m.Artwork != null (BrowseTiles.cs:166);
 |                                                                                                   |  a coverless More card is a plain plate
 |                                                                                                   |  cardW handed to Peek = width / cols,
 |                                                                                                   |  gaps NOT subtracted (BrowseDirectory.cs:312)
 |                                                                                   Pad bottom 96   |
 +--------------------------------------------------------------------------------------------------+
```

### W16 - Browse directory, loading @ pane 960

```
  Skeleton() = Body(BrowseDirectorySeeds.Categories, model:null, ChartsBandAt).Skeletonized(true)
  (Features/Browse/BrowseDirectory.cs:352-362; seeds at Features/Browse/BrowseDirectorySeeds.cs:28-95)

  The shape is IDENTICAL to W15 - the same bands, the same densities, the same wrap counts - because the seeds
  mirror BrowseTaxonomy's Map entry for entry:
      Top 4  |  For you 10  |  Genres 25  |  Mood & activity 14  |  More 3   (pinned by BrowseTaxonomyTests)
  Every seed title is a single space; `.Skeletonized(true)` turns each TextEl run into a shimmer bar sized from
  that run's own metrics. The Charts band shimmers off HomeBrowseCards.ChartDeckSeed (3 blank folds x 3 blank cards,
  Features/Home/HomeBrowseCards.cs:67-77).

  +--------------------------------------------------------------------------------------------------+
  |  [=]                                                       <- band label bar                      |
  |  (====)  (====)  (====)  (====)                            <- 4 word pills, still 36 DIP          |
  |                                                                                                   |
  |  [=]  Charts (real header text from Strings.Browse.Charts)                                         |
  |  +----------------------------------+ +----------------------------------+                        |
  |  |                                  | |                                  |   176 DIP folds        |
  |  +----------------------------------+ +----------------------------------+                        |
  |  ... every band below at its exact loaded count and wrap                                           |
  +--------------------------------------------------------------------------------------------------+

  A 3-per-band placeholder (the version this replaced) made For you and Genres wrap to one row while the loaded
  page wrapped to three or four -> the whole page height jumped when data landed. That is the regression.

  Two honest differences between the loading shape and the loaded one, neither of which moves a box:
   · every seed carries Artwork: null (BrowseDirectorySeeds.cs:31-94), so the More band's peek cards shimmer with NO
     hanging cover - the 88-DIP plate is identical, the art simply is not there yet;
   · the More band shows 3 seeds against a live count that is whatever browseAll leaves unmapped. The class comment
     says so explicitly (BrowseDirectorySeeds.cs:17-19): More is the one band whose height CAN still shift.
   · the loading page carries no navigation host (model: null -> BrowseTiles.ToModelNoop), so every seed cell still
     hovers and still takes focus, and does nothing (BrowseTiles.cs:270-281).
```

### W17 - Browse cell densities, at rest and on hover

```
  Word (Top)                          BrowseTiles.cs:56-57, Chip at :31-52
  +------------------+
  |      Music       |  Height 36 (WordChipH), Pad (12,0,12,0), Corners 999 (capsule), Shrink 0
  +------------------+  Fill Tok.FillControlDefault, Border 1 Tok.StrokeControlDefault
                        label Ui.BodyStrong 14/20/600, 1 line, NoWrap, ellipsis
  hover:  Fill -> Tok.FillControlSecondary ; BorderColor -> Tok.AccentDefault ; Scale 1.02
          over 167 ms (WaveeMotion.Fast) with Easing.FluentDecelerate
  press:  Scale 0.98        focus: FocusVisualMargin 2 all round

  Name (For you)                      BrowseTiles.cs:61-62
  +---------------------+
  | [#]  Discover       |  Height 32 (NameChipH), same plate, Gap 8, pip 8x8 Corners 2
  +---------------------+  label Ui.Body 14/20/400

  Link (Genres, and Search's genre results)      BrowseTiles.cs:69-88
   [#]  Alternative        Pad (4,4,4,4), Corners Radii.Control 4, NO rest fill, NO border
                           HoverFill Tok.FillControlSecondary
                           label Ui.Body.Secondary(), HoverColor Tok.AccentTextPrimary, Shrink 1

  Bar (Mood & activity)               BrowseTiles.cs:94-133
  +-------------------------------------------+
  ||                                    (wash)|  Height 52 (BarHeight), ZStack, ClipToBounds
  ||                                          |  Corners Radii.CardAll 8, Fill Tok.FillCardDefault
  ||   Chill                            (wash)|  Border 1 Tok.StrokeCardDefault, Shadow Elevation.Card
  +-------------------------------------------+  copy Pad 8, Justify End, AlignItems Start
   ^3 DIP tick, full height, Corners 2, raw seed   title WaveeType.CardTitle (BodyStrong 14/20/600)
                                                   Tok.TextPrimary, max 2 lines, wrap
   wash: radial, centre (1.0, 0.45), radius (0.65, 1.40), stop0 seed A=1, stop0.72 seed A=0,
         node Opacity 0.20 -> HoverOpacity 0.34 (~83 ms house cross-fade)
   hover: Fill -> Tok.FillCardSecondary at the SAME elevation - never a lift

  Peek (More)                         BrowseTiles.cs:139-179
  +--------------------------------------------+
  ||  Category                          (wash) |  Height 88 (MoreHeight), same plate as Bar
  ||  name                                     |  copy Pad 12, Justify Start, MaxWidth cardW*0.62
  ||                              .---------.  |  title WaveeType.ModuleHeader (Subtitle 20/28/600,
  +------------------------------|  80x80   |--+         display face) Tok.TextPrimary, wrap 2 lines
                                 |  -8 deg  |     art: AlignSelf End + JustifySelf End,
                                 `---------'      Margin (0,0,-24,-24) -> hangs 24 DIP past both edges
                                    clipped       Shadow Elevation.Card, Corners Radii.Control 4,
                                    by root       Surfaces.Artwork(decodePx 128)
   wash: centre (1.0, 0.28), radius (0.70, 1.30) - same opacity pair as Bar
   hover: art OffsetX -6, OffsetY +2, Rotation +3 (DELTAS on the rest pose) over MotionTok.ControlNormal
          (250 ms, Easing.FluentStandard); the edge cascades from the tile root even though the art is
          HitTestVisible = false
   NO ART: PeekArt is appended only `if (m.Artwork is not null)` (BrowseTiles.cs:166) - a coverless More category
          is the plate + wash + tick + title alone, and nothing hover-animates on it.
   the hang is DERIVED, not a literal: `hang = BrowseLayout.Peek * 0.3f` = 24 (BrowseTiles.cs:213).
   Key = "browse-peek:<uri>:<cardW>" (:170) - a column-count change remounts every peek cell (see §9 traps).
```

### W18 - Browse directory scrolled under the masthead

```
  at rest (OffsetY 0)                          scrolled (OffsetY > 0)
  +---------------------------------+          +---------------------------------+
  |  Browse            (band, 84)   |          |  Browse            (band, 84)   |  the band paints nothing:
  |  - - - - - - - - - - - - - - -  |          |~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~|  live Mica shows through
  |                                 |          |  [content cut at y = 84]        |  <- ClipBelow(84)
  |  Top                            |          |  ... Genres                     |  <- 24-DIP top EdgeFade,
  |  ( Music ) ( Podcasts ) ...     |          |   [#] Jazz    [#] K-pop         |     armed ONLY once the clip
  |                                 |          |                                 |     engages (underBand true)
  +---------------------------------+          +---------------------------------+
  BrowseDirectoryPage.cs:41-49; BrowseMastheadMetrics.cs:24-31 (ClipInset 84 == Reserve, ClipFadeBand 24
  == DetailVerticalLayout.StickyFadeBand). The reserve is a SPACER above the clipped node (:58), not padding
  inside it, so the cut engages exactly when content reaches the band rather than at rest.
```

### W19 - Browse category page, Shelves mode @ pane 960

```
 +--------------------------------------------------------------------------------------------------+
 |  Browse > Jazz                                                                     [ Show all ]  |  ShellMastheadBand: trail-as-title,
 |  ________________________________________________________________________________________  84   |  crumbs Tok.TextTertiary (hover Secondary)
 |                                                                                            16    |  + a "\u203a" separator at the same rung;
 | outer column: Pad (36,100,36,16), Gap 16                                                         |  tools button only in FlattenOne
 | <ScrollView ScrollKey "browse:spotify:page:...">  content Pad (0,0,0,96), Gap 16                  |
 |                                                                                                  |
 |  Jazz Essentials                                                        < >  o . o o             |  HomeModules.DrillHeader(title, open)
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                         |  PagedShelf: MediaCard.Shelf cards,
 |  | cover   | | cover   | | cover   | | cover   | | cover   | | cover   |                  220    |  minCardW 148, maxCardW 188, gap 12,
 |  | Title   | | Title   | | Title   | | Title   | | Title   | | Title   |                         |  edgeFade 24, Key "browse-shelf:<uri>"
 |  | Sub     | | Sub     | | Sub     | | Sub     | | Sub     | | Sub     |                         |
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                         |
 |                                                                                                  |
 |  (an UNTITLED shelf renders with NO header row at all - BrowsePage.cs:671, and an untitled CategoryGrid /        |
 |   Related block likewise renders as the bare LinkGrid with no DrillHeader - BrowsePage.cs:687)                   |
 |  (a Related / CategoryGrid header is a LABEL: CategoryBlock passes `DrillHeader(title, null)` - no chevron,      |
 |   no click. Only a SHELF header drills, into `browse-section:` - BrowsePage.cs:643-646, 691)                     |
 |                                                                                                  |
 |  Related                                                                                         |  CategoryBlock: DrillHeader + Gap 8
 |   [#] Blues          |  [#] Funk & Disco    |  [#] Soul                                          |  Responsive LinkGrid (fb 900), 3 cols
 |                                                                                                  |
 |  Explore all categories                                                                          |  TrackMeta (Caption 12/16) in
 |                                                                                                  |  Tok.AccentTextPrimary, Pad (0,8,0,8),
 +--------------------------------------------------------------------------------------------------+  AlignSelf Start, Role Button
```

### W20 - Browse category page, FlattenOne mode @ pane 960

```
 Chosen when exactly ONE Shelf section survives and its title is blank or equals the page title,
 OrdinalIgnoreCase (BrowsePageLayout.cs:43-44).

 +--------------------------------------------------------------------------------------------------+
 |  Browse > New Releases                                                             [ Show all ]  |  <- tools: visible iff
 |  ________________________________________________________________________________________  84   |     !exhausted && section.Total > Cards.Count
 |                                                                                            16   |     (BrowsePage.cs:170-184); disabled while
 |  pinned column (Gap 16): any CategoryGrid / Related blocks, then "Explore all categories"        |     loading; Button.Subtle, ControlSize.Small
 |                                                                                                  |
 |  +------------------------------------------------------------------------------------------+    |
 |  |  the ONE uniform grid - HomeModules.SectionGrid over a Virtual.Custom viewport that OWNS   |    |  Responsive.Of(..., fb 1100, grow 1)
 |  |  its own scroll (a virtual viewport measures 0 natural height inside a page ScrollView -   |    |  inside a Grow 1 / MinHeight 0 slot
 |  |  the cover-shear trap, BrowsePage.cs:460-473)                                              |    |
 |  |  [card] [card] [card] [card] [card] [card]                                                 |    |
 |  |  [card] [card] [card] [card] [card] [card]                                                 |    |
 |  |  ... pages IN PLACE as you approach the tail (HomeSectionAppendPreloader + nearTail)       |    |
 |  +------------------------------------------------------------------------------------------+    |
 +--------------------------------------------------------------------------------------------------+

 FlattenTwoConcat: both shelves' cards merged into one grid, sectionKey = the page uri, NO pager
   (the mode is only chosen when neither shelf has more - BrowsePageLayout.cs:49-53).
 FlattenTwoStacked: renders as Shelves today - a documented pragmatic deviation (BrowsePage.cs:334-341).
 The two-shelf rule is STRICTER than the one-shelf rule: FlattenTwo* needs both titles genuinely BLANK
   (`IsBlank`, BrowsePageLayout.cs:49, 68), not merely redundant-with-the-page the way FlattenOne accepts
   (`TitleIsRedundant`, :43, 60-66). A page "Jazz" with two shelves both titled "Jazz" stays Shelves.
 A page whose `Uri` is blank NEVER flattens (:29-30) - which is what pins the SkeletonPage to the Shelves shape.
```

### W21 - Browse category page, loading @ pane 960 (`ShimmerBody`)

```
 +--------------------------------------------------------------------------------------------------+
 |  Browse > Jazz                                              <- REAL from frame one: the title is  |
 |  ______________________________________________________       loadedNow.Title or the route's own  |
 |                                                               arg, published OUTSIDE the region   |
 |   [#=] | [#==] | [#=]          ShimmerRelated: CategoryBlock over 5 placeholder categories        |
 |                                                                                                   |
 |   [=========]                  ShimmerShelf header: WaveeType.ModuleHeader("Shelf title")         |
 |   +-------+ +-------+ +-------+ +-------+ +-------+ +-------+                                     |
 |   |148x148| |148x148| |148x148| |148x148| |148x148| |148x148|   Fill Tok.FillSubtleSecondary,     |
 |   |       | |       | |       | |       | |       | |       |   Corners Radii.CardAll            |
 |   |[Title]| |[Title]| |[Title]| |[Title]| |[Title]| |[Title]|   CardTitle("Title text")           |
 |   |[Sub]  | |[Sub]  | |[Sub]  | |[Sub]  | |[Sub]  | |[Sub]  |   TrackMeta("Subtitle")             |
 |   +-------+ +-------+ +-------+ +-------+ +-------+ +-------+   card box 148 x 220, Gap 4         |
 |   (a second identical shelf band)                               row Gap 12, ClipToBounds          |
 +--------------------------------------------------------------------------------------------------+
  NO PagedShelf, NO Virtual anywhere in this tree (BrowsePage.cs:242-247) - a virtualized carousel yields
  ZERO rows against an unmeasured viewport, which is exactly why the explicit `shimmerSource` overload is used.
  reveal FadeOnly: SkelReveal.None would floor the shimmer's ExitMs at 400 ms and leave a ghost under content
  that has no entrance of its own (BrowsePage.cs:212-217, 225).
```

### W22 - Browse category page, empty / error

```
  empty (page.IsEmpty)                              BrowsePage.cs:223, 613-621
  +--------------------------------------------------------------------------------------------+
  |                  This category isn't available right now                                   |  browse.unavailable
  |                                                                                            |  EmptyState.Build (PageHero)
  |  Explore all categories                                                                    |  <- still offered, Gap 16
  +--------------------------------------------------------------------------------------------+
  wrapped in FramedContent: a ScrollView with ScrollKey "browse:<pageUri>" and bottom pad 96 (:372-381)

  failed                                            BrowsePage.cs:224
  +--------------------------------------------------------------------------------------------+
  |                  Something went wrong / <common.errorSubtitle>      (no retry passed here)  |
  +--------------------------------------------------------------------------------------------+

  Browse DIRECTORY empty / failed                   BrowseDirectory.cs:95-96
  empty  -> EmptyState.Build(browse.unavailable)
  failed -> ErrorState.Build(err, onRetry: cats.Refresh)
  Charts band alone empty  -> EmptyState.Compact(home.chartsEmpty) (Subtitle rung), the rest of the
  Charts band alone FAILED -> ErrorState.Build(err, onRetry: charts.Refresh)  - a SECOND retry button, inside the
  band, retrying only the Charts deck (BrowseDirectory.cs:276). So Browse has TWO retry affordances, not one, and
  they refresh different resources; the rest of the directory is untouched either way (the two resources are
  independent - BrowseDirectory.cs:65-74, 271-277).

  Browse CATEGORY-PAGE section paging failure (a "Show all" / tail append that threw)   BrowsePage.cs:530-541
  -> NO visible error at all: what is already loaded stays on screen, the button stays armed, and the failure is
     logged as `browse.section.page.fail`. An endpoint that answers with an EMPTY page instead disarms the section
     permanently (Exhausted), which is what makes "Show all" disappear (:542-545).
```

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush token | material / elevation | source |
|---|---|---|---|---|---|---|---|
| **Search page** |||||||
| header column | - | Pad (16,12,16,8), Gap 8 | - | - | - | - | `SearchPage.cs:104-105` |
| query echo | 1 line, 52 tall | - | - | `WaveeType.SurfaceDisplay` 40/52/400, Display face, −12/1000 | `Tok.TextPrimary` | - | `SearchPage.cs:111-115`, `WaveeType.cs:146-151` |
| facet tab | auto | Pad (12,8,12,4), Gap 4 | - | `Ui.Body` 14/20 + `Ui.Caption` 12/16 | selected `Tok.TextPrimary`, else `Tok.TextSecondary`; count `Tok.TextTertiary` | - | `SearchPage.cs:282-310` |
| facet underline | H 3 | Stretch | `Radii.Full` | - | `Tok.AccentDefault` | - | `SearchPage.cs:311-320` |
| facet skeleton pill | H 16, W 40-116 | Pad (12,8,12,4) | `Radii.Control` 4 | - | `Tok.FillSubtleSecondary` | - | `SearchPage.cs:229-245` |
| result body | - | Pad (16,8,16,96) | - | - | - | - | `SearchPage.cs:67` |
| **Search hero** |||||||
| hero card | H 228, Grow 1 | - | `Radii.CardAll` 8 | - | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | `Interaction.Card` (press 0.985, StandardSpring) | `SearchHero.cs:115-121`, `Interaction.cs:146-153` |
| hero wash | H 228 | - | - | - | radial `WaveePalette.ChromeAccent(scheme)` A 0.55 -> 0 | - | `SearchHero.cs:124-138` |
| hero copy | - | Pad (16,16,20,16), Gap 8 | - | - | - | - | `SearchHero.cs:80-83` |
| hero eyebrow | - | - | - | `WaveeType.Eyebrow` 12/16/600 +30/1000 | `Tok.AccentTextPrimary` | - | `SearchHero.cs:86-89` |
| hero title | max 2 lines | - | - | `WaveeType.PageHero` = `Ui.Title` 28/36/600 | `Tok.TextPrimary` | - | `SearchHero.cs:90-94` |
| hero subtitle | - | - | - | `RichText.OfRow(..., 12, ...)` | `Tok.TextSecondary`, anchors `Tok.AccentTextPrimary` | - | `SearchHero.cs:97` |
| hero chip | - | Pad (8,4,8,4) | `Radii.Full` | `WaveeType.Eyebrow` | transparent fill, border 1 `Tok.StrokeControlDefault`, text `Tok.TextSecondary` | - | `SearchHero.cs:150-155` |
| hero cover | 300 x 260 | OffsetX 40, OffsetY −16, Rotation −2.5 | `Radii.Card` 8 | - | - | clipped by the card | `SearchHero.cs:106-113` |
| hero Play CTA | H 36 | Pad (18,6,18,7) | `Radii.Full` | 14 Bold | fill = graded accent, ink WCAG-picked | hover 1.04 / press 0.96 (`ScaleStandard`) | `WaveeCta.cs:65, 88-108` |
| hero Open-page CTA | H 36 | Pad (18,6,18,7) | `Radii.Full` | 14 Bold | `ButtonAppearance.Standard` ramp | same tier | `SearchHero.cs:67`, `WaveeCta.cs:88-108` |
| **Search rows** |||||||
| hit row (small) | H 64 | Pad (8,0,8,0), Gap 12 | `Radii.ControlAll` 4 | title `WaveeType.TrackTitle` 14/600; sub 12 | transparent -> `Tok.FillSubtleSecondary` -> `Tok.FillSubtleTertiary` | none (`plated: false`) | `MediaCard.cs:1039-1063` |
| hit row (large) | H 112 | Pad (16,12,16,12), Gap 16 | `Radii.ControlAll` | title `WaveeType.PageHero` | same | none | `MediaCard.cs:1043-1047, 990-992` |
| hit row art | 48 (large 84) | - | 4 (large 8; circular = half) | - | - | - | `MediaCard.cs:965-968` |
| hit row play FAB (hover) | 30 (large 44) | - | circle | - | on-media scrim | - | `MediaCard.cs:969, 974-982` |
| hit row text gap | - | Gap 2 (`Spacing.XXS`); large Gap 8 | - | - | - | - | `MediaCard.cs:1007-1008` |
| hit row w/ detail | H auto, MinH 64 | Pad `Edges4.All(8)` | `Radii.ControlAll` | - | - | - | `MediaCard.cs:1042-1046` |
| track-artwork policy | removes the art block on TRACK hits only | - | - | - | - | - | `SearchPage.cs:660, 784, 922, 980, 1041`; `Design/AppearancePrefs.cs:14-18` |
| row eyebrow | - | - | - | `WaveeType.Eyebrow` | `Tok.AccentTextPrimary` (lyrics) / `WaveeColors.PremiumText` (access) | - | `SearchPage.cs:773-777` |
| save button | 32 x 32 | - | circle | icon 16 | `Tok.AccentDefault` when saved, else `Tok.TextSecondary`; hover `Tok.FillSubtleSecondary` | - | `SearchPage.cs:890-896` |
| artists-facet row | H 60 | Pad (8,0,8,0), Gap 12, text Gap 1 | `Radii.ControlAll` | 14/20/600 + 12/16 | transparent -> subtle ladder | - | `SearchPage.cs:601-622` |
| type pill | - | Pad (8,4,8,4) | `Radii.Full` | `WaveeType.Eyebrow` | `Tok.FillSubtleSecondary`, text `Tok.TextTertiary` | - | `SearchPage.cs:624-629` |
| **Search sections** |||||||
| tick header tick | 3 x 14 | Gap 8 | 2 (`Spacing.XXS`) | - | `Tok.AccentDefault` | - | `SearchPage.cs:1129-1133`, `BrowseTiles.cs:313-316` |
| tick header label | - | - | - | `WaveeType.RailHeader` = `Ui.Subtitle` 20/28/600 | `Tok.TextPrimary` | - | `SearchPage.cs:1136-1139` |
| tick header chevron | 12 | Gap 4 | - | - | `Tok.TextTertiary` | - | `SearchPage.cs:1145-1148` |
| best-matches grid | cell min 280, rowH 64 (128 w/ audiobook) | gap 12, edgeFade 16 | - | - | - | - | `SearchPage.cs:949-959, 1006-1022` |
| songs grid | col min 280, rowH 64 | gap 12 | - | - | - | - | `SearchPage.cs:911-912, 937` |
| playlist rail card | 148-188 wide, H = w + 72 | gap 12, edgeFade 24 | `Radii.Card` | `WaveeType.CardTitle` + `TrackMeta` | - | - | `SearchPage.cs:1080-1089`, `HomeModules.cs:504-506`, `MediaCard.cs:212` |
| facet grid cell | min 180, cellW = (w − (n−1)·16)/n, H = cellW + 50 | colGap 16, rowGap 20 | `Radii.Card` | - | - | - | `SearchFacetGrid.cs:36-39, 147` |
| related query link | - | wrap Gap 12 | - | `WaveeType.ModuleHeader` 20/28 at **Weight 400**, lowercased | `Tok.TextSecondary` -> hover `Tok.AccentTextPrimary` | - | `SearchGenreTiles.cs:152-168` |
| **Omnibar popup** |||||||
| query row | MinH 40 | Pad (12,0,8,0), Margin (4,2,4,2) | `Radii.ControlAll` | 14 | selected/hover `Tok.FillSubtleSecondary`, press `Tok.FillSubtleTertiary` | popup plate (chapter 19) | `ShellToolbar.cs:449-462` |
| query match run | - | - | - | 14 **Weight 700** | `Tok.TextPrimary` (unmatched 400 / `Tok.TextSecondary`) | - | `ShellToolbar.cs:606-614` |
| rich row | H 58 | Pad (12,0,10,0), Margin (4,2,4,2), Gap 12 | `Radii.ControlAll` | 14/600 + 12 | same ladder | - | `ShellToolbar.cs:486-499` |
| rich row art | 44 x 44 | - | 22 circular / 5 square | - | - | - | `ShellToolbar.cs:502-507` |
| popup icon button | 28 x 28 | - | circle 14 | icon 14 (More 16) | `Tok.TextSecondary`, `Interaction.Subtle` | - | `ShellToolbar.cs:534-554` |
| popup type pill | - | Pad (9,2,9,2) | 10 | `WaveeType.Eyebrow` | `Tok.FillSubtleSecondary` / `Tok.TextTertiary` | - | `ShellToolbar.cs:563-570` |
| popup divider | H 1 | Margin (16,4,16,4) | - | - | `Tok.StrokeDividerDefault` | - | `ShellToolbar.cs:556-561` |
| popup body | W = max(anchor \|\| 720, 400), MaxH 560 | Pad (0,2,0,2), content Margin (−1,0,−1,0) | - | - | - | `PopupChrome.Static` acrylic | `ShellToolbar.cs:369-370, 412-435` |
| popup notice | MinH 40 | Pad (24,0,24|12,0), Gap 12 | - | 14 | `Tok.TextPrimary` | - | `ShellToolbar.cs:439-447` |
| **Browse frame** |||||||
| masthead reserve | 84 = 32 + 52 | - | - | `WaveeType.SurfaceDisplay` | crumbs `Tok.TextTertiary` (hover `Tok.TextSecondary`) | paints nothing (Mica) | `BrowseMastheadMetrics.cs:11-12`, `ShellMastheadBand.cs:90-101` |
| family body pad | (36, 100, 36, b) | - | - | - | - | - | `BrowseMastheadMetrics.cs:17-20` |
| under-band pad | (36, 0, 36, b) + an 84 spacer | - | - | - | - | - | `BrowseMastheadMetrics.cs:36-37`, `BrowseDirectoryPage.cs:58` |
| band gap | 16 (`Spacing.L`) | - | - | - | - | - | `BrowseDirectory.cs:138` |
| band label | - | band Gap 8 | - | `WaveeType.Eyebrow` 12/16/600 +30/1000, sentence case | `Tok.TextSecondary` | - | `BrowseDirectory.cs:242, 327-328` |
| **Browse cells** |||||||
| Word chip | H 36 | Pad (12,0,12,0), Gap 8, row Gap 8 | 999 | `Ui.BodyStrong` 14/20/600 | `Tok.FillControlDefault` -> `Tok.FillControlSecondary`; border 1 `Tok.StrokeControlDefault` -> `Tok.AccentDefault` | - | `BrowseTiles.cs:31-57, 304, 311` |
| Name chip | H 32 | same | 999 | `Ui.Body` 14/20 | same | - | `BrowseTiles.cs:61-62, 308` |
| identity pip | 8 x 8 | - | 2 | - | `WaveePalette.ToColor(c.Color)` or `Tok.AccentDefault` | - | `BrowseTiles.cs:236-240, 255, 326` |
| Link cell | auto | Pad (4,4,4,4), Gap 8 | `Radii.Control` 4 | `Ui.Body`.Secondary() | hover fill `Tok.FillControlSecondary`, hover ink `Tok.AccentTextPrimary` | - | `BrowseTiles.cs:69-88` |
| Link grid | 3 / 2 / 1 cols | colGap 12, rowGap 8 | - | - | - | - | `BrowseTiles.cs:257-263, 335` |
| Bar cell | H 52 | copy Pad 8, grid gap 4/4 | `Radii.CardAll` 8 | `WaveeType.CardTitle`, 2 lines | `Tok.FillCardDefault` -> `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault`, ink `Tok.TextPrimary` | `Elevation.Card` (light 0/2/4 #0000001A; dark 0/2/8 #00000033) | `BrowseTiles.cs:94-133, 318`, `Elevation.cs:18-21` |
| Peek cell | H 88 | copy Pad 12, grid gap 12/12 | `Radii.CardAll` | `WaveeType.ModuleHeader`, 2 lines, MaxWidth 0.62·cardW | same plate | `Elevation.Card` | `BrowseTiles.cs:139-179, 320, 324` |
| corner wash | full cell | - | - | - | raw seed, radial, A 1 -> 0 at 0.72; node Opacity 0.20 / hover 0.34 | - | `BrowseTiles.cs:189-202` |
| left tick | 3 x full height | - | 2 | - | raw seed | - | `BrowseTiles.cs:244-250` |
| peek art | 80 x 80 | Margin (0,0,−24,−24), Rotation −8 | `Radii.Control` 4 | - | - | `Elevation.Card` | `BrowseTiles.cs:211-232, 322` |
| Mood grid | cols = max(1, ⌊w/132⌋) | gap 4/4 | - | - | - | - | `BrowseDirectory.cs:301-307`, `BrowseTiles.cs:338-340` |
| More grid | cols = clamp(⌊w/168⌋, 1, 4) | gap 12/12 | - | - | - | - | `BrowseDirectory.cs:309-316`, `BrowseTiles.cs:344-347` |
| **Browse page** |||||||
| shelf card | 148-188 wide, H = w + 72 | gap 12, edgeFade 24 | `Radii.Card` | `WaveeType.CardTitle` + `TrackMeta` | - | - | `BrowsePage.cs:668-673` |
| Charts fold | 440-9999 wide, H 176 | gap 12, 1 row, max 2 cols | - | `HomeModules.ModuleHeader` | - | `HomeFoldTile` plate (chapter 11) | `HomeModules.cs:366-374`, `HomeModules.cs:540-552` |
| Explore all | - | Pad (0,8,0,8) | - | `WaveeType.TrackMeta` (Caption 12/16) | `Tok.AccentTextPrimary` | - | `BrowsePage.cs:698-705` |
| shimmer card | 148 x 220 | Gap 4 | `Radii.CardAll` | - | `Tok.FillSubtleSecondary` | - | `BrowsePage.cs:303-318` |

Scale constants referenced above (chapter 00 owns them): `Spacing.XXS 2 / XS 4 / S 8 / M 12 / L 16 / XL 20 / XXL 24 / XXXL 32`,
`Spacing.PageWide 36`; `Radii.Control 4 / Card 8 / Overlay 8 / Full 999`; `PlayerDock.Reserve 72`
(`FluentGpu.Engine/Dsl/Spacing.cs:12-20, 24`, `Radii.cs:10-15`, `Design/WaveeTokens.cs:79-84`).

---

## 4. Colour & material

### 4.1 The search hero wash (the one cover-derived colour on the Search surface)

```
h.Image.Url
  -> CoverColorPlane.CanGrade(url)                       SearchHero.cs:36        gate: only a gradeable url is watched
  -> CoverColorPlane.Current.Watch(url).Value            SearchHero.cs:37        subscribes; the wash appears when grading lands
  -> CoverColorPlane.Current.TryGetScheme(url, isLight)  SearchHero.cs:39-40     isLight = (Tok.Theme == ThemeKind.Light)
  -> WaveePalette.ChromeAccent(scheme)                   SearchHero.cs:41        = Vivid(Lift(Accent(scheme))) unless the lifted
                                                                                  colour's HSV saturation <= NeutralS, in which
                                                                                  case it falls back to Tok.AccentDefault
                                                                                  (Design/WaveePalette.cs:126-131)
```

Applied twice, from the same `accent`:

* **The plate wash** (`SearchHero.cs:124-138`): a `GradientShape.Radial` on a full-bleed, `HitTestVisible = false` child of the
  card's ZStack, `RadialCenter (0.88, 0.40)`, `RadialRadius (1.2, 0.8)`, stops `0.00 -> accent A 0.55` and `0.58 -> accent A 0.00`.
  The anchor sits under the cropped cover's left edge, so the colour reads as spilling off the art rather than as a chrome fill.
  When `scheme` is null the child is a bare `BoxEl` - **no fallback wash**; the card is the plain token plate.
* **The Play CTA fill** (`SearchHero.cs:65`): `WaveeCta.Play(accent, play)` - the pill takes the graded accent as its background and
  a WCAG-picked ink. With no scheme the accent is `Tok.AccentDefault` and the pill is the stock accent button.

Light/dark: `TryGetScheme` is asked for the theme's own variant, so the wash re-grades on a theme switch. Every other colour on the
Search surface is a theme token; there is no on-media ink anywhere on this page (the hero's title sits on the card plate, not on art).

### 4.2 The Browse category colour

`BrowseCategory.Color` is an opaque ARGB `uint` from the wire (`Wavee.Core/Library/Browse.cs:24`). One resolution for every cell
(`BrowseTiles.cs:255`):

```
m.Color is uint c  ->  WaveePalette.ToColor(c)            Design/WaveePalette.cs:16-20   (a>>24, r>>16, g>>8, b)
null / absent      ->  Tok.AccentDefault
```

`0` is **not** a request for a transparent tile: `ToColor(0)` is pure black, so the semantic accent is the fallback - the same rule
`HomeFoldTile`'s wash and `ChromeAccent` use. The colour is then applied **raw, never muted**, in three places:

| surface | treatment | source |
|---|---|---|
| identity pip (Name, Link) | solid 8x8 fill at full alpha | `BrowseTiles.cs:236-240` |
| left tick (Bar, Peek) | solid 3-DIP full-height hairline at full alpha | `BrowseTiles.cs:244-250` |
| corner wash (Bar, Peek) | radial anchored at the cell's right edge, `A 1 -> 0` at stop 0.72, carried on the NODE's `Opacity 0.20` / `HoverOpacity 0.34` | `BrowseTiles.cs:189-202` |

Alpha rides `Opacity`/`HoverOpacity` rather than a `WhileHover` `MotionTarget` because the node is a plain decoration, not a
gesture pose - it inherits the house ~83 ms hover cross-fade (`InteractionAnim.ControlFasterMs`, the same duration
`MotionTok.ControlFaster` names) at no extra cost (`BrowseTiles.cs:182-188`).

Bar / Peek anchors differ so the two densities do not read as the same stamp at two heights:

| | centreY | radiusX | radiusY | height |
|---|---|---|---|---|
| Bar | 0.45 | 0.65 | 1.40 | 52 |
| Peek | 0.28 | 0.70 | 1.30 | 88 |

**Ink on Bar / Peek is `Tok.TextPrimary`, not `WaveeOnMedia` white** (`BrowseTiles.cs:122-127, 150-155`). These plates are theme
surfaces now; a hardcoded white would break the light theme. That is the single most load-bearing difference from the earlier
"wash IS the plate" design (`browse-cards-v2-mica.html` variant 1) that this replaced.

### 4.3 Materials

* The shell ground is live Mica. **The Browse masthead paints nothing at all** - a fill would read as a black slab on Mica
  (`BrowseMastheadMetrics.cs:22-25`). The directory therefore clips itself at the band's lower edge and feathers that cut with a
  24-DIP `EdgeFade(EdgeMask.Top)`, armed only on the clip's engage edge (`BrowseDirectoryPage.cs:41-49`).
* Card plates on both surfaces are the token pair `Tok.FillCardDefault` / `Tok.FillCardSecondary` over a 1px
  `Tok.StrokeCardDefault`, at `Elevation.Card` - **the hover swaps the fill at the same elevation, never a lift**
  (`BrowseTiles.cs:105-110`, `SearchHero.cs:118-119`, `Interaction.cs:146-153`).
* Rows on both surfaces are unplated: transparent at rest, `Tok.FillSubtleSecondary` on hover, `Tok.FillSubtleTertiary` on press
  (`SearchPage.cs:606-609`, `MediaCard.cs:1053-1056` via `plated: false`). Twenty plated rows put twenty hairlines down a page and
  leave no quiet ground for hover to move against - the comment at `SearchPage.cs:602-605` is the rationale.
* The suggestion popup rides `PopupChrome.Static` (acrylic plate + border + `Radii.Overlay` + shadow + clip); the popup body adds
  only 2 DIP of vertical breathing room (`ShellToolbar.cs:429-435`).
* Skeleton bars are `Tok.FillSubtleSecondary` (primary) and `Tok.FillSubtleTertiary` (the quieter second line), with the engine's
  own 1 s breathe at `PulseMin 0.5` (`SkeletonRegion.cs:34-38`).

---

## 5. Motion

Every entry below samples engine frame time (`FrameTime.NowQpc` / `FrameClock.PresentQpc`) through the engine's motion system.
**One exception, and it is not visual:** `BrowseDirectoryStore` / `BrowsePageStore` stamp their TTL with
`Environment.TickCount64` (`BrowseDirectoryStore.cs:43, 48, 53`) - a cache clock, never an animation clock. Nothing on either
surface animates off `TickCount64`.

| trigger | target | property | from -> to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| facet selected | underline | ScaleX (origin left) | 0 -> 1 | 260 ms | `Easing.SmoothOut` | none | engine `MotionTokenDef.Eased` policy | `SearchPage.cs:36-37, 311-320` |
| facet chip changed (index up) | keyed facet body | Position X + Opacity | enter `Dx +8`, `Opacity 0`; exit `Dx −8`, `Opacity 0` | 250 ms (`Expressive.Fast`) | `Easing.SmoothOut` | none | tween, engine-governed | `SearchPage.cs:73-75`, `MotionRecipes.cs:193-197` |
| facet chip changed (index down) | same | same | signs swapped | 250 ms | `Easing.SmoothOut` | none | same | `MotionRecipes.cs:199-203` |
| first render of the page | facet body | - | **no slide** (`_slideArmed` false) | - | - | - | - | `SearchPage.cs:59-62` |
| search results Pending -> Ready (any facet but Tracks) | result rows | Opacity + TranslateY + Blur | per row, blur-rise | engine default | engine `SkelReveal.StaggerRows` | 40 ms per row | snaps | `SearchPage.cs:85`, `SkeletonRegion.cs:16-17` |
| search results Pending -> Ready (Tracks facet) | body | - | `SkelReveal.None` - the content owns its entrance; the shimmer's ExitMs is floored at 400 ms and cross-dissolves | 400 ms floor | - | - | snaps | `SearchPage.cs:85`, `SkeletonRegion.cs:20-24` |
| shimmer -> content swap | shimmer orphan | Opacity | 1 -> 0 | 250 ms (`SkeletonStyle.ExitMs` default) | - | - | snaps | `SkeletonRegion.cs:28-36` |
| shimmer while pending | shimmer bars | Opacity | 1 -> 0.5 -> 1 | 1000 ms loop | - | - | ignored | `SkeletonRegion.cs:34-35` |
| hover on a search row | row fill | Fill | transparent -> `Tok.FillSubtleSecondary` | ~83 ms | house control-faster | none | fill keeps | `MediaCard.cs:1053-1055` |
| hover on the save button | button | Scale | 1 -> 1.07 | ~83 ms | house | none | reduced-motion-safe tier | `SearchPage.cs:893`, `WaveeMotion.cs:48` |
| hover on the hero card | card | Fill + Scale | `FillCardDefault` -> `FillCardSecondary`; press 1 -> 0.985 | spring (`MotionTokenId.StandardSpring`) | - | none | fill keeps | `SearchHero.cs:147`, `Interaction.cs:146-153` |
| hover on a browse chip (Word/Name) | chip | Fill + BorderColor + Scale | `FillControlDefault` -> `FillControlSecondary`; `StrokeControlDefault` -> `AccentDefault`; 1 -> 1.02 | 167 ms (`WaveeMotion.Fast`) | `Easing.FluentDecelerate` | none | `ScaleSubtle` tier is reduced-motion aware | `BrowseTiles.cs:45-49`, `WaveeMotion.cs:39, 57` |
| press on a browse chip | chip | Scale | 1 -> 0.98 | 167 ms | `Easing.FluentDecelerate` | none | same | `BrowseTiles.cs:49` |
| hover on a Bar / Peek card | corner wash | Opacity | 0.20 -> 0.34 | ~83 ms | house control-faster | none | fill keeps | `BrowseTiles.cs:192` |
| hover on a Bar / Peek card | card fill | Fill | `FillCardDefault` -> `FillCardSecondary` | ~83 ms | house | none | fill keeps | `BrowseTiles.cs:109, 175` |
| hover on a Peek card | hanging art | OffsetX, OffsetY, Rotation (deltas) | (0,0,−8°) -> (−6, +2, −5°) | 250 ms | `Easing.FluentStandard` (`MotionTok.ControlNormal`) | none | `KeepFade` policy | `BrowseTiles.cs:218-225`, `MotionTok.cs:166` |
| Browse directory band mounts | each band | Opacity (+ 8 DIP rise, 2px blur on enter) | 0 -> 1 | 400 ms (`Expressive.Slow`) | `Easing.SmoothOut` | `clamp(index,0,8) * 40 ms`; index starts at 1, so Top 40 ms, Charts 80, For you 120, Genres 160, Mood 200, More 240 | delay 0, channel Opacity only | `BrowseDirectory.cs:130-131, 243`, `WaveeMotion.cs:112-126` |
| Browse directory Pending -> Ready | region | - | `SkelReveal.None` - the bands own the entrance | - | - | - | - | `BrowseDirectory.cs:93` |
| Browse category Pending -> Ready | body | Opacity | 0 -> 1 (`SkelReveal.FadeOnly`) | 250 ms | - | none | snaps | `BrowsePage.cs:225` |
| Browse directory scrolled past 84 | directory node | ClipRect top + `EdgeFade` | clip off -> clip at viewport + 84, 24-DIP top feather | scroll-linked (no duration) | linear in scroll | - | unchanged | `BrowseDirectoryPage.cs:45-49` |
| masthead family enter / leave | band | Opacity | 0 <-> 1 | 120 ms (`PageNavMotion.FadeThroughExitMs`) | enter `Easing.SmoothOut`, leave `Easing.FluentAccelerate` | none | `ReducedMotionPolicy.KeepFade` | `ShellMastheadBand.cs:23-26, 69-71` |
| a suggestion request begins | popup | progress bar | indeterminate sweep | engine | engine | none | engine | `ShellToolbar.cs:434` |
| a shelf page changes (pips/chevrons) | `PagedShelf` strip | scroll offset | snapped to a page boundary (`ShelfSnap.Page`) | engine physics | engine | - | engine | `SearchPage.cs:1017`, `PagedShelf.cs:33-39` |
| hover / press the hero Play or Open-page pill | pill | Scale | 1 -> 1.04 ; press -> 0.96 | engine interaction fade | house | none | `ScaleStandard` returns 1f under reduced motion | `WaveeCta.cs:104-106`, `WaveeMotion.cs:42` |
| hover / press a popup icon button (Play / More) | button | Scale | 1 -> 1.07 ; press -> 0.92 | ~83 ms | house | none | `ScaleEmphatic` tier | `ShellToolbar.cs:538, 549`, `WaveeMotion.cs:48` |
| hover a hit row's cover | play FAB | Opacity | 0 -> 1 | engine control-fast | house | none | fill keeps | `MediaCard.cs:974-982` |
| pointer rests where a row MOUNTS (back-nav) | row / FAB | - | **nothing** - `HoverMotionGate` needs a moved sample before it arms | - | - | - | - | `MediaCard.cs:964, 1037-1038`, `WaveeMotion.cs` (HoverMotionGate doc) |
| masthead "Show all" while a page is in flight | button | - | `isEnabled: false` (the stock disabled ramp; no spinner) | - | - | - | - | `ShellMastheadBand.cs:59-62` |

No tickers, countdowns, equalizers, morphs or shared-element transitions exist on either surface. `MediaCard.Row` passes no
`morphKey` from any search call site (`SearchPage.cs:775-784`), and no Browse cell passes one either.

---

## 6. Interaction

### 6.1 Search page

**Facet tabs.** `Role = AutomationRole.Tab`, `Cursor = CursorId.Hand`, `OnClick = () => _chip.Value = i`
(`SearchPage.cs:302`). Label colour is the only rest/hover difference (`HoverColor = Tok.TextSecondary` - so hovering a
selected tab dims it slightly; hovering an unselected one is a no-op visually). No tooltip, no focus visual beyond the engine's
default. `UseEffect` clamps the chip index to 0 whenever the facet count shrinks (`SearchPage.cs:49`).

**Hit rows** (`SearchAllList.HitRow`, `SearchPage.cs:759-785`):

| gesture | result |
|---|---|
| click (Track) | `model.PlayTrack(h.Uri)` - open == play for a track |
| click (Artist / Album / Playlist) | `go("artist:" \| "album:" \| "pl:" + uri, name)` |
| click (Podcast / Audiobook) | `go("show:" + uri, name)` |
| click (Episode) | `model.PlayContext(uri)` - plays, does not navigate |
| click (Genre) | `SearchRoutes.OpenGenre` - `spotify:genre:<id>` becomes `browse:spotify:page:<id>`; anything else re-commits the genre NAME as a search (`SearchRoutes.cs:12-27`) |
| click (User / Author) | nothing (`_ => static () => { }`, `SearchPage.cs:885`) |
| click on the cover's play FAB | `play` (the same action), `BlocksDragArm` so it is never a drag handle |
| click on a subtitle anchor | `model.Go(key, null)` - artist/album names inside the subtitle are individually clickable (`SearchPage.cs:780`) |
| right-click / `...` | `SearchAllList.HitMenu` - a Track hit resolved through `TrackOf` gets `Menus.TrackAttach` (the full track menu: Go to album, Go to artist(s), credits); every other hit gets `Menus.CardAttach(uri, name, image, subtitle, circular)`. The card menu's grammar is **kind-dependent** (`Actions/Menus.cs:544-602`): Album / Playlist get the container strip Play · Play next · Play after · Save, then Add to playlist / Open / Pin / Go to artist (albums) / Share as rows; an **Artist** drops the queue verbs and Add-to-playlist (no resolvable track set) and adds Follow + Go to artist radio; a **Podcast / Audiobook** (an `EntityKind.Show` uri) takes the separate SHOW arm - Play · Open · Pin · Share only (`Menus.cs:552`). Episode, Genre, User, Author and every unrecognised scheme get **no menu at all**, which also means no `...` button and no `.WithMenu` |
| drag | `SearchAllList.EntityDrag` - a Track hit resolved via `TrackOf` yields `WaveeResourceDragPayload.ForTrack(t)` (depositable on a playlist); otherwise `ForEntity(kind, uri, name, image)`. Artist / show / episode entities carry no tracks and are refused on drop with a cue, never a guess (`SearchPage.cs:700-704, 853-855`) |
| trailing button | `FollowButton` when `h.Followable` (the shared component - 36 DIP capsule, `Icons.Heart`/`HeartFill`, accent border when following, `Strings.Artist.Follow`/`Following`); else a 32-DIP `SaveButton` for tracks; else nothing |

**Hero card.** `Role = AutomationRole.Button`, whole-card `OnClick = open`, `Draggable = drag`, `.WithMenu(menu)` - the same menu
and drag resolution as the row it visually replaces (`SearchHero.cs:53-54, 120-121, 147`). The `...` button is
`MediaCard.MoreInline(true)` with `ClickRequestsContext = true` and `BlocksDragArm = true`, wrapped in
`ToolTip.Wrap(..., Loc.Get(Strings.Common.More))` - the only tooltip on the whole Search surface (`SearchHero.cs:72`).

**Artists-facet row** (`SearchPage.cs:601-622`): click opens `artist:<uri>`. No menu, no drag, no trailing action - this row is
deliberately thinner than a `MediaCard.Row` and that is a **parity gap worth fixing in 0.3, not preserving** (see §9).

**Section headers.** `SearchChrome.TickHeader(title, open)` becomes `Role = AutomationRole.Hyperlink`, `Focusable = true`,
`Cursor = Hand` with a 12-DIP `Icons.ChevronRight` when an `open` action is supplied (`SearchPage.cs:1143-1149`). Only the
All-tab **Playlists** header has one, and it calls `SelectFacet(SearchFacet.Playlists)` - it switches the chip, it does not
navigate (`SearchPage.cs:459`). "Best matches", "Genres" and "Related searches" are labels.

**Related-query links.** `Role = Button`, `Cursor = Hand`, `OnClick = () => go("search", q)` - committing a new query, which
mounts a new keep-alive `SearchPage` slot. The typed query itself is filtered out of the list
(`SearchGenreTiles.cs:132, 152-168`).

**Keyboard.** The page itself declares no key handling, and — worth stating because it is a gap, not a spec — **no node on the
Search page sets `Focusable = true`**: the facet tabs, the hero card and every hit row carry a `Role` and an `OnClick` but no
explicit focusability, so keyboard traversal of the results body is whatever the engine's default gives a `Role`-bearing node.
Browse is the opposite: every directory cell and "Explore all categories" set `Focusable = true` with a 2-DIP
`FocusVisualMargin` (`BrowseTiles.cs:37-38, 72-73, 100-101, 171-172`, `BrowsePage.cs:700-702`). Everything else keyboard is
the omnibar's (below).

**Settings that change what is drawn on this page.** Exactly one: `WaveeSettings.HideTrackArtwork`, read reactively through
`AppearancePrefs.TrackArtworkHidden(svc.Settings)` at three call sites — `SearchAllList.Render` (`SearchPage.cs:660`),
`SearchSongsGrid.Render` (`:922`) and `SearchHitsGrid.Render` (`:980`) — which drops the 48-DIP art from **track** hit rows
only, through `MediaCard.Row`'s `showArtwork` (`:784, 930-931, 1040-1041`). The flag is also baked
into `SearchHitsGrid`'s shelf `Key` (`:1022`), so toggling it remounts the Best-matches shelf. No theme, density or zoom
branch exists anywhere in the Search or Browse files.

### 6.2 Omnibar and suggestion popup (chapter 18 owns the field; this is the search behaviour on it)

| gesture | result | source |
|---|---|---|
| typing | `query.Begin(trimmed)` on the **undebounced** edge -> `Pending` immediately; a superseding generation cancels the in-flight request | `ShellToolbar.cs:205-209` |
| 150 ms of quiet | the settled GENERATION issues `SuggestRichAsync`; a retype to the same text inside the window is still a new generation and is answered | `ShellToolbar.cs:215-222` |
| inline ghost | `SearchSuggestions.GhostFor(typed, queries)` - the first query that starts with the typed text (ordinal-ignore-case) and is longer; suppressed whenever the keyboard cursor is on a row | `ShellToolbar.cs:246-251`, `Wavee.Core/Library/Library.cs:26-36` |
| Down / Up | `MoveSelection(+1 / −1)` over `min(6, queries) + min(10, items)`; wraps through **−1** (no selection) at both ends | `ShellToolbar.cs:303-312` |
| Enter with a cursor | `InvokeSelection(i)` - a query row rewrites the field and commits `go("search", chosen)`; an item row plays (Track, Episode) or navigates (`artist:` / `album:` / `pl:` / `show:`) or opens a genre through `SearchRoutes.OpenGenre` with a `NavOrigin(typedQuery, "search", typedQuery)` | `ShellToolbar.cs:265-301` |
| Enter with no cursor | `Submit(text)` -> `go("search", trimmed)`, or `go("search", null)` when blank - which `NavRouteNormalizer` rewrites to Browse | `ShellToolbar.cs:253-257`, `NavRouteNormalizer.cs:20-21` |
| click a row | the same `InvokeSelection`, then `context.Close()` | `ShellToolbar.cs:317` |
| click the row's `[>]` | `PlayItem` - `PlayTrackAsync` for a track, `PlayAsync(uri, 0)` otherwise; no-op for User / Genre; closes the popup | `ShellToolbar.cs:474, 525-532` |
| click the row's heart | `lib.ToggleSaved(item.Uri, item.Title)` - tracks only | `ShellToolbar.cs:475-476` |
| click the row's `...` / right-click the row | `Menus.Card(acts, item.Uri, item.Title)` via `row.WithContextMenu` | `ShellToolbar.cs:477-478, 520-522` |
| click Retry (Failed) | `query.Retry()` - re-arms the failed query as a NEW pending generation; the settled-generation effect issues it | `ShellToolbar.cs:320, 404-405`, `OmnibarSuggestQuery.cs:90-98` |
| unmount / remount the field | the request dies with the component; the store keeps `Pending`, so the next mount re-issues the same generation | `ShellToolbar.cs:224-225`, `OmnibarSuggestQuery.cs:112-124` |

Row automation: `Role = AutomationRole.MenuItem` on both query and rich rows (`ShellToolbar.cs:456, 495`); the icon buttons are
`AutomationRole.Button`. Accessibility names come from the row's own text; there are no explicit `AutomationName` overrides on
this surface - **a gap, not a spec** (see §9).

### 6.3 Browse

| gesture | result | source |
|---|---|---|
| click any directory cell | `model.OnOpenCategory(uri, title)` -> `go("browse:" + uri, title)`; a `BrowseClientFeature` instead calls `OnOpenFeature(uri)` -> `BrowseRoutes.FeatureRoute` maps `spotify:concerts` to the Concerts hub and returns **null** for anything else, in which case the tile declines and logs `browse.feature.unsupported` rather than navigating to a key no page renders | `BrowseTiles.cs:270-279`, `BrowseDirectoryPage.cs:27-35`, `BrowseRoutes.cs:37-40` |
| every directory cell | `Role = AutomationRole.Hyperlink`, `Focusable = true`, `Cursor = Hand`, `FocusVisualMargin = 2` on all four edges, `Key = m.Uri` | `BrowseTiles.cs:36-38, 71-73, 100-102, 171-173` |
| click the Charts band header | `model.OnOpenCategory(ChartPages.Charts, Strings.Home.Charts)` | `BrowseDirectory.cs:273` |
| click a Charts fold tile | `HomeCardNav.OpenBrowseSection(section, ..., origin: NavOrigin(Strings.Browse.HomeTitle, "browse", null))` - the origin being the IA root is what keeps the trail at `Browse > X` instead of `Browse > Browse > X` | `BrowseDirectory.cs:76-83` |
| click a category-page shelf header | `HomeCardNav.OpenBrowseSection(..., origin: NavOrigin(_pageTitle, route.Name, route.Arg))` -> a `browse-section:` route | `BrowsePage.cs:643-646` |
| click a category-page card | `HomeCardNav.Open(card, navPreview, model.Go, model.Play)` | `BrowsePage.cs:443-446, 660-664` |
| drag a category-page card | `WaveeResourceDragPayload.ForEntity(...)` - **except** Track and Episode cards, which carry `drag: null` | `BrowsePage.cs:655-658` |
| click "Show all" (masthead) | `LoadMore(primary.Uri)` - pages the flattened shelf **in place** via `browseSection`, never a navigation | `BrowsePage.cs:121-153, 182` |
| scroll near the tail (FlattenOne) | `HomeSectionAppendPreloader` fires the same `LoadMore`; `nearTail` is dropped after every append so only a fresh scroll continues the chain | `BrowsePage.cs:90, 428-440, 519-521` |
| click "Explore all categories" | `model.OnExploreAll()` -> `go("browse", null)` | `BrowsePage.cs:703`, `BrowsePageHost.cs:44` |
| click a masthead crumb | `go(crumb.RouteName, crumb.RouteArg)` | `ShellMastheadBand.cs:96-100` |
| click Retry on the directory error | `cats.Refresh` | `BrowseDirectory.cs:96` |

**Directory cells have no context menu and no drag source.** A browse category is a link, full stop
(`BrowseTiles.cs:31-52, 69-88, 94-133, 139-179` - no `WithMenu`, no `Draggable` anywhere in the file).

**Trail composition for a genre opened from search** (`Features/Shell/DrillTrail.cs:93-96`): a search origin is a *lookup*, not a
place, so its crumb stands alone - `"sleep" > Sleep`, **never** `"sleep" > Browse > Sleep`. A Home origin, by contrast, keeps the
Browse crumb between them. This is why `SearchRoutes.OpenGenre` threads a `NavOrigin(q, "search", q)` through `goOrigin`
(`SearchGenreTiles.cs:77, 82`, `SearchHero.cs:32`, `SearchPage.cs:661-662`).

**Tooltips.** Exactly one on Search (`Strings.Common.More` on the hero's `...`), none on Browse.

**Inline edit / selection / multi-select:** none on either surface.

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| query echo | `route.Arg` frozen at mount (`SearchPage.cs:39`) | `Search.QueryId` (StringId on the search row) | always ready - never skeletoned |
| facet tab list + order | `SearchResults.ChipOrder` (server rank) else a static fallback superset filtered by `HasAny`/`TotalFor` (`SearchPage.cs:328-356`) | ordered list on the `Search` row (**data gap 1**) | `Knows(SearchFields.Chips)`; until then the 11-pill skeleton |
| facet tab count | `TotalFor(facet)` or the chip's own `Total` (`SearchPage.cs:358-368`) | `Search.ChipTotal(facet)` (**data gap 1**) | shown only when `> 0` and not "All" |
| top-result hero | `SearchResults.TopHits[0]` (`SearchPage.cs:430-433`) | `Edges.SearchResult.Targets(searchSlot)[0]` + `SearchResultEdge` payload (**data gap 2**) | `Edges.SearchResult.State(searchSlot) != 0` **and** the target entity `Knows(Identity)` |
| hero eyebrow "Top result · <TypeLabel>" | `SearchTopHit.TypeLabel` (a server string) | `SearchResultEdge.TypeLabel` StringId (**data gap 2**) | same |
| hero chips (matched title / lyrics match / access label) | `MatchedTitle`, `MatchedLyrics`, `AccessLabel` | `SearchResultEdge.Flags` + `AccessLabel` StringId (**data gap 2**) | same |
| hero wash + Play fill | `CoverColorPlane` grading of `h.Image.Url` | cover palette service (chapter 00) | optional - absent scheme means no wash |
| hit row title / subtitle / art | `SearchTopHit.Name/Subtitle/Image` | the TARGET handle's `Title` / `ArtistLineId` / `ImageId` **plus** `SearchResultEdge.Subtitle` where the server's subtitle is not derivable | target `Knows(Identity)` |
| hit row round art | `SearchTopHit.RoundImage` | `SearchResultEdge.Flags` bit, or derive from `EntityKind == Artist \|\| User` | - |
| hit row trailing (follow / save) | `LibraryBridge.IsSaved(uri)` | `User.Me.Likes(track)` / `Edges.FollowedArtists.Contains` (plan §4.14) | reverse index, always answerable |
| hit row artwork present at all | `AppearancePrefs.TrackArtworkHidden(svc.Settings)` - the `HideTrackArtwork` setting + an `Epoch` signal as the update edge (`Design/AppearancePrefs.cs:8-18`) | the same settings read; it is a preference, not entity data | always ready; a settings flip must re-render the rows (0.2.9 uses the Epoch signal for exactly this) |
| suggestion row explicit flag | `SearchSuggestionItem.IsExplicit` (`Library.cs:15`) - **carried and never rendered** | `SuggestEdge.Flags` bit (data gap 5) | 0.3 should either render the badge or stop carrying the field |
| hit row menu + drag (track) | `TrackOf` - a linear scan of `SearchResults.Tracks` for the hit uri (`SearchPage.cs:791-798`) | the edge target IS the `Track` handle - **the scan disappears** | target `Knows(TrackFields.Identity)` |
| Songs facet rows | `SearchResults.Tracks` projected to hits (`SearchPage.cs:555-566`) | `Edges.SearchResult` restricted to the Tracks facet parent | edge `State == complete` for the first page |
| Albums / Playlists facet grid | `VirtualCollection` over `SearchAsync(q, facet, off, 50)` + a page-0 seed (`SearchFacetGrid.cs:82-92`) | per-facet `EdgeTable` with `State` (0/1/2) and `Total` columns - the plan's own CSR paging fields (plan §4.3) | cells render from `Targets(...)`; unrealised indices below `Total` render the 0.2.9 placeholder cell |
| Genres tiles | a SECOND fetch: `SearchAsync(q, Genres, 0, 30)` (`SearchGenreTiles.cs:38-40`) | one `Ensure` on mount demands the whole model, including genres (**data gap 3**) | `Knows(SearchFields.Genres)` -> until then the 8 seed cells, shimmered |
| Related searches | a THIRD fetch: `SuggestRichAsync(q)` (`SearchGenreTiles.cs:116-118`) | `Search.RelatedQueryIds` StringId list (**data gap 4**) | `Knows(SearchFields.Related)` -> until then nothing (`() => new BoxEl()`) |
| suggestion popup rows | `SuggestRichAsync` through `OmnibarSuggestQuery` | a `Suggest` synthetic parent + `Edges.SuggestResult` (**data gap 5**) | `SuggestState` machine, ported verbatim |
| directory band membership | `BrowseTaxonomy.GroupOf(category.Uri)` - a compile-time uri map (`BrowseTaxonomy.cs:40-106`) | unchanged, `Browse.cs` CORE | always ready |
| directory band ordering | `BandOrder` + `string.Compare(..., CurrentCultureIgnoreCase)` within a band, Top keeping server order (`BrowseTaxonomy.cs:136-140`) | unchanged | always ready |
| directory cell title / colour / art | `BrowseCategory.Title/Color/Artwork` | a `BrowseCat` handle over a new `BrowseTable` (**data gap 6**) | `Knows(BrowseFields.Identity)` - until then the seeds, `.Skeletonized(true)` |
| Charts band | `HomeBrowseCards.LoadChartDeckAsync` over `ChartSections.All` | `Home`/`Browse` section rows (chapter 11) | independent of the categories resource - a Featured outage must never blank the rest of the directory |
| category page title | `loadedNow.Title` else `route.Arg` (`BrowsePage.cs:160-161`) | `BrowsePage.TitleId` else the route arg | published to the masthead OUTSIDE the skeleton region - **real from frame one** |
| category page sections | `BrowsePageModel.Sections` (uri, title, kind, cards, categories, total, nextOffset) | a `BrowseSectionTable` + `EdgeTable<BrowseCardEdge>` (**data gap 7**) | per-section `State`/`Total`, exactly the 0.2.9 `Total > Cards.Count` "has more" rule |
| category page "Show all" | `BrowsePageLayout.HasMore(primary)` && !exhausted (`BrowsePage.cs:179`) | `section.Total > Edges.BrowseCards.Length(sectionSlot)` && state != complete | derived on the model, never probed by the UI |
| category page mode | `BrowsePageLayout.Of(model)` - pure | unchanged, `Browse.cs` CORE | - |
| session survival across keep-alive eviction | `BrowseDirectoryStore` (15 min TTL) / `BrowsePageStore` (16 FIFO slots) | **deleted** - table rows outlive page mounts by construction, and `Store.cs` persists them | a remount reads the table and paints full-height content immediately, which is what makes the keyed scroll offset restore against real layout |

**Demand on mount.** 0.2.9 issues up to **four** requests per search page (the facet results, the genres op, the suggest op, and
page 1+ of a facet grid) from three different components. 0.3 must issue **one** demand from the page:

```csharp
UseEffect(() => {
    Entities.Ensure(_s, SearchFields.All);                       // chips, totals, genres, related queries
    Entities.EnsureEdges(_s, EdgeKind.SearchResult, facet);      // the whole facet's result list
    Entities.EnsureRows(_s.ResultSlots(facet), TrackFields.Row); // one batch for every target entity
});
```

No page-side visible-window fetching: the facet grid asks for its whole model and the query layer batches
(CLAUDE.md; memory note *no-page-side-fetch-windows*). The 0.2.9 `SearchFacetGrid.EnsureRange` is exactly the pattern being
removed - in 0.3 the page demands the facet's `Total` rows and the planner decides what to send.

### DATA GAPS

| # | element | 0.2.9 source | proposed 0.3 column / edge |
|---|---|---|---|
| 1 | facet chips: ordered facet list + per-facet total | `searchV2.chipOrder` -> `SearchChip(Facet, Total)` (`Wavee.Core/Library/Library.cs:46`) | on the `Search` row: `Column<ulong> ChipMask` (11 bits for presence) + `Column<byte> ChipRank[11]` + `Column<int> ChipTotal[11]`, or a tiny `EdgeTable<ChipEdge(byte Facet, int Total)>` parented on the search slot to keep server rank order |
| 2 | per-HIT search chrome: `TypeLabel`, `Subtitle`, `Detail`, `Meta`, `AccessLabel`, `MatchedLyrics`, `MatchedTitle`, `RoundImage`, `Followable` | `topResultsV2.itemsV2` -> `SearchTopHit` (`Library.cs:54-59`) | `readonly record struct SearchResultEdge(byte Kind, byte Flags, StringId TypeLabel, StringId Subtitle, StringId Detail, StringId Meta, StringId AccessLabel)` as the payload of `Edges.SearchResult`. **The plan declares `SearchResult` as `EdgeTable<NoEdge>` (§4.3) - that cannot hold any of this.** |
| 3 | search genres: uri, name, accent `uint` | `searchGenres` -> `SearchGenre(Uri, Name, Accent)` (`Library.cs:49`) | shares the `BrowseTable` proposed in gap 6 (a `spotify:genre:<id>` maps 1:1 onto `spotify:page:<id>` - `SearchRoutes.cs:15-17`), plus an `EdgeTable<NoEdge> SearchGenres` parented on the search slot |
| 4 | related searches: an ordered list of query strings | `searchSuggestions` -> `SearchSuggestions.Queries` | `EdgeTable<StringId> RelatedQueries` on the search slot (targets unused, payload IS the string id - the same shape the plan gives `TrackTags`) |
| 5 | suggestion rows: kind, uri, title, subtitle, image, isExplicit + the query strings | `SearchSuggestionItem` / `SearchSuggestions` (`Library.cs:9-20`) | a singleton `Suggest` parent slot with `EdgeTable<SuggestEdge(byte Kind, StringId Subtitle)> SuggestItems` and `EdgeTable<StringId> SuggestQueries`. Lives only in memory - never persisted by `Store.cs` |
| 6 | browse categories: uri, title, colour `uint`, artwork, isClientFeature | `browseAll` -> `BrowseCategory` (`Wavee.Core/Library/Browse.cs:21-26`) | **a `BrowseTable` that plan §4.1's `Scope` does not declare**: `Column<StringId> Title, Image; Column<uint> Color; Column<uint> Flags /* ClientFeature */`, keyed by the `spotify:page:` uri |
| 7 | browse page + sections: page accent, section uri/title/kind/total/nextOffset, and per-card subtitle + accent | `browsePage` / `browseSection` -> `BrowsePageModel` / `BrowseSection` / `BrowseCard` (`Browse.cs:30-74`) | `BrowseSectionTable` (`Title`, `Kind`, `Total`, `NextOffset`) + `EdgeTable<BrowseCardEdge(StringId Subtitle, uint Accent)> BrowseCards` + `EdgeTable<NoEdge> BrowseSections` parented on the page row. `BrowseCard.Subtitle` is a server editorial string, **not** derivable from the target entity |
| 8 | `nextOffset` tri-state (a real offset / `PagingComplete = -1` / absent) | `BrowseSection.NextOffset` + `HomeSectionPaging.BrowseSectionNextOffset` (`Browse.cs:40-60`) | keep the sentinel on the section row; the plan's `State`/`Total` pair alone loses the "server explicitly said done even though Total claims more" case |
| 9 | cover palette scheme for the hero wash | `CoverColorPlane.TryGetScheme(url, isLight)` | chapter 00's cover-palette service; Search only consumes it |
| 10 | "Music video" vs "Song" on a fallback track row | `VideoPresence.HasVideo(track)` (`SearchPage.cs:840`) | `TrackFields.Video` / `TrackFlags.HasVideo` (already in plan §4.2) - and this line must start using `search.subtitleMusicVideo` / `search.subtitleSong` instead of the hardcoded English it uses today |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `SearchChipSkeletonPolicy` | `Features/Search/SearchChipSkeletonPolicy.cs` (11 lines) | whether the facet row shows the 11-pill skeleton: `!hasChipSource && isPending`. A later facet-switch fetch must NOT re-trigger it | `Wavee.Tests/SearchChipSkeletonPolicyTests.cs` (4 facts) | CORE section of `Entities/Search.cs` |
| `OmnibarSuggestQuery` + `SuggestState` | `Features/Search/OmnibarSuggestQuery.cs` (138 lines) | the whole suggestion lifecycle: generation-keyed publish guard (never text equality), Pending-on-keystroke, previous rows kept under Pending, Empty vs Failed, cancellation is not an answer, Retry re-arms | `Wavee.Tests/OmnibarSuggestQueryTests.cs` (283 lines, 14+ facts) | CORE section of `Entities/Search.cs` |
| `SearchSuggestions.GhostFor` | `Wavee.Core/Library/Library.cs:26-36` | the inline completion: the first query longer than and prefixed by the typed text (ordinal-ignore-case), not blindly `Queries[0]` | covered indirectly by the omnibar tests | CORE section of `Entities/Search.cs` |
| `SearchRoutes.OpenGenre` | `Features/Search/SearchRoutes.cs` (28 lines) | `spotify:genre:<id>` -> `browse:spotify:page:<id>`; anything unparseable re-commits the genre name as a search | none today | CORE section of `Shell/Shell.cs` (routes, plan §4.11) |
| `SearchHighlight.Row` + `LineBoxFor` | `Design/SearchHighlight.cs` (81 lines) | the accent match pill: `[before][matched on Tok.AccentSelectedTextBackground][after]`, radius `Radii.Control`, padding (3,1,3,1), ink `Tok.TextOnAccentSelectedText`; single-line vs wrapping geometry (`ClipToBounds`+`Basis 0` only when single-line; `MaxHeight = lines * LineBoxFor(size)` when wrapping); line-box table 14->20, 12->16, else `ceil(size*1.43)` | none today | `Platform/Design.cs`. **Note: used by Library rows and the Charts grid, NOT by the Search page** |
| `BrowsePageLayout` | `Features/Browse/BrowsePageLayout.cs` (69 lines) | Shelves / FlattenOne / FlattenTwoConcat / FlattenTwoStacked; empty-section survivors (a Shelf dies on `Cards.Count == 0`, a CategoryGrid/Related on `Categories.Count == 0`, `:23`); only **Shelf** sections are counted for the mode decision (`:37`); `TitleIsRedundant` (blank or OrdinalIgnoreCase equal to the page title) gates FlattenOne, while FlattenTwo* needs both titles genuinely **blank** — the stricter `IsBlank` (`:49, 68`); a page with no uri never flattens (`:29-30`); `HasMore(s) => s.Total > s.Cards.Count` | `Wavee.Tests/BrowsePageLayoutTests.cs` (190 lines, 8+ facts) | CORE section of `Entities/Browse.cs` |
| `BrowseTaxonomy` (+ `BrowseGroup`, `ChartPages`, `ChartSections`) | `Features/Browse/BrowseTaxonomy.cs` (181 lines) | uri -> band (never title), unmapped -> `More`, the fixed `BandOrder`, per-band current-culture alphabetisation with Top keeping server order, empty bands omitted | `Wavee.Tests/BrowseTaxonomyTests.cs` (153 lines, `BrowseChartTaxonomyTests`) **and** `Wavee.Tests/WireAdornmentTests.cs:155+` (`BrowseTaxonomyTests`) | CORE section of `Entities/Browse.cs` |
| `BrowseDirectorySeeds` | `Features/Browse/BrowseDirectorySeeds.cs` (96 lines) | the loading directory's SHAPE - the taxonomy map mirrored entry for entry (4 / 10 / 25 / 14) plus 3 unmapped tail entries | `BrowseTaxonomyTests.DirectorySkeletonSeeds_GroupIntoEveryBand_WithTheirIntendedCounts` pins all five counts | CORE section of `Entities/Browse.cs` - must stay test-visible |
| `BrowseLayout` | `Features/Browse/BrowseTiles.cs:286-358` | `FrameTop 32`, `MastheadReserve` (= `BrowseMastheadMetrics.Reserve`), `FrameX 36`, `Frame(bottom)`, `DirectoryFallbackWidth 900`, `WordChipH 36`, `NameChipH 32`, `ChipGap 8`, `TickW 3`, `TickH 14`, `BarHeight 52`, `MoreHeight 88`, `Peek 80`, `PeekCopyFrac 0.62`, `Pip 8`, `LinkColumns` (720/380 bands), `BarColMin 132` + `BarColumns` (plain floor-fit, no cap), `MoreColMin 168` + `MoreColumns` (cap 4), `StarGrid` (Star tracks + `AlignSelf.Stretch`, and a 0-cell list returns a bare `BoxEl`) | none today - **the four column functions and `StarGrid`'s empty case are worth a test in 0.3** | CORE section of `Entities/Browse.cs` |
| `BrowseMastheadMetrics` | `Features/Browse/BrowseMastheadMetrics.cs` (39 lines) | `TitleLine 52`, `Reserve 84`, `BodyTop 100`, `ClipInset 84`, `ClipFadeBand 24`, `FamilyBodyPad`, `FamilyUnderBandPad` | `Wavee.Tests/BrowseMastheadMetricsTests.cs` (52 lines, 4 facts) | CORE section of `Entities/Browse.cs` |
| `BrowseRoutes` | `Features/Browse/BrowseRoutes.cs` (41 lines) | `Home = "browse"` (exact, never prefix-matched), `Prefix = "browse:"`, `UriOf`, `FeatureRoute` (only `spotify:concerts` resolves; everything else returns null so the tile declines) | `Wavee.Tests/NavRouteNormalizerTests.cs`, `ShellNavDestTests.cs`, `DrillTrailTests.cs` | CORE section of `Shell/Shell.cs` |
| `NavRouteNormalizer.Apply` | `Features/Shell/NavRouteNormalizer.cs` (28 lines) | blank `search` -> `browse`; the legacy recents route -> `recents` | `Wavee.Tests/NavRouteNormalizerTests.cs` | CORE section of `Shell/Shell.cs` (chapter 18 owns it; listed here because the empty-search behaviour depends on it) |
| `SearchResults.TotalFor` / `HasAny` | `Wavee.Core/Library/Library.cs:92-124` | facet total resolution: a per-facet `…Total` field of `-1` means "the server sent no total", in which case the **local list's own Count** is returned - so `TotalFor` never distinguishes not-queried from queried-empty, it just answers 0. `HasAny` is the separate "does the local list have rows" question. `All` resolves to `TopHits.Count` or the four local lists summed | none today | folds into the `Search` row's chip columns (data gap 1) |
| `BrowseTiles.ToModel` | `Features/Browse/BrowseTiles.cs:270-281` | one category -> one tile model, including the client-feature branch and the shared inert `ToModelNoop` for a page with no navigation host (the loading directory) | none today | CORE section of `Entities/Browse.cs` |
| `SearchPage.FacetsFrom` / `FacetCount` | `Features/Search/SearchPage.cs:328-368` | the facet list: chip order first, then the ten-member fallback filtered by `HasAny \|\| TotalFor > 0`; count falls back to the chip's own total | none today - **extract and test in 0.3** | CORE section of `Entities/Search.cs` |
| `SearchPage.FallbackRows` interleave | `Features/Search/SearchPage.cs:800-835` | with no `TopHits`: top artist \| album \| playlist as the large row, then up to 8 tracks with artists spliced after index 2 and 5, cap 14 rows, then up to 4 albums and 4 playlists | none today - **extract and test in 0.3** | CORE section of `Entities/Search.cs` |
| `SearchHitsGrid.ColsFor` | `Features/Search/SearchPage.cs:1028-1035` | `clamp(floor((w+12)/292), 1, 3)`, growing immediately, shrinking only past a 24-DIP hysteresis; rows = cols except 1-wide -> 3 | none today - **extract and test in 0.3** | CORE section of `Entities/Search.cs` |
| `SearchPage.PlaylistShelfItems` / `PlaylistOwnerOf` | `Features/Search/SearchPage.cs:478-512` | the All-tab playlist rail: top-hit playlists first, then `r.Playlists`, deduped by uri, skipping the hero's own uri; owner = the substring after the first `" • "` | none today | CORE section of `Entities/Search.cs` |

---

## 9. Re-author notes

### Must not be simplified

1. **The three-shape skeleton.** Collapsing `SearchShimmer` back to one bar stack is the exact regression the comment at
   `SearchPage.cs:129-135` documents: "All" is the default landing facet on every fresh query, and swapping a plain list for a
   hero card plus a card shelf reflows the whole page the instant data arrives.
2. **`smoothResize: false` on every page-level region.** Three sites say so for the same reason
   (`SearchPage.cs:79-87`, `BrowseDirectory.cs:90-94`, `BrowsePage.cs:225`): easing a height from 0 to thousands of DIP makes the
   region clip its own content into a strip that grows line by line.
3. **The 11-pill chip skeleton at its real widths.** Not 3, not "a few" - the eleven-member superset, because that is what almost
   always lands and it wraps to two rows at typical pane widths.
4. **`BrowseDirectorySeeds` mirroring the taxonomy map entry for entry.** A three-per-band placeholder is cheaper to author and
   makes the page height jump by hundreds of DIP when data lands (`BrowseDirectorySeeds.cs:12-19`).
5. **Browse's five densities.** Flattening them to one card grid destroys the whole point of the page (`BrowseDirectory.cs:15-25`).
6. **The category colour as wash + tick, never as the plate.** Reverting to a full-bleed colour field forces on-media white ink and
   breaks the light theme (`BrowseTiles.cs:122-127`).
7. **`Shrink`, never `Grow`, on ellipsised titles in definite-width rows.** `Grow` + `MinWidth 0` + ellipsis makes a title report
   ONE GLYPH as its minimum size - the "R..." collapse. Three call sites carry the warning (`SearchPage.cs:108-110, 1134-1135`,
   `SearchGenreTiles.cs:171-173`).
8. **`FillCross` / `SearchSectionRoot.Stretch`.** `ComponentEl` has no layout props; a section component's root must be wrapped in
   a stretching column or its `TickHeader` measures as one glyph (`SearchPage.cs:470-476`, `SearchGenreTiles.cs:171-179`).
9. **The category page's masthead published OUTSIDE the skeleton region.** The breadcrumb must not shimmer - the route already
   carries the destination title (`BrowsePage.cs:106-111, 188-193`).
10. **`FlattenBody`'s `Grow 1 / MinHeight 0` slot and `Responsive.Of(..., grow: 1f)`.** Without them the virtual grid measures as
    content-sized, its natural height is 0, and `ClipToBounds` shears every square cover while the page box still fills the pane
    (`BrowsePage.cs:460-473`).
11. **The explicit `shimmerSource` overload on the category page.** `PagedShelf` yields zero rows against an unmeasured viewport,
    so `content(seed)` cannot be the shimmer (`BrowsePage.cs:233-257`).
12. **`SkelReveal.None` on the directory, `FadeOnly` on the category page.** `None` floors the shimmer's `ExitMs` at 400 ms
    assuming the body owns its entrance; the category body has none, so `None` leaves a ghost under real content for 400 ms
    (`BrowsePage.cs:212-217`).

### Traps

* **Props freeze at mount.** `SearchHero(hit)`, `SearchGenreTiles(q, go)`, `SearchRelatedQueries(q, go)` and `SearchPage(route)`
  all take ctor args. 0.2.9 survives this only by keying every one of them (`"hero:<uri>"`, `"genres:<q>"`, `"related:<q>"`, and
  a whole keep-alive slot per query). `SearchFacetGrid` takes the other route - `UseProps<Props>()` with a re-pushed record,
  documented at `SearchFacetGrid.cs:27-29` precisely because "a ctor arg would freeze at mount" when the page-level resource
  refreshes under stale-while-revalidate. **In 0.3 make all of these static `Element` functions over the handle** and the problem
  disappears; keep a Component only where one is genuinely needed (the page itself, the omnibar field).
* **`ReuseGuard`.** The Wave 5 gate is "row binding proven by the ReuseGuard (no remounts on a table publish)". Keys that embed a
  data value (`"best:<uri>:<len>"`, `"hits-shelf:<n>:<cols>:<rows>:..."`, `"media-shelf:<n>:<first>"`,
  `"browse-peek:<uri>:<cardW>"`) remount on every data change and will trip it. Only three key families should survive: the facet
  body, the category page, and column-count keys on `Responsive` grids.
* **`Key` on a Component's root is ignored.** `SearchHitsGrid` wraps `PagedShelf` in a plain box specifically because
  "PagedShelf's Key must be a CHILD (ReconcileSingleChild ignores Key on this component's root)" (`SearchPage.cs:986-987`).
* **Zero-allocation scroll frames vs per-row richness.** 0.2.9 reconciles them by *not virtualizing the rich rows at all*: the
  Songs facet is a flat `AutoGrid` of 50 rows (page size 50, no nested pager - `SearchPage.cs:907-909`), Best matches is a
  `PagedShelf` page of at most 9 cells, and only the uniform Album/Playlist grid virtualizes - through `LazyGrid`, whose cells are
  `MediaCard.GridCard` (a cover + two labels), not the row with its eyebrow/chip/detail/meta/trailing. **A search HIT ROW is never
  virtualized in 0.2.9.** In 0.3, if the facet list becomes an `ItemsView.CreateBound`, the row must be built from bound scopes
  (`item.Text`, `item.Image`, `item.Signal`) or the alloc gate will fail.
* **The Browse directory is EAGER and mounts exactly once per page mount, with a taxonomy-fixed band count** - the two conditions
  `WaveeEntrance` requires (`BrowseDirectory.cs:105-108`). Virtualizing a band breaks the cascade.
* **`Responsive.Of` fallback widths differ by surface and matter**: 900 for Browse's grids (`BrowseLayout.DirectoryFallbackWidth`),
  1100 for the category page's flattened grid (`HomeModuleLayout.FallbackWidth`). Getting them wrong costs a first-frame reflow.
* **The `DrillTrail` search-origin rule.** A `NavOrigin` whose `RouteName` is `"search"` is a *lookup*; its crumb stands alone.
  Any 0.3 rewrite of the origin plumbing must keep `LookupOrigin` (`DrillTrail.cs:93-96`).

### 0.2.9 gaps to FIX in 0.3, not to reproduce

These are places where 0.2.9 is thin rather than deliberate. Each is small; all of them are invisible until someone looks.

1. **Two different "no results" empties on the same page.** The generic arm carries the `Nothing matched "<q>"` caption
   (`SearchPage.cs:412-413`); the Songs / Albums / Playlists arms do not (`:525, 538, 544`). Pick the captioned one.
2. **Related searches fails silently.** A dead typeahead op removes the whole section with no trace
   (`SearchGenreTiles.cs:123`). The Genres section right above it shows `ErrorState.Compact`. Match them.
3. **Nothing on the Search page is `Focusable`.** Facet tabs, the hero card, hit rows and section headers (except the one
   with an `open` action, `SearchPage.cs:1147`) set a `Role` but never `Focusable = true`. Browse's cells all do.
4. **No `AutomationName` overrides anywhere on this surface**, and the omnibar rows are `MenuItem` with no accessible
   description (`ShellToolbar.cs:456, 495`).
5. **`IsExplicit` is carried and never rendered** in the suggestion popup (`Library.cs:15` vs `ShellToolbar.cs:464-523`) -
   the only surface in the app where an explicit marker is available and dropped.
6. **`search.noGenreResults`** is a live loc key with no call site (`assets/loc/en-US.json:624`).
7. **`TrackRowFb`'s "Music video" / "Song" is hardcoded English** (`SearchPage.cs:840`) while
   `search.subtitleMusicVideo` / `search.subtitleSong` already exist (`en-US.json:632-633`). See data gap 10.
8. **The Artists facet row is thinner than every other hit row** - no menu, no drag, no trailing control
   (`SearchPage.cs:601-622`). See §6.1.
9. **The search page's error state is terminal** - no `onRetry` is passed (`SearchPage.cs:86`), unlike Browse's two.

### Where plan sections 2 / 4.12 / 4.13 are wrong or too thin

1. ~~**§2 declares no `Browse.Page.cs`.**~~ **Settled 2026-09-12: `Entities/Browse.Page.cs` is in the tree, owner P.**
   `Browse.UI.cs` (600 lines) was expected to hold the tile factories, the directory composition, the category page, the page
   host, the masthead publication, the shimmer bodies and the two layout modes; 0.2.9 spends 1,220 lines on those
   (`BrowseDirectory` 363 + `BrowsePage` 730 + `BrowseDirectoryPage` 67 + `BrowsePageHost` 60). The new file takes the page
   half - `BrowsePage` 730 + `BrowseDirectoryPage` 67 + `BrowsePageHost` 60 = **857 lines of 0.2.9 with no home today**,
   re-counted from source - at **~700** in 0.3, and `Browse.UI.cs` keeps the tiles and the directory body. Note what it does
   **not** take: the `browse-section:` drill is `Home.SectionPage` in `Entities/Home.Page.cs` (A15, chapter 12), one class for
   both sources, so `Browse.Page.cs` owns the directory page and the category page only.
2. **§4.3 declares `Edges.SearchResult` as `EdgeTable<NoEdge>`.** A search result carries seven server-authored fields that belong
   to the RESULT, not to the entity (`TypeLabel`, `Subtitle`, `Detail`, `Meta`, `AccessLabel`, `MatchedLyrics`/`MatchedTitle`
   flags, `RoundImage`/`Followable` flags). `NoEdge` cannot hold them. It also needs to be **eleven** parented lists (one per
   facet), not one - the All tab's ordered `topResultsV2` and the Albums facet's paged list are different collections with
   different `State`/`Total`.
3. **§4.1's `Scope` has no `BrowseTable` and no `SearchTable`,** yet §2 names `Browse.cs` and `Search.cs` as entity files. Both
   surfaces need a table set of their own (see data gaps 1-7).
4. **§4.12's one row is `Height = 56` with number / cover 40 / title / artist / added-at / duration / equalizer.** The search row
   is 64 (small), 112 (large), or auto-sized with a 72 floor (audiobook), and it carries an eyebrow, an optional type chip, a
   two-line detail blurb, a meta line, a trailing follow/save control, `detailBelowArt` restacking, clickable subtitle anchors and
   an unplated fill ladder. **`Track.Row(style)` is not the search row.** Either `RowStyle` grows all of the above, or
   `Search.UI.cs` owns its own row - the second is what 0.2.9 does and what chapter 01 should arbitrate.
5. **§4.13's Album wireframe is the only worked page example,** and it is a one-column detail page. Neither Search (a tab bar over
   five genuinely different body geometries) nor Browse (five cell densities and four body modes) has a precedent in the plan.
   The wireframes in §2 above are that precedent.
6. **§5 Wave 5 owner P carries Home.UI + Home.Page + Search.UI + Search.Page + Browse.UI** - by 0.2.9 line count the heaviest
   owner in the wave by a wide margin. Search + Browse alone are 4,502 lines; Home is larger still. **Split P.**
7. **The plan never mentions the omnibar suggestion surface.** `OmnibarSuggestQuery` (138 lines, 283 lines of tests) is a CORE
   file with no home in §2. It belongs in `Entities/Search.cs`; its fetch loop and popup belong to owner I's `Shell.*`, which
   means owners I and P share a contract that the plan does not name.
8. **Line budget.**

| file | plan §2 target | 0.2.9 equivalent (total / code) | honest estimate |
|---|---|---|---|
| `Entities/Search.cs` (CORE) | 300 | `OmnibarSuggestQuery` 138 + `SearchChipSkeletonPolicy` 11 + the `SearchResults`/`SearchTopHit`/`SearchSuggestions` models ~90 + `FacetsFrom`/`FallbackRows`/`ColsFor`/`PlaylistShelfItems` extracted ~120 = **~360 / ~250** | **450** |
| `Entities/Search.UI.cs` | 500 | `SearchPage.cs` rows+grids+chrome ~700 + `SearchHero` 156 + `SearchGenreTiles` 180 + `SearchFacetGrid` 165 + the popup rows ~180 = **~1,380 / ~900** | **1,100** |
| `Entities/Search.Page.cs` | 500 | `SearchPage.cs` page shell + chip bar + shimmers + dispatch ~450 = **~450 / ~300** | **550** |
| `Entities/Browse.cs` (CORE) | 300 | `BrowseTaxonomy` 181 + `BrowseDirectorySeeds` 96 + `BrowsePageLayout` 69 + `BrowseLayout` 75 + `BrowseMastheadMetrics` 39 = **460 / ~290** | **500** |
| `Entities/Browse.UI.cs` | 600 | `BrowseTiles` 358 + `BrowseDirectory` body/bands ~200 = **~560 / ~330** | **650** |
| `Entities/Browse.Page.cs` | **settled 2026-09-12 - in the tree, owner P** | `BrowsePage` 730 + `BrowseDirectoryPage` 67 + `BrowsePageHost` 60 = **857 / ~600** | **700** |
| **surface total** | **2,900** (2,200 + the 700 now in the tree) | **4,502 / 2,880** | **3,950** |

The gap is not fat: roughly a third of the 0.2.9 lines are comments recording *why* a number is what it is, and those must
survive. The real savings come from deleting `VirtualCollection`/`Seed`/re-pushed props (`SearchFacetGrid`, ~80 lines), the two
session stores (`BrowseDirectoryStore` + `BrowsePageStore`, 118 lines), the three-way stale/fresh/empty cache policy in
`BrowseDirectory`/`BrowsePage` (~130 lines), and the `TrackOf` uri scan - all replaced by the table model.

### Files or pages missing from the §2 tree

* ~~`Entities/Browse.Page.cs`~~ - **settled 2026-09-12: in the tree at ~700 lines, owner P** (above). Its scope is the
  directory page + the category page + the page host; the `browse-section:` drill stays `Home.SectionPage` (A15).
* No home for `BrowseRoutes` / `SearchRoutes` / `NavRouteNormalizer` is named; §4.11 says `Shell.cs` is "routes and navigation
  (CORE)", so state that explicitly - a Browse route constant living in `Browse.cs` and a Search route constant in `Search.cs`
  would re-create the two-copies-of-one-sequence problem `BrowseTaxonomy.BandOrder` was written to solve.
* The `--fake` seed (`Entities.SeedFake()`, owner Q) must cover **both** surfaces: a search query with top hits across at least
  five kinds plus chips and genres, and a browse directory with all five bands populated plus at least one category page in each
  of the four `BrowsePageLayout` modes. Nothing in §5 says so, and the Wave 5 gate ("`--fake` opens every route") is otherwise
  satisfied by an empty page.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
"Static" = a still screenshot at rest; "hover" = a capture with the pointer held on the named element; "frames" = a screen
recording stepped frame by frame. Window sizes are the OUTER window; the pane is whatever the shell gives the page at that size.

**Search - frame and chips**

1. Route `search` with an empty/whitespace arg lands on the **Browse directory**, not a Search page. Verify: type a query, commit,
   clear the omnibar, press Enter. Static.
2. The query echo is lowercased regardless of what was typed. Verify: search `SLEEP`; the echo reads `sleep`. @1280. Static.
3. The query echo is 40/52 at Weight 400 in the Display face, one line, character-ellipsised when the pane is tight. Verify: search
   a 120-character string @800. Static.
4. On a fresh query the facet row shows **eleven** placeholder pills, wrapping to two rows @1280, before any tab text appears.
   Verify: frames, from the moment of commit.
5. The placeholder pills do NOT return when a facet tab is clicked (a later fetch is pending but a chip source is cached).
   Verify: click Songs, then Albums; frames.
6. Facet tabs appear in server `chipOrder` order with "All" always first. Verify @1280. Static.
7. A facet tab's count is omitted for "All" and for any facet whose total is 0. Static.
8. The selected tab's underline is 3 DIP, full tab width, accent, and **grows from its left edge** over ~260 ms. Verify: frames at
   30 fps; the underline should be visibly partial for ~8 frames.
9. Switching from a lower to a higher chip index slides the body in from the right; the reverse slides from the left. Verify:
   frames, All -> Albums, then Albums -> All.
10. Mounting the page does **not** slide the body. Verify: frames from commit.

**Search - All tab**

11. The All tab's skeleton is a 228-DIP hero plate plus a wrapping grid of 168-DIP cards - never a stack of bars. Verify: frames
    immediately after commit @1280.
12. The hero card is exactly 228 DIP tall at every pane width. Verify @1280 and @800. Static, measured.
13. The hero cover is 300x260, rotated −2.5°, offset (+40, −16), and visibly **cropped by the card's right edge**. Static @1280.
14. A gradeable cover produces a radial wash anchored under the cover's left edge; an ungradeable one produces a plain card plate
    with no wash. Verify: search a track with a rich cover and one with a monochrome cover. Static.
15. The hero's Play pill takes the cover's graded accent, not the app accent, whenever a wash is present. Static.
16. The hero eyebrow reads `Top result · <kind>` in accent ink at 12/16/600 with visible letter tracking. Static, zoomed.
17. A genre hero shows **Open page** and no Play; a profile hero shows neither. Verify: search a genre term, then a username.
    Static.
18. "matched title" / "Lyrics match" chips are outlined capsules (transparent fill, 1px stroke), never filled. Static.
19. The hero's `...` shows a tooltip after the standard delay. Hover.
20. Right-clicking a **track** hero opens the full track menu (Go to album / Go to artist present); right-clicking an album hero
    opens the card menu. Verify both.
21. "Best matches" is a 3x3 page @1280, 2x2 @~940 pane, and 1x3 (three rows) @~400 pane. Verify at each width. Static.
22. The best-matches column count grows immediately on widening but only drops after ~24 DIP of extra shrink. Verify: drag the
    window slowly across the 840-DIP content-width boundary; frames.
23. A page containing an audiobook hit reserves 128 DIP per cell for **every** cell on that page. Verify: search an audiobook
    term. Static, measured.
24. The All tab's playlist rail header carries a chevron and switching to the Playlists **tab** (not a navigation) when clicked.
    Verify: click it; the chip row selection changes and the URL/tab does not.
25. The playlist rail never repeats the hero's own item. Verify: search a playlist name; the hero playlist is absent from the rail.
26. The genre section renders `BrowseTiles.Link` cells - pip + secondary text with a hover fill - **identical** to the Browse
    directory's Genres band. Verify: capture both, overlay.
27. Related searches are lowercase, Weight 400, at the 20/28 rung, and turn accent on hover. Hover.
28. The typed query never appears in its own Related searches list. Static.

**Search - dedicated facets**

29. Songs is a wrapping `AutoGrid` at 3 columns @1280 pane 960 - not a shelf, no pips, no chevrons. Static.
30. Albums/Playlists is a uniform virtualized grid whose unrealised cells are card-shaped placeholders (square + two bars), so
    scrolling never shifts rows. Verify: search a term with 100+ albums and scroll fast; frames.
31. Opening the Albums tab issues **no** extra request for page 0 (it is seeded). Verify: the network/diagnostics log shows one
    `searchAlbums` at offset 0, not two.
32. The Artists tab renders 60-DIP rows with circular art and a trailing "Artist" capsule. Static.
33. A podcast row and an episode row look identical whether reached from the All tab or from their own tab. Verify: capture both.
34. An audiobook row stacks its meta + blurb **below** the art block; every other kind puts them beside it. Static.
35. A premium-access eyebrow is green (`WaveeColors.PremiumText`); a lyrics-match eyebrow is accent. Static, both themes.
36. A facet with no results shows its own sentence ("No podcasts or shows found"), not the generic one. Verify each of
    Audiobooks / Podcasts / Episodes / Profiles / Authors.
37. The generic empty state shows the headline **and** the `Nothing matched "<q>"` caption. Static.
38. The Genres tab with no genres shows **nothing** (no sentence). Static.

**Search - omnibar**

39. Typing one letter never flashes "No results found". Verify: frames at 60 fps while typing a single character.
40. While a new answer is pending, the previous answer's rows stay visible under an indeterminate progress bar. Frames.
41. A failed suggestion request shows "Couldn't load suggestions" with a Retry button; Retry re-issues. Verify: with the network
    off, type a query.
42. The inline ghost completes to the first query that is longer than and prefixed by the typed text - not necessarily the first
    suggestion. Verify: type `slee` against a list whose first entry does not start with it.
43. Arrow-down from the last row returns to "no selection" (the ghost reappears) rather than wrapping to the first. Frames.
44. At most 6 query rows and 10 item rows are shown, with a 1px divider between the two groups. Static.
45. A query row's matched run is Weight 700 in primary ink; the rest is Weight 400 in secondary. Static, zoomed.
46. Rich rows are 58 DIP with 44-DIP art (circular for artists and profiles). Static, measured.
47. The popup is at least 400 DIP wide even when the omnibar is crushed to its icon state. Verify @560 window.
48. A track row shows Play, heart, `...` and a type pill; a genre row shows only the pill. Static.

**Browse - directory**

49. The masthead reads "Browse" at 40/52/400 and paints no background - live Mica shows through it. Static, both themes.
50. The directory's first content row starts 100 DIP below the top of the page area. Static, measured.
51. Scrolling cuts the directory at 84 DIP with a 24-DIP feather; at rest there is no feather. Frames while scrolling from 0.
52. The bands appear in order Top, Charts, For you, Genres, Mood & activity, More, cascading ~40 ms apart. Frames from navigation.
53. The loading directory has the **same** band count and the **same** wrap counts as the loaded one - the page height does not
    jump when data lands. Verify: frames; measure total scroll extent before and after.
54. Top pills are 36 DIP; For you pills are 32 DIP; both are capsules with a 1px stroke. Static, measured.
55. Hovering a Top or For you pill turns its stroke accent and scales it to 1.02 over ~167 ms. Hover + frames.
56. Every For you and Genres cell shows an 8x8 rounded-square colour pip (not a circle). Static, zoomed.
57. The Genres band is 3 columns above a 720-DIP body width, 2 between 380 and 720, 1 below. Verify at each.
58. The Genres band is alphabetised; the Top band is **not** (Music, Podcasts, Audiobooks, Live Events in server order). Static.
59. Mood bars are 52 DIP cards with a 3-DIP left tick and a right-edge radial wash at ~20% opacity - not a saturated field.
    Static, both themes.
60. Hovering a mood bar brightens the wash to ~34% and swaps the plate fill, **without** changing its shadow. Hover, measured.
61. More cards are 88 DIP with an 80-DIP cover hanging past the bottom-right corner at −8°, clipped by the card. Static.
62. Hovering a More card slides its cover left/down and rotates it toward upright over ~250 ms. Hover + frames.
63. More is capped at 4 columns no matter how wide the window. Verify @2560.
64. The Charts band carries the fold deck's own header (with a drill chevron) and **no** separate "Charts" eyebrow above it.
    Static.
65. Charts fold tiles carry no per-tile eyebrow on Browse (Home's Charts row does). Verify: compare Browse and Home. Static.
66. A Charts outage shows a compact empty sentence inside the band while the other five bands render normally. Verify: with the
    Charts section unavailable in `--fake`.
67. A categories outage shows the page error state **with a Retry button** that re-fetches. Verify: offline, then online + Retry.
68. Clicking "Live Events" opens the Concerts hub; no other client feature navigates anywhere. Verify.
69. Returning to Browse after visiting eight other pages paints full content immediately and restores the scroll offset - it does
    not shimmer. Verify: scroll to More, navigate away 8 times, come back; frames.

**Browse - category page**

70. The masthead reads `Browse › <Title>` with the parent crumb dimmed and clickable. Static.
71. The title is correct from the **first frame** of the page - it never shimmers or appears late. Frames from the click.
72. The body shimmer is a Related block plus two shelf bands of six 148x220 cards - never an empty pane and never a reflow when
    the real width lands. Frames.
73. A page whose single shelf is untitled (or titled like the page) renders as one uniform grid with a "Show all" button in the
    masthead; a page with distinctly-titled shelves renders as carousels with per-shelf headers. Verify one of each.
74. "Show all" pages the grid **in place** - the route does not change and the scroll position is kept. Verify: click it; frames.
75. "Show all" is disabled while a page is in flight and disappears when the section is exhausted. Frames.
76. Scrolling to the tail of a flattened grid auto-loads the next page once, then requires a fresh scroll. Frames.
77. An untitled shelf in Shelves mode renders with no header row and no header gap. Static, measured.
78. Card covers in a flattened grid are square and unsheared at every window height. Verify @720 and @1440 window height. Static.
79. "Explore all categories" appears on every mode including the empty and error states, in accent ink at the Caption rung.
    Static.
80. An unavailable category shows "This category isn't available right now" plus the Explore link, inside a scrollable frame.
    Verify with a bad page uri.
81. Revisiting a category page drilled into earlier in the session paints its last content instantly rather than shimmering.
    Verify: visit A, visit 16 others, return to A; frames.
82. A genre opened from a search result shows the trail `"<query>" › <Genre>` - **not** `"<query>" › Browse › <Genre>`. Verify:
    search a genre term, click the genre tile. Static.
83. The same genre opened from the Browse directory shows `Browse › <Genre>`. Static.

**States the first pass of this chapter did not cover** (added by the audit - verify each)

84. **Track artwork off.** Settings → hide track artwork, then search: every TRACK hit row loses its 48-DIP art and keeps
    its 64-DIP height; artist / album / playlist / podcast rows keep theirs. Verify on the All tab, the Songs tab and the
    Best-matches shelf. Static, both states.
85. Toggling that setting while a search page is open re-renders the rows immediately (the `AppearancePrefs.Epoch` edge) and
    does not scroll-jump. Frames.
86. A playlist hit whose cover is a 4-tile mosaic renders a 2x2 mosaic in the hit row, in the hero, in the playlist rail and
    in the suggestion popup - one cover in all four, never a grey box. Static.
87. A search hit with no image at all shows the seeded gradient placeholder, not an empty square. Static.
88. Hovering a hit row's cover reveals a 30-DIP play FAB (44 on the `large` hero row); navigating back so a row mounts under
    a stationary cursor reveals **nothing** until the pointer actually moves. Frames.
89. A genre / profile / author hero has **no `...` button** and right-clicking it does nothing; an episode hero likewise.
    Verify all four.
90. Right-clicking a podcast or audiobook hit gives the SHOW arm (Play · Open · Pin · Share) - not Play next / Play after /
    Add to playlist. Verify against an album hit side by side.
91. In the suggestion popup, a genre row has no `...` button but still opens a card menu on right-click. Verify.
92. A suggestion row whose server subtitle is absent falls back to its type label rather than rendering one line. Static.
93. The Songs / Albums / Playlists tabs' own empty state (reachable when another list has rows) shows the headline with **no**
    caption - today's behaviour; 0.3 should show the captioned one (§9 gap 1). Verify whichever 0.3 ships.
94. The Genres section on the All tab shows eight shimmer link cells at the live column count while its own fetch is in
    flight, then swaps without a reflow. Frames.
95. A failed Genres section shows a compact error inside the section; a failed Related-searches section shows **nothing**
    (today). Verify both with the network off.
96. Best matches with only four hits renders a 2-column page at a width that would allow three. Static, measured.
97. **Browse: a Charts outage shows a retry button inside the Charts band**, and pressing it refetches only the deck - the
    other five bands never flicker. Verify.
98. A More card whose category has no artwork renders as a plain 88-DIP plate with wash + tick and no hanging cover, and
    nothing animates on hover. Verify: the loading directory's More band is exactly this case. Static + hover.
99. The loading directory's More band shows three cards; the loaded one shows however many browseAll left unmapped - the
    only band whose height may shift. Measure before/after.
100. Neither "Charts" nor "Podcast Charts" ever appears as a category TILE in any band. Static.
101. An untitled Related / CategoryGrid block renders as a bare link grid with no header row and no header gap, and a
     Related header is never clickable (no chevron). Static.
102. A "Show all" page that fails leaves the loaded cards on screen with the button still armed and shows no error; one that
     returns an empty page removes the button permanently. Verify both.
103. Every Browse cell takes a visible focus ring at a 2-DIP margin under keyboard traversal; the Search page's facet tabs and
     hit rows do not (today). Verify - and fix per §9 gap 3.

---

## 11. Audit log

Adversarial re-read of the 0.2.9 sources against this chapter (2026-09-12). Every line below is one correction; the
evidence is the `file:line` already carried in the section that was edited. Sources re-read independently:
`Features/Search/*.cs` (all 7), `Features/Browse/*.cs` (all 12), `Features/Shell/ShellToolbar.cs:146-617`,
`Design/SearchHighlight.cs`, plus `Components/MediaCard.cs`, `Design/AppearancePrefs.cs`, `Design/Surfaces.cs`,
`Design/WaveeCta.cs`, `Design/WaveeMotion.cs`, `Actions/Menus.cs`, `Features/Shell/NavRouteNormalizer.cs`,
`Features/Shell/ShellMastheadBand.cs`, `Wavee.Core/Library/Library.cs`, `Wavee.Core/Library/Browse.cs`,
`assets/loc/en-US.json`, the five named test files, and the engine's `Interaction.cs` / `LazyGrid.cs` /
`MotionRecipes.cs` / `SkeletonRegion.cs` / `Expressive.cs` / `AutoSuggestBox.cs`.

| # | kind | section | correction |
|---|---|---|---|
| 1 | wrong | §0.15, W8, §3 | Search-genre pips are **always** `Tok.AccentDefault`: `SearchGenreTiles.Columns` builds its `BrowseTileModel` with `Color: null` (`SearchGenreTiles.cs:81`) and `SearchGenre.Accent` is discarded. The chapter claimed `WaveePalette.ToColor(g.Accent)`. |
| 2 | overclaim | §0.8 | "every dedicated facet list" routes through `HitRow` — Artists uses `ResultRow` (`SearchPage.cs:420`) and Genres uses `BrowseTiles.Link` (`:409-410`). Both exceptions are now named in the non-negotiable itself, not only in W10. |
| 3 | wrong | W9, §3 | The audiobook stacked layout is conditional: `belowArt = detailBelowArt && (hasMeta \|\| hasDetail)` (`MediaCard.cs:971`). An audiobook hit with neither is an ordinary 64-DIP row. |
| 4 | wrong | §8 | `SearchResults.TotalFor`'s `-1` means "no server total, use the local Count" — it does **not** encode not-queried vs queried-empty. Line range corrected to `Library.cs:92-124`. |
| 5 | missing | §0.8, §3, §6.1, W5, W9, §7, §10 | The `WaveeSettings.HideTrackArtwork` setting removes the 48-DIP art from TRACK hit rows only (`SearchPage.cs:784`), is read at four sites through `AppearancePrefs` (`:660, 922, 980, 1041`), and is baked into the Best-matches shelf `Key` (`:1022`). The chapter had no settings branch at all. |
| 6 | missing | W22, §10 | The Charts band's own failed arm is `ErrorState.Build(err, onRetry: charts.Refresh)` (`BrowseDirectory.cs:276`) — Browse has **two** retry buttons, not "the one retry button on this surface". |
| 7 | missing | W11 | Three more empty states: `SongsGrid` / `AlbumGrid` / `PlaylistGrid` each build `EmptyState.Build(NoResults)` with **no** subtitle (`SearchPage.cs:525, 538, 544`), distinct from the captioned generic arm at `:412-413`. |
| 8 | missing | W11 | Two section-level failures on the All tab: Genres → `ErrorState.Compact` (`SearchGenreTiles.cs:50`); Related searches → silent `new BoxEl()` (`:123`), which also renders nothing while pending (`:119`). |
| 9 | missing | W4, §6.1 | The hero's `...` exists only when a menu resolved (`SearchHero.cs:72`), so Genre / User / Author / Episode heroes have none — and the card menu's grammar is kind-dependent, with Podcast/Audiobook taking the SHOW arm (`Menus.cs:552`). W4's action table gained a `...` column. |
| 10 | missing | W17, W15, W16, §10 | `PeekArt` is appended only when `m.Artwork is not null` (`BrowseTiles.cs:166`), and every directory SEED carries `Artwork: null` — so the loading More band has no hanging covers. Also recorded: `hang = Peek * 0.3f`, and `cardW = width / cols` with gaps not subtracted (`BrowseDirectory.cs:312`). |
| 11 | missing | W5 | `maxColumns = min(cols, ceil(n / rows))` (`SearchPage.cs:998`) — a short hit list narrows the Best-matches page below the nominal column count. |
| 12 | missing | W13, W14, §3 | Popup details: the anchor-width fallback is **720**, not 400 (`ShellToolbar.cs:369`); a subtitle-less rich row falls back to its type label (`:514`); right-click is on every row while `...` needs `canPlay` (`:477-478, 520-522`); a no-match query row is one plain primary run (`:594-598`). |
| 13 | missing | W13, §7, §9 | `SearchSuggestionItem.IsExplicit` (`Library.cs:15`) is carried and never rendered — the popup has no explicit badge. |
| 14 | missing | W9, §3, §5 | `MediaCard.Row`'s hover play FAB (30 small / 44 large) and its `HoverMotionGate` arming rule were absent; so was the detail row's padding change to `Edges4.All(8)` (`MediaCard.cs:1044-1046`) and the text-column gap pair (2 / 8). |
| 15 | missing | W9 | Mosaic covers: `Surfaces.Artwork` paints a 2x2 mosaic for a 4+-tile playlist cover and collapses 1-3 tiles to the first (`Surfaces.cs:241-245`) — a state every image slot on both surfaces can hit. |
| 16 | missing | W15, §10 | The two Charts-mapped categories never render as tiles: `Body` swallows the Charts bucket and injects the Fold deck (`BrowseDirectory.cs:130`). |
| 17 | missing | W19 | An untitled `CategoryBlock` renders as the bare `LinkGrid` with no header (`BrowsePage.cs:687`), and a Related/CategoryGrid header is `DrillHeader(title, null)` — a label, never a drill (`:691`). |
| 18 | missing | W20, §8 | `BrowsePageLayout`'s two-shelf rule is stricter than the one-shelf rule (`IsBlank` vs `TitleIsRedundant`, `:43, 49, 68`), only Shelf sections are counted (`:37`), and empties die per-kind (`:23`). |
| 19 | missing | W22 | The category page's section-paging failure is **invisible**: loaded cards stay, the button stays armed, `browse.section.page.fail` is logged (`BrowsePage.cs:530-541`); an empty answer disarms the section permanently (`:542-545`). |
| 20 | missing | W8 | The All-tab Genres section's own loading shape: eight named seed cells through the real `Link` cell (`SearchGenreTiles.cs:87-97`), `smoothResize: false`, non-zero shimmer height. |
| 21 | missing | W1 | The Genres and Related-searches headers are `SearchChrome.TickHeader`s (a 3x14 accent tick + a 20/28 label), not bare labels — the wireframe omitted the tick. |
| 22 | missing | §5 | Six motion rows: the hero CTA pills' `ScaleStandard` tier (1.04 / 0.96), the popup icon buttons' `ScaleEmphatic` (1.07 / 0.92), the row FAB reveal, the `HoverMotionGate` no-op on a stationary mount, and the masthead "Show all" disabled (not spinning) state. |
| 23 | missing | §6.1, §9 | Nothing on the Search page sets `Focusable = true` while every Browse cell does — recorded as a gap with the evidence, not as a spec. |
| 24 | missing | §8 | `BrowseLayout`'s constant list was missing `MastheadReserve` and `Frame(bottom)`; `BarColumns` has no cap (unlike `MoreColumns`); `StarGrid` returns a bare box for zero cells. `BrowseTiles.ToModel` added as a pure rule with its `ToModelNoop` arm. |
| 25 | missing | §9 | New subsection "0.2.9 gaps to FIX in 0.3" collecting nine thin spots (split empties, silent failure, focusability, automation names, unrendered `IsExplicit`, the dead `search.noGenreResults` key, hardcoded "Music video"/"Song", the thin Artists row, the terminal search error). |
| 26 | missing | §10 | Twenty new parity items (84-103) covering the states above. |
| 27 | **arbitration** | header · §1.2 · §9 #1 · §9 budget · §9 missing-files | arbitration 2026-09-12: **`Entities/Browse.Page.cs` is settled** - the file this chapter asked for is in plan section 2's tree with owner **P**, at ~700 lines for the 857 lines of 0.2.9 that have no home today (`BrowsePage` 730 + `BrowseDirectoryPage` 67 + `BrowsePageHost` 60, re-counted from source). Four places restated from a request into a decision: the header's 0.3 target, §1.2's `BrowseDirectoryPage` row, §9 item 1 and the missing-files bullet; the budget's surface total moves **2,200 → 2,900** because the plan column now counts the file. Scope recorded with it: `Browse.Page.cs` owns the directory page, the category page and the page host, **not** the `browse-section:` drill, which is `Home.SectionPage` in `Entities/Home.Page.cs` (A15, chapter 12) - one class for both sources. No pixel, token, geometry, motion or rule changed. |

**Verified correct, left untouched** (checked against source, no edit needed): the 11 skeleton pill widths and
`SearchChipSkeletonPolicy`'s gate; the 260 ms / `Sx 0` / `TransformOriginX 0` underline; `_slideArmed` and the
`PageSlideForward`/`Back` ±8 DIP / 250 ms / `SmoothOut` pair; the three shimmer geometries and every box inside them;
the hero's 228 / 300x260 / −2.5° / (+40, −16) geometry and its `(0.88, 0.40)` / `(1.2, 0.8)` / 0.55→0 radial;
`SearchHitsGrid`'s `clamp(floor((w+12)/292),1,3)` with a 24-DIP hysteresis (`DetailLayoutBreakpoints.TierHysteresisDip`)
and the 128-DIP audiobook reservation; `SearchFacetGrid`'s `(w+16)/196` columns, `max(90, …)` cell width, `+50` chrome,
`+70` row extra, page-0 seed and placeholder cell; `BrowseLayout`'s 720/380 link bands, 132 bar floor, 168/cap-4 more
grid, 36/32 chip heights, 8-DIP pip, 0.62 copy fraction; the Bar/Peek wash anchors and the 0.20/0.34 opacity pair;
`Elevation.Card` with no hover lift; the `WaveeEntrance` cascade (40 ms, cap 8, 400 ms `Expressive.Slow`, 8 DIP,
2 px blur, opacity-only); `ClipBelow(84)` + the 24-DIP `EdgeFade` armed on the engage edge and the spacer-not-padding
reserve; `BrowseMastheadMetrics`' 52 / 84 / 100; the taxonomy's 4 / 10 / 25 / 14 band counts and the seeds mirroring
them; `BrowseDirectoryStore`'s 15-minute TTL and `BrowsePageStore`'s 16 FIFO slots; `NavRouteNormalizer`'s blank-search
rewrite; every popup metric (40 / 58 / 44 / 22 / 5 / 28 / 9,2 / corner 10 / divider margins / 6 and 10 caps / 560 max
height / 150 ms debounce via `AutoSuggestBox.TextChangedDebounceMs`); the whole `OmnibarSuggestQuery` state machine;
`SearchHighlight`'s pill geometry and line-box table (and that it is not used by the Search page); and all five test
files named in §8, at the line counts and class names given.
