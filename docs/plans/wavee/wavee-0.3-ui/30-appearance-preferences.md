# Appearance & layout preferences — 0.3 visual fidelity contract

> 0.2.9 sources: `Platform/AppSettings.cs` (494) · `Features/Shell/SettingsPage.Appearance.cs` (651) ·
> `SettingsPage.General.cs` (251) · `SettingsPage.Playback.cs` (478) · `Design/AppearancePrefs.cs` (30) ·
> `Design/WaveeTheme.cs` (36) · `Design/WaveeTokens.cs` (297) · `Design/WaveePalette.cs` (333) ·
> `Design/WaveeType.cs` (283) · `Design/WaveeMotion.cs` (190) · `Design/WaveePicker.cs` (287) ·
> `Design/CoverPaletteLeaves.cs` (265) · `App/ZoomAutoPolicy.cs` (114) · `WaveeApp.cs` (439) ·
> `Features/Shell/WaveeShell.cs` (2 348) · `Features/Shell/ContentHost.cs` (322) ·
> `Features/Shell/ShellMaterialLayer.cs` (133) · `Features/Detail/DetailShell.cs` (858) ·
> `DetailVerticalLayout.cs` (662) · `DetailVerticalHero.cs` (579) · `DetailRail.cs` (607) ·
> `DetailLayoutBreakpoints.cs` (88) · `DetailRailPolicy.cs` (67) · `DetailConfig.cs` (311) ·
> `DetailHeaderMergeRules.cs` (39) · `ContextBandLayout.cs` (179) · `DetailTrackTableRules.cs` (279) ·
> `DetailTracks.cs` (4 525) · `Components/TrackRow.cs` (1 136) · `LikedCoverRules.cs` (307) ·
> `LikedCoverArt.cs` (156) · `LikedCoverTreatments.cs` (766) · `LikedFactsPanel.cs` (1 756) ·
> `Features/Shell/PlayerBar.cs` (1 548) · `ShellResponsiveLayout.cs` (247) ·
> `Features/Sidebar/SidebarDesign.cs` (134) · `SidebarPreferences.cs` (923) ·
> `Features/Player/LyricsView.cs` (3 160) · `LyricsBlurPolicy.cs` (37) · `ImmersiveLyricsSurface.cs` (550) ·
> `NowPlayingPanel.cs` (566) · `NpvPlayerCatalog.cs` (90) · `NpvPlayerPrefs.cs` (48)
> — plus the engine's `Splitter.cs` (333), `Marquee.cs` (357) and `AppHost.cs` (the theme-transition seam).
> | 0.3 target: `Platform/Platform.cs` + `Platform/Design.cs` (the store, the typed keys and every preference
> signal) and each surface's `*.UI.cs` / `*.Page.cs` (the reads) | **Waves 4–6**; owner **L** for
> `Design.cs`/`Controls.cs` and the preference epochs, **S** for `Platform.cs`, **R** for the Settings surface,
> **M/O** for the detail arms, **K** for lyrics + NPV, **I** for the player bar.
>
> Paths are cited as they are **today** (`src/apps/Wavee/...`). After Wave 0 the same file is
> `src/apps/_old/Wavee/...` with the identical relative path.

This chapter owns the dimension every other chapter under-covers: the surfaces are specified there in their
**default** configuration, and a user can change eighteen things about how they look and move. Nothing in 0.2.9
writes those variants down; the only record is the code Wave 0 moves and Wave 6 deletes.

---

## 0. The non-negotiables

Twelve properties of the 0.2.9 preference system that the rebuild must reproduce. Each is stated so it can be
checked, not admired.

**N1 — Every appearance preference applies LIVE, on the same frame, to every mounted surface.**
There is no "restart to see this" in the appearance surface, with exactly one deliberate exception (N2). The
mechanism is always the same shape: the settings store is **not observable**, so a writer persists the value and
then bumps an **epoch signal**; every consumer reads the epoch (subscribing) and then re-reads the store
(the truth). `AppearancePrefs.cs:12-18` states the contract; five epochs implement it — `AppearancePrefs.Epoch`
(`Design/AppearancePrefs.cs:8`), `DetailHeroPrefs.Epoch` (`Features/Detail/DetailVerticalHero.cs:577`),
`LyricsPrefs.Epoch` (`Features/Player/LyricsView.cs:3022`), `PlayerBarPrefs.Epoch`
(`Features/Shell/PlayerBar.cs:1133`), and `NpvPlayerPrefs.Epoch` (`Features/Player/NpvPlayerPrefs.cs:10`).
A preference must **never** be cached in a mirror signal that can drift from the store.

**N2 — The one exception is `UiCulture`, and it says so in its own row.**
`AppLocaleBootstrap.Initialize(settings)` runs once, at `Program.cs:90`, before the first mount; the writer at
`SettingsPage.General.cs:70-77` does not re-run it. The row's subtitle is
`Strings.Settings.Language.RestartSub` = "Changes are applied the next time Wavee starts."
(`assets/loc/en-US.json:790`). 0.3 may keep this, but it must keep the honest copy with it.

**N3 — Preference memory is per SCOPE, never "the last one wins".**
The detail rail keeps **four** independent `(width, collapsed)` pairs — Album, Playlist, Liked, Show — plus a
**fifth, separate** Uniform pair (`AppSettings.cs:144-171`, `RailScope` enum `DetailRailPolicy.cs:10`). Turning
"Keep left-rail same size" ON must not flatten the four; turning it OFF must restore each surface's own
remembered width. The same discipline governs the sidebar (one `(width, userSet, collapsed)` triple **per
design**, `AppSettings.cs:408-413`) and the Library page (one state set **per kind**, `AppSettings.cs:382-394`).

**N4 — A stored value is CLAMPED at the seed, never seeded raw.**
`DetailRailPolicy.ClampStored` (`DetailRailPolicy.cs:45-46`, bounds 180/480) is applied in `DetailShell.SeedRail`
(`DetailShell.cs:148-152`) *and* again in `ResyncRail` (`:168-172`). `ZoomLadder.Snap` is applied to the persisted
zoom before the window comes up (`Program.cs:550`). `ShellResponsiveLayout.ClampRailWidth` (200/500,
`ShellResponsiveLayout.cs:126-127`) and `ClampDockedVideoHeight` (`:133-138`) are applied at
`WaveeShell.cs:259-261`. A value written by a build with different bounds, or hand-edited in the registry, must
never reach layout.

**N5 — Every int-enum key clamps an unknown value to a defined one, silently.**
`LikedCoverRules.FromSetting` → Stock outside `[0,8]` (`LikedCoverRules.cs:75-78`); `LyricsPrefs.Clamp` → None
outside `[0,2]` (`LyricsView.cs:3040`); `SidebarDesignInfo.FromInt` → Classic (`SidebarDesign.cs:72`);
`ZoomMode` is `Math.Clamp(..., 0, 2)` at the read (`SettingsPage.Appearance.cs:189`). A downgrade from a build
that shipped more options must degrade, not blank.

**N6 — A visibility preference must never gate a FETCH.**
`PlaysColumn` is visibility only: kind 185 rides every list surface's trait bundle unconditionally
(key `AppSettings.cs:90`, the reason in its comment `:84-89`; `DetailShell.cs:384-392`). The 0.2.9 comment records why — gating the fetch permanently
starved every list opened while the setting was off, and 185 has no retry surface. In 0.3 the equivalent rule is:
a preference may change what a page **renders**, never what it **demands**.

**N7 — Collapsing a pane is not a width choice.**
`SidebarKeys.WidthUserSet` latches on the first committed seam drag only; collapse/expand must never set it
(`AppSettings.cs:26-29`, `SidebarDesign.cs:118-133`). The detail rail follows the same split: collapse has its own
key (`DetailAlbumRailCollapsed` …, `AppSettings.cs:157-160`) so re-opening restores the chosen width, not a
default.

**N8 — A responsive mode may ignore a stored preference, and must give it back on the way out.**
The detail rail's persisted width and collapsed flag are honoured **only in layout mode 0**
(`DetailRailPolicy.ResizableMode = 0`, `DetailRailPolicy.cs:20`; applied `DetailShell.cs:624-629`). Modes 1 and 2
compose their breakpoint rail (224 / 188) and ignore both; the preference returns intact when the page is wide
again. The grip that could undo a collapse does not exist at those modes, which is exactly why the collapsed flag
cannot be honoured there.

**N9 — The default vector reproduces today's first launch pixel for pixel.**
`ThemeMode 0 System` · `ZoomMode 0 Auto` + `ZoomLevel 1.0` · `UiCulture "system"` · `MarqueeEnabled true` ·
`ColorWashesEnabled true` · `RowDensity 1 Default` · `TrackRowStyle 0 Modern` · `HideTrackArtwork false` ·
`TempoColumn false` · `PlaysColumn false` · `DetailPageLayout 0 Automatic` · `DetailRailUniform false` ·
rail widths 280/240/240/280 with all four collapsed flags false · `LikedCoverStyle 1 Lens` (degrades to Stock
until the library owns ≥ 4 distinct covers, so a fresh install still paints the bundled PNG —
`LikedCoverRules.MinTiles`, `LikedCoverRules.cs:139-150`) · `LyricsAnimatedBackdrop true` ·
`LyricsSecondaryLine 0` · `LyricsBlurStrength -1 Auto` · `PlayerBarShowRemaining true` · `ShellRailWidth 340` ·
`ShellDockedVideoHeight 0` (= 16:9 of the live rail width) · `SidebarDesign 0 Classic` with
`sidebar.classic.width 240`, `userSet false`, `collapsed false` · `NpvPresentation 0 Cover` ·
`NpvPlayerStyle 0 Record`. A 0.3 build launched on a wiped store must be indistinguishable from a 0.2.9 build
launched on a wiped store.

**N10 — Reduced motion is an OS VALUE that is read, never an app preference that is branched.**
`Motion.ReducedMotion` (engine) vetoes the lyrics backdrop drift independently of `LyricsAnimatedBackdrop`
(`ImmersiveLyricsSurface.cs:139`), holds the marquee still whatever `MarqueeEnabled` says
(`FluentGpu.Controls/Marquee.cs:172`), and drops card hover travel (`Components/MediaCard.cs:820-821`).
`WaveeMotion.cs:17-19` records the rule. 0.3 must not add a Wavee "reduce motion" setting that competes with it.

**N11 — No environment-variable switches for any of this.** CLAUDE.md's rule, and the reason
`LyricsAnimatedBackdrop` is a setting rather than an env var is written out at `AppSettings.cs:119-122`.

**N12 — One defect must NOT be ported: the Settings row-density write does not reach a mounted page.**
`SettingsPage.Appearance.cs:242-247` (`SetDensity`) writes the key and bumps only the *settings page's* own
re-render — there is no `AppearancePrefs.Bump()`, unlike its sibling `SetTrackListStyle` at `:249-255` (the bump at `:253`).
`DetailShell` re-seeds `_density` from the store only inside a `UseEffect` keyed on the **context uri**
(`DetailShell.cs:372`, key `:375`), so a mounted or KeepAlive-parked detail page keeps its old density until the
user navigates to a different context. The in-page density control (the list's own command bar, routed through
`DetailHandlers.Density`) is live; the Settings one is not. In 0.3 both writers go through the same epoch.

---

## 1. Anatomy

This chapter's "component tree" is not a page. It is the **preference plumbing**: one store, one typed key
registry, five epoch signals, and the ~50 render sites that read a key. §1.1 is the inventory (which preference
changes what, and on which signal it rides); §1.2 draws the 0.2.9 tree with every node's file:line and role;
§1.3 draws the same tree in 0.3 terms; §1.4 states how live data reaches a node whose props froze at mount.

### 1.1 The matrix

One row per user-settable preference that changes how a surface looks or moves. "Live-update mechanism" names the
signal a mounted surface takes its edge from; "—" means the value is read once at composition and needs a
remount or a relaunch.

| Key (store name) | Setting name + options, as presented (file:line) | Default | Surfaces it changes | What changes visually | Live-update mechanism (file:line) | Sibling chapter that must show it |
|---|---|---|---|---|---|---|
| `ThemeMode` `theme.mode` | "Theme" · SelectorBar System / Light / Dark (`SettingsPage.Appearance.cs:337-339`; labels `settings.choice.system/light/dark`) | 0 System | every surface | the whole token set swaps (`Tok.Use`, `WaveeTheme.cs:27`); `Elevation.Card` itself is theme-dependent — dark `Blur 8 OffsetY 2 #00000033`, light `Blur 4 OffsetY 2 #0000001A` (`fluent-gpu/.../Elevation.cs:18-21`) | `WaveeTheme.ApplyThemeMode` + a 250 ms re-theme cross-fade (`SettingsPage.Appearance.cs:208-213`); OS changes via `FluentApp.SystemColorsChanged` (`WaveeApp.cs:43-63`), ignored unless mode 0 (`:50`) | `00-design-system.md` |
| `ZoomLevel` `appearance.zoom` | "Zoom" · ComboBox, 13 items: `Auto (n%)` + the 12 ladder rungs (`SettingsPage.Appearance.cs:343-345`, labels `:76-93`) | 1.0 | every surface; **layout tiers** | `viewportDip = clientPx / (osDpi × zoom)` (`fluent-gpu/.../AppHost.cs:5249-5254`) — zooming IN shrinks the DIP viewport and can DEMOTE a page across a tier | `FluentApp.SetZoom` live, no restart (`SettingsPage.Appearance.cs:237`, Auto arm `:228`); a chord/wheel change is persisted by a **2 s POLLING timer**, not a trailing debounce — it writes only when `\|live − stored\| > 0.004` (`WaveeApp.cs:239-244`); the picker writes immediately | `00-design-system.md`, `18-shell-frame.md` |
| `ZoomMode` `appearance.zoom.mode` | the same ComboBox: index 0 = Auto, any rung = Manual (`SettingsPage.Appearance.cs:222-240`). **Dense (2) has no UI writer** | 0 Auto | every surface | Auto re-derives zoom from the display on resize-settle; Manual pins the rung | `WaveeShell.cs:598-632`, 500 ms debounce (`:591`) | `00-design-system.md` |
| `UiCulture` `localization.culture` | "Language" · ComboBox System / English (US) / Nederlands / 한국어, the last two **shown disabled** (`SettingsPage.General.cs:32-43`, row `:84-87`) | `"system"` | every string | every label's length; no mirroring (see §2.4) | — next launch only (`Program.cs:90` → `App/AppLocale.cs:16-33`) | `00-design-system.md`, `27-settings-and-diagnostics.md` |
| `MarqueeEnabled` `appearance.marquee.enabled` | "Marquee text" · ON switch · "Scroll overflowing titles instead of truncating them" (`SettingsPage.Appearance.cs:349-350`) | true | player bar title + artists; the **now-playing track row only** | scrolling run ⇄ `TextTrim.CharacterEllipsis` with no edge fade | `AppearancePrefs.Epoch` (`PlayerBar.cs:128-129`; `DetailTracks.cs:707-708`, carried in `TrackRowsSnapshot`) | `01-track-row.md`, `20-player-bar.md` |
| `ColorWashesEnabled` `appearance.colorWashes.enabled` | "Color washes" · ON switch · "Tint the shell and page surfaces from artwork" (`SettingsPage.Appearance.cs:351-352`) | true | shell material (⇒ title row, sidebar, player dock), detail page ground, artist hero, Home / Home-section / Recents washes | art-derived tint ⇄ `WaveeColors.ShellGround @ 3 %`; the detail tone plane paints **nothing** | `AppearancePrefs.Epoch` — `HomePage.cs:216-217`, `HomeSectionPage.cs:168-169`, `RecentsPage.cs:310-311` & `:2478-2479`, `ArtistPage.cs:63-64`, `DetailShell.cs:212-213` | `03-detail-frame.md`, `08-artist-and-discography.md`, `10-home.md`, `18-shell-frame.md` |
| `RowDensity` `detail.rowdensity` | "Row density" expander · four preview cards Compact / Default / Cozy / Comfortable (`SettingsPage.Appearance.cs:411-449`) | 1 Default | every detail track table + the in-page command bar | row height 40/48/56/64 Modern, 36/40/44/48 Classic; art 32/32/40/48 Modern, 32/32/32/40 Classic | in-page: `DetailHandlers.Density` signal + a list **Key** remount (`DetailTracks.cs:1090`). **From Settings: broken — see N12** | `01-track-row.md`, `04-detail-track-table.md` |
| `TrackRowStyle` `detail.rowstyle` | "Track list style" expander · two preview cards Modern / Classic (`SettingsPage.Appearance.cs:452-478`) | 0 Modern | every track table; artist Popular; the queue panel | inset pill + 6-DIP corners + border + zebra ⇄ flush square rows + 1-DIP hairline; artist lane splits out; no thumb lane in Classic | `AppearancePrefs.Bump()` (`SettingsPage.Appearance.cs:253`) → `DetailTracks.cs:707-717`; list Key carries `:classic`/`:modern` (`:1090`) | `01-track-row.md`, `04-detail-track-table.md`, `21-right-rail-npv-queue-stage.md` |
| `HideTrackArtwork` `appearance.trackArtwork.hidden` | "Always hide track artwork" · CheckBox inside the Row-density expander (`SettingsPage.Appearance.cs:421-434`) | false | every track cell app-wide: detail, artist top tracks, artist Popular, Home artist rows, Library, Search, queue, NPV, stage, video rail | the thumb lane vanishes; its width returns to the title. Heroes, cards, sidebar covers, player artwork unaffected | `AppearancePrefs.TrackArtworkHidden` (`AppearancePrefs.cs:16-18`) — **14** call sites, enumerated in §1.2 | `01-track-row.md`, `04-detail-track-table.md`, `21-right-rail-npv-queue-stage.md` |
| `TempoColumn` `detail.tempoColumn` | not a Settings row — the list's **More** flyout (`DetailTracks.cs:4376-4386`) | false | playlist + Liked track tables (`ShowTempo`, `DetailConfig.cs:200,219`) | an 80-DIP right-aligned "BPM · Key" lane, gated `Tier <= 3` (`TrackRow.cs:578`) | `DetailHandlers.TempoColumn` signal (`DetailShell.cs:383`) | `04-detail-track-table.md`, `06-playlist.md`, `07-liked-songs.md` |
| `PlaysColumn` `detail.playsColumn` | the same **More** flyout; offered only where `PlaysColumnOptIn` (`DetailConfig.cs:179,200,219`) | false | playlist + Liked track tables only (album surfaces always show the lane) | a 52-DIP right-aligned "Plays" lane, gated `tier < 3` (`DetailTracks.cs:493,506`) | `DetailHandlers.PlaysColumn` signal (`DetailShell.cs:388-392`) | `04-detail-track-table.md`, `06-playlist.md`, `07-liked-songs.md` |
| `DetailPageLayout` `detail.page.layout` | "Track page layout" expander · two wireframe cards Automatic / Hero (`SettingsPage.Appearance.cs:485-495`, cards `:520-586`) | 0 Automatic | album, single, compilation, prerelease, playlist, Liked, local files. **Not podcast shows** | Hero forces `mode = Vertical` at every width (`DetailShell.cs:509-511`): no rail, ever | `DetailHeroPrefs.Bump()` (`SettingsPage.Appearance.cs:260`) read at `DetailShell.cs:463` | `03-detail-frame.md`, `05-album.md`, `06-playlist.md`, `07-liked-songs.md` |
| `DetailRailUniform` `detail.rail.uniform` | "Keep left-rail same size" · toggle, shown **only while Automatic is selected** (`SettingsPage.Appearance.cs:501-515`) | false | every two-column detail surface | all four surfaces resolve to one shared `(width, collapsed)` pair (`DetailRailPolicy.ScopeFor`, `:52`) | the **same** `DetailHeroPrefs` epoch (`SettingsPage.Appearance.cs:269`; re-sync `DetailShell.cs:464-472`) | `03-detail-frame.md` |
| `DetailAlbumRailWidth` / `…Playlist…` / `…Liked…` / `…Show…` | no picker — the **rail grip** (a `Splitter`, `DetailShell.cs:797-817`); "Clear all remembered sizes" resets all four (`SettingsPage.Appearance.cs:277-290`, `:510-513`) | 280 / 240 / 240 / 280 (`WaveeSize.RailAlbum/RailPlaylist`, `WaveeTokens.cs:60`) | the mode-0 detail rail on album/single/compilation/prerelease, playlist + local, Liked, show | rail column width, 180…480 | drag writes the signal directly, commits to the store on release (`DetailShell.cs:804-808`); `ResyncRail` on the epoch (`:168-172`) | `03-detail-frame.md` |
| `Detail*RailCollapsed` ×4 | the grip's force-push detent: push past `ForcePush 44` (raw ≈ 136) collapses; pull past `ReExpand 220` re-opens (`DetailShell.cs:200`) | false | same four scopes, **mode 0 only** | the rail becomes a 96-DIP compact identity strip (`RailCompactW`, `DetailShell.cs:203`) | same as the widths | `03-detail-frame.md` |
| `DetailUniformRailWidth` / `…Collapsed` | written only while uniform mode is on; a deliberately **separate** key pair (`AppSettings.cs:167-171`) | 240 / false | all four surfaces at once | one shared rail width everywhere | same | `03-detail-frame.md` |
| `LikedCoverStyle` `appearance.likedCover.style` | no Settings row — the **cover picker** on the Liked page itself (`LikedCoverPicker.cs:150-190`) | 1 Lens | the Liked collection cover, every place it is drawn (page hero, sidebar, Home cards, compact strip) **and the Liked page's own ground tone** (`DetailShell.LikedToneAnchor`, `:846-857`) | nine treatments (W24) degrading to the bundled PNG below their tile minimum | `AppearancePrefs.LikedCover` (`AppearancePrefs.cs:22-29`), bumped by the picker (`LikedCoverPicker.cs:187`) | `07-liked-songs.md`, `25-sidebar.md` |
| `LyricsAnimatedBackdrop` `lyrics.backdrop.animated` | "Animated lyrics backdrop" · ON switch (`SettingsPage.Appearance.cs:386-387`) | true | the **immersive** lyrics stage only (the rail panel has no backdrop) | the baked-blur cover drifts on two sinusoids ⇄ held perfectly still, **no ticker mounted** | `AppearancePrefs.Epoch` (`ImmersiveLyricsSurface.cs:135-136`) | `22-lyrics.md` |
| `LyricsSecondaryLine` `lyrics.secondary` | "Lyrics second line" · SelectorBar Off / Translation / Romanization (`SettingsPage.Appearance.cs:381-383`); also a cycling header toggle in both lyrics surfaces | 0 Off | rail lyrics + immersive stage | a second run under each line at 0.62 × the lyric size; **every row's height changes** | `LyricsPrefs.Set` → `Epoch` (`LyricsView.cs:3067-3071`); the view marks the viewport `LayoutDirty \| VirtualRangeDirty` + `ResetScrollSnap()` (`LyricsView.cs:394-399`) | `22-lyrics.md` |
| `LyricsBlurStrength` `appearance.lyrics.blurStrength` | "Lyrics blur" · Slider 0–100 step 1, ticks 25, length 180, + an "Auto" reset link shown only while pinned (`SettingsPage.Appearance.cs:139-154`, row `:388-390`) | −1 Auto (= 100 normal GPU, 40 weak) | rail lyrics + immersive stage | the depth-of-field σ ladder and the glow halo scale linearly; 0 emits no blur layer at all | `LyricsPrefs.Bump()` (`SettingsPage.Appearance.cs:312`); ramp re-armed on `DepKey.From(strength)` (`LyricsView.cs:363-373`) | `22-lyrics.md` |
| `PlayerBarShowRemaining` `playerbar.duration.remaining` | "Show remaining time" · toggle, Settings › Playback › Player bar (`SettingsPage.Playback.cs:103-104`); **also click-to-toggle on the label itself** (`PlayerBar.cs:1216-1220`) | true | the player bar's right time label; the immersive stage's (`StageIdentity.cs:363`) | `-m:ss` countdown ⇄ the total duration, in a 44-DIP slot | `PlayerBarPrefs.Epoch` (`PlayerBar.cs:1151-1154`) | `20-player-bar.md`, `21-right-rail-npv-queue-stage.md` |
| `ShellRailWidth` `shell.rail.width` | no picker — the shell's right-rail splitter | 340 (`ShellResponsiveLayout.RailDefaultW`, `:126`) | the right rail (lyrics / queue / now playing / video) and, by subtraction, the content region | rail column width, 200…500 | seeded + clamped `WaveeShell.cs:259`, committed on splitter release `:453-458` | `18-shell-frame.md`, `21-right-rail-npv-queue-stage.md` |
| `ShellDockedVideoHeight` `shell.rail.docked-video.height` | no picker — the rail's vertical splitter (`RightRail.cs:206-228`) | 0 = 16:9 of the live rail width | the docked video cap inside the right rail | video cap height, floor `railW × 9/16`, ceiling 560 (`ShellResponsiveLayout.cs:130-137`) | committed on the vertical splitter's release (`RightRail.cs:220-228`) | `21-right-rail-npv-queue-stage.md` |
| `SidebarDesign` `sidebar.design` | "Design" expander · three cards Classic / Library / Custom (`SettingsPage.Appearance.cs:606-649`); also the sidebar's own layout menu | 0 Classic | the whole left pane | three documents through one renderer; different width tiers; V3 adds a fixed chrome stack | `SidebarPreferences.Design` signal, subscribed directly (`SettingsPage.Appearance.cs:618-620`); the pane remounts on the design's mount key (`SidebarHost.cs:56`) | `25-sidebar.md` |
| (no key) the expander's **“Customize sidebar…” row** | composed **only while Curated is the active design** (`SidebarDesignGating.CanCustomize`, `SettingsPage.Appearance.cs:629-637`) | absent | the Settings expander itself | one row appears/disappears under the design cards; it navigates to `SidebarLayoutMenu.CustomizeRoute` and never switches design silently | the same `SidebarPreferences.Design` subscription | `25-sidebar.md`, `27-settings-and-diagnostics.md` |
| `sidebar.<slug>.width` / `.width.userSet` / `.collapsed` (per design) | the pane seam drag; collapse via the pane's own control | Narrow tier per design: Classic 240, V3 300, Curated 280 (`SidebarDesign.cs:62-69`) | the left pane | pane width 180…460, or the 56-DIP rail when collapsed (`ShellResponsiveLayout.cs:11`) | `SidebarPreferences` (`:83,150,164,233,249,255-258`) | `25-sidebar.md` |
| `sidebar.classic.section.{pinned,library,playlists}` | the section chevrons | true / true / true | Classic sidebar | sections collapse to their header row | the layout document | `25-sidebar.md` |
| `sidebar.v3.{filter,qualifier,sort,desc,view,size}` | the V3 chrome stack's own controls | 0 / 0 / 0 Recents / false / 1 List / 1 M | Library V3 sidebar | list ⇄ grid, card size S/M/L, chip rail state | `SidebarPreferences` | `25-sidebar.md` |
| `sidebar.curated.template` | the customizer | `wavee.curated.default` | Curated sidebar | which section set is composed | the layout document | `25-sidebar.md` |
| `sidebar.curated.rail.labels` | **none — dead key** | false | none | nothing. **No reader or writer exists in the repo** (grep for both the constant and the literal returns only `AppSettings.cs:434` and the test shim `TestAppSettingsShim.cs:126`). The rail's design position is that the tooltip IS the label (`SidebarPaneRail.cs:18-20`) | — | `25-sidebar.md` (as a deletion) |
| `NpvPresentation` `npv.presentation` | "Hero" · SelectorBar `Cover` / `<current style>` (`SettingsPage.Appearance.cs:398-400`); also the NPV header row, a flyout, the art context menu and the palette | 0 Cover | the right rail's pinned Now-Playing hero | the cover ⇄ one of twelve player decks | `NpvPlayerPrefs.Epoch` (`SettingsPage.Appearance.cs:184`) | `21-right-rail-npv-queue-stage.md` |
| `NpvPlayerStyle` `npv.player.style` | "Player style" · ComboBox, twelve presets in catalog order, index == id (`SettingsPage.Appearance.cs:401-403`) | 0 Record | the same hero | Record / Cassette / Reel / CD / Turntable / iPod / Winamp / VU / Zune / WMP / Canvas / Picture (`NpvPlayerCatalog.cs:16-17`) | `NpvPlayerPrefs.SetStyle` (`:35-41`) — picking a style also flips presentation to Player | `21-right-rail-npv-queue-stage.md` |
| `npv.player.<presetSlug>.<optionSlug>` | per-deck option rows in the same group, plus the flyout and the art context menu | 0 — the catalog lists the default FIRST, so a stored 0 is always valid (`NpvPlayerCatalog.cs:9`) | the selected deck only | **two option KINDS** (`NpvOptionKind`, `:6`): `Finish` is a **Swatch** with FIVE choices — black `#15171C`, clear `#8A93A6`, album (`FromCover` = 0, derive the vinyl colour from the cover), splatter `#F5F1EA`, marble `#6B4FA8` (`:22-27`); `Size` (12″ / 7″), `Rpm` (33 / 45) and `Sleeve` (shown / hidden) are **Segmented** two-choice (`:28-30`). Which options a preset carries is per-preset (`Preset.Options`, `:14`) | `NpvPlayerPrefs.SetChoice` (`:43-47`) — clamped by `ClampChoice` against the live `Choices.Length` (`:18`) | `21-right-rail-npv-queue-stage.md` |
| `library.<kind>.{leftw,midw,view,size,sort,desc}` + `.album.{view,size,sort,desc}` | the Library page's own sort/view control (`LibraryPage.cs:472`) and its column splitters | leftw 280 (artists) / 340, midw 440, view 1, size 1, album.view 3 Grid (`AppSettings.cs:382-394`) | the Library master–detail page, per kind | column widths; list row 40 (compact) / 60, grid cell `(compact ? 88 : 116) + size × (compact ? 16 : 24)`, gutter 8 (`LibraryPage.cs:507-509`) | signals + `SaveState` (`LibraryPage.cs:178`) | the Library chapter |
| `detail.sort.col:<ctxUri>` / `detail.sort.desc:<ctxUri>` | no Settings row — the track table's **column headers** and the toolbar Sort verb; **one pair PER CONTEXT URI** (`DetailShell.cs:178-179`) | `-1` “never chosen” / false — the `-1` sentinel is what makes the fallback PER KIND (album/playlist open in context order, Liked opens DateAdded-desc, `DetailShell.cs:180`, `:370-371`) | every detail track table | the visible ROW ORDER, and which header carries the descending caret (`NumCaretSlot` 9, `DetailTrackTableRules.cs:239`) | re-seeded by the **same `UseEffect` keyed on the context uri** that re-seeds `_density` (`DetailShell.cs:361-375`); the writer is `SetSort` (`:376-381`), which bumps no epoch because it writes the live signal first. Sort is deliberately **not** in the list key (`DetailTracks.cs:1071-1073`) | `04-detail-track-table.md` |
| `sidebar.onboarding.seen` | none — set true for existing installs by `SidebarBootstrap`, and by every exit path of the design chooser (`AppSettings.cs:39`) | false | the one-time sidebar **design-chooser** plate over the shell | a whole modal surface appears, once, ever | — read at launch | `25-sidebar.md` |
| `workspace.tabs.pinned` | the tab strip's pin verb | `""` | the merged chrome row | how many pinned tabs the tab island carries, which changes `MergedChromeLayout.LeadClusterW` and can demote the centred search to an icon (`MergedChromeLayout.cs:44-60`) | the workspace store | `18-shell-frame.md` |
| `diag.fpsOverlay` | "FPS overlay" · switch, disabled while Developer mode is off (`SettingsPage.General.cs:125-127`) | false | an always-on-top HUD | a frame-time overlay over every surface | `DeveloperMode.FpsOverlay` signal | `27-settings-and-diagnostics.md` |
| `diag.developerMode` | "Developer mode" · switch (`SettingsPage.General.cs:121-123`) | false | composes away/in the API console, the lyrics inspector, the V3 overflow entry, test-notification rows, the home image tracer, "Simulate an update" (`SettingsPage.General.cs:144-153`) | whole rows and pages appear/disappear | `DeveloperMode.Enabled` signal | `27-settings-and-diagnostics.md` |
| `video.placement` / `video.pip.rect` / `video.window.rect` / `video.aspect.mode` / `video.aspect.customRatio` / `video.window.ontop` | the video surface's own placement verbs and aspect menu | `""` / `""` / `""` / `"fit"` / (`VideoAspectPersistence.DefaultCustomRatio`) / true | the movable video surface | where the video lives (docked / PiP / detached window), its rect, and its letterbox policy | the placement store | `21-right-rail-npv-queue-stage.md` |
| `tips.seen` | every teaching tip's ✕; "Show tips again" writes `""` (`AppSettings.cs:172-178`) | `""` | every surface that hosts a tip | a teaching callout is present or absent | the tips store | `27-settings-and-diagnostics.md` |
| `app.whatsnew.autoShow` | "Show what's new" · toggle | true | first launch after an update | the What's New plate opens over the shell | — read at launch | `27-settings-and-diagnostics.md` |

