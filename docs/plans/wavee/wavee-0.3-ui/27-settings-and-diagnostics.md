# Settings pages and diagnostics screens — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Shell/SettingsPage.cs` (291) · `SettingsPage.About.cs` (715) ·
> `SettingsPage.Appearance.cs` (651) · `SettingsPage.General.cs` (251) · `SettingsPage.Notifications.cs` (279) ·
> `SettingsPage.Playback.cs` (478) · `SettingsPage.Storage.cs` (716) · `SettingsPage.VideoOverrides.cs` (319) ·
> `SettingsShared.cs` (89) · `SettingsGlyphs.cs` (87) · `Features/Shell/LogsPanel.cs` (592) ·
> `Features/Shell/VideoOverrideManagerFlyout.cs` (295) · `Components/WaveeEqualizerCurve.cs` (407) ·
> `Design/WaveePicker.cs` (287) · `Features/Feedback/CrashReportsCard.cs` (59) ·
> ~~`Features/Diagnostics/ApiConsolePage.cs` (329)~~ (read for the record; DELETED in 0.3, plan §9.6 Q7, 2026-09-12) · `ConnectDiagnosticsPage.cs` (196) ·
> `PlaybackRuntimeDiagnosticsPage.cs` (454) · `FpsOverlay.cs` (45) · `App/SettingsCatalog.cs` (137) ·
> `App/EqualizerSettings.cs` (47) · `App/NotificationPrefs.cs` (96) · `App/MeteredStatusLine.cs` (73) ·
> `App/VideoOverrideUx.cs` (381) · `App/ZoomAutoPolicy.cs` (114) · `Diagnostics/LogView.cs` (240) ·
> `Diagnostics/LogCapturePolicy.cs` (70) · `Features/Player/LyricsBlurPolicy.cs` (37) ·
> `Features/Detail/DetailRailPolicy.cs` (67) · `Platform/AppSettings.cs` (494)
> **≈ 7 500 lines of surface + 1 100 lines of pure rules**, plus one file this chapter does not read but must
> BUDGET: `Features/Player/LyricsInspectorDialog.cs` (564) lands in `Screens/Diagnostics.UI.cs` by arbitration
> (2026-09-12, A14 — `22-lyrics.md` §9 (d) decided the move and §9.5 below now carries its row).
> | 0.3 target: `Screens/Settings.UI.cs`, `Screens/Settings.cs`, `Screens/Settings.Host.cs`,
> `Screens/Diagnostics.UI.cs`, `Screens/Diagnostics.cs`, `Platform/Controls.cs` | Wave 6 owners **R** (Settings)
> and **S** (Diagnostics)
>
> Paths are cited as they are **today** (`src/apps/Wavee/...`). After Wave 0 the same file is
> `src/apps/_old/Wavee/...` with the identical relative path.
>
> Sibling chapters this one leans on and never re-specifies: **00-design-system.md** (tokens, type ramp,
> `Spacing`, `Radii`, `Tok`, `Elevation`, motion curves), **02-cards-and-controls.md** (the shared small
> controls), **18-shell-frame.md** (the window, the tab host, the content pane width this page is measured
> inside), **19-shell-overlays.md** (toasts, `ContentDialog`, the command palette route into Settings),
> **25-sidebar.md** / **26-sidebar-customizer-and-pipeline.md** (`SidebarDesignPicker.Row`, the Curated
> customizer this page links to), **28-setup-whatsnew-feedback.md** (`ReleaseNotesHero.Pill`,
> `AfterUpdateDialog`, `ReportRequests` / the report dialog, `SetupBody.SignatureSummary`),
> **30-appearance-preferences.md** (what each preference *does* to the rest of the app — this chapter owns only
> how the preference is *presented*).

Drift note: `docs/plans/wavee/settings-regroup-implementation.md` §B.2 says the Appearance › Lyrics group holds
two rows (second line, animated backdrop) and no Now-playing group, and that "Track page layout" is deleted
outright; the shipped code has a third Lyrics row (blur strength, `SettingsPage.Appearance.cs:388-390`), a fifth
Appearance section "Now playing" with two rows (`:395-404`), and Track page layout restored as a collapsed
expander with two sub-rows (`:485-515`). **The code wins.**
`docs/plans/wavee/logs-page-implementation.md` §C.0 says Capture level / File log level live as cascading radio
flyouts in the CommandBar overflow; the shipped code puts them as two headed `ComboBox`es in the filter row and
explains why in a 7-line comment (`LogsPanel.cs:229-235`). **The code wins.**

---

## 0. The non-negotiables

Twelve checkable properties. A rebuild that satisfies all twelve reads as 0.2.9; one that drops any of them does
not.

**N1 — The page is a left-aligned 1000-DIP column inside a 36-DIP page gutter, never a centred column and never
full-bleed.** `SettingsContentColumn` sets `MaxWidth = 1000f` with `AlignSelf = FlexAlign.Stretch`
(`SettingsPage.cs:192-198`, const at `:23`), inside `Padding = (36, 16, 36, 36)` (`:168`). On a 1600-DIP pane
the cards stop at 1000 and the remaining 500 DIP stays empty on the RIGHT. Centring it would be a different
page.

**N2 — Cards are 4 DIP apart, groups are 32 DIP apart.** `SettingsTabStack` gap is `SettingsCardSpacing = 4f`
(`SettingsPage.cs:24,200-206`); every `SettingsSectionHeader` carries `Margin = (0, Spacing.XXXL 32, 0,
Spacing.S 8)` (`:25,231`). The result is a tight stack of near-touching cards punctuated by generous group
breathing room — that rhythm *is* the page.

**N3 — On the four table-driven tabs, every row carries its own glyph and no row ever reuses its section's
glyph.** `SettingsCatalog` (`App/SettingsCatalog.cs:34-120`) is the table; `SettingsGlyphs.Resolve`
(`SettingsGlyphs.cs:17-65`) maps the name to `Icons.*` and `SettingsCatalogTests` pins the invariant. A row that
renders `Icons.Settings` *because its name was never mapped* is a **bug the Debug build asserts on**
(`SettingsGlyphs.cs:70-77`) and logs `settings.glyph.unmapped` once per name in Release.

Three qualifications the catalog states out loud (`SettingsCatalog.cs:20-25`) and 0.3 must keep:
- **Scope is General · Appearance · Playback · Storage only.** **Notifications** is deliberately excluded — its
  rows are one per `NotifyTopic`, enumerated at runtime, and carry their own `Glyph(NotifyTopic)` switch
  (`Notifications.cs:171-181`); its two section headers are literal `Icons.Bell` / `Icons.Settings`
  (`:36, 47`), and `Icons.Bell` is legitimately reused by the section header, two delivery rows and the
  Followers topic. **About** is excluded too: its headers are literal `Icons.Info` / `Icons.Document`
  (`About.cs:111, 113`) and its cards carry literal `Icons.Refresh` / `Devices` / `Download` / `RadioTower` /
  `RefineSparkle` / `StatusWarning`. **Logs** has no rows at all.
- **The invariant is per-SECTION, not global.** `SettingsCatalogTests` asserts (a) no row repeats its own
  section's glyph, (b) no glyph repeats *within* one section, (c) row ids unique per tab, (d) every section has
  ≥ 1 row, (e)/(f) both lookups throw on an unknown id — six facts. Glyphs DO legitimately repeat across
  sections, and far more often than a short list suggests. **The complete cross-section repeat set (17 names,
  section glyphs included)**, re-derived from `SettingsCatalog.cs:34-120`:
  `Document` ×4 (Storage/Metadata-cache SECTION · General/dealerArchive · Storage/logs · Storage/cacheKeys) ·
  `Picture` ×3 (Appearance/hideTrackArtwork · Appearance/npvPresentation · Storage/imageCache) ·
  `RowSize` ×3 (Appearance/rowDensity · Storage/budget · Storage/metadataBudget) ·
  `Delete` ×3 (Storage/Reset SECTION · Appearance/railReset · Storage/clearMetadata) ·
  `List` ×2 (Appearance/Lists SECTION · Storage/Memory SECTION) ·
  `Globe` (General/Language-&-region SECTION · Appearance/lyricsSecondary) ·
  `Code` (General/Developer SECTION · Storage/runtime) ·
  `DockLeft` (Appearance/Sidebar SECTION · Appearance/pageLayout) ·
  `Album` (Appearance/Now-playing SECTION · Storage/library) ·
  `MusicNote` (Playback/Audio SECTION · Storage/audioBodies) ·
  `Pin` (Playback/Player-bar SECTION · Appearance/railUniform) ·
  `ThisPc` (Storage/On-this-PC SECTION · Storage/residentCache) ·
  `Audio` (Playback/crossfade · Storage/cacheAudio) ·
  `Edit` (Appearance/sidebarCustomize · Playback/videoOverrides) ·
  `RadioTower` (Playback/meteredQuality · Playback/videoMetered) ·
  `Clock` (General/fpsOverlay · Playback/playerBarRemaining) ·
  `Settings` (General/developerMode · Appearance/npvStyle — the two deliberate gears below).
  A 0.3 re-author that "de-duplicates" any of these is fighting a test that does not exist and breaking a table
  that does.
- **Two rows render `Icons.Settings` on purpose** — General › Developer › "Developer mode" (`:67`) and
  Appearance › Now playing › "Player style" (`:88`). A gear on those two rows is the design, not a fallback;
  only an accompanying `settings.glyph.unmapped` log line means the fallback fired.

**N4 — A picker never sits in an expander HEADER; the header carries the ANSWER.** Wide content in a
`SettingsCard`'s right-hand `Auto` grid track starves the header's `Star` track to zero width, and a zero-width
text run neither wraps nor clips — the header then paints straight over the content
(`SettingsExpander.cs:65-74`, `SettingsCard.cs:78-82`). Row density, track list style, page layout and sidebar
design therefore all put the card strip in `ItemsHeader` and a `SettingsValueTag`
(14 DIP, `Tok.TextSecondary`, single line, character ellipsis — `SettingsPage.cs:275-278`) in the header.

**N5 — Those four groups are COLLAPSED by default and still answer their own question.** `InitiallyExpanded` is
left at its `false` default for density / track list style / page layout / sidebar design
(`SettingsPage.Appearance.cs:411-425, 452-460, 485-495, 639-648`). Equalizer and Crossfade are the deliberate
exceptions: `InitiallyExpanded = eqOn` / `= crossOn` (`SettingsPage.Playback.cs:184, 269`).

**N6 — The preview cards are live UI, never screenshots, and the selected card tints its WHOLE wireframe.**
`WaveePicker.Ink.For(on)` returns `(AccentDefault, AccentDefault @ A 0.45)` selected and
`(AccentDefault @ 0.58, AccentDefault @ 0.22)` unselected (`Design/WaveePicker.cs:32-34`); the card fills
`Tok.AccentSubtle` and grows its border 1 → 2 DIP *inward* by spending one DIP of its 8-DIP inset, so the
wireframe never shifts a pixel on selection (`:63-82`). The density miniature's row height and art edge are the
REAL numbers scaled by `DetailTrackTableRules.PreviewScale = 0.25f` (`:105-171`,
`Features/Detail/DetailTrackTableRules.cs:31`) — Compact 40→10, Default 48→12, Cozy 56→14, Comfortable 64→16.

**N7 — Switching a tab is instant, and each tab remembers its own scroll offset.** The body swaps with no
transition; only the `ScrollView` is keyed (`Key = "settings:scroll:" + slug`, `ScrollKey = "settings:" + slug`,
`SettingsPage.cs:170`). Adding a page transition here would be a regression, not a polish.

**N8 — The Logs tab is the ONLY tab that is not scrolled by the page.** It gets an unconstrained
`Grow/Shrink/MinHeight=0` lane with `Padding = (36, 16, 36, 16)` so `LogsPanel` owns the full remaining height
and scrolls its own virtualised list (`SettingsPage.cs:149-164`).

**N9 — The storage usage bar is seven fixed hues, never accent tints.** `#4A90D9 · #9B59B6 · #F5A623 · #27AE60 ·
#1ABC9C · #95A5A6 · #E74C3C` (`SettingsPage.Storage.cs:36-42`), each repeated as a 3-DIP left accent bar on its
own row card (`:223-243`) so the bar and the rows are one reading. Accent tints "read as identical blue on dark
themes" — the comment at `:35` is the rule.

**N10 — The equalizer is a direct-manipulation curve, not ten sliders.** Catmull-Rom through the ten band gains,
a filled area to the 0-dB line, a 2.5-DIP accent stroke over a 5.5-DIP 28 %-alpha underglow, 14→18 DIP nodes, a
104×28 value pill that follows the active band, and full keyboard editing
(`Components/WaveeEqualizerCurve.cs:61-152, 218-231, 233-257, 285-329`). Gains snap to 0.5 dB
(`:75`). Replacing it with sliders loses the feature.

**N11 — Every destructive action confirms through the SAME dialog shape, and every completed one toasts.**
`SettingsShared.Confirm` (`SettingsShared.cs:29-41`) builds a `ContentDialog` with `DefaultButton = Close` —
the cancel button is the default on every destructive confirm. **Eight** call sites in this surface: rail reset
(`Appearance.cs:362`), delete old logs (`Storage.cs:368`), clear audio cache (`:388`), clear keys (`:395`),
clear metadata (`:404`), factory reset (`:420`), clear-all video overrides (`VideoOverrides.cs:157`), clear log
view (`LogsPanel.cs:195`). (`SettingsShared.Confirm` has five further call sites elsewhere in the app —
`ContainerActions.cs:236`, `FolderActions.cs:257`, `WaveeActionDescriptor.cs:139`,
`PlaylistInlineEdit.cs:1143`, `HistoryPage.cs:569` — which belong to other chapters.)

**N12 — Nothing in this surface reads a live clock per frame.** The About receipts tick on a 5 000 ms
`UseInterval` (`SettingsPage.About.cs:252, 280`); the log tail polls at 750 ms and only bumps when
`WaveeLog.Instance.Version` actually moved (`LogsPanel.cs:86-91`); the FPS overlay renders **exactly once** and
lets the host refresh two retained `DynamicText` slots in place (`FpsOverlay.cs:16-21, 37-42`). An FPS HUD that
re-renders per frame depresses the very number it displays — that regression is documented at `FpsOverlay.cs:15-21`
and must not come back.

---

## 1. Anatomy

### 1.1 The 0.2.9 tree

```
SettingsPage : Component                                       Features/Shell/SettingsPage.cs:16
│  signals: _tab(0) :29 · _uiEpoch(0) :30 · _density(1) Appearance.cs:33 · _language(0) General.cs:27
│           _quality(2) _videoQuality(0) _meteredVideoQuality(1) _eqPreset(0) _crossSecs(5.0) _crossSlider(5f)
│                                                                             Playback.cs:31-41
│           _lyricsBlurSlider(100) Appearance.cs:39 · _storageLoad(NotStarted) Storage.cs:45
│           _bodyBudgetMode/_bodyBudgetPreset/_bodyBudgetGiB/_bodyBudgetPercent Storage.cs:29-32
│           _metaBudget(1) _metaTick(0) Storage.cs:52-53 · _voLoad/_voVersion VideoOverrides.cs:30-31
│  fields:  _overlay _voPost _nav _nc :32-40 · _zoomLive _viewportLive Appearance.cs:45,51
│           _storage _storageError Storage.cs:46-47 · _metaStats Storage.cs:55 · _voRows/_voHandle/_voAnchor
│  hooks (UNCONDITIONAL, above the tab switch — a conditional hook breaks hook ORDER):     :67-88
│    UseContext(InputHooks.Current) · Services.Slot · ThemeControl.Request · UsePost() ·
│    Overlay.Service · Viewport.Zoom · Viewport.Size · HistoryStore.NavCtx · NotificationCenterBridge.Slot
│  effects: seed-from-settings (DepKey.Empty) :90-104 · video-override deep link :115-122 ·
│           per-tab load/teardown :126-137 · WatchVideoOverrides (DepKey.Empty) :141
│
├─ Header()                                          role: page masthead              :239-248
│   ├─ Icon(Icons.Settings, 24, TextPrimary)                                           :245
│   └─ WaveeType.PageHero(Strings.Settings.Title)  (Ui.Title 28/36/600)                :246
├─ BoxEl{Dir=1, Pad=(36,0,36,0)}                     role: tab strip lane              :178-186
│   ├─ SelectorBar.Create(TabLabels(), _tab)         role: 7-tab selector              :183
│   └─ Divider()                                     1 DIP StrokeDividerDefault        :184
└─ content                                                                             :158-170
    ├─ [tab == Logs]  BoxEl{Grow,Shrink,MinH=0,Dir=1,Pad=(36,16,36,16)}                :159-164
    │    └─ Embed.Comp(() => new LogsPanel(svc?.Settings))                              :152
    └─ [else] ScrollView(BoxEl{Dir=1, Pad=(36,16,36,36)}) Key/ScrollKey per tab slug    :165-170
         └─ SettingsContentColumn(body)  MaxWidth=1000, AlignSelf=Stretch               :192-198
              └─ SettingsTabStack(...)   Dir=1, Gap=4, AlignSelf=Stretch                :200-206
                   ├─ SettingsSectionHeader(title, glyph, subtitle?)  Margin=(0,32,0,8) :211-237
                   │    ├─ Icon(glyph, 16, TextSecondary) Margin=(0,2,0,0)              :235
                   │    └─ BodyStrong(title) [+ Caption(sub) TextSecondary wrap max 2]  :217-222
                   ├─ SettingsRow(...)    → SettingsCard.Create                         :250-264
                   ├─ SettingsExpander.Create(...)                                      (per group)
                   │    ├─ header card (transparent, Pad=(16,16,4,16), MinH=68)  SettingsExpander.cs:30-39
                   │    ├─ chevron 32×32, Margin right 8                                SettingsExpander.cs:161-170
                   │    ├─ ItemsHeader → SettingsExpanderPanel(content) Pad=(16,12,16,12) SettingsPage.cs:285-290
                   │    └─ Items[] separated by 1-DIP StrokeDividerDefault              SettingsExpander.cs:133-141
                   └─ Embed.Comp(...)     (the four child Components, below)
```

**The five embedded child Components** — each exists because `AboutTab` / `GeneralTab` / `AppearanceTab` are
*not* render bodies (the tab switch calls them conditionally), so a hook called from one would be a conditional
hook:

| Component | File:line | Why it is a Component | Key |
|---|---|---|---|
| `GpuPickerCard` | `SettingsPage.General.cs:184-250` | `UseEffect` seeds the selection; the DXGI walk is a frozen ctor field (`:188`) | — |
| `SidebarSettingsCard` | `SettingsPage.Appearance.cs:606-650` | `UseContext(SidebarPreferences.Slot / Services.Slot / HistoryStore.NavCtx)` | `"appearance.sidebar.design"` |
| `AboutUpdatePanel` | `SettingsPage.About.cs:405-715` | `UseObservable(upd.Changed)` + three `UseSettingSignal`s + `UseContext(Overlay.Service)` | `"about:update"` (`:108`) |
| `WaveeNowReceipts` | `SettingsPage.About.cs:248-393` | `UseInterval(Tick, 5000f)` | — |
| `CrashReportsCard` | `Features/Feedback/CrashReportsCard.cs:16-59` | `UseMemo` listing on disk, once at mount | per-row `Key` `:40,55` |
| `LogsPanel` | `Features/Shell/LogsPanel.cs:37` | 12 signals + 4 hooks + an `ItemsViewController` | — |
| `WaveeEqualizerCurveCore` | `Components/WaveeEqualizerCurve.cs:27` | `UsePropsOrDefault` + `UseMeasuredWidth` | props-channel (`:24`) |
| `VideoOverrideManagerFlyout` | `Features/Shell/VideoOverrideManagerFlyout.cs:23` | own view/query/focus signals | `"vo-view:root"` / `"vo-view:all"` `:54` |

**Diagnostics pages** (separate routes, not tabs):

```
PlaybackRuntimeDiagnosticsPage : Component   Features/Diagnostics/PlaybackRuntimeDiagnosticsPage.cs:26
│  Route = "playback-diagnostics" :30 · ContentMaxW = 1000f :32 · _refresh signal :34
├─ PageHeader()  Icon(MusicNote,22) + PageHero(playback.runtime.diagnosticsTitle)      :384-393
└─ ScrollView(BoxEl{Dir=1, Gap=12, MaxW=1000, Pad=(16,16,16,24)})  ScrollKey=Route     :66-72
    ├─ CompiledInCard  → Status(glyph, colour, heading, body)                          :80-93, 435-453
    ├─ StatusSection   Card("Current status", 7 Rows)                                   :95-102
    ├─ LocateSection   Card("Locate", 2 Rows)                                           :104-106
    ├─ CandidatesSection  Card("Candidates", per-candidate name + 2 Chips + path)       :110-134
    ├─ VerifySection   Card("Verify", outcome/detail/trust + 6 signature Rows)          :136-157
    ├─ ModulesSection  Card("Playback modules", per-module block + "Refused" list)      :163-235
    ├─ UpdatesSection  Card(diagnostics.updates.title, 7 + 6 Rows + Repair link)        :247-303
    ├─ Actions         HStack(8): Copy diagnostics (Accent) · Open log folder · Refresh :305-313
    └─ Caption("This report is what the provisioner already computed…")                 :57-58

ConnectDiagnosticsPage : Component           Features/Diagnostics/ConnectDiagnosticsPage.cs:24
│  Route = "connect-diagnostics" :27 · _tick signal :33 · 3 UseEffect subscriptions :50-71
├─ PageHeader()  Icon(Devices,22) + PageHero("Connect diagnostics")                    :155-164
└─ ScrollView(same chrome)  ScrollKey=Route                                             :90-95
    └─ OwnerCard(7) · ClusterCard(8) · PutCard(6) · EchoCard(6) · HostCard(3) · Caption :102-148

ApiConsolePage : Component (developer-mode only)   Features/Diagnostics/ApiConsolePage.cs:17
│  ── 0.2.9 RECORD ONLY: DELETED in 0.3 with its four ApiDebug* helpers (plan §9.6 Q7, 2026-09-12) ──
│  ContentMaxW = 1000 :19 · FieldW = 920 :20 · 9 int signals + 7 string signals :23-38
├─ PageHeader()  Icon(Code,22) + PageHero("API Console")                                :290-299
└─ ScrollView(BoxEl{Dir=1, Gap=12, MaxW=1000, Pad=(16,16,16,24)}) ScrollKey="api-console" :198-238

FpsOverlay : Component                        Features/Diagnostics/FpsOverlay.cs:22
└─ one pill: Fill = FillSolidBase @ A 0.90, Border 1 StrokeCardDefault, r=6, Pad=(8,4,8,4) :30-44
   mounted by WaveeApp.cs:430-436 inside a Grow=1 HitTestPassThrough positioner, Pad=(0,104,14,0),
   gated on `DeveloperMode.Enabled && DeveloperMode.FpsOverlay` read INSIDE Render (`:429`), never an env var
```

**How each diagnostics page is REACHED in 0.2.9 — and the one that effectively cannot be.** The three pages
above are ordinary shell routes, but only two of them are wired up as such:

| page | route key | in `ShellRoutes.s_exact` | arm in `ShellNav.Dest` | pinnable | in-app entry points |
|---|---|---|---|---|---|
| `PlaybackRuntimeDiagnosticsPage` | `playback-diagnostics` | **yes** `ShellRoutes.cs:40` | **yes** → `Strings.Nav.PlaybackRuntime` + `Icons.MusicNote` `ShellNav.cs:71` | yes `SidebarPinId.cs:79` | Settings › Playback error InfoBar `Playback.cs:157-158`; the setup card `PlaybackRuntimeSetupCard.cs:362`; `wavee://` deep link, developer-mode-gated `WaveeShell.cs:1826-1831` |
| ~~`ApiConsolePage`~~ | ~~`api-console`~~ | ~~**yes** `ShellRoutes.cs:39`~~ | ~~**yes** → `Strings.Nav.ApiConsole` + `Icons.Code` `ShellNav.cs:67`~~ | ~~yes `SidebarPinId.cs:79`~~ | ~~sidebar dev-tools row `SidebarBuiltInDocuments.cs:46`, `SidebarTemplates.cs:107`; deep link, dev-gated `DeveloperMode.cs:61`~~ — **struck, 2026-09-12 (plan §9.6 Q7): the page and its route are DELETED in 0.3; this row records how 0.2.9 reached it** |
| `ConnectDiagnosticsPage` | `connect-diagnostics` | **NO** — absent from `ShellRoutes.cs:26-44` | **NO** — falls through to the default, "Your Library" + `Icons.MusicNote` `ShellNav.cs:50-79` | no `SidebarPinId.cs:79` | **exactly one**: the "Connect diagnostics" `HyperlinkButton` in the Settings › Playback **error** InfoBar `Playback.cs:162` — and that InfoBar is composed only when the runtime status is neither Ready nor NeverAttempted (`:116-143`) |

The last row is a 0.2.9 defect, not a design: `ContentHost.PageFor` renders the route (`ContentHost.cs:244-246`)
but `ShellRoutes` refuses it, so `wavee://open?route=connect-diagnostics` is logged `deeplink.route.unknown` and
dropped (`WaveeShell.cs:1819-1823`) and a history row for the page renders dimmed and inert
(`HistoryPage.cs:414-418`). On a healthy install — runtime Ready — there is no route into the page at all.
§9.6 owns the fix and 18-shell-frame.md §7 records the same defect from the shell's side.

### 1.2 The 0.3 tree

Everything below is a **static function over the settings store + a host snapshot**, not a bound item view — see
§9 for why plan §4.12/§4.13's `BoundItemScope` idiom does not reach this surface.

```
Screens/Settings.cs          CORE (engine-free, testable)
  Settings.Catalog            the (Tab, Section, RowId, Glyph) table                     ← SettingsCatalog.cs verbatim
  Settings.Glyph(tab,id)      name → Icons.* resolver + Debug.Fail + once-per-name warn   ← SettingsGlyphs.cs verbatim
  Settings.Eq                 ReadGains / SerializeGains / PresetIds / PresetGains        ← EqualizerSettings.cs + Playback.cs:20-29
  Settings.Zoom               ZoomAutoMode, ZoomAutoPolicy.Suggest/MigrateMode            ← ZoomAutoPolicy.cs verbatim
  Settings.Metered            MeteredStatusLine                                           ← MeteredStatusLine.cs verbatim
  (no Settings.Notify)        NotificationPrefs + the policy ladder live in Platform/Notify.cs (CORE, owner I,
                              Wave 4 — arbitration 2026-09-12, A9: ONE notification stack, shared with the panel
                              in 19-shell-overlays.md and the OS toasts in 14-os-surfaces.md). The Notifications
                              tab CALLS `Notify.Prefs.AllTopics` / `.Level` / `.SetLevel` / `.Policy` /
                              `.ShowsCategory`; it does not own them, and it must not carry a second copy of the
                              topic order.
  Settings.Storage            FmtBytes, BodyBudgetIndex, budget ladders, StorageSnapshot  ← Storage.cs:27-28,50-51,206-212,245-255
  Settings.Overrides          VideoOverrideUx (BuildRoster/Search/RecentlyAdded/…)        ← VideoOverrideUx.cs verbatim

Screens/Settings.UI.cs       UI (pure over a Model struct + the store)
  static Element Page(Signal<int> tab)                                  ← SettingsPage.Render :65-190
  static Element Header()                                               ← :239-248
  static Element TabStack(params Element[])                             ← :200-206
  static Element SectionHeader(string title, string? glyph, string? sub)← :211-237
  static Element Row(...)  / Item(...) / ValueTag(string) / Panel(El)   ← :250-290
  static Element GeneralTab(in Model m)                                 ← General.cs:65-106
  static Element AppearanceTab(in Model m)                              ← Appearance.cs:156-405
  static Element PlaybackTab(in Model m)                                ← Playback.cs:62-105
  static Element NotificationsTab(in Model m)                           ← Notifications.cs:24-56
  static Element StorageTab(in Model m)                                 ← Storage.cs:296-424
  static Element AboutTab(in Model m)                                   ← About.cs:101-117
  sealed class GpuPickerCard / SidebarSettingsCard / AboutUpdatePanel / NowReceipts : Component
  sealed class VideoOverrideFlyout : Component                          ← VideoOverrideManagerFlyout.cs

Screens/Settings.Host.cs     SHELL (every side effect, off the UI thread)
  StorageCensus()  → Task<StorageSnapshot>                              ← Storage.cs:129-204
  MetadataCensus() → Task<CacheStats?>                                  ← Storage.cs:59-69
  DeleteOldLogs / ClearAudioBodies / ClearLicenseKeys / ClearMetadata / FactoryReset
  RelocateCache (the 2-dialog flow)                                     ← Storage.cs:618-675
  OverrideRoster()  → Task<VideoOverrideRow[]>                          ← VideoOverrides.cs:75-98
  OpenFolder / Confirm / FilePicker wrappers                            ← SettingsShared.cs:18-41

Screens/Diagnostics.cs       CORE
  LogView (Build / Categories / CopyText / FormatTime / SessionLabel / RemountKey / MetaLine …) ← LogView.cs verbatim
  LogCapturePolicy (Resolve / ToSetting / IsVerbose / EffectiveFileLevel / Set*)                ← LogCapturePolicy.cs verbatim
  RuntimeReport.Build(diag, status) → string                                                     ← PlaybackRuntimeDiagnosticsPage.cs:317-374

Screens/Diagnostics.UI.cs    UI
  static Element LogsPanel(IAppSettings?)          ← LogsPanel.cs  (mounted BY Settings — see §9 seam note)
  sealed class RuntimePage / ConnectPage : Component   (ApiConsolePage DELETED — plan §9.6 Q7, 2026-09-12; no arm for it here)
  static Element FpsPill()                         ← FpsOverlay.cs:30-44
  Diagnostics.LyricsInspector — the button target + the dialog (Providers / Raw / Parsed tabs, ≈600)
                                                   ← Features/Player/LyricsInspectorDialog.cs (564), arriving from
                                                     22-lyrics.md §9 (d) / A14; `Rail.UI.cs` only calls
                                                     `Open(overlay, trackId)` and it reads `Lyrics.Diagnostics`
  static Element Card(title, rows) / Row(label,value) / Chip(label,present) / Status(...)
                                                   ← the shared diagnostics chrome, ONE copy (today it is
                                                     duplicated verbatim in two files: Runtime :395-431 and
                                                     Connect :166-195)

Platform/Controls.cs         UI primitives shared beyond Settings
  EqualizerCurve(gains, onBandChanged, isEnabled, height?)   ← WaveeEqualizerCurve.cs
  Picker.Ink / Picker.Card / Picker.Label / Picker.Strip / Picker.DensityRows / ModernRow / ClassicRow
                                                             ← Design/WaveePicker.cs
```

