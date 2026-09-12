# Liked Songs page (generated covers, facts panel, heart, filters) — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Detail/LikedCoverArt.cs` (156) · `LikedCoverLeaves.cs` (299) · `LikedCoverPicker.cs` (272) ·
> `LikedCoverRules.cs` (307) · `LikedCoverTreatments.cs` (766) · `LikedFactsPanel.cs` (1756) · `LikedFactsRules.cs` (995) ·
> `LikedHeart.cs` (60) · `Components/LikedSongsArtwork.cs` (85) · `Components/ContentFilterChips.cs` (107) ·
> `Components/FacePiles.cs` (153) · `Wavee.Core/Library/ContentFilterTags.cs` (165) — **5 121 lines of surface**
> | 0.3 target: `Entities/User.Liked.cs` (CORE) · `Entities/User.Facts.cs` (CORE) · `Entities/User.Cover.cs` ·
> `Entities/User.Facts.UI.cs` · `Entities/User.Page.Liked.cs` — five of the settled eight-file `User.*` scheme (§9)
> | Wave 5 owner **O**

All paths are `src/apps/Wavee/...` unless they start with `docs/` or `src/apps/Wavee.Core/`. After Wave 0 they live at
`src/apps/_old/Wavee/...` with the same relative path. Shared parts are NOT respecified here: the rail/hero frame is
`03-detail-frame.md`, the track table is `04-detail-track-table.md`, rows and the row save button are `01-track-row.md`,
tokens/curves/palette are `00-design-system.md`. This chapter owns: the nine generated covers, the cover picker, the
facts bento, the lens header, the content-filter chips, and the Liked page's own configuration of the shared frame.

---

## 0. The non-negotiables

1. **The cover is the user's own music, composed live.** Nine treatments, all built from the newest liked covers on ONE
   304-DIP authoring canvas and scaled by `size / 304` (`LikedCoverTreatments.cs:40,111-117`). A 76-DIP flyout
   miniature is provably the same composition as the 304 cover behind it, not a second drawing.
2. **Nothing is ever half-composed.** Below a style's `MinTiles` floor the answer is the bundled PNG — never a
   half-empty grid, never a grey hole (`LikedCoverRules.cs:139-156`). The floors: Lens 4 · Wall 8 · Rainbow 8 ·
   Marquee 6 · Feature 4 · Mosaic 4 · Tone 1 · Stack 3 · Stock 0.
3. **Nothing ever waits on a colour.** Every palette leaf paints immediately from a neutral fallback and upgrades in
   place when `CoverColorPlane.Watch` answers — one repainted node, never sixteen rebuilt `ImageEl`s
   (`LikedCoverLeaves.cs:15-24,107-108,171-174,215-217`).
4. **Ink over imagery is `WaveeOnMedia`, never a theme token.** A Rainbow cover is every luminance at once, so the
   picker pill, the name chip and the badge are always a dark scrim with white ink
   (`LikedCoverPicker.cs:24-28,116,129-133`; `LikedCoverTreatments.cs:34-36,182-189,204-207`).
5. **Two ambient loops exist and they are *slow*.** Wall drifts −46 DIP over a 92 s there-and-back; Marquee's three
   bands travel exactly one repetition (888 DIP) in 60 / 74 / 66 s so they never phase-lock
   (`LikedCoverTreatments.cs:438-440,548-553`). Reduced motion is a zero-amplitude *keyframe array*, never an `if`
   (`:445,738-750`).
6. **The picker shows LIVE miniatures, not swatches.** Seven of the nine styles differ only in how the SAME art is
   arranged, which a swatch cannot show; the nine thumbnails are texture-cache hits at the same 64/128/256 decode
   buckets the cover already paid for (`LikedCoverPicker.cs:139-150`; `LikedCoverTreatments.cs:54`).
7. **A fact with no evidence is NOT MOUNTED.** No zeroed bars, no invented percentages, no disabled buttons
   (`LikedFactsPanel.cs:74-78,28-43`). The blend needs `ContentFilterTags.MinTrackCount` = 3 carriers
   (`ContentFilterTags.cs:51`); a distribution needs 10 known values to speak at all and 20 to earn a chart
   (`LikedFactsRules.cs:665,684-690`).
8. **Every fact is a LENS.** A spark bar, a face, a name row, a blend slice, a tail tick, a tempo band and a pill all
   write `TrackFilterState` and light up when the list is showing what they describe
   (`LikedFactsPanel.cs:296-308,612-618,960-966,1583-1589`). A second click clears exactly that facet.
9. **The lens header names what is on, per facet, with its own ×.** Never one "clear all"
   (`LikedFactsPanel.cs:1389-1441`), and it is a constant 36 DIP tall (`:1353`) because the vertical arm clips rows by
   a computed inset.
10. **The facts settle before they speak.** The panel computes nothing during a hydration storm: 250 ms of quiet after
    the last list change, then one summary, then one blur-reveal; a straggler republishes Ready→Ready with no shimmer,
    and a shape may only UPGRADE (Absent → Label → Graph), never fold back (`LikedFactsPanel.cs:117-146,148-154`;
    `LikedFactsRules.cs:868`).
11. **The cover's own heart is the app's heart.** `LikedHeart.Data` is `Icons.HeartFill`'s contour, character for
    character, interned once through `PathGeometryTable` (`LikedHeart.cs:28-43`) — so the Lens window, Tone's glass
    heart and the save button three inches below are one shape.
12. **The page ground follows the cover's lead tile.** When a treatment composes, the page tone is keyed to
    `Tiles[0]` — the newest like — not to whichever track happened to grade first (`DetailShell.cs:278-291,835-857`).
13. **The chips are one line that scrolls, never a wrapped block** (`ContentFilterChips.cs:42-46`), exclusive
    selection (All + at most one), and an un-evidenced curated chip is shown DISABLED rather than dropped
    (`ContentFilterChips.cs:57-62`; `ContentFilterTags.cs:107-137`). Evidence is a **prefix**, not a flag:
    `ContentFilterChipSet.IsEvidenced(i) => i < EvidencedCount` (`ContentFilterTags.cs:23`), so `OrderByEvidence`
    physically moves the evidenced chips to the FRONT and the disabled ones sit as one tail — the bar must never
    interleave live and dead chips.
14. **The Liked rail is unlayered.** Every other two-column detail rail recedes onto `Tok.FillLayerDefault`; Liked
    keeps the base surface, and that is the ONLY difference between the two arms (`DetailRail.cs:271-298`).
15. **Geometry freezes at mount and the Key says so.** Size/radius/morph are constructor fields on
    `LikedCoverArt`/`LikedCoverPicker` and all three are in the element `Key`, so a rail drag REMOUNTS instead of
    silently composing at the old edge (`LikedSongsArtwork.cs:37-41`; `LikedCoverPicker.cs:34-38`).

---

## 1. Anatomy

### 1.1 The 0.2.9 tree

```
DetailPage (route "liked")                                   DetailPage.cs:22
└─ DetailShell                                               DetailShell.cs:83
   ├─ tintBinder  CoverPaletteLeaves.ShellTint(paletteUrl)   DetailShell.cs:321-323   shell material tint leaf
   ├─ tonePlane   CoverPaletteLeaves.PageTonePlane(...)      DetailShell.cs:575-577   page ground; url = LikedToneAnchor
   │                                                          DetailShell.cs:846-857  (Tiles[0] when a treatment composes)
   └─ [mode 0/1/2: two-column]  row = [railFaded | grip | right]      DetailShell.cs:639-656, 779-793
      ├─ DetailRail.Build(m, DetailConfig.Liked, …)          DetailRail.cs:124
      │  ├─ "rail:cover"  BoxEl 216² (Elevation.Card, clip, Draggable=Hero)  DetailRail.cs:145-170
      │  │  └─ IF DetailRail.IsDynamicLikedCover(m)                       DetailRail.cs:108-118
      │  │     = IsLikedUri(ContextUri) **AND m.Cover is null**  (MapLiked sets Cover:null — DetailPage.cs:664)
      │  │     ELSE → DetailRail.HeroArtwork → LikedSongsArtwork.Cover (the bundled PNG)   DetailRail.cs:98-106
      │  │  └─ LikedCoverPicker.Cover(cover, Radii.Card, m.MorphKey)      DetailRail.cs:159 → LikedCoverPicker.cs:34
      │  │     ├─ LikedSongsArtwork.Dynamic(size,radius,morph)           LikedCoverPicker.cs:103 → LikedSongsArtwork.cs:37
      │  │     │  └─ LikedCoverArt (THE only component of the feature)   LikedCoverArt.cs:23
      │  │     │     ├─ site Stock      → LikedSongsArtwork.Cover(PNG)   LikedCoverArt.cs:135-136
      │  │     │     ├─ site MiniTone   → Treatments.Build(Tone, mini)   LikedCoverArt.cs:137-139
      │  │     │     ├─ site MiniMosaic → Surfaces.Mosaic(2×2)           LikedCoverArt.cs:140-141
      │  │     │     └─ site Treatment  → Treatments.Build(effective)    LikedCoverArt.cs:142-145
      │  │     │        └─ frame ZStack (clip+corners+MorphId)           LikedCoverTreatments.cs:92-104
      │  │     │           └─ Scaled(size) keyed "liked-style:"+style    LikedCoverTreatments.cs:103,111-117
      │  │     │              └─ one of nine canvases (§2 W5-W13)        LikedCoverTreatments.cs:78-90
      │  │     │                 ├─ Lens     : LensMosaic ▸ LensVeil(leaf) ▸ heart clip ▸ rim ▸ sheen   :262-303
      │  │     │                 ├─ Wall     : plate ▸ drift row(loop A) ▸ 2 vignettes ▸ chip           :453-506
      │  │     │                 ├─ Rainbow  : plate ▸ RainbowGrid(leaf) ▸ chip                         :414-426
      │  │     │                 ├─ Marquee  : PaletteGround(leaf) ▸ 3 bands(loops A/B/C) ▸ badge       :557-644
      │  │     │                 ├─ Feature  : 202+2×100 grid ▸ scrim ▸ chip                            :368-409
      │  │     │                 ├─ Mosaic   : 3×3 ▸ scrim ▸ chip                                       :357-363
      │  │     │                 ├─ Tone     : ToneGround(leaf) ▸ heart fill+rim ▸ count                :650-681
      │  │     │                 ├─ Stack    : PaletteGround(leaf) ▸ 5-card fan ▸ badge                 :699-729
      │  │     │                 └─ Stock    : the bundled PNG                                          :72-73
      │  │     └─ Pill "Cover style" (hover/focus/open reveal)           LikedCoverPicker.cs:109-136
      │  │        └─ (on click) Overlay → LikedCoverStyleFlyout          LikedCoverPicker.cs:70-82, :152
      │  │           ├─ WaveeType.Eyebrow("Cover style")                 LikedCoverPicker.cs:236
      │  │           ├─ WaveePicker.Strip(9, selected, Mini, Apply, 3)   LikedCoverPicker.cs:237
      │  │           │  └─ ×9  WaveePicker.Titled(Card(CoverMini, thumb76), name)   :190-219
      │  │           └─ note (12/16, TextTertiary, ≤3 lines)             LikedCoverPicker.cs:238-242
      │  ├─ "rail:title"   WaveeType.DetailHero (40/52 or 28/36)         DetailRail.cs:183-189
      │  ├─ "rail:meta"    "166 songs · 9 hr 51 min"                     DetailRail.cs:199-200, DetailPage.cs:659-670
      │  ├─ "rail:cta"     Play pill · SaveButton(collection uri) · Share · OwnerMenu    DetailRail.cs:212-240
      │  └─ "rail:likedfacts"  LikedFacts.Panel(m, h, outerPadding:false)  DetailRail.cs:259-260 → LikedFactsPanel.cs:58
      │     └─ LikedFactsPanel (Skel.Region: shimmer ⇄ cards)           LikedFactsPanel.cs:79,138-140
      │        ├─ ThisWeekCard   numeral + 12-bar SparkBars             LikedFactsPanel.cs:286-337
      │        ├─ YearCard       numeral + 12-bar SparkBars             LikedFactsPanel.cs:350-410
      │        ├─ TempoCard      numeral + DensityPlot + 4 bands        LikedFactsPanel.cs:1530-1625
      │        ├─ LikedArtistsCard  FacePiles + 5 name rows + "N more"  LikedFactsPanel.cs:563-836
      │        ├─ LikedBlendCard    bar + tail ticks + legend + body    LikedFactsPanel.cs:860-1292
      │        │  └─ BlendCollapseWatcher (mounted only while collapsing)  LikedFactsPanel.cs:1302-1322
      │        ├─ RediscoverCard    caption + "Play them"               LikedFactsPanel.cs:422-459
      │        ├─ FactPills.Row     years / tempo / blend label pills   LikedFactsPanel.cs:1634-1755
      │        └─ SinceLine         "Liking since … · mostly the 2010s" LikedFactsPanel.cs:466-518
      └─ right = TrackList (04-detail-track-table.md)
         └─ chrome: Toolbar ▸ ContentFilterBar ▸ LensHeader ▸ column header   DetailTracks.cs:1874-1896
            ├─ ContentFilterChips.Build(chips, filters.Tag, …)          DetailTracks.cs:2188-2206 → ContentFilterChips.cs:47
            └─ LikedLens.Header(filters, visibleCount, h, culture)      DetailTracks.cs:2215-2222 → LikedFactsPanel.cs:1401
   └─ [mode 3: vertical — page < 540, OR DetailPageLayout == Hero at ANY width]  DetailShell.cs:509-512
      TrackList virtual viewport
      ├─ slot 0  DetailVerticalHero  → LikedCoverPicker.Cover(artSize)  DetailVerticalHero.cs:127-128
      ├─ slot 1  pinned chrome (chips + lens header + column header)    DetailTracks.cs:1863-1873
      ├─ rows …
      └─ last slot  VerticalFactsFooter → LikedFacts.Panel             DetailTracks.cs:1762-1784
```

