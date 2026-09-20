# Library rework — the Collection browser (Wavee 0.3)

Written 2026-09-17 against the worktree `C:\WAVEE\wavee-0.3` @ `02f22cce` (branch `feat/0.3-structure`, waves 0–6
landed, the app running live). The visual target is the approved prototype
`docs/plans/wavee/library-rework-mica.html` (published at https://claude.ai/artifact/1pV7cEmVjQgKN5Qtk5ZoUy — the
prototype is the parity reference for this plan the way the 0.2.9 Release build is for the chapters). Style precedent:
`library-artist-jump-and-recents-implementation.md`, `wavee-0.3-flac-implementation.md`.

Every claim about the tree below was read in source on 2026-09-17; the 0.2.10 mechanisms are quoted from the shipped
app in `C:\wavee\waveemusic` so the reader knows which of them still exist in 0.3 and which do not.

Rules baked in: **no legacy paths** (the three-column artists layout, the discography grid pane, the "View full album"
button and the `MinifiedAlbum` notice on this surface are *deleted*, not hidden); **derived facts live on the model**
(the letter groups, the reader's block order and extents, the pane's three-state readiness are pure rules in CORE
with tests — the UI renders them); **no page-side fetch windows** (every pane demands its whole model; the runner
batches 300/POST); **no environment switches**; **no source-text tests**; **props freeze at mount** (every changing
input is a re-pushed props record or a Signal instance); **every fix references its issue** (§12);
**subagents never build, test, or touch git** — the orchestrator runs one Debug build, one Release build and one test
run per wave (memory note `0.3 model split and minimal gates`).

---

## 0. The decision, in one screen