**Props-freeze map (what reaches a child, and how).** Component props freeze at mount; the settings surface
solves that four different ways and the re-author must keep each one:

| Child | Changing data | Channel | Evidence |
|---|---|---|---|
| `SettingsCardCore` | every option, every render | **re-pushed props** (`Embed.Comp(options, …)` + `UseProps`) | `SettingsCard.cs:113-114, 328` |
| `SettingsExpanderCore` → `Expander` | header/content/parts | **re-pushed props** (`Expander.ExpanderSlots`); only `InitiallyExpanded` stays a frozen field | `SettingsExpander.cs:184-193` |
| `WaveeEqualizerCurveCore` | `Gains`, `OnBandChanged`, `IsEnabled`, `Height` | **re-pushed props record** — record equality gates the re-render | `WaveeEqualizerCurve.cs:22-24` |
| `VideoOverrideManagerFlyout` | the roster (rebuilt off-thread) | **`Func<>` thunk + an `IReadSignal<int>` version** | `VideoOverrideManagerFlyout.cs:27-30` |
| `NumberBox` in the crossfade row | its `IsEnabled` | **`Key` remount** `"crossfade-duration-on"/"-off"` | `SettingsPage.Playback.cs:240` |
| `ComboBox` (session / category / capture / file level) | its item list | **`Key` remount** on the revision that changed the frozen input | `LogsPanel.cs:165, 260, 264, 268` |
| `CommandBar` | its command lists (frozen fields) | **`Key` remount** on `BarKey()` = live/newest/group/wrap/levels | `LogsPanel.cs:167-174` |
| `ItemsView` (log list) | the visible SET | **`Key` remount** on `LogView.RemountKey(...)`, `ScrollKey` restores the offset | `LogsPanel.cs:441-449` |
| every tab | any settings write | **`_uiEpoch` bump read at `:106`** — the store is not observable | `SettingsPage.cs:42, 106` |
| Appearance rows | writes made ELSEWHERE (palette, chords, sidebar menu) | **foreign epochs read in render**: `PlayerBarPrefs.Epoch` `:107`, `NpvPlayerPrefs.Epoch` Appearance.cs:184, `prefs.Design.Value` Appearance.cs:619, `NotificationPrefs.Epoch` Notifications.cs:27 | — |
| the four picker groups | a design/density switch | **stable `Key`** so the card never remounts and the silhouette never changes shape | Appearance.cs:425, 460, 495, 648 |

---

## 2. Wireframes

Scale: **1 monospace char ≈ 8 DIP**. "pane" is the shell content-host width (18-shell-frame.md); the settings
card width is `min(pane − 72, 1000)`. The two `SettingsCard` reflow thresholds are measured on the CARD's own
width (`SettingsCardCore.OnBoundsChanged`, `SettingsCard.cs:333-339`), so:

| card width | state | threshold | source |
|---|---|---|---|
| ≥ 476 | header left / content right, one row | — | `SettingsCard.cs:31, 129` |
| 286 … 475 | **wrapped**: content drops below the header, 8 DIP apart, left-aligned | `WrapThreshold = 476f` | `:31, 129, 137-138, 273` |
| < 286 | wrapped **and the 20-DIP header glyph is dropped** | `WrapNoIconThreshold = 286f` | `:32, 130, 201` |

Three things that table does not say and the re-author needs:

- **`ContentAlignment.Vertical` forces the wrapped layout at EVERY width** — `wrap = width < WrapThreshold ||
  Alignment == Vertical` (`SettingsCard.cs:129`). That is how the EQ curve item gets its full-width lane
  (`Playback.cs:210`); it is not a narrow-window state.
- **`ContentAlignment.Left` bypasses the whole thing.** It returns `Root(o, s, BuildLeftContent(...))`
  (`:126-127, 281-292`): no header, no glyph, no action chevron, no threshold — just the content, start-aligned,
  inside the card padding. The About links card and the receipts card are both `Left`.
- **The first frame always renders UNWRAPPED.** `SettingsCardCore` measures itself through `OnBoundsChanged`
  into a signal that starts at 0 and falls back to **720** (`:322-339`), so every card builds its ≥ 476 shape
  once before the real width arrives. Do not "fix" that with a pre-pass; it settles on the next frame.

There is **no hysteresis** on either threshold — the card re-measures and re-decides on every bounds change
(the only damping is a 0.5-DIP write deadband on the measured width, `:336-337`).
In pane terms: wrap at pane < 548, glyph drop at pane < 358. `SettingsExpander` items set both thresholds to
`0f` (`SettingsExpander.cs:45-47`), so an expander ROW never wraps and never drops its glyph, at any width.

### W1 — General tab, fully loaded @ pane 1072 (card 1000)

```
┌ pane ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                                                        │
│ 36                                                                                                                     │
│ ⚙ 24   Settings                                                            ← Icon(Settings,24) + Ui.Title 28/36/600    │
│ ↕12                                        ← the masthead's BOTTOM pad is Spacing.M 12, not 16 (SettingsPage.cs:242)   │
│ General   Appearance   Playback   Notifications   Storage & cache   Logs   About                                       │
│ ▔▔▔▔▔                                                          ← 16×3 accent pill, flush with item bottom              │
│ ────────────────────────────────────────────────────────────────────────────────────────────────────────────── 1 DIP  │
│ ↕16                                                                                                                    │
│ 🌐 Language & region                       ← 16 DIP glyph · BodyStrong 14/20/600      margin-top 32, margin-bottom 8   │
│    The language Wavee's own interface uses ← Caption 12/16 TextSecondary, wrap, max 2 lines                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ card 1000 × ≥68    │
│ │ ⌨ 20     App language                                     ┌ System default            ▾ ┐ 260 │ r=4, 1 DIP stroke   │
│ │ ←16→ ←20→ Changes are applied the next time Wavee starts. └──────────────────────────────┘     │ Pad 16 all         │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕4                                                                                                                     │
│ 🔗 Links                                                                                                               │
│    What Wavee opens from other apps                                                                                    │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐                    │
│ │ ⧉  Open spotify: links in Wavee                                               (  ●) 40×20 track│                    │
│ │    Off by default. Turning this on makes Wavee the handler for spotify: links…                 │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕4                                                                                                                     │
│ 🖥 Graphics                                                                                                            │
│    Which GPU Wavee renders on                                                                                          │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐                    │
│ │ 🖳  Graphics adapter                          ┌ Automatic (recommended)             ▾ ┐ 300     │                    │
│ │     Switching applies immediately — the window may flicker briefly…                            │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ items: "Automatic" │
│          + one per adapter; the LIVE one reads `Gpu.InUse(name)` — "<name> (in use)" (General.cs:214-220)              │
│ ↕4                                                                                                                     │
│ ⟨⟩ Developer                                                                                                           │
│    Debugging and inspection tools for Wavee itself                                                                     │
│ ┌ ⚙  Developer mode           Shows ~~the API console,~~ lyrics inspector…                     (  ●) ┐ 0.2.9 copy;  │
│    0.3 drops "the API console," from this caption — Q7, 2026-09-12, the console is deleted                          │
│ ┌ 🕐 FPS overlay              Frame timing in the corner of the window            [disabled]    ┐ ← isEnabled: dev    │
│ ┌ 📄 Archive Spotify realtime traffic   Writes every dealer frame to disk…                 (  ●) ┐                    │
│ ┌ ⟳  Simulate an update       Walks the update state machine locally…       [ Run simulation ]  ┐ ← dev-only, ELIDED  │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕36 (bottom page padding)                                                                                              │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```
`SettingsPage.General.cs:79-105` · toggles are `SettingsCard.CompactToggleStyle()` = `ToggleSwitch.DefaultStyle
with { MinWidth = 0, MinHeight = 36 }` (`SettingsCard.cs:116-120`); the track is 40 × 20, knob circle r = 10
(`ToggleSwitch.cs:59-60, 347-349`).
Two more gates on this tab: **FPS overlay** is `isEnabled: dev` — present but greyed while developer mode is off
(`General.cs:126`), never hidden; **Simulate an update** is composed away entirely when dev is off and is
additionally `IsEnabled = nc is not null` (no `NotificationCenterBridge` → a greyed row, `:152`). The **App
language** combo ships four items of which two are `itemEnabled = false` (`nl`, `ko-KR`) — shown, greyed,
unselectable, with a belt-and-suspenders reject in `SetLanguage` (`:29-43, 73`).

### W2 — Appearance tab, all four groups collapsed @ pane 1072

```
│ 🖌 Theme                         Theme, zoom and shell effects                                                          │
│ ┌ ☀  Theme      System follows the Windows setting live        [ System │ Light │ Dark ]        ┐ ← SelectorBar, 3 items│
│ ┌ 🔍 Zoom       Scale the whole app, like Ctrl + / Ctrl −      ┌ Auto (125%)            ▾ ┐ 160 ┐ ← 13 items (Auto+12) │
│ ┌ 𝐀  Marquee text        Scroll overflowing titles…                                       (  ●) ┐                       │
│ ┌ ◈  Color washes        Tint the shell and page surfaces from artwork                    (  ●) ┐                       │
│                                                                                                                         │
│ ☰ Lists                          How track rows and lists look                                                          │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ expander, collapsed │
│ │ ⌸ Row density      Choose how much music fits on screen…              Default            ⌄ 32  │ header Pad(16,16,4,16)│
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ MinH 68            │
│ ┌ ☷ Track list style   Choose artwork-forward rows or a classic column table.   Modern     ⌄    ┐                      │
│ ┌ ◨ Track page layout  Use the side rail automatically, or the artwork Hero…   Automatic   ⌄    ┐                      │
│                                     ↑ SettingsValueTag: 14 DIP TextSecondary, 1 line, char-ellipsis                    │
│ ◧ Sidebar                        The left-hand navigation                                                              │
│ ┌ ⊞ Design           Choose the left-hand navigation. Applies immediately.     Classic     ⌄    ┐                      │
│     └─ opened, its body is SidebarDesignPicker.Row(compact: true) and its ONE item row — ✎ "Customize sidebar" ›      │
│        — exists ONLY while Wavee Curated is the active design (SidebarDesignGating.CanCustomize, Appearance.cs:629)   │
│                                                                                                                         │
│ 🎙 Lyrics                        The reading surface for synced lyrics                                                  │
│ ┌ 🌐 Lyrics second line  Show a translation or a romanization…   [ Off │ Translation │ Romanization ] ┐                 │
│ ┌ ✨ Animated lyrics backdrop  Let the blurred cover art drift slowly…                     (  ●) ┐                      │
│ ┌ ⧩ Lyrics blur    Depth-of-field and glow blur. 0 turns them off…   ━━━━━●━━━ 180  Auto  ┐ ← link only when pinned    │
│                                                                                                                         │
│ ⏺ Now playing                    The player styles behind the pinned cover                                              │
│ ┌ 🖼 Hero         Show the cover or a player above the track details    [ Cover │ Record ]      ┐ ← 2nd label follows   │
│ ┌ ⚙  Player style Which of the twelve players shows when Player is selected  ┌ Record  ▾ ┐ 180  ┐   the current style  │
```
`SettingsPage.Appearance.cs:333-404`. Zoom labels: index 0 = `"Auto (N%)"` with the LIVE resolved percent
(`:87-93, 206`), indices 1…12 = `ZoomLadder.Steps` = 50 · 67 · 75 · 80 · 90 · 100 · 110 · 125 · 150 · 175 ·
200 · 250 % (`fluent-gpu/src/FluentGpu.Engine/Foundation/ZoomLadder.cs:17-18`).

**Three facts the sketch above flattens.**

- **`ZoomAutoMode` has THREE values, and the combo shows TWO shapes.** `Math.Clamp(ZoomMode, 0, 2)` →
  Auto · Dense · Manual (`Appearance.cs:189-190`). `zoomIndex` is `0` for **both Auto and Dense** — the single
  "Auto (N%)" head item — and only `Manual` selects a ladder rung, via
  `1 + IndexOf(Steps, Snap(_zoomLive))` (`:203-205`). Picking the head item writes `ZoomMode = Auto` (never
  Dense) and applies *this render's* `autoSuggested` immediately (`:225-231`); picking a rung writes
  `ZoomMode = Manual` (`:233-239`). A 0.3 re-author that models the row as a two-state (auto/manual) toggle
  loses the Dense arm's ability to round-trip through the picker.
- **The lyrics-blur control is a slider PLUS a conditional link, in a 12-DIP gap row.** `Slider` Min 0 ·
  Max 100 · Step 1 · **TickFrequency 25** · length 180 · thumb tooltip `"N%"` (`:141-147`). The whole row is
  just the slider while the stored value is `-1` (auto); an explicit value adds a `HyperlinkButton` "Auto" at
  `Gap = Spacing.M 12` (`:148-153`). Both halves take `isEnabled: settings is not null`. "Auto" writes `-1`
  back — it does **not** write whatever `Resolve` currently returns (`:318-325`).
- **Every row on this tab writes through a DIFFERENT epoch, on purpose.** Marquee / Color washes / Animated
  backdrop / Hide-artwork → `AppearancePrefs.Bump()`; Track list style → `AppearancePrefs`; Page layout,
  rail-uniform and rail-reset → `DetailHeroPrefs`; Lyrics second line and blur → `LyricsPrefs`; both Now-playing
  rows → `NpvPlayerPrefs` **and no page `Bump()` at all** (the tab already reads `NpvPlayerPrefs.Epoch` at
  `:184`). Collapsing these into one epoch would re-render every mounted surface on every settings write.

### W3 — Row-density expander OPEN @ pane 1072

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ⌸ 20  Row density                                                       Default              ⌃ 32 │ header 68 tall
│       Choose how much music fits on screen; the miniature rows preview the real spacing.           │
├────────────────────────────────────────────────────────────────────────────────────────────────────┤ ← no divider above ItemsHeader
│   ↕12                     ItemsHeader = SettingsExpanderPanel, Pad = (16,12,16,12)                 │
│   ┌ 116 ─────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐   ← WaveePicker.Tile 116 × 84, inset 8, gap 4│
│   │▪▬▬ ▬ ▬  │ │▪ ▬▬▬ ▬ ▬ │ │ ▪ ▬▬▬ ▬ ▬│ │ ▪  ▬▬▬ ▬ │   r = Radii.Card 8                          │
│   │▪▬▬ ▬ ▬  │ │▪ ▬▬▬ ▬ ▬ │ │ ▪ ▬▬▬ ▬ ▬│ │ ▪  ▬▬▬ ▬ │   selected: Fill AccentSubtle, border 2 accent│
│   │▪▬▬ ▬ ▬  │ │▪ ▬▬▬ ▬ ▬ │ │ ▪ ▬▬▬ ▬ ▬│ │ ▪  ▬▬▬ ▬ │   unselected: FillCardDefault, border 1      │
│   │ 84      │ │          │ │          │ │          │     StrokeControlDefault                      │
│   └──────────┘ └──────────┘ └──────────┘ └──────────┘   gap between cards = Spacing.M 12           │
│    ↕8 Compact      Default      Cozy       Comfortable  ← Picker.Label 12/16, 600+TextPrimary when on│
│                    ▔▔▔▔▔▔▔                                 400+TextSecondary otherwise              │
│   miniature row heights (real height × 0.25): 10 · 12 · 14 · 16 DIP                                 │
│   miniature art edges  (real edge  × 0.25):    8 ·  8 · 10 · 12 DIP                                 │
├──────────────────────────────────────────────────── 1 DIP StrokeDividerDefault ────────────────────┤
│ 🖼 Always hide track artwork                                                              ☐ 20    │ item MinH 52
│    Remove cover thumbnails from track rows…                          Pad = (58, 8, 44, 8)          │ CheckBox box 20,
└────────────────────────────────────────────────────────────────────────────────────────────────────┘ hit-box 32×32
```
`SettingsPage.Appearance.cs:411-449` · `Design/WaveePicker.cs:44, 63-82, 105-171, 225-231, 271-286` ·
checkbox override `MinWidth = MinHeight = Spacing.XXXL = 32` (`Appearance.cs:434`).

**The picker strip WRAPS — the one reflow state every picker group has.** `RadioButtons` is column-major and
never wraps by itself; `WaveePicker.s_strip` overrides `PartGrid` with `Wrap = true, Gap = Spacing.M 12` and
`PartColumn` with `Shrink = 0` (`WaveePicker.cs:247-254`), so a strip of fixed-width cards **drops to fewer
columns** instead of overflowing. `maxColumns` defaults to `count` = one column per card
(`:271-286`). Four 116-DIP density tiles at 12 DIP apart need 500 DIP of lane; the expander body's lane is
`card − 16 − 16` (the `SettingsExpanderPanel` padding), so the density strip goes 4 → 3 → 2 → 1 columns as the
card narrows below ~532 / ~404 / ~276 DIP. Cards never shrink — `Shrink = 0f` on both the card and the column
(`:68`, `:253`). The keyboard follows the same geometry: Up/Down step ±1 in DATA order (down a column),
Left/Right jump column to column (`:256-267`) — so at one column Up/Down is the only traversal that moves.

**The density miniature's real bar ladder** (`WaveePicker.DensityRows`, `:105-171`) — three identical rows in a
`Gap = 2` centred column, each row `Height = rowHeight`, `Gap = Spacing.XS 4`, `Pad (4,0,4,0)`:
an art square at `artworkEdge` with `Radii.Control 4` in `ink.Block`, then a `Grow = 1` **strong** 2-DIP pill
bar, then a fixed **24 × 2** faint pill and a fixed **16 × 2** faint pill. Only the two numbers derived from the
real row (`rowHeight`, `artworkEdge`) change between the four cards — the bar ladder is identical, which is
what makes the height difference legible. The card itself is `ClipToBounds` (`:63-82`): a miniature that
outgrows its tile is CUT, never painted over its neighbour — the exact failure that produced the overlapping
Sidebar header.

### W4 — Track-list-style and page-layout cards (both expanders open)

```
Track list style (2 cards, tile 116×84, Justify = Center inside the card)          Appearance.cs:464-478
   ┌──────────┐  ┌──────────┐      Modern card body = 3 × ModernRow(ink):
   │ ▪▬▬▬▬ ▬▬ │  │ ▬▬▬ ▬▬ ▬▬│        H 20, Pad (4,0,4,0), r 4, Fill ink.Faint, art tile 16, two bars (1.0 / 0.65)
   │ ▪▬▬▬▬ ▬▬ │  │ ────────  │      Classic card body = 3 × ClassicRow(ink):
   │ ▪▬▬▬▬ ▬▬ │  │ ▬▬▬ ▬▬ ▬▬│        H 20, three lanes (1.0 / 0.75 / 0.75) over a 1-DIP hairline
   └──────────┘  └──────────┘        (WaveePicker.cs:188-221)
      Modern        Classic

Track page layout (2 cards, tile 116×84)                                          Appearance.cs:520-586
   ┌──────────┐  ┌──────────┐      Automatic: LEFT column {art 20², bar 30×6, bar 22×4, pill 20×8} gap 4,
   │ ▪ ▁▁▁▁▁▁ │  │ ▆▆▆▆▆▆▆▆ │                 RIGHT column 4 × full-width 4-DIP row bars, gap 5; columns gap 8
   │ ▬ ▁▁▁▁▁▁ │  │ ▬▬ ▬ ⬭⬭  │      Hero: a 24-DIP full-width block, then {bar 48×6, bar 28×4, 2 × pill 24×8}
   │ ▬ ▁▁▁▁▁▁ │  │ ▁▁▁▁▁▁▁▁ │             gap 5, then 3 row bars
   │ ⬭ ▁▁▁▁▁▁ │  │ ▁▁▁▁▁▁▁▁ │      bar radius = height/2; art/pill radius 4; pill radius Radii.Control 4
   └──────────┘  └──────────┘
     Automatic       Hero
   Items (only while Automatic is selected — Hero composes no rail):               Appearance.cs:501-515
   ├ 📌 Keep left-rail same size      Ignore each page's own remembered width…        (  ●)
   └ 🗑 Clear all remembered sizes    Forget the rail width you set…              [ Clear ]  ← disabled unless
                                                                                     DetailRailPolicy.HasCustomizedRailPrefs
   (the Clear row is composed away entirely while "Keep left-rail same size" is ON)
```

### W5 — SettingsCard WRAPPED @ card 400 (pane 472)

```
┌─ 400 ────────────────────────────────────────────────┐
│ ⌨ 20  ←20→  App language                             │  header row: glyph + text column, Grow = 0
│             Changes are applied the next time Wavee  │  (fillMain = false when wrapping — SettingsCard.cs:132)
│             starts.                                  │  description wraps freely
│             ↕8  (VerticalHeaderContentSpacing)       │
│ ┌ System default                              ▾ ┐260 │  content Justify = Start (SettingsCard.cs:273)
│ └────────────────────────────────────────────────┘   │  MinWidth 120, Grow 0, Shrink 0
└──────────────────────────────────────────────────────┘
```

### W6 — SettingsCard wrapped with the glyph DROPPED @ card 260 (pane 332)

```
┌─ 260 ─────────────────────────────┐
│ App language                      │   hideIcon = width < 286 AND Alignment == Right
│ Changes are applied the next      │   (SettingsCard.cs:130, 201) — the 20-DIP glyph and its
│ time Wavee starts.                │   20-DIP right margin are simply not built
│ ↕8                                │
│ ┌ System default            ▾ ┐   │   the 260-DIP combo now exceeds the card's content box and
│ └──────────────────────────────┘  │   is clipped by the card's own bounds
└───────────────────────────────────┘
```

### W7 — Playback tab, runtime READY, equalizer expanded @ pane 1072

```
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ RuntimeCard, ready │
│ │ ✓ 20  Ready                                                                  [ Manage... ]     │ Icons.StatusSuccess│
│ │       Audio keys are derived locally on this PC.                                          ⌄    │ collapsed          │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ Playback.cs:116-134│
│                                                                                                                        │
│ ♪ Audio                                                                                                                │
│ ┌ 🎧 Audio quality        Streaming quality for local playback…      ┌ Very High           ▾ ┐280 ┐  itemDescriptions │
│ ┌ 📡 On metered connections  Not metered                             ┌ High                ▾ ┐280 ┐  ← sub IS the     │
│ ┌ 🔊 Remember volume      Restore the last volume level…                                  (  ●) ┐    detector verdict │
│ ┌ ▶  Autoplay             Continue with similar songs…                                    (  ●) ┐                     │
│                                                                                                                        │
│ 🔈 Sound                                                                                                               │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐                    │
│ │ ⌇ 20  Equalizer            10-band graphic EQ, +/-12 dB                              (  ●) ⌃  │ InitiallyExpanded  │
│ ├────────────────────────────────────────────────── 1 DIP ──────────────────────────────────────┤  = eqOn            │
│ │ ♫  Preset       No processing - flat, transparent playback        ┌ Flat              ▾ ┐200  │ item Pad (58,8,44,8)│
│ ├──────────────────────────────────────────────────────────────────────────────────────────────┤                    │
│ │ ⌇  Curve        Drag a node or use arrow keys on the active band                              │ alignment = Vertical│
│ │    ↕8                                                                                         │                    │
│ │   ┌───────────────────────────────────────────────────────────────────────────────────┐ 898×341                    │
│ │   │ +12 ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈ │ Fill FillCardDefault      │
│ │   │  +6 ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈ │ Border 1 StrokeCardDefault│
│ │   │0 dB ╌╌╌◉━━━◉━━━◉━━━◉━━━◉━━━◉━━━◉━━━◉━━━◉━━━◉╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌ │ r = 8                     │
│ │   │  -6 ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈ │                           │
│ │   │ -12 ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈ │                           │
│ │   │  31   62  125  250  500   1k   2k   4k   8k  16k                                  │ labels 10 DIP Cascadia     │
│ │   └───────────────────────────────────────────────────────────────────────────────────┘                            │
│ │    ↕8                                                        [ Reset curve to flat ]  ← Justify End, HyperlinkButton│
│ └──────────────────────────────────────────────────────────────────────────────────────────────┘                     │
```
`SettingsPage.Playback.cs:168-213`. The curve's width is self-measured (`UseMeasuredWidth(1f)`,
`WaveeEqualizerCurve.cs:47`); inside an expander item at card 1000 the available content lane is
1000 − 58 − 44 = **898 DIP**, so `height = clamp(898 × 0.38, 252, 360) = 341 DIP` (`:63`).

**The Ready runtime expander's four items** (`Playback.cs:125-132`): Version (`"<semver> (<packId>)"`, or bare
semver when there is no pack), Architecture, **Signature**, Location. `RuntimeInfoItem` passes
`isEnabled: !IsNullOrWhiteSpace(value)` (`:279-281`) — a fact the provisioner does not have renders as a
**greyed row with no description**, never as a blank one. The Signature row's description is
`SetupBody.SignatureSummary(status)` and it carries a "View signature" link ONLY when
`OperatingSystem.IsWindows() && File.Exists(dllPath)` (`:283-305`); the link opens the native Windows signature
dialog and raises a Warning toast if that call fails.

**The six EQ presets** (`Playback.cs:20-29, 42-60`), in combo order — the ComboBox carries `itemDescriptions`,
and the Preset ITEM's own description is the selected preset's sentence (`:187-190`):

| id | label | gains (31 Hz → 16 kHz) |
|---|---|---|
| `flat` | Flat | 0 0 0 0 0 0 0 0 0 0 |
| `bass` | Bass | +6 +5 +4 +2 0 0 0 0 0 0 |
| `treble` | Treble | 0 0 0 0 0 +1 +2 +3 +4 +5 |
| `vocal` | Vocal | −2 −1 0 +2 +4 +4 +2 0 −1 −2 |
| `radio` | Radio | 0 +2 −2 0 0 +2 +4 +2 +2 +2 |
| `proof` | Proof | +12 −12 +12 −12 +12 −12 +12 −12 +12 −12 |

The **Curve** item's description is two strings, not one: `Sound.CurveOn` while the EQ is on ("Drag a node or
use arrow keys…") and `Sound.CurveOff` while it is off (`:191-192`). Preset combo, curve and "Reset curve to
flat" are all `isEnabled: eqOn && settings is not null`.

**The two video ladders** (`Playback.cs:34-35, 372-406`): quality = Auto · 180p · 240p · 320p · 480p · 720p ·
1080p (stored as a height, `0` = Auto, combo 280 wide); metered = Unlimited · 480p · 720p · 1080p (default index
**1** = 480p when the stored height matches nothing, `:415-420`). The metered AUDIO combo is bound straight to
the live `NetworkPolicy.MeteredQualityCap` signal, not to a page-local mirror (`:361`).

**The audio-quality ladder is THREE rungs, not four, and that is a design decision with a comment.** Normal ·
High · Very High, each with its own `itemDescriptions` sentence (`:313-324`). `AudioQualityPreference.Lossless`
(= 3) still exists in the enum — the stored int is persisted — but is deliberately **not offered**: "a
permanently-disabled fourth row labelled 'Coming soon' is an advert, and the picker is the wrong place to make a
promise" (`:310-312`). Re-offering it later is one entry per array. The **metered** audio combo carries the
IDENTICAL three labels and three descriptions (`:349-360`), so the two rows read as one ladder seen twice — and
both write through `Math.Clamp`-free explicit `i < 0 || i > 2` guards (`:330, 366`).

**The runtime card's fourth state.** `RuntimeCard` reads `svc?.Playback.RuntimeStatus.Value ??
PlaybackRuntimeStatus.NotApplicable` (`:109`), so a Settings page mounted with no services at all
(`--fake`, pre-login) falls to the same `NeverAttempted` → Informational "not set up" bar as a fresh install —
never a blank slot, never an error. Three rendered shapes, four reachable inputs.

### W8 — EQ curve geometry, annotated @ 898 × 341

```
          PadLeft 40                                                         PadRight 16
        ├─────────┤                                                        ├──────────┤
   ┌────┬──────────────────────────────────────────────────────────────────────────────┬─────┐ ─┬─ PadTop 18
   │+12 │ ·   ·   ·   ·   ·   ·   ·   ·   ·   ·      dashed V grid: 1 DIP wide,        │     │  │
   │ 34 │                                            dash 2 / gap 5, StrokeDivider@0.58│     │  │ plotH = 341−18−34
   │×14 │ ·   ·   ·   ·   ·   ·   ·   ·   ·   ·                                        │     │  │      = 289
   │ +6 ├╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌ dashed H: 1 DIP, dash 2.5 / gap 5 │     │  │
   │    │              ┌──104 × 28──┐                                                  │     │  │
   │    │              │ 500  +4 dB │ ← value pill: r 14, FillControlDefault, border 1  │     │  │
   │    │              └─────▼──────┘   ControlElevationBorder, Shadow = Elevation.Tooltip     │  │
   │0 dB ├━━━◉━━━◉━━━◉━━━◉━━╱◉╲━◉━━━◉━━━◉━━━◉━━━◉━━ ZERO line: 1.2 DIP, dash 5 / gap 5,       │  │
   │    │ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒╱░░╲▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      TextSecondary @ A 0.34               │     │  │
   │ -6 ├╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌  fill strips: Accent @ A 0.15 (on)    │     │  │
   │    │                                            / TextDisabled @ A 0.10 (off)      │     │  │
   │-12 ├┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│     │  │
   ├────┴──────────────────────────────────────────────────────────────────────────────┴─────┤ ─┴─
   │      31    62   125   250   500    1k    2k    4k    8k   16k    ← labels 42×14 boxes,  │  PadBottom 34
   │                                                                    OffsetY = H − 24     │  (labels at H−24)
   └──────────────────────────────────────────────────────────────────────────────────────────┘
   plotW = max(120, 898 − 40 − 16) = 842      band i x = 40 + (i/9)×842       node y = 18 + GainToY(g, 289)
   GainToY(g,h) = (12 − clamp(g,−12,12)) / 24 × h      zeroY = 18 + 144.5 = 162.5
   curve: 96 samples, Catmull-Rom, clamped to ±12 (:375-388); each segment drawn TWICE —
          under: 5.5 DIP, Accent @ A 0.28 (off: TextDisabled @ 0.22);  main: 2.5 DIP, Accent (off: TextDisabled)
          PolylineStrokeEl, RoundCaps = true (:331-344)
   nodes: rest d = 14, hot d = 18; border 2.5 → 3; Fill FillControlSolid (off: FillControlDisabled);
          hot = ENABLED && (i == active || i == hover) — a DISABLED curve has no hot node at all (:240)
          BorderBrush = ControlElevationBorder, BorderColor = AccentDefault
                     (off: BorderBrush = GradientSpec.Solid(StrokeControlDefault), BorderColor = TextDisabled) (:251-252)
          Shadow = Elevation.Flyout when hot; BrushTransitionMs = WaveeMotion.Faster = 83 (:242-256)
   value pill placement (:291-293): px = clamp(x − 52, 6, max(6, W − 110)) · py = clamp(y − 42, 6, max(6, H − 58))
          — it rides 42 DIP above the ACTIVE node and is clamped inside the surface, so it never leaves the card
          and, near the left/right edge, stops following the node horizontally. Text: "0 dB" carries no sign
          (format "+0.#;-0.#;0", :405-406)
   dense labels: plotW < 420 → only even indices and index 9 are drawn (:261-263)
   disabled: whole surface Opacity 0.58, IsEnabled false, Focusable false, Cursor null, every handler null (:139-149)
   props not yet delivered (UsePropsOrDefault → null): the component renders ONE empty BoxEl with MinHeight 250
          and nothing else (:48) — the pre-first-props placeholder, never a spinner