Keys deliberately **out** of this matrix because they change behaviour, not pixels: `playback.*`, `audio.cache.*`,
`notify.*` (except that the in-app centre's own rows are a surface the notifications chapter owns), `app.protocol.spotify`,
`app.startOnLogin`, `diagnostics.log.*`, `gpu.preferred*` (a device pick, though it does re-create the swapchain),
`session.private`, `setup.*`, `crash.*`, `sidebar.pins.migratedToServer`, and the three legacy v0 sidebar keys at
`AppSettings.cs:30-32` that only the v0→v1 migration reads.

### 1.2 The 0.2.9 tree — store, keys, epochs, readers

Every node below is a real type. The tree is drawn in the order a value travels: the registry names a key, the
store holds it, a **writer** persists it and bumps an epoch, and the **readers** re-run and re-read the store.

```
Services.Settings : IAppSettings                          App/Services.cs:580 — the ONE store instance
  └─ AppDataSettings over AppDataStore.ForUnpackaged("Wavee","Wavee")   fluent-gpu/.../AppDataStore.cs:38-48
     · HKCU\Software\Wavee\Wavee\Settings · REG_SZ / REG_DWORD / REG_QWORD (float = IEEE-754 bits, :71-72)
     · every accessor defensive: a storage failure returns the key's Default, never throws  AppSettings.cs:446-494

WaveeSettings : the key REGISTRY (one file, one line per remembered thing)      Platform/AppSettings.cs:20-21
  ├─ SettingKey<T>(Name, Default)                                                AppSettings.cs:10
  ├─ appearance   ThemeMode :61 · UiCulture :64 · HideTrackArtwork :71 · LikedCoverStyle :79 ·
  │               MarqueeEnabled :97 · ColorWashesEnabled :100 · ZoomLevel :106 · ZoomMode :112 ·
  │               ZoomModeBootstrapVersion :116 · LyricsAnimatedBackdrop :123 ·
  │               LyricsSecondaryLine :131 · LyricsBlurStrength :136              AppSettings.cs:58-136
  ├─ detail       RowDensity :65 · TrackRowStyle :68 · TempoColumn :83 · PlaysColumn :90 ·
  │               DetailPageLayout :93 · Detail{Album,Playlist,Liked,Show}RailWidth :144-147 ·
  │               …RailCollapsed :157-160 · DetailRailUniform :166 ·
  │               DetailUniformRail{Width,Collapsed} :170-171                     AppSettings.cs:65-171
  │               (the per-context SORT pair is NOT in this registry — DetailShell.SortColKey/SortDescKey
  │                build a SettingKey<T> per context uri at the call site,          DetailShell.cs:178-179)
  ├─ shell        ShellRailWidth · ShellDockedVideoHeight                         AppSettings.cs:149-152
  ├─ sidebar      sidebar.design :36 · sidebar.<slug>.{width,width.userSet,collapsed} (SidebarKeys :409/:411/:413)
  │               · section flags :416-418 · V3 chrome :421-428 · curated template :433
  │               · (DEAD) sidebar.curated.rail.labels :434                       AppSettings.cs:405-440
  └─ npv          NpvPresentation · NpvPlayerStyle · npv.player.<preset>.<option>  NpvPlayerKeys.Option(...)

THE FIVE EPOCHS — "the store is not observable, so the epoch is the update EDGE"   AppearancePrefs.cs:12-18
  ├─ AppearancePrefs.Epoch     Design/AppearancePrefs.cs:8      rows · washes · marquee · liked cover · backdrop
  │    ├─ Bump()               :9
  │    ├─ TrackArtworkHidden(settings)  :16-18   — 14 call sites (listed under READERS)
  │    └─ LikedCover(settings)          :26-29   — clamped through LikedCoverRules.FromSetting
  ├─ DetailHeroPrefs.Epoch     Features/Detail/DetailVerticalHero.cs:577   page layout · rail uniform · rail reset
  ├─ LyricsPrefs.Epoch         Features/Player/LyricsView.cs:3022          second line · blur
  │    ├─ Clamp / BitFor / Next / Tooltip   :3040-3063     (the BACKDROP deliberately rides AppearancePrefs)
  │    ├─ Available : Signal<int>           :3037   which layers the document on screen carries
  │    └─ Set(settings, mode)               :3067-3071   the ONE writer for the picker AND both header toggles
  ├─ PlayerBarPrefs.Epoch      Features/Shell/PlayerBar.cs:1133            show-remaining
  └─ NpvPlayerPrefs.Epoch      Features/Player/NpvPlayerPrefs.cs:10        presentation · style · per-deck options
       └─ ClampPresentation / ClampStyle / ClampChoice   :16-18   · SetStyle also flips presentation  :35-41

WRITERS (persist, THEN bump — never the other way round)
  ├─ AppearanceToggle(settings, key)        SettingsPage.Appearance.cs:125-133  Set → AppearancePrefs.Bump → Bump
  │    used by: Marquee :349 · Color washes :351 · Lyrics backdrop :386
  ├─ TrackArtworkCheckBox                   SettingsPage.Appearance.cs:427-434  same shape
  ├─ SetTheme(mode)                         :208-213   WaveeTheme.ApplyThemeMode → requestTheme?.Invoke(250f)
  ├─ SetZoom(i)                             :222-240   FluentApp.SetZoom FIRST, then Set(ZoomLevel) + Set(ZoomMode)
  ├─ SetDensity(i)                          :242-247   ** NO epoch bump — the N12 defect **
  ├─ SetTrackListStyle(i)                   :249-255   AppearancePrefs.Bump
  ├─ SetPageLayout / SetRailUniform / ResetPerScopeRailPrefs   :257-291   DetailHeroPrefs.Bump ×3
  ├─ SetLyricsSecondary / SetLyricsBlur / ResetLyricsBlur      :295-325   LyricsPrefs.Bump
  ├─ SetNpvPresentation / SetNpvStyle                          :330-331   NpvPlayerPrefs.Bump (inside the setter)
  ├─ the rail GRIP's drag-end                DetailShell.cs:804-808     writes THIS scope's own pair
  ├─ the shell rail splitter's drag-end      WaveeShell.cs:453-458      ClampRailWidth → Set(ShellRailWidth)
  ├─ the Liked cover PICKER                  LikedCoverPicker.cs:187    AppearancePrefs.Bump
  ├─ the player-bar time LABEL               PlayerBar.cs:1200-1207     Set → PlayerBarPrefs.Bump
  └─ both lyrics header toggles              LyricsPrefs.Set (LyricsView.cs:3067-3071)

READERS — "subscribe to the epoch, then re-read the store"
  ├─ DetailShell.Render                      DetailShell.cs:212 (AppearancePrefs) · :463 (DetailHeroPrefs)
  │    ├─ colorWashesDisabled                :213        → the tone plane + the shell tint binder
  │    ├─ UseEffect(railEpoch) → ResyncRail ×4           :464-472
  │    ├─ the Hero override                  :509-511    mode = Vertical
  │    └─ LikedToneAnchor(m, settings)       :846-857    reads AppearancePrefs.LikedCover
  ├─ TrackList's rowsSnapshot memo           DetailTracks.cs:698-719   carries noMarquee / hideTrackArtwork /
  │    classic INTO the snapshot (:713-717) — the contract is stated at :702-706
  ├─ the virtualized list's Key              DetailTracks.cs:1090      "d<density>" + ":classic"/":modern"
  ├─ PlayerBar.Render                        PlayerBar.cs:128-129      marqueeDisabled
  ├─ TimeText.Render                         PlayerBar.cs:1150-1154    prefsEpoch → re-seed the label's state
  ├─ ImmersiveLyricsSurface.Render           :135-139 (backdrop + the reduced-motion veto) · :144-146 (secondary)
  ├─ LyricsView.Render                       :344 (LyricsPrefs.Epoch) · :345-346 (secondary) · :350-361
  │                                           (blur, incl. the Array.Fill(NaN) ramp reset) · :370-373
  │                                           (ramp re-arm) · :394-399 (LayoutDirty|VirtualRangeDirty)
  ├─ NowPlayingPanel's pinned hero           NowPlayingPanel.cs:531-532, :546-548
  ├─ HomePage / HomeSectionPage / RecentsPage / ArtistPage  :216-217 / :168-169 / :310-311 & :2479 / :63-64
  │    …and ArtistPage reads the flag a SECOND time inside Body (:293) — THAT is the read that feeds
  │    CoverPaletteLeaves.ArtistBlendWash (:295-296); the :63 read only re-renders the page
  ├─ LikedCoverArt's snapshot memo           LikedCoverArt.cs:81-100   carries the RESOLVED style, not the epoch
  └─ AppearancePrefs.TrackArtworkHidden — 14 call sites:
       ArtistPage.TopTracks.cs:39 · ArtistPopular.cs:116 · DetailTracks.cs:1209 · HomeModules.Artists.cs:282 ·
       LibraryPage.cs:869 · NowPlayingPanel.cs:45 · QueuePanel.cs:141 · StagePanes.cs:229 ·
       VideoRailPanel.cs:50 · RecentsPage.cs:1930 · RecentsPage.cs:1992 ·
       SearchPage.cs:660 · SearchPage.cs:922 · SearchPage.cs:980
     …plus ONE direct store read, inside the rows snapshot (DetailTracks.cs:709), which is correct: the snapshot
     is the recycled row's only channel (§1.4).

PRE-FRAME READS (before any component mounts — these cannot ride an epoch)
  ├─ AppLocaleBootstrap.Initialize(settings)   Program.cs:90   → App/AppLocale.cs:16-33
  ├─ ZoomLadder.Snap(stored zoom) → AppOptions.Zoom            Program.cs:550
  ├─ ZoomAutoPolicy.MigrateMode (once, guarded by ZoomModeBootstrapVersion)
  │    call site Program.cs:103 → policy App/ZoomAutoPolicy.cs:106-113
  ├─ the THEME, applied INLINE — Program.cs does NOT call WaveeTheme.ApplyThemeMode: it repeats the
  │    mode switch and calls Tok.Use(ResolvePalette(), kind) itself             Program.cs:402-404
  │    ⇒ the OS ACCENT RAMP is therefore NOT adopted before the first frame (ApplyThemeMode's
  │      `mode == 0` arm, WaveeTheme.cs:28-33, is skipped on the startup path); the first ramp
  │      adoption is the first SystemColorsChanged or an explicit Settings theme pick. Recorded,
  │      not "fixed": a 0.3 re-author must decide deliberately, not by accident.
  └─ SidebarPreferences' ctor seeds the ACTIVE design's pane triple            WaveeShell.cs:252-255
```

**Three structural facts a re-author must carry.** (a) There is exactly **one** `IAppSettings` instance and every
writer goes through it. (b) There is **no mirror of the store allowed to outlive a write** — `SettingsPage._density`
(`SettingsPage.Appearance.cs:33`) and `DetailShell._density` (`DetailShell.cs:109`) are the two that exist, and they
are exactly where the N12 defect lives. (c) The pre-frame reads above are the only values a 0.3 page may treat as
constant for its own lifetime.

### 1.3 The same tree in 0.3 terms

`Platform/Platform.cs` (SHELL) owns the store. It is the first thing `App.Main` boots
(`Platform.Boot()`, plan §3.5), which is correct and must stay that way: the theme, the zoom and the culture are
all read *before the window comes up*, and the sidebar design is read before the pane's first mount.

```csharp
// Platform/Platform.cs (SHELL) — the store and the typed keys
static partial class Platform
{
    public static IAppSettings Settings { get; private set; } = null!;   // HKCU\Software\Wavee\Wavee\Settings
    public static void Boot() { … Settings = AppDataSettings.ForUnpackaged("Wavee", "Wavee"); … }
}

// Platform/Design.cs (UI, pure over Platform.Settings) — ONE section owning EVERY preference signal
static partial class Design
{
    public static class Prefs
    {
        // one epoch per update DOMAIN, exactly as 0.2.9 had four — not one per key, and never one per surface
        public static readonly Signal<int> Appearance = new(0);   // rows, washes, marquee, liked cover
        public static readonly Signal<int> DetailLayout = new(0); // page layout, rail uniform, rail widths
        public static readonly Signal<int> Lyrics = new(0);
        public static readonly Signal<int> PlayerBar = new(0);
        public static readonly Signal<int> Npv = new(0);

        // every read is "subscribe to the epoch, then re-read the store" — the AppearancePrefs.cs:12-18 contract
        public static bool TrackArtworkHidden { get { _ = Appearance.Value; return Platform.Settings.Get(Keys.HideTrackArtwork); } }
        public static int  RowDensity        { get { _ = Appearance.Value; return Platform.Settings.Get(Keys.RowDensity); } }
        public static int  PageLayout        { get { _ = DetailLayout.Value; return Platform.Settings.Get(Keys.DetailPageLayout); } }
        …
        public static void Set<T>(SettingKey<T> key, T value, Signal<int> epoch)
        { Platform.Settings.Set(key, value); epoch.Value = epoch.Peek() + 1; }   // the ONE writer shape
    }
}
```

Three rules this shape enforces, each of which 0.2.9 violates somewhere:

- **One writer shape.** Every writer persists *then* bumps. 0.2.9's `SetDensity` forgets the bump (N12); making
  `Set` the only way to write makes that unrepresentable.
- **No mirror signals.** `SettingsPage` keeps `Signal<int> _density` (`SettingsPage.Appearance.cs:33`) seeded
  once at mount (`SettingsPage.cs:94`); `DetailShell` keeps a second one (`DetailShell.cs:109`) seeded per
  context. Two mirrors of one value is how they drift. In 0.3 the store is the value and the epoch is the edge.
- **Epochs are domain-scoped, not global.** One global epoch would re-render every lyrics row on a theme change.
  Five is the number 0.2.9 converged on; keep it.

### 1.4 The props-freeze trap, and what 0.3 must do about it

Component props freeze at mount (`..\fluent-gpu\docs\design\subsystems\component-props-contract.md`). A
preference changed while a page is mounted therefore **cannot** reach that page through a constructor argument.
0.2.9 solves it three ways, and 0.3 should use exactly these three and no fourth:

1. **Read the epoch signal in `Render`.** `DetailShell.cs:212` (`_ = AppearancePrefs.Epoch.Value;`) and `:463`
   (`int railEpoch = DetailHeroPrefs.Epoch.Value;`). This is the default answer. It works for a KeepAlive-parked
   page because the parked component's `Render` still runs when its subscriptions fire.
2. **Carry the value in a snapshot record the children subscribe to**, when the consumer is a *recycled bound
   row* rather than a component. `TrackRowsSnapshot` carries `MarqueeDisabled`, `TrackArtworkHidden`, `Classic`
   (`DetailTracks.cs:297, :344, :713-717`), with the contract stated at `:702-706`: *"every appearance setting
   read by a row must be carried in the snapshot — a flag read outside it would recompute here, compare equal,
   and silently never reach the rows."* This is load-bearing and must be ported verbatim in spirit.
3. **Remount through `Key`**, when the preference changes the element *shape* a frozen template cannot express:
   the virtualized list's key carries the density and the row style (`DetailTracks.cs:1090`), and the detail
   shell's track-list key carries the arm (`tracks:vertical:` / `tracks:standard:`, `DetailShell.cs:545`).
   Tier is deliberately **not** in the key — that was a remount that reset the scroll offset, and the fix was a
   memo the rows read instead (`DetailTracks.cs:1071-1080`).

   **The list key is not just the two preferences.** Verbatim at `DetailTracks.cs:1089-1090`:
   `"list:" + route.Name + (verticalHeader ? "vh:" : "") + "d" + density + (classic ? ":classic" : ":modern")
   + filterKey + ":r" + resetEpoch + (recsCapable ? ":rec" : "")`, where
   `filterKey = verticalHeader ? "" : ":q" + query + ":f" + filters.GetHashCode()`. So a **search query or a filter
   change also remounts the list** (in the two-column arm only — the vertical arm deliberately drops that half,
   because its hero and chrome are virtual prefix items and a remount would replay their entrance), a curated re-cut
   remounts it through `:r`, and the recommendations template rides `:rec` because `ItemsView`'s template and
   count-signal freeze at mount. A 0.3 re-author who ports only `"d<density>:classic"` loses four behaviours.

A fourth mechanism 0.2.9 uses once and 0.3 should keep: **read the preference inside a bound `Prop` thunk**, when
only a leaf's *value* changes. `PlayerBar.cs:1186-1199` reads `PlayerBarShowRemaining` inside the label's
`Prop.Of(...)` so a toggle lands on the next position tick with **no re-render at all**. FGRP002 applies: every
value the thunk depends on must be read *inside* it, because a replacement thunk is ignored after mount.

---

## 2. Wireframes

**Twenty-nine states.** W1–W16 are the detail page's own layout systems (Automatic's four modes, Hero's two
structural arms, the collapse, the uniform rail, the live toggle); W17–W29 are one per remaining preference.
Every width annotation is a **DIP viewport** width, not a window pixel width — zoom divides into the viewport
(W23), so a number pinned to a pixel width is wrong at any zoom but 100 %.

§2.0–§2.2 specify the Automatic-vs-Hero system that W1–W16 draw; §2.3 explains why podcasts are exempt; §2.4
covers localisation, which has no drawable state of its own.

### 2.0 Detail page layout: Automatic vs Hero — the two systems, and the one line that chooses between them

`DetailPageLayout` selects a page **SYSTEM**, not a hero variant. There is exactly one hero composition; the
setting decides whether the page is allowed to compose the **metadata rail** at all.

```csharp
// DetailShell.cs:509-511
if (_cfg.Content == DetailContent.Tracks
    && (settings?.Get(WaveeSettings.DetailPageLayout) ?? DetailVerticalLayout.PageAuto) == DetailVerticalLayout.PageHero)
    mode = Vertical;
```

`DetailVerticalLayout.PageAuto = 0`, `PageHero = 1` (`DetailVerticalLayout.cs:26-27`).

Three facts follow from that one line and must survive the rebuild:

1. **The override is applied at RENDER time only.** `_mode` keeps tracking the real measured width the whole time
   (`DetailShell.cs:485-487`), so flipping the setting back reverts instantly to whatever the width says — no
   re-measure, no remount (`:504-508`).
2. **Podcast shows are never overridden**, because the gate is `_cfg.Content == DetailContent.Tracks` and
   `DetailConfig.Show` sets `Content: DetailContent.Episodes` (`DetailConfig.cs:228`). §2.3 explains why that is
   right rather than an oversight.
3. **A page with `TwoColumn: false` never reaches this code at all** — it returns the bare track table at
   `DetailShell.cs:475-477`. All five shipped literals are `TwoColumn: true`; the single-column arm exists for
   the embedded library list and for a future config that opts out.

### 2.1 The mode ladder, with its thresholds and hysteresis

`DetailLayoutBreakpoints.NominalModeFor` (`:68-69`) measures the **page's own content column**, not the window:

| mode | name | nominal page width | rail width | right column MinWidth |
|---|---|---|---|---|
| 0 | Wide two-column | ≥ 820 | `cfg.RailWidth` = 280 album/show, 240 playlist/liked, **or the persisted width** | 300 |
| 1 | Mid two-column | 660 … 819 | 224 (`DetailShell.RailW`, `:192`) | 300 |
| 2 | Narrow two-column | 560 … 659 | 188 (`RailW`) | 300 |
| 3 | Vertical (the hero system) | < 560 (enter at < 540, leave at ≥ 580) | none | 0 (`ContentMinWidthForMode`, `:65-66`) |

Hysteresis (`DetailLayoutBreakpoints.ModeFor`, `:75-87`). **Lower mode number = wider**, so read the two arms
carefully — `nominal >= currentMode` is the *narrowing* arm and takes effect at once; the widening arm re-checks
the nominal answer at `w − 24` and only commits if *that* is still wider than the current mode:
- `ModeHysteresisDip = 24` (`:8`). **Narrowing is immediate**: mode 0 → mode 1 the instant the page drops below
  820. **Widening costs 24 DIP**: mode 1 → mode 0 only at **844**, because the test is
  `NominalModeFor(w − 24) == 0`. The same asymmetry governs 660 (widen at 684) and 560 (widen at 584).
  `DetailLayoutBreakpoints.cs:40-43` states the reason: the cost of the wrong guess in the widening direction is
  a column set the pane cannot hold.
- The vertical band is asymmetric and **not** 24: `VerticalEnterW = 540`, `VerticalExitW = 580` (`:59-60`).
  Entering vertical happens below 540; leaving it needs 580. Inside the band the page holds whatever it has.
- A nominal answer of Vertical arriving from a non-vertical current mode is coerced to 2, not 3 (`:82`) — you
  cannot skip into the hero system on a widening measurement.
- **First measure takes the nominal answer outright** (`initialized: false`, `:78`), because `prev` is then a
  construction default or a pre-measure seed, not a mode the user has seen. `TierFor` carries the identical rule
  (`:51`) for the identical reason — without it, whether the first real measure is honoured depends on which side
  of the seed it lands on.
- The **widening** arm coerces a dipped Vertical answer to 2 as well (`:85`), not only the nominal one (`:82`), so
  neither path can step into the hero system on a widening measurement.
- `w <= 0` returns `currentMode` unchanged (`:77`) and `NominalModeFor(0)` is **0**, not Vertical (`:69`) — a
  degenerate measurement must never be read as "this page is narrow".
- `DetailShell` runs a **self-heal** on top of the ladder: it never renders a mode WIDER than the last measured
  width supports (`if (fit > mode) mode = fit`, `DetailShell.cs:499-502`). Narrower than needed is fine; the next
  Measure widens it. This is a second fail-safe, not a duplicate of the hysteresis, and it must be ported.

The **pre-measure seed** deserves its own line, because getting it wrong is a visible remount flicker:
`EstimatePageWidthFromViewport(w) = max(0, w − 240)` (`DetailLayoutBreakpoints.cs:32-38`), where 240 is
`ShellResponsiveLayout.NavPaneNarrowW` — the sidebar's smallest plausible footprint. Deliberately the smallest:
an under-allowance can overshoot the real page width and seed the WIDE arm for one frame; an over-allowance only
seeds a narrower arm, which self-corrects at the next Measure because the hysteresis only ever widens on a
genuine subsequent measurement (`:24-31`). `DetailShell.cs:495-498` uses it, and hands the same number to the
vertical hero as `verticalHeroWSeed` (`:540`) so the two seeds can never disagree.

A second, independent ladder runs **inside** the track table on the right column's own width — the tier ladder,
`NominalTierFor` (`:10-11`): `≥860 → 0, ≥720 → 1, ≥560 → 2, ≥440 → 3, ≥340 → 4, ≥300 → 5, else 6`, with
`TierHysteresisDip = 24` (`:7`). Modes and tiers are **different measurements of different boxes** and must stay
that way in 0.3.

### 2.2 Where the rail's width actually comes from, per scope

```
resizableRail = cfg.RailResizable && mode == 0                  DetailRailPolicy.ResizableFor (:28)
railCollapsed = rail.Collapsed.Value && resizableRail           DetailShell.cs:628
railW         = resizableRail ? rail.Width.Value                DetailShell.cs:629
                              : RailW(mode, cfg)                DetailShell.cs:192  (224 / 188)
scope         = uniform ? RailScope.Uniform : cfg.RailScope     DetailRailPolicy.ScopeFor (:52)
```

| Detail kind | route prefix | `DetailConfig` | `RailScope` | mode-0 default rail | keys |
|---|---|---|---|---|---|
| Album / EP | `album:` | `Album` (`DetailConfig.cs:203`) | Album | 280 | `detail.rail.album.{width,collapsed}` |
| Single (≤ 2 tracks) | `album:` | `Single` = Album with `Selection: None` (`:210`) | Album | 280 | same as album |
| Compilation | `album:` | `Compilation` = Album with `ShowTrackArtist` (`:213`) | Album | 280 | same as album |
| Prerelease | `prerelease:` | resolves to the **Album** family (`DetailPage.cs:76`) | Album | 280 | same as album |
| Playlist | `pl:` | `Playlist` (`:196`) | Playlist | 240 | `detail.rail.playlist.*` |
| **Local files** | `local` | `Playlist` — route `local` maps to `(DetailKind.Playlist, "wavee:local:all")` (`DetailPage.cs:78`) | Playlist | 240 | **shares the playlist pair** |
| Liked Songs | `liked` | `Liked` (`:215`) | Liked | 240 | `detail.rail.liked.*` |
| Podcast show | `show:` | `Show` (`:224`) | Show | 280 | `detail.rail.show.*` |

Grip bounds and feel: `MinWidth 180`, `MaxWidth 480` (`DetailRailPolicy.cs:25`); `ForcePush 44`, `ReExpand 220`
(`DetailShell.cs:200`); grip hit strip `Splitter.StripW = 16` expanded, `GripStripCollapsedW = 20` collapsed
(`DetailShell.cs:200-206`, `fluent-gpu/.../Splitter.cs:22`). The floor is 180 rather than the old 220 because
every rail's own content survives it — the cover floors at `CoverEdge`, the title auto-fits to `MinSize 18`, the
CTA cluster wraps and the fact bentos are wrap-grow tiles (`DetailRailPolicy.cs:23-24`).

`CoverEdge(railW) = max(80, railW − 16 − 8)` (`DetailRail.cs:96`, `SidePadL = Spacing.L = 16`,
`SidePadR = Spacing.S = 8`). So: 280 → 256, 240 → 216, 224 → 200, 188 → 164, 180 → 156, 480 → 456.

One clarification the lane tables below need: **Title, Artist and Album are STAR tracks, not fixed lanes**
(`TitleStar` 1, `ArtistStar`, `AlbumStar` 0.75 : 1, `DetailTracks.cs:561-568`). Their 120 / 90 / 90 numbers are the
relief ladder's *floors* — the widths `MinWidthFor` charges them when it asks "does this lane set still fit"
(`DetailTrackTableRules.cs:173-178`) — never the width the grid gives them. Album being a 0.75 star rather than a
fixed 180 is load-bearing: the album name can never out-measure the song title, and the two share the squeeze
proportionally instead of the fixed lane holding its 180 while Title collapses toward zero
(`DetailTracks.cs:563-567`).

---

### W1 — Automatic · album/single/compilation/prerelease · mode 0 (page ≥ 820 DIP)

```
row: Grow 1 Shrink 1 Basis 0 MaxWidth 1600 (WaveeSize.PageMaxW) · Justify Center      DetailShell.cs:639-656
┌─ detail:two-column ──────────────────────────────────────────────────────────────────────────────────┐
│ DetailNoticeBar.For(...)          — 0 DIP tall while Notice == None                DetailShell.cs:669 │
├──────────────────────────┬────┬──────────────────────────────────────────────────────────────────────┤
│ rail 280 (persisted,     │grip│ right:tracks   Grow 1 · Shrink 1 · MinWidth 300                       │
│  180…480)                │ 16 │ ┌ chrome (a FIXED row, not sticky) ─────────────────────────────────┐ │
│ Fill Tok.FillLayerDefault│    │ │ Toolbar  44 = 32 pill + padY 5/5;  padX 6      DetailVertical…:229 │ │
│ Padding 16/24/8/24       │    │ │ [chips] [lens]                                                    │ │
│ Gap 14                   │    │ │ column header 36 Modern · 32 Classic  + 1-DIP rule  DTTR.cs:49    │ │
│ ┌──────────────────────┐ │    │ ├───────────────────────────────────────────────────────────────────┤ │
│ │ cover 256 × 256      │ │    │ │ #28│ TITLE ★                 │♥28│Plays 52│ ⏱52 │  ColGap 12      │ │
│ │  = 280 − 16 − 8      │ │    │ │  1 │ Track one               │ ♡ │ 11.8M  │ 3:41 │  PadX 16        │ │
│ │ Radii.Card 8         │ │    │ │  2 │ Track two               │ ♡ │   654K │ 3:12 │                 │ │
│ │ Elevation.Card       │ │    │ │ …  Modern rows 48 DIP, 8-DIP inset, corners 6, border 1 #Stroke…  │ │
│ │ saturation 1.18      │ │    │ │    virtualized; the whole model is demanded, never a window        │ │
│ └──────────────────────┘ │    │ └───────────────────────────────────────────────────────────────────┘ │
│ ALBUM · 2019   Eyebrow   │    │  ▼ (album only) AlbumTrailing scrolls BELOW the table in the same      │
│   12/16/600 +30 track    │    │     outer scroller: About the artist · Fans also like · More by ·      │
│ ┌ Title ────────────────┐│    │     Other versions                                                    │
│ │ 40/52 w600 if winH≥900││    │                                                                       │
│ │ 28/36 w600 otherwise  ││    │                                                                       │
│ │ MinSize 18, ≤3 lines  ││    │                                                                       │
│ └───────────────────────┘│    │                                                                       │
│ (◍◍◍+2) Artist  14/700   │    │   face pile: 28-DIP avatars, −10 overlap, cap 3 + "+N"  DetailRail:521│
│ [▶ Play]  (♡40)(↗40)(⋯40)│    │                                                                       │
│   Wrap: the FAB group    │    │                                                                       │
│   wraps as a unit        │    │                                                                       │
│ ┌ Release panel ────────┐│    │                                                                       │
│ │ Songs · Length · Out  ││    │                                                                       │
│ └───────────────────────┘│    │                                                                       │
│ blurb 12px ≤6 lines      │    │   descLines = winH < 760 ? 3 : 6                    DetailShell.cs:638│
│ (own ScrollView, hidden  │    │                                                                       │
│  bar — the LAST resort)  │    │                                                                       │
└──────────────────────────┴────┴───────────────────────────────────────────────────────────────────────┘
Behind everything, in the root ZStack: tintBinder (0×0) · DeferWatcher (0×0) · tonePlane   :681-687
tonePlane = flat art-derived ground, alpha 0.20 dark / 0.30 light                CoverPaletteLeaves.cs:139
```

Note two window-**height** dependencies that nothing else in the detail surface has: `titleSize` is
`winH >= 900 ? 40 : 28` and `titleLineHeight` is its paired rung from `TitleLineHeightFor`
(`DetailShell.cs:636-637`, `DetailVerticalLayout.cs:178-183`); `descLines` is `winH < 760 ? 3 : 6` (`:638`).
Both are keyed on the **window** height, known at mount and identical across navigation, so the title never jumps
on a nav and resizes smoothly (`:617-621`).

---

### W2 — Automatic · album · mode 1 (page 660 … 819 DIP)

```
┌──────────────────┬──────────────────────────────────────────────────────────────────────┐
│ rail 224 (fixed) │ right:tracks  MinWidth 300                                           │
│ NO GRIP          │ tier depends on the right column's own width, not the page's         │
│ cover 200 × 200  │ ┌───────────────────────────────────────────────────────────────────┐│
│  = 224 − 16 − 8  │ │ Toolbar 44 · header 36                                            ││
│ ALBUM · 2019     │ ├───────────────────────────────────────────────────────────────────┤│
│ Title (same rung)│ │ #│ TITLE                     │♥│Plays│ ⏱ │   ← Plays survives to   ││
│ face pile        │ │ … │                                          tier 2 (≥560)        ││
│ [▶][♡][↗][⋯]     │ └───────────────────────────────────────────────────────────────────┘│
│ blurb            │                                                                      │
└──────────────────┴──────────────────────────────────────────────────────────────────────┘
The persisted width and the collapsed flag are IGNORED here and return intact at mode 0.   N8
```

---

### W3 — Automatic · album · mode 2 (page 560 … 659 DIP)

```
┌────────────┬────────────────────────────────────────────────────────────────┐
│ rail 188   │ right:tracks  MinWidth 300                                     │
│ cover 164  │ ┌─────────────────────────────────────────────────────────────┐│
│ ALBUM·2019 │ │ Toolbar 44 (labels drop: toolbarLabeled = tier <= 1)        ││
│ Title      │ │ header 36                                                   ││
│ [▶][♡]…    │ ├─────────────────────────────────────────────────────────────┤│
│ (the CTA   │ │ #│ TITLE                        │♥│ ⏱ │                     ││
│  cluster   │ │ …  Plays has dropped (tier ≥ 3 or the relief ladder took it ││
│  wraps to  │ │    first — Plays is relief step 1, Tempo step 2) DTTR:154-163││
│  2 lines)  │ └─────────────────────────────────────────────────────────────┘│
└────────────┴────────────────────────────────────────────────────────────────┘
```

---

### W4 — Automatic · album · mode 3 Vertical, reached by NARROWING (page < 540 DIP)

This is the same composition the Hero setting forces, so it is specified once here and referenced from W10–W12.
Because an album has `HasTrailing: true`, it takes the **outer-scroll** arm (W12), not the virtualized-prefix one.

```
page 460 DIP · 460 ≥ RowFlowEnterW 424  ⇒ rowFlow TRUE  (art beside the identity)
page 380 DIP · 380 <  RowFlowEnterW 424 ⇒ rowFlow FALSE (art above it)   DetailVerticalLayout.RowFlow (:106)

 ── stacked flow, page 380 ──────────────────────      ── row flow, page 460 ──────────────────────
┌──────────────────────────────────────────────┐     ┌──────────────────────────────────────────────┐
│ pad 16 (NarrowHeroPad, page < 420)           │     │ pad 24 (HeroPad — row flow always takes 24)  │
│ ┌──────────────────────────────────────────┐ │     │ ┌────────────┐ gap 24 ┌────────────────────┐ │
│ │ artwork = clamp(380−32, 96, 280) = 280   │ │     │ │ art = round│        │ ALBUM · 2019  12/16│ │
│ │ Radii.Card 8 · Elevation.Card · sat 1.18 │ │     │ │  (clamp(   │        │ Title: TitleTypePlan│ │
│ └──────────────────────────────────────────┘ │     │ │  (460−48−24│        │  size solved against│ │
│  gap 16 (NarrowHeroGap)                      │     │  ×0.44,144,  │        │  a HEIGHT budget    │ │
│ ALBUM · 2019      Eyebrow 12/16/600 +30      │     │  240)) = 171 │        │  = art − chrome     │ │
│ ┌ Title ─────────────────────────────────┐   │     │ │ = 171 × 171│        │ ══ accent rule 2+2  │ │
│ │ TitleTypePlan: heightBudget = 0 in     │   │     │ └────────────┘        │ ◍ Artist  14        │ │
│ │ stacked flow, so the FLUID CAP rules:  │   │     │                       │ 12 songs · 41 min   │ │
│ │ slope 68/740 = 0.091892, intercept     │   │     │                       │ [▶ Play](⇄)(♡)(↗)(⋯)│ │
│ │  −5.0811 ⇒ cap(380) = 29.84 → snap 30  │   │     │                       │ blurb ≤3 lines 13px │ │
│ │ LineHeight = round(30 × 1.3301) = 40   │   │     │                       └────────────────────┘ │
│ └────────────────────────────────────────┘   │     │  identity MinHeight = art (171) — issue #78  │
│ ══ accent rule (2 DIP + 2 margin)            │     │  surplus spreads over every inter-block gap  │
│ ◍◍ Artist names            14/20             │     │  up to IdentityGapMax 12   (IdentityGapFor)  │
│ 12 songs · 41 min          16                │     └──────────────────────────────────────────────┘
│ [▶ Play] (⇄32)(♡32)(↗32)(⋯32)   gap 8        │      SatelliteSize = WaveeCta.IconButtonSize = 32
│ blurb ≤4 lines × 18                          │      (the two-column rail uses 40-DIP FABs instead)
│ pad-bottom HeroBottomPad 8                   │
├──────────────────────────────────────────────┤
│ toolbar band: 8 top + 44 + 4 bottom          │      ExpandedToolbarTopPad/BottomPad (:87-88)
├──────────────────────────────────────────────┤
│ column header 36 + 1                          │
├──────────────────────────────────────────────┤
│ rows, tier 4 (380 ≥ 340) → PadX 12, ColGap 12 │
└──────────────────────────────────────────────┘
```

The flow flip has its own hysteresis, mirroring the mode ladder's: `RowFlowEnterW = 424`,
`RowFlowLeaveW = 424 − 24 = 400` (`DetailVerticalLayout.cs:47-50`). `RowArtMin 144` binds exactly at
`RowFlowLeaveW` (`0.44 × (400 − 72) = 144`), so the artwork curve is **continuous across the flip** — there is no
width at which widening the window shrinks the cover (`:61-63`; pinned by
`DetailVerticalLayoutTests.ArtworkFor_IsMonotoneInWidth`).

---

### W5 — Automatic · playlist (and local files) · mode 0

