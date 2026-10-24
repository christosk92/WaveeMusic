# Wavee shell + non-entity screens: presentation map (main, 2026-10-10)

Scope: `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\` — `Shell\*`, `Screens\*`, the presentation parts of `Playback\*`, `Platform\*`, `Diagnostics\*` (UI only), `AiLyrics\*` (UI only), `App.cs`, plus the PR 205 worktree `C:\WAVEE\sidebar\WaveeMusic` (branch `feat/sidebar-rework`, 95 commits ahead of origin/main). Entities/Home pages and the engine are mapped elsewhere; §13 lists every place the shell hosts or reaches into pages.

How this file was built: the shell core (Shell.cs, Shell.UI.cs, Shell.Host.cs, Shell.Chrome.cs, App.cs, Platform.cs/Host.cs, Prefs.*, Settings.UI.Appearance.cs, Design.cs §3) was read directly; six parallel read-only mappers covered the sidebar family (25 files, 18k lines read in full), the stage/visualizer/video/lyrics/deck family (37 files), the player bar/rail/overlays/masthead/palette group, the Screens folder, the Platform/OS layer (Design.cs read in full), and the PR 205 diff. Their reports follow verbatim as Parts 2–7; Part 1 is the shell core. Every claim carries `file:line`.

---

## 0. Inventory table (surface → files → states → switches)

Path prefix `…\src\apps\Wavee\` omitted. "Switches" = presentation customization inputs the surface branches on (persisted keys in **bold**, derived/session in plain text). Details and line numbers are in the referenced part.

| Surface | Files (key types) | States | Switches it branches on | Part |
|---|---|---|---|---|
| Window / frame root | `Shell\Shell.Host.cs` (`Run`, `RootHost`, `SeedTheme`), `Shell\Shell.UI.cs` (`Frame`, `FrameRoot`, `ContentRegion`), `Shell\Shell.cs` (`FrameRules`, `Ui`), `App.cs` | chrome mounted / collapsed (fullscreen video, stage), start-hidden (tray), harness `--frames/--screenshot`, cover-dimmed (setup wizard), file-drag over | **theme.mode**, **appearance.zoom** / **.zoom.mode**, **shell.rail.width**, **shell.rail.docked-video.height**, session rail open/mode, sidebar band (derived from width), `Design.Reduced` | 1 §A–B |
| Title bar (merged chrome row) | `Shell.UI.cs:1038-1413` (`ChromeRow`, `TabsIsland`, `CaptionLeadingIsland`, `TrailingIsland`, `AuthChip`, `PinButton`, `NavHistoryButton`, `BellButton`), `Shell\Shell.Chrome.cs` (`Chrome` allocator, `Layout` constants, `TabWorkspace`) | width stages (name / actions-in-row / forward / back / new-tab / trailing / search Field vs Icon, hysteretic), auth chip forms Profile / Connecting / Reconnect / SignIn, pane-toggle disabled while editing, unread badge, store-shot | `Theme.Dark` (sun/moon glyph), auth fold, `Sidebar.PinsVersion`, `Sidebar.Editing`, `StoreShot.Presenting`; PR 205 adds **nav style** (wordmark vs tabs, pane toggle hidden, compact search, theme toggle removed) | 1 §D, 7 §3.3 |
| Tab strip + history flyout | `Shell.UI.cs:1109-1268`, `Shell.Chrome.cs:515-889` | pinned prefix, closable rule, drag/drop (spring-load, deposit), label staged by `Shown` | — (`TabStripAppearance.Text` fixed) | 1 §D |
| Omnibar / search box + suggestions flyout | `Shell\Shell.Masthead.UI.cs:215-731` (`SearchField`, `SearchFlyoutButtonView`, `RichOmnibar`, `SuggestionsPopup`, `OmniRow`), `Shell.cs:2108-2338` (`Omnibar` model), `Shell\Shell.Omnibar.Rows.cs` | Field vs Icon form, Idle / Pending / Results / Empty / Failed, highlight row, ghost completion, history fallback source | chrome `SearchMode`; PR 205: pill form, nav style placement, expand-on-focus | 2 §1.6–1.7, 7 §3.4 |
| Masthead band (Browse breadcrumb) + material/wash layer | `Shell.Masthead.UI.cs:36-213` (`MastheadBand`, `MaterialLayerView`), `Shell.cs:776-968` (origins, `Trail`, `TryMasthead`), `Shell.cs:2042-2100` (`Mastheads`) | band shown only for Browse/HomeSection/Concert families; tint neutral vs page-claimed; Home 3-wash | **appearance.colorWashes.enabled** (via pages), `Tok.Theme` (wash alphas); PR 205: **appearance.wash.surfaces/accent/nowPlaying**, bleed backdrop | 1 §C, §G; 2 §1.6; 7 §2.6, §3.6 |
| Sidebar (expanded pane, rail, drawer, rows, flyouts, menus, Edit mode, DnD, resize) | `Shell\Sidebar*.cs` (25 files; `PaneHost`, `PaneView`, `PaneSlot`, `EntityRow`, `LibraryHead`, `EditPane`, `SidebarPaneModeRules`, `SidebarResizeRules`), `Shell.UI.cs:512-650, 1415-1563` (column, seam, `NarrowDrawer`) | loading skeletons, empty, error banner / vacancy, offline (= pending), signed-out, hover/pressed/selected/multi-select/focus/playing, Expanded / Compact rail / Minimal drawer (Wide ≥700 / Narrow 528–660 / Tiny <528), Edit mode, drag states, width tiers (<240 drill, grid 2–4 cols) | **sidebar.layout.id** (Classic / Library), **sidebar.pane.density**, Library **view** (list/grid) + sort/desc/showLiked/hidden kinds in `sidebar.json`, **sidebar.library.filter**, **sidebar.pane.width**, **sidebar.pane.userCollapsed**; band/mode derived; PR 205: **nav style** (+Zune), **sidebar.classic.covers**, **sidebar.zune.pins**, **sidebar.layout.lastPane** | 3; 7 §2.1–2.5, §3.2 |
| Content host / page swap | `Shell.UI.cs:885-1036` (`ContentHost`, `PageTransition`, `PageBody`, `NotFoundPage`, `OfflineLane`), `Shell.cs:970-1056` | 3 keep-alive slots, Forward / Back / Neutral motion, video-safe swap, not-found, empty body, offline lane | **appearance.pageMotion.style** (5 styles, developer-only row), `FgMotion.ReducedMotion`, `diag.developerMode` (developer routes) | 1 §C |
| Player bar (+ podcast, chapters) | `Shell\Shell.PlayerBar.cs` (rules), `.UI.cs` (`PlayerBarRoot`, `PlayerBarContent`, seek rail, devices, volume, overflow), `.Podcast.UI.cs`, `.Chapters.UI.cs`, `Shell.cs:1195-1446` (tiers, device rows) | NoTrack / Loading / Reconnecting / Error / Active, playing / paused / buffering, face hold 450 ms, remote device, podcast vs music vs video, live / DVR, 6 width tiers (Minimal…Full, 24-DIP hysteresis), hidden under fullscreen | **appearance.marquee.enabled**, **playerbar.duration.remaining**, **playback.quality** (picker echo), `Theme.Dark` (hairline), `Design.Reduced`, tier flags | 2 §1.1, §2, §4 |
| Rail (right): lyrics / queue / now-playing / friends / video | `Shell\Rail.cs`, `Rail.UI.cs` (`FrameCore`, `PinnedHero`, `NowPlayingBody`, `FriendsBody`, `VideoBody`), `Rail.Styles.UI.cs` | open/closed (translate), docked vs floating (`RailFits`), nothing playing, skeletons, friends Rows/Skeleton/Empty/Offline/Error, video unknown/none, stage-hosts-video fallback | **npv.presentation** (dev-only), **npv.player.style** (12 presets), **npv.player.<preset>.<opt>**, **appearance.trackArtwork.hidden**, `Tok.Theme` (hero wash), session rail mode/open | 2 §1.2, §4 |
| Deck (rail hero player faces) | `Shell\Deck.cs`, `Deck.UI.cs`, `Deck.Faces.cs` | tick gate, entrance 320 ms | **npv.player.style** (2×12-arm dispatch), per-preset options (20 reads), **stage.sensitivity**, `Tok.Theme` (wash) | 4 §1.8 |
| History page | `Shell\Shell.History.UI.cs`, `Shell.cs:1683-1843` (rules) | empty ×3, date groups, most-visited, unreachable rows 0.6 | `diag.developerMode` | 2 §1.3 |
| Device picker | `Shell\Shell.DevicePicker.cs`, `Shell.cs:1326-1446`, `Shell.PlayerBar.UI.cs:1343-1531` | local vs Connect sections, quality echo row, empty roster | — | 2 §1.4 |
| Overlays layer (banner, toasts, doors, profile chip/menu, dialogs, tips) | `Shell\Shell.Overlays.UI.cs` (`OverlaysLayer`, `ProfileChipCore`, `ProfileMenuContent`, `ConfirmLogout`, `OpenPlayLink`, `OpenSignatureDialog`, tips) | banner shown/hidden, identity skeleton, tier hidden, store-shot generic chip, play-link busy/failed | `Theme.Dark` (premium ink, theme row), `StoreShot.Presenting`, **playback.runtime.dismissed**, **setup.pending** | 2 §1.5 |
| Notification panel | `Shell.UI.cs:1565-2064` | filter pills, empty per filter, pending sync row, update row states, expandable activity | — (tokens only) | 1 §F |
| Narrow drawer | `Shell.UI.cs:1415-1563` | mounted only in Narrow/Tiny band; open/pinned-while-editing; scrim | sidebar band, `Design.Reduced`; PR 205 disables under Zune | 1 §E |
| Command palette (Ctrl+K) | `Shell\Shell.Palette.cs` | no matches, selected row | writes **theme.mode**, zoom, sidebar layout, NPV style | 2 §1.8 |
| Stage / now-playing (fullscreen) | `Shell\Stage*.cs` (`SurfaceCore`, `StageCtx`, `LayoutRules`, `CaptionHost`, `ChromeHost`, `Stage.Options.UI`, `Stage.Hero.UI`, `Stage.Artist.UI`) | nothing playing, playing/paused/buffering, podcast/music/video, aspect classes Desktop/Ultrawide/Portrait/Compact (hysteretic), OS fullscreen, idle chrome, gallery open, options popover, teaching tip | **stage.mode**, **stage.layout** (Card/LargeArt/Centered/Artist), **stage.spectrum**, **stage.heroLyrics/heroMotion/heroDim**, **stage.galleryOpen**, **stage.lyricsOverlay**, **stage.tipSeen**, theme arm (`StageInk.IsDark`), `Design.Reduced` | 4 §1.1–1.2, §4 |
| Visualizer (19 shown faces, gallery, strip) | `Shell\Visualizer*.cs`, `Verse*.cs` | source ladder Live→Precomputed→TempoGrid→Breath, lease, LOD Stage/Preview/Poster, moments | **stage.visualizer** (19-arm `Face` switch), **stage.sensitivity**, **stage.calm**, **stage.moments**, **stage.syncOffsetMs**, theme arm (`pal.Dark` / `Ink.IsDark`), `Design.Reduced` | 4 §1.3–1.4 |
| Lyrics view (rail + stage pane + NPV peek) | `Shell\Lyrics.UI.cs`, `Lyrics.cs` (metrics), `AiLyrics\AiLyrics.UI.Lyrics.cs` | nothing playing / no lyrics / shimmer / failed, unsynced / line / word-synced, podcast metrics, video note, resync pill, AI sparkle states, AI footer progress, debug layer | **appearance.lyrics.blurStrength**, session secondary line, `lyrics.ai.*`, `StageInk.IsDark`, `diag.developerMode` | 4 §1.5 |
| Video view (docked cap, PiP, pop-out, fullscreen, join poster) + OnMedia transport | `Shell\Video*.cs`, `Shell\OnMedia.UI.cs` | None / Docked / Floating / Detached / Fullscreen, Poster / Working / Failed, hover-only chrome | **video.placement**, **video.aspect.mode/customRatio**, **video.window.ontop**, **video.pip.rect.v2**, **playback.video.quality** | 4 §1.6–1.7 |
| Settings (7 tabs) | `Screens\Settings*.cs` | loading census, errors, empty, disabled-with-reason, developer-only rows | writes every appearance key (29 rows in §4 of Part 6); reads `theme.mode`, zoom, `Developer.Enabled`, sidebar signals, stage/NPV epochs | 6 §1.1, §4 |
| Setup wizard + sign-in door + runtime card | `Screens\Setup*.cs` | entry FirstRun / Reauth / TermsRearm, pages Terms / SignIn / Layout / LocalPlayback, sign-in facets, runtime phases, wide ≥770 vs compact | **setup.pending/completed**, **setup.terms.acceptedVersion**, **sidebar.layout.id** (Layout page), accent (Lottie recolor) | 6 §1.2, §5 |
| Release notes (What's new) + after-update plate | `Screens\ReleaseNotes*.cs` | loading / empty / since banner / rail absent offline; viewer | **app.whatsnew.*** | 6 §1.3 |
| Feedback dialog | `Screens\Feedback*.cs` | composing vs ready, crash variant | — | 6 §1.4 |
| Logs page | `Screens\LogsPage*.cs` | reading / none / failed, empty filter, expanded rows | `diagnostics.log.*`, **diag.developerMode** | 6 §1.5 |
| Crash prompt / hang toast | `Screens\Crash.UI.cs` | from-list vs this-launch, hang vs crash, not-configured | **crash.*** | 6 §1.6 |
| Recovery window | `Screens\Recovery*.cs` | engine-free Win32 TaskDialog; OS theme only | — | 6 §1.7 |
| Diagnostics pages + FPS overlay + capture + tiles | `Screens\Diagnostics*.cs` | three headline states; FPS pill gated | **diag.developerMode**, **diag.fpsOverlay**, `StoreShot.Presenting`, **diag.scrollProbeLevel/scrollFeelProfile** | 6 §1.8 |
| Store shot | `Screens\StoreShot.cs` | presenting mode | drives stage/rail/video/zoom; hides identity and dev surfaces | 6 §6 |
| Tray icon + menu | `Platform\Tray.cs`, `Tray.Host.cs` | Normal / Offline / Update glyph; close/minimize to tray; start hidden | **tray.icon.mode**, **tray.closeToTray/minimizeToTray/startHidden**; icon follows the **taskbar** theme | 5 §3 |
| Toasts (in-app + Windows), update prompts | `Platform\Notify.cs`, `Notify.Host.cs`, `Update.Host.cs`, `Controls.cs:1390` | update plan Available…Failed; quiet hours; topic levels | **notify.*** | 5 §3 |
| SMTC / taskbar thumbnail / jump list / power | `Playback\Playback.Os.cs`, `.JumpListWorker.cs` | — | — (no mini-player exists) | 5 §3 |
| Shared controls + Design tokens | `Platform\Design.cs`, `Controls*.cs`, `ButtonRules.cs`, `Surface*.cs`, `AccentLadder.cs` | — | `Tok.Theme` (~45 branches in Design.cs, 5 in Controls.cs), `Design.Reduced`, wash `Disabled` props, `PageMotionStyle` param | 5 §1–2 |

---

## 0.1 Master list of presentation switches on main (what the theme refactor must absorb)

Counts are branch/read sites as measured by the mappers (grep over `src/apps/Wavee`, bin/obj excluded). All persisted keys are read through `Platform.Settings.Get` (synchronous registry/JSON read, store opened in `Platform.Boot` before any window) — **every persisted switch is available before the first frame.** Keys are declared in `Platform\Platform.cs` (`Keys`, lines 71-380) and `Platform\Platform.Settings.cs:35-80`.

| # | Switch | Key(s) (decl) | Values / default | Reader layer | Writers (UI) | Branch sites (count) | Notes |
|---|---|---|---|---|---|---|---|
| 1 | Light / dark / system theme | `theme.mode` (`Platform.cs:111`) | 0 System · 1 Light · 2 Dark | `Tok.Theme` after `Shell.SeedPalette` (= `Tok.Use(Tok.NeutralPalette, kind)`, `Design.cs:203`) | Settings `SetTheme` (`Settings.UI.Appearance.cs:195`), title-bar sun/moon (`Shell.UI.cs:1276`→`Shell.Palette.cs:267`), profile menu Theme row (`Shell.Overlays.UI.cs:514`), palette row (`Shell.Palette.cs:305`) | key: 5; **four duplicated apply routines** (`Shell.Host.cs:674`, `Shell.UI.cs:699`, `Shell.Palette.cs:267`, `Settings.UI.Appearance.cs:195`); live `Tok.Theme`/`ThemeKind`/`Theme.Dark` branches: 74 lines / 19 files (Design.cs 33, Controls.cs 5, Detail.UI 5, Profile.Page 4, Artist.Page 4, …); `StageInk.IsDark`/`pal.Dark` arm in stage family ~113 lines; `Signal<ThemeKind>` republish pattern ×3 | Seeded before the window (`Shell.Host.cs:230`). Engine cross-fade via `ThemeControl.Request` (4 consumers). |
| 2 | Accent | none | OS accent ramp only | `Tok.SetAccent(FluentApp.SystemAccentRamp())` at `Shell.Host.cs:685`, `Shell.UI.cs:701`, `Settings.UI.Appearance.cs:207` | — | artwork accent via `Design.Palette.ChromeAccent` / `AccentLadder` / `AccentCtx.Slot` (page-scoped) | PR 205 adds `appearance.wash.accent` gate. |
| 3 | Colour washes (page tint, Home washes, artist blend, veil) | `appearance.colorWashes.enabled` (`Platform.cs:144`) | bool, true | `Prefs.Appearance.ColorWashes()` (`Prefs.cs:82`) | Settings row `Settings.UI.Appearance.cs:118` | 5 readers (`Artist.Page.cs:295`, `Detail.UI.cs:669`, `Profile.Page.cs:242`, `Recents.Page.cs:1162,1205`) → `Disabled`/`Apply` props of `CoverPageTonePlane`/`CoverArtistBlendWash`/`CoverShellTintBinder` (`Design.cs:2337-2339, 2443, 2575`) | PR 205 splits into 3 keys (`appearance.wash.surfaces` Off/Subtle/Rich, `.accent`, `.nowPlaying`); 13 + 4 + 5 consumers; `ColorWashes()` deleted. |
| 4 | Track row style | `detail.rowstyle` (`Platform.cs:116`) | 0 Modern · 1 Classic | `Prefs.Appearance.TrackRowStyle()` (`Prefs.cs:98`) | `Settings.UI.Appearance.cs:362-378` | key 8; derived `classic` fans out to ~400 lines: Track.UI 63, Track.UI.Bound 65, Track.Rules 29, Track.Table 27, Track.Table.Chrome 20, Queue.UI 28, Artist.UI.Chart 44, Artist.UI 8, Artist.Page 3, Detail.cs 2, Controls.Picker 3 | The largest single "theme switch" on main; it lives in page code (entities map). |
| 5 | Row density | `detail.rowdensity` (`Platform.cs:115`) | 0 Compact · 1 Default · 2 Cozy · 3 Comfortable | `Prefs.Appearance.RowDensity()` (`Prefs.cs:91`); ladders `Track.TableRules.RowHeightFor/ArtSizeFor(density, classic)` | Settings cards `Settings.UI.Appearance.cs:322-360`; table ⋯ menu `Track.Table.Chrome.cs:804-889` | 11 (Track.Table 2, Track.Table.Chrome 4, Album.Pane 1, Settings 3, Prefs 1) | Separate from sidebar density. |
| 6 | Sidebar layout | `sidebar.layout.id` (`Platform.cs:83`) | 0 Classic · 1 Library | `Sidebar.Layout` signal (`Sidebar.Host.cs:158`), read at Boot `:78` | Settings cards `Settings.UI.Appearance.cs:605`, pane menu, palette `Shell.Palette.cs:335`, Setup Layout page `Setup.UI.cs:753` | 24 comparison lines in the family + 4 `Config.Layout` + 6 outside (~70 incl. `SidebarLayoutId.` refs); a full remount on switch (`Sidebar.UI.cs:95`); `OwningSection` renderer branch `Sidebar.UI.cs:1434` | PR 205 widens to `ShellNavStyle {Classic, Library, Zune}` (~70 sites) + `sidebar.layout.lastPane`. |
| 7 | Sidebar density | `sidebar.pane.density` (`Platform.cs:89`) | 0 Default · 1 Compact | `Sidebar.Density` signal (`Sidebar.Host.cs:161`) | Settings combo `Settings.UI.Appearance.cs:591`, pane menu | 1 root (`Sidebar.Layout.cs:594-597 ShapeFor`) → ~24 row-shape sites; 13 sites hardcode two-line | PR 205: applies to Library only; Classic gets `sidebar.classic.covers`. |
| 8 | Library view (cards vs rows) | `sidebar.json` library `"view"` (`Sidebar.Store.cs:299`) | list / grid | `Sidebar.Layout.cs:573` | Library toolbar toggles `Sidebar.UI.Library.cs:891-907` | 13 | The only cards-vs-rows switch on main. |
| 9 | Sidebar pane width / collapse / filter chip / Library sort,desc,showLiked,hidden kinds | `sidebar.pane.width`, `.userCollapsed`, `sidebar.library.filter`, `sidebar.json` | — | `Sidebar.Host.cs:77-87` | pane seam, menus | ~10 width tiers; filter 7; sort 9; showLiked 4; hidden 6 | Geometry/preference, not visual style. |
| 10 | Hide track artwork | `appearance.trackArtwork.hidden` (`Platform.cs:119`) | bool false | `Prefs.Appearance.TrackArtworkHidden()` | Settings `:334` | 11 readers (Track.Table, Queue.UI ×2, Recents.UI ×2, Artist.Page, Artist.UI.Chart, Search.Page, User.Page.Library, Rail.UI, Stage.Artist.UI) | |
| 11 | Marquee | `appearance.marquee.enabled` (`Platform.cs:142`) | bool true | `Prefs.Appearance.Marquee()` | Settings `:116` | 2 readers → 3 branches in PlayerBar.UI + Track.Table/Track.UI.Bound | |
| 12 | Page motion style | `appearance.pageMotion.style` (`Platform.cs:127`) | Fluent · Spatial · WinUi · **Classic** (default) · None | `Prefs.Appearance.PageMotionStyle()` → `Shell.cs:1007` → `Design.Nav.RecipeFor` (`Design.cs:1868`) | developer-only Settings row `:125` | 6 (+ `Shell.UI.cs:920` label staging) | A whole alternative motion grammar per style already exists in `Design.Nav`. |
| 13 | Liked cover treatment | `appearance.likedCover.style` (`Platform.cs:123`) | 9 styles, default Lens | `Prefs.Appearance.LikedCover()` | Liked page picker flyout `User.Cover.cs:1118` | 3 key + treatment switch `User.Cover.cs:259-265` | Entities map. |
| 14 | Daylist clock | `appearance.daylistClock` (`Platform.cs:139`) | Both / UpdateTime / Countdown | `Prefs.Appearance.DaylistClock()` | Settings `:442` | 3 readers | |
| 15 | Track page layout + rail uniform + 8 rail widths/collapse | `detail.page.layout`, `detail.rail.*` (`Platform.cs:135, 166-181`) | Automatic / Hero | `Prefs.DetailHero.*` | Settings `:393-487` | reader `Detail.UI.cs:670-672` + rail writers | Entities map. |
| 16 | Tempo / plays columns | `detail.tempoColumn`, `detail.playsColumn` | bool | Appearance epoch | table ⋯ menu | 7 | |
| 17 | Zoom | `appearance.zoom`, `.zoom.mode`, `.zoom.mode.bootstrap.version` (`Platform.cs:148-152`) | float 1.0; Auto/Dense/Manual | `AppOptions.Zoom` (`Shell.Host.cs:274`), auto-zoom loop `Shell.UI.cs:295-319` | Settings combo, Ctrl± chords, Ctrl+wheel, palette | 13 | Auto mode can re-zoom ~500 ms after launch. |
| 18 | Stage mode / layout / visualizer / spectrum / hero options / calm / moments / sensitivity / sync / gallery / tip | 14 `stage.*` keys (`Platform.Settings.cs:43-71`) | see Part 4 §4 | `Prefs.Stage.*` (`Prefs.Stage.cs`), copied into `StageCtx` on first render | Stage options popover, gallery, `[`/`]`/G keys, Settings Fullscreen rows, StoreShot | mode ~48, layout 77+43, visualizer 72 refs / 19-arm switch, spectrum 23, heroLyrics 29, calm ~55, moments 13, gallery 17, sensitivity 13 | Visualizer "Zune" / "iTunes" / "Classics" groups already exist as faces. |
| 19 | Now-playing hero: cover vs player (deck) | `npv.presentation` (`Platform.cs:254`) | 0 Cover · 1 Player; **developer-mode gated** (`Prefs.Player.cs:50`) | `Rail.PlayerPrefs.ResolvedPresentation()` | rail header switch, art menu, Settings (dev), palette | 5 | Hidden in production builds. |
| 20 | Player style (deck preset) + per-preset options | `npv.player.style`, `npv.player.<preset>.<opt>` (`Platform.cs:255, 379`) | 12 presets (Record, Cassette, Reel, CD, Turntable, iPod, Winamp, VU, Zune, WMP, Canvas, Picture) | `Rail.PlayerPrefs.Style/CurrentPreset/Choice` | style flyout `Rail.Styles.UI.cs`, Settings combo, palette | 2×12-arm dispatch (`Deck.cs:1446`, `Deck.UI.cs:199`) + 20 option reads + ~6 rail sites | Only reachable with #19 on. |
| 21 | Player bar remaining time | `playerbar.duration.remaining` (`Platform.cs:184`) | bool true | `Prefs.PlayerBar.ShowRemaining()` | bar time label click, Settings Playback | 1 branch (`Shell.PlayerBar.cs:617`) | |
| 22 | Lyrics blur strength / secondary line | `appearance.lyrics.blurStrength` (−1 Auto); secondary line session-only | — | `Prefs.Lyrics.*` | Settings slider; rail/stage globe toggles | 5 / 7 | Blur forced 0 on stage. |
| 23 | AI lyrics prefs | `lyrics.ai.*` (`Platform.cs:161-165`) | — | `Prefs.AiLyrics.*` | Settings AI card | header/footer rules | |
| 24 | Video placement / aspect / ontop / pip rect / quality | `video.*`, `playback.video.quality` (`Platform.cs:197-205, 216`) | — | `Video.Prefs` (`Video.Host.cs:233-264`) | placement menu, OnMedia ⋯ | ~91 placement refs, 29 aspect, 9 ontop | Mostly behaviour, but placement decides which surface is on screen. |
| 25 | Window width tiers (derived) | — | sidebar band Wide/Narrow/Tiny (700/660, 528/568); player bar 6 tiers (440/760/900/1100/1240 −24); chrome allocator stages (520/1200/1360 +40); stage aspect classes; Library <240; Settings none | pure rules (`SidebarPaneModeRules`, `Shell.PlayerBar`, `Chrome.StageFor`, `Stage.Aspect`) | — | ~35 bar flag sites; ~24 planner `st.Compact`; ~57 stage aspect lines | Not a theme switch, but every theme must honour the same ladders or replace them per theme. |
| 26 | Reduced motion (OS) | — | `FgMotion.ReducedMotion` via `Design.Reduced` (`Design.cs:1554`) | — | — | 73 reads / 35 files (+~126 in the stage family) | |
| 27 | Developer mode / FPS overlay / Store shot | `diag.developerMode`, `diag.fpsOverlay` (`Platform.cs:335-336`); `StoreShot.Presenting` signal | bool | `Platform.Developer.Enabled` signal; `StoreShot.Presenting` | Settings Privacy; `wavee://diag` | DeveloperMode key 8, `Developer.Enabled` 5, FpsOverlay 4, `Presenting` 5 | Gate developer-only routes/rows; Presenting hides identity. |
| 28 | Tray / notify / update prefs | `tray.*`, `notify.*`, `app.update.*` | — | `Tray.Host`, `Notify` | Settings General/Notifications/About | — | OS-surface behaviour; tray icon theme follows the **taskbar** (`Tray.cs:32-34, 197`). |
| 29 | Language | `localization.culture` (`Platform.cs:114`) | "system" | `Platform.Locale` at Boot | Settings General | 3 | Applies next launch. |

**Not present on main (confirmed by grep):** no `NavStyle`/`PresentedNavStyle`/Zune navigation (0 hits; the only "Zune" on main is the visualizer group and the deck preset), no "Show covers", no player-bar style variant, no accent picker, no Mica/transparency/materials switch (`MicaAlt = false` hard-coded `Shell.Host.cs:263`), no font/type-size option, no app-wide density, no high-contrast switch, no mini-player, no runtime V2/V3 sidebar branch (migration is one-time at boot).

**Theme-toggle entry points on main (4):** Settings Appearance selector; title-bar sun/moon (`Shell.UI.cs:1273-1286`); profile menu Theme row (`Shell.Overlays.UI.cs:514-516`); command palette row (`Shell.Palette.cs:94, 305`). PR 205 deletes the title-bar toggle.

## 0.2 Cross-cutting findings for the refactor

