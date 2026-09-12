# Playlist page (owner/follower/editorial/chart/radio, inline edit, reorder, picker) — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Detail/PlaylistInlineEdit.cs` (1175) · `PlaylistPicker.cs` (215) · `PlaylistDepositTargets.cs` (177) ·
> `PlaylistEditErrorKinds.cs` (82) · `PlaylistEditErrors.cs` (30) · `PlaylistListState.cs` (40) · `PlaylistPageNoticeRules.cs` (94) ·
> `PlaylistReorderDefer.cs` (68) · `PlaylistReorderRules.cs` (125) · `PlaylistTuneMenuModel.cs` (42) · `CollaboratorFacePile.cs` (162) ·
> `DetailNoticeBar.cs` (89) · the playlist branches of `DetailConfig.cs` (311) / `DetailPage.cs` (757) / `DetailShell.cs` (858) /
> `DetailRail.cs` (607) / `DetailVerticalHero.cs` (579) / `DetailTracks.cs` (4525) · `Features/DragDrop/PlaylistInsertionPreview.cs` (80) /
> `WaveeDragRules.cs` (210) / `WaveeResourceDrag.cs` (596) · `Actions/PlaylistCreateFlow.cs` (127) / `ContainerActions.cs` (364) /
> `FolderActions.cs` (285) / `Menus.cs` (deposit submenu) · `Components/FlipCountdown.cs` (172) / `SaveButton.cs` (203) ·
> `Features/Detail/PlaylistEditErrors.cs` (30) / `ContextBandLayout.cs` (179) / `DetailRailPolicy.cs` (67) /
> `DetailLayoutBreakpoints.cs` (88) / `DetailVerticalLayout.cs` · `Features/Detail/LikedFactsPanel.cs` (1756, the
> `LikedFacts.Has/Panel` seam) | 0.3 target: `Entities/Playlist.UI.cs`, `Entities/Playlist.Page.cs` | Wave 5 owner O
>
> Shared parts are specified elsewhere and only CONFIGURED here: `00-design-system.md` (tokens, type ramp, cover palette,
> motion curves, CTAs), `01-track-row.md` (row, heart, selection bar, drag chip), `03-detail-frame.md` (shell, rail, vertical
> hero, context band, skeleton, reveal, rail grip/collapse), `04-detail-track-table.md` (tiers, columns, sort, filters,
> command bar, expander), `07-liked-songs.md` (the facts panel this page re-uses), `25-sidebar.md` (rootlist rows that drag
> into this page), `19-shell-overlays.md` (toasts, dialogs, teaching tips).
>
> Paths are `src/apps/Wavee/…` as of release 0.2.9 (HEAD `b3f6647a`); after Wave 0 the same relative paths live under
> `src/apps/_old/Wavee/…`.

---

## 0. The non-negotiables

1. **One page, six identities, zero layout forks.** Owner, collaborator, collaborative-follower, editorial (Spotify-made),
   chart and radio/mix/daylist playlists all render the SAME `DetailConfig.Playlist` composition (`DetailConfig.cs:196-201`).
   Nothing about the frame changes; only capability-gated affordances appear and disappear. There is no "editorial playlist
   layout".
2. **The edit gate is ONE TRIO of predicates.** `PlaylistInlineEdit.Editable(m)` = `Notice == None && Capabilities.CanEditItems`,
   `EditableMetadata(m)` = `Notice == None && Capabilities.CanEditMetadata`, and `Live(m)` = `Notice == None` — the shared half
   for the two OWNER affordances whose capability is neither items nor metadata (`PlaylistInlineEdit.cs:77,80,84`).
   **17 call sites across 5 files** route through them: `Editable` **×6** (page-body drop `DetailShell.cs:711`, recs gate
   `DetailTracks.cs:931`, list drop `:1215`, block move `:1336`, row drag source `:1420`, row host `:1442`), `EditableMetadata`
   **×8** (rail cover `DetailRail.cs:128`, narrow header `:380`, hero `DetailVerticalHero.cs:69`, `HeroEditable` `DetailTracks.cs:1671`,
   and the cover/title/description editors themselves `PlaylistInlineEdit.cs:327,343,486,632`), `Live` ×3 (invite `:899`,
   owner ⋯ `:1090`, `AppendOwnerItems` `:1134`). The five files are `DetailShell` · `DetailTracks` · `DetailRail` ·
   `DetailVerticalHero` · `PlaylistInlineEdit`. So "a notice mounts ⇒ every edit affordance disappears in the same frame" is
   one fact, not seventeen agreements. **The two owner affordances carry a second gate the other fifteen do not**:
   `SpotifyEditsLive(svc)` = `RealPlaylistMutations is not null && Session.Status == Authenticated` (`:65-66`) — under `--fake`
   or logged out the invite pill and the ⋯ menu are absent even on an owned, notice-free playlist. `SpotifyEditsLive` has
   **7** call sites of its own and two of them are OFF this page — the sidebar/card `InviteCollaborators` action
   (`ContainerActions.cs:209`) and the row-menu access gate (`Menus.cs:738`) — so porting it belongs beside the CAPS, not
   inside the page.
3. **A notice never un-renders the page.** A deleted / access-revoked / create-failed playlist keeps its rows, its cover and
   its scroll position and gains one Informational `InfoBar` strip between the header and the list
   (`DetailNoticeBar.cs:45-88`, `PlaylistPageNoticeRules.cs:28-72`). Never a skeleton, never an error page.
4. **"Not loaded yet" and "empty" are different pictures.** `MembershipLoaded == false` ⇒ shimmer rows and a shimmer meta
   bar of the shape `"00 songs · 0 hr 00 min"` (`DetailRail.cs:77-94`, `DetailTracks.cs:420-433`); `MembershipLoaded == true &&
   total == 0` ⇒ "Nothing here yet". A thin rootlist-seeded header must never say "0 songs · 1 min".
5. **Inline title/description edit is a swap inside ONE node, not a mode.** `AnimatedSwap` keeps the same box across
   read↔edit so the height change tweens through layout (`MotionRecipes.CardResize`, 300 ms `Easing.SmoothOut`) instead of
   snapping (`PlaylistInlineEdit.cs:106-110`). The hover affordance is an engine-eased pill (fill + hairline border, no
   re-render) plus a bound-opacity pencil — never a discrete pop (`PlaylistInlineEdit.cs:523-561`).
6. **The hero title's measure is invariant.** The status chips ("Saving…/Saved", "{n} changes pending") live in a trailing
   row of a stable column, never inline with the title: putting them in the title row stole width for 1.8 s and forced a
   live re-wrap during the editor swap (`PlaylistInlineEdit.cs:563-582`).
7. **A same-list reorder never dims the app and never re-keys under the pointer.** `SpotlightWhen = !IsSameListDrop`
   (`DetailTracks.cs:1195`) and `PlaylistReorderDefer.TryHold` parks any live re-projection until the drag session ends
   (`PlaylistReorderDefer.cs:32-49`, `DetailPage.cs:211`).
8. **Every refusal is a sentence.** A refusing drop target is transparent, so `PlaylistDropRefusalRules.Evaluate`
   (`WaveeDragRules.cs:126-137`) answers the accept test AND the caption from one table: "Can't edit this playlist",
   "Still loading…", "Nothing to add" / "Can't add an artist", "Clear sorting to reorder", "Clear filters to reorder",
   "Still syncing — try again in a moment".
9. **The cover is a drag source, a drop target and an editor at once, on two different nodes.** The framing box carries
   `WaveeDetailDrag.Hero` (drag the whole playlist out); the editable cover inside it owns the `DropKinds.Files` target and
   the click-to-pick (`DetailRail.cs:145-162`, `PlaylistInlineEdit.cs:362-378`).
10. **Nothing flashes on the preview→full swap.** Every structural rail/hero row carries a stable key, late rows fade up
    (`DetailRail.FadeUp`) and survivors FLIP from their old origin (`DetailRail.Shove`, `Expressive.Fast` 250 ms,
    `Easing.SmoothOut`) — `DetailRail.cs:28-71`. The cover is row 0 and takes NO entrance at all (`DetailRail.cs:143-144`).
11. **An empty OWNED playlist is not an empty page.** It opens on the "Recommended songs" header and its extender rows,
    fetched on the header's own mount (`DetailTracks.cs:958-994, 2936-2998`). An empty FOLLOWED playlist says "Nothing here yet".
12. **Optimistic, then honest.** Every write flips the UI first and reports the mapped sentence on failure
    (`PlaylistEditErrorKinds.KeyFor`, `PlaylistEditErrors.cs:16-29`); "Queued offline" / "still syncing" / "Already there" /
    "Can't move here" are Informational, everything else is Error (`PlaylistEditErrorKinds.cs:79-81`).
13. **The accent is the cover's, never the page kind's.** `Surfaces.ChromeSchemeFor(paletteUrl) → WaveePalette.ChromeAccent`,
    falling back to the nav-preview `extractedColors.colorDark` payload, then `Tok.AccentDefault` (`DetailShell.cs:304-310`).
14. **The picker continues the submenu, it never reshuffles it.** `PlaylistDepositTargets.Order` is the ONE ordering
    (MRU-first, then rootlist order) and the ONE eligibility filter for the "Add to playlist ▸" submenu, the
    "Move to playlist ▸" submenu and the picker flyout (`PlaylistDepositTargets.cs:52-88`).
15. **The daylist countdown ticks off the frame clock.** `FlipCountdown` anchors ONE `DateTimeOffset.UtcNow` sample against
    `FrameTime.NowMs` and never re-polls the wall clock (`FlipCountdown.cs:51-78`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition (two-column arm, mode 0)

```
DetailPage (Component)                                   DetailPage.cs:22            route "pl:<uri>" | "local"
├─ UseResource(LoadAsync → MapPlaylist)                  DetailPage.cs:109-159       header+membership+saveCount, cover latch
├─ UseSignalEffect(store.Changes → DetailLiveRefresh)    DetailPage.cs:171-259       live re-map; owner-id scoped
└─ DetailShell (Component)                               DetailShell.cs:83           mode/tier measure, accent, handlers
   ├─ CoverPaletteLeaves.ShellTint (0×0 leaf)            DetailShell.cs:321-323      page-scoped Mica tint claim
   ├─ PlaylistReorderDeferWatcher (0×0)                  PlaylistReorderDefer.cs:56  flush the parked model on drag end
   ├─ CoverPaletteLeaves.PageTonePlane                   DetailShell.cs:575-577      one flat art-derived ground
   └─ twoColumnPage
      ├─ DetailNoticeBar.For(model, showMinifiedAlbum:false)  DetailNoticeBar.cs:42  Deleted / AccessRevoked / CreateFailed
      └─ row  [rail | grip | right]                      DetailShell.cs:639-656      DropTarget = PageDropTarget (append)
         ├─ railFaded (Opacity bind = _railFade)         DetailShell.cs:779-789
         │  └─ DetailRail.Build(...)                     DetailRail.cs:122-299       ScrollView, Fill=Tok.FillLayerDefault
         │     ├─ "rail:cover"  Draggable=WaveeDetailDrag.Hero   DetailRail.cs:145-162
         │     │  └─ PlaylistInlineEdit.Cover | HeroArtwork       PlaylistInlineEdit.cs:29-32 / DetailRail.cs:98-105
         │     │     └─ EditableCover → CoverOverlay (scrim+camera / status chip)  PlaylistInlineEdit.cs:296-432
         │     ├─ "rail:owner"  PlaylistOwnerBlock               DetailRail.cs:172-175, 558-564
         │     │  ├─ CollaboratorFacePile (≥2 members | collaborative)  CollaboratorFacePile.cs:14
         │     │  │  └─ InviteButton                              PlaylistInlineEdit.cs:61, 882-932
         │     │  └─ PlaylistOwnerRow (avatar 24 + name + InviteButton)  PlaylistInlineEdit.cs:736-760
         │     ├─ "rail:title"  PlaylistInlineEdit.Title | WaveeType.DetailHero  DetailRail.cs:183-189
         │     │  ├─ read arm: hover pill + pencil + status row  PlaylistInlineEdit.cs:519-582
         │     │  └─ edit arm: AnimatedSwap(EditChrome(EditableText, SaveCancelRow))  PlaylistInlineEdit.cs:499-517
         │     ├─ "rail:meta"   MetaRow (SkelRegionEl when !MembershipLoaded)  DetailRail.cs:77-94, 199-200
         │     ├─ "rail:daylist" FlipCountdown(Compact:true)     DetailRail.cs:204, 465-469
         │     ├─ "rail:chart"   ChartCaption                    DetailRail.cs:207, 474-480
         │     ├─ "rail:cta"     PlayPill · SaveButton · ShareButton · OwnerMenu   DetailRail.cs:212-240
         │     │                                                  PlaylistInlineEdit.cs:764-815, 1073-1118
         │     ├─ "rail:desc"    PlaylistInlineEdit.Description | RichText.Of   DetailRail.cs:249-253
         │     └─ "rail:likedfacts" LikedFacts.Panel (gate: LikedFacts.Has(m, OwnerRow))  DetailRail.cs:259-260
         ├─ DetailRailGrip (Splitter, collapse detent)    DetailShell.cs:797-817
         └─ right "right:tracks" → TrackList              DetailShell.cs:533-549
            ├─ Chrome: command bar + column header        DetailTracks.cs:911-912, 1926-2060
            │  ├─ SplitButton Play next (+ Add to queue)  DetailTracks.cs:1992-1994
            │  ├─ Shuffle · PlaylistTuneButton · Sort · Row size · Select   DetailTracks.cs:1996-2017, 3726
            │  ├─ DetailTrackMoreButton (Copy/Add to playlist, BPM·Key, Plays)  DetailTracks.cs:4338-4426
            │  └─ Search disclosure                       DetailTracks.cs:2025, 224-260
            ├─ ItemsView.CreateBound (rows | rows+recs)   DetailTracks.cs:950-1046
            │  ├─ Insertion = Insertion()                 DetailTracks.cs:1179-1212  (gap preview, captions, deposit)
            │  ├─ RowOrRecContent → track row | RecHeader | RecRow  DetailTracks.cs:2895-3024
            │  └─ ListPlaceholder(Loading|Empty|NoMatch)  DetailTracks.cs:416-433
            └─ SelectionCommandBar (selection mode)       DetailTracks.cs:1934-1939
```

**The config literal this chapter owns** (`DetailConfig.cs:196-201`) — everything the frame and the table branch on:

```
DetailConfig.Playlist = new(
  TwoColumn: true, RailWidth: WaveeSize.RailPlaylist (240), Badges: BadgeStyle.OwnerRow,
  ShowArtThumb: true, ShowAlbumColumn: true, Columns: ListColumns [ #36 | TITLE ★ | ALBUM 200 | ♥ 40 | ⏱ 52 ],
  CapTitle: true, Selection: ItemsSelectionMode.Extended, HasTrailing: false,
  Heart: HeartMode.Follow,  ← Follow for an OWNED playlist too (see §6.1's ⋯ label branch)
  ShowTrackArtist: true, Recommendations: true, ShowTempo: true, ShowVersions: true,
  PlaysColumnOptIn: true, RailScope: RailScope.Playlist)
```
`ListColumns` is a SHARED static instance with `DetailConfig.Liked` — the header and the rows are reference-equal, which is
the table's alignment invariant. `RailScope.Playlist` is the persisted width/collapse pair (`DetailRailPolicy.DefaultWidthFor`
= 240 for playlist/liked, 280 for album/show); **"Keep left-rail same size" re-points every surface at ONE synthetic
`RailScope.Uniform` pair** (`DetailRailPolicy.ScopeFor`, `DetailRailPolicy.cs:32-52`), so a playlist rail's width can be set
from an album page and vice versa. The rail also composes a `rail:prerelease` row between the CTA cluster and the
description (`DetailRail.cs:242`) — unreachable for a playlist (`UpcomingAt` is album-only), listed so nobody ports it.

**Two arms of this file are DEAD for a playlist — do not port them.** (a) `DetailRail.BuildHeader` (the fixed 140-DIP narrow
header with the 600-wide info column and the LABELLED invite pill) has exactly one call site,
`DetailShell.cs:587`, reached only when `verticalTracks` is false — and `verticalTracks = mode == Vertical && Content ==
Tracks` (`DetailShell.cs:512`), which a playlist always satisfies. So a playlist at mode 3 ALWAYS takes the vertical hero;
the 140 header is the podcast-show arm and its `BadgeStyle.OwnerRow` branches never render. (b) The rail's `rail:eyebrow`
row is `Badges == TypeYear` only (`DetailRail.cs:167-171`) — a playlist rail has NO eyebrow; the eyebrow string exists for
the vertical hero alone.

Narrow/hero arm (mode 3 or the "Hero" page-layout setting) replaces the rail with
`DetailVerticalHero.Build` as list item 0 (`DetailVerticalHero.cs:61-447`, mounted by `TrackList.VerticalList`,
`DetailTracks.cs:1463-1538`): artwork · eyebrow · title · accent rule · attribution · meta · daylist pulse · chart caption ·
action row (Play · Shuffle · ♥ · Share · ⋯) · description, with the sticky `ContextBand` above it and the facts panel moved to
the list FOOTER (`DetailVerticalHero.cs:271-279`).

### 1.2 The 0.3 tree

Props freeze at mount: the third column says how live data reaches each node. `Playlist p` is a handle (`int Slot`), so it is
a value that never goes stale; what changes is the table's `Changed` signal and the row's `Version`.

```
Playlist.Page (sealed class Page : Component)              Entities/Playlist.Page.cs
├─ UseSignal(Entities.Current.Playlists.Changed)           re-render on any playlist publish (D8)
├─ UseSignal(Entities.Current.Edges.PlaylistTracks.Version[p.Slot])  membership publish only
├─ UseEffect → Entities.Ensure(p, PlaylistFields.All)            demand the WHOLE model on mount
│              Entities.EnsureEdges(p, EdgeKind.PlaylistTracks)   … the complete membership
│              Entities.EnsureRows(p.TrackSlots, TrackFields.Row) … one batch, never a visible window
│              Entities.EnsureEdges(p, EdgeKind.PlaylistCollaborators)
├─ Detail.Frame(...)                                       03-detail-frame.md — static fn, takes a DetailSpec struct
│  ├─ Playlist.Rail(p, railW, titleSize, lineH, descLines)   static fn over the handle
│  │  ├─ Playlist.Cover(p, size, editable)                   Component (owns hover/drop/status signals) · Key = "pl-cover:{size}:{radius}"
│  │  ├─ Playlist.OwnerBlock(p, width)                       Component · Key = "pl-owner:{(int)width}" · reads p.Version
│  │  │  └─ Playlist.CollaboratorPile(p, width) | Playlist.OwnerRow(p, width)
│  │  ├─ Playlist.Title(p, width, size, lineH)               Component (draft/editing/status signals) · Key = "pl-title:{w}:{size}"
│  │  ├─ Playlist.MetaRow(p, width)                          static fn; shimmer while !p.MembershipKnown
│  │  ├─ Controls.FlipCountdown(p.DaylistExpiresAt, accent)  Component · Key = "daylist:{uri}:{expiresAt}"  ← remount on rollover
│  │  ├─ Playlist.ChartCaption(p)                            static fn (a fact, not a clock)
│  │  ├─ Playlist.Cta(p, accent)                             Play · Save · Share · OwnerMenu
│  │  ├─ Playlist.Description(p, width, maxLines)            Component · Key = "pl-desc:{w}:{maxLines}"
│  │  └─ User.FactsPanel(p.TrackSlots)                       07-liked-songs.md
│  └─ Playlist.List(p)                                      04-detail-track-table.md configured by Playlist.TableSpec(p)
│     ├─ ItemsView.CreateBound(p.TracksSource, item => Track.Row(item, Playlist.RowStyle(p)), …)
│     ├─ Playlist.Insertion(p)                              InsertionOptions built ONCE, every delegate reads live handles
│     └─ Playlist.Recs(p)                                   Component; appended slots, own fetch state
├─ Playlist.NoticeBar(p)                                    Component; reads p.Notice (a MODEL fact, never a probe)
└─ Playlist.ReorderDeferWatcher(p)                          0×0 Component; flushes the parked publish on drag end
```

Reactivity contract:
* **Signal** — the table `Changed` counter (page re-render), the per-parent edge `Version` (list re-bind), per-row
  `item.*` binds inside `BoundItemScope<Track>` (01-track-row.md), and the page-local UI signals: `_editing`, `_draft`,
  `_status`, `_hovered`, `_dropOver`, `_recs`, `_recState`, `_expandedRow`, `_selection.Version`.
* **Func/thunk** — `Accent = () => …` on every component that paints the cover accent (the palette lands AFTER mount:
  `SaveButton.Accent`, `FlipCountdown.Accent`, `PreReleaseCountdown.Accent`).