```

### W9 — Crossfade expander open @ pane 1072

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ♪ 20  Crossfade        Natural transition at the end of a prepared track            (  ●) ⌃   │ InitiallyExpanded = crossOn
├──────────────────────────────────────────────── 1 DIP ────────────────────────────────────────┤
│ Crossfade duration     5 seconds       ━━━━━━━●━━━━━━━━ 220   ┌  5 s  ─ + ┐ 96                │ Key = "crossfade-duration-on"
│                        ↑ Strings.Sound.Seconds(_crossSecs)     Slider 0…12 step 0.5 tick 2     │ NumberBox Min 0 Max 12
│                                                                thumb tooltip "0.# s"           │ SmallChange 0.5, Compact spin
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```
`SettingsPage.Playback.cs:236-276`. Both controls commit through `Commit(seconds)` → `round(clamp(s,0,12)×1000)`
ms, which mirrors BOTH signals back and pushes the DSP (`:225-234`).

### W10 — Playback tab, runtime PROBLEM (error InfoBar) @ pane 1072

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ⊗  Problem                                                                                     │ InfoBarSeverity.Error
│    <status.Outcome.ToUserMessage(status.Detail)> — the typed outcome PLUS the provisioner's    │ isClosable = false
│    own detail line, never one static sentence.                                                 │
│                              [ View diagnostics ]  [ Connect diagnostics ]  [ Retry setup ]    │ 2 links + 1 Accent button,
└────────────────────────────────────────────────────────────────────────────────────────────────┘ Gap = Spacing.S 8
```
`SettingsPage.Playback.cs:146-165`. The third state — never provisioned — is
`InfoBarSeverity.Informational`, title `settings.playback.runtimeNotSetUp`, one `Button.Accent("Set up")`
(`:136-143`).

### W11 — Video overrides row + "Manage" flyout (root view) @ pane 1072

```
│ 🎬 Video                                                                                                             │
│ ┌ 📺 Video quality  Auto adapts to bandwidth…        ┌ Auto (recommended)   ▾ ┐280 ┐                                 │
│ ┌ 📡 Video Auto on metered connections               ┌ 480p                 ▾ ┐280 ┐                                 │
│ ┌ ✎  Attached videos   Applied on this computer, for every account. Files are linked, not copied…                    │
│ │                                            3 attachments   [ Manage ]   [ Remove all ]                            │
│ └──────────────────────────────────────────────────────────┬─────────────────────────────────────┐                  │
│                                                   anchored ▼ BottomEdgeAlignedLeft, FocusTrap,    │                  │
│                                     ┌──── 420 ────────────────────────────────┐ LightDismiss,     │                  │
│                                     │ ┌ Search by track, artist or file name ┐ │ PopupChrome.Popup │                  │
│                                     │ └──────────────────── 396 × 32 ────────┘ │ Pad 12 all        │                  │
│                                     │  Recently added          ← Caption 600   │                   │                  │
│                                     │ ┌────────────────────────────────────┐   │ ScrollEl,         │                  │
│                                     │ │🎞 Track title            [ OK ]    │44 │ MaxHeight 280,    │                  │
│                                     │ │   file-name.mp4                    │   │ gap 2             │                  │
│                                     │ │🎞 Another track       [ Missing ]  │   │                   │                  │
│                                     │ └────────────────────────────────────┘   │                   │                  │
│                                     │ ──────────────── 1 DIP ────────────────  │                   │                  │
│                                     │ 📁 Browse all…      3 attachments    ›   │40 MinH            │                  │
│                                     └──────────────────────────────────────────┘                   │                  │
```
`SettingsPage.VideoOverrides.cs:105-172` · `VideoOverrideManagerFlyout.cs:37-38, 68-135`. Status chips:
OK = `TextSecondary` on `FillSubtleSecondary`; Missing / Drive offline = `SystemFillCaution` on
`SystemFillCautionBackground`; Unplayable = `SystemFillCritical` on `SystemFillCriticalBackground`; pill
Pad (8,3,8,3), `Radii.Full`, text 12/600 (`VideoOverrides.cs:303-318`).

**The row's four states**, in the order the code tests them (`:116-166`):

| state | what the control slot holds |
|---|---|
| **no curation service** (fake backend) | *the whole row is `isEnabled: false` with **no control at all*** — greyed label + description, nothing on the right (`:116-122`). The anchor is dropped. |
| cold load (`phase != Ready && rows.Count == 0`) | an 18-DIP indeterminate ring, and the anchor is dropped so a deep link that lands mid-load waits (`:128-133`) |
| ready, 0 rows | `"No videos attached yet"` 12 DIP `TextSecondary` + **[ Manage ]** — and **no "Remove all"** (`:138-153`) |
| ready, N rows | `"N attachments"` + **[ Manage ]** + **[ Remove all ]** (the one confirm, `:156-160`) |

Per-row **Remove** raises a Success toast carrying an **Undo** action that re-attaches `(uri, path)` and toasts
the failure if the re-attach throws (`:235-250`) — the only undoable action in this surface. Replace / Locate
raise a Success toast, and a rejected pick (not `.mp4`, or gone) raises an Error toast plus an
`override.attach.rejected` Warning log line rather than an inline message (`:270-281`).

### W12 — "Browse all" leaf (full rows)

```
┌──── 420 ──────────────────────────────────────────────┐
│ ┌ ‹ ┐28  Attached videos                3 attachments │  header MinH 36
│ └───┘                                                 │
│ ┌───────────────────────────────────────────────────┐ │ ScrollEl MaxHeight 400, gap 4
│ │ Track title (600)                        [ OK ]   │ │ row Pad 8 all, r 4,
│ │ Artist  ·  C:\media\clips\file-name.mp4           │ │ Fill FillSubtleSecondary
│ │ Replace   Locate…   Show in Explorer   [ Remove ] │ │ (AccentSubtle when drilled-into)
│ └───────────────────────────────────────────────────┘ │ Caption wrap max 2 lines
│ …                                                     │ actions Wrap = true, gap 8
└───────────────────────────────────────────────────────┘
```
`VideoOverrideManagerFlyout.cs:138-161, 225-259` · actions `SettingsPage.VideoOverrides.cs:220-257`. The view
swap is keyed and animated with `MotionRecipes.PageSlideForward` / `…Back` (`:54-55`).

### W13 — Notifications tab, Windows notifications BLOCKED @ pane 1072

```
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ InfoBar Warning,   │
│ │ ⚠  Windows is blocking Wavee's notifications                                                   │ isClosable = false │
│ │    Turned off for Wavee in Windows Settings. The dials below will have no effect…              │ (absent for        │
│ │                                                              [ Open Windows settings ]         │  DisabledByGroupPolicy)│
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ 🔔 Delivery                                                                                                            │
│ ┌ 🔔 Windows notifications  Let Wavee raise banners in the Action Center…                  (  ●) ┐                     │
│ ┌ 🔔 Play a sound           Off, banners appear silently.                       [ disabled ]    ┐ ← isEnabled = policy │
│ ┌ 🌙 Quiet hours            No banners during these hours…                      [ disabled ]    ┐    .WindowsEnabled   │
│ ┌ 🕐 From / Until                                    ┌ 22:00 ▾ ┐120 ┌ 08:00 ▾ ┐120              ┐ ← only when Windows  │
│                                                                                                     ON and quiet ON    │
│ ⚙ What you're told about                                                                                               │
│   Each row travels as far as you let it: nothing, the bell in Wavee, or a Windows banner as well.  ← Hint, Body 14/20, │
│                                                                                                      TextSecondary,    │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ Pad (8,0,8,4),     │
│ │ ⓘ Windows notifications are off, so nothing here becomes a banner yet…                         │ max 3 lines        │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ InfoBar Informational│
│ ┌ 💿 New albums and singles   A release from an artist you follow.  [ Off │ In Wavee │ Windows ] ┐                     │
│ ┌ 🎙 New podcast episodes     Separate from albums on purpose…      [ Off │ In Wavee │ Windows ] ┐                     │
│ ┌ ♥  Pre-saved albums, on release day   …  ·  Arrives even when Wavee is closed                  ┐ ← scheduled topics  │
│ ┌ 📅 Concerts and live shows  …                                     [ Off │ In Wavee │ Windows ] ┐    append the badge │
│ ┌ 🔔 New followers            Someone started following you.        [ Off │ In Wavee │ Windows ] ┐                     │
│ ┌ ☀  Your daylist refreshed   …  ·  Arrives even when Wavee is closed                            ┐                     │
│ ┌ ⤓  Wavee updates            …                                     [ Off │ In Wavee │ Windows ] ┐                     │
│ ┌ 🕐 Your own library activity  …                                    [ Off │ In Wavee ]          ┐ ← TWO segments when │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘   the ceiling is InApp│
```
`SettingsPage.Notifications.cs:24-56, 60-99, 195-220, 242-263`. A topic that cannot reach Windows renders **two**
segments, never three-with-one-dead (`:65-68`). Topic order is `NotificationPrefs.AllTopics` declaration order
(`App/NotificationPrefs.cs:21-31`) — NewAlbums · NewEpisodes · ReleaseDrops · Concerts · Followers ·
DaylistRefresh · AppUpdates · LibraryActivity — and the two scheduled ones (ReleaseDrops, DaylistRefresh) append
`"  ·  " + Notify.ClosedBadge` to their description (`:166-168`).

**The blocked banner is THREE distinct texts, not one** (`BlockedBanner`, `:242-263`) — all
`InfoBarSeverity.Warning`, `isClosable: false`:

| `ToastDeliverySetting` | title / body | action |
|---|---|---|
| `DisabledForApplication` | `Notify.BlockedApp` / `…AppSub` | **[ Open Windows settings ]** → `ms-settings:notifications` |
| `DisabledForUser` | `Notify.BlockedUser` / `…UserSub` | same button |
| `DisabledByGroupPolicy` | `Notify.BlockedPolicy` / `…PolicySub` | **none** — a dead end is not offered |
| `Enabled` / `Unknown` | *no banner at all* — an unreadable notifier is not evidence of a problem | — |

The Informational bar under the "What you're told about" hint is built with an **empty title** (`title: ""`) and
only a message (`:50-51`), so it reads as one wrapped sentence, not a titled bar.

### W14 — A topic row with developer mode ON (expander + "Send event")

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ 💿 New albums and singles      A release from an artist you follow.   [ Off │ In Wavee │ Win ] ⌄│
├──────────────────────────────────────────── 1 DIP ────────────────────────────────────────────┤
│ Send a test event   Pushes a real event of this kind through the same path…   [ Send event ]  │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```
`SettingsPage.Notifications.cs:84-111`. The result is reported as a 6 000 ms toast whose severity comes from
the pipeline outcome (`:115-134`) — `Dropped` → Informational, `RecordedInApp`/`Banner`/`NeverBanners`/
`Scheduled` → Success, `BannerQuietDeferred` → Informational, unavailable → Warning.

**Two of those seven toasts carry a CLOCK, and the clock has a fallback.** `BannerQuietDeferred` reads "Added
to the bell. The banner is held until **HH:mm** by quiet hours." and `Scheduled` reads "Scheduled for **HH:mm**
— it will arrive even if you close Wavee now." (`:129-132`); the time is `r.At?.ToLocalTime().ToString("HH:mm",
CurrentCulture)` and renders the literal **`"--:--"`** when the pipeline returns no time (`:136-137`). Two other
shapes to keep: the "Send a test event" row's DESCRIPTION differs for a scheduled topic
(`SendScheduledSub` vs `SendSub`, `:105-107`), and the row is `isEnabled: svc is not null` — enabled at every
dial level **including Off**, because "nothing happens, as configured" is the one outcome a user cannot
otherwise verify (`:101-110`). `RunSimulation` also `Bump()`s, so a new bell row or a changed scheduled count
is visible on this page without a tab round trip (`:120`).

### W15 — Storage tab, census LOADING @ pane 1072

```
│ 🖥 On this PC                                                                                                          │
│      ↕16                                                                                                               │
│      ◠ 20   Reading storage sizes…       ← ProgressRing.Indeterminate(20) + TextEl 12 TextSecondary, gap 12,           │
│      ↕16                                   Pad = (0, 16, 0, 16)                                (Storage.cs:340-350)    │
│ ┌ 💿 Library database   library.db - albums, artists, playlists, sync state    Open folder      ┐ ← no size text yet   │
│ ┃3                                                                             (size is null)   │   (StorageActions    │
│ ┌ ⟨⟩ Playback runtime   playplay\runtimes - managed from the Playback tab      Open folder      ┐    omits it, :217)   │
```

### W16 — Storage tab, census READY @ pane 1072

```
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ usage card:        │
│ │ total  1.9 GB                            ← 12/600 TextSecondary + 18/700 TextPrimary, gap 8    │ Fill FillCardSecondary│
│ │ ↕12                                                                                            │ Border 1 StrokeCard │
│ │ ███████████████▌ ██▌ ▌ ██▌ ████████████████████████████████████████████████████▌ ▌ ████▌       │ r = Radii.Card 8    │
│ │ ↑ 10 DIP tall, 2 DIP gaps, r 2 per segment, r 3 + Fill StrokeDividerDefault on the track,      │ Pad (16,12,16,12)   │
│ │   ClipToBounds; each segment Grow = its byte count (so widths are exact proportions)           │ gap 12              │
│ │ ↕12                                                                                            │                    │
│ │ ▪ Library database     12.4 MB   1%    ▪ Playback runtime   38.1 MB   2%   ← legend: Wrap,     │ swatch 10×10 r 2    │
│ │ ▪ Logs                  4.2 MB   0%    ▪ Local store & …     1.1 MB   0%     each item Grow 1, │ label 12 TextPrimary│
│ │ ▪ Encrypted audio…    1.8 GB    94%    ▪ Saved license keys  0.3 MB   0%     Basis 0,          │ bytes 12 TextSecondary│
│ │ ▪ Image cache          52.0 MB   3%                                          MinWidth 200      │ pct 11 TextTertiary, │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ Width 36           │
│ ┃3 ┌ 💿 Library database   library.db - albums, artists…        12.4 MB   Open folder            ┐ 3-DIP accent bar,  │
│ ┃3 ┌ ⟨⟩ Playback runtime   playplay\runtimes…                   38.1 MB   Open folder            ┐ CornerRadius4      │
│ ┃3 ┌ 📄 Logs               logs\wavee.log - 7 files…             4.2 MB   Open folder  [Delete old logs] ┐ (2,0,0,2)  │
│ ┃3 ┌ 📁 Local store & history   store.json, navigation history   1.1 MB   Open folder            ┐ AlignSelf Stretch  │
│ ┃3 ┌ 🖼 Image cache        cache\images - album art…             52.0 MB  Open folder            ┐ Fill = the row hue │
│                                                                                                                        │
│ ⤓ Playback cache                                                                                                       │
│ ┌ 🔊 Cache encrypted audio   Skip re-downloading track bodies…                             (  ●) ┐                     │
│ ┌ 📄 Cache license keys      Reuse obfuscated keys across sessions…                        (  ●) ┐                     │
│ ┌ ⌸  Audio body budget       Fixed, drive-aware, or unlimited…          ← see W17               ┐                     │
│ ┌ 📂 Cache location   C:\Users\…\Wavee\Cache   [Choose location] [Use default] Open folder      ┐ ← the PATH is the sub│
│ ┃3 ┌ ♪ Encrypted audio bodies  Sparse, SHA-256-verified 64 KB CDN chunks  1.8 GB  Open folder [Clear audio cache] ┐    │
│ ┃3 ┌ 🏷 Saved license keys     Wavee\Cache\audiokeys.db - 1 284 keys      0.3 MB  Open folder [Clear saved keys]  ┐    │
│                                                                                                                        │
│ 📄 Metadata cache                                                                                                      │
│ ┌ ⌸  Metadata cache size   Your library, playlists and recent pages are always kept… ┌ 64 MB ▾ ┐120 ┐                 │
│ ┌ 📄 Metadata cache   47.2 MB database - 31.0 MB cache, 8.1 MB kept offline (12 044 rows, 2 100 pinned,               │
│ │                     318 artist pages, 44 extensions) · evictable 22.9 MB of 64.0 MB budget  [Clear metadata cache] ┐│
│                                                                                                                        │
│ ☰ In memory                                                                                                            │
│ ┌ 🖥 Resident library cache   Playlists kept warm… - 18.2 MB of 92.0 MB cap (14 of 24 playlists)                       │
│ │                             · entities t=8421 al=1102 ar=744 pl=61 sh=12 ep=310 (~74.1 MB)  [ Release now ]        ┐│
│                                                                                                                        │
│ 🗑 Reset                    Erase all local data and restart as a first launch                                          │
│ ┌ ⚠  Factory reset   Signs you out and deletes login, library, metadata, settings, caches…  [ Reset Wavee ]           ┐│
```
`SettingsPage.Storage.cs:296-424, 442-504`. `FmtBytes` (`:206-212`): ≥ 1 GiB → `"0.0 GB"`, ≥ 1 MiB → `"0.0 MB"`,
≥ 1 KiB → `"0 KB"`, else `"N B"` — invariant culture, binary divisors. The size text in a row card's action
lane is **13 DIP `TextSecondary`, `Shrink = 0`**, and it is simply *not built* while `size` is null (`:217`).

**Per-row description variants** the wireframe above shows only one of:
- **Logs**: `LogsSubEmpty` before the census lands → `LogsSubOne` at exactly one file → `"… - N files"`
  (`:360-365`).
- **Saved license keys**: `LicenseKeysCountOne` / `"… - N keys"` when `AudioLicenseCache.Stats()` answers,
  else the generic `LicenseKeysSub` (`:321-325`).
- **Metadata cache**: the generic `MetadataCacheSub` until `_metaStats` lands, then the stats sentence — whose
  last field is localized as **"N extras"**, not "extensions" (`settings.storage.metadataCacheStats`), plus the
  deliberately un-localized `" · evictable X of Y budget"` suffix (`:71-87`).
- **Resident library cache**: the generic `ResidentCacheSub` when there is no `CachedStore`, else the
  membership sentence + the un-localized `" · entities t=… al=… ar=… pl=… sh=… ep=… (~X)"` census
  (`:281-294`).

**Zero-total census.** `StorageUsageBar` skips any category at `bytes <= 0` (`:460`) — so it vanishes from both
the bar and the legend while its row card stays. When EVERY category is zero the bar element is
`new BoxEl()` (`:500`): **no track, no rounding, nothing** — the card shows only "total 0 B" over an empty
legend row. Do not render an empty grey track there; 0.2.9 does not.

### W17 — Audio body budget, the three modes

```
Fixed size                                  Drive share                        Unlimited
┌──────────────────────────────────────┐    ┌──────────────────────────────┐   ┌──────────────────────────────┐
│[ Fixed size │ Drive share │Unlimited]│    │[ Fixed │ Drive share │ Unl. ]│   │[ Fixed │ Drive │ Unlimited ] │
│ ↕6                                   │    │ ↕6                           │   │ ↕6                           │
│ ┌ 32 GB ▾ ┐120  ┌ 32 GB   ─ + ┐150   │    │ ┌ Auto (10%)   ─ + ┐150      │   │ No cache-size ceiling;       │
│  (preset)        (Min 0.0625,        │    │  (0…90, "Auto (10%)" at 0)   │   │ emergency free space is      │
│                   Formatter "0.### GB")│  │                              │   │ always preserved.  12 DIP    │
│ ↕6                                   │    │                              │   │                              │
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░ 300       │    │ same bar                     │   │ same bar (frac = 0)          │
│ 1.8 GB of 32.0 GB budget   11.5 DIP  │    │                              │   │ …of Unlimited budget         │
│ Keeps at least 8.0 GB free on the    │    │                              │   │                              │
│ cache drive       11.5 TextTertiary  │    │                              │   │                              │
└──────────────────────────────────────┘    └──────────────────────────────┘   └──────────────────────────────┘
   over budget → ProgressBarState.Error;  Status().Available == false → "The selected cache drive is
   unavailable; playback will use the network." in Tok.SystemFillCritical instead of the reserve line
```
`SettingsPage.Storage.cs:506-604`. Preset ladder: 1 · 2 · 4 · 8 · 16 · 32 · 64 · 128 · 256 · 512 GB · 1 TB ·
Custom (`:27-28`); typing a value in the NumberBox snaps the preset to `Custom` (`:599`).

### W18 — Storage census FAILED

```
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ⊗  Could not read storage sizes                                                                │ InfoBarSeverity.Error
│    <exception message, or "Something went wrong">                            [ Retry ]         │ isClosable = false
└────────────────────────────────────────────────────────────────────────────────────────────────┘ Storage.cs:334-339
```
Every row card below it still renders — with no size text at all, since `size` is null (`:217`).

### W19 — About tab, update AVAILABLE @ pane 1072

```
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ hero card:         │
│ │ ┌──────┐  Wavee 0.2.9 "Breaker"                                            [ Update now ]      │ Pad (24,22,24,22)  │
│ │ │  ♪   │  ← 24/600 TextPrimary, wrap                                    What's new in Breaker  │ r = Radii.Card 8   │
│ │ │ 64²  │  ┌0.2.9.10┐ ┌Stable┐ ┌Update available┐  a1b2c3d · arm64    Show the update summary… │ Fill FillCardSecondary│
│ │ │ r=16 │  ← pills, Gap 8, Wrap; the quad pill is MONO; the stamp is a plain 12 DIP             │ Border 1 StrokeCard│
│ │ └──────┘  Built 2026-08-29  ·  last checked 29/08/2026 14:02   ← 12 TextTertiary, wrap         │ Gap 18, Align Center│
│ │  AccentSubtle fill, Icon(MusicNote,30,AccentTextPrimary)                                       │ right col Gap 8,    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ AlignItems End     │
│ ↕4                                                                                                                     │
│ ┌ ⟳ Update status      Wavee 0.3.0 "Comet" is available.                                        ┐ StatusCard          │
│ ┌ 🖥 Release channel    Beta installs side by side with Wavee…              Get Wavee Beta      ┐ ChannelCard (a LINK)│
│ ┌ ⤓ Install a waiting update when I quit Wavee   Wavee downloads it as it closes…         (  ●) ┐ InstallOnQuitCard   │
│ ┌ 📡 Download on metered connections    Off: waits for an unmetered network.              (  ●) ┐                     │
│ ┌ ✨ Show "What's new" after an update  A short summary the first time…                   (  ●) ┐                     │
│ ↕4                                                                                                                     │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ links card:        │
│ │ Open What's new ●          ← InfoBadge.Dot() while the running release's notes are unread      │ Alignment = Left,  │
│ │ Report a problem                                                                               │ content column     │
│ │ Suggest a feature                                                                              │ Gap 4,             │
│ │ All issues on GitHub                                                                           │ Margin (−12,0,0,0) │
│ │ Website                                                                                        │ (pulls the links   │
│ │ Privacy policy                                                                                 │  flush to the card │
│ │ Third-party notices                                                                            │  padding)          │
│ │ Copy diagnostics info                                                                          │                    │
│ │ Open data folder                                                                               │                    │
│ │ Wavee is an independent client and is not affiliated with or endorsed by Spotify AB. ← 12 Tert.│                    │
│ │ Microsoft Windows NT 10.0.26340.0 (Arm64)                                          ← 12 Second.│                    │
│ │ C:\Users\…\AppData\Local\Wavee                                        ← 12 Second., Cascadia   │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ┌ ⚠ Crash reports    The last 10 reports in the log folder                                   ⌄  ┐ CrashReportsCard    │
│ ↕32                                                                                                                    │
│ ⓘ Wavee right now                                                                                                      │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ receipts card:     │
│ │ GPU                         Apple M2  (Strong)              ← label 12 TextSecondary, Shrink 0 │ Alignment = Left,  │
│ │ Working set                 412.7 MB                          value 13/600 TextPrimary, Grow 1 │ Gap = Spacing.XS 4 │
│ │ Managed heap                 78.3 MB                                                           │                    │
│ │ Uptime                        2h 14m                                                           │ 5 000 ms UseInterval│
│ │ FPS                            120.0                                                           │                    │
│ │ Zoom                           125%                                                            │                    │
│ │ GPU assets                  188.2 MB local  ·  0.0 MB non-local                                │                    │
│ │ App memory excl. GPU assets 224.5 MB  (discrete)                                               │                    │
│ │ Local budget 8192.0 MB · non-local budget 16384.0 MB · tracked D3D12 …  ← 12 TextTertiary wrap │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕32                                                                                                                    │
│ 📄 Licenses                                                                                                            │
│ ┌ Wavee              MIT                                                                    ⌄   ┐ expander, custom    │
│ ┌ Third-party notices  THIRD-PARTY-NOTICES.txt                                              ⌄   ┐ ItemCardStyle:      │
│                                                                    body = 12 DIP Cascadia Code,  Pad (16,12,16,16),   │
│                                                                    TextTertiary, wrap            MinH 0, r 0, no wrap │
```
`SettingsPage.About.cs:101-117, 119-167, 204-241, 277-301, 449-483, 490-536`.

**The hero's right column is 1–3 elements, and two of them are conditional** (`:504-508`): the primary button
always; `"What's new in <codename>"` only when `nav is not null && me.Codename is { Length: > 0 }`; `"Show the
update summary again"` only when `overlay is not null && !me.IsDev && svc is not null` (`:435-447`). A dev build
therefore shows ONE button and (usually) one link. The hero's middle column is `Gap 6`, not 18 — 18 is the gap
between the three hero columns (`:512, 525`).

**The provenance line under the pills has three shapes, not one** (`Provenance`, `About.cs:540-548`): it joins
up to two parts with `"  ·  "` — `Built <BuildDate>` only when the build carries a stamp, and then EITHER
`last checked <g-formatted local stamp>` when `LastCheckedMs > 0` OR the literal **"not checked yet"**
(`Update.About.NeverChecked`) when it is 0. So a `dotnet run` build that has never polled prints the single
phrase "not checked yet" with no separator — never an empty leading `" · "`. The same string is reused as the
**Store build's state pill** (`:572`), deliberately, so the two lines on that card cannot contradict each other.

**The receipts card is EIGHT labelled lines + ONE unlabelled wrapping detail line** (`:289-297`) — GPU · Working
set · Managed heap · Uptime · FPS · Zoom · GPU assets · App memory excl. GPU assets, then the 12-DIP
`TextTertiary` budget/atlas/glyph sentence. All nine signals start at `"—"`; `snap.Valid == false` prints
`"— (no Present yet)"` for GPU assets, `"—"` for app-excl, and one explanatory sentence (`:322-327`).

**Crash reports card** (`Features/Feedback/CrashReportsCard.cs`): a `SettingsExpander`, `HeaderIcon =
Icons.StatusWarning`, collapsed. Empty → ONE item whose label is `""` and whose content is a 12-DIP
`TextTertiary` "no crash reports" line (`:36-40`). Otherwise up to ten items, each labelled with the file's
timestamp formatted `"g"` and carrying `HStack(8, [ Report ] [ Open ])` — Report opens `ReportRequests` in crash
mode prefilled with that file, Open reveals it in Explorer (`:42-58`). Every item is `Key`ed by path.

### W20 — About hero: the state matrix (one card, four shapes)

| Build / state | pill (`StatePill` `:569-584`) | primary button (`PrimaryButton` `:598-619`) | status card content (`:635-654`) |
|---|---|---|---|
| dev build, not simulating | `Development build — not for distribution` | "Check for updates", **disabled** | — |
| Store install | `not checked yet` | "Open Store page" → `ApplyAsync` | *(StoreCard replaces all four update cards)* |
| `None` | `Up to date` | "Check for updates" | — |
| `Checking` | `Checking` | "Checking…", disabled | — |
| `Available` | `Update available` **accent** | "Update now", **Accent** | — |
| `Snoozed` | `Waiting` | "Update now", Accent | — |
| `Downloading` | `Downloading` **accent** | "Installing…", disabled | ProgressBar 180 DIP + `NN%` 12 DIP Cascadia |
| `Installing` | `Restarting` **accent** | "Installing…", disabled | ProgressBar + percent |
| `Completed` | `Just updated` | "Check for updates" | `Dismiss` (Subtle, Small) |
| `Failed` | `Update failed` | "Retry", **Accent** | `Open release page` (Standard, Small) |

### W21 — Logs tab @ pane 1072 (the one unscrolled tab)

