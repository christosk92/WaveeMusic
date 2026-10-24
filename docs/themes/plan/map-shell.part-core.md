# Shell core (startup, frame, navigation, chrome, theme pipeline) — mapped from main

All paths are under `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\` unless absolute.

## A. Startup sequence up to the first presented frame

`App.cs:77 Main`:

| # | Call (App.cs line) | What it does for presentation | Sync before frame 1? |
|---|---|---|---|
| 1 | `GCSettings.LatencyMode` (79), `Glyphs.Register()` (80) | registers `wavee-icons.otf` PUA + bundled `SegoeFluentIcons.ttf` before the harness | yes |
| 2 | `Crash.Handler.TryRun` (83) | the crash-handler child process arm; returns early | — |
| 3 | `Platform.Boot()` (88) → `Platform.cs:1134` | `Args.Parse` (1140), `Backing = SettingsBackingFor(...)` (1141: HKCU registry / profile JSON / in-memory for `--fake`), factory reset (1144), `HostOpenSettings()` (1145 → `Platform.Host.cs:72`: `RegistryAppSettings.Open` or `FileAppSettings` or `OverlaySettings`), `ZoomAutoPolicy.MigrateMode` (1146), `Developer.Load` (1147), `ScrollSettings.Load` (1148), locale (1149-1153 → `Platform.Host.cs:93` loads `assets/loc`, sets culture), log (1154), crash writers (1155), credentials (1156), `Scope = ResolveScope()` (1157) | **yes: every `Platform.Settings.Get` is a synchronous registry/file read through the `Facade` (`Platform.cs:968-983`); no async load. Default-only facade before Boot.** |
| 4 | `Diagnostics.Probe.TryRun` (100) | `--headless`: no window | — |
| 5 | Recovery arm (110-140) | `Recovery.Run` is an **engine-free Win32 window** (`Screens/Recovery.cs`, `Recovery.Win32.cs`) shown BEFORE any engine/theme exists | — |
| 6 | `Shell.InstallMarshallers()` (142) → `Shell.Host.cs:357` | UI-thread posters for Playback/Spotify/Store/Palette/Lyrics/AiLyrics/StoreShot/Sidebar; `FluentApp.FrameCompleted` tick (399) | — |
| 7 | `Shell.AcquireInstance()` (147) → `Shell.Host.cs:155` | single-instance gate, activation payload | — |
| 8 | `RegisterShapes`, `Store.Use`, `Entities.Boot`, `Spotify.Boot`, `Playback.Boot`, `Modules.Boot`, `Video.Install` (148-193) | data/playback; `Playback.InstallCardSeam` (188) installs the now-playing equalizer pill seam into `Controls` | — |
| 9 | `Sidebar.Boot()` (194) | design/pane state/layout doc/pins (see sidebar part) | yes (sidebar part) |
| 10 | `HomeUi.HomeLayout.Boot()` (195) | home-layout.json v2 | yes |
| 11 | `Shell.InstallUi()` (199) → `Shell.UI.cs:55` | `RootFactory = Frame` (57); `SetPage(History)` (58); **`Design.Install()` (61) = `Shell.SeedPalette = Design.SeedPalette` (`Design.cs:206`, i.e. `Tok.Use(Tok.NeutralPalette, kind)` at `Design.cs:203`)**; `Actions.InstallUi()` (62); `Controls.RouteForUri` (65); `Actions.Services.*` seams (74-84) | — |
| 12 | `Modules.InstallUi`, `Queue.InstallUi`, `Track.InstallActions`, `Home/Album/Playlist/Artist.InstallPages`, `Settings.InstallScreens`, `Diagnostics.Install` (200-223) | page factories into `s_pages` (`Shell.cs:209`) + cross-cutting seams; `Settings.InstallScreens` → `Settings.UI.cs:52 Setup.InstallWizard()` (`Setup.UI.cs:441`: computes `due`, warms Lottie heroes) | — |
| 13 | `Shell.StartUpdater`, `Crash.Uploader.Install`, `Lyrics.Boot`/fixture, `Spotify.Login()` if `HasStoredCredential` (225-231) | login starts BEFORE the window; posts queue until the root attaches | — |
| 14 | `Shell.Run()` (232) → `Shell.Host.cs:195` | see below | — |

`Shell.Run` (`Shell.Host.cs:195-315`), in order:
1. `ParseArgs` (202): `--frames/--screenshot/--width/--height`; default window 1180×760 (323-324).
2. `InstallCrashNet` (204), `Crash.Host.Install` (206), `InstallMarshallers` (207, idempotent).
3. Protocol registration + first activation payload (212-228); `Tray.StartHidden(...)` / `Tray.Host.Arm(startHidden)` (222-227) read `Keys.TrayStartHidden`, `Keys.TrayIconMode`.
4. **`SeedTheme()` (230) → `Shell.Host.cs:674-690`**: reads `Keys.ThemeMode` (0 System / 1 Light / 2 Dark), resolves System via `FluentApp.SystemUsesLightTheme()`, calls `SeedPalette(kind)` (= Wavee neutral palette) else `Tok.Use(kind)`; `Tok.SetAccent(FluentApp.SystemAccentRamp() ?? SystemAccent())` (685-686); `Theme.IconFont = WaveeFonts.Icons` (689). **This is the only theme seed before the window exists.**
5. `Session.Load` (231), `History.Store.Load` (232), `PlayLog.Load` (233), **`RestoreNav()` (234 → 1005)**: pinned tabs from `Keys.WorkspacePinnedTabs`, session nav (`session.json`), sets `Current`, `CanBack/Forward`, `SelectedTab`, `SyncOmnibar`, arms jump list (1032). `Tips.Load` (235). `AttachPlayback()` (239, `Shell.Host.Deck.cs`: deck restore).
6. `FluentAppHarness.Run(() => new RootHost(), AppOptions{ Title="Wavee Music", MinWidth=300, CustomFrame=true, MicaAlt=false, RenderCensus=true, InitialSceneCapacity=8192, **Zoom = Keys.ZoomLevel** (274), ImageCacheDirectory, StartHidden }, HarnessOptions)` (249-285). `boot.window` mark at 243; `boot.firstframe` on first `FrameCompleted` (120-126).
7. `RootHost.Render` (`Shell.Host.cs:1041-1070`): `AttachUiPoster(UsePost())` (1046: drains early posts, `Notify.HostInstall`, arms audio warm + memory governor), `Tray.Host.Watch` effect (1051), palette filler (1056), ambient power poll (1062), AiLyrics driver effects (1066-1067), then `RootFactory()` → `Shell.Frame()` (`Shell.UI.cs:89`): `SeedFrameState()` (121-133: `Ui.RailWidth` ← `Keys.ShellRailWidth`, `Ui.DockedVideoHeight` ← `Keys.ShellDockedVideoHeight`, rail open/mode ← `Session.ShellSection`), then `new FrameRoot()`.
8. `FrameRoot.Render` (`Shell.UI.cs:223-510`): window band seeded from first viewport width (239-248: `SidebarPaneModeRules.BandOf/Resolve/PresentedWidth`), all effects, then the tree. The setup wizard is NOT mounted here: `Setup.WizardChrome()` sits inside `Shell.Overlays()` (`Shell.Overlays.UI.cs:48`, first of three chromes) and opens the plate via `post(Open)` / `post(post(Open))` (`Setup.UI.cs:460-472`) i.e. **one or two frames after the frame root mounts**; the shell paints behind it dimmed by `CoverScrim` (`Shell.UI.cs:870-883`, `Setup.Covering == Cover.Dim`, `Tok.FillSmoke`, 250 ms linear).

Exit tail: `Shell.Host.cs:293-314` + `App.cs:235-254`.

### Where a wrong-theme frame could come from (main)
- **Light/dark**: seeded synchronously at `SeedTheme` before the window; safe. `OnSystemColorsChanged` (`Shell.UI.cs:699-708`) flips live only when `ThemeMode == 0`, via `SeedPalette` + `s_requestTheme(Design.Motion.Standard)` (the engine's 250 ms re-theme cross-fade). `ToggleTheme` (`Shell.Palette.cs:267-271`, title-bar sun/moon button `Shell.UI.cs:1276-1280`) and `Settings.SetTheme` (`Settings.UI.Appearance.cs:195-213`) do the same and persist `Keys.ThemeMode`. Three writers + one seed = 4 sites, all calling the same `SeedPalette` seam; nothing else writes `Tok.Use`.
- **Accent**: always the OS accent (`Tok.SetAccent(FluentApp.SystemAccentRamp())`) — no user accent key exists on main. Re-applied on `SystemColorsChanged` (701) and in `SetTheme` when mode==0 (207-208).
- **Zoom**: `AppOptions.Zoom` seeded from `Keys.ZoomLevel` before frame 1 (`Shell.Host.cs:274`); the auto-zoom loop (`Shell.UI.cs:295-319`) runs debounced (`FrameRules.ZoomAutoDebounceMs = 500`) and can re-zoom AFTER the first frame when mode is Auto/Dense (`ZoomAutoPolicy.Suggest`), i.e. a visible relayout ~500 ms after launch on a fresh profile whose stored zoom disagrees with the suggestion.
- **Sidebar band/mode**: seeded from the first viewport read inside `FrameRoot.Render` (239-248) — frame 1 is correct.
- **Rail**: `SeedFrameState` runs before the component exists.
- **Material/wash**: `MaterialState` starts `default` (neutral); the first page's claim lands in the content host's first effect (`Shell.UI.cs:925-930`) and the page's own grading later → the shell tint cross-fades in (`BrushTransitionMs = Design.Motion.Standard`, `Shell.Masthead.UI.cs:159`) rather than painting wrong, but frame 1 IS the neutral ground even when the restored route is a coloured page (by design: `Claim` holds the previous colour, there is none at boot).
- **Theme-dependent values captured at construction**: `Design.Colors.*` are all properties over `Tok.Theme` (`Design.cs:221-329`); but several components capture `Tok.Theme` into a `Signal<ThemeKind>` and re-publish via `UseEffect(DepKey.From((int)Tok.Theme))` (`Entities/Artist.Page.cs:144,193,291`, `Detail.UI.cs:514,587,684`, `Profile.Page.cs:102,163,238`) — a theme flip re-derives one frame later.
- **Setup wizard**: posted open one/two frames after mount; the shell behind is already themed; the plate itself uses tokens.
- **Store shot (`Screens/StoreShot.cs`)**: a developer presentation mode that forces stage mode/visualizer/gallery (`StoreShot.cs:113-125`) and hides badges (`Shell.UI.cs:1391`, `Diagnostics.UI.cs:152`).

## B. The frame tree (`Shell.UI.cs`)

Header comment at `Shell.UI.cs:11-29` is accurate. `FrameRoot.Render` (223-510) builds, bottom-up:

```
OverlayHost.Create( ZStack(
  tinted = Box{ ZStack, DropTarget=_fileDrop, [ MaterialLayer() (Shell.Masthead.UI.cs:139), column ] }
     column = Box{ Direction=1, Height=vp.Height, OnKeyDown=OnShellKey,
                   [ 14 zero-size Chord boxes (450-463),
                     ChromeRow() with Visible=s_chromeMounted (469),
                     ContentRegion(vp) (470 → 512-650),
                     PlayerBarDock() with Visible=s_chromeMounted (471; Shell.PlayerBar.UI.cs:52) ] }
  Flow.Show(Ui.ImmersiveLyrics, Box{ Enter=Stage.EnterTerminal, Exit=Stage.ExitTerminal, [Stage.View()] }) (488-493)
  Video.PipLayer() (497), Video.FullscreenLayer() (498)
  Overlays() (499; Shell.Overlays.UI.cs:48)
  FileDropLayer() (500 → 849-864)
  CommandPalette() (501; Shell.Palette.cs:385)
  CoverScrim (502 → 870-883)
  DragPreviewLayer.Of(Drag.Preview) (503)
  Diagnostics.FpsOverlay() (504; Diagnostics.UI.cs:146) ) )
