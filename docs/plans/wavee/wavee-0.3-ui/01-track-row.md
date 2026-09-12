# Track row and list primitives (row variants, equalizer, save button, selection bar, menus, drag chip) — 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Components/TrackRow.cs` (1136) · `RowSwipe.cs` (147) · `Equalizer.cs` (206) ·
> `EqualizerMotionPolicy.cs` (28) · `NowPlayingMatch.cs` (42) · `TrackFactsStrip.cs` (296) · `TrackVersionsPanel.cs` (411) ·
> `SelectionCommandBar.cs` (394) · `SaveButton.cs` (203) · `FormatSplitButton.cs` (129) · `MembershipDiff.cs` (119) ·
> `Features/DragDrop/{PlaylistInsertionPreview.cs (80), WaveeDragChipModel.cs (52), WaveeDragRules.cs (210), WaveeResourceDrag.cs (596)}` ·
> `Actions/{Menus.cs (1197), ActionIcons.cs (87), TrackContextMenu.cs (37), TrackCreditsDialog.cs (123), TrackActions.cs (255)}` ·
> `Design/SearchHighlight.cs (81)` · `Features/Detail/DetailTrackTableRules.cs (279)` · `Features/Detail/TrackExpandedFacts.cs (367)`
> | 0.3 target: `Entities/Track.UI.cs` (Row, QueueRow, menus), `Entities/Track.Drawer.cs` (facts strip + versions +
> format button), `Platform/Controls.cs` (SaveButton, SelectionCommandBar, Equalizer), `Platform/Drag.cs`
> (payload, chip, rules, insertion preview), CORE rules in `Entities/Track.cs`.
> **The ONE file plan for the whole track surface — this chapter *and* `04-detail-track-table.md` — is §9's
> "The track surface file plan (01 + 04 reconciled)". It is the authority over both chapters' §9 line budgets and
> over `wavee-0.3-implementation.md` §2's `Track.cs Track.UI.cs → 300 + 1,500` tree line.**
> | Owner M owns every `Track.*` file, split across two slots (plan §5/§9.4): `Track.cs`, `Track.UI.cs` and
> `Track.Table.cs` land in **Wave 4.5** (the shared detail frame slot, gated before Wave 5 opens); only
> `Track.Drawer.cs` is **Wave 5**. Owner O *configures* the table from `Playlist.Page.cs`/`User.Page.cs` and never
> edits it; Wave 4 owner L lands `Platform/Controls.cs` + `Platform/Drag.cs` before Wave 4.5 starts

After Wave 0 every `src/apps/Wavee/...` path below lives at `src/apps/_old/Wavee/...` with the same relative path.

Cross-references (do not re-specify; configure): `00-design-system.md` (Spacing/Radii/Tok/WaveeColors/WaveeMotion/type ramp/
cover palette/`Surfaces.Artwork`), `02-cards-and-controls.md` (`MediaCard.Row`, `NowPlayingOverlay`, `PersonPicture`,
`ProgressRing`), `03-detail-frame.md` (reveal ramp, skeleton groups), `04-detail-track-table.md` (header, tier plumbing,
sort, filters, the bound list that *hosts* these rows), `21-right-rail-npv-queue-stage.md` (queue rows),
`13-search-and-browse.md` (search rows are **not** TrackRow — see §1), `27-settings-and-diagnostics.md` (density /
row-style / hide-artwork settings), `19-shell-overlays.md` (toasts raised by the menu verbs).

---

## 0. The non-negotiables

1. **One cell, every surface.** `TrackRow.Grid` (TrackRow.cs:198) is the single definition of what a track row looks like.
   Album, playlist, Liked, show, Recents, the artist album drawer and Home's top-tracks module all call it; callers vary
   only the `ColumnSet` and the container skin. Two row builders for one kind is the defect this prevents.
2. **The # cell is a state machine, not a number.** At rest: number / live equalizer / settled equalizer / star / spinner /
   number+chart glyph. On **row** hover the rest layer fades to 0 and a 24 DIP play-or-pause transport fades in
   (TrackRow.cs:1092-1105). The reveal follows the *row*, not the cell, and survives the pointer crossing onto the button.
3. **The heart is painted at rest on every row** — filled `AccentTextPrimary` when saved, outline `TextTertiary` when not
   (TrackRow.cs:935-957). Hover-only hearts left a 28 DIP dead gutter on the common (unsaved) row and hid a fact the row
   owes the reader.
4. **The trailing "…" is quiet, never absent.** `MoreRestOpacity = 0.45f` at rest, 1.0 on row hover (TrackRow.cs:989-997).
   A control that does not exist until you point at it cannot be found (user report 2026-08-10).
5. **Facts at rest, actions on hover, in one lane.** When a list has any video the trailing lane shows the film glyph at
   rest on rows that have one and the quiet "…" on rows that do not; on hover both become the full "…"
   (`VideoMoreCell`, TrackRow.cs:1020-1047). The lane is reserved once, never twice.
6. **Two type steps per row and no more.** `BodyStrong 14/20/600` for the title, `Caption 12/16/400` for every factual
   cell (number, album, added-by, date, plays, tempo, duration) — TrackRow.cs:309-313, `FactualText` 723-731. Classic
   swaps the factual rung to 14/20.
7. **Unknown is an em dash, never a zero.** `Dash = "—"` (TrackRow.cs:166). `PlayCount <= 0` → dash; `DurationMs == 0` →
   dash; a name-less album ref → dash in `TextTertiary`. A not-yet-out row states its release date in the duration lane
   instead (`ShortDate`, TrackRow.cs:306-308) and dims the whole title column to `Opacity 0.45` (TrackRow.cs:267).
8. **Columns are keyed, and the header is built from the same numbers.** Every cell carries a `CellKey` (TrackRow.cs:134-150)
   so a breakpoint cross removes exactly the departed cells; `PadXFor`/`ColGapFor` (129-130) are read by the header AND the
   rows so the grid stays header-aligned by construction.
9. **Identity wins under pressure.** The relief ladder yields Plays → BPM·key → Added by → Date added → Album → Artist →
   art → ♥ (DetailTrackTableRules.cs:154-163) until Title clears its 120 DIP floor. `#`, Title, duration and the trailing
   lane never yield.
10. **Now-playing is carried by CONTENT, never by a wash.** Accent title + the equalizer in the # cell. No row ever paints
    a "currently playing" fill (ArtistPopular.cs:34-40, DetailTracks.cs:3510-3517).
11. **A wide row never press-scales.** `PressedFill` is the only press acknowledgement on a full-width row; even the
    2 % `ScaleSubtle` tier blurs the title mid-scale (TrackRow.cs:379-383, DetailTracks.cs:3520-3524).
12. **Every affordance inside the row blocks the row's drag arm** (`BlocksDragArm = true`): heart, "…", video/more cell,
    add "+", expand chevron (TrackRow.cs:944, 976, 1007, 1042, 633). Without it a press on the heart arms the row drag and
    the like never fires.
13. **The equalizer stops ticking when it cannot be seen.** The row's hover signal is threaded in as `paused`
    (Equalizer.cs:110 via `EqualizerMotionPolicy.ShouldTick`), and reduced motion settles the bars into a fixed
    non-uniform "still playing" shape rather than flat (EqualizerMotionPolicy.cs:26-27).
14. **A drag says what it will DO.** The chip carries a resting verb ("Drag onto a playlist to add" / "Drag to reorder" /
    "Drop between playlists or onto a folder"), a live target's caption supersedes it, and a refusal supersedes both with a
    reason plus the blocked glyph (WaveeResourceDrag.cs:342-346, DragChip.cs:110-152).
15. **A refused drop is never silent.** `PlaylistDropRefusalRules.Evaluate` (WaveeDragRules.cs:126-137) answers the accept
    test and the caption from one table, so the refusal reason is always the reason the gate actually used.
16. **THREE row skins, not two.** Modern inset pill (`RowInset` margin, corner 6, zebra + border), Classic (margin 0,
    corner 0, hairline divider, no zebra) and **plain** — `plainRows = !classic && _verticalHeader && !_verticalHeroRowFlow`
    (DetailTracks.cs:3484): the hero/vertical page layout's STACKED flow drops the pill, the border and the zebra
    entirely, because on a one-column page an inset pill reads as a second, narrower page. Hover/press still paint
    (`RowHover`/`RowPressed`). Every skin hosts the identical `Grid`.
17. **The hide-artwork setting removes the art LANE, not just the picture.** `AppearancePrefs.TrackArtworkHidden`
    reaches `IdentityColumns(… artworkHidden …)` (DetailTrackTableRules.cs:38) and every eager caller keeps a second
    `ColumnSet`+`TrackSize[]` pair (`TrackColsNoArt`, `SingleRowColsNoArt`, `ChildColsNoArt`, ArtCard's
    `showArtwork:false`). A hidden thumb must never leave a reserved empty column.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
TrackRow (static)                               Components/TrackRow.cs:99
├─ ColumnSet (record struct)                    TrackRow.cs:77      which optional lanes + Tier + Classic
├─ State (record struct)                        TrackRow.cs:160     IsNow/IsPlaying/IsBuffering/IsTop/Saved
│   └─ StateOf(bridge, lib, track, isTop, extraBuffering)   TrackRow.cs:168
├─ Grid(t, i, st, set, tracks, rowH, title, …) → GridEl      TrackRow.cs:198   THE cell
│   ├─ [c.num]      NumberCell(...)             TrackRow.cs:1067   ZStack: rest layer (HoverOpacity 0) + transport layer (Opacity 0, HoverOpacity 1)
│   │   ├─ rest ─ Spinner()                     TrackRow.cs:1122   ProgressRing.Indeterminate 16, AccentTextPrimary
│   │   ├─ rest ─ WaveeEqualizer.Of(...)        Components/Equalizer.cs:30      now-playing (Modern)
│   │   ├─ rest ─ Icon(Icons.Volume,13)         TrackRow.cs:1076   now-playing (Classic)
│   │   ├─ rest ─ Icon(FavoriteStarFill,11)     TrackRow.cs:1080   album top track
│   │   ├─ rest ─ Caption(n+1) [+ ChartGlyph]   TrackRow.cs:1073 / 1113
│   │   └─ transport ─ Icon(Play|Pause,12) in a 24x24 PressScale box  TrackRow.cs:1086-1091
│   ├─ [c.heart]    CenterCell(Heart(...))      TrackRow.cs:238 / 935
│   ├─ [c.art]      CenterCell(Surfaces.Artwork(t.Image, seed, thumb, thumb, Radii.Control, decodePx: art*2))  TrackRow.cs:246
│   ├─ [c.title]    BoxEl(clip) > titleCol      TrackRow.cs:259-275
│   │   ├─ title    (CALLER-supplied: plain TextEl / bound TextEl / Marquee / ClassicTitleLine)  TrackRow.cs:255
│   │   └─ MetadataLine(t, go, artists, album, explicit, ink)   TrackRow.cs:807   Eyebrow(EPISODE) · [E] · artists · album
│   ├─ [c.artist]   LeftCell(ArtistLinks(...))  TrackRow.cs:278 / 780     Classic only
│   ├─ [c.album]    LeftCell(AlbumLink(...))    TrackRow.cs:282 / 870
│   ├─ [c.by]       AddedByCell(by, profile)    TrackRow.cs:285 / 891     PersonPicture 24 + Caption
│   ├─ [c.date]     LeftCell(FactualText(DetailFormat.DateAddedLabel))    TrackRow.cs:287
│   ├─ [c.plays]    EndCell(FactualText(PlaysLabel | Dash))               TrackRow.cs:297 / 153
│   ├─ [c.tempo]    EndCell(TempoCell(t))       TrackRow.cs:300 / 584     6px Camelot swatch + BPM · key
│   ├─ [c.dur]      EndCell(FactualText(DurationCell | ShortDate | Dash)) TrackRow.cs:312
│   ├─ [c.video]    CenterCell(VideoMoreCell(hasVideo, moreEnabled))      TrackRow.cs:320 / 1020
│   ├─ [c.more]     actionsCell (CALLER: MoreButton(enabled, classic))    TrackRow.cs:323 / 966
│   └─ [c.expand]   expandCell  (CALLER: ExpandChevron(open, toggle))     TrackRow.cs:327 / 625
├─ Row(...) → Embed.Comp(EagerRowProps, EagerRowHost)        TrackRow.cs:346-398   eager hover container + Grid
├─ ArtCard(...)  → BoxEl                        TrackRow.cs:405-514   art-forward cell (rail/grid)
├─ ArtCardSelectSkin(scope, content, kind, showCheckbox)      TrackRow.cs:516-574
├─ ExplicitBadge() / ClassicExplicitBadge(ink)  TrackRow.cs:745 / 755
├─ MoreButton / AddButton / Heart / Spinner / ExpandChevron   TrackRow.cs:966 / 1001 / 935 / 1122 / 625
├─ LikeEdge(prevRef, uri, saved) → bool         TrackRow.cs:920      per-slot unsaved→saved edge detector
└─ CenterCell / LeftCell / EndCell              TrackRow.cs:1130-1135

ArtistMoreButton : Component                    TrackRow.cs:20       "+N" artist overflow chip → MenuFlyout

WaveeEqualizer (static)                         Components/Equalizer.cs:23
├─ EqHost : Component                           Equalizer.cs:59      one 30 Hz ticker, 3 FloatSignals, batched writes
└─ EqBar : Component                            Equalizer.cs:189     2.5x13 bar, bound Transform = Scale(1, sy)

EqualizerMotionPolicy (pure)                    Components/EqualizerMotionPolicy.cs:17
NowPlayingMatch (pure)                          Components/NowPlayingMatch.cs:14

Row container skins (owned by the HOST, not by TrackRow)
├─ DetailTracks.BoundRowSkin(scope, content, …) Features/Detail/DetailTracks.cs:3447   zebra/hover/press/pill/divider/drop
├─ TrackRow.EagerRowHost                        TrackRow.cs:357      eager hover row
├─ TrackRow.ArtCardSelectSkin                   TrackRow.cs:516      bound art-card selection skin
└─ RowSwipe.Wrap / WrapBound                    Components/RowSwipe.cs:70 / 91        touch swipe belt

Expanded-row drawer
└─ TrackVersionsPanel : Component               Components/TrackVersionsPanel.cs:29
    ├─ TrackFactsStrip.Build(track, opts, go)   Components/TrackFactsStrip.cs:58
    │   ├─ hero line   Stat(f)                  TrackFactsStrip.cs:132   StatHero numerals + Caption label
    │   ├─ prose line  ProseRun / AddedByChip / LinkValue   TrackFactsStrip.cs:168 / 199 / 236
    │   └─ genre line  GenreRun / FlagRun       TrackFactsStrip.cs:259 / 266
    ├─ Eyebrow("Versions and formats")          TrackVersionsPanel.cs:133
    └─ ConnectedRow(version, …)                 TrackVersionsPanel.cs:173   gutter rail + stub + VersionRow/PendingVersionRow
        └─ FormatSplitButton                    Components/FormatSplitButton.cs:22     30+20 x 28 split play/caret

Selection
└─ SelectionCommandBar : Component              Components/SelectionCommandBar.cs:18   thumbs · count · commands · … · ✕

Save / follow
├─ SaveButton : Component                       Components/SaveButton.cs:21
├─ PreSaveButton : Component                    SaveButton.cs:61      resolves spotify:prerelease: (kind 138) itself
├─ FollowButton : Component                     SaveButton.cs:132
│   └─ FollowButton.SkeletonShape()             SaveButton.cs:169     the bordered pill the deriver shimmers (NOT a bar)
└─ FollowTextAction : Component                 SaveButton.cs:187     → WaveeCta.TextAction (plateless context band)

Drag & drop
├─ WaveeResourceDragPayload (record)            Features/DragDrop/WaveeResourceDrag.cs:32
├─ WaveeDragChipModel (pure)                    WaveeDragChipModel.cs:21
├─ WaveeDragKindMap / PlaylistDropRefusalRules / TabDropRules / QueueDragRules / SidebarRailDropRules   WaveeDragRules.cs
├─ WaveeResourceDrag.Chip(state) → DragChipSpec  WaveeResourceDrag.cs:305   the app's WHOLE chip contribution
└─ PlaylistInsertionPreview.Cards(payload, rowH) PlaylistInsertionPreview.cs:20

Menus
├─ TrackContextMenu.Build / BuildSingle         Actions/TrackContextMenu.cs:21 / 32
├─ Menus.Tracks(ctx, showGoToAlbum, extras)     Actions/Menus.cs:56
├─ Menus.TrackRows(ctx, …)                      Actions/Menus.cs:81
├─ Menus.TrackTransportStrip(ctx)               Actions/Menus.cs:61
├─ Menus.AddTrackTransportRows(rows, ctx)       Actions/Menus.cs:71    the rows-only projection (a 240-DIP pane has no strip)
├─ Menus.ModuleTrack(ctx) / NowPlaying(s, track) Menus.cs:150 / 1111   the two pre-bound variants
├─ Menus.ShareItem / VideoItem / AddToPlaylistItem / MoveToPlaylistItem / GoToArtistsItem / GoToPodcastItem   Menus.cs:201/218/271/397/242/256
├─ Menus.PlaylistDepositItem (the ONE deposit submenu shape)          Menus.cs:304
├─ Menus.TrackHeader / Header / PlainHeaderText Menus.cs:1115 / 1147 / 1168
├─ TrackActions.* (**13** AppAction singletons: Play · PlayNext · AddToQueue · ToggleLike · CopyLink · GoToAlbum ·
│                  GoToArtist · GoToSongRadio · ViewCredits · CopySpotifyUri · OpenInSpotifyWeb ·
│                  RemoveFromThisPlaylist · RemoveFromQueue)          Actions/TrackActions.cs:18-254
├─ ActionIcons.Resolve(key, isChecked)          Actions/ActionIcons.cs:46      28 keys → IconRef
└─ TrackCreditsDialog : Component               Actions/TrackCreditsDialog.cs:21   loading / grouped list / "No credits"
```

### 1.2 The same tree in 0.3 terms

Component props freeze at mount. The column **`→` marks how live data reaches the node**: `Sig` = signal/`item.*` bind,
`Ctx` = context, `Key` = remount, `Static` = frozen at mount and correct because it never changes.

| 0.3 node | File / form | Inputs | Live via |
|---|---|---|---|
| `Track.Row(in BoundItemScope<Track> item, in RowStyle style)` | `Entities/Track.UI.cs`, static | `item` (slot-bound), `style` (value) | `Sig` — every cell is an `item.*` closure built once per slot |
| `RowStyle` (readonly record struct) | `Entities/Track.cs` CORE | lanes + `Tier` + `Density` + `Classic` + `Edge` kind | recomputed by the host and **re-pushed**, never a ctor field (see §9 trap 1) |
| `Track.NumberCell(item, style)` | `Track.UI.cs`, static | `item.Signal(t => Playback.IsNowPlaying(t))`, `item.Signal(… IsPlaying)`, `Playback.PendingFor(t)` | `Sig` |
| `Controls.Equalizer(IReadSignal<bool> playing, Func<ColorF> ink, float h, IReadSignal<bool>? paused)` | `Platform/Controls.cs`, `Component` | props record | `Sig` (props re-push; **never** a Key flip on play↔pause) |
| `Track.HeartCell(item)` | `Track.UI.cs` | `item.Signal(t => User.Me.Likes(t))` | `Sig` |
| `Controls.SaveButton(uri, glyph, box, name, accent)` | `Platform/Controls.cs`, `Component` | `Uri` frozen | `Key` = `"save:" + uri`; ink via `Ctx` (`PageAccent`) |
| `Track.MetadataLine(item, style)` | `Track.UI.cs` | `item.Spans((t,b) => b.Artists(t.ArtistSlots))` | `Sig` + `item.InvokeSpan` |
| `Track.TempoCell(item)` | `Track.UI.cs` | `item.Show(t => t.Knows(TrackFields.Audio))` | `Sig` |
| `Controls.MoreButton(enabled, classic)` / `Track.VideoMoreCell(item)` | `Platform/Controls.cs` / `Track.UI.cs` | static geometry; `ClickRequestsContext` | `Sig` for `hasVideo` |
| `Controls.ExpandChevron(Func<bool> open, Action toggle)` | `Platform/Controls.cs` | thunks | `Sig` |
| `Controls.ExplicitBadge()` / `ClassicExplicitBadge(ink)` | `Platform/Controls.cs` | none | `Static` |
| `Track.RowSkin(scope, content, style)` | `Track.UI.cs` | `scope.Index`, `scope.IsSelected` | `Sig` — every fill/border/corner is a `Prop.Of` closure over the slot index |
| `Track.Drawer` (facts + versions) | **`Entities/Track.Drawer.cs`**, `Component` (NOT `Track.UI.cs` — see §9's file plan) | `Track.DrawerModel` (handle + `TrackFactsOptions` + indent + the two invoke thunks), pushed as a **context**, exactly as 0.2.9 does: `Context<Model?> Props` TrackVersionsPanel.cs:41-47, `UseContext(Props)` :81 | `Ctx` + `Key` — the host provides the model and keys the body `"drawer-body:" + rowKey` (DetailTracks.cs:3347-3348); the clip box is `"drawer:" + rowKey` and the presence box `"drawer-presence:" + rowKey` (:3316, :3342). **The drawer is mounted by the TABLE (`Track.Table.cs`), not by `Track.Row`** — its only 0.2.9 call site is DetailTracks.cs:3293-3348 |
| `Controls.FormatSplitButton(uri, onPlay)` | `Platform/Controls.cs`, `Component` | `uri` frozen | `Key = "fmt:" + uri` |
| `Controls.SelectionCommandBar(sel, trackAt, exit, host)` | `Platform/Controls.cs`, `Component` | `SelectionModel` | `Sig` — reads `sel.Version.Value` in the SAME Render that builds the labels |
| `Controls.RowSwipe(row, ctxThunk, group, leading, trailing, resetKey)` | `Platform/Controls.cs`, static | thunks | `Sig` (`resetKey = scope.Index`) |
| `Drag.Payload.ForTrack(Track)` / `ForTracks(span)` / `ForQueueRow(entry)` | `Platform/Drag.cs` | handles | cold — built once at drag promotion |
| `Drag.Chip(DragState) → DragChipSpec` | `Platform/Drag.cs` | live `DragState` | called per pointer move inside the 0-alloc frame region |
| `Drag.InsertionPreview(payload, rowH)` | `Platform/Drag.cs`, static | payload | rebuilt per gap change |
| `Shell.ActionsFor(EntityKind.Track, ctx)` | `Shell/Shell.cs` (Wave 4, owner I) | `ActionContext` | built lazily inside the menu open thunk |

**Which surfaces call it (every 0.2.9 call site, tabulated).** Search is deliberately absent: a search song row is
`MediaCard.Row` (SearchPage.cs:837) — see `13-search-and-browse.md`.

| # | Surface | Entry | Cell | ColumnSet | Width tracks (DIP) | Row height | Notes |
|---|---|---|---|---|---|---|---|
| 1 | Detail table (album / playlist / Liked / show) | `DetailTracks.RowGrid` DetailTracks.cs:3410 | `Grid` | tier + relief derived, `SetFor` DetailTracks.cs:466 | `TracksFor` DetailTracks.cs:554 | `RowHeightFor(density)` 40/48/56/64 | the only tiered caller; density-keyed art 32/32/40/48 |
| 2 | Artist page → album drawer | `AlbumDrawerPanel.DrawerTrackRow` ArtistPage.AlbumExpand.cs:330 | `Grid` | `(Heart:true)`, no thumb/album/by/date/plays/video, `Actions` on | `[26, 28, *, 44, 32]` AlbumExpand.cs:123 | 28 content in a 32 slot | title 13/600, swipe belt, per-album SelectionModel |
| 3 | Recents — single-track arm | `RecentsPage.BindTrackRow` RecentsPage.cs:2028 | `Grid` | `SingleRowCols` (Heart, Thumb, Actions) RecentsPage.cs:2001 | `[36, 28, 32, *, 52, 40]` | 64 | whole row is `Role=Button`, `OnClick=Invoke`, drag source |
| 4 | Recents — group drawer child | same | `Grid` | `ChildCols` RecentsPage.cs:1914 | `[30, 28, 32, *, 52, 112]` | 40 | actions cell = `played-at Caption(60) + "…"`; `ChildActionsCol = 40 + 12 + 60` |
| 5 | Home artist module — "Top tracks" | `HomeModules.Artists.TopTracks` HomeModules.Artists.cs:317 | `Row` (eager) | `TrackCols` (Plays, Heart, Thumb) :254 | `[36, 28, 32, *, 84, 52, 160]` | 48 | 5 rows max; actions cell = personal-top badge + "…"; `Key = "home-toptrack:" + uri + ":art=" + showArtwork` |
| 6 | Detail — recommendation rows | `DetailTracks.RecRow` DetailTracks.cs:3016 | `ArtCard` | `RecColumns` (nothing optional) :185 | n/a | pinned to `rowH` (MaxHeight + clip) | art 40, `explicitBadge`, duration, `onAdd` "+", `showMore` |
| 7 | Now-playing panel — "Next up" | `NowPlayingPanel.NextUpSection` :425 | `ArtCard` | `NextCols` (Video:true) :37 | n/a | ≥52 (Rail) | art 40, no duration, no heart |
| 8 | Video rail — "Up next" | `VideoRailPanel.Row` :145 | `ArtCard` | `UpNextCols` (Video:true) :42 | n/a | ≥52 (Rail) | identical config to #7 |
| 9 | Module page — playables | `ModulePage.PlayablesBlock` :551 | `ArtCard` | `PlayableCols` (Video:true) :532 | n/a | ≥52 (Rail) | synthetic module tracks; `Menus.ModuleTrack` |
| 10 | Artist page — "Popular" chart | `ArtistPopular.Row` :365 | **bespoke** row using `NumberCell` + `Heart` + `ExplicitBadge`/`ClassicExplicitBadge` + `PlaysLabel` | n/a | rank 24 · art 44 (40 classic) · mid ★ · trail | 56 (48 classic) | ≤5 rows, ≤2 columns, page-snapped shelf |
| 11 | Queue panel + now-playing card | `QueuePanel.QueueRow` :565 / `NowPlayingCard` :360 | bespoke; uses `Heart`, `ArtistLinks`, `ClassicExplicitBadge`, `Invoke` | n/a | heart lane 26 (card 30) × `RowExtent` · art `QueueArt` **34** with a 26 FAB · "…" · ✕ | `RowExtent` **44** (Modern card 64) | see `21-right-rail-npv-queue-stage.md` |
| 12 | Omnibar suggestion row | `ShellToolbar` :476 | `Heart` only (Track suggestions), beside `IconButton(Play)` · `MoreButton` · a type pill | n/a | trailing Gap 2 | n/a | see `18-shell-frame.md` |
| 13 | Settings density preview | `WaveePicker.DensityRows` :105 | `RowHeightFor` × `ArtSizeFor` × `PreviewScale 0.25` | n/a | n/a | n/a | the wireframe MUST mirror the real numbers |
| 14 | Playlist insertion gap | `PlaylistInsertionPreview.Row` :35 | bespoke card reusing `ThumbSize`, `PadX`, `RowInset` | n/a | n/a | caller's `rowH` | `Cap = SortableMath.DefaultPreviewCap` **3** (the FRAMEWORK's cap, never a local 3), last card carries "+N" |
| 15 | "View credits" modal | `TrackActions.ViewCredits` :148 → `TrackCreditsDialog` | not a row — a `ContentDialog` body | n/a | MinWidth 360 · MaxWidth 440 · scroll MaxHeight 420 | n/a | W27; the only surface in this chapter with its own loading + empty states |

**Hide-artwork pairs.** Every eager caller carries a SECOND `(ColumnSet, TrackSize[])` pair for
`AppearancePrefs.TrackArtworkHidden` — `TrackColsNoArt`/`TrackColumnsNoArt` (HomeModules.Artists.cs:256, 266),
`SingleRowColsNoArt`/`SingleRowTracksNoArt` and `ChildColsNoArt`/`ChildTracksNoArt` (RecentsPage.cs:2002, 1916),
`showArtwork:` on `ArtCard`/`ArtistPopular.Row`/`PlaylistInsertionPreview.Cards` — and the detail table routes it
through `IdentityColumns`. In 0.3 this is ONE `RowStyle.ShowArt` flag feeding both the lane list and the track list.

---

## 2. Wireframes

Scale: **1 monospace char ≈ 8 DIP**. `▁` marks a shimmer block.

### W1 — Detail table row, Modern, tier 0, density Default (48), playlist @ 880 DIP (right column)

```
  margin 8 ┆ pad 8                                                                        pad 8 ┆ margin 8
 ┌─────────┼────────────────────────────────────────────────────────────────────────────────────┼─────────┐
 │         │ #    ♥   [art]  Title ...................  Album .........  Added by ....  Date ..  │         │
 │         │ 28   28   32     150 (star 1.0)             112 (star .75)   132            88      │         │
 │         │                                                                                     │         │
 │         │  ...  Plays  0:00   ▣    ⌄                                                          │         │
 │         │        52     52    28   26                                                         │         │
 └─────────┼────────────────────────────────────────────────────────────────────────────────────┼─────────┘
   row skin: MinHeight 48 · Corners 6 · Margin L/R 8 · Border 1 (zebra rows only) · ZStack
   grid:     Padding L/R (PadX 16 − RowInset 8) = 8 · ColGap 12 · RowHeight 48

 laid out (one line, proportional; ┊ = 12 DIP column gap):
 ┌──┊──┊────┊──────────────────────┊─────────────┊────────────────┊──────────┊──────┊──────┊───┊──┐
 │ 1┊ ♡┊[▨] ┊Midnight City         ┊Hurry Up...  ┊ (◉) christos   ┊3 days ago┊ 1.85B┊  4:03┊ ▣ ┊ ⌄│
 │  ┊  ┊    ┊M83                   ┊             ┊                ┊          ┊      ┊      ┊   ┊  │
 └──┊──┊────┊──────────────────────┊─────────────┊────────────────┊──────────┊──────┊──────┊───┊──┘
   28  28  32          150               112            132           88       52     52    28  26

 Title   BodyStrong 14/20/600 TextPrimary · NoWrap · CharacterEllipsis · MinWidth 0
 Subline Caption 12/16/400 TextSecondary (artist links) · gap 4 · own row, column Gap = Spacing.XXS (2)
 Album/Date/By  Caption 12/16 TextSecondary   Plays  Caption 12/16 TextTertiary   Duration  Caption 12/16 TextSecondary
 ♥  28x28 circle, Icon Heart 14 TextTertiary (outline) / HeartFill 14 AccentTextPrimary (saved)
 ▣  the trailing Video/More lane — Icon Movie 13 TextTertiary at rest
 ⌄  ExpandChevron 24x24, Icon ChevronRight 12 TextSecondary (closed)

 WHY 150 / 112 AT 880 (and why this does not contradict W9). This set has the BPM·key lane OFF (tempo is opt-in), so
 its fixed total is 28+28+32+132+88+52+52+28+26 = 466 over 11 columns → min width 466+120+90 +10·12 +2·16 = 828.
 At 880 the star pool is 880 − 32 pad − 120 gaps − 466 = 262, split 1 : 0.75 → Title 149.7, Album 112.3. W9's worked
 example adds the 80-DIP Tempo lane, which is exactly what pushes the same set's minimum to 920 and relieves Plays
 at 880. Both are right; they are different lane sets.