```
┌ pane ──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ⚙ Settings                                                                                                          │
│ General  Appearance  Playback  Notifications  Storage & cache  [Logs]  About                                         │
│ ─────────────────────────────────────────────────────────────────────────────────────────────────────── 1 DIP      │
│ ↕16                    ← Pad = (36, 16, 36, 16); NO ScrollView, NO 1000-DIP cap: the panel owns the pane            │
│ ┌ This session                    ▾ ┐ 320                     ⟳  ⎘  ⤓  📁  ✕ │ ⇅ ▤ ⟨⟩            …                 │
│ │ pid 31544 · running 2 h 05 m      │                        ← CommandBar, MinHeight 48, Key = "logs:bar:"+BarKey() │
│ └───────────────────────────────────┘                          primary: Refresh · Copy visible · Export session ·   │
│  ↑ ComboBox, itemDescriptions, Key = "logs:session:"+_sessionsRev    Open log folder · Clear view(live only) · SEP ·│
│ ↕12                                                                  [T]Newest first · [T]Group repeats · [T]Verbose│
│ ┌ 🔍 Filter logs ────────────────────────── grow ┐34  [ All │ Info+ │ Warnings │ Errors ]  (⚠3) (⛔1)              │
│                                                    ↑ Segmented, Height 34, item MinW 52, pad 2, font 14            │
│                                                          ↑ InfoBadge.Count, clickable → sets the level filter      │
│                    …………………………………………… Grow spacer ……………  ┌ All categories ▾ ┐180  ┌Capture level┐132 ┌File level┐132 │
│ ↕12                                                                                                                 │
│ ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐│
│ │ ›    18:24:27.445  [ INFO  ]  connect      dealer connected                                                    ││ card: Fill FillCardSecondary,
│ │ ›    18:24:28.102  [ DEBUG ]  playback     prepared next uid=…                                          ×12    ││ Border 1 StrokeCardDefault,
│ │ ⌄  ⚠ 18:24:29.000  [ WARN  ]  lyrics       no synced lyrics for track                                          ││ r = Radii.Card 8,
│ │      Fields      ┌ uri=spotify:track:…   provider=musixmatch   elapsed=41ms          ⎘ ┐  ← CodeBlock 12 DIP   ││ ClipToBounds, Grow 1
│ │      Exception   ┌ System.Net.Http.HttpRequestException: …                            ⎘ ┐                       ││
│ │      #812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms      ← 11 DIP Cascadia TextTertiary, Margin-left 44         ││ row MinHeight 36,
│ │ ›  ⛔ 18:24:31.774 [ ERROR ]  audio        playback failed                                                      ││ Pad (12,0,12,0),
│ │                                (virtualized — MeasuredStackVirtualLayout, estimated 36)                        ││ Gap 8
│ ├──────────────────────────────────────────── 1 DIP Divider ─────────────────────────────────────────────────────┤│
│ │ Showing 500 of 3 214 events · live ring          Load more                     Capturing Trace+ · file Info+   ││ footer Pad (16,8,12,8)
│ └─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
  row columns (fixed):  chevron 12 · severity dot 6 · time 92 (mono 12) · level pill 58×22 · category 96 (mono 12,
                        ellipsis) · message (Grow, 13 DIP, NoWrap+ellipsis unless expanded or Wrap is on) · ×N badge
  severity dot (LogsPanel.cs:533-538): a dot is drawn ONLY for Warning (InfoBadge.Dot Caution) and ≥ Error
                        (Dot Critical). Trace / Debug / Info get a BLANK 6×6 spacer — the column is reserved so
                        the timestamps stay aligned, but the vast majority of rows show nothing there.
  ×N badge: built only while row.Repeat > 1; otherwise an empty BoxEl holds the slot (:508)
  level pill colours (LogsPanel.cs:547-563): Critical/Error → SystemFillCritical · Warning → SystemFillCaution ·
                        Debug/Trace → TextTertiary · else AccentDefault;  Fill = colour @ A 0.12, Border 1 @ A 0.38,
                        text 10 DIP weight 800, UPPERCASE, r = Radii.Full
  repeat badge: Pad (7,1,7,2), Radii.Full, FillSubtleSecondary, "×N" 10.5 DIP weight 700 TextSecondary
  expanded row: Fill FillSubtleSecondary; detail block Pad (44, 0, 12, 4), section caption 11/600 TextTertiary
```
`Features/Shell/LogsPanel.cs:131-151, 156-171, 236-271, 409-469, 478-563, 567-591`.

Three things the sketch flattens:
- **The filter row WRAPS.** It is `Direction = 0, Gap = 12, Wrap = true, MinHeight = 40, Shrink = 0`
  (`:242-244`), so on a narrow window the search box, the level `Segmented`, the badges, the category combo and
  the two headed level combos reflow onto two or three lines — the panel's only reflow state, and the reason
  the Logs tab has no 1000-DIP cap. The header row above it is `MinHeight 48, Shrink 0` (`:161`).
- **The command bar's overflow is three entries, not two**: "Wrap long lines" (a toggle, **no icon**), a
  **separator**, then "Report this session…" (`Icons.Attention`) which opens `ReportRequests` prefilled with the
  selected past session, or null for the live ring (`:214-226, 383-388`).
- **The live tail poll is disabled on a past session** — `UseInterval(…, 750f, enabled: _session.Value == 0)`
  (`:85-91`). Selecting a past run stops the poll entirely; only "Refresh" re-reads.
- **Four separate actions COLLAPSE the expanded row.** `_expandedSeq` is reset to `-1` by the session
  `UseSignalEffect` (`:75-81`), by the session combo's own `onChange` (`:165`), by **Newest first** (`:203`) and
  by **Group repeats** (`:206`) — because all four change which row a given sequence number even *is*. Changing
  the session additionally resets `_visibleLimit` back to `LogView.PageRows` 500 (`:79`), so "Load more" starts
  over. Search text and the level/category filters do NOT collapse it: the row survives if it is still in the
  set, and simply disappears with its own row if it is not.
- **The expanded row is a keyed WRAPPER, not a taller row.** `Key = "logs:row:" + Sequence`, `Direction = 1`,
  `Gap = 4`, `Padding = (0, 0, 8, 8)` around the 36-DIP line plus up to three detail blocks (`:514-520`):
  Fields (only when `LogView.FieldText` is non-empty), Exception (only when the entry carries one), and the
  meta line — which ALWAYS renders. A row with neither fields nor an exception still expands, to exactly one
  11-DIP mono meta line indented 44 DIP.

Also: `Segmented` defaults supply the level strip's geometry — Height 34, item MinWidth 52, Padding 2, font 14,
3-DIP indicator (`fluent-gpu Segmented.cs:27-34, 207-227, 267-268`); the search box is an `AutoSuggestBox` with
an EMPTY suggestion list, `grow: 1`, `minHeight: 34`, `cornerRadius: Radii.Control` (`:247-250`) — it is a
filter field, never a suggesting one.

### W22 — Logs: loading a past session / empty filter

```
loading (entries == null)                              empty (result.Shown == 0)
┌───────────────────────────────────────┐              ┌───────────────────────────────────────┐
│                                       │              │                 ↕64                   │
│               ◠ 32                    │              │               🔍 36  TextTertiary     │
│   Reading session from the log file…  │              │          No matching logs             │  ← Ui.Title 28/36/600
│               12 DIP TextSecondary    │              │                 ↕64                   │
└───────────────────────────────────────┘              └───────────────────────────────────────┘
   ProgressRing.Indeterminate() default 32              LogsPanel.cs:424-436
   LogsPanel.cs:411-422
```

### W23 — Playback runtime diagnostics page @ pane 1072

```
│ ♪ 22  Local playback diagnostics                         ← PageHeader Pad (16,16,16,12), Icon 22 + Ui.Title          │
│ ↕16                                                                                                                   │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ Status block:      │
│ │ ✓ 18   Local playback support is compiled into this build                                      │ Pad 12 all,        │
│ │        The app can search for and load a local Spotify.dll. Anything below is about THAT search.│ Fill FillLayerAlt, │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ r = Radii.Control 4,│
│ ↕12                                                                    glyph 18 DIP in Theme.IconFont, tinted:       │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ Success / Critical/│
│ │ Current status                             ← 12/600 TextSecondary card title                   │ Attention          │
│ │ Outcome              Ready                 ← Row: label 12 TextSecondary Width 132 Shrink 0,   │ heading 14/600,    │
│ │ Detail               —                       value 12 TextPrimary wrap Grow 1, gap 8           │ body 13 TextSecondary│
│ │ Pack                 spotify-1.2.63.394                                                        │                    │
│ │ Spotify version      1.2.63.394                                                                │ Card: Dir 1, Gap 6,│
│ │ Architecture         Arm64                                                                     │ Pad 12, FillLayerAlt│
│ │ Runtime path         C:\Users\…\Wavee\playplay\runtimes\…                                      │ r 4, Border 1      │
│ │ Signature trust      Trusted                                                                   │ StrokeCardDefault  │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ┌ Locate     Outcome / Reason                                                                    ┐                    │
│ ┌ Candidates  «source»  [✓ Spotify.dll] [✕ playplay-runtime.json]    ← Chip: Pad (6,1,6,1), r 4, │                    │
│ │             C:\…\path                     Fill SystemFillSuccessBackground / SystemFillCritical-│                    │
│ │             ──── 1 DIP between candidates ──── Background; text 11 DIP "✓ "/"✕ " + label        │                    │
│ ┌ Verify      outcome/detail/trust + publisher/issuer/thumbprint/reason/valid/file                ┐                    │
│ ┌ Playback modules  «DisplayName» [bundled] [Ready] · Id/Version/Publisher/Directory/Process/      ┐                    │
│ │                   Capabilities/Requests/Latency/Last error/Status · [Retry] Faulted/Crashed     │                    │
│ │                   "Refused" sub-list: dir + reason pairs                                        │                    │
│ ┌ Updates     Feed URL / Channel / Package version / State / Auto-update association / Last       ┐                    │
│ │             checked / Last failure + 6 release-notes receipts + "Repair auto-update" + hint      │                    │
│ ┌ [ Copy diagnostics ]  [ Open log folder ]  [ Refresh ]     ← HStack(8), first is Accent         ┐                    │
│   This report is what the provisioner already computed…      ← Caption 11 TextTertiary wrap       │                    │
```
`Features/Diagnostics/PlaybackRuntimeDiagnosticsPage.cs:60-74, 80-313, 395-453`. The `Status` block also carries
a **1-DIP `StrokeCardDefault` border** and `Gap 12` between glyph and text column (`:435-453`).

**Every section states its own absence — five prose states the wireframe does not show:**

| card | absent state | source |
|---|---|---|
| headline `Status` | **three** states, not two: no provisioner *or* no diagnostics → `Icons.StatusInfo` on `SystemFillAttention`, "No playback session yet" ("Local playback is provisioned by the live Spotify session. Sign in…"); `CompiledIn` → Success; `!CompiledIn` → `StatusError` on `SystemFillCritical` with the locate reason as the body | `:80-93` |
| Candidates | "No candidate locations were probed." | `:112-113` |
| a candidate with a blank dir | the path line reads `"(no path)"` | `:130` |
| Verify | "Verification was never reached — the search ended before a candidate could be checked." (when all four verify fields are null) | `:136-139` |
| Playback modules | no host → "This build has no playback-module host (the fake backend)."; host with zero installed → "No playback modules are installed. Bundled modules live in …; installed ones in …" | `:163-174` |

**What is NOT omitted when the provisioner is missing.** Only Locate / Candidates / Verify sit behind
`if (diag is { } d)` (`:48-53`). `Current status` still renders whenever `svc.Playback.RuntimeStatus` has a
value (`:47`), and **Playback modules, Updates, the three action buttons and the closing caption always
render** (`:54-58`). The page is never just a banner.

The presence `Chip`'s TEXT is `Tok.TextPrimary` in both states — only the BACKGROUND is severity-coloured
(`:422-427`); the module `Chip` reuses the same control for "bundled"/"installed" and for the process state,
treating `Ready` / `Stopped` / `Starting` (and a null process) as "present" and everything else as critical
(`:188-193`). The per-module **Retry** button appears for `Faulted` **or `Crashed`** — two states, not one
(`:215-218`) — and the row set below each module's chip line is conditional too: `Requests` / `Latency` only
when the process has `Stats` (`:203-212`), `Last error` only when that error string is non-empty (`:210-211`),
`Status` only when the process publishes a status card (`:214`). A module that has never started renders its
first six rows (`Id` · `Version` + protocol · `Publisher` · `Directory` · `Process` = "not running" ·
`Capabilities`) and nothing else — never a blank block.

Two more prose-absence facts the table above compresses: the **`Refused` sub-list** is composed only when
`host.Catalog.Rejections.Count > 0` (`:222-232`) — an 8-DIP-margin divider, a 12/600 `TextSecondary` "Refused"
caption, then a (dir, reason) pair per rejection, each a wrapping 12-DIP line (`TextPrimary` then
`TextSecondary`); and **every `Row` on both diagnostics pages prints `"—"` for a blank/whitespace value**
(`:417-418`, `ConnectDiagnosticsPage.cs:188-189`), so an unknown fact is a dash, never an empty right column.

### W24 — Connect diagnostics page @ pane 1072

