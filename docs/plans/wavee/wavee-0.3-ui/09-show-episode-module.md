# Show page, episode rows, module and watch pages - 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Detail/EpisodeList.cs` (260) · `src/apps/Wavee/Features/Modules/ModulePage.cs` (776) ·
> `src/apps/Wavee/Features/Modules/WatchPageView.cs` (402) · `src/apps/Wavee/App/WatchPageModel.cs` (292) ·
> show branches in `Features/Detail/DetailConfig.cs` (311), `Features/Detail/DetailPage.cs` (757), `Features/Detail/DetailShell.cs` (858),
> `Features/Detail/DetailRail.cs` (607), `Features/Detail/DetailTrailing.cs` (709) · `Actions/PlayableLinks.cs` (82) ·
> `Actions/LocalFileActions.cs` (115) · `src/apps/Wavee.Sdk/ModulePage.cs` (312) · `src/apps/Wavee/App/DockedVideoHosting.cs` (168)
> | 0.3 target: `Entities/Show.UI.cs`, `Entities/Show.Page.cs`, `Entities/Episode.UI.cs`, `Platform/Modules.UI.cs` (+ `Platform/Modules.cs` CORE)
> | Wave 5 owner M + Wave 6 owner T

After Wave 0 every 0.2.9 path below lives at `src/apps/_old/Wavee/<same relative path>` (and `src/apps/_old/Wavee.Core/...`,
`src/apps/_old/Wavee.Tests/...`). Line numbers are HEAD `b3f6647a`.

Shared parts specified elsewhere — do not re-derive them here:
`00-design-system.md` (tokens, ramp, cover palette, materials, motion curves, CTAs) ·
`01-track-row.md` (`TrackRow.ArtCard`, equalizer, menus) ·
`02-cards-and-controls.md` (`MediaCard.Shelf` / `MediaCard.VideoCard`, `PagedShelf`, `StatTile`, `SelectorBar`) ·
`03-detail-frame.md` (the hero/rail/skeleton/reveal frame the SHOW page sits inside) ·
`04-detail-track-table.md` (the track table the show page deliberately does **not** use) ·
`18-shell-frame.md` (masthead reserve, page transitions) · `20-player-bar.md` (the dock reserve) ·
`21-right-rail-npv-queue-stage.md` (the rail card the watch stage takes the video surface from) ·
`24-video-surfaces.md` (`DockedVideoSurface`, placement, letterbox, fullscreen).

---

## 0. The non-negotiables

1. **An episode is a CARD, not a table row.** Every episode is a bordered, 8-DIP-rounded box with its own 1 px
   `Tok.StrokeDividerDefault` hairline and 12-DIP padding — `EpisodeList.cs:202-232`. There is no `#` column, no zebra,
   no shared grid, no column header. A show page that renders `DetailTracks`-style rows has lost the surface.
2. **Two clamped lines of prose per episode.** Title 14/20 **Weight 700**, `MaxLines = 2`, wrap, character-ellipsis; the
   description directly under it at 12/16 `Tok.TextSecondary`, `MaxLines = 2`, wrap, character-ellipsis, gap 4
   (`EpisodeList.cs:219-224`). A one-line title or a dropped description reads as a podcast app with no editorial voice.
3. **The play affordance is always visible and always accent.** A 40×40 `Radii` 20 circle filled `Tok.AccentDefault`
   carrying a 15-DIP `Icons.Play` in `Tok.TextOnAccentPrimary`, with `Elevation.Card` and the emphatic hover/press tier
   (1.07 / 0.92) — `EpisodeList.cs:247-253`. It is **not** hover-revealed, unlike a track row's number cell.
4. **Resume progress is a 3-DIP rule, not a number.** `Height = 3`, corners 2, ground `Tok.FillSubtleTertiary`, fill
   `Tok.AccentDefault`, laid out as two flex siblings `Grow = max(0.001, pct)` / `Grow = max(0.001, 1-pct)`
   (`EpisodeList.cs:237-245`). It only exists when `pct > 0.01` (`EpisodeList.cs:230`).
5. **"Listen next" is the page's second hero.** One 72-DIP-art card on `Tok.FillCardSecondary` with a
   `Tok.StrokeCardDefault` hairline, an eyebrow, a 15/700 title, a "MMM d · N min" caption, the same progress rule, and a
   **Resume** media capsule pinned with `Shrink = 0` — `EpisodeList.cs:155-186, 257-259`. It is picked from *all*
   episodes (never the filtered view) as the most-progressed in-progress one (`EpisodeList.cs:78-81`).
6. **Four status pills and a Newest/Oldest pair, on opposite ends of one row.** `SelectorBar` (All · Unplayed ·
   In progress · Played) left, a `Grow = 1` spacer, `SelectorBar` (Newest · Oldest) right, gap 12, bottom margin 4
   (`EpisodeList.cs:137-153`). The pills carry the accent selection pill; nothing else on the page does.
7. **"Load more episodes" is a pill at the END of the list, never an infinite-scroll sentinel.** A
   `ButtonAppearance.Standard` `WaveeCta.Pill` that becomes the (inert) "Loading…" label while a page is in flight —
   `EpisodeList.cs:126-135`. The gate is the paging **cursor** (`PagedThrough < TotalEpisodes`), never
   resident-count-vs-total (`EpisodeList.cs:65-72`).
8. **The module page is drawn in the app's own detail vocabulary and never switches on a module id.** One hero
   (232-DIP art, `Ui.Title` name, eyebrow, link subtitle, meta line), stock Fluent action buttons, then typed sections —
   `ModulePage.cs:283-310`. An unknown section kind is skipped and the rest of the page still renders
   (`ModulePage.cs:482-484`).
9. **The module page's loading state is DERIVED from the page itself against a figure-space seed.** Three U+2007
   strings (12 / 8 / 5 figure spaces) give the shimmer a believable hero bar and two shorter lines with no string that
   could flash as real copy — `ModulePage.cs:65-72`, `Skel.Region(..., reveal: SkelReveal.Soft)` at `ModulePage.cs:147-156`.
   There is no hand-authored second tree.
10. **The watch stage is a full-width 16:9 black box pinned ABOVE the scroller, with zero motion of its own.** No
    `Enter`, no `Exit`, no `Layout`, no `Opacity`, no `Stagger`, outside `Skel.Region`, outside `Section()`, outside the
    `ScrollView` — `WatchPageView.cs:121-140` and the three-hazard class doc at `WatchPageView.cs:31-46`. Any ancestor
    opacity/blur/edge-fade silently erases the composited video's DestOut hole.
11. **The watch title drops one rung.** `WaveeType.NowPlayingTitle` (Subtitle 20/28/600), never `PageHero` —
    `WatchPageView.cs:187-190`. A 28-DIP display line over a 560-DIP picture reads as two competing identities.
12. **The idle stage's only affordance is one 64-DIP accent disc.** `WaveeCta.Icon(Icons.Play, …, Accent, size: 64)`
    wearing `Radii.Full`, centred, wrapped in a tooltip reading `detail.play` — `WatchPageView.cs:155-164`. Not a labelled
    capsule, not a hover-revealed control.
13. **Idle → live must not reflow.** The envelope (box, height, letterbox ground) is unconditional; only what paints
    inside changes, and the one visible transition is the element's own `PosterMotion` dissolving
    `DockedVideoSurface.PosterGround` on the first decoded frame — `WatchPageView.cs:48-52, 99-119`.
14. **Already playing states the fact, never a dead button.** The primary play capsule is replaced by the inert
    "Playing" badge (pill geometry, `Tok.FillSubtleSecondary`, `Tok.StrokeCardDefault` hairline, 12-DIP `Icons.Play` in
    `WaveeAccent.Decor`, `AutomationRole.Text`) — `WatchPageView.cs:268-271, 306-319`.
15. **Nothing is invented on a module page.** An action whose kind cannot be honoured is ABSENT, not disabled
    (`ModulePage.cs:408`, `WatchPageView.cs:275`); a name with no entity id is inert text, never a styled link
    (`WatchPageView.cs:226-239`, `ModulePage.cs:719-743`); a card navigates only where the module named a destination
    (`ModulePage.cs:593-602`).
16. **There are THREE templates, not two.** `entity` (a hero always, falling back to `PageHero(route.Arg ?? loc
    modulePage.title "Module", …)` when the document carries none), `custom` (sections-only — a hero ONLY when the
    module supplied one) and `watch` — `ModulePage.cs:286-293`. `custom` is the one reading with no guaranteed
    identity block at the top of the page; dropping it collapses two readings into one.
17. **The stage's video mount is gated on the RESOLVED placement, not on "is this playing".** `ShouldMount` returns
    false unless `VideoPlacementNow() == SurfacePlacement.Docked` (`DockedVideoHosting.cs:140`), so fullscreen, a
    popped-out window and the floating mini player all put the page stage back to poster + the 64-DIP disc — while the
    caption's play capsule still reads **Playing** (that one is a uri compare, placement-free, `ModulePage.cs:113`).
    Poster-while-playing is a legitimate, reachable state and must not be "fixed" into a black box.
18. **The item budget is ONE shared pool, spent in document order.** `BuildBody` opens a single
    `budget = ModulePageBudget.MaxItems` (500) and passes it `ref` through every block, so a `facts` section with 500
    rows starves every section after it, and a `playables` block takes `min(items.Length, budget)`
    (`ModulePage.cs:299-307, 508, 541, 587, 628`). The 40-section cap counts only sections that actually DREW
    (`drawn++` runs after a non-null block, `:304-306`), so twenty unknown kinds cost nothing.

---

## 1. Anatomy

### 1.1 — 0.2.9 composition: the SHOW page (two-column arm, mode 0)

```
DetailPage                                          Features/Detail/DetailPage.cs:22        route "show:<uri>" → DetailKind.Show (:79)
├─ UseResource(LoadAsync)                           DetailPage.cs:109-159                   GetShowAsync → MapShow (:385, :449-468)
│   └─ seed = preview ?? PendingSeed(Show)          DetailPage.cs:346-362                   8 blank Episode records, BadgeType " ", Publisher " "
├─ UseSignalEffect(live refresh)                    DetailPage.cs:171-259                   Show arm: c.IsBulk || c.Uri == showUri (:246)
└─ DetailShell(route, model, settings)              Features/Detail/DetailShell.cs           cfg = DetailConfig.Show (DetailPage.cs:335)
    ├─ tintBinder / DeferWatcher / tonePlane        DetailShell.cs:551-577, 677-688          CoverPaletteLeaves.PageTonePlane — see 03-detail-frame.md
    ├─ DetailNoticeBar.For(model, false)            DetailShell.cs:669                       a Show carries DetailNotice.None (DetailPage.cs:296-307)
    └─ row [ railFaded | grip | right ]             DetailShell.cs:639-656, 753-793          MaxWidth 1600, Justify Center
        ├─ railFaded  (Width = railW)               DetailShell.cs:779-789                   Opacity = Prop.Of(_railFade)
        │   └─ DetailRail.Build(m, cfg, …)          DetailRail.cs:122-298                    Width railW, Pad (16,24,8,24), Gap 14
        │       ├─ "rail:cover"                     DetailRail.cs:145-162                    cover=CoverEdge(railW)=railW-24, r8, Elevation.Card, Draggable
        │       ├─ "rail:eyebrow"  (TypeYear)       DetailRail.cs:167-171                    EyebrowText = m.BadgeType = loc podcast.show ("Podcast")
        │       ├─ "rail:title"                     DetailRail.cs:183-189                    DetailHero, Size titleSize, Width cover, MaxLines 3
        │       ├─ (rail:artists)   — NOT drawn     DetailRail.cs:193-194                    MapShow passes Artists = empty (DetailPage.cs:461)
        │       ├─ (rail:meta)      — NOT drawn     DetailRail.cs:199-200                    gate is `Badges != TypeYear`; Show is TypeYear ✗
        │       ├─ "rail:cta"                       DetailRail.cs:212-240                    PlayPill + [SaveButton(show) · ShareButton · OwnerMenu]
        │       ├─ (rail:release)   — NOT drawn     DetailRail.cs:244-245                    HasReleasePanel false (DetailTrailing.cs:200-201)
        │       ├─ "rail:desc"                      DetailRail.cs:249-253                    RichText.Of(show description, 12, width cover, descLines)
        │       └─ ScrollView(rail) on FillLayerDefault  DetailRail.cs:290-298
        └─ right  Key "right:eps"                   DetailShell.cs:525-531                   Grow 1, MinWidth = ContentMinWidthForMode(mode)
            └─ EpisodeList(model, handlers, showToolbar)  Features/Detail/EpisodeList.cs:20   Key "episodes:"+route, DeriveRenderedOutput
                └─ ScrollView(body) Grow 1          EpisodeList.cs:95-101                    body: Dir 1, Gap 12, Pad (16,12,16, 72+24)
                    ├─ Toolbar                      EpisodeList.cs:137-146
                    │   ├─ SelectorBar(status, _status)  EpisodeList.cs:142, 148-152          All / Unplayed / In progress / Played
                    │   ├─ spacer Grow 1            EpisodeList.cs:143
                    │   └─ SelectorBar(order, _order)    EpisodeList.cs:144, 153              Newest / Oldest
                    ├─ ResumeBanner (conditional)   EpisodeList.cs:78-81, 155-186
                    │   ├─ RailHeader "Listen next" EpisodeList.cs:160                        loc podcast.listenNext
                    │   └─ card                     EpisodeList.cs:161-184                    r8, FillCardSecondary, 1px StrokeCardDefault
                    │       ├─ art 72×72 r8         EpisodeList.cs:169-170                    Surfaces.Artwork(e.Image, seed, 72,72,8)
                    │       ├─ copy col Grow1 Gap4  EpisodeList.cs:171-181                    Eyebrow · 15/700 title · 12 caption · ProgressBar
                    │       └─ ResumePill Shrink 0  EpisodeList.cs:182, 257-259               WaveeCta.Accent("Resume", Tok.AccentDefault)
                    ├─ RailHeader "Episodes"        EpisodeList.cs:83                         loc podcast.episodes
                    ├─ EMPTY arm ── or ── rows      EpisodeList.cs:84-87
                    │   ├─ empty: Pad (16,20,16,20) EpisodeList.cs:85-86                      TextEl 14 TextTertiary, loc podcast.noEpisodes
                    │   └─ EpisodeRow × N           EpisodeList.cs:188-233                    ONE per filtered index, in view order
                    │       ├─ row A  Dir 0 Gap 16  EpisodeList.cs:210-228                    art 56 · text col · PlayCircle 40
                    │       ├─ row B  meta Gap 8    EpisodeList.cs:191-201, 229               "MMM d" · "N min" [· "In progress"]
                    │       └─ row C  ProgressBar   EpisodeList.cs:230                        or BoxEl{Height=0}
                    └─ LoadMore pill (conditional)  EpisodeList.cs:88-93, 127-135             gate: pagedThrough < TotalEpisodes
```

`DetailConfig.Show` in full (`DetailConfig.cs:224-228`), because half of what the page does NOT have is decided here:
`TwoColumn: true` · `RailWidth: WaveeSize.RailAlbum` (280) · `Badges: TypeYear` (which is what kills the rail meta row)
· `ShowArtThumb/ShowAlbumColumn: false` · `Columns: AlbumColumns` (inert — the right column is `Episodes`) ·
`CapTitle: false` · `Selection: None` · **`HasTrailing: false`** · `Heart: Follow` · `Content: Episodes` ·
`RailScope: Show` · and by omission `ShowPlays / ShowTrackArtist / Recommendations / ShowTempo / ShowVersions` all
false with `RailResizable` defaulted true. `HasTrailing: false` is the load-bearing one after `Content`: a show page
has **no trailing block at all** — no About/Fans/More-by, and it never takes the outer-scroll composition an album
page uses. There is no second scroller on this page.

### 1.2 — 0.2.9 composition: the MODULE page (entity template)

```
ContentHost                                         Features/Shell/ContentHost.cs:279-281   ModulePages.IsRoute → ModulePage, Key "module-page:"+route
└─ ModulePage(route)                                Features/Modules/ModulePage.cs:54       props frozen at mount; a different entity = a different slot (:57)
    ├─ TryParseRoute → moduleId, entityId           ModulePage.cs:88-91                     bad route ⇒ EmptyState.Build(loc modulePage.error)
    ├─ UseResource(host.PageAsync, seed, (uri,gen)) ModulePage.cs:100-105                   seed = ModulePages.Get(uri) ?? Seed (figure spaces)
    ├─ UseSignalEffect(claim ActiveStagePlayable)   ModulePage.cs:137-145                   gated on UseIsActive; reads inside the closure
    └─ root  Dir 1, Grow 1, Pad (0, 84, 0, 0)       ModulePage.cs:169-180                   84 = BrowseLayout.MastheadReserve
        ├─ stage slot = EMPTY BoxEl                 ModulePage.cs:175-177                   watch doc ⇒ WatchPageView.Stage; else new BoxEl()
        └─ ScrollView(content) ScrollKey "module-page:"+uri   ModulePage.cs:178
            └─ content  Dir 1, Pad (32,40,32,112)   ModulePage.cs:158-165                   112 = PlayerDock.Reserve(72) + 40
                └─ Skel.Region(page, content, Soft, onFailed, group)   ModulePage.cs:147-156
                    ├─ FAILED → FailedBody          ModulePage.cs:257-268                   ErrorState + optional "Open in browser"
                    └─ READY  → BuildBody           ModulePage.cs:283-310                   Dir 1, Gap 20, MinWidth 0
                        │   template gate           ModulePage.cs:286-293                   custom ⇒ hero ONLY if doc.Hero;
                        │                                                                   entity ⇒ doc.Hero ?? PageHero(route.Arg
                        │                                                                   ?? loc modulePage.title, …) — W18
                        ├─ Hero  Key "module-hero"  ModulePage.cs:313-357                   Dir 0, Gap 20, AlignItems Center, Wrap true
                        │   ├─ art 232×232 r8       ModulePage.cs:348-349                   decodePx 256 (the shared rung)
                        │   └─ column Grow1 Gap 8   ModulePage.cs:339-344                   Justify Center
                        │       ├─ "hero:eyebrow"   ModulePage.cs:316-317                   DetailRail.EyebrowRun
                        │       ├─ "hero:title" row ModulePage.cs:321-327                   PageHero + LiveBadge (:383-396)
                        │       ├─ "hero:subtitle"  ModulePage.cs:329-334, 706-744           ModuleMetaLink (Key carries text|route)
                        │       └─ "hero:meta"      ModulePage.cs:336-337                   TrackMeta, MaxLines 2, Wrap
                        ├─ ActionRow "module-actions"  ModulePage.cs:399-419                Enter FadeUp, Layout Shove, Gap 8, Wrap
                        │   └─ Button.Accent | Button.Standard  ModulePage.cs:408           STOCK Fluent rectangles, Key "action:"+Id+":"+Kind
                        └─ Section × ≤40            ModulePage.cs:298-307, 489-500           Key "sec:"+i+":"+kind, Enter FadeUp, Layout Shove, Gap 12
                            ├─ text     → RichText.ExpandableFlex(…, 6 lines)   ModulePage.cs:464-468
                            ├─ facts    → wrap-row of StatTile, Stagger 45      ModulePage.cs:505-528
                            ├─ playables→ RampedRows of TrackRow.ArtCard(Rail)  ModulePage.cs:535-578, 672-700
                            ├─ cards    → wrap-row of MediaCard.Shelf(168)      ModulePage.cs:582-621
                            └─ links    → 2-gap column of hyperlink rows        ModulePage.cs:624-664
```

### 1.3 — 0.2.9 composition: the WATCH page (same class, `TemplateWatch`)

```
ModulePage (identical route parse / fetch / skeleton / failed / retry)   ModulePage.cs:41-47 (class doc)
└─ root  Dir 1, Grow 1, Pad (0, 84, 0, 0)           ModulePage.cs:169-180
    ├─ WatchPageView.Stage(model, stagePlayable, bridge, ui, onPlay)   WatchPageView.cs:90-141
    │   └─ "module-stage"  AspectRatio 16/9, MaxHeight 560, Fill MediaLetterbox, Shrink 0, ClipToBounds
    │       └─ ZStack child (Grow 1, MinHeight 0)   WatchPageView.cs:133-138   ← hazard 2: AspectRatio never reaches a ZStack
    │           ├─ DockedVideoSurface.PosterGround(poster)  Features/Video/DockedVideoSurface.cs:423-426   Opacity 0.4 ArtworkFill
    │           ├─ Flow.Show(() => !Live(), PlayCta)        WatchPageView.cs:107, 155-164                  64-DIP accent disc + tooltip
    │           └─ Flow.Show(Live, DockedVideoSurface{Face=PageStage})  WatchPageView.cs:115-119           Key "module-stage-video"
    └─ ScrollView → content (32,40,32,112) → Skel.Region → WatchPageView.Caption(…)   ModulePage.cs:149-152
        └─ "watch-caption"  Dir 1, Gap 12, AlignSelf Stretch      WatchPageView.cs:201-205
            ├─ "watch:title"        NowPlayingTitle, MaxLines 2   WatchPageView.cs:187-190   ALWAYS (may be "")
            ├─ "watch:meta"         LiveBadge? + TrackMeta        WatchPageView.cs:210-221   if IsLive || MetaLine (:193)
            ├─ "watch:channel"      PersonPicture 40 + TrackTitle WatchPageView.cs:226-249   if ChannelName (:194)
            ├─ "watch:chips"        pills + PlayingChip + "…"     WatchPageView.cs:253-319   null when no chip survives (:296)
            ├─ "watch:description"  fact line bold + prose card   WatchPageView.cs:323-343   if FactLine || Description (:327)
            └─ "watch:shelf"        PagedShelf(VideoCard, ≤16)    WatchPageView.cs:349-387   if Shelf.Length > 0 (:353)
```

**Every row below the title is conditional** — a document with a title and nothing else is a legal watch page (W20), and
the title itself is `Trimmed(hero?.Title) ?? ""` (`WatchPageModel.cs:196`), so an empty first line is reachable too.
The caption box carries **no `Enter`, no `Layout`** (`WatchPageView.cs:201-205`): rows appearing or the description
expanding re-lay out with NO FLIP — unlike the entity layout's `Section()`, which has both.

The pure projection that decides **which** section becomes what:

```
WatchPageModel.From(doc, isPlayingEntity)           App/WatchPageModel.cs:128-209
├─ gate: doc.Template == TemplateWatch              WatchPageModel.cs:131          else null ⇒ the entity layout draws
├─ channel: hero.Subtitle/AvatarUrl/SubtitleEntityId, else the legacy one-card shelf   WatchPageModel.cs:136-156, 217-229
├─ FactLine  = first `facts` section's VALUES joined " · "   WatchPageModel.cs:169-171, 233-246
├─ Description = first `text` section's Text        WatchPageModel.cs:173-174
├─ Shelf     = first `playables`, else first `cards` that is not the channel card   WatchPageModel.cs:176-191
├─ ShelfTitle= Trimmed(shelfSection.Title) — NULL ⇒ the shelf draws with NO header   WatchPageModel.cs:205
├─ Chips     = every action with a label + a kind, in document order   WatchPageModel.cs:256-268
└─ Stage     = isPlayingEntity ? Live : Poster      WatchPageModel.cs:208
```

Three projection rules that change what is DRAWN and were not written down:

- **No name ⇒ no identity at all.** If `channelName` resolves to null the projection also clears the avatar, the
  entity id **and** `channelCard` (`WatchPageModel.cs:151-156`) — which RELEASES the one-card section back to the
  shelf. So "an avatar circle beside nothing" is structurally impossible, and a nameless channel card reappears as a
  shelf cell rather than vanishing.
- **The legacy channel card is exactly ONE item** carrying BOTH an entity id and a title (`WatchPageModel.cs:217-229`);
  the first cell of a five-card related shelf can never be promoted to the author row.
- **`ShelfTitle` null ⇒ `header: null`** is passed to `PagedShelf.Create` (`WatchPageView.cs:382`), so a headerless
  16:9 strip with no chevron row above it is a legal watch page — W13's "Up next" line is conditional.

The stage's surface also carries `OwnerStagePlayable = stagePlayable` (`WatchPageView.cs:118`): that is the
parked-twin discriminator inside `ShouldMount` (`DockedVideoHosting.cs:141-145`), not decoration.

### 1.4 — the 0.3 tree

Component props freeze at mount; the "reaches the child by" column is the ONLY supported way a value changes after mount.

| 0.3 node | file | kind | inputs | reaches the child by |
|---|---|---|---|---|
| `Show.Page` | `Entities/Show.Page.cs` | `sealed partial class Page : Component` nested in `Show` | `Show` handle (frozen) | `UseSignal(Entities.Current.Shows.Changed)` + `Edges.ShowEpisodes.Version[slot]` |
| `Show.Hero(Show)` | `Entities/Show.UI.cs` | `static Element` | handle | re-rendered by the page's version read; cover/title/eyebrow read live off the slab |
| `Show.RailRows(Show, …)` | `Entities/Show.UI.cs` | `static Element[]` | handle + `DetailFrame` config | as above — it is the shared frame's rail-row contributor (`03-detail-frame.md`) |
| `Show.EpisodeList` | `Entities/Show.Page.cs` | `sealed class : Component` | `Show` handle (frozen), `Signal<int> status`, `Signal<int> order` | **Signals** for the two toolbar selectors; the episode SET reaches it through the edge version |
| `Show.EpisodeToolbar(Signal<int>, Signal<int>)` | `Entities/Show.UI.cs` | `static Element` | the two signals | `Signal` (a `SelectorBar` binds it two-way) |
| `Show.ResumeBanner(Episode, Action)` | `Entities/Show.UI.cs` | `static Element` | `Episode` handle + play verb | re-created per render of the list (a value, not a component) |
| `Show.LoadMorePill(bool paging, Action)` | `Entities/Show.UI.cs` | `static Element` | `bool`, `Action` | re-created per render; `paging` comes from the list's own `Signal<bool>` |
| `Episode.Row(Episode, Action play)` | `Entities/Episode.UI.cs` | `static Element` | handle + verb | re-created per render; a bound-list variant takes `BoundItemScope<Episode>` (see §9) |
| `Episode.ProgressRule(float pct)` | `Entities/Episode.UI.cs` | `static Element` | float | value |
| `Episode.PlayCircle(Action)` | `Entities/Episode.UI.cs` | `static Element` | verb | value |
| `Modules.PageDoc` (the pure projection) | `Platform/Modules.cs` **CORE** | `static` + `WatchPageModel` record | `ModulePageDoc?`, `bool isPlayingEntity` | pure — no engine type, so it compiles into `Wavee.Tests` |
| `Modules.Page` | `Platform/Modules.UI.cs` | `sealed class : Component` | `Route` (frozen — ContentHost keys by route) | `UseResource(...).Loadable` — a `Loadable<ModulePageDoc>` signal, NOT a table |
| `Modules.WatchStage(model, stagePlayable, …)` | `Platform/Modules.UI.cs` | `static Element` | values + `ShellUi` | `Flow.Show(Func<bool>)` — **never** a captured bool (see §9) |
| `Modules.WatchCaption(model, …)` | `Platform/Modules.UI.cs` | `static Element` | values | value |
| `Modules.EntityBody(doc, …)` | `Platform/Modules.UI.cs` | `static Element` | values | value |

**Remounts (`Key`) that must survive the port**

| Key | 0.2.9 | why |
|---|---|---|
| `"episodes:" + route.Name` | `DetailShell.cs:530` | an album↔show swap in the reused detail slot must remount the right column, never reconcile a track table against an episode list |
| `"module-page:" + route.Name` | `ContentHost.cs:281` | one keep-alive slot per module entity |
| `"module-page:" + pageUri` (`ScrollKey`, `group`) | `ModulePage.cs:156, 178` | scroll offset + skeleton group per entity |
| `"module-stage-video"` | `WatchPageView.cs:119` | the one video surface's node identity |
| `"hero:subtitle:" + subtitle + "|" + route` | `ModulePage.cs:333` | `ModuleMetaLink` freezes text+route at mount |
| `"sec:" + index + ":" + kind`, `":row:"/":card:"/":link:"/":tiles"/":rows"/":cards"/":links"` | `ModulePage.cs:461, 564, 577, 611, 637, 523, 618, 662` | section identity survives a document refresh; a kind change remounts |
| `"chip:" + id + ":" + kind`, `"chip:playing"`, `"chip:more"` | `WatchPageView.cs:270, 281, 293` | a chip that changes kind is a different control |
| `"shim:" + url + ":" + w + "x" + h` | `Surfaces.cs:230` | a rebound cover at a new decode bucket remounts the shimmer |
| `"rich-expand-flex:" + contextKey + ":" + html + ":" + maxLines` | `RichText.cs:56-59` | the expandable paragraph freezes its TEXT and its clamp at mount — a `text` section whose copy changes, or the watch description (`contextKey` = `"watch-desc"`), must remount or it keeps the old prose |
| `"fact:" + key` | `StatTile.cs:47` | one tile per fact row; the VALUE cross-fade keys off `"v:" + value` inside it |
| `keyOf(item) = EntityId ?? PlayableId ?? index` | `WatchPageView.cs:383` | the shelf's cell identity across a document refresh |
| `"save:" + (PreReleaseUri ?? ContextUri)` | `DetailRail.cs:233` | `SaveButton` freezes its uri at mount — a show↔album swap in the reused rail must remount the heart |

---

## 2. Wireframes

≈ 8 DIP per monospace character. Window widths are the OS window; the page's own content width is
`window − ShellResponsiveLayout.NavPaneNarrowW (240)` as the pre-measure estimate
(`DetailLayoutBreakpoints.cs:ShellChromeAllowanceDip`, `EstimatePageWidthFromViewport`).

### W1 — Show page, two-column mode 0, fully loaded @ 1280 (page ≈ 1040)

```
◀ page content 1040 ─────────────────────────────────────────────────────────────────────────────────────────────▶
┌ rail 280 ────────────────────┬╥┬ right column ≈ 754 (MinWidth 300) ────────────────────────────────────────────┐
│ pad L16 T24 R8 B24 · gap 14  │║│ ScrollView · pad L16 T12 R16 B96 · gap 12                                     │
│ ┌──────────────────────────┐ │║│ ┌─ Toolbar · Dir 0 · gap 12 · margin-bottom 4 ──────────────────────────────┐ │
│ │                          │ │║│ │ [ All │Unplayed│In progress│Played ]          ← spacer →  [ Newest│Oldest ]│ │
│ │      cover 256×256       │ │║│ └───────────────────────────────────────────────────────────────────────────┘ │
│ │      r8 · Elevation.Card │ │║│  Listen next                          ← RailHeader · Ui.Subtitle 20/28/600     │
│ │      saturation 1.18     │ │║│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ │                          │ │║│ │ ┌────────┐  CONTINUE LISTENING                                 ┌────────┐ │ │
│ └──────────────────────────┘ │║│ │ │ 72×72  │  Episode title, one line, ellipsised         15/700 │ ▶ Resume│ │ │
│ Podcast            ← eyebrow │║│ │ │  r8    │  Mar 14 · 47 min                              12/Sec └────────┘ │ │
│                              │║│ │ └────────┘  ▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▃▃▃▃▃  3-DIP accent rule    r999 · h36      │ │
│ The Show Name                │║│ └─ pad L12 T12 R16 B12 · gap 16 · r8 · FillCardSecondary · 1px StrokeCard ───┘ │
│ Wraps to at most 3 lines     │║│  Episodes                             ← RailHeader · Ui.Subtitle 20/28/600     │
│ 28/36 (40/52 if winH ≥ 900)  │║│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ display face · tracking −20  │║│ │ ┌──────┐ #128 · The Build Trap                               ⎧ 40 ⎫       │ │
│                              │║│ │ │56×56 │ In this episode: notes, tangents and a few hard-won ⎪ ▶  ⎪ accent│ │
│ ┏━━━━━━━━━┓ ⬡  ⬡  ⬡          │║│ │ │ r8   │ lessons.                                 12/16 Sec ⎩ r20⎭       │ │
│ ┃ ▶ Play  ┃ 40 40 40         │║│ │ └──────┘                                                                  │ │
│ ┗━━━━━━━━━┛ ♥  ⤴  ⋯          │║│ │ Mar 14 · 47 min                                        12 · TextTertiary  │ │
│  h36 r999   (OwnerMenu is    │║│ └─ pad 12 · gap 8 · r8 · 1px StrokeDividerDefault ────────────────────────────┘│
│  an empty box for a show)    │║│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│                              │║│ │ ┌──────┐ #127 · Latency, Honestly                          ⎧ 40 ⎫       │ │
│ The show blurb, rich text,   │║│ │ │56×56 │ Two lines of description, clamped and ellipsised … ⎪ ▶  ⎪       │ │
│ clamped to 6 lines (3 when   │║│ │ └──────┘                                                    ⎩    ⎭       │ │
│ winH < 760). 12 · Secondary. │║│ │ Mar 7 · 33 min · In progress          12/600 AccentTextPrimary            │ │
│                              │║│ │ ▂▂▂▂▂▂▂▂▂▂▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃  h3 r2 FillSubtleTertiary       │ │
│ (rail scrolls on             │║│ └───────────────────────────────────────────────────────────────────────────┘│
│  FillLayerDefault)           │║│                                    ⋮  gap 12 between rows                     │
│                              │║│                     ┌──────────────────────┐                                  │
│                              │║│                     │  Load more episodes  │  Standard pill, row margin-L 8   │
└──────────────────────────────┴╨┴─────────────────────└──────────────────────┘──────────────────────────────────┘
   railW 280 (persisted 180–480)  grip Splitter.StripW        ▲ bottom pad 96 = PlayerDock.Reserve 72 + 24
```

Row height arithmetic (`EpisodeList.cs:202-232`): `12 + max(56, textCol) + 8 + 16 + 8 + (3|0) + 12`.
One-line title + one-line description → `textCol = 20+4+16 = 40` → row **112** (unplayed) / **115** (with progress).
Two-line title + two-line description → `textCol = 40+4+32 = 76` → row **132** / **135**.

### W2 — Show page, mode 1 @ 1000 (page ≈ 760, rail 224)

```
┌ rail 224 ──────────────┬╥┬ right ≈ 530 ───────────────────────────────────────────────┐
│ cover 200×200          │║│ [All│Unplayed│In progress│Played]      [Newest│Oldest]      │
│ Podcast                │║│ Listen next                                                 │
│ Title 28/36 (≤3 lines) │║│ ┌ 72 art · copy · [▶ Resume] ─────────────────────────────┐ │
│ [▶ Play] ♥ ⤴           │║│ └─────────────────────────────────────────────────────────┘ │
│ description ≤6 lines   │║│ Episodes                                                    │
│                        │║│ ┌ 56 art · title/desc (narrower ⇒ wraps sooner) · ▶ 40 ───┐ │
└────────────────────────┴╨┴─└─────────────────────────────────────────────────────────┘─┘
   RailW(1) = 224 (DetailShell.cs:192); the grip is GONE — DetailRailPolicy.ResizableFor is mode 0 only
```

### W3 — Show page, mode 2 @ 860 (page ≈ 620, rail 188)

```
┌ rail 188 ─────────┬ right ≈ 432 (MinWidth 300) ───────────────────────────────┐
│ cover 164×164     │ [All│Unplayed│In prog…│Played]        [Newest│Oldest]      │
│ Podcast           │ Listen next                                               │
│ Title ≤3 lines    │ ┌ 72 · copy (tight) · [Resume] ──────────────────────────┐│
│ [▶ Play] ♥ ⤴      │ └────────────────────────────────────────────────────────┘│
│ description       │ Episodes                                                  │
└───────────────────┴───────────────────────────────────────────────────────────┘
   CoverEdge(188) = max(80, 188−24) = 164 (DetailRail.cs:96)
```

Breakpoints and hysteresis — `DetailLayoutBreakpoints.cs`:
`NominalModeFor(w)`: ≥820 → 0, ≥660 → 1, ≥560 → 2, else Vertical(3).
Widening crossings at 820/660 use `ModeHysteresisDip = 24`; Vertical is entered below **540** and left at/above **580**
(`VerticalEnterW` / `VerticalExitW`). A nominal Vertical reached while already two-column clamps to mode 2.
`ContentMinWidthForMode`: Vertical → 0, else `TwoColumnContentMinW = 300`.

### W4 — Show page, Vertical (mode 3) @ 700 (page ≈ 460)

```
┌ page 460 ─────────────────────────────────────────────────────┐
│ DetailNoticeBar (absent for a show)                           │
│ ┌ DetailRail.BuildHeader · pad (16,16,16,8) · gap 12 ────────┐│
│ │ ┌────────┐  Podcast                       ← hdr:eyebrow    ││
│ │ │140×140 │  The Show Name                 28/36/600, ≤3 ln ││
│ │ │  r8    │   ← PageHero (Ui.Title, SYSTEM face) — NOT the ││
│ │ └────────┘      rail's display-face DetailHero  gap 16, ctr ││
│ │ [▶ Play]  ♥  ⤴                             hdr:play, wrap  ││
│ └────────────────────────────────────────────────────────────┘│
│ ┌ EpisodeList — showToolbar = FALSE (DetailShell.cs:518) ────┐│
│ │  (no status/order selectors at all in Vertical)            ││
│ │  Listen next                                               ││
│ │  ┌ resume card ──────────────────────────────────────────┐ ││
│ │  └───────────────────────────────────────────────────────┘ ││
│ │  Episodes                                                  ││
│ │  ┌ episode card, art 56, 2-line title, 2-line desc ──────┐ ││
│ │  └───────────────────────────────────────────────────────┘ ││
│ └────────────────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────────────┘
```

The vertical header's title is `WaveeType.PageHero` (`Ui.Title` in the SYSTEM face, `DetailRail.cs:396-398`), while the
two-column rail's is `WaveeType.DetailHero` (`Ui.Title` metrics in *Segoe UI Variable Display* at tracking −20,
`DetailRail.cs:183-189`). Same 28/36/600 numbers, **two different faces** — do not collapse them into one call.
The header's info column gaps at `Spacing.XS` (4), its cover row at 16, and the header box itself is
`Gap 12 · Padding (16,16,16,8) · Shrink 0` (`DetailRail.cs:434-441`).

`showToolbar = cfg.Content == Tracks || mode != Vertical` — `DetailShell.cs:518`. A podcast's filter/sort row is the ONE
control set that simply disappears in the narrow arm; there is no overflow home for it in 0.2.9. (See §9 — re-author
decision point, not a licence to invent one.)

### W5 — Show page, rail COLLAPSED (mode 0 only)

```
┌ 96 ──┬╥┬ right ────────────────────────────────────────┐
│ pad 8│║│  (unchanged episode column)                   │
│ ┌───┐│║│                                               │
│ │80 ││║│   BuildCompact(m, 96, expand) DetailRail.cs:304
│ │r8 ││║│   cover = max(48, 96−16) = 80                 │
│ └───┘│║│   title 12/600 ≤2 lines, width 80             │
│ Title│║│   spacer Grow 1                               │
│ …    │║│   ⟩ expand hit 80×28 r4, HoverFill Subtle²    │
│      │║│   ToolTip.Wrap(strip, m.Title)                │
│  ⟩   │║│   grip strip widens to 20 while collapsed     │
└──────┴╨┴───────────────────────────────────────────────┘
Strip: Dir 1 · Width 96 · Shrink 0 · gap 8 · **Padding (8, 16, 8, 16)** — side 8, top/bottom 16, NOT 8 all round ·
Fill `Tok.FillLayerDefault` · ClipToBounds (DetailRail.cs:340-352). Children, in order: cover · title ·
`compact:spacer` `Grow = 1` · chevron — the spacer pins the chevron to the FOOT of the strip, not under the title.
The collapsed row is `[compact, grip, right]` and `right` KEEPS its key across the detent, so the episode column
reconciles in place and never resets its scroll (DetailShell.cs:757-770).
Collapse detent: SnapThreshold = MinWidthFor(Show) = 180; ForcePush 44 (raw ≈136) collapses; ReExpand 220 re-opens.
(DetailShell.cs:797-817, DetailRailPolicy.cs:24-28)
BOTH the cover and the chevron carry `OnClick = expand` (DetailRail.cs:311-317, 330-338) — the whole strip is the
gesture. The chevron is `Icons.ChevronRight` at 14 in `Tok.TextSecondary`; the compact cover keeps `Elevation.Card`
and saturation 1.18, i.e. the same decode as the full rail (the texture stays warm across the detent).
```

### W6 — Show page, COLD OPEN skeleton (deep link, no nav preview)

```
┌ rail 280 ────────────────────┬╥┬ right ────────────────────────────────────────────────┐
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │║│ ▓▓▓ ▓▓▓▓▓▓▓▓ ▓▓▓▓▓▓▓▓▓▓▓ ▓▓▓▓▓▓   ▓▓▓▓▓▓ ▓▓▓▓▓▓       │
│ ▓  cover shimmer 256×256   ▓ │║│   ▲ the selector labels are REAL loc strings ⇒ real   │
│ ▓  (static tile: Cover null)▓ │║│     bar widths; nothing else on this page is         │
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │║│ ▓▓▓▓▓▓▓▓  ← "Episodes" RailHeader (a real string)      │
│ ▪  ← eyebrow bar = ONE SPACE │║│ ┌─────────────────────────────────────────────────────┐│
│    (BadgeType " ")           │║│ │ ▓▓▓▓▓                                      ▓▓▓▓   ││
│  (NO title bar — Title "")   │║│ │ ▓▓▓▓▓   ← NO title bar, NO description bar         ││
│  (NO description row at all) │║│ │ ▓▓▓▓▓                                             ││
│ ▓▓▓▓▓▓▓▓▓▓ ▓▓ ▓▓ ▓▓          │║│ │ ▓▓▓ ▪ ▓▓▓▓  ← "Jan 1 · 3 min", a REAL derived line ││
│  ▲ rail:cta (Play + 3 FABs)  │║│ └─────────────────────────────────────────────────────┘│
│                              │║│   × 8 (PendingSeed's eight blank Episode records)      │
└──────────────────────────────┴╨┴───────────────────────────────────────────────────────┘
Skel.Region(model, onFailed: ErrorState, reveal: SkelReveal.FadeOnly, smoothResize: false)   DetailPage.cs:273-289
Seed: DetailPage.PendingSeed(DetailKind.Show) — 8 × `Episode(id, uri, Title: "", ShowName: "", Image: null,
180_000ms, UnixEpoch)`, so **Description takes the record default `null`** (`Wavee.Core/Domain/Models.cs:528-531`) —
plus BadgeType " ", MetaLine " ", Publisher " ", ContextUri "pending:show", over `DetailModel.Empty`, whose
**Title is `""`, Description `null` and Cover `null`**.   DetailPage.cs:346-362
Shimmer bar geometry is the engine's `SkeletonStyle.Default`: bar fill `Tok.FillSubtleSecondary`, BarRadius **4**,
RowGap **8**, a text bar measuring **0.72 ×** its run's width, pulse 1000 ms down to 0.5 α (SkeletonRegion.cs:34-38).
```

**The seed is emptier than it looks — the one "port the pixels, not the hole" call on this surface.** A DERIVED
shimmer can only draw a bar for a run that MEASURES, and every identity string in this seed is empty or a space:

| slot | what 0.2.9's cold skeleton actually paints | why |
|---|---|---|
| `rail:cover` | one 256 shimmer block | `Cover` null ⇒ the STATIC placeholder tile (not `CoverShimmer`), which still derives a block — `Surfaces.cs:203, 211` |
| `rail:eyebrow` | a bar ~one space wide | the gate is `Length > 0` and `BadgeType` is a single space — `DetailRail.cs:169-170` |
| `rail:title` | **nothing** | the row is mounted unconditionally, but `DetailHero("")` measures zero — `DetailRail.cs:183-189` |
| `rail:artists` / `rail:meta` / `rail:release` | absent | their gates already fail for a Show (§1.1) |
| `rail:desc` | **never mounted** | gate `m.Description is { Length: > 0 }` — `DetailRail.cs:249-250` |
| `rail:cta` | the Play pill + three FAB squares | real geometry, no text dependency — `DetailRail.cs:216-239` |
| toolbar | six REAL-width bars | the `SelectorBar` items are real loc strings (All · Unplayed · In progress · Played / Newest · Oldest) |
| "Episodes" header | one real-width bar | `Loc.Get(podcast.episodes)` — `EpisodeList.cs:83` |
| episode title / description | **nothing** | `Title ""` and `Description ?? ""` both measure zero — `EpisodeList.cs:222-223` |
| episode meta | `Jan 1` · `3 min` — three real bars | `UnixEpoch.ToString("MMM d")` and `180_000/60000` are strings the seed genuinely produces |
| episode art / play disc | a 56 block and a 40 block | fixed geometry — `EpisodeList.cs:215, 247` |