```

### W2 — the SAME row, hovered @ 880

```
 ┌──┊──┊────┊──────────────────────┊─────────────┊────────────────┊──────────┊──────┊──────┊───┊──┐
 │ ▶┊ ♡┊[▨] ┊Midnight City         ┊Hurry Up...  ┊ (◉) christos   ┊3 days ago┊ 1.85B┊  4:03┊ ⋯ ┊ ⌄│
 │  ┊  ┊    ┊M83                   ┊             ┊                ┊          ┊      ┊      ┊   ┊  │
 └──┊──┊────┊──────────────────────┊─────────────┊────────────────┊──────────┊──────┊──────┊───┊──┘
  Fill     → RowHover (odd rows: RowHoverZebra)
  Border   → StrokeCardDefault on ALL four sides (1 DIP; always present, transparent at rest → no layout nudge)
  # cell   → rest layer Opacity 1→0, transport layer 0→1 (both are HoverOpacity, engine-serviced off the ROW)
             transport = 24x24 box, PressScale 0.92, Icon Play 12 TextPrimary (Pause + accent when this row is playing)
  ▣ lane   → the film glyph fades out (HoverOpacity 0) and Icon More 16 TextSecondary fades to Opacity 1
  cursor   → Hand over the transport layer only; the row itself is Role=Button
```

### W3 — now-playing row (playing) @ 880

```
 ┌──┊──┊────┊──────────────────────┊─────────────┊──────────────────────────────────────┊──────┊───┊──┐
 │▍▁▍┊ ♥┊[▨] ┊«Midnight City…      ┊Hurry Up...  ┊ …                                    ┊  4:03┊ ▣ ┊ ⌄│
 │▁▍▁┊  ┊    ┊M83                  ┊             ┊                                      ┊      ┊   ┊  │
 └──┊──┊────┊──────────────────────┊─────────────┊──────────────────────────────────────┊──────┊───┊──┘
   equalizer: 3 bars, 2.5 DIP wide, 2 DIP gap, box height 13, bottom-anchored (TransformOriginY 1)
              ink = PageAccent.Ink (Recents) else Tok.AccentTextPrimary
   title:     Marquee (14/600, AccentTextPrimary) — ONLY on the now-playing row; every other row is a plain TextEl
              (DetailTracks.BoundTitlePlain :2703). The Appearance setting "disable marquee" (`row.MarqueeDisabled`,
              DetailTracks.cs:2781) keeps the now-playing row on the plain accent ellipsis title — same colour, no scroll.
   heart:     HeartFill 14 AccentTextPrimary when saved
   NO fill change. Now-playing is content state, not a wash.

 PAUSED now-playing row (still the current track, transport stopped)
   · equalizer settles FLAT at scaleY 0.4 (one batched write, Equalizer.cs:100) — NOT the reduced-motion still shape
   · title stays accent + marquee; heart, transport glyph and every other cue are unchanged
   · hovering swaps the settled bars for a PLAY glyph (accent, because isNow) — not Pause
```

### W4 — buffering row (the row's PlayAsync command is in flight, or the bridge reports re-buffering)

```
 ┌──┊──┊────┊─────────────────────────
 │(◌)┊ ♡┊[▨] ┊Midnight City
 └──┊──┊────┊─────────────────────────
   ProgressRing.Indeterminate size 16, foreground AccentTextPrimary
   Shown whether or not the pointer is over the row (the transport layer is ALSO the spinner: TrackRow.cs:1084)
```

### W5 — chart row (a chart playlist; `Track.Chart` non-null) @ 880

```
 ┌──────┊──┊────┊──────────────────  rest layer only; the glyph never competes with EQ/star/spinner
 │ 1 ▲  ┊ ♡┊[▨] ┊Sailor Song          Up    → "▲" 8/12/700 Tok.SystemFillSuccess
 │ 2 ▼  ┊  ┊    ┊                     Down  → "▼" 8/12/700 Tok.SystemFillCritical
 │ 3 NEW┊  ┊    ┊                     New   → "NEW" 8/12/700 Tok.SystemFillSuccess
 │ 4    ┊  ┊    ┊                     Equal/Unknown → nothing at all, never a guessed arrow
 └──────┊──┊────┊──────────────────
   layout: BoxEl{ Direction 0, Gap 2, AlignItems Center, Children = [number, glyph] }  (TrackRow.cs:1082)
```

### W6 — not-yet-out row (`IsNotYetOut()` — unavailable AND no past AvailableAt)

```
 ┌──┊──┊────┊──────────────────────┊─────────────┊──────┊──────┊
 │ 7┊ ♡┊[▨] ┊Unreleased Track      ┊Album         ┊   —  ┊ 4 Sep┊     Opacity 0.45 on the WHOLE title column
 └──┊──┊────┊──────────────────────┊─────────────┊──────┊──────┊
   plays     → Dash "—" TextTertiary        (0 is never "nobody played it")
   duration  → ShortDate(AvailableAt) "4 Sep" / "4 Sep 2027" TextTertiary, else Dash
   # cell    → NO hover play button (onPlay passed as null, TrackRow.cs:233)
   un-dims by itself the moment the live timestamp passes — no refetch
```

### W7 — the density ladder (Modern) — the four settings values, same lanes

```
 Compact (0)     40 DIP row, art 32       ┌────────────────────────┐ 40
 Default (1)     48 DIP row, art 32       │ 1  ♡ [▨] Title         │
 Cozy    (2)     56 DIP row, art 40       └────────────────────────┘
 Comfort (3)     64 DIP row, art 48       ┌────────────────────────┐ 48   art 32 (row−art = 16)
                                          │ 1  ♡ [▨] Title         │
 Classic ladder (TrackRowStyle = 1)       │        M83             │
 Compact 36 · Default 40 · Cozy 44        └────────────────────────┘
 Comfort 48; art 32 except Comfort → 40   ┌────────────────────────┐ 56   art 40 (row−art = 16)
                                          │ 1  ♡ [ ▨ ] Title       │
 rule (DetailTrackTableRules.cs:51-56):   │           M83          │
 the row keeps ≥8 DIP above and below     └────────────────────────┘
 the art  (row − art ≥ 16)                ┌────────────────────────┐ 64   art 48 (row−art = 16)
                                          │ 1  ♡ [  ▨  ] Title     │
 TrackRow.ArtSizeFor forwards to          │             M83        │
 DetailTrackTableRules.ArtSizeFor         └────────────────────────┘
```

### W8 — Classic skin row (TrackRowStyle = 1), tier 0, density Default (40) @ 880

```
 ┌──┊──┊────────────────────────────────────────────────┊──────────────┊────────────┊──────┊──┐
 │ 1┊ ♡┊Midnight City  🎬  ·  M83, Kavinsky   [EXPLICIT] ┊Hurry Up...   ┊3 days ago  ┊  4:03┊ ⋯│
 └──┊──┊────────────────────────────────────────────────┊──────────────┊────────────┊──────┊──┘
 ─────────────────────────────────────────────────────────────────────────────────────────────  ← 1 DIP hairline
   Differences from Modern (all in ONE ColumnSet flag, `Classic`):
   · NO art column, NO metadata subline, NO row inset/pill → Margin 0, Corners 0, grid Padding = padX (16) both sides
   · Title cell is ONE SpanTextEl run: title 14/600 + " " + [Movie glyph, Theme.IconFont, tertiary] + "  ·  " + linked
     artists (TrackRow.ClassicTitleLine :647), joined by ", " in secondaryInk; the EXPLICIT word-mark is pinned to the
     cell's trailing edge behind a Gap 8. With the Artist LANE up (tier < 4) the title cell is the caller's plain title
     + an optional Icon(Movie, **12**, tertiary) instead — a different branch, a different glyph size (:693)
   · a dedicated Artist LANE exists at tier < 4 (ClassicArtistFoldTier); below that artists fold into the title run
   · the inline film glyph itself drops at tier ≥ 4 (`ClassicInlineVideoDropTier`, DetailTrackTableRules.cs:26, 73):
     Classic has NO trailing video lane at any tier, so below 440 DIP a Classic row states nothing about a video
   · Added-by drops the PersonPicture entirely — Classic renders the name as bare `FactualText` (TrackRow.cs:895-897)
   · factual rung is 14/20 (not 12/16) — `FactualText(classic: true)` TrackRow.cs:723
   · # cell now-playing = Icon(Volume, 13, accent), NOT the equalizer
   · every ink flips to AccentTextPrimary on the now-playing row (secondaryInk/tertiaryInk, TrackRow.cs:228-229)
   · MoreButton is square (Radii.None), no hover/press scale, Opacity 0 at rest → 1 on hover
   · row divider: 1 DIP StrokeDividerDefault at AlignSelf=End, inset by PadXFor(tier) each side
   · rest fill transparent; hover/press = RowHover / RowPressed (no zebra)
```

### W9 — the width ladder: which lanes at which width (playlist, Modern)

```
 tier   6      5      4      3        2        1        0
 px    <300  300-339 340-439 440-559 560-719 720-859  ≥860        hysteresis 24 DIP (re-admit only with margin)
 ───────────────────────────────────────────────────────────────  DetailLayoutBreakpoints.cs:10-11
 #       ●      ●      ●      ●        ●        ●        ●        never yields
 ♥       ·      ·      ●      ●        ●        ●        ●        tier < 5
 art     ·      ·      ●      ●        ●        ●        ●        tier < 5
 Title   ●      ●      ●      ●        ●        ●        ●        star 1.0, floor 120
 Album   ·      ·      ·      ·        ·        ●        ●        tier < 2 (else folds into the subline)
 By      ·      ·      ·      ·        ·        ·        ●        tier < 1
 Date    ·      ·      ·      ·        ●        ●        ●        tier < 3
 Plays   ·      ·      ·      ·        ●        ●        ●        tier < 3
 BPM·key ·      ·      ·      ●        ●        ●        ●        ShowTempo: Tempo && Tier ≤ 3
 0:00    ●      ●      ●      ●        ●        ●        ●        never yields
 ▣ / ⋯   ·      ●      ●      ●        ●        ●        ●        tier < 6 — exact complements (More rides IN ▣)
 ⌄       ·      ●      ●      ●        ●        ●        ●        tier < 6
 padX   8      12     12     16       16       16       16        PadXFor: ≤3→16, ≤5→12, else 8
 colGap 8      8      12     12       12       12       12        ColGapFor: ≤4→12, else 8

 THEN the relief ladder re-measures what the tier admitted (DetailTrackTableRules.cs:154-216):
   MinWidthFor = Num 28 + TitleFloor 120 + Duration 52 + Σ(present fixed lanes) + (cols−1)·colGap + 2·padX
   yield order: 1 Plays · 2 BPM·key · 3 Added by · 4 Date added · 5 Album · 6 Artist · 7 art · 8 ♥
   worked example (tier 0, full playlist set, colGap 12, padX 16):
     step 0 needs 28+120+52 +28+32+90+132+88+52+80+28+26 = 756, 12 cols → 132 gaps → 32 pad → 920 DIP
     at 880 DIP → step 1 (drop Plays): 704, 11 cols → 120 → 32 → 856 ≤ 880 ✔
   hysteresis 24 DIP, same asymmetry as the tier ladder: yield immediately, re-admit only with margin
```

### W10 — shimmer row (cold list, before this slot's reveal)

```
 ┌──┊──┊────┊──────────────────────┊─────────────┊──────┊──────┊
 │  ┊  ┊    ┊                      ┊             ┊      ┊      ┊    GridEl with the SAME Columns/RowHeight
 └──┊──┊────┊──────────────────────┊─────────────┊──────┊──────┊    and one EMPTY BoxEl per track
   DetailTracks.ShimmerRow :2625 — Key = ShimmerRowKey; the SkeletonDeriver paints the blocks.
   Identical lane geometry to the live row, so the reveal is a cross-fade and never a reflow.
```

### W11 — reveal in progress (the ramp crossing)

```
   slot's FIRST render was a shimmer (latch 1)  →  real row wrapped in
     BoxEl{ Key = "row:real", Direction 0, Grow 1, Animate = Opacity tween 280 ms FluentDecelerate, Enter Opacity 0 }
   slot mounted already-revealed (latch 2)      →  the grid is returned BARE: not one extra node, not one transition slot
   (DetailTracks.cs:2793-2813). Reduced motion still cross-fades opacity (ReducedSnap keeps the fade).
```

### W12 — selection: pill at rest, check lane in multi-select

```
 single selection (checks hidden)                 multi-select mode (checks visible)
 ┌─┬─────────────────────────────────────┐        ┌──────────┬──────────────────────────────┐
 │▐│ 1  ♡ [▨] Midnight City              │        │  ☑       │ 1  ♡ [▨] Midnight City       │
 └─┴─────────────────────────────────────┘        └──────────┴──────────────────────────────┘
   pill: 3 x 16, Margin L 2, Corners 1.5,           lane: leftMargin 4 + CheckboxSize 20 + 4 = 28 DIP
         Fill = _rowAccent (page accent),           Enter/Exit: Dx −28, Opacity 0, 333 ms FluentDecelerate
         Opacity bound to isSel && !checksVisible,  content lane slides right on the same 333 ms tween
         PressScale 10/16 = 0.625                   (DetailTracks.cs:3572-3583, SelectorVisuals.cs:62-65)
   Selection does NOT change the row fill. The pill is the only cue (Classic: no pill at all → hover fill instead;
   the pill element still exists but takes Corners Radii.None and an Opacity closure that is constant 0).
   A row whose DRAWER is open squares its bottom corners: CornerRadius4(6,6,0,0) (DetailTracks.cs:3499-3503).
```

### W12b — the THIRD skin: plain rows (hero page layout, stacked flow)

```
 ┌──────────────────────────────────────────────────────────┐
 │ 1  ♡ [▨] Midnight City                      3:12         │   Margin 0 · Corners 0 · Border 0 · NO zebra
 └──────────────────────────────────────────────────────────┘   HoverFill RowHover · PressedFill RowPressed
   plainRows = !classic && _verticalHeader && !_verticalHeroRowFlow   (DetailTracks.cs:3484)
   The grid's own Padding is still PadXFor(tier) − RowInset, so the columns stay header-aligned even with no margin.
   Selection pill: PRESENT and live (the classic-only suppression does not apply) — plain rows are still Modern.
```

### W12c — an OS file dragged over a row (.mp4 attach target)

```
 ┌──┊──┊────┊──────────────────────┊    the row under the pointer takes WaveeColors.RowHover through the SAME bound
 │ 1┊ ♡┊[▨] ┊Midnight City         ┊    Fill closure the zebra uses — no extra overlay node per row
 └──┊──┊────┊──────────────────────┊    (_videoDropRow signal, DetailTracks.cs:3457-3459, 3512-3516)
   only built when `_acts.VideoOverrides` exists — no curation service, no DropTargetSpec, no allocation, no cue
   a drop with no .mp4 in it falls THROUGH to the shell's play-this-file path (LocalFileActions.PlayDropped) — the
   row target sits between the pointer and the shell target, so swallowing it would make the whole list a dead zone
```

### W13 — SelectionCommandBar, three fit tiers (self-measured lane width)

```
 fit 0 — lane ≥ 760 DIP (labels)
 ┌──────────────────────────────────────────────────────────────────────────────────────────────┐
 │ (▨)(▨)(▨)  4 selected │ ▶ Play  ⏵ Play next  ⏭ Play after  ♡ Save │ ✓ Select all │ ⋯      ✕ │
 └──────────────────────────────────────────────────────────────────────────────────────────────┘
    thumbs 28x28, Corners 5, Border 2 FillCardSecondary, overlap Margin L −11 (max 3, deduped by image url/id)
    count  TextEl 12/650 TextPrimary, Key = "selection-count:" + n, Animate = MotionRecipes.TextSwap
    divider 1 x 20 StrokeDividerDefault, Margin L/R 4
    labeled button: Height 32, Gap 6, Padding 9/0/10/0, Corners 4, Icon 14 + Text 12/600 TextSecondary, Interaction.Subtle
    glyph button:   32 x 32, Corners 4, Icon 13 TextSecondary, Interaction.Subtle
    bar Gap 3; spacer Grow 1 pushes ✕ to the trailing edge

 fit 1 — 390 ≤ lane < 760 (glyphs + tooltips)
 ┌──────────────────────────────────────────────┐
 │ (▨)(▨)(▨)  4 selected │ ▶ ⏵ ⏭ ♡ │ ✓ │ ⋯   ✕ │
 └──────────────────────────────────────────────┘

 fit 2 — lane < 390 (essentials; everything else moves INTO the ⋯ menu)
 ┌────────────────────────────┐
 │ 4 selected │ ▶ │ ⋯      ✕ │     count cell MinWidth 66; no thumbs
 └────────────────────────────┘
   standalone (overlay) host: Padding 8/6/8/6 · Corners Radii.Card 8 · Acrylic Tok.AcrylicFlyout
   · Border 1 StrokeFlyoutDefault · Shadow Elevation.Flyout; outer Padding 16/0/16/bottomPadding (default Spacing.XL 20);
   HitTestPassThrough, AlignItems Center, Justify End
   visibility: docked host shows at ≥1 selected row, overlay host at ≥2 (SelectionCommandBar.cs:55)
   PRE-MEASURE: before the first OnBoundsChanged the lane reads 0, and `effective` falls back to **720** — so the very
   first composed frame is fit 1 (glyphs), never fit 0 (SelectionCommandBar.cs:69). A wide bar therefore crosses
   glyphs → labels on its second frame; do not "fix" that by seeding 1400.
   disabled command: IsEnabled false, Focusable false, icon + label recoloured to Tok.TextDisabled (never hidden)
   EVERY command exits the selection after it runs — `ActionButton` calls `_exit()` after Execute (:202) and every
   overflow row is wrapped in `WithExit` (:247, recursing into submenus). Select all is the ONE exception.
