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