| | 0.3 today (`User.Page.Library.cs`, 1,274 lines) | After this plan |
|---|---|---|
| Albums | navigator (340) │ `LibraryAlbumPane` (embedded `Track.Table`, Plays lane when wide, "View full album", the minified notice) | navigator (340) │ **`Album.Pane`** — one anatomy at every width: cover 128, eyebrow, title, artist, meta, Play · shuffle · ♥ · ⋯, **Open album ↗**; `#`/title/♥/⏱ only; counted shimmer; a real failure strip; "Also by … in your library" |
| Artists | navigator (280) │ `LibraryArtistPane` grid (440, floor 300) │ `LibraryAlbumPane` (floor 220) | navigator (280) │ **`Artist.Reader`** — artist band · word rail (*in your library* / *all releases* · newest / oldest / a–z) · a 36-px **cover spine** · the albums stacked as **art + tracks blocks**, one scroll, virtualized per block |
| Podcasts | navigator │ `LibraryShowPane` | unchanged (the show pane stays in `User.Page.Library.cs` §4) |
| Sort | pill + flyout (`SortViewPill` / `SortPanel`) | **word rail** (Zune's text pivot: active 100 %, others 50 %, accent underline; tap the active word to flip direction) + list/grid icons; size S/M/L in a trimmed `ViewPanel` |
| Alphabetical | flat list | **letter groups** (sticky letter overlay) + an **A–Z jump strip** on the column's edge |
| Artist rows | subtitle is the literal word "Artist" | "3 albums · 34 songs" |
| Collapsed (< 640) | depths 0/1/2 | depths 0/1 (the reader is one pane) |
| Persisted | 12 `library.<kind>.*` keys | the same 12 (byte-identical strings) **+ 1**: `library.artists.scope` |

What does **not** change: the master-detail contract (selection re-skins in place, never navigates — ch 15 §0.1),
the three rungs separated by the 16-DIP grip hairline (§0.2), the title inside the toolbar (§0.3), ctor-seeded
persistence (§0.4), the `view:size:OrderKey:FactsKey` remount rule (§0.6), Recents = played (§0.7), the
accent-neutral surface (§0.8 — the prototype's art wash stays a toggle **off**, not built here), search mode
(§0.9–§0.10, untouched: `LeftSearchBody`, `SearchArtistColumns`, `SearchAlbumDetail` keep their three-column shape
under the same `MidW` grip), and the collapse breakpoint (§0.11).

---

## 1. Context

### 1.1 What 0.3 already has (read before writing a line)

`src/apps/Wavee/Entities/User.Page.Library.cs` — six sections: **§1** the factory (`LibraryPageFor`, `NavShape`,
`PaneProps(int Slot)`, `KeyOf`/`SlotOfKey`), **§2** `LibraryPage : Component` (the persisted signals, `ComputeShape`
over `LibraryNavSorter<LibraryRows>` into pooled `int[]` buffers, `DemandEdge`/`DemandRows`, `SyncNav` with the
`_syncingSel` guard, `LeftColumn`/`Toolbar`/`ListBody`, `DetailColumn`, `ArtistColumns`, `Collapsed`, the search
mode), **§3** `LibraryAlbumPane` (`:773-860`), **§4** `LibraryShowPane`, **§5** `LibraryArtistPane` (`:980-1178`),
**§6** `PaneHero` (104 cover) / `PaneActions` / `PaneSkeleton`.

`src/apps/Wavee/Entities/User.UI.cs` (578) — `SortLabelKey`/`ViewGlyph`/`IsGridView`/`IsCompactView`,
`SortViewPill` + `SortPanel` (`SortPillHost`/`SortPanelHost`), `CrumbBar`, `ColumnGrip`, `NavPanel`/`ReadingPane`,
`GoToArtistLink`, the bound `NavRow`/`NavCard`/`DiscoRow`/`DiscoCard` (+ `TitleText`, `DiscoSubtitle` through a
hoisted `FormatCache<int>`, `ArtBox`, `FillArt`, `DragOf`, `BorderChrome`), the search rows, `LibrarySeam`.

`src/apps/Wavee/Entities/User.cs` — CORE: `LibraryLayoutBreakpoints` (`:602`), `LibrarySelectionCommit` (`:621`),
`LibraryNavSort` (`:654`), `LibraryNavSorter<TRows>` (`:686`), `LibraryNavOrder` (`:758`), `LibraryRows` (`:807`:
`IdOf`/`TitleOf`/`SubtitleOf`/`ImageOf`/`YearOf`/`FillPlayed`/`Filter`), `LibraryHits` + `SearchLibrary` (`:933`,
`:1008`); the edges surface `User.Me.SavedAlbumSlots` / `FollowedArtistSlots`, `Saves(Album)`, `Follows(Artist)`,
`AddedAt(kind, slot)`.

The data layer the panes bind to (all real): `Album` (`Title`, `ImageId`, `Year`, `TrackCount`, `Kind`,
`TrackSlots` = `Edges.AlbumTracks.Targets`, `ArtistSlots` = `Edges.AlbumArtists.Targets`, `Knows(AlbumFields)`),
`Track` (`Title`, `DurationMs`, `Knows(TrackFields)`), `Artist` (`Name`, `ImageId`, `Uri`, `Knows(ArtistFields)`),
the three facet edges `Edges.ArtistAlbums/ArtistSingles/ArtistCompilations` with
`Artist.DemandDiscography/DemandFacet/DemandNextPage` (`Artist.Discography.cs:482-527`), `Entities.Ensure(handle,
Fields)` / `Ensure(Table, ReadOnlySpan<int>, uint, FetchPriority)` / `EnsureEdge(FetchEdge, parent)` /
`RefreshEdge(FetchEdge, parent)`, `EdgeTable.State/IsFailed/Count/Total`, `Fetch.MaxUrisPerRequest = 300`.
`FetchEdge.AlbumTracks` at offset 0 rides the batched `AlbumV4` metadata route (`Fetch.Routes.cs:373`) — demanding
forty albums' track lists is one or two POSTs, not forty calls.

The row every short list uses: `Track.EagerRow(Track t, int displayIndex, in ColumnSet set, TrackSize[] tracks,
float rowH, Action onPlay, in EagerRowOptions o)` (`Track.UI.cs:950`), with the drawer-child presets in
`Recents.UI.cs:44-57` (`ChildCols`, `ChildTracks`, the no-art twins). The detail table: `Track.Table(new
TableArgs { Source, Profile, ShowToolbar, Embedded, ScrollKey })`; `Detail.Config` carries `ShowPlays` and
`HasTrailing` (`Detail.cs:893-911`); `Embedded` already forces `HasTrailing = false` (`Track.Table.cs:361`).

The loc constants (`Strings.Library.*`) are **generated** from `src/apps/Wavee/assets/loc/en-US.json` by the engine's
`LocalizationKeysGenerator`; a new key is a JSON edit (+ `ko-KR.json`, `nl.json` — untranslated values fall back to
`en-US`, but the keys must exist so the generator sees one set).

### 1.2 The three defects, traced

**A. "Skeleton rows forever" (0.2.10).** One pane, `LibraryDetailPane`, mounted twice. Both mounts run the same
`LoadDetail` → `GetAlbumAsync(uri, Rich)`; the Albums page reads its album at selection time (usually the persisted
one, before anything warmed it). That first Rich ask either names the disc rows or, on one network blip, seals the
album's Rich rung "exhausted" for 24 h (`OpenPolicy.cs:94`), and the seal skips `AlbumHydration.ContinueAsync`
wholesale — the very step that names the rows (`AlbumHydration.cs:85-90` calls the symptom by name). The pane never
asks the Full rung (only the full page's trailing band does, `DetailTrailing.cs:166-170`) and never re-projects on a
store change (no `DetailLiveRefresh`, no `Changes.Subscribe`). *In 0.3 this mechanism does not exist*: the pane
demands `AlbumFields.Detail`, then the `AlbumTracks` edge, then `TrackFields.Row` for every track slot
(`User.Page.Library.cs:788-802`), re-rendering on the tables' `Changed` signals. **What remains in 0.3** is the
missing failure arm (`:821` returns `PaneSkeleton()` for anything but ready — ch 15 W24, still open), and the
`MinifiedAlbum` notice as the answer to an unnamed row instead of a retry.

**B. "Empty album tiles" (0.2.10).** The grid is `Artist.TopAlbums`, gid-only stubs until the Open stub batch runs;
`HydrationLevels.Of(Artist)` samples only `TopAlbums[0]` (`HydrationLevel.cs:96`) and `ArtistDiscography.Assemble`
sorts any hydrated card to index 0 (`ArtistDiscography.cs:29`), so one resident album makes the artist read "Open",
the batch never runs, and every other tile paints as a bare card with the caption collapsing to "ALBUM". *In 0.3 the
grid reads the three facet edges and `DemandFacet` asks `AlbumFields.DiscoCard` for every listed release* — the
index-0 predicate is gone. **What remains in 0.3** is the shape: the third-of-three column (440 with a 300 floor,
tracks pane floored at 220) and a loading gate that paints the whole skeleton until *any* facet answers.

**C. "Two album pages" (0.2.10 and 0.3 alike).** One component whose look diverged by width: `Plays` is the first
lane the relief ladder yields (`DetailTrackTableRules.cs:146`; 0.3: `Track.Rules.cs:253`), so it survives in the
784-px Albums pane and vanishes in the 388-px Artists column; the notice appears only when rows are gid-only. 0.3
inherits this exactly because `LibraryAlbumPane` mounts `Track.Table` with `Detail.Config.For(DetailKind.Album, …)`
— `ShowPlays` true.

**D. Small things the prototype fixed that are worth naming.** The artists navigator subtitle is the literal loc
constant `search.typeArtist` (ch 15 §7 gap 10); the reader's "N albums · M songs" replaces it. A filtered-to-nothing
navigator drops the selection (kept). `Album.Notice` / `MetaLineId` are probed per render (ch 15 §7 gaps 6–7,
a recorded deviation from `derived-facts-live-on-the-model`) — this plan does not fix those two; it removes the
notice's only consumer on this surface (§5.5) and leaves `Detail.Identity.For` as is.

---

## 2. Wireframes

All at window 1440, sidebar 280, rail closed → content 1140. Tokens per ch 15 §3 unless stated. `┃` = the bound
AccentPill selection indicator (3 × 16, r 1.5). The grip is the 16-DIP strip with the centred 1-DIP hairline.

### W1 — Albums, wide, loaded (leftW 340 · grip 16 · pane 784)

```
├────────────────── 340 ──────────────────┤│├──────────────────────────── 784 ───────────────────────────────────┤
┌─────────────────────────────────────────┐│┌─────────────────────────────────────────────────────────────────────┐
│ FillLayerDefault                        │││ FillCardDefault                                                     │
│  Albums  42            PageHero 28/36/600│││  ┌ pad 20,20,20,12 ─────────────────────────────────────────────┐  │
│          ↑ count 14/400 TextTertiary    │││  │ ┌────────┐  gap 18 · AlignItems End                           │  │
│  recents  a–z  artist  added  year  ≡ ▦ │││  │ │        │  ALBUM · 2013                Eyebrow 12/16/600     │  │
│  ‾‾‾‾‾‾‾ ← 2-DIP AccentDefault underline│││  │ │ 128×128│  racine carrée                28/36/600, 2 lines   │  │
│  active 100 % · others 50 % · 13.5/400  │││  │ │  r 6   │  Stromae                      14/20/600 (link)      │  │
│  ┌────────────────────────────────────┐ │││  │ │ shadow │  13 songs · 45 min · saved 2024   12/16 Tertiary   │  │
│  │🔍 Filter                    h 32   │ │││  │ └────────┘                                                     │  │
│  └────────────────────────────────────┘ │││  └────────────────────────────────────────────────────────────────┘  │
│  ┌ ItemsView.CreateBound · Extents ───┐ │││  ( ▶ Play )  ( ⤨ )  ( ♥ )  ( ⋯ )                    Open album ↗    │
│  │ ▣ rosie                  row 56    │ │││   36 h accent   36 circ Subtle ×3                   Subtle/13 link  │
│  │   ROSÉ · 2024            12/16 2nd │ │││  ┌ Track.Table (Embedded, ShowPlays=false) ─────────────────────┐  │
│  │ ▣ [05]                             │ │││  │  #   Title                                          ♥     ⏱   │  │
│  │   Urban Zakapa · 2018              │ │││  │  1   Ta fête                                             2:56 │  │
│  │ ┃▣ racine carrée   SELECTED  ◉ now │ │││  │ ▍▍  Papaoutai                    (now: accent + equalizer)3:52 │  │
│  │   Stromae · 2013                   │ │││  │  3   Bâtard                                              3:29 │  │
│  │ ▣ Arcane League of Legends: Se…    │ │││  │  …                                                            │  │
│  │   League of Legends · 2024         │ │││  └───────────────────────────────────────────────────────────────┘  │
│  │ …                                  │ │││  ALSO BY STROMAE IN YOUR LIBRARY                 Eyebrow           │
│  └────────────────────────────────────┘ │││  [96 Multitude 2022] [96 Cheese 2010]            → selects in nav  │
└─────────────────────────────────────────┘│└─────────────────────────────────────────────────────────────────────┘
```

The album row keeps 0.3's `NavRow` (40 art, title 14/20/600, subtitle 12/16) — the subtitle gains "· year" through
`LibraryRows.SubtitleOf` (already "artist" for albums; the year is appended in §5.2, cache-keyed).

### W2 — Albums navigator, sort = a–z (letter groups + the jump strip)

```
┌───────────────────────────────────────────────┐
│  Albums  42                                   │
│  recents  a–z  artist  added  year      ≡ ▦   │
│           ‾‾‾                                 │
│  🔍 Filter                                    │
│ ┌────────────────────────────────────┐ ┌───┐  │
│ │ [A]  ← sticky letter overlay, 28 h │ │ # │  │  the strip: 27 rows, 9.5/400 TextTertiary, 18 wide,
│ │ ▣ Affirmation                      │ │ A │  │  present letters at 100 %, absent at 30 %, the letter
│ │   Savage Garden · 1999             │ │ B │  │  under the viewport top in AccentTextPrimary/700
│ │ ▣ ARE YOU EVER COMING BACK?        │ │ C │  │
│ │   vaultboy · 2022                  │ │ … │  │  click → StartBringItemIntoView(headerFlatIndex, 0f)
│ │ ▣ Arcane League of Legends: Se…    │ │ R │  │  unanimated (ch 15 §6: a jump is a jump)
│ │ [B]  ← inline header row, 28 h    │ │ S │  │
│ │ ▣ …But Seriously                   │ │ T │  │  letter of "…But Seriously" = B (leading punctuation
│ │   Phil Collins · 1989              │ │ … │  │  and a leading "the " are skipped — LibraryLetters.Of)
│ └────────────────────────────────────┘ └───┘  │
└───────────────────────────────────────────────┘
```

The header row is a flat item of its own (`ContentType 1`), 28 DIP: a 12/600 TextTertiary letter in a small
`FillCardDefault` plate with the `StrokeCardDefault` hairline, pad 10/8/4. Rows keep 56 (list) / 40 (compact); grid
views have no letters (the strip stays, jumping to the first card of the letter).

### W3 — Artists, wide: navigator (280) │ the reader (844)

```
├──────────── 280 ─────────────┤│├────────────────────────────────── 844 ──────────────────────────────────────┤
┌──────────────────────────────┐│┌────────────────────────────────────────────────────────────────────────────┐
│  Artists  38                 │││ ┌ band · pad 20,20,20,8 · gap 16 · AlignItems Center ──────────────────┐   │
│  recents  a–z  albums   ≡ ▦  │││ │ (72 circ) Stromae                          32/38/600 (link → artist) │   │
│  🔍 Filter                   │││ │           In your library: 3 albums · 35 songs · following  12/16 3rd │   │
│  ┃◉ Stromae                  │││ │           ( ▶ Play all ) ( ⤨ ) ( ✓ following ) ( ↗ )                 │   │
│     3 albums · 35 songs      │││ └────────────────────────────────────────────────────────────────────────┘   │
│   ◉ Urban Zakapa             │││ ┌ sub-rail · sticky 0 · pad 6,20,10 · hairline below ───────────────────┐   │
│     2 albums · 15 songs      │││ │  in your library   all releases · 6          newest  oldest  a–z       │   │
│   ◉ Jukjae                   │││ │  ‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾                             ‾‾‾‾‾‾                    │   │
│     2 albums · 9 songs       │││ └────────────────────────────────────────────────────────────────────────┘   │
│   ◉ ROSÉ                     │││ ┌ spine 56 ┐┌ ItemsView.Create · Extents(blockOf) ──────────────────────────┐│
│     1 album · 8 songs        │││ │ [36]  ◄─ ││ ┌ block: pad 16,20,8,16 · grid 120 | 1fr · gap 16 ────────────┐ ││
│   …                          │││ │ [36]     ││ │ ┌──────┐  Multitude                 20/600   3 songs · 10 min│ ││
│                              │││ │ [36]     ││ │ │ 120  │                            ( ▶ )( ♥ )( ⋯ ) 32 circ │ ││
│                              │││ │          ││ │ │  r 6 │   1  Invaincu                                  3:07 │ ││
│                              │││ │ sticky   ││ │ └──────┘   2  Santé                                     3:10 │ ││
│                              │││ │ top 52   ││ │  2022 ·    3  La solassitude                            3:24 │ ││
│                              │││ │          ││ │  Album     …   Track.EagerRow, rowH 36, ChildCols        │ ││
│                              │││ │          ││ ├ hairline StrokeDividerDefault ───────────────────────────────┤ ││
│                              │││ │          ││ │ ┌──────┐  racine carrée              2013 · Album           │ ││
│                              │││ │          ││ │ …                                                            │ ││
│                              │││ └──────────┘└──────────────────────────────────────────────────────────────┘│
└──────────────────────────────┘│└────────────────────────────────────────────────────────────────────────────┘
```

The spine is the block jump list: one 36×36 cover per block (r 4), opacity .5 → .9 hover → 1 + a 2-DIP accent
ring for the block under the viewport top (scroll-spy through `ItemsViewController.TryGetItemIndex(0, 0.2)` on
`OnScrollGeometryChanged`); click → `StartBringItemIntoView(i, 0f, animate: true)`. Under 1200 px of reader width the
spine hides and the block cover drops 120 → 88 (W6).

### W4 — The reader, scope = all releases, catalogue blocks landing

```
│  in your library   all releases · 9          newest  oldest  a–z       │
│                    ‾‾‾‾‾‾‾‾‾‾‾‾‾‾                                       │
│ [36] │ ┌──────┐  Multitude                    2022 · Album  (saved)  │   saved blocks first, in the sort;
│ [36] │ ┌──────┐  racine carrée                2013 · Album  (saved)  │   catalogue blocks after, same sort
│ [36] │ ┌──────┐  Cheese                       2010 · Album  (saved)  │
│ [··] │ ┌ ▒▒▒▒ ┐  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒               ▒▒▒▒ · Album           │   a catalogue block whose DiscoCard
│      │ │ ▒▒▒▒ │    ▒  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒               ▒:▒▒                │   has not landed: a COUNTED skeleton
│      │ └──────┘    ▒  ▒▒▒▒▒▒▒▒▒                    ▒:▒▒                │   (TrackCount rows when known, else 4),
│      │             ▒  ▒▒▒▒▒▒▒▒▒▒▒▒                 ▒:▒▒                │   never a bare card — Skel.Region
│ [36] │ ┌──────┐  Racine carrée Live            2015 · Album           │
│      │ …                                                               │
│      │  Fetching 3 more releases…   (facet Partial → the tail of the   │   the loadmore line is the facet's own
│      │                               realized window drives DemandNextPage)│  Partial state, not a button
```

### W5 — The pane's three states (Album.Pane)

```
 LOADING (identity known, tracks not answered)      FAILED (edge.IsFailed or rows still unnamed after Complete)
 ┌───────────────────────────────────────────┐      ┌───────────────────────────────────────────┐
 │ [128] ALBUM · 2013                        │      │ [128] ALBUM · 2013                        │
 │       racine carrée                       │      │       racine carrée                       │
 │       Stromae · 13 songs                  │      │       Stromae · 13 songs                  │
 │ ( ▶ Play ) ( ⤨ ) ( ♥ ) ( ⋯ )   Open ↗     │      │ ( ▶ Play ) ( ⤨ ) ( ♥ ) ( ⋯ )   Open ↗     │
 │  #  Title                          ⏱     │      │ ┌ Controls.Vacancy(Error, Compact) ─────┐ │
 │  ▒  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒          ▒▒▒▒         │      │ │ Couldn't load the songs.   [ Retry ]  │ │
 │  ▒  ▒▒▒▒▒▒▒▒▒▒                ▒▒▒▒       │      │ └───────────────────────────────────────┘ │
 │  … × 13 (TrackCount) — Track.ShimmerRow  │      └───────────────────────────────────────────┘
 └───────────────────────────────────────────┘
 NO SELECTION: Controls.Vacancy(Empty, Compact, "Pick an album", "Its songs show here.") — unchanged glyph.
```

The header is painted from the navigator's own row facts (`Title`, `ImageId`, `Year`, `TrackCount`, `ArtistSlots`
— all `AlbumFields.Identity`, which the navigator already demanded), so it never shimmers as a whole; only the rows
do. That is the rule that kills "skeleton forever": the pane has nothing it can wait for that the list did not
already have.

### W6 — Medium (window 1120): the reader without the spine; W7 — collapsed (< 640): crumbs

```
 W6 reader @ 640 wide                                W7 depth 0            W7 depth 1 (artists)
 ┌───────────────────────────────────────┐          ┌──────────────┐      ┌────────────────────────┐
 │ (72) Stromae            ( ▶ )( ⤨ )(✓) │          │ Artists ›    │      │ ‹ Artists › Stromae    │
 │ in your library  all · 6   newest a–z │          │ recents a–z  │      │ (72) Stromae           │
 │ ┌────┐ Multitude          2022 · Album│          │ 🔍 Filter    │      │ in your library  all   │
 │ │ 88 │  1 Invaincu             3:07   │          │ ◉ Stromae    │      │ ┌──┐ Multitude         │
 │ └────┘  2 Santé                3:10   │          │ ◉ Urban Z…   │      │ │88│  1 Invaincu  3:07 │
 └───────────────────────────────────────┘          └──────────────┘      └────────────────────────┘
```

Depth 2 is gone for artists (the reader is the leaf); albums keep 0/1. `LibrarySelectionCommit.ForAlbum`'s
`Depth` for the artists view becomes 1 (§5.1 — a one-line rule change with its test).

### W8 — The word rail's four states

```
   rest          hover          active                 active, desc (tap the active word again)
   recents       recents        recents                recents ⌄
   50 % ink      85 % ink       100 % · 600            100 % · 600 · a 10-px chevron after the word
                                ‾‾‾‾‾‾‾ 2 DIP accent   ‾‾‾‾‾‾‾‾‾
```

13.5/400 `TextPrimary` with `Opacity` .5 / .85 / 1 (a bound `Opacity`, compositor-only, `Design.Motion.Faster`
83 ms), the underline a 2-DIP `AccentDefault` box under the active word, gap 14, height 32. Focus ring: the
engine's default on each word's `BoxEl` (`Focusable`, `Role = Button`). Keyboard: Left/Right move between words.

---

## 3. Component trees

### 3.1 Albums

```
User.LibraryPage (kind = albums)                      keyed "library:albums", KeepAlive slot
├─ root BoxEl (OnBoundsChanged → _collapsed)
└─ WIDE row
   ├─ LeftColumn = NavPanel(Width = LeftW)
   │   ├─ Toolbar
   │   │   ├─ title row: PageHero("Albums") + count (TextTertiary 14, bound to _shape.Count)
   │   │   ├─ User.WordRail(words, Sort, Desc) + view icons (View) + ViewPanel button (View, Size)
   │   │   └─ AutoSuggestBox (Filter)                                  ← unchanged
   │   └─ ListBody
   │       └─ ZStack
   │           ├─ ItemsView.CreateBound<LibraryNavItem>(_items, NavSlot, RepeatLayout.Extents(_extentOf), _navOptions)
   │           │     ContentType = it.Slot < 0 ? 1 : 0 · IsItemEnabledTyped = it.Slot > 0 · OnScrollGeometryChanged
   │           ├─ StickyLetter overlay (only when letters != null)     ← Recents' StickyDayHeader idiom
   │           └─ (right edge) User.JumpStrip(letters, _jump)
   ├─ ColumnGrip(LeftW, 240, 560)                                     ← unchanged
   └─ ReadingPane("lib:detail")
       └─ Album.Pane  ← Embed.Comp(new Album.PaneProps(slot, showAlsoBy: true), static () => new Album.Pane())
           ├─ Album.PaneHeader(a)            cover 128 · eyebrow · title link · artist link · meta
           ├─ Album.PaneCommands(...)        Play · shuffle · SaveButton · MoreButton · "Open album ↗"
           ├─ rows: Track.Table(Embedded, ShowToolbar=false, Config with { ShowPlays=false, HasTrailing=false })
           │        | counted Track.ShimmerRow × TrackCount | Controls.Vacancy(Error, Compact, Retry)
           └─ Album.AlsoByStrip(a)           96-px tiles of the OTHER saved albums by the first billed artist
```

### 3.2 Artists

```
User.LibraryPage (kind = artists)
└─ WIDE row
   ├─ LeftColumn (as above; words = recents · a–z · albums)
   ├─ ColumnGrip(LeftW, 240, 560)
   └─ ReadingPane("lib:reader")
       └─ Artist.Reader  ← Embed.Comp(new Artist.ReaderProps(artistSlot, Scope, RSort, RDesc), static () => new Artist.Reader())
           ├─ ArtistBand(a)                  72 avatar · name link · "In your library: …" · Play all · ⤨ · FollowButton · ↗
           ├─ SubRail (sticky, Fill Card)     User.WordRail(scope words) · spacer · User.WordRail(newest/oldest/a–z)
           └─ HStack
               ├─ Spine (Width 56, sticky)   Flow.For over the shape's blocks → 36-px covers, bound ring on _current
               └─ ItemsView.Create(shape.Count, BlockAt, RepeatLayout.Extents(_extentOf, 320f), _options)
                     ├─ Block(i) — grid 120 | 1fr: cover + "year · kind" | head (title link, meta, ▶ ♥ ⋯) + rows
                     │     rows = Track.EagerRow(track, n, ChildCols, ChildTracks, 36, onPlay, o) × n
                     │     | counted skeleton (Skel.Region over the same block builder)
                     │     | failed block: the head + a 2-row Retry note (Artist.DrawerVerdictFor's Retry voice)
                     └─ trailing item: the facet Partial line ("Fetching N more…") | nothing
```

### 3.3 Where the code lives (the owner split)

| File | Change | Owner |
|---|---|---|
| `Entities/User.cs` | **CORE, new:** `LibraryLetters` (§5.1), `LibraryWordRail` codes (§5.1), `AlbumPaneReadiness` (§5.1), `LibraryAlbumsOf(artist)` (§5.1); `LibrarySelectionCommit.ForAlbum` depth 2 → 1 | **O1** (Opus) |
| `Entities/User.UI.cs` | **new:** `WordRail`, `LetterHeader`, `StickyLetter`, `JumpStrip`, `ViewPanel` (the trimmed `SortPanel`); `NavRow` artist subtitle → counts, album subtitle → "artist · year"; **delete** `DiscoRow`/`DiscoCard`/`SortViewPill`/`SortPanel` and their hosts | **O2** (Sonnet) |
| `Entities/User.Page.Library.cs` | §2 rework: letters in `ComputeShape`, the flat projection + `Extents` + sticky + strip in `ListBody`, `DetailColumn` → `Album.Pane`, `ArtistColumns` → `ReaderColumn`, `Collapsed` depths, `MidW` kept for search only; **delete §3 `LibraryAlbumPane`, §5 `LibraryArtistPane`, §6 `PaneHero`/`PaneActions`/`PaneSkeleton`**; §4 `LibraryShowPane` stays and takes its hero from `Album.PaneHeader`'s show twin (`Show.PaneHeader`, a 30-line static in `Show.UI.cs`, owner M) | **O3** (Opus) |
| `Entities/+Album.Pane.cs` | **NEW named partial** (`public readonly partial struct Album`): `PaneProps`, `Pane`, `PaneHeader`, `PaneCommands`, `AlsoByStrip` (§5.5) | **M** (Opus) |
| `Entities/+Artist.Reader.cs` | **NEW named partial** (`public readonly partial struct Artist`): `ReaderProps`, `Reader`, `ReaderShape` (CORE section, §5.6), `Block`, `Spine`, `ArtistBand` (§5.7) | **N** (Opus) |
| `Entities/Show.UI.cs` | `Show.PaneHeader` (the 104 → 128 twin of `Album.PaneHeader`, publisher line) | **M** |
| `Platform/Platform.cs` | `Keys.LibraryScope(kind)` → `"library." + kind + ".scope"`, int, default 0 (§5.9) | **O1** |
| `assets/loc/{en-US,ko-KR,nl}.json` | the 11 keys of §5.10 | **O2** |
| `Wavee.Tests/LibraryLettersTests.cs`, `AlbumPaneReadinessTests.cs`, `LibraryWordRailTests.cs`, `ArtistReaderShapeTests.cs`, `LibrarySelectionCommitTests.cs` (one changed fact) | pure-rule tests (§8) | **O1** / **N** (reader shape) |
| `Entities/Entities.Fake.Library.cs` | the seed must give ≥ 2 artists with ≥ 2 saved albums and ≥ 1 catalogue-only release each, so `--fake` renders W3/W4 loaded (§9) | **Q** (Sonnet, 40 lines) |
| `docs/plans/wavee/wavee-0.3-ui/15-library.md` | the §11 amendments (§10 here) | orchestrator |

Disjoint by construction: O1 = `User.cs` + `Platform.cs` + tests; O2 = `User.UI.cs` + loc; O3 = `User.Page.Library.cs`;
M = `Album.Pane.cs` + `Show.UI.cs`; N = `Artist.Reader.cs` + its test; Q = the fake seed. The contracts between them
are the props records and static signatures frozen in §5 — an agent that needs to change one stops and reports
instead of editing a file it does not own.

---

## 4. Interaction contract

**Navigator.** Single click selects (re-skins the pane in place); double click on an album row plays it
(`Playback.PlayContext(a.Id)`); Enter = select; typeahead over titles (kept); arrows skip letter headers
(`IsItemEnabledTyped`). Drag source unchanged (`DragOf`). Right-click: unchanged (none on this row today — not added).

**Word rail.** Tap a word → `Sort.Value = code; Desc.Value = false`. Tap the active word → `Desc.Value = !Desc`. The
codes are the persisted `LibraryNavSort` ints (never renumbered): albums rail = `recents 0 · a–z 2 · artist 3 ·
added 1 · year 4`; artists rail = `recents 0 · a–z 2 · albums` — **"albums" is a NEW sort code 5** (by saved-album
count desc, then title), appended to `LibraryNavSort` (persisted enums append, never move) and to
`LibraryNavSorter`'s comparator table (§5.1). Podcasts rail = `recents 0 · a–z 2 · added 1`.

**Letters.** Present only while `Sort == Alphabetical` in a list view. The strip is present whenever `Sort ==
Alphabetical` (list or grid). A letter with no rows is inert (30 % ink, no cursor).

**Album.Pane.** Title and "Open album ↗" → `Shell.GoTo(Shell.For(a.Uri, a.Title))` (the full page). Artist link →
`Shell.GoTo(Shell.For(artist.Uri, name))` — it does **not** switch the library to the artists view (that is a
different page's selection; the prototype's cross-jump is dropped: a route is a route). Play → `PlayContext(a.Id)`;
shuffle → `SetShuffle(true)` then play; ♥ → `Controls.SaveButton`; ⋯ → `Album.MoreMenu(a, overlay)` through `Detail.UI.MoreButton`
(the same menu the full page's hero raises). Rows: the embedded `Track.Table`'s own contract (ch 04). Also-by tile
click → `SelectedKey.Value = KeyOf(Album, slot)` through the page's `OnAlsoBy` callback in the props (select in place,
the one library-internal jump that stays).

**Artist.Reader.** Band: name → the artist page; Play all → `PlayContext(a.Id)` (the artist context); shuffle; the
`FollowButton`; ↗ → the artist page. Sub-rail: `Scope` 0 = in your library, 1 = all releases (persisted per kind);
`RSort` = the reader's own sort signal (persisted as `LibraryAlbumSort("artists")`, codes: 0 newest, 1 oldest,
2 a–z — a **reader-local** enum `ReaderSort`, NOT `LibraryNavSort`; the key's old discography values 0–4 clamp to
0). Block: title → the album page; ▶ → `PlayContext(album.Id)`; ♥ → `SaveButton`; ⋯ → the album menu; a row →
`Track.EagerRow`'s single-click play `PlayContext(album.Id, track.Id)`; the row's ♥ and ⋯ are the eager row's own.
Spine: click → bring the block to the top, animated (`ScrollAnimate.Glide` through `StartBringItemIntoView(i, 0f,
animate: true)`).

**Collapsed.** Albums: depth 0 navigator, depth 1 the pane. Artists: depth 0 navigator, depth 1 the reader. The
crumb root names the kind; level 1 names the selection. Search select-in-place: `LibrarySelectionCommit.ForArtist`
depth 1 (unchanged); `ForAlbum` in the artists view: depth 1 (was 2) and `AlbumKey` is **still written** — the reader
reads it once as its initial spine target (§5.7 `_initialAlbum`), then clears it.

**Splitters.** `LeftW` 240–560 (unchanged). `MidW` is used only by search mode's three columns (unchanged floors).

---

## 5. The code

Namespaces/usings as in the files quoted in §1.1. Every delegate a component owns is allocated once in its
constructor; every changing input is a re-pushed props record or a `Signal` instance; no LINQ, no per-render arrays
except where a bounded, explicitly-noted allocation happens at a *selection* edge.

### 5.0 Verified names (read in source on 2026-09-17 — the code below binds to these, not to guesses)

| Used as | Is | Where |
|---|---|---|
| `Loadable<T>.Pending()` / `.Ready(v)` / `.Failed(e)` | static factories on the sealed class | engine `Foundation/Signals/Loadable.cs:32-36` |
| `Skel.Region(loadable, Func<Element> shimmerSource, Func<T,Element> content, SkelReveal reveal = Soft, …)` | the three-arg overload | engine `Hooks/SkeletonRegion.cs:96` |
| the hero/rail "⋯" | `Detail.UI.MoreButton(Func<ContextMenuModel?> menu, float size, float glyph, bool round)` — private today, made `internal` by M | `Entities/Detail.UI.cs:1536` |
| the album's ⋯ menu model | `Album.Page.MoreMenu()` (Add to playlist ▸ · Play next · Add to queue, built at open) — lifted to `Album.MoreMenu(Album, IOverlayService?)` | `Entities/Album.Page.cs:387-408` |
| `Controls.MoreButton(Action?, bool requestsContext, float restOpacity)` | the ROW glyph, not a menu host — not used here | `Platform/Controls.cs:906` |
| the "songs · duration" line | `Detail.Text.AlbumMeta(int trackCount, long totalMs, bool durationsKnown, int year)` / `Detail.Identity.For(a).Meta` | `Entities/Detail.cs:1016`, `Detail.UI.cs:128-151` |
| a failed edge's strip | `Controls.Vacancy(VacancyVoice.Error, VacancyScale.Compact, onAction:)` — the voice supplies its own copy and Retry label | `Entities/Artist.Discography.cs:657-659` |
| `LibraryEdge.AddedAt` | UNIX **seconds** | `Entities/User.cs:28` |
| a per-row fetch failure | **does not exist** (`Table.Known` only; `Fetch.Failed` at `Fetch.cs:1019` un-asks) — O1 adds `Table.Failed` (§5.5 note) | `Entities/Entities.cs:962` |
| `Track.EagerRow(t, i, in ColumnSet, TrackSize[], rowH, onPlay, in EagerRowOptions)` | the short-list row (a component per row) | `Entities/Track.UI.cs:950` |
| `Detail.Config.ShowPlays` / `HasTrailing` | record fields; `with { … }` | `Entities/Detail.cs:893-911` |
| `Icons.More` / `Play` / `ChevronDown` / `OpenInNewWindow` / `Shuffle` / `ViewList` / `ViewGrid` | generated glyph table | engine `Icons.Glyphs.g.cs` |
| `Shell.For(EntityUri, name)` + `Shell.GoTo(Route)` | the route factory + navigation | `Shell/Shell.cs:386` |
| `ScrollBinds = [new() { PinTop = … }]` / `el.Sticky(top)` | containing-block-clamped `position:sticky`. `BakeScrollBinds` resolves the nearest **`Scrollable` ancestor** — an `ItemsView` viewport qualifies — and re-bakes wholesale on every re-render, so a recycled slot self-cleans; `ApplyPin` then writes one change-gated `LocalTransform` per frame, clamped to `parentH − nodeH`, with no allocation. Two consequences the reader lives by (§5.7 note 2): the pin's IMMEDIATE parent must be taller than the pinned node — so a sticky root returned from a *component* can never pin, because the component anchor is layout-transparent and mirrors its child's size (`MirrorParticipation`) — and a pinned list ITEM must sit in the list's `PersistentPrefixCount` | engine `Dsl/ScrollBindDsl.cs:47`, `:130` (`ScrollRecipes.Sticky`), `Animation/ScrollBindEval.cs:212` (`ApplyPin`), `Reconciler.cs:4652` (`BakeScrollBinds`) |
| `ListOptions.PersistentPrefixCount` + `ScrollOptions.ItemClipTopInset` / `ItemClipTopFadeBand` | the pinned-chrome pair: the first N items stay mounted, are never recycled and are never clipped; everything after them is guillotined by ONE shared band at the inset with a single feather — never a per-row clip. Both are **BOUND-path only** (`ItemsView.CreateBound`): the unbound recycler forces the prefix to 0 (`Reconciler.cs:3176`) and the reconciler writes `NaN` for the inset when `RowBind` is null (`Reconciler.cs:5601`) | engine `Controls/ListOptions.cs:169-176, :385`; reference wiring `Entities/Track.Table.cs:1094-1127` |
| `ItemsViewController.StartBringItemIntoView(index, alignmentRatio = NaN, animate = false)` | NaN = minimal scroll (moves only when the item is outside the viewport); 0 = align the item's start to the VIEWPORT's start; `animate` = the kernel's Driven chase. **It does not consult `ItemClipTopInset`**, so on a pinned-prefix list `0` lands the item under the pinned chrome by exactly the inset — an engine gap, not something an app corrects with a chasing `ScrollBy` | engine `Controls/ItemsView.cs:79-86, :990-1027` |
| `SkeletonDeriver` + `ScrollBinds` | the container arm rewrites a box-with-children as `ScrollBinds = []`, so a derived shimmer silently drops a pin instead of throwing or mis-baking it — a bind may be declared on a tree that is also a shimmer SOURCE | engine `Hooks/SkeletonDeriver.cs:59` |
| `TextEl.Weight`, `BoxEl.IsEnabled` | plain `ushort` / `bool` — NOT `Prop<>`; §5.2 gives the no-engine fallback for the bound weight/enabled | engine `Dsl/Element.cs:669, :305` |

### 5.1 `Entities/User.cs` — CORE additions (owner O1)

```csharp
// ── in the CORE section, after LibraryNavOrder ───────────────────────────────────────────────────────────────────

/// <summary>The sort keys the word rails offer. The int codes are PERSISTED (library.<kind>.sort) — never renumber,
/// only append. 5 (Albums) is new in the rework: followed artists by saved-album count, desc first.</summary>
public enum LibraryNavSort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, ReleaseDate = 4, Albums = 5 }

/// <summary>Which words a kind's rail shows, in rail order, as persisted codes. A pure table so the UI and the tests
/// read one source (the sidebar's Library V3 shares codes 0–3; 4 and 5 are library-page only).</summary>
public static class LibraryWordRail
{
    public static ReadOnlySpan<LibraryNavSort> WordsFor(EntityKind kind) => kind switch
    {
        EntityKind.Artist => s_artists,
        EntityKind.Show => s_shows,
        _ => s_albums,
    };
    static readonly LibraryNavSort[] s_albums = [LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.Creator, LibraryNavSort.RecentlyAdded, LibraryNavSort.ReleaseDate];
    static readonly LibraryNavSort[] s_artists = [LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.Albums];
    static readonly LibraryNavSort[] s_shows = [LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.RecentlyAdded];

    /// <summary>A persisted code this kind's rail does not offer (a stale value, or 5 on the albums page) → Recents.</summary>
    public static LibraryNavSort Clamp(EntityKind kind, int code)
    {
        var words = WordsFor(kind);
        for (int i = 0; i < words.Length; i++) if ((int)words[i] == code) return words[i];
        return LibraryNavSort.Recents;
    }

    /// <summary>The rail word's loc KEY per code (generated Strings.Library.Sort.*; the new "albums" word is §5.10).</summary>
    public static string WordKey(LibraryNavSort sort) => sort switch
    {
        LibraryNavSort.RecentlyAdded => Strings.Library.Sort.RecentlyAdded,   // "added"
        LibraryNavSort.Alphabetical => Strings.Library.Sort.Alphabetical,     // "a–z"
        LibraryNavSort.Creator => Strings.Library.Sort.Creator,               // "artist"
        LibraryNavSort.ReleaseDate => Strings.Library.Sort.ReleaseDate,       // "year"
        LibraryNavSort.Albums => Strings.Library.Sort.Albums,                 // "albums"
        _ => Strings.Library.Sort.Recents,                                    // "recents"
    };
}
```

`LibraryNavSorter<TRows>` gains the `Albums` arm. `ILibraryNavRows` gains `int Count(int row)` (the saved-album count
for an artist row; 0 for every other kind — `LibraryRows` implements it through `LibraryAlbumsOf`, below):

```csharp
// LibraryNavSorter<TRows>.Order — the new comparator arm, beside ReleaseDate:
//   LibraryNavSort.Albums => (a, b) => { int c = rows.Count(b).CompareTo(rows.Count(a)); return sign * (c != 0 ? c : ByTitle(rows, a, b)); }
```

The letters — a pure, allocation-free-after-warmup shape over the *sorted* slots:

```csharp
/// <summary>Letter groups over an ALPHABETICALLY sorted navigator: a flat index space that interleaves 27 possible
/// header items ("#", A–Z) with the rows. Headers are flat items with Slot = -(letter+1); rows carry their slot.
/// Everything is a pure function of the sorted titles, so the page can key its list on it and the tests can pin it.
/// Reused across computes (the arrays grow, never shrink) — the page owns ONE instance.</summary>
public sealed class LibraryLetters
{
    public const int Count = 27;            // '#' + A..Z
    public const float HeaderExtent = 28f;

    int[] _headerFlat = new int[Count];     // flat index of each letter's header, -1 when absent
    int[] _flatToRow = new int[64];         // flat index → row index (or -1 for a header)
    byte[] _flatLetter = new byte[64];      // flat index → letter of the header / of the row's group
    float[] _offset = new float[65];        // flat index → main-axis offset (prefix sum of extents); [FlatCount] = total
    int _flatCount, _rows;
    uint _present;                          // bit i = letter i has rows

    public int FlatCount => _flatCount;
    public bool IsHeader(int flat) => (uint)flat < (uint)_flatCount && _flatToRow[flat] < 0;
    public int RowOf(int flat) => (uint)flat < (uint)_flatCount ? _flatToRow[flat] : -1;
    public int LetterOf(int flat) => (uint)flat < (uint)_flatCount ? _flatLetter[flat] : -1;
    public bool Has(int letter) => (_present & (1u << letter)) != 0;
    public int HeaderFlat(int letter) => (uint)letter < Count ? _headerFlat[letter] : -1;
    public float OffsetOf(int flat) => _offset[Math.Clamp(flat, 0, _flatCount)];
    public float TotalExtent => _offset[_flatCount];

    /// <summary>The letter of a title: leading punctuation/brackets/quotes and a leading "the " are skipped; a first
    /// character outside A–Z (digits, CJK, an empty title) is '#' (index 0). Case-folded through char.ToUpperInvariant,
    /// which is what the alphabetical comparator (OrdinalIgnoreCase) agrees with for A–Z.</summary>
    public static int Of(ReadOnlySpan<char> title)
    {
        int i = 0;
        while (i < title.Length && !char.IsLetterOrDigit(title[i])) i++;
        var rest = title[i..];
        if (rest.Length > 4 && rest[3] == ' ' && rest[..3].Equals("the", StringComparison.OrdinalIgnoreCase)) rest = rest[4..];
        if (rest.Length == 0) return 0;
        char c = char.ToUpperInvariant(rest[0]);
        return c is >= 'A' and <= 'Z' ? c - 'A' + 1 : 0;
    }

    /// <summary>Rebuild for <paramref name="rows"/> (already in alphabetical order). O(n), no allocation once warm.</summary>
    public void Build(in LibraryRows rows, float rowExtent)
    {
        _rows = rows.Count;
        Grow(_rows + Count);
        _present = 0; _flatCount = 0;
        for (int l = 0; l < Count; l++) _headerFlat[l] = -1;
        int last = -1; float off = 0f;
        for (int r = 0; r < _rows; r++)
        {
            int letter = Of(rows.Title(r));
            if (letter != last)
            {
                _headerFlat[letter] = _flatCount; _present |= 1u << letter;
                _flatToRow[_flatCount] = -1; _flatLetter[_flatCount] = (byte)letter; _offset[_flatCount] = off;
                _flatCount++; off += HeaderExtent; last = letter;
            }
            _flatToRow[_flatCount] = r; _flatLetter[_flatCount] = (byte)letter; _offset[_flatCount] = off;
            _flatCount++; off += rowExtent;
        }
        _offset[_flatCount] = off;
    }

    /// <summary>Flat item extent: a header or a row. The analytic seed for RepeatLayout.Extents.</summary>
    public float ExtentOf(int flat, float rowExtent) => IsHeader(flat) ? HeaderExtent : rowExtent;

    /// <summary>The letter whose band contains <paramref name="offset"/> — the sticky overlay's input. Binary search over
    /// the prefix sums; -1 above the first header.</summary>
    public int StickyLetterAt(float offset)
    {
        int lo = 0, hi = _flatCount - 1, hit = -1;
        while (lo <= hi) { int mid = (lo + hi) >> 1; if (_offset[mid] <= offset) { hit = mid; lo = mid + 1; } else hi = mid - 1; }
        return hit < 0 ? -1 : _flatLetter[hit];
    }

    /// <summary>A stable identity of the grouping (letter → header flat index), folded FNV-1a — the remount key part.</summary>
    public ulong Key()
    {
        ulong h = 14695981039346656037UL;
        for (int l = 0; l < Count; l++) { h ^= (uint)(_headerFlat[l] + 1); h *= 1099511628211UL; }
        return h;
    }

    void Grow(int n)
    {
        if (_flatToRow.Length >= n) return;
        int size = Math.Max(n, _flatToRow.Length * 2);
        _flatToRow = new int[size]; _flatLetter = new byte[size]; _offset = new float[size + 1];
    }
}

/// <summary>The pane's readiness — THREE states (ch 15 W24 was two). Pure over the album's known bits and its
/// tracks edge, so the pane never paints a skeleton it cannot leave.</summary>
public enum AlbumPaneState : byte { Header, Rows, Failed, Ready }

public static class AlbumPaneReadiness
{
    /// <summary>
    /// Header  — the row's identity has not answered (the navigator's own demand; short-lived).
    /// Rows    — identity known; the tracks edge Unknown/Partial, or Complete with a row still unnamed and no failure.
    /// Failed  — the tracks edge failed, OR the row batch failed (edge Complete, a row unnamed, rowsFailed).
    /// Ready   — identity + a Complete edge whose every row knows its title.
    /// </summary>
    public static AlbumPaneState Of(bool knowsIdentity, EdgeState tracks, bool edgeFailed, bool anyUnnamed, bool rowsFailed)
    {
        if (!knowsIdentity) return AlbumPaneState.Header;
        if (edgeFailed) return AlbumPaneState.Failed;
        if (tracks != EdgeState.Complete) return AlbumPaneState.Rows;
        if (!anyUnnamed) return AlbumPaneState.Ready;
        return rowsFailed ? AlbumPaneState.Failed : AlbumPaneState.Rows;
    }

    /// <summary>The counted shimmer: TrackCount when the row knows it, the listed edge length when it doesn't, 6 when
    /// neither has answered — never 0 (a zero-row shimmer is a blank pane).</summary>
    public static int ShimmerRows(bool knowsCount, int trackCount, int listed)
        => knowsCount && trackCount > 0 ? trackCount : listed > 0 ? listed : 6;
}

/// <summary>The saved albums billed to an artist — "in your library" for the reader, and the count on the artists
/// navigator row. Reads Edges.SavedAlbums (parent = me) ∩ Edges.AlbumArtists (each saved album's billed artists),
/// both already demanded by the navigator (AlbumFields.Identity ⊇ Artists). O(saved · billed), allocation-free.</summary>
public static int LibraryAlbumsOf(int artistSlot, Span<int> into)
{
    var scope = Entities.Current;
    if (scope is null || scope.MeSlot <= Table.None || artistSlot <= Table.None) return 0;
    var saved = scope.Edges.SavedAlbums.Targets(scope.MeSlot);
    int n = 0;
    for (int i = 0; i < saved.Length; i++)
    {
        var billed = scope.Edges.AlbumArtists.Targets(saved[i]);
        for (int j = 0; j < billed.Length; j++)
            if (billed[j] == artistSlot) { if (n < into.Length) into[n] = saved[i]; n++; break; }
    }
    return n;   // may exceed into.Length: the caller sizes and retries (the reader) or only wants the count (the row)
}

public static int LibraryAlbumCountOf(int artistSlot) => LibraryAlbumsOf(artistSlot, default);

/// <summary>The songs across those albums: Σ TrackCount over the saved albums (the row's "· 34 songs"); an album
/// whose count is unknown contributes 0 and the caller shows the count without "songs" when the sum is 0.</summary>
public static int LibrarySongCountOf(int artistSlot)
{
    Span<int> slots = stackalloc int[128];
    int n = Math.Min(LibraryAlbumsOf(artistSlot, slots), slots.Length);
    int songs = 0;
    for (int i = 0; i < n; i++) { var a = new Album(slots[i]); if (a.Knows(AlbumFields.TrackCount)) songs += a.TrackCount; }
    return songs;
}
```

`LibrarySelectionCommit.ForAlbum(artistsView: true, …)` returns `Depth = 1` instead of 2 (and keeps writing
`AlbumKey`). Its test `ForAlbum_ArtistsView_DrillsToTracks` becomes `…_DrillsToReader` asserting 1.

### 5.2 `Entities/User.UI.cs` — the rail, the letters, the strip (owner O2)

```csharp
    // ══ 2. THE WORD RAIL (replaces the sort pill; W8) ═════════════════════════════════════════════════════════════════

    /// <summary>Zune's text pivot as a Fluent control: the words are the persisted sort codes; the active one is 100 %
    /// ink, 600, with a 2-DIP accent underline; the rest 50 %, 85 % on hover. Tap the active word to flip direction —
    /// a 10-px chevron after it says which. The rail reads the two Signal INSTANCES; nothing here is frozen data.</summary>
    public static Element WordRail(EntityKind kind, Signal<int> sort, Signal<bool> desc)
        => Embed.Comp(() => new WordRailHost(kind, sort, desc));

    sealed class WordRailHost(EntityKind kind, Signal<int> sort, Signal<bool> desc) : Component
    {
        Element[]? _words;   // built once: the rail's words are a per-kind constant; state rides binds

        public override Element Render()
        {
            var words = LibraryWordRail.WordsFor(kind);
            if (_words is null)
            {
                _words = new Element[words.Length];
                for (int i = 0; i < words.Length; i++) _words[i] = Word(words[i]);
            }
            return new BoxEl { Direction = 0, Height = 32f, Gap = 14f, AlignItems = FlexAlign.Center, Shrink = 0f, Children = _words };
        }

        Element Word(LibraryNavSort code)
        {
            int c = (int)code;
            Func<bool> isOn = () => sort.Value == c;
            Action tap = () => { if (sort.Peek() == c) desc.Value = !desc.Peek(); else { sort.Value = c; desc.Value = false; } };
            return new BoxEl
            {
                Direction = 1, Gap = 3f, Padding = new Edges4(0f, 2f, 0f, 0f), Cursor = CursorId.Hand,
                Role = AutomationRole.Button, Focusable = true, OnClick = tap,
                Opacity = Prop.Of(() => isOn() ? 1f : 0.5f), HoverOpacity = 0.85f, BrushTransitionMs = Design.Motion.Faster,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(Loc.Get(LibraryWordRail.WordKey(code)))
                            {
                                Size = 13.5f, LineHeight = 18f, Color = Tok.TextPrimary, CharSpacing = -5f,
                                Weight = Prop.Of(() => (ushort)(isOn() ? 600 : 400)),
                            },
                            new BoxEl
                            {
                                Width = 10f, Height = 10f, Visible = Prop.Of(() => isOn() && desc.Value),
                                Children = [Icon(Icons.ChevronDown, 10f, Tok.TextSecondary)],
                            },
                        ],
                    },
                    new BoxEl { Height = 2f, Corners = CornerRadius4.All(1f), Fill = Prop.Of(() => isOn() ? Tok.AccentDefault : ColorF.Transparent), BrushTransitionMs = Design.Motion.Faster },
                ],
            };
        }
    }
```

> **Engine note for O2.** `TextEl.Weight` is a plain `ushort` today (`Element.cs:669`), not a `Prop<ushort>`. If the
> engine does not accept a bound weight, render the word as two stacked `TextEl`s (400 and 600) with bound
> `Opacity` crossfading — the same trick `Sidebar.UI.LibraryV3.cs`'s destination rail uses for its active word
> (`DestinationWordSize 13.5`, `DestinationWordGap 14` in `Sidebar.Modes.cs:510`). Do not add an engine prop for this.

```csharp
    /// <summary>The view toggle beside the rail: list (1) / grid (3) icons, and the "…" that opens the trimmed
    /// ViewPanel (compact variants + S/M/L). View codes stay 0..3, persisted.</summary>
    public static Element ViewToggle(Signal<int> view, Signal<int> size) => Embed.Comp(() => new ViewToggleHost(view, size));

    sealed class ViewToggleHost(Signal<int> view, Signal<int> size) : Component
    {
        NodeHandle _anchor; OverlayHandle? _handle;
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            int v = view.Value;
            return new BoxEl
            {
                Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Shrink = 0f, OnRealized = h => _anchor = h,
                Children =
                [
                    ViewIcon(Icons.ViewList, !IsGridView(v), () => view.Value = IsCompactView(view.Peek()) ? 0 : 1),
                    ViewIcon(Icons.ViewGrid, IsGridView(v), () => view.Value = IsCompactView(view.Peek()) ? 2 : 3),
                    ViewIcon(Icons.More, false, () =>
                    {
                        if (Controls.IsNullOverlay(overlay)) return;
                        if (_handle is { IsOpen: true } open) { open.Close(); return; }
                        _handle = overlay.Open(() => _anchor, () => ViewPanel(view, size), FlyoutPlacement.BottomEdgeAlignedRight,
                            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup));
                        _handle.ClosedAction = () => _handle = null;
                    }),
                ],
            };
        }
        static Element ViewIcon(string glyph, bool on, Action tap) => new BoxEl
        {
            Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(4f),
            Fill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = tap, Children = [Icon(glyph, 15f, on ? Tok.TextPrimary : Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>SortPanelHost minus its sort rows: "View as" toggles + "Size" S/M/L. Same rows, same code; the sort
    /// rows and the hasCreator/hasRelease flags are deleted with the pill.</summary>
    public static Element ViewPanel(Signal<int> view, Signal<int> size) => Embed.Comp(() => new ViewPanelHost(view, size));
```

The letters:

```csharp
    // ══ 3. LETTERS (W2) ══════════════════════════════════════════════════════════════════════════════════════════════

    public static string LetterText(int letter) => letter <= 0 ? "#" : s_letters[letter - 1];
    static readonly string[] s_letters = ["A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z"];

    /// <summary>The inline header item (flat ContentType 1): 28 high, the letter in a small card plate.</summary>
    public static Element LetterHeader(BoundItemScope<LibraryNavItem> scope) => new BoxEl
    {
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [new TextEl(scope.Text(static it => LetterText(-it.Slot - 1))) { Size = 12f, LineHeight = 16f, Weight = 600, CharSpacing = 60f, Color = Tok.TextTertiary }],
            },
        ],
    };

    /// <summary>The pinned twin of LetterHeader — a ZStack overlay over the list, translated by the push distance the
    /// page computes from the scroll geometry (Recents' StickyDayHeader idiom). Hidden while no letter is under the top.</summary>
    public static Element StickyLetter(IReadSignal<int> letter, IReadSignal<float> push) => new BoxEl
    {
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        HitTestVisible = false, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Visible = Prop.Of(() => letter.Value >= 0),
        Transform = Prop.Of(() => Affine2D.Translation(0f, push.Value)),
        Children =
        [
            new BoxEl
            {
                Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [new TextEl(Prop.Of(() => LetterText(letter.Value))) { Size = 12f, LineHeight = 16f, Weight = 600, CharSpacing = 60f, Color = Tok.TextTertiary }],
            },
        ],
    };

    /// <summary>The A–Z strip on the navigator's right edge: 27 letters, 18 wide; present letters 100 %, absent 30 %
    /// and inert; the letter under the viewport top in accent/700. <paramref name="present"/> is the LibraryLetters
    /// bitmask, <paramref name="current"/> the sticky letter; a tap hands the letter to <paramref name="jump"/>.</summary>
    public static Element JumpStrip(IReadSignal<uint> present, IReadSignal<int> current, Action<int> jump)
    {
        var rows = new Element[LibraryLetters.Count];
        for (int l = 0; l < rows.Length; l++)
        {
            int letter = l;
            rows[l] = new BoxEl
            {
                Height = 13f, Width = 18f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(3f),
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                IsEnabled = Prop.Of(() => (present.Value & (1u << letter)) != 0),
                Opacity = Prop.Of(() => (present.Value & (1u << letter)) != 0 ? 1f : 0.3f),
                HoverFill = Tok.FillSubtleSecondary, OnClick = () => jump(letter),
                Children =
                [
                    new TextEl(LetterText(letter))
                    {
                        Size = 9.5f, LineHeight = 12f,
                        Color = Prop.Of(() => current.Value == letter ? Tok.AccentTextPrimary : Tok.TextTertiary),
                        Weight = Prop.Of(() => (ushort)(current.Value == letter ? 700 : 400)),
                    },
                ],
            };
        }
        return new BoxEl { Direction = 1, Width = 18f, Shrink = 0f, Justify = FlexJustify.Center, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS), Children = rows };
    }
```

> The same `Weight` caveat applies to the strip; `IsEnabled` is a plain `bool` on `BoxEl` (`Element.cs:305`) — if it
> is not bindable, drop the `IsEnabled` line and let the inert letter's `jump` no-op on an absent header (the page's
> `Jump` already checks `HeaderFlat(letter) < 0`).

