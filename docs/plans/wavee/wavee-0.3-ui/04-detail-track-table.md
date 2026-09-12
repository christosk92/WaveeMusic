# Detail track table (album / playlist / liked / show track lists) — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Detail/DetailTracks.cs` (4,525) · `Features/Detail/DetailTrackTableRules.cs` (279) ·
> `Features/Detail/DetailTrackCommandBarLayout.cs` (119) · `Features/Detail/TrackFilterFlyout.cs` (472) ·
> `Features/Detail/TrackFilterModel.cs` (290) · `Features/Detail/TrackExpandedFacts.cs` (367) ·
> `Features/Detail/DetailQueueActions.cs` (85) — 6,137 lines primary; plus the shared
> `Components/TrackRow.cs` (1,136, chapter 01), `Components/SelectionCommandBar.cs` (394),
> `Components/ContentFilterChips.cs` (107), `Components/TrackFactsStrip.cs` (296),
> `Components/MembershipDiff.cs` (119), `Components/TrackVersionsPanel.cs` (411),
> `Features/Detail/PlaylistReorderRules.cs` (126), `Features/Detail/PlaylistListState.cs` (41),
> `Features/Detail/DetailRevealRamp.cs` (27), `Features/Detail/DetailLayoutBreakpoints.cs` (89).
> | 0.3 target: the **four-file** track surface — `Entities/Track.cs` (CORE) · `Track.UI.cs` (the row) ·
> `Track.Table.cs` (this chapter's list) · `Track.Drawer.cs` (the expanded-row body) — plus the track section of the
> shared detail frame (chapter 03). The file split, the per-file line numbers and the ownership are
> `01-track-row.md` §9's **"The track surface file plan (01 + 04 reconciled — the one authority)"**, which
> supersedes this chapter's earlier three-file answer (arbitration 2026-09-12) | owner **M** for all four
> `Track.*` files, split across two slots: `Track.cs`, `Track.UI.cs` and `Track.Table.cs` (this chapter's list) in
> **Wave 4.5**, `Track.Drawer.cs` in **Wave 5**; owner **O** (playlist/liked) configures the table through
> `TableProfile` from `Playlist.Page.cs` / `User.Page.cs`, in Wave 5, and never edits it.
>
> Cross-references: `00-design-system.md` (tokens, type ramp, cover palette, motion curves, `Surfaces.Artwork`),
> `01-track-row.md` (the row CELL — `TrackRow.Grid`, `# ↔ play` transport, equalizer, heart, badges, menus, drag chip),
> `02-cards-and-controls.md` (`ArtCard` used by the recommendation rows), `03-detail-frame.md` (hero, context band,
> rail, sticky collapse, skeleton band, page tone plane), `05-album.md` / `06-playlist.md` / `07-liked-songs.md`
> (each page's own configuration of this table), `09-show-episode-module.md` (a SHOW renders `EpisodeList`, **not**
> this table — see §1), `19-shell-overlays.md` (toasts raised by this surface's failures).
>
> **Show-page note, stated once:** `DetailConfig.Show` sets `Content = DetailContent.Episodes`
> (`DetailConfig.cs:224-228`) and `DetailShell` then mounts `EpisodeList`, never `TrackList`
> (`DetailShell.cs:525-531`). This chapter therefore covers album / playlist / Liked / Local-files / prerelease and
> the Library master-detail embed; the show's episode table is chapter 09's. Everything below that says "the table"
> means `TrackList`.

---

## 0. The non-negotiables

1. **Header and rows are built from ONE `TrackSize[]` and ONE `ColumnSet`.** The column header grid
   (`DetailTracks.cs:2429-2434`) and every row grid (`TrackRow.cs:330-337`) take the same `tracks` array, the same
   `ColGapFor(tier)` and the same `padX`; a row's left inset is `padX − RowInset` inside a skin margin of `RowInset`,
   which sums to exactly the header's `padX` (`DetailTracks.cs:1905`, `TrackRow.cs:334`). A column heading must sit
   over its own values at every width, in every skin, at every density. Nothing else in this surface is allowed to
   compute a lane width. **Exception, same arithmetic:** Classic rows — and the vertical arm's stacked "plain" rows —
   carry NO skin margin, so their grid pays the full `padX` directly (`TrackRow.cs:334-335`, `DetailTracks.cs:3491`).
   `ArtCentreIndent` subtracts `RowInset` only for the Modern skin for exactly this reason (`:644`).
2. **Two ladders decide lanes, in this order, and only ever subtract.** The width TIER
   (`DetailLayoutBreakpoints.NominalTierFor`, 860/720/560/440/340/300) says which lanes a width may admit; the
   identity-first RELIEF ladder (`DetailTrackTableRules.ReliefFor`) then yields trailing lanes — Plays → BPM·Key →
   Added by → Date added → Album → Artist → thumb → ♥ — until Title (floor 120) and Classic's Artist (floor 90) clear
   their floors (`DetailTrackTableRules.cs:154-216`). `#`, Title, Duration and the trailing "…"/film/chevron lane
   never yield. A 650-DIP Classic Liked table gives up Plays and nothing else (test:
   `TrackRowStyleRulesTests.Relief_UserScenario_ClassicLikedAt650…`).
3. **A breakpoint cross re-skins the realized rows in place; it never remounts the list.** The tier is deliberately
   NOT in the list `Key` (`DetailTracks.cs:1074-1080`): the column shape is a memo the rows read
   (`_rowShape`, `:729-741`), so the viewport — and its scroll offset — survives opening the right rail.
4. **A cold list reveals over frames, never in one.** `DetailRevealRamp`: 12 real rows per frame, capped at 60, driven
   by a `TickerClock` that is mounted only while `_rampActive` is true (`DetailTracks.cs:1142-1149`). Rows past the
   ramp render a BLANK grid of the identical extent — not grey bars — and each row's crossing is a 280 ms opacity fade
   caused by a GridEl→BoxEl type change (`DetailTracks.cs:2794-2813`).
5. **Sort, filter, density, tier and now-playing each re-skin at their own granularity.** Sort → each bound row
   re-reads `View()` (scroll preserved). Now-playing → one row's presentation memo. Filter/density/`_resetEpoch` →
   a keyed remount (`DetailTracks.cs:1089-1090`). Only the last of those is allowed to throw scroll away.
6. **The zebra is `DisplayIndex() % 2 != 0`, and selection never changes the row fill.** Odd display rows carry
   `WaveeColors.RowZebra` + a `Tok.StrokeCardDefault` 1-DIP border; the ONLY selection cue is the 3×16 accent pill at
   the row's left edge, revealed by a bound opacity (`DetailTracks.cs:3512-3532`, `:3582-3589`). The pill is the cue
   for a HIGHLIGHT; once the check lane is in it hands over to the checkbox and drops to 0
   (`!classic && isSel() && !checksVisible`, `:3588`). Classic has no pill: there, and only there, selection tints the
   fill (`RowHover`, `:3512`). The one other thing that touches the rest fill is an `.mp4` hovering the row — it rides
   the same closure (`DropCue()`, `:3460`, `:3514`) rather than adding an overlay node per row.
7. **Rows are zero-allocation on a steady scroll frame.** Bound slots, plain elements, no per-row component except the
   three the design needs (`ExpandableRowSlot` → `BoundRowContent` → optional marquee). The marquee exists on the
   now-playing row ONLY (`DetailTracks.cs:2695-2706`).
8. **A row that cannot play does not offer to.** `IsNotYetOut()` dims the title column to 0.45, withholds the hover
   play button, prints the release date in the duration lane instead of a time, dashes Plays, and is refused by
   `PlayRow` (`TrackRow.cs:226-313`, `DetailTracks.cs:3103`).
9. **Unknown is a dash, never a zero.** `0` plays → `—`; `0 ms` duration → `—`; an album ref with a uri and no name →
   `—` (`TrackRow.cs:296-313`, `:882`, `TrackExpandedFacts.cs:224`).
10. **The command bar promotes, it does not shrink.** Every inline command is icon+label; when the measured pane
    cannot hold one it is EVICTED into the "…" flyout (`DetailTrackCommandBarLayout.Resolve`), with 16-DIP promotion
    hysteresis and a latch that freezes the fit while search is open (`DetailTracks.cs:1950-1958`).
11. **Search is a disclosure, not a permanent box.** 66 DIP of icon+funnel at rest; opening it reflows the row over
    260 ms (`SizeMode.Reflow`, `Easing.SmoothOut`) while the icon↔field cross-fades and the chrome brush fades on the
    SAME duration, so the box expanding and its styling resolving read as one motion (`DetailTracks.cs:226-260`).
12. **Live membership changes are narrated, loads are not.** The FIRST membership a context shows settling into its
    real shape is a load and must NOT choreograph (`_membershipSettled`, `DetailTracks.cs:862-873`); every later
    landing FLIP-glides survivors and fades adds in with a 20 ms/row stagger capped at 8
    (`Choreograph`, `:1546-1598`).
13. **The expanded row states every fact at every width.** The drawer exists because the table's own ladders take
    facts away; it restores Plays · BPM · Key · Duration as display figures plus a prose line and a descriptor line
    (`TrackExpandedFacts.For`, `TrackFactsStrip.Build`), and its connector rail descends from the ROW'S ARTWORK CENTRE
    (`ArtCentreIndent`, `DetailTracks.cs:641-649`).
14. **One drawer at a time, keyed by ROW identity, not by track uri and not by index**
    (`MembershipDiff.RowKey`: `ContextUid` else `uri#@displayIndex`).
15. **Every refusal owes a sentence.** A drop the table will not take shows a caption next to the not-allowed glyph
    (`DropRefusalCaption`, `:1252-1266`); a keyboard block-move that cannot be named speaks + toasts
    (`TryBlockMove`, `:1346-1354`). A silent no-op is a defect.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
TrackList : Component                                    Features/Detail/DetailTracks.cs:28
│  ctor(route, Loadable<DetailModel>, PlaybackBridge?, DetailHandlers,
│        showToolbar, embedded, verticalHeader, verticalHeroHeight, verticalHeroWSeed, liveHandlers)   :269-281
│  mounted by DetailShell.cs:476 (single-column), :537 (two-column / vertical) and LibraryPage.cs:1042 (embedded)
│
├─ ZStack root (Grow 1)                                                                     :1150
│  ├─ column : BoxEl(Direction 1, Grow 1)  — OnBoundsChanged = tier + relief measure        :1099-1136
│  │   ├─ chrome (two-column arm only; the vertical arm puts it in the list)                :911
│  │   │   └─ Chrome(set, tracks, sort, labeled, tier, checkInset, padX, chips, lens)        :1857-1920
│  │   │       └─ BoxEl Key="chrome"  Padding (padX, 8, padX, 0)                            :1902-1907
│  │   │           └─ BoxEl(Direction 1, Margin bottom 4)                                   :1888-1894
│  │   │               ├─ Toolbar(labeled, tier) → Responsive.Of(BuildToolbar)              :1922-1924
│  │   │               │   └─ CommandBarSurface("normal"|"selection"|"compact-selection")   :2044-2069
│  │   │               │       ├─ [normal] SplitButton "Play next" (▸ Add to queue)         :1963-1994
│  │   │               │       ├─ [normal] ToolFx.LabeledButton Shuffle                     :1996-1998
│  │   │               │       ├─ [normal] PlaylistTuneButton (playlist w/ tuning)          :1999-2001, :3726
│  │   │               │       ├─ ToolFx.Separator                                          :2006
│  │   │               │       ├─ SortMenuButton   (radio fields + asc/desc)                :2008-2011, :3938
│  │   │               │       ├─ ListButton       (density flyout → DensityPanel slider)   :2012-2014, :4428
│  │   │               │       ├─ MultiSelectButton                                         :2015-2017, :3643
│  │   │               │       ├─ DetailTrackMoreButton (overflow + column opt-ins + picker):2020-2022, :4338
│  │   │               │       ├─ spacer (Grow 1)                                           :2037
│  │   │               │       └─ SearchHost → DetailTrackSearchField | icon + FilterButton :2025, :2235-2356
│  │   │               │           └─ FilterButton → TrackFilterFlyout (368×620 card)       :4236, TrackFilterFlyout.cs:22
│  │   │               ├─ ContentFilterBar()  (Liked only)  → ContentFilterChips.Build      :2188-2206
│  │   │               ├─ LensHeader()        (Liked / playlist facts lens) → LikedLens.Header :2215-2222
│  │   │               └─ Header(set, tracks, sort, checkInset)  — GridEl + 1-DIP divider   :2393-2454
│  │   │                   ├─ IndexSortCell  (# with symmetric 9-DIP caret slots)           :2492-2516
│  │   │                   ├─ SortCell(SortLabel)   Title|Song  ← owns the Artist sort      :2405-2408, :3695
│  │   │                   ├─ SortCell(HLabel)      Artist / Album / Date added / Plays     :2409-2416
│  │   │                   ├─ PlainHeader           Added by / BPM·Key (not sortable)       :2414, :2419
│  │   │                   ├─ SortCell(Icon Clock | "Time")  Duration                       :2421-2424
│  │   │                   └─ empty cells: ♥ · art · Video · More · Expand (keys only)      :2401-2427
│  │   └─ rightBody = TrailingBody(listKeyed, …) | listKeyed                                :1092-1097, :1801-1846
│  │       └─ listKeyed : BoxEl Key "list:<route>:d<density>:<skin>:q<query>:f<hash>:r<epoch>[:rec]"   :1090
│  │           └─ Skel.Region(_full, shimmer, RealList, reveal: FadeOnly)                   :1065-1069
│  │               ├─ shimmer = RowsShimmer (12 × RowGrid(EmptyTrack)) | VerticalShimmer     :2585-2595, :2554-2567
│  │               └─ RealList()                                                            :950-1046
│  │                   ├─ ListPlaceholder(state, …)  → shimmer | "Nothing here yet" | "No songs match" :420-433
│  │                   ├─ [vertical] VerticalList — hero + chrome as persistent prefix items :1463-1539
│  │                   ├─ [recs]     ItemsView.CreateBound(listTotal, RowOrRecContent, …)    :971-994
│  │                   └─ [flat]     ItemsView.CreateBound(rowItems, ExpandableSlot, …)      :1003-1045
│  │                       └─ ExpandableRowSlot : Component                                  :3208-3365
│  │                           ├─ WrapRowSwipe → RowSwipe.WrapBound (touch only)             :2657-2668
│  │                           │   └─ BoundRowSkin : BoxEl (zebra/hover/press/pill/divider,  :3447-3636
│  │                           │        Draggable, DropTarget(.mp4), context menu, keys)
│  │                           │       ├─ SelectorVisualsBound.BoundCheckLane (28 DIP)       :3578
│  │                           │       └─ BoundRowContent : Component                        :2713-2814
│  │                           │           ├─ [not revealed] ShimmerRow (blank GridEl)       :2625-2637
│  │                           │           └─ RowGrid → TrackRow.Grid (chapter 01)           :3399-3432
│  │                           │               └─ title = BoundTitlePlain | Marquee.Of       :2687-2706
│  │                           └─ [expanded] drawer BoxEl → TrackVersionsPanel                :3315-3352
│  │                               ├─ TrackFactsStrip.Build (hero figures + prose + genres)   TrackFactsStrip.cs:58
│  │                               └─ connected version rows (self · video · alt audio)       TrackVersionsPanel.cs:173
│  └─ revealClock : BoxEl(0×0, HitTestVisible false) → Flow.Show(_rampActive, TickerClock)   :1142-1149
│
├─ external state that survives remounts
│   SelectionModel _selection                                                                :131
│   ItemsViewController _listCtl (scroll offset, anchoring, insertion handoff)                :156
│   SwipeGroup _swipeGroup                                                                    :2655
│   InsertionOptions _insertion (created once, every delegate reads live state)                :1179-1212
└─ per-render memos:  _rowsSnapshot · _rowShape · _rowAccent · _rowItems · _selectedCount ·
    _checksVisible · _selectionCommandsVisible                                                :696-745, :834-849
```

Props-freeze accounting (0.2.9, all verified in code):

| Data | Reaches the row/child by | Site |
|---|---|---|
| model, config, handlers, sort, query, filters, saved-set, appearance flags | `_rowsSnapshot` (`UseComputed`, equality-gated record struct) | :696-718 |
| column set + width tracks + art size | `_rowShape` (`UseComputed`) — read INSIDE the row, never a ctor arg | :729-741, :2738 |
| accent | `_rowAccent` (scalar memo) → bound `Fill` on the pill | :724, :3586 |
| the track for a slot | `_rowItems.BindItem(scope.Index)` (`BoundItems.Project`) | :719-723, :2830 |
| live `DetailHandlers` | `IReadSignal<DetailHandlers?> _liveHandlers` published by DetailShell in an effect | :65, DetailShell.cs:444-448 |
| expanded row | `Signal<string> _expandedRow` | :197 |
| reveal progress | `Signal<int> _reveal` read through an equality-gated per-row bool memo | :127, :2742 |
| density / filter / skin change | **`Key` remount** of the whole list | :1089-1090 |
| tier change | **NOT** a remount — a memo the rows read | :1074-1080 |

Three arm-level differences the wireframes above assume but do not state:

* **The vertical/hero arm's command bar is not the two-column one.** `vertical: true` drops Play from the mandatory
  set and never offers Shuffle at all (`DetailTrackCommandBarLayout.cs:85`, `:106`) — the hero owns Play — so its bar
  is Sort · Row size · Select · "…" · search, and the sticky band's right cluster is the three plateless words of W17.
* **The list `Key` is not the same in the vertical arm.** `filterKey` is EMPTY there (`DetailTracks.cs:1089`), so a
  query or filter change re-skins without a remount (the hero/chrome prefix items must not be torn down); the two-
  column arm still remounts on `:q<query>:f<hash>`. The key also carries `vh:` to keep the two arms' scroll memory apart.
* **Selection is per-config.** `Extended` on album / playlist / Liked; `None` on a SINGLE (`DetailConfig.cs:210`) and
  on a Show — no check lane, no selection bar, no Select command. The vertical arm additionally drops to `None` while
  the list is empty (`:1500`).

### 1.2 The 0.3 composition

| 0.2.9 node | 0.3 target | Input |
|---|---|---|
| `TrackList` | `Track.Table` (static partial, `Entities/Track.Table.cs` — NEW file, §9) | `Table(in TableArgs)` where `TableArgs = (ReadOnlySpan<Track> rows, TableProfile profile, Signal<TableView> view, IReadSignal<Handlers> live)`; `rows` comes from `Edges.AlbumTracks/PlaylistTracks/Liked.Targets(parent)` |
| `DetailModel` | the parent handle (`Album`/`Playlist`/`User`) + its edge; NO copied list | handle + `Edges.X.Version[parent]` signal |
| `TrackRowsSnapshot` | `Memo<TableSnapshot>` over `(Entities.Current.Tracks.Changed, view signal, appearance epoch)` | equality-gated record struct, same contract |
| `RowShape` memo | `Memo<RowShape>` over `(tierSignal, reliefSignal, densitySignal, profile)` | pure `Track.Lanes(...)` in the CORE section of `Track.cs` |
| `ItemsView.CreateBound(rowItems, …)` | unchanged engine call; source = `BoundItems.Project(snapshot, count, indexer, default(Track))` over slots | slot ints, not records |
| `ExpandableRowSlot` / `BoundRowContent` | `Track.Slot(scope)` / `Track.RowContent(scope)` in `Track.Table.cs` | `scope.Index` signal |
| `RowGrid` → `TrackRow.Grid` | `Track.Row(in BoundItemScope<Track>, in RowShape)` in `Track.UI.cs` (chapter 01) | handle + shape, both live |
| `Chrome`/`Header`/`Toolbar` | `Track.Chrome(...)`, `Track.Header(...)`, `Track.CommandBar(...)` in `Track.Table.cs` | shape memo + view signal |
| `TrackFilterFlyout` / `FilterButton` / `SortMenuButton` / `ListButton` / `MultiSelectButton` / `DetailTrackMoreButton` | same names, `Track.Table.cs` (they are 620 lines of leaf UI) | `Signal<TableView>` + `TrackFilterCapabilities` as re-pushed props |
| `SelectionCommandBar` | `Controls.SelectionBar` (`Platform/Controls.cs`, chapter 01) | `SelectionModel` + `Func<int, Track>` |
| `TrackVersionsPanel` + `TrackFactsStrip` + `FormatSplitButton` (`TrackVersionsPanel.cs:322`) | the drawer **body** is `Track.Drawer(...)` in **`Entities/Track.Drawer.cs`** — **not** `Track.UI.cs` (arbitration 2026-09-12, `01-track-row.md` §9). Its **mount, keying and reflow animation stay here**, in `Track.Table.cs`: the drawer's only call site in the whole app is this table (`DetailTracks.cs:3293-3348`; `grep` finds no other caller), so the row file must not own it | `Track.DrawerModel` pushed as a **context**, exactly as 0.2.9 does — `Ctx.Provide(TrackVersionsPanel.Props, model, …)` (`:3347`) over a body keyed `"drawer-body:" + rowKey` (`:3348`) — plus the `"drawer:"` clip box and `"drawer-presence:"` box (`:3317`, `:3341`) |
| `DetailTrackTableRules`, `TrackLane`, `DetailTrackCommandBarLayout`, `TrackFilterModel`, `TrackExpandedFacts`, `DetailRevealRamp`, `PlaylistListState`, `PlaylistReorderRules`, `MembershipDiff` | CORE section of `Entities/Track.cs` (pure, engine-free, test-included) | see §8 |

Props-freeze in 0.3 — the same three mechanisms, and the same three traps:

* **Signal** for everything live: the shape memo, the view (sort/query/filters/density), the snapshot, the accent,
  the reveal count, the expanded row key. A `Component` that takes any of these as a constructor field freezes it
  (`ReuseGuard` catches the remount, not the freeze).
* **`Key` remount** for density, skin, filter-set and the reset epoch ONLY.
* **Context** for `Overlay.Service`, `ActionServices`, `LibraryBridge`, `Services` — `UseContext` at the top of the
  table's render, cached into fields for the handlers below (0.2.9: `:654-657`).

---

## 2. Wireframes

All boxes are ~8 DIP per monospace character. Every number is the live value at default settings
(density 1 = Default, Modern skin, artwork shown, BPM·Key off, Plays off — `Platform/AppSettings.cs:65,68,71,83,90`).

### W1 — Playlist, fully loaded, tier 0 @ right pane 1060 DIP (window ≈1400)

```
│◀16▶                                                                                                        ◀16▶│
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ ← chrome pad (16, 8, 16, 0)
│ [▸ Play next|▾] [⤮ Shuffle]  │  [⇅ Custom order ▾] [≣ Default ▾] [☑ Select]   [⋯]          [🔍  ▽ ]           │ 44 DIP CommandBarSurface (pad 6/5)
│                                                                                                       ◀ 66 ▶   │ commands 32 high, gap 2, sep 17,
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 8 before the search host; 4 margin-bottom
│  #  ♥   ▣  Title                                       Album                      Date added   🕐     ⋯    ⌄   │ 36 DIP header row (⌄ lane: no label)
│ 28  28  32 ◀────────── 365 (star 1) ──────────▶  ◀───── 273 (star .75) ─────▶      88          52     40   26  │ colgap 12 · padX 16
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP StrokeDividerDefault
│  1  ♡  [▣] Sunset Boulevard                            Neon Horizon               3 days ago   3:41   ⋯    ▸   │ row 48, zebra OFF (even)
│      ▐                                                                                                        │
│  2  ♥  [▣] Weird Fishes                                In Rainbows                Sep 28       4:12   ⋯    ▸   │ row 48, zebra ON  (odd)
│                                                        ↑ subline: artist · album only when the Album lane is off
│  3  ♡  [▣] An Ending (Ascent)                          Apollo                     Jul 30       4:35   ⋯    ▸   │
│            └ 14/20/600 TextPrimary; 12/16 Caption subline (artists as per-artist link spans)                   │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
Lanes (playlist, `ShowVersions: true` → the chevron lane is up at every tier < 6, `DetailConfig.cs:200`):
  # 28 · ♥ 28 · art 32 · Title* · Album* · Date 88 · Duration 52 · Actions 40 · Expand 26  = 9 columns, 8 gaps
Star pool = 1060 − 2·16 (padX) − 8·12 (gaps) − (28+28+32+88+52+40+26) = 638 → Title 364.6 · Album 273.4
(The trailing lane is Actions 40 **or** Video 28 — never both: `TrackRow.MoreButton` rides INSIDE the film lane
when a video exists, `DetailTrackTableRules.cs:68-70`. `--fake` has no video, so it is Actions here.)
```

### W2 — Album, tier 0 @ 1060 (no thumb, no Album lane, Plays always on, top-track ★)

```
│  #  ♥  Title                                                                    Plays       🕐      ⋯    ⌄    │ 36
│ 28  28 ◀─────────────────────── 730 (single star) ───────────────────────▶       52         52      40   26   │
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│  ★  ♥  Sunset Boulevard                                                        60.0M       3:41    ⋯    ▸    │ ★ = top-track star in the # cell
│  2  ♡  Weird Fishes                                                            30.0M       4:12              │
│  3  ♡  Unreleased Track                                    (opacity 0.45)         —         4 Sep             │ not-yet-out: dash + release date
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
Star pool = 1060 − 32 − 6·12 (7 columns) − (28+28+52+52+40+26) = 730. A SINGLE (`DetailConfig.Single`,
`DetailConfig.cs:210`) is this table with `Selection = None`: no check lane, no selection bar, no Select command.
```

### W3 — tier 3 @ 500 DIP (right rail open, or a narrow window) — Album/Date/Plays gone, BPM·Key still allowed

```
│◀16▶                                                                   ◀16▶│
│ [▸ Play next|▾] [⤮ Shuffle] │ [⇅ ▾]  [⋯]                   [🔍 ▽]        │ evicted: Density, Select → the "…" flyout
├───────────────────────────────────────────────────────────────────────────┤
│  #  ♥   ▣  Title                                            🕐     ⋯    ⌄ │ 36
│ 28  28  32 ◀──────── 190 (star) ────────▶                   52     40  26 │ Title floor 120 cleared → relief 0
├───────────────────────────────────────────────────────────────────────────┤
│  1  ♡  [▣] Sunset Boulevard                                3:41    ⋯    ▸ │ 48
│  2  ♥  [▣] Weird Fishes                                    4:12    ⋯    ▸ │
└───────────────────────────────────────────────────────────────────────────┘
Star pool = 500 − 32 − 6·12 − (28+28+32+52+40+26) = 190. Relief check: MinWidthFor = 28+120+52 + 28+32+40+26
+ 6·12 + 32 = 430 ≤ 500 → step 0.
Tier bands (DetailLayoutBreakpoints.cs:11, hysteresis 24 DIP on widening only, :48-56):
  ≥860 t0 │ ≥720 t1 (drop Added by) │ ≥560 t2 (drop Album) │ ≥440 t3 (drop Date + Plays)
  ≥340 t4 (drop BPM·Key, padX 12) │ ≥300 t5 (drop ♥ + thumb, colGap 8) │ <300 t6 (drop the whole trailing lane, padX 8)
```

### W4 — tier 6 @ 280 DIP (ultra-compact; "…" reachable only by right-click)

```
│◀8▶                       ◀8▶│
│  #  Title            🕐     │ 36 · colGap 8 · padX 8
│ 28  ◀─── 168 ───▶    52     │
├─────────────────────────────┤
│  1  Sunset Boulev…   3:41   │ 48
│  2  Weird Fishes     4:12   │
└─────────────────────────────┘
```

### W5 — cold load (model Pending): `Skel.Region` shimmer, chrome held open

```
│ [▸ Play next|▾] [⤮ Shuffle] │ [⇅ ▾] [≣ ▾] [☑]  [⋯]        [🔍 ▽]        │ chrome is a SIBLING of the boundary →
├───────────────────────────────────────────────────────────────────────────┤ it never shimmers (two-column arm)
│  #  ♥   ▣  Title                       Album           Date   🕐      ⋯   │
├───────────────────────────────────────────────────────────────────────────┤
│ ▒▒  ▒▒ ▒▒▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒         ▒▒▒▒▒▒▒▒▒       ▒▒▒▒   ▒▒▒▒        │ 12 rows, derived by the engine from
│ ▒▒  ▒▒ ▒▒▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒             ▒▒▒▒▒▒▒         ▒▒▒▒   ▒▒▒▒        │ RowGrid(EmptyTrack) — ONE source
│ … 12 × rowH (48) …                                                        │ SkelReveal.FadeOnly, no per-row stagger
└───────────────────────────────────────────────────────────────────────────┘
```

### W6 — reveal ramp in flight (frame 1 of a cold open, `_reveal = 12`)

```
│  1  ♡ [▣] Sunset Boulevard            Neon Horizon   3 d ago  3:41   ⋯ │ real (fading 0→1 over 280 ms)
│  …                                                                     │ rows 0..11 real
│ 12                                                                     │ rows ≥12: ShimmerRow — an EMPTY GridEl
│ 13                                                                     │ of the same columns + rowH, nothing painted
│ 14                                                                     │ (deliberately blank, not grey bars)
└────────────────────────────────────────────────────────────────────────┘
next frame: 24 real, then 36, then Done (DetailRevealRamp.Next; target = min(visible, 60))
```

### W7 / W8 / W9 — empty, no-match, membership not landed

```
┌── W7 empty playlist ─────────────┐ ┌── W8 filter matched nothing ─────┐ ┌── W9 membership loading ─────────┐
│                                  │ │                                  │ │ ▒▒ ▒▒ ▒▒▒▒▒▒▒▒▒▒  ▒▒▒▒  ▒▒▒     │
│          Nothing here yet        │ │   No songs match your filter      │ │ ▒▒ ▒▒ ▒▒▒▒▒▒▒▒    ▒▒▒▒  ▒▒▒     │
│       14 px · TextTertiary       │ │       14 px · TextTertiary        │ │ … the same 12-row shimmer …      │
│   pad (16,24,16,24) · centered   │ │   pad (16,24,16,24) · centered    │ │ key "rows:loading"               │
└──────────────────────────────────┘ └──────────────────────────────────┘ └──────────────────────────────────┘
 detail.empty.noTracks               detail.empty.noMatch                  PlaylistListState.Loading
```
There is **no error/offline arm inside the table**: a deleted/unreachable playlist is stated by the page's notice bar
(chapter 03, `DetailNoticeBar`), and the table below it renders whatever membership is resident.

### W10 — hover on row 2 (pointer anywhere in the row)

```
│  2  ♥  [▣] Weird Fishes             In Rainbows        Sep 28    4:12    ⋯  │
│  ▲                                                                      ▲   │
│  │ number fades to 0, a 24-DIP ▶ (or ⏸ when this row is now-playing)     │  HoverFill = RowHoverZebra (odd) / RowHover
│  │ fades to 1 — both on the row's HoverFade channel (83 ms FluentPopOpen)│  border → StrokeCardDefault
│  └ the transport box is the single-click play target                    └── "…" 0.45 → 1.0 (MoreRestOpacity)
```

### W11 — now-playing row

```
│ ▮▯▮ ♥  [▣] Weird Fishes ⟵ marquee    In Rainbows       Sep 28    4:12    ⋯  │
│  ▲          ▲                                                               │
│  │          └ Marquee.Of, 14/20/600, Tok.AccentTextPrimary                  │
│  └ WaveeEqualizer: 3 bars 2.5×13, gap 2, r1.25, accent ink, 850 ms loop @30 Hz
│    (pauses while the row is hovered — the bars are behind the transport fade)
```

### W12 — selection mode (2+ rows selected, or the Select toggle on)

```
│ [⌗⌗] 3 selected │ [▶ Play] [▸ Play next] [＋ Add to queue] [♡ Save] │ [✓ Select all] [⋯]        [✕]  │ 44 DIP
│  ▲ up to 3 stacked 28×28 covers, −11 overlap, 2-DIP FillCardSecondary ring                             │
├────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│◀28▶│  #  ♥   ▣  Title                        Album              Date added   🕐     ⋯                  │ header shifts +28
│ [✓]│  1  ♡  [▣] Sunset Boulevard             Neon Horizon       3 days ago   3:41   ⋯                  │ rows: check lane slides in
│ [ ]│  2  ♥  [▣] Weird Fishes                 In Rainbows        Sep 28       4:12   ⋯                  │ from Dx −28 over 333 ms
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
lane width = 4 (left margin) + 20 (checkbox) + 4 = 28 DIP (SelectorVisualsBound.cs:79-85)
**The accent pill and the check lane never show together.** The pill's bound opacity is
`!classic && isSel() && !checksVisible` (`DetailTracks.cs:3588`): with the checkboxes in, the CHECK is the selection
cue and the pill is at 0. The pill is therefore the cue for a plain single-row highlight (Extended click, no Select
toggle) — which is exactly the state W12 is NOT in. Classic never draws a pill at all (its cue is `RowHover`, `:3512`).
selection bar fit tiers by its OWN measured lane: ≥760 labels · ≥390 glyphs · <390 essentials (SelectionCommandBar.cs:117)
bar order: covers (≤3, de-duplicated by cover url) · "N selected" · │ · Play · Play next · Add to queue · Save ·
│ · Select all · ⋯ · spacer · ✕ — gap 3 (`SelectionCommandBar.cs:130-195`). At the essentials tier the evicted verbs
re-appear inside the "⋯" flyout above `Menus.TrackRows(showGoToAlbum: false)` and a trailing "Select all" (`:221-240`).
```

### W13 — search expanded (a 700-DIP pane; Density and Select evicted)

```
│ [▸ Play next|▾] [⤮ Shuffle] │ [⇅ Custom order ▾]  [⋯]   [🔍 Search this list           ✕ │ ▽]      │
│                                                          ◀──────────── 240 …280 ─────────▶       │
│                                                          32 high, r4, FillControlInputActive when focused
│                                                          border StrokeControlDefault, 2-DIP accent underline
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
widths: rest 66 (icon 32 + funnel 32 + gap 2) → min 160 → preferred 240 → max 280 (DetailTrackCommandBarLayout.cs:44-47)
```

### W14 — the filter flyout (anchored BottomEdgeAlignedRight on the funnel)

```
┌───────────────────────────── 368 ─────────────────────────────┐
│ ┌────┐  Filter tracks                                         │ header pad (14,12,14,10); tile 34×34 r10 accent@14 %
│ │ ▽  │  3 active filters                                      │ title 15/650 · status 12 secondary
│ └────┘                                                        │
├───────────────────────────────────────────────────────────────┤ 1 DIP divider, margin 8 l/r
│ SEARCH IN                                                     │ Eyebrow 12/16/600 +30 tracking, TextTertiary
│ [ Everything │ Title │ Artist │ Album ]                        │ Segmented, one 34-DIP row
├───────────────────────────────────────────────────────────────┤
│ CONTENT                                                       │
│  ⚠  Explicit content              [ All │ Hide │ Only ]        │ trait row: 32 min-h, control lane 150
│  🎞 Video tracks                  [ All │ Hide │ Only ]        │
│  ┌──────────────┐ ┌──────────────┐                            │ status grid: 2 × star, gaps 5
│  │ ♥ Liked only │ │ ✓ Available… │                            │ checkbox rows 30 min-h, r4, FillSubtleTransparent
│  └──────────────┘ └──────────────┘                            │
├───────────────────────────────────────────────────────────────┤
│ MORE FILTERS                                                  │
│  🕐 Duration                       Any duration            ⌄  │ Expander header 42 min-h; value right, 12 tertiary
│  📅 Date added                     Any time                ⌄  │ one open at a time; opening scrolls it into view
│  ♪  Tempo                          Any tempo               ⌄  │ after 170 ms
│  ♪  Source                         Any source              ⌄  │ RadioButtons, maxColumns 2
├───────────────────────────────────────────────────────────────┤ scroll region caps at 500
│ No filters applied                        [ Clear all filters ]│ footer 46 high, pad (14,6,10,6), label 11 tertiary
└───────────────────────────────────────────────────────────────┘ card max height 620
```
**The card is not a fixed six-section form — every facet below "Search in" is capability-gated**
(`TrackFilterFlyout.cs:309-372`, capabilities from `FilterCapabilities`, `DetailTracks.cs:2365-2388`):
Liked only (`HasLibrary`) · Available only (`HasUnavailable`) · Date added (`HasDateAdded`) · Tempo (`HasTempo`,
i.e. at least one kind-222 row has landed) · Source (`HasMixedOrigin`). A facet already SET stays on screen even when
its capability goes false, so a filter can always be cleared. Explicit + Video traits and Duration are unconditional.
The status grid is 2 star columns when both checkboxes exist and ONE when only one does (`:317-327`) — never a lone
checkbox in a half-width cell. Camelot / artist / release-year facets are **not** in this card: they are lens facets
set from the rail (chapter 07) and show as pills in W18.

### W15 — expanded row (drawer open under row 2)

```
│  2  ♥  [▣] Weird Fishes             In Rainbows       Sep 28    4:12    ⌄  │ chevron 24×24, glyph swaps ▸→⌄, accent
├─ row keeps its plate; bottom corners square (6,6,0,0) ─────────────────────┤
│        ╷                                                                   │ drawer: same zebra parity, corners (0,0,6,6)
│        │  30.0M     128     8B        4:12                                 │ StatHero figures + Caption labels
│        │  Plays     BPM     Key       Duration                             │ wrap row, gap 20 + 20 right margin
│        │                                                                   │
│        │  Added Saturday, 28 September 2024 · In Rainbows · Released …     │ prose line, Caption 12/16, middot-joined
│        │  Indie · Explicit · Music video                                   │ genre/flag line, Caption tertiary
│        │                                                                   │
│        │  VERSIONS AND FORMATS                                             │ Eyebrow, margin-top 12
│        ├──▶ [▣ 43] This track                        FLAC 16/44            │ version rows 51 high (43 + 2×4,
│        │                                                                   │ TrackVersionsPanel.cs:59)
│        ├──▶ [▭ 76×43] Music video                                          │ 16:9 thumb = video, square = audio
│        └──▶ [▣ 43] Alternate mix                                           │ last row: rail stops at its own stub
│        ▲                                                                   │
│        └ indent = ArtCentreIndent(set, art) − 7 ; the rail lands on the ROW'S ARTWORK CENTRE
│          tier-0 modern with ♥ + 32 art: 16−8 + 28 + 12 + 28 + 12 + 16 = 104 → rail at 104 (drawer pad-left 97+7)
```

### W16 — drag insertion (same-playlist reorder, 2 rows)

```
│  1  ♡ [▣] Sunset Boulevard        …                                       │ dragged rows: opacity 0.4, stay in place
│  2  ♥ [▣] Weird Fishes            …                                       │
│ ═══════════════════════════════════ insertion line ════════════════════════│ engine-owned; gap opens to the preview
│ ┌──────────────────────────────────────────────────────────────────────┐  │ card: FillSolidSecondary, 1-DIP accent
│ │ [▣32] Sunset Boulevard                                               │  │ border, Elevation.Card, r4,
│ │       Radiohead                                                      │  │ margin 8 l/r, pad 8 l/r, height = rowH
│ └──────────────────────────────────────────────────────────────────────┘  │ ≤3 cards; the last carries "+N" (Pill,
│  3  … rows below FLIP down                                                │ AccentSubtle)
└───────────────────────────────────────────────────────────────────────────┘
chip caption:  "Move 2 songs" (same list) │ "Add 12 songs" (copy) │ "Add to <playlist>" (container, count unknown)
refusal:       "Clear sorting to reorder" │ "Clear filters to reorder" │ "Still syncing — try again in a moment"
               │ "Can't edit this playlist" │ "Still loading…" │ "Nothing to add" │ "Can't add an artist"
same-list drop does NOT dim the app (SpotlightWhen false); a cross-list deposit keeps the scrim.
```

### W17 — vertical / hero arm, scrolled (sticky band engaged) @ page 520

```
┌──────────────────────────────────────────────────────────┐
│  Neon Horizon                   Find   Filter   Play     │ 56 DIP compact identity row (chapter 03)
│  #  ♥  ▣  Song                          Time      ⋯      │ 36 DIP column header — the band's LOWER stratum
├──────────────────────────────────────────────────────────┤ 1 DIP hairline = the band's single rule
│ ░░ rows are CLIPPED at this line, not drawn under it ░░  │ ItemClipTopInset = 56 + 36 + 1 = 93
│  7  ♡ [▣] Sunset Boulevard              3:41      ⋯      │ (+48 with the Liked chip rail → 141; +36 with a lens)
│  8  ♥ [▣] Weird Fishes                  4:12      ⋯      │ ItemClipTopFadeBand = 24 DIP feather
└──────────────────────────────────────────────────────────┘
Header label is "Song"/"Time" in this arm, "Title"/🕐 elsewhere (DetailTracks.cs:2405-2424).
Rows in the STACKED flow (page < 424) are PLAIN: no zebra, no pill, no inset, no border (:3487).
```

### W18 — Liked Songs chrome (chips + lens + header)

```
│ [▸ Play next|▾] [⤮ Shuffle] │ [⇅ Date added ▾] [≣ ▾] [☑]  [⋯]      [🔍 ▽] │ default sort = Date added, descending
├───────────────────────────────────────────────────────────────────────────┤
│ ( All ) ( Mellow ) ( K-Pop ) ( Energetic ) ( Chill ) …→                    │ chips 32 high, r999, pad 12 l/r, gap 8
│                                                                           │ rail 40 high + 8 gap = 48 extent,
│ [ Liked Jul 27 – Aug 3  ✕ ] [ vaultboy  ✕ ]   1,204 songs                 │ one line, horizontal scroll + edge fade
│  ▲ lens pills 28 high, r999, AccentSubtle fill, AccentSecondary border    │ lens header 28 + 8 = 36 extent
├───────────────────────────────────────────────────────────────────────────┤
│  #  ♥   ▣  Title                    Album            Date added  🕐    ⋯  │
```

### W19 — "Recommended songs" (owned/collaborative playlist, scrolled to the bottom)

```
│ 40  ♡ [▣] Last real track            …                       4:01    ⋯    │
│───────────────────────────────────────────────────────────────────────────│
│ Recommended songs                                                    [⟳]  │ header row: BodyStrong, min-h rowH,
│                                                                           │ pad 16 l/r; ⟳ 32×32 r16; spinner while loading
│ [▣40] Some Suggestion                                       ＋  3:12      │ rec rows: TrackRow.ArtCard, art 40,
│        Artist Name                                                        │ "+" 28×28 bordered circle, duration 12/16
│ [▣40] Another One                                           ＋  2:48      │ clipped to rowH; never joins the selection
```
Three states this section has that the frame above does not show: **loading** — the ⟳ is replaced by a 32×32 box
holding `TrackRow.Spinner()` (`DetailTracks.cs:2971-2973`); **loaded-empty** — the caption "No suggestions right now"
(12, `TextTertiary`) sits to the LEFT of the refresh button (`:2967`); **not live** — `svc.RealExtender` null, not an
owned/collaborative playlist, embedded or vertical ⇒ the header row does not exist at all (`:926-932`), and the list
total is just the track count. The header's own MOUNT is the lazy first fetch (`:2952-2958`); Refresh re-fetches with
the accumulated skip set, so a batch never repeats (`RecBatch` 20, `:184`).

### W20 — unplayable / not-yet-out / local row

```
│  9  ♡ [▣] Unreleased Track                                    —     4 Sep │ title column Opacity 0.45; no hover ▶;
│                                                                           │ Plays "—"; duration lane = release date
│ 10  ♡ [▣] My Local File                                    —     3:55    │ Local: Origin filter "Local files";
│            └ subline carries no "LOCAL" badge in the row (the drawer flags it)
```

### W21 — the three toolbar flyouts

```
┌─ Sort (SortMenuButton) ────────┐ ┌─ Row size (ListButton) ─────────┐ ┌─ More (DetailTrackMoreButton) ─────┐
│ ◉ Custom order                 │ │  Row size            Cozy       │ │ ⤮ Shuffle            (if evicted)   │
│ ○ Title                        │ │  ├──●──┬───┬───┤  240 min-w     │ │ ⇅ Sort            ▸  (if evicted)   │
│ ○ Artist                       │ │  Compact…Comfortable, step 1    │ │ ≣ Row size        ▸  (if evicted)   │
│ ○ Album      (if the lane can) │ │  slider length 216, thumb tip   │ │ ☐ BPM · Key column   (ShowTempo)    │
│ ○ Date added (if HasDateAdded) │ │  names the level                │ │ ☐ Plays column       (opt-in kinds)  │
│ ○ Plays      (if the lane is on)│ └─────────────────────────────────┘ │ ☑ Select             (if evicted)   │
│ ○ Duration                     │                                    │ ────────────────────────────────────│
│ ───────────────────────────────│                                    │ ＋ Add to playlist / Copy to playlist│
│ ◉ Ascending   ○ Descending     │                                    └────────────────────────────────────┘
└────────────────────────────────┘
```
More-flyout ORDER is exactly `DetailTracks.cs:4359-4409`: the evicted commands first (Shuffle · Sort ▸ · Row size ▸),
then the two column opt-ins (BPM·Key, Plays), then the Select toggle, then — only if anything above it exists — a
separator and the playlist picker. The verb is "Copy to playlist" on a playlist/Liked (`Heart == Follow` or the Liked
uri) and "Add to playlist" on an album (`:4400-4401`). The "…" button itself is never accent-lit (`active: false`,
`:4423`), and Row size is never accent-lit either — density is a view preference, not an active filter (`:4475`).
While its flyout is open the Row-size button FREEZES its label so a drag cannot re-anchor the popup (`:4455-4474`).

### W22 — Classic skin (`WaveeSettings.TrackRowStyle = 1`), tier 0 @ 1060

```
│ #   TITLE                                ARTIST              ALBUM            DATE ADDED   PLAYS   TIME   ⋯ │ 32 DIP header
│     11 px UPPERCASE, +30 tracking, TextTertiary / TextSecondary when active                                 │
├────────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ 1   Sunset Boulevard  ·  Radiohead                            Neon Horizon     Sep 28       12.0M   3:41  ⋯ │ 40 DIP row (density 1)
├────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ per-row 1-DIP divider, inset padX
│ 2   Weird Fishes  🎞  ·  Radiohead        Radiohead            In Rainbows      Sep 27       30.0M   4:12  ⋯ │ inline film glyph (tier < 4)
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
Classic: no art thumb ever, no zebra, no pill, square corners, 14/20 factual cells, dedicated Artist lane to tier 3,
selection = RowHover fill, EXPLICIT drawn as a word-mark pinned to the Title lane's right edge.

Four more Classic-only facts the Modern wireframes do not show:
* **The `#` header cell is EMPTY** — `set.Classic ? new BoxEl() : IndexSortCell(sort)` (`DetailTracks.cs:2400`).
  Classic has no clickable `#`, no caret slots and therefore no header route back to the original order; the Sort
  flyout's "Custom order" is it.
* **The now-playing rest state is a speaker glyph, not the equalizer** — `Icon(Icons.Volume, 13, accent)`
  (`TrackRow.cs:1071`). No top-track ★ and no chart ▲▼ glyph either: both are Modern-only arms of the same switch.
  The whole row's factual ink turns `AccentTextPrimary` instead (`TrackRow.cs:227-229`).
* **The trailing "…" is fully hidden at rest** (opacity 0 → 1 on row hover), and it carries no hover/press scale
  (`TrackRow.cs:975-995`). Modern's is quiet-but-present at `MoreRestOpacity` 0.45.
* **The row grid pays the full `padX`** with no skin margin (`TrackRow.cs:334-335`, `DetailTracks.cs:3491`), and the
  row's own 1-DIP divider is inset by that same `padX` (`:3594-3595`).
```

---

## 3. Tokens

| Element | Size | Padding / gap | Radius | Type style | Colour / brush | Material / elevation | Source |
|---|---|---|---|---|---|---|---|
| Command-bar surface | h 44 | pad (6,5,6,5) | — | — | none (chromeless) | — | DetailTracks.cs:2051-2054, DetailVerticalLayout.cs:232-237 |
| Toolbar command (labeled) | h 32 | pad (9,0,10,0), gap 6 | 4 (`Radii.Control`) | 12/600 | ink `Tok.TextSecondary`; active `Tok.AccentTextPrimary` + fill accent@0.11 | hover accent@0.17 / `FillSubtleSecondary` | :4092-4124 |
| Toolbar icon button | 32×32 | — | 4 | icon 14 | same ramp | press accent@0.08 / `FillSubtleTertiary` | :4069-4088 |
| Toolbar separator | 1×20 | margin 4 l/r | — | — | `Tok.StrokeDividerDefault` | — | :4127-4132 |
| Command gap / group separator budget | — | gap 2; separator budget 17; search gap 8 | — | — | — | — | DetailTrackCommandBarLayout.cs:48-50 |
| Fit budget | measured pane − 12 | — | — | — | — | conservative first-frame labeled widths `[120, 92, 96, 156, 144, 82]` refined by `MeasureToolbarCommand` | :1949, :214, :2093-2098 |
| Search host (rest) | 66×32 | gap 2 | 4 | — | fill transparent, border transparent | — | :2332-2355, CommandBarLayout.cs:44 |
| Search host (expanded) | 160…240…280 × 32 | — | 4 | — | `FillControlDefault` → `FillControlInputActive` on focus; border `StrokeControlDefault` | `BrushTransitionMs = 260` | :2345-2352 |
| Search focus underline | h 2 | — | — | — | `Tok.AccentDefault` | — | :2320-2328 |
| Search field affixes | left 28×32, clear 26×26 | right pad 3 | 4 | icon 13 / 12 | `TextTertiary` / `TextSecondary` | `Interaction.Subtle` | :4184-4215 |
| Filter funnel (rest) | 32×32 | glyph offset (0, +1) — optical, the funnel carries more ink above its midpoint | 4 | icon 14 | `TextSecondary` | hover `FillSubtleSecondary` / press `FillSubtleTertiary` | :4301-4332 |
| Filter funnel (active) | 32×32 | glyph offset (−4, +4) | 4 | icon 14 `TextPrimary` | fill accent@0.16, hover 0.24, press 0.12 | `InfoBadge.Count` top-right, 1.5-DIP `FillSolidBase` ring | :4301-4331 |
| Content-filter chip | h 32 | pad 12 l/r, gap 8 | 999 | 13/400 (600 selected) | selected `AccentDefault` + `TextOnAccentPrimary`; else `FillControlDefault` + `TextPrimary`; unavailable `TextDisabled` | border `StrokeControlDefault` (none when selected); hover: unselected → border `AccentDefault` + fill `FillControlSecondary`, selected → fill `AccentSecondary`; an UNAVAILABLE chip is not focusable, keeps its rest fill/border and takes no scale | ContentFilterChips.cs:79-106 |
| Chip rail | h 40 (+8 margin) | gap 8 | — | — | — | `AutoEdgeFade`, no scrollbar | ContentFilterChips.cs:36-76 |
| Lens pill | h 28 | pad (12,0,3,0), gap 2 | 999 | Caption 12 | `Tok.AccentSubtle` fill, `AccentSecondary` border | close 22×22 r11 | LikedFactsPanel.cs:1467-1473 |
| Column header row | h 36 (Classic 32) | colGap 12/8, padX 16/12/8 | — | 12/600 (Classic 11/600 +30 tracking, uppercase) | active `TextSecondary`, else `TextTertiary` | — | DetailTrackTableRules.cs:49, :2465-2486 |
| Header divider | h 1 | margin 0 | — | — | `Tok.StrokeDividerDefault` | — | :2444-2450 |
| Header sort cell | fills its track | gap 4 | 4 | — | `HoverFill = FillSubtleSecondary` | — | :2531-2540 |
| Sort caret | glyph 9 | — | — | `Icons.CaretSolidUp` | `TextSecondary` | — | :2457, :3684-3688 |
| `#` caret slots | 9 each side of a 28 lane | — | — | — | — | — | TrackLane.Num/NumCaretSlot |
| Row height | 40 / 48 / 56 / 64 (Classic 36/40/44/48) | — | — | — | — | — | DetailTrackTableRules.cs:45-47 |
| Row art | 32 / 32 / 40 / 48 (Classic 32, 40 at Comfortable) | — | 4 | — | — | decode `art × 2` px | :54-56, TrackRow.cs:246 |
| Row skin (Modern) | min-h = rowH | margin 8 l/r | 6 (0 while its drawer is open on the bottom) | — | odd: `RowZebra`; hover `RowHover`/`RowHoverZebra`; press `RowPressed`/`RowPressedZebra` | border 1 `StrokeCardDefault` on odd rows; hover on all | :3488-3533 |
| Row selection pill | 3×16 | margin-left 2 | 1.5 | — | `_rowAccent` (cover accent) | opacity 0↔1, press scale 10/16 | :3582-3589 |
| Row divider (Classic) | h 1 | margin padX l/r | — | — | `Tok.StrokeDividerDefault` | — | :3590-3598 |
| Check lane | 28 (4+20+4) | pad (4,0,4,0) | — | — | engine CheckBox | slide-in Dx −28 | SelectorVisualsBound.cs:79-93 |
| Lane widths | # 28 · ♥ 28 · art 32 · Added-by 132 · Date 88 · Plays 52 · BPM·Key 80 · Duration 52 · Video 28 · Actions 40 · Expand 26 | colGap 12 (tier ≤4) / 8 | — | — | — | — | DetailTrackTableRules.cs:227-263 |
| Video ⟷ Actions | **mutually exclusive** — a film lane (28) REPLACES the "…" lane (40); when it is up, More rides inside it (rest = film glyph, hover = bare "…") | — | — | — | — | — | DetailTrackTableRules.cs:68-70, TrackRow.cs:317-323 |
| BPM·Key gate | present only while `Config.ShowTempo && setting && tier ≤ 3` | — | — | — | — | — | TrackRow.ShowTempo, TrackRow.cs:577 |
| Star weights / floors | Title 1 / 120 · Artist 0.75 / 90 · Album 0.75 / 90 | — | — | — | — | — | DetailTrackTableRules.cs:269-278 |
| Track title | — | — | — | 14/20/600 | `TextPrimary`; now-playing `AccentTextPrimary` | — | :2701-2706 |
| Every factual cell | — | — | — | Caption 12/16 (Classic 14/20) | `TextSecondary`; Plays/BPM `TextTertiary` | — | TrackRow.cs:723-731 |
| Explicit badge | min 14×14 | pad 2 l/r | 2 | 10/12/600 | `TextTertiary` @0.6, 1-DIP border | — | TrackRow.cs:745-751 |
| Heart | 28×28 | — | circle | icon 14 | saved `AccentTextPrimary` (Classic `TextPrimary`), else `TextTertiary` (Classic `TextSecondary`) | `Interaction.Subtle`, `BlocksDragArm` | TrackRow.cs:935-957 |
| "…" button | 28×28 | — | circle (Classic square) | icon 16 | `TextSecondary` | rest opacity 0.45 → 1 on row hover; **Classic rest 0** (hover-only) and no hover/press scale; a shimmer/overscan row keeps the lane at opacity 0 | TrackRow.cs:966-997 |
| `#` transport | box 24×24 | — | — | glyph 12 (▶/⏸) | now-playing `AccentTextPrimary`, else `TextPrimary` | `PressScale` = `ScaleEmphatic.Press` | TrackRow.cs:1086-1092 |
| Top-track ★ / chart glyph | — | — | — | ★ icon 11 / ▲▼NEW 8/12/700 | accent / system success·critical | rest state only — never beside the equalizer, star or spinner | TrackRow.cs:1075-1078, :1113-1119 |
| Expand chevron | 24×24 (`Spacing.XXL`) | — | 4 | icon 12 | open `AccentTextPrimary`, else `TextSecondary` | `HoverFill = FillControlSecondary` | TrackRow.cs:625-641 |
| Drawer | — | pad (indent, 0, 16, 8) | (0,0,6,6) | — | continues the row's zebra parity | clip, size-reflow | :3315-3333 |
| Drawer hero figure | — | gap 0, row gap 20 + 20 margin | — | `WaveeType.StatHero` + Caption 600 tertiary | — | pending stat at opacity 0.6 | TrackFactsStrip.cs:132-158 |
| Drawer connector | rail 1 DIP at x 7, stub 9, gutter 20 | — | — | — | `Tok.StrokeDividerDefault` | — | TrackVersionsPanel.cs:49-59 |
| Empty / no-match | — | pad (16,24,16,24) | — | 14 | `Tok.TextTertiary` | — | :409-414 |
| Selection bar count | — | min-w 66 at the narrowest tier | — | 12/650 | `TextPrimary` | — | SelectionCommandBar.cs:139-155 |
| Selection thumbs | 28×28 | overlap −11 | 5 | — | 2-DIP `FillCardSecondary` ring | — | SelectionCommandBar.cs:308-322 |
| Insertion preview card | h = rowH | margin 8, pad 8 | 4 | 14/600 + 12 | `FillSolidSecondary`, border `AccentDefault` | `Elevation.Card` | PlaylistInsertionPreview.cs:67-78 |
| Filter card | 368 × ≤620 (scroll ≤500) | header (14,12,14,10) | popup | 15/650 + 12 | `PopupChrome.Popup` | flyout | TrackFilterFlyout.cs:87-89, :385-470 |

---

## 4. Colour & material

| Input | Function (file:line) | Applied to | Transition |
|---|---|---|---|
| Page cover URL → `Surfaces.SchemeFor` → `WaveePalette.ChromeAccent` | DetailShell.cs:277-310 | `DetailHandlers.Accent` → `_rowAccent` memo → the row selection pill's `Fill`; also every accent-inked toolbar control via `Tok.AccentTextPrimary` | The handler record is re-minted only when the accent moves (`accentKey`, DetailShell.cs:423-448); the pill's fill is a BOUND prop, so a late grading repaints without a list re-render |
| Theme (`Tok.Theme`) | WaveeTokens.cs:230-242 | `RowZebra` = light `ShellPalette.RowZebra` (black α 0.031) / dark `Tok.FillSubtleTertiary`; `RowHoverZebra` / `RowPressedZebra` are `ColorContrast.Over(hover, zebra)` collapsed into ONE fill | `RethemeAll` re-fires the bound `Fill`/`BorderColor` closures; no remount |
| Row parity (`DisplayIndex() % 2 != 0`) | DetailTracks.cs:3512-3532 | zebra fill + `StrokeCardDefault` border on odd rows | Bound to the slot index signal → correct after a recycle |
| Camelot slot colour (wire ARGB) | `WaveePalette.DataDotInk(argb, Tok.Theme)` (TrackRow.cs:584-600) | the 6×6 r1.5 swatch in the BPM·Key cell, opacity 0.85 | Passthrough in dark, hue-dependent darkening in light |
| Chart status | TrackRow.cs:1113-1119 | ▲ `Tok.SystemFillSuccess` / ▼ `Tok.SystemFillCritical` / "NEW" success, 8/12/700, beside the plain number only | none |
| Page tone plane (chapter 03) | CoverPaletteLeaves.PageTonePlane (DetailShell.cs:575) | the ground the table floats on — **the table paints no background of its own** | — |
| Sticky band | DetailTracks.cs:1909-1919 | the chrome's stuck stratum paints **nothing**; rows are CLIPPED at its lower edge (`ItemClipTopInset`), so the band shows the page's own tone | 24-DIP alpha feather (`StickyFadeBand`) |
| Scroll edge | :1021-1027, :1519-1527 | flat/trailing lists use `AutoEdgeFade` (alpha mask on the rows themselves); the vertical arm sets `EdgeCues = None` — the stock surface-colour cue would paint an opaque ancestor-resolved slab over the unpainted band | per-frame, offset-driven |
| On-media ink | TrackRow.cs:477 | the buffering scrim over a rec card's art is `WaveeOnMedia.CoverScrim` | — |
| Light/dark | throughout | every colour here is a `Tok.*` token or a `WaveeColors.*` derivation — there is **no literal colour in this surface** except the Camelot swatch's wire ARGB and the chart glyph's system fills | live theme flip repaints via bound props |

---

## 5. Motion

| Trigger | Target | Property | From → to | Duration | Easing | Delay / stagger | Reduced motion | Source |
|---|---|---|---|---|---|---|---|---|
| Cold model Pending→Ready | list region | opacity | 0 → 1 | engine `Skel` default | `SkelReveal.FadeOnly` (no per-row stagger) | — | engine cross-fade kept | :1065-1069 |
| Reveal ramp tick | `_reveal` | count | +12 per frame until `min(visible,60)` | 1 frame/chunk | — | — | unaffected (not an animation) | DetailRevealRamp.cs:17-22 |
| A row crossing shimmer→real | that row | opacity | 0 → 1 | 280 ms | `FluentDecelerate` | none | engine `ReducedSnap` still cross-fades opacity | :2810-2813 |
| Slot MOUNT after a curated re-cut / cold nav | row skin | opacity | 0 → 1 | 280 ms | `FluentDecelerate` | none | same | :3503-3505 |
| Live add | added row | `TranslateY` then opacity | −6 → 0, 0 → 1 | engine displacement seed | — | `min(ord,8) × 20 ms` | `Motion.ReducedMotion` skips `ReDeal`; `Choreograph` still runs (a membership change is information) | :1590-1595 |
| Live add/remove | every surviving row | `TranslateY` | `(old−new+shift) × rowH` → 0 | engine FLIP | — | none | as above | :1583-1588 |
| Live add/remove above the viewport | scroll offset | offset | `shift × rowH` | instant (`PreserveAnchor`) | — | — | — | :1560-1575 |
| Curated re-cut (`MembershipDiff.IsReset`) | whole list | remount | — | mount entrance | `FluentDecelerate` 280 | — | — | :1551-1557 |
| Breakpoint cross (tier change) | realized rows | `TranslateY` + opacity | 6 → 0, 0 → 1 | engine seed | — | narrowing `min(ord,6) × 24 ms`, widening `min(ord,4) × 16 ms`, 24 rows max | **skipped entirely** | :1609-1641 |
| Breakpoint cross within 200 ms of the last one | — | — | — | — | — | — | skipped (toggle storm) | :1614-1623 |
| The FIRST tier a list ever measures | — | — | — | — | — | — | never narrated (`prev < 0`); the seeds are cleared so an unrelated `_dispVer` bump cannot replay them | :1611-1623 |
| Live membership: the FIRST landing of a context | — | — | — | — | — | — | never narrated (`_membershipSettled`) — a cold/stale-baseline correction is a load, not an edit | :862-873 |
| Density / filter / skin change | list | remount, no entrance | — | — | — | — | — | :1089-1090, :3501 |
| Multi-select on/off | check lane | Dx + opacity | −28 → 0, 0 → 1 | 333 ms | `FluentDecelerate` | — | engine policy | SelectorVisualsBound.cs:86-90 |
| Multi-select on/off | header grid + row content lane | position | +28 | 333 ms | `FluentDecelerate` | — | engine policy | :2439-2440, :3574-3575 |
| Selection count change | the count text | text swap | — | `MotionRecipes.TextSwap` | — | — | — | SelectionCommandBar.cs:139-142 |
| Toolbar mode swap (browse ↔ selection) | the keyed mode box | position + opacity | Dx 10 → 0 in, 0 → −8 out | 210 in / 150 out | `SmoothOut` / `FluentAccelerate` | — | engine policy | :262-267 |
| A command promoted / evicted | that command box | position + opacity | Dx 8, 0 | 220 in / 150 out | `SmoothOut` / `FluentAccelerate` | — | engine policy | :230-235 |
| Search open / close | the search host | **width (Reflow)** | 66 ↔ 160…280 | 260 ms **both ways** (`SearchDisclosureMotion` declares no `ExitDynamics` — only the swap/underline layers get the 180 exit) | `SmoothOut` | — | engine policy | :239-242 |
| Search open / close | icon ↔ field | opacity | cross-fade both layers | 260 / 180 ms | `SmoothOut` / `FluentAccelerate` | — | engine policy | :246-251 |
| Search open / close | host fill + border colour | brush | transparent ↔ control fill | 260 ms | engine brush fade | — | kept | :2345-2351 |
| Search focus / blur | 2-DIP underline | opacity | 0 ↔ 1 | 260 / 180 ms | `SmoothOut` / `FluentAccelerate` | — | kept | :255-260 |
| Sort column becomes active | caret | opacity + scale | 0→1, 0.3→1 | 250 ms (`Expressive.Fast`) | `EaseInOut` / `Overshoot` | — | engine policy | :3680-3682 |
| Sort direction flip | caret | rotation | 0° ↔ 180° | spring `FromResponse(0.30, 0.7)` | — | — | spring survives reduced motion | :3683 |
| Title↔Artist header word swap | `SortLabel` | opacity + `TranslateY` | 0→1, 4→0 | 250 ms | `SmoothOut` | keyed on the text | engine policy | :3711-3712 |
| Row hover enter/leave | row fill + border | brush | rest ↔ hover | 83 ms | `FluentPopOpen` (engine default) | — | kept | Columns.cs:556-563 |
| Row hover enter/leave | `#` cell number ↔ transport; "…" 0.45↔1 | opacity | — | 83 ms | `FluentPopOpen` | — | kept | TrackRow.cs:1092-1105, :986-992 |
| Row press | fill only — **no `PressScale`** on a full-width row | brush | hover → pressed | 83 ms | `FluentPopOpen` | — | kept | :3518-3524 |
| Like edge (same uri, unsaved→saved) | heart glyph | scale + opacity + blur | 0.25→1, 0→1, blur 2→0 | spring `(0.30, 0.55)` | — | — | spring kept | TrackRow.cs:913-916 |
| Now-playing | 3 equalizer bars | `ScaleY` | pattern loop | 850 ms loop @ 30 Hz | linear sample | per-bar phase from 3 patterns | still, non-uniform shape; never ticks | Equalizer.cs:62-110 |
| Buffering / play command in flight | `#` cell | `ProgressRing.Indeterminate(16)` | — | engine | — | — | exempt | TrackRow.cs:1122 |
| Drawer open | outer clip box | **height (Reflow, anchor Leading)** | 0 → measured | 250 in / 150 out | `FluentStandard` | descendant transitions suppressed | engine policy | :3245-3253 |
| Drawer open | inner presence box | opacity + Dy | 0→1, −8 → 0 (exit −4) | 250 / 150 | `FluentStandard` | — | engine policy | :3255-3260 |
| Drawer facts landing late (kind 222/185) | hero stats | FLIP + fade | — | `DetailRail.Shove` / `FadeUp` | — | `MastheadStaggerMs` 45 ms left-to-right | stagger reads 0 | TrackFactsStrip.cs:91-99 |
| Filter section expand | sibling collapse then scroll-into-view | offset | — | 170 ms delay then animated | engine | 170 ms | engine | TrackFilterFlyout.cs:92, :134-142 |
| Chip hover / press | chip | scale | 1.02 / 0.98 | `WaveeMotion.Fast` 167 | `FluentDecelerate` | — | tiers return 1 under reduced motion | ContentFilterChips.cs:94-96 |
| Scroll | rows | alpha edge fade at the overflowing edge | — | per frame | linear (offset-driven) | — | — | :1021-1027 |

**Frame-time rule.** Every motion above is engine-driven (the anim slab samples `FrameClock`). The two
`Environment.TickCount64` reads in this surface are **not** motion sampling and must be ported as-is:
`ReDeal`'s rapid-reversal gate (`:1614`, a gesture-rate debounce) and the equalizer's loop phase
(`Equalizer.cs:128`, chapter 01's known exception, noted in `01-track-row.md`).

---

## 6. Interaction

### Pointer

| Gesture | Target | Result |
|---|---|---|
| Hover anywhere in a row | row | fill/border step; `#` number → ▶/⏸; "…" 0.45 → 1; the EQ stops ticking while hidden (`rowHovered` signal, `:3560-3565`) |
| Single click | row body | SELECT (Extended semantics). While the check lane is visible, a plain tap synthesizes Ctrl → toggles into the selection (`SelectorVisualsBound.MultiSelectMods`, `:3543`) |
| Single click | the `#` cell's transport | play this track, or pause/resume when it is already now-playing (`TrackRow.Invoke` → `PlayRow`, `:3091-3105`) |
| Double click | row | invoke = the same play path (`OnInvokedTyped`, `:1015`) |
| Single click | an artist or album span in the title/subline/Album lane | navigate; the press lands on the text leaf, so the row is neither played nor selected (`TrackRow.cs:400-402`) |
| Single click | ♥ | `LibraryBridge.ToggleSaved(uri, title)`, optimistic (`:2785-2789`) |
| Single click | "…" or the film lane | `ClickRequestsContext` → opens the row's own context menu anchored at the button (`TrackRow.cs:966-997`, `:1020-1047`) |
| Single click | the expand chevron | toggle this row's drawer, closing any other (`ToggleExpanded`, `:3172-3173`) |
| Right click / Menu key / long press | row | `TrackContextMenu.Build(acts, _selection, DisplayTrack, index, HostInfo, showGoToAlbum: cfg.ShowAlbumColumn, singleTrackExtras)` (`:3630-3633`) |
| Click | a column header | `NextSort` cycle (below) |
| Click | a chip | set/clear `Filters.Tag` (re-tapping the active chip clears it) |
| Drag a row | row | `WaveeResourceDrag` payload of the selection (or just this row); the row dims to 0.4 and stays put; the chip follows the pointer |
| Drag a file over a row | row | while a file drag is over it the row paints `RowHover` through its own fill closure (`DropCue`, `:3460`, `:3514`) — one lit row at a time, no overlay node. The target exists only when `ActionServices.VideoOverrides` does (`:3461`) |
| Drop a `.mp4` on a row | row | attach/replace the local video override for that track (`VideoActions.Apply`, `:3461-3481`); any other file drop is handed on to `LocalFileActions.PlayDropped` (`:3476`) rather than swallowed — the row target sits between the pointer and the shell's play-this-file target |
| Touch swipe right / left on a row | row | ToggleLike / AddToQueue, `SwipeMode.Execute`, single-open per list — armed only after a real touch contact this session (`RowSwipe`, `TouchInput.SwipeArmed`) |

### Header sort cycle (`DetailTrackTableRules.NextSort`, :92-108)

* `#` → Index ascending is the default; clicking it while already on Index flips Descending; clicking it from any
  other column resets to the default. The caret is drawn only when Index is descending.
* Title **without** a dedicated Artist lane (Modern, and Classic below tier 4): Title↑ → Title↓ → Artist↑ → Artist↓ →
  default. The Title header reads "Artist" while the artist sort is active (`SortLabel`, `:3707`) and stays lit
  (`HeaderActive`).
* Title **with** a dedicated Artist lane (Classic, tier < 4): each header runs its own asc → desc → default cycle.
* Album / Date added / Plays / Duration: asc → desc → default.
* BPM·Key and Added by are **not** sortable (tempo lands asynchronously; a sort would reorder under the cursor).

### Keyboard

| Key | Result | Owner |
|---|---|---|
| ↑ ↓ | roving focus between rows | engine `ItemsView` (`ItemsView.cs:1334-1342`) |
| Home / End | first / last focusable row, corner-aligned | `:1322-1333` |
| PageUp / PageDown | one viewport, railed | `:1343-1362` |
| Enter | invoke = play (`OnInvoked`) | `:3547` |
| Space | toggle into the selection (synthesized Ctrl while the check lane is visible) | `:3548` |
| Ctrl+A | select all; a second Ctrl+A with everything selected clears it | `ItemsView.cs:1304-1313` |
| Escape | clear the selection | `ItemsView.cs:1314-1321` |
| Type-ahead | jumps to a row by title — **only in the vertical/hero arm** (`ItemText` is set there and nowhere else, `:1508`) | `ItemsView.cs:1391+` |
| Alt+↑ / Alt+↓ | move the selected contiguous block one row, through the same mutation seam and index convention the drag uses; refused (spoken + toasted) when the rows are not keyed | `TryBlockMove`, `:1332-1386` |
| Esc in the search field | collapse the field and restore focus to the button that opened it | `DetailTrackSearchField.OnCancel`, `:4216` |
| Clearing the query (✕ or backspacing to empty) | the field COLLAPSES itself on the text edge and hands focus back | `:4176-4182` |
| Blurring an EMPTY field | collapses without restoring focus | `:4217-4222` |
| Ctrl+F | focuses the **omnibar**, not this field — 0.2.9 has no shell-reachable ticket into `_searchExpanded` | WaveeShell.cs:2074-2079 |

### Tooltips (loc keys)

`detail.filter.searchThisList` ("Search this list") on the search icon and as the field placeholder ·
`detail.filter.title` ("Filter tracks") on the funnel · `common.more` ("More") on the "…" ·
`detail.select` on the Select toggle when icon-only · `detail.clearSelection` on the selection bar's ✕ ·
`detail.tuning.tooltip` on the Tune button · every selection-bar glyph button carries its action's own label.

### Inline edit

The table itself has no inline edit. Playlist title/description editing lives in the hero (chapter 06,
`PlaylistInlineEdit`); the table only consumes `PlaylistInlineEdit.Editable(model)` as the write gate for drops,
block moves, the Remove row and the recommendations section.

### Focus visuals

Rows are `Focusable = false` — the `ItemsView` roving effect owns the single tab stop and sets focus imperatively;
`FocusVisualMargin = 1` on the skin (`:3534-3535`). Inside a row the heart, the "…", the chevron and the lens-pill
close buttons are individually focusable; every one of them sets `BlocksDragArm = true` so a press on the affordance
never arms a row drag.

### Accessibility names

Row skin `Role = AutomationRole.Button` (`:3536`). Every toolbar control is `AutomationRole.Button`. The keyboard
block-move announces its refusal assertively through `Announcer.Say(Strings.Drag.StillSyncing, assertive: true)`
(`:1351`). `ItemText` (the type-ahead/name source) is supplied only by the vertical arm — see the drift note in §9.

---

## 7. Data & readiness in 0.3 terms

| Visual element | 0.2.9 source | 0.3 read | Readiness predicate (skeleton until true) |
|---|---|---|---|
| Row count / order | `DetailModel.Tracks` (a denormalised copy) | `Edges.AlbumTracks / PlaylistTracks / Liked .Targets(parent)` + `.Version[parent]` | `Edges.X.State[parent] == 2 (complete)`; `1 (partial)` renders the resident rows and shimmer for the rest — never "Nothing here yet" |
| "Nothing here yet" vs shimmer | `DetailModel.MembershipLoaded` | `Edges.X.State[parent] != 0` | `PlaylistListState.For(state != 0, total, visible)` — port verbatim |
| `#` number | display index | display index | always |
| Title | `Track.Title` | `Track.TitleId` (`TrackFields.Title`) | `Knows(Title)` |
| Artist subline (per-artist click spans) | `Track.Artists[]` | `Edges.TrackArtists.Targets(slot)` + `Artist.Name` | `Knows(Artists)` **and** every target `Knows(Title)` — a half-named credit line is a regression |
| Album lane / subline album | `Track.Album` (`AlbumRef`) | `Track.Album` slot → `Album.Title` | `Knows(Album)`; a known slot with an empty title renders `—`, never blank |
| Art thumb | `Track.Image` | `Track.ImageId` | `Knows(Image)`; placeholder cover until then |
| Duration | `Track.DurationMs` | `Track.DurationMs` | `Knows(Duration)`; `0` → `—` |
| Explicit badge | `Track.IsExplicit` | flag `Explicit` | `Knows(Explicit)` |
| Plays | `Track.PlayCount` | `Track.PlayCount` (`TrackFields.PlayCount`, kind 185) | `> 0`; else `—` when the surface asked, nothing when it did not |
| Top-track ★ | `TopTrack(model.Tracks)` — an O(n) scan per snapshot | **derived on the model**: max-playcount slot cached on the parent at commit | album profiles only (`Config.ShowPlays`) |
| BPM · Key + swatch | `TempoBpm` / `MusicalKey` / `CamelotCode` / `CamelotColor` | `Tempo` (ushort ×10), `Key`, `Camelot`, `CamelotColor` (`TrackFields.Audio`, kind 222) | `Knows(Audio)`; the cell renders EMPTY (not a dash) until it lands |
| Date added | `Track.AddedAt` | `PlaylistTrackEdge.AddedAt` / `LibraryEdge.AddedAt` | edge payload present; `0` = no stamp → the cell is empty and the Date filters never match it |
| Added by (avatar + name) | `Track.AddedBy` + `DetailModel.UserProfilesById` | `PlaylistTrackEdge.AddedBy` → `User` slot → `User.Name`/`Avatar` | `Knows(User.Identity)`; falls back to the raw id |
| "Has a video" lane | `VideoPresence.HasVideo(uri)` (kind 99 + the user's local overrides) | flag `HasVideo` + `VideoCounterpart` **plus a new local-override column** (see gaps) | `Knows(Video)` for the lane's PRESENCE (`model.HasVideo`), per row for the glyph |
| Chart ▲▼NEW | `Track.Chart` | `PlaylistTrackEdge.ChartStatus/Pos/Prev` | edge payload present |
| Descriptor chips / Tag filter | `Track.Tags` | `Edges.TrackTags.Payload(slot)` (kind 6) | `Knows(Tags)`; a chip with no evidence renders DISABLED, never hidden |
| Saved ♥ state | `LibraryBridge.IsSaved(uri)` | `User.Me.Likes(track)` (CSR reverse index) | `Edges.Liked.State[me] != 0` |
| Now-playing / playing / buffering | `PlaybackBridge.Identity/IsPlaying/IsBuffering` | `Playback.Current` / `PhaseSignal` / `Pending.Load` | always |
| Not-yet-out dimming | `Track.IsNotYetOut()` | flag `Unavailable` + `AvailableAt` + `Knows(Availability)` | a row with NO verdict is never dimmed |
| Column PRESENCE (`HasDateAdded`, `HasAddedBy`, `HasVideo`) | O(n) scans on the model | **derived facts on the parent**, computed at commit | the lane appears when the fact is true, not per render |
| Filter capabilities (local/streamed/unavailable/tempo) | `FilterCapabilities` — an O(n) scan per hero render | a per-parent capability bitmask maintained at commit | facets appear as enrichment lands (props channel, never a `Key`) |
| Total time / "N songs" in the lens header | `View().Length` | the same, from the view map | always |

**Demand.** A page demands its WHOLE model on mount — there is no visible-window fetching here. For this surface
that is: `Entities.EnsureEdges(parent, EdgeKind.X)` then
`Entities.EnsureRows(slots, TrackFields.Row | Audio | Tags | Video)` in one batch (the query layer pages it at 300 per
POST). The drawer adds `TrackFields.All` for ONE slot on expand. The Plays opt-in does **not** change the demand —
kind 185 rides every list surface's trait bundle in 0.2.9 (`DetailShell.cs:384-392`) and must keep doing so, or the
toggle shows a column of dashes.

### DATA GAPS

| Element | 0.2.9 source | Plan's model today | Proposal |
|---|---|---|---|
| Per-artist clickable credit spans | `Track.Artists[]` → one `TextSpan` with its own `OnClick` per artist (`TrackRow.cs:780-803`) | §4.12 proposes a single precomputed `ArtistLineId` StringId | Keep `ArtistLineId` for MEASURE/paint, but the row must build spans from `Edges.TrackArtists`; a single string cannot carry N click targets. **§4.12 is wrong here.** |
| Camelot code + key NAME | `CamelotCode` ("8B") and `MusicalKey` ("F#") are strings | `Key` (byte), `Camelot` (byte) | Add a static table in `Track.cs` CORE: `CamelotLabel(byte)` → "8B" and `KeyLabel(byte)` → "F#"; the wire's 12-slot ring is closed, so a table is exact. Pin with `TrackExpandedFactsTests` |
| "Nobody ruled on this track" | `Availability?` is **nullable** — null ≠ Unavailable | one `Unavailable` flag | Use `Knows(TrackFields.Availability)` as the verdict-known bit; `PlayableOnly` must hide only a CONFIRMED unavailable, and the facts strip must flag only a ruled row |
| User-attached local video override | `ActionServices.VideoOverrides` (a local curation store keyed by uri) | absent | A `LocalVideo` column on the track table (StringId path + flag) written by the curation store on commit; the film lane and the drawer read the OR of wire + override |
| Episodes inside a playlist | a playlist row can be an EPISODE (`EpisodeAsTrack`) — no artists, the SHOW in the album slot, no drawer chevron | `PlaylistTracks` targets Track slots only; Episode is a separate table | Add a `Kind` byte to `PlaylistTrackEdge` (0 track, 1 episode) and let the row resolve the right table; the row cell already branches on `EntityUri.KindOf` |
| Local files as a "playlist" | route `local` → `wavee:local:all`, `TrackOrigin.Local` | provider byte 1 exists | A synthetic parent slot for the local collection, same as Home/Search synthetic subjects |
| Per-row playlist membership id | `Track.ContextUid` — the identity of the drawer state, the reorder wire, and `MembershipDiff` | `PlaylistTrackEdge.ItemId` ✓ | Must be reachable as `(parent, displayIndex) → ItemId` from the row scope, not only from the edge span |
| Chart deltas | `Track.Chart` (status, pos, prev) | `PlaylistTrackEdge.ChartStatus/Pos/Prev` ✓ | none |
| Collaborator profiles | `DetailModel.UserProfilesById` (id → Owner{Name, Avatar}) | `User` table ✓ | needs `User.Avatar` (StringId) + `User.Name` in the identity group |
| Playlist capabilities / notice | `DetailModel.Capabilities`, `Notice` | absent from the plan | chapter 06's gap; this table only consumes `Editable(model)` |
| Curated content-filter chips | `ContentFilters.GetLikedChipsAsync` (spclient, ETag-cached) | absent | A per-user chip list on the `User` row (StringIds) + an ETag column; the derived fallback (`ContentFilterTags.Derive`) already works off `Edges.TrackTags` |
| Playlist tuning | `DetailModel.Tuning` | absent | chapter 06 |
| The cover-derived accent | `DetailHandlers.Accent` from `Surfaces.SchemeFor` | absent from the entity model (a paint-side cache) | keep it as a page-scoped signal; the pill and the toolbar read it, nothing stores it |

---

## 8. Pure rules to port verbatim

| Name | 0.2.9 file | Decides | Tests | 0.3 destination |
|---|---|---|---|---|
| `DetailTrackTableRules` (+ `TrackIdentityColumns`, `TrackTrailingColumns`, `TrackTableLanes`) | `Features/Detail/DetailTrackTableRules.cs:22-217` | identity/trailing lane presence per skin+tier; row height + header height + art size per density; classic inline video; the "Track details" menu fallback; header ownership; the sort cycle; the whole relief ladder (`MinWidthFor`/`NominalReliefFor`/`ReliefFor`/`Relieve`, `MaxRelief` 8, hysteresis 24) | `src/apps/Wavee.Tests/TrackRowStyleRulesTests.cs` (374) | CORE section of `Entities/Track.cs` |
| `TrackLane` | same file, `:227-279` | THE lane width table (28/28/32/132/88/52/80/52/28/40/26) + star weights and identity floors | same | CORE section of `Entities/Track.cs` |
| `DetailTrackCommandBarLayout` (+ `DetailTrackInlineCommand`, `DetailTrackCommandWidths`, `DetailTrackCommandBarFit`) | `Features/Detail/DetailTrackCommandBarLayout.cs` | which commands stay inline, the search reservation (66/160/240/280), 16-DIP promotion hysteresis, `Richness` | `Wavee.Tests/DetailTrackCommandBarLayoutTests.cs` (101) | CORE section of `Entities/Track.cs` |
| `TrackFilterModel` + `TrackFilterState` + its 6 enums | `Features/Detail/TrackFilterModel.cs` | the whole filter predicate, `ActiveCount`, the mutually-exclusive `WithAddedRange`/`WithAddedWindow`, `BandOf` | `Wavee.Tests/TrackFilterModelTests.cs` (251), `TempoFilterTests.cs` (70) | CORE section of `Entities/Track.cs` |
| `TrackExpandedFacts` (+ `TrackFact*`, `KeyMode`, the shared formatters `TrackTime`/`DurationCell`/`Bpm`/`KeyLabel`/`PrettyKey`/`KeySplit`/`ExactStamp`/`ExactDate`) | `Features/Detail/TrackExpandedFacts.cs` | which facts an expanded row states, in what order, which four are hero figures, the pending-dash rule, and every number format the row and drawer share | `Wavee.Tests/TrackExpandedFactsTests.cs` (591) | CORE section of `Entities/Track.cs` |
| `DetailRevealRamp` | `Features/Detail/DetailRevealRamp.cs` | chunk 12, cap 60, `Done` sentinel, `Next`, `Revealed` | `Wavee.Tests/DetailRevealRampTests.cs` (54) | CORE section of `Entities/Track.cs` |
| `PlaylistListState` | `Features/Detail/PlaylistListState.cs` | Loading / Empty / NoMatch / Rows | `Wavee.Tests/PlaylistListStateTests.cs` (58) | CORE section of `Entities/Playlist.cs` |
| `PlaylistReorderRules` | `Features/Detail/PlaylistReorderRules.cs` | when a same-list move is legal, the drop verb, the pre-move index convention, the keyed-reorder gate, display↔original mapping | `Wavee.Tests/PlaylistReorderRulesTests.cs` (241) | CORE section of `Entities/Playlist.cs` |
| `PlaylistDropRefusalRules` (+ `PlaylistDropRefusal`) | `Features/DragDrop/WaveeDragRules.cs:110-143` | ONE table behind both `CanAccept` and the refusal caption, in a fixed order | `Wavee.Tests/WaveeDragRulesTests.cs` (286) | CORE section of `Entities/Playlist.cs` |
| `MembershipDiff` (+ `RowChange`, `MembershipDelta`, `RowKey`, `RowKeyMatches`) | `Components/MembershipDiff.cs` | the keyed row diff, the reset thresholds (retained < 0.5 or > 40 structural), and the ROW identity every drawer/expansion keys off | `Wavee.Tests/MembershipDiffTests.cs` (226) | CORE section of `Entities/Track.cs` |
| `DetailLayoutBreakpoints.NominalTierFor` / `TierFor` / `InitialTierForViewport` / `EstimatePageWidthFromViewport` | `Features/Detail/DetailLayoutBreakpoints.cs` | the tier bands + hysteresis (shared with chapter 03's mode ladder) | `Wavee.Tests/DetailLayoutBreakpointTests.cs` (121) | CORE section of `Shell.cs` (chapter 03 owns the file; this surface reads `TierFor`) |
| `ContentFilterTags.Derive` / `OrderByEvidence` | `Wavee.Core/Library/ContentFilterTags.cs` | the derived chip fallback + evidence ordering | `Wavee.Tests/ContentFilterTagsTests.cs` (161) | CORE section of `Entities/User.cs` |
| `DetailFormat.DateAddedLabel` / `TotalTime` / `ShortDate` / `ArtistNames` | `Features/Detail/DetailConfig.cs:234-311` | the Date-added lane's relative→absolute ladder and the meta-line phrasing | (covered indirectly) | CORE section of `Entities/Track.cs` |
| `DetailQueueActions` | `Features/Detail/DetailQueueActions.cs` | the 50-track batch cap, the per-track `set_queue` metadata map, and the video-association stamp (`MediaSwitchLogic.StampVideoAssociation` — never a hardcoded "audio") | — | `Playback.cs` CORE (`Queue` section) |
| `DetailTrackTableRules.PreviewScale` (0.25) | same file, `:31` | the scale the SETTINGS row-density miniature draws the real row geometry at — it lives in the rules file precisely so "the preview mirrors the real row" is a tested number | `TrackRowStyleRulesTests` | CORE section of `Entities/Track.cs` (the settings page reads it) |
| `DetailVerticalLayout.StickyClipInset` / `ChromeExtent` / `ItemCount` / `ItemRole` / `RowFlow` (424, 24-DIP hysteresis) | `Features/Detail/DetailVerticalLayout.cs` | the vertical arm's clip inset (56 + header 36 + hairline 1, plus the chip rail 48 / lens 36), its slot map, and the stacked↔row flip that decides PLAIN rows | `DetailVerticalLayoutTests` | chapter 03's file; this surface READS all five |

Not rules, but ported unchanged: `TrackRow.PadXFor` / `ColGapFor` / `CellKey` / `ShowTempo` / `PlaysLabel`
(`Components/TrackRow.cs:129-157`, chapter 01) — the header depends on every one of them.

---

## 9. Re-author notes

### What must not be simplified

1. **The two ladders and their composition order.** Deleting relief gives back the 65-DIP title the user reported.
   Deleting the tier ladder loses the padding/gap scaling and the Classic artist fold. They are not redundant.
2. **The lane width table.** Every number in `TrackLane` is a measured decision with a comment naming the content it
   holds (Plays 52 because "1.85B" is ~42 at 14 px; Date 88 because the HEADER is wider than the value). Re-deriving
   them by eye reproduces the "huge gap between Date added and Plays".
3. **The keyed cells.** `TrackRow.CellKey` on both the header and the row grids is what lets a MIDDLE column vanish
   without the reconciler patching the wrong content into the wrong track.
4. **The three-component row stack** (`ExpandableRowSlot` → `BoundRowSkin` → `BoundRowContent`). The split is
   load-bearing: the slot owns the drawer and the swipe wrapper, the skin owns the bound zebra/hover/press/pill/
   drag/menu chrome (shape-stable, so selection is compositor-only), the content owns the grid and re-renders on its
   own subscriptions. Flattening them makes every selection change a list re-render.
5. **The reveal ramp AND its blank placeholder.** Grey shimmer bars under a row at opacity 0 read as
   bars → blank → text. The placeholder is a bare `GridEl` of the same tracks and `rowH`, and the crossing is a
   remount caused by the ELEMENT TYPE changing (GridEl → BoxEl). A `Key` change alone will not do it.
6. **`_membershipSettled`.** Without it every cold playlist open animates its own load.
7. **The search fit latch.** Without it the expanding box re-fits mid-flight and visibly jumps.
8. **The insertion contract.** The page declares INTENT (`AcceptKinds`, `CanAccept`, `Range`, `OnDeposit`, `Caption`,
   `RefusalCaption`, `GapPreview`, `SpotlightWhen`, `Transparent`); the engine owns every coordinate. The `Range`
   must exclude the vertical prefix items and the appended recommendation rows.
9. **`Transparent` vs a refusal.** An album table sits the gesture out silently; a read-only PLAYLIST says
   "Can't edit this playlist". The distinction is deliberate.
10. **The `#` lane's symmetric caret slots** (9 + label + 9 inside 28) — the "#" must not move when the sort
    indicator turns on.

### Traps

* **Props freeze at mount.** The column shape was a ctor arg once; that is exactly what forced a breakpoint cross to
  remount the whole viewport (`:2708-2712`). Anything a row can observe must be a signal it reads, or be carried in
  the snapshot record. `TrackRowsSnapshot`'s own contract (`:704-707`): *every appearance setting a row reads must be
  a FIELD of the snapshot* — a flag read outside it recomputes, compares equal, and never reaches the rows.
* **`Key` remounts throw scroll away.** Density/filter/skin/reset only. Adding tier back into the key reintroduces the
  "opening the rail scrolls the list to the top" bug.
* **`ReuseGuard`.** A publish of `_liveHandlers` per render re-renders every realized row; DetailShell solves it with
  a mount-stable trampoline record keyed on the accent (`DetailShell.cs:396-448`). Port that shape, not a fresh
  record per render.
* **Zero-alloc scroll vs per-row richness.** 0.2.9 reconciles them by: plain elements in the cell (no binds except
  the title/fill/opacity closures built once per slot), one memo per row (`presentation`) that gates on VALUE, the
  marquee only on the now-playing row, `Flow.Show` only for genuinely rare branches, and `RowSwipe` skipped entirely
  until a touch contact is observed. Keep all four.
* **Never write a signal from `Render`.** `_resetEpoch` is a plain field bumped during render precisely because the
  remount must land in the SAME pass (`:776`, `:1556`); the count signals are written from a layout effect (`:942`).
* **The header's `padX` and the row's `padX − RowInset`** must stay one arithmetic; the drawer's indent
  (`ArtCentreIndent`) is derived from the SAME lane table.

### Where the plan is wrong or too thin for this surface

* **§2 (the tree) had no file for a shared track table when this chapter was first written.** Album/playlist/liked/local all render ONE table; the tree
  offered `Track.UI.cs` (1,500) plus per-entity `*.Page.cs`. Writing it inside each page would duplicate 3,000 lines four
  times. **Settled: `Entities/Track.Table.cs`**, owner M, **Wave 4.5** (not Wave 5 — it lands in the shared
  detail-frame slot before Wave 5 opens), with O consuming it in Wave 5 through `TableProfile`.
* **§4.12's `Track.Row` sketch is not this row.** Concretely: `Height = 56` is one rung of a four-rung density ladder
  that also moves the art size; `style.ShowNumber` is a bool where the real decision is a 14-lane `ColumnSet` from two
  ladders plus a per-surface config, read live; `OnPointerDown = Play` is wrong (single click SELECTS, double-click
  invokes, the `#`-cell transport plays, and a click on a credit span navigates without doing either); the equalizer
  is not a sibling but the rest-state layer of the `#` cell under a hover cross-fade; and the sketch has no heart,
  date, added-by, plays, tempo, video, chevron, zebra, selection pill, context menu, drag source or shimmer twin.
* **§4.12's `ArtistLineId`** cannot carry per-artist navigation (see §7 gaps).
* **§4.13's album wireframe** shows a bare `# / Title / ▶ / Plays / ⏱` header. The real album page carries the
  command bar (Play next split, Shuffle, Sort, Row size, Select, More, Find+Filter), a sortable header with carets,
  the ♥ lane, the trailing film/"…" lane and the chevron lane. The wireframe understates the surface by roughly
  everything in §2 above.
* **§4.13's `EnsureRows(_a.TrackSlots, TrackFields.Row)`** is not enough: the table also needs `Audio` (BPM·Key),
  `Tags` (chips + the Tag filter) and `Video` (the film lane), and the drawer needs `All` for one row.
* **§5 Wave 5's gate** ("no allocation on a scroll frame, ReuseGuard silent") is the right gate but names no
  *visual* acceptance. Add: the parity checklist in §10 of this chapter.
* **§7's risk table** has no row for "the table's pure rules are re-derived instead of ported". They are 1,100 lines
  of tested decisions; re-deriving them is the single largest fidelity risk on this surface.

### Line budget

**The authority is `01-track-row.md` §9, "The track surface file plan (01 + 04 reconciled — the one authority)".**
This chapter and chapter 01 describe **one** C# surface, and until 2026-09-12 they budgeted it twice, with two
incompatible file splits: this section's earlier answer put the drawer inside `Track.UI.cs` and sized that file at
1,300 for "the row cell + drawer", against 01's `Track.UI.cs` 2,600 (row only) + `Track.Drawer.cs` 700. The
arbitration of 2026-09-12 takes 01's four-file plan whole. The table below is that plan's numbers, reproduced so
this chapter is not read alone — **do not re-derive them here and do not add this chapter's total to 01's.**

| | Lines |
|---|---|
| 0.2.9, this surface's own files | 6,137 (`DetailTracks` 4,525 + rules 279 + command bar 119 + filter flyout 472 + filter model 290 + expanded facts 367 + queue actions 85) |
| 0.2.9, shared code this surface depends on | ~2,100 (`TrackRow` 1,136 · `SelectionCommandBar` 394 · `TrackFactsStrip` 296 · `MembershipDiff` 119 · `ContentFilterChips` 107 · `PlaylistReorderRules` 126) |
| Plan §2 target | `Track.cs` 300 + `Track.UI.cs` 1,500 = **1,800** for the handle, the row AND (implicitly) the table |
| Honest estimate — the four `Track.*` files (`01-track-row.md` §9) | `Track.cs` CORE (the ported rules) **1,200** · `Track.UI.cs` (the ROW and nothing else) **2,600** · `Track.Table.cs` (chrome, command bar, header, tiers, list arms, choreography, drag, selection, recs, filters, **and the drawer's mount/keying/reflow**) **2,600** · `Track.Drawer.cs` (the drawer BODY: facts strip, version rows, gutter rail, waveform, format ladder, the credits modal's cold cache) **700** = **7,100** |
| …plus the Wave-4 primitives the surface cannot exist without | `Platform/Controls.cs` **900** (share) · `Platform/Drag.cs` **700** — owner L, Wave 4, before either owner starts |
| **The whole track surface, de-duplicated** | **8,700**, against the master plan's **1,800** |

This chapter's own share of that is `Track.Table.cs` **2,600**, plus the parts of `Track.cs` §8 lists. The 0.3
number is below 0.2.9 because the vertical/hero duplication (three list arms) collapses once the shared frame
(chapter 03) owns the hero and chrome placement, and because the hydration-era guards (`_lastCtxUri`, `_lastTrackSet`,
`_viewSavedSet`, the in-place-refresh guard) disappear with the store/merge layer. It is **not** 1,800.

### Files/pages missing from the §2 tree

* `Entities/Track.Table.cs` (above).
* `Entities/Track.Drawer.cs` — the expanded-row drawer body, 836 lines today (`TrackVersionsPanel` 411 +
  `TrackFactsStrip` 296 + `FormatSplitButton` 129), plus `Actions/TrackCreditsDialog.cs` (123), which 01's plan
  parks here for its cold cache even though 0.2.9 raises it from the menu action (`TrackActions.cs:167`). The §2 tree has no
  file for it and this chapter previously folded it into `Track.UI.cs`; per the arbitration it is its own named
  partial of `Track`, owner M — see `01-track-row.md` §9.
* No home for `SelectionCommandBar` — it is shared by the table and the artist page's album drawer
  (`ArtistPage.AlbumExpand.cs:158`). Proposal: `Platform/Controls.cs` (Wave 4, owner L).
* No home for the filter flyout / sort menu / density panel. They are table chrome → `Track.Table.cs`.
* No home for the content-filter chip bar (Liked) → `Entities/User.UI.cs` with the rest of Liked's chrome, called by
  the table through a `Func<Element?>` so the table does not depend on `User`.

---

## 10. Parity checklist

Verify each item side by side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe` launched with `--fake`.
Fake routes: `pl:spotify:playlist:pl0` (user playlist, dated), `pl:spotify:playlist:pl1` (collaborative → Added-by),
`album:spotify:album:al2` (a 12-track album), `liked` (161 rows), `local`. Fake tracks carry tempo/Camelot/tags/year,
explicit every 6th, album play counts descending (track 1 is the hit), and **no** video association — the film lane
cannot be checked in `--fake` (verify it live or accept the Actions lane in its place).

1. **Column order.** `pl0`, window 1400 (right pane ≈1060), static capture: `# · ♥ · art · Title · Album ·
   Date added · Time · …`. No lane between Album and Date; no lane after Time except the "…" and the chevron.
2. **Header alignment.** Same capture: the "Title" label's left edge is on the title text's left edge (not the art),
   and every header label starts on its own column's left edge. Overlay the two captures — zero drift.
3. **Star split.** `pl0` @ 1060: Title lane ≈365 DIP, Album ≈273 (ratio 1 : 0.75, pool 638 after # 28 · ♥ 28 ·
   art 32 · Date 88 · Time 52 · "…" 40 · chevron 26, eight 12-DIP gaps and 2×16 padX). Measure in the capture.
4. **Tier 1 at 720–859.** Drag the window so the right pane reads ~780: Added-by disappears on `pl1`, everything else
   stays.
5. **Tier 2 at 560–719.** Pane ~600: the Album lane disappears; the album name reappears in the title subline.
6. **Tier 3 at 440–559.** Pane ~500: Date added and Plays disappear; BPM·Key still present when the column is on.
7. **Tier 4 at 340–439.** Pane ~400: BPM·Key gone; `padX` tightens to 12 (the rows move 4 DIP closer to the edge).
8. **Tier 5 at 300–339.** Pane ~320: ♥ and the art thumb disappear; colGap tightens to 8.
9. **Tier 6 below 300.** Pane ~280: the trailing "…"/chevron lane is gone; `padX` 8; only `# · Title · Time` remain.
10. **Hysteresis.** Slowly drag the window across 860: the tier drops the moment the width falls below, and comes back
    only at 884. No flicker on a slow drag (frame recording).
11. **Relief.** Settings → Appearance → Classic row style, open `liked`, open the right rail so the pane reads ~650:
    the Plays lane is gone and the Title/Artist lanes are readable. Widening to ~680 does NOT bring it back; ~706 does.
12. **Density ladder.** Row size → Compact/Default/Cozy/Comfortable on `pl0`: rows 40/48/56/64 and the art
    32/32/40/48. Measure both in a static capture.
13. **Classic ladder.** Same with Classic: rows 36/40/44/48, header 32, uppercase tracked labels, no art column, a
    1-DIP divider under every row.
14. **Zebra parity.** `pl0`: rows 2, 4, 6 (display index 1, 3, 5) carry the zebra fill and a 1-DIP card border;
    rows 1, 3, 5 do not.
15. **Hover.** Hover capture on row 2: the number becomes a ▶, the "…" goes from quiet to full, the fill steps up,
    and the border appears on an even row.
16. **No press scale.** Frame recording of a press-and-hold on a row: the fill changes; the row does not shrink.
17. **Now-playing.** Play row 3 of `album:spotify:album:al2`: the number becomes a live 3-bar equalizer, the title
    turns accent, and the title marquees if it overflows. Frame recording: 850 ms loop.
18. **Equalizer pause on hover.** Hover the now-playing row: the bars are replaced by ⏸ and stop ticking
    (frame recording — no bar movement behind the fade).
19. **Top-track star.** `album:spotify:album:al2`: track 1's `#` cell shows the ★ at rest (play counts descend).
20. **Unknown dashes.** `pl0` with the Plays column ON (More → Plays column): every Plays cell is `—`, never `0`.
21. **Sort cycle on Title.** Click the Title header four times: Title↑ → Title↓ → the label reads "Artist" with the
    caret up → "Artist" caret down → back to "Title" with no caret and the original order.
22. **Sort caret motion.** Frame recording of two Title clicks: the caret rotates 180° continuously (spring), it does
    not swap glyphs.
23. **`#` does not move.** Static capture before/after clicking `#` twice (Index descending): the "#" label stays on
    the same x; the caret appears in the RIGHT slot only.
24. **Sort persistence.** Sort `pl0` by Duration descending, navigate away and back: the sort survives. Open `pl1`:
    it is unsorted. Open `liked` first time: Date added, descending.
25. **In-list search.** Click 🔍 on `pl0`: the box reflows open over 260 ms, the icon cross-fades into the field, the
    field is focused, and typing filters the rows live. Frame recording: no jump at either end of the tween.
26. **Search collapse on empty.** Clear the query with the ✕: the field collapses and focus returns to the icon.
27. **Command eviction.** Narrow the pane until Density and Select leave the bar: they appear in the "…" flyout as
    "Row size ▸" and a "Select" toggle, in that order, and nothing collapses to an icon-only button.
28. **Promotion hysteresis.** Widen slowly past the point where Select left: it returns only after ~16 DIP more.
29. **Filter funnel badge.** Apply two filters: the funnel gets an accent plate, the glyph shifts (−4, +4) and an
    InfoBadge "2" sits in the top-right corner with a 1.5-DIP ring.
30. **Filter flyout layout.** Open it at 1400: card 368 wide, "Search in" segmented on ONE row, Explicit/Video trait
    rows with their control on the SAME line, the status checkboxes in a 2-column grid, four disclosures, the footer
    with a disabled "Clear all filters" when nothing is set.
31. **One disclosure at a time.** Open "Duration" then "Date added": the first collapses, and the second scrolls under
    the card header ~170 ms later.
32. **Liked chips.** `liked`: a one-line chip rail above the column header with an "All" chip first; the rail scrolls
    horizontally with an edge fade and never wraps to a second line.
33. **Chip exclusivity.** Tap "Indie" then "Pop": only one is selected at a time; tapping the active chip clears it.
34. **Lens header.** `liked` → click a rail fact (chapter 07) → the lens pill row appears between the chips and the
    column header with the visible song count on the right, and the rows below shift down by exactly 36 DIP.
35. **Empty vs no-match.** Type a nonsense query on `pl0`: "No songs match your filter". Compare with an empty
    playlist's "Nothing here yet" — same type, same placement.
36. **Cold reveal.** Navigate Home → `pl0` and record: 12 shimmer rows derived from the real row shape, then the real
    rows appearing top-down in chunks of 12, each fading in over 280 ms. No single frame swaps the whole band.
37. **Warm re-open of an album.** Navigate away and back to `album:spotify:album:al2`: the ramp runs again
    (HasTrailing pages ramp warm opens too), still no one-frame band swap.
38. **Scroll survives the rail.** Scroll `liked` to row ~400, toggle the right rail (Ctrl+Q / the toolbar button):
    the columns change and the scroll position does NOT move.
39. **Breakpoint re-deal.** Same toggle, frame recording: the visible rows rise 6 DIP and fade in top-down with a
    short stagger (≤6 steps), once — not per row for 25 rows.
40. **Rapid toggle.** Toggle the rail four times quickly: no cascade, no stacked delays (the 200 ms reversal gate).
41. **Multi-select.** Ctrl-click three rows on `pl0`: the check lane slides in from the left over 333 ms, the header
    shifts +28 DIP, the selection command bar replaces the browse bar with up to 3 stacked covers and "3 selected".
42. **Selection cue, both halves.** (a) Plain-click ONE row on `pl0` (no Select toggle): the fill does not change and
    the only cue is the 3×16 accent pill at that row's left edge. (b) Ctrl-click a second row: the check lane slides
    in and every pill goes to 0 — the checkbox is now the cue, and no row shows both. In Classic, (a) shows no pill at
    all and the selected row takes the `RowHover` fill instead.
43. **Selection bar fit.** Narrow the window with the selection active: labels disappear at <760 DIP of lane, and
    below 390 only Play + "…" + ✕ remain.
44. **Escape / Ctrl+A.** With rows selected press Escape (selection clears); press Ctrl+A (all selected), then
    Ctrl+A again (cleared).
45. **Alt+Down.** Select two adjacent rows on `pl0` and hold Alt+Down: the block walks down one row per press and the
    selection follows it; on a sorted or filtered list nothing happens.
46. **Drag-reorder visuals.** Drag row 3 of `pl0` onto row 8: the dragged row dims to 0.4 and stays in place, an
    insertion line + gap opens, the gap shows the track's preview card (32 art, title 14/600, artist 12) with an
    accent border, and the chip reads "Move 1 song". No full-window scrim.
47. **Cross-list copy.** Drag a track from `liked` onto a playlist page: the chip reads "Add 1 song" AND the window
    dims (the spotlight scrim).
48. **Refusals.** Sort `pl0` by Title, then drag a row: the chip shows the not-allowed glyph and
    "Clear sorting to reorder". Type a query instead: "Clear filters to reorder". On `album:…`: the drag passes
    through with no cue at all.
49. **Expanded row.** Click the chevron on row 2 of `pl0`: the drawer reflows open over 250 ms with the content
    sliding down 8 DIP inside a stationary clip; the row's bottom corners square off; a vertical rail descends from
    the CENTRE of the row's artwork.
50. **Drawer facts.** In the same drawer: four display figures (Plays · BPM · Key · Duration) over sentence-case
    captions, then a middot-joined prose line, then the descriptor/flag line; a pending fact shows an em dash at
    opacity 0.6, never "0".
51. **One drawer.** Open another row's chevron: the first closes (150 ms) as the second opens.
52. **Drawer under a duplicate.** On a playlist holding the same song twice, expand one: only that row opens.
53. **Row context menu.** Right-click a row: Play · Play next · Add to queue · Save as a command strip, then
    Add to playlist ▸ (+ Move to playlist on an editable playlist) · Go to album (absent on an album page) ·
    Go to artist · Share ▸ · credits/radio/Video ▸, then — on an editable playlist — a separator and
    "Remove from this playlist" last.
54. **Menu on a multi-selection.** Select 3 rows, right-click inside the selection: the menu targets all three (no
    Go-to-album/artist rows). Right-click OUTSIDE it: the selection collapses to the clicked row first.
55. **"Track details" fallback.** Narrow to tier 6 and right-click a row: a "Track details" item appears (the chevron
    lane is gone). Widen back: the item disappears.
56. **Recommended songs.** Open a `--fake` owned playlist and scroll to the bottom: a "Recommended songs" header row
    with a refresh button, then up to 20 art-forward rows with a "+" button, clipped to the row height.
    *(`--fake` has no extender — verify live, or confirm the section is ABSENT under `--fake`, which is the correct
    behaviour since `svc.RealExtender` is null.)*
57. **Vertical/hero arm.** Settings → "Track page layout: Hero" on `pl0` at any width: the hero scrolls away, the
    56-DIP identity row and the column header pin as ONE band with a single hairline, and the rows are CLIPPED at the
    band's lower edge (they do not slide under it), with a 24-DIP feather.
58. **Compact band actions.** In that state: the band's right cluster reads `Find  Filter  Play` as plain words (no
    plates); clicking Find expands the field in place of the identity block.
59. **Stacked flow rows.** Same layout, narrow the window below 424: the rows lose the zebra, the pill and the inset
    (plain full-bleed rows), while the tier system still drops columns.
60. **Theme flip.** With the table on screen, flip the theme: the zebra, hover, border, header ink and the accent pill
    all re-colour in place with no remount (frame recording — no entrance replays).
61. **The chevron lane is real estate.** `pl0` @ 1060, static capture: there IS a fourth trailing lane after "…" —
    a 26-DIP chevron column with no header label. Count nine columns, not eight, and check the Title/Album split
    against item 3's pool. On `album:…` the same lane is present (albums set `ShowVersions` too).
62. **Film replaces "…", never joins it.** On a live (non-`--fake`) list holding a video track: that row's trailing
    lane is a film glyph at rest and a bare "…" on hover, and the table has NO separate 40-DIP "…" column — every row
    is 12 DIP narrower in its trailing cluster than the same list without a video.
63. **Classic `#` is inert.** Classic skin, `pl0`: the `#` header cell is blank — clicking it does nothing and no
    caret ever appears there. The only route back to the original order is Sort → Custom order.
64. **Classic now-playing.** Play a row in Classic: the `#` cell shows a small speaker glyph (not the 3-bar
    equalizer), the whole row's factual ink turns accent, and no ★ / chart glyph is drawn on any row.
65. **Classic "…" is hover-only.** Static capture of an unhovered Classic row: no "…" is painted at all. Hover it: it
    appears at full opacity without a scale bounce.
66. **More-flyout order.** Narrow until Shuffle/Sort/Row size/Select are all evicted, then open "…": the order is
    Shuffle · Sort ▸ · Row size ▸ · BPM·Key column · Plays column · Select · ─── · Copy to playlist.
67. **Filter facets are earned.** Open the funnel on a fresh `album:…` (no library, no dates, no tempo yet): the card
    shows Search in · Explicit · Video tracks · Duration and nothing else. Open it on `liked` after enrichment: the
    Liked-only / Available-only checkboxes, Date added, Tempo and Source appear.
68. **Drawer row height.** Expand a row with versions: each version row is 51 DIP tall (43 art + 4 above + 4 below),
    and the last one's rail stops at its own stub rather than running to the drawer's bottom edge.
69. **Vertical arm filters do not remount.** Hero layout on `pl0`, scroll to row ~30, type a query: the rows filter
    in place and the scroll offset is preserved (the vertical list key carries no `:q…:f…` segment).
70. **`.mp4` hover cue.** Drag a video file over the list: exactly one row lights (`RowHover`) and follows the
    pointer; drop a non-video file instead and it falls through to the shell's "play this file" path rather than
    being swallowed.
71. **Recommendations, all three states.** On an owned playlist: scroll to the bottom cold → the refresh button is a
    spinner; after an empty reply → "No suggestions right now" beside the button; on a non-owned playlist → no header
    row exists at all.
72. **Single has no selection.** Open a 1–2 track single: Ctrl+A, Select and right-click-multi do nothing — no check
    lane, no selection command bar (`ItemsSelectionMode.None`).

---

## 11. Audit log

Second pass against the 0.2.9 sources (every line reference below re-read, not trusted). The chapter was accurate on
the ladders, the motion table, the pure-rules inventory (all twelve test line counts match to the line) and every
token I could find a constant for; what follows is what did not survive the check.

| # | Section | Kind | Correction |
|---|---|---|---|
| 1 | §2 W1 | **wrong** | The playlist table's ninth lane — the 26-DIP expand chevron — was missing from the wireframe and the star maths (`DetailConfig.Playlist` sets `ShowVersions: true`, `DetailConfig.cs:200`; `TracksFor` appends it, `DetailTracks.cs:578`). The gap count was also 8 for what the old frame drew as 8 columns (7 gaps). Corrected pool 638 → Title 364.6 · Album 273.4 (was "650 → 371.4 · 278.6"). |
| 2 | §2 W2 | **wrong** | Same omission on the album frame: pool is 730, not 742 (7 columns / 6 gaps / fixed 226). |
| 3 | §2 W3 | **wrong** | Same again at tier 3: Title star is 190, not 202. Added the `MinWidthFor` arithmetic that proves relief 0. |
| 4 | §2 W12, §0.6, §10.42 | **wrong** | The 3×16 accent pill is NOT drawn while the check lane is visible — its bound opacity is `!classic && isSel() && !checksVisible` (`:3588`). The wireframe drew a pill on a checked row and the parity item asserted it. Rewritten as the two-state rule (pill = highlight, checkbox = multi-select), plus Classic's `RowHover` cue. |
| 5 | §2 W15 | **wrong** | Version rows are 51 DIP (`AudioThumb 43 + 2 × Spacing.XS 4`, `TrackVersionsPanel.cs:59`), not 59 (the chapter read `Spacing.XS` as 8). |
| 6 | §2 W21 | **wrong** | More-flyout order: the BPM·Key and Plays column toggles come BEFORE the Select toggle (`:4374-4397`); the chapter had Select above them. Added the "…"-is-never-accent and frozen-density-label facts. |
| 7 | §5 | **wrong** | The search host's width tween is 260 ms in BOTH directions — `SearchDisclosureMotion` declares no `ExitDynamics` (`:239-242`). Only the icon↔field swap and the underline take the 180 ms exit. |
| 8 | §0.1 | **missing** | The `padX − RowInset` inset is Modern-only: Classic rows and the vertical arm's stacked "plain" rows carry no skin margin and pay the full `padX` (`TrackRow.cs:334-335`, `:3491`), which is also why `ArtCentreIndent` subtracts `RowInset` conditionally (`:644`). |
| 9 | §3 | **missing** | Video (28) and Actions (40) are **mutually exclusive** — a film lane replaces the "…" lane and hosts More inside it (`DetailTrackTableRules.cs:68-70`). The lane table listed both as if they could coexist. |
| 10 | §2 W22 | **missing** | Four Classic-only states: the `#` header cell is EMPTY (no sort, no caret, `:2400`); the now-playing rest state is a speaker glyph, not the equalizer, and there is no ★ / chart glyph (`TrackRow.cs:1071-1078`); the "…" is fully hidden at rest (`:993`); the row grid pays the full `padX` and its divider is inset by it. |
| 11 | §2 W14 | **missing** | The filter card is capability-gated, not a fixed form: Liked-only / Available-only / Date added / Tempo / Source each appear only on their capability or when already set (`TrackFilterFlyout.cs:309-372`), and the status grid drops to one star column with a single checkbox. |
| 12 | §2 W19 | **missing** | The recommendations section's other three states — loading spinner in place of ⟳, the "No suggestions right now" caption, and the not-live case where the header row does not exist at all (`:926-932`, `:2967-2973`). |
| 13 | §1.1 | **missing** | Three arm-level differences: the vertical command bar never carries Play or Shuffle inline (`CommandBarLayout.cs:85`, `:106`); the vertical list `Key` omits the query/filter segment, so filtering there does NOT remount (`:1089`); selection mode is per-config, and a SINGLE is `None` (`DetailConfig.cs:210`). |
| 14 | §6 | **missing** | The `.mp4` drag-over CUE (the hovered row paints `RowHover` through the existing fill closure, `:3460`/`:3514`) and the non-video fall-through to `LocalFileActions.PlayDropped` (`:3476`). |
| 15 | §6 | **missing** | Two self-collapse paths for the search field beyond Esc: clearing the query (`:4176-4182`) and blurring while empty (`:4217-4222`). |
| 16 | §5 | **missing** | Two "deliberately silent" motion rows: the first tier a list measures is never re-dealt (`prev < 0`, `:1618`), and the first membership a context lands is never choreographed (`_membershipSettled`, `:862-873`) — the latter was stated in §0.12 but absent from the motion table. |
| 17 | §3 | **missing** | Fit budget rows: the resolver is handed `available − 12` (`:1949`) and seeds from the conservative labeled widths `[120, 92, 96, 156, 144, 82]` (`:214`) until `MeasureToolbarCommand` refines them; `SearchGap` 8 sits between the command cluster and the search host. |
| 18 | §3 | **missing** | Chip hover states (unselected → accent border, selected → `AccentSecondary` fill, unavailable → inert and unfocusable); the `#` transport box (24×24, glyph 12) and the top-track ★ (11) / chart glyph (8/12/700) rungs; the heart's Classic unsaved ink; the funnel's resting +1 optical offset. |
| 19 | §2 W12 | **missing** | The selection bar's command ORDER and gap 3, its cover de-duplication by image url, and the contents of its own "…" flyout at the essentials tier (`SelectionCommandBar.cs:130-240`). |
| 20 | §8 | **missing** | Three rules the port would have dropped: `DetailTrackTableRules.PreviewScale` (the settings density miniature's scale, `:31`), `DetailQueueActions`' video-association stamp, and the five `DetailVerticalLayout` members this surface reads (clip inset, slot map, the 424-DIP row-flow flip that decides PLAIN rows). |

Checked and found CORRECT (recorded so a third pass need not redo it): every tier band and the 24-DIP hysteresis
(`DetailLayoutBreakpoints.cs:7,11,48-56`); the whole relief ladder, its yield order and `MaxRelief` 8; the lane width
table and star floors; row/header/art density ladders in both skins; `padX` 16/12/8 and `colGap` 12/8; all thirteen
motion durations and easings I could resolve to a token (`Expressive.Fast` 250, `MotionTok.ControlNormal` 250 /
`ControlFast` 150 / `ControlFaster` 83 / `DisclosureExpand` 333, `WaveeMotion.Fast` 167, `MastheadStaggerMs` 45,
`ScaleSubtle` 1.02/0.98, `EyebrowTracking` 30, the 850 ms / 30 Hz equalizer, the 280 ms ramp reveal and mount
entrance, the 20 ms × 8 add stagger, the 24/16 ms × 6/4 re-deal); the filter card's geometry; the command-bar
constants; the drawer's two-spec split and its indent derivation; `ShowVersionsMenuItem`; the sort cycle; every loc
key I spot-checked; and all twelve test file line counts in §8.

**token-reconcile (2026-09-12):** §5's two multi-select rows (`:652, 653`) transcribe the check-lane slide as **333 ms** and **±28**. Both are engine constants with names — `SelectorVisualsBound.MultiSelectAnimMs` / `CheckboxContentOffset` (`FluentGpu.Controls/SelectorVisualsBound.cs:59, 61`) — so the numbers are right but unciteable; now carried as a magic-number row in `00-design-system.md §12.2`. Every other value this chapter names re-verified against `WaveeTokens.cs`, `WaveeMotion.cs`, `Dsl/Spacing.cs`, `Dsl/Radii.cs` and `Animation/MotionTok.cs` and left unchanged — including `ControlFast` 150 vs `WaveeMotion.Fast` 167, which §9 already keeps apart correctly.

**arbitration (2026-09-12):** the track surface's file split is settled against `01-track-row.md`, and this chapter loses. Its §1.2 row put the expanded-row drawer at `Track.Drawer(...)` **inside `Track.UI.cs`**, and its §9 budgeted that file at **1,300** for "the row cell + drawer" — against 01's `Track.UI.cs` 2,600 (the row and nothing else) + a separate `Track.Drawer.cs` 700, i.e. the same component in two files with a 2× size gap. 01's reconciled **four-file plan wins**: `Entities/Track.cs` (CORE) 1,200 · `Track.UI.cs` (the row) 2,600 · `Track.Table.cs` (this chapter's list) 2,600 · `Track.Drawer.cs` (the drawer body) 700 = **7,100**, plus `Platform/Controls.cs` 900 and `Platform/Drag.cs` 700 for the whole surface's **8,700**; all four `Track.*` files are owner **M**, and owner **O** configures the table through `TableProfile` and never edits them. Verified against 0.2.9 before changing: the drawer body is 836 lines (`TrackVersionsPanel` 411 + `TrackFactsStrip` 296 + `FormatSplitButton` 129) plus `Actions/TrackCreditsDialog.cs` (123, raised from `TrackActions.cs:167`, which is why 01 parks only its cold cache in the drawer file), and the drawer's **only** call site in the whole app is this table — `DetailTracks.cs:3293` builds the model, `:3317`/`:3341`/`:3348` key the clip / presence / body boxes, and `:3347` pushes the model through `Ctx.Provide(TrackVersionsPanel.Props, …)`; a `grep` for `TrackVersionsPanel` finds no other caller. That is the structural reason the body leaves the row file while its mount, keying and reflow animation stay in `Track.Table.cs`. Changed: the header's "0.3 target" line, the §1.2 drawer row, the §9 line budget (which now reproduces 01's plan and names it the single authority, with a warning not to add the two chapters' totals), and a new `Entities/Track.Drawer.cs` entry under "Files/pages missing from the §2 tree".

**consistency 2026-09-12:** header and §9's "where the plan is wrong" bullet put the whole track surface in Wave 5. The plan (`01-track-row.md` §9's reconciled file plan, mirrored in `wavee-0.3-implementation.md` §5) puts `Track.cs`, `Track.UI.cs` and `Track.Table.cs` — this chapter's own file — in **Wave 4.5**, with only `Track.Drawer.cs` in Wave 5. Header and the `Entities/Track.Table.cs` bullet corrected; the 8,700-line total and per-file ownership are unchanged.