Other mount sites of the SAME cover component (no picker — a card is not where you change a collection's art):

| Site | Call | Size | What paints |
|---|---|---|---|
| Home / shelf card | `HomeCards.cs:150` → `LikedSongsArtwork.For` | card width | treatment ≥140, else 2×2 |
| **Every other card shape** | `MediaCard.ArtworkOrLiked` (`MediaCard.cs:172-178`) → `.For` | caller's w/h | the shared funnel — the uri decides, the caller's `cover` is never consulted |
| Media grid cell | `MediaCard.cs:242` → `.Fill` (Responsive, fallback 160) | measured | treatment ≥140, else 2×2 |
| Recents rail card | `MediaCard.cs:1132` → `.For` | `CardW−2·Pad` | treatment ≥140, else 2×2 |
| Sidebar row / pin | `SidebarCover.cs:84-91` → `.Dynamic` | 20–64 | always 2×2 or Stock. **NOT** the built-in "Liked Songs" NAV row: an authored `IconOverride` resolves first (`SidebarPaneSlot.LeadingArt`), so that row keeps its heart mark |
| Context-menu header | `Menus.cs:1152` → `.For` | 38 (radius 6, or 19 when the menu's art is circular) | 2×2 or Stock |
| Drag chip | `WaveeResourceDrag.cs:362` → `.Dynamic` (built once at type init) | 40 | 2×2 or Stock |
| Compact rail strip | `DetailRail.cs:313-321` → `.Dynamic` | 80 | 2×2 or Stock |
| Vertical hero (mode 3) | `DetailVerticalHero.cs:127-128` → `LikedCoverPicker.Cover` | `artSize` | treatment ≥140 (picker mounted, `morphKey: null`) |
| **Loading skeleton hero** | `DetailTracks.PreviewHeroArt` → `DetailRail.HeroArtwork` | hero | **always the stock PNG** — the skeleton deriver walks a STATIC tree and a hook-bearing component inside it is untested (`DetailRail.cs:108-118`) |

**The router is `m.Cover is null`, not the uri.** `DetailRail.IsDynamicLikedCover` is `IsLikedUri(ContextUri) && m.Cover is null`;
`DetailPage.MapLiked` sets `Cover: null` deliberately (`DetailPage.cs:664`). A 0.3 model that fills in a provider cover for
the liked collection silently turns the whole feature off — and the provider DOES serve one
(`misc.scdn.co/liked-songs/liked-songs-300.png`, `LikedSongsArtwork.cs:63-70`). Consequence to keep: on the very first
cold open the hero paints **stock → treatment** in one frame; that cross is honest, a blank hero would not be.

**A non-square slot is not letterboxed.** `LikedSongsArtwork.Fitted` (`:48-61`) composes the treatment at
`FitSide = max(w,h)` inside a clipping box that owns the corners, and hands the inner square `radius: 0` so the crop is
not notched twice; `IsSquare` tolerates half a DIP (`LikedCoverRules.SquareEpsilon`). `.Fill` (`:81-84`) is the
width-agnostic arm for a cell with no size to give — `Responsive.Of(w => Dynamic(w ≥ 1 ? w : 160, …), 160)`.

### 1.2 The same tree in 0.3 terms

Component props freeze at mount. The **only** channels that may carry change: a `Signal`/`Func` read inside the child's
own `Render`, `Ctx.Provide` + `UseContext`, or a `Key` remount. Marked below as (S)ignal / (C)ontext / (K)ey / (F)rozen.

| 0.2.9 node | 0.3 home | Kind | Inputs | Change channel |
|---|---|---|---|---|
| `LikedCoverRules` | `Entities/User.Liked.cs` **CORE** | static | pure | — |
| `LikedFactsRules` | `Entities/User.Facts.cs` **CORE** | static | pure | — |
| `LikedHeart` | `Entities/User.Cover.cs` (UI, top) | static field | — | F (interned once) |
| `LikedCoverTreatments` | `Entities/User.Cover.cs` (UI) | static builders | `(style, tiles, tileKey, trackCount, size, radius, mini, morphKey, loops)` | F — pure factory, no hooks |
| `LikedCoverLeaves` + 4 leaf components | `Entities/User.Cover.cs` (UI) | `Component` ×4 | `Props(urls…)` re-pushed | S (`Palette.Watch(imageId)`) |
| `LikedCoverArt` | `User.Cover.cs` `Liked.Cover` | `Component` | ctor `(size, radius, morphKey)` | **K** = `"liked-cover:"+size+":"+radius+":"+morph`; style via `Platform.LikedCoverStyle` (S); tiles via `Edges.Liked.Version[me]` (S) |
| `LikedCoverPicker` | `User.Cover.cs` `Liked.CoverWithPicker` | `Component` | same three | K, same spelling |
| `LikedCoverStyleFlyout` | `User.Cover.cs` | `Component` | — | S (style signal + liked edge version) |
| `LikedFacts.Has` / `.Panel` | `User.Facts.UI.cs` | static seam | `(User me, Handlers h, bool outerPadding)` | S — the panel reads `Edges.Liked.Version` itself |
| `LikedFactsPanel` | `User.Facts.UI.cs` | `Component` | `Props(rowsVersion, contextUri, handlers, outerPadding)` | props re-pushed (equality-gated on `rowsVersion`) |
| `LikedArtistsCard` / `LikedBlendCard` / `TempoCard` | `User.Facts.UI.cs` | `Component` ×3 | `Props(summary slice, handlers)` re-pushed | S (`Filters` signal read inside each Render) |
| `BlendCollapseWatcher` | `User.Facts.UI.cs` | `Component` | `Host`/`Shown` | S (`FrameClock.Tick`) |
| `LikedLens` header/pill | `User.Facts.UI.cs` | static | `(in TrackFilterState, int visible, Handlers, culture, liked)` | caller subscribes `Filters` |
| `ContentFilterChips` | `Platform/Controls.cs` (generic) + the chip SET on `User.Liked.cs` | static | `(chipSet, selected, select, allLabel, scrollKey)` | caller subscribes |
| `ContentFilterTags` | `Entities/User.Liked.cs` **CORE** | static | pure | — |
| `TrackFilterState` / `TrackFilterModel` | `Entities/Track.cs` **CORE** (shared, see 04) | pure | — | — |

**The one structural change the re-author must absorb:** in 0.2.9 every fact reads `Track.AddedAt` off the row record.
In 0.3 the save timestamp is *edge payload* — `LibraryEdge(int AddedAt, byte Flags)` on `Edges.Liked`
(plan §4.3, `wavee-0.3-implementation.md:318`). So `LikedFactsRules` is ported **verbatim in its arithmetic** but its
input type changes from `IReadOnlyList<Track>` to a parallel pair of spans, or a `readonly record struct LikedRow(Track
Track, int AddedAtSec)`. Do NOT "solve" this by adding an `AddedAt` column to `TrackTable`: a track can be in a
playlist and in Liked with two different stamps, which is exactly why the plan put it on the edge.

---

## 2. Wireframes

≈8 DIP per monospace character. Every dimension annotated is from the code, cited in §3.

### W1 — Liked page, two-column, rail 240 @ page ≥ 820 DIP (mode 0), fully loaded

```
│◀ rail 240 ───────────────────────────▶│▮│◀ right column (Grow) ─────────────────────────────────────▶│
┌───────────────────────────────────────┐ ┌──────────────────────────────────────────────────────────────┐
│ pad 16 L / 8 R, 24 top, gap 14        │ │ Toolbar  [▶ Play][⇄ Shuffle] │ [⇅ Sort][≡ Rows]   [Find  ⌕▽]│
│ ┌───────────────────────────────────┐ │ ├──────────────────────────────────────────────────────────────┤
│ │                                   │ │ │ (All) (Pop) (Dance) (Indie) (Hip Hop) (Rock) (Electronic) →  │  ← chips 32h, rail 40h
│ │        cover 216 × 216            │ │ ├──────────────────────────────────────────────────────────────┤
│ │  = CoverEdge(240) = 240−16−8      │ │ │ ( Liked Jul 27 – Aug 3  ✕ ) ( vaultboy ✕ )       42 songs    │  ← lens header 28h + 8 gap
│ │  radius 8 · Elevation.Card        │ │ ├──────────────────────────────────────────────────────────────┤
│ │  clip · draggable(Hero)           │ │ │  #   TITLE                     ALBUM        ♥   ⏱            │
│ │             ┌──────────────────┐  │ │ ├──────────────────────────────────────────────────────────────┤
│ │             │ ✎ Cover style    │  │ │ │  1  ▮ Song title              Album name    ♥  3:41          │
│ │             └──────────────────┘  │ │ │  2  ▮ Song title              Album name    ♡  4:02          │
│ │      pill 30h, margin 10/10 BR    │ │ │ …                                                            │
│ └───────────────────────────────────┘ │ │                                                              │
│ Liked Songs           40/52/600 (28/36│ │                                                              │
│                        below 900 winH)│ │                                                              │
│ 166 songs · 9 hr 51 min      12/16 sec│ │                                                              │
│ ┌────────┐ ╭──╮╭──╮╭──╮               │ │                                                              │
│ │ ▶ Play │ │♡ ││⤴ ││⋯ │  40 FABs      │ │                                                              │
│ └────────┘ ╰──╯╰──╯╰──╯  gap 12/8     │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ THIS WEEK             last 12 wks ║ │ │                                                              │
│ ║ +12       ▁▃▂▅▃▁▄▂▆▄▅█  spark 38h ║ │ │                                                              │
│ ║ songs liked                       ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ TEMPO                   96 – 174  ║ │ │  ← trailing is "96 – 174 bpm" (tempoRange) only when EVERY   │
│ ║ 128      ╭─╮  ╭──╮   ╭╮   38h plot║ │ │    row is known; partial coverage prints "142 of 166"        │
│ ║ bpm · median  80  120  160        ║ │ │    (tempoCoverage) instead — LikedFactsPanel.cs:1598-1600    │
│ ╚═══════════════════════════════════╝ │ │    numeral col 72 fixed; no rug (RugDotMax 0), no hairlines  │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ MOST LIKED                        ║ │ │                                                              │
│ ║ (◍)(◍)(◍)(◍)(◍)(◍)(◍)(◍)(+32)     ║ │ │  ← the pile's "+N" and the row's "35 more" are DIFFERENT      │
│ ║   ↑ face count is RESPONSIVE:     ║ │ │    numbers: pileExtra = ranked − faceCount (faceCount is      │
│ ║   VisibleFaces(measuredCardW−24)  ║ │ │    measured), nameExtra = ranked − 5 (LikedFactsPanel.cs:     │
│ ║   first (unmeasured) frame keeps 5║ │ │    602-610). Both open the SAME flyout.                       │
│ ║ TOTO · 8                          ║ │ │                                                              │
│ ║ Billy Idol · 6                    ║ │ │                                                              │
│ ║ Clouseau · 5                      ║ │ │                                                              │
│ ║ aespa · 5                         ║ │ │                                                              │
│ ║ Adele · 4                         ║ │ │                                                              │
│ ║ ▸ 35 more                         ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ YOUR BLEND              272 songs ║ │ │                                                              │
│ ║ ████████▓▓▓▓▒▒▒░░▏▏▏▎▏▏▏  8h r4   ║ │ │                                                              │
│ ║ ▪Pop 20% ▪K-Pop 13% ▪Soundtrack.. ║ │ │                                                              │
│ ║ ▸ 44 more  47 %                   ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ REDISCOVER                        ║ │ │                                                              │
│ ║ 14 songs you liked   [ Play them ]║ │ │                                                              │
│ ║ this week last year               ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ Liking since March 2019 · mostly the  │ │                                                              │
│ 2010s · oldest like Africa            │ │                                                              │
└───────────────────────────────────────┘ └──────────────────────────────────────────────────────────────┘
  rail scroller: Grow 1, Shrink 1, MinHeight 0, NO Fill (DetailRail.cs:276-286)
```

Card shell (every ╔╗ above): padding `12 / 10 / 12 / 11`, gap 6, radius 8, `Tok.FillCardDefault`, 1 DIP
`Tok.StrokeCardDefault`, `Elevation.Card` (`LikedFactsPanel.cs:527-535`). Stack gap `Spacing.S` = 8 (`:165`).

**The measured width is the CARD's own border box, quantised.** `UseMeasuredWidth(FacePiles.Step)`
(`LikedFactsPanel.cs:578`) reports this component's rendered root — the fact card including its 12/12 padding — rounded
to a 20-DIP grid (one face's `Step`), so the pile re-counts only when a whole face's worth of width appears and never on
sub-pixel wobble. `inner = measured − Spacing.M·2`, i.e. rail 240 → card 216 → 220 (grid) → inner 196 →
`SlotsIn` 9 → **8 portraits + "+N"**; rail 180 → 156 → 160 → inner 136 → 6 slots → 5 portraits (W24). The ranked list
itself is capped at `ArtistFlyoutCap` 40 (`LikedFactsRules.Summarize(…, artistCap)`), so "N more" never exceeds 35.

### W2 — Rail at the grip floor, railW 180 → cover 156 @ mode 0

```
┌─────────────────────┐   cover = max(80, 180−24) = 156  (DetailRail.cs:96)
│ ┌─────────────────┐ │   156 ≥ BadgeMinSize 140  → a full treatment
│ │  cover 156      │ │   156 <  ChromeMinSize 180 → the "Liked Songs" name chip is DROPPED
│ │  scale 0.513    │ │     (LikedCoverTreatments.cs:43-49, 75-76)
│ │        ┌──────┐ │ │   the heart BADGE survives (Marquee / Stack)
│ │        │✎ Cov…│ │ │   the pill's label ellipsises (MaxLines 1, CharacterEllipsis)
│ │        └──────┘ │ │
│ └─────────────────┘ │   name chip returns at railW ≥ 204 (cover ≥ 180)
│ Liked Songs         │   treatment floor is crossed at railW < 164 — unreachable: the
│ 166 songs · 9 hr 51 │   grip floor is DetailRailPolicy.MinWidth 180 (DetailRailPolicy.cs:25)
│ [▶ Play] ♡ ⤴ ⋯      │
│ ╔═════════════════╗ │   the facts cards are Basis 0 / MinWidth 0 wrap-grow tiles and survive 180
```

**The grip is not the only way to reach a narrow cover.** Modes 1 and 2 compose a FIXED rail and ignore the persisted
width entirely — `RailW(mode) = { 0: cfg.RailWidth (240), 1: 224, 2: 188 }` (`DetailShell.cs:192`), and
`DetailRailPolicy.ResizableFor` gives the grip to mode 0 only (`DetailRailPolicy.cs:20,28`). So the cover ladder is
walked three ways:

| page width | mode | railW | cover | chrome |
|---|---|---|---|---|
| ≥ 820 | 0 | 240 (drag 180–480) | 216 (80–456) | chip ≥ 204 railW; badge always |
| 660–819 | 1 | **224** | **200** | chip (200 ≥ 180) |
| 560–659 | 2 | **188** | **164** | **no chip** (164 < 180), badge only |
| < 540 enter / < 580 exit | 3 | — | the vertical HERO (art 144–240) | see W4 |

…and **a setting jumps straight to mode 3 at any width**: `WaveeSettings.DetailPageLayout == DetailVerticalLayout.PageHero`
forces `mode = Vertical` for every tracks surface (`DetailShell.cs:509-511`), so a user on the Hero layout never sees the
rail, the rail-mounted facts bento or W1 at all — the cover is the hero's, and the bento is the list's FOOTER (W4).
`PageAuto` is the default. (`DetailRail.BuildHeader`'s fixed 140-DIP header is the EPISODES arm only —
`verticalTracks = mode == Vertical && Content == Tracks` is always true on Liked, `DetailShell.cs:512,587` — so no Liked
cover is ever composed at exactly 140.)

### W3 — Rail collapsed: the compact identity strip @ 96 DIP

```
┌──────────┐  RailCompactW 96 (DetailShell.cs:204)
│ ┌──────┐ │  cover = max(48, 96−8−8) = 80  → below 140 ⇒ LikedCoverSite.MiniMosaic
│ │ ▣ ▣  │ │  = Surfaces.Mosaic(tiles, 80, 80, 8)  — the ordinary 2×2 collection mosaic
│ │ ▣ ▣  │ │  (LikedCoverArt.cs:140-141; DetailRail.cs:316-321)
│ └──────┘ │  NO picker here: the whole cover is the expand gesture
│ Liked    │  title 12/600, ≤2 lines
│ Songs    │
│    ⋮     │  spacer Grow 1
│ ┌──────┐ │  expand chevron 28h (Icons.ChevronRight 14, TextSecondary),
│ │  ›   │ │  radius Radii.Control 4, hover FillSubtleSecondary
│ └──────┘ │  strip pad 8/16/8/16, gap 8
└──────────┘  the WHOLE strip is ToolTip.Wrap(strip, m.Title)  (DetailRail.cs:353)
```

**The compact strip IS layered** — `Fill = Tok.FillLayerDefault` (`DetailRail.cs:344`), on Liked too. Non-negotiable
#14 ("the Liked rail is unlayered") is about `DetailRail.Build`'s two arms (`:276-298`) only; a 0.3 re-author who
generalises it to the collapsed strip loses the strip's separation from the track list.

### W4 — Vertical hero (mode 3, page < 540 enter / < 580 exit) @ 520 DIP, row flow

```
┌────────────────────────────────────────────────────────────────────┐
│ pad 24                                                             │
│ ┌────────────────────┐  gap 24  ┌───────────────────────────────┐  │
│ │                    │          │ Liked Songs                   │  │  title: measured type plan
│ │   art 197          │          │ ──  (20×2 accent rule)        │  │  (DetailVerticalLayout)
│ │   = round(clamp(   │          │ 166 songs · 9 hr 51 min       │  │
│ │     (520−48−24)    │          │ [ ▶ Play ](⇄)(⤴)(⋯)  NO ♡     │  │  satellites 32 (WaveeCta
│ │      ×0.44,144,240))          └───────────────────────────────┘  │  .IconButtonSize), gap 8
│ │        ┌────────┐  │   artSize 197 ≥ 180 ⇒ FULL chrome (name chip)│
│ │        │✎ Cover │  │   the picker IS mounted here (DetailVerticalHero.cs:127-128)
│ │        └────────┘  │
│ └────────────────────┘
├────────────────────────────────────────────────────────────────────┤ ← sticky band, clip inset
│ (All)(Pop)(Dance)(Indie)…                              chips 40h   │   = 56 + chrome + hairline
│ ( Liked Jul 27 – Aug 3 ✕ )              42 songs   lens 28h + 8    │   (DetailVerticalLayout.cs:590)
│  #  TITLE                       ALBUM        ♥   ⏱                 │
├────────────────────────────────────────────────────────────────────┤
│  rows …                                                            │
│                                                                    │
│  ── last row ──                                                    │
│                                                                    │  padding XXL 24 above and below
│  ╔══════════════════════════════════════════════════════════════╗  │  VerticalFactsFooter
│  ║ THIS WEEK / TEMPO / MOST LIKED / YOUR BLEND … (the same cards)║  │  (DetailTracks.cs:1762-1784)
│  ╚══════════════════════════════════════════════════════════════╝  │  aligned to TrackRow.PadXFor(tier)
└────────────────────────────────────────────────────────────────────┘
```

Three things this arm gets right that the two-column arm does not:

1. **No heart.** `DetailConfig.Liked` is `Heart: HeartMode.None` (`DetailConfig.cs:218`) and the vertical hero gates its
   `SaveButton` on `cfg.Heart != HeartMode.None` (`DetailVerticalHero.cs:248`), so the action row is
   **Play · Shuffle · Share · More** and nothing else. The two-column rail mounts the heart UNCONDITIONALLY
   (`DetailRail.cs:232-234`, as does the episodes-only `PlayRow` at `:493-495`), so the wide arm shows a heart that
   saves the *collection* uri while this arm shows none. Port the asymmetry as it is or decide it deliberately — do not
   "harmonise" it by accident.
2. **The artwork edge is its own function.** `DetailVerticalLayout.ArtworkFor(colW, rowFlow)` =
   `round(clamp(inner × 0.44, 144, 240))` in row flow, `round(clamp(colW − 2·pad, 96, 280))` when stacked
   (`DetailVerticalLayout.cs:133-143`), with `pad`/`gap` 24 above `NarrowPadW` 420 and 16/16 below it (`:118-129`).
3. **Row flow has its OWN breakpoint**, not the mode band: beside at `RowFlowEnterW` **424**, back to stacked below
   `RowFlowLeaveW` **400** (24 DIP of hysteresis, `:47-50`). The 540/580 pair in this wireframe's title is the
   *mode* switch (`DetailLayoutBreakpoints.cs:59-60`); the two are independent and both must be ported.

### W5 — Cover: **Lens** (the default) on its 304 canvas

```
┌────────────────────────────────────────┐  304 × 304 canvas
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  ground = the SAME 3×3 mosaic (cell 101.33, gap 0)
│ ▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │    per-tile: Saturation 1.25, ColorOverlay #000 α .38
│ ▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░▓▓▓▓▓▓▓▓▓ │    BakedBlur σ 22 (skipped in `mini`)
│ ▓▓▓▓▓░░░░░ CRISP MOSAIC ░░░░░░░░▓▓▓▓▓▓ │  veil: linear 70°, chromaA α .2475 → chromaB α .209
│ ▓▓▓▓░░░░ inside a heart clip ░░░░░▓▓▓▓ │  window: 231.04² centred (inset 36.48), ClipPath = LikedHeart
│ ▓▓▓▓░░░ 231.04² = 76 % of 304 ░░░░▓▓▓▓ │    inner positioner offsets −36.48/−36.48 and re-lays the
│ ▓▓▓▓▓░░░ saturation 1.12 ░░░░░░░░▓▓▓▓▓ │    FULL 304 mosaic, so ground and window align to the pixel
│ ▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░▓▓▓▓▓▓▓▓ │  rim: LikedHeart.Rim(231.04, white α .55, width .35 vb-units)
│ ▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░▓▓▓▓▓▓▓▓▓▓▓ │       a SIBLING ABOVE the clip (a stroke inside would be halved)
│ ▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  sheen: white .10 @0 → transparent @.45 → black .18 @1
│ ▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  NO name chip, NO badge (Lens asks for neither)
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  ZStack order is E7: ground UNDER the crisp window so the
└────────────────────────────────────────┘  async blur lands as a fade behind a fixed shape
```

### W6 — Cover: **Wall**

```
┌────────────────────────────────────────┐  plate #131318 (LikedCoverTreatments.cs:430)
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  grid: 6×6 = 36 cells, cell 80.30, gap 7, corners 3, decode 64
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  placed at (−103.36, −127.68), span 516.8 (−34 %, −42 %, 170 %)
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  roll −11° about its own centre; X-tilt DROPPED (2D affine only)
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  cell index = (i·5 + i%7) % tiles  → no two neighbours repeat at 8+
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  TWO nodes: positioner (static offset) / inner (roll + TranslateY)
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  vignette 1: radial @(.5,.42) r(1.2,1.2), #0A0A0E 0 @.46 → .62 @1
│ ╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱▣╱ │  vignette 2: vertical .28 @0 → 0 @.32 → 0 @.62 → .50 @1
│ ┌───────────┐                          │  chip (≥180): height 34, margin 14/14 bottom-left
│ │ ♥ Liked S…│                          │  mini: 4×4 (cell 123.95) and static
│ └───────────┘                          │
└────────────────────────────────────────┘
```

### W7 — Cover: **Rainbow**

```
┌────────────────────────────────────────┐  plate #131318 under a 4×4, cell 74.5, gap 2, decode 128
│ ▣▣ ▣▣ ▣▣ ▣▣   hue 04° 31° 58° 77°      │  cells = FillCells(tiles, 16)
│ ▣▣ ▣▣ ▣▣ ▣▣                            │  ORDER = RainbowOrder(hues): graded tiles sorted by hue ASC
│ ▣▣ ▣▣ ▣▣ ▣▣   ← row 1 REVERSED         │    (ties by original index), ungraded LAST in original order,
│ ▣▣ ▣▣ ▣▣ ▣▣     176° 150° 122° 99°     │    then every ODD row reversed → a serpentine sweep
│ ▣▣ ▣▣ ▣▣ ▣▣   198° 220° 250° 280°      │  hue read from scheme.BackgroundTintedBase (never TextBrightAccent)
│ ▣▣ ▣▣ ▣▣ ▣▣                            │  the GRID is a leaf: a late grading MOVES a tile, repainting one node
│ ▣▣ ▣▣ ▣▣ ▣▣   ← row 3 REVERSED         │  cells are positional ⇒ a reorder swaps textures, never re-requests
│ ┌───────────┐                          │
│ │ ♥ Liked S…│                          │  chip when ≥180
└────────────────────────────────────────┘
```

### W8 — Cover: **Marquee** (three bands, ambient)

```
┌────────────────────────────────────────┐  ground = PaletteGround(Marquee): #17171C lerp→BaseTint 0.40
│ ░░╱▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░░ │    + radial A α .88 @(.15,.12) r(1.184,.855) falloff .72
│ ░╱▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░ │    + radial B α .82 @(.88,.88) r(1.250,.987) falloff .72
│ ╱▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░ │  3 bands: tile 104, gap 7, rotation −16°, pivot = band-box centre
│ ▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░░ │    box: left −182.4, width 699.2, height 104
│ ░░╱▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░░ │    tops −20 / 95.5 / 211  (perpendicular pitch 111, seams 7.0)
│ ░╱▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░░ │    seeds 0 / 2 / 5 (pairwise gaps 2/3/5 < MinTiles 6)
│ ╱▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣╱░░░░░░░░░░ │    cells: tiles[(seed + i%8) % count], 16 live / 8 mini
│ ╭──╮ ▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣▣░░░░░░░░░░░ │  TranslateX on the INNER row only: ±888 = exactly one repetition
│ │♥ │ badge 40 bottom-LEFT, margin 14   │    ⇒ seamless wrap; durations 60 s / 74 s / 66 s, Easing.Linear
│ ╰──╯ shadow blur 14 dy 3 #00000059     │  badge shadow is media-fixed (theme-split tokens lose it on art)
└────────────────────────────────────────┘  ground shows ONLY at the top-left (24.9) and bottom-right (25.1) slivers
```

### W9 — Cover: **Feature**

```
┌────────────────────────────────────────┐  gap 2; cell 100; hero 202 (= 2·100 + 2)
│ ┌──────────────────────┐ ┌───┐         │  rows: [hero | column(cell,cell)] then [cell,cell,cell]
│ │                      │ │ 2 │         │  hero decodes at 256, followers at 128
│ │                      │ └───┘         │  cells = FillCells(tiles, 7)
│ │      1  (202²)       │ ┌───┐         │  scrim: transparent → transparent @.55 → black α .50 @1
│ │                      │ │ 3 │         │  name chip when ≥180
│ │                      │ └───┘         │
│ └──────────────────────┘               │
│ ┌───┐ ┌───┐ ┌───┐                      │
│ │ 4 │ │ 5 │ │ 6 │  100² each           │
│ └───┘ └───┘ └───┘                      │
│ ┌───────────┐                          │
│ │ ♥ Liked S…│                          │
│ └───────────┘                          │
└────────────────────────────────────────┘
```

### W10 — Cover: **Mosaic**

```
┌────────────────────────────────────────┐  3×3, cell 101.33, gap 0 (edge to edge), decode 128
│ ┌────────┬────────┬────────┐           │  cells = FillCells(tiles, 9)
│ │   1    │   2    │   3    │           │  scrim: transparent → transparent @.55 → black α .55 @1
│ ├────────┼────────┼────────┤           │  chip when ≥180: ♥ 16 + "Liked Songs" 12.5/16/600 on ScrimRest
│ │   4    │   5    │   6    │           │
│ ├────────┼────────┼────────┤           │
│ │   7    │   8    │   9    │           │
│ └────────┴────────┴────────┘           │
│ ┌───────────┐                          │
│ │ ♥ Liked S…│                          │
│ └───────────┘                          │
└────────────────────────────────────────┘
```

### W11 — Cover: **Tone** (no art tiles at all)

```
┌────────────────────────────────────────┐  plate = lerp(#151515, Chroma(tile0), 0.55); ungraded ⇒ #151515
│ ◌ .18/.20 r .558        ◌ .82/.25 .496 │  5 radial spots, α .55 → 0, radius = factor × .62:
│                                        │    (.18,.20,.558) (.82,.25,.496) (.25,.85,.527)
│             ♥ 188.48²                  │    (.80,.80,.465) (.50,.50,.372)
│          fill white α .92              │  urls CYCLE: tiles[i % count] — one liked cover is a legal
│          rim  white α .60 (w .3)       │    monochrome tone, five empty spots would be a hole
│                                        │  heart: real vector geometry at 62 % of the canvas
│ ◌ .25/.85 .527          ◌ .80/.80 .465 │  vignette: white .10 @0 → black 0 @.6 → black .28 @1
│                                        │  count (non-mini, >0): 12/16/600 white α .85, margin R14 B12
│                                166     │  ENGINE ADAPTATION: canvas 'lighter' (additive) has no equivalent;
└────────────────────────────────────────┘  spots are source-over at α .55 instead of .75–.9; grain absent
```

### W12 — Cover: **Stack**

```
┌────────────────────────────────────────┐  ground = PaletteGround(Stack): #1B1B20 lerp→BaseTint 0.45
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │    + radial A α .85 @(.20,.10) r(1.250,.855) falloff .70
│ ░░░░░░░░░╱▔▔▔╲  ╱▔▔▔╲  ╱▔▔▔╲░░░░░░░░░░ │    + radial B α .80 @(.90,.90) r(1.184,.921) falloff .70
│ ░░░░░╱▔▔▔│t[3]│ │t[2]│ │t[1]│▔▔▔╲░░░░░░ │  5 cards 150², left 77, top 66, origin 50 %/120 %
│ ░░░░│t[4]│    │ │    │ │    │t[0]│░░░░░ │  left→right = tiles[4],[3],[2],[1],[0]; rest poses (rot,dx,dy):
│ ░░░░│    │   │  │   │  │   │    │░░░░░░ │    −22/−26/+6 · −11/−12/−2 · 0/0/−6 · 11/12/−2 · 22/26/+6
│ ░░░░╲____╱___╲__╱___╲__╱___╲____╱░░░░░░ │  paint order oldest→newest: slot i shows tiles[4−i] ⇒ the NEWEST
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░╭──╮░░░░░░░░░ │    like is on top; each: radius 6, 1 DIP WaveeOnMedia.Stroke,
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░│♥ │░░░░░░░░░ │    Elevation.Card, decode 256
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░╰──╯░░░░░░░░░ │  badge 40 bottom-RIGHT (Elevation.Card, not the media shadow)
└────────────────────────────────────────┘  hover (whole cover): the fan opens — see M-11
```

### W13 — Cover: **Stock**, and the two mini sites

```
 Stock (the ladder's floor)     MiniMosaic (<140, ≥4 tiles)   MiniTone (<140, style == Tone)
┌──────────────────────┐      ┌──────────────────┐          ┌──────────────────┐
│  assets/covers/      │      │ ┌──────┬───────┐ │          │  gradient + ♥    │
│  liked-songs-300.png │      │ │  1   │   2   │ │          │  at mini: no     │
│  ImageTransition.None│      │ ├──────┼───────┤ │          │  count numeral   │
│  caller's size /     │      │ │  3   │   4   │ │          │  (trackCount is  │
│  radius / MorphId    │      │ └──────┴───────┘ │          │   suppressed)    │
└──────────────────────┘      └──────────────────┘          └──────────────────┘
 reached when: style==Stock, tiles==0, or (size<140 and style!=Tone and tiles<4)
```

### W14 — Cover hover: the picker pill revealed

```
┌───────────────────────────────────┐   OnHoverMove on the WHOLE cover (the pill must exist before the
│  the composed treatment           │   pointer can reach it) — LikedCoverPicker.cs:99-100
│                                   │   Opacity is a BOUND Prop: hovered ‖ focused ‖ open ? 1 : 0,
│                                   │   MotionTok.ControlNormal (250 ms, FluentStandard) — a compositor
│                ┌────────────────┐ │   cross-fade on ONE node, never a mount/unmount
│                │ ✎  Cover style │ │   pill: 30 h, pad 10 L / 12 R, gap 6, Radii.Full, margin 10/10
│                └────────────────┘ │     fill ScrimRest (#000 α .55) / hover 190 / pressed 220
└───────────────────────────────────┘     1 DIP border white α 58; ink WaveeOnMedia.Ink; label 12/16/600
                                          Icon = Icons.Brush (U+E790) at 14
```

### W15 — The cover-style flyout (open), anchored BottomEdgeAlignedLeft

```
        ┌───────────────────────────────────────────────┐  width = 3·92 + 2·12 + 16 + 16 = 332 (border-box)
        │ 16/15/16/17 content pad, gap 12               │  chrome (acrylic + stroke + elevation + reveal) is the
        │ COVER STYLE            eyebrow, TextPrimary   │  HOST's — PopupChrome.Flyout, FocusTrap, LightDismiss,
        │ ┌────────┐ ┌────────┐ ┌────────┐              │  ConstrainToRootBounds
        │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │  card 92 wide │
        │ │ ▨ 76²▨ │ │ ▨    ▨ │ │ ▨    ▨ │  inset 8      │  COLUMN-MAJOR (RadioButtons):
        │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │  gap 5        │    col 0 = Lens, Wall, Rainbow
        │ └────────┘ └────────┘ └────────┘               │    col 1 = Marquee, Feature, Mosaic
        │   Lens       Marquee     Tone                  │    col 2 = Tone, Stack, Stock
        │ ┌────────┐ ┌────────┐ ┌────────┐              │  column gap 12 (PartGrid), row gap 8 (RowSpacing)
        │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │              │  label 12/16 under the card: 600 + TextPrimary when
        │ └────────┘ └────────┘ └────────┘              │    checked, 400 + TextSecondary otherwise
        │   Wall       Feature     Stack                 │  checked card: 2 DIP AccentDefault border drawn INWARD
        │ ┌────────┐ ┌────────┐ ┌────────┐              │    (padding 8→7), fill AccentSubtle
        │ │ ▨▨▨▨▨▨ │ │ ▨▨▨▨▨▨ │ │  PNG   │              │  UNFED style (tiles < MinTiles): the miniature paints
        │ └────────┘ └────────┘ └────────┘              │    STOCK at Opacity 0.45 — still selectable (E1 deviation)
        │   Rainbow    Mosaic      Stock                 │
        │ Built from your latest likes — the cover       │  note: 12/16, TextTertiary, WrapWholeWords, ≤3 lines
        │ changes as you like songs. Stock keeps the     │
        │ fixed artwork.                                 │
        └───────────────────────────────────────────────┘
```

### W16 — The facts panel, SHIMMER (0 → 250 ms after the last list change)

```
╔═══════════════════════════════════╗   Skel.Region derives the shimmer from the cards' OWN shells
║ ▬▬▬▬▬                     ▬▬▬▬▬   ║     (LikedFactsPanel.cs:138-140, 237-282) — one UI, not two
║ ▬▬▬▬▬▬   ▁▃▁▅▂▁▄▂▆▃▅█             ║   heights [10,18,8,26,14,6,22,12,30,16,24,38], column gap 3, 38 h
║ ▬▬▬▬▬▬▬                           ║   bars 56×30 and 72×12, radius 3, Tok.FillControlDefault
╚═══════════════════════════════════╝
╔═══════════════════════════════════╗   6 face circles 28 ⌀ at margin −12, then 5 name bars
║ ▬▬▬▬                              ║     widths [120, 96, 132, 88, 150] × 12 h
║ (◍)(◍)(◍)(◍)(◍)(◍)                ║
║ ▬▬▬▬▬▬▬▬▬▬▬▬                      ║   Liked and playlists share the same three silhouettes;
║ ▬▬▬▬▬▬▬▬▬                         ║   Rediscover is too data-bound to fake honestly (`_ = liked;`)
╚═══════════════════════════════════╝
╔═══════════════════════════════════╗
║ ▬▬▬▬▬                     ▬▬▬▬    ║   blend: an 8-DIP r4 bar + three legend bars (64/52/70 × 12)
║ ███████████████████████████████   ║
║ ▬▬▬▬▬  ▬▬▬▬  ▬▬▬▬▬                ║   on Ready the real cards blur-reveal ONCE (SkelReveal.Soft)
╚═══════════════════════════════════╝
```

### W17 — The blend card, OPEN (the tail at full width)

```
╔═════════════════════════════════════════╗
║ YOUR BLEND                    272 songs ║  header count = the TAGGED population, not the library
║ ████████▓▓▓▓▓▒▒▒▒░░░▏▏▎▏▏▎▏▏▏▏▎▏▏▏▏▏▏▏  ║  bar 8 h, r 4, seg gap 2; slice alphas 1/.8/.6/.45/.3 of
║ ▪Pop 20% ▪K-Pop 13% ▪Soundtrack 10%     ║    AccentDefault; a LENSED slice goes to FULL accent
║ ▪Sad 5% ▪Indie Pop 5%   ▾ Show less 47% ║  tail: one tick per descriptor, grow = its count,
║ ─────────────────────────────────────── ║    MinWidth 2, gap 1, TextPrimary at α .16/.22/.30 cycled,
║ THE OTHER 44        128 songs · at full ║    clipped to its OWN box so the five named slices keep shape
║ ████████▓▓▓▓████▓▓▓▓████▓▓▓▓████▓▓▓▓██  ║  body: top pad 8, 1 DIP hairline (StrokeCardDefault), eyebrow,
║ ▪EDM 3% ▪R&B 3% ▪Nostalgia 3% ▪Chill 3% ║    the tail bar at FULL width (alphas .55/.38 alternating),
║ ▪Dance 2% … and 34 under 1 %            ║    legend rows ≥1 % + a count of the rest
╚═════════════════════════════════════════╝  every tail-bar % is "of the tail" and SAYS so; every legend
   host: Height 0 ⇄ NaN under SizeMode.Reflow    % is of the library (two different tooltip wordings)
```

### W18 — Lens header + chips in the list chrome (a week bar and an artist face both lit)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ (All)( Pop )( Dance )( Indie )( Hip Hop )( Rock )( Electronic ) →        │ chips: 32 h, pad 12, r 999,
│  ▲ selected = AccentDefault fill + TextOnAccentPrimary 13/600            │ 40 h rail + 8 bottom margin,
├──────────────────────────────────────────────────────────────────────────┤ one line, AutoEdgeFade,
│ ( Liked Jul 27 – Aug 3  ⓧ ) ( vaultboy  ⓧ ) ( 120 – 139 bpm ⓧ )  42 songs│ SuppressScrollBar, ScrollKey
│   pill 28 h · pad 12/3 · r 999 · AccentSubtle · 1 DIP AccentSecondary     │ per route
│   label 12/600 TextPrimary, ellipsis (the row never wraps)                │ header extent CONSTANT 36
│   ⓧ = 22² circle, ChromeClose 10, TextSecondary, own focus stop           │ (28 + 8) — the vertical arm
└──────────────────────────────────────────────────────────────────────────┘ clips rows by this number
   order: Week · Artist · Tag · Year · Tempo, then the visible count (TextTertiary)
```

### W19 — Empty library (0 liked tracks)

```
┌───────────────────────────────────────┐ ┌─────────────────────────────────────────┐
│ ┌───────────────────────────────────┐ │ │ Toolbar                                 │
│ │  the bundled PNG (Stock)          │ │ ├─────────────────────────────────────────┤
│ │  0 tiles ⇒ Effective == Stock     │ │ │                                         │
│ │  for EVERY style                  │ │ │                                         │
│ │  the pill still reveals on hover; │ │ │          Nothing here yet               │  14/TextTertiary
│ │  the flyout's nine miniatures     │ │ │          (detail.empty.noTracks)        │  centred, pad 16/24
│ │  are ALL the dimmed stock card    │ │ │                                         │
│ └───────────────────────────────────┘ │ │                                         │
│ Liked Songs                           │ └─────────────────────────────────────────┘
│ 0 songs · 1 min      ← TotalTime      │  NO chip bar (chips.Count == 0 ⇒ null)
│ [▶ Play] ♡ ⤴ ⋯                        │  NO lens header (no lens is on)
│                                       │  NO facts panel at all: LikedFacts.Has == false
│ (nothing below the CTA cluster)       │    (tracks.Count == 0 → :33-34)
└───────────────────────────────────────┘
```

### W20 — A very large library (10 000+ liked)

```
Identical to W1. What changes, and what must NOT:
 · Tiles() scans newest-first and STOPS at 16 distinct (LikedCoverRules.cs:116) — a 10k list costs a short prefix scan
 · TileKey = string.Join('\0', 16 urls) — the equality gate; a like that does not alter the newest 16 repaints NOTHING
 · the facts summary is ONE pass over the whole list, off the render path, 250 ms after the last change
 · the artists card ranks ALL credits but paints 5 names + N faces; the flyout caps at 40 (ArtistFlyoutCap)
 · the blend tail is unbounded (BlendOther(…, int.MaxValue)) — 40-plus ticks crowd into their 1-DIP gaps at 240 DIP,
   which is the stated trade: the "N more" button is how a 1-song descriptor becomes reachable
 · the spark strip always has exactly 12 bars; the year histogram always exactly 12 bins
```

### W21 — Card variants in the time slot (mutually exclusive)

```
 GRAPH (week)                        GRAPH (years)                      LABEL fallbacks (pills row)
╔═══════════════════════════════╗  ╔═══════════════════════════════╗  ┌─────────────────────────────┐
║ THIS WEEK        last 12 weeks║  ║ THE YEARS        2008 – 2024  ║  │ 🗓 mostly 2024  41 of 50    │ 26 h, r 13
║ +12      ▁▃▂▅▃▁▄▂▆▄▅█        ║  ║ 2016     ▁▂▃▅█▄▃▂▁▂▃▁        ║  │ ⏱ mostly 120 – 139 bpm      │ pad 8/10
║ songs liked   ▲ newest = full ║  ║ most tracks  ▲ peak = full ink║  │ 🏷 K-Pop  98 %              │ gap 6
╚═══════════════════════════════╝  ╚═══════════════════════════════╝  └─────────────────────────────┘
 mounted when WeekShape==Graph      else when YearsShape==Graph          FillCardDefault + StrokeCardDefault
 (adds≥3 AND ≥2 active buckets)     (see the Shape table below)          lit ⇒ AccentSubtle + AccentSecondary
 numeral: Ui.Title 28/36/600 in a ZStack keyed "v:<value>" with MotionRecipes.TextSwap
```

**The shape ladder decides which of the three forms each distribution takes** (`LikedFactsRules.Shape`, `:684-690`).
A fact is a GRAPH card, a LABEL pill, or ABSENT — and the panel's slot rules make week/years mutually exclusive:

| fact | Absent when | Label (pill) when | Graph (card) when | consts |
|---|---|---|---|---|
| Week | 0 adds in the 12-week window | adds < 3 **or** only one active bucket → becomes a since-line clause "last like Jul 12", never a pill | else | `MinWeekEvidence` 3 (`:891,895-907`) |
| Years | `known < 10` **or** `known/total < 0.60` | `known < 20` **or** `categories < 3` **or** `topShare ≥ 0.50` | else | `MinDecadeEvidence` 10, `MinCoverage` .60, `MinGraphEvidence` 20, `MinGraphCategories` 3, `YearsCap` .50 (`:465,665-673`) |
| Tempo | same two preconditions | `known < 20` **or** `bands < 2` **or** `topBand ≥ 0.70` | else **and** `Stats.Known > 0` | `MinTempoGraphCategories` 2, `TempoCap` .70 (`:669,675`); card gate `LikedFactsPanel.cs:201` |
| Blend | `AboveFloor == 0` (no descriptor with ≥ 3 carriers, or `Tags` never fetched) | `topShare ≥ 0.85` **or** `Flat` (`topShare < 0.15`) | else | `BlendCap` .85, `BlendFlat` .15 (`:676-679,860-863`) |

`Latch` (`:868`) only ever moves a fact UP this ladder while the page is open. `yearsPill` also fires when the week
card took the time slot and years are non-Absent (`LikedFactsPanel.cs:194-195`) — so the years fact is never lost,
only demoted.

### W22 — Hover / press / focus, everything in one frame

```
 spark column       name row            blend slice        tail tick        legend row / pill
 ┌───┐ hover fill   ┌──────────────┐    ┌────┐ hover ink   ┌┐ hover ink     ┌────────────┐
 │▁▁▁│ FillSubtle2  │ TOTO · 8     │    │████│ AccentText  ││ AccentText    │ ▪ Pop 20 % │
 │███│ lit fill     │ scale 1.02   │    └────┘ Primary     └┘ Primary       └────────────┘
 └───┘ AccentSubtle │ lit AccentSub│    tooltip @0 ms      tooltip @0 ms    scale 1.02 / 0.98
  bar ink: accent   └──────────────┘    focus inset 2      focus inset 2    focus inset 1
  at α .38, FULL when lit or newest/peak          all brush cross-fades: ControlFaster 83 ms FluentStandard
 FOCUS: every lens is a real tab stop; the picker pill is a tab stop and stays LIT while focused or open
 A LIT control hovers to AccentSecondary, not to the idle hover brush (SparkBars.cs:92; LikedFactsPanel.cs:698,1187;
 FacePiles.cs:106) — the lit wash must deepen, never be replaced by a neutral one.
```

### W23 — The states this surface can be IN that are not "loaded and rich"

```
 (a) UNAVAILABLE ARTIST            (b) NO PLAYER / NO CONTEXT      (c) BLEND WITH NO TAIL
 ┌──────────────────────────┐      ╔══════════════════════════╗    ╔════════════════════════════╗
 │ Various Artists · 12     │      ║ REDISCOVER               ║    ║ YOUR BLEND        272 songs║
 │ ArtistKey() == ""  ⇒     │      ║ 14 songs you liked this  ║    ║ ████▓▓▓▒▒░░                ║
 │  Role None, Focusable    │      ║ week last year           ║    ║ ▪Pop 20% ▪K-Pop 13% …      ║
 │  false, Cursor Arrow,    │      ║   (NO "Play them" button)║    ╚════════════════════════════╝
 │  NO hover plate, NO      │      ╚══════════════════════════╝     no tick strip, NO "N more"
 │  tooltip lens, NO face   │       svc.Player null OR contextUri    button at all — the card
 │  click (:650-656,689)    │       empty ⇒ the fact still reads     cannot be opened
 └──────────────────────────┘       (:437-450)                       (other ≤ 0.005 ‖ tail empty, :978)

 (d) TEMPO, PARTIAL COVERAGE       (e) NOTHING TO SAY AT ALL       (f) COLD OPEN, FIRST EVER
 ╔══════════════════════════╗       LikedFacts.Has == false ⇒       the hero paints the STOCK PNG
 ║ TEMPO          142 of 166║       the whole "rail:likedfacts"     (the skeleton walks a static
 ║ 128    ╭─╮ ╭──╮  ╭╮      ║       row is NOT ADDED. Not an empty  tree), then crosses to the
 ║ bpm · median             ║       panel, not a placeholder card.  treatment on the first real
 ╚══════════════════════════╝       (DetailRail.cs:259-260)         render (DetailRail.cs:108-118)

 (g) TEMPO, FIRST FRAME            (h) A FLAT BLEND'S PILL         (i) NO LIBRARY BRIDGE
 ╔══════════════════════════╗       ┌────────────────────────────┐   the rail's heart is not
 ║ TEMPO          96 – 174  ║       │ 🏷 12 styles, none over 15 %│   rendered at all: SaveButton
 ║ 128    ░░░░ ░░░░ ░░ ░░░  ║       └────────────────────────────┘   returns an empty box when
 ║ bpm ·  (bands only, no   ║        Role.None, Focusable false,     LibraryBridge is absent —
 ║        ridge/line/marker)║        HOVER AND PRESS PINNED TO the   a capability gate, not a
 ╚══════════════════════════╝        idle fill: a plain pill must    disabled control
  the plot is authored against       not animate (:1746-1750)        (SaveButton.cs:37)
  the MEASURED stage width,
  floored to a 4-DIP grid, so
  frame 1 draws the four band
  hit boxes and nothing else
  (DensityPlot.cs:107,114,120)
```

### W24 — The rail at its floor: what the artists card does at railW 180 (cover 156)

```
╔═══════════════════════════════╗   measured card box = 180 − 16 − 8 = 156 → 160 on the 20-DIP grid
║ MOST LIKED                    ║   inner = 160 − 12 − 12 = 136 (measured − Spacing.M·2)
║ (◍)(◍)(◍)(◍)(◍)(+35)          ║   FacePiles.SlotsIn(136) = 1 + floor((136−32)/20) = 6 slots
║ TOTO · 8                      ║   ⇒ VisibleFaces(136, 40) = min(5, 39) = 5 portraits + one "+35"
║ Billy Idol · 6                ║   the pile NEVER overflows: when anyone would clip, one slot
║ …                             ║   is reserved for the count (FacePiles.cs:47-53)
║ ▸ 35 more                     ║   the name rows ellipsise (MaxLines 1, CharacterEllipsis)
║                               ║   ranked is capped at 40, so BOTH counts top out at 35
║                               ║   the cards are Basis 0 / MinWidth 0 tiles and survive 180
╚═══════════════════════════════╝   the BLEND tail crowds: sub-floor ticks paint at the 2-DIP
                                    floor and eat their 1-DIP gap (LikedFactsPanel.cs:1093-1097)
```

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| rail root | width `railW` (Liked default 240) | pad `16/24/8/24`, gap 14 | — | — | **no Fill** (Liked only) | — | `DetailRail.cs:262-286` |
| rail cover box | `max(80, railW−24)` = 216 @240 | — | `Radii.Card` 8 | — | — | `Elevation.Card` (dark 0/2/8 #00000033, light 0/2/4 #0000001A) | `DetailRail.cs:145-162`, `Elevation.cs:18-21` |
| authoring canvas | 304² | — | — | — | — | scale = `size/304`, origin (0,0) | `LikedCoverTreatments.cs:40,111-117` |
| treatment frame | `size²` | — | `radius` (caller: 8) | — | — | `ClipToBounds`, `MorphId = morphKey` | `:92-104` |
| cell (generic) | `edge²` | — | 0 (3 Wall, 4 Marquee, 6 Stack) | — | `Surfaces.PlaceholderFor(url)` | decode 64 / 128 / 256 | `:54,131-141` |
| name chip | h 34 | pad `10/0/12/0`, gap 8, margin `14/0/0/14` | `Radii.Full` | 12.5/16/600 | fill `WaveeOnMedia.ScrimRest` (#000 α .55), border 1 white α 58/255, ink `WaveeOnMedia.Ink` | — | `:176-193` |
| heart badge | 40² | margin 14 | full circle | glyph 20 | fill `#FFFFFF`, glyph `#1B1B20` | `Elevation.Card`, or on art blur 14 / dy 3 / `#00000059` | `:199-215` |
| picker pill | h 30 | pad `10/0/12/0`, gap 6, margin `0/0/10/10` | `Radii.Full` | 12/16/600 | ScrimRest → hover `#000 α 190/255` → pressed `220/255`; border 1 white α 58 | bound `Opacity`, `MotionTok.ControlNormal` | `LikedCoverPicker.cs:109-136` |
| flyout body | w 332 | pad `16/15/16/17`, gap 12 | host | eyebrow = Caption 12/16/600, tracking +30 | title `Tok.TextPrimary`, note `Tok.TextTertiary` | `PopupChrome.Flyout` (host acrylic + stroke + elevation) | `:157-158,221-243` |
| picker card + label | card 92 × auto; the label sits UNDER it in a `Titled` column, gap `Spacing.S` 8, centred | inset 8 (7 when checked), gap 5 | `Radii.Card` 8 | label 12/16, 600 + `TextPrimary` when on, else 400 + `TextSecondary` | `Tok.FillCardDefault` → `AccentSubtle` when on; hover `FillCardSecondary` → `WaveeColors.SelectedHover` when on; pressed `FillSubtleSecondary` → `AccentSubtle`; border 1 `StrokeControlDefault` → 2 `AccentDefault`; `ClipToBounds` | hover/press 1.02 / 0.98; focus visual inset 2 | `WaveePicker.cs:51,66-94,225-244` |
| picker thumbnail | 76², radius 5 | — | 5 | — | `Opacity 0.45` when unfed; `HitTestVisible = false` | — | `LikedCoverPicker.cs:154-155,204-215` |
| picker strip grid | 3 columns, column-major | column gap `Spacing.M` 12 (PartGrid override), row gap `RadioButtons.RowSpacing` 8 | — | — | — | `Wrap = true`, columns `Shrink = 0` | `WaveePicker.cs:248-255,271-285`; `RadioButtons.cs:42-43,89-92,162-181` |
| treatment chrome matrix | — | — | — | — | **name chip** (≥180): Wall · Rainbow · Feature · Mosaic. **heart badge** (≥140): Marquee (bottom-LEFT, media shadow) · Stack (bottom-RIGHT, `Elevation.Card`). **neither**: Lens, Tone (Tone shows its own count numeral), Stock. **scrim**: Mosaic .55 · Feature .50 only | — | `LikedCoverTreatments.cs:75-90,357-426,453-506,632-643,725-729` |
| Tone count numeral | — | margin `0/0/14/12` | — | 12/16/600 | white α .85 | non-mini and `trackCount > 0` only; `CultureInfo.CurrentCulture` formatting (thousands separators are LOCAL) | `LikedCoverTreatments.cs:672-679` |
| facts stack | — | gap `Spacing.S` 8; pad `16/8/16/16` only when `outerPadding` | — | — | — | `Enter = DetailRail.FadeUp`, `Layout = DetailRail.Shove`, `Stagger = 45` | `LikedFactsPanel.cs:156-169` |
| fact card | — | pad `12/10/12/11`, gap 6 | `Radii.Card` 8 | — | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | `Elevation.Card` | `:527-535` |
| card head | — | gap `Spacing.S` 8 | — | eyebrow 12/16/600 +30 tracking | `Tok.TextTertiary` both halves | — | `:539-554` |
| big numeral | — | gap `Spacing.XS` 4 | — | `Ui.Title` 28/36/600 | `Tok.TextPrimary` | `MotionRecipes.TextSwap` in a ZStack | `:315-328` |
| spark strip | h 38, gap 3, min bar 3 | column corners 3 | bar corners `(2,2,1,1)` | — | ink `Tok.AccentDefault`, rest α .38, hover `AccentTextPrimary` | lit col `AccentSubtle`; hover `FillSubtleSecondary`, **`AccentSecondary` when lit**; pressed `FillSubtleTertiary`; focus 1; column is a full-height hit target | `SparkBars.cs:31-100` |
| density plot | h 38 + axis row (`AxisFontSize + 2` = 12) | numeral column 72 (fixed) | band corners 3 | axis 10 | ink AccentDefault, area α .22, line 1.25, marker `AccentTextPrimary α .45` (kit default is α .6 — the card overrides it), top margin 3 | **`RugDotMax = 0`** (no rug) and no hairlines — the rail's calm variant; geometry memoised on a tempo CONTENT fingerprint AND on the stage width floored to `WidthQuantum` 4, so the first (unmeasured) frame paints the four band hit boxes alone | `DensityPlot.cs:52-76,107,114,120,174,219`, `LikedFactsPanel.cs:1539-1554,1566-1576,1592-1595` |
| tempo band (hit) | h 38, `grow` = its share of 60–200 | — | 3 | — | lit `AccentSubtle`; hover lit → `AccentSecondary`, else `FillSubtleSecondary`; pressed `FillSubtleTertiary` | focus 1; tooltip `tempoBandTip` at 0 ms; the four ridge/line/marker layers above are hit-test-INVISIBLE so every pointer lands on a band | `DensityPlot.cs:161-192` |
| face pile | avatar 28, ring 2, outer 32, overlap 12, step 20 | — | circle | — | ring `Tok.FillSolidBase`, selected ring `AccentDefault`, hover ring `AccentSubtle`, **selected+hover `AccentSecondary`** | scale 1.04 / 0.96, focus 1; a face with no `OnClick` keeps every channel at rest (arrow cursor, no focus stop) | `FacePiles.cs:24-36,88-118` |
| face pile "+N" | outer 32, inner disc 28 | same overlap | circle | label **10 / 700** `TextSecondary` | frame `FillSolidBase`, inner disc `FillCardDefault`; a BUTTON only when `onOverflow` is passed — else it is a count, not a control (no hover, no focus stop, arrow cursor) | hover `FillSubtleSecondary`, pressed `FillSubtleTertiary`, scale 1.04 / 0.96; tooltip = `moreArtists` | `FacePiles.cs:120-151` |
| name row | — | pad `2/1/2/1`, gap 4 | 4 | Caption 12/16/600 | lit `AccentSubtle` + `AccentTextPrimary`; idle `TextPrimary`; count `TextTertiary`; hover lit `AccentSecondary` else `FillSubtleSecondary`; pressed `FillSubtleTertiary` | hover/press 1.02 / 0.98, 83 ms; focus 1; **a credit with no `ArtistKey` is `Role.None`, unfocusable, arrow cursor, no fills** | `LikedFactsPanel.cs:687-716` |
| "N more" row (artists) | — | pad `2/1/2/1`, gap 6 | 4 | Caption `TextSecondary` | hover `FillSubtleSecondary`, pressed `FillSubtleTertiary` | `SidebarChevron.Disclosure` + label; `Down`/`F4` opens | `:718-737` |
| artists flyout | 280 × ≤360; list 264 × ≤320 | pad 8, gap 8; rows h 44 pad `8/0/8/0`, gap 12 | rows 6 | Caption 600 | lit `AccentSubtle` + `AccentTextPrimary`; count `TextTertiary` | `PopupChrome.Popup`, `ConstrainToRootBounds: false`, `AutoEdgeFade`, `.Interactive(Interaction.Subtle)` per row; `PersonPicture` 32; eyebrow `allArtists` `TextTertiary` | `:739-795` |
| rediscover body | — | gap 8 | — | Caption `TextSecondary`, Wrap, ≤3 lines, ellipsis | — | `Button.Create(ButtonAppearance.Standard, ControlSize.Small)`, `Shrink 0` — **omitted entirely when there is no player or no context uri** | `:422-459` |
| blend bar | h 8, seg gap 2 | — | 4 | — | AccentDefault × [1, .8, .6, .45, .3]; a LENSED slice → full `AccentDefault` whatever its rank | clip; `HitTestPassThrough`; hit boxes are separate nodes (focus 2) | `:873-891,988,1004-1009,1063-1086` |
| blend tail tick | min width 2, gap 1 | — | — | — | `Tok.TextPrimary` α [.16,.22,.30] cycled; hover `AccentTextPrimary`; lit → `AccentDefault` | strip grows by `other`, clips its OWN overflow; tick `grow` = its raw COUNT | `:884-897,1099-1137` |
| legend entry | dot 8 (r 2) | pad `4/2/4/2`, gap 6; container `Wrap`, gap 4 | 4 | Caption 12/16 | label `TextSecondary`→`AccentTextPrimary`; share `TextTertiary`; lit fill `AccentSubtle` | hover `FillSubtleSecondary`, `AccentSecondary` when lit; pressed `FillSubtleTertiary`; focus 1 | `:1033-1034,1176-1200` |
| blend "N more" | — | pad `4/2/4/2`, gap 6 | 4 | Caption `TextSecondary` + share `TextTertiary` | hover `FillSubtleSecondary`, pressed `FillSubtleTertiary` | chevron + label flips "44 more" ⇄ "Show less"; the SHARE stays put so the legend does not reflow | `:1139-1171` |
| opened body | — | pad top 8, gap 8 | — | eyebrow + Caption | hairline 1 `Tok.StrokeCardDefault`; segs `TextPrimary` α [.55,.38] | `SizeMode.Reflow`, anchor Leading | `:902-929,1269-1291` |
| fact pill | h 26 | pad `8/0/10/0`, gap 6 | 13 | Caption; `lead` (`mostly`) `TextSecondary`, `strong` 600 `TextPrimary`, `trailing` `TextTertiary` — all three → `AccentTextPrimary` when lit | idle `FillCardDefault` + `StrokeCardDefault`; lit `AccentSubtle` + `AccentSecondary` | `Elevation.Card`; hover lit `AccentSecondary` else `FillSubtleSecondary`, pressed `FillSubtleTertiary` — **a non-live pill (the flat blend) pins hover AND pressed to its own idle fill and takes `Role.None` / `Focusable = false` / no cursor** | `:1724-1755` |
| pill glyph | 13 in a 24 view box | — | — | — | stroke 2.4 round/round, ink `TextTertiary`→`AccentTextPrimary` | — | `:1637-1639,1730-1734` |
| since line | — | pad `2/2/2/0` | — | Caption, ≤3 lines, `" · "` joins | `Tok.TextTertiary` | FadeUp + Shove | `:505-518,522` |
| lens pill | h 28 | pad `12/0/3/0`, gap 2 | 999 | 12/600 | `Tok.AccentSubtle` + 1 DIP `AccentSecondary`, label `TextPrimary` | FadeUp + Shove | `:1467-1483` |
| lens clear ⓧ | 22² | — | 11 | glyph 10 | `Icons.ChromeClose` `TextSecondary`, hover `FillSubtleSecondary` | scale 1.04 / 0.96 | `:1454-1465` |
| lens header row | h 28 (+8 margin = 36 fixed) | gap 8 | — | count Caption `TextTertiary` | — | FadeUp + Shove | `:1353,1433-1440` |
| content chip | h 32 (rail 40, extent `40 + Spacing.S` = 48) | pad `12/0/12/0`, gap 8; rail margin-bottom 8 | 999 | 13, 600 when selected | selected `AccentDefault` + **border `Transparent`** + `TextOnAccentPrimary`; idle `FillControlDefault` + `StrokeControlDefault` + `TextPrimary`; disabled `TextDisabled` | hover fill `FillControlSecondary` (selected: `AccentSecondary`), hover border `AccentDefault` — **but a SELECTED chip's hover border stays `Transparent`** (`:93`), so the pill never grows an edge under the pointer; scale 1.02 / 0.98, 167 ms `FluentDecelerate`; **`Shrink 0`** (the rail overflows, labels do not ellipsise); focus visual 2 | `ContentFilterChips.cs:35-40,79-106` |
| disabled chip | same box | same | same | 13/400 `TextDisabled` | `IsEnabled=false`, `Focusable=false`, `CursorId.Arrow`, no `OnClick` | hover fill/border pinned to the IDLE values and scale suppressed (`HoverIf`/`PressIf`) — a dead chip must not animate | `ContentFilterChips.cs:60-62,81-96` |
| "All" chip | same box | same | same | same | always available; selected when `selected is null` | first child, before every curated/derived chip | `ContentFilterChips.cs:52` |
| tooltip (all facts) | — | — | — | — | — | `showDelayMs = 0` (vs the service's 800) | `LikedFactsPanel.cs:1336-1348`, `ToolTip.cs:85-90` |

### 3.1 The loc keys this surface owns

Every string below is in `assets/loc/en-US.json`; ICU plurals are marked `‹p›`. A 0.3 re-author that renames a key
silently ships English into every locale — the generated `Strings.Detail.*` consts are the contract.

| group | keys |
|---|---|
| cover | `detail.likedSongs` (the chip's label too) · `detail.likedCover.pill` "Cover style" · `.title` · `.note` · `.style.{lens,wall,rainbow,marquee,feature,mosaic,tone,stack,stock}` |
| week card | `detail.likedFacts.thisWeek` · `.windowCaption` "last {count} weeks" · `.likedDelta` "+{count}" · `.songsLiked`‹p› / `.songsAdded`‹p› · `.weekTip`‹p› / `.weekTipAdded`‹p› |
| years card | `.theYears` · `.mostTracks` · `.yearRange` · `.yearTip`‹p› / `.yearTipOne`‹p› |
| tempo card | `.tempo` · `.bpmMedian` "bpm · median" · `.tempoRange` "{min} – {max} bpm" · `.tempoCoverage` "{known} of {total}" · `.tempoBandTip`‹p› · `.bandUnder90/.band90/.band120/.band140` · `.bandNameUnder90/.bandName90/.bandName120/.bandName140` (Slow/Medium/Fast/Very fast) |
| artists card | `.mostLiked` / `.topArtists` · `.artistTip`‹p› / `.artistTipAdded`‹p› · `.moreArtists`‹p› · `.allArtists` |
| blend card | `.blend` · `.shareTip`‹p› · `.moreTags`‹p› · `.showLess` · `.tailHeader` "The other {count}" · `.tailSongs`‹p› · `.tailShareTip`‹p› ("… of the tail") · `.underFloor`‹p› ("… and N descriptors **under 1 %**" — the string HARD-CODES `TailLegendFloor` 0.01) |
| rediscover | `.rediscover` · `.lastYear`‹p› · `.playThem` |
| since line | `.since` · `.collectingSince` · `.decade` "mostly the {decade}s" · `.oldest` / `.oldestAdd` / `.oldestTrack` / `.oldestTrackYear` · `.lastLike` / `.lastAdd` |
| pills | `.pillMostly` · `.pillCount` "{count} of {known}" · `.pillYearsTip`‹p› · `.pillTempoTip` · `.pillBlendFlat` "{count} styles, none over {share}" |
| lens header | `.weekRange` · `.lensWeek` "Liked {range}" / `.lensAdded` "Added {range}" · `.lensYear` / `.lensYears` · `.lensClear` "Clear this filter" · `Strings.Detail.SongCount`‹p› (the visible count) |
| chip bar | `detail.filter.allChip` "All" — **not** `.all` and **not** `.allTracks`: the bar's leading chip sits beside "Mellow" and "K-Pop", where the short word reads as one of the set; `allTracks` is the filter flyout's status SENTENCE (`DetailTracks.cs:2200-2204`). Every other chip label is server/descriptor data, never a loc key |

Glyphs: `Icons.Brush` (picker pill, 14) · `Icons.HeartFill` (name chip 16, badge 20, and the CTA heart at 16 in a 40
box) · `Icons.Heart` (the CTA heart when unsaved) · `Icons.ChromeClose` (lens ⓧ, 10) · `Icons.ChevronRight` at 10
through `SidebarChevron.Disclosure`, rotated 0 ↔ 90° (both "N more") · `Icons.ChevronRight` at 14 (the compact strip's
expand). The three fact-pill glyphs are inline 24-unit stroke paths parsed once at type init, NOT icon-font entries
(`LikedFactsPanel.cs:1637-1639`) — and unlike `LikedHeart` they go through a bare `PathDataParser.Parse` with a minted
epoch, which is safe ONLY because they are `static readonly` (a per-render parse would re-tessellate every frame).

---

## 4. Colour & material

### 4.1 The grading source

`SpotifyLive.CoverColorPlane` is image-keyed: `TryGetScheme(url, lightTheme)` returns five roles
(`BackgroundBase`, `BackgroundTintedBase`, `TextSubdued`, `TextBrightAccent`, …) and a miss **self-enqueues that image
for grading**; `Watch(url)` is the signal that turns the arrival into a repaint (`LikedCoverLeaves.cs:60-82`).
Every leaf subscribes; `LikedCoverArt` and `LikedCoverTreatments` never touch `Watch` at all.

| derivation | input → function | where applied | transition |
|---|---|---|---|
| `Chroma(url)` | scheme(**dark half in BOTH themes**) → `WaveePalette.Vivid(WaveePalette.Accent(scheme))` (`LikedCoverLeaves.cs:60-66`) | Marquee/Stack washes, Lens veil, Tone spots + plate | leaf re-render on `Watch`; the two radial CHILDREN appear (not a bound brush — a bound brush cannot make children appear, `:126-128`) |
| `BaseTint(url)` | scheme(dark) → `WaveePalette.TintedDark` = `BackgroundTintedBase` (`:68-75`) | the ground PLATE of Marquee/Stack | `ColorF.Lerp(plate, tint, 0.40 / 0.45)` |
| `HueOf(BackgroundTintedBase)` | `LikedCoverRules.HueOf(uint argb)` → `[0,360)` or null for any achromatic swatch (`LikedCoverRules.cs:282-300`) | Rainbow's ORDER | a late grading MOVES a tile into the ramp; ungraded tiles sit last in original order |
| `Surfaces.PlaceholderFor(url)` | `TryGetTint` → `Lerp(neutral, tint, 0.55)`; neutral `#2A2A2A` dark / `#F2F2F2` light (`Surfaces.cs:57-92`) | every cell while decoding | a still-decoding cell is that record's colour, never a grey hole |
| page tone | `LikedToneAnchor` → `Tiles[0]` when a treatment composes, gated on `CanGrade` (`DetailShell.cs:846-857`) → `WaveePalette.PageTone` (L forced .15 dark / .94 light, S capped .30 / .16) | the whole page ground | `CoverPaletteLeaves.PageTonePlane`, a leaf; alphas 0.20 dark / 0.30 light over Mica |
| shell material tint | same `paletteUrl` → `CoverPaletteLeaves.ShellTint` (`DetailShell.cs:321-323`) | the window material | claim/hand-over, never a clear |

**Theme invariance is the point.** A cover treatment is IMAGERY: it takes the provider's **dark-half** chroma (median
HSV S ≈ 0.73) in light theme too, because the light half would wash out under a dark plate while the ink over it
stayed white (`LikedCoverLeaves.cs:54-59`). The only theme-following colours anywhere in the cover are none.

### 4.2 The grounds, exactly

| ground | plate | wash A | wash B | file |
|---|---|---|---|---|
| Marquee | `#17171C` lerp→BaseTint **0.40** | α .88, centre (.15,.12), radius (1.184,.855), falloff .72 | α .82, centre (.88,.88), radius (1.250,.987), falloff .72 | `LikedCoverLeaves.cs:93,104-120` |
| Stack | `#1B1B20` lerp→BaseTint **0.45** | α .85, centre (.20,.10), radius (1.250,.855), falloff .70 | α .80, centre (.90,.90), radius (1.184,.921), falloff .70 | same |
| Tone | `lerp(WaveePalette.PageToneNeutralDark, Chroma(t0), 0.55)`, else the neutral alone — the token IS `#151515` (`WaveePalette.cs:215`), but port the TOKEN: Tone's plate and the page ground under it are deliberately the same neutral | 5 spots α .55→0, centres/radii in W11; a spot exists only for a GRADED tile, so an ungraded cover is the plate + vignette alone | — | `LikedCoverLeaves.cs:194-254` |
| Lens veil | — | linear 70° (CSS 160° − 90°), stop 0 = ChromaA α **0.2475**, stop 1 = ChromaB α **0.209** (0.45 × 0.55 and 0.38 × 0.55 folded once) | **ONE grading is enough**: stop 1 falls back to stop 0's colour, not to the neutral (`b = Chroma(B) ?? a`, `:174`) — mixing a graded colour into a neutral reads as a gradient that stops half way. Only when NEITHER is graded does the veil use `WaveePalette.PageToneNeutralDark` at the same alphas | `LikedCoverLeaves.cs:155-184` |
| Wall / Rainbow | flat `#131318` | — | — | `LikedCoverTreatments.cs:430` |

### 4.3 Scrims, dims and sheens

* Mosaic scrim bottom α **0.55**, Feature **0.50**; both are `transparent → transparent @0.55 → black @α` (`:166-173`).
* Lens ground dim: `ColorOverlay = #000 α 0.38` — exactly CSS `brightness(.62)` because source-over black at 0.38
  leaves 0.62·src (`:236-239`). Saturation 1.25 on the ground, 1.12 in the window; CSS `contrast(1.04)` is dropped
  (no engine control, not worth an offscreen layer).
* Lens sheen: white .10 @0 → white 0 @.45 → black .18 @1, painted LAST, over the rim (`:344-351`).
* Wall vignette: radial `#0A0A0E` 0 @.46 → .62 @1 at centre (.5,.42) radius (1.2,1.2); then vertical .28 → 0 @.32 →
  0 @.62 → .50 @1 (`:486-504`).
* Tone vignette: white .10 @0 → black 0 @.6 → black .28 @1 (`LikedCoverLeaves.cs:239-246`).

### 4.4 Stated engine adaptations (do NOT silently "fix" these in 0.3)

1. **No multiply blend.** Lens's veil is source-over at the folded alphas; the ground under it is already at 62 %
   brightness, so it reads as a cast rather than a wash (`LikedCoverLeaves.cs:144-154`).
2. **No additive blend.** Tone's five spots are source-over at α .55 instead of the prototype's .75–.9
   (`LikedCoverLeaves.cs:186-193,205`).
3. **No film grain** anywhere (no noise shader, no from-pixels image API) — the prototype's `.grain` layers are absent.
4. **No perspective.** Wall drops `rotateX(16deg)`; the −11° roll plus the drift carry the tilt
   (`LikedCoverTreatments.cs:447-452`).
5. **Per-tile blur, not per-layer.** `BakedBlur` is a per-image derivative, so Lens's ground keeps faint cell
   boundaries where the CSS blur bleeds across them — the trade for a persistent derived image on a page that also
   scrolls 10k rows (`:314-325`).
6. **The path clip is a HARD edge** (tier-3 stencil): the heart silhouette has no anti-aliasing of its own; the rim
   stroke is what dresses it, which is why the rim is a sibling ABOVE the clip (`:248-261`).
7. **No `PathEl` shadow.** Tone's `drop-shadow(0 10px 24px …)` under the glass heart is absent; the rim keeps the
   edge legible (`:660-666`).

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file |
|---|---|---|---|---|---|---|---|---|
| M-1 mount, style ∈ {Wall} | drift row node | `AnimChannel.TranslateY` | 0 → −46 → 0 | **92 000 ms**, `loop: true` | keyframe-default | — | amplitude 0 (`s_still`), row still owns its slab track | `LikedCoverTreatments.cs:440,742` |
| M-2 mount, style ∈ {Marquee} | band A / B / C inner rows | `TranslateX` | A,C: 0 → −888 · B: −888 → 0 | **60 000 / 74 000 / 66 000 ms**, loop | `Easing.Linear` | — | `s_still` (zero amplitude) | `:548-553,745-749` |
| M-3 style swap | the whole canvas | remount | — | — | — | — | — | `Key = "liked-style:"+style` (`:103`) forces a remount so a Wall drift track cannot tick on a Marquee band |
| M-4 cover hover / pill focus / flyout open | picker pill | `Opacity` (bound `Prop`) | 0 → 1 | 250 ms | `FluentStandard` (`MotionTok.ControlNormal`) | — | KeepFade | `LikedCoverPicker.cs:125-126` |
| M-5 flyout open / close | host popup | host chrome reveal | — | host | host | — | host | `PopupChrome.Flyout` (`:78-80`) |
| M-6 keyboard rove in the picker | the cover behind the flyout | full repaint | old style → new | one frame | — | — | — | `AppearancePrefs.Bump()` on every rove; roving IS the preview (`:180-188`) |
| M-7 facts settle | the facts region | shimmer → cards | shimmer → blur-reveal | `SkelReveal.Soft` | engine | after **250 ms** of list quiet | engine snaps | `LikedFactsPanel.cs:128-129,146` |
| M-8 card insert (a late fact) | one fact card | Opacity + FLIP | 0 → 1, old origin → new | `Expressive.Fast` 250 ms | `Easing.SmoothOut` | stagger **45 ms** × index down the stack | stagger → 0; fade kept | `:161-167`, `DetailRail.cs:52-57` |
| M-9 numeral change | `"v:<value>"` ZStack child | Opacity + Dy + Blur | in Dy +4 / out Dy −4, blur 2 | 150 ms | `EaseInOut` | — | KeepFade | `MotionRecipes.cs:229-233` |
| M-10 hover/press on any lens (bar, face, name, slice, tick, legend, pill, chip) | fill / border / scale | brush cross-fade + 1.02 / 0.98 (faces and ⓧ: 1.04 / 0.96) | — | **83 ms** | `FluentStandard` | — | KeepFade | `SparkBars.cs:80,93`, `LikedFactsPanel.cs:700-702` |
| M-11 hover anywhere on a **Stack** cover | the 5 fan cards | `WhileHover` OffsetX/OffsetY/Rotation **deltas** | (−5,−6,+2) … (+5,+6,+2) | 250 ms | `FluentStandard` | — | the token carries the policy — no `if` | `LikedCoverTreatments.cs:690-693,715-716` |
| M-12 "N more" blend disclosure open | body host | layout `Height` 0 → auto | `SizeMode.Reflow`, `SizeAnchor.Leading` | **333 ms** | `FluentPopOpen` (0,0,0,1) | — | token policy (SnapEnd) | `LikedFactsPanel.cs:906-911` |
| M-13 … close | body host | auto → 0 | Reflow | **167 ms** | `FluentDisclosureCollapse` (1,1,0,1) | body unmounts at settle via `BlendCollapseWatcher` | same | `:906-911,1302-1322` |
| M-14 opened tail bar | `"tail-bar"` | ScaleX + Opacity | 0.55 → 1, 0 → 1 | 333 ms | `FluentPopOpen` | — | — | `:917-920,1232-1233` |
| M-15 opened tail legend rows | each `"tlw:<tag>"` | Dy + Opacity | +4, 0 → 1 | 333 ms | `FluentPopOpen` | `WaveeEntrance.DelayMs(i)` = `clamp(i,0,8) × 40 ms` | 0 delay | `:926-929,1252` |
| M-16 disclosure chevron (blend "N more", artists "N more") | `SidebarChevron.Disclosure` | `AnimChannel.Rotation` | 0 ↔ 90° | **167 ms** | `FluentDisclosureChevron` (.167,.167,0,1) | — | token policy | `SidebarChevron.cs:46-73`, `MotionTok.cs:182` |
| M-17 lens pill insert / removal | `"lens:*"` pills and the header row | Opacity + FLIP | — | 250 ms | `SmoothOut` | — | KeepFade | `LikedFactsPanel.cs:1438,1473` |
| M-18 rail heart toggle | `SaveButton` | glyph swap + scale | `Heart` ↔ `HeartFill`, 1.07 / 0.92 | 83 ms brush | `FluentStandard` | — | KeepFade | `SaveButton.cs:40-48` |
| M-19 chip hover / press | content chip | fill + border + scale | 1.02 / 0.98 | **167 ms** | `FluentDecelerate` | — | KeepFade | `ContentFilterChips.cs:94-96` |
| M-20 page open | the whole page | route transition | owned by `ContentHost` | — | — | — | — | 18-shell-frame.md |
| M-21 hover on an all-artists flyout row | the row | `Interaction.Subtle` (kit recipe: fill + press scale) | — | kit | kit | — | kit | `LikedFactsPanel.cs:781`, `Interaction.cs` |
| M-22 chevron rotation (BOTH "N more"s) | `SidebarChevron.Disclosure` | reads `open` as a `Func<bool>`, NOT a frozen prop | — | 167 ms | `FluentDisclosureChevron` | — | token | `LikedFactsPanel.cs:731,1166`; `SidebarChevron.cs:46-73` |
| M-23 cover cold cross | rail hero | stock PNG → treatment | — | one frame, `ImageTransition.None` | — | — | — | `DetailRail.cs:108-118`; `LikedSongsArtwork.cs:21-25` |
| M-24 hover / press a picker card | the card | fill + border + scale | 1.02 / 0.98 (`WaveeMotion.ScaleSubtle`) | element default | element default | — | tier returns 1f | `WaveePicker.cs:63-82` |
| M-25 keyboard move inside the picker | the focus visual | `MoveFocusVisual` → the roved card | — | host | host | — | host | `RadioButtons.cs:113-119` |
| M-26 compact strip ⇄ full rail | the rail column | width + the strip's own content | — | owned by the shell's splitter detents (`RailForcePush` 44 / `RailReExpand` 220) | — | — | — | `DetailShell.cs:200-206` (03 owns the curve) |

**The ambient loops are seeded only at `size >= BadgeMinSize` (140)** — `hasLoops` in `LikedCoverArt.cs:107` ANDs the
style test with the size, and the deps key is `(effective style, TileKey, (int)size)` (`:121`), so a tile change
re-seeds and a mere repaint does not. Below 140 there is no treatment to loop anyway (the site ladder has already
collapsed to MiniMosaic / MiniTone / Stock), but the AND must survive the port or a 76-DIP picker miniature would
allocate three timeline rows each.

**All motion samples frame time.** The two ambient loops are engine keyframe tracks on the animation slab (they
quiesce by themselves under a parked page, `LikedCoverArt.cs:110-112`); `MotionTarget`/`While*` and every brush
cross-fade are engine-driven. **No `Environment.TickCount64` anywhere in this surface.** The one clock this feature
reads is `DateTimeOffset.Now`, read **once per panel render**, floored to the hour, for bucketing — not for animation
(`LikedFactsPanel.cs:103-108`; `LikedFactsRules.cs:84-97`).

---

## 6. Interaction

### 6.1 The cover and its picker

| gesture | result |
|---|---|
| hover anywhere on the cover | the "Cover style" pill cross-fades in (M-4). Hover is read on the WHOLE cover, not the pill (`LikedCoverPicker.cs:97-100`) |
| pointer leaves the cover | pill fades out — unless focused or the flyout is open (`:125`) |
| click the pill | opens the flyout, `FlyoutPlacement.BottomEdgeAlignedLeft`, `FocusTrap: true`, `LightDismiss`, `ConstrainToRootBounds: true` (`:70-80`) |
| click again / light dismiss / Esc | closes; `ClosedAction` clears `_open` so the pill can fade (`:82`) |
| `Down` or `F4` on the pill | opens the flyout — the app's one keyboard contract for an anchored surface (`:85-92`) |
| Space / Enter on the pill | arrives as `OnClick` |
| Tab into the flyout | ONE tab stop, landing on the checked card (`RadioButtons` roving) |
| ↑ / ↓ in the flyout | ±1 in DATA order (down a column); ← / → jump column to column at the same row (`RadioButtons.cs:121-144`) |
| any rove | **applies immediately** — selection follows focus; the setting is written and `AppearancePrefs.Bump()` fires, so the full-size cover behind the flyout repaints. Ctrl+arrow moves without applying (`LikedCoverPicker.cs:180-188`) |
| picking a style the library cannot feed | allowed, deliberately: the value persists, the cover paints Stock, and the treatment lights up by itself when the library reaches the floor (`:190-201`) |
| drag the cover | drags the whole entity — `WaveeDetailDrag.Hero`, on the FRAMING box so the picker keeps its own hit area (`DetailRail.cs:152`) |
| the cover at ≤80 DIP (compact strip, sidebar, chip, menu header) | no picker at all — the whole cover is the expand/navigate gesture (`DetailRail.cs:313-321`) |
| hover a **Stack** cover anywhere | the five fan cards open by the authored deltas (M-11). This is the ONE treatment with a hover response; the cover itself is `HitTestVisible = false` throughout (`Scaled`, `Canvas`, `Cell`), so the pill and the drag frame own every pointer event |

**Key spellings differ and both are load-bearing.** `LikedSongsArtwork.Dynamic` keys `"liked-cover:"+(int)size+":"+(int)radius+":"+morphKey`
(`LikedSongsArtwork.cs:40`); `LikedCoverPicker.Cover` keys `"liked-cover-pick:"+…` (`LikedCoverPicker.cs:37`) — a
DIFFERENT prefix, because the picker wraps the artwork and the two must not position-match onto each other when a page
switches arms. Note both cast to `int`: a measured 149.9997 and a 149.0 share a key, which is deliberate (a responsive
cell must not remount on sub-pixel wobble) and is why `LikedCoverRules.SquareEpsilon` is 0.5.

Accessibility names: the pill is `AutomationRole.Button` + `Focusable` with the visible label `detail.likedCover.pill`
("Cover style") as its content; each picker card is a `RadioButton` item whose content is the miniature and whose
label is `LikedCoverRules.NameKey(style)`.

### 6.2 The facts, as lenses

| affordance | click | second click | tooltip (loc key) |
|---|---|---|---|
| spark bar (week) | `WithAddedWindow(after, before)` from the bar's OWN bucket | `WithAddedWindow(0,0)` | `detail.likedFacts.weekTip` / `weekTipAdded` |
| spark bar (years) | `WithReleaseYear(min, max)` | `WithReleaseYear(0,0)` | `yearTip` / `yearTipOne` |
| tempo band | `Tempo = band` | `Tempo = Any` | `tempoBandTip` |
| face in the pile | `WithArtist(key, name)` | `WithArtist(null)` | `artistTip` / `artistTipAdded` |
| name row | same as the face — the two are ONE affordance | same | same |
| "N more" (artists) — the row OR the pile's "+N" frame | opens the all-artists flyout (`PopupChrome.Popup`, `FocusTrap`, `LightDismiss`, `ConstrainToRootBounds:false`), anchored `BottomEdgeAlignedLeft` on the PILE, not the row (`:670,630-632`). `Down`/`F4` opens it from the row too (`:636-641`) | closes | `moreArtists` |
| a row in that flyout | sets the artist lens **and closes** | — | — |
| an artist credit with no uri, id **or** name (`ArtistKey == ""`) | not a control at all: `Role.None`, `Focusable = false`, arrow cursor, no hover plate, no click — the row still COUNTS, it just cannot lens (`:650-652,689-703`) | — | still tooltipped |
| blend slice / tail tick / legend row | `Tag = title` (the SAME facet the chip bar writes) | `Tag = null` | `shareTip`, `tailShareTip` |
| "N more · 47 %" | opens the card in place | "Show less" | — (the label IS the accessible name) |
| a fact pill | applies its named filter | clears it | `pillYearsTip` / `pillTempoTip` / `shareTip` |
| a flat-blend pill | nothing — it is the ONE plain, non-button pill | — | `pillBlendFlat` |
| "Play them" (Rediscover) | `PlayOrderedAsync(contextUri, thisWindow, 0)` — the subset seam, so a remote device gets THESE tracks in THIS order. The `PlaybackContextTrack[]` is built ON THE CLICK, never per render. **No player or no context uri ⇒ NO BUTTON**; the caption still reads on its own rather than offering a dead control (`:437-450`) | — | — |
| lens pill ⓧ | `ClearLens(filter, lens)` — retires exactly one facet | — | `lensClear` |
| content chip | exclusive select; re-tapping the active chip clears it | — | — |
| un-evidenced chip | `IsEnabled = false`, `Focusable = false`, arrow cursor, `TextDisabled` | — | — |

Every lens control reads the **live** filter with `Peek()` inside its handler, never this render's snapshot — the
filter can have moved (the header's ×, the flyout's Clear all) between the render that drew the control and the click
(`LikedFactsPanel.cs:300,614,964`).

Tooltips on this surface open at **0 ms**, not the service's 800: a spark column is ~10 DIP wide and carries no label,
so the bubble IS the label, and a pointer sweeping the strip cancels an 800 ms countdown twelve times
(`LikedFactsPanel.cs:1336-1348`).

Focus visuals: `FocusVisualMargin` 1 DIP on rows/pills/bars, 2 DIP on blend slices and ticks (their painted node is
8 DIP tall), 2 DIP inside picker cards (`WaveePicker.cs:238-245`).

No right-click menu is owned by this surface — the cover's context menu is the page's (see 03), the rows' is 01.

---

## 7. Data & readiness in 0.3 terms

`me` = `User.Me` (`plan §4.14`). `L = Edges.Liked` (parent = the user slot), payload `LibraryEdge(AddedAt, Flags)`.

| visual element | 0.2.9 source | 0.3 read | readiness predicate (skeleton/absent until) |
|---|---|---|---|
| cover tiles | `LibraryStore.Liked` → `Track.Image.Url`, newest-first | `L.Targets(me)` prefix → `Tracks.Image[slot]` + `Tracks.Album[slot]` | `L.State[me] != 0` **and** the first ≥16 rows `Knows(TrackFields.Image \| Album)`. Fewer distinct tiles than `MinTiles` ⇒ Stock — never a half grid |
| tile dedupe | `Track.Album.Uri` + url | `Albums.Uri[Tracks.Album[slot]]` + `Tracks.Image[slot]` (both `StringId`, compare by id) | same |
| cover palette | `CoverColorPlane.TryGetScheme(url, dark)` | **GAP** — see below | never blocks: neutral plate, upgrade in place |
| track count on Tone | `tracks.Count` | `L.Length[me]` (and `L.Total[me]` when partial) | `L.State[me] == complete`; a partial count printed on the cover is a lie |
| meta line "166 songs · 9 hr 51 min" | `tracks.Count`, `Σ DurationMs` | `L.Length[me]`, `Σ Tracks.DurationMs` over `L.Targets(me)` | `L.State == complete` **and** every row `Knows(Duration)` |
| week spark / since / rediscover / save decade | `Track.AddedAt` | `L.Payload(me)[i].AddedAt` (seconds since epoch) | `L.State == complete`. **Not a Known bit — an edge-payload field** |
| artists card | `Track.Artists` (every credit) | `Edges.TrackArtists.Targets(slot)` → `Artists.Name/Image` | every row `Knows(TrackFields.Artists)`; portraits need `ArtistFields.Identity` (the pile shows initials until then) |
| blend / chips | `Track.Tags` (primary = `Tags[0]`) | `Edges.TrackTags.Payload(slot)` (order = server descending weight) | every row `Knows(TrackFields.Tags)`; **null ≠ empty** — "not fetched" must never be presented as "no descriptors" |
| years card / pill | `Track.Year` | `Tracks.Year[slot]` (ushort) | `Knows(TrackFields.Year)` on ≥60 % of rows (`MinCoverage`) |
| tempo card / pill | `Track.TempoBpm`, `Track.CamelotColor` | `Tracks.Tempo[slot]` (×10), `Tracks.CamelotColor[slot]` | `Knows(TrackFields.Audio)` on ≥60 % |
| the lens header's count | `View().Length` | the filtered view length | always |
| the heart in the CTA cluster | `LibraryBridge.IsSaved(uri)` on the COLLECTION uri (`DetailRail.cs:232-234`) | `User.Likes(track)` / the collection uri | mounted only where a `LibraryBridge` exists (`SaveButton.cs:37` returns an empty box otherwise) — and **not at all in the vertical hero / Hero page layout**, which gates on `cfg.Heart != HeartMode.None` while `DetailConfig.Liked` is `None` (`DetailConfig.cs:218`, `DetailVerticalHero.cs:248`) |
| **the page's own cover slot** | `DetailModel.Cover` must be **null** (`DetailPage.cs:664`) | the liked page model must NOT carry a cover url | — a non-null cover turns the whole treatment feature off at `DetailRail.IsDynamicLikedCover` |
| the ArtistRef behind a face | `ranked[i].Artist` + `IStore.GetArtist(uri)` merge (`:797-810`) | `Artists.Name/Image[slot]`; the **first** credit seen for a key wins the display name (`LikedFactsRules.cs:234-250`) | never blocks: the name from the credit, the portrait when Identity lands |
| the "+N" on the face pile | `FacePiles.VisibleFaces(measured − Spacing.M·2, ranked.Count)` where `measured` is the CARD's border box on a 20-DIP grid (`UseMeasuredWidth(FacePiles.Step)`) | same — it is a LAYOUT fact, not a data one | first (unmeasured) frame paints 5, the measured count lands next frame (`LikedFactsPanel.cs:578,602-608`) |
| the artists' PORTRAITS | `Hydrator.EnsureManyAsync(uris, HydrationLevel.Identity, Revalidate: true)` inside a `UseResource` keyed on the ranked uris (`LikedFactsPanel.cs:585-598`) | `Artists.Image[slot]` + an `Ensure` that can ignore a name-only seal — see G9 | never blocks: initials until a portrait lands |

**Demand rule.** The page demands its whole model on mount — no page-side visible-window fetching:

```csharp
UseEffect(() =>
{
    Entities.EnsureEdge(User.Me, EdgeKind.Liked);                       // the whole liked list, paged by Fetch, never by the UI
    var rows = User.Me.LikedTrackSlots;                                 // ReadOnlySpan<int>, zero alloc
    Entities.EnsureRows(rows, TrackFields.Row | TrackFields.Year | TrackFields.Audio | TrackFields.Tags);
    Entities.EnsureEdges(rows, EdgeKind.TrackArtists);
});
```

At 10 000 liked that is ~34 batches of 300 for the row bundle — the planner's job, in one `Plan` call, not 34 page
renders (`plan §4.5`).

**Derived facts live on the model.** `LikedFactsRules.Summarize` must be computed ONCE per (liked edge version ×
tracks publication) and hung off the model — `User.Facts(me)` memoised on `Edges.Liked.Version[me]` and
`Entities.Publication` — not re-derived by the UI on every render and **never** probed field by field. The 0.2.9
250 ms settle timer exists only because hydration handed the page up to 20 new lists per second; in 0.3 the equivalent
gate is `L.State[me] == complete`, which is a better answer. Keep the **shape latch** regardless
(`LikedFactsRules.cs:868`): a fact may upgrade Absent→Label→Graph while the page is open, never downgrade.

### DATA GAPS

| # | what the surface shows | 0.2.9 source | the plan does not hold it | proposal |
|---|---|---|---|---|
| G1 | every cover treatment's chroma (Marquee/Stack washes, Tone's five spots, Lens's veil, Rainbow's ORDER, every placeholder tint, the page tone) | `SpotifyLive.CoverColorPlane` — per-image graded schemes from extended-metadata kind 179 (`VisualIdentity`) and the `fetchExtractedColors` pathfinder op | §4.6 decodes `VisualIdentity(k179)` into a `Staging` but §2's tree has **no table to put it in** and no `Known` bit | **New file `Entities/Palette.cs`**: `PaletteTable` keyed by image `StringId` — `Column<uint> DarkBase, DarkTinted, DarkSubdued, DarkAccent` + the same four Light + `Column<uint> Known` (bit 0 dark, bit 1 light) + `Signal<uint> Changed`. `Palette.Watch(StringId)` per-image signal for the leaves; `Palette.Ensure(span of image ids)` batched by `Fetch` exactly like rows |
| G2 | which liked cover each tile came from, deduped by ALBUM | `Track.Album.Uri` | fine — `Tracks.Album[slot]` is a slot; dedupe on the slot, not on a string | (none — note it so nobody re-introduces a string compare) |
| G3 | descriptor **display name** vs **wire token** (the chip joins on `text` = "k-pop", the blend and the chip LABEL show `display_name` = "K-Pop") | kind 6 `ExtensionDescriptorData.descriptors[]` (`docs/plans/wavee/liked-songs-content-filters-plan.md` §1.2) | §4.3 `TrackTags` payload is a bare `StringId` — one string, two meanings | `EdgeTable<TrackTagEdge> TrackTags` with `readonly record struct TrackTagEdge(StringId Token, StringId Display, ushort Weight)` — weight is the server's own descending order and the blend depends on `[0]` being the primary |
| G4 | the CURATED chip set (`content-filter/v1/liked-songs`, 15 chips this account, ETag-cached) | `IContentFilterService` → `ContentFilterChip(Title, Token)` | no home at all in §2 | `User.cs`: `Column<int> ContentFilterStart/Length` over a shared `Column<StringId> FilterTitles/FilterTokens` slab on the user row, + `Known` bit `UserFields.ContentFilters`; the ETag lives in `Store`'s `meta` table |
| G5 | the persisted cover style + its change notification | `WaveeSettings.LikedCoverStyle` int + `AppearancePrefs.Epoch` bump | §2 has `Platform.cs` but the plan never says how a settings change reaches a mounted page | `Platform.LikedCoverStyle` as a real `Signal<byte>` (wire-stable ints, `FromSetting` clamp kept) — the epoch+re-read dance exists only because the 0.2.9 store was not observable |
| G6 | the liked **AddedAt** per row | `Track.AddedAt` | §4.3 has it on `LibraryEdge` — correct, but every fact function takes a `Track` list | see §1.2: change the rules' input type, keep the arithmetic |
| G7 | `ContextUid` for "Play them" (the rediscover subset play) | `Track.ContextUid` | `PlaylistTrackEdge.ItemId` exists; `LibraryEdge` has no item id | add `StringId ItemId` to `LibraryEdge` (the collection wire does carry one) or accept uri-only ordered play |
| G8 | the bundled stock PNG | `assets/covers/liked-songs-300.png`, loaded by path | §2 moves `assets/` as-is ✔ | keep the path; keep `ImageTransition.None` (a fade on the degrade target is what makes a "flash") |
| G9 | the artist portraits behind the face pile | `Hydrator.EnsureManyAsync(uris, Identity, new HydrationOptions(Revalidate: true))` — a name-only artist stub ALREADY satisfies Identity, so a default Ensure no-ops and the pile stays on initials forever (`LikedFactsPanel.cs:591-596`) | §4.5's `Ensure` has no "ignore the seal" arm; a `Known` bit that is already set is the end of the story | `Entities.EnsureRows(span, fields, revalidate: true)` — one flag, same batching, so a satisfied-but-empty field can be asked for again. Without it the 0.3 pile is initials-only |
| G10 | the CURATED chip fetch's failure grammar | `IContentFilterService.GetLikedChipsAsync` is awaited off-render, never throws, falls back to its own ETag cache, and an EMPTY result is PUBLISHED (which hands the bar to the descriptor-derived fallback) rather than swallowed (`DetailTracks.cs:2170-2184`) | §4 has no seam for a remote read that is neither an entity nor a row bundle | keep it a service on `Services`, publish into the user row's chip columns (G4), and keep "empty is a publish" — a stale curated bar for the rest of the session is the bug this rule prevents |

---

## 8. Pure rules to port verbatim

| class | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `LikedCoverStyle` (enum) | `Features/Detail/LikedCoverRules.cs:16-36` | the nine **wire-stable** persisted values — Stock is 0 because it is the degrade target; new styles append, an existing value never re-means | `Wavee.Tests/LikedCoverRulesTests.cs:40,55,61` | `Entities/User.Liked.cs` CORE |
| `LikedCoverSite` (enum) | `:41-51` | Treatment / MiniTone / MiniMosaic / Stock — a pure VIEW decision, free to reorder | `:509,523` | same |
| `LikedCoverRules.FromSetting/ToSetting` | `:75-81` | clamp of an unknown int to Stock | `:40,55` | same |
| `LikedCoverRules.NameKey` | `:85-96` | style → loc key, total over the enum | `:425` | same |
| `LikedCoverRules.Tiles` | `:109-131` | distinct newest-first cover urls, deduped by album uri **and** url, blank artwork skipped, capped at 16 with an early stop | `:142,151,161,170,177,191,200,207,219` | same |
| `LikedCoverRules.MinTiles / Effective` | `:139-156` | the honesty ladder | `:83,97,108,124` | same |
| `LikedCoverRules.Site` | `:203-212` | what a cover of N DIP paints (floor injected, NaN falls through to Treatment) | `:509,516,523` | same |
| `LikedCoverRules.FillCells` | `:216-222` | deterministic cycling fill | `:235,239,245,252` | same |
| `LikedCoverRules.WallCellIndex` | `:229-234` | `(i·5 + i%7) % n` — no adjacent repeat at ≥8 tiles | `:269,278,295,305` | same |
| `LikedCoverRules.RainbowOrder` | `:245-275` | hue-sorted serpentine permutation, ungraded last, stable ties | `:317,326,334,344,352,362,375` | same |
| `LikedCoverRules.HueOf` | `:282-300` | hue in degrees or null for achromatic (plain uint math — stays engine-free) | `:390,404,409` | same |
| `LikedCoverRules.IsSquare / FitSide / IsLikedCollection / Canonical / ToneAnchorUrl` | `:169-193,305` | slot shape + uri identity | `:465,478,555,566,226` | same |
| `LikedFactsRules` (whole file) | `Features/Detail/LikedFactsRules.cs` | **every number on the panel**: `LikesPerWeek`, `BucketClock`, `WeekWindowMs`, `WeekRange`, `IsWeekLens`, `ThisWeekLastYearWindow`, `LikedInWindow`, `TopArtists`, `ArtistKey`, `BlendShares`, `BlendOther`, `TailSplit`, `TaggedTotal`, `LikingSince`, `OldestLike`, `DominantDecade`, `StampsSpread`, `YearHistogram`, `PeakYear`, `HasReleaseYears`, `DominantReleaseDecade`, `OldestRelease`, `Shape`, `YearsDominance`, `TempoSummarize`, `TempoValues`, `BlendsDominance`, `Latch`, `WeekShape`, `LatestStamp`, `AnyArtistCredit`, `Summarize`, `ActiveLenses`, `ClearLens`, `TracksEquivalent` | `Wavee.Tests/LikedFactsRulesTests.cs` (1191 lines, ~70 facts) | `Entities/User.Facts.cs` CORE |
| `ContentFilterTags` | `Wavee.Core/Library/ContentFilterTags.cs` | chip derivation (`MinTrackCount` 3, `MaxChips` 10, count-desc then name, case-insensitive), `DeriveCounted` + `TagCount` (the same set with the carrier counts kept), `OrderByEvidence` (curated set kept whole, evidenced first, boundary reported) and the older `Reconcile` (DROPS un-evidenced chips — superseded by `OrderByEvidence`, still exported; do not port it as the live path) | `Wavee.Tests/ContentFilterTagsTests.cs` | `Entities/User.Liked.cs` CORE |
| `LikedFactsRules` **stamp gate** | `LikedFactsRules.cs:28-40` | `UnknownStampFloor` = `DateTimeOffset.UnixEpoch` and `TryStamp` — a stamp at or before the epoch is UNKNOWN, not "liked in 1970". EVERY time fact is gated on it; artist and blend facts are not | `LikedFactsRulesTests.cs` | `Entities/User.Facts.cs` CORE |
| `LikedFactsRules` **lens predicates** | `:123-127,172-221` | `IsWeekLens` (compared on the WINDOW, never an index), `IsArtistLens` (ordinal, against `ArtistKey`), `IsTagLens` (case-insensitive), `IsYearLens` (the inclusive range), `IsTempoLens` (`Any` is never a lens), the `[Flags] LikedLens` enum, `ActiveLenses`, `ClearLens` | `LikedFactsRulesTests.cs` | same |
| `LikedFactsRules` **tempo pass** | `:719-834` | `TempoBandCount` 4, `TempoBandCounts`, `TempoSummary`/`TempoStats`/`TempoFingerprint`, `TempoSummarize` (ONE pass: histogram median — the LOWER middle, never an average — band counts, dominance, shape, fingerprint), `TempoDominance`, `TempoStatistics`, `FingerprintTempo`, `TempoValues` (bpm + Camelot ARGB in list order) | `LikedFactsRulesTests.cs` | same |
| `LikedFactsRules` **summary types** | `:695-711,940-994` | `Dominance` (ties → the LATER index), `YearsDominance`, `FactsSummary` + `Summarize(tracks, yearBars 12, blendSlices 5, artistCap 40)` — the artist cap is what bounds "N more" at 35 | `LikedFactsRulesTests.cs` | same |
| `LikedFactsRules` **shape thresholds** | `LikedFactsRules.cs:465,662-690,720,742,857-868,891-907` | `FactShape {Absent, Label, Graph}`, `Shape(known,total,categories,topShare,cap,minCategories)`, `ShapeOf`, `YearsShape`, `TempoShape`, `BlendShape`, `Latch`, `WeekShape` and their constants: `MinDecadeEvidence` 10 · `MinGraphEvidence` 20 · `MinGraphCategories` 3 · `MinTempoGraphCategories` 2 · `MinCoverage` .60 · `YearsCap` .50 · `TempoCap` .70 · `BlendCap` .85 · `BlendFlat` .15 · `MinWeekEvidence` 3 · `TempoBandCount` 4 · `MaxBpmBin` 400 | `LikedFactsRulesTests.cs` | `Entities/User.Facts.cs` CORE |
| `TempoBandText` | `Features/Detail/LikedFactsPanel.cs:1490-1519` | the ONE wording + domain table for the four bands — read by the card's bands, the tempo pill AND the lens header, so a pill can never name a band the rows do not match. Domains `(60,90) (90,120) (120,140) (140,200)` | (indirect) | `Entities/User.Facts.UI.cs` — **UI (it calls `Loc.Get`), but its DOMAIN intervals must agree with `TrackFilterModel.BandOf`** |
| `TrackTempoBand` / `TrackFilterModel.BandOf` | `Features/Detail/TrackFilterModel.cs:30,247-252` | `Any=0` then `<90 / <120 / <140 / else`; `Any` is never a lens | `Wavee.Tests/TrackFilterModelTests.cs` | `Entities/Track.cs` CORE |
| `TrackFilterState` / `TrackFilterModel` | `Features/Detail/TrackFilterModel.cs` | the filter value + predicate — `WithAddedWindow`/`WithAddedRange` mutual exclusion, the half-open `(after, before]` window, `BandOf`, `HasArtist`'s uri→id→name ladder, `HasTag`'s case-insensitive DISPLAY-name match | `Wavee.Tests/TrackFilterModelTests.cs` | `Entities/Track.cs` CORE (shared with 04) |
| `ContentFilterChipSet` | `Wavee.Core/Library/ContentFilterTags.cs:16-24` | `IsEvidenced(i) => i < EvidencedCount` — evidence is a PREFIX, so the ordering IS the state | `ContentFilterTagsTests.cs` | `Entities/User.Liked.cs` CORE |
| `DetailRailPolicy` | `Features/Detail/DetailRailPolicy.cs` | `RailScope.Liked` is its own persisted pair (default 240 = `WaveeSize.RailPlaylist`), floor 180 / ceiling 480, grip at mode 0 ONLY (`ResizableMode`), `ClampStored` — **and `ScopeFor(requested, uniform)`: with "Keep left-rail same size" on, Liked resolves to the shared `RailScope.Uniform` pair like every other surface** | `DetailRailPolicyTests.cs` | `Shell/Shell.cs` CORE (shared with 03) |
| `DetailLayoutBreakpoints` | `Features/Detail/DetailLayoutBreakpoints.cs` | 820/660/560 + 540/580 vertical band, 24 DIP hysteresis | `DetailLayoutBreakpointTests.cs` | `Shell/Shell.cs` CORE (shared with 03) |

Two constants are **composition facts** that must stay where the geometry is, injected into the pure rule rather than
duplicated: `LikedCoverTreatments.BadgeMinSize` (140) is passed INTO `LikedCoverRules.Site`
(`LikedCoverRules.cs:196-202`), and `ChromeMinSize` (180) is read only by `Build` (`:75`).

---

## 9. Re-author notes

### What must not be simplified

* **Nine treatments, not "a few".** Each is a distinct persisted value; dropping one silently re-means a stored int.
* **The 304 canvas.** Re-deriving proportions from `size` breaks three things at once: the static `Keyframe[]` arrays
  (the engine stores keyframes by reference, so one array must be correct at every size), the cheapness of a rail drag
  (re-scale a composited transform vs. re-lay-out 36 tiles), and the flyout's "the miniature IS the cover" property.
* **The leaf discipline.** A grading arriving for one of sixteen covers must repaint ONE node. If the re-author moves
  `Watch` up into the cover component, a Rainbow cover re-requests sixteen decodes every time a colour lands.
* **The two-node split in Wall and Marquee.** The anim fold reseeds a node's translate from its composited transform,
  so a static offset on the node a `TranslateX/Y` track drives is overwritten on the first tick. Outer node = static
  placement + rotation; inner node = the animated channel and nothing else (`LikedCoverTreatments.cs:466-472,576-589`).
* **Marquee's index arithmetic.** `tiles[(seed + i % MarqueeRun) % count]` — the `% 8` must happen BEFORE the tile
  wrap or the row's content period (16) fights the travel (8) and the loop shows a visible jump every minute.
* **The honesty gates.** `LikedFacts.Has` runs on every rail render and must stay an allocation-free early-exit scan.
* **The blend's partition.** Each track contributes its PRIMARY tag only (`Tags[0]`); raw tag counts sum past 100 %.
  **The CHIPS do the opposite on purpose**: `ContentFilterTags.DeriveCounted` counts EVERY descriptor a track carries
  (`ContentFilterTags.cs:79-84`), because a chip is a membership question ("show me the K-Pop ones") while a slice is a
  share of one whole. So "K-Pop" as a chip and "K-Pop" as a blend slice legitimately name different numbers, and the
  two must NOT be unified into one count in 0.3 — they do agree on the facet they write (`Tag`), which is what makes
  them light each other.
* **Reduced motion as a VALUE.** `s_still` is a real keyframe array; the track still exists and still owns its slab
  row. An `if (ReducedMotion)` that removes the node changes what is authored and is a hook-order hazard.
* **`Nothing`, not a missing child.** Wall's chip and Marquee's/Stack's badge are `chrome ? NameChip() : Nothing`
  where `Nothing` is a zero-extent hit-transparent BoxEl (`LikedCoverTreatments.cs:763`) — a conditional chip must be
  an EMPTY child, not a varying child COUNT, or the ZStack's other children shift position across a rail drag.
* **The blend body hangs off an UNGAPPED outer column.** A flex gap is charged per child BOUNDARY, so a zero-height
  host inside the gapped card column leaves 8 DIP of dead air while the card is shut — and the collapse watcher,
  a real child for ~167 ms, would add 8 more at exactly the moment the fold must read as smooth. The open state's
  breathing room lives INSIDE the body as its top padding (`LikedFactsPanel.cs:1037-1043,1275`).
* **Tone's count is culture-formatted.** `trackCount.ToString(CultureInfo.CurrentCulture)` — 1 166 likes print with
  the local thousands separator on the cover (`LikedCoverTreatments.cs:673`). Do not "simplify" to invariant.

### Traps

* **Props freeze at mount.** `LikedCoverArt`'s size/radius/morph are ctor fields; the Key carries all three
  (`LikedSongsArtwork.cs:37-41`). A 0.3 re-author who passes `size` as a plain field without the Key gets a cover
  frozen at the first rail width — invisible until someone drags the grip.
* **`Snapshot` equality is declared over `TileKey`, not `Tiles`.** An array compares by reference, so default record
  equality reports "changed" on every recompute and defeats the gate entirely (`LikedCoverArt.cs:32-36`). Same trap in
  `LikedRainbowGrid.Props` (`LikedCoverLeaves.cs:263-274`).
* **The epoch caveat.** `AppearancePrefs.Epoch` is only the RECOMPUTE TRIGGER; the VALUE read from the settings store
  must be CARRIED in the memo, or the memo recomputes on every bump, compares equal, and never propagates the style
  change it was bumped for (`LikedCoverArt.cs:27-31`). In 0.3 this whole hazard disappears if G5 lands (a real signal).
* **Hooks are declared unconditionally, in one order, on every path** — `LikedCoverArt` is the feature's only
  component precisely so that a hook behind "which style?" cannot shift order the first time a user picks one
  (`:13-18`). `LikedArtistsCard` / `LikedBlendCard` / `TempoCard` are separate components for the same reason: the
  panel mounts them under `if`s.
* **`Key` on the scaled canvas** (`"liked-style:"+style`) is what makes a treatment swap a REMOUNT. Without it the
  reconciler position-matches the outgoing style's nodes onto the incoming style's — fine for pixels, wrong for the
  ambient loops, whose slab rows are keyed to a node handle that then never re-realizes.
* **ReuseGuard.** The picker's flyout mounts nine live treatments; if the re-author lets the flyout re-push a fresh
  `string[]` of cells without a content key, ReuseGuard will show nine remounts per render.
* **Zero-allocation scroll frames vs per-row richness.** 0.2.9 reconciled them by keeping the cover OFF the row path
  entirely (it is a rail/hero/card object), by computing the facts OFF the render path behind a settle timer, by
  memoising the tempo plot's value array on a CONTENT fingerprint rather than on list identity
  (`LikedFactsPanel.cs:1570-1576`), and by making every hover/lit state a compositor brush swap rather than a
  re-render. Keep all four.
* **`ClipToBounds` on or under a rotated node clips to the AXIS-ALIGNED box.** The clip frame is outside and
  unrotated; rotation happens strictly inside it (`LikedCoverTreatments.cs:30-32`).
* **`ImageEl.DecodePx` is ignored the moment `Width` is explicit** — hence the wrapper box in `Cell` (`:127-130`).
* **The cover and its FLYOUT resolve the library store differently.** `LikedCoverArt` takes
  `UseContext(LibraryStore.Slot) ?? svc?.LibraryStore` (`LikedCoverArt.cs:79`) precisely because it mounts under hosts
  that do not re-provide the slot (an overlay, a menu header, a drag chip); `LikedCoverStyleFlyout` takes the CONTEXT
  alone (`LikedCoverPicker.cs:164`). Port both spellings as they are and check the 0.3 overlay host's context
  inheritance: with no store the flyout's `tiles` is empty and all nine miniatures degrade to the stock PNG, which
  looks exactly like "the picker is broken".
* **The rail heart is not config-driven, the hero heart is.** See §7 and W4: `DetailRail.Build` and `PlayRow` mount
  `SaveButton(ContextUri)` unconditionally, `DetailVerticalHero` gates on `cfg.Heart`. Whatever 0.3 decides, decide it
  once — three call sites with two rules is how this drifted.
* **`PathGeometryTable.Register` for the heart, a minted epoch for the pill glyphs.** `LikedHeart` interns through the
  table so the tessellation cache survives (`LikedHeart.cs:38-43`); `FactPills`' three glyphs mint their own epoch and
  are therefore safe ONLY as `static readonly` fields (`LikedFactsPanel.cs:1637-1639`). A 0.3 re-author who moves
  either into a render body re-tessellates per frame.

### Where the plan is wrong or too thin for this surface

1. **§2's tree had no file for any of this** — `User.cs 500 + User.UI.cs 500 + User.Page.cs 1500` was supposed to hold
   "library edges; library, liked, profile pages", while the liked cover alone is 1 600 lines of composition and 300 of
   rules and the facts are 2 750. **Settled (arbitration 2026-09-12).** `User.*` is eight files — pages split by page,
   helpers by concern: `User.cs` (CORE), `User.UI.cs`, `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Liked.cs`
   (CORE), `User.Facts.cs` (CORE), `User.Facts.UI.cs`, `User.Cover.cs` — all owner **O**, all named as partials on
   day one. **There is no `User.Page.Profile.cs` and no `RouteKind.User`**: 0.2.9 has no profile page and no profile
   route. `ShellRoutes.s_exact` (`ShellRoutes.cs:27-42`) lists `home browse search albums artists liked podcasts local
   history recents settings api-console playback-diagnostics whatsnew sidebar-customize home-customize` and nothing
   user-shaped (`api-console` is 0.2.9's; it is DELETED in 0.3 — plan §9.6 Q7, 2026-09-12); the only "profile" in the tree is `Features/Shell/ProfileMenu.cs`, the avatar **menu**, which chapter 19
   owns. See the file plan below.
2. **§4.3 `EdgeTable<TEdge>` does not compile as written** (`wavee-0.3-implementation.md:298-313`): `public Column<int>
   Targets;` and `public ReadOnlySpan<int> Targets(int parent)` are the same member name. Every page in Wave 5 hits
   this. Rename the columns (`_targets`/`_payload`) before Wave 1 ships.
3. **§4.3 has no `Insert`.** §4.14's `Like()` calls `E.Liked.Insert(Slot, t.Slot, …, at: 0)` — prepending is exactly
   what the cover needs (newest-first) but the table only offers `Replace`/`ReplacePage`. Add `Insert(parent, target,
   payload, at)` and `Remove(parent, target)` with the same rewrite-in-place rule, and make both bump `Version`.
4. **§4.12 (`Track.UI.cs`) is the wrong altitude for this page.** The liked list needs the Date-added, Album, Plays,
   BPM·Key, ♥ and Video lanes and the expander — see 04. Not this chapter's problem, but the plan's one-row sketch
   will mislead whoever reads it as the contract.
5. **§4.13's demand pattern is right and must be spelled out for liked**: the edge, then the rows, then the artist
   edges — one `Ensure` per kind per mount, never per visible range.
6. **Nothing in the plan covers image colour.** G1 is the single biggest gap for this surface: without a palette table
   five of the nine treatments lose their ground and Rainbow loses its ORDER (it degrades to input order — which is
   *legal* by `RainbowOrder`'s null rule, so the bug would look like "Rainbow is just a grid").
7. **§4.15's test list does not mention the two rule suites** that are the most valuable ports in the whole wave
   (1 759 lines of pinned decisions). They belong in the Wave-5 gate, not just Wave 1.

### Line budget

| | lines |
|---|---|
| 0.2.9, this surface (the 12 files in the header) | **5 121** |
| former plan §2 target for `User.cs + User.UI.cs + User.Page.cs` — which had to ALSO hold the library pages | 2 500 |
| honest estimate, this surface alone, in 0.3 | **4 600 – 5 200** |

Savings: handles remove `Resolve`/`UriKey`/`UrisNeedingPortrait` (~120 lines), the store-context dance in
`LikedCoverArt` (~20), `TracksEquivalent` + the settle timer if `L.State` replaces them (~40), and the hydration
plumbing in the artists card (~30). Costs: the edge-payload marshalling for `AddedAt` (~60), the palette table's
accessors (~80, in `Palette.cs`), and the chip-set columns (~60). Net: about the same. **A surface does not get
smaller because the data layer did.**

### The `User.*` file plan (settled 2026-09-12)

Eight files, one owner (**O**), all named as partials on day one under the plan's +30% rule. The rows this chapter
owns are marked ◆; the rest are chapter 15's and are listed so the type is accounted for in one place.

| file | this chapter | why | rough lines |
|---|---|---|---|
| `Entities/User.cs` (CORE) | — | the user row + the library edges, the ordering/selection/breakpoint rules and the library search matcher (chapter 15 §8) | 800 |
| `Entities/User.UI.cs` | — | the library row/card builders, the sort-view pill + panel, the crumb bar, the column grip (chapter 15) | 600 |
| `Entities/User.Page.Library.cs` | — | `User.LibraryPage` — the albums / artists / podcasts master-detail (chapter 15) | 1 300 |
| `Entities/User.Page.Liked.cs` | ◆ | the liked page's own configuration of the shared detail frame: the rail/hero bento hosts, the lens header's placement, the chip bar, the list footer arm for `PageHero` | 700 |
| `Entities/User.Liked.cs` (CORE) | ◆ | `LikedCoverRules` + `ContentFilterTags` + the liked uri identity | 500 |
| `Entities/User.Facts.cs` (CORE) | ◆ | `LikedFactsRules` verbatim, input type changed | 1 000 |
| `Entities/User.Facts.UI.cs` | ◆ | the bento: panel, week/years/tempo/artists/blend/rediscover cards, pills, since-line, lens header | 1 800 |
| `Entities/User.Cover.cs` | ◆ | the nine treatments, the four palette leaves, the heart geometry, the cover component, the picker + flyout | 1 700 |
| **`User.*` total** | | | **8 400** |

`Entities/Palette.cs` (CORE) is G1's home — the per-image graded schemes as columns, `Watch`, `Ensure`, ≈400 lines. It
is **not** a `User.*` file and it is not this chapter's to build: it lands in Wave 1 precisely because this surface
(and four others) cannot draw without it.

**Deleted from the tree:** `User.Page.Profile.cs`. There is no profile page in 0.2.9 — no route in
`ShellRoutes.s_exact`, no `PageFor` arm — so 0.3 inherits none, and no `RouteKind.User` either.
`Features/Shell/ProfileMenu.cs` is the avatar **menu** (chapter 19), and its "Play file…" row is chapter 15 §11.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.

**What `--fake` gives you** (verified from the bundled export + generator): Liked Songs = **166 tracks**
(`SpotifyExport` `PseudoPlaylist count = 166`; `SpotifyExportSource.cs:139-140` → `FakeData.LikedSongs`), **16 distinct
covers** (`FakeData.cs:12,24-29` — exactly `MaxTiles`), tags cycling 6 descriptors (~27 carriers each), tempo in three
humps (~96 / 128 / 172), years 2008–2024, Camelot colours — and **no `AddedAt` at all** (`FakeData.cs:56-60` never sets
it). Consequences to expect in `--fake`, in BOTH builds: **no week card, no Rediscover card, no since-line**; the time
slot is taken by **The years**; the fake cover urls are local file paths, so `CoverColorPlane.CanGrade` is false and
every palette ground paints its **neutral fallback** and Rainbow keeps input order. Items marked **[live]** need a real
login (or a seeded `AddedAt`) and are verified against the same account in both builds.

Route in both builds: sidebar → Liked Songs (or `wavee://liked`).

| # | check | how |
|---|---|---|
| 1 | Cover composes the user's own art, not the purple PNG | window ≥ 1200 wide, static capture of the rail; compare tile-for-tile |
| 2 | Default style is **Lens** on a fresh profile | delete `appearance.likedCover.style`, relaunch, capture |
| 3 | Lens: the heart window is 76 % of the cover, centred, and the crisp mosaic inside it ALIGNS with the blurred one | capture at rail 240; overlay the two builds' crops |
| 4 | Lens: the rim is a continuous white hairline at α .55 over the silhouette (no stair-stepping *inside* the fill) | 400 % crop of the heart's left lobe |
| 5 | Lens: the sheen lifts the top and seats the foot; the veil is a diagonal cast, not a flat tint | capture, sample 3 pixels (top, middle, bottom) |
| 6 | Wall: 6×6, gap 7, corners 3, rolled −11°, no perspective | capture; count cells and measure the roll |
| 7 | Wall: no two adjacent cells show the same cover | capture, visual scan (the `(i·5+i%7)` scatter) |
| 8 | Wall: the drift reaches −46 DIP and returns; one cycle is 92 s | 100 s frame recording at 2 fps; compare the extreme frames |
| 9 | Wall: two-layer vignette — a radial low-point at (50 %, 42 %) and a seated foot | capture, sample the four corners |
| 10 | Rainbow: 4×4, cell 74.5, gap 2, on the `#131318` plate | capture, measure |
| 11 | Rainbow: **[live]** hue-ascending serpentine with row 1 and row 3 reversed | live account capture, compare the tile ORDER |
| 12 | Rainbow with no gradings (`--fake`) is input order and NOT reshuffled between renders | capture, navigate away and back, re-capture |
| 13 | Marquee: three bands (not two), tile 104, gap 7, −16°, tops −20 / 95.5 / 211 | capture; measure band pitch perpendicular to the bands |
| 14 | Marquee: ground shows ONLY as the top-left and bottom-right corner slivers (~25 DIP) | capture |
| 15 | Marquee: the three bands never phase-lock; middle band travels the other way | 120 s recording at 1 fps |
| 16 | Marquee: the wrap is seamless (no content jump at the loop point) | 70 s recording at 10 fps around t=60 s |
| 17 | Marquee: badge bottom-LEFT with the fixed media shadow (blur 14, dy 3) | 300 % crop; compare shadow spread between light and dark theme (it must NOT change) |
| 18 | Feature: hero 202 top-left, two 100 followers right, three 100 below, gap 2 | capture, measure |
| 19 | Mosaic: 3×3 edge to edge, no gaps | capture |
| 20 | Tone: five soft spots at the authored anchors, the vector heart at 62 %, the count bottom-right | live capture (`--fake` gives the neutral plate + heart + count) |
| 21 | Tone at a size < 140 keeps its gradient + heart (MiniTone), never the PNG | sidebar pin capture at 32 DIP with style = Tone |
| 22 | Stack: five cards fanned, NEWEST on top, rotations ±22/±11/0 | capture; the top card must equal the first row of the track list |
| 23 | Stack hover: the fan opens by the authored deltas and settles in 250 ms | hover capture + a 1 s recording at 30 fps |
| 24 | Name chip appears at cover ≥ 180 and is dropped below it | drag the rail grip from 240 → 180, capture at both ends |
| 25 | Heart badge survives down to 140 — but **no Liked cover on the page is ever exactly 140**: the reachable floors are the vertical hero's 144 (`RowArtMin`) and mode 2's 164, so verify 144 here and 140 itself on a shelf/grid card | narrow the window until the hero art stops shrinking (144), capture; then a Home card at ~140-160 |
| 26 | Below 140 the cover is the flat 2×2 collection mosaic (and the stock PNG below 4 distinct covers) | collapse the rail (compact strip, cover 80), capture |
| 27 | The picker pill reveals on hover and stays lit while the flyout is open | hover capture, then open-flyout capture |
| 28 | The pill is a real tab stop and shows itself on keyboard focus | Tab to it from the cover, capture |
| 29 | `Down`/`F4` on the pill opens the flyout | key, capture |
| 30 | The flyout is 332 wide with exactly three columns and no fourth-column slack | capture, measure |
| 31 | The flyout's order is column-major: Lens/Wall/Rainbow · Marquee/Feature/Mosaic · Tone/Stack/Stock | capture |
| 32 | The nine miniatures are LIVE treatments (Wall shows a 4×4, everything static) | capture; compare each thumbnail against its full cover |
| 33 | Arrow-roving applies immediately — the big cover behind the flyout changes on every rove | hold ↓ and take a 2 s recording |
| 34 | A style below its floor shows a dimmed (0.45) stock miniature and is still selectable | seed a 3-track liked list (or capture on a fresh account), capture |
| 35 | The checked card takes a 2 DIP accent border drawn INWARD (the miniature does not shift by a pixel) | capture two adjacent cards at 400 % |
| 36 | Facts panel: the card ORDER is time → tempo → artists → blend → rediscover → pills → since-line | capture the whole rail |
| 37 | Facts shimmer appears on a cold open and reveals ONCE (no second flash) | 2 s recording at 30 fps from the click |
| 38 | The years card shows 12 bins, the peak bar at full accent, the numeral = the modal year | capture; compare the numeral with the tallest bar |
| 39 | Tempo card: median numeral, a 60–200 ridge, three axis captions (80/120/160), four bands | capture |
| 40 | Clicking a tempo band lights the band, adds a lens pill and filters the rows | click capture + the row count |
| 41 | Most-liked: 5 named artists + a face pile that never overflows its card at 180 DIP | drag the grip to 180, capture |
| 42 | Clicking a face and clicking its name row do the SAME thing (and light both) | click each, capture |
| 43 | "N more" opens the all-artists flyout (280 wide, rows 44, capped at 40) | click, capture, scroll |
| 44 | Blend bar: five accent-stepped slices + a tick strip, never a grey slab | capture at 400 % |
| 45 | Every tick is hoverable and names its descriptor instantly (0 ms tooltip) | sweep the strip in a 2 s recording |
| 46 | "N more · X %" opens the card in place and the cards BELOW reflow down (they are not overlapped) | 1 s recording at 30 fps of the open |
| 47 | The opened tail bar wipes from its left edge and the legend rows stagger at 40 ms, capped | same recording |
| 48 | Closing takes 167 ms and the body unmounts only at settle (no blink) | 1 s recording of the close |
| 49 | The opened tail's percentages say "of the tail"; the legend's say library share | hover one segment and its legend row, capture both tooltips |
| 50 | **[live]** Week card: +N numeral, 12 bars, newest at full accent | live capture |
| 51 | **[live]** Clicking a bar filters the list to exactly that week and lights that bar only | click, capture, compare the header count with the bar's tooltip count |
| 52 | **[live]** The lit bar stays lit across a re-render within the hour | click a bar, navigate away and back, capture |
| 53 | **[live]** Rediscover mounts only when the window holds tracks; "Play them" plays that subset in list order | live capture + play |
| 54 | **[live]** Since-line reads "Liking since <Month Year> · mostly the 2010s · oldest like <title>" | live capture |
| 55 | Pills appear ONLY when the matching card did not (years/tempo/blend are never doubled) | force a dominant library (filter to one year first is not enough — use a small account), capture |
| 56 | The lens header is exactly 36 DIP tall with 1 / 2 / 3 pills and never wraps | apply three lenses, capture; the rows below must not shift |
| 57 | Each pill's ⓧ retires exactly one facet | apply week+artist, clear the week, capture the remaining pill and the row count |
| 58 | The lens header's count equals the visible row count | capture both |
| 59 | Chips: one scrolling line with an edge fade, `All` + one selected | capture; drag-scroll the rail |
| 60 | Re-tapping the active chip clears it (no dead tap) | tap twice, capture |
| 61 | A chip and the blend slice for the same descriptor light each other | click the slice, capture the chip bar |
| 62 | **[live]** An un-evidenced curated chip renders disabled, not missing | live account with sparse descriptors, capture |
| 63 | Empty library: the stock PNG, "Nothing here yet", NO facts panel at all | sign out / fresh profile, capture |
| 64 | 10k library: opening the page does not stall; the cover composes from the newest 16 | live big account; `page.reveal` + `nav.frames` log lines in both builds |
| 65 | The Liked rail has NO layer fill (compare with the Playlist rail side by side) | capture both pages at the same window size |
| 66 | The Liked rail keeps its OWN width/collapse preference (drag it, then open a playlist) — **unless Settings' "Keep left-rail same size" is on, where every surface shares `RailScope.Uniform`** | drag to 300, open a playlist, come back, capture; then flip the setting and repeat |
| 67 | Default sort on Liked is Date added, descending | capture the column header |
| 68 | The page tone follows the cover's LEAD tile, not the first gradeable row | **[live]**: like a record with a loud sleeve, capture the page ground before/after |
| 69 | The vertical hero (window < 540) mounts the picker and the facts FOOTER under the last row | narrow the window, capture the hero and scroll to the end |
| 70 | The drag chip, the context-menu header and a sidebar pin all show the 2×2 mosaic of the newest likes | drag the Liked row, right-click it, pin it — three captures |
| 71 | The treatment chrome matrix holds: chip on Wall/Rainbow/Feature/Mosaic, badge on Marquee (left) / Stack (right), NEITHER on Lens or Tone | nine captures at rail 240, one per style |
| 72 | The Lens veil still tints when only the NEWEST cover has graded (stop 1 reuses stop 0, no half-neutral ramp) | live account, capture the first second of a cold open at 10 fps |
| 73 | Tone's count is culture-formatted (a 1 166-track library shows the local thousands separator) | switch the OS locale to de-DE, capture |
| 74 | Tempo card head shows "N of M" while coverage is partial and "lo – hi bpm" only when complete | **[live]** capture before and after the audio-features batch lands |
| 75 | Tempo plot has NO rug dots and NO hairlines — ridge, line, median marker, three captions | 400 % crop |
| 76 | The face pile never overflows: at every rail width the last frame is the "+N" count, never a clipped portrait | drag the grip 180 → 480 in a 2 s recording |
| 77 | An artist credit with no uri/id/name is a plain row — no hand cursor, no hover plate, no focus stop | **[live]** find a "Various Artists"-style credit, hover and Tab through the card |
| 78 | Rediscover shows the caption with NO "Play them" button when there is no active player | kill the player / open before the device is ready, capture |
| 79 | A blend whose five slices cover everything draws no tick strip and no "N more" button | small account with ≤ 5 descriptors, capture |
| 80 | The blend card's open/close leaves NO dead air under the legend while shut (the legend↔card-bottom gap is the same open and closed) | 400 % crop of the card foot, open and closed |
| 81 | The un-evidenced curated chips are a contiguous TAIL — evidenced chips never interleave with disabled ones | **[live]** sparse account, capture the whole scrolled chip rail |
| 82 | A disabled chip does not animate on hover (no scale, no border change) | hover it in a 1 s recording at 60 fps |
| 83 | The cover-style flyout's checked card is the SETTING's style even when the library cannot feed it (dimmed but checked) | 3-track liked list with style = Wall, open the flyout, capture |
| 84 | Ctrl+↓ in the flyout moves the focus ring without changing the cover behind it | hold Ctrl and press ↓ three times, capture the cover each time |
| 85 | `Down`/`F4` on the artists "N more" row opens the all-artists flyout | Tab to the row, press F4, capture |
| 86 | The lit spark column / name row / legend row hovers to a DEEPER accent, not to a neutral plate | hover a lit and an idle one, 400 % crops of both |
| 87 | The first-ever cold open crosses stock → treatment in one frame (no blank hero, no fade) | fresh profile, 1 s recording at 60 fps from the nav click |
| 88 | Mode 1 (page 660–819) composes rail **224** / cover **200** and KEEPS the name chip; mode 2 (560–659) composes rail **188** / cover **164** and DROPS it — and neither offers the grip | resize the window across 820 and 660, capture at each; try to drag the rail edge at both |
| 89 | The vertical hero's action row has **no heart** (Play · Shuffle · Share · More), while the two-column rail shows one | narrow to < 540, capture; compare with the same page at 900 |
| 89b | Settings → page layout **Hero** puts Liked on the vertical hero at EVERY width (no rail, no rail bento — the facts are the list's footer) | flip `DetailPageLayout` to Hero at a 1400-DIP window, capture the top of the page and the end of the list |
| 90 | The vertical hero's art is 197 at a 520-DIP column and never below 144 / above 240; the artwork sits BESIDE the copy down to 424 and above it down to 400 | resize 380 → 700 in a 3 s recording, sample the art edge at 400 / 424 / 520 |
| 91 | The compact strip is layered (`FillLayerDefault`) and tooltips its title; the expanded Liked rail is not layered | collapse the rail, capture + hover; compare with the expanded rail at the same window |
| 92 | The tempo card's first frame shows the four bands with no ridge, and the ridge lands on the next frame (never a re-tessellation per hydration pass) | 500 ms recording at 60 fps from the nav click; then watch `DensityPlot.GeometryBuilds` across a full hydration |
| 93 | The flat-blend pill does not answer the pointer at all (no hover fill, no scale, no focus ring, no hand cursor) | a library with ≤ 15 % top share, hover and Tab over the pill in a 1 s recording |
| 94 | The face pile's count changes only at 20-DIP steps of the card's width — it never re-flows mid-drag | drag the grip 180 → 480 slowly, count the faces at each step |
| 95 | The "N more" row and the pile's "+N" both stop at 35 / 40 − faces on a library with hundreds of credited artists | live big account, capture the artists card and open the flyout (it lists at most 40) |
| 96 | The cover-style flyout still shows nine LIVE miniatures from both picker sites (the rail cover and the vertical hero's) — never nine stock PNGs | open the flyout at window widths 900 and 500, capture each |

---

## 11. Audit log

Adversarial pass against the 0.2.9 sources (2026-09-12). Every line below is a change made to this file; the citations
were re-read from the code, not from the draft.

| # | kind | what |
|---|---|---|
| 1 | **wrong** | Header + §9 line budget said **4 976** lines. The twelve files listed sum to **5 121** (156+299+272+307+766+1756+995+60+85+107+153+165). Corrected in both places. |
| 2 | **wrong** | W4 gave the vertical hero art as **208**. `DetailVerticalLayout.ArtworkFor` (`:133-143`) is `round(clamp((colW − 2·24 − 24) × 0.44, 144, 240))` = **197** at a 520 column. Corrected, and the function + its clamps are now cited. |
| 3 | **wrong** | W4 drew the vertical hero's action row as `[▶ Play] (♡) (⤴)`. `DetailConfig.Liked` is `Heart: HeartMode.None` and `DetailVerticalHero.cs:248` gates the `SaveButton` on it, so the row is **Play · Shuffle · Share · More with NO heart**. Recorded, with the rail's unconditional heart beside it. |
| 4 | **wrong** | W24 computed `VisibleFaces(132, 43)` → "+38" / "38 more". `Summarize`'s `artistCap` is 40, so the ranked list never exceeds 40 and both counts top out at 35; the measured width is also grid-quantised (136, not 132, at rail 180). Numbers corrected. |
| 5 | **wrong** | W1 annotated the face count as `VisibleFaces(railW − 24)`. The input is the CARD's measured border box minus `Spacing.M·2`, i.e. `railW − 48` at the rail — 8 portraits at rail 240, not 5. Corrected and the quantum explained under W1. |
| 6 | **wrong** | §3's content-chip row claimed a hover border of `AccentDefault`. A SELECTED chip's hover border is `Transparent` (`ContentFilterChips.cs:93`). |
| 7 | **wrong** | §8's `DetailRailPolicy` row claimed Liked's pair is always its own. `ScopeFor(requested, uniform)` resolves every surface to `RailScope.Uniform` when "Keep left-rail same size" is on. Checklist #66 corrected too. |
| 8 | **wrong** | Checklist #25 verified the 140 badge floor at "the vertical header (cover is exactly 140)". That arm is the EPISODES arm (`verticalTracks` is always true on Liked), and no Liked cover on the page is ever 140 — the reachable floors are 144 (hero) and 164 (mode 2). Rewritten. |
| 9 | **overclaim** | §7 pointed at a "§9 trap" about the CTA heart that did not exist. Replaced with the actual facts (collection uri, `LibraryBridge` capability gate, `HeartMode.None`) and a real trap added to §9. |
| 10 | **missing** | Modes 1 and 2 compose FIXED rails (224 / 188 → covers 200 / 164) and have no grip at all; mode 2 is the one width where the name chip is dropped without touching the grip. New table under W2. |
| 11 | **missing** | `WaveeSettings.DetailPageLayout == PageHero` forces mode 3 at EVERY width (`DetailShell.cs:509-511`): no rail, no rail bento, the facts as the list footer. Added to the §1.1 tree, W2 and the checklist (#89b). |
| 12 | **missing** | W3: the compact strip IS layered (`FillLayerDefault`, `DetailRail.cs:344`) and the whole strip is wrapped in a title ToolTip (`:353`); chevron glyph/radius stated. Non-negotiable #14 is about `Build`'s two arms only. |
| 13 | **missing** | §1.1 mount table: `MediaCard.ArtworkOrLiked` (the shared card funnel, `:172-178`); the context-menu header's circular 19-radius variant; and the sidebar's built-in Liked NAV row, which keeps its heart mark because `IconOverride` resolves before `SidebarCover.Liked`. |
| 14 | **missing** | `LikedSongsArtwork.Fitted` / `.Fill` — the non-square (cover-fit at the longer edge, frame owns the corners) and unmeasured (Responsive, fallback 160) arms were unspecified. |
| 15 | **missing** | Three states added to W23: the tempo plot's first, unmeasured frame (bands only — `DensityPlot.cs:107,114`); the flat-blend pill's fully inert hover/press; and "no `LibraryBridge` ⇒ no heart at all". |
| 16 | **missing** | §3: the tempo BAND hit box as its own row; the density plot's measured-width memo and the marker-alpha override; the face pile "+N" label metrics and its inert arm; the picker card's label gap. |
| 17 | **missing** | §3.1: `detail.filter.allChip` — the bar's leading chip label, deliberately not `.all` / `.allTracks`. Glyph list extended (`Icons.Heart`, the chevron glyph + size, the CTA heart). |
| 18 | **missing** | §5: M-24 (picker-card hover/press scale), M-25 (the radio group's focus-visual move), M-26 (the rail's collapse detents). |
| 19 | **missing** | §8: five rules rows that were absent from the port list — `TryStamp`/`UnknownStampFloor`, the five `Is*Lens` predicates + the `LikedLens` flags enum, the whole tempo pass (`TempoSummarize`/`TempoFingerprint`/`TempoValues`/`TempoBandCounts`), `Dominance`/`FactsSummary`/`Summarize`'s caps, and `ContentFilterTags.DeriveCounted`/`Reconcile`/`TagCount`. |
| 20 | **missing** | §9: the chips count EVERY descriptor while the blend counts only `Tags[0]` — same word, two legitimate numbers. |
| 21 | **missing** | §9 traps: the flyout resolves `LibraryStore` from context ALONE where `LikedCoverArt` has a services fallback; the heart's two rules across three call sites; the interned heart vs the minted-epoch pill glyphs. |
| 22 | **missing** | §7 + DATA GAPS: G9 (artist portraits need a `Revalidate` arm — a name-only stub already satisfies Identity, so a plain `Ensure` leaves the pile on initials forever) and G10 (the curated chip fetch's failure grammar: never throws, ETag cache, and an EMPTY result is PUBLISHED so the derived fallback takes over). |
| 23 | **missing** | Nine parity checks (#88–#96) for the states above. |
| 24 | unverified→verified | Every number in §2/§3/§5 was re-derived from source: the 304 canvas and all nine treatments' geometry, both ambient loops (−46 / 92 s; ±888 at 60/74/66 s), the grounds' alphas/centres/radii, the Lens veil's folded 0.2475/0.209, Tone's five spot radii (0.9/0.8/0.85/0.75/0.6 × 0.62), the chrome matrix, the card/pill/legend/tick metrics, the shape ladder's ten constants, `MotionTok` (83/250/333/167) and the four Fluent curves, `Spacing`, `Radii`, `Elevation.Card`, `SparkBars`/`DensityPlot`/`FacePiles`/`WaveePicker`/`RadioButtons` defaults, and all forty-plus loc keys. Everything not listed above was correct as written. |
| 25 | unverified | `LikedHeart.Contour` is asserted to be `ThemedIconData`'s `HeartFill` "character for character". The generated file was not diffed in this pass — the claim rests on the source comment. |
| 26 | **arbitration** | arbitration 2026-09-12: A3 settles the `User.*` file scheme against chapter 15's competing one — pages split by page, helpers by concern (`User.cs`, `User.UI.cs`, `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Liked.cs`, `User.Facts.cs`, `User.Facts.UI.cs`, `User.Cover.cs`, owner O). The header target, the §1.2 homes for the facts bento (`User.UI.cs` → `User.Facts.UI.cs`), §9's "the plan is wrong" item 1, the budget row and the file table are rewritten to it; `User.Page.Profile.cs` and `RouteKind.User` are deleted — verified against 0.2.9: `ShellRoutes.s_exact` has no user/profile route and `ProfileMenu.cs` is a menu, not a page. |