```
┌──────────────────────────┬────┬────────────────────────────────────────────────────────┐
│ rail 240 (persisted)     │grip│ right:tracks                                           │
│ cover 216 × 216          │ 16 │ ┌ Toolbar 44 · [Search][Sort][Density][Filter][More]  ┐ │
│  = 240 − 16 − 8          │    │ │ column header 36                                   │ │
│ ┌ owner block ─────────┐ │    │ ├────────────────────────────────────────────────────┤ │
│ │ ◍ Owner   (or a      │ │    │ │ #│▣40│ TITLE            │ ALBUM ★ │ ♥ │Date│  ⏱    │ │
│ │   CollaboratorFace-  │ │    │ │  │   │ Song             │ artist  │   │ 88 │ 52    │ │
│ │   Pile when ≥2)      │ │    │ │ ShowArtThumb: true · ShowAlbumColumn: true · Show-  │ │
│ └──────────────────────┘ │    │ │ TrackArtist: true · ShowTempo: true · PlaysColumn-  │ │
│ Title 40/52 (or 28/36)   │    │ │ OptIn: true                       DetailConfig:196  │ │
│ 50 songs · 3 hr 12 min   │    │ └────────────────────────────────────────────────────┘ │
│  (a shimmer bar of the   │    │  + "Recommended songs" extender appended to the list   │
│   same shape while       │    │    on an owned/collaborative playlist (Recommendations)│
│   MembershipLoaded false)│    │                                                        │
│ [daylist flip countdown] │    │  ← only when ExpiresAtMs > 0 (PulseRowHeight 28)       │
│ [chart "N new · MMM d"]  │    │  ← only when ChartNewEntries > 0                       │
│ [▶ Play] (♡)(↗)(⋯)       │    │                                                        │
│ blurb ≤6 lines (rich)    │    │                                                        │
│ ┌ facts bento ─────────┐ │    │  LikedFacts.Has(m, BadgeStyle.OwnerRow) → the panel    │
│ │ this week · 12 weeks │ │    │  rides the RAIL on playlists AND Liked, because there  │
│ │ tempo curve · top    │ │    │  it costs the tracks no vertical space                 │
│ │ artists              │ │    │                            DetailRail.cs:259-260       │
│ └──────────────────────┘ │    │                                                        │
└──────────────────────────┴────┴────────────────────────────────────────────────────────┘
Local files is byte-for-byte this page: route "local" → (Playlist, "wavee:local:all")   DetailPage.cs:78
```

---

### W6 — Automatic · Liked Songs · mode 0

```
┌──────────────────────────┬────┬────────────────────────────────────────────────────────┐
│ rail 240 (its OWN scope; │grip│ right:tracks                                           │
│  never the album's)      │ 16 │ ┌ Toolbar 44 + the Liked content-filter chip rail ────┐ │
│ ┌ cover 216 ───────────┐ │    │ │ column header 36                                   │ │
│ │ the LikedCoverPicker │ │    │ ├────────────────────────────────────────────────────┤ │
│ │ — the dynamic        │ │    │ │ #│▣40│ TITLE           │ ALBUM ★ │ ♥ │Date│  ⏱     │ │
│ │ treatment + its      │ │    │ │ Badges: None — no eyebrow row at all                │ │
│ │ style chooser        │ │    │ │ Heart: HeartMode.None — no heart in the rail CTA    │ │
│ └──────────────────────┘ │    │ └────────────────────────────────────────────────────┘ │
│ Title "Liked Songs"      │    │                                                        │
│ 3 421 songs · 9 days     │    │                                                        │
│ [▶ Play] (↗)(⋯)          │    │                                                        │
│ ┌ facts bento ─────────┐ │    │                                                        │
│ └──────────────────────┘ │    │                                                        │
│ NOTE: this rail has NO   │    │  DetailRail.cs:276-286 — the Liked arm deliberately    │
│ Fill = FillLayerDefault  │    │  omits the layer fill; every other rail carries it     │
└──────────────────────────┴────┴────────────────────────────────────────────────────────┘
Page ground: LikedToneAnchor picks the TREATMENT's own lead tile, not the first gradeable
track cover, so the ground and the artwork above it agree.              DetailShell.cs:846-857
```

---

### W7 — Automatic · podcast show · mode 0

```
┌──────────────────────────┬────┬────────────────────────────────────────────────────────┐
│ rail 280 (RailScope.Show │grip│ right:eps — EpisodeList, NOT a track table              │
│  — its own keys since    │ 16 │ ┌────────────────────────────────────────────────────┐ │
│  the old "not playlist   │    │ │ episode toolbar (stays in the episode column at     │ │
│  ⇒ album" fallthrough    │    │ │ wide layouts)                                       │ │
│  was removed)            │    │ ├────────────────────────────────────────────────────┤ │
│ cover 256 × 256          │    │ │ ▣ Episode title                                    │ │
│ PODCAST · 2024           │    │ │   eyebrow · 42 min · Mar 3            [▶][+][⋯]     │ │
│ Title                    │    │ │ …                                                  │ │
│ Publisher · 312 episodes │    │ │ load-more gate: PagedThrough < TotalEpisodes        │ │
│ [▶ Play] (♡ Follow)(↗)(⋯)│    │ └────────────────────────────────────────────────────┘ │
│ blurb                    │    │                                                        │
└──────────────────────────┴────┴────────────────────────────────────────────────────────┘
Heart: HeartMode.Follow · Selection: None · HasTrailing: false          DetailConfig.cs:224-228
```

---

### W8 — Automatic · podcast show · mode 3 Vertical (< 540) — the one vertical arm that is NOT the hero

`verticalTracks = mode == Vertical && _cfg.Content == DetailContent.Tracks` (`DetailShell.cs:512`) is **false**
for a show, so the vertical arm composes `DetailRail.BuildHeader` above the episode list
(`DetailShell.cs:587`) rather than `DetailVerticalHero`.

```
┌──────────────────────────────────────────────────────┐
│ BuildHeader: Padding 16/16/16/8 · Gap 12   DetailRail.cs:435-440
│ ┌──────────┐ gap 16                                  │
│ │ cover 140│  PODCAST · 2024        ← a FIXED 140,   │
│ │  (const) │  Title 28/36 w600 ≤3 lines  not a       │
│ │ Radii 8  │  Publisher · 312 eps        function of │
│ │ Elev.Card│                             width       │
│ └──────────┘  AlignItems Center — a balanced pair,   │
│               never a wedge under the cover          │
│ [▶ Play] (♡40)(↗40)                  PlayRow         │
│ [release panel, if any]                              │
├──────────────────────────────────────────────────────┤
│ EpisodeList (its own scroller) — NO collapse, NO      │
│ sticky ContextBand: this header simply scrolls off    │
└──────────────────────────────────────────────────────┘
140 is exactly LikedCoverArt's treatment floor, which is why the header
cover still composes the dynamic Liked treatment.            DetailRail.cs:417
```

---

### W9 — Automatic · any two-column kind · mode 0 with the rail COLLAPSED

```
┌──────┬────┬──────────────────────────────────────────────────────────────────────┐
│ 96   │grip│ right:tracks — KEEPS ITS KEY, so the track list reconciles in place   │
│ strip│ 20 │ across the collapse instead of remounting (a remount would reset the  │
│      │    │ scroll offset and the hero morph on every collapse)  DetailShell:750-2│
│ ┌──┐ │    │                                                                      │
│ │80│ │    │  DetailRail.BuildCompact(m, 96, ExpandRail)        DetailRail.cs:304  │
│ │  │ │    │   pad 8 sides / 16 top / 16 bottom                                    │
│ └──┘ │    │   cover = max(48, 96 − 8 − 8) = 80, Radii.Card 8, Elevation.Card      │
│Title │    │   title 12/w600, Width 80, ≤2 lines, CharacterEllipsis                │
│ 12px │    │   spacer Grow 1                                                       │
│ ≤2ln │    │   chevron hit box 80 × 28, Radii.Control 4, HoverFill FillSubtle-     │
│      │    │     Secondary, glyph ChevronRight 14 Tok.TextSecondary                │
│  ⋮   │    │   the WHOLE strip is ToolTip.Wrap(strip, m.Title)                     │
│ [ › ]│    │   the cover is ALSO the expand gesture — no picker inside it, because │
└──────┴────┴─ a second hit target inside 48–80 DIP would be a coin flip  (:316-321)┘
Collapse is reached by pushing the grip past ForcePush (raw ≈ 136); re-opening needs a pull past 220,
comfortably above 136 so the rail cannot flicker shut/open at the seam.        DetailShell.cs:194-203
```

---

### W10 — Hero · playlist / Liked / local files (HasTrailing false) at a wide window

`DetailPageLayout = Hero` forces `mode = Vertical` at **every** width, so the composition is the vertical hero's —
but at 1 000 DIP rather than 380. This is the case the Hero setting exists for.

```
page width 1000 DIP · rowFlow true · pad 24 · gap 24
art = round(clamp((1000 − 48 − 24) × 0.44, 144, 240)) = 240  (the cap binds from 617 DIP up)
titleWrap  = min(1000, max(160, 1000 − 48 − 24 − 240)) = 688        TitleWidthFor  (:164)
contentW   = min(640,  max(160, 688))                   = 640        ContentWidthFor(:158)
heightBudget = art − chrome − (blocks−1)×IdentityGap                 TitleHeightBudgetFor (:421)
   chrome for a playlist with eyebrow+attribution+meta+actions, description EXCLUDED:
   16 (eyebrow) + 4 (rule) + 16 (attribution) + 16 (meta) + 40 (actions) = 92 ; blocks = 6
   ⇒ 240 − 92 − 5×4 = 128 DIP for the title
   one line: min(width-fit, 128 / 1.3301 = 96.23) → capped by FluidTitleCapFor(1000) = 86.81 → SnapTitleSize
   rounds to the 8-DIP grid (size ≥ 64) ⇒ 88. Note the snap may land ABOVE the fluid cap: SnapTitleSize clamps
   to [TitleSizeFloor 20, TitleSizeCap 96], not to the cap.                    DetailVerticalLayout.cs:327-333

┌ vitem:hero (virtual list item 0, persistent) ─────────────────────────────────────────────────┐
│ pad 24                                                                                        │
│ ┌───────────────┐ gap 24 ┌──────────────────────────────────────────────────────────────────┐ │
│ │ artwork       │        │ Private playlist            Eyebrow 12/16/600 +30 tracking       │ │
│ │ 240 × 240     │        │ ┌──────────────────────────────────────────────────────────────┐ │ │
│ │ Radii.Card 8  │        │ │  Deep Focus                 size 88 · LineHeight round(88 ×  │ │ │
│ │ Elevation.Card│        │ │                             1.3301) = 117 · ≤2 lines ·       │ │ │
│ │ sat 1.18      │        │ │                             MinSize from the floor packing   │ │ │
│ │ decode 1024   │        │ └──────────────────────────────────────────────────────────────┘ │ │
│ │ (art > 288)   │        │ ══════ Surfaces.AccentRule — 2 DIP + 2 top margin                │ │
│ │               │        │ ◍ Owner                                     16, MaxWidth 640    │ │
│ │ identity      │        │ 50 songs · 3 hr 12 min                      16                  │ │
│ │ MinHeight=240 │        │ [▶ Play] (⇄32)(♡32)(↗32)(⋯32)   gap 8, Wrap, 40-DIP row         │ │
│ │ so a short    │        │ blurb, expandable, 13px, ≤3 lines (rowFlow), MaxWidth 640       │ │
│ │ column never  │        │  ← the description is allowed to run PAST the cover's bottom;   │ │
│ │ opens a gap   │        │    it is excluded from the title's height budget on purpose     │ │
│ └───────────────┘        └──────────────────────────────────────────────────────────────────┘ │
│ pad-bottom 8 (HeroBottomPad)                                                                  │
├─ toolbar band: 8 + 44 + 4, padX = TrackRow.PadXFor(tier) = 16 ────────────────────────────────┤
└───────────────────────────────────────────────────────────────────────────────────────────────┘
┌ vitem:chrome (virtual list item 1, persistent, PINNED) ───────────────────────────────────────┐
│ [Liked content-filter chips] [lens header] column header 36 + 1-DIP rule                      │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
┌ rows: virtual slots 2 … 2+N-1, expandable track containers ───────────────────────────────────┐
│ tier is measured on the RIGHT column's width (here: the whole page) → tier 0, every lane on    │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
┌ FooterIndex(N) — the facts bento, ONE extra slot at the very bottom (Liked/playlist only) ────┐
└───────────────────────────────────────────────────────────────────────────────────────────────┘
PrefixCount = 2 · ItemCount = 2 + max(1, visible) + (hasFacts ? 1 : 0)      DetailVerticalLayout:602-615
```

The facts bento's **position is a mode difference, not a preference difference**: in the two-column arm it sits in
the rail (`rail:likedfacts`, `DetailRail.cs:259-260`); in the hero system it is the page's last slot, because
three stacked analytics cards inside the opening column would push the first track below the fold
(`DetailVerticalLayout.cs:593-599`, `DetailVerticalHero.cs:271-279`).

---

### W11 — Hero · playlist · narrow (page 360 DIP, stacked flow)

```
360 < RowFlowEnterW 424 ⇒ stacked · 360 < NarrowPadW 420 ⇒ pad 16, gap 16
art = round(clamp(360 − 32, 96, 280)) = 280
titleWrap = contentW = min(640, max(160, 360 − 32)) = 328
heightBudget = 0 (stacked) ⇒ the fluid cap alone: clamp(0.091892×360 − 5.0811, 28, 96) = 28.00 → snap 28
  (FluidTitleCapFor's line runs between (360, 28) and (1100, 96): slope 68/740, intercept −5.0811)  :301-314

┌──────────────────────────────────────────────┐
│ pad 16                                       │
│ ┌──────────────────────────────────────────┐ │
│ │ artwork 280 × 280  decode 512 (≤288)     │ │
│ └──────────────────────────────────────────┘ │
│  gap 16                                      │
│ Private playlist                             │
│ Deep Focus            28 / LH round(37) = 37 │
│ ══════ accent rule                           │
│ ◍ Owner                                      │
│ 50 songs · 3 hr 12 min                       │
│ [▶ Play] (⇄)(♡)(↗)(⋯)  — Wrap: the satellite │
│   group wraps as a unit below Play if needed │
│ blurb ≤4 lines × 18 (stacked gets one more   │
│   line than row flow)     DescriptionMaxLines│
│ pad-bottom 8                                 │
├─ toolbar 8 + 44 + 4, padX = PadXFor(tier 4)=12
├─ chrome: header 36 + 1                       │
├─ rows, tier 4 (360 ≥ 340): PadX 12, ColGap 12│
└──────────────────────────────────────────────┘
Identity: Width = contentW (328) explicitly, Grow 0 — AlignItems Stretch plus a DEFINITE width is
load-bearing: the action row is a wrapping flex row and cannot wrap against an intrinsic width.
                                                                   DetailVerticalHero.cs:292-302
```

---

### W12 — Hero · album / single / compilation / prerelease (HasTrailing TRUE)

The album family takes a **different structural arm** from W10, and the re-author must not merge them:

```csharp
// DetailTracks.cs:955-957 (the arm), :1081 (listGrow), :1092-1097 (rightBody)
if (_verticalHeader && !_cfg.HasTrailing) return VerticalList(...);   // hero + chrome are VIRTUAL PREFIX ITEMS
…
Element rightBody = _cfg.HasTrailing
    ? TrailingBody(listKeyed, VerticalHeroRoot(...), VerticalChromeRoot(chrome), verticalStickyInset)
    : listKeyed;                                                       // album: an OUTER scroller
float listGrow = _cfg.HasTrailing ? 0f : 1f;                           // the table does not own the viewport
```

```
┌ outer scroller (the page) ───────────────────────────────────────────────────────────┐
│ ┌ VerticalHeroRoot — the SAME DetailVerticalHero.Build, NOT a virtual slot ─────────┐ │
│ │ art 240 (row flow) │ ALBUM · 2019 / Title / rule / face pile / 12 songs · 41 min  │ │
│ │                    │ [▶ Play](⇄)(♡)(↗)(⋯) / blurb                                │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │
│ ┌ VerticalChromeRoot(chrome) — pinned ─────────────────────────────────────────────┐ │
│ │ column header 36 + 1                                                             │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │
│ ┌ the track table, Grow 0 — its natural height, no inner viewport ─────────────────┐ │
│ │ 1 … N rows at rowH                                                               │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │
│ ┌ AlbumTrailing (the reason this arm exists) ──────────────────────────────────────┐ │
│ │ About this release · About the artist · Fans also like · More by <artist> ▸      │ │
│ │ Other versions ▸                                                                 │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────────────┘
Liked/playlist have NO trailing sections, so they can own the viewport and virtualize hero+chrome.
An album cannot: its trailing shelves must scroll WITH the rows.
```

---

### W13 — Hero, scrolled: the header merge

This is the only "header merge on scroll" in the detail surface, and it exists **only in the hero system**. The
two-column arm's chrome is a fixed sibling row above a scroller (`DetailTracks.cs:1135`), so there is nothing to
merge.

```
scroll offset 0 ──────────────────────────► collapseDistance = max(1, expandedH − 56)
┌──────────────────────────┐   ┌──────────────────────────┐   ┌────────────────────────────────┐
│ expanded hero (H DIP)    │   │ …fading…                 │   │ ┌ ContextBand 56 ────────────┐ │
│ art + identity + toolbar │   │ ExpandedFadeStart =      │   │ │ Deep Focus  ·  Owner · 50  │ │
│                          │   │  collapseDistance − 96   │   │ │ songs, 3 hr 12 min         │ │
│                          │   │ CompactRevealStart =     │   │ │ [search] [actions]         │ │
│                          │   │  collapseDistance − 44   │   │ └────────────────────────────┘ │
├──────────────────────────┤   ├──────────────────────────┤   │ ┌ column header 36 + 1-DIP ──┐ │
│ chrome (pinned)          │   │ chrome (pinned)          │   │ └────────────────────────────┘ │
├──────────────────────────┤   ├──────────────────────────┤   ├────────────────────────────────┤
│ rows                     │   │ rows                     │   │ rows, CLIPPED at               │
└──────────────────────────┘   └──────────────────────────┘   │ StickyClipInset = 56 + 36 + 1  │
                                                              │ (+ the Liked filter rail's 48) │
                                                              └────────────────────────────────┘
The band paints NO fill and NO shadow: content is clipped at its lower edge and the page's own art-derived
tone plane shows through (ContextBandLayout.cs:33-40). Feather at the clip edge: StickyFadeBand 24 (:44).
Band geometry: Height 56 · Hairline 1 · ClusterGap 24 · PivotGap 16 · ActionGap 16 · PivotPadX 8 ·
ActionPadX 10 · underline 2 with a 4-DIP gap · TitleCap 280 · AvgCharW 7.6   ContextBandLayout.cs:27-76
compactLeft = TrackRow.PadXFor(tier) = 16 (tier ≤3) / 12 (4–5) / 8 (6)          DetailTracks.cs:1725
CompactIdentityHeight 56 == ContextBandLayout.Height 56, by construction        ContextBandLayout.cs:25-27
```

The pre-measure height of the expanded band is not a constant — it is the same pure sum the loading skeleton
reserves, so shimmer and live agree:
`HeroBandHeight = pad + (rowFlow ? max(art, identity) : art + gap + identity) + 8 + 8 + 44 + 4`
(`DetailVerticalLayout.cs:552-565`), with the title's own block height coming from a `TitleTypePlan` built for
`title: null` — a one-line title at the fluid cap, the tallest single-line reservation any real title at this
width could need (`:568-579`).

---

### W14 — Automatic · prerelease album · mode 0

```
┌──────────────────────────┬────┬───────────────────────────────────────────┐
│ rail 280 (Album scope)   │grip│ right:tracks — rows carry a SHORT DATE in  │
│ cover 256                │    │ the duration lane instead of a length      │
│ ALBUM · 2027             │    │ ("4 Sep", or "4 Sep 2027" across a year    │
│ Title                    │    │ boundary)                DetailFormat:239  │
│ face pile                │    │                                            │
│ [▶ Play] (♡ → the PRE-   │    │                                            │
│   RELEASE entity, not    │    │  (m.PreReleaseUri ?? m.ContextUri) — the    │
│   the album)(↗)(⋯)       │    │  heart's TARGET is swapped, never a second  │
│ ┌ countdown card ──────┐ │    │  heart; the key is the target because       │
│ │ PreReleaseCountdown  │ │    │  SaveButton's uri freezes at mount          │
│ │ keyed on ctx + ticks │ │    │                          DetailRail.cs:232  │
│ └──────────────────────┘ │    │                                            │
│ Release panel · blurb    │    │                                            │
└──────────────────────────┴────┴────────────────────────────────────────────┘
The card's gate is UpcomingAt, not IsPreRelease — three wire shapes make an album upcoming and only one
sets the flag.                                                        DetailRail.cs:443-460
Hero mode behaves exactly as W12 (prerelease resolves to the Album family, HasTrailing true).
```

---

### W15 — "Keep left-rail same size" OFF vs ON

```
        DetailRailUniform = false (default)                DetailRailUniform = true
  album ────────────────────────────────────────     album ───────────────────────────────────────
  ┌──── 280 ────┬──┬──────────────────────┐          ┌── 240 ──┬──┬───────────────────────────┐
  │ album rail  │  │ tracks               │          │ rail    │  │ tracks                    │
  └─────────────┴──┴──────────────────────┘          └─────────┴──┴───────────────────────────┘
  playlist ─────────────────────────────────────     playlist ────────────────────────────────────
  ┌── 240 ──┬──┬───────────────────────────┐         ┌── 240 ──┬──┬───────────────────────────┐
  │ pl rail │  │ tracks                    │         │ rail    │  │ tracks                    │
  └─────────┴──┴───────────────────────────┘         └─────────┴──┴───────────────────────────┘
  Liked ────────────────────────────────────────     Liked ───────────────────────────────────────
  ┌── 240 ──┬──┬───────────────────────────┐         ┌── 240 ──┬──┬───────────────────────────┐
  show ─────────────────────────────────────────     show ────────────────────────────────────────
  ┌──── 280 ────┬──┬──────────────────────┐          ┌── 240 ──┬──┬───────────────────────────┐
  four independent (width, collapsed) pairs          ONE shared pair: detail.rail.uniform.{width,collapsed},
  drag one, the others do not move                   default 240 — dragging ANY page moves ALL of them

  Turning it back OFF restores 280/240/240/280 (or whatever each scope last remembered): the uniform
  pair is a SEPARATE key pair and the four were never written.                AppSettings.cs:167-171
  "Clear all remembered sizes" resets the FOUR, never the uniform pair, and is only offered while
  uniform mode is OFF.                              SettingsPage.Appearance.cs:277-290, :501-515
  Its enable gate is DetailRailPolicy.HasCustomizedRailPrefs (:58-66) — a destructive-looking button
  that does nothing is worse than no button.
```

---

### W16 — What a live toggle does to a mounted or KeepAlive-parked page

`ContentHost` keeps `MaxEntries: 3` — the live page plus a two-deep back stack
(`Features/Shell/ContentHost.cs:96-108`). All three see the change.

```
Settings ▸ Track page layout: Automatic → Hero
  │
  ├─ settings.Set(DetailPageLayout, 1)                          SettingsPage.Appearance.cs:259
  └─ DetailHeroPrefs.Bump()                                     SettingsPage.Appearance.cs:260
        │
        ├─ every mounted DetailShell's Render reads DetailHeroPrefs.Epoch.Value   DetailShell.cs:463
        │     → the SAME subscription serves BOTH the `mode = Vertical` override (:509-511)
        │       and the rail re-sync effect (:464-472). One epoch, no second mechanism.
        │
        ├─ UseEffect(railEpoch) re-reads DetailRailUniform and ResyncRail()s all four scopes,
        │     clamping exactly as SeedRail did at mount                          DetailShell.cs:464-472
        │
        └─ the render then swaps arms. The track list REMOUNTS, because its Key carries the arm:
              (verticalTracks ? "tracks:vertical:" : "tracks:standard:") + route.Name   DetailShell.cs:545
           — deliberate: a route is a new scroll/hero identity, and reconciling a hero-system list
           against a standard one painted the previous page's collapsed-header signals for a frame.

Live toggles that do NOT remount anything: Track list style, Always hide track artwork, Marquee,
Color washes, Liked cover style (AppearancePrefs.Epoch → the rows' snapshot memo patches in place);
Lyrics second line / backdrop / blur (LyricsPrefs.Epoch); Show remaining time (PlayerBarPrefs.Epoch);
Hero / Player style (NpvPlayerPrefs.Epoch); Theme (Tok.Use + a 250 ms cross-fade).
Live toggles that DO remount: Track page layout (above), Row density and Track list style remount the
virtualized LIST ONLY (its Key carries "d<density>" and ":classic"/":modern", DetailTracks.cs:1090).
```

---

### 2.3 Why podcasts stay Automatic

Three reasons, all in the code:

1. **The right column is not a track table.** `DetailConfig.Show` sets `Content: DetailContent.Episodes`
   (`DetailConfig.cs:228`), so the vertical arm renders `EpisodeList`, and the hero's whole apparatus — the
   virtualized prefix slots, the collapse binds, the pinned column header, the facts footer — is built for
   `TrackList`. `verticalTracks` is false for a show at every width (`DetailShell.cs:512`).
2. **There is no column header to pin**, so there is nothing for the ContextBand to merge with. The show's
   narrow arm uses `DetailRail.BuildHeader` (a fixed 140-DIP cover header that simply scrolls off,
   `DetailShell.cs:587`), which needs no collapse ladder at all.
3. **A show's identity is not an artwork-forward object.** The hero's title type plan spends the cover's unclaimed
   height on the title; a show's rail is publisher + episode count + a Follow verb, and the trailing "release
   panel" the plan's chrome flags reason about (`HeroHasEyebrow/Attribution/Meta/Description/Pulse`,
   `DetailTracks.cs:1659-1671`) has no show analogue.

The Settings row says "**Track** page layout" for exactly this reason (`assets/loc/en-US.json`
`settings.appearance.pageLayout`), and the key comment states it: "podcasts keep the automatic layout"
(`AppSettings.cs:91-93`).

---

### W17 — Row density × track list style × hidden artwork @ a 1200-DIP page (right column tier 0)

The three compose into one grid. The row height ladder is a single pure function:

```csharp
// DetailTrackTableRules.cs:45-47
internal static float RowHeightFor(int density, bool classic) => classic
    ? density switch { 0 => 36f, 2 => 44f, 3 => 48f, _ => 40f }
    : density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => 48f };
```

| | Compact 0 | Default 1 | Cozy 2 | Comfortable 3 |
|---|---|---|---|---|
| **Modern** row | 40 | **48** | 56 | 64 |
| Modern art | 32 | **32** | 40 | 48 |
| **Classic** row | 36 | 40 | 44 | 48 |
| Classic art | — (Classic has no thumb lane at all) | — | — | — |

`ArtSizeFor` (`DetailTrackTableRules.cs:54-56`) publishes 32/32/40/48 Modern and 32/32/32/40 Classic; the Classic
values are dead in the table (the Thumb lane is gated `!classic && showArtThumb && !artworkHidden && tier < 5`,
`:38`) and survive only for the Settings preview cards. Header: 36 Modern / 32 Classic (`ClassicHeaderHeight`,
`:27`, `:49`). The documented invariant is `row − art ≥ 16` on the Modern ladder (`:51-53`). Artwork size is
**not** a function of `HideTrackArtwork` — hiding removes the lane, it does not resize it.

**Classic's Artist lane is itself width-gated, and it does not disappear — it FOLDS.**
`IdentityColumns` (`:33-41`) returns `Artist: classic && showTrackArtist && tier < ClassicArtistFoldTier` where
`ClassicArtistFoldTier = 4` (`:25`), i.e. the separate Artist lane survives only to a right column of ≥ 440 DIP;
below that `ArtistInTitle` goes true (`:40`) and the artist rides the Title metadata subline exactly as Modern
always does. So "Classic at a narrow width" is **not** "Classic minus a lane" — it is the Modern identity grammar
with Classic's chrome. Classic also folds VIDEO into the Title line as an inline film glyph rather than keeping a
media lane, and drops even that at tier ≥ `ClassicInlineVideoDropTier` 4 (`ShowClassicInlineVideo`, `:26`, `:73`).

```
Modern · Default (48) · artwork ON                      Modern · Default (48) · artwork OFF
┌────────────────────────────────────────────┐          ┌────────────────────────────────────────────┐
│ ← 8 inset (RowInset) ─────────────────────┐│          │                                            │
│┌ 6-corner pill, 1-DIP StrokeCardDefault ──┐│          │┌──────────────────────────────────────────┐│
││ 1 │▣32│ Song title          14/20/600    ││          ││ 1 │ Song title            14/20/600      ││
││   │   │ Artist · Album      12/16        ││          ││   │ Artist · Album        12/16          ││
│└──────────────────────────────────────────┘│          │└──────────────────────────────────────────┘│
│  ↑28  ↑32  ↑Star(1) floor 120  ColGap 12   │          │  ↑28  ↑Star(1) — the thumb's 32 + one gap  │
│  PadX 16 (tier ≤3)                         │          │       (12) = 44 DIP returns to the title   │
└────────────────────────────────────────────┘          └────────────────────────────────────────────┘
zebra: DisplayIndex % 2 != 0 → WaveeColors.RowZebra      art radius Radii.Control 4, decode (int)(art×2)
                             DetailTracks.cs:3512-3519                            TrackRow.cs:246-247

Classic · Default (40)                                  Classic · Comfortable (48)
┌──────────────────────────────────────────────┐        ┌──────────────────────────────────────────────┐
│ 1 │ Song title           14/20/600 │ Artist ★ │        │ 1 │ Song title                    │ Artist ★ │
├──────────────────────────────────────────────┤        │   │  (same two-lane grammar, 8 DIP taller)   │
│ 2 │ Second title                   │ Artist   │        ├──────────────────────────────────────────────┤
└──────────────────────────────────────────────┘        └──────────────────────────────────────────────┘
 no inset (Margin 0) · corners 0 · no border · no zebra · a 1-DIP Tok.StrokeDividerDefault hairline at
 the row's bottom, inset by PadXFor(tier) each side          DetailTracks.cs:3491-3533, :3590-3597
 factual cells (date/plays/tempo/duration) are 14/20 in Classic and 12/16 in Modern   TrackRow.cs:723-731
 column headers: Classic 11px/600 + 30 tracking + .ToUpper(CurrentUICulture); Modern 12px/600, no tracking
                                                        DetailTracks.cs:2468-2469, :2474-2475, :2483-2484
 no accent selection pill in Classic (Opacity gated on !classic)                     DetailTracks.cs:3584-3588
 the number cell's rest state is a 13px Icons.Volume glyph, not the animated equalizer  TrackRow.cs:1074-1083
 the More "…" button is square (Radii.None), scale-less, rest opacity 0               TrackRow.cs:971-989
```

One documented inconsistency to fix rather than port: the relief ladder measures the thumb lane at a hard 32
(`TrackLane.Thumb = 32f`, `DetailTrackTableRules.cs:243`) while the grid builds `art` (40 at Cozy, 48 at
Comfortable, `DetailTracks.cs:560`), so `MinWidthFor` under-predicts the squeeze by 8/16 DIP at those densities.

The Settings preview cards are the same numbers × `PreviewScale = 0.25` (`DetailTrackTableRules.cs:31`,
`Design/WaveePicker.cs:108-109`): preview row heights 10/12/14/16 and art edges 8/8/10/12.

### W18 — The Plays column, OFF vs ON @ tier 0 (right column ≥ 860)

```
OFF (default) — playlist / Liked at tier 0        ON — the same page, same width
┌───────────────────────────────────────┐        ┌───────────────────────────────────────┐
│ #│▣│ TITLE        │ ALBUM │♥│Date│ ⏱  │        │ #│▣│ TITLE   │ ALBUM │♥│Date│Plays│ ⏱ │
│28│ │ Star(1) ≥120 │Star .75│28│ 88│ 52│        │28│ │ Star(1) │Star.75│28│ 88│  52│ 52│
└───────────────────────────────────────┘        └───────────────────────────────────────┘
lane 52, header "Plays" (detail.column.plays), FlexJustify.End, SORTABLE (SortColumn.Plays)
value format 1.85B / 11.8M / 654.8K; an em dash when the count is 0 or the track is not out yet
gate: (ShowPlays || (PlaysColumnOptIn && PlaysColumn)) && tier < 3  →  the right column must be ≥ 560 DIP
album surfaces carry ShowPlays: true and are UNAFFECTED by the setting
                    DetailTracks.cs:493,506,571,2416 · TrackRow.cs:153-157,296-298 · DetailConfig.cs:174-179
```

### W19 — The Tempo (BPM · Key) column, ON @ tier ≤ 3 (right column ≥ 440)

```
ON — playlist / Liked, tier ≤ 3 (right column ≥ 440 DIP)
┌──────────────────────────────────────────────────────┐
│ #│▣│ TITLE            │ ALBUM │♥│Date│ BPM · Key │ ⏱ │
│  │ │                  │       │  │    │    80     │52 │
│  │ │ Fade Into You     │       │  │    │ ◧ 101.5 · 8A │
└──────────────────────────────────────────────────────┘
lane 80 · header "BPM · Key" (detail.column.tempo) · FlexJustify.End · NOT sortable (PlainHeader)
cell = a 6-DIP Camelot swatch + the tempo (one decimal when meaningful, invariant culture) + "·" + one key token
swatch: 6 × 6, `Corners` 1.5 (deliberately BELOW both ramps' smallest rungs — it is a colour SWATCH, not a
  surface), `Opacity` 0.85, `AlignSelf Center`, fill `WaveePalette.DataDotInk(argb, Tok.Theme)` — a PASSTHROUGH in
  dark and a hue-dependent DARKENING in light, because the wire hues were authored for a dark row
  gaps between swatch / bpm / "·" / key are `Spacing.XS`; the middot is `Tok.TextTertiary`   TrackRow.cs:584-610
empty box until extended-metadata kind 222 lands — the row expander shows tempo/key regardless, so the setting
hides a COLUMN, never the data                 DetailTracks.cs:574,2417-2419 · TrackRow.cs:299-300,578,584-607
gate: ShowTempo && TempoColumn && Tier <= 3 — one tier more generous than Plays
relief order (cheapest first): Plays(1) → Tempo(2) → Added by(3) → Date(4) → Album(5) → Artist(6) → Thumb(7) → ♥(8)
so at a squeezed width Plays leaves before Tempo whatever the tiers said     DetailTrackTableRules.cs:154-163
```

### W20 — Colour washes, ON vs OFF — the whole window without its art-derived tone @ 1600 × 900