wrapped in Ctx.Provide(HomeLayout.Slot) and Ctx.Provide(ShellMaterial.Slot, MaterialState) (508-509)
```

`ContentRegion` (512-650) = ZStack[
- row (MorphId `shell.content-row`): sidebar column (519-540: `Width = FrameRules.SidebarPaneWidth(DragPeek, Width, PresentedWidth)`, `Animate = SidebarPaneAnim` (146-148: `MotionTok.PaneOpen/PaneClose`, `Easing.FluentPane`), inner `IsolateLayout` box with `Opacity=_sidebarFade`, `Flow.Show(Sidebar.Mode != Minimal, Sidebar.Pane())`) · content card (541-557: `Design.Colors.FileArea` fill, `Design.Size.ContentPaneCorners`, `ContentCardAnim` (150-152, 300 ms `PaneEase`), `Embed.Comp(ContentHost)`, `ContentRegionStroke()` 671-682 left+top 1px `Tok.StrokeCardDefault`) · rail gap (559-563 `FrameRules.RailGapWidth`) · rail reservation (565-577 `FrameRules.RailReservedWidth`, `Design.Colors.FileArea` underlay with `RailBandCorners`)
- sidebar seam strip (581-591: `Splitter.Create(Sidebar.Seam, Sidebar.CommitSeam, SidebarSeamOptions)` with parts from `BuildSeamParts` 715-725: focusable slider, dbl-click toggles pane, right-click = `SeamPaneMenu` 730-735 → `SidebarMenuModel.Pane(Layout, State, Density, Editing, …)`)
- rail overlay (593-621: `Rail.Frame()`, floating backing `Design.Colors.FloatingChrome` when `FrameRules.RailFloats`)
- rail seam (625-635: `Splitter.Create(Ui.RailDragWidth, CommitRailDrag, RailSeamOptions, collapsed: Ui.RailOpen)`; `CommitRailDrag` 770-776 persists `Keys.ShellRailWidth`)
- `NarrowDrawer` (636; 1422-1563)
] with `Visible = s_chromeMounted` (646), `OnRealized/OnBoundsChanged → PublishScrimClip` (647-648: `scene.SpotlightScrimClip`).

`s_chromeMounted` (652-653) = `FrameRules.ChromeMounted(Video.PlacementCore.Resolve(Video.State.Surface), Ui.ImmersiveLyrics)` (`Shell.cs:1906-1907`): fullscreen video OR the fullscreen stage collapses chrome row + content region + player dock via bound `Visible` (not unmount).

Effects in `FrameRoot.Render` (each is a state→presentation decision site):
- window band hysteresis (251-258) → `Sidebar.Band`, closes overlay when the band loses it.
- pane toggle enabled (260) ← `Sidebar.Editing`.
- the ONE sidebar presentation effect (263-281): drag → `SidebarResizeRules.Track`; else `SidebarPaneModeRules.Resolve/PresentedWidth`.
- `FgMotion.SetLayoutTransitionsSuppressed(AppResize, dragging)` (282).
- rail drag cancel (285-290); `Session.CaptureShell(RailOpen, Mode)` (292).
- auto-zoom (295-311) + zoom persistence (313-319): `Keys.ZoomMode`, `Keys.ZoomLevel`.
- process-lifetime subscriptions (322-349): `FluentApp.AppNavigationCommand` (mouse back/forward), `ActivationRedirected`, `SystemColorsChanged`, `hooks.ZoomWheel`, `Diagnostics.SidebarPaneFrame`, `Notify.HostInstall`, `DrainActivation`, `Playback.Os.Activate(FluentApp.WindowHandle, …)` (SMTC).
- `Auth` fold (352-353) ← `Spotify.Status/Fault` + stored credential (`Shell.cs:1566 FoldAuth`).
- `_pageStageHosts`, `Ui.RailFits` (354-360), video host capability (362-372).
- rail↔docked-video coupling (376-397) → `Rail.VideoCoupling.*` rules, posted `CouplingWrites` (659-669).
- chrome row allocation (400-419): `Chrome.SeedTabExtent`, `Chrome.Resolve(w, extent, old, FrameRules.ChipFor(Auth))`, `ReissueSearchFocus`.
- material mirror (422-429): `MaterialState` → `Material` (`TintOwner`).
- AT announce (432-441).

Shell chords (177-190): Ctrl+T new tab, Ctrl+K palette, Ctrl+F search focus, Alt+←/→, F11 (`ToggleVideoFullscreen` 781-790 → `FrameRules.F11`), Ctrl+±/0 zoom. `OnShellKey` (794-818): Escape precedence (`FrameRules.Escape` 1916), Space toggles play when no text editor (`FocusedIsTextEditor` 821-827).

## C. Navigation model (`Shell.cs`, `Shell.Host.cs`, `Shell.Chrome.cs`)

- **Route table**: `RouteKind` enum (`Shell.cs:51-85`, 33 kinds incl. NotFound); `RouteRow` (128-142: Key, IsPrefix, TitleLocKey, Glyph, DeveloperOnly, **ClaimsMaterial**, KeyedByArg, PlaceByArg); `s_routes` (146-205) is THE table; `s_pages` factories (209) filled by `SetPage` (222); `PageFor` (230-236) with lazy group install (`InstallLazyGroupFor` 261-293: Home/Album/Playlist/Profile groups — but App.cs installs Home/Album/Playlist eagerly anyway, see App.cs:203-221).
- `ClaimsMaterial(route)` (332): which routes publish their own shell tint/wash (Home, Liked, Local, Recents, Album, Playlist, Artist, Show, Prerelease, HomeSection, BrowseSection, Episode, User). The content host claims NEUTRAL for the complement (`Shell.UI.cs:925-930`).
- `Dest(route, liveTitle)` (342-356): title+glyph for tab strip, history flyout, sidebar pins, breadcrumb.
- Key codec `Parse`/`NameOf`/`ArgOf`/`For` (379-463): the persisted `(name,arg)` pair is the route identity used by history.json, session.json, `workspace.tabs.pinned`, sidebar pin ids, `wavee://`.
- **Nav struct** (497-513): `Current`, `Back`, `Forward` lists capped 200 (`MaxBackStack` 516); `Go` (524), `BackStep` (534), `ForwardStep` (545), `Restore` (560, tab switch), `SameSlot` (564).
- **Deep links** (585-774): `DeepLinkKind {Open, Play, Resume, Pause, Report, Quit, Diag}`; `ApplyDeepLink` (`Shell.Host.cs:761-806`) is the one door (OS protocol, second launch, toast, jump list, play-a-link dialog); `Tray.WakeFor` decides window show.
- **Drill trail / origins** (776-968): `Origins` LRU 32 (788-853), `Trail` (864), `TryMasthead` (944-957: only Browse/BrowseCategory/BrowseSection/HomeSection/Concert family get the overlaid masthead band).
- **Page swap policy** (970-1056): `NavTransitionKind {Forward, Back, Neutral}`; `SlotKey` (981: tab + name + arg-if-KeyedByArg); `KeepAliveSlots = 3` (987); `PageScrollScope` context (996) / `ScrollScopeOf(tab)` (999); **`PageMotionStyle()` (1007-1008) reads `Prefs.Appearance.PageMotionStyle` → `Design.PageMotionStyle {Fluent, Spatial, WinUi, Classic, None}` (`Design.cs:1739`), default Classic (`Design.cs:1786`), DEVELOPER-ONLY row in Settings (`Settings.UI.Appearance.cs:125`)**; `RecipeFor` (1012-1017) → `Design.Nav.RecipeFor(style, motion, relation)` (`Design.cs:1868`); `RecipeForVideoSafe` for module pages (1019, 1027); `SurfaceOf` (1030: Module/Detail/TopLevel), `RelationOf` (1040).
- **Shell.Ui rail state** (1067-1138): `RailOpen`, `Mode` (`RailMode {Lyrics, Queue, NowPlaying, Friends, Video}` 1065), `RailWidth`, `RailDragWidth`, `DockedVideoHeight(+Pinned)`, `RailBodyHeight`, `ActiveStagePlayable`, `RailFits`, `ImmersiveLyrics`, `Toggle(mode)` (1127). Clamps 1144-1189 (`RailMinW 200 / RailMaxW 500 / RailDefaultW 340`, `MinContentW 480`).
- **Player bar tiers** (1195-1324): `PlayerBarTier {Minimal, Compact, Medium, Comfortable, Wide, Full}` at 440/760/900/1100/1240 with 24-DIP narrow hysteresis; `PlayerBarLayout.ForTier` (1271-1323) is the complete structural policy (what shows per tier, button boxes, LeftW, ArtSize…).
- **Device picker rows** (1329-1446): pure POD composition `DevicePickerRows(...)`.
- `LinkFor(track, LinkSlot)` (1477-1500): where title/artist/art navigate; module playables via `Modules.LinkRouteFor`.
- **Tint ownership** (1502-1543): `TintOwner`, `Claim`, `AcceptsRefresh`, `TintClaimantOf`.
- **Auth fold** (1549-1578): `AuthState {Live, Connecting, Offline, SignInRequired}`.
- Tips (1587-1669), History rules (1683-1843), **FrameRules** (1852-2040): all the frame's pure decisions (pane width, seam widths, rail gap/reservation/float, `ChromeMounted`, `Escape`, `F11`, `ReissueSearchFocus`, history flyout shape, `PinIdFor`, `BodyFor`, `ClearsStagePlayable`, `ChipFor`, `ShowsOfflineStrip`, auto-zoom loop).
- Masthead publications (2053-2100), Omnibar model (2108-2338: `ItemKind`, `Suggestions`, `Source` seam filled by `Home.InstallPages`, `FromHistory` fallback, `Query` generation state machine with `State {Idle, Pending, Results, Empty, Failed}`).