```

### W13b — the selection bar's ⋯ overflow (the same menu, re-composed per fit tier)

```
 ┌──────────────────────────────┐   anchored BottomEdgeAlignedRight on the ⋯ button, ToolFx.MenuPopup
 │ ⏵  Play 4 next               │ ← only at fit 2 (they are inline at fit 0/1), then a separator
 │ ⏭  Add 4 to queue            │
 │ ♡  Save                      │
 ├──────────────────────────────┤
 │ ＋ Add to playlist         ▸ │   = Menus.TrackRows(ctx, showGoToAlbum: **false**) verbatim — the SAME rows the
 │ →  Move to playlist        ▸ │     row context menu shows, minus the transport strip and the header
 │ ↗  Share                   ▸ │
 ├──────────────────────────────┤
 │ ✓  Select all                │   always last, after a separator
 └──────────────────────────────┘
   (SelectionCommandBar.ToggleMenu :207-245). A second click on ⋯ closes it (the handle is checked for IsOpen).
```

### W14 — expanded row: the drawer (facts strip + versions)

```
 ┌──┊──┊────┊─────────────────────────────────────────────────────────────────────────────┊──┐
 │ 7┊ ♥┊[▨] ┊Midnight City                                                          4:03 ┊ ⌃│  ← row, bottom corners squared
 └──┊──┊────┊─────────────────────────────────────────────────────────────────────────────┊──┘     chevron swaps to ChevronDown, AccentTextPrimary
      │  Indent = max(0, ArtCentreIndent(set, art) − TrackVersionsPanel.RailOffset **7**)  (DetailTracks.cs:3313)
      │  — the drawer's left padding is backed off by the rail's own x so the RAIL, not the gutter's left edge,
      │    lands on the art centre. ArtCentreIndent = (padX − RowInset) + Num 28 [+ gap + Heart 28] [+ gap + art/2],
      │    derived from the same lane table the width tracks are built from (DetailTracks.cs:641-650).
      │
      │   1.85B         101.5       8B          4:03            ← hero line: StatHero numerals + Caption 12/16/600 tertiary
      │   Plays         BPM         C major     Duration           Gap 20, plus a 20 right margin per stat (= 40 across, 20 down)
      │
      │   Added 28 September 2024 14:02 · Hurry Up, We're Dreaming · Released 18 October 2011 ·
      │   Added by (◉) christos · USUM71100123                       ← prose line, Caption 12/16, middot-joined, Gap 4
      │
      │   electronic · dream pop · 🎬 Music video                    ← genre line, Caption tertiary, Gap 4
      │
      │   Versions and formats                                       ← Eyebrow 12/16/600 +30 tracking, TextTertiary, Margin T 12 B 2
      ├──┐
      │  ├─[ 43 ]  Midnight City                  ▁▂▅▇▅▂▁▂▅▇▅▂  [▶│⌄]   ← self row: Fill FillSubtleSecondary
      │  │         This track · ● 101.5 · 8B · 4:03                       thumb 43x43, title 13.5/540, meta 12
      ├──┤
      └──┴─[  76 x 43  ]  Midnight City (Official Video)   [▶│⌄]        ← 16:9 video thumb with a 22 DIP white play badge
                          Music video · 3:44
   gutter: width 20, rail x = 7 (1 DIP StrokeDividerDefault), stub 9 x 1 at RowH/2; the LAST entry stops the rail at its stub
   version row: Height 43 + 2·4 = 51, Padding X 4, Gap 8, Corners 4, Interaction.Subtle; text column Gap 2; meta row Gap 8
   ORDER is fixed and flat: the self row, then the music video (kind 99), then every alternate audio (kind 98). No
   group headings — the KIND LABEL leads each meta line instead: "This track" / "Music video" / "Alternate audio"
   (detail.versions.*), then [swatch] BPM · key, then the duration (each part only when the wire stated it).
   drawer padding: Left = Indent, Right = TrackRow.PadX 16, Bottom 8, Top 0
   FormatSplitButton: [30 x 28 play | 20 x 28 caret], Corners (4,0,0,4)/(0,4,4,0), Fill FillControlDefault,
                      HoverFill AccentDefault, Border 1 StrokeControlDefault, Icon Play 11 TextPrimary /
                      ChevronDown 9 TextSecondary; both halves Focusable + Role=Button + Hand

 A version row that IS the now-playing item (TrackVersionsPanel.cs:142, 354-367)
   · title flips to AccentTextPrimary
   · the thumb's static white play badge is REPLACED by NowPlayingOverlay (cover-centred, fab = clamp(min(w,h)·0.62,
     22, 28)) — so the drawer's own row answers play/pause exactly like a card does
   · the self row keeps its FillSubtleSecondary plate either way

 The caret's format ladder (FormatSplitButton.LoadAndOpenAsync :84-123), anchored BottomEdgeAlignedRight
   ┌────────────────────────────────┐
   │ ● OGG Vorbis 320    320 kbps   │  RadioItem (E915 bullet) — mutually exclusive, never a toggle column
   │ ○ AAC 256           256 kbps   │  label + THREE spaces + "{n} kbps"; enabled = fmt.AvailableOnDevice
   │ ──────                         │
   │ ○ Use my default quality       │  detail.versions.useDefaultQuality — the way BACK out of an override
   └────────────────────────────────┘
   ZERO formats → the caret does NOTHING at all (an empty menu is a dead end), and a fetch failure is the same
   (catch → Array.Empty). The ladder is resolved LAZILY on first click; after the drawer's own fetch it is a cache hit.
```

### W15 — the reserved (pending) music-video row inside the drawer

```
      ├──┐
      │  ├─[  76 x 43  ]  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁  ← PendingBar 132 x 11, Corners 5.5, FillSubtleSecondary
      │  │                ▁▁▁▁▁▁▁▁▁       ← PendingBar  84 x  9, Corners 4.5
   Same Key ("v:video"), same Height (RowH 51), same 16:9 slot → the expansion PATCHES into the real row
   and the drawer's animated height never moves. Deliberately inert: no play affordance, no format button.
```

### W16 — ArtCard, Rail kind (queue rail / module playables) and Grid kind

```
 Rail (MinHeight 52, Padding 4/2/4/2, Gap 12)
 ┌──────────────────────────────────────────────────────┐
 │ [▨ 40]   Midnight City                         ♡  4:03│      art 40 → FAB clamp(40·0.62, 28, 36) = 28
 │          [E] · 🎬 · M83, Kavinsky                     │      meta row Gap 4: badge · middot · Movie 13 · artist links
 └──────────────────────────────────────────────────────┘      title 14/20/600 (accent when now-playing)
                                                               duration cell Padding X 8, Caption TextSecondary
 Grid (MinHeight 64, Padding 4 all round)
 ┌──────────────────────────────────────────────────────┐
 │ [▨ 48]   Midnight City                          +  ♡  │      "+" AddButton 28 circle, Border 1 StrokeControlDefault,
 │          [E] · M83                                    │          Icon Add 15 TextPrimary, hover 1.07 / press 0.92
 │          1,204,882 plays                              │      plays line (set.Plays) Caption TextTertiary
 └──────────────────────────────────────────────────────┘
 artwork stack: art box, ClipToBounds, Corners 4, ZStack = [Artwork, buffering ? CoverScrim + Spinner
                : NowPlayingOverlay(uri, onPlay, fab, cover:true, art, centered:true)]
 no-artwork variant (settings: hide track artwork): a ControlH 32 square button, Corners 4, Icon Play|Pause 14, Interaction.Subtle
 select skin (ArtCardSelectSkin): MinHeight 54 (rail) / 66 (grid), Margin 4/2/4/2 (rail) or 0/1/0/1 (grid),
   Corners 4, Fill FillSubtleSecondary when selected, HoverFill FillSubtleSecondary, PressedFill FillSubtleTertiary,
   Border 1 transparent → StrokeCardDefault on hover, PressScale 0.98, disabled Opacity = ItemContainer.DisabledOpacity
```

### W17 — eager row (Home "Top tracks"), 48 DIP @ ~420 DIP module width

```
 ┌─┬────────────────────────────────────────────────────────────────────┐
 │ │ 1  ♡ [▨]  Midnight City                  1.85B   4:03   [top] ⋯   │  Margin L/R 8, Corners 4, MinHeight 48
 │ │           M83                                                      │  Fill transparent (zebra: RowZebra)
 └─┴────────────────────────────────────────────────────────────────────┘  HoverFill RowHover / PressedFill RowPressed
   Border 1: transparent at rest (zebra rows: StrokeCardDefault), StrokeCardDefault on hover. NO PressScale.
   Whole row is Role=Button, OnClick = onPlay (single click plays — no multi-select on preview lists).
   OnHoverMove/OnPointerExit write the row hover signal → PointerBit for HoverOpacity AND the EQ pause.
```

### W18 — Artist "Popular" chart row (Modern 56 / Classic 48)

```
 Modern, cellW ≥ 340 (unstacked)                       Modern, 200 ≤ cellW < 340 (stacked)
 ┌──────────────────────────────────────────┐          ┌────────────────────────────────┐
 │ 1  [▨ 44]  Sailor Song                ♡ 3:12│        │ 1 [▨ 44] Sailor Song        ♡ │
 │            [E] · 🎬 · feat. X +2 · 1.8B │           │          [E] · feat. X +2     │
 └──────────────────────────────────────────┘          │          1,850,220,114 plays  │
   row: MinHeight 56, Gap 8, Padding X 8,              └────────────────────────────────┘
        Corners 6, Fill transparent,                     3 lines × 12/14 + 2 × Gap 1 ≈ 53 < 56
        HoverFill RowHover, PressedFill RowPressed,
        Border 1 transparent → StrokeCardDefault hover,  Classic: MinHeight 48, art 40, Corners 0,
        PressScale 0.98 (a chart row is NOT full width)  Padding X 4, no border, 1 DIP hairline at the bottom
   # box 24x24 hosting TrackRow.NumberCell; single click SELECTS, double click plays, the # hover play still plays.
   plays are ALWAYS the compact format here (TrackRow.PlaysLabel) with the exact count in a ToolTip.
```

### W19 — track context menu, single track (right-click / Menu key / long-press / the "…")

```
 ┌────────────────────────────────────────────┐
 │ [▨38] Midnight City                        │  header: art 38x38 r6 (19 if circular), title, subtitle
 │       M83 · Hurry Up, We're Dreaming        │  subtitle = artists · album, HTML-flattened
 ├────────────────────────────────────────────┤
 │  ▶      ⏵        ⏭          ♡              │  PRIMARY STRIP (labeled command bar)
 │ Play  Play next Play after  Save           │  = TrackActions.Play / PlayNext / AddToQueue / ToggleLike
 ├────────────────────────────────────────────┤  ToggleLike checked → "Saved" + accent-filled HeartFill
 │ ＋ Add to playlist                       ▸ │  New playlist · ≤10 MRU-ordered editable playlists · ─ · More playlists…
 │ →  Move to playlist                      ▸ │  ONLY in an editable playlist host (else the row is absent)
 │ ▤  Go to album        (or ⌁ Go to podcast) │  single target; absent on album pages (showGoToAlbum: false)
 │ ☺  Go to artist       (or Go to artists ▸) │  1 navigable artist → row; ≥2 → cascade, one row per artist
 │ ↗  Share                                 ▸ │  Copy link · [Copy Spotify URI · Open in Spotify Web] (single only)
 │ ▤  View credits                            │  single track with a primary artist URI
 │ ⌁  Go to song radio                        │  single spotify:track:*
 │ 🎬 Video                                 ▸ │  Attach… / Replace… + Locate… + Show in Explorer + ─ + Remove video
 ├────────────────────────────────────────────┤
 │ (surface extras: Move up / Move down —      │  queue rows;  "Track details" — the ultra-compact-tier drawer fallback
 │  Track details)                             │
 ├────────────────────────────────────────────┤
 │ ✕  Remove from this playlist                │  destructive LAST, behind its own separator
 │ ✕  Remove from queue                        │
 └────────────────────────────────────────────┘
```

### W20 — track context menu, multi-selection (right-click INSIDE a ≥2 selection)

```
 ┌────────────────────────────────────────────┐
 │ [▨38] 4 songs selected                     │  header title = "{n} songs selected"
 │       Midnight City  +3 more               │  subtitle = first title + "  +N more"
 ├────────────────────────────────────────────┤
 │  ▶      ⏵ Play 4 next   ⏭ Add 4 to queue  ♡ Save  │
 ├────────────────────────────────────────────┤
 │ ＋ Add to playlist                       ▸ │
 │ →  Move to playlist                      ▸ │
 │ ↗  Share  (→ Copy links (4) only)        ▸ │   the URI / web-player variants are single-target and drop out
 ├────────────────────────────────────────────┤
 │ ✕  Remove from this playlist                │
 └────────────────────────────────────────────┘
   Selection semantics: right-click INSIDE a ≥2 selection acts on all of it; outside it collapses to the clicked row
   (TrackTargetResolver, via TrackContextMenu.Build :26). The selection mutation happens AT OPEN, inside the factory.
```

### W21 — drag in progress: the chip and the insertion gap

```
 the chip (cursor-anchored, HitTestVisible false, MaxWidth 280)
   ┌────────────────────────────────┐  ← two 4 DIP-offset stack cards behind it when Count ≥ 2
  ┌┤ [▨ 40]  Midnight City       (4)│  ← count badge at the card's top-trailing corner (Padding 0/−6/−6/0)
 ┌┤│          M83                   │     title 13/600 TextPrimary, subtitle 12 TextSecondary
 └┤│          Add 4 songs           │     caption 12/16 TextSecondary (resting verb, or the live target's caption)
  └└────────────────────────────────┘     Fill FillSolidTertiary (OPAQUE) · Border 1 StrokeSurfaceDefault
     Padding 8/8/12/8 · Gap 10 · Corners Radii.Overlay 8 · Shadow Elevation.Flyout
     pickup FLASH once per gesture: Rotation 4° (DragChip.TiltDeg) + Scale 1.02 (PickupScale) → flat in
     150 ms (PickupFlashMs); NOT a resting pose.  Enter: Sx/Sy 0.92, Opacity 0
   source row stays in its slot at Opacity 0.4 (Drag.SourceDimOpacity) — never a lifted full-width snapshot

 NO COVER IN HAND (a tab drag, a folder, a cover-less playlist): the art box becomes a 40 DIP kind-GLYPH tile —
   Fill FillSubtleSecondary, Corners Radii.Control, the sidebar's own marks (WaveeResourceDrag.GlyphFor :366):
   Playlist → MusicNote · Album → Album · Artist → Contact · Show/Episode → RadioTower · Folder → Folder · Route → Home
 LIKED SONGS drags wearing its OWN cover: a pre-built `LikedChipArt` element (LikedSongsArtwork.Dynamic at
   DragChip.ArtSize) held in a static field, because Chip() runs inside the 0-alloc frame region (:352-362).
 A SECOND chip kind rides the same resolver: the sidebar customizer's palette chip (SidebarEditPlan.SectionDragKind →
   label + CzGlyphs mark + "Drop here"). One resolver, because DragPreviewLayer.Of takes exactly one (:307-318).
 SPRING-LOAD: a drag resting 500 ms (WaveeResourceDrag.SpringLoadMs) over a container opens it — a collapsed sidebar
   folder expands, a tab activates. Long enough that merely travelling ACROSS a folder never opens it.

 the insertion gap (a playlist track list accepting the drop)
 ──────────────────────────────────────────────────────  ← insertion line (framework-owned)
 ┌──────────────────────────────────────────────────┐
 │ [▨32]  Midnight City                             │   card: Fill FillSolidSecondary, Border 1 AccentDefault,
 │        M83                                       │         Shadow Elevation.Card, Corners 4,
 └──────────────────────────────────────────────────┘         Margin L/R 8 (RowInset), Padding L/R 8 (PadX−RowInset),
 ┌──────────────────────────────────────────────────┐         Height = the list's own rowH, HitTestVisible false
 │ [▨32]  Wait                            ( +37 )   │   title 14/600 TextPrimary, subtitle 12 TextSecondary
 └──────────────────────────────────────────────────┘   "+N" pill: Padding 8/2/8/2, Radii.Pill, Fill AccentSubtle,
 ──────────────────────────────────────────────────────      text 12/600 AccentTextPrimary  (cap = 3 cards)
   a SAME-LIST reorder never dims the app (SpotlightWhen = !IsSameListDrop); a cross-list deposit keeps the scrim
   NO TRACK SNAPSHOT (an album/playlist card dragged over a list — the resolver runs only after the drop): ONE card,
     title = payload.Name, subtitle empty, art = a 32 DIP FillSubtleSecondary tile with Icon MusicNote 16 TextSecondary
     (PlaylistInsertionPreview.cs:42-47). `total` falls back to 1, so no "+N" pill.
   hide-artwork: `showArtwork:false` drops the art child entirely — the card is title + subtitle only (:48-50).
```

### W22 — drop refused

```
   ┌────────────────────────────────────┐
   │ [▨ 40]  Midnight City          ⃠   │   NotAllowedGlyph E733, 14, Tok.SystemFillCritical
   │          M83                        │
   │          Clear sorting to reorder   │   caption escalates to Tok.SystemFillCritical
   └────────────────────────────────────┘
   reasons (WaveeDragRules.cs:87-105 → DetailTracks.cs:1257-1265, loc keys drag.*):
     NotEditable → drag.cantEditPlaylist "Can't edit this playlist"
     Loading     → drag.stillLoading     "Still loading…"
     NoTracks    → drag.cantAddArtist    "Can't add an artist"   (artist payload) else drag.nothingToAdd "Nothing to add"
     Sorted      → drag.clearSortingToReorder                    Filtered → drag.clearFiltersToReorder
     Syncing     → drag.stillSyncing     "Still syncing — try again in a moment"
   An album page / show page that is not a playlist is TRANSPARENT instead of refusing (no accusation for a pass-through).
```

### W23 — touch swipe belt (touch-only; armed only after a real contact this session)

```
 swipe RIGHT (leading, ToggleLike)            swipe LEFT (trailing, AddToQueue / RemoveFromQueue)
 ┌──────────┬─────────────────────────┐       ┌──────────────────────┬──────────┐
 │    ♡     │ 1  ♡ [▨] Midnight City  │       │ 1 ♡ [▨] Midnight City│    ⏭     │
 │   Save   │                         │       │                      │Play after│
 └──────────┴─────────────────────────┘       └──────────────────────┴──────────┘
   SwipeMode.Execute both sides · corners 6 · the row's own `Margin` is lifted OFF the wrapped child and handed to
   the SwipeControl instead (`LiftMargin`, RowSwipe.cs:60-62) — kept on the child, the wrapped body grid ends up
   wider than its fixed column header. A side whose action is absent or `EnabledFor == false` is DROPPED; with
   neither side landing, `Wrap` returns the row unchanged (no wrapper at all).
   pre-threshold plate = neutral tertiary; post-threshold = AccentDefault + on-accent foreground + icon pop
   destructive verbs (Remove from queue): Color SystemFillCriticalBackground, Foreground SystemFillCritical
   gate: TouchInput.SwipeArmed = a digitizer EXISTS (GetSystemMetrics(SM_MAXIMUMTOUCHES 95) > 0, cached once) AND a
         touch contact has been seen this session (InputDispatcher.TouchObserved) — RowSwipe.cs:25-42. The probe fails
         SAFE: an exception assumes touch is present. Reading the latch SUBSCRIBES the calling row render, so the first
         finger-down anywhere re-renders the lists once and every row grows its wrapper from then on.
   bound rows: WrapBound re-reads icon / label / enabled / invoke through a `Func<ActionContext?>` every time, so a
         recycled slot's swipe acts on the row now under the finger (RowSwipe.cs:116-135); `resetKey = scope.Index`
         snap-closes an open row when the slot recycles. Eager keyed rows pass null (each gets a fresh control).
   NOTE: the belt is currently mounted on the EAGER lists (queue / previews) only — the virtualized detail rows have
         it FLAGGED OFF pending three on-device checks (DetailTracks.cs:2695-2704). 0.3 should ship it on both.
