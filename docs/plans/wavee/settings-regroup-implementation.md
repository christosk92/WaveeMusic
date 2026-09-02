# Settings regroup — removals, distinct icons, seven tabs

Approved 2026-09-02 (session plan: onboarding v3 / settings regroup / logs page / dialog fade). Context and the cross-workstream sequencing live in the sibling docs of the same date; this file carries the workstream's real code shapes, component trees and wireframes.

## Workstream B — Settings regroup + removals

### B.1 Removals (delete the key, every reader, every writer, the loc keys, the tests — no compat shims)

| Remove | Key (`Platform/AppSettings.cs`) | Sites to delete / simplify |
|---|---|---|
| **Palette picker, everywhere** | `PaletteId` `:62-63` | `Design/WaveeTheme.cs:16-22,34` (`ResolvePalette`/`ApplyPalette` → always `Tok.NeutralPalette`; `ApplyThemeMode` keeps only the kind); `Program.cs:376`, `WaveeApp.cs:53`, `WaveeShell.cs:2074` (pass `Tok.NeutralPalette`); `SettingsPage.General.cs:218-219, 474-510` (`PaletteRow`); `ProfileMenu.cs:47,143-147,164,217-226,248` (the "Palette" submenu + `onPalette` plumbing); `Design/WaveeTokens.cs:167,294` (`PresetSwatch` if orphaned); tests `LightModeOverhaulTests.cs:191-222` (delete the palette theories; keep `WithTheme` save/restore of `Tok.Palette` if other tests use it), `AppearanceStageModelTests` (file deleted with A). Loc: `settings.appearance.palette*` ×6 in `en-US.json`, `nl.json`, `ko-KR.json`. |
| **Mica Alt** (Mica only) | `WindowMaterialBaseMica` `:105-115` | `Program.cs:512` → `MicaAlt = false` (drop the comment block `:486-495`); `SettingsPage.General.cs:78-82,135,174-180,220-221`; `WaveeShell.cs:1283` comment. Loc `settings.appearance.windowMaterial*`, `.materialMica`, `.materialMicaAlt`. `FluentApp.SetWindowMaterialAlt` stays in the engine, unused by the app. |
| **"Limit page color to the hero"** | `DetailPageToneHeroOnly` `:96-102` | `Features/Detail/DetailShell.cs:521` → `heroOnly = false` path only (delete the hero-band branch); `SettingsPage.General.cs:393-395`. Loc `settings.appearance.pageTone*`. |
| **"Track page layout" (Automatic/Hero)** | `DetailPageLayout` `:93-95` | `Features/Detail/DetailShell.cs:460-468` → always `PageAuto` (delete the `PageHero` branch + `DetailHeroPrefs` `DetailVerticalHero.cs:513-520`); `DetailVerticalLayout.cs:24` drop `PageHero`; `SettingsPage.General.cs:68-72,133,164-169,235,383-397,402-468` (the whole group + wireframe cards); `DetailTracks.cs:1806` comment. Loc `settings.appearance.pageLayout*`, `settings.choice.automatic`, `settings.choice.hero`. |
| **"Run setup again"** | — | `SettingsPage.General.cs:266-273`, loc `setup.runAgain` (Workstream A). |

**Invert the two negatives.** Rename `DisableMarquee` → `MarqueeEnabled` (`appearance.marquee.enabled`, default `true`) and `DisableColorWashes` → `ColorWashesEnabled` (`appearance.colorWashes.enabled`, default `true`). Flip the six readers (`DetailTracks.cs:667`, `PlayerBar.cs:125`, `ArtistPage.cs:65,293`, `DetailShell.cs:191`, `HomePage.cs:136`, `HomeSectionPage.cs:169`, `RecentsPage.cs:311,2478`). Present them as two **flat** rows "Marquee text" / "Color washes" (no "Visual effects" expander, no "All on / 2 disabled" tag — delete `VisualEffectsGroup` `:280-303` and loc `effectsTitle/effectsSub/effectsAllOn/effectsDisabled`). `AppearanceToggle` seeds the switch from `Get(key)` and writes `!Get(key)` — unchanged, now naturally ON = enabled. No migration of the old keys (cosmetic, pre-1.0; a user who had disabled one flips it once).

### B.2 New tab structure (`SettingsPage.cs:17,22,38-46` + partials)

`General · Appearance · Playback · Notifications · Storage · Logs · About` — still the `SelectorBar` strip, 7 tabs (Diagnostics is *renamed* Logs and becomes the full-height log viewer of Workstream C; its non-log content moves out, see below), scroll slugs updated. Rule: **every row carries its own glyph; the section header's glyph is never reused by a row.** Add the missing glyphs to the engine's `glyphs.json` (`C:\wavee\fluent-gpu\src\FluentGpu.Controls\glyphs.json`, flat `"Name": "hex"` map → generated `Icons.*`; note the existing `Brush` is E790, the palette glyph): `Zoom` (E8A3), `Design` (EB3C), `DockLeft` (E90C), `Audio` (E8D6), `LocaleLanguage` (F2B7) — engine gate build after.