```
ON (default)                                          OFF
┌───────────────────────────────────────────┐        ┌───────────────────────────────────────────┐
│ title row   ← paints nothing: shows the    │        │ title row   ← shows the NEUTRAL material  │
│               shell MATERIAL (tinted)      │        │                                           │
├───────┬───────────────────────────┬───────┤        ├───────┬───────────────────────────┬───────┤
│sidebar│ content: FileArea over    │ right │        │sidebar│ content: FileArea over    │ right │
│ ←also │  the tinted material +    │ rail  │        │       │  the neutral material,    │ rail  │
│ shows │  the detail TONE PLANE    │       │        │       │  and the tone plane       │       │
│ the   │  (flat, α 0.20 dark /     │       │        │       │  paints NOTHING at all    │       │
│ tint  │   0.30 light)             │       │        │       │                           │       │
├───────┴───────────────────────────┴───────┤        ├───────┴───────────────────────────┴───────┤
│ player dock ← also the material            │        │ player dock                               │
└───────────────────────────────────────────┘        └───────────────────────────────────────────┘
ON, flat arm (detail / artist / liked): Fill = tint. The 250 ms BrushTransitionMs belongs to the SHELL
  layer (ShellMaterialLayer.cs:98); the page tone plane's own 250 ms ramp is CoverPaletteLeaves.cs:115 —
  and it does NOT run when washes go off, because the node then has no Fill at all (§4.1, §5.2 row 15)
  light: WaveePalette.Lift(scheme.TextBase) @ A 0.05 · dark: WaveePalette.TintedDark(scheme) @ A 0.14
                                                                     CoverPaletteLeaves.cs:242-246
ON, Home's three radial washes: two stops (0, c@α) → (FadeOffset, c@0); Hero center (0.06,0.00) r(0.74,0.92)
  fade 0.62 · Weekly (0.92,0.10) r(0.58,0.78) fade 0.64 · Mix (0.58,1.00) r(0.90,0.70) fade 0.66;
  α 0.055 light / 0.10 dark (hero), 0.05 / 0.085 (shelves); clipped to stop at the dock line
  (Margin bottom = PlayerDock.Reserve 72)      ShellWashGeometry.cs:24-37 · ShellMaterialLayer.cs:72-77
OFF: the tint layer STAYS MOUNTED and fills WaveeColors.ShellGround @ A 0.03 — never ColorF.Transparent
  (#EDEDED@3% light, #202020@3% dark)                                    ShellMaterialLayer.cs:89
OFF, detail tone plane: `new BoxEl { Grow = 1f, HitTestVisible = false }` — nothing    CoverPaletteLeaves.cs:93-94
OFF, artist blend wash: `if (p.Disabled) return new BoxEl();`                          CoverPaletteLeaves.cs:178
OFF, Home / Home-section / Recents: wash = null; only the neutral rect above remains
The ACCENT is a different axis and is NOT disabled: Play pills, hearts and row chrome keep their
art-derived accent (WaveePalette.ChromeAccent / ChromeFromPayload / Tok.AccentDefault)  DetailShell.cs:304-310
```

### W21 — Marquee, ON vs OFF @ the player bar's Medium tier (≥ 760) and the now-playing row

```
ON (default) — player bar now-playing title            OFF
┌───────────────────────────────────────┐             ┌───────────────────────────────────────┐
│ ▣48 │ Bohemian Rhapsody (Remast…      │             │ ▣48 │ Bohemian Rhapsody (Rema…        │
│     │  ← scrolls on HOVER only:       │             │     │  ← TextTrim.CharacterEllipsis,  │
│     │    PingPong, Speed 18 dip/s,    │             │     │    Wrap NoWrap, MaxLines 1,     │
│     │    CycleMs 10000, EndPause 2500 │             │     │    MinWidth 0 — and NO edge     │
│     │    edge fade 24, clamped to 30% │             │     │    fade (the fade lives on      │
│     │    of the viewport              │             │     │    MarqueeHost)                 │
└───────────────────────────────────────┘             └───────────────────────────────────────┘
                 PlayerBar.cs:92-93, :277-291              PlayerBar.cs:266-276

ON — the NOW-PLAYING track row only (every other row is already a plain ellipsis TextEl, for cost)
   Marquee defaults: Speed 9 dip/s, Gap 48, Loop, Trigger Always, StartDelay 350, EndPause 900,
   FadeBand 24, FadeStrength 1        fluent-gpu/.../Marquee.cs:35-48 · DetailTracks.cs:2687-2693
OFF — the now-playing row behaves exactly like every other row                DetailTracks.cs:2701-2706
Motion.ReducedMotion holds every marquee still regardless of this setting     Marquee.cs:172
```

### W22 — Theme, Light vs Dark — every token that moves @ any width

```
                       LIGHT                                DARK
shell ground           #EDEDED  (FillSolidBase #F3F3F3      #202020  (FillSolidBase, Tinted 0.125)
                        − LightGroundDrop)
content surface        #80FFFFFF (FileArea, translucent)    #4C3A3A3A
content opaque         #F9F9F9  (FillSolidTertiary)         #282828  (+ DarkContentLift)
card fill / secondary  #B3FFFFFF / #80F6F6F6                #0DFFFFFF / #08FFFFFF
text primary           #E4000000                            #FFFFFFFF
text secondary         #9E000000                            #C5FFFFFF
accent (OS ramp wins   #005FB8                              #60CDFF
  in System mode)
divider / card stroke  #0F000000 / #0F000000                #15FFFFFF / #19000000
chrome plate           #B3FFFFFF (LayerOnMicaBaseAlt)       #733A3A3A (DarkPlate)
scrim                  #4D000000                            #4D000000
Elevation.Card         Blur 4  OffsetY 2  #0000001A         Blur 8  OffsetY 2  #00000033
   PaletteBuilder.cs:286-444 · WaveeTokens.cs:140-188 · fluent-gpu/.../Elevation.cs:18-21

The separation model carries NO shadow on the content rung: a translucent fill + a 1px Tok.StrokeCardDefault
on LEFT+TOP edges only, one rounded corner.                                    WaveeTokens.cs:96-99
System mode follows the OS live: FluentApp.SystemColorsChanged → re-read SystemUsesLightTheme() → Tok.Use →
a 250 ms cross-fade, armed only when Tok.Epoch actually advanced.               WaveeApp.cs:43-63
Light/Dark PIN and stop following (WaveeApp.cs:50). The OS accent ramp is adopted only in System mode
(WaveeTheme.cs:28-33). Applied before the first frame at Program.cs:402-404 — no startup flash.
One palette, two arms: ResolvePalette() => Tok.NeutralPalette (WaveeTheme.cs:15); the picker is gone.
```

### W23 — Zoom: the ladder, Auto, and a zoomed window crossing a layout tier @ Settings ▸ Appearance

```
ladder (12 rungs): 0.50 0.67 0.75 0.80 0.90 1.00 1.10 1.25 1.50 1.75 2.00 2.50
                                                        fluent-gpu/.../ZoomLadder.cs:17-18
Auto plateaus (the plateau-clean subset): 0.75 1.00 1.25 1.50 1.75 2.00     ZoomAutoPolicy.cs:59
Suggest(baseW, baseH, mode):
   ratio = min(baseW / 1600, baseH / 900)          DesignW/DesignH  ZoomAutoPolicy.cs:42-45
   lo    = mode == Dense ? 0.75 : 1.00                              :53, :75
   return SnapPlateauDown(clamp(ratio, lo, 2.0))   Ceiling 2.0      :49, :71-89
SnapPlateauDown snaps DOWN, never to the nearest: 1.20 → 100 %, 1.55 → 150 %.

┌ the picker ───────────────────────────────────────────────┐
│ Zoom                                    [ Auto (150%)  ▾ ]│  ← index 0's label carries the LIVE
│ Scale the whole app, like Ctrl + and    [ 50% ]           │    resolved percentage
│ Ctrl − in a browser                     [ 67% ] …         │    ComboBox width 160
└───────────────────────────────────────────────────────────┘    SettingsPage.Appearance.cs:343-345
```

**Does a zoomed window cross layout tiers? Yes — downward, and only downward.**
`viewportDip = clientPx / (osDpi × zoom)` (`fluent-gpu/.../Win32Platform.cs:817-818, 827-834`;
`AppHost.cs:5249-5254`), and that value is what every responsive ladder in this chapter reads
(`Viewport.Size`). Zooming **in** shrinks the DIP viewport, so a page can demote: sidebar Wide → Mid, detail
mode 0 → 1 → 2 → 3, tier 0 → 1 → …, the hero's `winH >= 900` TitleLarge rung → Title, the player bar's
`ShowTimesRemaining` tier. Zooming **out** can promote. `Context.cs:27-31` states the rule: `Scale` already
contains the zoom, so a preference read of `Viewport.Zoom` is display-only and must never convert coordinates.
`ZoomAutoPolicy.Suggest` takes `min()` over both axes precisely so an auto pick cannot demote the height
ladder while satisfying the width one (`ZoomAutoPolicy.cs:25-28, :74`).

Two facts worth carrying forward as-is: **Auto is not applied on the very first frame** — `Program.cs:550`
seeds `AppOptions.Zoom` from the stored value only, and `Suggest` is first consulted after the shell mounts and
the 500 ms debounce settles (`WaveeShell.cs:598-632`). And **`ZoomAutoMode.Dense` has no UI writer anywhere**
(verified by grep across `src/apps/Wavee`): it is reachable only by hand-editing `appearance.zoom.mode` to 2.
0.3 should either give Dense a picker item or delete the mode.

### 2.4 Localisation and long strings — no wireframe, because there is no RTL or long-string layout arm to draw

Bundled tables: `assets/loc/{en-US,ko-KR,nl}.json`. The picker offers System / English (US) / Nederlands /
한국어 with the last two **shown but disabled** (`SettingsPage.General.cs:32-43`), because their tables are
incomplete. Resolution: `AppLocaleBootstrap.Initialize` at `Program.cs:90` → `App/AppLocale.cs:16-33`
(`"system"` → `UseOsCulture()`, otherwise `TrySetCulture(selected)` with an `en-US` fallback). The same value
drives the Spotify metadata language (`AppLocale.cs:35-44`, 2-letter primary subtag, else `"en"`).

**There is no RTL story and no long-string layout adaptation.** A repo-wide grep for
`FlowDirection|RightToLeft|RTL|BiDi` over `src/apps/Wavee` returns no layout code — the engine's shaper resolves
BiDi *glyph runs* (`fluent-gpu/.../Seams/Text/Text.cs:44`) but nothing mirrors layout. Overflow is handled purely
per element, by `TextTrim.CharacterEllipsis` or by a `Marquee`. The places a long localised string is most
visible:

```
· ContextBand estimates label widths at AvgCharW 7.6 DIP/char, deliberately generous, and NOTHING in the band
  drops at a breakpoint: the title never drops, the actions never drop, the pivot lane scrolls behind an alpha
  edge fade                                                            ContextBandLayout.cs:10-13, :72-84
· Column headers .ToUpper(CurrentUICulture) in Classic only — and WaveeType.Eyebrow explicitly REFUSES to
  caps-transform a localised string (Turkish dotted i, German ß, a user's own display name)  WaveeType.cs:43-47
· Settings pickers with a fixed width: Zoom ComboBox 160, NPV style 180, Language 260, GPU 300 — a long
  translation ellipsizes inside them rather than widening the row
· A picker in an expander HEADER starves the header text track to zero and paints over it — the documented
  Sidebar-design bug; every picker lives in the expander BODY  SettingsPage.Appearance.cs:16-21
UNVERIFIED: the nl / ko-KR key coverage. Neither table is a shippable pick today, so no 0.3 chapter can
show a "long string" state derived from them; use a synthetic long string instead.
```

### W24 — The nine Liked cover styles @ 304 (the authoring canvas)

```
FromSetting(int) → the value in [0,8], anything else → Stock          LikedCoverRules.cs:75-78
Effective(requested, distinctTiles) → Stock when distinctTiles < MinTiles(requested)  :155-156
MinTiles: Stock 0 · Lens 4 · Wall 8 · Rainbow 8 · Marquee 6 · Feature 4 · Mosaic 4 · Tone 1 · Stack 3  :139-150
Tiles: newest-first, deduped by BOTH album uri and url, blanks skipped, capped at MaxTiles 16   :100-131
Authoring canvas DesignSize 304 · chrome floor 180 · treatment floor BadgeMinSize 140
decode buckets: wall 64 · grid 128 · hero 256                        LikedCoverTreatments.cs:40-54

0 Stock      the bundled PNG. What every other style degrades to, and what a fresh install paints.
1 Lens ★     nine-cell mosaic drawn TWICE: a ground copy at cell 304/3 ≈ 101.33, baked-blurred σ 22,
   default   saturation 1.25, black overlay A 0.38; above it a crisp copy at saturation 1.12 clipped to a
             heart stencil whose bounding square is 0.76 × 304 = 231.04 (inset 36.48 per side), plus a
             0.35-unit rim stroke drawn as a sibling above the clip.       LikedCoverTreatments.cs:230-262
2 Wall       6×6 grid (4×4 in a miniature) of 3-DIP-radius cells in a rect at left −103.4, top −127.7,
             span 516.8, gap 7; Rotation −11° about (0.5,0.5); TranslateY drift −46 DIP over 92 000 ms.
3 Rainbow    #131318 plate under a 4×4 grid, gap 2, cell 74.5, ordered hue-ascending boustrophedon
             (ungraded last), plus the name chip.                                          :237-275, :414-430
4 Marquee    three diagonal bands at −16°, tile 104, gap 7, run 8 per repetition (888), band left −182.4,
             band width 699.2, tops −20 / 95.5 / 211, loops 60 000 / 74 000 / 66 000 ms.   :517-620
5 Feature    the newest like as a 202-DIP hero (2×2) + six followers at cell 100, gap 2, Scrim 0.5.  :368-405
6 Mosaic     flat 3×3 of the nine newest, cell 304/3, gap 0, Scrim 0.55, name chip.                 :357-363
7 Tone       NO artwork: a multi-radial gradient graded from the newest likes + a vector heart at
             0.62 × 304 ≈ 188.5 as frosted glass + the collection size.                            :650-660
8 Stack      the last five fanned hub-card style: five 150-DIP covers at FanLeft 77 / FanTop 66, poses
             (−22,−26,6) (−11,−12,−2) (0,0,−6) (11,12,−2) (22,26,6), origin (0.5,1.2), oldest first.  :685-714

Below BadgeMinSize 140 the ladder swaps to MiniTone / a plain 2×2 Surfaces.Mosaic / the stock PNG,
because a 48-DIP Wall would be specks.                                             LikedCoverArt.cs:133-146
```

### W25 — Lyrics: second line, blur strength, animated backdrop @ the 340 rail and the immersive stage

```
SECOND LINE                     Off (default)              Translation / Romanization
┌────────────────────────────┐  ┌──────────────────────┐   ┌──────────────────────────────────┐
│ rail: 26 / LH 33 / w700    │  │ Yesterday, all my    │   │ Yesterday, all my troubles…      │
│ stage: 36 / LH 46 / w700   │  │ troubles seemed so…  │   │ 어제, 나의 모든 고민이…           │
│ row pad 7 rail / 9 stage   │  │                      │   │  ← 0.62 × size (16 rail / 22     │
│ NO fixed row height        │  │                      │   │    stage), 0.62 × line height,   │
└────────────────────────────┘  └──────────────────────┘   │    w600, _ink.Secondary, top     │
                                                           │    margin SecondaryGapDip 3      │
                                                           │    plain: no wipe, no glow, no   │
                                                           │    Lift — but it INHERITS the    │
                                                           │    row's DoF σ, opacity, scale   │
                                                           └──────────────────────────────────┘
A mode flip changes EVERY row's height, so the view marks the viewport LayoutDirty | VirtualRangeDirty and
calls ResetScrollSnap() in UseEffect(DepKey.From(secondary)).                     LyricsView.cs:394-399
The header toggle (rail + immersive) is HIDDEN when the document carries neither layer, and CYCLES only
through the layers it has: none → translation → romanization → none, skipping absent ones.  :3044-3054
Rail toggle: 32-DIP box / 16 glyph (RightRail.cs:373-421). Immersive: a 40-DIP StageChrome.ScrimFab left of
the 44-DIP ExitFab (ImmersiveLyricsSurface.cs:355-398).

BLUR σ ladder                    depth of field, by |index − active| clamped to 6
   rail  (RailSigma):   0 / 1.25 / 2.5 / 4 / 5.5 / 6.5              LyricsView.cs:38-46
   stage (StageSigma):  0 / 1.2 / 2.0 / 2.6 / 3.0                   LyricsView.cs:50-57
   σ(i) = DofSigma(min(|i−active|,6), large) × Scale(resolved), Scale = clamp(strength,0,100)/100
   100 (Auto on a normal GPU) ⇒ ×1.00 · 40 (Auto on a weak GPU) ⇒ ×0.40 · 0 ⇒ every σ snapped to 0 in ONE
   pass and no blur layer emitted at all (the engine drops σ ≤ 0.01)
   glow halo, active row only: (large ? 10 : 7) × haloScale  → 100: 10/7 · 40: 4.0/2.8 · 0: 0
   held-note bloom: (large ? 4.5 : 3) × dofScale × glowAlpha, and 0 whenever the row already has DoF
   ramp: increases snap, decreases ease with DofRampTauMs 65 (~95 % in 200 ms); write gate 0.5
                              LyricsBlurPolicy.cs:20-36 · LyricsView.cs:1582-1671, :2259-2273, :2800

BACKDROP                        ON (default)                         OFF
   the baked-blur cover         σ 80, resolution scale 0.5, baked     the SAME baked cover, held
   (ImmersiveLyricsSurface)     ONCE per art change; painted at       perfectly still — and NO TICKER
                                Overscale 1.30 (15 % margin/side)     IS MOUNTED AT ALL (not a ticking
                                and drifted on two incommensurate     no-op). ResetDrift writes
                                sinusoids: periods 37 s and 53 s,     Affine2D.Identity; the declared
                                translation ±4 % of body W/H,         1.30× re-centre stays, so the
                                scale ±2 %, ticked at 33 ms (~30 Hz)  still cover is still full-bleed
                                write gates 0.15 DIP / 0.0004         and resize-correct
                                clock: QPC, origin latched on the     :62-91, :162-165, :500-549
                                first tick (never TickCount64)
   Motion.ReducedMotion vetoes the drift independently of this setting.        :139
   The RAIL lyrics panel has no backdrop at all — this preference touches one surface.
```

### W26 — Player bar: remaining vs total @ the 44-DIP time slot

```
ON (default)                                           OFF
┌─────────────────────────────────────────┐           ┌─────────────────────────────────────────┐
│ 1:23 ├──────●───────────────────┤ -2:18 │           │ 1:23 ├──────●───────────────────┤  3:41 │
│  ↑44  seek rail                   ↑44   │           │                                   ↑44   │
│  Justify End       Justify Start ───────┘           │  the same 44-DIP slot, same Start align │
└─────────────────────────────────────────┘           └─────────────────────────────────────────┘
right label: ms = remaining ? max(0, duration − pos) : duration; prefix "-" ONLY while ms > 0 — at/after
the end it reads 0:00 with no sign, because "-0:00" is a countdown that never resolves    PlayerBar.cs:1186-1199
format: TimeFormat.Clock — m:ss, growing an h:mm:ss field at exactly 3 600 000 ms, invariant digits
width: 44 at rest (MinWidth 44, Shrink 0) so a track starting cannot reflow the seek row; a FLOOR, not a
cage, while live (Width NaN)                                                              PlayerBar.cs:1208-1212
the right label is CLICK-TO-TOGGLE (Cursor.Hand, hover/pressed fills); the left elapsed label is inert  :1216-1220
live/broadcast replaces the whole slot: a LivePill, or GO LIVE −m:ss once behind, in a fixed 104-DIP slot  :1250-1281
the label exists only from the Compact tier up (≥ 440 DIP); elapsed needs Medium (≥ 760)
                                                                          PlayerBarResponsiveLayout.cs:22-27, :139-140
the immersive stage mounts the SAME component, so its right label toggles identically    StageIdentity.cs:359-363
```

### W27 — Sidebar: the three designs and the one shared collapsed rail @ their Narrow tiers (240 / 300 / 280)

```
                Classic (0, default)      Library V3 (1)              Curated (2)
tiers N/M/W     240 / 280 / 320           300 / 340 / 380             280 / 320 / 360
clamp           180 … 460 (all three)     same                        same
collapsed rail  56 (all three)            56                          56
tier arms at    ≥1400 → Mid, ≥1800 → Wide, hysteresis 24 (widen at once, shrink only 24 past)
document        LOCKED built-in           built-in + a fixed chrome   USER-AUTHORED (sidebar-layout.json,
                (read-only; no customizer) stack ABOVE the list        reducer + undo + autosave)
sections        Pinned (rail) · Your      header 44 · toolbar 36 ·    whatever the template/customizer says;
                Library (Shortcuts, 40)   chip rail 40 · breadcrumb   template id sidebar.curated.template
                · Playlists (Cozy +       32 (narrow/drawer) · nav    default "wavee.curated.default"
                subtitles, 44 with        row 40 · word rail 30
                32-DIP art) · DevTools    grid gap 8; sort trigger
                                          icon-only < 280; folders
                                          drill-in < 320
row ladder      shared: Compact 32 · Cozy 40 (44 with a subtitle) · Comfortable 44 (48 with) ·
                Classic 44; art 20 / 32 / 40; pane pad (8,8,8,12); indent step 12; row inset 4 / 8
rail            ONE shared 56-DIP strip for all three: exactly the sections whose spec sets ShowInRail, in
                document order, per-kind caps + a 40-tile total cap; the TOOLTIP IS THE LABEL (56 DIP has no
                room for text); 4 shimmer tiles while pending
   SidebarDesign.cs:62-69 · ShellResponsiveLayout.cs:11,121,172-175 · SidebarRowGeometry.cs:21-125 ·
   SidebarPaneMetrics.cs:25-94 · LibraryV3Metrics.cs:22-90 · SidebarPaneRail.cs:11-28

┌ expanded, Classic, 240 ──┐   ┌ collapsed, any design, 56 ┐
│ ⌂ Home                   │   │  ⌂   │  ← the rail tiles: one per ShowInRail section, tooltip = label
│ ⌕ Search                 │   │  ⌕   │
│ ── Pinned ───────────    │   │  ♡   │
│ ▣ Liked Songs            │   │  ▣   │
│ ── Your Library ─────    │   │  ▣   │
│ ▣ Albums     40-DIP rows │   │  ▣   │
│ ▣ Artists                │   │ ...  │
│ ── Playlists ────────    │   │      │  cap 40 tiles; consecutive dividers collapse
│ ▣32 Deep Focus   44 rows │   │      │
│     Christos · 50 songs  │   │      │
└──────────────────────────┘   └──────┘
Switching designs is a snapshot/restore over that design's OWN (width, userSet, collapsed) triple:
Restore uses the stored width only if WidthUserSet, clamped 180/460 — otherwise `TierDefault(design, viewportWidth)`,
which is that design's OWN tier ladder evaluated at the LIVE viewport, NOT its Narrow tier. Narrow is only what a
zero/unknown viewport (the pre-measure seed) or a window below NavPaneMidEnterW 1400 resolves to; on a 1500-DIP
window an un-pinned Curated design restores at 320, and at 1900 at 360.
                                     SidebarDesign.cs:101-109, :113-114 · ShellResponsiveLayout.cs:172-175, :199-203
```

### W28 — Shell right rail width and the docked-video cap @ rail 340

```
┌ sidebar ─┬ content ─────────────────────┬ right rail (ShellRailWidth, default 340, 200…500) ─┐
│          │                              │ ┌ docked video cap ───────────────────────────────┐│
│          │                              │ │ height 0 ⇒ railW × 9/16 (340 → 191.25)          ││
│          │                              │ │ the vertical splitter only GROWS from there,     ││
│          │                              │ │ ceiling max(min, 560)                            ││
│          │                              │ │ splitter overlays the cap's bottom 16 DIP        ││
│          │                              │ └─────────────────────────────────────────────────┘│
│          │                              │ ┌ the rail pane: Lyrics / Queue / Now playing ────┐│
│          │                              │ │                                                  ││
│          │                              │ └─────────────────────────────────────────────────┘│
└──────────┴──────────────────────────────┴───────────────────────────────────────────────────┘
CanFitRail(viewportW, sidebarW, railW = 340, minContentW = 480)                ShellResponsiveLayout.cs:164-165
A content-aspect fit path (FitDockedVideoHeight) clamps to 120…560 and answers 9/16 when the player has not
reported a size — deliberately NOT through ClampDockedVideoHeight.                                 :142-162
```

### W29 — Now Playing hero: Cover vs Player deck @ rail 340

```
NOTHING PLAYING: the pinned hero slot composes `new BoxEl()` and the preference is INERT — NowPlayingPanel itself
shows Empty(...) with no hero at all, so a tile for no track (and a header strip over it) would be chrome for a hero
that is not there.                                                                     NowPlayingPanel.cs:525-528

NpvPresentation 0 Cover (default)              NpvPresentation 1 Player, NpvPlayerStyle 0 Record
┌ right rail ────────────┐                     ┌ right rail ────────────┐
│ ┌────────────────────┐ │                     │ ┌────────────────────┐ │
│ │ HeroArt            │ │                     │ │ the deck, side =   │ │
│ │ side = railW − 16, │ │                     │ │ railW − 2×Spacing.S│ │
│ │ snapped to a 4-DIP │ │                     │ │ snapped to 4 DIP   │ │
│ │ grid               │ │                     │ │ keyed KeyFor(preset)│ │
│ └────────────────────┘ │                     │ │  + "@" + side      │ │
│  Title / artists       │                     │ └────────────────────┘ │
└────────────────────────┘                     └────────────────────────┘   NowPlayingPanel.cs:531-548
Twelve presets, ids frozen (append-only, never renumbered): 0 Record · 1 Cassette · 2 Reel · 3 CD ·
4 Turntable · 5 iPod · 6 Winamp · 7 VU · 8 Zune · 9 WMP · 10 Canvas · 11 Picture      NpvPlayerCatalog.cs:16-17
…in THREE groups of four (`NpvPlayerGroup` Media / Devices / Software, `PerGroup = 4`, `:5`, `:19`) — the flyout
and the Settings ComboBox both present them in that order, and `DefaultPresetId = Record` (`:18`).
`NpvPlayerPrefs.NextStyle` cycles `(Style + 1) % 12` (`:42`), and `TogglePresentation` (`:33`) is the header row's
one-tap Cover⇄Player verb — two writers the flyout/menu paths share with Settings.
Record / Turntable / Zune / Picture are ONE face with four skins (RecordVariant, DeckFrame.cs:37).
An unknown-but-valid id draws a blank square, never a crash (DeckFaces.cs:44-46).
Per-deck options live at npv.player.<presetSlug>.<optionSlug>, default 0 (Finish / Size / Rpm / Sleeve).
Picking a STYLE also flips presentation to Player (NpvPlayerPrefs.cs:34-39).
```

---

## 3. Tokens

This chapter introduces **no new colour tokens**. What it owns is the set of values each preference *chooses
between* — the ladders. Every row is the value the preference selects, not the value the surface happens to have
today. A number with a formula is computed; a number without one is a literal at the cited line.

### 3.1 Row density × track list style — the row ladder

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| track row, **Modern** — Compact / Default / Cozy / Comfortable | 40 / **48** / 56 / 64 | `RowInset` 8 each side · `PadXFor(tier)` 16 (tier ≤3) / 12 (4–5) / 8 (6) · `ColGap` 12 | 6 | title 14/20/600 · sub 12/16 | border 1 `Tok.StrokeCardDefault`; zebra `WaveeColors.RowZebra` on `DisplayIndex % 2 != 0` | flat (no shadow) | `DetailTrackTableRules.cs:45-47` · `DetailTracks.cs:3512-3519` |
| track row, **Classic** | 36 / **40** / 44 / 48 | `Margin 0` (no inset) · same `PadXFor(tier)` | 0 (`Radii.None`) | title 14/20/600 · factual cells 14/20 (Modern: 12/16) | bottom hairline 1 `Tok.StrokeDividerDefault`, inset `PadXFor(tier)` each side | flat | `DetailTrackTableRules.cs:45-47` · `DetailTracks.cs:3491-3533`, `:3590-3597` · `TrackRow.cs:723-731` |
| row thumbnail, **Modern** | 32 / **32** / 40 / 48 | — | `Radii.Control` 4 | — | — | decode `(int)(art × 2)` px | `DetailTrackTableRules.cs:54-56` · `TrackRow.cs:246-247` |
| row thumbnail, **Classic** | published 32 / 32 / 32 / 40 — **dead in the table** | — | — | — | — | — | `:54-56`; the lane's gate is `!classic`, `:37` |
| column header | 36 Modern · 32 Classic (`ClassicHeaderHeight`), + a 1-DIP rule | — | — | Modern 12/600, no tracking · Classic 11/600 + 30 tracking + `.ToUpper(CurrentUICulture)` | — | — | `DetailTrackTableRules.cs:27`, `:49` · `DetailTracks.cs:2468-2484` |
| density **preview card** row | 10 / 12 / 14 / 16 = `TrackRow.RowHeightFor(d)` (the MODERN ladder) × `PreviewScale` **0.25**; art 8 / 8 / 10 / 12 | bar height `Spacing.XXS`, `Corners = Radii.PillAll` | — | — | `WaveePicker.Ink.For(on)` — `.Block` for the strong bar, `.Faint` otherwise | — | `DetailTrackTableRules.cs:31` · `WaveePicker.cs:105-118` |
| the preview **card shell** (`WaveePicker.Tile`) — density, track-list style and page-layout strips all use it | 116 × 84 | resting inset 4 | 8 | — | — | — | `WaveePicker.cs:44` |
| the strip container | wraps; `RadioButtons.PartGrid` `Wrap = true`, `Gap = Spacing.M`, `PartColumn` `Shrink = 0` — column-major (WinUI `ColumnMajorUniformToLargestGridLayout`), so items fill the first column before the second | — | — | — | — | — | `WaveePicker.cs:250-253`, `:263-277` |
| documented invariant | `row − art ≥ 16` on the Modern ladder | — | — | — | — | — | `DetailTrackTableRules.cs:51-53` |

### 3.2 Hide track artwork · Plays · Tempo — the lane widths this chapter's toggles add or remove

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| Thumb lane | 32 (`TrackLane.Thumb`) + one 12-DIP `ColGap` — **44 DIP returns to the title** when hidden | — | 4 | — | — | — | `DetailTrackTableRules.cs:243`; gate `:37` |
| Plays lane | 52 | `ColGap` 12 · `FlexJustify.End` | — | Modern 12/16 · Classic 14/20 | `Tok.TextSecondary` | — | lane table, `DetailTrackTableRules.cs` · `DetailTracks.cs:493`, `:506`, `:571` |
| Tempo ("BPM · Key") lane | 80 | same · **not** sortable (`PlainHeader`) | Camelot swatch 6 | same | swatch = the key's Camelot colour | — | `DetailTracks.cs:574`, `:2417-2419` · `TrackRow.cs:584-607` |
| the other lanes it competes with | Num 28 · Heart 28 · Title floor 120 · Artist floor 90 · Album floor 90 · By 132 · Date 88 · Duration 52 · Video 28 · Actions 40 · Expand 26 | `ColGap` 12 | — | — | — | — | `DetailTrackTableRules.cs` lane table |
| relief yield order (cheapest first) | Plays 1 → Tempo 2 → Added by 3 → Date 4 → Album 5 → Artist 6 → Thumb 7 → ♥ 8; `MaxRelief` 8, 24-DIP hysteresis | — | — | — | — | — | `DetailTrackTableRules.cs:154-163` |

**One inconsistency to fix rather than port:** the relief ladder measures the thumb lane at a hard 32 while the
grid builds `art` at 40 (Cozy) / 48 (Comfortable), so `MinWidthFor` under-predicts the squeeze by 8 / 16 DIP at
those densities (`DetailTrackTableRules.cs:243` vs `DetailTracks.cs:560`).

