# Shell frame (window layout, toolbar/omnibar, tabs, drill trail, masthead, material, page transitions) — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Shell/WaveeShell.cs` (2348) · `Features/Shell/ContentHost.cs` (322) · `Features/Shell/ShellToolbar.cs` (617) ·
> `Features/Shell/MergedChromeRow.cs` (343) · `Features/Shell/MergedChromeLayout.cs` (192) · `Features/Shell/ShellResponsiveLayout.cs` (247) ·
> `Features/Shell/ShellMastheadBand.cs` (131) · `Features/Shell/DrillTrail.cs` (112) · `Features/Shell/TabWorkspace.cs` (246) ·
> `Features/Shell/WorkspaceTabsPersistence.cs` (55) · `Features/Shell/NavOrigin.cs` (108) · `Features/Shell/NavRouteNormalizer.cs` (28) ·
> `Features/Shell/ShellNav.cs` (83) · `Features/Shell/ShellRoutes.cs` (79) · `Features/Shell/ShellWashGeometry.cs` (55) ·
> `Features/Shell/ShellMaterialLayer.cs` (133) · `Features/Shell/PageNavMotion.cs` (122) · `App/ShellMasthead.cs` (53) ·
> `App/ShellMaterial.cs` (59) · `App/ShellUi.cs` (91) · `App/NavigationFrameWatch.cs` (270) · `Features/Shell/ProfileMenu.cs` (341, chip only)
> **= 5 424 lines excluding ProfileMenu + NavigationFrameWatch** | 0.3 target: `Shell/Shell.cs` (CORE: nav, routes, deep links, responsive layout, page-nav recipes, tint ownership, the first-run composite), `Shell/Shell.UI.cs` (window frame, merged chrome row, tab strip, drill trail, narrow drawer, back/forward flyout, not-found page, network chrome, file-drop target + cue), the named partial `Shell/+Shell.Masthead.UI.cs` (masthead band, material layer, omnibar + suggestion popup) and `Shell/Shell.Host.cs` (window, activation, session snapshot) | Wave 4, owner I

**Path convention.** App paths are relative to `src/apps/Wavee/` (after Wave 0: `src/apps/_old/Wavee/` + the same relative path).
Engine paths are relative to `..\fluent-gpu\src\`. Every number below is `file:line`; computed values give the formula **and** a worked result.

---

## 0. The non-negotiables

1. **One 48-DIP chrome row, and it is the window's title bar.** Nav cluster + text tabs on the left, a search field centred *between the
   clusters*, identity on the right, the theme toggle immediately before the native caption buttons. No second toolbar row, no plate, no
   full-width hairline under it (`TitleBar.ExpandedHeight = 48` `FluentGpu.Controls/TitleBar.cs:102`; `WaveeShell.cs:943-972`; the
   "no seam hairline" decision is `WaveeShell.cs:973-977`).
2. **The chrome, the sidebar band and the player dock paint NOTHING — they are paint-site omissions over live Mica.** Only two regions
   paint: the content region and the right-rail band, both `WaveeColors.FileArea` (dark `#4C3A3A3A`, light `#80FFFFFF`,
   `PaletteBuilder.cs:151` / `:128`) with a 1-px `Tok.StrokeCardDefault` stroke on **left+top only** and corners `8,0,0,0`
   (`WaveeShell.cs:1082-1087`, `:1140`, `:150`, `:164-177`). A page must never read darker than the chrome around it.
3. **The content region's separation is the stroke, never a gutter and never a shadow.** The card is flush on all four sides;
   the only gap in the row is the rail's 8-DIP reservation gap, which exists only while the rail is inline (`WaveeShell.cs:1096-1113`, `:1147-1151`).
4. **Sidebar collapse and the content card move on ONE transition.** 300 ms, `cubic-bezier(0, 0.35, 0.15, 1)`, `SizeMode.Reveal`, the card
   FLIPping against the stable row frame `"shell.content-row"` — the pane's revealing edge and the sheet's left edge are coherent to the
   pixel (`WaveeShell.cs:144-147`, `:184-199`, `:1126-1135`).
5. **Page swaps are a fade-through, never a cross-fade and never a cut.** Exit fades in place over 120 ms `EaseOut`; enter starts at 90 ms
   from `Dx = 8` with opacity 0 over 250 ms `SmoothOut`. Both halves stay `Active` so the card is never empty
   (`PageNavMotion.cs:49-68`). A page hosting composited video takes the translate-only pair instead (`PageNavMotion.cs:96-121`).
6. **The masthead band ("Browse › Category") is mounted ONCE, as an overlay above the keep-alive boundary** — it never takes column
   height, never double-exposes during a swap, and fades opacity (120 ms) on leaving the family instead of collapsing to 0
   (`ContentHost.cs:111-118`, `ShellMastheadBand.cs:23-26`, `:64-73`).
7. **The window's material carries the page's colour.** A detail/artist page publishes a flat tint (dark `A = 0.14`, light `A = 0.05`),
   Home publishes three clipped radial washes; the layer sits between live Mica and the chrome column, cross-fades over 250 ms, and
   NEVER dips to neutral between two coloured pages (`ShellMaterialLayer.cs:93-99`, `CoverPaletteLeaves.cs:240-246`,
   `CoverColorPlane.cs:543-556`).
8. **The washes stop at the player dock line** — the wash host is inset by `PlayerDock.Reserve = 72` at the bottom and clips
   (`ShellMaterialLayer.cs:72-77`). The flat tint stays full-bleed.
9. **Tabs are text-first Zune tabs**: no plate, no separators, no rail; selection is weight 650 + `Tok.TextPrimary` against
   `Tok.TextSecondary`, marked by one strip-owned 2-DIP accent underline that springs between tabs
   (`WaveeShell.cs:1535-1566`, `TabStrip.cs:756-773`, `TabStrip.cs:1008-1037`).
10. **The chrome row is allocated by arithmetic, not by a threshold table.** `MergedChromeLayout.Resolve` subtracts a fixed budget from
    the window width and hands what is left out in one priority order; promotions wait 40 DIP, demotions are immediate; every published
    width is quantised to 10 DIP so a resize drag cannot re-render the bar per pixel (`MergedChromeLayout.cs:51-69`, `:121-145`,
    `ShellResponsiveLayout.cs:29`, `:93`).
11. **The search field never evicts a tab.** Tabs are measured against the 44-DIP magnifier, not against the field: the search yields all
    the way to an icon before a single tab is shed, and below that only `+`, identity and Back are shed, in that order
    (`MergedChromeLayout.cs:128-143`, `ShellResponsiveLayout.cs:100-116`).
12. **Full-screen video unmounts the chrome row and the player bar** (not opacity-0, not off-screen) — one derived predicate drives the
    surface mounting and the chrome leaving so they cannot disagree by a frame (`WaveeShell.cs:843-849`, `:943`, `:1347`, `:1460-1463`).
13. **Nothing in the frame re-renders on navigation except what must.** The shell component renders once and never again for a route
    change; the route reaches the chrome through signals, the material through a dedicated component, the page through the keep-alive
    boundary (`WaveeShell.cs:18-23`, `ShellMaterialLayer.cs:14-20`, `ContentHost.cs:96-108`).