**Shell.Host.cs nav verbs** (828-1001): `GoTo` (History always opens its own tab, 831), `Commit` (841-856: `Redirected`, `Go`, `Motion` BEFORE `Current`, `Origins.Write`, `RouteCommitted` = `ToolTip.CloseOpen` (863), `History.Store.Add`, `SyncActiveTab`, `SyncOmnibar`, `Session.CaptureNav`), `ReplaceCurrent` (882), `GoBack/GoForward` (896-924), `RestoreTo` (927: tab switch, Neutral), workspace ops `Apply` (959-974), `ActivateTab/OpenTab/CloseTab/SetTabPinned/CloseOther/CloseAllUnpinned/CloseTabsToRight` (976-993), `PersistPinsIfChanged` (996-1001 → `Keys.WorkspacePinnedTabs`), `RestoreNav` (1005-1033). `Redirect` seam (868, installed by `Playlist.InstallPages`: Your Episodes playlist → Podcasts library).

**Tab workspace** (`Shell.Chrome.cs:515-807` `TabWorkspace`): ids minted per process, pinned prefix block, `LastSelectedPinnedId`, `PinnedRevision`; `TabLabelStaging` (815-830: the strip labels from `Shell.Shown`, which lags `Current` by the exit leg `Design.Nav.ExitDurationMs`); `WorkspaceTabs` codec (843-889).