So the 0.2.9 cold show skeleton is a column of art squares and discs with **no copy bars on the cards and no title
bar in the rail** — visibly poorer than the module page's figure-space seed two sections down. **0.3 must seed
U+2007 FIGURE SPACE runs** for the show title, the show description and each seeded episode's title + description
(the `ModulePage.Seed` pattern, `ModulePage.cs:65-72`) instead of porting the empty strings verbatim. See §9.4.

What the seed does NOT produce, and must not be invented: **no "Listen next" banner** (every seeded ProgressMs is 0),
**no "In progress" cell, no progress rule**, and **no "Load more" pill** (seed TotalEpisodes 0 ⇒ `hasMore` false).

With a nav preview (a card click) there is NO skeleton region at all: `DetailShell` mounts straight away and the
episodes fill in through the shared loadable — `DetailPage.cs:263-264`.

### W7 — Episode row, every state (zoom, 1:1)

```
REST (unplayed)                                                 ┌ 1px Tok.StrokeDividerDefault, r8, fill: none ┐
┌──────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ 12 ┌──────┐ 16 The episode title, up to two lines, character-ellipsised          16 ⎧ 40 ⎫ 12          │
│    │ 56   │    ──gap 4──                                                            ⎪ ▶  ⎪            │
│    │ r8   │    Description, up to two lines, 12/16, Tok.TextSecondary               ⎩ r20⎭            │
│    └──────┘                                                                                           │
│ ──gap 8──  Mar 14 · 47 min                          12/16 Tok.TextTertiary, dot gap 8                │
│ ──gap 8──  (BoxEl Height 0 — the progress slot is present but zero-height)                            │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘

HOVER  — the whole card takes Tok.FillSubtleSecondary; nothing else changes (no scale, no reveal).
         The 40-disc has its OWN hover: WaveeMotion.ScaleEmphatic 1.07 / press 0.92.     EpisodeList.cs:206, 251

IN PROGRESS (0.01 < pct < 0.98)
│ ──gap 8── Mar 7 · 33 min · In progress          ← the 4th/5th meta cells: Dot() + 12/600 AccentTextPrimary │
│ ──gap 8── ▂▂▂▂▂▂▂▂▂▂▂▂▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  h3 r2, ground FillSubtleTertiary, fill AccentDefault       │

PLAYED (pct ≥ 0.98)
│ ──gap 8── Feb 28 · 51 min                       ← NO "In progress" chip                                   │
│ ──gap 8── ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  the bar is still drawn (pct > 0.01) and is ~full           │

FOCUSED — the card itself is NOT Focusable; only the 40-disc is: `TabStop` is null = auto and "clickable nodes are
         focusable" (Element.cs:291-294), so the bare OnClick box IS a tab stop — with no Role and no name (§9.4).
NOW-PLAYING — 0.2.9 draws NO now-playing treatment on an episode row (no equalizer, no accent title). See §9.
SELECTED — unreachable: `DetailConfig.Show` sets `Selection = ItemsSelectionMode.None` (DetailConfig.cs:227), so a
         show page has no selection model, no marquee, no batch bar and no checkbox column at any width.
NO DESCRIPTION — the second run is authored unconditionally as `new TextEl(e.Description ?? "")`
         (EpisodeList.cs:223): an episode with no description still mounts an empty text run inside the gap-4 column.
         0.3 must DECIDE (drop the run, or keep the slot); reproducing it by accident is how a row grows a dead line.
NO ART — `Surfaces.Artwork(null, seed, 56, 56, 8)` is the static placeholder tile (56 < ShimmerMinEdge 80), tinted
         toward the cover's graded colour at TintStrength 0.55 when `CoverColorPlane` has one (Surfaces.cs:60-115).
LONG TITLE — 2 lines then character-ellipsis; the text column is `Grow 1, Basis 0` so the 56 art and the 40 disc never
         give (EpisodeList.cs:215-226). The row grows; nothing overflows.
```

### W8 — "Listen next" banner (zoom, 1:1)

```
Listen next                                                        Ui.Subtitle 20/28/600, Tok.TextPrimary
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ 12 ┌────────┐ 16  CONTINUE LISTENING                       Caption 12/16/600, tracking 30,       │
│    │ 72×72  │     ──gap 4──                                Tok.TextTertiary (loc podcast.        │
│    │  r8    │     Episode title, ONE line, ellipsised      continueListening — literal caps)     │
│    │        │     15 / Weight 700 / Tok.TextPrimary                              ┌────────────┐  │
│    └────────┘     Mar 14 · 47 min       12 / Tok.TextSecondary                   │  ▶ Resume  │  │
│                   ▂▂▂▂▂▂▂▂▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓                  └────────────┘  │
│                                                                            16     h36 r999       │
└─ Dir 0 · AlignItems Center · gap 16 · pad L12 T12 R16 B12 · r8 · Tok.FillCardSecondary ─────────┘
   · BorderWidth 1 · Tok.StrokeCardDefault · ClipToBounds true                    Shrink 0 ▲
```

### W9 — Empty filter + load-more

```
Episodes
┌ pad L16 T20 R16 B20 ────────────────────────────────┐
│ No episodes match this filter        14, TextTertiary│      loc podcast.noEpisodes
└─────────────────────────────────────────────────────┘

                    ┌────────────────────────┐
        margin-L 8 →│   Load more episodes   │   WaveeCta.Pill(Standard), h36, r999, pad 18/6/18/7, bold
                    └────────────────────────┘   while paging: label = loc podcast.loadingMore ("Loading…"),
                                                 onClick = a no-op lambda (NOT a disabled button)

The SAME arm draws for a show with ZERO episodes: the gate is `view.Count == 0`, not "the filter removed them"
(EpisodeList.cs:84-86), so an empty podcast reads "No episodes match this filter" under an "Episodes" header with no
filter applied. That is a defect to fix in 0.3 (§9.4), not a state to port — the empty-show copy needs its own key.
The toolbar, the "Episodes" header and (when one exists) the resume banner all stay mounted above this box.
```

### W10 — Module page, `entity` template, loaded @ 1280 (page ≈ 1040)

```
   ▲ 84 DIP masthead reserve (root Padding.Top = BrowseLayout.MastheadReserve = Spacing.XXXL 32 + TitleLine 52)
┌ ScrollView · content pad L32 T40 R32 B112 ──────────────────────────────────────────────────────────┐
│ ┌ "module-hero" · Dir 0 · gap 20 · AlignItems Center · Wrap true ─────────────────────────────────┐ │
│ │ ┌──────────────────┐                                                                            │ │
│ │ │                  │  Channel                          eyebrow 12/16/600 tr30 TextTertiary      │ │
│ │ │   232 × 232      │  Entity title           ▭LIVE▭    Ui.Title 28/36/600  +  18-tall badge      │ │
│ │ │   r8 · dec 256   │  Owner name (link)                14/20 Secondary → Primary+underline hover │ │
│ │ │                  │  1.2M views · 2 days ago          Caption 12/16 secondary, ≤2 lines, wrap   │ │
│ │ └──────────────────┘  column: Grow 1 · Basis 0 · gap 8 · Justify Center                          │ │
│ └─────────────────────────────────────────────────────────────────────────────────────────────────┘ │
│  gap 20                                                                                             │
│ ┌ "module-actions" · Dir 0 · gap 8 · Wrap ─────────────────────────────────────────────────────────┐│
│ │ ▛▀▀▀▀▀▀▀▀▜ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜    Button.Accent (Primary) / Button.Standard — STOCK Fluent rectangles││
│ │ ▙  Play  ▟ ▙ Open in browser▟   (NOT WaveeCta pills: r4, MinHeight 32)                           ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│  gap 20                                                                                             │
│ ┌ sec:0:facts — Section shell: RailHeader + body, gap 12, Enter FadeUp, Layout Shove ──────────────┐│
│ │ About                                                                                            ││
│ │ ┌─────────┐ ┌─────────┐ ┌─────────┐   StatTile: Grow 1 Basis 0, pad (12,8,12,8), r4,             ││
│ │ │1.2M     │ │2 days   │ │12:04    │   Fill FillCardSecondary, 1px StrokeCardDefault,             ││
│ │ │Views    │ │Published│ │Length   │   value 18/800 TextPrimary, caption 11 TextSecondary         ││
│ │ └─────────┘ └─────────┘ └─────────┘   row: gap 8, Wrap true, Stagger 45 ms                       ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:1:text ──────────────────────────────────────────────────────────────────────────────────────┐│
│ │ Description                                                                                      ││
│ │ Prose at 14, Tok.TextSecondary, links Tok.AccentTextPrimary, clamped to 6 lines with an inline   ││
│ │ "… More" suffix on the last line (RichText.ExpandableFlex).                                      ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:2:playables ─────────────────────────────────────────────────────────────────────────────────┐│
│ │ Up next                                                                                          ││
│ │ ┌ TrackRow.ArtCard(Rail) in a r4 box, HoverFill FillSubtleSecondary, right-click = module menu ─┐││
│ │ │ ▣40  Item title                                                    (no duration, no heart)   │││
│ │ │      🎞 · Subtitle                                                                            │││
│ │ └──────────────────────────────────────────────────────────────────────────────────────────────┘││
│ │   row shell: MinHeight 52, gap 12, pad (4,2,4,2), art 40 (WaveeSize.ArtThumb) at r4             ││
│ │   state: TrackRow.StateOf(bridge, lib, track) — the module row DOES take the now-playing re-skin ││
│ │          (accent title + equalizer) and the paused variant; the wrapper adds the r4 hover veil   ││
│ │   the context menu is attached ONLY when acts AND overlay are both non-null (ModulePage.cs:568)  ││
│ │   rows: Dir 1, gap 2, revealed in DetailRevealRamp.Chunk(12)-sized slices                        ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:3:cards — Dir 0, gap 12, Wrap true; MediaCard.Shelf(cardW = 168) ───────────────────────────┐││
│ │ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐                                                              │││
│ │ │168 │ │168 │ │168 │ │168 │ │168 │   the wrap row measures the cards; MediaCard.ShelfHeight     │││
│ │ └────┘ └────┘ └────┘ └────┘ └────┘   (cardW + 72 = 240) is the VIRTUALIZED shelf's reserve and  │││
│ │   a cell whose uri is the item in the bar takes the      is not applied here (MediaCard.cs:212) │││
│ │   now-playing overlay; hover reveals the play FAB —                                             │││
│ │   LazyNowPlayingOverlay, keyed on cardUri = ModuleUri.Encode(moduleId, playableId)              │││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:4:links — Dir 1, gap 2 ──────────────────────────────────────────────────────────────────────┐│
│ │ ┌ pad (12,8,12,8) r4 HoverFill Subtle² Cursor Hand Role Hyperlink Focusable ──────────────────┐  ││
│ │ │ Website                                                                             ↗ 14   │  ││
│ │ │ example.com                                                    12/16 TrackMeta             │  ││
│ │ └────────────────────────────────────────────────────────────────────────────────────────────┘  ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────── bottom pad 112 = PlayerDock.Reserve 72 + 40 ────────────────────────┘
```

### W11 — Module page, derived skeleton (cold, no cached document)

```
┌ content pad (32,40,32,112) ─────────────────────────────────────────────────┐
│ ┌──────────────┐  ▓▓▓▓▓                              ← SeedMeta  (5 figure spaces)
│ │              │  ▓▓▓▓▓▓▓▓▓▓▓▓                       ← SeedTitle (12 figure spaces) at Ui.Title
│ │  232 cover   │  ▓▓▓▓▓▓▓▓                           ← SeedLine  (8 figure spaces) at 14/20
│ │  shimmer     │                                     no actions, no sections (Seed has [] , [])
│ └──────────────┘                                                            │
└─────────────────────────────────────────────────────────────────────────────┘
Seed = ModulePageDoc(CurrentVersion, TemplateEntity, PageHero(SeedTitle, null, SeedLine, null, SeedMeta, false), [], [], null)
       — ModulePage.cs:65-75.  U+2007 FIGURE SPACE measures like a digit and paints nothing.
Reveal = SkelReveal.Soft → the whole content blur-rises: Opacity 0→1, TranslateY 8→0, BlurSigma 3→0,
         500 ms (Expressive.VerySlow), Easing.SmoothOut (SkeletonRegion.cs:189 → MotionRecipes.SoftReveal).
A REVISIT paints its stage on the first frame instead: the seed is `ModulePages.Get(pageUri)` when the sync page
cache holds this entity (ModulePage.cs:104-105).
```

### W12 — Module page, FAILED

```
┌ content pad (32,40,32,112) ─────────────────────────────────────────────────┐
│                                                                             │
│                     This page couldn’t be loaded.         Ui.Title 28/36/600 │
│                     Check your connection and try again.  Caption 12/16 sec  │
│                            [ 16 spacer ]                                     │
│                          ▛▀▀▀▀▀▀▀▀▀▜                      Button.Standard    │
│                          ▙  Retry   ▟                     (never Accent)     │
│                                                                             │
│                        ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▜   ← ONLY when the cached document │
│                        ▙ Open in browser  ▟     names an openUrl action      │
│                          pad-top 8, Justify Center                           │
└─────────────────────────────────────────────────────────────────────────────┘
ErrorState.Build(error, retry, loc modulePage.error) + the escape hatch — ModulePage.cs:257-268, 272-280.
Retry re-fires the resource through `reload.Value++` (dep key `(pageUri, gen)`) — ModulePage.cs:94-105, 155.
A route that does not parse renders EmptyState.Build(loc modulePage.error) with NO retry — ModulePage.cs:88-89.
```

### W13 — Watch page, stage IDLE (poster) @ 1280 (page ≈ 1040)

