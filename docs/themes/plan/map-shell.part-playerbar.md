# Wavee shell map — player bar, rail, overlays, masthead, palette and related surfaces (main branch)

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