**Content host** (`Shell.UI.cs:885-1036`): `s_keepAlive` (887-891: `MaxEntries 3`, `TransitionFor = PageTransition`, `SuppressLayoutTransitionsOnActivation`); `Shown` signal (899) landed by `UseTimeout(s_landShown, TabLabelStaging.DelayMs(...))` (916-921); neutral material claim (925-930); `RouteNoted` + `ClearsStagePlayable` (932-937); tree = ZStack[ clip box with `Flow.KeepAlive(Current, SlotKey, PageBody, s_keepAlive)` (949), `Masthead()` overlay (954, `Shell.Masthead.UI.cs:36`), `Flow.Show(ShowsOfflineStrip(Auth), OfflineLane())` (956) ] with bottom padding `Video.FloatingSurfaceReserve` (911, 942). `PageTransition` (965-978): Home→Home same tab = null (facet switch), module pages = video-safe translate-only pair, else `RecipeFor(from, to, motion)`. `PageBody` (980-992) → `FrameRules.BodyFor(route, hasPage, Keys.DeveloperMode)` → `Page` (`Ctx.Provide(PageScrollScope, …, PageBox("page:"+name, factory(route)))`), `Empty` (`PageBox`), `NotFound` (`NotFoundPage` 1002-1019: glyph + `Design.Type.PageHero` + `Button.Standard` Go home). `OfflineLane` (1023-1036): `Controls.OfflineStrip(onRetry: Spotify.Login)` floated max 720 wide.