`NavRow` changes (§4 of the file): the artist subtitle becomes a bound text through a hoisted cache keyed by the
packed `(albums << 12 | songs)`:

```csharp
    static readonly FormatCache<int> s_artistCounts = new();
    // NavRow, kind == Artist, non-compact subtitle:
    //   new TextEl(scope.Text(static it => ArtistCountCode(it.Slot), s_artistCounts, static code => ArtistCountText(code))) { … }
    static int ArtistCountCode(int slot) => (Math.Min(User.LibraryAlbumCountOf(slot), 4095) << 12) | Math.Min(User.LibrarySongCountOf(slot), 4095);
    static string ArtistCountText(int code)
    {
        int albums = code >> 12, songs = code & 4095;
        if (albums == 0) return Loc.Get(Strings.Search.TypeArtist);                       // nothing saved yet: the old word
        string a = albums == 1 ? Strings.Library.OneAlbum : Strings.Library.NAlbums(albums);
        return songs > 0 ? a + " · " + Strings.Library.NSongs(songs) : a;
    }
```

The album subtitle appends the year: `LibraryRows.SubtitleOf` stays "artist"; the row uses a second cache
`s_albumSub` keyed by `(slot, year)` — or simply `scope.Text(static it => AlbumSubtitleOf(it.Slot))` where
`AlbumSubtitleOf` concatenates; **the rule**: never format inline in a bind thunk, so it is `scope.Text(keySel,
cache, formatter)` with the key `(slot << 12) | year` — a recycle never concatenates. `DiscoRow`, `DiscoCard`,
`SortViewPill`, `SortPanel`, `SortPillHost`, `SortPanelHost`, `GoToArtistLink` are **deleted** (the reader's ↗ is a
`Controls.Named` icon button; the search columns never used the link).

