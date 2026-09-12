# Home section ("see all") page and the Home customizer - 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Home/HomeSectionPage.cs` (628) · `HomeSectionNavigation.cs` (104) ·
> `HomeSectionPaging.cs` (119) · `HomeSectionRoutes.cs` (42) · `HomeCustomizerPage.cs` (310) ·
> `Persistence/HomeLayoutDoc.cs` (157) · `Persistence/HomeLayoutStore.cs` (244) · `BrowseSectionWalk.cs` (82, holds
> `ChartTitleMatch`) · `HomeSectionAppendPreloader.cs` (96) · `HomePreferences.cs` (88) ·
> `src/apps/Wavee.Core/Home/{HomeLayoutModel,HomeLayoutReducer,HomeLayoutCommands}.cs` (147 + 64 + 60) ·
> consulted: `Features/Shell/ContentHost.cs` (322), `Features/Shell/ShellMastheadBand.cs` (131),
> `Features/Shell/NavOrigin.cs` (108), `Features/Shell/DrillTrail.cs` (112), `App/ShellMasthead.cs` (53),
> `Features/Browse/BrowseMastheadMetrics.cs` (38), `Features/Home/HomeModules.cs:418-476,490-536`,
> `Features/Browse/BrowseTiles.cs:292-298` (`BrowseLayout.FrameX/FrameTop`), `Features/Browse/BrowseTaxonomy.cs:148-181`
> (`ChartPages` / `ChartSections`), `Components/MediaCard.cs:200-290,1286-1560` (`GridCard`, `LazyNowPlayingOverlay`,
> `NowPlayingOverlay`, `FabReveal`), `Components/Equalizer.cs` (`WaveeEqualizer`), `Components/EmptyState.cs`,
> `Components/ErrorState.cs`, `Design/SearchHighlight.cs`, `Design/WaveeType.cs`, `Features/Shell/PageNavMotion.cs`,
> `Features/Shell/ShellWashGeometry.cs`, `Features/Home/HomeWashSource.cs`, `App/ShellMaterial.cs`,
> `Features/Home/HomePage.cs:340-360,985-1017`, `assets/loc/en-US.json`
> **| 0.3 target** (settled 2026-09-12, A15 — four of the seven-file Home set, §9)**:** `Entities/Home.Page.cs` (the
> section page, as `Home.SectionPage`, in the same class family as the landing) + **`Entities/Home.Customizer.cs`**
> (the customizer page — not `Screens/HomeCustomize.UI.cs`; Home keeps all seven of its files in `Entities/`) +
> `Entities/Home.cs` (the layout document, reducer, commands, wire, and this page's cursor / walk / filter / routes)
> + `Entities/Home.Host.cs` (the json store)
> **| Wave 5 owner P**
>
> Cross-references (do not re-specify): design tokens, type ramp, motion curves, cover palette, materials →
> `00-design-system.md`. Grid card (`MediaCard.GridCard`), card menu, drag chip, `EmptyState`/`ErrorState` →
> `02-cards-and-controls.md`. Home page frame, wash, facets, section flow → `10-home.md`. Section/module kinds and
> the Fold tile that drills here → `11-home-cards-and-modules.md`. Masthead band, drill trail, page transitions,
> KeepAlive → `18-shell-frame.md`. Browse directory / category pages that mount the **same** page class →
> `13-search-and-browse.md`.
>
> Doc drift (one line, as asked): `docs/plans/wavee/home-sections-v1-mica.html` is a Home-page prototype
> (Fold × Strip deck + Browse) and contains **no** section-drill-in screen and **no** customizer screen at all —
> every number below therefore comes from the code, and the prototype contributes only the shared card/grid
> vocabulary already captured in `00-` and `02-`.

---

## 0. The non-negotiables

1. **The masthead title is real from frame one.** The drill publishes the title it already knows
   (`HomeSectionPage.cs:265` reads `currentSection`, whose `Title` is the route's own arg at
   `HomeSectionPage.cs:100`), so "Made For You" is set in 40/52 display type *before* the request lands. Only the
   grid shimmers. A section page that opens with a shimmering title is a regression.
2. **A drill that carries a seed never shimmers at all.** When Home stashed the first page
   (`HomeSectionPreviewStore`, `HomeSectionNavigation.cs:11-26`), the loadable starts `Ready`
   (`HomeSectionPage.cs:101-103`) and the region's shimmer→real branch edge never fires — the cards are simply
   there on the first presented frame. Cold drills get the 8-card shimmer and the blur-rise.
3. **Covers are square, always.** The row height is derived from the arranged cell width by
   `AspectGridVirtualLayout` (`aspect = 1`), never from a separately fitted card width
   (`HomeModules.cs:437`, and the comment at `:418-422` naming the shear this replaced: titles ellipsised to
   "Netherla…" because item rects came out shorter than the covers).
4. **One content column, one gutter, across the whole family.** The body pad is
   `BrowseMastheadMetrics.FamilyBodyPad(Spacing.L)` = `(36, 100, 36, 16)` (`BrowseMastheadMetrics.cs:17-20`),
   identical on the Browse directory and a Browse category page, so a drill never shifts the content column
   sideways or vertically.
5. **The page has no outer scroller.** The grid owns its own `Virtual.Custom` viewport
   (`HomeModules.cs:435-463`, `HomeSectionPage.cs:119-121`), so the toolbar row (Charts) stays pinned and only the
   cards move. A page-level `ScrollView` around this grid is a regression.
6. **The shell wash is this section's own first gradeable card, or nothing.** `WashCard` scans at most 32 cards
   for a payload accent or a gradeable cover (`HomeSectionPage.cs:32, 359-369`); with neither, the page publishes
   `definite: true` with no layers and the shell shows its deterministic ground. An invented tint is a lie.
7. **"Show all" lives in the shell band, not on the page.** `ShellMastheadState.ToolsVisible/ToolsAction`
   (`HomeSectionPage.cs:270-276`) renders a `Button.Subtle/Small` at the masthead's right edge, bottom-aligned with
   the title (`ShellMastheadBand.cs:59-73`). It disarms the instant the cursor ledger says there is nothing more.
8. **Infinite scroll is silent.** `HomeSectionAppendPreloader` renders `new BoxEl()` — no shimmer row, no spinner
   (`HomeSectionAppendPreloader.cs:72` and its class comment). Appended cards simply appear inside the same
   virtualized grid.
9. **Charts is a different page in exactly three ways and no more:** a 300 × 32 filter box + a 160 × 3 determinate
   walk bar on one 12-gap toolbar row; two-line titles with no metadata line; an accent highlight pill on the
   matched run. Everything else is the identical grid.
10. **The walk bar's slot is permanent.** The 160-wide box is always in the row; only the bar inside comes and
    goes (`HomeSectionPage.cs:310-317`), so nothing below moves when the walk starts or ends.
11. **The customizer is a page, not a dialog.** 64-DIP command bar (Back · eyebrow+title · Reset · Done), a 1-DIP
    divider, then a 720-capped scrolling column of 48-DIP rows (`HomeCustomizerPage.cs:21-23, 78-83`). It edits the
    live document; there is no OK/Cancel and no staged copy.
12. **Hidden is dimmed, never removed.** A hidden module stays in the list at `Opacity = 0.55`
    (`HomeCustomizerPage.cs:268`) with its toggle off — the hint string says exactly this
    (`home.customizer.hiddenHint`, `assets/loc/en-US.json:581`).
13. **Reorder is displacement, not an insertion line.** `LiveProject` is on (engine default,
    `Reorderable.cs:178`), so siblings FLIP out of the way after the 200 ms WinUI live-reorder dwell
    (`ReorderList.cs:44`) and the dragged row dims in place at 0.4 (`HomeCustomizerPage.cs:29`).
14. **Every edit persists immediately and fail-soft.** `Dispatch` → pure reducer → bump `LayoutVersion` → atomic
    write with one `.bak` (`HomePreferences.cs:36-45`, `HomeLayoutStore.cs:132-202`). A corrupt file is **never**
    overwritten: writes are blocked and a Warning `InfoBar` with a "Start fresh" action is the only way out
    (`HomeLayoutStore.cs:55-86`, `HomeCustomizerPage.cs:137-158`).
15. **`Done`/`Back` flushes before leaving — BEST EFFORT, not a guarantee.** `GoBack` calls
    `WaitForWrites(200)` (`HomeCustomizerPage.cs:180`), which waits on the ONE pending thread-pool write
    (`Task.Run` + a last-writer-wins `_seq`, `HomeLayoutStore.cs:140-142, 204-211`) and **returns a bool nobody
    reads**: past 200 ms the navigation leaves anyway. Home is never actually waiting on the file — it
    re-projects off the in-memory document and `LayoutVersion` — so this flush buys durability against a kill,
    not correctness. Port it as is; do not "fix" it into a blocking save.
16. **A cell that relates to what is playing wears the equalizer.** `MediaCard.GridCard` mounts
    `LazyOverlay` → `NowPlayingOverlay` (`MediaCard.cs:36-39, 243, 1294-1350, 1504-1520`): a card whose uri
    *relates* to the playing context (`NowPlayingMatch.RelatesToPlaying` — the LOOSE relation, so the album card of
    the playing track counts) paints a three-bar equalizer pill at the **bottom-left of the cover**, ticking while
    `IsPlaying` and settled low while paused. A card that *owns* playback (the STRICT relation) additionally swaps
    the hover FAB's glyph from `Icons.Play E768` to `Icons.Pause E769`. This is the one live, always-on motion on
    the section page and §5's table must carry it — see the audit log.
17. **The title only paints from frame one when the route carries an arg.** A drill always does
    (`HomeSectionPage.cs:100` takes `_route.Arg`), but a *cold deep link with no arg* seeds the placeholder title
    with `" "` — a single space — which `ShellMastheadRegistry.TryResolve` (`NavOrigin.cs:82-88`) treats as
    whitespace, so the band falls back to `StaticTitle` and shows the bare family word ("Home" / "Browse") with
    **no crumb at all** until the fetch lands. See W19.
18. **The skeleton's ledger is 8/8 on purpose, and that is what keeps the masthead quiet.** `BlankCards()` seeds
    `new HomeSection(uri, title, null, 8 cards, TotalCount: 8, RawItemCount: 8)` (`HomeSectionPage.cs:100`,
    `Wavee.Core/Library/HomeFeed.cs:103-111`), and `HasMore` with no cursor asks
    `TotalCount > max(RawItemCount, Cards.Count)` — 8 > 8 is false (`HomeSectionPaging.cs:38-39`). A *shimmering*
    section therefore shows **no "Show all"**; the button can only appear on the Ready edge. Seeding the
    placeholder with any larger total arms a button over a skeleton.
19. **A ONE-CARD browse section never opens this page at all.** `HomeCardNav.OpenBrowseSection` short-circuits
    `s.Cards.Count == 1` straight into `Open(s.Cards[0], …)` (`HomeSectionNavigation.cs:251-255`), so a Charts
    shelf or a Browse shelf header holding a single tile navigates to that tile's own destination — the "1-tile
    intermediate void" its comment names. Home's own `OpenSection` has **no** such rule
    (`HomePage.cs:347-355`): a one-card HOME section *does* open a section page with exactly one cell. Two
    different rules, both shipped; keep both. (`HomeCardNav` itself is owned by `11-home-cards-and-modules.md` —
    the entry contract is recorded here because it decides whether this page is ever reached.)
20. **The customizer has no undo, no toast, no confirmation and no reject feedback.** `HomeLayoutUndoLabels` and
    `HomeLayoutRejectReason` exist and are fully localized (`home.customizer.undo.{hide,show,move,reset}`,
    `assets/loc/en-US.json:587-592`), but **nothing renders them**: `HomePreferences.Dispatch` returns the reason
    (`HomePreferences.cs:36-45`) and both call sites discard it (`HomeCustomizerPage.cs:127, 282`). Hide, reorder
    and Reset are silent, immediate and irreversible within the session. 0.3 must not invent an undo bar off
    those loc keys, and must not delete them either — decide deliberately.

---

## 1. Anatomy

### 1a. Section page — 0.2.9 composition

```
ContentHost.PageFor(r)                                        Features/Shell/ContentHost.cs:207-209
└─ BoxEl Key="page:home-section" grow1/shrink1/minH0 dir=col   ContentHost.cs:208
   └─ Embed.Comp(() => new HomeSectionPage(r))                 ContentHost.cs:209   propless: route frozen at mount
      └─ BoxEl  dir=col grow1 gap=Spacing.L                    HomeSectionPage.cs:336-348
         padding = BrowseMastheadMetrics.FamilyBodyPad(16) = (36,100,36,16)
         └─ BoxEl dir=col grow1 gap=Spacing.M (12)             HomeSectionPage.cs:342-346   "bodyKids"
            ├─ [CHARTS ONLY] toolbar row                       HomeSectionPage.cs:294-319
            │   dir=row grow0 shrink0 minH=32 gap=12 alignItems=Center alignSelf=Stretch
            │   ├─ AutoSuggestBox.Create(...)                  HomeSectionPage.cs:303-305
            │   │    width 300, minHeight 32, radius Radii.Control(4), queryIcon Icons.Search,
            │   │    placeholder Loc(library.filter), text = `filter` Signal<string>, suggestions = []
            │   ├─ BoxEl grow1 shrink1 minW0                   HomeSectionPage.cs:306   (spacer)
            │   └─ BoxEl width=160 minH=3 alignSelf=Center     HomeSectionPage.cs:310-317   PERMANENT slot
            │      └─ [while walking] ProgressBar.Create(walkFrac, width:160)   :315  determinate
            └─ BoxEl dir=col grow1 shrink1 minW0 minH0         HomeSectionPage.cs:321-334
               └─ Skel.Region(section, …)                      HomeSectionPage.cs:326-332
                  reveal=StaggerRows · smoothResize=false · shimmerSource=null (shimmer IS content(seed))
                  isEmpty: Cards.Count==0 && UnsupportedCount==0
                  onEmpty → BoxEl grow1 minH0 [ EmptyState.Default() ]           :330
                  onFailed → BoxEl grow1 minH0 [ ErrorState.Build(section.Error) ] :331
                  content → GridBody(current)                                     :332
                     └─ BoxEl dir=col grow1 shrink1 minW0 minH0                   :215-257
                        ├─ Responsive.Of(width => …, fallback: 1100, grow: 1)     :233-247
                        │   │  DERIVES INSIDE THE CLOSURE (load-bearing, :224-232):
                        │   │    live   = section.Value.Value       (subscribe: a landed/appended page)
                        │   │    lq     = charts ? filter.Value.Trim() : ""  (subscribe: a keystroke)
                        │   │    shown  = charts ? ChartTitleMatch.Filter(live.Cards, lq) : live.Cards
                        │   ├─ [lq.Length>0 && shown.Count==0]
                        │   │    BoxEl grow1 minH0 [ EmptyState.Compact(Loc(library.noMatch)) ]   :239
                        │   └─ HomeModules.SectionGrid(shown, live.Uri, width, Open, svc, acts, overlay,
                        │            onScrollGeometryChanged: charts ? null : NearTailWatch(nearTail),
                        │            highlightQuery: lq or null, titleLines: charts ? 2 : 1)      :240-246
                        │      └─ Virtual.Custom(n, AspectGridVirtualLayout(cols,1,chrome,12),
                        │                        keyOf: sectionKey+"\u001F"+uri, overscan: 2)
                        │                        grow1 shrink1 minH0  + OnScrollGeometryChanged   HomeModules.cs:435-463
                        │         └─ per cell: MediaCard.GridCard(...) Key="home-section-card:<cols>:<lines>:<uri>"
                        │                       menu = Menus.CardAttach(...)  drag = Drag.Source(Resource) unless Track/Episode
                        └─ [canAutoPage] Embed.Comp(HomeSectionAppendPreloader{Loading,NearTail,Start})  :249-254
                              Key = "home-section-append:" + uri + ":" + cursor      ← Key REMOUNT is the prop channel
                           [else] new BoxEl()                                        :255   (child COUNT never changes)

ShellMastheadBand (mounted ONCE as a ContentHost overlay, NOT a child of this page)   ContentHost.cs:111-118
└─ BoxEl dir=row gap=12 alignItems=End padding=(36,32,36,0)                           ShellMastheadBand.cs:65-73
   ├─ TitleRow(trail, go, title)                                                      ShellMastheadBand.cs:81-130
   │   ├─ [per parent crumb] BoxEl Role=Button Cursor=Hand [ SurfaceDisplay(label) TextTertiary→HoverSecondary ]
   │   ├─ SurfaceDisplay("›") TextTertiary shrink0
   │   └─ SurfaceDisplay(title) Key="masthead-current" maxLines 2 grow1 basis0
   └─ [ToolsVisible] Button.Create(Loc(browse.showAll), ToolsAction, Subtle, Small, isEnabled:!ToolsLoading)
```

Page-level state (all `HomeSectionPage.Render`):

| cell | kind | seed | file:line |
|---|---|---|---|
| `section` | `Loadable<HomeSection>` | `Ready(seed)` when a seed paints, else `Pending(8 blank cards)` | :101-103 |
| `loadingMore` | `Signal<bool>` | false | :109 |
| `exhausted` | `Signal<bool>` | false — the "no more, ever" latch | :113 |
| `cursor` | `Signal<int?>` | null — the server's own `pagingInfo.nextOffset` | :116 |
| `nearTail` | `Signal<bool>` | **true** (so a first page shorter than the viewport still fills once) | :123 |
| `walking` / `walkFrac` | `Signal<bool>` / `FloatSignal` | false / 0 | :124-125 |
| `filter` | `Signal<string>` | "" | :126 |
| `walkCts` | `UseRef<CTS?>` | null; **cancelled on unmount** | :131-135 |
| `washClaimed` | `UseRef<bool>` | false | :181 |
| `_washOwner` | `readonly object` (per page instance) | — | :58 |

### 1b. Section page — 0.3 target

```
Entities/Home.Page.cs
└─ Home.SectionPage : Component                    ctor(SectionRef s, SectionSource src)   ← frozen at mount, correct:
   │                                                  each route is its own KeepAlive slot
   ├─ CORE (pure, in Entities/Home.cs):
   │    Home.SectionCursor   ← port of HomeSectionPaging  (NextOffset/HasMore/CanAdvance/Append/
   │                                                       Progressed/WalkFraction/BrowseNextOffset/BrowseSectionNextOffset)
   │    Home.SectionWalk     ← port of BrowseSectionWalk  (Begin/Fold)
   │    Home.SectionRoutes   ← port of HomeSectionRoutes + BrowseSectionRoutes (prefix consts, IsLocal)
   │    Home.ChartFilter     ← port of ChartTitleMatch    (TryFind/Filter)
   │    Home.GridFit(width)  ← port of FillRowVirtualLayout.Fit(width, 148, 188, 12) + GridCardChromeFor
   ├─ inputs
   │    Signal<uint> Entities.Current.<table>.Changed   (subscribe; NOT a per-card epoch)
   │    Edges.HomeSection  parent = the section's synthetic slot  → ReadOnlySpan<int> card slots
   │    Signal<bool>  walking, Signal<float> walkFrac, Signal<string> filter, Signal<int?> cursor,
   │    Signal<bool>  exhausted, loadingMore, nearTail        (all page-local, exactly as 0.2.9)
   └─ children (props freeze at mount — how data reaches each one)
      ├─ toolbar row (Charts)          static function of (filter, walking, walkFrac)   → SIGNALS
      │   ├─ Controls.FilterBox(filter)       text = the SAME Signal<string> instance    → SIGNAL
      │   └─ Controls.ProgressBar(walkFrac)   FloatSignal read compositor-live           → SIGNAL
      ├─ Skel.Region(sectionLoadable, …)      state read through Loadable's own signals  → SIGNAL
      │   └─ Responsive.Of(width => …)        the card span + filter read INSIDE         → SIGNAL, see §9 trap 1
      │       └─ Virtual.Custom(...)          renderItem is a Func<int,Element>          → FUNC
      │           └─ Home.Card(item, GridStyle)  bound item scope (row rebinds, no remount)
      └─ Home.AppendPreloader { Loading, NearTail, Start }   Key = "append:" + uri + ":" + cursor  → KEY REMOUNT
```

### 1c. Customizer — 0.2.9 composition

```
ContentHost.PageFor("home-customize")                             ContentHost.cs:200-202
└─ BoxEl Key="page:home-customize" grow1 dir=col
   └─ Embed.Comp(() => HomeCustomizerPage.Create())               ContentHost.cs:202
      └─ BoxEl Key="home-customizer" dir=col grow1 shrink1 minW0 minH0 ClipToBounds  HomeCustomizerPage.cs:74-83
         ├─ HeaderBar()                                           HomeCustomizerPage.cs:86-135
         │  BoxEl Key="cmdbar" dir=row Height=64 shrink0 gap=Spacing.S(8) alignItems=Center
         │  padding=(Spacing.S 8, 0, Spacing.L 16, 0)                                   :91-93
         │  ├─ BoxEl shrink0 [ ToolTip.Wrap( IconButton.Create(Icons.Back E72B, GoBack, Small)
         │  │                               , Loc(home.customizer.back) ) ]             :97-105
         │  │                  IconButton Small = 28×28 box, glyph 14, radius 4
         │  ├─ BoxEl dir=col grow1 basis0 shrink1 minW0 justify=Center                  :106-121
         │  │  ├─ WaveeType.Eyebrow(Loc(home.customizer.eyebrow))                       :111-114
         │  │  │     12/16/600, CharSpacing +30, Color = WaveeAccent.Decor, maxLines 1, char-ellipsis
         │  │  └─ TextEl(Loc(home.customizer.title)) Size 16 Weight 600 TextPrimary maxLines 1 :115-119
         │  └─ BoxEl dir=row shrink0 gap=Spacing.XS(4) alignItems=Center                :122-132
         │     ├─ Button.Create(Loc(…reset), Dispatch(ResetHomeLayout), Subtle, Small)  :127-128
         │     └─ Button.Create(Loc(…done),  GoBack,                   Accent, Small)   :129-130
         ├─ Divider()   Height 1, Fill Tok.StrokeDividerDefault, alignSelf Stretch      :78 / Factories.cs:177-179
         ├─ Banners()                                                                   :137-158
         │  [no fault or dismissed] BoxEl Key="banners" Height=0 shrink0                :140-141
         │  [fault]                 BoxEl Key="banners" dir=col shrink0
         │                          padding=(16, 8, 16, 0)                              :146
         │     └─ InfoBar.Create(Warning, Loc(…corrupt), Loc(…corruptSub),
         │            onClose: dismiss (local bool + _bannerEpoch bump),
         │            actionButton: Button.Create(Loc(…faultDiscard), DiscardCorrupt, Standard, Small))  :149-156
         └─ Body(list)                                                                  :160-176
            ScrollView(...) Key="home-customizer-column" grow1 shrink1 minH0 minW0
                            AutoEdgeFade=true (band 40, runway 24)  ScrollKey="home.customizer"   :172-176
            └─ BoxEl dir=col gap=Spacing.L(16) MaxWidth=720 minW0
               padding=(Spacing.L 16, Spacing.M 12, Spacing.L 16, Spacing.XL 20)        :162-163
               ├─ TextEl(Loc(home.customizer.hiddenHint)) Size 12 Tok.TextTertiary maxLines 3 :166-169
               └─ _reorder.List( BoxEl dir=col minW0 [ rows… ] )                        :78-82
                  Reorderable("wavee.home-layout") ItemExtent=48 Spacing=0
                     DragStyle = { Lift = Stationary, Opacity = Drag.SourceDimOpacity 0.4 }
                     RequireDropOnList = true                                           :25-31
                     Scene/RequestRender/ItemCount/ItemOf/OnReorder wired per render     :56-60
                  └─ per i: _reorder.Item(slot = _reorder.ItemAt(i),
                              Embed.Comp(() => new HomeCustomizeRow(kind)) Key="mod:<kindName>",
                              key: <kindName>)                                          :62-72
                     └─ HomeCustomizeRow                                                :250-286
                        BoxEl dir=row Height=48 alignItems=Center gap=Spacing.S(8) minW0
                             padding=(8,0,8,0)  Opacity = hidden ? 0.55 : 1             :264-268
                        ├─ BoxEl Width=12 shrink0 center/center HitTestVisible=false    :271-276
                        │     └─ Icon(Icons.GripperBar E76F, 12, Tok.TextTertiary)
                        ├─ TextEl(HomeCustomizeLabels.Of(kind)) Size 14 TextPrimary
                        │     grow1 basis0 shrink1 minW0 maxLines 1 char-ellipsis       :277-281
                        └─ ToggleSwitch.Create(_visible, v => Dispatch(SetHomeModuleHidden(kind, !v)))  :282
                              control box MinWidth 154 / MinHeight 40; pill 40×20 at its LEFT edge

Entry affordance (rendered by HomePage.GreetingBlock, defined HERE)                     :196-248
HomeCustomizeAffordance.Button(go) → Embed.Comp(HomeCustomizeButton) Key="home-customize-entry"
└─ ToolTip.Wrap( BoxEl 28×28 shrink0 center/center Corners=Radii.ControlAll(4)
                   Role=Button Focusable Cursor=Hand
                   [ Icon(Icons.More E712, 14, Tok.TextSecondary) ]
                 .Interactive(Interaction.Subtle),
                 Loc(home.customize) )                                                  :237-246
   OnClick → overlay flyout, one item: MenuFlyoutItem(Loc(home.customizer.title), Icons.Edit E70F,
             enabled, go("home-customize", title)); minWidth 200;
             BottomEdgeAlignedRight, FocusTrap, LightDismiss, PopupChrome.Popup          :222-234
   overlay null → navigate directly                                                      :217-220
```

### 1d. Customizer — 0.3 target

```
Entities/Home.Customizer.cs                            (settled 2026-09-12, A15)
└─ HomeCustomizePage : Component                         no ctor props
   ├─ CORE (Entities/Home.cs):
   │    Home.Layout           ← HomeLayoutDoc      (Modules, DeckOrder, IsHidden, IndexOf, VisibleFixedModules)
   │    Home.LayoutModules    ← HomeLayoutModules  (DefaultOrder[12], BuildDefault, IsFixedLanding, KindName, TryParseKind)
   │    Home.LayoutReducer    ← HomeLayoutReducer  (Apply / DoSetHidden / DoMove, MaxModules 24)
   │    Home.LayoutCommands   ← HomeLayoutCommands (SetHomeModuleHidden, MoveHomeModule, ResetHomeLayout, reject reasons, undo loc keys)
   │    Home.LayoutWire       ← HomeLayoutWire     (Read/Write + HomeLayoutWireCarry)
   ├─ SHELL (Entities/Home.Host.cs):
   │    Home.LayoutStore      ← HomeLayoutStore    (Load/Commit/WaitForWrites/DiscardCorrupt, atomic + .bak)
   │    Home.Prefs            ← HomePreferences    (one instance, Signal<int> LayoutVersion, Fault)
   └─ children
      ├─ command bar            static; Reset/Done are Actions                     → closures over Home.Prefs
      ├─ banner                 rendered from Home.Prefs.Fault + a local dismissed → LOCAL SIGNAL (see §9 trap 4)
      └─ Reorderable list       ItemCount / ItemOf / OnReorder assigned per render → fields, not props
         └─ Row(kind)           Embed.Comp(..) Key="mod:"+kindName                 → KEY (stable per kind)
             hidden state reaches the toggle through a Signal written by a
             UseLayoutEffect keyed on (hidden, LayoutVersion)                      → SIGNAL, :262
```

---

## 2. Wireframes

Scale ≈ 8 DIP per character. "content width" = the width `Responsive.Of` receives = content-pane width − 72
(2 × `Spacing.PageWide` 36). The masthead band is an overlay: it paints *over* the page's top 100 DIP of padding.

### W1 - Section page, cold load (Pending, shimmer) @ content 640 (4 cols × 151)

```
 ◀── 36 ──▶                            content column 640                              ◀── 36 ──▶
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                      ▲ 32  BrowseLayout.FrameTop │
│ Home  ›  Made For You                                              [Show all]  ← only if armed   │  band
│ ▲SurfaceDisplay 40/52 w400 Display face, tracking −12/1000 em, maxLines 2, bottom-aligned        │  overlay
│ ▲ crumbs: TextTertiary, hover TextSecondary, "›" TextTertiary, gap 8    ▼ 52 line                │  (no fill)
│  ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ 84 reserve │
│                                                                      ▼ +16 = body top 100        │
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐                          │
│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  8 cells (BlankCards)     │
│ │▒ 135×135 ▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  derived shimmer bars,    │
│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  Tok.FillSubtleSecondary, │
│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  radius 4, breathe 1000ms │
│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  min 0.5 (SkeletonStyle)  │
│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                           │
│ │ ▬▬▬▬▬▬▬▬▬     │ │ ▬▬▬▬▬▬▬▬      │ │ ▬▬▬▬▬▬▬▬▬▬    │ │ ▬▬▬▬▬▬▬       │  title bar  h20           │
│ │ ▬▬▬▬▬         │ │ ▬▬▬▬▬▬        │ │ ▬▬▬▬          │ │ ▬▬▬▬▬         │  meta  bar  h16           │
│ └───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘                          │
│  ◀── 151 ──▶  12   cell = 151 wide, 203 tall (151 cover-square + 52 chrome); row stride 215      │
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐                          │
│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                           │
│ └───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘                          │
│                                                                          ▼ bottom pad 16         │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W2 - Section page, reveal in progress (shimmer → real)

```
 the shimmer stays mounted as an EXIT ORPHAN under the real tree for 250 ms (SkeletonStyle.ExitMs = Expressive.Fast)
 the real GridBody rises: Opacity 0→1, TranslateY +8→0, Blur 3→0, 500 ms, Easing.SmoothOut

 t = 0 ms                          t = 200 ms                        t = 500 ms
 ┌───────────────────┐             ┌───────────────────┐             ┌───────────────────┐
 │ ▒▒▒▒ shimmer ▒▒▒▒ │  100% α     │ ░░ shimmer ░░  0% │             │                   │
 │                   │             │ ██ real ██   ~70% │             │ ██ real ██  100%  │
 │   (real at α 0,   │             │   y = +2.4 DIP    │             │   y = 0, blur 0   │
 │    y=+8, blur 3)  │             │   blur ≈ 0.9      │             │                   │
 └───────────────────┘             └───────────────────┘             └───────────────────┘

 NOTE (§5 pins this): StaggerRows walks GridBody's TWO DIRECT CHILDREN
 (the ResponsiveBox anchor and the preloader/empty box), not the grid's rows —
 the grid therefore rises as ONE slab, and the second child's 40 ms offset is invisible.
```

### W3 - Section page, loaded, seeded drill (no reveal) @ content 1100 (6 cols × 173.3)

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Home  ›  Made For You                                                                                                     [Show all]  │
│                                                                                                                                        │
│ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐  │
│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│  │
│ ││                 ││ ││                 ││ ││   ●  circular   ││ ││                 ││ ││                 ││ ││                 ││  │
│ ││  cover 157×157  ││ ││                 ││ ││  (Artist kind)  ││ ││                 ││ ││                 ││ ││                 ││  │
│ ││   radius 8      ││ ││                 ││ ││   radius Full   ││ ││                 ││ ││                 ││ ││                 ││  │
│ ││                 ││ ││                 ││ ││                 ││ ││                 ││ ││                 ││ ││                 ││  │
│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│  │
│ │ Daily Mix 1       │ │ Discover Weekly   │ │      Björk        │ │ Release Radar     │ │ Chill Hits        │ │ Deep Focus        │  │
│ │ Taylor Swift, …   │ │ Your weekly mix   │ │      Artist       │ │ New for you       │ │ Spotify           │ │ Spotify           │  │
│ └───────────────────┘ └───────────────────┘ └───────────────────┘ └───────────────────┘ └───────────────────┘ └───────────────────┘  │
│  ◀──── 173.3 ────▶ 12                                                                                                                 │
│  cell 173.3 × 225.3   cover 157.3 square (cellW − 2×Pad 8)   pad (8,8,8,12)   cover→label gap 8                                       │
│  title 14/20 w600 TextPrimary 1 line char-ellipsis · gap 2 · meta 12/16 TextSecondary 1 line char-ellipsis                            │
│  2 DIP of trailing slack per cell (AspectGrid reserves 52; the label block consumes 50) — content does NOT grow into it                │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W4 - Column breakpoints (no hysteresis — a pure function of width)

```
columns(W) = max( floor((W+12)/160) , ceil((W+12)/200) )            FillRowVirtualLayout.Fit(W, 148, 188, 12)
cellW(W)   = (W − (columns−1)·12) / columns , clamped to ≤ 188

 content W    │ 148│ 188 │ 308 │ 388 │ 468 │ 588 │ 628 │ 788 │ 948 │1108 │1268 │1428 │1588 │
 columns      │  1 │  1  │  2  │  2  │  3  │  3→4│  4  │  5  │  6  │  7  │  8  │  9  │ 10  │
 cellW at the │148 │ 188 │ 148 │ 188 │ 148 │ 188 │ 148 │ 148 │ 148 │ 148 │ 148 │ 148 │ 148 │
 lower bound  │    │     │     │     │     │ →138│     │     │     │     │     │     │     │

 exact edges (cell widths ride 148 → 188 inside each band, then a column is added):
   1 col  148 ≤ W ≤ 188      4 cols  588 <  W <  788
   2 cols 188 <  W ≤ 388     5 cols  788 ≤  W <  948
   3 cols 388 <  W ≤ 588     6 cols  948 ≤  W < 1108     n cols  160n−12 ≤ W < 160n+148  (n ≥ 5)

 The closed form holds only from FIVE columns up. Below that the `ceil` (max-width) arm is what adds the
 column, so each band opens EARLIER than 160n−12: 2 cols opens at W 189 (not 308), 3 at 389 (not 468),
 4 at 589 (not 628). From 5 up the two arms coincide and each band opens exactly at 148-wide cells.

 NO HYSTERESIS: the fit is recomputed on every measured width, so a slow drag across a boundary
 re-columns exactly once at the boundary. At the BOTTOM of every band below 5 columns the count-independent
 fit produces cells NARROWER than minCardW 148 — W 189 → 2 × 88.5, W 200 → 2 × 94, W 389 → 3 × 121.7,
 W 589 → 4 × 138.25 — a real behaviour, not a bug to "fix" (`VirtualLayout.cs:485-500`: perPage is the
 floor fit at minCardW, then grown until cardW ≤ maxCardW; nothing ever pushes cardW back up to the min).
```

### W5 - Cell hover / press / menu corner (delegates to `02-cards-and-controls.md`; the section-page configuration)

```
 rest                          hover (−4 DIP, plate at α1)          pressed
┌───────────────────┐         ┌───────────────────┐                ┌───────────────────┐
│  no plate         │         │╔═════════════════╗│  ▲ −4          │╔═════════════════╗│ scale .99
│  no border        │         │║ FillCardDefault ║│                │║                 ║│ y −1
│ ┌───────────────┐ │         │║ 1px StrokeCard  ║│                │║                 ║│
│ │    cover      │ │         │║ Elevation.Card  ║│                │║                 ║│
│ │           ⋯   │ │ ← hidden│║   [⋯ 30 circle] ║│ 8 inset TR     │║                 ║│
│ │  ▶ 44 FAB     │ │ ← hidden│║ ▶ 44 FAB        ║│ hover-revealed │║                 ║│
│ └───────────────┘ │         │╚═════════════════╝│                │╚═════════════════╝│
│  Title            │         │  Title            │                │                   │
│  Subtitle         │         │  Subtitle         │                │                   │
└───────────────────┘         └───────────────────┘                └───────────────────┘
 plate Opacity 0→1 over MotionTok.ControlFaster (83 ms); lift −4 / press scale .99 + y −1 on MotionTok.ControlNormal
 HoverElevatePaint = true (a hovered cell paints above its later siblings)

 HOVER IS MOVEMENT-ARMED, not enter-armed. The engine has no pointer-enter hook, so GridCard feeds its own
 `hovered` signal from OnPointerMoveWithin behind a HoverMotionGate (MediaCard.cs:228, 285-287): a cell that
 appears UNDER a stationary cursor — an append landing, a column re-fit, a KeepAlive unpark after a
 navigation — does NOT light up until the pointer actually moves. The plate/FAB/"⋯" reveals are engine
 HoverOpacity and are unaffected; only the now-playing overlay's mount gate reads the signal.
```

### W5b - Cell that relates to what is playing (now-playing / paused) — MISSING from the first draft

```
 related + playing                  related + paused                   OWNS playback, hovered
┌───────────────────┐              ┌───────────────────┐              ┌───────────────────┐
│┌─────────────────┐│              │┌─────────────────┐│              │╔═════════════════╗│
││                 ││              ││                 ││              │║           [⋯]   ║│
││     cover       ││              ││     cover       ││              │║     cover       ║│
││                 ││              ││                 ││              │║                 ║│
││ ┌────┐          ││              ││ ┌────┐          ││              │║ ┌────┐   ┌────┐ ║│
││ │▁▅▃ │  ← ticking││             ││ │▁▁▁ │ ← settled ││             │║ │▁▅▃ │   │ ⏸ 44│ ║│
││ └────┘          ││              ││ └────┘          ││              │║ └────┘   └────┘ ║│
│└─────────────────┘│              │└─────────────────┘│              │╚═════════════════╝│
│ Daily Mix 1       │              │ Daily Mix 1       │              │ Daily Mix 1       │
└───────────────────┘              └───────────────────┘              └───────────────────┘

 EQ PILL   BoxEl padding Spacing.XS 4 all · Corners Radii.ControlAll 4 · Fill WaveeOnMedia.ScrimRest
           inset from the cover's bottom-left corner by MediaCard.FabInset 8 / 8
           (MediaCard.cs:1504-1520 — `Padding = (FabInset, 0, 0, FabInset)`, Justify End / AlignItems Start)
           content = WaveeEqualizer.Of(playing, ink, height 14)
           ink = WaveeAccentCtx?.Ink ?? Tok.AccentTextPrimary — this page provides NO accent ctx, so
                 it is ALWAYS Tok.AccentTextPrimary here (RecentsPage is the only page that overrides it)
 BARS      three bottom-anchored bars, phase-staggered, 850 ms loop, ticked at ~30 Hz by ONE host interval
           (Equalizer.cs `EqHost.LoopMs = 850f`, `TickMs = 1000/30`); `playing` false ⇒ settled low, no ticker
 HOVER     `pauseOnHover` is FALSE on the cover layout — the bars keep ticking under a hovered card
           (the centred/inline layouts pause; the grid cell does not)
 MOUNT     LazyNowPlayingOverlay mounts the full overlay ONLY for a card whose uri relates
           (MediaCard.cs:1294-1345). An unrelated card pays one memo, no equalizer subtree, no subscriptions.
 FAB GLYPH ▶ Icons.Play E768 by default; ⏸ Icons.Pause E769 only while this card OWNS playback
           (NowPlayingMatch.OwnsPlayback) and IsPlaying — an ALBUM card of the playing track shows a moving
           equalizer but keeps ▶, because its click starts the album rather than pausing the track.
 CLICK     the FAB toggles pause/resume when it owns playback, else it plays the card (MediaCard.cs:1387-1400).
```

### W6 - Charts variant, walking @ content 640 (4 cols)

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Browse  ›  Weekly Song Charts                                       (no Show all — Charts walks) │  band
│                                                                                                  │
│ ┌──────────────────────────────────────┐                          ┌────────────────────┐         │  toolbar row
│ │ 🔍  Filter                           │ ◀─ flexible spacer ─▶    │▓▓▓▓▓▓▓▓▓░░░░░░░░░░░│         │  minH 32, gap 12
│ └──────────────────────────────────────┘                          └────────────────────┘         │
│  ◀────────── 300 ──────────▶ h 32 r4                               ◀─────── 160 ──────▶ h 3      │
│  AutoSuggestBox, queryIcon Icons.Search, placeholder "Filter"      ProgressBar determinate:      │
│  no suggestions list (NoSuggest = [])                              1px track ControlStrongStroke │
│                                                                    accent indicator r1.5         │
│                                                                    value = cards / max(total,    │
│                                                                              cards+20)           │
│                                            ▼ gap Spacing.M = 12 to the grid                      │
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐                          │
│ │  cover 135    │ │               │ │               │ │               │  cell 151 × 205          │
│ │   square      │ │               │ │               │ │               │  (chrome 54, not 52:     │
│ └───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘   2 title lines, NO meta)│
│ │ Top Songs –   │ │ Top Songs –   │ │ Top 50 –      │ │ Viral 50 –    │  title 14/20 w600 WRAPS  │
│ │ Netherlands   │ │ Argentina     │ │ Global        │ │ Japan         │  to 2 lines              │
│ └───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘  (no subtitle row)       │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
 the walk bar's 160×(≥3) SLOT is present even when not walking — nothing below can shift

 ⚠ FOUR crumb shapes, not one — the band is a function of (route, arg, NavOrigin), and every opener writes a
   DIFFERENT origin. `DrillTrail.Compose` (`DrillTrail.cs:71-93`) picks the arm:

   (a) from the BROWSE DIRECTORY (the band drawn above) — the origin IS the IA root (`BrowseRoutes.Home`), so
       `IsRoot` returns the IA untouched (`DrillTrail.cs:75, 101-104`; the origin is written explicitly at
       `BrowseDirectory.cs:79-83` for exactly this reason):

           Browse  ›  Weekly Song Charts

   (b) from HOME's Charts row — a foreign origin on a Browse-family route is PREPENDED to the WHOLE IA trail
       (`DrillTrail.cs:83-91`), so the band reads THREE crumbs, both parents clickable and both true:

           Home  ›  Browse  ›  Weekly Song Charts
           ▲clickable "home"   ▲clickable BrowseRoutes.Home   ▲current, never clickable

   (c) from a BROWSE CATEGORY page's shelf header — `BrowsePage.Shelf` writes
       `NavOrigin(<category title>, browse:<id>)` (`BrowsePage.cs:638-645`), which is the SAME family, so the
       origin is INSERTED between the root and the current crumb (`DrillTrail.cs:78-82`):

           Browse  ›  Netflix  ›  New on Netflix

       Also three crumbs, but a different arm and a different middle crumb. The first draft had only (a) and
       (b); a 0.3 that ports those two loses this one silently.

   (d) a SEARCH-lookup origin is the one exception and collapses to `"pop" › Pop` (`DrillTrail.cs:98`).

   A HOME-section drill never gains a third crumb — but not because it cannot: Home is its IA root, so the only
   origin it is ever handed (`HomePage.cs:274-275`) is `IsRoot` and the IA stands. A foreign origin on a
   `home-section:` route would take the last arm and REPLACE the Home crumb (`[origin, current]`,
   `DrillTrail.cs:92`), not prepend to it. Nothing mints one today; do not create one in 0.3 by accident.
```

### W7 - Charts variant, filter typed ("arg") — highlight pills

```
 filter box:  🔍  arg|                        walk bar slot: empty (walk finished, walking=false)

 ┌───────────────┐ ┌───────────────┐            ChartTitleMatch.TryFind: ordinal-ignore-case,
 │   cover       │ │   cover       │            first occurrence, length = query.Trim().Length
 └───────────────┘ └───────────────┘
 │ Top Songs –   │ │ Top 50 –      │            pill: BoxEl shrink0, Corners r4,
 │ ▓Arg▓entina   │ │ ▓Arg▓entina   │                  Fill  Tok.AccentSelectedTextBackground,
 └───────────────┘ └───────────────┘                  ink   Tok.TextOnAccentSelectedText,
                                                      padding (3,1,3,1), run maxLines 1 no-wrap
 run row: Wrap=true, Grow=0, Basis NaN, no ClipToBounds, MaxHeight = 2 × 20 = 40
 (SearchHighlight.Row, Design/SearchHighlight.cs:18-70 — same look as the library rows)

 NO CLEAR AFFORDANCE, and no suggestion popup. `AutoSuggestBox` never turns on the editor's DeleteButton lane —
 `ShowDeleteButton` is set by `TextBox` alone (`TextBox.cs:82-85`; the editor gates it further at
 `EditableText.cs:399`) — so there is no ✕ inside the 300-DIP box and no Clear button beside it: the only way
 back to the full grid is selecting the text and deleting it. `NoSuggest = []` means the popup never opens
 either, so Esc has nothing to close. Both are shipped facts and both are cheap to improve in 0.3 — decide,
 do not drift.
```

### W8 - Charts filter, no match

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ┌──────────────────────────────────────┐                          ┌────────────────────┐         │
│ │ 🔍  zzzz|                            │                          │                    │         │
│ └──────────────────────────────────────┘                          └────────────────────┘         │
│                                                                                                  │
│                                                                                                  │
│                              Nothing matches your filter                 ← EmptyState.Compact    │
│                              ▲ Ui.Subtitle 20/28 w600 TextPrimary, wrapped                       │
│                                                                          centred in a grow1 box, │
│                                                                          padding 24 all, gap 4   │
│                                                                          NO glyph, NO action     │
│                                                                          loc: library.noMatch    │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W9 - Section empty (an expired `wavee:local:` route, or a server section with zero cards)

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Home  ›  Sections for you                                                                        │  band keeps
│                                                                                                  │  the route's arg
│                                                                                                  │
│                                Nothing here yet                          ← EmptyState.Default()  │
│                                ▲ WaveeType.PageHero = Ui.Title 28/36 w600 TextPrimary            │
│                     When there's something to show, it'll appear here.   ← WaveeType.TrackMeta   │
│                                ▲ 12/16 TextSecondary, wrapped, gap Spacing.XS 4                  │
│                                                                                                  │
│                       centred (AlignItems Center + Justify Center), padding Spacing.XXL 24       │
│                       loc: common.emptyTitle / common.emptySubtitle                              │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
 REACHED ONLY VIA: seed is null AND the section uri starts "wavee:local:" (HomeSectionPage.cs:85, 98-99)
 → the page issues NO request at all. A stale local route is a quiet empty page, never an error page.
```

### W10 - Section failed (a 400 on a stale persisted hash, a dead session, a capped/failed walk with nothing landed)

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Browse  ›  Weekly Song Charts                                                                    │
│                                                                                                  │
│                             Something went wrong.                        ← ErrorState.Build      │
│                             ▲ PageHero 28/36 w600                          (EmptyState grammar)  │
│                     Check your connection and try again.                 ← TrackMeta 12/16       │
│                                                                                                  │
│                          (no Retry button — HomeSectionPage passes no onRetry)                   │
│                          loc: common.errorTitle / common.errorSubtitle                           │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
 the exception text goes to the log only (ErrorState.cs:19-20), never to the user
```

### W11 - Masthead "Show all" — armed / loading / disarmed

```
 armed                                       loading                              disarmed (exhausted)
 ┌──────────────────────────┐                ┌──────────────────────────┐         ┌────────────────────────┐
 │ Home › Made For You      │                │ Home › Made For You      │         │ Home › Made For You    │
 │                [Show all]│                │                [Show all]│ grey    │                        │
 └──────────────────────────┘                └──────────────────────────┘         └────────────────────────┘
  Button.Subtle / Small: padding (7,2,7,3), MinHeight 24, font 12, radius 4
  bottom-aligned with the title (band AlignItems = End), gap 12 from the title column
  armed  ⇔ !charts && CanPage(section) && !exhausted && HomeSectionPaging.HasMore(section, cursor)
  loading ⇔ loadingMore.Value  → isEnabled:false (WinUI disabled brushes; the label stays)

  ABSENT WHILE THE GRID SHIMMERS. The Pending placeholder is 8 cards with TotalCount 8 / RawItemCount 8, so
  HasMore is false against a null cursor (§0.18) — the band carries the real title and NOTHING at its right
  edge until the first page lands, at which point the button simply appears (the band re-renders; only
  `ToolsVisible` flipped, and there is no motion on that child). The slot is NOT reserved: the title column is
  grow1/basis0, so it gives 12 + the button's width back the instant the button arrives, and a two-line title
  can re-wrap at that moment. ALSO ABSENT on every Charts route (`!charts`) and on an expired `wavee:local:`
  route (`CanPage` false).
```

### W12 - Scrolled: the append gate

```
       ┌──────────────────────── the grid's own Virtual.Custom viewport ─────────────────┐
       │                                                                                 │
       │   OffsetY                                                                       │
 ──────┼─────────────────────────────────────────────────────────────────────────────────┤
       │                                                                                 │  ViewportH
       │  visible rows (+2 rows overscan above and below, AspectGrid.Window)              │
 ──────┼─────────────────────────────────────────────────────────────────────────────────┤
       │                                                                                 │
       │   ◀─────────── 1.5 × ViewportH ───────────▶                                     │
       │   nearTail  ⇔  OffsetY + ViewportH ≥ ContentH − 1.5·ViewportH                   │
       └─────────────────────────────────────────────────────────────────────────────────┘ ContentH

 projection key (coalescing): ((long)(OffsetY / 24) << 20) ^ (long)(ContentH / 48)
   → re-evaluates every 24 DIP of scroll, and again whenever ContentH moves by 48 (an append)
 gate chain: nearTail (C) → 300 ms arm-debounce (B) → !loadingMore (A) → Start() ; max 3 attempts per mount
 after every append: nearTail := false unconditionally — only a FRESH scroll event continues the chain
 (HomeSectionAppendPreloader.cs:14-25, 46-54, 59-95 ; HomeSectionPage.cs:580)

 THE 3-ATTEMPT COLLAPSE IS PER (uri, cursor), AND A FAILING PAGE NEVER RESETS IT. `_attempts` is a field on the
 component instance whose Key is "home-section-append:" + uri + ":" + cursor — a SUCCESSFUL append moves the
 cursor and remounts the preloader with a fresh counter, but a transient fetch failure leaves cursor and
 exhausted untouched (HomeSectionPage.cs:581-590, the "the button stays armed" leg), so the SAME instance keeps
 counting. After the third failure the silent infinite scroll is dead for the life of that page and the
 masthead's "Show all" is the only way to page it — with no visible difference between "nothing more to load"
 and "the tail gave up" (HomeSectionAppendPreloader.cs:29, 68, 91). Shipped behaviour; keep the cap in 0.3 and
 decide whether the collapse deserves a tell.
```

### W13 - Customizer, wide (content pane ≥ ~790) — the column is capped at 720 and LEFT-aligned

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ┌──┐                                                                        ┌─────┐ ┌──────┐      │  64 DIP
│ │◀ │        Your Home                                                       │Reset│ │ Done │      │  command bar
│ └──┘        Customize Home                                                  └─────┘ └──────┘      │
│  28×28      ▲Eyebrow 12/16 w600 +30 tracking AccentTextPrimary               Subtle   Accent      │
│  glyph 14   ▲Title   16/w600 TextPrimary                                     Small    Small       │
│ ◀8▶        ◀8▶  centre column grow1 basis0                                   gap 4        ◀─16─▶  │
├───────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP divider
│                                                                                                   │  StrokeDividerDefault
│ ◀─16─▶                                                                                            │
│   Hidden modules stay in this list so you can bring them back. Drag to reorder.                   │  12 / TextTertiary
│   ▲ maxLines 3                                                            ▲ top pad 12            │  maxLines 3
│                                                     ▼ gap 16                                      │
│   ⠿  Hero                                   ( ●━━ )                                               │  48 DIP row
│   ⠿  Discover Weekly & Release Radar         ( ●━━ )                                              │  48
│   ⠿  Jump back in                            ( ●━━ )                                              │  48
│   ⠿  Recents                                 ( ●━━ )                                              │  48
│   ⠿  Made for you                            ( ●━━ )                                              │  48
│   ⠿  Your top mixes                          (━━● ) ← OFF, row at Opacity 0.55                    │  48
│   ⠿  Radio                                   ( ●━━ )                                              │  48
│   ⠿  Up next                                 ( ●━━ )                                              │  48
│   ⠿  Audiobooks for you                      ( ●━━ )                                              │  48
│   ⠿  Podcasts for you                        ( ●━━ )                                              │  48
│   ⠿  Editors' picks                          ( ●━━ )                                              │  48
│   ⠿  Because you listened                    ( ●━━ )                                              │  48
│                                                                          ▼ bottom pad 20          │
│   ◀────────────────────── column MaxWidth 720 ──────────────────────▶     unused pane width       │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
 row internals: pad (8,0,8,0) · gripper box 12 wide (Icons.GripperBar E76F @12, TextTertiary,
 HitTestVisible=false) · gap 8 · label 14 TextPrimary grow1 basis0 maxLines 1 char-ellipsis · gap 8 ·
 ToggleSwitch (control box MinWidth 154 / MinHeight 40; PILL 40×20 r-circle sits at the box's LEFT edge,
 so the visible pill's right edge is ≈114 DIP inside the row's right edge)

 ⚠ CODE-DERIVED PREDICTION, now with the engine rule that settles it — still verify side by side (§9 trap 5,
   parity item 38), but expect the labels to be INVISIBLE, not merely narrow:
     · `Reorderable.Item` wraps the row in a `BoxEl` with **no Direction and no Grow** — a row whose single
       child therefore arranges at its own measured main size (`Reorderable.cs:329-352`);
     · `BoxEl.AlignItems` defaults to `Stretch` (`Element.cs:451`), so that wrapper DOES fill the 688-DIP
       column — but cross-axis stretch never touches the child's width inside a ROW;
     · in a row measured against a FINITE width, `Basis = 0` suppresses intrinsic width outright — the engine
       states it in so many words: "A row with a finite width is different: Basis=0 is the standard
       'flex: 1 1 0' contract and MUST suppress intrinsic width" (`FlexLayout.cs:590-604`). The grow-1/basis-0
       label therefore contributes 0 to the row's measure and receives 0 of its arrange.
   ⇒ the row measures 8 + 12 + 8 + **0** + 8 + 154 + 8 = 198 DIP, and what is on screen is a gripper and a
   toggle pill with an empty gap between them, twelve times over. The wide-layout drawing above is the
   INTENDED look, not the shipped one, and item 38 exists to say which of the two 0.3 must reproduce. Either
   way 0.3 MUST pass `Grow=1, Shrink=1, MinWidth=0` on the wrapped content exactly as
   `SidebarPaneSlot.cs:96-98, 130-132` does.
```

### W14 - Customizer row: rest / hidden / focused / keyboard-lifted

```
 rest (visible)            hidden (toggle off)        focused (Tab)             keyboard-lifted (Space)
┌─────────────────────┐   ┌─────────────────────┐   ┌─────────────────────┐   ┌─────────────────────┐
│ ⠿ Hero      ( ●━━ ) │   │ ⠿ Hero      (━━● )  │   │╔═══════════════════╗│   │┌───────────────────┐│ α .80
│                     │   │   α = 0.55          │   │║⠿ Hero    ( ●━━ )  ║│   ││⠿ Hero    ( ●━━ )  ││ Elevation.Flyout
│ no fill, no border  │   │   (whole row)       │   │╚═══════════════════╝│   │└───────────────────┘│
└─────────────────────┘   └─────────────────────┘   └─────────────────────┘   └─────────────────────┘
                                                      engine focus ring on       ↑/↓ move the slot,
                                                      the Reorderable.Item       Space commits,
                                                      wrapper (Focusable=true)   Esc / blur cancels
 THE ROW HAS NO HOVER TREATMENT AT ALL — no fill, no plate, no cursor change. Only the toggle,
 the (hit-test-invisible) gripper aside, reacts to the pointer. This is a shipped fact, not an omission
 to "improve" silently: see §9.
```

### W15 - Customizer, drag in progress (row 5 → slot 2)

```
 before lift                  lifted, pointer at slot 2, dwell elapsed (200 ms)
 ┌─────────────────────┐      ┌─────────────────────┐
 │ ⠿ Hero              │      │ ⠿ Hero              │
 │ ⠿ Discover Weekly…  │      │ ⠿ Discover Weekly…  │
 │ ⠿ Jump back in      │  ──▶ │░⠿ Made for you     ░│ ← the DRAGGED row, α 0.4, IN PLACE (DragLift.Stationary)
 │ ⠿ Recents           │      │ ⠿ Jump back in      │ ← displaced, FLIP-slid down 48 DIP
 │ ⠿ Made for you  ░░░ │      │ ⠿ Recents           │ ← displaced, FLIP-slid down 48 DIP
 │ ⠿ Your top mixes    │      │ ⠿ Your top mixes    │
 └─────────────────────┘      └─────────────────────┘

 • NO insertion line (LiveProject=true ⇒ InsertionVisible is false for a same-list lift)
 • NO drag chip: the shell's single preview resolver handles only `WaveeDragKinds.Resource` and
   `SidebarEditPlan.SectionDragKind` (WaveeResourceDrag.cs:305-347), so kind "wavee.home-layout"
   resolves to null and DragChip.Resolve returns no element (DragChip.cs:91-100).
   ⇒ the gesture has NO moving visual. §9 calls this out as the one thing to decide for 0.3.
 • mouse drag threshold 4 px (SM_CXDRAG); RequireDropOnList=true ⇒ a release away from the list CANCELS
 • displacement motion = the wrapper's LayoutTransition.Slide (Reorderable.Item default)
```

### W16 - Customizer with the corrupt banner

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ┌──┐        Your Home                                                       ┌─────┐ ┌──────┐      │
│ │◀ │        Customize Home                                                  │Reset│ │ Done │      │
│ └──┘                                                                        └─────┘ └──────┘      │
├───────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ◀─16─▶                                                                                 ◀─16─▶     │  pad (16,8,16,0)
│ ┌───────────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ ⚠  Home layout could not be read   Your previous file was kept. Start fresh to      ┌───────┐ ✕│ │ MinHeight 48
│ │    ▲16 glyph over    ▲14 w600       replace it, or keep using the default layout.   │ Start │  │ │ Warning severity:
│ │     SystemFillCaution                ▲14 w400            ▲ 12 left margin           │ fresh │  │ │ tinted content root
│ └───────────────────────────────────────────────────────────────────────────────────────────────┘ │ SystemFillCautionBackground
│                                                     ▼ (the banner box has 0 bottom padding)       │ close button 38, glyph 16
│   Hidden modules stay in this list so you can bring them back. Drag to reorder.                   │
│   ⠿  Hero                                   ( ●━━ )       ← the DEFAULT document is what is shown │
│   …                                                          (in memory; the file on disk is kept)│
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
 shown when HomePreferences.Fault != None  AND  the user has not dismissed it this mount
 (the dismiss is a plain bool field + a _bannerEpoch signal bump — see §9 trap 4)
 "Start fresh" → DiscardCorrupt(): file → *.corrupt, .bak/.tmp deleted, writes unblocked,
 layout := Default, LayoutVersion++, immediate Commit  (HomeLayoutStore.cs:213-233, HomePreferences.cs:47-57)
 ✕ close → the banner collapses to Height 0 (the slot itself is unconditional, so nothing remounts)

 ⚠ "START FRESH" CAN FAIL AND STILL LOOK LIKE IT WORKED. The rename/deletes run inside one try; if any of them
   throws (the file is locked, ACLs), `_writesBlocked` is never cleared — it is set false only AFTER the moves
   succeed — and the store records `SaveFault.IoFailure` plus one `home.layout.discard_failed` log line
   (`HomeLayoutStore.cs:217-231`). `HomePreferences.DiscardCorrupt` nevertheless clears `Fault`
   unconditionally, bumps `LayoutVersion` and calls `Commit` — which the still-blocked gate drops on the floor
   (`HomePreferences.cs:47-57`, `HomeLayoutStore.cs:134`). The banner disappears, the list looks healed, and
   nothing the user does from then on is ever persisted, with no surface at all. 0.3 must either re-read
   `WritesBlocked` after the discard and keep the banner up, or say out loud that it keeps this behaviour.
```

### W17 - Customizer, narrow (content pane < ~752)

```
┌───────────────────────────────────────────────┐
│ ┌──┐   Your Home            ┌─────┐ ┌──────┐  │  the command bar never wraps:
│ │◀ │   Customize Home       │Reset│ │ Done │  │  the title column is grow1/basis0/minW0,
│ └──┘   ▲ maxLines 1, char-  └─────┘ └──────┘  │  char-ellipsised; the buttons are shrink0
│        ellipsis on BOTH lines                 │
├───────────────────────────────────────────────┤
│ ◀16▶                                   ◀16▶   │
│  Hidden modules stay in this list so you      │  hint wraps to ≤3 lines
│  can bring them back. Drag to reorder.        │
│                                               │
│  ⠿ Discover Weekly & Rele…   ( ●━━ )          │  the label is the only thing that gives:
│  ⠿ Jump back in              ( ●━━ )          │  maxLines 1 + CharacterEllipsis
│                                               │
│  column = paneW, no longer capped at 720      │
└───────────────────────────────────────────────┘
 the customizer publishes NO shell material ⇒ ContentHost claims it NEUTRAL
 (ContentHost.cs:51-55, 184-186): the customizer always sits on the deterministic ground, never a wash.
```

### W18 - Entry affordance on Home (rendered by `HomePage.GreetingBlock`, defined in this file)

```
 Home, top-right of the greeting / chips chrome row (HomePage.cs:995, 1009-1017)

 ┌─────────────────────────────────────────────────────────────┬──────┐
 │ Good morning, Christos · your daylist                       │ ┌──┐ │
 │ [ All ] [ Music ] [ Podcasts ]                              │ │⋯ │ │ 28×28, r4, Icon(More E712)@14
 │                                                             │ └──┘ │ TextSecondary,
 └─────────────────────────────────────────────────────────────┴──────┘ Interaction.Subtle ramp,
   body column grow1 basis0 minW0                     gap 8            Cursor Hand, Role Button
   (AlignItems = Start; with no body at all the button right-aligns alone)
   ToolTip: "Customize Home"  (home.customize) — 800 ms show, 400 ms reshow, 200 ms between-show

 click → flyout, BottomEdgeAlignedRight, minWidth 200, FocusTrap + LightDismiss, PopupChrome.Popup
 ┌────────────────────────────┐
 │ ✎  Customize Home          │  ← ONE item; Icons.Edit E70F; navigates go("home-customize", title)
 └────────────────────────────┘
 clicking the button again while open CLOSES it (handle.IsOpen check, :222)
```

### W19 - Degenerate routes and titles: the five states the first draft omitted

```
 A. COLD DEEP LINK WITH NO ROUTE ARG   (`home-section:spotify:section:…` typed / restored, Arg null)
    ┌──────────────────────────────────────────────────────────────────────────────┐
    │ Home                                                          ← NO crumb,    │  placeholder Title = " "
    │ ▲ StaticTitle(name, arg) — Loc(nav.home) for home-section,      NO "›"        │  (HomeSectionPage.cs:100)
    │   Loc(browse.homeTitle) "Browse" for browse-section                          │  ⇒ whitespace ⇒ live = null
    │ ┌────┐ ┌────┐ ┌────┐ ┌────┐   the 8-card shimmer runs as usual               │  ⇒ trail = [] (DrillTrail.cs:34)
    └──────────────────────────────────────────────────────────────────────────────┘
    On Ready the band SNAPS to `Home › <real title>` — the crumb appears at the same instant the grid reveals.
    This is the ONE path on which §0.1 ("the title is real from frame one") does not hold, and it is the
    reason parity item 17's deep-link recipe must be run BOTH with and without a title arg.

 B. EXPIRED wavee:local: ROUTE WITH NO ARG
    SectionTitle falls through `Cards.FirstOrDefault()?.Title` (there are none) to `Loc(browse.title)` =
    **"Browse"** — on a HOME-section route (`HomeSectionPage.cs:371-373`). The body is W9's empty state.

 C. MALFORMED ROUTE / NO SERVICES  (`sectionUri.Length == 0`, or `svc is null`)
    The mount effect returns before issuing anything (`HomeSectionPage.cs:139`). The loadable stays
    `Pending(8 blank cards)` FOREVER: a permanent breathing skeleton, no empty state, no error state, no log
    line. 0.3 must decide this deliberately — either refuse the route at the router, or fail the loadable.

 D. A SECTION WHOSE ENTRIES ARE ALL UNSUPPORTED  (`Cards.Count == 0 && UnsupportedCount > 0`)
    `isEmpty` is `Cards.Count == 0 && UnsupportedCount == 0` (`HomeSectionPage.cs:329`), so this is NOT empty:
    Skel.Region takes the CONTENT branch and GridBody renders a virtual grid with zero cells — a blank
    content column under a real masthead, with no copy at all. Deliberate (the section did return items, we
    just cannot draw them) but undocumented; 0.3 should keep the predicate verbatim and say so.

 E. LEGACY SYNTHETIC RECENTS ROUTE
    `home-section:spotify:list:recents:main` persisted in an old document / history is rewritten to `recents`
    by ContentHost BEFORE the home-section arm can claim it (`ContentHost.cs:193-194`,
    `NavRouteNormalizer.LegacyRecentsRoute`). This page never mounts for it, and `PublishesShellMaterial`
    lists the legacy name explicitly (`ContentHost.cs:185`) so the wash hand-over still works across the
    rewrite. Port the normalizer with the route table.
```

### W20 - Customizer degenerate states the first draft omitted

```
 A. NO `HomePreferences` IN CONTEXT  (`UseContext(HomePreferences.Slot)` → null)
    The page still renders: `modules` falls back to `HomeLayoutDoc.Default.Modules`
    (`HomeCustomizerPage.cs:53-54`), so the reader sees the 12 default rows, every toggle ON. Every gesture is
    a no-op — `prefs?.Dispatch(...)` on the toggle (`:282`) and on Reset (`:127`), `prefs?.DiscardCorrupt()`
    on the banner — and `Banners()` returns the quiet Height-0 slot because the pattern match on a null prefs
    fails (`:140`). Unreachable in the shipped shell (Services provides the instance), but the null-shape is
    what 0.3 must keep: a customizer with no store is a READ-ONLY default list, never a crash and never an
    error page.

 B. A DOCUMENT HOLDING KINDS THIS BUILD DOES NOT LAY OUT
    `HomeLayoutWire.Read` routes both an UNPARSEABLE kind and a parseable-but-not-fixed-landing kind
    (`shelf`, `topic`, `sectionEntry`) into the forward-compat carry rather than into `Modules`
    (`Persistence/HomeLayoutDoc.cs:82-86`). They are re-emitted at their original index on the next save
    (`:129-139`) and they NEVER get a row. The customizer's list is therefore always exactly the fixed-landing
    set — 12 today, plus any future fixed kind, appended VISIBLE at the end (`:95-97`). The row count does not
    vary with the account, the feed, or the file.

 C. `Reset` ON AN ALREADY-DEFAULT DOCUMENT STILL WRITES
    `ResetHomeLayout` returns `Ok(HomeLayoutDoc.Default)` unconditionally — there is no `NoChange` arm
    (`HomeLayoutReducer.cs:21`) — so `Dispatch` sees `Changed`, bumps `LayoutVersion`, and commits a fresh
    file. Visually nothing moves; on disk the mtime and `updatedAtMs` change every time. (Hide/show and move
    DO reject their no-ops: `:41-42`, `:59-60`.)

 D. A MOVE WHOSE TARGET IS THE SOURCE
    `DoMove` removes first, then clamps `ToIndex` into `0..Count`, and rejects only when the clamped index
    equals `FromIndex` (`HomeLayoutReducer.cs:55-61`). A drag that ends on its own slot therefore writes
    nothing — no version bump, no file write, no FLIP — which is why a cancelled-looking drag sometimes leaves
    no trace in the log at all.
```

---

## 3. Tokens

### 3a. Section page

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| page root | fill | pad `(36, 100, 36, 16)`, gap `Spacing.L` 16 | — | — | none (transparent) | — | HomeSectionPage.cs:336-339 |
| body column | fill | gap `Spacing.M` 12 | — | — | — | — | :342-346 |
| masthead reserve | 84 = `Spacing.XXXL` 32 + 52 | +`Spacing.L` 16 → body top 100 | — | — | — | — | BrowseMastheadMetrics.cs:12-17 |
| masthead band | h = 32 + 52 | pad `(36, 32, 36, 0)`, gap 12 | — | `Ui.TitleLarge` 40/52 w400, Display face, tracking −12 | `Tok.TextPrimary` | none (paints nothing) | ShellMastheadBand.cs:65-73, WaveeType.cs:146-151 |
| crumb | — | gap `Spacing.S` 8 | — | same 40/52 | `Tok.TextTertiary`, hover `Tok.TextSecondary`, pressed `Tok.TextTertiary` | — | ShellMastheadBand.cs:90-101 |
| crumb separator `›` | — | — | — | same 40/52 | `Tok.TextTertiary` | — | :101 |
| "Show all" button | MinHeight 24 | pad `(7, 2, 7, 3)` | `Radii.Control` 4 | 12 | `ButtonAppearance.Subtle` | — | ShellMastheadBand.cs:60-61, ControlSize.cs:41 |
| grid viewport | grow1, MinHeight 0 | cell gap `HomeModuleLayout.GridGap` = `Spacing.M` 12 | — | — | — | — | HomeModules.cs:459-463, :510 |
| grid cell | `cellW × (cellW + chrome)` | — | — | — | — | — | AspectGridVirtualLayout, aspect 1 |
| cell chrome (1 title + meta) | 52 | — | — | — | — | — | HomeModules.cs:513 |
| cell chrome (2 titles, no meta) | 54 = 14 + 2×20 + 0 | — | — | — | — | — | :522, :533-536 |
| card cover | `cellW − 16` square | card pad `(8, 8, 8, 12)` | `Radii.Card` 8 (Artist: `Radii.Full`) | — | `Surfaces.ArtworkFill` | — | MediaCard.cs:231-253 |
| card cover→label gap | 8 (`MediaCard.Pad` = `Spacing.S`) | — | — | — | — | — | MediaCard.cs:28, 252 |
| card title | line box 20 | label gap `Spacing.XXS` 2 | — | `WaveeType.TrackTitle` = `Ui.BodyStrong` 14/20 w600 | `Tok.TextPrimary` | — | MediaCard.cs:268-273 |
| card metadata | line box 16 | — | — | `WaveeType.TrackMeta` = `Ui.Caption` 12/16 | `Tok.TextSecondary` | — | MediaCard.cs:274-275 |
| card hover plate | fills the cell | — | 8 | — | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | `Elevation.Card` | MediaCard.cs:83-98 |
| card "⋯" corner | 30×30 | inset `(0, 8, 8, 0)` | circle | glyph 13 | `WaveeOnMedia.ScrimRest/Hover/Pressed`, ink `WaveeOnMedia.Ink`, border 1 `WaveeOnMedia.Stroke` | `Elevation.Card` | MediaCard.cs:108-133 |
| card play FAB | 44 | inset 8 (bottom-**right** of the cover) | circle | glyph `Icons.Play E768` / `Icons.Pause E769` when this card owns playback | see `02-cards-and-controls.md` | — | MediaCard.cs:26, 243, 1407-1436 |
| card label block alignment | — | — | — | — | `AlignItems = Start`, **`Center` for a circular (Artist) card** | — | MediaCard.cs:259 |
| card cover clip | — | — | — | — | `ClipToBounds = !circular` (the Artist overlay layer stays rectangular so the FAB / "⋯" are not clipped by the avatar circle) | — | MediaCard.cs:233-235 |
| **now-playing** equalizer pill | 3 bars, run height 14 | pad `Spacing.XS` 4 all; inset 8 / 8 from the cover's bottom-**left** | `Radii.ControlAll` 4 | — | plate `WaveeOnMedia.ScrimRest`, bars `Tok.AccentTextPrimary` (no `WaveeAccentCtx` on this page) | — | MediaCard.cs:1504-1520, Equalizer.cs:23-34 |
| **Charts** toolbar row | MinHeight 32, grow0 shrink0, AlignSelf Stretch | gap `Spacing.M` 12 | — | — | — | — | HomeSectionPage.cs:294-298 |
| **Charts** filter box | 300 × ≥32 | — | `Radii.Control` 4 | placeholder 14 | stock `AutoSuggestBox` | — | :35-36, :303-305 |
| **Charts** walk-bar slot | 160 × ≥3, AlignSelf Center | — | — | — | — | — | :39-40, :310-317 |
| **Charts** progress bar | 160 × 3 band (a hard `Height = MinHeight`); track 1 px vertically centred, indicator full 3 | — | 1.5 / track 0.5 | — | track `Tok.StrokeControlStrongDefault`, indicator `Tok.AccentDefault` | — | ProgressBar.cs:36-39, 97-124 |
| **Charts** highlight pill | run height 20 | pad `(3, 1, 3, 1)` | `Radii.Control` 4 | 14/w600 | fill `Tok.AccentSelectedTextBackground`, ink `Tok.TextOnAccentSelectedText` | — | SearchHighlight.cs:42-54 |
| empty / error block | grow1 | pad `Spacing.XXL` 24 all, gap `Spacing.XS` 4 | — | `PageHero` = `Ui.Title` 28/36 w600 + `TrackMeta` 12/16 | `Tok.TextPrimary` / `Tok.TextSecondary` | — | EmptyState.cs:47-72 |
| no-match block | grow1 | same | — | `Ui.Subtitle` 20/28 w600 + `TrackMeta` | same | — | EmptyState.cs:43-45 |
| skeleton bar | derived from the real tree | row gap 8 | 4 | — | `Tok.FillSubtleSecondary` | — | SkeletonRegion.cs:34-38 |

### 3b. Customizer

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| page root | fill, `ClipToBounds` | — | — | — | none (neutral shell ground) | — | HomeCustomizerPage.cs:74-83 |
| command bar | Height 64, shrink0 (a **hard** `Height`, not `MinHeight` — §9 trap 11) | pad `(8, 0, 16, 0)`, gap `Spacing.S` 8 | — | — | none | — | :21, :91-93 |
| back button | 28×28, glyph 14 | — | `Radii.Control` 4 | — | `IconButton.DefaultStyle` (transparent → `FillSubtleSecondary` → `FillSubtleTertiary`) | — | :102, IconButton.cs:74, 53-64 |
| eyebrow | line box 16 | — | — | `WaveeType.Eyebrow` 12/16 w600, `CharSpacing` +30 | `WaveeAccent.Decor` = `Tok.AccentTextPrimary` | — | :111-114 |
| page title | LineHeight font-natural (≈23 at 16) | — | — | raw `TextEl` 16 / w600 — **off-ramp** (§9) | `Tok.TextPrimary` | — | :115-119 |
| Reset button | MinHeight 24 | pad `(7, 2, 7, 3)`; cluster gap `Spacing.XS` 4 | 4 | 12 | `ButtonAppearance.Subtle` | — | :124, :127-128 |
| Done button | MinHeight 24 | same | 4 | 12 | `ButtonAppearance.Accent` | — | :129-130 |
| divider | Height 1, stretch | — | — | — | `Tok.StrokeDividerDefault` | — | :78, Factories.cs:177-179 |
| banner slot (quiet) | Height 0, shrink0 | — | — | — | — | — | :140-141 |
| banner slot (fault) | auto, shrink0 | pad `(16, 8, 16, 0)` | — | — | — | — | :143-147 |
| InfoBar | MinHeight 48 | content root pad `(16,0,0,0)`; icon margin `(0,16,14,16)`; panel margin `(0,0,16,0)` | overlay 8 | title 14 w600 / message 14 w400 | icon **background circle** (`Icons.InfoBarBackgroundCircle` F136) = `Tok.SystemFillCaution`, **glyph ink** = `Tok.TextInverse`, status glyph `Icons.StatusWarning`; content-root plate `Tok.SystemFillCautionBackground` | — | InfoBar.cs:65-70, 92-95, SeverityVisuals.cs:22 |
| InfoBar close | 38, glyph 16 | margin 5 | 4 | — | subtle ramp | — | InfoBar.cs:69-70, 95 |
| scroll column | grow1, `MaxWidth` 720 | pad `(16, 12, 16, 20)`, gap `Spacing.L` 16 | — | — | — | AutoEdgeFade band 40 (`Reconciler.cs:4232`), alpha runway 24 (`SceneRecorder.cs:3486` `EdgeCueRunwayPx`) | :22, :160-176 |
| hint text | maxLines 3 | — | — | raw `TextEl` 12 — **off-ramp** (§9) | `Tok.TextTertiary` | — | :166-169 |
| row | Height 48 (`RowExtent`; a **hard** `Height` — §9 trap 11) | pad `(8, 0, 8, 0)`, gap `Spacing.S` 8 | — | — | none (no fill, no hover) | — | :23, :264-268 |
| row, hidden | same | same | — | — | `Opacity` 0.55 on the whole row | — | :268 |
| gripper | box 12, glyph 12, `HitTestVisible=false` | — | — | `Icons.GripperBar` U+E76F | `Tok.TextTertiary` | — | :271-276 |
| row label | grow1 basis0 shrink1 minW0, maxLines 1 | — | — | raw `TextEl` 14 w400 — **off-ramp** (§9) | `Tok.TextPrimary` | — | :277-281 |
| toggle | control box MinWidth 154 / MinHeight 40; track 40×20; knob 12 rest / 14 hover / 17×14 pressed; travel 20 | content gap 12 | track = circle(20) | — | off `Tok.FillControlAltSecondary` + 1 px `Tok.StrokeControlStrongDefault`, knob `Tok.TextSecondary`; on `Tok.AccentDefault`, knob `Tok.TextOnAccentPrimary` | — | :282, ToggleSwitch.cs:57-72, 105-112, 340-360 |
| lifted row (keyboard) | — | — | — | — | `Opacity` 0.80 | `Elevation.Flyout` | Reorderable.cs:83, 348-349 |
| dragged row (pointer) | — | — | — | — | `Opacity` 0.40 (`Drag.SourceDimOpacity`) | none (Stationary lift) | :29, DragDropFacade.cs:24 |
| entry button | 28×28, glyph 14 | — | `Radii.ControlAll` 4 | — | `Interaction.Subtle`: transparent → `Tok.FillSubtleSecondary` → `Tok.FillSubtleTertiary`, 83 ms brush fade | — | :237-246, Interaction.cs:132-135 |
| entry flyout | minWidth 200 | — | overlay 8 | — | `PopupChrome.Popup` | flyout acrylic/shadow (engine) | :228-233 |

---

## 4. Colour & material

**The page's wash (section page only).** Input → function → application → transition:

1. **Source card.** `WashCard(section)` scans `min(Cards.Count, 32)` in order and returns the **first** card with
   `Meta.Accent != 0` *or* a non-empty `Image.Url` (`HomeSectionPage.cs:32, 359-369`). Skeleton cards carry
   neither, so a *loading* section publishes nothing and the shell shows its bare ground — deliberate.
2. **Gate.** `svc.Settings.Get(WaveeSettings.ColorWashesEnabled)`; `AppearancePrefs.Epoch.Value` is read so the
   Settings toggle applies live (`HomeSectionPage.cs:168-171`).
3. **Resolution.** `HomeWashSource.Pick(card, Surfaces.ChromeSchemeFor)` (`HomeWashSource.cs:59-71`):
   - tier 1 — `WaveePalette.Lift(WaveePalette.ToColor(Meta.Accent))` with `A = 1` (the server's
     `extractedColors.colorDark`, lifted because the raw tone is near-black);
   - tier 2 — `WaveePalette.ChromeAccent(CoverColorPlane scheme)` with `A = 1`;
   - tier 3 — **does not exist**: null leg, no app-accent fallback.
4. **Plane watch.** Exactly ONE artwork is watched: `SpotifyLive.CoverColorPlane.Current.Watch(PlaneUrl(washCard))`
   (`HomeSectionPage.cs:174-175`) — never the plane's global epoch, which this page's own realized card batches bump.
   And only when there is something to wait FOR: `PlaneUrl` returns null for a card that already carries a payload
   accent and for a card with no image at all (`HomeWashSource.cs:78-79`), so a tier-1 section takes no watch.
5. **Publication.** `new HomeWash(new WashLayer(color, key), null, null)` — **the Hero leg only**
   (`HomeSectionPage.cs:177`). `tint: null`. Claim-on-first-publish + claim-on-reactivation, refresh-while-owner,
   never clear (`ShellMaterial.Publish`, `App/ShellMaterial.cs:47-58`; effect dep =
   `HashCode.Combine(washesDisabled, pick.Key, pick.Color.RGB)` at `HomeSectionPage.cs:183-188`).
6. **Where it lands.** `ShellWashGeometry.Hero`: an ellipse centred at `(0.06, 0.00)` of the window with radii
   `(0.74, 0.92)`, faded to α 0 at gradient offset `0.62`; origin alpha `0.10` dark / `0.055` light
   (`ShellWashGeometry.cs:24-25, 33-37`). It composites over live Mica in the chrome bands; the content pane above
   it is opaque, so the *page* never depends on the wallpaper (D29).
7. **Transition.** The layer node is keyed on `HomeWashPick.Key` (the size-independent artwork key, else the card
   uri). A **different** artwork ⇒ remount ⇒ cross-fade. The **same** artwork re-graded (accent → landed plane)
   ⇒ recolours in place, no fade (D23, DEFECT_REGISTER.md:30).
8. **Washes off** ⇒ the page publishes `definite: true` with no layers ⇒ the shell eases to its neutral ground.

**Customizer materials.** None. `ContentHost.PublishesShellMaterial` excludes `"home-customize"`
(`ContentHost.cs:184-186`), so the boundary claims neutral and the page sits on the deterministic ground in both
themes. The page paints no fill of its own; the only coloured surfaces are the accent `Done` button, the accent
eyebrow, the accent toggle track when on, and the caution-tinted InfoBar.

**Light / dark.** Everything above is token-resolved; the only hard-coded theme split on this surface is the
wash's origin alpha (0.10 dark / 0.055 light) and the alpha-lift inside `WaveePalette`. Nothing here holds a
literal colour.

**On-media ink.** Only inside the grid cell (the "⋯" corner and the play FAB use the `WaveeOnMedia` ladder) —
specified in `02-cards-and-controls.md`.

---

## 5. Motion

All durations are engine tokens; all animation reads frame time through the engine `FrameClock` (`AnimEngine`
tracks), never `Environment.TickCount64`. **One exception, and it is inherited, not authored:**
`Reorderable.OnDelta` advances the live-reorder dwell from `Environment.TickCount64`
(`Reorderable.cs:396, 407-410`) — a wall-clock delta between pointer events, not a per-frame sample. Port it as is
or drive `Advance(dtMs)` from the frame clock with `AutoDwell = false`; do not silently change the 200 ms feel.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| navigate **to** the section page (forward) | page root | X + Opacity | +8 DIP, α0 → 0, α1 | `Expressive.Fast` 250 | `Easing.SmoothOut` | 90 ms after the exit starts | engine `ReducedSnap` | PageNavMotion.cs:52-59 |
| navigate **away** | outgoing page root | Opacity | 1 → 0 | 120 | `Easing.EaseOut` | 0 | — | PageNavMotion.cs:57 |
| navigate back | page root | X + Opacity | −8 DIP, α0 → 0, α1 | 250 | `SmoothOut` | 90 | — | PageNavMotion.cs:61-68 |
| masthead enters the family | band root | Opacity | 0 → 1 | 120 | `Easing.SmoothOut` | 0 | `KeepFade` | ShellMastheadBand.cs:25-26, 71 |
| masthead leaves the family | band root | Opacity | 1 → 0 | 120 | `Easing.FluentAccelerate` | 0 | `KeepFade` | ShellMastheadBand.cs:23-24 |
| Pending mounts | derived shimmer root | Opacity | 1 ⇄ 0.5 loop | 1000 per cycle | engine pulse | — | ignored (no pulse) | SkeletonRegion.cs:35, Reconciler.cs:1477-1479 |
| Pending → Ready | shimmer orphan | Opacity | 1 → 0 | `Expressive.Fast` 250 (`SkeletonStyle.ExitMs`) | engine | 0 | swap snaps | SkeletonRegion.cs:36 |
| Pending → Ready | GridBody's 2 direct children | Opacity + TranslateY + BlurSigma | α0→1, +8→0, σ3→0 | `Expressive.VerySlow` 500 | `Easing.SmoothOut` | `Expressive.Stagger` 40 × index | `SoftReveal` returns early — **no reveal at all** | SkeletonRegion.cs:169-186, MotionRecipes.cs:54-70 |
| Ready → Ready (an appended page) | — | — | — | — | — | — | — | **no motion**: `SetReady` on an already-Ready region only re-renders the content; the reveal fires on the branch EDGE only (Reconciler.cs:1481) |
| cell pointer-enter | card plate | Opacity | 0 → 1 | `MotionTok.ControlFaster` 83 | `ControlFaster` easing | 0 | engine token policy | MediaCard.cs:94-96 |
| cell pointer-enter | card root | OffsetY | 0 → −4 | `MotionTok.ControlNormal` | token | 0 | token policy | MediaCard.cs:70-74 |
| cell press | card root | Scale + OffsetY | 1 → 0.99, 0 → −1 | `ControlNormal` | token | 0 | token policy | MediaCard.cs:72 |
| cell pointer-enter | "⋯" corner | Opacity | 0 → 1 | `WaveeMotion.Fast` | `Easing.FluentDecelerate` | 0 | token policy | MediaCard.cs:113 |
| cell pointer-enter | play FAB wrapper | Opacity | 0 → 1 | `WaveeMotion.Fast` | `Easing.FluentDecelerate` | 0 | token policy | MediaCard.cs:1428-1436 (engine `HoverOpacity` off the CARD's hover — the wrapper is non-interactive, so the FAB stays MOUNTED at all times and hover only reveals it) |
| **card relates to the playing context** | equalizer bars (×3) | ScaleY | pattern loop, phase-staggered per bar | **850 ms** loop, ticked at ~30 Hz by ONE host `UseInterval` | pixel-quantized bound Transform (not an anim-slab keyframe) | per-bar phase offset | engine interval policy | Equalizer.cs `EqHost.LoopMs = 850f`, `TickMs = 1000/30` |
| playback pauses while the card still relates | equalizer bars | ScaleY | → settled low, ticker off | — | — | — | — | MediaCard.cs:1473-1477 (`playing = active && IsPlaying`) |
| this card **owns** playback, play↔pause | FAB glyph | glyph swap | `Icons.Play E768` ⇄ `Icons.Pause E769` | none (a re-render, no tween) | — | — | — | MediaCard.cs:1407, 1476 |
| equalizer mount / unmount (a track change elsewhere) | equalizer slot | — | — | none — the ZStack keeps ONE shape `[eq slot, FAB]` on both legs so the FAB's node survives a playback edge mid-press | — | — | — | MediaCard.cs:1338-1348 |
| Charts walk publishes a page | progress indicator | Width (bound `FloatSignal`) | previous fraction → new | none (a bound width, re-resolved on the signal write, no tween) | — | — | unchanged | ProgressBar.cs:78-85, HomeSectionPage.cs:378-379 |
| Charts walk terminates | `walkFrac` | value | → 1 then the bar unmounts | — | — | — | — | HomeSectionPage.cs:405, 427, 447, 453, 466 |
| Charts filter keystroke | grid | content swap | — | none (`Skel.Region(smoothResize: false)` — no eased resize) | — | — | — | HomeSectionPage.cs:328 |
| column count changes (resize) | grid cells | relayout | — | none (no FLIP: the cell keys carry the tier, so a column change REMOUNTS the cells) | — | — | — | HomeModules.cs:454-456 |
| scroll | grid viewport | offset | — | engine scroll integrator (see `00-design-system.md`) | — | — | — | — |
| **customizer** page enter/exit | page root | same page fade-through as above | — | 250 / 120 | `SmoothOut` / `EaseOut` | 90 / 0 | — | PageNavMotion.cs:52-68 |
| toggle flip | toggle track fill | brush | off ⇄ on ramp | `Motion.ControlFaster` 83 | engine brush fade | 0 | token policy | ToggleSwitch.cs:360 |
| toggle flip | knob | Position (FLIP) | 0 → 20 | 167 travel / 83 size / 250 disabled | engine per-commit dynamics | 0 | token policy | ToggleSwitch.cs:325-337 (and its class doc) |
| toggle hover / press | knob | Width/Height | 12 → 14 / 17×14 | 83 (size dynamics) | engine | 0 | token policy | ToggleSwitch.cs:276-277 |
| row hidden ⇄ visible | row root | Opacity | 1 ⇄ 0.55 | **none — a hard swap** (a plain `Opacity` prop, no `Transition`) | — | — | — | HomeCustomizerPage.cs:268 |
| reorder: pointer drag past a midpoint | displaced siblings | Position (FLIP) | old slot → new slot | `LayoutTransition.Slide` (engine default) | engine spring | after the **200 ms** dwell (`ReorderList.ListDwellMs`) | engine policy | Reorderable.cs:329-351, ReorderList.cs:44 |
| reorder: lift (pointer) | dragged row | Opacity | 1 → 0.40 | engine drag promote | — | 4 px drag threshold first | — | HomeCustomizerPage.cs:29, DragDropFacade.cs:24 |
| reorder: lift (Space) | dragged row | Opacity + Shadow | 1 → 0.80, none → `Elevation.Flyout` | engine | — | 0 | — | Reorderable.cs:348-349 |
| reorder: drop | dragged row | Position | pointer → the committed slot | L1 drop-glide + the commit's FLIP retarget (`SettleOnDrop`) | engine spring | 0 | — | Reorderable.cs:107-112 |
| reorder: cancel (Esc / blur / release off-list) | dragged row + siblings | Position | → home | FLIP | engine spring | 0 | — | Reorderable.cs:423-448 |
| entry flyout open | popup | engine flyout recipe | — | see `19-shell-overlays.md` | — | — | — | HomeCustomizerPage.cs:228-233 |
| tooltip (back button, entry button) | tooltip | show | — | 800 ms show / 400 ms reshow / 200 ms between-show window | engine | — | — | ToolTip.cs:91-102 |
| navigate with `NavTransitionKind.Neutral` (a tab activation, a restored tab) | page root | Opacity only | `MotionRecipes.PageFade` — **no slide, no 90 ms delay** | engine | — | — | — | PageNavMotion.cs:43 |
| navigate to/from a module (watch) page | page root | Position only | ±8 DIP, **no opacity at all** (a composited video is a DestOut hole an ancestor opacity erases) | 250 | `SmoothOut` | 0 | — | ContentHost.cs:147-153, PageNavMotion.cs:105-121 |

**No countdown and no scroll-linked effect on either surface, and the Charts progress bar is driven by *arrivals*,
not by time.** There **is** one always-on ticker: the now-playing equalizer on any grid cell that relates to the
playing context (rows above; W5b). An earlier draft of this chapter claimed "no equalizer" — that was wrong, and
0.3 must carry the equalizer, its 850 ms loop and its bottom-left placement across.

---

## 6. Interaction

### 6a. Section page

**Pointer.**
- Hover a cell → plate reveal + −4 lift, play FAB and "⋯" corner fade in (`02-cards-and-controls.md`).
- Click a cell → `HomeCardNav.Open(card, navPreview, go, PlayTrack)` (`HomeSectionNavigation.cs:42-80`):
  `Liked` → `go("liked")` · `Track`/`Episode` → **plays** (`PlayTrackAsync(card.Uri)`) · `Artist` →
  `go("artist:<uri>", title)` · `Album` → `DetailNav.OpenAlbum` with a partial `Album` stash ·
  `Podcast`/`Audiobook` → `go("show:<uri>", title)` · default → `DetailNav.OpenPlaylist` with a partial
  `PlaylistSummary` carrying `OwnerName` (never `Subtitle`), the daylist window and the accent.
- Click the play FAB → **two different verbs, decided by ownership.** `NowPlayingOverlay.Toggle`
  (`MediaCard.cs:1387-1400`) peeks the playback identity: if this card **owns** playback
  (`NowPlayingMatch.OwnsPlayback`) it toggles `IsPlaying` optimistically and calls `Pause/ResumeAsync`;
  otherwise it falls through to the host's `onPlay` = `PlayGridCard`:
  `HomeCardPlayRouting.PlaysAsItem(kind)` ⇒ `PlayTrackAsync(uri)`, else `PlayAsync(uri, 0)`
  (`HomeModules.cs:470-475`). A track uri is not a context; sending every kind through `PlayAsync` is the bug
  this split fixed. An **album** card of the playing track relates (moving equalizer) but does not own, so its
  FAB keeps ▶ and its click starts the album.
- **Hover is movement-armed.** `OnPointerMoveWithin` + `HoverMotionGate` (`MediaCard.cs:228, 285-287`); a cell
  that appears under a stationary cursor does not arm. See W5.
- Click a masthead crumb → `go(crumb.RouteName, crumb.RouteArg)`. The last crumb is never clickable.
- Click "Show all" → `LoadMore(currentSection)` — one page, from the raw cursor.
- **Right-click a cell** → `Menus.CardAttach` → `Menus.Card`, keyed by `EntityUri.KindOf` (full grammar in
  `02-cards-and-controls.md` / `Actions/Menus.cs:544-602`). Order for a playlist/album/artist card:
  1. header (cover · name · `subtitle` flattened through `SpotifyExportMapper.ToPlainText`, else the kind label);
  2. transport strip: **Play** · [**Play next** · **Add to queue** — only when the kind has a resolvable track set]
     · [**Save/Unsave** — omitted for Liked Songs];
  3. rows, in grammar order: [**Follow/Unfollow** — artist only] · [**Add to playlist ▸**] · **Open** ·
     [**Pin/Unpin** — only when the uri is pinnable] · [**Go to artist** — album only] · **Share ▸** ·
     [**Go to radio** — artist only].
  A show/podcast card gets Play · Open · [Pin] · Share ▸. An episode uri gets **no menu**.
  The circular flag follows `card.Kind == Artist` (`HomeModules.cs:443`).
- **Drag a cell** → `Drag.Source(WaveeDragKinds.Resource, WaveeResourceDragPayload.ForEntity(...))` — for every
  kind **except** `Track` and `Episode`, which are not draggable here (`HomeModules.cs:444-447`). The chip is the
  shell's standard resource chip.
- **Charts filter box** → each keystroke re-derives `shown` inside the `Responsive.Of` closure; the grid
  re-renders, the matched run gets the accent pill, and zero hits shows `EmptyState.Compact`. `NoSuggest = []`
  ⇒ the suggestion popup never opens, so Esc has nothing to close. There is **no clear ✕ and no Clear button**:
  `AutoSuggestBox` never sets `ShowDeleteButton` (only `TextBox` does — `TextBox.cs:82-85`,
  `EditableText.cs:399`), so the filter is unwound by selecting and deleting the text. The filter is page-local
  and is **not** reset by an append or by the walk finishing; it survives until the page unmounts.

**Keyboard.** No page-level accelerators. The grid is a `Virtual.Custom` viewport (arrow/page keys drive the
scroller); cards are `Focusable` through `CardShell`'s `OnClick`. The masthead crumbs and "Show all" are
`Role = Button` / `Focusable`.

**Scroll.** The grid's own viewport. `nearTail` re-projects every 24 DIP / 48 DIP of content growth; see W12.
No `AutoEdgeFade` on the section grid (the grid is not wrapped in a `ScrollEl` that declares it). The grid's own
scrollbar is **not** suppressed during the shimmer either: the engine's skeleton leg hides the rail of the
region's nearest scroll ANCESTOR (`Reconciler.cs:1471`, `SetSkeletonScrollbarSuppression`, `:2735-2756`), and
this page deliberately has none (§0.5) — so unlike every scrolling page in the app, the shimmer here keeps a
live rail whose thumb is sized for 8 blank cards. Wrapping the grid in a page-level `ScrollView` in 0.3 would
silently switch that leg on, which is a second reason not to.

**Tooltips.** None on the section page itself (the cards' own tooltips are in `02-`).

**Accessibility names.** The cards carry `Role = Button` via `CardShell`; the masthead crumbs carry
`Role = AutomationRole.Button`; the "⋯" corner and the play FAB each carry their own `Role = Button`
(`MediaCard.cs:130`, and the FAB's tooltip is Play/Pause). The Charts walk bar carries
`Role = AutomationRole.ProgressBar` (`ProgressBar.cs:102`) and is therefore the **only** announced progress on
this page. The grid itself exposes no live region — the append is silent by design, which is a deliberate
accessibility gap, not an omission: nothing tells a screen-reader user that twenty more cards arrived.

### 6b. Customizer

| gesture | result | file:line |
|---|---|---|
| click **◀** (tooltip "Back") | `GoBack()`: flush writes ≤200 ms → `HistoryStore.BackCtx` if present → else walk the history log backwards for the first entry whose route is not `home-customize` → else `go("home")` | :101-104, :178-193 |
| click **Reset** | `Dispatch(new ResetHomeLayout())` → `HomeLayoutDoc.Default` (all 12 modules, designed order, all visible). **No confirmation.** | :127-128, HomeLayoutReducer.cs:21 |
| click **Done** | identical to Back | :129-130 |
| flip a row toggle | `Dispatch(new SetHomeModuleHidden(kind, !v))`; the reducer rejects a no-op (`NoChange`) and an unknown kind (`UnknownModule`); a kind absent from the document is **appended** (subject to `MaxModules` 24 → `CapReached`) | :282, HomeLayoutReducer.cs:26-47 |
| press-and-drag a row ≥4 px | lift; slot math = WinUI midpoint rule; displacement after a 200 ms dwell; commit on release **over the list** (`RequireDropOnList`) → `Dispatch(new MoveHomeModule(from, to))` where `to` is interpreted **after** the removal | :30, :60, Reorderable.cs:229, 413-439, HomeLayoutCommands.cs:27-33 |
| release a drag **off** the list | cancel — nothing is committed | Reorderable.cs:423-429 |
| **Space** on a focused row | keyboard lift (α 0.80 + `Elevation.Flyout`) | Reorderable.cs:508-541 |
| **↑ / ↓** while lifted | move the insertion slot, no dwell; re-render → FLIP | Reorderable.cs:523-530 |
| **Space** while lifted | commit at the shown slot | Reorderable.cs:543-549 |
| **Esc** while lifted, or focus loss | cancel | Reorderable.cs:517-521, 344 |
| click the InfoBar **✕** | dismiss for this mount (a plain `bool` field + `_bannerEpoch` bump) | :153 |
| click **Start fresh** | `HomePreferences.DiscardCorrupt()` → `*.corrupt` rename, `.bak`/`.tmp` deleted, writes unblocked, layout := Default, version++, immediate commit | :154-155, HomeLayoutStore.cs:213-233 |
| hover a row | **nothing** — the row has no hover treatment | :264-268 |
| click the gripper | falls through: the gripper box is `HitTestVisible = false` | :274 |

**The banner is a LOAD fault only, and it is one sentence for three faults.**
`HomeLayoutLoadFault` has three non-`None` values — `Corrupt`, `TooNew`, `Unreadable`
(`HomeLayoutStore.cs:14`) — and `Banners()` renders the SAME `home.customizer.corrupt` /`…corruptSub` copy for
all three (`HomeCustomizerPage.cs:140, 149-156`). `HomePreferences.FaultDetail` — the store's own sentence
("Layout version 2 is newer than supported version 1.", "The Home layout contains invalid data (JsonException).")
— is captured (`HomePreferences.cs:32, 65`) and **never shown**. Two further silent paths:

- **A valid `.bak` recovers silently — but only for a MALFORMED primary.** The `.bak` re-read sits AFTER the
  switch, on the fall-through that only `ReadOutcome.Malformed` reaches; `TooNew` and `Unreadable` return from
  inside the switch with writes already blocked and never look at the backup at all
  (`HomeLayoutStore.cs:59-86`). So a too-new file with a perfect `.bak` beside it still shows the banner and
  still refuses to save, while a truncated one silently heals with a single `home.layout.recovered` log line and
  `Fault = None`. Note also the two classifications that are easy to miss: a MISSING file is not a fault at all
  (`:57` — first run), and a document whose `version <= 0` is `Malformed`, i.e. it takes the `.bak` path and
  then the corrupt banner (`:115-119`).
- **"Start fresh" can fail and leave the UI looking healed.** See W16: a throwing rename keeps `WritesBlocked`
  true while `HomePreferences.DiscardCorrupt` clears `Fault` regardless, so the banner goes and nothing is ever
  written again (`HomeLayoutStore.cs:217-231`, `HomePreferences.cs:47-57`).
- **Every rejected command is invisible.** `Dispatch` returns a `HomeLayoutRejectReason` (`NoChange`,
  `UnknownModule`, `CapReached`) that both call sites discard (`HomeCustomizerPage.cs:127, 282`), and the
  localized undo labels (`home.customizer.undo.*`, `en-US.json:587-592`) are wired to nothing. There is no undo,
  no toast, no confirmation on Reset — see §0.20.
- **`HomeLayoutSaveFault` has no UI at all.** `DocumentTooLarge` (> 256 KiB) and `IoFailure`
  (`HomeLayoutStore.cs:18, 159-200`) are surfaced only as `home.layout.save_failed` log lines. A toggle whose
  write fails looks exactly like one that succeeded, and it will be gone after a restart. 0.3 should decide
  whether that stays silent (it is the shipped behaviour) or gets the second InfoBar severity.

**Screen-reader announcements: none.** `Reorderable.AnnounceText` is not wired here
(`HomeCustomizerPage.cs:25-31`), so the keyboard lift — whose only feedback is visual displacement — is silent.
`Reorderable.cs:203-214` documents this exact failure mode. **0.3 must wire it** (see §9).

**Automation names.** The toggle carries `AutomationRole.ToggleSwitch` from the control; the row itself carries
none, so the toggle is announced without the module name next to it.

---

## 7. Data & readiness in 0.3 terms

### 7a. Section page

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| masthead title | `HomeSection.Title`, seeded from `Route.Arg` at mount | the route arg carried on the nav command → `Home.SectionRef.TitleId` (`StringId`) | **none** — this must paint on frame one, from the route. Never gate it on a fetch. |
| crumb trail | pure function of route + `NavOrigin` | port `DrillTrail` verbatim (CORE, §8) | none |
| grid cell count | `HomeSection.Cards.Count` | `Edges.HomeSection.Length(sectionSlot)` | `Edges.HomeSection.State[slot] != 0` (unknown ⇒ skeleton) |
| cover | `HomeCard.Image` | `Track/Album/Playlist/Artist/Show.ImageId` via the card's entity handle | `h.Knows(<Kind>Fields.Image)`; else the placeholder tint |
| title | `HomeCard.Title` | `h.TitleId` | `h.Knows(<Kind>Fields.Identity)` |
| metadata line | `HomeCard.Subtitle ?? Eyebrow`, flattened by `SpotifyExportMapper.ToPlainText` | a **precomputed `StringId` column** written at commit (the plan's `ArtistLineId` precedent, §4.12) — never a per-frame concat, never HTML | `h.Knows(<Kind>Fields.Row)` |
| circular cover | `HomeCard.Kind == Artist` | `EntityKind` of the card handle | none (structural) |
| "Show all" armed | `TotalCount`, `RawItemCount`, `cursor`, `exhausted` | `Edges.HomeSection.Total[slot]`, `.Length[slot]`, plus page-local `cursor`/`exhausted` signals | `State == 1` (partial) **and** the ported `Home.SectionCursor.HasMore` |
| Charts walk fraction | `Cards.Count / max(Total, Cards+20)` | `Edges.HomeSection.Length / max(Total, Length+20)` | always defined; never reports 1 on its own |
| wash colour | `HomeCard.Meta.Accent` → `CoverColorPlane` scheme | **DATA GAP** — see below | a colour or a null leg; **never** an invented tint |
| context menu / drag payload | `card.Uri`, `card.Title`, `card.Image`, plain subtitle | the card handle's `Uri`/`TitleId`/`ImageId` | `Knows(Identity)`; a menu on an unknown row is refused, not empty |
| **now-playing equalizer** on a cell | `PlaybackBridge.HasActiveContext` → `Identity.{ContextUri, Track}` → `NowPlayingMatch.RelatesToPlaying(card.Uri, …)` | the same playback bridge signals; the relation stays a **pure** function over (cardUri, contextUri, trackUri) | none — it is derived from playback, never from the section's own readiness. Read the COARSE `HasActiveContext` bool FIRST and bail: an idle overlay must not join `Identity`'s ~70-way fanout (`MediaCard.cs:1316-1325`) |
| **FAB glyph** ▶/⏸ | `NowPlayingMatch.OwnsPlayback(...) && IsPlaying` | same | none; `IsPlaying` is read only BEHIND the ownership test so a non-owning card never subscribes |
| **Charts card semantics inside the grid** | `ChartSections.Contains(sectionKey)` — a **uri sniff inside `SectionGrid`** (`HomeModules.cs:434`), independent of the page's route-prefix decision | the section's `byte Flags` bit `Chart`, read ONCE and passed to both the renderer and the layout estimator | §9 trap 10 — the two discriminators must not be allowed to disagree |

**Demand-on-mount (CLAUDE.md's rule, plan §4.13):**

```csharp
UseEffect(() => {
    Entities.EnsureEdges(_section, EdgeKind.HomeSection);          // the whole section's membership
    Entities.EnsureRows(_section.CardSlots, CardFields.GridCell);  // ONE batch for every card the section holds
});
```

No visible-window fetching, no per-row demand. Paging is a **cursor over the edge list**, not a viewport query:
`ReplacePage(parent, offset, targets, payload, total)` grows the list and keeps `State = partial` until
`Length == Total` (`plan §4.3`), which is exactly the 0.2.9 ledger (`RawItemCount` / `TotalCount` /
`DuplicateCount`) expressed as an edge. The Charts **walk** stays a page-owned loop: it is an eager multi-page
read with its own cancellation token, not a fetch-planner concern.

**Readiness, restated as the owner's rule:** partial rows and sections popping in are a regression. The grid shows
the shimmer until `Edges.HomeSection.State != 0`; once it flips, **every** realized cell must already know
`Identity | Image | Row` because the page demanded them in one batch. A cell that arrives blank and fills in later
is the failure mode to test for.

### 7b. Customizer

| visual element | 0.2.9 source | 0.3 read | readiness |
|---|---|---|---|
| row order | `HomePreferences.Layout.Modules` | `Home.Prefs.Layout.Modules` | synchronous — the document is loaded in the `HomePreferences` constructor (`HomePreferences.cs:22-27`); there is **no async state on this page at all** |
| which rows exist | `HomeLayoutWire.Read` keeps only kinds that parse **and** are `IsFixedLanding`; everything else (an unknown string, or a known `shelf`/`topic`/`sectionEntry`) goes to the carry and gets **no row** (`Persistence/HomeLayoutDoc.cs:82-86`), while a fixed kind the file never mentioned is appended VISIBLE (`:95-97`) | same rule in `Home.LayoutWire` | synchronous; the list is always exactly the fixed-landing set (12 today) — it never varies with the account or the feed |
| row order **during a drag** | the projected order, not the document's: `_reorder.ItemAt(i)` maps the visual slot to the live item while `LiveProject` is on, and the row is built from `modules[slot]` (`HomeCustomizerPage.cs:63-72`) | same | — |
| row hidden | `Layout.IsHidden(kind)` | same | synchronous |
| row label | `HomeCustomizeLabels.Of(kind)` → `HomeModuleCopy.Titles` (live `Loc`) | same; keep the live read (a language change must re-resolve) | synchronous |
| banner | `HomePreferences.Fault` | `Home.Prefs.Fault` | synchronous |
| re-render trigger | `HomePreferences.LayoutVersion` `Signal<int>` | same signal | — |

### DATA GAPS

Everything this surface shows that the plan's data model does **not** yet hold:

| gap | what it is | 0.2.9 source | proposed 0.3 column / edge |
|---|---|---|---|
| **payload accent** | `HomeCardMeta.Accent` (`uint`, Spotify `extractedColors.colorDark`) — tier 1 of the wash, available **before one image byte lands** | `Wavee.Core/Library/HomeFeed.cs` `HomeCardMeta.Accent`, filled by the home decoder | `Column<uint> Accent` on every entity table that can front a card (Album, Playlist, Artist, Show) + `Fields.Accent` in the **hot** group; authority = the wire that wrote it |
| **graded cover scheme** | `CoverColorPlane.Scheme` — tier 2; a per-artwork-key grading produced on the decode thread | `SpotifyLive.CoverColorPlane` | keep the plane as a SHELL side table keyed by `CoverColorPlane.KeyForUrl(imageId)`, **not** a column: it is derived from pixels, invalidated by theme, and shared by every surface. Expose `Entities.Palette(StringId imageKey)` returning `Scheme?`. The plan has no home for it. |
| **section identity / title** | a `spotify:section:` uri that is **not** an entity; plus the client-minted `wavee:local:<hash>` identity for sections the server named nothing | `HomeSection.Uri/Title`, `HomeSectionRoutes.LocalPrefix` | a **synthetic parent slot** in `Home.cs` (the plan already reserves `EdgeTable<NoEdge> HomeSection` with "synthetic subjects (Home.cs/Search.cs own the parent slots)") + `Column<StringId> SectionUri, SectionTitle` and a `byte SectionSource` (0 = homeSection, 1 = browseSection) — **the source must be a column, not a uri sniff** (see §9) |
| **the lossless ledger** | `TotalCount` (server), `RawItemCount` (raw cursor), `UnsupportedCount`, `DuplicateCount` | `HomeSection` record | `Edges.HomeSection`: `Total` exists in the plan; add `Column<int> RawCount, Unsupported, Duplicates` per parent. `Length` (deduped) and `RawCount` **must stay distinct** — that distinction is the whole of `HomeSectionPaging` |
| **the server cursor** | `pagingInfo.nextOffset`, tri-state (`null` = complete, a real offset, `BrowseSection.PagingComplete`) | `HomeSectionPageResult.NextOffset`, `BrowseSection.NextOffset` | `Column<int> NextOffset` + `Column<byte> CursorState` on the `HomeSection` edge parent (0 unknown / 1 offset / 2 complete). Do **not** collapse it to a nullable int: the explicit terminator must beat an untrustworthy `Total` |
| **chart card semantics** | Charts cards carry no subtitle and long two-line names; the set of chart section ids is a hardcoded taxonomy | `Wavee.Features.Browse.ChartSections` (5 const uris) | keep the id list in `Browse.cs` (CORE) and set the section's `byte Flags` bit `Chart` at decode; the UI reads the flag, never `Contains(uri)` at render time |
| **home layout document** | `HomeLayoutDoc` + `HomeLayoutWireCarry` (unknown kinds/fields round-trip) | `Features/Home/Persistence/*` + `Wavee.Core/Home/*` | not entity data — a preference document. Port whole into `Entities/Home.cs` (CORE) + `Entities/Home.Host.cs` (SHELL). It must **not** become a table |
| **module titles** | live `Loc` reads (`HomeModuleCopy.Titles`) | `Features/Home/HomeModuleCopy.cs` | unchanged; `Home.ModuleLabel(kind)` in the UI file |
| **the playing relation** | `NowPlayingMatch.RelatesToPlaying` / `.OwnsPlayback` over (cardUri, contextUri, trackUri) — the input to every grid cell's equalizer and FAB glyph | `PlaybackBridge.{HasActiveContext, Identity, IsPlaying}` | NOT entity data: a SHELL side signal read through a coarse-first gate. The plan's §4 models no playback relation at all, and every card grid in 0.3 (this page, Home, Recents, Browse, Search, Library) needs the same three signals plus the pure relation. Give it one named seam, e.g. `Entities.PlayingRelation(uri)` returning `(relates, owns, playing)` |
| **the save fault** | `HomeLayoutSaveFault` (`DocumentTooLarge` / `IoFailure`) — classified, logged, never rendered | `HomeLayoutStore.SaveFault` | carry the enum into `Home.Host.cs`; decide explicitly whether 0.3 keeps it silent |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | what it decides | tests | 0.3 destination (CORE section) |
|---|---|---|---|---|
| `HomeSectionPaging` | `Features/Home/HomeSectionPaging.cs` (119) | the whole cursor ledger: `NextOffset` (raw, floored at the deduped count), `HasMore` (server cursor wins; total is an arming hint only — and the comparator is **`next >= NextOffset`**, not `>`: a well-behaved cursor EQUALS the raw position it left us at, so offset 20 + 20 items → `nextOffset: 20` means "ask for 20 next", `HomeSectionPaging.cs:38-39`), `CanAdvance` (`nextOffset > requestedOffset` — note the **different** comparator, and deliberately so; `0` on a complete section is a terminator), `Append` (dedupe by uri, ordinal-ignore-case; raw cursor advances by the **full** page), `WalkFraction` (an untrustworthy total is worth +`pageAssumed`), `Progressed`, `BrowseNextOffset`, `BrowseSectionNextOffset` (tri-state) | `Wavee.Tests/HomeSectionPagingTests.cs` (211, 19 facts) | `Entities/Home.cs` → `Home.SectionCursor` |
| `BrowseSectionWalk` | `Features/Home/BrowseSectionWalk.cs:9-54` | how a Charts walk BEGINS from an optional seed (`Publish` / `FetchFirst` / `Offset` / `Exhausted`) and how one page FOLDS onto the running section | `Wavee.Tests/BrowseSectionPagingWalkTests.cs` (326) | `Entities/Home.cs` → `Home.SectionWalk` |
| `ChartTitleMatch` | `Features/Home/BrowseSectionWalk.cs:58-82` | the ordinal-ignore-case first-occurrence span, and the filtered card list | `Wavee.Tests/ChartTitleMatchTests.cs` (36) | `Entities/Home.cs` → `Home.ChartFilter` |
| `HomeSectionRoutes` / `BrowseSectionRoutes` | `Features/Home/HomeSectionRoutes.cs` (42) | the two prefixes (`home-section:` / `browse-section:`) and `IsLocal("wavee:local:")` — **the route prefix, never the uri, selects the endpoint** | driven by `DrillTrailTests`, `ShellMastheadRegistryTests`, `ShellRoutesTests` | `Entities/Home.cs` → `Home.SectionRoutes` (or `Shell/Shell.cs` route table) |
| `HomeLayoutReducer` | `Wavee.Core/Home/HomeLayoutReducer.cs` (64) | hide / show (no-op and unknown-kind rejections, append-on-absent, `MaxModules` 24), move (post-removal index, clamp, no-op rejection), reset | `Wavee.Tests/HomeLayoutTests.cs:48-114` | `Entities/Home.cs` → `Home.LayoutReducer` |
| `HomeLayoutDoc` / `HomeLayoutModules` | `Wavee.Core/Home/HomeLayoutModel.cs` (147) | `DefaultOrder` (the 12 kinds, in the designed rhythm), `IsFixedLanding`, `KindName`/`TryParseKind` (**persisted strings — never rename**), `IsHidden`, `IndexOf`, `VisibleFixedModules` | `HomeLayoutTests.cs:96-114, 244-272` | `Entities/Home.cs` → `Home.Layout` / `Home.LayoutModules` |
| `HomeLayoutCommands` | `Wavee.Core/Home/HomeLayoutCommands.cs` (60) | the closed command set + `HomeLayoutRejectReason` + the undo loc keys | exercised throughout `HomeLayoutTests` | `Entities/Home.cs` → `Home.LayoutCommands` |
| `HomeLayoutWire` | `Features/Home/Persistence/HomeLayoutDoc.cs:66-157` | DTO read/write with **forward compatibility**: unknown kinds and unknown members survive a round trip at their original index; a kind this build knows but the file never mentioned is APPENDED VISIBLE | `HomeLayoutTests.cs:116-175` | `Entities/Home.cs` (CORE; the DTO + `JsonSerializerContext` may live beside it) |
| `HomeLayoutStore` | `Features/Home/Persistence/HomeLayoutStore.cs` (244) | fault classification (`Corrupt`/`TooNew`/`Unreadable`), `.bak` recovery, atomic `File.Replace` with a `.tmp`, 256 KiB cap, write-block until `DiscardCorrupt` | `HomeLayoutTests.cs:176-243` | `Entities/Home.Host.cs` (SHELL) — it is I/O |
| `DrillTrail` | `Features/Shell/DrillTrail.cs` (112) | the crumb trail as a pure function of (route, arg, live title, `NavOrigin`): a Home section trails to **Home**, a browse section to **Browse**, and a foreign origin on a Browse-family page is PREPENDED (Home › Browse › X) unless it is a search lookup | `Wavee.Tests/DrillTrailTests.cs` (272) | `Shell/Shell.cs` (CORE) — shared with `18-shell-frame.md` |
| `ShellMastheadRegistry` | `Features/Shell/NavOrigin.cs:75-108` | the ONE family predicate + the static title fallback per family | `Wavee.Tests/ShellMastheadRegistryTests.cs` (106) | `Shell/Shell.cs` (CORE) |
| `BrowseMastheadMetrics` | `Features/Browse/BrowseMastheadMetrics.cs` (38) | `Reserve` 84 = 32 + 52, `BodyTop` 100, `FamilyBodyPad`, `ClipInset`, `ClipFadeBand` | `Wavee.Tests/BrowseMastheadMetricsTests.cs` (52) | `Platform/Design.cs` (CORE constants) |
| `HomeCardNav.OpenBrowseSection`'s one-card rule | `Features/Home/HomeSectionNavigation.cs:246-260` | whether this page is reached AT ALL: a browse section of exactly one card opens that card's own destination instead (§0.19); a Home section has no such rule | *(none today — add one)* | owned by `11-home-cards-and-modules.md`; the rule must stay pure and testable |
| `HomeSectionAppendPreloader.NearTailWatch` | `Features/Home/HomeSectionAppendPreloader.cs:46-54` | the coarse scroll-geometry projection key and the 1.5-viewport near-tail predicate | *(none today — see §9)* | `Entities/Home.cs` → `Home.NearTail(ScrollGeometry)`; **add a test** |
| `FillRowVirtualLayout.Fit` + `GridCardChromeFor` | engine `Scene/VirtualLayout.cs:483-500` + `HomeModules.cs:533-536` | column count, cell width, and the per-cell chrome reserve from `(titleLines, hasSubtitle)` | engine-side | `Entities/Home.cs` → `Home.GridFit`; keep `GridCardChromeFor` as a pure function so the renderer and the layout estimator **cannot** be handed different numbers |
| `NowPlayingMatch` | `Features/Player/…` (owned by `15-player-bar.md` / `02-cards-and-controls.md`) | `RelatesToPlaying` (LOOSE — the equalizer) vs `OwnsPlayback` (STRICT — the ⏸ glyph and the pause verb). The split is load-bearing on this grid: an album card of the playing track shows a moving equalizer but keeps ▶ | *(check the owning chapter)* | CORE — shared by every card grid; this page must not re-derive it |
| `ChartSections` | `Features/Browse/BrowseTaxonomy.cs:160-181` | the 5 hardcoded chart section ids + `Contains`. Read **twice** today — once by the page (route-prefix-gated) and once by `SectionGrid` (ungated, `HomeModules.cs:434`) | *(none)* | `Browse.cs` CORE list; the UI must read a decoded **flag**, never `Contains(uri)` at render time (§9 trap 10) |

---

## 9. Re-author notes

### What must not be simplified

1. **The prefix/uri split.** `HomeSectionPage`'s class comment (`:18-27`) is the single most important paragraph
   on this surface: some hardcoded Charts sections carry a `spotify:section:` uri **textually indistinguishable**
   from a Home section uri, and a uri-shaped discriminator silently sent those reads to the wrong endpoint. In 0.3
   the discriminator becomes a `byte SectionSource` **column written at decode**, and there is **no fallback in
   either direction**: a `home-section` never retries as `browse-section`, and vice versa. A 400 surfaces as
   `ErrorState`, loudly.
2. **The three terminators.** `totalCount` is an *arming hint*, never a terminator. Stopping is decided by
   (a) `CanAdvance(offset, nextOffset)` — a complete section answers `nextOffset: 0`; (b) `Progressed(before,
   after)` — a page the dedup ate whole; (c) an empty page. Re-deriving this from "loaded < total" re-creates the
   infinite request loop the comments at `HomeSectionPaging.cs:9-19` measured (7 of 31 captured sections
   disagreed with their own `totalCount`).
3. **Raw vs deduped.** `NextOffset = max(RawItemCount, Cards.Count)`. Paging by the deduped count walks the
   cursor backwards by exactly the number of dropped items.
4. **The expired-local empty state.** A `wavee:local:` route whose preview entry has been evicted must render the
   **ordinary empty page** and issue **no request** (`HomeSectionPage.cs:80-99`). Turning it into an error page is
   a regression the code explicitly reverted.
5. **The seed handoff.** `BrowseSectionWalk.Begin` decides whether the seed is PUBLISHED before the next offset is
   asked. Skipping that `SetReady` leaves a 74-item Weekly section blank until offset 20 returns
   (`BrowseSectionPagingWalkTests.cs:203-226` pins it).
6. **The permanent walk-bar slot and the fixed toolbar child count.** `HomeSectionPage.cs:285-293` documents the
   defect: `bodyKids` is unkeyed and matched by position + element type, so adding the bar as a *sibling* shifted
   the grid one index and remounted its whole virtual viewport twice per drill.
7. **`ProgressBar.Create(signal)` is always determinate.** `Create(null)` returns a `ComponentEl` and
   `Create(signal)` a `BoxEl` — gating on the value swaps the element type mid-walk (`:307-309`).
8. **`smoothResize: false`.** Easing 0 → N rows of a virtual grid clips the covers into a strip
   (`HomeSectionPage.cs:278-281`).
9. **`grow: 1f` on `Responsive.Of` and `Grow=1/MinHeight=0` on the rendered root.** `ResponsiveBox` defaults grow
   to 0; in a column that sizes it to content, a virtual viewport's natural content height is 0, so the grid
   realizes zero rows and the pane is empty mica under the masthead (`:220-223`, `:205-211`).
10. **The forward-compatibility carry.** `HomeLayoutWire` re-emits unknown kinds at their original index and
    unknown members on known modules. Dropping it silently destroys a future build's settings on a downgrade.
11. **Fail-soft persistence.** A corrupt / too-new / unreadable file is **preserved**, writes are blocked, the
    default is used in memory, and only `DiscardCorrupt` moves it aside (sidebar locked decision 8).

### Traps

1. **The frozen-closure trap, and its current shape.** `HomeSectionPage.cs:224-232` records that a card list
   computed in `GridBody`'s own scope painted **once** and never changed: the Charts filter did nothing, and an
   appended page only ever appeared because an unrelated unkeyed child-index shift remounted the grid. The fix —
   derive `live`, `lq`, `shown` **inside** the `Responsive.Of` closure — is mandatory in 0.3 for a second,
   permanent reason: reading the signals there makes `ResponsiveBox` itself the subscriber, so an append
   re-renders the grid *without* re-rendering the page. (One drift to note: today's `Responsive.Of` passes its
   build closure through the **props** channel (`Responsive.cs:28-36`), so the comment's stated mechanism —
   "`Embed.Comp` runs the factory once and a parent re-render reuses the propless component" — describes an
   earlier engine. The *rule* is unchanged and still load-bearing.)
2. **The preloader's `Key` is its prop channel.** `Embed.Comp(() => new HomeSectionAppendPreloader { … })` freezes
   its fields at mount. The page therefore remounts it on every cursor change:
   `Key = "home-section-append:" + uri + ":" + cursor` (`:254`). Drop the key and the preloader keeps calling
   `LoadMore` with a stale closure. The signals it reads (`Loading`, `NearTail`) are fine to freeze — they are
   *instances*, not values.
3. **`ReuseGuard` and the grid cell key.** `"home-section-card:" + tier + ":" + titleLines + ":" + uri`
   (`HomeModules.cs:454-456`): the column count and the title-line count are in the key because both change the
   cell's measured **shape**, and the recycle-shape guard compares structure per key. In 0.3, prefer a *bound* row
   (the plan's `BoundItemScope`) so a rebind does not remount — but then the shape must be invariant, which means
   `titleLines` must become a per-grid constant, not a per-cell one.
4. **`_corruptDismissed` is a plain field on the Component instance.** The re-render is forced by a manual
   `_bannerEpoch` signal bump (`HomeCustomizerPage.cs:36-38, 51, 153`). This works only because the page instance
   survives; it is exactly the pattern the props contract warns about. In 0.3 make it a `UseSignal<bool>`.
5. **The reorder wrapper does not grow.** `Reorderable.Item` wraps content in a `Direction = 0` row, so content
   with no `Grow` arranges at its own measured width (`Reorderable.cs:329-352`), and in a finite-width row a
   `Basis = 0` child is denied intrinsic width by contract (`FlexLayout.cs:590-604`) — the two rules together
   are what put the label at width 0 (W13). The sidebar's wrap sites pass
   `Grow = 1f, Shrink = 1f, MinWidth = 0f` for precisely this reason and document it as the pattern's owner
   (`SidebarPaneSlot.cs:96-98, 100-131`). The Home customizer passes a bare `Embed.Comp` (`:69-71`) and
   `HomeCustomizeRow`'s root has no `Grow` (`:264-268`) — the row therefore measures to
   `8 + 12 + 8 + 0 + 8 + 154 + 8 = 198` DIP and its `Grow=1/Basis=0` label gets zero free space to grow into.
   **0.3 must apply the sidebar's treatment.** Confirm the 0.2.9 symptom side by side (parity item 38) before
   deciding whether this is "restore the look" or "fix the look".
6. **The drag has no moving visual.** `DragLift.Stationary` + `Drag.SourceDimOpacity` is the "the chip is the
   moving visual" contract — but the shell's single `DragPreviewLayer` resolver (`WaveeResourceDrag.Chip`,
   `:305-347`) answers only two kinds, and `"wavee.home-layout"` is not one of them. **Decide in 0.3:** either add
   a chip arm (glyph + the module label + a "Reorder" caption) **or** drop `DragStyle` and take the engine's ghost
   lift, as `SidebarPane.SectionReorder` deliberately does.
7. **No reorder announcements.** `AnnounceText` is unset, so the keyboard lift is completely silent. Wire it in
   0.3 — the engine composes nothing for you on purpose (`Reorderable.cs:201-214`).
8. **`StaggerRows` does not stagger rows here.** `SkeletonReveal` walks the *direct children* of the real root and
   only descends a **single-child chain** into a virtual viewport (`SkeletonRegion.cs:169-186, 207-219`).
   `GridBody` has two children, so the walk stops there and the grid rises as one slab. If 0.3 wants a genuine
   per-cell cascade, the region's content must be the viewport itself (single child) — but note D34
   (`DEFECT_REGISTER.md:41`) deliberately fences entrance staggers off virtualized surfaces because a
   realize-on-scroll replay reads as flicker. **Keep the current behaviour unless the owner asks otherwise.**
9. **Zero-allocation scroll frames vs per-row richness — how 0.2.9 reconciled them.** The grid is
   `Virtual.Custom` with `overscan: 2` **rows** and a keyed `renderItem`; per-cell menus and drag sources are
   built as *lazy attachments* (`MenuAttach` holds a `Func`, `Drag.Source` holds a payload factory), the
   now-playing overlay is `Embed.Comp(...).Skeletonized(false)` behind a hover signal, and the card's hover state
   lives in a per-card `Signal<bool>` written only from `OnPointerMoveWithin`. Nothing is computed per frame. In
   0.3 the same discipline is the plan's `BoundItemScope`: a slot rebinds, it does not remount, and the row reads
   columns through bound thunks.
10. **The chart discriminator is read TWICE, and only one read is route-gated.** `HomeSectionPage` derives
    `charts` from `browse && ChartSections.Contains(sectionUri)` (`:90`) and uses it for the toolbar, the walk,
    `titleLines` and the append gate. `HomeModules.SectionGrid` then derives its OWN `charts` from
    `ChartSections.Contains(sectionKey)` (`HomeModules.cs:434`) — a **bare uri sniff**, with no route prefix in
    sight — and uses it to blank the subtitle and to pick `hasSubtitle` for the cell reserve. The two agree today
    only because no `home-section:` route is ever minted for a chart uri. They are exactly the pair the class
    doc-comment at `:18-27` says must not exist: a `home-section:` route on one of the five chart uris would
    render blanked subtitles at `titleLines: 1` with a 34-DIP reserve and no filter box. In 0.3 the decoded
    `SectionSource` / `Flags.Chart` must be threaded into `SectionGrid` as an argument — `SectionGrid` must not
    look a uri up in a taxonomy at render time. (It is also a per-cell `Contains` over a 5-element list on every
    grid build, which the zero-alloc discipline should not inherit either.)
11. **Hard `Height`s in the customizer.** The command bar is `Height = 64` and every row is `Height = 48`
    (`HomeCustomizerPage.cs:91, 266`), while the section page's own filter box deliberately takes `MinHeight`
    only *because* "a hard Height clips under text scaling" (`HomeSectionPage.cs:302`). The customizer does not
    follow its own app's rule: at 150–200 % text scale the 14-DIP row label and the 16/12 title stack clip.
    `ReorderList` needs a fixed `ItemExtent` for its slot math, so the row height cannot simply become
    `MinHeight` — 0.3 must either derive `RowExtent` from the resolved type metrics or accept the clip
    **deliberately** and say so.
12. **Off-ramp type in the customizer.** Three raw `TextEl`s sit off the D30 type ramp: the page title
    (`16 / w600`, no line height — the ramp has 14 and 18, not 16), the hint (`12`, should be `Ui.Caption`
    12/16), the row label (`14`, should be `Ui.Body` 14/20). Port them **onto** the ramp in 0.3 and say so; do
    not preserve the drift.

### Where the plan is wrong or too thin for this surface

- **§2's tree had no home for the customizer — settled 2026-09-12 (A15).** `Entities/Home.{cs,UI.cs,Page.cs}` was
  400 + 800 + 800 lines, and the customizer is a *page with its own route, its own reducer, its own json document
  and its own store* — not a Home page part. It is now **`Entities/Home.Customizer.cs`**, with the document +
  reducer + commands + wire in `Entities/Home.cs` (CORE) and the store in **`Entities/Home.Host.cs`** (SHELL). The
  one change from this chapter's proposal: the page lives in `Entities/` with the rest of Home rather than in
  `Screens/` beside `Settings.*` / `Setup.*` — Home is one owner (**P**) and one seven-file set, and a `Screens/`
  file would have split it across two blocks of the tree for no gain.
- **§2's tree had no `Browse.Page.cs` — settled.** `Entities/Browse.Page.cs` is now in the tree with owner **P**
  (chapter 13 §9), so `Browse.UI.cs` no longer has to host the directory page, the category page and this page
  class at once. **And the recommended half of this bullet is taken too: `Home.Page.cs` owns the shared section
  page for both sources** — one class, one grid, one set of terminators, exactly as 0.2.9 designed it. The
  `browse-section:` arm is `Home.SectionPage` with `SectionSource = 1`; `Browse.Page.cs` owns the directory and
  category pages, not the drill.
- **§4.12/§4.13 only show a track row and an album page.** Neither models (a) a **synthetic parent** whose
  children are heterogeneous entity kinds, (b) a **page-owned cursor** that is not the fetch planner's business,
  or (c) a **page-owned eager multi-page walk with cancellation**. All three are load-bearing here. §4.3's
  `HomeSection` edge is one line ("synthetic subjects") and needs the ledger columns listed in §7's DATA GAPS.
- **§4.3's `EdgeTable` has `Total` but not `RawCount`.** `Length` (deduped) and the raw server cursor are
  different numbers, and every defect `HomeSectionPaging` exists to prevent comes from conflating them.
- **§5's Wave 5 gate** ("`--fake` opens every route in the nav probe list") does not name `home-customize` or a
  `home-section:`/`browse-section:` route. Both must be on the probe list, and the section page must be probed
  **twice** — once seeded (from Home) and once cold (a direct deep link) — because those are two different
  visual paths.
- **Nothing in the plan owns the cover palette.** §7 DATA GAPS proposes `Entities.Palette(imageKey)`; the plan has
  no such seam, and this page (plus Home, Recents, every detail page) needs it.

### Line budget

| | lines |
|---|---|
| 0.2.9, this surface's own files | **1 350** (628 + 104 + 119 + 42 + 310 + 157 + 244 + the 82-line `BrowseSectionWalk` + 96-line preloader = 1 782 counting the two shared helpers; 1 350 excluding `HomeSectionNavigation`'s `HomeCardNav`, which `11-home-cards-and-modules.md` owns) |
| + `Wavee.Core/Home/*` (model, reducer, commands) | **271** |
| + the shared shell rules this surface depends on (`DrillTrail`, `NavOrigin` registry, `BrowseMastheadMetrics`) | **258** (owned by `18-shell-frame.md`) |
| former plan §2 target for the whole of `Home.{cs,UI.cs,Page.cs}` | **2 000** — for Home's landing page *and* this page *and* the customizer *and* the layout document |
| **honest estimate for this chapter's surface alone** | **section page 700–800** (page 480, cursor/walk/filter/routes 260) · **customizer + document + store 620–700** (page 300, model+reducer+commands 270, wire+store 380 → 650 with the wire trimmed) · **total ≈ 1 400–1 500** |

That left roughly 500 lines of the plan's 2 000 for Home's landing page, its hero, its facets, its wash and all
twelve module renderers — which was not survivable.

**Settled 2026-09-12 (A15): seven files, all `Entities/`, all owner P.** This chapter's recommendation is taken with
two amendments — the customizer page is `Entities/Home.Customizer.cs` rather than `Screens/HomeCustomize.UI.cs`, and
chapters 11's two named UI partials (`Home.Cards.UI.cs`, `Home.Artists.UI.cs`) are real files, so `Home.UI.cs` is not
the single 900-line catch-all this chapter guessed at. Each chapter keeps its own estimate for the parts it owns;
**this chapter's four columns are the ≈ 1 400 – 1 500 above, spread across the files that actually hold it**:

| file | ch. 10 | ch. 11 | **ch. 12** | lines |
|---|--:|--:|--:|--:|
| `Entities/Home.cs` (CORE) | 650 | 450 | **700** | 1 800 |
| `Entities/Home.UI.cs` | 600 | 1 000 | — | 1 600 |
| `Entities/Home.Cards.UI.cs` | — | 1 250 | — | 1 250 |
| `Entities/Home.Artists.UI.cs` | — | 650 | — | 650 |
| `Entities/Home.Page.cs` | 1 750 | — | **480** | 2 230 |
| `Entities/Home.Customizer.cs` | — | — | **300** | 300 |
| `Entities/Home.Host.cs` (SHELL) | — | — | **400** | 400 |
| **total** | 3 000 | 3 350 | **1 880** | **8 230** |

This chapter's 1 880 is the top of its own ≈ 1 400 – 1 500 range plus the store's full 400 (the range charged the
wire and the store together at ≈ 380; the wire is CORE and sits inside the 700). Nothing in it is double-counted
against chapters 10 or 11: the landing page and the module renderers are theirs, the section page, the customizer,
the layout document and the json store are this chapter's, and `Home.Page.cs` is the one file both write in — the
landing in ch. 10's 1 750, `Home.SectionPage` in this chapter's 480. Keeping CORE testable is what the split was for,
and it survives: `Home.cs` is BCL-only and test-included, `Home.Host.cs` is the only file that touches disk.

### Files / pages missing from the section 2 tree

All four are now settled; kept here as the record of what was asked for and what was decided.

- ~~`Entities/Home.Host.cs`~~ — **in the tree (A15)**, owner P: the SHELL half of Home, where `home-layout.json` is
  read and written off the UI thread.
- ~~`Screens/HomeCustomize.UI.cs`~~ — **the customizer is `Entities/Home.Customizer.cs` (A15)**, owner P.
- ~~`Entities/Browse.Page.cs`~~ — **in the tree**, owner P (chapter 13 §9) — *and* `Home.Page.cs` owns the shared
  section page for both sources, which was the recommended half of the same bullet.
- The `home-customize`, `home-section:` and `browse-section:` routes — **added to the Wave 5 gate's probe list**,
  with the section page probed twice (seeded from Home, and cold from a direct deep link).

---

## 10. Parity checklist

Verify each item side by side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Unless stated otherwise: **window 1440 × 900**, sidebar in its default expanded state, right rail closed, dark
theme, colour washes ON. "content width" = the measured width the grid receives; anchor it once by widening the
window until the grid shows exactly 6 columns and noting the window width — every column item below is then
expressed as a relative drag from that anchor.

**Section page — frame and masthead**

1. From Home, click any shelf header's "Show all" / a Fold tile → the masthead reads `Home › <Section title>`
   with the section's real title **in the first presented frame**. *Frame recording; step to frame 1.*
2. The masthead title is 40/52, Display face, weight 400, negative tracking; the crumb and the `›` are
   `TextTertiary`. *Static capture, zoom 400 %.*
3. Hovering the `Home` crumb lightens it to `TextSecondary`; the current title never reacts. *Hover capture.*
4. Clicking the `Home` crumb returns to Home. *Interaction.*
5. The first row of cards starts exactly 100 DIP below the top of the content card (84 reserve + 16). Measure
   against the Browse directory at the same window size — they must match to the pixel. *Two static captures,
   overlaid.*
6. Left and right gutters are 36 DIP and identical to the Browse directory's. *Same overlay.*
7. Navigating Home → section slides the body in from +8 DIP over 250 ms while the outgoing page fades out over
   120 ms starting immediately; the card is never empty. *Frame recording at 60 fps.*
8. The masthead does **not** slide with the page — it holds position and only its opacity changes when leaving
   the family. *Same recording.*

**Section page — grid geometry**

9. At the 6-column anchor width, covers are exactly square and the grid gap is 12 DIP horizontally and
   vertically. *Static capture, measure.*
10. Narrow the window until the grid drops to 5 columns, then widen 1 DIP — it returns to 6 immediately
    (**no hysteresis**). *Slow drag, frame recording.*
11. Column boundaries land at content widths **189 / 389 / 589 / 788 / 948 / 1108 / 1268** — the first three come
    from the max-width (`ceil`) arm and open EARLIER than the `160n − 12` rule, which only takes over from five
    columns up (W4). 628 is **not** a boundary: at 628 the grid has already been on 4 columns since 589, at a
    138.25-DIP cell. *Slow drag with the measured content width noted at each flip.*
12. Cell height = cell width + 52 for a normal section. *Static capture, measure one cell.*
13. Card padding is 8 / 8 / 8 / 12 and the cover→title gap is 8. *Zoomed capture.*
14. The title is one line, `BodyStrong` 14/20 w600, character-ellipsised; the metadata line is `Caption` 12/16
    secondary, one line. *Zoomed capture of a long-titled card.*
15. An Artist card's cover is a circle; every other kind is `Radii.Card` 8. *Static capture of a section
    containing artists (e.g. a "Your top artists" drill).*
16. Scrolling the grid does **not** move the Charts toolbar / the masthead. *Frame recording while scrolling.*

**Section page — states**

17. Deep-link a section cold (open a new tab and paste the `home-section:` route via the command palette) → 8
    shimmer cells appear, breathing on a 1 s cycle between α 1 and α 0.5. *Frame recording.*
18. On Ready, the grid rises 8 DIP with a 3 σ blur over 500 ms — **as one block**, not row by row. *Frame
    recording; confirm no per-row offset.*
19. Drill in from Home (seeded) → **no shimmer and no rise at all**; the cards are present on the first frame.
    *Frame recording.*
20. A section with zero cards shows `Nothing here yet` / `When there's something to show, it'll appear here.`,
    centred, big type, **no glyph and no button**. *Static capture.*
21. Kill the network and deep-link a `home-section:` route → `Something went wrong.` /
    `Check your connection and try again.`, **no Retry button**. *Static capture.*
22. Restart the app and re-open a Home-minted section route from the history (a `wavee:local:` route whose
    preview was evicted) → the **empty** state, never the error state, and no network request. *Static capture +
    the log (no `home.section` request line).*

**Section page — paging**

23. On a long section, scroll to within 1.5 viewports of the end → after ~300 ms more cards appear with **no
    spinner and no shimmer row**. *Frame recording.*
24. Stop scrolling at the tail → nothing further loads (the chain does not run away). *Wait 10 s, count the
    cards.*
25. "Show all" is present in the masthead while more pages exist and **disappears** once the section is
    exhausted. *Two static captures.*
26. Clicking "Show all" greys the label while the fetch is in flight (disabled), then re-enables. *Frame
    recording.*
27. A section whose server reports a larger `totalCount` than it will serve disarms the button after the first
    empty page rather than leaving a dead button. *Drive a section known to over-report; watch the button.*

**Section page — Charts variant**

28. Open Browse → Charts → a chart shelf header. The toolbar row appears: a 300 × 32 filter box at the left, a
    160 × 3 determinate bar at the right, 12 DIP above the grid. *Static capture, measure.*
29. The walk bar's 160-wide slot is reserved even before the walk starts and after it ends — the grid's top edge
    never moves. *Frame recording across the whole walk.*
30. The bar advances in steps as pages land and never shows a full bar while pages are still in flight. *Frame
    recording of a Weekly Song Charts drill (74 items over 4 pages).*
31. Chart cards show **two** title lines and **no** metadata line; cell height = cell width + 54. *Zoomed
    capture, measure.*
32. Type `arg` in the filter → only matching cards remain, and `Arg` inside each title is drawn on an accent
    pill with 3/1/3/1 padding and radius 4. *Zoomed hover-free capture.*
33. Type `zzzz` → `Nothing matches your filter` at the Subtitle rung (20/28), centred. *Static capture.*
34. Clear the filter → the full set returns without a remount flash. *Frame recording.*
35. Charts sections show **no** "Show all" in the masthead and **no** infinite-scroll append. *Static capture +
    scroll to the end.*
36. Navigate away mid-walk and back → the walk does not continue in the background (no further
    `home.section.walk` log lines after the drill-away). *Log.*

**Customizer**

37. Home → the `⋯` button right of the greeting/chips row → one flyout item `Customize Home` with a pencil icon,
    right-aligned under the button, min width 200. *Static capture.*
38. Open the customizer. **Record what the rows actually look like**: whether each module's name is visible, how
    wide the row content is, and where the toggle pill sits relative to the column's right edge. This is the one
    item whose 0.2.9 answer the code cannot settle (§9 trap 5). *Static capture + measure.*
39. The command bar is exactly 64 DIP tall with a 1 DIP divider under it. *Measure.*
40. The eyebrow reads `Your Home` in accent ink, all-lowercase-as-authored (**sentence case, not caps**), letter-
    spaced; the title reads `Customize Home`. *Zoomed capture.*
41. `Reset` is a Subtle small button and `Done` an Accent small button, 4 DIP apart, 16 DIP from the right edge.
    *Measure.*
42. The rows list is capped at 720 DIP and sits at the **left** of the pane, not centred. *Widen the window to
    1600 and capture.*
43. The 12 rows appear in this order: Hero · Discover Weekly & Release Radar · Jump back in · Recents · Made for
    you · Your top mixes · Radio · Up next · Audiobooks for you · Podcasts for you · Editors' picks · Because you
    listened. *Static capture.*
44. Each row is exactly 48 DIP with **no** gap between rows. *Measure two adjacent rows.*
45. Turn a toggle off → the whole row (gripper, label, toggle) drops to α 0.55 **instantly**, with no fade.
    *Frame recording.*
46. Hovering a row changes **nothing** (no fill, no plate, no cursor change). *Hover capture.*
47. Go back to Home → the hidden module is gone from the feed and nothing else moved. *Two static captures of
    Home.*
48. Re-open the customizer → the hidden row is still present and still dimmed. *Static capture.*
49. Drag a row: the dragged row dims to ~0.4 **in place**, the siblings slide out of the way after a short dwell,
    and there is **no insertion line**. *Frame recording.*
50. During that drag, note whether **any** moving visual (a chip) follows the cursor. (§9 trap 6 predicts none.)
    *Frame recording.*
51. Release the drag outside the list (over the command bar) → the order is unchanged. *Before/after capture.*
52. Tab to a row, press Space → the row lifts to α 0.80 with a flyout-class shadow; ↑/↓ move it; Space commits;
    Esc cancels. *Frame recording of each step.*
53. Reorder, then go back to Home → the feed's module order matches, with the chrome rows still at their anchors
    (Artists after Made-for-you, Timeline/Charts/Sections after Podcasts). *Two static captures.*
54. Press `Reset` → the order and visibility snap back to item 43's list with **no confirmation dialog**. *Frame
    recording.*
55. Kill the app immediately after a reorder (no `Done`) and relaunch → the new order is still there. *Restart.*
56. Corrupt `%LOCALAPPDATA%\Wavee\WaveeMusic\home-layout.json` (write `{` ) with **no** `.bak` present, relaunch,
    open the customizer → the Warning InfoBar appears above the list with `Home layout could not be read`, a
    second sentence, a `Start fresh` button and a close `✕`; the list shows the **default** order. *Static
    capture.*
57. With the banner showing, flip a toggle → the change is visible in the UI but the file on disk is **not**
    rewritten. *Check the file's mtime.*
58. Click `Start fresh` → a `home-layout.json.corrupt` appears, `.bak` is gone, and the next toggle **does** write
    the file. *File listing + mtime.*
59. Dismiss the banner with `✕` → it collapses to zero height and nothing below jumps by more than the banner's
    own height. *Frame recording.*
60. The customizer sits on the neutral shell ground — no wash — even when arriving from a Home page that was
    heavily tinted. *Two static captures of the title bar / sidebar while navigating Home → customizer.*
61. `Done` returns to whatever page was open before the customizer (not unconditionally to Home): open it from a
    Home page reached via a drill, press `Done`, and confirm the destination. *Interaction.*
62. Narrow the window to ~700: the command bar never wraps; the title and eyebrow ellipsise; `Reset`/`Done` stay
    full size. *Static capture.*
63. Scroll the (tall) list at a small window height → the top and bottom edges feather over ~40 DIP as content
    passes under them. *Frame recording.*
64. Leave the customizer scrolled, navigate away and back → the scroll position is restored (`ScrollKey`
    `home.customizer`). *Interaction.*

**Now-playing on the section grid (added by the audit — W5b)**

65. Start a playlist, then drill into a section that contains that playlist's card → the card shows a
    **three-bar equalizer pill** at the **bottom-left** of its cover: 4 DIP padding, radius 4, on
    `WaveeOnMedia.ScrimRest`, bars in `AccentTextPrimary`, inset 8/8 from the cover's corner. *Zoomed capture,
    measure.*
66. The bars move on an 850 ms loop while playing; pause the player → they settle low and STOP, the pill stays.
    *Frame recording of both edges.*
67. Hover that card → the bars keep ticking (the cover layout does NOT pause on hover), the plate and FAB reveal
    over them. *Frame recording.*
68. The FAB on the card that OWNS playback reads **⏸**; the FAB on an album card of the same playing track reads
    **▶** while still showing a moving equalizer. *Two zoomed captures.*
69. Click the ⏸ FAB → playback pauses and the glyph flips in the same frame (an optimistic local write). Click ▶
    on the album card → the album starts. *Interaction.*
70. Skip to a track in another context while the section page is open → the equalizer unmounts from the old cell
    and mounts on the new one with **no** flash on the FAB beside it (one ZStack shape on both legs). *Frame
    recording.*
71. Let a page of cards append while the cursor sits STILL over where a card lands → the new card does **not**
    show a hover plate until the pointer moves. *Frame recording.*

**Degenerate routes and titles (added by the audit — W19)**

72. Deep-link a `home-section:` route with **no title arg** → the masthead reads just `Home` with no crumb and
    no `›` until the fetch lands, then snaps to `Home › <title>`. Repeat for `browse-section:` → `Browse`.
    *Frame recording; step to frame 1 and to the Ready frame.*
73. A `wavee:local:` route whose seed was evicted **and** whose arg is empty → the masthead reads `Browse`
    (`SectionTitle`'s last fallback) over W9's empty body. *Static capture.*
74. Hand-type `home-section:` with an empty uri → the page shimmers **forever**; no empty state, no error, no
    `home.section` log line. Record it as the shipped behaviour before 0.3 changes it. *Frame recording + log.*
75. Restore a session that still holds the legacy `home-section:spotify:list:recents:main` route → the Recents
    page opens (not a section page), and the shell material still hands over. *Interaction + static capture.*
76. Open a chart section from **Home's** Charts row (not from Browse) → the masthead reads three crumbs,
    `Home › Browse › <chart title>`, with both parents clickable. Open the same section from Browse → two
    crumbs. *Two static captures.*

**Persistence faults (added by the audit)**

77. Corrupt `home-layout.json` **with a valid `.bak` present** → the customizer opens with **no banner**, the
    recovered order, and one `home.layout.recovered` log line. *Static capture + log.*
78. Write a `{"version": 99}` document → the banner shows the **same** `Home layout could not be read` copy as
    the corrupt case; the version detail is nowhere on screen. *Static capture.*
79. Make `home-layout.json` read-only, flip a toggle → the UI changes, the file does not, **nothing is shown**;
    only a `home.layout.save_failed` line. *File mtime + log.*

**Entry, crumbs and the quiet states (added by the second audit)**

81. Find a browse/Charts shelf whose section holds exactly **one** card and click its header → the app opens
    **that card's own page**, never a one-cell section page (§0.19). Then find a Home section with one card and
    open it → a section page with one cell. *Two interactions.*
82. Watch a COLD section drill frame by frame: while the 8 cards shimmer the masthead shows the title and
    **nothing at its right edge**; "Show all" appears only in the frame the grid reveals, and a two-line title
    may re-wrap at that instant. *Frame recording.*
83. In the Charts filter box, type a query → there is **no ✕ inside the box and no Clear button**; Esc does
    nothing. Navigate away and back (KeepAlive) → record whether the query survives. *Interaction.*
84. Open a Browse CATEGORY page (e.g. a genre), click one of its shelf headers → the band reads
    `Browse › <Category> › <Section>` (three crumbs, the middle one the category), which is a **different**
    shape from the `Home › Browse › <Chart>` of item 76. *Static capture.*
85. Kill the network while scrolled at the tail of a long section, then scroll to re-trigger the append three
    times → after the third failure the silent auto-append never fires again on that page even with the network
    restored; only the masthead button pages it. *Log: three `home.section.page.fail` lines, then silence.*
86. Note `home-layout.json`'s mtime, press `Reset` on an already-default layout → the file **is** rewritten
    (`updatedAtMs` moves) although nothing on screen changes. *File mtime, before/after.*
87. Make the corrupt `home-layout.json` undeletable (hold it open / deny delete), click `Start fresh` → the
    banner disappears but no `.corrupt` file appears, and every later toggle is lost on restart. Expect one
    `home.layout.discard_failed` line. *Log + restart.*
88. Write `{"version": 99}` as the primary **with a valid `.bak` beside it** → the banner STILL shows (the
    backup is only consulted for a malformed primary). *Static capture + log.*
89. Put an unknown module (`{"kind":"futureThing"}`) and a known-but-not-landing one (`{"kind":"topic"}`) in the
    file → the customizer still lists exactly 12 rows, and after flipping one toggle both entries are still in
    the rewritten file at their original indices. *File diff.*
90. Drag a row and release it on its own slot → nothing is written at all (no mtime change, no version bump).
    *File mtime + the Home feed unchanged.*

**Text scaling (added by the audit — §9 trap 11)**

80. Set Windows text scaling to 150 % and open the customizer → record whether the 64-DIP command bar and the
    48-DIP rows clip their labels. This is the shipped behaviour the hard `Height`s produce; decide for 0.3
    with the capture in hand. *Static capture at 100 % and 150 %.*

---

## 11. Audit log

Adversarial re-read of the 0.2.9 sources against the draft, 2026-09-12. Every line below is a correction made in
place; nothing correct was removed. `wrong` = a value or claim that contradicted the code; `missing` = a state,
element or motion the draft did not cover; `unverified` = a citation that did not land where it said;
`overclaim` = a "there is none" that the code contradicts.

| # | section | kind | correction |
|---|---|---|---|
| 1 | §5 · §2 W5b · §0.16 · §3a · §7 · §10 | **overclaim** | "No ticker, no countdown, **no equalizer**" was wrong. `MediaCard.GridCard` mounts `LazyOverlay` → `NowPlayingOverlay` (`MediaCard.cs:36-39, 243, 1294-1348, 1504-1520`): a cell whose uri *relates* to the playing context paints a three-bar equalizer pill at the cover's bottom-left, ticking on an 850 ms loop at ~30 Hz, settling low when paused. Added W5b, four §5 rows, §3a rows, a §7 read and parity items 65-71. |
| 2 | §2 W5b · §6a · §5 | **missing** | The FAB glyph swaps `Icons.Play E768` → `Icons.Pause E769` only while the card **owns** playback (the STRICT relation), and the FAB's click then pauses/resumes instead of playing. The LOOSE/STRICT split — an album card of the playing track shows a moving equalizer but keeps ▶ — was absent. |
| 3 | §2 W4 | **wrong** | `n cols 160n−12 ≤ W < 160n+148 (n ≥ 4)` is false at n = 4; the chapter's own explicit line already said 4 columns start at W > 588, not 628. The closed form holds from **n ≥ 5**. Corrected, with the three early-opening bands spelled out. |
| 4 | §2 W4 | **wrong** | "Below 308 the fit produces cells narrower than 148" under-states it: sub-148 cells occur at the bottom of *every* band under 5 columns — W 189 → 2 × 88.5, W 389 → 3 × 121.7, W 589 → 4 × 138.25. From 5 columns up each band opens at exactly 148. |
| 5 | §3b | **wrong** | The InfoBar brushes were reversed. `Tok.SystemFillCaution` is the icon **background circle**; `Tok.TextInverse` is the glyph **ink** (`SeverityVisuals.cs:22`). Cite corrected from `:23` to `:22`. |
| 6 | §0.11 · §3b | **unverified** | `HeaderHeight` / `ColumnMaxWidth` / `RowExtent` are `HomeCustomizerPage.cs:21` / `:22` / `:23`, not `:20` / `:21` / `:22`. Three cites corrected. |
| 7 | §3b | **unverified** | "AutoEdgeFade band 40, runway 24 — `Reconciler.cs:4232`": only the band lives there. The runway 24 is `SceneRecorder.cs:3486` (`EdgeCueRunwayPx`). Both cited now. |
| 8 | §0.17 · §2 W19-A · §10.72 | **missing** | A cold deep link with **no route arg** seeds the title with `" "` (`HomeSectionPage.cs:100`), which `ShellMastheadRegistry.TryResolve` reads as whitespace → `live = null` → `DrillTrail.Of` returns `[]` → the band shows the bare `StaticTitle` ("Home" / "Browse") with **no crumb**, then snaps on Ready. The one path where §0.1 does not hold. |
| 9 | §2 W19-B | **missing** | An expired `wavee:local:` route with no arg falls through `SectionTitle` (`:371-373`) to `Loc(browse.title)` = **"Browse"** — on a *home*-section route. |
| 10 | §2 W19-C · §10.74 | **missing** | `svc is null` or `sectionUri.Length == 0` returns from the mount effect (`:139`) with the loadable still `Pending(8 blank cards)`: a **permanent** breathing skeleton — no empty state, no error state, no log line. |
| 11 | §2 W19-D | **missing** | `isEmpty` is `Cards.Count == 0 && UnsupportedCount == 0` (`:329`), so a section whose entries are ALL unsupported takes the **content** branch and renders a zero-cell grid: a blank column with no copy, not `EmptyState`. |
| 12 | §2 W19-E · §10.75 | **missing** | `ContentHost.cs:193-194` rewrites the legacy synthetic `home-section:spotify:list:recents:main` route to `recents` *before* the home-section arm, and `PublishesShellMaterial` lists the legacy name explicitly (`:185`) so the wash hand-over survives the rewrite. The section page never mounts for it. |
| 13 | §2 W6 · §10.76 | **missing** | Only the two-crumb, from-Browse masthead was drawn. The same chart section opened from **Home** carries a `NavOrigin`, and `DrillTrail.Compose` PREPENDS it (`DrillTrail.cs:83-91`): `Home › Browse › Weekly Song Charts`, both parents clickable. A *home*-section drill never gains a third crumb (`IsRoot`, `:75, 101-104`). |
| 14 | §9 trap 10 · §7 · §8 | **missing** | `HomeModules.SectionGrid` derives its OWN `charts` from `ChartSections.Contains(sectionKey)` — a **bare uri sniff** at `HomeModules.cs:434`, no route prefix in sight — and that sniff decides subtitle blanking *and* the cell reserve. It is exactly the discriminator `HomeSectionPage`'s class comment exists to abolish, and the draft's §9.1 implied there was only one. New trap 10; §8 gains a `ChartSections` row. |
| 15 | §9 trap 11 · §3b · §10.80 | **missing** | The customizer uses hard `Height = 64` / `Height = 48` while the section page's own filter box takes `MinHeight` *because* "a hard Height clips under text scaling" (`HomeSectionPage.cs:302`). The surface breaks its own app's rule, and `ReorderList.ItemExtent` makes it non-trivial to fix. |
| 16 | §6b · §7 · §10.77-79 | **missing** | Three silent persistence paths: a valid `.bak` recovers with **no banner** (`HomeLayoutStore.cs:75-81`); all three load faults (`Corrupt` / `TooNew` / `Unreadable`) share one copy and `FaultDetail` is never rendered; `HomeLayoutSaveFault` (`DocumentTooLarge` > 256 KiB, `IoFailure`) has **no UI at all**. |
| 17 | §2 W5 · §6a · §10.71 | **missing** | Hover is **movement-armed**: `OnPointerMoveWithin` + `HoverMotionGate` (`MediaCard.cs:228, 285-287`). A cell that appears under a stationary cursor — an append, a column re-fit, a KeepAlive unpark — does not arm until the pointer moves. |
| 18 | §5 | **missing** | Two nav recipes were absent: `NavTransitionKind.Neutral` → `MotionRecipes.PageFade` (opacity only, no slide, no 90 ms delay — `PageNavMotion.cs:43`), and the video-safe translate-only pair a swap touching a module page takes (`ContentHost.cs:147-153`). |
| 19 | §5 | **unverified** | The play FAB's reveal was folded into the "⋯" corner's row at `MediaCard.cs:113`. The FAB's own reveal is `FabReveal` (`MediaCard.cs:1428-1436`), an engine `HoverOpacity` on a **non-interactive** wrapper — so the FAB is MOUNTED at all times and hover only reveals it. Split into two rows. |
| 20 | §8 | **missing** | `HasMore` compares `next >= NextOffset` while `CanAdvance` compares `next > requestedOffset`. The two comparators differ deliberately (`HomeSectionPaging.cs:33, 38-39, 46-47`) and the draft named neither. |
| 21 | §3a | **missing** | Three card facts: the label block is `AlignItems = Center` for a circular (Artist) card and `Start` otherwise (`MediaCard.cs:258`); `ClipToBounds = !circular` on the cover stack (`:231-233`); the FAB sits bottom-**right** while the equalizer sits bottom-**left**. |
| 22 | §6a | **missing** | The Charts walk bar carries `Role = AutomationRole.ProgressBar` (`ProgressBar.cs:102`) and is the page's only announced progress; the silent append is a deliberate a11y gap, now stated as one. |
| 23 | §7 · §8 | **missing** | `NowPlayingMatch` and the `PlaybackBridge` signal triple are a CORE dependency of this grid that the plan models nowhere. Added as a §7 data gap (`Entities.PlayingRelation(uri)`) and an §8 row. |

**Verified unchanged (spot-checked against source; no edit needed).** The `(36, 100, 36, 16)` body pad and the
84 = 32 + 52 reserve (`BrowseMastheadMetrics.cs:12-20`, `BrowseTiles.cs:292-297`, `Spacing.cs`); every cell
arithmetic in W1 / W3 / W6 (151 / 203 / 215; 173.33 / 225.33; chrome 52 vs 54 = 14 + 2 × 20 — `HomeModules.cs:510-536`);
the whole §5 skeleton block, including `StaggerRows` stopping at GridBody's two children
(`SkeletonRegion.cs:169-186, 194-219` — `UnwrapTransparentBoundaries` does not unwrap a `BoxEl` and
`FindVirtualRows` bails on `ChildCount != 1`) and `SoftReveal`'s early return under `Motion.ReducedMotion`
(`MotionRecipes.cs:58`); the 250 / 120 / 90 page fade-through (`PageNavMotion.cs:49-59`); `SkeletonStyle`
1000 / 0.5 / 8 / 4 and `ExitMs = Expressive.Fast` 250; the wash geometry (0.06, 0.00) / (0.74, 0.92) / 0.62 with
α 0.10 dark / 0.055 light (`ShellWashGeometry.cs:24-25, 33-34`) and `HomeWashSource.Pick`'s three tiers;
`ProgressBar` 3 / 1 / 1.5 / 0.5 on `StrokeControlStrongDefault` + `AccentDefault` (`ProgressBar.cs:36-39, 106-121`);
`ControlSize.Small` (7,2,7,3) / 24 / 12 / 4 and `IconButton` Small 28 + glyph 14; `ToggleSwitch` 40 × 20,
knob 12 / 14 / 17 × 14, travel 20, box 154 × 40, content gap 12; `InfoBar` 48 / (16,0,0,0) / (0,16,14,16) /
(0,0,16,0) / close 38 + 16 + margin 5; `Reorderable` `LiveProject = true` (`:178`), `LiftOpacity` 0.80 (`:83`),
the row-wrapper-does-not-grow contract (`:329-352`), `ListDwellMs = 200` (`ReorderList.cs:44`),
`DragThresholdPx = 4`, `SourceDimOpacity = 0.4`, and the chip resolver answering only two kinds
(`WaveeResourceDrag.cs:305-347`, `DragChip.cs:89-100`); `SearchHighlight.Row`'s wrap / Grow / Basis / MaxHeight
rules and the (3,1,3,1) pill; the 12 default modules and their labels; every loc key
(`en-US.json:574-592, 543, 682, 685, 1980-1983`); every glyph code (Back E72B, More E712, Edit E70F,
GripperBar E76F, Search E721); the tooltip 800 / 400 / 200 window; all seven test-file line counts in §8; and
the `NearTailWatch` projection key, the 300 ms arm-debounce and the 3-attempt cap
(`HomeSectionAppendPreloader.cs:29-30, 46-54, 91`).

### Round 2 — independent adversarial re-read, 2026-09-12

A second pass over the assigned 0.2.9 sources (`HomeSectionPage.cs`, `HomeSectionNavigation.cs`,
`HomeSectionPaging.cs`, `HomeSectionRoutes.cs`, `HomeCustomizerPage.cs`, `Persistence/HomeLayoutDoc.cs`,
`Persistence/HomeLayoutStore.cs`, `ContentHost.cs`) plus the call sites that reach them. Same rules: nothing
correct was removed; every row below is an edit made in place.

| # | section | kind | correction |
|---|---|---|---|
| 24 | §10.11 | **wrong** | Parity item 11's column boundaries (`628 / 788 / 948 / 1108 / 1268`) contradicted the chapter's own corrected W4: the `ceil` (max-width) arm opens 2/3/4 columns at **189 / 389 / 589**, and 628 is mid-band. Re-derived from `FillRowVirtualLayout.Fit` (`VirtualLayout.cs:484-500`) and rewritten. |
| 25 | §0.19 · §8 · §10.81 | **missing** | The page's own ENTRY rule was absent: `HomeCardNav.OpenBrowseSection` short-circuits a section of exactly ONE card into that card's destination (`HomeSectionNavigation.cs:251-255`), so a one-card browse/Charts section never renders this page — while Home's `OpenSection` (`HomePage.cs:347-355`) has no such rule and does. Added as a non-negotiable, an §8 row and a parity item. |
| 26 | §2 W6 · §10.84 | **missing** | W6 documented two crumb shapes; there are **four**. A section opened from a Browse CATEGORY page carries a SAME-FAMILY origin (`BrowsePage.cs:638-645`) and `Compose` INSERTS it — `Browse › Netflix › New on Netflix` (`DrillTrail.cs:78-82`) — a different arm and a different middle crumb from the Home-origin prepend. Also recorded why a home-section drill cannot grow a third crumb (its only origin is `IsRoot`, `HomePage.cs:274-275`) and what a foreign origin there WOULD do (replace the Home crumb, `DrillTrail.cs:92`). |
| 27 | §0.18 · §2 W11 · §10.82 | **missing** | Nothing said what the masthead does while the grid shimmers. `BlankCards()` seeds `TotalCount = RawItemCount = 8` (`HomeSectionPage.cs:100`), so `HasMore` is false (`HomeSectionPaging.cs:38-39`) and "Show all" is **absent** until the Ready edge — the button's slot is not reserved, so the title column can re-wrap when it appears. |
| 28 | §0.20 · §6b · §10 | **overclaim by omission** | §6b listed the gestures but never said that **every** verdict is discarded: `HomeLayoutRejectReason` and the four localized `home.customizer.undo.*` strings (`en-US.json:587-592`) are wired to nothing (`HomeCustomizerPage.cs:127, 282`). No undo, no toast, no confirmation, no reject message. Stated as a shipped fact so 0.3 does not invent one off the loc keys. |
| 29 | §0.15 | **overclaim** | "`GoBack` flushes so Home re-projects against a document that is already on disk" is not what the code guarantees: `WaitForWrites(200)` waits on one thread-pool task and its bool is discarded, and Home re-projects off the in-memory document + `LayoutVersion` anyway (`HomeLayoutStore.cs:140-142, 204-211`). Reworded to "best effort — durability against a kill, not correctness". |
| 30 | §2 W16 · §6b · §10.87 | **missing** | `DiscardCorrupt` can throw: the moves run inside one try and `_writesBlocked = false` is only reached on success, but `HomePreferences.DiscardCorrupt` clears `Fault`, bumps the version and commits regardless (`HomeLayoutStore.cs:217-231`, `HomePreferences.cs:47-57`). The banner vanishes, the page looks healed, and nothing is ever persisted again — with only a `home.layout.discard_failed` log line. |
| 31 | §6b · §10.88 | **wrong (too broad)** | "A valid `.bak` recovers silently" holds only for a MALFORMED primary: `TooNew` and `Unreadable` return from inside the switch with writes already blocked and never read the backup (`HomeLayoutStore.cs:59-86`). Also added the two easily-missed classifications — a missing file is no fault (`:57`), and `version <= 0` is Malformed (`:115-119`). |
| 32 | §2 W12 · §10.85 | **missing** | The append preloader's 3-attempt cap collapses **per (uri, cursor)** and a transient failure never resets it: the failing leg leaves cursor and exhausted untouched (`HomeSectionPage.cs:581-590`), so the same instance keeps counting and after the third failure the silent infinite scroll is dead for the life of the page (`HomeSectionAppendPreloader.cs:29, 68, 91`). |
| 33 | §2 W13 · §9 trap 5 | **unverified → settled** | The "labels may be invisible" prediction now has the engine rule behind it: `Reorderable.Item`'s wrapper gives its single child no `Grow` (`Reorderable.cs:329-352` — the draft cited `:321-328` / `:315-327`), `AlignItems` defaults to `Stretch` so only the WRAPPER fills the column (`Element.cs:451`), and in a finite-width row `Basis = 0` suppresses intrinsic width by explicit contract (`FlexLayout.cs:590-604`). The row measures 198 DIP and the label is arranged at 0. Item 38 still decides whether 0.3 restores or fixes it. |
| 34 | §2 W20 · §7b | **missing** | Four customizer states were undocumented: (A) a null `HomePreferences` renders a read-only default list with every gesture a no-op and no banner (`HomeCustomizerPage.cs:53-54, 127, 140, 282`); (B) `HomeLayoutWire.Read` sends BOTH unknown kinds and known-but-not-fixed-landing kinds (`shelf`/`topic`/`sectionEntry`) to the carry, so the list is always exactly the fixed-landing set (`Persistence/HomeLayoutDoc.cs:82-97`); (C) `Reset` on an already-default document still writes (`HomeLayoutReducer.cs:21` has no `NoChange` arm); (D) a drop on the source slot writes nothing (`:55-61`). |
| 35 | §6a · §7 W7 · §10.83 | **missing** | The Charts filter has **no clear affordance**: `AutoSuggestBox` never sets `ShowDeleteButton` (only `TextBox` does — `TextBox.cs:82-85`, `EditableText.cs:399`), and with `NoSuggest = []` Esc closes nothing. The filter is also page-local and survives an append and the end of the walk. |
| 36 | §6a | **missing** | The engine's skeleton leg hides the rail of the region's nearest scroll ANCESTOR (`Reconciler.cs:1471, 2735-2756`); this page has none (§0.5), so — unlike every scrolling page in the app — the grid keeps a live scrollbar through the shimmer. One more reason a page-level `ScrollView` in 0.3 would change behaviour invisibly. |
| 37 | §3a | **unverified** | Four cites landed a line or two off the code they name: the grid card's cover stack is `MediaCard.cs:231-253` and its `ClipToBounds` is `:233-235` (not `:231-233`); the label block's `AlignItems` is `:259` (not `:258`); `ProgressBar`'s determinate view is `:97-124` with `Role` at `:103` (not `:102`), and its indicator is the full 3-DIP band over a vertically-centred 1-DIP track. |
| 38 | header · §1d · §9 tree gaps · §9 line budget · §9 missing-files | **arbitration** | arbitration 2026-09-12: **A15 settles the Home file set at seven files, all `Entities/`, all owner P** — `Home.cs` (CORE) · `Home.UI.cs` · `Home.Cards.UI.cs` · `Home.Artists.UI.cs` · `Home.Page.cs` · **`Home.Customizer.cs`** · **`Home.Host.cs`**. This chapter's recommendation is taken with two amendments: the customizer page is `Entities/Home.Customizer.cs`, **not** `Screens/HomeCustomize.UI.cs` (Home stays one owner and one block of the tree), and chapter 11's `Home.Cards.UI.cs` / `Home.Artists.UI.cs` are real files, so `Home.UI.cs` is not the 900-line catch-all this chapter guessed at. The recommended half of the `Browse.Page.cs` bullet is also taken: **`Home.Page.cs` owns the shared section page for both sources**, and `Entities/Browse.Page.cs` exists separately for the directory and category pages (owner P, chapter 13). §1d's tree header, the 0.3-target line, both tree-gap bullets, the budget (now a seven-file table; this chapter's share **1 880**, family **≈8 230**) and the missing-files list are rewritten to it. No pixel, geometry, token, motion or rule in §§0–8 changed. |

**Verified unchanged in round 2 (re-derived independently; no edit needed).** The `Fit` closed form and every
band edge in W4 (`VirtualLayout.cs:484-500`); the cell arithmetic 151/203/215, 173.33/225.33 and chrome
52 = 14 + 20 + 18 vs 54 = 14 + 40, including the 2-DIP trailing slack (`HomeModules.cs:504-536`,
`MediaCard.cs:249-275`); `Spacing` 36/32/24/20/16/12/8/4/2 and `Radii` 4/8/999 (`Spacing.cs:11-24`,
`Radii.cs:10-23`); `BrowseMastheadMetrics` 52 / 84 / 100 / `(36,100,36,16)` (`:12-20`) and
`BrowseLayout.FrameTop/FrameX` (`BrowseTiles.cs:292-297`); the masthead band's row
(`ShellMastheadBand.cs:64-73`), its held-trail fade and its crumb treatment (`:81-130`); `ShellMastheadRegistry`
falling back to `Nav.Home` / `Browse.HomeTitle` on a whitespace title (`NavOrigin.cs:82-107`); the whole
`HomeSectionPaging` ledger including the two different comparators (`:26, 38-39, 46-47, 56-75, 84-89, 113-118`);
`BrowseSectionWalk.Begin/Fold`'s five outcomes (`:24-53`); `ChartTitleMatch` ordinal-ignore-case first
occurrence (`:60-81`); the five `ChartSections` ids (`BrowseTaxonomy.cs:160-181`); every `HomeSectionPage` line
cited in §1a/§3a/§5 (spot-checked 30 of them, all landed); the customizer's 64/720/48, `(8,0,16,0)`,
`(16,12,16,20)`, `(16,8,16,0)`, gaps 8/4/16 and `Opacity 0.55` (`HomeCustomizerPage.cs:21-23, 91-93, 146,
162-163, 264-268`); the 12 default modules, their order and their labels (`HomeLayoutModel.cs:70-84`,
`HomeCustomizerPage.cs:288-310`); `MaxModules` 24 and the reducer's three rejections
(`HomeLayoutReducer.cs:10, 28-61`); the store's `CurrentVersion` 1, 256 KiB cap, `.tmp`/`.bak`/`.corrupt` names
and `File.Replace` cadence (`HomeLayoutStore.cs:24-51, 145-202`); the forward-compat carry's index-preserving
re-emit (`Persistence/HomeLayoutDoc.cs:112-140`); `ContentHost`'s three arms and
`PublishesShellMaterial` (`:184-209`); and all four empty/error strings (`en-US.json:1977-1983`).

**token-reconcile (2026-09-12):** `Tok.FillControlAltSecondary`, named here at `:962` but missing from the first build of `00-design-system.md §12.1`, is now indexed there (`#00000006` light / `#00000019` dark — note the dark arm is a BLACK wash, not a white one). No value in this chapter changed.