How chrome changes per route: (a) material tint/wash via `ShellMaterial.Publish` from pages vs neutral claim (above); (b) `Masthead()` band only for `TryMasthead` families; (c) trailing Pin button follows `Current` + `Sidebar.PinsVersion` (`ChromeContentVersion` 1086-1099); (d) tab label/glyph from `Dest(Shown)`; (e) `Ui.ActiveStagePlayable` cleared by non-module routes; (f) `NarrowDrawer` closes the overlay on any leaf navigation (1462-1466); (g) History route forces its own tab.

## D. Window chrome / title bar (`Shell.UI.cs:1038-1413`, `Shell.Chrome.cs`)

- ONE 48-DIP engine `TitleBar` in merged mode (`ChromeRow` 1069-1084): `IconGlyph=""`, `ShowBackButton=false`, `ShowCaptionButtons=true`, `ShowPaneToggle=true` (`OnPaneToggle = Sidebar.TogglePane`, enabled signal `s_paneToggleEnabled`, tip `PaneToggleTip` 1061-1064), `ShowRailBaseline=false`, `Tabs = TabsIsland`, `TabsElasticLane = true`, `Trailing = TrailingIsland`, `CaptionLeading = CaptionLeadingIsland`, `CenterContent = CenterIsland(bar.CenterAvail)` (1082, `Shell.Masthead.UI.cs:249`), `ContentVersion = ChromeContentVersion` (memo key).
- **Allocator** `Chrome` record (`Shell.Chrome.cs:201-513`): `ShowName/ShowActions/ShowForward/ShowBack/ShowNewTab/ShowTrailing/Chip/SearchMode{Field,Icon}/SearchWidth/LeadClusterW`; constants in `Layout` (26-172): `ChromeNameEnterW 1360`, `ChromeActionsEnterW 1200`, `ChromeForwardEnterW 520` (+40 promotion hysteresis), `ChromeSearchMinW 280 / MaxW 420 / ratio 0.28`, `ChromeTabMinW 110 / MaxW 200`, fixed budget items (60 lead, 44 nav button, 32 add slot, 36 overflow, 32/90/84/100 chip forms, 90 name, 48 drag strip, 138 caption cluster, 44 theme toggle, 8 gutters, 10 quantum). `StageFor` (390-443) is the promotion ladder; `FixedBudget` (358-370) always charges the theme toggle + caption cluster.
- **Tabs island** (1109-1129): optional `NavHistoryButton(forward:false/true)` (1341-1380: `IconButton` 40×44, right-click/touch-hold opens the history `MenuFlyout` of up to 8 rows + "View all history", `FrameRules.HistoryMenu`), then `TabStrip` (1131-1157: `TabStripAppearance.Text`, `OverflowMode.Scroll`, 13 pt, hover-only "+" with reserved slot, `IndicatorFill = Tok.AccentDefault`, `SelectedIndex = SelectedTab`, `ScrollMetricsChanged → s_tabExtent`). Items (1159-1190): `TabViewItem{Header, Icon, IsClosable (not pinned && >1), IsPinned, ContextMenu=TabMenu, Drag (pin payload), DropTarget (spring-load activate; editable playlist = deposit target)}`. `TabMenu` (1227-1249): Pin/Unpin tab · Close · Close others · Close to right · Close all unpinned · [Pin destination to sidebar].
- **Caption-leading island** (1273-1286): sun/moon theme toggle (`Icons.Sun`/`Icons.Moon` by `Theme.Dark`, tooltip Light/Dark theme) + `SearchFlyoutButton()` when `SearchMode == Icon`.
- **Trailing island** (1288-1306): `AuthChip(l.Chip)` (1314-1325: `Profile` → `ProfileChip()` (`Shell.Overlays.UI.cs:53`), `Connecting` → caption, `Reconnect`/`SignIn` → `Button.Accent` → `Spotify.Login`); when `ActionsInRow`: `BellButton` (1383-1412: unread `InfoBadge.Count`, 0 under Store shot; opens `OpenNotificationPanel` 1573-1590), friends button (`Ui.Toggle(RailMode.Friends)`), `PinButton` (1329-1338: direct Pin/Unpin for the current destination or an invisible 44-DIP placeholder).
- States: width tiers via the allocator (hysteretic); auth forms (4); search Field vs Icon; developer-only routes; `s_chromeMounted` collapses the whole row under fullscreen video / stage.
- Not on main: breadcrumbs in the title bar, Zune wordmark, nav-style-dependent title bar (all PR 205).