### 5.3 `Entities/User.Page.Library.cs` — the page (owner O3)

Fields added/removed on `LibraryPage`:

```csharp
        // removed: MidW's ArtistColumns use, ASort/ADesc/AView/ASize as the discography's, AFilter, _midGrip (search keeps MidW + _midGrip)
        internal readonly Signal<int> Scope;                // artists only: 0 in your library · 1 all releases (persisted)
        internal readonly Signal<int> RSort;                // artists only: the reader's ReaderSort (persisted as LibraryAlbumSort)
        readonly LibraryLetters _letters = new();
        readonly Signal<int> _stickyLetter = new(-1);
        readonly Signal<float> _stickyPush = new(0f);
        readonly Signal<uint> _present = new(0u);
        readonly Func<int, float> _extentOf;
        readonly Func<int, int> _contentType;
        readonly Action<int> _jump;
        readonly Action<int> _onAlsoBy;
        readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) _geometry;
        Element? _reader;
```

`NavShape` gains the letters' key: `sealed record NavShape(int Count, int FlatCount, string OrderKey, string FactsKey,
ulong LettersKey)`. `ComputeShape` builds the letters after sorting when `sort == Alphabetical && !IsGridView(View)`:

```csharp
            bool letters = sort == LibraryNavSort.Alphabetical && !IsGridView(View.Value);
            ulong lettersKey = 0;
            if (letters)
            {
                _letters.Build(display, IsCompactView(View.Value) ? 40f : 56f);
                lettersKey = _letters.Key();
                _present.SetIfChanged(_letters.Present);            // add `public uint Present => _present;` to LibraryLetters
            }
            else _present.SetIfChanged(0u);
            _hasLetters = letters;
            return new NavShape(n, letters ? _letters.FlatCount : n, LibraryNavOrder.OrderKey(display), LibraryNavOrder.FactsKey(display), lettersKey);
```

The projection maps a flat index to an item — a header when the letters say so:

```csharp
        LibraryNavItem ItemAt(int flat)
        {
            if (_hasLetters)
            {
                if (_letters.IsHeader(flat)) return new LibraryNavItem(_entity, -_letters.LetterOf(flat) - 1, 0u);
                flat = _letters.RowOf(flat);
            }
            return (uint)flat < (uint)_sortedCount ? LibraryNavItem.Of(_entity, _sorted[flat]) : default;
        }
        float ExtentOf(int flat) => _hasLetters ? _letters.ExtentOf(flat, RowExtent) : RowExtent;
        float RowExtent => IsCompactView(View.Peek()) ? 40f : 56f;       // list rows: 56 (was 60 — the prototype's row)
        int ContentTypeOf(int flat) => _hasLetters && _letters.IsHeader(flat) ? 1 : 0;
```

`ListBody` for the list views becomes the ZStack of the bound list, the sticky overlay and the strip; the grid views
keep `GridFit` (letters off, strip on when alphabetical):

```csharp
        Element ListBody(NavShape shape)
        {
            if (shape.Count == 0)
                return Filter.Peek().Length > 0 ? EmptyCompact(Loc.Get(Strings.Library.NoMatch)) : LoadingCaption();
            int view = View.Value, size = Size.Value;
            bool grid = IsGridView(view), compact = IsCompactView(view);
            bool alpha = (LibraryNavSort)Sort.Value == LibraryNavSort.Alphabetical;
            var layout = grid
                ? RepeatLayout.GridFit((compact ? 88f : 116f) + size * (compact ? 16f : 24f), 8f)
                : RepeatLayout.Extents(_extentOf, compact ? 40f : 56f);
            var template = grid ? (compact ? _cardCompactT : _cardT) : (compact ? _slotCompactT : _slotT);   // _slotT branches header/row
            Element list = new BoxEl
            {
                Key = "nav:" + view + ":" + size + ":" + shape.OrderKey + ":" + shape.FactsKey + ":" + shape.LettersKey.ToString("x16"),
                Grow = 1f, Direction = 1, MinHeight = 0f,
                Padding = grid ? new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f) : default,
                Children = [ItemsView.CreateBound(_items!, template, layout, grid ? _navOptions : _navOptionsLettered)],
            };
            if (!alpha) return list;
            Element body = grid ? list : new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true, Children = [list, StickyLetter(_stickyLetter, _stickyPush)] };
            return new BoxEl { Direction = 0, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch, Children = [body, JumpStrip(_present, _stickyLetter, _jump)] };
        }
```

`_navOptionsLettered` is `_navOptions with { ContentType = _contentType, IsItemEnabledTyped = static (_, it) => it.Slot > 0,
Scroll = _navScroll with { OnScrollGeometryChanged = _geometry, ItemClipTopInset = LibraryLetters.HeaderExtent,
ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand } }` — both records are frozen at mount, so both variants
are built once in the constructor. The slot template branches at build time on the pooled content type (Recents'
`RecentsRowSlot` idiom):

```csharp
        Element NavSlot(BoundItemScope<LibraryNavItem> scope, bool compact)
            => scope.Item.Peek().Slot < 0 ? LetterHeader(scope) : NavRow(scope, _entity, compact);
```

The scroll-spy projects a coarse key and writes two signals — the bound overlay re-skins with no render:

```csharp
        long ProjectLetters(ScrollGeometry g)
        {
            if (!_hasLetters) return 0;
            int letter = _letters.StickyLetterAt(g.OffsetY);
            int quantized = (int)MathF.Round(PushOf(g.OffsetY, letter) / Spacing.XXS);
            return ((long)(letter + 1) << 16) | (uint)(ushort)(quantized + 32768);
        }
        void UpdateLetters(ScrollGeometry g)
        {
            int letter = _hasLetters ? _letters.StickyLetterAt(g.OffsetY) : -1;
            _stickyLetter.SetIfChanged(letter);
            _stickyPush.SetIfChanged(letter < 0 ? 0f : MathF.Round(PushOf(g.OffsetY, letter) / Spacing.XXS) * Spacing.XXS);
        }
        /// <summary>The next header's top minus the viewport top minus the overlay's height, clamped ≤ 0: the pinned
        /// letter is pushed up by the one arriving under it.</summary>
        float PushOf(float offsetY, int letter)
        {
            for (int l = letter + 1; l < LibraryLetters.Count; l++)
            {
                int next = _letters.HeaderFlat(l);
                if (next >= 0) return MathF.Min(0f, _letters.OffsetOf(next) - offsetY - LibraryLetters.HeaderExtent);
            }
            return 0f;
        }
        void Jump(int letter)
        {
            int flat = _hasLetters ? _letters.HeaderFlat(letter) : FirstRowOfLetter(letter);   // grid: the first card of the letter
            if (flat >= 0) _navCtl.StartBringItemIntoView(flat, alignmentRatio: 0f);
        }