```

### W24 — album drawer row (artist page discography expander), 28 DIP content in a 32 DIP slot @ ~520

```
 ┌───┊──┊──────────────────────────────────────────────────┊─────┊───┐
 │ 1 ┊ ♡┊Midnight City                                     ┊ 4:03┊ ⋯ │
 └───┊──┊──────────────────────────────────────────────────┊─────┊───┘
   26   28                     star                          44    32
   title 13/600 (not the row ramp's 14) — the drawer's own tighter register (AlbumExpand.cs:325-333)
   no art, no subline, no video/expand lanes; "…" raises ClickRequestsContext → the selection-aware track menu
   left accent SelectionPill 3 x 16, r 1.5, Margin L 2, opacity bound to the drawer's own SelectionModel
```

### W25 — Recents rows

```
 single-track arm (64 DIP)                          group drawer child (40 DIP)
 ┌────────────────────────────────────────┐         ┌──────────────────────────────────────────┐
 │ 1  ♡ [▨32]  Midnight City       4:03 ⋯ │         │ 1 ♡ [▨32] Midnight City       14:02   ⋯ │
 └────────────────────────────────────────┘         └──────────────────────────────────────────┘
   [36, 28, 32, *, 52, 40]                            [30, 28, 32, *, 52, 112]
                                                       trailing lane = Caption(playedAt, 60 DIP, tertiary) + gap 12 + "…"
   both: Role=Button, Cursor Hand, HoverFill FillSubtleSecondary, Corners 4, Draggable when uri non-empty,
         WithContextMenu(TrackContextMenu.BuildSingle)
```

### W26 — empty / offline / unavailable

```
 unavailable track (server ruled on it; NOT the not-yet-out case)
   · row renders normally; the drawer's genre line carries "ⓘ Unavailable" (TrackFactsStrip.FlagRun, Icons.Info 11)
 name-less album ref (known uri, empty name)
   · album lane renders Dash "—" in TextTertiary, and the span stays CLICKABLE whenever a uri exists (TrackRow.cs:870-888)
 no Mutations source connected (offline / a backend without collection writes)
   · SaveButton / FollowButton / PreSaveButton render an EMPTY BoxEl — the affordance is capability-gated, never disabled
     (SaveButton.cs:37, 89, 143). The per-row ♥ still paints (its onLike is simply null → no cursor, no click).
 empty column set (every optional lane relieved away, tier 6)
   ┌──┊──────────────────────────────┊──────┐
   │ 1┊Midnight City                 ┊  4:03│      # 28 · Title star (floor 120) · duration 52; padX 8, colGap 8
   │  ┊M83 · Hurry Up, We're Dreaming┊      │      Album folds into the subline; "…" only via the row context menu
   └──┊──────────────────────────────┊──────┘
 drawer: the expansion FETCH failed (offline, a rejected envelope)
   · `catch { result = TrackExpansion.Empty }` (TrackVersionsPanel.cs:160) → the facts strip + the eyebrow + the SELF
     row alone. No error text, no retry: the drawer's contract is "everything the TRACK carries", and the track is
     already in hand. A reserved video row that the empty result contradicts simply never mints (Facts.HasVideo false).
 drawer: the track states NOTHING at all (a shimmer row, an empty slot)
   · `TrackFactsStrip.Build` returns a bare `BoxEl` when `TrackExpandedFacts.For` yields 0 facts (:61) — no hero line,
     no prose line, no genre line, and the "Versions and formats" eyebrow then heads the self row alone.
 pre-release pill with no resolvable prerelease entity (kind 138 answers null, or the release already dropped)
   · `PreSaveButton` renders an EMPTY BoxEl — it is never a disabled pill and never a spinner (SaveButton.cs:90).
 album drawer (artist discography) that settles READY but EMPTY (offline / a cold stub)
   · not a 0-row void: an `EmptyNote(retry)` row with a Retry that re-runs the loader stale-while-revalidate
     (ArtistPage.AlbumExpand.cs:344-352). Sized by AlbumDrawerVerdict's ReadyEmpty branch (2 rows) — see ch 08.
 Home "Top tracks" while the artist overview is pending
   · FIVE skeleton rows at `TrackRow.RowHeight` 48, each a `Body(" ")` box `.Skeletonized(true)` — the row SHAPE, never
     a spinner, so the expand never flashes empty and never jumps when the overview lands (HomeModules.Artists.cs:330).
```

### W27 — "View credits" modal (TrackCreditsDialog)

```
 ContentDialog: Title = the TRACK title · PrimaryText "" (display-only) · CloseText = auth.close · DefaultButton Close
 ┌──────────────────────────────────────────────┐  body MinWidth 360 · MaxWidth 440
 │ PERFORMERS                                   │  ← Eyebrow (12/16/600 +30 tracking) TextTertiary, the SERVER's own
 │ Anthony Gonzalez              Lead vocals    │    casing ("Performers" / "Songwriters"), never re-cased
 │ Kavinsky                      Guitar         │  ← name 13/650: AccentTextPrimary + clickable when the credit is
 │ SONGWRITERS                                  │    Linkable AND carries an artist uri, else TextPrimary, plain
 │ Anthony Gonzalez              Writer         │  ← role 11 TextTertiary, trailing; blank role → an empty box
 │ Source: Universal Music, MLC                 │  ← 11 TextTertiary, Wrap, MaxLines 2 (player.creditsSource)
 └──────────────────────────────────────────────┘  ScrollView, MaxHeight 420 · column Gap 8 · row Gap 8

 loading (EITHER read still Pending — 186 answering late must not flash an empty drawer first)
   ┌──────────────────┐   four FillSubtleSecondary bars, Corners 4, column Gap 10, Padding 0/8/0/8:
   │ ▁▁▁▁▁            │     12 x 120  ·  14 x 220  ·  14 x 190  ·  14 x 210
   │ ▁▁▁▁▁▁▁▁▁        │
   └──────────────────┘
 empty (both reads settled, neither has credits) → one line: menu.noCredits "No credits available", 13 TextSecondary
   sources: the uncapped kind-186 drawer FIRST (keyed on the track), the NPV enrichment's capped ten rows as the
   fallback for a track the wire has no 186 for, and for offline (TrackCreditsDialog.cs:43-49)
```

### W28 — hide track artwork (Settings → Appearance), across every surface in this chapter

```
 detail row      art COLUMN removed from the ColumnSet AND the TrackSize[] (IdentityColumns.Thumb false)
                 → the left cluster is # · ♥ · Title; the header loses the same track by construction
 eager rows      TrackColsNoArt / SingleRowColsNoArt / ChildColsNoArt — the same removal, statically paired
 ArtCard         the art stack is replaced by a ControlH **32** square button, Corners 4, Icon Play|Pause **14**
                 TextPrimary, Interaction.Subtle, Role=Button, Cursor Hand, Focusable — the art WAS the play
                 affordance (the cover FAB), so hiding it has to hand the verb somewhere (TrackRow.cs:482-498)
 ArtistPopular   rowChildren drops to 3 (# · mid · trail); the # cell keeps its own hover transport
 insertion card  the art child is not emitted; the card is title + subtitle
 drag chip       unaffected — the chip is a drag VISUAL, not a row, and it keeps its 40 DIP art or glyph tile
 drawer          unaffected — the version thumbs are the SUBJECT of the drawer, not row decoration
```

---

## 3. Tokens

> **Source note.** `TrackLane` is **not** a file — it is a second static class at the bottom of
> `Features/Detail/DetailTrackTableRules.cs` (lines 227-279). Rows below citing "TrackLane.cs:NNN" mean
> `DetailTrackTableRules.cs:NNN`; the line numbers are correct, the filename was not.

| Element | Size (DIP) | Padding / gap | Radius | Type style | Colour / brush | Material / elevation | Source |
|---|---|---|---|---|---|---|---|
| Row height, density 0/1/2/3 | 40 / 48 / 56 / 64 | — | — | — | — | — | TrackRow.cs:118 |
| Row height, Classic 0/1/2/3 | 36 / 40 / 44 / 48 | — | — | — | — | — | DetailTrackTableRules.cs:45-47 |
| Header height | 36 (Classic 32) | — | — | — | — | — | TrackRow.cs:103, DetailTrackTableRules.cs:27 |
| Art, density 0/1/2/3 | 32 / 32 / 40 / 48 (Classic 32/32/32/40) | — | `Radii.Control` 4 | — | — | `decodePx = art × 2` | DetailTrackTableRules.cs:54-56, TrackRow.cs:246 |
| Default art (non-detail callers) | `WaveeSize.Thumb32` = 32 | — | 4 | — | — | — | TrackRow.cs:110 |
| Row skin (Modern) | MinHeight `rowH` | Margin L/R `RowInset` 8 | 6 (open drawer: 6,6,0,0) | — | rest transparent / `WaveeColors.RowZebra` odd | Border 1 `Tok.StrokeCardDefault` (odd rows) | DetailTracks.cs:3496-3532 |
| Row skin hover / press | — | — | — | — | `RowHover` / `RowPressed` (zebra: `RowHoverZebra` / `RowPressedZebra`) | Border → `StrokeCardDefault` | DetailTracks.cs:3514-3518, WaveeTokens.cs:232-242 |
| Row hover ink (dark / light) | — | — | — | — | `#0FFFFFFF` / `#0D000000` | — | PaletteBuilder.cs:160, 118 |
| Row pressed ink (dark / light) | — | — | — | — | `#0AFFFFFF` / `#12000000` | — | PaletteBuilder.cs:162, 119 |
| Row zebra ink (dark / light) | — | — | — | — | `Tok.FillSubtleTertiary` `#0AFFFFFF` / `#08000000` | — | WaveeTokens.cs:230, PaletteBuilder.cs:117, 303 |
| Eager row skin | MinHeight `rowH` | Margin L/R 8 | `Radii.ControlAll` **4** | — | same ladder as above | Border 1 | TrackRow.cs:370-386 |
| Grid | `RowHeight = rowH` | Padding L/R `PadXFor(tier) − RowInset` (Classic: `PadXFor`), ColGap `ColGapFor(tier)` | — | — | — | — | TrackRow.cs:329-337 |
| `PadXFor(tier)` | 16 (≤3) / 12 (4-5) / 8 (6) | — | — | — | — | — | TrackRow.cs:129 |
| `ColGapFor(tier)` | 12 (≤4) / 8 (5-6) | — | — | — | — | — | TrackRow.cs:130 |
| `#` lane | 28 | — | — | Caption 12/16 | `Tok.TextTertiary` | — | TrackLane.cs (DetailTrackTableRules.cs:235), TrackRow.cs:1073 |
| `#` header caret slot | 9 each side, symmetric | — | — | — | — | — | DetailTrackTableRules.cs:239 |
| transport button | 24 × 24 | — | — | Icon 12 | `TextPrimary` (accent when now-playing) | `PressScale` 0.92 | TrackRow.cs:1086-1091 |
| equalizer bar | 2.5 × 13 | Gap 2 | 1.25 | — | `PageAccent.Ink` ?? `AccentTextPrimary` | `TransformOriginY 1` | Equalizer.cs:198-203, 114 |
| top-track star | Icon 11 | — | — | — | `AccentTextPrimary` | — | TrackRow.cs:1080 |
| Classic now-playing mark | Icon `Volume` 13 | — | — | — | `AccentTextPrimary` | — | TrackRow.cs:1076 |
| chart glyph | 8 / lineHeight 12 | Gap 2 from the number | — | weight 700 | `SystemFillSuccess` (▲/NEW) / `SystemFillCritical` (▼) | — | TrackRow.cs:1113-1119 |
| spinner | 16 | — | — | — | `AccentTextPrimary` | `ProgressRing.Indeterminate` | TrackRow.cs:1122 |
| ♥ lane / button | 28 lane, 28 × 28 button | — | circle (14) | Icon 14 | saved `AccentTextPrimary` (Classic `TextPrimary`) / unsaved `TextTertiary` (Classic `TextSecondary`) | `Interaction.Subtle` | TrackRow.cs:114, 935-957 |
| Title | star 1.0, floor 120 | column Gap `Spacing.XXS` 2 | — | BodyStrong 14/20/600 | `TextPrimary` → `AccentTextPrimary` now-playing | — | TrackLane.cs:269-274, WaveeType.cs:23 |
| Metadata subline | — | Gap 4 | — | Caption 12/16 | `TextSecondary` | — | TrackRow.cs:846-856 |
| Episode eyebrow | — | Shrink 0 | — | Caption 12/16/600 + 30 tracking | `TextTertiary` | — | TrackRow.cs:840-843, WaveeType.cs:54 |
| Explicit badge | MinWidth 14, Height 14 | Padding X `Spacing.XXS` 2 | **2** (below the ramp, deliberate) | "E" **10**/12/600 (below the ramp, deliberate) | Border 1 `TextTertiary`, Opacity 0.6 | — | TrackRow.cs:745-751 |
| Classic explicit word-mark | Height 14 | Padding X 2 | 2 | 9/12/600 | `TextTertiary` (accent + Opacity 1 when now-playing) | — | TrackRow.cs:755-768; loc `detail.badge.explicit` |
| Artist lane (Classic) | star 0.75, floor 90 | — | — | 14/20 (Modern 12/16) | `secondaryInk` | — | TrackRow.cs:278-280, TrackLane.cs:275-276 |
| Album lane | star 0.75, floor 90 | — | — | 12/16 (Classic 14/20) | `TextSecondary`; name-less → `Dash` in `TextTertiary` | — | TrackRow.cs:281-283, 870-888 |
| Added-by lane | 132 | Gap `Spacing.S` 8 | — | Caption 12/16 | `TextSecondary`; avatar `PersonPicture` 24 | — | TrackLane.cs:244-245, TrackRow.cs:891-908 |
| Date lane | 88 | — | — | Caption 12/16 | `TextSecondary` | — | TrackLane.cs:248-249 |
| Plays lane | 52 | — | — | Caption 12/16 | `TextTertiary` | — | TrackLane.cs:252-253, TrackRow.cs:296-298 |
| Tempo lane | 80 | Gap `Spacing.XS` 4 | swatch 1.5 | Caption 12/16 | swatch `WaveePalette.DataDotInk(argb, theme)` @ Opacity 0.85; BPM `TextSecondary`; key `TextTertiary` | — | TrackLane.cs:254-255, TrackRow.cs:584-611 |
| Camelot swatch | 6 × 6 | — | 1.5 | — | see above | — | TrackRow.cs:598-599 |
| Duration lane | 52 | — | — | Caption 12/16 | `TextSecondary` (not-yet-out: `TextTertiary`) | — | TrackLane.cs:256-257, TrackRow.cs:312 |
| Video lane | 28 | — | — | Icon `Movie` 13 | `TextTertiary` | — | TrackLane.cs:258-259, TrackRow.cs:1022 |
| Actions lane | 40 | — | — | — | — | — | TrackLane.cs:260-261 |
| "…" button | 28 × 28 | — | circle 14 (Classic 0) | Icon `More` 16 | `TextSecondary` | `HoverScale` 1.07 / `PressScale` 0.92 (Classic 1/1), `Interaction.Subtle` | TrackRow.cs:966-993 |
| "…" rest opacity | — | — | — | — | `MoreRestOpacity` **0.45** (Classic 0) | `HoverOpacity` 1 | TrackRow.cs:989-997 |
| "+" add button | 28 × 28 | — | circle 14 | Icon `Add` 15 | `TextPrimary`, Border 1 `StrokeControlDefault` | hover 1.07 / press 0.92 | TrackRow.cs:1001-1009 |
| Expand chevron | `Spacing.XXL` 24 × 24 (lane 26) | — | 4 | Icon `ChevronRight`/`ChevronDown` 12 | closed `TextSecondary` / open `AccentTextPrimary` | `HoverFill Tok.FillControlSecondary` | TrackRow.cs:625-641, TrackLane.cs:262-263 |
| Selection pill | 3 × 16 | Margin L 2 | 1.5 | — | page accent (`_rowAccent`) | `PressScale` 0.625 | DetailTracks.cs:3572-3579 |
| Multi-select check lane | 4 + 20 + 4 = 28 | Padding L 4, R 4 | — | — | — | 333 ms tween, Dx −28 | SelectorVisualsBound.cs:72-94 |
| Classic row divider | height 1 | Margin L/R `PadXFor(tier)` | — | — | `StrokeDividerDefault` | — | DetailTracks.cs:3581-3589 |
| Drawer gutter / rail / stub | 20 / 1 wide at x 7 / 9 × 1 | — | — | — | `Tok.StrokeDividerDefault` (NOT `StrokeCardDefault`) | — | TrackVersionsPanel.cs:52-56, 187-208 |
| Drawer version row | Height 43 + 2×4 = 51 | Padding X `Spacing.XS` 4, Gap `Spacing.S` 8 | 4 | title 13.5/540, meta 12 | self row `FillSubtleSecondary`, others transparent | `Interaction.Subtle` | TrackVersionsPanel.cs:59, 292-325 |
| Drawer video thumb | 76 × 43 | — | 4 | — | scrim `#47000000` (black α 0.28) + 22 DIP white α 0.92 badge, Icon `Play` 11 `#111111` | — | TrackVersionsPanel.cs:49, 377-391 |
| Drawer waveform | 64 bars × 2 px, 2 px silence floor | — | — | — | `TextTertiary` | `FluentGpu.Controls.Waveform` | TrackVersionsPanel.cs:333-344 |
| Facts hero stat | — | row Gap `Spacing.XL` 20 **+** per-stat right Margin 20 | — | `WaveeType.StatHero` 28/36 + Caption 12/16/600 | value inherit, label `TextTertiary`; pending Opacity 0.6 | — | TrackFactsStrip.cs:51, 132-158 |
| Facts prose / genre lines | — | Gap `Spacing.XS` 4, block Gap `Spacing.M` 12 | — | Caption 12/16 | prose `TextSecondary` (lead-ins `TextTertiary`), genres `TextTertiary` | — | TrackFactsStrip.cs:100-121 |
| Facts strip padding | — | 0 / 4 / 0 / 8 | — | — | — | — | TrackFactsStrip.cs:120 |
| `FormatSplitButton` | 30 × 28 + 20 × 28 | — | (4,0,0,4) / (0,4,4,0) | Icon `Play` 11 / `ChevronDown` 9 | `FillControlDefault` → `HoverFill AccentDefault`, Border 1 `StrokeControlDefault` | — | FormatSplitButton.cs:52-70 |
| `SaveButton` | box 40 (param), glyph 16 (param) | — | circle | — | saved: `Accent()` ?? `PageAccent.Ink` ?? `AccentTextPrimary`; unsaved `TextSecondary` | hover 1.07 / press 0.92, `Interaction.Subtle` | SaveButton.cs:27-48 |
| `PreSaveButton` pill | — | Padding 12/5/12/5, Gap 6 | 4 | 12/600 (`TextSize`) | unsaved: accent fill + `ColorContrast.PickContrast`; saved: transparent + 1 DIP accent border, accent ink | `HoverFill FillSubtleSecondary` (saved) | SaveButton.cs:97-115 |
| `FollowButton` | Height `WaveeCta.PillHeight` | Padding X `Spacing.M` 12, Gap 4 | `Radii.FullAll` | Body 600 | following: accent border + `AccentTextPrimary`; else `StrokeControlDefault` | hover 1.04 / press 0.96 | SaveButton.cs:146-164 |
| Selection bar (bar) | button 32 tall / 32 square | Gap 3, divider 1 × 20 Margin X 4 | 4 | count 12/650, labels 12/600, icons 14 / 13 | `TextSecondary`, `TextDisabled` when off | `Interaction.Subtle` | SelectionCommandBar.cs:184-393 |
| Selection bar thumbs | 28 × 28, ≤3 | overlap Margin L −11 | 5 | — | Border 2 `Tok.FillCardSecondary` | — | SelectionCommandBar.cs:308-322 |
| Selection bar (overlay) | — | Padding 8/6/8/6; outer 16/0/16/20 | `Radii.Card` 8 | — | `Acrylic = Tok.AcrylicFlyout`, Border 1 `StrokeFlyoutDefault` | `Elevation.Flyout` (blur 16, y 8, `#42000000` dark / `#24000000` light) | SelectionCommandBar.cs:100-113, Elevation.cs:33-35 |
| Drag chip | art 40, MaxWidth 280 | Padding 8/8/12/8, Gap 10 | `Radii.Overlay` 8 | title 13/600, subtitle 12, caption 12/16 | `Tok.FillSolidTertiary` (OPAQUE), Border 1 `StrokeSurfaceDefault` | `Elevation.Flyout` | DragChip.cs:68-165 |
| Drag stack card | MinWidth 96, MinHeight 56 | offset 4 / 8 | 8 | — | `FillSolidSecondary`, Opacity 0.85 | — | DragChip.cs:208-215 |
| Insertion preview card | Height = list `rowH`, art 32 | Margin L/R 8, Padding L/R 8, Gap 12 | 4 | title 14/600, subtitle 12 | `FillSolidSecondary`, Border 1 `Tok.AccentDefault` | `Elevation.Card` (dark blur 8 y 2 `#33000000`) | PlaylistInsertionPreview.cs:67-78 |
| Insertion "+N" pill | — | Padding 8/2/8/2 | `Radii.PillAll` 16 | 12/600 | `Tok.AccentSubtle` fill, `AccentTextPrimary` ink | — | PlaylistInsertionPreview.cs:61-66 |
| Swipe belt | — | — | 6 | — | neutral tertiary → `AccentDefault`; destructive `SystemFillCriticalBackground`/`SystemFillCritical` | — | RowSwipe.cs:79-105, 137-146 |
| Search-highlight pill | — | Padding 3/1/3/1 | `Radii.Control` 4 | inherits the row's size/weight | `Tok.AccentSelectedTextBackground` on `Tok.TextOnAccentSelectedText` | — | SearchHighlight.cs:42-54 |
| **ArtCard, no-artwork play button** | `WaveeSize.ControlH` 32 × 32 | — | 4 | Icon `Play`/`Pause` 14 | `TextPrimary` | `Interaction.Subtle` | TrackRow.cs:484-497 |
| **ArtCard rail duration cell** | — | Padding X `Spacing.S` 8 | — | Caption 12/16 | `TextSecondary` | — | TrackRow.cs:450-456 |
| **ArtCard plays line** (`set.Plays`) | — | — | — | Caption 12/16, `{n:N0} plays` (the FULL count — the row lane's compact form is not used here) | `TextTertiary` | — | TrackRow.cs:443-444 |
| **ArtCard cover FAB** | `clamp(art × 0.62, 28, 36)` | — | — | — | — | `NowPlayingOverlay`, cover-centred | TrackRow.cs:414, 478 |
| **Home "in your top N" badge** | — | Padding 8/4/8/4 | `Radii.FullAll` (999, clamped to half the 24-DIP badge ⇒ 12) | `Eyebrow` 12/16/600 +30 tracking | `Tok.SystemFillSuccessBackground` fill, `SystemFillSuccess` ink (a SEMANTIC colour, outside the accent budget) | — | HomeModules.Artists.cs:344-357 |
| **Drag chip glyph tile** (no cover) | 40 × 40 | — | `Radii.Control` 4 | Icon (kind mark) | `Tok.FillSubtleSecondary` | — | DragChip.cs:219-222, WaveeResourceDrag.cs:366 |
| **Insertion card placeholder art** (no track) | 32 × 32 | — | 4 | Icon `MusicNote` 16 | `FillSubtleSecondary` / `TextSecondary` | — | PlaylistInsertionPreview.cs:42-47 |
| **Drawer Added-by chip avatar** | `PersonPicture` **20** (the row lane's is 24) | Gap `Spacing.XXS` 2 | — | Caption 12/16 | lead-in `TextTertiary`, name `TextSecondary`; **no OnClick** — no `user:` route exists | — | TrackFactsStrip.cs:199-217 |
| **Drawer flag glyphs** | Icon 11 | Gap `Spacing.XXS` 2 | — | — | `TextTertiary` | — | TrackFactsStrip.cs:266-295: Video→`Movie`, LocalFile→`Folder`, Unavailable→`Info`; Explicit gets **none** |
| **Drawer version now-playing** | thumb overlay fab `clamp(min(w,h)×0.62, 22, 28)` | — | — | title 13.5/540 | `AccentTextPrimary` title; `NowPlayingOverlay` replaces the static badge | — | TrackVersionsPanel.cs:266, 354-367 |
| **ArtistPopular mid / trail** | — | mid column Gap 1 (Classic `Spacing.XS` 4), sub-line Gap 5, trail Gap 6, row Gap `Spacing.S` 8 | 6 (Classic 0) | title 14/600 · sub 12 · duration **13** | duration `TextSecondary` (accent when Classic + now-playing) | `PressScale` `ScaleSubtle.Press` 0.98 | ArtistPopular.cs:430-465, 490-500 |
| **Credits dialog** | MinWidth 360 / MaxWidth 440, scroll MaxHeight 420 | column Gap 8, row Gap 8; loading Gap 10, Padding 0/8/0/8 | loading bars 4 | group `Eyebrow`; name 13/650; role 11; source 11 | name `AccentTextPrimary` (linkable) / `TextPrimary`; role + source `TextTertiary`; bars `FillSubtleSecondary` | — | TrackCreditsDialog.cs:51, 71-122 |
| **PreSave glyph** | `TextSize + 1` (13 at the default) | — | — | — | saved: accent; unsaved: `ColorContrast.PickContrast(fill)` | — | SaveButton.cs:110 |
| **FollowButton, tinted variant** | — | — | — | — | border `fg` α 0.42, hover `fg` α 0.12, pressed `fg` α 0.18 (a `foreground` is passed on a cover-derived hero) | — | SaveButton.cs:151-154 |
| **Eager row** | — | `ClipToBounds = true` (the detail skin clips too) | — | — | — | — | TrackRow.cs:372 |

---

## 4. Colour & material

1. **Now-playing ink.** `Tok.AccentTextPrimary` (dark default `#A6D8FF`, re-derived from the live accent ramp) recolours
   the title, the equalizer bars, the transport glyph, the filled heart and — in Classic only — **every** secondary and
   tertiary cell in the row (`classicNow`, TrackRow.cs:227-229). Modern keeps the factual cells neutral so the accent
   marks identity, not the whole line.
2. **Page accent override.** `NumberCell(ctx: IReadSignal<PageAccent>)` lets the Recents page tint its equalizer from the
   page's ambient accent (`WaveeAccentCtx`, TrackRow.cs:1079). Null on every other page → a pure no-op. `SaveButton`
   resolves the same three-step ladder: explicit `Accent` thunk → `PageAccent.Ink` → `AccentTextPrimary` (SaveButton.cs:39).
3. **The selection pill is the page accent.** `_rowAccent` on the detail table, `_accent()` in the album drawer — see
   `03-detail-frame.md` for where that colour comes from.
4. **Camelot swatch → `WaveePalette.DataDotInk(argb, Tok.Theme)`** (TrackRow.cs:599, TrackVersionsPanel.cs:280). A
   **pass-through in dark**, a hue-dependent darkening in light: the wire hues were authored for a dark surface and read
   as unreadable smears on a light row. Rendered at 6 × 6, Opacity 0.85 — at 8 px opaque it out-shouted the title.
5. **The zebra ladder.** `WaveeColors.RowZebra` is `Tok.FillSubtleTertiary` in **dark** (the shell's own dark zebra is
   literally the hover fill, which would make hovering a striped row a no-op) and the shell's black-α `#08000000` in
   **light** (solved together with the light hover/press rungs against the art-derived page tone at `PageToneLightL`
   0.94). Invariants: zebra < hover, pressed > hover. `RowHoverZebra`/`RowPressedZebra` are `ColorContrast.Over(state,
   zebra)` — one fill, never two stacked plates (WaveeTokens.cs:224-242, PaletteBuilder.cs:96-119).
6. **The cover scrim.** A buffering ArtCard paints `WaveeOnMedia.CoverScrim` (`#6E000000`, black α 110/255) under its
   spinner (TrackRow.cs:477); the `NowPlayingOverlay` cover-centred FAB uses the same scrim and fades it in over
   `WaveeMotion.Fast` 167 ms with `Easing.FluentDecelerate` (MediaCard.cs:1415-1421).
7. **Drawer connector.** `Tok.StrokeDividerDefault` (dark `#15FFFFFF`, theme-flipping) and **not** `StrokeCardDefault`
   (a black α in *both* themes), which made the whole spine vanish on a dark surface (TrackVersionsPanel.cs:193-207).
8. **Drag chip opacity.** The card is deliberately **opaque** (`Tok.FillSolidTertiary`) — a translucent chip is the S3
   text-overdraw bug (DragChip.cs:159). The stacked backdrop cards sit at 0.85.
9. **Light vs dark, in one line each.** Row states swap ink polarity only (white-α dark / black-α light); the raised
   light rungs (hover `#0D`, pressed `#12`, zebra `#08`) exist because 3.5 % of black over a chromatic detail page is
   below the "did anything happen?" threshold. Everything else (`TextPrimary/Secondary/Tertiary`, strokes, accent) flips
   through the token set, so **the row carries no `Tok.Theme` branch except the Camelot swatch**.
10. **No Mica or acrylic on a row.** The only acrylic in this chapter is the *overlay* SelectionCommandBar's
    `Tok.AcrylicFlyout` plate and the menu flyouts (framework-owned). A track row is flat ink on the page ground.

---

## 5. Motion

| Trigger | Target | Property | From → to | Duration | Easing | Delay / stagger | Reduced motion | Source |
|---|---|---|---|---|---|---|---|---|
| Row hover enter/exit | `#` cell rest layer | Opacity | 1 → 0 | engine `BrushTransitionMs`/hover fade (83 ms default) | engine hover fade | — | unchanged (opacity is kept) | TrackRow.cs:1097 |
| Row hover enter/exit | `#` cell transport layer | Opacity | 0 → 1 | as above | as above | — | unchanged | TrackRow.cs:1098-1103 |
| Row hover | "…" wrapper | Opacity | 0.45 → 1 | as above | as above | — | unchanged | TrackRow.cs:989-990 |
| Row hover | video/more lane | Opacity | film 1→0, "…" 0.45→1 | as above | as above | — | unchanged | TrackRow.cs:1028-1044 |
| Row hover | row fill / border | fill + border | rest → `RowHover` + `StrokeCardDefault` | 83 ms (`Interaction` `BrushMs`) | engine brush fade | — | unchanged | DetailTracks.cs:3514-3532 |
| Press | row fill | fill | hover → `RowPressed` | 83 ms | — | — | unchanged | DetailTracks.cs:3518 |
| Press | transport glyph box | scale | 1 → 0.92 | `MotionTokenId.ControlFaster` 83 ms | eased | — | tier returns 1 → no transform | TrackRow.cs:1089, WaveeMotion.cs:48 |
| Hover / press | "…" and "+" buttons | scale | 1 → 1.07 / 1 → 0.92 | 83 ms | eased | — | tier returns 1 | TrackRow.cs:972-973, 1005 |
| Hover / press | ArtCard select skin | scale | press 1 → 0.98 | 83 ms | eased | — | tier returns 1 | TrackRow.cs:548 |
| **Never** | full-width row | scale | — | — | — | — | — | TrackRow.cs:379-383, DetailTracks.cs:3520-3524 |
| Like edge (same uri, unsaved → saved) | heart glyph | Opacity + scale + blur | Sx/Sy 0.25, Opacity 0, Blur 2 → 1 | spring (response 0.30, damping 0.55) | `TransitionDynamics.Spring` | — | **survives** — spring dynamics are kept by the reduced-motion policy where tween Enters are not | TrackRow.cs:913-916 |
| Recycle / different uri | heart glyph | — | snaps (Animate = null) | — | — | — | — | TrackRow.cs:920-925 |
| Now-playing + playing | equalizer bars | `Transform = Scale(1, sy)` | pattern-driven, pixel-quantised | loop 850 ms, tick 1000/30 = 33.3 ms | 5-key piecewise linear per bar (`Sample`), 3 phase-staggered patterns | — | **no tick at all**; the bars write `Sample(pattern, u = 0.3)` once — a fixed, non-uniform "still playing" shape | Equalizer.cs:61-186, EqualizerMotionPolicy.cs:20-27 |
| Paused / hover-paused | equalizer bars | scaleY | → 0.4 (flat) | one write | — | — | same | Equalizer.cs:102, 153-160 |
| Slot's first render was a shimmer | real row wrapper | Opacity | 0 → 1 | 280 ms | `Easing.FluentDecelerate` | — | engine `ReducedSnap` still cross-fades | DetailTracks.cs:2801-2813 |
| Slot mount (nav cold load / curated re-cut) | row skin | Opacity | 0 → 1 | 280 ms | `FluentDecelerate` | — | kept (fade) | DetailTracks.cs:3504-3507 |
| Multi-select toggle | check lane | Opacity + Dx | Dx −28, Opacity 0 → 1 | 333 ms | `FluentDecelerate` | — | kept | SelectorVisualsBound.cs:86-90 |
| Multi-select **off** | check lane | Opacity + Dx | 1 → 0, Dx → −28 | 333 ms | `FluentDecelerate` | — | kept | SelectorVisualsBound.cs:89-90 — the `Exit` leg is declared and symmetric; the lane leaves the way it arrived |
| Multi-select toggle | content lane | Position | slides right by 28 | 333 ms (`MotionTok.DisclosureExpand`) | `FluentDecelerate` | — | kept | DetailTracks.cs:3574-3576, MotionTok.cs:180 |
| Selection count change | count text | Opacity + Dy + blur | Dy 4 → 0, Opacity 0 → 1, Blur 2 → 0 | 150 ms | `Easing.EaseInOut` | — | kept | SelectionCommandBar.cs:142, MotionRecipes.cs:229-233 |
| Selection bar first appearance | count text | — | Enter suppressed (`Enter = default`) | — | — | — | — | SelectionCommandBar.cs:142 |
| Facts strip: hero stats | each `Stat` | Opacity (`DetailRail.FadeUp`) + Position (`Shove`) | 0 → 1 | 250 ms (`Expressive.Fast`) | `Easing.SmoothOut` | `Stagger = Motion.ReducedMotion ? 0 : WaveeMotion.MastheadStaggerMs` **45 ms**, left to right | stagger reads 0; the fade stays | TrackFactsStrip.cs:91-99, 140, DetailRail.cs:52-57 |
| Facts strip: prose / genre lines | whole line | same | 0 → 1 | 250 ms | `SmoothOut` | none (a middot line staggered word-by-word reads as a teleprompter) | as above | TrackFactsStrip.cs:100-113 |
| Late fact lands (kind 222 tempo, kind 185 plays) | neighbouring stats | Position (FLIP) | old origin → new | 250 ms | `SmoothOut` | — | kept | TrackFactsStrip.cs:140 |
| Drawer open / close | drawer height | Height (`SizeMode.Reflow`) | 0 ↔ measured | engine reflow | — | — | — | TrackVersionsPanel.cs:99-113 (rationale) |
| Drag pickup | chip | Rotation + Scale | 4°, 1.02 → 0°, 1.0 | 150 ms (`ControlFast`) | eased | once per gesture | — | DragChip.cs:72-80, 189-201 |
| Drag chip mount | chip | Sx/Sy + Opacity | 0.92, 0 → 1, 1 | declarative Enter | — | — | — | DragChip.cs:200 |
| Drag active | source row | Opacity | 1 → 0.4 | — | — | — | — | `Drag.SourceDimOpacity` DragDropFacade.cs:24 |
| Swipe drag | swipe plate | fill + icon pop | neutral → accent at the native threshold | engine `SwipeControl` | — | — | — | RowSwipe.cs:79-105 |
| ArtCard check lane | content row | Position | slides | 333 ms | `FluentDecelerate` | — | kept | TrackRow.cs:567-569 |
| Sort caret (header) | caret | Opacity/Scale/Rotation | 0→1 / 0.3→1 / 0°↔180° | `Expressive.Fast` 250 ms; spring response 0.30 damping 0.7 | `EaseInOut` / `Overshoot` / spring | — | — | DetailTracks.cs:3672-3679 (see `04-detail-track-table.md`) |
| Hover / press | drawer version row, selection-bar buttons, heart, SaveButton, FollowButton | fill + (scale) | `Interaction.Subtle` brush ladder | **83 ms** (`InteractionRecipe.BrushMs`, the `ControlFaster` curve) | eased | — | tier returns 1 | Interaction.cs:49-63, 132 |
| Hover / press | FollowButton / PreSave pill | scale | 1 → 1.04 / 1 → 0.96 (`ScaleStandard`) | 83 ms | eased | — | tier returns 1 | SaveButton.cs:155, WaveeMotion.cs:43 |
| Hover / press | SaveButton | scale | 1 → 1.07 / 1 → 0.92 (`ScaleEmphatic`) | 83 ms | eased | — | tier returns 1 | SaveButton.cs:44 |
| Hover | drag chip's live target change | caption text | swapped in place (no transition of its own) | — | — | — | — | the chip re-renders per pointer move inside the 0-alloc region — the caption must never animate |
| Slot recycle while a swipe is open | swipe belt | offset | snaps closed | — | — | — | — | `resetKey = scope.Index` → `SwipeControl` reset (RowSwipe.cs:84) |
| Drawer facts: a fact ARRIVES vs a value CHANGES | one `Stat` | Enter vs cross-swap | keyed by `"fact:" + Kind`, **not** by value | 250 ms | `SmoothOut` | stagger 45 (hero row only) | stagger reads 0 | TrackFactsStrip.cs:140 — a changed value cross-swaps in place; a NEW fact fades up as a new stat |

**Frame-time exception to flag.** `WaveeEqualizer.EqHost.Tick` samples `Environment.TickCount64` (Equalizer.cs:79, 128)
for its loop phase, not `FrameTime.NowQpc`. Every *other* animated surface in the app was moved onto the frame clock.
The 0.3 port must move this onto the present clock: `UseInterval`'s 30 Hz tick already decides *when*, but the phase
`u = (now − start) / 850` must come from `FrameTime.NowQpc` so a stalled or decimated frame does not shear the three
bars against each other. Everything else about the ticker — one timer for three bars, batched writes
(`Context.Runtime.Batch`), whole-device-pixel quantisation (`MathF.Round(sy · hPx) / hPx`), and the "no bar crossed a
step → return without writing" early-out that lets skip-submit elide the frame — is load-bearing and ports verbatim
(Equalizer.cs:126-151). Measured at ~80 % of the whole playing-state wake budget before the batching (Equalizer.cs:17-22).
See `docs/plans/wavee/scroll-feel-investigation-2026-09-10.md` (lines 313-318): the per-source `Cadence` work landed in
the engine, but this widget's own wall-clock read did not change — that is the one place the doc's intent and the code
disagree today.

---

## 6. Interaction

### 6.1 Pointer

| Gesture | Where | Result |
|---|---|---|
| Hover | anywhere on the row | fill/border swap; `#` cell reveals the transport; "…" goes to full opacity; `PointerBit` set so every `HoverOpacity` descendant inherits; the row's hover `Signal<bool>` is written (EQ stops ticking) |
| Single click | row body (detail table, album drawer, artist Popular) | **selects** (WinUI Extended: plain replaces, Ctrl toggles, Shift extends from the anchor) |
| Single click | row body (eager lists: Home top tracks, Recents, ArtCard rails) | **plays** (`TrackRow.Invoke` — toggles pause when this row is already the playing track) |
| Single click | the `#` cell's transport layer | plays / pauses this row — the single-click play path on **every** surface |
| Double click | row body (selection-backed lists) | plays |
| Click | artist name / album name span | navigates; the press lands on the text leaf, so it never plays or selects the row (TrackRow.cs:400-403) |
| Click | "…" / the video-more lane | `ClickRequestsContext` → re-enters the context funnel → the row's own `OnContextRequested` opens **byte-identically** to a right-click, anchored at the button |
| Click | ♥ | `LibraryBridge.ToggleSaved(uri, title)` — optimistic, persisted |
| Click | ⌄ | toggles this row's drawer (`ToggleExpanded(MembershipDiff.RowKey(t, displayIndex))`) |
| Click | "+" (recommendation rows) | adds to THIS playlist; the card stays until the write is confirmed |
| Right-click / Menu key / touch long-press | anywhere on the row | the track context menu (§6.4) |
| Drag | the row | lifts a `WaveeResourceDragPayload`; the row stays in its slot at Opacity 0.4 |
| Drag-over (OS file) | a detail-table row | the row lights `RowHover` through its existing bound `Fill` closure (`_videoDropRow`) — W12c. Built only when a curation service exists |
| Drop (OS `.mp4`) | a detail-table row | attaches a local video override to that track (`replace:` when one is already attached); a non-video file falls through to the shell's play-this-file path (DetailTracks.cs:3465-3489) |
| Click | a version row's split-play half / its caret | plays THAT version (the button hands back the `TrackVersion`, so a music video cannot start the audio track) / opens the format ladder — W14 |
| Click | an artist "+N" overflow chip | `ArtistMoreButton` (TrackRow.cs:20) opens a `MenuFlyout`, one row per credited artist, BottomEdgeAlignedLeft, light-dismiss; a second click closes it |

### 6.2 Keyboard

- `Enter` → `ItemContainerTrigger.EnterKey` (invoke / play). `Space` → `SpaceKey` (select; synthesised Ctrl while the
  check lane is visible). `args.IsRepeat` is ignored (DetailTracks.cs:3546-3550).
- `Alt+↑` / `Alt+↓` on a selection → shift the selected block one row (the keyboard equivalent of the drag reorder),
  under the same gates the drag has: editable playlist, natural order, no query, no filter (DetailTracks.cs:3554-3559).
- Bare `↑`/`↓` belong to the ItemsView roving focus; the row itself is `Focusable = false` (one tab stop per list).
- `Tab` reaches the expand chevron (`Focusable = true`, `Role = Button`); `Space`/`Enter` toggle it (TrackRow.cs:629).
- `F2` on a sidebar playlist row → rename (out of scope here; `Menus.SidebarRenameAction` :695).

### 6.3 Focus visuals

`FocusVisualMargin = Edges4.All(1f)` on the row skin and `(1,1,1,1)` on the chevron. The row is not itself focusable —
the ItemsView roving effect owns the single tab stop and toggles the slot root's focusability imperatively.

### 6.4 The track context menu — exact items, order, icons, conditions

Built by `Menus.Tracks(ctx, showGoToAlbum, extras)` (Menus.cs:56). The grammar (Menus.cs:16-46) is
**transport → state → collection → navigation → Share → surface extras → destructive last**.

**Header** (`TrackHeader`, Menus.cs:1115): art 38 × 38 r6 · title · subtitle. Single = `artists · album`; multi =
title `"{n} songs selected"`, subtitle `"{first title}  +{n−1} more"`. Both strings are HTML-flattened
(`SpotifyExportMapper.ToPlainText` then `HtmlText`) — producers build subtitles as markup, and a raw `<a href=…>` in a
menu header is a shipped defect (Menus.cs:1133-1145).

**Primary strip** (`TrackTransportStrip`, Menus.cs:61) — four labeled commands, always in this order:

| # | Action | Label (loc) | Icon key → `IconRef` | Enabled when | Checked |
|---|---|---|---|---|---|
| 1 | `TrackActions.Play` | `detail.play` "Play" | `play` → `Themed("Play", Icons.Play E768)` | `Svc != null && Count > 0` | — |
| 2 | `TrackActions.PlayNext` | `detail.playNext` / `menu.playNextN` "Play {n} next" | `play-next` → `Themed("PlayNext")`, fallback `WaveeIcons.PlayNext` | same | — |
| 3 | `TrackActions.AddToQueue` | `detail.playAfter` "Play after" / `menu.addToQueueN` "Add {n} to queue" | `queue` → `Themed("AddToQueue")`, fallback `WaveeIcons.PlayAfter` | same | — |
| 4 | `TrackActions.ToggleLike` | `menu.save` "Save" / `menu.saved` "Saved" | `like` → `Themed("Heart", EB51)` / checked `Themed("HeartFill", EB52)` | `Library != null && Count > 0` | all targets saved |

**Rows** (`TrackRows`, Menus.cs:81-133), in order:

| # | Row | Condition | Icon | Sub-items |
|---|---|---|---|---|
| 1 | **Add to playlist ▸** (`detail.addToPlaylist`) | always (disabled when no Library / no tracks) | `Icons.Add` E710 | `detail.newPlaylist` "New playlist" (+`Icons.Add`) · up to **10** editable real playlists, **most-recently-filed first** (`PlaylistDepositTargets.Order`) · separator · `menu.morePlaylists` "More playlists…" → the `PlaylistPickerPanel` in a centred `ContentDialog` |
| 2 | **Move to playlist ▸** (`menu.moveToPlaylist`) | `Library != null` and the target sits in a playlist this user can remove rows from | `Icons.Forward` | same submenu, source playlist excluded; semantics = add-then-remove (Spotify has no cross-playlist move) |
| 3 | **Go to album** (`menu.goToAlbum`) | single target, `showGoToAlbum`, `ActionRules.CanGoToAlbum` | `album` → `Icons.Album` | — |
| 3b | **Go to podcast** (`menu.goToPodcast`) | single EPISODE row instead of 3 — `CanGoToPodcast` **and still `showGoToAlbum`** (Menus.cs:97: the album page's `showGoToAlbum:false` suppresses both) | `Icons.RadioTower` | — |
| 4 | **Go to artist** (`detail.goToArtist`) | single target, exactly ONE artist with a uri. Two shapes: the `GoToArtist` singleton when that artist is the PRIMARY, else a bespoke row carrying that artist (`GoToArtistItem`, Menus.cs:105) — the singleton always navigates to `Artists[0]`, which a name-only primary makes wrong | `artist` → `Icons.Contact` | — |
| 4b | **Go to artists ▸** (`menu.goToArtists`) | ≥2 navigable artists | `Icons.Contact` | one plain row per artist, in billing order |
| 5 | **Share ▸** (`menu.share`) | always | `Icons.Share` | `menu.copyLink` "Copy link" / `menu.copyLinks` "Copy links (n)" · then, single spotify target only: `menu.copySpotifyUri` (`Icons.Copy`) · `menu.openInSpotifyWeb` (`Icons.Globe`) |
| 6 | **View credits** (`menu.viewCredits`) | single target with a primary artist uri | `credits` → `Icons.Document` | — |
| 7 | **Go to song radio** (`menu.goToSongRadio`) | single `spotify:track:*` | `radio` → `Icons.RadioTower` | — |
| 8 | **Video ▸** (`videoOverride.menuTitle`) | single target, a curation service exists, `VideoOverrideUx.MenuFor != None` | `video` → `Icons.Movie` | Attach video file… · Replace video file… · Locate video file… (broken link) · Show in Explorer (file present) · separator · **Remove video** (destructive) |
| — | separator + caller extras | `extras != null` | — | queue: Move up / Move down; detail: **"Track details"** (`detail.trackFacts.showDetails`, `Icons.List`) **only when the chevron lane is off** — one action must not have two visible controls (`ShowVersionsMenuItem`, DetailTrackTableRules.cs:83) |
| 9 | separator + **Remove from this playlist** (`menu.removeFromThisPlaylist`) | `RemoveFromThisPlaylist.EnabledFor(ctx)` | `remove` → `Icons.Remove` | destructive |
| 10 | **Remove from queue** (`menu.removeFromQueue`) | `Kind == QueueEntry && RemoveFromDisplay != null` | `Icons.Remove` | destructive |

Module playables (`Menus.ModuleTrack`, Menus.cs:150) = the same menu with `showGoToAlbum: false` plus an
`"Open on {Module}"` extra (`Icons.OpenInNewWindow`) when the module's page document carries a http(s) `openUrl`.
Player-bar now playing (`Menus.NowPlaying`, Menus.cs:1111) = the same menu with `Host = null`, so BOTH remove rows drop.
A 240-DIP pane (the sidebar) takes `AddTrackTransportRows` (Menus.cs:71) instead of the strip — same four verbs, as
plain rows, because an Explorer-style labelled command bar does not fit there.

**The deposit submenu is ONE shape** (`PlaylistDepositItem`, Menus.cs:304) shared by Add-to-playlist (tracks),
Add-to-playlist (container) and Move-to-playlist: `New playlist` (`Icons.Add`) → up to 10 rows → separator →
`More playlists…`. `New playlist` mints the next unused `My Playlist #N` (never another literal "New playlist");
the submenu itself is DISABLED (not absent) when `canAdd` is false, and `More playlists…` additionally needs an
overlay service. `Move to playlist` excludes the source; `Add to playlist` on a container excludes the container.

**Toasts raised by these verbs** (see `19-shell-overlays.md`): `detail.addedToQueue` "Added {count} to queue" ·
`detail.addedToPlaylist` "Added to {name}" with **Undo** (a create-then-add toasts **Open** instead — you just made a
playlist and it needs a name) · `detail.edit.removedFromPlaylist` "Removed N songs" with **Undo** ·
`menu.movedToPlaylist` "Moved to {name}" with `detail.goToPlaylist` (whose text is literally **"Open"**) ·
`menu.linkCopied` / `menu.uriCopied` (plus a screen-reader `Announce` of `auth.copied`) · **"Radio started → Open
playlist"** from `GoToSongRadio` (`RadioLaunch.Start` parks the radio playlist after the current track and never
interrupts playback) · `drag.movedTo` / `drag.movedManyTo` with **Undo** for a rootlist filing · every FAILED write
maps through `PlaylistEditErrors.Toast(ex, verb)` — a refused reorder and a refused copy are different sentences.
A clipboard failure toasts through the same mapper rather than silently doing nothing (TrackActions.cs:94, 183).

### 6.5 Selection

`SelectionModel` with `ItemsSelectionMode.Extended`. Plain tap replaces, `Ctrl` toggles, `Shift` extends from the
anchor. While the check lane is visible a plain tap/Space is rewritten to a Ctrl-tap
(`SelectorVisualsBound.MultiSelectMods`). Turning multi-select **off** clears the selection (`MultiSelectButton`,
DetailTracks.cs:3641-3660). "Select all" spans only the rows `trackAt` resolves to a track — and it
`DeselectAll()` then `SelectRange(first, last)`, so it selects the whole CONTIGUOUS span between the first and last
track rows, not a set of individual indices (SelectionCommandBar.cs:293-306).

**Running a selection command ends the selection.** Every inline button (`ActionButton` → `action.Execute(c); _exit();`,
:202) and every overflow row (`WithExit`, recursing into submenus, :247-259) calls the bar's `exit` after invoking.
The docked host's `exit` is the caller's (the detail page's "leave multi-select"); the overlay host's is
`sel.DeselectAll`. "Select all" is the one command that deliberately does not exit.
The count itself is `SelectedTrackCount()` — a scan that counts only selected indices `trackAt` resolves, so a
selection that includes the "Recommended" header row still reads "4 selected", never 5.

### 6.6 Drag and drop

**Sources.** Every track row (`Drag.Source(WaveeDragKinds.Resource, …)`): the detail table (payload = the whole
selection when the dragged row is inside a ≥2 selection, else that row), recommendation rows (always a single-track
copy), Recents rows, queue rows (`ForQueueRow` — marked as a reorder), the detail hero cover (`WaveeDetailDrag.Hero`),
sidebar rows, tabs, cards.

**Payload** (`WaveeResourceDragPayload`, WaveeResourceDrag.cs:32): `Kind` · `Id` · `Uri` · `Name` · optional ordered
`Tracks` snapshot · `SourcePlaylistUri` + `SourceRows` (a same-list drop is a MOVE) · a cold `TrackResolver`
(playlist/album only — an **artist is never resolved**, a locked product decision: an artist dropped on a playlist is
refused with a cue rather than depositing a guess, WaveeResourceDrag.cs:210-217) · `RootlistItem` / `RootlistItems` ·
`ArtUrl` · `SourceQueueItemId`.

**Targets.** Playlist track lists (insertion), the tab strip, sidebar rows and the collapsed rail, the player bar.
`CanCopyTracks = hasTracks && !fromQueue` is the ONE predicate every target reads, so the queue's reorder-only rule
reaches all of them without any target knowing the queue exists (WaveeResourceDrag.cs:66-67, WaveeDragRules.cs:186-190).

**Visuals.** One chip (W21), one insertion line + gap preview, one scrim policy (same-list reorder = no scrim), one
refusal cue (W22). The chip's `RestingCaption` ladder: queue → `drag.reorderHint` "Drag to reorder"; rootlist
playlist/folder → `drag.organizeHint` "Drop between playlists or onto a folder"; can-copy → `drag.dragOntoPlaylist`
"Drag onto a playlist to add"; else none. A live target's caption supersedes it
(`drag.moveTracks` "Move {n} songs" / `drag.addTracks` "Add {n} songs" / `drag.addTo` "Add to {name}").

**A rootlist multi-select of ≥2 carries NO track resolver** (`FromEntries`, WaveeResourceDrag.cs:113-134): an
organisation gesture must not offer "deposit the first playlist's songs". N = 1 is byte-for-byte `FromEntry`, so
there is no second single-item path. The chip's count then comes from `RootlistCount`, not from `Tracks`.

**A tab is a deposit destination** when it stands for a real, writable `spotify:playlist:*` — and `TabDropRules.
AcceptsDeposit` additionally refuses the payload's OWN source playlist and the payload's own uri (WaveeDragRules.cs:
168-174): a tab drop can only APPEND, so a row dragged out of P onto P's own tab would fall to the copy arm and
duplicate the user's rows into their own playlist. Refusing means the tab never lights up for a gesture with
nothing to do.

**A same-list MOVE's insertion index is the PRE-move one** ("insert before the row currently at this index");
`DepositTracksAsync` hands it to the seam unchanged and `MoveRowsConventionTests` pins it. Do not pre-correct it.
A container dropped on ITSELF without membership refs is a silent no-op (WaveeResourceDrag.cs:553-555).

### 6.7 Tooltips and accessibility names

- Row commands in the selection bar use `ToolTip.Wrap(glyphButton, label)` at fit 1 and 2 (SelectionCommandBar.cs:341);
  the `⋯` is always wrapped with `common.more` and the `✕` with `detail.clearSelection`.
- Artist Popular's compact play count carries the exact count (`"1,850,220,114 plays"`) as a tooltip
  (ArtistPopular.cs:402-406).
- `Role = AutomationRole.Button` on the row, on every in-row affordance, and on the ArtCard select skin.
- `Cursor = CursorId.Hand` resolves up the ancestor chain, so an unannotated `TextEl` inside a hand-cursor box works;
  the heart/`"…"`/transport set it explicitly and drop it when their handler is null.
- A rootlist move announces itself (`Announcer.SayThrottled`), as does a link copy
  (`InputHooks.Current.Default.Announce`).

### 6.8 Inline edit

None on this surface. Playlist title/description inline edit is `06-playlist.md`.

---

## 7. Data & readiness in 0.3 terms

| Visual element | 0.2.9 source | 0.3 read | Readiness predicate (skeleton until true) |
|---|---|---|---|
| Row number | display index | `item.Index − trackStart` | always |
| Title | `Track.Title` | `t.TitleId` → `Entities.Strings.Resolve` | `t.Knows(TrackFields.Title)` |
| Artist subline (per-artist links) | `Track.Artists` (`ArtistRef[]`) | `Edges.TrackArtists.Targets(slot)` → `Artist.NameId` per slot | `t.Knows(TrackFields.Artists)` **and** each artist slot `Knows(ArtistFields.Name)` |
| Album lane / subline album | `Track.Album` (`AlbumRef`) | `t.Album` (slot) → `Album.NameId`, `Album.Uri` | `t.Knows(TrackFields.Album)`; empty name → `Dash` + clickable |
| Art thumb | `Track.Image` | `t.ImageId` | `t.Knows(TrackFields.Image)`; else `Design.PlaceholderCover` |
| Explicit badge | `Track.IsExplicit` | `TrackFlags.Explicit` | `t.Knows(TrackFields.Explicit)` |
| Duration | `Track.DurationMs` | `t.DurationMs` | `t.Knows(TrackFields.Duration)`; 0 → `Dash` |
| Plays | `Track.PlayCount` | `t.PlayCount` | **`t.Knows(TrackFields.PlayCount)`** — see plan correction 3 |
| Not-yet-out dim + date | `Availability` + `AvailableAt` | `TrackFlags.Unavailable` + `t.AvailableAt` | `t.Knows(TrackFields.Availability)` |
| BPM · key · swatch | `TempoBpm`/`MusicalKey`/`CamelotCode`/`CamelotColor` | `t.Tempo` (×10), `t.Key`, `t.Camelot`, `t.CamelotColor` | `t.Knows(TrackFields.Audio)`; unknown renders **empty** (not a dash — kind 222 lands late and a dash would flicker) |
| Saved ♥ | `LibraryBridge.IsSaved(uri)` | `User.Me.Likes(t)` (reverse index) + `LibraryEdge.Flags` pending bit | `Edges.Liked.State(meSlot) != unknown` |
| Added by (name + avatar) | `Track.AddedBy` + `DetailModel.UserProfilesById` | `PlaylistTrackEdge.AddedBy` → user slot → `User.NameId` / `User.AvatarId` | edge known **and** `User.Knows(UserFields.Profile)` |
| Date added | `Track.AddedAt` | `PlaylistTrackEdge.AddedAt` (int) | edge known |
| Chart glyph | `Track.Chart` (`ChartEntry`) | `PlaylistTrackEdge.ChartStatus/ChartPos/ChartPrev` | edge known; `Unknown`/`Equal` → nothing |
| Row identity for drawer/state | `Track.ContextUid` else `uri#@index` | `PlaylistTrackEdge.ItemId` else `uri#@index` | — (`MembershipDiff.RowKey` ports verbatim) |
| Video film glyph | `VideoPresence.HasVideo(t)` (3 planes) | `TrackFlags.HasVideo` **∪** the override table **∪** the module form probe | `t.Knows(TrackFields.Video)` |
| Now-playing / playing / buffering | `PlaybackBridge.Identity/IsPlaying/IsBuffering` + `UseAsyncCommands.IsRunning(id)` | `Playback.Current` / `Playback.IsPlaying` / `Playback.PendingSlot` signals (§4.8) | always (playback state has no "unknown") |
| Top-track star | `DetailModel.TopTrackId` | **gap** — propose `AlbumTable.TopTrackSlot` | `a.Knows(AlbumFields.Detail)` |
| Descriptor chips (drawer) | `Track.Tags` | `Edges.TrackTags.Payload(slot)` | `t.Knows(TrackFields.Tags)`; null ≠ empty — neither renders |
| ISRC (drawer) | `Track.Isrc` | `t.Isrc` | `t.Knows(TrackFields.Isrc)` |
| Released (drawer) | `Track.AvailableAt` | `t.AvailableAt` | as above |
| Versions + formats + waveform (drawer) | `Services.TrackExpansion.GetAsync(uri)` | **gap** — see DATA GAPS 1 | edge state `complete` |
| Selection bar thumbs | `Track.Image` of the first 3 distinct selected | `t.ImageId` | `Knows(Image)` |
| Drag chip title/subtitle/art | `WaveeDragChipModel.For(name, artUrl, tracks, rootlistCount)` | same rule over handles | — |

**Readiness discipline.** A row renders `ShimmerRow` (identical lane geometry, empty cells) until its slot's reveal
edge; the crossing is a 280 ms opacity cross-fade. The owner treats *partial rows popping in* as a regression, so a
0.3 row must gate on `Knows(TrackFields.Row)` as a **group**, not per-cell: the page demands
`Entities.EnsureRows(slots, TrackFields.Row)` for its **whole** model on mount (plan §4.13) and the list shimmers until
the group lands. The two late-arriving *enrichments* — kind 222 (`Audio`) and kind 185 (`PlayCount`) — are the only
per-cell exceptions, and they render **empty** and **dash** respectively rather than shimmering, because a shimmer that
resolves 4 seconds later on 200 rows reads worse than a settled lane.

**No page-side fetch windows.** `TrackRow` never fetches. The one exception in 0.2.9 is
`TrackVersionsPanel`'s on-expand fetch (TrackVersionsPanel.cs:91-97) — legitimate, because the drawer only mounts on an
explicit user gesture.

### DATA GAPS

| Element | 0.2.9 source | Proposed 0.3 column / edge |
|---|---|---|
| **1. Alternate versions + audio formats + waveform** (the whole drawer) | `ITrackExpansionService` → kinds 98 (alt audio) / 99 (music video) / 5 (formats) / 237 (waveform); `TrackExpansion` record | `EdgeTable<TrackVersionEdge> TrackVersions` (payload `{byte Kind, int DurationMs, StringId Title, StringId ArtUrl}`), `EdgeTable<FormatEdge> TrackFormats` (payload `{int FormatId, int AvgBitrate, byte AvailableOnDevice}`), `Column<StringId> WaveformBlob` on `TrackTable` (or a side `Dictionary<int, float[]>` in `Track.UI.cs`'s own cache — it is 64 floats and cold). The plan's §4.2/§4.3 have none of this. |
| **2. `ArtistLineId`** | derived per render from `Track.Artists` | Plan §4.12 *uses* `t.ArtistLineId`, §4.2 does not declare it. Declare `Column<StringId> ArtistLine` on `TrackTable`, computed at commit (P11). **But it is not sufficient** — see plan correction 1. |
| **3. Per-artist click targets** | `TextSpan(name, OnClick: () => go(route, name))` per artist | `Edges.TrackArtists.Targets(slot)` (exists) + a `item.Spans` / `item.InvokeSpan` builder over artist slots. Needs `Artist.NameId` + `Artist.Uri` in the Row field group. |
| **4. Album top track** | `DetailModel.TopTrackId` (kind 185 ranking) | `Column<int> TopTrackSlot` on `AlbumTable` + `AlbumFields.TopTrack` |
| **5. Added-by profile (name + avatar)** | `DetailModel.UserProfilesById` → `Owner(Id, Name, Avatar)` | `UserTable.NameId`, `UserTable.AvatarId`, `UserFields.Profile`; §4.14 declares no `UserFields` at all |
| **6. Buffering per row** | `UseAsyncCommands<string>.IsRunning(track.Id)` ∪ `PlaybackBridge.IsBuffering` | `Playback.PendingSlot` (int signal, 0 = none) + `Playback.Buffering` (bool signal). §4.8 mentions "Pending" but the shape is undeclared |
| **7. Local video override** | `IVideoOverrideCurationService` (a per-uri dictionary + persisted store) | `EdgeTable<NoEdge> VideoOverride` on the user slot, payload `StringId LocalPath`, plus a `TrackFlags.VideoOverride` bit so `HasVideo` stays one ordinal probe |
| **8. Module playable "form"** | `ModulePlayables` sync cache | keep as a `Platform/Modules.cs` dictionary probe; `Track.HasVideo` folds all three planes in one place (port `VideoOverrideUx.HasVideo`) |
| **9. Camelot colour → light-mode ink** | `WaveePalette.DataDotInk(argb, theme)` | pure; ports to `Platform/Design.cs` verbatim. `CamelotColor` (uint) is already in the plan |
| **10. Marquee-disabled setting** | `WaveeSettings` + `TrackRowsSnapshot.MarqueeDisabled` | a `Settings` read in `Track.UI.cs`; no column needed |
| **11. Deposit MRU** | `WaveeSettings.PlaylistDepositRecents` (a serialised uri list) | unchanged — a settings string, parsed by `PlaylistDepositTargets` (pure) |
| **12. Artwork variation seed** | `t.Id.GetHashCode() & 0x7fffffff` | `t.Slot` is NOT stable across scope switches; use `t.Uri.Full.GetHashCode()` (the interned StringId) so a generated-cover hue survives a restart |
| **13. Track credits** (the View-credits modal) | `svc.TrackCredits.GetAsync(trackUri)` (kind 186, uncapped) ∪ `svc.AlbumEnrichment.GetNowPlayingInfoAsync(artistUri, trackUri).Track.Credits` (NPV, capped at ten) — `TrackCredit(Name, Role, RoleGroup, ArtistUri, Linkable)` + `CreditSources` | not in the plan at all. Propose a cold side-cache in `Track.Drawer.cs` keyed by track slot (a modal, opened at human rate) rather than a column; the TWO-source fallback and the "both must settle before 'no credits' is the truth" rule are the load-bearing part |
| **14. Audio-format ladder + the per-track override** | `svc.TrackExpansion.Formats` (kind 5), `FormatOverrideFor(uri)` / `SetFormatOverride(uri, id?)` | folds into GAP 1's `TrackFormats` edge, PLUS a persisted per-uri override map (a settings string, like GAP 11). The override is what the caret's radio column reflects; without it the split button has no state |
| **15. "In your top N"** (Home top tracks) | `userTopUris` set + `userTopCount`, computed by the Home artist module | a `Set<slot>` handed in as a prop; no column. Purely a badge predicate |
| **16. Row-hover signal** | a per-row `Signal<bool>` written by the skin's `OnHoverMove`/`OnPointerExit` | 0.3's `item.Hovered` (or the equivalent slot signal). It drives THREE things at once — `PointerBit` for every `HoverOpacity` descendant, the EQ `paused` gate, and nothing else. It must come from the interactive ANCESTOR, never from the bars' own hit target |

---

## 8. Pure rules to port verbatim

| Class | 0.2.9 file | Decides | Tests | 0.3 destination |
|---|---|---|---|---|
| `TrackLane` | `Features/Detail/DetailTrackTableRules.cs:227-279` | every fixed lane width (28/28/32/132/88/52/80/52/28/40/26), the star weights (1 / 0.75 / 0.75) and the identity FLOORS (120 / 90 / 90) | `TrackRowStyleRulesTests.cs`, `DetailLayoutBreakpointTests.cs` | `Entities/Track.cs` **CORE** |
| `DetailTrackTableRules` — `IdentityColumns`, `TrailingColumns`, `RowHeightFor`, `HeaderHeightFor`, `ArtSizeFor`, `ShowClassicInlineVideo`, `ShowVersionsMenuItem`, `HeaderActive`, `NextSort`, `PreviewScale` | `DetailTrackTableRules.cs:22-108` | which lanes a skin + tier admits; the 40/48/56/64 and 36/40/44/48 ladders; the art ladder; the sort cycle | `TrackRowStyleRulesTests.cs` (374 lines) | `Entities/Track.cs` **CORE** (`Track.TableRules`) |
| **the relief ladder** — `TrackTableLanes`, `Relieve`, `MinWidthFor`, `NominalReliefFor`, `ReliefFor`, `MaxRelief 8`, `ReliefHysteresisDip 24` | `DetailTrackTableRules.cs:129-216` | which lanes yield, in which order, at which measured width, with which hysteresis | `TrackRowStyleRulesTests.cs` | `Entities/Track.cs` **CORE** |
| `DetailLayoutBreakpoints` (`NominalTierFor`, `TierFor`, `TierHysteresisDip`) | `Features/Detail/DetailLayoutBreakpoints.cs` | the 860/720/560/440/340/300 tier ladder | `DetailLayoutBreakpointTests.cs` | shared with `04-detail-track-table.md`; `Entities/Track.cs` **CORE** |
| `TrackExpandedFacts` (`For`, `IsHeroFact`, `HeroSplit`, `LabelKey`, `TrackTime`, `DurationCell`, `Bpm`, `KeyLabel`, `ModeOf`, `PrettyKey`, `KeySplit`) | `Features/Detail/TrackExpandedFacts.cs` (367) | the ordered fact list, the hero/prose partition, and **every shared number format** (the row lane, the drawer and the facts strip must never spell one fact two ways) | `TrackExpandedFactsTests.cs` (591 lines) | `Entities/Track.cs` **CORE** |
| `EqualizerMotionPolicy` (`ShouldTick`, `ShouldShowStillShape`) | `Components/EqualizerMotionPolicy.cs` (28) | whether the 30 Hz tick runs; the reduced-motion settled shape | `EqualizerMotionPolicyTests.cs` (49 lines) | `Platform/Controls.cs` **CORE** section |
| `NowPlayingMatch` (`MatchesContext`, `MatchesTrack`, `OwnsPlayback`, `RelatesToPlaying`) | `Components/NowPlayingMatch.cs` (42) | strict (a click may pause) vs loose (a reveal may light up) now-playing relations | `NowPlayingOverlayMatchTests.cs` (96 lines) | `Playback/Playback.cs` **CORE** |
| `MembershipDiff` (`Diff`, `Keys`, `RowKey`, `RowKeyMatches`, `MembershipDelta`, `RowChange`) | `Components/MembershipDiff.cs` (119) | the keyed row diff, reset classification (retained < 0.5 or structural > 40), and **the per-row identity the drawer/like-edge/expand state key on** | `MembershipDiffTests.cs` (226 lines) | `Entities/Playlist.cs` **CORE** |
| `WaveeDragChipModel` (`For`, `ArtOf`) | `Features/DragDrop/WaveeDragChipModel.cs` (52) | which line wins for a track vs entity drag, where the art comes from, what the badge counts | `WaveeDragChipModelTests.cs` (101 lines) | `Platform/Drag.cs` **CORE** |
| `WaveeDragKindMap` (`Of(HomeCardKind)`, `Of(SearchHitKind)`, `OfUri`, `Of(SidebarEntryKind)`) | `WaveeDragRules.cs:15-81` | card kind → drag kind | `WaveeDragRulesTests.cs` (286 lines) | `Platform/Drag.cs` **CORE** |
| `PlaylistDropRefusalRules` (`Evaluate`, `Accepts`) + `PlaylistDropRefusal` | `WaveeDragRules.cs:87-143` | accept + refusal reason from ONE table, in a deliberate order | `WaveeDragRulesTests.cs`, `RootlistRefusalTests.cs` | `Platform/Drag.cs` **CORE** |
| `TabDropRules`, `QueueDragRules`, `SidebarRailDropRules` | `WaveeDragRules.cs:151-210` | tab deposit legality, the queue's reorder-only rule, the rail tile's transparency | `WaveeDragRulesTests.cs`, `SidebarDropCueTests.cs` | `Platform/Drag.cs` **CORE** |
| `ActionRules` / `SpotifyLink` (`CanGoToAlbum`, `CanGoToPodcast`, `CanGoToArtist`, `CanViewCredits`, `CanStartTrackRadio`, `CanRemoveFromPlaylist`, `AllSaved`, `HasLink`, `LinkText`, `SingleUri`, `WebUrl`) | `Actions/ActionRules.cs`, `Actions/SpotifyLink.cs` | which menu rows exist and are enabled | `Actions/ActionRulesTests.cs`, `Actions/SpotifyLinkTests.cs`, `Actions/MenuGrammarTests.cs`, `Actions/SelectionSemanticsTests.cs` | `Shell/Shell.cs` **CORE** (the one action table, Wave 4 owner I) |
| `PlaylistDepositTargets` (`Order`, `Parse`, `Serialize`, `Remember`, `IsDepositable`, `NextDefaultName`) | `Actions/PlaylistDepositTargets.cs` | the Add-to-playlist submenu's eligibility + MRU order + default name | `PlaylistDepositTargetsTests.cs` | `Entities/Playlist.cs` **CORE** |
| `PlaylistReorderRules` (`AllowsSameListMove`, `RowsAreKeyed`, `VerbFor`) | `Features/Detail/PlaylistReorderRules.cs` | whether a same-list move is legal and which verb the chip states | `PlaylistReorderRulesTests.cs`, `MoveRowsConventionTests.cs` | `Entities/Playlist.cs` **CORE** |
| `ArtistPopularLayout` (tier + hysteresis) | `Features/Detail/ArtistPopularLayout.cs` | the chart row's art size / duration visibility / subtitle stacking | `ArtistPopularLayoutTests.cs` (168 lines) | `Entities/Artist.cs` **CORE** (chapter 08) |
| `VideoOverrideUx` (`MenuFor`, `HasVideo`, `FirstMp4`) | `App/VideoOverrideUx.cs` | which Video ▸ rows exist; the three-plane video answer | `Actions/VideoOverrideUxTests.cs` | `Platform/Modules.cs` / `Entities/Track.cs` **CORE** |
| `TouchInput.SwipeArmed` | `Components/RowSwipe.cs:21-46` | whether the swipe wrapper is in the tree at all | **untested** — see §9 | `Platform/Platform.cs` |
| `SelectionCommandBar.FitFor` (760 / 390) | `Components/SelectionCommandBar.cs:117` | labels / glyphs / essentials | **untested** — extract and test | `Platform/Controls.cs` **CORE** |
| `SearchHighlight.LineBoxFor` | `Design/SearchHighlight.cs:75` | the wrap cap for a highlighted title (14→20, 12→16, else `ceil(size × 1.43)`) | **untested** (verified: no test references it) | `Platform/Controls.cs` |
| `TrackRow.PlaysLabel` | `Components/TrackRow.cs:153` | the ONE compact stream-count format (`1.85B` / `11.8M` / `654.8K` / `N0`). The artist chart uses it ALWAYS and puts the exact count in a tooltip; the ArtCard plays line uses `N0` instead | covered indirectly by `ArtistPopularTracksTests` | `Entities/Track.cs` **CORE** |
| `TrackRow.ShowTempo(set)` | `TrackRow.cs:578` | `Tempo && Tier <= 3` — read by the row AND by `TracksFor`, so the width track and the cell can never disagree | `TrackRowStyleRulesTests.cs` | `Entities/Track.cs` **CORE** |
| `TrackRow.PadXFor` / `ColGapFor` / `RowHeightFor` / `ArtSizeFor` | `TrackRow.cs:118-130` | the alignment invariant — header and rows read the SAME helpers keyed by the set's Tier | `TrackRowStyleRulesTests.cs` | `Entities/Track.cs` **CORE** |
| `DetailTracks.ArtCentreIndent` | `Features/Detail/DetailTracks.cs:641` | where the drawer's rail lands, derived from the lane table (not a constant) | **untested** — extract and test alongside the lane table | `Entities/Track.cs` **CORE** |
| `WaveeResourceDragPayload.CanCopyTracks` / `FromQueue` / `RootlistCount` / `TryPin` | `WaveeResourceDrag.cs:48-93` | the one predicate every target reads; the pin kind map routed through `SidebarPinId.IsPinnable` rather than a guessed fallback | `WaveeDragRulesTests.cs` | `Platform/Drag.cs` **CORE** |
| `PlaylistInsertionPreview.Cap` | `PlaylistInsertionPreview.cs:18` | `= SortableMath.DefaultPreviewCap` (3). Deliberately the FRAMEWORK's number — the view sizes the gap from it, so a local literal drifts the cards off the gap | — | `Platform/Drag.cs` |
| `WaveeRootlist.IsMember` / `CanEditPlaylist` | `WaveeResourceDrag.cs:227-255` | "not known to be in the rootlist / editable" must never present as "is" — both answer FALSE on a cold store | `RootlistRefusalTests.cs` | `Platform/Drag.cs` |

---

## 9. Re-author notes

### Must not be simplified

- **`ColumnSet` is a value record, and it is the cache key.** `_tracksBySet` is keyed by `(ColumnSet, art)`
  (DetailTracks.cs:139). Making the 0.3 `RowStyle` a class, or adding a mutable field, silently forks the width-track
  cache and costs a per-render allocation on every row.
- **Cell keys.** `CellKey.*` must survive. Without them the reconciler matches a surviving Added-by cell against a
  departed Album cell at a breakpoint cross and patches the wrong content into the wrong column — and destroys/remounts
  any component that landed opposite a different element type (TrackRow.cs:214-218).
- **`MinWidth = 0` + `ClipToBounds` on every cell wrapper.** Both halves are load-bearing when the grid is handed less
  width than its fixed tracks need: without `MinWidth = 0` the cell floors at its natural min; without `ClipToBounds` a
  squeezed cell paints straight over its neighbours (the rail-open pile-up: title, artist and album stacked on the same
  pixels) — TrackRow.cs:1124-1129.
- **The title column's `Opacity = 0.45`, not a colour swap.** The title element is built by the *caller* (plain, bound,
  marquee, Classic span run), so the grid cannot reach in to retint it — but it can step the whole column back in the
  hierarchy, which works for every title variant (TrackRow.cs:264-267).
- **One equalizer ticker for three bars, with the batched write and the "nothing crossed a pixel step" early-out.**
  Per-bar timers were up to 45 distinct wake instants/second, each moving ONE bar, so skip-submit could almost never see
  a byte-identical frame (Equalizer.cs:17-22, 126-151).
- **Hover-pause the equalizer.** Under a `HoverOpacity = 0` reveal the bars are invisible but still present a full
  window 30×/s. Pass the row's hover signal as `paused`; do **not** flip `animate`/`Key` on hover.
- **`MoreRestOpacity = 0.45`.** Not 0 (undiscoverable — the reported defect) and not 1 (real scanning cost on 1500 rows).
- **The video/actions complement.** `Actions` is the exact complement of `Video` so the trailing lane is reserved exactly
  once. Getting this wrong double-reserves 68 DIP or loses the menu entirely.
- **The em-dash discipline.** `PlayCount <= 0`, `DurationMs == 0`, a name-less album — all dash. A pending *tempo*
  renders **empty**, never a dash, because it flickers to a value a moment later.
- **`LikeEdge`.** The heart pop must fire only when the **same uri** flipped unsaved → saved since this slot's last
  render. A recycle re-binds a different uri, so scrolling must never replay the pop.
- **Menu grammar order.** `transport → state → collection → navigation → Share → extras → destructive`. A verb may be
  omitted only where the seam genuinely does not exist, and then with a comment naming the reason.

### Traps

1. **Props freeze at mount.** `RowStyle` must **not** be a constructor field of the row content component. 0.2.9 hit
   this exactly once and fixed it by making the shape a signal the row *subscribes* to (`_rowShape.Value`,
   DetailTracks.cs:2738 + the comment at 2708-2712): a frozen shape forced a breakpoint cross to remount the whole list.
   Same rule for `PreSaveButton`/`SaveButton`/`FollowButton` — their uri freezes, so **every** call site keys the embed
   (`Key = "presave:" + uri`, SaveButton.cs:58-60).
2. **`Key` remount vs in-place patch.** The equalizer used to key on `animate ? "eq-play" : "eq-pause"`, which tore the
   whole `#`-cell subtree down on every play↔pause. It is now a persistent host reading a **signal**
   (Equalizer.cs:25-29) with a deps-gated `UseEffect(…, animate)` that re-runs the phase/settle reset exactly on the
   transition the old remount used to cover. Do not reintroduce the key.
3. **A component's root cannot be remounted by a changed Key.** `ReconcileSingleChild` reuses a same-type child and only
   *reports* the ignored key. What forces Remove+Mount is the root's **element type** changing — which is why the
   shimmer row is a `GridEl` and the real row is wrapped in a `BoxEl` for its crossing (DetailTracks.cs:2794-2801). The
   keys are the intent; the type is the mechanism.
4. **`ReuseGuard`.** The Wave 5 gate is "no remounts on a table publish". Every row cell must therefore be an `item.*`
   bind or a `Prop.Of` closure, never a value captured at template-build time.
5. **Zero-allocation scroll frames vs per-row richness — how 0.2.9 reconciled them.** (a) The row **template runs once
   per slot**; every `item.*` call allocates one closure at that moment and nothing afterwards. (b) Per-row *state* lives
   in `UseRef`/`UseComputed` with equality gates, so a playback change re-runs a cheap effect and schedules no re-render
   for the 11 rows it does not concern. (c) The marquee is mounted **only** on the now-playing row — it is 2 nested
   components + a measure→re-render cycle + a perpetual TranslateX track per row, and on a 12-row cold mount that was
   ~24 of ~60 components (DetailTracks.cs:2695-2700). (d) `ChartGlyph` uses constant literals, never per-render string
   concat. (e) `ArtistPopular.Row` builds **exact-size arrays**, not `List` + `ToArray`, because a column crossing
   realises five rows in one frame. (f) The swipe wrapper is skipped entirely until a finger has touched the app this
   session — its props record carries the freshly built row `Element`, so the equality gate can never coalesce and the
   ~470-line control re-renders 1:1 with every recycle (29-84 per scroll flush, measured) for something that cannot
   activate (RowSwipe.cs:12-20). All six survive.
6. **Selection re-skin must be compositor-only.** The pill is an always-present child revealed by a **bound** opacity;
   the zebra/hover/press fills are `Prop.Of` closures over the slot index. A selection change must not re-render the
   list or replay an Enter.
7. **`BlocksDragArm`.** Forget it on one affordance and that affordance stops working the moment the row is a drag source.
8. **The drawer's reserved video row.** Reserve it from `Facts.HasVideo` **before** the fetch answers, keyed by KIND, so
   the first solved height is the final height — otherwise the drawer's reflow chases a moving target and the rows below
   it jump (TrackVersionsPanel.cs:99-120).

### Where the plan is wrong or too thin

1. **§4.12's artist line kills the artist hyperlinks.** `new TextEl(item.Text(t => t.ArtistLineId))` renders one flat
   string. 0.2.9 renders **one clickable span per artist**, joined by `", "`, each navigating to its own artist, with
   the press landing on the text leaf so the row is neither played nor selected (TrackRow.cs:780-803, 807-858). The
   engine has the primitive (`item.Spans` + `item.InvokeSpan` — the skill's own bound-row example shows exactly this).
   Fix §4.12 to use it; keep `ArtistLineId` only as a fallback for surfaces with no nav handler.
2. **§4.12's sketch is a 56 px row with a 40 px cover.** Field by field against 0.2.9:

   | §4.12 | 0.2.9 | Verdict |
   |---|---|---|
   | `Height = 56` | 40 / 48 / 56 / 64 by density (Classic 36/40/44/48) | **wrong** — 56 is the Cozy rung; Default is 48 |
   | `Size = 40` cover | 32 / 32 / 40 / 48 by density (Classic 32 except Comfortable 40) | **wrong** — 40 is the Cozy art; Default is 32 |
   | `Radius = 4` | `Radii.Control` 4 | ✔ |
   | `style.ShowNumber` | `#` is **always present** (it is also the transport) | **wrong** — never optional |
   | number cell = text | a five-state machine + a hover transport reveal | **far too thin** |
   | one title + one subline | title + subline **+ nine optional lanes**, tier- and relief-gated | **far too thin** |
   | `style.ShowAddedAt` | `By`, `Date`, `Album`, `Plays`, `Tempo`, `Video`, `Actions`, `Expand`, `Heart`, `Thumb`, `Artist` | 11 lanes, not 1 |
   | `item.Duration(...)` | `DurationCell` (0 → dash) **or** `ShortDate(AvailableAt)` for a pending row | needs the dash/date rule |
   | `item.ShowWhen(now, Equalizer)` | the equalizer lives **inside** the `#` cell's rest layer, under a `HoverOpacity` reveal, with a `paused` signal | wrong placement; will double-render with the number |
   | `OnPointerDown = Play` | single click **selects** on selection-backed lists; double click plays; the `#` transport plays | **wrong** — three surfaces of behaviour collapsed into one |
   | no hover state at all | the whole row is a hover machine | missing |
   | no `Padding` numbers | `PadXFor(tier) − RowInset`, `ColGapFor(tier)`, `Margin RowInset` | missing |
   | no keys | every cell is keyed | missing — this one is a correctness bug, not a polish item |

3. **`TrackFields.Row = Identity | PlayCount | Availability` conflates "asked" with "answered".** The dash rule is
   `Knows(PlayCount) ? (n > 0 ? PlaysLabel(n) : "—") : "—"`, and the *pending* variant of BPM·key renders **empty**.
   Put the distinction in the field group comment or the first re-author will render `0`.
4. **`wavee-0.3-implementation.md` §2 has no home for the drawer, the drag layer, or the row primitives, and the two
   chapters that share the track surface disagreed about where the drawer lives.** `Entities/Track.cs (300) +
   Track.UI.cs (1,500)` (master plan §2, tree line `Track.cs  Track.UI.cs   300 + 1,500`) is the whole budget for a
   surface that is 5,150 lines today. Worse, this chapter's tree gaps proposed `Entities/Track.Drawer.cs` (700) with
   `Track.UI.cs` at 2,600 while `04-detail-track-table.md` §1.2 (row `TrackVersionsPanel + TrackFactsStrip →
   Track.Drawer(...) in Track.UI.cs`) and its §9 line budget (`Track.UI.cs` (the row cell + drawer) **1,300**) put the
   same component in a different file at half the size — and both chapters are Wave 5 (01 → owner M; 04 → owners M
   *and* O), so it would have been settled by whoever wrote first. **Resolved below in "The track surface file plan
   (01 + 04 reconciled)", which is now the single authority for both chapters.** Missing files: see §9 tree gaps.
5. **§4.13's `Album.Page` demands `TrackFields.Row` for the whole list** — correct and exactly what this surface needs.
   But nothing in the plan says a **playlist** page must also demand the *edge payload* group (`AddedBy` → user profiles)
   before the Added-by lane can paint. Add `Entities.EnsureRows(edge.AddedBySlots, UserFields.Profile)` to §4.13's
   pattern, or the avatar column shimmers for the life of the page.
6. **Nothing in the plan covers the context-menu composition.** §4.12 hand-waves it as
   `Shell.ActionsFor(EntityKind.Track, style.Context(t))`. The grammar, the 10-row order, the conditions, the submenu
   shapes and the toasts are ~450 lines of `Menus.cs` + 255 of `TrackActions.cs` and they are the part users touch most.
   Wave 4 owner I builds the action table; owner M builds the **track** composition in `Track.UI.cs`, **Wave 4.5** — say so.
7. **Wave 5's alloc gate ("zero managed allocations on a scroll frame") is achievable only if `RowStyle` is re-pushed,
   not rebuilt.** 0.2.9 caches the width tracks by `(ColumnSet, art)` and the row shape by a memo. Without the same
   caching, every scroll frame rebuilds a `TrackSize[]`.

### Tree gaps

| What | Why | Proposed file |
|---|---|---|
| Expanded-row drawer (facts strip + versions + format button) | 836 lines today (`TrackFactsStrip` 296 + `TrackVersionsPanel` 411 + `FormatSplitButton` 129); no file in the master plan's §2 tree, and `04-detail-track-table.md` put it inside `Track.UI.cs` | `Entities/Track.Drawer.cs` (a named partial of `Track`, per §5's "30 % over budget → a named partial") — **and 04 §1.2/§9 defer to it**; see the file plan below |
| The detail table itself (chrome, header, command bar, filter flyout, list arms, drag, selection, recs) | 04's surface, but it hosts every row this chapter specifies and mounts the drawer; the master plan's §2 gives it no file at all | `Entities/Track.Table.cs` (04 §9's proposal, adopted here unchanged so the two chapters name one tree) |
| Drag payload, chip, rules, insertion preview | 938 lines today; §2's `Platform/` bucket lists only `Platform.cs Platform.Host.cs Modules.* Design.cs Controls.cs` | `Platform/Drag.cs` (CORE rules + the payload record + the chip resolver) |
| Row primitives shared by non-entity surfaces (Equalizer, SaveButton/FollowButton/PreSaveButton, SelectionCommandBar, RowSwipe, ExplicitBadge, MoreButton, ExpandChevron, SearchHighlight) | ~1,100 lines; §2 names `Platform/Controls.cs` with no budget | `Platform/Controls.cs` — this chapter's share is **900** of `00-design-system.md` §9.4's **4,500-6,000 envelope for the whole file** (arbitration 2026-09-12; the "~1,200-line budget" this row used to propose was smaller than the shares that must fit inside it — 02's own share alone is 3,200-3,800). The file is split on day one into `Controls.cs` / `Controls.Cta.cs` / `Controls.Art.cs` / `Controls.Picker.cs`; these primitives are `Controls.cs`'s |
| The 0.2.9 `Wavee.Tests/Actions/` suite (10 files: menu grammar, selection semantics, action rules, Spotify link, video-override UX) | §6 lumps these into "~150 files: port" without naming them | keep as `Wavee.Tests/Track.Menus.Tests.cs` + `Track.Rules.Tests.cs` |

### The track surface file plan (01 + 04 reconciled — the one authority)

`01-track-row.md` (the cell) and `04-detail-track-table.md` (the list that hosts it) describe **one** C# surface, and
until 2026-09-12 they described it with two incompatible file plans. This table replaces both chapters' file
assignments, and amends `wavee-0.3-implementation.md` §2's tree line `Track.cs  Track.UI.cs   300 + 1,500`. Nothing
below is a new decision: it is 01's drawer/drag/controls split and 04's `Track.Table.cs` split, made consistent, with
one number per file.

| File | Owns | 0.2.9 sources it absorbs | Lines | Owner | Wave |
|---|---|---|---|---|---|
| `Entities/Track.cs` **CORE** | the handle + columns + field groups, **and every pure rule this surface has**: lane table, tier + relief ladders, density/art ladders, sort cycle, filter model, reorder rules, membership diff, reveal ramp, list state, expanded-fact list and the shared number formats | `DetailTrackTableRules` 279 (incl. `TrackLane` 227-279) · `TrackExpandedFacts` 367 · `TrackFilterModel` 290 · `PlaylistReorderRules` 125 · `DetailTrackCommandBarLayout` 119 · `MembershipDiff` 119 · `DetailLayoutBreakpoints` 88 · `PlaylistListState` 41 · `DetailRevealRamp` 27 · `TrackRow`'s pure helpers (`PlaysLabel` :153, `ShowTempo` :578, `PadXFor`/`ColGapFor`/`RowHeightFor`/`ArtSizeFor` :118-130) · `DetailTracks.ArtCentreIndent` :641 | **1,200** | M | **4.5** |
| `Entities/Track.UI.cs` | the ROW and nothing else: `Grid` + the 11 lanes, the `#` state machine, both skins, the metadata line, the cells, `ArtCard`, the eager row, the 14 call-site variants of §1.2, and the **track menu composition** (§6.4) | `TrackRow.cs` 1,136 · the track slice of `Menus.cs` (~450) · `TrackContextMenu` 37 · `SearchHighlight` 81 (usage) | **2,600** | M | **4.5** |
| `Entities/Track.Table.cs` | the LIST: chrome, header, command bar, toolbar flyouts, tier/relief plumbing, the bound list and its arms, the reveal choreography, selection, drag hookup, recommendations, filters — **and the drawer's mount/keying/reflow animation** (the drawer *body* is the next row; the host is here) | `DetailTracks.cs` 4,525 · `TrackFilterFlyout` 472 · `DetailQueueActions` 85 | **2,600** | M (04's owner O *configures* via `TableProfile` from `Playlist.Page.cs` / `User.Page.cs`; O never edits this file) | **4.5** |
| `Entities/Track.Drawer.cs` | the expanded-row drawer BODY: facts strip, version rows, the gutter rail, the waveform, the format ladder, and the "View credits" modal's cold cache (§7 DATA GAPS 13) | `TrackVersionsPanel` 411 · `TrackFactsStrip` 296 · `FormatSplitButton` 129 · `TrackCreditsDialog` 123 | **700** | M | 5 |
| `Platform/Controls.cs` (this chapter's share) | the entity-free primitives: `Equalizer`, `SaveButton`/`PreSaveButton`/`FollowButton`/`FollowTextAction`, `SelectionBar`, `RowSwipe`, `ExplicitBadge`, `MoreButton`, `ExpandChevron`, `SearchHighlight` | `Equalizer` 206 + `EqualizerMotionPolicy` 28 · `SaveButton` 203 · `SelectionCommandBar` 394 · `RowSwipe` 147 · `NowPlayingMatch` 42 · `SearchHighlight` 81 | **900** — a share of `00-design-system.md` §9.4's **4,500-6,000 envelope** for the whole `Controls.cs`, **not** a file budget of its own (arbitration 2026-09-12); it files into the `Controls.cs` partial | L | 4 |
| `Platform/Drag.cs` | payload record, chip resolver, all drop rules, insertion preview | `WaveeResourceDrag` 596 · `WaveeDragRules` 210 · `PlaylistInsertionPreview` 80 · `WaveeDragChipModel` 52 | **700** | L | 4 |
| | | | **8,700 total** | | |

**Wave placement (settled, plan §5/§9.4):** `Track.cs`, `Track.UI.cs` and `Track.Table.cs` are **Wave 4.5** — the
single-owner slot (owner M alone) that opens when Wave 4 closes and gates before Wave 5 opens, alongside
`Entities/Detail.cs`/`Detail.UI.cs` (A1). Only `Track.Drawer.cs` is Wave 5, alongside the rest of M's per-kind pages
(`Album.*`, `Show.*`, `Episode.*`). This is *not* a change to the file plan or the 8,700-line total above — only to
when M writes which of the four files.

Three consequences, each of which was previously ambiguous:

1. **`Track.Drawer` lives in `Track.Drawer.cs`.** `04-detail-track-table.md` §1.2's row `TrackVersionsPanel +
   TrackFactsStrip → Track.Drawer(...) in Track.UI.cs` and its §9 `Track.UI.cs` (the row cell + drawer) **1,300** are
   superseded; 04's row-cell share is a *view onto* `Track.UI.cs` (2,600), not a second budget for it.
   The reason the drawer is not in the row file is structural, not size: the drawer has exactly ONE call site and it is
   the **table**, not the row (`Ctx.Provide(TrackVersionsPanel.Props, model, Embed.Comp(…))`, DetailTracks.cs:3347-3348;
   `grep` finds no other caller). A drawer in `Track.UI.cs` would give the row file a dependency the row does not have.
2. **The naive sum of the two chapters' honest estimates (01's 4,350 + 04's 5,100 = 9,450) double-counts.**
   `Track.UI.cs` was counted twice (2,600 + 1,300) and `Track.cs` twice (+350 inside 04's 1,200). De-duplicated the
   track surface is **8,700 lines**, against the master plan's **1,800**. That is the number to put in front of the
   orchestrator before Wave 5 is scheduled — not 9,450, and certainly not 1,800.
3. **One owner per file.** Both chapters originally named owner M for the row and Wave 5 for everything; 04
   additionally named owner O for playlist/liked. Splitting `Track.Table.cs` by page would have produced two table
   implementations behind one `TableProfile`. M owns all four `Track.*` files, across two slots: `Track.cs`,
   `Track.UI.cs` and `Track.Table.cs` in **Wave 4.5**, `Track.Drawer.cs` in **Wave 5**. O's playlist/liked work is a
   profile + a page, and lands in Wave 5 **after** M's Wave 4.5 table closes. L's `Controls.cs` and `Drag.cs` are
   Wave 4 and must be done before Wave 4.5 starts.

### Line budget

Per-chapter view (this chapter's files only). The authoritative, de-duplicated plan is the table immediately above.

| | Lines |
|---|---|
| 0.2.9, this chapter's owned files | 4,049 (`Components/*` 3,071 + `Features/DragDrop/*` 938 — note `TrackRow.cs` alone is 1,136) |
| 0.2.9, shared rules/actions this surface cannot exist without | ~1,100 (`DetailTrackTableRules` 279 + `TrackExpandedFacts` 367 + `TrackActions` 255 + `ActionIcons` 87 + `TrackContextMenu` 37 + `SearchHighlight` 81) + ~250 of `Menus.cs` |
| 0.2.9 total | **≈ 5,400** |
| Master plan §2 target | `Track.cs` 300 + `Track.UI.cs` 1,500 = **1,800** (plus an undeclared share of `Platform/Controls.cs`) — and that same 1,800 is all `04-detail-track-table.md` gets for the table |
| Honest estimate | **≈ 4,350**: `Track.UI.cs` 2,600 (row grid + 14 call-site variants + cells + menu composition) · `Track.Drawer.cs` 700 · `Platform/Controls.cs` 900 (this chapter's share) · `Platform/Drag.cs` 700 · `Track.cs` CORE +350 (lane table, tier + relief ladders, formats — a *share* of the file plan's 1,200, not a second file) |
| With 04's table | **8,700** for the whole track surface — see the file plan above. Do not add 01's 4,350 to 04's 5,100: `Track.UI.cs` and `Track.cs` appear in both. |

The gap is not slop. It is: 11 optional lanes with tier **and** relief gating (≈500), two skins (≈300), the `#` cell
state machine (≈150), the drawer (≈700), the drag layer (≈700), the menu composition (≈450), and the 14 call-site
variants that today share one cell (≈400). Cutting to 1,800 means cutting lanes, and the owner will see it in the first
side-by-side.

---

## 10. Parity checklist

Verify side by side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
"Static capture" = a screenshot with the pointer parked off the window. "Hover capture" = pointer held on the named row
for ≥1 s. "Frame recording" = a ≥3 s capture at ≥60 fps, frame-differenced.

| # | Item | How to verify |
|---|---|---|
| 1 | Row height is 48 at the default density, 40/56/64 at Compact/Cozy/Comfortable | `wavee://album/<id>`, window 1400, Settings → Appearance → Row density; static capture per rung; measure the row pitch |
| 2 | Art is 32/32/40/48 across those four rungs and always leaves ≥8 DIP above and below | same four captures, measure the art box |
| 3 | Classic row style gives 36/40/44/48 with art 32 (Comfortable 40), no subline, no art column on a playlist | Settings → Appearance → Track list style = Classic; `wavee://playlist/<id>` @ 1400 |
| 4 | Grid padding is `PadX − RowInset` = 8 and the first column starts 16 DIP from the row-skin edge, aligned under the header's "#" | `wavee://playlist/<id>` @ 1400, static capture; overlay the header and the first row |
| 5 | Column gap is 12 at tier ≤4 and 8 at tier 5-6 | resize the window from 1400 → 320; static captures at 900, 400, 320 |
| 6 | Lane presence crosses at 860 / 720 / 560 / 440 / 340 / 300 with 24 DIP hysteresis (drag narrow then wide, no flicker) | `wavee://playlist/<id>`, slow window drag 1000 → 280 → 1000; frame recording |
| 7 | The relief ladder drops Plays before BPM·key before Added-by before Date before Album | `wavee://liked` with the Plays and BPM columns enabled @ 1400, then narrow in 40 DIP steps; static capture each step |
| 8 | Title never falls below ~120 DIP; the row never shows a one-glyph title beside a half-empty Plays lane | the same sequence; measure the title lane |
| 9 | `#` shows the track number in Caption 12/16 TextTertiary at rest | static capture, `wavee://album/<id>` |
| 10 | Hovering ANY part of the row swaps the number for a 24 DIP play button and lifts the "…" from 0.45 to 1.0 | hover capture on row 3; compare with a static capture of the same row |
| 11 | The hover reveal survives moving the pointer from the title onto the play button (no flicker) | frame recording of a slow pointer sweep across one row |
| 12 | The now-playing row shows a 3-bar equalizer (2.5 × 13, gap 2), accent ink, and an accent marquee title | play a track on `wavee://album/<id>`; frame recording (bars must move) + static capture |
| 13 | Pausing settles the bars flat at 0.4 and the title stays accent | pause; static capture |
| 14 | Hovering the now-playing row swaps the equalizer for a **Pause** glyph in accent | hover capture |
| 15 | The equalizer stops ticking while the row is hovered (no presents attributable to it) | hover the now-playing row for 3 s; frame recording — the `#` cell region must be byte-identical across frames |
| 16 | Reduced motion settles the bars into a fixed NON-uniform shape (not flat, not looping) | Windows Settings → Accessibility → Visual effects → Animation effects Off; play a track; frame recording |
| 17 | A starting track shows a 16 DIP indeterminate ring in accent, whether or not hovered | click a row's play button, capture within ~300 ms |
| 18 | The album top track shows an 11 DIP filled star at rest | `wavee://album/<id>` with the Plays column on; static capture |
| 19 | Chart rows show ▲ green / ▼ red / "NEW" green at 8/12/700 beside the number, and nothing for Equal/Unknown | `wavee://playlist/37i9dQZEVXbMDoHDwVN2tF` (a Top-50 chart) in `--fake`; static capture |
| 20 | The heart is painted on **every** row: outline TextTertiary unsaved, filled AccentTextPrimary saved | `wavee://liked` and `wavee://album/<id>`; static capture |
| 21 | Clicking the heart pops the filled glyph in with an overshoot spring (scale 0.25 → 1, blur 2 → 0) | frame recording of one click on an unsaved row |
| 22 | Scrolling a list with saved rows never replays the pop | frame recording of a 2 s flick through `wavee://liked` |
| 23 | The explicit badge is a 14 DIP box, 2 DIP corner, 1 DIP TextTertiary border at 0.6 opacity with a 10 px "E" | `wavee://playlist/<id>` with explicit tracks @ 900 (Album column folded → the badge appears in the subline); static capture, zoom 400 % |
| 24 | Classic shows the full "EXPLICIT" word-mark pinned to the Title cell's trailing edge | Classic style, same playlist; static capture |
| 25 | The trailing lane shows a 13 DIP film glyph on rows that have a video and the quiet "…" on those that do not | `wavee://album/<id>` for an album with videos @ 1400; static capture |
| 26 | Hovering that row turns both into the full-strength "…" | hover capture of a video row and of a non-video row |
| 27 | Clicking the "…" opens exactly the same menu, at the same anchor, as a right-click on the row | click the "…" and right-click the row; two static captures, overlay them |
| 28 | The BPM cell is `[6 DIP swatch] 101.5 · 8B`, swatch at 0.85 opacity, and renders **empty** (not "—") before kind 222 lands | Settings → enable the BPM column; `wavee://liked` @ 1400; capture immediately after navigation and again after 5 s |
| 29 | A not-yet-out row dims its title column to 0.45, dashes Plays, shows "4 Sep" in the duration lane, and has **no** hover play button | `wavee://album/<prerelease-id>` in `--fake`; static + hover capture |
| 30 | A name-less album ref renders "—" in TextTertiary and is still clickable | `wavee://artist/<id>` → the chart rows (their wire has no album name); static capture + click |
| 31 | The Added-by cell is a 24 DIP PersonPicture + 8 DIP gap + a Caption name | `wavee://playlist/<collaborative-id>` @ 1400; static capture |
| 32 | Zebra striping is on odd rows only, and hovering a striped row is visibly *stronger* than the stripe | `wavee://liked` @ 1400, light **and** dark theme; static + hover captures of rows 2 and 3 |
| 33 | Pressing a row changes only the fill — the row never scales or blurs | frame recording of a press-and-hold on a full-width row |
| 34 | Single click selects (pill appears); double click plays | `wavee://playlist/<id>`; click then double-click row 4; static captures |
| 35 | The selection pill is 3 × 16, r 1.5, 2 DIP from the row edge, in the page accent | static capture, zoom 400 % |
| 36 | Turning on multi-select slides a 28 DIP check lane in over 333 ms and pushes the content right by the same 28 | frame recording of the Select toggle |
| 37 | The selection command bar shows labels ≥760, glyphs 390-759, essentials <390, and the count text cross-fades on change | select 2 rows; resize 1400 → 700 → 360; three static captures; frame recording of selecting a 3rd row |
| 38 | Up to 3 deduped 28 DIP thumbs overlap by 11 DIP with a 2 DIP FillCardSecondary ring | select 4 rows @ 1400; static capture |
| 39 | The single-track context menu has the exact 4-command strip and the row order of W19 | right-click a playlist row; static capture; compare row-by-row |
| 40 | A multi-selection collapses Share to "Copy links (n)" and drops Go-to-album / Go-to-artist / credits / radio / Video | select 3 rows, right-click inside; static capture |
| 41 | An episode row offers "Go to podcast" instead of "Go to album", and its subline carries the EPISODE eyebrow | `wavee://playlist/<id>` containing an episode; static capture + right-click |
| 42 | Add-to-playlist lists **10** playlists, most-recently-filed first, then a separator, then "More playlists…" | file into a playlist, then reopen the menu on another track; two static captures |
| 43 | The expand chevron opens a drawer whose connector rail lands on the **art centre**, not the title | click the chevron on a playlist row @ 1400; static capture, measure the rail x against the art box |
| 44 | The drawer shows 4 hero numerals (Plays / BPM / Key / Duration) at 28/36 over Caption labels, then one prose line, then one genre line — **no tiles, no pills, no rules** | static capture of an expanded row |
| 45 | Late facts (tempo, plays) fade up and FLIP their neighbours instead of snapping them narrower | expand a row immediately after navigating; frame recording of the next 5 s |
| 46 | The drawer reserves the music-video row before the fetch lands, and the drawer height never jumps when it arrives | expand a row with a video on a cold page; frame recording |
| 47 | The version rows are 51 DIP with a 43 square audio thumb / 76 × 43 video thumb + a 22 DIP white play badge | static capture of an expanded row with a video |
| 48 | The format split button is 30 + 20 × 28 with a 4 DIP outer radius and a flat inner seam | static capture, zoom 400 % |
| 49 | Dragging a row dims it to 0.4 in place and shows a 280-wide opaque chip with art 40, title, subtitle and the resting verb | start a drag from `wavee://liked`, hold; static capture mid-drag |
| 50 | A multi-row drag stacks two 4-DIP-offset cards behind the chip and shows a count badge at the top-trailing corner | select 4, drag; static capture |
| 51 | The chip's pickup tilt is a **flash** — 4° and 1.02 scale settling to flat within ~150 ms, not a held pose | frame recording of the first 400 ms of a drag |
| 52 | Dragging over an editable playlist opens a gap with ≤3 accent-bordered preview cards, the last carrying "+N" | drag 5 selected rows over another playlist's track list; static capture |
| 53 | A same-list reorder does **not** dim the app; a cross-list deposit does | two drags, two static captures |
| 54 | Dragging onto a sorted playlist refuses with "Clear sorting to reorder" plus the blocked glyph | sort by Title, then drag a row within the list; static capture |
| 55 | Dragging an artist card onto a playlist refuses with "Can't add an artist" | drag an artist card from Home onto a sidebar playlist; static capture |
| 56 | Dragging a queue row over a playlist shows no accept cue at all and carries "Drag to reorder" | open the queue, drag a row over the sidebar; static capture |
| 57 | The Home "Top tracks" module renders 5 eager rows at 48 DIP with the same hover transport and equalizer as the detail table | `wavee://home` (artist module); hover capture of row 2 |
| 58 | The artist page "Popular" chart row is 56 DIP with a 44 art (Classic: 48/40) and stacks the play count on a third line below ~340 DIP | `wavee://artist/<id>`; resize the chart column; two static captures |
| 59 | Recents single rows are 64 DIP, drawer children 40 DIP with a played-at caption before the "…" | `wavee://recents`; expand a group; static capture |
| 60 | The now-playing panel's "Next up" rows are ArtCards with 40 art, artists, no duration and no heart | open the right rail → Now playing; static capture |
| 61 | The Settings density preview mirrors the real geometry at exactly 0.25 scale | Settings → Appearance; static capture; compare the preview's row:art ratio with W7 |
| 62 | A touch swipe right on a queue row reveals Save; swipe left reveals Play after (or a red Remove) | requires a touch device: swipe both directions; frame recording |
| 63 | Nothing on a track row is lost between light and dark except the Camelot swatch's darkening | toggle the theme on `wavee://liked` with the BPM column on; two static captures |
| 64 | Turning OFF track artwork removes the art COLUMN (not just the picture) on the detail table, Home top tracks, Recents and the artist chart — no reserved empty gutter anywhere | Settings → Appearance → hide track artwork; static captures of `wavee://album/<id>`, `wavee://home`, `wavee://recents`, `wavee://artist/<id>` |
| 65 | With artwork off, an ArtCard row (Next up / module playables) shows a 32 DIP square Play button where the cover was — the play verb is not lost | right rail → Now playing; static + hover capture |
| 66 | The hero page layout's STACKED flow uses plain rows — margin 0, corner 0, no border, no zebra — while its ROW flow keeps the pill | Settings → Appearance → Page layout = Hero; `wavee://album/<id>` at ~600 and at ~1200; two static captures |
| 67 | Dragging an `.mp4` over a track row lights that row (and only that row) with the hover fill; releasing attaches the video; dragging a `.flac` over it plays the file instead of being swallowed | drag a local mp4 from Explorer onto row 3; frame recording; repeat with an audio file |
| 68 | Pausing the now-playing row settles the bars FLAT (0.4), while reduced motion settles them into the non-uniform shape — the two are visibly different | pause with animations ON, static capture; then animations OFF, play, static capture; compare |
| 69 | The selection bar's first frame at a wide window is the GLYPH tier, then labels — and it never flickers back | select 2 rows at 1400; frame recording of the first 500 ms |
| 70 | Running any selection-bar command (Play, Save, Play next, a ⋯ row) clears the selection; "Select all" does not | select 3 rows, click Save; then select 3 and click Select all; two frame recordings |
| 71 | The ⋯ overflow of the selection bar carries the SAME rows as a row right-click, minus Go-to-album, plus Select all — and at <390 also Play next / Add to queue / Save | select 3 rows at 1400 and at 360; open ⋯ in both; compare with a row right-click |
| 72 | The version row's caret opens a RADIO list of real formats with bitrates + "Use my default quality", and does nothing at all on a track with no format ladder | expand a row with alternates, click the caret; then a local/module track, click the caret |
| 73 | A version row that is currently playing shows the now-playing overlay on its thumb and an accent title — not the static white play badge | play the music video from a drawer; static capture |
| 74 | "View credits" opens four shimmer bars, then role-grouped credits with linkable accent names, and "No credits available" for a track with none | right-click → View credits on a major-label track, then on a local file; capture at ~200 ms and at ~2 s |
| 75 | A cover-less drag (a tab, a folder) shows the kind GLYPH tile in the chip, and a Liked Songs drag shows its own mosaic | drag a tab onto the sidebar; drag Liked Songs onto a folder; two static captures |
| 76 | Holding a drag still over a collapsed sidebar folder opens it after ~500 ms, and merely crossing it does not | one slow sweep, one hold; frame recording |
| 77 | Dragging an entity card (no track snapshot) over a playlist shows ONE preview card with a music-note tile and no "+N" | drag an album card from Home over a playlist's track list; static capture |
| 78 | A Classic row below 440 DIP states nothing about a video (no inline glyph, no trailing lane) — and the "…" still opens the full menu | Classic style, an album with videos, resize to ~400; static capture + click the "…" |
| 79 | Classic's Added-by cell is a bare name with no avatar; Modern's is avatar + name | Classic vs Modern on `wavee://playlist/<collaborative-id>` @ 1400; two static captures |
| 80 | Home "Top tracks" shows five row-shaped skeletons before the overview lands — never a spinner, never an empty block, and the rows do not jump when it arrives | `wavee://home`, expand an artist row on a cold start; frame recording |
| 81 | A rootlist multi-select drag over a playlist offers NO deposit cue (it is an organisation gesture), while a single playlist drag does | select 3 sidebar playlists, drag over another playlist's track list; then drag one; two static captures |
| 82 | Dragging a row out of playlist P onto P's OWN tab shows no accept cue | open P in a tab, drag a row onto that tab; static capture |

---

## 11. Audit log

Adversarial re-read of every assigned 0.2.9 source against §§0-10, 2026-09-12. `wrong` = a stated value or claim the
code contradicts · `missing` = a state, element, motion, rule or number the chapter never mentioned ·
`unverified` = a citation that does not resolve as written · `overclaim` = stated more confidently than the code supports.

| # | Section | Kind | Correction |
|---|---|---|---|
| 1 | §1.1 tree | **wrong** | "TrackActions.* (11 AppAction singletons)" — there are **13** (Play, PlayNext, AddToQueue, ToggleLike, CopyLink, GoToAlbum, GoToArtist, GoToSongRadio, ViewCredits, CopySpotifyUri, OpenInSpotifyWeb, RemoveFromThisPlaylist, RemoveFromQueue). Enumerated in place. |
| 2 | §3 tokens | **unverified** | Ten rows cite a file `TrackLane.cs` that does not exist. `TrackLane` is a second static class at the bottom of `Features/Detail/DetailTrackTableRules.cs` (227-279); the line numbers are right, the filename was invented. A source note now says so. |
| 3 | §0, §2 | **missing** | The **third row skin**. `plainRows` (DetailTracks.cs:3484) — the hero page layout's stacked flow drops margin, corners, border and zebra entirely. Added as non-negotiable 16 and wireframe W12b. |
| 4 | §0, §2 | **missing** | **Hide track artwork** removes the art LANE, not just the picture, and every eager caller keeps a second `(ColumnSet, TrackSize[])` pair for it. Added as non-negotiable 17, a note under the call-site table, and wireframe W28. |
| 5 | §2 W23 | **wrong** | "the row's own Margin is LIFTED onto the wrapped child" — `LiftMargin` (RowSwipe.cs:60-62) does the opposite: it strips the margin OFF the child and hands it to the `SwipeControl`. Corrected, with the reason. |
| 6 | §2 W14 | **wrong** | Drawer `Indent` is `max(0, ArtCentreIndent(set, art) − TrackVersionsPanel.RailOffset 7)` (DetailTracks.cs:3313), not `ArtCentreIndent` itself — the rail's own x is backed off so the LINE, not the gutter edge, lands on the art centre. The derivation is now spelled out. |
| 7 | §1.2 call sites, row 11 | **wrong** | Queue: `QueueArt` is **34**, not 44; `RowExtent` is 44; the queue row's heart lane is 26 × RowExtent and the now-playing card's is 30 × 44. Corrected (the detail stays with ch 21). |
| 8 | §2 W12 | **missing** | The **`.mp4` drag-over cue** — the hovered row takes `RowHover` through the same bound `Fill` closure the zebra uses, built only when a curation service exists; a non-video drop falls through to the shell. Added as W12c and a §6.1 row. |
| 9 | §2 W13 | **missing** | The selection bar's **pre-measure fallback**: `effective = lane > 0.5 ? lane : 720` (SelectionCommandBar.cs:69) — the first composed frame is always fit 1, never fit 0. |
| 10 | §2 W13, §6.5 | **missing** | **Every selection command exits the selection** (`ActionButton` :202 and `WithExit` :247, recursing into submenus). Select all is the one exception. |
| 11 | §2 | **missing** | The selection bar's **⋯ overflow composition** (fit-2 transport rows + separator + `Menus.TrackRows(showGoToAlbum:false)` + separator + Select all, BottomEdgeAlignedRight). Added as W13b. |
| 12 | §2 W14 | **missing** | The **format ladder** the caret opens: radio items `"{Label}   {n} kbps"`, disabled when `!AvailableOnDevice`, plus `detail.versions.useDefaultQuality`; ZERO formats (or a failed fetch) → the caret does nothing at all. |
| 13 | §2 W14 | **missing** | A version row that IS the now-playing item: accent title + `NowPlayingOverlay` (fab `clamp(min(w,h)×0.62, 22, 28)`) replacing the static white badge. |
| 14 | §2 W14 | **missing** | The three kind labels leading each version's meta line — `This track` / `Music video` / `Alternate audio` — and the fixed self → video → audio order. |
| 15 | §2, §3 | **missing** | **W27, the whole "View credits" modal** (loading skeleton, role-grouped list, linkable accent names, roles, source line, `menu.noCredits`, the two-source 186/NPV fallback and its "both must settle" rule, 360/440/420 geometry). The chapter previously named the file and nothing else. |
| 16 | §2 W26 | **missing** | Four more empty/error states: the drawer's swallowed fetch failure (self row only, no error UI), `TrackFactsStrip` returning a bare box on zero facts, `PreSaveButton` rendering nothing when kind 138 answers null, and the album drawer's READY-BUT-EMPTY note with Retry. Plus Home top tracks' five row-shaped skeletons. |
| 17 | §2 W3 | **missing** | The **paused** now-playing row as its own state (bars flat at 0.4, NOT the reduced-motion shape; hover shows Play, not Pause), and the "disable marquee" setting keeping the accent title plain. |
| 18 | §2 W8 | **missing** | Classic's inline film glyph drops at tier ≥ 4 (`ClassicInlineVideoDropTier`) and Classic has no trailing video lane at any tier — so below 440 a Classic row states nothing about a video. Also: Classic Added-by has **no avatar**, and the non-artist Classic title branch uses Icon(Movie, **12**) rather than the span run's icon-font glyph. |
| 19 | §2 W21 | **missing** | The chip's **kind-glyph tile** when no cover is in hand (six-way map), the pre-built Liked Songs chip art, the second chip kind (the sidebar customizer's palette chip) riding the same resolver, and `SpringLoadMs` **500**. |
| 20 | §2 W21 | **missing** | The insertion preview's **no-track card** (music-note tile, payload name, no "+N") and its `showArtwork:false` variant; and that `Cap` is `SortableMath.DefaultPreviewCap`, deliberately the framework's number. |
| 21 | §2 W23 | **missing** | `TouchInput` detail: the `SM_MAXIMUMTOUCHES` probe fails SAFE, reading the latch subscribes the render, `WrapBound` re-reads icon/label/enabled per invocation, `resetKey` snap-closes on recycle — and that the belt is **currently flagged OFF on the virtualized detail rows** (eager lists only). |
| 22 | §5 | **missing** | The check lane's **Exit** leg (symmetric, Dx −28 / Opacity 0, 333 ms); the 83 ms `Interaction.Subtle` brush ladder as its own row; `ScaleStandard` 1.04/0.96 on Follow/PreSave; swipe reset-on-recycle; and that a hero `Stat` is keyed by KIND so a changed value cross-swaps while a new fact fades up. |
| 23 | §2 W1 | **overclaim** (resolved, not an error) | W1's 150/112 at 880 looked to contradict W9's "Plays relieved at 880". It does not — W1's set has Tempo OFF (min 828), W9's example includes the 80-DIP Tempo lane (min 920). The arithmetic for both is now written into W1 so the next reader does not "fix" a correct number. |
| 24 | §6.4 | **wrong** | "Go to podcast" is gated on `showGoToAlbum` as well as `CanGoToPodcast` (Menus.cs:97) — an album page suppresses both. |
| 25 | §6.4 | **missing** | The single-artist row has **two shapes**: the `GoToArtist` singleton when the navigable artist is the primary, else a bespoke row carrying that artist (the singleton always navigates to `Artists[0]`). |
| 26 | §6.4 | **missing** | The deposit submenu is ONE shared shape; "New playlist" mints `My Playlist #N`; the submenu is disabled rather than absent when `canAdd` is false; `More playlists…` additionally needs an overlay service. |
| 27 | §6.4 toasts | **missing** | The **song-radio** toast, the create-then-add **Open** variant, the rootlist move toasts with Undo, the `PlaylistEditErrors.Toast(ex, verb)` failure mapping, and the clipboard-failure toast. Confirmed `detail.goToPlaylist`'s text is literally "Open". |
| 28 | §6.5 | **missing** | "Select all" is a contiguous `SelectRange(first, last)` after `DeselectAll`, not a per-index selection; the count scan skips non-track rows. |
| 29 | §6.6 | **missing** | A rootlist multi-select of ≥2 carries **no** track resolver; `TabDropRules.AcceptsDeposit`'s same-playlist exclusions; the PRE-move insertion-index convention; the container-on-itself silent no-op. |
| 30 | §7 DATA GAPS | **missing** | Four more gaps: track credits (kind 186 ∪ NPV), the audio-format ladder **and its per-uri override**, the "in your top N" set, and the per-row hover signal (which drives PointerBit and the EQ pause together and must come from the interactive ancestor). |
| 31 | §8 | **missing** | Eight pure rules with no row: `PlaysLabel`, `ShowTempo`, the `PadXFor`/`ColGapFor`/`RowHeightFor`/`ArtSizeFor` quartet, `ArtCentreIndent` (untested), the payload predicates, `PlaylistInsertionPreview.Cap`, `WaveeRootlist.IsMember`/`CanEditPlaylist`. |
| 32 | §8 | verified | `SelectionCommandBar.FitFor`, `SearchHighlight.LineBoxFor` and `TouchInput.SwipeArmed` really are untested (no test file references them); `DetailTrackCommandBarLayoutTests` is the browse command bar, not this one. Every other test file and line count in §8 checks out. |
| 33 | §10 | **missing** | 19 parity items (64-82) for the states above. |
| 34 | header, §1.2, §9 | **wrong** (contradiction between chapters) | critic-fix: the chapter contradicted **itself and chapter 04** about where the expanded-row drawer lives and how big `Track.UI.cs` is. §1.2 said `Track.Drawer` is in `Track.UI.cs`; §9 tree gaps proposed `Entities/Track.Drawer.cs` (700) with `Track.UI.cs` at 2,600; `04-detail-track-table.md` §1.2 put `Track.Drawer(...)` in `Track.UI.cs` and §9 budgeted that file at **1,300** for "the row cell + drawer" — same component, two files, a 2× size gap, and both chapters land in Wave 5 (01 → owner M; 04 → owners M **and** O), so it would have been settled by whoever wrote first. Resolved by a new §9 subsection, **"The track surface file plan (01 + 04 reconciled — the one authority)"**: `Track.cs` CORE 1,200 · `Track.UI.cs` (row only) 2,600 · `Track.Table.cs` 2,600 · `Track.Drawer.cs` 700 · `Platform/Controls.cs` 900 share · `Platform/Drag.cs` 700 = **8,700**, one owner per file (M owns all four `Track.*`; O configures the table through `TableProfile` and never edits it; L lands `Controls.cs`/`Drag.cs` in Wave 4). The header's "0.3 target" line, §1.2's drawer row and §9 item 4 now all point at it, and it is stated as the amendment to `wavee-0.3-implementation.md` §2's tree line `Track.cs  Track.UI.cs   300 + 1,500`. |
| 35 | §1.2, §9 | **wrong** + **missing** | critic-fix: while reconciling #34, two facts about the drawer were checked against source and corrected in §1.2. (a) Its props do **not** freeze at mount as a handle + options pair — 0.2.9 pushes a `Model` through a **context** (`internal static readonly Context<Model?> Props` TrackVersionsPanel.cs:47, `UseContext(Props)` :81, `Ctx.Provide(...)` DetailTracks.cs:3347), so "Live via" is `Ctx` + `Key`, not `Key` alone; the three keys are `"drawer:"`/`"drawer-presence:"`/`"drawer-body:" + rowKey` (DetailTracks.cs:3316, 3342, 3348). (b) The drawer's only call site in the whole app is the detail table (`grep` for `TrackVersionsPanel` finds `DetailTracks.cs:3293-3348` and nothing else), so the row file must not own it — which is the structural reason the file plan puts the body in `Track.Drawer.cs` and its mount/keying/reflow in `Track.Table.cs`. |

**Verified correct and left alone** (spot-checked against source): every number in §3's lane table
(28/28/32/132/88/52/80/52/28/40/26, stars 1/0.75/0.75, floors 120/90/90); the 40/48/56/64 and 36/40/44/48 density
ladders and the 32/32/40/48 art ladder; `PadXFor`/`ColGapFor`; the 860/720/560/440/340/300 tier ladder and its
24 DIP hysteresis; the relief yield order and `MaxRelief 8`; W9's worked arithmetic (756 → 920, 704 → 856);
`MoreRestOpacity 0.45`; the explicit badge's 14/2/10 exceptions; the equalizer's 2.5 × 13, gap 2, corner 1.25,
850 ms loop, 33.3 ms tick, five-key patterns and `u = 0.3` still shape; the heart spring (0.30 / 0.55, Sx 0.25,
`Expressive.BlurSmall` 2); the 280 ms `FluentDecelerate` reveal; the 333 ms check lane (`MotionTok.DisclosureExpand`);
the pill 3 × 16 / r 1.5 / margin 2 / press 10⁄16; every DragChip constant (280 / 40 / 4° / 1.02 / 150 ms / 4 / E733 /
8-8-12-8 / gap 10); `Elevation.Flyout` and `Elevation.Card` per theme; `Spacing`/`Radii`/`Expressive`/`ScaleTier`
resolutions; the glyph codes (Heart EB51, HeartFill EB52, Play E768, Add E710); every `drag.*`, `menu.*` and
`detail.*` loc key quoted; the menu grammar and its 10-row order; and the
`TrackLane`/`Relieve`/`MinWidthFor`/`NominalReliefFor`/`ReliefFor` contracts.

**token-reconcile (2026-09-12):** §3's Home "in your top N" badge row gave `Radii.FullAll` the value **16**. `Radii.Full` is **999**, clamped to half the box at record time (`Dsl/Radii.cs:15, 21`); 16 is `Radii.Pill`. `HomeModules.Artists.cs:350` does take `Radii.FullAll`, so only the number was wrong — corrected to "999, clamped to half the 24-DIP badge ⇒ 12". Everything else this chapter cites re-verified against the token layer and left alone: `ScaleSubtle` 1.02/0.98, `ScaleStandard` 1.04/0.96, `ScaleEmphatic` 1.07/0.92, `WaveeMotion.Faster/Fast/Standard` 83/167/250, `MastheadStaggerMs` 45, `StaggerMs` 40, `Radii.Control` 4 / `Radii.Card` 8 / `Radii.PillAll` 16, `MotionTok.DisclosureExpand` 333 `FluentPopOpen` and `DisclosureChevron` 167 `cubic-bezier(0.167,0.167,0,1)`, `Expressive.BlurSmall` 2, and the `TrackLane` floors 120/90/90 and fixed lanes 28/52 (`DetailTrackTableRules.cs:235-278`). The full index is `00-design-system.md §12.1`; this chapter's re-authored lane numbers are §12.2's last row.

**token-reconcile (2026-09-12):** §5's multi-select rows (`:1019, 1020, 1032`) give the check-lane slide as a bare **333 ms / −28 DIP**. Both numbers are NAMED in the engine — `SelectorVisualsBound.MultiSelectAnimMs = 333f` and `CheckboxContentOffset = 28f` (`FluentGpu.Controls/SelectorVisualsBound.cs:59, 61`) — and the app re-authors the 333 raw at `TrackRow.cs:569`. Left as written (the numbers are correct) and recorded instead as a magic-number row in `00-design-system.md §12.2`, with the warning that this 333 pairs `FluentDecelerate` and is **not** `MotionTok.DisclosureExpand`'s 333 on `FluentPopOpen`.

**arbitration (2026-09-12):** `Platform/Controls.cs` gets one budget, and it is not this chapter's. §9's tree-gap row proposed "declare a ~1,200-line budget" and the file plan's share row read "**900** (of a ~1,200 file budget)" — but `02-cards-and-controls.md`'s share of the same file is **3,200-3,800** on its own, so 900 + 3,200 never fitted in 1,200. Settled: **`00-design-system.md` §9.4's 4,500-6,000 is the envelope for the whole file**, and this chapter's 900 (equalizer, save/pre-save/follow buttons, selection bar, row swipe, explicit badge, more button, expand chevron, search highlight) is a share *inside* it, as is 02's 3,200-3,800 and 00's own ≈ 1,100 — the three are not summed into a new file number. The day-one named partials are fixed at four: `Controls.cs`, `Controls.Cta.cs`, `Controls.Art.cs`, `Controls.Picker.cs`; this chapter's primitives are `Controls.cs`'s. Changed: the §9 tree-gap row and the file plan's `Platform/Controls.cs` row. The §9 file plan's own totals are unaffected — 900 was already the number, only its denominator was wrong — so the track surface's **8,700** stands.

**consistency 2026-09-12:** header, the reconciled file plan and §9 items 4/6 put the whole track surface in Wave 5. The plan puts `Track.cs`, `Track.UI.cs` and `Track.Table.cs` in **Wave 4.5** (owner M, the shared detail-frame slot, gated before Wave 5 opens) — only `Track.Drawer.cs` stays Wave 5. Header, the file-plan table (new Wave column) and consequence #3 corrected; a note was added under the table stating the 8,700-line total and per-file assignment are unchanged, only the wave placement of three of the four files moved. **Also repaired a markdown defect**: W14's code fence (opened before the drawer wireframe) was never closed and swallowed the "### W15" heading and W15's own opening fence as plain text. Closed W14's fence immediately before the W15 heading; all 32 fence pairs in this file now balance (64 fences total, verified by toggling OPEN/CLOSE line by line).
