# Wavee Platform presentation layer: full map (main branch)

> Agent summary: (1) Theme: one key `theme.mode` (`Keys.ThemeMode`, `Platform.cs:111`, 0 System/1 Light/2 Dark); 5 direct sites, ~45 derived `Tok.Theme` reads in Design.cs, ~40 elsewhere, 4 engine `Theme.Dark` reads. (2) Accent: **no accent key**; OS ramp or artwork (`Design.Palette.ChromeAccent`, `AccentLadder`, `AccentCtx`). (3) Colour washes: `appearance.colorWashes.enabled` read only through `Prefs.Appearance.ColorWashes()` at 5 sites → the `Disabled` props of the four cover-leaf components in Design.cs. (4) Row density `detail.rowdensity` 11 sites; sidebar density `sidebar.pane.density` 2 key sites + ~20 signal sites. (5) Track row style `detail.rowstyle` 8 key sites; derived `classic` bool fans out to ~400 lines (Track.UI 63, Track.UI.Bound 65, Track.Rules 29, Track.Table 27, Track.Table.Chrome 20, Queue.UI 28, Artist.UI.Chart 44, Artist.UI 8, Artist.Page 3). (6) Hide track artwork 15 sites. (7) Sidebar layout `sidebar.layout.id` ~70 `Sidebar.Layout`/`SidebarLayoutId` sites. (8) 14 `stage.*` keys behind `Prefs.Stage`, ~75 call sites. (9) Zoom 13 sites; `appearance.pageMotion.style` 6 sites (developer-only row); reduced motion OS-only (`Design.Reduced`, 73 reads). (10) **No user setting for materials/Mica/transparency or accent colour.** `Platform.SettingsChanged` has 4 subscribers; `Prefs.BumpAll()` has 0 callers.