* **Context** — `Overlay.Service`, `InputHooks.Current`, the nav `Go`, the drag state.
* **Key remount** — `FlipCountdown` on `expiresAt` (a rollover is a new window), `SaveButton` on its target uri,
  `Playlist.Cover/Title/Description` on their geometry bucket (8-DIP buckets, `DetailVerticalLayout.BucketW`), the picker
  rows on `p.Uri`, `RecRow` on the track id, the list on
  `route + density + classic + query + filters + resetEpoch + recsCapable` (`DetailTracks.cs:1090`).

---

## 2. Wireframes

≈8 DIP per monospace character. Page width = window − nav pane (`DetailLayoutBreakpoints.ShellChromeAllowanceDip` = 240,
`DetailLayoutBreakpoints.cs:32`).

**Breakpoints (all with hysteresis).** Page-mode ladder `DetailLayoutBreakpoints.NominalModeFor`
(`DetailLayoutBreakpoints.cs:68-87`): `≥820 → 0`, `≥660 → 1`, `≥560 → 2`, else `3 (Vertical)`; 820/660 crossings use
`ModeHysteresisDip = 24`; the vertical band is asymmetric — enter at `<540`, leave at `≥580`.
**The nominal ladder is NOT what ships, though:** `ModeFor` rewrites a nominal Vertical verdict to **2** whenever the page
is still ≥ 540 (`:82`, and again for the hysteresis dip at `:85`). So the LIVE mode-2 band is **540…660**, not 560…660,
and there is no width at which the page is Vertical but ≥540. Rail width per mode:
mode 0 = the persisted width (default `WaveeSize.RailPlaylist` = 240, `WaveeTokens.cs:60`; grip range 180…480,
`DetailRailPolicy.cs:25`), mode 1 = 224, mode 2 = 188 (`DetailShell.cs:192`). The [rail | right] row itself is capped at
`MaxWidth = 1600` and CENTRED inside the page (`DetailShell.cs:649,672`), and the right column carries
`MinWidth = ContentMinWidthForMode(mode)` — **300** in modes 0/1/2, **0** at mode 3, so the tier-6 table gets the full
width below 300 DIP instead of clipping (`DetailLayoutBreakpoints.cs:61-66`). Hero stacked↔row flow crosses at
`RowFlowEnterW = 424` / leave `400` (`DetailVerticalLayout.cs:47-50`). Title rung: `winH ≥ 900 → 40/52` else `28/36`
(`DetailShell.cs:636-637`). Description cap: `winH < 760 → 3` else `6` lines (`DetailShell.cs:638`). Table tiers
(`NominalTierFor`, `DetailLayoutBreakpoints.cs:10-11`): 860/720/560/440/340/300 — see 04-detail-track-table.md.

### W1 — Owner playlist, fully loaded, mode 0 @ page 1040 (window 1280)

```
┌ rail 240 ─────────────────────┬16┬ right (grow) ───────────────────────────────────────────┐
│ pad 16 ▸◂ 8   gap 14          │gr│  chrome: command bar 44 + column header 36              │
│ ┌───────────────────────────┐ │ip│ ┌─────────────────────────────────────────────────────┐ │
│ │                           │ │  │ │ [▶ Play next|⌄] [⤨ Shuffle] [✨ Tune] │ [⇅ Sort ⌄]  │ │
│ │      cover 216×216        │ │  │ │  [▤ Row size ⌄] [☑ Select] [🔍] [⋯]                 │ │
│ │      r8, Elevation.Card   │ │  │ ├─────────────────────────────────────────────────────┤ │
│ │      saturation 1.18      │ │  │ │ #   TITLE            ALBUM     [ADDED BY][DATE] ♥ ⏱│ │
│ └───────────────────────────┘ │  │ ├─────────────────────────────────────────────────────┤ │
│ (👤24) Christos      [👥 Invite]│ │ │ 1  ▓▓ Track one                                     │ │
│                                │  │ │     Artist          Album      (av) You   3d   ♡ 3:41│ │
│ Late Night Mix            ✎    │  │ │ 2  ▓▓ Track two …                              ♥ 3:12│ │
│  ↑ DetailHero 40/52/600,       │  │ │ …  (row 48 @ density 1, PadX 16, inset 8)           │ │
│    ≤3 lines, auto-fit to 18    │  │ │                                                     │ │
│ 47 songs · 2 hr 51 min         │  │ │ ── Recommended songs                        [↻]     │ │
│  ↑ TrackMeta, ≤2 lines, w=216  │  │ │ ▓▓ Suggested track            Artist       [+] 3:02 │ │
│ ┌──────────┐ ┌──┐┌──┐┌──┐      │  │ └─────────────────────────────────────────────────────┘ │
│ │ ▶  Play  │ │♡ ││⇗ ││⋯ │      │  │                                                          │
│ └──────────┘ └──┘└──┘└──┘      │  │                                                          │
│  36 capsule   40  40  40       │  │                                                          │
│  gap 12 / inner gap 8          │  │                                                          │
│ Late-night songs I keep coming │  │                                                          │
│ back to.  (12/Secondary, ≤6 l) │  │                                                          │
│ ┌───────────────────────────┐  │  │                                                          │
│ │ THE YEARS        2014-24  │  │  │   ← LikedFacts.Panel (07-liked-songs.md), gate:          │
│ │ 2019 ▁▃▅█▅▃▁  most tracks │  │  │      LikedFacts.Has(m, BadgeStyle.OwnerRow)              │
│ └───────────────────────────┘  │  │                                                          │
└────────────────────────────────┴──┴──────────────────────────────────────────────────────────┘
```
**The two bracketed columns above are DATA-GATED, not tier-gated** (`DetailPage.MapPlaylist`, `DetailPage.cs:501-531`):
`ADDED BY` renders only when the playlist carries **≥2 distinct contributors** (`HasAddedBy = contributors.Count >= 2`) —
a solo owner's playlist never shows the lane at any width; `DATE` only when at least one row has an `AddedAt`; the video
glyph only when at least one row has a video (`HasVideo`). A followed editorial playlist therefore usually shows neither.
The whole [rail | right] row is centred and capped at 1600 DIP, so a 2560-wide window keeps the same picture with gutters.

### W2 — Editorial / followed playlist, mode 0 @ page 1040

Differences from W1, and ONLY these: the owner row loses the Invite pill (`CanAdministratePermissions` false), the ⋯ owner
menu is absent entirely (`OwnerOverflowMenu` returns an empty box unless `IsOwner` + live Spotify edits,
`PlaylistInlineEdit.cs:1090-1091`), the title/description/cover lose their hover pill + pencil + camera overlay, the heart
is a FOLLOW toggle (same `SaveButton`, `DetailConfig.Heart = HeartMode.Follow`, `DetailConfig.cs:199`), the "Recommended
songs" block is absent (`DetailTracks.cs:927-932`), rows do not drag as a MOVE (`source == null`, `DetailTracks.cs:1420-1421`)
and the list refuses drops with "Can't edit this playlist".

```
│ (👤24) Spotify                 │   ← no invite pill; name Grows/trims into the whole 216
│ Today's Top Hits               │   ← plain WaveeType.DetailHero, no pill, cursor = default
│ 50 songs · 34.5M saves · 2 hr 41 min │  ← saves segment only when popcount > 0 (DetailPage.cs:513-525)
│ ┌──────────┐ ┌──┐┌──┐          │   ← Play · ♡(follow) · ⇗ ; NO ⋯
```

### W3 — Mode 1 @ page 700 (rail 224, cover 200) · W4 — Mode 2 @ page 580 (rail 188, cover 164)

```
mode 1                            mode 2
┌ rail 224 ─────┬16┬ right ─┐     ┌ rail 188 ──┬16┬ right ──────┐
│ cover 200     │  │ table   │     │ cover 164  │  │ table (tier │
│ owner row     │  │ drops   │     │ owner row  │  │  3-4: no    │
│ title 28/36   │  │ Added-by│     │ title 28/36│  │  Album, no  │
│ meta          │  │ (tier<1)│     │ meta       │  │  Date/Plays)│
│ [▶ Play]      │  │ then    │     │ [▶ Play]   │  │             │
│ [♡][⇗][⋯]  ←wraps as a unit│     │ [♡][⇗][⋯] ←the FAB group wraps below Play (Wrap=true,
└───────────────┴──┴─────────┘     └────────────┴──┴  DetailRail.cs:216-217) ───────────────┘
```
Below 260 DIP of owner-row width the Invite pill collapses to a 28×28 round icon + tooltip
(`PlaylistInlineEdit.cs:904-915`) — at rail 240 the row is 216 wide, so **the pill is always the round arm on the rail**;
the labelled arm appears only in the vertical hero (`maxWidth` = `contentW`, up to 640).

### W5 — Vertical hero, stacked flow @ page 400 (`rowFlow` false)

```
┌ page 400 ────────────────────────────────────────┐
│ pad 16 (NarrowHeroPad, <420)                     │
│ ┌──────────────────────────────────────────────┐ │  artSize = clamp(400-32, 96, 280) = 280
│ │              cover 280×280                   │ │  r8, Elevation.Card, sat 1.18
│ └──────────────────────────────────────────────┘ │
│ gap 16 (NarrowHeroGap)                           │
│ PLAYLIST · COLLABORATIVE      ← Eyebrow/Tertiary │  DetailRail.EyebrowText, OwnerRow arm: ONE of
│                                                  │   "Playlist · Collaborative" / "Playlist · Private" /
│                                                  │   "Playlist" (collaborative wins; public+solo says
│                                                  │   nothing extra) — DetailRail.cs:581-583
│ Late Night Mix                ← type PLAN size   │  DetailVerticalLayout.TitleTypeFor; the EDITABLE arm
│                                                  │   passes NO maxLines ⇒ the 2-line displayFace default
│                                                  │   (PlaylistInlineEdit.cs:463), not titlePlan.Lines —
│                                                  │   and `TitleLinesMax = 2` caps the PLAN too, so the
│                                                  │   plan's Lines is 1 or 2 and the editable arm can only
│                                                  │   ever wrap MORE, never less (see §9's corrected entry)
│ ▂▂  ← Surfaces.AccentRule 20×2 accent            │
│ (👥👤+2) 4 collaborators  [👥 Invite]            │  CollaboratorFacePile, avatars 28+2 ring, −12 overlap
│ 47 songs · 2 hr 51 min                (≤2 lines) │
│ 02:41:09  Next update at 6:00 AM   ← daylist only│  FlipCountdown Compact:false, cells 13×28, 20/300
│ 3 new entries · Sep 8              ← chart only  │
│ [ ▶ Play ] [⤨] [♡] [⇗] [⋯]        gap 8, wrap   │  36 capsule + four 32×32 satellites
│ Late-night songs I keep coming back to.  (≤4 l)  │
├──────────────────────────────────────────────────┤
│ toolbar row (pad 16/8/16/4)                      │
│ # TITLE … (tier 6 table)                         │
└──────────────────────────────────────────────────┘
```

### W6 — Vertical hero, row flow @ page 520 (`rowFlow` true, ≥424)

```
┌ page 520 ────────────────────────────────────────────────────┐
│ pad 24 (HeroPad — row flow always takes the full pair)       │
│ ┌───────────────┐ gap 24  PLAYLIST                           │  artSize = round(clamp((520-48-24)*0.44,144,240)) = 197
│ │ cover 197×197 │         Late Night Mix                     │  identity: Grow 1, Basis 0, MinHeight = artSize
│ │               │         ▂▂                                 │
│ │               │         Christos                           │  contentW = min(640, max(160, 520-48-24-197)) = 251
│ │               │          ↑ NOT the rail's row: a solo      │  ← see W29: the hero attribution's plain arm is a
│ │               │            playlist's hero attribution is  │    bare 12/600 run — NO avatar, NO invite pill
│ │               │         47 songs · 2 hr 51 min             │
│ └───────────────┘         ←fill block Grow=1 →               │  surplus opens BETWEEN meta and actions
│                           [▶ Play][⤨][♡][⇗][⋯]              │  … so the actions land on the cover's bottom edge
│ Late-night songs I keep coming back to.                      │
└──────────────────────────────────────────────────────────────┘
```

### W7 — Collapsed rail (compact identity strip), mode 0

```
┌96┬20┬ right ──────────────┐   RailCompactW = 96 (DetailShell.cs:203)
│▓▓│gr│                     │   pad 8/16/8/16, Fill = Tok.FillLayerDefault
│80│ip│   the table keeps    │   cover 80 (click = expand), title 12/600 ≤2 lines w=80
│  │  │   most of the card   │   spacer Grow=1 → chevron 80×28 at the foot, r4, HoverFill FillSubtleSecondary
│Lat│ │                     │   whole strip wrapped in ToolTip(m.Title)
│e N│ │                     │   re-expand: click cover OR chevron OR pull the grip past 220 (RailReExpand)
│ › │ │                     │   collapse: push the grip below 180 by ForcePush 44 (raw ≈136)
└───┴──┴─────────────────────┘   glyph = Icons.ChevronRight 14/TextSecondary (the EXPAND direction, not a ⌄);
                                 cover = max(48, stripW − 2·8) — the 48 floor, not CoverEdge's 80
```

### W8 — Cold open (deep link, no nav preview): skeleton

```
┌ rail 240 ──────────┬ right ───────────────────────────┐   Skel.Region derives the shimmer from the REAL shell
│ ░░░░░░░░░░░░░░░░░░ │ ░░░░ ░░░░ ░░░  ░░░               │   rendered against DetailPage.PendingSeed(Playlist):
│ ░ cover 216 ░░░░░░ │ ─────────────────────────────────│    OwnerName = " ", MetaLine = " ", 8 blank tracks
│ ░░░░░░░░░░░░░░░░░░ │ ░░ ░░░░░░░░░░░░░  ░░░░░  ░░  ░░░ │   (DetailPage.cs:346-379)
│ ░░░░░░░░░ (owner)  │ ░░ ░░░░░░░░░░     ░░░░░  ░░  ░░░ │   Reveal = SkelReveal.FadeOnly, smoothResize false
│ ░░░░░░░░░░░░░░░░░░ │ ░░ ░░░░░░░░░░░░░  ░░░░░  ░░  ░░░ │   (DetailPage.cs:268-291)
│ ░░░░░░ (meta)      │ … 8 rows                         │
│ ░░░░░░░░ ░░ ░░ ░░  │                                  │
└────────────────────┴──────────────────────────────────┘
```

### W9 — Reveal in progress (nav preview → full model)

The page paints on the click frame from the preview (cover + title only). The full model INSERTS rows into the middle of the
column: owner block, meta, daylist, chart, description and the facts panel fade up from opacity 0 while the title, the CTA
cluster and everything below FLIP from their old origin.

```
frame 0 (preview)            frame N (full)                what moves
┌──────────────┐             ┌──────────────┐
│ cover 216    │  anchor →   │ cover 216    │              cover: NO motion (key only, row 0)
│ Late Night   │             │ (👤) Christos│  ← FadeUp    owner: Enter opacity 0→1
│              │             │ Late Night   │  ← Shove     title: FLIP from its old y
│ [▶][♡][⇗]    │             │ 47 songs ·…  │  ← FadeUp
│              │             │ [▶][♡][⇗][⋯] │  ← Shove     CTA: FLIP (and ⋯ appears with the model)
└──────────────┘             │ description  │  ← FadeUp
                             └──────────────┘
```

### W10 — Empty OWNED playlist (recommendations live)

```
┌ rail ──────────┬ right ──────────────────────────────────────────┐
│ cover (mosaic  │ [▶ Play next|⌄] [⤨ Shuffle] │ [⇅][▤][☑][🔍][⋯] │
│  or generated) │ #  TITLE                       ALBUM   ♥    ⏱  │
│ (👤) You       │ ────────────────────────────────────────────────│
│ My Playlist #4 │ Recommended songs                          [↻] │  ← BodyStrong 14/20/600, MinHeight = rowH,
│ 0 songs · 0 min│ ▓▓ Suggested track      Artist         [+] 3:02│    PadX 16; refresh 32×32 circle
│ [▶ Play][♡][⇗] │ ▓▓ Suggested track      Artist         [+] 2:48│  ← ArtCard rows, art 40, MinHeight=MaxHeight=rowH
│ [⋯]            │ …20 per batch (RecBatch, DetailTracks.cs:184)  │
└────────────────┴───────────────────────────────────────────────┘
   The header realizes at index `visible` and its MOUNT is the first fetch (DetailTracks.cs:2952-2958).
   While loading: a 32×32 TrackRow.Spinner() in the header's trailing slot.
   Loaded empty: "No suggestions right now" (12/Tertiary) beside the refresh button.
   Refresh button: 32×32, r16 (a circle), Icons.Refresh 14 / TextSecondary, ScaleEmphatic, Interaction.Subtle.
   NOT EVERYWHERE: `recsCapable = cfg.Recommendations && !_embedded && !_verticalHeader` (DetailTracks.cs:926) — the
   recommendations block does not exist in the VERTICAL/hero arm (mode 3 or the Hero page-layout setting) nor in the
   embedded library pane. An empty owned playlist there falls back to "Nothing here yet" like a followed one.
   Rec rows carry the explicit badge + the artist subline + the duration and open the SINGLE-track menu (Menus.TrackAttach).
```

### W11 — Empty FOLLOWED playlist · W12 — Membership not loaded

```
W11                                            W12
┌ right ─────────────────────┐                 ┌ right ─────────────────────┐
│ (chrome)                   │                 │ (chrome)                   │
│                            │                 │ ░░ ░░░░░░░░░░░░  ░░░░ ░ ░░ │
│      Nothing here yet      │ 14/Tertiary,    │ ░░ ░░░░░░░░░     ░░░░ ░ ░░ │  RowsShimmer derived from the
│      (centred, Grow=1,     │ pad 16/24       │ ░░ ░░░░░░░░░░░░  ░░░░ ░ ░░ │  real Row(EmptyTrack) template
│       pad 16/24/16/24)     │                 │ … (rail meta is a shimmer  │  (DetailTracks.cs:420-430)
└────────────────────────────┘                 │    bar of "00 songs · 0 hr │
  "No songs match your filter" is the same     │    00 min")                │
  box with the NoMatch string.                 └────────────────────────────┘
```
Both strings are LOCALIZED, not literals: `detail.empty.noTracks` = "Nothing here yet" and `detail.empty.noMatch` =
"No songs match your filter", read through `Loc.Get` in `DetailTracks.FilterEmpty` (`:409-414`).
**W12 cannot be reached without a real store.** `MembershipLoaded` is TRUE by construction when `svc.RealStore is null`
(i.e. `--fake`), when the uri is not a `spotify:playlist:` one (Local Files, an offline `wavee:playlist:*`), or once
`store.HasMembership(uri)` (`DetailPage.cs:441-444`) — which is why parity item 9 can only be checked on a live build.
The Loading arm is a `SkelRegionEl` whose `Pending` thunk re-reads `PlaylistListState.IsLoading` off the LIVE model and
whose `Content` falls through to the Empty box when the membership lands empty in the same flush (`DetailTracks.cs:419-431`).

### W13 — Notice strip (Deleted / AccessRevoked / CreateFailed)

```
┌ page ──────────────────────────────────────────────────────────────────────┐
│ pad 16/8/16/4                                                              │
│ ┌────────────────────────────────────────────────────────────────────────┐ │
│ │ ⓘ  This playlist was deleted.                        [ Go to Library ] │ │  InfoBar, Informational,
│ └────────────────────────────────────────────────────────────────────────┘ │  isClosable:false, action =
├────────────────────────────────────────────────────────────────────────────┤  Button.Subtle/Small → go("albums")
│ rail (NO pencil, NO camera, NO invite, NO ⋯) │ rows STAY on screen         │  DetailNoticeBar.cs:67-88
└────────────────────────────────────────────────────────────────────────────┘
```
Strings: `detail.notice.deleted` "This playlist was deleted." / `detail.notice.accessRevoked` "You no longer have access to
this playlist." / `detail.notice.createFailed` "This playlist couldn't be created." / `detail.notice.goToLibrary` "Go to Library".
At `Notice == None` the bar is a `BoxEl { Height = 0, HitTestVisible = false }` — it reserves NO space and adds no gap, so
its arrival/departure is a pure insert above the row (`DetailNoticeBar.cs:49`). The fifth `DetailNotice` value,
`MinifiedAlbum`, ships in the same enum and the same component but is unreachable here: it is an ALBUM verdict, and the full
page passes `showMinifiedAlbum: false` anyway (`DetailShell.cs:600,669`).

### W14 — Inline TITLE edit (rail arm)