1. **Theme apply logic is copied four times** (mode→`ThemeKind`, `SeedPalette`, `Tok.SetAccent`, `ThemeControl.Request`): `Shell.Host.cs:674-690`, `Shell.UI.cs:699-708`, `Shell.Palette.cs:267-273`, `Settings.UI.Appearance.cs:195-213`. One owner is the obvious first move.
2. **The only pre-window seed is light/dark + accent + icon font** (`SeedTheme`, `Shell.Host.cs:230`). Everything else presentation-related is read by the component that needs it at its first render (sidebar layout at `Sidebar.Boot`, stage prefs in `SurfaceCore`'s first effect, rail width/mode in `SeedFrameState`, zoom in `AppOptions`). Because every read is synchronous and happens before the mount, main does not paint a wrong-theme frame at boot; the residual first-frame risks are (a) the auto-zoom re-apply ~500 ms after launch (`FrameRules.ZoomAutoDebounceMs`), (b) the neutral material ground on frame 1 before the restored page claims its tint (by design, cross-faded), (c) the sidebar binder planning skeletons until `PumpBinder` runs, (d) the setup wizard plate opening 1–2 posted frames after the frame mounts over an already-painted shell.
3. **Switch application is "live, in place"**: writers persist then bump an epoch (`Prefs.*`), readers re-read the store inside render (`_ = Epoch.Value; return Settings.Get(key)`). Theme and zoom are the exceptions (engine epoch / window scale). A sidebar layout switch is a full remount keyed on the layout (`Sidebar.UI.cs:95`); a stage layout change is a KeepAlive-keyed slide. Nothing on main has a "switch everything atomically" primitive; a theme switch would currently have to bump every epoch (`Prefs.BumpAll` exists at `Prefs.cs:322` but has **no callers**) and still would not remount layout-keyed trees.
4. **Design.cs is the only token layer and it has no app-level theme object**: every colour is a property over `Tok.Theme`; the only per-theme structure is the engine `TokenSet` (light/dark) plus `ShellPalette`, `StageArm`, `Palette.*` recipes; geometry (`Design.Size`, `Dock`), type (`Design.Type`), motion (`Design.Motion`, `Design.Nav`) and wash placements are static. Radii/spacing/elevation/motion tokens come from the engine (`Radii.*`, `Spacing.*`, `Elevation.*`, `MotionTok.*`).
5. **Pixel ladders live in CORE rule files**, not in tokens: `Shell.cs:1195-1323` (player bar per-tier metrics), `Shell.Chrome.cs:26-172` (chrome budget), `Sidebar.cs:340-586` (`SidebarRowGeometry`), `Sidebar.Library.cs:32-61`, `Stage.cs:78-102, 258-276, 351-357`, `Stage.Layouts.cs:269-289, 367-380`, `Setup.cs:41-104`, `Lyrics.cs:1456-1521`, `ReleaseNotes.cs:1456-1632`. A theme that changes shape must be able to supply these tables.
6. **View files own real logic** (per part): player bar verbs/scrub state machine/device intents in `Shell.PlayerBar.UI.cs`; sidebar binder/plan pipeline/selection sweeps in `Sidebar.UI.cs`; the wizard state machine in `Setup.UI.cs`; theme/zoom/rail persistence in `Shell.UI.cs`; settings writers throughout `Settings.UI.*.cs`. A theme boundary drawn at `*.UI.cs` would cut through logic.
7. **Glyph/loc choices in rule tables**: route glyphs in `s_routes` (`Shell.cs:148-204`), `PlayerBarRules.RepeatGlyph/ConnectGlyph`, palette builtins, Settings catalog glyphs (`Settings.cs:102-216`), sidebar section glyphs — a theme that changes iconography needs these as inputs.
8. **Three documented cases of the same visual decision made in two places**: `ToggleGallery` (`Stage.UI.cs:205-220` vs `Stage.Options.UI.cs:268-274`); zoom persistence (`Shell.UI.cs:305-319` vs `Settings.UI.Appearance.cs:279-298`); `pal.Dark` vs `Ink.IsDark` in visualizer faces.
9. **Stale docs**: `docs\guide\fullscreen-visualizers.md` (8 kinds / Horizon / 30 Hz), `Shell.PlayerBar.UI.cs:51` (says unmount, is bound Visible), `Shell.Palette.cs:11` (19 rows, is 29), `Stage.UI.cs` header (cites `Shell.UI.cs:500`, mount is 488-493).

## 0.3 PR 205 absorption summary (details in Part 7)

- Adds **`ShellNavStyle {Classic, Library, Zune}`** stored in the existing `sidebar.layout.id` (+ `sidebar.layout.lastPane`), with a live signal `Sidebar.NavStyle` and a **lagging presented copy** `Shell.Ui.PresentedNavStyle` (+332 ms `HoistSettleMs`) so the frame moves first and heads reflow second. ~70 branch sites / 25 files, 21 in `Shell.UI.cs`.
- Adds the **Zune band** (row 1 pivots + pins, row 2 `ZuneSubRow {Library, Views, Categories, Context, None}` 84/52/0 DIP), **PageHead/PageGeometry** (route-static head kinds, `Shell.Ui.PageGutter` 36/32/16, `Shell.PageViews`/`PageBands` LRUs that pages publish into: 11 `PageHead.Create`, 6 `Publish`, 4 `PublishBand` sites), the **artist bleed** through `ShellMaterialState.Backdrop`, **chrome ink** binds (`Shell.Ui.ChromeInk*`, currently always 0-mix), the **three wash settings**, `sidebar.classic.covers` (new `SidebarRowShape.Text`), `sidebar.zune.pins`, the search **pill** + animated flyout, one action row / Play split / Liked slim head, and 7 engine commits (`TitleBar.CaptionInk`, keyed title-bar children, `AutoSuggestBox` inks, `AutoSuggestBoxPresenter.Chrome`, disclosure-band retire/slide, loop-survives-snap).
- Deletes from main: `PivotTabs`, `FacetForm`, `Design.Type.PivotLabel`, `FacetTitleStyle/FacetCompactStyle`, `Prefs.Appearance.ColorWashes()`, the title-bar theme toggle, `TableDensityButton/SelectButton/DensityPanel`, Search `QueryEcho/FacetRow`, `WordRail`, the footer Settings row (Ctrl+, instead).
- The tip (59fd96c1, round 4c P02) is an **unfinished checkpoint**; its own message recommends reverting P01 and dropping the entrance-clock plumbing (`ShellEntranceClock`, `BleedPresenter.Follow`, `Artist.Page` clock arming) — treat as disposable.

---

# Parts

1. Shell core — startup, frame tree, navigation, chrome, theme pipeline (`map-shell.part-core.md`)
2. Player bar, rail, history, device picker, overlays, masthead/omnibar, palette (`map-shell.part-playerbar.md`)
3. Sidebar family (`map-shell.part-sidebar.md`)
4. Stage, visualizers, verse, lyrics, video, OnMedia, deck (`map-shell.part-stage.md`)
5. Platform: Design tokens, shared controls, OS surfaces, settings persistence, app-wide key inventory (`map-shell.part-platform.md`)
6. Screens: Settings, Setup, Release notes, Feedback, Logs, Crash, Recovery, Diagnostics, Store shot (`map-shell.part-screens.md`)
7. PR 205 `feat/sidebar-rework` (`map-shell.part-pr205.md`)

---


# Part 1 — Shell core (startup, frame, navigation, chrome, theme pipeline)


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


# Part 2 — Player bar, rail, history, device picker, overlays, masthead/omnibar, palette


Paths are relative to `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\`, abbreviated as follows:

| Short name | Path |
|---|---|
| PB | `Shell\Shell.PlayerBar.cs` |
| PBUI | `Shell\Shell.PlayerBar.UI.cs` |
| PBPod | `Shell\Shell.PlayerBar.Podcast.UI.cs` |
| PBCh | `Shell\Shell.PlayerBar.Chapters.UI.cs` |
| R | `Shell\Rail.cs` |
| RUI | `Shell\Rail.UI.cs` |
| RSt | `Shell\Rail.Styles.UI.cs` |
| Hist | `Shell\Shell.History.UI.cs` |
| DP | `Shell\Shell.DevicePicker.cs` |
| Ov | `Shell\Shell.Overlays.UI.cs` |
| Mh | `Shell\Shell.Masthead.UI.cs` |
| OmR | `Shell\Shell.Omnibar.Rows.cs` |
| Pal | `Shell\Shell.Palette.cs` |
| HDeck | `Shell\Shell.Host.Deck.cs` |
| PC | `Shell\PublishCadence.cs` |
| CtxT, Seam, End, Gl | `Playback\Playback.ContextToggle.cs`, `.CardSeam.cs`, `.Endgame.cs`, `.Glitch.cs` (they live under `Playback\`, not `Shell\`) |
| Pick, Pod, Cta, MC | `Platform\Controls.Picker.cs`, `Platform\Controls.Podcast.cs`, `Platform\Controls.Cta.cs`, `Platform\Menus.Container.cs` |

## 0. Where the brief's assumptions don't match the code

- **Shell.History.UI.cs is the History page**, mounted as the `history` route at Shell.UI.cs:58. It is not a back/forward overlay. Back and forward are icon buttons at Shell.UI.cs:1376, the hover/nav hook at Shell.UI.cs:325, and the chords at Shell.UI.cs:453-454.
- **Shell.Masthead.UI.cs is not the title-bar row.** It holds three things:
  - the Browse breadcrumb band (Mh:36-132);
  - the material/wash layer behind the chrome (Mh:139-213);
  - the omnibar (Mh:219-731).
  The title bar itself (caption buttons, back/forward, the profile chip slot) is `ChromeRow` at Shell.UI.cs:1069-1340, with `ShowCaptionButtons` at 1073, the profile chip mounted at 1316 and the search icon at 1284.
- **Shell.Host.Deck.cs is not visual deck hosting.** It persists and restores the playback deck and queue to session.json and writes the play log (HDeck:1-34). The visual "deck" (the record/cassette player faces) is `Deck.View()` in Deck.UI.cs, mounted from RUI:369.
- **There is no "styles" view inside the rail body.** "Styles" means the now-playing hero's player-style picker flyout (RSt).
- **PublishCadence.cs is pure data-publish throttling** with no visuals (PC:27-57).
- **The doc comment at PBUI:51 is stale.** It says the frame "UNMOUNTS [the bar] while video is fullscreen". The code actually keeps it mounted and hides it with a bound `Visible = Prop.Of(s_chromeMounted)` (Shell.UI.cs:463-471, defined at 652).
- **The Palette header comment is stale.** It says "19 rows" (Pal:11), but `BuiltinCount = 29` (Pal:75) and `Builtins()` has 29 entries (Pal:84-114).

---

## 1. Surfaces and their parts

### 1.1 Player bar

**Mount point.** `Shell.PlayerBarDock()` at PBUI:52 wraps `PlayerBarRoot`. The frame mounts it at Shell.UI.cs:471.

**Tier owner** (`PlayerBarRoot`, PBUI:165-188):
- It folds `Viewport.Size` into a `Signal<PlayerBarLayout>` using `PlayerBarLayout.Initial` / `Resolve` (Shell.cs:1264-1267), with 24 DIP of hysteresis on narrowing (Shell.cs:1204, 1216-1223).
- It logs tier changes (PBUI:182).
- The tier ladder (Shell.cs:1195-1212):

| Tier | From width |
|---|---|
| Minimal | below 440 |
| Compact | 440 |
| Medium | 760 |
| Comfortable | 900 |
| Wide | 1100 |
| Full | 1240 |

- `ForTier` (Shell.cs:1271-1323) sets the 13 `Show*` flags and the metrics: `ButtonBox` is a flat 32/16; `PrimaryBox` is 40/20 from Medium up, otherwise 36/18; `LeftW` is 260/240/230/220/172/132; `ArtSize` is 48 or 40; gaps and padding vary per tier.

**Pure rules** (PB):

| Rule | Location |
|---|---|
| `PlayerState` {NoTrack, Loading, Reconnecting, Error, Active} | PB:34 |
| `NowPlayingText` | PB:37 |
| `FaceArm` {Live, Hold, Placeholder} | PB:48 |
| `NowPlayingInk` | PB:51 |
| `PrimaryVerb` | PB:54 |
| `RowAction` | PB:57 |
| `OverflowCommand` (10 values) | PB:60 |
| `SeekRailMode` {Track, Dvr, Line} | PB:64 |
| `RightSlot` flags | PB:74-87 |
| `PlayerBarFacts` | PB:90-95 |
| `PlayerBarRules.FaceOf` (450 ms hold) | PB:129 |
| `StateOf` | PB:142 |
| `Fold` | PB:161-181 |
| `RowVerb` | PB:187 |
| `TextOf` / `InkOf` | PB:192 / 200 |
| `ShowsArtistLine` | PB:208 |
| `ShowsPauseGlyph` | PB:217 |
| `TopEdgeSweeps` (450 ms grace) | PB:226 |
| `OwnsTransport` | PB:233 |
| `VideoSlotReserved` | PB:241 |
| `OverflowSlotReserved` | PB:247 |
| `RightSlots` / `SlotWidth` / `RightWidth` | PB:256 / 273 / 283 |
| `SlotFaceVisible` | PB:301 |
| `LikeFaceVisible` | PB:315 |
| `VolumeInOverflow` | PB:320 |
| `Overflow` | PB:330-344 |
| `NextRepeat` / `RepeatGlyph` | PB:347 / 354 |
| `VolumePercent` | PB:357 |
| `LikePops` | PB:362 |
| `FaultTitleKey` | PB:369 |
| `DeviceRoster` (`RemoteSlot`, `SlotOfId`, `ConnectGlyph`, `LocalGlyph`, `PickerMaxHeight`) | PB:383-432 |
| `PlayerKey` key map | PB:443 |
| `PodcastBarLayout` | PB:455 |
| `SeekRail` (`ModeOf`, `Enabled`, `Advances`, `SpanMs`, `Ticks`, `DwellMs`, `ModelFraction`, `Displayed`, `Quantize`, `FractionAt`, `CanCommit`, `CommitTargetMs`, `CommitHoldMs`, `HoldsDrop`, `StepTargetMs`, `KeySpace`, `ThumbX`) | PB:470-587 |
| `TimeLabel` | PB:591-621 |

**Bar body** (`PlayerBarContent`, PBUI:192-685):

- **Root** (PBUI:646-683):
  - Fixed height `Design.Size.PlayerBarH`, `IsolateLayout`, focusable, with no fill or shadow (it sits over Mica).
  - Keyboard: Left/Right step the scrub ladder (5 s, 15 s, 30 s; Shift = 1 s), Up/Down change volume by ±0.05, Space toggles play (PBUI:654-671).
- **Top edge** (PBUI:634-644): a 1 px hairline, or `ProgressBar.Indeterminate(L.TopEdgeWidth)` while loading or reconnecting.
- **Left cluster — now playing** (PBUI:321-457):
  - Optional "Playing on <device>" line, `BarRemoteDeviceLine`, PBUI:1492-1531.
  - Title, either a `Marquee` or a trimmed `TextEl` with a `TrimTip` tooltip (PBUI:334-367, 378).
  - Artists line, `BarArtistsLine` (PBUI:939-1023): per-artist links, or the first name plus a "+N" chip (`BarArtistOverflowChip`, PBUI:1062-1099, which opens a `MenuFlyout` upward). `BarMetaLink` is at PBUI:1027.
  - Art tile, `Controls.Artwork` with morph key `Stage.Entry.MorphKey` (PBUI:414-426). Clicking it goes to the playback context route.
  - Heart / save slot (PBUI:428-435).
  - The cluster is a drag source (PBUI:451) and has a context menu through `Stage.NowPlayingMenu` (PBUI:457).
- **Centre — transport and seek** (PBUI:459-518):
  - Prev / (−10 for podcasts) / Primary / Next / (+10, speed for podcasts) (PBUI:460-479).
  - `BarPrimaryButton` (PBUI:1164-1191): an accent disc with the solid play/pause glyph (PlaySolid/PauseSolid at PBUI:1158).
  - Elapsed label, seek rail, remaining label (PBUI:482-492).
  - The centre is a drop target that does "Play next" (PBUI:499-504 → `BarPlayNextDropAsync`, PBUI:901-933).
- **Right cluster** (PBUI:520-628): slots in bit order Shuffle, Repeat, Volume, VolumeSlider, Lyrics, Video split (+ chevron menu, `Video.PlacementMenu`), Queue, Devices, Expand (fullscreen stage), More "⋯".
  - Each slot is wrapped by `Slot()` (PBUI:1138-1148): a fixed box whose face fades with opacity only.
  - `BarButton` (PBUI:1109-1132) is the toggle/command glyph. When latched it shows an accent glyph on a `FillSubtleSecondary` plate; the heart (`platedWhenLatched: false`) gets no plate.
- **Overflow** (`BarMoreButton` / `BarMoreMenu`, PBUI:1199-1290): a `MenuFlyout` opening up and right-aligned. Rows are built when the menu opens, from live peeks (`OverflowItem`, PBUI:1245).
- **Volume** (`BarVolumeButton`, PBUI:1297-1335):
  - Inline form: clicking mutes.
  - Popup form: a vertical `Slider` (124 × 32) in a `PopupChrome.Popup` above the button.
  - The inline slider uses `s_barVolume` (PBUI:154), length 96 (PBUI:570-572).
- **Devices** (PBUI:1343-1531): `BarDevicesButton` is latched when playback is remote. It auto-opens on `Playback.DevicePickerRequest` (PBUI:1354-1364). `BarToggleDevicePicker` (PBUI:1374), `BarDevicePickerMenu` (PBUI:1391, re-keyed per roster change and capped at window height − 96), `DevicePickerMenuItems` (PBUI:1438), `RunDeviceIntent` (PBUI:1473).
- **Seek rail** (`BarSeekRail`, PBUI:1538-1865):
  - Fill and thumb are compositor binds (PBUI:1586-1587).
  - A pixel-dwell interval (PBUI:1618) and a scrub gate.
  - A readout bubble shows the time under the pointer while scrubbing (PBUI:1834-1864).
  - The DVR live-edge mark (PBUI:1647-1657).
  - `BarLiveLine` (PBUI:1870-1898) is a 2 DIP "breathing" accent line used when there is nothing to rewind.
- **Time labels** (`BarTimeText`, PBUI:1910-1978) use a bound text channel. Clicking the right label toggles remaining vs total. `BarLiveSlot` (PBUI:1983-2029) shows a LIVE mark with a red dot, or a "GO LIVE −m:ss" text button.

**Podcast variant**:
- Layout from `PodcastBarLayout` (PB:455-468): no shuffle/repeat, no volume slider, Prev/Next only at Wide and up, times only at Comfortable and up, heart only at Medium and up, `LeftW` narrowed.
- `BarSeekStepButton` ±10 s (PBPod:111-126).
- `BarEpisodeSpeed` with the speed panel flyout: slider 0.5-3× plus 1/1.5/2/3 presets (PBPod:130-184).
- Save glyph instead of heart; "Transcript" label instead of "Lyrics" (PBUI:577, 1266).
- Artists line becomes a single show link (PBUI:951-959).
- At the Minimal tier the left cluster shows art only (PBUI:441).
- The file also holds the bar-wide keyboard scrub ladder state (PBPod:30-109).

**Chapters variant** (`BarChapterTimeline`, PBCh:14-111):
- Mounted only for an episode on a Track-mode rail (PBUI:1627-1634).
- Tick marks are drawn with transforms bound to the rail width.
- Hovering shows a chapter tip popup (`PopupChrome.Static`, acrylic) with the timestamp, title and range.
- Pure decoration; it takes no input.

**What drops under width pressure** (flag → minimum tier, from Shell.cs:1281-1293; anything dropped moves to the overflow, PB:330-344):

| Flag | Present from |
|---|---|
| `ShowExpand` | Full |
| `ShowQueue`, `ShowVolumeSlider` (and the inline video split) | Wide |
| `ShowShuffleRepeat`, `ShowRemoteDeviceLine` | Comfortable |
| `ShowVolumeButton`, `ShowLyrics`, `ShowTimesElapsed`, `ShowPrevNext` | Medium |
| `ShowTimesRemaining` | Compact |
| `ShowDevices`, `ShowLikeSlot`, `ShowSubtitle` | always |

### 1.2 Rail (right side)

**Mount point.** `Rail.Frame()` at RUI:42 wraps `FrameCore` (RUI:55-113). The frame mounts it at Shell.UI.cs:616.

**Modes** (Shell.cs:1065): `RailMode` {Lyrics, Queue, NowPlaying, Friends, Video}. Open/mode state lives in `Shell.Ui` (Shell.cs:1067-1132). `Ui.Toggle` (Shell.cs:1127) closes the rail if that mode is already showing.

**Two arms** (RUI:106-111):

*NowPlaying arm*: `PinnedHero` (RUI:182-189) is either the docked video cap or `HeroTile` (RUI:354-388), with an optional Art|Video toggle (RUI:193-214). Below it, `NowPlayingBody` (RUI:483-535) contains:
- `HeroMeta`: title, artist and album links, `SaveButton`, and the lyrics peek (RUI:538-596);
- About the artist (RUI:604), Top cities (RUI:609), Merch (RUI:646), a loading skeleton (RUI:680);
- "Next up", at most 5 rows (RUI:701-768).

*Other arms*: `DockedCap` (RUI:142-168, with a vertical splitter), then a header, then a body:
- `Header` (RUI:232-251); the Video mode header adds mini-player and fullscreen buttons.
- `LyricsHeader` (RUI:256-275): secondary-line globe, AI sparkle, expand-to-stage, the inspector button, close.
- Bodies (RUI:127-136):
  - Lyrics → `Lyrics.View()`.
  - Queue → the injected `QueueBody` (RUI:46), or a Vacancy if none is installed.
  - Friends → `FriendsBody` (RUI:834-877; row at 888-998; skeleton at 1008).
  - Video → `VideoBody` (RUI:772-801; "no video for this song" placeholder at RUI:807).
- `BodyModeFor` (R:97) swaps Video/NowPlaying to Queue when the page stage hosts the video.

**Hero header row** (`HeaderRow`, RUI:396-470): eyebrow, then the Cover | ‹Player› `SelectorBar` (developer-only), then a gear button that opens the style flyout `Popup` (BottomEdgeAlignedRight).

**Styles** (RSt). The style flyout `StyleFlyoutCore` (RSt:48-68) is 340 wide and shows:
- three group rows of four `ThumbCard`s (RSt:77-116), labelled Media / Devices / Software;
- option rows under them (RSt:119-171), either a compact `Segmented` or swatch dots.

The art context menu `ArtMenu` (RSt:184-198) offers Cover / ‹Player› radios plus "Player style…". The 12 mini-art thumbnails are `Thumbnail` (RSt:204-285), which uses literal hex colours.

The **12 styles** are the catalog in R:120-186:

| Group | Styles and their options |
|---|---|
| Media | Record (finish, size, rpm, sleeve); Cassette (shell, label, side); Reel (reels); CD (format cd/md) |
| Devices | Turntable (finish, size, rpm, sleeve); iPod (body, lcd); Winamp (skin); VU (face, ballistics) |
| Software | Zune (accent swatch, rpm); WMP (preset, colour); Canvas (drift, bleed); Picture (size, rpm, sleeve) |

**Hero geometry** (`Rail.Hero`, R:344-361): side = round((railW − 16) / 4) · 4.

**Friends feed** (R:368-426): surfaces Rows / Skeleton / Empty / Offline / Error; the "live" window is 120 s; refresh tick every 30 s.

### 1.3 History page

`HistoryPage()` (Hist:35) wraps `HistoryPageView` (Hist:39-96):
- **Header** (Hist:100-158): clock icon, title, two stat pills, "Clear all" behind a `Controls.Confirm`, a search `AutoSuggestBox`, filter `SelectorBar` chips, and a sort `ComboBox`.
- **Bodies**:
  - Empty: three variants (Hist:176-186).
  - Date-grouped cards (Hist:190-213).
  - Most-visited single card (Hist:217-233).
- **Row** (Hist:266-351): 56 DIP; icon cell with an accent tint for playlists, title, kind label, timestamp, ×N badge, delete button. An unreachable route renders at 0.6 opacity and is inert.

### 1.4 Device picker

- **Pure items** (DP:37-80): `DevicePicker.Items`, `IsRemote`, `NeedsTakeOver`, `DeviceIntent` (DP:31).
- **Row composition**: `DevicePickerRows` in Shell.cs §7.1 (from Shell.cs:1326).
- **Rendering**: PBUI:1438-1488, as a `MenuFlyout` with headers drawn as disabled rows, radio rows for Connect devices, and plain command rows for local outputs.
- The same file also holds `PlaybackFailureToasts.Between` (DP:88-97) and `SkipRule.Facts` (DP:105-110).

### 1.5 Overlays

`Shell.Overlays()` (Ov:48) wraps `OverlaysLayer` (Ov:83-135): a full-window, hit-test pass-through column. It contains:

| Child | Location | Notes |
|---|---|---|
| `RuntimeBannerChrome` | Ov:141-199 | Floating InfoBar warning with a "Set up" button that opens `Setup.OpenRuntimeCard` |
| `UpdateToastWatcher` | Ov:325-375 | Through `Notify.Say`; has a custom download progress card |
| `RuntimeToastWatcher` | Ov:204-221 | |
| `PlaybackFailureToastWatcher` | Ov:227-249 | |
| `Setup.SignInDoor()`, `Setup.WizardChrome()`, `Feedback.Chrome()`, `Crash.Chrome()`, `ReleaseNotes.AfterUpdateChrome()` | Ov:123-131 | Mount order matters |

Dialogs and flyouts in the same file:
- **Profile chip** `ProfileChip()` (Ov:53) wraps `ProfileChipCore` (Ov:398-463). Its menu `ProfileMenuContent` (Ov:477-537) has an account header (Ov:543), a tier line (Ov:567) and rows Profile, Settings, Play ▸ (Ov:583), Notifications, Friends, Theme, Log out.
- **Log-out confirm** `ConfirmLogout` (Ov:616): a modal card.
- **Play-a-link dialog** `OpenPlayLink` / `PlayLinkCardCore` (Ov:662, Ov:710-788).
- **Signature dialog** `OpenSignatureDialog` (Ov:256), a `ContentDialog`.
- **Teaching tips** `TryShowTip` / `CloseTip` / `AcknowledgeTip` (Ov:799-855).

Not in this file:
- The drag scrim and drag preview: `FileDropLayer()`, `CoverScrim`, `DragPreviewLayer` at Shell.UI.cs:500-503.
- The toast lane belongs to Notify; this file only decides when to show toasts.

### 1.6 Masthead, material layer and omnibar

- **Masthead band** (`MastheadBand`, Mh:41-87):
  - The breadcrumb trail rendered as the title (Mh:92-132), plus an optional "Show all" tools button.
  - It keeps the last trail and fades when the route family is unknown.
  - Mounted at Shell.UI.cs:954.
- **Material layer** (`MaterialLayerView`, Mh:141-213): a tint box with a brush transition. Home gets three radial washes (hero, weekly, mix) keyed by artwork and clipped above the dock line. Mounted at Shell.UI.cs:481.
- **Omnibar**:
  - Shared state: `OmnibarStore` / `s_omnibar` (Mh:219-237).
  - `CenterIsland` (Mh:249) decides field vs nothing from `ChromeLayout.SearchMode`.
  - Field mode: `SearchField` (Mh:256-308).
  - Icon mode: `SearchFlyoutButtonView` (Mh:310-376), with a flyout placed BottomEdgeAlignedRight.
  - `RichOmnibar` (Mh:380-502): an `AutoSuggestBox` with a 150 ms debounced fetch through `Omnibar.Source`, falling back to the history log.
  - `SuggestionsPopup` (Mh:520-593): query rows plus a divider plus rich rows. States: Empty → "No results found"; Failed → Retry; Pending → indeterminate bar.
  - `OmniRow` (Mh:597-674): a slot-mode `Controls.SlotSurface` with play, menu and drag.
  - `OmniNotice` (Mh:677) and query-text match bolding (Mh:708).

### 1.7 Omnibar row rules

`OmnibarRowRules` (OmR:13-44): `EntityKindOf`, `MenuOf` (None/Track/Container), `Drags`, and `RowShape` (44 DIP art in a 58 DIP row).

### 1.8 Command palette (Ctrl+K)

- **Pure** (Pal:38-255):
  - Enums at Pal:38-44.
  - `PaletteEntry` (Pal:48).
  - `CommandTable.Builtins` (29 rows: Navigate, Playback, Settings, Library, Home facets).
  - `BuildIndex` merges registry actions.
  - `Filter` with scoring (prefix 0, contains 1, subsequence 2), the `>` prefix for commands only, and a "Search for…" row.
- **Shell** (Pal:259-371): `ToggleTheme` (Pal:267), `ZoomStep` (Pal:278), `InvokePaletteEntry` (Pal:282), `InvokeHomeFollowing` (Pal:355).
- **UI** (Pal:375-539): `CommandPalette()` lane (Pal:385, a `Popup` bound to `PaletteOpen` with a 64 DIP top pad and 560 width) and `PaletteCard` (Pal:403: `TextBox` plus up to 8 rows, with Up/Down/Enter/Escape).

### 1.9 Deck persistence (Shell.Host.Deck.cs)

`AttachPlayback` (HDeck:50), `OnPersistDeck` (HDeck:67), `CaptureDeckForExit` (HDeck:71), `DeckDto` (HDeck:81), `RecordPlay` (HDeck:102), and `SessionDeck` (HDeck:120-289; `MaxRows` 200, `HistoryKeep` 10).

### 1.10 Playback files (what the bar and cards subscribe to)

- **Context toggle**: `ContextPlayRules.For` (CtxT:25-31) and `Playback.PlayOrToggleContext` (CtxT:37-54). Used by omnibar rows (Mh:628, 631, 634).
- **Card seam**: `CardRelation` (Seam:17-28); `Playback.HasCardContext` signal (Seam:34); `InstallCardSeam` sets up `Controls.NowPlaying` (Seam:40-41). Read by `Controls.ShowsPause` (Cta:474-485).
- **Endgame**: `Playback.Audio.EndgamePlan.Decide` (End:53-77). Pure audio logic, no presentation.
- **Glitch**: `GlitchLedger` (Gl:17-136) and `DeviceUnderrunBaseline` (Gl:141-156). The verdict loc keys (Gl:21-22) feed the Diagnostics "Playback health" card, not these surfaces.

### 1.11 Platform controls

- **Picker** (Pick):
  - `PickerInk` (Pick:42), `PickerShell` presets (Pick:54-68), `PickerCard` (Pick:77), `PickerLabel` (Pick:103), `PickerTitled` (Pick:114).
  - Miniatures (Pick:126-212).
  - `PickerStrip`, a radio group (Pick:258).
  - `EqualizerCurve` / `EqCurveCore` / `EqGridLayer` (Pick:290-664).
- **Podcast** (Pod): `Chip` (Pod:67), `LedgerBar` / `LedgerHost` (Pod:97-212), `StarRow` (Pod:226), `Door` (Pod:272-402), `FindBox` (Pod:412).
- **CTA** (Cta):
  - `PrimaryButton` / `PlayButton` / `AccentIconButton` / `OnMediaPalette` / `AccentToggleStyle` (Cta:107-188).
  - The legacy pill set `Play` / `Accent` / `Pill` / `IconPill`, marked "DELETE in Wave 5" (Cta:196-284).
  - `IconAction` (Cta:288), `PlayFab` (Cta:308), `CoverActionFab` (Cta:337), `TextAction` (Cta:397), `CtaPalette` (Cta:447), `ShowsPause` (Cta:474), `ContextPlayButton` (Cta:491-511).
- **Container menu** (MC): `ContainerMenuRules` (MC:40-93), `ContainerExtras` (MC:97-120), `Menus.Container` (MC:126-160).

---

## 2. States

### Player bar
- **Data and connection states** (`PlayerState`, PB:34, folded at PB:142-148):
  - Title text (PBUI:778-786): NoTrack → "Nothing playing"; Loading → "Loading…"; Reconnecting → "Reconnecting"; Error → a per-fault title (PB:369-378).
  - Title ink (PB:200; PBUI:763-768): secondary or critical (`BarCriticalInk`, PBUI:149).
  - The artists line is absent for NoTrack and Error (PB:208).
  - The primary button becomes Retry on error and is disabled for NoTrack or an unsupported format (PB:171-179).
  - There is no unauthenticated state in the bar; sign-in is handled by `Setup.SignInDoor` (Ov:123).
- **Playing / paused / buffering**: the pause glyph keeps showing through a load (PB:217; PBUI:298-302). The top-edge sweep appears after 450 ms, or immediately while reconnecting (PB:226-229; PBUI:634-644). The rail ticks only while advancing (PB:487-500).
- **Face hold on skip**: the outgoing face is held up to 450 ms (PB:129-133; PBUI:711-757). While held, the heart is lit but inert (PBUI:278).
- **Remote device**: the "Playing on" line appears and the Devices button latches (PB:388-390; PBUI:322-325, 1353).
- **Media type**:
  - Podcast vs music: §1.1.
  - Video: the split button exists only when the track has a video, and its face lights only when Active (PB:241, 304); it latches while video is showing (PBUI:589).
  - Live vs DVR vs track rail: PB:478; PBUI:1625, 1647, 1951.
- **Fullscreen**:
  - Video fullscreen hides the bar through bound `Visible` (Shell.UI.cs:471).
  - `OwnsTransport` false empties the centre (PB:233; PBUI:461-492).
  - The stage door is Expand (Full tier) or the overflow "Full screen" row (PBUI:603-609, 1272-1274).
- **Window tiers**: six tiers, §1.1.
- **Interaction states**:
  - `BarButton` hover/press fill and scale with an 83 ms brush transition (PBUI:1113-1130).
  - Primary button fills: Accent, AccentSecondary, AccentTertiary (PBUI:1173-1177).
  - The seek thumb appears on hover/press (PBUI:1683).
  - The title shows link ink on hover (PBUI:327-329, 365-366); meta links use their own hover (PBUI:1035-1050).
  - Focus: the root is focusable with `FocusVisualMargin` (PBUI:651); buttons use `AllowFocusOnInteraction = false`.
- **Menus and flyouts opened from the bar**: overflow (PBUI:1226), video placement (PBUI:535), "+N" artists (PBUI:1083), volume popup (PBUI:1314), device picker (PBUI:1378), episode speed (PBPod:141), scrub readout (PBUI:1837), chapter tip (PBCh:51), context menu (PBUI:457).

### Rail
- Open vs closed: a translateX slide, and hit-testing is off while closed (RUI:76-88, 118).
- Docked vs floating (`RailFits`): RUI:64, 93, 103, 119-121.
- Nothing playing: an empty `HeroTile` (RUI:366) and a NowPlaying Vacancy (RUI:499-501).
- Artist loading: skeleton (RUI:518, 680).
- Queue empty: RUI:131-132, 792-794.
- Friends: Rows, Skeleton, Offline, Error with Retry, Empty (RUI:851-875).
- Video unknown vs none: RUI:782.
- Stage hosts the video: falls back to Queue (R:97; RUI:70-73).
- Dev switch on or off: RUI:459-467.
- Style flyout open: the gear is lit (RUI:430, 435).
- Lyrics secondary-line availability: RUI:264-267.

### History
Empty (search / filter / nothing logged): Hist:176-186. Unreachable rows: 0.6 opacity (Hist:333). Sort: recent vs visited (Hist:62, 71-77).

### Overlays
- Banner shown or hidden (Ov:152, 178).
- Profile identity unknown → skeleton (Ov:418-420, 449-451, 547-560).
- Tier hidden when unknown (Ov:551).
- Store-shot presenting → generic chip (Ov:413-414).
- Play-link: busy / "no owner" / failed with "Open in browser" (Ov:721-765).

### Omnibar
- Query states Idle / Pending / Empty / Failed (Mh:550-557, 572).
- Highlight row (Mh:581, 611).
- Field vs icon mode (Mh:249-252).

### Palette
No matches (Pal:468-478); the selected row shows an accent glyph and "Enter" (Pal:486-516).

### Animation and motion APIs used
- `LayoutTransition`:
  - `BarMoveMotion`: Bounds, Tween Fast with `FluentPopOpen`, `SizeMode.Reveal` (PBUI:107-108).
  - `BarItemMotion`: Bounds and Opacity, `Reflow`, opacity enter/exit, Standard in / Fast out (PBUI:116-122).
  - `BarFaceMotion`: Opacity, `SizeMode.Auto` (PBUI:1152-1153).
  - `LayoutTransition.Slide` on friend rows (RUI:985).
- `MotionTokenDef.Eased(..., ReducedMotionPolicy.KeepFade / SnapEnd)`: PBUI:139-140; Mh:43-44; Pod:148.
- Imperative animation through `Context.Anim`:
  - `Animate(...Opacity)` for the face swap (PBUI:409-410).
  - `IconSwapIn` for the heart pop (PBUI:316).
  - `Animate(...TranslateX, 300 ms, CubicBezier(0, .35, .15, 1))` for the rail slide (RUI:83, 87).
  - `SeedValue` FLIP for the ledger bar (Pod:208-209).
- `UseKeyframes(AnimChannel.Opacity)` for the live-line breathing (PBUI:1880).
- `Transition = Show/Hide` on the masthead (Mh:81).
- `Enter`/`Exit` `EnterExit`: masthead washes (Mh:209-210) and friend rows (RUI:983-984).
- `WhileHover` with `MotionTok.ControlFast` on the podcast door (Pod:342).
- `HoverScale` / `PressScale` from `Design.Motion.ScaleEmphatic` / `ScaleSubtle` / `ScaleStandard`: PBUI:1125, 1177, 1237; Cta:230, 279, 294, 318; Pod:245, 382; Pick:92; RUI:980.
- `BrushTransitionMs` (PBUI:1124; Mh:159; RUI:951), `HoverDurationMs` / `PressDurationMs` (PBUI:1697).
- `Marquee` in PingPong with `SyncCycle`: 14 s cycle, 3 s end pause, 2 s start delay, speed 18 (PBUI:145, 340-353).
- `ProgressBar.Indeterminate` (PBUI:637; Mh:572).
- Shared-element entry via `SharedTransition.Begin` into `Stage.Open` (PBUI:214, 608, 1274; RUI:260, 270).
- Theme flip transition `ThemeControl.Request` (Pal:272; Ov:403).

---

## 3. Logic vs presentation split

**Intended split.** The `X.cs` files are meant to be pure and engine-free (PB, R, DP, PC, OmR, HDeck, CtxT, Seam, End, Gl). The `X.UI.cs` files render. The places where that split is broken are listed below.

### UI files that hold logic, state or persistence

- **PBUI**
  - Shared verbs used app-wide: `TogglePlayPause`, `ToggleShuffle`, `CycleRepeat`, `TransportFacts`, `RetryPlayback` (PBUI:56-81).
  - Static mutable `s_barVolume` mirror (PBUI:154); the tier change log (PBUI:182).
  - A render effect fires the `Spotify.Podcasts.ReadSavedAsync` network read (PBUI:270-274).
  - The `BarFaceHold` static mutable store (PBUI:711-737), written from layout effects (PBUI:283-296).
  - Data and route resolution (PBUI:691-886).
  - Library mutation in `BarToggleLike` (PBUI:876-886).
  - Async drag-drop resolve, queue insert and toasts (PBUI:897-933).
  - `Playback.PickerOpened` side effect (PBUI:1377).
  - Settings read for quality (PBUI:1429-1431).
  - Device intent execution, which persists the output and transfers playback (PBUI:1473-1488).
  - The scrub gesture state machine and seek-generation hold (PBUI:1546-1825).
- **PBPod**: the keyboard scrub ladder (static mutable state at PBPod:36-48, `BarKeyStep` / `KeyLadderTick` commit logic at PBPod:51-93) and `BarSeekBy` (PBPod:21-28). This is bar-wide logic sitting in a file named after podcasts.
- **PBCh**: chapter resource lease acquisition and normalisation (PBCh:31-43).
- **RUI**
  - `CommitCap` persists `ShellDockedVideoHeight` (RUI:172-179).
  - `ShowVideoAt` mutates `Video.State` (RUI:217-222).
  - `CloseButton` writes `Shell.Ui.RailOpen` (RUI:314).
  - `HeroWashColor` is a public colour derivation also used by RSt and the deck (RUI:340-345).
  - `Entities.Ensure` demand (RUI:493-497).
  - Queue windowing (RUI:701-719).
  - `Friends.SetActive` host seam (RUI:838).
- **RSt**: a per-render `Signal<int>` mirror (RSt:134); preference writes (RSt:101, 135, 159, 187, 192, 194).
- **Ov**
  - Composition-root wiring of `Actions.Services` (Confirm, Clipboard, OpenExternal, Post), `OnPlayLink`, `Track.DrawerOverlay`, `OnReportRequested` (Ov:93-110).
  - Banner and toast gating with logging (Ov:147-176, 204-249).
  - Static update-toast state `s_previous` / `s_progress` (Ov:330-331).
  - `RunUpdateAction` (Ov:381).
  - `Spotify.Logout` (Ov:635).
  - The async router `PlayLinkDirect` via `Task.Run` (Ov:678-706).
  - The static teaching-tip slot `s_tipHandle` (Ov:792).
- **Mh**
  - Static omnibar store and focus tickets (Mh:219-237).
  - Fetch with cancellation (Mh:477-501).
  - `ChooseOmnibarItem` play/navigate decision (Mh:506-516).
  - Row play decision via `ContextPlayRules` (Mh:620-635).
- **Hist**: a non-reactive `Settings.Get(DeveloperMode)` (Hist:65); destructive `Store.Clear` / `Remove` (Hist:133, 324).
- **Pal** mixes regions by design (Pal:10-16): `ToggleTheme` persists the theme mode (Pal:271), and the palette's crossfade row writes settings and calls `Playback.Audio.SetCrossfade` (Pal:306-310).
- **Cta**: `ShowsPause`, a playback-relation decision (Cta:474-481).
- **Pod**: pure `LedgerPartsOf` / `LedgerGeometry` sit in the UI file (Pod:102-139).

### Logic files that build visuals or carry presentation constants

- **PB**
  - Imports `FluentGpu.Controls` (PB:21).
  - Icon glyph choices: `RepeatGlyph` (PB:354), `ConnectGlyph` / `LocalGlyph` (PB:401-418).
  - Display strings: `VolumePercent` (PB:357); loc keys in `FaultTitleKey` (PB:369-378).
  - Layout geometry: `SlotWidth` / `RightWidth` (PB:273-296), `PickerMaxHeight` (PB:423-431), `TimeLabel.SlotW` / `LiveSlotW` (PB:594-597), `ThumbX` (PB:585), `PodcastBarLayout` (PB:455-468).
- **Shell.cs §7**: per-tier metrics and spacing (Shell.cs:1294-1317).
- **R**: swatch ARGB colours and loc keys in the catalog (R:130-131, 170-171); `Hero` geometry (R:344-361).
- **Pal**'s "pure" table carries icon glyphs and `Loc.Get` (Pal:84-114).
- **OmR** is labelled "Role: UI" but is a rules file; `RowShape` is a `SurfaceShape` (OmR:43).
- **Seam** installs a UI control seam, `Controls.NowPlaying` (Seam:40-41).
- **DP, HDeck, PC, End, Gl** contain no visuals.

---

## 4. Presentation switches

### 4.1 Persisted switches

All are read with `Platform.Settings.Get`, which is synchronous (Platform.cs:970; registry `RegistryAppSettings`, Platform.Host.cs:500; file `FileAppSettings`, Platform.Host.cs:435). The store is opened in `Platform.Boot` before any window (Platform.cs:1137-1147), so every one of these is available before the first frame.

| # | Key (definition) | Reader / writer | Branch sites (count) |
|---|---|---|---|
| S1 | `appearance.marquee.enabled` (Platform.cs:142, default true) | `Prefs.Appearance.Marquee()` (Prefs.cs:73-77, epoch); read at PBUI:222 | PBUI:339 title marquee vs box; PBUI:378 marquee vs `TrimTip`; PBUI:380-391 artists marquee vs compact line. **3** in scope (plus Track.Table.cs:612 outside) |
| S2 | `playerbar.duration.remaining` (Platform.cs:184, true) | `Prefs.PlayerBar.ShowRemaining` / `ToggleRemaining` (Prefs.Player.cs:129-140); read PBUI:1940, written PBUI:1968 | PB:617 (remaining vs total). **1** |
| S3 | `appearance.trackArtwork.hidden` (Platform.cs:119) | `Prefs.Appearance.TrackArtworkHidden` (Prefs.cs:66); read RUI:732 | RUI:760 `ShowArtwork`, RUI:763 `ArtEdge`, RUI:766 key. **1** read / 3 uses in scope (about 10 more outside: Queue.UI, Recents.UI, Artist.Page, etc.) |
| S4 | `npv.presentation` (Platform.cs:254) — only effective in developer mode (`diag.developerMode`, Platform.cs:335, loaded into the `Platform.Developer.Enabled` signal at Platform.Settings.cs:134) and not during the Store shot | `Prefs.NpvPlayer.Presentation` / `ResolvedPresentation` (Prefs.Player.cs:55-64); `Rail.PlayerPrefs` (R:250-259); reads RUI:362, 426; writers RUI:463, RSt:192, 194, Pal:315, Settings.UI.Appearance.cs:176 | RUI:368 deck vs cover image; RUI:459 switch composed or not; RSt:188 menu radios; RSt:191/193 checked states. **5** |
| S5 | `npv.player.style` (Platform.cs:255), 12 presets | `PlayerPrefs.Style` / `CurrentPreset` (R:262-265); reads RUI:364, 427, RSt:52; writers RSt:101, Pal:318, Settings.UI.Appearance.cs:758 | In scope: RUI:369 (slug@side remount key), RUI:433 / RSt:193 labels, RSt:56-58/82 selected card, RSt:61 option rows. **About 6**. Deck consumers: the 12-way switch at Deck.UI.cs:201-212, Deck.UI.cs:157, 160; Deck.cs:1448-1457, 1466-1468, 1476-1477, 1486 |
| S6 | `npv.player.<preset>.<option>` (Platform.cs:379) | `PlayerPrefs.Choice` / `ChoiceSlug` (R:267-275); writers RSt:135, 159 | RSt:134, 143, 149-152 (album-colour bound fill), 157. **4** in scope. Deck consumers: Deck.Faces.cs:48-50, 263, 378, 531-532, 789, 985, 1198-1200, 1345-1346; Deck.UI.cs:164, 172, 803-806 |
| S7 | `playback.quality` (Platform.cs:214) | PBUI:1430 | Picker quality echo row. **1** |
| S8 | `shell.rail.docked-video.height` (Platform.cs:174) | Written RUI:178; seeded Shell.UI.cs:127 | Geometry only, **0** branches |
| S9 | `diag.developerMode` (Platform.cs:335) | Direct, non-reactive read at Hist:65; via signal at R:259 | Hist:271, 333-338 (route reachability). **1** |
| S10 | `playback.runtime.dismissed`, `setup.pending` (Platform.cs:210, 101) | Ov:150-151, 212, 215 | Ov:152, 178 (banner); Ov:213-215 (toast). **2** |
| S11 | `theme.mode` (Platform.cs:111) | Written Pal:271 | See T1 |
| S12 | `playback.crossfade.enabled` | Pal:307-309 | A palette toggle, not a presentation branch |

**Session-only switches:**
- Rail open/mode: session.json, restored at Shell.UI.cs:128-131, captured at Shell.Host.cs:1377-1386.
- Rail width (`ShellRailWidth`): Shell.UI.cs:125, 775.
- Lyrics secondary line: not persisted (Prefs.cs:152-154, 204); read at RUI:262 and branched at RUI:264-267.

### 4.2 Derived (not persisted) switches

- **Width tiers (T0).** Defined at Shell.cs:1195-1323; resolved at PBUI:169-184. Not persisted. The first render peeks the viewport width (PBUI:170-171); if it is 0 the tier is `Minimal` until the first measured width arrives.
  - Flag branch sites:
    - `ShowExpand`: PB:266, 339
    - `ShowDevices`: PB:265
    - `ShowQueue`: PB:241, 264, 337, 341
    - `ShowVolumeSlider`: PB:261; PBUI:563
    - `ShowShuffleRepeat`: PB:259, 335
    - `ShowLikeSlot`: PB:316; PBUI:441
    - `ShowVolumeButton`: PB:260, 320
    - `ShowLyrics`: PB:262, 336
    - `ShowRemoteDeviceLine`: PBUI:322
    - `ShowTimesElapsed`: PBUI:483
    - `ShowTimesRemaining`: PBUI:491
    - `ShowPrevNext`: PB:334; PBUI:461, 471
    - `ShowSubtitle`: PB:208; PBUI:379
    - Tier compares: PB:461-465; PBUI:441
  - Metric uses: PBUI:319, 416, 425, 440, 447, 469, 497, 515, 627, 637, 677-678; PB:295.
- **Podcast variant (V1).** `isEpisode` at PBUI:220-221; branches at PBUI:263-268, 272, 428, 432-434, 441, 464, 475, 577, 951, 1266, 1629, 1832; PB:455-468; RUI:270, 284.
- **Video and transport ownership (V2).** PB:233-234, 241, 304, 341; PBUI:254-255, 260, 461-492, 578-596, 589-590.
- **Live / DVR (V3).** PB:478-479; PBUI:1607, 1625, 1647, 1951, 1991.
- **Rail docked vs floating (V4).** RUI:64, 93, 103, 119-121. Rail body mode: RUI:106-111, 127-136, 234.
- **Light/dark theme (T1).** Theme is persisted by S11 and applied before the first frame. Six branch sites:
  - PBUI:643 — top hairline: divider token in dark, black at 0x0F alpha in light;
  - RUI:344 — hero wash lerp 0.18 vs 0.10;
  - Ov:394 — premium gold ink;
  - Ov:482 and 514 — theme menu row target label;
  - Mh:151 — wash alphas, applied at Mh:165-167;
  - Pal:269 — toggle direction.
- **Accent and wash.** The rail hero wash comes from the cover palette (`Design.SchemeFor`, `Palette.Watch`, RUI:340-345). WMP thumbnails use the accent (RSt:262). Podcast tone uses a `Func<ColorF>` (Pod:67-71, 340, 380). CTA accents come from `Button.ButtonPalette.ForAccent` (Cta:117, 141). Picker ink is accent-based (Pick:45-47). The masthead wash comes from `MaterialState` (Mh:149-167).
- **Reduced motion (M1).** It is read live from `FgMotion.ReducedMotion` (Design.cs:1554). Branches at PBUI:1879 and Mh:145; as a token value at PBUI:140, Mh:43-44, Pod:148.
- **Presentation mode (Store shot).** Ov:413-414, 447; R:259.
- **Chrome layout.** Ov:408, 430, 447, 457; Mh:250, 296.
- **Density.** There is no density switch in these files. Row density lives elsewhere; `PickerDensityRows` (Pick:146) only draws its preview.

---

## 5. How these surfaces reach into pages, entities and actions

**Navigation**
- `Shell.GoTo` / `For` / `LinkFor` / `Parse`:
  - PBUI:363, 418, 853, 856, 868, 873, 958, 977, 991, 1041, 1081
  - RUI:347, 902-904, 941
  - Hist:334
  - Ov:387, 492, 496
  - Mh:102, 437, 449, 515
  - Pal:287, 290, 370
  - MC:208
- `Stage.Open` (PBUI:608, 1274; RUI:270), `Stage.NowPlayingMenu` (PBUI:457).
- `Ui.Toggle(RailMode)` (PBUI:575, 599, 1267-1271; Ov:511).
- `Sidebar.EnterEdit` / `SwitchLayout` (Pal:325, 335).

**Entities**
- `Entities.ScopeEpoch`, `TableFor`, and `Entities.Current.{Tracks, Episodes, Shows, Artists, Albums, Users, Edges.Queue/Friends/ArtistCities}.Changed`:
  - PBUI:269, 695-699, 796-806, 1007-1015
  - RUI:323-334, 503-507, 730-731, 777-779, 844-848
  - Ov:409-410
- `Entities.Ensure` (RUI:495-496), `Entities.Strings.Resolve` / `Intern` (RUI:618, 653, 892-897; Ov:473; Pal:245, 359, 370), `Entities.Track` (HDeck:110).
- Row handles `Track`, `Episode`, `Artist`, `Album`, `User`, `Playlist`, `Merch` are used throughout.
- `Queue.UpNext` / `RefAt` / `Rows` / `PlayItem` / `PackedRefs` (RUI:703-758; HDeck:84-85).
- `Spotify.Podcasts` (PBUI:266-273, 881); `Controls.Library` (PBUI:267, 884); `Spotify.Logout` (Ov:635); `Spotify.Current.Tier` (Ov:432).

**Actions**
- `Actions.Services` (PBUI:911; Ov:95-104, 608, 687).
- `Actions.ProfileRules` (Ov:415, 480, 514, 551); `Actions.PlayLinkRules` (Ov:666 and following).
- `Actions.Registry.Current` (Pal:345, 415); `ActionIcons` (PBUI:428; Pal:378; MC:207).
- `AppActions` / `ActionId` / `Actions.Menu` (MC:131-216).
- `Track.Menu` / `Track.Invoke` / `Track.RowData` / `Track.EnsureActions` (RUI:759-760, 960; Mh:627, 641-642).
- `Menus.Container` (RUI:969; Mh:644).
- `Notify.Say` (PBUI:913, 928; Ov:216, 240, 243, 260, 355, 684, 695, 702, 756; Pal:332).

**Playback** (the main signals the bar reads)
- `Current`, `CurrentId`, `PhaseSignal`, `Buffering`, `Recovery`, `Error`, `IsPlaying`, `Shuffle`, `Repeat`, `Volume`, `Muted`.
- `PositionMs`, `DurationMs`, `Live`, `IsBehindLive`, `TunedInAtMs`, `ScrubPositionMs`, `CanSeek`, `SeekGen`, `LastSeekLandedGen`.
- `OwnerSignal`, `ActiveDeviceSlot`, `Devices.*`, `Audio.*`, `DevicePickerRequest`, `EpisodeSpeed`.
- `PrevAllowedByContext`, `NextAllowedByContext`, `ContextUri`.
- Verbs: `TogglePlay`, `Previous`, `Next`, `Scrub*`, `SeekTo`, `SetVolume`, `ToggleMute`, `TransferTo`, `TakeOver`, `GoLive`.
- Key reads at PBUI:68-71, 234-242, 1601-1609, 1725-1824.

**Shell members these surfaces depend on**
- Rail state: `Shell.Ui.{RailOpen, Mode, RailWidth, RailFits, ActiveStagePlayable, DockedVideoHeight, DockedVideoHeightPinned, RailBodyHeight, Toggle}` (Shell.cs:1067-1132).
- Player bar layout and device rows: `PlayerBarLayout` / `PlayerBar` / `PlayerBarTier` (Shell.cs:1195-1323) and `DevicePickerRows` (Shell.cs §7.1).
- Rail clamps: `ClampDockedVideoHeight` / `DockedVideoNaturalH` / `DockedCapCeiling` (Shell.cs:1151-1178).
- Navigation and chrome: `Current`, `Mastheads`, `Origins`, `TryMasthead`, `MaterialState` (Shell.UI.cs:114), `ChromeLayout` / `SearchFocusRequest` / `PaletteOpen` (Shell.Host.cs:67, 80, 83), `SearchText`, `Omnibar.*`, `History.*`, `Dest` / `IsKnown` / `ArgOf` / `NameOf`, `SetPage`, `SeedPalette`.
- Seams: `OnPlayContext`, `OnPlayLink`, `OnReportRequested`, `NotificationsLauncher`, `PickAndPlayFile`, `LinkModules`, `MatchLink`, `ReportDialogOpener`, `OnNewPlaylist` / `OnNewFolder`.
- Session: `Session.CaptureDeck` / `DeckSection` / `ShellSection`.
- Cross-surface: `Video.{State, PlacementCore, PlacementMenu, DockedCap, DockedHosting}`, `Lyrics.View` / `NpvPeek`, `Deck.View`, `AiLyrics.HeaderButton`, `Diagnostics.LyricsInspector` / `StoreShot`, `Setup.*`, `Crash` / `Feedback` / `ReleaseNotes.Chrome`, `Tips`.


# Part 3 — Sidebar family


> Agent summary: (1) **Layout** (`sidebar.layout.id`, Classic or Library): 24 comparison sites inside the family plus 4 `Config.Layout` consumers; read synchronously in `Sidebar.Boot`. (2) **Density** (`sidebar.pane.density`): 1 root branch (`ShapeFor`) fanning out to ~24 row-shape sites; 13 sites hardcode the two-line shape. (3) **Library View** (List or Grid, `"view"` in `sidebar.json`): 13 sites; the only "cards vs rows" switch. (4) **Pane mode** (Expanded, Compact rail, Minimal drawer): derived from the window band, not persisted; ~24 planner sites, ~17 slot sites, `InDrawer` ~31 sites. (5) **Pane width** (`sidebar.pane.width`, 180–460): ~10 sites incl. 3 width tiers. (6) Smaller: library chip, ShowLiked, user collapse, Edit mode. (7) **Store versions/migration are one-time only; no runtime branch on V2 vs V3.** (8) Not found on main: "Show covers" (0 hits), no Style/Cards toggle.

Path legend: `<S>` = `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\Shell\`, `<W>` = `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\`. So `Sidebar.UI.cs:85` means `<S>Sidebar.UI.cs` line 85.

Docs: `C:\wavee\fgpu\WaveeMusic\docs\guide\sidebar.md` (220 lines); `C:\wavee\fgpu\WaveeMusic\.claude\skills\wavee-sidebar\SKILL.md` (60 lines). The skill's rule 1 ("one renderer, no layout branches inside it", SKILL.md:33-35) is violated in places, listed in §3. All 25 files were read in full (18,032 lines).

---

## 1. Surfaces and sub-parts

### Mount and host
- `Sidebar.Pane()` (`Sidebar.UI.cs:46`) mounts the docked pane. `Sidebar.DrawerPane()` (`:51`) mounts the overlay drawer. Both create a `PaneHost(inDrawer)` (`:62-101`).
- `PaneHost.Render` picks the mode component: `LibraryMode` vs `ClassicMode` at `:85-87`; keyed per layout at `:95` (`"sidebar.library"` / `"sidebar.classic"`), so a layout switch is a full remount; enter fade uses `EnterExit(Opacity 0)` with `MotionTok.ControlFast` (`:96-97`).
- `ClassicMode` (`:178-196`) builds a `PaneConfig` with `Layout = Classic` (`:188`).
- `LibraryMode` (`Sidebar.UI.Library.cs:24-91`) builds a `PaneConfig` with 15 delegates (`:59-80`).
- `PaneConfig` (`Sidebar.UI.cs:204-237`) is "the only mode seam".

### Expanded pane: `PaneView`
`PaneView` (`Sidebar.UI.cs:290-2087`) is the one renderer.
- `Render` is at `:459-566`. The layer box is `:524-533` (`Width = _expandedWidth`, `HitTestVisible`, `DropTarget` = rail peek). Context-menu shield `:551-564`.
- `PaneChildren` (`:575-593`) lays out head + body + footer.
- `PaddedList` / `PlanList` (`:610-651`) is `ItemsView.CreateBound` with `ContentType` = row kind, a reorder displacement channel and disclosure options.
- `EmptyPane` (`:867-884`) uses `Icons.SplitView` 24, `Tok.TextTertiary`, 14/600 `TextSecondary`.

### Rows
`PaneSlot` (`Sidebar.UI.Slot.cs:37-883`) dispatches on row kind at `:79-96`.

| Row kind | Builder | Notes |
|---|---|---|
| SectionHeader | `HeaderRow` `Slot.cs:137-170`; visuals `SectionHeader.Header` `Sidebar.UI.Rows.cs:873-899` | 40 tall; title 14/600 `TextSecondary`, hover `TextPrimary` (`Rows.cs:880`); "+" for Playlists (`Slot.cs:144-148`); ⋯ via `InlineButton` (`Rows.cs:904-913`); chevron (`Rows.cs:595-657`); pill |
| Divider | `SectionHeader.Separator` `Rows.cs:917-924` | 8 tall, 1-px `Tok.StrokeDividerDefault` |
| IconRow (Home, Collections pages, glyph rows) | `RouteRow` `Slot.cs:493-527` | count badge `CountBadge` `Slot.cs:696-700`, `Counts.Live` `Rows.cs:748-775` |
| EntityRow (entity, pin, Liked, feed row) | `EntryRow` `Slot.cs:225-329` | built by `EntityRow.Create` `Rows.cs:174-293`, `Finish` `:298-334` |
| FolderHeader | `FolderRow` `Slot.cs:334-416` | missing folder: `MissingFolderRow` `:420-443`; folder "+" `:646-674` |
| GridStrip (Library Grid) | `GridStripRow` `Slot.cs:550-573`, `GridCell` `:579-584` | cell = `Controls.Surface(SidebarCards.Tile(...), Shape.SidebarTile)`; tile adapter `Sidebar.Cards.cs:108-130`; no-art cover `:134-145` |
| Empty | `EmptyRow` `Slot.cs:591-604` | |
| Skeleton | `Skeletons.Row` `Rows.cs:825-862` | |
| TreeEnd (drop gutter) | `TreeEndRow` `Slot.cs:835-852` | |
| SectionTile (rail, collapsed section) | `SectionTileRow` `Slot.cs:474-488` | glyph from `PaneIcon.SectionGlyph` `Rows.cs:1091-1099` |
| DropBand ("Drop here to pin") | `DropBandRow` `Slot.cs:531-539` | dashed `AccentDefault` border |

Row-level visual parts:
- **Indicator ZStack** (`Slot.cs:705-716`): drop plate (`:722-746`), then the row, then the `SelectionPill` (`Rows.cs:667-718`), then the insertion line (`Slot.cs:753-797`).
- **Covers** (`Rows.cs:414-578`): radius ladder `:421-422`; `Liked` mosaic `:453-462` rendered by `LikedArt` (`:522-577`); `Art`, `Glyph`, `Folder`, `RouteGlyph` `:468-508`.
- **Trailing cluster**: equalizer, pin mark (12-px `Tok.TextTertiary`) and count (`Rows.cs:237-259`); hover "…" (`:373-390`).
- **Track art hover scrim** (`Rows.cs:395-405`).

### Liked and pins
- **Classic Pinned section**: `PlanPins` (`Sidebar.Planner.cs:121-138`); a pinned folder expands inline (`:132-133`, `AppendPinnedFolderChildren` `:367-397`).
- **Library**: pins lead the list, no header (`Planner.cs:264-283`); Liked row `:285-291` with rule `SidebarVisibilityRules.ShowsLiked` (`Sidebar.Layout.cs:689-696`).
- **Pin mark rule**: `SidebarPinRules.ShowsPinMark` (`Sidebar.Rules.cs:173-175`).
- **Pin row state** (Resolved / Pending / Unavailable / Offline): `Sidebar.Layout.cs:752-772`, rendered at `Slot.cs:237-248, 304, 308`.

### Library layout head
`LibraryHead` (`Sidebar.UI.Library.cs:341-679`): Home row and pill `:427-456`; separator `:402`; "Your Library ▾" dropdown and pill `:458-523` (pages menu `:533-545`); `LibraryChips` `:683-775` (chip visuals `:729-750`: 32 tall, pill radius; active chip `AccentDefault`/`TextOnAccentPrimary` with ✕); `LibraryToolbar` `:779-927` (search, sort, List/Grid toggles `:891-907`, "+", ⋯; built from `TabOrder`); `LibrarySearchBox` `:931-998`; drill breadcrumb `:595-619`; rule line `:408-413`; error banner `:651-678`; `EmptyBand` `:623-648`; `CompactHead` (rail Home + search tile) `:571-592`.

### Classic transient filter box
`PaneFilterBox` (`Sidebar.UI.cs:790-848`), 32 tall, `FontSize 13`; opened with Ctrl+F (`:740-764`), hosted in the header (`Slot.cs:163`).

### Feeds
Recently played and New releases are planned as sections by `PlanFeed` (`Planner.cs:239-249`); data `Sidebar.Feeds.cs:52-143`; age badge `Slot.cs:680-685` using `PaneText.AgeBadge` (`Rows.cs:1046-1056`).

### Accounts
No visual surface. `Sidebar.Accounts.cs` is data only: swap `:40-68`, load `:70-90`, commit `:94-110`.

### Footer
`PaneFooter` (`Sidebar.UI.Footer.cs:31-122`): expanded = Settings row with ⋯ in its chevron column (`:97-104`) or ⋯ alone right-aligned (`:90-96`); compact = Settings tile over a ⋯ tile (`:84-89`); footer pill `:79-81`.

### Flyouts
- **Section flyout** (collapsed section in the rail): `PaneSectionFlyout` (`Sidebar.UI.Flyout.cs:68-149`); panel 300 wide, list capped at 420 high.
- **Folder flyout**: `PaneFolderFlyout` (`Flyout.cs:160-385`), header, back button, page slide.
- Open helpers `Flyout.cs:33-62` (`RightEdgeAlignedTop`, light dismiss). Pure drill stack `SidebarFolderFlyoutNav` (`Sidebar.cs:2198-2248`).

### Context menus
- **Model** (pure): `SidebarMenuModel` (`Sidebar.Edit.cs:194-395`).
- **Mapper**: `SidebarMenus.Map` / `Run` (`Sidebar.UI.Menus.cs:62-252`).
- **Pane menu**: `UI.Menus.cs:265-285` (footer ⋯ and background). **Header ⋯**: `Sidebar.UI.cs:855-864`.
- **Row menus**: `EntryMenu`, `FolderMenu`, `RouteMenu`, `MissingFolderMenu`, `GridCellMenu` (`UI.Menus.cs:290-341`); per-kind models `:345-478`.
- **Navbar extras**: Move to folder… and Select (`:501-523`). **"+" menus**: `CreateRootMenu`, `FolderCreateMenu` (`:692-740`). **Pickers/prompts**: folder picker (`:635-682`), rename prompt (`:717-727`).

### Edit mode
`EditPane` (`Sidebar.UI.Edit.cs:26-617`): edit bar `:103-128` (36 tall, `FillLayerDefault` + `StrokeCardDefault` border, Undo/Redo, Reset ▾, Done accent); Outline `:146-244`; section blocks `:256-322`; header row `:334-363`; checkboxes `:515-526`; pin row `:395-430`; item row `:434-458`; Settings row `:462-481`; locked head `:484-494`; hint row `:497-502`; show-all row `:505-510`; keys `:532-582`; drag chip `:589-606`. Pure model `SidebarEditRules` (`Sidebar.Edit.cs:46-162`).

### Drag and drop targets
All drop specs in `Sidebar.UI.Drop.cs`: `ResourceDropSpec` `:59-210`; `RailFolderDropSpec` `:213-258`; slot resolve/decide `:263-334`; `RailPeekDropSpec` `:428-434`; `HeaderCreateDropSpec` `:440-464`; `FolderCreateDropSpec` `:468-486`. Pure rules `Sidebar.cs:1228-1832`. Reorder bands: in-pane Pinned only `Sidebar.UI.cs:1067-1123`, `1625-1758`; Edit Outline `UI.Edit.cs:149-173`.

### Resize grip and pane column
Owned by the shell: splitter `<W>Shell\Shell.UI.cs:584-588`; column width `:525-527`; keyboard `:755-765`; double-click `:722`. Rules `Sidebar.Resize.cs:105-169`.

### Disclosures and expanders
`StartDisclosure` (`Sidebar.UI.cs:1172-1214`) uses `ItemsViewController.BeginDisclosure`; bookkeeping `SidebarDisclosures` (`Sidebar.Disclosures.cs:16-73`); chevron rotation `Rows.cs:595-657`; section toggle `UI.cs:1922-1932`; folder toggle `:1935-1946`.

---

## 2. States

### Loading
- Binder not yet built (`Revision == 0`) → sources Pending (`Sidebar.UI.cs:1056-1060`).
- Skeleton rows: Playlists `Planner.cs:156-159`; Library list `Planner.cs:294-299` (grid shows nothing while pending); visual `Rows.cs:825-862`, rendered from `Slot.cs:90-91`.
- Count still loading: pending plate (`Rows.cs:727-740`, `800`). Library counts null until known (`Sidebar.Library.cs:18-28`).
- Pending pin shows its kind noun in `TextTertiary` (`Slot.cs:242-248, 304`; `Layout.cs:764-772`); pending folder pin `Sidebar.cs:3099-3102`; unresolved grid tile shows a bone (`Cards.cs:85`).

### Empty
- A section with no rows is not rendered (`Planner.cs:91`), except Playlists. Classic Playlists Empty row `Planner.cs:161-170`, rendered by `Slot.cs:591-612`.
- Whole pane empty: `EmptyPane` (`UI.cs:867-884`); an empty rail shows a spacer (`UI.cs:571, 585`).
- Library empty state owned by the head: rules `SidebarLibraryEmptyRules` (`Library.cs:238-268`); UI `UI.Library.cs:389-397`, `:623-648` (`Controls.Vacancy` with Error, NoMatch, Empty voices).
- Folder flyout empty hint `Flyout.cs:314-325`; Edit hint rows `UI.Edit.cs:305-308`.

### Error
- Library failed load with rows → banner (`UI.Library.cs:416, 651-678`); with no rows → Vacancy Error (`:628`). The binder never publishes Failed (`Sidebar.Host.cs:1773-1776`, marked "GAP").
- Layout file corrupt: `Host.cs:122-127, 134`, surfaced in Settings (`<W>Screens\Settings.UI.Appearance.cs:573-574`).
- Write faults `Store.cs:17-23, 175-220`; `PersistenceHealth` `Host.cs:153`. Drop refusals: sentences `Sidebar.cs:1275-1290`, toasts `UI.Drop.cs:374-383`. Missing write seam `UI.cs:2001-2006`.

### Offline
`SidebarPinState.Offline` defined (`Layout.cs:752-760`), fed from `Spotify.Current.IsOnline` (`Slot.cs:239`); the slot only special-cases Unavailable (`:241`), so Offline renders like Pending.

### Signed out
Account key `""` → no pins load (`Accounts.cs:18-19, 56, 72`); pinning while signed out toasts (`UI.Drop.cs:499-503`); `InstallActionSeams` returns early (`UI.Menus.cs:41`); migration while signed out writes the pending account file (`Store.cs:484-490`; adopted at `Accounts.cs:74-79`).

### Row interaction states
- **Hover / pressed / selected / disabled**: `NavRow` `StateBrush` (`Rows.cs:162-170`): `FillSubtleTransparent`, `FillSubtleSecondary`, `FillSubtleTertiary`; brush cross-fade 83 ms; disabled opacity 0.3 (`:304`).
- **Selected**: the pill — rule `SidebarPillState` (`Sidebar.cs:746-780`); one-pill rule `Rules.cs:28-73`; visual 3×16, r2, `Tok.AccentDefault` (`Rows.cs:703-716`); lanes List/Head/Footer (`Rules.cs:76-84`).
- **Multi-select**: quiet plate via `NavRow(selected || MultiSelected)` (`Rows.cs:320`); check lane `Slot.cs:620-640`.
- **Focus**: roving stops `Rules.cs:118-127`, `UI.cs:660-720`; chip focus `UI.Library.cs:745`; Library head key ring `UI.Library.cs:549-567`.
- **Playing**: equalizer in `Tok.AccentDefault` (`Rows.cs:251`), driven by the play sweep (`UI.cs:1337-1378`) and `RowPlayState` (`:1278-1283`).
- **Hover reveals**: row "…" (`Rows.cs:377`), header ⋯ (hover or permanent after touch, `Rows.cs:910`), folder "+" (`Slot.cs:669-674`), track scrim (`Rows.cs:403`).

### Collapsed vs expanded, and window tiers
- Pane mode from `SidebarPaneModeRules` (`Sidebar.Resize.cs:26-100`): Wide (≥700 to enter, stays while ≥660) → Expanded, or Compact if user collapsed; Narrow (528–660) → forced Compact rail, toggle opens an overlay; Tiny (<528, leaves at ≥568) → Minimal, drawer only. Presented width `:66-73`.
- Overlay drawer is in the shell (`Shell.UI.cs:1417-1560`).
- Library width tiers: drill into folders below 240 (`Library.cs:275`; `UI.Library.cs:42`); toolbar Full / IconSort / Folded below 240 (`Library.cs:157, 174-178`); grid columns 2–4 from a 116-px cell (`Library.cs:53-61`), slot fallback (`Sidebar.cs:531-540`; `Slot.cs:556`).
- Section collapse persisted (`SetSectionCollapsed`, `Layout.cs:245`).

### Edit mode
`Sidebar.Editing` (`Host.cs:508`); enter `:449-458`, exit `:462-474`; pane swaps in the Outline (`UI.cs:498-520, 579`); in Narrow/Tiny the overlay is pinned (`Resize.cs:88`; `UI.cs:529`).

### Drag states
Drop plate and insertion line `Slot.cs:722-797`; drop band `Slot.cs:531-539`, armed `UI.cs:1895`; rail drag peek (250 ms) `UI.cs:1870-1906`, `UI.Drop.cs:428-434`; mid-drag freeze `UI.cs:979-993`; "+" cue `Rows.cs:993-999`; folder flyout row cue `Rows.cs:322-331`; spotlight `UI.Drop.cs:52, 204-206`; captions `UI.Drop.cs:113-136`; reorder lift Stationary at opacity 0 (`UI.cs:1631-1636`); Edit lift `SourceDimOpacity` (`UI.Edit.cs:31`); spring-loaded folder `Slot.cs:370-373`; entering Edit refused while a drag is live (`Host.cs:452-456`).

### Menus, flyouts, dialogs
Listed in §1. Popups use `PopupChrome.Popup`. Reset everything uses a `ContentDialog` (`UI.Menus.cs:90-98`). Undo toasts 6000 / 5000 ms (`Host.cs:347, 385-399`).

### Motion APIs used

| Motion | Where | API |
|---|---|---|
| Layout switch fade | `UI.cs:96-97` | `MotionTok.ControlFast`, `EnterExit` |
| Row reorder | `UI.cs:446` | `MotionTok.ItemPlacement.ToDynamics()` |
| Section/folder disclosure | `UI.cs:1182-1205` | `ItemsViewController.BeginDisclosure` (`MotionTok.Reveal`) |
| Chevron | `Rows.cs:639, 645` | `anim.SeedValue(Rotation, MotionTokenId.Reveal)` |
| Pill flight | `UI.cs:1536-1570`; `Rows.cs:700` | `NavigationSelectionMotion.StartVertical` / `SnapVertical` (600 ms) |
| Breadcrumb | `UI.Library.cs:344-347` | `MotionTok.ConnectedFly` |
| Folder flyout page slide | `Flyout.cs:195` | `MotionRecipes.PageSlideForward` / `Back` |
| Edit section band | `UI.Edit.cs:198` | `LayoutTransition.Slide` |
| Chips and view toggles | `UI.Library.cs:743, 901` | `Design.Motion.Fast` |
| Row and dropdown fills | `Rows.cs:169`; `UI.Library.cs:512` | `BrushMs = 83` |
| Pane column (shell) | `Shell.UI.cs:146-148` | `MotionTok.PaneOpen` / `PaneClose` with `Easing.FluentPane` |
| Drawer (shell) | `Shell.UI.cs:1543` | `UseTransition(TranslateX)` |
| Splitter drag (shell) | `Shell.UI.cs:283` | `FgMotion.SetLayoutTransitionsSuppressed` |

---

## 3. Logic vs presentation

Intended ownership: **CORE** `Sidebar.cs`, `.Layout.cs`, `.Planner.cs`, `.Rules.cs`, `.Resize.cs`, `.Edit.cs`, `.Library.cs`, `.Disclosures.cs`, `.Census.cs`, `.Store.V2.cs`, `.Migration.cs` (the `SidebarMigrationV2` part); **SHELL** `Sidebar.Host.cs`, `.Store.cs`, `.Accounts.cs`, `.Feeds.cs` (peek), `.Migration.cs` host part (`:242-316`); **UI** `Sidebar.UI*.cs`, `Sidebar.Cards.cs`.

### UI files that own logic (state, persistence, decisions)

**`Sidebar.UI.cs`**: `PaneHost` builds/starts the projection binder (`EnsureBinder` `:128`, `PumpBinder` `:138-172`, subscribing every entity table edge); account swap effect `EnsureAccount` on `ScopeEpoch` (`:75-79`); migration toast (`:107-117`); `DefaultReorderCommit` mutates the pin store (`:246-253`); the plan/publish pipeline, A/B buffers and freeze (`:952-1049`); selection/play-state/pill sweeps (`:1337-1498`); **`OwningSection` branches on `doc.Layout == Library` (`:1434`) — breaks the one-renderer rule**; Classic filter state and Ctrl+Z/Y → `Undo`/`Redo` (`:740-782`); `ToggleSection` dispatches and calls `MarkNewReleasesSeen` (`:1922-1932`); `ActivateFolder` (`:1935`); navigation and playback (`:1960-1980`); `CreatePlaylistFlow` / `RefuseWrite` (`:1992-2006`); `PayloadOf` (`:2010-2035`); marker cache (`:2062-2076`); tree-selection mutation (`:1783-1866`); pin drop (`:1746-1758`); writes the global `DragPeek` signal (`:1873-1878`); contains a public pure class `PlanDiff` (`:2098-2129`).

**`Sidebar.UI.Library.cs`**: `LibrarySession` (`:95-338`) — drill stack `:239-277`, `ActivateFolder` switches Grid→List `:281-292`, `CanReorderCustom` `:214-216`, `ShapeInput`/`View.Build` `:166-188`, `SwitchToCustomSortForReorder` `:232-237`, search close/retry `:324-337`; 90 ms search debounce + `Resync` (`:27, 51-57`); head self-correcting effects (`:360-368`); empty-state computation (`:387-397`); chip commit (`:720-725`); search-box Esc logic (`:967-983`).

**`Sidebar.UI.Edit.cs`**: writes `Sidebar.s_enabledEmptySection` (`:379-380`); `HasNothingToShow` reads binder state (`:385-392`); steps the library filter off a hidden kind (`:443-444`); replaces the global `Drag.ExtraChip` (`:54-65`); key handlers dispatch ops (`:532-582`); branches on layout at `:215`, `:232`.

**`Sidebar.UI.Menus.cs`**: `Run` dispatches every mutation (`:151-192`); `ToggleKind`, `MoveBy`, `HideItem` decide ops and toasts (`:196-245`); `InstallActionSeams` signed-out guard (`:35-48`); `TreeMoves` legality (`:577-595`); `CommitMove` (`:612-628`); `RenameAction` (`:744-759`); `LayoutName` "· modified" rule (`:52-56`).

**`Sidebar.UI.Drop.cs`**: all drop arm/commit decisions (`:59-210`); `PinWithToast` signed-out guard (`:496-506`).

**`Sidebar.UI.Slot.cs`**: pin state reads `Spotify.Current.IsOnline` (`:239`); label fallback rules (`:242-248`); `RootlistSection` rule (`:219-220`); `TreeRowFacts` (`:802-829`); `ShowsPinMark(_o.Config.Layout, …)` layout-dependent at `:309, 395, 437`.

**`Sidebar.UI.Rows.cs`**: `LikedArt` reads `User.Me` liked edges and the Tracks table (`:539-563`); `CountHost` reads `User.Relation` (`:764-771`); public pure class `ShortcutCount` (`:780-815`); `PaneText.AgeBadge` reads the clock (`:1049`).

**`Sidebar.UI.Flyout.cs`**: `ToggleFolder` persists expansion (`:137`); flyout navigation state (`:175-179`).

### Logic files holding visuals, colours, sizes

**`Sidebar.cs`**: `SidebarPaneBounds` (`:54-68`) 180/460/320/24; `SidebarRowGeometry` (`:340-586`) the whole pixel ladder (rows 36/40, row margin 2, pane edge 4, top inset 3; rail 48, tile 40, icon column 40, glyph 16, label gap 4; trailing pad 14, chevron column 40, indent 31; header 40, header text x 12, header button 24; separator 8, empty hint 40, tree end 24, pill 3×16 r2; art 32/24 `:395-400`); `SidebarRowExtents` (`:596-617`); `RootlistSlotResolver` edge bands 10–16 px, 4-px hysteresis (`:1339-1346`); `SidebarDropCue` line 2, corner 1, dot 6 (`:1483-1514`); `GridFallbackColumns` (`:531-540`).

**`Sidebar.Library.cs`**: `SidebarLibraryHeadRules` metrics (`:32-49`): home 40, separator 8, dropdown 40, chip row 40, chip 32, gap 8, toolbar 40, controls 32, rule 1, TitleX 16, inset 12; grid min cell 116, gap 8 (`:53-61`); toolbar icon button 28, gap 4, sort chrome 40, fold below 240 (`:147-152`); toolbar width estimate 7-px avg char (`:164-170`); `SidebarLibraryMetrics` drill width 240, breadcrumb 32 (`:275-278`).

**`Sidebar.Edit.cs`**: `SidebarEditRules` heights 40/40/44 (`:50-52`); `ExtentOf` (`:115-126`); the menu model chooses loc keys and radio states (`:203-395`).

**`Sidebar.Rules.cs`**: `SidebarLabelFit` 7 px/char (`:132-144`); `SidebarPillMotionRules.Slides` (`:78-84`); `MenuLabel` 28-char clip (`:182-200`).

**`Sidebar.Resize.cs`**: band widths (`:29-33`); fade minimum 0.35 and nudge 8/40 (`:109-113`).

**`Sidebar.Cards.cs`** (logic namespace, Role UI): builds a `BoxEl` with `Tok.FillSubtleSecondary` and `Tok.TextSecondary` (`:134-145`); kind glyphs (`:158-166`); `Design.Type.TrackMeta` (`:214-217`); tile cover size from `SurfaceGeometry.ShelfPlatePad` (`:92`); subtitle only for two-line rows (`:121`).

**`Sidebar.Host.cs`**: toast text/durations 6000/5000 ms (`:346-357, 385-399`); undo labels from loc keys (`:235-250`). **`Sidebar.Layout.cs`**: title loc keys (`:106-115`); fallback nouns (`:764-772`); `ShapeFor` maps density to the visual row shape (`:594-597`). **`Sidebar.Migration.cs`**: legacy default widths 280/340/320 (`:259`).

### Literal colours outside tokens
Track scrim `new ColorF(0,0,0,0.55)` and white play glyph (`Rows.cs:402-404`); `Tok.AccentDefault with { A = 0.18f }` drop plate (`Rows.cs:327, 996`; `Slot.cs:738`); `ColorF.Transparent` at `Rows.cs:329, 997`; `Slot.cs:738, 745`; `UI.Library.cs:899, 943`; `UI.cs:811`.

### Token usage per file

| File | Tokens |
|---|---|
| `UI.Rows.cs` | AccentDefault ×6, FillSubtleSecondary ×7, TextSecondary ×7, TextTertiary ×4, FillSubtleTertiary ×3, FillSubtleTransparent ×2, StrokeDividerDefault, TextPrimary ×2, AccentTextPrimary |
| `UI.Library.cs` | TextSecondary ×8, FillSubtleSecondary ×6, FillSubtleTertiary ×4, TextPrimary ×3, TextOnAccentPrimary ×2, AccentDefault, AccentSecondary, AccentTextPrimary, StrokeDividerDefault |
| `UI.Edit.cs` | TextTertiary ×9, TextSecondary ×8, TextPrimary ×5, StrokeCardDefault ×2, FillLayerDefault |
| `UI.Slot.cs` | AccentDefault ×6, TextTertiary ×4 |
| `UI.Flyout.cs` | TextSecondary ×3, TextPrimary, TextTertiary |
| `UI.cs` | TextSecondary, TextTertiary |
| `Cards.cs` | FillSubtleSecondary, TextSecondary |

Typography literals (`Size`, `Weight`, `FontSize`): `UI.Edit.cs:117, 341, 405, 447, 469, 492`; `UI.Library.cs:479, 485, 615, 663, 674, 733, 735, 964`; `UI.Rows.cs:880`; `UI.cs:832, 874`. Corner-radius literals: `Rows.cs:383` (13), `:861`, `:983` (4); `Slot.cs:727` (4).

---

## 4. Presentation switches

### 4.1 Layout (Classic or Library)
**Persisted**: key `sidebar.layout.id`, `SettingKey<int>` (0 Classic, 1 Library), `<W>Platform\Platform.cs:83`; read `Sidebar.Host.cs:78` in `Boot`; written `Host.cs:268` (`SwitchLayout`) and `Sidebar.Migration.cs:279`; signal `Sidebar.Layout` (`Host.cs:158`). **Available synchronously before the first frame: yes** (`Boot` runs from `App.cs:194` before `Shell.Run`).

**Branch sites in the family (24 comparison lines)**: Host/UI-structure `UI.cs:85-87` (mode component), `:95` (remount key), `:111` (toast name), `:1434` (**renderer branch**); Planner `Planner.cs:49`; Catalogue/rules `Layout.cs:67` (`KindsOf`), `:126` (`Movable`), `:138-139` (`ItemsOf`), `:219`, `:233, 236`, `:460`, `:665` (`DedupesPins`), `Rules.cs:174` (`ShowsPinMark`); Binder `Host.cs:1484`, `Host.cs:1826` (`PackV3` trigger, `Sidebar.cs:3240`); Menus/Edit `Edit.cs:212`, `:226, 228`, `UI.Edit.cs:215`, `:232`, `UI.Menus.cs:54`, `:145, 156`.

**Indirect**: `PaneConfig.Layout` fixed per mode (`UI.cs:188`; `UI.Library.cs:61`), consumed at `UI.Slot.cs:309, 395, 437` and `UI.cs:1929`; the whole Library mode hangs off `PaneConfig` delegates (`UI.Library.cs:59-80`).

**Outside the family (6)**: `<W>Shell\Shell.UI.cs:733`, `:837`; `<W>Screens\Settings.UI.Appearance.cs:551, 562-563, 588-625` (`SwitchLayout` at `:591`); `<W>Screens\Setup.UI.cs:753`; `<W>Shell\Shell.Palette.cs:102-103, 327-335`.

### 4.2 Density (Default or Compact)
**Persisted**: `sidebar.pane.density` (`Platform.cs:89`); read `Host.cs:79`; written `Host.cs:287`, `Migration.cs:282`; signal `Sidebar.Density` (`Host.cs:161`); `Doc` cache keys on it (`Host.cs:185-201`). Synchronous.
**Root branch (1)**: `SidebarSection.ShapeFor` (`Layout.cs:594-597`): Compact → `EntityOneLine` (36 tall, art 24, no subtitle); Default → `EntityTwoLine` (40 tall, art 32). Glyph sections unaffected.
**Shape fan-out (~24)**: `Sidebar.cs:389, 398, 427, 610`; `Cards.cs:121`; `UI.cs:1120`; `Slot.cs:90-91, 254, 294, 341, 347-348, 387, 423, 429, 433, 497, 512`; `Rows.cs:178, 180-181, 190, 404, 828`.
**Controls**: menu `Edit.cs:238-239`, `UI.Menus.cs:159`; undo `Host.cs:273-288, 420-422`; equality `Layout.cs:637`; Settings picker `Settings.UI.Appearance.cs:553, 559-560, 592-601`.
**Ignores density (hardcodes two-line)**: flyouts `Flyout.cs:89, 97, 143-144, 300-301`; extent fallbacks `UI.cs:456, 1102, 1590, 1592, 1604, 1607, 1609`; `Slot.cs:707`.

### 4.3 Library View (List or Grid)
**Persisted** in `sidebar.json` library section `"view": "list" | "grid"`: DTO `Store.cs:299`; written `:418`; read `:441`; default List (`Layout.cs:154`); resolved `Layout.cs:573`. Synchronous (`LoadDeviceFile` in `Boot`, `Host.cs:86, 107-131`).
**Branch sites (13)**: `Planner.cs:298`, `:314`; `Library.cs:307`; `UI.Library.cs:216, 284-286, 296, 854, 857, 896`; `Edit.cs:311-312`; `Host.cs:243`; `Migration.cs:62`. Rendering: grid `Slot.cs:550-584`, `Cards.cs:108-145`. **The only "cards vs rows" switch.**

### 4.4 Other Library options (`sidebar.json`, synchronous)
- **Sort / Descending**: `Store.cs:297-298, 416-417`; branches `Planner.cs:328-329`, `Library.cs:134-142, 174-178`, `UI.Library.cs:215, 792, 815-832`, `Sidebar.cs:2457`.
- **ShowLiked**: `Store.cs:300`; branches `Layout.cs:692`, `Edit.cs:315`, `UI.Menus.cs:168, 237-238`.
- **Hidden kinds**: `HiddenItems`; branches `Layout.cs:722-746`, `Library.cs:75, 90, 103-104`.
- **Library filter chip**: key `sidebar.library.filter` (`Platform.cs:91`); read `Host.cs:87`; written `:293`, `Migration.cs:283`; branches `Host.cs:1486`; `Layout.cs:672-696`; `Planner.cs:271, 329`; `UI.Library.cs:148, 193, 215, 380, 696, 791, 823`.

### 4.5 Pane width
`sidebar.pane.width` (`Platform.cs:85`); read `Host.cs:80`; written `Host.cs:572`, `Migration.cs:280`; synchronous. Branch sites (~10): layer width `UI.cs:327, 527`; `ContentWidth` `UI.cs:2049`; `RowExtentOf` `:1605`; label tooltip `Slot.cs:453`; grid `Slot.cs:552-557`; Library columns/narrow `UI.Library.cs:40, 42, 105-107`; toolbar shape `UI.Library.cs:819-826`; overlay width `Resize.cs:77-78`.

### 4.6 User collapse
`sidebar.pane.userCollapsed` (`Platform.cs:87`); read `Host.cs:81`; written `Host.cs:573`, `Migration.cs:281`; used by `Resize.cs:57-62`, `Shell.UI.cs:245, 268`.

### 4.7 Pane mode, window band, drawer (derived, not persisted)
`Sidebar.Band` / `Sidebar.Mode` / `Sidebar.PresentedWidth` (`Host.cs:499-515`); seeded in the shell's first render before the column mounts (`Shell.UI.cs:238-248`); updated by band/presentation effects (`:251-281`). Branch sites: `UI.cs:471, 529, 531, 579, 585, 698`; `Footer.cs:41, 52, 69, 74, 84`; `UI.Library.cs:370`; ~24 `st.Compact` sites in `Planner.cs:47-328`; slot `CompactPlan` sites `Slot.cs:228, 300, 350-357, 398-399, 432, 459, 464, 516`; `Layout.cs:665`. `InDrawer`: `UI.cs:46, 51, 86-87, 194, 471, 529, 537, 632`; `UI.Library.cs:29-42, 105-116, 370, 818-824`; `Footer.cs:41`.

### 4.8 Edit mode (session only)
`Host.cs:508`; `UI.cs:498-520, 529, 579`; `Edit.cs:226-244`; `Settings.UI.Appearance.cs:554-571`; `Shell.UI.cs:260, 1062, 1432`.

### 4.9 Store versions, V2 and migrations (one-time, at boot)
- Current: `sidebar.json` v3 (`Store.cs:372-468`; version check `:389`); account file v1 (`Store.cs:480-560`; `:534`).
- Legacy read-only: `sidebar-layout.json` v1–2 (`Store.V2.cs:178-197`); its display DTO had `Density` ("compact|cozy|comfortable"), `Artwork`, `Subtitles`, `CountBadges`, `Presentation` (`:120-137`). **The migration ignores all of these except `MaxItems`** (`Migration.cs:155, 158`).
- Latch `sidebar.bootstrap.version` = 2 (`Platform.cs:81`; checked `Migration.cs:256`, set `:285`).
- Legacy keys read once (`Migration.cs:245-265`): `sidebar.design` (0 Classic, 1 LibraryV3, 2 Curated), `sidebar.classic.section.{pinned,library,playlists}`, `sidebar.v3.{filter,sort,desc,view}`, `sidebar.<classic|v3|curated>.{width,collapsed}`, `sidebar.pins.migratedToServer` (`Platform.cs:97`).
- Branch sites (6): `Migration.cs:42, 45-50` (design→layout with `CuratedLooksLikeLibrary` `:124-141`), `:62, 64` (V3 view→density), `:258-259`. `sidebar.migration.dropped` (`Platform.cs:93`) shown in Settings (`Settings.UI.Appearance.cs:575-578`).
- **No runtime branch on V2 vs V3.** Only the `PackV3` trigger name (`Sidebar.cs:3240`; `Host.cs:1826`) plus `Strings.Sidebar.V3.*` loc keys.

### 4.10 Not present on main
"Show covers": 0 hits. "Style": only `DragVisualStyle` (`UI.Edit.cs:31`; `UI.cs:1635`). "Cards": only the Grid tile (`Cards.cs`).

---

## 5. Seams into pages and the shell

**Navigation**: single path `PaneView.Navigate` → `Shell.GoTo(Shell.Parse(route, arg))` (`UI.cs:1960-1968`); fallback `LibrarySession.Navigate` (`UI.Library.cs:313-317`); route reads `Shell.Current` (`UI.cs:1457-1460`; `UI.Library.cs:381, 554, 573`) and `Shell.NameOf`; titles/glyphs via `Shell.Dest(Shell.Parse())`: `Slot.cs:243, 306, 495`; `Cards.cs:161`; `Rows.cs:508`; `UI.cs:680, 1706`; `UI.Menus.cs:46, 86, 317`; `UI.Edit.cs:418, 446, 464`; `UI.Library.cs:383, 429, 574`; `Flyout.cs:125`; `Footer.cs:44`.

**Playback**: `Acts.Play` or `Shell.OnPlayContext` (`UI.cs:1973-1980`); `Controls.NowPlaying` (`UI.cs:1340-1342`).

**Entities**: projection over `User.Me` edges (`Sidebar.cs:2715-3034`); `Entities.Ensure`/`EnsureEdge` (`:2745-2758, 2839-2847`); pins `Host.cs:1583-1760`; feed peek `Feeds.cs:147-194`; `LikedArt` `Rows.cs:539-563`; `CountHost` `:764-771`; `Entities.Track` `UI.Menus.cs:456`; comment-only alignment with `Playlist.MosaicTiles` (`Sidebar.cs:2672-2679`).

**Actions/menus**: `Actions.Services` (`UI.cs:464`; `UI.Menus.cs:37, 257, 347`); `Actions.Menu.*` (`UI.Menus.cs:295-523`); `Menus.Container` (`:391, 432`); `AppActions.Find(RenamePlaylist)` (`:753`); `Actions.OpenPicker` (`:658`); `Controls.Prompt` (`:721`); `ContentDialog` (`:91`).

**Feeds/notify**: `Notify.Items`, `ReleasesState`, `RefreshFeeds` (`Host.cs:1511-1516, 1842-1845`; `UI.cs:159-160`); `Notify.Say` (`Host.cs:356, 454, 472`; `UI.cs:115, 2005`; `UI.Drop.cs:380-382, 501`).

**Shell logs**: `Shell.History.Store`, `Shell.PlayLog` (`Host.cs:1203-1214`; `UI.cs:157-158`). **Other**: `Shell.FocusedIsTextEditor` (`UI.cs:748`); `Diagnostics.BenchHooks.ToggleSidebarSection` (`UI.cs:1913-1918`); `Shell.SidebarReplanCensus` (`Census.cs`; `UI.cs:914-955, 1035`); `Spotify.Current.IsOnline` (`Slot.cs:239`); `Drag.*` throughout `UI.Drop.cs`.

**Library write seam**: `Sidebar.LibraryWrites` (`Host.cs:1967`), installed by `<W>Spotify\Spotify.Library.cs:111`; also called by `Entities\Playlist.Page.cs`, `Album.Page.cs`, `Track.Menu.cs`, `Browse.Cards.cs`, `User.UI.cs`, `Shell.UI.cs`.

**Inbound callers**: `<W>Shell\Shell.UI.cs` band/mode/presentation `:238-281`; pane column `:521-537`; seam `:584-588`; keys `:722-765`; pane menu `:732-734`; invariant `:836-838`; title-bar toggle `:1062-1074`; pin seams `:1265-1268`; overlay drawer `:1417-1560`. `<W>Screens\Settings.UI.Appearance.cs:542-640`. `<W>Screens\Setup.UI.cs:748-758`. `<W>Shell\Shell.Palette.cs:102-105, 324-336`. `<W>Shell\Shell.Host.cs:379` (`Activate`).

**Sidebar primitives reused by pages (move with the theme)**: `Sidebar.Chevron`: `Entities\Episode.Discussion.cs:155`, `Track.UI.Bound.cs:780`, `User.Facts.UI.cs:987, 1338`, `Screens\LogsPage.UI.cs:572`. `Sidebar.Cover.Liked`: `Entities\Recents.Page.cs:625`, `Recents.UI.cs:212`, `Home\Zones.UI.cs:499`.

---

## 6. Startup: `Sidebar.Boot()`
Called at `<W>App.cs:194`, after `Video.InstallMirror` and before `HomeLayout.Boot`, `InstallActionSeams` (`:196`), `Spotify.Library.Install` (`:197`) and `Shell.Run`. Body `Sidebar.Host.cs:72-94`.

**Synchronous, in order**: 1 profile dir (`:74`; `UseProfileDir` override, `--fake` demo temp dir `:100-105`, or `%LOCALAPPDATA%\Wavee\WaveeMusic`); 2 migration (`:75`, `SidebarMigrationHost.RunIfNeeded` `Migration.cs:254-291`: if the latch is below 2 it reads legacy keys and `sidebar-layout.json`(+`.bak`) with `File.ReadAllBytes` (`Store.V2.cs:182-197`), writes `sidebar.json` and the account file, then `FlushNow` and **blocks in `WaitForWrites(2000)` per file** (`Migration.cs:295-315`) — up to ~4 s on the boot thread); 3 settings (`:77-83`: `Layout`, `Density`, `Width` clamped, `UserCollapsed`, `Seam` = 48 if collapsed else width, `PresentedWidth` = `Seam`); 4 device file (`:85-86`, `LoadDeviceFile` `:107-131`: sync read, `.bak` fallback, `MarkCorrupt`, defaults; bumps `LayoutVersion`); 5 library filter (`:87`); 6 account (`:89-91`: `EnsureAccount` → `SwapAccount` `Accounts.cs:40-68` → `LoadAccount` `:70-90`; pending-file adopt, sync read, `Pins.LoadFrom`, expanded folders, first-seen stamps); 7 hook (`:92`: `Pins.OnChanged = CommitAccount`).

**Deferred**: UI-thread dispatcher `Sidebar.Activate(post)` at `Shell.Host.cs:379` (until then `ToUi` inline, `Host.cs:46`); projection binder constructed in the first `PaneHost` render (`UI.cs:72, 128`), first rebuild in `PumpBinder` (`UI.cs:73, 138-172`) — until then the pane plans skeletons (`UI.cs:1056-1060`); first-seen stamps lazy (`Host.cs:1447`); new releases on demand (`Host.cs:1508-1517`); migration toast after first mount (`UI.cs:81, 107-117`); Band/Mode/PresentedWidth seeded by the shell's first render (`Shell.UI.cs:238-248`); Library grid columns/narrow flag computed in `LibraryMode` render (`UI.Library.cs:39-48`); write seams `Sidebar.LibraryWrites` (`App.cs:197`; `Spotify.Library.cs:111`) and pin action seams (`App.cs:196`; `UI.Menus.cs:35-48`); shutdown `Sidebar.Shutdown` at `App.cs:241` (`Host.cs:758-776`).

**Available synchronously before the first frame**: layout id, density, width, user collapse, library filter, the whole `sidebar.json` overlay state (sections, collapse, limits, Library sort/view/ShowLiked/hidden kinds), pins with expanded folders. The window band and mode become available in the shell's first render, before the column mounts.


# Part 4 — Stage, visualizers, verse, lyrics, video, OnMedia, deck


> Agent summary (family files only): (1) `stage.mode` (Lyrics/Visualizer/Queue/Artist): ~48 branch lines. (2) `stage.visualizer` (26 Kind ints, 19 shown, 5 groups, default Bloom=9): 72 `Kind.X` refs, ~12 runtime branch sites incl. the 19-arm `Visualizer.Face` switch. (3) `stage.layout` (Card/LargeArt/Centered/Artist): 77 `VizLayout.` refs + 43 `Eff`/`IsLayout`/`LayoutRules` lines. (4) `stage.spectrum` 23 lines; `stage.heroLyrics` 29; `stage.heroMotion` 5; `stage.heroDim` 6. (5) `stage.calm` ~55; `stage.moments` 13; `stage.lyricsOverlay` 5; `stage.sensitivity` 13 (incl. rail Deck); `stage.syncOffsetMs` 4; `stage.galleryOpen` 17; `stage.tipSeen` 2. (6) Not persisted but switched on: aspect class ~57 lines; theme arm (`Ink.IsDark`/`pal.Dark`) ~113 lines; OS reduced motion ~126 lines. (7) NPV deck: `npv.presentation` (developer-gated), `npv.player.style` (12 presets), `npv.player.<preset>.<opt>` (20 ChoiceSlug reads; two 12-arm dispatches). (8) Video: `video.placement` ~91 refs, aspect 29, ontop 9, pip rect, quality. (9) Lyrics: secondary line session-only (7 sites); blur strength 5 (always 0 on stage); podcast-vs-music 47. (10) All persisted keys read synchronously; the stage copies prefs into its own signals during its first render. `docs\guide\fullscreen-visualizers.md` is stale (8 kinds / Horizon default / 30 Hz clock; omits layout/spectrum/hero/moments keys).

**Path legend.** `Shell\X.cs:N` means `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\Shell\X.cs:N`. `Platform\…`, `AiLyrics\…` and `Screens\…` are relative to `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\`.

## 0. File inventory (line counts)

| File | Lines | Role |
|---|---|---|
| Shell\Stage.cs | 540 | CORE: modes, aspect allocator, transport predicates, tone alphas, entry, caption clock |
| Shell\Stage.Layouts.cs | 472 | CORE: VizLayout/SpectrumStyle/HeroState/PaneKind/Look, LayoutRules, HeroRules, per-look geometry |
| Shell\Stage.UI.cs | 1854 | UI: StageCtx, SurfaceCore, Backdrop, Hero, Chips, NowPlayingCard, PaneHost, LyricFacts, BackdropArt, FaceHost, FaceScrim, CaptionHost, CaptionWipeRows, ChromeHost, TopBar, TransportCard, Volume/Device/MetaLink, GalleryHost, QueuePane |
| Shell\Stage.Layouts.UI.cs | 77 | UI: SpectrumLayer (strip/ring) |
| Shell\Stage.Options.UI.cs | 440 | UI: options popover (Visualizer tab / Layout tab) |
| Shell\Stage.Hero.UI.cs | 267 | UI: Artist layout (HeroFacts, HeroLayer, HeroPhoto, ArtistNameLayer) |
| Shell\Stage.Artist.UI.cs | 764 | UI and ArtistRules (pure): Artist mode pane |
| Shell\Stage.Host.cs | 89 | SHELL: Stage.Diagnostics log events |
| Shell\Visualizer.cs | 932 | CORE: Kind/Need/Tier/Source/Group/Pace/Lod, Catalog, Demand, Bands, Beat, Aurora, Timeline, Oscilloscope, Field, Model.Tick |
| Shell\Visualizer.UI.cs | 753 | UI: Slab, SeriesSource, InstanceSource, Clock, FaceSpec, Face switch, FaceFrame, drifting blobs, BackdropField, AmbientMotion |
| Shell\Visualizer.Fluent.UI.cs | 803 | UI: Bloom, Bars, Ring, Orbit, Aurora, Timeline, FluentGeometry |
| Shell\Visualizer.Classics.UI.cs | 498 | UI: FaceSim/SimHost, Classic, Warp, Tunnel, Ambience, Kaleido, Scope, Drift |
| Shell\Visualizer.Zune.UI.cs | 907 | UI: Type, Mosaic, Spotlight |
| Shell\Visualizer.ITunes.UI.cs | 217 | UI: Magneto, Flow |
| Shell\Visualizer.Gallery.UI.cs | 405 | UI: gallery pane, tiles, settings rows, GalleryNav (pure), names/blurbs |
| Shell\Visualizer.Strip.UI.cs | 141 | UI: StripBars, StripLine, RingAround |
| Shell\Visualizer.Covers.cs | 356 | CORE: Zune rules (Collect, Tiles, Flips, Spot, Drift) |
| Shell\Visualizer.Moments.cs | 77 | CORE: 8-bar palette rotation schedule |
| Shell\Visualizer.Palette.cs | 231 | CORE: cover palette A/B/C/Deep, contrast floor, Fade |
| Shell\Visualizer.Particles.cs | 449 | CORE: sim bodies, Tints (arm colours) |
| Shell\Verse.cs | 808 | CORE: Verse words/sections/emphasis/timing/flow/envelopes/lanes/geometry |
| Shell\Verse.UI.cs | 1161 | UI: Verse face host, rig, driver, cloud |
| Shell\Video.cs | 1576 | CORE: placement state machine and video rules |
| Shell\Video.UI.cs | 1484 | UI: docked/PiP/pop-out/fullscreen surfaces, placement menu, join poster |
| Shell\Video.Host.cs | 578 | SHELL: Video.State (Commit), Video.Prefs, PopOut controller |
| Shell\Video.Host.Wiring.cs | 299 | SHELL: Install, PlacementPost, BoundaryFold, NaturalSeed, HostObserver |
| Shell\Video.Overrides.cs | 246 | SHELL: local-video attachment roster |
| Shell\Video.Overrides.Mirror.cs | 220 | HOST: roster mirrored to track flags (a data mirror, not a picture flip) |
| Shell\Lyrics.UI.cs | 2693 | UI: ViewCore (rail and stage), Ink seam, LineRow, Ticker/Stepper, DebugLayer, NPV peek |
| Shell\Lyrics.cs (UI-facing §1-12b) | 3923 | CORE: Doc model, Fx/BlurPolicy, SyncGate, RowShape, Prefs adapter, Surface metrics, Follow, DofRamp, Interlude, Glow |
| Shell\Lyrics.Host.cs (§6 Store) | 2293 | Store (UI door) |
| Shell\OnMedia.UI.cs | 350 | UI: shared on-media transport over video, ⋯ menu |
| Shell\PodcastChapterTimeline.cs | 53 | CORE: chapter intervals/markers (consumed by Shell.PlayerBar.Chapters.UI.cs:19-54) |
| Shell\Deck.cs | 1638 | CORE: deck models, Fold, catalog→model, ClockRules/GripRules/Lettering |
| Shell\Deck.UI.cs | 1230 | UI: ViewHost/Host/Clock/Slab/SpectrumFold/Gesture/RecordFace |
| Shell\Deck.Faces.cs | 1459 | UI: Cassette, Reel, CD/MD, iPod, VU, Winamp, WMP, Canvas |
| AiLyrics\AiLyrics.UI.Lyrics.cs | 290 | UI: rail sparkle button, end-of-lyrics AI footer |
| AiLyrics\AiLyrics.UI.Settings.cs | 480 | UI: Settings › Appearance AI lyrics card |
| Platform\Prefs.Stage.cs | 56 | Prefs.Stage (one epoch, 15 keys) |
| Platform\Prefs.Player.cs | 142 | Prefs.NpvPlayer (presentation/style/options), Prefs.PlayerBar |

---

## 1. Surfaces and their sub-parts

### 1.1 The fullscreen stage (now playing)

**Mount, open and close**
- `Stage.View()` (Shell\Stage.UI.cs:62) mounts `SurfaceCore`.
- `Open(begin, cause)` (:148-155) refuses over fullscreen video (`Entry.CanEnter`, Shell\Stage.cs:427). It captures the morph key `"stage:art"` (Shell\Stage.cs:424) and sets `Shell.Ui.ImmersiveLyrics = true`.
- `Close` (:159-165) requires an explicit begin, because reverse shared-element capture is not implemented.

**StageCtx** (Shell\Stage.UI.cs:87-142): the one bag of signals: Layout, Mode, Kind, GalleryOpen, LyricsOverlay, Calm, Moments, GalleryShown; LayoutPref, SpectrumPref, HeroLyricsPref, HeroMotion, HeroDim, HeroFact, HeroArtist, NextHeroUrl; Effective, CaptionOn, Look, Pane, Sensitivity, SyncOffsetMs, Chrome, HasTimedLyrics; Accent (AccentSet), BasePalette, Palette, TrackKey, GalleryButton, Slab, PosterSlab, OptionsOpen; action slots OpenOptions, SetMode, ToggleGallery, Exit, Activity, OverControls, MenuOpen, Scrubbing.

**SurfaceCore** (Shell\Stage.UI.cs:186-567)
- Effects: layout signal (249), gallery-shown gate (252), the one preference effect (255-272), derived layout axis (276-290), track key (294-307), accent/palette (313-315, 475-495).
- Real OS fullscreen with restore of the prior state (318-324); focus scope (327-342); idle machine (356-359, 545-564); hairline fraction (362); TeachingTip (365-380).
- Layer z-order (384-469): Backdrop → HeroLayer (Artist) → FaceHost → smoke → FaceScrim → SpectrumLayer → NowPlayingCard → Hero → ArtistNameLayer → PaneHost → CaptionHost → ChromeHost → hairline → LyricFacts → HeroFacts → Visualizer.Clock.
- Key map (500-541): Esc/F11 close; `[`/`]` step face (Shift steps group); inside the Artist layout `[`/`]` step layouts (513-518); G toggles the gallery (526); everything else goes to `Shell.PlayerKey`.

**Backdrop** (573-606): one quarter-scale RepaintBoundary (591): floor, BackdropArt, `Visualizer.BackdropField` (597), scrim via `LayoutRules.ScrimFor` (601); hidden under a known Artist header photo (593). BackdropArt (804-822) bakes the blurred cover and keeps the previous cover underneath.

**Hero: the identity block** (610-678): one cover node `stage:cover` with a Layout FLIP (Bounds spring 0.45/0.90, ScaleCorrect; 625, 655-658); titles column with a Position spring (626, 663-674); right-click opens NowPlayingMenu (670-672); cover corner radius per look `CoverCorners` (682-688); `Chips` (692-719): format chip (remote-aware), "Synced lyrics" chip, BPM chip outside Visualizer mode; `MetaLink` (1694-1780): artist · album link, closes the stage before navigating (1777).

**NowPlayingCard** (722-742): acrylic card, Visualizer mode + ShowChips + Card layout only.

**PaneHost** (745-782): `Flow.KeepAlive` keyed by PaneKind (764-770): Lyrics → `Lyrics.StagePane(slab.Accent)`; Queue → QueuePane; Artist → ArtistStage. Pane frame entrance Dy 40, opacity tween 500 (775-781); pane moves with a Position spring (747-758).

**Queue body**: `QueuePane` (1803-1852): "Up next" header, Autoplay toggle written straight to `Platform.Keys.AutoplayEnabled` (1840), rows from `Stage.QueuePaneBody` (installed by Entities\Queue.UI.cs:66). Empty queue → empty sentence (1847). Rows scaled by `Layout.QueueScale` (Shell\Stage.cs:343).

**Artist pane** (Artist mode) — Shell\Stage.Artist.UI.cs: ArtistRules (49-159, pure, inside the UI file); ArtistStage (166-247): two columns when PaneW ≥ 1100 (194); sections HeaderBand (300-393: photo, veil, name + verified badge, listeners · rank, tour line, Follow, "Go to artist"), AboutBlock (400-463), SongCredits (470-531), PopularList (539-648, honours `Prefs.Appearance.TrackArtworkHidden` at 561), CityList (654-705), FansRow (709-763). Every navigation closes the stage first (281-287, 413).

**Lyric caption over the face** (904-1199, `CaptionHost`): previous / active (karaoke wipe + accent bloom) / next slots, or breathing dots in a break (1091-1112), over a radial veil (1017-1023). Clock view `Stage.Caption.View/ViewAt/NextEdgeMs/EdgeWake/Slots` (Shell\Stage.cs:432-539). Render-thread wipe `CaptionWipeRows` (1208-1341).

**Chrome**: ChromeHost (1349-1414): top bar, transport card, gallery (right column, or Portrait bottom sheet). TopBar (1417-1476): brand; SelectorBar with 4 modes (labelled or icon-only); options/gallery glyph (TeachingTip anchor); Exit (labelled or glyph). TransportCard (1480-1564): bar seek and times; "Playing from"; shuffle/prev/play/next/repeat; heart, mute, volume (if it fits), device button, ⋯ menu. Play disc (1568-1577), Glyph (1580-1588), Satellite (1591-1599), Heart (1601-1607), MuteGlyph (1609-1616), VolumeRow (1619-1640), DeviceButton flyout (1643-1690). Hairline progress while chrome is hidden (450-463).

**Options popover** — Shell\Stage.Options.UI.cs: opened from the top-bar glyph (Shell\Stage.UI.cs:237-246), re-provides the stage context (439). Tabs (87-128). Visualizer tab (132-251): "Now showing" poster (223-241); group chips All + 5 groups (148-173); 3-column poster grid with arrow/Enter keys (175-221); footer "Open gallery (G)" (266-297). Layout tab (301-351): 4 layout radio rows with schematic thumbs (316-318, 388-429); "Spectrum under the cover" segmented, only Large art / Centered (319-323); hero options (325-345: lyrics over photo, slow pan/zoom (disabled under reduced motion), dimming slider snapped to 5 %). Compact hides layout rows (304, 314, 325).

**Artist layout (hero photo)** — Shell\Stage.Hero.UI.cs: HeroFacts (43-97); HeroLayer (101-179: artist photo or blurred cover, previous layer kept, two arm-aware scrims 159-170); HeroPhoto (184-219: Ken Burns via 4 `UseKeyframes` rows at 30 Hz with CompositePose; `UseAmbientPause`); ArtistNameLayer (225-266).

**SpectrumLayer** (Shell\Stage.Layouts.UI.cs:25-76): strip (Bars/Line) or ring around the cover; keyed by layout, style and width bucket.

**Diagnostics** (Shell\Stage.Host.cs:18-88): NoteEnter/Exit/Mode/Layout/Pick/Lease/Source.

### 1.2 Stage layouts (VizLayout) — Shell\Stage.Layouts.cs:30

| Layout | What draws | Key rules |
|---|---|---|
| Card (0) | face full-bleed, NowPlayingCard thumb, gallery, smoke, caption | `ShowsNowPlayingCard`/`GalleryAllowed` (98-99); spectrum Off (104) |
| LargeArt (1) | cover beside the lyrics pane over a backdrop face | face is backdrop (79); pane = Lyrics (121); geometry 304-319, 412-423 |
| Centered (2) | cover (optional ring) centred, titles, one-line caption | geometry 322-338; caption context off (131) |
| Artist (3) | header photo full-bleed, name, identity row, optional lyrics pane | no face (77); Ring becomes Line (104); a known header-less artist draws LargeArt (64-65) |

- Effective layout: `Effective` (64-65). Compact always draws Card. `LookOf` (69-73); BackdropKind substitution to Bloom (86-87); `NeedsOf` lease (109-112); `Pane` (116-122); `ShowsCaption` (127-128); `Step` (135-140); `ScrimFor` (144-145); `CardNav` (153-165).

### 1.3 Visualizer faces (selectable key = `stage.visualizer` int; Shell\Visualizer.cs:44-49)

Gallery order (87-94), groups (122-129), pace (131-136), needs (139-150), now-playing content (158-165), CaptionFriendly (199-200):

| Int | Kind | Group | Pace | Needs | Builder |
|---|---|---|---|---|---|
| 8 | Verse | Lyrics | Calm | Spectrum+Lyrics | Shell\Verse.UI.cs:68 |
| 9 | Bloom (default) | Fluent | Calm | Level | Shell\Visualizer.Fluent.UI.cs:59 (ambient at 96) |
| 10 | Bars | Fluent | Lively | Spectrum | Fluent.UI.cs:163 |
| 11 | Ring | Fluent | Lively | Spectrum+Beats | Fluent.UI.cs:209 (ambient at 281) |
| 12 | Orbit | Fluent | Calm | Spectrum | Fluent.UI.cs:312 |
| 4 | Aurora | Fluent | Calm | Spectrum | Fluent.UI.cs:412 |
| 13 | Timeline | Fluent | Calm | Precomputed+Beats+Level | Fluent.UI.cs:466 |
| 14 | Classic | Classics | Lively | Spectrum | Classics.UI.cs:116 |
| 15 | Warp | Classics | Lively | Spectrum+Beats | Classics.UI.cs:191 |
| 16 | Tunnel | Classics | Lively | Spectrum+Beats | Classics.UI.cs:235 |
| 17 | Ambience | Classics | Calm | Spectrum | Classics.UI.cs:284 |
| 18 | Kaleido | Classics | Lively | Spectrum | Classics.UI.cs:325 |
| 19 | Scope | Classics | Lively | Spectrum+Scope | Classics.UI.cs:367 |
| 20 | Drift | Classics | Lively | Spectrum+Beats | Classics.UI.cs:413 |
| 21 | Type | Zune | Calm | Level+Beats | Zune.UI.cs:118 |
| 22 | Mosaic | Zune | Lively | Spectrum+Beats+Covers | Zune.UI.cs:261 |
| 23 | Spotlight | Zune | Calm | Beats+Gallery | Zune.UI.cs:638 |
| 24 | Magneto | iTunes | Lively | Spectrum+Beats | ITunes.UI.cs:37 |
| 25 | Flow | iTunes | Calm | Spectrum+Scope | ITunes.UI.cs:135 |

**Legacy ints** 0 Field, 1 Halo, 2 Horizon, 3 Matrix, 5 Spectrum, 6 Pulse, 7 Tape migrate to a successor (Shell\Visualizer.cs:103-120). **Dispatch:** `Visualizer.Face` (Shell\Visualizer.UI.cs:596-617). Names/blurbs/tips: Shell\Visualizer.Gallery.UI.cs:359-396. LOD Stage/Preview/Poster: `LodFor` (Shell\Visualizer.cs:248-253).

**Engine:** Clock (Shell\Visualizer.UI.cs:200-573): per-frame paceable ticker (279-284); lease (292-307); palette fade (374-425); moments (474-475); scope pull (480-503); one batch write (508-562). Model.Tick (Shell\Visualizer.cs:729-866). Base Field backdrop (Shell\Visualizer.UI.cs:709-724; Shell\Visualizer.cs:673-680).

**Gallery pane** (Shell\Visualizer.Gallery.UI.cs:47-177): grouped tiles with live/poster LOD (239-336), hover promotion after 120 ms (55, 80), check disc (307-313); settings rows Sensitivity, Lyrics overlay, Calm, Moments, Sync offset (142-152); `GalleryNav` (181-228).

**Strip (layouts only)** — Shell\Visualizer.Strip.UI.cs: StripBars 72 accent bars (51-79); StripLine 120/140 ink bars (83-108); RingAround 96 capsules (113-140).

### 1.4 Verse (lyrics-as-face)
Rules Shell\Verse.cs — Words (50-155), Song (161), Memory (190), Sections/chorus echo (292-438), Emphasis (446-482), Timing modes Static/Line/Word (486-535), Flow (543-584), envelopes (590-680), Lanes (692-712), Geometry per aspect class (732-807). UI Shell\Verse.UI.cs — `Face` (68), Preview (75-99), Rig (111-138), Host (142-1080), Driver/Waker/Frames (1081-1111), CloudWord (1119). States: no song → NoLyrics (246, 809-866); untimed or video → Poem (248, 868-870); timed composition (249, 311-346). Ghosts (620), Lower: hairline, translation, count-in, read-ahead (675), Cloud in breaks (777). A returning chorus forces a palette moment (922-923).

### 1.5 Lyrics view (stage pane and rail)
**Mounts:** rail `View()` (Shell\Lyrics.UI.cs:75-76); stage `StagePane(accent)` to `StageColumn`, keyed by type size (84-113); NPV peek (118, 2558-2693).
**Ink seam:** `Lyrics.Ink(OnMedia)` (125-155) — rail theme tokens vs stage StageInk; the bloom flips with the arm (152-154).
**ViewCore** (167-2011): states (443-454) Nothing playing / No lyrics; document host with skeleton region (727-753): shimmer (1075-1094), failed/empty (744-748); unsynced or suppressed (1007-1010) → UnsyncedContent (1043-1071); timed → virtual list of LineRow (1017-1038); overlays: interlude dots (679-720), resync pill with 4 s ring (533-562), video note (503-530), podcast peek gate (568-673), DebugLayer (developer only, 2416-2426), AI footer (492-496).
**Line kinds** (LineRow, 2106-2333): word-synced (`line.IsWordByWord && Syllables > 0`, 2192-2217): GlyphWipe main and glow, wipe split with lead; podcasts use ink rungs. Line-synced (2218-2239): halo blur on the active row; Secondary↔Primary ink with brush transition. Secondary translation/romanization line (2242-2247, 2314-2320). Stage-only accent pill (2255-2278). Scale 0.98 inactive / 1 active by spring (2181, 2186-2188).
**Model/metrics:** `SyncKind {None, Unsynced, Line, Syllable}` (Shell\Lyrics.cs:61); Line/Doc (75-127). Stage DoF ladder flattened (158-165) but forced 0 on stage (191). Surface metrics: music 36/46 stage, 26/33 rail; podcast 20/30 and 16/24 (1464-1470).
**Local AI lyrics states:** Doc flags `Generated`/`Override` (Shell\Lyrics.cs:91-105); store `Upgrade` (Shell\Lyrics.Host.cs:2023-2039). Rail sparkle (AiLyrics\AiLyrics.UI.Lyrics.cs:61-129): hidden, visible, pulsing while working, toggle prefer-AI, or open Settings. Footer (135-289): "x of y lines" progress with buffer bar and playhead tick, or the disclosure only. Settings card phases (AiLyrics\AiLyrics.UI.Settings.cs:145-155): NeedsSetup, Downloading, Paused, Preparing, Ready (expander with WordSync, PlainText, BatterySaver, Languages, Files), Error, Off/Unavailable/Unknown; deferred-remove InfoBar 157-162.

### 1.6 Video view
**Placement model** (Shell\Video.cs:51-66): None, Docked, Floating, Detached, Fullscreen. TransportOwner (85-103); PlacementState (139-156).
**Surfaces:** docked cap / watch-page stage (Shell\Video.UI.cs:98, 105-135, 662-814); stay-mounted main presenter PipSurface (887-1188: geometry binds 1005-1025, PiP chrome and 8 resize bands 1029-1122); pop-out content (1195-1342) with OS caption band (1253-1341); fullscreen host/surface (1350-1483) with exit chrome (1441-1482); shared PlayerStage over the engine MediaPlayerElement (505-603).
**Overlays/controls:** `OnMedia.Transport` (Shell\OnMedia.UI.cs:42, 160-301); docked hover strip mini/fullscreen/off (Shell\Video.UI.cs:823-853); join poster Poster/Working/Failed (339-408) driven by `Joining.Decide` (Shell\Video.cs:1545-1560) via JoinWatch (Shell\Video.UI.cs:420-480); placement menu (173-221).
**Mirror:** `Video.Overrides.Mirror.cs` mirrors the local-file roster onto track flags (72-118). No picture-flip feature.
**Aspect modes** Fit/Crop/Stretch/Native/Custom (Shell\Video.cs:883-926), persisted via `Video.Prefs` (Shell\Video.Host.cs:233-264).

### 1.7 OnMedia (Shell\OnMedia.UI.cs)
Transport strip: prev/play/next, SeekBar, times, mute, volume flyout, ⋯ menu (248-300). ⋯ menu: placement radios, then Aspect / Quality / Speed cascades, then trailing extras (74-158). Chrome fade via `FadeChrome` SeedEased with MotionTok.MediaChromeReveal/Conceal (51-62); visibility driven by the element's idle machine, not hover (7-13).

### 1.8 Deck (rail hero "player" faces)
Mounted in the rail hero only when `ResolvedPresentation == Player` (Shell\Rail.UI.cs:362-369); developer-mode-gated (Platform\Prefs.Player.cs:50-64). **Catalog** (Shell\Rail.cs:112-186): 12 presets with options (Record: finish/size/rpm/sleeve; Cassette: shell/label/side; Reel: reel; CD: format cd/md; Turntable as Record; iPod: body/lcd; Winamp: skin; VU: face/ballistics; Zune: accent/rpm; WMP: preset/colour; Canvas: drift/bleed; Picture: size/rpm/sleeve). Physics dispatch `Models.Create` (Shell\Deck.cs:1444-1460); face dispatch `BuildFace` (Shell\Deck.UI.cs:199-214). Record family (Shell\Deck.UI.cs:791-1230); others in Shell\Deck.Faces.cs: Cassette 44, Reel 251, CD/MD 374, iPod 527, VU 779, Winamp 978, WMP 1191, Canvas 1341. Clock gate `ClockRules.ShouldTick` (Shell\Deck.cs:1531-1532). SpectrumFold, the rail's own FFT fold reading `Prefs.Stage.Sensitivity` (Shell\Deck.UI.cs:444-483, 300-302).

### 1.9 Podcast chapters
`PodcastChapterTimeline.Normalize/At/HoverTime/MarkerFractions` (Shell\PodcastChapterTimeline.cs:7-52), used only by Shell\Shell.PlayerBar.Chapters.UI.cs:19-54. Not on the stage.

---

## 2. States

**Loading / empty / error / offline**

| Surface | Behaviour |
|---|---|
| Stage title | "Nothing playing" (Shell\Stage.UI.cs:635; `Transport.UsesTitle` Shell\Stage.cs:385) |
| Artist pane | NothingPlaying (Shell\Stage.Artist.UI.cs:181-186); each section `NoSection()` when data absent (256) |
| Queue | empty sentence (Shell\Stage.UI.cs:1847) |
| Lyrics | shimmer, No lyrics, Nothing playing (Shell\Lyrics.UI.cs:443-454, 741-751); upgrade held until next hand-off (780-796) |
| Verse | NoLyrics only after the store answered (Shell\Verse.UI.cs:245) |
| Video | Poster / Working (ring after budget) / Failed (fault line) (Shell\Video.cs:1495-1560; Shell\Video.UI.cs:368-408); demotion toast (Shell\Video.Host.Wiring.cs:67-72) |
| Visualizer | source ladder Live → Precomputed → TempoGrid → Breath (Shell\Visualizer.cs:747-797); gallery subtitle names the fallback (Gallery.UI.cs:400-404) |
| Remote device | format chip hidden (Shell\Stage.UI.cs:703-704; Shell\Stage.cs:382); lease None when not owner (Shell\Visualizer.UI.cs:300) |

No explicit "offline" state on these surfaces.

**Playing / paused / buffering**: idle machine playback (Shell\Stage.UI.cs:359); caption no timer while paused, per-frame only for dots or non-render-owned host (970-987), advancing = playing and not buffering (960); visualizer clock only while `Demand.Ticks` (Shell\Visualizer.UI.cs:252; Shell\Visualizer.cs:286-287); ambient drifts pause (Shell\Visualizer.UI.cs:732-752); Verse lands complete on pause (Shell\Verse.UI.cs:462-472); Deck ShouldTick (Shell\Deck.cs:1531).

**Podcast vs music vs video**: lyrics metrics and podcast ink rungs (Shell\Lyrics.cs:1464-1470; Shell\Lyrics.UI.cs:2196-2233); transcript peek gate (1004-1005, 568-673); rail ⛶ tooltip "Transcript" for episodes (Shell\Rail.UI.cs:270); video suppresses timed sync (Shell\Lyrics.cs:222) with a note (Shell\Lyrics.UI.cs:503-530); Verse shows Poem (Shell\Verse.UI.cs:196, 217); stage refuses to open over fullscreen video (Shell\Stage.UI.cs:150); docked video hidden, not unmounted, under the stage (Shell\Video.cs:782-783; Shell\Video.UI.cs:978).

**Narrow / compact tiers**: aspect classes with hysteresis (Shell\Stage.cs:65, 98-117, 123-174): Compact (no pane, chips or gallery; 96 transport; Play disc 52, Shell\Stage.UI.cs:1553), Portrait (gallery becomes a bottom sheet 1355, 1399-1406), Ultrawide (hero share 0.26·W). Icon-only selector (1427); exit label (Shell\Stage.cs:227; Shell\Stage.UI.cs:1458); volume fits (Shell\Stage.cs:244; Shell\Stage.UI.cs:1506); caption context lines only when H ≥ 600 (Shell\Stage.cs:278); Verse regions per class (Shell\Verse.cs:768-806); Artist pane columns (Shell\Stage.Artist.UI.cs:66, 194); options Compact (Shell\Stage.Options.UI.cs:304-325).

**Fullscreen vs windowed**: the stage is always OS fullscreen while mounted, restores prior state (Shell\Stage.UI.cs:318-324); video fullscreen same (Shell\Video.UI.cs:1388-1393); pop-out has its own `DetachedFullscreen` (Shell\Video.Host.cs:75; Shell\Video.cs:485-492).

**Hover / pressed / selected / focus**: glass HoverFill/PressedFill/HoverScale (Shell\Stage.UI.cs:1468-1475, 1568-1607); SelectorBar style (1429); options tabs roving TabStop (Shell\Stage.Options.UI.cs:112-125); grid cursor borders (190, Gallery.UI.cs:288); layout row 2-DIP border + check (Options.UI.cs:405-421); focus scope and never-null focus (Shell\Stage.UI.cs:327-342, 393-397; Shell\Video.UI.cs:1397-1434); hover-only chrome on docked/PiP/FS (`Opacity 0 / HoverOpacity 1`, Shell\Video.UI.cs:834, 1048, 1451).

**Menus / flyouts / dialogs**: options popover (Shell\Stage.UI.cs:237-246); TeachingTip (365-380); NowPlayingMenu (670-672, 1514-1516); device flyout (1662-1671); every open holds the idle chrome via counted `MenuOpen` (224); OnMedia ⋯ and volume (Shell\OnMedia.UI.cs:197-246); video placement menu (Shell\Video.UI.cs:173-221); AI confirm dialogs (AiLyrics\AiLyrics.UI.Settings.cs:383-395).

**Transitions and motion APIs**

| API | Where |
|---|---|
| `EnterExit` + `MotionTok.StandardEnter` / `ControlNormal` | Shell\Stage.UI.cs:405, 412, 444, 737, 1368, 1383, 1399 |
| `LayoutTransition` Bounds/Position springs 0.45/0.90 | 625-626, 747 |
| Tween 550 FluentDecelerate (face) | 859-860 |
| Opacity tween 500 (pane) | 778-779 |
| `BrushTransitionMs` scrim cross-fade 600 | 601, 878 |
| `ImageTransition.Fade(600)` | 818 |
| `UseKeyframes` + `UseAmbientPause` (Ken Burns, blobs) | Shell\Stage.Hero.UI.cs:194-203; Shell\Visualizer.UI.cs:644-655 |
| `AnimEngine.Keyframes(GlyphWipeSplit)` / `SetHeld` / `Cancel` | Shell\Stage.UI.cs:1233-1309 |
| `UseSpring` scale + `anim.Spring` opacity (lyrics rows) | Shell\Lyrics.UI.cs:2120-2121, 2156, 2187-2188 |
| `SeedEased` media chrome | Shell\OnMedia.UI.cs:51-62 |
| Deck entrance 320 SmoothOut | Shell\Deck.UI.cs:121-122 |
| AI footer Reflow | AiLyrics\AiLyrics.UI.Lyrics.cs:146-154 |
| Shared-element morph `"stage:art"` | Shell\Stage.cs:424; Shell\Stage.UI.cs:152, 162, 658 |

Under reduced motion, Enter/Exit and the caption rise become fade-only (Shell\Stage.UI.cs:64-67, 941-942).

**Enter / exit choreography**: 1 `begin("stage:art")` captures the bar art (Shell\Stage.UI.cs:152); 2 `ImmersiveLyrics = true` (153), shell `Flow.Show` mounts with Enter scale 1.03 + fade (Shell\Shell.UI.cs:488-493); 3 chrome row, content region and player bar collapse through `s_chromeMounted` (469, 471, 646, 652-653; `FrameRules.ChromeMounted`, Shell\Shell.cs:1906-1907); 4 SurfaceCore effects set OS fullscreen and push the focus scope; 5 exit mirrors this (Exit 1.02). F11/Esc fallbacks at Shell\Shell.UI.cs:781-807.

**No-jump rules**: one cover node with Layout FLIP; titles Position spring; pane KeepAlive keyed by PaneKind (Lyrics ⇄ LargeArt one sliding view, Shell\Stage.UI.cs:747-770); face keyed by kind and 8-DIP width bucket (856-858); spectrum keyed by bucket (Shell\Stage.Layouts.UI.cs:47, 57); `Effective` keeps Artist while the header is Pending (Shell\Stage.Layouts.cs:61-65); aspect hysteresis and 4-DIP art quantum (Shell\Stage.cs:95-117); previous blurred cover kept under the new one (Shell\Stage.UI.cs:802-822); hero photo keyed by artist (Shell\Stage.Hero.UI.cs:132-153); gallery not gated on idle chrome (Shell\Stage.UI.cs:92-97, 1395); linear accent fade lands exactly (Shell\Stage.cs:415; Shell\Visualizer.UI.cs:529-537); caption slots keyed by role/index/generation (Shell\Stage.UI.cs:1000-1008). Exception: the lyrics StageColumn re-keys and remounts on a type-size step (Shell\Lyrics.UI.cs:107-108).

---

## 3. Logic vs presentation (actual split, and leaks)

| Pair | Real split | UI files that own logic | Logic files that build visuals |
|---|---|---|---|
| Stage.cs / Stage.Layouts.cs vs Stage.UI.cs / *.UI.cs | CORE holds geometry, rules and alphas; UI lays out | **Stage.UI.cs:** ToggleGallery decision inline (205-220), duplicated in Options footer (Options.UI.cs:268-274); key routing (509-525); `CoverCorners` per-look radii (682-688, not in Layout); title weight 700/600 by layout (640); decode px choice (624); BPM chip rule (706); caption Tick/edge logic (1121-1150); `CaptionWipeRows` near-pure logic in a UI file (1208-1341) | **Stage.cs:** fonts/sizes (`TitleFont`/`MetaFont`/`CaptionFont`, 258-276, 351-357), all DIP constants (78-102), `Tone` alphas/ms (390-416). **Stage.Layouts.cs:** fonts per look (367-380), cover sizes (269-289), scrim alpha (94), ring metrics (469-470), HeroRules.FootAlpha (188) |
| Stage.Options.UI.cs | pure UI | layout/spectrum section gating (314-325); schematic thumb geometry switch (394-400); `Ink.IsDark` wash (137) | — |
| Stage.Hero.UI.cs | UI over HeroRules | prev/shown layer bookkeeping (134-139); scrim gradient stops per HeroLyrics hard-coded (159-168) | HeroRules.Pan / DecodePx (Shell\Stage.Layouts.cs:184-197) |
| Stage.Artist.UI.cs | ArtistRules (pure) lives in the UI file | the rules themselves (49-159) | — |
| Visualizer.cs / Palette / Moments / Covers / Particles vs *.UI.cs | CORE folds bands and decides kinds; UI binds | **Visualizer.UI.cs Clock:** lease wiring, palette fade state, moments, scope gating (292-562) are orchestration in UI. **Gallery.UI.cs:** `GalleryNav` (181-228) and `SubtitleOf` (400-404) are pure | **Palette.cs:** all colours, contrast floor and arm (37-201). **Particles.Tints:** per-arm colours (68-79). **Covers.Drift.InkAlpha(dark)** (Shell\Visualizer.Covers.cs:322). **Field.BaseOpacity** reads Stage.Tone (Shell\Visualizer.cs:679) |
| Face files | colour by binding to the slab; `pal.Dark` vs `Ink.IsDark` inconsistent | Zune Type and Spotlight read `Ink.IsDark` (Zune.UI.cs:155, 641); other faces read `pal.Dark` (Fluent.UI.cs:62, Classics.UI.cs:118) | — |
| Verse.cs vs Verse.UI.cs | clean | Measure/rig assembly (Shell\Verse.UI.cs:350-455) | `Geometry.GhostAlpha(d, dark)`, ghost scales/weights, sizes (Shell\Verse.cs:743-748); `Region` booleans (724-726) |
| Video.cs vs Video.UI.cs vs Video.Host*.cs | CORE rules; Host holds State/Commit/Prefs; UI surfaces | PipSurface geometry (Shell\Video.UI.cs:1005-1025); `OverlayChildren` placement switch (997-1003); docked cap fit effect (681-707); ToEngine/FromEngine aspect mapping (265-281) | `Pip` sizes (Shell\Video.cs:1374-1384); `PipGesture` margins (1425) |
| Lyrics.cs / Lyrics.Host.cs vs Lyrics.UI.cs | CORE metrics/ladders; Store; UI drives nodes | `Ink` seam colour mapping (Shell\Lyrics.UI.cs:125-155); `RungInk` (2327-2332) | `Surface` font sizes/alphas/shimmer widths (Shell\Lyrics.cs:1456-1521); `Fx` σ ladders (140-166); `Interlude` sizes (1599-1660) |
| OnMedia.UI.cs | UI plus menu builders | speed rates (34); quality rows logic (129-145) | — |
| Deck.cs vs Deck.UI.cs / Faces | physics vs pixels | `SpectrumFold` is logic in UI (Shell\Deck.UI.cs:444-483); `Wash` colour mixing by theme (586-612) | `ClockRules.DiscFrac`/`SideFor`/`RpmFor` (Shell\Deck.cs:1522-1572); faces' literal hex palettes by design (Shell\Deck.Faces.cs:20-21) |
| AiLyrics.UI.Settings.cs | executes Rules | `AiIndeterminate`, `AiDownloadLine`, `AiLanguageName`, language ordering (401-478) | — |

**Smaller mismatches**: `LayoutRules.BackdropScrim` ignores its `kind` parameter and returns a flat 0.42 (Shell\Stage.Layouts.cs:94) while Shell\Stage.UI.cs:422-423 says the scrim is deeper over cover-carrying faces. The Stage.UI header cites Shell.UI.cs:500; the actual mount is Shell\Shell.UI.cs:488-493. The Settings fullscreen toggles bypass the `Prefs.Stage` setters: they write the raw key then `Bump()` (Screens\Settings.UI.Appearance.cs:777).

---

## 4. Presentation switches (key, persistence, sync, branch sites)

Persistence path: `Platform.Settings` is backed by a file or the registry, opened in `Platform.Boot` before the first window (Platform\Platform.cs:1130-1146, 946; Platform\Platform.Host.cs:417-520). Every `Get` is synchronous. On the stage, all `Prefs.Stage` values are copied into StageCtx by SurfaceCore's eager `UseSignalEffect` during the first render before any child mounts (Shell\Stage.UI.cs:255-272); the StageCtx field initializers (89-124) are placeholder seeds overwritten in that same render; accent/palette seeded eagerly (309-316, 485-493). **Every key below is available synchronously before the first frame.**

| Switch | Key / default (definition) | Read / write | Branch sites (count; key lines) |
|---|---|---|---|
| Stage mode | `stage.mode` 0 (Platform\Platform.Settings.cs:43) | Prefs.Stage.cs:27-28; Shell\Stage.UI.cs:204, 257 | ~48: Shell\Stage.UI.cs:209-218, 368, 402, 409, 420, 429, 466, 509, 593, 687, 706, 732, 843, 877, 1396, 1427, 1450; Shell\Stage.cs:43-53, 179-191, 211, 281, 353-357, 400; Shell\Stage.Layouts.cs:71, 116-121, 128, 145, 149; Shell\Visualizer.UI.cs:295, 464; Shell\Visualizer.cs:272 |
| Visualizer face | `stage.visualizer` 9/Bloom (Platform.Settings.cs:46; Shell\Visualizer.cs:85, 120) | Prefs.Stage.cs:29-30; writes Shell\Stage.UI.cs:522, Stage.Options.UI.cs:255, Visualizer.Gallery.UI.cs:232, Screens\Settings.UI.Appearance.cs:785, Screens\StoreShot.cs:123 | 72 Kind refs. Runtime: Face switch (Shell\Visualizer.UI.cs:596-617), BackdropKind (Stage.Layouts.cs:87; Stage.UI.cs:839), ScrimFor (Stage.cs:400), CaptionFriendly (Stage.cs:53), lease NeedsOf (Visualizer.UI.cs:298), Aurora ribbons (468), UseScopeReader (Stage.UI.cs:848), FaceScrim (877), key step (520-523), gallery/options selection (Gallery.UI.cs:288, 311; Options.UI.cs:186) |
| Layout | `stage.layout` 0/Card (Platform.Settings.cs:63) | Prefs.Stage.cs:43-44; writes Stage.UI.cs:212, 517; Options.UI.cs:270, 318; Settings.UI.Appearance.cs:798 | 77 VizLayout refs + 43 Eff/IsLayout lines: Stage.UI.cs:209, 252, 276-290, 402, 409, 420, 429, 466, 513, 593, 601, 632, 640, 682-686, 732, 838-839, 862, 877; Stage.Layouts.UI.cs:37; Visualizer.UI.cs:298, 467; all of Stage.Layouts.cs:64-465 |
| Spectrum style | `stage.spectrum` 0/Bars (:65) | Prefs.Stage.cs:45-46; Options.UI.cs:320-323 | 23: Stage.Layouts.UI.cs:39-57; Stage.Layouts.cs:104, 111-112, 241-242, 271, 291, 297, 325, 330; Stage.UI.cs:268, 281; Visualizer.UI.cs:298 |
| Hero: lyrics over photo | `stage.heroLyrics` true (:67) | Prefs.Stage.cs:47-48; Options.UI.cs:331 | 29: Stage.Layouts.cs:72, 115-121, 243, 273, 287, 345, 359, 371, 378, 424-428; Stage.UI.cs:281, 288, 429, 632, 686; Stage.Hero.UI.cs:142, 159; Stage.Layouts.UI.cs:46-47 |
| Hero: slow pan/zoom | `stage.heroMotion` true (:69) | Prefs.Stage.cs:49-50; Options.UI.cs:332 | 5: Stage.Hero.UI.cs:140; Stage.UI.cs:270 |
| Hero: dimming | `stage.heroDim` 75 (:71) | Prefs.Stage.cs:51-52; Options.UI.cs:340 | 6: Stage.Hero.UI.cs:156, 167; Stage.Layouts.cs:188 |
| Sensitivity | `stage.sensitivity` 1.0 (:48) | Prefs.Stage.cs:31-32; Gallery.UI.cs:144; Settings.UI.Appearance.cs:806 | 13: Visualizer.UI.cs:465, 488; Visualizer.cs:734; Deck.UI.cs:302 (cross-surface) |
| Lyrics over visualizer | `stage.lyricsOverlay` true (:50) | Prefs.Stage.cs:33-34; Gallery.UI.cs:146 | 5: Stage.UI.cs:277; Stage.cs:53 |
| Sync offset | `stage.syncOffsetMs` 0 (:52) | Prefs.Stage.cs:35-36; Gallery.UI.cs:152 (+ `Playback.Audio.SetSpectrumOffsetMs`); Playback\Playback.Audio.cs:564 | 4 (engine-side value only) |
| Calm motion | `stage.calm` false (:54) | Prefs.Stage.cs:37-38; Gallery.UI.cs:148 | ~55: Visualizer.cs:296-337, 734, 801, 827, 836; Visualizer.Moments.cs:34; Visualizer.UI.cs:464, 475, 488; Verse.UI.cs:191, 425; Verse.cs:622, 678-679 |
| Change with the music | `stage.moments` true (:56) | Prefs.Stage.cs:39-40; Gallery.UI.cs:150 | 13: Visualizer.UI.cs:474; Visualizer.Moments.cs:59-66; Verse force Verse.UI.cs:923 |
| Gallery open | `stage.galleryOpen` true (:58) | Prefs.Stage.cs:41-42 | 17: Stage.UI.cs:252, 841-844, 949, 990, 1396; Stage.cs:281-323; Visualizer.cs:274; Verse.UI.cs:189; Verse.cs:785 |
| Tip seen | `stage.tipSeen` false (:60) | Prefs.Stage.cs:53-54 | 2: Stage.UI.cs:368, 378 |
| NPV presentation (cover / deck) | `npv.presentation` 0 (Platform\Platform.cs:254) | Prefs.Player.cs:41-90; developer-gated `ResolvePresentation` (50-51) | Shell\Rail.UI.cs:362-369; Settings.UI.Appearance.cs:172-177; Shell.Palette.cs:315; Rail.Styles.UI.cs:192-194 |
| Player style | `npv.player.style` 0 (Platform.cs:255) | Prefs.Player.cs:68-105; Shell\Rail.cs:262-298 | 2×12-arm dispatch: Shell\Deck.cs:1446-1460; Shell\Deck.UI.cs:199-214; ViewHost key (70-72) |
| Deck options | `npv.player.<preset>.<opt>` 0 (Platform.cs:379) | Prefs.Player.cs:75-112; Shell\Rail.cs:267-305 | 20 ChoiceSlug reads: Deck.UI.cs:164, 172, 803-806; Deck.Faces.cs:48-50, 263, 378, 531-532, 789, 985, 1198-1203, 1345-1346; Deck.cs:1455 |
| Player bar remaining | `playerbar.duration.remaining` true (Platform.cs:184) | Prefs.Player.cs:129-140 | Shell.PlayerBar.UI.cs:1940, 1968 |
| Lyrics secondary line | session only (Shell\Lyrics.cs:1419-1421) | `Prefs.Lyrics` (Platform\Prefs.cs:155) | 7: Lyrics.UI.cs:404-405, 2242-2247; Stage.UI.cs:1072; Verse.UI.cs:683-686 |
| Lyrics blur strength | `appearance.lyrics.blurStrength` −1 (Platform.cs:158) | Shell\Lyrics.cs:1425; forced 0 on stage (191) | 5: Lyrics.UI.cs:406-418; Verse.UI.cs:240 |
| AI lyrics | `lyrics.ai.enabled` / `wordSync` / `plainText` / `onBatterySaver` (Platform.cs:161-164) | AiLyrics.UI.Settings.cs:252-260, 326-333 | header/footer rules in AiLyrics.Rules |
| Video placement (preferred) | `video.placement` "" (Platform.cs:197) | loaded at `Video.Install` (Video.Host.Wiring.cs:60-62); persisted (88-92) | ~91 SurfacePlacement refs (Video.cs, Video.UI.cs, Video.Host.cs) |
| Video aspect / custom ratio | `video.aspect.mode` "fit", `video.aspect.customRatio` 16/9 (Platform.cs:203-204) | Video.Host.cs:246-263; synced eagerly at Video.UI.cs:926 | 29: Video.UI.cs:265-281; OnMedia.UI.cs:101-123 |
| Pop-out always-on-top | `video.window.ontop` true (205) | Video.Host.cs:239-243 | 9: Video.UI.cs:208-213 |
| PiP rect | `video.pip.rect.v2` (201) | Video.UI.cs:912, 1186 | 1 |
| Video quality pin | `playback.video.quality` 0 (216) | OnMedia.UI.cs:131 | 1 |
| Autoplay (queue pane) | `playback.autoplay` true (231) | Stage.UI.cs:1810, 1840 | 1 |

**Derived switches (not persisted)**: aspect class ~57 lines (Stage.cs 23; Stage.Layouts.cs 12; Stage.UI.cs 646, 1355, 1427, 1458, 1503, 1506; Verse.cs:773-801). Theme arm (`Design.StageInk.IsDark`, Platform\Design.cs:634; pure `Arm` 627) ~113 lines: Palette Visualizer.Palette.cs:48-200; faces Fluent.UI.cs:62-801, Classics.UI.cs:52-485, ITunes.UI.cs:39-206, Zune.UI.cs:155, 641-644; Verse.UI.cs:199-831; lyrics bloom Lyrics.UI.cs:152-154; Deck.Faces.cs:550, 681; Deck.UI.cs:605; Stage.UI.cs:121, 124, 483; Stage.Options.UI.cs:137. Colour from art: `Ink.Accent(url)` → AccentSet / `Visualizer.Palette.From` (Stage.UI.cs:475-495; Design.cs:663-666); WMP deck "colour=cover" (Deck.Faces.cs:1200-1203); Deck wash (Deck.UI.cs:599-606). OS reduced motion (`Design.Reduced`): ~126 occurrences across every family file.

**Developer-mode switches:** NPV presentation (Prefs.Player.cs:50-51; Rail.cs:259); lyrics DebugLayer (Lyrics.UI.cs:2416-2426).

**Stale guide** — `docs\guide\fullscreen-visualizers.md`: lines 52-55, 57-66, 119 say 8 kinds with default Horizon (code: 26 kinds, 19 shown, default Bloom); lines 37-38, 210 say a 30 Hz clock (now per frame, Visualizer.UI.cs:279-284; Visualizer.cs:927); the key table (114-125) omits `stage.moments`, layout, spectrum and the three hero keys.

---

## 5. How the shell hosts these

**Stage mount**: `Flow.Show(Ui.ImmersiveLyrics)` with Stage.EnterTerminal/ExitTerminal around `Stage.View()` (Shell\Shell.UI.cs:488-493); signal at Shell\Shell.cs:1116. Doors calling `Stage.Open`: rail lyrics header ⛶ (Shell\Rail.UI.cs:270); player bar Expand (Shell\Shell.PlayerBar.UI.cs:608) and overflow (1274); F11 (Shell\Shell.UI.cs:788); diagnostics/bench/StoreShot (Screens\StoreShot.cs:126; Screens\Diagnostics.Probe.FrameBench.cs:297, 636).

**Taking over the window**: chrome row, content region and player bar dock collapse through bound `Visible = s_chromeMounted` (Shell\Shell.UI.cs:469, 471, 646, 652-653); rule `FrameRules.ChromeMounted(resolved, immersive)` (Shell\Shell.cs:1906-1907). OS borderless fullscreen through `InputHooks.WindowSetFullscreen` with prior-state restore (stage Stage.UI.cs:318-324; video Video.UI.cs:1388-1393). Focus scope push/pop (Stage.UI.cs:327-342; Video.UI.cs:1397-1412); cursor hidden via `SetCursorOverride` (Stage.UI.cs:551-556).

**Key routing**: stage root handles Esc/F11 first (Stage.UI.cs:505-506); shell fallbacks `ToggleVideoFullscreen` via `FrameRules.F11` (Shell.UI.cs:781-790; Shell.cs:1930-1934); `OnShellKey` Escape via `FrameRules.Escape` (Shell.UI.cs:797-811; Shell.cs:1917-1922). Stage reuses `Shell.PlayerKey`, `Shell.BarKeyStep`, `Shell.TogglePlayPause`, `Shell.SeekBar(onScrubbing)`, `Shell.TimeText`, `Shell.TransportFacts`, `Shell.ToggleShuffle`/`CycleRepeat`, `Shell.DevicePickerMenuItems`, `Shell.DevicePicker.IsRemote`/`DeviceRoster`, `Shell.LinkFor`/`GoTo`/`For`/`Parse` (Stage.UI.cs:527-539, 1491, 1529, 1551-1555, 1653, 1687-1689, 1728, 1778; Stage.Artist.UI.cs:286, 413).

**Injected seams:** `Stage.QueuePaneBody` (Entities\Queue.UI.cs:66); `Stage.NowPlayingMenu` (Platform\Actions.UI.cs:600, installed from Shell\Shell.UI.cs:62).

**Video layers**: `Video.PipLayer()` and `Video.FullscreenLayer()` above the stage in the shell ZStack (Shell\Shell.UI.cs:497-498); docked cap mounted by the rail (Shell\Rail.UI.cs:144); watch-page stage by Platform\Modules.UI.cs:234; video fullscreen collapses the chrome through the same `s_chromeMounted`; transport ownership `MainWindowHole.OwnerFor` / `State.Transport` (Shell\Video.cs:799-805; Shell\Video.Host.cs:84, 173).

**Rail-hosted surfaces:** lyrics `Lyrics.View()` (Shell\Rail.UI.cs:129), NPV peek (593), Deck hero (362-369).

**Settings mirrors:** Appearance › Full screen rows read/write `Prefs.Stage` (Screens\Settings.UI.Appearance.cs:152-166, 773-818), epoch-subscribed at Screens\Settings.UI.cs:137; NPV rows at Settings.UI.Appearance.cs:172-179, 749-758; AI card mount at AiLyrics\AiLyrics.UI.Settings.cs:39-40.


# Part 5 — Platform: Design tokens, shared controls, OS surfaces, settings persistence


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


# Part 6 — Screens


> Agent summary: (1) Theme (`Keys.ThemeMode`): 5 direct key sites (3 reads, 2 writes). **Four separate copies** of the mode→`ThemeKind` + `SeedPalette`/`Tok.Use` + `SetAccent` logic: `Settings.UI.Appearance.cs:195-213`, `Shell.Host.cs:674-690`, `Shell.UI.cs:699-708`, `Shell.Palette.cs:267-273`. 4 `ThemeControl.Request` consumers. (2) Live theme-branch readers: 74 code lines in 19 files (33 in Design.cs) + 3 `_theme` signal subscribers + 1 `Tok.Epoch` read. **Four ways to flip the theme**: Settings row, caption button, profile menu, palette row. (3) Zoom: `ZoomMode` 6, `ZoomLevel` 5, bootstrap 2; `FluentApp.SetZoom` 5, `FluentApp.Zoom` 4, `Viewport.Zoom` 4. (4) `Prefs.Appearance` epoch keys (direct / derived): Marquee 2/2, ColorWashes 2/5, PageMotion 4/2, RowDensity 6/5, HideTrackArtwork 3/11, TrackRowStyle 4/4, DaylistClock 4/3; same epoch but not in Settings: LikedCoverStyle 3, TempoColumn 3, PlaysColumn 4. (5) `Prefs.DetailHero`: DetailPageLayout 5, DetailRailUniform 3, + 8 rail keys; only reader `Detail.UI.cs:670-672`. `Prefs.Lyrics`: blur 5 key / 3 reads. (6) `Prefs.Stage` (7 Fullscreen rows): 20 key refs, 16 derived readers, 9 other writer sites. (7) NPV: 10 key refs, ~19 `PlayerPrefs` reads/writes. (8) Sidebar: 6 key sites; `Layout`/`Density` signals ~40 reads. (9) **Options that do not exist**: accent picker, transparency/Mica, fonts/font size, app-wide density; motion = developer-only page-motion row + OS reduced-motion. (10) StoreShot has no screen; Recovery is an engine-free Win32 TaskDialog (OS theme only).

Short names: `Screens\X.cs` = `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\Screens\X.cs`. `Shell\`, `Platform\`, `Entities\` are siblings under `...\src\apps\Wavee\`.

## 0. Registration and mounting

- `App.cs:151` → `Setup.BootstrapInstall()`, before any store opens.
- `App.cs:222` → `Settings.InstallScreens()`, at `Screens\Settings.UI.cs:45-53`: `Shell.SetPage(RouteKind.Settings, …Page())` (47); `Shell.OnSettingsTabRequested` for `route=settings&arg=<slug>` (49); `ReleaseNotes.InstallUi()` (`Screens\ReleaseNotes.UI.cs:46-50`: `SetPage(WhatsNew)` + `InstallSeams()`); `Feedback.InstallUi()` (`Screens\Feedback.UI.cs:41-43`, sets `Shell.ReportDialogOpener`); `Setup.InstallWizard()` (`Screens\Setup.UI.cs:441-447`).
- `App.cs:223` → `Diagnostics.Install()`, at `Screens\Diagnostics.UI.cs:58-84`: `Crash.Host.BeginGuiRun` (`Platform\Crash.Host.cs:624`); `SetPage` for `PlaybackDiagnostics` (67), `ConnectDiagnostics` (68), `CaptureDiagnostics` (69), `Logs` → `LogsPage.Page()` (70); fills `Settings.SendTestEvent`, `Feedback.DiagnosticsText`, `Feedback.PastSessionLog`.
- Route table: `Shell\Shell.cs:164-203`; field 6 `DeveloperOnly` (`Shell.cs:134`) true for playback-, connect- and capture-diagnostics. `Shell.PageFor` `Shell.cs:230-236`. `FrameRules.BodyFor(route, hasPage, developerMode)` (`Shell.cs:1973`) picks NotFound for a developer route while developer mode is off (called from `Shell\Shell.UI.cs:983`).
- Overlay chromes, mounted in order at `Shell\Shell.Overlays.UI.cs:118-132`: runtime banner (119), toast watchers (120-122), `Setup.SignInDoor()` (123); then, load-bearing order: `Setup.WizardChrome()` (128), `Feedback.Chrome()` (129), `Crash.Chrome()` (130), `ReleaseNotes.AfterUpdateChrome()` (131).
- FPS overlay: `Diagnostics.FpsOverlay()` mounted at `Shell\Shell.UI.cs:504`.
- Separate engine-free window: `Recovery.Run` from `App.cs:118-121`, before `Platform.Boot`.

## 1. Screens and sub-parts

### 1.1 Settings
Page in `Screens\Settings.UI.cs`.
- **Page component:** `SettingsPageView` 107-184: captures `Overlay.Service`, `InputHooks`, `ThemeControl.Request`, `Viewport.Zoom`/`Size` into statics (112-118); seed effect once per mount (120-131); `UseActivation` deactivation calls `EnterPlayback(false)` (133); subscribes `s_epoch`, `Prefs.PlayerBar.Epoch`, `Prefs.Stage.Epoch` (135-137); switches tabs (146-155).
- **Layout:** each tab body a `ScrollView` keyed `settings:scroll:<slug>` (161-166); header (199-209), `SelectorBar` tab strip + divider (178), content column capped at 1000 (`ContentColumn`, 212-215).
- **Tab model:** `Settings.cs:33-48` — 7 tabs, slugs `general/appearance/playback/notifications/storage/privacy/about`. `Open(Tab)` `UI.cs:59-63`.
- **Row grammar (`Settings.UI.cs`):** `SectionHeader` 225-248, `Row` (SettingsCard) 251-266, `Item` 269-272, `ValueTag` 275-278, `ExpanderPanel` 282-287, `Toggle` (writes the store, optional after-write hook, then `Bump`) 291-297, `ConfirmThen` 300-301, `Loading` 304-309, `Glyph` name→Icons map 324-342, `RowVisible` 358.
- **Catalog of sections/rows with glyphs:** `Settings.cs:57-244` (sections 69-100, rows 102-216; developer-only rows `pageMotion` 122, `npvPresentation` 153).

**General** (`GeneralTab`, `Settings.UI.cs:362-386`): Language & region combo (368-370, `SetLanguage` 396-402; nl/ko shown disabled `Settings.cs:541-555`); spotify: links toggle (406-413; greyed on packaged builds; scheme synced `Settings.Host.cs:36-48`); Notification area (`AddTrayRows` 417-468): icon mode combo (425-441), close to tray (443-446), minimize to tray (448-451), start hidden (453-457), start on login (459-467), disabled with reason caption; Graphics `GpuPickerCard` (473-528: enumerates adapters at construction 476-482; `GpuAdapterInfo.RequestAdapterSwitch`).

**Notifications** (`NotificationsTab` 535-569): blocked InfoBar (543, 708-722; "Open Windows settings" only for App/User block); Delivery: Windows toggle (546-547), sound/quiet hours dependent rows (549-552, 674-675), quiet range hour combos (553-554, 677-700); Topics: hint (557-561), "Windows off" InfoBar (562-564), per-topic `SelectorBar` ladders (571-604); developer mode makes a topic row an expander with "Send a test event" (592-603; `RunTestEvent` 608-623).

**Appearance** (`Screens\Settings.UI.Appearance.cs`, `AppearanceTab` 95-182) — every row listed in §4. Theme section 108-128, Lists 130-137, Sidebar 139-141, Lyrics 145-149 (+ `AiLyrics.SettingsCard()` `AiLyrics\AiLyrics.UI.Settings.cs:39`), Fullscreen 152-166, Now playing 168-179. Child components: `ZoomPicker` 247-299, `DaylistClockPicker` 442-459, `SidebarLayoutCard` 547-588, `SidebarDensityPicker` 591-601, `NpvStylePicker` 749-760, `StageVisualizerPicker` 779-787, `StageLayoutPicker` 793-800, `StageSensitivitySlider` 801-808, `StageSyncSlider` 809-821. Picker-card faces: `DensityCard` 346-351, `TrackListStyleCard` 382-389, `PageLayoutCard` 491-540, `SidebarLayoutCardFace` 619-645 (+ `ActiveTag` 647-652, previews 655-709). `SidebarLayoutCards` public (605-617); the setup Layout page reuses it.

**Playback** (`Screens\Settings.UI.Playback.cs`, `PlaybackTab` 102-144): `RuntimeCard` 151-192 (ready expander with facts + diagnostics links 154-172; not set up: Informational InfoBar + "Set up" 175-180; problem: Error InfoBar + links + Retry setup 183-191; `DiagnosticsLinks` 196-206, `RuntimeFact` 209-213, `RuntimeSignatureItem` 216-227 → `Shell.OpenSignatureDialog`); Audio: quality combo (`QualityCombo` 261-271; Lossless only when `Spotify.Audio.CanDerive`), metered combo with live status description (275-294), remember volume (111-112), autoplay (113-114), normalization (116-118), mode combo (119-124), album normalization (125-127); Sound: `EqualizerGroup` 324-360 (preset combo, `Controls.EqualizerCurve`, reset link), `CrossfadeGroup` 407-432; Video: quality (453-461), metered quality (465-472), prepare ahead (478-481), `VideoOverridesRow` 487-523 (store absent → greyed; 0 rows → "Manage"; N rows → "Manage" + "Remove all" with confirm; flyout toggle 526-540; deep link `OpenVideoOverrides` 545-558); Player bar: show remaining (141-144). Video-override flyout body `Screens\Settings.UI.Video.cs`, `VideoOverrideManager` 38-312 (root view search + recently added or results + "Browse all"; leaf view whole roster 75-161; full row Replace/Locate/Show in Explorer/Remove + undo toast 196-242; `StatusChip` 278-292).

**Storage** (`Screens\Settings.UI.Storage.cs`, `StorageTab` 95-172): census block (Failed InfoBar + Retry / spinner / `StorageUsageCard` 101-115, 209-258); `StorageRow` with hue accent (181-205): library, runtime, logs, local store, image cache; Playback cache: cache audio/keys toggles, `BodyBudgetControl` (262-322: Fixed / Drive share / Unlimited, meter, reserve line), cache location (choose/default/open; two-dialog flow `Settings.Host.cs:272-341`), audio bodies clear, license keys clear; Metadata cache budget combo (`SetMetaBudget` 336-344), clear; Reset: factory reset with confirm (`Settings.Host.cs:348-360`).

**Privacy & diagnostics** (`Screens\Settings.UI.Privacy.cs`, `PrivacyDiagnosticsTab` 41-63): `WhatLeavesExpander` 69-84; `CrashReportsExpander` 90-114 (radio `ModeItem`s 118-131, include-dump toggle, contents); `CrashServiceCard` 145-180 (Copy id, Delete my data); `PrivacyPolicyCard` 182-184; `SavedReportsGroup` 192-323 (queued InfoBar Send now/Discard, report rows with "…" `MenuFlyout` View/Copy/Send…/Delete, Open folder/Delete all); Logs: `LogViewerCard` 400-439 (750 ms interval, routes to `logs`), `DetailLevelGroup` 445-475, `LogFilesCard` 479-483, `ReportProblemCard` 485-487; Tools: `ToolLinkCard` ×2 (492-494), `RealtimeCaptureExpander` 498-511; Developer: `DeveloperGroup` 524-559 (developer mode, FPS overlay, simulate update, send test crash report 565-594; greyed not hidden while off). Pure decisions `Screens\Settings.Privacy.cs:19-120`.

**About** (`Screens\Settings.UI.About.cs`, `AboutTab` 36-44): `AboutUpdatePanel` 109-137 (Store build: one Store card; otherwise status card 211-233, channel link, install-on-quit, metered; always auto-show what's-new); `UpdateHero` 141-207 using `AboutRules.Hero` (`Settings.cs:644-659`); `AboutLinksCard` 49-78 with unread `InfoBadge.Dot`; `ReceiptsCard` 237-319 (5 s interval); `LicenseExpander` ×2 (82-103).

**Side effects** in `Screens\Settings.Host.cs` (census 126-189, clears 218-268, relocation 272-341, factory reset 348-360, notices 394-421, diagnostics copy 425-440). Pure rules in `Screens\Settings.cs` (EQ 248-309, quality 313-360, metered line 364-404, storage 410-469 and 607-629, notify 473-535, language 539-555, receipts 559-603, about 631-673).

### 1.2 Setup wizard and sign-in door
Pure rules `Screens\Setup.cs`: `Cover` 37; `Layout` 41-104 (plate 762×490, min 320×184, icon breakpoint 770, footer 80, progress column 210; `HeroAsset` 98-103); `Commands.Resolve`/`ResolveFor` 123-141; SignIn rows 143-151, LocalPlayback rows 153-163; `WizardRules` 224-268; `SignInPresentation` 273-301; `Recolor` 306-330; `RuntimeRules` 401-563; `WizardPage` 577; `WizardEntry` 581; `Gating` 586-674; `Bootstrap` 681-733; `SignInFacet` 736; `SignInRules` 739-811; `QrPlate` 815-819; `Qr` encoder 824–end. Disk probe `Screens\Setup.Host.cs:28-81`.

UI `Screens\Setup.UI.cs`:
- **Sign-in door:** `SignInDoor()` 59-60 (empty under `--fake`); `SignInDoorView` 72-102 opens on request or on entering `SignInRequired` with no credential, closes once Online. `OpenSignIn` 104-124: 460 `ContentDialog` with `SignInBody` (168-237: facets Busy / Done / cards) and `SignInFooter` (376-416). Shared parts: `BrowserCard` 143-151, `PremiumRow` 155-164, `PairingCard` 244-321 (1 Hz countdown), `QrGrid` 329-372 (fixed black-on-white).
- **Wizard:** `WizardMarkerEpoch` / `WizardOpen` / `Covering` 426-434; `WizardChromeView` 454-475 (opens once per launch, 1 or 2 posts); `OpenWizard` 479-504 (Modal overlay, `ScrimVisual = false`; `ClosingAction` veto unless `EscapeClosesPlate`); `WizardSession` 511-658 (page/dir signals, Primary/Secondary/Back); `WizardPlate` 669-731 (Enter/Backspace keys; `Flow.KeepAlive` with `Design.Nav.RecipeFor(Dir)`; back overlay); `WizardFooter` 763-812; `WizardFrame`/`PageFrame` 818-867 (icon column only at ≥770, `setup:layout:wide` vs `compact`); Lottie heroes 871-909 with `RecolorLive` from accent tokens (887); steps: page 0 `TermsPage` 931-958 (+ `CrashConsentCard` 967-1007), page 1 `SignInPage` 1011-1106, page 2 `LayoutPage` 749-759, page 3 `LocalPlaybackPage` 1172-1267; `LoginStepRow` 1110-1147, `LoginStepBar` 1150-1168, `DownloadCard` 1270-1280.

Runtime card (`Screens\Setup.UI.Runtime.cs`): `RuntimeHost` seam 60-73; `Runtime` cells 77-131; `RuntimeModel` 137-497; standalone dialog `OpenRuntimeCard` 504-523 (460 ContentDialog; closing vetoed while busy); `RuntimeBody` 535-602; pieces 606-809; `RuntimeFooterView` 816-894.

### 1.3 Release notes (What's new)
Route `whatsnew` (`ReleaseNotes.UI.cs:48`), arg = semver. `Page` 117-224: load → `Frame` 226-244, `LoadingState` 246-250, `EmptyState` 252-261, `SinceBanner` 210-223, `Hero` 278-323, `Pill` 326-340 (public; About and Setup reuse), `Strip` 355-374, `CardView` 435-553, `SectionView` 559-632, `ChangelogRow` 636-669, `IssueChip`/`PrChip`/`Chip` 672-709, `AvatarStack` 722-743, `ReleaseRail` 777-845 (208 wide; absent with no index). Highlight viewer `OpenViewer` 853-862 / `ViewerView` 864-1164 (own veil `rgba(0,0,0,.72)`; `Band`, `PagerRow`, `TextStack`; `HighlightViewerMotion` 1172-1196). After-update plate: `AfterUpdateChromeView` 1211-1229; `LoadThenOpenAsync` 1234-1259; `OpenPlate` 1263-1272 (Modal, `ScrimVisual = true`); `PlateView` 1277-1347. Doors: `ShowSummaryAgain` 66-71; `RunningNotesUnread` 74-75; `VersionDisplayText` 79-89. Loaders `ReleaseNotes.Host.cs:97-168`; store `:238`. Pure `ReleaseNotes.cs` (Links 43-73, AfterUpdateGate 98, ChangelogParser 198, MarkdownLite 422, Range 748, Validation 860, HighlightCardMetrics 1456-1550, HighlightViewerLayout 1561-1632). Records `ReleaseNotes.Model.cs`.

### 1.4 Feedback (report dialog)
`Screens\Feedback.UI.cs`: `ChromeCore` 92-112 (driven by `Requests` signal 77-88); `OpenDialog` 126-149 (548 ContentDialog, primary "Open GitHub", vetoable); `DialogBody` 165-194 (Segmented Bug/Feature/Question/Idea; hidden for a crash report); `DialogCard` 199-417 (title box, per-kind fields 313-339, include-logs checkbox, preview with Copy/Save as, crash InfoBar 348-357); `ReportComposer` 440-537. Pure `Screens\Feedback.cs` (channels 41-108, bundle 193-372, form 386-499, URL 506-585, redaction 592-715).

### 1.5 Logs page (route `logs`)
`Screens\LogsPage.UI.cs`: `Page` 49; `LogsPageView` 77-670: `HeaderBar` 205-247 (breadcrumbs Settings › Privacy › Logs, picker caption, session combo, Open folder); `Toolbar` 266-309 (AutoSuggest filter, Segmented levels, category combo, Copy/Export/More); `OpenMore` 313-344 (toggles Newest first / Group repeats / Wrap / Verbose, Clear view confirm, Report this session); `ColumnHeader` 478-495; `LogBody` 513-547 (loading/empty/`ItemsView` measured virtual); `LogRow` 558-603 (expand drawer); `Footer` 646-669; Esc/Back via `GoBack` 62-66. Pure `Screens\LogsPage.cs:21-61`.

### 1.6 Crash (in-app)
`Screens\Crash.UI.cs`: `Chrome()` 46; `ChromeCore` 48-120 with three effects (A: this launch's bundle → prompt, silent upload or toast, deferred while the wizard is pending/open; B: hang toast; C: `--crash-probe` arm); `OpenPrompt` 128-147 (548 ContentDialog; Send disabled when uploader not configured); `CrashPromptBody` 222-375; `OutcomeToasts` 169-204. Platform side `Platform\Crash.Host.cs` has no UI: `ReportsVersion`/`HangReported` signals (146-147), `ThisLaunch` (152), `ArmProbe` 588-608, `BeginGuiRun` 624-670. `Platform\Crash.Launch.cs` no UI.

### 1.7 Recovery (separate Win32 window)
`Screens\Recovery.cs`: `Reason`/`Outcome`/`ActionKind` 29-35, `ButtonIds` 41-45, `Actions.For` 59-69, `Text` 90-182 (English fallbacks + loc), private `Loc` 189-225. `Screens\Recovery.Win32.cs`: `Run` loop 30-73; `ShowMain` 76-118 (`TaskDialogIndirect` with command links, privacy hyperlink footer, "always send" verification; fallback `ShowMainFallback` `MessageBoxW` 122-145); send confirm 199, result 217, reset confirm 230, `TryShowTaskDialog` 326-404. Presentation entirely the OS comctl32 dialog: no app theme, accent or zoom.

### 1.8 Diagnostics (UI only)
`Screens\Diagnostics.UI.cs`: `RuntimePageView` 267-491 (compiled-in Status, current status, Locate, Candidates, Verify, Modules, Updates, Stage card, `PlaybackHealthView` 505-545, `PodcastWireCheckView` 562-680, Copy/Open log folder/Refresh); `ConnectPageView` 689-775; `FpsOverlay` 146-156 / `FpsPill` 160-175; shared chrome `PageHeader`/`PageFrame`/`Card`/`Row`/`Chip`/`Status` 181-257; `LyricsInspector` dialog 789-1360. `Screens\Diagnostics.Capture.UI.cs`: `CapturePage` 40 / `CapturePageView` 45 (status, anomalies, roots with in-place tree; 750 ms poll). `Screens\Diagnostics.Scroll.cs`: `ScrollSettings` 63-130 (persists `ScrollProbeLevel`/`ScrollFeelProfile`), `ScrollCardView` 135. `Screens\Diagnostics.Tiles.cs`: `TileDiagRules` 31-122, `TileCardView` 129. `Screens\Diagnostics.Evidence.cs`: `EvidenceCardView` 31. Not mapped: `Diagnostics.Probe*.cs`, `Headless*.cs`, `FrameBench.cs`, `BenchHooks.cs`, `EvidenceAuto/Bundle/Report.cs` (probe arms, headless runner, benchmarks).

### 1.9 StoreShot
`Screens\StoreShot.cs` (§6).

## 2. States

**Settings**: loading (Storage census `Settings.UI.Storage.cs:101-115`, phase `Settings.cs:413`, `Settings.Host.cs:126-155`); error (storage failed InfoBar + Retry; runtime problem InfoBar `Settings.UI.Playback.cs:183`; notification blocked `Settings.UI.cs:708`; "Windows off" 562); empty (video overrides `Settings.UI.Video.cs:88-98`; saved reports "none" `Settings.UI.Privacy.cs:224`); offline/metered (metered status line `Settings.cs:364-404`); disabled with a reason (tray rows, spotify links, crash Automatic without a service, developer items, sidebar items while editing `Settings.UI.Appearance.cs:556-570`); responsive: no window-width tiers (only `SettingsCard` engine wrap thresholds and the 1000 column `Settings.UI.cs:71`); transitions: none on tab swap (N7, `UI.cs:158`); theme flip asks for a 250 ms cross-fade (`Appearance.cs:211`); flyouts: video-override manager (`Settings.UI.Playback.cs:530-537`, `PageSlideForward`/`Back` inside `Settings.UI.Video.cs:67`), saved-report "…" menu (`Settings.UI.Privacy.cs:316-319`); dialogs: `Controls.Confirm` (rail reset, storage clears, factory reset, delete data, delete reports, Remove all), cache relocation dialogs (`Settings.Host.cs:282-313`), signature dialog, crash send prompt; interaction: `Interaction.Subtle` rows in the video manager (`Settings.UI.Video.cs:126,147,192`), picker cards (`Controls.PickerCard`).

**Setup**: sign-in facets Idle/Busy/Done/Failed/Expired/Premium (`Setup.cs:736`, `Project` 752-765); offline = Failed facet with `NetworkError` (`Setup.cs:778`); runtime phases Offer, FetchingCatalog, Downloading, Verifying, Untrusted, Ready, Failed, Advanced + model-null arm (`Setup.UI.cs:1177-1180`); narrow tier: icon column only at ≥770 (`Setup.cs:65`, `Setup.UI.cs:849-865`), footer progress column collapses (`Setup.UI.cs:774-792`); motion: page KeepAlive recipe, body Enter/Exit Dy 6/-4 on facet/phase change (926-927, 1042, 1189), login step mark scale-pop (1125-1131), step bar reflow ease (1152-1159), Lottie with `SnapEnd` under reduced motion (881-884), 250 ms cover scrim (`Shell\Shell.UI.cs:870-881`).

**ReleaseNotes**: loading/empty (`ReleaseNotes.UI.cs:141, 246-261`); rail absent offline or without an index (780); stacked multi-release view with since banner; card hover/press `Interaction.Card`, store card 0.985 press (458-476); `ReadMoreRow` hover/focus ink (539-552); viewer chrome hover fill/scale, dimmed ends (1034-1050); pips width tween 120 ms (1086-1093); image fade 140 ms (995); slide motion (1172-1196).

**Feedback**: composing ("Preparing") vs ready (`Feedback.UI.cs:275-277`); crash InfoBar three states (348-357); title required / still preparing toasts (241-252).

**LogsPage**: session caption Reading/NoneOnDisk/Failed (`LogsPage.cs:44-48`); loading-session spinner and empty-filter state (`LogsPage.UI.cs:515-527`); row expanded `Fill` + `Design.Reveal.Resize`/`Drawer` (567-600); `Interaction.ListRow` (584); time header hover (507-509).

**Crash prompt**: from-list vs this-launch; hang vs crash title; "read full" toggle; not-configured note (`Crash.UI.cs:134-136, 273-293`).

**Diagnostics**: runtime page three headline states (`Diagnostics.UI.cs:313-320`); presence chips; FPS pill only with developer mode + overlay on + not presenting (152-153).

## 3. Logic vs presentation
Intended split: `X.cs` = engine-free decisions; `X.Host.cs` = disk/OS/network; `X.UI.cs` = layout and wiring. Breaks:

**UI files that own logic or persistence**
- `Settings.UI.Appearance.cs:195-213` `SetTheme`: mode→kind, palette seed, OS accent, persist, transition — duplicated in `Shell\Shell.Host.cs:674-690`, `Shell\Shell.UI.cs:699-708`, `Shell\Shell.Palette.cs:267-273`.
- `Settings.UI.Appearance.cs`: `ZoomPicker.Pick`/`ApplyZoom` 279-298 write `ZoomMode`/`ZoomLevel` and call `FluentApp.SetZoom` (the shell persists zoom too, `Shell.UI.cs:305-319`); `ResetRailSizes` 474-487 writes 8 keys; `HasCustomizedRail` 462-470; `SetLyricsBlur` 729-744; `StageSyncSlider` 814-819 calls `Playback.Audio.SetSpectrumOffsetMs`; `StageToggle` 777 writes `Stage*` keys directly through `Toggle` instead of `Prefs.Stage.Set*`.
- `Settings.UI.cs`: `Toggle` 291-297; `GpuPickerCard` enumerates DXGI at construction (476-482), `Pick` persists and switches (512-527); `ToastDelivery()` OS query 702-706; `SetLanguage` 396-402; tray writers 425-467.
- `Settings.UI.Playback.cs`: EQ/crossfade persistence + live DSP push (374-449); `File.Exists` during render (219); `ClearAllVideoOverrides` 560-569.
- `Settings.UI.Video.cs`: picker/attach/remove/Explorer launch (213-275); entity lookups 306-311.
- `Settings.UI.Privacy.cs`: `BundleBytes` does `FileInfo` I/O per render via `Tally` (326-337, at 205); `ViewReport` launches Notepad (369-373); `CopyReport` 378-391; `SendTestCrashReport` 565-594; `CrashServiceCard` network delete 158-168; `SetCrashMode` 135-140.
- `Settings.UI.Storage.cs`: `SetMetaBudget` mutates `Store.Policy` and calls `Store.Tick` (336-344); budget writes 280-330.
- `Settings.UI.About.cs`: `ReceiptsCard.Tick` reads process/GC/D3D12 and composes static `ReceiptsText` with hard-coded English (255-317).
- `Setup.UI.cs`: `WizardSession` 511-658 is the whole wizard state machine (terms write 581, `MarkCompleted`/`MarkDeferred`, `Logout`, `Tray.Host.Quit`); `CrashConsentCard` writes `CrashReporting` (993); `LoadHero` disk I/O 889-898.
- `Setup.UI.Runtime.cs`: `RuntimeModel` 137-497 (network tasks, settings writes 111, 422).
- `ReleaseNotes.UI.cs`: writes `ReleaseNotesLastSeen` on open (137); after-update gate/latch/load (1201-1259); plate checkbox writes `ReleaseNotesAutoShow` (1332); `DeveloperMode` read + deep-link parse (1155).
- `Feedback.UI.cs`: `ReportComposer` file I/O and redaction (440-537); `OpenGithub` writes a file and opens a URL (396-416); `SaveReport` 370-392.
- `LogsPage.UI.cs`: disk walk/load/export (351-473); `SetVerbose` write (328).
- `Crash.UI.cs`: consent policy + `Uploader.Enqueue` in effect A (62-91); prompt submit writes settings (260-264); `ComposePreview` reads files (153-161).
- `Diagnostics.UI.cs`: `Install` composition root 58-84; `InfoText` 110-122. `Diagnostics.Scroll.cs`: settings persistence in the card file (84-110).

**Logic/host files that build visuals**: `Settings.Host.cs:282-313` relocation `ContentDialog`s; `Settings.cs` glyph names in the catalog (102-216), `AboutRules.HeroTone` (633-659); `Setup.cs` `Layout` pixel geometry (41-104), `RuntimeRules` widths (405-420), `Recolor` colour math (306-330); `ReleaseNotes.cs` `HighlightCardMetrics`/`HighlightViewerLayout` (1456-1632); `ReleaseNotes.Host.cs:51-79` `KindPillLocKey`/`SectionTitleLocKey`; `Diagnostics.Tiles.cs:31-122` `TileDiagRules` beside the card.

## 4. Presentation options exposed in Settings, and every reader
Write mechanics: every `Platform.Settings.Set` bumps `Platform.SettingsChanged` (`Platform\Platform.cs:972-982`); subscribers `Settings.UI.Privacy.cs:450`, `StoreShot.cs:50`, `Shell\Stage.UI.cs:1810`, `Entities\Queue.UI.cs:325`. Settings-page writers also call `Bump()` (`Settings.UI.cs:90`). Epochs in `Platform\Prefs.cs` (Appearance 55-141, Lyrics 155-238, AiLyrics 242-272, DetailHero 279-310, `BumpAll` 322-330), `Platform\Prefs.Stage.cs:20-54`, `Platform\Prefs.Player.cs:29-140`.

| # | Settings row (file:line) | Key | Writer | Notifies |
|---|---|---|---|---|
| 1 | Theme SelectorBar `Settings.UI.Appearance.cs:110-112` | `ThemeMode` (`Platform.cs:111`) | `SetTheme` 195-213 | engine theme epoch (`Tok.Use`), `ThemeControl.Request` cross-fade, `SettingsChanged`, page Bump; no Prefs epoch |
| 2 | Zoom combo 114-115 / `ZoomPicker` 247-299 | `ZoomMode` (151), `ZoomLevel` (148) | `Pick`/`ApplyZoom` 279-298 + `FluentApp.SetZoom` | `Viewport.Zoom` context, `SettingsChanged`, Bump |
| 3 | Marquee 116-117 | `MarqueeEnabled` (142) | `Toggle` + `Prefs.Appearance.Bump` (302) | `Prefs.Appearance.Epoch` |
| 4 | Colour washes 118-119 | `ColorWashesEnabled` (144) | same | `Prefs.Appearance.Epoch` |
| 5 | Page motion (developer-only) 125-128 | `PageMotionStyle` (127) | `SetPageMotionStyle` 229-234 | `Prefs.Appearance.Epoch` |
| 6 | Row density cards 322-343 / `SetDensity` 355-360 | `RowDensity` (115) | `Prefs.Appearance.Set` | Appearance epoch |
| 7 | Hide track artwork 334-340 | `HideTrackArtwork` (119) | `Prefs.Appearance.Set` 337 | Appearance epoch |
| 8 | Track list style 362-378 | `TrackRowStyle` (116) | `Prefs.Appearance.Set` 374 | Appearance epoch |
| 9 | Track page layout 393-436 | `DetailPageLayout` (135) | `Prefs.DetailHero.Set` 431 | `DetailHero.Epoch` |
| 10 | Keep left rail same size 399-406 | `DetailRailUniform` (179) | `Prefs.DetailHero.Set` 403 | `DetailHero.Epoch` |
| 11 | Clear all remembered sizes 412-418 | `Detail{Album,Playlist,Liked,Show}Rail{Width,Collapsed}` (166-178) | `ResetRailSizes` 474-487 | `DetailHero.Bump` |
| 12 | Daylist clock 136-137 / picker 442-459 | `DaylistClock` (139) | `Prefs.Appearance.Set` 456 | Appearance epoch; no page Bump |
| 13 | Sidebar layout cards 547-588, 605-617 | `SidebarLayoutId` (`Platform.cs:83`) | `Sidebar.SwitchLayout` → `Shell\Sidebar.Host.cs:268` | `Sidebar.Layout`/`LayoutVersion` signals |
| 14 | Sidebar density 559-560 / picker 591-601 | `SidebarPaneDensity` (89) | `Sidebar.SetDensity` → `Sidebar.Host.cs:287` | `Sidebar.Density` signal |
| 15 | Sidebar reset / edit / reset everything 561-570; "what changed" reads `SidebarMigrationDropped` 574 | — | `Sidebar.Dispatch`/`EnterEdit` | sidebar signals |
| 16 | Lyrics blur slider 147-148, 716-744 | `LyricsBlurStrength` (158) | `Prefs.Lyrics.SetBlurStrength` | `Prefs.Lyrics.Epoch` |
| 17 | Stage visualizer 153-154, 779-787 | `StageVisualizer` (`Platform.Settings.cs:46`) | `Prefs.Stage.SetVisualizer` | `Prefs.Stage.Epoch` |
| 18 | Stage layout 155-156, 793-800 | `StageLayout` (63) | `Prefs.Stage.SetLayout` | Stage epoch |
| 19 | Sensitivity 157-158, 801-808 | `StageSensitivity` (48) | `Prefs.Stage.SetSensitivity` | Stage epoch |
| 20 | Lyrics overlay 159-160 | `StageLyricsOverlay` (50) | `StageToggle` 777 (`Toggle` + `Prefs.Stage.Bump`) | Stage epoch |
| 21 | Sync offset 161-162, 809-821 | `StageSyncOffsetMs` (52) | `Prefs.Stage.SetSyncOffsetMs` + `Playback.Audio.SetSpectrumOffsetMs` | Stage epoch |
| 22 | Calm motion 163-164 | `StageCalm` (54) | `StageToggle` | Stage epoch |
| 23 | Change with the music 165-166 | `StageMoments` (56) | `StageToggle` | Stage epoch |
| 24 | NPV presentation (developer-only) 172-177 | `NpvPresentation` (`Platform.cs:254`) | `Rail.PlayerPrefs.SetPresentation` (`Shell\Rail.cs:279`) | `NpvPlayer.Epoch` |
| 25 | Player style 178-179, 749-760 | `NpvPlayerStyle` (255) | `Rail.PlayerPrefs.SetStyle` (`Rail.cs:291`) | `NpvPlayer.Epoch` |
| 26 | Language `Settings.UI.cs:368-370` | `UiCulture` (114) | `SetLanguage` 396-402 | next launch |
| 27 | GPU `Settings.UI.cs:473-528` | `PreferredGpuLuid`/`Name` (326-327) | `Pick` | device reset |
| 28 | Show remaining `Settings.UI.Playback.cs:141-144` | `PlayerBarShowRemaining` (184) | `Toggle` + `Prefs.PlayerBar.Bump` | `PlayerBar.Epoch` |
| 29 | Developer mode / FPS overlay `Settings.UI.Privacy.cs:538-548` | `DeveloperMode`/`FpsOverlay` (335-336) | `Platform.Developer.Set`/`SetFpsOverlay` (`Platform.Settings.cs:139-140`) | `Developer.Enabled`/`FpsOverlay` signals |

**Not offered at all:** accent picker (accent follows the OS: `SetAccent` from `SystemAccentRamp` in `Settings.UI.Appearance.cs:207-208`, `Shell.Host.cs:685-686`, `Shell.UI.cs:701`), transparency or Mica, fonts or font size, high contrast, app-wide density. Reduced motion OS-only (`Design.Reduced`, `Platform\Design.cs:1554`; 98 hits).

### App-wide reader inventory (excludes bin/obj)
**ThemeMode — 5**: R `Settings.UI.Appearance.cs:97`; `Shell\Shell.Host.cs:676` (`SeedTheme`, called at 230); `Shell\Shell.UI.cs:702` (`OnSystemColorsChanged`, wired at 328). W `Settings.UI.Appearance.cs:210`; `Shell\Shell.Palette.cs:271`.
**Theme apply sites**: `SeedPalette`/`Tok.Use`: `Platform\Design.cs:203,206`; `Shell\Shell.UI.cs:61`; `Shell.Host.cs:113,683-684`; `Shell.UI.cs:705-706`; `Shell.Palette.cs:270`; `Settings.UI.Appearance.cs:204`. `Tok.SetAccent`: 5 sites. `ThemeControl.Request` consumers (4): `Settings.UI.cs:114`, `Shell.UI.cs:227`, `Shell.Palette.cs:413`, `Shell.Overlays.UI.cs:403`. Flip entry points: Settings row; caption button `Shell.UI.cs:1276-1279`; profile menu `Shell.Overlays.UI.cs:514-516`; palette `Shell.Palette.cs:94, 305`.
**Live theme branches — 74 code lines** (`Tok.Theme`, `ThemeKind.`, `Theme.Dark`): `Platform\Design.cs` (33): 223, 238, 273, 277, 287, 291, 302, 306, 318, 502, 590, 630, 732, 738, 758, 768, 775, 929, 931, 1009, 1061, 1065, 1071, 1103, 1107, 2352, 2363, 2366, 2379, 2447, 2581, 2588, 2605; `Platform\Controls.cs` (5): 507, 525, 529, 1015, 1032; `Entities\Detail.UI.cs` (5): 514, 587, 656, 684, 735; `Entities\Profile.Page.cs` (4): 102, 163, 206, 238; `Entities\Artist.Page.cs` (4): 144, 193, 238, 291; `Shell\Shell.UI.cs` (3): 703, 704, 1276; `Shell\Shell.Host.cs` (3): 679-681; `Settings.UI.Appearance.cs` (3): 200-202; `Shell\Shell.Palette.cs` (2): 269, 271; `Shell\Shell.Overlays.UI.cs` (2): 394, 482; `Entities\Concert.UI.cs` (2): 134, 251; one each: `Shell\Visualizer.Palette.cs:71`, `Shell\Shell.PlayerBar.UI.cs:643`, `Shell\Shell.Masthead.UI.cs:151`, `Shell\Rail.UI.cs:344`, `Shell\Deck.UI.cs:605`, `Entities\Track.UI.cs:760`, `Entities\Track.UI.Bound.cs:699`, `Entities\Track.Drawer.cs:411`. Plus `_theme` signal subscribers: `Artist.Page.cs:1023`, `Detail.UI.cs:1078`, `Profile.Page.cs:663`. Plus `Tok.Epoch`: `Shell\Stage.UI.cs:314`.
**Zoom**: `ZoomMode` (6): `Platform\Platform.cs:471`; `Settings.UI.Appearance.cs:259, 283, 289`; `Shell.UI.cs:305, 310`. `ZoomLevel` (5): `Platform.cs:469`; `Settings.UI.Appearance.cs:296`; `Shell.Host.cs:274`; `Shell.UI.cs:317, 318`. `ZoomModeBootstrapVersion` (2): `Platform.cs:468, 472`. `FluentApp.SetZoom` (5): `Settings.UI.Appearance.cs:295`; `StoreShot.cs:170, 228`; `Shell.Palette.cs:279`; `Shell.UI.cs:657`. `FluentApp.Zoom` (4): `Settings.UI.About.cs:293`; `StoreShot.cs:167, 215`; `Shell.Palette.cs:280`. `Viewport.Zoom` (4): `Settings.UI.Appearance.cs:255`; `Settings.UI.cs:115`; `Shell.cs:2023` (doc); `Shell.UI.cs:235`. `ZoomAutoPolicy`: `Settings.UI.Appearance.cs:258`; `Shell.cs:2033`; `Platform.cs:1146`.
**MarqueeEnabled**: key (2) `Prefs.cs:76`; `Settings.UI.Appearance.cs:117`. `Marquee()` (2): `Entities\Track.Table.cs:612`, `Shell\Shell.PlayerBar.UI.cs:222` → `Track.Table.cs:2222` → `Track.UI.Bound.cs:414-417`.
**ColorWashesEnabled**: key (2) `Prefs.cs:85`; `Appearance.cs:119`. `ColorWashes()` (5): `Artist.Page.cs:295`, `Detail.UI.cs:669`, `Profile.Page.cs:242`, `Recents.Page.cs:1162, 1205`.
**PageMotionStyle**: key (4) `Design.cs:1783` (doc), `Prefs.cs:122`, `Appearance.cs:231-232`. Derived (2): `Appearance.cs:104`; `Shell\Shell.cs:1008` → 1013, 1017, `Shell.UI.cs:920`.
**RowDensity**: key (6) `Track.Table.Chrome.cs:359`; `Track.Table.cs:903` (W); `Prefs.cs:94`; `Appearance.cs:324, 357-358`. `RowDensity()` (5): `Album.Pane.cs:192`; `Track.Table.Chrome.cs:804, 807, 821`; `Track.Table.cs:644`.
**HideTrackArtwork**: key (3). `TrackArtworkHidden()` (11): `Artist.Page.cs:297`; `Artist.UI.Chart.cs:177`; `Queue.UI.cs:469, 985`; `Recents.UI.cs:296, 316`; `Search.Page.cs:136`; `Track.Table.cs:612`; `User.Page.Library.cs:1397`; `Shell\Rail.UI.cs:732`; `Shell\Stage.Artist.UI.cs:561`.
**TrackRowStyle**: key (4). `TrackRowStyle()` (4): `Artist.Page.cs:296`; `Artist.UI.Chart.cs:176`; `Queue.UI.cs:468`; `Track.Table.cs:612`.
**Same Appearance epoch, not in Settings**: `LikedCoverStyle` (3): `User.Cover.cs:1118` (W), `Prefs.cs:112`, `User.Cover.cs:93, 141`. `TempoColumn` (3) / `PlaysColumn` (4): `Track.Table.Chrome.cs:346, 881-889`; `Track.Table.cs:585-592`.
**DetailHero**: `DetailPageLayout` key (5), `DetailRailUniform` key (3). Readers `Detail.UI.cs:670` (layout), 671 (uniform), 672 (epoch). Rail writers `Detail.UI.cs:473, 480`.
**DaylistClock**: key (4). `DaylistClock()` (3): `Entities\Playlist.UI.cs:990`; `Home\Daylist.UI.cs:400`; `Appearance.cs:446`.
**LyricsBlurStrength**: key (5) `Prefs.cs:220, 235`; `Appearance.cs:86, 98, 732`. Derived: `Shell\Lyrics.cs:1425` → `Lyrics.UI.cs:406, 417`, `Verse.UI.cs:240`.
**Stage keys (7 rows)**: key refs (20) `Prefs.Stage.cs:29-44` (14); `Playback\Playback.Audio.cs:565`; `Appearance.cs:160, 164, 166`; doc lines `Stage.Layouts.cs:29`, `Visualizer.cs:42`. `Prefs.Stage.X()` readers (16): `Appearance.cs:88-90, 774, 797, 805, 813`; `Shell\Deck.UI.cs:302`; `Shell\Stage.UI.cs:260, 262-267`. Other writers: `Stage.Options.UI.cs:255, 270, 318`; `Stage.UI.cs:212, 517, 522`; `Visualizer.Gallery.UI.cs:144-152, 232`; `StoreShot.cs:123`.
**NPV**: keys (10) `Prefs.Player.cs:58, 71, 84, 97-99`; `Shell\Rail.cs:279, 285, 291, 298`. `PlayerPrefs` users: `Appearance.cs:102-103, 176, 753, 758`; `Deck.UI.cs:70`; `Deck.Faces.cs:1198`; `Rail.Styles.UI.cs:52, 101, 134, 143, 192, 194`; `Rail.UI.cs:362, 364, 426, 427, 463`; `Rail.cs:250-268`; `Prefs.Player.cs:64` (reads developer mode).
**PlayerBarShowRemaining**: key (3) `Prefs.Player.cs:132, 138`; `Settings.UI.Playback.cs:143`. Readers `Shell.PlayerBar.UI.cs:1940, 1968`; epoch `Settings.UI.cs:136`.
**Sidebar**: keys (6) `Sidebar.Host.cs:78, 79, 268, 287`; `Sidebar.Migration.cs:279, 282`. `Sidebar.Layout` reads: 24 inside `Shell\Sidebar*.cs` + `Appearance.cs:551, 563, 609`; `Setup.UI.cs:753`; `Shell.Palette.cs:335`; `Shell.UI.cs:733, 837`. `Sidebar.Density` reads: 7 inside + `Appearance.cs:553, 596, 599`; `Shell.UI.cs:733`.
**UiCulture (3)**: `Platform.cs:555`; `Settings.UI.cs:122, 399`.
**Developer / presentation mode**: `DeveloperMode` key (8): `Platform.Settings.cs:134, 139`; `ReleaseNotes.UI.cs:1155`; `Settings.UI.cs:538`; `StoreShot.cs:51`; `Shell.History.UI.cs:65`; `Shell.Host.cs:768`; `Shell.UI.cs:983`. `Developer.Enabled` (5): `Prefs.Player.cs:64`; `Diagnostics.UI.cs:152`; `Appearance.cs:99`; `Privacy.cs:528`; `Rail.cs:259`. `FpsOverlay` (4): `Platform.Settings.cs:135, 140`; `Diagnostics.UI.cs:153`; `Privacy.cs:529`. `DeveloperSurfaces()` (2): `Diagnostics.UI.cs:849`; `Lyrics.UI.cs:2426`. `StoreShot.Presenting` (5): `Home\Daylist.UI.cs:302`; `Diagnostics.UI.cs:152`; `Rail.cs:259`; `Shell.Overlays.UI.cs:413`; `Shell.UI.cs:1391`.

## 5. Setup and login gating
**Bootstrap** (`Setup.cs:681-733`, via `Setup.Host.cs:28-32`): a fresh install has no `library.*`, no credential in `store.json`, no `history.json`, no legacy sidebar keys. Rules: arm `SetupPending` once (`TargetVersion` 1); reset a "completed" install whose data folder was wiped; re-arm when accepted terms are older than `TermsVersion` 1, except grandfathered installs.
**Opening the wizard** (`WizardRules.ShouldOpen`, `Setup.cs:227`): entry FirstRun, Reauth (completed + no stored credential) or TermsRearm (`EntryFor` 232); start page SignIn for Reauth, Terms otherwise (235); Layout page only on first run (`Gating.ShowsLayout` 635); SignIn skipped when a credential exists (620, `Setup.UI.cs:528`); Escape closes only an idle TermsRearm (`CanDismiss` 623); Decline on a first run quits (`Setup.UI.cs:621-626`); "Not now" on Local playback dismisses the runtime prompt and finishes (641-647); `MarkCompleted`/`MarkDeferred` `Setup.cs:602-617`.
**Unauthenticated**: `Shell.Auth` from `FoldAuth` (`Shell\Shell.cs:1566-1577`, applied `Shell.UI.cs:353`); `SignInRequired` without a stored credential opens the sign-in door (`Setup.UI.cs:83-93`, `SignInRules.OpensDoor` `Setup.cs:785`); the door stands down while the wizard owns sign-in (`OwnsSignIn` 243).
**Offline**: `AuthState.Offline` shows an offline strip and a Reconnect chip (`Shell.cs:1985-1997`), not the wizard; a sign-in network failure is the Failed facet (`Setup.cs:778`); `--fake` mounts no door (`Setup.UI.cs:60`).
**How the shell swaps.** There is no swap. The wizard is a Modal overlay plate over the live shell: `Setup.Covering = Dim` (`Setup.UI.cs:502`) painted by the shell's `CoverScrim` (`Shell\Shell.UI.cs:870-881`); `Runtime.WizardCovering` suppresses the runtime banner and toast (`Shell.Overlays.UI.cs:149, 213`); `WizardMarkerEpoch` re-gates the crash prompt (`Crash.UI.cs:61-65`) and the after-update plate (`ReleaseNotes.UI.cs:1203-1225`).

## 6. StoreShot.cs
`Screens\StoreShot.cs` holds `Diagnostics.StoreShot` (37-265): developer-only `wavee://diag` verbs for shooting the Microsoft Store listing.

| Verb | Line(s) | What it does |
|---|---|---|
| `present` | 73-76 | sets `Presenting` |
| `seek` | 77-82 | moves playback |
| `stage` | 109-129 | sets stage mode, face and gallery, then open/close |
| `rail` | 84-92 | sets `Shell.Ui.RailOpen`/`Mode` |
| `video` | 93-103 | sets the video surface placement |
| `reveal` | 134-158 | scrolls a viewport to a keyed node |
| `shot` | 163-250 | capture: `Presenting = true`, optional `FluentApp.SetZoom(prior × zoom / 100)` (170), settle, next-frame BGRA capture with alpha, `shot.png` + `shot.json` with keyed rects, then restores zoom and presenting (226-231) |

Presentation it forces: `Presenting` (41) hides developer surfaces through `DeveloperSurfaces()` (48-52): FPS overlay, lyrics debug and inspector, NPV presentation switch; hides account identity: profile chip name/picture (`Shell.Overlays.UI.cs:413`), daylist greeting (`Daylist.UI.cs:302`), unread badge (`Shell.UI.cs:1391`); can apply a temporary render zoom and drive the stage, rail and video surfaces. It never touches theme or accent.


# Part 7 — PR 205 feat/sidebar-rework (shell-cohesion)

---

# PR #205 `feat/sidebar-rework` (shell-cohesion): map for the theme refactor

Path prefixes used below:
- `W\` = `C:\WAVEE\sidebar\WaveeMusic\src\apps\Wavee\`
- `E\` = `C:\WAVEE\sidebar\fluent-gpu\src\`
- `R\` = `C:\WAVEE\sidebar\WaveeMusic\`

**State of the worktrees**
- App: branch `feat/sidebar-rework`, 95 commits ahead of origin/main and 0 behind. The working tree is clean; the tip is 59fd96c1 (2026-10-10 21:54).
- Diff: 178 files, +17032/−3200.
- Engine: branch `feat/sidebar-rework`, 7 commits ahead, clean. Diff: 21 files, +888/−66.
- No file under `R\docs\plans\wavee\` is touched by the branch. Code headers cite `R\docs\plans\wavee\sidebar-rework-implementation.md` (§P3, §P10, §P11, and the page-head package), which is already on main.
- The branch also touches `R\.claude\skills\wavee-sidebar\SKILL.md` (2 lines) and `R\CHANGELOG.md` (+106).

**What the CHANGELOG `[0.3.5] - unreleased` section adds for this branch** (`R\CHANGELOG.md:11-137`)
- Zune as a third navigation style (lines 15-20).
- Folders and Show covers (21-24).
- The three "Experimental" artist-bleed bullets (25-32).
- The Play menu (33-35) and Liked date groups (36-37).
- Changed:
  - Colour washes become three settings (55-56).
  - One page head (58-60) and no-jump heads (61-62).
  - The Home title (63) and Zune row 2 (64-65, 71-72, 88-89).
  - Title bar: the theme toggle moved into the profile menu; search is now a compact pill (69).
  - The Zune rail becomes an inline full-height panel (73-74).
  - One action row (75-76) and toolbar list tools (77-79).
  - The Liked page and rows (80-83).
  - The search pill (90-91) and the Liked bar (92-93).
  - The Play split (94), Classic/Library card depth (97-100), and Ctrl+, opening Settings.
- Fixed: lines 116-137.

---

## 1. Commits grouped by theme (oldest first)

### NAV: sidebar and navigation-style model (round "sidebar", P1-P12)
- 84795aff P1 NAV 1: row and head geometry on one grid.
- 68d566f4 P6 NAV 4 (part 1): Navigation style expander with Library rows.
- 059b234e P7 NAV 2 (model and rows): Classic "Show covers" with text-only entity rows.
- 6bdda475 P8 NAV 2 (menu and Settings): Show covers next to Density.
- 12811f45 P10 NAV 3 (core): Zune nav-style model, persistence, pane hiding.
- feee05ff P11 NAV 3 (UI): Zune pivot band, pins, Layout menu.
- 8305c851 P12 NAV 4 (part 2): Zune in Settings and the wizard, plus CHANGELOG.
- 504bcabb final-gate fixes.

### WASH: the colour-wash split
- d5fbf9ca P2 WASH 1+2: the switch becomes three persisted settings; Home is gated.
- 1d1af8eb P3 WASH 3: the surfaces gate on every plane, tint and veil, plus the Rich strength.
- 825365d6 P4 WASH 4: the accent-from-artwork gate.
- a3f1933f P5 WASH 6: three Settings rows with a live preview.
- 20e6bdab P9 WASH 5: the now-playing colours gate.

### PageHead and page geometry (shell round 1, P1-P9 and O1)
- e7ce9594 P1: PageGeometry, type roles, PageViewsStyle, the gutter signal.
- 7521799b P2: the PageHead builder, the head kinds, the deferred hoist, the page-views contract, the route-static Zune sub-row.
- 4bd4db1c P3: Settings, What's new, Diagnostics and Not found move to PageHead.
- 82bf0127 P4: Home (title "Home", reserved views band, published views and Following).
- eb696296 P5: Recents (PageHead, Overview action); **PivotTabs deleted**.
- 4e548012 P6: Browse (masthead at the gutter, BodyTop, chips, deferred hoist).
- 9c533a7f P7: Library master pane (PaneInset 16, PaneTitle, count meta).
- 9db1d1b9 P8a: the artist family (bottom reserve, Profile lists, Discography heads).
- 8cc95530 P8b: History, Logs, Customize, Concert months, Show reserve.
- c16ba661 P8c: Search head (echo as PageTitle, facet row as PageViews).
- e0644cbb P9: frame geometry and the Zune band (FrameGap, constant band height, derived inset, pivot roles).
- e3b91c9f O1: owner override — the content card bleeds to the window edge when no pane is docked.

### Zune row 2 and the A-series (shell round 1 continued)
- 68a6174c A1: row 2 is never empty (route-static sub-row, seeds, entity title row, plateless pivots, chrome-field audit).
- eb837017 A2: one baseline in context bands; artist, profile, episode and show bands move into row 2 with a floor latch.
- 48dcd230 A3: album, playlist and Liked bands into row 2 (later reversed by F4); Insights closed by default.
- 7add30d2 A4: rail header baseline; Library master pane sort dropdown; Podcasts views.
- 07917fb2 A5 EXPERIMENTAL: artist hero full bleed through the shell material channel.
- bb1f1cf8 P10: Zune title bar (pane toggle hidden, wordmark cross-fade, compact right-aligned search).
- 49ec32ac P11: folder hierarchy (folder mark, 32-DIP indent, tree guides).
- 85a1e67f P12: panes use one PaneInset.
- 40773686 P13: round-1 leftovers, CHANGELOG, stability sweep.
- 21edc8dd: row-2 views seeded from the first reveal frame.
- 1e40171d: library views items fit label plus pill.
- 0983fe46: bleed switched off temporarily (seam, notch).
- 7563bc07: final-gate fixes.

### Round 2 (F-series = Zune and bleed, D-series = detail bars)
- 9d3942f1 P01 F1a/b: bleed back on; one shared photo frame for card and shell.
- 0fc90409 P02 F1b: chrome ink cross-fades over the bleed, plus the engine `TitleBar.CaptionInk`.
- 0faad816 P03 F1c/d: corner notch, parallax sliver, pose lag under the Async host.
- 2659da8d P04 F2: hoisted heads take no space; bodies start at HeadTop 24.
- e93a2a17 P05 F3: Browse drops its own Top band while categories are hoisted.
- ea238434 P06 F4: row 2 never just repeats the title; title-only routes collapse row 2; the entity-title row is deleted.
- 011f7deb P07 F5: suggestions flyout matches the pill; the pill widens while focused.
- 7fa6c56e P08 F6: Zune right panel is a full-height inline column; the band belongs to the page column.
- 59139315 P09 F7: round-2 minors.
- e4bbf324 P10 D1: shared action pieces (Play split, secondary, quiet icon buttons).
- 6a50c8fc P11 D2a: album/playlist identity block, one action row.
- 345443d8 P12 D2b: track table command bar keeps only list tools.
- a20702ed P13 D3a: Liked Songs one layout, slim head, one bar.
- 99d7f157 P14 D3b: Liked rows (hover heart, plain rows, relative "Added", date groups).
- Final-gate fixes: 2a9b73cd, e7653367, f6813b15, 44457e76, da3a0402, 305c06bf, dfc67573.
- Merge of origin/main: 88b82506.

### Round 3 (shell P02-P24)
- c9c43183 P02: engine — looping tracks survive a structural snap (K1), plus the EqBar reseed belt.
- e1b82a26 P03: engine — a disclosure band clears when the source version advances (S1).
- 1471a759 P06: page swap, row 2 and the crumb head on one clock (A1/A2/A4).
- 8539d825 P07: search pill box and content move together; the pill never covers the pivots (C1/C3).
- 16419a60 P08: suggestions flyout reserves its height, grows animated, exits with a transition (C2/C4).
- 23d6dd92 P09: Liked chips look like Browse pills (D1/D3/D4/D6).
- e8fb4a57 P10: funnel folds into ⋯ as "Filter…"; sort joins the right cluster (D5/M1). Partly superseded by a6ba93fa.
- deef9366 P11: a chip registers the first click (D2).
- f33984ae P12: the Play split reads as one accent control (N1).
- b1828c37 P13: album rail gap 24; "Featured on" without the playing outline (B1/B4).
- b7076739 P14: album trailing skeleton mirrors the sections (B2/B3).
- 9a4c34e5 P15: bleed Zune-only, never on stacked tiers; stops at the floating panel (G1/D-3).
- f3cdec15 P16: light chrome ink over the bleed; lighter veil (H1a/H1b).
- c8e1f487 P17: Classic/Library card depth; no footer Settings row; Ctrl+, (I1/G3/G2).
- ba8583d1 P18: sidebar rows — count and ⋯ share one slot, Classic text rows 36, pin button (I2/I3/J1).
- 37557e55 P19: top-level Pin in the sidebar menu (J2/J3).
- d617337d P20: sidebar expanders and pin moves (S2d).
- d20fe9bf P21: Queue survives width pressure in the player bar (L2).
- b495b40c P22: rail artist line under width pressure (L1).
- e285b89e P23: runtime setup dialog footer fits; Settings head without the meta line (E1/E2).
- 3ef6adb5 P24: CHANGELOG and gates.

### Round 4b: artist bleed follows the theme
- f5172154 P1: the field takes the theme's polarity (one production veil, page-tone field, theme chrome ink, theme hero copy).
- 57627d99 P2: the hero tone wash never yields; it starts at the photo's feather.
- a6aa8d70 P3: no side fields; a per-theme scrim.
- 8002d28f P4: one entrance clock (FadeOnly reveal; HeroArt yields its fade).
- b66e7e26 WIP P5: shell photo split at the card top.
- 82d1d1d2 P5: one shared feather.
- 918de576 P6: album/playlist list bar shows the list facts (D-7).
- Merge into the branch: f728903a.

### Round 4c: paused
- d1af8eb2 P01: artist reveal waits for the hero bitmap under the bleed (250 ms cap; `W\Entities\Artist.Rules.cs:218` `HeroWaitCapMs = 250f`). A failed photo never bleeds.
- 59fd96c1 P02: **unfinished checkpoint** (details below).

### Table and filter (after 4b) and a cross-cutting fix
- 5a3154fe: one chip everywhere (`Controls.Chip` loses its capsule; `LinkChip` is the stock button); rail panel gap; Play/Shuffle gap 8; no duplicate genre pill.
- 7379bc1c: the in-list find stays open on the vertical arm.
- a6ba93fa: Filter is a primary command-bar action; the filter card is 360 DIP of combo rows.
- 7db10864: filter review fixes.

### The WIP tip (59fd96c1, "P02 unfinished checkpoint (round 4c paused)")
What was added:
- A stepped page entrance clock: `ShellEntranceClock` (`W\Platform\Design.cs:2330`), `ShellEntranceMode {Idle, Primed, Running}` (`:2323`), and `ShellBackdrop.Entrance` (`:2316`).
- The page owns one clock: `W\Entities\Artist.Page.cs:184-187` (`_clock`). It is primed in Render at `:497`, armed at `:310`, bound at `:313-314` (`_contentIn`, `_photoIn`), passed to the backdrop at `:511`, and its ticker is mounted at `:570` (`ArtistBleed.TicksEntrance`).
- The card field opacity binds to it: `W\Entities\Artist.UI.cs:516` (`ArtistBleed.FieldOpacity`).
- The shell's `BleedPresenter` follows the page clock instead of stepping its own: `W\Shell\Shell.UI.cs:1130`, `Follow` at `:1196`, `ClockFollower` at `:1207`.
- The pure clock rules: `W\Entities\Artist.Bleed.cs:253-283` (`MaxStepMs 40`, `ClockIdleMs`, `StepClock`, `ContentIn`, `PhotoIn`, `FieldOpacity`, `TicksEntrance`).

What is half-done, per the commit message:
- The investigation **recommends reverting P01 (d1af8eb2) and dropping this checkpoint**.
- Production's staged hero entrance (copy first, then the photo fades and zooms in) is deliberate.
- The real fixes are engine-side: start-pending tracks, FLIP capture after timers, and one hoisted hero photo under the bleed.

So the entrance-clock plumbing (ShellEntranceClock, the Follow path, Artist.Page clock arming) should be treated as **disposable**.

---

## 2. Presentation switches

### 2.1 ShellNavStyle (the main switch)

**Definition and persistence**
- Enum: `W\Shell\Sidebar.Layout.cs:21` — `public enum ShellNavStyle : byte { Classic = 0, Library = 1, Zune = 2 }` (append only).
- `SidebarLayoutId {Classic, Library}` is kept at `:16`. Zune keeps the pane layout underneath it.
- Pure mapping, `ShellNavStyleRules` (`:25-45`): `FromStored` `:27`, `Of(SidebarLayoutId)` `:29`, `LayoutOf` `:33`, `PaneLayoutFromStored` `:41`, `HidesPane` `:44`.
- Keys in `W\Platform\Platform.cs`:
  - `:83` `SidebarLayoutId = "sidebar.layout.id"` (now 0/1/2).
  - `:85` NEW `SidebarLastPane = "sidebar.layout.lastPane"` (the pane layout Zune returns to).
- Boot read: `W\Shell\Sidebar.Host.cs:78-79`.
- Live signal: `Sidebar.NavStyle` at `W\Shell\Sidebar.Host.cs:162`.
- Writer: `SwitchNavStyle` at `:281-296`. It persists both keys at `:292-293`, clears the library search, closes the overlay and clears the undo ring. `SwitchLayout` (`:277`) now forwards to it.

**Two-commit presentation**
- `Shell.Ui.PresentedNavStyle` (`W\Shell\Shell.cs:1078`) is written by `NavStylePresenter` (`W\Shell\Shell.UI.cs:1472-1484`) after `FrameRules.HoistSettleMs = CardMotionMs(300) + 32` (`W\Shell\Shell.cs:2015`), and seeded at boot (`W\Shell\Shell.UI.cs:304`).
- Commit 1 (live NavStyle) moves the frame: the pane, band height, gutter and card FLIP.
- Commit 2 (Presented) changes what is inside the card: head heights reflow and row-2 words appear.
- The rule is documented at `W\Shell\Shell.UI.cs:1461-1467` and `W\Platform\Page.UI.cs:20-26`.

**Every branch site on the live `Sidebar.NavStyle` (about 32 code lines, 7 files)**

`W\Shell\Shell.UI.cs` (21):
- `:300` boot `paneHidden`; `:304` seeds Presented; `:338` mode resolve hides the pane.
- `:509-510` chrome allocator (`zune`, `compactSearch`).
- `:699` sidebar seam width is 0 under Zune.
- `:904` card stroke (`CardStrokeShows`).
- `:963` ContentCardAnchor `BandHeight`; `:982` RailOverlayTopSpacer `BandHeight`.
- `:1073`/`:1081` CardPoseTracker toggle arm.
- `:1298` pane menu `zune:`; `:1403` PaneHidden snapshot; `:1416` scrim clip excludes the inline rail.
- `:1474`, `:1478` NavStylePresenter; `:1498` SubRowPresenter.
- `:1756` initial `ShowPaneToggle`; `:1779-1783` ChromeContentVersion (bit 128, hides the pane toggle); `:1800` TitleBarTabsVersion.
- `:1824` wordmark versus tab strip; `:2027` Zune search in the trailing island; `:2168` narrow drawer overlay disabled under Zune.

Other files:
- `W\Shell\Sidebar.Host.cs:283` (switch guard), `:494` EnterEdit (toast `sidebar.pane.zuneNoPane`), `:578` TogglePane (toast), `:592` OpenPane (silent).
- `W\Shell\Sidebar.UI.Zune.cs:107` PumpWhenZune; `:159-160`, `:174`, `:191` band rows and height.
- `W\Shell\Shell.Masthead.UI.cs:248` bleed `PhotoRight`; `:441` CenterIsland (no centred search under Zune).
- `W\Entities\Artist.Page.cs:473` bleed eligibility uses the live style.
- `W\Screens\Settings.UI.Appearance.cs:613-614`, with sub-branches at `:625` (Classic rows), `:631` (Library rows), `:641` (Zune row) and `:648` (pane-only rows).
- `W\Screens\Setup.UI.cs:753` wizard Layout page.

**`Shell.Ui.PresentedNavStyle` reads (12 reads, 2 writers)**
- `W\Platform\Page.UI.cs:115` `HoistedFor`; `:119` `ViewsInBandFor`.
- `W\Shell\Shell.Masthead.UI.cs:62` masthead hoist.
- `W\Shell\Sidebar.UI.Zune.cs:428` row-2 views opacity.
- `W\Entities\Detail.UI.Hero.cs:132` Liked SlimHeadHeight; `:229` SlimExpanded; `:663` slim skeleton.
- `W\Entities\Artist.Page.cs:143` (`_floorInRow2` seed) and `:898`.
- `W\Entities\Profile.Page.cs:95` and `:574`.
- `W\Entities\Episode.Page.cs:298`.
- Writers: `W\Shell\Shell.UI.cs:304` and `:1474`.

**Indirect callers**
- `PageHead.HoistedFor` (6): `W\Home\Facet.UI.cs:179`, `W\Home\HomeScreen.cs:209`, `W\Home\Zones.UI.cs:171`, `W\Entities\Browse.Page.cs:152`, `W\Entities\Recents.Page.cs:314`, `W\Entities\User.Page.Library.cs:1092`.
- `PageHead.ViewsInBandFor` (4): `W\Screens\Settings.UI.cs:197`, `W\Entities\Artist.Discography.cs:1761`, `W\Entities\Search.Page.cs:228`, `W\Entities\Profile.Lists.Page.cs:277`.
- `Detail.BandLayout.InRow2` (5 calls, 3 pages): the Artist, Profile and Episode lines listed above.

**Pure rules and parameters that take the style**
- `W\Shell\Sidebar.Zune.cs:184` `BandHeight(style, row)`.
- `W\Shell\Shell.cs:2049` `CardStrokeShows`; `:2068` `ShowsWordmark`.
- `W\Platform\Page.Rules.cs:177` `Hoisted`; `:185` `ViewsInBand`.
- `W\Entities\Detail.cs:1116` `InRow2`.
- `W\Entities\Artist.Bleed.cs:66` `Applies`; `:74` `PhotoRight`.
- `W\Shell\Sidebar.Resize.cs:58` `Resolve(..., paneHidden)`.
- `W\Shell\Shell.Chrome.cs:257-273` and `:451` (`zune` drops Forward).
- `W\Shell\Sidebar.Edit.cs:201-222` (`zune` and `zunePins` change the pane menu).
- `W\Shell\Shell.Palette.cs:40` (new verb `SidebarZune`), `:104`, `:339-341`.
- `W\Shell\Sidebar.UI.Menus.cs:59-60` (`NavStyleName`) and `:271-276` (`NavStyleOf`).
- Tests: `R\src\apps\Wavee.Tests\ShellNavStyleTests.cs` (+425).

### 2.2 ZuneSubRow: the contents of Zune row 2 (route-derived, not persisted)
- Enum: `W\Shell\Sidebar.Zune.cs:35-52`. Library = sub-pivots; Views = the page's views; Categories = Browse top categories; Context = entity title, tabs and actions; None = collapsed.
- `SubRowOf(route)` at `:129-153`:
  - Library pages → Library.
  - Home, Recents, Settings, Search, ProfileList, Discography → Views.
  - The Browse root and its 4 top categories → Categories.
  - Artist, User, Episode, Show → Context.
  - Everything else (album, playlist, concerts, history, …) → None.
- Presented signals: `Shell.Ui.PresentedSubRow` (`W\Shell\Shell.cs:1084`) and `Shell.Ui.PresentedRoute` (`:1090`). They are written in one batch by `SubRowPresenter` (`W\Shell\Shell.UI.cs:1491-1526`) when the page swap lands (`ZuneNavRules.PresentsNow`, `W\Shell\Sidebar.Zune.cs:164`), and seeded at boot (`W\Shell\Shell.UI.cs:306-311`).
- Readers of the row kind:
  - Band height (`W\Shell\Sidebar.UI.Zune.cs:191`, `W\Shell\Shell.UI.cs:963`, `:982`).
  - Row2HostView (`W\Shell\Sidebar.UI.Zune.cs:300`, `:309-330`).
  - CardPoseTracker (`W\Shell\Shell.UI.cs:1074`).
  - `PageHeadRules.ViewsInBand` (`W\Platform\Page.Rules.cs:187`).

### 2.3 Zune pins
- Key `sidebar.zune.pins` (bool, default true): `W\Platform\Platform.cs:94`.
- Signal `Sidebar.ZunePins` (`W\Shell\Sidebar.Host.cs:168`); boot at `:80`; setter `SetZunePins` at `:298-302`.
- Sites (4):
  - Visibility gate `ZuneNavRules.ShowsPins(setting, pageColumnW ≥ 720)` at `W\Shell\Sidebar.Zune.cs:279`, used at `W\Shell\Sidebar.UI.Zune.cs:147`.
  - Settings row `W\Screens\Settings.UI.Appearance.cs:643`.
  - Menu row `W\Shell\Sidebar.Edit.cs:222`.
  - Menu action `W\Shell\Sidebar.UI.Menus.cs:297`.

### 2.4 Classic "Show covers"
- Key `sidebar.classic.covers` (bool, default false): `W\Platform\Platform.cs:96`.
- Signal `Sidebar.ClassicCovers` (`W\Shell\Sidebar.Host.cs:175`); boot at `:82`; setter `SetClassicCovers` at `:323-329`; reset to false on reset-everything at `:348`.
- Effect: `SidebarSection.ShapeFor(layout, kind, density, classicCovers)` (`W\Shell\Sidebar.Layout.cs:636-641`). In Classic, covers off gives the new `SidebarRowShape.Text` and covers on gives `EntityOneLine`. Library still follows density.
- The new shape is defined in `W\Shell\Sidebar.cs`:
  - `Text = 3` at about `:333-336`.
  - Geometry: `TextRowHeight = 36` (`:367`), `TextLabelX`, `TextGlyphGap 8`, `TextGlyphColumn`, `TextGlyphLabelX` (`:371-379`).
  - `HeightOf` at `:444-449`.
  - `IndentStep` changed from 31 to 32 (`:386`); `TreeGuideX` at `:480`.
  - `HeaderButton` changed from 24 to 28; new `HeaderGlyph`/`PlusGlyph` 16, `RowButton` 24, `OverflowBox` 26, `CountSlotWidth`, `PinWidth`.
  - `PaneTopInset` and `SeparatorLineTop` changed from 3 to 4.
- Sites (6): the document resolve in `W\Shell\Sidebar.Host.cs:207-214`; menu row `W\Shell\Sidebar.Edit.cs:248`; menu action `W\Shell\Sidebar.UI.Menus.cs:296`; Settings `W\Screens\Settings.UI.Appearance.cs:618` and `:627`; row rendering `W\Shell\Sidebar.UI.Rows.cs:188` and `W\Shell\Sidebar.UI.Slot.cs:255` (Text rows have no icon column).

### 2.5 Density
- Existing key `sidebar.pane.density`; enum `SidebarDensity` (`W\Shell\Sidebar.Layout.cs:56`). It now applies only to Library entity rows.
- Classic shows Show covers instead in the pane menu (`W\Shell\Sidebar.Edit.cs:247-253`) and in Settings (`W\Screens\Settings.UI.Appearance.cs:625-639`).
- The Library rows in Settings are View (`SidebarViewPicker`, `:680`), Density (`:635`) and Show Liked Songs (`:637`).
- Separate from this: the track table's density button was removed. Row size now lives under ⋯ (`W\Entities\Track.Table.Chrome.cs:2-8`).

### 2.6 Colour wash, now three settings
- Keys in `W\Platform\Platform.cs`:
  - `:153` `WashSurfaces = "appearance.wash.surfaces"` (int: -1 unset, 0 Off, 1 Subtle, 2 Rich).
  - `:155` `WashAccent = "appearance.wash.accent"` (-1/0/1).
  - `:157` `WashNowPlaying = "appearance.wash.nowPlaying"` (-1/0/1).
  - The old `ColorWashesEnabled` (`:152`) is now a read-only fallback and is never written.
- Enum `WashLevel {Off, Subtle, Rich}`: `W\Platform\Prefs.cs:352-357`.
- Migration at read time, `ColorWashRules`: `W\Platform\Prefs.cs:365-386` (`Unset = -1`, `Level`, `Flag`).
- Reactive reads (they bump the Appearance epoch): `SurfaceWash()` `W\Platform\Prefs.cs:82`, `AccentFromArtwork()` `:90`, `NowPlayingColors()` `:99`.

**SurfaceWash consumers (13)**
- `W\Home\HomeScreen.cs:548`, `W\Home\Daylist.UI.cs:157`.
- `W\Platform\Design.cs:2524` (CoverPageTonePlane rich) and `:2771` (CoverShellTintBinder rich).
- `W\Entities\Detail.UI.cs:683`.
- `W\Shell\Shell.Masthead.UI.cs:189` (Home washes rich alphas).
- `W\Entities\Recents.Page.cs:1152` and `:1196`.
- `W\Entities\Artist.UI.cs:507`.
- `W\Entities\Artist.Page.cs:393`, `:474` and `:488` (bleed gate).
- `W\Entities\Profile.Page.cs:271`.
- Settings reads it at `W\Screens\Settings.UI.Appearance.cs:320`.

**AccentFromArtwork consumers (4)**
- `W\Entities\Detail.UI.cs:1245` and `:1267` (`PageAccentOf` returns the system pair when off).
- `W\Entities\Recents.Page.cs:1153`.
- `W\Entities\Artist.Page.cs:168`.
- Settings `:122`.

**NowPlayingColors consumers (5)**
- `W\Platform\Design.cs:682` (`ArtStandIn`; the overload with a flag is at `:686`).
- `W\Shell\Deck.UI.cs:603`, `W\Shell\Rail.UI.cs:351`, `W\Shell\Stage.UI.cs:482`, `W\Entities\Queue.UI.cs:451`.
- Settings `:124`.

**Rich strength constants, `Design.Wash`** (`W\Platform\Design.cs:2204-2237`)
- `HeroAlphaLightRich 0.08`, `ShelfAlphaLightRich 0.075`, `HeroAlphaDarkRich 0.15`, `ShelfAlphaDarkRich 0.13`.
- `PlaneAlphaLightRich 0.45`, `PlaneAlphaDarkRich 0.30`.
- `TintAlphaLightRich 0.10`, `TintAlphaDarkRich 0.22`.
- `HeroAlpha(light, rich)` `:2209`; `PlaneAlpha(light, rich)` `:2218`; `PlaneAlpha(light, rich, paneDocked)` `:2226` (a light theme beside a docked pane is capped at the Subtle value); `TintAlpha` `:2234`.

**Other wash changes**
- `CoverKeyedVeil.Props` gets a `Disabled` field (surfaces Off gives a neutral veil): `W\Platform\Design.cs` around `:2697-2741`.
- The tone plane uses `Shell.s_contentCorners` (`W\Shell\Shell.UI.cs:781`).

### 2.7 Page head kind and gutter (route-static)
- `PageHeadKind` (`W\Platform\Page.Rules.cs:153-165`) and its extents (`:212-221`):
  - Title 120, TitleViews 164, CrumbTitle 156, CrumbTitleViews 200, Hoisted 24.
  - Without a meta line each is 20 shorter (Settings uses `HasMeta = false`).
- Gutter: `Shell.Ui.PageGutter` (`W\Shell\Shell.cs:1072`) is driven by `PageGeometry.GutterFor(width, previous)` (`W\Platform\Page.Rules.cs:62-74`): 36 at ≥ 880, 32 at ≥ 600, otherwise 16, with ±24 hysteresis that mirrors `ArtistHeroLayout.TierFor` (`W\Entities\Artist.UI.cs:66-116`). It is written in `W\Shell\Shell.UI.cs:313-315` (boot) and `:356-360`.

### 2.8 Artist bleed (code switch)
- `ArtistBleed.Enabled = true` (`W\Entities\Artist.Bleed.cs:31`), read once in `W\Entities\Artist.Page.cs:474`/`:488`.
- Gate: `Applies(enabled, surfaces, headerUrl, zune, stacked)` (`:66-67`) — Zune, not stacked, surfaces not Off, and a header URL present.

### 2.9 Chrome ink over the bleed (runtime; currently idle)
- `Shell.Ui.ChromeInkMix()` (`W\Shell\Shell.cs:1143`) uses `ArtistBleed.ChromeInkMix` (`W\Entities\Artist.Bleed.cs:300-301`). That returns 0 whenever `fieldDark == themeDark`, and since round 4b `FieldDark(theme)` (`:36`) always equals the theme, so **the mix is always 0**.
- The binds stay wired:
  - `ChromeInkPrimary`, `Secondary`, `Tertiary`, `Accent` (`W\Shell\Shell.cs:1153-1161`).
  - `ChromePrimary`, `Secondary`, `Tertiary` Props (`:1181-1185`).
  - `ChromeOnMedia` (`:1167`) and `ChromeArmsOnMedia` (`:1174`, set at `W\Shell\Shell.Masthead.UI.cs:171`).
  - `Design.OnMedia.AccentInk*` (`W\Platform\Design.cs:425-429`).
- Consumers:
  - Title bar `CaptionInk` (`W\Shell\Shell.UI.cs:1759`); `ChromeButtonStyle` (`:1698-1707`); `ChromeGlyphParts` (`:1713-1720`); `ChromeTabParts` (`:1847`); wordmark (`:1828`).
  - Zune words: `WordInk`, `HoverInk`, `PressedInk` (`W\Shell\Sidebar.UI.Zune.cs:669-676`); the "pinned" caption (`:238`); pin glyphs (`:722-724`); the row-2 divider (`:603-604`).
  - Search pill: `PillInkMix`, `PillInkPrimary`/`Secondary`, `s_pillPlate`, `s_pillHairline` (`W\Shell\Shell.Masthead.UI.cs:461-472`); hint chip border (`:804`).
  - `Controls.TextActionInk` (`W\Platform\Controls.Cta.cs:438`) and the `IconAction(onMedia)` overload (`:343`).
  - `ButtonRules.FollowStyle(onMedia)` (`W\Platform\ButtonRules.cs:108`).

### 2.10 Smaller switches
- **Liked page config:** `Detail.Config.SlimHead`, `HoverHeart`, `PlainRows` (`W\Entities\Detail.cs:1670-1672`); set in `Config.Liked` (`:1694-1699`).
  - `SlimHeadHeight(zune)` (`:630`).
  - `LikedGroups` (`W\Entities\Track.Rules.cs:694`; applies only when HoverHeart and sorted by DateAdded, `:703`).
  - `RelativeAddedLabel` (`:649`).
- **`PlateKind.TileStill`:** `W\Platform\Surface.Rules.cs:38`; `Shape.RowTileStill` (about `:107`); `PlateRules` (about `:193`).
- **`Track.InlineCommand`:** reshaped to `{None, Sort=2, Filter=4, FilterLabel=8}` (`W\Entities\Track.Rules.cs:837`).
  - `CommandWidths(Tune, Sort, Filter)` (`:842`), `CommandBarFit` (`:844`), `CommandBarLayout.FilterLabelledNominal = 92` (`:861`/`:873`).
  - `LikedBarFit` (`:954`), `LikedBarLayout` (`:964`).
- **Page accent:** `Design.PageAccent` (`W\Platform\Design.cs:2142`) and the new `Design.AccentRoles.Ink/Fill` (`:2168`). One page has one accent; with Accent from artwork off it falls back to the system pair.

---

## 3. Shell surfaces added or changed, and what pages must publish

### 3.1 The frame (`W\Shell\Shell.UI.cs`)
- **Page column** (`:642-671`): [Zune band, card stack]. The band is the column's first child, so under Zune the inline rail runs from the title bar to the dock.
  - Card stack: `CardGround` + content card + `CardStroke`, animated by `PageColumnCardAnim` (`:187-189`, a Y-only Position FLIP plus a Height Relayout).
  - Content card: `ContentCardAnim` (`:164-166`), relative to `ContentCardAnchor` (`:953-968`). Its corners use `s_bleedCorners`.
- **ZuneBandAnim** (`:197-199`): a Size Relayout on the card tween (300 ms, `CubicBezier(0, .35, .15, 1)`; `W\Shell\Shell.cs:2005-2009`).
- **Frame rules** (`W\Shell\Shell.cs:1986-2076`):
  - `FrameGap 8`, `RailGapW`, `StrokeOverhangTop 0`, `CardMotionMs 300`, `HoistSettleMs 332`, `ChromeEdgeSnapMs 100`.
  - `ContentCardX(sidebarW) = sidebarW`, so under Zune the card is at x = 0 and flush to the window.
  - `PaneDocked`, `ContentCorners(docked)` (square when undocked), `CardStrokeShows`, `StrokeLeftShift`, `ZuneBandInset(gutter) = gutter`, `RailOverlayTop`, `ShowsWordmark`.
- **Card ground and stroke** (`:803-945`): with a bleed backdrop, the ground splits into strip, fade and riser over the photo, and the stroke is clipped. Without one it is the plain FileArea fill and the StrokeCardDefault ring (transparent under Zune).
- `PageColumnWidth` (`:141-144`) is used by the gutter and pin visibility.
- Scrim clip under Zune excludes the inline rail (`:1406-1422`).
- Narrow drawer is disabled under Zune (`:2168`).

### 3.2 The Zune band (`W\Shell\Sidebar.UI.Zune.cs`; rules in `W\Shell\Sidebar.Zune.cs`)
- Header contract: `:1-79`. Mounted at `ZuneBand()` `:100`; `ZuneBandView` `:111-194`.
- **Height:** 84 with row 2, 52 without, 0 outside Zune.
  - Rhythm: `PivotTop 4`, `PivotLine 36`, `PivotToSub 4`, `SubRowHeight 32`, `SubToCard 8` (`W\Shell\Sidebar.Zune.cs:68-87`).
  - Left inset is the page gutter, card-relative; right padding is `Spacing.L`.
- **Row 1** (`TopRow`, `:200-269`): the top pivots home · browse · library · recents (`Top`, `W\Shell\Sidebar.Zune.cs:95`), lower-cased.
  - Text roles: `Design.Type.ZunePivot` 28/36, 600 when selected and 350 otherwise (`W\Platform\Design.cs:1429`).
  - The pivots are plateless; ink carries the state (secondary at rest, primary on hover or when selected, tertiary on press). They have Role Tab and sit in a horizontal scroller with AutoEdgeFade.
  - Library lands on Liked (`LandingOf`, `:113`).
  - **Pins:** up to 6 (`MaxPins`, `:58`); folders are skipped. Layout is the caption "pinned" (MicroMeta, tertiary chrome ink), then 32-DIP tiles (`PinTile`, gap 8) showing a cover or kind glyph, a tooltip, and a 4-DIP accent dot under the tile that is lit while playing (`PinDotView`, `:736-750`).
  - Pins show only when the page column is ≥ 720. They mount after the band settles (`CardMotionMs + 40`) and fade out of flow before unmounting (`PinsMounted` and `PinsTakeRowWidth`, `W\Shell\Sidebar.Zune.cs:285-292`).
- **Row 2** (`Row2HostView`, `:287-335`): always mounted, 32 tall. It latches the last non-None content and fades it to 0 while the band closes; the latch clears after `CardMotionMs + 120`. Content by kind:
  - `LibraryRow` (`:359-373`): liked · albums · artists · podcasts · audiobooks with `ZuneSubPivot` 14/20, 600 when selected and 400 otherwise (`W\Platform\Design.cs:1437`).
  - `CategoriesRow` (`:380-400`): music · podcasts · audiobooks · live events.
  - `ViewsRow`/`ViewsContent` (`:411-485`): the page's published views, or until then the route's seed (`ViewSeedKeys`/`SeedSelected`, `W\Shell\Sidebar.Zune.cs:192-259`). Search seeds 5 skeleton words. There is an optional trailing control (Home's Following, the Recents summary). Words stay at opacity 0 until Presented is Zune.
  - `ContextRow` (`:493-598`): title word (ZuneSubPivot, selected, capped at `BandLayout.TitleCap 280`, optionally a scroll-to-top button), a divider, scroll-spy tabs (`SpyTab`: constant 400 weight with a 2-DIP accent underline at `BandLayout.UnderlineY`), and the page's action cluster. Until the page publishes, skeleton slots show (`ContextSeed`: artist 3/2, profile 2/1, episode 4/0, show 0/0). Built from `Detail.BandCluster` (`W\Entities\Detail.UI.Hero.cs:829`).
  - Transitions are 83 ms fades (`s_viewsFade`, `PageHead.FadeIn`/`FadeOut`/`FadeMotion`, `W\Platform\Page.UI.cs:100-107`).
- **Context menu** (`ZuneMenu`, `:762-767`): Layout ▸ (Classic / Library / Zune radios, Reset layout disabled), "Pins in the title bar", Reset everything (`W\Shell\Sidebar.Edit.cs:218-226`).
- Under Zune the band pumps the projection binder itself (`PumpWhenZune`, `:105-109`).
- It publishes `Shell.Ui.ZunePivotsRightProbe` (`W\Shell\Shell.cs:1100`; set at `:166-171`) for the search pill cap.

### 3.3 Title bar (`W\Shell\Shell.UI.cs:1686-2040`)
- `ChromeRow` (`:1751-1768`): `ShowPaneToggle` is false under Zune (the toggle is absent, not disabled; pushed through `ChromeContentVersion`, `:1770-1793`); `CaptionInk = Ui.ChromeInkPrimary`.
- **Wordmark** (`:1824-1835`): under Zune with one tab, `Design.Type.Wordmark("wavee")` (20/28 display face, `W\Platform\Design.cs:1445`) replaces the tab strip. The two cross-fade inside the keyed lane `chrome-tabs-lane` (`TabLaneMotion`).
- **Theme toggle removed** from `CaptionLeadingIsland` (`:2010-2019`); it now lives in the profile menu's Theme row. Only the icon-mode search button remains there.
- **Search placement:** Zune puts a compact search in the trailing island before the identity chip (`:2027`; `ZuneSearchHost`, `W\Shell\Shell.Masthead.UI.cs:452-455`). Classic and Library keep the centred field (`CenterIsland`, `:440-443`). Under Zune the allocator drops Forward (`W\Shell\Shell.Chrome.cs:451`) and uses the compact search.
- Ctrl+, opens Settings (`SettingsChord`, `W\Shell\Shell.UI.cs:227-229`).

### 3.4 Search pill and suggestions flyout (`W\Shell\Shell.Masthead.UI.cs`)
- `SearchField` (`:499-626`).
  - It sits in a width-less slot (`chrome-search-slot`), so the island follows the pill as it eases.
  - At rest the width is `Chrome.FieldWidthFor`. Expanded (focused or suggestions open; `FrameRules.SearchExpands`, `W\Shell\Shell.cs:2149`) it is `Chrome.ExpandedFieldWidth(SearchExpandW)`, capped so it never covers the pivots (`Chrome.CapExpandForPivots`, `W\Shell\Shell.Chrome.cs:363-373`).
  - Width motion is a Reflow over `Design.Motion.Standard` (`s_searchWidthMotion`, `:476-477`). A settle counter (`s_searchSettle`, `:486-497`) re-pushes the title-bar regions once the width settles.
- The pill is a fully rounded `AutoSuggestBox` (corner = height / 2) with the "Search" placeholder and a shortcut hint chip (`SearchHintChip`, `:809`; `Chrome.ShowSearchHint`, `W\Shell\Shell.Chrome.cs:393`).
  - Plate and hairline bind to `s_pillPlate`/`s_pillHairline`; the typed text and placeholder use the engine's `textInk`/`placeholderInk` (`:790-795`).
- **Flyout:** `AutoSuggestBoxPresenter(... Chrome: PopupChrome.Dropdown)` (`:771-778`) gives an animated exit. It is exactly the pill's width and left edge.
  - A pending body reserves the previous answer's height (`Omnibar.PendingSkeletonRows`/`PendingBodyHeight`/`PopupBodyHeight`, `W\Shell\Shell.cs:2533-2568`; `PopupBodyMaxHeight 560`, row heights 44 and 62, divider 9, notice 40).
  - Height motion is a Reflow (`s_bodyHeightMotion`, `:963-965`); the pending body is at `:971`.

### 3.5 Masthead crumb head (`W\Shell\Shell.Masthead.UI.cs:38-145`)
- It changes with the page swap: until `Shown` lands it holds the old trail and fades (`ZuneNavRules.HeadLanded`, `W\Shell\Shell.Sidebar.Zune.cs:169`).
- It hides when the route is hoisted (`:62`).
- Padding is the gutter plus `PageGeometry.HeadTop` (`:90`). The current title uses `Design.Type.PageTitle`, one line (`:128`).

### 3.6 Material layer and artist bleed (`W\Shell\Shell.Masthead.UI.cs:147-400`)
- Always mounted: the tint box (`Design.Wash.NeutralGround` or the page tint), plus Home's three washes with Rich alphas, plus the bleed nodes.
- `BleedNodes` (`:237-361`) draws three layers:
  - **Under-card part:** clipped at `ArtistBleed.PhotoRight`, opacity `BleedUnderlay × HeroVisible`.
  - **Chrome part:** from the window top to the card's top, opacity `BleedPresence × HeroVisible`.
  - **Scrim:** a linear gradient over `ArtistBleed.FieldBase(theme, scheme, payloadAccent)` with stops `ScrimStopsLight` 0.75/0.55/0.2/0 or `ScrimStopsDark` 0.55/0.38/0.12/0 (`W\Entities\Artist.Bleed.cs:42-58`).
- Both photo parts share one feathered frame (`FrameFor`, `FeatherBoxHeight`) and carry `Palette.ArtistHeroVeil` with a top fade down to the card's top.
- They follow the card's presented pose (`Ui.CardPose`, `CardPoseTracker`, `W\Shell\Shell.UI.cs:1032+`, analytic `ArtistBleed.PoseAt`).
- Signals: `CardRect`, `CardPose`, `CardSettle`, `BleedPresence`, `BleedUnderlay`, `BleedHandover`, `BleedBackdrop` (`W\Shell\Shell.cs:1095-1135`). `BleedPresenter` is at `W\Shell\Shell.UI.cs:1130`.
- **Data contract:**
  - `ShellMaterialState(Owner, Tint, Wash, Backdrop)` (`W\Platform\Design.cs:2299`).
  - `ShellBackdrop(Url, PhotoHeight, HeroHeight, Floor, ScrollY, CollapseDistance, DecodeW, DecodeH, Key, PaletteUrl, PayloadAccent, EntranceAtMs, Entrance)` (`:2316`).
  - Published with `ShellMaterial.Publish(..., backdrop)` (`:2389`), carried through `CoverShellTintBinder.Props.Backdrop`.
- **Timing:** the entrance is 1 + 2 legs (`EntranceMs = Expressive.Fast`, `LegMs = Motion.Standard`, `SettleMs`; `W\Entities\Artist.Bleed.cs:172-184`). The card's top-left corner fades (`CornerFor`, `:121`).

### 3.7 Detail-page bars (`W\Platform\Controls.Cta.cs`)
- The 32/r4 grammar table: `:30-41`.
- **`PlaySplitButton`** (`:612`; `PlaySplitHost` `:619`; `PlaySplitLabelHost` `:754`): one engine SplitButton in the accent palette.
  - Size 88 + 1 + 32 = 121 (`W\Platform\ButtonRules.cs:40-43`). The menu is always Add to queue · Play next · Start radio (`PlaySplitVerb`/`PlaySplitItems`, `:46-51`).
  - The primary face flips between Play and Pause. Each half swaps its own fill (rest/hover/pressed); a 1-DIP divider column holds a 20-tall hairline at 20% ink. While open, the root border turns transparent and the halves show the pressed fill.
- **`SecondaryButton`** (Shuffle; `:176`): a bordered Standard button, labelled or icon-only. **`QuietIconButton`** (share; `:206`).
- **Sort dropdown:** `Controls.SortButton`/`LabeledButton`/`SortCaret` (`W\Platform\Controls.cs:860-960`). There is exactly one chevron: the direction caret replaces the dropdown chevron. The active state is an accent fill at 0.11/0.17/0.08.
- **Chips:** `Controls.Chip(…, parts)` is now the stock toggle (32/r4; checked = accent; the capsule is gone). `LinkChip` is the stock button. `ChipRail(…, inBar)` takes a 24-DIP edge-fade band (`W\Platform\Controls.Art.cs:569-614`).
- **Track table command bar** (`W\Entities\Track.Table.Chrome.cs:1-21`): Tune ─ [album/playlist list facts "12 songs · 48 min" / "3 of 12 songs" (`ShowsListFacts` `:242`, `ListFactsText` `:247`, `Detail.Text.ListFacts` `W\Entities\Detail.cs:1811`)] ─ Filter · Sort · find · Insights · ⋯ (Select, Row size ▸, Columns ▸). Filter is labelled with an accent tint and an 18-DIP badge, and opens a 360-wide card of combo rows (`FilterPopup` `:1150`).
- **Liked bar** (`BuildLikedBar`, `:287`): Play split · Shuffle · divider · genre chips ─ Filter · Sort · find · Insights · ⋯, using the `LikedBarLayout` rungs. It holds its rung while the pointer is over the bar.

### 3.8 Liked Songs
- Single layout with a slim head: title and meta, or nothing under Zune — `SlimHeadHeight(zune)` = HeadTop + (zune ? 0 : TitleLine + TitleToMeta) (`W\Entities\Detail.cs:630-631`; `W\Entities\Detail.UI.Hero.cs:427-440`, `:660-702`).
- One bar. Rows: hover heart and ⋯, plain rows, regular-weight titles, relative "Added" (`RelativeAddedLabel`), and date groups when sorted by date added (`LikedGroups`: ThisWeek, LastWeek, months; `HeaderH 40`).
- Page: `W\Entities\User.Page.Liked.cs:60` (`LikedPage`); chip bar `:340`.

### 3.9 Settings › Appearance rows added (`W\Screens\Settings.UI.Appearance.cs`)
- `:119-124`: three rows replace "Color washes":
  - Tinted surfaces: `WashSurfacesControl` `:318` — a SelectorBar Off / Subtle / Rich plus `WashPreview` (`:344`, a 64×36 swatch: chrome strip on page at alpha × 3, capped at 0.6).
  - Accent from artwork: `WashToggle` `:311`.
  - Now playing colours: `WashToggle`.
- The Sidebar expander (`:607-678`) is now titled "Navigation style". Its value tag is `NavStyleName` and its header is three cards (`SidebarLayoutCards(ShellNavStyle)` `:707` over `PickerStrip(3)`; face `:721`; `SidebarPreview` `:763`, which has a Zune mini-preview at `:766-780`).
- Per-style rows:
  - Classic: Show covers (`:627`).
  - Library: View (`:633`), Density (`:635`), Show Liked Songs (`:637`).
  - Zune: Pins in the title bar (`:643`).
  - Not Zune: Reset this layout and Edit sidebar (`:648-655`).
  - Always: Reset everything (`:656`).
- **Settings page head** (`W\Screens\Settings.UI.cs:177-217`): `PageHead.Create` (title, the seven tab views, `HasMeta = false`, `ViewsInBand`). It publishes its views (`PublishViews` `:217`; `UseActivation` plus an active effect `:142-149`). The tab keys come from `ZuneNavRules.SettingsTabKeys`. `TabIndex` is at `:65`.

### 3.10 Setup wizard
- The Layout page shows three navigation-style cards (`W\Screens\Setup.UI.cs:747-757`).
- The runtime dialog footer wraps its buttons onto a second right-aligned line (`W\Screens\Setup.UI.Runtime.cs:885-899`; `RuntimeRules.FooterWraps`, `W\Screens\Setup.cs:421-427`).

### 3.11 What pages must publish (the PageHead contract)
- **Head:** `PageHead.Create(PageHeadSpec)` (`W\Platform\Page.UI.cs:202-245`; spec `:40-88`). Height is `PageHeadRules.Extent(kind, HasMeta)`; padding is the gutter. Rows fade in place.
  - Spec fields: Title (one line, `Design.Type.PageTitle` 40/52, `W\Platform\Design.cs:1340`), Meta (`PageMeta` 12/16, `:1351`, always reserved), HasMeta, Above (route-static breadcrumb), Actions, Views (pass an empty list if labels come later), ViewsSelected, OnView, ViewsTrailing, ViewsPlaceholder, ViewsScroll, Hoisted, ViewsInBand, Gutter, Key.
- **Geometry constants** (`W\Platform\Page.Rules.cs:78-148`): HeadTop 24, AboveLine 32, AboveToTitle 4, TitleLine 52, TitleToMeta 4, MetaLine 16, HeadToViews 12, ViewsToBody 16, HeadToBody 24; ViewsBarPadY 4, ViewsItemH 40, ViewsBarH 48, ViewsLeadingInset −12; BottomReserve = Dock.Reserve + 32; PaneInset 16.
- **Views style:** `Design.PageViewsStyle` (14/20 stock pill, leading −12; `W\Platform\Design.cs:1499`), `RailViewsStyle` (`:1510`), `PaneViewsStyle` (`:1527`). Pane titles use `Design.Type.PaneTitle` (`:1362`).
- **PageHead.Create sites (11):** `W\Home\Customize.UI.cs:112`, `W\Entities\Artist.Discography.cs:1757` (crumb), `W\Shell\Shell.History.UI.cs:109`, `W\Entities\Profile.Lists.Page.cs:273` (crumb), `W\Entities\Recents.Page.cs:308`, `W\Shell\Shell.UI.cs:1662` (not found), `W\Entities\Search.Page.cs:224`, `W\Screens\Diagnostics.UI.cs:190`, `W\Screens\LogsPage.UI.cs:224` (crumb), `W\Screens\Settings.UI.cs:194`, `W\Screens\ReleaseNotes.UI.cs:232`.
- **Pages with their own hoist-aware heads:** Home (`W\Home\HomeScreen.cs:209`, `W\Home\Facet.UI.cs:179`, `W\Home\Zones.UI.cs:171`), Browse (`W\Entities\Browse.Page.cs:152` plus the Browse masthead), Library (`W\Entities\User.Page.Library.cs:1092`).
- **Views to row 2:** `PageHead.Publish(routeName, labels, selected, onSelect, trailing?)` (`W\Platform\Page.UI.cs:128`) writes to `Shell.PageViews` (`W\Shell\Shell.cs:2344-2412`; an LRU of 16 with a Version signal). Sites (6): `W\Home\HomeScreen.cs:384`, `W\Entities\Artist.Discography.cs:1709`, `W\Entities\Profile.Lists.Page.cs:307`, `W\Entities\Recents.Page.cs:387`, `W\Entities\Search.Page.cs:153`, `W\Screens\Settings.UI.cs:217`.
- **Entity band to row 2:** `PageHead.PublishBand(routeName, title, pivots, active, onPivot, actions?, onTitle?, accent?)` (`W\Platform\Page.UI.cs:123`) writes to `Shell.PageBands` (`W\Shell\Shell.cs:2416-2484`). Sites (4): `W\Entities\Artist.Page.cs:913`, `W\Entities\Profile.Page.cs:587`, `W\Entities\Episode.Page.cs:381`, `W\Entities\Show.Page.cs:191`. Under Zune these pages skip their in-page band; the hero collapses to the latched floor (`Detail.BandLayout.FloorLatch`, `W\Entities\Detail.cs:1145`).
- **Artist page bleed:** it publishes `ShellBackdrop` through `CoverShellTintBinder` (`W\Entities\Artist.Page.cs:473-511`).

---

## 4. Engine API additions (`C:\WAVEE\sidebar\fluent-gpu`, 7 commits)

Commits:
- 55dadcbcc: `TitleBarOptions.CaptionInk` and `AutoSuggestBox` TextInk/PlaceholderInk.
- e67e93ee2: merge of origin/main.
- d28a471fa P01: TitleBar keyed children and live-handle region push (G2).
- 7ed738322 P02: looping tracks survive a structural snap (K1).
- 50e6bb0ff P03: a disclosure band clears when the source version advances (S1).
- ff422a120 P04: disclosure bands slide with the clamped edge (S2a).
- fadcbe89e P05: `AutoSuggestBoxPresenter.Chrome` (C4).

| API | Location | Used by the app at |
|---|---|---|
| `TitleBarOptions.CaptionInk` / `TitleBar.CaptionInk` (`Func<ColorF>`, bound on caption, back and pane glyphs while the window is active; not part of the memo key) | `E\FluentGpu.Controls\TitleBar.cs:59`, `:215-226`, `:586-588` | `W\Shell\Shell.UI.cs:1759` |
| Keyed TitleBar root children (`tb-back`, `tb-pane`, `tb-tabs`, `tb-center`, `tb-trailing`, `tb-min/max/close`, …) and `Live(h)` checks in region push | `E\FluentGpu.Controls\TitleBar.cs:334-560`, `:603-633` | needed so the title-bar buttons work after a nav-style switch (`W\Shell\Shell.UI.cs:1776-1778`) |
| `AutoSuggestBox.TextInk/PlaceholderInk` and the `Create(textInk:, placeholderInk:)` parameters | `E\FluentGpu.Controls\AutoSuggestBox.cs:200`, `:229-243`, `:546` | `W\Shell\Shell.Masthead.UI.cs:795` |
| `EditableText.ForegroundInk/PlaceholderInk` | `E\FluentGpu.Controls\EditableText.cs:121-122`, `:1021-1022` | through AutoSuggestBox |
| `AutoSuggestBoxPresenter(... PopupChrome Chrome = Static)` | `E\FluentGpu.Controls\AutoSuggestBox.cs:46`, `:349-350` | `W\Shell\Shell.Masthead.UI.cs:778` (`PopupChrome.Dropdown`) |
| Disclosure band retire: `ItemsViewController.BandGone(band, count, sourceVer, resolve)`, `ItemRevealBand.SourceVersionAtCommit/Retired`, retire in render; `AnimEngine.RetireRevealBand`; `SceneStore.RetireRevealBand` | `E\FluentGpu.Controls\ItemsView.cs` about `:39-43`, `:299-300`, `:347-354`, `:410-413`, `:1006-1020`, `:1656-1689`; `E\FluentGpu.Engine\Animation\AnimScheduler.FlowReveal.cs:430`; `E\FluentGpu.Engine\Scene\SceneStore.cs:1771` | sidebar expanders and pin moves (P20 S2d; `R\src\apps\Wavee.Tests\SidebarDedupeMotionTests.cs`) |
| Disclosure slide: `RevealBand.Visible`, `RevealPlan.VisibleSpan`, `SetRevealBand(..., visible)`, FlowCursor slide | `E\FluentGpu.Engine\Scene\Columns.cs:436`; `E\FluentGpu.Engine\Animation\RevealPlan.cs:41`; `E\FluentGpu.Engine\Scene\SceneStore.cs:1714`; `E\FluentGpu.Engine\Scene\FlowCursor.cs` about `:56-94`; `AnimScheduler.FlowReveal.cs:391-394` | sidebar sections |
| Loop survives a structural snap (`AnimFlags.Loop` excluded from the snap) | `E\FluentGpu.Engine\Animation\AnimScheduler.Reflow.cs` about `:374`, `:520` | EqBar; the app's belt reseeds on activation (`W\Platform\Controls.cs` about `:825-840`) |

Tests and gallery: `E\FluentGpu.Engine.Tests\Scout\LoopSurvivesStructuralSnapTests.cs`, `RevealBandsTests.cs`, `RevealPlanTests.cs`, `FlowCursorTests.cs`; `E\FluentGpu.VerticalSlice\Suites\TitleBarSuite.cs`, `ControlsSuite.AutoSuggestChrome.cs`, `RevealSuite.cs`, `AnimSuite.cs`; `TitleBarCaptionInkTests.cs`.

The app also relies on things from engine main, not this branch: `ScrollView.AutoEdgeFadeBand` (`W\Platform\Controls.Art.cs`), and the hidden scrollbar lane not taking input (fluent-gpu 7dcd19d06, cited at `W\Platform\Controls.Art.cs:~604`).

---

## 5. Migration notes: main symbols the branch deletes or renames

Checked with `git grep` against origin/main and HEAD.

| Main symbol (main location) | On the branch |
|---|---|
| `Recents.Page.cs` `PivotTabs` component (main `:848`) | **Deleted** (eb696296). Recents uses PageHead and SelectorBar views. |
| `Home\Facet.Rules.cs` `FacetForm` (main `:42`), `Design.FacetTitleStyle`, `Design.FacetCompactStyle` | **Deleted.** Home uses a "Home" title plus `PageViewsStyle` views (`W\Platform\Design.cs:1499`). |
| `Design.Type.PivotLabel` (19/25/350) | **Deleted.** Replaced by `Design.Type.ZunePivot`/`ZuneSubPivot`/`Wordmark` (`W\Platform\Design.cs:1429-1449`). The "sanctioned off-ramp" doc changed. Same-named `PivotLabel(...)` helpers in Artist.Page and Profile.UI are unrelated local functions. |
| `Prefs.Appearance.ColorWashes()` (main `W\Platform\Prefs.cs:82`) | **Renamed and split** into `SurfaceWash()`/`AccentFromArtwork()`/`NowPlayingColors()`. The `ColorWashesEnabled` key is legacy (read only). The Settings `ColorWashes` row strings were replaced by `WashSurfaces*`/`WashAccent*`/`WashNowPlaying*`. |
| `Design.Wash.HeroAlpha(bool)`/`ShelfAlpha(bool)` | Gain a `rich` parameter; `PlaneAlpha`/`TintAlpha` are new. |
| `ShellMaterialState(Owner, Tint, Wash)` | Gains a 4th member `Backdrop` (default null). `ShellMaterial.Publish` gains `backdrop`. |
| `CoverKeyedVeil.Props` | Gains `Disabled`. `HeroOnlyVeil(Props)` becomes `(Props, rich)`. |
| `PlateKind {CardPlate, ListRow, Tile, Outline}` | Gains `TileStill`. |
| Title-bar theme toggle (`chrome-theme-toggle`, `Shell.UI.cs`) | **Deleted.** The profile menu's Theme row is the only toggle. |
| `Settings.SidebarLayoutCards(SidebarLayoutId, bool)` | Now `SidebarLayoutCards(ShellNavStyle, bool)` with 3 cards. `SidebarLayoutCardFace` and `SidebarPreview` take `ShellNavStyle`. |
| `Sidebar.SwitchLayout(SidebarLayoutId)` | Kept as a shim to `SwitchNavStyle`. |
| `SidebarMenuModel.Pane(...)` | New parameters `classicCovers`, `zune`, `zunePins`. Classic shows Show covers instead of Density. |
| `SidebarRowGeometry` | `IndentStep` 31 → 32; `HeaderButton` 24 → 28; `PaneTopInset`/`SeparatorLineTop` 3 → 4; new `Text` shape. |
| Track table: `TableDensityButton`, `TableSelectButton`, `TableDensityPanel`, `TableSearchField`, `TableSortCaret`, `CommandSurface`, `s_commandBarSplitParts`, `ToolbarPaneInset`; `InlineCommand {Shuffle, Density, Select}`; `CommandWidths(Play, Tune, Shuffle, Sort, Density, Select)` | **Deleted or reshaped.** Now `InlineCommand {None, Sort, Filter, FilterLabel}` and `CommandWidths(Tune, Sort, Filter)`. Sort uses the generic `Controls.SortButton`. |
| `Search.UI.cs` `QueryEcho`, `FacetRow`, `FacetTab`; `FacetUnderlineH/Ms`, `HeroHeight 228` | **Deleted.** Search uses PageHead (echo as title) and PageViews facets. |
| `Browse.Rules.cs` `MastheadReserve`, `FrameTop`/`FrameX`/`Frame()`, `WordChipH`/`NameChipH`, chip `Word`/`Name` builders; Browse `BandOf`/`WrapRow` | **Deleted or replaced** by PageGeometry plus `Controls.LinkChip`. `FamilyBodyPad` was re-implemented. |
| `User.UI.cs` `WordRail(...)` (Library sort words) | **Deleted** (one sort dropdown). `ViewToggle` is kept. |
| `Artist.Page.cs` `FacetExpandedTopInset` | **Deleted.** `ArtistHeroLayout.PageGutterFor` forwards to `PageGeometry.GutterFor`. |
| `Detail.UI.Hero.cs` `ToolbarRow`, `HeroActions`, `SkeletonActionRow`, `Band(...)`; `PlayButtonWidth 120` | Replaced by the one action row with the Play split, `BandCluster` and `BandByline(byline, ink)`. |
| `Detail.Insights.cs` `InsightsBandAction(toggle)`; the `RailHostsFacts`/`SheetHostsFacts`/`ShowsToggle`/`OpenFor` rules | Signature `(toggle, live)`. Insights are closed by default. |
| `Album.Page.cs` `TrailingShape`/`SkeletonShape`/`Skel*` constants | Reworked (the skeleton mirrors the loaded sections). |
| `Controls.Chip(label, selected, available, onClick)` | Gains `parts`; loses the capsule and size override (stock 32/r4 toggle). `ChipRail` gains `inBar`. `Controls.Words` uses `Design.Type.DisplayFace`. |
| `ButtonRules.FollowStyle(a, light)` | Gains `onMedia`. |
| `Home\Zones.UI.cs` `BodyClipInset`; `Facet.Rules` dim-plan pieces (`R\src\apps\Wavee.Tests\HomeUi\FacetDimPlanTests.cs` −13) | Reworked: the content clip is 0 when hoisted (`Facet.ContentClipFor(hoisted)`). |
| Settings page's own `Header()` plus SelectorBar and `Divider` | Replaced by `PageHead.Create`. Padding now uses the gutter and `PageGeometry.BottomReserve`. |
| Classic/Library footer Settings row | Removed; Ctrl+, opens Settings. |

**For the theme refactor:**
- Every chrome ink on the branch already goes through `Shell.Ui.Chrome*` binds (permanently 0-mix today) or plain `Tok.*`.
- Wash strength is centralised in `Design.Wash.*Alpha(light, rich[, paneDocked])`.
- Page typography is centralised in `Design.Type.PageTitle`/`PageMeta`/`PaneTitle`/`ZunePivot`/`ZuneSubPivot`/`Wordmark` and the `PageViewsStyle` family.
- The card ground is `Design.Colors.FileArea`; the stroke is `Tok.StrokeCardDefault` (hidden under Zune). The floating rail uses `Design.Colors.FloatingChrome`.
- The bleed field uses `Design.Palette.PageTone*` and `ArtistBleed.ScrimStops*`.
