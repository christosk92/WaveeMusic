# Wavee full-screen / now-playing family: map (main @ 21abd98e)

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
