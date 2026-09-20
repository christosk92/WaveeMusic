# Library pages (albums / artists / podcasts / local) - 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Library/LibraryPage.cs` (1479), `LibrarySortView.cs` (154),
> `LibraryNavOrder.cs` (104), `LibrarySelectionCommit.cs` (54), `LibraryLayoutBreakpoints.cs` (18) = **1809 lines**;
> plus `src/apps/Wavee/Platform/AppSettings.cs:380-396` (`LibraryStateKeys`), `src/apps/Wavee/Features/Shell/ContentHost.cs:262-264`
> (the only mount site), `src/apps/Wavee.Core/Library/LibrarySearch.cs` (63, the result shapes),
> `src/apps/Wavee/Backend/Library/StoreLibrarySource.cs:439-495` (the search runner).
> 0.3 target: **`Entities/User.cs`** (CORE rules + the matcher), **`Entities/User.UI.cs`** (rows/cards/pill/crumbs/grip),
> **`Entities/User.Page.Library.cs`** (the page) — three of the settled eight-file `User.*` scheme (§9) | Wave 5 owner **O**
>
> **Local-files sources (the `local` route - owned here for the ENTRY POINT, rendered by the shared detail frame):**
> `src/apps/Wavee.Core/Sources/LocalSource.cs` (85, the synthetic "Local Files" playlist + the four deliberately empty
> collection reads), `src/apps/Wavee/App/LocalPlayables.cs` (165, the synthetic `Track` a file becomes + the pure drop
> classifier), `src/apps/Wavee/Actions/LocalFileActions.cs` (115, the two gestures and their three toasts), plus the
> shell's own file-drop target and cue (`src/apps/Wavee/Features/Shell/WaveeShell.cs:1351-1359, :1385, :1418-1431`).
> 0.3 target: **`Entities/Playlist.Page.cs`** (its `wavee:local:all` arm) | owner **O** - see §1.2 and §11.
>
> After Wave 0 every `src/apps/Wavee/...` path below lives at `src/apps/_old/Wavee/...` with the same relative path.
>
> **Route facts.** `albums` / `artists` / `podcasts` are this chapter (`ContentHost.cs:262-264` → `new LibraryPage(r.Name, _settings)`).
> **`local` is NOT a library page**: `ContentHost.cs:178` routes it to `DetailHost` and `DetailPage.cs:78` resolves it to
> `(DetailKind.Playlist, "wavee:local:all")` — the shared detail frame. §11 below records everything the library owner
> needs about it; the frame itself is `03-detail-frame.md` / `06-playlist.md`.
>
> **Ownership decision (stated once, here, because `local` sits in `ShellRoutes.s_exact` at `ShellRoutes.cs:35` beside
> `albums`/`artists`/`podcasts`, and in `ContentHost.IsDetail` at `:178` beside `album:`/`pl:`/`liked`).** `local` is a
> **`DetailKind.Playlist` arm of the shared detail frame** — not a fourth library page, and not a `Local.*` page class
> of its own: `03-detail-frame.md` owns the frame, `06-playlist.md` owns the playlist body, `Entities/Playlist.Page.cs`
> (owner **O**) is the 0.3 file, and **this** chapter owns the library-side entry point — the route's existence, its
> Folder glyph, its "library" history facet, its pinnability, and the rule that its tracks never leak into the
> albums/artists navigators (`LocalSource.cs:57-67`). The two app files that make a file playable at all —
> `App/LocalPlayables.cs` and `Actions/LocalFileActions.cs` — are carried over unchanged and belong to this chapter's
> source list (§1.2, §9, §11); they are engine-free app code, not entity UI, so they get no `Entities/*` file.
>
> **Doc drift (one line, code wins).** `library-v3-destinations-{,v2-,v3-}mica.html` are about the *sidebar's* Library V3
> destination strip (how to REACH these pages) — they belong to `25-sidebar.md`, not here; `library-v3-chrome-implementation.md`
> is likewise the sidebar's chrome (search morph, chip rail, nav band) and its only claim on this chapter is the shared
> `LibraryNavOrder` "Recents = played" rule, which the sidebar was scoped OUT of in 0.2.9 (its `SidebarSort.Recents` still
> sorts by navigation visits). `library-artist-jump-and-recents-implementation.md` **matches the shipped code** — its §E.1
> component tree is still accurate, and its §F.0 sort semantics are exactly `LibraryNavOrder.Order`.

---

## 0. The non-negotiables

1. **This page is a MASTER-DETAIL BROWSER, not a list that navigates.** Clicking an album/artist/show in the left
   navigator re-skins the right pane(s) in place; it never pushes a route. Even a full-text *search hit* commits into
   the browse selection instead of navigating (`LibraryPage.cs:606-627`, `LibrarySelectionCommit.cs`). If the rebuilt
   page navigates on click, the surface is gone.
2. **Three column rungs, separated by a stroke and not by a fill.** Navigator = `Tok.FillLayerDefault`, reading panes =
   `Tok.FillCardDefault`, and a **1-DIP `Tok.StrokeCardDefault` hairline centred inside every 16-DIP `Splitter` strip**
   does the separating (`LibraryPage.cs:352-365, :984-998`). In light the three surfaces measure ≈253.7 / ≈254.2 over a
   ≈252.3 ground — a 2/255 ladder. Drop the seam and the whole browser reads as one white sheet.
3. **The page title lives INSIDE the left column's toolbar**, `WaveeType.PageHero` (28/36/600), one line,
   `CharacterEllipsis` — never a full-width band above three panes owned by three different things
   (`LibraryPage.cs:445-467`). **2026-09-18 (§12):** the title row also carries the row count — 14/400
   `TextTertiary`, bound to the shape's `Count` — beside the hero (`Albums 42`, `Artists 38`); the 0.2.9 anatomy
   never had one.
4. **Column widths, sort, direction, view type, grid size and the selected item are persisted per kind and seeded in the
   constructor**, so frame one already paints the saved layout — no default→saved flash (`LibraryPage.cs:93-117`,
   `AppSettings.cs:380-396`). Filter text is deliberately NOT persisted.
5. **Four view types × three sizes — the codes stay.** CompactList(0) / List(1) / CompactGrid(2) / Grid(3) with S/M/L
   cell sizing are still the persisted view/size codes. **Superseded 2026-09-18 (§12):** the one pill + flyout
   control (`LibrarySortView.cs`) that used to drive them is gone — the toolbar now exposes a plain list/grid icon
   pair (`User.ViewToggle`) and a "…" that opens a trimmed `ViewPanel` (compact variants + S/M/L only; sort moved to
   the word rail, rule 20 below). The artists view's **second, independent** copy of the old control — the one that
   sat over the discography column — is deleted with the column itself (rule 19).
6. **Selection never remounts the list.** The `ItemsView` remount key is `view:size:OrderKey:FactsKey` — pure functions
   of the rows (`LibraryPage.cs:499`, `LibraryNavOrder.cs:78-96`). Selection is not an input. A same-set republish must
   not remount, and every remount that *does* happen lands back at the saved scroll offset via
   `ScrollOptions.ScrollKey = "lib:nav:" + kind` (`LibraryPage.cs:99`).
7. **"Recents" means recently PLAYED**, not recently visited and not saved-set order: played block newest-first, then the
   never-played block in source order, with `desc` flipping *inside* each block only (`LibraryNavOrder.cs:40-47`).
8. **The library is the app's one accent-NEUTRAL surface.** No cover-palette extraction anywhere on it: the detail pane's
   Play CTA is `WaveeCta.Play(Tok.AccentDefault, …)` — the system accent, stated as a deliberate exception
   (`LibraryPage.cs:1180-1183`). **Stays, 2026-09-18 (§12):** the library-rework prototype's art-wash cover treatment
   was evaluated against this rule and recorded as **NOT built** — a toggle off, not shipped.