```
│ 🖥 22  Connect diagnostics                                                                                            │
│ ┌ Owner              State / Claim phase / Claim id / Started at / Fence server ts / Last server ts /                 │
│ │                    Last seen (unconfirmed)          ← Row label Width 160 (vs 132 on the runtime page)              │
│ ┌ Last cluster       Active id (raw) / Origin / Update reason / Changed devices / Server ts / Track uri /             │
│ │                    Playing / paused / Position                                                                       │
│ ┌ Last put-state     Msg id / Reason / is_active / started_playing_at / has_been_playing_for / Sent at                │
│ ┌ Last put-state response   Msg id / Active id / Server ts / Cluster started_at / Adopted / Received at               │
│ ┌ Host               Playing / Clock valid / Position     (or "No local audio stack — a pure Connect viewer…")        │
│   This report is a direct read of the owner state…       ← Caption 11 TextTertiary                                    │
│   timestamps: "yyyy-MM-dd HH:mm:ss.fff" local, invariant; 0 renders as "—"        (ConnectDiagnosticsPage.cs:150-151) │
```
**Only TWO of the five cards have a prose-absence state.** `Owner` falls back to "No live Connect session yet."
and `Last cluster` to "No cluster has folded yet." (`ConnectDiagnosticsPage.cs:75-76`); `Host` to "No local
audio stack — a pure Connect viewer, or nothing has loaded yet." (`:141-142`). **`Last put-state` and `Last
put-state response` have none** — `PutCard(ConnectDiagnostics.LastPut)` / `EchoCard(…LastEcho)` are called
unconditionally (`:77-78`), so on a machine that has never sent a put-state they render their full 6 rows with
every value a `"—"`, an `is_active` reading `False` and two `0 ms` counters. That is deliberate (the shape of
the report is the same every time); do not "improve" it into a third absence sentence, and do not mistake the
zeroes for a bug. `_tick` is bumped by three subscriptions — ownership `Changed`, the projection's `Changes`
observable and the static `ConnectDiagnostics.Changed` — the first two re-keyed on
`DepKey.FromRef(connect)` so they re-attach on every login/logout (`:48-71`).

**Getting here in 0.2.9 takes a broken install.** This page's route is not registered
(`ShellRoutes.cs:26-44`), so the only way in is the Settings › Playback **error** InfoBar's link
(`Playback.cs:162`), which does not exist while the runtime is Ready — and its tab is labelled "Your Library"
with `Icons.MusicNote` because `ShellNav.Dest` has no arm for it either (`ShellNav.cs:50-79`). Wireframe the
page as drawn; wire the route as §9.6 says, not as 0.2.9 has it.

### W25 — API console page (developer mode only) @ pane 1072 — ~~STRUCK~~, DELETED 2026-09-12 (plan §9.6 Q7)

> **Do not port this surface.** Decision, Christos, 2026-09-12 (plan §9.6 Q7): the four `ApiDebug*` helpers behind
> this page (`ApiDebugBodyBuilder` / `ApiDebugExecutor` / `ApiDebugProto` / `ApiDebugProtoDecomposer`, ~1,400
> developer-only lines) are DELETED, and with them this console — `ApiConsolePage` (329 lines) has no function
> without them. The wireframe below is kept, struck through in spirit rather than removed, as the record of what
> 0.2.9 painted here; 0.3 has no `ApiConsole` route, no `Diagnostics.UI.cs` arm for it, and no
> `+Diagnostics.Api.cs` file.

```
│ ⟨⟩ 22  API Console                                                                                                    │
│ Base URL: https://gew4-spclient.spotify.com          ← Caption 11 TextTertiary                                        │
│ ┌ POST ▾ ┐96  ┌ /extended-metadata/v0/extended-metadata ───────── 920 × 36 ┐  [ Send ] Accent                        │
│ Channel                          Response decode                                                                      │
│ ┌ spclient ▾ ┐160                ┌ Auto ▾ ┐320                                                                        │
│ Headers  (Key: Value per line)   ← Label 12/600 TextSecondary                                                         │
│ ┌ …────────────────────────────────────────────────────────────────── 920 × 80, multiline ┐                          │
│ Body                                                                                                                  │
│ [ None │ Text │ Entity lines ]   ← SelectorBar                                                                        │
│ [ Fill extended-metadata headers ]                                                                                    │
│ One entity per line: uri | EXTENSION_KIND | optional_etag …   ← Caption 11                                            │
│ (  ●) Bulk hydration (infer TrackV4/AlbumV4/… from URI only)                                                          │
│ ┌ …────────────────────────────────────────────────────────────────── 920 × 160, multiline ┐                         │
│ [ Preview body ] [ Insert example ]                                                                                   │
│ Request preview     bulk hydration → 4 812 bytes gzip                                                                 │
│ [ Preview request ] [ Copy response ] [ Save JSON ] [ Save raw ] [ Copy hex ]                                         │
│ Status              OK 200 · 412 ms · 182 041 bytes                                                                   │
│ [ Body │ Headers ]                                                                                                    │
│ ┌ response pane ───────────────────────────────────────────────────────────────────────────┐ MinH 240, MaxH 420,     │
│ │ {                                                                                        │ Fill FillControlSecondary,│
│ │   "…": …                                                                                 │ Border 1 StrokeControlDefault,│
│ └──────────────────────────────────────────────────────────────────────────────────────────┘ r 6, inner Pad 10, scrolls│
│ gzip/zstd auto-decompressed · on-screen body truncated · Save JSON unpacks Artist/Track/Album protos ← Caption        │
```
`Features/Diagnostics/ApiConsolePage.cs:192-288`. The sketch shows the **Entity lines** body mode; there are
three, and the `[ None │ Text │ Entity lines ]` `SelectorBar` is rebuilt inside each arm (`:257-287`):

| mode | body section |
|---|---|
| None | Label + SelectorBar + Caption "No request body." — nothing else |
| Text | Label + SelectorBar + Caption "JSON or plain text — enable gzip…" + a **920 × 140** multiline editor + a "Gzip request body" toggle row |
| Entity lines | Label + SelectorBar + **[ Fill extended-metadata headers ]** + Caption + "Bulk hydration" toggle + a **920 × 160** editor + `HStack(8)` [ Preview body ] [ Insert example ] |

Every editor is an `EditableText` at `FieldW = 920` (`:313-318`); the URL field is 920 × 36, Headers 920 × 80.
Field labels are 12/600 `TextSecondary`, captions 11 `TextTertiary` (`:301-305`).

Three in-flight / default states the sketch does not show: the **Send button flips its own label and disables
itself** while a request is out — `Button.Accent(_busy.Value != 0 ? "Sending…" : "Send", Send, isEnabled:
_busy.Value == 0)` — so there is no spinner anywhere on this page, the button IS the progress indicator; the
page opens on **POST** (`_method` default 1) with the body mode on **Entity lines** (`_bodyMode` default 2) and
the URL pre-filled with `/extended-metadata/v0/extended-metadata` (`:22-34`); and the **Base URL caption** reads
the literal **`(not connected)`** when `svc.RealSpclientBaseUrl` is empty (`:52`). Switching *into* Entity lines
auto-fills the Headers box when it is blank (`:57-59`) — a side effect of the mode switch, not of Send.
The `ToggleRow` switches ("Gzip request body", "Bulk hydration") use the **default** `ToggleSwitch` style, not
`SettingsCard.CompactToggleStyle()` — this page is not built out of `SettingsCard`s at all.

### W26 — FPS overlay (developer mode + overlay toggle)

```
                                                                        ┌──────────────────┐
  pinned top-right of the CONTENT, Pad = (0, 104, 14, 0)  ───────────►  │ 120  fps · 8.3 ms│  Pad (8,4,8,4), r 6
  on a Grow=1 HitTestPassThrough positioner (WaveeApp.cs:429-437)       └──────────────────┘  Fill FillSolidBase @ A 0.90
  both numeric slots are authored as the literal "--" and stay "--" until the host writes the first reading
  (FpsOverlay.cs:38, 41) — that, not a zero and not a spinner, is the overlay's first painted frame
                                                                                              Border 1 StrokeCardDefault
  "120" = DynamicTextKind.FrameFps (12/700, AccentTextPrimary) · "8.3" = DynamicTextKind.FrameMs (12/600, TextSecondary)
  "fps" 12/600 TextSecondary · "·" 12/600 TextTertiary · "ms" 12/600 TextTertiary · Gap 4      FpsOverlay.cs:30-44
```

### W27 — The one confirm dialog, and the two cache-relocation dialogs

```
SettingsShared.Confirm (SettingsShared.cs:29-41)         OfferCacheRelocation (Storage.cs:626-640)
┌───────────────────────────────────────┐                ┌──────────────────────────────────────────────┐
│ Clear all remembered sizes?           │                │ Change audio cache location?                 │
│                                       │                │                                              │
│ This resets the left rail's remembered│                │ Wavee can move verified encrypted chunks, or │
│ width and collapsed state for albums… │                │ start with an empty cache in the new location.│
│                                       │                │                                              │
│              [ Clear ]  [ Cancel ]    │                │ [ Move existing ] [ Start empty ] [ Cancel ] │
│                          ▲ DEFAULT    │                │  ▲ DEFAULT                                   │
└───────────────────────────────────────┘                └──────────────────────────────────────────────┘
  DefaultButton = ContentDialog.DefaultBtn.Close          DefaultButton = Primary; "Start empty" opens a
  on EVERY destructive confirm in this surface            SECOND dialog: "What should happen to the old
                                                          cache?" → [Delete old cache][Leave old cache][Cancel]
```

### W28 — SCROLLED @ pane 1072: the chrome is PINNED, and nothing compacts

```
┌ pane ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ⚙ 24   Settings                              ← STILL full size: Header() is a SIBLING of the ScrollView, never        │
│ ↕16                                            inside it (SettingsPage.cs:172-189). It does not shrink, fade, gain    │
│ General  Appearance  [Playback]  Notif…        a shadow, or turn into a compact bar at ANY scroll offset.             │
│ ──────────────────────────────────────────────────────────────────────────────────────────────────────── 1 DIP      │
│ ╔══════════════════════════════════════ the ONLY scrolling region ═════════════════════════════════════════════╗     │
│ ║ …ideo quality        Auto adapts to bandwidth…        ┌ Auto (recommended)  ▾ ┐280   ← clipped mid-card       ║ ▲   │
│ ║ ┌ 📡 Video Auto on metered connections                ┌ 480p                ▾ ┐280 ┐                          ║ █   │
│ ║ ┌ ✎  Attached videos …                                                                                        ║ █   │
│ ║                                                                                                               ║ ▼   │
│ ║ 📌 Player bar                              ← a section header scrolls like anything else: NOT sticky          ║     │
│ ║ ┌ 🕐 Show remaining time   Count down time left…                                           (  ●) ┐            ║     │
│ ║ ↕36  bottom page padding                                                                                      ║     │
│ ╚═══════════════════════════════════════════════════════════════════════════════════════════════════════════════╝     │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**There is no sticky header, no compact/condensed masthead rung, and no scroll-linked effect of any kind in this
surface.** The page is three stacked children — `Header()`, the tab-strip lane, and `content`
(`SettingsPage.cs:174-188`) — of which only the third scrolls, and only on six of the seven tabs (N8). The
`ScrollView` carries `Grow = 1f`, `ScrollKey = "settings:" + slug` and `Key = "settings:scroll:" + slug`
(`:165-170`), so the offset is per-tab and survives a tab round trip; it does **not** survive leaving the
Settings route (the page is keyed `"page:settings"` in `ContentHost.cs:222-224` and its KeepAlive behaviour is
18-shell-frame.md's, not this chapter's). The diagnostics pages are the same shape: a pinned `PageHeader()`
plus one `ScrollView` keyed on the route (`PlaybackRuntimeDiagnosticsPage.cs:60-73`,
`ConnectDiagnosticsPage.cs:84-97`, ~~`ApiConsolePage.cs:192-240`~~ — the last DELETED in 0.3, plan §9.6 Q7, 2026-09-12).

### W29 — Hover / pressed / focused, the seven interactive shapes

```
① SettingsRow (NOT clickable — every row but one)          ② SettingsCard, CLICKABLE — an expander ITEM, not a
┌──────────────────────────────────────────┐                  top-level card: Appearance › Sidebar › Design ›
│ ⌨  App language           ┌ System ▾ ┐   │ hover: NOTHING   "Customize sidebar", visible only while Wavee
└──────────────────────────────────────────┘ changes on the   Curated is active. Pad (58,8,16,8), MinH 52, r 0
                                                           ┌──────────────────────────────────────────────┐
                                                           │ ✎  Customize sidebar                      ›  │ 13 DIP chevron
                                                           └──────────────────────────────────────────────┘
  HoverFill == Fill and PressedFill == Fill when             rest  FillCardDefault   / StrokeCardDefault
  `clickable` is false (SettingsCard.cs:156-162), so a       hover FillControlSecondary / ControlElevationBorder
  pointer crossing the page produces NO shimmer at all.      press FillControlTertiary  / StrokeControlDefault
  The CONTROL inside it still has its own states.            focus ring inset −3 (`:165`) · brush x-fade 83 ms

③ SettingsExpander header                                   ④ Picker card (WaveePicker.Card)
┌────────────────────────────────────────────┐              ┌ 116 ─────┐  rest   off: FillCardDefault + 1 StrokeControlDefault
│ ⌸  Row density        Default        ⌄ 32 │              │▪▬▬ ▬ ▬   │         on : AccentSubtle    + 2 AccentDefault
└────────────────────────────────────────────┘              │▪▬▬ ▬ ▬   │  hover  off: FillCardSecondary · on: WaveeColors.SelectedHover
  The HEADER BACKGROUND DOES NOT CHANGE ON HOVER            │▪▬▬ ▬ ▬   │         + Scale 1.02 (WaveeMotion.ScaleSubtle.Hover)
  (`Expander.cs:228-235` — "stays CardBackgroundFill…       └──────────┘  press  off: FillSubtleSecondary · on: AccentSubtle
  at rest and hover"). ONLY the 32 × 32 chevron button                           + Scale 0.98 (…ScaleSubtle.Press)
  lights: HoverFill `FillSubtleSecondary`, PressedFill              focus: RadioButtons item ring, inset +2 (WaveePicker.cs:238-245)
  `FillSubtleTertiary` (`Expander.cs:203-211`); the whole           cursor: CursorId.Hand on the card (`:80`)
  header is still the click target.

⑤ SelectorBar item (tabs, theme, dials)      ⑥ Log row (.Interactive(Interaction.ListRow))   ⑦ EQ node
  background: TRANSPARENT in EVERY state       rest    FillSubtleTransparent                   rest  d = 14, border 2.5
  text  rest    Tok.TextPrimary                hover   FillSubtleSecondary                     hot   d = 18, border 3
        hover   Tok.TextSecondary              press   FillSubtleTertiary                            + Elevation.Flyout
        press   Tok.TextTertiary               (an EXPANDED row already paints                 hot = i == active OR i == hover
  (selected-pressed → TextSecondary)            FillSubtleSecondary, so hovering it            fill/border cross-fade 83 ms
  focus ring inset −2 (SelectorBar.cs:183)      reads identical to its rest state)             (WaveeEqualizerCurve.cs:233-256)
  Interaction.cs:139-142 · LogsPanel.cs:488, 510
```

The rule the whole surface follows: **a settings ROW is inert under the pointer; only its control reacts.** The
one clickable card, the expander chevron, the picker cards, the log rows and the EQ nodes are the complete list
of things that change under hover. Adding a hover fill to ordinary rows would make scrolling the page shimmer —
the exact reason `SettingsCard` ties `HoverFill` to `clickable` (`SettingsCard.cs:156-157`).

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| page root | — | — | — | — | — | inherits the shell Mica | `SettingsPage.cs:172-189` |
| page header | h = 24 + 16 + 12 | Pad (36,16,36,12), Gap 12 | — | `Ui.Title` 28/36/600 | `Tok.TextPrimary`; glyph 24 `TextPrimary` | — | `:239-248` |
| tab strip lane | — | Pad (36,0,36,0) | — | — | — | — | `:180` |
| SelectorBar item | content + 20 | Pad (12,10,12,7), icon gap 8 | `Radii.Control` 4 | 14 / normal | rest `TextPrimary`, hover `TextSecondary`, pressed `TextTertiary` (selected-pressed `TextSecondary`) | none | `fluent-gpu SelectorBar.cs:123-186` |
| SelectorBar pill | 16 × 3 | — | 1 | — | `Tok.AccentDefault` | — | `SelectorBar.cs:29-31, 159-168` |
| divider under the strip | h 1 | — | — | — | `Tok.StrokeDividerDefault` | — | `Factories.cs:177-179` |
| scroll lane | — | Pad (36,16,36,36) | — | — | — | — | `SettingsPage.cs:168` |
| content column | MaxW 1000 | — | — | — | — | — | `:23, 195` |
| card stack | — | Gap 4 | — | — | — | — | `:24, 203` |
| section header | — | Margin (0,32,0,8), Gap 8 | — | `BodyStrong` 14/20/600 + `Caption` 12/16 | title `TextPrimary`, sub `TextSecondary`; glyph 16 `TextSecondary` (Margin top 2) | — | `:211-237` |
| SettingsCard | MinW 148, MinH 68 | Pad 16 all; glyph margin-right 20; header margin-right 24; text gap 1 | `Radii.Control` 4 | header 14, description 12 | `FillCardDefault` / hover `FillControlSecondary` / press `FillControlTertiary` / disabled `FillControlDisabled`; border `StrokeCardDefault` / hover `ControlElevationBorder` / press `StrokeControlDefault` | border 1; brush x-fade 83 ms | `SettingsCard.cs:24-32, 58-70, 96-111, 144-169` |
| card content slot | MinW 120 | — | — | — | — | Grow 0, Shrink 0 | `:26, 269-280` |
| card action chevron | 13 | Margin-left 14 | — | glyph `0xE974` | `Foreground` | — | `:28, 34, 295-319` |
| SettingsExpander header **card** (the inner `SettingsCard`) | MinH 68 | Pad (16,16,4,16) | 4 (transparent fill) | as card | transparent, border 0 | — | `SettingsExpander.cs:22-24, 30-39` |
| SettingsExpander header **shell** (the engine `Expander`'s own header) | MinH forced to 68 | Pad 0 (the part override) | `Radii.Control` 4 — **top corners only while the body is mounted**, all four when closed | — | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | background does NOT change on hover | `Expander.cs:228-242`, `SettingsExpander.cs:151-160` |
| expander **body panel** (the engine `Expander`'s content) | MinH 16 (part override; engine default 48) | Pad 0 (part override; engine default 16) | bottom corners 4 | — | **`Tok.FillCardSecondary`**, border 1 `Tok.StrokeCardDefault`, `Margin-top −1` so the header's bottom border IS the divider | — | `Expander.cs:256-270`, `SettingsExpander.cs:183-190` |
| expander chevron | 32 × 32 | Margin-right 8 (the Settings part override drops the engine's 20-DIP left margin) | `Radii.Control` 4 | glyph `Icons.ChevronDown` 12 DIP `TextPrimary` | hover `FillSubtleSecondary`, press `FillSubtleTertiary` | rotation **0 ↔ 180** | `SettingsExpander.cs:161-170`, `Expander.cs:203-221` |
| expander item | MinH 52 | Pad (58,8,44,8); clickable (58,8,16,8) | **0** | as card | as card | wrap thresholds **0** | `:23, 25-26, 41-57` |
| item separator | h 1 | — | — | — | `Tok.StrokeDividerDefault` | — | `:138` |
| `SettingsExpanderPanel` | — | Pad (16,12,16,12) | — | — | deliberately **no** fill/border/corners | — | `SettingsPage.cs:285-290` |
| `SettingsValueTag` | — | — | — | 14, 1 line, char-ellipsis | `Tok.TextSecondary` | — | `:275-278` |
| picker tile | 116 × 84 | inset 8 (7 when selected), gap 4 | `Radii.Card` 8 | — | on: `AccentSubtle` + 2-DIP `AccentDefault`; off: `FillCardDefault` + 1-DIP `StrokeControlDefault`; hover on `WaveeColors.SelectedHover`, off `FillCardSecondary`; press on `AccentSubtle`, off `FillSubtleSecondary` | HoverScale 1.02 / PressScale 0.98 | `WaveePicker.cs:44, 63-82` |
| picker label | — | gap 8 above | — | 12 / lineHeight 16 | on 600 `TextPrimary`; off 400 `TextSecondary` | — | `:86-94, 225-231` |
| picker strip | — | grid gap 12, wrap | — | — | — | focus visual inset 2 | `:238-254, 271-286` |
| ToggleSwitch (compact) | track 40 × 20, MinH 36 | — | `Radii.Circle(20)` | — | engine default states | — | `SettingsCard.cs:116-120`, `ToggleSwitch.cs:59-70` |
| CheckBox (hide artwork) | box 20, hit 32 × 32 | — | — | — | engine default | — | `Appearance.cs:434`, `CheckBox.cs:40` |
| ComboBox | MinH 32; widths 120 / 132 / 160 / 180 / 200 / 260 / 280 / 300 / 320 | — | 4 | 14 | engine default | dropdown MaxH 504 | `ComboBox.cs:73-82`, call sites listed in §1 |
| Slider (crossfade) | length 220 | — | — | — | thumb tooltip `"0.# s"` | — | `Playback.cs:244-250` |
| Slider (lyrics blur) | length 180 | row gap 12 to the "Auto" link | — | — | Min 0 · Max 100 · Step 1 · **TickFrequency 25**; thumb tooltip `"N%"`; the link exists only while the stored value is not `-1` | — | `Appearance.cs:141-153` |
| section header title/caption column | — | Gap `Spacing.XXS` **2** | — | — | — | `AlignItems` = **Start** with a caption, **Center** without (so the 16-DIP glyph sits beside the TITLE, not between the two lines) | `SettingsPage.cs:213-236` |
| picker card (extra) | — | — | — | — | — | `ClipToBounds` — a miniature that outgrows its tile is cut, never painted over its neighbour | `WaveePicker.cs:62-72` |
| picker label (extra) | — | — | — | 1 line, character-ellipsis | — | — | `WaveePicker.cs:92-93` |
| EQ surface (extra) | — | — | — | — | — | `ZStack` + `ClipToBounds` — every child is absolutely offset, and the clip is what makes the pill-clamp rule sufficient | `WaveeEqualizerCurve.cs:133-135` |
| density miniature bar ladder | art = `artworkEdge`² r 4 · strong bar Grow 1 h 2 · faint 24 × 2 · faint 16 × 2 | row gap 4, Pad (4,0,4,0), column gap 2 | pill (`Radii.PillAll`) / art `Radii.Control` 4 | — | `ink.Block` / `ink.Faint` | 3 rows, column `Justify = Center` | `WaveePicker.cs:105-171` |
| NumberBox | W 96 (crossfade) / 150 (budget) | Compact spin | — | — | — | — | `Playback.cs:255`, `Storage.cs:528, 593` |
| ProgressBar (budget) | W 300, h 3 | — | 1.5 | — | `Normal` / `Error` | — | `Storage.cs:553`, `ProgressBar.cs:36-39` |
| ProgressBar (update) | W 180 | — | — | — | — | — | `About.cs:642` |
| ProgressRing | 28 (`SettingsShared.Loading`) · 20 (storage census) · 18 (video roster) · 32 = `ProgressRing.DefaultSize` (log session) | — | — | — | — | — | `SettingsShared.cs:50`, `Storage.cs:347`, `VideoOverrides.cs:132`, `LogsPanel.cs:418` |
| storage row size text | — | inside the action lane, Gap 12 | — | 13 | `Tok.TextSecondary`, `Shrink = 0`; **absent** while `size` is null | — | `Storage.cs:217, 220` |
| level `Segmented` (Logs) | H 34, item MinW 52 | Pad 2 | — | 14 | engine default; 3-DIP indicator | — | `fluent-gpu Segmented.cs:27-34, 207-227` |
| log search `AutoSuggestBox` | MinH 34, Grow 1 | — | `Radii.Control` 4 | — | empty suggestion list — a filter field, not a suggesting one | — | `LogsPanel.cs:247-250` |
| storage usage card | — | Pad (16,12,16,12), Gap 12 | `Radii.Card` 8 | total 12/600 + 18/700 | `FillCardSecondary`, border 1 `StrokeCardDefault` | — | `Storage.cs:476-503` |
| usage bar | h 10, seg gap 2 | — | track 3, seg 2 | — | track `StrokeDividerDefault`; segs the 7 hues | ClipToBounds | `:461, 493-499` |
| legend item | MinW 200 | Gap 8 | swatch r 2 | label 12, bytes 12, pct 11 | `TextPrimary` / `TextSecondary` / `TextTertiary` (pct Width 36) | — | `:463-473` |
| storage row accent bar | W 3 | — | `(2,0,0,2)` | — | the row's hue | AlignSelf Stretch | `:232-236` |
| EQ surface | W = measured, H = clamp(W×0.38, 252, 360) | Pad L40 R16 T18 B34 | 8 | — | `FillCardDefault`, border 1 `StrokeCardDefault` | Opacity 0.58 when disabled | `WaveeEqualizerCurve.cs:30-37, 63, 129-151` |
| EQ node | 14 rest / 18 hot | — | circle | — | `FillControlSolid` (off `FillControlDisabled`), border 2.5/3 `AccentDefault` | `Elevation.Flyout` when hot; brush 83 ms | `:233-257` |
| EQ value pill | 104 × 28 | Pad (8,0,8,0), Gap 5 | 14 | freq 11 mono, value 12/700 | `FillControlDefault`, border 1 `ControlElevationBorder` | `Elevation.Tooltip` | `:285-329` |
| EQ axis text | — | — | — | 10 DIP Cascadia Code | 0 dB `TextSecondary` 650; others `TextTertiary` 400; freq `TextTertiary` | — | `:162-179, 259-283` |
| About hero | — | Pad (24,22,24,22), Gap 18 | `Radii.Card` 8 | 24/600 title | `FillCardSecondary`, border 1 `StrokeCardDefault` | — | `About.cs:510-535` |
| About hero tile | 64 × 64 | — | 16 | glyph 30 | `AccentSubtle` + `AccentTextPrimary` | — | `:517-522` |
| About receipts | — | Gap 4 | — | label 12, value 13/600 | `TextSecondary` / `TextPrimary`; detail 12 `TextTertiary` | — | `:281-301, 370-378` |
| license body | — | Pad (16,12,16,16) | 0 | 12 DIP Cascadia Code | `TextTertiary` | — | `:66-76, 219, 235` |
| log card | — | — | `Radii.Card` 8 | — | `FillCardSecondary`, border 1 `StrokeCardDefault` | ClipToBounds | `LogsPanel.cs:140-143` |
| log row | MinH 36 | Pad (12,0,12,0), Gap 8 | — | time/category 12 mono, message 13 | expanded fill `FillSubtleSecondary` | `Interaction.ListRow` | `:484-510` |
| log level pill | 58 × 22 | — | `Radii.Full` | 10 / 800 uppercase | colour @ A 0.12 fill, @ A 0.38 border | — | `:547-563` |
| log repeat badge | — | Pad (7,1,7,2) | `Radii.Full` | 10.5 / 700 | `FillSubtleSecondary`, `TextSecondary` | — | `:540-545` |
| log footer | — | Pad (16,8,12,8), Gap 12 | — | 12 + 11 | `TextSecondary` + `TextTertiary` | — | `:585-590` |
| diagnostics card | — | Pad 12, Gap 6 | `Radii.Control` 4 | title 12/600 | `FillLayerAlt`, border 1 `StrokeCardDefault` | — | `PlaybackRuntimeDiagnosticsPage.cs:395-409` |
| diagnostics row | — | Gap 8 | — | 12 / 12 | label `TextSecondary` Width 132 (Connect: 160), value `TextPrimary` wrap | — | `:411-420`, `ConnectDiagnosticsPage.cs:182-191` |
| diagnostics chip | — | Pad (6,1,6,1) | `Radii.Control` 4 | 11 | `SystemFillSuccessBackground` / `SystemFillCriticalBackground` | — | `:422-427` |
| video-override flyout | W 420 | Pad 12 | popup chrome | — | — | anchored popup | `VideoOverrideManagerFlyout.cs:37-38, 59-64` |
| video-override status chip | — | Pad (8,3,8,3) | `Radii.Full` | 12 / 600 | see §0 N-list | — | `VideoOverrides.cs:312-317` |
| FPS pill | — | Pad (8,4,8,4), Gap 4 | 6 | 12 / 600–700 | `FillSolidBase` @ A 0.90, border 1 `StrokeCardDefault` | hit-test pass-through | `FpsOverlay.cs:30-44` |

---

## 4. Colour & material

This surface is deliberately the **least** tinted in the app: it carries **no cover palette, no wash, no scrim,
no on-media ink**. The only art-derived colour anywhere near it is whatever the shell paints behind the page
(18-shell-frame.md). Four things do vary:

**4.1 The storage hue set — the one hand-picked palette in Wavee.**
Input: nothing (they are constants). `SettingsPage.Storage.cs:36-42` →
`ColorF.FromRgba(0x4A,0x90,0xD9)` Library · `(0x9B,0x59,0xB6)` Runtime · `(0xF5,0xA6,0x23)` Logs ·
`(0x27,0xAE,0x60)` Local store · `(0x1A,0xBC,0x9C)` Audio bodies · `(0x95,0xA5,0xA6)` License keys ·
`(0xE7,0x4C,0x3C)` Image cache. Applied at full opacity in **two** places for each category: the bar segment
(`:461`) and the 3-DIP row accent bar (`:232-236`), plus a 10×10 legend swatch (`:468`). They are theme-INVARIANT
— the same seven RGB triples in light and dark, which is the whole reason they exist (`:35`). No transition: the
bar is rebuilt when the census lands.

**4.2 Picker ink — the only accent derivation.**
Input: `bool on` → `WaveePicker.Ink.For` (`Design/WaveePicker.cs:32-34`) →
selected `(AccentDefault, AccentDefault @ A 0.45)`, unselected `(AccentDefault @ 0.58, AccentDefault @ 0.22)`.
Applied to every bar, tile and pill inside a preview card, so the whole wireframe tints on selection rather than
just the border. The card itself swaps `Tok.FillCardDefault` → `Tok.AccentSubtle` and its border
`Tok.StrokeControlDefault` 1 DIP → `Tok.AccentDefault` 2 DIP, spending one DIP of inset so nothing moves
(`:63-82`). Transition: fill/border cross-fade on the engine's default brush transition; scale on hover/press
(§5).

**4.3 Equalizer alpha ladder.**
Input: `IsEnabled`. `WaveeEqualizerCurve.cs:191` fill = `(enabled ? AccentDefault : TextDisabled) with { A =
enabled ? 0.15f : 0.10f }`; `:220-221` underglow = same base `with { A = enabled ? 0.28f : 0.22f }`, main stroke
= the base colour at full alpha. The zero line is `Tok.TextSecondary with { A = 0.34f }` (`:179`); the vertical
grid is `Tok.StrokeDividerDefault with { A = 0.58f }` (`:185`). Disabled additionally drops the whole surface to
`Opacity = 0.58f` (`:139`) — so a disabled curve is 0.58 × 0.10 ≈ 6 % alpha fill, visible but plainly inert.

**4.4 Severity colour, used in four places.**
`Tok.SystemFillCritical` / `SystemFillCaution` / `SystemFillSuccess` / `SystemFillAttention` and their
`*Background` twins drive: the log level pill (`LogsPanel.cs:549-560`, fill @ A 0.12 / border @ A 0.38), the
video-override status chip (`VideoOverrides.cs:307-310`, fg + `*Background` fill), the diagnostics presence chip
(`PlaybackRuntimeDiagnosticsPage.cs:425`), and the `Status` block glyph (`:84, 89, 91`). One vocabulary, four
renderings — keep them distinguishable (pill = outlined, chip = filled, glyph = bare).

**4.5 Light / dark.** Nothing in this surface branches on `Tok.Theme`. Everything theme-varying comes through
`Tok.*` and `Elevation.*` (`Elevation.Card` 4/2/#0000001A light vs 8/2/#00000033 dark;
`Elevation.Flyout` 16/8/#00000024 light vs #00000042 dark; `Elevation.Tooltip` 8/4/#00000024 vs 16/4/#00000040 —
`fluent-gpu/src/FluentGpu.Engine/Dsl/Elevation.cs:18-35`). The seven storage hues (4.1) are the ONE deliberate
exception and must stay one.

**4.6 The Mica/page background.** The settings page paints **no** background of its own — its cards
(`FillCardDefault`, `FillCardSecondary`, `FillLayerAlt`) sit directly on the shell's material. That is why the
card stroke matters: on Mica it is the only thing separating a 68-DIP card from the desktop behind it.

---

## 5. Motion

Every animation here is **engine-owned** — a `LayoutTransition`, a `MotionTok` token, a `BrushTransitionMs`, or
a `HoverScale`/`PressScale` pair. No code in this surface samples a clock; the two periodic refreshes
(`UseInterval` 5 000 ms and 750 ms) are data refreshes, not animation, and the FPS overlay's numbers are written
by the host into retained slots. **No `Environment.TickCount64` anywhere in this surface** — confirmed by
reading all 7 500 lines.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| tab click | body | — | swap | **0** (no transition) | — | — | n/a | `SettingsPage.cs:143-170` |
| tab click | SelectorBar pill | Opacity + ScaleX | 0 / ¼ → 1 / 1 | 167 ms | `Easing.FluentPopOpen` = cubic-bezier(0,0,0,1) | — | **fade kept** (`LayoutTransition` defaults to `KeepFade`; the scale snaps) | `SelectorBar.cs:57-62`, `AnimScheduler.Structural.cs:195` |
| tab deselect | pill | Opacity | 1 → 0 | 167 ms | same | — | fade kept | `SelectorBar.cs:57-62` |
| expander header click | content clip | Height | 0 → auto | **333 ms** | `FluentPopOpen` | — | **snaps** (Size channel) | `Expander.cs:112-121` |
| expander header click | content clip | Height | auto → 0 | **167 ms** | cubic-bezier(1,1,0,1) | — | snaps | `Expander.cs:121` |
| expander header click | chevron | Rotation | **0 ↔ 180°** (a `ChevronDown` flipped, NOT a 90° `ChevronRight`) — a mid-flight toggle retargets from the LIVE angle, never snapping | 167 ms | cubic-bezier(0.167,0.167,0,1) | — | snaps (`ReducedMotionPolicy.SnapEnd`) | `Expander.cs:12-14, 117, 155-177` |
| log row click | disclosure chevron | Rotation | 0 ↔ 90° | 167 ms | `Easing.FluentDisclosureChevron` = cubic-bezier(0.167,0.167,0,1) | — | snaps | `MotionTok.cs:182`, `SidebarChevron.cs:46-73` |
| hover / press a CLICKABLE card | fill + border | brush | rest → hover → pressed | 83 ms | engine brush x-fade | — | UNVERIFIED (brush transitions are not gated by `Motion.ReducedMotion`) | `SettingsCard.cs:70, 156-162` |
| hover / press a NON-clickable card | — | — | **nothing** | — | — | — | — | `SettingsCard.cs:156-157` (`clickable` false ⇒ hover fill == rest fill) |
| hover / press an expander HEADER | header background | — | **nothing** — only the 32 × 32 chevron button lights (`FillSubtleSecondary` → `FillSubtleTertiary`) | engine brush x-fade | — | — | UNVERIFIED | `Expander.cs:203-211, 228-235` |
| hover / press a SelectorBar item | item text | brush | `TextPrimary` → `TextSecondary` → `TextTertiary`; background transparent in every state | engine brush x-fade | — | — | UNVERIFIED | `SelectorBar.cs:123-186` |
| scroll any tab | masthead, tab strip, divider | — | **nothing** — they sit outside the `ScrollView` and never compact, fade or gain a shadow | — | — | — | n/a | `SettingsPage.cs:172-189` |
| hover a picker card | whole card | Scale | 1 → 1.02 | engine `HoverScale` | — | — | UNVERIFIED | `WaveePicker.cs:78`, `Design/WaveeMotion.cs:39` |
| press a picker card | whole card | Scale | 1 → 0.98 | engine `PressScale` | — | — | UNVERIFIED | `WaveePicker.cs:79` |
| select a picker card | card fill/border, all inner bars | brush | neutral ink → accent ink | engine default | — | — | UNVERIFIED | `WaveePicker.cs:63-82` |
| hover / activate an EQ band | node diameter | Width/Height | 14 → 18 | **instant** (a layout change, not a tween) | — | — | n/a | `WaveeEqualizerCurve.cs:240-241` |
| hover / activate an EQ band | node fill + border | brush | rest → hot | 83 ms (`WaveeMotion.Faster`) | engine brush x-fade | — | UNVERIFIED | `:254` |
| drag an EQ node | curve, fill, pill | geometry | rebuilt per commit | **per-frame re-render**, no tween | — | — | n/a | `:74-84` |
| theme row click | whole window | theme cross-fade | old → new | **250 ms** (requested) | engine theme transition | — | engine | `SettingsPage.Appearance.cs:208-213` |
| zoom row pick | whole window | scale | old → new | **instant** (`FluentApp.SetZoom`) | — | — | n/a | `:227-239` |
| storage census lands | census block | — | spinner → usage bar (element swap) | 0 | — | — | n/a | `SettingsPage.Storage.cs:334-351` |
| storage census lands | budget ProgressBar | indicator width | old → `frac` | engine `ProgressBar` determinate | — | — | engine | `:553` |
| video-override flyout drill | view | `MotionRecipes.PageSlideForward` / `…Back` | — | app page-slide recipe (18-shell-frame.md) | — | — | recipe-owned | `VideoOverrideManagerFlyout.cs:54-55` |
| About receipts | 9 signal-backed `TextEl`s | text | previous → new | **5 000 ms tick**, no tween | — | — | n/a | `SettingsPage.About.cs:252, 279-280, 303-359` |
| log tail | rows | list content | append | **750 ms poll**, bumps only on a version change; list REMOUNTS on a visible-set change | — | — | n/a | `LogsPanel.cs:86-91, 441-449` |
| log expand | row | fill | transparent → `FillSubtleSecondary` | `Interaction.ListRow` brush ramp | — | — | UNVERIFIED | `LogsPanel.cs:488, 510` |
| log expand | list | scroll | `StartBringItemIntoView(idx, 0f)` | engine bring-into-view | — | — | engine | `:471-476` |
| notification simulation | toast | — | — | **6 000 ms** visible | — | — | n/a | `SettingsPage.Notifications.cs:119` |
| FPS overlay | two `TextEl`s | `DynamicText` slots | refreshed **in place** by the host | ~10 Hz at idle, display-rate while animating | — | — | n/a | `FpsOverlay.cs:16-21, 38, 41` |
| update progress | ProgressBar | value | `s.ProgressPercent / 100` | engine determinate | — | — | engine | `SettingsPage.About.cs:642` |

`Motion.ReducedMotion` is host-set from `SPI_GETCLIENTAREAANIMATION`
(`fluent-gpu/src/FluentGpu.Engine/Dsl/Motion.cs:23-24`) and enforced **at the animation seed**, never by an
author-side branch (`AnimScheduler.Structural.cs:108-145`): a `LayoutTransition` carries no policy and defaults
to `KeepFade` (opacity tweens survive, size/offset snap); a `MotionTok` token defaults to `SnapEnd`. **No file in
this surface reads `Motion.ReducedMotion`** — and none should.

---

## 6. Interaction

### 6.1 Pointer

- **Cards.** A `SettingsCard` is only hoverable/pressable when `IsClickEnabled && OnClick != null`
  (`SettingsCard.cs:145`). Exactly one row in the whole surface is clickable: **Appearance › Sidebar › Design ›
  "Customize sidebar"**, which navigates to `SidebarLayoutMenu.CustomizeRoute`
  (`SettingsPage.Appearance.cs:632-635`). Three qualifiers: it is an expander **item** (`ClickableItemPadding`
  58,8,16,8 · `CornerRadius 0` · MinH 52), not a free-standing 68-DIP card; it exists only while
  `SidebarDesignGating.CanCustomize(design)` — i.e. Wavee Curated is the active design (`:629`); and it is the
  only place the 13-DIP action chevron (`0xE974`) appears in this surface. An expander's own header card forces
  `IsActionIconVisible = false` (`SettingsExpander.cs:127`), so a header never shows one.
- **Expander headers.** The whole header is the toggle (`Expander.PartHeader`, MinHeight 48 forced to the card's
  68 — `SettingsExpander.cs:151-160`), including the header's content slot region, so clicking near a toggle
  switch inside a header must still hit the switch first; that is why the switch is in the content slot and the
  chevron is a separate 32-DIP column.
- **Picker cards.** The card carries `Cursor = CursorId.Hand` and NO `Role`/`Focusable`/`OnClick` — the
  `RadioButtons` item root owns all three, so the card is never announced twice
  (`WaveePicker.cs:60-62, 80`).
- **EQ curve.** `OnPointerDown` → `Commit(NearestBand(x), y)`; `OnDrag` → keeps the band captured in `_dragBand`
  (so a fast vertical drag cannot jump bands); `OnHoverMove` → sets `_hover`; `OnPointerExit` → clears both
  (`WaveeEqualizerCurve.cs:79-96, 146-149`). `NearestBand` rounds `(x − 40)/plotW × 9` (`:390-394`).
- **Log rows.** Single click invokes (`SelectionMode = None`, `IsItemInvokedEnabled = true`,
  `Selector = SelectorVisual.None` — `LogsPanel.cs:456-464`); clicking the already-expanded row collapses it
  (`:473`).
- **Clickable badges.** The warning/error `InfoBadge.Count` in the log filter row is promoted to a button by
  `ClickableBadge` (`Role = Button`, `Focusable`, `Cursor = Hand`) and sets the level filter (`:253-256, 281-282`).
- **"Manage" is a TOGGLE, not an opener.** `ToggleVideoOverrideManager` closes the flyout when the same button
  is pressed while it is open (`SettingsPage.VideoOverrides.cs:178-198`) — the contract every other anchored
  surface in the app uses. The anchor lives on a `BoxEl` WRAPPER around the button, not on the button itself
  (`Button` owns its own root props), and its `OnRealized` is what satisfies a pending deep-link open
  (`:142-151, 210-216`). Leaving the Playback tab closes the flyout and nulls the anchor (`SettingsPage.cs:136`,
  `VideoOverrides.cs:66-71`); leaving Settings entirely also disarms any pending deep link (`:58-62`).
- **Right-click.** There is **no context menu anywhere in this surface.** Not on rows, not on log lines, not on
  diagnostics cards. Do not add one.
- **Double-click.** Nothing binds it.

### 6.2 Keyboard

- **SelectorBar (tabs, theme, lyrics second line, notification dials, budget mode).** ONE tab stop, on the
  selected item. Left / Right / Home / End move focus and **selection follows focus**; entering a bar with no
  selection auto-selects (`SelectorBar.cs:74-111, 186-190`).
- **Picker strip.** ONE tab stop landing on the current value; Up/Down step ±1 in data order, Left/Right jump
  column to column; selection follows focus; Ctrl+arrow moves without applying; Space selects
  (`WaveePicker.cs:19-24, 256-267`).
- **EQ curve.** Focusable when enabled, `Role = AutomationRole.Slider`, `FocusVisualMargin = −3`
  (`WaveeEqualizerCurve.cs:140-144`). Keys (`:98-119`): **Left/Right** move the active band (no gain change and
  `e.Handled = true`); **Up/Down** ±0.5 dB; **PageUp/PageDown** ±3 dB; **Home** → 0 dB; **End** → −12 dB when the
  current gain ≥ 0, else +12 dB. Every gain change clamps to ±12.
- **Expanders.** Engine `Expander` header keyboard contract (Space/Enter).
- **Everything else** is the engine control's own contract (ComboBox, NumberBox, Slider, CheckBox,
  ToggleSwitch, CommandBar, AutoSuggestBox).
- **No shortcut is bound BY this surface** — no accelerator, no access key, no `Ctrl+*` handler anywhere in the
  settings or diagnostics files. Two app-level chords merely have a face here: `Ctrl+K` → the palette's
  "Settings" entry (18-shell-frame.md / 19-shell-overlays.md), and `Ctrl+± / Ctrl+0 / Ctrl+wheel`, whose ladder
  IS the Appearance › Zoom row — the row reads `Viewport.Zoom` live so a chord moves the combo without a
  settings write (`SettingsPage.cs:73-82`, `Appearance.cs:198-205`).

### 6.3 Drag and drop

**None.** There is no drag source and no drop target anywhere in Settings or the diagnostics pages. The video
attachment feature accepts drops, but only on a TRACK ROW (01-track-row.md) — never here.

### 6.4 Tooltips

Only two, both control-owned: the crossfade slider thumb (`"0.# s"`, `SettingsPage.Playback.cs:248`) and the
lyrics-blur slider thumb (`"N%"`, `SettingsPage.Appearance.cs:145`). `SettingsCard.Options.ActionIconToolTip`
exists (`SettingsCard.cs:88`) and is never set. The CommandBar labels are its own button labels, not tooltips.

### 6.5 Inline edit / pickers / dialogs

- **File pickers** (Win32, via `FluentApp.WindowHandle`): choose a cache folder
  (`FilePicker.PickFolder`, `Storage.cs:621`), export a log session (`FilePicker.SaveFile`, `LogsPanel.cs:394`),
  ~~save an API response (`ApiConsolePage.cs:118, 141`)~~ (struck — the API console is DELETED, plan §9.6 Q7, 2026-09-12), pick/locate a video (`FilePicker.OpenFile`,
  `VideoOverrides.cs:264-266`).
- **Explorer**: `SettingsShared.OpenFolder` (8 call sites) and `ShellOpen.RevealInExplorer` (crash reports,
  video override rows).
- **Native Windows signature dialog**: `PeAndSignature.TryShowNativeSignatureDialog(dllPath,
  FluentApp.WindowHandle)` from the runtime expander's Signature row, with a Warning toast on failure
  (`Playback.cs:292-297`).
- **`ContentDialog`**: §2 W27.

### 6.6 Focus visuals

`SettingsCard` root `FocusVisualMargin = Edges4.All(-3f)` when clickable (`SettingsCard.cs:165`); SelectorBar
item `−2` (`SelectorBar.cs:183`); picker radio item `+2` so the ring does not draw through the card's own border
(`WaveePicker.cs:238-245`); EQ surface `−3` (`WaveeEqualizerCurve.cs:145`).

### 6.7 Accessibility names

| element | role | name source |
|---|---|---|
| clickable SettingsCard | `AutomationRole.Button` | header text | `SettingsCard.cs:163` |
| SelectorBar item | `AutomationRole.Tab` | item label | `SelectorBar.cs:180` |
| picker card | `RadioButton` (via `RadioButtons`) | the card's `Titled` label | `WaveePicker.cs:271-286` |
| EQ curve | `AutomationRole.Slider` | **UNVERIFIED — no explicit name is set**; the surrounding `SettingsExpander.Item` header ("Curve") is the only nearby text | `WaveeEqualizerCurve.cs:140` |
| clickable log badge | `AutomationRole.Button` | **UNVERIFIED — no name; the badge shows only a count** | `LogsPanel.cs:281-282` |
| flyout back button | `AutomationRole.Button` | **UNVERIFIED — glyph only, no name** | `VideoOverrideManagerFlyout.cs:163-170` |
| FPS overlay | none | `HitTestPassThrough`, not in the tree for input | `WaveeApp.cs:430-436` |

Three unnamed interactive elements are a real 0.2.9 defect. 0.3 should name them; that is an improvement, not a
fidelity break.

---

## 7. Data & readiness in 0.3 terms

This surface is the one place in Wavee whose model is **not** the entity graph. Its inputs are the settings
store, the filesystem, the process, the OS and four service snapshots. Plan §4 has nothing to say about any of
them — see §9.

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| every toggle / combo / slider / segment | `IAppSettings.Get(SettingKey<T>)` (`Platform/AppSettings.cs`) | `Platform.Settings.Get(key)` — a CORE typed key table (30-appearance-preferences.md §5.1) | **always ready**; the store is loaded before the window exists (`Platform.Boot()`, plan §3.5) |
| any row's live re-read | **read in render (4):** `_uiEpoch` (`SettingsPage.cs:106`), `PlayerBarPrefs.Epoch` (`:107`), `NpvPlayerPrefs.Epoch` (`Appearance.cs:184`), `NotificationPrefs.Epoch` (`Notifications.cs:27`), plus `prefs.Design` in `SidebarSettingsCard` (`Appearance.cs:618-619`). **Written but NOT read here (4):** `AppearancePrefs`, `LyricsPrefs`, `DetailHeroPrefs`, `PlaybackPrefs` — this page bumps those for OTHER mounted surfaces and relies on its own `Bump()` for itself. Do not "tidy" that into one list; a page that reads its own writes twice re-renders twice. | the same epoch signals, in `Platform/Design.cs` | always |
| Zoom row's "Auto (N%)" | `Viewport.Zoom` + `Viewport.Size` contexts → `ZoomAutoPolicy.Suggest(w×z, h×z, Auto)` | unchanged engine contexts | always |
| Graphics adapter list | `GpuAdapterInfo.EnumerateAdapters()` — frozen at mount | unchanged (engine) | cold DXGI walk, **once per mount**; show "Automatic" until the seeding effect lands |
| Runtime card | `svc.Playback.RuntimeStatus` (`Signal<PlaybackRuntimeStatus>`) | `Playback.RuntimeStatus` signal | three terminal states, no skeleton — `NotApplicable` renders the "not set up" InfoBar |
| Metered status line | `NetworkPolicy.Cost` + `NetworkPolicy.MeteredQualityCap` signals | `Platform.Network.Cost` / `.MeteredCap` | fail-soft: `Unknown` renders "Couldn't read network cost (treated as unmetered)" — **never blank** |
| EQ gains | `PlaybackDsp.ReadEqGains(settings)` → `EqualizerSettings.ReadGains` | `Settings.Eq.ReadGains(Platform.Settings)` | always (garbage parses to 0 dB) |
| Video-override roster | `VideoOverrideService.All()` + `Directory.Exists` + `IColdStore.GetTrack(uri)` for titles, on a background task | **needs a new home** — see DATA GAPS | `_voLoad != Ready && rows.Count == 0` → 18-DIP spinner ONLY on the cold load (a warm reload keeps the last roster so the flyout's anchor node survives — `VideoOverrides.cs:126-134`) |
| Storage census | `Directory.EnumerateFiles` over `%LOCALAPPDATA%\Wavee`, `AudioBodyDiskCache.ResolveDirectory`, `LicenseKeyDiskCache.DefaultDbPath` | `Settings.Host.StorageCensus()` | `StorageLoadPhase` = NotStarted → Loading → Ready \| Failed; **the row cards render throughout**, only their size text is withheld (`Storage.cs:217`) |
| Metadata cache stats | `CachedStore.GetCacheStats()` on the writer connection, off-thread | `Store.Stats()` (plan §4.4's sqlite layer) | `_metaStats == null` → the generic description (`Storage.cs:73`) |
| Resident cache line | `CachedStore.ResidentMembershipBytes/Count`, `.EntityCounts`, `.EstimatedEntityBytes` | **no equivalent** — see DATA GAPS | `cold == null` → the generic description (`Storage.cs:283`) |
| Update hero / status | `IAppUpdateService.Current` + `.Changed` (`IObservable<int>`) | `Update.Current` signal | every `AppUpdateState` has a pill AND a button — **never a blank card** (`About.cs:569-619`) |
| Release-notes unread dot | `WaveeSettings.ReleaseNotesLastSeen != AppVersion.Info.Core` | same | always |
| "Wavee right now" receipts | `Process.GetCurrentProcess()`, `GC.GetTotalMemory`, `WaveeStartupBench.Host.LastStats.Fps`, `D3D12Device.LastVideoMemory`, `GpuProfile` | `Diagnostics.Receipts()` | initial `"—"` for all nine; the first `UseEffect(Tick)` fills them at mount; `snap.Valid == false` prints "— (no Present yet)" and a sentence explaining why (`About.cs:322-327`) |
| Third-party notices | `File.ReadAllText(AppContext.BaseDirectory/THIRD-PARTY-NOTICES.txt)` cached in a static | same, cached once per process | missing file → the `settings.about.noticesMissing` string, never an empty expander |
| Crash reports | `CrashReportFiles.List(CrashReport.DefaultDirectory, 10)` in a `UseMemo` | `Diagnostics.CrashReports(10)` | listed once at mount; empty → one "No crash reports" row |
| Log sessions | `WaveeLogSessions.ListPastSessions(WaveeLog.Instance.BasePath, pid)` off-thread | `Diagnostics.Sessions()` | picker shows only "This session" until the list lands, then `_sessionsRev` bumps the Key |
| Log entries | `WaveeLog.Instance.Snapshot()` (live ring) or `WaveeLogSessions.LoadSession(info)` | same | `entries == null` → the loading block; `result.Shown == 0` → the empty block |
| Runtime diagnostics | `PlayPlayProvisioner.GetDiagnostics()` / `.GetSnapshot()` | `Playback.RuntimeDiagnostics()` | `provisioner == null` → an **Attention** status block ("No playback session yet"), and **only** Locate / Candidates / Verify are omitted (`:48-53`). Current status still renders from `Playback.RuntimeStatus`; Playback modules, Updates, the three buttons and the caption always render (`:47, 54-58`) |
| Module list | `ModuleHost.Installed`, `.ProcessFor(id)`, `.Catalog.Rejections` | `Modules.*` (plan Wave 6 owner T) | `host == null` → "This build has no playback-module host (the fake backend)." |
| Connect diagnostics | `ConnectOwnership.Current`, `Projection.LastCluster`, `ConnectDiagnostics.LastPut/LastEcho`, `AudioPlaybackStack.Host` | `Playback.Ownership` / `Spotify.Connect.*` | each card states its own absence in prose ("No live Connect session yet.", "No cluster has folded yet.") — **never an empty card** |

**Every completed mutation toasts, and the storage ones re-run the census.** The full list of call sites, so 0.3
does not quietly drop one: delete old logs → Success, and the copy BRANCHES on the count
(`settings.storage.oldLogsDeleted` plural vs `NoOldLogsDeleted` — deleting nothing still toasts,
`Storage.cs:270-277`; and it deletes only the ROLLED `wavee-*.log` files, never the live `wavee.log`,
`:266`) · clear audio cache → Success (`:693-698`) · clear saved keys → Success (`:708-713`) · clear metadata
cache → Success (`:119-125`) · "Release now" → Success (`:410-414`) · cache relocation → Success **or** an
Error toast when `PrepareRelocationAsync` returns false (`:662-673`) · clear-all video overrides → Success
(`:298`) · per-row remove → Success **with an Undo action** · replace/locate → Success, a rejected pick → Error
(`VideoOverrides.cs:275-287`) · "View signature" failure → Warning (`Playback.cs:295-297`) · "Copy diagnostics
info" → Success on both surfaces (`About.cs:157`, `PlaybackRuntimeDiagnosticsPage.cs:309`) · "Third-party
notices" with no shipped file → Informational, and an unopenable one → Warning (`About.cs:174-183`) ·
a notification simulation → one of five severities (W14). Four of the storage ones also null `_storage` and
re-enter the census, so the usage bar visibly re-computes after a clear (`:122-123, 696-697, 711-712, 668-669`).

**The readiness rule this surface actually follows**: *a value that is not known yet is replaced by a sentence,
not by a skeleton.* There are no shimmer skeletons anywhere in Settings or Diagnostics — the two spinners
(storage census, video roster cold load) and one prose fallback per unknown are the whole vocabulary. Keep it.
Pages demand their model on mount: `RefreshStorage` + `RefreshMetadataStats` fire on the tab effect
(`SettingsPage.cs:126-137`), the video roster on tab entry plus a store-sentinel watch (`:133, 141`), the log
sessions on mount (`LogsPanel.cs:73`). Nothing here windows or pages by viewport.

### DATA GAPS

Everything this surface shows that the 0.3 plan's data model does not hold:

| # | what it shows | 0.2.9 source | proposed 0.3 home |
|---|---|---|---|
| G1 | **The settings store itself** — 130+ typed keys, their defaults, and the epoch signals that make writes live | `Platform/AppSettings.cs` (494) + `AppDataSettings` | plan §2 names `Platform/Platform.cs` but lists no settings type. Add a CORE section `Platform.Settings`: the `SettingKey<T>` table + `Get/Set` + the six epoch signals. Cross-ref 30-appearance-preferences.md §5.1. |
| G2 | **Resident-cache census** — "18.2 MB of 92.0 MB cap (14 of 24 playlists) · entities t=… al=… (~74.1 MB)" | `CachedStore.ResidentMembershipBytes/Count/MaxResident*`, `.EntityCounts`, `.EstimatedEntityBytes` | 0.3 has no resident-playlist cache (rows live in slabs). Either DELETE the row, or re-point it at a new `Entities.Census()` returning per-table `(Count, Free, Bytes)` and `Edges.Bytes`. **Decide explicitly** — this row is the only place a user sees Wavee's memory shape. |
| G3 | **Metadata-cache stats** — db bytes, cache bytes, pinned bytes, entity rows, pinned rows, overview rows, extension rows, evictable bytes, budget bytes | `Wavee.Backend.Persistence.EntityCacheStats` via `CachedStore.GetCacheStats()` | plan §4.4's `Store` gains `public static CacheStats Stats()` reading the same indexed counts + three pragmas on the WRITER connection, off the UI thread. Keep `evictable ≠ gross` (the budget governs only the evictable slice). |
| G4 | **Audio-body cache status** — budget bytes, reserve bytes, `Available`, current directory | `AudioBodyDiskCache.Status()` / `.CurrentDirectory` / `.SetBudget` / `.Trim` / `.PrepareRelocationAsync` | `Spotify/Spotify.Audio.cs` (SHELL) — the plan names the file but not the cache's status surface. Add `Audio.CacheStatus()` + `Audio.Relocate(base, mode)`. |
| G5 | **License-key count** | `AudioLicenseCache.Stats().Count` | same file. |
| G6 | **Video-override roster** — uri, path, file name, title, subtitle (artists), status (Ok/Missing/DriveOffline/Unplayable), `CanLocate`, `CanReveal` | `VideoOverrideService` + the `video-overrides` store sentinel + `IColdStore.GetTrack(uri)` for title/artists | a small `Platform.Overrides` table (uri → path) plus **a Track handle for the title**: in 0.3 the row's title is `Entities.Track(uri).Title`, which means the roster must `Ensure(TrackFields.Identity)` for its own uris before it can render. That is a real new dependency the plan does not cover. |
| G7 | **Playback runtime status + diagnostics** — outcome, detail, packId, version, arch, path, signature trust + the full candidate/verify report | `PlayPlayProvisioner` | `Playback/Playback.Os.cs` or a `Platform.Runtime` section; the report BUILDER (`BuildReport`, 58 lines of string shaping) is pure and belongs in `Screens/Diagnostics.cs` CORE with a test. |
| G8 | **Update snapshot + release-notes diagnostics** — state, target, progress, failure (kind + HRESULT), `AutoUpdateAssociated`, last checked, feed URL, notes source/cache/embedded/feed/last-fetch/budget | `IAppUpdateService`, `ReleaseNotesService.DiagnosticsSnapshot()` | `Screens/ReleaseNotes.*` (28-setup-whatsnew-feedback.md) exposes both; Settings and the diagnostics page both read them. |
| G9 | **GPU identity** — adapter name, power tier, software flag, LUID list, `D3D12Device.LastVideoMemory` (local/non-local usage + budgets, tracked resources, atlas pages, cached glyphs) | engine `GpuProfile`, `GpuAdapterInfo`, `D3D12Device` | engine, unchanged. No plan work — but `Screens/Diagnostics.cs` must own the *formatting* (`GpuSummary`, `ClassifySharedIgpu`) as pure functions with tests. |
| G10 | **Process receipts** — working set, managed heap, uptime, FPS | `Process`, `GC`, `WaveeStartupBench.Host.LastStats` | `Screens/Diagnostics.Probe.cs` (the plan's own file) — currently unowned. |
| G11 | **Log sessions on disk** — rolled-file discovery, per-session entry counts, export | `WaveeLogSessions` | `Screens/Diagnostics.Host.cs`. |
| G12 | **Crash report files** | `CrashReportFiles.List` | `Screens/Diagnostics.Host.cs`; the card itself is Feedback (`Feedback.UI.cs`, chapter 28) but it is *mounted* by About. |
| G13 | **Notification delivery setting** — `ToastNotifier.Default.Setting` (DisabledForApplication / ForUser / ByGroupPolicy / Unknown) | `FluentGpu.WindowsApi.Notifications` | engine, unchanged; the three-way banner decision is already pure enough to test (`BlockedBanner`, `Notifications.cs:242-263`) — move it into `Screens/Settings.cs` CORE and give it a test. |
| G14 | **Module host inventory** — installed modules, process state, pid, stats (requests/failures/restarts/p50/p95/last error), catalog rejections | `Wavee.Backend.Modules.ModuleHost` | `Platform/Modules.cs` (plan Wave 6 owner T) — the diagnostics page is owner S. A cross-owner read the plan does not name. |

---

## 8. Pure rules to port verbatim

Engine-free classes that encode this surface's decisions. **Port, never re-derive.**

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `SettingsCatalog` | `App/SettingsCatalog.cs` (137) | the `(Tab, Section, RowId, Glyph)` table for **General · Appearance · Playback · Storage only** (§0 N3) + the two lookups that throw on an unknown id; the no-repeat invariant is **per-section**, not global | `Wavee.Tests/SettingsCatalogTests.cs` (6 facts: section-has-rows · no row repeats its section's glyph · no glyph repeats within a section · row ids unique per tab · both lookups throw) | `Screens/Settings.cs` CORE — `Settings.Catalog` |
| `SettingsGlyphs` | `Features/Shell/SettingsGlyphs.cs` (87) | name → `Icons.*`; `Debug.Fail` + once-per-name Warn + `Icons.Settings` fallback in Release | none (engine-bound by design) | `Screens/Settings.UI.cs` — keep it OUT of the CORE section so the catalog stays test-includable |
| `EqualizerSettings` | `App/EqualizerSettings.cs` (47) | parse/clamp/serialize the 10-band gain vector; `BandCount 10`, `±12 dB`; garbage → 0 dB | `Wavee.Tests/EqualizerSettingsTests.cs` | `Screens/Settings.cs` CORE — `Settings.Eq` |
| `NotificationPrefs` | `App/NotificationPrefs.cs` (96) | topic order (declaration order IS the UI order), per-topic key mapping, clamp-to-ceiling, `Policy`, `ShowsCategory`, `TopicOf` | via `NotificationPolicy` tests | **`Platform/Notify.cs` CORE** (owner I, Wave 4 — arbitration 2026-09-12, A9). Not `Screens/Settings.cs`: the same class decides what the bell counts and what the OS banner raises (chs. 19 and 14). Settings › Notifications is a **caller**, and its tab order is `Notify.Prefs.AllTopics`, read — never re-listed |
| `NotificationPolicy` | `Wavee.Core/Notifications/NotificationPolicy.cs` | the two decisions the Notifications tab's *shape* depends on: `CeilingFor(topic)` (→ two segments vs three, `Notifications.cs:65`) and `IsScheduled(topic)` (→ the "even when closed" description suffix, the `ReconcileScheduled` call after a dial write, and the "Send event" row's copy), plus `DefaultFor` / `Clamp` | `Wavee.Tests/NotificationPolicyTests.cs` | already CORE (`Wavee.Core`); Settings only calls it |
| `WaveeLogSessions` | `Diagnostics/` (rolled-file discovery + load + export) | which past runs exist, their pid/start/entry count, and the session export | `Wavee.Tests/WaveeLogSessionsTests.cs` | **SHELL** (`Screens/Diagnostics.Host.cs`) — it touches the disk; only the LABEL formatting (`LogView.SessionLabel` / `Uptime`) is CORE |
| `MeteredStatusLine` | `App/MeteredStatusLine.cs` (73) | the metered card's description: headline kind + over-limit / roaming suffixes joined with `" · "` | `Wavee.Tests/MeteredStatusLineTests.cs` | `Screens/Settings.cs` CORE |
| `ZoomAutoPolicy` + `ZoomAutoMode` | `App/ZoomAutoPolicy.cs` (114) | `Suggest(baseW, baseH, mode)` = snap-DOWN to a plateau of `min(w/1600, h/900)` clamped to `[floor, 2]`; `MigrateMode` | `Wavee.Tests/ZoomAutoPolicyTests.cs` (also cross-checks `DesignW == WaveeSize.PageMaxW` and every plateau against the live ladder) | `Screens/Settings.cs` CORE (also read by 30-appearance-preferences.md) |
| `LyricsBlurPolicy` | `Features/Player/LyricsBlurPolicy.cs` (37) | `-1` = auto → 40 (weak GPU) / 100 (strong); `Scale`, `Enabled` | `Wavee.Tests/LyricsBlurPolicyTests.cs` | `Shell/Lyrics.cs` CORE (22-lyrics.md); Settings only calls it |
| `DetailRailPolicy.HasCustomizedRailPrefs` | `Features/Detail/DetailRailPolicy.cs:58-66` | whether "Clear all remembered sizes" is enabled | `Wavee.Tests/DetailRailPolicyTests.cs` | `Shell/Rail.UI.cs` CORE (03-detail-frame.md); Settings only calls it |
| `VideoOverrideUx` | `App/VideoOverrideUx.cs` (381) | `BuildRoster` (status per row, sort), `Search`, `RecentlyAdded`, `RootSection`, `ShowsBrowseAll`, `NearestExistingAncestor`, `Validate`, `PickerFilter` | `Wavee.Tests/Actions/VideoOverrideUxTests.cs` | `Screens/Settings.cs` CORE — `Settings.Overrides` |
| `LogView` | `Diagnostics/LogView.cs` (240) | `PageRows 500` / `MaxRows 2000`, `LevelNames`, `Build` (order → level → category → search → adjacent-repeat grouping → cap), `Categories`, `CopyText`, `FormatTime`, `SessionLabel`, `Uptime`, `MetaLine`, `IndexOfSequence`, `RemountKey` | `Wavee.Tests/LogViewTests.cs` (16 facts) | `Screens/Diagnostics.cs` CORE |
| `LogCapturePolicy` | `Diagnostics/LogCapturePolicy.cs` (70) | build-default levels, `Resolve(-1)`, `ToSetting`, `IsVerbose (≤ Debug)`, `VerboseTarget`, `EffectiveFileLevel` (upward-only), the three `Set*` writers | `Wavee.Tests/LogCapturePolicyTests.cs` | `Screens/Diagnostics.cs` CORE |
| `AppUpdateToasts.FailureText` / `.ReleaseName` | `Wavee.Core` | the failure copy and the one release-naming rule (codename → semver → quad, never empty) | `Wavee.Tests/AppUpdateToastsTests.cs` | `Screens/ReleaseNotes.cs` CORE (chapter 28); About calls it |
| `ShutdownUpdatePolicy.ShouldApply` | `Wavee.Core` | whether "install on quit" actually runs | `Wavee.Tests/ShutdownUpdatePolicyTests.cs` | `Screens/ReleaseNotes.cs` CORE |
| storage formatting | `SettingsPage.Storage.cs:206-212, 245-255` | `FmtBytes` (binary divisors, invariant, `0.0 GB` / `0.0 MB` / `0 KB` / `N B`) and `BodyBudgetIndex` (nearest preset) | **none today** | `Screens/Settings.cs` CORE — **add tests** |
| `BlockedBanner` decision | `SettingsPage.Notifications.cs:242-263` | which of the three OS-block banners to show, and whether the "Open Windows settings" button is offered (never for group policy) | **none today** | `Screens/Settings.cs` CORE — **add tests** |
| `PlaybackRuntimeDiagnosticsPage.BuildReport` | `:317-374` | the clipboard/bug-report text dump | **none today** | `Screens/Diagnostics.cs` CORE — **add tests** |
| `WaveeNowReceipts.GpuSummary` / `ClassifySharedIgpu` / `FormatBytes` / `FormatUptime` | `SettingsPage.About.cs:265-274, 362-368, 380-392` | GPU line, iGPU-vs-discrete heuristic, MB formatting, uptime tiers | **none today** | `Screens/Diagnostics.cs` CORE — **add tests** |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The four collapsed picker groups.** Flattening them into always-visible strips is exactly the bug the
   0.2.9 regroup fixed (`SettingsPage.Appearance.cs:22-24`): the page stopped fitting. Collapsed + a
   `SettingsValueTag` answer is the design.
2. **The picker-in-the-body rule.** A picker in a `SettingsExpander` header starves the header text track to
   zero and paints the title over the cards (`:16-21`). This is not a style preference; it is a layout failure
   mode with a name.
3. **The equalizer curve.** Ten sliders is not a port.
4. **The storage hue set and its double use** (bar segment + row accent bar).
5. **`InitiallyExpanded = eqOn / crossOn`.** The two feature expanders open when the feature is on — every other
   expander stays closed.
6. **The video-override warm-reload rule.** A live re-load must NOT swap the Manage button for a spinner: that
   destroys the anchor node the open flyout hangs off (`VideoOverrides.cs:126-134`).
7. **The unconditional-hook discipline.** Nine `UseContext`/`UsePost` calls sit above the tab switch precisely
   because the tab bodies are not render bodies (`SettingsPage.cs:67-88, 73-81`). The five embedded
   `Component`s exist for the same reason. 0.3 must keep both, or re-architect the tabs into real child
   components (which would change the scroll/Key story — see 9.2).
8. **One confirm shape, `DefaultButton = Close`.** Eight destructive actions in this surface, one dialog helper,
   cancel default (N11 lists all eight with line numbers).
9. **The FPS overlay's retained-slot idiom** (`FpsOverlay.cs:15-21`) and its content-sized output (a `Grow = 1`
   output would leave the component wrapper full-bleed and hittable, silently killing scrolling —
   `:24-29`).
10. **Prose over skeletons.** Every "not known yet" in this surface is a sentence. No shimmer.

### 9.2 Traps

- **Props freeze at mount.** See §1.2's table. The four `Key`-remount sites in `LogsPanel` and the one in the
  crossfade row are not incidental — they are the documented cure for `ComboBox`/`CommandBar`/`NumberBox`/
  `ItemsView` freezing their interesting fields (`LogsPanel.cs:25-31`).
- **Post before remount.** Every toggle/level command in the log CommandBar runs through `UsePost` so the click
  finishes before the Key remount it triggers tears the clicked node down (`LogsPanel.cs:203-210, 263, 267`).
  Losing this makes the overflow menu swallow clicks.
- **Never write a signal during render.** The log category clamp lives in a `UseEffect`, not in `Render`
  (`LogsPanel.cs:116-119`); the picker strips hand `RadioButtons` a FRESH throwaway signal per render carrying
  the live value rather than a mirror (`WaveePicker.cs:274-281`) — a mirror kept in step by a write-during-render
  is the `BackwardsWriteGuard`'s exact tripwire.
- **Fresh signal per control, seeded from the store.** Every toggle is `ToggleSwitch.Create(new
  Signal<bool>(settings.Get(key)), onChange: _ => settings.Set(key, !settings.Get(key)))` — the truth lives in
  the store, never in a mirror (`General.cs:158-163`). The exception is
  `SettingsHooks.UseSettingSignal` (`SettingsShared.cs:87-88`), used only inside `AboutUpdatePanel` where a
  stable per-call-site signal is required because the control freezes the instance at mount.
- **`ReuseGuard` / stable element type at a slot.** `SidebarSettingsCard` documents the failure: it used to
  return a bare `SettingsRow` for two designs and a `SettingsExpander` for the third, with no `Key`, so a design
  switch remounted the card and the section's silhouette changed under the user
  (`Appearance.cs:592-596`). ONE shape, one `Key`.
- **Zero-allocation scroll frames vs per-row richness.** The settings tabs are NOT virtualised — a tab is a
  fixed list of ≤ 20 cards built once per render, and the render only happens on an epoch bump. The ONE
  virtualised list in this surface is the log body, and 0.2.9 reconciles it by making the row cheap (fixed
  columns, no images, `MeasuredStackVirtualLayout(36f)`) and by REMOUNTING the whole list when the visible set
  changes rather than pushing reactive props into it (`LogsPanel.cs:438-449`). 0.3 must keep exactly that
  split: settings rows may be rich because they are few; log rows must stay cheap because they are many.
- **The EQ curve's width.** It self-measures (`UseMeasuredWidth(1f)`), falls back to 720, and clamps to
  ≥ 260 (`WaveeEqualizerCurve.cs:47, 57`). At the setup wizard's Sound stage it is given an explicit `height`
  instead (`:17-20`) — chapter 28 owns that call site.
- **`ContentAlignment.Vertical` for wide content.** The EQ curve item sets it explicitly
  (`Playback.cs:210`); the About links card and the receipts card use `Left`. Getting this wrong reproduces the
  header-over-content bug at a different site.

### 9.3 Where the plan is wrong or too thin for this surface

1. **§2 gives the whole `Screens/` folder one number (13 files, ~13 700 lines) and NO per-file budget** — unlike
   every other folder, which is budgeted per file. Settings + Diagnostics alone are ~7 500 lines of 0.2.9 UI.
   Fix: budget `Settings.cs 700 · Settings.UI.cs 3 400 · Settings.Host.cs 700 · Diagnostics.cs 600 ·
   **Diagnostics.UI.cs 2 300** · Diagnostics.Host.cs 500 · Diagnostics.Probe.cs 400`, and state that
   `Settings.UI.cs` splits into named partials (`Settings.UI.Appearance.cs`, `.Playback.cs`, `.Storage.cs`,
   `.About.cs`) the moment it passes budget — which D13's `<Type>.<Concern>.cs` rule already permits and which
   is exactly the 0.2.9 split.
2. **§2 has no home for four real components**: `WaveeEqualizerCurve` (407), `WaveePicker` (287),
   `VideoOverrideManagerFlyout` (295), `FpsOverlay` (45). Wave 4 owner L's `Platform/Controls.cs` is described
   as "Components/* minus entity rows/cards", so the first two land there — but nothing says so, and
   `Platform/Controls.cs` has no budget either. State it: `Controls.cs` gains `EqualizerCurve` + `Picker`
   (~700 lines of the two), `Diagnostics.UI.cs` gains `FpsPill` **and the lyrics inspector**, `Settings.UI.cs` gains
   the flyout. **`Diagnostics.UI.cs` is 2 300, not 1 700** (arbitration 2026-09-12, A14): 1 700 for the logs panel,
   the three diagnostics pages and the shared card/row/chip chrome, plus **≈600 for the lyrics inspector**, which
   `22-lyrics.md` §9 (d) moves out of the lyrics files (`LyricsInspectorDialog.cs`, 564 lines today, developer-mode
   only — `RightRail.cs:382`) precisely so the lyrics surface holds no developer dialog. It is a diagnostics
   surface: it reads `Lyrics.Diagnostics` and is opened by a glyph the rail composes only when
   `DeveloperMode.Enabled` is true.
3. **§5 Wave 6 splits Settings (R) and Diagnostics (S) but they are not separable.** Settings › Logs *is* the
   log viewer; Settings › General › Developer owns the switches that gate every developer surface; the Playback
   runtime card links to `playback-diagnostics` and `connect-diagnostics`. R and S must agree an interface
   BEFORE the wave starts: `Diagnostics.LogsPanel(IAppSettings?) → Element`, `Diagnostics.RuntimeRoute`,
   `Diagnostics.ConnectRoute` (§9.6: a route kind of its own, and registered — 0.2.9's is neither),
   and `Platform.DeveloperMode` (an
   `Enabled`/`FpsOverlay` signal pair + `IsDeveloperRoute`). ~~`Diagnostics.ApiConsoleRoute`~~ — struck, 2026-09-12
   (plan §9.6 Q7): the API console is deleted, so this seam has no third member. Today that seam is four
   `using`-free references across two files; in 0.3 it is a cross-owner contract of the two survivors.
4. **§4.12/§4.13 are the only UI guidance in the plan and neither applies here.** `Track.Row(in
   BoundItemScope<Track>, RowStyle)` and `Album.Page` teach a *bound-row, table-versioned, `Entities.Ensure`*
   idiom. The settings page has no entity, no table `Changed` signal, no `Ensure`. Its model is the settings
   store (not observable → epoch signals), the filesystem (async census → phase enum), and four service
   snapshots. The plan must say — somewhere — how a NON-entity screen re-renders in 0.3, or six Wave-6 files
   will invent six answers. Proposal: the 0.2.9 answer, unchanged and written down — *a screen that is not
   backed by a table subscribes to one or more `Signal<int>` epochs and re-reads its sources in `Render`.*
5. **Nothing in the plan mentions the settings store at all** beyond `Platform.Boot()` "log, credentials,
   settings, update policy". 130+ typed keys, their defaults, the six epochs, and `AppDataSettings`'s packaged
   vs unpackaged path resolution are load-bearing for this chapter and for
   30-appearance-preferences.md. See DATA GAP G1.
6. **§6 test migration lists no category for this surface's pure classes.** `SettingsCatalogTests`,
   `LogViewTests`, `LogCapturePolicyTests`, `ZoomAutoPolicyTests`, `MeteredStatusLineTests`,
   `EqualizerSettingsTests`, `VideoOverrideUxTests`, `LyricsBlurPolicyTests`, `DetailRailPolicyTests` all fall
   under "pure-decision tests of files that survive as core sections" (port) — say so explicitly, and add the
   five NEW test files §8 asks for.
7. **Wave ordering.** Settings is Wave 6, but it *writes* preferences that Waves 4 and 5 *read*. The epoch
   signals must exist by Wave 4 (Design.cs) or the Wave-5 pages will hardcode defaults and Wave 6 will have to
   re-open them.

### 9.4 Line budget

| | lines |
|---|--:|
| 0.2.9, this surface (UI + its own pure rules, as counted in the header) | **7 515** |
| plan §2 target | *none stated per file*; the whole `Screens/` folder is ~13 700 for 13 files incl. Setup, ReleaseNotes and Feedback |
| honest estimate for 0.3 | **6 800**, **+ ≈600 arriving from `22-lyrics.md`** (the lyrics inspector into `Diagnostics.UI.cs`, A14) = **≈ 7 400** for the two owners' files together |

Where the 700-line saving comes from, and nowhere else: one copy of the diagnostics `Card`/`Row`/`Chip`/`Body`/
`Caption` chrome instead of two verbatim copies (≈ −90); one `FmtBytes`/`SerializeEqGains` instead of the
duplicate pair in `Storage.cs` and `Playback.cs:423-433` (≈ −40); the settings-store plumbing collapsing into
one `Platform.Settings` section (≈ −250); `Services?.` null-guarding disappearing now that there is one static
graph (≈ −300, spread across every method that currently takes `Services? svc`). **Nothing else should shrink.**
If the re-author's file is materially under 6 500 lines, something in §9.1 was dropped.

### 9.5 Files or pages missing from the §2 tree

| missing | where it must go |
|---|---|
| the settings key table + epochs | `Platform/Platform.cs` (G1) |
| `WaveeEqualizerCurve` | `Platform/Controls.cs` |
| `WaveePicker` | `Platform/Controls.cs` |
| `VideoOverrideManagerFlyout` | `Screens/Settings.UI.cs` |
| `FpsOverlay` | `Screens/Diagnostics.UI.cs` |
| `LyricsInspectorButton` / `LyricsInspector` / `LyricsInspectorBody` (`Features/Player/LyricsInspectorDialog.cs`, 564 — a developer-only dialog owned by no chapter's file plan before this pass) | `Screens/Diagnostics.UI.cs` as `Diagnostics.LyricsInspector` (≈600 of its 2 300 — arbitration 2026-09-12, A14; specified by `22-lyrics.md` §2 W17–W19b and §3, ported by owner **S**, reading `Lyrics.Diagnostics` from `Lyrics.Host.cs` and opened from `Rail.UI.cs` by a `DeveloperMode.Enabled` glyph) |
| `CrashReportsCard` | `Screens/Feedback.UI.cs` (mounted by `Settings.UI.cs` — cross-owner, chapter 28) |
| ~~`ApiDebugBodyBuilder` / `ApiDebugExecutor` / `ApiDebugProto` / `ApiDebugProtoDecomposer` (the API console's backing helpers, ~1 400 lines, developer-only)~~ | **DELETED, 2026-09-12 (plan §9.6 Q7).** The console they serve (`ApiConsolePage`, 329 lines) has no function without them, so both go: nowhere in 0.3, not `Screens/Diagnostics.Probe.cs`. This is no longer the live decision it was when this row was written — it is settled. |
| `SettingsCatalog` + `SettingsGlyphs` | `Screens/Settings.cs` (CORE) + `Screens/Settings.UI.cs` |
| `WaveeLogSessions` / `WaveeLog` | `Platform/Platform.cs` or `Screens/Diagnostics.Host.cs` — the plan names neither |

### 9.6 A 0.2.9 route defect to fix, not port: `connect-diagnostics` is not a registered route

**The defect.** `ContentHost.PageFor` renders `connect-diagnostics` (`ContentHost.cs:244-246`) but the key is
absent from `ShellRoutes.s_exact` (`ShellRoutes.cs:26-44`), which lists its two siblings `api-console` (`:39`; DELETED in 0.3, plan §9.6 Q7)
and `playback-diagnostics` (`:40`). By that class's own ownership note — *"a key that PageFor can render but this
class rejects is a page the user can no longer deep-link to"* (`ShellRoutes.cs:18-20`) — the page is exactly that.
Three consequences, each with its own call site:

1. **The deep link is refused.** `wavee://open?route=connect-diagnostics` fails the `ShellRoutes.IsKnown` gate,
   logs `deeplink.route.unknown` and returns before it can navigate (`WaveeShell.cs:1819-1823`).
2. **A history row for the page is dimmed and inert.** `HistoryPage.EntryRow` computes
   `reachable = ShellRoutes.IsKnown(e.Route.Name)` and renders an unreachable destination as a record, not an
   offer (`HistoryPage.cs:414-418`). A user who visited the page once cannot get back to it from History.
3. **Its tab is labelled "Your Library".** `ShellNav.Dest` has no arm for the key either, so it falls through to
   the switch default → `Strings.Nav.YourLibrary` + `Icons.MusicNote` (`ShellNav.cs:50-79`) in the tab strip, the
   back/forward flyout and the not-found glyph. (A *restored* tab does come back: `WorkspaceTabsPersistence.Decode`
   validates only blankness and length, never `IsKnown` — `WorkspaceTabsPersistence.cs:29-33`. It comes back
   mislabelled, not missing.)

And because the page is also not pinnable (`SidebarPinId.cs:79` lists the other four dev/utility routes and not
this one), 0.2.9 leaves it with **one** entry point: the "Connect diagnostics" link inside the Settings › Playback
**error** InfoBar (`Playback.cs:162`), which is composed only when the runtime status is neither Ready nor
NeverAttempted (`:116-143`). On a healthy install the page is unreachable. 18-shell-frame.md §7 records the same
defect from the shell's side (together with the identical `Dest` hole on `disco:` and `whatsnew`); this chapter
records it because W24 wireframes the page.

**No issue is filed for it today** (`gh issue list --state all --search connect-diagnostics` returns nothing).
Per CLAUDE.md's "every fix references its issue", file one before the 0.3 fix lands, so the CHANGELOG bullet can
end with ` (#n)` and the commit body can carry `Fixes #n` — the release script's `issue refs` gate rejects a
mismatch. **Status, 2026-09-12 (plan §9.6 Q6): the orchestrator has drafted this issue for Christos's approval —
it is not filed. Record it as "issue drafted, awaiting approval — no number yet" and leave the `(#n)` placeholder
in place wherever the fix is cited until a real number exists.**

**The 0.3 fix, and why the 0.3 design already removes the class of bug.** Plan §4.11 gives the whole surface one
`RouteKind.Diagnostics` member, which would fold both reports onto one key and re-create the drift in a new
shape. Instead:

| 0.3 `RouteKind` | page | title / glyph | `IsKnown` | developer-only |
|---|---|---|---|---|
| `PlaybackDiagnostics` | `Diagnostics.RuntimePage` | `Strings.Nav.PlaybackRuntime` · `Icons.MusicNote` | yes | yes (today's `WaveeShell.cs:1826` special case becomes this column) |
| `ConnectDiagnostics` | `Diagnostics.ConnectPage` | "Connect diagnostics" · `Icons.Devices` | **yes — the fix, still awaiting its issue (§9.6 Q6, plan 2026-09-12)** | no (ownership is a user-facing question, not a dev tool) |
| ~~`ApiConsole`~~ | ~~`Diagnostics.ApiConsolePage`~~ | ~~`Strings.Nav.ApiConsole` · `Icons.Code`~~ | ~~yes~~ | ~~yes~~ — **struck, 2026-09-12 (plan §9.6 Q7): the API console and the four `ApiDebug*` helpers behind it are DELETED, not ported. No `ApiConsole` kind exists in 0.3's route table.** |

**Two** distinct kinds, **one** table (was three before Q7 deleted the console). The 0.3 route table carries
`(page, isKnown, title, glyph, developerOnly)` per kind, so `Shell.PageFor` and `Shell.IsKnown` read the same rows
and a kind cannot be added with four of the five filled in — the two-lists problem that produced this defect stops
existing by construction. Two riders: `Shell.Dest` must have **no fall-through default that names a real
destination** (a kind with nothing to say says its own kind), and the Settings › Playback link must stop being the
page's only door — 0.3 should offer `ConnectDiagnostics` from the Ready runtime expander as well, not only from the
error banner. **`ConnectDiagnostics`'s own fix still needs its GitHub issue filed before it lands (Q6): the
orchestrator has drafted one for Christos's approval — awaiting approval, no number yet.**

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Route in via the profile menu → Settings, or the command palette (`Ctrl+K` → "Settings"), or
`wavee://settings`. Unless stated otherwise: window **1600 × 1000**, sidebar at its default width, light AND
dark both checked.

**Frame and rhythm**

1. Page masthead is a 24-DIP gear + "Settings" at 28/36/600, padded (36,16,36,**12**) — the gap between the
   title baseline block and the tab strip is 12, not 16. *Static capture, measure the glyph and the cap height.*
2. Seven tabs in order: General · Appearance · Playback · Notifications · Storage & cache · Logs · About. *Static
   capture; read the strip left to right.*
3. The selected tab's pill is 16 × 3 DIP, accent, flush with the item bottom — not an underline, not bold text.
   *Zoom to 400 % on a static capture.*
4. A 1-DIP `StrokeDividerDefault` hairline runs the full padded width under the strip. *Static capture.*
5. Card column stops at 1000 DIP and is LEFT-aligned: at 1600 × 1000 there is ~500 DIP of empty space to the
   right of every card. *Static capture; measure from the card's right edge to the pane's right edge.*
6. Cards are 4 DIP apart; the gap above a section header is 32 DIP and below it 8 DIP. *Static capture, measure
   three consecutive gaps on the General tab.*
7. Switching tabs is instant — no fade, no slide. *Frame recording at 60 fps: the first frame after the click
   must already show the new body.*
8. Scroll the Appearance tab to the bottom, switch to Playback and back: Appearance is still at the bottom.
   *Two static captures.*
8a. Scrolling never changes the chrome: the gear + "Settings" stay 24/28 DIP, the tab strip stays put, the
    hairline gains no shadow, and no section header sticks to the top. *Frame recording while scrolling the
    Playback tab top to bottom.*
8b. Hovering an ordinary settings row changes NOTHING on the card — no fill, no border, no shadow; only the
    control inside it (combo, toggle, button) reacts. *Hover capture over the "App language" label, not the
    combo.*
8c. Hovering a `SettingsExpander` header changes nothing either, except the 32-DIP chevron button, which gains
    a subtle fill; the whole header is still the click target. *Hover capture over "Row density" text, then
    over its chevron.*

**Reflow**

9. Narrow the window until the card is 470 DIP wide (pane ≈ 542): every right-aligned control drops BELOW its
   header, 8 DIP down, left-aligned. *Static capture at pane 542 and at pane 560.*
10. Narrow to card 280 (pane ≈ 352): the 20-DIP header glyph disappears on `SettingsRow`s but NOT on expander
    items. *Static capture; compare the "App language" row with the "Always hide track artwork" row inside the
    open density expander.*
11. There is no hysteresis: dragging the window edge back and forth across pane 548 flips the layout on every
    crossing. *Frame recording while dragging.*
11a. The EQ curve item is wrapped (content below the header, full lane) at EVERY width, including 1600 — it is
    `ContentAlignment.Vertical`, not a narrow-window state. *Static capture at pane 1600 and at pane 600.*
11b. The About links card and the receipts card have no header, no glyph and no chevron at any width — they are
    `ContentAlignment.Left`, which bypasses both thresholds. *Static capture at pane 352.*

**Appearance tab**

12. All four groups (Row density, Track list style, Track page layout, Sidebar › Design) are COLLAPSED on first
    open, and each header shows its current value in 14-DIP secondary text. *Static capture.*
12a. Opening any expander: the chevron rotates **180°** (a down-chevron flipped to point up), the header keeps
    only its TOP corners rounded while the body is mounted, and the body panel is `FillCardSecondary` — one
    step darker/lighter than the header's `FillCardDefault`, with the header's bottom border as the seam.
    *Frame recording + static capture, zoom 400 % on the header/body junction.*
12b. Sidebar › Design's "Customize sidebar" item exists only while Wavee Curated is the active design; switch
    to Classic and the expander keeps its shape but the item disappears. *Two static captures.*
13. Open Row density: four 116 × 84 cards, 12 DIP apart, labels below at 12 DIP; the selected card is
    accent-filled with a 2-DIP accent border and a 600-weight primary label. *Static capture.*
14. The four density miniatures show visibly different row heights (10 / 12 / 14 / 16 DIP) and art edges
    (8 / 8 / 10 / 12), and the bar ladder beside the art is IDENTICAL on all four (Grow-1 strong bar, then a
    fixed 24 × 2 and a fixed 16 × 2 faint pill). *Static capture, zoom 400 %.*
14a. Narrow the window with Row density open: the four tiles WRAP to 3, then 2, then 1 column (card ≈ 532 /
    404 / 276 DIP) — they never shrink and the strip never scrolls horizontally. At one column, Up/Down
    traverses the strip and Left/Right do nothing. *Four static captures + a keyboard pass.*
14b. Track list style and Track page layout wrap the same way (two tiles → one column), and Sidebar › Design's
    three compact cards likewise. *Static capture at pane ≈ 400.*
15. Hovering a picker card scales it to 1.02 and does NOT shift the wireframe inside it. *Frame recording.*
16. Selecting a picker card grows its border 1 → 2 DIP with **zero** pixel movement of the wireframe.
    *Two static captures, diffed.*
17. Track page layout's Items show "Keep left-rail same size"; turning it ON removes the "Clear all remembered
    sizes" row entirely (not disables it). *Two static captures.*
18. With no rail ever resized, "Clear" is disabled. *Static capture on a fresh `--fake` profile.*
19. The Zoom combo's first item reads "Auto (N%)" with N following the window size; resize the window and reopen
    the combo to see N change. *Two static captures at 1600 × 1000 and 2560 × 1440.*
19a. That head item is the selection for BOTH the Auto and the Dense zoom modes; only a manual ladder pick
    highlights a percentage rung. Picking the head item while in Dense writes mode = Auto. *Two static captures
    with `zoomMode` pre-seeded in store.json.*
20. Lyrics blur: with the setting on Auto there is NO "Auto" link beside the slider; drag the slider once and the
    link appears; click it and the link disappears again. *Three static captures.*

**Playback tab**

21. Runtime Ready renders a collapsed expander with `Icons.StatusSuccess` and a "Manage..." button; expanding it
    shows Version / Architecture / Signature / Location. *Static capture, expanded.*
22. Runtime error renders an Error InfoBar with THREE actions in one row: "View diagnostics", "Connect
    diagnostics" (links) and "Retry setup" (accent button). *Static capture (force by clearing the runtime
    path).*
23. The Equalizer expander is OPEN when the EQ is on and CLOSED when it is off, on first open of the tab. *Two
    static captures, toggling the setting between app runs.*
24. The curve fills the expander item's full 898-DIP lane at pane 1072 and is 341 DIP tall. *Static capture,
    measure.*
25. The curve shows five dashed horizontal grid lines labelled +12 / +6 / 0 dB / −6 / −12, with "0 dB" heavier
    (650 weight, secondary) than the rest (400, tertiary). *Static capture, zoom 400 %.*
26. Ten frequency labels 31 … 16k, 10-DIP Cascadia Code. *Static capture.*
27. Hovering a node grows it 14 → 18 DIP and adds a flyout shadow; the value pill follows the ACTIVE band, not
    the hovered one. *Hover capture.*
28. Dragging a node moves the curve continuously and snaps the value to 0.5 dB steps. *Frame recording; read the
    pill.*
29. Keyboard: Tab to the curve, Right ×3 then Up ×4 raises band 8 (index 8 from a default active band 5) by
    exactly +2.0 dB. *Two static captures of the pill.*
30. Turning the EQ off drops the whole curve to 0.58 opacity, removes the hand cursor and disables the "Reset
    curve to flat" link. *Static capture + hover capture.*
30a. With the EQ off, NO node is hot: every node stays 14 DIP with a 2.5-DIP `TextDisabled` border and no
    shadow, even under the pointer, and the Curve item's description changes to the "off" sentence. *Hover
    capture over a node with the EQ off.*
30b. The value pill never leaves the card: drag band 0 to +12 and band 9 to −12 — the pill stops following
    horizontally near each edge (clamped to 6 DIP) and vertically 42 DIP above the node. *Two static captures.*
30c. Choosing the "Proof" preset draws a full-swing ±12 zig-zag through all ten bands (the diagnostic preset);
    "Flat" is a straight line on 0 dB. *Two static captures.*
31. The crossfade slider and NumberBox stay in lock-step: dragging the slider updates the box and the item
    description ("N seconds"). *Frame recording.*
32. Turning crossfade off immediately disables BOTH controls (no stale enabled NumberBox). *Static capture.*
33. "On metered connections" always shows a sentence — on an unmetered machine it reads "Not metered". *Static
    capture.*
33a. Both audio-quality combos offer exactly THREE items — Normal / High / Very High, each with its own
    description line — and there is no "Lossless" row, enabled or disabled. *Static capture of both combos
    open.*
33b. The "On metered connections" combo is bound to the LIVE cap: change it from another surface (or force a
    `NetworkPolicy.MeteredQualityCap` write) and the combo moves without re-entering the tab. *Two static
    captures.*
33c. Turning the EQ off while its expander is open leaves the expander OPEN (the `InitiallyExpanded = eqOn`
    read happens at mount, not per render) — only re-entering the tab collapses it. Same for Crossfade.
    *Frame recording of the toggle, then a tab round trip.*
34. Video overrides: with 0 attachments the row shows "No videos attached yet" and a "Manage" button but NO
    "Remove all". *Static capture on a fresh `--fake` profile.*
35. "Manage" opens a 420-DIP flyout anchored to the button's bottom-left, with a search box, "Recently added"
    and a "Browse all…" row. *Static capture.*
35a. On the `--fake` backend (no curation service) the Attached-videos row is GREYED with no control at all —
    no count, no Manage, no spinner. *Static capture with `--fake`.*
35b. Removing an attachment raises a Success toast carrying an **Undo** action; pressing it re-attaches the
    same file. *Toast capture + a second static capture of the restored row.*
35c. (runtime card, cf. 21) The Ready expander's Version / Architecture / Signature / Location items grey out
    individually when the provisioner has no value for them, and "View signature" appears only when the DLL
    still exists on disk. *Static capture expanded, then again after renaming the DLL.*

**Notifications tab**

35d. Pressing "Manage" a second time CLOSES the flyout (it is a toggle, not an opener); navigating to another
    settings tab also closes it, and returning does not re-open it. *Three static captures.*
36. Eight topic rows in the fixed order: New albums · New episodes · Pre-saved albums · Concerts · Followers ·
    Daylist · Wavee updates · Library activity. *Static capture.*
37. "Your own library activity" shows TWO segments (Off / In Wavee), not three. *Static capture, zoom.*
38. The two scheduled topics append "  ·  Arrives even when Wavee is closed" to their description. *Static
    capture.*
39. Turning Windows notifications OFF disables "Play a sound" and "Quiet hours", hides the From/Until row, and
    shows an Informational InfoBar above the topic list. *Two static captures.*
40. With developer mode ON, every topic row becomes an expander with one "Send a test event" item; with it OFF
    they are plain cards with no chevron. *Two static captures.*
40a. Block Wavee's notifications in Windows Settings (per-app) and reopen the tab: a Warning InfoBar appears
    with an "Open Windows settings" button. Under a group-policy block the SAME bar appears with **no button**.
    With notifications healthy — or unreadable — there is NO bar at all. *Three static captures (the policy one
    via `HKLM\…\PushNotifications\NoToastApplicationNotification`).*

**Storage tab**

40b. With developer mode ON, press "Send event" on a topic set to Off: a 6 s Informational toast says the event
    was dropped. Set quiet hours to cover now and press it on a Windows-level topic: the toast carries a real
    **HH:mm** release time (never `--:--` unless the pipeline gives none). *Two toast captures.*
40c. The "Send a test event" row's description differs between a scheduled topic (Pre-saved albums, Daylist)
    and every other topic. *Two static captures, both expanders open.*
41. On first entry the census block shows a 20-DIP indeterminate ring + "Reading storage sizes…", and the row
    cards below it are already present with no size text. *Frame recording of the first 500 ms after the tab
    click.*
42. When the census lands, the usage card shows "total" + a bold 18-DIP figure, a 10-DIP segmented bar, and a
    wrapping legend whose percentages sum to 100 (±1 from rounding). *Static capture; add the percentages.*
43. The seven legend hues match the seven 3-DIP row accent bars one-for-one, in both light and dark. *Two static
    captures.*
44. A zero-byte category is omitted from the bar and the legend but its row card is still shown. *Static capture
    on a fresh `--fake` profile (image cache empty).*
44a. With EVERY category at zero the usage card shows "total 0 B" over **no bar at all** — not an empty grey
    track. *Static capture against an empty `%LOCALAPPDATA%\Wavee`.*
44b. Before the census lands, no row card shows any size text (the 13-DIP secondary number is simply absent,
    not "—" and not "0 B"). *Frame capture of the first 500 ms.*
45. Audio body budget: switching Fixed → Drive share → Unlimited swaps the editor to a preset combo + GB
    NumberBox, a percent NumberBox reading "Auto (10%)" at 0, and a plain sentence. *Three static captures.*
46. Typing a custom GB value snaps the preset combo to "Custom". *Two static captures.*
47. Exceeding the budget turns the 300-DIP progress bar to its Error state. *Static capture (set the budget to
    1 GB with a larger cache).*
48. "Delete old logs" / "Clear audio cache" / "Clear saved keys" / "Clear metadata cache" / "Factory reset" each
    open a confirm dialog whose DEFAULT button is Cancel. *Five static captures; press Enter and confirm nothing
    happens.*
48a. Confirming "Delete old logs" on a folder with no rolled `wavee-*.log` files STILL toasts ("no old logs
    deleted") and the live `wavee.log` is untouched. Confirming with rolled files toasts a pluralised count.
    *Two toast captures + a folder listing before and after.*
48b. After any successful clear, the usage bar and the "total" figure visibly re-compute (the census re-runs) —
    the card does not keep the pre-clear numbers until the next tab visit. *Frame recording across the confirm.*
49. Changing the cache location opens dialog 1 ("Change audio cache location?"), and choosing "Start empty"
    opens dialog 2 ("What should happen to the old cache?"). *Two static captures.*

**About tab**

50. The hero is a 64-DIP accent-subtle rounded tile + a 24-DIP version line + a wrapping pill row + a provenance
    line, with the primary action and up to two links right-aligned. *Static capture.*
50a. On a build that has never polled the feed, the provenance line reads "Built <date>  ·  not checked yet" —
    and on an unstamped build, just "not checked yet" with no leading separator. *Static capture on a fresh
    profile with `lastCheckedMs` cleared.*
51. The version quad pill is monospaced and the commit/arch stamp is plain 12-DIP tertiary Cascadia Code.
    *Static capture, zoom.*
52. With the running release's notes unread, "Open What's new" carries a dot badge; opening the page clears it.
    *Two static captures.*
53. The receipts card shows **eight labelled lines plus one unlabelled wrapping tertiary line** and refreshes
    within 5 s of a zoom change (Ctrl+= then wait). *Frame recording over 6 s.*
53a. The hero's right column carries the primary button plus up to two links: "What's new in <codename>"
    (only when this build has a codename) and "Show the update summary again" (never on a dev build).
    *Static capture on a packaged build and on a `dotnet run` build.*
53b. Open the Crash reports expander: with no reports it holds ONE row whose only content is a 12-DIP tertiary
    sentence; with reports each row is a `"g"`-formatted timestamp plus [ Report ] and [ Open ]. *Two static
    captures (drop a fake `crash-report-*.txt` into the log folder for the second).*
54. "Copy diagnostics info" toasts "Diagnostics info copied" and the clipboard contains the OS, engine, GPU,
    data folder, feed and runtime outcome lines. *Toast capture + paste.*
55. The Licenses section has exactly two expanders (Wavee / Third-party notices), both closed, body text in
    12-DIP Cascadia Code tertiary. *Static capture, both expanded.*
56. On a `dotnet run` build the Third-party notices expander body reads "Third-party notices are generated at
    packaging time." *Static capture in the unpackaged build.*

**Logs tab**

57. The Logs tab has NO page scrollbar; the log card fills the remaining window height exactly. *Static capture
    at 1600 × 1000 and at 1600 × 700.*
58. Session combo lists "This session" with a "pid N · running …" description, plus past runs after ~1 s.
    *Static capture after launching the app twice.*
59. The command bar shows Refresh · Copy visible · Export session · Open log folder · Clear view, a separator,
    then three toggle buttons (Newest first, Group repeats, Verbose), with Wrap long lines and "Report this
    session…" in the overflow. *Static capture + overflow capture.*
60. "Clear view" is disabled while a PAST session is selected. *Static capture.*
61. The level Segmented is 34 DIP tall with four items; the warning and error `InfoBadge` counts appear only
    when non-zero and set the filter when clicked. *Static capture + click.*
62. A log row is 36 DIP tall with fixed columns: 12-DIP chevron, 6-DIP severity dot, 92-DIP mono time, 58 × 22
    level pill, 96-DIP mono category, message, ×N badge. *Static capture, measure.*
63. Level pill colours: ERROR/CRITICAL critical, WARN caution, DEBUG/TRACE tertiary, INFO accent — 10-DIP
    800-weight uppercase on a 12 %-alpha fill with a 38 %-alpha border. *Static capture, zoom 400 %.*
64. Single click expands a row: the chevron rotates 90° over 167 ms, the row fills `FillSubtleSecondary`, and
    Fields / Exception `CodeBlock`s plus an 11-DIP mono meta line appear indented 44 DIP. *Frame recording +
    static capture.*
65. Turning Verbose on makes DEBUG/TRACE rows appear live and the footer caption change to "Capturing Trace+ ·
    file Info+". *Two static captures 1 s apart.*
66. The footer reads "Showing N of M events · live ring" (or "· log file" for a past session) and offers "Load
    more" only while truncated; each press adds 500 rows up to 2 000. *Four static captures.*
67. Scroll the list, change the search text, clear it: the scroll offset is preserved across the remount.
    *Frame recording.*
67a. Trace / Debug / Info rows show NOTHING in the 6-DIP dot column; only Warning and Error/Critical rows carry
    a dot, and the timestamps stay aligned either way. *Static capture, zoom 400 %.*
67b. Narrow the window to ~700 DIP: the filter row WRAPS to two or three lines (search · level Segmented ·
    badges, then the category and the two headed level combos) — it never clips or scrolls horizontally.
    *Static capture at 1600, 900 and 700.*
67c. Select a past session: the 750 ms live tail stops entirely (the footer's "Showing N of M" no longer moves,
    "Clear view" is disabled, and only Refresh re-reads). *Two static captures 5 s apart.*
67d. Expand a row, then toggle "Newest first" (or "Group repeats", or change session): the row COLLAPSES.
    Change the search text or the level filter instead: an expanded row that survives the filter STAYS
    expanded. *Four static captures.*
67e. Expand a row that carries neither fields nor an exception: it expands to exactly one 11-DIP mono meta line
    indented 44 DIP — no empty "Fields" caption, no empty `CodeBlock`. *Static capture, zoom 400 %.*
67f. Press "Load more" twice on a long session, then switch session and come back: the window is back at 500
    rows. *Three footer captures.*

**Diagnostics pages**

68. `wavee://playback-diagnostics` (developer mode ON) opens a page with a 22-DIP note glyph + title, a coloured
    status block, and Locate / Candidates / Verify / Playback modules / Updates cards at `FillLayerAlt` with
    132-DIP label columns. *Static capture.*
69. Candidate presence chips are green-background "✓ Spotify.dll" / red-background "✕ playplay-runtime.json".
    *Static capture, zoom.*
69a. Sign out (or run `--fake`) and open the page: the headline block turns Attention — "No playback session
    yet" — Locate / Candidates / Verify disappear, and **Current status, Playback modules, Updates, the three
    buttons and the closing caption all still render**. *Static capture.*
69b. Each absent section says so in prose: "No candidate locations were probed.", "Verification was never
    reached…", "This build has no playback-module host (the fake backend)." — never an empty card. *Static
    capture on `--fake`.*
69c. A `Faulted` **or `Crashed`** playback module shows a [Retry] button; `Ready` / `Starting` / `Stopped`
    do not. A module that has never started shows exactly its six identity rows (Id · Version + protocol ·
    Publisher · Directory · Process "not running" · Capabilities) and no Requests / Latency / Last error /
    Status row. *Two static captures.*
69d. With at least one refused module directory, the section ends with a "Refused" caption and a (dir, reason)
    pair per rejection; with none, that block is absent entirely. *Two static captures.*
70. With developer mode OFF, the same deep link does nothing and logs `deeplink.route.developerOnly`. *Log
    capture.*
71. `connect-diagnostics` shows five cards with 160-DIP label columns and millisecond-precision local
    timestamps; every unknown value renders "—". *Static capture.* **Reaching it in 0.2.9 requires a broken
    local-playback runtime** — the only link is in the Settings › Playback error InfoBar (`Playback.cs:162`), so
    capture this one on a machine where provisioning failed, or by forcing the error state.
71a. **Known 0.2.9 defect — must NOT be reproduced.** In 0.2.9: `wavee://open?route=connect-diagnostics` does
    nothing and logs `deeplink.route.unknown` (`WaveeShell.cs:1819-1823`); a History row for the page is dimmed
    and un-clickable (`HistoryPage.cs:414-418`); and the page's tab reads "Your Library" with a music-note glyph
    (`ShellNav.cs:50-79`). In 0.3 all three must be right: the deep link opens the page, the History row is live,
    and the tab reads "Connect diagnostics" with `Icons.Devices`. *Deep-link attempt + log capture, a History
    capture, and a tab-strip capture — run against 0.2.9 first to see all three failures, then against 0.3.*
71b. `connect-diagnostics` on a machine that has never sent a put-state still renders "Last put-state" and
    "Last put-state response" in full — six dashed rows each — while "Owner", "Last cluster" and "Host" each
    say their own absence in prose. *Static capture on `--fake`.*
~~72. The API console route is reachable ONLY with developer mode on, and its response pane is 240–420 DIP tall
    with its own scroll. *Static capture with a long response.*~~ — **struck, 2026-09-12 (plan §9.6 Q7): the API
    console is DELETED, not ported. Kept struck through, not removed, as the record of what 0.2.9 painted here.**
~~72a. Press Send: the button's own label becomes "Sending…" and it disables — there is no spinner anywhere on
    the page. With no live session the Base URL caption reads "(not connected)". *Two static captures.*~~ —
    **struck, 2026-09-12 (plan §9.6 Q7), same reason as 72.**
73. Turning on Developer mode + FPS overlay puts a small pill at the content's top-right, 104 DIP below the top
    and 14 DIP from the right, that does not intercept clicks (a card behind it still hovers). *Hover capture
    over the pill's position.*
74. The FPS overlay's numbers update while the app animates and settle to ~10 Hz at idle, with no measurable
    frame-rate cost. *Frame recording with the overlay on and off.*

**Glyphs and localisation**

75. Every row on General / Appearance / Playback / Storage has a glyph different from its section's glyph and
    from every sibling row's glyph **in that section**. Glyphs may and do repeat ACROSS sections — §0 N3 lists
    all **17** repeated names, `Document` appearing four times and `Picture` / `RowSize` / `Delete` three each.
    *Static captures of all four tabs; compare against `SettingsCatalog.Rows`.*
75a. Notifications and About are NOT catalog-driven: the Delivery section header, both of its first two rows and
    the Followers topic all legitimately carry `Icons.Bell`, and the Topics header carries the gear. That is
    0.2.9, not a bug to "fix" in 0.3. *Static capture of the Notifications tab.*
76. Two rows show the gear ON PURPOSE — General › Developer › "Developer mode" and Appearance › Now playing ›
    "Player style". Anywhere else, a gear means the fallback fired, which also writes `settings.glyph.unmapped`
    to the log. *Same captures + a log capture: the line must be absent.*
77. Switch the app language to Nederlands: the combo offers it but it is DISABLED (greyed, unselectable), as are
    Korean; System and English (United States) are selectable. *Static capture of the open combo.*
78. The Graphics adapter combo lists "Automatic (recommended)" plus one entry per adapter, and the adapter the
    app is currently rendering on carries an "(in use)" suffix. *Static capture of the open combo on a
    two-adapter machine.*
79. The FPS overlay's two numeric slots read literally `--` on the first painted frame, before the host writes
    a reading. *Frame capture of the first frame after toggling the overlay on.*

---

## 11. Audit log

Adversarial re-read of every assigned 0.2.9 source against this chapter (2026-09-12). One line per correction.

**wrong**

1. §0 N3 — **overclaim**: "Every row carries its own glyph" is true only for General / Appearance / Playback /
   Storage. `SettingsCatalog.cs:20-25` explicitly excludes Notifications (runtime-enumerated topics, literal
   `Icons.Bell` / `Icons.Settings` headers, `Icons.Bell` reused four times on that tab), About (literal
   `Icons.Info` / `Document` / `Refresh` / `Devices` / `Download` / `RadioTower` / `RefineSparkle` /
   `StatusWarning`) and Logs. Rewritten with the scope, the per-section (not global) invariant, and the two rows
   that render `Icons.Settings` deliberately (`developerMode`, `npvStyle`).
2. §3 / §5 — **wrong value**: the expander chevron rotates **0 ↔ 180°**, not 0 ↔ 90°. `ExpanderTemplateSettings.
   For(open) => new(open ? 180f : 0f, open)` (`Expander.cs:12-14`); the 90° rotation belongs to
   `SidebarChevron.Disclosure`, which is the LOG row's chevron. Fixed in both places.
3. §2 W21 — **wrong**: the wireframe drew a severity dot on the INFO and DEBUG rows. `SeverityDot`
   (`LogsPanel.cs:533-538`) emits a dot only for Warning and ≥ Error; everything else gets a blank 6 × 6 spacer.
   Wireframe corrected, and the rule spelled out under the column list.
4. §7 — **wrong**: "`provisioner == null` → … every section below it is omitted". Only Locate / Candidates /
   Verify sit behind `if (diag is { } d)` (`PlaybackRuntimeDiagnosticsPage.cs:48-53`); Current status, Playback
   modules, Updates, the three buttons and the caption always render. Corrected + a new W23 table of the five
   prose-absence states and two new parity items (69a, 69b).
5. §7 — **wrong**: the epoch row listed six epochs as "any row's live re-read". The page READS four
   (`_uiEpoch`, `PlayerBarPrefs`, `NpvPlayerPrefs`, `NotificationPrefs`) plus `prefs.Design`, and WRITES four it
   never reads (`AppearancePrefs`, `LyricsPrefs`, `DetailHeroPrefs`, `PlaybackPrefs`). Split the two lists.
6. §2 W1 — **wrong value**: `↕16` under the masthead. `Header()`'s bottom pad is `Spacing.M` = **12**
   (`SettingsPage.cs:242`); §3's token row already had it right. Wireframe and parity item 1 corrected.
7. §2 W16 — **wrong string**: the metadata stats line renders "N **extras**", not "N extensions"
   (`settings.storage.metadataCacheStats`). Noted with the other per-row description variants.
8. §10 item 53 — **wrong count**: the receipts card is **eight** labelled lines plus one unlabelled wrapping
   tertiary line, not nine labelled lines (`About.cs:289-297`). Corrected; the W19 sketch was already right.
9. §6.1 — **imprecise**: "Exactly one row … is clickable" is true, but it is an expander ITEM (`58,8,16,8`,
   `r 0`, MinH 52) gated on Wavee Curated being active (`Appearance.cs:629`), not a free-standing 68-DIP card.
   §6.1, W2 and W29 ② all corrected.
10. §2 W26 — **stale citation**: the FPS positioner is `WaveeApp.cs:429-437`, not `:423-430`/`:424-430`.

**missing**

11. §2 threshold table — `ContentAlignment.Vertical` forces the wrapped layout at EVERY width
    (`SettingsCard.cs:129`); `ContentAlignment.Left` bypasses header, glyph, chevron and both thresholds
    entirely (`:126-127, 281-292`); the first frame always builds the ≥ 476 shape off the 720-DIP fallback
    (`:322-339`). Added, plus parity items 11a / 11b.
12. §3 — the expander's OUTER chrome was undocumented: the engine `Expander` header paints `FillCardDefault` +
    a 1-DIP `StrokeCardDefault` border with top-only corners while open, and the body panel paints
    **`FillCardSecondary`** + a 1-DIP border with bottom-only corners and `Margin-top −1`
    (`Expander.cs:228-270`). Three token rows replaced one, and parity item 12a added.
13. §2 W8 — the EQ value pill's clamp rule (`px = clamp(x−52, 6, W−110)`, `py = clamp(y−42, 6, H−58)`), the
    `hot = ENABLED && …` gate (a disabled curve has no hot node), the disabled node's
    `GradientSpec.Solid(StrokeControlDefault)` / `TextDisabled` border, the signless "0 dB" format, and the
    `p is null` → `MinHeight 250` placeholder. Added, plus parity items 30a / 30b.
14. §2 W7 — the six EQ presets and their gain vectors (`Playback.cs:20-29`), the Curve item's two descriptions
    (on/off), the Ready expander's per-item `isEnabled` greying, the "View signature" gate
    (`IsWindows && File.Exists`), and both video quality ladders. Added, plus parity items 30c / 35c.
15. §2 W11 — the video-override row has **four** states; the chapter showed two. The no-curation-service state
    is a fully greyed row with **no control at all** (`:116-122`), and Remove raises a toast carrying an
    **Undo** action (`:235-250`). Added as a table, plus parity items 35a / 35b.
16. §2 W13 — the blocked banner is three distinct title/body pairs with a fourth "no banner" case, and the
    topics InfoBar is built with an EMPTY title (`Notifications.cs:50-51, 242-263`). Added as a table + parity
    item 40a. Topic order pinned to `NotificationPrefs.AllTopics`.
17. §2 W15/W16/W18 — the zero-total census renders `new BoxEl()` (no bar, no track, `Storage.cs:500`); the
    Logs / License-keys / Metadata / Resident descriptions each have 2–3 variants; the row size text is 13 DIP
    `TextSecondary` and is *absent* (not "—") while null. Added, plus parity items 44a / 44b.
18. §2 W19 — both hero links are conditional (`About.cs:435-447, 504-508`); the hero's middle column gap is 6,
    not 18. The `CrashReportsCard` row shape (timestamp `"g"` + [ Report ] [ Open ], and the empty row's
    blank-label item) was entirely undocumented. Added, plus parity items 53a / 53b.
19. §2 W21 — the filter row is `Wrap = true, MinHeight 40` (the panel's one reflow state); the overflow holds a
    separator between "Wrap long lines" and "Report this session…"; the 750 ms tail poll is
    `enabled: _session.Value == 0`, so a past session stops it dead. Added, plus parity items 67a–67c.
20. §2 W25 — the API console's body section has three arms; only "Entity lines" was drawn. The Text arm's
    gzip toggle and 920 × 140 editor added.
21. §2 W1 — the Graphics combo's "(in use)" suffix, the FPS-overlay row's `isEnabled: dev`, the Simulate row's
    `IsEnabled = nc is not null`, and the two `itemEnabled = false` language rows. Added, plus parity item 78.
22. §2 W2 — the Sidebar Design expander's one item exists only for Wavee Curated. Added, plus parity item 12b.
23. §6.2 — **no shortcut is bound by this surface**; the only chords with a face here are `Ctrl+K` (palette →
    Settings) and `Ctrl+± / Ctrl+0 / Ctrl+wheel` (the Zoom row reads `Viewport.Zoom` live). Stated.
24. §3 — token rows added for the level `Segmented` (34 / 52 / pad 2 / 14), the log `AutoSuggestBox` (MinH 34,
    empty suggestions), and the storage row size text.
25. §8 — `NotificationPolicy` (`CeilingFor` → two-vs-three segments, `IsScheduled` → the badge, the reconcile
    and the "Send event" copy) and `WaveeLogSessions` were both absent from the port table. Added with their
    existing tests. The `SettingsCatalog` row now states its four-tab scope and lists the six pinned facts.
26. §2 W26 — the FPS pill's slots are authored as the literal `"--"` (`FpsOverlay.cs:38, 41`). Added + parity
    item 79.

**verified, no change**

27. Every number in §2's threshold table, §3's token table and §8's line counts re-derived from source:
    `SettingsCard` 148/68/16/120/20/13/8/476/286/20/24/14/1/83 ✓ · `SettingsExpander` 32/52/(16,16,4,16)/
    (58,8,44,8)/(58,8,16,8)/0 ✓ · `SelectorBar` pill 16×3 r1, item pad (12,10,12,7), gap 8, focus −2 ✓ ·
    `WaveePicker` Tile 116×84 inset 8 gap 4, strip gap 12, label 12/16, focus +2, ink pairs ✓ ·
    density miniatures 40/48/56/64 × 0.25 = 10/12/14/16 and 32/32/40/48 × 0.25 = 8/8/10/12 ✓ ·
    `ZoomLadder.Steps` = 50·67·75·80·90·100·110·125·150·175·200·250 (12 rungs + the Auto head) ✓ ·
    EQ pads 40/16/18/34, plotH 289, zeroY 162.5, 96 samples, 5.5/2.5 strokes, 14→18 nodes, 104×28 pill r14 ✓ ·
    seven storage hues and their double use ✓ · `FmtBytes` ladder ✓ · budget preset ladder ✓ ·
    log pill 58×22 / 10 / 800 / @0.12 / @0.38, repeat badge (7,1,7,2) / 10.5 / 700, footer (16,8,12,8) ✓ ·
    `LogView.PageRows 500` / `MaxRows 2000` / five `LevelNames` ✓ · diagnostics label columns 132 (runtime) and
    160 (Connect) ✓ · all eight `SettingsShared.Confirm` call sites and their line numbers ✓ · the eight
    `AppUpdateState` pill strings ✓ · the seven tab labels and their order ✓.
28. §6.1's "no context menu, no double-click anywhere in this surface" — grep for `ContextMenu` /
    `OnDoubleClick` / `Shortcut` / `Accelerator` across all assigned files returns nothing. Confirmed.
29. §0 N12's "nothing reads a live clock per frame" — confirmed; no `Environment.TickCount64`, no
    `FrameTime`/`FrameDiagnostics` read in any render body of this surface.
30. §8's test inventory — all nine named test files exist in `src/apps/Wavee.Tests/`, and the five it asks
    0.3 to ADD (storage formatting, `BlockedBanner`, `BuildReport`, the receipt formatters) genuinely have no
    test today.

**critic-fixes** (completeness critic, 2026-09-12)

31. critic-fix: §1.1 / §2 W24 / §9 — **the Connect diagnostics page has no registered route**, and the chapter
    wireframed it as if it were an ordinary destination. Verified against 0.2.9: `ContentHost.PageFor` renders
    `connect-diagnostics` (`ContentHost.cs:244-246`) but the key is absent from `ShellRoutes.s_exact`
    (`ShellRoutes.cs:26-44`, whose own ownership note at `:18-20` calls such a key "a page the user can no longer
    deep-link to"), so the deep link is dropped with `deeplink.route.unknown` (`WaveeShell.cs:1819-1823`) and a
    History row for it is dimmed and inert (`HistoryPage.cs:414-418`). Added a reachability table to §1.1, a
    "getting here takes a broken install" note under W24, a new **§9.6** with the fix, and parity items 71 / 71a.
32. critic-fix: §9.6 — **two facts the critic's finding did not have, both verified.** (a) `ShellNav.Dest` has no
    arm for `connect-diagnostics` either (`ShellNav.cs:50-79`), so the tab/flyout label falls through to "Your
    Library" + `Icons.MusicNote`; (b) 0.2.9 leaves the page **one** entry point — the link in the Settings ›
    Playback *error* InfoBar (`Playback.cs:162`), composed only when the runtime is neither Ready nor
    NeverAttempted (`:116-143`) — and it is not pinnable (`SidebarPinId.cs:79`), so on a healthy install there is
    no door at all. Both recorded; 0.3 must also offer the page from the Ready runtime expander.
33. critic-fix — **correction to the finding as written**: "it will not survive a restored tab" is not true.
    `WorkspaceTabsPersistence.Decode` validates blankness and length only, never `IsKnown`
    (`WorkspaceTabsPersistence.cs:29-33`), so a restored `connect-diagnostics` tab does come back — mislabelled
    (fact (a) above), not missing. §9.6 states it that way. The history-row half of the finding is exact.
34. critic-fix: §9.6 / §9.3 item 3 — plan §4.11's single `RouteKind.Diagnostics` for both reports replaced with
    three distinct kinds (`PlaybackDiagnostics`, `ConnectDiagnostics`, `ApiConsole`) in **one** table carrying
    `(page, isKnown, title, glyph, developerOnly)`, which dissolves the two-lists drift by construction; the
    `developerOnly` column also absorbs today's `playback-diagnostics` special case at `WaveeShell.cs:1826`.
    No issue is filed for the defect (`gh issue list --state all --search connect-diagnostics` → none); §9.6 says
    to file one so the fix's CHANGELOG bullet can carry ` (#n)` as the repo's release gate requires.
    **Superseded later the same day by Q7 (plan §9.6): `ApiConsole` is struck from that table — the console is
    deleted, so §9.6 now carries TWO kinds, not three. `ConnectDiagnostics`'s issue is drafted, awaiting Christos's
    approval (Q6) — still no number.**

**adversarial-audit** (second independent re-read of all 19 assigned 0.2.9 sources + the engine controls they
sit in, 2026-09-12). One line per correction.

**wrong**

35. adversarial-fix: §0 N3 — **incomplete**, and the incompleteness was load-bearing. "Glyphs DO legitimately
    repeat across sections: `RowSize`, `RadioTower`, `Picture`, `Document`, `Delete`, `Clock`, `Album`" names
    **7 of 17**. Re-derived the complete set from `SettingsCatalog.cs:34-120` (section glyphs included) and
    wrote all seventeen out with their sites: `Document` ×4, `Picture` / `RowSize` / `Delete` ×3, `List` ×2,
    plus `Globe` · `Code` · `DockLeft` · `Album` · `MusicNote` · `Pin` · `ThisPc` · `Audio` · `Edit` ·
    `RadioTower` · `Clock` · `Settings`. A re-author working from the short list would have "fixed" ten
    legitimate repeats and broken the table.
36. adversarial-fix: §1.1 — **stale citation**: `_viewportLive` is `SettingsPage.Appearance.cs:51`, not `:48`
    (`:45,48` → `:45,51`).
37. adversarial-fix: §1.1 / §6.7 — **stale citation, NOT fixed by audit item 10 despite its claim**: the FPS
    positioner is `WaveeApp.cs:430-436` (the `hud` `BoxEl`), with the two-setting gate read at `:429`. §1.1 still
    said `:423-430` and §6.7 said `:425`. Both corrected, and the gate ("`DeveloperMode.Enabled &&
    DeveloperMode.FpsOverlay`, read inside Render — never an env var") recorded with it.
38. adversarial-fix: §0 N12 / §5 — **off-by-one**: `WaveeNowReceipts.TickMs` is `SettingsPage.About.cs:252`
    (`:251` is blank). Both citations corrected; §5's also now cites the `UseEffect(Tick)` at `:279`.
39. adversarial-fix: §2 W23 — **incomplete state**: the per-module Retry button appears for `Faulted` **or
    `Crashed`** (`PlaybackRuntimeDiagnosticsPage.cs:215-218`), not Faulted alone. Wireframe + prose corrected,
    plus parity item 69c.

**missing**

40. adversarial-add: §2 W2 — **`ZoomAutoMode` is a THREE-value enum and the combo has two shapes.** `zoomIndex`
    is `0` for **both Auto and Dense**; only `Manual` selects a ladder rung (`Appearance.cs:189-190, 203-205`),
    and picking the head item writes `ZoomMode = Auto` (never Dense) plus this render's `autoSuggested`
    (`:225-231`). The chapter modelled the row as auto-vs-manual. Added, plus parity item 19a.
41. adversarial-add: §2 W2 / §3 — the lyrics-blur slider's full spec (Min 0 · Max 100 · Step 1 ·
    **TickFrequency 25** · length 180 · tooltip `"N%"`) and the 12-DIP gap row that carries the conditional
    "Auto" link (`Appearance.cs:141-153`); "Auto" writes `-1`, not the resolved value (`:318-325`). §3's row
    had only "length 180".
42. adversarial-add: §2 W2 — **the Appearance tab writes through FIVE different epochs on purpose**
    (`AppearancePrefs` · `DetailHeroPrefs` · `LyricsPrefs` · `NpvPlayerPrefs` + the page's own `Bump()`), and
    the two Now-playing rows deliberately do **not** call `Bump()` at all. Collapsing them into one epoch would
    re-render every mounted surface on every settings write.
43. adversarial-add: §2 W3 — **the picker strip's wrap is the only reflow state the four picker groups have.**
    `WaveePicker.s_strip` overrides `PartGrid` with `Wrap = true, Gap = 12` and `PartColumn` with `Shrink = 0`
    (`WaveePicker.cs:247-254`); `maxColumns` defaults to `count` (`:271-286`). Cards never shrink, so the
    density strip goes 4 → 3 → 2 → 1 column as the card narrows (≈ 532 / 404 / 276 DIP) and the keyboard
    geometry follows. Added with the column-break numbers, plus parity items 14a / 14b.
44. adversarial-add: §2 W3 / §3 — the density miniature's actual bar ladder (art square r 4, a `Grow = 1`
    strong 2-DIP pill, then a fixed **24 × 2** and a fixed **16 × 2** faint pill; row gap 4, Pad (4,0,4,0),
    column gap 2, `Justify = Center`) — identical on all four cards, which is what makes the height difference
    legible — and the card's `ClipToBounds` (`WaveePicker.cs:62-72, 105-171`). Parity item 14 extended.
45. adversarial-add: §2 W7 — **the audio-quality ladder is THREE rungs and the omission is documented design.**
    `AudioQualityPreference.Lossless` exists in the enum and is deliberately not offered (`Playback.cs:310-312`);
    the metered combo carries the identical three labels + descriptions (`:349-360`). Added, plus parity items
    33a / 33b. Also recorded the runtime card's fourth INPUT (`svc is null` → `NotApplicable` → the same
    "not set up" bar, `:109`).
46. adversarial-add: §2 W14 — **two of the seven simulation toasts carry a clock**, with a `"--:--"` fallback
    (`Notifications.cs:129-137`); the "Send a test event" row's description differs for a scheduled topic and
    the row is enabled at every level **including Off** (`:101-110`). Added, plus parity items 40b / 40c.
47. adversarial-add: §2 W19 — **the hero's provenance line has three shapes**, including the never-checked
    "not checked yet" with no leading separator (`About.cs:540-548`), and the same string is reused as the Store
    build's pill so the two lines cannot disagree (`:572`). Added, plus parity item 50a.
48. adversarial-add: §2 W21 — **four actions collapse the expanded log row** (session effect `:75-81`, the
    session combo `:165`, Newest first `:203`, Group repeats `:206`); a session change also resets the window to
    500 rows (`:79`); search and level/category changes do **not** collapse it. And the expanded row is a keyed
    wrapper (`Key = "logs:row:" + Sequence`, Gap 4, Pad (0,0,8,8)) whose Fields / Exception blocks are each
    conditional while the meta line always renders (`:514-520`). Added, plus parity items 67d–67f.
49. adversarial-add: §2 W24 — **`Last put-state` and `Last put-state response` have no prose-absence state**:
    both cards are composed unconditionally (`ConnectDiagnosticsPage.cs:77-78`) and render six dashed rows on a
    machine that has never sent one. Recorded with the three `_tick` subscriptions and their
    `DepKey.FromRef(connect)` re-key (`:48-71`). Parity item 71b.
50. adversarial-add: §2 W23 — the modules block's **conditional rows** (Requests / Latency only with `Stats`,
    Last error only when non-empty, Status only with a status card, `:203-214`), the `Refused` sub-list's own
    gate and chrome (`:222-232`), and the `Row` → `"—"` rule shared by both diagnostics pages.
51. adversarial-add: §2 W25 — the API console's **in-flight state is the Send button itself** ("Sending…" +
    disabled, no spinner), its defaults (POST · Entity lines · the extended-metadata URL), the
    `(not connected)` Base-URL caption and the Headers auto-fill on entering Entity-lines mode
    (`ApiConsolePage.cs:22-34, 52, 57-59`). Added, plus parity item 72a.
52. adversarial-add: §6.1 — **"Manage" is a toggle, not an opener** (`VideoOverrides.cs:178-198`), the anchor
    lives on a wrapper `BoxEl` whose `OnRealized` satisfies a pending deep link (`:142-151, 210-216`), and both
    tab-leave and route-leave tear it down (`SettingsPage.cs:136`, `VideoOverrides.cs:58-71`). Parity item 35d.
53. adversarial-add: §7 — **the complete toast inventory for this surface** (every call site, with severities), the
    fact that "Delete old logs" toasts even when it deleted nothing and touches only rolled `wavee-*.log`
    (`Storage.cs:266, 270-277`), and that four storage mutations null `_storage` and re-enter the census. Added,
    plus parity items 48a / 48b.
54. adversarial-add: §3 — token rows for the section header's title/caption column (Gap 2, `AlignItems` Start
    vs Center), the picker card's `ClipToBounds`, the picker label's 1-line char-ellipsis, the EQ surface's
    `ZStack` + `ClipToBounds` (which is what makes the pill-clamp rule sufficient), and the density bar ladder.
55. adversarial-add: §10 — parity item 33c: flipping the EQ/Crossfade switch does **not** collapse an
    already-open expander (`InitiallyExpanded` is the one frozen field on `Expander`,
    `SettingsExpander.cs:184-193`); only a tab round trip re-reads it.

**verified, no change**

56. Re-derived independently and confirmed: `SettingsCard` 148 / 68 / 16 / 120 / 20 / 13 / 8 / **476** / **286** /
    20 / 24 / 14 / 1 / **83**, the `wrap = width < WrapThreshold || Alignment == Vertical` and
    `hideIcon = width < WrapNoIconThreshold && Alignment == Right` predicates, the `Left` bypass, and the
    **720** first-frame fallback with its 0.5-DIP deadband (`SettingsCard.cs:24-32, 126-138, 195-205, 322-339`) ✓ ·
    `SettingsExpander` 32 / 52 / (16,16,4,16) / (58,8,44,8) / (58,8,16,8) / r 0 / both thresholds 0, the chevron
    margin override (0,0,8,0) and the "divider between ItemsHeader and item 0, none above it" rule ✓ ·
    `Expander` 333 / 167 ms, **0 ↔ 180°**, top-only corners while mounted, `FillCardSecondary` body,
    `Margin-top −1` ✓ · `WaveeMotion.Faster = 83` and `ScaleSubtle = (1.02, 0.98)` ✓ ·
    `ProgressRing.DefaultSize = 32` ✓ · `Spacing.PageWide 36 / L 16 / M 12 / S 8 / XS 4 / XXS 2 / XXXL 32` ✓ ·
    density 40/48/56/64 × `PreviewScale 0.25` = 10/12/14/16 and art 32/32/40/48 × 0.25 = 8/8/10/12
    (`DetailTrackTableRules.cs:31, 45-57`, `TrackRow.cs:118, 124`) ✓ · EQ pads 40/16/18/34, node 14→18 /
    2.5→3, 96 samples, 5.5 / 2.5 strokes, pill 104 × 28 r 14, `px = clamp(x−52, 6, W−110)`,
    `py = clamp(y−42, 6, H−58)`, dense-label rule at plotW < 420, `"+0.#;-0.#;0"`, `_active` default **5**,
    the `p is null → MinHeight 250` placeholder ✓ · seven storage hues and their double use ✓ · `FmtBytes`
    ladder ✓ · budget preset ladder + `BodyBudgetIndex` default 5 ✓ · `MeteredVideoQualityIndex` default 1 ✓ ·
    log pill 58 × 22 / 10 / 800 / @0.12 / @0.38, repeat badge (7,1,7,2) / 10.5 / 700, footer (16,8,12,8),
    row columns 12 / 6 / 92 / 58 / 96, the blank-spacer severity-dot rule ✓ · `LogView.PageRows 500` /
    `MaxRows 2000` / five `LevelNames` ✓ · the four `Key`-remount sites at `LogsPanel.cs:165, 260, 264, 268` ✓ ·
    diagnostics label columns 132 / 160 ✓ · the eight `SettingsShared.Confirm` sites ✓ · both relocation dialogs
    and their default buttons ✓ · the ten `AppUpdateState` hero shapes ✓ · the eight notification topics, their
    glyphs, the `Icons.Bell` ×4 reuse and the `Icons.Moon` quiet row ✓ · `SettingsCatalogTests`' six facts ✓ ·
    `settings.storage.metadataCacheStats` really does say "**extras**" ✓ · `connect-diagnostics` absent from
    `ShellRoutes.s_exact` (`:26-44`, `api-console` at `:39`, `playback-diagnostics` at `:40`), rendered by
    `ContentHost.cs:244-246`, and with no `ShellNav.Dest` arm (`:50-79`) — §9.6 stands exactly as written ✓.
57. §6.1's "no context menu, no double-click, no accelerator anywhere in this surface" re-grepped across all 19
    assigned files plus `SettingsShared` / `WaveePicker` / `VideoOverrideManagerFlyout` — still nothing.
58. §0 N12 re-confirmed: no `Environment.TickCount64`, no per-frame `FrameDiagnostics` read; the three periodic
    reads are 5 000 ms (receipts), 750 ms (log tail, version-gated AND session-gated) and the host's own
    in-place `DynamicText` refresh.

**arbitration 2026-09-12:** two file rows this chapter owed and had not taken. **A14** — the lyrics inspector is a
**diagnostics** surface: `22-lyrics.md` §9 (d) moved `Features/Player/LyricsInspectorDialog.cs` (564, verified;
developer-mode only, composed at `RightRail.cs:382`) into `Screens/Diagnostics.UI.cs` and instructed this chapter to
carry the other half, which it never did. Added: a row in §9.5's unowned-types table, an entry in §1.2's
`Diagnostics.UI.cs` block, and the allocation stated where the budgets live — **`Diagnostics.UI.cs` is 2 300, not
1 700** (§9.3.1 and §9.3.2: 1 700 for the logs panel, the three pages and the shared chrome, plus ≈600 for the
inspector), with §9.4's honest estimate annotated 6 800 + ≈600 = ≈ 7 400. Owner **S** ports it; `22-lyrics.md`
§2 W17–W19b and §3 remain its specification. **A9** — `NotificationPrefs` is **not** `Screens/Settings.cs` CORE
(`Settings.Notify`): it belongs to the one notification stack in `Platform/Notify.cs` (owner I, Wave 4) with the
policy ladder, because the same class decides the bell’s count and the OS banner’s topic (chs. 19 and 14). §1.2 and
§8 now say so and mark Settings › Notifications a caller — its tab order is read from `Notify.Prefs.AllTopics`,
never re-listed. Nothing this chapter renders, measures or gates changed.

**answers 2026-09-12: Q6 and Q7 both land in this chapter.** Q6 — `connect-diagnostics`'s route fix (§9.6) still
needs its GitHub issue before it lands; the orchestrator has drafted one for Christos's approval, so it is recorded
as "issue drafted, awaiting approval — no number yet," and every `(#n)` placeholder for it stays a placeholder. Q7 —
the API console and its four `ApiDebug*` helpers are DELETED, not ported: §9.5's unowned-types row, §9.6's route
table, W25's wireframe and parity items 72/72a are struck through with this reason and this date rather than
removed, so the chapter still records what 0.2.9 painted here even though 0.3 does not port it.
