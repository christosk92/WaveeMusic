# Shared detail frame (hero, context band, rail, trailing, skeleton, reveal) used by album / playlist / liked / show / prerelease - 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Detail/DetailPage.cs` (757) · `DetailShell.cs` (858) · `DetailVerticalHero.cs` (579) · `DetailVerticalLayout.cs` (662) · `DetailLayoutBreakpoints.cs` (88) · `DetailHeaderMergeRules.cs` (39) · `DetailSkeleton.cs` (215) · `DetailRevealRamp.cs` (27) · `DetailNoticeBar.cs` (89) · `DetailRail.cs` (607) · `DetailRailPolicy.cs` (67) · `DetailTrailing.cs` (709) · `ContextBand.cs` (398) · `ContextBandLayout.cs` (179) · `HeroCta.cs` (23) · `DetailConfig.cs` (311) · `DetailOwnerIds.cs` (58) · `DetailLiveRefresh.cs` (111) · `PlaylistPageNoticeRules.cs` (94) · `PlaylistListState.cs` (40) · `PreReleaseDerivation.cs` (43) — plus the frame-owned ~620 lines inside `DetailTracks.cs` (4525): the vertical hero/chrome roots, the trailing body, the sticky band wiring, the skeleton and the reveal ramp. Also consulted: `Features/Shell/ContentHost.cs` (322), `Design/CoverPaletteLeaves.cs` (265), `Features/Shell/ShellMaterialLayer.cs` (133), `Design/WaveeCta.cs` (243). **Frame total ≈ 5,780 + 620 = ~6,400 lines.**
> 0.3 target: **Settled (A1, 2026-09-12).** §2's tree carries `Entities/Detail.cs` (CORE, 900: breakpoints, vertical-layout arithmetic, rail policy, notice rules, reveal ramp, header merge, `ContextBandLayout`, the per-kind config table) + `Entities/Detail.UI.cs` (UI, 2,600: `Frame`, `Hero`, `Rail`, `CompactRail`, `ContextBand`, `Skeleton`, `NoticeBar`, `TonePlane` — static functions over handles, **no type hierarchy, no `DetailModel` record**), composed by `Album.Page`, `Playlist.Page`, `User.Page` (liked) and `Show.Page`.
> **Owner: M — in a dedicated single-owner slot at the END of Wave 4 ("Wave 4.5"), gated before Wave 5 opens** (§9). Plan §5's Wave 5 table hands out `Album.*`/`Show.*` (M), `Playlist.*`/`User.*` (O), `Artist.*` (N) and names no shared frame at all, and plan §2's tree gives it no line — so as written Wave 5 begins with five pages whose common frame does not exist. It is not two owners' file to share: **three of Wave 5's five owners consume it** — M (`Album.Page`, `Show.Page`), O (`Playlist.Page` incl. the `local` route, `User.Page` = Liked; all six route families reach the one scaffold, `ContentHost.cs:170-172`, `DetailPage.cs:264`, `:284`) and N (the artist page reads the band half: `ArtistCompactBar.cs:48,78,100`, `ArtistPage.cs:232,321,328,338`). Two owners cannot both author one frame; it must land once, before any of the three pages starts.

**Cross-references** (specify only how this surface *configures* them): `00-design-system.md` for every `Tok.*` / `Spacing` / `Radii` / `Elevation` / `WaveeType` / `WaveeMotion` value and the cover-palette functions; `01-track-row.md` for the row, the selection bar and the drag chip; `02-cards-and-controls.md` for `MediaCard.Row`, `StatTile`, `SaveButton`, `FollowButton`, `FlipCountdown`, `PreReleaseCountdown`, `ArtistFacePile`, `CollaboratorFacePile`, `RichText`; `04-detail-track-table.md` for the track table, its chrome/toolbar, the tier column sets and the list itself; `05-album.md` / `06-playlist.md` / `07-liked-songs.md` / `09-show-episode-module.md` for the per-kind bodies that plug into this frame (they read the config table in §8); `08-artist-and-discography.md` for the *other* consumer of `ContextBand` / `ContextBandLayout`; `18-shell-frame.md` for `ContentHost`, `KeepAlive`, the page transition and `ShellMaterialLayer`; `19-shell-overlays.md` for `InfoBar`, `Toast` and `MenuFlyout`.

**Doc drift** (code wins, one line each): `detail-resize-flicker-fix-proposal.md` cites a tier-keyed list remount (`"list:t"+tier`) — the tier is **no longer** in the list key (`DetailTracks.cs:1074-1080`), and `staggerColdRealize` is now hard `false` (`:886`); `about-this-release-facts-implementation.md` proposes a `GridEl` with two star columns — the shipped panel is a flex row of two tiles plus a full-width third (`DetailTrailing.cs:230-251`); `handoff-20260911-connect-detail.md` and `device-reopen-and-visual-fixes-implementation.md` describe playback/audio work with no bearing on this surface beyond `DetailTrackTableRules.ArtSizeFor` (chapter 04).

