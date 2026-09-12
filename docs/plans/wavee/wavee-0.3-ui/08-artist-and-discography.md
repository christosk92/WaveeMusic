# Artist page and discography page — 0.3 visual fidelity contract

> **0.2.9 sources** (all `src/apps/Wavee/…`; after Wave 0 the same relative paths under `src/apps/_old/Wavee/…`):
> `Features/Detail/ArtistPage.cs` 353 · `ArtistPage.Hero.cs` 366 · `ArtistPage.TopTracks.cs` 335 ·
> `ArtistPage.AlbumExpand.cs` 879 · `ArtistPage.Shelves.cs` 233 · `ArtistPage.Biography.cs` 136 ·
> `ArtistPage.Sections.cs` 55 · `ArtistPage.Discography.cs` 38 · `ArtistCompactBar.cs` 108 ·
> `ArtistPopular.cs` 669 · `ArtistPopularLayout.cs` 78 · `ArtistHeroLayout.cs` 117 ·
> `ArtistGalleryLightbox.cs` 408 · `ArtistFacePile.cs` 271 · `DiscographyPage.cs` 174 ·
> `DiscographyEraBands.cs` 218 · `AlbumDrawerVerdict.cs` 48 — **4,215 lines artist-owned** + 271 (`ArtistFacePile`,
> which this surface does **not** use — see §1.0) + the shared `ContextBand.cs` 398 / `ContextBandLayout.cs` 179
> (**shared, and NOT authored here**: both land in `Entities/Detail.UI.cs` / `Entities/Detail.cs` in Wave 4 and this
> page consumes them — §1.2's ownership note).
> **0.3 target**: `Entities/Artist.cs` (columns + handle), `Entities/Artist.UI.cs` (leaves: hero, chart row, pick,
> banners, shelves, drawer panel), `Entities/Artist.Page.cs` (`ArtistPage`), **plus
> `Entities/Artist.Discography.cs`** (1,300 lines, settled in plan §2/§9.5) — the discography facet page, the
> virtualized grid, the album drawer and the era bands, and in the route table (`disco:`, §4.11). **Wave 5, owner N.**

---

## 0. The non-negotiables

Checkable statements. Each is what a user notices first if it is lost.

1. **The photograph is the hero and it is full-bleed.** `HeaderImage ?? Image` fills the entire hero box at
   `ImageFit.Cover`, `FocusX 0.62 / FocusY 0.34`, pre-scaled to `1.05` at rest inside a `1.08` inner frame lifted
   `-4%` (`ArtistPage.Hero.cs:286-289, :345-362`). There is **no avatar** on this page and no framed portrait card.
   The v2 "Marquee Plate" design doc (a 208² framed portrait) was never built — see §9 drift.
2. **Wide/Medium set the copy ON the picture behind a horizontal veil; Compact/Narrow put the picture on top and the
   copy below it on the page surface.** The switch is `ArtistHeroVeilAxis` (`ArtistHeroLayout.cs:85-89`), never a
   scrim tweak. On stacked tiers there is no veil at all, because no type sits on the photo.
3. **The name is display type, not a UI label**: 84/96/700 at Wide, 48/60/700 at Medium, 32/40/700 at
   Compact+Narrow, all "Segoe UI Variable Display" with negative tracking and a real `MinSize` auto-fit window
   (`WaveeType.cs:155-187`). Two lines max at every tier.
4. **Scroll collapses the hero into a 56-DIP text-chrome band with a section PIVOT in it** — title · pivot ·
   Play/Follow as words, one hairline, **no fill, no avatar, no capsules** (`ArtistCompactBar.cs`,
   `ContextBand.cs:71-96`). The band paints nothing: the magazine is *clipped* at its lower edge and the page's own
   ground (shell Mica + the blend wash) shows through the 56 DIP.
5. **The active pivot item carries a 2-DIP accent underline and nothing else does.** The underline is always mounted
   and changes colour (`ContextPivot.Link`, `ContextBand.cs:288-315`); the spy activates a section at the upper
   quarter of the usable viewport (`ContextBandLayout.SpyLine`).
6. **Top tracks is a two-column paged CHART, capped at 5 rows, that pages instead of growing** — a `PagedShelf` with
   page-mandatory snapping, a `‹ ●● ›` pip pager in the header, and rows keyed by track URI so a page flip *slides*
   the realized window instead of remounting five rows (`ArtistPopular.cs:16-46, :157-196`). The realized cell's key is
   **not** the uri alone: it is `"row:" + t.Uri + ("|art" | "|noart") + ("|classic" | "|modern")`
   (`ArtistPopular.cs:246`) — the two appearance settings below are baked in because they change the row's CHILD COUNT
   and its height, not just its skin. An empty uri (a local/synthetic track) falls back to `"chart#" + i` in `RowKey`.
7. **Clicking a discography card opens the album's tracks in place**, full width, directly after that card's row,
   with a 16×8 caret pointing at the clicked card and a 2-DIP accent border on it; the clicked row is always parked
   under the sticky facet header (`ExpandedReveal.AlignTop`). One 200 ms open, no shimmer for a warm album
   (`ArtistPage.AlbumExpand.cs:398-687`).
8. **The page's accent is the cover's, everywhere it is spent**: the Play capsule, the section rules, the facet
   spine, the selection pill, the city bars, the pivot underline — all `WaveePalette.ChromeAccent(ChromeSchemeFor(…))`
   with `Tok.AccentDefault` only as the greyscale fallback (`ArtistPage.cs:36-37, :195-196`).
9. **One artist-tinted wash bleeds down from the hero and stops.** `CoverArtistBlendWash`: light `Lift(Accent)` @
   0.20 → 0.06 → 0; dark `BackgroundDark` @ 0.30 → 0.08 → 0, over `heroHeight + 96` with the boundary stop at
   `h/(h+96)` (`CoverPaletteLeaves.cs:171-196`).
10. **Every section is keyed, appears in a fixed order, and only a DESTINATION joins the pivot.** The two banners
    (latest release, tour) are deliberately excluded from the pivot (`ArtistPage.cs:222-233`).
11. **Sections are dividers + accent-ruled headers, never cards.** Only objects get chrome. The one exception is the
    biography/profile-facts band, whose two columns are genuine object panels.
12. **The Artist pick owns the rail beside Top tracks when it exists**, and an upcoming release then moves to its own
    quiet full-width band above Latest release — never two stacked cards in the rail
    (`ArtistPage.cs:238-243`, `ArtistPage.TopTracks.cs:74-104`).
13. **Play counts never disappear and never squeeze the "feat." credit.** The row always renders the compact form
    (`7.7M`) with the exact count in a tooltip; under 340 DIP of cell width the subtitle stacks onto a third line
    (`ArtistPopular.cs:394-407`, `ArtistPopularLayout.cs:20-29`).
14. **The gallery is a modal, full-window lightbox** with auto-hiding floating chrome (2600 ms), a filmstrip, a
    FlipView pager and a real pan/zoom mode that *replaces* the pager subtree (`ArtistGalleryLightbox.cs`).
15. **Nothing pops in.** The whole magazine renders from one Ready model behind `Skel.Region(..., FadeOnly)`; the
    derived shimmer is the real `Body` rendered against a representative seed, so the skeleton has the page's exact
    geometry (`ArtistPage.cs:132-141, :179-186`). Where 0.2.9 still violates this (extras arriving after the first
    Ready render; the chart growing 10→50; play counts landing late) — see §7, which is the part 0.3 must fix.

---

## 1. Anatomy

### 1.0 What is NOT on this page

`ArtistFacePile.cs` (271 lines) is the **album** header's billed-artist control (`DetailRail.cs:194, :401`). It is in
this chapter's source list but belongs to `03-detail-frame.md` / `05-album.md`. Its constants (`Avatar 28`, `Ring 2`,
`Outer 32`, `Overlap 12`, `MaxVisible 4`, flyout `280 × ≤360`, rows `44`) are recorded here only so they are not lost:
port them verbatim into `Album.UI.cs` alongside `Components/FacePiles.cs`.
`ArtistExtras.WatchFeed` is decoded and stored but **never rendered** by the artist page (verified: no `WatchFeed`
reference in any `Features/**` file).
`Components/ConcertUi.cs` is in this chapter's source list but **this surface never calls it** (verified: no `ConcertUi.`
reference anywhere under `Features/Detail/`). The artist page's "Upcoming concerts" shelf rolls its own
`ArtistPage.ConcertStub` (`Shelves.cs:108-136`) — which is exactly the duplication §9 asks `Concert.UI.cs` to absorb.
`MerchItem.ShopUrl` is decoded and carried into `MerchCard` but **never used**: the merch card has no `OnClick`, no
menu and no drag (`Shelves.cs:152-165`). It is the one inert card on the page.

### 1.1 The 0.2.9 composition

```
ContentHost.ArtistHost(route)                                    ContentHost.cs:164
└─ ArtistPage : Component                                        ArtistPage.cs:20        route-reactive page root
   ├─ [fields] _tintOwner, _anchors (SectionAnchors), _anchorsRoute,
   │           _paletteAccent, _acts, _menuOverlay,
   │           _heroWidth : Signal<float>(880)                   ArtistPage.Hero.cs:20
   │           _topBandWide : bool (latched)                     ArtistPage.TopTracks.cs:21
   │           _biographyMode : int (latched)                    ArtistPage.Biography.cs:19
   ├─ Ctx.Provide(LazyScroll.Slot, pageScroll)                   ArtistPage.cs:172   page scroll → in-page LazyGrids
   └─ BoxEl "artist-page:{route}"  Direction 1, Grow 1           ArtistPage.cs:172
      ├─ CoverPaletteLeaves.ShellTint(…)  key artist-tint:{route} ArtistPage.cs:102   0-size leaf; publishes shell material
      └─ ScrollView  key artist-scroll:{route}, ScrollKey route   ArtistPage.cs:133   OnRealized → _anchors.Viewport
         └─ SkelRegionEl (reveal FadeOnly, group route)          ArtistPage.cs:133   Pending → shimmer derived from Body(seed)
            └─ Body(a, fans, svc, go, bridge, …)                 ArtistPage.cs:188
               └─ BoxEl ZStack                                   ArtistPage.cs:329
                  ├─ BoxEl "artist-wash-clip" .ClipBelow(56)     ArtistPage.cs:334
                  │  └─ CoverArtistBlendWash key artist-wash:{uri} ArtistPage.cs:295 / CoverPaletteLeaves.cs:171
                  └─ BoxEl Direction 1                           ArtistPage.cs:341
                     ├─ Banner(...)  = the HERO                  ArtistPage.Hero.cs:22
                     │  └─ BoxEl ZStack H=height .Collapse(h,56,cd) ArtistPage.Hero.cs:189
                     │     ├─ expandedPresentation (2 arms)      ArtistPage.Hero.cs:129-180
                     │     │  ├─ media BoxEl .StretchFromTop().ParallaxY(.15,photoH) ArtistPage.Hero.cs:103
                     │     │  │  └─ HeroArt(url,_heroWidth,blurHash) key heroart:{url} ArtistPage.Hero.cs:285
                     │     │  ├─ CoverKeyedVeil key artist-veil:{uri}  (horizontal arms only) ArtistPage.Hero.cs:175
                     │     │  └─ copy / identity band            ArtistPage.Hero.cs:144-165
                     │     │     └─ Identity()                   ArtistPage.Hero.cs:34
                     │     │        ├─ verified row (InfoBadge.Icon + Caption) ArtistPage.Hero.cs:36-46
                     │     │        ├─ name TextEl (3 tiers)     ArtistPage.Hero.cs:48-60
                     │     │        ├─ bio = FirstSentence(a.Bio) ArtistPage.Hero.cs:62-72
                     │     │        ├─ HeroMeta(rank·monthly·followers) ArtistPage.Hero.cs:236
                     │     │        └─ HeroActions(Play·Shuffle·Follow·Radio) ArtistPage.Hero.cs:200
                     │     └─ ArtistCompactBar.Build(...).Skeletonized(false) ArtistPage.Hero.cs:186
                     │        └─ BoxEl ZStack H=56 .Reveal(cd-44, cd, 4) ArtistCompactBar.cs:96
                     │           ├─ ContextBand.Row(rowW, gutter, …)      ArtistCompactBar.cs:78 / ContextBand.cs:145
                     │           │  ├─ title (ContextBand.Title, cap 280) ArtistCompactBar.cs:45
                     │           │  ├─ ContextPivot key artist-pivot:{uri} ArtistCompactBar.cs:51 / ContextBand.cs:190
                     │           │  └─ actions: TextAction(Play, primary) + FollowTextAction ArtistCompactBar.cs:64
                     │           └─ ContextBand.HairlineOverlay(width)    ContextBand.cs:130
                     ├─ sentinel BoxEl H=0 .Sticky(56, → compactInteractive) ArtistPage.cs:288
                     └─ magazine BoxEl "artist-under-band" .ClipBelow(56) ArtistPage.cs:313
                        ├─ divider BoxEl H=1 StrokeDividerDefault          ArtistPage.cs:325
                        └─ centering row → inner BoxEl (Gap 32, MaxW 1600) ArtistPage.cs:271
                           ├─ sec:popular      (ContextBand.Anchor)  TopBand(...)          ArtistPage.TopTracks.cs:34
                           │  ├─ Responsive.Of(w ⇒ …, fallback 900)                        ArtistPage.TopTracks.cs:41
                           │  ├─ ArtistPopular (chart)                                     ArtistPopular.cs:47
                           │  │  └─ PagedShelf.Create(rows 5, maxCols 2, snap Page)        ArtistPopular.cs:157
                           │  │     ├─ header Surfaces.AccentHeader("Top tracks", accent)  ArtistPopular.cs:161
                           │  │     ├─ customPager ChartPager (chevrons + PipsPager)       ArtistPopular.cs:528
                           │  │     └─ cell: BoxEl ZStack [ChartRow, SelectionPill]        ArtistPopular.cs:258
                           │  │        └─ ChartRow → Row(...)                              ArtistPopular.cs:365
                           │  │           ├─ TrackRow.NumberCell 24×24                     ArtistPopular.cs:417
                           │  │           ├─ art 44/40 r4                                  ArtistPopular.cs:424
                           │  │           ├─ mid: title + sub(E·▤·feat·plays)[+plays line] ArtistPopular.cs:491
                           │  │           └─ trail: TrackRow.Heart 28 + duration 13        ArtistPopular.cs:409
                           │  └─ FeaturedColumn → MediaCard.ArtistPick | Section(Upcoming) ArtistPage.TopTracks.cs:78
                           ├─ sec:upcoming        Section("Upcoming", UpcomingCard(wide:false)) ArtistPage.cs:242
                           ├─ sec:latest-release  Section("Latest release", LatestReleaseBanner) ArtistPage.cs:247
                           ├─ sec:albums        ┐ ContextBand.Anchor → DiscographySection     ArtistPage.cs:250-258
                           ├─ sec:singles       ├─ Embed.Comp(Props(items), () => new DiscographySection(kind,…))
                           ├─ sec:compilations  ┘   └─ Expander(InitiallyExpanded, AnimateContentResize:false) AlbumExpand.cs:764
                           │                          ├─ header: spine 3×22 + FacetHeaderLabel  AlbumExpand.cs:802
                           │                          └─ grid .ClipBelow(96) → DiscoGrid        AlbumExpand.cs:746
                           │                             └─ LazyGrid(minCol 180, gap 16, rowExtra 70) AlbumExpand.cs:503
                           │                                ├─ cell: MediaCard.GridCard          AlbumExpand.cs:529
                           │                                └─ drawer "disco-drawer"             AlbumExpand.cs:616
                           │                                   ├─ AlbumDrawerPanel key drawer:{uri} AlbumExpand.cs:90
                           │                                   └─ caret PathEl + PolylineStrokeEl AlbumExpand.cs:653
                           ├─ sec:appears-on    PagedShelf(measured) MediaCard.Shelf   ArtistPage.Discography.cs:21
                           ├─ sec:tour          TourBannerCard                         ArtistPage.Shelves.cs:27
                           ├─ sec:music-videos  PagedShelf MediaCard.VideoCard (≤16)   ArtistPage.Shelves.cs:58
                           ├─ sec:playlists     PagedShelf MediaCard.Shelf (≤16)       ArtistPage.Shelves.cs:77
                           ├─ sec:concerts      PagedShelf ConcertStub (≤12)           ArtistPage.Shelves.cs:94
                           ├─ sec:merch         PagedShelf MerchCard (≤12)             ArtistPage.Shelves.cs:139
                           ├─ sec:biography     BiographyBand (Responsive, 2 columns)  ArtistPage.Biography.cs:30
                           │  ├─ left card: AccentHeader + RichText + link pills + city bars
                           │  └─ right col : AccentHeader("Profile facts") + StatTile wrap
                           ├─ sec:gallery       PagedShelf square tiles (≤16) → OpenGallery ArtistPage.Shelves.cs:168
                           │                     └─ ArtistGalleryLightbox (modal overlay)   ArtistGalleryLightbox.cs:25
                           └─ sec:related | sec:fans  PagedShelf MediaCard.Shelf(circular) ArtistPage.Shelves.cs:200/217

DiscographyPage : Component                                      DiscographyPage.cs:50   route "disco:{kind}:{uri}"
└─ ScrollView (ScrollKey route)                                  DiscographyPage.cs:144
   └─ content BoxEl (Gap 32, Pad 36/24/36/108)                   DiscographyPage.cs:129
      ├─ breadcrumb column                                       DiscographyPage.cs:106
      │  ├─ BreadcrumbBar [artistName, FacetTitle]               DiscographyPage.cs:111
      │  ├─ WaveeType.PageHero(FacetTitle)                       DiscographyPage.cs:114
      │  └─ SelectorBar [Albums, Singles, Compilations]          DiscographyPage.cs:120
      └─ DiscoGrid key disco-grid:{route}  (same grid + drawer)  DiscographyPage.cs:140
```

### 1.2 The same tree in 0.3 terms