14. **Three retained pages, keyed by tab + route.** Back to a page shows it exactly as it was, including scroll and selection; the fourth
    oldest is evicted (`ContentHost.cs:96-108`, `PageNavMotion.cs:28-29`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
WaveeApp.Render                                          WaveeApp.cs:27      app root: providers + login gate
└─ WaveeShell : Component                                WaveeShell.cs:24    built ONCE; never remounted for theme/route
   └─ Ctx.Provide ×15 → OverlayHost.Create(...)          WaveeShell.cs:1509-1524
      └─ Ui.ZStack shellWithOverlays (Grow=1)            WaveeShell.cs:1474-1507
         ├─ tinted  BoxEl Grow=1 ZStack Fill=Transparent DropTarget=_fileDrop    WaveeShell.cs:1381-1387
         │  ├─ ShellMaterialLayer(_shellMaterial, vpSig)  ShellMaterialLayer.cs:21   the ONE material layer
         │  │  ├─ Tint  Key="shell.material.tint"         ShellMaterialLayer.cs:93-99   flat rect, BrushTransitionMs 250
         │  │  └─ WashHost (Margin bottom 72, clips)      ShellMaterialLayer.cs:72-77
         │  │     └─ Wash ×3 (hero/weekly/mix)            ShellMaterialLayer.cs:101-132  keyed on artwork ⇒ remount = cross-fade
         │  └─ column  BoxEl Direction=1 Height=vp.Height OnKeyDown=OnShellKey    WaveeShell.cs:851-1348
         │     ├─ AmbientPowerPolicy.Watcher (0×0)                                WaveeShell.cs:859
         │     ├─ 14 × zero-size accelerator hosts (Ctrl+T/K/F, Alt+←/→, F11, 8 zoom)  WaveeShell.cs:867-937
         │     ├─ Flow.Show(ShellChromeMounted) → TitleBar (merged)               WaveeShell.cs:943-972
         │     │  ├─ PartPaneToggle "hamburger" 40×44, Margin(6,2,−2,2)           WaveeShell.cs:2020-2027 · TitleBar.cs:300-311
         │     │  ├─ LeftHeaderPad 14                                             TitleBar.cs:310
         │     │  ├─ Tabs island (hug, Shrink=1, Clip, Opacity .5 inactive)       TitleBar.cs:364-372
         │     │  │  └─ MergedChromeRow.Tabs  Key="chrome-tabs-lane"              MergedChromeRow.cs:82-106
         │     │  │     ├─ NavHistoryButton(Back)   40×44 + margin 2/2            ShellToolbar.cs:45-99
         │     │  │     ├─ NavHistoryButton(Forward)                              ShellToolbar.cs:45-99
         │     │  │     └─ TabStripHost → TabStrip(Appearance.Text)               WaveeShell.cs:1527-1571 · TabStrip.cs:546-712
         │     │  ├─ grow drag band                                               TitleBar.cs:383
         │     │  ├─ centre column (Grow=0/Shrink=0 under TabsElasticLane)        TitleBar.cs:386-412
         │     │  │  └─ MergedChromeRow.Center → MergedSearchField                MergedChromeRow.cs:108-115 · :222-277
         │     │  │     └─ FluentRichOmnibar (AutoSuggestBox + rich popup)        ShellToolbar.cs:164-333
         │     │  ├─ grow drag band                                               TitleBar.cs:414
         │     │  ├─ Trailing island (hug)                                        TitleBar.cs:416-431
         │     │  │  └─ MergedChromeRow.Trailing                                  MergedChromeRow.cs:141-161
         │     │  │     ├─ ProfileChip (avatar 24 [+ name])                       MergedChromeRow.cs:201-219 · ProfileMenu.cs:176-192
         │     │  │     ├─ NotificationBellButton (+InfoBadge.Count)              ShellToolbar.cs:105-143
         │     │  │     ├─ NavButton(Friends)                                     MergedChromeRow.cs:152 · :165-167
         │     │  │     ├─ PinButton / PinPlaceholder (44, Opacity 0)             MergedChromeRow.cs:177-190
         │     │  │     └─ NavButton(Settings)                                    MergedChromeRow.cs:154
         │     │  ├─ MinDragStrip 48 (fixed)                                      TitleBar.cs:477-479
         │     │  ├─ CaptionLeading island                                        TitleBar.cs:481-494  (island Opacity .5 inactive, :486)
         │     │  │  └─ MergedChromeRow.CaptionLeading                            MergedChromeRow.cs:117-132
         │     │  │     ├─ ThemeToggle Key="chrome-theme-toggle"                  MergedChromeRow.cs:136-139
         │     │  │     └─ [icon mode] MergedSearchFlyoutButton                   MergedChromeRow.cs:279-343
         │     │  └─ CaptionButton min / max / close  46 each                     TitleBar.cs:496-505 · CaptionButton.cs:19-21
         │     ├─ content REGION = Ui.ZStack(...) with Grow=1 Shrink=1 MinHeight=0 ClipToBounds
         │     │                    OnRealized→PublishScrimClip                   WaveeShell.cs:978-1343
         │     │  ├─ row  MorphId="shell.content-row" Direction=0 Grow=1 Clip     WaveeShell.cs:986-1205
         │     │  │  ├─ sidebar pane  Shrink=0 Clip Width=bound Animate=SidebarPaneAnim   WaveeShell.cs:1002-1044
│     │  │  │     Width = presentedCompact && !_sidebar.DragPeek ? 56 : _sidebarWidth  (:1014-1015)
         │     │  │  │  └─ fade box Opacity=_sidebarFade IsolateLayout Clip       WaveeShell.cs:1026-1042
         │     │  │  │     └─ SidebarHost(route, go, presentedCompact, width)     WaveeShell.cs:1041  → ch 25
         │     │  │  ├─ content region  ZStack Grow=1 Shrink=1 MinW/H=0 Basis=0   WaveeShell.cs:1050-1142
         │     │  │  │  ├─ FileArea underlay, Corners 8,0,0,0                     WaveeShell.cs:1082-1087
         │     │  │  │  ├─ content CARD  Fill=Transparent Clip IsolateLayout
         │     │  │  │  │                Animate=ContentCardAnim RelativeTo=row   WaveeShell.cs:1088-1137
         │     │  │  │  │  └─ ContentHost(route, navMotion, ActiveTabId, settings) ContentHost.cs:21-121
         │     │  │  │  │     ├─ 2 route effects: neutral-material claim (:51-55) + ActiveStagePlayable clear
         │     │  │  │  │     │  AND `NavigationFrameWatch.NoteRoute` (:71-77) — the always-on nav.frames window
│     │  │  │  │     │  root Padding bottom = Bridge.FloatingSurfaceReserve   ContentHost.cs:45, :81
│     │  │  │  │     │  (UNCONDITIONAL wrapper, 0 when nothing is reserved — mounting/unmounting it
│     │  │  │  │     │   would remount KeepAlive and cold-restart every cached page, :37-40)
         │     │  │  │  │     ├─ clip layer → Flow.KeepAlive(PageSlot, SlotKey, PageFor, opts)  ContentHost.cs:88-110
         │     │  │  │  │     │                 MaxEntries 3 · TransitionFor=PageTransition
         │     │  │  │  │     └─ masthead overlay (HitTestPassThrough)            ContentHost.cs:111-118
         │     │  │  │  │        └─ ShellMastheadBand(route)                      ShellMastheadBand.cs:21-131
         │     │  │  │  │           └─ TitleRow(trail, go, title) + "Show all"    ShellMastheadBand.cs:59-73 · :81-130
         │     │  │  │  └─ ContentRegionStroke (topmost, paint-only)              WaveeShell.cs:1140 · :164-177
         │     │  │  ├─ rail GAP  Width = open&&fits ? 8 : 0                      WaveeShell.cs:1147-1151
         │     │  │  └─ rail SPACER Width = open&&fits ? RailWidth : 0            WaveeShell.cs:1163-1203
         │     │  │     └─ FileArea underlay, Corners 8,0,0,0, Margin(0,0,−1,−1)  WaveeShell.cs:1189-1199
         │     │  ├─ sidebar splitter overlay (16 wide, translated to the seam)   WaveeShell.cs:1209-1239
         │     │  ├─ rail splitter overlay (16 wide, Leading polarity)            WaveeShell.cs:1240-1257
         │     │  ├─ rail OVERLAY (constant RailWidth, Clip, ZStack)              WaveeShell.cs:1258-1312
         │     │  │  ├─ floating backing (FloatingChrome when !RailFits)          WaveeShell.cs:1284-1291
         │     │  │  └─ panel host IsolateLayout Clip Corners 8,0,0,0 → RightRail WaveeShell.cs:1292-1308  → ch 21
         │     │  └─ ShellNarrowDrawer(narrow, open, vp, width, compact, route)   WaveeShell.cs:1315-1316 · :2171-2348
         │     │     ├─ ShellNarrowDrawerScrim  #33000000                         WaveeShell.cs:2241-2261
         │     │     └─ ShellNarrowDrawerPane   acrylic + Elevation.Flyout        WaveeShell.cs:2291-2348 → SidebarHost (2nd mount)
         │     └─ Flow.Show(ShellChromeMounted) → PlayerBar                       WaveeShell.cs:1347  → ch 20
         ├─ immersiveLyricsLayer        Flow.Show(ShellUi.ImmersiveLyrics)        WaveeShell.cs:1440-1455 → ch 22
         ├─ runtimeBannerLayer          Padding top 48+8, MaxWidth 560            WaveeShell.cs:1396-1405 → ch 19
         ├─ fileDropLayer               centred pill, bound opacity               WaveeShell.cs:1418-1432
         ├─ WaveeCommandPalette.Overlay(_paletteOpen, …)                          WaveeShell.cs:1475 → ch 19
         ├─ ActionServicesOverlayBinder / SidebarOnboardingChrome / SetupChrome /
         │  ReportChrome / AfterUpdateChrome / SidebarBinder.MountPoint           WaveeShell.cs:1476-1497 → ch 19/26/28
         ├─ InWindowVideoPip · VideoPlacementHost · videoFullscreenLayer          WaveeShell.cs:1498-1500, :1460-1473 → ch 24
         ├─ SetupCoverScrim (Tok.FillSmoke, 250 ms)                               WaveeShell.cs:1506 · :2272-2289 → ch 28
         └─ DragPreviewLayer.Of(WaveeResourceDrag.Preview)                        WaveeShell.cs:1507 → ch 01
```

**State owned by the shell** (all `WaveeShell.cs:26-131`): `_route` `Signal<Route>`, `_navMotion`, `_canBack`, `_canForward`,
`_history`/`_forwardHistory` (`List<Route>`, cap `MaxBackStack = 200`, `:30`), `_historyStore`, `_session`, `_navPreview`,
`_homeSectionPreview`, `_browseDirectory`, `_browsePages`, `_shellMaterial`, `_shellMasthead`, `_navOrigins`, `_shellUi`,
`_videoFullscreenUserOpen`, `_actions`, `_tabs` (`TabWorkspace`) + `_tabsVersion` + `_selectedTab`, `_chromeLayout`,
`_searchFocusRequest`, `_paletteOpen`, `_searchFocused`, `_searchFlyoutOpen`, `_tabNaturalExtent`, `_searchText`,
`_drawerExpanded`, `_sidebarCompact`/`_sidebarWidth` (aliases of `SidebarPreferences`), `_sidebarDragging`, `_sidebarFade`,
`_rightRailFade`, `_fileDropOver`.

### 1.2 The same tree in 0.3 terms

| 0.2.9 node | 0.3 home | Shape | Inputs (how data reaches it) |
|---|---|---|---|
| `WaveeShell` fields + ctor restore | `Shell.Host.cs` | `static partial class Shell` statics | `Shell.Boot()` seeds from `Platform.Settings` + `session.json`; every cell a `Signal<T>` created once |
| `Route`, `Go/Back/Forward`, normalizer, route table, `ShellNav.Dest`, `DrillTrail`, masthead registry, `TabWorkspace`, `MergedChromeLayout`, `ShellResponsiveLayout`, `PageNavMotion` | `Shell.cs` **CORE** | pure statics + structs | values only — no signals, no `Element` |
| wash geometry, tint ownership, wash/tint colour derivation | `Shell.Palette.cs` **CORE** | pure statics | `ColorF` in / out |
| `WaveeShell.Render` body | `Shell.UI.cs` → `Shell.Root : Component` | Component (mounted once) | reads `Viewport.Size`/`Viewport.Zoom` contexts + the `Shell.*` signals; **no ctor args** |
| chrome row builders (`MergedChromeRow`) | `Shell.UI.cs` → `static Element Chrome*(…)` | plain static builders invoked inside `TitleBar.Render` (no hooks allowed there) | `Shell.ChromeLayout` signal, `Shell.CanBack/CanForward`, `Shell.SearchText`, `Shell.TabsVersion` |
| `MergedSearchField` / `MergedSearchFlyoutButton` / `FluentRichOmnibar` / `OmnibarSuggestionsPopup` | `+Shell.Masthead.UI.cs` | Components (they own hooks: focus tickets, debounce, overlay handles) | `Signal<string> Shell.SearchText`, `Shell.SearchFocusRequest` (monotonic int ticket), the suggest store as a field on one long-lived instance |
| `NavHistoryButton` | `Shell.UI.cs` | Component | `Signal<bool> canDo` + the **live** `List<Route>` reference (read at flyout-open time, `ShellToolbar.cs:50`) |
| `TabStrip` config + `BuildTabItems` | `Shell.UI.cs` | `Func<IReadOnlyList<TabViewItem>>` + `ItemsVersion` thunk | `Shell.TabsVersion`/`Shell.SelectedTab` signals; items rebuilt on version bump |
| `ContentHost` | `Shell.UI.cs` → `Shell.Content : Component` | Component | `Signal<Route>`, `Signal<NavTransitionKind>`, `Func<int> activeTabId` — **pages get Route VALUES, never the signal** (`ContentHost.cs:19-20`) |
| `ShellMastheadBand` | `+Shell.Masthead.UI.cs` → `Shell.Masthead : Component` | Component | `Signal<Route>` + the masthead store + origin store from context |
| `ShellMaterialLayer` | `+Shell.Masthead.UI.cs` → `Shell.Material : Component` | Component | `IReadSignal<ShellMaterialState>`, `IReadSignal<Size2>` (viewport read through **bound** props only) |
| `ShellNarrowDrawer{,Scrim,Pane}` | `Shell.UI.cs` | Components | `IReadSignal<bool> narrow`, `Signal<bool> open`, viewport, width signals |
| `ShellUi`, `ShellMaterial`, `ShellMasthead`, `NavOriginStore` context slots | `Shell.Host.cs` | `Context<T>` statics, provided once at the root | unchanged shape |

**Props freeze at mount — where the 0.3 tree must use a Signal/Func/Key:**

* `Shell.Root`, `Shell.Content`, `Shell.Material`, `Shell.Masthead` are all mounted once for the process. Every value they show changes
  through a signal read **inside `Render`** or through a **bound prop** (`Prop.Of(...)`). The 0.2.9 precedent is explicit:
  `ProfileMenu` takes `IReadSignal<MergedChromeLayout>` rather than a `bool showName`, "a `ComponentEl` never re-runs its factory, so a
  plain bool ctor arg would freeze at mount" (`ProfileMenu.cs:41-44`).
* `MergedChromeRow.Bridge/Ui/Acts` are **plain fields refreshed by the shell every render** (`WaveeShell.cs:837-839`) because slot
  builders run inside `TitleBar.Render` and cannot call `UseContext`. Keep that idiom: the builder object is reference-stable, its
  service fields are re-assigned, never re-constructed.
* `TabStrip.SelectedIndex` is a **controlled cell the engine writes before it raises `OnSelectionChanged`** — never read it back to decide
  whether a tab changed; ask the workspace model (`WaveeShell.cs:1557-1562`, `TabWorkspace.cs:20-26`).
* Keyed remounts that must survive: page slots keyed `tabId␟name␟arg` (`PageNavMotion.cs:28-29`); wash layers keyed
  `shell.wash.<leg>:<artworkKey>` so a re-grading cross-fades by mount (`ShellMaterialLayer.cs:110`); the tint node keeps a **stable**
  key so its `BrushTransition` has a previous colour to fade from (`ShellMaterialLayer.cs:95`); `page:whatsnew:<arg>`,
  `page:sidebar-customize:<arg>`, `page:<module route>` key by arg so two instances are two slots (`ContentHost.cs:236`, `:251`, `:280`).
* `ReuseGuard` trap: the content card carries `IsolateLayout` + `ClipToBounds` + `Animate`; do not let a page's re-render escape to a
  full-tree layout (`WaveeShell.cs:1115-1118`) and do not put a layout boundary on a box whose own size animates (`WaveeShell.cs:1030-1040`).

---

## 2. Wireframes

Scale is declared per frame. Full-window frames use **1 char ≈ 16 DIP**; chrome-row detail frames use **1 char ≈ 8 DIP**.
Vertical proportions are annotated, not drawn to scale.

### W1 — wide, loaded, rail closed @ 1600×900 (1 char ≈ 16 DIP)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48  chrome row (no fill: live Mica)
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋      [ 🔎 Search songs, artists, albums...  ]      ◉ Christos  🔔 👥 📌 ⚙│  — │ 48 tall, ExpandedHeight
├──────────────┬─────────────────────────────────────────────────────────────────────────────┬───────┤    ⌄ theme toggle then ─ □ ✕
│              │╭────────────────────────────────────────────────────────────────────────────╮       │
│  sidebar     ││ content region: FileArea over Mica, 1px stroke on LEFT+TOP, corner 8,0,0,0 │       │
│  280 DIP     ││                                                                            │       │
│  (Mid tier,  ││   ← the page (ch 03/05/10/…) lives here, flush to all four edges            │       │
│   no fill)   ││                                                                            │       │ 780 = 900 − 48 − 72
│              ││                                                                            │       │
│              │╰────────────────────────────────────────────────────────────────────────────╯       │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤
│  player bar — 72 DIP, paints nothing (Mica), ch 20                                                  │ 72
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
  ↑ sidebar 280           ↑ content 1320 (= 1600 − 280)                                       rail: closed ⇒ page is flush to the window edge
```

Chrome-row allocation at 1600, 2 unpinned tabs (`MergedChromeLayout.Resolve`, `MergedChromeLayout.cs:51`):

| segment | DIP | source |
|---|---|---|
| root padding 2 + pane toggle **advance 44** (40 wide, margin 6 left / −2 right) + LeftHeaderPad 14 | **60** | `ShellResponsiveLayout.cs:57` (`ChromeBarLeadW`) |
| Back 44 + Forward 44 | **88** | `ChromeNavButtonW` `:60` |
| tab lane reservation `LeadClusterW` | **240** | `LeadClusterFor` `MergedChromeLayout.cs:159-171`; `ComfortableTabExtent(220) = clamp(ceil10(220×0.78)=180, 240, 720) = 240` |
| grow drag band A | **132** | free space ÷ 2 |
| search field | **420** | `PreferredSearchWidth(1600) = quantise10↓(clamp(1600×0.28, 280, 420)) = 420` |
| grow drag band B | **132** | |
| trailing island: chip 32 + name 90 + 4×44 | **298** | `ChromeProfileChipW/NameW` `:69/:72`, `FixedBudget` `:113` |
| MinDragStrip | **48** | `ChromeMinDragStripW :79` (engine pins it, `TitleBar.cs:477-479`) |
| theme toggle | **44** | `ChromeThemeToggleW :84` |
| caption cluster (3×46) | **138** | `ChromeCaptionClusterW :81` · `CaptionButton.Width = 46` |

Free = 1600 − 1336 = 264 ⇒ 132 per band (the bar's two drag bands are `Grow=1, Shrink=1` with **equal** weight, `TitleBar.cs:383`, `:414`).
**The field's centre lands at x = 730, 70 DIP left of the window centre** — the bar centres the island between the two clusters, not in the
window (`TitleBar.cs:376-381`). Do not "fix" this.

**Two different sums — do not conflate them.** The table above is the *rendered* row (what a screenshot measures). The *allocator's*
`FixedBudget` at this stage is **724** = 60 lead + 44 theme + 44 back + 44 fwd + **32 add slot** + 32 chip + 90 name + 4×44 actions +
**2×8 gutter floor** + 48 drag strip + 138 caption (`MergedChromeLayout.cs:105-116`). The add slot and the two 8-DIP gutter floors are
**charged but never laid out as their own segments**: the "+" lives inside the tab strip's hug and the gutters are the floor the two
elastic drag bands must never fall below. So `available = 1600 − 724 − 420 = 456`, `LeadClusterW = quantise10↓(min(240, 456)) = 240`.

**The tab extent input is MEASURED, not estimated.** `Resolve` takes `naturalTabExtent` from `_tabNaturalExtent`, which the strip publishes
through `ScrollMetricsChanged → OnTabStripMetrics`: `max(110, round10(metrics.ContentExtent))` — **round**, not floor
(`WaveeShell.cs:1587-1593`). `MergedChromeLayout.EstimatedTabExtent(count, pinned) = pinned×40 + (count−pinned)×110` is only the **upward
seed** applied the instant a tab is added/unpinned (`SeedTabExtent`, `WaveeShell.cs:1595-1602`) so the row can collapse the search in the
same event turn instead of squeezing the new tab behind a stale measurement. Every worked example below uses the seed value for legibility.

**Two floors the worked examples never hit but the 0.3 port must keep:** `Resolve` floors `naturalTabExtent` at
`ChromeTabViewportMinW = 32` before anything else (`MergedChromeLayout.cs:54`), and `EstimatedTabExtent` floors the tab count at 1
(`:82`) — so a zero-tab workspace (unreachable: `TabWorkspace` always seeds Home) still resolves a sane row rather than a degenerate
one. `LeadClusterFor` also floors its own result at 32 twice, once on `available` and once on the held value (`:163`, `:170`), which
is why the W7 ladder's last row can report a 70-DIP lane instead of clipping to nothing.

### W2 — name folded @ 1360 → 1200 (1 char ≈ 8 DIP, chrome row only)

```
x=0   60      148          388  420               800  832        1130    1178  1222        1360
│ ☰  │◀ │▶ │ tab lane 240 │←32→│ search field 380 │←32→│ chip+name+🔔👥📌⚙ 298 │48 drag│ ⌄ │ ─ □ ✕ 138 │
      └ back/forward 44 each     └ PreferredSearchWidth(1360)=quantise10↓(380.8)=380 … at 1200 → 330
```

Rendered fixed = 60 + (88 + 240) + 380 + 298 + 48 + 44 + 138 = **1296**; free = 64 ⇒ **32 per band**; the field's centre is x = 610, again
70 DIP left of the window centre (680). Allocator view: `FixedBudget = 724`, `1360 − 724 − 380 = 256 ≥ 240 = tabComfort` ⇒ **Field**.

* `ChromeNameEnterW = 1360` (`ShellResponsiveLayout.cs:34`): **below 1360 the display name disappears and the chip is the bare 24-DIP
  avatar**; the four action buttons stay.
* Promotion back to the named chip waits until `1360 + 40` hysteresis is satisfied by `Resolve`'s reserve probe
  (`MergedChromeLayout.cs:58-68`).
* **Computed edge case to preserve:** at exactly 1200 with 2 tabs the actions arrive (`ChromeActionsEnterW = 1200`, `:42`) and the search
  demotes to the icon for a 4-DIP band — `1200 − 634 − 330 = 236 < 240 = tabComfort` — recovering at 1204+. Demotion is immediate,
  re-promotion waits the 40-DIP reserve.

### W3 — actions folded into the profile menu @ 1000 (1 char ≈ 8 DIP)

```
x=0   60      148          388 423              703 738 770     818   862        1000
│ ☰  │◀ │▶ │ tab lane 240 │←35→│ search field 280│←35→│◉│ 48 drag │ ⌄ │ ─ □ ✕ 138 │
                                                       ↑ bare avatar chip 32 (no name, no bell/friends/pin/settings)
```

Budget = 60+44+44+44+32+32+16+48+138 = **458**; search = `clamp(280, 280, 420) = 280`; tab lane = `1000 − 458 − 280 = 262 ≥ 240` ⇒ **Field**.
Bell and Friends become rows in the profile menu (`ProfileMenu.cs:209-219`); Settings is always a menu row; **Pin simply drops** — the tab
context menu still offers it (`ShellResponsiveLayout.cs:36-41`).

### W4 — search collapsed to the magnifier @ 880 (1 char ≈ 8 DIP)

```
x=0   60      148          388                          614 646  694   738   742   880
│ ☰  │◀ │▶ │ tab lane 240 │←──── one continuous drag band ────→│◉│48 drag│ ⌄ │ 🔎 │ ─ □ ✕ 138 │
                                                                         ↑ theme toggle FIRST, then the magnifier
```

**Order, and it is not the one you would guess:** `CaptionLeading`'s children are built `[ThemeToggle, MergedSearchFlyoutButton]` into a
`Direction = 0` row (`MergedChromeRow.cs:120-123`), so the magnifier sits **to the RIGHT of the theme toggle**, immediately before the native
caption buttons. In icon mode `MergedChromeRow.Center` returns a `0×0` box (`:114`), so the two `Grow=1` drag bands abut and read as **one**
continuous band rather than two flanking a field.

Field boundary for 2 tabs = `458 + 280 + 240 = 978`; below it `SearchMode = Icon`, `SearchWidth = 44`
(`MergedChromeLayout.cs:138-143`, `:149`). Clicking the magnifier opens a `FlyoutPlacement.BottomEdgeAlignedRight` popup whose width is
`clamp(viewport − 24, 44, 420)` carrying the same omnibar with **inline** suggestions (`MergedChromeRow.cs:296-329`).
A live mode flip while the field was focused re-issues a focus ticket so the caret survives (`WaveeShell.cs:820-826`).

### W5 — narrow shell (compact rail) @ 720×760 (1 char ≈ 16 DIP)

```
┌──────────────────────────────────────────────┐ 48
│☰ ◀▶│Home│Liked Songs│ ＋      ◉  48drag ⌄🔎 ─□✕│    search is the magnifier (right of ⌄); no bell/friends/pin/settings
├───┬──────────────────────────────────────────┤
│ ♫ │╭─────────────────────────────────────────╮      sidebar = CompactRailW 56, icons only (ch 25)
│ 🔍│││ content 664 DIP                        │      narrow band: width ≤ 720 enters, < 760 leaves
│ 📚│││                                        │      (ShellResponsiveLayout.NarrowFor :225-230)
│ ＋│││                                        │ 640
│   │╰─────────────────────────────────────────╯
├───┴──────────────────────────────────────────┤
│ player bar 72                                │
└──────────────────────────────────────────────┘
```

In narrow mode `presentedCompact` is forced true (`WaveeShell.cs:530-531`) and the **saved desktop expand/collapse preference is not
overwritten** — the hamburger opens the drawer instead (`WaveeShell.cs:460-471`). The sidebar splitter overlay is `Width = 0`
(`WaveeShell.cs:1211`).

### W6 — narrow drawer open @ 688 (1 char ≈ 16 DIP)

```
┌──────────────────────────────────────────┐
│☰ …chrome unchanged…                      │
├──────────────────────┬───────────────────┤
│ drawer pane          │ scrim #33000000   │   pane width = clamp(_sidebarWidth, 240, viewport−32) = 240…656
│ acrylic Tok.Acrylic  │ (click = close)   │   corners 0,8,8,0 · border 1 StrokeCardDefault · Elevation.Flyout
│ Flyout + Elevation   │                   │   translate −width→0 over 300 ms SmoothOut
│ .Flyout              │                   │   Escape closes via a KeyPreview chain (WaveeShell.cs:2196-2219)
│ (2nd SidebarHost     │                   │
│  mount, same state)  │                   │
└──────────────────────┴───────────────────┘
```

`DrawerWidth = min(max(240, preferred), max(56, viewport − 32))` (`ShellResponsiveLayout.cs:239-243`).

**The drawer's sidebar is always the EXPANDED one.** Its second `SidebarHost` takes `_drawerExpanded` — a shell signal seeded
`false` and never written — as its `compact` argument, plus `inDrawer: true` (`WaveeShell.cs:107`, `:1316`, `:2345`). So while the
inline band is the 56-DIP compact rail, the drawer over it shows full rows. Both mounts share the same `_sidebarWidth` signal and
the same `SidebarPreferences`: **one mode, one state, two mounts.**
The drawer root and pane are both `HitTestVisible = open` and the pane keeps its scene nodes while closed — open/close is scrim
opacity + a translate, nothing else (`WaveeShell.cs:2223`, `:2342`, `:2169-2170`).

### W7 — extreme shedding @ 420 (1 char ≈ 8 DIP)

```
x=0   60                         130          178   222        420
│ ☰  │ tab lane 70 (1 clipped tab) │←─ drag ─→│ 48 drag │ ⌄ │ 🔎 │ ─ □ ✕ 138 │
   no Back, no Forward, no "+", no identity chip — 420 is BELOW all three thresholds
```

Shed order (`MergedChromeLayout.cs:128-136`) — `+` first, then the trailing island (identity), then Back. Worked thresholds for 2 tabs
(each stage re-runs `FitsEssential`, so each threshold is computed against the budget the previous shed already reduced):

| width | budget | `w − budget − 44` vs `ChromeTabViewportMinW` 32 | result |
|---|---|---|---|
| 490 | 414 (no name, no actions, fwd hidden below 520) | 32 | `+` survives; **below 490 it drops** |
| 458 | 382 (`+` gone) | 32 | trailing survives; **below 458 the identity chip drops** |
| 426 | 350 (`+` and trailing gone) | 32 | Back survives; **below 426 Back drops** |
| 420 | 306 (everything shed) | 70 | the floor — `306 + 44 (icon) + 32 (tab viewport) = 382` |

**So @470 only the "+" is gone** — the identity chip and Back are both still in the row. `MinWidth = 300` is the OS window floor
(`Program.cs:540`); between 300 and 382 the tab lane simply clips. Forward is not part of this ladder at all: it is the fixed
`width > ChromeForwardEnterW (520)` stage (`MergedChromeLayout.cs:125`), hidden **at 520 itself** and re-promoted only at 561.

### W8 — right rail open, inline @ 1600 (1 char ≈ 16 DIP)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48 chrome
├──────────────┬──────────────────────────────────────────────────────────┬─┬─────────────────────────┤
│ sidebar 280  │ content 972 = 1600 − 280 − 8 − 340                       │ │ rail band 340           │
│              │ FileArea + stroke                                        │8│ FileArea + corner 8,0,0,0│
│              │                                                          │ │ (RightRail paints       │
│              │                                                          │g│  Transparent while docked)│
├──────────────┴──────────────────────────────────────────────────────────┴─┴─────────────────────────┤
│ player bar 72                                                                                        │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘
   RailFits ⇔ sidebar + rail + 480 ≤ viewport   (ShellUi.CanFitRail :88-90 → ShellResponsiveLayout.cs:164-165)
   RailWidth clamp 200…500, default 340 (:126)   the 8-DIP gap exists ONLY while inline (WaveeShell.cs:1147-1151)
```

### W9 — right rail floating @ 1000 (1 char ≈ 16 DIP)

```
┌──────────────────────────────────────────────────────────────┐
├──────────────┬───────────────────────────────────┬───────────┤   1000: 240 + 340 + 480 = 1060 > 1000 ⇒ !RailFits
│ sidebar 240  │ content 760 (NOT resized)         │ rail 340  │   spacer width 0; the overlay covers the page
│              │                                   │ backing = │   backing = WaveeColors.FloatingChrome (opaque)
│              │                                   │ Floating  │   (WaveeShell.cs:1284-1291)
│              │                                   │ Chrome    │
└──────────────┴───────────────────────────────────┴───────────┘
```

### W10 — page swap in progress (t ≈ 100 ms after a forward navigation), content card only (1 char ≈ 8 DIP)

```
╭──────────────────────────────────────────────────────────╮
│  outgoing page: opacity ≈ 0.06, Dx 0 (fades in place)    │   exit: 120 ms EaseOut, delay 0, Active
│  ╭────────────────────────────────────────────────────╮  │   enter: starts at 90 ms, Dx 8 → 0, opacity 0 → 1,
│  │ incoming page: opacity ≈ 0.1, Dx ≈ 7 DIP right     │  │          250 ms SmoothOut  (PageNavMotion.cs:52-59)
│  ╰────────────────────────────────────────────────────╯  │   both roots are attached; the card is NEVER empty
╰──────────────────────────────────────────────────────────╯   masthead band fades on the SAME 120 ms window
```

### W10b — the THREE channels of one navigation, composed (`browse:<uri>` → `pl:<uri>`; time annotated, not to scale)

```
 t(ms)   0        90       120                250                340
         ├────────┼────────┼──────────────────┼──────────────────┤
 page    │◀── exit: opacity 1→0, 120 ms EaseOut, in place ──▶│
         │        │◀── enter: Dx 8→0, opacity 0→1, 250 ms SmoothOut ──────▶│
 band    │◀── Hide: opacity 1→0, 120 ms FluentAccelerate ──▶│   showing the OLD (browse) trail
 material│◀── tint rect: NeutralGround → the playlist's colour, 250 ms BrushTransition ──▶│
         ↑ ALL THREE start on the SAME route flush: `_navMotion` is written, then `_route`
           (WaveeShell.cs:1866, :1868) — the page recipe from ContentHost.PageTransition (:147-153),
           the band from its own route read (ShellMastheadBand.cs:42, :69-71), the material from the
           page's claim effect / ContentHost's neutral effect (ContentHost.cs:51-55).
```

**The chrome's colour LEADS the page.** The material ramp starts at t = 0 and is done at 250 ms; the incoming page does not
begin arriving until 90 ms and is not settled until 340 ms. The window is already wearing the destination's colour while the
destination is still fading in — that is the intended order, not a timing bug to "fix" by delaying the material.

**The dashed case.** The ramp above assumes the playlist's cover is already graded. If it is not, the claim resolves to
`WriteHeldColor` (`CoverColorPlane.cs:552-554`) — the ORIGIN's material stays on screen, owner transferred, and the 250 ms
ramp starts whenever the grading lands, long after the page swap has finished. Both readings are correct; §5.1 marks every
destination this can happen to.

### W11 — sidebar collapsing (t ≈ 150 ms of 300 ms) @ 1280 (1 char ≈ 16 DIP)

```
├───────┬──────────────────────────────────────────────────┤   pane clips its content (Reveal: laid out at FINAL size,
│ pane  │ content card FLIP-translating left, width easing │   only a clip window + translate ease)
│ 168/  │ RelativeTo "shell.content-row"                   │   the FileArea underlay + the stroke stay STATIC —
│ 280→56│ (the static underlay stays put and fills behind) │   the edge must not slide with the card
```

### W12 — sidebar seam drag, inside the resist zone (1 char ≈ 16 DIP)

```
│ pane (content Opacity → 0.35)   ┆│ 16-DIP grip strip, cursor SizeWE, no painted indicator
   raw pointer width  240 → 176      width = 240 − 0.28 × (240 − raw)      (SplitterMath.ResistWidth)
   fade               1.0 → 0.35     fade  = 1 − t(1 − 0.35), t = into/64  (SplitterMath.Fade)
   collapse fires at raw ≤ 176 (into ≥ ForcePush 64) → the pane snaps to the 56 rail, fade back to 1
   re-expand while collapsed: drag out past ReExpand 190
```

Configured at `WaveeShell.cs:1227-1237`; math at `FluentGpu.Controls/SplitterMath.cs:21-47`.
**Derived, and a trap:** with `FadeStart = 240` and `Resist = 0.28` the *committed* width can never land between 180 and 222.08
(= 240 − 0.28×64) even though `NavPaneMinW = 180` (`ShellResponsiveLayout.cs:121`). The 180 floor is reachable only through
`SidebarPreferences`' own clamp, not through this drag.

**Drag PEEK — the collapsed pane widens while you drag it.** The pane's bound width is
`presentedCompact && !_sidebar.DragPeek ? 56 : _sidebarWidth` (`WaveeShell.cs:1014-1015`), so a drag that starts on a
*collapsed* rail presents the pane EXPANDED for the duration: the pane column is `ClipToBounds`, and a 56-DIP column around
expanded rows renders as a strip of art + tree connectors with every label cut off. The 0.3 port must keep the peek term in
the same bound expression — deriving the width from `presentedCompact` alone is the regression.

### W12b — right-rail seam drag (1 char ≈ 16 DIP) — the detent §6 used to deny

```
 … content │╱│ rail (panel Opacity → 0.35 via _rightRailFade, bound at WaveeShell.cs:1306)
            ↑ 16-DIP Leading-polarity grip, translated to vp.W − RailWidth − 16 (WaveeShell.cs:1244-1245)
   raw ≥ 200  → ordinary clamp, width 200…500, commit persists ShellRailWidth
   raw < 200  → RESIST zone: width = 200 − 0.28 × (200 − raw); panel fades 1 → 0.35 over 44 DIP
   raw ≤ 156  → the rail CLOSES (ForcePush = FadeDistance = 44 below FadeStart = Min = 200)
   drag back out past 210 (ReExpand default) → the rail re-opens
```

The rail splitter is created with **no** `FadeStart`, `FadeDistance`, `MinFade`, `Resist`, `ForcePush` or `ReExpand`
(`WaveeShell.cs:1248-1255`), so every one of those is the engine default: `FadeStart = Options.Min` (`Splitter.cs:144`),
`FadeDistance 44` `:68`, `MinFade 0.35` `:69`, `Resist 0.28` `:70`, `ForcePush = FadeDistance` `:145`, `ReExpand 210` `:73`.
`collapsed:` is bound to `ShellUi.RailOpen` with `InvertCollapsed: true`, which is what turns "collapse the pane" into
"close the rail". It also passes **no `dragging:` signal**, so — unlike the sidebar seam — a rail drag does **not** snap the
window's layout transitions; only the sidebar drag does (`WaveeShell.cs:134-135`, `:1237`).

### W13 — masthead band, Browse family @ 1280 (1 char ≈ 8 DIP)

```
      x=36 (FrameX = Spacing.PageWide)                                             right gutter 36
      ┆                                                                                       ┆
 32   ┆  Home ›  Browse ›  Weekly Song Charts                            [ Show all ]         ┆  ← FrameTop = Spacing.XXXL
      ┆  ╰ TextTertiary, clickable, hover→TextSecondary   ╰ current: TitleLarge 40/52,        ┆
      ┆                                                     Display face, weight 400, −12/1000┆
      ┆                                                     MaxLines 2, ellipsis              ┆
 ─────┆────────────────────────────────────────────────────────────────────────────────────── ┆
         the page body scrolls UNDER this band; the band paints NOTHING
         reserve = Spacing.XXXL + 52 = 84 DIP (BrowseMastheadMetrics.cs:12-13)
```

`ShellMastheadBand.cs:64-73` (`Padding = (BrowseLayout.FrameX 36, BrowseLayout.FrameTop 32, 36, 0)` — `BrowseTiles.cs:292`, `:297`;
`Gap = Spacing.M`, `AlignItems = End`); crumbs `ShellMastheadBand.cs:81-130`; "Show all" is
`Button.Create(Strings.Browse.ShowAll, …, Subtle, Small)` disabled while `ToolsLoading` (`:59-62`).

**Which routes show a band — the five families, and nothing else** (`ShellMastheadRegistry.TryResolve`, `NavOrigin.cs:82-83`):
`browse` (the directory) · `browse:<uri>` · `browse-section:<uri>` · `home-section:<uri>` · the whole concert family
(`ConcertRoutes.Is`). Every other route leaves `live = false`, which HOLDS the last trail at `Opacity 0` (`ShellMastheadBand.cs:69-71`) —
and the band is `HitTestVisible = live`, so a held, invisible trail is never clickable. Before the FIRST resolve of the process
`_heldTitle` is null and the band is a zero-size `Opacity 0` box (`:56-57`).

**The title is a THREE-TIER fallback, and the family membership alone does not make the band live** (`NavOrigin.cs:85-88`):

1. the page's **live published title** — `ShellMastheadStore.For(name, arg).Title`, trimmed, blank ⇒ skipped;
2. else **the last trail crumb's label** (`trail[^1].Label`) — which for a browse category is the route `Arg`;
3. else **`ShellMastheadRegistry.StaticTitle`** (`:93-107`): browse-home → `browse.title` "Browse" · the concert family →
   the `Arg` or `concerts.title` "Concerts" · any non-blank `Arg` → the `Arg` · `browse:`/`browse-section:` → `browse.homeTitle`
   "Browse" · `home-section:` → `nav.home` "Home".

`TryResolve` returns `!IsNullOrWhiteSpace(title)`, so a family route that answers blank on all three tiers is **not live** and
the band holds the previous trail exactly as a foreign route does. In 0.3 this is the readiness contract for the band: a page
that has not loaded yet still gets a real title from tier 2/3 — **never a skeleton, never a blank band, never a collapse.**

**Two more per-crumb states the frame does not draw:** a crumb whose `RouteName` is empty gets `OnClick = null` but KEEPS
`Role = Button`, `Focusable = true` and `Cursor = Hand` (`ShellMastheadBand.cs:97-98`) — an inert-but-focusable crumb; and every
crumb's press colour is `Tok.TextTertiary`, i.e. the pressed state returns to the rest colour rather than deepening (`:92`).

**"Show all" absent is a zero-size `BoxEl`, not an omitted child** (`ShellMastheadBand.cs:72`): the row always has two children,
so gaining/losing the button never re-flows the title's `Grow = 1, Basis = 0` measure. While `ToolsLoading` the button is
rendered **disabled**, not hidden (`:59-62`).

**Crumb metrics the frame above does not draw:** every crumb is `WaveeType.SurfaceDisplay` at `MaxLines = 1` + character ellipsis,
`Shrink = 0`, wrapped in a `Role = Button`, `Focusable`, `Cursor = Hand` box; the `›` separators are the same face in `TextTertiary`; the
prefix run has `Gap = Spacing.S` (8) internally and another 8 before the current title; the current title is `Grow = 1, Basis = 0,
Shrink = 1, MaxLines = 2` under the stable key `"masthead-current"`, so Browse-home → category re-points it without a remount
(`ShellMastheadBand.cs:90-128`). When there is no parent crumb the prefix child is **omitted entirely** — a zero-width first sibling still
participates in the row and padded Browse-home a rung right of the body (`:103`, `:110-117`).

**The page-body side of the same number** (`BrowseMastheadMetrics.cs`): `Reserve = Spacing.XXXL + TitleLine 52 = 84`;
`BodyTop = Reserve + Spacing.L = 100` is the top inset a family page's body actually uses; `ClipInset = Reserve` is where a scrolling
family body must cut its content, because the band paints nothing and an uncut body would slide its own H1 through the crumb.

**Published-but-unrendered:** `ShellMastheadState.Caption` exists in the store (`App/ShellMasthead.cs:11-12`) and the band never reads it.
Do not port it as a visual — port it as nothing, or delete the field.

### W14 — tab strip states (1 char ≈ 8 DIP)

```
 pinned chips │ divider │  text tabs (hover: close overlays the label tail)        │ "+" (hover-only)
 ┌──┐┌──┐     │         │                                                          │
 │♫ ││♥ │  1  │  Home        Daily Mix 3      Bad Bunny ⓧ                          │  ＋
 └──┘└──┘     │  ━━━━━━━                                                           │
  36  36      │  ↑ 2-DIP accent underline, inset 12 each side, baseline y = 40     │  32×24 plate, Radii.Control
                 selected: weight 650 + TextPrimary   others: TextSecondary → TextPrimary on hover
                 tab plate 32 tall, padding 12/12, width hug clamped to [110, 200]
                 close ⓧ: 20×20, Radii.Control, FillSolidSecondary, glyph 10 — hover-gated, OVERLAID (no reserved column)
                 pin divider: 1×20, Margin (4,0,6,0), StrokeDividerDefault
                 overflow: the lane scrolls (edge-fade + 28×28 chevron pips that fade in on hover)
```

Wavee's configuration: `Appearance = Text`, `OverflowMode = Scroll`, `TextFontSize = 13`, `MinTabWidth = 110`, `MaxTabWidth = 200`,
`IndicatorFill = Tok.AccentDefault`, `AddButtonVisibility = OnStripPointerOver` (`WaveeShell.cs:1535-1566`).
Engine geometry: `TabStrip.cs:90-104` (`TextTabPadX 12`, `TextTabHeight 32`, `IndicatorThickness 2`, `IndicatorMinWidth 8`,
`PinnedTabWidth 36` `:95`, `TextTabBaseline = (48+32)/2 = 40` `:104`).

**Per-tab states the frame above does not draw:**

* **Closable is a rule, not a hover**: `IsClosable = !tab.Pinned && tabs.Count > 1` (`WaveeShell.cs:1627`). A **pinned** tab has no ⓧ at
  any hover, and the **last remaining** tab has none either. The ⓧ is mounted whenever closable and only fades/disarms
  (`Opacity = closeVisible ? 1 : 0`, `HitTestVisible = closeVisible`) so hover never remounts anything (`TabStrip.cs:771-808`).
* **Pinned cells have no label and no underline.** The selection indicator skips a pinned selection outright
  (`TabStrip.cs:604-609`); the chip plate carries it instead (`Tok.AccentSubtle` selected / `FillSubtleSecondary` rest), the icon goes
  `Tok.AccentTextPrimary` while selected, and the cell is `ToolTip.Wrap`ped with its own header (`TabStrip.cs:726-744`, `:830-836`, `:855`).
* **Selected-glyph no-op ramp**: a selected tab's `HoverColor` is `default` (A = 0) so the recorder leaves the colour alone rather than
  ramping toward a token it is already at (`TabStrip.cs:735-743`).
* **The "+" is hit-testable while invisible** — that is what makes its reserved 32-DIP slot part of the strip's hover area, so approaching
  from any direction reveals it (`TabStrip.cs:905-911`).
* **Scroll pips only exist when they can scroll**: each `EdgePip` returns a `0×0` hit-test-free box unless `metrics.CanScroll*`
  (`TabStrip.cs:662-663`).

### W15 — omnibar focused, suggestions open @ 1600 (1 char ≈ 8 DIP)

```
        ┌ field: AutoSuggestBox, minHeight 32, Radii.Control, standard chrome, width = SearchWidth (≤ 420)
        │  [ 🔍  bad bunny|ny                                        ]        ← ghost completion (SearchSuggestions.GhostFor)
        ├──────────────────────────────────────────────────────────────┐  popup width = max(field, 400); MaxHeight 560
        │ ▂▂▂▂▂ indeterminate progress bar (only while Pending)         │
        │ 🔍 bad bunny                        (query rows, max 6)       │  row: MinHeight ItemMinHeight, pad 12/8, margin 4/2,
        │ 🔍 bad bunny tickets                                          │       Radii.Control, match segment weight 700/TextPrimary,
        ├───────────────── 1px divider, margin 16/4 ────────────────────┤       rest weight 400/TextSecondary
        │ ▢ 44  Bad Bunny                    ▶      ⋯  [ Artist ]       │  rich row: 58 tall, gap 12, art 44 (circle r22 for artist/user,
        │        Artist                                                 │           radius 5 otherwise), title 14/600, sub 12/secondary,
        │ ▢ 44  Tití Me Preguntó             ▶  ♡  ⋯  [ Song   ]        │           trailing 28×28 circular buttons (gap 2) + type pill
        └───────────────────────────────────────────────────────────────┘           (pad 9/2, radius 10, FillSubtleSecondary, Eyebrow ink)
```

`ShellToolbar.cs:328-332` (field), `:356-436` (popup body), `:449-462` (query row), `:464-523` (rich row), `:563-570` (type pill).
Empty / failed / pending copy: `Strings.Search.NoResults`, `Strings.Search.SuggestFailed` + `Strings.Common.Retry`, and **nothing at all**
while pending (the progress bar is the answer) — `ShellToolbar.cs:398-409`. Both notices are one `Notice` row at
`MinHeight = AutoSuggestBox.ItemMinHeight (40)`, padding `(24,0,24,0)` — `(24,0,12,0)` when Retry rides along (`:439-447`).

**Per-row trailing affordances are kind-gated, not uniform** (`ShellToolbar.cs:467-484`):

| affordance | shown when | source |
|---|---|---|
| ▶ play (28×28, glyph 14, `Interaction.Subtle`, `ScaleEmphatic`) | kind **is not** User or Genre | `:470`, `:474`, `:534-541` |
| ♡ heart (`TrackRow.Heart`, live `lib.IsSaved`) | kind **== Track only** | `:475-476` |
| ⋯ more (always visible, `ClickRequestsContext`) | `ActionServices` **and** `Overlay` resolved **and** playable | `:477-478`, `:545-554` |
| type pill | always | `:479` |

Row caps: **6 query rows and 10 rich rows** (`:386`, `:395`), and the same two caps bound the keyboard cursor and `InvokeSelection`
(`:268-270`, `:306`). A rich row also carries the card context menu (`Menus.Card`, `:520-522`). Popup width floors at 400 unless the host
passes `allowNarrow` — the **icon-mode flyout does** (`MergedChromeRow.cs:321`), because its omnibar renders `Inline`, not as a second popup.

**The width has a pre-measure fallback of 720**, not 0: `measuredWidth = _width.Value > 0 ? _width.Value : 720` and then
`width = allowNarrow ? measuredWidth : max(measuredWidth, 400)` (`ShellToolbar.cs:369-370`). A popup that opens on the frame before
its anchor is arranged therefore renders at 720, not at a sliver. Keep the fallback; it is the reason the first keystroke's
popup never flashes narrow.

**Row invocation by kind** (`ShellToolbar.cs:281-299`): Track → `Player.PlayTrackAsync` (no navigation) · Artist → `artist:<uri>` ·
Album → `album:<uri>` · Playlist → `pl:<uri>` · Podcast/Audiobook → `show:<uri>` · Episode → `Player.PlayAsync(uri, 0)` ·
Genre → `SearchRoutes.OpenGenre` through **`HistoryStore.GoWithOrigin`** carrying `NavOrigin(query, "search", query)`
(`:259-263`, `:296-298`). That origin is the whole mechanism behind the `"<query>" › <Section>` crumb in parity item 30 —
`DrillTrail.LookupOrigin` recognises `RouteName == "search"` and suppresses the Browse rung (`DrillTrail.cs:98`). Port the
origin write with the row, not just the navigation.

### W16 — back/forward history flyout (1 char ≈ 8 DIP)

```
 ◀ ▶                       right-click / touch-hold on either button
 └─┬──────────────────────────────────┐   FlyoutPlacement.BottomEdgeAlignedLeft, light dismiss, focus trap
   │ 🏠 Home                          │   up to 8 entries, MOST RECENT FIRST (ShellToolbar.cs:54, :76-82)
   │ ♫  Daily Mix 3                   │   each row = ShellNav.Dest(route) → (title, glyph)
   │ 💿 nadie sabe lo que va a pasar…  │
   ├──────────────────────────────────┤   separator + "View all history" (Strings.Nav.ViewAllHistory, Icons.Clock)
   │ 🕘 View all history              │   shown only when the stack exceeds 8
   └──────────────────────────────────┘   choosing a row calls Go (it does NOT pop the stack)
```

**Empty stack ⇒ no flyout at all** — the handler returns before opening (`ShellToolbar.cs:70`), so a right-click on a disabled Back is
silent rather than showing an empty menu. `ConstrainToRootBounds = false` (`:92`): the menu may hang outside the window.
**"View all history" does NOT navigate the current tab** — it calls `_go("history", null)`, and `GoNav` routes the literal key
`"history"` through `OpenNewTab` instead of `Go` (`WaveeShell.cs:1937`). So the row opens a **new tab**, leaving the back stack
the user was inspecting exactly where it was. Same for every other route into `history` (the sidebar, the palette). Keep the
special case in `Shell.Go`, or the flyout silently loses the stack it was just showing.

**A null `NotificationCenterBridge` renders the bell DISABLED, not absent** (`ShellToolbar.cs:122`,
`isEnabled: nc is not null`) — the glyph goes `Tok.TextDisabled` and the 44-DIP slot stays, so the trailing island's width is
the same with and without the notification service. That is the same "the tree must agree with the budget" rule `PinPlaceholder`
exists for.

### W17 — profile chip and its menu (1 char ≈ 8 DIP)

```
  ◉ Christos K…            chip: height 32, gap 8, padding (4,0,10,0) named / (4,0,4,0) bare, Radii.Control,
  └──┬────────────────┐          Interaction.Subtle ramp; avatar PersonPicture 24; name capped at 90−8−6 = 76
     │ ◉ 40  Christos │    menu: width 304, BottomEdgeAlignedRight, PopupChrome.Flyout (MenuPopupThemeTransition)
     │      ★ Premium │    header pad (14,10,14,10), gap 12; name 14/600, tier line, email 12/tertiary
     │      me@…      │    rows: Account · Settings · [Play ▸] · (Notifications · Friends when folded) ·
     ├────────────────┤           Light/Dark theme · Log out
     │ Account        │    → full menu spec in 19-shell-overlays.md
```

`ProfileMenu.cs:176-192` (chip), `:196-246` (menu), `:255-294` (header). The name caption is capped at
`NameCapW = ChromeProfileNameW − 8 − 6 = 76` with `MaxLines 1` + ellipsis, which is what makes the 90-DIP budget line a **real**
reservation rather than a nominal one (`ProfileMenu.cs:32-38`, issue #88).

Auth states other than `Live` (`MergedChromeRow.cs:196-219`) — the chip reads the folded `ShellAuthState`, **never** raw `AuthStatus`:

| state | what the trailing island renders | footprint vs the 32-DIP budget |
|---|---|---|
| `Live` | `ProfileMenu` chip (avatar 24 [+ name]) | 32 / 122 — exactly budgeted |
| `Connecting` | a 32-tall box, `Padding (8,0,8,0)`, `Caption(Strings.Shell.Connecting).Secondary()` | **wider than budgeted** — the row's drag bands absorb it |
| `Offline` | `Button.Accent(Strings.Shell.Reconnect)` → `Bridge.SignIn` | **wider than budgeted** |
| `SignInRequired` | `Button.Accent(Strings.Shell.SignIn)` — unreachable on the real backend (the wizard owns the window), reachable on `--fake` | **wider than budgeted** |

That over-run is deliberate and must be preserved: the two grow bands give the space back, and charging the budget for the widest auth
state would move the search field for every user on every launch.

### W18 — full-screen video @ any width (1 char ≈ 16 DIP)

```
┌────────────────────────────────────────────────────────────┐   chrome row UNMOUNTED (Flow.Show false)
│                                                            │   player bar UNMOUNTED
│                    video surface (ch 24)                   │   the surface's own auto-hiding transport is the only one
│                                                            │   Escape / F11 leave (WaveeShell.cs:2067-2072, :2115-2131)
└────────────────────────────────────────────────────────────┘
```

### W19 — file drag over the window (1 char ≈ 16 DIP)

```
┌────────────────────────────────────────────────────────────┐  title bar stays FULLY LIT
├──────────────┬─────────────────────────────────────────────┤
│ sidebar dims │   ░░░ spotlight scrim, scoped to the CONTENT REGION rect ░░░ │
│ with the     │            ┌───────────────────────────┐    │  drop cue: pad (18,10,18,10), Radii.Control,
│ region       │            │  Drop a file to play it   │    │  Fill FillSolidBase, 1px AccentDefault border,
│              │            └───────────────────────────┘    │  Elevation.Dialog, text 14 TextPrimary
├──────────────┴─────────────────────────────────────────────┤  (WaveeShell.cs:1418-1432, loc localFile.dropHint)
│ player bar stays FULLY LIT (keeps showing what is playing)  │  scrim rect published at WaveeShell.cs:416-421
└────────────────────────────────────────────────────────────┘
```

### W20 — window deactivated (1 char ≈ 8 DIP)

```
│ ☰(TextTertiary) │ tabs island @ .5 │ centre @ .5 │ trailing @ .5 │ ⌄🔎 @ .5 │ caption glyphs → TextDisabled │
   fills are untouched everywhere; only foregrounds/opacity change
   hamburger + the bar's own nav buttons: FOREGROUND swap, not opacity — `navStyle.Foreground = active ? TextPrimary : TextTertiary` (TitleBar.cs:277-282)
   the FOUR islands that dim to Opacity 0.5: tabs (:369) · centre column (:404) · trailing (:422) · **CaptionLeading (:486)**
   caption buttons: glyph → `Style.InactiveForeground` = `Tok.TextDisabled`; FILLS stay wired (Win11 shows hover on an
   inactive caption) — CaptionButton.cs:30, :42, :73-79
```

**The CaptionLeading island is the one the frame used to omit**, and it is the one that matters most: it carries the theme
toggle **and** (in icon mode) the search magnifier, so on a blurred window those two read at 50 % while the caption buttons
beside them are at full fill. That asymmetry is stock WinUI and must be reproduced, not "fixed".

### W21 — unknown route (1 char ≈ 16 DIP, content card only)

```
╭──────────────────────────────────────────╮   column, centred both axes, Gap = Spacing.M
│                  ♫  40                   │   Icon(destination glyph, 40, Tok.TextTertiary)
│            Page not found                │   WaveeType.PageHero → Ui.Title 28/36/600
│              [ Go home ]                 │   Button.Standard → go("home", null)
╰──────────────────────────────────────────╯   ContentHost.cs:290-309; one warn line per route key (:315-321)
```

### W22 — z-order of the window's layers (not to scale)

```
   ┌ DragPreviewLayer                     WaveeShell.cs:1507   (drag chip — ch 01)
   ├ SetupCoverScrim                      :1506
   ├ videoFullscreenLayer                 :1460
   ├ VideoPlacementHost / InWindowVideoPip :1498-1499
   ├ SidebarBinder.MountPoint (0×0)        :1497
   ├ AfterUpdate / Report / Setup / SidebarOnboarding chrome (0×0)  :1480-1494
   ├ ActionServicesOverlayBinder (0×0)     :1476
   ├ WaveeCommandPalette.Overlay           :1475
   ├ fileDropLayer                         :1418
   ├ runtimeBannerLayer (top inset 56)     :1396
   ├ immersiveLyricsLayer                  :1440
   └ tinted ── ShellMaterialLayer ── column (chrome row · content region · player bar)   :1381
   (engine popups + the auto-mounted Toast lane sit ABOVE all of this, inside OverlayHost; Toast.EdgeInset = 72, :482)
```

### W23 — window state: restored · maximized · snapped (1 char ≈ 16 DIP, chrome row + the frame's edges only)

Every frame W1–W22 varies by **width**. This one varies by **placement**, and the answer is deliberately boring:
**Wavee has no window-state branch at all.** `WindowState` / `Maximized` / `IsZoomed` have **no reader** anywhere under
`src/apps/Wavee/` (the single substring hit is `RecentsPage.IsZoomedOut`, an unrelated timeline flag) — the app never
calls `InputHooks.GetWindowState`, so the sidebar, the content region, the rail, the player dock and every chrome stage
resolve from the viewport's DIP **width** alone, exactly as in W1–W7.

```
 RESTORED @ 1180×760 (the launch size)          MAXIMIZED on a 1920×1080 @100 % monitor
 ╭───────────────────────────────────╮ ← DWM    ┌──────────────────────────────────────────────────┐ ← OS SQUARES
 │☰ ◀▶│tabs│ ＋   [ 🔎 ]   ◉ 🔔👥📌⚙│⌄│─□✕│      rounds │☰ ◀▶│tabs│ ＋    [ 🔎 field 420 ]   ◉ Christos …│⌄│─❐✕│   the corners
 ├────────┬──────────────────────────┤          ├──────────┬───────────────────────────────────────┤   itself; the
 │sidebar │╭─────────────────────────╮          │ sidebar  │╭──────────────────────────────────────╮   max glyph is
 │        ││ content, corners 8,0,0,0│          │          ││ content, corners 8,0,0,0 — UNCHANGED │   ChromeRestore
 └────────┴─────────────────────────-┘          └──────────┴───────────────────────────────────────┘
   corners: DWM rounds the WINDOW.                the reclaimed caption strip is inset by
   ContentPaneCorners is 8,0,0,0 either way.      SM_CXPADDEDBORDER + SM_CYSIZEFRAME so the 48-DIP
                                                  row does not render above the screen edge

 SNAPPED HALF, 1920×1080 @100 %  → 960×1040 DIP   SNAPPED HALF, the same panel @150 % → 640×693 DIP
 │ ☰ ◀▶ │tabs│ ＋   ……drag……  ◉ │48│⌄│🔎│─❐✕│      │☰ ◀▶│Home│…│＋   ◉ │48drag│⌄🔎│─❐✕│
   W4's icon mode: 960 < 978 = 458 + 280 + 240       W5: 640 ≤ 720 ⇒ NARROW — the 56-DIP compact
   ⇒ SearchMode.Icon, and < 1200 ⇒ no bell/          rail, the hamburger opens the drawer, the
   friends/pin/settings (W3's trailing island)       sidebar splitter is Width = 0
```

**The three things that DO change, and all three are the engine's or the OS's:**

1. **The caption max button re-glyphs** `Icons.ChromeMaximize` → `Icons.ChromeRestore` (`TitleBar.cs:253`, `:497-503`).
   It is pulled from `InputHooks.GetWindowState` on a **`WindowChromeEpoch` bump** — the *same* signal W20's
   deactivation dim rides (`Context.cs:241`, `:276-278`; `AppHost.cs:2494`, `:2509`), raised from the `WM_SIZE`
   zoomed-edge detect (`Win32Platform.cs:1819-1827`). `maximized` is a bit in the bar's memo key, so the row rebuilds
   **once** on the edge and returns the cached tree on every later resize tick (`TitleBar.cs:270`).
2. **Corners, shadow and resize borders are DWM's, never the app's.** The custom frame restores only the caption strip
   and keeps `DefWindowProc`'s thin L/R/B frame, so "DWM shadow, Win11 rounded corners and the L/R/B resize borders all
   stay system-handled" (`Win32Platform.cs:2084-2092`). Windows squares the corners on maximize/snap by itself; Wavee
   sets **no** `DWMWA_WINDOW_CORNER_PREFERENCE` on the main window (the only caller is the popup path's `DONOTROUND`,
   `Win32Theme.cs:157`). **`WaveeShell.ContentPaneCorners` stays `(8,0,0,0)` in every state** (`WaveeShell.cs:150`) —
   a maximized window does **not** square the content card's top-left arc, and the FileArea coats, the left+top stroke
   and the 8-DIP rail gap are untouched.
3. **Maximized, the top row lives at negative client y.** `WM_NCCALCSIZE` insets the reclaimed top by
   `SM_CXPADDEDBORDER + SM_CYSIZEFRAME` at the window's DPI so the 48-DIP bar is not drawn above the screen edge
   (`Win32Platform.cs:2093-2099`), and the hit tests fold `pt.y < 0` onto row 0 so the Win11 Fitts slam-zone still
   reaches close/max/min and the drag band (`:2113-2115`, `:1092`).

**Snapped is a size, not a state.** A snap (half, quarter, or a Snap-Layouts zone) arrives as a plain `WM_SIZE`; the
frame answers it with the ordinary allocator. Two worked results, both derived from this chapter's own numbers:
snap-half of a 1080p panel at 100 % is **960 DIP** ⇒ below the `458 + 280 + 240 = 978` field boundary, so the search is
the magnifier (W4) *and* below 1200, so the four action buttons are folded into the profile menu (W3); the same snap on
the same panel at **150 %** is **640 DIP** ⇒ the narrow shell (W5), which is precisely why `MinWidth` is **300 DIP** and
not 360 — 360 was a ~564-px floor at 150 % that stopped the window fitting a half-screen split (`Program.cs:536-540`).

**There is no viewport-HEIGHT breakpoint in the frame.** `ShellResponsiveLayout` takes a height only for the docked-video
cap (`:128-158`); every band it resolves — narrow, toolbar-narrow, nav-pane tier, rail fit, drawer width — is a function
of width alone. A snapped **quarter** (960×520 DIP) is therefore W3's chrome over a 400-DIP content region
(520 − 48 − 72), with the column pinned to the live viewport height so the player bar cannot be pushed off
(`WaveeShell.cs:1052-1061`, and §9's rule 6). Height binds in exactly two other places, both outside this chapter: the
zoom policy's `min()` over both axes (W24) and page-level arms such as the detail hero's 900-DIP rung (ch 03).

**Placement is NOT persisted — a 0.2.9 fact to decide about, not to copy blindly.** The window is created at
`1180×760` restored on every launch (`Program.cs:214`, `:539`), and `SessionShellDto` carries only `RailOpen` +
`RailMode` (`App/SessionSnapshotStore.cs:29-36`). Maximize Wavee, quit, relaunch: it comes back **restored at
1180×760**. Parity item 56's "everything comes back" list is correct precisely because window placement is not on it.

### W24 — monitor change and the live DPI hop (no frame to draw — a sequence)

Sources: `docs/plans/wavee/large-display-scaling.md §1.1` (DPI is already correct) and **§3.2** (the policy), which
`ZoomAutoPolicy` and `WaveeShell.cs:581` both cite by name; 00 §5's W13 ladder is the table this sequence feeds.

```
 drag the window onto a monitor with a different DPI
   │
   ├─ WM_DPICHANGED                                         Win32Platform.cs:1841-1858
   │    _rawDpiScale = LOWORD(wParam)/96
   │    _scale = _rawDpiScale * _zoom                       ← the APP ZOOM SURVIVES the hop (:1848)
   │    SetWindowPos(suggested rect)                        ← the OS's rect: the window keeps its APPARENT (DIP) size
   │    RefreshClientSize(); PaintRequested()
   │
   ├─ next Paint → EnsureSize sees scale != lastScale       AppHost.cs:5246-5260
   │    publishes Viewport.Scale, full re-layout + glyph re-raster
   │    returns resized=true ⇒ CancelStructuralAll          AppHost.cs:3400-3410  ⇒ EVERYTHING SNAPS
   │
   └─ ≤ 500 ms later (trailing-edge quiet): the auto-zoom tick   WaveeShell.cs:587-632
        baseDip = viewportDip × Viewport.Zoom = clientPx / osDpiScale
        ZoomAutoPolicy.Suggest(baseW, baseH, mode) → FluentApp.SetZoom(suggested)
        SetZoom moves Scale only, px untouched → a SECOND relayout, also snapped (Win32Platform.cs:825-834)
```

**The counter-intuitive half, and the one the 0.3 port must not "fix": a restored window's monitor hop changes no
breakpoint and no zoom.** Because the OS's suggested rect preserves the apparent DIP size, `viewportDip` is unchanged,
so the narrow band, the nav-pane tier, every chrome stage, `RailFits` and the wash geometry all re-resolve to the same
answers. And `baseDip = clientPx / osDpiScale` is unchanged too, so `Suggest` returns the same rung and the no-op guard
(`|suggested − liveZoom| ≤ 0.004`, `WaveeShell.cs:621`) returns before touching the zoom. What the user sees is text
re-rasterised at the new device scale — nothing moves.

**What DOES re-resolve the ladder is a change in the DIP extent itself**: an edge resize, maximize/restore/snap, or a
**maximized** window hopping monitors (the OS sizes it to the new work area, so `clientPx` becomes the new panel's).
The two 3840×2160 rows in 00's W13 are exactly these two readings of the same policy:

| window state | client px / OS scale | `baseDip` | `min(baseW/1600, baseH/900)` | snapped down | visible result |
|---|---|---|---|---|---|
| maximized on 3840×2160 @150 % | 3840×2160 / 1.5 | 2560×1440 | 1.60 | **150 %** | DIP viewport 1707×960 |
| the same window dragged to 1920×1080 @100 % | 1920×1080 / 1.0 | 1920×1080 | 1.20 | **100 %** | DIP viewport 1920×1080 |
| restored 1600×900 window, either monitor | 2400×1350 / 1.5 · 1600×900 / 1.0 | 1600×900 | 1.00 | **100 %** | unchanged across the hop |

So the hop above steps the zoom **down** 150 % → 100 %, and every DIP-width band in this chapter re-resolves once more
on that second, debounced relayout — a **two-stage settle**: geometry on the hop's own frame, zoom ≤ 500 ms later.
Both stages snap; neither animates (`AppHost.cs:3400-3410`).

**Three more rules the port must keep:**

* **The 500 ms debounce covers the zoom half only** (`UseDebouncedValue`, trailing-edge, `WaveeShell.cs:591-597`;
  `RenderContext.Timers.cs:188-194`). The DIP re-layout is never debounced — a deferred structural width is a frame
  rendered at the wrong tier, the same rule the nav-pane tier effect states (`WaveeShell.cs:510-518`).
* **A user who ever picked a zoom is pinned to `Manual`** and a hop re-resolves nothing for them: `MigrateMode` pins any
  upgrade with a non-1.0 stored zoom (`ZoomAutoPolicy.cs:105-114`), and a live manual step is detected by comparing the
  live zoom against *this policy's own last pick* (`WaveeShell.cs:624-627`).
* **A drag between two monitors of equal DPI raises neither `WM_DPICHANGED` nor `WM_DISPLAYCHANGE`.**
  `WM_EXITSIZEMOVE` is the one reliable settle point: it re-derives the monitor and re-probes the panel's refresh
  period into the compositor clock (`Win32Platform.cs:1900-1906`, `:1711-1717`, `:1939`). Frame **cadence** follows the
  new panel; every duration in §5 is in milliseconds, so nothing re-times.

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| chrome row | H 48 | root padding `(2,0,0,0)` | — | — | **no fill** | live Mica (base layer) | `TitleBar.cs:102`, `:513-517` |
| pane toggle (hamburger) | 40×44 | margin `(6,2,−2,2)` | `Radii.Control` 4 | glyph 16 `Icons.Menu` | `Tok.TextPrimary` / inactive `TextTertiary`; hover `FillSubtleSecondary`, press `FillSubtleTertiary` | — | `WaveeShell.cs:2025`, `TitleBar.cs:277-283` |
| left header pad | W 14 | — | — | — | — | — | `TitleBar.cs:310` |
| Back / Forward | 40×44 | margin `(2,0,2,0)` | 4 | glyph 16 | as above; disabled glyph `Tok.TextDisabled`, fill transparent | — | `ShellToolbar.cs:31`, `:38`, `:96-97` |
| tab lane island | W = 44·navCount + `LeadClusterW` | — | — | — | clip, `Opacity 0.5` inactive | — | `MergedChromeRow.cs:100-105` |
| text tab | H 32, W hug ∈ [110, 200] | padding `(12,0,12,0)` | — (no plate) | 13 / weight 650 selected, 0 otherwise | `Tok.TextPrimary` selected, `TextSecondary` rest, hover → primary | — | `TabStrip.cs:744-762`, `:820-846`, `WaveeShell.cs:1542`, `:1553-1554` |
| tab icon | 16 (pinned: no right margin) | margin right 8 | — | icon font | selected `AccentTextPrimary` when pinned / `TextPrimary` otherwise; rest `TextSecondary` → hover primary | — | `TabStrip.cs:726-744` |
| pinned tab chip | 36×32 | — | 4 | icon only | selected `Tok.AccentSubtle` (hover keeps it), rest `FillSubtleSecondary` → hover `FillSubtleTertiary`, press `FillSubtleTertiary` | — | `TabStrip.cs:95`, `:830-836` |
| pin divider | 1×20, `AlignSelf=Center`, hit-test-free | margin `(4,0,6,0)` | — | — | `Tok.StrokeDividerDefault` | — | `TabStrip.cs:578-586` |
| tab close ⓧ | 20×20, `JustifySelf=End`, `TabStop=false` | — | 4 | glyph 10 `Icons.Cancel` | `FillSolidSecondary` → `FillSolidTertiary` → `FillSubtleTertiary`; glyph `TextPrimary`, pressed `TextSecondary` | — | `TabStrip.cs:773-802` |
| selection underline | H 2, W = `max(tab − 24, 8)`, resting y 38–40 | left inset 12 | — | — | `Tok.AccentDefault` (Wavee's `IndicatorFill`) | — | `TabStrip.cs:90-93`, `:104`, `:604-609`, `:1010-1025` |
| add "+" | 32×24, hit-testable at Opacity 0 | margin 0 (the last tab's 12-DIP pad is the gap) | 4 | glyph 12 `Icons.Add` | `FillSubtleTransparent` → `FillSubtleSecondary` → `FillSubtleTertiary`; glyph `TextSecondary` | — | `TabStrip.cs:905-923` |
| scroll pips | 28×28, `0×0` when the lane cannot scroll | — | 4 | chevron 10 `TextSecondary` | `FillSolidSecondary` → hover `FillSolidTertiary` → press `FillSubtleTertiary`; `Opacity 0`, `HoverOpacity 1` | — | `TabStrip.cs:662-686` |
| search field | H 32, W = `SearchWidth` ∈ {44} ∪ [280, 420] | — | 4 (`cornerRadius: 0` resolves to `Radii.Control`) | 14 | `AutoSuggestBoxChrome.Standard` | — | `ShellToolbar.cs:328-332`, `MergedChromeRow.cs:266-275` |
| search **icon** (icon mode) | 40×44 | margin `(2,0,2,0)` | 4 | glyph 16 `Icons.Search` | IconButton default ramp | — | `MergedChromeRow.cs:338-341` |
| search flyout (icon mode) | W = `clamp(vp − 2·Spacing.M, 44, 420)` | — | popup chrome | — | `PopupChrome.Popup`, focus trap, light dismiss | popup shadow | `MergedChromeRow.cs:296-326` |
| centre column, icon mode | **0×0**, `HitTestVisible = false` | — | — | — | — | — | `MergedChromeRow.cs:114` |
| suggest popup | W = max(field, 400), MaxHeight 560 | `(0,2,0,2)` | popup chrome | — | `PopupChrome.Static` acrylic plate | popup shadow | `ShellToolbar.cs:412-435` |
| suggest query row | MinHeight `AutoSuggestBox.ItemMinHeight` | pad `(12,0,8,0)`, margin `(4,2,4,2)` | 4 | 14 | selected/hover `FillSubtleSecondary`, press `FillSubtleTertiary` | — | `ShellToolbar.cs:449-462` |
| suggest rich row | H 58, art 44 | pad `(12,0,10,0)`, gap 12 | art 22 (circle) / 5 | title 14/600, sub 12 | as above | — | `ShellToolbar.cs:486-519` |
| type pill | hug | `(9,2,9,2)` | 10 | `WaveeType.Eyebrow` 12/600 +30/1000 | `Tok.FillSubtleSecondary`, ink `TextTertiary` | — | `ShellToolbar.cs:563-570` |
| profile chip | H 32, avatar 24 | gap 8, pad `(4,0,10|4,0)` | 4 | name = `Ui.Caption` 12/16 primary, MaxWidth 76 | `Interaction.Subtle` (transparent → `FillSubtleSecondary` → `FillSubtleTertiary`, 83 ms) | — | `ProfileMenu.cs:176-192`, `:32`, `:38` |
| bell / friends / pin / settings | 40×44 | margin `(2,0,2,0)` | 4 | glyph 16 | IconButton default ramp | — | `MergedChromeRow.cs:165-167`, `ShellToolbar.cs:31` |
| unread badge | `InfoBadge.Count` | ZStack over the 40×44 box, top-right | — | — | `Tok.AccentDefault` | zero footprint | `ShellToolbar.cs:126-141` |
| min drag strip | W 48 | — | — | — | — | window-drag band | `TitleBar.cs:109`, `:477-479` |
| theme toggle | 40×44 | margin `(2,0,2,0)` | 4 | glyph 16 `Icons.Sun`/`Moon` | IconButton default | — | `MergedChromeRow.cs:136-139` |
| caption buttons | 46×48 each | — | 0 | glyph 10 | min/max `FillSubtleSecondary/Tertiary`; close `Tok.CaptionCloseHover/Pressed` + white glyph | — | `CaptionButton.cs:19-21`, `:35-53` |
| sidebar pane | W = 56 compact / `_sidebarWidth` ∈ [180, 460] | — | — | — | **no fill** | Mica omission | `WaveeShell.cs:1004-1015`, `ShellResponsiveLayout.cs:14`, `:121` |
| nav-pane default tiers | 240 / 280 / 320 (Classic) | — | — | — | — | — | `ShellResponsiveLayout.cs:172-174` |
| content underlay | fills region | — | `(8,0,0,0)` | — | `WaveeColors.FileArea` (dark `#4C3A3A3A`, light `#80FFFFFF`) | **no shadow** | `WaveeShell.cs:1082-1087`, `PaletteBuilder.cs:128`, `:151` |
| content stroke | 1 px, left+top only | margin `(0,0,−1,−1)` in a clipping parent | `(8,0,0,0)` | — | `Tok.StrokeCardDefault` | paint-only, static | `WaveeShell.cs:159`, `:164-177` |
| rail gap | W 8 (`Spacing.S`) when inline | — | — | — | — | — | `WaveeShell.cs:1150` |
| rail band | W = `RailWidth` ∈ [200, 500], default 340 | — | `(8,0,0,0)` | — | `WaveeColors.FileArea` (one coat) | — | `WaveeShell.cs:1163-1199`, `ShellResponsiveLayout.cs:126` |
| rail floating backing | — | — | — | — | `WaveeColors.FloatingChrome` (= `ShellGround`: light `#EDEDED`, dark `#202020`) | opaque | `WaveeShell.cs:1289-1290`, `WaveeTokens.cs:133`, `:163-170` |
| splitter strips | W 16 | — | — | — | no indicator (`ShowIndicator = false`) | cursor `SizeWE` | `Splitter.cs:22`, `WaveeShell.cs:1231`, `:1253` |
| player dock reserve | H 72 | — | — | — | **no fill** | Mica omission | `WaveeTokens.cs:56`, `:81-83` |
| masthead band | reserve 84 | pad `(36,32,36,0)`, gap 12 | — | crumbs + title `WaveeType.SurfaceDisplay` = `Ui.TitleLarge` 40/52, Display face, weight 400, −12/1000 | crumb `TextTertiary`→hover `TextSecondary`; title `TextPrimary` | **no fill** | `ShellMastheadBand.cs:64-73`, `WaveeType.cs:146-151`, `BrowseTiles.cs:292`, `:297` |
| narrow drawer scrim | full bleed | — | — | — | `#33000000` | — | `WaveeShell.cs:2257` |
| narrow drawer pane | W = `DrawerWidth` | — | `(0,8,8,0)` | — | `Tok.AcrylicFlyout` + 1 px `StrokeCardDefault` | `Elevation.Flyout` = blur 16, dy 8, `#42000000` dark / `#24000000` light | `WaveeShell.cs:2331-2342`, `Elevation.cs:33-35` |
| file-drop cue | hug | `(18,10,18,10)` | 4 | 14 | `Tok.FillSolidBase` + 1 px `Tok.AccentDefault` | `Elevation.Dialog` = blur 64/48, dy 16/12 | `WaveeShell.cs:1425-1430`, `Elevation.cs:41-43` |
| runtime banner lane | MaxWidth 560 | top inset 56 (48 + 8) | — | — | — | pass-through positioner | `WaveeShell.cs:1396-1405` |

Chrome budget constants (all `ShellResponsiveLayout.cs`): `ChromePromotionHysteresisW 40` `:29` · `ChromeNameEnterW 1360` `:32` ·
`ChromeActionsEnterW 1200` `:42` · `ChromeForwardEnterW 520` `:46` · `ChromeBarLeadW 60` `:57` · `ChromeNavButtonW 44` `:60` ·
`ChromeAddSlotW 32` `:63` · `ChromeTabOverflowW 36` `:66` *(declared but never summed — dead in `FixedBudget`)* ·
`ChromeProfileChipW 32` `:69` · `ChromeProfileNameW 90` `:72` · `ChromeMinDragStripW 48` `:79` · `ChromeCaptionClusterW 138` `:81` ·
`ChromeThemeToggleW 44` `:84` · `ChromeGutterMinW 8` `:88` · `ChromeWidthQuantumW 10` `:93` · `ChromeSearchMaxW 420` `:97` ·
`ChromeSearchMinW 280` `:98` · `ChromeSearchWidthRatio 0.28` `:99` · `ChromeSearchIconW 44` `:104` · `ChromeTabMaxW 200` `:107` ·
`ChromeTabMinW 110` `:108` · `ChromePinnedTabW 40` `:109` · `ChromeTabViewportMinW 32` `:110` · `ChromeTabComfortRatio 0.78` `:111` ·
`ChromeTabComfortMinW 240` `:112` · `ChromeTabComfortMaxW 720` `:113`.

---

## 4. Colour & material

**The stack, bottom-up** (`WaveeShell.cs:1360-1380`, `WaveeTokens.cs:86-106`):

1. **Base layer — live Mica.** `Program.cs:540-541` asks for `CustomFrame = true, MicaAlt = false` (DWM `DWMSBT_MAINWINDOW`). The shell
   root paints `ColorF.Transparent` (`WaveeShell.cs:1384`), so the chrome row, the sidebar band and the player dock are *omissions* and the
   user's wallpaper shows through all three.
2. **Material layer — the page's colour.** `ShellMaterialLayer` (`ShellMaterialLayer.cs:21`), a component so that a `GradientSpec` (not a
   `Prop`) can change by re-render and so that the tint's implicit `BrushTransition` arms on a **static** fill (a bound fill is excluded
   per-channel by the reconciler) — `ShellMaterialLayer.cs:14-19`.
3. **Content layer — `WaveeColors.FileArea`**, the stock WinUI `LayerFillColorDefault` rung: dark `#4C3A3A3A` (≈ `#303030` over the plate),
   light `#80FFFFFF` (≈ `#FCFCFC`) — `PaletteBuilder.cs:151`, `:128`. Painted exactly twice: the content region underlay
   (`WaveeShell.cs:1085`) and the docked rail band (`WaveeShell.cs:1197`). The content card itself is `ColorF.Transparent` — a fill there
   double-composited the smoke and made the pane one rung too light (`WaveeShell.cs:1099-1106`). **Regression tell: a dark pane sampling
   `#333333` means that fill came back.**

**Tint derivation** (`CoverPaletteLeaves.cs:230-246`):

```
cover url → CoverColorPlane.Current.Watch(url)          (grading cache; also FallbackUrl)
          → Surfaces.SchemeFor(url) : ColorScheme?      (null until graded)
          → dark : WaveePalette.TintedDark(scheme) with { A = 0.14 }
            light: WaveePalette.Lift(WaveePalette.ToColor(scheme.TextBase)) with { A = 0.05 }
          → ShellMaterial.Publish(slot, owner, isClaim, definite, tint, wash: null)
          → ShellTintOwnership.Resolve  (CoverColorPlane.cs:543-556)
          → Signal<ShellMaterialState> → ShellMaterialLayer.Tint  (static Fill + BrushTransitionMs 250)
```

`ShellTintOwnership.Resolve` is the whole hand-over rule (`CoverColorPlane.cs:543-556`):
not-owner + not-claim ⇒ **NoWrite**; `Definite` ⇒ write known colour or the neutral ground; colour known ⇒ write it; otherwise a claim
⇒ **WriteHeldColor** (take ownership, keep painting what is already there). *Never a clear.*

**Neutral is a real colour, not transparent**: `WaveeColors.ShellGround with { A = 0.03 }` (`ShellMaterialLayer.cs:89`). Cross-fading
through `ColorF.Transparent` (premultiplied black) dragged every navigation's tint toward black for the length of the ramp.

**Home's three washes** (`ShellWashGeometry.cs:24-37`, painted at `ShellMaterialLayer.cs:101-132`):

| leg | centre (window fraction) | radius (window fraction) | fade stop | clipped box | anchor |
|---|---|---|---|---|---|
| Hero | (0.06, 0.00) | (0.74, 0.92) | 0.62 | x ∈ [0, 0.5188] ⇒ W **0.5188**; y ∈ [0, 0.5704] ⇒ H **0.5704** | left · top |
| Weekly | (0.92, 0.10) | (0.58, 0.78) | 0.64 | x ∈ [0.5488, 1] ⇒ W **0.4512**; y ∈ [0, 0.5992] ⇒ H **0.5992** | right · top |
| Mix | (0.58, 1.00) | (0.90, 0.70) | 0.66 | x ∈ [0, 1] ⇒ W **1.0** (spans); y ∈ [0.538, 1] ⇒ H **0.462** | left · bottom |

Worked from `Resolve` (`ShellWashGeometry.cs:41-53`): `x0 = clamp(cx − rx·f)`, `x1 = clamp(cx + rx·f)`, `W = x1 − x0` (same for y);
`AnchorRight = x0 > 0`, `AnchorBottom = y0 > 0`; the node-relative centre is `(cx − x0)/W` and the radius `rx/W`, so the ellipse rides the
box on a resize with no transform. Weekly's box is cut on the right (x1 clamps to 1) and Mix's on the bottom (y1 clamps to 1) — which is
exactly why only Mix is affected by the 72-DIP dock inset.

Each layer is a two-stop radial gradient `colour@alpha → colour@0` where **the transparent stop carries the wash's own RGB**
(`ShellMaterialLayer.cs:120-124`), sized to the bounding box of its own ellipse at the fade stop — a paint-rate saving of ≈4×, not a
look change (`ShellWashGeometry.cs:16-19`). Alphas: hero `0.055` light / `0.10` dark, shelves `0.05` / `0.085`
(`ShellWashGeometry.cs:33-37`) — dark carries ≈2× because the same colour reads weaker over the dark ground.

**Light vs dark summary**: only three things actually branch — the `FileArea` rung, the wash alphas, and the tint recipe (Lift+TextBase vs
TintedDark). Everything else is a `Tok.*` token that the engine re-resolves on `Tok.Epoch`; the shell never freezes a colour into a ctor
arg, which is why a theme switch re-themes **in place** with no remount (`WaveeShell.cs:18-23`, `ToggleTheme` → `ThemeControl.Request(250)`
at `:2150-2156`).

---

## 5. Motion

Every entry below reads frame time through the engine's transition/animation scheduler (`Animate`, `Enter/Exit`, `UseTransition`,
`anim.SeedValue`). **No shell-frame motion samples `Environment.TickCount64`.** The one time-based thing in the frame is the
auto-zoom debounce (`UseDebouncedValue`, 500 ms) and the session-save debounce, neither of which animates.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| forward navigation | incoming page root | Position + Opacity | `Dx 8 → 0`, `0 → 1` | 250 ms | `SmoothOut` = cubic-bezier(0.22, 1, 0.36, 1) | 90 ms | engine policy (fade kept, translate dropped) | `PageNavMotion.cs:52-59` |
| forward navigation | outgoing page root | Opacity | `1 → 0` in place | 120 ms | `EaseOut` = `1−(1−t)²` | 0 | as above | `PageNavMotion.cs:49`, `:57` |
| back navigation | incoming page root | Position + Opacity | `Dx −8 → 0` | 250 ms | `SmoothOut` | 90 ms | — | `PageNavMotion.cs:61-68` |
| tab switch / fresh-tab push | incoming page root | Opacity only | `0 → 1` | 250 ms | `SmoothOut` | 0 | — | `MotionRecipes.cs:205-209` via `PageNavMotion.cs:43` |
| same | **outgoing** page root | Opacity only | `1 → 0` | 250 ms (same dynamics, **no** exit delay) | `SmoothOut` | 0 | — | `MotionRecipes.cs:209` |
| nav to/from a module watch page | page roots | Position only (**no opacity — it would erase the video hole**) | `±8 → 0` enter, `0 → ∓8` exit | 250 ms | `SmoothOut` | 0 | — | `PageNavMotion.cs:105-121`, classification `ContentHost.cs:147-153` |
| neutral nav to/from a watch page | — | **none (hard cut)** | — | — | — | — | — | `PageNavMotion.cs:99` |
| masthead family enter | band | Opacity | `0 → 1` | 120 ms | `SmoothOut` | 0 | `ReducedMotionPolicy.KeepFade` | `ShellMastheadBand.cs:25-26`, `:71` |
| masthead family leave | band | Opacity | `1 → 0` | 120 ms | `FluentAccelerate` = cubic-bezier(0.9, 0.1, 1, 0.2) | 0 | KeepFade | `ShellMastheadBand.cs:23-24` |
| sidebar collapse / expand | sidebar pane | Size + Position, `SizeMode.Reveal`, descendants suppressed | 56 ↔ width | 300 ms | cubic-bezier(0, 0.35, 0.15, 1) | 0 | engine snap | `WaveeShell.cs:144-147`, `:184-189` |
| same edge | content card | Position + Size, Reveal, `RelativeTo "shell.content-row"` | old → new rect | 300 ms | same curve | 0 | snap | `WaveeShell.cs:194-199`, `:1134-1135` |
| sidebar grip drag | every layout transition | — | snapped 1:1 to the pointer | — | — | — | n/a | `WaveeShell.cs:134-135`, `Splitter.cs:238` |
| sidebar resist zone | pane content | Opacity | `1 → 0.35` (drag-position driven, not timed) | — | linear in `into/64` | — | unchanged | `SplitterMath.cs:36-43`, `WaveeShell.cs:1233-1236` |
| **rail** resist zone | rail panel host | Opacity (`_rightRailFade`, bound) | `1 → 0.35` | — | linear in `into/44` (engine defaults: `FadeStart = Min 200`, `FadeDistance 44`, `MinFade 0.35`) | — | unchanged | `WaveeShell.cs:1248-1255`, `:1306`, `Splitter.cs:66-70`, `:144` |
| rail seam drag past raw 156 | rail | open → closed (`InvertCollapsed`) | — | snap | — | — | — | `WaveeShell.cs:1254`, `Splitter.cs:145` (`ForcePush = FadeDistance`) |
| tab lane overflows, pip clicked | tab scroll viewport | OffsetX | ± `max(MinTabWidth 110, viewportW − 40)` | engine scroll ease | — | 0 | **`animate: !Motion.ReducedMotion` ⇒ instant jump** | `TabStrip.cs:715-720` |
| rail open / close (shipping path) | rail spacer | Width | `0 ↔ RailWidth` | **snap, 0 ms** | — | — | — | `WaveeShell.cs:209-212`, `:1169-1170` |
| same | rail panel | TranslateX + presented width (owned by `RightRail`) | off → 0 | 300 ms class | — | — | — | `WaveeShell.cs:201-208` (see ch 21) |
| same (`WAVEE_RAIL_BASELINE=1` A/B only) | spacer + overlay | real Width | `0 ↔ RailWidth` | spring response 0.22, damping 1.0 | critically damped | — | — | `WaveeShell.cs:140`, `:205-212` |
| tab selection | underline | TranslateX + ScaleX (FLIP; layout carries the resting x/width; `TransformOriginX = 0`) | previous → new tab | spring response 0.25, damping 0.80 (`MotionSprings.SelectorPill`) | — | 0 | `SnapEnd` | `TabStrip.cs:996-1008`, `FluentGpu.Engine/Animation/Motion.cs:17` |
| tab add / remove / pin | tabs lane box | Bounds | old → new reserved width | 167 ms (`Motion.ControlFast`) | `FluentPopOpen` = cubic-bezier(0, 0, 0, 1) | 0 | engine | `MergedChromeRow.cs:79-80` |
| pointer enters / leaves the strip | "+" plate | Opacity | `0 ↔ 1` | 150 ms (`MotionTok.ControlFast`) | `FluentStandard` = cubic-bezier(0.8, 0, 0.2, 1) | 0 | `KeepFade` | `TabStrip.cs:216-229` |
| hover any icon button | fill + glyph | Fill ramp; glyph scale | rest → `FillSubtleSecondary`; 1 → 1.08 | 83 ms | engine spline (0,0,0,1) | 0 | scale dropped | `IconButton.cs:47-49`, `:53-58` |
| press any icon button | glyph | scale | 1 → 0.88 | 83 ms | as above | 0 | dropped | `IconButton.cs:49` |
| caption button hover/press | fill | ramp | — | 83 ms | spline (0,0,0,1) | 0 | — | `CaptionButton.cs:66` |
| page publishes a new tint | material tint rect | Fill (BrushTransition) | old → new colour | 250 ms (`WaveeMotion.Standard`) | engine brush fade | 0 | — | `ShellMaterialLayer.cs:98`, `WaveeMotion.cs:59` |
| Home re-grades a wash | wash layer | Opacity via keyed remount (Enter/Exit) | `0 → 1` new, `1 → 0` old | engine default enter/exit | — | 0 | **`WashFade` is null ⇒ hard swap** | `ShellMaterialLayer.cs:34`, `:110`, `:129-130` |
| theme toggle | whole tree | animated re-theme in place (`ThemeControl.Request`) | — | 250 ms | engine | 0 | — | `WaveeShell.cs:2155` |
| narrow drawer open/close | scrim | Opacity | `0 ↔ 1` | 167 ms (`WaveeMotion.Fast`) | `Linear` | 0 | **0 ms** | `WaveeShell.cs:2250-2253` |
| same | drawer pane | TranslateX | `−width ↔ 0` | 300 ms | `SmoothOut` | 0 | **0 ms** | `WaveeShell.cs:2312-2315` |
| setup wizard covers the shell | cover scrim | Opacity | `0 ↔ 1` | 250 ms (`WaveeMotion.Standard`) | `Linear` | 0 | 0 ms | `WaveeShell.cs:2278-2281` |
| file drag enters / leaves the window | drop cue | Opacity (**bound prop ⇒ snaps**) | `0 ↔ 1` | 0 | — | — | — | `WaveeShell.cs:1422` |
| keep-alive activation | page subtree | layout transitions suppressed for the activation frame | — | — | — | — | — | `ContentHost.cs:108` |
| window resize (interactive) | all structural tracks | cancelled / snapped | — | — | — | — | — | `Motion.SetLayoutTransitionsSuppressed`, `TabStrip.cs:952-963` |
| **maximize / restore / snap** (a plain `WM_SIZE`, **no** modal loop) | every structural track: sidebar pane, content-card FLIP, tab-lane bounds | Bounds / Position | cancelled, snapped onto the geometry the re-layout is about to solve | **0 ms** | — | 0 | n/a | the `WindowResize` suppression is gated on `InModalLoop` (`AppHost.cs:3234`) and therefore **false** here; the snap comes from `CancelStructuralAll` gated on `resized` (`AppHost.cs:3240`, `:3400-3410`) |
| **per-monitor DPI hop** (`WM_DPICHANGED`) | whole window | effective Scale → DIP viewport | old → new scale | **snap** (full re-layout + glyph re-raster on the hop's own frame) | — | 0 — the geometry half is **never** debounced | — | `Win32Platform.cs:1841-1858`, `AppHost.cs:5246-5260` |
| **auto-zoom re-resolve** after a DIP-extent change (resize / maximize / snap / a maximized monitor hop) | whole window | zoom → DIP viewport | ladder rung → rung (e.g. 150 % → 100 %, W24) | **snap** (`SetZoom` moves Scale only; `EnsureSize` sees it and cancels structural tracks) | — | **500 ms** trailing-edge quiet (`UseDebouncedValue`) | — | `WaveeShell.cs:587-597`, `:621-632`, `Win32Platform.cs:825-834`, `RenderContext.Timers.cs:188-194` |
| monitor change at equal DPI (a pure titlebar drag) | frame **cadence** only | present interval | old panel → new panel's refresh period | — | — | at `WM_EXITSIZEMOVE` | — | every duration in this table is in ms, so nothing re-times (`Win32Platform.cs:1900-1906`, `:1711-1717`) |
| maximize / restore edge | caption max button | glyph | `Icons.ChromeMaximize` ↔ `Icons.ChromeRestore` | **snap** (one memo-busting re-render, no ramp) | — | 0 | — | `TitleBar.cs:253`, `:270`, `:497-503`, `Win32Platform.cs:1819-1827` |
| masthead band (either edge) | band | Opacity | `0 ↔ 1` | 120 ms | see the two rows above | 0 | KeepFade | the band carries a `Transition` **MotionTokenDef**, not `Enter`/`Exit` — it never mounts or unmounts (`ShellMastheadBand.cs:71`) |
| tab close / middle-click / pin move | remaining tabs | Bounds (the lane reservation, not the tabs) | old → new | 167 ms | `FluentPopOpen` | 0 | engine | one `TabLaneMotion` on `"chrome-tabs-lane"` — individual tab plates carry **no** layout transition (`MergedChromeRow.cs:79-80`, `:102`) |
| a chrome stage flips (name / actions / forward / search mode) | the whole island tree | — | **no transition at all** | — | — | — | — | the bar re-renders off `ContentVersion`; only the tab lane's Bounds eases (`WaveeShell.cs:965-966`) |
| search mode flips while focused | omnibar caret | focus ticket, not motion | — | — | — | — | — | `_searchFocusRequest++` on Field→Icon **and** Icon→Field while the flyout was open (`WaveeShell.cs:820-826`) |
| omnibar suggest fetch | rows | — | 150 ms of quiet after the last keystroke (`AutoSuggestBox.TextChangedDebounceMs`), `Begin` fires on the keystroke edge | — | — | — | — | `ShellToolbar.cs:205-222` |
| auto-zoom | whole window | zoom | ladder step | — | — | `UseDebouncedValue` **500 ms** resize-settle | — | `WaveeShell.cs:591-597` — the full path (what re-resolves the rung, and what does not) is the **auto-zoom re-resolve** row above and W24 |
| every `Go`/`Back`/`Forward` | — | session snapshot write | — | — | — | **2 000 ms** debounce (`SessionSnapshotStore.SaveDebounceMs`) | — | `WaveeShell.cs:1764-1765` |

**There is no connected-animation / hero fly in 0.2.9.** `DetailShell` deliberately publishes `MorphKey = null`; the forward capture seam
was removed repo-wide and `WaveeShell.ProbeCardNav` ignores its own `doMorph` argument. Re-adding one means restoring the whole capture
seam **and** deciding how it composes with the page slide — read `Features/Detail/DetailShell.cs:237-258` before touching this.

### 5.1 The pair matrix — a navigation is THREE channels, and only the pair decides what you see

The table above specifies each channel **per direction**. What the user actually sees is the composition of three channels
that the same route flush starts and that answer to **different** predicates:

1. **the page recipe** — direction only (`PageNavMotion.RecipeFor`, `PageNavMotion.cs:40-45`), *except* that a swap with a
   module watch page on **either** side takes the translate-only pair instead, because the outgoing root is still drawing
   (`ContentHost.PageTransition`, `ContentHost.cs:147-153`);
2. **the masthead band** — a 120 ms opacity `Transition` driven by whether the ORIGIN and the DESTINATION are in the
   masthead family (`ShellMastheadBand.cs:42`, `:46`, `:69-71`; the family is `ShellMastheadRegistry.TryResolve`,
   `NavOrigin.cs:81-82`);
3. **the shell material** — a hand-over between the two routes' material FORMS (`ContentHost.PublishesShellMaterial`,
   `ContentHost.cs:184-186`, and the pages' own publishes; resolved by `ShellTintOwnership.Resolve`,
   `CoverColorPlane.cs:543-555`; painted by `ShellMaterialLayer.cs:36-51`).