## E. Narrow drawer (`Shell.UI.cs:1415-1563`)
`NarrowDrawer` mounts only when `SidebarPaneModeRules.HasOverlay(band)` (`NarrowDrawerMount.ShouldMount` 1503-1506); chains `KeyPreview` for Escape (1435-1460); closes on leaf navigation (1462-1466). `DrawerScrim` (1508-1530): 0x33 black (or 0.2 when pinned while editing), `Design.Motion.Fast`, 0 under `Design.Reduced`. `DrawerPane` (1532-1563): translate-X slide `MotionTok.PaneOpen/PaneClose`, `Easing.FluentPane`, width `SidebarPaneModeRules.OverlayWidth`, `Acrylic = Tok.AcrylicFlyout`, 1px `Tok.StrokeCardDefault`, right corners `Radii.Card`, `Elevation.Flyout`, hosts `Sidebar.DrawerPane()`.

## F. Notification panel (`Shell.UI.cs:1565-2064`)
`OpenNotificationPanel` (1573-1590): overlay popup `BottomEdgeAlignedRight`, `PopupChrome.Popup`, light dismiss; `Notify.PanelOpened`. `NotificationPanelView` (1595-1646): header (`PanelHeader` 1668-1688: title, Mark all read, Clear), `FilterPills` (1690-1716: All/Updates/Spotify/New/Activity — accent pill when on), `PendingSyncRow` (1650-1666), body `ScrollEl` (`Notify.FeedMaxHeight`, `Notify.PanelWidth`) or `PanelEmpty`. Row kinds by `NotifyCategory` (1718-1724): `UpdateRow` (1761-1818: glyph chip by `UpdateRowGlyph`, progress bar `Notify.UpdateProgress`, verb pills), `SocialRow` (1847-1868), `ReleaseCard` (1728-1735: album context menu via `Menus.Container`), `ActivityCard` (1934-1976: expandable, Undo). `NotificationCard` frame (1738-1753): 56 min, r8, `Design.Colors.RowHover/RowPressed`, `Enter/Exit` dy 6/-4, `LayoutTransition.Slide`. All colours from `Tok.*` / `Design.Colors.*` inline; no theme switch beyond tokens.