### 3.3 Track page layout + the rail preferences — the geometry each choice selects

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| rail column, mode 0 | `cfg.RailWidth` (280 album/show · 240 playlist/liked) **or** the persisted width, clamped 180…480 | Padding 16/24/8/24 · Gap 14 | — | — | `Tok.FillLayerDefault` — **except the Liked arm, which omits it** | — | `WaveeTokens.cs:60` · `DetailRailPolicy.cs:25` · `DetailShell.cs:629` · `DetailRail.cs:276-286` |
| rail column, modes 1 / 2 | 224 / 188, fixed — the persisted pair is ignored | same | — | — | same | — | `DetailShell.cs:192` |
| rail column, **collapsed** | 96 (`RailCompactW`) | pad 8 sides / 16 top / 16 bottom | — | title 12/600, `Width 80`, ≤2 lines, `CharacterEllipsis` | chevron hover `Tok.FillSubtleSecondary`, glyph 14 `Tok.TextSecondary` | — | `DetailShell.cs:203` · `DetailRail.cs:304`, `:316-321` |
| rail cover | `CoverEdge(railW) = max(80, railW − 16 − 8)` → 280→256 · 240→216 · 224→200 · 188→164 · 180→156 · 480→456 | `SidePadL` 16 · `SidePadR` 8 | `Radii.Card` 8 | — | — | `Elevation.Card`, saturation 1.18 | `DetailRail.cs:96` |
| rail **grip** | hit strip 16 expanded (`Splitter.StripW`) / 20 collapsed (`GripStripCollapsedW`) | — | — | — | — | reveal-on-hover 2-DIP thumb | `Splitter.cs:22` · `DetailShell.cs:206` |
| grip bounds + detent | Min 180 · Max 480 · `ForcePush` 44 (raw ≈ 136) · `ReExpand` 220 | `FadeDistance` 44 · `MinFade` 0.35 · `Resist` 0.28 (engine defaults) | — | — | — | — | `DetailRailPolicy.cs:25` · `DetailShell.cs:200` · `Splitter.cs:68-70` |
| uniform pair | `detail.rail.uniform.{width,collapsed}`, default **240 / false** — a FIFTH key pair | — | — | — | — | — | `AppSettings.cs:167-171` |
| hero artwork, **row flow** | `round(clamp((w − 2×24 − 24) × 0.44, 144, 240))`; the 240 cap binds from w ≥ 617 | pad 24 · gap 24 | `Radii.Card` 8 | — | — | `Elevation.Card`, sat 1.18, decode 1024 above 288 | `DetailVerticalLayout.cs:61-63` |
| hero artwork, **stacked** | `round(clamp(w − 32, 96, 280))` | pad 16 (`NarrowHeroPad`, w < 420) · gap 16 | same | — | — | decode 512 at ≤288 | `DetailVerticalLayout.cs` |
| hero flow flip | `RowFlowEnterW` 424 / `RowFlowLeaveW` 400 (= 424 − 24) | — | — | — | — | — | `DetailVerticalLayout.cs:47-50`, `:106` |
| hero title | `SnapTitleSize`, floor 20, cap 96, snapped to the 8-DIP grid at ≥ 64 | — | — | `LineHeight = round(size × NaturalLineRatio 1.3301)`; hero clears `LineHeight` to NaN | — | — | `DetailVerticalLayout.cs:327-333`, `:238-265` |
| hero fluid cap | `clamp(0.091892 × w − 5.0811, 28, 96)` — the line through (360, 28) and (1100, 96) | — | — | — | — | — | `DetailVerticalLayout.cs:301-314` |
| two-column title | 40/52 when `winH ≥ 900`, else 28/36 | — | — | w600, ≤3 lines, `MinSize 18` | — | — | `DetailShell.cs:636-637` |
| two-column description cap | `descLines = winH < 760 ? 3 : 6` | — | — | 12 px | — | — | `DetailShell.cs:638` |
| ContextBand (hero only) | Height 56 + 1 hairline · `StickyClipInset` = 56 + 36 + 1 (+ 48 for the Liked chip rail) | `ClusterGap` 24 · `PivotGap` 16 · `ActionGap` 16 · `PivotPadX` 8 · `ActionPadX` 10 | — | underline 2 with a 4-DIP gap · `TitleCap` 280 · `AvgCharW` 7.6 | **no fill, no shadow** — the page's tone plane shows through | feather `StickyFadeBand` 24 | `ContextBandLayout.cs:25-76`, `:33-44` |
| page row | `MaxWidth` 1600 (`WaveeSize.PageMaxW`) · `Grow 1 Shrink 1 Basis 0` · `Justify Center` | — | — | — | — | — | `WaveeTokens.cs:75` · `DetailShell.cs:639-656` |
| mode ladder | 0 ≥ 820 · 1 660–819 · 2 560–659 · 3 < 560 (enter < 540, leave ≥ 580); `ModeHysteresisDip` 24 | right column `MinWidth` 300 (modes 0–2) / 0 (vertical) | — | — | — | — | `DetailLayoutBreakpoints.cs:8`, `:59-69`, `:65-66`, `:75-87` |
| tier ladder (right column) | ≥860 → 0 · ≥720 → 1 · ≥560 → 2 · ≥440 → 3 · ≥340 → 4 · ≥300 → 5 · else 6; `TierHysteresisDip` 24 | — | — | — | — | — | `DetailLayoutBreakpoints.cs:7`, `:10-11` |
| pre-measure seed | `EstimatePageWidthFromViewport(w) = max(0, w − 240)` (240 = `NavPaneNarrowW`) | — | — | — | — | — | `DetailLayoutBreakpoints.cs:32-38` |

**Where the context band lives in 0.3** (arbitration 2026-09-12, recorded here because this chapter is where a
Wave-4 owner looks the numbers up): `ContextBand` and `ContextBandLayout` are **not** this chapter's plumbing and
**not** `Platform/Design.cs`. They are the shared detail frame's — `Entities/Detail.cs` (CORE) +
`Entities/Detail.UI.cs` (the `Detail.Band` / `BandTitle` / `BandByline` / `BandAnchor` / `Detail.Pivot` helpers),
owner **M**, Wave **4.5**. The reason is arithmetic, not taste: the band's clip feather *is*
`DetailVerticalLayout.StickyFadeBand` and its `Height` 56 *is* `CompactIdentityHeight`, so any other home drags the
detail frame's vertical layout in behind it. Specified in `03-detail-frame.md` (§1.2 `:151`, §8 `:880`, §9 `:987`)
with `08-artist-and-discography.md` §1.2 as the second consumer. The row above stays this chapter's business for the
one reason this chapter exists: **no preference moves any of these numbers, and one preference decides whether the
band is on screen at all** — Track page layout = Hero (or a window narrow enough to reach mode 3) selects the
vertical arm, which is the only arm that has a band (`03-detail-frame.md` §0 item 17).

### 3.4 Zoom, theme, marquee

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| zoom ladder (12 rungs) | 0.50 · 0.67 · 0.75 · 0.80 · 0.90 · **1.00** · 1.10 · 1.25 · 1.50 · 1.75 · 2.00 · 2.50 | — | — | — | — | — | `fluent-gpu/.../ZoomLadder.cs:17-18` |
| Auto plateaus | 0.75 · 1.00 · 1.25 · 1.50 · 1.75 · 2.00; `Ceiling` 2.0; `DenseFloor` 0.75; `DesignW/H` 1600 / 900 | — | — | — | — | — | `ZoomAutoPolicy.cs:42-59` |
| zoom equality tolerance | 0.004 — spelled **four** times for zoom (`ZoomAutoPolicy.cs:110` · `WaveeShell.cs:615`, `:624` · `WaveeApp.cs:242`); `WaveeApp.cs:231` is the same literal for the **volume** save timer and is a different decision | — | — | — | — | — | one named constant in 0.3, and do NOT fold the volume one into it |
| the zoom picker | ComboBox width **160**, 13 items (Auto + 12 rungs) | — | — | index 0's label carries the live resolved percentage | — | — | `SettingsPage.Appearance.cs:343-345` · `:84-90` |
| other fixed picker widths | NPV style 180 · Language 260 · GPU 300 | — | — | a long translation ellipsizes inside them | — | — | `SettingsPage.Appearance.cs:402` · `SettingsPage.General.cs` |
| `Elevation.Card` (theme-dependent) | dark Blur 8 OffsetY 2 `#00000033` · light Blur 4 OffsetY 2 `#0000001A` | — | — | — | — | **this is the only geometry a theme flip moves** | `fluent-gpu/.../Elevation.cs:18-21` |
| player-bar title marquee | Speed 18 dip/s · `CycleMs` 10 000 · `EndPauseMs` 2 500 · `PingPong` · `Trigger.Hover`, driven by an EXTERNAL gate (`scrollWhen: titleHover`) so the whole identity block's hover scrolls it, not just the text box | edge fade `FadeBand` 24, clamped to `MaxFadeViewportFraction` **0.30** of the viewport (`Marquee.cs:102`, `:118`) — present on overflow whether or not it is scrolling, which is what tells an idle title it has more to it | — | 14/700 | `NowPlaying(b).Color`, or `Tok.AccentTextPrimary` while the title is hovered AND album-nav is available | — | `PlayerBar.cs:92-93`, `:277-291` · `Marquee.cs:102`, `:118` |
| now-playing ROW marquee | Speed 9 · `CycleMs` 0 (derive the duration from Speed alone) · Gap 48 · `StartDelayMs` 350 · `EndPauseMs` 900 · `FadeBand` 24 · `FadeStrength` 1 · `Loop` · `Trigger.Always` — the engine defaults, taken as-is | — | — | 14/600 (`BoundTitle` overrides only FontSize/Weight/Foreground) | `Tok.AccentTextPrimary` | — | `fluent-gpu/.../Marquee.cs:35-49` · `DetailTracks.cs:2687-2693` |
| the marquee-OFF arm | `TextTrim.CharacterEllipsis` · `Wrap NoWrap` · `MaxLines 1` · `MinWidth 0` | **no edge fade** (the fade lives on `MarqueeHost`) | — | same size/weight | same | — | `PlayerBar.cs:266-276` · `DetailTracks.cs:2701-2706` |

### 3.5 Liked cover, lyrics, player bar, shell rail, sidebar, NPV

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| liked-cover authoring canvas | `DesignSize` **304** square; every treatment scales `size / 304` | per treatment (§2.25) | caller's radius | — | — | — | `LikedCoverTreatments.cs:40`, `:113-114` |
| liked-cover floors | `ChromeMinSize` **180** (name chip etc.) · `BadgeMinSize` **140** (any dynamic treatment at all) | — | — | — | — | below 140 → MiniTone / a 2×2 `Surfaces.Mosaic` / the stock PNG | `LikedCoverTreatments.cs:46`, `:49` · `LikedCoverArt.cs:133-146` |
| liked-cover tile minimums | Stock 0 · Lens 4 · Wall 8 · Rainbow 8 · Marquee 6 · Feature 4 · Mosaic 4 · Tone 1 · Stack 3; `MaxTiles` 16 | — | — | — | — | — | `LikedCoverRules.cs:139-150`, `:100-131` |
| liked-cover decode buckets | `WallDecodePx` 64 · `GridDecodePx` 128 · `HeroDecodePx` 256 | — | — | — | — | — | `LikedCoverTreatments.cs:54` |
| the liked-cover **style flyout** itself | miniature edge `MiniEdge` **76** (= `WaveePicker.CoverMini`'s 92 card minus its 8-per-side resting inset); `Columns` **3** | card pad (16, 15, 16, 17) = WinUI `FlyoutContentPadding`, which the presenter deliberately supplies none of | `MiniRadius` **5** (the `CoverMini` shell's own radius) | — | `WaveePicker.Ink.For(on)` | a style below its own `MinTiles` draws its miniature as the **stock cover, DIMMED**, under the same name — the miniature shows what picking it would paint TODAY | `LikedCoverPicker.cs:154-158`, `:190-195` · `WaveePicker.cs:52` |
| the picker's ORDER | `LikedCoverRules.PickerOrder` — **not** the enum order W24 lists; the enum values are the persisted wire, the picker order is a presentation decision | — | — | — | — | — | `LikedCoverRules.cs` · `LikedCoverPicker.cs:176-178` |
| lyric row | font 26 / LH 33 (rail) · 36 / 46 (stage) — ratio ≈1.27 | `rowPad` 7 rail / 9 stage; inter-line gap = 2 × `rowPad`; **no fixed row height** | — | w700 | `_ink` | — | `LyricsView.cs:1312-1313`, `:1336` |
| lyrics **secondary** run | `0.62 ×` the lyric size *and* line height → ≈16 rail / ≈22 stage | top margin `SecondaryGapDip` **3**; the row's own pad still owns the gap to the next line | — | w600, `Wrap`, `MaxLines 0`, `Trim.None` | `_ink.Secondary` | no wipe, no glow, no Lift — but it **inherits** the row's σ / opacity / scale | `LyricsView.cs:2900-2911`, `:2918-2919` |
| DoF σ ladder | rail `0 / 1.25 / 2.5 / 4 / 5.5 / 6.5` · stage `0 / 1.2 / 2.0 / 2.6 / 3.0`, indexed by `min(abs(i − active), 6)` | — | — | — | — | `σ(i) = DofSigma(...) × clamp(strength,0,100)/100` | `LyricsView.cs:34`, `:38-57`, `:1582-1592` |
| blur strength resolution | `Auto` = −1 → **100** on a normal GPU, **40** on a weak one (`GpuProfile.IsWeak`) | — | — | — | — | 0 emits no blur layer at all (the engine drops σ ≤ 0.01) | `LyricsBlurPolicy.cs:20-36` |
| the blur slider | Min 0 · Max 100 · Step 1 · `TickFrequency` 25 · length **180** + an "Auto" link shown only while pinned | `Gap = Spacing.M` between slider and link | — | thumb tooltip = `round(v) + "%"` | — | — | `SettingsPage.Appearance.cs:139-154` |
| glow halo (active row) | `(large ? 10 : 7) × haloScale` → 100: 10 / 7 · 40: 4.0 / 2.8 · 0: 0 | — | — | — | — | held-note bloom `(large ? 4.5 : 3) × dofScale × glowAlpha`, and 0 whenever the row already has DoF | `LyricsView.cs:2259-2273`, `:2800` |
| immersive backdrop | baked blur σ **80**, `BackdropResolutionScale` **0.5**, `Overscale` **1.30** | drift ±`DriftAmpFrac` 0.04 of body W/H, scale ±2 %, periods `DriftPeriodASec` 37 / `DriftPeriodBSec` 53, tick `DriftIntervalMs` 33 (~30 Hz) | — | — | — | `BakedBlurSpec(80, 0.5)`, baked ONCE per art change | `ImmersiveLyricsSurface.cs:62-77`, `:428`, `:515-521` |
| lyrics second-line toggles | rail 32-DIP box / 16 glyph · immersive 40-DIP `StageChrome.ScrimFab` left of the 44-DIP `ExitFab` | — | — | — | — | hidden when the document carries neither layer | `RightRail.cs:373-421` · `ImmersiveLyricsSurface.cs:355-398` |
| player-bar right time slot | `Width` 44 at rest (`MinWidth` 44, `Shrink` 0); `Width NaN` while live; the live/broadcast slot is a fixed **104** | — | `Radii.Control` | `Caption().Secondary()`, `TimeFormat.Clock` (`m:ss`, grows `h:mm:ss` at exactly 3 600 000 ms) | Hover `Tok.FillSubtleSecondary` · Pressed `Tok.FillSubtleTertiary` (on-media: `WaveeOnMedia.GlassHover/Pressed`) | — | `PlayerBar.cs:1208-1220`, `:1250-1281` |
| shell right rail | `RailDefaultW` **340**, clamp `RailMinW` 200 … `RailMaxW` 500 | — | — | — | — | — | `ShellResponsiveLayout.cs:126-127` |
| docked-video cap | 0 ⇒ `railW × 9/16` (340 → 191.25); ceiling `max(min, DockedVideoMaxH 560)`; the fit path clamps `DockedVideoFitMinH` … 560 | the vertical splitter overlays the cap's bottom 16 | — | — | — | — | `ShellResponsiveLayout.cs:130-161` · `RightRail.cs:206-228` |
| sidebar pane tiers | Classic 240 / 280 / 320 · Library V3 300 / 340 / 380 · Curated 280 / 320 / 360 | — | — | — | — | — | `SidebarDesign.cs:63-69` |
| sidebar clamp + rail | `NavPaneMinW` 180 … `NavPaneMaxW` 460; collapsed `CompactRailW` **56** for all three designs | pane pad (8,8,8,12) · indent step 12 · row inset 4 / 8 | — | — | — | — | `ShellResponsiveLayout.cs:11`, `:121` · `SidebarPaneMetrics.cs:25-94` |
| sidebar tier thresholds | Mid ≥ `NavPaneMidEnterW` 1400 · Wide ≥ `NavPaneWideEnterW` 1800 · `NavPaneHysteresisDip` 24 | — | — | — | — | — | `ShellResponsiveLayout.cs:172-175` |
| sidebar row ladder (shared) | Compact 32 · Cozy 40 (44 with a subtitle) · Comfortable 44 (48 with) · Classic 44; art 20 / 32 / 40 | — | — | — | — | — | `SidebarRowGeometry.cs:21-125` |
| NPV hero side | `round((railW − 2 × Spacing.S) / 4) × 4` — quantised to the 4-DIP grid because it is in the remount key | `Spacing.S` = 8 each side | — | — | — | — | `NowPlayingPanel.cs:541-542` |
| NPV deck ids (frozen, append-only) | 0 Record · 1 Cassette · 2 Reel · 3 CD · 4 Turntable · 5 iPod · 6 Winamp · 7 VU · 8 Zune · 9 WMP · 10 Canvas · 11 Picture | — | — | — | — | Record / Turntable / Zune / Picture are ONE face with four skins | `NpvPlayerCatalog.cs:16-17` · `DeckFrame.cs:37` |

---

## 4. Colour & material

Two preferences in this chapter are colour decisions rather than layout ones — **Color washes** and **Theme** —
and one (**Liked cover style**) reaches colour indirectly, through the page's tone anchor. This section says
exactly what each removes or moves, per surface. The token pair itself is W22; this is the mechanism.

### 4.1 What "Color washes OFF" actually removes, surface by surface

`ColorWashesEnabled` is not one switch on one layer. It is read independently by six surfaces, and each one
does something different with the answer — including one that **cross-fades** and three that **snap** (§5).