**Source-comment drift** (the code's own comments, same rule — code wins): `DetailConfig.cs:16` says "Single is the Album path with ≤2 tracks — resolved post-load" — it is `ReleaseKind == AlbumKind.Single`, resolved in `DetailPage.ResolveConfig` (`:331-342`), and **EP shares the Album config** (`:340`); `DetailConfig.cs:222-223` advertises the show rail as "cover · PODCAST pill · title · publisher/episode-count meta" — **no rail arm renders a show's meta line** (see W22); `DetailShell.cs:195-196` names the splitter's floor `SnapThreshold`, but the shipped option record is `{Min, Max, ForcePush, ReExpand}` (`Splitter.cs:60-73`) and has no such field.

**Dead code in the frame — do NOT port** (verified by repo-wide grep): `Design/HeroCta.cs` (23 lines, the whole file) has **zero callers** — every detail CTA goes straight to `WaveeCta.Play` / `WaveeCta.Accent`; `DetailRail.BilledArtists` (`:503-556`, ~54 lines) is unreferenced, superseded by `ArtistFacePile`; `DetailConfig.CapTitle` has no reader; `TwoColumn: false` has no literal (`DetailShell.cs:475-477` is unreachable); hero-only mode is a `const bool heroOnly = false` (`DetailShell.cs:565`).

---

## 0. The non-negotiables

1. **The page has ONE art-derived ground, and it is mostly Mica.** A flat `WaveePalette.PageTone` plane at **α 0.20 dark / 0.30 light** behind everything (`Design/CoverPaletteLeaves.cs:139`), cross-fading over **250 ms** when the grading lands (`:115`). Nothing samples the cover at page scale — no blurred artwork band, ever (`CoverPaletteLeaves.cs:59-73` is that feature's tombstone, and `DetailPageToneTests` asserts its absence).
2. **The cover never flashes.** The model is published through `ImageSource.PreferVisible` on the initial merge *and* on every live refresh (`DetailPage.cs:142`, `:203`), so the same art at a different CDN size hash keeps the rendition already on screen. One cover id per route from first paint on.
3. **The loading band is the loaded band.** The vertical skeleton composes the hero from the *same* pure resolver at the *same* sizes (`DetailSkeleton.cs:39-130` ← `DetailVerticalLayout.HeroBandHeight`), so content arriving never shoves the toolbar, the column header or the rows (D49).
4. **The rows never arrive in one frame.** A cold list reveals 12 real rows per frame (`DetailRevealRamp.cs:11`), each crossing cross-fading over 280 ms `FluentDecelerate` (`DetailTracks.cs:2810-2813`); only slots whose *first* render was a placeholder animate.
5. **Resize never flips twice.** Every width decision is hysteretic: mode 820/660 ± 24 (`DetailLayoutBreakpoints.cs:8`,`:75-87`), vertical 540 enter / 580 exit (`:59-60`), hero flow 424 enter / 400 leave (`DetailVerticalLayout.cs:47,50`), title size ±2 steps up / 1 step down (`:503-510`). A grip parked on a seam cannot chatter.
6. **The sticky header is typography, not a plate.** The 56-DIP context band paints **no fill at all** (`ContextBand.cs:71-96`); content is clipped at its lower edge (`DetailVerticalLayout.StickyClipInset` = 56 + 36 + 1 = **93**) with a 24-DIP feather, so what shows behind the words is the page's own tone.
7. **One hairline, at the bottom of the whole stuck stack** — the track table's own column-header divider (`DetailTracks.cs:2444-2450`, `Tok.StrokeDividerDefault`), never a second seam between the identity row and the column row.
8. **The hero title is a measured plan, not a rung.** Size/lines/min-size are solved against the cover's own height budget and the string's own advance (`DetailVerticalLayout.cs:451-494`); a short album title grows to fill the cover (e.g. "Pony" → 88/117 at a 1000-DIP column), a long one drops to 52 or takes two lines.
9. **The rail is a user-owned column.** Drag 180…480, a resist zone below 180 that fades the rail's content to 0.35, a collapse detent at ~136 raw, re-open at 220 — and four independent persisted scopes (album / playlist / liked / show) plus a fifth "uniform" one (`DetailShell.cs:200-206`, `DetailRailPolicy.cs:25`).
10. **A collapsed rail is never gone.** It becomes a 96-DIP identity strip with a real cover, a two-line title and a chevron (`DetailRail.BuildCompact`, `DetailShell.cs:205`) — WP-κ.
11. **Late rows fade up; they never blink in.** Every conditional hero/rail row is keyed and carries `FadeUp` + a position-only `Shove` FLIP (`DetailRail.cs:52-70`), so the eyebrow/meta/description landing with the full model pushes its siblings from their old origin.
12. **Bad news is a strip, never an error page.** Deleted / access-revoked / create-failed / minified keep the rows on screen and add one Informational `InfoBar` between the header and the list (`DetailNoticeBar.cs:45-88`); the full album page suppresses the minified verdict because it self-heals (`DetailShell.cs:600`, `:669`).
13. **The page's colour reaches the window chrome.** A cover-keyed leaf publishes a flat tint into `ShellMaterial` (`CoverPaletteLeaves.cs:242-246`) — dark `TintedDark(scheme) @ 0.14`, light `Lift(TextBase) @ 0.05` — as a *hand-over*, never a clear, so two coloured pages never dip through neutral.
14. **The hero cover is the page's anchor and never animates.** No entrance, no morph, no connected fly (`DetailRail.cs:143-144`, `DetailShell.cs:235-256`); only the copy arrives in sequence.
15. **Everything above the fold is reachable with no data.** A deep link with no nav preview still composes the real responsive shell against `PendingSeed` and shimmers it (`DetailPage.cs:268-291`, `:346-379`).
16. **The vertical arm has NO scroll-edge cue, in either list.** Both the virtual list (`DetailTracks.cs:1520-1527`) and the trailing scroller (`:1841`) set `EdgeCues = ScrollEdgeCues.None` when `_verticalHeader` is on, because the stock surface-colour top cue resolves its colour by an ANCESTOR walk — which sails past the tone plane (a ZStack *sibling*) to the shell's neutral ground and lands a one-rung-off opaque slab exactly over the unpainted band. **The band's clip plus its 24-DIP feather IS the "more content above" cue.** Any 0.3 band that re-arms the stock cue undoes non-negotiable 6. (The two-column arm keeps `ScrollEdgeCues.Auto` — it has no band.)
17. **The context band exists in the vertical arm only.** `ContextBand.Row` is reached from exactly one place, `DetailVerticalHero.Build` (`:393`); modes 0/1/2 have no sticky identity row, no `compactLeft` gutter and no clip contract — scrolling a two-column page just scrolls the right column under the track table's own column header. Do not generalise the band to the rail arm "for consistency".
18. **Input ownership crosses with the band, not just opacity.** `onStuck` flips `_verticalCompactInteractive`, and that ONE flag drives `HitTestVisible = compactCanHit` + `HitTestPassThrough = true` on the compact band and `HitTestVisible = !compactCanHit` on the expanded presentation (`DetailVerticalHero.cs:70,425,435`). Fading the band in without the handoff leaves the scrolled-away hero eating clicks.

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
ContentHost.PageFor(route)                              Features/Shell/ContentHost.cs:188  route → page
└─ Flow.KeepAlive(PageSlot(tab,route), …, MaxEntries 3) ContentHost.cs:96-108  parks the page for Back
   └─ DetailHost(route)  BoxEl Key="page:detail"        ContentHost.cs:158-162
      └─ DetailPage : Component                         DetailPage.cs:22        route→kind/id, load, owners, live refresh
         ├─ UseMemo preview = navPreview.Take(route)    DetailPage.cs:103       partial model from the card that was clicked
         ├─ UseResource model (Loadable<DetailModel>)   DetailPage.cs:109-159   + cover latch (:137-144) + title merge (:124-125)
         ├─ UseSignalEffect live refresh                DetailPage.cs:171-259   store subscription → DetailLiveRefresh pump
         ├─ [preview path]  Embed.Comp(DetailShell)     DetailPage.cs:263-264   header live on the click frame
         └─ [deep-link path] Skel.Region(model, …)      DetailPage.cs:272-290   FadeOnly, smoothResize:false, ErrorState on Failed
            └─ DetailShell (DeriveRenderedOutput)       DetailPage.cs:284
   DetailShell : Component                              DetailShell.cs:83       ONE scaffold for every kind
   ├─ (state) _mode 0..3, _sort, _query, _filters,      DetailShell.cs:95-128
   │  _density, _tempoColumn, _playsColumn, _multiSelect,
   │  5 × RailPrefs (width+collapsed), _railFade, _railUniform,
   │  _verticalHeroHeight (published up by TrackList)
   ├─ handlers: DetailHandlers trampolines              DetailShell.cs:407-448  mount-stable identity, live closures
   ├─ ZStack root  OnBoundsChanged=Measure              DetailShell.cs:602-612 (vertical) / :677-688 (two-column)
   │  ├─ tintBinder  CoverPaletteLeaves.ShellTint       DetailShell.cs:321-323  0×0 leaf → shell material
   │  ├─ DeferWatcher  PlaylistReorderDeferWatcher      DetailShell.cs:694-695  0×0 leaf
   │  ├─ tonePlane   CoverPaletteLeaves.PageTonePlane   DetailShell.cs:575-577  the page's ground
   │  └─ page
   │     ├── [mode 0/1/2 — TWO-COLUMN]  BoxEl Key="detail:two-column"      DetailShell.cs:657-676
   │     │   ├─ DetailNoticeBar.For(model, showMinifiedAlbum:false)        DetailShell.cs:669  → DetailNoticeBar.cs:42
   │     │   └─ centering row (Justify=Center)  →  row MaxWidth 1600       DetailShell.cs:639-656
   │     │      ├─ railFaded  (Opacity ← _railFade)                        DetailShell.cs:779-789
   │     │      │  └─ DetailRail.Build(m,cfg,h,railW,titleSize,lh,descLines,model,acts)   DetailRail.cs:122-299
   │     │      │     ├─ rail:cover      cover=railW−24, Radii.Card, Elevation.Card, Draggable  DetailRail.cs:145-162
   │     │      │     ├─ rail:eyebrow    LateRow · EyebrowRun, Width=cover  [Badges==TypeYear && text≠""]  :167-171
   │     │      │     ├─ rail:owner      LateRow · PlaylistOwnerBlock / CollaboratorFacePile
   │     │      │     │                  [Badges==OwnerRow && OwnerName≠""] — no name ⇒ NO row at all  :172-175
   │     │      │     ├─ rail:title      Row · DetailHero / PlaylistInlineEdit.Title, MaxLines 3   :183-189
   │     │      │     ├─ rail:artists    LateRow · ArtistFacePile [TypeYear && Artists.Count>0]  :193-194
   │     │      │     ├─ rail:meta       LateRow · MetaRow (+ shimmer when !MembershipLoaded)
   │     │      │     │                  [Badges != TypeYear] ⇒ playlist/liked ONLY; album AND SHOW have no
   │     │      │     │                  rail meta line (albums use the facts bento)  :199-200, :77-94
   │     │      │     ├─ rail:daylist    LateRow · FlipCountdown compact [ExpiresAtMs>0]  :204, :465-469
   │     │      │     ├─ rail:chart      LateRow · ChartCaption [ChartNewEntries>0]  :207, :474-480
   │     │      │     ├─ rail:cta        Row(Shove) · PlayPill + [Save, Share, OwnerMenu] 40-DIP FABs  :212-240
   │     │      │     ├─ rail:prerelease LateRow · PreReleaseCountdown [UpcomingAt≠null]  :242, :456-460
   │     │      │     ├─ rail:release    Row · AlbumTrailing.ReleasePanel(outerPadding:false)
   │     │      │     │                  [TypeYear && HasReleasePanel]  DetailRail.cs:244-245
   │     │      │     ├─ rail:desc       LateRow · RichText.Of / InlineEdit.Description
   │     │      │     │                  [descMaxLines>0 && (editable || Description≠"")]  :249-253
   │     │      │     ├─ rail:likedfacts Row(not LateRow — the panel owns its own entrance)
   │     │      │     │                  [LikedFacts.Has(m, cfg.Badges)]  DetailRail.cs:259-260
   │     │      │     └─ ScrollView wrapper (Fill=FillLayerDefault, except Liked)  DetailRail.cs:262-298
   │     │      ├─ [collapsed] DetailRail.BuildCompact(m, 96, expand)      DetailRail.cs:304-354
   │     │      ├─ DetailRailGrip → Splitter.Create(width, commit, opts)   DetailShell.cs:797-817
   │     │      └─ right  BoxEl Key="right:tracks" | "right:eps"           DetailShell.cs:525-549
   │     │         ├─ TrackList (chapter 04)   Key=(vertical?"tracks:vertical:":"tracks:standard:")+route  DetailShell.cs:545
   │     │         │  ├─ chrome (toolbar + chips + lens + column header)   DetailTracks.cs:1857-1920
   │     │         │  └─ list / TrailingBody(listKeyed,null,null,inset)    DetailTracks.cs:1801-1846
   │     │         │     └─ AlbumTrailing (HasTrailing only)               DetailTrailing.cs:25-107
   │     │         └─ EpisodeList (Content==Episodes)                      DetailShell.cs:529
   │     └── [mode 3 — VERTICAL / HERO SYSTEM]  BoxEl Key="detail:vertical"  DetailShell.cs:592-601
   │         ├─ DetailNoticeBar.For(model, showMinifiedAlbum:false)        DetailShell.cs:600
   │         └─ verticalContent (ClipToBounds, DropTarget)                 DetailShell.cs:583-589
   │            · showToolbar = Content==Tracks || mode!=Vertical          DetailShell.cs:518
   │              ⇒ a SHOW in the vertical arm renders its episode list with NO toolbar
   │            ├─ [Episodes] DetailRail.BuildHeader(m,cfg,h,model,acts)   DetailRail.cs:361-441
   │            └─ right → TrackList(verticalHeader:true, …)               DetailShell.cs:537-547
   │               ├─ [playlist/liked] VerticalList  ItemsView.CreateBound DetailTracks.cs:1463-1539
   │               │  · PersistentPrefixCount 2, ItemClipTopInset=inset, ItemClipTopFadeBand 24
   │               │  · EdgeCues None, AutoEdgeFade false (non-negotiable 16) :1519-1527
   │               │  · SelectionMode = visible>0 ? cfg.Selection : None  :1499
   │               │  · ScrollKey = route.Name + ":r" + _resetEpoch       :1521
   │               │  · item 0 Hero   → row.Collapse(heroH, 56, cd)        DetailTracks.cs:1490-1492
   │               │  · item 1 Chrome → row.Sticky(56, onStuck)            DetailTracks.cs:1493-1494
   │               │  · items 2..n-1  ExpandableTrack · last (opt) Footer  DetailVerticalLayout.cs:621-630
   │               └─ [album/show]   TrailingBody(list, VerticalHeroRoot, VerticalChromeRoot, inset)  :1801-1846
   │                  · hero root  .Collapse(heroH, 56, cd)                DetailTracks.cs:1786-1790
   │                  · chrome root .Sticky(56, …)                         DetailTracks.cs:1792-1796
   │                  · body ScrollBinds ClipTopAtViewport(inset) + EdgeFade(Top,24)  :1815-1826
   │                  · body children, IN ORDER: list · [vertical only] PreReleaseCard ·
   │                    [vertical only] AlbumTrailing.ReleasePanel(outerPadding:true) ·
   │                    AlbumTrailing                                      DetailTracks.cs:1806-1813
   │                    (the hero arm has no rail, so the countdown + the About-this-release
   │                     bento move BELOW the rows — they are not dropped)
   │               └─ DetailVerticalHero.Build(...)                        DetailVerticalHero.cs:61-447
   │                  ├─ expandedPresentation (ScrollBinds TransY/Opacity) :432-445
   │                  │  └─ expanded
   │                  │     ├─ hero box  [artworkBox | identity]           :323-330 (Animate=HeroReflowMotion)
   │                  │     │  ├─ artworkBox (Radii.Card, Elevation.Card, sat 1.18, Draggable)  :107-132
   │                  │     │  └─ identity column (Gap=IdentityGapFor, MinHeight=art in row flow)  :295-302
   │                  │     │     hero-eyebrow · hero-title · hero-rule · hero-attribution ·
   │                  │     │     hero-meta · hero-pulse · hero-chart · hero-actions · hero-description  :186-269
   │                  │     └─ toolbar host (pad compactLeft / 8 / 4)      :345-352
   │                  └─ compactIdentity  ContextBand.Row(viewportW, compactLeft, …)  :369-430
   │                     ├─ normal arm: [identity block | spacer | Find·Filter·Play]  :369-398
   │                     └─ selection arm: SelectionCommandBar             :401-410
   └─ (parked) KeepAlive entry for Back
```

### 1.2 The same tree in 0.3 terms

Data reaches a child only through a `Signal`/`Func`, `Ctx.Provide`+`UseContext`, or a `Key` remount — **every mount below says which**.

| 0.2.9 node | 0.3 file · symbol | inputs | how data reaches it |
|---|---|---|---|
| `DetailPage` (route→load→model) | *dissolved* — each page owns its own demand | `Album a` / `Playlist p` / `User me` / `Show s` handle | `Album.Page(a)` etc. `UseEffect` → `Entities.Ensure(a, AlbumFields.All)` + `EnsureEdges` + `EnsureRows` (plan §4.13) |
| `DetailShell` (scaffold) | `Entities/Detail.UI.cs` · `static Element Detail.Frame(in FrameSpec s)` | `FrameSpec` struct: subject `EntityUri`, `DetailConfig cfg`, `Signal<int> mode`, handles, action delegates | **static function, re-invoked every page render** — no frozen props at all. The page owns the signals. |
| `DetailHandlers` (23 delegates) | `Detail.Actions` readonly struct | same shape, `Shell.ActionId` table for the menus (plan §4.11) | passed **by value into a static function**, so it never freezes; `Accent` becomes `Signal<ColorF>` |
| `DetailRail.Build` | `Detail.UI.cs` · `static Element Rail(in FrameSpec, float railW, float titleSize, float lh, int descLines)` | handle + `Signal<float> railWidth` | static; the rail row bodies that are still `Component` (SaveButton, FlipCountdown, ArtistFacePile, CollaboratorFacePile, PreReleaseCountdown, PlaylistInlineEdit.*) keep **Key = identity** remounts — the complete list of keyed remounts in this frame: `save:{uri}` (`DetailRail.cs:233`, `PlayRow` `:494`), `vhero-save:{uri}` (`DetailVerticalHero.cs:250`), `daylist:{uri}:{expiresAtMs}` (`:468`), `prerelease:{uri}:{utcTicks}` (`:459`), `vhero-more:{ctxUri}` (`DetailVerticalHero.cs:253`), **`pl-collab:{(int)width}`** — a WIDTH-keyed remount of `CollaboratorFacePile`, in both the rail (`DetailRail.cs:560`, width = cover) and the hero (`DetailVerticalHero.cs:475`, width = contentW) — and `trail:{signature}` (`DetailTrailing.cs:488`) |
| `DetailRail.BuildCompact` | `Detail.UI.cs` · `static Element CompactRail(handle, 96f, Action expand)` | handle + `Action` | static |
| `DetailRail.BuildHeader` | `Detail.UI.cs` · `static Element ShowHeader(Show s, …)` | handle | static (episodes arm only) |
| `DetailVerticalHero.Build` | `Detail.UI.cs` · `static Element Hero(in FrameSpec, in HeroGeom g, in BandSlots slots)` | `HeroGeom` = the pure plan from `Detail.cs`; `BandSlots` = the four already-built band elements | static; `Signal<bool>` for `compactInteractive` / `searchExpanded` / `selectionVisible` |
| `ContextBand.Row/Title/Byline` | `Shell/Rail.UI.cs`? **No** — keep in `Detail.UI.cs` · `Detail.Band(...)`, and let `Artist.Page` call the same three helpers | `float width`, `float gutter`, `Element[]` | static |
| `DetailNoticeBar` (Component) | `Detail.UI.cs` · `static Element NoticeBar(DetailNotice n, bool showMinified, Action goLibrary)` | `n` read from the **model** each render | static; the notice is a *column* on the entity (see §7), so the page re-renders on `Changed` and the strip appears/clears with it |
| `DetailSkeleton.VerticalHeroBand` | `Detail.UI.cs` · `static Element HeroSkeleton(in HeroGeom g, Func<float,Element>? previewArt)` | the same `HeroGeom` | static |
| `CoverPageTonePlane` (Component, `UseProps`) | keep a Component — it owns a `Watch` subscription | `Props(url, fallbackUrl, disabled, heroBand, pageHeight, heroOnly)` | **`Embed.Comp(props, factory)` + `UseProps`** (props re-pushed, never frozen) + `Key = "detail-tone:"+route+":"+theme` |
| `CoverShellTintBinder` (Component) | keep a Component (owns `Watch` + `UseActivation`) | `Props(url, fallbackUrl, ready, disabled, apply, owner, slot)` | `UseProps` + `Key = "detail-tint:"+route` |
| `TrackList` (Component) | chapter 04 | | |
| `AlbumTrailing` (Component) | `Album.Page.cs` (album-only) | | `Key = "trail:"+signature` remounts on a data-signature change (`DetailTrailing.cs:482-489`) |
| `DetailLiveRefresh` pump | **deleted** — no re-projection exists in 0.3 | — | the table's `Changed` signal is the refresh (plan §4.1) |
| `DetailOwnerIds` | **deleted** — no store-change predicate | — | edges publish per-table |
| `DetailHeaderMergeRules` | `Detail.cs` CORE — still needed (see §7 DATA GAPS: rolling identity) | | |

**Props-freeze checklist for the re-author**: the 0.2.9 frame has exactly five frozen-prop hazards, and all five were solved, not avoided — copy the solutions. (a) `DetailShell`'s `DetailHandlers` is mount-stable with *trampolines* into a per-render box so a frozen copy still calls this render's closures (`DetailShell.cs:395-448`) — in 0.3 a static function needs none of this. (b) `SaveButton`'s uri freezes → keyed on the target (`DetailRail.cs:233`, `DetailVerticalHero.cs:250`). (c) `FlipCountdown`/`PreReleaseCountdown` freeze their window → keyed on the window (`DetailRail.cs:459`, `:468`). (d) `ContextPivot` re-pushes `Props` via `UseProps` because sections arrive after mount (`ContextBand.cs:184-188`). (e) `TrailingStack` freezes `count`/`rowAt` → keyed on a data signature (`DetailTrailing.cs:481-489`).

---

## 2. Wireframes

Scale: **1 monospace char ≈ 8 DIP**. All numbers are DIP unless marked.

### W1 - two-column, mode 0 (wide), album, fully loaded @ page width 1280

Mode 0 because 1280 ≥ 820 (`DetailLayoutBreakpoints.cs:68`). Rail = `cfg.RailWidth` 280 (album) unless the user dragged it (`DetailShell.cs:629`). Row is centred with `MaxWidth 1600` (`DetailShell.cs:649`).

```
◀── page 1280 ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────▶
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ (notice strip: 0 height when Notice==None — DetailShell.cs:669)                                                                               │
├─ rail 280 ──────────────────────┬16┬─ right column  (Grow 1, MinWidth 300)  ────────────────────────────────────────────────────────────────┤
│ pad 16 L / 8 R / 24 T / 24 B    │gr│  chrome (toolbar 44 + chips? + lens? + column header 36 + hairline 1)                                  │
│ ┌────── cover 256 ───────────┐  │ip│  ┌──────────────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │  Radii.Card 8              │  │  │  │ [▶ Play next ▾] [⇄ Shuffle] │ [↕ Sort] [≣ Row size] [☑ Select] [⋯]        [ 🔍 Find … 240 ]     │ │
│ │  Elevation.Card            │  │  │  └── CommandBarSurface h=44, pad 6/5 ───────────────────────────────────────────────────────────────┘ │
│ │  saturation 1.18           │  │  │  #   TITLE                                     ALBUM              ♥     ⏱                            │
│ │  decodePx 256              │  │  │ ──────────────────────────────────────────────────────── 1 DIP StrokeDividerDefault ─────────────── │
│ └────────────────────────────┘  │  │  1   Give Life Back to Music        Daft Punk    Random Access…   ♡   4:35                           │
│ ↕14 gap (DetailRail.cs:264)     │  │  2   The Game of Love                            Random Access…   ♡   5:22                           │
│ ALBUM · 2013            eyebrow │  │  3   Giorgio by Moroder                          Random Access…   ♥   9:04                           │
│ ┌ Random Access ─────────────┐  │  │  …                                                                                                    │
│ │ Memories        40/52/600  │  │  │                                                                                                       │
│ └ (winH≥900 ⇒ 40, else 28)───┘  │  │                                                                                                       │
│ ●●+2  Daft Punk      facepile   │  │                                                                                                       │
│ [ ▶ Play  36 ]  (◯40)(◯40)(◯40) │  │                                                                                                       │
│   accent capsule   ♡   ↗   ⋯    │  │                                                                                                       │
│ About this release   (eyebrow)  │  │                                                                                                       │
│ ┌ Songs ─┐ ┌ Length ┐           │  │                                                                                                       │
│ │  13    │ │ 74 min │           │  │                                                                                                       │
│ └────────┘ └────────┘           │  │                                                                                                       │
│ ┌ Released ───────────────────┐ │  │                                                                                                       │
│ │ May 17, 2013                │ │  │                                                                                                       │
│ └─────────────────────────────┘ │  │                                                                                                       │
│ Label: Columbia   (11px note)   │  │                                                                                                       │
│ ℗ 2013 Daft Life Ltd.           │  │                                                                                                       │
│ ▽ rail ScrollView (hidden bar)  │  │  ── AlbumTrailing (HasTrailing) scrolls with the rows ──                                              │
│    Fill = FillLayerDefault      │  │  About the artist · Fans also like · More by · Featured on · Merch · Similar albums                   │
└─────────────────────────────────┴──┴───────────────────────────────────────────────────────────────────────────────────────────────────────┘
     cover = railW − SidePadL 16 − SidePadR 8 = 256          (DetailRail.cs:96)
```

### W2 - two-column, mode 1 @ page width 700 · W3 - mode 2 @ page width 600

`RailW(mode,cfg)` = `{0→cfg.RailWidth, 1→224, 2→188}` (`DetailShell.cs:192`); persisted widths and the collapse flag are ignored outside mode 0 (`DetailRailPolicy.cs:28`).

```
W2 @ 700  (700 ≥ 660 ⇒ mode 1)              W3 @ 600  (600 ≥ 560 ⇒ mode 2; holds down to 540)
┌ rail 224 ──────────┬16┬ right ≥300 ─────┐ ┌ rail 188 ──────┬16┬ right ≥300 ────────────┐
│ cover 200          │gr│ chrome           │ │ cover 164      │gr│ chrome                  │
│ eyebrow            │  │ # TITLE  ♥  ⏱    │ │ eyebrow        │  │ # TITLE ♥ ⏱             │
│ title 28/36        │  │ rows…            │ │ title 28/36    │  │ rows…                   │
│ facepile           │  │                  │ │ facepile       │  │                         │
│ [▶ Play] (◯)(◯)(◯) │  │                  │ │ [▶ Play]       │  │                         │
│  (wraps as a unit) │  │                  │ │ (◯)(◯)(◯) wrap │  │                         │
└────────────────────┴──┴──────────────────┘ └────────────────┴──┴─────────────────────────┘
 cover = 224−24 = 200                          cover = 188−24 = 164
```

### W4 - rail collapsed (WP-κ) @ any width, mode 0 only

`railCollapsed = rail.Collapsed && resizableRail` (`DetailShell.cs:628`); children become `[compact, grip(20), right]` (`DetailShell.cs:764-769`).

```
┌ 96 ─┬20┬ right ───────────────────────────────────────────────┐
│ p8  │gr│ chrome + rows (Key unchanged ⇒ no scroll reset)      │
│ ┌80┐│ip│                                                      │
│ │▦ ││20│  cover = max(48, 96 − 8 − 8) = 80   DetailRail.cs:307│
│ └──┘│  │  click cover OR chevron ⇒ expand (persists)          │
│ Rand│  │                                                      │
│ Acce│  │  title 12/600, MaxLines 2, width = cover             │
│ ⋮   │  │  spacer Grow 1                                       │
│ [ › ]  │  28-high chevron row, HoverFill FillSubtleSecondary  │
└─────┴──┴──────────────────────────────────────────────────────┘
 strip Fill = FillLayerDefault, pad 8/16/8/16, gap 8, ToolTip = title
```

### W5 - rail grip drag, resist zone

`Splitter.Create(rail.Width, commit, {Min 180, Max 480, ForcePush 44, ReExpand 220}, collapsed, fade)` (`DetailShell.cs:804-814`). Strip 16 wide at rest (`Splitter.StripW`, `Splitter.cs:22`), 20 while collapsed (`DetailShell.cs:206`). Below Min the rail tracks nothing and its **content opacity** rides `_railFade` down to `MinFade 0.35` over `FadeDistance 44` (`Splitter.cs:66-69`); pushing 44 past the floor (raw ≈ 136) collapses.

```
         180 (Min — "SnapThreshold" is prose only)  220 (ReExpand)  480 (Max)
 raw ─────┼──────────┼──────────────────────┼────────────────────┼────▶
      136 │  resist  │      1:1 tracking                          │
   collapse│ fade 1→0.35                                          │
          ▲ indicator: 2-DIP rounded bar, reveal-on-hover (Splitter.cs:154-166)
```

### W6 - vertical / hero system, ROW FLOW @ page width 520

Mode 3 because 520 < 540 (`DetailLayoutBreakpoints.cs:80`). Row flow because 520 ≥ 424 (`DetailVerticalLayout.cs:47`). Computed (bucket 520): pad 24, gap 24, art = round(clamp((520−48−24)·0.44, 144, 240)) = **197**, content = min(640, 520−48−24−197) = **251**.

**The title plan at this width, solved end to end** (this is the worked example the re-author checks `TitleTypeFor` against — the numbers below are the algorithm, not a rung):
`titleW` = `TitleWidthFor(520,row)` = min(1000, 251) = **251** · chrome (eyebrow 16 + rule 4 + attribution 16 + meta 16 + actions 40) = 92 over 6 blocks ⇒ `heightBudget` = 197 − 92 − 5·4 = **85** · `FluidTitleCapFor(520)` = 0.0919·520 − 5.08 = **42.7**.
· **"Random Access Memories"** (advance 11.97 em, longest word "Memories" 4.48 em): 1 line ⇒ widthFit 19.4, heightFit 63.9 → 19.4 (below the 20 floor, never wins); 2 lines ⇒ widthFit 39.4, heightFit 31.9 → 31.9 ⇒ **size 32 / lh 43 / 2 lines**.
· **null-title pessimistic plan** (the skeleton's and the pre-measure fallback's): advance starves both width terms ⇒ 1 line at min(heightFit 63.9, cap 42.7) = 42.7 ⇒ snapped **44 / lh 59 / 1 line**.
A 52/69 one-line title is NOT reachable here — the fluid cap alone forbids it below ~1010 DIP of column.

```
┌───────────────── page 520 ─────────────────┐
│ pad 24                                      │
│ ┌── art 197 ──┐ gap24 ┌ identity (Grow 1) ─┐│
│ │  Radii.Card │       │ ALBUM · 2013  16px ││  ← hero-eyebrow (late, FadeUp)
│ │  Elev.Card  │       │ Random Access      ││  ← hero-title 32/43/600 × 2 lines, Display face,
│ │  sat 1.18   │       │ Memories           ││     −20/1000 em, LineHeight prop = NaN
│ │  decode 512 │       │ ▄▄▄▄  20×2 accent  ││  ← hero-rule (Surfaces.AccentRule)
│ │             │       │ Daft Punk  12/600  ││  ← hero-attribution (late)
│ │             │       │ 13 songs · 1 hr 14 ││  ← hero-meta 12/16, MaxLines 2 (late)
│ │             │       │ ┌────────┐(32)(32) ││  ← hero-actions: Play pill 36 + satellites 32
│ │             │       │ │▶ Play  │(32)(32) ││     Gap 8, Wrap true, Margin top 4
│ └─────────────┘       └────────────────────┘│
│ identity MinHeight = art (197) in row flow   │
│ pad-bottom 8                                 │
├──── toolbar host: pad L/R = compactLeft ─────┤
│ [▶][⇄] │ [↕][≣][☑][⋯]        [🔍 240]   h44  │
├──────────────────────────────────────────────┤
│ #   TITLE                        ♥    ⏱   36 │  ← column header + 1 hairline
├──────────────────────────────────────────────┤
│ 1  Give Life Back to Music       ♡  4:35     │
│ 2  The Game of Love              ♡  5:22     │
└──────────────────────────────────────────────┘
 band height = 24 + max(197, identity) + 8 + 8 + 44 + 4     DetailVerticalLayout.cs:552-565
```

### W7 - vertical, STACKED @ page width 380

380 < 400 ⇒ stacked (`RowFlowLeaveW`, `DetailVerticalLayout.cs:50`); 380 < 420 ⇒ `NarrowHeroPad` 16 / `NarrowHeroGap` 16 (`:33-37`,`:123-129`). Bucket 384 (`BucketW` rounds 380/8 = 47.5 to **48** — banker's rounding, `:97-102`): art = round(clamp(384−32, 96, 280)) = **280** (the cap), content = titleW = 352.

**Stacked flow starves the HEIGHT term, not the width one** — `heightBudget: 0` ⇒ `heightFit = +∞`, so the winner is `min(widthFit, FluidTitleCapFor(384) = 30.2)`. For "Random Access Memories" (advance 11.97 em, longest word 4.48): 1 line ⇒ widthFit = min(0.9259·352/11.97, 352/4.48) = **27.2**; 2 lines ⇒ widthFit = min(0.94·2·352/11.97, 78.6) = 55.3, capped to **30.2** — so two lines win and the cap binds. Snapped: `TitleSnapStep(30.2)` = 2 ⇒ **30 / lh round(30·1.3301) = 40 / 2 lines**. A *longer* title here would be width-bound below the cap; the cap is not the only constraint in stacked flow, it just happens to be the binding one for this string.

```
┌──── page 380 ────┐
│ pad 16           │
│ ┌─ art 280 ────┐ │
│ │              │ │
│ │              │ │
│ └──────────────┘ │
│ gap 16           │
│ ALBUM · 2013     │
│ Random Access    │  30/40/600
│ Memories         │
│ ▄▄▄▄             │
│ Daft Punk        │
│ 13 songs · 1 hr… │
│ [▶ Play] (32)(32)│  wraps (Wrap=true) — needs the definite width from AlignItems=Stretch
│ (32)(32)         │
│ description ≤4 ln│  DescriptionMaxLines(false)=4
├──────────────────┤
│ toolbar 44       │
│ # TITLE   ⏱  36  │
└──────────────────┘
 band ≈ 16 + (280+16+identity) + 8 + 8 + 44 + 4 ≈ 531 at this width
```

### W8 - "Track page layout = Hero" at a WIDE window (page 1100)

`settings.Get(DetailPageLayout) == PageHero` forces mode 3 at every width for `Content == Tracks` (`DetailShell.cs:509-511`). Row flow, bucket 1100: art = round(clamp((1100−48−24)·0.44,144,240)) = **240** (capped), content = min(640, 788) = **640**, title cap `FluidTitleCapFor(1100)` = **96**, title width = min(1000, 788) = **788**.

Title plan for "Discover Weekly" (advance 7.51 em, longest word "Discover" 3.95 em): budget = 240 − 92 − 20 = **128**; 1 line ⇒ widthFit 97.1, heightFit 96.2, cap 96 → **96**; 2 lines ⇒ heightFit 48.1, never wins. ⇒ **size 96 / lh 128 / 1 line** (`round(96 · 1.3301)`). 1100 is exactly `CapLockMaxW`, so this is the largest title the app can ever set.

```
┌──────────────────────────────── page 1100 ───────────────────────────────────────┐
│ ┌ art 240 ┐ gap24 ┌ identity Grow1, MinHeight 240 ────────────────────────────┐  │
│ │         │       │ PLAYLIST · Collaborative                                   │  │
│ │         │       │ Discover Weekly                     96/128/600 (real title)│  │
│ │         │       │ ▄▄▄▄                                                       │  │
│ │         │       │ ●●● +4 collaborators      (CollaboratorFacePile)           │  │
│ │         │       │ 30 songs · 18.7M saves · 2 hr 11 min      (wraps to 2)     │  │
│ │         │       │ ┌ fill block Grow 1 — surplus opens HERE ┐                 │  │
│ │         │       │ [▶ Play](32)(32)(32)(32)                                   │  │
│ └─────────┘       │ description ≤3 lines, measure 640                          │  │
│                   └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────────┘
 identity gap = IdentityGapFor(...) — resting 4, widened (even 2-DIP steps) up to 12 when short
```

### W9 - LOADING skeleton, vertical arm (D49)

`VerticalShimmer` = `DetailSkeleton.VerticalHeroBand(...)` + the **real** chrome + `RowsShimmer(12)` (`DetailTracks.cs:2554-2567`). Bars are `Radii 4` / `Tok.FillSubtleSecondary`, breathing 1000 ms / min 0.5 (`SkeletonStyle.Default`).

```
┌──────────────── page 520 ─────────────────┐
│ ┌─ art 197 ─┐  ┌ identity ──────────────┐ │  previewArt: the REAL cover when the nav
│ │ ▒ real    │  │ ▓▓▓▓▓▓▓    eyebrow 32% │ │  preview carried one (.Skel(self) exempts
│ │ preview   │  │ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓  title  │ │  it from the deriver) — else a plain
│ │ cover or  │  │ ▓▓▓▓▓▓▓▓▓  (68% last)  │ │  Radii.Card grey square
│ │ grey card │  │ ▄▄  20×2 rule          │ │
│ │           │  │ ▓▓▓▓▓▓▓▓   attrib 40%  │ │  bar widths: eyebrow .32 · attribution .40
│ │           │  │ ▓▓▓▓▓▓▓▓▓▓▓ meta 62%   │ │  · meta .62 · pulse .35 · title lines .68
│ └───────────┘  │ [104×36]○○○○  actions  │ │  · description lines .55  (min 32 DIP)
│                └────────────────────────┘ │
├───────────────────────────────────────────┤
│ toolbar band 44: [72][88][64] ··· [240]   │  pills at 32, inset 6/5
├───────────────────────────────────────────┤
│ # TITLE ♥ ⏱  (REAL chrome, same origin)   │
├───────────────────────────────────────────┤
│ ▓▓▓ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓        ▓▓▓  ×12 rows   │  RowsShimmer derived from the real row
└───────────────────────────────────────────┘
 MinHeight of the hero block = HeroBandHeight(pessimistic null-title plan)  DetailSkeleton.cs:112
```

### W10 - LOADING, two-column arm

The rail is a **sibling column** of the boundary and the chrome a sibling row, so both are already held open; only the rows shimmer (`DetailTracks.cs:1058-1064`). A cold deep link additionally wraps the whole shell in `Skel.Region(..., FadeOnly, smoothResize:false)` (`DetailPage.cs:272-290`) rendered against `PendingSeed` (8 blank tracks, a blank badge/owner/meta — `DetailPage.cs:346-379`).

```
┌ rail 280 (REAL, from the preview model) ┬gr┬ right ───────────────┐
│ ▦ cover (preview art, never a grey box) │  │ toolbar (real)        │
│ ALBUM · —                               │  │ # TITLE ♥ ⏱           │
│ Random Access Memories                  │  │ ▓▓▓ ▓▓▓▓▓▓▓  ×12      │
│ [▶ Play] ○ ○ ○                          │  │                       │
│ (meta row: shimmer bar shaped as        │  │                       │
│  "00 songs · 0 hr 00 min" while         │  │                       │
│  MembershipLoaded == false)             │  │                       │
└─────────────────────────────────────────┴──┴───────────────────────┘
```

### W11 - REVEAL in progress (ramp)

`_reveal` starts at 12 on the content edge, +12 per frame, `Done` once it reaches `min(visible, 60)` (`DetailRevealRamp.cs:11-22`). Rows past the ramp are **blank grid cells of the exact row extent** — deliberately not grey bars (`DetailTracks.cs:2613-2637`).

```
frame 0            frame 1            frame 2            frame 3
 1 real            1..12 real         1..24 real         all real (Done)
 …                 13 ▁ blank         25 ▁ blank         no per-row cost
 12 rows of the    …                  …
 derived shimmer   24 ▁ blank         36 ▁ blank
 dissolve under    (each crossing: Opacity 0→1, 280 ms FluentDecelerate, once per slot)
```

### W12 - SCROLLED (the merged band), vertical arm

Collapse distance `cd = max(1, heroBand − 56)`. Expanded hero: `TransY 0→−cd` over `[0,cd]`; `Opacity 1→0` over `[cd−96, cd]`. Compact band: `Opacity 0→1` + `TransY 4→0` over `[cd−44, cd]`. Hero root: `PinTop 0` + `PresentedH heroBand→56` over `[0,cd]` (`DetailVerticalHero.cs:436-445`, `:430`, `DetailTracks.cs:1790`).

**The byline is a four-rung fallback, not "owner · meta"** (`DetailVerticalHero.cs:365-368`): `owner + " · " + meta` when BOTH exist ▸ `owner` ▸ `meta` ▸ the eyebrow string. `MapAlbum` sets `OwnerName: null` (`DetailPage.cs:709`), so an **album's** band byline is its meta line alone — "13 songs · 1 hr 14 min · 2013" — and only a playlist/show ever shows the "Spotify · 50 songs, 3 hr 12 min" two-part form. Liked has neither owner nor a distinct eyebrow beyond "Your Library", so it falls to its meta line.

```
┌─────────────────────── page 520, scrolled past cd ───────────────────────┐
│ Random Access Memories        Find   Filter   Play      ← band 56, NO fill│
│ 13 songs · 1 hr 14 min · 2013        (Byline, Caption/Tertiary)           │
│ (a PLAYLIST reads "Spotify · 50 songs · 3 hr 12 min" here — owner prefix)  │
│──────────────────────────────────────────────────────────────────────────│ ← nothing here: the band is an
│ #   TITLE                                  ♥      ⏱     ← column row 36   │   unpainted omission; the page's
│ ═════════════════════════════════════════════════════ 1 DIP hairline      │   own tone plane shows through
│ 7   Doin' It Right                         ♡   4:11                       │
│ 8   Contact                                ♡   6:21      rows are CLIPPED │
└──────────────────────────────────────────────────────────────────────────┘   at 56+36+1 = 93 with a 24 feather
 gutter = compactLeft = TrackRow.PadXFor(tier): 16 (tier≤3) / 12 (4-5) / 8 (6)
 + 48 when the Liked chip rail is present, + 36 when the Liked lens header is
```

### W13 - SELECTION mode in the band

The band swaps its **content** for the batch command bar at the same 56 (`DetailVerticalHero.cs:401-421`); visibility is `count>0 && (multiSelect || count≥2)` (`DetailTracks.cs:844-848`).

```
┌──────────────────────────────────────────────────────────────┐
│  3 selected   [Add to playlist] [Add to queue] [Remove] [✕]  │ 56, pad compactLeft/4
└──────────────────────────────────────────────────────────────┘
```

### W14 - SEARCH expanded in the band

The field takes the **title's** slot, never the actions' (`DetailVerticalHero.cs:382-392`). Width = `availW − 2·compactLeft − ActionsWidth(Find,Filter,Play) − ClusterGap 24`, clamped to `[66, 280]` (`DetailTracks.cs:2156-2168`).

```
┌──────────────────────────────────────────────────────────────┐
│ [ 🔍 Find in playlist ………………… ✕ ]   Find   Filter   Play     │
└──────────────────────────────────────────────────────────────┘
 field 32 tall; disclosure 260 ms expand / 180 ms collapse, Reflow width
```

### W15 - NOTICE strip

```
┌──────────────────────────────────────────────────────────────────────────┐
│ pad 16/8/16/4                                                            │
│ ⓘ  This playlist was deleted.                        [ Go to Library ]   │ InfoBarSeverity.Informational
└──────────────────────────────────────────────────────────────────────────┘
│ … the rows the reader was looking at STAY below this strip …             │
 Deleted · AccessRevoked · CreateFailed carry the action; MinifiedAlbum has none
 and is suppressed on the full page (shown only in the embedded library pane)
```

### W16 - ERROR / offline (deep-link path only)

```
┌───────────────────────── page ──────────────────────────┐
│                                                          │
│                Something went wrong.        28/36/600    │
│          Check your connection and try again.  12/16     │
│                     [  Retry  ]   (only when onRetry)    │
│                                                          │
└──────────────────────────────────────────────────────────┘
 ErrorState.Build → EmptyState.Build: centred, Gap 4, Padding 24, Button.Standard
```

### W17 - EMPTY

```
rows area:   "Nothing here yet"      (empty membership)     14/Tertiary, centred, pad 16/24
             "No songs match your filter"  (filtered to 0)
rail meta:   shimmer bar while MembershipLoaded == false — never "0 songs · 1 min"
```

### W18 - HOVER / PRESSED / FOCUS

```
Play capsule 36:   rest = accent fill, WCAG-picked ink   hover scale 1.04   press 0.96   83 ms brush ramp
Satellite 32:      rest transparent, hover FillSubtleSecondary, press FillSubtleTertiary,
                   BrushTransitionMs 83, hover 1.04 / press 0.96, Radii.Control 4, Cursor.Hand
Rail FAB 40:       hover 1.07 / press 0.92 (ScaleEmphatic), Interaction.Subtle fill
Band text action:  NO scale (a growing word shoves its neighbours) — ink only:
                   TextSecondary → TextPrimary (hover) → TextSecondary (press);
                   the PRIMARY (Play) rides AccentTextPrimary → Secondary → Tertiary
Focus:             engine ring, FocusVisualMargin −3 on every Button-derived control;
                   the plateless text action draws its ring on Radii.Control
```

### W19 - DRAG over the page body

```
┌ rail/hero column (the whole thing is ONE append target) ┐
│   ╔══════════════════════════════════════════════════╗  │   caption: "Add to {name}"
│   ║  ⊕  Add to Discover Weekly                       ║  │   refusal: "Can't edit this playlist" /
│   ╚══════════════════════════════════════════════════╝  │            "Can't add an artist" /
└─────────────────────────────────────────────────────────┘            "Nothing to add"
 TRANSPARENT (the gesture passes through) for: a same-list row drag, and any drag over an
 album/show page that is not editable — DetailShell.cs:706-748
```

### W20 - hero "More" flyout open

```
                    [⋯]
                     └─▶ ┌──────────────────────────────┐  BottomEdgeAlignedRight,
                         │ ＋ Add to playlist           │  FocusTrap, LightDismiss
                         │ ⏭ Play next                  │  ConstrainToRootBounds:false
                         │ ⏱ Add to queue               │
                         │ ────────────────────────────  │  (separator only when owner items exist)
                         │ 👤 Invite collaborators      │  owner-only, capability-gated
                         │ 🗑 Delete                    │
                         └──────────────────────────────┘
 "Copy to playlist" replaces "Add to playlist" when Heart==Follow or the context is Liked
```

### W21 - album trailing (below the rows, both arms)

```
│ ┌ About the artist ────────────────────────────────────────────┐ card, Radii.Card, FillCardSecondary
│ │ (84 round) Daft Punk ✓            bio ≤2 lines     [Follow]  │ border 1 StrokeCardDefault, pad 16/12
│ └──────────────────────────────────────────────────────────────┘
│ Fans also like                      (RailHeader 20/28/600)
│ ( 32 )Name  ( 32 )Name  ( 32 )Name      chips h48, Radii 24, ≤8
│ More by Daft Punk                                  [Show all 12]
│ ▦ 48  Discovery                     2001            (MediaCard.Row)
│ ▦ 48  Homework                      1997             ≤5 rows, expands IN PLACE
│ Featured on · Merch · Similar albums  (same shape)
 section padding 16/20/16/16, gap 12
```

### W22 - podcast show, two-column

```
┌ rail 280 ──────────────┬gr┬ right: EpisodeList ─────────────────┐
│ cover 256 (sat 1.18)   │  │ (episode rows — chapter 09)         │
│ Podcast        eyebrow │  │                                     │
│ Show name  40/52 or 28 │  │                                     │
│ [▶ Play]  ♡(Follow) ↗  │  │  ← rail CTA = PlayPill + [SaveButton,│
│ description ≤6/3 lines │  │    ShareButton, OwnerMenu]          │
│                        │  │                                     │
└────────────────────────┴──┴─────────────────────────────────────┘
 Content == Episodes ⇒ right column Key "right:eps"; the vertical arm uses
 DetailRail.BuildHeader (cover 140 beside the copy) instead of the hero system
```

**There is NO "Publisher · 212 episodes" line on a show's rail — in either arm.** `rail:meta` and `hdr:meta` are both gated `cfg.Badges != BadgeStyle.TypeYear` (`DetailRail.cs:199`, `:402`) and `DetailConfig.Show.Badges` is `TypeYear` (`DetailConfig.cs:225`), while the vertical hero — the one composition that calls `m.MetaLine` unconditionally — is unreachable for a show (`verticalTracks` requires `Content == Tracks`, `DetailShell.cs:512`). A show's `MetaLine` (`Publisher + " · " + EpisodeCount`, `MapShow`) is therefore **rendered nowhere in this frame**, and its rail carries no publisher, no episode count and no facts bento either (`AlbumTrailing.HasReleasePanel` and `LikedFacts.Has` are both false for a show). Reproduce the *mechanism* (one `Badges` gate, not a kind test) in 0.3, but this specific outcome is a hole: decide deliberately whether `Show.Page` keeps it or gives the show its own meta row. The source comment at `DetailConfig.cs:222-223` still describes the line as if it shipped.

### W23 - NOW-PLAYING (what the frame itself changes)

The row-level re-skin is chapter 04's. The **frame** changes exactly one thing: when the playing track belongs to this context (`trackIds.Contains(cur.Id)`, an O(1) test over a mount-time id set — `DetailShell.cs:264-292`), the tone plane and the shell tint take the **now-playing track's cover** as their fallback url. On a page whose own cover cannot be graded (Liked's generated mosaic, a show) that is the difference between a neutral page and a coloured one.

```
before play              during play (a track from THIS page)
┌─────────────────┐      ┌─────────────────┐
│ neutral #151515 │  →   │ tone of the     │   250 ms brush cross-fade,
│ (ungradeable    │      │ PLAYING track's │   one 0×0 leaf repaints —
│  cover)         │      │ cover           │   the rail and the list do not re-render
└─────────────────┘      └─────────────────┘
 the ACCENT (Play capsule, accent rule, row chrome) refreshes on the next
 natural shell re-render (track change, model ready, theme), not paint-only
```

### W24 - LOCAL FILES (`local` route)

`route.Name == "local"` maps to `(DetailKind.Playlist, "wavee:local:all")` (`DetailPage.cs:78`), so it is byte-for-byte the playlist frame — `RailScope.Playlist`, `BadgeStyle.OwnerRow`, the same rail width pair — and the recommendations extender never goes live (it needs a real Spotify context uri and live edits, `DetailTracks.cs:927-932`).

### W25 - the VERTICAL show header (`DetailRail.BuildHeader`) - the third composition, not a variant of the hero

A show in mode 3 never enters the hero system (`verticalTracks` requires `Content == Tracks`, `DetailShell.cs:512`), so it gets this fixed header ABOVE the episode list — no collapse, no context band, no scroll binds. Port it as its own function; it is 80 lines, not a mode flag on the hero.

```
┌──────────── page (any width < 540, or an episodes page in the vertical arm) ────────────┐
│ pad 16 / 16 / 16 / 8, column gap 12                        DetailRail.cs:435-440        │
│ ┌ cover 140 ─┐ gap16 ┌ info (Grow 1, Basis 0, gap 4) ──────────────────────────────┐   │
│ │ Radii.Card │       │ hdr:eyebrow  "Podcast"      (TypeYear only)     :369-374     │   │
│ │ Elev.Card  │       │ hdr:owner    OwnerBlock @ 600 (OwnerRow only)   :375-378     │   │
│ │ sat 1.0 ←  │       │ hdr:title    WaveeType.PageHero 28/36/600, MaxLines 3  :396  │   │
│ │ (NOT 1.18) │       │ hdr:artists  ArtistFacePile @ 600               :400-401     │   │
│ │ decode 256 │       │ hdr:meta     MetaRow(width:null, MaxLines 1)    :402-403     │   │
│ └────────────┘       └───────────────────────────────────────────────────────────────┘  │
│ AlignItems = Center on the cover row (balanced, never a wedge under the art)  :408       │
│ hdr:play   [▶ Play 36] (♡40 SaveButton) (↗40 Fab Share)   Wrap, gap 12      :484-498    │
│            ← NO OwnerMenu and NO PlaylistInlineEdit.ShareButton here: a bare Icons.Share │
│              Fab that opens m.ShareUrl. This is the one CTA cluster that differs.         │
│ hdr:prerelease  PreReleaseCard                                              :431         │
│ hdr:release     AlbumTrailing.ReleasePanel (includeReleasePanel && TypeYear) :432-433     │
└──────────────────────────────────────────────────────────────────────────────────────────┘
 The title run is WaveeType.PageHero, not DetailHero — and the cover takes NO saturation
 boost (DetailRail.cs:422 calls HeroArtwork with the default 1.0). Both are deliberate
 asymmetries with the other two arms; the diagnostic trace at :383-393 logs them as such.
```

### W26 - NO COVER ART / an UNGRADEABLE cover

Two independent facts, and they are answered in two different places:

```
no Image at all          Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, …)
(DetailRail.cs:103)      → the deterministic hash-seeded placeholder, NOT a grey box.
                           The same album always draws the same placeholder, in every arm.

Liked, Cover == null     IsDynamicLikedCover ⇒ the dynamic treatment (rail/hero: with the
(DetailRail.cs:116-117)  style picker; compact strip: WITHOUT — the whole cover is expand).
                           The SKELETON deliberately keeps painting the stock PNG (E2).

cover present but        CanGrade == false ⇒ FirstGradeableTrackCover(m.Tracks) ⇒ still
ungradeable              nothing ⇒ the page renders NEUTRAL (#151515 / #F5F5F5) and the
(DetailShell.cs:285-291) shell tint HOLDS the previous page's colour (never a clear).
                           A now-playing track from THIS page then supplies the colour (W23).
```

### W27 - the hero action row is NOT the same on every kind

```
album / single / show :  [▶ Play 36] (⇄32) (♡32 Save/Follow) (↗32 Share) (⋯32 More)
playlist (followed)   :  [▶ Play 36] (⇄32) (♡32 Follow)      (↗32 Share) (⋯32 More)
LIKED                 :  [▶ Play 36] (⇄32)                   (↗32 Share) (⋯32 More)
                          └─ cfg.Heart == HeartMode.None DROPS the heart entirely
                             (DetailVerticalHero.cs:248) — it is not a disabled heart.
 keys: vhero-play · vhero-shuffle · vhero-save:{uri} · vhero-share · vhero-more:{ctxUri}
 glyphs: satellites Icon(glyph, 14), More Icon(Icons.More, 16), rail FAB Icon(glyph, 16),
         compact chevron Icon(Icons.ChevronRight, 14)   — four different glyph sizes, all real
 The RAIL's cluster has no Shuffle at all (it lives in the track-list command bar,
 DetailRail.cs:227) — Play + [Save, Share, OwnerMenu] only.
```

### W28 - the CHART playlist's caption is NOT in the reserved band (a live D49 residue)

The hero emits nine identity blocks; the band arithmetic knows **eight**. `IdentityChrome` takes exactly five presence flags — eyebrow / attribution / meta / pulse / description (`DetailVerticalLayout.cs:390-402`) — and `DetailSkeleton.VerticalHeroBand` takes the same five (`:39-41`), but `DetailVerticalHero` also emits `hero-chart` (`ChartCaption`, `:226`) between the pulse and the actions. `TrackList` has `HeroHasEyebrow/Attribution/Meta/Description/Pulse` (`DetailTracks.cs:1659-1670`) and **no `HeroHasChart`**.

```
 reserved band (skeleton + pre-measure)      what a CHART playlist actually composes
 ┌ eyebrow 16 ┐                              ┌ eyebrow 16 ┐
 │ title      │                              │ title      │
 │ rule 4     │                              │ rule 4     │
 │ attrib 16  │                              │ attrib 16  │
 │ meta 16    │                              │ meta 16    │
 │ (pulse 28) │                              │ (pulse 28) │
 │            │  ← nothing reserved here     │ CHART ~16  │  ← unreserved: pushes the toolbar,
 │ actions 40 │                              │ actions 40 │     the column header and every row
 └ desc …  ───┘                              └ desc … ────┘     down when the full model lands
```

Consequence in 0.2.9: on a chart playlist (`ChartNewEntries > 0`) the caption row plus one `IdentityGap` — ~20 DIP — arrives *outside* the band the skeleton and the collapse binds reserved, which is precisely the shove non-negotiable 3 exists to prevent. It is invisible on every other kind, which is why it survived. **In 0.3 the chart row gets a sixth flag** (`chart`) threaded through `IdentityChrome` / `TitleHeightBudgetFor` / `IdentityGapFor` / `HeroBandHeight` / `HeroSkeleton`, exactly like `pulse`; `DetailSkeletonGeometryTests` gains the case.

**The eyebrow asymmetry, stated once** (`DetailRail.cs:570-585`, `DetailVerticalHero.cs:163,186`): `EyebrowText` answers for **every** kind — `TypeYear` → "ALBUM · 2013" / the kind alone / the year alone / `""`; `OwnerRow` → "Playlist · Collaborative" ▸ "Playlist · Private" ▸ "Playlist"; anything else (Liked) → "Your Library". The **vertical hero calls it unconditionally**, so a playlist and Liked both get an eyebrow row there. The **two-column rail does not**: it emits the eyebrow only for `TypeYear` and the owner/collaborator block for `OwnerRow` (and nothing for Liked). Port both behaviours; they are deliberate, and the hero's own reserved-band predicate (`HeroHasEyebrow`, `DetailTracks.cs:1659`) reads the same function so the skeleton reserves the row a playlist will actually show.

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| Rail column | W = `cfg.RailWidth` (280 album/show, 240 playlist/liked) · mode1 224 · mode2 188 · drag 180–480 | pad 16 L / 8 R / 24 T / 24 B, gap **14** | — | — | `Tok.FillLayerDefault` (Liked: **no fill**) | — | `DetailRail.cs:19-20,262-298`; `DetailShell.cs:192`; `DetailRailPolicy.cs:25` |
| Rail cover | `railW − 24`, min 80 | — | `Radii.Card` 8 | — | saturation 1.18, decodePx 256 | `Elevation.Card` | `DetailRail.cs:96,145-162,24-26` |
| Rail title | 40/52 when window H ≥ 900, else 28/36; `MinSize 18`, `MaxLines 3` | — | — | `WaveeType.DetailHero` (Display face, −20/1000 em, Weight 600) | `Tok.TextPrimary` | — | `DetailShell.cs:636-637`; `DetailRail.cs:183-189` |
| Rail eyebrow | 12/16/600, +30/1000 em | — | — | `WaveeType.Eyebrow` | `Tok.TextTertiary` | — | `DetailRail.cs:590-593` |
| Rail meta (**playlist / Liked only** — gated `Badges != TypeYear`) | 12/16/400, `MaxLines 2`, `Width = cover` | — | — | `WaveeType.TrackMeta` (= `Ui.Caption().Secondary()`) | `Tok.TextSecondary` | — | `DetailRail.cs:77-94,199-200` |
| Rail eyebrow **width discipline** | explicit `Width = cover` on the run (the rail's "every clamped run gets an explicit width" rule, `DetailRail.cs:15-17`) — `BuildHeader`'s `hdr:eyebrow` deliberately does NOT (its info column already clamps via `Grow 1 / Basis 0`) | — | — | — | — | — | `DetailRail.cs:170`; `:371-373` |
| Rail artwork **transform origin** | `TransformOriginX/Y = 0` + `ClipToBounds` on the hero's artwork box, so the 280 ms `ScaleCorrect` bounds tween grows from the top-left instead of the centre | — | — | — | — | — | `DetailVerticalHero.cs:111-115` |
| Rail CTA row | Play 36 tall; FABs 40 | gap 12 (outer), 8 (FAB group), margin-top 4 | Play `Radii.Full`; FAB circle 20 | label Bold (700) | accent fill + `ColorContrast.PickContrast` ink | — | `DetailRail.cs:212-240`; `WaveeCta.cs:88-107` |
| Rail description | 12 px, `descMaxLines` = 6 (winH ≥ 760) / 3 | — | — | `RichText.Of` | `Tok.TextSecondary`, links = accent | — | `DetailShell.cs:638`; `DetailRail.cs:249-253` |
| Rail grip | strip 16 (20 while collapsed) | — | 1 (indicator) | — | reveal-on-hover 2-DIP bar | — | `Splitter.cs:22,33`; `DetailShell.cs:206,797-817` |
| Compact rail strip | W 96, cover 80, chevron row 28 | pad 8/16/8/16, gap 8 | `Radii.Card` cover, `Radii.Control` chevron | title 12/600 `MaxLines 2` | `Tok.FillLayerDefault`; chevron `HoverFill FillSubtleSecondary` | `Elevation.Card` on the cover | `DetailShell.cs:205`; `DetailRail.cs:304-354` |
| Vertical hero pad / gap | 24 (row flow, or ≥ 420 stacked) else 16; bottom pad 8 | — | — | — | — | — | `DetailVerticalLayout.cs:30-42,123-129` |
| Hero artwork | stacked `clamp(w−2p, 96, 280)`; row `round(clamp(inner·0.44, 144, 240))` | — | `Radii.Card` 8 | — | saturation 1.18; decodePx 256 unmeasured / 256‖512‖1024 measured | `Elevation.Card` | `DetailVerticalLayout.cs:53-63,133-143,643-654` |
| Hero title | plan: size ∈ [20, 96] on a 2/4/8 grid, lines ≤ 2, `LineHeight = round(size·1.3301)`, `LineHeight` prop = NaN | — | — | `WaveeType.DetailHero` + plan | `Tok.TextPrimary` | — | `DetailVerticalLayout.cs:275-295,321-333,451-478`; `DetailVerticalHero.cs:193-203` |
| Hero accent rule | 20 × 2, top margin 2, `AlignSelf.Start` | — | — | — | `h.Accent` | — | `Surfaces.cs:339-356`; `DetailVerticalHero.cs:206` |
| Hero attribution — **THREE arms in precedence order**, not two: (1) `CollaboratorFacePile` when `ShowCollaborators(m)` = `Collaborators.Count>0 && (IsCollaborative ‖ Count≥2)`, keyed `pl-collab:{(int)contentW}`; (2) owner `TextEl`; (3) billed-artist accent spans; else **null** (no row, and `HeroHasAttribution` agrees) | owner 12/600, 1 line, ellipsis; spans 12/400, **`NoWrap`**, 1 line, `", "` separator, each span `OnClick → h.Go("artist:"+uri)` | — | — | — | `Tok.TextSecondary`; artist spans `h.Accent` | — | `DetailVerticalHero.cs:469-501`; `DetailRail.ShowCollaborators` `:563-564`; `DetailTracks.cs:1662-1663` |
| Hero meta | 12/16, `MaxLines 2`, wrap whole words | — | — | `WaveeType.TrackMeta` | `Tok.TextSecondary` | — | `DetailVerticalHero.cs:214-218` |
| Hero action row | Play 36 + satellites 32 | gap 8, wrap, margin-top 4 | Play `Radii.Full`; satellite `Radii.Control` 4 | — | satellite rest transparent / `FillSubtleSecondary` / `FillSubtleTertiary` | — | `DetailVerticalHero.cs:240-260,452-467` |
| Play capsule (every arm) | MinHeight **36** (a FLOOR, not a height) | **pad 18 / 6 / 18 / 7** (the 1-DIP bottom bias is the optical baseline nudge) | `Radii.Full` (engine clamps to 18) | stock 14 px + `Bold = true` (700) | `Palette(accent)`: fill / fill@0.90 hover / fill@0.80 press, ink `PickContrast`, pressed ink dimmed to 0x80 (dark ink) or 0xB3 (light), border `AccentControlElevationBorder` | — | `WaveeCta.cs:88-107,225-242` |
| Hero description | 13 px (**not** the rail's 12), `RichText.Expandable` — it carries its own more/less toggle, keyed on `ContextUri ?? Title` | reserved line height 18 | — | — | `Tok.TextSecondary`, links `h.Accent` | — | `DetailVerticalHero.cs:262-269`; `DetailVerticalLayout.cs:226` |
| Show header (vertical, `BuildHeader`) | cover **140**, title 28/36/600 `WaveeType.PageHero`, `MaxLines 3` | pad 16/16/16/8, column gap 12, cover-row gap 16, info gap 4 | `Radii.Card` | — | cover saturation **1.0** (the one arm with no 1.18 boost) | `Elevation.Card` | `DetailRail.cs:363,396-399,405-440` |
| Hero identity column | `MinHeight = art` (row flow), width = content (stacked) | gap 4 → up to 12 (2-DIP steps) | — | — | — | — | `DetailVerticalLayout.cs:197-198,527-547` |
| Reserved block heights | eyebrow 16 · rule 4 · attribution 16 · meta 16 · pulse 28 · actions 40 · description line 18 · toolbar 44 (pills 32, inset 6/5) | gap 4 | — | — | — | — | `DetailVerticalLayout.cs:211-237` |
| Context band | H 56, gutter = `TrackRow.PadXFor(tier)` (16/12/8 ← `PadX` = `Spacing.L`) | cluster gap 24, action gap 16, action padX 10; pivot gap 16 / padX 8; underline 2, gap 4 | — | title `Ui.BodyStrong` 14/20/600; byline `Ui.Caption` 12/16 | title `Tok.TextPrimary`; byline `Tok.TextTertiary`; **no fill** | none (offset model) | `ContextBandLayout.cs:27,46-66`; `ContextBand.cs:105-153` |
| Band title slot | `TitleCap` **280** — past it the surplus goes to the pivot lane, never to the title | — | — | — | — | — | `ContextBandLayout.cs:70` |
| Band label estimate | `EstimateLabelWidth(len, padX)` = `len · AvgCharW 7.6 + 2·padX` — deliberately generous so a localized label reserves rather than clips | — | — | — | — | — | `ContextBandLayout.cs:72-84` |
| Band text action | H = 56 − 2·`Spacing.M` = 32 | padX 10, gap 8 | `Radii.Control` | 14/20/600, **sentence case, label passed through verbatim** (no caps transform — it mangles Turkish dotted i and expands ß) | rest `TextSecondary` → hover `TextPrimary` → pressed `TextSecondary`; primary/`toggledOn` `AccentTextPrimary` → `AccentTextSecondary` → `AccentTextTertiary` | — | `WaveeCta.cs:159-218` |
| Band text action — optional leading glyph | 14 px in `Theme.IconFont`, same three-state ink ramp as the label, `Spacing.S` 8 before it | — | — | — | — | — | `WaveeCta.cs:191-197` (unused by this frame's three actions; the artist band uses it) |
| Band pivot link (artist arm) | H = 56 − 24 = 32, `Role = Tab` | padX 8, row gap 16 | `Radii.Control` | 14/20/600 | active `TextPrimary` / inactive `TextSecondary`, `HoverColor TextPrimary` **on the link's own box**, never the row | — | `ContextBand.cs:288-315` |
| Band hairline | 1 DIP, on the LAST stuck stratum | — | — | — | `Tok.StrokeDividerDefault` | — | `ContextBandLayout.cs:31`; `DetailTracks.cs:2444-2450` |
| Sticky clip | inset 56 + 36 + 1 (+48 chips, +36 lens), feather 24 | — | — | — | — | — | `DetailVerticalLayout.cs:78,90-92,587-591`; `DetailTracks.cs:902-904` |
| …the two optional strata, derived | chips `ContentFilterChips.VerticalExtent` = `RailHeight 40 + Spacing.S 8` = **48** (`ContentFilterChips.cs:38,40`); lens `LikedLens.HeaderExtent` = `LikedLens.PillHeight 28 + Spacing.S 8` = **36** (`LikedFactsPanel.cs:1350-1355`) — that is the lens row's OWN private 28, **not** `WaveeCta.PillHeight` 36 (`WaveeCta.cs:65`). Both are CONSTANTS on purpose: a header whose height followed its content would desync the cut, which is why the lens row does not wrap and its pills ellipsise | — | — | — | — | — | `DetailTracks.cs:898-908` |
| …the column-header stratum is **not always 36** | `DetailTrackTableRules.HeaderHeightFor(classic)` = `classic ? 32 : 36` (`:27,49`), but `StickyClipInset` sums the hard `ChromeHeaderHeight = 36` (`DetailVerticalLayout.cs:90`). Under the **Classic table skin** the clip over-cuts by 4 DIP. Port the 56/36/1 arithmetic, but make the middle term read the table's real header height | — | — | — | — | — | `DetailTracks.cs:2430-2433` |
| Band search field width | `clamp(availW − 2·compactLeft − ActionsWidth(Find, **Filter**, Play) − ClusterGap 24, SearchIconWidth **66**, SearchMax **280**)`; collapsed rest width is `SearchIconWidth` 66; the skeleton's toolbar search pill is `SearchPreferred` **240** | — | — | — | — | — | `DetailTracks.cs:2152-2169`; `DetailTrackCommandBarLayout.cs:41-46` |
| Band scroll spy (artist arm; shared file) | `SpyProbe 8`, `SpyViewportFraction 0.25`, `EndProbe 8`, `ContextPivot.MaxItems 16`; the active underline is ALWAYS mounted and swaps colour (`accent` ↔ `Transparent`) over `WaveeMotion.Fast` 167 ms — never mounted/unmounted, never a FLIP flight | — | — | — | — | — | `ContextBandLayout.cs:101-111`; `ContextBand.cs:192-193,306-313` |
| Notice strip | auto; `Shrink = 0`, `Direction = 1`; zero-height `HitTestVisible:false` box when `Notice == None` | pad 16/8/16/4 | — | InfoBar, `isClosable: false` | `InfoBarSeverity.Informational` | — | `DetailNoticeBar.cs:49,58-64,80-87` |
| Notice action | `Button.Create(..., ButtonAppearance.Subtle, ControlSize.Small)`; **omitted entirely when `HistoryStore.NavCtx` is null**; target is the `"albums"` route (there is no bare "library" destination) | — | — | — | — | — | `DetailNoticeBar.cs:77-78` |
| Skeleton bars | bar radius 4; every bar width `max(32, round(measure · f))`; the accent-rule placeholder is 20 × 2 at radius 1 | — | `4` / `1` | — | `Tok.FillSubtleSecondary` via `SkeletonStyle.Default` | — | `DetailSkeleton.cs:65-71,209-214` |
| Trailing section | — | pad 16/20/16/16, gap 12 | `Radii.Card` | header `WaveeType.RailHeader` 20/28/600 | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault`, hover `FillCardDefault` | — | `DetailTrailing.cs:482-489,540-547,560-602` |
| Trailing rows | 48 thumb, merch row 64 | gap 4 between rows | `Radii.Control` thumb | `WaveeType.TrackTitle` | price `Tok.AccentTextPrimary` | — | `DetailTrailing.cs:460-538,699-708` |
| Release facts tile | 2-up row + full-width row; `Grow 1, Basis 0, MinWidth 0` | pad 12/8/12/8, gap 1 inside, 8 between, stagger 45 ms | `Radii.Control` 4 | value 18/800, caption 11 | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault`; value `TextPrimary`, caption `TextSecondary` | — | `Components/StatTile.cs:22-58`; `DetailTrailing.cs:228-252` |
| Page tone plane | full bleed | — | `WaveeShell.ContentPaneCorners` | — | `WaveePalette.PageTone` @ α 0.20 dark / 0.30 light | over FileArea over live Mica | `CoverPaletteLeaves.cs:101-118,139` |
| Shell tint | full bleed (window) | — | — | — | dark `TintedDark(scheme) @ 0.14`; light `Lift(TextBase) @ 0.05`; neutral `ShellGround @ 0.03` | flat Tint arm of `ShellMaterialLayer` | `CoverPaletteLeaves.cs:242-246`; `ShellMaterialLayer.cs:89-99` |
| Two-column row | `MaxWidth 1600`, `Basis 0`, centred | — | — | — | — | — | `DetailShell.cs:649,672` |
| Right column | `MinWidth` 300 (modes 0-2) / 0 (vertical) | — | — | — | — | — | `DetailLayoutBreakpoints.cs:62-66`; `DetailShell.cs:519` |

---

## 4. Colour & material

**The chain, in order.** `coverUrl = m.Cover?.Url` → `paletteUrl` → `Surfaces.SchemeFor(paletteUrl)` (page grading, theme-matched) and `Surfaces.ChromeSchemeFor(paletteUrl)` (chrome grading, theme-**opposite** branch first) → the accent, the tone plane and the shell tint (`DetailShell.cs:276-310`).

1. **Palette url resolution** (`DetailShell.cs:276-291`):
   `LikedToneAnchor(m, settings)` — Liked with a dynamic cover treatment anchors on the treatment's own lead tile (`LikedCoverRules.ToneAnchorUrl`, capped 16-tile prefix scan) — else the cover url when it is gradeable (`CoverColorPlane.CanGrade`), else `FirstGradeableTrackCover(m.Tracks)` (a playlist mosaic cannot be graded), else the cover url. If the resolved url differs from the cover url the scheme is re-read for it.
2. **Live-track fallback** (`DetailShell.cs:292-297`): if the currently playing track belongs to *this* context (`O(1)` set test over a mount-time id set), `liveUrl = cur.Image.Url` is threaded as the tone/tint leaf's `fallbackUrl`, so a page with no grading of its own (Liked, a show) still takes colour while it is playing.
3. **Accent** (`DetailShell.cs:304-310`): `chrome = coverChrome ?? liveChrome`; then `chrome is {} cp ? WaveePalette.ChromeAccent(cp) : m.Accent != 0 ? WaveePalette.ChromeFromPayload(m.Accent) : Tok.AccentDefault`. `ChromeAccent = Vivid(Lift(Accent(s)))`, falling back to `Tok.AccentDefault` when saturation ≤ 0.08 (`WaveePalette.cs:37`). `m.Accent` is the Home daylist card's `extractedColors.colorDark`, carried on the nav preview, so a daylist page's Play/countdown/heart match the card that opened them before any grading exists.
4. **Page tone** (`CoverPaletteLeaves.cs:143-144` → `WaveePalette.PageTone`): hue from `Accent(scheme)`; if HSV saturation < 0.12 the tone is the neutral (`#151515` dark / `#F5F5F5` light); otherwise HSL with **L forced to 0.15 dark / 0.94 light** and **S capped at 0.30 dark / 0.16 light**. Applied as a bound `Fill` on a full-bleed `ZStack` sibling of the page, `BrushTransitionMs = WaveeMotion.Standard` (250 ms), clipped to `WaveeShell.ContentPaneCorners`. Alpha 0.20 dark / 0.30 light — the ratchet (opaque → 0.72 → 0.45/0.90 → here) is documented at `CoverPaletteLeaves.cs:119-138` and **must not be reversed**. Consequence stated there and kept: in light theme the record's hue is close to imperceptible; light identity is carried by the chrome accent.
5. **Hero-only mode** is dead code kept alive only because the leaf's Props still carry it (`const bool heroOnly = false`, `DetailShell.cs:565`). Its veil recipe (`CoverPaletteLeaves.cs:150-168`) is a 4-stop vertical gradient α→0 between `clamp(band/pageH, 0.12, 0.80)` and `+0.22`, over `BackdropBandFor(heroBand) = max(112, heroBand)` (`DetailVerticalLayout.cs:660-661`). The `heroBand` the Props still carry is the **measured** hero height in the vertical arm and `pageH × TwoColumnHeroBandFraction 0.55` in the two-column one (`DetailShell.cs:189,569-571`) — a synthetic band, since a full-height rail has no single hero to measure. Do not port any of it unless the setting comes back.
6. **Shell material tint** (`CoverPaletteLeaves.cs:223-264`): a 0×0 leaf, keyed `"detail-tint:"+route.Name`, that `Watch`es the cover (and the fallback) so a grading arrival repaints **one node**. Publishes the flat arm only (`wash: null`). `ready: true` — the tint applies as soon as a url is known, not on model-ready (gating on ready left Home→detail on the bare ground). `definite = disabled || !apply`. The write is a hand-over through `ShellMaterial.Publish` (`App/ShellMaterial.cs:48-58`): claim on first publish and on `UseActivation(onActivated)`, refresh only while still the owner, **never clear**. The neutral ground is `WaveeColors.ShellGround @ A 0.03` — never `Transparent`, because cross-fading through premultiplied black read as "neutral AND darker" for several frames (`ShellMaterialLayer.cs:82-89`).
7. **Colour washes off** (`Settings › Appearance`): `colorWashesDisabled` kills the tone plane (renders a bare `Grow=1` box) and makes the tint publish `definite` neutral (`DetailShell.cs:213`, `CoverPaletteLeaves.cs:92-94,239`).
8. **On-media ink does not exist on this surface.** The page sits on one clamped-lightness plane, so the standard `Tok` ink tokens are correct in both themes; every on-media ladder, white-alpha plate and media scrim was deleted with the immersive hero (`DetailVerticalHero.cs:36-42`, `Surfaces.cs:194-196`).
9. **Light/dark differences**: tone alpha (0.20/0.30) and clamp (L 0.15/0.94, S 0.30/0.16); tint recipe (`TintedDark @ 0.14` vs `Lift(TextBase) @ 0.05`); `Elevation.Card` (dark blur 8 / y 2 / `#00000033`, light blur 4 / y 2 / `#0000001A`).

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| Route change (forward) | whole page | Position + Opacity | Dx +8, α 0 → rest | 250 (`Expressive.Fast`) | `SmoothOut` | enter delay 90; exit 120 `EaseOut` | engine `ReducedSnap` keeps the fade | `PageNavMotion.cs:52-59` |
| Route change (back) | whole page | Position + Opacity | Dx −8, α 0 → rest | 250 | `SmoothOut` | same | same | `PageNavMotion.cs:61-68` |
| Shimmer → content | region root | Opacity | 0 → 1 | 250 | `SmoothOut` | — | snaps | `SkeletonRegion.cs` `SkelReveal.FadeOnly` |
| Shimmer exit | shimmer orphan | Opacity | 1 → 0 | 250 (`SkeletonStyle.ExitMs`) | — | cross-dissolves under the content | snaps | `SkeletonRegion.cs:36` |
| Shimmer idle | bars | Opacity pulse | 1 → 0.5 | 1000 loop | — | — | — | `SkeletonStyle.Default` |
| Reveal ramp | one row slot | Opacity | 0 → 1 | 280 | `FluentDecelerate` | one chunk (12 rows) per frame | engine still cross-fades | `DetailTracks.cs:2810-2813`; `DetailRevealRamp.cs:11` |
| Late model row (hero/rail) | keyed row box | Opacity | 0 → 1 (`FadeUp`) | `Expressive.Fast` 250 | `SmoothOut` | — | `KeepFade` policy: fade stays, travel snaps | `DetailRail.cs:52-57` |
| A sibling row arrives | every row below | Position (FLIP) | old origin → new | 250 | `SmoothOut` | — | snaps | `DetailRail.cs:56-57` (`Shove`) |
| Width crosses the flow seam | hero box / expanded | Position + Size (`Reveal`) | old → new | 280 | `SmoothOut` | — | snaps | `DetailVerticalHero.cs:51-54` |
| Width changes the artwork edge | artwork box | Bounds (`ScaleCorrect`) | old → new | 280 | `SmoothOut` | — | snaps | `DetailVerticalHero.cs:46-49` |
| Scroll 0 → cd | expanded hero | TransY | 0 → −cd | scroll-linked | `Linear` | — | scroll-linked, unaffected | `DetailVerticalHero.cs:438-440` |
| Scroll cd−96 → cd | expanded hero | Opacity | 1 → 0 | scroll-linked | `Linear` | — | unaffected | `DetailVerticalHero.cs:441-443`; `DetailVerticalLayout.cs:89,632-634` |
| Scroll 0 → cd | hero root | PresentedH | heroBand → 56 | scroll-linked | linear (`Ease` default) | pinned at the viewport top | unaffected | `DetailTracks.cs:1790`; `ScrollBindDsl.cs:118-125` |
| Scroll cd−44 → cd | compact band | Opacity + TransY | 0 → 1, +4 → 0 | scroll-linked | `Linear` | — | `dy` collapses to 0 (value, not branch) | `DetailVerticalHero.cs:430`; `ScrollBindDsl.cs:145-161` |
| Sticky engage | column-header row | PinTop 56 | — | — | — | `onStuck` flips `_verticalCompactInteractive` | — | `DetailTracks.cs:1796` |
| Clip engage (album/show body) | trailing body | ClipTop + EdgeFade | — | — | — | feather 24 | — | `DetailTracks.cs:1815-1826` |
| Grading arrives | tone plane | Fill (brush channel) | previous → tone | 250 (`WaveeMotion.Standard`) | engine brush ramp | — | — | `CoverPaletteLeaves.cs:115` |
| Page claims the material | shell tint rect | Fill | previous page's → this page's | 250 | engine brush ramp | — | — | `ShellMaterialLayer.cs:93-99` |
| Hover a Play capsule | button | Scale | 1 → 1.04 | engine `HoverFade` | — | — | tier returns 1 ⇒ no transform | `WaveeMotion.cs:47`; `WaveeCta.cs:104-106` |
| Press a Play capsule | button | Scale | 1 → 0.96 | engine `PressFade` | — | — | as above | `WaveeMotion.cs:47` |
| Hover a rail FAB | FAB | Scale | 1 → 1.07 (press 0.92) | engine | — | — | as above | `WaveeMotion.cs:52`; `DetailRail.cs:603` |
| Hover a satellite | 32 box | Fill | transparent → `FillSubtleSecondary` | 83 (`WaveeMotion.Faster`) | engine brush ramp | — | — | `DetailVerticalHero.cs:459-461` |
| Hover a band action | word | Colour | `TextSecondary` → `TextPrimary` | engine `HoverT` | — | — | — | `WaveeCta.cs:186-203` |
| Rail drag into the resist zone | rail content | Opacity | 1 → 0.35 | paint-bound (no re-render) | linear over 44 DIP | — | — | `DetailShell.cs:786-787`; `Splitter.cs:66-69,281` |
| Release facts arrive | tiles / notes | Opacity (`FadeUp`) + Position (`Shove`) | 0 → 1 | 250 | `SmoothOut` | `Stagger` 45 ms (`MastheadStaggerMs`), 0 under reduced motion | value, not branch | `DetailTrailing.cs:221,232,257,266-272` |
| Stat value refines ("2013" → "May 17, 2013") | tile value box (`Key = "v:"+value`) | Opacity + Dy ±4 + Blur 2 | α 0 → 1 | 150 | `EaseInOut` | inside a `ClipToBounds` box the parent already sized | engine `ReducedSnap` | `MotionRecipes.TextSwap`; `Components/StatTile.cs:39-42` |
| Toolbar command promoted/evicted | command box | Position + Opacity | Dx 8, α 0 → rest | 220 in / 150 out | `SmoothOut` / `FluentAccelerate` | — | — | `DetailTracks.cs:230-235` |
| Search disclosure | field box | Position + Size (`Reflow`, width axis — **never `Reveal`**: the field must PUSH its neighbours through real layout) | icon 66 → field ≤280 | 260 open / 180 close | `SmoothOut` / `FluentAccelerate` | — | — | `DetailTracks.cs:238-244` |
| …the icon↔field swap inside the growing box | search content | Opacity | 0 → 1 | same 260 / 180 | `SmoothOut` / `FluentAccelerate` | — | — | `DetailTracks.cs:246-252` (`SearchSwapMotion`) |
| …the focus underline | underline | Opacity | 0 → 1 | same 260 / 180 | `SmoothOut` / `FluentAccelerate` | mounted only while focused, but FADES | — | `DetailTracks.cs:254-260` (`SearchUnderlineMotion`) |
| Check column appears/disappears | column-header grid | Position | 28-DIP inset in/out | `MotionTok.DisclosureExpand` | `FluentDecelerate` | — | — | `DetailTracks.cs:2435-2441` |
| Band mode swap (normal ↔ selection) | band content | Position + Opacity | Dx ±10/−8 | 210 in / 150 out | `SmoothOut` / `FluentAccelerate` | — | — | `DetailTracks.cs:262-267` |
| Daylist countdown | flip digits | — | live | per second | — | — | — | `FlipCountdown` (chapter 06) |
| Pre-release countdown | unit tiles | — | live | per second | — | — | — | `PreReleaseCountdown` (chapter 05) |
| Breakpoint cross (tier) | realized rows | Opacity + TransY | 0 → 1, +6 → 0 | engine displacement seed | — | stagger 24 ms narrowing (cap 6) / 16 ms widening (cap 4); seeded only for the window `[firstVis, firstVis + ReDealRows)` off the live scroll offset, and the seeds are **cleared** on a skip so a later `_dispVer` bump cannot replay them as a phantom fade | skipped entirely (`Motion.ReducedMotion`, a rapid reversal, `prev < 0`, or `rowH ≤ 0`) | `DetailTracks.cs:1609-1642` |
| Hover a band pivot link's underline (artist arm) | 2-DIP rule | Fill | `Transparent` ↔ `accent` | 167 (`WaveeMotion.Fast`) | engine brush ramp | — | — | `ContextBand.cs:306-313` |
| Rail FAB / hero "More" fill | 40 / 32 box | Fill | via `.Interactive(Interaction.Subtle)` — an engine-resolved ramp, **not** an authored `HoverFill` + `BrushTransitionMs` like the hero satellite's | — | engine | — | — | `DetailRail.cs:599-605`; `DetailVerticalHero.cs:569` |

**Clock**: every one of these rides the engine's declarative surface (`LayoutTransition` / `ScrollBinds` / brush channels), which samples the frame clock. The only `Environment.TickCount64` reads in the frame are non-visual: `DetailLiveRefresh`'s storm window (`DetailLiveRefresh.cs:41`) and `ReDeal`'s rapid-reversal guard (`DetailTracks.cs:1614`). Both are policy timers, not animation — keep them, but never sample TickCount64 for motion.

---

## 6. Interaction

**Hero / rail cover** — drag source for the whole entity (`Drag.Source(WaveeDragKinds.Resource, ForEntity(kind, uri, title, cover, acts))`, `WaveeResourceDrag.cs:386-394`) mounted on the **framing box**, so the editable playlist cover's own *file drop* target inside it is untouched. Editable playlist → click opens the cover picker; Liked with a dynamic treatment → click opens the style picker; otherwise static art with no click.

**Play capsule** — `h.PlayAll()`, which late-binds through the `PlayAllOverride` cell the track list fills with "play the VISIBLE (sorted/filtered) order from the top"; null until the list mounts, then falls back to `Play(0)` (`DetailShell.cs:410-411`, `DetailTracks.cs:751`).

**Satellites (hero)** — Shuffle (`Icons.Shuffle`, tooltip `Strings.Detail.Shuffle`), Save/Follow heart (`SaveButton`, target = `PreReleaseUri ?? ContextUri`), Share (`PlaylistInlineEdit.ShareButton`), More. All 32 × 32, `ToolTip.Wrap`ped, `Role = AutomationRole.Button`, `Focusable`, `Cursor.Hand` (`DetailVerticalHero.cs:240-260,452-467`).

**Rail CTA cluster** — Play pill + a FAB group `[Save/Follow, Share, OwnerMenu]` that wraps **as a unit** (`Wrap = true` on the outer row, the group is one child) so a narrow rail never orphans a single FAB (`DetailRail.cs:212-240`).

**Hero More flyout** — built lazily at open from the LIVE model (`DetailHeroMoreButton.Toggle`, `DetailVerticalHero.cs:528-557`), in this order: **Add to playlist** / **Copy to playlist** (`Icons.Add`; "Copy" when `cfg.Heart == HeartMode.Follow` or the context is Liked) → **Play next** (`WaveeIcons.PlayNext`) → **Add to queue** (`WaveeIcons.PlayAfter`) → separator + owner items (Invite / Delete, capability-gated inside `PlaylistInlineEdit.AppendOwnerItems`). Placement `BottomEdgeAlignedRight`, `FocusTrap: true`, `LightDismiss`, `ConstrainToRootBounds: false`.

**Band actions** (compact, vertical arm) — **Find** (toggles the search field; captures its node so collapsing restores focus), **Filter** (the funnel as a word — it only ever existed inside the expanded field before, so the collapsed bar could not reach it), **Play** (the band's one accent word) (`DetailTracks.cs:2118-2142`).

**Rail grip** — pointer drag writes the width signal directly; **release** commits both width and collapsed to this scope's setting pair (`DetailShell.cs:804-808`). Below the floor the grip resists and the rail's content fades; 44 DIP further collapses; re-open needs a pull past 220.

**Compact rail** — the **whole cover** and the chevron are both `expand` (no second hit target inside 48–80 DIP); the strip carries a `ToolTip` of the title (`DetailRail.cs:311-353`).

**Page-body drop target** — mounted for every `Content == Tracks` page with a context uri, editable or not, so a read-only playlist can explain itself (`DetailShell.cs:706-748`). Accepts when `!SameList(p) && PlaylistDropRefusalRules.Evaluate(...) == None`; **transparent** (the gesture passes through to the list) for a same-list row drag and for a non-editable album/show. Caption `Strings.Drag.AddTo(name)`; refusals `Strings.Drag.CantEditPlaylist` / `CantAddArtist` / `NothingToAdd`.

**Keyboard** — the band/rail controls are ordinary focusables with the engine's ring; the track table owns roving focus, Enter/Space invoke and Extended selection (`ItemsSelectionMode` from the config). The hero has no keyboard shortcuts of its own.

**Accessibility names** — `AutomationRole.Button` on every satellite, FAB, pill, chevron and text action; `AutomationRole.Tab` on pivot links (artist page only); the notice is an `InfoBar` with `isClosable: false`; rows expose `ItemText` in the vertical list (`DetailTracks.cs:1508`).

**Inline edit** (playlist, owner) — title, description and cover are `PlaylistInlineEdit` facades gated on `EditableMetadata(m)` = `Notice == None && Capabilities.CanEditMetadata` (`PlaylistInlineEdit.cs:80`); contents edits gate on `Editable(m)` = `Notice == None && Capabilities.CanEditItems` (`:77`). **A notice mounting removes every edit affordance** — that is one fact, not nine call sites.

**Live refresh** — an open page re-projects in place on a store change (playlist: its own uri, an owner it renders, or a bulk; Liked: any Liked-kind change or a bulk; album/show: its uri or a bulk) through a single-flight pump: an immediate leading pass, one coalesced trailing pass, a `SettleMs` **50** cooldown after every pass, and a `StormPasses` **40** / `StormWindowMs` **10 000** tripwire that logs `detail.refresh.storm` once per window (`DetailPage.cs:226-251`; `DetailLiveRefresh.cs:13-16`). A KeepAlive **reactivation** also requests one pass (`DetailPage.cs:252`), and the subscription + `SetOpenContext`/`ClearOpenContext` are torn down on park (`:253-258`). A same-list reorder in flight **holds** the new model until the gesture ends (`PlaylistReorderDefer`, `DetailPage.cs:211`); a pass that landed nothing republishes the SAME `Tracks` instance so reference-keyed consumers do not re-render (`:215`).

**Per-context view state** — the shell re-keys on the **context uri**, not the mount (`DetailShell.cs:361-375`): it clears `_query`, `_filters` and `_multiSelect`, then loads this context's persisted sort (`detail.sort.col:{ctxUri}` / `.desc:`, with a **−1 "never chosen" sentinel** whose per-kind fallback is `DateAdded desc` for Liked and context order for everything else — `:178-180`, `:231`) plus the three app-wide settings (`RowDensity`, `TempoColumn`, `PlaysColumn`). A page mounted from a nav preview settles onto its real context one render later, which is exactly why the key is the uri.

**The mode's two fail-safes, both anti-flicker, both mandatory** (`DetailShell.cs:495-502`). (a) **Pre-measure seed**: before any bounds callback, `mode = Math.Max(mode, InitialModeForViewport(EstimatePageWidthFromViewport(viewport.Width)))` — `Math.Max`, because a *narrower* arm is always safe to seed and a wider one is the flicker. (b) **Self-heal on every render**: `if (_measuredW > 0) { var fit = ModeFor(_measuredW, mode, initialized); if (fit > mode) mode = fit; }` — never RENDER a mode wider than the last measured width supports, so a stale mode signal can never leave a rail + table in a width that cannot hold both. Note the mode ladder is numerically INVERTED (0 = widest), so "fit > mode" means "narrower than what we were about to draw". The tier ladder carries the same clamp inside `TrackList`.

**Loc keys this frame owns** (`assets/loc/en-US.json`; base-culture English quoted so a 0.3 string table can be diffed against it):

| key | en-US |
|---|---|
| `nav.playlist` / `nav.playlistCollaborative` / `nav.playlistPrivate` / `nav.yourLibrary` | `Playlist` · `Playlist · Collaborative` · `Playlist · Private` · `Your Library` |
| `detail.notice.deleted` | `This playlist was deleted.` |
| `detail.notice.accessRevoked` | `You no longer have access to this playlist.` |
| `detail.notice.createFailed` | `This playlist couldn't be created.` |
| `detail.notice.minifiedAlbum` | `Minified album view — track details load on the full album page.` |
| `detail.notice.goToLibrary` | `Go to Library` |
| `detail.play` / `detail.shuffle` / `detail.aboutRelease` | `Play` · `Shuffle` · `About this release` |
| `detail.addToPlaylist` / `detail.copyToPlaylist` / `detail.playNext` / `detail.addToQueue` | `Add to playlist` · `Copy to playlist` · `Play next` · `Add to queue` |
| `detail.filter.find` / `detail.filter.short` | `Find` · `Filter` (the band's two words) |
| `drag.addTo(name)` / `drag.cantEditPlaylist` / `drag.cantAddArtist` / `drag.nothingToAdd` | the drop caption and its three refusals |
| `detail.badge.album` / `.ep` / `.single` / `.compilation` | the eyebrow's `BadgeType` half (`DetailPage.MapAlbum`) |

**Preference epochs** — `DetailHeroPrefs.Epoch` is the ONE bump behind three Settings rows (Track page layout, "Keep left-rail same size", "Clear all remembered sizes"). A render subscribes to it and an effect re-seeds `_railUniform` and all four per-scope width/collapsed signals through `ResyncRail` (clamped exactly as at mount), so an already-mounted — including a KeepAlive-parked — page reflects any of the three **without a relaunch** (`DetailShell.cs:463-472`, `:168-172`, `:503-511`).

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate (skeleton until true) |
|---|---|---|---|
| Hero/rail cover | `DetailModel.Cover` ← Album/Playlist/Show/`LikedCoverRules` | `a.ImageId` / `p.ImageId` / `s.ImageId` (StringId) | `Knows(Image)`; Liked composes from the first N liked tracks' images |
| Title | `DetailModel.Title` | `a.TitleId` / `p.NameId` / `Loc.Get(detail.likedSongs)` | `Knows(Title)` |
| Eyebrow "ALBUM · 2013" | `BadgeType` + `Year` | `a.Kind` (byte) + `a.Year` (ushort) | `Knows(Identity)` |
| Eyebrow "Playlist · Collaborative/Private" | `Capabilities.IsCollaborative`, `IsPublic` | `p.Flags` bits `Collaborative`, `Public` + `Knows(Capabilities)` | `Knows(Capabilities)` — an unknown block must not read as "private" |
| Attribution (billed artists) | `Artists` (ArtistRef[]) | `Edges.AlbumArtists.Targets(a.Slot)` → `Artist.TitleId` | edge `State == complete` |
| Attribution (owner) | `OwnerName`, `OwnerImage` | `p.Owner` (user slot) → `User.NameId` / `ImageId` | `Knows(Owner)` **and** the owner row's `Knows(Identity)` |
| Collaborator facepile | `Collaborators`, `UserProfilesById` | **DATA GAP** (below) | |
| Meta line "13 songs · 1 hr 14 min · 2013" | computed in the mapper from `TrackCount` + `Σ DurationMs` | computed on the model at commit: `a.TrackCount`, `a.TotalMs` column | `Edges.AlbumTracks.State == complete` **and** every row `Knows(Duration)` — the 0.2.9 rule (`DetailPage.cs:695-699`) drops the duration segment entirely while any row is thin; port it |
| Meta line (playlist, saves) | `PlaylistPopcount` with a 250 ms grace | **DATA GAP** (below) | the segment is omitted, never "0 saves" |
| Meta line (show) | `Publisher + " · " + EpisodeCount(TotalEpisodes)` | `s.PublisherId`, `s.TotalEpisodes` | `Knows(Identity)` |
| Meta-line shimmer | `MembershipLoaded` | `Edges.PlaylistTracks.State[p.Slot] == 0 (unknown)` | state 0 ⇒ shimmer bar shaped as "00 songs · 0 hr 00 min" |
| Description | `Description` (HTML fragment) | `p.DescriptionId` / `s.DescriptionId` (StringId, pre-parsed spans **DATA GAP**) | `Knows(Description)` |
| Accent / tone / tint | `CoverColorPlane` grading of the cover url | **DATA GAP** (below) | never gates anything — the page renders neutral until it lands |
| Notice strip | `DetailModel.Notice` ← `PlaylistPageNoticeRules` | `p.Notice` (byte column) computed at commit; album thinness from `AlbumTracks` rows | always known (it is a derived fact on the model, never probed by the UI) |
| Tracks / episodes | `Tracks` / `Episodes` | `Edges.AlbumTracks` / `PlaylistTracks` / `Liked` / `ShowEpisodes` | edge `State == complete` for the count; rows paint from `TrackFields.Row` |
| Date-added column gate | `HasDateAdded` (scan) | `Edges.PlaylistTracks.Payload[].AddedAt != 0` — precompute a `HasDateAdded` flag on the parent at edge commit | edge complete |
| Added-by column gate | `HasAddedBy` (≥2 distinct contributors) | same: a `Contributors` byte on the parent at commit | edge complete |
| Video gate | `HasVideo` (per-track probe) | `Track.Flags.HasVideo` folded into a parent flag at commit | never blocks: it only adds a lane |
| Release facts panel | `ReleaseFacts` (computed once in the mapper, gated on `HydrationLevel.Full`) | `a.Label/Copyright/Courtesy/ReleaseDate/Precision` + `AlbumReleaseFactsRules.For(...)` at commit | **gate the whole record on `Knows(Publishing)`** — the panel must appear once, complete (`DetailPage.cs:737-740`) |
| Other versions | `OtherVersions` | `Edges.AlbumVersions` (**tree gap**: the plan has `ArtistReleases`/`ArtistAppearsOn` but no album-versions edge) | edge complete |
| Trailing sections | five independent async reads behind one region | `Edges.ArtistRelated`, `ArtistReleases`, plus **DATA GAPS** (featured-on, merch, similar) | one region, one skeleton, `SmoothResize: true` |
| Daylist countdown | `ExpiresAtMs`, `CreatedAtMs` | **DATA GAP** | row exists only when > 0 |
| Chart caption | `ChartNewEntries`, `ChartUpdatedAtMs` | **DATA GAP** | row exists only when > 0 |
| Pre-release countdown | `UpcomingAt` ← `PreReleaseDerivation` | `a.ReleaseInstant` + per-row `AvailableAt` + `a.PreReleaseEnd` | derived on the model at commit with ONE `now` read |
| Pre-save heart target | `PreReleaseUri` (kind 138) | **DATA GAP** | falls back to the album uri |
| Share url | `ShareUrl` | derived from the uri (`SpotifyLink.WebUrl`) — pure, no column | always |
| Episode load-more | `PagedThrough` vs `TotalEpisodes` | `Edges.ShowEpisodes.Length` vs `.Total` (the CSR table already carries `Total` and `State`) | partial ⇒ the pill shows |

**Demand**: each page demands its whole model on mount — `Entities.Ensure(subject, Fields.All)`, `Entities.EnsureEdges(subject, EdgeKind.X)`, `Entities.EnsureRows(subject.TrackSlots, TrackFields.Row)` — one 300-uri batch per page, never a visible-window fetch (plan §4.13; CLAUDE.md "no page-side fetch windows").

### DATA GAPS

| what the frame shows | 0.2.9 source | proposed 0.3 column / edge |
|---|---|---|
| **Cover palette** (accent, page tone, shell tint, chrome scheme) | `SpotifyLive/CoverColorPlane` — a side plane keyed by image url, graded per theme, with a `Watch` signal per url | a `Palette` table keyed by `StringId` image id: `Column<uint> AccentDark/AccentLight/TextBase/BackgroundTintedBase`, `Column<byte> State`, ONE `Signal<uint> Changed`. Entities never own it (it is a *rendering* fact of an image), but it must exist and be watchable per-url or the 0.3 detail page has no colour at all. |
| **Visual identity** (Spotify's own per-entity colour, extended-metadata kind 179) | `Decode.VisualIdentity` seeds the plane | `Column<uint> IdentityColorDark/Light` on Album/Playlist/Show + `Known` bit |
| **Home card accent payload** | `DetailModel.Accent` ← `extractedColors.colorDark` on the nav preview | `Column<uint> CardAccent` on Playlist (written by the Home decode), read when no grading exists |
| **Playlist save count** | `svc.PlaylistPopcount.GetSaveCountAsync` + a 250 ms grace | `Column<int> Saves` + `Known` bit `Saves`; the meta line omits the segment while unknown or 0 |
| **Daylist window** | `playlist4` format_attributes, else the Home card's Pathfinder attrs merged in | `Column<long> ExpiresAtMs, CreatedAtMs` on Playlist + `Known` bit `Daylist` |
| **Chart header** | format_attributes `format=="chart"` | `Column<int> ChartNewEntries`, `Column<long> ChartUpdatedAtMs` |
| **Capabilities** | `PlaylistCapabilities` (Known/CanView/CanEditItems/CanEditMetadata/IsOwner/IsCollaborative) | `Column<uint> Flags` bits + a `Known` bit — **`Known` is load-bearing**: a thin header's all-false rights must never read as a revocation |
| **Collaborators + profile map** | `Playlist.Collaborators`, `UserProfilesById` | `EdgeTable<NoEdge> PlaylistCollaborators` (parent = playlist, targets = user slots); the Added-by cell resolves through the edge, not a dictionary |
| **Notice** | `DetailNotice` computed by `PlaylistPageNoticeRules` from header + create lifecycle | `Column<byte> Notice` on Playlist, written at commit by the ported rules; `Column<byte> Flags` bit `DeletedByOwner` |
| **Create lifecycle** (pending / failed) | `LibraryBridge.IsCreatePending/IsCreateFailed` | the intent journal (plan §4.4 `intent` table) exposes `Pending`/`Rejected` for a uri |
| **Release facts** | `Label`, `Copyright`, `CourtesyLine`, `ReleaseDate`, `ReleaseDatePrecision`, `DiscCount` | `Column<StringId> Label, Copyright, Courtesy`, `Column<int> ReleaseDate` (days since epoch), `Column<byte> DatePrecision, DiscCount` + `Known` bit `Publishing` |
| **Other versions** | `Album.OtherVersions` (getAlbum) | `EdgeTable<NoEdge> AlbumVersions` |
| **Trailing enrichment** | `AlbumEnrichment` (about-artist, related artists, recommended playlists, merch, similar albums) | `EdgeTable<NoEdge> AlbumFeaturedOn, AlbumSimilar`; merch needs a small `MerchTable` (name, price StringId, image, shop url) or a per-album blob |
| **Pre-release link** | kind 138 `PreRelease.ResolveAsync` | `Column<int> PreReleaseSlot` on Album (0 = none) + `Known` bit; resolved only when the album already looks upcoming |
| **Per-track availability** | `Track.AvailableAt` | already in the plan (`AvailableAt`, §4.2) ✔ |
| **Rolling identity (daylist) merge** | `DetailHeaderMergeRules` over the nav preview | keep the rule; its inputs become "the row's `FetchedAt` vs the card's" — a rolled-over playlist's resident row is stale by construction |
| **Show paging** | `Show.PagedThrough`, `TotalEpisodes` | `Edges.ShowEpisodes.Length` / `.Total` / `.State` ✔ (already in the plan) |
| **Description rich text** | HTML fragment parsed per render by `RichText` | parse **once at commit** into a span table (`Column<int> DescSpanStart/Len` into a shared span slab) — a per-render HTML parse on a 10k playlist page is exactly the cost 0.3 exists to remove |

---

## 8. Pure rules to port verbatim

| name | file (0.2.9) | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `DetailLayoutBreakpoints` | `Features/Detail/DetailLayoutBreakpoints.cs` | tier ladder 860/720/560/440/340/300 + 24 hysteresis; mode ladder 820/660/560 + 24; vertical 540/580; `ContentMinWidthForMode`; `EstimatePageWidthFromViewport` (viewport − 240) | `Wavee.Tests/DetailLayoutBreakpointTests.cs`, `DetailCoverStabilityTests.cs` | `Entities/Detail.cs` CORE |
| `DetailVerticalLayout` | `Features/Detail/DetailVerticalLayout.cs` | the whole hero arithmetic: flow seam 424/400, artwork curve, content/title measures, the title TYPE PLAN (`TitleTypeFor`, `SnapTitleSize`, `StableTitleSize`, `FluidTitleCapFor`, `TitleAdvanceEm`, `TitleLongestWordEm`), `IdentityChrome`/`IdentityHeightFor`/`IdentityGapFor`, `HeroBandHeight`, `CollapseDistance`, `ChromeExtent`/`StickyClipInset`, `ItemRole`/`ItemCount`/`FooterIndex`, `ArtworkDecodePx`, `BucketW` | `DetailVerticalLayoutTests.cs` (615 lines, 36 facts), `DetailSkeletonGeometryTests.cs`, `DetailVerticalFooterTests.cs` | `Entities/Detail.cs` CORE |
| `ContextBandLayout` | `Features/Detail/ContextBandLayout.cs` | band height 56, hairline 1, clip inset 56, feather 24, cluster 24 / pivot 16 / action 16 gaps, pivot padX 8 / action padX 10, underline 2/4, `TitleCap` 280, `EstimateLabelWidth` (`AvgCharW` 7.6), `ActionsWidth`, the scroll spy (`SpyProbe` 8, `SpyViewportFraction` 0.25, `EndProbe` 8, `SpyLine`, `ActiveSection` incl. its **−1 "no answer"** contract, `IsAtScrollEnd`, `ScrollTargetFor`) | `ContextBandLayoutTests.cs` | `Entities/Detail.cs` CORE (shared with `Artist.Page`) |
| `DetailTrackCommandBarLayout` (the three constants this frame reads) | `Features/Detail/DetailTrackCommandBarLayout.cs:41-46` | `SearchIconWidth` 66 · `SearchPreferred` 240 · `SearchMax` 280 — the band's collapsed/expanded field clamp (`DetailTracks.CompactSearchWidth`) and the skeleton's toolbar search pill. The *fit solver* itself is chapter 04's; these three are frame geometry | `DetailTrackCommandBarLayoutTests` (chapter 04) | `Entities/Detail.cs` CORE (shared with the table) |
| `DetailTrackTableRules.HeaderHeightFor` | `Features/Detail/DetailTrackTableRules.cs:27,49` | `classic ? 32 : 36` — the middle term of `StickyClipInset`, which today hard-codes 36 (see §3's clip rows) | `DetailTrackTableRulesTests` (chapter 04) | `Entities/Detail.cs` CORE |
| `DetailRailPolicy` | `Features/Detail/DetailRailPolicy.cs` | which mode resizes (0 only), 180/480 bounds, per-scope defaults, `ClampStored`, `ScopeFor(uniform)`, `HasCustomizedRailPrefs` | `DetailRailPolicyTests.cs` | `Entities/Detail.cs` CORE |
| `DetailRevealRamp` | `Features/Detail/DetailRevealRamp.cs` | chunk 12, cap 60, `Done`, `Next`, `Revealed` | `DetailRevealRampTests.cs` | `Entities/Detail.cs` CORE |
| `DetailHeaderMergeRules` | `Features/Detail/DetailHeaderMergeRules.cs` | rolling-identity title/cover preference | `Actions/DetailHeaderMergeRulesTests.cs` | `Entities/Detail.cs` CORE |
| `PlaylistPageNoticeRules` | `Features/Detail/PlaylistPageNoticeRules.cs` | `Next`/`Cold`/`ForAlbum` — the notice ladder, incl. sticky `CreateFailed`, create-pending suppression, unknown-capabilities hold | `Actions/PlaylistPageNoticeRulesTests.cs` | `Entities/Playlist.cs` CORE (written at commit) |
| `PlaylistListState` | `Features/Detail/PlaylistListState.cs` | Loading / Empty / NoMatch / Rows from (membershipKnown, total, visible) | `PlaylistListStateTests` | `Entities/Detail.cs` CORE |
| `PreReleaseDerivation` | `Features/Detail/PreReleaseDerivation.cs` | `UpcomingAt` precedence ladder, `ReleaseInstant` | (covered through album tests) | `Entities/Album.cs` CORE |
| `AlbumReleaseFactsRules` | `Features/Detail/AlbumReleaseFactsRules.cs` | the About-this-release record (Songs/Length/Released tense/Label/Notes) | `AlbumReleaseFactsRulesTests.cs` | `Entities/Album.cs` CORE (chapter 05 owns the panel) |
| `DetailLiveRefresh` | `Features/Detail/DetailLiveRefresh.cs` | leading pass + single-flight + 50 ms cooldown + storm tripwire | `DetailLiveRefreshTests.cs` | **delete** — 0.3 has no re-projection; keep the *tests'* intent as a guard if a batching layer reappears |
| `DetailOwnerIds` | `Features/Detail/DetailOwnerIds.cs` | which user ids an open playlist page renders | `DetailOwnerIdsTests.cs` | **delete** — edges publish per table |
| `DetailConfig` literals | `Features/Detail/DetailConfig.cs:196-228` | the per-kind knob table (below) | — | `Entities/Detail.cs` CORE as a `static ReadOnlySpan<DetailConfig>` indexed by kind |

### The per-kind configuration (`DetailConfig.cs:191-229`) — reference table for chapters 05/06/07/09

Resolution is `DetailPage.ResolveConfig(kind, m)` (`:331-342`), POST-load because it reads `m.ReleaseKind`: Playlist / Liked / Show are fixed by route; the album route then branches `AlbumKind.Single → Single`, `AlbumKind.Compilation → Compilation`, **everything else (Album *and* EP) → `DetailConfig.Album`**. There is no "≤2 tracks" rule despite `DetailConfig.cs:16` saying so, and **EP has no column of its own below** — it is the Album column with an `"EP"` `BadgeType` in the eyebrow (`DetailPage.MapAlbum`).

| knob | Playlist (`pl:`, `local`) | Album + **EP** (`album:`, `prerelease:`) | Single (`ReleaseKind == Single`) | Compilation | Liked (`liked`) | Show (`show:`) |
|---|---|---|---|---|---|---|
| `TwoColumn` | true | true | true | true | true | true |
| `RailWidth` | 240 | 280 | 280 | 280 | 240 | 280 |
| `Badges` | `OwnerRow` | `TypeYear` | `TypeYear` | `TypeYear` | `None` | `TypeYear` |
| `ShowArtThumb` | true | false | false | false | true | false |
| `ShowAlbumColumn` | true | false | false | false | true | false |
| `Columns` | `ListColumns` [36, ★, 200, 40, 52] | `AlbumColumns` [36, ★, 40, 52] | Album | Album | List | Album |
| `CapTitle` | true | false | false | false | true | false |
| `Selection` | Extended | Extended | **None** | Extended | Extended | **None** |
| `HasTrailing` | false | **true** | **true** | **true** | false | false |
| `Heart` | `Follow` | `Save` | `Save` | `Save` | `None` | `Follow` |
| `ShowPlays` | false | **true** | **true** | **true** | false | false |
| `ShowTrackArtist` | true | false | false | **true** | true | false |
| `Content` | Tracks | Tracks | Tracks | Tracks | Tracks | **Episodes** |
| `Recommendations` | **true** | false | false | false | false | false |
| `ShowTempo` | **true** | false | false | false | **true** | false |
| `ShowVersions` | true | true | true | true | true | **false** |
| `PlaysColumnOptIn` | **true** | false | false | false | **true** | false |
| `RailScope` | Playlist | Album | Album | Album | Liked | Show |
| `RailResizable` | true | true | true | true | true | true |

`CapTitle` has **no reader** outside the literals (verified by grep) — do not port it. `TwoColumn: false` likewise has no literal — the single-column fallback at `DetailShell.cs:476-477` is unreachable; keep the branch only if a new kind needs it.

---

## 9. Re-author notes

**Must not be simplified.**

1. **The two hero SYSTEMS are not two designs.** The rail arm and the hero arm share the eyebrow string (`DetailRail.EyebrowText`), the eyebrow run, the artwork treatment, the CTA grammar, the daylist/chart/prerelease cards and the release panel. Dropping one arm to "simplify" loses the narrow window entirely; re-implementing one arm's parts separately is how the same album ends up worded two ways across a resize.
2. **The type plan.** `TitleTypeFor` is not a lookup that can be replaced by a rung ladder — the old ladder's authored line heights were silently discarded by the engine's line stacking (`NaturalLineRatio` 1.3301 > every authored pair above 20), which under-reserved the band by ~48 DIP at the top rung. Port `NaturalLineRatio`, the snap grid, the packing constants (1/1.08, 0.94, 0.78) and `StableTitleSize`'s asymmetric hysteresis **verbatim**.
3. **`HeroBandHeight` has two consumers that must never disagree** — the skeleton's reserved band and the loaded hero's pre-measure collapse binds (`DetailTracks.cs:1643-1654`). Both build the *pessimistic null-title* plan. A second number here is D49 all over again.
4. **The reveal ramp's blank placeholder is deliberate** (blank grid cells, not grey bars) — bars that vanish under a row still at opacity 0 read as bars → blank → text (`DetailTracks.cs:2613-2622`).
5. **The offset model.** The band paints nothing and the page owes it a clip. If the 0.3 band grows a fill "for contrast", it becomes a black slab on a dark wallpaper again (`ContextBand.cs:71-96`).
6. **The ownership hand-over for the shell tint.** Never clear on unmount; claim on activation. A clear produces a neutral dip between two coloured pages.
7. **The `MembershipLoaded` shimmer** in the meta row. "0 songs · 1 min" for a playlist whose rows have not landed is a lie the user sees for ~300 ms on every cold open.
8. **`showMinifiedAlbum: false` on the full page.** The strip would appear for ~0.4 s and take ~94 px of hero with it.

**Traps.**

- **Props freeze at mount** — see §1.2's five solved hazards. In 0.3 the frame is static functions, which removes the trampoline machinery (`DetailShell.cs:395-448`, ~55 lines) but *not* the keyed-remount rules for the Components that remain (SaveButton, the countdowns, the facepiles, the inline-edit facades).
- **`Key` changes are remounts, and remounts reset scroll.** 0.2.9 learned this twice: the track list's key deliberately **excludes** the tier (`DetailTracks.cs:1074-1080`) because a tier-keyed remount seeded the new viewport from `ScrollMemory` before the old one wrote its offset; and `right` keeps its key across a rail collapse (`DetailShell.cs:750-752`) for the same reason. The route **is** in the key (`"tracks:standard:"+route.Name`) because a route swap is a new scroll/hero identity.
- **ReuseGuard**: the vertical list's prefix slots (hero, chrome) are `PersistentPrefixCount 2` and must never recycle; the footer is a *slot*, not a wrapper, so it stays outside the insertion range (`DetailVerticalFooterTests`).
- **Zero-allocation scroll frames vs per-row richness** — how 0.2.9 reconciled them, and the pattern to copy: the frame publishes ONE equality-gated snapshot (`rowsSnapshot`) and ONE column-shape memo (`rowShape`) that the *rows* read through their own subscriptions; the parent never re-renders the list for a track change, a sort, a breakpoint or a palette arrival. The palette work lives in 0×0 leaves (`tintBinder`, `tonePlane`) so a graded batch repaints one node. The reveal clock is mounted only while ramping and unmounts itself so the frame loop quiesces (`DetailTracks.cs:1142-1150`).
- **Never write a signal from `Render`.** 0.2.9 publishes `_liveHandlers`, `_visibleCount`, `_verticalItemCount`, `_verticalFacts` from effects; `_resetEpoch` is a plain field written in Render *because* it is not a signal.
- **`SectionAnchors.Reset()` must be called from Render, never an effect** (`ContextBand.cs:36-44`) — an effect erases the registrations of the frame that made them. Only relevant if the detail band ever grows a pivot.
- **The mode's two fail-safes are not belt-and-braces, they are the fix** (§6). Dropping either the `Math.Max` pre-measure seed or the per-render `fit > mode` self-heal reintroduces the hero-remount flicker `EstimatePageWidthFromViewport` was written for.
- **The skeleton and the hero must agree on the BLOCK SET, not just the total.** 0.2.9 already fails this for one kind (W28: `hero-chart` is emitted but not reserved). The 0.3 rule: every `Add(...)` in `Hero` has a matching presence flag in `IdentityChrome`, and `DetailSkeletonGeometryTests` enumerates the flags so a new row cannot be added without one.
- **`BucketW` everywhere or nowhere.** `DetailSkeleton.VerticalHeroBand` derives pad/gap/art/content/title from `bw = BucketW(colW)` but passes the raw `colW` to `HeroBandHeight` for its `MinHeight` (`DetailSkeleton.cs:43` vs `:112`) — up to a 4-DIP disagreement between the shimmer it draws and the floor it declares. In 0.3 bucket once, at the top, and pass the bucketed width down.

**Things this frame does that a "cleaner" 0.3 will be tempted to change, and must not.**

- The `showToolbar` asymmetry (`DetailShell.cs:518`): a show in the vertical arm gets **no** episode toolbar. Deliberate today; if `Show.Page` changes it, change it knowingly.
- `SelectionMode = visible > 0 ? cfg.Selection : None` (`DetailTracks.cs:1499`) — a loading or empty list is not selectable, so a rubber-band over shimmer cannot select placeholders.
- `right` keeps `Key = "right:tracks"` across a rail collapse (`DetailShell.cs:750-752`) and the inner list key excludes the tier (`DetailTracks.cs:1074-1080`). Both are scroll-preservation fixes with their own regression history.
- The rail's Liked arm differs from every other rail in **exactly one** property — no `Fill = Tok.FillLayerDefault` (`DetailRail.cs:276-298`). Same scroller, same padding, same gap. Do not "unify" it and do not add a second difference.

**Where the plan is wrong or too thin for this surface.**

- **§2 had no shared detail file when this chapter was first written.** Album, Playlist, User and Show pages each get `X.Page.cs`, and the frame (≈6,400 lines in 0.2.9) had no home. Without a shared file the frame is written four times, or once and copied — which is how the hero and the rail drifted in 0.2.7 and had to be re-merged. **Settled by A1 (2026-09-12): `Entities/Detail.cs` (CORE, 900) and `Entities/Detail.UI.cs` (UI, 2,600) are now in §2**, owner M, Wave 4.5.
- **§5 gives the frame no slot and no owner, and that is the plan's largest single hole.** Wave 4's four owners are Shell (I), Sidebar (J), Rail/Deck/Lyrics (K), Design/Controls (L); Wave 5's five are M/N/O/P/Q by entity. The biggest shared UI in the app — **5,754 lines across its 17 dedicated `Features/Detail/*` files plus ~620 frame-owned lines inside `DetailTracks.cs` ≈ 6,400** — appears in neither table, and the other chapters each defer to the next: this chapter's header said "Wave 5 owners M and O (shared)", `04-detail-track-table.md:13` says "Wave 5 owners M (album/show) and O (playlist/liked)", `05-album.md:1198` says "owned by whoever writes **03/04**", and `06-playlist.md:1215` records the same gap again. Nobody is named anywhere, so Wave 5 opens with five pages whose common frame does not exist and the "DISJOINT files" rule is violated on day one.

  **Decision — the frame gets its own slot, its own owner and its own gate.**

  | | |
  |---|---|
  | **Slot** | **Wave 4.5** — a single-owner slot that runs *after* Wave 4 closes (it needs L's `Design.cs`/`Controls.cs` vocabulary: `WaveeCta`, `Surfaces`, `Tok.*`) and *before* Wave 5 opens |
  | **Owner** | **M**, alone. Not L: `Design.cs`/`Controls.cs` is the entity-free visual vocabulary and the frame is written over entity handles (`Album`, `Playlist`, `User`, `Show`) — putting it in L inverts the dependency. M already owns the frame's first consumer (`Album.Page`) and, per `04-detail-track-table.md`'s §9, the shared `Track.Table.cs` |
  | **Files** | `Entities/Detail.cs` (CORE ~900) · `Entities/Detail.UI.cs` (UI ~2,600) · `Entities/Track.Table.cs` (chapter 04's proposal) · `Entities/Track.UI.cs` (the row — the table's leaf) — all four pulled out of Wave 5 into this slot, and struck from M's Wave 5 row |
  | **Gate** | `dotnet run -- --fake`, route `album` / `spotify:album:al3`, Debug **and** Release: the frame renders that album from the fake seed through the real `Detail.Frame` — both arms (mode 0 at 1280 and mode 3 at 520), the skeleton→loaded transition with no shove, a rail drag that persists, and the tone plane + shell tint landing. Concretely: **parity items 1, 6, 8, 24, 29, 35 pass on the album route alone** (see the gate item 67 in §10). ReuseGuard silent on a resize across 540/580. |
  | **Then** | Wave 5 opens with M finishing `Album.Page`/`Show.Page`, O starting `Playlist.Page`/`User.Page` against a green frame, N consuming `Detail.Band` for the artist compact bar |

  **This decision must be mirrored, not re-litigated, in three other chapters** (they currently point at each other): `04-detail-track-table.md:13` and its §9 `Track.Table.cs` bullet; `05-album.md:1198` (#21, "whoever writes 03/04"); `06-playlist.md:1215`. All four chapters must read *owner M, Wave 4.5*. Those edits are out of this file's scope and are listed here so the next pass makes them.
- **§4.13's Album page sketch is a five-child column** (`Hero, TrackList, About, MoreBy, Versions`). The real frame has: a tone plane and a tint leaf as ZStack siblings, a notice strip, a two-column row with a resizable/collapsible rail, a 4-mode responsive ladder with hysteresis, a vertical hero system with a scroll-linked collapse into a 56-DIP band, a sticky two-stratum chrome with a clip contract, a skeleton that reserves the hero band, and a reveal ramp. The sketch is not wrong, it is 5% of the surface.
- **§4.12's `Track.Row` sketch omits the column shape as a signal** — in 0.2.9 the shape is a memo the rows read (that is what let a breakpoint cross patch in place). If 0.3 freezes `RowStyle` into the bound template, the tier ladder cannot work without remounting the viewport.
- **§4.1's "one signal per table"** is right for the rows but not sufficient for this frame: the *palette* is not a table and has no signal in the plan (see §7 DATA GAPS). Without a per-url watchable grading, the detail page has no accent, no tone and no shell tint.
- **§7's risk table has no entry for the 0.2.9 flicker classes** this frame spent three releases fixing: the cover-hash flash, the hero-remount flicker on a first measure, the D49 shove, the tier-remount blank list. Each is a *shipped fix with a test*; a rebuild that does not port `PreferVisible`, `EstimatePageWidthFromViewport`, `HeroBandHeight` and the non-tier-keyed list will reintroduce all four.

**Line budget.**

| | lines |
|---|---|
| 0.2.9, this surface | ~6,400 (5,780 dedicated files + ~620 frame-owned lines inside `DetailTracks.cs`) |
| Plan §2 target | **settled by A1: `Entities/Detail.cs` 900 + `Entities/Detail.UI.cs` 2,600, owner M, Wave 4.5** — before this chapter's edit, none: no file, no owner, no wave slot; as written the frame would have had to fit inside `Album.UI 600 + Album.Page 1200` and `Playlist.UI 700 + Playlist.Page 1500`, twice over |
| Honest estimate | **~3,500 for the shared frame** (CORE ~900: the six pure classes ported verbatim + the config table; UI ~2,600: hero 650, rail 550, compact/show header 200, band 200, skeleton 200, notice 60, tone/tint mounts 120, frame composition + modes + grip + drop target 620) **+ ~700 album trailing** in `Album.Page.cs`. The per-kind pages then cost 300–600 each instead of 1,200–1,500. |

The reduction from 6,400 → 3,500 is real and comes from: no `DetailModel` record and no four mappers (−900), no live-refresh pump / owner-ids / store predicate (−350), no cover latch or header merge plumbing at the page level (−150, the rule stays), no handler trampolines (−120), no hydration-level threading (−200). It does **not** come from dropping arms, modes, hysteresis or motion.

**Files this surface needed and did not have when this chapter was first written**: `Entities/Detail.cs`, `Entities/Detail.UI.cs` (now in §2, settled by A1, owner M, Wave 4.5); a home for `ContextBand`'s shared half (the artist page uses the same helpers — `ArtistCompactBar.cs:48,78,100`, `ArtistPage.cs:232,321,328,338` — put them in `Detail.UI.cs` and let `Artist.Page` call them, or the band gets written twice and Wave 5 owner N blocks on M); a home for the palette plane (`Design.cs` in §2 is 1 of 2 files under `Platform/` and carries no colour-grading service).

**Wave-slot summary** (the one line the plan's §5 is missing): `Wave 4.5 — owner M — Entities/Detail.cs, Entities/Detail.UI.cs, Entities/Track.Table.cs, Entities/Track.UI.cs — gate: --fake renders spotify:album:al3 through the shared frame in both arms (§10 item 67).`

---

## 10. Parity checklist

Verify side by side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`
Routes (deep link `wavee://open?route=…&arg=…`, or navigate from Home/sidebar): **A** = `album` / `spotify:album:al3` · **P** = `pl` / `spotify:playlist:pl1` · **L** = `liked` · **S** = `show` / `wavee:show:2` · **Local** = `local`.
"Static capture" = one screenshot; "hover capture" = screenshot with the pointer parked; "frame recording" = a capture sequence over the motion (≥ 30 fps).

**Layout & breakpoints**

1. **A @ 1280 wide, mode 0**: rail is exactly 280 wide, cover 256, gap 14 between rail rows. Static capture, measure in pixels ÷ scale.
2. **A @ 1280**: the `[rail | right]` row is centred and never wider than 1600 — widen the window to 1900 and confirm the content stops growing and gutters appear. Static capture.
3. **P @ 1280**: rail 240, cover 216 (playlist scope), not 280.
4. **Resize A from 1280 → 700**: rail steps to 224 exactly at the 820 page-width crossing, not before. Frame recording of the drag.
5. **Resize A from 700 → 600**: rail steps to 188 at 660. Frame recording.
6. **Resize A 600 → 520**: the page switches to the vertical hero system at 540, and back to two-column only at 580 — drag slowly across 540–580 and confirm exactly one flip in each direction (no chatter). Frame recording.
7. **Resize A 1280 → 830 → 806 → 830**: the mode does **not** oscillate; mode 1 is held until 844 on the way back up (24-DIP hysteresis). Frame recording.
8. **Vertical @ 520**: artwork is 197 (beside the copy); @ 380 it is 280 (above the copy) — and the flip happens at 424 going down, 400 going up. Two static captures + a frame recording across the seam.
9. **Vertical @ 380**: hero padding and the art/copy gap are both 16, not 24.
10. **Settings › Appearance › Track page layout = Hero** at 1280: the rail disappears at every width and the hero system renders with a 240 cover and a ≤96-point title; switching back to Automatic restores the rail **without a relaunch**. Two static captures.

**Rail**

11. **A @ 1280**: drag the grip to 180 — the rail tracks 1:1 down to 180; below it the rail's content fades to ~35% and the rail stops shrinking. Frame recording.
12. Continue pushing ~44 DIP past the floor: the rail collapses to the 96-DIP identity strip with a readable cover and a two-line title; the track list does **not** reset its scroll offset. Frame recording + static capture after a scroll.
13. Pull the collapsed grip right: it re-opens only past 220, not at 140. Frame recording.
14. Release a drag, navigate away and back: the rail returns at the dragged width (persisted on release, not during).
15. Resize **P**'s rail, then open **A**: the album rail is unchanged (independent scopes). Two static captures.
16. Turn on **Settings › Appearance › Keep left-rail same size**, resize on **L**, open **A** and **S**: all four surfaces share the new width; turn it off and each returns to its own. Static captures.
17. **L @ 1280**: the Liked rail has **no** `FillLayerDefault` panel fill (it sits on the page ground) while A/P/S rails do. Static capture, compare the rail's background against the page.
18. **A @ 1280, short window (height 700)**: the rail title is 28/36 and the description caps at 3 lines; at height 1000 the title is 40/52 and the description caps at 6. Two static captures.

**Hero (vertical system)**

19. **P @ 520**: the order is eyebrow → title → 20×2 accent rule → attribution → meta → actions → description, left-aligned, with the rule's 2-DIP top margin. Static capture.
20. **A @ 1100 with layout = Hero, title "Pony"-length**: the title grows to fill the cover's height (≈88 pt) instead of leaving an empty band; a long title (≥ 20 chars) drops to ≈52 and stays one line. Two static captures of different albums.
21. **P @ 1100 with a short identity** (no description): the surplus opens **between the metadata and the action row**, and the action row lands on the cover's bottom edge — not as dead space under the actions. Static capture.
22. **P @ 520**: the action row wraps as Play + satellites without overflowing the column. Static capture at 400 and 380.
23. **L**: the hero cover is the dynamic Liked treatment with its style picker (click opens it); at the 96-DIP compact rail it is the flat mosaic/stock, with **no** picker. Two hover captures.

**Skeleton & reveal**

24. **Cold deep link to A** (no nav preview, clear the app first): the page opens with the hero band already reserved — the toolbar, column header and first row do **not** jump down when content lands. Frame recording from click to loaded.
25. **Same, from a Home card**: the skeleton's hero slot shows the **real** preview cover (not a grey square) and the loaded hero keeps the same cover id — no fade-out/fade-in of the same picture. Frame recording.
26. **P with 50+ tracks, cold**: rows reveal in chunks of 12 over several frames, each row cross-fading; no single frame swaps the whole band. Frame recording at ≥ 60 fps.
27. **A (HasTrailing) re-opened from Back (warm)**: the ramp still runs (album pages ramp on every context edge). Frame recording.
28. **P whose membership has not landed**: the rail meta row is a shimmer bar shaped like "00 songs · 0 hr 00 min", never "0 songs · 1 min", and the rows are shimmer, never "Nothing here yet". Static capture during load.

**Context band & sticky**

29. **P @ 520, scroll past the hero**: the expanded hero translates up and fades over its last 96 DIP while the 56-DIP band fades in over the last 44; the two overlap (no gap, no double-header frame). Frame recording.
30. **Scrolled state**: the band shows title + "owner · N songs, duration" and the three text actions; it has **no** fill — the page tone shows through it — and there is exactly **one** hairline, under the column header. Static capture.
31. **Scrolled**: rows are cut at 93 DIP with a 24-DIP feather, not guillotined and not sliding under an opaque plate. Static capture, zoom on the cut.
32. **L with the chip rail visible, scrolled**: the clip inset grows to 141 (93 + 48) and the chips stay inside the stuck stack. Static capture.
33. **Click "Find" in the band**: the field takes the title's place (the actions do not move) and expands over 260 ms; Escape collapses it in 180 ms and returns focus to "Find". Frame recording.
34. **Select 2 rows**: the band's content swaps to the selection command bar at the same 56 height; deselect restores the identity block. Frame recording.

**Colour & material**

35. **A with a saturated cover** in dark theme: the page ground is a low-lightness, low-saturation tone of the cover's hue at ~20% alpha over Mica — the wallpaper is still visible through it. Static capture with a bright desktop wallpaper.
36. **Same album, light theme**: the ground is near-white; the record's hue is almost imperceptible (this is correct). Static capture.
37. **A with a greyscale cover**: the ground is the neutral `#151515`/`#F5F5F5`, not an invented tint. Static capture.
38. **Navigate A → P**: the window chrome (title bar, sidebar, player dock) cross-fades from A's tint to P's over ~250 ms and never passes through a darker neutral. Frame recording of the chrome, not the page.
39. **Navigate A → Settings → back to A**: the chrome goes neutral for Settings (claimed by the content host) and returns to A's tint on Back. Frame recording.
40. **Settings › Appearance › colour washes off**: the page ground is the plain content surface and the chrome is neutral on every detail page. Static capture.
41. **A while a track from A is playing, cover ungradeable (Liked/show)**: the page takes the now-playing track's colour instead of staying neutral. Static capture on **L** while playing a liked song.

**Notices, empty, error**

42. **P (fake backend) — force a notice** by opening a playlist uri that resolves to nothing: the strip reads "This playlist was deleted." with a "Go to Library" button, the rows stay on screen, and every edit affordance (title pencil, description, cover, reorder) is gone. Static capture.
43. **Embedded library pane on a minified album**: the strip shows "Minified album view…"; the **full** album page for the same album shows no strip. Two static captures.
44. **Filter a playlist to zero matches**: "No songs match your filter", centred, tertiary — not "Nothing here yet". Static capture.
45. **Deep link to a nonexistent album with the network down**: the error state is the centred 28/36 headline + caption, not a blank page. Static capture.

**Interaction & motion details**

46. **Hover the hero Play capsule**: it scales to 1.04 with an 83 ms fill ramp; press reaches 0.96. Frame recording.
47. **Hover a 32-DIP satellite**: fill appears (`FillSubtleSecondary`), radius 4, with a tooltip after the standard delay. Hover capture.
48. **Hover a band text action**: the *word* changes ink and does **not** scale or shove its neighbours. Hover capture + frame recording.
49. **Open the hero "More" flyout**: items and order are Add/Copy to playlist · Play next · Add to queue · (separator) Invite · Delete; a followed playlist shows "Copy to playlist". Two static captures (owned and followed playlist).
50. **Drag a track from the sidebar over the hero/rail column of an editable P**: the whole column accepts with the caption "Add to {name}". Drag a row **within** the same playlist over the hero: no refusal glyph, the gesture passes through to the list. Two frame recordings.
51. **Drag over an album page's hero**: nothing is offered and no refusal is shown. Frame recording.
52. **Drag the hero cover** of A onto a sidebar playlist: the drag chip shows the album cover and the drop appends. Frame recording.
53. **Live-edit narration**: with **P** open, add a track from another surface — the rows below part and the new row fades in at its slot with a 20 ms/row stagger; the scroll anchor does not jump. Frame recording.
54. **Reduced motion on** (OS setting): the page transition, the hero reflow, the late-row rises and the ramp all **snap** while opacity still cross-fades; no hook-count crash on toggling it mid-session (resize while it flips). Frame recording + a resize.
55. **Trailing sections on A**: one skeleton for the whole block from mount, easing (not snapping) into the real sections; "More by" shows ≤ 5 rows with "Show all N" expanding in place, never navigating. Frame recording + click.
56. **About this release on A**: the tiles' rects never move as Label arrives — only the text inside them refines, and Label appears as a note line under the tiles. Two static captures ~2 s apart after a cold open.

**Added by the 2026-09-12 audit** (each is a fact §2–§8 now states and the checklist previously could not catch)

57. **S @ 1280, two-column**: the show rail shows cover · "Podcast" eyebrow · title · Play/Follow/Share · description and **no** publisher or episode-count line anywhere in the rail. Static capture. (If 0.3 decides to add one, this item flips to asserting it — but the decision must be recorded, not drifted into.)
58. **S @ 480, vertical**: `BuildHeader` renders (cover 140, title `PageHero` 28/36, cover saturation **1.0** — visibly flatter than the 1.18 rail cover at the same window) and the episode list below it has **no toolbar**. Two static captures, A vs S at the same width, comparing cover saturation.
59. **A chart playlist (`ChartNewEntries > 0`) at 520, cold**: watch the toolbar's Y position across the model landing. In 0.2.9 it drops ~20 DIP when the "N new entries" caption arrives (W28). **In 0.3 it must not move.** Frame recording from click to loaded.
60. **Scrolled, vertical, both arms**: there is **no** opaque gradient cue at the top of the viewport — the only top treatment is the band's clip feather. Static capture at scroll offset ~200, comparing against the two-column arm (which *does* keep the stock cue).
61. **Scrolled, then click a row in the band's region**: the click reaches the band's actions, not the scrolled-away hero, and wheel over the band does not scroll the rows behind it. Two interaction captures (non-negotiable 18).
62. **A collaborative P @ 1100 hero, then resize to 900**: the collaborator facepile re-renders (it is keyed on `(int)contentW`) without flashing or losing its "+N" count. Frame recording across the resize.
63. **Row density = Classic, L @ 520, scrolled**: the column header is 32 tall, and the rows are cut at the band's inset with no 4-DIP band of dead space above the first visible row. Static capture, zoom on the cut. (This is the `HeaderHeightFor` / `ChromeHeaderHeight` mismatch; 0.3 should show *no* gap.)
64. **P @ 520, filter to zero matches, then select nothing and drag over the rows**: the empty list offers no selection (`SelectionMode None`) — no rubber band, no highlighted placeholders. Interaction capture.
65. **A @ 1280 launched cold into a 1280 window, first frame**: the first composed frame is already mode 0 — no visible flip from a narrower arm — and at a 700-wide launch the first frame is mode 1, never mode 0 then 1. Frame recordings of the first ~5 frames after launch (§6's two fail-safes).
66. **The rail's Play, before the list mounts**: click it on the very first frame of a cold open — it plays from index 0 rather than doing nothing (`PlayAllOverride` is null until `TrackList` fills it). Interaction capture.

**The Wave 4.5 gate** (§9's frame slot — this is not a parity item against 0.2.9's *polish*, it is the pass/fail that lets Wave 5 open)

67. **`dotnet run -- --fake`, route `album` / `spotify:album:al3`, Debug and Release, against the shared frame with no per-kind page work beyond Album**: items **1** (mode 0 geometry at 1280), **6** (the 540/580 vertical flip with no chatter), **8** (the 424/400 art flip), **24** (the skeleton reserves the hero band — no shove when content lands), **29** (hero→band cross-fade with no double-header frame) and **35** (the tone plane over Mica) all pass; ReuseGuard is silent across a 500→700→500 resize. Playlist / Liked / Show are **not** exercised by this gate — they are Wave 5 — but they must compile against the same `Detail.Frame` signature before the slot closes.

---

## 11. Audit log

Adversarial re-read of every assigned 0.2.9 source against §§0–10, 2026-09-12. Verdict: **the chapter's numbers are overwhelmingly right** — every value re-derived in sections 2, 3, 4 and 5 checked out (the 520 / 380 / 1100 title plans reproduce to the decimal, the splitter 16 / 180 / 480 / 44 / 220 / 0.35, the pill 36 + 18/6/18/7 + @0.90/@0.80 + 0x80/0xB3, the tone 0.20/0.30 and tint 0.14/0.05, the band 56/1/24/16/16/10/8/2/4/280/7.6, the skeleton fractions 0.32/0.40/0.62/0.35/0.68/0.55 and its 32-DIP floor, the motion 250/280/220/150/260/180/210/83/167/45/24/16, `Expressive.Fast` 250 + `DistBase` 8, `Elevation.Card` 8/2/#33 dark and 4/2/#1A light, and the whole `DetailConfig` matrix row for row). The corrections below are the exceptions.

| # | § | kind | correction |
|---|---|---|---|
| 1 | 2 · W22 | **wrong** | The show rail did not show "Publisher · 212 episodes". `rail:meta` AND `hdr:meta` are both gated `cfg.Badges != TypeYear` (`DetailRail.cs:199,402`) and `Show.Badges == TypeYear` (`DetailConfig.cs:225`); the hero — the one arm that reads `MetaLine` unconditionally — is unreachable for a show (`DetailShell.cs:512`). A show's `MetaLine` renders **nowhere**. Wireframe rewritten, with the hole called out as a decision for `Show.Page` rather than a bug to copy silently. |
| 2 | 2 · W7 | **wrong** (reasoning; answer right) | "title budget 0 (stacked) ⇒ size bounded by the fluid cap only" overstates it: stacked flow starves the HEIGHT term, and the WIDTH term still binds (1 line = 27.2 for this title). The cap wins here only because the 2-line candidate reaches it. Both candidates now worked out, plus the `BucketW` banker's-rounding step (380 → 384) the bucket number was asserted without. |
| 3 | 8 | **wrong** | The config table's Single column read "≤ Kind.Single", echoing a stale comment at `DetailConfig.cs:16`. The real rule is `ReleaseKind == AlbumKind.Single` in `ResolveConfig` (`DetailPage.cs:331-342`); no track-count rule exists. Resolution paragraph added. |
| 4 | 8 | **missing** | **EP** had no place in the config table and no note: it is the Album config carrying an `"EP"` `BadgeType`. Column header and paragraph now say so. |
| 5 | 3 | **wrong** (under-specified) | "Rail meta 12/16/400" omitted its gate — the row exists on **playlist / Liked only**; album and show rails have no meta line. Row rewritten with the gate and the `Width = cover` clamp. |
| 6 | 2 · W5 | **wrong** (name) | "180 (Min / SnapThreshold)" — `SnapThreshold` is not a field. `SplitterOptions` is `{Min, Max, ForcePush, ReExpand}` (`Splitter.cs:60-73`); the name lives only in `DetailShell.cs:195`'s prose. |
| 7 | 2 · **new W28** | **missing (a live defect)** | `hero-chart` (`DetailVerticalHero.cs:226`) is emitted by the hero but has **no presence flag** in `IdentityChrome` (`DetailVerticalLayout.cs:390-402`), no flag on `DetailSkeleton.VerticalHeroBand` (`:39-41`) and no `HeroHasChart` predicate (`DetailTracks.cs:1659-1670`). A chart playlist's caption lands outside the reserved band — D49 for one kind, still shipping. New wireframe W28, a §9 trap, and parity item 59. |
| 8 | 0 · 16 | **missing** | Both vertical scrollers set `EdgeCues = ScrollEdgeCues.None` (`DetailTracks.cs:1519-1527`, `:1841`) because the stock cue resolves its colour by an ancestor walk that misses the tone plane (a ZStack sibling) and paints an opaque slab over the unpainted band. A direct consequence of the offset model, trivially undone in a rebuild. Now a non-negotiable. |
| 9 | 0 · 17 | **missing** | Never stated that `ContextBand.Row` has exactly one call site (`DetailVerticalHero.cs:393`) — modes 0/1/2 have no band, no gutter and no clip. Added, because "generalise the band to the rail arm" is the obvious wrong simplification. |
| 10 | 0 · 18 | **missing** | The `onStuck` flag is an INPUT handoff, not just visibility: `HitTestVisible = compactCanHit` + `HitTestPassThrough` on the band, `HitTestVisible = !compactCanHit` on the expanded presentation (`DetailVerticalHero.cs:70,425,435`). §5 mentioned `onStuck` only as a flag flip. Added, plus parity item 61. |
| 11 | 3 | **missing** | Hero attribution has **three** arms, not two — `CollaboratorFacePile` precedes the owner run and the artist spans (`DetailVerticalHero.cs:474-475`); the spans are `NoWrap` with a `", "` separator; all three agree with `HeroHasAttribution`. Row rewritten. |
| 12 | 1.2 | **missing** | `pl-collab:{(int)width}` is a **width-keyed remount** of a Component, in both the rail (`DetailRail.cs:560`) and the hero (`DetailVerticalHero.cs:475`) — the only geometry-keyed remount in the frame, and the one most likely to be dropped in a static rewrite. The keyed-remount list is now exhaustive. |
| 13 | 3 | **missing** | The two optional sticky strata were quoted as "+48 chips, +36 lens" with no derivation: `ContentFilterChips.VerticalExtent` = 40 + 8 (`:38,40`), `LikedLens.HeaderExtent` = 28 + 8 (`LikedFactsPanel.cs:1350-1355`), both constants on purpose. Added with sources. |
| 14 | 3 | **missing (an inconsistency)** | `StickyClipInset` sums a hard `ChromeHeaderHeight = 36`, but the real column header is `HeaderHeightFor(classic) = classic ? 32 : 36` (`DetailTrackTableRules.cs:27,49`). Under the Classic table skin the clip over-cuts by 4 DIP. Recorded as a token row, a §8 pure-rule entry and parity item 63. |
| 15 | 3 | **missing** | The band's search-field clamp was quoted as `[66, 280]` with no source; added `SearchIconWidth 66 / SearchPreferred 240 / SearchMax 280` with `DetailTrackCommandBarLayout.cs:41-46`, plus a §8 row — the frame reads these three even though the fit solver belongs to chapter 04. |
| 16 | 3 · 8 | **missing** | The scroll-spy constants (`SpyProbe` 8, `SpyViewportFraction` 0.25, `EndProbe` 8, `MaxItems` 16) and `ActiveSection`'s **−1 "no answer"** contract — load-bearing (D40) and absent from both the token table and the §8 row. |
| 17 | 5 | **missing** | Three motions were absent: `SearchSwapMotion` (icon↔field cross-fade, 260/180), `SearchUnderlineMotion` (focus underline, 260/180) and the column-header grid's Position tween on the check-column inset (`MotionTok.DisclosureExpand` / `FluentDecelerate`). Also added the pivot underline's 167 ms brush swap and the `Interactive(Interaction.Subtle)` ramp on the rail FAB / hero More — a different mechanism from the satellite's authored 83 ms. |
| 18 | 5 | **missing** | `ReDeal`'s seeds are windowed (`[firstVis, firstVis + ReDealRows)` off the live scroll offset) and are **cleared** on a skip so an unrelated `_dispVer` bump cannot replay them as a phantom fade (`DetailTracks.cs:1621-1626,1635-1642`). |
| 19 | 6 · 9 | **missing** | The mode's two fail-safes — the `Math.Max` pre-measure seed and the per-render `fit > mode` self-heal (`DetailShell.cs:495-502`). §8 and §9 name `EstimatePageWidthFromViewport` but never the two places its answer is applied. Added to §6, §9 and parity item 65. |
| 20 | 6 | **missing** | Every loc key the frame owns, with base-culture English — the chapter named `Strings.*` paths but never the keys or their text, so a 0.3 string table had nothing to diff against. |
| 21 | 1.1 | **missing** | Every conditional rail/header row's actual GATE (`rail:owner` needs a non-empty `OwnerName`; `rail:release` needs `TypeYear && HasReleasePanel`; `rail:likedfacts` is `Row`, not `LateRow`, because the panel owns its entrance; …). The tree listed the rows and their line numbers but not what turns them on. |
| 22 | 1.1 · 9 | **missing** | `showToolbar = Content == Tracks \|\| mode != Vertical` (`DetailShell.cs:518`) — a show in the vertical arm has no episode toolbar; and `SelectionMode = visible > 0 ? cfg.Selection : None` (`DetailTracks.cs:1499`). Both are per-state behaviour a rebuild silently loses. |
| 23 | header · 9 | **overclaim** | The source header lists `Design/HeroCta.cs` among the consulted files without noting it has **zero callers** repo-wide, and §8/§9 never mention `DetailRail.BilledArtists` (`:503-556`, ~54 lines, unreferenced). ~77 lines of dead code a faithful port would carry over. A "Dead code — do NOT port" block now sits beside the existing `CapTitle` / `TwoColumn:false` findings. |
| 24 | header | **missing** | A "Source-comment drift" block: three comments in the assigned files now contradict their own code (`DetailConfig.cs:16`, `:222-223`; `DetailShell.cs:195-196`). The chapter had a doc-drift block for the *plans* but none for the *sources* — and a re-author reads the sources. |
| 25 | 4 | **missing** | `TwoColumnHeroBandFraction = 0.55` (`DetailShell.cs:189,569-571`) — the synthetic hero band the two-column arm feeds the tone leaf — and `BackdropBandFor = max(112, heroBand)`. Dead with hero-only mode, but the Props still carry it and §4.5 described the veil without saying what `heroBand` is. |
| 26 | 3 | **missing** | The hero artwork's `TransformOriginX/Y = 0` (`DetailVerticalHero.cs:114-115`): the 280 ms `ScaleCorrect` bounds tween grows from the **top-left**, which is what keeps the cover's corner pinned to the title's top across a resize. Plus the rail eyebrow's explicit `Width = cover` vs `BuildHeader`'s deliberate omission. |
| 27 | 9 | **missing** | `DetailSkeleton` derives its geometry from `BucketW(colW)` but declares `MinHeight` from the raw `colW` (`:43` vs `:112`) — up to 4 DIP of disagreement between the shimmer drawn and the floor declared. Added as a trap. |
| 28 | 10 | **missing** | Ten parity items (57–66) covering the states the new findings introduce: show rail, show vertical-header saturation, chart-caption reservation, absent edge cues, band input handoff, facepile width remount, Classic header height, empty-list selection, first-frame mode, and the pre-mount rail Play fallback. |
| 29 | header · 9 · 10 | **critic-fix: missing (no owner, no wave slot)** | The frame had **no owner and no slot in plan §5**, and every chapter deferred to another: this header said "Wave 5 owners M and O (shared)", `04-detail-track-table.md:13` says "owners M … and O", `05-album.md:1198` says "whoever writes 03/04", `06-playlist.md:1215` records the gap a third time — so Wave 5 (`wavee-0.3-implementation.md:749-757`) opens with five pages whose common frame does not exist, and its own "DISJOINT files" rule (`:702`) is broken on day one. Verified real against 0.2.9: the 17 dedicated `Features/Detail/*` files total **5,754 lines** (+ ~620 frame-owned inside `DetailTracks.cs` = the header's ~6,400; the whole folder including the table is 10,279) and **one** scaffold serves every kind — `ContentHost.cs:170-172` routes album / playlist / liked / local / show / prerelease into `DetailPage` (`:288`, `:158-161`) → `DetailShell` (`DetailPage.cs:264`, `:284`), with `DetailKind {Album, Playlist, Liked, Show}` (`DetailConfig.cs:17`) flipping the knobs. It is not a two-owner file either: **three** of Wave 5's five owners consume it — M, O (incl. the `local` route, `15-library.md:1523`) and N, whose artist compact bar and page read `ContextBand.Row/Title/HairlineOverlay/Anchor/ClipInset/ClipFadeBand` directly (`ArtistCompactBar.cs:48,78,100`, `ArtistPage.cs:232,321,328,338`). **Fix applied:** the header and §9 now name a single owner (**M**), a dedicated **Wave 4.5** slot after Wave 4 closes (the frame needs L's `Design.cs`/`Controls.cs` vocabulary, so it cannot live *in* Wave 4, and it cannot live in L: `Design/Controls` is entity-free and the frame is written over entity handles), the four files that move into it (`Detail.cs`, `Detail.UI.cs`, `Track.Table.cs`, `Track.UI.cs`), and a pass/fail gate — the album from the fake seed rendering through the real frame in both arms. New §10 item **67** states that gate as a checklist entry, and §9's line-budget row and "files missing from §2" paragraph now say "no owner, no slot" rather than only "no file". |
| 30 | 9 | **critic-fix: cross-chapter** | The same decision must be mirrored in `04-detail-track-table.md:13` + its `Track.Table.cs` bullet, `05-album.md:1198` (#21) and `06-playlist.md:1215`, which currently point at each other. This pass edits chapter 03 only; §9 now carries the list so the next pass makes those three edits instead of re-deriving the question. |

**Not corrected, deliberately.** §0.8's "Pony → 88/117 at a 1000-DIP column" (re-derived exactly: art 240, budget 128, cap 86.81 → snap 88, lh 117). §2's W1/W2/W3 cover arithmetic (256 / 200 / 164, and P's 216). §2 W27's eyebrow-asymmetry paragraph, which is right down to the loc strings — "Playlist · Collaborative" / "Playlist · Private" / "Playlist" are single keys, not composed. §7's readiness table and DATA GAPS, which are 0.3 proposals this audit has no 0.2.9 evidence to contradict. §9's line budget, which is an estimate and labelled as one.

**Residual risk this pass could not close.** §7's 0.3 column/edge proposals and §9's line estimates are unverified by construction — no 0.3 code exists. `Splitter`, `SkeletonRegion`, `ScrollBindDsl` and the `MotionTok` rungs were read at their declarations only: the frame's *use* of them is verified, their engine behaviour is taken on the engine's word. The parity items requiring a live 0.2.9 build were not executed — this was a source audit, not a visual one, so every "looks like" claim in §§2 and 4 (how the 0.20 tone reads over Mica, whether the light-theme hue is "imperceptible") remains the author's, unverified.

**token-reconcile (2026-09-12):** §3's derived-strata row wrote the lens header as `PillHeight 28 + Spacing.S 8`. There are TWO `PillHeight` constants — `WaveeCta.PillHeight` = **36** (`WaveeCta.cs:65`) and `LikedLens`'s own private **28** (`LikedFactsPanel.cs:1355`) — and unqualified the row reads as the wrong one. Qualified in place; the arithmetic (= 36) was already right. Nothing else changed: `WaveeMotion.Standard` 250, `Faster` 83, `AccentRule` 20 × 2 + 2 (`Surfaces.cs:356`), `Radii.Control` 4 / `Card` 8, `PlayerDock.Reserve` 72, `WaveeSize.ArtThumb` 40 / `RailAlbum` 280 and the `DetailHero` 28/36 → 40/52 swap all re-verified. Index: `00-design-system.md §12.1`.

**consistency 2026-09-12:** header block's "0.3 target" line said NOT IN PLAN and proposed the files; A1 has since settled them (`Entities/Detail.cs` 900 + `Entities/Detail.UI.cs` 2,600, owner M, Wave 4.5) — header rewritten to cite the settlement, and the §9 "where the plan is wrong" bullets, its line-budget table row and its "files missing" paragraph reworded from present-tense gaps to past-tense history now that §2 carries the files.