9. **Full-text search replaces the browse list in place, as a drill-down across the SAME columns** (matched artists ▸ their
   matched albums ▸ that album's matched tracks), with stale-while-revalidate so rows never flash to "nothing matches"
   (`LibraryPage.cs:194-196, :719-745`), one shared skeleton group so the three columns settle together
   (`LibraryPage.cs:52-53, :208`), and an honesty rule: an unattributable match reason draws no "why" caption
   (`LibraryPage.cs:811-823`, `LibrarySearch.cs:25-27`).
10. **Match highlighting is an accent PILL inside the row title**, not a colour swap: `Tok.AccentSelectedTextBackground`
    plate, `Tok.TextOnAccentSelectedText` ink, radius 4, pad 3/1 (`SearchHighlight.cs:42-54`).
11. **Under 640 DIP of content width the row collapses to a single-column breadcrumb drill-in** with 24 DIP of hysteresis
    (`LibraryLayoutBreakpoints.cs:8-17`) — and the collapsed layout drops the page title, because the breadcrumb root
    already names the kind (`LibraryPage.cs:452-455`).
12. **An empty navigator with no filter text is still LOADING** — it shows a secondary "…" caption, never a big-type
    "nothing here" (`LibraryPage.cs:489-492`). Only a filter that matched nothing earns `EmptyState.Compact`.
13. **The right pane's two ways OUT are the hero title and one Subtle/Small "View full album" button** — never a second
    accent CTA (`LibraryPage.cs:1054-1067, :1146-1170, :1186-1187`).
14. **Rows are drag SOURCES with WinUI's ×2 click-primary threshold** (`Drag.ClickPrimaryThresholdMultiplier = 2f`) and
    the page is a drop target for nothing (`LibraryPage.cs:926-934, :1460-1464`).
15. **The discography's "Go to artist" is a HyperlinkButton, not a pill** — `Tok.AccentTextPrimary` ink on the
    rest/hover/pressed ramp, `Radii.Control` (4) so it can never be mistaken for a CTA, with a trailing ↗
    (`LibraryPage.cs:1307-1336`).
16. **The navigator ALWAYS ends up with a selection once rows exist.** `SyncNav` adopts row 0 whenever the persisted
    key is empty *or* no longer present in the shown set (`LibraryPage.cs:558-564`), and `SyncDisco` does the same for
    the discography whenever `_albumKey` is empty (`:1404-1412`). The right pane is therefore populated the instant the
    list lands, and the W9 "Select an album…" placeholders are a **transient** state, not the resting one — reachable
    only while the navigator itself is empty. A rebuild that shows a permanent "pick something" pane is not this page.
17. **A title filter that matches nothing also EMPTIES the right pane.** `SyncNav` clears `_selectedKey` (and, in
    artists, `_albumKey`) and deselects, so "Nothing matches your filter" in the navigator is always paired with the
    W9 placeholder beside it (`:546-556`). Only a filter **with text** does this; a still-empty (loading) set keeps the
    persisted selection, which is what makes the launch restore work.
18. **In search mode the sort pill, the view bank and the size bar are live but INERT.** `Toolbar(title: true)` renders
    unchanged over the search columns (`:442`), yet nothing downstream of `_sort`/`_desc`/`_view`/`_size` reaches a
    search row: `Shape` (`:407-418`) feeds only `ListBody`, and the hit rows are fixed-height `SelectableRow` /
    `TrackHitRow`. This is shipped behaviour and the parity target; it is also the single most defensible thing on this
    surface to *improve* in 0.3 — but only with the owner's sign-off, never silently.

**Library rework (the Collection browser), 2026-09-18 — rules 19-23 are new; see §12 for the full audit entry.**

19. **The artists view is TWO panes, not three.** Navigator (280) │ `Artist.Reader` (`Entities/Artist.Reader.cs`,
    owner N) — the old third-of-three `LibraryArtistPane` discography column is gone, folded into the reader's own
    single scroll. `LibraryArtistPane`, `DiscoRow`/`DiscoCard` and the `Pane{Key="lib:tracks"}` / `"lib:tracks:empty"`
    sibling pane are **deleted, not hidden** (no legacy paths). W2 is superseded by W28/W29 below.
20. **Sort is a word rail, not a pill + flyout.** `User.WordRail(kind, sort, desc)` — Zune's text pivot: the active
    word is 100% ink + 600 weight with a 2-DIP accent underline, the rest 50% ink (85% on hover); tapping the active
    word flips `desc`. The codes are `LibraryNavSort` and **never renumber, only append**:
    `Recents 0 · RecentlyAdded 1 · Alphabetical 2 · Creator 3 · ReleaseDate 4 · Albums 5` — **5 (`Albums`) is new,
    appended 2026-09-18**, offered on the artists rail only (by saved-album count desc, then title).
    `LibrarySortView` / `LibrarySortPanel` and the discography's second copy of them (rule 5) are deleted.
21. **Alphabetical sort adds letter groups and an A–Z jump strip.** `LibraryLetters` (CORE, `Entities/User.cs`)
    interleaves a 28-DIP header flat-item ("#", A–Z) ahead of each letter's first row, present only while
    `Sort == Alphabetical`; the jump strip (`User.JumpStrip`) sits at the column's edge whenever `Sort ==
    Alphabetical` in **either** list or grid view, and jumps unanimated (a jump is a jump) while a sticky letter
    overlay tracks scroll position. New 2026-09-18.
22. **The album pane (`Album.Pane`) has FOUR readiness states.** `AlbumPaneReadiness.Of`: `Header` (identity not
    yet known — short-lived, the navigator already asked) · `Rows` (identity known, the tracks edge not yet
    Complete-and-fully-named — a counted shimmer, never zero rows) · `Failed` (the tracks edge failed, or the row
    batch failed) · `Ready`. Rule 13's "View full album" link stays; **the `MinifiedAlbum` notice is gone from this
    surface** — an unnamed row after a Complete edge is either still loading (shimmer) or failed (a real strip with
    Retry). This is W24's failure arm, closed for real for the first time. New 2026-09-18.
23. **A new persisted key, `library.<kind>.scope`.** `Platform.Keys.LibraryScope(kind)` → `"library." + kind +
    ".scope"`, int, default 0 — the reader's *in your library* (0) vs *all releases* (1) toggle, added to DATA GAP
    8's twelve keys. Three keys are now **orphaned but still persisted**: `library.<kind>.album.{desc,view,size}`
    (the old discography column's own sort/view/size) are read by nobody now that the column is deleted (rule 19) —
    left in `Platform.Settings` unmigrated, so an old value simply goes unread rather than erroring. New 2026-09-18.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition (wide layout)

```
LibraryPage(kind, settings)                                         Features/Library/LibraryPage.cs:23
└─ BoxEl  Dir=1 Grow=1 AlignItems=Stretch  OnBoundsChanged→_collapsed      :239-249   width probe + breakpoint write
   └─ inner  (collapsed ? CollapsedLayout : BoxEl Dir=0 Grow=1 Stretch)    :221-235
      │
      ├── LeftColumn = NavPanel{Width=_leftW, Shrink=0}                    :437-443   Fill=Tok.FillLayerDefault (:364)
      │   ├─ Toolbar(title:true)  Dir=1 Gap=8 Pad=(12,16,12,8)             :456-467
      │   │   ├─ WaveeType.PageHero(ShellNav.Dest(kind).Title)             :462       28/36/600, 1 line, ellipsis
      │   │   ├─ ToolbarPicker  Dir=0 AlignCenter                          :469-473
      │   │   │   ├─ LibrarySortView(_sort,_desc,_view,_size,hasCreator,hasRelease)   LibrarySortView.cs:18
      │   │   │   └─ BoxEl{Grow=1}  (right-shim)                           :472
      │   │   └─ ToolbarFilter = AutoSuggestBox(NoSuggest, "Filter", _filter, Icons.Search, grow 1, minH 32, r4)  :475-477
      │   └─ body = fullSearch ? LeftSearchBody : ListBody                 :442
      │       ├─ ListBody(nav)                                             :483-518
      │       │   ├─ [shown.Length==0 && filter] EmptyState.Compact("Nothing matches your filter")      :491
      │       │   ├─ [shown.Length==0 && !filter] BoxEl Pad=(12,20,12,20) → Caption("…").Secondary()    :492
      │       │   └─ BoxEl{Key="nav:"+view+":"+size+":"+OrderKey+":"+FactsKey, Grow=1, Dir=1,
      │       │       Pad = grid ? (8,8,8,0) : (0,0,0,0)}                  :499, :513-517
      │       │      └─ ItemsView.Create(count, template, layout, ListOptions)                          :516
      │       │          ├─ layout: grid ? GridFit((compact?88:116)+size*(compact?16:24), gap 8)
      │       │          │           : Stack(compact ? 40 : 60)            :509-510
      │       │          ├─ options: Single · Selection=_navSel · Controller=_navCtl · Grow=1
      │       │          │           Selector = grid ? Border : AccentPill · OnChange=OnNavSel
      │       │          │           Scroll = _navScroll (ScrollKey "lib:nav:"+kind)                    :500-507
      │       │          ├─ NavRowContent(it, compact)   Dir=0 Gap=12 Pad=(8,0,8,0) Draggable          :901-914
      │       │          │   ├─ [!compact] BoxEl 40×40 r(circ?20:5) Clip → Surfaces.Artwork(…)         :905-906
      │       │          │   └─ BoxEl Dir=1 Grow=1 Basis=0 Gap=1
      │       │          │       ├─ TextEl Title  14/20/600 TextPrimary  1 line ellipsis               :910
      │       │          │       └─ [!compact] TextEl Subtitle 12/16 TextSecondary 1 line ellipsis     :911
      │       │          └─ NavCardContent(it, compact) Dir=1 Gap=8 Pad=circ?16:8 Clip Draggable       :918-924
      │       │              ├─ Surfaces.ArtworkFill(cover, circ ? Radii.Full : 6)                     :921
      │       │              └─ [!compact] TextEl Title 12/16/600 TextPrimary, AlignSelf circ?Center:Start  :922
      │       └─ LeftSearchBody(sr, skel, sArtist, sAlbum)                 :656-662
      │           └─ SearchSkel(skel, SkelArtistRow|SkelAlbumRow, content)  → SkelRegionEl             :730-745
      │               ├─ Pending  → ShimmerStack(6 × rowTemplate)  Dir=1 Gap=2 Pad=(8,4,8,4)          :749-759
      │               ├─ Failed   → ErrorState.Build(loadable.Error)       :741
               │            ! reachable ONLY from a search column; the two BROWSE loadables have no Failed
               │              arm at all and skeleton forever instead — see W24.
      │               └─ Content  → 0 hits ? SearchMessage("Nothing matches your filter")             :658/:661
                   ! that 0-hit message exists ONLY here, in LeftSearchBody. Every OTHER search column
                     (s:albums :675-676 · s:tracks :681-682 · s:detail :700-701 · the three collapsed search
                     bodies :304/:318/:334) hands SearchSkel a bare SearchScroll, so an empty facet paints its
                     FacetHeader with count 0 and then nothing at all. See W23.
      │                                    : SearchScroll(items, ArtistRow|AlbumRow)                   :761-771
      │
      ├── Grip(_leftW, min 240, max 560, commit→LibraryStateKeys.LeftW)     :233, :987-998
      │   └─ BoxEl{Width=Splitter.StripW(16), Shrink=0, ZStack}
      │      ├─ BoxEl{Width=1, Stretch, JustifySelf=Center, HitTestVisible=false,
      │      │        Fill=Prop.Of(()=>Tok.StrokeCardDefault)}  ← THE SEAM                             :992-993
      │      └─ Splitter.Create(w, onCommit, {Min,Max})  (2-DIP hover thumb, inset 4)                  :996
      │
      └── right  (four shapes; all children of the same flex row)          :226-229
          ├─ ALBUMS/PODCASTS, browse:  DetailColumn(detail, svc, bridge, hasSel)                       :943-948
          │   ├─ [!hasSel] Placeholder → Pane{Key="lib:empty"} → EmptyState.Compact(SelectAlbum|SelectShow)  :973-977
          │   └─ Pane{Key="lib:detail", Grow=1, Basis=0} → LibraryDetailPane(detail, isShow, svc, bridge)
          ├─ ARTISTS, browse:          ArtistColumns(artist, albumTracks, …, railOpen)                 :950-971
          │   ├─ [!hasSel] Placeholder → EmptyState.Compact("Select an artist to see their discography")
          │   ├─ artistPane  Pane{Basis=_midW, MinW = railOpen?220:300, MaxW=_midW, Shrink=1, Grow=0}  :953-960
          │   │   └─ LibraryArtistPane(artist,_albumKey,_aSort,_aDesc,_aView,_aSize,_aFilter,onDrill)
          │   ├─ Grip(_midW, min 300, max 620, commit→MidW)                :969
          │   └─ tracksPane Pane{Grow=1, Basis=0, MinW=220, Shrink=1}      :961-965
          │       ├─ [_albumKey set]  Key="lib:tracks"  → LibraryDetailPane(albumTracks, false, …)
          │       └─ [else]           Key="lib:tracks:empty" → EmptyState.Compact("Select a release")
          ├─ ARTISTS, search:          SearchArtistColumns(sr, skel, sArtist, sAlbum, railOpen)        :665-689
          │   ├─ albumPane Pane{Key="s:albums", same flex as artistPane}
          │   │   ├─ FacetHeader("Albums", shimmer ? -1 : albums.Count, refining)                      :675, :775-790
          │   │   └─ SearchSkel(… AlbumRow)
          │   ├─ Grip(_midW, 300, 620)                                     :687
          │   └─ trackPane Pane{Key="s:tracks", Grow=1, Basis=0, MinW=220}
          │       ├─ FacetHeader("Songs", …)
          │       └─ SearchSkel(… TrackHitRow)
          └─ ALBUMS, search:           SearchAlbumDetail(sr, skel, sAlbum)  Pane{Key="s:detail"}       :692-703
              ├─ FacetHeader("Songs", …)
              └─ SearchSkel(… TrackHitRow)

LibraryDetailPane : Component                                             LibraryPage.cs:1004-1236
├─ [not Ready | null | Title==""] Skeleton()   Dir=1 Pad=20 Gap=16        :1030, :1225-1235
└─ BoxEl Dir=1 Grow=1 Clip
   ├─ Hero(m, go, navPreview)   Dir=0 Gap=16 AlignCenter Pad=(20,20,20,12)  :1116-1144
   │   ├─ BoxEl 104×104 r8 Clip → Surfaces.Artwork(cover, seed, 104,104, r8, decodePx 256)  :1128-1129
   │   └─ BoxEl Dir=1 Grow=1 Basis=0 Gap=3
   │       ├─ WaveeType.Eyebrow(m.BadgeType ?? (show ? "Podcast" : ""))  Color=TextTertiary  :1133
   │       ├─ TitleLink → 28/36/600 TextPrimary, Hover=AccentTextPrimary, 2 lines wrap      :1152-1170
   │       ├─ m.Publisher ? TextEl 14/20/600 TextSecondary : TrackRow.ArtistLinks(…,14,600) :1136-1139
   │       └─ TextEl m.MetaLine 12/16 TextTertiary 1 line                                   :1140
   ├─ Actions(uri, name, play, shuffle, viewFullAlbum)  Dir=0 Gap=12 Pad=(20,0,20,12)       :1174-1189
   │   ├─ WaveeCta.Play(Tok.AccentDefault, play)    36-tall capsule, Radii.Full, pad 18/6/18/7  :1183
   │   ├─ Fab(Icons.Shuffle, shuffle)  40×40 circle, Icon 16 TextSecondary, Subtle          :1191-1196
   │   ├─ show ? FollowButton(uri,name) : SaveButton(uri, name:name)                        :1185
   │   └─ [album && uri] Button.Create("View full album", …, Subtle, Small)                 :1060-1067
   ├─ DetailNoticeBar.For(_model)   (zero-height when the model has nothing to say)          :1050
   └─ body
       ├─ [show]  ScrollView(CompactEpisodes(eps, onPlay))  Dir=1 Gap=2 Pad=(12,0,12,92)    :1198-1223
       │           rows: MinH 56, Gap 12, Pad 8, r4, 32×32 circle FillSubtleSecondary + Play 12
       │                 title 14/20/600 (2 lines wrap) · duration 12/16 TextTertiary
       └─ [album] TrackList(_trackRoute, _model, bridge, handlers, showToolbar:false, embedded:true)  :1042

LibraryArtistPane : Component                                             LibraryPage.cs:1240-1479
└─ BoxEl Dir=1 Grow=1 Clip
   ├─ Toolbar(a, go)  Dir=1 Gap=8 Pad=(12,12,12,8)   ← ALWAYS rendered, even while loading  :1292-1305
   │   ├─ row: LibrarySortView(_aSort,_aDesc,_aView,_aSize, hasCreator:false, hasRelease:true)
   │   │        · BoxEl{Grow=1} · [a!=null] GoToArtist(a, go)                                :1297-1302
   │   └─ AutoSuggestBox(NoSuggest,"Filter",_aFilter,Search, grow 1, minH 32, r4)            :1303
   └─ body
       ├─ [not Ready] Skeleton()  Dir=1 Grow=1 Pad=12 Gap=8 · 8 × BoxEl{H=148, r8, FillCardDefault}  :1474-1478
       ├─ [0 rows && filter] EmptyState.Compact("Nothing matches your filter")               :1360
       ├─ [0 rows && !filter] BoxEl Pad=(12,20,12,20) → Caption("…").Secondary()             :1361
       ├─ [grid] BoxEl{Key="disco:"+view+":"+size+":"+OrderKey, Grow=1, Basis=0, MinH=0, Clip, Pad=(12,0,12,0)}
       │          └─ ItemsView.Create(n, DiscoCardContent, GridFit((compact?84:100)+size*(compact?16:24), 8),
       │                              {Single, _discoSel, Border, OnChange→Pick, _discoCtl, Grow 1,
       │                               Scroll={ScrollKey="lib:disco:"+artistUri}})            :1371-1379
       └─ [list] BoxEl{Key=…} → ItemsView.Create(n, DiscoRowContent, Stack(compact?44:60),
                                {…, Selector=AccentPill, …})                                  :1381-1389
```

### 1.2 The same tree in 0.3 terms

Target files: **`Entities/User.UI.cs`** (row/card builders + the sort-view control, pure over handles) and
**`Entities/User.Page.Library.cs`** (`User.LibraryPage : Component`). Pure rules go to the CORE section of
**`Entities/User.cs`** (§8). **Settled 2026-09-12:** there is no single `User.Page.cs` — the pages are split by page
(`User.Page.Library.cs` here, `User.Page.Liked.cs` in chapter 07) and the helpers by concern; see §9 for the whole
eight-file scheme.

| 0.2.9 node | 0.3 home | Kind | Inputs (and how data reaches it) |
|---|---|---|---|
| `LibraryPage` | `User.LibraryPage : Component` in `User.Page.Library.cs` | Component | ctor: `EntityKind kind` (Album/Artist/Show) + `IAppSettings?`. **Both freeze at mount** — kind is fixed per keep-alive slot (`ContentHost` keys `"page:"+r.Name`), settings is a stable singleton. |
| the persisted signals `_leftW _midW _sort _desc _view _size _selectedKey _albumKey _a*` | fields on the page component | `Signal<T>` | seeded from `Platform.Settings` in the ctor exactly as today; every child receives the **Signal instance**, never its value. |
| `_navSel` / `_navCtl` / `_navScroll` | unchanged engine types | `SelectionModel`, `ItemsViewController`, `ScrollOptions` | frozen instances; selection survives every remount. |
| `Project(store)` → `NavItem[]` | `User.Me.SavedAlbumSlots` / `.FollowedArtistSlots` / `.SavedShowSlots` (`Edges.SavedAlbums/FollowedArtists/SavedShows`, parent = `User.Me`) | `ReadOnlySpan<int>` | read inside `Render`; re-render is driven by `UseSignal(Entities.Current.Albums.Changed)` **plus** `UseSignal(Edges.SavedAlbums.Version-signal)`. Never copied into a `NavItem[]` — see §9. |
| `Shape(...)` / `LibraryNavOrder.Order` | `User.OrderLibrary(span, sort, desc, recency)` — CORE in `User.cs` | pure | ports verbatim; operates on slots + a facts projection. |
| `ListBody` → `ItemsView.Create` | `ItemsView.CreateBound<Album>(source, scope => Album.NavRow(scope, compact))` | bound rows | the plan's own bound path (`§4`): per-row signals, no per-row remount. The **remount key** stays `view:size:OrderKey:FactsKey`. |
| `NavRowContent` / `NavCardContent` | `Album.NavRow(in BoundItemScope<Album>, bool compact)` / `.NavCard(...)` in `Album.UI.cs`; `Artist.*`, `Show.*` siblings | static, pure over handles | `scope.Text(a => a.TitleId)`, `scope.Image(a => a.ImageId)` — the scope re-binds on slot version bump; no props to freeze. |
| `LibrarySortView` + `LibrarySortPanel` | `User.SortViewPill(...)` + `User.SortPanel(...)` in `User.UI.cs` | Components (they must subscribe) | receive the **four Signal instances** + `hasCreator`/`hasRelease` bools; both are frozen-at-mount props, both are constants per kind. |
| `LibraryDetailPane` | `Album.CompactPane : Component` in `Album.Page.cs` (owner M), `Show.CompactPane` in `Show.Page.cs` | Component | input is the **`Album` handle as a `Signal<Album>`**, not a `Loadable<DetailModel>`: a selection change writes the signal and the pane re-skins. **This is the one shape change that props-freeze forces** — 0.2.9 got away with a frozen `Loadable` because `UseResource` re-drove the same cell. |
| `LibraryArtistPane` | `Artist.DiscographyPane : Component` in `Artist.Page.cs` (owner N) | Component | `Signal<Artist>` + the five `_a*` signals + `Action? onDrill`. |
| `_collapsed` / `_depth` | same two signals | `Signal<bool>` / `Signal<int>` | written from the root `OnBoundsChanged`; value-gated. |
| `CollapsedCrumbBar` | `User.CrumbBar(IReadOnlyList<string>, Action<int>)` in `User.UI.cs` | static | `BreadcrumbBar.Create` unchanged. |
| `SearchLib` / `LibrarySearchResults` | **NEW** `User.SearchLibrary(...)` — CORE matcher over the resident tables (§7 DATA GAPS) | pure + a `Signal<LibraryHits>` | the results carry slots + match spans, not records. |
| `Grip` | `User.ColumnGrip(Signal<float>, min, max, onCommit)` in `User.UI.cs` | static | unchanged geometry; the seam brush stays a `Prop.Of` thunk. |
| **the `local` route** (no 0.2.9 node on this page at all) | the `wavee:local:all` arm of `Playlist.Page.cs` — the shared detail frame, owner **O** | Component | `DetailPage.ParseDetail` must keep `r.Name == "local"` → `(DetailKind.Playlist, "wavee:local:all")` (`DetailPage.cs:78`) and `ContentHost.IsDetail` must keep `local` in its list (`ContentHost.cs:178`); the model is `LocalSource.GetPlaylistAsync` (`LocalSource.cs:24-31`), never a library collection. |
| the **file drop** that lands over these pages | `App/LocalPlayables.cs` + `Actions/LocalFileActions.cs`, carried over verbatim | static, engine-free | this page accepts no drop (§0 #14), so the deepest accepting node is the shell backdrop's own `Files` target (`WaveeShell.cs:1351-1359`, `DropTarget = _fileDrop` at `:1385`). `LocalPlayables.ClassifyDrop` (`:155-163`) decides; `LocalFileActions.Play` (`:60-78`) plays or toasts. See W27. |

**Where `local` lives, in one sentence, so neither chapter drops it.** The library owner builds **no** local page: the
route's page class is `Playlist.Page.cs` (owner O) and its visual contract is `03-detail-frame.md` + `06-playlist.md`
(§11 carries every fact those chapters need). What the library owner *does* own is that the route keeps existing with
its nav identity (`ShellNav.cs:59` — loc `nav.localFiles`, `Icons.Folder`), keeps its "library" history facet
(`HistoryPage.cs:33`), keeps its sidebar row and pinnability (`SidebarPinId.cs:48`, `SidebarBuiltInDocuments.cs:96`),
and keeps out of the navigators (`LocalSource.cs:57-67`). The plan's §2 names no file for it and Wave 5's owner O has
no local arm — that is the gap this chapter asks §2/§5 to close, with `Playlist.Page.cs` as the file and O as the
owner (§9 item 4, §11).

**Props freeze at mount — the four places it bites on this surface:**

1. `Embed.Comp(() => new LibrarySortView(_sort, _desc, _view, _size, HasCreator, HasRelease))` (`LibraryPage.cs:472`) is
   safe only because every argument is either a Signal instance or a per-kind constant. Keep it that way.
2. `Embed.Comp(() => new LibraryDetailPane(detail, _kind=="podcasts", svc, bridge))` (`:947`) passes a **stable
   `Loadable` cell** that `UseResource` re-drives by key (`:188-190`). In 0.3 the equivalent is a `Signal<Album>` the
   page writes; passing a bare `Album` handle would freeze the selection at mount.
3. `LibraryDetailPane.TrackHandlers` reads `_model.Value.Peek()` **at call time** inside every closure
   (`:1072-1110`) precisely so a later selection is honoured through frozen delegates. Port the discipline, not just the
   delegates.
4. `Key` remounts are used deliberately in four places and must all survive: `"nav:"+view+":"+size+":"+OrderKey+":"+FactsKey`
   (`:499`), `"disco:"+view+":"+size+":"+OrderKey` (`:1365`), `Pane{Key="lib:tracks"}` vs `"lib:tracks:empty"`
   (`:961-965`), and the search columns' `"s:albums"/"s:tracks"/"s:detail"` (`:674,:680,:699`).

---

## 2. Wireframes

Scale: **1 monospace char ≈ 8 DIP horizontally**, 1 line ≈ 1 row of the named height. "Content width" = the shell's page
slot (window minus sidebar minus right rail), which is what `OnBoundsChanged` measures (`LibraryPage.cs:242-247`).

### W1 - Albums, wide, fully loaded @ content 1140 (window 1440, sidebar 280, rail closed)

`leftW` = 340 (default, `AppSettings.cs:382`) · grip 16 · right pane = 784.

```
├───────────────────── 340 ─────────────────────┤│├──────────────────────────── 784 ─────────────────────────────────────────────────────────────────┤
┌───────────────────────────────────────────────┐│┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Tok.FillLayerDefault                          │││ Tok.FillCardDefault                                                                               │
│  ┌ pad 12 ─────────────────────────────────┐  │││                                                                                                   │
│  │                                          │ 16│   ┌ pad 20,20,20,12 ───────────────────────────────────────────────────────────────────────────┐  │
│  │  Albums                        PageHero  │ │ ││   │ ┌──────────┐  gap 16                                                                      │  │
│  │  28/36/600 · 1 line · ellipsis           │ │ ││   │ │          │   ALBUM                       Eyebrow 12/16/600 +30/1000em  TextTertiary      │  │
│  │                            pad-top 16    │ │ ││   │ │ 104×104  │   gap 3                                                                       │  │
│  │  gap 8                                   │ │ ││   │ │  r 8     │   In Rainbows                 TitleLink 28/36/600 TextPrimary, 2 lines        │  │
│  │  ┌──────────────────────────┐            │ │ ││   │ │ decode   │     hover → Tok.AccentTextPrimary (83 ms)                                     │  │
│  │  │⇅ Recents ⌃ │ ≡           │ h 32       │ │ ││   │ │  256px   │   Radiohead                   ArtistLinks 14/20/600                           │  │
│  │  └──────────────────────────┘ pad 10/0/8 │ │ ││   │ └──────────┘   2007 · 10 songs, 42 min     MetaLine 12/16 TextTertiary                     │  │
│  │  gap 8                                   │ │ ││   └───────────────────────────────────────────────────────────────────────────────────────────┘  │
│  │  ┌──────────────────────────────────────┐│ │ ││   ┌ pad 20,0,20,12 · gap 12 ───────────────────────────────────────────────────────────────────┐ │
│  │  │🔍 Filter                    minH 32  ││ │ ││   │ ( ▶ Play )  ( ⤨ )  ( ♡ )  [ View full album ]                                              │ │
│  │  │  r 4 · grow 1                        ││ │ ││   │  36 h        40    Save    Subtle/Small                                                    │ │
│  │  └──────────────────────────────────────┘│ │ ││   │  Radii.Full  circ                                                                          │ │
│  └──────────────────────────────────────────┘ │ ││   └───────────────────────────────────────────────────────────────────────────────────────────┘ │
│  ┌ ItemsView · Stack(60) · AccentPill ──────┐ │ ││   DetailNoticeBar.For(model)  — 0 height when silent                                             │
│  │ ┃ ▣  In Rainbows            SELECTED     │ │ ││  ┌ TrackList (embedded, no toolbar) ─────────────────────────────────────────────────────────┐   │
│  │ ┃ 40×40 Radiohead                        │ │ ││  │  #  Title                                              ♡        ⏱                        │   │
│  │ │    r 5   12/16 TextSecondary           │ │ ││  │  1  15 Step                                            ♡      3:57                       │   │
│  │ │ ▣  Kid A                  row h 60     │ │ ││  │  2  Bodysnatchers                                      ♡      4:02                       │   │
│  │ │    Radiohead              gap 12       │ │ ││  │  …                                                                                        │   │
│  │ │ ▣  Amnesiac               pad 8/0/8/0  │ │ ││  │                                                                                           │   │
│  │ │    Radiohead                           │ │ ││  │      see 04-detail-track-table.md                                                         │   │
│  │ └────────────────────────────────────────┘ │ ││  └───────────────────────────────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────┘│└───────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                 ↑
                                      Grip 16 DIP: 1-DIP Tok.StrokeCardDefault hairline centred,
                                      HitTestVisible=false, behind a 2-DIP hover thumb (inset 4).
```

`┃` at the row's left edge = the AccentPill selection indicator: **3 × 16 DIP, r 1.5, margin-left 4,
`Tok.AccentDefault`**, vertically centred, over a `Tok.FillSubtleSecondary` backplate inset 4/2
(`SelectorVisuals.cs:59-60, :188-207, :212-218`).

### W2 - Artists, wide, three columns, fully loaded @ content 1140

**Superseded 2026-09-18 (§0 #19, §12).** The third-of-three discography column below no longer exists; the artists
view is two panes, navigator + `Artist.Reader`. Kept here for the record — see W28 (the reader, wide) and W29 (the
reader, scope = all releases) for what replaced it.

`leftW` = 280 (the artists default, `AppSettings.cs:382`) · grip · `midW` = 440 · grip · tracks = 388.

```
├──────────── 280 ────────────┤│├──────────────── 440 ────────────────┤│├──────────── 388 ─────────────┤
┌─────────────────────────────┐│┌─────────────────────────────────────┐│┌──────────────────────────────┐
│ FillLayerDefault            │││ FillCardDefault   Basis=_midW       │││ FillCardDefault  Grow=1      │
│  Artists          PageHero  │││   MinW = railOpen ? 220 : 300       │││   MinW 220  Basis 0          │
│  ┌───────────────────────┐  │││   MaxW = _midW  Shrink 1  Grow 0    │││                              │
│  │⇅ Recents ⌃ │ ≡        │  │││  ┌ pad 12,12,12,8 · gap 8 ────────┐ │││  ┌ pad 20,20,20,12 ───────┐ │
│  └───────────────────────┘  │││  │ ⇅ Alphabetical ⌃ │ ▦   Go to  ↗│ │││  │ ┌────┐  ALBUM          │ │
│  ┌───────────────────────┐  │││  │                    artist      │ │││  │ │104 │  In Rainbows    │ │
│  │🔍 Filter              │  │││  │ ┌────────────────────────────┐ │ │││  │ └────┘  Radiohead      │ │
│  └───────────────────────┘  │││  │ │🔍 Filter                   │ │ │││  │         2007 · 10 …    │ │
│ ┌ Stack(60) · AccentPill ─┐ │││  │ └────────────────────────────┘ │ │││  └────────────────────────┘ │
│ │ ┃ ◉ Radiohead  SELECTED│ │││  └────────────────────────────────┘ │││  ( ▶ Play )  ( ⤨ )  ( ♡ )    │
│ │ ┃ 40×40 r 20 (circle)  │ │││  ┌ GridFit(140, gap 8) pad 12,0,12,0┐│││  [ View full album ]         │
│ │ │   Artist             │ │││  │ ┌────┐ ┌────┐ ┌────┐            ││││  ┌ TrackList (embedded) ──┐  │
│ │ │ ◉ Aphex Twin         │ │││  │ │ ▣  │ │ ▣  │ │[▣] │ ← Border   ││││  │ 1  15 Step      3:57   │  │
│ │ │   Artist             │ │││  │ └────┘ └────┘ └────┘   selected ││││  │ 2  Bodysnatchers 4:02  │  │
│ │ │ ◉ Boards of Canada   │ │││  │  Kid A  Amne… In Rai…           ││││  │ …                      │  │
│ │ └──────────────────────┘ │││  │  2000·  2001· 2007·ALBUM        ││││  └────────────────────────┘  │
│ │  (Project→ subtitle is   │ │││  │  ALBUM  ALBUM  12/16 Secondary ││││                              │
│ │   the literal "Artist")  │ │││  └─────────────────────────────────┘││                              │
└─────────────────────────────┘│└─────────────────────────────────────┘│└──────────────────────────────┘
                    Grip(_leftW,240,560)                    Grip(_midW,300,620)
```

**Rail-open variant (W20).** When `ShellUi.RailOpen` is true the mid pane's floor drops **300 → 220**
(`LibraryPage.cs:958`, `:674`) so the three-column sum-of-minimums still fits; nothing else changes.
`LibraryPage.Render` subscribes `ui.RailOpen.Value` at `:182` for exactly this.

### W3 - Podcasts, wide, list view @ content 1140

Identical to W1 except: `_kind == "podcasts"` ⇒ **`HasCreator` true** (so the flyout shows the "Creator" sort row) and
**`HasRelease` false** (no "Release date" row) — `LibraryPage.cs:129-130`, `LibrarySortView.cs:92-93`. Subtitle is the
**publisher**, not an artist (`LibraryPage.cs:397`). Detail pane is `_show = true`: the eyebrow falls back to "Podcast"
(loc `podcast.show`), the publisher renders as plain 14/20/600 text with no links (`:1136-1138`), the action row shows
`FollowButton` instead of `SaveButton` and **no "View full album"** (`:1185-1187`), and the body is
`CompactEpisodes` rather than `TrackList`. **Podcasts never enter full-text search mode** — `fullSearch` is gated on
`_kind != "podcasts"` (`:167`), so the box stays a plain title filter.

### W4 - Left navigator, Grid view (view = 3), size M @ leftW 340

`GridFit(116 + 1×24 = 140, gap 8)`; the wrapper pads `(8, 8, 8, 0)`. Usable width 340 − 16 = 324 → **2 columns** of
(324 − 8)/2 = 158 each.

```
┌ pad 8,8,8,0 ──────────────────────────────────┐
│ ┌───────────────┐ gap 8 ┌───────────────┐     │   Cell = NavCardContent, non-compact:
│ │ pad 8         │       │ pad 8         │     │     Dir=1 Gap=8 Clip Pad=8 (album) / 16 (artist)
│ │ ┌───────────┐ │       │ ┌───────────┐ │     │     ArtworkFill(cover, r 6)   ← square, fills the cell
│ │ │  ▣  r 6   │ │       │ │  ▣  r 6   │ │     │     TextEl 12/16/600 TextPrimary, 1 line ellipsis
│ │ └───────────┘ │       │ └───────────┘ │     │     AlignSelf = Start (album) / Center (artist)
│ │ In Rainbows   │       │ Kid A         │     │
│ └───────────────┘       └───────────────┘     │   Selector = SelectorVisual.Border (ItemContainer):
│ ┌───────────────┐       ┌───────────────┐     │     3-DIP accent ring (SelectionVisualThickness)
│ │┌─────────────┐│       │ ┌───────────┐ │     │     + 1-DIP inner stroke, inset 2, corners r 4
│ ││ ▣ SELECTED  ││ ←ring │ │  ▣        │ │     │     fade 167 ms  (ItemContainer.cs:47-53)
│ │└─────────────┘│       │ └───────────┘ │     │
│ │ Amnesiac      │       │ OK Computer   │     │   Cell-width ladder (LibraryPage.cs:509):
│ └───────────────┘       └───────────────┘     │     compact grid: 88 / 104 / 120  (S / M / L)
└───────────────────────────────────────────────┘     full grid:   116 / 140 / 164
```

Artist cards use `pad 16` and `Radii.Full` artwork so round covers do not touch (`LibraryPage.cs:920-921`).
Cover prefetch decode size follows the size knob: **64 (S) / 168 (M) / 256 (L)** px, issued for every row the moment
the list lands (`LibraryPage.cs:148-150`) — for the whole shown set, on **every** render, in list views too (the knob
is read unconditionally at `:148`).

**The two artwork paths are not the same element, and neither matches that prefetch size.** Record both, because the
loading and no-cover looks differ between the list and grid arms:

| arm | call | placeholder while loading | no cover at all | decode |
|---|---|---|---|---|
| list rows (nav + disco) | `Surfaces.Artwork(cover, uri.GetHashCode() & 0x7fffffff, 40, 40, r)` (`:906`, `:1450`) | a seeded `Shimmer` tile under the image, cross-fading out as the image decodes (`Surfaces.cs:238-287`) | the image child is a bare `BoxEl` — the **Shimmer tile is what you see** | the display size, **40 × 40** |
| grid cards (nav + disco) | `Surfaces.ArtworkFill(cover, r)` (`:921`, `:1436`) | no shimmer tile — a `WatchedPlaceholder(url)` brush graded from the cover's own colour (`Surfaces.cs:292-303`) | an empty-url `Ui.Image` ⇒ the placeholder brush alone | the parameter default, **256** regardless of S/M/L |
| detail hero | `Surfaces.Artwork(m.Cover, **m.Title**.GetHashCode() & 0x7fffffff, 104, 104, 8, decodePx: 256)` (`:1129`) | seeded shimmer | shimmer tile | 256 |
| search rows | `Surfaces.Artwork(cover, uri…, 44|36, …)` + an explicit `SkeletonOverride = CoverSkeleton(…)` (`:850-852`, `:872-877`) | the `CoverSkeleton` tile — a same-sized `FillSubtleSecondary` square (`:861-865`) | that tile | display size |

Two consequences a rebuild must decide about deliberately rather than inherit by accident: the hero seeds its generated
placeholder off the **title** while the row beside it seeds off the **uri**, so a cover-less album gets two different
fallbacks on one screen; and the 64/168/256 prefetch matches **neither** arm's real decode (40 in list, 256 in grid),
so at size S and M in grid view it warms a texture nothing asks for. Both are shipped; both are recorded here so 0.3
changes them on purpose or not at all.

### W5 - Left navigator, CompactList (view 0) and CompactGrid (view 2)

```
CompactList — Stack(40), no cover, no subtitle          CompactGrid — GridFit(88+16·size, 8), no title
┌──────────────────────────────┐                        ┌──────────────────────────────┐
│ ┃ In Rainbows        h 40    │                        │ ┌────┐ ┌────┐ ┌────┐         │
│ │ Kid A              14/20/600│                       │ │ ▣  │ │ ▣  │ │[▣] │         │
│ │ Amnesiac           pad 8/0  │                       │ └────┘ └────┘ └────┘         │
│ │ OK Computer                 │                       │ ┌────┐ ┌────┐ ┌────┐         │
└──────────────────────────────┘                        │ │ ▣  │ │ ▣  │ │ ▣  │         │
  compact drops the 40×40 cover (:904-906)              └──────────────────────────────┘
  and replaces the subtitle with an empty BoxEl (:911)    compact drops the title (:922)
```

Discography column list extents differ by 4 DIP from the navigator's: **compact 44 / non-compact 60**
(`LibraryPage.cs:1387`) vs the navigator's **40 / 60** (`:510`). That is real, shipped, and must be reproduced.

### W6 - Sort / view flyout open

Anchored `FlyoutPlacement.BottomEdgeAlignedLeft` on the pill, `FocusTrap`, `LightDismiss`, `PopupChrome.Popup`
(WinUI FlyoutPresenter acrylic + stroke + corners + shadow), `ConstrainToRootBounds = false` (`LibrarySortView.cs:49-54`).

```
 ┌ pill h 32, r 4, pad 10/0/8, gap 5 ────────┐
 │ ⇅  Recents   ⌃  │  ≡                      │   Icons.Sort 14 TextSecondary
 └───────────────────────────────────────────┘   label 14/20/600 TextSecondary
 ↓ BottomEdgeAlignedLeft                         chevron Down(desc)/Up(!desc) 12 TextTertiary
┌──────────────── MinW 230 ─────────────────┐    divider 1×16 Tok.StrokeDividerDefault
│ pad 4 · gap 1                             │    view glyph (ViewGrid if view≥2 else ViewList) 14 TextSecondary
│  SORT BY            Eyebrow TextTertiary  │ ← Header: pad 8,4,8,2
│ ┌───────────────────────────────────────┐ │
│ │ Recents                    ⌃   ✓      │ │ ← active: 14/600 Tok.AccentTextPrimary,
│ │ h 32 · r 5 · pad 10/0/8 · gap 8       │ │    chevron 11 + check 12, both AccentTextPrimary
│ │ Recently added                    (12)│ │ ← inactive: 14/400 Tok.TextPrimary + 12-wide spacer
│ │ Alphabetical                          │ │
│ │ Creator            [hasCreator only]  │ │ ← albums + podcasts; hidden for artists
│ │ Release date       [hasRelease only]  │ │ ← albums + the discography; hidden for artists/podcasts
│ └───────────────────────────────────────┘ │
│ ────────── divider 1 px, margin 4 ─────── │
│  VIEW AS                                  │
│ ┌────┬────┬────┬────┐  gap 4, pad 2,2,2,4 │  4 cells 40×30, Grow 1, r 5
│ │ ≡  │ ≡  │ ▦  │ ▦  │                     │  glyph sizes 14 / 16 / 12 / 15
│ │ 14 │ 16 │ 12 │ 15 │                     │  ON  → Fill Tok.AccentDefault,  ink Tok.TextOnAccentPrimary
│ └────┴────┴────┴────┘                     │        hover Tok.AccentSecondary
│  SIZE          [only when view ≥ 2]       │  OFF → Fill Tok.FillSubtleSecondary, ink Tok.TextSecondary
│ ┌───────────────────────────────────────┐ │        hover Tok.FillSubtleTertiary
│ │   S      M      L       SelectorBar   │ │
│ └───────────────────────────────────────┘ │
└───────────────────────────────────────────┘
```

Click rule (`LibrarySortView.cs:118`): **a different key → `sort = key, desc = false`; the active key → `desc = !desc`.**
The flyout stays open (no auto-dismiss on pick) so a user can flip direction immediately.

### W7 - Detail pane skeleton (selection loading) @ right pane 784

`LibraryPage.cs:1225-1235`. Note this is a **hand-authored** skeleton, not a `SkelRegionEl`: solid
`Tok.FillCardDefault` blocks with no shimmer pulse and no blur-reveal. Parity requires the same.

```
┌ pad 20 · gap 16 ────────────────────────────────────────────┐
│ ┌──────────┐ gap 16                                          │
│ │ 104×104  │  ▭ 80×12 r4                                     │  every block: Fill = Tok.FillCardDefault
│ │   r 8    │  ▭ 200×22 r4        col gap 8                   │
│ │          │  ▭ 140×12 r4                                    │
│ └──────────┘                                                 │
│  gap 16                                                      │
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  h 14 r4  margin-top 8│  × 6, column gap 8
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                     │
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                     │
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                     │
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                     │
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                     │
└──────────────────────────────────────────────────────────────┘
```

### W8 - Discography pane skeleton @ midW 440 — DELETED 2026-09-18 (§0 #19, §12)

The column this wireframe paints — `LibraryArtistPane`'s discography grid/list — is deleted, not hidden, with the
rest of the three-column artists layout (rule 19). Its replacement's loading state is `Skel.Region` per catalogue
block (plan's counted skeleton), not a whole-pane gate — see W29. Kept here for the record.

`LibraryPage.cs:1474-1478`. **The toolbar stays up** — only the body swaps (`:1279-1286`), so the sort pill, filter and
"Go to artist" never flash in and out across a selection change.

```
┌ Toolbar — RENDERED, unchanged ───────────────────┐
│ ⇅ Alphabetical ⌃ │ ▦              Go to artist ↗ │
│ ┌─────────────────────────────────────────────┐  │
│ │🔍 Filter                                    │  │
│ └─────────────────────────────────────────────┘  │
├ body: pad 12 · gap 8 ────────────────────────────┤
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  h 148  r 8   │  × 8 full-width blocks,
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  FillCardDefault│  Dir=1 (a column, not a grid)
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                │
│ …                                                │
└──────────────────────────────────────────────────┘
```

### W9 - No selection (the three placeholders) — TWO placeholders, 2026-09-18 (§12)

**Superseded in part.** The third placeholder below ("Select a release", the discography column's tracks pane) is
gone with the three-column artists layout (§0 #19) — there is no third pane to be empty. Only the albums and
artists placeholders remain, and the artists one now belongs to `Artist.Reader`'s own no-selection state (the
reader still shows `Controls.Vacancy(Empty, Compact, …)`, not a bare `EmptyState.Compact`, but the *occasion* —
nothing selected in the navigator — is the same). Kept here for the record; the surviving two placeholders' rule is
unchanged.

**When these are actually on screen.** `hasSel` is `_selectedKey.Value.Length > 0` (`LibraryPage.cs:228-229`) and
`SyncNav` adopts row 0 the moment a non-empty set lands (`:558-564`), so the two left-hand placeholders below appear
only while the navigator is empty — a cold start before `EnsureAlbums` resolves, or the filter-matched-nothing state of
§0 #17. The third ("Select a release") was the durable one: `_albumKey` is empty until `SyncDisco` fires, and a search
commit or a restored key can leave it set to a release the shown discography does not contain. Do not design the first
two as a resting screen.

`EmptyState.Compact` = `Ui.Subtitle` headline (**20/28/600**), centred, `Gap 4`, `Padding 24`
(`EmptyState.cs:43-45, :68-72`). No glyph — the grammar forbids it.

```
albums:   ┌──────────────────────────┐   artists:  ┌──────────────────────────┐   artists, 3rd column:
          │                          │             │                          │   DELETED 2026-09-18 —
          │  Select an album to see  │             │ Select an artist to see  │   there is no 3rd column;
          │  its tracks              │             │ their discography        │   Artist.Reader's own
          │       20/28/600 centred  │             │                          │   Vacancy(Empty) covers
          └──────────────────────────┘             └──────────────────────────┘   the no-selection case.
          Key="lib:empty"  loc library.selectAlbum  loc library.selectArtist
podcasts: "Select a show to see its episodes"  (loc library.selectShow)
```

### W10 - Navigator: filtered to nothing vs still loading

```
FILTER MATCHED NOTHING (filter text present)          STILL LOADING (no filter text, 0 rows)
┌──────────────────────────────────────┐              ┌──────────────────────────────────────┐
│  ┌ toolbar unchanged ──────────────┐ │              │  ┌ toolbar unchanged ──────────────┐ │
│  │🔍 zzzz                          │ │              │  │🔍 Filter                        │ │
│  └─────────────────────────────────┘ │              │  └─────────────────────────────────┘ │
│                                      │              │  ┌ pad 12,20,12,20 ────────────────┐ │
│      Nothing matches your filter     │              │  │ …                               │ │
│        20/28/600, centred            │              │  │ Caption(12/16).Secondary()      │ │
│        loc library.noMatch           │              │  └─────────────────────────────────┘ │
│                                      │              │  (LEFT-aligned, small — deliberately │
└──────────────────────────────────────┘              │   NOT a big-type empty state)        │
   LibraryPage.cs:491                                 └──────────────────────────────────────┘
                                                          LibraryPage.cs:492
```

**The left arm above is only half the frame.** The filter-matched-nothing branch also runs `SyncNav`'s clear
(`LibraryPage.cs:546-556`): `_selectedKey = ""`, `_albumKey = ""` (artists), `SyncSelect(-1)`. So the *whole page* is:

```
├──────────── 340 ────────────┤│├──────────────────── 784 ────────────────────┤
┌─────────────────────────────┐│┌─────────────────────────────────────────────┐
│  Albums          PageHero   │││                                             │
│  ⇅ Recents ⌃ │ ≡            │││                                             │
│  ┌───────────────────────┐  │││        Select an album to see its tracks    │ ← Pane Key="lib:empty"
│  │🔍 zzzz                │  │││              20/28/600 centred              │   (the selection was just
│  └───────────────────────┘  │││                                             │    thrown away, :552-554)
│                             │││                                             │
│  Nothing matches your filter│││                                             │
│      20/28/600 centred      │││                                             │
└─────────────────────────────┘│└─────────────────────────────────────────────┘
```

Clearing the filter does **not** restore the previous selection — `SyncNav` re-adopts **row 0** of the restored set
(`:563`). Losing the user's place on a mistyped filter is shipped 0.2.9 behaviour; reproduce it, and flag it to the
owner rather than "fixing" it unilaterally. Note this branch is reachable only when the plain title filter is in force:
`albums`/`artists` on a real session enter full-text search instead (`:167`), so in practice it is **podcasts**, and
every kind under `--fake`.

**An empty *library* (no saves at all) shows the "…" state indefinitely** in 0.2.9 — there is no
"you haven't saved anything yet" copy anywhere in `Features/Library`. Parity means reproducing that; §9 flags it as the
one place the re-author may legitimately improve, but only with the owner's sign-off.

### W11 - Full-text search, artists view, results @ content 1140

Entered the moment `_filter` is non-empty AND kind ≠ podcasts AND `svc.RealStore != null` (`LibraryPage.cs:167`).
The whole three-column body swaps; the **toolbar and the page title stay**.

```
├──────────── 280 ────────────┤│├──────────────── 440 ────────────────┤│├──────────── 388 ─────────────┤
┌─────────────────────────────┐│┌─────────────────────────────────────┐│┌──────────────────────────────┐
│  Artists                    │││ ┌ FacetHeader pad 12,12,12,8 gap 4 ┐│││ ┌ FacetHeader ──────────────┐ │
│  ⇅ Recents ⌃ │ ≡            │││ │ ALBUMS   7                       ││││ │ SONGS   23                │ │
│  ┌───────────────────────┐  │││ │ Eyebrow  12/16/600 TextTertiary   ││││ └───────────────────────────┘ │
│  │🔍 rainb               │  │││ └──────────────────────────────────┘│││ ┌ SearchScroll gap 2 ───────┐ │
│  └───────────────────────┘  │││ ┌ SearchScroll gap 2, pad 8,4,8,92 ┐││││ │┌─────────────────────────┐│ │
│ ┌ SearchScroll ───────────┐ │││ │┌────────────────────────────────┐│││││ ││▣ 15 Step          h 44 ││ │
│ │┌───────────────────────┐│ │││ ││ ▣  In [Rainb]ows      h 56     ││││││ ││36×36 r4  gap 12        ││ │
│ ││◉ Ra[dioh]ead   h 56   ││ │││ ││44×44 r4  2007 · ALBUM          ││││││ │└─────────────────────────┘│ │
│ ││44×44 r Full           ││ │││ │└────────────────────────────────┘│││││ │┌─────────────────────────┐│ │
│ ││SELECTED FillSubtle2ndy││ │││ │┌────────────────────────────────┐│││││ ││▣ Bodysnatchers         ││ │
│ │└───────────────────────┘│ │││ ││ ▣  Kid A                       ││││││ │└─────────────────────────┘│ │
│ │┌───────────────────────┐│ │││ ││    2000 · ALBUM  SELECTED      ││││││ │ 13/600, highlight pill on ││ │
│ ││Matched · album ‘Rain…’││ │││ │└────────────────────────────────┘│││││ │ the matched run           ││ │
│ ││◉ Thom Yorke           ││ │││ └──────────────────────────────────┘│││ └───────────────────────────┘ │
│ ││  eyebrow 12/16/600    ││ │││   Key="s:albums"                    │││   Key="s:tracks"               │
│ │└───────────────────────┘│ │││   same flex as artistPane (W2)      │││   Grow 1 · Basis 0 · MinW 220  │
│ └─────────────────────────┘ ││└─────────────────────────────────────┘│└──────────────────────────────┘
└─────────────────────────────┘│                Grip(_midW, 300, 620)
```

`[…]` = the accent highlight pill. Scroll padding is `(8, 4, 8, PlayerDock.Reserve(72) + 20 = 92)`
(`LibraryPage.cs:768`) so the last row clears the transport.

The **albums view** search is two columns: left = matched albums (`AlbumRow` with `explainMatch: true`), right =
`SearchAlbumDetail` (`Key="s:detail"`, a single `FacetHeader("Songs") + track hits`) — `LibraryPage.cs:660-662, :692-703`.

### W12 - Search shimmer (first query of a session)

Gate (`LibraryPage.cs:730-745`): shimmer while `Pending`, **or** while the answer in hand is empty and a newer one is
coming (`IsFetching || query != raw`). Non-empty results are never replaced by a shimmer — that is the
stale-while-revalidate promise of `KeepPreviousData` (`:77, :194-196`).

```
┌────────────────────────┐┌──────────────────────────────┐┌────────────────────────┐
│ ALBUMS  (count hidden) ││ ALBUMS   (count hidden)      ││ SONGS   (count hidden) │  count < 0 ⇒ no number at all
├────────────────────────┤├──────────────────────────────┤├────────────────────────┤
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  ×6  ││ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  ×6    ││ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬  ×6     │  SkelRows = 6 (:713)
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬      ││ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬        ││ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬         │  bars DERIVED from the SAME
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬      ││ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬        ││ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬         │  row builder (SkelArtistRow /
│ …                      ││ …                            ││ …                      │  SkelAlbumRow / SkelTrackRow)
└────────────────────────┘└──────────────────────────────┘└────────────────────────┘
  Pad (8,4,8,4) gap 2 (:756)   ONE shared Group token (_skelGroup) → all three settle in the same window (:52-53, :208)
  Pulse: opacity 1 → 0.5 → 1, 1000 ms loop (SkeletonStyle.Default)
  Bar colour: Tok.FillSubtleSecondary; cover slots override to a same-sized tile in that colour (CoverSkeleton, :861-865)
```

### W13 - Search row anatomy

```
SelectableRow  (ArtistRow / AlbumRow) — LibraryPage.cs:825-856
┌───────────────────────────────────────────────────────────────── h 56, Corners r 4, Clip ──┐
│ pad 8/0/8/0 · gap 12                                                                        │
│  ┌────────┐   Matched · album ‘In Rainbows’     ← eyebrow: 12/16/600 Tok.TextSecondary,     │
│  │ 44×44  │                                        1 line, ONLY when MatchReason.ShouldExplain│
│  │ r 4 or │   Ra[dioh]ead                       ← SearchHighlight.Row(title, start, len,    │
│  │ r Full │      gap 1                             14, 600, Tok.TextPrimary)                │
│  │        │   2007 · ALBUM                      ← subtitle 12/16 Tok.TextSecondary, 1 line  │
│  └────────┘                                        (empty for artists ⇒ omitted entirely)   │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
  Fill      selected ? Tok.FillSubtleSecondary : transparent          ← the BROWSE-LIST language,
  HoverFill Tok.FillSubtleSecondary                                      deliberately NOT Tok.AccentSubtle
  PressFill Tok.FillSubtleTertiary                                       (:841-846)
  Key       "search:" + uri       Animate = SearchRowChange (see §5)

TrackHitRow — LibraryPage.cs:867-891
┌───────────────────────────────────────────── h 44, r 4, Clip, Interaction.Subtle ──┐
│ pad 8/0/8/0 · gap 12                                                                │
│  ┌──────┐   [15] Step            ← 13/600 Tok.TextPrimary + highlight pill          │
│  │36×36 │                           (no subtitle, no eyebrow)                        │
│  │ r 4  │                        ← artwork dropped entirely when                     │
│  └──────┘                           AppearancePrefs.TrackArtworkHidden (:869)        │
└─────────────────────────────────────────────────────────────────────────────────────┘
  Key "search:" + uri + ":art=" + showArtwork   ← the art toggle is part of the identity (:885)
  OnClick → PlayTrack(albumUri, t.AlbumIndex)

Highlight pill — SearchHighlight.cs:42-54
    [before]  ┌─────────┐  [after]        plate: Tok.AccentSelectedTextBackground, r 4, pad 3/1/3/1
              │ matched │                 ink:   Tok.TextOnAccentSelectedText, 1 line, NoWrap
              └─────────┘                 row:   Dir 0, AlignCenter, Clip, Basis 0, Grow 1
    matchLen ≤ 0 or out of range ⇒ a plain TextEl, no pill (:23-29)
```

### W14 - Collapsed, depth 0 (master) @ content 560

Trigger: `LibraryLayoutBreakpoints.Collapsed(w, was)` — enter below **640**, leave at **≥ 664** (640 + 24 hysteresis).
Entering collapsed resets `_depth` to 0 (`LibraryPage.cs:219`).

```
├────────────────────────── 560 ──────────────────────────┤
┌──────────────────────────────────────────────────────────┐
│ ┌ CollapsedCrumbBar — Fill Tok.FillLayerDefault ───────┐ │
│ │ pad 12,8,12,8                                        │ │
│ │  Artists                                             │ │ ← BreadcrumbBar.Create(crumbs, i => _depth = i)
│ │  14/20 Tok.TextPrimary, pad 1/3, r 4, Role=Button    │ │    crumb[0] = KindCrumb():
│ └──────────────────────────────────────────────────────┘ │      artists→"Artists" (loc search.artists)
│ ──────────────────── 1 DIP Tok.StrokeDividerDefault ──── │      albums →"Albums"  (loc search.albums)
│ ┌ Toolbar(title:FALSE) — pad 12,12,12,8 ───────────────┐ │      else   →"Podcast" (loc podcast.show)
│ │ ⇅ Recents ⌃ │ ≡                                      │ │
│ │ ┌──────────────────────────────────────────────────┐ │ │  NO PageHero here: two titles for one
│ │ │🔍 Filter                                         │ │ │  column is the double-title this
│ │ └──────────────────────────────────────────────────┘ │ │  converges away from (:452-455)
│ └──────────────────────────────────────────────────────┘ │
│ ┌ ListBody(nav) — full width ──────────────────────────┐ │
│ │ ┃ ◉ Radiohead                           h 60        │ │
│ │ │ ◉ Aphex Twin                                      │ │  Tapping a row → Select(it) AND _depth = 1
│ │ │ ◉ Boards of Canada                                │ │  (:527) — the collapsed drill-in.
│ └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### W15 / W16 / W17 - Collapsed, depths 1 and 2

**Depths changed 2026-09-18 (§12).** Artists' `maxDepth` drops **2 → 1** in the browse arm: `Artist.Reader` is one
pane, so W15 (below) is now the whole of depth 1 and **W16 (depth 2) no longer exists in browse** — kept below for
the record. Albums/podcasts stay at `maxDepth = 1` (W17, unchanged). **Search mode is untouched**: the three
collapsed search bodies still drill to depth 2 (§0 #9's "same columns" rule was never in scope for this rework), so
artists SEARCH keeps 0/1/2 while artists BROWSE keeps 0/1.

```
W15  artists, depth 1  (maxDepth = 1, browse)  W16  artists, depth 2 — browse: DELETED; SEARCH: unchanged  W17  albums/podcasts, depth 1
┌────────────────────────────────────┐       ┌────────────────────────────────┐   ┌────────────────────────────┐
│ Artists › Radiohead                │       │ Artists › Radiohead › Kid A    │   │ Albums › In Rainbows       │
│ ────────────────────────────────── │       │ ────────────────────────────── │   │ ────────────────────────── │
│ Pane Key="col:disco" Grow 1 Basis 0│       │ Pane Key="col:tracks"          │   │ Pane Key="col:detail"      │
│  ┌ LibraryArtistPane ───────────┐  │       │  ┌ LibraryDetailPane ───────┐  │   │  ┌ LibraryDetailPane ───┐  │
│  │ ⇅ Alphabetical ⌃ │ ▦  Go ↗  │  │       │  │ ┌────┐ ALBUM             │  │   │  │ ┌────┐ ALBUM         │  │
│  │ 🔍 Filter                    │  │       │  │ │104 │ Kid A             │  │   │  │ │104 │ In Rainbows   │  │
│  │ ┌────┐┌────┐┌────┐           │  │       │  │ └────┘ Radiohead         │  │   │  │ └────┘ Radiohead     │  │
│  │ │ ▣  ││ ▣  ││[▣] │ tap →     │  │       │  │ (▶Play)(⤨)(♡)[View full] │  │   │  │ (▶Play)(⤨)(♡)[View]  │  │
│  │ └────┘└────┘└────┘ depth 2   │  │       │  │  TrackList (embedded)    │  │   │  │  TrackList / episodes│  │
│  └──────────────────────────────┘  │       │  └──────────────────────────┘  │   │  └──────────────────────┘  │
└────────────────────────────────────┘       └────────────────────────────────┘   └────────────────────────────┘
  crumb text = artist.Name, "…" while loading   crumb[2] = albumTracks.Title        maxDepth = 1 for albums/podcasts
  (Crumb(s) → s.Length>0 ? s : "…", :346)
```

Crumb sources swap in search mode: `FindArtist(sr, sArtist)?.Name` / `FindAlbum(...)?.Name` instead of the loadables
(`LibraryPage.cs:263-268`).

### W18 - Grip: rest / hover / dragging

```
 rest           hover                 dragging
┌─┐            ┌─┐                   ┌─┐            strip: 16 DIP wide, Shrink 0, ZStack, Cursor = SizeWE
│┊│            │┃│                   │┃│            seam:  1 DIP, AlignSelf Stretch, JustifySelf Center,
│┊│            │┃│  ← 2-DIP thumb    │┃│                   Fill = Prop.Of(() => Tok.StrokeCardDefault),
│┊│            │┃│     r 1, inset 4  │┃│                   HitTestVisible = false
│┊│            │┃│     Tok.FillControlStrong          thumb: Opacity 0 → 1 on hover/press,
│┊│            │┃│     opacity 0 → 1                        HoverDurationMs = Motion.ControlFast,
└─┘            └─┘                   └─┘                    Easing.FluentDecelerate
 1 DIP seam     seam + thumb          width writes 1:1 to the pointer; layout transitions suppressed
                                      for the gesture; onCommit fires ONCE on drag-end → settings write
```

Ranges: left column **240 … 560**, mid column **300 … 620** (`LibraryPage.cs:233, :687, :969`). No detent
(`collapsed` is null), so the columns clamp and never collapse.

### W19 - Row states (the two selector visuals)

```
AccentPill (list arms)                              Border (grid arms) — ItemContainer.Build
 rest      ┌────────────────────────┐                rest      ┌──────────┐   transparent
           │ (transparent)          │                          └──────────┘
 hover     ┌────────────────────────┐                hover     ┌──────────┐   subtle plate
           │ Tok.FillSubtleSecondary│                          └──────────┘
 pressed   ┌────────────────────────┐                selected ╔══════════╗   3-DIP Tok.AccentDefault ring
           │ Tok.FillSubtleTertiary │                         ║┌────────┐║   + 1-DIP inner stroke, inset 2
 SELECTED ┃┌────────────────────────┐                         ║└────────┘║   corners r 4, fade 167 ms
          ┃│ Tok.FillSubtleSecondary│                         ╚══════════╝
 sel+hover┃│ Tok.FillSubtleTertiary │               plate margin: 4,2,4,2 · MinHeight 40 · r 4
 sel+press┃│ Tok.FillSubtleSecondary│               focus: engine focus ring, FocusVisualMargin 1
          ↑ 3×16 r1.5 AccentDefault, margin-left 4; PressScale 10/16 = 0.625
```

### W21 - Local Files (the detail frame, NOT this page) @ content 1140

Reached by route `local` → `DetailPage` → `DetailShell` with `kind = Playlist`, `uri = "wavee:local:all"`.

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│  ┌──────────┐   PLAYLIST                                                                            │
│  │ generated│   Local Files                                       ← Playlist.Name (LocalSource.cs:28)
│  │  gradient│   Music imported from this computer.                ← Description
│  │  cover   │   On this device · 14 songs                         ← Owner("local","On this device")
│  └──────────┘                                                        + TrackCount
│  ( ▶ Play ) ( ⤨ ) ( ⋯ )                                                                             │
│  # Title              Artist            Album             ⏱                                         │
│  1 Sunset Boulevard   Marble Sounds     Sunset Boulevard  2:30                                      │
│  … 14 rows (FakeData.LocalSeed, FakeData.cs:344-351)                                                │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
  Capabilities: CanView, CanEditItems, CanEditMetadata, IsOwner = true; IsCollaborative = false
  Everything about this frame belongs to 03-detail-frame.md + 06-playlist.md.
```

### W22 - Drag in progress from a navigator row / card

```
 ┌───────────────────────────┐                 source: NavRowContent / NavCardContent / DiscoRowContent /
 │ ┃ ▣  In Rainbows          │  ← row stays     DiscoCardContent  (all four carry Draggable)
 │ │    Radiohead            │    fully painted kind:  WaveeDragKinds.Resource
 └───────────────────────────┘    (no source-hide) payload: WaveeResourceDragPayload.ForEntity(
                ╲                                             KindOfRoute(routeKey), uri, title, cover, acts)
                 ╲  ┌──────────────┐             threshold: Drag.ClickPrimaryThresholdMultiplier = 2 ×
                  ╲ │ ▣ In Rainbows│  ← the       (WinUI LISTVIEWBASEITEM_MOUSE_DRAG_THRESHOLD_MULTIPLIER)
                    └──────────────┘    shared    targets: the sidebar, playlist rows, folder tiles —
                     drag chip                    NOTHING on this page accepts a drop.
                     (01-track-row.md)
```

### W23 - A search facet with zero hits (the column that says nothing)

Only `LeftSearchBody` owns a 0-hit message (`LibraryPage.cs:657-662`). Every other search column is
`FacetHeader + SearchSkel(… SearchScroll(items, row))` with no count test, so an artist whose matched albums list is
empty — or an album with no matched tracks — renders a header and an empty scroll body.

```
├──────────── 280 ────────────┤│├──────────────── 440 ────────────────┤│├──────────── 388 ─────────────┤
┌─────────────────────────────┐│┌─────────────────────────────────────┐│┌──────────────────────────────┐
│  Artists                    │││ ALBUMS   0     ← count renders "0"  │││ SONGS   0                    │
│  ┌───────────────────────┐  │││                   (count >= 0 ⇒     │││                              │
│  │🔍 zzz                 │  │││                    the number IS    │││                              │
│  └───────────────────────┘  │││                    drawn, :782-787) │││                              │
│                             │││                                     │││                              │
│   Nothing matches your      │││          (nothing — no message,     │││      (nothing)               │
│   filter                    │││           no empty state, just      │││                              │
│   14/20 Tok.TextTertiary    │││           an empty ScrollView)      │││                              │
│   pad 12,20,12,20  (:792)   │││                                     │││                              │
└─────────────────────────────┘│└─────────────────────────────────────┘│└──────────────────────────────┘
   SearchMessage — the ONLY      Key="s:albums"  :672-677                Key="s:tracks"  :678-683
   zero-hit copy on the page
```

Note the left column's zero-hit copy is `SearchMessage` — **14 / 20 `Tok.TextTertiary`, left-aligned, pad
(12,20,12,20)** (`:792-796`) — and NOT the browse list's big-type `EmptyState.Compact` at 20/28/600 (`:491`). Two
different "nothing matches your filter" treatments, same loc key `library.noMatch`, one page. Reproduce both.

### W24 - A failed browse load (there is no error state)

`LibraryDetailPane` gates on `st != LoadState.Ready || m is null || m.Title.Length == 0` (`:1030`) and
`LibraryArtistPane` on `st != Ready || a is null || a.Name.Length == 0` (`:1281`). **Neither has a Failed arm.** A
detail/artist load that throws — or that resolves Ready with an empty title — parks on its skeleton forever.

```
┌ right pane, load FAILED ─────────────────────────────────┐    The W7 skeleton, permanently:
│ ┌──────────┐                                             │      · solid Tok.FillCardDefault blocks
│ │ 104×104  │  ▭ 80×12                                    │      · no pulse, no reveal, no retry
│ │   r 8    │  ▭ 200×22        (identical to W7 —          │      · no ErrorState, no copy, no glyph
│ │          │  ▭ 140×12         the pane cannot tell you   │      · the toolbar above it (disco) stays
│ └──────────┘                   anything went wrong)       │        fully live and interactive
│ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  × 6                           │
└──────────────────────────────────────────────────────────┘
```

`ErrorState.Build(loadable.Error)` is wired on exactly one path — the search columns' `SkelRegionEl.OnFailed`
(`:741`), which `KeepPreviousData` makes rare in the first place (only a *cold* failure reaches it). This is a real
hole, recorded honestly: 0.3 may close it, but it is a **change**, and the owner decides.

### W25 - Collapsed: what the narrow layout does NOT have

```
┌ CollapsedLayout — :255-286 ──────────────────────────────────────────────────┐
│  NO Grip anywhere. The two splitters exist only in the wide arm (:233, :687,  │
│  :969); collapsed, `_leftW` / `_midW` are still persisted and still seeded,   │
│  but nothing reads them and there is no way to resize.                        │
│                                                                               │
│  Toolbar() is mounted ONLY at depth 0 (:274-275). At depth 1 and 2 the page's │
│  sort pill and filter box are GONE — the only controls left are the artist    │
│  pane's own copies (LibraryArtistPane.Toolbar, :1292-1305). Crumb back to     │
│  depth 0 to re-narrow the master list.                                        │
│                                                                               │
│  Depth 0's body swaps for search exactly like the wide layout's left column   │
│  (`fullSearch ? LeftSearchBody : ListBody`, :275). Depths 1/2 swap too:       │
│    · artists d1 → SearchSkel(SkelAlbumRow, AlbumRow…)  instead of the pane    │
│      (Key stays "col:disco", :300-306)                                        │
│    · artists d2 / albums d1 → SearchSkel(SkelTrackRow, TrackHitRow…)          │
│      (Keys "col:tracks" / "col:detail", :309-338)                             │
│  So a collapsed search shows hit rows, never the compact detail pane.         │
│                                                                               │
│  The LAST crumb is inert: no OnClick, and no hover/pressed ink ramp —         │
│  BreadcrumbBar gives the hover/press colours only when `!isLast`              │
│  (BreadcrumbBar.cs:96-103). It is still a TabStop and still Role=Button.      │
└───────────────────────────────────────────────────────────────────────────────┘
```

### W26 - Search hits that carry no highlight

`LibraryAlbumGroup` carries **all** of an album's tracks when the album's or artist's *name* matched, and only the
matching tracks otherwise (`LibrarySearch.cs:35-38`); `MatchLen == 0` on any level means that level's own name did not
match, and `SearchHighlight.Row` then returns a plain `TextEl` with **no pill** (`SearchHighlight.cs:23-29`).

```
query "radiohead" ⇒ the ARTIST name matched, so:
┌──────────────────────────────┐┌──────────────────────────────┐┌──────────────────────────────┐
│ ◉ Ra[diohead]   ← pill       ││ ▣ In Rainbows   ← NO pill    ││ ▣ 15 Step       ← NO pill    │
│ ◉ Radiohead …                ││ ▣ Kid A         MatchLen 0   ││ ▣ Bodysnatchers              │
└──────────────────────────────┘└──────────────────────────────┘└──────────────────────────────┘
                                   every album, un-highlighted     EVERY track of the album,
                                                                   un-highlighted — the Songs
                                                                   column is a full tracklist here
```

A rebuild that only ever paints highlighted runs will render the second and third columns as empty. The pill is a
*match* affordance, not a row decoration.

### W27 - A file dragged over a library page (the shell's drop cue - the one local-files visual this page gets)

§0 #14 says the page is a drop target for nothing — and that is precisely why this cue appears **over** it. The engine
hands a drop to the deepest **accepting** node, so a file dragged anywhere over the navigator, either grip, or the
reading panes falls through to the shell backdrop's own `Files` target (`WaveeShell.cs:1351-1359`, wired as
`DropTarget = _fileDrop` at `:1385`). The library page itself paints nothing: no row hover, no insertion line, no
column highlight, no re-render at all.

```
┌ the library page, UNCHANGED under the cue ──────────────────────────────────────────────┐
│ Albums                      ││  In Rainbows                                             │
│ ┌───────────────────────┐   ││  Radiohead                                               │
│ │ ┃ ▣  In Rainbows      │   ││  ( ▶ Play ) ( ⤨ ) ( ♡ )                                  │
│ │ │ ▣  Kid A            │   ││  ┌────────────────────────────────────────────────────┐  │
│ │ │ ▣  Amnesiac         │   ││  │  1  15 Step   ┌────────────────────────┐   3:57    │  │
│ │ │ ▣  OK Computer      │   ││  │  2  Bodysnat… │ Drop a file to play it │   4:02    │  │
│ └───────────────────────┘   ││  │  3  Nude      └────────────────────────┘   4:15    │  │
│   ← no hover, no ring,      ││  │       ↑ centred on the WHOLE window, both axes      │  │
│     no insertion line       ││  └────────────────────────────────────────────────────┘  │
└─────────────────────────────┴┴──────────────────────────────────────────────────────────┘

 the pill  (WaveeShell.cs:1418-1431):  pad 18/10 · r Radii.Control (4) · Fill Tok.FillSolidBase
                                       Border 1 DIP Tok.AccentDefault · Shadow Elevation.Dialog
                                       TextEl loc `localFile.dropHint` = "Drop a file to play it", 14, TextPrimary
 the layer:                            Grow 1 · HitTestPassThrough · Direction 1 · Justify + AlignItems Center
                                       Opacity = Prop.Of(() => _fileDropOver.Value ? 1f : 0f)   ← BOUND, so the shell
                                       (and this page) is never re-rendered by a drag hover
```

What the release does, by drop content (`LocalPlayables.ClassifyDrop:155-163` — **audio wins over video**, deliberately,
because a plain audio drop is the unambiguous "play this song" gesture):

| the drop contains | result | source |
|---|---|---|
| an `.mp3` / `.ogg` / `.flac` | `LocalPlayables.ForLocalFile` builds a **complete** synthetic `Track` (`TrackOrigin.Local`, `Source = "local"`, `Availability.Playable`, title = the file name without its extension, duration from a fail-soft header probe) and `Player.PlayTrackAsync` starts it. **No navigation and no selection change** on the library page. | `LocalPlayables.cs:28-32, :88-114, :120-139`, `LocalFileActions.cs:81-86` |
| an `.mp4` and no audio file | self-attaches as its own playable's video override through `VideoActions.Apply`, then plays as video | `LocalFileActions.cs:88-106` |
| nothing playable | error toast "That file can't be played. Wavee plays .mp3, .ogg, .flac and .mp4 files." (loc `localFile.rejected`) | `LocalFileActions.cs:64-67` |
| anything, on a build with no audio host | informational toast "Local playback isn't ready yet." (loc `localFile.notReady`) — reachable only from a **drop**, because the menu row is hidden rather than disabled (`:24-32`) | `LocalFileActions.cs:69-75` |

The remaining `localFile.*` strings (`pickTitle`, `filter`, `playFile`) belong to the profile menu's "Play file…" row
(`ProfileMenu.cs:114-119`), not to this page; recorded here only so the family is accounted for in one place.

---

### W28 - Artists, wide: navigator (280) │ `Artist.Reader` (844) — new 2026-09-18, replaces W2 (§12)

Source: `library-rework-implementation.md` §2 W3. Two panes, not three — the discography column of W2 is gone.

```
├──────────── 280 ─────────────┤│├────────────────────────────────── 844 ──────────────────────────────────────┤
┌──────────────────────────────┐│┌────────────────────────────────────────────────────────────────────────────┐
│ Artists  38          PageHero│││ band, pad 20,20,20,8, gap 16, AlignItems Center                             │
│ recents  a–z  albums    ≡ ▦  │││  (72 circ) Stromae      32/38/600 (link)   ( ▶ Play all )( ⤨ )(✓ following)│
│ 🔍 Filter                    │││  In your library: 3 albums · 35 songs · following   12/16 TextTertiary ( ↗ )│
│ ┃◉ Stromae  3 albums·35 songs│││ sub-rail, sticky top 0, pad 6,20,10, hairline below                        │
│  ◉ Urban Zakapa  2 · 15 songs│││  in your library   all releases · 6         newest  oldest  a–z             │
│  ◉ Jukjae        2 · 9 songs │││ spine (56, sticky) │ ItemsView.Create · Extents(blockOf)                    │
│  …                           │││ [36]◄current block │ block: pad 16,20,8,16, grid 120|1fr, gap 16            │
│                               │││ [36]                │ 120-cover  Multitude  3 songs·10 min  (▶)(♥)(⋯)      │
│                               │││ [36]                │   1  Invaincu               3:07  (Track.EagerRow,   │
│                               │││ (sticky top 52)     │   2  Santé                  3:10   rowH 36)          │
└──────────────────────────────┘│└────────────────────────────────────────────────────────────────────────────┘
```

The artist row's subtitle is no longer the literal loc constant `search.typeArtist` ("Artist") — §0's original
anatomy called that out as a defect (§1.1.D of the plan); it now reads "N albums · M songs" from
`LibraryAlbumsOf`/`LibrarySongCountOf` (CORE, `User.cs`), a pure read over `Edges.SavedAlbums ∩ Edges.AlbumArtists`,
no new demand. The spine is a block jump list (one 36×36 cover per catalogue block, r 4, opacity .5 → .9 hover → 1,
a 2-DIP accent ring on the block under the viewport top via `ItemsViewController.TryGetItemIndex(0, 0.2)`); it hides
under 1200 px of reader width (W6 of the plan). Sort here is the artists word rail (rule 20): `recents · a–z ·
albums` (code 5, new). Scope and the reader's own sort (`newest`/`oldest`/`a–z`, a reader-local `ReaderSort` enum,
NOT `LibraryNavSort`) live in the sub-rail.

### W29 - `Artist.Reader`, scope = all releases, catalogue blocks landing — new 2026-09-18 (§12)

Source: `library-rework-implementation.md` §2 W4. Saved blocks land first (in the chosen sort), catalogue blocks
after, same sort:

```
│  in your library   all releases · 9          newest  oldest  a–z       │
│                    ‾‾‾‾‾‾‾‾‾‾‾‾‾‾                                       │
│ [36] │ Multitude                    2022 · Album  (saved)              │  saved blocks first
│ [36] │ racine carrée                2013 · Album  (saved)              │
│ [··] │ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒               ▒▒▒▒ · Album                       │  a catalogue block whose DiscoCard
│      │   ▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒               ▒:▒▒                            │  has not landed: a COUNTED skeleton
│      │   ▒ ▒▒▒▒▒▒▒▒▒                    ▒:▒▒                            │  (TrackCount rows when known, else
│      │   ▒ ▒▒▒▒▒▒▒▒▒▒▒▒                 ▒:▒▒                            │  4), never a bare card — Skel.Region
│ [36] │ Racine carrée Live            2015 · Album                       │
│      │  Fetching 3 more releases…                                       │  the facet's own Partial state, not
└──────┴───────────────────────────────────────────────────────────────────┘  a button; DemandNextPage scroll-paces
```

Demand (§7 below): in scope 0 (library), every saved block's tracks edge + rows at Visible; in scope 1 (all
releases), additionally the three facets' first pages at Visible, `DiscoCard` for each listed release, and every
catalogue block's tracks edge + rows at **Prefetch**. The whole model is asked; the runner batches (no page-side
fetch windows) — page N+1 is scroll-paced from `OnVisibleRange`, the discography's existing rule.

### W30 - `Album.Pane`'s four readiness states — new 2026-09-18, closes W24 (§12)

Source: `library-rework-implementation.md` §2 W5, `AlbumPaneReadiness` (§0 #22).

```
 HEADER (identity not yet known)      ROWS (identity known, tracks loading)   FAILED (edge or row batch failed)
 ┌ [128] shimmer over the whole ─┐    ┌ [128] ALBUM · 2013               ┐    ┌ [128] ALBUM · 2013               ┐
 │       hero — short-lived,     │    │       racine carrée              │    │       racine carrée              │
 │       the navigator already   │    │       Stromae · 13 songs         │    │       Stromae · 13 songs         │
 │       asked for identity      │    │ (▶Play)(⤨)(♥)(⋯)   Open album ↗  │    │ (▶Play)(⤨)(♥)(⋯)   Open album ↗  │
 └────────────────────────────────┘    │  ▒ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒        ▒▒▒▒   │    │ Controls.Vacancy(Error, Compact)  │
                                        │  … × ShimmerRows (never 0)      │    │  "Couldn't load the songs."      │
 READY: the table, no shimmer, no      └───────────────────────────────────┘    │  [ Retry ] → RefreshEdge + a      │
 crossfade on the selection change                                              │    re-armed row batch            │
                                                                                 └───────────────────────────────────┘
```

The header paints from the navigator's own row facts (`Title`, `ImageId`, `Year`, `TrackCount`, `ArtistSlots` —
`AlbumFields.Identity`, already demanded), so only the ROWS state shimmers, never the whole pane — the mechanism
that closes W24's "no error state on the browse side" for good. **No `MinifiedAlbum` notice anywhere in this
state machine** (§0 #22).

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush token | material / elevation | source |
|---|---|---|---|---|---|---|---|
| page root | Grow 1, Dir 1 | — | — | — | none (inherits shell content card) | — | `LibraryPage.cs:239-249` |
| `NavPanel` (left column) | `Width = _leftW`, Shrink 0 | — | square (outer corners come from the shell card's clip) | — | `Tok.FillLayerDefault` (light `#80FFFFFF`, dark `#4C3A3A3A`) | flat, no elevation | `:364, :437-443` |
| `Pane` (every reading column) | Grow 1 / Basis `_midW` | — | square | — | `Tok.FillCardDefault` (light `#B3FFFFFF`, dark `#0DFFFFFF`) | flat | `:365` |
| `Grip` strip | 16 × Stretch | — | — | — | transparent | — | `:987-998`, `Splitter.cs:22` |
| grip seam | 1 × Stretch | centred in strip | — | — | `Prop.Of(() => Tok.StrokeCardDefault)` (light `#0F000000`, dark `#19000000`) | — | `:992-993` |
| grip thumb | 2 × Stretch | margin-Y 4 | 1 | — | `Tok.FillControlStrong` | opacity 0→1 on hover | `Splitter.cs:33-43, :160-171` |
| Toolbar (left, title arm) | — | `(12, 16, 12, 8)`, gap 8 | — | — | — | — | `:456-467` |
| Toolbar (left, no-title arm) | — | `(12, 12, 12, 8)`, gap 8 | — | — | — | — | `:458` |
| page title | 1 line | — | — | `WaveeType.PageHero` = `Ui.Title` **28 / 36 / 600** | `Tok.TextPrimary` | — | `:462`, `WaveeType.cs:128`, `Typography.cs:44` |
| sort/view pill | h 32, Shrink 0 | `(10, 0, 8, 0)`, gap 5 | `Radii.Control` 4 | label 14/20/600 | `Tok.TextSecondary`; `Interaction.Subtle` ramp | — | `LibrarySortView.cs:58-71` |
| pill sort glyph | 14 | — | — | `Icons.Sort` (`\uE8CB`) | `Tok.TextSecondary` | — | `LibrarySortView.cs:65` |
| pill direction chevron | 12 | — | — | `Icons.ChevronDown` (`\uE70D`) / `ChevronUp` (`\uE70E`) | `Tok.TextTertiary` | — | `:67` |
| pill divider | 1 × 16 | — | — | — | `Tok.StrokeDividerDefault` | — | `:68` |
| pill view glyph | 14 | — | — | `Icons.ViewGrid` (`\uF0E2`) if view ≥ 2 else `Icons.ViewList` (`\uE14C`) | `Tok.TextSecondary` | — | `:35, :69` |
| filter box | minH 32, grow 1, maxFill 9999 | — | `Radii.Control` 4 | AutoSuggestBox Standard chrome | stock | — | `LibraryPage.cs:475-477` |
| flyout body | MinW 230 | pad 4, gap 1 | — | — | — | `PopupChrome.Popup` = FlyoutPresenter acrylic + stroke + corners + shadow | `LibrarySortView.cs:52-53, :104-108` |
| flyout header | — | `(8, 4, 8, 2)` | — | `WaveeType.Eyebrow` **12 / 16 / 600 + 30/1000 em** | `Tok.TextTertiary` | — | `:152`, `WaveeType.cs:38, :54` |
| flyout sort row | h 32 | `(10, 0, 8, 0)`, gap 8 | 5 | 14 / weight 600 active, 400 idle | active `Tok.AccentTextPrimary`, idle `Tok.TextPrimary` | `Interaction.Subtle` | `:111-126` |
| flyout row chevron / check | 11 / 12 | — | — | `Icons.ChevronDown|Up`, `Icons.Check` (`\uE73E`) | `Tok.AccentTextPrimary` | — | `:122-123` |
| flyout divider | h 1 | margin 4 | — | — | `Tok.StrokeDividerDefault` | — | `:153` |
| view-toggle cell | 40 × 30, Grow 1 | gap 4, bank pad `(2,2,2,4)` | 5 | glyph 14 / 16 / 12 / 15 | ON `Tok.AccentDefault` + `Tok.TextOnAccentPrimary`; OFF `Tok.FillSubtleSecondary` + `Tok.TextSecondary`; hover `Tok.AccentSecondary` / `Tok.FillSubtleTertiary` | — | `:128-150` |
| size selector | — | — | — | `SelectorBar.Create(["S","M","L"], _size)` | stock | 4×3 pill, 167 ms | `:100`, `SelectorBar.cs:29-33` |
| nav list row | extent **40** (compact) / **60** | `(8, 0, 8, 0)`, gap 12 | — | title 14/20/600, subtitle 12/16 | `Tok.TextPrimary` / `Tok.TextSecondary` | `SelectorVisual.AccentPill` | `LibraryPage.cs:510, :901-914` |
| nav row cover | 40 × 40 | — | 20 (artist) / **5** (album, show) | — | `Surfaces.Artwork(...)` | — | `:905-906` |
| nav grid cell | min width **116 + 24·size** (88 + 16·size compact), gap 8 | card pad 16 (artist) / 8, gap 8 | artwork `Radii.Full` / **6** | title 12/16/600 | `Tok.TextPrimary` | `SelectorVisual.Border` | `:509, :918-924` |
| nav grid wrapper | — | `(8, 8, 8, 0)` | — | — | — | — | `:515` |
| navigator empty "…" | — | `(12, 20, 12, 20)` | — | `Caption` 12/16 `.Secondary()` | `Tok.TextSecondary` | — | `:492` |
| navigator no-match | — | `EmptyState.Compact` pad 24, gap 4 | — | `Ui.Subtitle` **20 / 28 / 600** | `Tok.TextPrimary` | — | `:491`, `EmptyState.cs:43-45, :68-72` |
| search `SelectableRow` | h 56 | `(8, 0, 8, 0)`, gap 12 | `Radii.Control` 4 | eyebrow 12/16/600, title 14/600, subtitle 12/16 | `Tok.TextSecondary` / `Tok.TextPrimary` / `Tok.TextSecondary` | rest transparent, hover `FillSubtleSecondary`, press `FillSubtleTertiary`, selected `FillSubtleSecondary` | `:825-856` |
| search row cover | 44 × 44 | — | `Radii.Full` (artist) / `Radii.Control` 4 | — | `Surfaces.Artwork` + `CoverSkeleton` override | — | `:850-852, :861-865` |
| `TrackHitRow` | h 44 | `(8, 0, 8, 0)`, gap 12 | 4 | title 13 / 600 | `Tok.TextPrimary` | `Interaction.Subtle` | `:867-891` |
| `TrackHitRow` cover | 36 × 36 | — | `Radii.Control` 4 | — | hidden when `AppearancePrefs.TrackArtworkHidden` | — | `:869-877` |
| highlight pill | — | `(3, 1, 3, 1)` | `Radii.Control` 4 | same size/weight as the run | `Tok.AccentSelectedTextBackground` plate, `Tok.TextOnAccentSelectedText` ink | — | `SearchHighlight.cs:42-54` |
| `FacetHeader` | — | `(12, 12, 12, 8)`, gap 4 | — | label `WaveeType.Eyebrow`, count 12/16/600 | `Tok.TextTertiary`; refining → `TextTertiary` × α 0.4 | brush cross-fade 83 ms | `LibraryPage.cs:775-790` |
| `SearchMessage` | — | `(12, 20, 12, 20)` | — | 14 / 20 | `Tok.TextTertiary` | — | `:792-796` |
| `SearchScroll` content | — | `(8, 4, 8, 92)`, gap 2 | — | — | — | — | `:761-771` |
| `ShimmerStack` | 6 rows | `(8, 4, 8, 4)`, gap 2 | — | — | `SkeletonStyle.Default.BarColor` = `Tok.FillSubtleSecondary` | pulse 1 → 0.5 → 1 @ 1000 ms | `:713, :749-759` |
| crumb bar | — | `(12, 8, 12, 8)` | — | crumb 14/20, chevron 12 | `Tok.TextPrimary`, hover `TextSecondary`, press `TextTertiary` | `Tok.FillLayerDefault` + 1-DIP `Tok.StrokeDividerDefault` under | `:288-297`, `BreadcrumbBar.cs:86-137` |
| detail hero | — | `(20, 20, 20, 12)`, gap 16, text gap 3 | — | — | — | — | `:1116-1144` |
| detail hero cover | 104 × 104, decode 256 | — | `Radii.Card` 8 | — | `Surfaces.Artwork` — **no elevation** (stroke-only content rule) | — | `:1128-1129` |
| detail hero eyebrow | — | — | — | `WaveeType.Eyebrow` 12/16/600 | `Tok.TextTertiary` | — | `:1133` |
| detail hero title | 2 lines, wrap | plate `(8, 2, 8, 2)`, margin `−8, −2, −8, −2` | `Radii.Control` 4 | **28 / 36 / 600** | `Tok.TextPrimary` → hover `Tok.AccentTextPrimary` | `Interaction.Subtle` plate, Cursor Hand, Role Button | `:1152-1170` |
| detail hero publisher | 1 line | — | — | 14 / 20 / 600 | `Tok.TextSecondary` | — | `:1138` |
| detail hero meta line | 1 line | — | — | 12 / 16 | `Tok.TextTertiary` | — | `:1140` |
| detail action row | — | `(20, 0, 20, 12)`, gap 12 | — | — | — | — | `:1174-1189` |
| Play CTA | minH 36 | `(18, 6, 18, 7)` | `Radii.Full` | 14, Bold | fill `Tok.AccentDefault`, ink picked by WCAG luminance (`WaveeCta`) | hover 1.04 / press 0.96 | `:1183`, `WaveeCta.cs:65, :87-105` |
| Shuffle Fab | 40 × 40 | — | `Radii.Circle(40)` = 20 | glyph 16 | `Tok.TextSecondary` | `Interaction.Subtle`, hover 1.07 / press 0.92 | `:1191-1196` |
| "View full album" | Small | — | stock | stock | `ButtonAppearance.Subtle` | — | `:1060-1067` |
| compact episode row | MinH 56 | pad 8, gap 12 | `Radii.Control` 4 | title 14/20/600 (2 lines), duration 12/16 | `Tok.TextPrimary` / `Tok.TextTertiary` | `Interaction.Subtle` | `:1198-1223` |
| episode play chip | 32 × 32 | — | `Radii.Circle(32)` = 16 | glyph 12 | `Tok.FillSubtleSecondary` plate, `Tok.TextSecondary` ink | — | `:1211-1212` |
| detail skeleton blocks | 104×104 / 80×12 / 200×22 / 140×12 / n×14 | pad 20, gap 16; bar column gap 8 | 8 / 4 | — | `Tok.FillCardDefault` | **no pulse, no reveal** | `:1225-1235` |
| discography toolbar | — | `(12, 12, 12, 8)`, gap 8; row gap 8 | — | — | — | — | `:1292-1305` |
| "Go to artist" | — | `(8, 4, 8, 4)`, gap 4 | `Radii.Control` 4 | 14 / 20 / 600 + glyph 14 | `Tok.AccentTextPrimary` → hover `AccentTextSecondary` → press `AccentTextTertiary`; fill `FillSubtleTransparent` → `FillSubtleSecondary` → `FillSubtleTertiary` | 83 ms brush, hover 1.04 / press 0.96, Role Hyperlink | `:1317-1336` |
| disco list row | extent **44** (compact) / **60** | `(8, 0, 8, 0)`, gap 12 | cover wrapper `Radii.ControlAll` 4, artwork **5** | title 14/20/600, subtitle 12/16 | `Tok.TextPrimary` / `Tok.TextSecondary` | `SelectorVisual.AccentPill` | `:1387, :1445-1457` |
| disco grid cell | min width **100 + 24·size** (84 + 16·size compact), gap 8 | card pad 4, gap 4 | artwork 6 | title 12/16/600, subtitle 12/16 | `Tok.TextPrimary` / `Tok.TextSecondary` | `SelectorVisual.Border` | `:1377, :1432-1442` |
| disco grid wrapper | — | `(12, 0, 12, 0)` | — | — | — | — | `:1375` |
| disco skeleton block | h 148 | pad 12, gap 8 | `Radii.Card` 8 | — | `Tok.FillCardDefault` | no pulse | `:1474-1478` |

**Every glyph on the surface** (verified against the generated `Icons.Glyphs.g.cs`; all Segoe Fluent Icons):

| where | constant | code | size | colour |
|---|---|---|---|---|
| sort pill, leading | `Icons.Sort` | `` | 14 | `Tok.TextSecondary` |
| sort pill / flyout row, direction | `Icons.ChevronDown` / `Icons.ChevronUp` | `` / `` | 12 (pill) / 11 (row) | `TextTertiary` / `AccentTextPrimary` |
| sort pill, trailing view glyph | `Icons.ViewGrid` (view ≥ 2) / `Icons.ViewList` | `` / `` | 14 | `Tok.TextSecondary` |
| flyout active-row tick | `Icons.Check` | `` | 12 | `Tok.AccentTextPrimary` |
| flyout view bank, 4 cells | `ViewList`, `ViewList`, `ViewGrid`, `ViewGrid` | as above | **14 / 16 / 12 / 15** | ON `TextOnAccentPrimary`, OFF `TextSecondary` |
| filter box (both toolbars) | `Icons.Search` | `` | AutoSuggestBox stock | stock |
| Play CTA | `Icons.Play` (WaveeCta's default glyph) | `` | stock | WCAG-picked on-fill ink |
| Shuffle fab | `Icons.Shuffle` | `` | 16 | `Tok.TextSecondary` |
| compact episode chip | `Icons.Play` | `` | 12 | `Tok.TextSecondary` |
| "Go to artist" trailing ↗ | `Icons.OpenInNewWindow` | `` | 14 | `Tok.AccentTextPrimary` |
| breadcrumb separator | `Icons.ChevronRightMed` | `` | 12 | stock crumb ramp |

**Loc keys that exist but never reach a pixel.** `LibrarySortPanel.ViewToggles` builds a `(Glyph, Size, Label)` tuple
per cell and resolves `library.view.compactList` / `.list` / `.compactGrid` / `.grid` (`LibrarySortView.cs:130-136`) —
then renders `defs[i].Glyph` and `defs[i].Size` only (`:146`). The four labels are computed and discarded: the bank has
**no text, no tooltip and no `AutomationName`**, so the four view modes are distinguishable by glyph alone, and to a
screen reader not at all. Recorded so 0.3 does not delete the keys *or* silently start drawing them.

**Persisted defaults** (`AppSettings.cs:382-395`): `leftw` = 280 (artists) / 340 (albums, podcasts) · `midw` = 440 ·
`sort` = 0 (Recents) · `desc` = false · `view` = 1 (List) · `size` = 1 (M) · `selected` = "" · `albumkey` = "" ·
`album.sort` = 0 · `album.desc` = false · `album.view` = **3 (Grid)** · `album.size` = 1.

---

## 4. Colour & material

**There is no cover-palette derivation on this surface.** The library is deliberately the app's accent-neutral browser:
the one CTA takes `Tok.AccentDefault` (the *system* accent), stated as an explicit exception in the code
(`LibraryPage.cs:1180-1183`). `WaveeCta` still picks the on-fill ink from the fill's WCAG luminance, so for
`Tok.AccentDefault` it resolves the accent-keyed ink (`Tok.OnAccent`), not the theme-keyed `TextOnAccentPrimary` the
stock ramp would use. **Do not** add artwork tinting to these pages in 0.3.

**The three coincident whites, and why a fill cannot fix them** (`LibraryPage.cs:349-363`, verbatim measurement from the
code comment): over the bare light ground the shell's plate lands ≈**249.6**, the content region's smoke ≈**252.3**, the
navigator's layer on top ≈**253.7**, and a card rung ≈**254.2**. The entire remaining light ladder is 2/255 — white-alpha
layering has run out of headroom. The fix is the WinUI card recipe (**a fill PLUS a stroke**): reading panes take the card
rung, the navigator keeps the layer rung, and the 1-DIP `Tok.StrokeCardDefault` seam inside the column grip does the
separating work the fills physically cannot. In dark the same structure reads on its own
(`FillLayerDefault` `#4C3A3A3A` vs `FillCardDefault` `#0DFFFFFF`), but the seam stays for consistency.

| surface | input → function | light | dark | where applied | transition |
|---|---|---|---|---|---|
| navigator ground | `Tok.FillLayerDefault` (`PaletteBuilder.cs:417` / `:306`) | `#80FFFFFF` over the content region | `#4C3A3A3A` | `NavPanel` (`:364`) and `CollapsedCrumbBar` (`:290`) | none (re-resolved on re-render) |
| reading pane ground | `Tok.FillCardDefault` (`PaletteBuilder.cs:412` / `:304`) | `#B3FFFFFF` (CardBackgroundFillColorDefault) | `#0DFFFFFF` | every `Pane` (`:365`) | none |
| column seam | `Tok.StrokeCardDefault` via `Prop.Of(() => …)` — a **live thunk**, so a theme swap re-resolves it without a re-render | `#0F000000` | `#19000000` | 1 DIP inside every grip (`:992-993`) | instant on theme change |
| crumb-bar rule | `Tok.StrokeDividerDefault` | `#0F000000` | `#15FFFFFF` | `:296`; also the sort pill's 1×16 divider and the flyout divider | none |
| selected row plate | `Tok.FillSubtleSecondary` | `#09000000` | `#0FFFFFFF` | AccentPill backplate; `SelectableRow` selected fill (`:845`) | 83 ms brush cross-fade |
| pressed row plate | `Tok.FillSubtleTertiary` | `#06000000` | `#0AFFFFFF` | AccentPill pressed; `SelectableRow` pressed (`:846`) | 83 ms |
| selection indicator | `Tok.AccentDefault` | system accent | `#60CDFF` | 3×16 pill, `SelectorVisuals.cs:196-199` | spring on reveal (§5) |
| grid selection ring | `Tok.AccentDefault` 3 DIP + 1 DIP `Tok.FillControlSolid` inner | — | — | `ItemContainer.Build` (`ItemContainer.cs:48-50`) | 167 ms fade |
| match highlight | `Tok.AccentSelectedTextBackground` | **`#0078D4`** (literal, not derived) | **`#0078D4`** — the same value in both themes | `SearchHighlight.cs:44`; `PaletteBuilder.cs:452` (light) / `:341` (dark) | none (the run re-renders) |
| match ink | `Tok.TextOnAccentSelectedText` | `#FFFFFF` | `#FFFFFF` | `SearchHighlight.cs:50` | none |
| "Go to artist" ink | `Tok.AccentTextPrimary` → `AccentTextSecondary` → `AccentTextTertiary` | WinUI HyperlinkButton ramp | `#A6D8FF` / `#A6D8FF` / `#76B9ED` | `:1329-1333` | 83 ms |
| hero title hover ink | `Tok.TextPrimary` → `Tok.AccentTextPrimary` | — | — | `:1157` | 83 ms (`BrushTransitionMs = WaveeMotion.Faster`) |
| facet count "refining" | `Tok.TextTertiary` with `A × 0.4` | — | — | `:786` | 83 ms |
| flyout material | `PopupChrome.Popup` — the one WinUI FlyoutPresenter acrylic (`Tok.AcrylicFlyout` = `AcrylicSpec.InAppDefault`), its stroke, corners and shadow | — | — | `LibrarySortView.cs:53, :103` | popup open/close |
| skeleton bar | `SkeletonStyle.Default.BarColor` = `Tok.FillSubtleSecondary` | — | — | derived shimmer + `CoverSkeleton` (`:861-865`) | pulse, see §5 |
| hand-rolled skeletons | `Tok.FillCardDefault` blocks | — | — | `LibraryDetailPane.Skeleton` (`:1231-1233`), `LibraryArtistPane.Skeleton` (`:1477`) | **none** — static blocks |

**Two themes, and only two.** `WaveeTheme.ResolvePalette() => Tok.NeutralPalette` (`Design/WaveeTheme.cs:15`), and
`PaletteBuilder.Build(Neutral)` short-circuits to `BuildWinUILight()` / `BuildDark(Neutral)`
(`PaletteBuilder.cs:44, :168-169`). The engine's Warm / Slate / accent-tinted seeds — whose light rungs are *opaque*
(`FillLayerDefault #FAF9F6`, `FillCardDefault #FCFBF9`, `StrokeCardDefault #DCDAD4`, `PaletteBuilder.cs:497-511`) and
would dissolve the three-coincident-whites problem entirely — are **not** shipped by Wavee. Every number in this
chapter's light column is `BuildWinUILight`. If 0.3 ever exposes a palette picker, this page's seam argument has to be
re-derived, not inherited.

**No gradients, no scrims, no washes, no Mica publication.** `ContentHost.PublishesShellMaterial` (`ContentHost.cs:184-186`)
does **not** list `albums`/`artists`/`podcasts`: the library pages publish no shell material, so the shell keeps its
default. `local` **does** (it is a detail route) — that is a real behavioural difference between the library pages and
Local Files, and it must survive.

---

## 5. Motion

All motion samples the engine frame clock (`FrameTime.NowQpc` / `FrameClock.PresentQpc`); nothing on this surface reads
`Environment.TickCount64`. The one `TickCount64` in the neighbourhood is the search-corpus TTL in
`StoreLibrarySource.cs:482` — a cache staleness clock, not an animation, and correct as-is.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| search result set changes (insert / remove / reorder) | `SelectableRow`, `TrackHitRow` | Position + Opacity | enter `Dy 3, opacity 0` → rest; exit → opacity 0 | **90 ms** enter/move, **70 ms** exit | `Easing.SmoothOut` | none | engine-wide no-op | `LibraryPage.cs:86-91, :838, :885` |
| navigator data change (add/remove/reorder) | nav rows | — | — | — | — | — | — | **none** — `ListOptions.Transition` is never set (`:500-507`); the remount key handles set changes |
| search column Pending → Ready | realized rows of all three columns | Opacity + TranslateY + Blur | opacity 0 → 1, `dy 8 → 0`, blur σ **3 → 0** | **500 ms** (`Expressive.VerySlow`) | engine `SoftRevealStaggered` | **40 ms per row** (`Expressive.Stagger`) | no-op | `SkeletonRegion.cs:169-186`, `LibraryPage.cs:742` |
| all three columns settle | group `_skelGroup` | — | the three reveals are released together by `SkelGroupCoordinator` | — | — | one window, not three | — | `LibraryPage.cs:52-53, :208, :744` |
| shimmer mounted | derived shimmer root | Opacity | 1 → **0.5** → 1, looping | **1000 ms** | engine pulse | — | no pulse | `SkeletonStyle.Default` (`SkeletonRegion.cs:34-38`) |
| shimmer → content | shimmer orphan (drawn UNDER the live tree) | Opacity | 1 → 0 | **250 ms** (`Expressive.Fast`) | — | — | the swap snaps | `Reconciler.cs:1443-1445` |
| refining (a newer answer coming) | `FacetHeader` count | Colour alpha | `TextTertiary` → `TextTertiary × 0.4` | **83 ms** (`WaveeMotion.Faster`) | brush cross-fade | — | instant | `LibraryPage.cs:785-786` |
| hover a nav/disco row | AccentPill backplate | Fill | transparent → `FillSubtleSecondary` (or selected `FillSubtleSecondary` → `FillSubtleTertiary`) | **83 ms** | engine HoverFade | — | instant | `SelectorVisuals.cs:216-218` |
| press a nav/disco row | AccentPill backplate | Fill | → `FillSubtleTertiary` (selected → `FillSubtleSecondary`) | **83 ms** | PressFade | — | instant | `SelectorVisuals.cs:218` |
| a row becomes selected | 3×16 accent pill | ScaleY + Opacity | `Sy 0, opacity 0` → rest | spring **(0.30, 0.85)** — the NavPill spring | spring | — | snaps | `SelectorVisuals.cs:205-207` |
| press a selected row | the accent pill | Scale | 1 → **0.625** (10/16) | press tier | — | — | none | `SelectorVisuals.cs:67, :201` |
| grid item selected / deselected | `ItemContainer` ring + inner stroke | Opacity / border | 0 → 3 DIP ring + 1 DIP inner | **167 ms** (`ControlFast`) | KeySpline 0,0,0,1 | — | snaps | `ItemContainer.cs:53` |
| hover the sort pill / flyout row / search row / episode row / track hit | plate | Fill | `Interaction.Subtle` ramp `FillSubtleTransparent → FillSubtleSecondary → FillSubtleTertiary` | **83 ms** | — | — | instant | `Interaction.cs:132-135` |
| hover / press "Go to artist" | box + label | Fill + Scale + ink | fill ramp; scale **1 → 1.04 / 0.96**; ink `AccentTextPrimary → Secondary → Tertiary` | 83 ms brush, `MotionTokenId.ControlFaster` for scale | — | — | scale dropped | `LibraryPage.cs:1322-1333`, `WaveeMotion.cs:43` |
| hover / press the Shuffle Fab | box | Scale | 1 → **1.07 / 0.92** (`ScaleEmphatic`) | ControlFaster | — | — | dropped | `:1194`, `WaveeMotion.cs:48` |
| hover / press Play CTA | capsule | Scale | 1 → **1.04 / 0.96** (`ScaleStandard`) | — | — | — | dropped | `WaveeCta.cs:102-104` |
| hover the detail hero title | title ink | Colour | `TextPrimary` → `AccentTextPrimary` | **83 ms** | — | — | instant | `:1157` |
| hover a grip | 2-DIP thumb | Opacity | 0 → 1 | `Motion.ControlFast` (**167 ms**) | `Easing.FluentDecelerate` | — | instant | `Splitter.cs:36, :164-170` |
| drag a grip | column width signal | Width | 1:1 with the pointer | none (direct) | — | — | unchanged | `Splitter.cs` (size writes are 1:1; layout transitions suppressed for the gesture) |
| drag-end on a grip | settings | — | one `onCommit` → `_settings.Set(LeftW|MidW)` | — | — | — | — | `LibraryPage.cs:233, :687, :969` |
| flyout open / close | popup | opacity + scale | stock `PopupChrome.Popup` | stock | stock | — | stock | `LibrarySortView.cs:49-54` |
| `SelectorBar` S/M/L pick | 3-DIP pill | ScaleX + Opacity | `ScaleX 1/4, opacity 0` → 16 px | **167 ms** | KeySpline 0,0,0,1 | — | snaps | `SelectorBar.cs:29-33, :53-57` |
| multi-select checkbox reveal (unused here — Single mode) | — | — | — | 333 ms | `FluentDecelerate` | — | — | `SelectorVisuals.cs:64` — recorded so nobody re-enables it |
| programmatic selection move | viewport scroll | offset | jump | **unanimated on purpose** (`alignmentRatio` NaN, minimal `BringIntoViewOptions`) | — | — | — | `LibraryPage.cs:530-541, :1423-1429` |
| breadcrumb click / depth change | body subtree | — | plain swap, no transition | — | — | — | — | `:284, :294` |
| breakpoint cross | layout | — | plain swap (collapsed ⇄ wide), no morph | — | — | — | — | `:221-235` |
| a file drag enters / leaves the window while a library page is up (W27) | the shell's drop-hint pill | Opacity | 0 → 1 / 1 → 0 | **a bound `Prop.Of` with no motion spec declared** — do not invent a fade | — | — | nothing to drop | `WaveeShell.cs:1418-1431`; the layer is `HitTestPassThrough`, so the cue is compositor-only and **the library page under it is not re-rendered by the hover** |
| page enter / exit | the whole page | — | owned by `ContentHost` / `PageNavMotion` | — | — | — | — | `18-shell-frame.md` |
| toggle "hide track artwork" in Settings | every `TrackHitRow` in the Songs column | Position + Opacity | full `SearchRowChange` enter (`Dy 3`, opacity 0 → rest) | **90 ms** | `Easing.SmoothOut` | none | no-op | the row `Key` embeds `":art=" + showArtwork` (`:885`), so the whole column remounts and re-enters — `AppearancePrefs.Epoch` is the edge (`AppearancePrefs.cs:9-18`) |
| a shimmer column that realizes **0** rows | the real root | Opacity + TranslateY + Blur | plain `SoftReveal` (no stagger) | 500 ms | `SoftReveal` | — | no-op | `SkeletonRegion.cs:179` — `n == 0 ⇒ anim.SoftReveal(realRoot)`; an empty facet still reveals, it just does not stagger |
| shimmer exit under `SkelReveal.None` | shimmer orphan | Opacity | 1 → 0 | floored at `Expressive.Slow` | — | — | snaps | `SkeletonRegion.cs:31-33` — not this page's path (it uses `StaggerRows`), recorded so nobody swaps the reveal and silently changes the dissolve |
| collapsed ⇄ wide, depth 0 ⇄ 1 ⇄ 2 | grips, page toolbar | — | the grips and the page `Toolbar()` **disappear/appear with no transition** (they are simply not in the collapsed/deep tree) | — | — | — | — | `:233, :274-275, :687, :969` — see W25 |
| hover a breadcrumb crumb | crumb ink | Colour | `TextPrimary` → `TextSecondary` → `TextTertiary` | eased hover/press progress (WinUI uses 0 ms discrete keyframes — a stated engine deviation) | — | — | instant | `BreadcrumbBar.cs:96-103`. **The last/current crumb gets none of this** — no `HoverColor`, no `PressedColor`, no `OnClick` |

**Explicitly absent, and that is correct:** no equalizer, no ticker, no countdown, no progress bar, no scroll-linked
effect, no shared-element morph and no now-playing treatment anywhere on the library chrome. The now-playing equalizer
and per-row heart appear only inside the embedded `TrackList` (`04-detail-track-table.md`).

---

## 6. Interaction

### Navigator (left column) and discography grid

| input | result | source |
|---|---|---|
| click a row / card | `ItemsView` selects → `OnChange` → `OnNavSel` → `Select(it)`: `_selectedKey = RouteKey`; **artists: `_albumKey = ""`** (a new artist resets the 3rd column). Collapsed: also `_depth = 1`. | `:367-371, :520-528` |
| click a discography tile | `Pick(i)`: `_albumKey = "album:" + uri`; `onDrill?.Invoke()` → collapsed `_depth = 2` | `:1366, :252` |
| double-click | **nothing extra** — `IsItemInvokedEnabled` is never set, so `OnInvoked` never fires | `:500-507`, `ItemsView.cs:382-385` |
| right-click | **no context menu anywhere in `Features/Library`** (verified: zero `ContextMenu` / `Menus.` references). The event falls through to the shell. | — |
| Up / Down | move current ±1 in a stack layout (Left/Right no-op) | `ItemsView.cs:387-389` |
| Left / Right, Up / Down in a grid | ±1 and ±columns | `ItemsView.cs:387-389` |
| Home / End | bring item 0 / n−1 into view edge-aligned, focus it | `ItemsView.cs:390-391` |
| PageUp / PageDown | railed three-phase page navigation | `ItemsView.cs:391-392` |
| printable characters | typeahead: accumulate (**1000 ms** reset), jump to the next prefix match from current+1, wrapping | `ItemsView.cs:396-397, :403` |
| Tab | **one roving stop** (`TabNavigation="Once"`); tab-in with no current lands on the selected item | `ItemsView.cs:393-395` |
| Space | **selects** the current item without invoking (WinUI's `CanRaiseItemInvoked` split) — so it runs the same `OnNavSel` / `Pick` path a click does | `ItemsView.cs:381-383` |
| Enter | would invoke, but `IsItemInvokedEnabled` is never set ⇒ nothing | `ItemsView.cs:381-383` |
| Ctrl+A | no-op (Single mode) | `ItemsView.cs:386` |
| any keyboard move | runs the selector's `OnFocusedAction` — in Single mode **selection follows focus**, so Up/Down/Home/End/PageDown/typeahead each re-skin the right pane(s) and refire the detail load | `ItemsView.cs:387-392` |
| a filter that matches nothing (title filter only) | `SyncNav` clears `_selectedKey`, `_albumKey` (artists) and deselects ⇒ the right pane falls to the W9 placeholder; clearing the filter re-adopts **row 0**, not the previous selection | `:546-556, :563` |
| a persisted selection that is no longer in the set (unfollowed, not yet streamed) | silently re-adopted as row 0 | `:560-563` |
| drag a row/card ≥ 2× the mouse drag box | starts a `WaveeDragKinds.Resource` drag with `WaveeResourceDragPayload.ForEntity(KindOfRoute(routeKey), uri, title, cover, acts)` | `:926-940, :1460-1464` |
| drop onto the page | **nothing** — Single selection, no reorder, no `Insertion` options | `:926-928` |
| drop a **FILE** onto the page (any column, either grip) | the page accepts nothing, so the shell backdrop's `Files` target takes it: `LocalFileActions.PlayDropped` → `ClassifyDrop` → play the first audio file, else self-attach + play the first `.mp4`, else the "can't be played" error toast. The library selection, scroll offset and columns are untouched; while the drag is over the window the shell's centred drop-hint pill is up — W27. | `WaveeShell.cs:1351-1359, :1385`, `LocalFileActions.cs:58-78`, `LocalPlayables.cs:155-163` |

`KindOfRoute` derives the drag kind from the row's own route key, never from the page kind, because the search facets
mix kinds (`:936-940`).

### Toolbar

| input | result | source |
|---|---|---|
| type in the filter box | **two** reads of one box: the browse title-filter re-narrows on the **raw** text every keystroke (`Shape`, `:409-410`); the full-text search rides a **180 ms trailing debounce** (`:74, :160`) | `:155-167` |
| clear the filter box | browse panes return on the **same frame** (the mode flips on raw text, not the debounced one) | `:164-166` |
| click the sort pill | toggles the flyout; a second click on an open flyout closes it | `LibrarySortView.cs:45-56` |
| click a *different* sort row | `sort = key`, `desc = false` | `LibrarySortView.cs:118` |
| click the *active* sort row | `desc = !desc` (flyout stays open) | `LibrarySortView.cs:118` |
| click a view cell | `view = idx` (0..3) | `LibrarySortView.cs:145` |
| click S / M / L | `size` = 0/1/2 — the bank appears only when `view ≥ 2` | `LibrarySortView.cs:97-101` |
| light-dismiss / Esc | closes the flyout (`DismissBehavior.LightDismiss`, `FocusTrap: true`) | `LibrarySortView.cs:53` |
| **any of the above while search mode is up** | the pill, the chevron and the bank all update and persist — and **nothing on screen changes**. `_sort`/`_desc`/`_view`/`_size` feed `Shape` → `ListBody` only; the search columns render fixed-height `SelectableRow` / `TrackHitRow`. See §0 #18 | `:407-418, :442, :825-891` |
| type in the **discography** filter | title-contains over `Artist.TopAlbums`, instant, **never** full-text search — `_aFilter` has no debounce and no `SearchLib` path | `:1340-1351` |

### Search results

| input | result | source |
|---|---|---|
| click an artist hit | `_sArtist = uri; _sAlbum = ""`, then `LibrarySelectionCommit.ForArtist` → `_selectedKey = "artist:"+uri`, `_albumKey = ""`, **clear the filter**, collapsed `_depth = 1` | `:616-620`, `LibrarySelectionCommit.cs:41-42` |
| click an album hit, albums view | `_selectedKey = "album:"+uri`, `_albumKey` untouched, clear filter, collapsed `_depth = 1` | `LibrarySelectionCommit.cs:43-44` |
| click an album hit, artists view | `_albumKey = "album:"+uri` **and** `_selectedKey = "artist:"+ownerArtistUri` (the owning artist is committed because a hit is reachable without ever clicking its artist row), clear filter, collapsed `_depth = 2` | `LibrarySelectionCommit.cs:45-46` |
| click a track hit | `PlayTrack(albumUri, t.AlbumIndex)` → `svc.Player.PlayAsync(albumUri, max(0, index))` — plays in place, no navigation | `:637-640, :888` |
| results arrive | first artist auto-selects; then the first album under it auto-selects — so results appear immediately without a click | `:583-605, :213-214` |
| results go empty | the search selection clears; the **browse** selection is untouched, so clearing the query restores exactly what the user was browsing | `:588-594` |

### Detail pane

| input | result | source |
|---|---|---|
| click the hero title | stash the full `DetailModel` as the nav preview, then `go(routeKey, title)` — the destination paints from real data on frame one. **No `ContextUri` ⇒ plain text, never a dead click target.** | `:1152-1170` |
| click a billed artist name | `TrackRow.ArtistLinks(...)` → artist page | `:1139` |
| click "View full album" | same preview + nav; albums only, never for a show | `:1060-1067` |
| click Play | `svc.Player.PlayAsync(uri, 0)` | `:1033` |
| click Shuffle | `SetShuffleAsync(true)` then `PlayAsync(uri, 0)` | `:1034` |
| click Save / Follow | `SaveButton` / `FollowButton` (`02-cards-and-controls.md`); `LibraryBridge.SetSaved` announces "Saved" / "Removed from library" to the screen reader, throttled | `:1185`, `LibraryBridge.cs:284-293` |
| click a compact episode row | `PlayAsync(showUri, index)` | `:1041, :1208` |
| track rows | the full embedded `TrackList` cell: number↔play/pause on hover, now-playing equalizer, per-row heart, multi-select, tier-fitted columns (`04-detail-track-table.md`). The embedded list has **no toolbar** and **never** offers the BPM·Key column. | `:1036-1042, :1101-1109` |

### Breadcrumb (collapsed only)

Crumbs are `Role = AutomationRole.Button`, every crumb a `TabStop`; Left/Right move focus crumb-to-crumb without
navigating; clicking crumb *i* sets `_depth = i` (`:294`, `BreadcrumbBar.cs:58-137`).

- **The last (current) crumb is inert**: `isLast` suppresses `OnClick` *and* the hover/pressed ink ramp, so it paints
  flat `Tok.TextPrimary` in every state (`BreadcrumbBar.cs:96-106`). It is still focusable and still a `TabStop`.
- Every crumb shapes at **FontWeight Normal** — the current one is not bolder, only non-interactive
  (`BreadcrumbBar.cs:85-88`).
- A crumb whose name has not loaded is the literal `"…"` (`LibraryPage.cs:346`), and in search mode the names come
  from `FindArtist` / `FindAlbum` over the hit groups instead of the loadables (`:263-268`).

### Splitters

Cursor `SizeWE` over the whole 16-DIP strip; eager pointer capture so the gesture survives leaving the strip; clamped to
`[min, max]`; one `onCommit` at drag-end writes the width to settings. No keyboard resize.

### Tooltips and accessibility names

- **No tooltip is declared anywhere in `Features/Library`.** Any tooltip a user sees comes from a shared control
  (`Button`, `IconButton`, `AutoSuggestBox`). UNVERIFIED which of those self-tooltip; do not add new ones without a
  decision.
- Roles: `ItemsView` containers → `AutomationRole.Button`; hero title plate → `Button`; "Go to artist" →
  `AutomationRole.Hyperlink`, `Focusable`, `Cursor.Hand`; breadcrumb crumbs → `Button`.
- The library page declares **no** `AutomationName` of its own; names come from the rows' text content. This is a real
  gap, recorded honestly — 0.3 may improve it, but not silently.

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 data source | 0.3 read | readiness predicate (skeleton until true) |
|---|---|---|---|
| page title | `ShellNav.Dest(kind).Title` (loc `nav.albums` / `nav.artists` / `nav.podcasts`) | unchanged — a route constant | always ready |
| navigator rows (albums) | `LibraryStore.EnsureAlbums()` + `LibraryStore.Albums` (`LibraryStore.cs:33, :75`) → `Album` records | `Edges.SavedAlbums.Targets(User.Me.Slot)` (plan §4.3/§4.14) | `Edges.SavedAlbums.State[me] == 2` (complete). `State == 0` ⇒ the "…" caption, **not** an empty state |
| navigator rows (artists) | `EnsureArtists` / `Artists` | `Edges.FollowedArtists.Targets(User.Me.Slot)` | same |
| navigator rows (podcasts) | `EnsureShows` / `Shows` | `Edges.SavedShows.Targets(User.Me.Slot)` | same |
| row title | `Album.Name` / `Artist.Name` / `Show.Name` | `Album.TitleId` / `Artist.NameId` / `Show.TitleId` | `Knows(AlbumFields.Title)` per row; the row **shimmers its own title leaf** (`Skel.Pending`) rather than holding the whole list back |
| row subtitle | album → `Artists[0].Name`; artist → literal loc `search.typeArtist`; show → `Publisher` | album → `Edges.AlbumArtists.Targets(slot)[0]` → `Artist.NameId`; artist → the same loc constant; show → **`Show.PublisherId`** (DATA GAP) | `Knows(Artists)` / n-a / `Knows(Publisher)` |
| row cover | `Album.Cover` / `Artist.Image` / `Show.Cover` (`Image` record) | `Album.ImageId` (StringId) etc. | `Knows(Image)`; the cover slot shimmers via `CoverSkeleton` |
| cover prefetch | `PrefetchImage(url, 64|168|256)` per row on every render (`:148-150`) | identical — the engine `ImageCache` + `MemoryGovernor` bound residency | not a readiness input |
| sort: Recents | `svc.PlayLog.Recency` — `IReadOnlyDictionary<string,long>`, uri → last-played unix ms (`PlayLogStore`, `play-recency.json`) | **DATA GAP** — see below | order is recomputed on `svc.PlayLog.Version` bumps (`:141`) |
| sort: Recently added | source order (= `StoreLibrarySource.JoinSet`, added-desc) | `LibraryEdge.AddedAt` (plan §4.3) — **or** keep edge insertion order, which the plan already defines as added-desc | ready with the edge |
| sort: Alphabetical / Creator | `Title` / `Subtitle` (case-insensitive) | `Entities.Strings.Resolve(TitleId)` — note this is the **only** place the library resolves a StringId to a string outside paint; cache the ordinal keys | `Knows(Title)` / `Knows(Artists|Publisher)` |
| sort: Release date | `Album.Year` (albums + discography only) | **`Album.Year` column — DATA GAP** (the plan shows `Year` on `Track`, never on `Album`) | `Knows(AlbumFields.Year)` |
| grid/list card subtitle "2007 · ALBUM" | `Album.Year` + `AlbumKind` → loc `detail.badge.*` | `Album.Year` + **`Album.Kind` byte column — DATA GAP** | both known |
| selected item restore | `LibraryStateKeys.Selected(kind)` (a route-key string) | unchanged: `Platform.Settings`, a uri string; resolve to a handle via `Entities.Album(EntityUri.Parse(...))` | the handle always exists (factories allocate empty rows); the pane skeletons until `Knows(Identity)` |
| detail pane model | `DetailPage.LoadAsync(svc, kind, id, ct)` → `DetailModel` via `UseResource` keyed on the selection | `Signal<Album>` + `Entities.Ensure(album, AlbumFields.All)` + `Entities.EnsureEdges(album, EdgeKind.AlbumTracks)` + `Entities.EnsureRows(album.TrackSlots, TrackFields.Row)` — **one 300-uri batch, no visible-window fetching** | `a.Knows(AlbumFields.Identity)` **and** `Edges.AlbumTracks.State[slot] == 2`. Partial rows popping in is a regression |
| detail pane eyebrow | `DetailModel.BadgeType` | `Album.Kind` → the same loc badge | `Knows(Kind)` |
| detail pane meta line | `DetailModel.MetaLine` (year · n songs · duration) | derived **on the model at commit** (`Album.MetaLineId`, a StringId column — P11) — never a per-frame concat | `Knows(Identity)` + tracks complete |
| detail notice strip | `DetailModel.Notice` — a model-derived fact, never a UI probe | **`Album.Notice` (byte) — DATA GAP**; the DetailNotice pipeline is the owner's stated rule | part of the Identity group |
| discography grid | `Artist.TopAlbums` (`svc.Library.GetArtistAsync`) | `Edges.ArtistReleases.Targets(artistSlot)` + `DiscographyEdge.Kind` | `Edges.ArtistReleases.State == 2` |
| 3rd-column tracks | `LoadDetail(svc, _albumKey)` — a second `UseResource` keyed on `_albumKey` | `Signal<Album>` driven by `_albumKey`; same Ensure batch as the detail pane | same as the detail pane |
| search results | `svc.Library.SearchLibraryAsync(query, scope, ct)` → `LibrarySearchResults` (grouped artist ▸ album ▸ track with `MatchStart/MatchLen/MatchReason`) | **DATA GAP — no equivalent in the plan** | `Loadable.State` + the widened `awaiting` gate (`:730-745`) |
| search "why" caption | `MatchReason.Kind/Term` from the matcher | part of the same gap | `ShouldExplain` |
| facet counts | `sr.Artists.Count` / `albums.Count` / `tracks.Count`; `-1` while shimmering | hit-list lengths | count hidden entirely when `< 0` |
| Save / Follow state | `LibraryBridge.IsSaved(uri)` — a per-uri `Signal<bool>` | `User.Me.Saves(album)` via `Edges.SavedAlbums.Contains` (plan §4.14 reverse index) | ready with the edge |
| rail-open floor | `ShellUi.RailOpen` | `Shell.RailOpen` signal | reactive |
| collapsed / depth | self-measured `OnBoundsChanged` | identical | reactive |

**Readiness has no failure arm on the browse side.** Both browse panes gate on `Ready` alone — `LibraryDetailPane`
(`:1030`) and `LibraryArtistPane` (`:1281`) — and a `Failed` (or a `Ready`-with-empty-title) load renders the skeleton
indefinitely. `ErrorState` is reachable on exactly one path, the search columns' `SkelRegionEl.OnFailed` (`:741`). In
0.3 terms that means: `a.Knows(AlbumFields.Identity)` being false must not be the *only* input — the pane needs a
third state (`Failed`) that 0.2.9 never gave it. See W24; changing this is a decision, not a port.

**Closed 2026-09-18 (§0 #22, §12).** `AlbumPaneReadiness.Of(knowsIdentity, tracksEdgeState, edgeFailed, anyUnnamed,
rowsFailed)` — CORE, `Entities/User.cs` — gives the pane a real **fourth** state, not just a third:

| visual element | 0.3 read | readiness predicate |
|---|---|---|
| `Album.Pane` readiness | `AlbumPaneReadiness.Of(...)` | `Header` (identity unknown) → `Rows` (tracks edge Unknown/Partial, or Complete with an unnamed row and no failure) → `Failed` (the tracks edge failed, or the row batch failed) → `Ready` (Complete, every row named) |
| `Album.Pane`'s shimmer count | `AlbumPaneReadiness.ShimmerRows(knowsCount, trackCount, listed)` | `TrackCount` when known, else the listed edge length, else 6 — never 0 |

`Album.Notice` is **no longer consumed on this surface** — the `MinifiedAlbum` notice's only reader here is deleted
(§0 #22); `Detail.Identity.For` and the DATA GAP 6 `Album.Notice` column themselves are untouched (still read
elsewhere).

**`Artist.Reader`'s demand rules, 2026-09-18 (replaces the discography-pane row above for the artists view).**
Scope 0 (*in your library*): every saved block's tracks edge + `TrackFields.Row` at **Visible**. Scope 1 (*all
releases*): additionally the three facet edges' first pages at Visible (`DemandDiscography`), `AlbumFields.DiscoCard`
for every listed release, and every catalogue block's tracks edge + rows at **Prefetch**; facet pages beyond the
first are scroll-paced (`DemandNextPage` from `OnVisibleRange`). The whole model is asked per the
`no-page-side-fetch-windows` rule — the reader keeps no visible-window fetch of its own; the runner batches.

**One more search shape the model has to carry.** `LibraryAlbumGroup.Tracks` is "all tracks when the album/artist name
matched; only the matching tracks otherwise" (`LibrarySearch.cs:35-38`), and `MatchStart/MatchLen` are per-level with
`MatchLen == 0` meaning "this level's own name did not match" → no pill. So the hit shape is not "matched rows" but
"rows reachable from the match, each with an optional span". DATA GAP 5's `LibraryHits` struct must keep that
distinction or the second and third columns render empty (W26).

### DATA GAPS

Everything this surface shows that the 0.3 plan's model does **not** hold. Each names the 0.2.9 source and a proposed
column or edge.

1. **Play recency (the "Recents" sort).** 0.2.9: `PlayLogStore.Recency` (uri → last-played unix ms), fed by
   `PlaybackBridge.PushState` and `RecentsPage.Adopt`, folded from the 200-entry ring, capped at 4096 with a trim to
   3840, persisted to `play-recency.json` (`library-artist-jump-and-recents-implementation.md` §F.0). The plan has
   `Table.Touched` ("seconds since app epoch, P7") but that is a **cache** touch — conflating the two would make an
   ordering depend on cache residency, which is exactly root-cause #E.
   **Proposal:** `Column<int> LastPlayedAt` (seconds since app epoch, 0 = never) on `AlbumTable`, `ArtistTable`,
   `ShowTable` and `TrackTable`, written by a `Playback.StampRecency(track, context, album, artistSlots)` fold on every
   track boundary, plus one `Signal<uint> RecencyVersion` so the library page re-orders in place. Persist as today.
2. **`Album.Year`.** 0.2.9: `Album.Year` (`Models.cs` record; decoded from `getAlbum`/`TrackV4`). The plan shows `Year`
   only on `Track` (§4.2). Needed for the Release-date sort **and** the "2007 · ALBUM" subtitle.
   **Proposal:** `Column<ushort> Year` on `AlbumTable`, in the `AlbumFields.Identity` group.
3. **`Album.Kind`** (Album / Single / EP / Compilation). 0.2.9: `AlbumKind` on the `Album` record; rendered through loc
   `detail.badge.{album,single,ep,compilation}` in three places (`:893-899, :1466-1472`, the hero eyebrow).
   **Proposal:** `Column<byte> Kind` on `AlbumTable`, Identity group. (`DiscographyEdge.Kind` in the plan is the *relation*
   kind — album vs appears-on — and is not a substitute.)
4. **`Show.Publisher`.** 0.2.9: `Show.Publisher`, the podcasts navigator's entire subtitle and the detail pane's
   publisher line. **Proposal:** `Column<StringId> Publisher` on `ShowTable`, Identity group.
5. **Library full-text search.** 0.2.9: `LibrarySearchIndex.Run` over a cached `LibrarySearchCorpus`
   (`StoreLibrarySource.cs:444-495`), producing `LibrarySearchResults` — a hierarchy of
   `LibraryArtistGroup ▸ LibraryAlbumGroup ▸ LibraryTrackHit`, each carrying `(MatchStart, MatchLen)` **and** a
   `MatchReason(Kind, Term)` attribution. The plan's `Search.cs` / `Edges.SearchResult` is *catalog* search; nothing in it
   carries match spans or a per-hit reason.
   **Proposal:** a CORE `User.SearchLibrary(Scope, ReadOnlySpan<char> query, LibrarySearchScope)` in `User.cs` returning a
   pooled `LibraryHits` struct — `(int slot, ushort matchStart, ushort matchLen, byte reasonKind, StringId reasonTerm)`
   triples per level — plus a `Signal<uint>` the page keys its skeleton off. The 180 ms debounce, the
   keep-previous-data behaviour and the 30 s corpus TTL are page/host concerns and port as-is.
6. **`Album.Notice` / `Show.Notice`** (the `DetailNoticeBar` fact, e.g. "the rows below are still the minified gid-only
   view"). 0.2.9: `DetailModel.Notice`, derived by the loader. **Proposal:** `Column<byte> Notice` on `AlbumTable` /
   `ShowTable`, set at commit. **The UI must never probe for this** (`derived-facts-live-on-the-model`).
7. **`Album.MetaLineId`.** 0.2.9: `DetailModel.MetaLine` (a formatted "2007 · 10 songs, 42 min"). **Proposal:** a
   `Column<StringId> MetaLine` computed at commit (the plan's own P11 rule for `Track.ArtistLineId`), never a per-frame
   concat.
8. **Per-kind persisted page state.** 0.2.9: `LibraryStateKeys` — 12 runtime-built `SettingKey`s per kind
   (`AppSettings.cs:380-396`). The plan's `Platform.cs` must keep **exactly these key strings and their int codes**
   (`LibraryNavSort` 0..4 and the view codes 0..3 are persisted; `LibraryNavOrder.cs:6-8` says "never renumber").
9. **Scroll memory.** 0.2.9: `ScrollOptions.ScrollKey = "lib:nav:"+kind` and `"lib:disco:"+artistUri`. Engine-side, no
   model change — but it is load-bearing for #E and must be carried over verbatim.
10. **`Artist` display kind literal.** The artists navigator's subtitle is the loc constant `search.typeArtist`
    ("Artist"), not data (`:396`). Record it so nobody "fixes" it into a follower count.

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `LibraryNavOrder` (+ `LibraryNavSort`, `LibraryNavFacts`) | `Features/Library/LibraryNavOrder.cs` (104) | The **one comparator set** behind every library list: Recents (played block newest-first, then never-played in source order, `desc` flipping inside blocks), Recently added (source order), Alphabetical, Creator, Release date (year desc, unknown years sink as a block) — always a **total** order with the source index as last tie-break. Also `OrderKey` — **`rows.Length + ":" + FNV-1a-hex16` over the uris in order** (the count prefix is part of the key, `LibraryNavOrder.cs:78-83`) — and `FactsKey` (bare FNV-1a hex16 over uri+title+subtitle+coverUrl, `:87-96`), the two halves of the `ItemsView` remount key. Note the `sign` multiplies the **tie-breaks** too, so `desc` reverses equal-key runs as well as the primary order (`:46, :52, :59`). | `src/apps/Wavee.Tests/LibraryNavOrderTests.cs` (198) | **CORE section of `Entities/User.cs`** — it is a library-ordering rule over edge targets. Keep FNV-1a (stable across runs so a test can pin it), keep the separator byte `0x1F`. |
| `LibrarySelectionCommit` (+ `LibrarySelectKind`) | `Features/Library/LibrarySelectionCommit.cs` (54) | Search **select-in-place**: given the hit kind, `artistsView`, `collapsed` and the uris, which of `_selectedKey` / `_albumKey` / `ClearFilter` / `_depth` change. A `null` field means "leave that signal alone". | `src/apps/Wavee.Tests/LibrarySelectionCommitTests.cs` (84) | **CORE section of `Entities/User.cs`** |
| `LibraryLayoutBreakpoints` | `Features/Library/LibraryLayoutBreakpoints.cs` (18) | `CollapseBelow = 640`, `Hysteresis = 24`; `Collapsed(w, wasCollapsed)` with `w <= 0 ⇒ keep previous`. | `src/apps/Wavee.Tests/LibraryLayoutBreakpointTests.cs` (32) | **CORE section of `Entities/User.cs`** |
| `LibrarySortView.SortLabel(int)` / `.ViewGlyph(int)` | `Features/Library/LibrarySortView.cs:27-35` | int code → loc key; view code → grid/list glyph. Pure, no engine. | (none today) | **`Entities/User.UI.cs`**, as static helpers — and give them a test, since the int codes are persisted. **NOT library-private:** the sidebar's Library V3 deliberately shares this numbering and these loc keys for codes 0–3 and clamps its own with `LibraryV3Metrics.NormalizeSort` (`Features/Sidebar/Modes/LibraryV3/LibraryV3Metrics.cs:105, :133-141`), and `V3SortViewFlyout.cs` is a *copy* of the pill + panel, not a reuse. Renumbering, renaming a key, or moving the labels out of reach of the sidebar breaks 25-sidebar.md. |
| `LibrarySearchIndex` / `LibrarySearchCorpus` / `MatchReason.ShouldExplain` | `Backend/Library/*` + `Wavee.Core/Library/LibrarySearch.cs:25-27` | The match/rank/group walk and the honesty rule (an unattributable reason renders nothing). | `src/apps/Wavee.Tests/LibrarySearchTests.cs` (142) | **CORE section of `Entities/User.cs`** (see DATA GAP 5) |
| `PlayRecency` / `RecentsRecency` | `App/PlayRecency.cs`, `Wavee.Core` | uri stamp merge (max-merge, cap 4096, trim to 3840 every 256 appends) feeding the Recents order. | `src/apps/Wavee.Tests/RecentsRecencyTests.cs` (117) | **CORE section of `Playback/Playback.cs`** (it is a playback fold), read by `User.cs` |
| `LibraryLetters` — **new 2026-09-18** | `library-rework-implementation.md` §5.1 | Letter groups over an alphabetically-sorted navigator: `Of(title)` (leading punctuation/a leading "the " skipped, non-A–Z folds to `#`), `Build` (the flat header/row index space + prefix-sum offsets), `StickyLetterAt`, `Key()` (the remount identity). | `src/apps/Wavee.Tests/LibraryLettersTests.cs` | **CORE section of `Entities/User.cs`** |
| `LibraryWordRail` (+ `LibraryNavSort.Albums = 5`) — **new 2026-09-18** | `library-rework-implementation.md` §5.1 | Which sort words each kind's rail shows, in rail order, as persisted codes; `Clamp` (a code the rail doesn't offer falls to `Recents`); the rail-word loc key. Codes never renumber, only append. | `src/apps/Wavee.Tests/LibraryWordRailTests.cs` | **CORE section of `Entities/User.cs`** |
| `AlbumPaneReadiness` — **new 2026-09-18** | `library-rework-implementation.md` §5.1 | The pane's four-state gate (`Header`/`Rows`/`Failed`/`Ready`) and its counted-shimmer rule (§7). | `src/apps/Wavee.Tests/AlbumPaneReadinessTests.cs` | **CORE section of `Entities/User.cs`** |
| `Artist.ReaderShape` — **new 2026-09-18** | `library-rework-implementation.md` §5.6 | The reader's block order and extents: library-first then catalogue, the three sorts (newest/oldest/a–z, unknown years sinking), union-dedup against saved, `ExtentOf` arithmetic, `OrderKey` stability. | `src/apps/Wavee.Tests/ArtistReaderShapeTests.cs` | `Entities/Artist.Reader.cs` (CORE half, owner N) |
| `Table.Failed` / `Fetch.MarkFailed` — **new 2026-09-18** | `library-rework-implementation.md` §5.0, §8 | A per-row fetch failure did not exist before this rework (`Table.Known` only; `Fetch.Failed` un-asks). `Table.Failed` is a `Column<uint>` cleared both by the planner and in `Applied`, read by `AlbumPaneReadiness.Of`'s `rowsFailed` input. | no dedicated test file — covered by `AlbumPaneReadinessTests`' `Failed` cases | `Entities/Entities.cs` |

Do **not** re-derive any of these. `LibraryNavOrderTests` pins exact permutations (e.g. source A B C D E with plays
A@100 C@300 E@200 ⇒ `[2,4,0,1,3]`); `LibraryLayoutBreakpointTests` pins 639 collapse / 640 not / 663 stay / 664 leave.

---

## 9. Re-author notes

### Must not be simplified

- **The three-column artists layout.** Not two. Not a grid inside the detail pane. `artistPane` is
  `Basis = _midW, MinWidth = railOpen ? 220 : 300, MaxWidth = _midW, Shrink = 1, Grow = 0` — the comment at `:955-957`
  explains why `Shrink = 0 + fixed Width` is wrong (the viewport outgrows the flex slot and the discography tiles slide
  under the tracks pane). Copy the flex values exactly.
- **Two independent copies of the sort/view control** with their own persisted keys. The discography's copy passes
  `hasCreator: false, hasRelease: true`; the navigator's passes `HasCreator = kind != "artists"`,
  `HasRelease = kind == "albums"`.
- **The "…" loading caption vs the big-type no-match state.** They are different states and the distinction is the
  page's honesty.
- **The two `_syncingSel` guards** (`:66, :525, :530-541` and `:1250, :1366, :1423-1429`). `ItemsView` forwards *every*
  `SelectionModel` mutation — including the page's own re-sync — to `OnChange`. Without the guard: a re-sync wipes the
  persisted `_albumKey`, and the discography sync fires `onDrill` and skips the collapsed discography level entirely.
  This is two shipped bug fixes encoded as four lines.
- **`SyncDisco`'s snap-to-first is an auto-select, not a correction** (`:1392-1417`): it fires *only* when `_albumKey` is
  empty. A key that is set but absent from the shown (filtered/sorted) list leaves the grid unhighlighted and the tracks
  column still resolves it. Restoring the older "fix it whenever it's missing" behaviour clobbers every deliberately
  chosen release.
- **`StartBringItemIntoView` must stay unanimated and minimal** (`alignmentRatio` NaN): a smooth scroll across a 10k-row
  library is a journey, not a cue (`:530-534`).
- **One row definition.** The search shimmer is *derived* from the same row builders using blank instances
  (`SkelArtist`, `SkelAlbum`, `SkelTrack` at `:81-83`) — never a second hand-authored tree. Those instances are real
  (non-null) because the builders read `.Uri` / `.Name` / `.Match` unconditionally.
- **`Prop.Of(() => Tok.StrokeCardDefault)`** on the grip seam — a live thunk, not a resolved colour.
- **The `awaiting` widening** (`:199, :726-728`): `Skel.Region`'s built-in pending test reads the loadable state alone,
  and `KeepPreviousData` deliberately holds it at Ready across a re-query. Correct for a refine, wrong for the first
  query of a session (where the kept value is the empty seed and Content would paint "Nothing matches" for the length of
  the fetch).

### Traps

| trap | what happens | how 0.2.9 handles it |
|---|---|---|
| **Hooks must never be branched** | All three kinds are the same type, so a branched hook count lets the reconciler reuse a sibling's hook slot → an `EffectCell` → `AsyncResourceCell` cast **crash** | All three `UseResource` loads are unconditional and in a fixed order; the off-kind ones key on `""` and resolve to `Empty` with no fetch (`:186-190`). `LibraryArtistPane` does the same with `svc?.PlayLog.Version.Value` and its `SyncDisco` effect, both **above** the early return (`:1265-1277`) |
| **Props freeze at mount** | Passing a value where a Signal belongs freezes the selection at mount | The detail pane gets a stable `Loadable` cell re-driven by `UseResource`; its handlers read `_model.Value.Peek()` at call time (`:1074`) |
| **`Key` remounts** | A frozen `ItemsView` template indexes `shown` by position; a stale template lies | Remount on `view:size:OrderKey:FactsKey` only, with `ScrollKey` restoring the offset across every remount (`:496-499`) |
| **`ReuseGuard`** | Would fire on a same-set republish that remounts | `OrderKey`/`FactsKey` are deterministic functions of the rows, so a same-set republish produces an identical key. The always-on `library.nav.remount` log line (`:570-580`, fields `kind`, `reason` ∈ `view\|order\|facts`, `before`, `after`) is the confirmation — **keep it** |
| **Zero-allocation scroll frames vs per-row richness** | `NavRowContent`/`NavCardContent` allocate a `List<Element>` per row per realize | Acceptable in 0.2.9 because `ItemsView.Create` realizes only the window. **In 0.3 this must move to `CreateBound` with `BoundItemScope` binds** — the plan's Wave 5 gate is "no allocation on a scroll frame (FG_FPS_LOG alloc counter = 0)". Build rows from value-only `with`-swaps over a pre-allocated shape, never a `List<Element>` |
| **Two `SelectionModel`s, two `ItemsViewController`s** | Navigator and discography each need their own | Separate instances on separate components |
| **The 180 ms debounce is deliberately shorter than the omnibar's 250 ms** | This corpus is local/cache-only (`:72-74`) | Keep 180 |
| **Podcasts must not enter search mode** | Their corpus does not exist | `fullSearch` gates on `_kind != "podcasts"` **and** `svc.RealStore is not null` — on `--fake` every kind keeps the plain title filter (`:161-167`) |
| **`Render` has side effects** | `Project` calls `store.EnsureAlbums()` / `EnsureArtists()` / `EnsureShows()` through `Warm(...)` **during render** (`:394-401`), and `PrefetchImage` is issued for every shown row on every render (`:148-150`) | Idempotent by construction (a re-hit cache entry is a dictionary lookup), which is why it is tolerable in 0.2.9. In 0.3 the demand belongs in an effect / the query layer — `no-page-side-fetch-windows` says a page demands its **whole** model once, and a render-time `Ensure` is the shape that turns into a per-frame demand the moment anything else re-renders the page |
| **Per-render array churn, not just per-row `Element` churn** | Every render allocates: a LINQ `Select(...).ToArray()` over the whole saved set (`Project`, `:394-399`), a filtered `Where(...).ToArray()`, a `LibraryNavFacts[]`, a permutation `int[]`, a `NavItem[]` and a second `LibraryNavFacts[]` (`Shape`, `:407-418`) — on **every** play-log bump (`:141`), rail toggle (`:182`), keystroke (`:155`) and selection change | Nothing — this is the cost 0.3 must not inherit. `LibraryNavOrder` should sort **slot indices in a pooled `int[]`** over the edge's targets, with the facts read through the table columns rather than copied into a record array. This is a harder constraint than the §9 "zero-allocation scroll frames" note, because these allocations happen on a *selection click*, not only on scroll |
| **The `library.nav.remount` reason is inferred, not measured** | `NoteNavKey` assigns `_lastFactsKey` and never reads it (`:574, :579`), so `reason` falls to `"order"` whenever the order changed — even if the facts changed in the same render | Harmless today; keep the line, but do not treat `reason=facts` as proof nothing else moved |

### Where the plan is wrong or too thin for this surface

- **§2 line budget.** `User.cs` 500 + `User.UI.cs` 500 + `User.Page.cs` 1500 = **2500 lines** was supposed to cover
  *library edges + the library pages + Liked Songs + a profile page*. The library master-detail **alone** is 1809 lines
  in 0.2.9 and gains a search matcher (DATA GAP 5, ≈300 lines that 0.2.9 kept in `Backend/Library`); Liked Songs
  (chapter 07) is a separate, substantial surface; and **there is no profile page to budget for at all** — 0.2.9 has
  neither the page nor a route to it (`ShellRoutes.s_exact`, `ShellRoutes.cs:27-42`, lists no user/profile key, and
  `Features/Shell/ProfileMenu.cs` is the avatar **menu**, chapter 19's). **The old figure was off by roughly 2×.**
  **Settled (arbitration 2026-09-12):** eight files, ≈8400 lines — the table under "The `User.*` file plan" below.
- **§2 tree is missing the library search matcher.** There is no file for a cache-only, grouped, span-carrying library
  search anywhere in the tree. It belongs in `User.cs` CORE; say so.
- **§4.12 `Track.Row`** is the *detail* row and has nothing to say about this surface. The library needs three more row
  shapes (`Album.NavRow`/`NavCard`, `Artist.NavRow`/`NavCard`, `Show.NavRow`/`NavCard`) plus three search-row shapes
  (`SelectableRow` artist, `SelectableRow` album, `TrackHitRow`) and two compact shapes (`CompactEpisodes`). The plan's
  `RowStyle` abstraction does not cover a card, and the `style.ShowNumber ? … : null` pattern shown there conflicts with
  the zero-alloc rule it also states.
- **§4.13 `Album.Page`** demands the whole model on mount — correct, and exactly what the library detail pane needs — but
  it shows a **full page**, not the 104-DIP compact pane that is the library's right column. `Album.Page.cs` must carry
  **two** components: `Album.Page` and `Album.CompactPane`. Owner M builds the first, owner O needs the second; that
  boundary is unassigned in §5.

  **Settled 2026-09-18 (§12) — not two components in one file.** `Album.Pane.cs` is its own new named partial
  (`public readonly partial struct Album`: `PaneProps`, `Pane`, `PaneHeader`, `PaneCommands`, `AlsoByStrip`), owner
  M; the library's artist column is `Artist.Reader.cs` (`public readonly partial struct Artist`: `ReaderProps`,
  `Reader`, `ReaderShape`, `Block`, `Spine`, `ArtistBand`), owner N — not an `Artist.DiscographyPane` inside
  `Artist.Page.cs`. This closes both the boundary above and the "three owners, one screen" bullet just below.
- **§4.14 `User.cs`** models the library as edges (correct) but shows only `Liked`, `Rootlist` and `Like(...)`. It never
  names `SavedAlbums` / `FollowedArtists` / `SavedShows` accessors, the `AddedAt` read the "Recently added" sort needs,
  or any ordering rule. §4.3 does declare the three edge tables — the two sections are out of step.
- **§5 Wave 5 owner split.** Owner O owns `User.Page.Library.cs` but the library's right column is `Album.CompactPane` /
  `Show.CompactPane` (owner M) and `Artist.DiscographyPane` (owner N). Three owners, one screen. Either move the compact
  panes to O or make them the first thing M and N deliver. **Settled 2026-09-18 — see the note above:** `Album.Pane.cs`
  (M) and `Artist.Reader.cs` (N) ship as their own named partials, each demanded/mounted by O's
  `User.Page.Library.cs` through a frozen props record; no compact-pane file moved to O.
- **No chapter of the plan mentions column-width persistence, scroll memory keys, or the collapse breakpoint.** All
  three are load-bearing (§8).

### Line budget

| | lines |
|---|---|
| 0.2.9 (the five `Features/Library` files) | **1809** |
| 0.2.9 + the library-search backend it depends on | ≈ 2100 |
| former plan §2 target for `User.cs` + `User.UI.cs` + `User.Page.cs` (library **+ liked**) | **2500** |
| honest estimate, library master-detail alone in 0.3 | **1900 – 2200** (`User.Page.Library.cs` structure ≈ 1300, `User.UI.cs` rows/pill/flyout ≈ 600, `User.cs` CORE rules + matcher ≈ 300) |
| honest estimate, `User.*` total (library + liked + edges + the cover and facts of chapter 07) | **8 400** |

### The `User.*` file plan (settled 2026-09-12)

The recommendation above was taken, with one change: **there is no `User.Page.Profile.cs`**, because there is no
profile page and no profile route in 0.2.9 to re-author (verified: `ShellRoutes.cs:27-42`; the only "profile" in the
tree is `Features/Shell/ProfileMenu.cs`, a menu). `RouteKind.User` comes out of the plan's route enum with it.

Eight files, one owner (**O**), split **pages by page, helpers by concern**, all named as partials on day one under
the plan's own +30% rule rather than discovered at Wave 5. The rows this chapter owns are marked ◆.

| file | this chapter | why | rough lines |
|---|---|---|---|
| `Entities/User.cs` (CORE) | ◆ | the user row + the library edges (`SavedAlbums` / `FollowedArtists` / `SavedShows` / `Liked` / `Rootlist`, with `AddedAt` on the payload), `OrderLibrary`, `LibrarySelectionCommit`, `LibraryLayoutBreakpoints` and the library search matcher (§8, DATA GAP 5) | 800 |
| `Entities/User.UI.cs` | ◆ | `Album`/`Artist`/`Show` nav row + card builders, `SortViewPill` + `SortPanel`, `CrumbBar`, `ColumnGrip`, the search-row shapes | 600 |
| `Entities/User.Page.Library.cs` | ◆ | `User.LibraryPage` — the three-column master-detail, its ten persisted signals, the collapse arm, search mode | 1 300 |
| `Entities/User.Page.Liked.cs` | | the Liked page's own configuration of the shared detail frame (chapter 07) | 700 |
| `Entities/User.Liked.cs` (CORE) | | `LikedCoverRules` + `ContentFilterTags` (chapter 07) | 500 |
| `Entities/User.Facts.cs` (CORE) | | `LikedFactsRules` verbatim (chapter 07) | 1 000 |
| `Entities/User.Facts.UI.cs` | | the facts bento (chapter 07) | 1 800 |
| `Entities/User.Cover.cs` | | the nine generated covers + the picker (chapter 07) | 1 700 |
| **`User.*` total** | | | **8 400** |

Not `User.*` and not owner O's, but named here so nobody looks for them under this type: the library's right column is
`Album.CompactPane` / `Show.CompactPane` (`Album.Page.cs` / `Show.Page.cs`, owner M) and `Artist.DiscographyPane`
(`Artist.Page.cs`, owner N); the `local` route is the `wavee:local:all` arm of `Playlist.Page.cs` (owner O, §11).

### Files / pages missing from the §2 tree

1. `Entities/User.Page.Library.cs` — **settled 2026-09-12** and now in the tree with owner O (the file plan above).
2. A home for the library search matcher — **settled**: the `User.cs` CORE section, named in §2.
3. `Album.CompactPane` / `Show.CompactPane` / `Artist.DiscographyPane` — three components the tree implies but never
   names, each with a different owner.
4. The **local files** route has no file at all in §2, and Wave 5's owner O gets `User.*` with no local arm — so the
   one route sitting in `ShellRoutes.s_exact` beside `albums`/`artists`/`podcasts` (`ShellRoutes.cs:35`) and in
   `ContentHost.IsDetail` (`:178`) is owned by nobody. The decision this chapter records (header, §1.2, §11): it is a
   `Playlist` page over a synthetic uri, so **`Playlist.Page.cs` must handle `wavee:local:all` and plan §5 must give
   owner O that arm**. Its frame is `03-detail-frame.md` + `06-playlist.md`; its entry point (route, glyph, history
   facet, pin, and the never-in-the-navigators rule) is §11.
5. `App/LocalPlayables.cs` (165) and `Actions/LocalFileActions.cs` (115) are named by **no** chapter's source list and
   by no §2 entry, yet they own two visible things: the **display title of every local file** anywhere in the app
   (`LocalPlayables.TitleOf:120-139` — the file name without its extension; tag reading is explicitly out of scope) and
   the whole **file-drop policy** behind W27's cue (`ClassifyDrop:142-163` + the three toasts). They are engine-free app
   code and carry over verbatim; they need a §2 line in the app-file list, not an `Entities/*` home.

---

## 10. Parity checklist

Side-by-side method for every item: run the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake` beside the 0.3 build at the
same window size and zoom, drive both to the named route, and compare. "static capture" = a screenshot diff;
"hover capture" = screenshot with the pointer parked; "frame recording" = a 60 fps capture, compared frame by frame.
Settings live in `%LOCALAPPDATA%\Wavee`; delete `library.*` keys to reset to defaults before a defaults test.

1. Route `albums`, window 1440, rail closed, **fresh settings** — left column is exactly **340** DIP wide and the page
   title reads "Albums" at 28/36/600 on one line. *(static capture, measure)*
2. Route `artists`, same conditions — left column is exactly **280**. *(static capture)*
3. Route `artists` — the mid column starts at exactly **440** and the third column takes the remainder. *(static)*
4. Navigator ground is one rung lighter than the reading panes, and a **1-DIP hairline** is visible between every pair of
   columns in **both** light and dark. *(static capture, both themes, zoom in)*
5. The hairline sits at the horizontal centre of the 16-DIP grip strip, behind the hover thumb. *(hover capture)*
6. Hovering a grip reveals a 2-DIP rounded thumb inset 4 from top and bottom, fading in over ~167 ms. *(frame recording)*
7. Dragging the left grip clamps at **240** and **560**; the mid grip at **300** and **620**. *(drag to each end, measure)*
8. A grip drag followed by app restart restores the dragged width. *(restart)*
9. Route `podcasts` — the sort flyout shows **Recents / Recently added / Alphabetical / Creator** and **no** "Release
   date". *(open flyout, static)*
10. Route `artists` — the flyout shows **no** "Creator" and **no** "Release date". *(static)*
11. Route `albums` — the flyout shows all five rows. *(static)*
12. The discography column's flyout (artists view) shows **no Creator** but **does** show **Release date**. *(static)*
13. Clicking the active sort row flips the chevron between `ChevronUp` and `ChevronDown` **without closing the flyout**,
    and the list re-orders. *(frame recording)*
14. Clicking a different sort row sets direction back to ascending (chevron up). *(static)*
15. The "Size" S/M/L bank appears only when a grid view (cell 3 or 4) is selected. *(static, all four views)*
16. Grid cell minimum widths: compact grid **88 / 104 / 120** and full grid **116 / 140 / 164** for S / M / L. Verify by
    counting columns at a fixed 340-DIP left column. *(static, six captures)*
17. List row extents: navigator **40** compact / **60** full; discography **44** compact / **60** full. *(static, measure)*
18. Compact list has no cover and no subtitle; compact grid has no title. *(static)*
19. Artist grid cards use round covers with 16 DIP of padding and a centre-aligned title; album cards use 6-radius
    covers with 8 DIP padding and a start-aligned title. *(static)*
20. Selecting a list row paints a **3 × 16** accent bar at the row's left edge (margin-left 4, r 1.5) over a subtle
    backplate — not a full accent row. *(static, zoom)*
21. The selection bar springs in (scale-Y from 0) rather than appearing instantly. *(frame recording)*
22. Selecting a **grid** item paints a 3-DIP accent ring with a 1-DIP inner stroke, not an accent bar. *(static)*
23. Hovering an already-selected list row goes **lighter** (tertiary), not darker. *(hover capture)*
24. Scroll the navigator halfway, change the view type, and the list lands back at the same offset. *(frame recording)*
25. Scroll the navigator, switch tabs/route away and back — the offset is restored on the **first** painted frame, not
    after a visible jump. *(frame recording)*
26. Play a track from an album, then set sort = Recents: that album/artist jumps to the top of the played block, and
    never-played rows stay below in source order. *(live session required — `--fake` never plays; compare on the real
    session or drive `PlayLogStore` directly)*
27. With `desc` on, Recents reverses **inside** each block; a never-played row never floats above a played one.
    *(static, before/after)*
28. Click a row, then click it again — no navigation, no route change, the tab title does not change. *(static)*
29. Click a navigator row in the artists view: the discography re-picks the artist's **first** release (the third column
    changes). *(static)*
30. Select a release deliberately, filter the discography so it disappears, clear the filter — the same release is still
    selected and the tracks column still shows it. *(static)*
31. Restart with a selection saved — the same item is selected, the same release is in the third column, and the
    navigator has scrolled it into view. *(restart)*
32. Type into the filter box: the **browse** list narrows on the very first keystroke (albums/artists/podcasts). *(frame
    recording; watch for a 180 ms lag, which would be wrong)*
33. On `--fake` (no `RealStore`), typing never enters search mode for any kind — the plain title filter stays.
    *(static)*
34. On a real session, typing in `albums`/`artists` swaps the columns to search results after ~180 ms of pause; the
    toolbar and page title stay put. *(frame recording)*
35. First query of a session: all three columns shimmer **together** (one settle window), 6 rows each, with a
    ~1 s breathing pulse. *(frame recording)*
36. Refining an existing query: the previous rows **stay on screen** (no shimmer, no "Nothing matches"), and only the
    facet count fades to 40% alpha. *(frame recording)*
37. Search rows reveal with a per-row 40 ms stagger, an 8-DIP rise and a soft blur, over ~500 ms. *(frame recording)*
38. A title match paints an accent-plate pill around the matched run with white ink and 4-radius corners — not a coloured
    word. *(static, zoom)*
39. An artist that matched through one of its albums shows an eyebrow "Matched · album ‘…’" above the name; an artist
    whose own name matched shows **no** eyebrow. *(static)*
40. In the artists view's middle column, album rows show **no** eyebrow (they are context under an already-explained
    artist); in the albums view's left column, they do. *(static, both)*
41. Click a search track hit — it plays immediately and the panes do not change. *(frame recording + audio)*
42. Click a search artist hit — the filter box clears, the browse panes return, and that artist is selected and scrolled
    into view in the navigator. *(frame recording)*
43. Click a search album hit in the **artists** view — the owning artist becomes the navigator selection and the album
    becomes the third-column release. *(static)*
44. Narrow the window until content width crosses **640**: the layout collapses to one column with a breadcrumb, and the
    page title disappears (the crumb names the kind instead). *(frame recording)*
45. Widen again: the layout stays collapsed until content width reaches **664**. *(slow resize, frame recording)*
46. Collapsed, tap a navigator row → depth 1; tap a discography tile → depth 2; the crumb trail reads
    "Artists › <artist> › <release>". *(static, three captures)*
47. Collapsed, a crumb whose name has not loaded shows "…" rather than an empty gap. *(static, cold start)*
48. Collapsed albums/podcasts: the trail never goes past depth 1. *(static)*
49. Collapsed: the crumb bar sits on the navigator's ground rung with a 1-DIP divider under it. *(static, zoom)*
50. Detail pane, cold: the skeleton is **solid blocks** (104 cover + 80/200/140 bars + six 14-tall bars) with **no**
    shimmer pulse and **no** blur reveal. *(frame recording — a pulsing skeleton here is wrong)*
51. Discography pane, cold: the toolbar (sort pill, filter, "Go to artist") is **already up** while the body shows eight
    148-tall blocks. *(static)*
52. Detail hero: cover 104 × 104 at radius 8 with **no** drop shadow, eyebrow in tracking-widened caps, title 28/36/600
    wrapping to at most 2 lines. *(static, zoom)*
53. Hovering the hero title turns it accent over ~83 ms and shows a hand cursor with a subtle plate; clicking navigates
    to the album/show page with the header already populated on frame one. *(frame recording)*
54. An entry with no context uri renders the title as **plain text** — no hover, no cursor, no click. *(static; use a
    degraded/fake entry)*
55. Action row: accent Play capsule, a 40 × 40 circular Shuffle fab, the Save heart, then a **Subtle/Small**
    "View full album" — never a second accent button. *(static)*
56. Podcasts: the action row shows Follow instead of Save and **no** "View full album". *(static)*
57. Podcast body is the compact episode list (56-tall rows, 32 circular play chips), not the track table. *(static)*
58. Album body is the full embedded track table with hover play/pause, the now-playing equalizer and per-row hearts —
    identical cells to the album detail page. *(hover capture + frame recording while playing)*
59. The embedded track table has **no** toolbar and never offers a BPM·Key column. *(static)*
60. "Go to artist" reads as a link — accent ink, 4-radius, no capsule, with a trailing ↗ — and hovering shifts the ink
    one rung and scales 1.04. *(hover capture)*
61. Empty states: no selection in albums/podcasts/artists shows the right sentence at 20/28/600, centred, **with no
    icon**. *(static, three captures)*
62. The artists third column with no release picked shows "Select a release". *(static)*
63. A filter that matches nothing shows "Nothing matches your filter" centred at 20/28/600. *(static)*
64. An empty-but-still-loading navigator shows a small left-aligned secondary "…", **never** a big-type empty state.
    *(static, first frames of a cold start)*
65. Drag a navigator row: a drag chip appears only after the pointer travels **twice** the normal drag box; a short
    wobble still registers as a click. *(frame recording, deliberate slow click)*
66. Nothing on this page accepts a drop (drag a playlist over the navigator — no insertion line, no highlight).
    *(frame recording)*
67. Right-click a navigator row — no context menu appears. *(static)*
68. Keyboard: focus the navigator and press Down/Up, Home/End, PageDown — the current item moves and selection follows;
    typing "ra" jumps to the first row starting with "ra". *(frame recording)*
69. Tab moves **once** into the navigator (roving tab stop), landing on the selected item. *(frame recording)*
70. Open the right rail: the artists mid column's minimum drops from 300 to 220 and the three columns still fit.
    *(static, before/after)*
71. `albums`/`artists`/`podcasts` publish **no** shell material — the shell background is identical to a neutral page
    such as `settings`. *(static, compare)*
72. Route `local` is the **detail frame** (hero with a generated gradient cover, "Local Files" / "Music imported from
    this computer." / "On this device · 14 songs"), not the master-detail browser. *(static)*
73. Nothing on the library pages tints to a cover palette — switch between a red-cover and a blue-cover album and no
    chrome colour changes. *(static, two captures)*
74. The `library.nav.remount` log line appears in `%LOCALAPPDATA%\Wavee\logs` on a view/size change with
    `reason=view`, and does **not** appear on a plain selection click. *(log diff)*
75. Cold start with **no** persisted selection: the moment the navigator's first rows land, **row 0 selects itself** and
    the right pane populates. The "Select an album to see its tracks" placeholder must not survive into the loaded
    state. *(frame recording from launch)*
76. Route `podcasts`, type a filter that matches nothing: the navigator shows the big-type "Nothing matches your
    filter" **and** the right pane falls back to "Select a show to see its episodes". Clear the filter — the selection
    lands on **row 0**, not on whatever was selected before. *(two static captures)*
77. In search mode, open the sort flyout and change sort / direction / view / size: the pill and bank update, and the
    three result columns **do not move**. *(frame recording — any reorder here is a 0.3 addition, not parity)*
78. Search an artist by their exact name: the artist row carries the accent pill, and the albums and songs columns show
    **un-highlighted** rows — every album, and every track of the selected album. *(static, zoom)*
79. Search a term that matches an artist but none of their albums: the middle column shows "ALBUMS 0" and **an empty
    body** — no message, no empty state. Same for "SONGS 0" on the right. *(static)*
80. The left search column's zero-hit copy is **14/20 tertiary, left-aligned** (`SearchMessage`), not the browse list's
    20/28/600 centred `EmptyState.Compact`. Two different treatments of the same sentence. *(static, both)*
81. Force a detail load to fail (kill the network mid-selection): the right pane stays on its **skeleton** — no error
    text, no retry. Confirm 0.3 reproduces it, or that the owner signed off on an error state. *(frame recording)*
82. Collapsed at depth 1 or 2: there is **no page sort pill and no page filter box**, and **no splitter anywhere** in
    the collapsed layout. *(static, three captures)*
83. Collapsed, the **current** (last) crumb does not highlight on hover and does not respond to a click; the earlier
    crumbs do both. *(hover capture)*
84. Collapsed + searching: depth 1 shows matched **album rows**, depth 2 matched **track hits** — never the compact
    detail pane. *(static, two captures)*
85. Grid view, a cover-less album: the card's fallback is a flat graded placeholder (`ArtworkFill`), while the same
    album in list view shows the seeded shimmer tile (`Artwork`). Confirm both, and confirm the detail hero's fallback
    (seeded off the **title**) against the navigator row's (seeded off the **uri**). *(static, four captures)*
86. Keyboard: with focus in the navigator, Down moves the current item **and the right pane re-skins** (selection
    follows focus in Single mode); Space on a row does the same thing a click does. *(frame recording)*
87. Toggle Settings ▸ "hide track artwork" while a library search is up: every row in the Songs column re-enters with
    the 90 ms rise-and-fade, because the artwork flag is part of the row key. *(frame recording)*
88. The four view-toggle cells in the flyout carry **no label and no tooltip** — glyph only. *(hover capture; a tooltip
    appearing here is a 0.3 addition)*
89. Drag an `.mp3` out of Explorer and hold it over route `albums`: a **centred** pill reading "Drop a file to play it"
    (14/TextPrimary, pad 18/10, r 4, `FillSolidBase`, 1-DIP `AccentDefault` border, dialog shadow) appears over the
    page, and the library page underneath does **not** change — no row hover, no selection ring, no insertion line, no
    column highlight. *(hover capture with a drag in progress; W27)*
90. Release that drop anywhere over the navigator or a reading pane: the file starts playing, the title in the player
    bar is the **file name without its extension**, and the library route, selection and scroll offset are all
    unchanged. *(frame recording)*
91. Drop a `.txt` over the same page: the error toast "That file can't be played. Wavee plays .mp3, .ogg, .flac and
    .mp4 files." and nothing else — no navigation, no row state change. *(static)*
92. Route `local` renders the **detail frame**, and its 14 tracks appear in **no** library navigator: `albums` and
    `artists` under `--fake` contain no "Local Files" entry and no local album or artist row. *(static, all three
    routes)*

---

## 11. Appendix - Local files, in one place

`local` is not a library master-detail page and never was. Facts the library owner needs:

- **Route.** `ShellRoutes.cs:35` registers it; `ContentHost.cs:178` classes it as a detail route; `DetailPage.cs:78`
  resolves `local` → `(DetailKind.Playlist, "wavee:local:all")`. It **does** publish shell material
  (`ContentHost.cs:184-186`), unlike the three library pages.
- **Nav identity.** `ShellNav.cs:59`: title = loc `nav.localFiles` ("Local files"), glyph `Icons.Folder` (`\uE8B7`).
  `HistoryPage.cs:33` files it under the "library" history facet alongside `albums`/`artists`/`liked`/`podcasts`.
  `SidebarPinId.cs:48` lists it as a pinnable app route; `SidebarBuiltInDocuments.cs:96` places it in the built-in
  library section with a Folder glyph.
- **Model.** `Wavee.Core/Sources/LocalSource.cs:24-31` — one synthetic playlist: name **"Local Files"**, description
  **"Music imported from this computer."**, subtitle **"On this device"**, `Owner("local", "On this device", null)`,
  `Capabilities(CanView, CanEditItems, CanEditMetadata, IsOwner = true, IsCollaborative = false, Known = true)`,
  `Source = "local"`, **no cover** (so the detail frame's generated-gradient path paints it).
- **`--fake` content.** 14 tracks from `FakeData.LocalSeed` (`FakeData.cs:344-351`), each `TrackOrigin.Local`,
  `Source = "local"`, `Availability.Playable`, duration `150 000 + (i·41 mod 140)·1000` ms, with synthetic
  `wavee:local:{track,album,artist}:{i}` uris.
- **Why it is not in the library collections.** `LocalSource.GetLibraryAsync` / `GetAlbumsAsync` / `GetArtistsAsync` all
  return empty on purpose (`LocalSource.cs:57-67`): the local library is reached through its own row, never merged into
  the streamed collections, so it can never duplicate into the albums/artists navigators.
- **Playables — what a file becomes.** `App/LocalPlayables.cs` (165, engine-free, BCL + `Wavee.Core` only).
  `ForLocalFile` (`:28-32`) builds a **complete** `Track` at construction — there is no resolver behind it to fill
  anything in later: `Id = EntityUri.IdOf(uri)`, `TrackOrigin.Local` (what classifies it `PlayableKind.LocalFile`),
  `Source = "local"`, `Availability.Playable` **stated** rather than left unknown, no artists, no album, no image, and
  `DurationMs` from a **fail-soft** header probe — an unreadable header means 0 ("unknown length"), never "cannot play"
  (`:88-114`). The visual one is `TitleOf` (`:120-139`): the display title is the **file name without its extension**
  (a URL falls back to its last path segment), because tag reading is explicitly out of scope. Every row, player-bar
  line and queue entry for a local file shows exactly that string.
- **The drop policy and its toasts.** `LocalPlayables.ClassifyDrop` (`:142-163`) is pure and returns
  `None` / `PlayAudio` / `PlayVideo`, with **audio winning over video** when a drop carries both.
  `Actions/LocalFileActions.cs` is the caller-facing half: `PickAndPlay` (`:37-53`, the profile menu's "Play file…",
  gated on `CanPlayFiles` — the affordance is HIDDEN, never disabled, `:24-32`) and `PlayDropped` (`:58`). Toasts:
  `localFile.rejected` (Error) at `:66` and `:93`, `localFile.notReady` (Informational) at `:73`; the cue's
  `localFile.dropHint` lives in the shell (`WaveeShell.cs:1428`). Two other surfaces call in: a detail-page track row
  hands a **non-mp4** file drop straight back to this same shell path rather than swallowing it
  (`DetailTracks.cs:3468-3477`), and `ProfileMenu.cs:114-119` owns the menu row.
- **Where a file drop lands while a LIBRARY page is up.** Nowhere on the page — §0 #14 holds, so the shell backdrop's
  `Files` target takes it (`WaveeShell.cs:1351-1359, :1385`) and the centred hint pill is the only visual. **W27.**
- **0.3 owner.** `Playlist.Page.cs` (owner O), rendering the same shared detail frame. Its visual contract is
  `03-detail-frame.md` + `06-playlist.md`; the only library-specific requirement is that the route continues to exist,
  keeps its Folder glyph and its "library" history facet, and stays pinnable. **Two edits are needed outside this
  chapter**, recorded here because this chapter cannot make them: plan §2 must name `Playlist.Page.cs`'s
  `wavee:local:all` arm plus the two carried-over app files (`App/LocalPlayables.cs`, `Actions/LocalFileActions.cs`),
  and plan §5 must give Wave 5 owner **O** the local arm alongside `User.*`. `29-cross-cutting.md` §12.2's "15 / 03"
  row for `local` should read **03 (frame + page class) / 15 (entry point + the local-files facts in this appendix)**.

**token-reconcile (2026-09-12):** `Tok.FillControlStrong`, named here at `:695` but missing from the first build of `00-design-system.md §12.1`, is now indexed there (`#00000072` / `#FFFFFF8B` — the same pair as `Tok.StrokeControlStrongDefault`, used as a FILL). No value in this chapter changed.

---

## 12. Audit log

Adversarial re-read of `Features/Library/*.cs` (all five), `App/LibraryBridge.cs`, `Actions/PinActions.cs`,
`Actions/FolderActions.cs`, plus the engine controls and tokens every number here cites.
(Numbered **12** rather than 11 — §11 was already the Local-files appendix, and duplicating a heading would be worse
than renumbering the log.)

**What survived.** Every number in §3's token table, §4's colour table and §5's motion table was checked against
source and is correct: `Spacing` XXS 2 / XS 4 / S 8 / M 12 / L 16 / XL 20 / XXL 24; `Radii` Control 4 / Card 8 /
Full 999; the pill (h 32, gap 5, pad 10/0/8), the flyout (MinW 230, pad 4, gap 1, header pad 8/4/8/2, row h 32 r 5
gap 8, divider margin 4, bank 40×30 gap 4 pad 2/2/2/4, glyphs 14/16/12/15); the nav extents 40/60 and disco 44/60; the
grid ladders 88+16·size / 116+24·size and 84+16·size / 100+24·size; hero pad 20/20/20/12 gap 16 text-gap 3, cover
104 r 8 decode 256; actions pad 20/0/20/12 gap 12; episode rows MinH 56 pad 8 gap 12 chip 32 glyph 12; both hand-rolled
skeletons (80/200/140 + six 14-tall bars with margin-top 8; eight 148-tall r 8 blocks); `SearchScroll` pad
(8,4,8,`PlayerDock.Reserve` 72 + 20 = 92); `ShimmerStack` 6 rows pad (8,4,8,4) gap 2; the highlight pill (pad 3/1,
r 4); every persisted default (leftw 280/340, midw 440, sort 0, desc false, view 1, size 1, album.view **3**);
`CollapseBelow` 640 + `Hysteresis` 24; the four palette values (`#80FFFFFF` / `#B3FFFFFF` / `#0F000000` / `#19000000`);
`WaveeMotion` 83 / 167 / 250 / stagger 40 and the 1.04–0.96 and 1.07–0.92 tiers; `SelectorVisuals` 3×16 r 1.5
margin-left 4, backplate margin 4/2, press 10/16, spring 0.30/0.85 and the full six-state fill ramp; `ItemContainer`
3 + 1 inset 2 fade 167; `Splitter` StripW 16, thumb 2 r 1 inset 4; `SelectorBar` 4×3 → 16 px @ 167;
`SkeletonStyle.Default` pulse 1000 → 0.5, exit 250; `SkelReveal.StaggerRows` dy 8, blur 3, 500 ms, 40 ms; all loc-key
families. §8's five pure rules, their destinations, their **exact** test line counts (198 / 84 / 32 / 142 / 117) and
the pinned permutation `[2,4,0,1,3]` are correct. §0's fifteen original non-negotiables are correct as written.

**Corrections made.**

1. *missing* — **§0 #16 added.** `SyncNav` adopts row 0 whenever the key is empty or absent (`:558-564`) and
   `SyncDisco` does the same for an empty `_albumKey` (`:1404-1412`). The chapter presented W9's placeholders as the
   no-selection state without saying they are transient. W9 gained the same caveat.
2. *missing* — **§0 #17 plus a new wireframe under W10.** A title filter that matches nothing also clears
   `_selectedKey`, `_albumKey` and the `SelectionModel` (`:546-556`), so the right pane falls to the W9 placeholder;
   clearing the filter re-adopts **row 0**, not the prior selection (`:563`). Neither half was recorded.
3. *missing* — **§0 #18 added, §6 row added.** In search mode the sort pill, view bank and size bar are live,
   persisted and completely inert: nothing downstream of those four signals reaches a search row (`:407-418, :442`).
4. *wrong* — **§1.1 tree.** The tree showed `0 hits ? SearchMessage(...)` as the generic `SearchSkel` Content arm. That
   message exists only in `LeftSearchBody` (`:657-662`); `s:albums`, `s:tracks`, `s:detail` and all three collapsed
   search bodies have **no** zero-hit copy. Corrected in place and given wireframe **W23**.
5. *missing* — **W23 also records** that the left column's zero-hit copy is `SearchMessage` (14/20 `TextTertiary`,
   left-aligned, pad 12/20/12/20, `:792-796`) while the *browse* list's is `EmptyState.Compact` at 20/28/600 centred
   (`:491`) — two treatments of the same `library.noMatch` string on one page.
6. *missing* — **W24 added: there is no error state on the browse side.** `LibraryDetailPane` (`:1030`) and
   `LibraryArtistPane` (`:1281`) gate on `Ready` alone, so a failed (or Ready-but-empty-title) load skeletons forever.
   `ErrorState` is wired on exactly one path (`:741`). §1.1 and §7 annotated to match.
7. *missing* — **W25 added: what collapsed does not have.** No `Grip` anywhere in `CollapsedLayout` (`:255-286`) even
   though `_leftW`/`_midW` stay persisted, and `Toolbar()` mounts only at depth 0 (`:274-275`), so the page's sort pill
   and filter box vanish at depth ≥ 1. It also records the three collapsed **search** bodies (`:300-338`), which the
   chapter previously reached only through their crumb sources.
8. *missing* — **W26 added: hits that carry no highlight.** `LibraryAlbumGroup.Tracks` is all tracks when the
   album/artist *name* matched and only the matching ones otherwise, and `MatchLen == 0` ⇒ a plain `TextEl`, no pill
   (`LibrarySearch.cs:35-38`, `SearchHighlight.cs:23-29`). §7 gained the matching shape requirement for DATA GAP 5.
9. *missing* — **W4 gained a four-row artwork table.** List rows use `Surfaces.Artwork` (seeded `Shimmer` tile, decode
   = display size 40); grid cards use `Surfaces.ArtworkFill` (no shimmer tile, a graded `WatchedPlaceholder`, decode
   fixed at **256** regardless of S/M/L, `Surfaces.cs:292-303`); search rows add an explicit `CoverSkeleton` override
   (`:861-865`). The loading and no-cover looks therefore differ between the list and grid arms, and none of them
   matches the 64/168/256 prefetch the same section describes.
10. *missing* — **the detail hero seeds its generated cover off `m.Title`** (`:1129`) while every row and card seeds
    off the uri (`:852, :876, :906, :1450`), so one cover-less album gets two different fallbacks on one screen.
    Recorded in the new W4 table.
11. *missing* — **§3 gained a full glyph inventory** (12 entries, each verified against the generated
    `Icons.Glyphs.g.cs`). The chapter previously named five codes and omitted `Search` ``, `Play` ``,
    `Shuffle` ``, `OpenInNewWindow` `` (the ↗) and `ChevronRightMed` `` (the crumb separator).
12. *missing* — **§3 records four dead loc keys.** `LibrarySortPanel.ViewToggles` resolves `library.view.compactList` /
    `.list` / `.compactGrid` / `.grid` into a tuple (`LibrarySortView.cs:130-136`) and then renders glyph + size only
    (`:146`). The bank carries no text, no tooltip and no `AutomationName`.
13. *overclaim* — **§4 match-highlight row.** "light `#0078D4`-family / dark `#0078D4`" was vague; the token is the
    literal `#0078D4` in **both** themes (`PaletteBuilder.cs:452, :341`). Tightened, with line cites.
14. *missing* — **§4 gained a "two themes, and only two" paragraph.** `WaveeTheme.ResolvePalette() =>
    Tok.NeutralPalette` (`Design/WaveeTheme.cs:15`) and `Build(Neutral)` short-circuits to `BuildWinUILight()`
    (`PaletteBuilder.cs:44, :168`). The engine's Warm/Slate seeds have **opaque** light rungs (`#FAF9F6` / `#FCFBF9` /
    `#DCDAD4`, `:497-511`) that would dissolve the three-coincident-whites argument outright — they are not shipped,
    and that fact is load-bearing for §0 #2.
15. *missing* — **§5 gained six motion rows:** the artwork-toggle remount of the whole Songs column (the row `Key`
    embeds `":art="`, `:885`); the `n == 0 ⇒ plain SoftReveal` branch (`SkeletonRegion.cs:179`); the
    `SkelReveal.None` exit floor, recorded so nobody swaps the reveal and silently changes the dissolve; the
    transition-less appearance/disappearance of the grips and page toolbar across a breakpoint or depth change; and
    the breadcrumb ink ramp **with** its last-crumb exception.
16. *missing* — **§6 keyboard table gained Space, Enter and "selection follows focus".** In Single mode every keyboard
    move runs the selector's `OnFocusedAction` (`ItemsView.cs:387-392`), so Down / Home / PageDown / typeahead each
    re-skin the right pane and refire the detail load — a materially different cost model from "arrows move a
    highlight". Two `SyncNav` rows were added there too.
17. *missing* — **§6 breadcrumb section**: the last crumb has no `OnClick` and no hover/pressed ink
    (`BreadcrumbBar.cs:96-106`); every crumb shapes at Normal weight, the current one differing only by inertness.
18. *missing* — **§6 toolbar table**: the discography filter (`_aFilter`) is title-contains only, with no debounce and
    no search path (`:1340-1351`), so the page filter's two-reads-of-one-box rule does not apply to it.
19. *missing* — **§8 `OrderKey` is `rows.Length + ":" + hex16`**, not a bare FNV hash (`LibraryNavOrder.cs:82`), and
    `sign` multiplies the tie-breaks as well as the primary key (`:46, :52, :59`), so `desc` reverses equal-key runs.
20. *missing* — **§8 `SortLabel` is not library-private.** Sidebar Library V3 shares the int codes and the
    `library.sort.*` keys for 0–3 (`LibraryV3Metrics.cs:105, :133-141`) and `V3SortViewFlyout.cs` is a deliberate
    *copy* of the pill + panel. Moving or renumbering them breaks 25-sidebar.md.
21. *missing* — **§9 gained three traps:** `Render` has side effects (`Warm` calls `store.Ensure*` inline, `:394-401`,
    and `PrefetchImage` fires per row per render, `:148-150`), which collides head-on with the owner's
    `no-page-side-fetch-windows` rule; the per-render array churn of `Project` + `Shape` (six allocations over the
    whole saved set on every play-log bump, rail toggle, keystroke and selection click, `:394-418`), a harder
    constraint than the existing scroll-frame note; and `_lastFactsKey` being written but never read (`:574, :579`),
    so `reason=order` can mask a simultaneous facts change.
22. *missing* — **§10 gained items 75–88** covering all of the above: cold-start auto-select, the filter-clears-
    selection pairing, the inert search-mode controls, un-highlighted hits, the silent empty facets, the two no-match
    treatments, the missing error state, collapsed's missing chrome, collapsed search, the two artwork paths, keyboard
    selection-follows-focus, the artwork-toggle remount, and the label-less view bank.

**Unverified, stated as such.**

- `WaveeType.Eyebrow(m.BadgeType ?? (_show ? "Podcast" : ""))` (`:1133`) renders an **empty** 12/16 eyebrow for an
  album whose model carries no `BadgeType`. Whether an empty `TextEl` measures its line box (reserving ~16 DIP above
  the title) or collapses to zero was not confirmed against the engine's text measurer. Check it on a degraded entry
  before reproducing the hero's vertical rhythm.
- `Reconciler.cs:1443-1445` (the 250 ms shimmer-orphan dissolve) was confirmed from `SkeletonStyle.ExitMs =
  Expressive.Fast` (`SkeletonRegion.cs:35`), not read at that line: the duration is right, the line cite is
  second-hand.
- Which shared controls self-tooltip (`Button`, `IconButton`, `AutoSuggestBox`) is still open — §6 already flagged it
  and this audit did not close it.
- §2's "1 monospace char ≈ 8 DIP" scale makes every wireframe an approximation; the numbers beside them, not the
  ASCII, are the contract.

**Out of scope, checked and clean.** `Actions/PinActions.cs` and `Actions/FolderActions.cs` touch this surface only as
*drop targets* for a library row's `WaveeDragKinds.Resource` payload — there is no pin row, no folder verb and no
context menu anywhere in `Features/Library` (grep for `Menus.` / `ContextMenu` / `ContextFlyout` / `Tooltip` returns
zero hits). §0 #14 and §6's right-click row are correct as written. `App/LibraryBridge.cs` reaches the page through
`SaveButton`/`FollowButton` only; its one visible contribution is the throttled screen-reader announcement
("Saved" / "Removed from library", `LibraryBridge.cs:286-294`), which §6 already records.


**critic-fix (2026-09-12) — the `local` destination's ownership, and the two source files nobody named.**
(Logged under §12; this chapter's audit log is numbered 12 because §11 is the Local-files appendix.) The completeness
critic reported that `local` is in `ShellRoutes.s_exact` (`ShellRoutes.cs:35`) and in `ContentHost.IsDetail`
(`ContentHost.cs:178`), yet no chapter *owned* it, `App/LocalPlayables.cs` was named by no chapter at all, and
`Actions/LocalFileActions.cs` was cited elsewhere only for its toasts. Verified against 0.2.9 and **upheld in part**:
the route facts, W21 and §11 were already here and correct, but the *ownership statement* was missing from §1.2, the
two app files were missing from every list, and the one local-files visual that actually appears **over** a library page
had no wireframe. Six changes:

23. *critic-fix: ownership stated* — the header's route-facts block gained an explicit **ownership decision** paragraph
    (`local` = a `DetailKind.Playlist` arm of the shared frame; `03` owns the frame, `06` the body,
    `Entities/Playlist.Page.cs` / owner **O** the page class, **15** the entry point), and §1.2 gained the matching
    one-paragraph statement plus the ask on plan §2/§5 and on `29-cross-cutting.md` §12.2's "15 / 03" row.
24. *critic-fix: §1.2 rows added* — two rows: the `local` route → the `wavee:local:all` arm of `Playlist.Page.cs`
    (`DetailPage.cs:78`, `ContentHost.cs:178`, `LocalSource.cs:24-31`), and the file drop that lands over these pages →
    `App/LocalPlayables.cs` + `Actions/LocalFileActions.cs` carried over verbatim.
25. *critic-fix: sources folded in* — the header source list now names `Wavee.Core/Sources/LocalSource.cs` (85),
    `App/LocalPlayables.cs` (165), `Actions/LocalFileActions.cs` (115) and the shell's drop target/cue
    (`WaveeShell.cs:1351-1359, :1385, :1418-1431`) as this chapter's local-files arm.
26. *critic-fix: W27 added* — **A file dragged over a library page.** The page accepts no drop (§0 #14), so the engine's
    deepest-accepting-node rule hands the drag to the shell backdrop (`WaveeShell.cs:1351-1359`, `DropTarget = _fileDrop`
    at `:1385`) and the only visual is a window-centred pill: pad 18/10, r `Radii.Control` (4), `Tok.FillSolidBase`,
    1-DIP `Tok.AccentDefault` border, `Elevation.Dialog`, text loc `localFile.dropHint` ("Drop a file to play it") at
    14/`TextPrimary`, on a `HitTestPassThrough` layer with a **bound** `Opacity` (`:1418-1431`). The wireframe carries
    the by-content outcome table (audio beats video, `ClassifyDrop:155-163`) and the three toasts.
27. *critic-fix: §5 + §6 rows added* — §5 gained the drop-cue row (bound `Prop.Of`, **no motion spec declared** — the
    code comment says "fades in" but no duration is stated anywhere, so none is invented here; compositor-only, the page
    is never re-rendered by a drag hover). §6's navigator table gained "drop a **FILE** onto the page", which is the
    honest complement to the existing "drop onto the page → nothing" row.
28. *critic-fix: §9, §10 and §11 extended* — §9's "files missing from the §2 tree" item 4 now states the decision rather
    than only the gap, and a new item 5 covers the two unnamed app files (`TitleOf:120-139` is the **display title of
    every local file in the app**; `ClassifyDrop:142-163` is the whole drop policy). §10 gained parity items **89-92**
    (the drop pill over `albums`, the drop that plays without moving the selection, the rejected `.txt` toast, and
    local's 14 tracks never appearing in a navigator). §11 gained the playables, drop-policy and drop-landing bullets,
    and its 0.3-owner bullet now names the two out-of-chapter edits (plan §2, plan §5) the decision requires.
29. *arbitration 2026-09-12* — **A3 settles the `User.*` file scheme.** This chapter and chapter 07 proposed two
    incompatible partial schemes for one type with one owner; the decision is **pages by page, helpers by concern**:
    `User.cs` (CORE), `User.UI.cs`, `User.Page.Library.cs`, `User.Page.Liked.cs`, `User.Liked.cs` (CORE),
    `User.Facts.cs` (CORE), `User.Facts.UI.cs`, `User.Cover.cs` — owner **O**, ≈8400 lines, all named as partials on
    day one. The header target, §1.2's lead and its `LibraryPage` row, §9's budget bullet, the three line-budget rows,
    the "Recommendation" paragraph (now **The `User.*` file plan**) and file-gap items 1–2 are rewritten to it.
    **`User.Page.Profile.cs` is deleted and `RouteKind.User` with it** — verified against 0.2.9: `ShellRoutes.s_exact`
    (`ShellRoutes.cs:27-42`) carries no user or profile key, `ContentHost.PageFor` has no arm for one, and the only
    "profile" in the tree is `Features/Shell/ProfileMenu.cs`, the avatar menu (chapter 19); its "Play file…" row is
    already recorded in §11. The `User.*` total row moves from "3400 – 3900 (library + liked + profile + edges)" to
    the settled **8 400**.

**library-rework (2026-09-18) — Library rework (the Collection browser).** Implemented in
`src/apps/Wavee/Entities/{User.cs, User.UI.cs, User.Page.Library.cs, Album.Pane.cs, Album.UI.cs §5, Artist.Reader.cs,
Show.UI.cs, Detail.UI.cs, Track.Table.Chrome.cs §6, Entities.cs (`Table.Failed`), Fetch.cs,
Entities.Fake.Library.cs}` and `Platform/Platform.cs`, per `docs/plans/wavee/library-rework-implementation.md`
§§5-10. Eleven changes, all against the chapter as it stood before this entry:

30. *superseded* — **§0 #3.** The title row now also carries the row count (14/400 `TextTertiary`, bound to the
    shape's `Count`) beside the `PageHero`; the 0.2.9 anatomy never had one.
31. *superseded* — **§0 #5.** The view/size codes stay, but the one-pill-plus-flyout control (`LibrarySortView.cs`)
    is gone: a plain list/grid icon pair (`User.ViewToggle`) plus a trimmed `ViewPanel` (compact + S/M/L only). The
    artists view's second, independent copy of the old control — over the discography column — is deleted with the
    column.
32. *noted* — **§0 #8.** The prototype's art-wash cover treatment was evaluated against the accent-neutral rule and
    recorded as **not built**; the rule itself is unchanged.
33. *new* — **§0 gained #19-23.** The artists view is two panes (navigator + `Artist.Reader`, not three); sort is a
    word rail (`LibraryWordRail`, persisted `LibraryNavSort` codes 0-5, with **5 = `Albums`** newly appended for the
    artists rail, by saved-album count desc then title); alphabetical sort adds letter groups + an A-Z jump strip
    (`LibraryLetters`); `Album.Pane` gets a real four-state readiness (`AlbumPaneReadiness`: `Header` · `Rows` ·
    `Failed` · `Ready`), closing W24's failure arm for good, with **no `MinifiedAlbum` notice** on this surface; and
    a new persisted key `library.<kind>.scope` joins DATA GAP 8's twelve. `library.<kind>.album.{desc,view,size}`
    (the deleted discography column's own keys) are now **orphaned but still persisted** — read by nobody, left
    unmigrated in `Platform.Settings`.
34. *superseded* — **W2 (Artists, wide, three columns).** The discography column is deleted, not hidden. Kept for
    the record; replaced by **W28** (navigator + `Artist.Reader`, wide) and **W29** (the reader, scope = all
    releases, catalogue blocks landing).
35. *new* — **W30 added.** `Album.Pane`'s four readiness states, illustrating item 33's `AlbumPaneReadiness` and
    closing the "no error state on the browse side" gap W24 named.
36. *deleted, kept for the record* — **W8 (discography pane skeleton).** The column it painted no longer exists;
    its replacement's loading state is a per-block `Skel.Region`, not a whole-pane gate (see W29).
37. *superseded* — **W9.** Two placeholders, not three: the third ("Select a release", the discography column's
    tracks pane) is gone with the column. The surviving albums/artists placeholders and their transience rule (§0
    #16) are unchanged.
38. *superseded* — **W14-W17 depths.** Artists' browse `maxDepth` drops 2 → 1 (`Artist.Reader` is one pane, so W16
    no longer exists in browse); albums/podcasts stay at `maxDepth = 1` (W17, unchanged). Search mode is untouched —
    artists SEARCH keeps depth 0/1/2 while artists BROWSE keeps 0/1.
39. *new* — **§7 gained `AlbumPaneReadiness`'s table rows and `Artist.Reader`'s demand rules**, closing "readiness
    has no failure arm on the browse side" for the album pane. `Album.Notice`'s only reader on this surface is
    deleted; the column and DATA GAP 6 itself are untouched elsewhere.
40. *new* — **§8 gained five pure rules** with their tests: `LibraryLetters` (`LibraryLettersTests`),
    `LibraryWordRail` + `LibraryNavSort.Albums` (`LibraryWordRailTests`), `AlbumPaneReadiness`
    (`AlbumPaneReadinessTests`), `Artist.ReaderShape` (`ArtistReaderShapeTests`), and `Table.Failed` /
    `Fetch.MarkFailed` (no dedicated test file — covered by `AlbumPaneReadinessTests`' `Failed` cases).
41. *settled* — **§9's "`Album.Page.cs` must carry two components".** Not two components in one file:
    `Album.Pane.cs` (named partial, owner M) and `Artist.Reader.cs` (owner N) — not `Artist.DiscographyPane` inside
    `Artist.Page.cs`. Closes both that item and the "three owners, one screen" bullet beneath it.