No two of those predicates agree, so the composition is a property of the PAIR and of nothing else. The seven families:

| tag | routes | masthead family? | material it publishes | source |
|---|---|---|---|---|
| **H** | `home` | no | **W3** — three radial washes (hero / weekly / mix) over the neutral tint rect | `HomePage.cs:229-237` |
| **R** | `recents`, `NavRouteNormalizer.LegacyRecentsRoute` | no | **W1** — ONE hero leg, `Weekly`/`Mix` null | `RecentsPage.cs:318-325` |
| **S** | `home-section:<uri>`, `browse-section:<uri>` | **yes** | **W1** — one hero leg from the section's first gradeable card | `HomeSectionPage.cs:177-182` |
| **B** | browse home, `browse:<uri>`, the concert family | **yes** | **N** — neutral, claimed by `ContentHost` | `NavOrigin.cs:81-82`, `ContentHost.cs:51-55` |
| **T** | `artist:`, `album:`, `pl:`, `prerelease:`, `show:`, `liked`, `local` | no | **T** — flat tint (dark `A=0.14` / light `A=0.05`) | `CoverPaletteLeaves.cs:240-252` |
| **N** | `search`, `settings`, `albums`/`artists`/`podcasts`, `history`, `whatsnew`, `disco:`, the two customizers, the diagnostics pages, not-found | no | **N** — neutral | `ContentHost.cs:184-186` |
| **V** | `module:<…>` (a watch page) | no | **N** — neutral | `ContentHost.cs:150-152`, `:184-186` |