```
read state (hover)                            edit state
┌ 216+16 hover pill ───────────────┐          ┌ 216 ──────────────────────────────┐
│ margin −8,−4,−8,−4  pad 8,4,8,4  │          │ ┌──────────────────────────────┐  │  fieldH = max(40, titleSize+12)
│ r4  HoverFill FillSubtleSecondary│          │ │ Late Night Mix|              │  │   → 52 at 40/52, 40 at 28/36
│ border 1 → HoverBorderColor      │          │ └──────────────────────────────┘  │  EditableText, caret at end
│   StrokeControlDefault           │          │                     [✓ Save][✕ Cancel]│  gap 8, buttons 32 tall,
│ cursor = IBeam                   │          └───────────────────────────────────┘  r16, pad 10/0/12/0
│ ┌──────────────────────────┐ ✎20 │          Save: Fill Accent@0.16 → hover 0.26 → pressed 0.12, ink AccentTextPrimary
│ │ Late Night Mix           │     │          Cancel: transparent → FillSubtleSecondary / FillSubtleTertiary, TextSecondary
│ │ run width = 216−20−8=188 │     │          The whole chrome is brought into view (margin 40, then 32 on every growth).
│ └──────────────────────────┘     │
└──────────────────────────────────┘
trailing status row (Justify End, gap 6, column gap 0→4 when busy):
   [⟳ Saving…]  →  [✓ Saved]   (1.8 s)     [↻ 3 changes pending]
   chip: pad 8,3,10,3 · r12 · Fill FillSubtleSecondary · icon 16 box · text 11/600/TextSecondary
   The row is ALWAYS mounted (it carries the persistent PendingChip even at idle); only the saving→saved chip comes and
   goes, so the title's measure never moves (PlaylistInlineEdit.cs:576-580). The DESCRIPTION editor's trailing row carries
   the status chip ONLY — no pending chip — and collapses to a 0-height box at idle (`:709-711`).
   Save glyph Icons.Accept 13, Cancel Icons.Cancel 13; the ✓ inside the status chip is Icons.Accept 12/AccentTextPrimary,
   the ⟳ a 16-DIP ProgressRing.Indeterminate in TextSecondary.
   Bring-into-view margins: 40 on edit-enter (EditChrome's double-post), 32 on every later growth (UseEditScroll),
   48 on the DESCRIPTION's click (it scrolls to the READ shell, `:674`).
   PLACEHOLDER, both arms: `detail.edit.namePlaceholder` = "Playlist name". The field carries it as `Placeholder`
   (`:510`) AND the READ arm substitutes it for a blank title, painted `Tok.TextTertiary` instead of `TextPrimary`
   (`:520,551`) — so an untitled playlist reads as a prompt, not as an empty hero.
   FOCUS: `FocusOnMount` focuses with `visual: false` and the field sets `PlaceCaretAtEndOnFocus = true` — **caret at the
   end, nothing selected** (`:266-275,509`). The class comment above `PlaylistCreateIntent` still says "selected"; the code
   is the truth.
   The field/button gap inside `EditChrome` is `Spacing.S` 8, and the whole chrome is `Direction = 1, Width = _width`
   (`:252-263`) — the Save/Cancel row is never clipped by the field's own chrome.
```

### W15 — Inline DESCRIPTION edit

```
read (hover)                                   edit
┌ 216+16, AlignItems Start ───────────┐        ┌ 216 ─────────────────────────┐
│ Late-night songs I keep coming   ✎  │        │ ┌─────────────────────────┐  │  fieldH = 72 (fixed ≈3 lines,
│ back to.   (RichText.ExpandableFlex,│ 16×18  │ │ Late-night songs I keep │  │   never derived from the read cap;
│  12/Secondary, links AccentTextPrim)│ pencil │ │ coming back to.|        │  │   overflow scrolls INSIDE the field)
│  Grow 1, Basis 0 → wraps to what is │  13px  │ └─────────────────────────┘  │
│  left after the pencil              │        │              [✓ Save][✕ Cancel]│
└─────────────────────────────────────┘        └──────────────────────────────┘
empty: placeholder "Add an optional description" in TextTertiary (detail.edit.descriptionPlaceholder)
```

### W16 — Cover hover / file drag-over / saving

```
rest                 hover                        drag-over (DropKinds.Files)      saving
┌──────────┐         ┌──────────┐                 ┌──────────┐                     ┌──────────┐
│          │         │▒▒▒▒▒▒▒▒▒▒│ scrim #000@.52  │▓▓▓▓▓▓▓▓▓▓│ scrim #000@.68      │▒▒▒▒▒▒▒▒▒▒│
│  cover   │   →     │▒  📷 32  ▒│ white ink       │▓  📷 32 ▓│                     │▒[⟳ Saving…]│
│          │         │▒ Change  ▒│ 12/600, wrap    │▓  Drop  ▓│ "Drop image to      │▒▒▒▒▒▒▒▒▒▒│
└──────────┘         │▒  cover  ▒│ ≤2 lines, w=120 │▓ image… ▓│  change cover"      └──────────┘
                     └──────────┘                 └──────────┘
Opacity is a BOUND prop (0→1) with MotionTok.ControlNormal (250 ms) — the overlay is always mounted, never remounted.
The bind is `status != idle || hovered || dropOver || a live Files drag anywhere in the app` (`:392`, `:348-349`) — so the
scrim ALSO deepens the moment a file drag starts, before the pointer reaches the cover.
Click → FilePicker.OpenFile("Choose playlist cover image", JPEG *.jpg;*.jpeg). A non-JPEG drop raises a Warning toast
with the same PickCover string (PlaylistInlineEdit.cs:140-150).
NOT editable (followed / editorial / under a notice / no LibraryBridge): the whole overlay, the drop target, the hand
cursor and the click are gone — the arm falls through to the bare `DetailRail.HeroArtwork` (`:343-345`). The only thing
left on the cover is the framing box's `WaveeDetailDrag.Hero` drag source.
While `status != idle` the overlay swaps its CHILDREN, not just its opacity: the camera + label are replaced by the
StatusChip alone (`:394-398`), so a saving cover shows the chip centred on the scrim with no glyph behind it.
A successful write patches `m.Cover` from the STORE header (`svc.RealStore.GetPlaylist(uri).Cover`, `:419-420`) rather than
from the uploaded bytes — the cover only changes once the store agrees it did.
```

### W17 — Invite & access flyout (anchored BottomEdgeAlignedLeft from the pill, BottomEdgeAlignedRight from the ⋯ menu)

```
┌ 300 wide, pad 16, gap 12 ─────────────────────────┐
│ Invite collaborators            14/700/TextPrimary│
│ Anyone with the link can join this playlist as a  │ 12/Secondary, wrap ≤3
│ collaborator.                                     │
│ ┌───────────────────────────────────────────────┐ │  CTA: h 34, r17, Fill = Tok.AccentDefault,
│ │        🔗  Copy invite link                   │ │  hover AccentSecondary, pressed AccentTertiary,
│ └───────────────────────────────────────────────┘ │  ink = ColorContrast.PickContrast(accent), 13/600
│ ────────────────────────────── divider 1, m 0,2   │  busy → ProgressRing 14; done → ✓ "Copied"
│ Collaborative playlist            [====O]         │  label 13/600, caption 11.5/Secondary ≤2 lines
│ Collaborators can add, remove, and reorder songs. │  ToggleSwitch (SettingsCard.CompactToggleStyle)
│ Public playlist                   [O====]         │  caption states the CURRENT setting:
│ Only you and collaborators                        │   public → "Anyone can find and view"
└───────────────────────────────────────────────────┘   private → "Only you and collaborators"
Rows appear only when `CanEditMetadata && CanAdministratePermissions`; the CTA only when `CanAdministratePermissions`.
A saving row shows the status chip between the caption column and the switch, and disables the switch.
The link the CTA copies is `{m.ShareUrl ?? SpotifyPlaylistWebUrl(uri)}` + `"?pt=" + token` from
`CreateContributorInviteAsync` (`PlaylistInlineEdit.cs:822-823`); with no clipboard seam it OPENS the url instead, and
the label then never flips to "Copied" (`:826-834`). A successful copy also refreshes the page model
(`RefreshPlaylistDetailAsync`, `:832`) — the only write on this surface that re-reads.
Both toggle writes are OPTIMISTIC-ONLY: `PatchDetail` flips the local model and the ack (or another device's dealer
permission push) re-publishes the page in place — there is no re-read after either (`:838-860`).
```

### W18 — Owner overflow (rail ⋯, 40×40) · W19 — Playlist picker · W20 — Add-to-playlist submenu

```
W18 (BottomEdgeAlignedRight)      W19 picker flyout (320)            W20 submenu (from a row's context menu)
┌────────────────────────┐        ┌──────────────────────────┐       ┌──────────────────────────┐
│ 👥 Invite collaborators│        │ pad 8, gap 4             │       │ ＋ New playlist          │
│ ────────────────────── │        │ ┌──────────────────────┐ │       │ ──────────────────────── │
│ 🗑 Delete playlist     │        │ │ Find a playlist      │ │ 300×32│ Late Night Mix           │
└────────────────────────┘        │ └──────────────────────┘ │       │ Gym                      │  ≤10 rows (MaxInline),
  Delete → SettingsShared.Confirm │ ＋ (40 tile r6) New playlist│ 44  │ … MRU first, then rootlist│  MRU order shared
  "Delete this playlist? It will  │ ─ scroll, MaxHeight 360 ─ │       │ ──────────────────────── │  with the picker
  be removed from your library.   │ ▓▓ Late Night Mix        │ 44    │ More playlists…          │  → opens W19 in a
  This cannot be undone."         │ ▓▓ Shared Mix           │       └──────────────────────────┘   ContentDialog
  → DeletePlaylistAsync → go home │    Collaborator  (12/2nd)│
                                  │ (art 40 r6, gap 10,      │
                                  │  pad 6/0/8/0, r4 row)    │
                                  │ empty → "No playlists    │
                                  │  found" (h 44, 13/2nd)   │
                                  └──────────────────────────┘
W18 is NOT a fixed pair. `Invite collaborators` + its separator appear only when `CanAdministratePermissions`
(PlaylistInlineEdit.cs:1136-1141), so an owner without that right gets a ONE-ROW menu ("Delete playlist", no separator).
The button itself is absent unless `SpotifyEditsLive(svc) && Live(m) && IsOwner`. It carries NO `Focusable` / `Role`
in 0.2.9 (`:1108-1116`) — an a11y gap to FIX in 0.3, not to copy.
W19: there is NO divider between "New playlist" and the list (the "─ scroll ─" above is an annotation, not a rule); the
"+" tile's glyph is `Icons.Add` 20/TextSecondary; the scrolled row list has its own inner gap of 2; rows are keyed by uri;
the "Collaborator" subline is `p.CanEdit && !p.IsOwner` (PlaylistPicker.cs:205). The panel mounts fresh on every open
(the overlay's thunk), so it is always the current rootlist, and it calls `store.EnsurePlaylists()` on mount (`:55`).
W19 has a SECOND arm the sketch does not show — the **MOVE** picker: `Deposit` overrides what a pick does (deposit into
the target, then remove the rows from their source) and `ExcludeUri` hides the source playlist, which a move could only
be a no-op against (`PlaylistPicker.cs:43-47`, `Menus.cs:509-534`). Everything else is identical, including the order.
W19 row ART is three-armed (`PlaylistPicker.CoverOf`, `:159-165`): the playlist's own cover · else a 4-tile MOSAIC image
(`new Image("", MosaicTiles: tiles)`) when `MosaicTiles.Count >= 4` · else the FIRST tile alone · else seeded generated
art from an ordinal hash of the uri (`SeedFrom`). The launcher's default placement is `FlyoutPlacement.BottomLeft`.
W20: the submenu has NO separator after "New playlist" — the ONLY separator is the one before "More playlists…"
(Menus.cs:308-331). Inline playlist rows carry no icon (the "New playlist" row does: `Icons.Add`), and the whole cascade
is DISABLED (not hidden) when the payload has no tracks or there is no library bridge. "More playlists…" is itself
disabled without an overlay service. The dialog it opens is titled with the SUBMENU's own label ("Add to playlist" /
"Move to playlist"), has an EMPTY primary button (`d.PrimaryText = ""` — the rows are the action) and a "Cancel" close,
defaulted to Close (`Menus.cs:516-531`).
```

### W21 — Same-list reorder in progress

```
┌ right ────────────────────────────────────────────────────────┐
│ 1  ▓▓ Track one                                     ♥   3:41  │
│ 2  ▓▓ Track two                                     ♥   3:12  │  ← the dragged rows are virtually removed
│ ╔═══════════════════════════════════════════════════════════╗ │
│ ║ ▓▓ Track five                Artist                       ║ │  ← gap preview: ≤ SortableMath.DefaultPreviewCap
│ ║ ▓▓ Track six                 Artist                  [+3] ║ │     cards, row height = the live rowH,
│ ╚═══════════════════════════════════════════════════════════╝ │     Fill FillSolidSecondary, border 1 AccentDefault,
│ 5  ▓▓ Track seven                                   ♥   2:58  │     Shadow Elevation.Card, r4, margin/pad = the row's
└───────────────────────────────────────────────────────────────┘     own inset, "+N" pill AccentSubtle/AccentTextPrimary
        chip under the pointer: [▓▓ Track five · Artist  ⑤]  "Move 5 songs"
        NO spotlight scrim (SpotlightWhen = !sameList). A CROSS-list deposit keeps the scrim and says "Add 5 songs" /
        "Add to {playlist}" for a container whose count is unknown.
        Two preview-card states the sketch above does not show (PlaylistInsertionPreview.cs:35-50):
          · `Settings › Appearance › track artwork` HIDDEN ⇒ the cards drop their art entirely
            (`showArtwork: !AppearancePrefs.TrackArtworkHidden`, DetailTracks.cs:1206) — text-only cards, same geometry;
          · a CONTAINER payload with no resolved track snapshot ⇒ ONE card, art = a `FillSubtleSecondary` tile with
            `Icons.MusicNote` 16/TextSecondary, title = the payload's NAME, subtitle empty.
        The "+N" pill is `Radii.PillAll`, not the card's 4.
```

### W22 — Drag refusal (chip caption beside the not-allowed glyph)

```
[▓▓ 3 songs ③] ⃠  Clear sorting to reorder      ← sort ≠ Index-asc
[▓▓ 3 songs ③] ⃠  Clear filters to reorder      ← a query or a non-default filter
[▓▓ 3 songs ③] ⃠  Still syncing — try again in a moment   ← a row (or the anchor) has no membership item_id
[▓▓ Album    ] ⃠  Can't edit this playlist      ← read-only destination
[▓▓ Album    ] ⃠  Still loading…                ← the destination's own list is still Pending (drag.stillLoading)
[🎙 Show     ] ⃠  Nothing to add                ← a payload that can resolve no tracks and is not an artist
[👤 Artist   ] ⃠  Can't add an artist           ← no resolvable track set
```
The order is fixed and is the table's, not the caller's: NotEditable → Loading → NoTracks → (cross-list ⇒ accept) →
Sorted → Filtered → Syncing (`WaveeDragRules.cs:126-137`). A CROSS-list copy is legal under any sort or filter — only the
three same-list arms can be refused for the display order, which is why a foreign drag never shows the last three
sentences. `Syncing` is deliberately LAST of the three: sorting and filtering have a remedy, "still syncing" is a wait.

### W22b — The write-failure sentences (toast copy, `PlaylistEditErrorKinds.KeyFor`)

Every failing mutation on this surface goes through ONE mapped chokepoint — never raw exception text
(`PlaylistEditErrors.cs:16-29`). The cell is (kind × verb); only `Reorder` currently changes any copy.

| `PlaylistMutationFailure` | generic verb | verb = `Reorder` | severity |
|---|---|---|---|
| `Conflict` | "This playlist changed — try again." | "Couldn't reorder — the playlist changed elsewhere." | Error |
| `Forbidden` | "You can't edit this playlist anymore." | same | Error |
| `Deleted` | "This playlist was deleted." | same | Error |
| `NotSupported` | "Sign in to edit Spotify playlists." | same | Error |
| `Unknown` (+ any unmapped) | "Something went wrong. Try again." | same | Error |
| `Offline` | "You're offline — this change will sync when you're back." | same | **Informational** |
| `Pending` | "Saved on this device — still syncing." | "Still syncing — try again in a moment" (`drag.stillSyncing`) | **Informational** |
| `NoOp` | "Something went wrong. Try again." | "Already there" (`drag.alreadyThere`) | **Informational** |
| `Invalid` | "Something went wrong. Try again." | "Can't move here" (`drag.cantMoveHere`) | **Informational** |

`NotSupported` is `NotSupportedException` — the "this build cannot edit Spotify playlists" stub; the typed
`PlaylistMutationException` wins anywhere in the inner/aggregate chain so a wrapper task cannot downgrade a real
`Conflict` to `Unknown` (`PlaylistEditErrorKinds.cs:26-42`). Note the asymmetry the table makes visible: `NoOp` and
`Invalid` are Informational but still say "Something went wrong" for every verb that is not a reorder — a real 0.2.9
copy gap to resolve in 0.3, not to copy.

### W23 — Selection / multi-select

```
┌ right ────────────────────────────────────────────────────────┐
│ ┌──────────────────────────────────────────────────────────┐ │  The command bar SWAPS to the SelectionCommandBar
│ │ 3 selected   [▶][＋ Add to][➜ Move to][🗑 Remove][✕]      │ │  in the SAME 44-DIP surface (DetailTracks.cs:1934-1938)
│ └──────────────────────────────────────────────────────────┘ │  — see 01-track-row.md for the bar itself.
│ ☑ 1  ▓▓ Track one …                                          │  Checkbox lane appears when MultiSelect is on OR
│ ☑ 2  ▓▓ Track two …                                          │  ≥2 rows are selected (DetailTracks.cs:843).
│ ☐ 3  ▓▓ Track three …                                        │  Alt+↑ / Alt+↓ moves the selected BLOCK.
└───────────────────────────────────────────────────────────────┘
```

### W24 — Tune (radio/mix playlists only)

```
command bar                       flyout (336 wide, BottomEdgeAlignedRight)      teaching tip (first run)
[✨ Tune ⌄]  ← 32 tall, r4,       ┌──────────────────────────────────────┐      ┌──────────────────────┐
  pad 9/0/10/0, gap 6            │ ┌────┐  Tune this mix                 │      │ Tune this playlist   │
  active: Fill accent@0.11,      │ │ ✨ │  Choose a direction and Wavee  │      │ Adjust the mix's     │
   hover .17, pressed .08,       │ │36r10│ will rebuild the track list.  │      │ direction right from │
   ink AccentTextPrimary         │ └────┘  (accent@0.13 tile)            │      │ here — Wavee rebuilds│
  idle: transparent, TextSecondary│ ─────── divider 1, m 8,3,8,4 ─────── │      │ the track list.      │
  busy: ProgressRing 16          │ ◉ Chill                     Current   │      └──────────────────────┘
                                 │ ○ Upbeat                              │       WaveeTips, anchored to the
                                 │ ○ Instrumental                        │       button; dismissed by using it
                                 │ ───────────────────────────────────── │
                                 │   Reset tuning                        │
                                 └──────────────────────────────────────┘
Applied → Success toast "Playlist tuned"; failure → Error toast "Couldn't tune this playlist. Try again."
The radio rows are `MenuFlyoutItem.RadioItem`; the CURRENT one is `enabled: false` and carries "Current" as its
ACCELERATOR text (the right-hand column), not as a second label (DetailTracks.cs:3806-3815).
"Reset tuning" (and the separator above it) exists ONLY while a tuning is selected — `PlaylistTuneMenuModel.ResetOption`
returns null when `SelectedIdentifier is null` (PlaylistTuneMenuModel.cs:34-41). An untuned mix's flyout is header +
divider + the choices, nothing more.
The labelled button ends in `Icons.ChevronDown` 9 (accent when active, else TextTertiary); the icon-only arm is 32×32 with
the "Tune this playlist" tooltip. Busy: `IsEnabled = false`, cursor Arrow, a 16-DIP ProgressRing in the glyph's place.
Idle (untuned) hover/press are the ordinary `FillSubtleSecondary` / `FillSubtleTertiary`; only the ACTIVE arm tints.
```

### W25 — Collaborator pile + flyout