## G. Theme pipeline summary (main)
| Concern | Reader/writer sites |
|---|---|
| `Keys.ThemeMode` ("theme.mode", `Platform.cs:111`, 0 System/1 Light/2 Dark) | read: `Shell.Host.cs:676` (SeedTheme), `Shell.UI.cs:702` (OnSystemColorsChanged), `Settings.UI.Appearance.cs:97`; written: `Shell.Palette.cs:271` (ToggleTheme), `Settings.UI.Appearance.cs:210` (SetTheme) |
| Palette seam `Shell.SeedPalette` (`Shell.Host.cs:113`) | installed `Design.cs:206`; invoked `Shell.Host.cs:683`, `Shell.UI.cs:705`, `Shell.Palette.cs:270`, `Settings.UI.Appearance.cs:204` |
| Engine re-theme request `ThemeControl.Request` | `Shell.UI.cs:227/707`, `Shell.Palette.cs:413`, `Shell.Overlays.UI.cs:403`, `Settings.UI.cs:114` |
| Accent | OS only: `Tok.SetAccent(FluentApp.SystemAccentRamp())` at `Shell.Host.cs:685-686`, `Shell.UI.cs:701`, `Settings.UI.Appearance.cs:207-208` |
| Direct light/dark branches in app code | `Tok.Theme == ThemeKind.*` 27 lines / 8 files (`Design.cs` 20+, `Controls.cs:507,525,529,1015,1032`, `Concert.UI.cs:134,251`, `Rail.UI.cs:344`, `Deck.UI.cs:605`, `Shell.Masthead.UI.cs:151`, `Detail.UI.cs:735`, `Shell.Palette.cs:269`, `Shell.UI.cs:704`); `Theme.Dark` 5 lines (`Shell.UI.cs:1276`, `Shell.Overlays.UI.cs:394,482`, `Shell.PlayerBar.UI.cs:643`); `Ink.IsDark`/`StageInk.IsDark` 10 lines (stage/visualizer/verse/lyrics: `Lyrics.UI.cs:152,154`, `Stage.Options.UI.cs:137`, `Stage.UI.cs:121,124,483`, `Verse.UI.cs:199`, `Visualizer.Gallery.UI.cs:281`, `Visualizer.Zune.UI.cs:155,641`, `Visualizer.Palette.cs:71`); `Signal<ThemeKind>` republish pattern in `Artist.Page.cs`, `Detail.UI.cs`, `Profile.Page.cs`; `Design.Palette.DataDotInk(…, Tok.Theme)` in `Track.Drawer.cs:411`, `Track.UI.Bound.cs:699`, `Track.UI.cs:760`. |
| Shell material (page tint / Home wash) | `ShellMaterialState(Owner, Tint, HomeWash)` (`Design.cs:2173`), `Shell.MaterialState` (`Shell.UI.cs:114`), `ShellMaterial.Publish`, painted by `MaterialLayerView` (`Shell.Masthead.UI.cs:141-213`: tint box `BrushTransitionMs = Design.Motion.Standard`, three radial washes `Design.Wash.Hero/Weekly/Mix` with `HeroAlpha/ShelfAlpha(light)`), mirrored to `Shell.Material: TintOwner` (`Shell.UI.cs:422-429`). Gated app-wide by **`Keys.ColorWashesEnabled`** via `Prefs.Appearance.ColorWashes()` (`Prefs.cs:82`): readers `Artist.Page.cs:295`, `Detail.UI.cs:669`, `Profile.Page.cs:242`, `Recents.Page.cs:1162,1205` (+ Home, see Home map). |
| Reduced motion | `Design.Reduced` 73 lines / 35 files; `FgMotion.ReducedMotion` 2. |
| Developer-only presentation rows | `Platform.Developer.Enabled` 5 sites; `Catalog.RowVisible` in Settings (page motion, NPV presentation); FPS overlay; developer routes (`RouteRow.DeveloperOnly`). |
| Store shot presentation mode | `StoreShot.Presenting` 5 sites. |