```

`SyncNav`/`IndexOfSlot` translate between row indices and flat indices (`_letters.HeaderFlat`/`RowOf`) when
`_hasLetters`; `OnNavSel` reads `_navSel.FirstSelectedIndex` as a flat index and maps it back. `NoteNavKey` gains
`"letters"` as a reason.

The toolbar swaps the pill for the rail:

```csharp
            Element picker = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children = [WordRail(_entity, Sort, Desc), new BoxEl { Grow = 1f }, ViewToggle(View, Size)],
            };
            // the title row: PageHero + the count
            Element titleRow = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Baseline, Gap = Spacing.S,
                Children =
                [
                    Design.Type.PageHero(Shell.Dest(new Shell.Route(_route)).Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(Prop.Of(() => FormatCache.Int(_shape!.Value.Count))) { Size = 14f, LineHeight = 20f, Color = Tok.TextTertiary },
                ],
            };
```

The right column:

```csharp
        Element DetailColumn(bool hasSelection, int slot)
        {
            if (!hasSelection) return Placeholder(IsPodcasts ? Strings.Library.SelectShow : Strings.Library.SelectAlbum);
            return ReadingPane with { Key = "lib:detail", Grow = 1f, Basis = 0f, Children = [PaneFor(_entity, slot)] };
        }

        Element PaneFor(EntityKind kind, int slot) => kind == EntityKind.Show
            ? Embed.Comp(new PaneProps(slot), static () => new LibraryShowPane()) with { Key = "show-pane:" + slot }
            : Embed.Comp(new Album.PaneProps(slot, ShowAlsoBy: true, OnAlsoBy: _onAlsoBy), static () => new Album.Pane());

        Element ReaderColumn(bool hasSelection, int artistSlot)
        {
            if (!hasSelection) return Placeholder(Strings.Library.SelectArtist);
            return ReadingPane with
            {
                Key = "lib:reader", Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [Embed.Comp(new Artist.ReaderProps(artistSlot, Scope, RSort, AlbumKey), static () => new Artist.Reader())],
            };
        }
```

`_onAlsoBy = slot => Select(slot)` (a library-internal select-in-place). The wide arm's `right` picks
`ReaderColumn` for artists; `Collapsed` clamps depth to 1 for artists and mounts the reader at depth 1. `SaveState`
writes `Scope` (`Keys.LibraryScope(_kind)`) and `RSort` (`Keys.LibraryAlbumSort(_kind)`), drops `ADesc/AView/ASize`
(the keys stay defined in `Platform.cs`; nothing reads them — a note in the file header records the orphaned keys so
nobody "cleans them up" into a renumber).

### 5.4 The show pane

`LibraryShowPane` stays in `User.Page.Library.cs` §4 unchanged except its hero: `PaneHero(...)` → `Show.PaneHeader(...)`
(owner M writes it in `Show.UI.cs` as the 128-cover twin of `Album.PaneHeader` with the publisher line; O3 only
re-points the call). The `FollowButton` and compact episodes are untouched.

### 5.5 `Entities/Album.Pane.cs` — the one album pane (owner M)

```csharp
// ── Entities/Album.Pane.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the library master-detail's right column for an album: ONE anatomy at every width (ch 15 §0 amendment 2026-09-17),
// the whole model demanded on mount, three-state readiness (Header · Rows · Failed · Ready), the counted shimmer,
// and the "Also by … in your library" strip. Owner M. Wave L2. Budget 320 lines.
//
// THE RULE THAT KILLS "SKELETON FOREVER": the header paints from AlbumFields.Identity, which the navigator already
// demanded for every row it shows — the pane has nothing header-shaped it can wait for. Only the rows load, they load
// as a COUNTED shimmer (TrackCount rows), and a failed edge or a failed row batch is a strip with Retry, never a
// skeleton. The pane mounts no trailing band, so it asks AlbumFields.Detail (never Publishing) — ch 05 §9's warning
// about the Full rung stands: this surface does not grow an "About this release" panel.

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Album
{
    /// <summary>Re-pushed props: the selection changes → the pane re-skins in place (no remount, no lost scroll).
    /// <paramref name="OnAlsoBy"/> is behaviour (ignored by equality): a tile click hands the page a slot to select.</summary>
    public sealed record PaneProps(int Slot, bool ShowAlsoBy, Action<int>? OnAlsoBy = null)
    {
        public bool Equals(PaneProps? other) => other is not null && other.Slot == Slot && other.ShowAlsoBy == ShowAlsoBy;
        public override int GetHashCode() => HashCode.Combine(Slot, ShowAlsoBy);
    }

    public sealed class Pane : Component
    {
        const float Cover = 128f;
        int _slot;
        bool _rowsFailed;
        Action<int>? _onAlsoBy;
        readonly Signal<int> _retryEpoch = new(0);
        readonly Action _demand, _demandRows, _play, _shuffle, _open, _retry;
        readonly Func<ContextMenuModel?> _menu;
        readonly Action<int> _alsoBy;
        IOverlayService? _overlay;
        int[] _also = new int[16];

        public Pane()
        {
            _demand = () =>
            {
                _ = _retryEpoch.Value;                                    // Retry re-arms both effects
                var a = new Album(_slot);
                if (!a.IsValid) return;
                Entities.Ensure(a, AlbumFields.Detail);
                var edge = Entities.Current.Edges.AlbumTracks;
                if (edge.State(_slot) == EdgeState.Unknown && !edge.IsFailed(_slot)) Entities.EnsureEdge(FetchEdge.AlbumTracks, _slot);
            };
            _demandRows = () =>
            {
                _ = _retryEpoch.Value;
                _ = Entities.ScopeEpoch.Value;
                _ = Entities.Current.Edges.AlbumTracks.Changed.Value;
                var a = new Album(_slot);
                if (a.IsValid && a.TrackSlots.Length > 0)
                    Entities.Ensure(MemoryMarshal.Cast<int, Track>(a.TrackSlots), TrackFields.Row | TrackFields.Audio | TrackFields.Tags | TrackFields.Video);
            };
            _play = () => { var a = new Album(_slot); if (a.IsValid) Playback.PlayContext(a.Id); };
            _shuffle = () => { var a = new Album(_slot); if (!a.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(a.Id); };
            _open = () => { var a = new Album(_slot); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Title)); };
            _retry = () =>
            {
                var edge = Entities.Current.Edges.AlbumTracks;
                if (edge.IsFailed(_slot)) Entities.RefreshEdge(FetchEdge.AlbumTracks, _slot);
                _rowsFailed = false;
                _retryEpoch.Value = _retryEpoch.Peek() + 1;
            };
            _menu = () => { var a = new Album(_slot); return a.IsValid ? Album.MoreMenu(a, _overlay) : null; };   // the page's hero menu, lifted (§5.0)
            _alsoBy = slot => _onAlsoBy?.Invoke(slot);
        }

        public override Element Render()
        {
            var p = UseProps<PaneProps>();
            _overlay = UseContext(Overlay.Service);
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Albums.Changed.Value; _ = scope.Tracks.Changed.Value; _ = scope.Artists.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value; _ = scope.Edges.AlbumArtists.Changed.Value; _ = scope.Edges.SavedAlbums.Changed.Value;
            _slot = p.Slot; _onAlsoBy = p.OnAlsoBy;
            UseEffect(_demand, DepKey.From(p.Slot, (int)epoch));
            UseEffect(_demandRows);
            _rowsFailed = UseComputed(RowsFailed).Value;                  // Fetch's per-row failure bit for the batch (see note)

            var a = new Album(_slot);
            var edge = scope.Edges.AlbumTracks;
            var tracks = MemoryMarshal.Cast<int, Track>(a.TrackSlots);
            var state = AlbumPaneReadiness.Of(
                knowsIdentity: a.IsValid && a.Knows(AlbumFields.Title),
                tracks: edge.State(_slot), edgeFailed: edge.IsFailed(_slot),
                anyUnnamed: Detail.NoticeRules.ForAlbum(tracks) != DetailNotice.None, rowsFailed: _rowsFailed);
            if (state == AlbumPaneState.Header) return HeaderSkeleton();

            var id = Detail.Identity.For(a);
            string uri = a.Uri.Text;
            Element body = state switch
            {
                AlbumPaneState.Ready => Track.Table(new Track.TableArgs
                {
                    Source = Track.TableSource.ForAlbum(a),
                    Profile = Track.TableProfile.From(Detail.Config.For(DetailKind.Album, a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album) with { ShowPlays = false, HasTrailing = false }),
                    ShowToolbar = false, Embedded = true, ScrollKey = "lib:tracks:" + uri,
                }),
                AlbumPaneState.Failed => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact,
                    title: Loc.Get(Strings.Library.SongsFailed), onAction: _retry),   // the Error voice supplies the Retry label
                _ => CountedShimmer(AlbumPaneReadiness.ShimmerRows(a.Knows(AlbumFields.TrackCount), a.TrackCount, tracks.Length)),
            };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    PaneHeader(id.CoverUrl, EyebrowOf(a), id.Title, _open, Detail.UI.ArtistLine(id.Artists), MetaOf(a, id)),
                    PaneCommands(_play, _shuffle,
                        Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = id.Title }) with { Key = "save:" + uri },
                        Detail.UI.MoreButton(_menu, 36f, 16f, round: true), _open),
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, Children = [body] },
                    p.ShowAlsoBy && state == AlbumPaneState.Ready ? AlsoByStrip(a, _also, _alsoBy) : new BoxEl(),
                ],
            };
        }

        /// <summary>"ALBUM · 2013": kind label + year when known; the kind alone otherwise.</summary>
        static string EyebrowOf(Album a)
        {
            string kind = Detail.Text.KindLabel(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);
            return a.Knows(AlbumFields.Year) && a.Year > 0 ? kind + " · " + a.Year : kind;
        }

        /// <summary>"13 songs · 45 min · saved 2024": Identity's meta (songs · duration, duration dropped while thin — ch 03
        /// §7) plus the library's AddedAt year when the album is saved.</summary>
        static string MetaOf(Album a, Detail.Identity id)
        {
            string meta = id.Meta ?? Detail.Text.AlbumMeta(a.Knows(AlbumFields.TrackCount) ? a.TrackCount : a.TrackSlots.Length, 0L, durationsKnown: false, a.Year) ?? "";
            int added = User.Me.IsValid ? User.Me.AddedAt(LibraryEdgeKind.SavedAlbums, a.Slot) : 0;   // UNIX seconds (User.cs:28)
            return added > 0 ? meta + " · " + Strings.Library.SavedIn(DateTimeOffset.FromUnixTimeSeconds(added).Year) : meta;
        }

        bool RowsFailed()
        {
            _ = _retryEpoch.Value; _ = Entities.ScopeEpoch.Value; _ = Entities.Current.Tracks.Changed.Value;
            var a = new Album(_slot);
            if (!a.IsValid) return false;
            var slots = a.TrackSlots;
            for (int i = 0; i < slots.Length; i++) if (Entities.Current.Tracks.IsFailed(slots[i], (uint)TrackFields.Row)) return true;
            return false;
        }
        // NOTE for M / O1: `Table.IsFailed(slot, group)` does NOT exist today (verified 2026-09-17: Table has `Known`
        // only; `Fetch.Failed(ticket, status, retryAfter)` at Fetch.cs:1019 un-asks the batch's groups and records
        // nothing per row). O1 adds ONE `Column<uint> Failed` to Table in Entities.cs, ORs the batch's groups into it
        // for every row the ticket carried inside `Fetch.Failed` (the terminal arm only — a retryable 429/503 is not a
        // failure), clears the bits in `Ensure` when a group is re-asked, and bumps `table.Changed` — the pane's
        // `RowsFailed` memo then re-runs. A 30-line change in wave L1; do not invent a timer or a retry counter here.

        static Element CountedShimmer(int rows)
        {
            var kids = new Element[rows + 1];
            kids[0] = Track.TableHeaderShim(compact: true);              // the "#  Title  ♥  ⏱" header, M's static
            for (int i = 1; i <= rows; i++) kids[i] = Track.ShimmerRow(in Track.EmbeddedColumns, Track.EmbeddedTracks, Track.RowMetrics.RowH, Spacing.M) with { Key = "sh:" + i };
            return new BoxEl { Direction = 1, Padding = new Edges4(Spacing.M, 0f, Spacing.M, Spacing.M), Children = kids };
        }

        static Element HeaderSkeleton() => new BoxEl
        {
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.End, Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
            Children =
            [
                new BoxEl { Width = Cover, Height = Cover, Corners = Radii.CardAll, Fill = Tok.FillCardDefault },
                new BoxEl { Direction = 1, Gap = Spacing.S, Grow = 1f, Children =
                    [ new BoxEl { Width = 80f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                      new BoxEl { Width = 220f, Height = 26f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                      new BoxEl { Width = 120f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault } ] },
            ],
        };
    }

    // ══ the pane's static pieces (also the show pane's twin lives in Show.UI.cs) ══════════════════════════════════════

    /// <summary>Cover 128 (r 6, Elevation.Card) · eyebrow · title link 28/36/600 (2 lines, accent on hover) · the
    /// attribution · meta 12/16. AlignItems End: the text block sits on the cover's baseline, the prototype's stance.
    /// Accent-NEUTRAL: nothing here reads a palette (ch 15 §0.8).</summary>
    public static Element PaneHeader(string? cover, string eyebrow, string title, Action open, Element attribution, string meta) => new BoxEl
    {
        Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Shrink = 0f,
        Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
        Children =
        [
            new BoxEl { Width = 128f, Height = 128f, Shrink = 0f, Corners = Radii.CardAll, ClipToBounds = true, Shadow = Elevation.Card,
                        Children = [Controls.Artwork(cover, 128f, 128f, Radii.Card, decodePx: 256)] },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f, MinWidth = 0f,
                Children =
                [
                    Design.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary },
                    new BoxEl
                    {
                        Corners = Radii.ControlAll, Direction = 1,
                        Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Margin = new Edges4(-Spacing.S, -Spacing.XXS, -Spacing.S, -Spacing.XXS),
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
                        Children = [ new TextEl(title) { Size = 28f, LineHeight = 34f, Weight = 600, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                                                         BrushTransitionMs = Design.Motion.Faster, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis } ],
                    }.Interactive(Interaction.Subtle),
                    attribution,
                    new TextEl(meta) { Size = 12.5f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
        ],
    };

    /// <summary>Play (system accent, 36) · shuffle (36 circ) · save · more (36 circ each) · spacer · "Open album ↗"
    /// (a 13-px accent text link with the OpenInNewWindow glyph — a HyperlinkButton, not a capsule). Never a second
    /// accent CTA (ch 15 §0.8).</summary>
    /// <summary>A 36-px subtle circle with a glyph and a tooltip name — the pane's and the reader's secondary verb.</summary>
    internal static Element CommandCircle(string glyph, string name, Action tap) => Controls.Named(new BoxEl
        {
            Width = 36f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(36f),
            Fill = Tok.FillSubtleSecondary, HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
            Children = [Icon(glyph, 16f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle), name);

    public static Element PaneCommands(Action play, Action shuffle, Element save, Element more, Action open)
    {
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, Shrink = 0f,
            Padding = new Edges4(Spacing.XL, Spacing.M, Spacing.XL, Spacing.S),
            Children =
            [
                Controls.Play(Tok.AccentDefault, play),
                CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle),
                save, more,
                new BoxEl { Grow = 1f },
                new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll, Padding = new Edges4(Spacing.S, 6f, Spacing.S, 6f),
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, BrushTransitionMs = Design.Motion.Faster,
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children = [ new TextEl(Loc.Get(Strings.Library.OpenAlbum)) { Size = 13f, LineHeight = 18f, Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary },
                                 Icon(Icons.OpenInNewWindow, 14f, Tok.AccentTextPrimary) ],
                },
            ],
        };
    }

    /// <summary>"ALSO BY {artist} IN YOUR LIBRARY": the other saved albums billed to the album's FIRST billed artist,
    /// as 96-px tiles (art r 5 · title 12.5 · year 11.5), in Recents order. Absent when there are none. The tile
    /// selects in place through <paramref name="pick"/>. The slot buffer is the pane's, grown at a selection edge only.</summary>
    public static Element AlsoByStrip(Album a, int[] buffer, Action<int> pick)
    {
        var billed = a.ArtistSlots;
        if (billed.Length == 0) return new BoxEl();
        int artist = billed[0];
        int n = User.LibraryAlbumsOf(artist, buffer);
        if (n > buffer.Length) { Array.Resize(ref buffer, n); n = User.LibraryAlbumsOf(artist, buffer); }
        var tiles = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            int slot = buffer[i];
            if (slot == a.Slot) continue;
            var o = new Album(slot);
            tiles.Add(new BoxEl
            {
                Width = 96f, Direction = 1, Gap = 6f, Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = () => pick(slot),
                Children =
                [
                    new BoxEl { Width = 96f, Height = 96f, Corners = Radii.ControlAll, ClipToBounds = true, Children = [Controls.Artwork(Controls.ArtUrl(o.ImageId), 96f, 96f, Radii.Control, decodePx: 192)] },
                    new TextEl(o.Title) { Size = 12.5f, LineHeight = 16f, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(o.Knows(AlbumFields.Year) && o.Year > 0 ? FormatCache.Int(o.Year) : "") { Size = 11.5f, LineHeight = 14f, Color = Tok.TextTertiary },
                ],
            }.Interactive(Interaction.Subtle));
        }
        if (tiles.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Direction = 1, Gap = 10f, Shrink = 0f, Padding = new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.XL),
            Children =
            [
                Design.Type.Eyebrow(Strings.Library.AlsoBy(new Artist(artist).Name)) with { Color = Tok.TextTertiary },
                ScrollView(new BoxEl { Direction = 0, Gap = 12f, Children = tiles.ToArray() }, horizontal: true) with { SuppressScrollBar = true },
            ],
        };
    }
}
```

> **Two things M must resolve in the file, not in this plan:** (1) `Detail.UI.ArtistLine` — 0.3's `LibraryAlbumPane`
> calls a file-local `ArtistLine(id.Artists)`; move that 15-line static into `Detail.UI.cs` as `Detail.UI.ArtistLine`
> (it is a detail-frame piece). (2) `Track.TableHeaderShim` / `Track.EmbeddedColumns` / `Track.EmbeddedTracks` — the
> compact `#  Title  ♥  ⏱` header and the lane set the embedded table paints at `ShowPlays = false`; if
> `Track.Table.Chrome.cs` already exposes the header builder, use it, else add the 25-line static there (M owns it).
> `Strings.Detail.Retry` exists if the discography's failure arm uses it (`Artist.Discography.cs:657`); otherwise
> it is one of §5.10's keys.