Root: `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\` (paths below are relative to it unless absolute).

## 1. Design.cs (`Platform\Design.cs`, 2613 lines, read in full)

Design.cs **never reads `Platform.Settings`**. Its only inputs are:
- `Tok.Theme`, which is seeded from `Keys.ThemeMode` or the OS;
- `FgMotion.ReducedMotion` (OS);
- parameters callers pass in, such as `PageMotionStyle style` and the `Disabled` / `Apply` flags.

Rule 1 (lines 26-28): every colour member is a `get =>` property, so it is re-read live on every access.

### Token groups

**§1 Geometry, `Design.Size` (62-119)**
- ControlH 32, NavItemH 44, TrackRowH 56, PlayerBarH 72
- RailCard 180, NavPaneW 240, NavCompactW 56
- ArtThumb 40 / 64 / 48; RailAlbum 280, RailPlaylist 240
- Thumb ladder 32 / 40 / 48 / 56 / 64 (77)
- SectionGap 32 / 40 (82), PageMaxW 1600 (87), DesignW / DesignH 1600 / 900 (94)
- ShelfCardMin / Max 148 / 188, GridGap 12 (98); WideTile 330 / 440, 16:9 (103)
- Fade 16 / 24 / 36 (107); FacePile sizes (111-114)
- `ContentPaneCorners` static readonly (118)

**Other geometry**
- `Design.Dock` (124-129): BarH 72, Reserve 72.
- Focus insets: `FocusInsetBordered` = 2 (146), `FocusInsetRow` = 1 (151).
- There are **no radii, spacing or elevation tokens in Design.cs**. Those come from the engine's `Radii.*`, `Spacing.*`, `Elevation.*`, `MotionTok.*` and `Expressive.*`.

**§2 Accent budget, `Design.Accent` (183-195)**
- `Action` = `Tok.AccentDefault` (187)
- `Selection` = `Tok.AccentDefault` (190)
- `Decor` = `Tok.AccentTextPrimary` (194)

**§3 Colours, `Design.Colors` (221-367)**
- `ActiveShell` picks LightShell / DarkShell by `Tok.Theme` (223).
- Toolbar / Sidebar / PlayerBar / FileArea / Content / ContentAlt (228-233).
- `PremiumText` branches on theme (237-238).
- `FloatingChrome` / `FloatingPane` (243, 248).
- `ShellGround` (272-273) and `ShellGroundFor(set, theme)` (277-279): light = Darken(#F3 → #ED), dark = FillSolidBase.
- `ContentSurface` / `ContentSurfaceFor` (286-293), `ContentLayer` / `ContentLayerFor` (302-306).
- `RowZebra` branches on theme (318); RowHover / RowPressed (320-321); RowHoverZebra / RowPressedZebra (329-331).
- Selection ladder `SelectedRest` / `Hover` / `Pressed` (351-353).
- ChromeHover / ChromePressed / Badge (355-357).
- Tombstone note at 360-361: the palette picker was removed.

**Light/dark ground math constants (254-267)**
- `DarkGroundL` 0.125 and `DarkContentLift`
- `LightGroundDrop` 6/243
- `LightContentLayerA` 2/3

**§4 On-media ladder, `Design.OnMedia` (386-476), theme-invariant by contract**
- ScrimRest = `Tok.MediaScrim`. These are static readonly: ScrimHover 190a, ScrimPressed 220a, CoverScrim 110a, Stroke white@58.
- Ink tiers (414-418).
- Glass ramp 0.10 / 0.16 (428-432); GlassPlate 0.14 / 0.22 / 0.28 (443-447).
- BackdropDim, Spotlight (453-458).
- LightButton ramp (465-475).

**§5 Stage polarity, `StageArm(bool Dark)` record (498-619)**
- `For(theme)` (502).
- Veil / Floor / Ink tiers / Glass / Plate / Scrim / Stroke / ButtonFill / ButtonInk all branch on `Dark` (509-570).
- `ScrimBaseA` 0.46 (575); `AccentFrom` (589-590); SkeletonBar (600).
- Stage card acrylic, `Card`: dark = `AcrylicSpec.InAppBase`, light = an F3 recipe (609-610). This is the **only material/acrylic token in Design.cs**.
- Plate 0.045 / 0.075 (614-618).
- `StageInk` live facade (624-672): `Live => Arm(Tok.Theme)` (630), **`IsDark`** (634), all passthroughs (636-658), `Accent(url)` (663-666), `ArtStandIn` (671).

**§6 Cover palette → colour, `Design.Palette` (681-1027)**
- `NeutralS` 0.08 (689), `TextContrast` 4.5 (694), `ToColor` (696).
- `Lift` (705), `Vivid` (721).
- `Hairline(seed)` uses `Tok.Theme` (731-733); `HairlineHover` (737-739).
- Pure 22-iteration bisection that branches on theme (745-770).
- `TextInk` (774-783).
- `ChromeFromPayload` (792-798), `DarkFromPayload` (803-813).
- **`ChromeAccent(scheme)` (821-826): artwork accent, falling back to `Tok.AccentDefault`.**
- `Accent(scheme)` hue picker (842-854); BackgroundDark / TintedDark (859-861).
- Page tone:
  - constants: ChromaFloor 0.12, Dark L 0.15 / S ≤ 0.30, Light L 0.94 / S ≤ 0.16 (882-897)
  - neutrals #151515 / #F5F5F5 (906-908)
  - `PageTone(scheme, theme)` (920), `PageToneFromHue` branches on theme (925-934)
- HSL helpers (941-978).
- `DataDotInk(argb, theme)`: dark is a passthrough (1006-1013).
- `Neutral` scheme (1019), `BarSurface` (1026).

**§7 Grading halves and placeholders (1029-1165)**
- `SchemeFor(url / StringId)` follows the theme (1060-1065).
- `ChromeSchemeFor` deliberately takes the **opposite** theme (1069-1074).
- `ArtworkPlaceholderDark` / `Light` #2A / #F2 (1087-1089); `TintStrength` 0.55; `ShimmerMinEdge` 80 (1095-1099).
- `ArtworkPlaceholder` branches on theme (1102-1103).
- `PlaceholderFor(url)` uses `Tok.Theme` (1107); `PlaceholderFor(url, light)` (1113-1120).
- `WatchedPlaceholder` (1134-1165) is **cached** in `s_watchedPlaceholders` (cap 4096); the theme is read when the thunk is evaluated.

**§8 Type ramp, `Design.Type` (1190-1425)**
- DisplayFace "Segoe UI Variable Display" (1194).
- TrackTitle / CardTitle / TrackMeta (1197-1205); EyebrowTracking 30 and Eyebrow (1212, 1227).
- MicroMeta 11/15, DenseMeta 13/18, DenseTitle, SheetTitle 16/22 (1232-1241).
- RailHeader / ModuleHeader (1244-1265), `Paired` (1270).
- PageHero, DetailHero, SurfaceDisplay, HeroTitle (1296-1330).
- ArtistDisplay / Title / CompactTitle at weight 700 (1335-1350); NowPlayingTitle (1353).
- PickQuote / FoldTitle (1360-1371); PivotLabel 19/25/350 (1377); NpvLyric (1383); StatHero (1401).
- `FacetTitleStyle` and `FacetCompactStyle` are properties (1436, 1461).

**§9 Motion, `Design.Motion` (1482-1530)**
- Scale tiers: Subtle 1.02 / 0.98, Standard 1.04 / 0.96, Emphatic 1.07 / 0.92.
- Durations: Faster 83, Fast 167, Standard 250, Slow 333; StaggerMs 40; MastheadStaggerMs 45.
- `Reveal` (1536-1550).
- **`Design.Reduced => FgMotion.ReducedMotion` (1554).**
- `Entrance` with `StaggerCap` 8 (1574-1600); `DelayMs` uses Reduced (1594-1595).
- `ScaleTier.Hover` / `Press` collapse to 1 under Reduced (1620-1627).
- `HoverMotionGate` (1641); `FrameTime` (1673-1683); `Cadence` ClockHz 60 / EnergySaverMaxFps 30 (1697-1708).

**§10 Page-nav motion (1716-1950)**
- `PageMotionStyle` enum {Fluent, Spatial, WinUi, Classic, None} (1739); count 5 (1743).
- `Nav.DefaultStyle = Classic` (1786); Exit 90 / EnterDelay 90 / Enter 210 ms (1790-1796).
- Geometry constants (1805-1814).
- `RecipeFor(style, motion, relation)` is the pure switch (1868-1897).
- Video-safe pair (1922-1949).

**§11-15**
- `RevealRamp` (1959), `ImageDecodeScale` (1995), `MorphKeys` (2049).
- `PageAccent` / **`AccentCtx.Slot`**, the page-scoped accent context (2070-2082).
- `Wash` placements (2091-2146): HeroAlpha L 0.055 / D 0.10, ShelfAlpha L 0.05 / D 0.085, `HeroAlpha(light)` / `ShelfAlpha(light)` (2105-2110), `NeutralGround` (2135).

**§16 Shell material**
- `ShellWashPlacement`, `WashLayer`, `HomeWash`, `ShellMaterialState` (2157-2173).
- `ShellMaterial.Slot` / `Publish` (2182-2213); `TintOwnership` (2228-2268).

**§17 The four cover-leaf components, each with a theme branch**
- `CoverPageTonePlane`: PlaneAlpha D 0.20 / L 0.30 (2323), branches at 2352, 2363, 2366, 2379; `Disabled` prop at 2338.
- `VeilSettlement` (2412).
- `CoverArtistBlendWash`: `light` at 2447; alphas 0.20 / 0.30 and 0.06 / 0.08 (2460-2461); `Disabled` at 2443.
- `CoverKeyedVeil` (2499).
- `CoverShellTintBinder`: TintAlpha L 0.05 / D 0.14 (2561); theme arms 2581-2588; `Tok.Theme` included in the effect key (2605); `definite = p.Disabled || !p.Apply` (2575).

### How light/dark is resolved
- `Tok.Theme` is set by `Shell.SeedPalette` = `Design.SeedPalette` → `Tok.Use(Tok.NeutralPalette, kind)` (203). It is installed by `Design.Install` (206), which is called from `Shell.UI.cs:61`.
- The kind comes from `Keys.ThemeMode`:
  - boot: `Shell.Host.cs:674-690` (`SeedTheme`, called at `Shell.Host.cs:230`, before `FluentAppHarness.Run`);
  - live OS follow while in System mode: `Shell.UI.cs:699-708`;
  - the Settings selector: `Settings.UI.Appearance.cs:195-213`;
  - the toggle: `Shell.Palette.cs:267-273`.
- There is no `Design.IsLight` or `Design.Dark`. The only names are `StageInk.IsDark` (634) and `StageArm.Dark` (498).

### How accent is resolved
- System accent: `Tok.SetAccent(SystemAccentRamp | SystemAccent)` at boot (`Shell.Host.cs:685-686`); on every system-colour change (`Shell.UI.cs:701`, unconditional); and on Settings → System only (`Settings.UI.Appearance.cs:205-209`).
- There is no custom-accent key.
- Artwork accent:
  - `Palette.ChromeAccent` (821) and `ChromeFromPayload` (792);
  - `AccentLadder.Resolve`, Graded → Payload → Held → Default (`AccentLadder.cs:30-36`);
  - `AccentHold.Last` (`AccentLadder.cs:40-50`);
  - `StageInk.Accent` (663);
  - per page through `Design.AccentCtx.Slot` (2081).

### Density
Design.cs has no density token. Row ladders live in `Entities\Track.Rules.cs` (`TableRules.RowHeightFor` / `ArtSizeFor(density, classic)`).

### Computed per read vs cached
- **Per read:** every `Colors.*`, `StageInk.*`, `Accent.*`, `Palette.Hairline` / `TextInk` (a 22-step bisection on every call, not cached), `SchemeFor`, `PlaceholderFor`, `FacetTitleStyle`.
- **Cached / static:** OnMedia literals (395-458), the Motion tiers, `Reveal`, `Wash` placements, `Palette.Neutral`, `PageToneNeutral*`, `BarSurface`, the artwork placeholder constants, and the `WatchedPlaceholder` prop cache (1148-1165).
- Per frame: `FrameTime` (1676).

### Every setting-dependent branch in Design.cs
- Theme (from `Keys.ThemeMode` / OS): 223, 238, 273, 287, 302, 318, 630 (StageInk), 732, 738, 775, 1061, 1065, 1071, 1103, 1107, 2352, 2363, 2366, 2379, 2447, 2581, 2588, 2605.
- Theme passed as a parameter: 277, 291, 306, 758, 768, 929-933, 1009.
- Reduced motion (OS): 1595, 1620, 1622.
- `PageMotionStyle` (from `Keys.PageMotionStyle`, via `Shell.cs:1008`): 1868-1896; `DefaultStyle` feeds the key's default (`Platform.cs:127`).
- ColorWashes (as `Disabled` / `Apply` props): 2337-2339, 2443, 2575.
- `HeroOnly` (its setting is gone): 2343, 2374-2391.

## 2. Shared controls (Controls.*)

- **Controls.cs**
  - Seams for Library / RouteForUri / PreSave (73-86); `ArtUrl` (92-118).
  - `Shimmer` (149), `Artwork` (207), `ArtworkFill` / `Cover` / `CoverFill` (290-334), `IconPlate` (340), `TileCardStyle` (356), `Mosaic` (392).
  - `AccentRule` / `AccentHeader` / `SectionHeader` (433-479).
  - `SectionBand` / `HomeHeroBackdrop`, with a theme branch at **507** (504-512).
  - `ArtistHeroVeil`, theme branches at **525, 529** (522-541).
  - `SearchHighlight` (561).
  - Equalizer `EqHost`, which reads **`Design.Reduced` at 710** (660-839).
  - `SaveButton` (856), `PreSaveButton` (895), `FollowButton` (933).
  - `FollowToggle`, theme at **1015, 1032**, passed into `ButtonRules.FollowStyle(a, light)`.
  - `FollowTextAction` (1038), `ExplicitBadge` (1063), `MoreButton` (1080), `ExpandChevron` (1089).
  - Swipe gate (1114); `SelectionBar` with acrylic flyout (1137-1191).
  - `Vacancy` with `VacancyScale.Page` / `Compact` (1212-1268); `OfflineStrip` (1272).
  - Dialogs `Confirm` / `Prompt` / `Sheet` (1309-1359); `Notify.Say` (1390-1409).
- **Controls.Art.cs**: playback seam (79-86); `CardCover` hover zoom (102); `MoreCorner` (136); `RowChip` (152); `NowPlayingOverlay` (186-299); `CardData` (324-440); `CardChromeRules` (454); `CoverShimmer` (495); `Chip` / `ChipRail` (567-601); `StatTile` (619); countdowns (660-681); `FlipTransition` / `FlipDigit` use `Design.Reduced` (696, 720); FacePile (777-857); RichText (868-1092).
- **Controls.Cta.cs**: `PrimaryButton` / `PlayButton` / `AccentIconButton` (107-155); `OnMediaPalette` (162); `AccentToggleStyle` (174); the legacy pill `Play` / `Accent` / `Pill` / `IconPill` (196-284); `IconAction` (288); `PlayFab` (308); `CoverActionFab` (337); `TextAction` (397); `CtaPalette` (447); `ContextPlayButton` (491).
- **Controls.Picker.cs**: `PickerInk` (42), `PickerCard` (77, uses `Design.Colors.SelectedHover`), `PickerDensityRows` (146), `PickerModernRow` / `PickerClassicRow` (178, 199), `PickerStrip` (258), `EqualizerCurve` (290).
- **Controls.Words.cs**: word rail (190-409), with reduced-motion snapping at 325 and 384; `Links` (420).
- **Controls.Podcast.cs**: badge `Chip` (67), `LedgerBar` (97), `StarRow` (226), `Door` (272), `FindBox` (412).
- **Controls.ArtistAbout.cs**: Page / Rail about card (35-187).
- **Controls.Pending.cs**: `PendingBar` (32).
- **Controls.TrimTip.cs**: `TrimTip` (50), `TrimmedTitle` (65).
- **Controls.Cover.cs**: `MosaicTiles` (16).
- **ButtonRules.cs**: `FollowTint(a, light)` (50-82), `FollowStyle` (88-129).
- **Surface family**: `Surface` / `SurfaceHost` (Surface.Host.cs:42-193); shapes (Surface.Rules.cs:77-126); `SurfacePlate.For` (Surface.Parts.cs:44-58); Shell / StackBody / RowBody / Labels / Seed (Surface.Parts.cs:74-493); `BoundSurface` (Surface.Bound.cs:36).
- **No-UI or non-presentation files**: CoverImage.cs (WIC JPEG encode), Bodies.cs (pooled reads), Menus.Container.cs (menu verb table, rendered by the engine's ContextMenu), SelectionDrag.cs, RowClickPolicy.cs.

### Switch branches inside Controls.* / Surface / ButtonRules
- **Theme:** `Controls.cs:507, 525, 529, 1015, 1032`; `ButtonRules.cs:53-57, 71, 78`.
- **Reduced motion:** `Controls.cs:710`; `Controls.Art.cs:696, 720`; `Controls.Words.cs:325, 384`.
- **There are no `Classic`, Density, Wash or Marquee branches in any Platform shared control.** The only related items:
  - `Controls.Picker.cs:146 / 199` are the Classic and Density miniatures for Settings;
  - `Drag.InsertionCards(showArtwork)` at `Drag.cs:554` takes the HideTrackArtwork value as a parameter.

## 3. OS surfaces (decision vs rendering)

**Tray icon**
- Decisions in `Tray.cs`: modes (24-27); glyph Normal / Offline / Update (192-195); the icon theme follows the **taskbar** theme, not the app theme (32-34, 197); `.ico` files (199-207); tooltip (248-272); menu rows NowPlaying / PlayPause / Next / Previous / Like / Devices / Open-or-Hide / HideIcon / Quit (344-369).
- Binding in `Tray.Host.cs`: Watch / Boot (98-139), Publish (174), close and minimize (293-352), menu (442-460).

**Notifications** (`Notify.cs`)
- Topics (51-73) and levels (37-45).
- In-app toasts go through `Notify.Say` (`Controls.cs:1390`, 138 call sites in 40 files).
- The update-toast plan Available / Downloading / Installing / Completed / Failed is at `Notify.cs:777-818`; it is consumed at `Shell.Overlays.UI.cs:341`.

**Windows toasts** (`Notify.Host.cs`)
- Live banners: NewRelease, NewEpisode, Social, AppUpdate Downloading / Installing / Completed (278-328), plus the summary banner (229).
- Scheduled: daylist roll (359-450) and pre-save release drop with Play and Dismiss buttons (582-627).

**Updates**: `Update.Host.cs` is logic only. It publishes `Notify.Update` (689-694); the UI is the notification row (`Notify.cs:1199`) and the in-app toast.

**SMTC, taskbar, jump list, power** (`Playback.Os.cs`)
- SMTC: card, status, rate, buttons, timeline (152-354).
- Taskbar: pause overlay, progress modes, a 3-button thumbnail toolbar with `.ico` glyphs (436-608).
- Jump list: Pause/Resume and Search tasks plus 6 recents (621-829); the COM transaction runs on an STA worker (`Playback.Os.JumpListWorker.cs`).
- Power keep-awake (878-951).

**Not present**: there is no mini-player; Tray.cs §13 says "no mini-player flyout".

**File picker**: `Pickers.cs:20-58` (busy and slow toasts) and `ModalFilePicker.cs` (STA thread) with `ModalFilePicker.Rules.cs` (watchdog). No custom visuals.

**Drag/drop**: the chip data is `Drag.Chip` (`Drag.cs:476-507`; the engine renders it); the insertion preview is `Drag.InsertionCards` (554-613).

**Other OS-facing/presentation files**: `Playback.Transitions.cs` has no UI choreography (reducer only). Modules.UI.cs is the module page; its only switch is `Design.Reduced` at 392. Actions.UI.cs is the menu vocabulary and picker, with no theme branches.

## 4. Settings persistence

**Key tables**
- `Platform.Keys`: `Platform.cs:71-380` (140 static keys plus 16 per-subject factories at 351-379) and `Platform.Settings.cs:35-80` (17 keys).
- Private keys elsewhere: `Playback.Video.cs:587`, `Playback.Speed.cs:8`, `Playback.Host.cs:1280`.
- Legacy sidebar keys `sidebar.design` / `sidebar.classic.*` / `sidebar.v3.*`: `Sidebar.Migration.cs:245-252`.

**Appearance-relevant keys** (name, type, default, Platform.cs line)

| Key | Type | Default | Line |
|---|---|---|---|
| `theme.mode` | int | 0 | 111 |
| `localization.culture` | string | "system" | 114 |
| `detail.rowdensity` | int | 1 | 115 |
| `detail.rowstyle` | int | 0 | 116 |
| `appearance.trackArtwork.hidden` | bool | false | 119 |
| `appearance.likedCover.style` | int | 1 | 123 |
| `appearance.pageMotion.style` | int | `(int)Design.Nav.DefaultStyle` = 3 | 127 |
| `detail.tempoColumn` | bool | false | 130 |
| `detail.playsColumn` | bool | false | 133 |
| `detail.page.layout` | int | 0 | 135 |
| `appearance.daylistClock` | int | 0 (Both) | 139 |
| `appearance.marquee.enabled` | bool | true | 142 |
| `appearance.colorWashes.enabled` | bool | true | 144 |
| `appearance.zoom` | float | 1 | 148 |
| `appearance.zoom.mode` | int | Auto | 151 |
| `appearance.zoom.mode.bootstrap.version` | int | 0 | 152 |
| `appearance.lyrics.blurStrength` | int | -1 | 158 |
| rail widths / collapses | — | — | 169-181 |
| `npv.presentation` | int | 0 | 254 |
| `npv.player.style` | int | 0 | 255 |
| sidebar keys | — | — | 77-97 |
| `tray.*` | — | — | 316-323 |

**Migrations**
- `ZoomAutoPolicy.MigrateMode` (`Platform.cs:466-473`) runs at Boot.
- Sidebar v3 migration (`Sidebar.Migration.cs:256-285`).
- `video.pip.rect.v2` was abandoned rather than migrated (`Platform.cs:198-201`).

**Where the store lives** (`Platform.Host.cs:72-77`)
- Default: HKCU `Software\Wavee\Wavee` via `RegistryAppSettings`, using the engine's AppDataStore (488-560).
- `--profile`: `<profile>\settings.json` (`FileAppSettings`, 417-481).
- `--fake`: an in-memory overlay.

**Boot order**
- `App.cs:88` calls `Platform.Boot()` (`Platform.cs:1134-1168`): 1 factory reset · 2 **open settings (sync)** · 3 zoom migration · 4 developer signals · 5 scroll settings · 6 locale · 7 log · 8 crash writers · 9 credentials · 10 scope.
- Then `App.cs:199` calls `Shell.InstallUi`, which runs `Design.Install` (`Shell.UI.cs:61`).
- Then `Shell.Run` calls `SeedTheme()` at `Shell.Host.cs:230`, **before the window**, and the zoom is seeded through `AppOptions.Zoom` (`Shell.Host.cs:274`).
- `Settings.Get` is synchronous (`Facade`, `Platform.cs:968-983`) and available from Boot onward, so it is ready before the first frame. Before Boot it returns key defaults.

**`SettingsChanged`**
- Declared at `Platform.cs:955`; written on every `Set` (`Platform.cs:979-981`).
- Subscribers: `Screens\StoreShot.cs:50`, `Screens\Settings.UI.Privacy.cs:450`, `Shell\Stage.UI.cs:1810`, `Entities\Queue.UI.cs:325`.
- Comment-only mentions: `Update.Host.cs:24`, `Settings.UI.Appearance.cs:776`, `Prefs.cs:26`, `Notify.cs:18`, `App.cs:236`, `Crash.Host.cs:357/411`.

**Prefs layer** (`Prefs.cs`, `Prefs.Player.cs`, `Prefs.Stage.cs`)
- It caches no values. Each group is an epoch `Signal<int>` plus a read-through to the store with a clamp: Appearance (58), Lyrics (158), AiLyrics (245), DetailHero (282), NpvPlayer (`Prefs.Player.cs:32`), PlayerBar (`Prefs.Player.cs:124`), Stage (`Prefs.Stage.cs:23`).
- A writer persists first, then bumps (`Prefs.cs:136-140`).
- The theme and zoom do not bump an epoch (`Prefs.cs:52-53`).
- `BumpAll` (`Prefs.cs:322`) has **no callers**.
- Notification prefs use their own epoch (`Notify.cs:425`).

## 5. Theme-switch inventory (whole app)

**ThemeMode** (`Platform.cs:111`, default 0)
- Direct sites (5): `Shell\Shell.Host.cs:676`, `Shell\Shell.UI.cs:702`, `Shell\Shell.Palette.cs:271`, `Screens\Settings.UI.Appearance.cs:97, 210`.
- Derived `Tok.Theme` / `ThemeKind` reads:
  - Design.cs: about 45 lines (listed in §1);
  - `Platform\Controls.cs:507, 525, 529, 1015, 1032`
  - `Entities\Detail.UI.cs:514, 587, 656, 684, 735`
  - `Entities\Artist.Page.cs:144, 193, 238, 291`
  - `Entities\Profile.Page.cs:102, 163, 206, 238`
  - `Entities\Concert.UI.cs:134, 251`
  - `Entities\Track.UI.cs:760`, `Track.Drawer.cs:411`, `Track.UI.Bound.cs:699` (DataDotInk)
  - `Shell\Deck.UI.cs:605`, `Shell\Rail.UI.cs:344`, `Shell\Shell.Masthead.UI.cs:151`
  - `Shell\Shell.Palette.cs:269`, `Shell\Shell.UI.cs:703-704`, `Shell\Shell.Host.cs:679-681`
  - `Screens\Settings.UI.Appearance.cs:200-202`
  - `Shell\Visualizer.Palette.cs:71`
  - `Shell\Lyrics.UI.cs:152, 154` (`StageInk.IsDark`)
- Engine `Theme.Dark`: `Shell\Shell.Overlays.UI.cs:394, 482`; `Shell\Shell.PlayerBar.UI.cs:643`; `Shell\Shell.UI.cs:1276` (chrome toggle; also the profile menu at `Shell.Overlays.UI.cs:513-516`).
- Theme-derived Design APIs used outside Design.cs:

| API | Sites | Where |
|---|---|---|
| `StageInk` | 17 | Lyrics.UI 12, Video.UI 3, Visualizer.Palette 1, Visualizer.Zune.UI 1 |
| `SchemeFor` | 4 | Deck.UI 2, Rail.UI, Stage.UI |
| `ChromeSchemeFor` | 5 | Detail.UI, Queue.UI, Recents.Page 2, Search.UI |
| Placeholders | 24 | Artist.UI 6, Controls 6, others |
| `Design.Colors.*` | 34 | Track.Table 8, Shell.UI 8, Queue.UI 6, Rail.UI 4, Track.UI 3, Artist.Discography 2, others |
| `Palette.PageTone` / `Hairline` / `TextInk` / `DataDotInk` | 5 | various |
| `Wash.HeroAlpha` / `ShelfAlpha` | 3 | Shell.Masthead.UI |
| `ButtonRules.FollowStyle(light)` | — | via Controls.cs |

**Accent**: no key. System accent at `Shell.Host.cs:685-686`, `Shell.UI.cs:701`, `Settings.UI.Appearance.cs:207-208`. `Design.Accent.*` is used at 5 sites (Artist.Page, Modules.UI 3, Shell.PlayerBar.UI).

**ColorWashesEnabled** (`Platform.cs:144`, true; 7 sites)
- `Prefs.cs:82-85`; `Settings.UI.Appearance.cs:119`
- `Entities\Detail.UI.cs:669`, `Artist.Page.cs:295`, `Profile.Page.cs:242`, `Recents.Page.cs:1162, 1205`

**RowDensity** (`Platform.cs:115`, 1; 11 sites)
- `Prefs.cs:94`
- `Settings.UI.Appearance.cs:324, 357, 358`
- `Entities\Album.Pane.cs:192`
- `Track.Table.Chrome.cs:359, 804, 807, 821`
- `Track.Table.cs:644, 903`

**SidebarPaneDensity** (`Platform.cs:89`, 0)
- Key sites: `Sidebar.Host.cs:79, 287`; `Sidebar.Migration.cs:282`.
- The `Sidebar.Density` signal: `Sidebar.Host.cs:161, 191, 275, 284-285, 302`; `Settings.UI.Appearance.cs:553, 596`; `Shell.UI.cs:733`; `Sidebar.UI.Menus.cs:159, 268`; `Sidebar.Layout.cs:554, 594-597`; `Sidebar.Edit.cs:203, 238-239`.

**TrackRowStyle** (`Platform.cs:116`, 0; 8 key / Prefs sites)
- Settings writer: `Settings.UI.Appearance.cs:364, 373, 374`.
- Readers: `Prefs.cs:101`; `Entities\Artist.UI.Chart.cs:176`; `Artist.Page.cs:296`; `Queue.UI.cs:468`; `Track.Table.cs:612`.
- Derived `classic` lines:
  - Track.UI.cs (63): 230-1170
  - Track.UI.Bound.cs (65): 41-727
  - Track.Rules.cs (29): 43-413, 1585-1588
  - Track.Table.cs (27): 562-670, 1020, 1066, 2055-2169
  - Track.Table.Chrome.cs (20): 586-747, 1407
  - Queue.UI.cs (28): 26-1487
  - Artist.UI.Chart.cs (44): 20-866
  - Artist.UI.cs (8): 171-191
  - Artist.Page.cs: 116, 296, 1132
  - Detail.cs: 20, 569 (Modern 36 / Classic 32 header)
  - Controls.Picker.cs: 143, 197-212
- Other "Classic" hits are unrelated: the sidebar layout, `PageMotionStyle.Classic`, Visualizer Classics, `Deck.cs:1291`, `Video.cs:1379`.

**HideTrackArtwork** (`Platform.cs:119`, false; 15 sites)
- `Prefs.cs:66-69`; `Settings.UI.Appearance.cs:335, 337`
- `Artist.Page.cs:297`; `Artist.UI.Chart.cs:177`; `Search.Page.cs:136`
- `Rail.UI.cs:732`; `Recents.UI.cs:296, 316`; `Queue.UI.cs:469, 985`
- `Stage.Artist.UI.cs:561`; `Track.Table.cs:612`; `User.Page.Library.cs:1397`

**Sidebar layout** (`Platform.cs:83`, 0)
- Key sites: `Sidebar.Host.cs:78, 268`; `Sidebar.Migration.cs:279`.
- `Sidebar.Layout` / `SidebarLayoutId`: about 70 sites across Settings.UI.Appearance, Setup.UI:753, Shell.Palette:102/335, Shell.UI:733/837, and Sidebar*.cs.
- Related keys:
  - `SidebarPaneWidth` (85): `Sidebar.Host.cs:80, 572`; `Migration:280`
  - `SidebarPaneUserCollapsed` (87): `Host:81, 573`; `Migration:281`
  - `SidebarLibraryFilter` (91): `Host:87, 293`; `Migration:283`
  - `SidebarMigrationDropped` (93): `Settings.UI.Appearance.cs:574`; `Migration:284`
  - `SidebarBootstrapVersion` (81): `Migration:256, 285`
  - Legacy keys (77-78): `Setup.cs:691`
  - `PinsMigratedToServer` (97): `Migration:267`

**Stage keys** (`Platform.Settings.cs:43-71`)

| `Prefs.Stage` member | Default | Sites |
|---|---|---|
| Mode | 0 | 11 |
| Visualizer | 9 | 7 |
| Sensitivity | 1 | 7 |
| LyricsOverlay | true | 2 |
| SyncOffsetMs | 0 | 5 |
| Calm | false | 2 |
| Moments | true | 2 |
| GalleryOpen | true | 6 |
| Layout | 0 | 8 |
| Spectrum | 0 | 2 |
| HeroLyrics | true | 2 |
| HeroMotion | true | 2 |
| HeroDim | 75 | 2 |
| TipSeen | false | 2 |

- Direct `Keys.Stage*` reads: `Playback.Audio.cs:565`; `Settings.UI.Appearance.cs:160, 164, 166`; `Stage.cs:34`; `Stage.Layouts.cs:29, 32`; `Visualizer.cs:42`.
- `StageRects` (bool, false): `Platform.Settings.cs:136, 141`.

**Zoom**
- `ZoomLevel`: `Platform.cs:469`; `Settings.UI.Appearance.cs:296`; `Shell.Host.cs:274`; `Shell.UI.cs:317-318`.
- `ZoomMode`: `Platform.cs:471`; `Settings.UI.Appearance.cs:259, 283, 289`; `Shell.UI.cs:305, 310`.
- `ZoomModeBootstrapVersion`: `Platform.cs:468, 472`.

**Materials / transparency**: no key.
- `MicaAlt = false` is hard-coded at `Shell.Host.cs:263`.
- `Materials.EnergySaver` comes from power (`App.cs:72-73`).
- The stage acrylic is `Design.cs:609-610`.

**Motion**
- `PageMotionStyle`: `Design.cs:1783` (doc), `Prefs.cs:122`, `Settings.UI.Appearance.cs:104, 231-232`, `Shell.cs:1008`.
- Reduced motion is OS-only: 73 `Design.Reduced` reads in 34 files.
- `StageCalm` and `StageHeroMotion` are listed under the stage keys.

**Other appearance keys**
- `MarqueeEnabled` (142): `Prefs.cs:76`, `Settings.UI.Appearance.cs:117`, `Track.Table.cs:612`, `Track.UI.cs:339`, `Shell.PlayerBar.UI.cs:222`.
- `DaylistClock` (139): `Daylist.cs:135`, `Prefs.cs:131`, `Settings.UI.Appearance.cs:446, 455-456`, `Playlist.UI.cs:990`, `Home\Daylist.UI.cs:400`.
- `LikedCoverStyle` (123): `User.Cover.cs:93, 141, 1118`; `User.Liked.cs:23`; `Prefs.cs:112`.
- `DetailPageLayout` / `DetailRailUniform`: `Detail.cs:146`, `Detail.UI.cs:670-672`, `Prefs.cs:293, 301`, `Settings.UI.Appearance.cs:395-431`.
- `LyricsBlurStrength`: `Prefs.cs:220, 235`; `Settings.UI.Appearance.cs:86, 98, 732`; `Lyrics.cs:1425`.
- `NpvPresentation` / `NpvPlayerStyle`: `Prefs.Player.cs:58-99`; `Rail.cs:235-298`.
- `TempoColumn` / `PlaysColumn`: `Track.Table.cs:586, 592`; `Track.Table.Chrome.cs:346, 881-889`.
- `UiCulture`: `Platform.cs:555`; `Settings.UI.cs:122, 399`.