| surface | ON (default) | OFF | what the node does | file:line |
|---|---|---|---|---|
| shell **material tint** (⇒ title row, sidebar, player dock, and the ground under every page) | light `WaveePalette.Lift(scheme.TextBase) @ A 0.05` · dark `WaveePalette.TintedDark(scheme) @ A 0.14` | `WaveeColors.ShellGround @ A 0.03` — `#EDEDED@3%` light, `#202020@3%` dark | the layer **stays mounted** and its `Fill` cross-fades; it is never `ColorF.Transparent` | `CoverPaletteLeaves.cs:242-246` · `ShellMaterialLayer.cs:81-89`, `:93-98` |
| detail **page tone plane** | the art-derived `WaveePalette.PageTone`, flat, at `PlaneAlphaLight` 0.30 / `PlaneAlphaDark` 0.20 | **nothing at all** — `return new BoxEl { Grow = 1f, HitTestVisible = false }` | the node loses its `Fill` entirely, so the ramp has nothing to fade | `CoverPaletteLeaves.cs:92-94`, `:113-115`, `:139` |
| **artist** blend wash | light `Lift(Accent(pal))` else `Tok.AccentDefault`; dark `BackgroundDark(pal)` | `return new BoxEl();` — an empty box | same: a structural removal, not a colour | `CoverPaletteLeaves.cs:178`, `:183-186` |
| **Home**'s three radial washes | two stops `(0, c@α) → (FadeOffset, c@0)`: Hero centre (0.06, 0.00) r (0.74, 0.92) fade 0.62 · Weekly (0.92, 0.10) r (0.58, 0.78) fade 0.64 · Mix (0.58, 1.00) r (0.90, 0.70) fade 0.66; α 0.055 light / 0.10 dark (hero), 0.05 / 0.085 (shelves) | `wash = null` — the wash host composes no children; only the neutral tint rect above remains | the washes are clipped to stop at the dock line (`Margin bottom = PlayerDock.Reserve` 72) | `ShellWashGeometry.cs:24-37` · `ShellMaterialLayer.cs:72-77` · `HomePage.cs:216-217` |
| **Home-section** / **Recents** washes | the section's own first gradeable card, through `HomeWashSource` | `washCard = null` | same as Home | `HomeSectionPage.cs:168-171` · `RecentsPage.cs:310`, `:2478` |
| the chrome **ACCENT** (Play pill, hearts, row chrome, the hero's accent rule, the countdown) | `WaveePalette.ChromeAccent(chrome)` → `ChromeFromPayload(m.Accent)` → `Tok.AccentDefault` | **unchanged — the accent is a different axis and is NOT disabled** | chroma for chrome, a clamped tone for the page | `DetailShell.cs:304-310` |
| the **Liked cover treatment** itself | Rainbow's `#131318` plate, Tone's multi-radial gradient, every tile's own colour | **unchanged** — only the page GROUND under it goes neutral | the cover is artwork, not a wash | `LikedCoverTreatments.cs` |

**The one rule behind the asymmetry.** The shell tint is a *published material* with an owner and a definite
"no colour" value, so it can interpolate; the page tone plane and the artist wash are *leaves that exist or do
not*, so they can only appear and disappear. `ShellMaterialLayer.cs:81-89` states why the "no colour" value must
be a real colour: cross-fading into `ColorF.Transparent` (implicitly premultiplied **black**) drags the
interpolated colour toward black for the whole ramp — which is exactly what read as the shell going "neutral AND
DARKER" for several frames at almost every navigation. A 0.3 rebuild must keep a real ground at both ends.

### 4.2 The Liked cover styles' tone anchors

`LikedCoverStyle` is the only preference in this chapter that can change a **page's ground colour** rather than
just what is drawn on it — and only while washes are ON.

```
DetailShell.LikedToneAnchor(m, settings)                                      DetailShell.cs:846-857
  ├─ not the Liked page                       → null   (DetailRail.IsDynamicLikedCover(m) false)
  ├─ the style composes no art at all (Stock) → null   Effective(requested, int.MaxValue) == Stock   (cheap reject)
  ├─ the library is below the style's MinTiles→ null   Effective(requested, tiles.Count)   == Stock
  ├─ the lead tile is not gradeable           → null   CoverColorPlane.CanGrade(anchor) false
  └─ otherwise                                → LikedCoverRules.ToneAnchorUrl(tiles) = tiles[0]
                                                        LikedCoverRules.cs:305-306
```

`tiles` is the SAME newest-first, doubly-deduped, 16-capped list the cover itself composes from
(`LikedCoverRules.Tiles`, `:100-131`), so the ground and the artwork above it can never disagree about which
record they are keyed to. Per style, the anchor is therefore:

| style | what the cover leads with | tone anchor | file:line |
|---|---|---|---|
| 0 Stock | the bundled PNG | **null** — the generic first-gradeable-track answer stands | `LikedCoverRules.cs:75-78` · `DetailShell.cs:850-852` |
| 1 Lens (default) | the nine-cell mosaic's ground copy | `tiles[0]` (newest like) | `LikedCoverTreatments.cs:230-262` |
| 2 Wall | a 6×6 grid, drifting | `tiles[0]` | `:414-430` |
| 3 Rainbow | a hue-ascending 4×4 over `#131318` | `tiles[0]` — **not** the hue-sorted first cell | `:237-275` |
| 4 Marquee | three diagonal bands | `tiles[0]` | `:517-620` |
| 5 Feature | the newest like as a 202-DIP hero | `tiles[0]` — which IS the hero tile | `:368-405` |
| 6 Mosaic | a flat 3×3 of the nine newest | `tiles[0]` | `:357-363` |
| 7 Tone | **no artwork** — a gradient graded from the newest likes + a vector heart | `tiles[0]`, so the ground agrees with the gradient's own source | `:650-660` |
| 8 Stack | five fanned covers, oldest first | `tiles[0]` — the TOP card of the fan | `:685-714` |

So: switching from Stock to any treatment on a library with enough distinct covers can change the Liked page's
**ground**, not only its cover (§2.30 · interaction 4.11). Switching between two treatments cannot, because
they all anchor on `tiles[0]`.

### 4.3 Theme is colour-only, with one exception and one instant surface

1. **No geometry moves.** Theme swaps the token set through `Tok.Use(WaveeTheme.ResolvePalette(), kind)`
   (`WaveeTheme.cs:27`); the one thing that is not a colour is `Elevation.Card`, which is theme-dependent by
   construction — dark `Blur 8 OffsetY 2 #00000033`, light `Blur 4 OffsetY 2 #0000001A`
   (`fluent-gpu/.../Elevation.cs:18-21`). Card shadows therefore change *shape*, not just tint.
2. **The OS window material cannot cross-fade and does not try.** `OnApplyThemeMaterial` (DWM immersive-dark +
   Mica) is invoked on the same frame and flips **instantly**, while the in-app content cross-fades — the
   engine's own comment says so (`fluent-gpu/.../AppHost.cs:736-738`, invoked `:3499`).
3. **The OS accent ramp is adopted only in System mode** (`WaveeTheme.cs:28-33`), and a Light/Dark pin stops
   following the OS entirely (`WaveeApp.cs:50`). **But not at startup.** `Program.cs:402-404` does *not* call
   `ApplyThemeMode` — it repeats the mode switch and calls `Tok.Use(ResolvePalette(), kind)` directly, so the
   `mode == 0` accent-ramp arm is skipped on the launch path. The first frame therefore paints
   `Tok.AccentDefault`'s ramp, and the OS ramp lands at the first `SystemColorsChanged` or the first explicit
   Settings theme pick. Recorded as a fact, not endorsed: 0.3 should decide deliberately whether the launch path
   adopts the ramp (a one-line change: call `ApplyThemeMode` there), because the accent is what W20 says survives
   a washes-off page.
4. **One palette, two arms.** `ResolvePalette() => Tok.NeutralPalette` (`WaveeTheme.cs:15`) — the palette picker
   is gone. A future preset re-introduction has exactly one call site to change.
5. The separation model carries **no shadow on the content rung**: a translucent fill plus a 1 px
   `Tok.StrokeCardDefault` on LEFT + TOP edges only, one rounded corner (`WaveeTokens.cs:96-99`).

---

## 5. Motion

**This is the section the chapter was missing, and it is the point of W16.** A preference flip changes *what a
surface is*, not *where it is going* — so almost every row below is an explicit **snap**, and the few that are
not are colour fades or a width commit that was already animated for another reason. Nobody added motion for a
preference, and a 0.3 re-author must not add any: an animated density change ripples an entire virtualized
column, and an animated layout-arm swap shows two heroes at once.

Every row states what happens on a **MOUNTED** page and on a **KeepAlive-PARKED** one. The parked arm is not a
footnote: `ContentHost` keeps `MaxEntries: 3` — the live page plus a two-deep back stack — and all three see the
change, because a parked component's `Render` still runs when its subscriptions fire
(`Features/Shell/ContentHost.cs:106`). What a parked page does *not* do is play a transition on reactivation:
`SuppressLayoutTransitionsOnActivation: true` (`ContentHost.cs:108`).

### 5.1 The five epoch bumps — what a bump actually causes

An epoch is a `Signal<int>`; bumping it re-runs every `Render` that read it and every `UseEffect` keyed on it.
It carries no value and no duration. This table is the fan-out, so a 0.3 owner can see what one write costs.

| epoch | bumped by | re-runs | what that re-run does | file:line |
|---|---|---|---|---|
| `AppearancePrefs.Epoch` | `AppearanceToggle` (marquee · washes · lyrics backdrop), `TrackArtworkCheckBox`, `SetTrackListStyle`, the Liked cover picker | `DetailShell.Render`, `TrackList`'s `rowsSnapshot` memo, `LikedCoverArt`'s snapshot memo, `PlayerBar.Render`, `ImmersiveLyricsSurface.Render`, Home / Home-section / Recents / Artist | re-reads the store; the equality-gated memos propagate only the values they carry | `AppearancePrefs.cs:8-9` · consumers §1.2 |
| `DetailHeroPrefs.Epoch` | `SetPageLayout`, `SetRailUniform`, `ResetPerScopeRailPrefs` | `DetailShell.Render` (`:463`) **and** its rail-resync effect (`:464-472`) — one subscription, two jobs | re-solves the Hero override *and* re-seeds all four scope pairs, clamped exactly as `SeedRail` did at mount | `DetailVerticalHero.cs:577-578` · `DetailShell.cs:463-472` |
| `LyricsPrefs.Epoch` | `LyricsPrefs.Set` (Settings picker **and** both header toggles), `SetLyricsBlur`, `ResetLyricsBlur` | `LyricsView.Render`, `ImmersiveLyricsSurface.Render`, the rail header, the stage header | re-reads mode + strength; two `UseEffect`s then do the real work (rows 21–24 below) | `LyricsView.cs:3022-3023`, `:3067-3071` |
| `PlayerBarPrefs.Epoch` | `SetShowRemaining` (Settings ▸ Playback) and the label's own click | `TimeText.Render` ×2 (player bar + immersive stage) | re-seeds the toggle's `UseState`; the LABEL itself does not need it (row 27) | `PlayerBar.cs:1133-1134`, `:1150-1154` |
| `NpvPlayerPrefs.Epoch` | `SetPresentation` / `SetStyle` / `SetChoice`, from Settings, the NPV header, the flyout, the art context menu, the palette | the pinned hero, the Settings rows, the deck faces | re-reads presentation / style / per-deck choice | `NpvPlayerPrefs.cs:10-11`, `:28-45` |

### 5.2 Per preference — mounted page, parked page, reduced motion

| trigger | target | property | from → to | duration | easing | delay/stagger | reduced-motion | file:line |
|---|---|---|---|---|---|---|---|---|
| **1. Theme** Light / Dark / System (mounted) | every mounted component, in place | every colour token | old palette → new | **250 ms** | the engine's uniform cross-fade window, armed around exactly the flush that runs the re-renders | 0 | **not consulted** — `SetThemeTransition(themeMs)` is unconditional; it is a colour fade, deliberately kept | `SettingsPage.Appearance.cs:208-213` → `AppHost.cs:744`, `:3493-3501` |
| **2. Theme** (parked page) | the parked subtree | same | same | 250 ms | `RethemeAll` reaches parked subtrees, so a parked page is already correct when it reactivates | 0 | same | `AppHost.cs:3500-3501` · `ContentHost.cs:106` |
| **3. Theme** → the OS window material (DWM immersive-dark + Mica) | the window frame | light ⇄ dark | — | **0 ms — instant, no transition** | — | 0 | n/a (Windows owns it) | `AppHost.cs:736-738`, invoked `:3499` |
| **4. Theme = System**, the OS flips | as rows 1–3 | same | same | 250 ms | armed **only when a guarded `Tok` mutator actually advanced `Tok.Epoch`** — Windows broadcasts `ImmersiveColorSet` with no effective change | 0 | same | `WaveeApp.cs:48-59`, gate `:50`, arm `:59` |
| **5. Zoom** (picker, `Ctrl ±`, `Ctrl 0`, wheel) | the whole window | DIP viewport = `clientPx / (osDpi × zoom)` | old scale → new | **snap, no transition** — a full relayout plus a glyph re-raster | — | 0 | already a snap | `SettingsPage.Appearance.cs:222-240` (`FluentApp.SetZoom`) · `AppHost.cs:5249-5254` |
| **6. Zoom Auto** re-resolve (resize / monitor hop) | the same | same | same | **500 ms of nothing, then the snap of row 5** | the debounce is `UseDebouncedValue`, not an easing | 500 ms (`ZoomAutoDebounceMs`) | already a snap | `WaveeShell.cs:591`, `:592-597`, effect `:598-631` |
| **7. Zoom** (parked page) | every parked page | layout | — | snap on reactivation, with no transition | — | 0 | already a snap | `ContentHost.cs:108` |
| **8. Row density**, from the list's own command bar (or its **More** flyout's "Row size" submenu when the bar overflows, `DetailTracks.cs:4370-4372`) | the virtualized list | the list `Key` gains `"d" + density` → **REMOUNT** | old rows → new rows | **snap** | — | 0 | already a snap | `DetailTracks.cs:1090` · writer `DetailShell.cs:382` |
| **9. Row density**, from **Settings** | **nothing on a mounted or parked page** | — | — | — | — | — | — | **the N12 defect**: `SettingsPage.Appearance.cs:242-247` bumps no epoch; `DetailShell` re-seeds `_density` only in a `UseEffect` keyed on the context uri (`DetailShell.cs:372`, key `:375`) |
| **10. Track list style** Modern ⇄ Classic | the virtualized list | the list `Key` gains `":classic"` / `":modern"` → **REMOUNT** | inset pill + corners + border + zebra ⇄ flush rows + hairline | **snap** | — | 0 | already a snap | `DetailTracks.cs:1090` · writer `SettingsPage.Appearance.cs:249-255` |
| **11. Hide track artwork** | the realized rows | the Thumb column leaves the grid; 44 DIP (32 + one 12-DIP gap) returns to the title | lane present → absent | **snap**, and **no remount** — the flag rides `TrackRowsSnapshot`, which is deliberately *not* in the list key | — | 0 | already a snap | `DetailTracks.cs:709`, `:713-717`, contract `:702-706` |
| **12. Marquee** ON ⇄ OFF, player-bar title | the `"np-title"` slot | `Marquee.Of(...)` ⇄ a `TextEl` in a `BoxEl` — a structural swap, both carrying the same `Key` so the slot reconciles | scrolling run ⇄ ellipsis | **snap**; the edge fade leaves in the same frame (it lives on `MarqueeHost`) | — | 0 | `Motion.ReducedMotion` already holds every marquee still, whatever the setting says | `PlayerBar.cs:266-291` · `Marquee.cs:172` |
| **13. Marquee** ON ⇄ OFF, the now-playing ROW | that one row only | same swap | same | **snap**; only the now-playing row re-renders | — | 0 | same | `DetailTracks.cs:2687-2706`, `:2779-2782` |
| **14. Color washes** OFF, shell material | `"shell.material.tint"` | `Fill` | art tint → `WaveeColors.ShellGround @ A 0.03` | **250 ms** (`BrushTransitionMs = WaveeMotion.Standard`) | the engine's brush ramp | 0 | kept — a colour fade | `ShellMaterialLayer.cs:93-98` · `WaveeMotion.cs:59` |
| **15. Color washes** OFF, detail tone plane | `CoverPageTonePlane` | the component returns a **Fill-less** `BoxEl` | tone → nothing | **snap, no transition** — there is no `Fill` left for the 250 ms ramp at `:115` to interpolate | — | 0 | already a snap | `CoverPaletteLeaves.cs:92-94` vs `:113-115` |
| **16. Color washes** OFF, artist blend wash | `CoverArtistBlendWash` | `return new BoxEl();` | wash → nothing | **snap** | — | 0 | already a snap | `CoverPaletteLeaves.cs:178` |
| **17. Color washes** OFF, Home / Home-section / Recents | the wash host's children | `wash = null` → no children | three radials → none | **snap** (only the neutral rect above keeps its 250 ms ramp) | — | 0 | already a snap | `HomePage.cs:217` · `HomeSectionPage.cs:171` · `RecentsPage.cs:310` |
| **18. Track page layout** Automatic ⇄ Hero (mounted) | the detail page's right column | `Key` flips `"tracks:standard:"` ⇄ `"tracks:vertical:"` → **REMOUNT** | two-column + rail ⇄ the hero system | **snap** | — | 0 | already a snap | `DetailShell.cs:509-511`, `:545` · writer `SettingsPage.Appearance.cs:257-262` |
| **19. Track page layout** (parked page) | the parked page | same | same | **snap on reactivation**, with no layout transition | — | 0 | already a snap | `ContentHost.cs:106-108` |
| **20. Keep left-rail same size** / **Clear all remembered sizes** | `railFaded`'s `Width` | `Width = railW` (a plain layout value on a node with **no `Animate`**) | 280 → 240, or back | **snap** | — | 0 | already a snap | `DetailShell.cs:464-472`, `:779-789` · writers `SettingsPage.Appearance.cs:266-291` |
| **21. Lyrics second line** Off / Translation / Romanization | the lyrics viewport | every row's height changes | one run → two | **snap** — the viewport is marked `LayoutDirty \| VirtualRangeDirty` and `ResetScrollSnap()` hard-latches the active line onto the focal band from the freshly measured geometry, zeroing every cascade on the way | one re-arrange, not a tween | 0 | already a snap | `LyricsView.cs:395-399` |
| **22. Lyrics blur** slider move | every line's σ | `BlurSigma` | old σ ladder → new | **snap, in ONE pass** — `Array.Fill(_dofCurrent, NaN)` makes the ramp adopt every target immediately in **both** directions, and a `UseEffect` keyed on the resolved int drives one pass at once so it lands on a paused, tickerless surface too | — | 0 | not consulted (a blur value, not travel) | `LyricsView.cs:355-361`, `:371-373` |
| **23. Lyrics blur** — the ordinary arm the slider bypasses (a line hand-off) | the same σ | `BlurSigma` | rung → rung | **increase: snap.** **decrease: exponential**, τ = `DofRampTauMs` **65 ms** (≈95 % in 200 ms) | `1 − e^(−dt/65)` | 0 | — | `LyricsView.cs:1611`, `:1648-1660`; write gate `DofWriteEps` 0.5, `:1617` |
| **24. Lyrics blur → 0** | every line | `BlurSigma` | current → 0 | **snap, in one pass**, `_dofRampPending` cleared so the machinery is never re-entered; the engine then emits no blur layer at all | — | 0 | — | `LyricsView.cs:1631-1644` |
| **25. Lyrics animated backdrop** ON → OFF | the backdrop carrier's transform | `Affine2D` | the current drift offset → `Affine2D.Identity` | **snap** (`ResetDrift`), and the 33 ms ticker is **unmounted entirely** — not a ticking no-op | — | 0 | reduced motion vetoes the drift independently of the setting (`drift = animated && !Motion.ReducedMotion`) | `ImmersiveLyricsSurface.cs:139`, `:162`, `:165`, `:539` |
| **25b. No cover art at all** | the same carrier | — | — | there is no backdrop and **no ticker**, whatever the setting and whatever reduced motion says: the interval's gate is `enabled: drift && art.Length > 0` (`:165`), and `art` is the normalized current-track image url (`:149`). A track with no image is therefore a THIRD state of this preference, not a degenerate ON | — | 0 | n/a | `ImmersiveLyricsSurface.cs:148-150`, `:165` |
| **26. Lyrics animated backdrop** OFF → ON | the same carrier | translation ±4 % of body W/H, scale ±2 % | still → drifting | continuous, periods **37 s** and **53 s** (incommensurate), ticked every **33 ms** (~30 Hz) | two sinusoids | 0 | same veto | `ImmersiveLyricsSurface.cs:73-77`, `:515-521`; write gates 0.15 DIP / 0.0004 |
| **27. Show remaining time** | the player-bar right label (and the immersive stage's) | the label's text | `-2:18` ⇄ `3:41` | **lands on the next position tick, with NO re-render at all** — the preference is read *inside* the bound `Prop` thunk (FGRP002) | — | ≤ 1 position tick | already a snap | `PlayerBar.cs:1186-1199`; the toggle's own state re-seed `:1150-1154` |
| **28. NPV hero** Cover ⇄ Player | the pinned hero slot | element `Key` swaps `"cover"` ⇄ `NpvDeck.KeyFor(preset) + "@" + side` → **REMOUNT** | cover ⇄ deck | **snap** | — | 0 | already a snap | `NowPlayingPanel.cs:546-548` |
| **29. NPV player style** (preset → preset) | the same slot | the deck key changes → **REMOUNT** (a Cassette must not inherit a Record's mounted deck state) | deck ⇄ deck | **snap** | — | 0 | already a snap | `NowPlayingPanel.cs:545-548` |
| **30. NPV per-deck option** (Finish / Size / Rpm / Sleeve) | the SAME mounted deck | restyles in place off the epoch — **no remount** | skin → skin | **snap** | — | 0 | already a snap | `NpvPlayerPrefs.cs:43-46` · `NowPlayingPanel.cs:545-546` |
| **31. NPV rail resize** (not a preference, but it re-keys the deck) | the deck | `side` is quantised to the 4-DIP grid *because it is in the key* | side → side | **snap, once per 4 DIP of drag** | — | 0 | already a snap | `NowPlayingPanel.cs:541-542` |
| **32. Sidebar design** Classic / V3 / Curated | the pane | the mode remounts under a design-derived `Key`; the width ladder re-seeds against the incoming design's tiers; `SidebarPaneAnim` then animates whatever width was committed, **for free** | 240 ⇄ 300 ⇄ 280 | **300 ms** (`SplitViewPaneDurationMs`) | `EasingSpec.CubicBezier(0, 0.35, 0.15, 1)` (`SplitViewPaneEase`), `SizeMode.Reveal`, `SuppressDescendantTransitions` | 0 | the engine's reduced arm for a layout transition | `WaveeShell.cs:144-145`, `:184-189`, `:519-529` · `SidebarHost.cs:56` |
| **33. Sidebar collapse ⇄ expand** | the pane column | `Width` 56 ⇄ the expanded width | — | **300 ms**, same spec — a clip + translate reveal (compositor-only; no per-tick boundary relayout) | same | 0 | same | `WaveeShell.cs:1016-1019` |
| **34. A sidebar / rail seam DRAG** | every layout transition in the window | all of them | animated → snapped | **0 ms for the drag's duration** | `Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, …)` | 0 | stronger than reduced motion, deliberately | `WaveeShell.cs:134-135`, armed `:540` |
| **35. The detail rail grip below its floor** (the resist zone) | the rail subtree | `Opacity` | 1.0 → `MinFade` **0.35** | **drag-driven, no duration** — a bound compositor opacity written 1:1 from the pointer across `FadeDistance` **44** DIP of travel | `SplitterMath.Fade(into, 44, 0.35)`, linear in travel | 0 | not consulted (it is a direct-manipulation cue, not an animation) | `Splitter.cs:68-70`, `:279-281` · `DetailShell.cs:787` |
| **35b. The grip's own INDICATOR** (every seam in the app: detail rail, shell rail, docked-video height, sidebar) | the 2-DIP `PartIndicator` thumb | `Opacity` → `HoverOpacity` / `PressedOpacity` | invisible at rest → revealed | `Motion.ControlFast` (WinUI `ControlFasterAnimationDuration`) | the kit's reveal tween | 0 | the engine's own reduced arm for a control reveal | `fluent-gpu/.../Splitter.cs:27-36` (`IndicatorW` 2, `IndicatorInset` 4, `IndicatorFill = Tok.FillControlStrong`); the shell rails pass `ShowIndicator: false` and rely on the `SizeWE`/`SizeNS` cursor alone (`:57-58`) |
| **36. The rail COLLAPSES** (push past `ForcePush` 44, raw ≈ 136) | the row's child 0 | `railFaded` → `DetailRail.BuildCompact(m, 96, ExpandRail)` | 180 → 96 | **snap** — a structural swap. `right` keeps its `Key`, so the track list reconciles in place and its scroll offset survives | — | 0 | already a snap | `DetailShell.cs:750-752`, `:756-770` |
| **37. Any of the above on a KeepAlive-PARKED page** | the parked subtree | — | — | the `Render` runs when the subscription fires, so the page is already correct; reactivation plays **no** layout transition | — | 0 | — | `ContentHost.cs:106-108` |

### 5.3 The remounts, listed once

Four preferences change the element *shape* a frozen template cannot express, so they are served by a `Key`
change rather than a transition. This is the exhaustive list, and a 0.3 owner must not add a fifth:

| preference | what remounts | the key | cost | file:line |
|---|---|---|---|---|
| Row density | the virtualized track list only | `"d" + density` inside `"list:" + route + …` | the viewport re-realizes; the outer scroller's offset is the list's own | `DetailTracks.cs:1090` |
| Track list style | the same list | `":classic"` / `":modern"` in the same key | same | `DetailTracks.cs:1090` |
| Track page layout | the whole right column | `"tracks:vertical:"` / `"tracks:standard:"` + route name | deliberate: reconciling a hero-system list against a standard one painted the previous page's collapsed-header signals for a frame | `DetailShell.cs:545` |
| NPV presentation / style | the pinned hero | `"cover"` / `NpvDeck.KeyFor(preset) + "@" + side` | deliberate: a Cassette must not inherit a Record's mounted deck state | `NowPlayingPanel.cs:546-548` |
| Sidebar design | the sidebar mode subtree | the design's mount key | the pane width then animates for free (row 32) | `SidebarHost.cs:56` · `SidebarDesign.cs` mount keys |

**Tier is deliberately NOT in any key.** It used to be, and the remount reset the scroll offset on every rail
toggle; the fix was a memo the rows read instead (`_rowShape`, `DetailTracks.cs:1071-1080`). Do not reintroduce it.
**Sort is not in the key either** (`:1071-1073`) — each bound row re-skins itself to the new order through its
sort-subscribed binds, so a header click preserves the scroll offset.

**What else IS in that same key, and is not a preference:** `vh:` (the vertical arm), `:q<query>:f<filtersHash>`
(two-column arm only), `:r<resetEpoch>` (a curated re-cut) and `:rec` (the recommendations template, which must
ride a value that cannot change while mounted). Full expression at `DetailTracks.cs:1089-1090`.

### 5.4 Reduced motion

`Motion.ReducedMotion` is an **OS value that is read**, never an app preference that is branched (N10), and it
is read at the point of consumption:

| where | what it does | file:line |
|---|---|---|
| the immersive lyrics backdrop | `drift = animated && !Motion.ReducedMotion` — vetoes the drift *independently* of `LyricsAnimatedBackdrop`, and the ticker is then never mounted | `ImmersiveLyricsSurface.cs:139`, `:165` |
| every marquee | `canScroll = Sty.Enabled && overflow && !Motion.ReducedMotion` — holds the run still whatever `MarqueeEnabled` says | `fluent-gpu/.../Marquee.cs:172` |
| every hover / press scale | the three `ScaleTier` accessors return `1f`, which makes the recorder's `abs(isc − 1) > 0.0008` test fail and skips the transform entirely | `WaveeMotion.cs:17-30`, `:179-182` |
| every list/shelf entrance | `WaveeEntrance.DelayMs(index)` returns 0; the engine's `ReducedSnap` parks the rise and the blur at their end state and still cross-fades opacity | `WaveeMotion.cs:115-122` |
| the theme cross-fade | **nothing** — it is not consulted, and that is correct: a fade aids orientation and is not travel | `AppHost.cs:3499-3501` |

**Reduced motion is a VALUE, never a branch.** Gating an entrance *hook* on the flag changes the hook COUNT
between renders and crashes the reconciler the moment the flag flips mid-session — and a resize grip flips it
(`WaveeMotion.cs:87-89`). Every row above reads the flag inside `Render` or inside an accessor; none of them
adds or removes a hook. 0.3 must keep that shape, and must not add a Wavee "reduce motion" setting that competes
with the OS one.

---

## 6. Interaction

### 6.1 Cross-effects between preferences (X1–X18)

**Combinations that change the outcome — the re-author must handle these explicitly.**

**X1 Hero × the rail preferences.** Hero never composes a rail, so `DetailRailUniform`, the four
`(width, collapsed)` pairs and the uniform pair are all **inert** while Hero is selected. The Settings UI
already encodes this: both sub-rows are composed away when `pageLayout != PageAuto`
(`SettingsPage.Appearance.cs:504`). They are *preserved*, not cleared — switching back to Automatic restores
every remembered width. Do not "simplify" this into clearing them.

**X2 Uniform × per-scope memory.** The uniform pair is a **fifth** key pair, never a reuse of the album's
(`AppSettings.cs:167-171`). Turning uniform ON must not write the four; turning it OFF must not read the fifth.
`DetailShell.ResyncRail` re-seeds all four on every epoch bump so an open page reflects "Clear all remembered
sizes" immediately rather than on the next launch (`DetailShell.cs:163-172`).

**X3 Rail width × layout mode.** The persisted width and collapsed flag apply at mode 0 only (N8). A user who
collapses the rail on a wide window and then narrows to mode 1 sees the 224-DIP rail return; widening restores
the collapse. This is intentional and tested.

**X4 Rail width × the rail's own content floors.** Dragging to the 180 floor gives `CoverEdge = 156`. The
title still auto-fits (`MinSize 18`, `≤3 lines`), the CTA cluster wraps as `[Play]` over `[♡][↗][⋯]`, and the
fact bentos wrap-grow. Below that the rail's own `ScrollView` is the last resort — the cover never shrinks for
height (`DetailRail.cs:119-121, :268-275`).

**X5 Row density × row style.** Not independent: the ladder is a single function of both, and Classic is
uniformly shorter (36/40/44/48 vs 40/48/56/64). Classic also ignores the artwork size entirely, because it has
no thumb lane.

**X6 Row style × hidden artwork.** `HideTrackArtwork` is a **no-op in Classic** — the Thumb gate is
`!classic && showArtThumb && !artworkHidden && tier < 5` (`DetailTrackTableRules.cs:37`). A chapter that shows a
"Classic + hidden artwork" state must show it as identical to plain Classic.

**X7 Row style × per-surface config.** `ShowArtThumb` is false on album surfaces (`DetailConfig.cs:205`), so
neither the density art ladder nor `HideTrackArtwork` changes an album page's rows at all. The setting's visible
effect is confined to playlist / Liked / local / search / library / queue / artist rows.

**X8 Plays × Tempo × width.** Both are gated by tier *and* by the relief ladder, and the relief ladder drops
them **first** (Plays step 1, Tempo step 2). At a squeezed width both toggles can be ON and neither column
visible. A chapter must not draw "PlaysColumn = true" as an unconditional lane.

**X9 Plays × surface.** On album/single/compilation the Plays lane is always present (`ShowPlays: true`) and
the setting is not even offered (`PlaysColumnOptIn: false`). `ShowPlays` also drives the top-track star and
`DetailTrailing.SeedTrack`, which is exactly why the opt-in is a **different knob** — turning on a playlist's
Plays column must not drag album semantics onto it (`DetailConfig.cs:174-179`).

**X10 Colour washes × theme.** Not independent: both the tint formula and the tone plane alpha branch on theme
(light `Lift(TextBase) @ 0.05`, dark `TintedDark @ 0.14`; plane 0.30 light / 0.20 dark). With washes OFF the
neutral fill is still theme-dependent (`#EDEDED@3%` / `#202020@3%`). A "washes off" wireframe must be drawn in
both themes or labelled with which one.

**X11 Colour washes × Liked cover style.** On the Liked page the tone anchor is the *treatment's own lead tile*
when a dynamic treatment is composing, and the generic first-gradeable-track answer otherwise
(`DetailShell.LikedToneAnchor`, `:846-857`). So changing `LikedCoverStyle` from Stock to any treatment can
change the page's **ground colour**, not just its cover — but only while washes are ON.

**X12 Zoom × every breakpoint.** Zoom divides into the DIP viewport (W23), so it moves the sidebar tier, the
detail mode, the table tier, the relief step, the player-bar tier and the hero's `winH >= 900` rung all at once.
Any chapter that pins a number to a window pixel width is wrong; pin it to a DIP viewport width and say so.

**X13 Zoom mode × manual zoom.** In Auto, a manual zoom (a chord, the wheel, the picker) is detected on the
next resize-settle by `|live − lastAuto| > 0.004` and silently flips the mode to Manual
(`WaveeShell.cs:624-628`). A user cannot be fighting the auto-derivation.

**X14 Lyrics second line × blur strength.** The secondary run is a *sibling inside* `dofContent`
(`LyricsView.cs:2843-2847`), so it inherits the row's σ, emphasis opacity and scale. Blur 0 flattens both lines
equally; there is no "sharp translation under a blurred lyric" state.

**X15 Lyrics backdrop × reduced motion × blur.** Three independent inputs to one surface: the backdrop drift
(setting × OS reduced motion), the depth-of-field σ (blur strength), and the halo (blur strength). Blur 0 with
the backdrop ON still drifts; reduced motion with blur 100 still blurs.

**X16 Marquee × row style × now-playing.** The marquee exists on exactly one row — the now-playing one — and
swapping plain ⇄ marquee re-renders only that row (`DetailTracks.cs:2779-2782`). Classic and Modern both get it.

**X17 Sidebar design × pane width.** Changing design does **not** carry the width across: each design restores
its own remembered width, and only if that design's `WidthUserSet` has latched (`SidebarDesign.cs:101-109`). The
tier defaults differ (240 / 300 / 280), so the content region's width changes when the design does — which in
turn can move a detail page across a layout mode.

**X18 Sidebar width × detail mode.** The detail page's own width is the window minus the sidebar minus the
right rail. A 460-DIP sidebar and a 500-DIP right rail take 960 DIP off the window before the detail page sees
any. This is the single most common way a user reaches mode 2 or 3 without resizing the window.

**Explicitly independent — do NOT invent coupling:**

- `ThemeMode` × every layout preference. Theme changes colours and `Elevation.Card` only; no geometry moves.
- `MarqueeEnabled` × `RowDensity` / `TrackRowStyle` / `HideTrackArtwork`. The marquee's box is the title cell,
  whatever size that cell is.
- `PlayerBarShowRemaining` × everything. It is one 44-DIP slot's content.
- `LikedCoverStyle` × `RowDensity` / row style / columns. The cover and the table share no geometry.
- `LyricsBlurStrength` / `LyricsSecondaryLine` / `LyricsAnimatedBackdrop` × the detail surface. Different pages.
- `NpvPresentation` / `NpvPlayerStyle` × the detail surface. The deck lives in the right rail.
- `DetailPageLayout` × `RowDensity` / `TrackRowStyle` / `TempoColumn` / `PlaysColumn`. The vertical/Apple-Music
  table profile follows **the same tier gates** as the standard one for Heart, By, Date, Video and Plays —
  three separate user reports fixed hard-false branches there, because the hero system is forced at *every*
  width and a lane hidden "because narrow" was hidden on a 1400-DIP page (`DetailTracks.cs:472-497`).
- `UiCulture` × layout. No layout branches on culture; only string lengths change.
- `ShellRailWidth` × `DetailRailUniform`. Two different rails, two different key families.

### 6.2 Keyboard

| chord | what it does | where it is handled | notes |
|---|---|---|---|
| `Ctrl +` / `Ctrl =` | steps UP one `ZoomLadder` rung | `WaveeShell.ZoomStep` (a static verb with no `IAppSettings` in hand) | it **cannot** flip `ZoomMode` itself; the Auto effect detects the disagreement on the next resize-settle and writes Manual (`WaveeShell.cs:624-628`) |
| `Ctrl −` | steps DOWN one rung | same | same |
| `Ctrl 0` | back to 1.00 | same | same |
| `Ctrl + wheel` | steps the same ladder | the Ctrl+wheel hook | the persist is debounced 2 s (`WaveeApp.cs:239-244`), so a spin writes the registry once |
| `Tab` / arrows inside a picker strip | moves between the density / track-style / page-layout / Liked-cover preview cards | `WaveePicker.Strip` → `FluentGpu.Controls.RadioButtons` owns the group keyboard contract | the card **is** the radio — there is no separate radio control to focus. **SELECTION FOLLOWS FOCUS** (the WinUI `RadioButtons` contract): `Strip` fires `onChange` on every keyboard ROVE, not only on a commit, so arrow-keying through the cards **applies and persists each preference in turn**. Every writer this chapter names must therefore be idempotent and cheap — `LikedCoverPicker.Apply` documents exactly that, and treats roving AS the preview because the full-size cover behind the flyout repaints on the Bump (`WaveePicker.cs:258`, `:271-277` · `LikedCoverPicker.cs:180-188`). A 0.3 writer that, say, journals or re-fetches on write would fire once per arrow key |
| `Left` / `Right` / `Up` / `Down` inside a strip | column-major traversal | `RadioButtons.PartGrid` (`Wrap = true`) | the strip is WinUI's `ColumnMajorUniformToLargestGridLayout`, so items fill the first column before the second — arrow direction does not map to visual order the way a row would (`WaveePicker.cs:250-253`, `:263`) |
| `Escape` on the immersive lyrics stage | leaves the surface | `ImmersiveLyricsSurface` takes focus once at mount and keeps the root focusable so a background click returns focus | `ImmersiveLyricsSurface.cs:154-158` |

No preference in this chapter has a dedicated accelerator other than zoom.

### 6.3 Pointer

| gesture | target | result | file:line |
|---|---|---|---|
| drag the detail **rail grip** | `Splitter` over `rail.Width` | writes the width signal 1:1 while down; at/below 180 it **resists** (`Resist` 0.28) and the rail fades to `MinFade` 0.35 across 44 DIP; past `ForcePush` 44 (raw ≈ 136) it collapses; the store is written **only on release**, to this scope's own pair | `DetailShell.cs:797-817`, commit `:804-808` · `Splitter.cs:265-300` |
| drag the grip from **collapsed** | the 20-DIP seam | does not re-open until a pull past `ReExpand` 220 — comfortably above the 136 collapse point, so the rail cannot flicker shut/open at the seam | `DetailShell.cs:194-203`, `:206` |
| click the **collapsed cover** | `DetailRail.BuildCompact`'s expand gesture | re-opens immediately at the remembered width; the whole 96-DIP strip is `ToolTip.Wrap(strip, m.Title)` | `DetailRail.cs:304`, `:316-321` |
| drag the **shell rail** splitter | `ShellUi.RailWidth` | clamped 200…500, committed on release | `WaveeShell.cs:453-458` |
| drag the rail's **vertical** splitter | the docked-video cap | floor `railW × 9/16`, ceiling 560, committed on release | `RightRail.cs:206-228` |
| drag the **sidebar seam** | the pane width | latches `WidthUserSet` for that design on the first committed drag only — collapse/expand never sets it; every layout transition in the window is snapped for the drag's duration | `SidebarDesign.cs:118-133` · `WaveeShell.cs:134-135`, `:540` |
| click the player bar's **right time label** | `ToggleDuration` | flips `PlayerBarShowRemaining` and bumps `PlayerBarPrefs`; `Cursor.Hand`, hover/pressed fills. The **left** elapsed label is inert | `PlayerBar.cs:1200-1207`, `:1216-1220` |
| click either lyrics surface's **second-line toggle** | `LyricsPrefs.Set(Next(mode, available))` | cycles none → translation → romanization → none, **skipping absent layers**; hidden entirely when the document has neither | `LyricsView.cs:3044-3054` · `RightRail.cs:373-421` · `ImmersiveLyricsSurface.cs:355-398` |
| right-click the **NPV hero art** | `NpvArtMenu` | the same popup the gear owns (`StyleFlyoutOpen` is shared), so both entry points agree | `NowPlayingPanel.cs:552-554` · `NpvPlayerPrefs.cs:13` |
| the track list's **More** flyout | `DetailHandlers.TempoColumn` / `PlaysColumn` | the only writers for those two keys — they are not Settings rows. Both rows are CONDITIONAL: Tempo only where `cfg.ShowTempo` (`:4374`), Plays only where `cfg.PlaysColumnOptIn` AND both handlers are non-null (`:4382`) | `DetailTracks.cs:4374-4387` |
| the same **More** flyout, when the command bar has overflowed | `DetailHandlers.Density` / `SetDensity` | "Row size" rides in as a SUBMENU (`ListButton.ItemsFor`) whenever `DetailTrackInlineCommand.Density` is in the overflow set — so the in-page density control does not disappear at a narrow width, it moves. `DetailTrackCommandBarLayout` decides which verbs overflow | `DetailTracks.cs:4370-4372` |
| click a track-table **column header** | `DetailHandlers.SetSort` | cycles that column's sort and persists it to `detail.sort.{col,desc}:<ctxUri>` — per context, never app-wide; the descending caret is drawn in the trailing `NumCaretSlot` (9 DIP) so turning the indicator on never nudges `#` off the row numbers beneath it | `DetailShell.cs:376-381` · `DetailTrackTableRules.cs:236-239` |

### 6.4 Focus, tooltips and the honest-copy rules

- **A picker never goes in an expander HEADER.** The header content slot lands in a `SettingsCard`'s right-hand
  Auto grid track, which starves the header text track toward zero once the content is wider than the card — and
  a zero-width text run neither wraps nor clips, so the header paints straight over the content. The header
  carries the ANSWER via `SettingsValueTag`; the pickers live in `ItemsHeader`
  (`SettingsPage.Appearance.cs:16-21`).
- **Collapsed by default.** Row density, Track list style, Track page layout and Sidebar design are all
  `SettingsExpander`s whose collapsed header states the current answer
  (`SettingsPage.Appearance.cs:411-417`, `:452-458`, `:485-493`).
- **A destructive-looking no-op is worse than no button.** "Clear all remembered sizes" is enabled only when
  `DetailRailPolicy.HasCustomizedRailPrefs` says something has actually moved, and it goes through
  `ConfirmThen(...)` (`DetailRailPolicy.cs:58-66`; the call `SettingsPage.Appearance.cs:167-174`, the confirm `:361-365`).
- **An "Auto" link with nothing to reset is a dead affordance.** The lyrics-blur Auto link is composed only while
  the setting is pinned to an explicit value (`SettingsPage.Appearance.cs:148-153`).
- **The one honest "restart" copy in the app.** `Strings.Settings.Language.RestartSub` = "Changes are applied the
  next time Wavee starts." (`assets/loc/en-US.json:790`) — N2. 0.3 may keep the restart, but it must keep the
  sentence with it.
- **The cycling toggle's tooltip names the state it is in NOW**, not the state it will go to — a tooltip that
  named its next state would read as a lie the moment the user hovered it after clicking
  (`LyricsView.cs:3056-3063`, the switch at `:3058`).

---

## 7. Data & readiness in 0.3 terms

### 7.1 Persistence

`HKCU\Software\Wavee\Wavee\Settings` (`AppDataStore.ForUnpackaged("Wavee", "Wavee")`,
`fluent-gpu/.../AppDataStore.cs:38-48`; call site `App/Services.cs:580`). Value types:
`REG_SZ` for strings, `REG_DWORD` for `int`/`bool`, `REG_QWORD` for `long` **and for `float`/`double`** (the
IEEE-754 bits, `AppDataStore.cs:71-72`). A type mismatch reads as the fallback, never as reinterpreted garbage
(`:23`). Every access in `AppDataSettings` is defensive — a storage failure returns the key's default and never
throws into the UI (`AppSettings.cs:446-494`). Packaged builds write into the package's `LocalCache`; the
startup log line prints `logResolved=` for exactly this reason (CLAUDE.md).

0.3 keeps this store and these names verbatim: **the storage names are the wire**. Renaming
`detail.rowdensity` loses every existing user's row density silently. Keep the `SettingKey<T>` record
(`AppSettings.cs:10`) and the "one registry of what the app remembers" discipline (`:20-21`) — in 0.3 that
registry is a `static class Keys` section of `Platform.cs`.

Two persisted-guard patterns must survive because `IAppSettings` has no key-exists probe (an absent key is
indistinguishable from one written as its default): the **monotonic bootstrap version** (`SidebarBootstrapVersion`,
`SetupBootstrapVersion`, `ZoomModeBootstrapVersion`) and the **userSet latch** (`SidebarWidthUserSet`). Both are
what let a factory-reset profile re-arm a one-time migration instead of skipping it forever.

**The `settings is null` arm is a real, composable state, not a null-check.** Every reader in this chapter takes
`IAppSettings?` and falls back to the key's own `Default`, and the Settings surface goes further: the Zoom
ComboBox, the NPV style ComboBox, the Language ComboBox and the lyrics blur slider are all
`isEnabled: settings is not null` (`SettingsPage.Appearance.cs:344`, `:402`, `SettingsPage.General.cs:85`,
`SettingsPage.Appearance.cs:389`), every `Set*` writer opens with an early return, and `AppearanceToggle`'s
`onChange` no-ops (`:128`). That is what lets the page mount in isolation — a harness, a unit render, a
pre-`Services` frame — showing every default with nothing writable. 0.3 must keep the shape: an optional store,
key defaults as the fallback, and DISABLED controls rather than a page that throws or paints blank.

### 7.2 DATA GAPS

What a 0.3 owner cannot check offline, or cannot check at all, and what has to be seeded to close the gap.
Every row names the fixture, not just the hole.

| # | gap | consequence for this chapter | close it by |
|---|---|---|---|
| **DG1** | **`--fake` installs `NoLyricsProvider`** (`App/Services.cs:624`), so no lyrics document ever lands | W25 (second line / blur / backdrop) and parity items 43–45 are **live-only** — three preferences with no offline arm at all | wire the already-existing, already-complete `FakeData.Lyrics` (a 40-line word-synced document, `Wavee.Core/Fakes/FakeData.cs:543-567`) into the fake `Services`. It has **no caller today**; the fixture is not missing, the wiring is |
| **DG2** | `FakeData.Lyrics` — **UNVERIFIED** whether it carries a translation or romanization layer | even with DG1 closed, the second-line cycle may have nothing to cycle through, and `LyricsPrefs.Available` would be 0 (the toggle is then **hidden**, which is itself a state worth checking) | read the fixture; if it has neither layer, add one — the "no layers ⇒ no toggle" state must still be reachable, so seed a second document that has none |
| **DG3** | `FakeData` has **no prerelease album and no daylist window** (grep for `IsPreRelease` / `prerelease` / `ExpiresAtMs` returns nothing) | W14's countdown card and the hero's `PulseRowHeight 28` row cannot be checked offline | seed them in `Entities.SeedFake()` (Wave 5, owner Q), or mark those two parity items live-only |
| **DG4** | fake cover art is generated at **64 px** (`FakeData.Cover(i, 64)` inside `Track(i)`, `FakeData.cs:58`) over **16** distinct files (`CoverCount = 16`, `:12`) | every Liked treatment composes in `--fake` (16 distinct tiles clears every `MinTiles`, max 8) — but a 304-DIP Lens drawn from 64-px tiles is visibly soft, so W24 is checkable for **layout** and not for **fidelity** | raise the liked-list fixture's decode to at least the treatment's own bucket (wall 64 · grid 128 · hero 256, `LikedCoverTreatments.cs:40-54`) |
| **DG5** | **UNVERIFIED**: whether `SpotifyLive.CoverColorPlane.CanGrade(url)` accepts the fake covers' **local file paths** | if it does not, every colour wash, the detail tone plane and `LikedToneAnchor` are all neutral in `--fake`, and parity items 35–36 and the whole of §4 cannot be checked offline | read `CanGrade`; if local paths are refused, either accept them for the fake source or state in §10 that washes are live-only |
| **DG6** | the **nl** and **ko-KR** tables' key coverage is unknown, and both are shipped **disabled** in the picker (`SettingsPage.General.cs:32-43`) | no chapter can derive a real "long string" state from them | use a synthetic long string; do not derive a wireframe from an incomplete table |
| **DG7** | **`ZoomAutoMode.Dense` (2) has no UI writer anywhere** in `src/apps/Wavee` | the mode is reachable only by hand-editing `appearance.zoom.mode`; its `DenseFloor` 0.75 arm is untested by any user path | 0.3 decides: give Dense a picker item, or delete the mode and the `MigrateMode` arm that tolerates it |
| **DG8** | **`sidebar.curated.rail.labels` is a dead key** — no reader and no writer exist (only `AppSettings.cs:434` and the test shim `TestAppSettingsShim.cs:126`) | nothing renders differently for it, ever | delete it in 0.3 (the rail's design position is that the tooltip IS the label, `SidebarPaneRail.cs:18-20`) |
| **DG9** | there is **no key-exists probe** on `IAppSettings` — an absent key is indistinguishable from one written at its default | a factory-reset profile would silently skip every one-time migration | keep BOTH persisted-guard patterns: the monotonic bootstrap version (`SidebarBootstrapVersion`, `SetupBootstrapVersion`, `ZoomModeBootstrapVersion`) and the userSet latch (`SidebarWidthUserSet`) |
| **DG10** | the settings store holds `float` as **REG_QWORD IEEE-754 bits** (`AppDataStore.cs:71-72`) | a rail width or zoom level cannot be set by hand in `regedit` for a test run | reset a parity run with a factory reset, never by editing float keys |
| **DG11** | **no RTL and no long-string layout arm exists** anywhere in `src/apps/Wavee` (a repo-wide grep for `FlowDirection\|RightToLeft\|RTL\|BiDi` returns no layout code) | §2.4's overflow answers are the whole story: ellipsis or marquee, per element | state it as a position (0.3 does not mirror layout), not as a gap to be quietly filled |
| **DG12** | the queue panel reads `TrackRowStyle` but **not** `RowDensity` (44 classic / 64 modern) | a 0.3 owner who unifies "row extents" across surfaces will silently make the queue follow density | keep the two reads separate, and put it in chapter 21's parity list |

### 7.3 Readiness — what a preference looks like before its data has landed

A preference is read at composition; the data it decorates is not. Five places in this chapter have a genuine
"chosen, but not yet drawable" window, and each one already has an answer that must be ported rather than
re-invented.

| # | preference | what is not ready | what 0.2.9 draws meanwhile | file:line |
|---|---|---|---|---|
| **R1** | `LikedCoverStyle` | the liked LIST (`LikedCoverRules.Tiles` needs the track rows) | `Effective(requested, tiles.Count)` degrades to **Stock**, i.e. the bundled PNG — never a blank square and never a half-filled treatment. The picker is the second thing that charges the warm (`store?.EnsureLiked()`, an idempotent guarded one-shot, never a write during render) | `LikedCoverRules.cs:139-156` · `LikedCoverPicker.cs:166-170` |
| **R2** | `LikedCoverStyle` = Lens (and every `BakedBlur` treatment) | the **baked blur derivative**, which is asynchronous: the sharp tile paints first and the blur lands a beat later | the ZStack order is the answer — the blurred GROUND sits UNDER the crisp heart window, so the arrival is a fade behind a fixed shape. The other order flashes the whole cover sharp and then smears it, which reads as a bug. This ordering is a REQUIREMENT, not a style choice | `LikedCoverTreatments.cs:258-261` |
| **R3** | `TempoColumn` | extended-metadata **kind 222** (tempo / key / camelot) | the lane is present and the CELL renders **empty** — not "0 BPM", not an em dash: a placeholder that flickers to a real value a moment later is worse than nothing. The row's expander states tempo/key regardless, so the setting hides a COLUMN, never the data | `TrackRow.cs:580-586` |
| **R4** | `PlaysColumn` | kind **185** | an **em dash** (`TrackRow.Dash`) when the count is 0 or the track is not out yet — unlike R3, because "we do not have a count" is a fact the row can honestly state, while "0 BPM" is not: rendering 0 would state a fact the app does not have. Crucially the FETCH is never gated on the setting — N6 | `TrackRow.cs:166`, `:294-298` · `AppSettings.cs:84-89` |
| **R5** | every detail layout preference | the MODEL, while the page shimmers | the skeleton reserves the hero band from the **same pure resolver** the live hero uses (`HeroBandHeight`, a `TitleTypePlan` built for `title: null` = a one-line title at the fluid cap), and the vertical/hero arm's shimmer leads with that band plus the REAL chrome element, because there hero and chrome are prefix items INSIDE the virtualized boundary. The two-column arm's rail and chrome are siblings outside it, so only the rows shimmer | `DetailVerticalLayout.cs:552-579` · `DetailTracks.cs:1058-1069` |

The rule behind all five: **a preference may change what is drawn once the data is there; it may never change
whether the page reserves the space, and it may never be the reason a fetch does or does not happen** (N6).

---

## 8. Pure rules to port verbatim

Every one of these is engine-free by construction and is already unit-tested without a component harness. They
are the cheapest part of the whole rebuild and the part most likely to be "simplified" by accident.

| Class | 0.2.9 file | What it decides | Tests | 0.3 destination |
|---|---|---|---|---|
| `DetailLayoutBreakpoints` | `Features/Detail/DetailLayoutBreakpoints.cs` (88) | the mode ladder (820/660/560, vertical 540/580), the tier ladder (860/720/560/440/340/300), both hysteresis rules, the pre-measure page-width estimate, `ContentMinWidthForMode` | `Wavee.Tests/DetailLayoutBreakpointTests.cs` (121) | `Entities/Album.Page.cs` is the wrong home — put it in **`Platform/Design.cs`** as `Design.DetailBreakpoints`, because album, playlist, Liked, show and local all read it |
| `DetailRailPolicy` | `Features/Detail/DetailRailPolicy.cs` (67) | `RailScope`, `ResizableFor`, `DefaultWidthFor`, `MinWidthFor`/`MaxWidth` (180/480), `ClampStored`, `ScopeFor` (the uniform resolution), `HasCustomizedRailPrefs` | `Wavee.Tests/DetailRailPolicyTests.cs` (113) | `Platform/Design.cs` |
| `DetailVerticalLayout` | `Features/Detail/DetailVerticalLayout.cs` (662) | the whole hero geometry: `RowFlow` + its 424/400 hysteresis, `HeroPadFor`/`HeroGapFor`, `ArtworkFor`, `ContentWidthFor`/`TitleWidthFor`, the **title type plan** (`FluidTitleCapFor`, `SnapTitleSize`, `TitleAdvanceEm`, `TitleLongestWordEm`, `TitleHeightBudgetFor`, `TitleTypeFor`, `StableTitleSize`), `IdentityHeightFor`/`IdentityGapFor`, `HeroBandHeight`, `CollapseDistance`, `ExpandedFadeStart`, `CompactRevealStart`, `ArtworkDecodePx`, the slot map (`PrefixCount`/`ItemRole`/`FooterIndex`/`ItemCount`) | `Wavee.Tests/DetailVerticalLayoutTests.cs` (615), `DetailVerticalFooterTests.cs`, `DetailSkeletonGeometryTests.cs` (232) | `Platform/Design.cs` (geometry) — the slot map belongs with whatever owns the vertical list in `Entities/Track.UI.cs` |
| `ContextBandLayout` | `Features/Detail/ContextBandLayout.cs` (179) | the 56-DIP sticky band: `Height`/`ClipInset`/`ClipFadeBand`, cluster and pivot gaps, `PivotPadX`/`ActionPadX`, the underline, `TitleCap`, `AvgCharW`/`EstimateLabelWidth`/`ActionsWidth`, and the **scroll spy** (`SpyProbe` 8, `SpyViewportFraction` 0.25, `IsAtScrollEnd`, `SpyLine`, `ActiveSection`, `ScrollTargetFor`) | `Wavee.Tests/ContextBandLayoutTests.cs` (214) | **`Entities/Detail.cs`** (CORE), with the band's UI half — `Detail.Band` / `BandTitle` / `BandByline` / `BandAnchor` / `Detail.Pivot` — in **`Entities/Detail.UI.cs`**; owner **M**, Wave **4.5**. **Not `Platform/Design.cs`** (arbitration 2026-09-12): the band's geometry is detail-frame arithmetic, not shell or token arithmetic — `ClipFadeBand` *is* `DetailVerticalLayout.StickyFadeBand` (`ContextBandLayout.cs:44`) and `Height` 56 *is* `CompactIdentityHeight` (`DetailVerticalLayout.cs:78`), by construction, and the class is declared in `namespace Wavee.Features.Detail`. The detail hero and the artist page **consume** it; neither writes it. See `03-detail-frame.md:880` and `08-artist-and-discography.md:1107` |
| `DetailTrackTableRules` + `TrackLane` | `Features/Detail/DetailTrackTableRules.cs` (279) | `RowHeightFor`, `HeaderHeightFor` (36 Modern / `ClassicHeaderHeight` 32), `ArtSizeFor`, `IdentityColumns` (incl. `ClassicArtistFoldTier` **4** — Classic's Artist lane folds into the Title subline below a 440-DIP column), `TrailingColumns`, `ShowClassicInlineVideo` (+ `ClassicInlineVideoDropTier` **4**), the sort cycle, the **relief ladder** (`MinWidthFor`, `NominalReliefFor`, `ReliefFor`, yield order, `MaxRelief` 8, `ReliefHysteresisDip` = the tier ladder's own 24), `PreviewScale`, and the lane-width table (Num 28 with `NumCaretSlot` 9, Heart 28, Title floor 120, Artist floor 90, Album floor 90, By 132, Date 88, Plays 52, Tempo 80, Duration 52, Video 28, Actions 40, Expand 26, Thumb 32 — the three "floors" are what `MinWidthFor` charges STAR tracks, never widths the grid hands out) | `Wavee.Tests/TrackRowStyleRulesTests.cs` (374) | `Entities/Track.UI.cs` — it is the row's own rule set |
| `DetailHeaderMergeRules` | `Features/Detail/DetailHeaderMergeRules.cs` (39) | `IsRollingIdentity`, `ResolveTitle`, `ResolveIncomingCover` — the initial-load merge for a rolling-identity container (daylist) | `Wavee.Tests/Actions/DetailHeaderMergeRulesTests.cs` (68) | `Entities/Playlist.cs` (CORE) |
| `ZoomAutoPolicy` | `App/ZoomAutoPolicy.cs` (114) | `ZoomAutoMode`, `DesignW` 1600 / `DesignH` 900, `Ceiling` 2.0, `DenseFloor` 0.75, the `Plateaus` subset, `Suggest`, `SnapPlateauDown`, the base-extent recovery, `MigrateMode` | `Wavee.Tests/ZoomAutoPolicyTests.cs` (178) | `Platform/Platform.cs` — it runs before the window |
| `LyricsBlurPolicy` | `Features/Player/LyricsBlurPolicy.cs` (37) | `Auto` −1, `WeakGpuDefault` 40, `StrongGpuDefault` 100, `Resolve`, `Enabled`, `Scale` | `Wavee.Tests/LyricsBlurPolicyTests.cs` (58) | `Shell/Lyrics.cs` (CORE half) |
| `LyricsPrefs` (the pure half) | `Features/Player/LyricsView.cs:3020-3072` | `Clamp`, `BitFor`, `Next` (the cycle that skips absent layers), `Tooltip` | pinned inside the lyrics tests | `Shell/Lyrics.cs` (CORE) — the `Epoch`/`Available` signals go to `Design.Prefs` |
| `LikedCoverRules` | `Features/Detail/LikedCoverRules.cs` (307) | `LikedCoverStyle`, `FromSetting`/`ToSetting`, `MinTiles`, `Effective`, `Tiles`, `MaxTiles` 16, `MosaicCells`, `Site`, `FillCells`, `WallCellIndex`, `RainbowColumns`/`RainbowOrder`/`HueOf`, `ToneAnchorUrl`, `PickerOrder` | `Wavee.Tests/LikedCoverRulesTests.cs` (568) | `Entities/User.cs` (the liked collection is a `User` edge in 0.3) |
| `ShellResponsiveLayout` | `Features/Shell/ShellResponsiveLayout.cs` (247) | `NavPaneMinW/MaxW` 180/460, `NavPaneNarrowW/MidW/WideW` 240/280/320, `NavPaneMidEnterW/WideEnterW` 1400/1800, `NavPaneHysteresisDip` 24, `RailMinW/MaxW/DefaultW` 200/500/340, `ClampRailWidth`, `DockedVideoNaturalH`, `DockedVideoMaxH` 560, `ClampDockedVideoHeight`, `FitDockedVideoHeight`, `CanFitRail`, `CompactRailW` 56, the 720/760 narrow band, the 520/560 toolbar band | `Wavee.Tests/ShellResponsiveLayoutTests.cs` (199) | `Shell/Shell.cs` (CORE) |
| `MergedChromeLayout` | `Features/Shell/MergedChromeLayout.cs` | the 48-DIP chrome row's pressure allocator: which of name/actions/forward/back/new-tab/trailing survive, field ⇄ icon search, the quantised `LeadClusterW` with its *opposite-polarity* hysteresis | `Wavee.Tests/MergedChromeLayoutTests.cs` (253) | `Shell/Shell.cs` (CORE) |
| `SidebarDesignInfo` | `Features/Sidebar/SidebarDesign.cs` (134) | `SidebarDesign` values + slugs + mount keys, `Tiers` (240/280/320 · 300/340/380 · 280/320/360), `FromInt`, `Snapshot`/`Restore`/`TierDefault`/`ResetWidth`/`CommitWidth` and the 180/460 clamp | `Wavee.Tests/SidebarDesignGatingTests.cs`, `SidebarPaneInvariantTests.cs` | `Shell/Sidebar.cs` (CORE) |
| `SidebarRowGeometry` / `SidebarPaneMetrics` / `LibraryV3Metrics` | `Features/Sidebar/Data/*`, `Modes/LibraryV3/*` | the shared row-height ladder (32/40/44/48), art rungs (20/32/40), pane padding, indent step, the V3 chrome stack's fixed heights | `SidebarRowGeometryTests.cs`, `SidebarRowExtentsTests.cs`, `SidebarNavLayoutTests.cs` | `Shell/Sidebar.cs` (CORE) |
| `PlayerBarResponsiveLayout` | `Features/Shell/PlayerBarResponsiveLayout.cs` | the bar's tier ladder (440/760/900/1100/1240, 24-DIP narrow hysteresis) and which clusters survive — including `ShowTimesRemaining` | `Wavee.Tests/PlayerBarResponsiveLayoutTests.cs` (199) | `Shell/Shell.cs` (CORE) |
| `DetailTrackCommandBarLayout` | `Features/Detail/DetailTrackCommandBarLayout.cs` | which toolbar verbs survive at which width (this is where the in-page density control lives) | `Wavee.Tests/DetailTrackCommandBarLayoutTests.cs` (101) | `Entities/Track.UI.cs` |
| `TimeFormat.Clock` | engine | `m:ss` / `h:mm:ss`, negatives → `0:00`, invariant digits | `Wavee.Tests/TimeFormatTests.cs` | unchanged (engine) |

Every one of these keeps its assertions verbatim (plan §6, "Port: same assertions, call the section function on
the new type"). A single number changed in any of them is a visible regression somewhere in this chapter.

---

## 9. Re-author notes

### 9.1 What a Wave 4/5 owner must not simplify

1. **Do not collapse the Automatic/Hero override into a hero variant.** It selects a page SYSTEM. Automatic on a
   narrow window and Hero on a wide window produce the *same* composition at the *same* width — that is the
   point, and it is what makes the setting cheap.
2. **Do not merge the two Hero arms.** `HasTrailing` true (album family) takes an outer scroller with the hero
   and chrome as ordinary children and the table at `Grow 0`; `HasTrailing` false (playlist / Liked / local)
   virtualizes hero and chrome as list prefix items 0 and 1. Merging them either breaks album trailing shelves
   or costs playlist virtualization.
3. **Do not "fix" podcasts by giving them the Hero option.** §2.3.
4. **Do not make the hero title a rung ladder again.** The old four-rung ladder under-reserved by up to ~48 DIP
   because Segoe UI Variable's natural line box is 1.3301 em, taller than every authored pair above 20
   (`DetailVerticalLayout.cs:238-265`). Every height in the type plan is computed from `NaturalLineRatio`, and
   the hero clears its own `LineHeight` to `NaN` so there is nothing for the engine to discard.
5. **Do not charge the title's height budget for the description.** It would make a playlist with a long blurb
   pick a *smaller* title than an identical one without (`DetailVerticalLayout.cs:409-419`).
6. **Do not remove the 24-DIP asymmetry from any hysteresis.** Narrow immediately, widen only past the band. The
   cost of the wrong guess is asymmetric: a too-wide column set the pane cannot hold, versus one frame narrower
   than necessary.
7. **Do not put a picker in an expander header.** It starves the header text track to zero and the header paints
   over the content (`SettingsPage.Appearance.cs:16-21`). Pickers go in `ItemsHeader`; the header carries the
   answer via `SettingsValueTag`.
8. **Do not gate a hydration/fetch on a visibility preference.** N6.
9. **Do not hard-false a lane "because the vertical profile is narrow."** The vertical profile is forced at every
   width by the Hero setting, and three shipped user reports came from exactly that assumption (Heart 2026-08-10,
   Date/Added-by 2026-07-23, Video). Use the same tier gates as the standard profile
   (`DetailTracks.cs:472-497`).
10. **Do not clear a preference when a mode makes it inert.** Preserve and restore (X1, X2).
11. **Do not add a Wavee "reduce motion" setting.** N10.
12. **Do not write a preference from two places without one shared writer.** N12 and §1.3.

### 9.2 Mode-specific wireframes the sibling chapters still need

The orchestrator can patch these directly; each item names the chapter file and the exact state that is missing.
Every one is specified above, so the patch is a copy, not a new derivation.

| Chapter file | Mode-specific wireframe it still needs |
|---|---|
| `03-detail-frame.md` | the mode ladder table (820 / 660 / 560 / 540–580) with its hysteresis; **mode 1** and **mode 2** rail states (224 / 188) as distinct wireframes; the **collapsed rail** compact strip (W9); the **rail grip** bounds and detent (180/480, ForcePush 44, ReExpand 220); the **uniform vs per-scope** pair (W15); the live-toggle/KeepAlive path (W16) |
| `04-detail-track-table.md` | the **density × row-style grid** (W17) as 8 row states; **hidden artwork** on/off; **Plays** on/off at tier 0; **Tempo** on/off at tier ≤3; the **relief ladder** drop order with the three columns already gone at a squeezed width; **Classic** header casing + hairline + no-selection-pill |
| `05-album.md` | **Hero mode** for an album (W12 — the outer-scroll arm with trailing shelves), which is structurally different from the playlist Hero; the **prerelease** variant (W14); the album page's immunity to `HideTrackArtwork` and the density art ladder (X7) |
| `06-playlist.md` | **Hero mode** wide (W10) and narrow (W11); the **facts bento in the rail vs as the page footer** (the two-column ↔ hero move); the Plays/Tempo opt-in columns; the local-files page as the same composition on the playlist rail scope |
| `07-liked-songs.md` | **Hero mode** (W10 with the Liked filter chip rail inside the pinned chrome, `StickyClipInset` + 48); the **nine cover styles** (W24) with their tile minimums; the tone-anchor coupling (X11 · §4.2); the rail's missing `FillLayerDefault` (W6) |
| `08-artist-and-discography.md` | **colour washes OFF** for the artist hero (`CoverPaletteLeaves.cs:178` returns an empty box) and the hero gradient's exact stops when on; the shared `ContextBand` (the artist page clips at `ContextBand.ClipInset` 56 while the detail pages clip at `StickyClipInset` 93+) |
| `10-home.md` | **colour washes OFF** — the three radial washes become `null` and only the 3 %-alpha neutral rect remains; the wash geometry table (centres, radii, fade offsets, per-theme alphas) when on |
| `18-shell-frame.md` | the **nav-pane tier ladder** per sidebar design (1400 / 1800, 24-DIP hysteresis, three different triples); the **right rail width** and **docked video height** states (W28); **zoom demoting a tier** (W23) with a worked example; the merged chrome row's pinned-tab pressure (matrix row `workspace.tabs.pinned`) |
| `20-player-bar.md` | **remaining vs total** (W26) including the `-` sign rule and the 44-DIP slot; **marquee off** (the ellipsis title with no edge fade); the tier at which the time labels appear at all (440 / 760) |
| `21-right-rail-npv-queue-stage.md` | **Cover vs Player deck** and the twelve deck ids (W29); the queue panel's own row extents under `TrackRowStyle` (44 classic / 64 modern — it does **not** read `RowDensity`); `HideTrackArtwork` in the queue, NPV, stage and video-rail rows; the docked-video height splitter |
| `22-lyrics.md` | **second line off/translation/romanization** with the 0.62 ratio and the 3-DIP gap, plus the viewport-invalidation consequence; **blur 0 / 40 / 100** as three σ states for both the rail and the stage; **backdrop on/off** (drift constants vs held still with no ticker); the header toggle's hidden state |
| `25-sidebar.md` | the **three designs side by side** at their Narrow tiers (240 / 300 / 280) plus the shared 56-DIP collapsed rail; the fact that an un-pinned design restores at its TIER DEFAULT for the live viewport, not its Narrow tier (W27); the per-design `WidthUserSet` latch and what switching designs restores; the **dead `sidebar.curated.rail.labels` key** flagged for deletion |
| `00-design-system.md` (when written) | the **light/dark token table** (W22) including the theme-dependent `Elevation.Card`; the **zoom ladder** and the `viewportDip = px / (dpi × zoom)` rule; the localisation position (no RTL, ellipsis or marquee only) |
| `01-track-row.md` (when written) | the full density × style × artwork grid; the Classic/Modern structural diffs (inset, corners, border, zebra, hairline, selection pill, number cell, More button); the marquee's single-row scope |
| `27-settings-and-diagnostics.md` (when written) | the Appearance tab's own composition — three collapsed expanders whose headers carry the current answer, the two flat ON switches, the conditional sub-rows under Track page layout, and the confirm dialog on "Clear all remembered sizes" |

### 9.3 What the plan's §2 tree and §5 waves miss for preferences

1. **`Platform.*` is a Wave 6 file (owner S) but Wave 5 pages read preferences.** `App.Main` calls
   `Platform.Boot()` first, and the theme, zoom, culture and sidebar design are all read before the first frame.
   The **settings store, the typed key registry and `Design.Prefs`** must land in **Wave 4**, owned by L
   (`Design.cs`/`Controls.cs`) with a `Platform.cs` skeleton from Wave 0, or every Wave-5 page will invent its
   own read.
2. **The five preference epochs are scattered across four different wave owners in 0.2.9's layout**:
   `AppearancePrefs` → `Design/` (Wave 4, L), `LyricsPrefs` → `LyricsView.cs` (Wave 4, K), `PlayerBarPrefs` →
   `PlayerBar.cs` (Wave 4, I), `NpvPlayerPrefs` → `Features/Player/` (Wave 4, K), `DetailHeroPrefs` →
   `DetailVerticalHero.cs` (**Wave 5**, M/O). Five owners writing five near-identical epoch classes is how the
   `SetDensity` bug happens. Put all five in `Design.Prefs` and make it a Wave-4 deliverable with its own gate.
3. **No wave owns the pure layout-rule port.** `DetailLayoutBreakpoints`, `DetailRailPolicy`,
   `DetailVerticalLayout`, `DetailTrackTableRules`, `ShellResponsiveLayout`,
   `MergedChromeLayout`, `PlayerBarResponsiveLayout`, `ZoomAutoPolicy`, `LyricsBlurPolicy`, `LikedCoverRules`
   and the sidebar geometry classes are ~2 000 lines of already-tested pure code that **every** Wave-5 page
   depends on. They should be a named Wave-4 task with their test files ported first, so Wave 5 starts green.
   `ContextBandLayout` is the one item off that list, because it now has an owner and a slot: the arbitration of
   2026-09-12 homes it — with `ContextBand` itself — in `Entities/Detail.cs` + `Entities/Detail.UI.cs`, owner
   **M**, in the **Wave 4.5** shared-detail-frame slot that opens when Wave 4 closes and must be green before
   Wave 5 opens (`03-detail-frame.md` §9 and §10 item 67). Its tests port with it, and the artist page (owner N,
   Wave 5) consumes the same helpers rather than writing the band twice.
4. **Settings is Wave 6 (owner R) but the preferences are live from Wave 4.** That is survivable only if the
   store and the epochs exist earlier (point 1). Add a Wave-5 gate item: *every preference in this chapter's
   matrix can be exercised by writing the registry value and bumping its epoch from the diagnostics page*, so
   the variants are verifiable before the Settings UI exists.
5. **`--fake` cannot reproduce two states in this chapter.** `FakeData` has no prerelease album and no daylist
   window (grep for `IsPreRelease` / `prerelease` / `ExpiresAtMs` in `Wavee.Core/Fakes/FakeData.cs` returns
   nothing), so W14's countdown card and the hero's `PulseRowHeight 28` row cannot be checked offline. Either
   seed them in `Entities.SeedFake()` (Wave 5, owner Q) or mark those two parity items live-only.
   Fake shows are `wavee:show:{i}` and fake albums/playlists are `spotify:album:al{i}` / `spotify:playlist:pl{i}`.
6. **The plan's §8 definition of done has no appearance-parity item.** Add one: *the §10 checklist of
   `30-appearance-preferences.md` passes against the kept 0.2.9 Release build.*
7. **The plan has no home for a PER-SUBJECT persisted preference.** `detail.sort.{col,desc}:<ctxUri>` is one
   `SettingKey<T>` built at the call site per context uri (`DetailShell.cs:178-179`) — it is not in the registry
   and it cannot be, because there is one per album/playlist the user has ever sorted. 0.3's `Platform.cs`
   `static class Keys` is a registry of STATIC keys; this pattern needs an explicit, named seam beside it
   (a `Keys.SortCol(EntityUri)` factory), or Wave 5's page owners will each invent one. The same shape covers
   `library.<kind>.*` and `sidebar.<slug>.*`, which the plan also does not name.
8. **Nothing in the plan owns the `IAppSettings?`-is-null contract** (§7.1). Every 0.3 reader must take an
   optional store and fall back to the key default; make it a Wave-4 gate item, because the first page that
   dereferences `Platform.Settings` unconditionally makes every later test harness start the shell.

### 9.4 Line budget

This chapter owns no page of its own, so its budget is the **plumbing plus the pure rules** — everything a Wave-4
owner has to write before a Wave-5 page can read a preference at all.

**0.2.9, as it stands today** (counted with `wc -l`; a partial count names the fraction and why):

| 0.2.9 file | lines | what of it is this chapter's |
|---|---|---|
| `Platform/AppSettings.cs` | 494 | all of it — the key registry and the defensive store wrapper |
| `Features/Shell/SettingsPage.Appearance.cs` | 651 | all of it — the tab, the three expanders, the six writers, the preview cards' call sites |
| `Design/WaveePicker.cs` | 287 | all of it — `Strip` / `Card` / `Titled` / `DensityRows` / `ModernRow` / `ClassicRow`, the ink pair, the group keyboard contract |
| `Features/Shell/SettingsPage.General.cs` | 251 | ~60 — the Language row (N2) and the two developer-mode rows |
| `Design/AppearancePrefs.cs` | 30 | all |
| `Features/Player/NpvPlayerPrefs.cs` | 48 | all |
| `Features/Player/LyricsBlurPolicy.cs` | 37 | all |
| `App/ZoomAutoPolicy.cs` | 114 | all |
| `Features/Detail/DetailRailPolicy.cs` | 67 | all |
| `Features/Detail/DetailLayoutBreakpoints.cs` | 88 | all |
| `Features/Sidebar/SidebarDesign.cs` | 134 | all |
| the three embedded epoch classes | ~63 | `DetailHeroPrefs` (`DetailVerticalHero.cs:575-579`, 5) · `LyricsPrefs` (`LyricsView.cs:3020-3072`, 53) · `PlayerBarPrefs` (`PlayerBar.cs:1131-1135`, 5) |
| **subtotal — the plumbing** | **≈ 2 073** | |
| `Features/Detail/DetailVerticalLayout.cs` | 662 | the pure geometry §8 says no wave owns |
| `Features/Detail/DetailTrackTableRules.cs` | 279 | same |
| `Features/Detail/LikedCoverRules.cs` | 307 | same |
| `Features/Shell/ShellResponsiveLayout.cs` | 247 | same |
| `Features/Detail/ContextBandLayout.cs` | 179 | same — except that this one now has an owner and a slot: `Entities/Detail.cs`, owner M, Wave 4.5 (arbitration 2026-09-12) |
| `Features/Shell/ShellMaterialLayer.cs` | 133 | the wash/tint composition §4 owns |
| `Design/CoverPaletteLeaves.cs` | 265 | the three wash leaves §4 owns |
| `Features/Detail/LikedCoverArt.cs` | 156 | the style resolution + the site ladder |
| **subtotal — the pure rules + the colour leaves** | **≈ 2 228** | `MergedChromeLayout` and `PlayerBarResponsiveLayout` are named in §8 but counted by chapters 18 / 20 |
| **0.2.9 total for this chapter** | **≈ 4 300** | |

**The plan's target.** Plan §2 gives `Platform/` **7 files, ~8 600 lines** — `Platform.cs`, `Platform.Host.cs`,
`Modules.cs`, `Modules.UI.cs`, `Modules.Host.cs`, `Design.cs`, `Controls.cs`. This chapter's material is
`Platform.cs` (the store + the typed key registry + `ZoomAutoPolicy`) and the `Design.Prefs` / `Design.*Breakpoints`
sections of `Design.cs`; the Settings *surface* is `Screens/Settings*.cs`, counted inside the plan's `Screens/`
**~13 700**. The plan therefore budgets this chapter's plumbing implicitly, and **nowhere names it as a
deliverable** — which is §9.3's first finding.

**The honest estimate.**

| 0.3 destination | honest estimate | why it differs from 0.2.9 |
|---|---|---|
| `Platform/Platform.cs` — the store, the `SettingKey<T>` record, the whole key registry, `Boot()`, `ZoomAutoPolicy` | **≈ 650** | the registry is the wire and shrinks only by losing the dead `sidebar.curated.rail.labels` key and the three legacy v0 sidebar keys; the comments are the documentation and must survive |
| `Platform/Design.cs` §Prefs — five epochs, one `Set<T>` writer, one reactive read per key | **≈ 260** | **up** from 0.2.9's 148 scattered lines: one shape stated once, plus the reads that are open-coded at 50 call sites today |
| `Platform/Design.cs` §breakpoints — `DetailBreakpoints`, `DetailRailPolicy`, `DetailVerticalLayout` geometry, `ShellResponsiveLayout` | **≈ 1 120** | a straight port; the assertions are already written, so the only shrink is de-duplicated doc comments |
| `Entities/Detail.cs` §band — `ContextBandLayout` (arbitration 2026-09-12; **not** `Design.cs`) | **≈ 180** | a straight port of the 179-line file, carved OUT of the row above so the total does not move; owner M, Wave 4.5. The band's UI half (`Detail.Band` and friends) is `Entities/Detail.UI.cs` and is budgeted by `03-detail-frame.md`, not here |
| `Platform/Controls.cs` §picker — the preview-card strip | **≈ 280** | unchanged; it is already one file |
| `Entities/Track.UI.cs` §rules — `DetailTrackTableRules` + `TrackLane` | **≈ 280** | unchanged |
| `Entities/User.cs` §liked-cover — `LikedCoverRules` + the site ladder | **≈ 400** | `LikedCoverRules` (307) + the resolution half of `LikedCoverArt` (~90) |
| `Shell/Shell.cs` §material — the tint/wash composition and its three "no colour" arms | **≈ 330** | `ShellMaterialLayer` + `CoverPaletteLeaves`, minus the hero-only veil that is dead with hero-only mode gone |
| `Screens/Settings.UI.cs` §appearance — the tab | **≈ 620** | roughly flat: six writers collapse into `Design.Prefs.Set`, but the N12 fix and the honest-copy rules add rows |
| **total** | **≈ 4 120** | ~4 % below 0.2.9 — **this chapter does not shrink**, and a plan that assumed it would is wrong |

The reason it does not shrink is stated in §9.1: every number here is a decision with a user report behind it,
and the comments that record those reports are the only place they exist. The savings in 0.3 are in the *page*
files, not in the rule files.

---

## 10. Parity checklist

Every item is binary and is checked **side by side**: the kept 0.2.9 Release build at
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe` in one window, the 0.3
build in the other, both launched with `--fake`. Navigate with `wavee://open?route=<name>` (a second `Wavee.exe`
hands the link to the running instance and exits). Window widths are **DIP viewport** widths — keep zoom at 100 %
(Settings ▸ Appearance ▸ Zoom ▸ 100 %) unless the item says otherwise, because zoom divides into the viewport
(W23). The settings store is `HKCU\Software\Wavee\Wavee\Settings`; reset a run with a factory reset rather than
editing float keys by hand (they are REG_QWORD IEEE-754 bits).

**Defaults and first launch**

1. On a wiped store, both builds' first frame is identical on `route=home`: theme follows the OS, zoom 100 %,
   Classic sidebar at 240 DIP, no colour-wash difference. (N9)
2. `route=liked` on a wiped store paints the **Lens** treatment, NOT the bundled stock PNG: `--fake` gives
   every track a distinct album uri and cycles `CoverCount = 16` distinct cover files (`FakeData.cs:12`, `:41`,
   `:58`), so `LikedCoverRules.Tiles` returns **16** — above every style's `MinTiles` (max 8). Verify the
   nine-cell mosaic, its baked-blur ground and the heart stencil are identical between builds. (The stock PNG
   arm is only reachable offline by pinning the style to Stock, or live on an account with < 4 distinct covers.)
3. `route=album:spotify:album:al3` at a 1200-DIP window opens in Automatic mode 0 with a **280**-DIP rail and a
   **256**-DIP cover. Measure the cover's left edge at 16 DIP from the rail's left edge.
4. `route=pl:spotify:playlist:pl2` at the same width opens with a **240**-DIP rail and a **216**-DIP cover.
5. `route=show:wavee:show:0` at the same width opens with a **280**-DIP rail and an episode list (not a track
   table) on the right.

**Detail mode ladder**

6. From a wide window, narrow the detail page's own column to 819 DIP: mode 1 (rail 224) **immediately**. Now
   widen: at 820 it is **still mode 1**, at 843 still mode 1, and only at **844** does it return to mode 0.
   (Narrowing is immediate, widening costs the 24-DIP band — §2.1.)
7. At a page width of 700: mode 1, rail **224**, no grip. At 600: mode 2, rail **188**, no grip.
8. Narrow to a page width of 535: the page flips to Vertical. Widen to 570: still Vertical. Widen to 580: back
   to mode 2. (540/580 band.)
9. Launch the app already sized to a ~600-DIP window on `route=pl:spotify:playlist:pl2`: the **first composed
   frame** is not the wide two-column arm. Watch for a one-frame rail flash. (The pre-measure estimate.)
10. In Vertical at a page width of 460, the hero is in **row flow** (art beside the identity). At 415, it is
    **stacked**. Widen from 415 to 424: row flow. Narrow from 460 to 405: still row flow; 399: stacked.
11. Across the whole 300→1200 sweep in Vertical, the artwork edge **never shrinks as the window widens**.

**Hero mode**

12. Settings ▸ Appearance ▸ Lists ▸ Track page layout ▸ **Hero**, with `route=pl:spotify:playlist:pl2` already
    open in a 1200-DIP window. The page switches arms **without a relaunch**, and the rail is gone.
13. With Hero selected, switch back to Automatic with the same page open: the rail returns at the width it had
    before, not at the default.
14. With Hero selected, navigate to `route=album:spotify:album:al3`: the hero appears **above** the track table
    and the trailing shelves (About / Fans / More by / Other versions) scroll below the rows in the same
    scroller.
15. With Hero selected, navigate to `route=show:wavee:show:0` at 1200 DIP: it is **still the two-column rail**
    layout. (§2.3)
16. With Hero selected, `route=local`: the local-files page takes the hero (it is a playlist).
17. In Hero at 1200 DIP, scroll the playlist down: the hero collapses into a **56**-DIP text band with the
    title and "Owner · N songs, H hr M min", the column header pins under it, and the rows are clipped — not
    covered — at 56 + 36 + 1 DIP.
18. In Hero on `route=liked` with a content filter chip rail showing, the clip inset grows by the rail's 48 DIP.
19. Open three detail pages in a row, then change Track page layout: **all three** (the live page plus two
    KeepAlive-parked ones) are in the new arm when you press Back twice.

**Rail preferences**

20. Drag the album rail to ~400 DIP, navigate to a playlist: the playlist rail is still 240. Navigate back: the
    album rail is 400. (Per-scope memory, N3.)
21. Drag the album rail to 400, narrow to mode 1 (rail 224), widen back to mode 0: the rail is 400 again. (N8.)
22. Drag any rail below ~180 and keep pushing: the content fades, then the rail collapses to a **96**-DIP strip
    with an 80-DIP cover, a 12px 2-line title and a chevron. The track list's scroll offset does **not** reset.
23. From collapsed, drag the 20-DIP seam right: the rail does not re-open until ~220 DIP. Clicking the compact
    cover re-opens it immediately.
24. Turn on Settings ▸ Appearance ▸ Track page layout ▸ **Keep left-rail same size** with an album page open:
    the rail snaps to 240 and every other detail page opens at 240. Turn it off: the album rail returns to 400.
25. With uniform OFF and at least one rail moved, "Clear all remembered sizes" is **enabled**, shows a confirm
    dialog, and resets all four to 280/240/240/280 on the **open page immediately** — not on the next launch.
26. With uniform ON, the "Clear all remembered sizes" row is **not composed at all**.

**Track table preferences**

27. With `route=pl:spotify:playlist:pl2` at 1200 DIP, step Row density through Compact/Default/Cozy/Comfortable
    using the **list's own command bar** (not Settings): row heights measure 40 / 48 / 56 / 64 and thumbnails
    32 / 32 / 40 / 48.
28. Switch Track list style to **Classic**: rows become 36 / 40 / 44 / 48, lose their 8-DIP inset, corners,
    border and zebra, gain a 1-DIP hairline inset by 16 DIP each side, and the artist moves to its own lane.
29. In Classic, toggle "Always hide track artwork": **nothing changes** (Classic has no thumb lane). In Modern,
    the same toggle removes the lane and gives 44 DIP (32 + one 12-DIP gap) back to the title.
30. On `route=album:spotify:album:al3`, toggle "Always hide track artwork": **nothing changes** (album rows have
    `ShowArtThumb: false`).
31. On a playlist, turn on the **Plays** column from the list's More flyout: a 52-DIP right-aligned "Plays"
    column appears, sortable. Narrow the right column below 560 DIP: it disappears. Widen: it returns.
32. On the same playlist turn on **BPM · Key**: an 80-DIP right-aligned column appears, not sortable, with a
    6-DIP Camelot swatch. It survives to a 440-DIP right column, one tier past Plays.
33. With both on, narrow until the table is squeezed: **Plays** leaves first, then **BPM · Key**, before Album,
    Artist or the thumb. (Relief order.)
34. The Row-density Settings expander header shows the current answer ("Cozy") while collapsed, and its four
    preview cards' miniature rows are exactly 0.25 × the real ladder (10 / 12 / 14 / 16 DIP tall).

**Shell, theme, zoom**

35. Settings ▸ Appearance ▸ **Color washes** OFF with a coloured album page open: the shell material, title row,
    sidebar, player dock and page ground all go neutral **on the same frame**, and the Play pill keeps its
    art-derived accent.
36. With washes OFF, `route=home` shows no radial washes and the shell is `ShellGround @ 3 %`, not transparent —
    compare against the page's own Mica by looking at the title row.
37. Theme ▸ Light / Dark: card shadows change (`Blur 4 #1A` light vs `Blur 8 #33` dark) as well as colours.
    Theme ▸ System, then flip the Windows theme: the app follows within one 250 ms cross-fade, with no relaunch.
38. Zoom ▸ 150 % on a 1400-DIP window: the sidebar demotes from Mid (280) to Narrow (240) because the DIP
    viewport is now ~933. Zoom ▸ 100 %: it promotes back. Confirm the same crossing in both builds.
39. Zoom ▸ 200 % with a detail page open: the page's own column crosses into Vertical without the window moving.
40. Ctrl+= / Ctrl+- / Ctrl+0 step the same 12-rung ladder as the picker, and the picker's label updates live.
    With Zoom mode Auto, one chord flips the mode to Manual within one resize-settle.

**Player bar, lyrics, sidebar, NPV**

41. Play a track. The right time label reads `-2:18` and counts down; clicking it swaps to `3:41`; the Settings
    ▸ Playback ▸ "Show remaining time" toggle agrees in both directions. The slot stays 44 DIP wide in both
    states, and the seek row does not reflow when the track starts.
42. Marquee OFF: a long now-playing title in the player bar truncates with an ellipsis and shows **no edge
    fade**; the now-playing track row stops scrolling and matches every other row.
43. Open the immersive lyrics stage. "Animated lyrics backdrop" OFF: the blurred cover is perfectly still and
    stays full-bleed (no visible edge at the 1.30× overscale). ON: it drifts. Toggle with the surface open.
44. Lyrics blur slider to **0**: no depth-of-field on any line and no halo on the active line. To **100**: the
    ±5-line ladder is visible. The "Auto" link appears only after the slider has been moved and resets to −1.
45. Lyrics second line ▸ Translation on a document that has one: a smaller secondary run appears under every
    line, every row grows, and the list does not lose its place. On a document with neither layer, the header
    toggle is **absent**, not disabled.
46. Sidebar design ▸ Library: the pane jumps to 300 DIP and gains the fixed chrome stack (header 44 + toolbar
    36 + chip rail 40). Back to Classic: 240 DIP. Drag Classic to 320, switch to Library and back: Classic is
    320, Library is still 300.
47. Collapse the sidebar in each of the three designs: the rail is **56** DIP in all three and shows one tile per
    `ShowInRail` section with the label as a tooltip.
48. Right rail: drag the shell splitter to its ends — it stops at 200 and 500. The docked video cap's floor is
    `railW × 9/16` (at 340 → ~191) and it only grows from there, ceiling 560.
49. Now Playing hero ▸ Player: the pinned hero becomes the Record deck at `railW − 16` snapped to 4 DIP. Cycle
    all twelve presets; none crashes and each keeps its own option choices.
50. Localisation: with Language ▸ System, no layout differs from English. Confirm the row's subtitle still reads
    "Changes are applied the next time Wavee starts." and that changing it does **not** change anything until
    relaunch. (N2 — the honest copy is the parity item.)

**States the chapter added on this audit** (all checkable in `--fake` unless marked)

66. On a playlist at a right-column width of ~400 DIP in **Classic**: the separate Artist lane is **gone and the
    artist is in the Title subline**, not simply missing. Widen past 440: the lane returns. (`ClassicArtistFoldTier`
    4 — W17. A chapter that draws "Classic narrow" as "Classic minus a lane" is wrong.)
67. Sort a playlist by Title, navigate to another playlist, navigate back: the first playlist is **still sorted by
    Title** and the second is not. Then relaunch: both remember. (`detail.sort.{col,desc}:<ctxUri>` — one pair per
    context uri, §1.1. Confirm the scroll offset survives the header click: sort is deliberately not in the list key.)
68. Type into the track list's search box on a two-column page scrolled to row ~40: the list **remounts** (the query
    is in the list key). Do the same in **Hero** mode: it does **not** — `filterKey` is empty in the vertical arm.
    (§5.3 — both behaviours must match 0.2.9.)
69. Open Settings ▸ Appearance ▸ Row density, focus one preview card and hold the arrow key: the density
    **changes on every rove**, not on a commit. Same for Track list style, Track page layout and the Liked cover
    flyout. (Selection follows focus, §6.2 — so every 0.3 writer must be idempotent and cheap.)
70. Open the immersive lyrics stage on a track with **no cover art**: the backdrop is absent and the idle frame
    cost is the same whether "Animated lyrics backdrop" is ON or OFF — the ticker's gate is `drift && art.Length > 0`.
    (§5.2 row 25b.)
71. With **nothing playing**, open the right rail's Now Playing pane: the pinned hero slot composes **nothing**, in
    both Cover and Player presentation. (W29.)
72. Sidebar design ▸ Wavee Curated: a **"Customize sidebar…" row** appears under the design cards. Switch to
    Classic or Library: it is **gone** — not disabled. (`SidebarDesignGating.CanCustomize`, §1.1.)
73. On a window wide enough for the sidebar's MID tier (≥ 1400 DIP), switch to a design whose width was never
    dragged: it opens at that design's **Mid** tier (Classic 280 / V3 340 / Curated 320), not its Narrow tier.
    (W27 — the previous wording of this chapter said Narrow and was wrong.)
74. Turn on **BPM · Key** on a playlist whose tracks have no kind-222 adornment yet: the lane is present and the
    cells are **empty**, not "0 BPM" and not a dash. Turn on **Plays** on rows with no count: those cells are an
    **em dash**. The two are deliberately different. (§7.3 R3/R4.)
75. Launch onto `route=home` with the Windows accent colour set to something loud, Theme ▸ System: note whether
    the first frame's Play pill uses the OS ramp or `Tok.AccentDefault`. In 0.2.9 it is the DEFAULT until the first
    `WM_SETTINGCHANGE` or an explicit theme pick, because `Program.cs:402-404` bypasses `ApplyThemeMode`.
    0.3 must make this a decision, and this item records whichever it decides. (§4.3 point 3.)

**Motion — what a live flip may and may not animate** (all new with §5; every one is a *side-by-side* check —
the 0.2.9 build in one window, 0.3 in the other, and the answer must be the same in both)

51. Theme ▸ Light ⇄ Dark with a coloured album page open: the app **cross-fades over ~250 ms** and does not
    remount — the scroll position, the expanded rows and any open flyout survive. The **window frame** (title bar
    / Mica) flips **instantly**, ahead of the content. Both facts must hold in both builds. (§5.2 rows 1 and 3)
52. With Theme ▸ System, flip the Windows theme twice in quick succession: each flip cross-fades once. Now flip a
    Windows setting that broadcasts `ImmersiveColorSet` **without** changing the palette (e.g. toggle transparency
    effects): the app does **not** re-render or flash. (§5.2 row 4 — the `Tok.Epoch` guard)
53. Settings ▸ Appearance ▸ Color washes OFF with a coloured album page open: the **shell material** (title row,
    sidebar, player dock) **eases** to neutral over ~250 ms, while the **page ground** under the track table goes
    neutral **on the same frame, with no fade**. That asymmetry is the correct behaviour, not a bug. (§5.2 rows
    14–15)
54. Row density from the **list's own command bar**, on a page scrolled to row ~40: the rows change height with
    **no transition**. Now do the same from **Settings ▸ Appearance ▸ Row density** with that page still open:
    in 0.2.9 **nothing happens** until you navigate to a different context; in 0.3 it must change immediately.
    This item is expected to FAIL against 0.2.9 — it is N12. (§5.2 row 9)
55. Track list style Modern ⇄ Classic on a page scrolled to row ~40: the list **remounts** (the key carries the
    style), so the viewport re-realizes. Confirm the behaviour matches 0.2.9 exactly — including whatever the
    scroll offset does. (§5.3)
56. "Always hide track artwork" on the same scrolled page: the thumb lane leaves **without** a remount and the
    scroll offset does **not** move. (§5.2 row 11 — the flag rides the snapshot, not the key)
57. Drag the detail rail grip slowly toward its floor: below ~180 the rail's **content fades** (to about a third
    opacity) and the grip **resists** — the rail moves slower than the pointer. Release before the detent: the
    fade returns to full and the width is committed. (§5.2 row 35)
58. Open three detail pages, then flip Track page layout: press Back twice. Each parked page is already in the
    new arm and **plays no layout transition** as it reactivates. (§5.2 rows 18–19, 37)
59. Sidebar design ▸ Library: the pane width **animates** over ~300 ms while the pane content **remounts**.
    Now grab the sidebar seam and drag: during the drag **nothing in the window animates** — every layout
    transition is snapped. (§5.2 rows 32, 34)
60. Zoom ▸ Auto on a window you then resize slowly: the zoom does **not** chase the resize; it re-resolves once,
    about half a second after you stop. (§5.2 row 6)
61. With a track playing, click the player-bar right time label: the label swaps on the **next second tick** —
    the seek row does not reflow and the 44-DIP slot does not change width. (§5.2 row 27)
62. On the immersive lyrics stage, move the blur slider from 100 to 0 **while paused**: every line goes crisp
    immediately, in one step — not at the next lyric line, and not over a ~200 ms ease. Move it back to 100 while
    still paused: the ladder returns immediately. (§5.2 rows 22 and 24 — both arms are the ones the 0.2.9 fix
    exists for, so both must hold)
63. On the same surface, turn "Animated lyrics backdrop" OFF: the cover stops **instantly**, stays full-bleed (no
    visible edge at the 1.30× overscale), and the app's idle frame cost drops — there is no ticker left. (§5.2
    row 25; check `[wake]` / frame lines if the drop is not obvious)