## H. Logic vs presentation in the shell core
- The split is explicit and mostly clean: `Shell.cs` + `Shell.Chrome.cs` are pure (route table, nav reducer, allocator, tiers, device-picker rows, `FrameRules`, tint fold, auth fold, history rules, omnibar state machine); `Shell.Host.cs` is imperative shell (window, documents, verbs, marshallers); `Shell.UI.cs` lays out + binds. Every geometry/gating value in `Shell.UI.cs` is a call into `FrameRules`/`Chrome`/`SidebarPaneModeRules`/`SidebarResizeRules`/`Rail.VideoCoupling`/`Notify` rules.
- Leaks of logic into the view file: `FrameRoot.Render` owns the auto-zoom control-loop state (`_lastAutoZoom`, `_autoZoomSeeded`, 217-218, 301-311) and persists `Keys.ZoomMode/ZoomLevel` (310, 318); `CommitRailDrag` persists `Keys.ShellRailWidth` (775); `OnSystemColorsChanged` (699-708) decides theme policy in the UI file; `BuildTabItems` composes drag payloads and drop targets (1159-1225); `TabMenu`/`PinRow`/`PinDestination` (1227-1268) mutate the sidebar pin store from the chrome; notification panel verbs (`RunUpdateRowVerb`, `OpenSocial`, `OpenRelease`, `UndoActivityAsync` 1832-1985) are behaviour in the view file; `NotFoundPage` logs (1006).
- Logic file building visuals: `Shell.Host.cs` sets `AppOptions` (MicaAlt, Title, min size, zoom) 249-285 — acceptable host config; `Shell.cs` `PlayerBarLayout.ForTier` hard-codes metrics (button boxes, art size, gaps) 1279-1322 — a style table living in CORE; `Shell.cs` route table carries glyphs (`Icons.*`) 148-204.
- Hard-coded visual constants in `Shell.UI.cs`: pane easing (141-143), `StrokeOverhang`, `RailBandCorners`, drawer scrim alphas (1525), file-drop pill (855-862), notification pill geometry (1703-1716, 2038-2064).
