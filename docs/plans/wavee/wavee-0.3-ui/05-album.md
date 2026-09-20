# Album page (and prerelease) — 0.3 visual fidelity contract

> 0.2.9 sources (all under `src/apps/Wavee/`, HEAD b3f6647a): `Features/Detail/DetailTrailing.cs` (709) ·
> `Features/Detail/DetailRail.cs` (607) · `Features/Detail/DetailPage.cs` (757) · `Features/Detail/DetailConfig.cs` (311) ·
> `Features/Detail/DetailShell.cs` (858) · `Features/Detail/DetailVerticalHero.cs` (579) ·
> `Features/Detail/DetailVerticalLayout.cs` (662) · `Features/Detail/ArtistFacePile.cs` (271) ·
> `Features/Detail/AlbumReleaseFactsRules.cs` (116) · `Features/Detail/PreReleaseDerivation.cs` (43) ·
> `Features/Detail/AlbumDrawerVerdict.cs` (48) · `Features/Detail/CollaboratorFacePile.cs` (162, the playlist twin of the
> face pile) · `Features/Detail/DetailSkeleton.cs` (215) · `Features/Detail/PlaylistPageNoticeRules.cs` (94) ·
> `Features/Detail/TrackExpandedFacts.cs` (367) · `Features/Detail/ArtistPage.AlbumExpand.cs` (879, the drawer half) ·
> `Components/PreReleaseCountdown.cs` (127) · `Components/TrackVersionsPanel.cs` (411) · `Components/StatTile.cs` (57) ·
> `Components/SaveButton.cs` (203) · `Design/WaveeCta.cs` (243)
> | 0.3 target: `Entities/Album.cs`, `Entities/Album.UI.cs`, `Entities/Album.Page.cs` | Wave 5 owner M
>
> After Wave 0 every cited path lives at `src/apps/_old/Wavee/<same relative path>`.
> Shared machinery is specified elsewhere and only *configured* here: `00-design-system.md` (tokens, type ramp, cover
> palette, CTA, motion curves), `01-track-row.md` (the row cell, equalizer, heart, selection, menus),
> `02-cards-and-controls.md` (MediaCard.Row, StatTile, chips), `03-detail-frame.md` (the two-column shell, the rail
> scaffold, the vertical hero, the context band, skeleton + reveal, rail grip/collapse), `04-detail-track-table.md`
> (tier/relief ladders, column header, command bar, drawer host), `08-artist-and-discography.md` (the artist page that
> embeds this chapter's album drawer).

---

## 0. The non-negotiables

1. **The wide album page is TWO COLUMNS, not a hero over a list.** A fixed-width metadata rail (default 280 DIP,
   user-resizable 180–480) sits beside the track table for the whole scroll; the rail has its own scroller and its own
   `Tok.FillLayerDefault` layer fill (`DetailRail.cs:290-298`). The rail is the page's identity; the table is the page's
   content. Collapsing the rail to a 96-DIP strip keeps the cover and the title (`DetailShell.cs:205, 766`).
2. **The rail never shows a meta line.** "12 songs · 41 min · 2019" is deliberately absent from the two-column rail,
   because the "About this release" bento below the CTA states Songs/Length/Released (`DetailRail.cs:198-200`). Showing
   both is the regression this design removed.
3. **"About this release" has ONE shape from its first paint.** Songs and Length share row 1; Released always spans row 2
   alone, so a bare year sits in the exact rect the full date later occupies; Label/℗/© are 11-px NOTE LINES under the
   tiles, never a fourth tile (`DetailTrailing.cs:196-273`). Nothing in the block may be content-sized or wrap-grown.
4. **The whole facts record is gated on the Full rung**, so the panel appears once, complete, under one fade — never
   Released-then-Label-then-copyright reflowing under the reader (`DetailPage.cs:737-740`).
5. **The billed-artist line is a stacked face pile, not text**: up to 4 overlapping 28-DIP avatars in 2-DIP rings
   (−12 overlap), a `+N` frame, a 8-DIP chevron in `Tok.TextTertiary`, and the billed names in accent ink — the pile
   opens a flyout of every artist (`ArtistFacePile.cs:29, 83-107, 161-189`). **`+N` is `allDistinct − billed.Count`** —
   the *track-only* contributors beyond the billed set, NOT the total distinct count (`ArtistFacePile.cs:53-54`).
   Degenerate arm: when no billed artist resolves, the pile shows the first 4 of `all` and `+N = all.Count − 4`.
   **Known 0.2.9 defect to fix, not port:** `FaceStack` draws `min(MaxVisible, billed.Count)` faces while the overflow
   is computed against `billed.Count` in full (`:55` vs `:111`), so an album billed to 6 artists shows 4 faces and a
   `+N` that counts *none* of the two it hid — two artists vanish from the row entirely. 0.3 should compute the
   overflow from the number of faces actually drawn.
6. **The cover is the column's anchor and never animates.** No entrance, no FLIP, key only; 1.18 saturation, `Radii.Card`
   8, `Elevation.Card`, decoded at 256 px so a Home card hands off the same texture (`DetailRail.cs:25, 144-162`).
7. **Late rows fade up and push, they never blink.** Every structural rail row carries a stable key; full-model-only rows
   (eyebrow, face pile, countdown, release panel, description) enter with `FadeUp` and every row FLIPs position-only on
   `Shove` (`DetailRail.cs:28-71`).
8. **An album that is not out yet says so three ways**: the countdown card (days/hrs/min/sec + spinning ring), the
   pending rows greyed to 0.45 opacity with their release date in the duration lane, and "8 of 12" in the Songs tile —
   all driven by wall-clock predicates that expire themselves (`PreReleaseCountdown.cs`, `TrackRow.cs:226, 267, 306-308`,
   `AlbumReleaseFactsRules.cs:52-69`).
9. **The heart on a full pre-release saves the PRERELEASE entity, not the album** — the target is swapped, never a second
   heart added, and the button is keyed on the target because its uri freezes at mount (`DetailRail.cs:229-234`).
10. **The trailing band is reserved from mount and swaps exactly once.** One `SkelRegionEl` with `SmoothResize: true`
    shimmers from the first frame and eases into whatever it resolves to — real sections or a collapsed empty box
    (`DetailTrailing.cs:74-106`). It must never insert itself into the page mid-scroll.
11. **Trailing sections are VERTICAL, capped at 5 rows, expandable in place.** No horizontal shelves, no pager, no
    second surface: "Show all N" lengthens the stack where it stands (`DetailTrailing.cs:436-489`, `TrailingStack` at
    `:669-709`).
12. **Album rows carry no art thumb and no Album column**, but they do carry a Plays lane and a top-track star
    (`DetailConfig.cs:203-207`, `DetailTracks.cs:399-406`, `TrackRow.cs:1080`).
13. **The narrow arm is a reflow of one hero, not a different design**: artwork · eyebrow · title · 20×2 accent rule ·
    attribution · meta · actions · description, side-by-side at or above **`RowFlowEnterW` 424** and stacked once the
    column drops to **`RowFlowLeaveW` 400** (a 24-DIP hysteresis, `DetailVerticalLayout.cs:47-51`), with the title
    SIZED by a measured type plan that spends the cover's own height (`DetailVerticalHero.cs:16-43`,
    `DetailVerticalLayout.cs:238-478`).
14. **The page has an art-derived ground, not a wash**: one clamped `PageTone` plane at α 0.20 dark / 0.30 light behind
    everything, plus a shell material tint claimed by the page (`DetailShell.cs:551-577`, `WaveePalette.cs:169-239`).