**The matrix.** Rows = origin family, columns = destination family. Each cell is `band · material`.
Band: **▲** fades in (120 ms `SmoothOut`) · **▼** fades out (120 ms `FluentAccelerate`, showing the OLD trail) ·
**=** stays lit, the title/trail merely swap · **–** absent on both sides, nothing animates.
Material: `→` a real hand-over, `=` no write lands at all. **⟂** = the page recipe is the translate-only video-safe pair
(and `null` — an honest hard cut — when the direction is Neutral, `PageNavMotion.cs:96-101`); every other cell takes the
ordinary direction recipe.
A starred COLUMN (`H*` `R*` `S*` `T*`) is a destination whose colour is content-derived: if it is not graded at claim time
the hand-over resolves to `WriteHeldColor` and the ORIGIN's material stays on screen, owner transferred, until it is
(`CoverColorPlane.cs:552-554`). That is the anti-flash rule, and it means the material channel can finish long after the
page channel.

| origin ↓ / dest → | **H\*** | **R\*** | **S\*** | **B** | **T\*** | **N** | **V** |
|---|---|---|---|---|---|---|---|
| **H** | – · W3→W3 | – · W3→W1 | ▲ · W3→W1 | ▲ · W3→N | – · W3→T | – · W3→N | – · W3→N ⟂ |
| **R** | – · W1→W3 | – · W1→W1 | ▲ · W1→W1 | ▲ · W1→N | – · W1→T | – · W1→N | – · W1→N ⟂ |
| **S** | ▼ · W1→W3 | ▼ · W1→W1 | **=** · W1→W1 | **=** · W1→N | ▼ · W1→T | ▼ · W1→N | ▼ · W1→N ⟂ |
| **B** | ▼ · N→W3 | ▼ · N→W1 | **=** · **N→W1** | **=** · N**=**N | ▼ · N→T | ▼ · N**=**N | ▼ · N**=**N ⟂ |
| **T** | – · T→W3 | – · T→W1 | ▲ · T→W1 | ▲ · T→N | – · T→T | – · T→N | – · T→N ⟂ |
| **N** | – · N→W3 | – · N→W1 | ▲ · N→W1 | ▲ · N**=**N | – · N→T | – · N**=**N | – · N**=**N ⟂ |
| **V** | – · N→W3 ⟂ | – · N→W1 ⟂ | ▲ · N→W1 ⟂ | ▲ · N**=**N ⟂ | – · N→T ⟂ | – · N**=**N ⟂ | – · N**=**N ⟂ |