```
   ▲ 84 DIP masthead reserve
┌ "module-stage" — Shrink 0, AspectRatio 16/9, MaxHeight 560, Fill #000000FF, ClipToBounds ───────────┐
│                                                                                                     │
│                      poster: ArtworkFill at Opacity 0.40 over the black ground                      │
│                                                                                                     │
│                                        ⎛        ⎞                                                   │
│                                        ⎜   ▶    ⎟   64-DIP disc, Radii.Full, ButtonAppearance.Accent│
│                                        ⎝        ⎠   glyph box 16 DIP, tooltip "Play"                │
│                                                                                                     │
│  height h = pageWidth / (16/9); at 1040 → 585 → CLAMPED to 560, the ELEMENT pillarboxes inside      │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
┌ ScrollView → content pad (32,40,32,112) → "watch-caption" Dir 1 gap 12 ─────────────────────────────┐
│ The video title, up to two lines, wrapping            Ui.Subtitle 20/28/600 (NOT PageHero)          │
│ ▭LIVE▭  1.2M views · 2 days ago                       badge h18 + Caption 12/16 secondary ≤2 lines  │
│ ⬤ 40   Channel name                                   PersonPicture 40 + BodyStrong 14/20/600       │
│   pad (8,4,12,4) · r999 · HoverFill Subtle² when the channel has an entity id                       │
│ ▛▀▀▀▀▀▀▀▀▜ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜ ⊙   capsules h36 r999 · gap 8 · Wrap · AlignItems Center · then a 36 round │
│ ▙ ▶ Play ▟ ▙ ↗ Open on YT ▟ ⋯   "…" — which exists ONLY when a play chip carried a PlayableId AND     │
│                                 acts + overlay are non-null (WatchPageView.cs:286-294).              │
│                                 glyph per kind: play ⇒ Icons.Play, openUrl ⇒ Icons.OpenInNewWindow,   │
│                                 moduleAction ⇒ NO glyph (WatchPageView.cs:276-278). Accent only where │
│                                 the document said Primary; every other capsule is Standard.           │
│ ┌ "watch:description" pad (16,12,16,12) r8 FillCardSecondary 1px StrokeCardDefault gap 8 ──────────┐│
│ │ 1.2M views · 2 days ago                        Ui.Body 14/20 at Weight 600 — the DISSOLVED facts ││
│ │ The module's prose, 14, Tok.TextSecondary, clamped to 3 lines with "… More"                      ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ "watch:shelf" — PagedShelf(measured: true, maxItems 16, header Surfaces.SectionHeader) ──────────┐│
│ │ Up next                                                                          ‹  ›  chevrons  ││
│ │ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐                                      ││
│ │ │ 16:9 ▣  │ │ 16:9 ▣  │ │ 16:9 ▣  │ │ 16:9 ▣  │ │ 16:9 ▣  │  MediaCard.VideoCard, cardW fitted   ││
│ │ │ Title   │ │ Title   │ │ Title   │ │ Title   │ │ Title   │  into [150, 200], gap 12             ││
│ │ │ Sub · m │ │ Sub · m │ │ Sub · m │ │ Sub · m │ │ Sub · m │  edgeFade 36                         ││
│ │ └─────────┘ └─────────┘ └─────────┘ └─────────┘ └─────────┘                                      ││
│ │   cell states: hover reveals a 44-DIP play FAB over the thumb (LazyNowPlayingOverlay, armed only  ││
│ │   after HoverMotionGate sees real travel) and the cell whose uri is the item in the bar takes the ││
│ │   now-playing overlay; title 1 line ellipsised; an item with no subtitle AND no meta drops the    ││
│ │   third line to an empty box (MediaCard.cs:908-914, WatchPageView.cs:392-399)                     ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

`WatchItem.IsLive` is projected (`WatchPageModel.cs:41, 279`) and then **never drawn** — `MediaCard.VideoCard` has no
live parameter, so a live cell in the shelf is indistinguishable from a recorded one. §9.4.

### W14 — Watch page, stage LIVE (this entity is the item in the bar)

```
┌ "module-stage" — IDENTICAL envelope: same box, same height, same black ground, no reflow ───────────┐
│                                                                                                     │
│            ███ the app's ONE video surface (DockedVideoSurface, Face = PageStage) ███                │
│            the PosterGround stays UNDER it; the element's own PosterMotion dissolves the             │
│            poster into the first decoded frame — exactly one cross-fade, never two                   │
│                                                                                                     │
│            hover reveals the surface's own 30-DIP top strip: ⧉ pop out · ⛶ fullscreen · ✕            │
│            BEFORE the first frame the ELEMENT's own PosterContent paints: the same dimmed art plus a │
│            centred spinner (DockedVideoSurface.Poster → LoadingOverlay, :409-413) — a manifest/DRM   │
│            resolve takes real time on every track change and must not read as a black box           │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
   (the PlayCta's Flow.Show predicate is now false ⇒ the 64-disc is unmounted, not hidden)
│ ▭▶ Playing▭ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜ ⊙      the play capsule is REPLACED by the inert badge:                 │
│             ▙ ↗ Open on YT ▟ ⋯      h36, pad (18,0,18,0), r999, Fill FillSubtleSecondary,           │
│                                     1px StrokeCardDefault, ▶ 12 in WaveeAccent.Decor,               │
│                                     Caption "Playing" at Weight 600, Role = Text                    │
   Meanwhile the RIGHT RAIL gives its docked video card back and shows the queue — DockedVideoHosting
   .ShouldMount: the rail face requires `!stage` (DockedVideoHosting.cs:137-147). See 21-right-rail….
```

### W15 — Watch page @ 700 (page ≈ 460) — the stage never demotes

```
┌ stage: AspectRatio 16/9 at 460 wide → h = 259 (well under the 560 cap) ─────┐
│                       ⎛  ▶  ⎞  still 64 DIP                                 │
└─────────────────────────────────────────────────────────────────────────────┘
│ The video title, wrapping to two lines at 20/28                             │
│ ▭LIVE▭  1.2M views · 2 days ago                                             │
│ ⬤ Channel name                                                              │
│ ▛▀▀▀▀▀▀▀▀▜ ⊙        ← chips WRAP (Wrap = true); "…" follows on the same row  │
│ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜        or the next                                          │
│ ┌ description card (same padding, narrower) ───────────────────────────────┐│
│ └──────────────────────────────────────────────────────────────────────────┘│
│ ┌ shelf: PagedShelf re-fits to 2 cards per page at [150,200] ──────────────┐│
│ └──────────────────────────────────────────────────────────────────────────┘│
DockedVideoHosting.DockedHostAvailable(railFits, pageStageWouldHost) = railFits **|| pageStageWouldHost**
(DockedVideoHosting.cs:158-159) — a narrow window demotes the RAIL's card to the floating mini player but
NEVER a watch page's own stage. This is a non-negotiable: it is the whole point of the page.
```

### W16 — Module playable row, right-click

```
┌ TrackRow.ArtCard(Rail) in its r4 box ──────────────────────────┐
│ ▣40  Item title                                                │
│      🎞 · Subtitle                                             │  ← right-click anywhere on the box
└────────────────────────────────────────────────────────────────┘
  ╔═ Menus.ModuleTrack(ctx) = Menus.Tracks(ctx, showGoToAlbum: FALSE, extras: OpenOnModuleRows) ═╗
  ║ [ ▶ Play ] [ ⤓ Play next ] [ ≡ Add to queue ] [ ♥ Save ]   ← the labeled command STRIP        ║
  ╟───────────────────────────────────────────────────────────────────────────────────────────────╢
  ║ Add to playlist                            ▸                                                  ║
  ║ (no "Go to album" — showGoToAlbum is false for a module playable)                              ║
  ║ Go to artist / Go to artists ▸    — only when an ArtistRef carries a uri; a module's do NOT    ║
  ║ Share                                                                                         ║
  ║ (View credits / Song radio / Video ▸ — each gated by ActionRules; off for a module playable)   ║
  ╟───────────────────────────────────────────────────────────────────────────────────────────────╢
  ║ ↗ Open on <Module display name>   loc modulePage.openOn({name}); only when the cached page doc ║
  ║                                   for THIS playable names an http(s) openUrl action            ║
  ╚═══════════════════════════════════════════════════════════════════════════════════════════════╝
Menus.cs:56-58, 81-134, 150-181.  The SAME menu is attached to the watch page's "…" chip (WatchPageView.cs:286-294).
Row order, verbatim from `Menus.TrackRows` (Menus.cs:81-134): Add to playlist · [Move to playlist — only in an editable
playlist context, so never here] · [Go to album / Go to podcast — suppressed] · Go to artist(s) · Share ·
[View credits] · [Song radio] · [Video ▸] · ─── · Open on <Module>. The destructive block (Remove from this playlist /
Remove from queue) is absent on a module row. The strip's fourth verb is `ToggleLike` — its label flips Save/Saved.
```

### W17 — Module page, `custom` template and the entity hero FALLBACK

```
custom (sections-only, module supplied NO hero)            entity, doc.Hero == null
┌ content pad (32,40,32,112) ───────────────┐             ┌ content pad (32,40,32,112) ────────────────┐
│ (no hero row at all — the page opens on   │             │ ┌────────────┐  (no eyebrow)               │
│  its first Section)                       │             │ │  232 art   │  The route's Arg            │
│ ┌ sec:0:facts ──────────────────────────┐ │             │ │ placeholder│  ← _route.Arg ?? loc         │
│ │ About                                 │ │             │ └────────────┘    modulePage.title="Module"│
│ └───────────────────────────────────────┘ │             │  (no subtitle, no meta line)               │
└───────────────────────────────────────────┘             └────────────────────────────────────────────┘
ModulePage.cs:286-293.  `custom` ⇒ hero only when doc.Hero is non-null; `entity` ⇒ always a hero, synthesised from the
navigating surface's label when the document carries none. Neither arm invents an image: `ImageOf(null)` is the
placeholder tile, and the action row / sections are skipped when the document has none (`:294`, `:412` returns an
empty box for an action row whose every action was unhonourable).
```

### W18 — Watch page, THIS entity is playing but the surface is hosted ELSEWHERE

```
┌ "module-stage" — identical envelope, and it is back on the POSTER ──────────────────────────────────┐
│                      poster ArtworkFill @ 0.40 over black                                           │
│                                        ⎛   ▶    ⎞  ← the 64-disc is MOUNTED AGAIN                   │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
│ ▭▶ Playing▭ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜ ⊙   ← and the caption still says Playing                                │
```

`Live()` = `ShouldMount(PageStage, VideoPlacementNow(), …)`, whose FIRST test is
`resolved == SurfacePlacement.Docked` (`DockedVideoHosting.cs:140`; `WatchPageView.cs:95-97`). So every state that
resolves the placement away from Docked — **fullscreen**, the **popped-out window**, the **floating mini player**, the
surface turned off — unmounts the video from the page and re-mounts the idle CTA, while `isPlaying` (a plain uri
compare made in `ModulePage.Render`, `:113`) keeps the chip row on the inert **Playing** badge. Consequences the
re-author must keep or deliberately change: the 64-disc is clickable in this state and re-issues `PlayWatch`, and the
disc is what the user sees the moment they enter fullscreen from this very page. **Not a bug to "fix" silently.**

### W19 — Watch page, MINIMAL document (title only; no play action)

```
┌ "module-stage" — the same 16:9 black box, poster only, NO disc ─────────────────────────────────────┐
│                      (onPlay is null ⇒ the CTA layer is never added — WatchPageView.cs:107)         │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
│ The video title                                      ← the ONLY caption row that is unconditional   │
│ (no meta row, no channel row, no chips, no "…", no description card, no shelf)                      │
```

`StagePlay` returns null when no `play` action carries a `PlayableId` (`ModulePage.cs:223-233`), and then
`StagePlayableUri` is `""` too — so this page can never go Live, never claims `ActiveStagePlayable`, and shows a bare
poster. A document whose hero title is missing renders an EMPTY first line (`Trimmed(hero?.Title) ?? ""`).

### W20 — Watch page, COLD (the document has not landed: there is NO stage yet)

```
   ▲ 84 DIP masthead reserve
   (NO 16:9 box at all — the stage slot is `new BoxEl()`)
┌ ScrollView → content pad (32,40,32,112) → Skel.Region(Soft) ────────────────────────────────────────┐
│ ┌──────────────┐  ▓▓▓▓▓                       ← the ENTITY skeleton, exactly as W11                 │
│ │  232 cover   │  ▓▓▓▓▓▓▓▓▓▓▓▓                                                                     │
│ │  shimmer     │  ▓▓▓▓▓▓▓▓                                                                         │
│ └──────────────┘                                                                                    │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
        ⇓ the document lands ⇒ the 16:9 stage APPEARS above the scroller and the whole caption drops ⇓
┌ "module-stage" — 16:9, up to 560 tall, materialising in one frame ──────────────────────────────────┐
```

**The loading seed is `TemplateEntity`** (`ModulePage.cs:69-72`), so `WatchPageModel.From` returns null
(`WatchPageModel.cs:131`) and `ModulePage.Render`'s stage slot takes the EMPTY-box arm (`ModulePage.cs:175-176`).
Three consequences the re-author has to decide about, because 0.2.9 simply lives with them:

1. A **cold** watch page has no stage while loading, so Ready pushes the entire caption down by up to 560 DIP. §0.13's
   "idle → live must not reflow" is about the *poster → video* handover only; **load → ready DOES reflow**, and hard.
2. The cold skeleton of a watch page is derived from the **entity** layout (hero square + two bars), never from the
   caption it is about to become — the shimmer shape does not predict the page.
3. `stagePlayable` is `""` for that whole window, so the claim effect writes `""` and the rail keeps the surface until
   the document lands (`ModulePage.cs:112, 138-145`).

A REVISIT inside the 10-minute page TTL skips all three: the seed is `ModulePages.Get(pageUri)`, already a watch
document, so the stage is up on the first composed frame (`ModulePage.cs:104-105`; `ModulePages.cs:25, 52-56`).

### W21 — Watch page, FAILED with a cached document (stage over error)

```
┌ "module-stage" — STILL MOUNTED: poster + 64-disc, or live video ────────────────────────────────────┐
│                                  ⎛   ▶    ⎞                                                         │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
┌ ScrollView → content → Skel.Region FAILED arm ──────────────────────────────────────────────────────┐
│                     This page couldn’t be loaded.        + Retry + Open in browser  (W12)            │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

`model` / `stagePlayable` are computed from `page.Value.Value` **outside** `Skel.Region` (`ModulePage.cs:110-113,
175-177`), and a failed `Loadable` keeps its last value — the cached seed. So a refresh that fails on a page whose
document was cached leaves the stage exactly where it was (idle, or even LIVE and playing) with the error body
underneath it. This is reachable, and it is the right behaviour — do not "fix" it into a blank page — but it is a
state no wireframe covered.


---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| **Show page — right column** |
| episode scroller body | fills | pad L16 T12 R16 B96 · gap 12 | — | — | — | — | `EpisodeList.cs:95-100` |
| bottom reserve | 96 | = `PlayerDock.Reserve` 72 + `Spacing.XXL` 24 | — | — | — | — | `WaveeTokens.cs:81-83` |
| toolbar row | auto | gap 12 · margin-bottom 4 · `AlignItems Center` | — | — | — | — | `EpisodeList.cs:137-139` |
| status selector | 4 items | — | pill 4 × 3, `ScaleX 4` selected ⇒ **16 DIP wide** | `SelectorBar` | `Tok.AccentDefault` pill | — | `EpisodeList.cs:142`; `SelectorBar.cs:29-31` |
| order selector | 2 items | — | as above | `SelectorBar` | as above | — | `EpisodeList.cs:144` |
| section header | auto | — | — | `Ui.Subtitle` 20/28/600 | `Tok.TextPrimary` | — | `EpisodeList.cs:83,160`; `WaveeType.cs:57` |
| empty-filter box | auto | pad (16,20,16,20) | — | raw `TextEl` **14** | `Tok.TextTertiary` | also the ZERO-EPISODE arm (§9.4) | `EpisodeList.cs:84-86` |
| skeleton bar | — | RowGap 8 · text bar = 0.72 × its run | 4 | — | `Tok.FillSubtleSecondary`, pulse → 0.5 α | derived, never hand-authored | `SkeletonRegion.cs:34-38` |
| **Listen-next banner** |
| header → card | — | gap 8 (the banner's own column) | — | — | — | — | `EpisodeList.cs:155-160` |
| card | auto | pad L12 T12 R16 B12 · gap 16 | 8 (`Radii.Card`) | — | `Tok.FillCardSecondary` + 1 px `Tok.StrokeCardDefault` | ClipToBounds, no shadow | `EpisodeList.cs:161-167` |
| art | 72×72 | shrink 0 | 8 | — | `Surfaces.Artwork(decode = display)` | shimmer (72 < 80 ⇒ static tile) | `EpisodeList.cs:169-170`; `Surfaces.cs:203,211` |
| eyebrow | — | — | — | `Ui.Caption` 12/16/600 + tracking 30 | `Tok.TextTertiary` | — | `EpisodeList.cs:176`; `WaveeType.cs:38,54` |
| banner title | — | col gap 4 | — | raw `TextEl` **15 / Weight 700** | `Tok.TextPrimary` | 1 line, char-ellipsis | `EpisodeList.cs:177` |
| banner caption | — | — | — | raw `TextEl` **12** | `Tok.TextSecondary` | — | `EpisodeList.cs:178` |
| Resume pill | h36 | pad 18/6/18/7 | `Radii.Full` (→18) | Button label, Bold | `Tok.AccentDefault` + WCAG-picked ink | `Shrink = 0` | `EpisodeList.cs:257-259`; `WaveeCta.cs:79-107` |
| **Episode row** |
| card | auto (112–135) | pad 12 all · col gap 8 | 8 | — | rest: none · hover `Tok.FillSubtleSecondary` · 1 px `Tok.StrokeDividerDefault` | no shadow | `EpisodeList.cs:202-207` |
| row A (art · copy · disc) | — | gap 16 · `AlignItems Center` | — | — | — | — | `EpisodeList.cs:210-212` |
| art | 56×56 | shrink 0 | 8 | — | `Surfaces.Artwork` | static tile (56 < 80) | `EpisodeList.cs:215-216` |
| title | — | text-col gap 4 | — | raw `TextEl` **14 / Weight 700** | `Tok.TextPrimary` | 2 lines, wrap, char-ellipsis | `EpisodeList.cs:222` |
| description | — | — | — | raw `TextEl` **12** | `Tok.TextSecondary` | 2 lines, wrap, char-ellipsis | `EpisodeList.cs:223` |
| play disc | 40×40 | shrink 0 | 20 | glyph `Icons.Play` 15 | fill `Tok.AccentDefault`, ink `Tok.TextOnAccentPrimary` | `Elevation.Card` (dark 0/2/8 `#00000033`, light 0/2/4 `#0000001A`) | `EpisodeList.cs:247-253`; `Elevation.cs:18-21` |
| meta row | 16 | gap 8 | — | raw `TextEl` **12** | `Tok.TextTertiary` | — | `EpisodeList.cs:193-196, 229` |
| separator dot | — | — | — | `TextEl("·")` 12 | `Tok.TextTertiary` | — | `EpisodeList.cs:235` |
| "In progress" | — | — | — | raw `TextEl` **12 / Weight 600** | `Tok.AccentTextPrimary` | — | `EpisodeList.cs:200` |
| progress rule | h3 | — | 2 | — | ground `Tok.FillSubtleTertiary` · fill `Tok.AccentDefault` | ClipToBounds | `EpisodeList.cs:237-245` |
| **Show page — rail** (shared frame; see `03-detail-frame.md`) |
| rail | `railW` 280 / 224 / 188 | pad L16 T24 R8 B24 · gap 14 | — | — | `Tok.FillLayerDefault` on the scroller wrapper | — | `DetailRail.cs:20-21, 262-267, 290-298`; `DetailShell.cs:192` |
| cover | `railW − 24` (min 80) | shrink 0 | 8 | — | `HeroArtwork(saturation 1.18, decodePx 256)` | `Elevation.Card` | `DetailRail.cs:96, 145-162` |
| eyebrow | — | — | — | `Ui.Caption` 12/16/600 tracking 30 | `Tok.TextTertiary` | width = cover, 1 line | `DetailRail.cs:170, 590-593` |
| title | — | — | — | `WaveeType.DetailHero`: `Ui.Title` metrics in *Segoe UI Variable Display*, tracking −20 | `Tok.TextPrimary` | `Size` 28 (winH < 900) / 40, `MinSize` 18, ≤3 lines | `DetailRail.cs:183-189`; `DetailShell.cs:636-637` |
| Play pill | h36 | pad 18/6/18/7 | `Radii.Full` | Bold | cover-extracted accent | — | `DetailRail.cs:596-597`; `WaveeCta.cs:74-107` |
| CTA cluster | — | gap 12 · FAB group gap 8 · margin-top 4 · Wrap (the FAB group wraps as a UNIT) | — | — | — | `Layout = DetailRail.Shove` | `DetailRail.cs:212-240` |
| Follow (heart) FAB | 40×40 | — | 20 | `Icons.Heart` 16 | `SaveButton` | — | `DetailRail.cs:232-233` |
| Share FAB | 40×40 | — | 20 | `Icons.Share` 16 | `Tok.TextSecondary` | subtle interaction | `DetailRail.cs:235`, `PlaylistInlineEdit.cs:764-798` |
| description | — | — | — | `RichText.Of(…, 12, …)` | `Tok.TextSecondary`, links = accent | width = cover, ≤6 lines (≤3 when winH < 760) | `DetailRail.cs:249-253`; `DetailShell.cs:638` |
| **Module page** |
| root top reserve | 84 | = `Spacing.XXXL` 32 + `TitleLine` 52 | — | — | — | — | `ModulePage.cs:172`; `BrowseMastheadMetrics.cs:12-13`; `BrowseTiles.cs:295` |
| content | fills | pad L32 T40 R32 B112 | — | — | — | — | `ModulePage.cs:163` |
| body stack | — | gap 20 (`Spacing.XL`) | — | — | — | `MinWidth 0` | `ModulePage.cs:309` |
| hero row | auto | gap 20 · AlignItems Center · Wrap | — | — | — | — | `ModulePage.cs:351-356` |
| hero art | 232×232 | shrink 0 | 8 | — | `Surfaces.Artwork(decodePx 256)` | breathing shimmer (232 ≥ 80) | `ModulePage.cs:74, 348-349` |
| hero column | Grow 1 Basis 0 | gap 8 · Justify Center | — | — | — | `MinWidth 0` | `ModulePage.cs:339-344` |
| hero title ROW | — | gap 8 · `AlignItems Center` · `MinWidth 0` | — | — | — | title + `LiveBadge` on one line | `ModulePage.cs:323-327` |
| hero title | — | — | — | `Ui.Title` 28/36/600 | `Tok.TextPrimary` | `Shrink 1`, `MinWidth 0` | `ModulePage.cs:321` |
| LIVE badge | h18 | pad L4 R4 · `AlignItems`/`Justify` Center | 2 | `TextEl` 10/14/600, `NoWrap` | text + 1 px border both `WaveeAccent.Decor` = `Tok.AccentTextPrimary` | no fill, `Shrink 0`; copy = loc **`play.live` = "LIVE"** (literal caps) | `ModulePage.cs:383-396` |
| hero subtitle | — | — | — | raw `TextEl` **14 / 20** | rest `Tok.TextSecondary` → hover `Tok.TextPrimary` + `Underline` | `NoWrap`, 1 line, ellipsis | `ModulePage.cs:734-740` |
| hero meta | — | — | — | `Ui.Caption` 12/16 secondary | `Tok.TextSecondary` | ≤2 lines, wrap | `ModulePage.cs:337` |
| action button | stock | — | 4 (`Radii.Control`) | Fluent Button | `Accent` / `Standard` ramp | — | `ModulePage.cs:408` |
| action ROW | — | gap 8 · Wrap · `AlignItems Center` | — | — | — | `Enter FadeUp`, `Layout Shove` | `ModulePage.cs:413-418` |
| section shell | — | gap 12 | — | header = `Ui.Subtitle` | — | `AlignSelf Stretch`, `MinWidth 0` | `ModulePage.cs:489-500` |
| fact tile | Grow 1 Basis 0 | pad (12,8,12,8) · col gap 1 | 4 (`Radii.Control`) | value 18/800 · caption 11 | `Tok.FillCardSecondary` + 1 px `Tok.StrokeCardDefault`; value `Tok.TextPrimary`, caption `Tok.TextSecondary` | ClipToBounds | `StatTile.cs:21-56` |
| fact row | — | gap 8 · Wrap · Stagger 45 | — | — | — | — | `ModulePage.cs:520-525` |
| playable row wrapper | — | — | 4 | — | hover `Tok.FillSubtleSecondary` | — | `ModulePage.cs:563-567` |
| playable row itself | MinHeight 52 | gap 12 · pad (4,2,4,2) | 4 | title `Ui.BodyStrong` · meta `Ui.Caption` | now-playing re-skin from `TrackRow.StateOf` | art 40 (`WaveeSize.ArtThumb`) at `Radii.Control` | `TrackRow.cs:408-414, 508-513`; `ModulePage.cs:550-560` |
| playable rows stack | — | gap 2 | — | — | — | ramped in 12-row slices | `ModulePage.cs:681, 697` |
| card shelf | — | gap 12 · Wrap | — | — | `MediaCard.Shelf` | — | `ModulePage.cs:610, 616-620` |
| card width | 168 | — | 8 | — | — | the wrap row MEASURES the cards; `ShelfHeight(cardW) = cardW + 72` (=240) is the *virtualized* shelf's reserve and is **not** applied here | `ModulePage.cs:75`; `MediaCard.cs:212` |
| card hover FAB | 44 | — | circle | `Icons.Play` | `LazyNowPlayingOverlay` (also the now-playing state for `cardUri`) | `HoverMotionGate`-armed | `MediaCard.cs:26, 35-40` |
| link row | — | gap 8 · pad (12,8,12,8) | 4 | title `Ui.BodyStrong` · sub `Ui.Caption` | hover `Tok.FillSubtleSecondary`; glyph `Icons.OpenInNewWindow` 14 `Tok.TextTertiary` | `Interaction.Subtle` | `ModulePage.cs:635-655` |
| link stack | — | gap 2 | — | — | — | — | `ModulePage.cs:662` |
| **Watch page** |
| stage | w = page, h = w/(16/9) | — | 0 | — | `Tok.MediaLetterbox` = **opaque #000000** | ClipToBounds; **no** shadow, **no** transition | `WatchPageView.cs:121-140`; `Tokens.cs:372` |
| stage max height | 560 | — | — | — | — | clamp AFTER the aspect derive ⇒ pillarboxing is the element's | `WatchPageView.cs:64` |
| poster ground | fills | — | — | — | `Surfaces.ArtworkFill(art, 0)` at **Opacity 0.40** | — | `DockedVideoSurface.cs:423-426` |
| idle play disc | 64×64 | — | `Radii.Full` (→32) | glyph box **16** (`WaveeCta.Icon` never overrides `IconButton.Style.GlyphSize`, so the disc does NOT scale its glyph with its box) | `ButtonAppearance.Accent` ramp **plus that ramp's 1 px border brush** (`WaveeCta.cs:140-144`) | button hover/press ramp 1.04 / 0.96 | `WatchPageView.cs:69, 155-164`; `IconButton.cs:30, 35` |
| caption stack | — | gap 12 | — | — | — | `AlignSelf Stretch` | `WatchPageView.cs:201-205` |
| watch title | — | — | — | `Ui.Subtitle` 20/28/600 | `Tok.TextPrimary` | ≤2 lines, wrap | `WatchPageView.cs:187-190` |
| meta row | — | gap 8 | — | `Ui.Caption` 12/16 | `Tok.TextSecondary` | ≤2 lines, wrap | `WatchPageView.cs:210-221` |
| channel row | — | gap 12 · pad (8,4,12,4) | `Radii.Full` | `Ui.BodyStrong` 14/20/600 | hover `Tok.FillSubtleSecondary` **only when linked**, else `ColorF.Transparent` | — | `WatchPageView.cs:231-248` |
| channel avatar | 40 | — | circle | initials 42 % of Ø = 16.8 | `PersonPicture` quaternary fill + 1 px card stroke | — | `WatchPageView.cs:71, 242` |
| chip row | — | gap 8 · Wrap | — | — | — | — | `WatchPageView.cs:297-301` |
| chip | h36 | pad 18/6/18/7 | `Radii.Full` | Button label Bold | `Accent` (Primary) / `Standard` | scale 1.04 / 0.96; glyph `Icons.Play` (play) / `Icons.OpenInNewWindow` (openUrl) / none (moduleAction) | `WatchPageView.cs:276-281`; `WaveeCta.cs:88-107` |
| Playing badge | h36 | pad (18,0,18,0) · gap 8 · `AlignItems`/`Justify` Center | `Radii.Full` | `Ui.Caption` 12/16 at Weight 600 | `Tok.FillSubtleSecondary` + 1 px `Tok.StrokeCardDefault`; the WORD is `Ui.Caption`'s own **`Tok.TextSecondary`**, only the glyph (12) is `WaveeAccent.Decor` | `Role = Text`, `Shrink 0` | `WatchPageView.cs:306-319`; `Typography.cs:39` |
| overflow "…" | 36×36 | — | `Radii.Full` | `Icons.More` 16 | `Standard` button ramp | `ClickRequestsContext` | `WatchPageView.cs:290-293`; `WaveeCta.cs:121-152` |
| description card | — | pad (16,12,16,12) · gap 8 | 8 | fact line `Ui.Body` 14/20 at **Weight 600**; prose 14 | `Tok.FillCardSecondary` + 1 px `Tok.StrokeCardDefault`; the fact line is `Ui.Body`'s **`Tok.TextPrimary`**, the prose `Tok.TextSecondary`, links `Tok.AccentTextPrimary` | `AlignSelf Stretch` | `WatchPageView.cs:330-342`; `Typography.cs:40` |
| description clamp | 3 lines | — | — | — | — | `RichText.ExpandableFlex` inline "… More" | `WatchPageView.cs:72, 332` |
| shelf | ≤16 cells | gap 12 · headerGap 12 · edgeFade 36 | — | header `Surfaces.SectionHeader` — **`null` when the section had no Title** | — | `measured: true` (no virtualization); pager `Chevrons` | `WatchPageView.cs:359-385`; `PagedShelf.cs:114-152` |
| shelf cell | fitted [150, 200] | pad (8,8,8,12) · gap 8 | 8 | title `Ui.BodyStrong`, meta `Ui.Caption` | `Tok.FillCardDefault`, hover `Tok.FillControlSecondary`, 1 px `Tok.StrokeCardDefault` | `Elevation.Card`, scale 1.02 / 0.98 | `MediaCard.cs:885-916` |
| shelf thumb | `cardW − 16` × ×9/16 | — | 4 | — | `Surfaces.Artwork(decodePx 480)` | — | `MediaCard.cs:906` |

Token values (dark / light), `PaletteBuilder.cs`:
`FillCardSecondary` `#FFFFFF08` / a solved card² · `FillSubtleSecondary` `#FFFFFF0F` / `#00000009` ·
`FillSubtleTertiary` `#FFFFFF0A` / `#00000006` · `FillCardDefault` `#FFFFFF0D` / solved ·
`StrokeCardDefault` `#00000019` / `#0000000F` · `StrokeDividerDefault` `#FFFFFF15` / `#0000000F` ·
`TextPrimary` `#FFFFFF` / `#000000E4` · `TextSecondary` `#FFFFFFC5` / `#0000009E` · `TextTertiary` `#FFFFFF87` / solved ·
`TextOnAccentPrimary` `#000000` / `#FFFFFF` · default `AccentDefault` `#60CDFF` / `#005FB8` ·
`AccentTextPrimary` `#A6D8FF` / `#004275` (`PaletteBuilder.cs:302-340, 412-450`; live values come from the
cover-derived ramp — `Tokens.cs:438-449`).

---

## 4. Colour & material

1. **Page tone (show page only).** The whole detail surface sits on ONE opaque art-derived plane —
   `CoverPaletteLeaves.PageTonePlane(paletteUrl, liveUrl, colorWashesDisabled, heroBand, pageH, heroOnly: false)`,
   mounted at index 2 of the shell's root ZStack, behind everything and OUTSIDE every scroller
   (`DetailShell.cs:551-577, 677-688`). `heroBand = pageH × 0.55` in the two-column arm (`DetailShell.cs:189, 571`).
   Alphas 0.20 dark / 0.30 light are owned by `CoverPageTonePlane`. Specified in `00-design-system.md` /
   `03-detail-frame.md`; the episode column adds nothing of its own — **every episode card is transparent at rest**, so
   the page tone reads through it and only its hairline and hover veil are painted.
2. **The rail's own layer.** `ScrollView(rail)` sits on `Tok.FillLayerDefault` (dark `#3A3A3A4C`, light `#FFFFFF80`) —
   `DetailRail.cs:290-298` — so the contextual column recedes while the episode column stays on the page tone.
3. **Cover saturation.** The rail cover is drawn at `saturation: 1.18` (`DetailRail.cs:160`); the episode arts and the
   module hero are **not** (`saturation` defaults to 1).
4. **Art placeholders.** `Surfaces.Artwork` stacks `Shimmer(url, dw, dh, w, h, r)` under the image. Below
   `ShimmerMinEdge = 80` (every episode art: 56 and 72) the tile is a STATIC opaque neutral tinted toward the cover's
   own graded colour at `TintStrength = 0.55` when `CoverColorPlane` has one, through `WatchedPlaceholder` so a landed
   grading repaints exactly that tile with no component re-render (`Surfaces.cs:60-115, 203-230`). At 232 (module hero)
   the breathing `CoverShimmer` component is used instead.
5. **Accent budget on these surfaces** (`WaveeTokens.cs:39-51`):
   - *AccentAction* (solid plate + on-accent ink): the rail Play pill, the episode play disc, the Resume pill, the watch
     page's primary capsule and the idle stage disc. At most one per screenful — the episode list breaks that rule on
     purpose (one disc per card) because the disc IS the row's affordance; it is the same value at a 40-DIP scale.
   - *AccentSelection*: only the two `SelectorBar` pills.
   - *AccentDecor* (`WaveeAccent.Decor` = `Tok.AccentTextPrimary`): the "In progress" caption, the LIVE badge's text and
     border, the "Playing" badge's glyph, the progress rule's fill, rich-text links.
6. **The LIVE badge has no fill.** A 1 px `WaveeAccent.Decor` hairline at `Radii` 2 with the word in the same ink,
   `Height 18`, `Padding L/R 4` — `ModulePage.cs:383-396`. It is `internal` precisely so the watch layout can reuse the
   same element (`WatchPageView.cs:213`); two spellings of "live" on two readings of one document is the drift the
   single-owner rule exists to stop.
7. **The stage's ground is opaque black, not a theme token that follows light/dark.** `Tok.MediaLetterbox` is
   `new(0,0,0,1)` in both themes (`Tokens.cs:372`) — a composited video is a DestOut hole punched into the back buffer,
   and its surround must be video black in every theme.
8. **The poster is the video's own ground.** `DockedVideoSurface.PosterGround(art)` = `Grow 1`, `Opacity 0.40`,
   `Surfaces.ArtworkFill(art, corners: 0)` — byte-identical to what the docked card paints under its own poster, so the
   idle→live handover is exactly ONE dissolve (`DockedVideoSurface.cs:415-426`, `WatchPageView.cs:48-52`).
9. **No blur, no acrylic, no Mica anywhere on these three surfaces.** The stage forbids every one of them structurally
   (an ancestor opacity group / blur / edge-fade pushes an offscreen RT and the punch never reaches the back buffer);
   the episode list and the module body simply do not use them.
10. **Light vs dark.** Nothing here branches on `Tok.Theme` in app code. The two theme-dependent values are resolved by
    the engine: `Elevation.Card` (blur 8 / offset 2 / `#00000033` dark vs blur 4 / offset 2 / `#0000001A` light,
    `Elevation.cs:18-21`) and every `Tok.*` above. The one app-side polarity decision on this surface is the art
    placeholder's, and it is made inside `Surfaces.PlaceholderFor(url, light)`.

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| page enter (nav) | whole page | ContentHost's keep-alive slide | — | — | — | — | engine policy | `ContentHost.cs`; **the module page root deliberately carries NO entrance** — `ModulePage.cs:36-39` |
| show page: loadable Ready (cold) | detail shell | Opacity | 0 → 1 | `Expressive.Fast` 250 | `SmoothOut` | — | the fade survives, travel snaps | `DetailPage.cs:288` (`SkelReveal.FadeOnly`) → `SkeletonRegion.cs:167` |
| module page: loadable Ready | `content` subtree | Opacity + TranslateY + BlurSigma | 0→1, 8→0, 3→0 | `Expressive.VerySlow` **500** | `SmoothOut` | — | `MotionRecipes.SoftReveal` returns early ⇒ no motion | `ModulePage.cs:154` (`SkelReveal.Soft`) → `SkeletonRegion.cs:189`, `MotionRecipes.cs:55-62` |
| shimmer → real | shimmer orphan | Opacity | 1 → 0 | `SkeletonStyle.ExitMs` = `Expressive.Fast` 250 | — | drawn UNDER the revealing content | snaps | `SkeletonRegion.cs:34-38` |
| shimmer at rest | shimmer bars | Opacity pulse | 1 → 0.5 → 1 | 1000 ms loop | — | — | ignored | `SkeletonRegion.cs:35` |
| a module section mounts | `Section()` box | Opacity | 0 → 1 | folded into `Shove`'s `Expressive.Fast` 250 | `SmoothOut` | — | `ReducedMotionPolicy.KeepFade` keeps the fade | `ModulePage.cs:496`; `DetailRail.cs:52` |
| a sibling section changes height | `Section()` box | Position (FLIP) | old → new origin | `Expressive.Fast` 250 | `SmoothOut` | — | travel snaps | `DetailRail.cs:56-57` |
| facts section mounts | fact tiles | per-child entrance offset | — | — | — | **45 ms × index** (`WaveeMotion.MastheadStaggerMs`) | **explicitly 0** (`Motion.ReducedMotion ? 0f : …`) | `ModulePage.cs:520-523`; `WaveeMotion.cs:75` |
| fact tile value changes in place | tile value | Opacity + Dy + Blur | 0→1, 4→0, 2→0 | 150 | `EaseInOut` | — | engine snaps | `StatTile.cs:40`; `MotionRecipes.cs:229-233` |
| playables section mounts, **> 12 rows** | rows | mount count | 12 → +12/frame → all | `DetailRevealRamp.Chunk` 12 per FRAME, `Cap` 60 (`Next` returns `Done` once a chunk reaches `min(rows, 60)`, so a 100-row block mounts its tail in ONE step) | — | driven by a `TickerClock` that is mounted only while the ramp runs | unaffected (it is a mount schedule, not an animation) | `ModulePage.cs:683-698`; `DetailRevealRamp.cs:11-22` |
| playables section mounts, **≤ 12 rows** | rows | — | all at once | — | — | **no ramp, no clock at all** — the short-circuit is explicit ("a short list is never worth a ramp") | — | `ModulePage.cs:679-681` |
| a watch document lands (cold) | the whole page | **layout jump** | no stage → a ≤560-DIP 16:9 stage above the scroller | one frame, untweened | — | the stage slot goes from `new BoxEl()` to `Stage(...)` because the loading seed is `TemplateEntity` | — | W20; `ModulePage.cs:69-72, 175-177`; `WatchPageModel.cs:131` |
| hover an episode card | card fill | Fill | transparent → `Tok.FillSubtleSecondary` | engine brush ramp `WaveeMotion.Faster` 83 | WinUI brush | — | unaffected (a colour, not motion) | `EpisodeList.cs:206` |
| hover the episode play disc | disc | Scale | 1 → **1.07** | engine hover fade | — | — | `ScaleTier.Hover` returns **1f** | `EpisodeList.cs:251`; `WaveeMotion.cs:48, 179` |
| press the episode play disc | disc | Scale | 1 → **0.92** | engine press fade | — | — | returns **1f** | same |
| hover any pill / chip / stage disc | button | Scale | 1 → **1.04** | — | — | — | 1f | `WaveeCta.cs:104-106, 147-149`; `WaveeMotion.cs:43` |
| press any pill / chip / stage disc | button | Scale | 1 → **0.96** | — | — | — | 1f | same |
| hover a shelf cell (watch) | card | Scale + Fill | 1 → **1.02**; `FillCardDefault` → `FillControlSecondary` | brush 83 | — | armed only after `HoverMotionGate` sees real pointer travel > 0.5 DIP | 1f | `MediaCard.cs:894-897`; `WaveeMotion.cs:39, 141-158` |
| hover the hero subtitle link | that word | Color + Underline | `TextSecondary` → `TextPrimary`, underline off → on | engine text ramp | — | boundary is the LINK's own box, never the row | unaffected | `ModulePage.cs:719-743` |
| hover the channel row | row fill | Fill | transparent → `Tok.FillSubtleSecondary` | 83 | — | only when `linked` | unaffected | `WatchPageView.cs:235` |
| first decoded video frame | stage | `PosterContent` cross-fade | poster → frame | element's own `PosterMotion` | — | — | engine policy | `WatchPageView.cs:48-52` |
| the playing item becomes / stops being this entity | stage layers | `Flow.Show` mount/unmount | poster+CTA ⇄ video | instant (a mount, not a tween) | — | — | — | `WatchPageView.cs:107, 115-119` |
| an art tile's image decodes | image over shimmer | Opacity | 0 → 1 | engine image cross-fade (~220) | — | — | — | `Surfaces.cs:238-289` |
| `PlaylistShareButton` copy | glyph | `MotionRecipes.IconSwap` (Sx/Sy 0.25→1, Opacity, Blur 2) | — | `Expressive.Fast` 250 | `EaseInOut` | resets after **1600 ms** | engine snaps | `PlaylistInlineEdit.cs:774, 792` |
| rail resize drag into the resist zone | rail subtree | Opacity (paint-bound) | 1 → `_railFade` | compositor channel, no re-render | — | — | — | `DetailShell.cs:787` |
| the SHOW rail's late rows land (eyebrow, description) | `rail:eyebrow` / `rail:desc` wrappers | Opacity (folded into the FLIP) | 0 → 1 | `Expressive.Fast` 250 | `SmoothOut` | — | `ReducedMotionPolicy.KeepFade` — fade kept, travel snaps | `DetailRail.cs:62-71, 169-170, 249-253` |
| a rail row is pushed by a late sibling | any keyed rail row | Position (FLIP) | old → new origin | `Expressive.Fast` 250 | `SmoothOut` | — | snaps | `DetailRail.cs:55-57` |
| a fact TILE mounts | the tile box itself | Opacity + Position | 0 → 1 | `Expressive.Fast` 250 | `SmoothOut` | on top of the row's 45 ms stagger | fade kept | `StatTile.cs:47` |
| the watch shelf pages (chevrons) | the strip | scroll offset | page → page | `PagedShelf`'s own | — | — | — | owned by `02-cards-and-controls.md`; `PagedShelf.cs:1206` |
| the watch description expands ("… More") | `watch:description`, `watch:shelf` | **nothing** | — | — | — | — | — | the caption's children carry NO `Enter`/`Layout` (`WatchPageView.cs:201-205`) — the shelf JUMPS down. Contrast the module page's `Section()`, which FLIPs |

**No frame-time exception exists on this surface.** Nothing here samples `Environment.TickCount64`: every animation is
an engine channel driven by the frame clock, and the one app-owned per-frame driver is `RampedRows`' `TickerClock`
(`ModulePage.cs:688-693`), which counts FRAMES, not wall-clock. The one wall-clock timer is
`PlaylistShareButton`'s `UseTimeout(1600)` — a frame-clock timeout hook, not `TickCount64`.

---

## 6. Interaction

### 6.1 Episode list

| gesture | target | result |
|---|---|---|
| click | the 40-DIP disc | `_h.Play(originalIndex)` → `svc.Player.PlayAsync(m.ContextUri, index)` — the SHOW context resolves the episode, so the show's own ordering is what plays regardless of the current filter/sort view (`EpisodeList.cs:87`, `DetailShell.cs:326`) |
| click | anywhere else on the card | **nothing.** The card has no `OnClick`; only the disc is interactive (`EpisodeList.cs:202-232`) |
| double-click / right-click | the card | **nothing** — no context menu on an episode row in 0.2.9 |
| click | a status pill | `_status.Value = index`; filter predicate `1 ⇒ Pct ≤ 0.01`, `2 ⇒ InProgress`, `3 ⇒ Played`, else all (`EpisodeList.cs:56`) |
| click | Newest / Oldest | `_order.Value`; `1` reverses the ORIGINAL-index view list (`EpisodeList.cs:59`) |
| click | **Resume** | plays the banner's episode by its original index (`EpisodeList.cs:81`) |
| click | **Load more episodes** | `Page(svc, showUri, from, post)` — fire-and-forget `svc.Library.LoadMoreEpisodesAsync(showUri, from)`; the rows land in the store and re-map the model; only the re-entrancy flag is posted back. A failed page keeps the affordance so the next tap retries (`EpisodeList.cs:107-123`) |
| click while paging | the pill | a no-op lambda; the label reads `podcast.loadingMore` (`EpisodeList.cs:132-133`) |
| keyboard | the disc | `Focusable` + `Space`/`Enter` from the box's own click mechanics; the CARD is not focusable |
| tooltip | — | none anywhere in the episode list |
| accessibility names | — | none authored: the disc carries no `Role`/tooltip. **This is a gap, not a design** — see §9 |
| drag | the rail COVER | `WaveeDetailDrag.Hero(m, acts)` — the show entity as a resource payload (`DetailRail.cs:152`) |
| drop | anywhere on a show page | **refused transparently.** `PageDropTarget` returns null for `Content != Tracks` (`DetailShell.cs:708`), and the track list's own body target treats an Album/Show page as `foreignSurface` (`DetailShell.cs:715`) |
| selection (click-drag marquee, Ctrl/Shift-click, Select all) | the episode column | **nothing** — `DetailConfig.Show` is `Selection: ItemsSelectionMode.None` (`DetailConfig.cs:227`), so a show page has no selection model and no batch bar |
| click | the COLLAPSED rail's cover *or* chevron | both expand (`DetailRail.cs:311-317, 330-338`) |
| settings | "Hero page layout" (`WaveeSettings.DetailPageLayout = PageHero`) | **ignored on a show** — the force-vertical arm is gated on `_cfg.Content == Tracks` (`DetailShell.cs:509-511`). A show reaches the vertical arm only by WIDTH |
| settings | "Keep left-rail same size" | `DetailRailPolicy.ScopeFor(RailScope.Show, uniform)` → `RailScope.Uniform`: the show then shares ONE width/collapse pair with every other detail surface (`DetailRailPolicy.cs:48-52`; `DetailShell.cs:623`) |

### 6.2 Module page (entity template)

| gesture | target | result |
|---|---|---|
| click | an action button | `KindPlay` → `PlayModule(moduleId, playableId, hero.Title, hero.ImageUrl, …)`: the form comes from `ModulePlayables.Get(...)?.Form ?? SdkForm.Audio`, never guessed from the page (`ModulePage.cs:446-454`). `KindOpenUrl` → `ShellOpen.OpenUrl` behind the http(s) guard. `KindModuleAction` → fire-and-forget `module/action` (`ModulePage.cs:750-776`). Unknown kind ⇒ the button is **absent** |
| click | the hero subtitle | navigates to `ModulePages.RouteFor(now, LinkSlot.Artist)` — and **only** when the playable whose own page this IS is the item in the bar (`ModulePage.cs:359-377`). Otherwise it is plain text with no cursor, no focus, `AutomationRole.Text` |
| click | a `cards` cell | route → `go(route, item.Title)`; else an http(s) `Url` → browser; else play. Nothing invents a destination (`ModulePage.cs:593-602`) |
| click / hover-FAB | a `cards` cell's play FAB | `PlayCard(item)` → `PlayModule` |
| click | a `playables` row | `TrackRow.Invoke(bridge, track, …)`: if this track is already the item in the bar it toggles play/pause; otherwise `VideoActions.PlayAs(..., PlayLinkActions.FormFor(item.Form ?? Audio))` (`ModulePage.cs:552-554`; `TrackRow.cs:184-192`) |
| right-click | a `playables` row | `Menus.ModuleTrack` — W16. Attached ONLY when `acts` **and** `overlay` resolve; otherwise the row has no menu at all (`ModulePage.cs:568-572`) |
| click | a `playables` row that is already the item in the bar | `TrackRow.Invoke` toggles play/pause, and the row wears the now-playing re-skin from `TrackRow.StateOf` (`ModulePage.cs:550`) |
| hover | a `cards` cell | the 44-DIP play FAB fades in over the cover; a cell whose `cardUri` is playing shows the now-playing overlay instead (`MediaCard.cs:35-40`) |
| click | a `links` row | `ShellOpen.OpenUrl(url)`; `Role = Hyperlink`, `Focusable`, `Cursor = Hand`; a non-http(s) url is **not drawn at all** (`ModulePage.cs:624-664`) |
| click | **Retry** on the failed state | `reload.Value++` re-fires the resource (`ModulePage.cs:155`) |
| click | **Open in browser** on the failed state | the first `openUrl` action on the CACHED document (`ModulePage.cs:257-268, 272-280`) |
| scroll | the page | `ScrollKey = "module-page:" + pageUri` restores the offset per entity (`ModulePage.cs:178`) |

### 6.3 Watch page

| gesture | target | result |
|---|---|---|
| click | the idle 64-disc | `StagePlay` — the document's FIRST `play` action → `PlayWatch`, which plays **as video unconditionally** (`SdkForm.Video` + `VideoActions.PlayAs`), because on a cold watch page the resolve-cache fallback to Audio would start audio and the stage would never light (`ModulePage.cs:223-251`) |
| tooltip | the idle disc | `ToolTip.Wrap(…, loc detail.play)` — the only tooltip on this surface, and the control's accessible name (`WatchPageView.cs:161-162`) |
| click | a `play` chip | same `PlayWatch` (`ModulePage.cs:206-219`) |
| click | an `openUrl` chip | browser, behind `ShellOpen.IsWebUrl` |
| click | a `moduleAction` chip | `ModuleActions.Invoke(moduleId, actionId)` |
| — | the play chip while this entity is playing | replaced by the inert **Playing** badge; it is not a disabled button (`WatchPageView.cs:268-271`) |
| click | the "…" chip | `ClickRequestsContext = true` re-enters the engine's context funnel to find the attached `Menus.ModuleTrack` (`WatchPageView.cs:290-293`; `WaveeCta.cs:120, 151`) |
| — | the "…" chip's existence | gated on a `play` chip having carried a `PlayableId` **and** `acts`/`overlay` being non-null (`WatchPageView.cs:286`): a document with no play action has no overflow menu. The synthetic track it targets is always built `SdkForm.Video` (`:288`) |
| — | the idle 64-disc's existence | gated on `StagePlay != null` — no honourable `play` action ⇒ no disc, just the poster (W19, `ModulePage.cs:223-233`, `WatchPageView.cs:107`) |
| click | the idle disc **while this entity is already playing elsewhere** (fullscreen / pop-out / mini player) | reachable, and it re-issues `PlayWatch` — see W18. 0.2.9 has no guard |
| hover | a shelf cell | the 44-DIP play FAB fades in over the 16:9 thumb; the playing cell takes the now-playing overlay (`MediaCard.cs:908`) |
| click | the channel row | `go(route, name)` when `ChannelEntityId` resolves; otherwise `Role = Text`, no cursor, no focus, no hover fill (`WatchPageView.cs:226-248`) |
| click | "… More" / "Less" in the description | expands / collapses the rich paragraph in place (`RichText.ExpandableFlex`, 3 lines) |
| click | a shelf cell | `EntityId` route, else play as video (`WatchPageView.cs:361-379`) |
| hover | the live stage | `DockedVideoSurface`'s own 30-DIP top strip fades in (pop out · fullscreen · turn off) — specified in `24-video-surfaces.md` |
| — | the right rail | while the stage hosts, the rail shows the QUEUE instead of its video card; nothing writes state to make that happen — it is `ShouldMount` evaluated at render (`DockedVideoHosting.cs:70-73, 137-147`) |

### 6.4 The play verbs the module surfaces share with the rest of the app

`VideoActions.PlayAs(player, bridge, track, form)` is the ONE ordered verb: it lights the video surface for this uri
*before* the play command when the form is video, and leaves the standing intent alone when it is audio.
On THIS chapter's surfaces it has FOUR call sites — the watch page's play verb (`ModulePage.cs:250`, form forced to
Video), the entity layout's play action and card FAB (`ModulePage.cs:453`, form from the resolve cache), a `playables`
row (`ModulePage.cs:553`), and a watch-shelf cell (`WatchPageView.cs:370`, Video) — plus a dropped/picked local video
file (`LocalFileActions.cs:109`). Outside the chapter it is also the track table's video lane
(`DetailTracks.cs:3306, 3308`) and the play-link dialog (`PlayLinkDialog.cs:76, 173`). It is **not** a two-caller
verb. Every one of them is an explicit "show me this" gesture, scoped to ONE play through
`PlaybackBridge.PrimeVideoIntentFor`, dying at the next track boundary.

`PlayableLinks.RouteFor(track, slot)` is the ONE table both the player bar and the stage ask where a now-playing span
goes: a module track answers from `ModulePages` (title/art → the playable's own page, subtitle → the publisher entity);
everything else keeps `album:` / `artist:`; a module playable **never** falls through to the Spotify arms
(`PlayableLinks.cs:44-58`). `LabelFor` supplies the route Arg the tab strip and breadcrumb show (`PlayableLinks.cs:64-76`).

### 6.5 Local file play — the other half of `VideoActions.PlayAs` on this surface

`Actions/LocalFileActions.cs` (115) is listed as one of this chapter's sources because it is the **only other**
"start this exact thing as video" gesture in the app, and it has visible states of its own:

| gesture | result | source |
|---|---|---|
| the profile menu's **Play file…** | a modal `FilePicker.OpenFile` titled `localFile.pickTitle` with the `localFile.filter` mask; cancel = nothing; a picker EXCEPTION becomes an Error toast carrying `ex.Message` | `LocalFileActions.cs:36-52` |
| drop a file **on a track row** | never reaches here — the engine gives a drop to the DEEPEST accepting target, so a row drop ATTACHES the .mp4 to that playable (P3) instead of playing it | `LocalFileActions.cs:54-57` |
| drop a file anywhere else on the window | `ClassifyDrop` → play audio, play video, or **`None` ⇒ an Error toast, `localFile.rejected`** | `LocalFileActions.cs:59-66` |
| any of the above on a build with no audio host | the affordances are **HIDDEN, never disabled** (`CanPlayFiles`); a drop that still lands gets the Informational toast `localFile.notReady` | `LocalFileActions.cs:31-32, 67-75` |
| a dropped/picked **video** with no curation service | Error toast `localFile.rejected`; nothing plays | `LocalFileActions.cs:91-96` |
| a dropped/picked **video**, happy path | the file SELF-ATTACHES as its own playable's override through `VideoActions.Apply` (validation + persistence + the undo toast all live there), and only then `VideoActions.PlayAs(..., MediaForm.Video)` | `LocalFileActions.cs:98-109` |

Note the form argument: this call site passes `Wavee.Core.MediaForm.Video` directly, where every module call site goes
through `PlayLinkActions.FormFor(SdkForm…)`. Two `MediaForm` enums, one verb — the `using SdkForm =` alias at the top of
both module files exists for exactly this reason.

Copy, verbatim (`assets/loc/en-US.json`): `localFile.playFile` = "Play file…" · `localFile.pickTitle` =
"Choose a file to play" · `localFile.filter` = "Audio and video" · `localFile.rejected` = "That file can’t be
played. Wavee plays .mp3, .ogg, .flac and .mp4 files." · `localFile.notReady` = "Local playback isn’t ready yet."
· `localFile.dropHint` = "Drop a file to play it" (the shell drop overlay’s label — `19-shell-overlays.md`).

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate (skeleton until true) |
|---|---|---|---|
| rail cover | `DetailModel.Cover` ← `Show.Cover` | `Show.ImageId` (`StringId` column) | `show.Knows(ShowFields.Identity)` |
| rail eyebrow "Podcast" | `DetailModel.BadgeType` = `Loc.Get(podcast.show)` | a constant on `Show.UI.cs` — it is not data | always |
| rail title | `Show.Name` | `Show.TitleId` | `show.Knows(ShowFields.Identity)` |
| rail description | `Show.Description` | `Show.DescriptionId` | `show.Knows(ShowFields.About)` — the row FADES IN late (`LateRow`), it must not hold the page |
| publisher / "N episodes" line | `DetailModel.MetaLine` — **built but never rendered** (§1.1) | `Show.PublisherId` + `Edges.ShowEpisodes.Total[slot]` | see §9: this is a 0.2.9 defect to fix, not port |
| Play CTA | `h.PlayAll` → `PlayAsync(showUri, 0)` | `Playback.Post(new(InputKind.Play, show, cursor 0))` | `show.Knows(ShowFields.Identity)` |
| Follow heart | `SaveButton(m.ContextUri)` | `User.Me.FollowsShow(show)` ← `Edges.SavedShows.Contains(meSlot, showSlot)` | the edge's `State != 0` |
| episode art | `Episode.Image` | `Episode.ImageId` | `ep.Knows(EpisodeFields.Identity)` |
| episode title | `Episode.Title` | `Episode.TitleId` | `ep.Knows(EpisodeFields.Identity)` |
| episode description | `Episode.Description` | **`Episode.DescriptionId` — NOT IN THE PLAN** (see gaps) | `ep.Knows(EpisodeFields.About)` |
| episode date | `Episode.PublishedAt` | **`Episode.PublishedAt` (int, unix seconds) — NOT IN THE PLAN** | `ep.Knows(EpisodeFields.Identity)` |
| episode duration | `Episode.DurationMs` | **`Episode.DurationMs` (int) — NOT IN THE PLAN** | `ep.Knows(EpisodeFields.Identity)` |
| progress rule, "In progress", status filter, Listen-next | `Episode.ProgressMs` | **NOT IN THE PLAN AT ALL** — and it is per-USER state, not catalogue state | `ep.Knows(EpisodeFields.Progress)`; an episode whose progress is unknown must render as *unplayed*, never as a half-drawn bar |
| the episode SET + its order | `Show.Episodes` (resident join) | `Edges.ShowEpisodes.Targets(showSlot)` | `Edges.ShowEpisodes.State[slot] != 0` **and** every returned slot `Knows(EpisodeFields.Row)` |
| "N episodes" total | `Show.TotalEpisodes` | `Edges.ShowEpisodes.Total[slot]` (§4.3) | `State != 0` |
| Load-more gate | `Show.PagedThrough < TotalEpisodes` | **`Edges.ShowEpisodes` has no `Asked` cursor — NOT IN THE PLAN** (see gaps) | `State == partial` **and** `Asked < Total` |
| module page document | `UseResource(host.PageAsync)` → `Loadable<ModulePageDoc>` | **unchanged**: stays a `Loadable`, NOT a table row | `loadable.State == Ready` |
| module page cached seed | `ModulePages.Get(pageUri)` | a small `Dictionary<string, ModulePageDoc>` in `Platform/Modules.Host.cs`, `ExpiresAtUnixMs`-bounded (default 10 min) | — |
| watch stage's "is this playing" | `bridge.CurrentTrack.Value?.Uri` vs `ModuleUri.Encode(moduleId, playAction.PlayableId)` | `Playback.Current.Value` handle's `Uri` compared ordinal against the same encode | both non-empty and ordinal-equal (`DockedVideoHosting.cs:91-94`) |
| the stage claim | `ShellUi.ActiveStagePlayable` written from an effect by the ATTACHED page only | same signal on `Shell.cs`; the writer is still `Modules.Page`'s `UseSignalEffect` gated on `UseIsActive` | — |
| whether the stage MOUNTS the video | `bridge.VideoPlacementNow()` — the RESOLVED placement | `Playback.VideoPlacementNow()`, unchanged | `resolved == SurfacePlacement.Docked` **and** the claim matches. Not Docked ⇒ poster + disc (W18), never a black box |
| the watch shelf cell's live flag | `PageItem.IsLive` → `WatchItem.IsLive` | carried, **not drawn** (`MediaCard.VideoCard` has no live arm) | — see §9.4 |
| a module playable's now-playing state | `TrackRow.StateOf(bridge, lib, track)` / `LazyNowPlayingOverlay` keyed by `cardUri` | `Playback.Current` handle compared against `ModuleUri.Encode(...)` | both non-empty and ordinal-equal |

**Pages demand their whole model on mount.** In 0.3 `Show.Page` runs one `UseEffect`:
`Entities.Ensure(show, ShowFields.All)`, `Entities.EnsureEdges(show, EdgeKind.ShowEpisodes)`,
`Entities.EnsureRows(show.EpisodeSlots, EpisodeFields.Row)` — one batch, never a visible-window ask. The load-more pill
stays, because a 700-episode show's membership genuinely arrives in pages: the pill asks for the NEXT page of the edge,
not for a viewport.

**Derived facts live on the model.** `InProgress` / `Played` / `Pct` are computed from `ProgressMs` and `DurationMs`.
In 0.3 they are a pure `Episode.Rules` section (§8), computed at read time from two slab columns — never probed from the
UI, never cached in a second column.

### DATA GAPS

| element it draws | 0.2.9 source | what the plan's model holds | proposed column / edge |
|---|---|---|---|
| episode resume position (progress rule, "In progress", the 4-way status filter, the Listen-next pick) | `Episode.ProgressMs` ← `Wavee.Core/Domain/Models.cs:528-532`; the wire is the episode's own playback-state trait | nothing | `EpisodeTable.ProgressMs : Column<int>` + `EpisodeFields.Progress`, with its OWN `Authority` (it is user state; a catalogue write must never clobber a local one). If it is modelled as library state instead: `Edges.EpisodeProgress : EdgeTable<ProgressEdge(int PositionMs, int At)>` parented on `User.Me` |
| episode published date | `Episode.PublishedAt` (`DateTimeOffset`) | nothing | `EpisodeTable.PublishedAt : Column<int>` (unix seconds, app epoch like `FetchedAt`) |
| episode duration | `Episode.DurationMs` | nothing | `EpisodeTable.DurationMs : Column<int>` |
| episode description (the two-line clamp) | `Episode.Description` | nothing | `EpisodeTable.Description : Column<StringId>` + `EpisodeFields.About` |
| episode art | `Episode.Image` | nothing | `EpisodeTable.Image : Column<StringId>` |
| episode → show back-link (`EpisodeAsTrack`'s album slot, and the "Go to podcast" menu row) | `Episode.ShowUri` / `ShowName` | nothing | `EpisodeTable.Show : Column<int>` (show slot) |
| show publisher + description + cover | `Show.Publisher` / `Description` / `Cover` | nothing | `ShowTable.Publisher, Description, Image : Column<StringId>` |
| **the paging CURSOR** | `Show.PagedThrough` (`Models.cs:545-547`); `StoreLibrarySource` stamps it, `LoadMoreEpisodesAsync` advances it even when the page landed zero rows | `EdgeTable<T>.Length` + `.Total` + `.State` (`plan §4.3`) — i.e. resident-vs-total | **`EdgeTable<T>.Asked : Column<int>`**, advanced by `ReplacePage` whether or not rows came back; load-more gates on `Asked < Total`. Without it the 0.2.9 bug returns verbatim: a withdrawn / region-locked member keeps `Length` permanently short, the pill never disappears and every tap re-asks the same block (`EpisodeList.cs:65-72`) |
| cover-derived page tone + the rail's graded accent | `SpotifyLive.CoverColorPlane` (image-keyed, async, watched per tile) | nothing | owned by `00-design-system.md` / `03-detail-frame.md` — this chapter only needs `Surfaces.PlaceholderFor` to keep its `Watch`-bound repaint so a 56-DIP episode tile still tints when the grading lands |
| module page document | a pipe answer (`ModulePageDoc`), cached in `ModulePages` until `ExpiresAtUnixMs` | nothing (correctly) | **keep it out of the entity tables.** Its readiness is a `Loadable`, its identity is a route, and forcing it into a slab would buy nothing and cost the `Skel.Region` seed |
| module playable → form / page-entity / subtitle-entity | `ModulePlayables` / `ResolvedPlayable.PageEntityId` / `SubtitleEntityId` | nothing | keep the two small sync caches in `Platform/Modules.Host.cs`; `PlayableLinks` (§8) reads them |
| the stage arbitration signal | `ShellUi.ActiveStagePlayable` (`Signal<string>`) | not mentioned | one `Signal<string>` on `Shell.cs`; the ONE writer stays the attached module page |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | what it decides | tests | 0.3 destination (CORE section) |
|---|---|---|---|---|
| `WatchPageModel` (+ `WatchChip`, `WatchItem`, `WatchStageKind`) | `App/WatchPageModel.cs` (292) | every watch-layout DECISION: the template gate, the channel identity (hero-first then the legacy one-card shelf), the dissolved fact line (`" · "`-joined VALUES only), which section becomes the description, playables-outrank-cards for the shelf, the `Trimmed` whitespace rule, and `StagePlayableIdOf` (the ONE id space) | `src/apps/Wavee.Tests/Modules/WatchPageModelTests.cs` (379 lines, 24 facts) | `Platform/Modules.cs` — **CORE, `System` + `Wavee.Sdk` only, no FluentGpu type** (that is what lets it compile into the test assembly) |
| `DockedVideoHosting` (+ `DockedVideoFace`, `DockedVideoHost`) | `App/DockedVideoHosting.cs` (168) | `PageStageHosts`, `HostFor`, `HostOf`, `ShouldMount` (the ≤1-surface invariant), `DockedHostAvailable` (the OR that keeps a narrow watch page's stage) | `src/apps/Wavee.Tests/DockedVideoHostingTests.cs` (298 lines, incl. a property test over arbitrary histories) | `Playback/Playback.Video.cs` CORE section (or `Shell/Shell.cs` CORE if placement lives there) — it is `System`-only today and must stay so |
| `DetailRevealRamp` | `Features/Detail/DetailRevealRamp.cs` (27) | `Chunk = 12`, `Cap = 60`, `Done`, `Next`, `Revealed` — the progressive row reveal the module page's `playables` block uses | `src/apps/Wavee.Tests/DetailRevealRampTests.cs` (54) | `Entities/Track.UI.cs` CORE section (shared with `04-detail-track-table.md`) |
| `DetailLayoutBreakpoints` | `Features/Detail/DetailLayoutBreakpoints.cs` (88) | `NominalModeFor` / `ModeFor` (820 / 660 / 560, hysteresis 24 on the WIDENING crossing, Vertical 540↔580, a nominal Vertical while two-column clamps to 2), `ContentMinWidthForMode`, `EstimatePageWidthFromViewport` | `src/apps/Wavee.Tests/DetailLayoutBreakpointTests.cs` (note the singular) | `03-detail-frame.md`'s destination — the show page only CONSUMES it |
| `DetailRailPolicy` (+ `RailScope`) | `Features/Detail/DetailRailPolicy.cs` (67) | `RailScope.Show` is its own persisted width/collapse pair; `DefaultWidthFor(Show) = 280`; `MinWidth 180` / `MaxWidth 480`; `ResizableFor` is mode 0 only | `src/apps/Wavee.Tests/DetailRailPolicyTests.cs` (113) | `03-detail-frame.md`'s destination; `Show.UI.cs` supplies the scope |
| `ModulePageBudget` | `src/apps/Wavee.Sdk/ModulePage.cs:250-312` | 40 sections / 500 items / 64 KiB per section / 2 MiB per document, enforced by REJECTING, on both sides of the wire | `src/apps/Wavee.Tests/ModulePageGateTests.cs` (123, incl. the loc-key parity check) | **unchanged** — `Wavee.Sdk` is out of scope (plan §1). The app still re-applies the caps at draw time (`ModulePage.cs:299-307`) |
| `PlayableLinks` | `Actions/PlayableLinks.cs` (82) | which route a now-playing identity SLOT navigates to, and its display label; `IsModule` | `src/apps/Wavee.Tests/` — via the module-page/link tests; port as its own file | `Platform/Modules.cs` CORE (it is `Wavee.Core` + `Wavee.Sdk` today, no engine type) |
| `ShellOpen.IsWebUrl` | `Actions/ShellOpen.cs` | the http(s)-only whitelist every module url passes (a page crosses a pipe as DATA) | `ModulePageGateTests.cs:24-45` (driven, not scanned) | `Platform/Platform.cs` CORE |
| `ModulePage.OpenUrlOf` | `ModulePage.cs:272-280` | the FIRST `openUrl` action on a document whose url survives the web guard — the failed state's escape hatch **and** the "Open on &lt;Module&gt;" menu row (`Menus.cs:153-161`). Pure over `ModulePageDoc`; today it is a static on a `Component`, i.e. untestable without the engine | none today — **write them** | `Platform/Modules.cs` CORE, beside `WatchPageModel` |
| `ModulePages` route grammar (`TryParseRoute`, `UriOf`, `RouteForEntity`, `RouteFor`, `RoutePrefix`) + `ModuleUri.Encode/TryDecode` | `Backend/Modules/ModulePages.cs`, `Wavee.Sdk/ModuleUri.cs` | the `module:wavee:module:<id>:<b64>` route ↔ uri mapping every surface in this chapter parses, plus the entity-id-vs-playable-id split | `src/apps/Wavee.Tests/Modules/ModulePagesTests.cs` | `Platform/Modules.cs` CORE (route grammar) + `Wavee.Sdk` (unchanged) |
| `ModulePages` page CACHE ttl | `Backend/Modules/ModulePages.cs:25, 46, 52-56` | the module's own `ExpiresAtUnixMs`, else `DefaultTtlMs` = 10 min; an expired entry reads as a MISS, which is what turns a revisit back into a figure-space skeleton | via `ModulePagesTests` | `Platform/Modules.Host.cs` |
| **NEW — `Episode.Rules`** | *extracted from* `EpisodeList.cs:40-42, 56, 59, 78-81, 65-72` | `Pct(progressMs, durationMs)`, `InProgress`, `Played`, `Unplayed`, the 4-way status predicate, the Newest/Oldest order, the resume PICK (max `Pct` among in-progress over ALL episodes), and the load-more gate `max(edgeAsked, localAsked) < total` | none today — **write them**: `src/apps/Wavee.Tests/EpisodeRulesTests.cs` | `Entities/Episode.cs` CORE section |

The last row is the one genuinely new pure class this chapter asks for. In 0.2.9 those five decisions are private
statics inside a `Component`, which is exactly the shape this repo's no-source-text-test rule refuses; extracting them
is a port, not a redesign — the thresholds `> 0.01` / `< 0.98` / `>= 0.98` and the `max(model, local)` cursor fold are
already load-bearing and already have comments explaining why.

Show paging end-to-end is pinned by `src/apps/Wavee.Tests/ShowEpisodePagingTests.cs` (246 lines) — including
`LoadMoreEpisodes_AdvancesPastMembersThatCannotHydrate`, which is the test that proves the cursor, not the resident
count, is the gate. That test survives the rewrite against `Edges.ShowEpisodes` + the proposed `Asked` column.

---

## 9. Re-author notes

### 9.1 Must not be simplified

- **The episode card's two clamped paragraphs.** Dropping the description, or clamping the title to one line, turns the
  show page into a track table with bigger art. This is the single most visible thing about the surface.
- **The always-visible accent disc.** A hover-revealed play (as a track row has) makes a 112-DIP card look empty.
- **The three separate progress reads.** The rule (3 DIP), the "In progress" caption, and the status filter are three
  presentations of the same number and must stay in agreement: they all come from `Pct`, never from three predicates.
- **The resume banner's `Shrink = 0` pill.** Without it the Resume capsule is the child that collapses when the title is
  long (`EpisodeList.cs:257-259` says so explicitly).
- **The paging cursor.** See §7's DATA GAPS. `Length < Total` is the bug, not the fix.
- **The module page's ONE container shape.** The stage slot is an EMPTY box for a non-watch document
  (`ModulePage.cs:175-176`) so the scroller is never re-parented and a template flip never remounts the page.
- **Everything the stage is forbidden from having.** No `Enter`, `Exit`, `Layout`, `Opacity`, `Stagger`; outside the
  scroller, outside `Skel.Region`, outside `Section()`. `AspectRatio` on a COLUMN with the ZStack as its `Grow = 1`
  child — never `AspectRatio` on the ZStack itself. All three hazards are written out at `WatchPageView.cs:31-46`.
- **`Flow.Show`, not a C# `if`, for the stage's two layers.** `Flow.KeepAlive` exit-freezes the outgoing page in the
  same reconcile pass as the route change, and `RunComponent` skips a frozen/parked component — so a branch taken inside
  `Render()` can never be re-evaluated to hand the surface back, and the video stays parented to a dead page
  (tripping `OneSurfacePerPlayerGuard`). `Flow.Show`'s predicate runs from an effect bound to the NODE
  (`WatchPageView.cs:109-114`).
- **The `UseIsActive` gate + reading everything INSIDE the effect closure.** `UseSignalEffect` registers once at mount,
  so a captured local is frozen at the FIRST render's value — and the first render happens while the document is still
  the loading Seed, which names no play action. Capturing it pinned the claim to `""` forever and the stage never lit
  (`ModulePage.cs:120-145`). This paragraph is a scar; port it with its comment.
- **Peek vs Value, deliberately opposite in two places.** `SubtitleRoute` PEEKS `CurrentTrack` (a one-shot link frozen
  into the mounted span — subscribing would re-render the whole page on every track change, `ModulePage.cs:367-377`),
  while `Render` READS `bridge.CurrentTrack.Value` (the stage is derived from what is playing, so peeking would light
  the picture one navigation late, `ModulePage.cs:108-113`).
- **Unknown is skipped, never fatal** — for a section kind (`ModulePage.cs:482-484`), an action kind
  (`ModulePage.cs:441-443`, `WatchPageModel.cs:22-24`) and a chip kind (`ModulePage.cs:218`).

### 9.2 Traps

- **Props freeze at mount.** `ModulePage`'s `_route` is correct to freeze because ContentHost keys the page by the whole
  route (`ModulePage.cs:56-58`). `EpisodeList`'s `_full` is a `Loadable`, i.e. a signal container, which is why it can
  be a frozen ctor arg and still track the model. In 0.3, `Show.Page` freezes a `Show` HANDLE (a slot index) — also
  correct, because a slot is stable and the data behind it is read through the table's `Changed` signal. Never freeze a
  *value* read off a handle.
- **`Key` remounts you need:** the `"episodes:" + route` key on the right column and the `"module-page:" + route` key on
  the page. Both exist to stop a reused slot from reconciling two structurally different trees. Reproduce them.
- **`ReuseGuard`.** The `playables` block's `RampedRows` is an `Embed.Comp` keyed `key + ":rows"`; without the key a
  document refresh that changes a section's kind would reuse the component against a different element array. Keep every
  key in §1.4's table.
- **Zero-allocation scroll frames vs per-row richness.** 0.2.9 reconciles the two by *not virtualizing the episode
  list at all*: `EpisodeList` builds a `List<Element>` of every filtered row and hands it to a plain `ScrollView`
  (`EpisodeList.cs:74-101`). That is affordable because a show opens with ≤300 resident episodes and each row is ~10
  nodes. The module page does the same and is bounded by construction (`ModulePageBudget`), with `RampedRows` spreading
  the MOUNT of a long `playables` block over frames rather than virtualizing it (`ModulePage.cs:580-581, 668-700`).
  **In 0.3 the episode list must become a bound list** (`ItemsView.CreateBound` over the `ShowEpisodes` edge, rows
  re-bound by slot version) to satisfy Wave 5's "no allocation on a scroll frame" gate — but the ROW must stay the
  card in W7, not shrink to a table row to make binding easier. `Episode.Row` therefore needs both shapes: a static
  `Element Row(Episode, Action)` for the resume banner and the eager path, and
  `Element Row(in BoundItemScope<Episode>, EpisodeRowStyle)` for the list, exactly as plan §4.12 shapes `Track.Row`.
- **`right:eps` has no `MinHeight = 0f`, and `EpisodeList`'s `ScrollView` has no `ScrollKey`.** The tracks arm carries
  both (`DetailShell.cs:533`, `TrackList`'s own scroll identity); the episodes arm carries neither
  (`DetailShell.cs:527`, `EpisodeList.cs:101`). In 0.2.9 that is survivable because the column is a plain flex child
  inside a definite-height row and keep-alive parks the whole page with its offsets intact — but the moment 0.3 turns
  the list into an `ItemsView.CreateBound` virtualizer, a missing `MinHeight = 0` is a scroller that grows its parent
  instead of scrolling, and a missing scroll key is a lost position on every album↔show swap through the reused slot.
  Give `Show.Page`'s right column both.
- **The 300-DIP guard.** The right column carries `MinWidth = ContentMinWidthForMode(mode)` — 300 in two-column /
  transient modes, 0 in Vertical (`DetailShell.cs:519-534`). Dropping it lets a mid-spring frame clip the episode cards.

### 9.3 Where the plan is wrong or too thin for this surface

1. **§4.3's `EdgeTable` cannot express the show paging cursor.** It has `Length`, `Total` and
   `State ∈ {unknown, partial, complete}`, and the doc says "state=partial until `Length == Total`". That is precisely
   the resident-vs-total test 0.2.9 removed. Add `Column<int> Asked` and make `ReplacePage` advance it by the REQUESTED
   span, not the returned one. Without this fix the "Load more episodes" pill becomes permanently stuck on any show
   with a withdrawn or region-locked member.
2. **§4.12's `RowStyle` has no episode shape.** `ShowNumber` / `ShowAddedAt` / `NumberOf` / `AddedAtOf` describe a track
   table. An episode row needs `PublishedAt`, `DurationMs`, `ProgressMs`, a two-line description and no number column
   at all. Either widen the style record or — better — let `Episode.UI.cs` own its own row entirely, since it shares no
   column set with `Track.Row`.
3. **§4.13's page example demands `AlbumFields.All` + one edge + one row batch.** A show page needs **three** demands
   (`ShowFields.All`, `EdgeKind.ShowEpisodes`, `EpisodeFields.Row` over the returned slots) *plus* a per-user progress
   demand that the plan's model has nowhere to put (§7 gaps). Say so in `Show.Page.cs`'s header.
4. **§4.14's library edges list `SavedShows`** — good — but nothing anywhere carries an episode's resume position, which
   is the single most load-bearing per-user fact on this surface.
5. **§2's `Platform/` budget is far too thin for modules.** Seven files at ~8,600 lines total must hold
   `Platform.cs`, `Platform.Host.cs`, `Design.cs`, `Controls.cs` **and** `Modules.cs` / `Modules.UI.cs` /
   `Modules.Host.cs`. The module SURFACE alone is 1,470 lines today (`ModulePage` 776 + `WatchPageView` 402 +
   `WatchPageModel` 292) before `ModuleHost`, `ModuleProcess`, `ModuleCatalog`, `ModulePages`, `ModulePlayables`,
   `ModuleProjectionRelay`, the secret store and the protocol glue — which are several thousand more.
   `Modules.UI.cs` needs its own budget line of ~1,500 and `Modules.Host.cs` ~2,500.
6. **§2 gives no `Episode.Page.cs`, and that is CORRECT** — 0.2.9 has no episode page on purpose:
   `RichText.RouteForUri` routes a SHOW and explicitly refuses an EPISODE, "because a link to one would navigate to a
   route nothing renders" (`RichText.cs:88-101`). Do not invent one in 0.3.
7. **Wave 5 gives owner M `Show.UI.cs` + `Show.Page.cs` + `Episode.UI.cs` while Wave 6 gives owner T `Modules.*`.**
   That split is right, but they share exactly one thing: `TrackRow.ArtCard(kind: Rail)` and `Menus.ModuleTrack`. M must
   land `Track.UI.cs`'s art-card row in Wave 5 with the `ColumnSet(Album:false, By:false, Date:false, Video:true,
   Plays:false, Heart:false, Thumb:false)` configuration reachable, or T has no row to draw a `playables` section with.
8. **`--fake` cannot open a module page at all.** `Services.CreateFake` never calls `ModuleHost.Attach`
   (`Services.cs:584-635` vs `:741`), so `ModuleHost.Current` is null and the page resolves to
   `ModuleException(Unsupported, "no module host")` → W12. Wave 5's gate ("`--fake` opens every route in the nav probe
   list") therefore cannot cover `module:` routes; Wave 6 owner T must verify them against a real build with the YouTube
   module installed (modules boot PRE-login, so no sign-in is needed).

### 9.4 Defects in 0.2.9 to carry as decisions, not as accidents

- **The show's meta line is computed and never drawn.** `MapShow` builds `"<Publisher> · N episodes"`
  (`DetailPage.cs:456`), but `DetailRail` renders the meta row only when `Badges != TypeYear` and Show is `TypeYear`
  (`DetailRail.cs:199-200`); the vertical header has the same gate (`DetailRail.cs:406-408`). So neither layout ever
  shows the publisher or the episode count. **Recommendation: draw it in 0.3** (a show is not an album; it has no facts
  bento to duplicate). Flagged here so the re-author does not "faithfully" reproduce a hole.
- **The share button copies a PLAYLIST url for a show.** `PlaylistShareButton.Share` falls back to
  `DetailPage.SpotifyPlaylistWebUrl(uri)` → `https://open.spotify.com/playlist/spotify:show:…`
  (`PlaylistInlineEdit.cs:801-804`, `DetailPage.cs:746-747`). Fix in 0.3 via `SpotifyLink.WebUrl(uri)` without the
  playlist fallback.
- **`"N min"` is a hardcoded English suffix.** `EpisodeList.cs:178, 195` concatenate `" min"` while the loc catalogue
  already carries `podcast.minutes = "{n} min"`. Use the key. Likewise `"MMM d"` uses the ambient culture with no
  explicit `CultureInfo` (`EpisodeList.cs:178, 193`) — the rest of the app passes
  `CultureInfo.CurrentCulture` explicitly (`DetailConfig.cs:243-244`).
- **`LoadMore`'s margin is `Left 8`, not `Top 8`** (`EpisodeList.cs:129`) — an `Edges4(Left, Top, Right, Bottom)`
  mis-order. It shifts the centred pill 4 DIP right of the column centre and adds no gap above. Fix to
  `new Edges4(0f, Spacing.S, 0f, 0f)`.
- **Type-ramp escapes.** `EpisodeList` authors five raw `TextEl { Size = … }` values (15/700, 14/700, 12, 12/600, 12)
  against `WaveeType`'s "never author a raw `TextEl { Size = … }`" rule and its 400/600-only weight policy
  (`WaveeType.cs:6-19`). **Port the pixels** — the 700 title weight is what makes the card read as a card — but record
  them as two sanctioned aliases in `00-design-system.md` (`EpisodeCardTitle` 14/20/700, `EpisodeBannerTitle` 15/20/700)
  rather than as five anonymous literals. **Both are now carried in `00-design-system.md §12.1`'s "used by a chapter,
  defined nowhere" table**, with the two conditions they have to clear first: 700 would be a FOURTH sanctioned weight
  divergence and needs the same explicit blessing `ArtistDisplay`/`PivotLabel` carry, and **15 px is off the engine
  type ramp entirely** (12 · 14 · 18 · 20 · 28 · 40 · 68) — snap the banner to 14 or record it as a second off-ramp
  rung beside `PivotLabel`'s 19/25.
- **No accessibility names or roles on the episode play disc.** It is a bare `BoxEl` with `OnClick`
  (`EpisodeList.cs:247-253`) — no `Role`, no tooltip, no announced name. The stage disc does it right
  (`ToolTip.Wrap(..., loc detail.play)`, `WatchPageView.cs:143-163`). Give the episode disc the same treatment.
- **A show with ZERO episodes says "No episodes match this filter".** The gate is `view.Count == 0`, not "a filter is
  applied" (`EpisodeList.cs:84-86`), so an empty podcast blames a filter the user never set. 0.3 needs a second key
  (`podcast.empty`) and the filter copy only when `status != 0` or the unfiltered set is non-empty.
- **`WatchItem.IsLive` is projected and never drawn.** `WatchPageModel` carries it (`:41, :279`) but
  `MediaCard.VideoCard` has no live arm, so a live cell in the watch shelf looks like a recorded one — while the SAME
  document's hero gets `LiveBadge`. Either draw it on the cell in 0.3 or stop projecting it.
- **The idle disc comes back while the entity is still playing.** Whenever the placement resolves away from Docked
  (fullscreen, pop-out, mini player) the stage re-mounts the 64-DIP CTA and it is clickable, re-issuing `PlayWatch` on
  something already playing — W18. The caption meanwhile says "Playing". Decide in 0.3: a "return the video here"
  affordance is the honest control for that state.
- **The watch caption has no layout motion.** Expanding "… More" jumps the shelf (`WatchPageView.cs:201-205` — no
  `Enter`, no `Layout`), while the entity layout's `Section()` FLIPs. One of the two is wrong; the module page's is
  the good one.
- **The cold skeleton has no copy bars.** `PendingSeed(Show)` seeds empty strings, not figure spaces, so the derived
  shimmer has no show title, no show description row and no episode title/description bars — see W6's table. Port the
  `ModulePage.Seed` idiom (U+2007 runs) instead of the empty strings; this is the one place the module page is the good
  example and the detail page is the bad one.
- **A cold watch page reflows by up to 560 DIP on Ready.** The seed is `TemplateEntity`, so the stage does not exist
  while the document loads (W20). Either seed a watch-shaped placeholder document for a `watch:`-prefixed route, or
  reserve the 16:9 envelope unconditionally and let it paint the letterbox ground until the document names a poster.
  Do NOT solve it by moving the stage inside `Skel.Region` — that is hazard 1.
- **The 500-item budget is a shared pool.** A module whose first section is a 500-row `facts` block silently drops every
  later section (`ModulePage.cs:299-307`). 0.3 should at minimum log it; a per-section floor would be a behaviour
  change worth raising with the owner.
- **`podcast.episodeCount` exists and is only ever used by the line that is never drawn.** `MapShow` builds
  `"<Publisher> · " + Strings.Podcast.EpisodeCount(total)` (`DetailPage.cs:456`) — a properly pluralised ICU string —
  and `DetailRail` then refuses to render the row. Drawing the meta line in 0.3 (the bullet above) costs no new loc key.
- **The show page has no now-playing treatment.** Nothing in `EpisodeList` reads `PlaybackBridge`, so the episode that
  is currently playing looks identical to every other card — no equalizer, no accent title, no pause glyph. The module
  page's `playables` rows DO get it (`TrackRow.StateOf`, `ModulePage.cs:550`). This is the most defensible "add in 0.3"
  on the surface; raise it with the owner rather than shipping it silently.

### 9.5 Line budget

| | lines |
|---|---|
| 0.2.9, this surface's own code | **~1,790** — `EpisodeList.cs` 260 + `ModulePage.cs` 776 + `WatchPageView.cs` 402 + `WatchPageModel.cs` 292 + ~60 of show branches spread over `DetailConfig`/`DetailPage`/`DetailShell`/`DetailRail`/`DetailRailPolicy` |
| plan §2 target | **1,500** for show + episode (`Show.cs` 150 + `Show.UI.cs` 300 + `Show.Page.cs` 600 + `Episode.cs` 150 + `Episode.UI.cs` 300). `Modules.UI.cs` has **no line of its own** inside `Platform/`'s ~8,600 across seven files |
| honest estimate | **~3,600**: `Show.cs` 180 · `Show.UI.cs` 320 · `Show.Page.cs` 700 · `Episode.cs` 200 (incl. the new `Episode.Rules`) · `Episode.UI.cs` 400 · `Platform/Modules.cs` (the pure `WatchPageModel` + `PlayableLinks`) 400 · `Platform/Modules.UI.cs` 1,400 |

The show/episode overrun (1,800 vs 1,500) is ~20 % and absorbable. The module overrun is not a budget miss so much as a
budget *omission*: the plan simply never allocated the module page any lines.

---

## 10. Parity checklist

Reference build: `C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`.
Show-page items run in `--fake` (`FakeData` seeds 8 shows at `wavee:show:0…7`, each with 8–12 episodes and exactly one
in-progress episode at ⅓ progress — `Wavee.Core/Fakes/FakeData.cs:374-420`; `FakePodcastSource` stamps
`TotalEpisodes == PagedThrough == resident`, so the load-more pill is correctly absent —
`Wavee.Core/Sources/FakePodcastSource.cs:16-21`). Module/watch items require a normal (non-`--fake`) build with the
YouTube module installed — see §9.3 item 8. Routes are reached with
`wavee://open?route=show&arg=wavee:show:3` (fake) / `spotify:show:<id>` (live), and for a watch page via
`wavee://play?link=<youtube url>` then the player bar's art tile.

**Show page — layout**

1. @1280, route `show:wavee:show:3`, static capture: rail is 280 DIP wide, cover 256×256 with an 8-DIP radius and a
   drop shadow; the episode column starts exactly 16 DIP right of the grip.
2. Same capture: the rail shows eyebrow "Podcast", title, Play + heart + share, description — and **no** meta line and
   **no** facts panel between them.
3. Resize to 1000: rail snaps to 224, cover to 200, the grip disappears.
4. Resize to 860: rail 188, cover 164.
5. Resize down through 700 → 560 → 520: the layout flips to the vertical header (140-DIP cover on the left, metadata
   right) at **540**, and back to two-column only at **580** (drag slowly across 540–580 and confirm no flicker).
6. In vertical mode, the status/order selector row is **absent**.
7. @1280, drag the rail grip left past ~136 raw: the rail collapses to a 96-DIP strip with an 80-DIP cover, a 2-line
   12/600 title and a chevron; the tooltip on the strip is the show title.
8. Pull right past 220: the rail re-expands. Relaunch: the width and collapse state persist for SHOWS only (an album
   page still opens at its own width).
9. Deep-link cold (no nav preview) with the network throttled: a full-page shimmer appears with a 256 cover bar, an
   eyebrow bar, title bars, and **eight** episode-shaped placeholders; it cross-dissolves (fade only, no rise).
10. Click a podcast card in the sidebar/Home instead: **no** skeleton — the shell paints immediately from the preview
    and episodes fill in.
11. Scroll the episode column to the bottom: the last card's bottom edge clears the player bar by 24 DIP.

**Show page — episode row**

12. Zoom to 400 % on one unplayed row: 1 px hairline, 8-DIP radius, 12-DIP padding on all four sides, art 56×56 r8,
    gap 16 to the text, gap 16 to the disc.
13. Same row: the title is 14 px bold (visibly heavier than a track-row title), wrapping to at most 2 lines; the
    description is 12 px secondary, at most 2 lines, both ending in "…" when clipped.
14. Hover capture over a row: only the card's fill changes (a faint veil); the card does not scale.
15. Hover capture over the disc alone: the disc scales up ~7 % and nothing else moves. Press: it scales to ~92 %.
16. The disc is 40×40, a full circle, accent-filled, with a small drop shadow and a 15-DIP play glyph in the on-accent
    ink — in BOTH themes (toggle theme, re-capture).
17. Row 2 of `wavee:show:3` (the seeded in-progress one): meta reads `<date> · <N> min · In progress`, the last cell in
    the accent text ink at 12/600.
18. Same row: a 3-DIP accent rule spans the card's inner width, filled to ~33 %, on a faint ground, with 2-DIP corners.
19. An unplayed row has **no** rule and is ~112 DIP tall; the in-progress row is ~115 DIP (or ~135 with a 2-line title
    and 2-line description). Measure both.
20. Right-click a row: **nothing** happens (no menu). Double-click: nothing.
21. Click a row's body (not the disc): nothing happens.
22. Click the disc: that episode plays and the player bar's title matches the card's title.

**Show page — toolbar, banner, empty, paging**

23. The toolbar's status selector sits flush left, the Newest/Oldest selector flush right, with 4 DIP between the
    toolbar and the "Listen next" header.
24. Click **Unplayed**: every visible row has no progress rule. Click **In progress**: exactly one row on
    `wavee:show:3`. Click **Played**: an empty state reading "No episodes match this filter" at 14 px tertiary, with
    16/20/16/20 padding.
25. With **Played** selected (empty list) the "Listen next" banner is STILL present — it is picked from all episodes,
    not the filtered view.
26. Click **Oldest**: the row order reverses and the "Listen next" banner does not move or change.
27. The banner: 72-DIP art, an all-caps 12/600 tracked eyebrow, a 15 px bold single-line title, a
    `<date> · <N> min` caption, a progress rule, and a Resume capsule pinned to the right edge at 36 DIP tall.
28. Narrow the window until the banner title would overflow: the **copy column** gives; the Resume capsule never
    shrinks.
29. `--fake` shows never show a "Load more episodes" pill (the fake source reports the cursor complete). On a live
    700-episode show the pill appears once, centred near the column bottom, in the standard (not accent) appearance.
30. Tap it: the label becomes "Loading…" and a further tap does nothing until rows land; the pill then disappears if
    the cursor reached the total, else remains for the next page.

**Module page (entity)**

31. Open a YouTube `channel:` page (a non-watch document): 84 DIP of clear space above the hero, then a 232×232 cover,
    then a 20-DIP gap to the text column.
32. The hero title is 28/36 semibold (one rung larger than a watch page's title — compare side by side).
33. Action buttons are stock Fluent RECTANGLES (4-DIP radius), not capsules, and at most one is accent-filled.
34. A `facts` section renders value-over-label tiles that grow to equal widths and wrap; capture a frame during mount
    and confirm they arrive left-to-right ~45 ms apart.
35. A `links` section row shows title over subtitle with a 14-DIP "open in new window" glyph right-aligned, and turns
    the cursor to a hand.
36. Kill the module process mid-open: W12 appears — headline "This page couldn't be loaded.", one caption, a quiet
    **Retry**, and (when the document was cached) **Open in browser** below it.
37. Click Retry: the shimmer returns and the page reloads.
38. Navigate away and back: the page paints its hero on the FIRST frame (the cached document is the seed) — capture the
    very first frame after the route change; there must be no figure-space shimmer.
39. Force a cold open (clear the module page cache / first visit): the shimmer shows a hero bar plus two shorter bars
    and NEVER a flash of real text.
40. A `playables` section: rows are the app's art-card row at 40-DIP art, with no duration, no heart, and a film glyph
    only when the item is video. Right-click one: W16's menu, with **no** "Go to album" row and an
    "Open on YouTube" row at the bottom.
41. A `cards` section: 168-DIP-wide shelf cards laid out in a wrapping row with a 12-DIP gap — **not** a paged carousel.

**Watch page**

42. Open a YouTube video page while nothing is playing. Static capture: a full-width black 16:9 box directly under the
    84-DIP reserve, holding a dimmed poster and one centred 64-DIP accent disc with a small play glyph.
43. Widen past ~996 DIP of page width: the stage height stops growing at 560 and black pillars appear INSIDE the box
    (the element letterboxes, the wrapper does not).
44. Narrow to a 700-DIP window: the stage is still a 16:9 box (≈259 tall) and is NOT demoted to the floating mini
    player. The right rail's own video card, by contrast, does demote at that width.
45. Hover the disc: it scales ~4 %; a "Play" tooltip appears.
46. Click it. Frame recording across the transition: the stage box does not move, resize or fade; the poster dissolves
    into the first decoded frame in exactly ONE cross-fade; no second fade, no cut.
47. While playing: the right rail shows the queue, not a video card. Navigate away to Home: the video moves back to the
    rail without a black flash.
48. Frame recording while scrolling the caption: the stage stays pinned and razor-sharp — no edge-fade, no blur, no
    opacity ramp touches it.
49. The caption title is 20/28 semibold, at most 2 lines.
50. The LIVE badge (on a live stream) is an 18-DIP outlined chip with no fill, in the accent text ink, sitting to the
    LEFT of the facts line — and identical to the badge on the same module's entity-layout hero.
51. The channel row: a 40-DIP circular avatar (initials when there is no image), a 14/600 name, a fully-rounded hover
    veil, and a hand cursor — only when the module supplied a channel entity id. On a module that did not, the row is
    inert: no hover, no cursor change, no focus ring.
52. Chip row: capsules at 36 DIP with 18-DIP side padding, wrapping at narrow widths, with a 36-DIP round "…" at the
    end.
53. While this entity is playing, the primary capsule is replaced by a **Playing** badge: same height, subtle fill, a
    hairline border, a 12-DIP accent play glyph, and no hover/press response at all.
54. The description card: 16/12/16/12 padding, 8-DIP radius, card-secondary fill with a hairline; the first line is the
    facts values in bold (no labels), the prose below clamped to exactly 3 lines with an inline "… More".
55. Click "… More": the card grows in place. In 0.2.9 the shelf below **jumps** (the caption carries no layout
    transition) — capture it, then decide: if 0.3 adds the FLIP, this item becomes "slides, position-only, ~250 ms".
56. The shelf: a measured paged carousel of 16:9 cards with a section header and chevrons; each cell's third line is
    `subtitle · meta` joined with " · ".
57. Click a shelf cell with an entity id: it navigates to that module page (and the stage re-derives). One without an
    entity id plays instead.
58. Click the "…" chip: the module track menu opens — the same menu as a `playables` row's right-click.
59. Open the SAME video page twice in two tabs, play it, and switch between them: the video mounts only in the attached
    tab; the parked one shows its poster. No black flash, no duplicate surface.
60. Theme toggle on every one of W1, W7, W8, W10, W13, W14: the stage stays pure black in both themes; every card fill,
    hairline and text ink flips with the theme; no element keeps a hard-coded light-theme colour.

**States the first pass missed — run these too**

61. A show whose filter is **All** and whose episode list is genuinely empty (point the fake source at a show with no
    episodes): 0.2.9 shows "No episodes match this filter". Confirm 0.3 shows its own empty-show copy instead (§9.4).
62. An episode with **no description** and one with **no artwork**: the row keeps its geometry, the art is the tinted
    placeholder tile, and no line collapses to zero height unexpectedly. Measure the no-description row.
63. Enable **Settings → Hero page layout** and open a show at 1280: the layout must NOT flip to the vertical hero (that
    arm is tracks-only). Then narrow to 520 and confirm it flips on WIDTH alone.
64. Turn **"Keep left-rail same size"** on, resize the rail on an album page, then open a show: the show opens at the
    shared width. Turn it off: the show returns to its own persisted pair.
65. Marquee-drag across the episode column and Ctrl+A: nothing selects, no batch bar appears.
66. Collapse the rail and click the **cover** (not the chevron): it expands.
67. Module page, `custom` template (a module that ships sections with no hero): the page opens on its first section, no
    hero row, no reserved 232-DIP square (W17).
68. Module page, `entity` template whose document carries **no hero**: the hero title is the label the navigating
    surface passed (the route Arg), or "Module" when it passed none.
69. Watch page with a document that names **no play action**: the stage is poster-only — no 64-DIP disc — and the chip
    row has no "…" overflow (W19).
70. Play the watch entity, then press fullscreen: on returning, and while the video is in the floating mini player or a
    popped-out window, the page stage shows the poster + disc while the chip row still reads **Playing** (W18).
    Confirm the app does not black-box the stage.
71. Watch shelf: hover a cell (44-DIP play FAB fades in), and play one of the shelf's own videos — the playing cell
    takes the now-playing overlay. A LIVE shelf cell is currently indistinguishable from a recorded one (§9.4).
72. Module `playables`: play a row, then re-open the page — the row wears the now-playing re-skin and clicking it
    toggles pause. With no overlay service (a module page opened before the overlay mounts) the row has no menu.

**Third-pass additions — states the second pass still missed**

73. Deep-link a show COLD with the network throttled and capture the skeleton at 400 %: confirm the 0.2.9 behaviour
    W6's table describes — **no rail title bar, no rail description row, and no title/description bars on the episode
    cards** (only art squares, play discs and the "Jan 1 · 3 min" meta). Then confirm 0.3 shows figure-space bars in
    all four places instead (§9.4).
74. Open a watch page COLD (clear the module page cache, throttle the module): capture the frame before and the frame
    after Ready — 0.2.9 has NO stage while loading and the caption jumps down by the stage height on Ready (W20).
    Confirm 0.3's chosen fix (reserved envelope, or a watch-shaped seed) removes the jump.
75. Open a watch page, let it cache, then kill the module and hit Retry: the 16:9 stage stays mounted ABOVE the error
    body (W21). It must not blank.
76. A watch document whose shelf section has **no title**: the 16:9 strip draws with no header line and no chevron row
    above it (`ShelfTitle` null ⇒ `header: null`).
77. A watch document whose hero has an avatar url and a subtitle **entity id but no subtitle text**: no channel row at
    all, and the one-card `cards` section that would have been the channel becomes a normal shelf cell
    (`WatchPageModel.cs:151-156`).
78. A module `playables` section of exactly 12 rows vs 13: the 12-row one mounts in a single frame with no ticker; the
    13-row one ramps. Capture both with the frame counter.
79. A module document whose FIRST section is a `facts` block with 500 rows: every later section is dropped (the shared
    item budget). Confirm the page still draws and does not error.
80. Collapse the rail and measure the strip: 96 wide with **16 DIP of top/bottom padding and 8 at the sides**, the
    chevron pinned to the FOOT of the strip (a `Grow = 1` spacer above it), not sitting under the title.
81. Zoom the vertical (mode 3) header and the two-column rail side by side at the same window height: both titles are
    28/36/600, but the rail's is the *display* face with −20 tracking and the header's is the system face. Confirm 0.3
    keeps the distinction rather than unifying them.
82. The "Playing" badge's word is SECONDARY ink, not primary — capture it beside a Standard capsule's label and
    confirm it reads quieter, with only the 12-DIP play glyph in the accent ink.
83. Drop a non-media file on the window (not on a row): an Error toast, no playback. Drop an .mp4 on a TRACK ROW
    instead: it attaches to that playable rather than playing (§6.5).

---

## 11. Audit log

Second pass, re-derived from the 0.2.9 sources at HEAD `b3f6647a`. Every number in §2/§3/§5 was re-checked against the
file it cites; the ones not listed below verified as written (Spacing/Radii/Elevation rungs, the 12/16/8/40/56/72/232/
168/64/560/36/18 geometry, the 820/660/560 + 24 / 540↔580 breakpoints, 180/280/480/224/188/96/80, `Expressive` 83/150/
250/500, `ScaleTier` 1.02·0.98 / 1.04·0.96 / 1.07·0.92, stagger 45, `SoftReveal` dy 8 / blur 3 / 500 SmoothOut,
`FadeOnly` 250, shimmer pulse 1000→0.5 α, PagedShelf 150–200 / gap 12 / headerGap 12 / edgeFade 36, StatTile 18/800 +
11, PillHeight 36 + pad 18/6/18/7, IconButton glyph 16, VideoCard pad (8,8,8,12) + thumb `cardW−16` at r4 decode 480,
`PlayerDock.Reserve` 72, `BrowseMastheadMetrics.Reserve` 32+52=84, the `PendingSeed` shape, the `--fake` podcast seeds,
and every loc key quoted — `podcast.minutes` really does exist unused).

| # | kind | section | correction |
|---|---|---|---|
| 1 | missing | §0, §1.2, W17 | the module page has THREE templates: `custom` (sections-only, hero only if supplied) and the `entity` hero FALLBACK (`route.Arg ?? loc modulePage.title`) were undocumented — `ModulePage.cs:286-293` |
| 2 | missing | §0, W18, §6.3, §7 | the stage's video mount is gated on `resolved == SurfacePlacement.Docked` first (`DockedVideoHosting.cs:143`): fullscreen / pop-out / mini player return the page to poster + the 64-disc while the caption still says "Playing". A whole reachable state was absent |
| 3 | missing | W19, §6.3 | no play action ⇒ NO idle disc and NO "…" overflow (`ModulePage.cs:223-233`, `WatchPageView.cs:107, 286`); the minimal (title-only) caption was undocumented |
| 4 | missing | §1.3 | every caption row below the title is conditional, and the title itself may be `""` (`WatchPageModel.cs:196`) |
| 5 | missing | §1.3, §5, §9.4, parity 55 | the watch caption carries NO `Enter`/`Layout`, so "… More" JUMPS the shelf — parity item 55 claimed a 250 ms position FLIP that only the module page's `Section()` has. Overclaim, now corrected in place |
| 6 | missing | W13, §3, §6.3 | the chip glyph rule (play → `Icons.Play`, openUrl → `Icons.OpenInNewWindow`, moduleAction → none) and the "…" chip's two gates |
| 7 | missing | W13, W10, §3, §6.2/6.3 | `MediaCard.VideoCard` / `MediaCard.Shelf` cells carry a 44-DIP hover play FAB and the now-playing overlay keyed by the module playable uri; the `playables` row carries `TrackRow.StateOf`'s now-playing/paused re-skin and its full geometry (MinHeight 52, gap 12, pad 4/2/4/2, art 40 r4) |
| 8 | missing | W14 | before the first decoded frame the element paints its own poster PLUS a spinner (`DockedVideoSurface.cs:409-413`) — the DRM/manifest wait was not drawn anywhere |
| 9 | missing | W7, §6.1 | states absent from the episode-row zoom: selection is structurally impossible (`Selection = None`), no-description (an empty `TextEl` is still authored), no-art (static tinted tile below `ShimmerMinEdge` 80), long-title behaviour; also *why* the disc is focusable (`TabStop` null = auto, `Element.cs:290-293`) |
| 10 | missing | W9, §3, §9.4 | the zero-episode show falls into the empty-FILTER arm and its copy — a defect, now recorded as one |
| 11 | missing | W6 | the cold seed produces no resume banner, no "In progress", no progress rule and no load-more pill; added the engine's shimmer bar geometry (r4, RowGap 8, 0.72 text ratio) |
| 12 | missing | W5, §6.1 | the collapsed rail's COVER is also an expand target; chevron glyph/size named |
| 13 | missing | §6.1 | two settings branches that change this surface: "Hero page layout" is tracks-only (a show never force-flips), and "Keep left-rail same size" re-points the rail scope to `Uniform` (parity item 8's "shows only" is true only with it off) |
| 14 | missing | §1.4 | keys that freeze content: `rich-expand-flex:…:{html}:{maxLines}`, `fact:`+key, the shelf's `keyOf`, and the rail heart's `save:`+uri |
| 15 | missing | §5 | the SHOW rail's own motion (`LateRow` fade-up + `Shove` FLIP on eyebrow/description), `StatTile`'s own FadeUp/Shove under the 45 ms stagger, and the shelf's paging scroll |
| 16 | missing | §3 | rows for the empty-filter box, the skeleton bar, the banner's header→card gap 8, and the rail CTA cluster (gap 12 / FAB group 8 / margin-top 4 / wrap-as-a-unit) |
| 17 | missing | §8 | three pure rules with no row: `ModulePage.OpenUrlOf` (shared by the failed state and the menu), the `ModulePages`/`ModuleUri` route grammar, and the page cache's TTL rule |
| 18 | missing | W16 | the module menu's full row ORDER from `Menus.TrackRows` (incl. the separator before "Open on <Module>", the absent Move-to-playlist and destructive blocks, and that the 4th strip verb is `ToggleLike`) |
| 19 | wrong | §8 | the test file is `DetailLayoutBreakpointTests.cs`, not `DetailLayoutBreakpointsTests.cs`; `DetailLayoutBreakpoints.cs` is 89 lines, not 81 |
| 20 | overclaim | §3 | the card-width row claimed "height = `cardW + 72` = 240" sourced to `MediaCard.cs:209` — `ShelfHeight` is at `:212` and is the VIRTUALIZED shelf's extent reserve; `CardsBlock`'s wrap row measures its cards instead |
| 21 | wrong | §3 | the rail Play pill is `DetailRail.cs:596-597` (`PlayPill`), not `:601` (that line is inside `Fab`) |
| 22 | wrong | header, §8 | `DockedVideoHosting.cs` is 168 lines, not 169 (every other cited length re-counted correct: 260 / 776 / 402 / 292 / 311 / 757 / 858 / 607 / 709 / 115 / 27, and the five test files 298 / 54 / 113 / 123 / 246 / 379) |
| 23 | unverified | §10 | the reference build path `C:\WAVEE\wavee-0.2.9\…\Wavee.exe` and the 0.2.9-era capture claims were not exercised (no build/launch in this pass); everything else in §10 now has a source behind it |
| 24 | unverified | §9.5 | the 0.3 line estimates are judgement, not measurement; the 0.2.9 side (`EpisodeList` 260 · `ModulePage` 776 · `WatchPageView` 402 · `WatchPageModel` 292) was re-counted and is correct |

### Third pass — adversarial re-derivation (2026-09-12)

Independently re-read `EpisodeList.cs`, `ModulePage.cs`, `WatchPageView.cs`, `WatchPageModel.cs`, the show branches in
`DetailConfig`/`DetailPage`/`DetailShell`/`DetailRail`, `PlayableLinks.cs`, `LocalFileActions.cs`, `Menus.cs`,
`DockedVideoHosting.cs`, `Models.cs`, `FakeData.cs`, `FakePodcastSource.cs`, `ContentHost.cs`, `ModulePages.cs`,
`Wavee.Sdk/ModulePage.cs`, plus the engine's `Spacing`/`Radii`/`Elevation`/`Typography`/`Expressive`/`MotionRecipes`/
`SkeletonRegion`/`IconButton`/`SelectorBar`/`PagedShelf`/`Element` and the app's `WaveeTokens`/`WaveeType`/`WaveeCta`/
`WaveeMotion`/`Surfaces`/`StatTile`/`MediaCard`/`TrackRow`/`RichText`/`BrowseMastheadMetrics`, and `en-US.json`.

Re-verified as written (not repeated below): every `Spacing`/`Radii` rung used here (XS 4 · S 8 · M 12 · L 16 · XL 20 ·
XXL 24 · XXXL 32; Card 8 · Control 4 · Full 999), `Edges4(Left, Top, Right, Bottom)`, `Elevation.Card` (dark 8/2/
`#00000033`, light 4/2/`#0000001A`), the type ramp (Caption 12/16 secondary · Body 14/20 primary · BodyStrong 14/20/600
· Subtitle 20/28/600 · Title 28/36/600 · TitleLarge 40/52/600) and every `WaveeType` alias on top of it,
`PlayerDock.Reserve` 72, `WaveeSize.RailAlbum` 280 / `ArtThumb` 40, `RailCompactW` 96 / `GripStripCollapsedW` 20,
`CoverEdge = railW − 24` (SidePadL 16 / SidePadR 8) ⇒ 256 / 200 / 164, `RailForcePush` 44 / `RailReExpand` 220,
`DetailRailPolicy` 180/480/280 + `ResizableMode` 0, `DetailLayoutBreakpoints` 820/660/560 + hysteresis 24 + 540↔580 +
`TwoColumnContentMinW` 300 + `ShellChromeAllowanceDip` 240, `titleSize` 40 at winH ≥ 900 else 28, `descLines` 3 below
760, `DetailRevealRamp` 12/60, `ModulePageBudget` 40/500/64 KiB/2 MiB, `SkeletonStyle.Default` (r4 · RowGap 8 · 0.72 ·
1000 ms → 0.5 α · ExitMs 250), `SoftReveal` dy 8 / blur 3 / 500 / SmoothOut and its reduced-motion early return,
`TextSwap` 150 EaseInOut dy 4 blur 2, `IconSwap` 250, `ScaleTier` 1.02·0.98 / 1.04·0.96 / 1.07·0.92,
`MastheadStaggerMs` 45, `WaveeCta.PillHeight` 36 + pad 18/6/18/7 + `Radii.Full` + Bold, `IconButton` GlyphSize 16,
`PagedShelf` 150–200 / gap 12 / headerGap 12 / edgeFade 36, `StatTile` 18/800 + 11 + pad (12,8,12,8) + gap 1 +
`"fact:"`/`"v:"` keys, `MediaCard.VideoCard` pad (8,8,8,12) + `cardW − 16` thumb at r4 decode 480 + FabSize 44 +
`ScaleSubtle`, `TrackRow.ArtCard` MinHeight 52 / gap 12 / pad (4,2,4,2) / art radius `Radii.Control`,
`BrowseMastheadMetrics.Reserve` 32 + 52 = 84 (through `BrowseTiles.cs:295`), `Tok.MediaLetterbox` opaque black at
`Tokens.cs:372`, `DockedVideoSurface.PosterGround` opacity 0.40 at `:423-426`, `ShimmerMinEdge` 80 / `TintStrength`
0.55, `RichText`'s `rich-expand-flex:` key shape, `Menus.Tracks`/`TrackRows`/`ModuleTrack` at 56 / 81-133 / 150, the
`--fake` podcast seeds (8 shows · 8–12 episodes · index 1 at ⅓ · `TotalEpisodes == PagedThrough == resident`), and
every loc key quoted — including `podcast.minutes` and `podcast.episodeCount`, which both really do exist unused or
drawn-nowhere.

| # | kind | section | correction |
|---|---|---|---|
| 25 | wrong | W6 | the cold-skeleton wireframe drew rail title bars, a rail description and episode title/description bars. `PendingSeed(Show)` seeds `Title ""` / `Description null` over `DetailModel.Empty` (`Title ""`, `Description null`, `Cover null`), so a DERIVED shimmer can paint none of them — and `rail:desc` is not even mounted. W6 is rewritten with a slot-by-slot table, and the fix (seed U+2007 runs, the `ModulePage.Seed` idiom) is added to §9.4 |
| 26 | missing | W20 (new), §5, §9.4 | a COLD watch page has **no stage at all**: the loading seed is `TemplateEntity`, so `WatchPageModel.From` returns null and the stage slot takes the empty-box arm. Ready then materialises a ≤560-DIP box above the scroller and drops the whole caption — a load→ready reflow no wireframe covered |
| 27 | missing | W21 (new) | a FAILED refresh over a CACHED watch document keeps the stage mounted (poster, or even live) above the W12 error body: `model`/`stagePlayable` are derived OUTSIDE `Skel.Region`, from the loadable's last value |
| 28 | missing | §0.18, §9.4 | the 500-item budget is ONE pool spent in document order (`ref int budget` threaded through every block), so an early fat section starves the rest; and the 40-section cap counts only sections that actually DREW |
| 29 | missing | §5 | `RampedRows` short-circuits at ≤ 12 rows — no ramp, no `TickerClock` at all; and `Next` returns `Done` at `min(rows, 60)`, so a 100-row block mounts its tail in one step |
| 30 | missing | §1.3, §3, parity 76 | `ShelfTitle` is `Trimmed(section.Title)`, and null ⇒ `header: null` — a headerless 16:9 strip is a legal watch page |
| 31 | missing | §1.3, parity 77 | the channel identity drops WHOLE when there is no name (avatar, entity id and the card index all cleared), which RELEASES the legacy one-card section back to the shelf; and `FindChannelCard` requires exactly ONE item carrying BOTH an entity id and a title |
| 32 | missing | §1.3 | the stage's surface carries `OwnerStagePlayable = stagePlayable` — the parked-twin discriminator inside `ShouldMount`, not decoration |
| 33 | wrong | §3, parity 82 | the "Playing" badge's word is `Ui.Caption`'s own `Tok.TextSecondary`, not primary — only the 12-DIP glyph is accent. Likewise the description card's fact line is `Ui.Body`'s `Tok.TextPrimary`. Neither ink was stated |
| 34 | missing | §3 | the LIVE badge's copy is loc `play.live` = "LIVE" (literal caps) and it carries `Shrink 0` + centred alignment; the hero TITLE ROW has its own gap 8 / AlignItems Center; so do the action row, the episode row A and the episode toolbar |
| 35 | missing | §3 | `WaveeCta.Icon` never overrides `IconButton.Style.GlyphSize`, so the 64-DIP stage disc keeps a **16-DIP** glyph (it does not scale with the box) — and it wears the Accent ramp's own 1 px border brush |
| 36 | wrong | W5, parity 80 | the collapsed strip's padding is `(8, 16, 8, 16)`, not "pad 8"; added the `compact:spacer` `Grow = 1` that pins the chevron to the strip's foot, and the fact that `right` keeps its key across the detent |
| 37 | overclaim | §6.4 | "its two other callers" — `VideoActions.PlayAs` has FOUR call sites on this chapter's surfaces alone (`ModulePage.cs:250, 453, 553`, `WatchPageView.cs:370`), plus `LocalFileActions.cs:109`, `DetailTracks.cs:3306/3308` and `PlayLinkDialog.cs:76/173` |
| 38 | missing | §6.5 (new) | `LocalFileActions` was named once and never drawn: the modal picker and its exception toast, `localFile.rejected` / `localFile.notReady`, the HIDE-not-disable rule, the row-drop-wins rule, and the self-attach through `VideoActions.Apply` before a video plays |
| 39 | missing | §1.1 | `DetailConfig.Show` spelled out — in particular **`HasTrailing: false`**, i.e. a show page has no trailing block and never takes the outer-scroll composition |
| 40 | missing | W4 | the VERTICAL header's title is `WaveeType.PageHero` (system face) while the rail's is `WaveeType.DetailHero` (display face, tracking −20) — same metrics, two faces; added the header's own gaps/padding |
| 41 | missing | §9.2, parity 80 | `right:eps` carries no `MinHeight = 0f` (the tracks arm does) and `EpisodeList`'s `ScrollView` carries no `ScrollKey` (the module page's does) — both become real defects the moment 0.3 virtualizes the list |
| 42 | wrong | §0.17, W18 | the placement gate is `DockedVideoHosting.cs:`**`140`**, not `:143` |
| 43 | wrong | header, §8 | `DetailLayoutBreakpoints.cs` is **88** lines (the previous pass said 89), `DetailRailPolicy.cs` is **67** (said 68), and `Wavee.Sdk/ModulePage.cs` is **312** (said 313). Every other cited length re-counted correct |
| 44 | wrong | §8, W7, §1.4 | the "Open on &lt;Module&gt;" row is `Menus.cs:153-161` (`OpenOnModuleRows`), not `:163-172`; the TabStop rule is `Element.cs:291-294`, not `:290-293`; the rail heart's `save:` key is `DetailRail.cs:233`, not `:231`; and §1.4's key list was missing the `":cards"` (`:618`) and `":links"` (`:662`) containers |
| 45 | missing | §9.4 | `podcast.episodeCount` is a properly pluralised ICU key that exists solely to build the meta line `DetailRail` then refuses to draw — so drawing it in 0.3 costs no new loc key |
| 46 | unverified | §10 | as in the second pass: the reference-build path and every capture claim are still unexercised (no build, no launch in this pass, per the audit's terms) |
| 47 | unverified | §9.5 | the 0.3 line estimates remain judgement; the 0.2.9 counts were re-counted a third time and stand (260 / 776 / 402 / 292 / 311 / 757 / 858 / 607 / 709 / 82 / 115 / 168 / 27) |

**token-reconcile (2026-09-12):** this chapter is the only one in the contract that names a token the token layer does not have — `EpisodeCardTitle` 14/20/700 and `EpisodeBannerTitle` 15/20/700 (§9's type-ramp-escapes bullet). Both are now carried in `00-design-system.md §12.1`'s "used by a chapter, defined nowhere" table, and the bullet here records the two conditions they have to clear: 700 is a fourth sanctioned weight divergence, and 15 px is off the engine ramp (12 · 14 · 18 · 20 · 28 · 40 · 68). No value in this chapter was wrong — `ScaleEmphatic` 1.07/0.92, `WaveeMotion.Faster/Fast` 83/167, `PlayerDock.Reserve` 72, `WaveeSize.ArtThumb` 40, `WaveeCta.PillHeight` 36, `Radii.Card` 8 / `Control` 4, `Surfaces.TintStrength` 0.55 and `ShimmerMinEdge` 80 all re-verified against `src/apps/Wavee/Design/*.cs`.

**token-reconcile (2026-09-12):** second pass. `Tok.MediaLetterbox` — which this chapter names but the first build of `00-design-system.md §12.1` omitted — is now indexed there (`#000000`, theme-invariant, one rung below `MediaStage`'s `#0A0A0A`). No value in this chapter changed.