| Tab | Section (icon) | Rows (icon → key / control) |
|---|---|---|
| **General** | Language & region (`Globe`) | Language `LocaleLanguage` → `UiCulture` combo |
| | Links (`Link`) | Open spotify: links in Wavee `Link` → `HandleSpotifyLinks` toggle |
| | Graphics (`Devices`) | Preferred GPU `Devices` → `PreferredGpu*` combo (moved from About) |
| | Developer (`Code`) | Developer mode `Code` · FPS overlay `Clock` · Dealer archive `Document` · [dev] Simulate update `Refresh` — moved from the old Diagnostics tab (`DiagnosticsPanel.cs:144-212`) |
| **Appearance** | Theme (`Brush`) | Theme `Sun` → `ThemeMode` segmented · Zoom `Zoom` → `ZoomLevel` combo · Marquee text `Font` → `MarqueeEnabled` toggle · Color washes `Design` → `ColorWashesEnabled` toggle |
| | Lists (`List`) | Row density `RowSize` → `RowDensity` expander (keeps the wireframe strip; child "Always hide track artwork" `Picture` → `HideTrackArtwork`) · Track list style `ViewList` → `TrackRowStyle` expander |
| | Sidebar (`DockLeft`) | Design `SplitView` → `SidebarDesign` expander (child Customize `Edit`) |
| | Lyrics (`Microphone`) | Second line `Globe` → `LyricsSecondaryLine` segmented · Animated backdrop `RefineSparkle` → `LyricsAnimatedBackdrop` toggle |
| **Playback** | (runtime card first, as today) | |
| | Audio (`MusicNote`) | Quality `Headphones` → `PlaybackQuality` · On metered `RadioTower` → `MeteredQualityCap` · Remember volume `Volume` → `RememberVolume` · Autoplay `Play` → `AutoplayEnabled` |
| | Sound (`Speakers`) | Equalizer `Equalizer` → expander · Crossfade `Audio` → expander |
| | Video (`Movie`) | Quality `TvMonitor` → `VideoQuality` · On metered `RadioTower` → `VideoMeteredMaxHeight` · Per-title overrides `Edit` |
| | Player bar (`Pin`) | Show remaining `Clock` → `PlayerBarShowRemaining` |
| **Notifications** | unchanged (already distinct per-topic glyphs); Delivery rows: Windows `Bell`, Sound `Volume`, Quiet hours `Moon`, From/To `Clock` |
| **Storage** | On this PC (`ThisPc`) | Library `Album` · Runtime `Code` · Logs `Document` · Local store `Folder` · Image cache `Picture` (**add loc key** `settings.storage.imageCache` — hard-coded English today `Storage.cs:45`) |
| | Playback cache (`Download`) | Cache audio `Download` · Cache keys `Tag` · Budget `RowSize` · Location `FolderOpen` · Audio bodies `MusicNote` · License keys `Tag` |
| | Metadata cache (`Document`) | Budget `RowSize` · Clear `Delete` · Memory: Resident cache `ThisPc` |
| | Reset (`Delete`) | Factory reset `Delete` |
| **Logs** | Workstream C — the full-height `LogsPanel`, nothing else on the tab |
| **About** | Version hero + `AboutUpdatePanel` (unchanged glyphs: `Refresh`/`Devices`/`Download`/`RadioTower`/`RefineSparkle`) · Links card · **Reports** (`CrashReportsCard`, moved from Diagnostics — it sits next to the "Report a problem" links) · "Wavee right now" receipts (`Info`; add loc key `settings.about.rightNow`) · Licenses (`Document`) |

`SettingsPage.General.cs` splits into `SettingsPage.General.cs` (Language/Links/Graphics/Developer) and `SettingsPage.Appearance.cs` (Theme/Lists/Sidebar/Lyrics); `SettingsPage.About.cs` loses GPU, gains Reports. Section subtitles stay (they are the findability aid). Pure class: `App/SettingsCatalog.cs` — a static table `(Tab, Section, RowId, Glyph)` that the page renders from and a test `SettingsCatalogTests` asserting **no glyph repeats within a section and no row reuses its section glyph** (this is the rule the user complained about; a test keeps it from regressing).

CHANGELOG `Changed`: "Settings regrouped into General / Appearance / Playback / Notifications / Storage / Diagnostics / About, with a distinct icon per row (#n)". `Removed`: "Palette picker (Settings + profile menu), Mica Alt, 'Limit page color to the hero', 'Track page layout' — Wavee now always uses the neutral palette, Mica, and the automatic page layout (#n)". `Changed`: "'Disable marquee text' / 'Disable color washes' are now 'Marquee text' / 'Color washes' on-switches (#n)".