**`browse-section:` claims the material and `browse:` does not** (`ContentHost.cs:186` — `BrowseSectionRoutes.Is` is in the
predicate, `BrowseRoutes.Is` is not). That asymmetry is invisible except across a pair, and it is the **B→S** cell: the band
does not move at all (both are masthead family, the crumb row just grows a rung) while the window takes on a colour it did
not have a frame earlier. The reverse pair **S→B** is the same band and loses the colour. Do not "regularise" this in 0.3 —
a browse CATEGORY is a directory and a browse SECTION is content, and the material is the thing that says so.

**`N=N` is a real no-op, not a repaint.** `B`, `N` and `V` are all claimed neutral by the SAME token
(`ContentHost._neutralMaterialOwner`, `:31`, `:54`), so the write is `ShellMaterialState(sameOwner, null, null)` — equal by
value to what is already in the cell, and `Signal<T>` coalesces an equal write (`FluentGpu.Engine/Foundation/Signals/Signal.cs:60-65`). Settings → Search → a
library page moves the material channel ZERO times. 0.3 must keep the single shared neutral owner: one owner token per
neutral route would turn every one of those into a re-render of the material layer.

**The six pairs worth pinning** (each is a parity item below, 84-89):

| pair | page | band | material — what actually happens |
|---|---|---|---|
| `home` → `album:` | fade-through fwd | – | **Home's three washes stay lit** while the album claims ungraded (`WriteHeldColor`); when the grading lands the state flips to `(tint, wash: null)` in one write, so the wash host is **dropped in a frame** while the tint ramps 250 ms from the neutral ground. The drop is a hard cut: `WashHost` carries no `Exit` of its own (`ShellMaterialLayer.cs:72-77`) — only the individual legs do (`:129-130`), and those run when a leg is RE-KEYED under a mounted host, i.e. a re-grade. **0.3 decision point:** either give the host an exit or accept the cut, but write it down. |
| `browse:<uri>` → `pl:<uri>` | fade-through fwd | ▼ 120 ms | neutral → the playlist's tint (held until graded). The full timeline is **W10b**. |
| `album:` → `artist:` | fade-through fwd | – | tint → tint on the SAME node: `Key = "shell.material.tint"` is stable precisely so the `BrushTransition` has a previous colour to fade FROM (`ShellMaterialLayer.cs:93-99`). One 250 ms cross-fade, no neutral in between. (The page-side cover cross-fade is ch 03's, not the shell's.) |
| `settings` → `home` | fade-through fwd | – | **no tint ramp at all** — the tint rect is already `NeutralGround` on both sides (`ShellMaterialLayer.cs:49`, `:97`), so the only material motion is the three wash legs entering on their keyed mount (`:110`, `:129-130`). Contrast the `album:` → `home` row above, where the tint rect is the channel that moves. |
| `disco:<…>` → `artist:` | fade-through fwd | – | **neutral → tint.** `disco:` is NOT in `PublishesShellMaterial` and `artist:` is (`ContentHost.cs:186`), so the two halves of one artist's surface do not share a material: the discography is claimed neutral by the content host and drilling back into the artist re-tints the window. |
| `module:<…>` ↔ anything | **⟂** translate-only, both ways | per the other side | the material and band channels are unaffected — only the page recipe changes, and it changes because of the OTHER side too (`ContentHost.cs:150-151` classifies both tokens). On a Neutral direction (a tab switch onto or off a watch page) the recipe is `null`: the page cuts while the band still fades and the material still eases. |