```
rail / hero attribution                          flyout (280, MaxHeight 360, pad 8)
┌──────────────────────────────────────┐         ┌────────────────────────────┐
│ (◉)(◉)(◉)(+2) ⌄  4 collaborators  [👥]│         │ (32) Christos              │ 44-DIP rows, r6,
│  ↑ 28 avatar + 2 ring = 32 outer,    │         │ (32) Sofia                 │ gap 12, pad 8/0/8/0,
│    overlap −12, ring Fill FillSolidBase│        │ (32) Marc                  │ Role MenuItem, click closes
│  chevron 8 TextTertiary               │        │ … scroll 264 wide,         │
│  label 14/700 AccentTextPrimary       │        │   AutoEdgeFade             │
└──────────────────────────────────────┘         └────────────────────────────┘
label: ≥2 members → "{n} collaborators"; 1 member + IsCollaborative → "Open to collaboration"; else the member's name.
button hover Fill FillCardDefault, pressed FillSubtleTertiary, r8, pad 6/4/6/4, inner gap 4; tooltip "View collaborators".
The chevron glyph is `Icons.ChevronDownSmall` 8/TextTertiary (not the 9-DIP `ChevronDown` the Tune button uses); the
button carries NO scale tier at all (no HoverScale/PressScale) — it is the one control on this page whose press is a
pure brush change. Outer row gap `Spacing.S` 8, `MaxWidth = cover`. Flyout placement `BottomEdgeAlignedLeft`.
Keyboard: Down / F4 opens the flyout.
Flyout geometry, exactly: outer 280 wide / MaxHeight 360 / pad 8 → inner ScrollEl 264 wide / MaxHeight **344** /
ContentSized / AutoEdgeFade → a 264-wide column with a 2-DIP row gap (CollaboratorFacePile.cs:154-160).
The pile mounts nothing at all when `Collaborators` is empty; the pile-vs-owner-row choice is
`ShowCollaborators(m) = Collaborators.Count > 0 && (IsCollaborative || Count >= 2)` (DetailRail.cs:563-564) — the SAME
predicate in the rail and in the vertical hero, which is why a collaborative playlist keeps its overlaps at every width.
"+N" counts `members.Count − 4`, so five members render five circles (four faces + the pill), not four.
```

### W26 — Daylist + chart rail rows

```
rail (compact)                                   hero (Compact:false)
02:41:09  Next update at 6:00 AM                 0 2 : 4 1 : 0 9   Next update at 6:00 AM
 ↑ cells 10×20, colon 6, 14/300 Display          ↑ cells 13×28, colon 8, 20/300 Display
 ink = WaveePalette.TextInk(accent); expired → Tok.TextTertiary at 00:00:00
 hours clamp at 99 (two fixed cells — a >4-day window clamps rather than reflowing, FlipCountdown.cs:97)
 the 1 s UseInterval is `enabled: !expired`, so a closed window costs no wakes; the caption is Ui.Caption/TextTertiary
3 new entries · Sep 8      ← Caption, Tok.TextSecondary (chart playlists, ChartNewEntries > 0)
   date = DetailFormat.ChartUpdatedDateLabel: absolute "MMM d" (same year) / "MMM d, yyyy" — never relative
```

### W27 — Scrolled, vertical arm (sticky context band)

```
┌ page ────────────────────────────────────────────────────────────┐
│ Late Night Mix                              Find  Filter  Play   │ 56 DIP band, unpainted (the page tone shows
│ Christos · 47 songs · 2 hr 51 min                                │ through), pad = compactLeft, actions are
├──────────────────────────────────────────────────────────────────┤ WaveeCta.TextAction (14/600/20)
│ #  TITLE …            ← the list's own column header pins under  │ Reveal ramp: CompactRevealBand 44 ending at
│ 12 ▓▓ Track twelve …                                             │ the collapse floor; expanded hero translates
└──────────────────────────────────────────────────────────────────┘ −collapseDistance and fades out (linear binds)
When search opens, the identity block is replaced IN PLACE by the field (one zero-gap slot).
When ≥1 row is selected in multi-select, the band's content swaps to the batch command bar at the same 56 DIP.
```

### W28 — Hover / pressed / focused / now-playing (summary)

```
title pill      : rest transparent + transparent border → hover FillSubtleSecondary + StrokeControlDefault (engine HoverFade)
pencil          : opacity 0 → 1 on hover && status == idle, MotionTok.ControlFast (150 ms)
cover           : no hover fill; the overlay scrim cross-fades at 250 ms
Play pill       : HoverScale 1.04 / PressScale 0.96 (WaveeMotion.ScaleStandard), auto-lightened accent fill
♥ (40)          : HoverScale 1.07 / Press 0.92 (ScaleEmphatic) + Interaction.Subtle backplate (SaveButton.cs:43)
⇗ / ⋯ (40)      : ScaleSubtle 1.02 / 0.98 — NOT the emphatic tier (PlaylistInlineEdit.cs:786, 1112). The rail's three
                  40-DIP circles are therefore two different press depths on purpose: the heart is the loud one.
                  (`DetailRail.Fab` — the Heart FALLBACK when there is no save uri — is ScaleEmphatic, :603.)
hero satellites : 32×32, HoverFill FillSubtleSecondary, pressed FillSubtleTertiary, ScaleStandard, brush 83 ms
hero ⋯ (32)     : ScaleStandard + Interaction.Subtle, glyph 16 — and NO HoverFill (unlike its satellite siblings)
invite pill     : ScaleSubtle (1.02 / 0.98), border 1 StrokeControlDefault
rows            : 01-track-row.md; now-playing row re-skins in place through its own binds (no list re-render)
every tier collapses to 1.0 under reduced motion (ScaleTier.Hover/Press read Motion.ReducedMotion, WaveeMotion.cs:179-182)
```

### W29 — Hero attribution: the two arms (the one the rail does NOT share)

```
collaborative (ShowCollaborators true)            solo / editorial (the plain arm)
┌──────────────────────────────────┐              ┌──────────────────────────────────┐
│ (◉)(◉)(◉)(+2) ⌄ 4 collaborators  │              │ Christos                         │
│                        [👥 Invite]│              │  ↑ a BARE TextEl: 12/600,        │
└──────────────────────────────────┘              │    Tok.TextSecondary, 1 line,    │
 the same CollaboratorFacePile the rail mounts,   │    ellipsis, MaxWidth = contentW │
 keyed pl-collab:{contentW}                       └──────────────────────────────────┘
                                                   NO 24-DIP avatar. NO invite pill. NO hover.
                                                   (DetailVerticalHero.cs:468-481)
```
The rail's owner block and the hero's attribution are NOT the same element. The rail always renders
`PlaylistOwnerRow` (avatar 24 + name + the self-gating InviteButton); the hero renders the face pile for a collaborative
playlist and otherwise a plain run. Consequence: **at mode 3 a solo owner has no invite affordance on the page body at
all** — the only way in is the ⋯ → "Invite collaborators" row. Decide in 0.3 whether that is intended; today it is
silent asymmetry, not a decision anyone wrote down.

### W30 — The meta line's four grammars (rail + hero + context band read the same string)

```
not loaded      ░░░░░░░░░░░░░░░░░░░░░░   shimmer bar shaped by "00 songs · 0 hr 00 min"   MembershipLoaded false
                                          (MetaLine is "" then — DetailPage.cs:524)
plain           47 songs · 2 hr 51 min                                    Strings.Detail.MetaLine
with saves      50 songs · 34.5M saves · 2 hr 41 min                      Strings.Detail.MetaLineSaved, saveCount > 0
                 ↑ compact above 999 ("{value} saves"); a genuine 0 omits the segment entirely
MIXED content   48 songs · 3 episodes · 5 hr 12 min                       DetailPage.cs:515-518
                 ↑ a playlist holding episodes states BOTH kinds; the songs half is the SERVER count minus the
                   episodes we joined, so a songs-only playlist is byte-identical to the plain arm
```
Durations come from the RESIDENT rows (`Σ DurationMs`), the count from the server header — which is why the shimmer
arm exists at all: the two disagree until the membership lands.

---

## 3. Tokens