| 0.2.9 node | 0.3 home | Shape | Inputs (and how data reaches it) |
|---|---|---|---|
| `ArtistPage` | `Artist.Page.cs` → `Artist.Page : Component` | Component | ctor `Artist a` (a handle = an int slot; stable for the page's life). `UseSignal(Entities.Current.Artists.Changed)` re-renders the page on a table publish. `UseEffect` on mount demands the whole model (§7). |
| route reactivity (`Signal<Route>`) | **gone** | — | `Shell` mounts one `Artist.Page` per keep-alive slot keyed by `(tab, route)`; artist→artist is a new slot, so there is no route signal inside the page. Keep `ScrollKey = uri`. |
| `_heroWidth : Signal<float>` | keep | `Signal<float>` on the page | written from `OnBoundsChanged` with the 0.5-DIP threshold. **Signal**, because `HeroArt` and the wash read it. |
| `Banner` / `Identity` / `HeroActions` / `HeroMeta` | `Artist.UI.cs` → `static Element Hero(Artist a, IReadSignal<float> width, …)` | static functions | handle + width signal. Pure over the handle; re-rendered by the page. |
| `HeroArt` | `Artist.UI.cs` → `sealed class HeroArt : Component` | Component | ctor: `StringId url`, `IReadSignal<float> width`, `string? blurHash`. Keeps `UseRef` decode latch + `UseKeyframes`. **Key on the url** (props freeze). |
| `ArtistCompactBar` | `Artist.UI.cs` → `static Element ContextBar(Artist a, …)` | static | pivot array is a **value** recomputed per render; `ContextPivot` keeps re-pushed `Props` (the set grows). |
| `SectionAnchors` / `ContextPivot` / `ContextBand` | **`Entities/Detail.UI.cs`** (the shared UI half — `Detail.Band` / `Detail.BandTitle` / `Detail.BandByline` / `Detail.BandAnchor` / `Detail.Pivot`), with `ContextBandLayout` as a CORE section of `Entities/Detail.cs`. **NOT `Shell/Shell.UI.cs`** | unchanged | per `03-detail-frame.md:151` ("keep in `Detail.UI.cs` · `Detail.Band(...)`, and let `Artist.Page` call the same three helpers") and `:880`. Owner N **consumes** these; N writes none of them — see the ownership note under this table. The page still owns the `SectionAnchors` instance and still **resets it from Render**, not from an effect (`ContextBand.cs:39-44`, called at `ArtistPage.cs:116`). |
| `ArtistPopular` | `Artist.UI.cs` → `sealed class Popular : Component` | Component | ctor `Artist a` only. The chart list is `a.PopularSlots` (`Edges.ArtistPopular.Targets`) read **live** every render — no frozen `IReadOnlyList<Track>`, so the seed→extended growth that forces the `"chart:{total}:{counted}:…"` remount key today disappears (§9). |
| `ChartRow` | nested `Popular.Row : Component` | Component | props `(int Index, ArtistPopularLayout.Tier Tier)`; the track is `Entities.Current.Edges.ArtistPopular.Targets(a.Slot)[Index]` read live; `UseSignal(Tracks.Changed)` + a `Version` compare re-skins one row. |
| `ChartPager` | nested | Component | re-pushed `Props(page, pageCount, …)` unchanged. |
| `FeaturedColumn` / `ArtistPick` / `UpcomingCard` / `LatestReleaseBanner` | `Artist.UI.cs` static functions | static | `Artist a` + `Album latest` (a slot) + the pick row. `PreSaveButton` / `PreReleaseCountdown` stay Components **keyed on the uri** (props freeze). |
| `DiscographySection` | `Artist.Discography.cs` → `sealed class Facet : Component` | Component | ctor `(Artist a, DiscoKind kind)`; the item span is `Edges.ArtistAlbums/Singles/Compilations.Targets(a.Slot)` read live. The `VirtualCollection`, its snapshot key and `ReplaceSnapshot` **all disappear** — the edge table already versions the list. |
| `DiscoGrid` | `Artist.Discography.cs` → `sealed class Grid : Component` | Component | ctor `(ReadOnlySpan<int> source is illegal — pass the parent slot + an edge id)`, `Signal<StringId> expandedUri`. Keep `LazyGrid` and its `expanded`/`drawer`/`drawerHeight` contract verbatim. |
| `AlbumDrawerPanel` | `Artist.Discography.cs` → `sealed class Drawer : Component` | Component | re-pushed `Props(Album thin, DrawerVerdict verdict)`; the track list is `Edges.AlbumTracks.Targets(album.Slot)` read live. `Key = "drawer:" + albumUri` stays (per-album `SelectionModel`/`SwipeGroup`). |
| `AlbumDrawerVerdict` | **CORE section of `Artist.Discography.cs`** | pure static | ported verbatim (§8). |
| `DiscographyEraBands` | **CORE section of `Artist.Discography.cs`** | pure static | ported verbatim (§8); input becomes `(ReadOnlySpan<ushort> years)` instead of `IReadOnlyList<Album>` — the per-album year read moves to the caller. |
| `DiscographyPage` + `DiscographyRoute` | `Artist.Discography.cs` → `Artist.DiscographyPage : Component` + a CORE `Route` helper | Component + pure | `Shell.Route(RouteKind.Artist, subject, Tab: (int)kind)` is the cleaner 0.3 spelling — plan §4.11's `Route` already carries a `Tab`. Keep `disco:` deep-link parsing in `Shell.DeepLink`. |
| `ArtistGalleryLightbox` | `Artist.UI.cs` → `sealed class GalleryLightbox : Component` | Component | ctor `(ReadOnlySpan<StringId> → StringId[] photos, int index, Func<OverlayHandle?>)`. Unchanged internals. |
| cover palette (`Surfaces.SchemeFor` / `ChromeSchemeFor` / `CoverColorPlane`) | **`Entities/Palette.cs`** (CORE, Wave 1 owner A) + **`Entities/Palette.Host.cs`** (SHELL, Wave 4 owner L) — see §7 DATA GAPS #1 | — | the page reads `Palette.ChromeAccent(a.PaletteImageId)`; the **watch** stays in leaves (`CoverPaletteLeaves` equivalents), never in `Page.Render`. The pure colour arithmetic it calls (`Lift`, `Vivid`, `Accent`, `PageTone`, `TextInk`) stays in `Platform/Design.cs` — the *plane* moved, the *maths* did not. |

**Where the context band lives — one home, two consumers.** The chapter previously said `Shell/Shell.UI.cs`; that was
wrong and it contradicted `03-detail-frame.md`. The band belongs in **`Entities/Detail.UI.cs`** (CORE half in
`Entities/Detail.cs`), and this page calls it. The evidence, all of it from 0.2.9:

- **It is already one file with exactly two consumers, and it already sits under `Features/Detail/`.**
  `ContextBand.cs` (398) declares `SectionAnchors` (`:26-44`), `static class ContextBand` (`:69-188`) and
  `sealed class ContextPivot : Component` (`:190`) in namespace `Wavee` (`:12`) — a flat namespace, so the folder is
  the only statement of ownership the code makes, and the folder is `Detail`. Consumer 1, the detail frame:
  `DetailVerticalHero.cs:373-374` (`ContextBand.Title` / `ContextBand.Byline`) and `:393` (`ContextBand.Row`).
  Consumer 2, this page: `ArtistCompactBar.cs:48` (`Title`), `:52-53` (`ContextPivot.Props` + ctor), `:78` (`Row`),
  `:100` (`HairlineOverlay`), plus `ArtistPage.cs:232` (`ContextBand.Anchor`), `:321` (`ContextBand.ClipFadeBand`) and
  `:328, :338` (`ClipBelow(ContextBand.ClipInset)`). Nothing else in the app touches it (`Design/WaveeTokens.cs:296`
  and `DetailTracks.cs:1916` are comments about it, not calls).
- **Its geometry is detail-frame arithmetic, not shell arithmetic.** `ContextBandLayout.ClipFadeBand` is *literally*
  `DetailVerticalLayout.StickyFadeBand` (`ContextBandLayout.cs:44` → `DetailVerticalLayout.cs:92`), and
  `ContextBandLayout.Height 56` (`:27`) equals `DetailVerticalLayout.CompactIdentityHeight 56` (`DetailVerticalLayout.cs:78`)
  by construction; `ContextBand.ClipInset` / `ClipFadeBand` are re-exports of the layout constants
  (`ContextBand.cs:158, :161`). Homing the band in `Shell.UI.cs` therefore drags `DetailVerticalLayout` into the shell
  file — a dependency `18-shell-frame.md` never asks for (it claims neither the band nor its layout anywhere) and
  cannot afford: its own §9 already reports the `Shell.cs + Shell.UI.cs + Shell.Host.cs` budget of 4 800 lines
  over-subscribed by the port (`18-shell-frame.md:1137-1143`).
- **It must land ONCE, before either page starts.** `ContextBand`'s two consumers belong to different Wave 5
  subagents — this page is **owner N**, the detail frame is **owners M and O** — and `03-detail-frame.md` states the
  same rule for the frame as a whole ("two owners cannot both author one frame; it must land once, before either page
  starts"). Neither `Entities/Detail.cs` nor `Entities/Detail.UI.cs` exists in the plan's §2 tree
  (`wavee-0.3-implementation.md:32-70`; flagged at `03-detail-frame.md:975`), so **Wave 4 must add both files to §2
  and land the band in them**. If that does not happen, N writes the artist arm and M/O write the detail arm and the
  band exists twice — which is exactly how the 0.2.7 hero and rail drifted.
- **What legitimately differs between the two arms** (so nobody "unifies" the wrong half): content and gutter only.
  The artist arm passes `gutter = ArtistHeroLayout.PageGutterFor(width)` = 36/32/16/16 (`ArtistCompactBar.cs:41`) and
  fills the row with *title · pivot · actions*, no byline; the detail arm passes `compactLeft` =
  `TrackRow.PadXFor(tier)` = 16/12/8 (`03-detail-frame.md:675`) and fills it with *title + byline*, the expanded-search
  swap and the selection command arm (`DetailVerticalHero.cs:365-400`). Everything geometric —
  height 56, hairline 1, `ClipInset`, feather 24, `TitleCap` 280, cluster/pivot/action gaps, the underline and the
  whole scroll spy — is one shared implementation and is not allowed to fork.

**Props freeze at mount — the live list for this surface.** Data that changes after mount and how it reaches a child:

| Child | Changing input | Channel |
|---|---|---|
| `ContextPivot` | the section set grows as data lands | re-pushed `Props` (`ContextBand.cs:186-188`) |
| `Popular.Row` | the geometry Tier (shelf remeasure) | re-pushed `Props` — **never a Key** (`ArtistPopularLayout.cs:28-29`) |
| `Popular.Row` | the track behind index `i` | read live off the parent field/edge at render (`ArtistPopular.cs:592-604`) |
| `Drawer` | Pending→Ready rows, the verdict | re-pushed `Props` (`ArtistPage.AlbumExpand.cs:107-113`) |
| `Drawer.TrackRow` | the track, bridge, lib | read live off the panel (`ArtistPage.AlbumExpand.cs:297-318`) |
| `DiscographyFacetHeaderLabel` | title/total/eras | re-pushed `Props`; the visible range is a **ctor signal** |
| `PreSaveButton`, `PreReleaseCountdown`, `FollowButton`, `FollowTextAction` | the uri / the instant | **Key remount** (`"presave:"+uri`, `"artist-upcoming:"+uri+":"+ticks`, `"artist-follow:"+uri`, `"artist-band-follow:"+uri`) |
| `MediaCard.ArtistPick` | wide↔stacked tier | **Key remount** `"featured:pick:rail" / ":band"` (`ArtistPage.TopTracks.cs:98`) |
| `BiographyBand` | wide↔stacked | **Key remount** `"artist-biography:wide" / ":stacked"` |
| `HeroArt` | the url | **Key remount** `"heroart:"+url` |
| every section | its own identity | **Key** `"sec:"+key` — keyless children would cross-wire when a section is inserted mid-stream (`ArtistPage.cs:214-221`) |

---

## 2. Wireframes

Scale: **1 char ≈ 8 DIP horizontally**, 1 line ≈ 8–12 DIP vertically; every box is annotated with its real DIP.
`W` below is the **hero's own measured width** (`_heroWidth`, i.e. the content pane), not the window width.
At a 1440-wide window with the Classic mid sidebar (280) that is ≈1160; at 1280 with the narrow sidebar (240) ≈1040.

### Breakpoint table (all thresholds, with hysteresis)

| Decision | File | Thresholds | Hysteresis |
|---|---|---|---|
| Hero tier | `ArtistHeroLayout.cs:68-78` | Wide ≥880 · Medium ≥600 · Compact ≥360 · else Narrow | ±24: Wide holds to 856, re-entered at 904; Medium holds to 576, entered at 624; Compact holds to 336, entered at 384. First decision seeds from `ArtistHeroTier.Wide`. |
| Page gutter | `ArtistHeroLayout.cs:92-95` | ≥880 → 36 · ≥600 → 32 · ≥360 → 16 · else 16 | **none** (see §9 trap) |
| Top-tracks band wide | `ArtistPage.TopTracks.cs:17-27` | ≥760 | 24: holds to 736 once wide; latched in a plain field, seeded `true` |
| Chart columns | `ArtistPopular.cs:102-108, :138` | **two** clamps, ANDed: the shelf's own fit needs chart-column width ≥540 (`minCardW` 264, `maxCardW` 9999, gap 12/8), **and** `maxColumns = clamp(⌈total/5⌉, 1, 2)` — so a ≤5-track chart is ONE column at any width | shelf-owned (the count clamp has none) |
| Chart row tier | `ArtistPopularLayout.cs:38-40` | art 44 ≥220 (else 40) · duration ≥200 · subtitle stacks <340 | 24, one-directional (`Admit`). `previous: null` (mount, and every classic↔modern toggle) takes `NominalFor` outright |
| Drawer track columns | `AlbumDrawerVerdict.cs:21,24` | grid columns ≥5 ⇒ 2 | none (grid columns already hysteretic by width) |
| Grid columns | `LazyGrid.cs:234` | `cols = max(1, floor((w+16)/196))`; `cellW = max(90, (w−(cols−1)×16)/cols)` (minCol 180, gap 16) | none |
| Biography band wide | `ArtistPage.Biography.cs:22-27` → `DetailLayoutBreakpoints.ModeFor` | mode 0 at ≥820 | narrows immediately below 820; widens only at ≥844. First measure (`initialized:false`) takes the nominal answer |
| **Pre-measure width** | `TopTracks.cs:71`, `Biography.cs:78` | both `Responsive.Of(…, fallback: 900f)` — the first composed frame is decided at **900**, not at the real pane width, so Top tracks starts WIDE and Biography starts WIDE | n/a (one frame) |
| Scroll spy | `ContextBandLayout.cs:101-129` | active line = `bandBottom + (viewportH − bandBottom) × 0.25 + 8` (`SpyProbe`); at-scroll-end within `EndProbe` 8 of the limit the LAST contiguously measured section wins; a `NaN` (unrealized) top STOPS the scan | 8-DIP probe IS the hysteresis |

---

**W1 — Wide, fully loaded, scroll 0 @ W=1160** (hero 440, gutter 36, copy max 1120 → 1088)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ PHOTOGRAPH · full-bleed W×440 · ImageFit.Cover · FocusX .62 FocusY .34 · rest ScaleX/Y 1.05 (inner frame 1.08, OffsetY −17.6)                 │
│ ┌── veil: GradientRight over the whole 1160×440 — α .96 @0 → .92 @0.30 → .35 @0.62 → 0 @1.0, colour = Lerp(FillLayerDefault, wash, .16/.24)   │
│ │                                                                                                                                            │
│ │ ⟵36⟶ ✓ Verified Artist            ← InfoBadge.Icon(Accept, accent) 16 + Caption 12/16 TextSecondary, row Gap 8                    24 top pad│
│ │                                                                                                                        ↑                   │
│ │       Conan Gray                   ← ArtistDisplay 84/96/700, CS −28, MinSize 68, MaxLines 2, TextPrimary          Justify                  │
│ │                                                                                                                     Center                 │
│ │       Nobody has tapped into the thoughts and feelings of this era quite like Conan Gray.   ← Body 14/20, 2 lines, ellipsis, TextSecondary  │
│ │                                                                                                                        │                   │
│ │       #419 in the world    20,577,457 monthly listeners    13,357,890 followers   ← BodyStrong accent · Body · Body, row Gap 16             │
│ │                                                                                                                        ↓                   │
│ │       ╭─────────────╮ ╭────╮ ╭──────────────╮ ╭────╮      ← Play 36 capsule (accent fill) · Shuffle 36○ · Follow 36 pill · Radio 36○        │
│ │       │  ▶  Play    │ │ ⤨  │ │  ♡  Follow   │ │((· │        Gap 8, AlignItems Center. Order: Play, Shuffle, Follow, Radio.      24 bottom pad│
│ │       ╰─────────────╯ ╰────╯ ╰──────────────╯ ╰────╯                                                                                        │
│ └── photo EdgeFade Bottom, band clamp(440×.28,120,180) = 123 ──────────────────────────────────────────────────────────────────────────────  │
├──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP StrokeDividerDefault
│⟵36⟶                                                                                                                                    ⟵36⟶ │ inner top pad 12
│  ▌ Top tracks                                                                                        ‹  ● ●  ›   ← AccentHeader + ChartPager  │
│  ──                     ← AccentRule 20×2 accent, top margin 2 (header column Gap 2)                                                         │
│                                                                                                                                              │
│  ┌───── chart column: Grow 2, 712 ─────────────────────────┐ ⟵20⟶ ┌──── featured rail: Grow 1, 356 ────────┐                                 │
│  │ 1  [44] Heather                    ♥  3:18 │ 6 [44] …   │      │  ╭──────────────────────────────────╮  │                                 │
│  │       E · feat. X +2 · 2.4B plays          │            │      │  │ (32) Conan Gray                  │  │ ArtistPick, 8 r, FillCardDefault│
│  │ 2  [44] The Cut That Always Bleeds ♡  3:51 │ 7 [44] …   │      │  │      Artist pick                 │  │ + accent wash .16→.05→0         │
│  │ 3  [44] Memories                   ♡  4:08 │ 8 [44] …   │      │  │  “Out now — give it a listen.”   │  │ PickQuote 28/36/400, ≤4 lines   │
│  │ 4  [44] Vodka Cranberry            ♡  4:05 │ 9 [44] …   │      │  │  ┌────────────────────────────┐  │  │                                 │
│  │ 5  [44] Maniac                     ♡  3:05 │10 [44] …   │      │  │  │  photo 150 tall, cover     │  │  │ only when a wide image exists   │
│  └── rows 56 · gap 12 both axes · cell 350 ────────────────┘      │  │  └────────────────────────────┘  │  │                                 │
│                                                                   │  │  [44] Wishbone Deluxe   ╭─────╮  │  │ foot row: 44 cover · title ·    │
│                                                                   │  │       Album             │ ▶Play│  │  │ kind · Play (or Pre-save)      │
│                                                                   │  ╰──────────────────────────────────╯  │                                 │
│                                                                   └────────────────────────────────────────┘                                 │
│                                                    ⟵ section gap 32 ⟶                                                                        │
│  ▌ Latest release                                                                                                                            │
│  ──                                                                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐  │
│  │ [72] Album · Jun 12, 2026 · 17 tracks           ← Eyebrow 12/16/600 CS30 TextTertiary                    ╭────────╮ ╭──────╮            │  │
│  │      Wishbone Deluxe                            ← Ui.Subtitle 20/28/600, 1 line                          │ ▶ Play │ │ View │            │  │
│  └────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘  │
│   Pad 12 all · r8 · FillCardDefault · 1px StrokeCardDefault · Gap 12 · draggable (Album)                                                      │
│                                                    ⟵ section gap 32 ⟶                                                                        │
│  ▌ Albums  6 releases              ← facet header 40 tall, pinned at 56; spine 3×22 r-pill accent, Gap 8                                      │
│  ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐   cols = floor((1088+16)/196) = 5 · cellW = (1088−64)/5 = 204.8 · card h = 254.8          │
│  │ 204.8 │ │       │ │       │ │       │ │       │   row pitch = cellW + 70 (CardChrome 50 + RowGap 20) = 274.8 · column gap 16             │
│  │ cover │ │       │ │       │ │       │ │       │                                                                                           │
│  └───────┘ └───────┘ └───────┘ └───────┘ └───────┘                                                                                           │
│   Title 14/20/600                                                                                                                            │
│   2026 · 17 tracks 12/16                                                                                                                     │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                                                                    bottom pad = PlayerDock.Reserve 72 + 40
```

**W2 — Medium hero @ W=700** (tier Medium: 600 ≤ W < 880; height 384, gutter 32, copy max 760 → 636)

```
┌───────────────────────────────────────────────────────────────────────────────────┐
│ PHOTOGRAPH 700×384, horizontal veil (same 4 stops)                                │
│ ⟵32⟶ ✓ Verified Artist                                                    24 pad  │
│       Conan Gray            ← ArtistTitle 48/60/700, CS −20, MinSize 40           │
│       Nobody has tapped into the thoughts and feelings of this era…   2 lines     │
│       #419 in the world   20,577,457 monthly listeners   13,357,890 followers     │
│       ╭───────────╮ ╭───╮ ╭────────────╮ ╭───╮                            24 pad  │
│       │ ▶  Play   │ │ ⤨ │ │ ♡  Follow  │ │((·│                                    │
│       ╰───────────╯ ╰───╯ ╰────────────╯ ╰───╯                                    │
└───────────────────────────────────────────────────────────────────────────────────┘  photo fade band 120
   collapse distance 328 · expanded fade 232→328 · band reveal 284→328
```

**W3 — Compact hero @ W=500** (360 ≤ W < 600; **stacked**: photo band 200 + identity 252 = 452; gutter 16; copy 468)

```
┌───────────────────────────────────────────────────┐
│ PHOTOGRAPH 500×200 — EdgeFade Bottom band 120     │  no veil: nothing is set on the picture
│                                                   │
├───────────────────────────────────────────────────┤  identity band 252, Justify End, Pad 16/12/16/20
│                                                   │
│ ✓ Verified Artist                                 │  Gap 8 between blocks
│ Conan Gray                ← ArtistCompactTitle    │  32/40/700, CS −12, MinSize 28, ≤2 lines
│ Nobody has tapped into the thoughts and feelings  │
│ of this era quite like Conan Gray.                │
│ #419 in the world  20,577,457 monthly  13,357,890 │  meta stays ONE horizontal row at Compact
│ ╭──────────╮ ╭───╮ ╭───────────╮ ╭───╮            │
│ │ ▶ Play   │ │ ⤨ │ │ ♡ Follow  │ │((·│            │  one 36 row
│ ╰──────────╯ ╰───╯ ╰───────────╯ ╰───╯     ↓20 pad│
└───────────────────────────────────────────────────┘
```

**W4 — Narrow hero @ W=340** (< 360; photo 176 + identity 300 = 476; gutter 16; copy 308)

```
┌────────────────────────────────────┐
│ PHOTOGRAPH 340×176                 │
├────────────────────────────────────┤ identity 300
│ ✓ Verified Artist                  │
│ Conan Gray                         │ 32/40/700
│ Nobody has tapped into the         │
│ thoughts and feelings of this era. │
│ #419 in the world                  │ meta STACKS (Direction 1, Gap 4)
│ 20,577,457 monthly listeners       │
│ 13,357,890 followers               │
│ ╭──────────╮ ╭───────────╮         │ actions row 1: Play, Follow
│ │ ▶ Play   │ │ ♡ Follow  │         │
│ ╰──────────╯ ╰───────────╯         │ Gap 8
│ ╭───╮ ╭───╮                        │ actions row 2: Shuffle, Radio
│ │ ⤨ │ │((·│                        │
│ ╰───╯ ╰───╯                        │
└────────────────────────────────────┘
```

**W4b — Hero with NO image** (`(a.HeaderImage ?? a.Image)?.Url` empty — a cold artist, a stripped export, an offline
identity-only row). `HeroArt` is **not mounted at all**: the media box's only child becomes a flat
`BoxEl { Width = W, Height = photoH, Fill = Surfaces.ArtworkPlaceholder }` (`Hero.cs:99-102`) — the theme neutral
`#2A2A2A` dark / `#F2F2F2` light, **not** a cover-derived tint (there is no url to grade). Everything else is unchanged:
the box still carries `ClipToBounds`, the bottom `EdgeFade`, `.StretchFromTop()` and `.ParallaxY(.15, photoH)`, and on
Wide/Medium the veil still paints over it — so the copy sits on an accent-tinted flat field. No zoom keyframes run
(there is no `ImageEl` to settle), which is the one motion that disappears.

**W5 — Skeleton (Pending) @ W=1160** — `Skel.Region` derives this from `Body(PendingArtist(uri))`: the SAME tree, with
`FakeData.Artist(IndexFromUri(uri))` geometry and `Id/Uri/Name` blanked (`ArtistPage.cs:179-186`).

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ░░░░░░░░░░░░░░  hero photo slot — derived bar, full 1160×440  ░░░░░░░░░░░░░░░░░░░░░░░ │
│  ▒▒▒▒▒▒▒▒                    ← verified caption bar                                  │
│  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒    ← name bar at the tier's real 84/96 line box             │
│  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒                                                │
│  ▒▒▒▒▒▒▒▒  ▒▒▒▒▒▒▒▒▒▒▒  ▒▒▒▒▒▒▒▒▒                                                    │
│  (NO action row, NO compact bar: HeroActions and ArtistCompactBar are .Skeletonized(false)) │
├──────────────────────────────────────────────────────────────────────────────────────┤
│  ▒▒▒▒▒▒▒▒  ← AccentHeader bar (title width, NOT full width)                           │
│  ▒▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  ▒▒▒  │  ▒▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒  ▒▒▒     ← ArtistPopular.SkeletonShape:
│  ▒▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  ▒▒▒       │  ▒▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   seed cap 10 ⇒ 2 columns × 5 rows,
│  … 5 rows per column at the live 56/48 pitch, gap 12/8 …                              │
│  ▒▒▒▒▒▒  ← facet header bar                                                           │
│  ░░░░░  ░░░░░  ░░░░░  ░░░░░  ░░░░░   ← DiscoGrid.Placeholder cells: square ImageEl +   │
│  ▒▒▒▒▒  ▒▒▒▒▒  ▒▒▒▒▒  ▒▒▒▒▒  ▒▒▒▒▒     13-tall bar (max 150) + 11-tall bar (92)        │
│  ▒▒▒    ▒▒▒    ▒▒▒    ▒▒▒    ▒▒▒                                                      │
└──────────────────────────────────────────────────────────────────────────────────────┘
Bars: Tok.FillSubtleSecondary, r4, breathe 1000 ms 0.5→1 (SkeletonStyle.Default).
```

**W6 — Reveal in progress.** `SkelReveal.FadeOnly` — the content cross-dissolves **without** translate or blur, over
the shimmer, which exits as an orphan at `SkeletonStyle.ExitMs = Expressive.Fast = 250 ms`. Nothing moves; the derived
shimmer's geometry is the loaded geometry, so no row shifts. The hero copy additionally plays its own `EnterExit(Dy 12,
Opacity 0)` on `MotionTok.EmphasizedEnter` (500 ms `FluentDecelerate`) — the only motion in the reveal.

**W7 — Scrolled, context band engaged @ W=1160, offset ≥ 384**

```
 ┌────────────────────────────────────────────────────────────────────────────────────────────────────────┐
 │⟵36⟶ Conan Gray   ⟵24⟶  Top tracks  Albums  Singles & EPs  Compilations  Appears on  Biography   ⟵24⟶ Play  Following ⟵36⟶│  56
 │                                     ‾‾‾‾‾‾‾‾‾‾  2-DIP accent underline, 4 under the line box                            │
 ├────────────────────────────────────────────────────────────────────────────────────────────────────────┤  1 DIP hairline overlay
 │  (magazine, clipped at exactly 56; a 24-DIP EdgeFade Top feathers the cut while compactInteractive)     │
 │  ▌ Albums  1990s · 12 releases     ← facet header pinned at 56, its own 40 → sticky stack ends at 96    │
 │  ┌───────┐ ┌───────┐ ┌───────┐ …                                                                       │
```

The band's 56 DIP paint **nothing**: what shows there is the shell material + the blend wash (which is itself clipped
at 56 by a second `ClipBelow`). Title is capped at `TitleCap 280`; the pivot is the only elastic lane and scrolls
horizontally behind an auto edge fade; the actions never drop.

**W8 — Top tracks band, wide (chart 712 / rail 356) — see W1.**
**W9 — Top tracks band, stacked (W < 760, e.g. 636)**

```
┌────────────────── 636 ───────────────────┐
│ ▌ Top tracks                     ‹ ●● ›  │
│ ──                                       │
│ 1 [44] Heather               ♥ 3:18 │ …  │  cell = (636−12)/2 = 312 ⇒ tier: art 44, duration on,
│        E · feat. X +2                │    │  StackSub TRUE (312 < 340) ⇒ 3rd line for the count
│        2,381,125,897 plays           │    │
│ …                                         │
├──────────────────────────────────────────┤  Gap 20 (Spacing.XL), AlignItems Stretch
│ ArtistPick — horizontal arm (wide:false) │  photo becomes a 300-wide right COLUMN, AlignSelf Stretch
│ ┌───────────────────┬──────────────────┐ │
│ │ (32) Conan Gray   │                  │ │
│ │      Artist pick  │   photo 300 col  │ │
│ │ “Out now…”        │                  │ │
│ │ [44] Wishbone  ▶  │                  │ │
│ └───────────────────┴──────────────────┘ │
└──────────────────────────────────────────┘
```

**W10 — Chart row anatomy**

```
Modern, cell ≥ 340 (no stack), art 44:                                        row 56 · ZStack · r6 · border 1 transparent
│3│⟵2⟶┌──────┐                                                                   ↑ SelectionPill 3×16 r1.5, Margin left 2
│ │   │ 1 ⇄ ▶│ 24×24 NumberCell (number ⇄ play/pause on hover, EQ when now)       hover: HoverFill RowHover + HoverBorderColor StrokeCardDefault
│ │   └──────┘  ⟵8⟶ ┌────────┐ ⟵8⟶ Heather                       ⟵8⟶ ♡28 ⟵6⟶ 3:18  press: RowPressed + PressScale 0.98
│ │              44 │ artwork│     E · ▤ · feat. Ariana +2 · 2.4B plays           title 14/600 (accent when now-playing)
│ │                 └────────┘     (dot separators 12 TextTertiary, gap 5)        sub 12 TextTertiary, dur 13 TextSecondary
Modern, cell < 340 (stack), a feat line AND a play count present:              3 lines: title / sub / "2,381,125,897 plays"
   The 3rd line is gated on ALL THREE: stackSub && featLine != null && PlayCount > 0 (`ArtistPopular.cs:372`).
   A stacked row with no feat credit stays 2-line and keeps the compact count in the sub run.
Modern mid-column gap is 1 DIP (Classic 4); the sub run's own inter-part gap is 5, the trailing cluster's 6.
Classic (TrackRowStyle == 1): row 48, art fixed 40, Corners None, border 0, padding 4/0/4/0, + 1-DIP hairline AlignSelf End,
                              cell gap 8 both axes, header gap 8, pill corners 0, ExplicitBadge = TrackRow.ClassicExplicitBadge.
cell < 220 ⇒ art 40.   cell < 200 ⇒ the duration cell is dropped entirely.

TRACK ARTWORK HIDDEN (Settings → Appearance, `AppearancePrefs.TrackArtworkHidden`) — a REAL state this page has:
│ 1 │ Heather                                              ♡  3:18 │  the art cell is not hidden, it is NOT BUILT
│   │ E · feat. X +2 · 2.4B plays                                  │  (rowChildren is 3 long, `ArtistPopular.cs:415-429`)
The mid column simply takes the width back; row height is unchanged. It rides BOTH keys — the row's
(`|art` / `|noart`) and the shelf's (`:art=True/False`) — so toggling it remounts the chart rather than re-skinning it.

CLASSIC + NOW-PLAYING (`classicNow`) tints MORE than the title: the explicit badge, every `·` separator, the video
glyph, the play-count run AND the duration all take `Tok.AccentTextPrimary` (`ArtistPopular.cs:386-413`). In MODERN
the accent stops at the title and the rest of the row keeps its resting ink.
```

**W11 — Chart pager**

```
one page:      ▌ Top tracks                                        10      ← TextEl(total) 12/600 TextTertiary
≥ two pages:   ▌ Top tracks                                    ‹  ● ○  ›   ← Chevron 28×28 r14 (glyph 12) + PipsPager
                                                                             disabled chevron: no HoverFill, glyph TextTertiary
Pager cluster gap 4 (Spacing.XS). The pips are a CONTROLLED pager mirrored from the shelf's page through an EFFECT;
onChange AND onReselect both call ctx.GoTo, because after a partial pan the strip rests BETWEEN pages while the pip
still reads that page and the re-click is the request to be put back on the boundary (`ArtistPopular.cs:541-556`).
Shelf knobs not visible in the wireframe but load-bearing: `pager: ShelfPager.None` (the stock row is replaced),
`headerGap` 10 Modern / 8 Classic, `edgeFade` 16 (≥ the shelf's 12-DIP halo bleed, and no more — past that the fade
reaches the duration cell), `maxItems` 50, `maxCardW` 9999 (uncapped on purpose: with maxColumns 2 the fitted card
must keep filling the band).
```

**W12 — Upcoming card, both arms**

```
P1 · rail (wide, no pick)  — Direction 1, Pad 16, Gap 8      P2 · full band (a pick owns the rail) — Pad 20/16/20/16, Gap 20
┌──────────────────────────────────────┐                     ┌──────────────────────────────────────────────────────────────────┐
│ Upcoming · Album      ← Eyebrow tint  │                     │ ┌────┐  Upcoming · Album                       Jun 12   ╭──────╮ │
│ Jun 12               ← SurfaceDisplay │                     │ │ 88 │  Wishbone Deluxe          ← 1 line       40/52   │Pre-sv│ │
│ 40/52/400 display face, CS −12        │                     │ │ r4 │  Conan Gray · Releases Jun 12, 2026             ╰──────╯ │
│ Wishbone Deluxe      ← PickQuote ≤2   │                     │ └────┘                                             ╭──────────╮ │
│ Conan Gray           ← Caption        │                     │          (countdown under the actions, AlignEnd)    │ 23:41:02 │ │
│ ⏱ 13d 04:12          ← countdown ≤14d │                     │                                                    ╰──────────╯ │
│ ╭────────╮ ╭──────╮                   │                     └──────────────────────────────────────────────────────────────────┘
│ │Pre-save│ │ View │  ← no Play, ever  │                     Both: r8, FillCardDefault, 1px StrokeCardDefault, accent gradient
│ ╰────────╯ ╰──────╯                   │                     down α .16 @0 → .05 @0.55 → 0 @0.85.
└──────────────────────────────────────┘
```

**W13 — Latest release banner** — see W1. Cover 72 r4 (decode 144), eyebrow + `Ui.Subtitle` title, trailing
`[▶ Play][View]`, whole card draggable, **no whole-card OnClick** (leaf buttons only).

**W14 — Discography facet, expanded @ inner 1088**

```
▌ Albums   1990s · 12 releases        ← 3×22 accent spine (r Pill), RailHeader 20/28/600 + meta 12/16 TextTertiary
  ⌄                                      header row MinHeight 40, Pad 0/4/8/4, chevron 28×28 Margin-left 8, PinTop 56
┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐
│ 204.8 │ │       │ │       │ │       │ │       │   grid ClipBelow(96) with a 24-DIP EdgeFade Top while clipped
│ cover │ │       │ │       │ │       │ │       │   card height = cellW + 50; row pitch = cellW + 70
└───────┘ └───────┘ └───────┘ └───────┘ └───────┘
 Title                                              GridCard: cover fills the cell, title 14/20/600 1 line,
 2026 · 17 tracks                                   meta 12/16 1 line, hover plate + shadow + OffsetY −4
└──── trailing spacer 24 (Spacing.XXL), then the page's own 32 section gap ────┘
```

**W15 — Album drawer open (2 track columns; 5 grid columns).** The row numbers below are a **1-column** illustration of
the column-major split and the show-all tail; the real 2-column cap is 24, so the "Show all" row cannot appear at 2
columns until the album passes 24 tracks — see the corrected arithmetic under the frame.

```
┌───────┐ ┌───────┐ ┌═══════┐ ┌───────┐ ┌───────┐    ← the opened card: BorderColor accent, BorderWidth 2, Fill FillCardDefault
│       │ │       │ ║ 204.8 ║ │       │ │       │
└───────┘ └───────┘ └═══════┘ └───────┘ └───────┘
                        ╱╲                             caret 16×8 at the card's centre, clamped to [8, panelW−8]
┌───────────────────────╱──╲──────────────────────────────────────────────────────────────────────┐  ← TopGap 8 band
│ ⏵26  [28] Wishbone Deluxe · 2026 · 17 tracks                                            ( ↗ 28 ) │  Head row 28
│ ─────────────────────────────────────────────────────────────────────────────────────────────── │
│  1  ♡  Heather                     3:18  ⋯ │  7  ♡  Footnote                     2:53  ⋯        │  RowPitch 32 (content 28)
│  2  ♡  Maniac                      3:05  ⋯ │  8  ♡  (Online Love)                2:11  ⋯        │  columns: # 26 · ♥ 28 · title ★ · time 44 · … 32
│  3  ♡  Wish You Were Sober         3:21  ⋯ │  9  ♡  The Story                    4:04  ⋯        │  column gap 20 (Spacing.XL), column-major split ⌈n/2⌉
│  4  ♡  Checkmate                   2:59  ⋯ │ 10  ♡  Show all 17 tracks                          │  Show-all row = the LAST cell in the same sequence
│  5  ♡  The Cut That Always Bleeds  3:51  ⋯ │                                                    │
│  6  ♡  Little League               3:33  ⋯ │                                                    │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘  ← BottomGap 8
 Panel: Pad 12/6/12/6 · r8 · FillCardSecondary · 1px StrokeCardDefault
 Heights: panel = HeaderH 40 + rows×RowPitch 32; slot = panel + TopGap 8 + BottomGap 8.
 cap = CapPerColumn 12 × columns.  shown = min(have, cap).  showAll = total > cap.  rows = ⌈(shown + showAll)/columns⌉.
 WORKED CASES (`AlbumDrawerVerdict.cs:31-47`):
   17 tracks, 2 cols (cap 24)  ⇒ shown 17, showAll FALSE, rows ⌈17/2⌉ = 9  ⇒ panel 328, slot 344   ← the frame above
   12 tracks, 2 cols           ⇒ shown 12, showAll FALSE, rows 6           ⇒ panel 232, slot 248   (parity item 53)
   17 tracks, 1 col  (cap 12)  ⇒ shown 12, showAll TRUE,  rows 13          ⇒ panel 456, slot 472
   30 tracks, 2 cols           ⇒ shown 24, showAll TRUE,  rows ⌈25/2⌉ = 13 ⇒ panel 456, slot 472
 A ready-but-empty drawer is a FIXED 2 rows regardless of columns (`readyEmpty ? 2 : …`) ⇒ panel 104, slot 120.

 THE SELECTION COMMAND BAR (missed by every earlier pass). The panel body is not the panel: it is
 `ZStack(body, SelectionCommandBar(_sel, i => rows[i], bottomPadding: Spacing.S))` (`AlbumExpand.cs:158-162`) — the
 shared multi-select action bar floats over the drawer's own rows, 8 DIP up from its bottom edge, the moment a row is
 selected. Its row accessor reads `_rows` / `_verdict` LIVE (never a captured local), because the factory freezes at
 mount and a frozen count would index past an empty list while the album is still loading. `_sel.ItemCount` is
 `verdict.Shown` — only the shown rows are selectable; the ones behind "Show all" are not.
```

**W16 — Drawer loading / ready-empty**

```
loading (fetch pending for THIS uri):              ready but empty (offline / trackless):
│ ⏵ [28] Album · 2026 · 17 tracks        (↗) │     │ ⏵ [28] Album · 2026                     (↗) │
│ ▬▬ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ ▬▬  ▫32 │ ▬▬ ▬▬▬▬… │     │  No tracks                       ╭────────╮  │  2 rows tall
│ ▬▬ ▬▬▬▬▬▬▬▬▬▬▬▬ ▬▬      ▫32 │ ▬▬ ▬▬▬… │     │                                  │ Retry  │  │  Retry = Resource.Refresh
 shimmer row: 16×11 + flexible (max 240)×11 + 30×11 + a reserved 32 lane, all FillSubtleSecondary, SAME 32 pitch
 rows shown = min(total or 3, cap) — the slot height is FINAL on the click frame, so nothing reflows when the rows land.
```

**W17 — Facet collapsed** (`Expander` toggled): the header row (40) stays pinned, the grid unmounts, the section
collapses with the stock `DisclosureCollapse` (167 ms `FluentDisclosureCollapse`); **content-resize animation is off**
(`AnimateContentResize = false`) so a drawer opening inside the grid never plays a second section tween.

**W18 — Biography band**

```
wide (≥820 first-measure / ≥844 re-entry) @ inner 1088:                        stacked (<820):
┌──── left card, Grow 2 = 712 ─────────────┐ ⟵20⟶ ┌── right, Grow 1 = 356 ──┐  ┌────── left card, full width ──────┐
│ ▌ Biography                              │      │ ▌ Profile facts         │  │ ▌ Biography                       │
│ ──                                       │      │ ──                      │  │ …                                 │
│ Rich text, 14/20 TextSecondary, links in │      │ ┌─────────┐ ┌─────────┐ │  └───────────────────────────────────┘
│ AccentTextPrimary, wrap width            │      │ │20,577,45│ │13,357,89│ │  ┌───── right column, full width ────┐
│ = 1088×0.62 − 60 = 614.6, max 14 lines   │      │ │Monthly l│ │Followers│ │  │ tiles wrap, Basis 140 each        │
│                                          │      │ └─────────┘ └─────────┘ │  └───────────────────────────────────┘
│ ( Instagram ) ( Twitter ) ( Wikipedia )  │      │ ┌─────────┐ ┌─────────┐ │
│  ← pills r-Full, Pad 12/7/14/7, 1px      │      │ │ 6       │ │ 20      │ │  StatTile: Pad 16, r8, FillCardSecondary,
│                                          │      │ │ Albums  │ │Singles &│ │  1px stroke, Ui.Title 28/36/600 + Caption
│ Listened to most in       ← Eyebrow      │      │ └─────────┘ └─────────┘ │  A zero stat drops its WHOLE tile;
│ São Paulo                     684,000    │      │ ┌─────────┐ ┌─────────┐ │  no tiles ⇒ the right column is dropped
│ ████████████████████░░░░░░░░  4 DIP bar  │      │ │ 7       │ │ 12      │ │  and the biography takes the full width.
│ London                        342,000    │      │ │Upcoming │ │Related  │ │
│ ██████████░░░░░░░░░░░░░░░░░░             │      │ └─────────┘ └─────────┘ │
└──────────────────────────────────────────┘      └─────────────────────────┘
 Card: Pad 20/16/20/16, Gap 16, r8, FillCardSecondary, 1px StrokeCardDefault, ClipToBounds
 City row: 34 tall, Gap 4; bar Grow = max(0.001, listeners/max) against Grow = max(0.001, 1 − frac), Height 4, r2, Fill accent
```

**W19 — The shelves** (all `PagedShelf.Create(measured: true)` ⇒ auto-fit cards between `minCardW 150` and
`maxCardW 200`, gap 12, one row, `edgeFade 36`, stock chevrons)

```
▌ Appears on          ← AccentHeader(accent)                           card = MediaCard.Shelf, square cover, title + year|kind
──                                                                      cap: none
┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐ …  ▸

▌ Music videos        16:9 VideoCard (thumb cardW−16 at 9/16), title + duration, ≤16
▌ Playlists and discovery   Shelf card, subtitle = PlaylistRef.Subtitle, ≤16
▌ Upcoming concerts   ConcertStub: [48 date col: Eyebrow "Jan" accent / day 28/36/600] venue 14/20/600 · ⌖ city 12/16, ≤12
▌ Merch               MerchCard: ArtworkFill square, name 13/600 ≤2 lines, price 13/700 AccentTextPrimary, ≤12
▌ Gallery             square tile w×w r8, HoverScale 1.04 / PressScale 0.96, decode 480, click → lightbox, ≤16
▌ Fans also like      Shelf card circular:true (PersonPicture fallback), subtitle "Artist"
```

**W20 — Tour banner** (no pivot entry; a full-width announcement)

```
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ ╭────╮  ON TOUR NOW            ← Eyebrow 12/16/600 CS30, WaveeAccent.Decor (AccentTextPrimary) │
│ │ ((·│  Conan Gray — on tour   ← 16/700 TextPrimary, 1 line                                 ›│
│ ╰────╯  Next: JUN 12 · The O2 · London · 14 dates total   ← 13 TextSecondary, 1 line         │
└────────────────────────────────────────────────────────────────────────────────────────────┘
 44 circle r22 Fill accent, glyph RadioTower (live) | Calendar 18 TextOnAccentPrimary · Pad 16 · Gap 16 · r8
 FillCardSecondary → HoverFill FillCardDefault · Role Button · FocusVisualMargin 2 · click → artist-concerts route
```

**W21 — Gallery lightbox**

```
chrome visible:                                             chrome hidden (2600 ms idle):   zoomed (Ctrl+wheel / pinch):
┌───────────────────────────────────────────────────┐       ┌─────────────────────────┐   ┌────────────────────────────┐
│ Gallery                  ╭──────────╮╭─────────╮  │ 72    │                         │   │  FlipView UNMOUNTED        │
│ 3 / 12         ← spacer  │ Export   ││  Close  │  │ scrim │      photo Contain      │   │  FILMSTRIP UNMOUNTED too   │
│                   Grow 1 ╰──────────╯╰─────────╯  │ 158→0 │                         │   │  pan/zoom carrier:         │
├───────────────────────────────────────────────────┤       │                         │   │  scale ≤ 4, click-zoom 2.2 │
│                                                   │       │                         │   │  spring(0.32, 0.9)         │
│         photo · ImageFit.Contain                  │       │                         │   │  drag pans, 4px slop       │
│         DecodePx min(max(vw,vh), 2048)            │       │                         │   │  click (no drag) → home    │
│         RevealTransition Fade(140 ms)             │       │                         │   │  Esc → unzoom (vetoes close)│
│                                                   │       │                         │   └────────────────────────────┘
│   ▫56 ▫56 ▪56 ▫56 ▫56   filmstrip 84, AlignSelf End│       │                         │
└───────────────────────────────────────────────────┘       └─────────────────────────┘
 TOP CHROME is ONE 72-tall ROW (Direction 0): the [Gallery 14/600 · "n / N" 12] column (inner gap 2), a `Grow = 1`
 spacer, then `Button.Accent("Export image" / "Exporting…", isEnabled: !saving)` and `Button.Standard(Close)` side by
 side. Pad 16/12/16/12, Gap 12, Gradient = GradientDown(rgba(0,0,0,158) → 0). (`ArtistGalleryLightbox.cs:298-322`)
 Ground: rgba(0,0,0,224) ≈ 88 %. Filmstrip: Thumb 56, Pad 14 all, Gap 8, Justify Center, r4, decode 128,
 HoverScale 1.04. Active thumb: 2-DIP AccentTextPrimary border, Opacity 1; others 0.55, Transition ControlFast (150 ms).
 Chrome opacity 1↔0 on MotionTok.ControlNormal (250 ms) — the SAME signal drives the top row and the filmstrip.
 The photo's own Placeholder is fully TRANSPARENT (rgba 0,0,0,0) + the BlurHash — not the artwork neutral, so a
 still-decoding frame shows the black ground, never a grey tile.
```

**W22 — Discography page (`disco:0:{artistUri}`)**

```
┌──────────────────────────────────────────────────────────────────────┐
│⟵36⟶ Conan Gray  ›  Albums                        ← BreadcrumbBar      │  content Gap 32, Pad 36/24/36/(72+36)
│                                                                      │
│ Albums                                           ← PageHero 28/36/600 │
│ ( Albums )( Singles )( Compilations )            ← SelectorBar        │
│                                                                      │
│ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐   the SAME DiscoGrid,
│ │       │ │       │ │       │ │       │ │       │   expandedTopInset 28
│ └───────┘ └───────┘ └───────┘ └───────┘ └───────┘   (no sticky facet band here)
│  Title                                              pages of 60 (DiscoVc)
│  2026 · 17 tracks                                   total probed with limit 0 ⇒ shimmer-up-to-N at once
└──────────────────────────────────────────────────────────────────────┘
 breadcrumb column Gap 8. SelectorBar takes a FRESH Signal<int>(FacetIndex(kind)) every render — the ROUTE, not the
 control, is the source of truth; onChange navigates and the next route re-seeds it (`DiscographyPage.cs:120-125`).
```

**Five things this page does NOT inherit from the inline facet, all of them visible:**

1. **No cover palette.** `DiscoGrid` is constructed with no `accent`, so it falls back to `ThemeAccent = Tok.AccentDefault`
   (`AlbumExpand.cs:408, :476`). The expanded card's 2-DIP border, the caret's neighbours, the drawer's play circle and
   its selection pills are all **system accent** here, while the same album inside the artist page wears the cover's.
2. **No readiness boundary.** There is no `Skel.Region` and no `ErrorState` on this page (`DiscographyPage.cs:129-149`):
   a failed facet fetch shows an empty grid, not the shared error state. The only "loading" affordance is the total
   probe's shimmer-up-to-N.
3. **No shell tint.** `disco:` is absent from `ContentHost.PublishesShellMaterial` (`ContentHost.cs:184-186`), so the
   window chrome keeps whatever the PREVIOUS page published — open a facet from a colourful artist and the chrome stays
   that artist's colour; open it from a deep link and it is neutral.
4. **A facet switch is a NEW keep-alive slot.** `PageNavMotion.SlotKey` is `tab + route.Name + route.Arg`, so
   `disco:0:…` and `disco:1:…` never share a slot. The page's own `_vc is null || _key != route.Name` rebuild branch
   (`DiscographyPage.cs:86-95`) therefore only ever fires on the mount frame — it is dead defence in the shell, and the
   page slides like any other route change.
5. **Hardcoded breadcrumb fallback.** `artistName` is `"Artist"` written into the file (`DiscographyPage.cs:83`) when the
   route carries no `Arg` — while `artist.fallbackName` ("Artist") exists in `en-US.json` and has **no call site at
   all** in the app.

**W23 — Error / offline.** `Skel.Region(onFailed: ErrorState.Build(artist.Error))` — a full-page `EmptyState` with
`Strings.Common.ErrorTitle` / `ErrorSubtitle` and **no Retry** (none is passed, `ArtistPage.cs:135`). The hero, the
band and every section are absent; the page tint claim still holds the previous page's colour. *0.3 must add the
retry* (§9).

**W24 — Empty.** There is no "empty artist" state: `biography` is unconditional, so the minimum page is
hero + divider + `▌ Biography` with an empty body. Sections with no data are simply not built (their `if` is false),
so the page shortens — it never renders an empty shelf.

**W25 — Chart row states**

```
rest        │ 1 │ [44] │ Heather                     ♡  3:18 │  transparent fill, 1px transparent border
hover       │ ▶ │ [44] │ Heather                     ♡  3:18 │  RowHover fill + StrokeCardDefault border; number ⇄ play glyph
pressed     │ ▶ │ [44] │ Heather                     ♡  3:18 │  RowPressed fill, PressScale 0.98
now-playing │≡≡≡│ [44] │ Heather   (accent title)    ♥  3:18 │  equalizer in the # cell + AccentTextPrimary title — NO row fill
selected    │▌1 │ [44] │ Heather                     ♡  3:18 │  3×16 accent pill at the left edge (opacity bind only)
focused     │ 1 │ [44] │ Heather                     ♡  3:18 │  engine focus ring (Role Button)
```

**W26 — Drag in progress.** A chart row, a discography card, a drawer row, the latest-release banner and every shelf
card are `Drag.Source(WaveeDragKinds.Resource, …)`. The payload is built once at promotion: a chart/drawer row inside a
multi-selection drags the **whole selection**. Visuals are the shared drag chip (`01-track-row.md`); the chart's axis
arbitration is free — a vertical lift wins, a horizontal sweep yields to the shelf's pan
(`ArtistPopular.cs:249-256`).

**W27 — Card context menu** (right-click / `Menu` key / long-press / the card's hover "⋯"): `Menus.CardAttach` →
`Menus.Card(uri…)`. For an **album** card: strip `[Play · Play next · Play after · Save]`, rows
`[Add to playlist ▸ · Open · Pin/Unpin · Go to artist · Share ▸]`. For an **artist** card (related/fans):
strip `[Play · Follow]`, rows `[Follow · Open · Pin/Unpin · Share ▸ · Go to artist radio]` — no queue verbs, no
Add-to-playlist (`Menus.cs:544-603`). Header = cover + name + subtitle (circular for artists).

The other shelves on this page take the same `Menus.Card` seam and therefore get **different** menus, which the row
above does not cover: a **playlist** card (Playlists and discovery) gets the full container grammar including
Add-to-playlist; a **music-video** card is a `spotify:track:` uri and falls through to `Menus.TrackUriCard`'s thin
track shape; and three surfaces carry **no menu at all** — gallery tiles, merch cards and concert stubs, none of which
pass a `menu:` argument (`Shelves.cs:101-180`). The drawer's own rows use `TrackContextMenu.Build` (selection-aware,
Explorer semantics) while the CHART's rows use `TrackContextMenu.BuildSingle` — a real asymmetry to preserve or to
deliberately unify in 0.3 (`ArtistPopular.cs:267` vs `AlbumExpand.cs:247-248`).

**W28 — Pivot under pressure @ W=700.** Title (≤280) and actions never drop; the pivot scrolls horizontally with an
auto edge fade and `MaxItems = 16`. A pivot click brings that section's anchor to `margin = bandBottom` through the
page's registered viewport.

---

## 3. Tokens

| element | size (DIP) | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| hero root | W × 440/384/452/476 | — | 0 | — | — | ClipToBounds, ZStack | `ArtistHeroLayout.cs:31-52`, `Hero.cs:189` |
| hero photo box | W × photoH | — | 0 | — | `Surfaces.ArtworkPlaceholder` (#2A2A2A dark / #F2F2F2 light) | `EdgeFade Bottom clamp(h×.28,120,180)` | `Hero.cs:103-109`, `ArtistHeroLayout.cs:97` |
| `HeroArt` inner frame | W × photoH | — | 0 | — | — | ScaleX/Y 1.08, OffsetY −photoH×0.04 | `Hero.cs:288-290, :350-356` |
| hero veil (Wide/Medium) | W × height | — | 0 | — | `GradientRight` .96/.92/.35/0 of `Lerp(FillLayerDefault, wash, .16 light / .24 dark)` | — | `Surfaces.cs:173-192` |
| hero copy pad (horizontal) | — | 36/24/36/24 (Wide), 32/24/32/24 (Medium) | — | — | — | `Justify Center, AlignItems Start` | `Hero.cs:157-165` |
| hero identity pad (stacked) | — | 16/12/16/20 | — | — | — | `Justify End` | `Hero.cs:144-151` |
| identity column | ≤`CopyMaxWidth` (1120/760/640/520) | Gap 8 | — | — | — | `EnterExit(Dy 12, Opacity 0)` | `Hero.cs:74-94` |
| verified badge | 16 (glyph 14) | row Gap 8 | — | `Ui.Caption` 12/16/400 | badge `_accent`, text `Tok.TextSecondary` | — | `Hero.cs:36-45`, `InfoBadge.cs:44` |
| artist name | — | — | — | `ArtistDisplay` 84/96/700 CS −28 MinSize 68 · `ArtistTitle` 48/60/700 CS −20 MinSize 40 · `ArtistCompactTitle` 32/40/700 CS −12 MinSize 28 | `Tok.TextPrimary` | — | `WaveeType.cs:155-187` |
| hero bio | ≤ copy width | — | — | `Ui.Body` 14/20/400, `MaxLines 2` | `Tok.TextSecondary` | — | `Hero.cs:62-72` |
| hero meta | — | Gap 16 (row) / 4 (stacked) | — | `Ui.BodyStrong` 14/20/600 (rank) · `Ui.Body` (counts) | rank `Tok.AccentTextPrimary`, counts `Tok.TextSecondary` | — | `Hero.cs:236-259` |
| Play capsule | h 36 | 18/6/18/7 | `Radii.Full` | Button label, `Bold` | `WaveeCta.Palette(_accent)`, ink `PickContrast` | hover 1.04 / press 0.96 | `WaveeCta.cs:88-107` |
| Shuffle / Radio | 36 × 36 | — | `Radii.Full` | icon | `ButtonAppearance.Subtle` ramp | hover 1.04 / press 0.96 | `WaveeCta.cs:121-152`, `Hero.cs:212-214` |
| Follow pill | h 36 | 12/0/12/0, Gap 4 | `Radii.Full` | `Ui.Body` 600 | border `AccentDefault` when following else `StrokeControlDefault`; glyph Heart/HeartFill 14 | hover 1.04 | `SaveButton.cs:146-164` |
| context band | W × 56 | 36/0/36/0 (gutter), cluster gap 24 | 0 | — | **no fill** | hairline 1 `Tok.StrokeDividerDefault` overlaid at the bottom | `ContextBand.cs:130-153`, `ContextBandLayout.cs:27-53` |
| band title | ≤280 | — | — | `Ui.BodyStrong` 14/20/600 | `Tok.TextPrimary` | — | `ContextBand.cs:113-117` |
| pivot link | h 32 | 8/0/8/0, gap 16 | 4 | 14/20/600 | active `TextPrimary`, rest `TextSecondary`, hover `TextPrimary` | — | `ContextBand.cs:288-315` |
| pivot underline | h 2, stretch | margin-top 4 | 0 | — | `_accent` / transparent, `BrushTransitionMs 167` | — | `ContextBand.cs:306-313` |
| band text action | h 32 | 10/0/10/0, gap 16 | 4 | 14/20/600 | primary/toggled `AccentTextPrimary`→`AccentTextSecondary`; else `TextSecondary`→`TextPrimary` | — | `WaveeCta.cs:183-218` |
| page divider under hero | h 1 | — | — | — | `Tok.StrokeDividerDefault` | — | `ArtistPage.cs:325` |
| magazine column | ≤1600 | 36/12/36/112 (gutter/12/gutter/`PlayerDock.Reserve`+40), section gap 32 | — | — | — | `EdgeFade Top 24` while the band is engaged | `ArtistPage.cs:271-285, :320-321` |
| `AccentHeader` | — | column gap 2 | — | `RailHeader` = `Ui.Subtitle` 20/28/600, `MaxLines 1` + ellipsis | `Tok.TextPrimary` | rule 20×2 `_accent`, margin-top 2, `AlignSelf.Start` | `Surfaces.cs:339-369` |
| `Section(title, body)` | — | column gap 12 (`Spacing.M`) | — | — | — | header + body, nothing else | `Sections.cs:39-42` |
| `AccentHeader(title, count)` / `SectionN` | — | title row gap 8 | — | + `BodyStrong(count)` `TextTertiary` beside the title | — | **defined but unused by this page today** — the counted arm is dead code the 0.3 port should either wire to the facet totals or drop | `Sections.cs:21-48` |
| chart row (Modern) | h 56 | 8/0/8/0, gap 8 | 6 | title 14/600, sub 12, dur 13 | fill transparent; `WaveeColors.RowHover` / `RowPressed`; border 1 transparent → `StrokeCardDefault` on hover | press 0.98 | `ArtistPopular.cs:443-486` |
| chart row (Classic) | h 48 | 4/0/4/0 | 0 | as above, mid gap 4 | + 1-DIP `StrokeDividerDefault` hairline at the bottom | — | `ArtistPopular.cs:442-485, :516-522` |
| chart art | 44 (≥220) / 40 | — | 4 | — | decode 96 | — | `ArtistPopular.cs:424-429`, `ArtistPopularLayout.cs:53` |
| chart number cell | 24 × 24 | — | — | — | `TrackRow.NumberCell` | — | `ArtistPopular.cs:417-422` |
| chart heart | 28 × 28 | — | circle | — | `TrackRow.Heart` | — | `TrackLane.Heart = 28` |
| selection pill | 3 × 16 | margin-left 2 | 1.5 (0 Classic) | — | `_accent` | opacity bind (compositor only) | `ArtistPopular.cs:275-285` |
| chart chevron | 28 × 28 | — | 14 | glyph 12 | `HoverFill Tok.FillSubtleSecondary`; glyph `TextSecondary` / `TextTertiary` when disabled | hover `ScaleEmphatic` 1.07 | `ArtistPopular.cs:561-567` |
| Artist pick card | fills the rail | head 16/16/16/0 · quote 16/8/16/16 · foot 16/12/16/12 | 8 | `PickQuote` 28/36/400 display | `Tok.FillCardDefault` + accent wash .16/.05/0 | `Elevation.Card`; hover OffsetY −4, press 0.99 | `MediaCard.cs:299-421` |
| pick avatar | 32 | — | circle | — | `PersonPicture`, border `StrokeCardDefault` | — | `MediaCard.cs:331-334` |
| pick photo | h 150 (column arm) / w 300 (row arm) | — | 0 | — | `Ui.Image` Cover aspect 1.6, decode 640 | — | `MediaCard.cs:370-388` |
| pick foot cover | 44 × 44 | — | 4 | `TrackTitle` 14/20/600 + `TrackMeta` 12/16 | decode 96 | — | `MediaCard.cs:425-471` |
| pick foot date | — | row gap 4 | — | `Ui.Caption` 600 | `Strings.Detail.ReleasesOn(date)` in the **cover accent**, beside the kind — only when `pinned.ReleaseAt` exists | — | `MediaCard.cs:455-467` |
| pick head | — | 16/16/16/0, gap 12 | — | `Ui.BodyStrong` name + `Ui.Caption` "Artist pick" `TextSecondary` | — | — | `MediaCard.cs:326-352` |
| drawer selection command bar | over the rows | bottom pad 8 | — | shared `SelectionCommandBar` | — | ZStack over the panel body; `ItemCount = verdict.Shown` | `AlbumExpand.cs:158-162` |
| drawer head cover | 28 × 28 | — | 4 | — | same `Surfaces.Artwork` seed as the grid card (never a second decode) | — | `AlbumExpand.cs:181` |
| drawer shimmer row | h 32 | 8/0/8/0, gap 12 | 4 | — | 16×11 + Grow(max 240)×11 + 30×11 + a reserved 32 lane, all `Tok.FillSubtleSecondary` | — | `AlbumExpand.cs:359-370` |
| drawer empty note | h = 2 rows (104) | 8/12/8/12, gap 12 | — | 13 `TextTertiary` | `Strings.Detail.Empty.NoTracks` + `Button.Standard(Strings.Common.Retry)` | — | `AlbumExpand.cs:343-353` |
| Follow pill (press) | — | — | — | — | — | press 0.96 (`ScaleStandard`), `Radii.FullAll`, `Role.Button`, hand cursor; `SkeletonProxy = FollowButton.SkeletonShape` | `SaveButton.cs:146-183` |
| facet trailing spacer | h 24 (`Spacing.XXL`) | — | — | — | — | inside the section, BEFORE the page's own 32 gap | `AlbumExpand.cs:766` |
| `AlbumNavAction` | default **34** (glyph 14.28); the drawer passes 28 | — | circle | — | 1px `StrokeControlDefault`, `Icons.OpenInNewWindow` | `Interaction.Subtle`, focusable, hand cursor, tooltip `"Go to album"` | `AlbumExpand.cs:52-60` |
| Upcoming card | — | 16 all (column) / 20/16/20/16 (band) | 8 | eyebrow 12/16/600 CS30 · `SurfaceDisplay` 40/52/400 · `PickQuote` 28/36 | `FillCardDefault` + accent .16/.05/0 | 1px `StrokeCardDefault` | `TopTracks.cs:182-252` |
| Upcoming band cover | 88 × 88 | — | 4 | — | decode 192 | — | `TopTracks.cs:204-210` |
| Latest release banner | — | 12 all, gap 12 | 8 | eyebrow 12/16/600 + `Ui.Subtitle` 20/28/600 | `FillCardDefault`, 1px `StrokeCardDefault` | — | `TopTracks.cs:270-311` |
| Latest cover | 72 × 72 | — | 4 | — | decode 144 | — | `TopTracks.cs:278-283` |
| facet header | h ≥40 | 0/4/8/4, gap 8 | 0 | `RailHeader` + meta 12/16 | spine 3 × ≥22 `_accent` r-Pill | transparent fill, `BrushTransitionMs 0`, `PinTop 56` | `AlbumExpand.cs:776-822` |
| facet chevron | 28 × 28 | margin-left 8 | — | — | stock Expander | `DisclosureChevron` 167 ms | `AlbumExpand.cs:788-792` |
| discography card | cellW × (cellW+50) | grid gap 16, row gap 20 | 8 | `TrackTitle` 14/20/600 + `TrackMeta` 12/16 | hover plate `FillCardDefault` + 1px + `Elevation.Card` | hover OffsetY −4, press 0.99 | `AlbumExpand.cs:418-425`, `MediaCard.cs:223-288` |
| expanded card | — | — | 8 | — | `BorderColor _accent`, `BorderWidth 2`, `Fill FillCardDefault` | — | `AlbumExpand.cs:547-550` |
| drawer panel | w = panel width | 12/6/12/6 | 8 | — | `Tok.FillCardSecondary`, 1px `StrokeCardDefault` | — | `AlbumExpand.cs:149-154` |
| drawer head | h 28 | gap 12 | — | Span 13/600 + 12/400 `TextSecondary` | play circle 26 r13 `_accent`, glyph 11 `PickContrast` | — | `AlbumExpand.cs:171-199` |
| drawer nav button | 28 (glyph 11.76) | — | circle | — | 1px `StrokeControlDefault`, glyph `TextSecondary` | `Interaction.Subtle` | `AlbumExpand.cs:52-60` |
| drawer row | h 32 (content 28) | — | 4 | title 13/600 | `RowHover` / `RowPressed` | — | `AlbumExpand.cs:220-243` |
| show-all row | h 32 | 8/0/8/0 | — | 12/600 | `Tok.AccentTextPrimary` | — | `AlbumExpand.cs:271-282` |
| caret | 16 × 8 | overlap 1 | — | — | fill `FillCardSecondary`, stroke 1 `StrokeCardDefault` | painted LAST | `AlbumExpand.cs:430-441, :653-663` |
| biography card | Grow 2 | 20/16/20/16, gap 16 | 8 | `RichText` 14 | `FillCardSecondary`, 1px `StrokeCardDefault` | — | `Biography.cs:34-50` |
| external-link pill | h ≈29 | 12/7/14/7, gap 6 | `Radii.Full` | 13/600 | 1px `StrokeCardDefault`, `HoverFill FillSubtleSecondary`, glyph `Link` 13 | — | `Biography.cs:80-90` |
| city row | h 34 | gap 4 | bar 2 | 14 / 13 | bar `_accent`, city `TextPrimary`, count `TextSecondary` | — | `Biography.cs:101-123` |
| stat tile | Basis 140, Grow 1 | 16 all, gap 4 | 8 | `Ui.Title` 28/36/600 + `Ui.Caption` | `FillCardSecondary`, 1px `StrokeCardDefault` | — | `Biography.cs:125-134` |
| tour banner | — | 16 all, gap 16 | 8 | eyebrow + 16/700 + 13 | `FillCardSecondary` → `FillCardDefault`; circle 44 `_accent` | Focus visual margin 2 | `Shelves.cs:27-51` |
| concert stub | — | 12 all, gap 12 | 8 | eyebrow (month) + 28/36/600 (day) + 14/20/600 + 12/16 | `FillCardSecondary` → `FillCardDefault` | — | `Shelves.cs:108-136` |
| merch card | — | 8/8/8/12, gap 8 | 8 (cover 4) | 13/600 + 13/700 | price `AccentTextPrimary` | hover 1.04 | `Shelves.cs:152-165` |
| gallery tile | w × w | — | 8 | — | decode 480 | hover 1.04 / press 0.96 | `Shelves.cs:175-177` |
| lightbox ground | viewport | — | 0 | — | `rgba(0,0,0,224)` | modal overlay, focus trap | `ArtistGalleryLightbox.cs:95-101` |
| lightbox top chrome | h 72 | 16/12/16/12, gap 12 | 0 | 14/600 + 12 | `GradientDown(rgba(0,0,0,158) → 0)` | `Transition ControlNormal` | `ArtistGalleryLightbox.cs:298-322` |
| filmstrip | h 84 | 14 all, gap 8 | thumb 4 | — | active border 2 `AccentTextPrimary`; inactive opacity 0.55 | `Transition ControlFast` | `ArtistGalleryLightbox.cs:324-355` |
| discography page frame | — | 36/24/36/108, gap 32 | — | `PageHero` 28/36/600 | — | — | `DiscographyPage.cs:129-142` |

---

## 4. Colour & material

Everything artwork-derived on this page keys off **one url**:
`PaletteImageUrl(a) = a.HeaderImage?.Url ?? a.Image?.Url` (`ArtistPage.cs:57-58`).

| # | Input → function (file:line) | Applied to | Transition |
|---|---|---|---|
| 1 | `Surfaces.ChromeSchemeFor(url)` (the **opposite** theme's grading — `Surfaces.cs:149-155`) → `WaveePalette.ChromeAccent` = `Vivid(Lift(Accent(s)))`, `S ≤ 0.08` ⇒ `Tok.AccentDefault` (`WaveePalette.cs:126-131`) | `_paletteAccent` → **every accent object on the page**: Play capsule fill, section rules, facet spine, selection pills, city bars, pivot underline, tour circle, drawer play circle, pick wash, upcoming gradient | Recomputed in `Body` each render; NOT watched — a late grading reaches the chart through its `accent.GetHashCode()` shelf key (`ArtistPopular.cs:190-193`) and everything else on the next page render. **0.3 must make this a watched leaf value** (§9). |
| 2 | `Surfaces.SchemeFor(url)` (the **theme's** grading) → `Lift(Accent(s))` (light) / `BackgroundDark(s)` (dark) (`CoverPaletteLeaves.cs:180-186`) | `CoverArtistBlendWash`: `GradientDown` α **light 0.20 → 0.06 → 0**, **dark 0.30 → 0.08 → 0**, over `HeroHeightFor(w) + 96`, boundary stop at `h/(h+96)` (0.821 Wide / 0.800 Medium / 0.825 Compact / 0.832 Narrow) | a cover-keyed leaf (`key = "artist-wash:"+uri`) — a grading arrival re-renders **only that node**. Clipped at 56 so it never bleeds through the band. |
| 3 | same scheme → `washAccent = Lift(Accent(pagePal))`, else the chrome accent (`CoverPaletteLeaves.cs:206-215`) → `Surfaces.ArtistHeroVeil(washAccent, axis)` = `Lerp(Tok.FillLayerDefault, accent, 0.16 light / 0.24 dark)`, stops **.96 / .92 / .35 / 0** left→right (`Surfaces.cs:173-192`) | the hero veil, **horizontal tiers only** | cover-keyed leaf `"artist-veil:"+uri`; a grading swaps the gradient without rebuilding `Banner`. |
| 4 | `Ready` + scheme → light `Lift(ToColor(s.TextBase)) @ α 0.05`; dark `TintedDark(s) @ α 0.14` (`CoverPaletteLeaves.cs:242-246`) | `ShellMaterial` flat tint (the whole window chrome) — `Wash: null`; the three-layer radial wash belongs to Home | published from the 0-size `ShellTint` leaf; claims on mount and on keep-alive reactivation, refreshes while owner, **never clears**. Gated on `artistReady` so the *pending* artist's avatar colour is not a first step before the header's. |
| 5 | per-cover, per-card: `Surfaces.SchemeFor(album.Cover.Url)` → `Lift(Accent(p))` | each discography `GridCard`'s `accent` (its hover/expanded accent) (`AlbumExpand.cs:538`) | resolved at cell build; the expanded card's 2-DIP border uses the **page** accent, not the card's. |
| 6 | `Surfaces.PlaceholderFor(url)` = `Lerp(neutral, tint, 0.55)` with neutral `#2A2A2A` dark / `#F2F2F2` light (`Surfaces.cs:57-92`) | the loading tile of every slot that goes through `Surfaces.Artwork` / `Surfaces.ArtworkFill` / `Surfaces.Shimmer`: chart art, discography `GridCard` covers, gallery tiles, the drawer-head cover, the pick's foot cover, the upcoming and latest-release covers, the filmstrip thumbs | `WatchedPlaceholder` re-paints exactly that tile when the grading lands — a paint-only `Prop.Of`, never a re-render. |
| 6b | **NOT the hero.** `Banner` passes `Placeholder() => Surfaces.ArtworkPlaceholder` — the FLAT theme neutral — to `HeroArt`, and uses the same flat fill for the no-image box (`Hero.cs:98-102`, `:359`). The pick's wide photograph does the same (`MediaCard.cs:381`), and the lightbox photo's placeholder is fully transparent (`ArtistGalleryLightbox.cs:142`). | hero photo, pick photo, lightbox photo | none — these three never tint and never re-paint. Do not "unify" them onto `PlaceholderFor` in 0.3: a 440-DIP cover-tinted slab across the top of the page is a different design from a neutral field, and the lightbox's transparent tile is what keeps the black ground unbroken. |
| 7 | `ColorContrast.PickContrast(_accent)` | the drawer head's play glyph (`AlbumExpand.cs:180`) and the Play capsule's label ink (`WaveeCta.cs:227`) | — |
| 8 | `WaveeAccent.Decor` = `Tok.AccentTextPrimary` | the tour eyebrow and the concert-stub month (`Shelves.cs:45, :125`) | theme token, not artwork |

**Light / dark differences that are real, not derived:** the veil pull (0.16 vs 0.24), the wash alphas (0.20/0.06 vs
0.30/0.08), the wash colour ROLE (`Lift(Accent)` in light vs `BackgroundDark` in dark — i.e. light tints with the
cover's *accent*, dark with its *background*), the shell tint role and alpha (TextBase @0.05 vs TintedDark @0.14), and
the artwork placeholder neutral. Everything else is a theme token.

**Colour washes off** (`WaveeSettings.ColorWashesEnabled == false`, read at `ArtistPage.cs:64, :293`): the blend wash
renders `new BoxEl()` (nothing) and the shell tint publishes `definite: true, tint: null` (the chrome eases to
neutral). The hero veil and the chrome accent are **not** gated — the veil still paints and Play keeps the cover's
colour. Preserve that asymmetry.

---

## 5. Motion

Motion samples the engine frame clock (`ScrollBinds` are evaluated per frame from the scroll sample;
`UseKeyframes`/`Transition`/`Animate` ride `AnimEngine`). **One exception in this surface**:
`ArtistGalleryLightbox.SpringZoomHome` uses `Task.Delay(260)` to swap the subtree back after the spring settles
(`ArtistGalleryLightbox.cs:277`) — a wall-clock timer. Port it as a frame-clock `UseTimeout` in 0.3.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| page enter (route swap) | page root | per `PageNavMotion.RecipeFor(direction)` — fade-through: the outgoing page fades in place over **120 ms** on a fast-out ease while the incoming one slides `Expressive.DistBase` 8 after a **90 ms** delay, so two full-bleed pages never mix at readable opacity | — | 120 out / enter delayed 90 | — | — | engine policy | `ContentHost.cs:147-153`, `PageNavMotion.cs:49-53` |
| Pending → Ready | `Skel.Region` content | opacity only (`FadeOnly`) | 0 → 1 | **250 ms** (`Expressive.Fast`) with `Easing.SmoothOut` — the CONTENT's own fade, seeded by `SkeletonReveal.Play` | — | `group = routeKey` (one settle window) | snaps | `ArtistPage.cs:136`, `SkeletonRegion.cs:166-168` |
| Pending → Ready | the shimmer orphan | opacity | 1 → 0 | `SkeletonStyle.ExitMs` = `Expressive.Fast` 250 ms, drawn UNDER the live tree | — | same window | — | `SkeletonRegion.cs:36` |
| hero copy mount | identity column | `Dy 12` + opacity | 12→0, 0→1 | 500 ms | `FluentDecelerate` (`EmphasizedEnter`) | — | `KeepFade` | `Hero.cs:81-82` |
| hero image ready | `HeroArt` root | `ScaleX/ScaleY` | 1 → 1.05 | 500 ms (`EmphasizedEnter`) | `FluentDecelerate` | — | keyframes; engine `ReducedSnap` | `Hero.cs:290-342` |
| hero image ready (cold) | the `ImageEl` | opacity cross-fade | 0 → 1 | 300 ms (`StandardEnter`) | — | — | keep fade | `Hero.cs:343` |
| hero image warm/settled | the `ImageEl` | — | — | `ImageTransition.None` | — | — | — | `Hero.cs:343` |
| scroll 0 → `cd` | expanded hero | `TransY` | 0 → −`cd` | scroll-linked | `Linear` | — | direct manipulation, no gate | `Hero.cs:121-123` |
| scroll `cd−96` → `cd` | expanded hero | `Opacity` | 1 → 0 | scroll-linked | `Linear` | — | — | `Hero.cs:124-126` |
| scroll 0 → `photoH` | photo box | `TransY` | 0 → `photoH×0.15` (66 at Wide) | scroll-linked | `Linear` | — | — | `Hero.cs:109`, `ScrollBindDsl.cs:105` |
| overscroll (pull) | photo box | uniform scale from the top | — | scroll-linked | — | — | — | `.StretchFromTop()` `Hero.cs:109` |
| scroll 0 → `cd` | hero root | `PresentedH` | `height` → 56 | scroll-linked | default | pinned (`PinTop 0`) | — | `Hero.cs:197`, `ScrollBindDsl.cs:118` |
| scroll `cd−44` → `cd` | compact band | `Opacity` 0→1 **and** `TransY` 4→0 | — | scroll-linked | `Linear` | — | `TransY` amplitude 0 (value, not branch) | `ArtistCompactBar.cs:101`, `ScrollBindDsl.cs:145` |
| band engages | magazine | `EdgeFade Top` | none → 24-DIP band | mount | — | gated on `compactInteractive` | — | `ArtistPage.cs:320-321` |
| active pivot item changes | underline | `Fill` | transparent ↔ accent | 167 ms (`WaveeMotion.Fast`) | brush ramp | — | colour cross-fade survives | `ContextBand.cs:311` |
| pivot click | page scroll | offset | → `sectionTop − 56` | engine `ScrollIntoView` | — | — | `animate: !Motion.ReducedMotion` | `ContextBand.cs:360-361` |
| active pivot item changes | pivot viewport | horizontal scroll | brings the item into view, margin 8 | engine | — | first resolve is not animated (`_scrollSelectionSeeded`) | not animated | `ContextBand.cs:337-346` |
| card hover | any media card | `OffsetY` | 0 → −4 | 250 ms (`ControlNormal`) | `FluentStandard` | — | `KeepFade` | `MediaCard.cs:68-74` |
| card press | any media card | `Scale`, `OffsetY` | 1→0.99, 0→−1 | 250 ms | `FluentStandard` | — | — | `MediaCard.cs:71` |
| card hover | hover plate | `Opacity` | 0 → 1 | 83 ms (`ControlFaster`) | `FluentStandard` | — | — | `MediaCard.cs:94-96` |
| chart row hover/press | row | fill + border + `PressScale 0.98` | — | engine interaction ramp | — | — | scale tier returns 1 (`WaveeMotion.cs:17-29`) | `ArtistPopular.cs:459-463` |
| chart page flip | shelf strip | offset glide + page-mandatory snap | — | shelf-owned | — | — | shelf | `ArtistPopular.cs:183` |
| chart selection | selection pill | `Opacity` bind over `SelectionModel.Version` | 0 ↔ 1 | compositor | — | — | — | `ArtistPopular.cs:283` |
| drawer open / close | `"disco-drawer"` slot | **Size only**, `SizeMode.Reflow`, `Anchor Leading` | 0 ↔ `SlotHeight` | **200 ms in / 150 ms out** | `SmoothOut` | `SuppressDescendantTransitions: true` | engine policy → 0 ms | `AlbumExpand.cs:459-463` |
| drawer album switch | inner panel | **Opacity only** | 0 ↔ 1 | **150 ms in / 100 ms out** | `EaseInOut` | — | — | `AlbumExpand.cs:465-468` |
| drawer open | page scroll | offset | → `cardTop − topInset` (`ExpandedReveal.AlignTop`) | engine | — | keyed on the expanded index alone (height is final on the click frame) | — | `AlbumExpand.cs:516-520` |
| facet expand / collapse | `Expander` | disclosure | — | 333 ms expand / 167 ms collapse | `FluentPopOpen` / `FluentDisclosureCollapse` | — | engine policy | `MotionTok.cs:180-182` |
| facet content resize | — | **disabled** | — | — | — | — | — | `AlbumExpand.cs:764-765` |
| grid sticky clip | grid | `EdgeFade Top` | none → 24 | on clip engage | — | — | — | `AlbumExpand.cs:741-743` |
| countdown tick | `PreReleaseCountdown` | digits | — | `UseInterval` (auto-paused when parked/minimised) | — | only inside 14 days | — | `TopTracks.cs:175-180` |
| lightbox chrome idle | top chrome + filmstrip | `Opacity` | 1 → 0 | 250 ms (`ControlNormal`) | — | after **2600 ms** of pointer stillness | — | `ArtistGalleryLightbox.cs:28, :93, :304-305` |
| lightbox pointer activity | chrome | `Opacity` | 0 → 1 | 250 ms | — | timer restarts | — | `ArtistGalleryLightbox.cs:359-363` |
| lightbox enter zoom | zoom carrier | Scale + Translate | 1 → 2.2 about the anchor | spring `response 0.32, ζ 0.9` | — | — | — | `ArtistGalleryLightbox.cs:203-269` |
| lightbox unzoom | zoom carrier | Scale + Translate | → 1, 0, 0 | same spring, then subtree swap after 260 ms | — | — | — | `ArtistGalleryLightbox.cs:271-278` |
| lightbox page | `FlipView` | — | — | control-owned | — | — | — | `ArtistGalleryLightbox.cs:133` |
| lightbox photo reveal | `ImageEl` | opacity | 0 → 1 | **140 ms** | — | — | — | `ArtistGalleryLightbox.cs:144` |
| filmstrip active change | thumb | border + opacity | 0.55 ↔ 1 | 150 ms (`ControlFast`) | — | — | — | `ArtistGalleryLightbox.cs:339` |
| equalizer (now playing) | `NumberCell` | bars | — | engine `WaveeEqualizer` | — | pauses on row hover (`hoverPaused`) | — | `ArtistPopular.cs:479-484` |
| pivot overflow | the pivot's own `ScrollEl` | alpha edge fade | — | `AutoEdgeFade = true`, `SuppressScrollBar`, `EdgeCues.None` | — | — | — | `ContextBand.cs:266-279` |
| follow / play / shuffle / radio hover | hero capsules + circles | `Scale` | 1 → 1.04, press 0.96 | engine interaction ramp | — | — | tier returns 1 | `WaveeCta.cs:104-106, :146-148`, `SaveButton.cs:154` |
| gallery tile · merch card · filmstrip thumb hover | the tile | `Scale` | 1 → 1.04 (gallery also press 0.96) | engine ramp | — | — | tier returns 1 | `Shelves.cs:157, :176`, `ArtistGalleryLightbox.cs:341` |
| facet header brush | `Expander` header | fill / border | — | **0 ms** (`BrushTransitionMs = 0`) — the pinned header must not cross-fade a fill it does not have | — | — | — | `AlbumExpand.cs:785` |
| shell tint arrival | window chrome | `ShellMaterial` flat tint | — | shell-owned ramp | — | published from a 0-size leaf on `(url, known, theme, ready, disabled, apply)` change + on keep-alive reactivation | — | `CoverPaletteLeaves.cs:254-261` |
| wash / veil grading arrival | the wash + veil leaves | `Gradient` | — | re-render of ONE node (not a brush ramp — the gradient is rebuilt) | — | — | — | `CoverPaletteLeaves.cs:171-217` |

---

## 6. Interaction

**Hero**
- `▶ Play` → `svc.Player.PlayAsync(artistUri, 0)`. Label `Strings.Artist.Play` ("Play").
- `⤨ Shuffle` → `SetShuffleAsync(true)` **then** `PlayAsync(artistUri, 0)`. Tooltip `Strings.Detail.Shuffle`.
- `♡ Follow` → `LibraryBridge.ToggleSaved(uri, name)`; label `Strings.Artist.Follow` / `.Following`; the pill's border
  becomes `Tok.AccentDefault` and the glyph `HeartFill` while following.
- `((· Radio` → `RadioLaunch.Start(player, uri, name, go)`: park/play a real radio (never a replay of the artist
  context), then a toast — success `Strings.Menu.RadioStarted` with an `Strings.Menu.OpenRadioPlaylist` action
  navigating to `pl:{seededUri}`; null result → `Strings.Menu.RadioUnavailable` (warning); a THROWN
  `StartRadioAsync` → the raw `ex.Message` as an **Error** toast (`RadioLaunch.cs:28`) — the one place this surface
  shows an exception string to the user, and the one 0.3 should replace with a friendly line.
  Tooltip `Strings.Artist.ArtistRadio`.
- Hero hit-testing flips with the collapse: `HitTestVisible = !compactCanHit` on the expanded arm and
  `HitTestVisible = canHit` on the band — a single edge at the 56-DIP floor, driven by the 0-height sentinel.

**Context band**
- Title: not interactive. Pivot item: `Role = AutomationRole.Tab`, focusable, hand cursor; click scrolls that section
  under the band. Actions: `Play` (primary ink) and `Follow/Following` (accent ink when on), both
  `AutomationRole.Button`, focusable, no hover scale.

**Chart rows** — the app-wide track contract:
- single click **selects** (`ItemsSelectionMode.Extended`: plain = replace, Ctrl = toggle, Shift = range from anchor);
- double click **plays** — `PlayContextTrackAsync(artistUri, PlaybackContextTrack(t.Uri), index)`, **by URI** with the
  index as a fallback only (the server's popular list has its own order);
- hover over the `#` cell swaps the number for ▶ / ⏸ (now-playing row); clicking it plays/toggles;
- `♡` toggles saved; drag lifts the whole selection when the pressed row is in it;
- right-click / `Menu` / long-press → `TrackContextMenu.BuildSingle` (see `01-track-row.md`);
- the "feat." credit: first featured name is a clickable span → `artist:{uri}`; `+N` opens `ArtistMoreButton`'s flyout
  of the rest, each navigating. Shown only when the page artist is in the credits **and** someone else is too.
- tooltip on the play count: the exact `N,NNN plays` (the row shows the compact form).

**Discography grid**
- card click **toggles the drawer** (same uri ⇒ collapse); it does **not** navigate;
- the card's hover ▶ FAB plays the album; the hover `↗` FAB and the drawer head's `↗` (tooltip "Go to album",
  hardcoded) navigate to `album:{uri}`; the drawer head's title also navigates;
- the drawer's play circle plays the album from the top; a drawer row: single click selects, double click plays from
  that index, `⋯` raises the row's context menu, swipe (`RowSwipe.Wrap`) exposes `ToggleLike` / `AddToQueue`;
- `Show all N tracks` → `album:{uri}`;
- card right-click → `Menus.CardAttach` (W27).

**Facet header** — the whole header row is the `Expander`'s toggle (stock keyboard/`Space`/`Enter` semantics).

**Gallery**
- tile click opens the lightbox at that index; `Escape` closes — **unless zoomed**, where it unzooms and vetoes the
  dismissal (`ClosingAction`); `←/→` page (and unzoom first); `Ctrl`+wheel or pinch enters zoom; in zoom, drag pans
  (4-DIP slop), two-finger scroll pans, click (without drag) springs home; filmstrip thumb click selects.
- `Export image` opens the native Save-As picker, downloads the original CDN bytes, and toasts success/failure.

**Settings this surface reads (and must keep reading in 0.3)** — all through `svc.Settings`, none through an
environment switch, and `ArtistPage.Render` takes `_ = AppearancePrefs.Epoch.Value` so an appearance change re-renders
the page (`ArtistPage.cs:63`):

| setting | read at | what changes |
|---|---|---|
| `WaveeSettings.ColorWashesEnabled` | `ArtistPage.cs:64, :293` | the blend wash renders nothing; the shell tint publishes `definite:true, tint:null`. The hero veil and the chrome accent are deliberately NOT gated (§4). |
| `WaveeSettings.TrackRowStyle` (0 Modern / 1 Classic) | `TopTracks.cs:40`, `ArtistPopular.cs:117` | chart row 56→48, art pinned 40, square corners, no border, a hairline per row, cell/header gaps 8, selection pill corners 0, `ClassicExplicitBadge`, and the now-playing accent spreads to the whole sub run + duration. Resets the hysteresis tier (`_tier = null`). |
| `AppearancePrefs.TrackArtworkHidden` | `TopTracks.cs:39`, `ArtistPopular.cs:116` | the chart row's art cell is not built at all (W10). |
| `Motion.ReducedMotion` | every `ScaleTier` accessor, `ContextBand.cs:344, :361` | hover/press scales collapse to 1; the pivot's click scroll and its bring-into-view snap instead of animating. |

**Accessibility names / roles present today**: `AutomationRole.Button` on chart rows, drawer rows, show-all, tour
banner, concert stub, gallery FABs, the band's actions; `AutomationRole.Tab` on pivot items; `Focusable` + engine focus
ring on the tour banner, concert stub, drawer nav button, pivot items, band actions. **Gaps to close in 0.3**: the hero
photo has no accessible name; the chart rows have no per-row name beyond their content; the external-link pills are
`BoxEl`s with **no `OnClick` at all** (`Biography.cs:83-89`) — they look like buttons and do nothing.

**Tooltips**: Shuffle (`Strings.Detail.Shuffle`), Radio (`Strings.Artist.ArtistRadio`), play count (exact count,
hardcoded `" plays"`), drawer nav (`"Go to album"`, hardcoded), face-pile (`"View all artists"`, hardcoded).

**Localisation.** Every visible artist-page string has a key under `artist.*` / `detail.*` in
`src/apps/Wavee/assets/loc/en-US.json` — `artist.play`, `artist.follow`, `artist.following`, `artist.verified`
("Verified Artist"), `artist.worldRank` (`#{rank} in the world`), `artist.metaMonthly`, `artist.metaFollowers`,
`artist.topTracks`, `artist.upcoming`, `artist.latestRelease`, `artist.albums`, `artist.singlesEps`,
`artist.compilations`, `artist.appearsOn`, `artist.musicVideos`, `artist.playlistsDiscovery`,
`artist.upcomingConcerts`, `artist.merch`, `artist.biography`, `artist.gallery`, `artist.profileFacts`,
`artist.listenedMostIn`, `artist.artistPick`, `artist.artistRadio`, `artist.view`, `artist.feat`,
`artist.trackCount`, `artist.releaseCount`, `artist.releaseMeta`, `artist.stat.*`, `artist.fallbackName`,
`detail.fansAlsoLike`, `detail.releasesOn`, `detail.discography.showAllTracks`, `search.typeArtist`.
**Strings that bypass their own keys in 0.2.9 and must not in 0.3**: the whole gallery lightbox
(`artist.galleryExport`, `.galleryExporting`, `.galleryExportTitle`, `.galleryImages`, `.galleryAllFiles`,
`.galleryExported`, `.galleryExportFailed` all exist and are unused), `" plays"`, `"Go to album"`,
`"View all artists"`, and `DiscographyRoute.FacetTitle`/`FacetWord`, which return raw English
(`DiscographyPage.cs:30-44`) while `artist.albums` / `artist.discoCount*` exist.

---

## 7. Data & readiness in 0.3 terms

`A` = the `ArtistTable`; `E` = `Entities.Current.Edges`. Field-group names below are the ones this chapter proposes
(§DATA GAPS); the readiness predicate is what the page checks **before** it renders the element at all — anything not
yet Known renders the derived skeleton, never a half-filled row.

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| hero photo | `a.HeaderImage ?? a.Image` | `A.Header[s]` ?? `A.Image[s]` (StringId) | `a.Knows(ArtistFields.Overview)` |
| name | `a.Name` | `A.Name[s]` | `Knows(Identity)` |
| verified | `a.Verified` | `A.Flags[s] & ArtistFlags.Verified` | `Knows(Overview)` |
| world rank / monthly / followers | `a.WorldRank`, `.MonthlyListeners`, `.Followers` | `A.WorldRank[s]` (ushort), `A.Monthly[s]`, `A.Followers[s]` (uint) | `Knows(Overview)` — the three render as a unit or not at all |
| hero bio sentence | `FirstSentence(StripHtml(a.Bio))` | `A.BioFirstSentence[s]` — a StringId **computed at commit** | `Knows(Overview)` |
| biography body | `a.Bio` → `RichText.Of` | `A.Bio[s]` (StringId, HTML) | `Knows(Overview)` |
| accent / wash / shell tint | `CoverColorPlane` via `Surfaces.*SchemeFor` | `Palette.Scheme(A.Header[s] ?? A.Image[s])` (GAP #1) | never gates the page; the semantic accent is the fallback |
| chart rows | `a.TopTracks` merged with the extension, + kind-185 counts | `E.ArtistPopular.Targets(s)` → `Track` handles | `a.Knows(ArtistFields.Chart)` **and** `E.ArtistPopular.State(s) == complete` **and** every target `Knows(TrackFields.Row)` — i.e. Identity + PlayCount + Availability. Otherwise the whole chart is the shimmer. **This is the owner's "no rows without play counts" gate.** |
| chart artwork | `t.Image` | `Track.ImageId` | inside `TrackFields.Row` |
| explicit / video dots | `t.IsExplicit`, `VideoPresence.HasVideo(t)` | `TrackFlags.Explicit`, `TrackFlags.HasVideo` (plan §4.2 `Video` group) | folded into the chart gate — today they arrive late and force a shelf remount |
| "feat. X +N" | `t.Artists` vs the page uri | `t.ArtistSlots` (CSR) | `Knows(TrackFields.Artists)` |
| Artist pick | `a.Pinned` (11 fields) | `A.Pick[s]` → `ArtistPickTable` row (GAP #5) | `Knows(Overview)`; absent ⇒ the section is not built |
| Upcoming | `a.Extras.PreRelease` + `IsUpcoming` | `A.PreRelease[s]` → sparse row (GAP #6); `IsUpcoming` **derived at commit** into a flag | `Knows(Overview)` |
| Latest release | `a.LatestRelease` | `A.Latest[s]` (album slot) + `Album` identity of that slot | `Knows(Overview)` **and** `Entities.Album(A.Latest[s]).Knows(AlbumFields.Identity \| Release)` |
| Albums / Singles / Compilations grids | `a.TopAlbums` filtered by `Kind` + `a.{X}Total` | `E.ArtistAlbums / ArtistSingles / ArtistCompilations` (three edges, GAP #8) | section shows when `State != unknown`; the GRID shows when `State == complete` **and** every target `Knows(AlbumFields.Card)` = Name + Cover + Year/ReleaseDate + TrackCount + Kind. `Total` from the edge drives the shimmer count while incomplete. |
| card subtitle "date · N tracks" | `DiscoGrid.AlbumMeta` | the same pure function over album columns | inside `AlbumFields.Card` |
| era band label | `DiscographyEraBands.PlanAlbums(items)` | the same pure function over `ReadOnlySpan<ushort>` years | needs the complete facet (it is a whole-catalogue grouping) |
| drawer rows | `GetAlbumAsync(uri)` → `album.Tracks` | `E.AlbumTracks.Targets(albumSlot)` + `Track.Title/DurationMs` | `DrawerVerdict` already encodes it: `loading = !match && pending` becomes `!(E.AlbumTracks.State(album) == complete)`; `Total` from `A.TrackCount` |
| Appears on | `a.AppearsOn` | `E.ArtistAppearsOn` (plan §4.3 ✓) | `State == complete` + `AlbumFields.Card` on every target |
| Related / fans | `a.Extras.Related` else `store.Artists` pool | `E.ArtistRelated` (plan ✓) | `State == complete` + `ArtistFields.Identity` on every target |
| Music videos | `a.Extras.MusicVideos` | `E.ArtistVideos` (GAP #11) | `Knows(Overview)` |
| Playlists | `a.Extras.Playlists` | `E.ArtistPlaylists` (GAP #10) + `Playlist` identity | `Knows(Overview)` + `PlaylistFields.Identity` on targets |
| Concerts | `a.Extras.Concerts` | `E.ArtistConcerts` → `ConcertTable` (plan has the table; GAP #16 is the edge) | `Knows(Overview)` |
| Tour banner | `a.Extras.Tour` (already derived server/fake-side) | `A.Tour*` StringIds **derived at commit** (GAP #15) | `Knows(Overview)` |
| Merch | `a.Extras.Merch` | `E.ArtistMerch` → `MerchTable` (GAP #12) | `Knows(Overview)` |
| Top cities | `a.Extras.TopCities` | `E.ArtistCities` payload edge (GAP #13) | `Knows(Overview)` |
| External links | `a.Extras.ExternalLinks` | `E.ArtistLinks` payload edge (GAP #14) | `Knows(Overview)` |
| Gallery | `a.Extras.Gallery` | `E.ArtistGallery` payload = image StringIds (GAP #9) | `Knows(Overview)` |
| Profile-facts tiles | mixed: stats + **the in-memory album/single counts** + concerts + related | stats columns + `E.ArtistAlbums.Total` / `ArtistSingles.Total` + `E.ArtistConcerts.Length` + `E.ArtistRelated.Length` | `Knows(Overview)` + the two facet edges' `Total` |

**Demand on mount** (plan §4.13's rule — pages demand their whole model; no page-side visible windows):

```csharp
UseEffect(() =>
{
    Entities.Ensure(_a, ArtistFields.All);                       // one pathfinder overview + the chart transport
    Entities.EnsureEdges(_a, EdgeKind.ArtistPopular, EdgeKind.ArtistAlbums, EdgeKind.ArtistSingles,
                             EdgeKind.ArtistCompilations, EdgeKind.ArtistAppearsOn, EdgeKind.ArtistRelated,
                             EdgeKind.ArtistGallery, EdgeKind.ArtistPlaylists, EdgeKind.ArtistVideos,
                             EdgeKind.ArtistConcerts, EdgeKind.ArtistMerch, EdgeKind.ArtistCities,
                             EdgeKind.ArtistLinks);
    Entities.EnsureRows(_a.PopularSlots, TrackFields.Row);       // ONE 300-uri batch, not per visible range
    Entities.EnsureRows(_a.AllReleaseSlots, AlbumFields.Card);   // every facet, one batch — the grid virtualizes
});                                                              // RENDER, not FETCH
```

The drawer keeps its single extra demand: `Entities.EnsureEdges(album, EdgeKind.AlbumTracks)` on expand, with the
store's synchronous warm read standing in for `AlbumLoader.Peek` so a re-opened album is complete on the click frame.

### DATA GAPS

Everything this surface paints that the plan's model does not hold. "Source" is where 0.2.9 gets it.

| # | Element | 0.2.9 source | Proposed 0.3 column / edge |
|---|---|---|---|
| 1 | **Cover palette (the whole accent/wash/tint system)** | `SpotifyLive/CoverColorPlane.cs` (556) — an image-keyed plane of 5-role schemes (light + dark halves) fed by `getDynamicColorsByUris` and extension kind 179, with per-url `Watch` signals | **Not in the plan at all, and every chapter needs it.** **`Entities/Palette.cs` CORE** (arbitration 2026-09-12, A5 — this chapter’s earlier `Platform/Design.cs` answer is withdrawn): `struct Scheme(uint BackgroundBase, BackgroundTintedBase, TextBase, TextSubdued, TextBrightAccent)`, the image-keyed table (`00-design-system.md` §9.5 spells it as a `Column<PaletteEntry>` over a small side table with a 180-day hit / 7-day miss TTL; `07-liked-songs.md` G1 spells the same thing as eight `Column<uint>`s + a `Known` bitmask + `Signal<uint> Changed`), the key identity, `Palette.Watch(StringId)` and `Palette.Ensure(span of image ids)`. **`Entities/Palette.Host.cs` SHELL** holds the debounced pump, the `Spotify.Api.GetDynamicColorsByUris` filler and the persisted store. `Palette.ChromeAccent(StringId)`, `Palette.PageTone(StringId)` and `Palette.Placeholder(StringId)` are the three readers this page uses. **Why not `Platform/Design.cs`:** it is a per-image, TTL’d, fetched, persisted, `Ensure`-able plane keyed like an entity — `Design.cs` is a token/colour-maths file with no store, no fetch and no readiness, and five other chapters (00, 03, 07, 12, 21) already ground on `Entities/Palette.*`. **Wave:** the CORE lands in **Wave 1 (owner A)** because those five chapters’ grounds depend on it; the Host and the page-tone plane land in **Wave 4 (owner L)**. |
| 2 | Monthly listeners, followers, world rank, verified | `queryArtistOverview` → `Artist` record | `Column<uint> Monthly, Followers; Column<ushort> WorldRank; ArtistFlags.Verified` |
| 3 | Bio + its first sentence | overview `profile.biography.text` (HTML) | `Column<StringId> Bio` + `Column<StringId> BioLead` (stripped + first sentence, computed once at commit — never per frame) |
| 4 | Header image (landscape) distinct from the avatar | overview `visuals.headerImage` | `Column<StringId> Header` |
| 5 | Artist pick (11 fields) | overview `profile.pinnedItem` (+ `itemV2`, `backgroundImageV2`) | sparse `ArtistPickTable` + `Column<int> Pick` (0 = none): `Eyebrow, Title, Subtitle, Comment, Cover, Uri, ItemUri, Background` (StringId), `ItemKind` (byte), `ReleaseAt` (int) |
| 6 | Pre-release | overview `preReleaseV2` (+ extended-metadata kind 138 for the album↔prerelease pair) | sparse `ArtistPreReleaseTable` + `Column<int> PreRelease`: `Uri, Name, Cover, Type` (StringId), `ReleaseAt` (int), flag `Upcoming` derived at commit |
| 7 | Latest release | `discography.latest` | `Column<int> Latest` (album slot) |
| 8 | Per-facet discography + totals | `discography.{albums,singles,compilations}.totalCount` + paged `items` | **three** `EdgeTable<NoEdge>`: `ArtistAlbums`, `ArtistSingles`, `ArtistCompilations` (each with its own `State`/`Total`). The plan's single `ArtistReleases` + `DiscographyEdge(Kind)` cannot express three independently paged facets with three totals. |
| 9 | Gallery images | overview `visuals.gallery.items` | `EdgeTable<StringId> ArtistGallery` — payload IS the image id, `Targets` unused (the `TrackTags` precedent) |
| 10 | Playlists & discovery (uri + subtitle) | overview `relatedContent.featuring/discoveredOn` | `EdgeTable<StringId> ArtistPlaylists` — targets = playlist slots, payload = the subtitle StringId |
| 11 | Music videos (thumb + duration per track) | overview `videos` | `EdgeTable<VideoEdge>(StringId Thumb, int DurationMs) ArtistVideos` — targets = track slots |
| 12 | Merch | overview `merch.items` | `MerchTable` (Name, Price, Image, ShopUrl StringIds) + `EdgeTable<NoEdge> ArtistMerch` |
| 13 | Top cities | overview `stats.topCities` | `EdgeTable<CityEdge>(StringId City, StringId Country, uint Listeners) ArtistCities`, targets unused |
| 14 | External links | overview `profile.externalLinks` | `EdgeTable<LinkEdge>(StringId Name, StringId Url, byte Kind) ArtistLinks`, targets unused |
| 15 | Tour banner (eyebrow/headline/subline/isLive) | **derived** from the concert list (`FakeData.TourBannerFor`, and the mapper for live data) | derive at commit into `Column<StringId> TourEyebrow, TourHeadline, TourSubline` + `ArtistFlags.TourLive`. Derived facts live on the model — the UI must not recompute this per render. |
| 16 | Artist → concerts | overview `goods.events` / ArtistConcerts | `EdgeTable<NoEdge> ArtistConcerts` (the plan has `ConcertTable`, not the edge) |
| 17 | Album card facts | `getAlbum` / overview items | `AlbumFields.Card` = `Name \| Cover \| Year \| ReleaseDate \| ReleaseDatePrecision \| TrackCount \| Kind`. The plan's §2 never enumerates `Album`'s columns; `ReleaseDate`+precision and `Kind` are load-bearing here (the card subtitle, the era bands, the facet split). |
| 18 | Chart order + play counts | overview seed ∪ `artist-top-tracks-extensions`, then kind 185 | `EdgeTable<NoEdge> ArtistPopular` (plan ✓) + `TrackFields.PlayCount` (plan ✓). What is missing is the **readiness bit**: `ArtistFields.Chart`, set only when the chart transport has answered (0.2.9 needed a whole `ChartFetchedAt` column to express this — `Models.cs:100-109`). |
| 19 | "Fans also like" fallback pool | `store.Artists` (the followed-artists list) | `E.FollowedArtists.Targets(User.Me)` (plan ✓) — the page takes the first 12 excluding itself |
| 20 | Video presence on a chart row | `VideoPresence` over extension kind 99 | `TrackFlags.HasVideo` (plan §4.2 ✓) |
| 21 | Discography paging | `VirtualCollection` pages of 60 + a `limit 0` total probe | `EdgeTable.ReplacePage(parent, offset, …)` + `Total` (plan ✓). The **probe** (a total-only request that resolves same-tick from cache) has no equivalent in `Fetch.Plan` — add `Fetch.PlanTotal(edge, parent)`. |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `ArtistHeroLayout` | `Features/Detail/ArtistHeroLayout.cs` (117) | tier + hysteresis, hero/photo heights, gutters, copy max widths, photo fade band, parallax fraction, collapse distance, fade/reveal starts, blend backdrop height + boundary | `src/apps/Wavee.Tests/ArtistHeroLayoutTests.cs` (9 facts) | **CORE section of `Entities/Artist.UI.cs`** (engine-free `static class ArtistHeroLayout`) |
| `ArtistPopularLayout` | `Features/Detail/ArtistPopularLayout.cs` (78) | chart-row geometry tier (art 44/40, duration on/off, subtitle stacked) with one-directional 24-DIP hysteresis; `NominalFor` for a fresh chart | `ArtistPopularLayoutTests.cs` (10 facts) | **CORE section of `Entities/Artist.UI.cs`** |
| `AlbumDrawerVerdict` / `DrawerVerdict` | `Features/Detail/AlbumDrawerVerdict.cs` (48) | the ONE drawer decision: identity match, loading vs ready-empty, columns, shown, cap, show-all row, rows, panel + slot heights | `AlbumDrawerVerdictTests.cs` (10 facts) | **CORE section of `Entities/Artist.Discography.cs`** |
| `DiscographyEraBands` | `Features/Detail/DiscographyEraBands.cs` (218) | resize-stable era grouping: year runs → buckets (widths 1/2/5/10/20/50/100, seeded from `clamp(round(√itemCount), 3, 8)`) → coalesce sparse → ≤8 bands, ≥5 items each. **Four** bail-outs, all of which must survive the port: `itemCount ≤ 5`, `distinct years < 3`, `covered ≤ 5`, `planned.Count < 2` — each returns `null`, i.e. the header keeps the plain "N releases". Label grammar has **five** arms, not four: `"1994"` (one year) · `"1990s"` (an exact decade) · `"1990s–1970s"` (multi-decade, **newest first**) · `"1994–1991"` (the plain newest–oldest fallback for any other span) · `"1990s and earlier"` (`openEnded`, reachable ONLY through `Plan(…, provisional: true)`, which `PlanAlbums` never passes — so today it is **dead on the artist page**; decide in 0.3 whether a paged facet should set it) | `DiscographyEraBandsTests.cs` (8 facts) | **CORE section of `Entities/Artist.Discography.cs`** — change the input to `ReadOnlySpan<ushort> years` |
| `ContextBandLayout` | `Features/Detail/ContextBandLayout.cs` (179) | band height/gaps/underline geometry, label-width estimate, the scroll spy (`ActiveSection`, `SpyLine`, `IsAtScrollEnd`, `ScrollTargetFor`) | `ContextBandLayoutTests.cs` (15 facts) | **shared** — CORE section of **`Entities/Detail.cs`** (the band's UI half in `Entities/Detail.UI.cs`); the artist page only consumes it. Matches `03-detail-frame.md:880`. **Not** `Shell/Shell.cs`: `ClipFadeBand` *is* `DetailVerticalLayout.StickyFadeBand` (`ContextBandLayout.cs:44`) and `Height 56` *is* `CompactIdentityHeight` (`DetailVerticalLayout.cs:78`), so a shell home imports the detail frame's vertical layout into the shell file. (`30-appearance-preferences.md:1448` still files it under `Platform/Design.cs` — a third answer; this chapter and 03 agree on `Entities/Detail.cs` and that is the one to take.) |
| `DetailLayoutBreakpoints.ModeFor` | `Features/Detail/DetailLayoutBreakpoints.cs` | the biography band's wide/stacked mode (820/844) | (detail-table tests) | **shared** — CORE of `Entities/Album.cs`-adjacent layout, per `03-detail-frame.md` |
| `ArtistPopularTracks` | `src/apps/Wavee.Core/Library/ArtistPopularTracks.cs` | `OverviewSeedCap 10`, `ExtendedCap 50`, the seed∪extension merge order, `WithPlayCounts`, `UrisWithoutPlayCount` | `ArtistPopularTracksTests.cs` (7 facts) | **CORE section of `Entities/Artist.cs`** — in 0.3 the merge becomes the commit-time edge `Replace`, but the ORDER contract (seed keeps its order and its counts at the head; extension-only tracks append; dedupe by uri, seed wins; cap 50) is the same and must keep its tests |
| `DiscographyRoute` | `Features/Detail/DiscographyPage.cs:18-45` | `disco:{kind}:{uri}` make/parse (kind is the single digit at index 6, uri from 8) + facet titles/words | `DiscographyPaginationTests.cs` (13 facts, with `VirtualCollection` source-included) | **CORE section of `Shell/Shell.cs`** (`DeepLink`) + `Artist.Discography.cs`; the titles must move to loc keys (§6) |
| `ArtistPage.UriOf` / `PaletteImageUrl` | `ArtistPage.cs:54-58` | route → uri; which image the palette keys on | — | CORE of `Artist.cs` (`PaletteImageId`) |
| `FirstSentence` / `StripHtml` | `ArtistPage.Hero.cs:262-282` | the hero's one-sentence bio (split at `". "` only when the index > 20) | — (no test today) | CORE of `Artist.cs`, run **at commit** into `BioLead`; add a test |
| `DiscoGrid.AlbumMeta` / `ReleaseDateLabel` / `ReleaseYearLabel` | `ArtistPage.AlbumExpand.cs:558-588` | the card subtitle + the `YEAR`/`MONTH`/day date formats (culture-aware) | — (no test today) | CORE of `Album.cs` (shared with `05-album.md`); add a test |
| `ArtistPage.KindLabel` | `ArtistPage.TopTracks.cs:328-334` | `AlbumKind` → the localised badge string | — | CORE of `Album.cs` |

---

## 9. Re-author notes

**Must not be simplified.**

1. **The two hero arms are two compositions, not one with a flag.** Horizontal = ZStack (photo, veil, copy centred);
   stacked = column (photo band, identity band justified End). Collapsing them into one "responsive" arm loses the
   veil's whole reason for existing.
2. **The chart is a paged shelf, not a list.** Five rows, two columns, page-mandatory snap, URI keys. A flat 10-row
   list (what the v2 design doc proposes) is a different page.
3. **The drawer's ONE verdict.** The panel, the reserved slot height and the bring-into-view scroll must all read the
   same `DrawerVerdict`. Re-deriving any of them is exactly the bug the verdict was written to kill.
4. **`ExpandedReveal.AlignTop` + the caret + the 2-DIP accent border.** All three answer "where did it open"; dropping
   any one returns the "unclear where it opens, still jumpy" report.
5. **The band paints nothing.** Any fill — flat, translucent, flattened-over-Mica — is the bug that shipped a black
   slab on dark wallpapers. The clip is the contract.
6. **`SectionAnchors.Reset()` is called from Render, never an effect** — `OnRealized` fires during the reconcile of
   *this* render; an effect-scheduled reset wipes the registrations the same frame made them (`ContextBand.cs:39-43`).
7. **Every section keyed**, and `related` / `fans` deliberately DIFFERENT keys (they are alternatives holding
   different data).
8. **`Skeletonized(false)`** on `HeroActions`, the compact bar, the pick's trailing button, the latest-release
   actions, the upcoming actions — a hover/press affordance is not skeleton content, and the band is invisible at
   offset 0 anyway (it would otherwise shimmer a phantom link row above the hero).
9. **The pick owns the rail; upcoming moves to its own band.** Two stacked cards in the rail out-run Top tracks and
   leave a dead band — tried and rejected (both in the code and in `artist-pick-zune-concepts.html` §3, whose S3
   "two footer rows" concept the code explicitly refuses, `MediaCard.cs:296-298`).
10. **One play-count format.** Compact in the row, exact in the tooltip. The width-dependent format is the defect
    `ArtistPopularLayout`'s doc comment exists to record.
11. **The band is ONE implementation with two consumers — it is not this page's to write.** `SectionAnchors`,
    `ContextBand` and `ContextPivot` land once in `Entities/Detail.UI.cs` (+ `ContextBandLayout` in `Entities/Detail.cs`)
    during Wave 4, and owner N *calls* them; see the ownership note in §1.2 for the evidence and the Wave ordering.
    "The artist band is different enough, I'll just write my own 56-DIP row" is the failure mode: the two arms differ
    only in content and gutter (36/32/16 here vs `TrackRow.PadXFor` 16/12/8 there), and every geometric number —
    height, hairline, clip inset, feather, title cap, gaps, underline, the whole scroll spy — is shared. A second copy
    drifts on the first token change, the way the 0.2.7 hero and rail did.

**Traps.**

- **Props freeze at mount** — the full list is in §1.2. The two that bite hardest: `PreSaveButton` (an unkeyed one
  keeps pre-saving whichever uri was current at mount) and `PagedShelf` (its `cardAt`/`keyOf`/`customPager` freeze,
  which is why `ArtistPopular` writes `_live`, `_go`, `_total` as plain fields *before* building the shelf and the
  shelf carries a composite remount key).
- **A key on a component's ROOT element is inert.** `TreeReconciler.ReconcileSingleChild` pairs by `ElementTypeId`
  alone; only `ReconcileChildren` reads `Key`. `ArtistPopular` wraps its shelf in a pass-through `BoxEl` for exactly
  this reason (`ArtistPopular.cs:145-151`) — the chart froze at its seed count before that wrapper existed.
- **`ReuseGuard`**: the 0.3 chart must prove that a table publish re-skins rows without remounting them. That is the
  whole point of dropping the `"chart:{total}:{counted}:facts=…"` key — in 0.3 the row reads the edge live, so the
  key can be the uri alone.
- **Zero-allocation scroll frames vs per-row richness.** 0.2.9's reconciliation: (a) exact-size arrays in the row
  builder, never `List` + `ToArray` (a column crossing realizes five rows in one frame, `ArtistPopular.cs:374-382`);
  (b) `FeatLine` counts first and only allocates the featured list on the `+N` branch; (c) per-row playback state
  behind a `UseComputed` memo so a skip between two other tracks does not re-render ten rows; (d) the selection pill
  is an always-mounted **bound opacity**, so selecting is compositor-only; (e) the shelf's frozen closures read
  fields, not render locals. Keep all five.
- **The hero's `_heroWidth` write threshold is 0.5 DIP** — a smaller threshold makes a resize write every frame.
- **`_topBandWide` and `_biographyMode` are plain latched FIELDS, not signals** — they are idempotent functions of
  the width and must not schedule a render from a build lambda.
- **`DrawerHeight` reads a CACHED verdict, `DrawerFor` recomputes one.** `LazyGrid` calls `DrawerHeight` BEFORE
  `DrawerFor` on every pass, so the reserved slot lags the verdict by at most one LazyGrid render and self-heals on the
  next signal-driven one (`AlbumExpand.cs:444-452, :610-612`). Reversing that order, or making `DrawerHeight` recompute,
  reads a `GridDrawerInfo.Columns` that does not exist yet.
- **The drawer's expanded identity is a URI, never an ordinal.** `_expandedUri` is the truth and `_expandedIndex` is
  re-derived from it by a linear scan every render, so a late page landing or a facet replace re-points (or closes, −1)
  the drawer instead of leaving a stale ordinal pointed at whatever now sits there (`AlbumExpand.cs:487-493`).
- **`expandedRevealPeek` is a real contract, not a default.** `DiscoGrid`'s ctor defaults it to
  `AlbumDrawerVerdict.HeaderH + 2 × RowPitch` = **104** — enough of the drawer to prove it opened — alongside
  `expandedTopInset` (96 inline / 28 on the disco page) and `overscanRows: 4` (`AlbumExpand.cs:472-473, :508`).
  Port all three; dropping the peek is what makes `AlignTop` look like it scrolled nowhere on a short facet.

**Where plan §2 / §4.12 / §4.13 are wrong or too thin for this surface.**

- **§2 had no home for the discography page when this chapter was first written.** `disco:` is a real,
  deep-linkable route with its own breadcrumb, `SelectorBar`, paging and total probe. Settled: `Entities/Artist.Discography.cs`
  (1,300 lines, owner N, Wave 5, in the route table per §4.11) holds the CORE rules (`AlbumDrawerVerdict`,
  `DiscographyEraBands`, the route helpers) plus `Facet`, `Grid`, `Drawer` and `Artist.DiscographyPage`.
- **§4.3's `ArtistReleases` + `DiscographyEdge(byte Kind)` cannot serve three independently paged facets** with three
  totals and three completeness states (GAP #8). Three edges, or an edge-per-facet state vector.
- **§4.12 `Track.Row` is not this surface's row.** The artist chart row has a rank cell with a hover transport swap,
  a two-to-three-line mid column with explicit/video/feat/plays runs, a heart, a duration, a selection pill, a drag
  source, a context menu and a geometry tier — at 56 (Modern) or 48 (Classic). It cannot be a `RowStyle` on the one
  shared row; it is `Artist.UI.cs`'s own function that *reuses* `Controls.NumberCell` / `Controls.Heart`.
- **§4.13's album-page sketch is the closest thing the plan has to a page contract, and it is missing the three
  things this page lives on**: a readiness predicate before rendering (it renders `Hero/TrackList/About/...`
  unconditionally), a skeleton branch, and a reveal. Spell all three into the 0.3 page shape.
- **The plan has no cover-palette subsystem at all** (GAP #1) — and the artist page's entire visual identity is
  artwork-derived. This is the single largest omission for this chapter. It now has exactly one home
  (`Entities/Palette.cs` + `Entities/Palette.Host.cs`, A5) and one wave slot (CORE Wave 1 / Host Wave 4); what is
  still missing is the §2 row for it.
- **The plan's `Publication`/`Changed` model needs a per-row escape hatch.** A chart of 50 rows must not re-render on
  every `Tracks.Changed`; §4.12's `BoundItemScope` is right for a virtualized list but the chart is a `PagedShelf` of
  mounted components. Give `Track` a cheap `Version` compare and let each row memo on `(slot, version)`.

**Line budget.**

| | lines |
|---|---|
| 0.2.9, artist-owned (16 files listed in the header) | **4,215** |
| plan §2 target for `Artist.cs + Artist.UI.cs + Artist.Page.cs + Artist.Discography.cs` | **550 + 1,500 + 2,200 + 1,300 = 5,550** (all owner N, Wave 5) |
| honest 0.3 estimate | `Artist.cs` ~550 (columns, flags, the handle, the ported pure rules, commit-time derivations) + `Artist.UI.cs` ~1,500 (hero, chart, pick, banners, shelves, lightbox, the two layout rules) + `Artist.Page.cs` ~2,200 (composition, sections, readiness, band wiring) + `Artist.Discography.cs` ~1,300 (facet, grid, drawer, verdict, era bands, the disco page) = **~5,550** |

The delta is not padding: ~800 lines of 0.2.9 are hydration/store/`VirtualCollection` plumbing that genuinely
disappears, but the 0.3 side gains ~700 lines of columns, edge accessors, decode-side derivations and explicit
readiness that 0.2.9 got for free from records. Budget `Artist.*` at ~5,500 and split `Artist.UI.cs` with a named
partial (`Artist.UI.Chart.cs`) rather than letting one file run to 2k.

**Files/pages missing from the §2 tree for this surface**: the discography page (above); a home for the cover-palette
plane (GAP #1 — **`Entities/Palette.cs` CORE in Wave 1, owner A, plus `Entities/Palette.Host.cs` SHELL and the
page-tone plane in Wave 4, owner L**; arbitration 2026-09-12, agreeing with `00-design-system.md` §9.5 and
`07-liked-songs.md` G1, and superseding this chapter’s `Platform/Design.cs` proposal); and `Concert.UI.cs` must own
`ConcertStub`, which today lives in
`ArtistPage.Shelves.cs` (owner N has both, so this is a placement note, not a conflict).

**Doc drift (code wins).**

- `artist-page-v2-design.md` ("Marquee Plate": a 208² framed portrait, no scrim, a flat 256 band, a single 10-row
  ledger, a `SelectorBar` releases grid) — **never implemented**; 0.2.9 kept the full-bleed photo hero, the veil,
  the 2-column paged chart and the inline `Expander` facets.
- `artist-hero-treatment-note.md` (Option A1: a detached hero CARD with `Radii.Card`, a shadow, a hold-to-0.66 black
  scrim, `HeroSize` 48/44/38/32) — **not implemented**; the hero is still full-bleed and the scrim is
  `Surfaces.ArtistHeroVeil`'s theme-aware accent veil, not black. The note's §3 typography advice *was* taken in
  spirit (display face, negative tracking, `MinSize`) but at 84/48/32 weight **700**, not 44/600.
- `artist-editorial-split-native-spec.md` §3 tier table (thresholds 1040/760/480, heights 440/384/540/516, "copy/photo
  54%/46%") — **stale**; the real thresholds are 880/600/360 and the heights 440/384/452/476, and there is no
  percentage split (the photo is full-bleed and the copy is a max-width column over it). Its "Watch-feed portrait
  preserved" row describes a control the page does not render.
- `artist-pick-zune-concepts.html` §2 S3 ("pick + separate upcoming = ONE panel, two footer rows") — **rejected in
  code**; the upcoming release takes its own full-width band.
- `artist-album-expander-implementation.md` — **implemented**, including its v2 addendum (AlignTop, caret,
  `AnimateContentResize=false`), with one number moved: `BottomGap 16` split into `TopGap 8` + `BottomGap 8`
  (`AlbumDrawerVerdict.cs:15-19`), `SlotHeight` unchanged.

**Two real inconsistencies in 0.2.9 to fix, not to port.**

1. **Hysteretic tier vs non-hysteretic width.** `Banner` sizes the hero from the *hysteretic* tier while
   `CoverArtistBlendWash` and the magazine gutter read `HeroHeightFor(width)` / `PageGutterFor(width)`, which have no
   hysteresis. Between 856 and 880 the hero is 440 tall with a 36 gutter while the wash is sized for 384+96 and the
   magazine takes a 32 gutter. Publish the resolved `ArtistHeroMetrics` (or at least the tier) to both consumers.
2. **The chrome accent is unwatched.** `_paletteAccent` is computed in `Body` from `ChromeSchemeFor` with no
   subscription, so a grading that lands after the page settles reaches the Play capsule only on the next unrelated
   re-render (the chart gets it via a remount key). In 0.3 resolve the accent through a watched leaf value.

---

## 10. Parity checklist

Baseline: `C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`, side by side
with the 0.3 build in the same mode, same window size, same theme.

**What `--fake` actually gives you** (`FakeData.cs:142-195`) — the earlier "every artist has everything" reading is
wrong and will make you chase absent sections as regressions. Every artist (`spotify:artist:ar0…ar{N}`) gets 6 albums,
a synthesized bio, a header image, external links and a latest release. Everything else is **hash-gated per artist**
off `h = |name.GetHashCode()|`, so no single fake artist shows the whole magazine:

| facet | present when | so it is ABSENT on |
|---|---|---|
| pinned pick | `h % 3 != 1` | ~⅓ of artists |
| concerts (and therefore the tour banner) | `h % 4 != 0` | ~¼ |
| merch | `h % 3 != 1` | ~⅓ |
| playlists | `h % 2 == 0` | half |
| music videos | `h % 5 != 2` | ~⅕ |
| top cities | `h % 2 == 1` | half |
| gallery | `h % 3 != 2` | ~⅓ |
| verified badge | `h % 5 != 0` | ~⅕ |
| world rank | `h % 7 != 0` | ~⅐ |
| **Appears on** | never — `AppearsOn: null` always | **every** fake artist |
| **Related** | never — `Related: null` always, so "Fans also like" is ALWAYS the cached-artists fallback | **every** fake artist |

`TopTracks` is null too, so the chart falls back to `FakeData.TopTracksOf(a)` = **5 rows** ⇒ one column, no pager, and
the count "5" in the header. Pick two artists by trying `ar0…ar5` and reading which sections render, rather than
trusting an index-to-hash mapping. Route: type `artist:spotify:artist:ar1` into the omnibar or use the Home shelves.
Items 29, 45, 56 and 71 below need a LIVE artist; they cannot be checked in `--fake` at all.

*Static capture* = screenshot both builds and diff. *Hover capture* = park the pointer, then capture.
*Frame recording* = record 2 s and step frames.

1. **Hero photo extent** — 1440 window, Wide tier: the photo fills the full hero box, 440 tall, no letterbox, no
   rounded corners. Static capture.
2. **Hero focal point** — the same crop of the same image: focus 0.62/0.34, not centre. Static capture, diff the crop.
3. **Hero rest scale** — the photo sits at 1.05 with the inner frame at 1.08 lifted 4%: an image edge is outside the
   hero box on all sides. Static capture at scroll 0.
4. **Hero image reveal** — cold cache, navigate to an unvisited artist: the photo fades in over ~300 ms **and** zooms
   1→1.05 over 500 ms. Frame recording.
5. **Veil axis** — Wide/Medium: a left-to-right accent veil across the whole hero (α .96→0). Compact/Narrow: **no
   veil**. Static captures at W=1160 and W=500.
6. **Tier thresholds** — drag the window so the pane crosses 880, 600, 360: the name steps 84 → 48 → 32 and the layout
   flips to stacked at 600→360. Static captures on both sides of each threshold.
7. **Tier hysteresis** — from Wide, narrow to 870: still Wide (name 84). From Medium, widen to 890: still Medium until
   904. Static captures.
8. **Hero heights** — 440 / 384 / 452 (200+252) / 476 (176+300) at the four tiers. Measure in the capture.
9. **Verified row** — badge + "Verified Artist" in TextSecondary above the name; absent on an unverified artist
   (`FakeData`: `h % 5 == 0`). Static capture on two artists.
10. **Meta line** — `#N in the world` in accent, then monthly listeners, then followers, one row with 16-DIP gaps;
    **stacked with 4-DIP gaps only at Narrow**. Static captures at Compact (500) and Narrow (340).
11. **Action row order** — Play, Shuffle, Follow, Radio at Wide/Medium/Compact; at Narrow, [Play, Follow] over
    [Shuffle, Radio]. Static captures.
12. **Play capsule colour** — the cover's `ChromeAccent`, not system blue, on an artist with coloured art; system blue
    on a greyscale one. Static capture.
13. **Follow toggle** — click Follow: the border goes accent and the glyph fills; the band's text action reads
    "Following" in accent ink. Static capture before/after.
14. **Radio** — click the radio pill: a "Radio started" toast with an "Open playlist" action (not a replay of the
    artist context). Static capture of the toast.
15. **Parallax** — scroll 200 px: the photo has moved 30 px less than the copy (0.15×200). Frame recording, measure a
    photo landmark.
16. **Collapse** — scroll to exactly the collapse distance (384 at Wide): the hero is exactly 56 tall, the expanded
    copy is fully faded, the band is fully opaque. Frame recording.
17. **Band reveal window** — the band starts appearing only in the last 44 px of the collapse and slides 4 px up.
    Frame recording.
18. **Band anatomy** — title (≤280, ellipsised), pivot, `Play` + `Follow/Following` as words; **no avatar, no
    capsules, no fill**, one hairline. Static capture at scroll > cd.
19. **Band transparency** — on a dark desktop wallpaper, the band's 56 DIP show the shell material, not a slab.
    Static capture with a bright wallpaper.
20. **Pivot contents and order** — exactly the sections that are destinations: Top tracks, Albums, Singles & EPs,
    Compilations, Appears on, Music videos, Playlists and discovery, Upcoming concerts, Merch, Biography, Gallery,
    Fans also like. **No** "Latest release", **no** "Upcoming", **no** "tour". Static capture.
21. **Pivot underline** — scroll through the page: the underline moves to the section occupying the upper quarter of
    the viewport below the band, and cross-fades (it does not fly). Frame recording.
22. **Pivot click** — click "Biography": the section's top parks exactly under the band (56), animated. Frame
    recording.
23. **At-scroll-end** — scroll to the very bottom: the last measured section is active even if it is short. Static
    capture.
24. **Magazine clip + feather** — at scroll > cd, a card crossing the band's lower edge dissolves over 24 DIP rather
    than being cut. Frame recording.
25. **Section gap** — 32 DIP between every section (not 20). Measure in a static capture.
26. **Page gutter** — 36 at ≥880, 32 at ≥600, 16 below; the hero copy and the first section header start on the same
    x. Static capture with a ruler overlay.
27. **Content cap** — at a 2560-wide window the magazine column stops at 1600 and centres. Static capture.
28. **Chart shape (fake)** — 5 tracks ⇒ ONE column, 5 rows, no pager, the count "5" at the header's right. Static
    capture on `ar1`.
29. **Chart shape (live)** — a live artist with >5 popular tracks ⇒ 2 columns × 5 rows and a `‹ ●● ›` pager.
30. **Chart row geometry** — Modern: 56 tall, art 44 r4, `#` cell 24, heart 28, duration 13 px; gap 12 both axes.
    Measure in a static capture.
31. **Chart row tiers** — narrow the window until the chart cell drops below 340: the subtitle stacks onto a third
    line with the FULL play count; below 220 the art is 40; below 200 the duration disappears. Static captures.
32. **Classic rows** — Settings → track row style Classic: rows 48, art 40, square corners, a hairline under each row.
    Static capture.
33. **Play-count format** — the row shows `2.4B`; the tooltip shows `2,381,125,897 plays`. Hover capture.
34. **feat. line** — a track crediting the page artist plus others shows `feat. X` with a clickable name and `+N`;
    a track crediting only the page artist shows none. Static capture.
35. **Row hover** — hover a chart row: the `#` becomes ▶, the fill becomes `RowHover`, a 1-DIP card stroke appears,
    and the row does **not** change height. Hover capture.
36. **Now-playing row** — play a chart track: the equalizer replaces the number, the title turns accent, and the row
    has **no fill**. Static capture.
37. **Selection** — click one row, Shift-click another: a 3×16 accent pill on the left edge of each selected row, no
    fill change, and the page does not re-render (no flicker). Frame recording.
38. **Chart paging** — click ›: the strip slides one page and snaps; the rows that stay on screen keep their identity
    (no shimmer, no remount). Frame recording.
39. **Artist pick present** — `ar1` at ≥760 pane width: the pick sits in a right rail (Grow 1) beside the chart
    (Grow 2) with a 20-DIP gap, and has **no** "Artist pick" section header above it. Static capture.
40. **Artist pick stacked** — narrow below 760: the pick becomes a full-width horizontal card with a 300-wide photo
    column. Static capture.
41. **No pick** — `ar2`: the chart takes the full band width and there is no empty rail. Static capture.
42. **Latest release banner** — a full-width band above Albums with a 72 cover, eyebrow `Album · date · N tracks`,
    a 20/28 title, and `[▶ Play][View]`; clicking the card body does nothing. Static capture + click test.
43. **Facet sections** — Albums / Singles & EPs / Compilations each render as an expanded `Expander` with a 3×22
    accent spine, the title, and `N releases`. Static capture.
44. **Facet header pins** — scroll into a facet: its header sticks at exactly 56 (directly under the band) and the
    grid clips under it with a 24-DIP feather. Frame recording.
45. **Era label** — on an artist with ≥3 distinct years and >5 releases, scrolling the grid changes the header meta to
    `1990s · 12 releases` for the visible range. Frame recording (live artist; `--fake` albums may not qualify).
46. **Grid columns** — measure: `floor((innerW+16)/196)` columns, cell `(innerW−(cols−1)×16)/cols`, card height
    `cell+50`, row pitch `cell+70`. Static capture at 1160 and 1040.
47. **Drawer open** — click a card: the drawer opens after that card's ROW, full width, in ~200 ms, and the clicked
    row parks directly under the sticky header. Frame recording.
48. **Drawer caret + border** — the caret points at the clicked card's centre and the card has a 2-DIP accent border.
    Static capture.
49. **Drawer columns** — at ≥5 grid columns the tracks are in 2 column-major columns; at 4 or fewer, one column.
    Static captures at two widths.
50. **Drawer cap** — an album with >12 (1 col) / >24 (2 col) tracks ends with `Show all N tracks` in accent. Static
    capture.
51. **Drawer re-open** — collapse and re-open the same album: the rows are there on the very next frame, **no
    shimmer**. Frame recording.
52. **Drawer switch** — click a different card in a different row: no frame shows the previous album's tracks under
    the new cover, and the slot height is final on the click frame. Frame recording.
53. **Drawer height arithmetic** — `panel = 40 + rows×32`, `slot = panel + 16`. Measure a 12-track, 2-column case
    (232 / 248).
54. **Drawer row height** — 32-DIP pitch with 28-DIP content; the column set is `# 26 · ♥ 28 · title ★ · time 44 ·
    ⋯ 32`. Measure.
55. **Facet collapse** — click the facet header: exactly ONE motion (the disclosure), no second section reflow.
    Frame recording.
56. **Appears-on shelf** — measured `PagedShelf`, cards 150–200 wide, subtitle = the year (or the kind label when the
    year is 0). Static capture.
57. **Music-video cards** — 16:9 thumbnails, duration under the title, ≤16 cards. Static capture.
58. **Concert stubs** — 48-DIP date column with an accent `MMM` eyebrow over a 28/36 day numeral, venue, ⌖ city.
    Static capture.
59. **Merch cards** — square art, 2-line name, price in accent ink. Static capture.
60. **Gallery strip** — square tiles, hover scale 1.04; clicking opens the modal lightbox at that index. Hover capture
    + click.
61. **Lightbox chrome** — opens with chrome visible; after 2.6 s of stillness the top bar and the filmstrip fade out
    over 250 ms; any pointer move brings them back. Frame recording.
62. **Lightbox counter** — `3 / 12` under a "Gallery" label. Static capture.
63. **Lightbox zoom** — Ctrl+wheel zooms to ~2.2 about the pointer with a spring; drag pans; a click springs home;
    `Esc` while zoomed unzooms instead of closing. Frame recording.
64. **Lightbox filmstrip** — the active thumb has a 2-DIP accent border and full opacity; the rest sit at 0.55.
    Static capture.
65. **Biography band wide** — ≥820: a bordered biography card (Grow 2) beside a Profile-facts column (Grow 1) with a
    20-DIP gap. Static capture.
66. **Biography band stacked** — narrow below 820: the two stack full width. Static capture.
67. **Stat tiles** — only non-zero stats render; Monthly listeners, Followers, Albums, Singles & EPs, Upcoming
    concerts, Related artists, in that order, each 28/36 over a caption. Static capture.
68. **No tiles** — an artist with zero stats: the biography card takes the full width (no empty right column).
    Static capture (live artist, or seed a fake with zeroed stats).
69. **City bars** — bars are proportional to `listeners / max`, 4 DIP tall, accent-filled. Measure the top two.
70. **Tour banner** — a full-width card with a 44 accent circle, an accent eyebrow, a 16/700 headline and a chevron;
    clicking opens the artist's concert schedule. Static capture + click.
71. **Fans also like** — circular cards with the subtitle "Artist"; on an artist whose `Related` is empty the shelf
    falls back to up to 12 cached artists excluding the page artist (`--fake`: always the fallback). Static capture.
72. **Blend wash** — the accent wash reaches its strongest alpha at the top of the page and is gone by the bottom of
    `heroHeight + 96`; turning off `ColorWashesEnabled` in Settings removes it entirely while the hero veil and the
    Play colour stay. Static captures both ways.
73. **Shell tint** — opening a colourful artist warms the whole window chrome; going Back restores the previous
    page's tint. Static captures.
74. **Skeleton parity** — open an artist with a cold cache: the shimmer has the hero at the right height, a
    title-width header bar, two 5-row chart columns and a row of square grid placeholders; nothing shifts when the
    content lands. Frame recording (this is the strictest single test of §7).
75. **Reveal** — the Pending→Ready swap is a cross-fade with **no** translate and **no** row stagger. Frame recording.
76. **Error state** — with the network off and a cold cache, the page shows the shared error state (title + subtitle).
    Static capture. *(0.3 additionally must offer Retry — a deliberate improvement, flag it in the diff.)*
77. **Discography page** — open `disco:0:spotify:artist:ar1`: breadcrumb `Artist › Albums`, a 28/36 page title, a
    3-item `SelectorBar`, and the same grid + drawer; switching the selector navigates. Static capture.
78. **Discography page shimmer** — the grid shows shimmer-up-to-N cells immediately (total probe) before the first
    page lands. Frame recording.
79. **Scroll restore** — scroll an artist page, navigate to an album, go Back: the scroll position is restored and the
    band is in the same state. Frame recording.
80. **Artist → artist** — from the "Fans also like" shelf, open another artist: a fresh page slides in, the new
    pivot reflects the new sections, and clicking a pivot item scrolls the NEW page (no stale anchors). Frame
    recording + click test.
81. **Hero with no image** — an artist with no header image and no avatar: a flat neutral field at the tier's photo
    height (no tinted tile, no shimmer that never resolves), the veil still painting over it on Wide/Medium, the copy
    unchanged. Static capture. *(Seed a fake with `Cover` nulled, or use an offline identity-only row.)*
82. **Track artwork hidden** — Settings → Appearance → hide track artwork: the chart's art cell **disappears** and the
    title column takes the width; the row stays 56 tall; nothing else on the page changes. Static captures both ways.
83. **Classic + now playing** — Classic rows, play a chart track: the explicit badge, the `·` separators, the video
    glyph, the play count AND the duration all turn accent, not just the title. Static capture.
84. **Drawer multi-select** — Ctrl-click two drawer rows: the shared selection command bar appears over the drawer's
    own rows, 8 DIP up from its bottom edge, and the drawer does not grow. Static capture.
85. **Drawer swipe** — swipe a drawer row: `ToggleLike` / `AddToQueue` are exposed, and swiping a second row closes the
    first (one `SwipeGroup` per album). Frame recording.
86. **Drawer ready-empty height** — an offline/trackless album: exactly 2 rows tall (panel 104 / slot 120) with
    "No tracks" and a Retry that re-runs the loader keeping the drawer open. Static capture + click.
87. **Facet header meta with no eras** — an artist with <3 distinct years or ≤5 releases in a facet: the header meta
    stays plain "N releases" and never flickers to an era label while scrolling. Frame recording.
88. **Discography page accent** — open `disco:0:…` from a colourful artist: the expanded card's border, the drawer's
    play circle and its selection pills are the **system** accent here, not the cover's (a deliberate 0.2.9 difference;
    decide and record which one 0.3 ships). Static capture side by side with the inline facet.
89. **Discography page chrome** — the window tint does not change when entering `disco:` (it holds the previous page's),
    and there is no error state if the facet fetch fails. Static capture with the network off.
90. **Pre-measure frame** — resize the window to a pane width well below 760 and navigate to an artist: the FIRST
    composed frame decides Top tracks and Biography at the 900-DIP fallback (wide), then settles. Frame recording;
    confirm the settle is one frame and does not remount the pick (its `Key` folds the tier).
91. **Inert merch card** — click a merch card: nothing happens, no menu on right-click, no drag. Confirm this is
    intentional in 0.3 or wire `ShopUrl`.
92. **External-link pills** — click "Instagram" in the biography card: nothing happens (they are `BoxEl`s with no
    `OnClick`). This is the one straightforward bug on the page; 0.3 must fix it, so flag it in the diff rather than
    matching it.
93. **One band, two pages** — park an artist and an album at the SAME pane width, scroll each past its hero and
    capture both bands. Everything from the shared layout must measure identically: band 56, hairline 1 at the bottom,
    the 93/24 clip-and-feather under it, title cap 280, cluster gap 24, action gap 16 / padX 10, pivot gap 16 / padX 8,
    underline 2 with a 4-DIP gap. Only two things may differ, and both are per-consumer inputs, not band geometry: the
    gutter (artist `ArtistHeroLayout.PageGutterFor` 36/32/16 vs detail `TrackRow.PadXFor` 16/12/8) and the row content
    (artist: title · pivot · Play + Follow, no byline; detail: title + byline, search swap, selection arm). Two
    different heights, hairlines or feathers mean the band got written twice — reopen §1.2's ownership note before
    shipping. Static capture ×2, overlaid.

---

## 11. Audit log

Adversarial re-read of the chapter against the 0.2.9 sources, 2026-09-12. One line per correction; `wrong` = the
chapter stated a value or behaviour the code contradicts, `missing` = a state/element/rule the code has and the chapter
did not, `unverified` = a claim softened because the code does not support it as stated, `overclaim` = a claim narrowed,
`critic-fix` = a completeness-critic finding applied (the chapter contradicted another chapter of this contract).

| # | § | kind | correction |
|---|---|---|---|
| 1 | 2 · W15 | **wrong** | "12 shown, 2 cols, show-all ⇒ rows ⌈13/2⌉ = 7 ⇒ panel 264, slot 280" is arithmetically impossible: at 2 columns `cap = CapPerColumn 12 × 2 = 24`, so `showAll = total > 24` cannot be true while `shown` is 12. The wireframe's own "Show all 17 tracks" cell at 2 columns is the same error. Replaced with four worked cases from `AlbumDrawerVerdict.cs:31-47` (17@2col ⇒ 9 rows / 328 / 344 — no show-all; 12@2col ⇒ 232/248; 17@1col ⇒ 456/472; 30@2col ⇒ 456/472) plus the fixed 2-row ready-empty case (104/120). |
| 2 | 4 · row 6 | **wrong** | The hero photo does NOT use the cover-derived `Surfaces.PlaceholderFor`. `Banner` passes the flat `Surfaces.ArtworkPlaceholder` to `HeroArt` and uses it for the no-image box (`Hero.cs:98-102`); the pick's wide photograph does the same (`MediaCard.cs:381`); the lightbox photo's placeholder is fully transparent (`ArtistGalleryLightbox.cs:142`). Split into rows 6 and 6b with the real slot list. |
| 3 | 2 · W21 | **wrong** | The lightbox's top chrome was drawn as two stacked buttons on the right. It is ONE 72-DIP `Direction = 0` row: the [Gallery / "n / N"] column, a `Grow = 1` spacer, then `Export image` (accent) and `Close` (standard) side by side (`ArtistGalleryLightbox.cs:298-322`). Also added: the filmstrip UNMOUNTS in zoom mode, not just the FlipView; thumb decode 128 and hover scale 1.04; the transparent photo placeholder. |
| 4 | 2 · breakpoints | **wrong** | "Chart columns: ≥540 ⇒ 2" is only half the decision. `maxColumns = clamp(⌈total/5⌉, 1, 2)` (`ArtistPopular.cs:138`) also clamps by row count, so a ≤5-track chart is one column at ANY width — which is the only shape `--fake` ever shows. |
| 5 | 0 · #6 | **wrong** | "rows keyed by track URI" understates the key. It is `"row:" + uri` plus an `art`/`noart` marker plus a `classic`/`modern` marker (`ArtistPopular.cs:246`), with `"chart#" + i` for an empty uri. This matters because §9 asserts 0.3 can reduce the key "to the uri alone" — it cannot, unless the two appearance settings stop changing the row's child count. |
| 6 | 5 · Pending→Ready | **wrong** | The row conflated the shimmer's exit with the content's entrance. `SkeletonReveal.Play` animates the REAL root's opacity 0→1 over `Expressive.Fast` 250 ms on `Easing.SmoothOut` (`SkeletonRegion.cs:166-168`); the shimmer orphan separately fades at `SkeletonStyle.ExitMs` (also 250). Split into two rows. |
| 7 | 8 · era bands | **wrong** | The label grammar has FIVE arms, not four — the plain `"1994–1991"` newest–oldest fallback was missing — and `"1990s and earlier"` is reachable only via `Plan(…, provisional: true)`, which `PlanAlbums` never passes, so it is dead on the artist page today. Added the four bail-out conditions and the `clamp(round(√n), 3, 8)` band target. |
| 8 | 10 · preamble | **overclaim** | "every artist has 6 albums, a synthesized bio, gallery, links, cities, concerts, merch, videos, playlists" is false: every facet except albums/bio/header/links/latest is hash-gated per artist, and `AppearsOn` + `Related` are ALWAYS null in `--fake` (`FakeData.cs:163-194`). Replaced with the real gate table and a note that items 29/45/56/71 need a live artist. The `ar1`/`ar2` pick/no-pick instruction was also softened — it asserted an index→hash mapping the chapter never verified. |
| 9 | 2, 3, 6 | **missing** | **Track artwork hidden** (`AppearancePrefs.TrackArtworkHidden`) — a shipped Appearance setting that removes the chart row's art cell entirely (3 children, not 4) and rides both the row key and the shelf key. Added to W10, the new §6 settings table and parity item 82. |
| 10 | 2 · W15, 3 | **missing** | The drawer's **`SelectionCommandBar`** — the panel body is `ZStack(body, SelectionCommandBar(_sel, …, bottomPadding 8))` (`AlbumExpand.cs:158-162`), with `ItemCount = verdict.Shown`. The chapter had no trace of it. Added with parity item 84. |
| 11 | 2 · W10/W25 | **missing** | Classic + now-playing (`classicNow`) tints the explicit badge, the dot separators, the video glyph, the play count AND the duration accent — not just the title (`ArtistPopular.cs:386-413`). Parity item 83. |
| 12 | 2 · W11 | **missing** | The chart shelf's unrecorded knobs: `pager: ShelfPager.None`, `headerGap` 10/8, `edgeFade` 16, `maxItems` 50, `maxCardW` 9999, pager cluster gap 4, and the `onReselect` re-arm contract. |
| 13 | 2 · breakpoints | **missing** | Both `Responsive.Of` call sites use `fallback: 900f`, so the first composed frame decides Top tracks AND Biography at 900 regardless of the real pane. Added as a table row and parity item 90. |
| 14 | 2 | **missing** | **W4b — hero with no image**: `HeroArt` is not mounted; a flat `ArtworkPlaceholder` box takes its place, the veil still paints over it, and the zoom keyframes are the one motion that disappears. Parity item 81. |
| 15 | 2 · W22 | **missing** | Five real differences of the discography page: it uses the THEME accent (no cover palette), has no `Skel.Region` (no skeleton and no error state), is absent from `PublishesShellMaterial` (no shell tint), gets a NEW keep-alive slot per facet (so its own `_key` rebuild branch is dead), and hardcodes `"Artist"` while `artist.fallbackName` exists unused. Parity items 88-89. |
| 16 | 9 | **missing** | Four traps the chapter did not carry: `DrawerHeight` reads the CACHED verdict while `DrawerFor` recomputes it (and why that ordering is correct); `_expandedUri` is the truth and `_expandedIndex` is re-derived every render; `expandedRevealPeek = HeaderH + 2×RowPitch = 104`; `overscanRows: 4`. |
| 17 | 2 · W10 | **missing** | The third chart line is gated on `stackSub && featLine != null && PlayCount > 0` — a stacked row with no feat credit stays 2-line. Also the Modern mid-column gap (1), the sub-run gap (5) and the trailing gap (6). |
| 18 | 3 | **missing** | Ten token rows: `Section` gap 12; the unused `AccentHeader(title,count)` / `SectionN` pair; the pick's accent release-date span and its head type; the drawer's command bar, head cover, shimmer row and empty note; the Follow pill's press scale + skeleton proxy; the facet's 24-DIP trailing spacer; `AlbumNavAction`'s default 34. |
| 19 | 5 | **missing** | Seven motion rows: the page swap's real numbers (120 ms exit, 90 ms enter delay, 8-DIP slide); the pivot's `AutoEdgeFade`; the hero capsules' 1.04/0.96; the gallery/merch/filmstrip 1.04; the facet header's `BrushTransitionMs = 0`; the shell-tint publication deps; the wash/veil grading re-render. |
| 20 | 2 · breakpoints | **missing** | The scroll spy's constants — `SpyProbe 8`, `SpyViewportFraction 0.25`, `EndProbe 8`, the at-scroll-end rule and the NaN-stops-the-scan rule — were named only as "`ContextBandLayout.SpyLine`". |
| 21 | 6 | **missing** | The menus of the OTHER shelves: playlist cards get the full container grammar (Add-to-playlist included), music-video cards get the thin track-uri shape, and gallery tiles / merch cards / concert stubs carry no menu at all. Plus the chart-vs-drawer `BuildSingle` / `Build` asymmetry. |
| 22 | 6 | **missing** | A settings table (`ColorWashesEnabled`, `TrackRowStyle`, `TrackArtworkHidden`, `ReducedMotion`) and the `AppearancePrefs.Epoch` read that makes an appearance change re-render the page. |
| 23 | 6 | **missing** | `RadioLaunch`'s third arm: a thrown `StartRadioAsync` toasts the raw `ex.Message` as an Error — the only exception string this surface shows a user. |
| 24 | 1.0 | **missing** | `Components/ConcertUi.cs` is in the assigned source list but is never called by this surface; and `MerchItem.ShopUrl` is decoded, passed to the card and never used — the merch card has no click, menu or drag at all. Parity item 91. |
| 25 | 2, 10 | **missing** | Parity items 85-87 and 92: drawer swipe actions + the per-album `SwipeGroup`; the ready-empty height; the no-eras header meta; the dead external-link pills as an explicit 0.3 fix rather than a parity target. |
| 26 | 1.2 | **critic-fix** | critic-fix: the `SectionAnchors` / `ContextPivot` / `ContextBand` row said `Shell/Shell.UI.cs` (shared) and cited `03-detail-frame.md` / `18-shell-frame.md` for it. 03 says the opposite (`:151` keep them in `Detail.UI.cs` as `Detail.Band(...)` and let `Artist.Page` call them; `:975` warns that otherwise "the band gets written twice") and 18 claims neither the band nor its layout anywhere. Row re-pointed at **`Entities/Detail.UI.cs`** (UI half) + **`Entities/Detail.cs`** (CORE), with owner N as a consumer only. |
| 27 | 1.2 | **missing** | critic-fix: added the "Where the context band lives" note under the §1.2 table — the two consumers with call sites (`DetailVerticalHero.cs:373-374, :393`; `ArtistCompactBar.cs:48, 52-53, 78, 100`; `ArtistPage.cs:232, 321, 328, 338`), the file/namespace evidence (`ContextBand.cs:12, 26-44, 69-188, 190`), the hard dependency that decides the home (`ContextBandLayout.cs:44` = `DetailVerticalLayout.StickyFadeBand`; `Height 56` = `CompactIdentityHeight`, `DetailVerticalLayout.cs:78`; re-exports at `ContextBand.cs:158, 161`), the 18-shell budget objection (`18-shell-frame.md:1137-1143`), the Wave ordering (owner N vs owners M/O; `Entities/Detail*.cs` missing from `wavee-0.3-implementation.md:32-70`, so Wave 4 must add them), and the list of what legitimately differs between the two arms (gutter + content only). |
| 28 | 8 | **critic-fix** | critic-fix: the `ContextBandLayout` destination cell said "CORE section of `Shell/Shell.cs` (see `18-shell-frame.md`)". Corrected to `Entities/Detail.cs`, matching `03-detail-frame.md:880`, with the `DetailVerticalLayout` dependency as the reason and a flag that `30-appearance-preferences.md:1448` still files the same class under `Platform/Design.cs` (a third answer, to be reconciled in that chapter). |
| 29 | 9, 10 | **missing** | critic-fix: §9 "must not be simplified" item **11** — the band is one implementation with two consumers, owner N calls it and writes none of it — and parity item **93** ("One band, two pages"): artist and album bands captured at the same pane width must agree on 56/1/93/24/280/24/16/10/16/8/2/4, with only the gutter (36/32/16 vs 16/12/8) and the row content allowed to differ. |

**Verified correct, and left alone** (spot-listed so the next pass does not re-derive them): the hero tier ladder and
its 856/904/576/624/336/384 hysteresis; heights 440/384/452/476 and photo bands 200/176; gutters 36/32/16/16 and copy
caps 1120/760/640/520; collapse 384/328, fade `cd−96`, reveal `cd−44`, `.Reveal(…, 4)`; `1.05` rest inside `1.08`
lifted 4 %, focus 0.62/0.34, decode clamp 320…1920; the veil's `.96/.92/.35/0` and `0.16/0.24` pull; the wash's
`0.20/0.06/0` light, `0.30/0.08/0` dark over `h + 96` with the boundary at `h/(h+96)`; the shell tint's
`TextBase @0.05` / `TintedDark @0.14`; every `ContextBandLayout` constant (56 · 1 · 24 · 16 · 16 · 8 · 10 · 2 · 4 ·
280 · 7.6); the chart's 56/48, 44/40, 24, 28, 6, 12/8, 264, 540; `AlbumDrawerVerdict`'s 40/32/8/8/12/5/3; the grid's
180/16/50/20 and `floor((w+16)/196)`; `LazyGrid` cell and pitch arithmetic at 1088 (204.8 / 254.8 / 274.8); the drawer
transitions 200/150 Size-Reflow-Leading and 150/100 Opacity; disclosure 333/167; `MotionTok` 83/150/250/300/500;
`WaveeMotion` 1.02·0.98 / 1.04·0.96 / 1.07·0.92 and 83/167/250; `Radii` 0/4/8/16/999; `Spacing` 2…36;
`PlayerDock.Reserve` 72 (⇒ 112 and 108 bottom pads); `PageMaxW` 1600 and `SectionGap` 32; every `WaveeType` metric
(84/96/700 CS−28 Min 68 · 48/60/700 CS−20 Min 40 · 32/40/700 CS−12 Min 28 · PickQuote 28/36/400 CS−12 ·
SurfaceDisplay 40/52/400 CS−12); `WaveeCta` 18/6/18/7 and 36; the lightbox's 2600 / 4 / 2.2 / 0.0022 / 0.32·0.9 / 260 /
224 / 158 / 72 / 84 / 56 / 14 / 140; `DetailLayoutBreakpoints.ModeFor`'s 820 → 844; the `Menus.Card` strip and row
order for albums and artists; every loc key listed in §6 (all present in `en-US.json`, and the "bypassed" list is
accurate); and all seven test-file fact counts in §8 (9 / 10 / 10 / 8 / 15 / 7 / 13 — each matches exactly).

**Not re-derived, flagged for the next pass**: §1.2's 0.3 mapping table and §7's field-group / edge proposals are
design, not parity — this audit checked only the 0.2.9 column of each row. §7's `Track`-side readiness predicates lean
on `01-track-row.md` / `04-detail-track-table.md`; the `ArtistFields.Chart` gate (DATA GAP #18) has no 0.2.9
counterpart to check it against beyond `Models.cs`'s `ChartFetchedAt`.

**arbitration 2026-09-12:** the cover-palette plane is **`Entities/Palette.cs` (CORE) + `Entities/Palette.Host.cs`
(SHELL)**, not `Platform/Design.cs` (A5). This chapter’s §1.2 mapping row, §7 readiness row, §7 DATA GAP #1 and
§9’s two mentions were rewritten to it, and the reason is recorded in the gap row: what `CoverColorPlane.cs` (556,
verified) holds is a per-image, TTL’d, fetched, persisted, `Ensure`-able plane keyed like an entity, while
`Platform/Design.cs` is tokens and colour arithmetic with no store, no fetch and no readiness. The colour MATHS the
page calls (`WaveePalette.Lift`/`Vivid`/`Accent`/`PageTone`/`TextInk`, `Design/WaveePalette.cs` 333) stays in
`Platform/Design.cs` — only the plane moved. Wave slots recorded with it: **CORE in Wave 1 (owner A)**, because
five other chapters’ grounds (00 §9.5, 03, 07 G1, 12, 21) depend on it, and **Host + the page-tone plane in Wave 4
(owner L)**. Cross-referenced to `00-design-system.md` §9.5 and `07-liked-songs.md` G1, which already named these
two files; no value, geometry or 0.2.9 citation in this chapter changed.

**consistency 2026-09-12:** header said the discography page had no home in the plan; settled as `Entities/Artist.Discography.cs` (1,300 lines), owner N, Wave 5, with `disco:` in the route table. Header, the §9 "where the plan is wrong" bullet and the §9 line-budget table corrected to state this as settled rather than proposed.