**A tab switch is a pair too.** `Restore` writes `NavTransitionKind.Neutral` and then the route (`WaveeShell.cs:1956-1957`),
so the page takes `MotionRecipes.PageFade` — but the band and the material read the ROUTE, not the direction, and run exactly
as they would for a Go. Switching from a Browse tab to an album tab fades the band out and hands the material over; the page
merely cross-fades instead of sliding. The same holds for a fresh-tab push, which is a `Go` with Neutral (`:1975-1976`).

**Inside the family the band does not blink.** At the **=** cells the band re-renders with a new trail and a new title while
staying at opacity 1. The current title keeps `Key = "masthead-current"`, so it re-records instead of remounting and the text
SNAPS (`ShellMastheadBand.cs:104-109`) — but the prefix box is keyed `"masthead-prefix:" + trail[0].Label` (`:120`), so a pair
whose trail ROOT changes (a Browse-rooted crumb → a search-lookup-rooted crumb, W15's Genre row) remounts the prefix while the
current title snaps. Two different behaviours in one row, and only a pair shows either of them.

---

## 6. Interaction

### Keyboard

| chord | verb | why it is an accelerator (or not) | source |
|---|---|---|---|
| `Ctrl+T` | new Home tab | accelerator host box | `WaveeShell.cs:870`, `:2033` |
| `Ctrl+K` | toggle the command palette | `:875`, `:2034` |
| `Ctrl+F` | focus the omnibar (bumps `_searchFocusRequest`) | `:880`, `:2035`, `:2074-2079` |
| `Alt+←` / `Alt+→` | Back / Forward | `:885`, `:890`, `:2036-2037` |
| `F11` | toggle video fullscreen **only while a video is active** | F-keys are accelerator-eligible with no modifier | `:895`, `:2041`, `:2067-2072` |
| `Ctrl+=` / `Ctrl+Shift+=` / `Ctrl+Num+` | zoom in | a `KeyAccelerator` matches EXACT modifiers → 8 chords, 3 verbs | `:901-911`, `:2047-2049` |
| `Ctrl+-` / `Ctrl+Shift+-` / `Ctrl+Num−` | zoom out | `:916-926`, `:2050-2052` |
| `Ctrl+0` / `Ctrl+Num0` | zoom 100 % | `:931-936`, `:2053-2054` |
| `Space` | play/pause — **not** an accelerator (the dispatcher only matches Ctrl/Alt or F-keys); bubbles to `OnShellKey` after editors refuse; suppressed when focus is a text editor | `:2081-2088`, `:2133-2148` |
| `Escape` | closes immersive lyrics, then video fullscreen; yields to the palette, to overlays (`OverlayHost.PreviewKey`) and to any deeper focused owner. Sets `e.Handled` so `OnKey`'s unhandled-Escape arm does not also clear focus | `:2091-2131` |
| `Escape` / **`GamepadB`** (narrow drawer only) | closes the drawer, through a **chained** `InputHooks.KeyPreview` that saves and restores the previous slot — and only restores it if the slot is still ours | `:2196-2219` |
| mouse XButton1/2, keyboard Back/Forward keys | `WM_APPCOMMAND` → `FluentApp.AppNavigationCommand` → `Back()`/`Forward()` | the engine does not deliver X buttons as clicks | `:662-667`, `:2032` |
| `Ctrl`+wheel | `ZoomStep(±1)` via `InputHooks.ZoomWheel` (single slot, restored on unmount only if still ours) | `:674-680` |

### Pointer

* **Hamburger** — collapses/expands the pane; in narrow mode toggles the drawer instead and never writes the desktop preference
  (`WaveeShell.cs:460-471`).
* **Back / Forward** — click = navigate; **right-click or touch-hold** (`OnContextRequested`) opens the history flyout (W16).
  A second open-request closes it. No tooltip on either button (deliberate: `ShellToolbar.cs:96-97` wraps nothing).
* **Tab** — click selects (`ActivateTab` → `TabWorkspace.Activate`, never the strip's own cell);
  **middle-click closes** — `OnPointerPressed` with `e.Button == 2`, gated on `item.IsClosable` (`TabStrip.cs:841-844`); hover reveals the ⓧ; hovering the strip anywhere reveals the "+".
  Drag threshold is widened (`Drag.ClickPrimaryThresholdMultiplier`) because a tab is click-primary (`WaveeShell.cs:1630-1638`).
* **Tab context menu**, in order (`WaveeShell.cs:1700-1728`), with a header showing `ShellNav.Dest(tab.Route).Title`:
  1. `Pin tab` / `Unpin tab` — `shell.pinTab` / `shell.unpinTab`, icon `ActionIcons.Pin`/`Unpin`, **always enabled**
  2. — separator —
  3. `Close tab` (`shell.closeTab`, **the only close row with a glyph**, `Icons.Cancel`) — enabled when `tabs.Count > 1`
  4. `Close other tabs` (`shell.closeOtherTabs`, **no glyph** — `default`) — enabled when another unpinned tab exists
  5. `Close tabs to the right` (`shell.closeTabsRight`, no glyph) — enabled when an unpinned tab is to the right
  6. `Close all unpinned tabs` (`shell.closeAllUnpinned`, no glyph) — enabled when **any** unpinned tab exists (`HasAnyUnpinned`)
  7. — separator + the destination's Pin-to-sidebar row — only when `PinActions.RowForDestination` yields one

  **A close verb that empties the workspace reseeds one Home tab** (`TabWorkspace.CloseWhere`, `TabWorkspace.cs:131-136`):
  "Close all unpinned tabs" with zero pins leaves a fresh Home tab and a `Restore` intent, never an empty strip.
  Closing the ACTIVE tab restores its right neighbour (else the left one); closing a background tab is a `None` intent and
  changes nothing on screen (`TabWorkspace.cs:98-114`).
* **Drag & drop**
  * *Sources*: every tab whose route maps to a `SidebarDestination` is a `WaveeDragKinds.Resource` source
    (`WaveeShell.cs:1634-1638`).
  * *Targets*: every tab is a **spring-load waypoint** (`springLoadOnly: true`) so dragging across the strip never flashes a refusal;
    a tab standing for an editable playlist additionally **accepts a track deposit** (append, `insertionIndex: null`) with caption
    `Strings.Drag.AddTo(name)` and `settleOnDrop: false` (`WaveeShell.cs:1655-1680`).
  * *The window itself* is a `DropKinds.Files` target allocated ONCE and hung on the full-bleed `tinted` layer; the engine gives a drop to
    the deepest accepting target, so per-row `.mp4` targets still win (`WaveeShell.cs:72-78`, `:1351-1359`).
  * *Scrim scope*: `SceneStore.SpotlightScrimClip` is published as the **content region's** absolute rect, so the title bar and the player
    bar stay lit (`WaveeShell.cs:416-421`, `:1341-1342`).
* **Splitters** — 16-DIP invisible strips translated onto the seams; cursor `SizeWE`; the sidebar one carries the detent
  (`Min 180, Max 460, CompactWidth 56, FadeStart 240, FadeDistance 64, ForcePush 64, ReExpand 190, ShowIndicator false`,
  `WaveeShell.cs:1227-1237`); the rail one is `Polarity.Leading`, `InvertCollapsed`, `Min 200, Max 500` (`:1248-1255`).
  A click on a collapsed pane re-opens it (`Splitter.cs:294-300`). Drag-end commits + persists (`CommitSidebarDrag`, `CommitRailDrag`).
* **Omnibar** — typing begins a suggest generation immediately (so "No results found" never flashes) and fetches 150 ms after quiet;
  `↑`/`↓` move the highlight with wrap to "none"; `Enter` invokes the highlighted row or submits the query (`go("search", q)`);
  a row invoke navigates or plays by kind (`ShellToolbar.cs:265-312`). The omnibar is **the search page's query**: leaving Search clears it,
  arriving restores it (`WaveeShell.cs:1877-1884`).
* **Profile chip** — click opens/toggles the account flyout (`FlyoutPlacement.BottomEdgeAlignedRight`, `PopupChrome.Flyout` =
  `MenuPopupThemeTransition`, focus trap, light dismiss, `ConstrainToRootBounds = false`, `ProfileMenu.cs:162-172`); the chip is
  also the notification panel's anchor when the bell is folded away, through its own separate `OverlayHandle` so the two flyouts
  never fight (`ProfileMenu.cs:78-87`).
* **Friends button (and every rail toggle)** — `ShellUi.Toggle(mode)` is a THREE-state verb, not a switch: clicking the mode the
  rail is *already showing* CLOSES the rail; any other mode switches and opens (`ShellUi.cs:80-85`). The trailing island's
  Friends button is the one chrome-frame caller (`MergedChromeRow.cs:192`).

### Tooltips (loc keys)

theme toggle `shell.lightTheme` / `shell.darkTheme` (`MergedChromeRow.cs:139`) · search icon `nav.search` (`:341`) ·
bell `notifications.title` (`ShellToolbar.cs:142`) · friends `shell.friends` (`MergedChromeRow.cs:152`) ·
settings `auth.settings` (`:154`) · pin = the action row's own label (`:182`) · pinned tab = its header (`TabStrip.cs:860`) ·
"+" = `tabStrip.newTab`, engine loc (`TabStrip.cs:923`) · search placeholder `shell.searchPlaceholder` (`ShellToolbar.cs:328`) ·
drop hint `localFile.dropHint` (`WaveeShell.cs:1429`) · not-found `nav.pageNotFound` + `nav.goHome` (`ContentHost.cs:306-307`).

### Focus & accessibility

`AutomationRole.Tab` on tab plates, `Button` on every chrome affordance and on crumbs, `MenuItem` on suggestion rows
(`ShellToolbar.cs:455`, `:495`). Caption buttons never take focus (`AllowFocusOnInteraction = false`, `CaptionButton.cs:68`).
Focusing the omnibar walks `InputHooks.FirstFocusableIn` from the box chrome to the real editor — focusing the chrome paints a ring that
cannot type (`MergedChromeRow.cs:255-264`). Track boundaries are announced via `Announcer.SayThrottled("title, artist")`, only when an
assistive client is listening (`WaveeShell.cs:643-652`).

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| current route | `Signal<Route>` (`Route(string Name, string? Arg)`) | `Shell.Nav.Current : Shell.Route(RouteKind, EntityUri Subject, StringId Arg, int Tab)` (plan §4.11) | always known — a route is app state, never fetched |
| back / forward enabled | `_canBack/_canForward` from two `List<Route>` | `Shell.Nav.Back.Count > 0` / `Forward.Count > 0` | always known |
| tab label + glyph | `ShellNav.Dest(route)` → `(Arg ?? loc default, glyph)` | `Shell.Dest(route)`: prefer `handle.Title` when `Knows(Identity)`, else the route's `Arg` StringId, else the kind's loc default | **never a skeleton** — a tab must always read as *something*; the label upgrades in place when identity lands |
| tab pinned set | settings `WorkspacePinnedTabs` JSON | unchanged (`Platform.Settings`), decoded by the ported codec | synchronous at boot |
| active tab id | `TabWorkspace.ActiveId` | `Shell.Tabs.ActiveId` | always |
| masthead title | `ShellMastheadStore.For(name, arg)` published by the page + `ShellMastheadRegistry` static fallback | same store shape; the page publishes from the handle's `Title` | **held**: an unknown family keeps the last trail at opacity 0, never collapses |
| drill trail | `DrillTrail.Of(name, arg, liveTitle, origin)` | `Shell.Trail(route, liveTitle, origin)` | pure |
| nav origin | `NavOriginStore` LRU 32, keyed `name␟arg` | unchanged (session state, not entity data) | pure |
| profile chip name / avatar / tier | `PlaybackBridge.User` (`DisplayName`, `AvatarUrl`, `IsPremium`, `Email`) | `User.Me` handle: `Title` (display name), `Image`, flag `Premium` | chip shows the bare avatar until `Me.Knows(UserFields.Identity)`; **never** a spinner in the row |
| auth verb (Sign in / Reconnect / Connecting) | `PlaybackBridge.AuthState : ShellAuthState` (4-state fold) | `Spotify.AuthState` signal with the same four states (**gap — see below**) | the fold IS the readiness |
| unread badge | `NotificationCenterBridge.UnreadCount` | ch 19 | count 0 ⇒ no badge |
| shell tint | `CoverColorPlane` grading of the page's cover url | palette column on the entity (**gap**) | `Definite` vs "not graded yet" is the whole contract — an ungraded claim **holds** the previous colour |
| Home wash (3 legs) | Home modules' first artwork + grading | `Edges.HomeSection` first child's `Image` + palette (**gap**) | a leg with no grading contributes **no layer**, never a grey one |
| rail open / mode / width | `session.json` + `ShellRailWidth` setting | unchanged | synchronous at boot |
| sidebar collapsed / width | `SidebarPreferences` per design | ch 25 | synchronous at boot |
| page content | `ContentHost.PageFor(route)` → page component | `Shell.PageFor(route)` → the handle's `Page` component (plan §2: `X.Page.cs`) | **each page demands its whole model on mount** (plan §4.13); the shell frame itself gates nothing |
| keep-alive residency | `KeepAliveOptions(MaxEntries: 3)` | unchanged | — |
| route → page existence | `ShellRoutes.IsKnown` | `Shell.IsKnown(route)`; developer routes gated on `DeveloperMode.Enabled` | a deep link that fails the check is **logged and dropped**, never opened as a tab (`WaveeShell.cs:1819-1831`) |
| frame telemetry per route | `NavigationFrameWatch.NoteRoute(name, arg)` from the content host's route effect (`ContentHost.cs:73`) | unchanged — keep the call on the 0.3 route effect | always-on, no switch: it attributes the next 4 s of frames to the route and closes with a `nav.frames` line + a memory sample (`App/NavigationFrameWatch.cs:9-24`). `ops/tools/nav-measure.ps1` parses those lines |

**Derived facts live on the model.** The shell must not probe: `RailFits` is computed from three widths in one effect and published as a
signal (`WaveeShell.cs:696-702`); the docked-video capability set is *reported* by the shell and intersected elsewhere (`:711-727`);
`ShellAuthState` is a fold, not four `if`s at the chip (`MergedChromeRow.cs:196-200`).

**Which routes CLAIM the shell material, and which are claimed neutral for them.** The content host owns the complement, and the
two sets must stay disjoint or the order of two effects in the same flush decides the colour
(`ContentHost.PublishesShellMaterial`, `ContentHost.cs:184-186`):

| claims its own material | claimed **neutral** by `ContentHost` |
|---|---|
| `home` · `recents` · `NavRouteNormalizer.LegacyRecentsRoute` · `home-section:<uri>` · `browse-section:<uri>` · `artist:<uri>` · and every `IsDetail` route (`album:` · `pl:` · `prerelease:` · `show:` · `liked` · `local`) | everything else — `search`, `browse`, `browse:<uri>`, `settings`, `albums`/`artists`/`podcasts`, `history`, `whatsnew`, `disco:`, `module:`, the concert family, the two customizers, the diagnostics pages, not-found |

Note the asymmetry that is easy to lose: **`browse-section:` claims, `browse:` does not.** A browse CATEGORY page therefore eases
the chrome to the neutral ground while a browse SECTION drill carries a colour. In 0.3 `Shell.PageFor` and this predicate must be
the same table entry, for the same reason `IsKnown` and `PageFor` must be (below).
**What that asymmetry looks like on screen is a PAIR, not a route** — §5.1's matrix (the `B→S` / `S→B` cells).

### DATA GAPS

| what the shell shows | 0.2.9 source | the plan's model has | proposed column / edge |
|---|---|---|---|
| **Cover palette** (the shell tint, and every accent in the app) | `SpotifyLive/CoverColorPlane.cs` — an async grading plane over decoded covers + a persisted colour table | nothing: no palette column, no image table | an `ImageTable` keyed by `StringId` (the image id) with `Column<uint> Bg, TextBase, Accent`, `Column<byte> Known`, plus `Album.PaletteImage`/`Artist.PaletteImage` slots; grading stays a background producer that commits a Staging like any decoder |
| **Home wash sources** | `HomePage` picks 3 modules' artwork and grades each | `Edges.HomeSection` exists, but no "first artwork per section" and no palette | `HomeSectionEdge { StringId HeroImage }` payload + the palette table above |
| **Auth state fold** (`ShellAuthState`: Live / Connecting / Offline / SignInRequired) | `PlaybackBridge.ProjectAuthState` | `Spotify.Session` exists; no shell-facing projection | `Spotify.AuthState : Signal<byte>` written by the session shell; the chip reads it, never `AuthStatus` |
| **Account identity** (display name, avatar, premium, email) | `PlaybackBridge.User` DTO | `User` handle exists (plan §4.14) but its field groups are unspecified | `UserFields.Identity = Title | Image | Flags(Premium)` + `Email` (StringId, cold) |
| **Nav history log** (`HistoryStore`, 500 entries, disk) + **recent surfaces pin** (`RecordRecentSurface`, 50-slot LRU, ch 16) | `history.json`, `CachedStore.RecordRecentSurface` | `Store` has `touched` + a GC sweep, but no pin-reason | `meta`-backed `history` table (route, arg, at) and a `pin_reason` column on the entity row, or a `recent_surface` edge from `User.Me` |
| **Session snapshot** (active route, back/forward stacks, per-entry origins, tab id, rail state) | `session.json` via `SessionSnapshotStore` | not mentioned | keep the JSON document; it is chrome state, not entity state — `Platform.cs` owns it |
| **Per-route masthead publications** + **nav origins** | in-memory LRUs (16 / 32) | not mentioned | keep as `Shell` statics; they are UI state |
| **Module route parsing** (`ModulePages.TryParseRoute`) | `Backend/Modules` | plan §2 keeps `Modules.*` | `Shell.Route` gains `RouteKind.Module` + a module-id StringId (already in plan §4.11) |
| **Click→detail preview** (`NavPreviewStore`, `HomeSectionPreviewStore`, `BrowseDirectoryStore`, `BrowsePageStore`) | 4 shell-owned stores that carry partial models across a navigation | **obsolete by construction** in 0.3 — the card and the page address the same handle | delete all four; the header renders from the handle the instant the route resolves |

**A 0.2.9 defect to fix, not port**: `ShellRoutes.IsKnown` lists `playback-diagnostics` but **not** `ConnectDiagnosticsPage.Route`
(`ShellRoutes.cs:26-44`), even though `ContentHost.PageFor` renders it (`ContentHost.cs:244-246`). The two lists are supposed to be kept in
step — the class's own ownership note says so — so today a `wavee://open?route=connect-diagnostics` link is refused with
`deeplink.route.unknown` while the Settings/palette route to the same page works. In 0.3 `Shell.IsKnown` and `Shell.PageFor` must be one
table, not two lists that can drift.

**A SECOND 0.2.9 defect, and it is visible on screen**: `ShellNav.Dest` has no arm for **`disco:`** or **`whatsnew`**
(`ShellNav.cs:14-79`), even though both are renderable routes `ShellRoutes.IsKnown` accepts (`ShellRoutes.cs:41`, `:55`). Both fall
through to the switch's default and read **"Your Library" with `Icons.MusicNote`** — in the tab strip's header, in the back/forward
history flyout's rows, in the sidebar's pinned rows (whose pin id IS the route key) and in `ContentHost`'s not-found glyph. So an
artist's discography tab and a What's-new tab are both labelled "Your Library". `connect-diagnostics` has the same hole on both
sides (no `Dest` arm AND no `IsKnown` entry). In 0.3 the ONE route table must carry `(page, isKnown, title, glyph)` per kind, so a
new kind cannot be added with three of the four filled in.

The 0.3-side consequence for §7's "tab label + glyph" row: `Shell.Dest` must have **no fall-through default that names a real
destination**. A kind with nothing to say should say its own kind, never another surface's name.

---

## 8. Pure rules to port verbatim

| class | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `ShellResponsiveLayout` | `Features/Shell/ShellResponsiveLayout.cs` (247) | narrow band 720/760, compact rail 56, drawer width + resting translate, nav-pane tiers + 24-DIP hysteresis, rail clamp 200/500/340, docked-video height fit, `CanFitRail`, **and every chrome budget constant** | `Wavee.Tests/ShellResponsiveLayoutTests.cs` (199) | `Shell/Shell.cs` CORE |
| `MergedChromeLayout` | `Features/Shell/MergedChromeLayout.cs` (192) | the whole chrome-row allocation: stages, fixed budget, search width, tab comfort, lead-cluster reservation + its widen-now/narrow-later hold, quantisation | `Wavee.Tests/MergedChromeLayoutTests.cs` (253) | `Shell/Shell.cs` CORE |
| `TabWorkspace` (+ `WorkspaceTab`, `TabNavIntent`, `TabNavResult`) | `Features/Shell/TabWorkspace.cs` (246) | tab list, active id, open/activate/close/close-where/pin, pinned block ordering, `LastSelectedPinnedId`, `PinnedRevision` | `Wavee.Tests/TabWorkspaceTests.cs` (373) | `Shell/Shell.cs` CORE |
| `WorkspaceTabsPersistence` | `Features/Shell/WorkspaceTabsPersistence.cs` (55) | AOT-safe pinned-tab codec, v1, caps 128 tabs / 1024-char route / 4096-char arg | `Wavee.Tests/WorkspaceTabsPersistenceTests.cs` (64) | `Shell/Shell.cs` CORE |
| `NavRouteNormalizer` | `Features/Shell/NavRouteNormalizer.cs` (28) | the one back-compat rewrite (empty search → Browse; legacy recents route) | `Wavee.Tests/NavRouteNormalizerTests.cs` (47) | `Shell/Shell.cs` CORE |
| `ShellRoutes` | `Features/Shell/ShellRoutes.cs` (79) | the closed set of renderable route keys (deep-link intake, history rows, not-found) | `Wavee.Tests/ShellRoutesTests.cs` (96) | `Shell/Shell.cs` CORE |
| `ShellNav` | `Features/Shell/ShellNav.cs` (83) | route → (title, glyph) for tabs, history, sidebar pins, breadcrumbs | `Wavee.Tests/ShellNavDestTests.cs` (187) | `Shell/Shell.cs` CORE |
| `DrillTrail` (+ `DrillCrumb`) | `Features/Shell/DrillTrail.cs` (112) | breadcrumb composition: the IA arms (Home-section → `Home › X`; browse category/section → `Browse › X`; **concerts → `Browse › Concerts [› event]`**, `:53-65`), origin composition, same-family vs foreign-family vs search-lookup, and the "no label ⇒ empty trail" floor (`:34`) | `Wavee.Tests/DrillTrailTests.cs` (272) | `Shell/Shell.cs` CORE |
| `NavOriginStore` / `ShellMastheadRegistry` | `Features/Shell/NavOrigin.cs` (108) | LRU-32 origin capture; which route families show a masthead and what its title is | `Wavee.Tests/ShellMastheadRegistryTests.cs` (106) | `Shell/Shell.cs` CORE (`Signal` only) |
| `PageNavMotion` (+ `PageSlot`, `NavTransitionKind`) | `Features/Shell/PageNavMotion.cs` (122) | keep-alive slot identity and the four motion recipes | `Wavee.Tests/ContentHostPageTransitionTests.cs` (272) | `Shell/Shell.cs` CORE (it is already source-included into tests) |
| `ShellWashGeometry` (+ `ShellWashPlacement`) | `Features/Shell/ShellWashGeometry.cs` (55) | the three wash ellipses → clipped boxes + node-relative centres/radii, and the four alphas | `Wavee.Tests/ShellWashGeometryTests.cs` (136) | `Shell/Shell.Palette.cs` CORE |
| `ShellTintOwnership` | `SpotifyLive/CoverColorPlane.cs:516-556` | the material hand-over (claim / refresh / definite / held) | `Wavee.Tests/ShellTintOwnershipTests.cs` (135) | `Shell/Shell.Palette.cs` CORE |
| `ZoomAutoPolicy` (+ `ZoomAutoMode`) | `App/ZoomAutoPolicy.cs` (114) | design box 1600×900, `min()` over both axes, plateau snap-down, ceiling 2.0, dense floor 0.75, the one-shot mode migration | `Wavee.Tests/ZoomAutoPolicyTests.cs` (178) | `Shell/Shell.cs` CORE (or `Platform.cs` — it is settings-adjacent) |
| `SessionSnapshotStore` nav codec (+ `SessionRouteDto`, `TryApplyNav`) | `App/SessionSnapshotStore.cs` (417) | the session document: v1, **`MaxStack = 50`** per stack, `SaveDebounceMs = 2000`, the per-entry `NavOrigin` triple, fail-soft restore | `Wavee.Tests/SessionSnapshotTests.cs` (330) | `Shell/Shell.cs` CORE (codec) + `Platform.cs` (the file) |
| deep-link intake (`GoDeepLinkOpen`'s key composition + the `IsKnown` / developer gate) | `Features/Shell/WaveeShell.cs:1796-1833` | `route=album&arg=<uri>` → `album:<uri>` for the six entity prefixes, then `ShellRoutes.IsKnown`, then the developer-route refusal; `report` short-circuits to a dialog | `Wavee.Tests/DeepLinkParseTests.cs` (68) — the URI parse half only; **the composition + gate half has no test today** | `Shell/Shell.cs` CORE |
| `SplitterMath` | engine `FluentGpu.Controls/SplitterMath.cs` | raw width, clamp, resist, fade, collapse | `Wavee.Tests/SplitterMathTests.cs` (103) | **not ported** — engine |
| shell rung invariants | — | `Over(ContentLayer, ShellGround) == ContentSurface`, the stock `FileArea` values | `Wavee.Tests/ShellMergedRungTests.cs` (295) | keep as a `Shell.Palette.cs` test |

---

## 9. Re-author notes

### What must not be simplified

1. **The chrome allocator is arithmetic.** Do not replace `MergedChromeLayout` with `if (width < X)` ladders; every threshold that remains
   (1360 / 1200 / 520) is a *cost input to the budget*, not a layout branch. The 253-line test file pins the trades.
2. **The tab lane is a RESERVATION, not a hug** (issue #88). Its width comes from `LeadClusterW`, quantised, held with the opposite
   hysteresis polarity to the boolean stages. A hug makes the centred search box jump by half a title's length whenever a tab title changes
   (`MergedChromeLayout.cs:22-30`, `MergedChromeRow.cs:71-78`).
3. **`PinButton` must render an invisible 44-DIP placeholder when the destination is unpinnable** — the budget charges for four trailing
   buttons unconditionally, and an element tree that disagrees with the budget reflows the island on every navigation
   (`MergedChromeRow.cs:169-190`).
4. **The content region's stroke is a static sibling, never a border on the FLIPping card**, and it is left+top only via the 1-DIP negative
   margin inside a clipping parent — two 1-DIP strips cannot follow the 8-DIP arc (`WaveeShell.cs:152-177`).
5. **Exactly one `FileArea` coat per band.** The card is transparent; the rail band's coat lives on the spacer's child, and `RightRail`
   paints transparent while docked (`WaveeShell.cs:1099-1106`, `:1152-1199`).
6. **`MinHeight = 0` / `Shrink = 1` / `MinWidth = 0` on every level of the content chain.** Without them a tall page overflows the column and
   shoves the 72-DIP player bar off the bottom for a frame, and a narrow window pushes the fixed rail off the right edge
   (`WaveeShell.cs:1052-1061`, `:1090-1096`, `:1317-1336`).
7. **The exit half of a page recipe is load-bearing.** `Exit.Active = false` detaches the outgoing page in the same frame and the card
   flashes empty (`PageNavMotion.cs:31-34`).
8. **Video-safe classification looks at BOTH sides of a swap**, because the outgoing root is still drawing (`ContentHost.cs:147-153`), and
   the classification stays in the shell, not in the pure motion file, because that file is source-included into the tests
   (`ContentHost.cs:141-144`).
9. **`ShellMaterialLayer` is a component on purpose** (gradients are not `Prop`s; a bound fill is excluded from the implicit brush fade).
   Inlining it into the shell would re-render the shell on every navigation (`ShellMaterialLayer.cs:14-20`).
10. **Neutral is `ShellGround @ A = 0.03`, never `Transparent`** (`ShellMaterialLayer.cs:81-89`).
11. **The narrow drawer stays mounted while closed** so its sidebar state and scene nodes are retained; open/close is compositor-only
    (`WaveeShell.cs:2169-2170`).
12. **`SyncOmnibarToRoute` runs on navigation only, never on keystrokes** (`WaveeShell.cs:1877-1884`).
13. **`Restore` (a tab switch) is not a `Go`**: no back-stack push, no origin write, no history record, neutral motion
    (`WaveeShell.cs:1949-1960`).

### Traps

* **Props freeze at mount** — the shell is mounted once and lives for the process; *anything* it wants to change later is a signal, a bound
  prop, or a `Key` remount. The 0.2.9 code is littered with the correct pattern; copy it rather than re-deriving it.
* **`TabStrip.SelectedIndex` pre-write** — see §1.2. Reading the cell as an early-out is precisely the bug that left the content on the
  previous tab's page (`WaveeShell.cs:1557-1562`).
* **`ContentVersion` is mandatory on the merged `TitleBar`** and must fold in the route *and* the pin-store version, or the trailing
  island shows the previous page's pin state (`WaveeShell.cs:963-966`).
* **A layout boundary must not sit on a box whose size animates** — the sidebar's `IsolateLayout` is *inside* the animating pane
  (`WaveeShell.cs:1030-1040`).
* **`RelativeTo` on the content card** — without it the FLIP measures zero delta (the region absorbs the whole shift) and the sheet snaps
  (`WaveeShell.cs:1126-1133`).
* **Zero-allocation scroll frames vs per-row richness**: the shell's answer is *layout firewalls plus bound props*, not fewer nodes.
  Three `IsolateLayout` boundaries (sidebar interior, content card, rail panel host) stop a deep re-render escaping to a full-tree layout;
  every hot value (viewport height, pane width, rail width, opacity) is a `Prop.Of` bind so a resize re-lays out without re-rendering;
  the chrome's `MergedChromeLayout` signal is published **only when a stage flips** so a resize drag does not re-render the bar per pixel
  (`WaveeShell.cs:488-497`, `:813-828`, `:1040`, `:1115-1118`, `:1299-1305`).
* **Auto-zoom is a control loop** — `baseDip = viewportDip × zoom` is zoom-invariant by construction, but the no-op guard
  (`|suggested − live| ≤ 0.004`) is what actually stops re-entry; and "someone else moved the zoom" is detected by comparing the live zoom
  against *this policy's own last pick* (`WaveeShell.cs:598-632`).
* **Effects must read every signal they depend on FIRST and unconditionally** — an early return before a read drops the subscription and the
  effect never fires again (`WaveeShell.cs:510-518`, `:598-606`).

### Where the plan is wrong or too thin for this surface

1. **§2's Shell budget could not hold owner I's port when this chapter was first written.** `Shell.cs 1 200 + Shell.UI.cs 2 800 +
   Shell.Host.cs 800 = 4 800` lines, against a port list (§5 Wave 4) of `WaveeShell` + `ShellNav/ShellRoutes/ContentHost` + `PlayerBar` +
   `WaveeCommands` + `Actions/*` = 2 348 + 484 + 2 230 + 292 + 3 947 ≈ **9 300 lines before this chapter's other 2 300** (merged chrome,
   toolbar, masthead, material, responsive layout, tabs, drill trail). **Settled**: the surface now splits into named partials —
   `Shell.cs` 2,100, `Shell.UI.cs` 2,600, `+Shell.Masthead.UI.cs` 950, `+Shell.PlayerBar.UI.cs` 2,000, `+Shell.Overlays.UI.cs` 1,700,
   `Shell.Palette.cs` 600, `+Shell.History.UI.cs` 400, `Shell.Host.cs` 1,000 — all owner I, Wave 4.
2. **§2 had no file for the masthead band, the material layer, the narrow drawer or the omnibar when this chapter was first written.**
   They are not optional chrome; each is a named, tested surface. **Settled**: the masthead band, the material layer and the omnibar +
   suggestion popup are the named partial `+Shell.Masthead.UI.cs` (950 lines); the narrow drawer stays in `Shell.UI.cs`.
3. **§4.11's `Route` cannot express 0.2.9's routes.** `home-section:<uri>` and `browse-section:<uri>` are *different families over the same
   uri* (`ContentHost.cs:204-209`), `whatsnew` keys on a version arg, `disco:`/`prerelease:`/`module:` each have their own page rule, and
   `search` carries the query as the arg **and** as a keep-alive slot discriminator (`PageNavMotion.cs:26-29`). `RouteKind` needs
   `HomeSection`, `BrowseSection`, `Discography`, `Prerelease`, `HomeCustomize`, `SidebarCustomize`, `WhatsNew`, `Recents`, `History`,
   ~~`ApiConsole`~~ (DELETED, plan §9.6 Q7, 2026-09-12), `PlaybackDiagnostics`, `ConnectDiagnostics`, `NotFound` — plus a `StringId Arg` that is sometimes a display name and
   sometimes a discriminator. Say which.
4. **§4.11's `Nav` has no forward-stack cap, no origin capture, no session persistence and no tab dimension.** 0.2.9 caps both stacks at
   200, writes a `NavOrigin` per destination on every `Go`, records a `HistoryStore` entry, pins a recent-surface, syncs the active tab and
   snapshots the session — all in one 19-line verb (`WaveeShell.cs:1856-1875`). Port the verb, not just the stack.
5. **§4.12/§4.13 describe pages, not the frame.** Neither mentions the keep-alive boundary, the page transition recipes, the masthead
   overlay, the shell material hand-over or the chrome row — i.e. everything in this chapter. Wave 4's gate ("`--fake` shows the shell frame
   with an empty content host") must also assert: the merged row at three widths, the sidebar collapse animation, a page swap's
   fade-through, and the material cross-fade.
6. **Plan §5 Wave 4 gives owner I "WaveeCommands + Actions/* → one table" alongside the entire shell.** That is two surfaces with 3 947
   lines of action descriptors between them; the action table is chapter 19's subject and should be its own owner.
7. **`--fake` must keep the probe seams.** `WaveeShell.Probe*` (`:223-238`) is how nav/perf/screenshot tooling drives real navigation
   without synthetic input. They are inert in normal runs and must survive the rebuild, or `ops/tools/nav-measure.ps1` and every screenshot
   probe die with the old tree.

### Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's files (excluding `ProfileMenu`, `NavigationFrameWatch`) | **5 424** |
| plan §2 target for `Shell.cs` + `Shell.UI.cs` + `+Shell.Masthead.UI.cs` + `Shell.Host.cs` | 2,100 + 2,600 + 950 + 1,000 = **6,650** (this chapter's surface only; `+Shell.PlayerBar.UI.cs`, `+Shell.Overlays.UI.cs`, `Shell.Palette.cs`, `+Shell.History.UI.cs`, `Platform/Actions.*` are named partials/files for other chapters' surfaces, all still owner I) |
| honest estimate, shell frame alone | **4 400** (≈1 150 of it the pure rules, ported 1:1; the saving over 0.2.9 comes from deleting the four preview/hand-off stores, the hydration-era readiness plumbing and the `ShellUi`/`ShellMaterial`/`ShellMasthead` DTO layer) |
| honest estimate, owner I's whole Wave-4 bucket | **10 000 – 11 000** |

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe -- --fake --width W --height H`.
"static" = screenshot, "hover" = screenshot with the pointer parked, "recording" = a frame capture diffed frame by frame.

1. **@1600×900, route `home`, static** — chrome row is exactly 48 DIP tall and paints no fill; the wallpaper/Mica shows through it.
2. **@1600×900, static** — the content region's stroke exists on the LEFT and TOP edges only, follows the 8-DIP top-left arc, and there is
   no stroke along the player-dock seam or the window's right edge.
3. **@1600×900, static** — no shadow anywhere on the content region; no full-width hairline under the chrome row.
4. **@1600×900, static, colour-pick the page background** — dark build reads ≈ `#303030`, not `#333333` (a lighter value means the card's
   fill came back).
5. **@1600, static** — measure from the window's left edge: hamburger centre at **28** (root padding 2 + the `ChromeParts` margin 6 + half of
   40 — the nudge exists so it lines up with the 56-DIP compact rail's icon column at 28, `WaveeShell.cs:2012-2027`), tab lane starting at
   148, search field 420 wide, and its centre ≈70 DIP LEFT of the window centre.
6. **@1360 → 1359, static pair** — the profile display name disappears at 1359 and does not return until ≈1400.
7. **@1200 → 1199, static pair** — bell + friends + pin + settings leave the row together and reappear as menu rows in the profile menu.
8. **@1000, static** — search is a real field (2 tabs); **@880** it is the 44-DIP magnifier **to the RIGHT of the theme toggle**
   (⌄ then 🔎 then ─ □ ✕), and the centre column is gone entirely so the drag band is continuous.
9. **@880, click the magnifier** — flyout opens bottom-right-aligned, width ≈ `min(420, viewport − 24)`, with inline (not popup)
   suggestions.
10. **@1000, open 12 tabs, static** — search collapses to the icon and every tab is still present in a scrolling lane (none evicted).
11. **@1000, pin 6 of 6 tabs, static** — the search field comes back (pinned chips cost 40 vs 110).
12. **@521 → 520, static pair** — Forward disappears (the stage is `width > 520`, so 520 itself already hides it); it does not come back
    until **561** (the 40-DIP promotion reserve). Alt+→ still navigates while it is off-screen.
13. **@491 → 489 → 457 → 425, four statics** — `+`, then the identity chip, then Back leave, in that order, as the window narrows through
    490 / 458 / 426. At **470 only the "+" is gone**; the chip and Back are still there.
14. **@720 vs @760, static pair** — the sidebar snaps to the 56-DIP compact rail at ≤720 and returns to the expanded pane at ≥760, with no
    oscillation while dragging the window edge across the band.
15. **@688, click the hamburger, recording** — the drawer slides in from −width over 300 ms on `SmoothOut` while the scrim fades over 167 ms
    linear; the desktop expand/collapse preference is unchanged afterwards.
16. **@688, drawer open, press Escape** — the drawer closes and nothing else does.
17. **@1600, click the hamburger, recording** — the pane and the content card ease on identical dynamics for 300 ms; the FileArea underlay
    and the stroke do NOT move; the trailing gap is filled at every frame.
18. **@1600, drag the sidebar seam inward, recording** — the pane tracks the pointer 1:1 (no easing) and every layout transition in the
    window is snapped for the duration.
19. **Drag the seam past 240 DIP** — the pane resists (≈0.28 of the overshoot), its content fades to 0.35, and it collapses to the rail at
    raw 176; drag back out past 190 to re-expand.
20. **Click the collapsed rail's seam strip** (no drag) — the pane re-opens.
21. **@1600, open the right rail, recording** — the reserved band appears painted (FileArea) on frame 0; only the panel's content is seen
    to arrive; the 8-DIP gap appears in the same commit.
22. **@1000, open the right rail** — the page does NOT resize; the rail floats over it on an opaque `FloatingChrome` backing.
23. **Drag the rail's left seam** — width clamps to [200, 500]; the page's content re-tiles; the value survives a relaunch.
24. **Home → Album, recording** — outgoing page fades in place (≈94 % gone by 90 ms), incoming slides 8 DIP from the right while fading in
    over 250 ms; the card is never empty at any frame.
25. **Album → Home (Back), recording** — the same, mirrored (enter from −8).
26. **Switch tabs, recording** — opacity-only cross-fade, no slide.
27. **Home → a module watch page, recording** — translate-only, both directions, and the video hole never washes out or disappears.
28. **Navigate 5 pages forward then Back 3×, static each** — pages 1–3 return instantly with their scroll position intact; the 4th rebuilds.
29. **Browse → a category, recording** — the masthead crumb reads `Browse › <Category>`, the band never changes the page body's height, and
    the title snaps (no slide) while the band's opacity rides the 120 ms window.
30. **Home → a Home-minted chart section, static** — the trail reads `Home › <Section>`; from Browse the same page reads `Browse › <Section>`;
    from a search result it reads `"<query>" › <Section>` with no Browse crumb.
31. **Browse family → playlist, recording** — the band fades out over 120 ms; it never snaps to height 0 and the page body does not jump.
32. **Album A → Album B, recording** — the shell tint cross-fades over 250 ms; it never dips to neutral or toward black between them.
33. **Album (coloured) → Settings, recording** — the tint eases to the neutral ground (a real colour), not to transparent.
34. **Home, static, light and dark** — three radial washes are visible in the chrome bands; the player-dock band carries **no** wash
    gradient (it is cut at y = height − 72) but does carry a flat tint on a detail page.
35. **Home, toggle colour washes off in Settings** — the washes leave and the chrome eases to neutral (a "definite" publish).
36. **Theme toggle, recording** — the whole window re-themes in place over ≈250 ms with no remount, no flash, and the tab underline stays
    under the selected tab.
37. **Tab strip, hover a tab** — the ⓧ fades in over the label's tail without changing the tab's width or the strip's extent.
38. **Tab strip, hover anywhere in the strip** — the "+" cross-fades in over 150 ms; its 32-DIP slot is reserved even while invisible.
39. **Switch tabs, recording** — the 2-DIP accent underline springs to the new tab (response 0.25, damping 0.80) and is inset 12 DIP from
    both label edges; it sits at y = 38–40 in the 48-DIP row.
40. **Pin a tab** — it becomes a 36-DIP icon chip, moves to the head of the strip, gains an accent-subtle plate when selected, and a 1×20
    divider separates the pinned run; relaunch and it is still there.
41. **Right-click a tab** — the menu lists exactly: Pin/Unpin · — · Close tab · Close other tabs · Close tabs to the right ·
    Close all unpinned tabs (+ separator + Pin-to-sidebar when applicable), with a header naming the destination.
42. **Middle-click a tab** — it closes.
43. **Drag a track over a playlist tab, hold** — the tab springs open mid-drag; releasing appends the track and shows the deposit toast.
44. **Right-click Back** — a flyout lists up to 8 destinations, most recent first, with the correct per-kind glyph, plus "View all history"
    when the stack is deeper.
45. **Type in the omnibar** — the progress bar appears on the first keystroke (never "No results found"), rows arrive ≈150 ms after quiet,
    the ghost completion appears only when no row is highlighted, and ↑/↓ walk queries then rich rows.
46. **Focus the omnibar with Ctrl+F, then narrow the window past the field boundary** — the caret lands in the flyout's field.
47. **Navigate away from Search** — the omnibar clears to the placeholder; navigate back to Search and the query is restored.
48. **Deactivate the window (click another app), static** — tabs, centre, trailing and caption-leading islands drop to 50 % opacity, the
    hamburger's glyph goes tertiary, caption glyphs go disabled, and no fill changes.
49. **Drag a file over the window** — the page dims under the spotlight scrim while the title bar and the player bar stay fully lit; the
    "Drop a file to play it" pill is centred over the content region.
50. **Play a video and press F11** — the chrome row and the player bar leave the tree entirely (no ghost strip, no doubled transport);
    Escape or F11 restores them.
51. **Ctrl+= / Ctrl+− / Ctrl+0 and Ctrl+wheel** — zoom steps on the ladder, the whole window re-lays out, the value survives a relaunch, and
    a manual step flips the mode out of Auto.
52. **Resize to a very tall page (a detail page with a 600-DIP rail) at 900 tall, recording** — the player bar never slides off the bottom
    for a frame.
53. **@1600, static** — the tab lane's reserved width does not change when the active tab's title changes length (open a playlist with a
    long name in tab 2 and re-select tab 1).
54. **Deep link `wavee://open?route=album&arg=<uri>`** — it opens on the active tab; `wavee://open?route=nonsense` opens nothing and logs
    `deeplink.route.unknown`.
55. **Deep link a developer route with developer mode off** — nothing opens (`deeplink.route.developerOnly`).
56. **Relaunch** — the pinned tabs, the last-selected pin, the active route, the back/forward stacks (capped at 50 entries each on disk),
    the per-entry nav origins, the rail's open state + mode, the sidebar width and the rail width all come back exactly as they were.
57. **@1600, static, exactly 1 tab open** — the tab shows **no** ⓧ at any hover (`tabs.Count > 1` is the gate); open a second tab and both
    gain one.
58. **Pin a tab, hover it** — a pinned chip shows **no** ⓧ ever, carries no underline (its accent-subtle plate is the selection), and
    tooltips its own header.
59. **@1600, right-click a DISABLED Back (fresh launch, empty stack)** — nothing opens; no empty menu.
60. **Type a query, hover an artist row vs a song row** — the ♡ appears on the **song** row only; ▶ is absent on a User/Genre row; "…" is
    absent when no action services are resolved.
61. **Type a query with more than 10 rich hits** — exactly 6 query rows and 10 rich rows render, and ↑/↓ walk exactly those 16.
62. **Sign out / pull the network, static** — the trailing island becomes "Connecting…" then an accent `Reconnect` button; the search field
    does **not** move stage, and the drag bands absorb the width difference.
63. **Browse → a category with a very long name @720** — the crumb run stays one line each with ellipses, the current title wraps to at most
    2 lines, and neither pushes the "Show all" button off the row.
64. **Navigate anywhere, then read `%LOCALAPPDATA%\Wavee\logs`** — a `nav.frames` line exists for that route within ~4 s, with no
    environment variable set.
65. **@1600, drag the window edge from 1600 to 1400 slowly, recording** — the chrome row re-renders only when a stage flips (watch for tab
    lane re-quantisation at 10-DIP steps), never per pixel.
66. **Open a tab whose title is much longer than the previous one** — the tab lane's reservation widens on the SAME frame (no clip), and a
    shorter title afterwards does not give the width back until it is 40 DIP smaller.
67. **@1600, deactivate the window, static, zoom into the caption-leading cluster** — the theme toggle **and** the magnifier (icon mode)
    are at 50 % opacity, while the three caption buttons beside them keep full-strength fills. Four islands dim, the hamburger only
    swaps foreground, nothing else changes.
68. **@1600, open the rail, then drag its left seam RIGHT past 200** — the rail resists (0.28 of the overshoot), its panel fades to
    0.35, and at raw 156 the rail CLOSES; dragging back out past 210 re-opens it. Unlike the sidebar seam, the rest of the window's
    layout transitions are NOT snapped during this drag.
69. **Collapse the sidebar to the 56-DIP rail, then drag the seam** — the pane presents EXPANDED for the whole drag (drag peek), not a
    56-DIP column of cut-off labels; releasing below the collapse point leaves it collapsed.
70. **Browse → a category whose page has not published a title yet, static** — the crumb reads `Browse › <route Arg>`, and with no Arg
    it reads `Browse › Browse`. It never renders blank, never collapses, and never shows a skeleton.
71. **Right-click Back with a 12-deep stack, choose "View all history"** — a NEW TAB opens on History; the tab you were on keeps its
    back stack exactly as the flyout showed it.
72. **Close all unpinned tabs with no pinned tabs** — exactly one fresh Home tab remains (never an empty strip), and it is active.
73. **Right-click a tab, static** — only "Close tab" carries a glyph; the other three close rows are glyph-less, and "Close all
    unpinned tabs" is greyed only when every tab is pinned.
74. **Type a query, click a GENRE row** — the page opens and its masthead crumb reads `"<query>" › <Genre>` with **no** Browse rung
    (the search-lookup origin), proving the row wrote a `NavOrigin`, not just a route.
75. **Run with the notification service unavailable (`--fake` without it), @1600, static** — the bell is a DISABLED glyph in the row,
    not a missing button: the trailing island is exactly as wide as it is with the service present.
76. **Navigate to a browse CATEGORY from a coloured album page, recording** — the shell material eases to the neutral ground (the
    category route does not claim); navigate to a browse SECTION drill instead and it carries a colour. The two must not behave alike.
77. **Open a `disco:` (discography) tab and a `whatsnew` tab, static** — *known 0.2.9 defect, must NOT be reproduced*: in 0.2.9 both
    read "Your Library" with a music-note glyph. In 0.3 each must read its own destination name.
78. **Play a video in the in-window mini player at its default anchor, scroll a page to the bottom** — the page's content ends ABOVE
    the mini player (the content host's bottom reserve), and dragging the surface releases the reservation without remounting the page
    (scroll position and selection survive).
79. **@880 (icon mode), open the flyout on the very first frame after a resize** — the suggestion popup is ≈ `min(420, vp − 24)` wide
    immediately; it never renders one frame at a sliver width.
80. **Maximize, restore, then snap left / right / to a quarter, static each (1920×1080 @100 %)** — nothing in the frame branches on
    placement: the content card keeps its `8,0,0,0` corners in every state, the left+top stroke and the FileArea coats are unchanged, and
    each state reads as the width-only frame it is (snapped half = 960 DIP ⇒ W3's trailing island + W4's magnifier; quarter = the same
    chrome over a 400-DIP content region). Only the caption max glyph changes (`❐` restore while maximized), and the window's corners are
    squared by Windows, not by the app.
81. **Maximize, then restore, recording** — no structural track survives the size change: the sidebar pane, the content card and the tab
    lane land at their new geometry on the frame the resize lands, with no FLIP overlap and no eased width (maximize/snap take no modal
    loop, so the snap is `CancelStructuralAll`, not the resize suppression).
82. **Two monitors, `Auto` zoom.** (a) Drag a **restored** 1600×900 window between a 4K@150 % and a 1080p@100 % monitor — nothing moves:
    the DIP width is preserved by the OS's suggested rect, every band re-resolves to the same answer, and the zoom stays where it was
    (only glyphs re-rasterise). (b) **Maximize** on the 4K@150 % (zoom reads 150 %), then drag it to the 1080p@100 % — the window
    re-lays out at the new DIP extent immediately and the zoom steps to **100 %** within ~500 ms of the drag settling; both stages snap.
    (c) Repeat (b) with the zoom picker set to any explicit value (mode `Manual`) — the zoom never moves.
83. **Maximize, quit, relaunch** — 0.2.9 comes back **restored at 1180×760** (window placement is not persisted; the session document
    carries only the rail's open state and mode). Decide this deliberately for 0.3 rather than inheriting it by accident.
84. **`browse:<uri>` → `pl:<uri>`, recording (the composed pair, W10b)** — three things start on the same frame and end on
    three different ones: the band fades out over 120 ms **still showing the Browse trail** (never the playlist's title, never
    a collapse to zero height), the page fade-through runs 0 → 340 ms, and the shell material eases neutral → the playlist's
    colour over 250 ms. If the cover is not cached, the material instead stays neutral through the whole swap and ramps
    later — both readings are correct, a DIP to neutral and back is not.
85. **`browse:<uri>` → `browse-section:<uri>` and back, recording** — the masthead band must **not** fade out and back in: it
    stays lit and the crumb row grows/loses a rung. The window's material, however, gains a colour going in and loses it
    coming back — the `browse-section:` claims / `browse:` does not asymmetry. Anything that makes the two behave alike is
    the regression.
86. **`settings` → `home` vs `album:` → `home`, two recordings** — from Settings the three washes simply enter and the flat
    tint rect never moves (it is the neutral ground at both ends). From an album the ALBUM's colour is what is on screen
    while Home is already mounted, and it hands over only when Home's first leg grades. Neither may flash neutral.
87. **`disco:<…>` → `artist:<same artist>`, recording** — the window goes from the neutral ground to the artist's tint, and
    the masthead band is absent on both sides. The discography and the artist page are the same artist and deliberately
    **not** the same material.
88. **A `module:` watch page ↔ any page, recording, with video playing** — both directions are translate-only: no opacity
    anywhere on either page root, and the video hole never washes out or vanishes. Then switch TABS onto and off the watch
    page: the page hard-cuts (no motion at all) while the masthead band and the shell material still run their own channels.
89. **`album:` → `artist:` (the album's own artist link), recording** — ONE 250 ms cross-fade of the window's tint from the
    album's colour to the artist's, with no neutral frame between them and no band on either side. Colour-pick the chrome
    every frame: the ramp must be monotonic between the two colours, never a dip toward the neutral ground (the stable
    tint key is what makes this a brush fade rather than a remount).

---

## 11. Audit log

Adversarial re-read against the 0.2.9 sources on 2026-09-12. Every value in §2/§3/§5 was recomputed or read back from
`file:line`; the wash table, the four chrome-row worked examples, the W7 shed ladder, the splitter arithmetic, all 26
`Chrome*` budget constants, the five motion curves, `Expressive.Fast/DistBase`, `WaveeMotion.Faster/Fast/Standard`,
`MotionSprings.SelectorPill`, the `FileArea` rungs, `PlayerDock.Reserve`, `SessionSnapshotStore.MaxStack/SaveDebounceMs`,
`ZoomAutoPolicy`'s design box, all 25 loc keys and every line count in the header and §8 **verified correct as written**.

| # | kind | section | correction |
|---|---|---|---|
| 1 | wrong | §3 constants, W1 table | `ChromeNameEnterW` is `ShellResponsiveLayout.cs:32`, not `:34`; `ChromeBarLeadW` is `:57`, not `:56`. Values (1360 / 60) were right. |
| 2 | wrong | W1 allocation table | The lead-column breakdown read "root padding 2 + pane toggle 40 + LeftHeaderPad 14", which sums to 56, not 60. The toggle's **advance** is 44 (40 wide + `ChromeParts` margin 6 left / −2 right). Re-stated. |
| 3 | wrong | §5 tab-selection row | `MotionSprings.cs:17` names a file that does not exist. `MotionSprings.SelectorPill` (response 0.25, damping 0.80) lives in `FluentGpu.Engine/Animation/Motion.cs:17`. |
| 4 | wrong | §1.1 tree, W1 note | Seven `TitleBar.cs` refs inside the merged-row block were 1–5 lines high (tabs island, both drag bands, the centre column, the trailing island, CaptionLeading, the caption cluster, the island-centring comment). Corrected. `:102`, `:300-311`, `:310`, `:477-479`, `:513-517` were already right. |
| 5 | wrong | W20 | Cited `:375`, `:463`, `:419` for the deactivated dim. `:463` is the NON-merged content column, which Wavee never mounts. The four islands that actually dim are `:369` (tabs), `:404` (centre), `:422` (trailing), `:486` (CaptionLeading). |
| 6 | missing | W20 | **The CaptionLeading island also dims to Opacity 0.5** — i.e. the theme toggle and, in icon mode, the search magnifier. The frame listed only tabs/centre/trailing. Added, with the CaptionButton "glyph dims, fills stay wired" rule. |
| 7 | missing | W12 / §6 | **The right-rail seam carries a live detent the chapter denied.** `WaveeShell.cs:1248-1255` passes no `FadeStart`/`FadeDistance`/`MinFade`/`Resist`/`ForcePush`/`ReExpand`, so every one is an engine default (`FadeStart = Min = 200`, 44, 0.35, 0.28, 44, 210): dragging the seam in resists, fades the rail panel to 0.35 through `_rightRailFade` (`:1306`) and **closes the rail at raw ≤ 156**, re-opening past 210. New wireframe **W12b** + two motion rows + parity 68. Also noted: the rail splitter passes no `dragging:` signal, so a rail drag does not snap the window's layout transitions. |
| 8 | missing | W12, §1.1 tree | **`_sidebar.DragPeek`**: the pane's bound width is `presentedCompact && !DragPeek ? 56 : _sidebarWidth` (`:1014-1015`) — a drag on a collapsed pane presents it expanded. Absent everywhere. Added + parity 69. |
| 9 | missing | §1.1 tree | **`ContentHost`'s unconditional bottom reserve** for the floating video surface (`ContentHost.cs:37-45`, `:81`) — and the reason the wrapper is unconditional (mount/unmount would cold-restart every keep-alive page). Added + parity 78. |
| 10 | missing | W13, §7 | **The masthead title's three-tier fallback** (live publication → last trail crumb → `ShellMastheadRegistry.StaticTitle`, `NavOrigin.cs:85-88`, `:93-107`) and the fact that a family route whose three tiers are all blank is **not live**. This is the band's whole readiness contract and §7 only said "held". Added + parity 70. |
| 11 | missing | W13 | Three per-crumb states: a route-less crumb keeps `Role=Button`/`Focusable`/`Cursor=Hand` with `OnClick = null`; the crumb press colour returns to `TextTertiary`; "Show all" absent is a zero-size `BoxEl` sibling, not an omitted child. |
| 12 | missing | W16, §6 | **"View all history" opens a NEW TAB** — `_go("history", null)` is routed through `OpenNewTab` by `GoNav` (`WaveeShell.cs:1937`), so the flyout does not navigate the stack it was showing. Added + parity 71. |
| 13 | missing | W15 | The suggestion popup's **720-DIP pre-measure fallback** (`ShellToolbar.cs:369`), and the full per-kind row-invoke table — including that a **Genre** row writes `NavOrigin(query, "search", query)` through `HistoryStore.GoWithOrigin` (`:259-263`, `:296-298`), which is the mechanism parity item 30 measures. Added + parity 74, 79. |
| 14 | missing | §6, W15 | **A null `NotificationCenterBridge` renders the bell DISABLED, not absent** (`ShellToolbar.cs:122`) — the trailing island's width is service-independent, the same rule `PinPlaceholder` exists for. Added + parity 75. |
| 15 | missing | §6 | **`ShellUi.Toggle` is a three-state verb**: clicking the mode the rail already shows CLOSES the rail (`ShellUi.cs:80-85`). The Friends button is the chrome frame's one caller. |
| 16 | missing | §6 tab context menu | Only `Close tab` carries a glyph; the other three close rows pass `default`. `Close all unpinned tabs` is enabled on `HasAnyUnpinned`, which the list omitted entirely. Also added `CloseWhere`'s **reseed-a-Home-tab** floor and `Close`'s neighbour rule (`TabWorkspace.cs:98-146`). Parity 72, 73. |
| 17 | missing | §7 | **Which routes claim the shell material** (`ContentHost.PublishesShellMaterial`, `:184-186`) was never written down, including the asymmetry that `browse-section:` claims and `browse:` does not. Added as a table + parity 76. |
| 18 | missing | §7 defects | **A second 0.2.9 defect**: `ShellNav.Dest` has no arm for `disco:` or `whatsnew`, so both renderable routes read **"Your Library" + `Icons.MusicNote`** in the tab strip, the history flyout, the sidebar pins and the not-found glyph (`ShellNav.cs:14-79` vs `ShellRoutes.cs:41`, `:55`). `connect-diagnostics` has the hole on both sides. Added with the 0.3 rule (one table carrying page + isKnown + title + glyph). Parity 77. |
| 19 | missing | W6 | The narrow drawer's second `SidebarHost` is always the **expanded** one (`_drawerExpanded` is seeded false and never written) and takes `inDrawer: true`; both mounts share one width signal and one `SidebarPreferences`. |
| 20 | missing | W1 note | `Resolve` floors `naturalTabExtent` at 32 and `EstimatedTabExtent` floors the tab count at 1 (`MergedChromeLayout.cs:54`, `:82`); `LeadClusterFor` floors twice more (`:163`, `:170`). |
| 21 | missing | §5 | The tab lane's **pip-click scroll** (`max(110, viewportW − 40)`, animated unless reduced motion, `TabStrip.cs:715-720`) had no motion row. |
| 22 | wrong | §3 token table | Sixteen `TabStrip.cs` refs drifted 8–20 lines (close button, pinned chip, pin divider, scroll pips, add button, icon, underline, indicator, middle-click, tooltip). All VALUES were correct; the refs are corrected and several rows gained the missing hover/pressed fills, `TabStop=false`, `JustifySelf`, and the pinned-icon `AccentTextPrimary` colour. |
| 23 | overclaim | W16 | "up to 8 entries, MOST RECENT FIRST" is right, and `HistoryMenuMax = 8` has exactly one consumer — confirmed, no change; recorded here so a future reader does not re-check. |
| 24 | unverified→verified | §5 | "reduced motion: fade kept, translate dropped" for the page recipes is engine policy (`ReducedSnap` parks the rise/blur and still cross-fades opacity — the rule `WaveeEntrance` states explicitly). Left as written; it is an engine contract, not an app number. |
| 25 | unverified | §9, §10 | The line-budget estimates (4 400 / 10 000–11 000) and the three "the plan is wrong" line sums are the author's projections and were not re-derived. The 0.2.9 line counts they build on (5 424 and every per-file figure in the header and §8) **were** re-counted and are exact. |
| 26 | critic-fix: missing | §2 (new **W23**, **W24**), §5, §10 | **Window state and the per-monitor DPI path were unspecified** — all 22 frames varied by width alone. Verified against 0.2.9 and added. (a) **W23 — restored / maximized / snapped**: the app has **no** window-state branch (`WindowState`/`Maximized`/`IsZoomed` have no reader under `src/apps/Wavee/`); only three things change and all three are engine/OS — the caption max glyph swap on the `WindowChromeEpoch` bump W20 already rides (`TitleBar.cs:253`, `:270`, `:497-503`, `Win32Platform.cs:1819-1827`), DWM-owned corners/shadow/borders with **no** app corner preference on the main window (`Win32Platform.cs:2084-2092`, `Win32Theme.cs:157`) so `ContentPaneCorners` stays `(8,0,0,0)` maximized (`WaveeShell.cs:150`), and the maximized top inset + negative-client-y Fitts fold (`:2093-2099`, `:2113-2115`, `:1092`). Snapped is a size: half a 1080p panel = 960 DIP ⇒ W4 icon mode + W3's folded actions; the same snap at 150 % = 640 DIP ⇒ W5's narrow shell, which is why `MinWidth` is 300 and not 360 (`Program.cs:536-540`). Recorded too: the frame has **no viewport-height breakpoint** (`ShellResponsiveLayout` reads height only for the docked-video cap, `:128-158`), and window placement is **not persisted** (`Program.cs:214`, `:539`; `SessionSnapshotStore.cs:29-36`). (b) **W24 — the DPI hop**, citing `large-display-scaling.md §1.1/§3.2` (which no chapter had inherited): `WM_DPICHANGED` folds the new scale, keeps the app zoom and adopts the OS's suggested rect (`Win32Platform.cs:1841-1858`), so a **restored** window's monitor hop changes **no** breakpoint and **no** zoom — `baseDip = clientPx / osDpiScale` is invariant and the `≤ 0.004` no-op guard returns (`WaveeShell.cs:621`); the ladder re-resolves only when the DIP extent itself changes (a **maximized** hop 4K@150 % → 1080p@100 % steps 150 % → 100 %, the two 3840×2160 rows of 00's W13), as a two-stage settle — geometry on the hop's frame, zoom ≤ 500 ms later (`WaveeShell.cs:587-597`). (c) **Five motion rows**: maximize/restore/snap and the DPI hop both **snap**, and not through the modal-loop suppression — that is gated on `InModalLoop` (`AppHost.cs:3234`) and false for them; the snap is `CancelStructuralAll` gated on `resized` (`:3240`, `:3400-3410`). Plus the cadence re-probe at `WM_EXITSIZEMOVE` for an equal-DPI hop (`:1900-1906`, `:1711-1717`). Parity 80–83. |
| 27 | critic-fix: missing | §2 (new **W10b**), new **§5.1**, §10 | **Page transitions were specified per DIRECTION and never per PAIR.** §5's recipes, the band's 120 ms family fade and the material hand-over are three channels with three different predicates, started by one route flush, and nothing composed them (the only pair written down anywhere was 10 parity 34, album → Home). Verified against 0.2.9 and added: **W10b**, the composed timeline (all three start at t = 0; the material is done at 250 ms while the page settles at 340 ms — the chrome's colour LEADS the page), and **§5.1**, a seven-family × seven-family matrix (`H` home / `R` recents / `S` the two section drills / `B` browse+concerts / `T` tint pages / `N` plain neutral / `V` module) whose cells carry band + material, with **⟂** marking the video-safe recipe's row and column. Families derived from `ContentHost.PublishesShellMaterial` (`:184-186`), `ShellMastheadRegistry.TryResolve` (`NavOrigin.cs:81-82`), `ContentHost.PageTransition` (`:147-153`) and the four publishers (`HomePage.cs:229-237` = three legs, `RecentsPage.cs:318-325` and `HomeSectionPage.cs:177-182` = ONE hero leg each — a split §7's claim table did not carry — `CoverPaletteLeaves.cs:240-252` = the flat tint). Four compositions that only a pair exposes were added with it: (a) `B→S` moves the material while the band does not move at all, and `S→B` the reverse — the `browse-section:` claims / `browse:` does not asymmetry item 17 added as a table now has its visible consequence attached; (b) `N=N` is a coalesced no-op, not a repaint, because `B`/`N`/`V` share ONE neutral owner token (`ContentHost.cs:31`, `:54`) and `Signal<T>` drops an equal write (`FluentGpu.Engine/Foundation/Signals/Signal.cs:60-65`); (c) `settings → home` runs **no** tint ramp (the tint rect is `NeutralGround` at both ends, `ShellMaterialLayer.cs:49`, `:97`) while `album: → home` holds the ALBUM's colour until Home grades, and the flip to the tint drops the wash host in one frame — the host carries no `Exit` (`:72-77`), only the legs do (`:129-130`), flagged as a 0.3 decision point; (d) at the **=** cells the title snaps on a stable key (`ShellMastheadBand.cs:104-109`) but the prefix box remounts when the trail ROOT changes (`:120`). Also recorded: a tab switch is a pair too — `Restore` writes Neutral (`WaveeShell.cs:1956-1957`), so the page cross-fades while the band and material behave exactly as for a `Go`. **One correction to the finding itself:** it described `disco: → artist:` as "no tint either side". `artist:` DOES claim (`ContentHost.cs:186` → `IsArtist`) and `disco:` does not, so the pair is neutral → tint; that is parity 87. Parity 84-89. |


**token-reconcile (2026-09-12):** `Tok.CaptionCloseHover`, named here at `:876` but missing from the first build of `00-design-system.md §12.1`, is now indexed there (`#C42B1C` in BOTH themes — it does not follow the theme the way `Tok.SystemFillCritical` does). No value in this chapter changed.

**consistency 2026-09-12:** header named only `Shell.UI.cs`/`Shell.Host.cs`/`Shell.cs`; the plan splits this surface into named partials. Header, §1.2's tree (the omnibar/search rows, `ShellMastheadBand`, `ShellMaterialLayer`) and §9's "where the plan is wrong" bullets 1-2 and line-budget table now point at `Shell.cs`, `Shell.UI.cs` and the named partial `+Shell.Masthead.UI.cs` (masthead band, material layer, omnibar + suggestion popup), all owner I, Wave 4, per plan §2.

**answers 2026-09-12: Q7 (plan §9.6) reaches this chapter once.** `ApiConsole` is struck from §9's `RouteKind` member list — the API console and its four `ApiDebug*` helpers are DELETED, not ported — and the entry is kept struck through with the reason and date rather than removed. No wireframe, token or motion row changed.