15. **Nothing on this page probes for data.** Thinness, countdown target, release facts, notice and the top-track star
    are computed once at the model boundary with an injected `now` (`DetailPage.cs:695-741`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition (wide / two-column arm, `mode 0`)

```
DetailPage (Component)                                  Features/Detail/DetailPage.cs:22    route → kind/id, loads, maps, live-refresh
└─ DetailShell (Component)                              DetailShell.cs:83                   measures width → mode, owns accent/tone/rail prefs
   ├─ CoverPaletteLeaves.ShellTint (leaf)               DetailShell.cs:321                  page-scoped shell material tint (zero-size)
   ├─ PlaylistReorderDeferWatcher (leaf)                DetailShell.cs:694                  playlist-only; inert on album
   ├─ CoverPaletteLeaves.PageTonePlane (leaf)           DetailShell.cs:575                  the art-derived page ground
   └─ BoxEl "detail:two-column"                         DetailShell.cs:657
      ├─ DetailNoticeBar.For(model, showMinifiedAlbum:false)  DetailNoticeBar.cs:42        0-height on the full album page
      └─ BoxEl (Justify=Center) → BoxEl row (MaxWidth 1600)   DetailShell.cs:639-656
         ├─ BoxEl "detail-rail-fade" (Opacity-bound)    DetailShell.cs:779                  resist-zone fade during a grip drag
         │  └─ DetailRail.Build(...)                    DetailRail.cs:122                   the rail column + its own ScrollView
         │     ├─ BoxEl "rail:cover"                    DetailRail.cs:145                   cover, shadow, drag source; NO motion
         │     │  └─ Surfaces.Artwork(... saturation 1.18, decodePx 256)   DetailRail.cs:98-105
         │     ├─ LateRow "rail:eyebrow" → EyebrowRun("ALBUM · 2019")      DetailRail.cs:167-171, 570-593
         │     ├─ Row "rail:title" → WaveeType.DetailHero                  DetailRail.cs:183-189
         │     ├─ LateRow "rail:artists" → ArtistFacePile (Embed.Comp+Props) DetailRail.cs:193-194 / ArtistFacePile.cs:20
         │     ├─ BoxEl "rail:cta" (Wrap)               DetailRail.cs:212-240
         │     │  ├─ WaveeCta.Play(accent, PlayAll)     DetailRail.cs:596 / WaveeCta.cs:74
         │     │  └─ BoxEl group: SaveButton(saveUri,40) · PlaylistInlineEdit.ShareButton(40) · OwnerMenu(empty on album)
         │     ├─ LateRow "rail:prerelease" → PreReleaseCountdown          DetailRail.cs:242, 456-460
         │     ├─ Row "rail:release" → AlbumTrailing.ReleasePanel(outerPadding:false)  DetailRail.cs:244-245
         │     │  ├─ OtherVersionsDropDown (DropDownButton)  DetailTrailing.cs:286-302
         │     │  └─ BoxEl "release-about": Eyebrow + "release-facts" (2 StatTiles + 1 full-width) + "release-notes"
         │     └─ LateRow "rail:desc" → RichText.Of(...)     DetailRail.cs:249-253   (albums rarely carry one)
         ├─ BoxEl "detail-rail-grip-strip" → Splitter    DetailShell.cs:797-817
         └─ BoxEl "right:tracks"                         DetailShell.cs:532
            └─ TrackList (Component, Key "tracks:standard:"+route)   DetailTracks.cs:28
               ├─ Chrome: command bar + (chips) + column header       DetailTracks.cs:1857
               └─ TrailingBody(ScrollView)                            DetailTracks.cs:1801-1846
                  ├─ listKeyed → Skel.Region → ItemsView.CreateBound(rows)   DetailTracks.cs:1003-1045
                  │  └─ ExpandableRowSlot → TrackRow.Grid + (drawer) TrackVersionsPanel   DetailTracks.cs:3208-3364
                  └─ AlbumTrailing (Component)                        DetailTrailing.cs:25
                     └─ SkelRegionEl (SmoothResize)                   DetailTrailing.cs:86-104
                        └─ TrailingSections  (in this fixed order)    DetailTrailing.cs:109-144
                           ├─ VideosSection          (ANY release with ≥1 video)     :355 → 0.3 rewrite
                           │    ├─ VideoHero   (exactly one video)  the 0.2.9 card, corrected
                           │    └─ VideoShelf  (two or more)        PagedShelf: header+pips, edge fade, page snap
                           │         └─ VideoShelfCard × N          16:9 still · title · duration
                           ├─ AboutArtistSection → AboutCard + FollowButton          :549-602
                           ├─ Section("Fans also like") → FansRow (≤8 ArtistChips)   :411-434
                           ├─ AlbumList("More by X") → TrailingSection→TrailingStack :470-489
                           ├─ FeaturedSection("Featured on")                          :460
                           ├─ MerchSection → MerchRow                                 :498-538
                           └─ AlbumList("Similar albums")                             :470
```

Narrow arm (`mode 3`, or the "Hero" page-layout preference at any width): `DetailShell.cs:581-613` composes
`TrackList(verticalHeader:true)`, whose virtual list carries `VerticalHeroRoot` (item 0) and `VerticalChromeRoot`
(item 1) — but for an album (`HasTrailing`) the list is the *unwindowed* body inside `TrailingBody`, so hero + chrome are
direct children of the outer scroller (`DetailTracks.cs:1092-1097, 1801-1846`) and the trailing band appends
`PreReleaseCard`, `ReleasePanel(outerPadding:true)` and `AlbumTrailing` after the rows (`DetailTracks.cs:1805-1812`).
The hero itself is `DetailVerticalHero.Build` (`DetailVerticalHero.cs:61`).

**Rail rows an album never mounts.** `DetailRail.Build` also authors `rail:owner` (playlist), `rail:meta` (playlist /
Liked — the album's `cfg.Badges == TypeYear` suppresses it, `DetailRail.cs:199`), `rail:daylist` (`FlipCountdown`, a
playlist window), `rail:chart` (chart caption) and `rail:likedfacts`. All five are gated off on the album path; do not
carry them into `Album.Page.Rail`.

**The embedded (library master-detail) album is a FIFTH arm.** `TrackList` hosted in the library's compact pane forces
`config = config with { HasTrailing = false }` and `_verticalHeader = verticalHeader && !embedded`
(`DetailTracks.cs:270-276, 701, 756`): no trailing band, no in-body release panel, no in-body countdown card, and the
rows themselves are the scroller (the pane owns the hero and actions above). It is also the ONE surface that shows the
`MinifiedAlbum` InfoBar (W18). Because `HasTrailing` is what makes the album's Full rung get asked for at all
(`DetailTrailing.cs:168-170`), the embedded pane never acquires Label/©/℗/Other-versions — by design.

**`DetailRail.BuildHeader` (the fixed 140-DIP cover-left header, `DetailRail.cs:361-441`) is NOT an album arm.** Its one
call site is `DetailShell.cs:587`, reached only when `verticalTracks` is false — i.e. the **podcast Show** page at
vertical width. An album at Vertical always takes the `DetailVerticalHero` path. Do not port BuildHeader into
`Album.Page.cs`; it belongs to ch. 09 (Show). (Its `hdr:*` keys, its `includeReleasePanel` flag and its 1.0-saturation
cover are therefore all out of scope here.)

### 1.2 The same tree in 0.3 terms

| 0.2.9 node | 0.3 home | Shape | Inputs (and how data reaches it) |
|---|---|---|---|
| `DetailPage` route parse + load | `Shell.cs` `Route(RouteKind.Album, uri)` + `Album.Page` | Component | `Album` handle from `Shell.Nav.Current.Subject`; `prerelease:` becomes `RouteKind.Album` with a `PreRelease` resolve step (§7 gap D9) |
| `DetailShell` (mode, tone, accent, rail prefs, handlers) | `Detail.Frame` in **03-detail-frame.md** | static + one Component | album passes a `Detail.Spec` value (rail width/scope, badges, heart mode, columns, trailing flag) — the 0.3 replacement for `DetailConfig.Album` |
| `DetailRail.Build` album arm | `Album.Page.Rail(Album a, in Detail.Frame f)` | **static** function | `a` (handle, re-read every render), `f.RailW`, `f.TitleSize`, `f.Accent` as `Func<ColorF>` |
| `ArtistFacePile` | `Album.UI.FacePile` | Component, **re-pushed Props** | `Props(ReadOnlySpan<int> billedSlots, int trackArtistCount, float maxWidth)` — the set changes after mount when portraits land, so this must stay `UseProps`, never ctor args |
| `AlbumTrailing.ReleasePanel` | `Album.Page.ReleasePanel(Album a, ...)` | static | reads `a.ReleaseFacts()` (pure, computed in `Album.cs` from columns) — no state |
| `PreReleaseCountdown` | `Album.UI.Countdown` | Component | `ReleaseAt` frozen at mount + **`Key = "prerelease:" + uri + ":" + ticks`** so a new target remounts; `Accent` is a `Func<ColorF>` thunk |
| `AlbumTrailing` (enrichment) | `Album.Page.Trailing(Album a)` | Component | subscribes the album table + the trailing edges; each section is its own `Knows`/edge-state gate |
| `TrailingStack` | `Album.UI.Stack(title, count, rowAt)` | Component | `Key = "trail:" + signature` — ctor args freeze, so a re-bound section remounts (0.2.9 does exactly this, `DetailTrailing.cs:488`) |
| `TrackList` + `TrackRow.Grid` | `Track.UI.Row` + the table in **04-detail-track-table.md** | bound rows | `BoundItemsSource<Track>` over `a.TrackSlots`; per-row state through the row's own signals |
| `TrackVersionsPanel` | `Track.UI.Drawer` (04) / `Album.UI` for the album-specific facts | Component + `Ctx.Provide` | props via context, exactly as 0.2.9 does (`DetailTracks.cs:3347`) |
| `AlbumDrawerPanel` (artist page) | `Album.UI.DrawerPanel` | Component, re-pushed Props | `Props(Album thin, ReadOnlySpan<int> rows, DrawerVerdict v, Action retry)`; `Key = "drawer:" + uri` |
| `DetailSkeleton.VerticalHeroBand` | `Detail.Frame.Skeleton` (03) | static | album passes its own four presence flags (eyebrow/attribution/meta/description) |

**Props-freeze map for this surface (the four that bite):**

- `Album.UI.FacePile` — **Props re-push.** Billed artists arrive with the album, portraits land later; ctor args freeze
  the raw placeholders forever (the exact bug `CollaboratorFacePile.cs:31-35` documents).
- `Album.UI.Countdown` and the daylist/flip twin — **Key remount** on the target instant; props frozen on purpose.
- `SaveButton`/heart — **Key remount** on the save uri, because a resolved prerelease link swaps the target after mount.
- `Album.UI.Stack` (a trailing section) — **Key remount** on a data signature; its expand state is per-section.
- Everything else in the rail and the release panel is a **static function re-run every render** (0.2.9's `DetailRail`
  and `DetailTrailing.ReleasePanel` are static for this reason — no frozen edge exists).

---

## 2. Wireframes

Scale: **1 char ≈ 8 DIP horizontally, 1 line ≈ 16 DIP vertically**, except where a wireframe says otherwise.
Page width = the content region (window minus the sidebar ≈ 240, `DetailLayoutBreakpoints.cs:32`).

### W1 — Fully loaded, two-column @ page 1040 DIP (mode 0, rail 280)

```
├────────────────────────── 1040 page ─────────────────────────────────────────────────────────────────────────────────┤
┌── rail 280 (Fill=Tok.FillLayerDefault, own ScrollView) ──┬─┬── right column (Grow, MinWidth 300) ───────────────────┐
│ pad 16 L / 8 R / 24 T / 24 B      gap 14 between rows    │g│ chrome padX 16, padTop 8                              │
│ ┌────────────────── cover 256×256 ─────────────────────┐ │r│ ┌─ command bar (CommandBarSurface h 44, pills 32) ──┐ │
│ │  Radii.Card 8 · Elevation.Card · saturation 1.18     │ │i│ │ [▷ Play next ▾] [⤮ Shuffle] │ [⇅ Sort][▤ Row size]│ │
│ │  decodePx 256 · Draggable(whole album)               │ │p│ │                              [☰ Select]  [🔍 Find]│ │
│ └──────────────────────────────────────────────────────┘ │ │ └───────────────────────────────────────────────────┘ │
│  ALBUM · 2019            Caption 12/16/600 +30 tracking  │6│ ┌ column header h 36 ───────────────────────────────┐ │
│                          Tok.TextTertiary, 1 line        │ │ │  #   TITLE                       PLAYS    ⏱      │ │
│  Random Access            Title 40/52/600 display face   │ │ ├───────────────────────────────────────────────────┤ │
│  Memories                 (28/36 when window H < 900)    │ │ │  1  ♡  Give Life Back to Music   12,345,678  4:35 │ │ 48
│                           −20/1000 tracking, ≤3 lines    │ │ │  2  ♡  The Game of Love           9,876,543  5:22 │ │ 48
│  ((●))((●))(●) +7  Daft Punk, …   face pile 32 frames    │ │ │  ★  ♡  Get Lucky                 1.85B       6:09 │ │ zebra
│                       names 14/700 AccentTextPrimary     │ │ │  4  ♡  Within                     3,210,987  3:48 │ │
│  ┌─ Play ─────┐ ( ♡ ) ( ⤴ )     pill h36 r-full          │ │ │  …                                                │ │
│  │ ▶  Play    │  40   40        gap 12, wrap as a unit   │ │ │                                                   │ │
│  └────────────┘                                          │ │ │  (rows continue; the list is Grow=0 inside the    │ │
│                                                          │ │ │   trailing scroller — the page scrolls, not it)   │ │
│  About this release      Eyebrow, TextTertiary           │ │ │                                                   │ │
│  ┌──────────────┐┌──────────────┐   gap 8               │ │ ├─── trailing band (one skeleton region) ───────────┤ │
│  │ 13           ││ 74 min       │   StatTile 18/800     │ │ │  Music videos / WATCH THE OFFICIAL VIDEO (≥1 video)│ │
│  │ Songs        ││ Length       │   caption 11          │ │ │  About the artist   [84 avatar] Daft Punk  [♡ Fol]│ │
│  └──────────────┘└──────────────┘   r4 FillCardSecondary│ │ │  Fans also like     (chip)(chip)(chip)(chip)…     │ │
│  ┌────────────────────────────────┐  full width, wrap 2  │ │ │  More by Daft Punk  [48][row] ×5   Show all 12   │ │
│  │ May 17, 2013                   │  lines               │ │ │  Featured on        [48][row] ×5                  │ │
│  │ Released                       │                      │ │ │  Merch              [48][name ……………… €24.99]      │ │
│  └────────────────────────────────┘                      │ │ │  Similar albums     [48][row] ×5                  │ │
│  Label: Columbia          11 px TextTertiary, gap 3      │ │ └───────────────────────────────────────────────────┘ │
│  ℗ 2013 Daft Life Ltd.    wrap ≤4 lines                  │ │                                                       │
│  © 2013 Daft Life Ltd.                                   │ │                                                       │
│  [ ♪ Other versions ▾ ]   DropDownButton (when present)  │ │                                                       │
└──────────────────────────────────────────────────────────┴─┴───────────────────────────────────────────────────────┘
```

Rail cover edge = `max(80, railW − 16 − 8)` (`DetailRail.cs:19, 96`): **280→256 · 224→200 · 188→164 · grip 180→156 ·
grip 480→456**.

### W2 — Rail interior, exact geometry (zoom: 1 char ≈ 4 DIP)

```
        ┌ rail column, Direction=1, Gap=14, Padding (16,24,8,24), Width=railW, Shrink=0 ────┐
 y=24   │ ┌ "rail:cover"  256×256  Corners 8  Shadow Elevation.Card  ClipToBounds ────────┐ │
        │ └──────────────────────────────────────────────────────────────────────────────┘ │
 +14    │   "rail:eyebrow"  Width=256  Caption 12/16/600  CharSpacing 30  TextTertiary     │
 +14    │   "rail:title"    Width=256  Size 40 MinSize 18 LineHeight 52 Weight 600         │
        │                   WrapWholeWords MaxLines 3 CharacterEllipsis                    │
 +14    │   "rail:artists"  MaxWidth=256  [ (32)(32)(32)+N ⌄ ]  gap 8  names 14/700 accent │
 +14    │   "rail:cta"      Margin-top 4  Direction=0 Wrap Gap=12 AlignItems=Center        │
        │                   [Play pill h36]  gap 12  [ (40)(40) ] group gap 8              │
 +14    │   "rail:prerelease"  (only when UpcomingAt != null) — card, see W13              │
 +14    │   "rail:release"  ReleasePanel(outerPadding:false) — Gap 12, no padding          │
 +14    │   "rail:desc"     Width=256  12 px TextSecondary  ≤6 lines (≤3 when winH<760)    │
        └──────────────────────────────────────────────────────────────────────────────────┘
```

`Gap = 14` is off the 4-grid and deliberate (`DetailRail.cs:264`). The rail is wrapped in
`ScrollView(...) { Grow=1, Shrink=1, MinHeight=0, Width=railW }` inside a `ClipToBounds` box filled
`Tok.FillLayerDefault` (`DetailRail.cs:290-298`).

### W3 — Mid / narrow two-column (mode 1 @ page 700 → rail 224; mode 2 @ page 600 → rail 188)

```
mode 1 (page 660–819)                            mode 2 (page 560–659)
┌ rail 224 ──────────┬──┬ tracks ──────────┐     ┌ rail 188 ──────┬──┬ tracks ─────────────┐
│ cover 200×200      │  │ # ♡ TITLE  ⏱ ⌄   │     │ cover 164×164  │  │ # ♡ TITLE   ⏱ ⌄    │
│ ALBUM · 2019       │  │ (Plays drops at  │     │ ALBUM · 2019   │  │ (tier 2/3 + relief) │
│ Random Access …    │  │  tier ≥ 3, or at │     │ Random …       │  │                     │
│ ((●))(●) +7 Daft…  │  │  relief step 1)  │     │ ((●))(●) +7    │  │                     │
│ [▶ Play] (♡)(⤴)    │  │                  │     │ [▶ Play](♡)(⤴) │  │                     │
│ facts tiles 2-up   │  │                  │     │ facts 2-up     │  │                     │
└────────────────────┴──┴──────────────────┘     └────────────────┴──┴─────────────────────┘
```

Mode ladder (`DetailLayoutBreakpoints.cs:68-87`), on **page** width: `≥820 → 0 · ≥660 → 1 · ≥560 → 2 · else Vertical(3)`;
hysteresis **24 DIP** when widening (narrowing is immediate — `nominal >= currentMode` returns at once; widening re-runs
the ladder at `w − 24`); vertical is entered below **540** (`VerticalEnterW`) and left at **≥580** (`VerticalExitW`).
**The 540–560 band is mode 2, not Vertical**: once already two-column, a nominal Vertical above `VerticalEnterW` is
clamped up to 2 (`DetailLayoutBreakpoints.cs:83, 86`), so the rail holds at 188 for those 20 DIP. Rail width per mode:
`0 → the persisted width (default 280, clamped 180…480) · 1 → 224 · 2 → 188` (`DetailShell.cs:192, 629`;
`DetailRailPolicy.cs:25`). The resizable grip exists at mode 0 only (`DetailRailPolicy.cs:20, 28`) — at modes 1/2 the
row's children are `[railFaded, right]` with **no grip strip node at all** (`DetailShell.cs:791-793`), so the `│ │`
drawn above is a zero-width seam there, not a hit target.

### W4 — Rail collapsed (drag past the detent; mode 0 only)

```
┌ 96 ──┬──┬ tracks (keeps its Key, so scroll survives) ─────────────┐
│ ┌──┐ │20│   The compact strip: Padding (8,16,8,16), Gap 8,         │
│ │80│ │  │   Fill=Tok.FillLayerDefault, ClipToBounds                │
│ └──┘ │  │   cover = max(48, stripW−16) → 80, Corners 8,            │
│ Rand │  │   Shadow Elevation.Card, click=expand                    │
│ Acce │  │   title 12/600 ≤2 lines WrapWholeWords, Width = cover    │
│  ›   │  │   spacer (Grow, MinHeight 0) pushes the chevron to foot  │
│      │  │   [ › ] Icons.ChevronRight 14 TextSecondary, box 28 tall │
│      │  │        Radii.Control, hover FillSubtleSecondary, =expand │
│      │  │   ToolTip.Wrap(strip, title) — the WHOLE strip           │
└──────┴──┴─────────────────────────────────────────────────────────┘
```

`DetailRail.cs:304-354`; collapse detent: the grip resists below 180 and collapses at raw ≈136 (`ForcePush 44`),
re-expands only past 220 (`DetailShell.cs:200-206`). Unlike modes 1/2, the collapsed arm **does** mount a grip strip
node — `[compact, grip(collapsedNow: true), right]` at `GripStripCollapsedW` 20, because the seam is then also the
re-open gesture (`DetailShell.cs:760-770, 797-817`). There are **two** hit targets inside the strip (the cover and the
chevron), both calling `expand`; the title between them is not clickable.

### W5 — Vertical hero, row flow @ page 560 (art 215, copy 273)

```
┌ page 560 ────────────────────────────────────────────────────────────┐
│ pad 24                                                               │
│ ┌ art 215×215 ─────┐ gap 24 ┌ identity (MinHeight = 215) ──────────┐ │
│ │ Radii.Card 8     │        │ ALBUM · 2019          eyebrow 16      │ │
│ │ Elevation.Card   │        │ Random Access         title 40/53 ×2  │ │
│ │ saturation 1.18  │        │ Memories              (worked ex. §2a)│ │
│ │ decodePx 512     │        │ ▬▬                    rule 20×2 accent│ │
│ │                  │        │ Daft Punk             attribution 12  │ │
│ │                  │        │ 13 songs · 74 min · 2013   meta ≤2 ln │ │
│ │                  │        │ (slack lands in the interior gaps)    │ │
│ │                  │        │ [▶ Play](⤮)(♡)(⤴)(⋯)   actions 36/32 │ │
│ └──────────────────┘        └──────────────────────────────────────┘ │
│ pad-bottom 8                                                         │
│ ┌ toolbar band 44 (padX = TrackRow.PadXFor(tier)) ──────────────────┐ │
│ │ [Play next ▾][Shuffle] │ [Sort][Row size][Select]      [🔍 Find] │ │
│ └───────────────────────────────────────────────────────────────────┘ │
│ ┌ column header 36 ─────────────────────────────────────────────────┐ │
│ │  #   TITLE                                    PLAYS        ⏱     │ │
│ ├───────────────────────────────────────────────────────────────────┤ │
│ │  rows …                                                           │ │
│ │  [prerelease countdown card]  (vertical arm only, after the rows) │ │
│ │  [About this release panel]   ReleasePanel(outerPadding:true)     │ │
│ │  [trailing sections]                                              │ │
│ └───────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
```

Geometry (`DetailVerticalLayout.cs:118-165`): `pad = 24` in row flow; `art = round(clamp(0.44 × (w − 2·pad − gap), 144,
240))`; `contentW = min(640, max(160, w − 2·pad − gap − art))`; `titleW = min(1000, …)`.
Worked: **w=560 → art 215, copy 273**; **w=900 → art 240, copy 588**; **w=1200 → art 240, copy 888 → contentW 640,
titleW 888**.
Constants (`DetailVerticalLayout.cs:29-93`): `HeroPad 24 / NarrowHeroPad 16`, `HeroGap 24 / NarrowHeroGap 16`, the
pad/gap switch at `NarrowPadW 420` **on the column width, independently of the flow breakpoint** (so a row-flow hero
always takes 24/24 — `HeroPadFor(colW, rowFlow: true)` short-circuits, `:123-124`), `RowArtMin 144 / RowArtMax 240 /
RowArtFraction 0.44`, `ArtMin 96 / StackedArtMax 280`, `ContentWMin 160 / ContentWMax 640`, `TitleWMax 1000`,
`HeroBottomPad 8`, `AccentRuleRowHeight 4` (a 2-DIP rule + a 2-DIP top margin), `FallbackW 580`.
Cover decode is a three-rung bucket, not the art size (`ArtworkDecodePx`, `:643-645`): `≤128 → 256 · ≤288 → 512 · else
1024`. Every album art size on this page (96…280) lands on **512**; the 1024 rung is unreachable here.

**§2a — the title type plan, worked twice** (`DetailVerticalLayout.cs:421-494`):
chrome = eyebrow 16 + rule 4 + attribution 16 + meta 16 + actions 40 = **92**, blocks 6 → gaps 5×4 = **20**.

- *w = 900, title "Pony"*: budget = 240 − 92 − 20 = **128**; cap = `FluidTitleCapFor(900)` = 0.0919×900 − 5.08 = **77.6**;
  1 line: widthFit = min(0.926×588/2.22, 588/2.22) = 245, heightFit = 128/1.3301 = 96.2 → cand **77.6**; 2 lines:
  heightFit = 48.1 → loses. ⇒ **Size 80, LineHeight 106, MinSize 72, 1 line, wrap 588.**
- *w = 560, title "Random Access Memories"* (advance ≈ 11.97 em, longest word ≈ 4.48 em): budget = 215 − 92 − 20 = **103**;
  cap = **46.4**; 1 line cand = 21.1; 2 lines: widthFit 42.9, heightFit 38.7 → cand **38.7** wins.
  ⇒ **Size 40, LineHeight 53, MinSize 36, 2 lines, wrap 273** (block height 106).

### W6 — Vertical hero, stacked @ page 400 (art 280, copy 368)

```
┌ page 400 ─────────────────────────────┐   pad = 16 below 420 (NarrowPadW), gap 16
│ ┌ art 280×280 ──────────────────────┐ │   art = round(clamp(400 − 32, 96, 280)) = 280
│ │                                   │ │   contentW = 368, identity MinHeight = 0   decodePx 512
│ └───────────────────────────────────┘ │
│ ALBUM · 2019                          │
│ Random Access Memories                │   title plan: heightBudget = 0 (stacked) ⇒ the
│ ▬▬                                    │   fluid cap + width fit alone decide the size
│ Daft Punk                             │
│ 13 songs · 74 min · 2013              │
│ [▶ Play](⤮)(♡)(⤴)(⋯)   wraps as a row │
└───────────────────────────────────────┘
```

### W7 — Loading, two-column (cold open, no nav preview)

```
┌ rail 280 ───────────────────┬─┬ right column ───────────────────────┐
│ ░░░░░░░░ cover 256 ░░░░░░░░ │ │ ░░ command-bar pills ░░             │
│ ░░░░░░░ (shimmer) ░░░░░░░░░ │ │ ░ column header ░                   │
│ ░░░░░░░░░░░░                │ │ ░░░░░░░░░░░░░░░░░░░░░░░░  ░░░  ░░   │ 48
│ ░░░░░░░░░░░░░░░░░░░         │ │ ░░░░░░░░░░░░░░░░░░░░░░░░  ░░░  ░░   │ ×8 (PendingSeed)
│ ░░░░░░░ ░░░░ ░░░            │ │ …                                   │
│ ░░░░░ ░░ ░░                 │ │ ┌ trailing region, reserved ───────┐ │
└─────────────────────────────┴─┴ │ ░ 160×18 header ░   ← §1 of 3    │ │
                                  │ ░ 96-tall card ░     r8 Card     │ │
                                  │ ░ 160×18 header ░   ← §2 of 3    │ │
                                  │ ░ 5 × 132×40 chips (r20) ░       │ │
                                  │ ░ 160×18 header ░   ← §3 of 3    │ │
                                  │ ░ 3 × 64 rows (48 thumb + 160×12)│ │
                                  └───────────────────────────────────┘ │
```

The whole page shimmer is DERIVED from the real tree rendered against `PendingSeed(DetailKind.Album)` — 8 blank tracks,
`BadgeType = " "`, `MetaLine = " "`, `ContextUri = "pending:detail"` (`DetailPage.cs:346-379`), through
`Skel.Region(..., reveal: SkelReveal.FadeOnly, smoothResize: false)` (`DetailPage.cs:273-290`).
With a nav preview present (a click from Home/search/library) the shell renders LIVE header content immediately and only
the rows shimmer (`DetailPage.cs:263-264`).

**Trailing skeleton, exactly** (`DetailTrailing.cs:605-657`): `TrailingSkeleton` is THREE stacked `SectionSkeleton`s,
each of which carries **its own 160×18 r4 `FillCardDefault` header bar** over one body, with the real section's padding
`(16,20,16,16)` and gap 12. Bodies, in order: a **96-tall `Radii.Card` `FillCardDefault` block** (stands in for the
about-artist card), **5 × 132×40 r20 `FillCardDefault` chips** in a `ClipToBounds` row at gap 8, and **3 × 64-tall
`Radii.Card` `FillCardSecondary` rows** (gap 4, padX 8, gap 12) each holding a 48 r4 thumb block and a 160×12 r4 bar.
There is no music-video, merch or "show all" placeholder: the reserve deliberately under-states rather than over-states.
(The music-video section is instead held IN the reserve — `PageRules.VideoDecided` keeps the band shimmering until every
member knows its `TrackFields.Video` group, at any release length — so it never appears a frame after the reveal and
shoves About-the-artist down. `SmoothResize` eases whatever the under-state got wrong.)

### W8 — Loading, vertical hero band

`DetailSkeleton.VerticalHeroBand` composes the same parts at the same sizes from the same resolver
(`DetailSkeleton.cs:39-130`): eyebrow bar = 32 % of contentW × 16 · title = the pessimistic plan's line count at its own
line height (last line 68 %) · accent rule 20×2 (+2 top margin) · attribution 40 % × 16 · meta 62 % × 16 · action row
[104×36 pill + 4 × 32 squares] · description 4 lines × 18 (last 55 %) · toolbar box 44 with pills 72/88/64 + right-docked
`SearchPreferred`. `MinHeight = HeroBandHeight(...)` — the same number the loaded hero's pre-measure collapse binds use.

### W9 — Reveal in progress (the first ~5 frames after Ready)

```
│  1  ♡  Give Life Back to Music   12,345,678  4:35 │  ← real (displayIndex < reveal)
│  2  ♡  The Game of Love           9,876,543  5:22 │  ← real
│ …12 real rows (DetailRevealRamp.Chunk) …          │
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ░░░░░░░░  ░░░░    │  ← ShimmerRow, cross-fades to real
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ░░░░░░░░  ░░░░    │
```

12 rows per frame, cap 60, then `Done` (`DetailRevealRamp.cs:11-13`). **Album pages ramp on EVERY context change, cold
or warm**, because a `HasTrailing` list is one unwindowed mandatory band (`DetailTracks.cs:777-791`).

### W10 — Scrolled, vertical arm (the sticky context band)

```
┌ page ────────────────────────────────────────────────────────────┐
│ Random Access Memories                              [⤮][♡][⋯]   │ 56  ← ContextBand.Row, NO fill
│ 13 songs · 74 min · 2013                                         │      title BodyStrong 14/20/600
│──────────────────────────────────────────────────────────────────│      byline Caption tertiary
│  #   TITLE                                 PLAYS         ⏱      │ 36  ← the tracklist column header
│──────────────────────────────────────────────────────────────────│  1  ← the band's ONE hairline
│  7  ♡  Doin' It Right                    2,345,678     4:11      │
│  (content is CLIPPED at 56+36+1 = StickyClipInset, never painted │
│   under the band; a 24-DIP feather at the cut)                   │
└──────────────────────────────────────────────────────────────────┘
```

`ContextBand.cs:71-96`, `DetailVerticalLayout.cs:78, 90-92, 587-591`. The band has **no material at all**; the expanded
hero translates up by `CollapseDistance` and fades over its last 96 DIP (`DetailVerticalHero.cs:436-445`). The
two-column arm has no sticky band (its rail is a full-height sibling column).

### W11 — "About this release", every state of the same fixed grid

```
first paint (Full not in yet)        Full lands                       future release
┌───────────────────────────┐        ┌───────────────────────────┐    ┌───────────────────────────┐
│ (panel does not exist)    │   →    │ About this release        │    │ About this release        │
│  the rail row is absent   │        │ ┌────────┐┌────────┐      │    │ ┌────────┐┌────────┐      │
│  entirely — HasReleasePanel│       │ │ 13     ││ 74 min │      │    │ │ 8 of 12││ 31 min │      │
│  reads facts.IsEmpty       │       │ │ Songs  ││ Length │      │    │ │ Songs  ││ Length │      │
└───────────────────────────┘        │ └────────┘└────────┘      │    │ └────────┘└────────┘      │
                                     │ ┌───────────────────────┐ │    │ ┌───────────────────────┐ │
                                     │ │ May 17, 2013          │ │    │ │ September 4, 2026     │ │
                                     │ │ Released              │ │    │ │ Releases              │ │
                                     │ └───────────────────────┘ │    │ └───────────────────────┘ │
                                     │ Label: Columbia           │    │ Label: …                  │
                                     │ ℗ 2013 …                  │    │ ℗ …                       │
                                     │ [ ♪ Other versions ▾ ]    │    └───────────────────────────┘
                                     └───────────────────────────┘
```

Tile: `StatTile.Create` — 18/800 value (`MaxLines 1`, or 2 with `wrapValue` on Released), 11-px caption
`TextSecondary`, padding 12/8/12/8, column gap **1**, `Radii.Control` 4, `FillCardSecondary` + 1-px `StrokeCardDefault`,
`Grow 1 Basis 0 MinWidth 0 Shrink 1 ClipToBounds` (`StatTile.cs:21-56`). At rail 280 the two row-1 tiles are
(256 − 8)/2 = **124** wide; at the grip floor 180 → **74**; at the ceiling 480 → **224**; mode 1/2 → **96 / 82**.
The value box is a `ZStack` with `MinWidth 0` over a `Key = "v:" + value` child carrying `MotionRecipes.TextSwap`, so a
refined value cross-fades **inside a box the row already sized** — an unconstrained ZStack would measure to the wider of
the outgoing/incoming runs mid-swap. `StatTile.Create` also takes an optional `trailing` element (unused on this page).

**Three more states of the same grid the sketch does not draw:**

- **A missing fact is an em dash, not a missing tile.** `StatTile.Create("songs", facts.Songs ?? "—", …)` — the row-1
  pair and the Released row are authored unconditionally once `facts.HasTiles` is true, so a record with a Released date
  but no durations reads `[13][—] / [May 17, 2013]` rather than reflowing to one tile (`DetailTrailing.cs:240-249`).
- **Notes-only panel.** `HasTiles` is `Songs is not null || Length is not null || Released is not null`
  (`AlbumReleaseFactsRules.cs:21`). A record with only a label/© and no tiles mounts the eyebrow + the notes column with
  **no tile grid at all** — and `HasReleasePanel` is `!facts.IsEmpty || OtherVersions.Count > 0`
  (`DetailTrailing.cs:200-201`), so an album with *only* other versions shows the dropdown with no "About this release"
  eyebrow above it.
- **Length arithmetic.** `Length` is summed over the tracks that are **out**, and only when that sum is > 0; a
  sub-minute total floors up to `"1 min"`, never `"0 min"`; an hour-plus total reads `"1 hr 12 min"`
  (`AlbumReleaseFactsRules.cs:59-68, 110-115`). `Songs` is `"13"` when every row is out, `"8 of 13"` otherwise.
  `Released` is `YEAR → "2014" · MONTH → "November 2014" · DAY/absent → "November 4, 2014"`, falling back to the bare
  year, and **an unparseable ISO string yields null** — the mapper's old raw-echo fallback is deliberately dropped
  (`:91-102`).

### W12 — Trailing sections (one section, expanded and capped)

```
┌ Section: padding (16,20,16,16) ───────────────────────────────────────┐
│ More by Daft Punk                              Show all 12            │  header Subtitle 20/28/600 + HyperlinkButton Small
│ gap 12                                                                │
│ ┌ 64 ────────────────────────────────────────────────────────────────┐│  MediaCard.Row: h 64, Radii.Card 8,
│ │ [48 cover r4]  Discovery                                           ││  FillCardSecondary + 1px StrokeCardDefault,
│ │   ▷ hover FAB 30   Daft Punk                     (drag source)     ││  hover FillCardDefault, gap 12, padX 8
│ └────────────────────────────────────────────────────────────────────┘│  title BodyStrong 14/20/600, subtitle 12 rich
│ … 5 rows (TrailingStack.Cap), gap 4 …                                 │
└───────────────────────────────────────────────────────────────────────┘

About the artist card                       Fans also like                Merch row
┌───────────────────────────────────┐       ( ● )Artist  ( ● )Artist      ┌──────────────────────────────┐
│ (84 round) ABOUT THE ARTIST       │       chip h48 r24, avatar 32,      │ [48] Random Access Tee  €24.99│
│            Daft Punk ✓  [♡ Follow]│       name 14/600, padding 8/0/16/0 │ h 64, price 12/600 accent     │
│            bio 13 px, ≤2 lines    │       ≤8 chips, ClipToBounds        └──────────────────────────────┘
└───────────────────────────────────┘
```

`DetailTrailing.cs:482-489` (section wrapper), `:669-709` (stack), `:549-602` (about card), `:411-434` (chips),
`:498-538` (merch).

**States the sketch omits:**

- **Only FOUR of the seven sections are `TrailingStack`s.** "Featured on", "More by", "Merch" and "Similar albums" go
  through `TrailingSection` → `TrailingStack` and therefore carry the 5-row cap and the "Show all N" link
  (`DetailTrailing.cs:460-500`). **"Fans also like" is a plain `Section(...)`** — a `ClipToBounds` row of at most 8 chips
  with no cap link and no expand (`:121, 411-415, 540-547`), **"About the artist"** is a single card in its own padded
  box (`:549-556`), and **the music-video section** is a single card OR a horizontal `PagedShelf` (`:355`).
  §0.11's "capped at 5, expandable in place" is true of the four list sections only.
- **"Show all N" is ONE-WAY.** `setExpanded(true)`; there is no "Show less", and the link disappears once `_count <=
  shown` (`DetailTrailing.cs:694-697`). Contrast the artist-page album drawer's "Show all N tracks", which **navigates**
  rather than expanding (W19) — do not unify the two in 0.3.
- **Merch with no price** falls back to the localized `artist.buy` ("Buy"), still in accent ink (`:531`). Merch with no
  `ShopUrl` additionally loses its hover/press scale, its `AutomationRole`, its focusability, its hand cursor and its
  click (`:516-522`) — it is a listing, not a dead button.
- **The music-video section (0.3 REWRITE — do not port 0.2.9 here).** 0.2.9 drew ONE "Watch the official video" card,
  on short releases only, showing the **album's** cover, the **album's** title and the **album's** meta line, and
  clicking it played the **album** (`DetailTrailing.cs:355-408, 369`). Every one of those four is now wrong on purpose.
  The 0.3 rules, in full:

  - **Presence: one section per ALBUM, at any length, whenever at least one member row has a video.** The old gate was
    `shortRelease && hasVideo`, so a 12-track album with three music videos surfaced **nothing at all**. `shortRelease`
    now governs only the "Fans also like" seed and the trailing skeleton's shape.
  - **Selection: `Album.PageRules.SelectVideos(members, into)`** — a pure fold, engine-free, one `AlbumVideo`
    `(MemberSlot, CounterpartSlot, Thumb, DurationMs)` per member whose `Track.HasVideo` holds (the kind-99 association
    OR a user-attached mp4), **in track order**, capped at `PageRules.VideoCap` (16, the artist shelf's own cap). Kind
    99 carries at most ONE counterpart per track (`video_associations.proto:21-23`, `optional` not `repeated`), so N
    videos on an album means N video-bearing rows; there is no album-level video edge to read.
  - **THE THUMBNAIL IS THE VIDEO'S OWN 16:9 STILL, NEVER THE ALBUM COVER.** This is the rule the old spec never wrote
    down, which is exactly how the sleeve-under-a-play-badge bug survived a parity pass. The ladder is the versions
    drawer's, verbatim (`Track.Drawer.cs:458`): the member's own `Track.VideoImageId` (what kind 99 writes beside the
    counterpart uri — `Spotify.Decode.cs:1435-1437`) ▸ the counterpart row's `ImageId` ▸ the song's own `ImageId`. The
    last rung exists for a user-attached mp4, which has no still anywhere.
  - **THE CLICK WATCHES THE VIDEO — and "play the song" is NOT that.** Kind 99 keys a video on its SONG, so the
    playable is the MEMBER row, not the counterpart — the same target `TrackVersionsPanel`'s own play verb resolves
    (`Track.Drawer.cs` `PlayVersion`). But starting that row is only half the instruction: the reducer decides a row's
    media kind from `videoWanted && (flags & TrackFlags.VideoMask)` (`Playback.Transitions.cs:697`, `KindOfRow`), and
    `videoWanted` comes from the placement state, whose `Requested` starts at `SurfacePlacement.None`
    (`Shell/Video.cs:142-150`, resolved `:249`). **Playing the row with the surface off therefore gives exactly what it
    says: audio** — a play badge over a video still that starts the ordinary song. That was the shipped defect.
    - The decision is `Album.PageRules.WatchFor(canHostVideo, isDeckRow)` — pure, three outcomes, no video count, so
      the hero card and a shelf cell cannot diverge:
      - `RequestThenPlay` (a host exists, the row is cold) — **request the surface FIRST, then play.** The order is the
        contract: the reducer's inbox is FIFO and folded in one batch, so the placement input lands before the load and
        `KindOfRow` already reads `videoWanted = true`. Play first and the user hears a beat of audio and then a switch.
      - `SwitchInPlace` (a host exists, the clicked row is already on the deck) — the placement input **alone** re-decides
        the row's kind and reloads it on the video host at the carried position (`DoVideoPlacement`). Never `PlayContext`,
        which would restart it. A paused deck is resumed with `TogglePlay`.
      - `AudioOnly` (`UpgradeGate.AvailabilityFor(hasVideo, hostCapable)` is empty — no rail room, no second window, no
        fullscreen hook) — **the card SAYS so** (`Notify.Say(player.videoUnavailable, Warning, dedupeKey
        "album.video.nohost")`) and then plays the song. It is the one arm that ends in audio and it is never silent
        about it.
    - **The request is `Video.State.FoldAvailability(hasVideo)` then `Video.State.OpenAt(preferred)`** — the rail's own
      "show video here" pair, each of which `Commit`s directly, so `Commit` → `PlacementPost.ShouldPost`
      (`Shell/Video.Host.Wiring.cs:115`) → `Playback.SetVideoPlacement(true)` (`Playback.Host.cs:1006`). The fold must
      come first: `OpenAt` resolves against `Available`, which a deferred upgrade leaves stale at `None`. `preferred` is
      G-150's persisted home (docked when nothing is remembered) — the same one the player bar's badge opens at, so a
      card click and the badge put the surface in the same place.
    - **NOT `Video.State.TogglePrimary` / `UpgradeGate.PrimaryClick`.** That path is (a) a TOGGLE — a second card click
      would turn the surface *off* — and (b) routed through `UpgradeGate.DeferUpgrade` (`Shell/Video.cs:652-653`), which
      exists to withhold a mid-track upgrade **nobody asked for**. A click on a video card *is* the ask, so it must never
      be handed to a gate built to ignore un-asked-for ones. This is the one seam where the card deliberately does not
      reuse the badge's entry point.
    - One `hasVideo` feeds **both** halves — the availability gate and the fold. Asking the gate a hardcoded `true` while
      the fold stamps the row's real bit is how the two would disagree and reopen the defect from the other side.
  - **Title and duration come from the VIDEO.** The counterpart row's `Title`/`DurationMs` once its identity has landed;
    the song's until then (an upgrade, never a blank). The counterpart's identity is one `FetchPriority.Prefetch` ask
    from the section itself (`SectionsHost.DemandVideos`) — kind 99 hands the page a uri and a still and nothing else —
    and the counterpart rows' versions are folded into `SectionsStamp.Videos` so the upgrade actually repaints.
  - **ONE video → the hero arm**, which keeps 0.2.9's geometry exactly (200×116 thumb, 44 FAB, `RailHeader` title,
    12/TextSecondary subtitle), so the single-video page looks unchanged apart from the corrected image, title,
    subtitle and click. Its eyebrow is still `detail.watchOfficialVideo`, swapping to `videoOverride.customLabel` when
    a row carries a user-attached override (`PageRules.HasCustomVideo`). Its subtitle is `detail.versions.musicVideo`
    ("Music video") · the video's duration — **not** the album's "N songs · M min · year".
  - **TWO OR MORE → the SHELF arm. A horizontal strip, NOT a wrapping grid.** A wrap row of fixed 216-wide cells was
    built first and rejected on sight: four videos fell into a 2×2 block that reads as a page of its own rather than a
    strip under the tracklist. The replacement is the **shared `FluentGpu.Controls.PagedShelf`** — the same control the
    artist page's own music-video shelf runs on (`Artist.Page.cs:1008-1021` `VideosShelf` → `ShelfOf`), and the same one
    home, browse and search shelves use. **Nothing here is hand-rolled**, and nothing here may be:
    - **Edge fades** are the control's `edgeFade:` parameter — `Design.Size.FadeShelf` (24), the token every other
      Wavee shelf passes. It reaches the viewport as the engine's `AutoEdgeFadeBand` scratch-buffer fade, so it is a
      real feather over the clipped strip and **not** a gradient overlay faked on top of the cards.
    - **Pips** are `ShelfPager.Pips` — the stock `PipsPager` the control drops into its own header row, beside
      `ShelfPager.Chevrons`. The control builds `[header, spacer, pips, chevrons]` itself
      (`PagedShelf.cs:1360-1393`), so the `header:` this page passes is **only** the title + count; the pager lands at
      the trailing edge on its own. Pips appear only when `pageCount > 1`.
    - **`snap: ShelfSnap.Page`** is what makes a fling, a chevron and a pip all rest on a page boundary — without it the
      pips would point at a strip resting mid-page.
    - **`measured: true`** — an album has a handful of videos, so the strip lays them ALL out and sizes itself to the
      tallest card instead of estimating a height from a `cardHeight(w)` formula (the `Concert.Page` / `Modules.UI`
      watch-shelf idiom). **This is what keeps the shelf from jumping**: nothing is guessed and then corrected, and each
      card's thumb carries an explicit `Width`/`Height`, so a still decoding late repaints inside a box that was already
      reserved. `maxItems: PageRules.VideoCap`.
    - `minCardW: 200, maxCardW: 280` — wider than the 148–188 square-card shelf range, because a 16:9 card needs the
      room; `gap: Spacing.M`, `headerGap: Spacing.M`, `prevGlyph/nextGlyph: Icons.ChevronLeft/ChevronRight`.
    - The `cardAt` and `keyOf` delegates are **static readonly** fields (`s_videoCard`, `s_videoKey`): `PagedShelf`
      re-pushes them as props on every render and ignores them in its gate, so rebuilding them per render would be pure
      allocation. The **items** array is what gates, and `AlbumVideo` is a `readonly record struct`, so the shelf's
      clamped element-by-element compare short-circuits an unchanged publication. The `header:` Element rides a
      *separate* chrome signal, so rebuilding it each render re-renders the header row only — never a card.
    - Header: `artist.musicVideos` ("Music videos") as a `RailHeader`, then the plain count (13/600 `TextTertiary`).
    - One cell: the video's own still fitted 16:9 to `inner = max(96, cardW − 2×8)`, `thumbH = round(inner × 9/16)`,
      over a one-line `TrackTitle` and a one-line `TrackMeta` duration (omitted entirely when the duration is 0).
  - **Both arms are the same card chrome**: `FillCardSecondary` + a 1-px `StrokeCardDefault`, `HoverFill =
    Tok.FillCardDefault`, `ClipToBounds`, **no** press fill and **no** hover scale — unlike the media rows and chips
    beside them. 0.3 adds `Focusable` + `FocusVisualMargin = Design.FocusInsetBordered`; 0.2.9's card had a
    `AutomationRole.Button` and a hand cursor but **no keyboard stop at all**. Both arms carry the same 44 play FAB
    (`VideoThumb`, clamped `min(w,h) × 0.38` into `[28, 44]`, so a hero thumb and every reachable shelf-card width land
    on the same 44 object) and take the same `WatchVideo(v.MemberSlot)` click.
  - Its section box keeps the one asymmetric trailing padding, `(16,20,16,**0**)`, because About-the-artist under it
    supplies the bottom step; the shelf arm's own header→strip gap is the control's `headerGap` (12).

```
        ┌──────────────────────────────────────────────────────────────────────────┐
        │  Music videos  4                                    ● ○      ‹    ›      │  RailHeader + count · pips · chevrons
        │                                                                          │
        │  ┌────────────┐  ┌────────────┐  ┌────────────┐  ┌───────────┐▒          │  ▒ = edge fade (AutoEdgeFadeBand, 24)
        │  │ ▓▓▓▓▓▓▓▓▓▓ │  │ ▓▓▓▓▓▓▓▓▓▓ │  │ ▓▓▓▓▓▓▓▓▓▓ │  │ ▓▓▓▓▓▓▓▓▓ │▒          │
        │  │ ▓▓▓▓(▶)▓▓▓ │  │ ▓▓▓▓(▶)▓▓▓ │  │ ▓▓▓▓(▶)▓▓▓ │  │ ▓▓▓(▶)▓▓▓ │▒          │  16:9 still, 44 FAB centred
        │  │ ▓▓▓▓▓▓▓▓▓▓ │  │ ▓▓▓▓▓▓▓▓▓▓ │  │ ▓▓▓▓▓▓▓▓▓▓ │  │ ▓▓▓▓▓▓▓▓▓ │▒          │
        │  │ Last Chri… │  │ Everythin… │  │ Wake Me U… │  │ Freedom   │▒          │  TrackTitle, 1 line
        │  │ 4:38       │  │ 5:01       │  │ 3:52       │  │ 5:03      │▒          │  TrackMeta duration
        │  └────────────┘  └────────────┘  └────────────┘  └───────────┘▒          │
        └──────────────────────────────────────────────────────────────────────────┘
           ├── 200…280 ──┤  ├─12─┤                        page-snapping, one row, never wrapped
```

  - **The reserve waits for the verdict at any length.** `PageRules.VideoDecided(members)` lost its `shortRelease`
    short-circuit: the section is the FIRST in the band, so landing a frame late would shove About-the-artist down.
    `PageRules.TrailingDeadlineMs` (400) is still the fail-soft cap.
- **Chip and media-row press states.** An artist chip carries `PressedFill = Tok.FillSubtleTertiary` on top of its hover
  fill (`:423`); a `MediaCard.Row` carries the same press fill plus a 1-px `StrokeCardDefault` border, and its whole row
  — not the cover — is the `AutomationRole.Button` that reveals the hover FAB (`MediaCard.cs:1039-1060`).
- **Image fallbacks differ by row type.** The about-artist portrait and the face-pile/flyout avatars are
  `PersonPicture.Create(displayName:)` → **initials** when no portrait exists. The "Fans also like" chips, the merch
  thumb and the media-row covers are `Surfaces.Artwork(...)` → the **hashed placeholder plate**, never initials.
- **A verified artist** adds `Icons.Check` at 12 in `TextSecondary` beside the 20/700 name (`:591`); unverified draws an
  empty box in that slot, so the name's measure does not change.
- **Section presence is per-section and fail-soft.** Each of the five enrichment calls is wrapped in `Safe(...)`, so a
  failure returns the empty fallback and that section simply does not mount — the band never reports an error
  (`:189-194`). `shortRelease` = `ReleaseKind == AlbumKind.Single || (tracks.Count is > 0 and <= 2)` (`:65`). In 0.2.9
  it did two jobs; in 0.3 it does **one**: it switches "Fans also like" from the artist's related artists to the **seed
  track's** (`:320-331`) and shapes the trailing skeleton. It no longer gates the music-video section — see the
  music-video bullet above.

### W13 — Prerelease / upcoming album

```
rail (or, on the vertical arm, after the rows)
┌ countdown card: padding 12, Radii.Card 8, FillCardDefault, 1px StrokeCardDefault, gap 12 ┐
│  ( ◜ )   Coming soon                     eyebrow in the page ACCENT                      │
│  ring34  ┌──────┐┌──────┐┌──────┐┌──────┐  wrap-grow tiles, gap 4                        │
│          │ 12   ││ 04   ││ 37   ││ 09   │  value 18/800 · caption 11                     │
│          │ DAYS ││ HRS  ││ MIN  ││ SEC  │  (2×2 in a narrow rail, one row when wide)     │
│          └──────┘└──────┘└──────┘└──────┘                                                │
└──────────────────────────────────────────────────────────────────────────────────────────┘
CTA cluster:   [▶ Play]   ( ♡ → saves spotify:prerelease:… )   ( ⤴ )

track table (partly released album)
│  1  ♡  Out Now Track                      1,234,567   3:41 │  full opacity
│  2  ♡  Pending Track                          —       4 Sep│  title column Opacity 0.45, Plays "—",
│  3  ♡  Pending Track                          —       4 Sep│  duration lane = ShortDate("4 Sep" /
│        ↑ hover play suppressed on these rows                │  "4 Sep 2027"), row click refuses to play
```

Card: `PreReleaseCountdown.cs:65-90`; card padding **12 all round**, `Gap = 12` between ring and column, inner column
`Gap = 4`, tile row `Gap = 4`, `Wrap = true`, `MinWidth 0`, `Radii.Card` 8, `FillCardDefault` + 1-px `StrokeCardDefault`.
Ring = stock indeterminate `ProgressRing` at **34**, `foreground: accent()`, `isActive: !released` (`:41, 75`) — the
control's own Inactive state fades it out once the wait is over, it is never removed. The eyebrow is
`WaveeType.Eyebrow(detail.preReleaseEyebrow)` **in the page accent, `MaxLines 1`** (`:82-85`).

**Three states of the card, not two:**

| State | What renders | Source |
|---|---|---|
| Counting | eyebrow + four `StatTile`s: **days at natural width (un-padded — a wait can be 100+ days)**, hrs/min/sec zero-padded `D2`; tiles keyed by UNIT so the value cross-fades in place | `:101-114, 126` |
| Expired (`remaining <= 0`) | the tiles are replaced by a single `"Out now"` run — **15 px / 700 / `Tok.TextPrimary`, 1 line, NoWrap, CharacterEllipsis** — the ring goes Inactive and `UseInterval` is disabled; **no refetch** | `:57, 59, 86, 93-97` |
| **`Bare = true`** | returns ONLY the tiles (or "Out now"): **no plate, no Fill/Border/Padding, no ProgressRing, no eyebrow**. The tone panels mount it this way because their own plate + eyebrow already say what it is; a card ring inside a card is double chrome | `:36-38, 63` |

The seconds tile changes every tick, so there is no slower rate at which a wake redraws an unchanged card — hence one
1 Hz `UseInterval`, seeded on **first render** rather than at construction (a component built during a parked-page
rebuild would otherwise carry a stale `now` until its first tick, `:52-53`). `Breakdown` clamps every unit at 0 so a
tick landing a hair past the instant can never paint a negative tile (`:120-121`).

Row treatment: `TrackRow.cs:226, 233-234, 267, 296-313`; the play gate is `DetailTracks.cs:3103`.
Precise pending-row facts: the whole **title column** (title + subline) takes `Opacity = 0.45` — the `#`, heart, Plays
and duration cells keep full opacity (`:267`); the `#` cell is handed `onPlay: null`, which removes the hover transport
AND its hand cursor (`:232-234`); Plays reads the em dash (`:296-298`); the **duration lane reads `ShortDate(AvailableAt)`
only when that instant exists and is still future — otherwise it falls back to the em dash** (`:305-307`), and its ink
drops from `TextSecondary` to **`Tok.TextTertiary`** (`:313`). `ShortDate` converts to **local time first**
(`when.ToLocalTime()`) and compares that against `DateTimeOffset.Now.Year`: `"4 Sep"` inside the reader's current year,
`"4 Sep 2027"` across a year boundary, **CurrentCulture** (`DetailConfig.cs:239-245`). The pending predicate itself
(`Track.IsNotYetOut()`, Wavee.Core) is the ONE source the grey treatment, the play gate (`DetailTracks.cs:3103`) and the
"N of M songs" tile all read, so they can never disagree about which row is pending.

### W14 — Expanded track drawer (versions and formats)

```
│  3  ♡  Get Lucky                             1.85B      6:09   ⌄ │  ← the chevron lane (Expand, 26)
├──────────────────────────────────────────────────────────────────┤
│        ┌ drawer: zebra parity continues, bottom corners 6 ──────┐│
│ indent │  Plays        BPM        Key        Duration           ││  facts strip (hero facts 18/800)
│  = art │  1,850,000,000  116       8B · C major   6:09          ││  then prose facts (Album, ISRC…)
│  centre│  Versions and formats     eyebrow, TextTertiary        ││
│        │  │                                                     ││  connector rail 1px StrokeDividerDefault
│        │  ├─[43]  This track · 6:09          ▂▃▅▇▅▃  [FLAC ▾]   ││  RowH = 43 + 2×4 = 51
│        │  └─[76×43 ▶] Music video · 4:38             [Auto ▾]   ││  16:9 thumb + play badge
│        └──────────────────────────────────────────────────────┘ │
```

`TrackVersionsPanel.cs:49-59, 130-152, 165-215, 261-326`; the host slot, its clip and its two motion specs are
`DetailTracks.cs:3245-3261, 3315-3352` (spec'd in **04-detail-track-table.md**).

**Geometry and ink (`TrackVersionsPanel.cs:49-59, 144-152, 261-326`):** drawer padding `(Indent, 0, TrackRow.PadX, 8)` —
top padding is **0** on purpose, the row's own bottom edge is the separation. Connector gutter `GutterW 20`, rail at
`RailX 7` (1 px wide, `Tok.StrokeDividerDefault` — **not** `StrokeCardDefault`, which is a black alpha in both themes
and vanished on dark), stub `StubW 9` × 1 px at `RowH/2`; the LAST entry's rail stops at `RowH/2` so the line terminates
on content (an elbow). `RailOffset = RailX` is what the caller subtracts from the artwork centre, so the **rail** lands
on the art rather than the gutter's left edge. `RowH = 43 + 2×4 = 51`; thumbs `43×43` (audio) / `76×43` (video); each row
is `Radii.Control` with `Interaction.Subtle`, `Gap 8`, padX 4, and **the self row alone carries a
`Tok.FillSubtleSecondary` plate** — the others are transparent at rest (rows, not cards).
Title 13.5 / **540** weight, `AccentTextPrimary` when that version is now-playing else `TextPrimary`; the meta line is a
gap-8 run of 12-px tokens: kind label (`TextTertiary`) → optional **6×6 r1.5 α0.85 Camelot swatch** → BPM
(`TextSecondary`) → `·` → key (`TextTertiary`) → duration (`TextTertiary`). The `Versions and formats` eyebrow carries
`Margin (0,12,0,2)`. Every row ends in a `FormatSplitButton` keyed `"fmt:" + uri`.

**States the sketch omits:**

- **Row order is authored, not sorted:** the self row FIRST (always — it is what gives the track's own format a home),
  then every kind-99 video, then every kind-98 alternate audio (`:114-119`). The self row is also the only one that can
  carry a waveform and the only one with the `FillSubtleSecondary` plate.
- **A resting alternate-audio row has NO play affordance.** `Thumb(...)` returns the bare artwork unless the version is
  now-playing (the overlay) or a video (the badge) (`:350-368`), and the row body itself has **no `OnClick`** — it is
  `.Interactive(Interaction.Subtle)` for the hover wash only. Play on an alternate audio version comes from its
  `FormatSplitButton` alone. Do not "fix" this into a row-wide click in 0.3 without deciding it deliberately.
- **The drawer fetches on MOUNT, per track uri** — `UseEffect(..., DepKey.From(track.Uri.GetHashCode()))` at `:91-97`,
  cancelled on unmount. The drawer only mounts when the chevron is clicked, so this *is* the on-expand fetch; there is
  no prefetch and no cache above it inside this panel.
- **The music-video row is RESERVED before the fetch answers.** When `Facts.HasVideo` is true the panel mounts
  `PendingVersionRow` under the SAME key (`"v:video"`, the kind is a complete identity because kind 99 yields at most
  one counterpart), at the same `RowH` and the same 76×43 slot, with a `FillSubtleSecondary` block for the thumb and
  two `FillSubtleSecondary` pill bars (**132×11** and **84×9**, gap 6). It is deliberately inert: no `Interactive`
  recipe, no play affordance, no format button. Data landing **patches** that node rather than swapping it, so the
  drawer's animating height never chases a moving target (`:63-75, 104-120, 217-254`).
- **Now playing on a version**: the thumb becomes a ZStack of the art under `NowPlayingOverlay.Create(...)` at a FAB of
  `clamp(min(w,h) × 0.62, 22, 28)`, replacing the static video badge (`:266, 350-367`).
- **Video badge at rest**: a full-slot `#000 α 0.28` scrim under a **22×22 `#FFF` α 0.92** circle holding
  `Icons.Play` at 11 in `#111` (`:377-392`).
- **Waveform**: only on the drawer's OWN track, and only once kind 237 lands — 64 mirrored 2-px bars in `TextTertiary`
  (`FluentGpu.Controls.Waveform`, `:328-344`). Absent → the row simply closes the gap; no reserved empty rail.
- **Expansion failure** is swallowed into `TrackExpansion.Empty` (`:158-160`), so a failed fetch collapses to the self
  row alone — never an error line inside the drawer.
- **Facts strip** leads with the four hero facts in `TrackFactKind` declaration order — **Plays · BPM · Key · Duration**
  (`TrackExpandedFacts.cs:148-151`) — then the prose facts in that same declaration order: Added, **Album** (a link),
  **Released** (a track's own live date — this is where a pending album row states its date in prose), Added by, ISRC,
  Descriptors (chips), then the flags Explicit, Video, Local file, **Unavailable** (`:62-127`).
  **The last two flags are TWO facts about two different things and must read that way.** `Video` says a music video
  exists for this song; `Unavailable` is the AUDIO's verdict (`Unplayable` — region-locked or withdrawn) and says
  nothing whatsoever about the video. The drawer middot-joins the whole flag run onto ONE line, so bare labels printed
  "Music video · Unavailable", which readers took to mean the VIDEO was unavailable. There is no fourth fact line to
  move it to (the strip is exactly three: heroes, prose, marks) and the prose line would sit it beside the Released
  date, which is the contradiction `Facts.For`'s `!notYetOut` guard exists to prevent — so the fix is the LABEL:
  **`detail.trackFacts.unavailable` reads "Audio unavailable"**, and every label in that run must likewise name its
  own subject. Pinned by `Wavee.Tests/AlbumVideoSectionTests.cs` (`AlbumVideoFactSeparationTests`).

### W15 — "Other versions" flyout open

```
[ ♪ Other versions ▾ ]
└─ MenuFlyout (BottomEdgeAligned, light dismiss)
   ♪  Random Access Memories · 2013 · ALBUM
   ♪  Random Access Memories (10th Anniversary) · 2023 · ALBUM
   ♪  Random Access Memories (Drumless Edition) · 2023 · ALBUM
```

Label = `Name · Year · KIND`, joined with " · ", kind localized (`DetailTrailing.cs:304-316`). Invoking calls
`h.OpenAlbum(v)` → stashes a nav preview then routes (`DetailShell.cs:415`, `NavPreview.cs:90`).

### W16 — Face pile flyout open

```
((●))((●))((●))+7 ⌄  Daft Punk, Pharrell Williams
└─ flyout: Width 280, MaxHeight 360, Padding 8 (inner list 264 / 344, AutoEdgeFade)
   ( 32 )  Daft Punk                 row h 44, gap 12, radius 6, Interaction.Subtle
   ( 32 )  Pharrell Williams
   ( 32 )  Nile Rodgers              … every DISTINCT artist across the album's tracks
```

`ArtistFacePile.cs:161-189`. Tooltip on the pile button: literal `"View all artists"` (`:103`) — see §6 for the loc
drift.

### W17 — Selection (album pages are `ItemsSelectionMode.Extended`; singles are `None`)

```
│ ▌ 1  ♡  Give Life Back to Music …            │  ← 3×16 accent pill, bound opacity
│ ▌ 2  ♡  The Game of Love …                   │
┌ command bar swaps to the SELECTION bar ──────┐
│ 2 selected   [▶ Play] [＋ Add to] [♡] [⋯] [✕]│
└──────────────────────────────────────────────┘
```

`DetailConfig.cs:206, 210` (Single drops multi-select), `DetailTracks.cs:1936-1938`. Details in **01/04**.

### W18 — Empty / error / offline

```
unresolvable prerelease route        thin ("minified") album            trailing failed / nothing
┌─────────────────────────┐          ┌──────────────────────────┐       ┌────────────────────────┐
│ (rail with empty title) │          │ full page: NO notice bar │       │ region eases to height │
│                         │          │ (self-heals: the trailing│       │ 0 — the page simply    │
│   Nothing here yet      │          │ band asks for Full)      │       │ ends after the rows    │
│   14 px TextTertiary    │          │ embedded library pane:   │       └────────────────────────┘
└─────────────────────────┘          │ InfoBar Informational    │
                                     │ "Minified album view — … │
```

`DetailPage.cs:412` (empty model), `DetailTracks.cs:409-415` (`Nothing here yet` / `No songs match your filter` —
14 px `Tok.TextTertiary`, centred, padding 16/24/16/24), `DetailNoticeBar.cs:43-65` + `DetailShell.cs:598-600, 667-669`,
`DetailTrailing.cs:88-95` (`OnFailed → new BoxEl()`).

Precise arms:

- **An EMPTY tracklist is `DetailNotice.None`, never MinifiedAlbum** (`PlaylistPageNoticeRules.cs:86-93`): no rows is
  "still loading" (the list renders its own shimmer), not "minified" — the notice is about rows the reader can SEE being
  blank. The verdict is re-read off **every** projection and is stateless, so it clears in place the frame the TrackV4
  repair fills the names (`DetailPage.cs:297`).
- The InfoBar arm (embedded library pane only) is `InfoBarSeverity.Informational`, `isClosable: false`, in a box padded
  **16/8/16/4**; the suppressed arm is a `Height = 0, HitTestVisible = false` box, not a missing node
  (`DetailNoticeBar.cs:48, 57-65`).
- **Unresolvable `prerelease:` route** → `LoadAlbumDetailAsync` returns `DetailModel.Empty` outright when
  `svc.PreRelease.ResolveAsync` yields null (offline / 404 / dead entity) — the shell paints with an empty title and the
  list's own "Nothing here yet", never an error page (`DetailPage.cs:405-411`).
- **A failed trailing enrichment** eases the region to height 0 (`OnFailed → new BoxEl()`), and the region is
  additionally held in its shimmer branch by `reserving = !ready && !albumSettled`, whose terminator is the album
  loadable's own non-Pending state — so an album that resolves with **zero tracks** settles instead of shimmering
  forever (`DetailTrailing.cs:54-60, 87-88`).

### W19 — The album drawer on the artist page (this surface's album preview)

```
one column (grid < 5 cols)                       two columns (grid ≥ 5 cols)
        ▲ caret 16×8 at the clicked card's centre, TopGap 8
┌ panel: pad 12/6/12/6, Radii.Card, FillCardSecondary, 1px stroke ───────────────────────────┐
│ (26 ▶)[28 art] Discovery · 2001 · 14 songs                                       [ ↗ 28 ]  │ head h 28
│  1 ♡ One More Time                3:59  … │  8 ♡ Face to Face             4:00  …          │ row pitch 32
│  2 ♡ Aerodynamic                  3:27  … │  9 ♡ Too Long                10:00  …          │ (content 28)
│  …                                        │  …                                             │
│ Show all 14 tracks   (past 12 / 24)       │                                                │ accent 12/600
└────────────────────────────────────────────────────────────────────────────────────────────┘
        ▼ BottomGap 8 → the next card row
```

`AlbumDrawerVerdict.cs:13-22` (HeaderH 40 = 6+28+6, RowPitch 32, TopGap/BottomGap 8+8, cap 12/column, two columns at
≥5 grid columns, 3 fallback shimmer rows), `ArtistPage.AlbumExpand.cs:120-124, 145-205, 205-282, 370-397, 425-470,
640-690`.

**Corrections and detail the sketch flattens:**

- **"Show all N tracks" NAVIGATES to the album page — it does not lengthen the drawer** (`_go("album:" + uri)`,
  `ArtistPage.AlbumExpand.cs:271-282`). It is a `RowPitch`-tall row, padX 8, hand cursor, `AutomationRole.Button`,
  `detail.discography.showAllTracks` at **12/600 `AccentTextPrimary`**, and it is **counted as a cell** in
  `AlbumDrawerVerdict.Rows` so the reserved slot never has to guess.
- **Lane set** (`:121-123`): `# 26 · ♥ TrackRow.HeartCol · Title★ · time 44 · trailing "…" 32` — no thumb, no Plays,
  no Album, no Date, no Video. Row content height is **28** inside the 32 slot.
- **Two columns split column-major**: the first `⌈cells/2⌉` cells go LEFT (numbered tracks read down, then across), and
  the columns are separated by **`Spacing.XL` = 20** (`:378-399`). The "Show all" cell is simply the last cell in that
  sequence, so it lands wherever the split puts it.
- **Header** (`:149-199`): 28 tall, gap 12 — a **26×26 circle filled with `_accent()`** holding `Icons.Play` at 11 in
  `ColorContrast.PickContrast(accent)` (never `TextOnAccentPrimary`: a lifted cover accent is often pale) → a 28×28
  `Radii.Control` cover → a single `SpanTextEl` paragraph `Name` **13/600 `TextPrimary`** + `" · " + AlbumMeta` at
  **12/400 `TextSecondary`**, NoWrap + ellipsis → `AlbumNavAction` at 28.
- **The caret is two nodes, not one** (`:645-668`): a `PathEl` wedge filled `Tok.FillCardSecondary` **plus** a
  `PolylineStrokeEl` outline in `Tok.StrokeCardDefault` at 1 px. Its apex is clamped to
  `[Radii.Card, panelWidth − Radii.Card]` so it can never overhang the panel's rounded corners, and it is painted LAST
  in the ZStack with a **1-DIP overlap** (`caretY = TopGap − CaretH + 1`) so its fill hides the panel's top border
  stroke across the wedge's base.
- **Drawer rows are fully interactive** (`:214-292`) — this is not a read-only preview: `Radii.Control`,
  `WaveeColors.RowHover` / `RowPressed`, `AutomationRole.Button`, hand cursor; **single click selects** (Ctrl toggles,
  Shift extends from the anchor), **double click plays**; a selection-aware drag source (`DrawerPayload` — the whole
  selection when the gesture starts on a selected row, else that one track); `WithContextMenu` → the shared
  `TrackContextMenu`; `RowSwipe` with ToggleLike + AddToQueue. A **3×16 r1.5 accent selection pill** with a bound
  opacity is always mounted at margin-left 2, and a `SelectionCommandBar` sits in the panel's own ZStack with
  `bottomPadding: 8` (`:170-176, 255-268`).
- **Ready-empty is not a bare line — it carries a RETRY.** `rows = 2` by construction (`AlbumDrawerVerdict.cs:44`)
  reserves two row pitches, and what fills them is `EmptyNote(retry)` (`ArtistPage.AlbumExpand.cs:343-353`): a
  `Direction = 0`, gap-12 row padded `(8,12,8,12)` holding `detail.empty.noTracks` at **13 px `Tok.TextTertiary`**,
  one line, ellipsized, beside a stock **`Button.Standard(common.retry, …)`**. It is the only error affordance anywhere
  on this surface — everything else fails soft and disappears — and the `Action retry` in `Album.UI.DrawerPanel`'s props
  (§1.2) exists for it. Do not port the props row without porting the button.
- **Loading** shows `min(max(thinTrackCount, 3), cap)` shimmer cells through the SAME `BuildColumns` splitter as the
  real rows, so the two can never disagree about column height (`:357, 378-399`). A shimmer cell is `RowPitch` tall,
  padX 8, gap 12, holding a **16×11 r4** block (the number) and a **grow bar 11 tall capped at `MaxWidth` 240, r4** (the
  title) — both `Tok.FillSubtleSecondary`, keyed `"shimmer:"+i` (`:359-367`). No heart, no time, no "…" placeholder.
- **The open scrolls the card into view by a fixed peek**, not by the drawer's full height: `DiscoGrid`'s
  `expandedRevealPeek` defaults to `HeaderH + 2 × RowPitch` = **104** with an `expandedTopInset` of **28**
  (`ArtistPage.AlbumExpand.cs:686-690`) — so opening a drawer near the fold reveals the header plus two rows, never
  scrolls a full 424-DIP panel under the reader.

### W20 — Row states on an album page

```
rest        │  4  ♡  Within                             3,210,987   3:48        │  # = Caption tertiary
hover       │ ▶   ♡  Within                             3,210,987   3:48   ⌄    │  number cross-fades to ▶
now playing │ ≋   ♡  Within                             3,210,987   3:48        │  equalizer in accent, title accent
top track   │ ★   ♡  Get Lucky                              1.85B   6:09        │  FavoriteStarFill 11, accent
buffering   │ ◌   ♡  Within                             3,210,987   3:48        │  ProgressRing 16
pending     │  9  ♡  Unreleased                                 —      4 Sep    │  column Opacity 0.45, no ▶ on hover
```

`TrackRow.cs:1067-1106`; the star is album-only because it needs `ShowPlays` + a non-null top-track id
(`DetailTracks.cs:2751-2753`).

---

## 3. Tokens

| Element | Size | Padding / gap | Radius | Type style | Colour / brush | Material / elevation | Source |
|---|---|---|---|---|---|---|---|
| Rail column | `railW` (280 default; 224/188 by mode; 180–480 grip) | pad 16/24/8/24, gap **14** | — | — | `Tok.FillLayerDefault` | own `ScrollView`, `ClipToBounds` | `DetailRail.cs:20-21, 262-298`; `DetailRailPolicy.cs:23, 27` |
| Rail cover | `max(80, railW − 16 − 8)` (256 @280) | — | `Radii.Card` 8 | — | art, `saturation 1.18`, `decodePx 256` (`HeroCoverDecodePx`) | `Elevation.Card` (dark blur 8/y2/#00000033; light blur 4/y2/#0000001A) | `DetailRail.cs:26, 96, 145-162`; `Elevation.cs:18-21` |
| Eyebrow "ALBUM · 2019" | width = cover, 1 line | — | — | `Ui.Caption` 12/16 **600** + `CharSpacing 30` | `Tok.TextTertiary` | — | `WaveeType.cs:38, 54`; `DetailRail.cs:590-593` |
| Rail hero title | width = cover, ≤3 lines | — | — | `WaveeType.DetailHero` = `Ui.Title` 28/36/600, display face, `CharSpacing −20`; **40/52 when window H ≥ 900**, `MinSize 18` | `Tok.TextPrimary` | — | `WaveeType.cs:134-138`; `DetailShell.cs:636-637`; `DetailRail.cs:183-189` |
| Face pile avatar / frame / overlap | 28 / 32 / −12, ≤4 + `+N` | button pad 6/4/6/4, gap 4; row gap 8 | frame 16 (circle), button 8 | `+N` = 10/700 | frame `Tok.FillSolidBase`, `+N` chip `Tok.FillCardDefault` on `TextSecondary`; hover `FillCardDefault`, press `FillSubtleTertiary` | — | `ArtistFacePile.cs:29, 83-140`; `FacePiles.cs:24-34` |
| Billed-artist names | grow, 1 line | — | — | 14 / **700** | `Tok.AccentTextPrimary` | — | `ArtistFacePile.cs:157` |
| Play CTA | MinHeight **36** | padding 18/6/18/7; cluster gap 12 | `Radii.Full` | Button label 14 **Bold** | fill = cover `ChromeAccent`; ink = `PickContrast(fill)`; hover fill α .90 / press α .80; **pressed LABEL dims to α 0x80 (dark ink) / 0xB3 (light ink), keyed off the ink's luminance, not the theme**; border `AccentControlElevationBorder` at rest+hover, transparent when pressed/disabled; disabled legs stay on `Tok.AccentDisabled`/`TextOnAccentDisabled` | stock Button internals, 83 ms brush ramp, `BackgroundSizing.OuterBorderEdge`, focus margin −3 | `WaveeCta.cs:65, 74-107, 220-242` |
| Heart (`SaveButton`) | 40 × 40 | — | 20 (circle) | glyph 16 | saved → `HeartFill` in the accent thunk (→ `WaveeAccentCtx` → `Tok.AccentTextPrimary`); unsaved → `Heart` in `Tok.TextSecondary` | `Interaction.Subtle`, **`ScaleEmphatic` hover 1.07 / press 0.92** | `SaveButton.cs:33-48`; `WaveeMotion.cs:48` |
| Share FAB (`PlaylistShareButton`) | 40 × 40 | — | 20 (circle) | glyph 16 | `Icons.Share` in `TextSecondary`; copied → `Icons.Accept` in **`Tok.AccentTextPrimary`** | `Interaction.Subtle`, **`ScaleSubtle` hover 1.02 / press 0.98** — *not* the heart's rung | `PlaylistInlineEdit.cs:777-797`; `WaveeMotion.cs:39` |
| Inert heart fallback | 40 × 40 | — | 20 | glyph 16 | `Tok.TextSecondary`, no-op click | `ScaleEmphatic`, `Interaction.Subtle` | `DetailRail.cs:234, 599-605` (only when both `PreReleaseUri` and `ContextUri` are empty) |
| Countdown card | ring 34 | pad 12, gap 12, tiles gap 4 | `Radii.Card` 8 | eyebrow (accent), tiles 18/800 + 11 | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | — | `PreReleaseCountdown.cs:41, 65-115` |
| Stat tile | grow/basis 0, `MinWidth 0` | pad 12/8/12/8, column gap 1 | `Radii.Control` 4 | value 18/**800** `TextPrimary`; caption 11 `TextSecondary` | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` | `Enter = FadeUp`, `Layout = Shove` | `StatTile.cs:21-56` |
| Release panel | — | gap 12; padding 16/20/16/16 (outer) or 0 (rail) | — | eyebrow `TextTertiary` | — | `Enter = FadeUp`, `Layout = Shove` | `DetailTrailing.cs:259-273` |
| Release note line | — | column gap 3 | — | 11 px, wrap, ≤4 lines | `Tok.TextTertiary` | keyed, `FadeUp` + `Shove` | `DetailTrailing.cs:255-284` |
| Other-versions button | — | row padding 0/2/0/2 | stock DropDownButton | stock | — | keyed `release-versions`, `FadeUp` | `DetailTrailing.cs:286-302` |
| Trailing section wrapper | grow, stretch | padding 16/20/16/16, gap 12 | — | header `WaveeType.RailHeader` = `Ui.Subtitle` 20/28/600 | — | — | `DetailTrailing.cs:482-489, 540-547` |
| Trailing media row | h 64, thumb 48 | padX 8, gap 12, **text stack gap 2** (`Spacing.XXS`) | row 8, thumb 4 | title 14/20/600 (`WaveeType.TrackTitle` = `Ui.BodyStrong`); subtitle rich 12 `TextSecondary` | `FillCardSecondary` → hover `FillCardDefault`, press `FillSubtleTertiary`, border 1 `StrokeCardDefault` | hover play FAB 30, row is the `Button` | `MediaCard.cs:965-1063` |
| Artist chip | h 48, avatar 32 | pad 8/0/16/0, gap 8; row gap 12 | 24 / avatar 16 | name 14/600 | `FillCardSecondary` + 1px stroke; hover `FillCardDefault`, press `FillSubtleTertiary` | — | `DetailTrailing.cs:417-434` |
| About-artist card | avatar 84 | pad 16/12/16/12, gap 16, text gap 4 | 8 / avatar 42 | eyebrow; name 20/700; bio 13 ≤2 lines | `FillCardSecondary` + 1px stroke, hover `FillCardDefault` | — | `DetailTrailing.cs:560-602` |
| Music-video HERO card (exactly one video) | thumb 200×116 (the video's OWN still), FAB 44 | section pad 16/20/16/**0**; card pad 12/12/16/12, gap 16 | card 8, thumb 4, FAB 22 | eyebrow `TextTertiary`; title `RailHeader` 1 line = the VIDEO's title; meta 12 `TextSecondary` 1 line = `detail.versions.musicVideo` · the VIDEO's duration | `FillCardSecondary` + 1px `StrokeCardDefault`, hover `FillCardDefault` (no press fill, no scale), `ClipToBounds`; FAB `Tok.AccentDefault` + `TextOnAccentPrimary`, glyph 16; `Focusable` + focus margin `Design.FocusInsetBordered` | — | `Album.Page.cs` `VideoHero` / `VideoThumb` (was `DetailTrailing.cs:355-408`) |
| Music-video SHELF (two or more) | auto-fit card **200…280** (`PagedShelf` `minCardW`/`maxCardW`, never wrapped); thumb = `inner × 9/16` where `inner = max(96, cardW − 16)`; FAB `clamp(min(w,h) × 0.38, 28, 44)` = 44 at every reachable width | section pad 16/20/16/**0**; `headerGap` 12; card gap 12 (`Spacing.M`); card pad 8/8/8/12, gap 8 | card 8, thumb 4, FAB = half | header `RailHeader` (`artist.musicVideos`) + count 13/600 `TextTertiary`, then the control's own spacer → pips → chevrons; per cell `TrackTitle` 1 line + `TrackMeta` duration 1 line (omitted when 0) | as the hero card | edge fade `Design.Size.FadeShelf` (24) via `AutoEdgeFadeBand`; `ShelfPager.Chevrons \| Pips`; `ShelfSnap.Page`; `measured: true` (no height estimate → no reflow as stills decode); `maxItems` `VideoCap` | `Album.Page.cs` `VideoShelf` / `VideoShelfCard` / `VideoShelfHeader`; `FluentGpu.Controls/PagedShelf.cs` |
| Compact rail strip (collapsed) | strip 96, cover `max(48, stripW−16)` = 80, chevron box 28 | pad 8/16/8/16, gap 8 | cover `Radii.Card` 8, chevron `Radii.Control` 4 | title 12/600 ≤2 lines `WrapWholeWords` | `Tok.FillLayerDefault`, `ClipToBounds`; chevron hover `FillSubtleSecondary`, glyph `Icons.ChevronRight` 14 `TextSecondary` | cover `Elevation.Card` | `DetailRail.cs:304-354` |
| Drawer empty note (artist page) | — | pad 8/12/8/12, gap 12 | — | 13 px, 1 line, ellipsized | `Tok.TextTertiary` + a stock `Button.Standard` | — | `ArtistPage.AlbumExpand.cs:343-353` |
| Drawer shimmer cell (artist page) | h 32 (RowPitch); blocks 16×11 and grow×11 (`MaxWidth` 240) | padX 8, gap 12 | 4 | — | `Tok.FillSubtleSecondary` | — | `ArtistPage.AlbumExpand.cs:359-367` |
| Merch row | h 64, thumb 48 | padX 8, gap 12 | 8 / thumb 4 | name 14/20/600; price 12/16/600 | price `Tok.AccentTextPrimary` | hover/press scale `ScaleSubtle` only when a shop url exists | `DetailTrailing.cs:505-538` |
| Album track row (album profile) | h 48 (density 0/2/3 → 40/56/64) | padX 16 (tier ≤3), gap 12 (tier ≤4) | row 4 / inset 8 | title 14/20/600; every fact `Caption` 12/16 | zebra `WaveeColors.RowZebra` on odd rows | — | `TrackRow.cs:102-130`, `DetailTrackTableRules.cs:45-47` |
| Album lane set | `# 28 · ♥ 28 · Title★1 · Plays 52 · Duration 52 · (Video 28 \| Actions 40) · Expand 26` | gaps 12 | — | — | — | — | `DetailTracks.cs:554-582`; `TrackLane.cs`=`DetailTrackTableRules.cs:227-279` |
| Context band (narrow) | h 56 + hairline 1 | padX = `TrackRow.PadXFor(tier)`, cluster gap | — | title 14/20/600; byline `Caption` | **no fill**; hairline `Tok.StrokeDividerDefault` | content clipped at 93, 24-DIP feather | `ContextBand.cs:98-153`; `ContextBandLayout.cs`; `DetailVerticalLayout.cs:590` |
| Album drawer (artist page) | header 28 in 40, rows 32 (content 28), caret 16×8 (+1 overlap) | pad 12/6/12/6, head gap 12, two-column gap **20** | `Radii.Card` 8; rows `Radii.Control`; pill r1.5 | header title 13/600 + meta 12/400 secondary; show-all 12/600 accent | panel `FillCardSecondary` + 1px `StrokeCardDefault`; rows transparent → `WaveeColors.RowHover` / `RowPressed`; caret fill `FillCardSecondary` + 1px `StrokeCardDefault` outline | — | `AlbumDrawerVerdict.cs:13-22`; `ArtistPage.AlbumExpand.cs:149-199, 214-282, 400-410, 645-690` |
| Album drawer lanes | `# 26 · ♥ TrackRow.HeartCol · Title★ · time 44 · "…" 32` | — | — | — | no thumb / Plays / Album / Date / Video lane | — | `ArtistPage.AlbumExpand.cs:120-124` |
| Version drawer connector | gutter 20, rail x 7 (1 px), stub 9 × 1 px at `RowH/2`; last row's rail = `RowH/2` | row gap 8, padX 4 | `Radii.Control` | version title 13.5/**540**; meta tokens 12 | rail + stub `Tok.StrokeDividerDefault` (theme-flipping — *not* `StrokeCardDefault`); self row `FillSubtleSecondary`, others transparent | `Interaction.Subtle` | `TrackVersionsPanel.cs:51-59, 186-208, 292-325` |
| Page ground | full page | — | `WaveeShell.ContentPaneCorners` | — | `WaveePalette.PageTone` @ **α 0.20 dark / 0.30 light** | over `FileArea` over live Mica | `CoverPaletteLeaves.cs:102-139`; `WaveePalette.cs:188-239` |

**The album's full config literal** (`DetailConfig.cs:203-207`) — every knob, including the four the wireframes never
show: `TwoColumn: true · RailWidth: WaveeSize.RailAlbum (280) · Badges: TypeYear · ShowArtThumb: false ·
ShowAlbumColumn: false · Columns: AlbumColumns (dead, §9) · CapTitle: false · Selection: Extended · HasTrailing: true ·
Heart: Save · ShowPlays: true · ShowVersions: true · RailScope: Album · RailResizable: true`, and by omission
**`ShowTempo: false`** (no BPM·Key lane on an album — the running order is the point and Plays owns that width) and
**`PlaysColumnOptIn: false`** (the album's Plays lane is *not* the user-toggleable one; the setting that hides Plays on
playlist/Liked does nothing here, and that same knob is what keeps the top-track star and `SeedTrack` album-only,
`DetailConfig.cs:163, 176-179`). `Single = Album with { Selection = None }`; `Compilation = Album with
{ ShowTrackArtist = true }`; EP takes the plain `Album` literal (`DetailPage.cs:330-341`).

**The rail width can be shared with other pages.** With Settings' "Keep left-rail same size" on,
`DetailRailPolicy.ScopeFor(RailScope.Album, uniform: true)` resolves to `RailScope.Uniform`, so the album's persisted
width + collapsed pair becomes the one every detail surface reads and writes (`DetailRailPolicy.cs:50`). The default
width per scope is 280 for Album/Show and 240 for Playlist/Liked (`:29-34`). Owned by **03-detail-frame.md**; named here
because the album is the surface whose 280 people notice moving.

---

## 4. Colour & material

| Input | Function (file:line) | Applied to | Transition |
|---|---|---|---|
| Cover url → `CoverColorPlane.Scheme` (5 graded roles) | `Surfaces.SchemeFor` / `ChromeSchemeFor` (`DetailShell.cs:277-304`) | the page's `accent` | resolved per render; a late grading repaints the leaves, and accent-filled controls refresh on the next natural re-render |
| scheme → **chrome accent** | `WaveePalette.ChromeAccent` = `Vivid(Lift(Accent(s)))`, falling back to `Tok.AccentDefault` when HSV S ≤ 0.08 (`WaveePalette.cs:119-131`) | Play pill fill, filled heart, countdown eyebrow + ring, accent rule, selection pill, links | Button's 83 ms brush ramp |
| nav-preview payload ARGB (`DetailModel.Accent`) | `WaveePalette.ChromeFromPayload` (`DetailShell.cs:308-310`; seeded at click time by `NavPreview.cs:62-77`) | the same accent, before grading lands | none — it *is* the first value |
| scheme → **page tone** | `WaveePalette.PageTone`: hue only; L forced 0.15 dark / 0.94 light; S capped 0.30 / 0.16; below S 0.12 → neutral #151515 / #F5F5F5 (`WaveePalette.cs:188-239`) | `CoverPaletteLeaves.PageTonePlane` behind the whole page | bound brush, `BrushTransitionMs = 250` (`CoverPaletteLeaves.cs:113-141`) |
| same url | `CoverPaletteLeaves.ShellTint(..., apply: cfg.TwoColumn)` (`DetailShell.cs:321-323`) | the shell material (toolbar/sidebar band) — a **hand-over**, never a clear | claimed on publish; held across a page swap until the next page claims it |
| accent → readable ink on a card | `WaveePalette.TextInk(seed)` (contrast 4.5 against the flattened card) | countdown/flip digits, any accent-as-ink | — |
| album art | `Surfaces.Artwork(..., saturation: 1.18f)` (`DetailRail.cs:160, 321`; `DetailVerticalHero.cs:129`) | rail cover, compact strip cover, vertical hero cover | fixed value on **all three album arms**. (`DetailRail.BuildHeader`'s 140-DIP cover at saturation 1.0, `:422`, is the **Show** page's narrow header — not reachable from an album; see §1.1.) |
| theme | every token above is theme-derived; the clamped tone is what makes the ordinary `Tok` ink correct in both themes | — | no on-media ink ladder exists on this page (`DetailVerticalHero.cs:37-41`) |
| "Colour washes" setting off | `WaveeSettings.ColorWashesEnabled` (`DetailShell.cs:213`) | tone plane paints nothing; shell tint suppressed | instant |

The rail's own recede is **not** art-derived: it is `Tok.FillLayerDefault` (`DetailRail.cs:290-293`), matching
`LibraryPage.NavPanel`. The sticky band paints **no** material at all (`ContextBand.cs:71-96`) — its 56+36+1 DIP are an
unpainted omission over the tone plane.

---

## 5. Motion

| Trigger | Target | Property | From → to | Duration | Easing | Delay / stagger | Reduced motion | Source |
|---|---|---|---|---|---|---|---|---|
| Route enter | whole page | Position + Opacity | Dx ±8, α 0→1 | 250 ms | `SmoothOut` (0.22,1,0.36,1) | — | engine `KeepFade`: fade only | `MotionRecipes.cs:193-203` (owned by ch. 18) |
| Full model lands | rail eyebrow / face pile / countdown / description | Opacity | 0 → 1 (`FadeUp`, folded into `Shove`) | `Expressive.Fast` 250 ms | `SmoothOut` | — | fade kept, travel snapped | `DetailRail.cs:52-71` |
| A late sibling inserts | every keyed rail row | Position (FLIP) | old origin → new | 250 ms | `SmoothOut` | — | snaps | `DetailRail.cs:56-57` |
| Release facts / notes mount | `release-facts`, `release-notes` columns | child entrance offset | — | — | — | `Stagger = 45 ms` (`MastheadStaggerMs`), **0 under reduced motion (a VALUE, not a branch)** | 0 stagger | `DetailTrailing.cs:221, 232, 257` |
| A tile's value refines | `StatTile` value box | **channel = Opacity only**; Dy ±4 + blur 2 ride the enter/exit offsets | Dy ±4, α 0→1, blur 2 | 150 ms | `EaseInOut` | — | engine policy | `MotionRecipes.cs:229-233` via `StatTile.cs:40` |
| A tile is inserted / shoved | the whole `StatTile` box | Position (FLIP) + `FadeUp` entrance | — | 250 ms | `SmoothOut` | — | fade kept, travel snapped | `StatTile.cs:49` — and note `Create` takes an **optional `layout` override** (`layout ?? DetailRail.Shove`), unused on this page but the seam a 0.3 caller needs |
| Trailing enrichment resolves | the trailing region | Size (`SmoothResize`) then content fade | shimmer height → real height (or → 0) | engine skeleton region | — | swaps **once** | fade only | `DetailTrailing.cs:86-104` |
| Album fetch completes | track rows | per-row cross-fade as the ramp passes them | placeholder → real | per-frame, 12 rows/frame | — | 12 rows per frame, cap 60 | ramp still runs (it is realization, not decoration) | `DetailRevealRamp.cs`; `DetailTracks.cs:777-791, 1137-1149` |
| Countdown tick | the four unit tiles | value text swap in place | old → new | 150 ms | `EaseInOut` | 1 s interval, `UseInterval` (auto-pauses when parked/minimized) | engine policy | `PreReleaseCountdown.cs:44, 59, 126` |
| Hover a row | `#` cell | Opacity cross (rest ↔ transport) | 0 ↔ 1 | engine hover ramp | — | — | — | `TrackRow.cs:1092-1105` |
| Press | Play pill / heart FAB / **share FAB** / merch row | Scale | 1 → **0.96** (pill, `ScaleStandard`) / **0.92** (heart, `ScaleEmphatic`) / **0.98** (share + merch, `ScaleSubtle`) | engine press ramp | — | — | collapses to 1.0 (`ScaleTier.Press`) | `WaveeMotion.cs:39, 48`; `WaveeCta.cs:69-72, 104-106`; `SaveButton.cs:43`; `PlaylistInlineEdit.cs:786`; `DetailTrailing.cs:516-517` |
| Hover | same four | Scale | 1 → **1.04 / 1.07 / 1.02 / 1.02** | — | — | — | collapses to 1.0 | same |
| Hover a merch row with NO shop url | merch row | — | **no scale at all** (`HoverIf/PressIf(false)`) | — | — | — | — | `DetailTrailing.cs:516-517` |
| Expand a trailing section | `TrailingStack` | — | none — "Show all N" is a plain `UseState(true)` re-render, no layout transition | — | — | — | — | `DetailTrailing.cs:685-697` |
| The `#` cell's transport press | 24×24 transport box | Scale | 1 → 0.92 (`ScaleEmphatic.Press`) | engine press ramp | — | — | collapses to 1.0 | `TrackRow.cs:1085-1092` |
| Expand a track row | drawer clip box | Size (`Reflow`, `Anchor: Leading`, `SuppressDescendantTransitions`) | 0 → measured | in 150 ms (`ControlFast` dynamics of `ControlNormal`: enter `ControlNormal`, exit `ControlFast`) | `FluentStandard` | — | engine `KeepFade` | `DetailTracks.cs:3245-3253` |
| …its content | drawer presence layer | Opacity + Position | Dy −8 → 0, α 0→1 (exit Dy −4) | enter `ControlNormal` / exit `ControlFast` | `FluentStandard` | — | fade only | `DetailTracks.cs:3255-3260` |
| Hero reflow (stacked ↔ row) | hero box | Position + Size (`Reveal`) | old → new axis | 280 ms | `SmoothOut` | — | snaps | `DetailVerticalHero.cs:51-54` |
| Hero artwork resize | artwork box | Bounds (`ScaleCorrect`) | old → new edge | 280 ms | `SmoothOut` | — | snaps | `DetailVerticalHero.cs:46-49` |
| Scroll (narrow arm) | expanded hero | TransY 0 → −`CollapseDistance`; Opacity 1 → 0 over the last 96 DIP | scroll-linked | — | `Linear` | — | scroll-linked, unaffected | `DetailVerticalHero.cs:436-445` |
| Scroll (narrow arm) | context band | reveal over the last 44 DIP of the collapse (`Reveal(start, distance, 4)`) | — | — | — | — | — | `DetailVerticalHero.cs:430`; `DetailVerticalLayout.cs:85, 636-639` |
| Drawer open (artist page album drawer) | `disco-drawer` slot | Size (`Reflow`, `Leading`, suppress descendants) | 0 → `SlotHeight` | **200 ms** in / **150 ms** out | `SmoothOut` | — | snaps | `ArtistPage.AlbumExpand.cs:459-463` |
| …its panel | inner ZStack | Opacity | 0 → 1 | 150 ms in / 100 ms out | `EaseInOut` | — | fade only | `ArtistPage.AlbumExpand.cs:465-468` |
| Share copied | share glyph | icon swap (scale 0.25 + blur 2) | ⤴ → ✓ → ⤴ after **1600 ms** | 250 ms | `EaseInOut` | — | engine policy | `PlaylistInlineEdit.cs:774, 789-795` |

**Clock rule:** the countdown card polls `DateTimeOffset.UtcNow` once per 1 s tick (`PreReleaseCountdown.cs:53, 59`) —
the one documented exception on this surface, and the reason is that it displays a *wall-clock* target, not a media
position. Its sibling `FlipCountdown` (daylist, playlist pages) was moved to a single wall-clock anchor plus
`FrameTime.NowMs` deltas after the stuck-timer bug (`FlipCountdown.cs:52-78`); **port the countdown to that pattern in
0.3** rather than the per-tick poll. Nothing on this surface reads `Environment.TickCount64`.

---

## 6. Interaction

**Cover.** Drag source for the whole album (`Drag.Source(WaveeDragKinds.Resource, ForEntity(kind, uri, title, cover))`,
`WaveeDetailDrag.Hero`, `WaveeResourceDrag.cs:386-393`) — on the framing box, not on the image. The kind comes from
`WaveeDragKindMap.OfUri(uri)`, and **a uri that maps to `WaveeResourceKind.Route` yields no drag source at all**
(`:388-390`) — worth checking for the `spotify:prerelease:` arm in 0.3. No click target on an album cover (only the
compact collapsed strip's cover is clickable → expands the rail, `DetailRail.cs:311-321`).

**Face pile.** Click or `Down`/`F4` opens the flyout (`FocusTrap`, light dismiss, `BottomEdgeAlignedLeft`,
`ConstrainToRootBounds: false`); `AutomationRole.Button`, focusable, hand cursor; tooltip `"View all artists"`
(literal). The button itself is padded 6/4/6/4 at `Corners 8`, transparent at rest, `HoverFill = FillCardDefault`,
`PressedFill = FillSubtleTertiary`. Each flyout row is a `MenuItem`, 44 tall, navigates to `artist:<uri>` and closes.
The names run beside the pile is a separate hit target that navigates to the **lead** artist
(`ArtistFacePile.cs:61-107, 142-189`).
*Degraded arms:* a flyout row whose artist carries **no uri** gets `OnClick = null` and no cursor (it still occupies its
44-DIP row, `:171-173`); the names run likewise drops to `AutomationRole.Text` with no click and no hand cursor when the
lead has no uri (`:149-152`) — it is never hidden. The whole pile returns an **empty box** when `all.Count == 0`
(`:58`), so an album with no resolvable artist simply has no `rail:artists` row.

**Face-pile portrait demand.** The pile fires its OWN fetch: a `UseResource` keyed on the billed uri list that calls
`svc.Hydrator.EnsureManyAsync(uris, HydrationLevel.Identity)` **only when some billed avatar is still missing**
(`NeedsPortraitFetch`), then re-reads the store (`ArtistFacePile.cs:38-51, 246-252`). Portraits do not ride `getAlbum`.

**CTA cluster.** `Play` plays this album from the **visible (sorted/filtered) order** via the `PlayAllOverride` cell the
list fills, falling back to index 0 (`DetailShell.cs:411`, `DetailTracks.cs:751`). Heart toggles the saved set
optimistically (`SaveButton.cs:46`) and renders **nothing at all** when no mutations source is connected. Share copies
`m.ShareUrl` (getAlbum) or the derived web url, announces "Copied" (`Strings.Auth.Copied`, via
`InputHooks.Default.Announce`), and shows `Icons.Accept` in accent ink for **1600 ms** through a generation-guarded
`UseTimeout` handle; a clipboard **throw** routes to `PlaylistEditErrors.Toast(ex)` and the ✓ never appears; with no
clipboard PAL at all it opens the url instead (`PlaylistInlineEdit.cs:770-814`). The rail's `⋯` (`OwnerMenu`) is **empty
on an album** — it gates on `lib is not null && SpotifyEditsLive(svc) && Live(m) && Capabilities.IsOwner`
(`PlaylistInlineEdit.cs:1090`). Note that `PreSaveButton` (the labelled "Pre-save / Pre-saved" pill in the same file) is
the **artist page's** affordance; the album page deliberately swaps the plain heart's target instead of mounting it.

**Vertical hero actions** (narrow arm only): `Play` · `Shuffle` (32 satellite, tooltip "Shuffle") · heart · share ·
`⋯` = `DetailHeroMoreButton` whose flyout is built **lazily at open from the live model**
(`DetailVerticalHero.cs:240-260, 508-557`): `Add to playlist` (album → "Add", `Icons.Add`, opens the searchable picker),
`Play next` (`WaveeIcons.PlayNext`), `Add to queue` (`WaveeIcons.PlayAfter`). Owner items are appended only for a
playlist the user owns — never on an album.

**Track rows.** Single click selects (Ctrl toggles, Shift extends); double-click / Enter plays; hover reveals the
transport in the `#` cell; right-click / long-press / the `…` cell opens the selection-aware track menu; the `⌄` chevron
opens the versions drawer (one row at a time); swipe actions are like + add-to-queue. All of this is
**01-track-row.md** / **04-detail-track-table.md**; the album-specific parts are: *no* art thumb, *no* Album lane, a
Plays lane, the top-track star, and **a pending row refuses to play** (`DetailTracks.cs:3103`) and suppresses its hover
play button (`TrackRow.cs:233-234`).

**Page body as a drop target.** An album page is a `foreignSurface`: the page-body target is mounted but **transparent**
for resource drags, so a drag crossing the hero is not refused with an accusation (`DetailShell.cs:706-747`).

**Trailing sections.** The whole about-artist card navigates to the artist (Follow is a separate hit target inside it);
chips navigate; media rows navigate on click, play from the hover FAB, and are drag sources; the merch row opens the
external shop through the PAL `OpenUri` and is inert (no `AutomationRole`, no focus) when it carries no url
(`DetailTrailing.cs:519-522`); "Show all N" expands in place, **one-way** (`DetailTrailing.cs:694-697`). A
music-video card — hero or shelf cell — **watches its video**: `Album.PageRules.WatchFor` decides, and the normal arm
**requests the video surface and only then plays the SONG that owns the video** (kind 99 keys a video on its song).
Requesting first is load-bearing, not tidiness: the reducer reads `videoWanted` off the placement state when it decides
the row's kind, so playing first yields audio (see §"The music-video section"). The clicked row when it is already on the
deck **switches in place** rather than restarting; with no placement able to host a video the card **says so**
(`player.videoUnavailable`) and falls back to audio. (0.2.9 played the whole ALBUM from that card,
`DetailTrailing.cs:369`; that is a deliberate 0.3 change, not a regression.)

**Album drawer rows (artist page).** Single click selects (Ctrl toggles, Shift extends from `_sel.AnchorIndex`);
double-click plays via `TrackRow.Invoke` → `Player.PlayAsync(albumUri, i)`; right-click / long-press / the `…` cell
opens the shared selection-aware `TrackContextMenu`; the row is a selection-aware drag source; `RowSwipe` exposes
ToggleLike + AddToQueue; a `SelectionCommandBar` overlays the panel. "Show all N tracks" **navigates** to the album page
(`ArtistPage.AlbumExpand.cs:214-282`).

**Keyboard / accessibility names.** Roles used on this surface: `Button` (cover-less actions, cards, chips, merch rows
with a url, "Show all"), `Hyperlink` (billed names), `MenuItem` (flyout rows), `Tab` (context-band pivot, artist page
only). Focus visuals come from the stock controls (`FocusVisualMargin −3` on `Button`). The countdown, the release
panel and the notes are static text with no focus stops.

*Per-control gaps to note (0.2.9 is inconsistent here; 0.3 should pick one):* the **heart** carries
`AutomationRole.Button` but **no hand cursor, no `Focusable`, and no tooltip** (`SaveButton.cs:38-47`); the **share FAB**
carries Role + `Focusable` + hand cursor but no tooltip (`PlaylistInlineEdit.cs:787`); only the **face pile** button has
a tooltip (a literal, §6 loc drift). The face-pile keyboard affordance is `Down`/`F4` on the button
(`ArtistFacePile.cs:74-81`) — there is no keyboard path to the "Other versions" dropdown beyond the stock
`DropDownButton`'s own.

**Localised strings used by this surface** (`assets/loc/en-US.json`): `detail.badge.album|ep|single|compilation`,
`detail.metaLineYear`, `detail.metaLineYearPending`, `detail.songCount`, `detail.durationHrMin|durationMin`,
`detail.play`, `detail.shuffle`, `detail.playNext`, `detail.addToQueue`, `detail.addToPlaylist|copyToPlaylist`,
`detail.aboutRelease`, `detail.factSongs`, `detail.factLength`, `detail.factLabel`, `detail.factReleased`,
`detail.factReleases`, `detail.otherVersions`, `detail.aboutTheArtist`, `detail.fansAlsoLike`, `detail.featuredOn`,
`detail.moreBy`, `detail.similarAlbums`, `detail.watchOfficialVideo` (the HERO arm's eyebrow only),
`artist.musicVideos` (the SHELF arm's header), `detail.versions.musicVideo` (the hero subtitle),
`player.videoUnavailable` (the no-host warning a card raises before falling back to audio), `artist.merch`,
`artist.buy`,
`artist.follow|following`, `detail.preReleaseEyebrow` ("Coming soon"), `detail.preReleaseOut` ("Out now"),
`detail.preReleaseUnitDays|Hours|Minutes|Seconds`, `detail.preSave|preSaved`, `detail.empty.noTracks|noMatch`,
`detail.notice.minifiedAlbum`, `detail.versions.*`, `detail.trackFacts.*`, `home.showAllCount`,
`detail.discography.showAllTracks`, `videoOverride.customLabel`.

**Loc drift to fix in 0.3** (code wins today, but these are bugs): `"View all artists"` is a literal
(`ArtistFacePile.cs:103`) although `detail.viewAllArtists` exists; `"View collaborators"` likewise
(`CollaboratorFacePile.cs:87` vs `detail.collabView`); `"N collaborators"` / `"Open to collaboration"` are string
concatenations (`CollaboratorFacePile.cs:40-42`) although `detail.collabCount` / `detail.collabOpen` exist; and
`"8 of 12"` is built by concatenation in `AlbumReleaseFactsRules.cs:65-67` although `detail.factOfCount` exists (the
design doc's D5 asked for the key — `about-this-release-facts-implementation.md:100`).

**The worst of the four: the Length tile is hard-coded English.** `AlbumReleaseFactsRules.TotalTimeLiteral`
(`:110-115`) spells `$"{h} hr {m} min"` / `$"{m} min"` as literals — deliberately, because the rules file must stay
engine-free for its tests — while `DetailFormat.TotalTime` (`DetailConfig.cs:264-269`) spells the *same arithmetic*
through `Strings.Detail.DurationHrMin` / `DurationMin`. Both are on the album page at the same time: the rail's
**Length tile** takes the literal and the vertical hero's **meta line** takes the localized form, so in any non-English
locale one album page states its own duration two different ways. In 0.3 the rule must return the *parts* (`h`, `m`) and
let the view format them, or the loc runtime must be reachable from `Album.cs`'s CORE section. Same shape applies to
`Released` (`FormatReleaseDate` is `InvariantCulture` `"MMMM d, yyyy"`, `:91-102`) while the pending row's `ShortDate`
right beside it is `CurrentCulture`.

---

## 7. Data & readiness in 0.3 terms

| Visual element | 0.2.9 source | 0.3 read | Readiness predicate (skeleton until true) |
|---|---|---|---|
| Cover | `Album.Cover` (`DetailModel.Cover` after the `PreferVisible` latch, `DetailPage.cs:137-144`) | `a.ImageId` (StringId column) | `a.Knows(AlbumFields.Identity)`; before that the shimmer box — **never** a placeholder gradient behind a real title |
| Eyebrow "ALBUM · 2019" | `a.Kind` + `a.Year` → `DetailRail.EyebrowText` | `a.Kind` (byte) + `a.Year` (ushort) | `Knows(Identity)`. **The three degraded forms (`EyebrowText`'s kind-alone / year-alone / "") are UNREACHABLE on the album path in 0.2.9**: `MapAlbum` writes `Year: a.Year.ToString()` unconditionally (`DetailPage.cs:711`) over an `int Year` (`Models.cs:294`), so a yearless album carries `"0"` and the eyebrow reads **"ALBUM · 0"**. Fix in 0.3 (emit null below 1); the degraded arms belong to Show |
| Title | `Album.Name` | `a.TitleId` → `Entities.Strings.Resolve` | `Knows(Identity)` |
| Billed artists + avatars | `Album.Artists` (uri+name) ∪ `Album.ArtistsDetailed` (visuals) ∪ store portraits, with a self-fired `Hydrator.EnsureManyAsync(billed, Identity)` when any portrait is missing (`ArtistFacePile.cs:40-51`, `NeedsPortraitFetch` at `:244-249`, `ResolveOne`'s three-source merge at `:231-242`) | `Edges.AlbumArtists.Targets(a.Slot)` + each `Artist.ImageId` | pile paints as soon as the edge is complete; a face with no portrait draws **initials** (`PersonPicture`), never a gap |
| `+N` overflow | **`allDistinct.Count − billed.Count`** — the track-only contributors beyond the billed set, not the total (`ArtistFacePile.cs:53-55`, `AllDistinctArtists` at `:204-220`). With no resolvable billed artist it degrades to `all.Count − 4` over the first four of `all`. See §0.5 for the >4-billed defect | `|union(Edges.TrackArtists over a.TrackSlots)| − |Edges.AlbumArtists|` | needs `Edges.AlbumTracks` complete **and** `TrackFields.Artists` known for those rows; until then show the billed faces with **no** `+N` frame (an under-count is a lie the same way an over-count is) |
| Meta line (narrow arm) | `Strings.Detail.MetaLineYear(songCount, totalTime, year)`; the *Pending* variant drops the duration while any row is thin | `a.TrackCount`, Σ `DurationMs` over `a.TrackSlots`, `a.Year` | all rows `Knows(TrackFields.Title \| Duration)`; else the year-only form |
| Track rows | `Album.Tracks` | `Edges.AlbumTracks.Targets(a.Slot)` + `AlbumTrackEdge(Disc, Number)` | edge `State == complete`; each row paints at `TrackFields.Row` |
| Plays lane + top-track star | `Track.PlayCount` (kind 185) | `T.PlayCount` | per row: `> 0` → value, else `—`; star = `argmax(PlayCount) > 0` (album profile only) |
| Pending-row treatment | `Track.IsNotYetOut()` (Availability + `AvailableAt`) | `TrackFlags.Unavailable` / `AvailableAt > now` | per row, wall-clock; expires itself with no refetch |
| "About this release" | `AlbumReleaseFacts` computed in `MapAlbum` when `HydrationLevels.Of(a) >= Full` | `Album.ReleaseFacts()` over the publishing columns | **`a.Knows(AlbumFields.Publishing \| Release)`** — the whole record or nothing (the panel must not grow a Label line later) |
| Countdown | `DetailModel.UpcomingAt` = `PreReleaseDerivation.UpcomingAt(a, now)` | same rule over `a.PreReleaseEnd`, the rows' `AvailableAt`, `a.ReleaseInstant` | needs `Knows(Release)` **and** the track rows, because a waterfall album's only evidence is the rows |
| Pre-save target | `PreReleaseLink` (kind 138), asked only when `album.IsPreRelease \|\| UpcomingAt is not null` (`DetailPage.cs:416-418`) | `a.PreReleaseUri` column + a resolve epoch | heart falls back to the album uri until resolved — never blocks the CTA. **A resolved link is only adopted when `link is { IsUpcoming: true }`** (`DetailPage.cs:725`): kind 138 is cached up to 30 days, and a stale link must not turn the heart into a pre-save for a record that shipped last week |
| Saved state | `LibraryBridge.IsSaved(uri)` | `User.Me` + `Edges.SavedAlbums.Contains(me, a.Slot)` | the edge's own `Known` bit; unsaved outline is the honest default |
| Other versions | `Album.OtherVersions` (getAlbum) | **new** `Edges.AlbumVersions` (gap D4) | `Knows(Publishing)`; absent ⇒ no button |
| More by artist | `Album.MoreByArtist` (getAlbum), falling back to `About.TopAlbums` | `Edges.ArtistReleases` of the lead artist, or **new** `Edges.AlbumMoreBy` (gap D5) | section mounts only when non-empty |
| About the artist / Fans also like / Featured on / Merch / Similar | 5 independent enrichment calls behind one aggregate (`DetailTrailing.cs:155-187`) | see gaps D6–D8 | each section is independently present-or-absent; a failure **omits** its section, never the band |
| Notice (minified album) | `PlaylistPageNoticeRules.ForAlbum(tracks)` | any row `!Knows(TrackFields.Title)` while the edge is complete | the full page suppresses the strip (it self-heals); the embedded library pane shows it |
| Page tone + accent | `CoverColorPlane` grading keyed by image id | gap D10 | tone paints when graded; before that the page is the neutral ground — never a flash of a different colour |

**Who asks for what, in 0.2.9 — the split 0.3 must reproduce or deliberately change.** The open path asks
`GetAlbumAsync(uri, HydrationLevel.Rich)` (`DetailPage.cs:420-421`) — identity, tracks, ©/℗ and the Plays star ride one
catalogue POST. A store-triggered refresh re-projects at `HydrationLevel.None` (`:398-403`), scheduling nothing. The
**Full/getAlbum rung — Label, courtesy, `OtherVersions`, `MoreByArtist`, `ArtistsDetailed` — is asked for by the
TRAILING PANE**, in the background, off the interactive open path: `svc.Hydrator.EnsureAsync(albumUri, Full,
new HydrationOptions(HydrationMode.Background, Surface: TraitSurface.AlbumOpen))` (`DetailTrailing.cs:166-170`). Two
consequences the chapter's readiness table depends on: "About this release" cannot appear before the trailing band has
mounted and fired, and **the embedded library pane (which forces `HasTrailing = false`) never gets Full at all**, so it
permanently shows no release panel. The pile's portrait batch (`EnsureManyAsync(billed, Identity)`) is a third,
independent ask fired by the face pile itself.

Demand, in 0.3 shape (the page asks for its **whole** model on mount — no visible-window fetching):

```csharp
UseEffect(() =>
{
    Entities.Ensure(_a, AlbumFields.All);                 // identity + release + publishing
    Entities.EnsureEdges(_a, EdgeKind.AlbumTracks);       // the complete tracklist
    Entities.EnsureRows(_a.TrackSlots, TrackFields.Row);  // ONE batch (300/POST), not per visible range
    Entities.EnsureEdges(_a, EdgeKind.AlbumVersions | EdgeKind.AlbumMoreBy);   // below-the-fold, lower priority
}, _a.Slot);
```

### DATA GAPS

| # | What this surface shows | 0.2.9 source | Proposed 0.3 column / edge |
|---|---|---|---|
| D1 | Album identity: name, cover, year, track count, kind (Album/EP/Single/Compilation) | `Album` record (`Wavee.Core/Domain/Models.cs:292-306`) | `AlbumTable`: `Column<StringId> Title, Image; Column<ushort> Year; Column<int> TrackCount; Column<byte> Kind` + `AlbumFields.Identity`. **The plan defines no Album columns at all** (§2 budgets `Album.cs` at 200 lines; §4 shows none). |
| D2 | Release facts: label, copyright (multi-line), courtesy line, ISO release date, precision, disc count, share url | getAlbum (`Models.cs:299-305`) | `Column<StringId> Label, Copyright, Courtesy, ReleaseDateIso, ShareUrl; Column<byte> DatePrecision, DiscCount; Column<int> ReleaseInstant` + `AlbumFields.Publishing` |
| D3 | Prerelease: `IsPreRelease`, `PreReleaseEnd`, the resolved `spotify:prerelease:` uri | `Album.IsPreRelease/PreReleaseEnd` + kind-138 `PreReleaseLink` | `Column<int> PreReleaseEnd; Column<StringId> PreReleaseUri; AlbumFlags.PreRelease` + `AlbumFields.PreReleaseLink` (its own group: the resolve is a separate request, gated as it is today) |
| D4 | "Other versions" (deluxe / remaster / anniversary) | `Album.OtherVersions` | **`EdgeTable<NoEdge> AlbumVersions`** — named by plan §4.13's own tree but **missing from §4.3** |
| D5 | "More by <artist>" carried *by the album payload* | `Album.MoreByArtist` | `EdgeTable<NoEdge> AlbumMoreBy` (or `ArtistReleases` with an Authority note — but the album payload lands before the artist is ever fetched) |
| D6 | About-the-artist card (bio, verified, top albums) | `AlbumEnrichment.GetAboutArtistAsync(leadArtist, leadTrack)` | `Artist` columns `BioId`, flag `Verified` + `Edges.ArtistReleases`; the card is then a pure read of the artist handle |
| D7 | "Fans also like" — related artists of the **artist** (full album) or of the **seed track** (short release) | `GetRelatedArtistsAsync` / `GetTrackContextAsync` | `Edges.ArtistRelated` exists ✓; add **`EdgeTable<NoEdge> TrackRelatedArtists`** for the short-release arm |
| D8 | "Featured on" (playlists), "Similar albums" (seeded by the top-play track), "Merch" | `GetRecommendedPlaylistsAsync`, `GetSimilarAlbumsAsync(seedTrack, 24)`, `GetMerchAsync` | `EdgeTable<NoEdge> AlbumFeaturedOn`, `EdgeTable<NoEdge> AlbumSimilar` (keyed on the album even though the request is seeded by a track), and — since merch is not an entity — a small `MerchTable` (`Name, Price, ImageId, ShopUrl`) + `EdgeTable<NoEdge> AlbumMerch` |
| D9 | The `prerelease:<id>` route and its album resolve | `PreReleaseUris` + `svc.PreRelease.ResolveAsync` | keep `RouteKind.Album` with a `Provider`-tagged `EntityUri`; resolve in `Fetch` and rewrite the route's subject once the album slot is known |
| D10 | Cover palette (accent, page tone, WCAG ink) | `SpotifyLive.CoverColorPlane` (async grading, cached by image id) | an `ImagePalette` side table keyed by image `StringId`: `Column<uint> AccentArgb, ToneArgb; Column<byte> Known` + one `Signal<uint> Changed`, written by the grading SHELL. **The plan has no palette anywhere**, and every accent-filled control on this page depends on it |
| D11 | Music-video presence, incl. a user-attached override | kind-99 association plane + `VideoPresence.HasOverride` | `TrackFlags.HasVideo` ✓ + `VideoCounterpart` ✓ (plan §4.2) — add a `Local`-authority override bit so a user's own mp4 still lights the lane |
| D12 | The versions drawer: alternate audio (kind 98), the video counterpart, per-item audio format, and the waveform (kind 237) | `TrackExpansion` | `EdgeTable<byte /*kind*/> TrackVersions` + `Column<StringId> WaveformBlob` (or a side `WaveformTable`); none exist in the plan |
| D13 | Disc + track numbers | not decoded at all in 0.2.9 — rows are numbered by **display position** | `AlbumTrackEdge(Disc, Number)` exists in plan §4.3 ✓, but the decoder must actually fill it, and §9 records the behaviour change |
| D14 | "Is this album thin / minified?" | `HydrationLevels.TrackUnnamed(track)` | `!t.Knows(TrackFields.Title)` over a complete `AlbumTracks` edge — a derived fact on the model, exactly as today |
| D15 | The album's own save target when it is a prerelease | `LibraryBridge.ToggleSaved(prereleaseUri)` | `Edges.SavedAlbums` must accept a `spotify:prerelease:` uri (its own `EntityKind`, or a flag on the album row) |

**D8's merch table — DECIDED, plan §9.6 Q4, 2026-09-12: ADDED.** The `MerchTable` + `EdgeTable<NoEdge> AlbumMerch`
this row specifies land in Wave 1 with the other tables and edges, owner B, inside `Edges.cs` (§4.3 gains both;
`Edges.cs`'s §2 budget grows 900 → 980, +80 UNVERIFIED since this row gives a shape, not a line count). The album
trailing band (§1, §2 W12, `Album.UI.cs`/`Album.Page.cs`) keeps its merch row and every merch parity item (27, 62)
exactly as this chapter already specifies — nothing about the rendered surface changes, only `Edges.cs` grows.

---

## 8. Pure rules to port verbatim

| Name | 0.2.9 file | Decides | Tests | 0.3 destination |
|---|---|---|---|---|
| `AlbumReleaseFacts` + `AlbumReleaseFactsRules` | `Features/Detail/AlbumReleaseFactsRules.cs` (116) | Songs ("13" / "8 of 13"), Length ("74 min" / "1 hr 12 min"), Released by precision (YEAR/MONTH/DAY), the Released↔Releases tense, Label, the ordered notes list | `Wavee.Tests/AlbumReleaseFactsRulesTests.cs` (188) | `Entities/Album.cs` — CORE section "release facts" |
| `PreReleaseDerivation` | `Features/Detail/PreReleaseDerivation.cs` (43) | the single countdown instant: `PreReleaseEnd` ▸ earliest future row `AvailableAt` ▸ a future parsed release date ▸ null; plus `ReleaseInstant(iso)`. **Every rung is wall-clock gated (`> now`)**, so a stale flag can never resurrect a countdown; a bare `"2026"` deliberately does NOT parse (the mapper normalises YEAR precision to `yyyy-01-01` upstream) | `Wavee.Tests/PreReleaseModelTests.cs` (class `PreReleaseDerivationTests`) — plus `PreReleaseAvailabilityTests`, `PreReleaseMapperTests`, `PreReleaseMergeTests`, `PreReleaseWireTests` for the wire/merge half | `Entities/Album.cs` — CORE "upcoming" |
| `AlbumDrawerVerdict` (+ `DrawerVerdict`) | `Features/Detail/AlbumDrawerVerdict.cs` (48) | the artist-page drawer's rows/columns/shown/total/loading/ready-empty/show-all and **both heights** from plain inputs | `Wavee.Tests/AlbumDrawerVerdictTests.cs` (146) | `Entities/Album.cs` — CORE "drawer"; consumed by `Artist.Page.cs` |
| `PlaylistPageNoticeRules.ForAlbum` | `Features/Detail/PlaylistPageNoticeRules.cs:88-93` | MinifiedAlbum vs None from the rows in hand (stateless, self-clearing) | **none — the file's own doc-comment claims `PlaylistPageNoticeRulesTests`, which does not exist** (verified: no test file references the class) | `Entities/Album.cs` — CORE "notice"; write the missing test |
| `DetailRail.EyebrowText` / `EyebrowRun` | `DetailRail.cs:566-593` | "ALBUM · 2019" / kind only / year only / "" — one composition for all three layouts | none | `Entities/Album.cs` CORE (string rule) + `Album.UI.cs` (the run) |
| `DetailPage.MapAlbum`'s meta-line rule | `DetailPage.cs:695-699` | which of `MetaLineYear` / `MetaLineYearPending` the header states while any row is thin | none | `Entities/Album.cs` CORE |
| `AlbumTrailing.HasReleasePanel` / `HasTrailingSections` | `DetailTrailing.cs:200-201, 146-153` | whether the panel / the band exists at all | none | `Entities/Album.cs` CORE |
| `AlbumTrailing.SeedTrack` | `DetailTrailing.cs:334-341` | the similar-albums seed = the highest play-count track, else track 0 | none | `Entities/Album.cs` CORE |
| `AlbumTrailing`'s **`shortRelease`** predicate | `DetailTrailing.cs:65` | `ReleaseKind == Single \|\| tracks.Count is > 0 and <= 2` — in 0.2.9 it gated the watch-video card AND switched "Fans also like" to the seed track's. **0.3 keeps only the second job** (plus the skeleton's shape): the music-video section is a function of `SelectVideos` alone | `AlbumPageRulesTests`, `AlbumVideoSectionTests` | `Entities/Album.cs` CORE |
| `PageRules.SelectVideos` + `AlbumVideo` | **new in 0.3** (no 0.2.9 counterpart — 0.2.9 reduced the members to one bool) | one entry per video-bearing member, in track order: the member slot (what plays), the kind-99 counterpart, **the video's own still** and the video's duration. Capped at `VideoCap` 16 | `Wavee.Tests/AlbumVideoSectionTests.cs` | `Entities/Album.cs` CORE |
| `DetailFormat.ShortDate` | `DetailConfig.cs:239-245` | the pending row's duration-lane date: `"4 Sep"` in-year, `"4 Sep 2027"` across a year boundary, CurrentCulture | none (`TrackExpandedFactsTests` covers its siblings) | `Entities/Track.cs` CORE |
| `PreReleaseCountdown.Breakdown` | `Components/PreReleaseCountdown.cs:120-121` | the four per-unit remainders, clamped at 0 | none | `Entities/Album.cs` CORE |
| `AlbumReleaseFactsRules.TotalTimeLiteral` | `AlbumReleaseFactsRules.cs:110-115` | the Length phrase — **a second, English-only copy of `DetailFormat.TotalTime`** (`DetailConfig.cs:264-269`), kept because the rules file must stay engine-free. See §6's loc note: in 0.3 the rule should return `(hours, minutes)` and let the view format | `AlbumReleaseFactsRulesTests` pins the literal, so it pins the drift too | `Entities/Album.cs` CORE returns parts; `Album.UI.cs` formats |
| `AlbumTrailing.HasCustomVideo` (over `VideoPresence.HasOverride`) | `DetailTrailing.cs:345-351` | whether the music-video HERO arm's eyebrow reads the custom-video label — a linear scan (the shelf arm draws no eyebrow, so it never asks) | `AlbumPageRulesTests` | `Entities/Album.cs` CORE (D11) |
| `DetailPage.ResolveConfig` | `DetailPage.cs:330-341` | `AlbumKind` → which of `Album` / `Single` / `Compilation` the surface takes (**EP falls through to plain `Album`**) — the one place release kind becomes layout | none | `Entities/Album.cs` CORE |
| `AlbumTrailing.AlbumSubtitle` / `VersionLabel` | `DetailTrailing.cs:304-316, 492-495` | a related album's subtitle (artist ▸ year ▸ kind) and an other-version's menu label | none | `Entities/Album.cs` CORE |
| `TrackList.TopTrack` | `DetailTracks.cs:399-406` | which row gets the star (album profiles only; null when no row has plays) | none | `Entities/Album.cs` CORE |
| `TrackExpandedFacts` (+ `TrackFact*`) | `Features/Detail/TrackExpandedFacts.cs` (367) | the expanded row's ordered facts, the hero-four partition, the key/tempo/duration formatters | `Wavee.Tests/TrackExpandedFactsTests.cs` (591) | `Entities/Track.cs` CORE (**01-track-row.md** owns it; the album drawer is a consumer) |
| `DetailVerticalLayout` (title type plan, artwork, band height, slot map) | `Features/Detail/DetailVerticalLayout.cs` (662) | every narrow-arm number | `DetailVerticalLayoutTests.cs` (615) | **03-detail-frame.md** — shared |
| `DetailLayoutBreakpoints`, `DetailRailPolicy`, `DetailRevealRamp` | 88 / 67 / 27 lines | mode + tier ladders with hysteresis; which surface gets a grip and at which mode; the reveal ramp | `DetailLayoutBreakpointTests.cs`, `DetailRailPolicyTests.cs`, `DetailRevealRampTests.cs` | **03-detail-frame.md** |
| `DetailTrackTableRules` + `TrackLane` | 279 lines | lane presence, relief ladder, lane widths, row heights, art sizes | (covered by the table's tests) | **04-detail-track-table.md** |
| `PlaylistListState` | 40 lines | shimmer vs "Nothing here yet" vs "No songs match" | `PlaylistListStateTests.cs` | **04-detail-track-table.md** |
| `FacePiles` geometry (`Avatar/Ring/Outer/Overlap/Step/SlotsIn/VisibleFaces`) | `Components/FacePiles.cs` (153) | the pile's geometry and how many faces fit a width | none | `Platform/Controls.cs` (**00/02**) — and in 0.3 the two hand-rolled piles (`ArtistFacePile`, `CollaboratorFacePile`) should finally be ONE control over it |

---

## 9. Re-author notes

**Must not be simplified.**

1. *Two columns at wide widths.* Collapsing the album page onto the vertical hero at all widths (which is what plan
   §4.13's wireframe draws) deletes the rail, the grip, the collapse detent, the release bento's home and the page's
   whole reading order. The vertical hero exists for narrow windows and for the explicit "Hero" preference — it is not
   the default look.
2. *The fixed facts grid.* Any content-sized / wrap-grown tile row re-creates the bug the whole
   `about-this-release-facts-implementation.md` pass removed. Rows are authored: `[Songs][Length]` then a full-width
   `Released`; Label is a note.
3. *The Full-rung gate on the facts record.* Showing Songs/Length/Released at Rich and prepending Label at Full is a
   visible reflow under the reader (`DetailPage.cs:726-740` spells out the trade).
4. *The three-shape upcoming test.* `IsPreRelease` alone is wrong for two of the three wire shapes; the vaultboy case
   (`IsPreRelease=false`, a future date) and the waterfall case (rows with a future `AvailableAt`) both matter.
5. *One reveal, one swap.* The trailing band must be mounted (and shimmering) from the first frame; a zero-height box
   that later inserts a skeleton is the "three jolts" regression (`DetailTrailing.cs:19-24`).
6. *Zero-allocation scroll vs per-row richness.* 0.2.9 reconciled these by: recycled bound slots (`ItemsView.CreateBound`
   + `BoundItemsSource`), ONE equality-gated snapshot memo feeding every row (`TrackRowsSnapshot`), the column shape as a
   memo the rows *read* rather than a Key (so a breakpoint cross patches instead of remounting), and plain/diffable cells
   with no `Animate`. Keep all four. The measured (not uniform) layout is what lets one row grow for its drawer.
7. *Keys.* `rail:*` keys are constant across preview→full; `save:<uri>`, `prerelease:<uri>:<ticks>`, `trail:<signature>`,
   `drawer:<rowKey>` encode identity **because** those children must remount. Do not "tidy" either family.

**Traps.**

- `ReuseGuard` will fire if the face pile or a trailing stack takes ctor args that change after mount — use `Props`
  (pile) or a `Key` (stack).
- The accent is a `Func<ColorF>` thunk everywhere (countdown, heart, flip): the palette lands *after* mount, and reading
  it inside `Render` is the subscription (`PreReleaseCountdown.cs:31-34`).
- `DetailRail.BilledArtists` (`DetailRail.cs:503-556`) is **dead code** — unreferenced since the face pile landed. Do not
  port it; its "+N counts distinct track artists" rule lives on in `ArtistFacePile.AllDistinctArtists`.
- `DetailConfig.AlbumColumns` (`DetailConfig.cs:193-194`) is **also dead**: the table builds its own width tracks
  (`DetailTracks.cs:554-582`). Do not carry a second column list into 0.3.
- The 0.2.9 `#` column numbers rows by **display position**, not by wire track number (`TrackRow.cs:1073`). With
  `AlbumTrackEdge(Disc, Number)` the 0.3 row *can* show the real number — that is a deliberate change and it must be
  taken as one (it changes what a sorted or filtered album list shows).
- **Two different "Show all" verbs share one phrase.** A trailing section's `Show all N` **expands the stack in place**
  (`TrailingStack`, one-way); the artist-page album drawer's `Show all N tracks` **navigates to the album page**
  (`ArtistPage.AlbumExpand.cs:271-282`). They are different affordances in different type ramps (`HyperlinkButton
  Small` vs a 12/600 accent row). Do not merge them into one `Album.UI` helper.
- **`DetailRail.BuildHeader` is not album code** despite living in `DetailRail.cs` — its only call site is the Show
  page's narrow arm (`DetailShell.cs:587`). Porting it into `Album.Page.cs` would import an arm the album never renders
  (see §1.1).
- **`+N` is an overflow, not a total** (`ArtistFacePile.cs:53-54`). Writing it as "the distinct artist count" — the
  shape 0.2.9's *dead* `BilledArtists` used (`extra = seen.Count − shown`, itself an overflow) — is the same arithmetic
  said two ways; state it once, as `all − billed`, and unit-test it, because nothing does today.
- **`PreReleaseCountdown.Bare`** exists (`:36-38, 63`) and drops the plate, ring and eyebrow for a caller that already
  has its own. It is the artist-page tone panels' arm; the album rail never sets it. Keep the flag, or the 0.3 artist
  page grows a second copy of the tile formatting.
- **The embedded library-pane album is a real arm, not an edge case** (`DetailTracks.cs:701`). It forces
  `HasTrailing = false` — which silently also means the Full rung is never requested (`DetailTrailing.cs:166-170` is the
  only caller), so "About this release" is structurally absent there, not merely late. If 0.3 moves the Full ask onto
  the page, the embedded pane gains a release panel it has never had: that is a behaviour change and must be taken as
  one.
- **Two duration formatters, one page.** `AlbumReleaseFactsRules.TotalTimeLiteral` (invariant English) feeds the rail's
  Length tile; `DetailFormat.TotalTime` (loc runtime) feeds the vertical hero's meta line. They are the same arithmetic
  with different output in any non-English locale. Porting both verbatim ports the bug (§6, §8).
- **`MapAlbum` stringifies the year unconditionally** (`DetailPage.cs:711`), so `EyebrowText`'s degraded arms never fire
  on an album and a yearless release reads "ALBUM · 0". Port `EyebrowText` as written; fix the *input* in `Album.cs`.
- **`PreReleaseCountdown` polls the wall clock per tick; `FlipCountdown` does not.** Both live on this surface's
  neighbours and they disagree — port the countdown to `FlipCountdown`'s anchor+`FrameTime.NowMs` pattern
  (`FlipCountdown.cs:52-78`) rather than copying `PreReleaseCountdown.cs:53, 59`. See §5's clock rule.

**Where the plan is wrong or too thin for this surface — every difference from §4.13, plus §2 and §4.12.**

| # | Plan §4.13 (and §2 / §4.12) | 0.2.9 reality |
|---|---|---|
| 1 | One vertical composition: Hero over TrackList over About/MoreBy/Versions | Four arms: two-column mode 0/1/2 (rail 280/224/188), the collapsed 96 strip, and the vertical hero — plus a persisted "Hero at every width" preference (`DetailShell.cs:509-512`) |
| 2 | "cover 232" | No such number: rail cover = `railW − 24` (156…456); vertical art = `clamp(0.44·inner, 144, 240)` or `clamp(w−2·pad, 96, 280)` |
| 3 | "◀ back" inside the page | The page draws no back affordance; the shell toolbar owns navigation |
| 4 | Eyebrow "ALBUM" | "ALBUM · 2019" as ONE tracked run; kind is `AlbumKind` and also switches the config (Single ⇒ no multi-select, Compilation ⇒ per-track artist subline) |
| 5 | "● artist" | An interactive stacked face pile (≤4 avatars, `+N` of distinct track artists, chevron, flyout) + accent name run |
| 6 | "· 2024 · 12 songs, 41 m" beside the artist | The two-column rail deliberately has **no** meta line; the vertical hero has `MetaLineYear`, with a duration-less variant while any row is thin |
| 7 | Actions `[▶ Play] [♡] [⋯]` | Wide arm: Play + heart + **share** (no `⋯` — the overflow is playlist-owner-only). Narrow arm: Play + **Shuffle** + heart + share + `⋯` |
| 8 | Track list header `# Title/Artist ▶ Plays ⏱` | `# · ♥ · Title · Plays · Duration · (Video \| …) · ⌄` under a tier+relief ladder, a sortable header, a command bar (Play next split, Shuffle, Sort, Row size, Select, Find + filter funnel), zebra rows, the top-track star, swipe actions and a context menu |
| 9 | `About(a)` as a section between the list and MoreBy, gated on `Knows(Publishing)` | The release bento lives **in the rail** (wide) / in the trailing body (narrow), gated on the **whole** facts record being final |
| 10 | Three trailing blocks | Up to **seven**, in a fixed order (Music videos → About the artist → Fans also like → More by → Featured on → Merch → Similar albums), each capped at 5 rows with "Show all N". The music-video section is 0.3's own rewrite: one card per video at ANY release length, each with the VIDEO's own still, title and duration; one video is a hero card, two or more a horizontal `PagedShelf` (edge fades + pips + page snap, never a wrapping grid); and the click **requests the video surface and then plays**, so it watches the video instead of starting the song |
| 11 | `OtherVersions` as a section | A `DropDownButton` inside the release panel; items read "Name · Year · KIND" |
| 12 | "Label · ℗ 2024 · Copyright line" as a page footer strip | 11-px note LINES under the tiles, inside the panel, wrapping to ≤4 lines each |
| 13 | (nothing) | **The entire prerelease surface is missing from the plan**: the `prerelease:` route, the kind-138 resolve, the countdown card, the pre-save heart-target swap, the greyed pending rows with a date in the duration lane, and the "N of M songs" tile |
| 14 | `Entities.Ensure(_a, AlbumFields.All)` + one row batch | Correct as far as it goes, but 0.2.9 also runs a deliberately below-the-fold Full fetch and five fail-soft enrichment calls; the plan has no vocabulary for "this section fails soft and simply disappears" |
| 15 | `UseSignal(Entities.Current.Albums.Changed)` is the page's only subscription | The page also needs: the **track** table's publication (title/plays repair), the edge version, the palette signal, the saved-set signal, and the playback identity |
| 16 | `UseEffect(() => …)` with no dep key | Every 0.2.9 loader is keyed on the route so a reused page instance re-demands for its new subject |
| 17 | No skeleton in the plan at all | The album skeleton reserves the *exact* hero band and trailing band from mount; without it the page shoves itself when content lands (D49) |
| 18 | §4.3 `Edges` has no `AlbumVersions` | …although §4.13's own tree names it. Also missing: `AlbumMoreBy`, `AlbumFeaturedOn`, `AlbumSimilar`, ~~`AlbumMerch`~~, `TrackRelatedArtists`, `TrackVersions` — **`AlbumMerch` resolved 2026-09-12, plan §9.6 Q4: added to §4.3 alongside `MerchTable`, the rest of this row still stands** |
| 19 | §4.12 `Track.Row` draws a 40-DIP `ImageEl` thumb on every row | Album rows have **no** art thumb (`ShowArtThumb: false`); the thumb is a playlist/Liked lane, density-keyed 32/40/48 |
| 20 | §4.12 `style.NumberOf(t)` | 0.2.9 numbers by display position; see the trap above |
| 21 | §2 has no `Detail.*` file | Album, playlist, Liked and Show share ONE frame (rail, hero, table, band, skeleton, reveal, rail prefs) — ~4,000 lines of shared machinery with no home in the plan's tree. Propose `Entities/Detail.UI.cs` (+ `Detail.cs` for the pure ladders), owned by whoever writes **03/04** |
| 22 | §2 budgets Album at 200 + 600 + 1,200 = 2,000 | See the line budget below |

**Line budget.** 0.2.9, album-owned files: `DetailTrailing.cs` 709 + `ArtistFacePile.cs` 271 + `PreReleaseCountdown.cs`
127 + `AlbumReleaseFactsRules.cs` 116 + `AlbumDrawerVerdict.cs` 48 + `PreReleaseDerivation.cs` 43 = **1,314**, plus the
album branches of shared files (rail album arms ≈ 130, `MapAlbum`/`LoadAlbumDetailAsync` ≈ 90, `DetailConfig` album model
fields + literals ≈ 80, vertical-hero album arms ≈ 60, drawer panel in `ArtistPage.AlbumExpand.cs` ≈ 300) ≈ **1,970**;
`TrackVersionsPanel.cs` (411) is shared with playlist/Liked. Plan target: **2,000** (`Album.cs` 200 + `Album.UI.cs` 600 +
`Album.Page.cs` 1,200). **Honest estimate: 2,600–3,000** — `Album.cs` needs ~400 (columns for identity + release +
publishing + prerelease, the flags, and five ported pure rules), `Album.UI.cs` ~800 (face pile, cover, eyebrow, stat
tiles, trailing rows, versions menu, the artist-page drawer panel), `Album.Page.cs` ~1,500 (two arms, the release panel,
seven trailing sections with their gates, the prerelease card, the skeleton hooks). Wave 5's owner M also carries
`Track.UI.cs`, `Episode.UI.cs`, `Show.UI.cs`, `Show.Page.cs` — ~4,850 lines of budget, realistically ~6,000; consider
splitting Show/Episode to another owner.

**Files missing from the §2 tree for this surface:** a shared detail frame (see #21), a palette/grading home (D10), a
merch table (D8), and `Wavee.Tests` files for `PlaylistPageNoticeRules` (missing today) and for the four rules currently
untested (`EyebrowText`, `SeedTrack`, `TopTrack`, `HasTrailingSections`).

---

## 10. Parity checklist

Run the kept 0.2.9 Release build side by side:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Unless stated, the route is a **fake album** (`--fake` seeds albums; open one from Home or Search → an album card) and
the comparison is a static screen capture at the same window size with the same theme.

1. **Rail width** at 1280×900: rail is exactly 280 DIP; cover exactly 256×256. *(static capture, measure in pixels/DIP)*
2. **Rail gap**: 14 DIP between every rail row (cover→eyebrow→title→pile→CTA). *(capture, measure)*
3. **Rail layer**: the rail column is one flat `FillLayerDefault` band against the page tone; the track side is not.
4. **Cover treatment**: `Radii.Card` 8, a card shadow, and visibly more saturated than the same art in the track list.
5. **Eyebrow** reads "ALBUM · <year>" in tertiary ink, letter-spaced, one line, never uppercase-transformed beyond what
   the loc string carries.
6. **Title rung**: at window height ≥ 900 the title is 40/52; shrink the window under 900 → it becomes 28/36 with no
   other row moving. *(two captures)*
7. **Title auto-fit**: an album with a very long name wraps to at most 3 lines in the rail and ellipsizes; it never
   widens the rail. *(pick the longest fake album title)*
8. **Face pile**: up to 4 overlapping 28-DIP avatars in rings, a `+N` frame when the album has more distinct artists, a
   small chevron, then the names in accent ink on one ellipsized line.
9. **Face pile flyout**: clicking opens a 280-wide list of every distinct artist, rows 44 tall; Esc/light-dismiss closes.
   *(hover + click capture)*
10. **No meta line in the rail** — "N songs · duration · year" appears nowhere in the two-column arm.
11. **CTA cluster**: Play capsule 36 tall on the cover-derived accent, then a heart and a share circle at 40; at rail
    180 the two circles wrap to their own line **as a group**. *(drag the grip to the floor)*
12. **Play ink**: the Play label/glyph is WCAG-picked against the accent (dark ink on a pale accent, light on a dark one).
13. **Heart**: unsaved = outline in secondary ink; click → filled in the page accent, immediately. *(hover + click)*
14. **Share**: click copies and the glyph becomes a ✓ for ~1.6 s, then swaps back with a scale/blur icon swap.
    *(frame recording)*
15. **No `⋯` in the wide rail** for an album.
16. **About this release** appears only once every fact is in: Songs and Length side by side, Released full width below,
    then `Label: …`, then ℗/© lines, then the Other versions button when present.
17. **Tile width**: at rail 280 the two top tiles are 124 DIP each; drag the grip to 480 → 224 each; the Released tile
    always spans the full 256/456. *(two captures)*
18. **No reflow**: capture at t≈0 and t≈+2 s after opening an album with a label — identical tile rects in both.
19. **Note lines** are 11 px tertiary and wrap; a long copyright never ellipsizes to one line.
20. **Other versions** opens a menu whose items read "Name · Year · KIND"; choosing one navigates and the new page opens
    with its header already painted (nav preview). *(click capture)*
21. **Trailing band reserve**: on a cold open the area below the rows shows one shimmer block (header bar + 96 card +
    5 chips + 3 rows) from the first frame, and eases once into the real sections. *(frame recording of the open)*
22. **Section order** top to bottom: the music-video section (any release with ≥1 video — "Watch the official video"
    when there is exactly one, "Music videos" when there are more) → About the artist → Fans also like →
    More by <artist> → Featured on → Merch → Similar albums. Absent sections leave no gap.
23. **Section cap**: each list shows at most 5 rows with "Show all N" beside the header; clicking expands in place
    without navigating. *(click capture)*
24. **Trailing rows** are 64 tall with a 48 cover, a hover play FAB, and a plated card background with a hairline border.
25. **About-the-artist card**: 84 round portrait, "About the artist" eyebrow, 20/700 name (+ check glyph when verified),
    ≤2 bio lines, a Follow capsule on the right; the whole card navigates to the artist.
26. **Fans also like** chips are 48 tall pills with a 32 avatar, capped at 8, clipped (never wrapped).
27. **Merch** rows show the price in accent ink on the right; a merch entry with no shop url has no hover scale and no
    button role. *(hover capture)*
28. **Track lanes (album)**: `#`, heart, title, Plays, duration, then the film/`…` lane and the chevron — no art thumb,
    no Album column, no BPM·Key column. *(capture at 1280)*
29. **Top-track star**: the highest-play row shows a filled accent star in place of its number.
30. **Plays formatting**: `1.85B` / `11.8M` / `654.8K` / `999`; a row with no count shows an em dash, never `0`.
31. **Zebra + hover + press**: odd rows carry the zebra plate; hover lifts it; press deepens it; a hovered row reveals
    the transport in the `#` cell. *(hover capture)*
32. **Expand chevron** opens the versions drawer under exactly one row at a time, with the connector rail landing on the
    art centre and the drawer continuing the row's zebra parity and bottom corners. *(click + capture)*
33. **Drawer facts strip** leads with Plays · BPM · Key · Duration as big figures, then prose facts, then "Versions and
    formats".
34. **Reveal ramp**: record the open — rows fill in about 12 per frame and cross-fade; no single frame paints the whole
    band. *(frame recording; compare frame counts)*
35. **Mode crossings**: drag the window from 1400 → 400 slowly. Rail goes 280 → 224 (at page 820) → 188 (at 660) →
    vertical hero (below 540, back to two-column only at ≥580). The list never scrolls to the top on a crossing.
    *(frame recording)*
36. **Collapse detent**: drag the rail grip left past ~136 → the 96-DIP compact strip (cover 80, 2-line title, chevron);
    the track list keeps its scroll offset; pull right past 220 to restore. *(frame recording)*
37. **Vertical hero, row flow @ page 560**: art 215 beside the copy, title at the plan's size, a 20×2 accent rule under
    the title, the meta line present here (unlike the rail), actions Play + Shuffle + heart + share + `⋯`.
38. **Vertical hero, stacked @ page 400**: art 280 above the copy, padding 16.
39. **Sticky band**: scroll the vertical arm — the hero translates up and fades over its last 96 DIP, then a 56-DIP
    text-only band with the title + "N songs · duration · year" appears over the page's own tone, with one hairline under
    the column header, and the rows are clipped (not painted) at that line. *(frame recording)*
40. **Prerelease page** (`--fake` seeds an upcoming album; otherwise compare a live upcoming release on both builds):
    the countdown card shows a spinning ring, "Coming soon" in accent, and four tiles counting down every second.
41. **Pre-save**: the heart on a full prerelease saves the prerelease entity (toggling it and re-opening keeps the state)
    and falls back to the album when no link resolved.
42. **Waterfall album**: pending rows sit in position at 0.45 opacity, show `—` for plays and a short date ("4 Sep") in
    the duration lane, and refuse to play on click or double-click; the Songs tile reads "N of M".
43. **Countdown expiry**: with a target seconds away, watch it reach zero — the tiles are replaced by "Out now" and the
    ring stops, with no refetch. *(frame recording)*
44. **Compilation**: a compilation album shows a per-track artist subline in the title cell (and keeps the album lane
    set); a single (≤2 tracks) has no multi-select and, when its one video-bearing row is the only one, shows the
    "Watch the official video" hero card.
44b. **A full-length album with videos shows them.** Open a 10+-track album whose rows carry music videos (Wham!'s
    *Last Christmas* reissues, or any album where the film glyph lights in more than one row): the trailing band opens
    with a **"Music videos"** header, the count beside it, and **one card per video-bearing row in track order** —
    not one card, and not nothing. Cross-check the count against the film glyphs in the `Video` lane.
44d. **The multi arm is a HORIZONTAL SHELF and must never wrap.** Four videos lie in ONE row that scrolls sideways —
    the 2×2 block is the rejected design. Confirm all four of: (a) **edge fades** on both sides when the strip is
    scrolled off either end (the engine's `AutoEdgeFadeBand` feather, not a gradient painted over the cards);
    (b) **pips** at the trailing edge of the header showing the page position, and clicking one pages the strip;
    (c) chevrons, a pip and a touchpad fling each **rest on a page boundary** (`ShelfSnap.Page`) — never mid-card;
    (d) the strip **does not jump or reflow** as the stills decode — watch a cold open frame by frame; the cards are
    laid out measured, so the row's height is final before the first image arrives. *(capture at 1280 and at 720: the
    card count per page changes with width, the strip never wraps to a second row)*
44c. **Every thumbnail is the video's own still, and no two are the same.** On that same album, the cards must NOT all
    show the album sleeve. Compare each card's image with the same track's video row in its versions drawer (the `⌄`
    chevron) — they are the same 16:9 still. A row whose only video is a user-attached mp4 is the one allowed
    fallback to the song's art. *(capture)*
45. **Empty / unresolvable**: a `prerelease:` deep link that cannot resolve shows the shell with "Nothing here yet"
    rather than an error page.
46. **Album drawer on the artist page** (route `artist:` → discography): the drawer opens under the clicked row with a
    16×8 caret over that card, header 40 (26 play circle, 28 cover, title · meta, an open-album button), rows at a 32
    pitch, two track columns once the grid has ≥5 columns, and a "Show all N tracks" row past 12 (or 24). Re-opening the
    same album shows rows on the next frame with no shimmer. *(frame recording at 1500×900 and 1280×720)*
47. **Reduced motion** (Windows "Show animations" off): the release facts appear with no stagger, the drawer opens with
    no travel, hover/press scales are gone — and every rect is identical to the animated run. *(two captures)*
48. **Colour washes off** (Settings → Appearance): the page ground is neutral, the Play pill falls back to the system
    accent, and no other geometry changes.
49. **Theme flip** light/dark while the page is open: the tone plane, the rail layer, the card fills and the ink all
    follow without a remount (the cover keeps its texture).
50. **Zero-allocation scroll**: scroll the track list with `FG_FPS_LOG` on — the allocation counter stays at 0 for
    phases 6–13, matching 0.2.9. *(log comparison, not a capture)*
51. **Missing-fact tiles**: open an album whose label/© are known but whose durations are not — Songs and Length still
    occupy row 1 with Length reading an em dash, and Released still spans row 2. No tile is ever absent once the panel
    exists. *(capture)*
52. **Versions-only panel**: an album with other versions but no publishing facts shows the `♪ Other versions ▾` button
    with **no** "About this release" eyebrow and no tile grid above it.
53. **Reserved video row in the drawer**: expand a track that has a music video and record the open — the 76×43 slot and
    its two placeholder bars are present from the first frame of the reflow, and the real video row **patches** into
    them (the drawer height never steps twice). *(frame recording)*
54. **Drawer waveform**: only the drawer's own track carries the 64-bar strip; the video and alternate-audio rows do
    not, and their rows simply close that gap.
55. **Countdown `Bare`**: the artist page's release tone panel shows the four tiles with no plate, no ring and no
    "Coming soon" eyebrow, while the album rail's card shows all three. *(two captures)*
56. **Pending row with no live instant**: a not-yet-out row whose `AvailableAt` is absent shows an em dash in the
    duration lane (not a date, not `0:00`) and keeps the 0.45 title opacity.
57. **Drawer selection**: click a row inside an artist-page album drawer — the 3×16 accent pill appears, the selection
    command bar slides up inside the panel, Ctrl/Shift extend, and double-click plays. *(click capture)*
58. **Drawer caret**: open the drawer under the **first** and the **last** card of a row — the 16×8 wedge apexes on that
    card's centre in both cases and never overhangs the panel's rounded corner; its 1-px outline meets the panel's top
    stroke with no hairline cutting across the wedge. *(two captures)*
59. **Flow hysteresis (narrow arm)**: drag the window slowly across 424 → the hero goes stacked; drag back → it does not
    return to row flow until 424 again (and does not flip at 400–424). *(frame recording)*
60. **540–560 band**: drag the window so the page region sits at 550 — the page is still **two-column at rail 188**, not
    the vertical hero. *(capture)*
61. **Empty album is not "minified"**: an album whose rows have not landed shows shimmer rows and **no** notice strip;
    only a *complete* list holding unnamed rows produces the MinifiedAlbum verdict (and even then, only in the embedded
    library pane).
62. **Merch with no price** reads "Buy" in accent ink; merch with no shop url has no hover scale, no focus ring and no
    button role. *(hover + Tab capture)*
63. **A music-video card click WATCHES that video** — it does not merely start the song, and it never starts the album
    (the deliberate 0.3 reversal of 0.2.9, `DetailTrailing.cs:369`). Click a shelf cell on an album where the videos are
    not track 1 and confirm the deck lands on **that** song (kind 99 keys the video on its song, so the song is the
    playable) **and the video surface comes up with it** — not a beat of audio and then a switch, and not audio alone.
    Do it from a cold app with the surface never opened this session: `Requested` starts at `None`, which is precisely
    the state in which the shipped build played the song. Verify from **both** arms — the hero card on a single-video
    release and a cell on a ≥2 release — since they share one decision and must not diverge. The hero arm's eyebrow
    still reads the custom-video label when a track on that release carries a user-attached mp4.
63b. **The hero card's subtitle describes the VIDEO, not the release.** A single with one video reads
    "Music video · m:ss" under the video's title — never "N songs · M min · year", which is what 0.2.9 printed
    because the card was fed the album.
63c. **"Music video" and the availability verdict cannot be misread as one fact.** Expand a row that has a music video
    AND whose audio is region-locked: the flag line reads "Music video · **Audio unavailable**", not "Music video ·
    Unavailable". Neither flag implies the other — check all four combinations. *(capture)*
63d. **Clicking the card for the row already on the deck switches it to video IN PLACE** — the position is carried, the
    track does not restart, and a paused deck resumes. Clicking it again does **not** turn the surface back off: the
    card is an instruction, not the player bar's toggle.
63e. **With no placement able to host a video, the card says so and does not pretend.** Force the state (no rail room,
    no second window) and confirm the warning info bar (`player.videoUnavailable`, dedupe key `album.video.nohost`)
    appears and the song then plays — audio under a play badge with nothing said is the defect this arm prevents.
63f. **`UpgradeGate.DeferUpgrade` must not swallow the click.** It withholds a mid-track upgrade nobody asked for, so
    verify mid-track: start any track, let it run past the boundary, then click a video card and confirm the surface
    still comes up. The card commits through `FoldAvailability` + `OpenAt` rather than `PrimaryClick` for exactly this.
64. **Share failure**: with the clipboard unavailable the share button opens the url instead of showing a ✓; with a
    clipboard that throws, a toast appears and the glyph does **not** change. *(two runs)*
65. **Drawer empty + retry**: open an artist-page album drawer for a release whose tracklist resolves EMPTY — the panel
    holds two row pitches and shows "Nothing here yet" beside a **Retry button**, not a bare line. Clicking Retry
    re-fetches without closing the drawer. *(click capture)*
66. **Drawer shimmer shape**: open a drawer cold — each placeholder cell is a small 16×11 block plus one bar (never a
    heart, a time or a "…"), at the same 32 pitch and the same column split the real rows land in. *(frame recording)*
67. **Drawer reveal peek**: open a drawer on a card near the bottom of the viewport — the page scrolls just enough to
    show the header plus about two rows (≈104 DIP), not the whole panel. *(frame recording)*
68. **Fans also like has no "Show all"**: the chips row is clipped at the section's right edge and never wraps, and
    there is no link beside its header — unlike More by / Featured on / Merch / Similar albums, which all have one past
    5 rows. *(capture at 1280 and at 700)*
69. **Collapsed rail is two hit targets**: with the rail collapsed, clicking the 80-DIP cover AND clicking the chevron
    both re-expand; hovering the title between them does nothing and shows no hand cursor; the whole strip carries the
    full title as a tooltip. *(hover + two clicks)*
70. **Alternate-audio row has no play affordance at rest**: expand a track that carries an alternate recording — that
    row shows a plain square thumb with no play badge and no hover FAB; the only way to play it is its format button.
    The row still takes the subtle hover wash. *(hover capture)*
71. **Yearless album eyebrow**: open an album whose year is unknown. 0.2.9 reads "ALBUM · 0"; 0.3 must read "ALBUM"
    alone. *(this is the ONE parity item where 0.3 is deliberately allowed to differ — record the difference.)*
72. **Length tile vs meta line in a non-English locale**: set the app language to one with a translated
    `detail.durationMin`, then compare the rail's Length tile with the same album's vertical-hero meta line. 0.2.9
    spells the same duration two ways; 0.3 must spell it once. *(two captures, one per arm)*
73. **Embedded library album pane**: open an album inside the library master-detail pane — the rows are the scroller,
    there is **no** trailing band, **no** release panel and **no** countdown card below them, and a thin tracklist shows
    the Informational InfoBar there (and only there). *(capture)*
74. **Album has no BPM·Key lane at any width**: widen to 1600 — the album table still shows `#`/♥/Title/Plays/Duration/
    chrome and never a tempo column, and the Settings "Plays column" toggle does not remove the album's Plays lane.

---

## 11. Audit log

Adversarial pass against the 0.2.9 sources at HEAD `b3f6647a`. Every number in §2/§3/§5 was re-read from source; the
entries below are only the ones that moved. Unmarked numbers verified as written (`Spacing` M12/S8/L16/XL20/XXL24/XS4,
`Radii` Control 4 / Card 8, `WaveeSize.RailAlbum` 280, `MastheadStaggerMs` 45, `ScaleEmphatic` 1.07/0.92,
`ScaleSubtle` 1.02/0.98, `DetailRevealRamp` Chunk 12 / Cap 60, `DetailRailPolicy` 180/480/280, mode ladder
820/660/560/540/580 + 24 hysteresis, `ExpandedContentFadeDistance` 96, `CompactRevealBand` 44, `ContextBandLayout`
56 + 1, `StickyClipInset` 93, `RailForcePush` 44 / `RailReExpand` 220 / `RailCompactW` 96 / `GripStripCollapsedW` 20,
`RowArtFraction` 0.44 / 144 / 240, `ArtMin` 96 / `StackedArtMax` 280, `ContentWMax` 640 / `TitleWMax` 1000,
`PageTone` α 0.20 dark / 0.30 light, hero motion 280 ms SmoothOut, drawer 200/150 in and 150/100 out).

| # | Kind | Section | Correction |
|---|---|---|---|
| 1 | **wrong** | §0.5, §7 (`+N` row) | `+N` was described as "the distinct artists across every track". It is `allDistinct − billed.Count` — the track-only extras beyond the billed set (`ArtistFacePile.cs:53-54`). Added the degenerate no-billed arm (`all.Count − 4`). |
| 2 | **overclaim** | §4 (album-art row), §1.1 | "the 140-DIP narrow header cover uses 1.0" described `DetailRail.BuildHeader`, whose only call site (`DetailShell.cs:587`) is the **Show** page's narrow arm. An album never renders it. Re-scoped, with a §1.1 note and a §9 trap so it is not ported into `Album.Page.cs`. |
| 3 | **wrong** | §3 (Heart / Share FAB row), §5 | The heart and the share circle were merged into one row at "hover 1.07 / press 0.92". `SaveButton` is `ScaleEmphatic` (1.07/0.92); `PlaylistShareButton` is `ScaleSubtle` (1.02/0.98). Split into two rows plus a third for the inert-heart fallback (`DetailRail.cs:234`); §5's press/hover rows now carry all four rungs. |
| 4 | **wrong** | W7 | The trailing skeleton was drawn with ONE 160×18 header. `TrailingSkeleton` is three `SectionSkeleton`s, **each** with its own header bar (`DetailTrailing.cs:605-657`). Wireframe and prose corrected; chip radius 20, row fills and the 160×12 bar added. |
| 5 | **wrong** | W19 | "Show all 14 tracks" was implied to expand the drawer. It **navigates** to the album page (`ArtistPage.AlbumExpand.cs:271-282`). Corrected, with a §9 trap distinguishing it from `TrailingStack`'s in-place expand. |
| 6 | missing | §0.13, W5 | The stacked↔row-flow breakpoint has a 24-DIP hysteresis (`RowFlowEnterW` 424 / `RowFlowLeaveW` 400) that the chapter stated as a single 424. |
| 7 | missing | W3 | The 540–560 clamp-to-mode-2 (`DetailLayoutBreakpoints.cs:83, 86`), and the fact that modes 1/2 mount **no grip strip node** (`DetailShell.cs:791-793`). |
| 8 | missing | W5, W6 | The layout constants block (`HeroPad`/`NarrowPadW` 420 acting on colW independently of the flow arm, `HeroBottomPad` 8, `AccentRuleRowHeight` 4, `FallbackW` 580) and the three-rung `ArtworkDecodePx` ladder (256/512/1024 — only 512 reachable here). |
| 9 | missing | W11 | Three states of the facts grid: the em-dash fallback per tile, the tiles-less notes-only / versions-only panel (`HasTiles`, `HasReleasePanel`), and the exact Songs/Length/Released arithmetic incl. the sub-minute "1 min" floor and the null-on-unparseable-ISO rule. Plus `StatTile`'s `ZStack MinWidth 0` + `TextSwap` mechanism and its unused `trailing` slot. |
| 10 | missing | W12 | One-way expand; merch "Buy" price fallback and the no-url inert arm; the watch-video card playing the **album** and its custom-video label swap; `PersonPicture` (initials) vs `Surfaces.Artwork` (hashed plate) fallbacks; the verified check glyph (12, `TextSecondary`) and its empty-box placeholder; the `shortRelease` predicate; per-section `Safe(...)` fail-soft. |
| 11 | missing | W13 | The **`Bare`** render mode (no plate/ring/eyebrow); "Out now" at 15/700 `TextPrimary`; the un-padded days tile vs `D2` hrs/min/sec; the card's padding/gap values; first-render seeding of `_nowTicks`; `Breakdown`'s zero clamp. Pending-row precision: the 0.45 opacity is on the **title column only**, the duration lane falls back to an em dash with no future `AvailableAt`, and its ink drops to `TextTertiary`. |
| 12 | missing | W14 | The reserved-video-row mechanism (`PendingVersionRow`, the `v:video` key, 132×11 + 84×9 bars, inert); connector geometry (20/7/9, last-row elbow, `StrokeDividerDefault` and why); the self row's `FillSubtleSecondary` plate; version title 13.5/540 and the meta token run incl. the 6×6 Camelot swatch; the 22×22 white-α.92 badge over a 0.28 scrim; waveform = 64 bars, self row only; `FormatSplitButton`; drawer padding; the swallowed fetch failure; the full hero-four + prose fact order. |
| 13 | missing | W18 | An **empty** tracklist is `DetailNotice.None`, not MinifiedAlbum (`PlaylistPageNoticeRules.cs:86-93`); the InfoBar's severity/padding and the zero-height suppressed arm; the `reserving`/`albumSettled` terminator that stops a zero-track album shimmering forever. |
| 14 | missing | W19 | Drawer lane widths (26 / heart / star / 44 / 32); the 20-DIP two-column gap and the column-major split; the header's 26-circle `PickContrast` ink and `SpanTextEl` paragraph; the caret's **two** nodes (fill + 1-px polyline stroke), its apex clamp and its 1-DIP overlap; the full row interaction set (select / double-play / drag / context menu / swipe / selection pill / `SelectionCommandBar`); the ready-empty 2-row reserve; the shimmer path sharing `BuildColumns`. |
| 15 | missing | §3 | Rows added for the album-drawer lane set and the version-drawer connector; the `Play CTA` row gained the pressed-label alpha (0x80/0xB3 by ink luminance), the border ramp and `BackgroundSizing`. |
| 16 | missing | §5 | Rows added for the no-scale merch arm, `TrailingStack`'s transition-free expand, and the `#` cell transport's own press scale. |
| 17 | missing | §6 | The face-pile button's own fill ramp and `ConstrainToRootBounds: false`; the two disabled arms (flyout row with no uri, names run → `AutomationRole.Text`); the empty-box arm at `all.Count == 0`; the pile's self-fired `EnsureManyAsync(Identity)` portrait batch; the share button's throw→toast arm and the "Copied" announce; the `OwnerMenu` gate's full predicate; a note that `PreSaveButton` is the artist page's control, not this one's. |
| 18 | missing | §8 | Four untested pure rules the table did not list: `shortRelease`, `DetailFormat.ShortDate`, `PreReleaseCountdown.Breakdown`; plus `PreReleaseDerivation`'s wall-clock gating and its four extra wire/merge test files. |
| 19 | missing | §10 | 14 parity items (51–64) covering the states above: missing-fact tiles, versions-only panel, reserved video row, waveform scope, `Bare` countdown, dateless pending row, drawer selection, caret apex, flow hysteresis, the 540–560 band, empty-vs-minified, merch fallbacks, the watch-video click target, share failure. |
| 20 | verified | §8 | `PlaylistPageNoticeRulesTests` genuinely does not exist — `Wavee.Tests.csproj:137` names it in a comment beside the `<Compile Include>` for the rule, and no test file references the class. The chapter's claim stands. |
| 21 | verified | §9 | `DetailRail.BilledArtists` (`:503-556`) has exactly one occurrence in the tree — its own declaration — and `DetailConfig.AlbumColumns` is referenced only by the `DetailConfig.Album` literal that passes it, never by `DetailTracks`. Both dead-code claims stand. |
| 22 | verified | §6 | All four loc-drift claims confirmed against `assets/loc/en-US.json`: `detail.viewAllArtists:364`, `detail.collabView:368`, `detail.collabCount:367`, `detail.collabOpen:366`, `detail.factOfCount:360` all exist while the code concatenates or hard-codes. |

### Second adversarial pass

Independent re-read of the assigned 0.2.9 sources (`AlbumDrawerVerdict`, `AlbumReleaseFactsRules`,
`PreReleaseDerivation`, `CollaboratorFacePile`, `ArtistFacePile`, the album branches of `DetailConfig` / `DetailPage` /
`DetailRail` / `DetailShell` / `DetailTrailing` / `DetailTracks` / `ContextBand`, `TrackVersionsPanel`,
`PreReleaseCountdown`, `FlipCountdown`, `StatTile`, `SaveButton`, `TrackRow`, `MediaCard`, `WaveeCta`, `WaveeType`,
`WaveeMotion`, `DetailTrackTableRules`, `DetailLayoutBreakpoints`, `DetailRailPolicy`, `DetailRevealRamp`,
`ArtistPage.AlbumExpand`, `PlaylistInlineEdit`, `WaveeResourceDrag`, `Models.cs`, `assets/loc/en-US.json`, the engine's
`Spacing`/`Elevation`/`Expressive`/`MotionRecipes`, and the `Wavee.Tests` file list).

| # | Kind | Section | Correction |
|---|---|---|---|
| 23 | **wrong** | §3 (trailing media row) | "stack gap 4" — `MediaCard.Row`'s text stack is `Spacing.XXS` = **2** for a non-`large` row (`MediaCard.cs:1006-1007`; `Spacing.cs:11`). Only the `large` (112-DIP hero) arm uses 8. Corrected, and the row gained its real title/subtitle roles. |
| 24 | **overclaim** | §7 (eyebrow row) | "kind alone / year alone / '' are the three degraded forms" — on the ALBUM path none of them is reachable: `MapAlbum` writes `Year: a.Year.ToString()` over an `int` (`DetailPage.cs:711`, `Models.cs:294`), so a yearless album reads **"ALBUM · 0"**. Recorded as a 0.2.9 defect with a §9 trap and parity item 71; the degraded arms are Show's. |
| 25 | **overclaim** | §0.11, W12 | "Trailing sections are VERTICAL, capped at 5 rows, expandable in place" is true of only FOUR of the seven: Featured on / More by / Merch / Similar albums go through `TrailingStack`. **Fans also like is a plain clipped `Take(8)` chip row with no cap link** (`DetailTrailing.cs:121, 411-415, 540-547`); About-the-artist and Watch-video are single cards. Qualified in W12; parity item 68 added. |
| 26 | **wrong** | §0.5 | The `+N` rule was stated as complete. `FaceStack` draws `min(MaxVisible, billed.Count)` faces while the overflow is `all − billed.Count` in full (`ArtistFacePile.cs:55` vs `:111`), so a 6-artist billing hides two faces and counts neither. Recorded as a defect to fix rather than port. |
| 27 | missing | §1.1, §9, §7, W18 | **The embedded library-pane album arm.** `DetailTracks.cs:270-276, 701, 756` forces `HasTrailing = false` and `_verticalHeader = false`; because `DetailTrailing.cs:166-170` is the ONLY caller that asks for the Full rung, that pane structurally never has an "About this release" panel. Added to §1.1, §7 and §9. |
| 28 | missing | §7 | **Who asks for what.** Open = `HydrationLevel.Rich`; store refresh = `None`; **Full is a background ask fired by the trailing pane** with `TraitSurface.AlbumOpen`; the face pile fires its own `Identity` batch. Three independent demands, not one. |
| 29 | missing | §7 (pre-save row) | `PreReleaseUri` is adopted only when `link is { IsUpcoming: true }` (`DetailPage.cs:725`) — a kind-138 link is cached up to 30 days and must not resurrect a pre-save heart on a shipped record. The resolve gate (`IsPreRelease \|\| UpcomingAt is not null`, `:416-418`) was also uncited. |
| 30 | missing | W19, §3 | **Ready-empty carries a RETRY BUTTON** (`ArtistPage.AlbumExpand.cs:343-353`) — 13 px tertiary text + `Button.Standard(common.retry)`, padded 8/12/8/12 at gap 12. The chapter described it as "its own empty line". Also added: the shimmer cell's exact blocks (16×11 + grow×11 capped 240, r4, `FillSubtleSecondary`) and the `expandedRevealPeek` 104 / `expandedTopInset` 28 bring-into-view rule. |
| 31 | missing | W14 | Version ORDER is authored — self → kind-99 video → kind-98 audio (`:114-119`); **a resting alternate-audio row has no play affordance at all** (`Thumb` returns bare art; the row body has no `OnClick`); the panel's own fetch is a mount-time `UseEffect` keyed on the track uri (`:91-97`). |
| 32 | missing | W4, §3 | The compact strip's `Gap` 8, its `max(48, stripW−16)` cover floor, the chevron's `Icons.ChevronRight` at 14 in `TextSecondary` inside a `Radii.Control` box, the whole-strip `ToolTip.Wrap`, the two-hit-target structure — and that the COLLAPSED arm **does** mount a 20-DIP grip strip node (unlike modes 1/2). |
| 33 | missing | §3, §6, §8 | The album's full `DetailConfig` literal, including the two knobs set only by omission: `ShowTempo: false` (no BPM·Key lane ever) and `PlaysColumnOptIn: false` (the album's Plays lane is not the user-toggleable one, and that same knob is what scopes the top-track star and `SeedTrack`). Plus `ResolveConfig` (EP → plain `Album`) as a pure rule. |
| 34 | missing | §3 | The "Keep left-rail same size" setting: `DetailRailPolicy.ScopeFor` resolves every surface to `RailScope.Uniform`, so the album's 280 can be the width every detail page reads (`DetailRailPolicy.cs:29-34, 50`). |
| 35 | missing | §6, §8 | **The Length tile is hard-coded English.** `AlbumReleaseFactsRules.TotalTimeLiteral` (`:110-115`) vs `DetailFormat.TotalTime` (`DetailConfig.cs:264-269`) — the same arithmetic, one invariant and one localized, both on the same page (rail bento vs vertical meta line). Added to the loc-drift list, §8 and §9, with parity item 72. `FormatReleaseDate` is likewise `InvariantCulture` beside a `CurrentCulture` `ShortDate`. |
| 36 | missing | W13 | `ShortDate` converts to **local time** before comparing years (`DetailConfig.cs:241-244`) — the chapter only named the culture. |
| 37 | missing | W12, §3 | The watch-video card's `HoverFill` + `ClipToBounds` + absence of any press fill or scale, and its asymmetric section padding `(16,20,16,0)`; the artist chip's and media row's `PressedFill = FillSubtleTertiary`. |
| 38 | missing | §5, §6 | `StatTile.Create`'s optional `layout` override (`layout ?? DetailRail.Shove`); `MotionRecipes.TextSwap`'s channel is Opacity ONLY (Dy/Blur ride the enter/exit offsets); the heart's missing cursor/focus/tooltip vs the share FAB's Role+Focusable+cursor; the drag kind coming from `WaveeDragKindMap.OfUri` with a `Route` kind yielding **no** drag source. |
| 39 | missing | §1.1 | The five rail rows an album never mounts (`rail:owner`, `rail:meta`, `rail:daylist`, `rail:chart`, `rail:likedfacts`), named so a porter does not carry them into `Album.Page.Rail`. |
| 40 | verified | §3, §5, W3, W7, W11, W14, W19 | Re-measured from source and unchanged: `Spacing` XXS 2 / XS 4 / S 8 / M 12 / L 16 / XL 20 / XXL 24; `Radii.Control` 4 / `Card` 8; rail pad (16,24,8,24) gap 14; cover edge `railW − 24`; `CoverEdge` floor 80; `PillHeight` 36 + padding 18/6/18/7 + `ScaleStandard` 1.04/0.96 + on-fill alphas 0.90/0.80 + pressed-label 0x80/0xB3 by luminance; `ScaleEmphatic` 1.07/0.92, `ScaleSubtle` 1.02/0.98; `MastheadStaggerMs` 45; `Elevation.Card` dark 8/y2/#33 and light 4/y2/#1A; `Expressive.Fast` 250; `TextSwap` 150 EaseInOut ±4 blur 2; `IconSwap` 250 scale 0.25; share reset 1600 ms; mode ladder 820/660/560 with the 540/580 vertical band, the 24-DIP hysteresis and the clamp-to-2 at `:83, 86`; rail 180–480 and `ResizableMode` 0; `RailForcePush` 44 / `RailReExpand` 220 / `RailCompactW` 96 / `GripStripCollapsedW` 20; `titleSize` 40 at winH ≥ 900 else 28 and `descLines` 3 below winH 760; `DetailRevealRamp` Chunk 12 / Cap 60; lane widths `# 28 · ♥ 28 · Plays 52 · Duration 52 · Video 28 · Actions 40 · Expand 26` (Tempo 80, off here); countdown ring 34, tick 1000 ms, `Bare`, "Out now" 15/700; `StatTile` 18/800 over 11, gap 1, pad 12/8/12/8; trailing skeleton 3 × (160×18 header) + 96 card + 5 × 132×40 r20 + 3 × 64 rows; drawer geometry 40/32/8/8/12/5/3 and the 16×8 caret with its 1-DIP overlap; drawer motion 200/150 in and 150/100 out. |
| 41 | verified | §8, §9, §6 | `PlaylistPageNoticeRulesTests` still does not exist (the `Wavee.Tests` listing has `AlbumDrawerVerdictTests`, `AlbumReleaseFactsRulesTests`, `PreReleaseModelTests` + four wire/merge files, `TrackExpandedFactsTests`, `DetailVerticalLayoutTests`, `DetailLayoutBreakpointTests`, `DetailRailPolicyTests`, `DetailRevealRampTests`, `PlaylistListStateTests` — and nothing referencing `EyebrowText` / `SeedTrack` / `TopTrack` / `HasTrailingSections` / `shortRelease`). All four loc-drift keys re-confirmed at `en-US.json:360, 364, 366-368`. `DetailRail.BuildHeader` re-confirmed Show-only: `verticalTracks = mode == Vertical && Content == Tracks` and an album's `Content` is always `Tracks` (`DetailShell.cs:512, 587`). |

| 42 | **0.3 CHANGE (not a 0.2.9 audit finding)** | W12, §3, §8, §10 | **The music-video section is rewritten.** Reported on Wham!'s *Last Christmas* (3 tracks, 3 videos): 0.2.9 reduced the members to one bool, so three videos drew ONE card; that card drew the ALBUM's cover, title and meta line; clicking it played the ALBUM; and the whole section was gated on `shortRelease`, so a full-length album with videos surfaced nothing at all. 0.3: `PageRules.SelectVideos` yields one `AlbumVideo` per video-bearing member in track order at ANY length; the thumbnail is the VIDEO's own still by the drawer's ladder (`VideoImageId` ▸ counterpart art ▸ song art); the hero arm keeps 0.2.9's 200×116/44 geometry for the single-video case and a **horizontal `PagedShelf`** (edge fades, pips, page snap, measured) takes over at two or more; the hero subtitle is `detail.versions.musicVideo` · the video's duration. **Two further defects were found on the rewrite itself and are part of this row.** (i) The card played the SONG, not the video: the click was `PlayContext(member.Id)` alone, and the reducer only routes a row to the video host when the surface is wanted (`KindOfRow` = `videoWanted && (flags & VideoMask)`; `videoWanted` starts at `SurfacePlacement.None`), so a play badge over a video still started the ordinary song. `PageRules.WatchFor` now decides: request the surface through `Video.State.FoldAvailability` + `OpenAt` FIRST and play second; switch in place when the clicked row is already on the deck; say `player.videoUnavailable` when nothing can host a video rather than quietly playing audio. It deliberately does NOT route through `TogglePrimary`/`PrimaryClick`, whose `DeferUpgrade` arm would swallow an explicit click. (ii) The multi arm was a wrapping grid — four videos fell into a 2×2 block — and is now one horizontal shelf on the shared control. Separately, the drawer's flag run said "Music video · Unavailable", which reads as one fact — `detail.trackFacts.unavailable` now names its own subject ("Audio unavailable"). W12, the §3 geometry table, §8, the §10 comparison row 10 and parity items 22 / 44 / 44b / 44c / 44d / 63 / 63b / 63c / 63d / 63e / 63f all restate the new rules; `Wavee.Tests/AlbumVideoSectionTests.cs` pins them (`AlbumVideoArmTests`, `AlbumWatchTargetTests`). Ledger rows 10, 25 and 37 above describe what 0.2.9 did and stay as written. |

**Unverified / residual.** `WaveeMotion.Standard` (asserted as 250 ms in §4 for `BrushTransitionMs`) and the
`MotionRecipes.TextSwap` / `IconSwap` / `KeepFade` internals were taken from the chapter's own citations rather than
re-read — they are engine-owned and belong to ch. 00/18. The §2a worked title-plan arithmetic was not recomputed
line-by-line against `TitleTypeFor`; its inputs (chrome 92, 6 blocks / 5 gaps, `FluidTitleCapFor`'s straight line) were
confirmed to exist at the cited symbols. Section-2 wireframe *drawings* are schematic by construction; the numbers
beside them are what this pass checked.

*Still unverified after the second pass:* the §2a title-plan arithmetic and every `DetailVerticalLayout` constant were
taken from ch. 03's ownership rather than recomputed (the narrow arm is shared machinery); `CoverPaletteLeaves` /
`WaveePalette` values in §4 were not re-read (ch. 00 owns them); `TrackFactsStrip`'s own layout — the drawer's facts
strip above the versions list — is described only through `TrackExpandedFacts`' ORDER and belongs to **01-track-row.md**;
`ContextBandLayout`'s 56/36/1/93/44 numbers were confirmed only through `ContextBand`'s own references to them, not by
reading that file. `MediaCard.Row`'s hover-FAB reveal and `NowPlayingOverlay` are **02**'s.

**answers 2026-09-12: Q4 (plan §9.6) settles this chapter's own D8 gap — the album merch table is ADDED, not left
open.** `MerchTable` (`Name`, `Price`, `ImageId`, `ShopUrl`) + `EdgeTable<NoEdge> AlbumMerch` land in Wave 1 inside
`Edges.cs`, owner B, per this chapter's own D8 shape; §4.3's gap-18 row is updated to drop `AlbumMerch` from the
still-missing list. Nothing about the rendered surface (§1, §2 W12, parity items 27/62) changes.