| Element | Size (DIP) | Padding / gap | Radius | Type style | Colour / brush | Material / elevation | Source |
|---|---|---|---|---|---|---|---|
| Rail column | W = 240 (mode 0 default, grip 180…480) · 224 (mode 1) · 188 (mode 2) | pad 16/24/8/24, gap 14 | — | — | `Tok.FillLayerDefault` | — | `DetailRail.cs:19-20,262-267`, `DetailShell.cs:192`, `DetailRailPolicy.cs:25` |
| Rail grip strip | 16 (expanded) / 20 (collapsed) | — | — | — | Splitter default | — | `DetailShell.cs:200-207,797-817`, `Splitter.cs:22` |
| Compact strip | 96 wide, cover `max(48, 96−16)` = 80, chevron 80×28, glyph `Icons.ChevronRight` 14 | pad 8/16/8/16, gap 8 | cover 8, chevron 4 | title 12/600 ≤2 lines, `WrapWholeWords` + ellipsis | `Tok.FillLayerDefault`, chevron hover `FillSubtleSecondary`, glyph `TextSecondary` | — | `DetailRail.cs:304-354` |
| Cover (rail) | `CoverEdge = max(80, railW−24)` → 216 @ 240 | — | `Radii.Card` 8 | — | art, saturation 1.18 | `Elevation.Card` (dark 0 2 8 #00000033 / light 0 2 4 #0000001A) | `DetailRail.cs:96,145-162`, `Elevation.cs:18-21` |
| Cover (narrow header) — **ALBUM/SHOW only, unreachable for a playlist** (`DetailShell.cs:512,587`) | 140 | — | 8 | — | saturation 1.0 | `Elevation.Card` | `DetailRail.cs:363,411-424` |
| Cover (vertical hero) | stacked `clamp(w−2·pad, 96, 280)` · row `round(clamp(inner·0.44, 144, 240))` | — | 8 | — | saturation 1.18 | `Elevation.Card` | `DetailVerticalLayout.cs:52-63,133-143` |
| Cover decode | 256 px (`HeroCoverDecodePx`); hero uses `ArtworkDecodePx(artSize, measured)`, 256 pre-measure | — | — | — | — | — | `DetailRail.cs:25-26`, `DetailVerticalHero.cs:84` |
| Cover overlay | fills the cover; icon 32, label w 120 ≤2 lines | gap 6 | inherits clip | 12/600 | scrim `#000` A .52 (hover) / .68 (drop); ink `#FFF` | — | `PlaylistInlineEdit.cs:381-411` |
| Eyebrow (hero) | one line | — | — | `WaveeType.Eyebrow` | `Tok.TextTertiary` | — | `DetailRail.cs:590-593` |
| Owner row | avatar 24 | gap 8 | — | `WaveeType.TrackTitle`, Grow 1 ≤1 line | `Tok.TextPrimary` | — | `PlaylistInlineEdit.cs:749-758` |
| Collaborator pile | avatar 28 + ring 2 = 32 outer, overlap −12, ≤4 + "+N" | gap 4, button pad 6/4/6/4 | circle / button r8 | label 14/700 | label `Tok.AccentTextPrimary`; ring `Tok.FillSolidBase`; "+N" `FillCardDefault` + 10/700 `TextSecondary` | — | `CollaboratorFacePile.cs:20,70-132` |
| Invite pill (wide ≥260) | h 28, glyph `Icons.Friends` **12** | pad 10/0/12/0, gap 4 | 14 | 12/600 | border `Tok.StrokeControlDefault`, glyph + label both `Tok.TextPrimary` (no fill) | `Interaction.Subtle` brush 83 ms | `PlaylistInlineEdit.cs:917-930` |
| Invite icon (narrow <260) | 28×28, glyph `Icons.Friends` **14** | — | 14 | — | icon `Tok.TextSecondary` (the wide arm's is TextPrimary — a real inconsistency) | tooltip, `Interaction.Subtle` | `PlaylistInlineEdit.cs:905-915` |
| Page row (two-column) | `MaxWidth = 1600`, centred; right col `MinWidth` 300 (modes 0–2) / 0 (mode 3) | — | — | — | — | — | `DetailShell.cs:649,672,519`, `DetailLayoutBreakpoints.cs:61-66` |
| Hero title (rail) | size 40 (winH≥900) / 28, line 52 / 36, MinSize 18, ≤3 lines | — | — | `WaveeType.DetailHero` w 600 | `Tok.TextPrimary`; placeholder `Tok.TextTertiary` | — | `DetailRail.cs:183-189`, `DetailShell.cs:636-637` |
| Title hover pill | width = cover+16 | margin −8,−4,−8,−4 · pad 8,4,8,4 · gap 8 | `Radii.Control` 4 | — | `HoverFill FillSubtleSecondary`, border 1 transparent → `StrokeControlDefault` | engine HoverFade | `PlaylistInlineEdit.cs:523-537` |
| Title pencil | 20×20 box, glyph `max(14, size·0.4)` | — | — | — | `Tok.TextSecondary`, opacity bind 0/1 | `MotionTok.ControlFast` | `PlaylistInlineEdit.cs:554-560` |
| Title run inside pill | width = `wrapWidth − 20 − 8` | — | — | same rung | — | — | `DetailVerticalLayout.cs:218-224` |
| Title edit field | w = cover, h = `max(40, titleSize+12)` | — | control | `FontSize = titleSize` | `EditableText` defaults | — | `PlaylistInlineEdit.cs:501-516` |
| Save / Cancel | h 32 | pad 10/0/12/0, gap 6; row gap 8, Justify End | 16 | 12/600 | accent: `AccentTextPrimary@0.16 / .26 / .12`; plain: transparent / `FillSubtleSecondary` / `FillSubtleTertiary` | — | `PlaylistInlineEdit.cs:223-248` |
| Status chip | icon box 16, h auto | pad 8,3,10,3 · gap 6 | 12 | 11/600 | `Fill FillSubtleSecondary`, text `TextSecondary`, ✓ `AccentTextPrimary` | — | `PlaylistInlineEdit.cs:191-220` |
| Pending chip | same | same | 12 | 11/600 | same; icon `Icons.Refresh` 12 `TextTertiary` | tooltip `detail.edit.pendingSync` | `PlaylistInlineEdit.cs:161-189` |
| Meta line | width = cover, ≤2 lines (rail) / ≤1 (narrow header) / ≤2 wrap (hero) | — | — | `WaveeType.TrackMeta` | `Tok.TextSecondary` | shimmer via `SkelRegionEl` | `DetailRail.cs:77-94,199-200`, `DetailVerticalHero.cs:210-218` |
| Daylist strip (rail) | cell 10×20, colon 6 | gap 8 | — | 14/300 "Segoe UI Variable Display" | `WaveePalette.TextInk(accent)`; expired `TextTertiary`; caption `TextTertiary` | — | `FlipCountdown.cs:43,91-94,166-171` |
| Daylist strip (hero) | cell 13×28, colon 8 | gap 8 | — | 20/300 | same | — | `FlipCountdown.cs:42,91-94` |
| Chart caption | one line | — | — | `Ui.Caption` | `Tok.TextSecondary` | — | `DetailRail.cs:474-480` |
| CTA cluster | — | gap 12, inner gap 8, margin-top 4, `Wrap = true` | — | — | — | — | `DetailRail.cs:212-240` |
| Play pill | h 36 (`WaveeCta.PillHeight`) | pad 18,6,18,7 | `Radii.Full` (→18) | 14/700 (Bold) | fill = cover accent, ink = WCAG pick | Scale 1.04 / 0.96 | `WaveeCta.cs:65,89-107` |
| ♥ (rail) | 40×40, glyph 16 | — | 20 | — | `Tok.TextSecondary`; saved ♥ = the accent thunk, glyph `Icons.HeartFill` | `Interaction.Subtle`, Scale **1.07 / 0.92** (`ScaleEmphatic`) | `SaveButton.cs:40-48`, `DetailRail.cs:23,232-234` |
| ⇗ / ⋯ (rail) | 40×40, glyph 16 (`Icons.Share` / `Icons.More`) | — | 20 | — | `Tok.TextSecondary`; copied ⇗ = `AccentTextPrimary` | `Interaction.Subtle`, Scale **1.02 / 0.98** (`ScaleSubtle`) | `PlaylistInlineEdit.cs:786,793,1110-1116` |
| Eyebrow string (hero only) | one line | — | — | `WaveeType.Eyebrow` | `TextTertiary` | — | `nav.playlistCollaborative` "Playlist · Collaborative" / `nav.playlistPrivate` "Playlist · Private" / `nav.playlist` "Playlist" — `DetailRail.cs:581-583` |
| Hero satellites | 32×32 (`WaveeCta.IconButtonSize`), glyph 14 | — | `Radii.Control` 4 | — | `HoverFill FillSubtleSecondary`, pressed `FillSubtleTertiary` | brush 83 ms, Scale 1.04 / 0.96 | `DetailVerticalHero.cs:59,452-467` |
| Accent rule (hero) | 20×2 | margin-top 2 | — | — | page accent | — | `DetailVerticalHero.cs:206`, `DetailVerticalLayout.cs:212` |
| Description (rail) | width = cover, `descLines` 3 (winH<760) / 6 | — | — | 12 | `Tok.TextSecondary`; links = the PAGE accent in the read-only arm, `Tok.AccentTextPrimary` in the editable arm (§4) | — | `DetailRail.cs:249-253`, `DetailShell.cs:638` |
| Description (hero) | width = `contentW` (≤640), 3 (row) / 4 (stacked) lines | — | — | **13 read-only / 12 editable** (the editable arm is the rail's 12-px `PlaylistInlineEdit.Description`, `DetailVerticalHero.cs:258-261`) | `Tok.TextSecondary` | — | `DetailVerticalHero.cs:262-269`, `DetailVerticalLayout.cs:187` |
| Description edit field | w = contentW/cover, h 72 | — | control | 12 | `EditableText` | clipped, scrolls | `PlaylistInlineEdit.cs:634-656` |
| Notice bar | full width | pad 16/8/16/4 | InfoBar | InfoBar | `InfoBarSeverity.Informational` | — | `DetailNoticeBar.cs:56-88` |
| Picker panel | 320 wide; field 300×32; rows 44; scroll MaxHeight 360 (`ContentSized`) | pad 8, panel gap 4, **row-list gap 2**, row inner gap 10, row pad 6/0/8/0 | row 4, art 6 | name 14, sub 12, empty 13 | `TextPrimary` / `TextSecondary`; "+" tile `FillSubtleSecondary` + `Icons.Add` 20 | `Interaction.Subtle` | `PlaylistPicker.cs:131-215` |
| Picker art | 40×40, decode 80 | — | 6 | — | cover → 4-tile MOSAIC (≥4 tiles) → first tile (1–3) → seeded generated art | — | `PlaylistPicker.cs:159-165,199` |
| Insertion card "+N" pill | — | pad 8/2/8/2 | `Radii.PillAll` 16 | 12/600 | `Tok.AccentSubtle` fill, `Tok.AccentTextPrimary` ink | — | `PlaylistInsertionPreview.cs:60-66` |
| Tune flyout icon glyph | `Icons.RefineSparkle` **18** inside the 36 tile (the BUTTON's is 16) | — | — | — | `Tok.AccentTextPrimary` | — | `DetailTracks.cs:3860-3868` |
| Insertion preview card | h = live rowH | margin `TrackRow.RowInset` 8, pad `PadX−RowInset` 8, gap 12 | `Radii.Control` 4 | 14/600 + 12 | `Fill FillSolidSecondary`, border 1 `Tok.AccentDefault`; "+N" `Tok.AccentSubtle` / `AccentTextPrimary` | `Elevation.Card` | `PlaylistInsertionPreview.cs:60-79` |
| Rec header row | MinHeight = rowH; refresh 32×32 | pad 16/0/16/0, gap 12 | 16 | `Ui.BodyStrong` 14/20/600 | `TextPrimary`; note 12 `TextTertiary` | `Interaction.Subtle` | `DetailTracks.cs:2973-2997` |
| Rec row | MinHeight = MaxHeight = rowH, art 40 | pad 8/0/8/0 | — | ArtCard | — | — | `DetailTracks.cs:3004-3024` |
| Access flyout | 300 wide; CTA h 34; divider 1 | pad 16, gap 12; CTA gap 8 | CTA 17 | title 14/700, hint 12, row 13/600 + 11.5 | `AccentDefault` → `AccentSecondary` → `AccentTertiary`; divider `StrokeDividerDefault` | popup | `PlaylistInlineEdit.cs:1006-1068` |
| Tune button | h 32, w auto (labelled) / 32; glyph `Icons.RefineSparkle` 16 + `Icons.ChevronDown` 9 | pad 9/0/10/0, gap 6 | 4 | 12/600 | active `AccentTextPrimary` + fill @0.11/.17/.08; idle `TextSecondary` on `FillSubtleSecondary` / `FillSubtleTertiary` | brush `Motion.ControlFaster` 83 ms | `DetailTracks.cs:3898-3933` |
| Tune flyout | 336 wide; icon tile 36 | pad 0/8/0/6; header pad 14/9/14/11, gap 12 | tile 10 | `BodyStrong` + `Caption` | tile `AccentTextPrimary@0.13` | popup | `DetailTracks.cs:3840-3896` |
| Context band | h 56; ONE 1-DIP hairline under the whole stuck surface (carried by the column row below it); title cap 280 | pad `compactLeft`, action gap 16, cluster gap 24, action pad-x 10 | — | `ContextBand.Title/Byline` | unpainted (page tone shows through); hairline `StrokeDividerDefault` | clip inset = band + column row, fade band 24 | `ContextBandLayout.cs:27,31,47-70` |
| Rec header refresh | 32×32, glyph `Icons.Refresh` 14 | — | 16 (circle) | — | `TextSecondary` | `Interaction.Subtle`, Scale 1.07 / 0.92 | `DetailTracks.cs:2990-2997` |
| Collaborator flyout | outer 280 / MaxHeight 360; inner scroll 264 / MaxHeight 344 | pad 8, row gap 2 | row 6 | 14/600 | `AutoEdgeFade` | popup | `CollaboratorFacePile.cs:134-160` |

---

## 4. Colour & material

| Input | Function (file:line) | Applied to | Transition |
|---|---|---|---|
| `m.Cover.Url` (or the first gradeable track cover when the playlist art cannot be graded — a user-uploaded cover / a mosaic) | `DetailShell.cs:276-291` → `Surfaces.SchemeFor` / `ChromeSchemeFor` | the page's palette source for everything below | resolved per render; a late grading repaints only the leaves |
| chrome scheme → `WaveePalette.ChromeAccent`; else `m.Accent` (Home card `extractedColors.colorDark`, ARGB) → `ChromeFromPayload`; else `Tok.AccentDefault` | `DetailShell.cs:304-310` | Play pill fill, `Surfaces.AccentRule`, `FlipCountdown` ink, saved-♥ ink, description link ink, `PreReleaseCountdown` | the `handlers` record is re-minted only when the accent value changes (`accentKey`, `DetailShell.cs:423-448`), so a re-tint costs one publish, not a page re-render |
| palette url | `CoverPaletteLeaves.PageTonePlane(paletteUrl, liveUrl, …)`, `DetailShell.cs:575-577` | ONE flat art-derived ground behind the whole page, alphas 0.20 dark / 0.30 light over Mica | cover-keyed leaf; a graded batch repaints one node, never the tree |
| palette url | `CoverPaletteLeaves.ShellTint(..., apply: _cfg.TwoColumn, …)`, `DetailShell.cs:321-323` | the window's shell material tint (flat arm; `Wash: null`) | claim on first publish and on KeepAlive reactivation, refresh after; never cleared on park, so the chrome never dips to neutral between two coloured pages |
| `WaveeSettings.ColorWashesEnabled == false` | `DetailShell.cs:213` | disables the tone plane + the shell tint (the accent stays) | immediate on the settings epoch |
| cover art | `saturation: 1.18` on `Surfaces.Artwork` | rail cover, vertical-hero cover, the editable arm (threaded so the editable↔read-only remount does not pop the vibrancy) | `DetailRail.cs:158-160`, `PlaylistInlineEdit.cs:305-308,373-374` |
| cover art (narrow 140 header) | `saturation` default 1.0 | `DetailRail.cs:420-422` | — |
| — | image overlays must NEVER use theme fill/text tokens (artwork can be any luminance) | the cover's editor overlay: black scrim A .52 / .68 + `#FFFFFF` ink | `PlaylistInlineEdit.cs:381-389` |
| accent | `WaveePalette.TextInk(accent)` | daylist digits (an accent FILL painted as ink on a wash of the same hue is unreadable) | `FlipCountdown.cs:86-90` |
| **`Tok.AccentDefault`** (the THEME accent — deliberately NOT the cover accent) | `PlaylistInlineEdit.cs:1015` | the "Copy invite link" CTA's fill (hover `AccentSecondary`, pressed `AccentTertiary`), its ink via `ColorContrast.PickContrast` | the flyout is chrome over the page, not part of it; every other accent on this surface is the cover's. Keep or unify DELIBERATELY in 0.3 — do not "fix" it by accident |
| **`Tok.AccentTextPrimary`** (again the theme accent, not the page's) | `DetailTracks.cs:3897,3900` | the Tune button + its flyout tile and radio ink | same call: a command-bar control, not a hero control |
| page accent (`h.Accent`) **vs** `Tok.AccentTextPrimary` | `DetailRail.cs:252` vs `PlaylistInlineEdit.cs:682` | description LINK ink — the READ-ONLY arm uses the page/cover accent, the EDITABLE arm hard-codes `AccentTextPrimary` | a real 0.2.9 drift: the same description changes its link colour when the viewer happens to own the playlist. Pick ONE in 0.3 (the page accent) |
| theme | `Tok.FillLayerDefault` on the rail column (Liked deliberately stays unlayered) | the contextual left column recedes while the rows stay on the base surface | `DetailRail.cs:288-298` |
| theme | `Elevation.Card` = dark `0 2 8 #00000033`, light `0 2 4 #0000001A` | the cover in every arm, the insertion preview cards | `Elevation.cs:18-21` |
| drop state | `Tok.AccentDefault` 1 px border + `Tok.AccentSubtle` "+N" pill | the insertion gap's preview cards | `PlaylistInsertionPreview.cs:73-77` |

Light/dark: every colour above is a token that flips with `Tok.Theme`; the only hard-coded colours on this surface are the
cover overlay's black scrim and white ink (deliberate — see `PlaylistInlineEdit.cs:382-384`).

---

## 5. Motion

Unless stated, motion samples the engine frame clock (`FrameTime` / `Motion`); the two exceptions are called out.

| Trigger | Target | Property | From → to | Duration | Easing | Delay / stagger | Reduced motion | Source |
|---|---|---|---|---|---|---|---|---|
| Full model lands (late row inserts) | rail rows: owner, meta, daylist, chart, description (`LateRow`) · hero rows: eyebrow, attribution, meta, pulse, chart, description (`late: true`) | Opacity | 0 → 1 | `Expressive.Fast` 250 | `SmoothOut` (0.22,1,0.36,1) | none | fade kept, travel snapped (`ReducedMotionPolicy.KeepFade`) | `DetailRail.cs:52-71`, `DetailVerticalHero.cs:147-158` |
| — | **the facts panel is NOT in that list** | — | — | — | — | — | — | `rail:likedfacts` is a plain `Row` (FLIP only) because the panel owns its own entrance — `DetailRail.cs:259-260`; in the hero arm it is not in the hero at all (the list FOOTER) |
| Read arm mounts (edit → read, or the model arriving editable) | the title / description HOVER PILL itself | Opacity (Enter) | 0 → 1 | engine default | — | — | fade kept | `PlaylistInlineEdit.cs:537,676` — the pill fades in; it does not pop back after a save |
| Pending count changes 0 → n | the "{n} changes pending" chip | Opacity (Enter) | 0 → 1 | engine default | — | — | fade kept | `PlaylistInlineEdit.cs:181` |
| A late sibling pushes a row | every keyed rail/hero row | Position (FLIP) | old origin → rest | 250 | `SmoothOut` | none | travel snapped | `DetailRail.cs:56-57` |
| Preview → full | cover (row 0) | — | — | — | — | — | — | deliberately NO motion (`DetailRail.cs:143-144`) |
| Read ↔ edit swap (title / description) | the persistent `AnimatedSwap` box | Size (Reflow) | old h → new h | 300 | `SmoothOut` | — | snapped | `PlaylistInlineEdit.cs:106-110`, `MotionRecipes.cs:243-244` |
| Hover the title / description | pill fill + border | HoverFade brush | transparent → `FillSubtleSecondary` / `StrokeControlDefault` | engine hover default | — | — | instant | `PlaylistInlineEdit.cs:530-533,664-667` |
| Hover (status idle) | pencil | Opacity (bound) | 0 → 1 | `MotionTok.ControlFast` 150 | `FluentStandard` | — | fade kept | `PlaylistInlineEdit.cs:557-558,695-696` |
| Hover / drag-over / saving | cover overlay | Opacity (bound) | 0 → 1 | `MotionTok.ControlNormal` 250 | `FluentStandard` | — | fade kept | `PlaylistInlineEdit.cs:392-393` |
| Save starts / finishes | status chip icon + text | keyed swap | `IconSwap` (scale 0.25, blur 2, 250) / `TextSwap` (dy ±4, blur 2, 150) | 250 / 150 | `EaseInOut` | — | fade kept | `PlaylistInlineEdit.cs:197-218`, `MotionRecipes.cs:229-239` |
| Saved → idle | status chip | unmount (Exit fade) | 1 → 0 | — | — | **1800 ms wall-clock hold** (`Task.Delay`) | — | `PlaylistInlineEdit.cs:1172` — the ONE non-frame-clock timing on this surface; port as a frame-clock timeout |
| Copy share link | share glyph | keyed `IconSwap` | ⇗ → ✓ → ⇗ | 250 | `EaseInOut` | 1600 ms `UseTimeout` (frame-clock, generation-guarded) | fade kept | `PlaylistInlineEdit.cs:769,774,811` |
| Daylist second boundary | each changed digit cell | Dy + Opacity (keyed remount inside a clipped cell) | new: +35 % h → 0, 0 → 1; old: 0 → −35 % h, 1 → 0 | `MotionTok.ControlFast` 150 | `FluentStandard` | — | crossfade only | `FlipCountdown.cs:135-155` |
| Live add / remove / move | surviving rows | Position (FLIP) | `(old−new+shift)·rowH` → 0 | engine displacement seed | — | — | seeds skipped when reduced | `DetailTracks.cs:1577-1597` |
| Live add | added rows | Dy + Opacity | −6 → 0, 0 → 1 | engine | — | 20 ms/row, capped at 8 | skipped | `DetailTracks.cs:1589-1595` |
| Curated re-cut (`IsReset`) | the whole list | keyed remount (mount entrance replays) | — | — | — | — | — | `DetailTracks.cs:1551-1558` |
| Breakpoint cross (column re-deal) | realized rows | Dy + Opacity | +6 → 0, 0 → 1 | engine | — | narrowing 24 ms/row cap 6; widening 16 ms cap 4; skipped on a reversal <200 ms | skipped entirely (`Motion.ReducedMotion`) | `DetailTracks.cs:1609-1641` — reversal detection uses `Environment.TickCount64` (a debounce, not a motion sample) |
| Cold reveal ramp | rows past the ramp | shimmer → real (cross-fade per row) | — | per-frame chunk | — | `DetailRevealRamp.Chunk` per frame | — | `DetailTracks.cs:114-129` |
| Hero stacked ↔ row flow | hero box + artwork | Position + Size (`Reveal` / `ScaleCorrect`) | old → new | 280 | `SmoothOut` | — | snapped | `DetailVerticalHero.cs:46-54` |
| Scroll (vertical arm) | expanded hero | TransY + Opacity | 0 → −collapseDistance; 1 → 0 over the fade band | scroll-linked | `Linear` | — | unchanged (scroll-linked) | `DetailVerticalHero.cs:436-444` |
| Scroll (vertical arm) | context band | Reveal ramp | hidden → shown | `CompactRevealBand` 44 DIP of travel | — | — | unchanged | `DetailVerticalHero.cs:430` |
| Rail grip enters the resist zone | rail content | Opacity (bound `_railFade`) | 1 → fade | compositor bind | — | — | unchanged | `DetailShell.cs:779-789` |
| Command-bar promote/evict | toolbar commands | Position + Opacity | dx 8 / opacity 0 | 220 in, 150 out | `SmoothOut` / `FluentAccelerate` | — | fade kept | `DetailTracks.cs:230-235` |
| Search disclosure | the search box | Position + Size (Reflow, width) | icon → field | 260 open / 180 close | `SmoothOut` / `FluentAccelerate` | — | fade kept | `DetailTracks.cs:227-260` |
| Any button press | Play pill (0.96) · ♥ (0.92) · ⇗ / ⋯ / invite (0.98) · hero satellites (0.96) | Scale | 1 → tier | engine press | — | — | scale suppressed (tier accessors return 1f) | `WaveeMotion.cs:39-48,179-182` |
| Hover a hero satellite / the Tune button | backplate fill | brush cross-fade | transparent → `FillSubtleSecondary` | `WaveeMotion.Faster` 83 | — | — | instant | `DetailVerticalHero.cs:459`, `DetailTracks.cs:3918-3919` |
| Invite CTA / status swaps inside the access flyout | icon + label | keyed `IconSwap` / `TextSwap` | ring → ✓, "Copy invite link" → "Copied" | 250 / 150 | `EaseInOut` | — | fade kept | `PlaylistInlineEdit.cs:1032,1040` |
| Hover any `Interaction.Subtle` node (♥ · ⇗ · ⋯ · invite · rec-refresh · picker rows) | backplate fill | brush cross-fade | `FillSubtleTransparent` → `FillSubtleSecondary` → pressed `FillSubtleTertiary` | **83** (`BrushMs`, `MotionTokenId.ControlFaster`) | — | — | instant | `Interaction.cs:55-64,83-110` — the recipe sets brush + scale ONLY; it never sets `Role` or `Cursor` (see §9) |
| Cover file-drop enter/leave | overlay scrim alpha | Fill (discrete) | `#000` A .52 ↔ .68 | — | — | — | — | the DEPTH is a re-render, not a transition — only the overlay's OPACITY is bound (`PlaylistInlineEdit.cs:389,392`) |
| The 1 s daylist ping | — | — | — | — | — | — | — | `UseInterval(1000, enabled: !expired)` — an expired window schedules NO wake at all (`FlipCountdown.cs:82`) |

---

## 6. Interaction

### 6.1 Hero / rail

* **Cover** — hover (editable): scrim + camera + "Change cover". Click: `FilePicker.OpenFile` titled
  `detail.edit.pickCover`, filter `("JPEG", "*.jpg;*.jpeg")`. Drop (`DropKinds.Files`): first path only; non-JPEG raises a
  Warning toast; success patches `m.Cover` from the store header (`PlaylistInlineEdit.cs:413-432`). Press-and-drag: lifts the
  PLAYLIST as a resource payload (chip = cover + name, resting caption "Drag onto a playlist to add" only when it can copy
  tracks; a rootlist member gets "Drop between playlists or onto a folder") — `WaveeResourceDrag.cs:301-347,386-393`.
  Not editable: the cover is still a drag source, has no hover state and no click action.
* **Title** — hover: pill + pencil, cursor `IBeam`. Click: enter edit, caret at end, no select-all (`visual:false`,
  `PlaylistInlineEdit.cs:266-275`). Enter / blur commits, Esc cancels (`OnCommit` / `OnCancel` / `OnFocusChanged`).
  An empty or unchanged trimmed value is discarded. Commit records `previousName = the title as it stood when editing
  STARTED` so Undo cannot restore another device's rename (`PlaylistInlineEdit.cs:452-456,585-600`).
  A brand-new playlist opens IN edit mode once (`PlaylistCreateIntent.Take(uri)` in a layout effect keyed on the uri,
  `PlaylistInlineEdit.cs:474-483`).
* **Description** — same gestures; commit writes `null` for an empty string. Links inside the read state navigate through
  `RichText.RouteForUri` (`PlaylistInlineEdit.cs:682-684`).
* **Owner name / avatar** — display only (not a link in 0.2.9).
* **Invite** — click opens the access flyout (`FlyoutPlacement.BottomEdgeAlignedLeft` from the pill,
  `BottomEdgeAlignedRight` from the owner ⋯ menu), focus-trapped, light-dismiss, `ConstrainToRootBounds = false`.
  Tooltip (narrow arm) `detail.edit.inviteCollaborators`.
* **Collaborator pile** — click or Down / F4 opens the member flyout; rows close it on click (no navigation in 0.2.9).
* **Play** — plays the VISIBLE (sorted/filtered) order from the top via `PlayAllOverride[0]` when the list has mounted,
  else `Play(0)` (`DetailShell.cs:410-411`; the cell is filled at `DetailTracks.cs:751` — `playAllCell[0] = () =>
  StartVisible(0)` — and `_checksVisible` is the unrelated memo at `:843`). The override cell is a mount-stable
  `UseRef(new Action?[1])`: a fresh array per render would hand the rail an empty cell every time the list had filled one.
* **♥** — `SaveButton.ToggleSaved(contextUri, title)`; filled = `lib.IsSaved(uri)`; ink = the page accent thunk.
* **⇗ Share** — copies `m.ShareUrl ?? SpotifyPlaylistWebUrl(uri)` to the clipboard, announces "Copied", flips to ✓ for
  1600 ms; with no clipboard seam it opens the url instead (`PlaylistInlineEdit.cs:801-814`).
* **⋯ (rail, owner only)** — `Invite collaborators` (👥) · separator · `Delete playlist` (🗑). Delete opens
  `SettingsShared.Confirm` with `detail.edit.deletePlaylistConfirm` and navigates `home` on success
  (`PlaylistInlineEdit.cs:1130-1158`). Rename is deliberately NOT in this menu — the inline title editor is the rename.
* **⋯ (vertical hero)** — row 1 is **always "Copy to playlist" on a playlist page**, never "Add to playlist": the label
  branches on `cfg.Heart == HeartMode.Follow || IsLikedUri`, and `DetailConfig.Playlist.Heart` is `Follow` for an OWNED
  playlist too (`DetailConfig.cs:199`, `DetailVerticalHero.cs:534-537`). Then `Play next` · `Add to queue` ·
  [separator · `Invite collaborators` · separator · `Delete playlist`] — the owner pair arrives through the SAME
  `AppendOwnerItems`, so it carries its own inner separator and its own `CanAdministratePermissions` gate
  (`DetailVerticalHero.cs:528-557`).
* **Tooltips** — compact strip: the playlist title; invite (narrow): "Invite collaborators"; pile: "View collaborators";
  pending chip: "Saved on this device — still syncing."; tune (icon-only arm): "Tune this playlist".
* **Accessibility names** — `Role = AutomationRole.Button` on the CTA/FAB/invite/pile/rec-refresh nodes; the collaborator
  flyout rows are `AutomationRole.MenuItem`; a refused keyboard reorder is announced assertively
  (`Announcer.Say(Strings.Drag.StillSyncing, assertive: true)`, `DetailTracks.cs:1351`); a folder/rootlist move announces
  through `Announcer.SayThrottled` (`WaveeResourceDrag.cs:464`); a successful create announces
  "New playlist: {name}" (`PlaylistCreateFlow.cs:79`).

### 6.2 List

* **Click / double-click / Enter** on a row — play from the visible order (01-track-row.md). A not-yet-released row is
  inert at the single funnel `PlayRow` (`DetailTracks.cs:3103`).
* **Right-click** — the shared track menu (`Menus.Tracks`), whose playlist-specific rows are: `Add to playlist ▸`,
  `Move to playlist ▸` (only when the host playlist is editable — `ActionRules.CanRemoveFromPlaylist`), and
  `Remove from this playlist` (last, after a separator).
* **Keyboard on a row** — `Enter` invokes (play), `Space` toggles selection (with the multi-select mods when the check
  lane is visible), bare arrows are the list's own roving focus (`DetailTracks.cs:3547-3554`).
* **Alt+↑ / Alt+↓** — move the selected BLOCK one position. Gated by `PlaylistReorderRules.AllowsBlockMove(canEditItems,
  naturalOrder, query, filters)`; a non-contiguous selection is refused; the selection is re-pointed at the landed rows
  immediately and put back on failure (`DetailTracks.cs:1326-1386`).
* **Drag a row** — payload carries the selection when the dragged row is selected, else just that row;
  `SourcePlaylistUri`/`SourceRows` are set only when the page is `Editable` (so a noticed page drags as a COPY).
  Chip title = the track title (or "{n} songs"), subtitle = its first artist, count badge = the selection size.
* **Drop into the list** — `InsertionOptions` (`DetailTracks.cs:1179-1212`): `Range = (TrackStart, View().Length)` so the
  hero/chrome prefix and the appended rec rows are outside the gap; `GapPreview` draws ≤3 cards; captions
  "Move {n} songs" / "Add {n} songs" / "Add to {name}"; commit = `DepositTracksAsync(acts, uri, title, payload, at)` with
  `at = OriginalInsertionIndex(displaySlot)` (PRE-move convention, pinned by `MoveRowsConventionTests`).
* **Drop on the page body** (rail/hero column) — APPEND (`insertionIndex: null`), caption "Add to {name}"; transparent for a
  same-list row drag (the list a few DIP away owns it) and for album/show surfaces (`DetailShell.cs:706-748`).
* **Sort / filter / search** — 04-detail-track-table.md. Their playlist consequence is the reorder gate: any non-Index-asc
  sort, any query, or any non-default filter refuses a same-list move with its own caption.
* **Recommendations** — `[+]` adds the track (awaited; the card stays until the write is confirmed, then the row is dropped
  and the batch auto-refills when it empties); `[↻]` re-fetches with the accumulated skip set (`DetailTracks.cs:3026-3086`).
  Rec rows drag as a COPY and open the single-track menu.

### 6.3 Picker and deposits

* Opening: the vertical hero's ⋯, the track-table's ⋯, any row menu's "Add/Move to playlist ▸ → More playlists…"
  (a centred `ContentDialog` there, because the menu that spawned it is gone by invoke time — `Menus.cs:509-534`).
* Typing filters by ordinal-case-insensitive substring over the name; order is MRU-first then rootlist order and is
  STABLE (no reshuffle under the pointer).
* "New playlist" creates through the ONE path (`PlaylistCreateFlow`): the name is the next unused
  `"{My Playlist} #N"` (the base is `Loc.Get(Strings.Sidebar.NewPlaylist)`, whose en-US value is literally `"My Playlist"`;
  first-unused search, culture-safe, case-insensitive), the optimistic row is in the store before the
  call returns, and the toast offers **Open** (a new playlist needs a name) while an ordinary add offers **Undo**.
  The title editor only auto-opens when the create **navigated** — `PlaylistCreateIntent.Arm` runs inside
  `if (navigate)` (`PlaylistCreateFlow.cs:50-55`) — so the picker's and the submenus' inline "New playlist"
  (`navigate: false`) creates the playlist, files the tracks and stays where it is. A create that dead-letters is retried
  under the SAME name with a NEW client-minted id (a rejected id is never reused, `:95`).
* A failed add/move reports the mapped sentence, never raw exception text; a successful add records the MRU
  (`Menus.RememberDeposit` → `WaveeSettings.PlaylistDepositRecents`, written only when the serialized MRU actually
  changed, so re-filing into the front playlist costs no settings write).
* **The same verbs reached from OFF this page (card menus, sidebar rows) do NOT all look like this page's.** Rename there
  is a `ContentDialog` over one `EditableText` seeded with the current name, never an inline editor
  (`ContainerActions.RenameDialog`, `ContainerActions.cs:141-199` — the sidebar's rows are recycled bound slots, so an
  inline editor would lose focus on the rootlist push the rename itself lands). Visibility there is two ABSOLUTE rows
  (`SetVisibility(public)` / `(private)`, `:250-262`) rather than this page's toggle, because the sidebar summary carries
  no live `IsPublic`; collaborative stays a toggle (`:263-278`). Those surfaces are 25-sidebar.md / 11-home-cards; what
  binds them to this chapter is only the create path and the deposit order.
* **The rootlist ORGANISATION verbs are not this surface's** — `FolderActions` (new folder, move up/down, Alt+↑/↓ between
  siblings, "Move to folder…", "Move out of {parent}", delete folder) is entirely sidebar/rootlist and belongs to
  25-sidebar.md. It shares exactly ONE thing with this page: `PlaylistCreateFlow` (`FolderActions.cs:33-39`, "New
  playlist in this folder"), which is why the numbered-name rule and the create notice live here.

---

## 7. Data & readiness in 0.3 terms

`PL` = `PlaylistFields` (proposed, §7 gaps). `E` = `Entities.Current.Edges`.

| Visual element | 0.2.9 source | 0.3 read | Readiness predicate (skeleton until true) |
|---|---|---|---|
| Cover | `DetailModel.Cover` ← `Playlist.Cover` (+ the nav-preview latch `ImageSource.PreferVisible`) | `p.ImageId` (`Column<StringId> Image`) | `p.Knows(PL.Identity)`; the latch becomes "keep the resident StringId when the new one is the same art" |
| Title | `Playlist.Name` | `p.TitleId` | `p.Knows(PL.Identity)` |
| Eyebrow "Playlist · Collaborative / Private" | `Capabilities.IsCollaborative`, `IsPublic` | `p.IsCollaborative`, `p.IsPublic` (Caps bits) | `p.Knows(PL.Capabilities)` — otherwise render the plain "Playlist" |
| Owner name + avatar | `Playlist.OwnerName`, `Owner.Avatar` | `p.Owner` (user slot) → `User(slot).Name/Image` | `p.Knows(PL.Identity) && owner.Knows(UserFields.Identity)` |
| Collaborator pile | `Playlist.Collaborators` (resolved `Owner[]`) | `E.PlaylistCollaborators.Targets(p.Slot)` → user slots | `E.PlaylistCollaborators.State[p.Slot] == complete` |
| Added-by cell | `Track.AddedBy` + `UserProfilesById` | `PlaylistTrackEdge.AddedBy` (user slot) | edge known; the user row's name may refine later (the cell shows the raw id until then — 0.2.9 does the same) |
| Added-by / Date / Video COLUMN EXISTENCE | `HasAddedBy = distinct AddedBy ≥ 2` · `HasDateAdded = any AddedAt` · `HasVideo = any VideoPresence` — all computed at map time (`DetailPage.cs:501-531`) | three derived facts on the playlist row, recomputed at commit over `p.TrackSlots` | needs the row fields, so the columns can APPEAR when the membership lands — 0.2.9 does the same and the table must not re-deal on it (04-detail-track-table.md's tier memo) |
| Membership-known itself | `MembershipLoaded(svc, p)` = `RealStore is null` ‖ uri is not a `spotify:playlist:` ‖ `store.HasMembership(uri)` (`DetailPage.cs:441-444`) | `E.PlaylistTracks.State[p.Slot] != 0` — a single fact with no "no store" escape hatch | the 0.3 column must reproduce the THREE-way truth, or `--fake`, Local Files and `wavee:playlist:*` sources pin on the shimmer forever |
| Meta line "N songs · M saves · duration" | `Playlist.TrackCount`, popcount, `Σ DurationMs` | `p.TrackCount`, `p.Saves`, `Σ Track.DurationMs` over `p.TrackSlots` | `E.PlaylistTracks.State != 0` (else the shimmer bar). The saves segment renders only when `p.Knows(PL.Saves) && Saves > 0`; the 250 ms grace becomes "paint without it, fill in later" |
| Meta line, MIXED arm ("48 songs · 3 episodes · …") | `EntityUri.KindOf(track.Uri) == Episode` counted at map time; the songs half = `p.TrackCount − episodes` (`DetailPage.cs:509-518`) | the same count over `p.TrackSlots`' kinds | needs the row kinds, i.e. `EnsureRows` — until then the plain arm. **The 0.3 plan has no episode-in-playlist story at all** (see `EpisodeInPlaylistJoinTests`) |
| Rows | `Playlist.Tracks` | `E.PlaylistTracks.Targets(p.Slot)` + `Track.Row` binds | page demands `EnsureEdges(PlaylistTracks)` + `EnsureRows(Row)` on mount; rows show their own per-field skeletons |
| List state (Loading / Empty / NoMatch / Rows) | `MembershipLoaded` + counts | `E.PlaylistTracks.State[p.Slot]` (0 unknown / 1 partial / 2 complete) + `Length` + view length | `State == 0 && Length == 0` ⇒ Loading (**the exact `PlaylistListState.IsLoading` shape**) |
| Notice strip | `DetailModel.Notice` ← `PlaylistPageNoticeRules` | `p.Notice` — a COLUMN written by the same pure rule at commit, never probed from the UI | always readable; `Known` gates `CanView` exactly as today |
| Edit affordances | `Capabilities.*` + `Notice` | `Playlist.Editable` / `EditableMetadata` over the Caps bits + `p.Notice` | `p.Knows(PL.Capabilities)`; unknown ⇒ read-only (never "revoked") |
| Daylist countdown | `DaylistExpiresAtMs/CreatedAtMs` | `p.DaylistExpiresAt` (int, app-epoch seconds) | `> 0` |
| Chart caption | `ChartNewEntries`, `ChartUpdatedAtMs` | `p.ChartNewEntries`, `p.ChartUpdatedAt` | `ChartNewEntries > 0` |
| Tune command | `Playlist.Tuning` | `E.PlaylistTuning.Payload(p.Slot)` + `p.TuningSelected` | `PlaylistTuneMenuModel.IsEligible` over the edge, unchanged |
| Pending chip | `LibraryBridge.PendingEdits(uri)` (outbox) | `Store.PendingFor(p.Uri)` as a per-uri signal | count > 0 |
| Create pending / failed | two `HashSet<string>` on the bridge | two bits in `Column<uint> Flags` on the playlist row | — |
| Recommendations | `svc.RealExtender.ExtendAsync(uri, skip, 20)` | `E.PlaylistRecs.Targets(p.Slot)` (synthetic, page-owned) + a fetch-state signal | gate = `Playlist.Editable(p) && Spotify.LoggedIn` |
| Page accent / tone | `Surfaces.SchemeFor(coverUrl)` | `Design.SchemeFor(p.ImageId)` (palette cache keyed by StringId) | paints neutral until graded; leaves repaint |
| Save/Follow heart | `LibraryBridge.IsSaved(uri)` | `User.Me` ↔ `E.Rootlist` / saved set membership | reverse index (`Contains`) |
| Deposit targets (picker, submenus) | `LibraryStore.Playlists` (rootlist summaries) + the MRU setting | `E.Rootlist.Targets(User.Me.Slot)` → playlist slots + `Platform.Settings` MRU string | rootlist edge state; an unloaded rootlist shows "No playlists found", never a wrong list |

### DATA GAPS

The plan's §4 gives Track a full field table and Playlist none (`Playlist.cs` is one line of the tree, 500 lines). Everything
below is shown by this surface today and has nowhere to live yet.

| Element | 0.2.9 source | Proposed 0.3 column / edge |
|---|---|---|
| Playlist identity (title, description, cover, owner, server track count) | `Playlist` record (`Wavee.Core/Domain/Models.cs:456-478`), decoded from playlist4 `ListAttributes` + `/decorate` | `PlaylistTable: Column<StringId> Title, Description, Image; Column<int> Owner (user slot), TrackCount;` flags `PlaylistFields.Identity` |
| Capabilities (7 bools incl. `Known`) | `PlaylistCapabilities` (`Models.cs:426-428`), wire `currentUserCapabilities` | `Column<byte> Caps` (bitmask CanView/CanEditItems/CanEditMetadata/IsCollaborative/IsOwner/CanAdministratePermissions) + a `PlaylistFields.Capabilities` Known bit — **`Known` must be the bit, not a bool**, or a thin rootlist row reads as "revoked" |
| Visibility + permission revision | `IsPublic`, `BasePermissionRevision` (`/permission/base`) | `Column<byte> Flags` bit `Public` + `Column<StringId> PermissionRevision` |
| Tombstone | `DeletedByOwner` (playlist4 `deleted_by_owner`, LATCHING: `incoming || current`) | `Flags` bit `DeletedByOwner`, merged with OR at commit |
| Notice verdict | `DetailModel.Notice` (`PlaylistPageNoticeRules.Next`, stateful/sticky) | `Column<byte> Notice` written by the ported rule at commit time (the rule needs `prev`, so it MUST be a column, not a UI-local) |
| Daylist window | `DaylistExpiresAtMs/CreatedAtMs` (format_attributes `expires`/`created`) | `Column<int> DaylistExpiresAt, DaylistCreatedAt` (app-epoch seconds, P7) |
| Chart facts | `ChartNewEntries`, `ChartUpdatedAtMs`, `ChartRankType` (format=="chart") | `Column<ushort> ChartNewEntries; Column<int> ChartUpdatedAt; Column<StringId> ChartRankType` |
| Per-row chart delta | `Track.Chart` (0.2.9 carries `ChartStatus/Pos/Prev` on the PlaylistTrackEdge in the plan) | already in `PlaylistTrackEdge` (plan §4.3) — but nothing renders it in 0.2.9; keep the fields, note the gap the other way |
| Save count (popcount) | `svc.PlaylistPopcount.GetSaveCountAsync` + a 250 ms grace | `Column<int> Saves` + `PlaylistFields.Saves`; the grace disappears (paint without, fill in on commit) |
| Tuning | `PlaylistTuning(Revision, Available[], SelectedIdentifier)` | `EdgeTable<TuningEdge> PlaylistTuning` with `TuningEdge(StringId Identifier, StringId DisplayName, byte Kind)` + `Column<StringId> TuningSelected` + `Column<uint> TuningRevisionHash` (the options are only valid while the revision matches membership) |
| Collaborators | `Playlist.Collaborators` + `UserProfilesById` (name-normalised map) | `EdgeTable<NoEdge> PlaylistCollaborators` (parent = playlist, targets = user slots); the map dies — `PlaylistTrackEdge.AddedBy` IS the user slot |
| User identity for owner/added-by | `Owner(Id, Name, Avatar)` | `UserTable: Column<StringId> Name, Image` + `UserFields.Identity` — the plan's `User.cs` is "the library IS edges" only |
| Cover mosaic (cover-less playlist) | `PlaylistSummary.MosaicTiles` (up to 4 album covers) | `Column<StringId> Mosaic0..3` or `EdgeTable<StringId> PlaylistMosaic`; needed by the picker rows, the sidebar and the drag chip |
| Card accent (`extractedColors.colorDark`) | `DetailModel.Accent` (rides the nav preview) | `Column<uint> Accent` on the playlist row — the nav-preview store is deleted in 0.3, so the column IS the preview |
| Share url | `SpotifyPlaylistWebUrl(uri)` | derive from `p.Uri` (no column) |
| Pending edits per playlist | `LibraryBridge.PendingEdits(uri)` over the mutation outbox | `Store.PendingFor(uri)` + one `Signal<int>` per open uri (the chip must not re-render the hero) |
| Create lifecycle | `_createPending` / `_createFailed` sets | two `Flags` bits + `Playlist.SettleCreate(slot, ok)` |
| Deposit MRU | `WaveeSettings.PlaylistDepositRecents` (newline-joined uris, cap 8) | unchanged (Platform settings); `PlaylistDepositTargets.Parse/Serialize/Remember` port verbatim |
| Episodes inside a playlist | `Track` rows whose uri kind is Episode (`EpisodeAsTrack`) | the plan's `Track` table has no kind discriminator — the meta line, the row art and the single-track menu all branch on it today |
| Track artwork hidden | `AppearancePrefs.TrackArtworkHidden(settings)` | a settings signal the ROW and the drag GAP PREVIEW both read (`DetailTracks.cs:1206`) — not just a row concern |
| Recommendations | `IPlaylistExtender.ExtendAsync(uri, skip, 20)` | `EdgeTable<NoEdge> PlaylistRecs` (synthetic parent = the playlist slot) + a page-local skip set |
| Cover palette / page tone | `Surfaces.SchemeFor(url)` cache | a `Design.cs` palette map keyed by the cover `StringId` — the plan has no home for the graded palette at all |

---

## 8. Pure rules to port verbatim

| Name | 0.2.9 file | Decides | Tests | 0.3 destination (CORE section) |
|---|---|---|---|---|
| `PlaylistListState` (+`PlaylistRowsState`) | `Features/Detail/PlaylistListState.cs` | Loading / Empty / NoMatch / Rows from (membershipKnown, total, visible) + the diagnostics spelling | `Wavee.Tests/PlaylistListStateTests.cs` | `Entities/Playlist.cs` |
| `PlaylistPageNoticeRules` (+`DetailNotice`, **5 values** incl. the album-only `MinifiedAlbum`) | `Features/Detail/PlaylistPageNoticeRules.cs` | the sticky notice verdict (`Next`, `Cold`, `ForAlbum`) — CreateFailed terminal, create-pending suppression, `Known` gating, owner never "revoked"; `ForAlbum` is deliberately STATELESS (no `prev`) while `Next` is not | `Wavee.Tests/Actions/PlaylistPageNoticeRulesTests.cs` | `Entities/Playlist.cs` (the enum is shared with the album chapter — one file, two owners) |
| `PlaylistEditErrorKinds` (+`PlaylistEditVerb`) | `Features/Detail/PlaylistEditErrorKinds.cs` | exception → `PlaylistMutationFailure` → loc key per (kind × verb); which failures are Informational | `Wavee.Tests/Actions/PlaylistEditErrorKindsTests.cs` | `Entities/Playlist.cs` |
| `PlaylistDepositTargets` | `Features/Detail/PlaylistDepositTargets.cs` | depositable-uri predicate, eligibility, MRU-then-rootlist ORDER, MRU remember/parse/serialize, `NextDefaultName` | `Wavee.Tests/PlaylistDepositTargetsTests.cs` | `Entities/Playlist.cs` |
| `PlaylistReorderRules` (+`PlaylistDropVerb`) | `Features/Detail/PlaylistReorderRules.cs` | same-list move legality (`AllowsSameListMove`), drop VERB, `AllowsBlockMove`, `BlockMoveTarget` (pre-move convention), `RowsAreKeyed` / `AnchorRowIsKeyedAt` / `AnchorRowIsKeyed`, `OriginalInsertionIndex`, **and `DisplayRowOf`** — the display↔original INVERSION the framework's virtual-removal math needs, O(1) in natural order with a defensive scan fallback (`:118-124`) | `Wavee.Tests/PlaylistReorderRulesTests.cs`, `MoveRowsConventionTests.cs` | `Entities/Playlist.cs` |
| `PlaylistEditErrors` | `Features/Detail/PlaylistEditErrors.cs` | not a rule — the engine-side RAISE (`Loc.Get(KeyFor(...))` + the Informational/Error severity split). Listed so the pure half and the toast half port together and nobody re-derives the severity | (covered by `PlaylistEditErrorKindsTests`) | `Entities/Playlist.UI.cs` |
| `PlaylistDropRefusalRules` (+`PlaylistDropRefusal`) | `Features/DragDrop/WaveeDragRules.cs:107-143` | the ONE accept/refuse table and its ORDER | `Wavee.Tests/WaveeDragRulesTests.cs` | `Entities/Playlist.cs` (or `Shell.cs` if the drag table is shared — keep it with the playlist, every caller is this page) |
| `TabDropRules`, `QueueDragRules`, `SidebarRailDropRules`, `WaveeDragKindMap` | `Features/DragDrop/WaveeDragRules.cs` | who else may take a playlist deposit | same file of tests | `Shell.cs` (25-sidebar.md / 18-shell-frame.md also depend on them) |
| `WaveeDragChipModel` | `Features/DragDrop/WaveeDragChipModel.cs` | chip title/subtitle/art/count resolution | `Wavee.Tests/WaveeDragChipModelTests.cs` | `Shell.cs` |
| `PlaylistTuneMenuModel` | `Features/Detail/PlaylistTuneMenuModel.cs` | tune eligibility (≥1 named Choice + a source), visible choices, **and the reset option's gate — null when nothing is selected, which is what makes "Reset tuning" appear and disappear** | `Wavee.Tests/PlaylistSignalsTests.cs:130-134` | `Entities/Playlist.cs` |
| `PlaylistPickerPanel`'s ordering call | `Features/Detail/PlaylistPicker.cs:122-129` | not a rule of its own — it is `PlaylistDepositTargets.Order` with the picker's `query`. Listed so nobody re-derives it while porting the panel | (covered by `PlaylistDepositTargetsTests`) | — |
| `ContextBandLayout` | `Features/Detail/ContextBandLayout.cs` | the sticky band's height, clip inset, cluster/action gaps, underline, title cap | `Wavee.Tests/ContextBandLayoutTests.cs` | 03-detail-frame.md's file (shared with the artist page) |
| `MembershipDiff` | `Components/MembershipDiff.cs` | the live add/remove/move/reset diff + `RowKey` identity | `Wavee.Tests/MembershipDiffTests.cs` | `Entities/Playlist.cs` (the choreography seeds) |
| `DetailRailPolicy` (+`RailScope`) | `Features/Detail/DetailRailPolicy.cs` | which surface gets a grip, default/clamped widths, the uniform scope | `Wavee.Tests/DetailRailPolicyTests.cs` | 03-detail-frame.md's file |
| `DetailLayoutBreakpoints`, `DetailVerticalLayout`, `DetailTrackTableRules`, `DetailTrackCommandBarLayout`, `DetailRevealRamp`, `DetailHeaderMergeRules` | `Features/Detail/*` | the frame's geometry ladders | the matching `*Tests.cs` | 03-/04-detail chapters |
| `PlaylistInsertionPreview.Cap` | `Features/DragDrop/PlaylistInsertionPreview.cs:18` | = `SortableMath.DefaultPreviewCap` (never a local literal, or the cards drift off the gap) | `Wavee.Tests/PlaylistInsertionGeometryTests.cs` | `Entities/Playlist.UI.cs` |

---

## 9. Re-author notes

**Must not be simplified.**

1. The **two-predicate edit gate** and its nine call sites. Collapsing it back to `Capabilities.CanEditItems` re-opens the
   bug it exists for: a playlist deleted or revoked under the reader keeps the capabilities it loaded with, so the page keeps
   offering edits the server can only refuse.
2. The **notice is sticky and stateful** (`prev` in, verdict out). A stateless re-derive relabels "couldn't be created" as
   "was deleted" on the next reload.
3. **`MembershipLoaded` is a separate fact from the count.** One input (the count) is exactly the bug
   `PlaylistListState` was written to kill.
4. **The keyed-reorder gate, both halves.** Payload-level (`RowsAreKeyed`) at accept time and anchor-level
   (`AnchorRowIsKeyedAt`) at commit time — there is no positional fallback on the wire, and the second half can only be
   asked once a slot exists.
5. **Pre-move insertion indices.** `BlockMoveTarget` returns `max + 2` for a down-move on purpose; pre-correcting for the
   removed rows moves the block twice (`MoveRowsConventionTests`).
6. **The reorder deferral.** Publishing a re-projection mid-gesture re-keys the list under the pointer. The liveness test
   must stay in the same statement as the write (`TryHold`), or a model parked after the last drag-epoch edge waits for the
   NEXT drag to end.
7. **The status chips are vertical chrome.** Never an inline sibling of the hero title. And the trailing row is
   ALWAYS mounted (it carries the persistent pending chip), so its arrival cannot move the title either.
8. **The cover's two gestures live on two nodes.** Framing box = drag source; editable cover = file drop target.
9. **`PlaylistDepositTargets.Order` is shared by three surfaces.** Re-deriving eligibility anywhere else recreates the
   three-copy defect its doc comment names.
10. **The empty owned playlist opens on recommendations**, and the recs gate is split in two halves on two clocks: the
    CAPABLE half (page config) may ride the list Key; the LIVE half (capabilities, session, extender) may only move the
    COUNT. Folding the live half into the Key destroyed and recreated every bound slot the moment the full model landed.

**Traps.**

* *Props freeze at mount.* `PlaylistInlineEdit.Cover/Title/Description/OwnerRow/ShareButton/InviteButton/PendingChip` all
  take a `Loadable<DetailModel>` (0.3: a handle) and read it INSIDE `Render`, precisely so the data can change without a
  remount; their `Key`s fold only GEOMETRY (`pl-edit-title:{w}:{size}:{weight}:{face}:{lineHeight}:{maxLines}`). Do not put
  model text in those keys. Conversely `FlipCountdown`, `SaveButton` and `PreReleaseCountdown` key on IDENTITY because they
  must remount when it changes.
* *The 8-DIP bucket.* Hero geometry is bucketed (`DetailVerticalLayout.BucketW`) before the facade keys are folded, or a
  sub-pixel resize frame remounts the editors every frame.
* *`ReuseGuard`.* The editable↔read-only cover flip changes the element TREE (a remount). 0.2.9 threads `saturation: 1.18`
  through the editable arm so the flip is structural only and does not visibly pop the cover's vibrancy — keep that.
* *Zero-alloc scroll vs per-row richness.* 0.2.9 reconciles them by making rows PLAIN bound slots whose every per-item value
  is a bind (`item.Text/.Show/.Signal`), and by pushing every playlist-specific decision into either the frozen
  `TrackRowsSnapshot` (an equality-gated memo) or a pure rule evaluated at gesture time. The recommendations extender is
  appended to the SAME bound list (a branch on the recycled index), not a second list. Keep both.
* *The drag chip resolver runs inside the 0-alloc frame region* — `Loc.Get` (a table lookup), never interpolation; the
  Liked chip art is built once at type init (`WaveeResourceDrag.cs:352-362`).

**Defects in 0.2.9 this surface should FIX in 0.3, not reproduce.**

* **The a11y gap is the ROLE and the CURSOR, not focus — and it is a CLUSTER, not one button.** First, correct the
  premise the chapter used to carry: **a clickable node is keyboard-focusable by default.** The engine derives
  `InteractionInfo.Focusable = TabStop ?? (Focusable || OnClick != null)` (`InputDispatcher.cs:3822-3823`), so an omitted
  `Focusable = true` costs nothing — every control below is in the tab order. What an omitted `Role` costs is the
  screen-reader NAME and role; what an omitted `Cursor` costs is the hand pointer that says "this is a button".
  `Interaction.Subtle` supplies neither: it expands brush + scale only (`Interaction.cs:83-110`).

  | control | `Role = Button` | `Cursor = Hand` | source |
  |---|---|---|---|
  | rail ⋯ owner menu | ✗ | ✗ | `PlaylistInlineEdit.cs:1108-1116` |
  | rail ♥ (`SaveButton`) | ✓ | ✗ | `SaveButton.cs:40-48` |
  | `DetailRail.Fab` (the ♥ fallback, the narrow header's ⇗) | ✗ | ✗ | `DetailRail.cs:599-605` |
  | collapsed strip: cover + chevron | ✗ | ✗ | `DetailRail.cs:311-338` |
  | picker rows + "New playlist" row | ✓ | ✗ | `PlaylistPicker.cs:174-202` |
  | rec-header refresh | ✓ | ✓ | `DetailTracks.cs:2988-2997` (complete) |

  Everything else on the page (⇗ share, invite, the pile, Save/Cancel, the invite CTA, Tune, hero satellites) declares
  both. So the honest statement is: **the rail's ⋯ and the collapsed strip's two hit targets are unnamed to a screen
  reader, and five controls never show a hand cursor** — including the ♥, the loudest button on the rail. Give every
  clickable node on this surface an explicit `Role` + `Cursor` in 0.3; do not port the table.
* The description's link ink differs between the read-only and editable arms (§4) — the same playlist changes colour
  depending on who is looking at it.
* **The hero's editable title diverges from its read-only twin in THREE ways — but not the one previously recorded.**
  `DetailVerticalLayout.TitleLinesMax = 2` (`:295`) caps the PLAN too, so `titlePlan.Lines` is 1 or 2 and the editable
  arm's 2-line `displayFace` default can only ever wrap MORE, never truncate earlier. What actually differs: (a) the
  editable run is measured at `EditableTitleMeasure(wrapWidth)` = `wrapWidth − 20 − 8` to leave the pencil room
  (`DetailVerticalLayout.cs:220-224`), so an owner's hero title wraps **28 DIP narrower** at the same point size;
  (b) the editable arm hard-codes `MinSize = 18f` (`PlaylistInlineEdit.cs:492,542`) instead of `titlePlan.MinSize`, so it
  auto-fits further down than the plan intended; (c) when the plan chose ONE line, the editable arm still permits two —
  overflowing the height the plan reserved from the cover's budget. Thread the plan (Lines AND MinSize) through the
  editable arm in 0.3.
* The hero's editable description renders at 12 px against the read-only arm's 13 px.
* A solo owner has no invite affordance anywhere on the mode-3 page body (W29).
* The invite affordance's own two arms disagree about ink: the wide pill's glyph is `TextPrimary`, the narrow round arm's
  is `TextSecondary` (`PlaylistInlineEdit.cs:914,927`) — the same control, two weights, at one breakpoint.
* "No suggestions right now" / "Recommended songs" / "{n} collaborators" / "Open to collaboration" / "View collaborators"
  are hard-coded English beside the loc keys that already exist (see the drift note below). **"Nothing here yet" is NOT
  among them** — it is `Loc.Get(Strings.Detail.Empty.NoTracks)` (`detail.empty.noTracks`, `DetailTracks.cs:413`).
* The hard-coded `members.Count + " collaborators"` is also not plural-aware, while the unused `detail.collabCount` key
  already carries the ICU plural — so a 1-collaborator collaborative list would read "1 collaborators" if the
  `Count >= 2` branch were ever widened.

**Where the plan is wrong or too thin for this surface.**

* **§2 tree has no home for the shared detail FRAME.** `Album.Page.cs`, `Playlist.Page.cs` and `User.Page.cs` are three
  separate files, but 0.2.9's album/playlist/liked/show pages are ONE shell (`DetailShell` 858 + `DetailRail` 607 +
  `DetailTracks` 4525 + `DetailVerticalHero` 579 + `DetailVerticalLayout` 662 ≈ 7,200 lines). Either add
  `Entities/Detail.UI.cs` (the frame) or every page file duplicates the rail/hero/table. This is the single biggest
  omission for Wave 5 owners M/N/O.
* **§2 has no home for drag & drop** (`WaveeResourceDrag.cs` 596 + `WaveeDragRules.cs` 210 + `WaveeDragChipModel.cs` 52 +
  `PlaylistInsertionPreview.cs` 80 ≈ 940 lines) — payload, chip, kind map, refusal table, deposit seam. It is not a page
  and not the sidebar; propose `Shell.Drag.cs` or a named partial of `Shell.UI.cs`.
* **§4.11's `ActionId` list is missing this surface's verbs**: `MoveToPlaylist`, `RenamePlaylist`, `DeletePlaylist`,
  `InviteCollaborators`, `SetVisibility`, `SetCollaborative`, `TunePlaylist`, `NewPlaylist`, `NewFolder…`. `ActionsFor(kind,
  ctx)` also needs a HOST (the playlist the rows belong to + its capabilities) — 0.2.9's `PlaylistHost(uri, caps, rows)` —
  or "Remove from this playlist" and "Move to playlist" cannot be expressed.
* **§4.12's `RowStyle` is too thin for a playlist row**: it has `ShowNumber`/`ShowAddedAt` only. A playlist row needs the
  heart, the Added-by USER cell (avatar + name), the album lane, the tempo/plays opt-in lanes, the video glyph, the expander
  chevron, the drag source and the per-tier column set — see 01-/04-.
* **§4.13's page shape** demands the model on mount (correct) but shows no readiness branch: this page has four
  (`Loading`/`Empty`/`NoMatch`/`Rows`) plus a notice strip plus a membership-unknown shimmer, and the notice must be a
  COLUMN because its rule is stateful.
* **§4.3's `PlaylistTrackEdge` is right** (`ItemId`, `AddedAt`, `AddedBy`, chart triple, pending Flags) — keep it exactly;
  it is the one place the plan already matches this surface.

**Line budget.**

| | lines |
|---|---|
| 0.2.9, playlist-only files | 2,506 (`Playlist*.cs` 2,048 + `CollaboratorFacePile` 162 + `DetailNoticeBar` 89 + `PlaylistInsertionPreview` 80 + `PlaylistCreateFlow` 127) |
| 0.2.9, playlist branches inside shared files | ≈1,150 (recs ≈350, insertion/drop/reorder/block-move ≈330, tune button ≈210, rail/hero playlist arms ≈180, notice/list-state/mapper ≈80) |
| 0.2.9 total attributable | **≈3,650** (excluding the shared frame and the track table) |
| Plan §2 target (`Playlist.cs` 500 + `Playlist.UI.cs` 700 + `Playlist.Page.cs` 1,500) | 2,700 |
| Honest estimate | **4,400–4,800**: `Playlist.cs` 900 (columns + the seven ported rule sets + the notice column), `Playlist.UI.cs` 1,400 (cover/title/description editors, owner block, pile, invite + access flyout, picker, chips, insertion preview), `Playlist.Page.cs` 2,100–2,500 (page, rail/hero configuration, list configuration, insertion/drop/reorder, recs, tune, menus) |

Files missing from the §2 tree for this surface: the shared detail frame, the drag/drop seam, and a home for the
playlist-deposit submenu/picker (it is used by track rows, cards, the sidebar and tabs — not only by this page).

**Drift between the design docs and the code (code wins).** `playlist-facts-mica.html` proposes a per-playlist facts
rail (The years · Top artists · Your blend · Rediscover · a since-line) with a per-page-kind accent flip
(`--accent` per `data-page="editorial|owned"`); the code ships that panel through `LikedFacts.Has(m, BadgeStyle.OwnerRow)`
(`LikedFactsPanel.cs:28-43`) with **Rediscover Liked-only** (`:216-221`) and the accent taken from the COVER palette, never
from the page kind. `playlist-facts-v2-mica.html` is the "which facts earn a card" ladder (graph vs pill vs absent, the
tempo coverage floor, the top-band cap, the evidence floors); that one DID land, as `LikedFactsRules.FactShape` +
`LikedFactsPanel.Cards` (`LikedFactsPanel.cs:172-233`) — for a playlist the time slot is the week card only when the adds
actually SPREAD (`s.StampsSpread`, `:182`), else the years card, else a pill. See 07-liked-songs.md. `radio-inspiredby-mix-design.md` is still marked "proposed"; what shipped is `RadioLaunch.Start` /
`ContainerActions.GoToArtistRadio` (`ContainerActions.cs:120-126`), and a radio/mix opens as an ORDINARY playlist page
(`pl:` route) whose only radio-specific affordance is the Tune command. `playlist-saz-fable5-handoff.md` is a wire capture
(playlist4 `/changes`, permission, popcount), not a visual design; its visual consequences are exactly two — the item-keyed
MOV (hence the keyed-reorder gate) and the save-count segment in the meta line.

**Unused loc keys (a real drift to fix in 0.3, not to copy):** `detail.recommended` ("Recommended songs"),
`detail.noSuggestions` ("No suggestions right now"), `detail.collabOpen` ("Open to collaboration"), `detail.collabCount`
(`{n, plural, one {# collaborator} other {# collaborators}}`) and `detail.collabView` ("View collaborators") all exist in
`assets/loc/en-US.json` but the code hard-codes the English at `DetailTracks.cs:2968,2981` and
`CollaboratorFacePile.cs:40-42,87`. Port the keys, not the literals — and note `detail.collabCount` is PLURAL-AWARE while
the literal it shadows is not.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`. Routes: `pl:<uri>` for a fake
owned playlist ("Late Night Mix"), a fake followed/editorial one, and `local` for the Local Files list. Unless stated,
compare a static capture at window 1280×900 (page ≈1040).

1. Rail width is 240 and the cover is exactly 216×216 with `Radii.Card` 8 and the card shadow. (static, 1280×900)
2. Rail padding is 16 left / 8 right / 24 top+bottom and the inter-row gap is 14. (static; measure cover→owner gap)
3. Cover saturation reads identical on the editable (owner) and read-only (followed) pages — no vibrancy pop when a page
   loads editable. (two captures, same cover)
4. Hero title is 40/52/600 at window height ≥900 and 28/36/600 below it; it auto-fits down to 18 px within 3 lines.
   (resize 1280×920 → 1280×860)
5. Owner row: 24-DIP avatar, name at `WaveeType.TrackTitle` trimming with an ellipsis, invite affordance as the ROUND
   28-DIP arm on the rail. (static, long owner name)
6. A collaborative playlist shows the face pile (28+2 ring, −12 overlap, ≤4 + "+N") with the label
   "{n} collaborators" and the chevron. (static)
7. The follower/editorial page has NO ⋯ button beside Share in the rail. (static, both pages)
8. Meta line: "N songs · M saves · duration" only when the save count > 0; compact above 999 ("18.7M saves"). (static)
9. A playlist whose membership has not landed shows a shimmer BAR in the meta slot and shimmer ROWS — never "0 songs".
   (`--fake` may not reproduce; force by opening a playlist from the sidebar before its membership loads, live build)
10. Empty followed playlist → "Nothing here yet", centred, 14/Tertiary. (static)
11. Empty owned playlist → the "Recommended songs" header at the top of the list with a refresh button, and rec rows once
    fetched. (static)
12. A filter that matches nothing → "No songs match your filter" (same box, different string). (type "zzz" in Find)
13. Hovering the title fades in the pill (fill + hairline border) AND the pencil; the cursor is an I-beam over the whole
    pill. (hover capture)
14. Clicking the title swaps to the field WITHOUT the rail jumping: the swap box tweens its height over 300 ms.
    (frame recording, 60 fps)
15. The Save/Cancel row sits BELOW the field, right-aligned, 32 tall with 16-radius pills; Save is accent-tinted.
    (static, edit mode)
16. Committing shows "⟳ Saving…" then "✓ Saved" in a trailing row that does NOT change the title's wrap. (frame recording)
17. The "Saved" chip disappears ≈1.8 s after the save lands. (recording)
18. Hovering the cover (owner) cross-fades a 52 %-black scrim with a 32-DIP camera glyph and "Change cover" over 250 ms.
    (hover capture)
19. Dragging a .jpg over the cover deepens the scrim to 68 % and changes the label to "Drop image to change cover".
    (drag capture)
20. Dragging a non-JPEG file and dropping raises a WARNING toast, not an error. (live)
21. The CTA cluster wraps as a UNIT at mode 1/2: Play on one line, the three FABs together below — never one orphan FAB.
    (resize to page ≈700 and ≈580)
22. The rail collapses to a 96-DIP strip (cover 80, 2-line title, chevron at the foot) when the grip is pushed past the
    detent, and re-expands from the cover, the chevron, or a pull past 220. (drag the grip)
23. Mode 3 (page <540): the rail is gone and the vertical hero is item 0 of the list; at page 400 the cover is 280 and
    stacked, at page 520 it is 197 and beside the identity column. (two captures)
24. In row flow, the action row sits on the cover's BOTTOM edge (no dead band under it) for a bare title + actions
    playlist. (static, page 520, a playlist with no description)
25. Scrolling the vertical arm reveals the 56-DIP context band with "Title" over "Owner · N songs · duration" and the
    Find / Filter / Play text actions; the band paints NO material. (scroll recording)
26. A daylist playlist shows the compact flip strip in the rail (cells 10×20) and the hero strip (13×28) in mode 3, with
    "Next update at {time}"; digits flip (old up/out, new up/in) once a second. (frame recording, 2 s)
27. A chart playlist shows "N new entries · MMM d" in `Tok.TextSecondary`, static. (static)
28. A mix/radio playlist shows the ✨ Tune command in the command bar; its flyout is 336 wide with the accent tile,
    title + subtitle, a radio group and "Reset tuning"; the active state tints the button. (static + open capture)
29. The first-run teaching tip appears anchored to Tune and is dismissed by using the command. (fresh settings, live)
30. The ⋯ owner menu lists exactly "Invite collaborators", a separator, "Delete playlist" — and collapses to the single
    "Delete playlist" row (no separator) when the owner cannot administer permissions. (open capture)
31. Delete asks for confirmation with the exact sentence and navigates home on confirm. (live, a throwaway playlist)
32. The Invite & access flyout is 300 wide with the accent "Copy invite link" CTA, a divider, and the two toggle rows whose
    captions state the CURRENT setting. (open capture, public and private)
33. The picker flyout is 320 wide: search field 300×32, a "New playlist" row with a 40-DIP "+" tile, then 44-DIP rows with
    40-DIP art; a collaborator playlist carries the "Collaborator" subline. (open capture)
34. The picker order is MRU-first: file a track into playlist X, reopen the picker, X is first. (live, two opens)
35. "Add to playlist ▸" lists at most 10 playlists then "More playlists…", in the SAME order the picker shows.
    (open capture of both)
36. "New playlist" from any deposit surface creates "My Playlist #N" (the first unused N), and the toast's action is
    **Open**; an ordinary add's toast action is **Undo**. (live)
37. Creating a playlist from the sidebar navigates to it with the title editor ALREADY open, the caret at the END of
    "My Playlist #N" and **nothing selected** (`visual: false` + `PlaceCaretAtEndOnFocus`). Creating one from the PICKER
    or a deposit submenu ("New playlist") does NOT open the editor and does not navigate. (live, recording, both paths)
38. Dragging rows within the list shows a gap with ≤3 preview cards (accent 1-px border, card shadow, "+N" pill on the last
    one) and NO full-window scrim; the chip says "Move N songs". (drag recording)
39. Dragging from another list into this one KEEPS the scrim and says "Add N songs"; a container with unresolved tracks says
    "Add to {name}". (drag recording)
40. With a non-default sort, a same-list drag is refused with "Clear sorting to reorder"; with a query, "Clear filters to
    reorder". (drag capture, each state)
41. Alt+↓ moves the selected block down one row and the selection follows the block; a gapped selection does nothing.
    (live, recording)
42. Dropping anywhere on the hero/rail column APPENDS with the caption "Add to {name}"; the same gesture with a SAME-LIST
    payload passes through it to the list. (drag capture)
43. A live add/remove (edit from another device, or the picker) FLIP-glides the rows and fades the new row in with a
    20 ms/row stagger — it does not re-shuffle or scroll-jump. (recording; drive with a second client or the fake seed)
44. While a same-list drag is live, a background refresh does NOT re-key the list; the refreshed model lands after the
    chip animates home. (recording)
45. Deleting the playlist on another device shows the Informational strip "This playlist was deleted." with a
    "Go to Library" action, the rows STAY on screen, and every edit affordance (pencil, camera, invite, ⋯, drag, drop)
    disappears in the same frame. (recording, live)
46. A create that the server rejects shows TWO different sentences on purpose: the Error toast says **"Couldn't create the
    playlist."** with a **Retry** action (`detail.edit.createFailed`, `PlaylistCreateFlow.cs:91-96`) and the page's notice
    strip says **"This playlist couldn't be created."** (`detail.notice.createFailed`) — the toast is the moment, the strip
    is what is still there four seconds later. The failure is also announced assertively. (live, offline)
47. An offline edit toasts "You're offline — this change will sync when you're back." as INFORMATIONAL, and the header
    grows a "{n} changes pending" chip with the refresh glyph. (live, airplane mode)
48. Copying the share link flips the ⇗ glyph to ✓ for 1.6 s and announces "Copied". (recording)
49. The page's Play pill, the accent rule, the daylist digits and the saved heart all take the SAME cover-derived accent,
    and the page ground is the flat tone plane (no banded wash). (static, a strongly coloured cover)
50. With `Settings › Appearance › color washes` off, the tone plane and the shell tint disappear but the accent stays.
    (static, toggle)
51. Mode 3 has NO "Recommended songs" block at all: an empty owned playlist at page 400 says "Nothing here yet", the same
    as a followed one. (resize an empty owned playlist across 560 → 400)
52. At mode 3 a SOLO owner's hero shows the owner name as a plain 12/600 secondary run — no avatar, no invite pill — while
    a COLLABORATIVE one shows the face pile and the pill (W29). (two captures, page 400)
53. The rail's three 40-DIP circles do not press to the same depth: ♥ dips to 0.92, ⇗ and ⋯ only to 0.98. (frame
    recording of a press on each; or accept the source, `SaveButton.cs:43` vs `PlaylistInlineEdit.cs:786,1112`)
54. A playlist holding podcast episodes states both kinds in the meta line — "48 songs · 3 episodes · 5 hr 12 min".
    (static, a mixed fake playlist)
55. With `Settings › Appearance › track artwork` hidden, the reorder gap's preview cards lose their art too (not just the
    rows). (drag capture, setting on and off)
56. An untuned mix's Tune flyout has NO "Reset tuning" row and no separator above it; tuning it once makes both appear and
    disables the row that is now current, which shows "Current" in the accelerator column. (two open captures)
57. The rail shows NO eyebrow above the owner row (the "Playlist · Collaborative" line exists only in the mode-3 hero).
    (static, both arms of the same collaborative playlist)
58. With `--fake` (or signed out) an OWNED playlist still shows the pencil, the camera and the drag affordances but NO
    invite pill and NO ⋯ menu — those two also require `SpotifyEditsLive`. (static, fake vs live)
59. The collapsed rail's chevron points RIGHT (expand), and the strip's cover floors at 48 if the strip is ever narrower
    than 64. (static, collapsed)
60. Committing a description edit while a save chip is up does not move the description's wrap: the chip lives in its own
    trailing row, which is 0-height at idle. (frame recording)
61. The ADDED BY column is ABSENT on a solo-owner playlist and present on a collaborative one with ≥2 distinct
    contributors, at the SAME window width — it is data-gated, not tier-gated. (two static captures, page ≈1040)
62. A playlist whose rows carry no `AddedAt` shows no DATE column. (static, a fake playlist with no stamps)
63. At window ≥1700 the [rail | right] pair stops growing and CENTRES: the row caps at 1600 DIP and equal gutters open on
    both sides. (resize 1600 → 2000)
64. Mode 2 holds all the way down to page 540, not 560: a page at 550 is still two-column (rail 188), and 539 flips to
    the vertical hero. (slow resize across 560 → 535, watch for one flip, not two)
65. A blank-titled playlist's hero reads "Playlist name" in `TextTertiary`, and clicking it opens the editor with the
    field's own placeholder. (live, a playlist renamed to whitespace)
66. Hovering the rail's ♥ and ⋯ shows the ARROW cursor while ⇗ Share beside them shows the hand, and a screen reader
    announces the ⋯ with no name — the 0.2.9 a11y gap §9 records (focus itself is fine: all three are tab stops).
    Confirm all three read identically in 0.3. (hover capture + Narrator walk)
67. The picker opened from "Move to playlist ▸ → More playlists…" does NOT list the playlist the rows came from; the one
    opened from "Add to playlist ▸" does. (two open captures from the same row)
68. A cover-less playlist's picker row shows a 2×2 MOSAIC of member album covers when it has ≥4, the single first cover
    when it has 1–3, and the seeded generated art when it has none. (open capture, three playlists)
69. A drop refused because the destination list is still loading says "Still loading…", not "Can't edit this playlist".
    (drag onto a playlist tab/page opened a frame earlier, live)
70. A reorder rejected by the server says "Couldn't reorder — the playlist changed elsewhere." (Error), while an
    Alt+↓ into the slot the block already occupies says "Already there" as INFORMATIONAL. (live, two states)
71. The collaborator pile's button does NOT scale on press (brush only), while every other control beside it does.
    (frame recording of a press)
72. "Keep left-rail same size" (Settings) makes a width dragged on an ALBUM page apply to this playlist's rail on the
    next open, and turning it off restores this playlist's own remembered width. (live, toggle + two opens)

---

## 11. Audit log

Adversarial re-read of the 0.2.9 sources against every number, state and citation in §§0–10 (2026-09-12). One line per
correction; "verified" items are not listed — only what changed.

**Wrong (fixed in place).**

1. §0.2 — "ONE pair of predicates … nine call sites". It is a TRIO (`Editable` / `EditableMetadata` / `Live`,
   `PlaylistInlineEdit.cs:77,80,84`) across **17** call sites in 8 files, and the two owner affordances carry a second
   gate the chapter never named: `SpotifyEditsLive(svc)` (`:65-66`). Rewritten with the full call-site census.
2. §2 W28 / §3 — "♥ / ⇗ / ⋯ (40): ScaleEmphatic 1.07 / 0.92". Only ♥ is emphatic (`SaveButton.cs:43`); ⇗ and ⋯ are
   `ScaleSubtle` 1.02 / 0.98 (`PlaylistInlineEdit.cs:786,1112`). Row split in the token table; W28 corrected.
3. §5 row 1 — the facts panel listed among the `FadeUp` late rows. It is a plain `Row` (FLIP only) because the panel owns
   its own entrance (`DetailRail.cs:259-260`), and in the hero arm it is not in the hero at all. Corrected + a negative row.
4. §4 / §3 — "Description links = page accent". True only of the READ-ONLY arm (`DetailRail.cs:252`); the editable arm
   hard-codes `Tok.AccentTextPrimary` (`PlaylistInlineEdit.cs:682`). Logged as a real 0.2.9 drift to resolve in 0.3.
5. §4 — the "Copy invite link" CTA's accent read as the page/cover accent. It is `Tok.AccentDefault`, the THEME accent
   (`:1015`); so is the whole Tune control. Two explicit rows added.
6. §2 W6 — the row-flow hero drawn with "(👤) Christos [👥 Invite]". The hero's plain attribution arm is a bare 12/600
   `TextSecondary` run with no avatar and no invite (`DetailVerticalHero.cs:474-481`). W6 corrected; W29 added.
7. §2 W7 — the compact strip's chevron drawn as "⌄". It is `Icons.ChevronRight` (the expand direction), 14/TextSecondary.
8. §2 W18 — "lists exactly Invite, separator, Delete". Invite AND its separator are gated on
   `CanAdministratePermissions` (`:1136-1141`); an owner without it gets one row. Parity item 30 amended.
9. §2 W20 — a divider drawn after "New playlist". The submenu's only separator is the one before "More playlists…"
   (`Menus.cs:308-331`).
10. §6.1 — "⋯ (vertical hero) — Copy to playlist / Add to playlist". On a playlist page it is ALWAYS "Copy to playlist"
    (`Heart == Follow` for owned playlists too, `DetailConfig.cs:199`).
11. ~~§6.1 — Play cited `DetailTracks.cs:751`; the `PlayAllOverride` cell is filled at :843. Conversely W23 cited :843
    for the checkbox lane, which is at :751. The two citations were swapped; both corrected.~~
    **RETRACTED by the second pass (item 34): this "correction" was itself the error.** `playAllCell[0]` is filled at
    **:751** and `_checksVisible` is the memo at **:843** — the original citations were right. §6.1 restored.
12. §10 #46 — one sentence claimed for the create failure. The toast says "Couldn't create the playlist." + Retry;
    the notice strip says "This playlist couldn't be created.". Both spelled out.

**Missing (added).**

13. The 140-DIP narrow header and the rail eyebrow are DEAD arms for a playlist (`DetailShell.cs:512,587`;
    `DetailRail.cs:167-171`) — flagged so Wave 5 does not port ~80 lines of unreachable composition.
14. W29 — the hero attribution's two arms, and the consequence that a solo owner has NO invite affordance at mode 3.
15. W30 — the meta line's four grammars, including the MIXED "48 songs · 3 episodes" arm (`DetailPage.cs:509-518`) the
    chapter did not mention at all; §7 gained two data rows for it.
16. W10 — recommendations do not exist in the vertical/hero arm or the embedded pane
    (`recsCapable = … && !_embedded && !_verticalHeader`, `DetailTracks.cs:926`).
17. W21 — two preview-card states: artwork suppressed by `AppearancePrefs.TrackArtworkHidden` (`:1206`), and the
    container-with-no-snapshot card (MusicNote tile + payload name). Plus the "+N" pill's `Radii.PillAll`.
18. W24 — "Reset tuning" (and its separator) exist only while a tuning is selected (`PlaylistTuneMenuModel.cs:34-41`);
    the current choice is a DISABLED radio row whose "Current" is accelerator text; the button's 9-DIP chevron; the
    idle hover/press fills.
19. W16 — the overlay's opacity bind also fires on ANY live Files drag in the app (`:348-349`), and the non-editable arm
    loses the overlay, the drop target, the cursor and the click entirely.
20. W14 — the trailing status row is always mounted (it carries the pending chip); the description editor's row has no
    pending chip and collapses to 0 height; the three bring-into-view margins (40 / 32 / 48); the glyph sizes.
21. W19 — the row list's inner gap of 2, the `Icons.Add` 20 tile glyph, and the absence of a divider.
22. W25 — the flyout's inner scroll geometry (264 × 344) and the `ShowCollaborators` predicate shared with the hero.
23. W26 — the 99-hour clamp, the `enabled: !expired` interval, the chart date format.
24. §3 — new rows: the eyebrow string's three-way rule, the rec-header refresh button, the collaborator flyout, the
    context band's hairline / title cap / action pad.
25. §5 — new rows: the read-arm pill's own Enter fade, the pending chip's Enter fade, the 83 ms satellite/Tune brush,
    the invite CTA's icon/text swaps, and the "no wake when expired" fact.
26. §6.2 — Enter / Space / roving-arrow keyboard on rows (`:3547-3554`).
27. §6.3 — the off-page verbs this chapter silently inherited: `ContainerActions.RenameDialog` (a ContentDialog, not an
    inline editor), the ABSOLUTE visibility rows vs this page's toggle, and `FolderActions` (25-sidebar.md) sharing only
    `PlaylistCreateFlow`.
28. §8 — `ContextBandLayout` + `ContextBandLayoutTests`; the picker's ordering call named as a non-rule so nobody
    re-derives it; the reset-option gate called out inside `PlaylistTuneMenuModel`.
29. §9 — a new "defects to FIX, not reproduce" block (the ⋯ button's missing `Focusable`/`Role`, the link-ink drift, the
    2-line editable hero title, the 12-vs-13 px hero description, the mode-3 invite gap, the hard-coded strings).
30. §10 — ten new parity items (#51–60) covering every state above that a capture can actually catch.

**Unverified (left standing, flagged).**

31. `Radii.Card` = 8, `Radii.Control` = 4, `Radii.Full`, `Elevation.Card`'s two shadows, `MotionRecipes.CardResize` /
    `IconSwap` / `TextSwap` durations and `Expressive.Fast` = 250 are quoted from the engine repo's own files; this audit
    read `Spacing` (XXS 2 / XS 4 / S 8 / M 12 / L 16 / XXL 24), `Splitter.StripW` = 16 and
    `SortableMath.DefaultPreviewCap` = 3 directly and confirms those four.
32. Every "0.3 target" name (`Entities/Playlist.UI.cs`, `PlaylistFields`, `Column<…>`) is a proposal, not a read fact —
    unchanged by this audit.

**Overclaim (softened).**

33. §2 W3's "the labelled invite arm appears only in the vertical hero" was true by accident, not by design: the OTHER
    labelled call site (`BuildHeader`, width 600) simply cannot be reached by a playlist. Now stated as the dead-arm fact
    it is, so 0.3 chooses the adaptive threshold deliberately instead of inheriting it.

---

### Second pass — adversarial re-read (2026-09-12, later)

A second auditor re-read every assigned source independently, including the five the first pass did not open
(`Interaction.cs`, `Radii.cs`/`Spacing.cs`/`Expressive.cs`/`MotionRecipes.cs` in the engine, `DetailRailPolicy.cs`,
`DetailLayoutBreakpoints.cs`, `DetailVerticalLayout.cs`, `assets/loc/en-US.json`). Twelve findings; four of them correct
the FIRST pass.

**Wrong (fixed in place).**

34. §6.1 / audit item 11 — the first pass SWAPPED two correct citations. `playAllCell[0] = () => StartVisible(0)` is
    `DetailTracks.cs:751`; `_checksVisible = UseComputed(...)` is `:843`. W23's :843 was right all along. Item 11 is
    marked retracted and §6.1 restored with the source text quoted.
35. §0.2 — "`Editable` ×7 … `EditableMetadata` ×7 … 17 call sites across **8 files**". A grep census gives
    `Editable` **×6**, `EditableMetadata` **×8**, `Live` ×3 = 17, across **5** files (the chapter's own bullet lists
    already enumerated 6 and 8; only the counts disagreed). `SpotifyEditsLive` has 7 call sites of its own, TWO of them
    off this page (`ContainerActions.cs:209`, `Menus.cs:738`) — added, because it changes where the predicate ports to.
36. §9 — "the hero's editable title clamps at 2 lines while its read-only twin uses `titlePlan.Lines` — an owner's long
    title truncates where a follower's wraps". Backwards: `DetailVerticalLayout.TitleLinesMax = 2` (`:295`) caps the plan
    too, so `Lines ∈ {1,2}` and the editable arm can only wrap MORE. Replaced with the three divergences that are real —
    the 28-DIP pencil reservation on the measure, the hard-coded `MinSize = 18`, and a 1-line plan rendered at 2.
37. §9 + the drift note — "'Nothing here yet' … hard-coded English". It is `Loc.Get(Strings.Detail.Empty.NoTracks)`
    (`detail.empty.noTracks`, `DetailTracks.cs:413`), as is "No songs match your filter". Removed from the hard-coded
    list; the remaining five literals are named with their shadowed keys.
38. §9 — "the rail's ⋯ has no `Focusable` … it is unreachable by keyboard … alone among this page's controls". Wrong on
    BOTH halves. (a) An omitted `Focusable` costs nothing: the engine derives
    `InteractionInfo.Focusable = TabStop ?? (Focusable || OnClick != null)` (`InputDispatcher.cs:3822-3823`), so every
    clickable node here is already a tab stop. (b) It is not alone — a six-row table now records every control that omits
    `Role` (3) or `Cursor` (5), including the ♥. The defect is the missing NAME and the missing hand cursor, not focus.
39. §2 breakpoints — "≥560 → 2" as the live band. `ModeFor` rewrites a nominal Vertical verdict to 2 for any page ≥540
    (`DetailLayoutBreakpoints.cs:82,85`), so the shipped mode-2 band is **540…660**. Parity item 64 added.
40. §10 #37 — "the title editor ALREADY open and the name selected". `FocusOnMount` focuses `visual: false` and the field
    sets `PlaceCaretAtEndOnFocus` — caret at end, nothing selected (`PlaylistInlineEdit.cs:266-275,509`). The stale
    "selected" wording survives only in a code COMMENT. Also: the editor opens only on the navigating create.
41. Header line counts — `FlipCountdown.cs` is 172 (not 173) and `SaveButton.cs` 203 (not 204); `FolderActions.cs` (285)
    and six pure-rule files the chapter cites were missing from the source list entirely. W7's `RailCompactW` citation is
    `DetailShell.cs:203`, not :205.

**Missing (added).**

42. §1.1 — the `DetailConfig.Playlist` literal in full (`:196-201`), the shared `ListColumns` instance, and the
    `RailScope` → `DefaultWidthFor` / `ScopeFor` rule that lets "Keep left-rail same size" drive this rail from an album
    page. Plus the `rail:prerelease` row, flagged unreachable.
43. W1 — the ADDED BY / DATE / video columns are **data-gated** (`HasAddedBy = contributors.Count >= 2`,
    `HasDateAdded`, `HasVideo` — `DetailPage.cs:501-531`), not tier-gated. A solo owner's playlist never shows Added-by
    at any width. Parity 61-62.
44. §2 / §3 — the two-column row's `MaxWidth = 1600` + centring and the right column's `MinWidth` 300 / 0
    (`DetailShell.cs:649,672,519`). Parity 63.
45. W12 — `MembershipLoaded` is true by construction with no real store, for a non-`spotify:playlist:` uri, or once the
    store has membership (`DetailPage.cs:441-444`) — the reason `--fake` and Local Files can never show the shimmer arm.
    Plus the Loading arm's SkelRegion fall-through to Empty in the same flush.
46. W13 — the strip is a `Height = 0, HitTestVisible = false` box at `Notice == None` (reserves nothing), the three
    sentences in full, and the fifth enum value (`MinifiedAlbum`) that ports with the rule but never renders here.
47. W14 — the `detail.edit.namePlaceholder` "Playlist name" placeholder in BOTH arms (the read arm paints it
    `TextTertiary` for a blank title), the caret-at-end focus contract, and `EditChrome`'s 8-DIP field→row gap. Parity 65.
48. W16 — the overlay swaps its CHILDREN while saving (chip instead of camera + label), and the cover is patched from the
    STORE header, not from the uploaded bytes (`:419-420`).
49. W17 — the invite url shape `{shareUrl}?pt={token}`, the no-clipboard fallback (opens the url, label never flips), the
    post-copy model refresh, and the fact that both toggle writes are optimistic-only with no re-read.
50. W19 — the picker's MOVE arm (`Deposit` + `ExcludeUri`, `PlaylistPicker.cs:43-47`), `EnsurePlaylists()` on mount, the
    `BottomLeft` default placement, and the row art's four-arm fallback incl. the 4-tile MOSAIC. Parity 67-68.
51. W20 — "More playlists…" is disabled without an overlay; its ContentDialog is titled with the submenu's own label, has
    an EMPTY primary button and a "Cancel" close, defaulted to Close (`Menus.cs:516-531`).
52. W22 — the two refusal sentences the sketch omitted ("Still loading…", "Nothing to add"), the table's fixed order, and
    **W22b: the complete write-failure copy table** (kind × verb → sentence + severity) — nine kinds, four of them
    Informational, with the `NoOp`/`Invalid` non-reorder cells flagged as a real copy gap. Parity 69-70.
53. W25 — `Icons.ChevronDownSmall` 8 (not the Tune button's 9-DIP `ChevronDown`), the 4-DIP inner gap, the 8-DIP outer
    row gap, `BottomEdgeAlignedLeft`, and the fact that the pile button carries **no scale tier at all**. Parity 71.
54. §3 — new rows: the page row's 1600 cap, the insertion card's "+N" pill geometry, the picker art ladder, the Tune
    flyout tile's 18-DIP glyph (the button's is 16), and the invite pill's 12-vs-14 glyph split.
55. §5 — the `Interaction.Subtle` brush row (83 ms / `ControlFaster`, and the explicit note that the recipe sets no
    `Role` or `Cursor`), and the fact that the cover scrim's .52→.68 DEPTH is a re-render while only its opacity is bound.
56. §8 — `PlaylistReorderRules.DisplayRowOf` named in the Decides column; `PlaylistEditErrors` added as the engine-side
    raise that must port with the pure half; `DetailNotice`'s five values and `ForAlbum`'s statelessness recorded.
57. §10 — twelve new parity items (#61-72); §7 — three new readiness rows (the data-gated columns, and
    `MembershipLoaded`'s three-way truth that 0.3's single edge `State` does not yet reproduce).

**Verified against the engine (previously flagged unverified).**

58. `Radii.Control = 4`, `Radii.Card = 8`, `Radii.Pill = 16`, `Radii.Full = 999` (clamped to half the box);
    `Expressive.Fast = 250`; `MotionRecipes.CardResize` = 300 `SmoothOut` `SizeMode.Reflow`; `IconSwap` = 250 `EaseInOut`
    scale 0.25 + `BlurSmall`; `TextSwap` = 150 `EaseInOut` dy ±4 blur 2; `InteractionRecipe.BrushMs = 83` /
    `MotionTokenId.ControlFaster`. Item 31's remaining unknowns are closed except `Splitter.StripW`.

**Still unverified.**

59. `Splitter.StripW = 16`, `Elevation.Card`'s two shadow values and `SortableMath.DefaultPreviewCap = 3` are quoted from
    the first pass's reading of the engine repo and were not re-opened here.
60. Every "0.3 target" name (`Entities/Playlist.UI.cs`, `PlaylistFields`, `Column<…>`) remains a proposal.