### 5.6 `Entities/Artist.Reader.cs` — the CORE half: `ReaderShape` (owner N)

```csharp
public readonly partial struct Artist
{
    /// <summary>The reader's own sort — a reader-local enum, persisted through LibraryAlbumSort("artists"); old
    /// discography values (0..4) clamp to Newest. Never renumber.</summary>
    public enum ReaderSort : byte { Newest = 0, Oldest = 1, Alphabetical = 2 }

    /// <summary>What one block is: the album, whether it is saved (library blocks precede catalogue blocks in every sort),
    /// and how many rows it lays out (TrackCount when known, else the listed edge length, else 4 — the counted skeleton).</summary>
    public readonly record struct ReaderBlock(int AlbumSlot, bool Saved, int Rows, bool Failed);

    /// <summary>The reader's shape as a VALUE (the remount key + count); the blocks live in the reader's pooled buffer.</summary>
    public sealed record ReaderShapeKey(int Count, int Library, string OrderKey, int Scope, int Sort)
    {
        public static readonly ReaderShapeKey Empty = new(0, 0, "0", 0, 0);
    }

    /// <summary>PURE: the block order and extents. Library blocks first (Scope 0 shows only them), then — Scope 1 — the
    /// union of the three facet edges minus the saved ones; inside each group the sort applies: Newest = year desc,
    /// unknown years sink; Oldest = year asc, unknown years sink; Alphabetical = title (OrdinalIgnoreCase), then uri.
    /// The source index is the tie-break of last resort, so the order is total and the key deterministic.</summary>
    public static class ReaderShape
    {
        public const float BlockPadTop = 16f, BlockPadBottom = 8f, CoverEdge = 120f, CoverEdgeNarrow = 88f, HeadH = 44f, RowH = 36f, RowsPadBottom = 8f, Divider = 1f;

        /// <summary>The analytic block extent: pad + max(cover + its caption, head + rows) + pad + divider.</summary>
        public static float ExtentOf(in ReaderBlock b, bool narrow)
        {
            float cover = (narrow ? CoverEdgeNarrow : CoverEdge) + 8f + 16f;   // cover + gap + the "2022 · Album" caption
            float body = HeadH + b.Rows * RowH + RowsPadBottom;
            return BlockPadTop + MathF.Max(cover, body) + BlockPadBottom + Divider;
        }

        /// <summary>Fills <paramref name="into"/> with the ordered blocks; returns the count (library count in
        /// <paramref name="library"/>). <paramref name="scratch"/> is the union buffer, sized ≥ saved + listed.</summary>
        public static int Build(int artistSlot, int scope, ReaderSort sort, Span<int> savedSlots, int savedCount,
                                ReadOnlySpan<int> albums, ReadOnlySpan<int> singles, ReadOnlySpan<int> compilations,
                                Span<int> scratch, Span<int> perm, Span<ReaderBlock> into, out int library)
        {
            // 1. library blocks
            int n = 0;
            for (int i = 0; i < savedCount; i++) scratch[n++] = savedSlots[i];
            Order(scratch[..n], perm[..n], sort);
            for (int i = 0; i < n; i++) into[i] = BlockOf(scratch[perm[i]], saved: true);
            library = n;
            if (scope == 0) return n;
            // 2. catalogue blocks: union of the facets, minus saved (a saved album is already a block above)
            int m = 0;
            m = Union(albums, savedSlots[..savedCount], scratch, n, m);
            m = Union(singles, savedSlots[..savedCount], scratch, n, m);
            m = Union(compilations, savedSlots[..savedCount], scratch, n, m);
            var cat = scratch.Slice(n, m);
            Order(cat, perm[..m], sort);
            for (int i = 0; i < m; i++) into[n + i] = BlockOf(cat[perm[i]], saved: false);
            return n + m;
        }

        static ReaderBlock BlockOf(int slot, bool saved)
        {
            var a = new Album(slot);
            var edge = Entities.Current.Edges.AlbumTracks;
            int listed = edge.Count(slot);
            int rows = a.Knows(AlbumFields.TrackCount) && a.TrackCount > 0 ? a.TrackCount : listed > 0 ? listed : 4;
            return new ReaderBlock(slot, saved, rows, edge.IsFailed(slot));
        }

        static int Union(ReadOnlySpan<int> facet, ReadOnlySpan<int> saved, Span<int> scratch, int start, int m)
        {
            for (int i = 0; i < facet.Length; i++)
            {
                int s = facet[i];
                if (s <= Table.None || saved.IndexOf(s) >= 0 || scratch.Slice(start, m).IndexOf(s) >= 0) continue;
                scratch[start + m++] = s;
            }
            return m;
        }

        static void Order(Span<int> slots, Span<int> perm, ReaderSort sort)
        {
            for (int i = 0; i < perm.Length; i++) perm[i] = i;
            if (slots.Length < 2) return;
            var cmp = new BlockComparer(slots, sort);
            MemoryExtensions.Sort(perm, cmp);
        }

        readonly struct BlockComparer(Span<int> slots, ReaderSort sort) : IComparer<int>
        {
            readonly int[] _slots = slots.ToArray();   // ONE bounded copy per shape compute (a selection/sort edge, never a frame)
            public int Compare(int x, int y)
            {
                var a = new Album(_slots[x]); var b = new Album(_slots[y]);
                int c;
                switch (sort)
                {
                    case ReaderSort.Alphabetical:
                        c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase); break;
                    default:
                        bool ya = a.Knows(AlbumFields.Year) && a.Year > 0, yb = b.Knows(AlbumFields.Year) && b.Year > 0;
                        if (ya != yb) return ya ? -1 : 1;
                        c = sort == ReaderSort.Newest ? b.Year.CompareTo(a.Year) : a.Year.CompareTo(b.Year); break;
                }
                if (c == 0) c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = a.Uri.Text.CompareTo(b.Uri.Text);
                return c != 0 ? c : x.CompareTo(y);
            }
        }

        /// <summary>FNV-1a over the block slots + saved bits, so the list keys on the SEQUENCE and nothing else.</summary>
        public static string OrderKey(ReadOnlySpan<ReaderBlock> blocks)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < blocks.Length; i++) { h ^= (uint)blocks[i].AlbumSlot; h *= 1099511628211UL; h ^= blocks[i].Saved ? 1u : 0u; h *= 1099511628211UL; }
            return blocks.Length + ":" + h.ToString("x16");
        }
    }
}
```

> N: `BlockComparer` copies the slot span once per compute because `Span<int>` cannot be captured in a struct field;
> the compute runs on a selection/sort/scope edge or a table publish, never per frame — say so in the file and keep
> the comparer's title reads through `Entities.Strings.Resolve` (it allocates nothing; the interned string is
> returned). If `Album.Title` resolves through a cache miss in a hot compute, hoist a `StringId` compare instead.

### 5.7 `Entities/Artist.Reader.cs` — the UI half: `Reader` (owner N)