64. Now Playing ▸ Player, then cycle presets: each preset change **remounts** the deck (any running deck
    animation restarts from its own beginning). Change a per-deck **option** instead: the deck restyles **without**
    restarting. (§5.2 rows 29–30)
65. Turn Windows' "Show animations in Windows" OFF, then repeat items 51, 59 and 63: the theme cross-fade
    **still runs** (it is a colour fade, deliberately not suppressed), the sidebar width transition takes the
    engine's reduced arm, and the lyrics backdrop is held still **regardless of the app setting**. (§5.4)

---

## 11. Audit log

| # | date | kind | § | note |
|---|---|---|---|---|
| 1 | 2026-09-12 | **restructure** | whole chapter | **This chapter was re-cut onto the standard twelve-section structure.** It was the only one of the thirty that did not follow it: it ran `0 / 1 the matrix / 2 Automatic-vs-Hero / 3 per-preference states / 4 interaction / 5 data / 6 rules / 7 notes / 8 parity`, with **no `## 2. Wireframes` heading, no §3 Tokens, no §4 Colour & material, no §5 Motion and no §11 Audit log**. The completeness critic filed it as a **high** finding, with the consequence stated exactly: *"the app's live preference flips (row density, track-list style, track-page layout, hide-artwork, keep-rail-same-size) have zero motion rows anywhere in the contract, even though W16 is titled 'What a live toggle does to a mounted or KeepAlive-parked page'."* The mapping, so nothing reads as lost: old §1 → **§1.1**; old §5.1 → **§1.3**; old §5.2 → **§1.4**; old §2.0–2.2 → **§2.0–2.2**; old §2.7 → **§2.3**; old §3.8 → **§2.4**; old §3.1–3.14 → **W17–W29** (the thirteen per-preference state blocks now carry W-numbers and `@ width` titles, so §2 holds **29** wireframes instead of 16); old §4 → **§6.1** (its eighteen items renumbered `4.n` → `X<n>`, so they cannot collide with the §6.n sub-sections; the three cross-references to them were updated); old §5.3 → **§7.1**; old §6 → **§8**; old §7 → **§9**; old §8 → **§10**. §3, §4, §5, §7.2 (DATA GAPS), §6.2–6.4, §9.4 (line budget) and this log are **new**. No prose, no table row and no wireframe was deleted. |
| 2 | 2026-09-12 | **missing → added** | §5 | **The whole of §5.** Thirty-seven motion rows, each with a duration or an explicit "snap, no transition" and the `file:line` that proves it, split into mounted / KeepAlive-parked arms; plus §5.1 (what each of the five epoch bumps actually fans out to), §5.3 (the five key-carried remounts, listed once) and §5.4 (reduced motion, read as a value at five sites). The thirteen-row table `29-cross-cutting.md` §5.2 was hosting **on this chapter's behalf** is now superseded: chapter 29's own audit item 11 says it lives there "until ch 30 is renumbered". It has been renumbered. Chapter 29 §5.2 may now defer here, and the rows that were only sketched there — the wash asymmetry, the blur ramp, the backdrop ticker, the NPV re-key, the grip fade — are stated with their constants for the first time. |
| 3 | 2026-09-12 | **wrong → corrected** | §5.2 row 14/15, §4.1 | **The colour-wash flip is not one behaviour, it is two.** The old §3.4 cited `BrushTransitionMs = WaveeMotion.Standard 250` at `CoverPaletteLeaves.cs:242-246`, which mixed two different facts: `:242-246` is the *tint formula*, and the 250 ms ramp on the **shell** layer is at `ShellMaterialLayer.cs:98`. More importantly the **detail tone plane does not fade at all** when washes go off — `CoverPageTonePlane.Render` returns a **Fill-less** `BoxEl` at `CoverPaletteLeaves.cs:93-94`, so the 250 ms `BrushTransitionMs` at `:115` has nothing to interpolate. The shell eases; the page ground snaps. Corrected in §4.1, given two separate motion rows, and turned into parity item 53. |
| 4 | 2026-09-12 | **wrong → corrected** | §10 item 2 | **`--fake` does NOT paint the stock Liked cover.** The old parity item 2 asserted "the fake library has fewer than 4 distinct covers". It has **sixteen**: `FakeData.Track(i)` gives every track a distinct album uri (`spotify:album:al{i}`) and `Cover(i, 64)` cycling over `CoverCount = 16` files (`FakeData.cs:12`, `:24-32`, `:41`, `:58`), and `LikedCoverRules.Tiles` dedupes by url *and* album uri up to `MaxTiles` 16 — so it returns 16, which clears **every** style's `MinTiles` (max 8, `LikedCoverRules.cs:139-150`). In `--fake` the default **Lens** treatment composes, and all nine styles are reachable. Item 2 rewritten; the real offline limitation is fidelity, not degradation (DG4: the tiles are 64 px). |
| 5 | 2026-09-12 | **wrong → corrected** | §1.1 `HideTrackArtwork` row | "12 call sites" was a miscount. `AppearancePrefs.TrackArtworkHidden` has **14** (enumerated in §1.2), plus one deliberate direct store read inside `TrackRowsSnapshot` (`DetailTracks.cs:709`) that must stay direct because the snapshot is the recycled row's only channel. |
| 6 | 2026-09-12 | **wrong → corrected** | §2.1, §2 W4 | Two dangling references. The matrix said the 500 ms zoom debounce lives at `WaveeShell.cs:591` — correct for `ZoomAutoDebounceMs`, but it paired it with an effect range of `:598-632`; the effect is `:598-631`. And W4's "referenced from §2.4" pointed at a heading that never existed — the Hero arms are **W10–W12**. Both fixed. |
| 7 | 2026-09-12 | **missing → added** | §3 | **The whole of §3.** Five token tables — the row ladder, the lane widths a toggle adds or removes, the rail/hero geometry each layout choice selects, the zoom/theme/marquee constants, and the liked-cover / lyrics / player-bar / shell-rail / sidebar / NPV values. Every computed number carries its formula (`CoverEdge`, the hero artwork curve, the fluid title cap, the docked-video floor, the NPV deck side). |
| 8 | 2026-09-12 | **missing → added** | §4 | **The whole of §4**, including the per-style **tone-anchor** table the chapter previously only gestured at: every dynamic treatment anchors on `tiles[0]` (`LikedCoverRules.cs:305-306`), which is why switching *between* treatments cannot move the page ground but switching *out of Stock* can. |
| 9 | 2026-09-12 | **missing → added** | §4.3 | **The OS window material flips instantly while the content cross-fades.** `OnApplyThemeMaterial` (DWM immersive-dark + Mica) is invoked on the same frame as `SetThemeTransition`, and the engine's own comment says the OS "cannot cross-fade it" (`fluent-gpu/.../AppHost.cs:736-738`, `:3499`). No chapter had this, and it is the first thing a re-author would try to "fix". |
| 10 | 2026-09-12 | **missing → added** | §5.2 row 1, §5.4 | **The theme cross-fade does not consult reduced motion**, and that is deliberate: `_reconciler.SetThemeTransition(themeMs)` is unconditional at `AppHost.cs:3500`. Stated so nobody "fixes" it into a snap. |
| 11 | 2026-09-12 | **missing → added** | §7.2 DG1/DG2 | **Three of this chapter's preferences have no offline arm at all**: `--fake` installs `NoLyricsProvider` (`Services.cs:624`), so second line, blur and backdrop cannot be exercised without a live login. The fixture that would close it (`FakeData.Lyrics`, `:543-567`) already exists and has **no caller** — the same finding `29-cross-cutting.md` audit item 4 filed from the other side. |
| 12 | 2026-09-12 | **missing → added** | §6.2–6.4 | The chapter had no Interaction section of its own — only the preference-vs-preference matrix. Added the keyboard table (the zoom chords and why they cannot flip `ZoomMode` themselves), the pointer table (nine gestures that write a preference, including the grip's resist/collapse/re-expand and the time label's click), and the four honest-copy / affordance rules the Settings surface enforces. |
| 13 | 2026-09-12 | **missing → added** | §9.4 | The chapter had no line budget. Counted: **≈ 4 300** lines in 0.2.9 (≈ 2 073 plumbing + ≈ 2 228 pure rules and colour leaves) against an honest 0.3 estimate of **≈ 4 120**. The finding is that this chapter **does not shrink**, and the plan budgets it only implicitly inside `Platform/` (~8 600) and `Screens/` (~13 700). |
| 14 | 2026-09-12 | **verified, no change** | §0, §2.0–2.3, W1–W16 | Re-read against the sources: `DetailShell.cs:148-152` / `:168-172` (seed + resync clamp), `:200` / `:203` / `:206` (44 / 220 / 96 / 20), `:212-213`, `:463-472`, `:509-512`, `:545`, `:628-629`, `:636-638`, `:639-656`, `:669`, `:681-687`, `:750-752`, `:797-817`, `:846-857`; `DetailTrackTableRules.cs:31` / `:37` / `:45-47` / `:49` / `:54-56`; `DetailTracks.cs:702-717` / `:1090`; `PlayerBar.cs:92-93` / `:128-129` / `:1151-1154` / `:1186-1199` / `:1208-1220`; `WaveeApp.cs:43-63` / `:239-244`; `WaveeShell.cs:259-261` / `:453-458`; `AppSettings.cs:86-93` / `:119-122` / `:144-171`; `WaveeTheme.cs:15` / `:27` / `:28-33`; `SidebarDesign.cs:63-69` / `:72`; `Splitter.cs:22`; `Marquee.cs:172`; `LyricsView.cs:363-373` / `:394-399` / `:3040` / `:3067-3071`; `ImmersiveLyricsSurface.cs:135-139` / `:162-165`; `NowPlayingPanel.cs:531-548`; `ShellResponsiveLayout.cs:121` / `:126-127` / `:130-161` / `:172-175`. All correct as cited. |
| 15 | 2026-09-12 | **source-comment drift** | §3.3 | `WaveeTokens.cs:59` still reads *"liked is single-column → no rail"*. Liked has had its **own** rail scope and its own key pair since the `RailScope` split (`DetailRailPolicy.cs:10`, `AppSettings.cs:146`, W6). The comment is stale, not the code. Recorded rather than edited — this chapter is read-only on code. |
| 16 | 2026-09-12 | **wrong → corrected** | end of file | The chapter file literally ended with two stray XML tags — `</content>` and `</invoke>`, a write artefact from the original authoring run, sitting after parity item 50. Removed. Worth a grep across the other twenty-nine chapters. |
| 17 | 2026-09-12 | **unverified** | §10 | **The parity checklist has not been executed.** Every item is derived from a `file:line` fact in this chapter. Items **43–45**, **62–63** and **70** additionally need a live login until DG1 is closed (70 also needs a track with no cover art); items **54** (N12) and **2** (the corrected Liked-cover expectation) are written as *0.2.9 defects or 0.2.9 facts 0.3 must change*, so 54 is expected to FAIL against 0.2.9. The fifteen items this audit added (**66–75**) are equally underived from a running build. Item **75** is deliberately written as "record what 0.2.9 does and decide", not as a pass/fail. Nothing in this chapter was written with either build running — the task forbade launching them. |
| 18 | 2026-09-12 | **wrong → corrected** | §3.4 | **The 0.004 zoom tolerance is spelled FOUR times, not five.** `WaveeApp.cs:231` is the same literal guarding the **volume** save timer (`SavedVolume`), a different decision on a different key; the zoom sites are `ZoomAutoPolicy.cs:110`, `WaveeShell.cs:615`, `:624` and `WaveeApp.cs:242`. 0.3 must not fold the volume one into the zoom constant. |
| 19 | 2026-09-12 | **wrong → corrected** | W27, §9.2 | **Switching to an un-pinned sidebar design does NOT restore its Narrow tier.** `SidebarPaneState.Restore` falls back to `TierDefault(design, viewportWidth)` — that design's OWN tier ladder at the LIVE viewport (`SidebarDesign.cs:101-109`, `:113-114`), so a ≥ 1400-DIP window restores the Mid tier and a ≥ 1800-DIP one the Wide tier. Narrow is only the zero/unknown-viewport seed. Also fixed §9.2's "(240 / 300 / 380)", where 380 was V3's WIDE tier standing in for Curated's Narrow 280. Parity item 73 added. |
| 20 | 2026-09-12 | **wrong → corrected** | §1.2 pre-frame reads, §4.3 | **`Program.cs` does not call `WaveeTheme.ApplyThemeMode`.** It repeats the mode switch inline and calls `Tok.Use(ResolvePalette(), kind)` (`Program.cs:402-404`), which SKIPS `ApplyThemeMode`'s `mode == 0` arm — so the **OS accent ramp is not adopted before the first frame**; the first adoption is the first `SystemColorsChanged` or an explicit Settings pick. Recorded in §4.3 point 3 and as parity item 75 rather than silently "fixed" in prose. Also corrected `ZoomAutoPolicy.MigrateMode`'s citation from an `AppSettings.cs` comment range to its real call site `Program.cs:103` → `ZoomAutoPolicy.cs:106-113`. |
| 21 | 2026-09-12 | **wrong → corrected** | §1.1, §5.2 row 9, §1.2 | Four drifted citations: the zoom-save path is a **2 s POLLING timer** (writes only when the value moved by > 0.004), not a trailing debounce (`WaveeApp.cs:239-244`); the Auto-zoom effect is `WaveeShell.cs:598-632` (audit item 6 had narrowed it to `:631`, which cut the closing line); the in-page density writer is `DetailShell.cs:382`, not `:381`; the Thumb-lane gate is `DetailTrackTableRules.cs:38`, not `:37`. The §1.2 registry group ranges (`appearance :86-125`, `detail :83-171`, `sidebar :408-434`) were all wrong and are now per-key. |
| 22 | 2026-09-12 | **wrong → corrected** | W12, §1.4, §5.3 | The `DetailTracks` citations in the 1000-1100 band had drifted ~20 lines: the hero-arm branch is `:955-957`, `listGrow` `:1081`, `rightBody` `:1092-1097`, and the "tier is deliberately not in the key" tombstone `:1071-1080` (cited as `:1092-1099` throughout). |
| 23 | 2026-09-12 | **missing → added** | §1.1, §6.3, §10 | **The per-context track sort is a persisted preference this chapter did not list.** `detail.sort.col:<ctxUri>` / `detail.sort.desc:<ctxUri>` — one pair per album/playlist ever sorted, built as a `SettingKey<T>` at the call site (`DetailShell.cs:178-179`), re-seeded by the same context-keyed `UseEffect` that carries the N12 density bug (`:361-375`), written by `SetSort` (`:376-381`). It changes the visible row order of every detail table and the header caret, and it is deliberately NOT in the list key. Added as a matrix row, a pointer row, §9.3 finding 7 (0.3's `Keys` registry has no home for a per-subject key) and parity item 67. Also added `sidebar.onboarding.seen`, which gates a whole one-time modal. |
| 24 | 2026-09-12 | **missing → added** | §1.4, §5.3, §10 | **The track list's remount key carries four things beyond the two preferences.** Verbatim at `DetailTracks.cs:1089-1090`: `vh:` (the vertical arm), `:q<query>:f<filtersHash>` — suppressed entirely in the vertical arm — `:r<resetEpoch>` (a curated re-cut) and `:rec` (the recommendations template, which must ride a value that cannot change while mounted). A re-author who ports only `"d<density>:classic"` loses four behaviours. Parity item 68. |
| 25 | 2026-09-12 | **missing → added** | W17, §8 | **Classic's Artist lane is width-gated and FOLDS rather than disappearing.** `IdentityColumns` returns `Artist: classic && showTrackArtist && tier < ClassicArtistFoldTier 4` and flips `ArtistInTitle` true below it (`DetailTrackTableRules.cs:25`, `:33-41`), so "Classic at a narrow width" is the MODERN identity grammar with Classic's chrome. `ShowClassicInlineVideo` + `ClassicInlineVideoDropTier` 4 carry the same shape for the inline film glyph. Parity item 66. |
| 26 | 2026-09-12 | **missing → added** | §6.2 | **The preview-card strips are SELECTION-FOLLOWS-FOCUS.** `WaveePicker.Strip` delegates to `FluentGpu.Controls.RadioButtons`, whose WinUI contract fires `onChange` on every keyboard ROVE (`WaveePicker.cs:258`) — so arrow-keying through Row density, Track list style, Track page layout or the nine Liked cover cards **applies and persists each one in turn**. `LikedCoverPicker.Apply` documents it and treats roving AS the preview (`:183-187`). Every 0.3 writer must therefore be idempotent and cheap. Added with the column-major traversal note (`ColumnMajorUniformToLargestGridLayout`) and parity item 69. |
| 27 | 2026-09-12 | **missing → added** | §7.3 | **A whole readiness section.** Five "chosen but not yet drawable" windows with the answer 0.2.9 already has: R1 the liked list behind a cover treatment (degrade to Stock, and the picker's own `EnsureLiked` warm), R2 the **asynchronous BakedBlur** — the ZStack order (blurred ground UNDER the crisp heart window) is a requirement, not a style choice (`LikedCoverTreatments.cs:258-261`), R3 kind 222 (the tempo cell renders EMPTY, never a dash), R4 kind 185 (the plays cell renders a DASH, deliberately the opposite of R3), R5 the shimmer, which reserves the hero band from the same pure resolver the live hero uses. Parity item 74. |
| 28 | 2026-09-12 | **missing → added** | §5.2, §3.3 | Two motion facts the section had no row for: the **splitter's indicator reveal** (the 2-DIP `PartIndicator` thumb at `Motion.ControlFast`, `IndicatorInset` 4, `Tok.FillControlStrong` — and the shell rails deliberately pass `ShowIndicator: false`, `Splitter.cs:27-36`, `:57-58`), and the **no-cover-art arm of the lyrics backdrop** (row 25b): the drift interval's gate is `drift && art.Length > 0` (`ImmersiveLyricsSurface.cs:165`), so a track with no image is a third state of that preference, not a degenerate ON. Parity items 70-71. |
| 29 | 2026-09-12 | **missing → added** | §1.1, W29, §3.5 | The NPV rows were one level too shallow. Added: the **two option kinds** (`NpvOptionKind.Segmented` / `Swatch`), Finish's FIVE swatch choices including `FromCover = 0` (derive the vinyl colour from the cover), the **three catalog groups** (Media / Devices / Software, `PerGroup = 4`), `NextStyle`'s `(id + 1) % 12` cycle and `TogglePresentation` as writers this chapter's list missed, and the **nothing-playing** arm where the pinned hero composes `new BoxEl()` and the preference is inert (`NowPlayingPanel.cs:525-528`). `SetStyle` is `:35-41`, not `:34-39`. |
| 30 | 2026-09-12 | **missing → added** | §2.2, §3.1, §3.5, W19 | Token gaps: **Title / Artist / Album are STAR tracks, not fixed lanes** (`AlbumStar : TitleStar = 0.75 : 1`, `DetailTracks.cs:561-568`) — the 120 / 90 / 90 numbers are only what `MinWidthFor` charges them; the preview-card **shell** (`WaveePicker.Tile` 116 × 84, radius 8, resting inset 4) and the strip container's wrap/gap/column-major rules; the Liked style **flyout's** own tokens (mini 76, radius 5, 3 columns, WinUI `FlyoutContentPadding` 16/15/16/17) and its below-floor miniature state (the stock cover, DIMMED, under the style's own name); the Camelot swatch's full spec (6 × 6, corner 1.5, opacity 0.85, `DataDotInk` — passthrough in dark, hue-dependent darkening in light); and the player-bar marquee's external hover gate + the 0.30 fade-fraction clamp. |
| 31 | 2026-09-12 | **missing → added** | §7.1, §9.3 | **`IAppSettings?` being null is a composable STATE, not a null-check.** Every reader falls back to the key's own `Default` and every Settings control is `isEnabled: settings is not null`, with the writers early-returning — which is what lets the page mount in a harness showing every default with nothing writable. Added as a §7.1 paragraph and §9.3 finding 8, because the first 0.3 page that dereferences `Platform.Settings` unconditionally makes every later test start the shell. |
| 32 | 2026-09-12 | **missing → added** | §2.1, §6.3 | `TierFor` carries `ModeFor`'s own first-measure rule (`:51`); the WIDENING arm coerces a dipped Vertical answer to 2 as well (`:85`), not only the nominal one; `w <= 0` returns the current mode and `NominalModeFor(0)` is **0**, never Vertical; and `DetailShell` runs a second fail-safe on top of the ladder — it never renders a mode wider than the last measured width supports (`:499-502`). §6.3 also gained the **column-header click** (the sort writer) and the fact that **Row size** rides the More flyout as a submenu when the command bar overflows (`DetailTracks.cs:4370-4372`), so the in-page density control moves rather than disappearing at a narrow width. |
| 33 | 2026-09-12 | **verified, no change** | §0 N1-N12, §2.0-2.2, §3, §4, §5 | Independently re-read against the sources this pass: `AppSettings.cs` (every key + default in the matrix, the four rail pairs at `:144-147`/`:157-160` and the fifth at `:170-171`); `DetailRailPolicy.cs` (180/480 `:25`, `ResizableMode` 0 `:20`, `ClampStored` `:45-46`, `ScopeFor` `:52`, `HasCustomizedRailPrefs` `:58-66`); `DetailLayoutBreakpoints.cs` (820/660/560, 540/580, 24/24, the tier ladder, `EstimatePageWidthFromViewport`); `DetailShell.cs` (`:148-152`, `:168-172`, `:192`, `:200`, `:203`, `:206`, `:212-213`, `:361-375`, `:463-472`, `:499-512`, `:545`, `:624-629`, `:636-638`, `:639-656`, `:750-752`, `:797-817`, `:846-857`); `DetailTrackTableRules.cs` (the 36/40/44/48 · 40/48/56/64 ladder, 32/32/40/48, `PreviewScale` 0.25, `MaxRelief` 8, the yield order, every lane constant); `DetailConfig.cs` (`:196-228`, all six literals); `SettingsPage.Appearance.cs` (every writer `:125-133`, `:208-213`, `:222-247`, `:249-291`, `:295-331`, and every row `:337-404`, the picker widths 160/180/260); `SettingsPage.General.cs` (`:32-43`, `:70-77`, `:84-87`, `:121-127`); `AppearancePrefs.cs`, `LyricsPrefs` (`LyricsView.cs:3020-3072`), `PlayerBarPrefs` (`PlayerBar.cs:1131-1135`), `NpvPlayerPrefs.cs`; `LyricsBlurPolicy.cs`; `ZoomAutoPolicy.cs`; `LikedCoverRules.cs` (`:75-78`, `:100-131`, `:139-150`, `:305-306`); `LikedCoverTreatments.cs` (304 / 180 / 140 / 64 / 128 / 256, Lens's σ 22 · 1.25 · 0.38 · 1.12 · 0.76 · 0.35); `ShellResponsiveLayout.cs`; `SidebarDesign.cs`; `ContextBandLayout.cs`; `CoverPaletteLeaves.cs` (`:92-94`, `:113-115`, `:139`, `:178`, `:242-246`); `ShellMaterialLayer.cs` (`:72-77`, `:81-89`, `:93-98`); `WaveeMotion.cs` (`Standard` 250 `:59`); the engine's `Splitter.cs` (`StripW` 16, 44 / 0.35 / 0.28 / 210) and `Marquee.cs` (`:35-49`, `:102`, `:118`, `:172`). The 14 `TrackArtworkHidden` call sites were re-counted: exactly 14, and the list in §1.2 is correct file-for-file. |
| 34 | 2026-09-12 | **wrong → corrected (arbitration)** | §3.3, §8, §9.3 item 3, §9.4 | arbitration 2026-09-12: **`ContextBand` and `ContextBandLayout` do not live in `Platform/Design.cs`.** This chapter filed the layout class under `Design.cs` in the §8 pure-rules table and budgeted it inside the `Design.cs` §breakpoints row; chapters `03-detail-frame.md` (`:151`, `:880`, `:987`) and `08-artist-and-discography.md` (`§1.2`, `:1107`) both put it in the shared detail frame, and that is now the decision. Their ground is checked and correct: `ContextBandLayout` is declared in `namespace Wavee.Features.Detail`, its `ClipFadeBand` is literally `DetailVerticalLayout.StickyFadeBand` (`ContextBandLayout.cs:44`) and its `Height` 56 is `DetailVerticalLayout.CompactIdentityHeight` (`:78`) by construction — the band's geometry is detail-frame arithmetic, so a `Design.cs` home imports the detail frame's vertical layout into the token file. Corrected: the §8 destination is now `Entities/Detail.cs` (CORE) + `Entities/Detail.UI.cs` (the band helpers), owner **M**, Wave **4.5**; §9.3 item 3 takes it off the ownerless pure-rule list and names the Wave 4.5 slot and its gate; §9.4 carves ≈ 180 lines OUT of the `Design.cs` §breakpoints row (1 300 → 1 120) into a new `Entities/Detail.cs` §band row, so the chapter's ≈ 4 120 total is unchanged; and §3.3 gains a cross-reference to `03-detail-frame.md` stating where the band lives and that this chapter keeps only the preference that decides whether it is on screen. |