```csharp
public readonly partial struct Artist
{
    /// <summary>Re-pushed props. The three Signal INSTANCES are the page's persisted state; <paramref name="AlbumKey"/>
    /// is the search select-in-place's target (read once on mount/change, then cleared by the reader).</summary>
    public sealed record ReaderProps(int Slot, Signal<int> Scope, Signal<int> Sort, Signal<string> AlbumKey)
    {
        public bool Equals(ReaderProps? other) => other is not null && other.Slot == Slot && ReferenceEquals(other.Scope, Scope) && ReferenceEquals(other.Sort, Sort) && ReferenceEquals(other.AlbumKey, AlbumKey);
        public override int GetHashCode() => Slot;
    }

    public sealed class Reader : Component
    {
        const float SpineW = 56f, SubRailH = 44f, SpineHideBelow = 1200f;
        int _artist;
        ReaderProps? _p;
        ReaderBlock[] _blocks = new ReaderBlock[32];
        int[] _saved = new int[32], _scratch = new int[96], _perm = new int[96];
        int _count, _library;
        bool _narrow;
        readonly Signal<int> _current = new(0);                 // the spine's highlighted block (scroll-spy)
        readonly Signal<int> _blocksVersion = new(0);           // bumps when the block array is rebuilt (the spine's Flow.For source)
        readonly ItemsViewController _ctl = new();
        readonly ListOptions _options;
        readonly Func<ReaderShapeKey> _compute;
        readonly Func<int, float> _extentOf;
        readonly Func<int, Element> _blockAt;
        readonly Action _demandBand, _demandBlocks, _seekAlbumKey, _playAll, _shuffle, _goArtist;
        readonly Action<int> _jump;
        readonly Action<int, int> _onVisible;
        readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) _geometry;
        readonly Action<RectF> _onBounds;
        Memo<ReaderShapeKey>? _shape;
        IOverlayService? _overlay;
        string _lastKey = "";

        public Reader()
        {
            _compute = Compute;
            _extentOf = i => (uint)i < (uint)_count ? ReaderShape.ExtentOf(in _blocks[i], _narrow) : 320f;
            _blockAt = BlockAt;
            _demandBand = () => { var a = new Artist(_artist); if (a.IsValid) Entities.Ensure(a, ArtistFields.Identity | ArtistFields.Stats); };
            _demandBlocks = DemandBlocks;
            _seekAlbumKey = SeekAlbumKey;
            _playAll = () => { var a = new Artist(_artist); if (a.IsValid) Playback.PlayContext(a.Id); };
            _shuffle = () => { var a = new Artist(_artist); if (!a.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(a.Id); };
            _goArtist = () => { var a = new Artist(_artist); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Name)); };
            _jump = i => { if ((uint)i < (uint)_count) _ctl.StartBringItemIntoView(i, alignmentRatio: 0f, animate: true); };
            _onVisible = OnVisibleRange;
            _geometry = (ProjectSpy, UpdateSpy);
            _onBounds = r => { bool narrow = r.W > 0f && r.W < SpineHideBelow; if (narrow != _narrow) { _narrow = narrow; _blocksVersion.Value = _blocksVersion.Peek() + 1; } };
            _options = new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None, Controller = _ctl, Grow = 1f, Overscan = 2,
                Scroll = new ScrollOptions { ScrollKey = "lib:reader", OnScrollGeometryChanged = _geometry, AutoEdgeFade = true },
                OnVisibleRange = _onVisible,
            };
        }

        public override Element Render()
        {
            _p = UseProps<ReaderProps>();
            _overlay = UseContext(Overlay.Service);
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Artists.Changed.Value; _ = scope.Albums.Changed.Value; _ = scope.Tracks.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value; _ = scope.Edges.AlbumArtists.Changed.Value; _ = scope.Edges.SavedAlbums.Changed.Value;
            _artist = _p.Slot;
            _shape = UseComputed(_compute);
            UseEffect(_demandBand, DepKey.From(_artist, (int)epoch));
            UseEffect(_demandBlocks);                                                 // auto-tracked: re-runs as edges/scope land
            var shape = _shape.Value;
            UseEffect(_seekAlbumKey, _p.AlbumKey.Value + "|" + shape.OrderKey);

            var a = new Artist(_artist);
            bool named = a.IsValid && a.Knows(ArtistFields.Name);
            string key = "reader:" + _artist + ":" + shape.Scope + ":" + shape.Sort + ":" + shape.OrderKey;
            Element list = new BoxEl
            {
                Key = key, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Direction = 1,
                Children = [ItemsView.Create(shape.Count + 1, _blockAt, RepeatLayout.Extents(_extentOf, 320f), _options)],   // +1: the trailing facet line
            };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true, OnBoundsChanged = _onBounds,
                Children =
                [
                    Band(a, named, shape),
                    SubRail(shape),
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Basis = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                        Children = _narrow ? [list] : [Spine(), list],
                    },
                ],
            };
        }

        // ── shape ────────────────────────────────────────────────────────────────────────────────────────────────────

        ReaderShapeKey Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = e.SavedAlbums.Changed.Value; _ = e.AlbumArtists.Changed.Value; _ = e.AlbumTracks.Changed.Value;
            _ = e.ArtistAlbums.Changed.Value; _ = e.ArtistSingles.Changed.Value; _ = e.ArtistCompilations.Changed.Value;
            _ = scope.Albums.Changed.Value;
            int artist = _artist;
            int scopeWord = _p!.Scope.Value;
            var sort = (ReaderSort)Math.Clamp(_p.Sort.Value, 0, 2);
            if (artist <= Table.None) { _count = 0; return ReaderShapeKey.Empty; }

            int saved = User.LibraryAlbumsOf(artist, _saved);
            if (saved > _saved.Length) { _saved = new int[Math.Max(saved, _saved.Length * 2)]; saved = User.LibraryAlbumsOf(artist, _saved); }
            var albums = e.ArtistAlbums.Targets(artist); var singles = e.ArtistSingles.Targets(artist); var comps = e.ArtistCompilations.Targets(artist);
            int cap = saved + albums.Length + singles.Length + comps.Length;
            if (_scratch.Length < cap) { _scratch = new int[cap]; _perm = new int[cap]; }
            if (_blocks.Length < cap) _blocks = new ReaderBlock[cap];
            _count = ReaderShape.Build(artist, scopeWord, sort, _saved, saved, albums, singles, comps, _scratch, _perm, _blocks, out _library);
            _blocksVersion.Value = _blocksVersion.Peek() + 1;
            string order = ReaderShape.OrderKey(_blocks.AsSpan(0, _count));
            if (order != _lastKey)
            {
                Log.Event(WaveeLogLevel.Info, "ui", "library.reader.shape", "Artist reader reshaped", null, -1, null,
                    WaveeLogField.Of("artist", artist), WaveeLogField.Of("scope", scopeWord), WaveeLogField.Of("library", _library), WaveeLogField.Of("blocks", _count));
                _lastKey = order;
            }
            return new ReaderShapeKey(_count, _library, order, scopeWord, (int)sort);
        }

        // ── demand: the WHOLE model, batched by the runner ───────────────────────────────────────────────────────────

        /// <summary>Library scope: every saved album's tracks edge + its rows (Visible). All-releases scope: the three
        /// facets' first pages (Artist.DemandDiscography, Visible), DiscoCard for every listed release, then their
        /// tracks edges + rows at Prefetch — the runner batches 300/POST and keeps four in flight; the page never
        /// windows. Page N+1 of a facet is asked by OnVisibleRange when the realized window reaches the tail
        /// (Artist.DemandNextPage — the scroll-paced contract the discography already has).</summary>
        void DemandBlocks()
        {
            _ = Entities.ScopeEpoch.Value;
            var e = Entities.Current.Edges;
            _ = e.SavedAlbums.Changed.Value; _ = e.AlbumArtists.Changed.Value; _ = e.AlbumTracks.Changed.Value;
            _ = e.ArtistAlbums.Changed.Value; _ = e.ArtistSingles.Changed.Value; _ = e.ArtistCompilations.Changed.Value;
            int scopeWord = _p!.Scope.Value;
            var a = new Artist(_artist);
            if (!a.IsValid) return;
            if (scopeWord == 1) DemandDiscography(a);
            for (int i = 0; i < _count; i++)
            {
                var b = _blocks[i];
                var priority = b.Saved ? FetchPriority.Visible : FetchPriority.Prefetch;
                var album = new Album(b.AlbumSlot);
                if (!album.IsValid) continue;
                if (!b.Saved) Entities.Ensure(album, AlbumFields.DiscoCard, priority);
                if (e.AlbumTracks.State(b.AlbumSlot) == EdgeState.Unknown && !e.AlbumTracks.IsFailed(b.AlbumSlot))
                    Entities.EnsureEdge(FetchEdge.AlbumTracks, b.AlbumSlot, 0, priority);
                var tracks = album.TrackSlots;
                if (tracks.Length > 0) Entities.Ensure(Entities.Current.Tracks, tracks, (uint)(TrackFields.Row | TrackFields.Audio), priority);
            }
        }

        void OnVisibleRange(int first, int last)
        {
            if (_p!.Scope.Peek() != 1 || last < _count - 3) return;      // the tail of what landed is realized → next pages
            var a = new Artist(_artist);
            DemandNextPage(a, DiscoFacet.Albums); DemandNextPage(a, DiscoFacet.Singles); DemandNextPage(a, DiscoFacet.Compilations);
        }

        /// <summary>The search select-in-place wrote AlbumKey: scroll that block to the top once, then clear the key so a
        /// later re-render never re-scrolls. A key absent from the blocks is ignored (and cleared).</summary>
        void SeekAlbumKey()
        {
            string key = _p!.AlbumKey.Peek();
            if (key.Length == 0) return;
            int slot = User.SlotOfKey(EntityKind.Album, key);
            for (int i = 0; i < _count; i++) if (_blocks[i].AlbumSlot == slot) { _ctl.StartBringItemIntoView(i, 0f); break; }
            _p.AlbumKey.Value = "";
        }

        // ── scroll-spy (compositor-cheap: a coarse key, two signal writes) ───────────────────────────────────────────

        long ProjectSpy(ScrollGeometry g) => _ctl.TryGetItemIndex(0f, 0.2f, out int i) ? i : -1;
        void UpdateSpy(ScrollGeometry g) { if (_ctl.TryGetItemIndex(0f, 0.2f, out int i)) _current.SetIfChanged(Math.Min(i, _count - 1)); }

        // ── the pieces ───────────────────────────────────────────────────────────────────────────────────────────────

        Element Band(Artist a, bool named, ReaderShapeKey shape)
        {
            string uri = a.IsValid ? a.Uri.Text : "";
            int songs = User.LibrarySongCountOf(_artist);
            string line = named
                ? Strings.Library.InYourLibrary(shape.Library == 1 ? Strings.Library.OneAlbum : Strings.Library.NAlbums(shape.Library), songs > 0 ? Strings.Library.NSongs(songs) : "")
                : "";
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.S),
                Children =
                [
                    new BoxEl { Width = 72f, Height = 72f, Shrink = 0f, Corners = Radii.Circle(72f), ClipToBounds = true, Shadow = Elevation.Card,
                                Children = [Controls.Artwork(named ? Controls.ArtUrl(a.ImageId) : null, 72f, 72f, 36f, decodePx: 144)] },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                        Children =
                        [
                            new BoxEl
                            {
                                Corners = Radii.ControlAll, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Margin = new Edges4(-Spacing.S, 0f, -Spacing.S, 0f),
                                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = _goArtist,
                                Children = [ new TextEl(named ? a.Name : "…") { Size = 32f, LineHeight = 38f, Weight = 600, CharSpacing = -10f, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                                                                                 BrushTransitionMs = Design.Motion.Faster, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ],
                            }.Interactive(Interaction.Subtle),
                            new TextEl(line) { Size = 12.5f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                    Controls.Play(Tok.AccentDefault, _playAll, Loc.Get(Strings.Library.PlayAll)),
                    Album.CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), _shuffle),
                    Embed.Comp(() => new Controls.FollowButton { Uri = uri, Name = named ? a.Name : null }) with { Key = "follow:" + uri },
                    Album.CommandCircle(Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), _goArtist),
                ],
            };
        }

        /// <summary>Sticky under the band: the scope words · spacer · the sort words. Two word rails over the page's
        /// Signal instances; the "all releases" word carries the total when the facets have answered.</summary>
        Element SubRail(ReaderShapeKey shape) => new BoxEl
        {
            Direction = 0, Height = SubRailH, AlignItems = FlexAlign.Center, Shrink = 0f, Fill = Tok.FillCardDefault,
            Padding = new Edges4(Spacing.XL, 0f, Spacing.XL, 0f),
            ScrollBinds = [new() { PinTop = 0f }],
            Children =
            [
                User.ScopeRail(_p!.Scope, Prop.Of(() => TotalReleases())),
                new BoxEl { Grow = 1f },
                User.ReaderSortRail(_p.Sort),
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.End },
            ],
        };

        int TotalReleases()
        {
            var e = Entities.Current.Edges;
            return e.ArtistAlbums.Total(_artist) + e.ArtistSingles.Total(_artist) + e.ArtistCompilations.Total(_artist);
        }

        /// <summary>The cover spine: one 36-px dot per block, bound ring on the current one. Rebuilt on _blocksVersion
        /// (a shape edge), never per scroll.</summary>
        Element Spine() => new BoxEl
        {
            Width = SpineW, Shrink = 0f, Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.M, 0f, Spacing.M),
            ScrollBinds = [new() { PinTop = SubRailH }],
            Children = [Flow.For(() => { _ = _blocksVersion.Value; return _count; }, i => i, (i, _) => Dot(i))],
        };

        Element Dot(int i)
        {
            var a = new Album(_blocks[i].AlbumSlot);
            return Controls.Named(new BoxEl
            {
                Width = 36f, Height = 36f, Shrink = 0f, Corners = CornerRadius4.All(4f), ClipToBounds = true, Cursor = CursorId.Hand,
                Role = AutomationRole.Button, Focusable = true, OnClick = () => _jump(i),
                Opacity = Prop.Of(() => _current.Value == i ? 1f : _blocks[i].Saved ? 0.5f : 0.3f), HoverOpacity = 0.9f,
                BorderWidth = 2f, BorderColor = Prop.Of(() => _current.Value == i ? Tok.AccentDefault : ColorF.Transparent), BrushTransitionMs = Design.Motion.Fast,
                Children = [Controls.Artwork(Controls.ArtUrl(a.ImageId), 36f, 36f, 3f, decodePx: 72)],
            }, a.Title);
        }

        /// <summary>One block: cover + caption | head (title link · meta · ▶ ♥ ⋯) + rows. A block whose card or tracks
        /// have not landed is the SAME builder under Skel.Region (a counted skeleton, TrackCount rows); a failed tracks
        /// edge is the head + a Retry note. The last index is the trailing facet line.</summary>
        Element BlockAt(int i)
        {
            if (i >= _count) return Trailing();
            var b = _blocks[i];
            var a = new Album(b.AlbumSlot);
            var e = Entities.Current.Edges;
            bool cardReady = a.IsValid && a.Knows(AlbumFields.Title);
            bool rowsReady = e.AlbumTracks.State(b.AlbumSlot) == EdgeState.Complete && Detail.NoticeRules.ForAlbum(MemoryMarshal.Cast<int, Track>(a.TrackSlots)) == DetailNotice.None;
            var loadable = cardReady && rowsReady ? Loadable<int>.Ready(i) : Loadable<int>.Pending();
            return Skel.Region(loadable, () => Block(b, a, real: false), _ => Block(b, a, real: true), SkelReveal.Soft) with { Key = "blk:" + b.AlbumSlot };
        }

        Element Block(in ReaderBlock b, Album a, bool real)
        {
            float cover = _narrow ? ReaderShape.CoverEdgeNarrow : ReaderShape.CoverEdge;
            int slot = b.AlbumSlot;
            string uri = a.IsValid ? a.Uri.Text : "";
            Element rows;
            if (b.Failed)
                rows = Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Library.SongsFailed),
                    onAction: () => Entities.RefreshEdge(FetchEdge.AlbumTracks, slot));
            else
            {
                var tracks = a.TrackSlots;
                int n = real ? tracks.Length : b.Rows;
                var kids = new Element[n];
                bool art = false;   // the block's cover IS the art; rows never repeat it
                for (int r = 0; r < n; r++)
                {
                    if (!real) { kids[r] = Track.ShimmerRow(in ReaderCols, ReaderTracks, ReaderShape.RowH, 0f) with { Key = "sh:" + r }; continue; }
                    var t = new Track(tracks[r]);
                    int trackSlot = tracks[r];
                    kids[r] = Track.EagerRow(t, r + 1, in ReaderCols, ReaderTracks, ReaderShape.RowH,
                        () => Playback.PlayContext(LibraryRows.IdOf(EntityKind.Album, slot), LibraryRows.IdOf(EntityKind.Track, trackSlot)),
                        new Track.EagerRowOptions(ShowTrackArtist: false)) with { Key = "t:" + trackSlot };
                }
                rows = new BoxEl { Direction = 1, Padding = new Edges4(0f, 0f, 0f, ReaderShape.RowsPadBottom), Children = kids };
            }
            string caption = (a.Knows(AlbumFields.Year) && a.Year > 0 ? a.Year + " · " : "") + Detail.Text.KindLabel(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);
            string meta = real ? Detail.Identity.For(a).Meta ?? "" : "";
            return new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Start, Padding = new Edges4(Spacing.L, ReaderShape.BlockPadTop, Spacing.XL, ReaderShape.BlockPadBottom),
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 1, Gap = Spacing.S, Shrink = 0f, Width = cover, ScrollBinds = [new() { PinTop = SubRailH + Spacing.M }],
                                Children =
                                [
                                    new BoxEl { Width = cover, Height = cover, Corners = Radii.CardAll, ClipToBounds = true, Shadow = Elevation.Card,
                                                Children = [Controls.Artwork(Controls.ArtUrl(a.ImageId), cover, cover, Radii.Card, decodePx: 256)] },
                                    new TextEl(caption) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                ],
                            },
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, MinHeight = ReaderShape.HeadH,
                                        Children =
                                        [
                                            new BoxEl
                                            {
                                                Corners = Radii.ControlAll, Padding = new Edges4(Spacing.XS, 2f, Spacing.XS, 2f), Margin = new Edges4(-Spacing.XS, 0f, 0f, 0f),
                                                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, MinWidth = 0f, Shrink = 1f,
                                                OnClick = () => { var al = new Album(slot); if (al.IsValid) Shell.GoTo(Shell.For(al.Uri, al.Title)); },
                                                Children = [ new TextEl(a.Title) { Size = 20f, LineHeight = 26f, Weight = 600, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ],
                                            }.Interactive(Interaction.Subtle),
                                            new TextEl(meta) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f },
                                            new BoxEl { Grow = 1f },
                                            Album.CommandCircle(Icons.Play, Strings.Library.PlayAlbum(a.Title), () => Playback.PlayContext(LibraryRows.IdOf(EntityKind.Album, slot))) with { Fill = Tok.AccentDefault },
                                            Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = a.Title }) with { Key = "save:" + uri },
                                            Detail.UI.MoreButton(() => { var al = new Album(slot); return al.IsValid ? Album.MoreMenu(al, _overlay) : null; }, 32f, 15f, round: true),
                                        ],
                                    },
                                    rows,
                                ],
                            },
                        ],
                    },
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(Spacing.XL, 0f, Spacing.XL, 0f) },
                ],
            };
        }

        /// <summary>The list's last item: "Fetching N more releases…" while any facet is Partial (scope 1), "Everything
        /// {name} has released is in your library" when scope 1 lists nothing beyond the saved set, else 16 DIP of air.</summary>
        Element Trailing()
        {
            var e = Entities.Current.Edges;
            int scopeWord = _p!.Scope.Peek();
            bool partial = e.ArtistAlbums.State(_artist) == EdgeState.Partial || e.ArtistSingles.State(_artist) == EdgeState.Partial || e.ArtistCompilations.State(_artist) == EdgeState.Partial;
            string text = scopeWord == 1 && partial ? Strings.Library.FetchingMore(TotalReleases() - _count)
                        : scopeWord == 1 && _count == _library && _library > 0 ? Strings.Library.AllInLibrary(new Artist(_artist).Name) : "";
            return new BoxEl { Height = text.Length > 0 ? 48f : Spacing.L, Padding = new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.XL), Children = text.Length > 0 ? [new TextEl(text) { Size = 12.5f, Color = Tok.TextTertiary }] : [] };
        }

        // # | title | ♥ | duration — the prototype's `28 | 1fr | 32 | 52` (library-rework-mica.html `.block .trow`).
        // No per-row "…" (the head owns the album's menu), and the heart is TRAILING and revealed on row hover.
        static readonly Track.ColumnSet ReaderCols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: false, Thumb: false, Actions: false, HeartTrailing: true);
        static readonly TrackSize[] ReaderTracks = [TrackSize.Px(28f), TrackSize.Star(1f), TrackSize.Px(Track.Lane.HeartTrailing), TrackSize.Px(52f)];
        static readonly Track.EagerRowOptions ReaderRowOptions = new(ShowTrackArtist: false, HeartRevealOnHover: true);
    }
}
```

> **Engine facts N must honour.** (1) `RepeatLayout.Extents` is the analytic path; a block whose `TrackCount` lands
> after mount changes its extent — the shape memo re-runs on `Albums.Changed`, `_blocks[i].Rows` changes, and the
> layout re-reads `_extentOf(i)` only if the engine re-seeds on a count/version change. If it does not, call
> `_ctl.CorrectMeasuredExtent(i, ReaderShape.ExtentOf(in _blocks[i], _narrow))` from the shape compute for every block
> whose `Rows` changed (the drawer idiom in `DetailTracks`). The VerticalSlice `ScrollSuite` gate for `Extents` is the
> reference; the orchestrator verifies it before wave L2 closes. (2) **`ScrollBinds PinTop` works INSIDE the
> `ItemsView`, and that is where the reader's chrome belongs** (corrected 2026-09-18 — the earlier reading of this note,
> "the sub-rail is simply above the list, not sticky; the block's cover `PinTop` must be deleted", was wrong and is
> what shipped the flat first round). `BakeScrollBinds` resolves the nearest **`Scrollable` ancestor**, and an
> `ItemsView` viewport qualifies; it re-bakes wholesale on every re-render, so a recycled slot's bind self-cleans.
> `ApplyPin` is then a containing-block-clamped `position:sticky` — one change-gated `LocalTransform` write per frame,
> no allocation. So the reader is **ONE scroller** whose flat index space is `0` band · `1` sub-rail · `Prefix + b`
> block · last tail: the band scrolls away because it is an item, the sub-rail pins because it is item 1 of
> `PersistentPrefixCount = 2` carrying `.Sticky(0)`, and the recyclable suffix is guillotined by one shared
> `ScrollOptions.ItemClipTopInset` (+ `ItemClipTopFadeBand`) at the rail's lower edge — the hero system's own shape
> (`Track.Table.VerticalList`). The block's cover `PinTop` **stays**: it is clamped by its own block's row
> (`AlignItems.Start`), which is exactly the prototype's `.block .side{position:sticky; top:64px}`. Two hard rules fall
> out. (a) The persistent prefix and the item clip are **BOUND-path only** (`ItemsView.CreateBound`); the unbound
> recycler forces the prefix to 0. (b) A sticky node's containing block is its IMMEDIATE parent, and a component anchor
> is layout-transparent — it mirrors its child's size — so a sticky root returned FROM a component has `limit = 0` and
> never pins: the sub-rail item must be handed to the list as a RAW element, whose parent is then the scroller's
> content node. (3) `Skel.Region(Loadable<int>, shimmerSource, content, reveal)` — use the three-arg overload from
> `SkeletonRegion.cs:96` exactly; `Loadable<T>.Ready/Pending` are the engine's factories — check their spelling in
> `Hooks/Loadable.cs`. (4) `Track.EagerRow` is a component per row with its own hover signal — 12 rows × 6 realized
> blocks = 72 components, which is the Recents page's budget and fine; do **not** reach for `Track.Table` per block.
> (5) `Album.CommandCircle` (§5.5) is the shared secondary-verb circle; the reader's ▶ overrides its `Fill` to the accent. (6) `Detail.UI.MoreButton(Func<ContextMenuModel?>, size, glyph, round)` is `static` (private) in `Detail.UI.cs:1536` — M makes it `internal`; `Album.MoreMenu(Album, IOverlayService?)` is `Album.Page.cs:387`'s `MoreMenu()` lifted into `Album.UI.cs` as a static over a handle (the page calls it too).

`User.ScopeRail(Signal<int> scope, Prop<int> total)` and `User.ReaderSortRail(Signal<int> sort)` are two 20-line
variants of `WordRail` in `User.UI.cs` (owner O2): the words are `Strings.Library.Scope.InLibrary` /
`Strings.Library.Scope.AllReleases` (+ " · N" when `total > 0`) and `Strings.Library.ReaderSort.Newest` / `Oldest` /
`Alphabetical`; same visuals, no direction flip.

### 5.8 `Platform/Platform.cs` (owner O1)

```csharp
        /// <summary>The artists reader's scope (0 in your library · 1 all releases). Per kind for symmetry with the other
        /// library keys; only "artists" reads it today.</summary>
        public static SettingKey<int> LibraryScope(string kind) => new("library." + kind + ".scope", 0);
        // LibraryAlbumDesc / LibraryAlbumView / LibraryAlbumSize: ORPHANED by the 2026-09-17 rework (the discography grid is
        // gone). Kept — persisted strings never change — and unread; do not renumber or repurpose.
```

### 5.9 Loc keys (owner O2) — `assets/loc/en-US.json`, under `"library"`

```json
    "sort": { "recents": "recents", "recentlyAdded": "added", "alphabetical": "a–z", "creator": "artist", "releaseDate": "year", "albums": "albums" },
    "scope": { "inLibrary": "in your library", "allReleases": "all releases" },
    "readerSort": { "newest": "newest", "oldest": "oldest", "alphabetical": "a–z" },
    "openAlbum": "Open album",
    "playAll": "Play all",
    "alsoBy": "Also by {name} in your library",
    "inYourLibrary": "In your library: {albums}{songs}",
    "oneAlbum": "1 album",
    "nAlbums": "{n} albums",
    "nSongs": " · {n} songs",
    "savedIn": "saved {year}",
    "songsFailed": "Couldn't load the songs.",
    "playAlbum": "Play {name}",
    "fetchingMore": "Fetching {n} more releases…",
    "allInLibrary": "Everything {name} has released is already in your library.",
    "selectArtist": "Pick an artist",
    "selectAlbum": "Pick an album"
```

The existing `sort.*` values change from Title Case labels ("Recently added") to the rail's lowercase words — **the
sidebar's Library V3 shares `library.sort.*` for codes 0–3** (`Sidebar.Modes.cs:591`, ch 25). Its pill labels would
turn lowercase too. Decision: the rail gets its **own** keys (`library.rail.*`) and `LibraryWordRail.WordKey` points
there; `library.sort.*` stays as is. (O2 applies this; the snippet above is the *rail* block under `"rail"`.)
`viewFullAlbum` and `selectAlbumTracks` are deleted with their consumers. `ko-KR.json` / `nl.json` receive the same
keys with the English values (a translator's follow-up, not this plan's).

---

## 6. Data & readiness rules (the contract every owner codes against)

1. **The navigator demands identity for every saved row** (`AlbumFields.Identity` ⊇ `Artists`; unchanged). The
   artist row's counts are pure reads over `SavedAlbums` ∩ `AlbumArtists` — no new demand.
2. **`Album.Pane` demands** `AlbumFields.Detail` once per (slot, scope), the `AlbumTracks` edge while Unknown and not
   failed, and `TrackFields.Row|Audio|Tags|Video` for every listed track (auto-tracked on the edge's `Changed`).
   It never asks `Publishing` (no trailing band on this surface — ch 05 §9's warning holds).
3. **Readiness is `AlbumPaneReadiness.Of`** — four states, pure, tested. `Header` is short-lived (the navigator
   already asked); `Rows` paints `ShimmerRows` counted rows; `Failed` paints the strip with Retry (`RefreshEdge` +
   a re-armed row batch); `Ready` mounts the table. **No `MinifiedAlbum` notice on this surface** — an unnamed row
   after a Complete edge is either still loading (shimmer) or failed (strip).
4. **`Artist.Reader` demands** the band's `ArtistFields.Identity|Stats`; in scope 0 every saved block's tracks edge +
   rows at Visible; in scope 1 additionally the three facets' first pages (Visible, `DemandDiscography`), `DiscoCard`
   for each listed release, and every catalogue block's tracks edge + rows at **Prefetch**. Facet pages beyond the
   first are scroll-paced (`DemandNextPage` from `OnVisibleRange`, the discography's own rule). The whole model is
   asked; the runner batches (300 uris, four in flight) — the page keeps no window.
5. **A block is a counted skeleton until both its card and its rows answered**; a failed tracks edge is the head +
   Retry; a facet failure is the discography's existing Retry vacancy (owner N reuses
   `Artist.Discography.cs:657`'s arm at the reader's trailing item).
6. **Zero allocation on a scroll frame**: navigator rows are bound slots (unchanged); reader blocks are the
   RenderItem path — a realize allocates the block once (bounded by overscan), a steady scroll frame allocates
   nothing. The scroll-spy writes two signals per geometry *change*, never per frame.
7. **Remount keys**: navigator `view:size:OrderKey:FactsKey:LettersKey`; reader
   `artist:scope:sort:OrderKey`. Selection is never an input to either. `ScrollKey`s: `lib:nav:<kind>` (kept),
   `lib:reader` (one per artists page; the key changes with the list's `Key`, and the engine namespaces by the
   KeepAlive slot).
8. **Persisted per kind**: `leftw`, `sort`, `desc`, `view`, `size`, `selected`, `albumkey`, `album.sort` (the
   reader's), `scope` (new). Filter text: not persisted (unchanged).

---

## 7. Motion

| Where | What | Token |
|---|---|---|
| Word rail | ink opacity .5 → .85 → 1; underline fill | `Design.Motion.Faster` (83) brush fade |
| Letter overlay | translate by push (bound transform) | none (compositor) |
| Jump strip / spine click | bring-into-view | strip: unanimated (a jump is a jump); spine: `animate: true` glide |
| Spine ring | border colour | `Design.Motion.Fast` (167) |
| Pane selection change | the pane re-skins in place; the table remounts under its own `ScrollKey` | none added (ch 15 §5: no crossfade on selection) |
| Block reveal | `Skel.Region` fade | `SkelReveal.Fade` |
| Also-by tile hover | `Interaction.Subtle` | engine recipe |
| Reduced motion | the engine's value, never a branch | — |

Nothing here reads a frame clock; nothing animates a layout channel.

---

## 8. Waves, owners, gates

**Wave L1 — CORE + helpers (O1, O2, M-a, Q in parallel; disjoint files).**
- O1 (Opus): `User.cs` §5.1 (+ `LibraryNavSort.Albums`, the sorter arm, `ILibraryNavRows.Count`,
  `LibrarySelectionCommit` depth), `Platform.cs` §5.8, the per-row `Table.IsFailed(slot, group)` if absent (the
  20-line `Column<uint> Failed` in `Entities.cs`, cleared on commit), and the tests: `LibraryLettersTests` (Of: "the
  Beatles" → B, "…But Seriously" → B, "[05]" → #, "Ölüm" → #, ""→ #; Build: header positions, flat count, offsets,
  StickyLetterAt at the boundaries, Key stability), `AlbumPaneReadinessTests` (the 4-state table, ShimmerRows),
  `LibraryWordRailTests` (WordsFor per kind, Clamp, the codes never renumber: `(int)LibraryNavSort.Albums == 5`),
  `LibrarySelectionCommitTests` (ForAlbum artists depth 1), the `Albums` sort arm in `LibraryNavOrderTests`.
- O2 (Sonnet): `User.UI.cs` §5.2 (WordRail, ViewToggle, ViewPanel, LetterHeader, StickyLetter, JumpStrip,
  ScopeRail, ReaderSortRail, the two subtitle caches; delete the pill/panel/disco rows), the loc JSON ×3.
- M-a (Sonnet): `Show.UI.cs` `Show.PaneHeader`; `Detail.UI.ArtistLine` moved; `Track.TableHeaderShim` +
  `Track.EmbeddedColumns/EmbeddedTracks` in `Track.Table.Chrome.cs`.
- Q (Sonnet): the fake seed (§9).
- N-a (Opus): `Artist.Reader.cs` CORE half (§5.6) + `ArtistReaderShapeTests` (Build: library-first, scope 0 vs 1,
  the three sorts incl. unknown-year sinking, union dedup vs saved, ExtentOf arithmetic, OrderKey stability).

Gate L1 (orchestrator): Debug build, Release build, `Wavee.Tests` green (baseline + the new facts). Nothing
renders differently yet — `User.Page.Library.cs` still compiles against the old helpers *only if* O2's deletions are
staged as a second commit inside L1; **simpler: O2 keeps the old builders until L2 and deletes them in L2 with O3**
(O2 has no file in L2, so the deletion is O3's one edit outside its file — say so in the dispatch).

**Wave L2 — the three surfaces (M, N-b, O3 in parallel).**
- M (Opus): `Album.Pane.cs` §5.5.
- N-b (Opus): `Artist.Reader.cs` UI half §5.7.
- O3 (Opus): `User.Page.Library.cs` §5.3 + the deletions (§3, §5, §6 of the file; the old `User.UI.cs` builders).

Gate L2 (orchestrator, the only one who builds): Debug + Release clean; tests green; `dotnet run --project
src/apps/Wavee -- --fake` renders **albums** and **artists** in their loaded state (W1, W2 after choosing a–z, W3,
W4 after choosing all releases, W5 loading/failed via the fake's failing album, W6 at 1120, W7 at 620); the
always-on lines `library.nav.remount` (reason `letters` on the a–z switch) and `library.reader.shape` appear once
per change; `FG_REUSE_GUARD=1` in Debug reports nothing on selection clicks; the nav-measure harness
(`ops/tools/nav-measure.ps1`) shows no allocation ticks during a reader scroll. Then the side-by-side against the
prototype at 1440 dark and light — the prototype is this plan's parity reference; deviations are listed and either
fixed or recorded in §10.

**Wave L3 — docs (orchestrator).** Chapter 15 amendments (§10), the CHANGELOG bullets with their issue numbers
(§12), a handoff note. Commit only when Christos says so.

---

## 9. The fake seed (owner Q)

`Entities.Fake.Library.cs`'s `LibrarySeed` must produce, for the `--fake` gate: ≥ 2 followed artists with ≥ 2 saved
albums each (one with ≥ 3, so the spine and the also-by strip have something to show), ≥ 1 followed artist with
**0** saved albums (the row reads the old "Artist" word; the reader shows the empty library scope with the "all
releases" word carrying a count), each artist with ≥ 2 catalogue-only releases across the facets (so W4's counted
skeletons hydrate through the fake's answer path), one album whose tracks edge is seeded **Failed** (W5's strip), and
titles that exercise the letter rule (`"…But Seriously"`, `"[05]"`, `"The Wall"`, a Hangul title). No new fake arms;
the existing seed shapes are extended with rows.

---

## 10. Chapter 15 amendments (orchestrator, wave L3) and the open decisions

Amendments to `wavee-0.3-ui/15-library.md`, logged in its §11 with the date:
1. §0.5: "four view types × three sizes" → the codes stay; the UI exposes list/grid + the ViewPanel for compact and
   size. The discography column's second control is deleted with the column.
2. §0.3: the title row carries the count.
3. W1 → this plan's W1; W2 → W3/W4; W7 → W5; W8 (discography skeleton) → deleted; W9's three placeholders → two;
   W14–W17 depths; W2's letters and W8's rail are new wireframes (numbered W28–W30 to keep the chapter's numbering).
4. §7: the readiness table gains `AlbumPaneReadiness` (four states) and the reader's demand rules (§6 here);
   `Album.Notice` is no longer consumed on this surface.
5. §9's "Album.Page.cs must carry two components": settled as `Album.Pane.cs`, a named partial, owner M; the
   `Artist.DiscographyPane` row becomes `Artist.Reader` in `Artist.Reader.cs`, owner N. The 2026-09-13 file-local
   placement is superseded.
6. §8: `LibraryLetters`, `LibraryWordRail`, `AlbumPaneReadiness`, `ReaderShape` join the pure-rule table with their
   tests.
7. §0.8 stays; the prototype's art wash is recorded as *not built* (a later decision, off by default if ever).

Open decisions, with the default this plan takes:
- **The Plays lane leaves the pane** (`ShowPlays = false`). Ch 05 lists the embedded arm as a real arm; this is a
  behaviour change, taken deliberately: plays live on the full page. Default: yes.
- **"albums" sort (code 5)** for the artists rail. Default: yes; it is what the prototype shows and the count is a
  free read.
- **Sticky sub-rail / sticky block cover** inside the virtualized reader: not in this plan (engine note in §5.7).
- **The reader's rows are `Track.EagerRow`**, not `Track.Table`: no multi-select, no drag-reorder, no column sort in
  a block — those are the album page's. Default: yes.
- **Podcasts** keep their pane and gain the word rail only.
- **0.2.x**: the 0.2.10 mechanisms (the 24 h seal, the index-0 readiness) are *not* patched by this plan; they need
  their own issues against `main` (§12) if a 0.2.11 is ever cut.

---

## 11. Verification checklist (the orchestrator's, after wave L2)

- [ ] `dotnet build Wavee.slnx` and `-c Release` clean (`TreatWarningsAsErrors`).
- [ ] `dotnet test src/apps/Wavee.Tests` — baseline count + `LibraryLettersTests`, `AlbumPaneReadinessTests`,
      `LibraryWordRailTests`, `ArtistReaderShapeTests`, the changed `LibrarySelectionCommitTests` fact, the `Albums`
      arm in `LibraryNavOrderTests`.
- [ ] `--fake`: albums page renders W1 loaded; a–z → W2 with letters + strip; strip click lands the header at the
      top, unanimated; the sticky letter pushes under the next header; grid view keeps the strip, drops the headers.
- [ ] `--fake`: artists page renders W3; scope → W4 with counted skeleton blocks that hydrate; the spine highlights
      the block under the top and glides on click; 1120 hides the spine; 620 collapses to crumbs with depth 1 = the
      reader.
- [ ] W5: the seeded failed album shows the strip; Retry re-arms and lands the rows.
- [ ] Selection click: `FG_REUSE_GUARD=1` silent; `library.nav.remount` does **not** fire; the pane re-skins in
      place with its `lib:tracks:<uri>` scroll memory.
- [ ] Reader scroll: no allocation ticks (nav-measure); `library.reader.shape` fires once per scope/sort/artist
      change, not per scroll.
- [ ] Side-by-side against the prototype at 1440 dark + light: header geometry (cover 128, 28/34 title, the command
      row), the rail's three ink levels, the letter plate, the block grid (120 | 1fr, 36-px rows), the spine.
- [ ] The engine's own gates untouched (no engine change in this plan; if an engine prop is needed — bound `Weight`,
      `IsEnabled` — that is a separate `..\fluent-gpu` change with its VerticalSlice run, and this plan's §5.2 has
      the no-engine fallback).

---

## 12. Issues

Every fix references its issue (CLAUDE.md). None is filed yet — filing is a modifying `gh` call and needs
Christos's approval through the `github-triage` skill. The three to file, so the CHANGELOG bullets and the commit
bodies can carry their numbers:
1. **Library rework: one album pane, the artist reader, word rails and letters** — the feature issue this plan
   implements (milestone 0.3).
2. **Library album pane can wait forever / shows the minified notice instead of retrying** — the 0.3 defect (W24
   + the notice), closed by `Album.Pane`'s failure arm.
3. **Artists page: three squeezed columns and a whole-skeleton discography gate** — closed by `Artist.Reader`.
Plus, against `main` for 0.2.x if wanted: the 24 h Rich seal skipping the row-naming step
(`AlbumHydration.cs:85-90`), and `HydrationLevels.Of(Artist)` sampling `TopAlbums[0]` (`HydrationLevel.cs:96`).

The CHANGELOG bullets end with `(#n)`; the commit bodies carry `Fixes #n`; the release script's `issue refs` gate
refuses a mismatch.

---

## 13. As built (2026-09-18)

Deviations the owners took from §5-§9 above, one line each, by owner. Chapter 15's §12 audit log entry for this
rework (`wavee-0.3-ui/15-library.md`) is the doc-facing record; this is the code-facing one.

- **O1** — `ILibraryNavRows.CountOf(int)`, not `Count(int)` (a name clash with the `Count` property).
- **O1** — `LibraryLetters.Build<TRows>` is generic, not fixed to `LibraryRows`.
- **O1** — `LibraryRows` gained an optional `counts` buffer plus `FillCounts`, not a bare `Count(int)` read.
- **O1** — `Table.Failed` is cleared both in the planner and in `Applied`, not in `Applied` alone.
- **O2** — the rail's active-word weight is two stacked `TextEl`s fading by a bound `Color`, not a bound `Weight`
  (engine `TextEl` has no bound `Weight`/`Opacity`; §5.2's engine note anticipated this but not the exact shape).
- **O2** — the rail uses its own loc keys, `library.rail.*`, not the plain `Strings.Library.Sort.*` keys §5.1 named.
- **O2** — the album subtitle cache is keyed by (artist name id, year), not by album slot alone.
- **M** — the pane's counted shimmer is a purpose-built row, not `Track.ShimmerRow` (which paints nothing).
- **M** — the pane crossfades via a slot-keyed child with an Enter/Exit `LayoutTransition` — §7's "no crossfade on
  selection" is superseded.
- **M** — `RowsFailed` is inline, not a memo.
- **N** — the reader is an `IPropsHost`, not a plain `Component` over a frozen props record.
- **N** — its list is keyed `artist:scope:sort:arm:generation` with a `CountSignal` — the `OrderKey` is deliberately
  NOT in the key, so a landed facet page never remounts.
- **N** — extents are corrected through `ItemsViewController.CorrectMeasuredExtent`, not the plan's `_extentOf`
  analytic seed alone.
- **N** — each album is a per-album `Loadable` cell, not a bare readiness enum per block.
- **N** — the block's ▶ is `Controls.PlayFab`, not a hand-rolled fab; head meta renders through
  `Detail.Text.AlbumMeta`.
- **O3** — the sticky letter and its "present" flag are written from an effect, not read inline during `Render`.
- **O3** — letters are built for grid views too, not list views only.
- **O3** — the navigator is one `Skel.Region` gated on `NavShape.Answered`, not a hand-rolled shimmer branch.
- **O3** — `NavRow` carries an explicit `Height`, not an implicit one from its children.
- **O3** — collapsed artists SEARCH keeps depth 2 (the browse reader is one pane, but search's three columns are
  untouched — chapter 15 §0 #9 and the corrected W14-W17 note both hold).
- **Gate** — the footprint gate rose 1,470,000 → 1,560,000 for `Table.Failed`.
- **Gate** — the fake seed's saved-album count is 18.
