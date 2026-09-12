# Player bar (now-playing block, transport, seek bar, volume, device picker, toggles) — 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Shell/PlayerBar.cs` (1548 lines), `src/apps/Wavee/Features/Shell/PlayerBarResponsiveLayout.cs` (171), `src/apps/Wavee/Features/Shell/SeekBar.cs` (511) = **2230 lines**; consulted: `App/DevicePickerModel.cs` (88), `App/LocalAudioDeviceService.cs` (162), `App/PlacementCore.cs`, `App/ShellUi.cs`, `App/PlaybackBridge.cs`, `Backend/Playback/{TimeFormat,LiveRail,LiveEdgeState}.cs`, `Features/Video/VideoPlacementMenu.cs` (77), `Components/FormatSplitButton.cs` (129), `Actions/PlayableLinks.cs`, `Design/{WaveeTokens,WaveeMotion,Surfaces,Glyphs}.cs` | 0.3 target (settled, plan §2): the named partial `Shell/+Shell.PlayerBar.UI.cs` (2,000 — the bar, the seek bar, the responsive tiers, the device picker menu and the device roster it reads, and the four rail-state toggles) + `Shell/Shell.cs` (CORE: the bar's tier + picker rules, and the `Shell.Ui` rail-state section the toggles write) + `Playback/Playback.cs` (CORE: `TimeFormat` / `LiveRail` / `LiveEdgeState`, owner G, Wave 3) | Wave 4 owner I (`Playback.cs`'s three rules are owner G, Wave 3)
>
> After Wave 0 every 0.2.9 path below lives at `src/apps/_old/Wavee/<same relative path>`.
> Cross-references: `00-design-system.md` (tokens, type ramp, motion curves, accent budget, zoom), `01-track-row.md` (track context menu, drag chip, `ArtistMoreButton`), `18-shell-frame.md` (window column, Mica/paint-site omission, the dock slot), `19-shell-overlays.md` (MenuFlyout chrome, toasts, tooltips), `21-right-rail-npv-queue-stage.md` (rail panels the bar's toggles open; `StageIdentity` re-uses `TimeText`/`SeekBar`), `23-deck-faces.md` (decks re-use `PlayerBarContent.TogglePlayPause`/`Fmt`), `24-video-surfaces.md` (placement ladder the split button opens).

---

## 0. The non-negotiables

1. **The dock is 72 DIP tall and paints NOTHING.** No fill, no shadow, no ring — it is a paint-site omission over live Mica; the only ink on its top edge is ONE 1-DIP hairline (`PlayerBar.cs:626-632`, `:646-660`, `WaveeSize.PlayerBarH = 72` in `Design/WaveeTokens.cs:56`). A plate, a border, or `Elevation.DockTop` under it is a regression.
2. **One centred row, three clusters, that degrade by DROPPING commands — never by shrinking buttons.** Every secondary transport is 32×32 with a 16-DIP glyph at *every* tier including the 300-DIP floor; the primary is 40/20 from Medium up and 36/18 below (`PlayerBarResponsiveLayout.cs:146-149`, pinned by `PlayerBarResponsiveLayoutTests.TransportMetrics_AreFlatAcrossTheLadder`).
3. **Identity survives to the floor.** Art (48 → 40), title, artist line and the heart are present at every one of the six tiers; `ShowSubtitle` and `ShowLikeSlot` are hard-coded `true` (`PlayerBarResponsiveLayout.cs:135,142`).
4. **The top edge is the global activity cue and it is SEPARATE from the seek bar.** At rest a 1-DIP hairline; while `IsLoading || IsBuffering || RecoveryKind==Network` it becomes a WinUI indeterminate `ProgressBar` sweeping across `max(2400, barWidth)` DIP, 2000 ms loop (`PlayerBar.cs:626-627`, `PlayerBarResponsiveLayout.cs:116,169`).
5. **The playhead is smooth and never snaps back under the finger.** Position is reported ~1 Hz; between ticks the fill is extrapolated on a pixel-due ticker and quantised to whole pixels, and while scrubbing the displayed fraction ignores the reported one (`SeekBar.cs:74-120`, `:373-411`). A 1 Hz stepping playhead or a thumb that jumps back mid-drag is the defect this design exists to prevent.
6. **Nothing hot re-renders the bar.** The seek fill/thumb are compositor binds on one `FloatSignal`; the volume rail is a signal-bound `Slider`; the two time labels are their own components with a *bound text channel*; the bar itself subscribes only to low-frequency signals (`PlayerBar.cs:24-27`, `SeekBar.cs:39-43`, `PlayerBar.cs:1172-1199`).
7. **The now-playing block is clickable, draggable and right-clickable.** Art → the playback *context*; title → the album; each artist name → its artist page; the cluster drags the current track; right-click opens the track menu. All resolve at INVOKE time via `Peek()`, never from a render-time capture (`PlayerBar.cs:239-255`, `:370-380`).
8. **Both now-playing lines scroll together, only on hover, and never on their own.** `Marquee.TriggerMode.Hover` + one shared `titleHover` gate + a 10 s capped cycle and a 2.5 s tail pause; at rest they are static with an edge fade (`PlayerBar.cs:92-93`, `:277-291`, `:319-328`). Auto-scrolling with the pointer elsewhere is the shipped-and-fixed bug (audit S2 #12).
9. **A latched toggle is a filled plate, not an ornament.** On-state = accent glyph over `Tok.FillSubtleSecondary`, hover `Tok.FillSubtleTertiary`, cross-faded over 83 ms; off-state is completely unpainted (`PlayerBar.cs:958-980`). No accent dot, no underline.
10. **"Playing on <device>" is one authority.** The remote line and the Devices button's lit state both come from `RemoteDevice(b)` — owner-derived `ActiveDeviceId` with **no** fallback to a device's own `IsActive` flag (`PlayerBar.cs:744-756`). `ActiveDeviceId` is `_ourDeviceId` for `OwnerKind.Us`, the foreign id for `Foreign`, and **`""` for `Nobody` — including `NobodyCause.FromForeign`** (`PlaybackProjection.cs:229-234`), so a departed device drops the line and unlatches the button even while its row is still in the roster.
    **CAVEAT (a real 0.2.9 inconsistency, not a thing to reproduce):** the *picker's* Connect row check is `id == activeConnectId || d.IsActive` (`DevicePickerModel.cs:78`) — the raw cluster flag is still a second opinion there. A roster row whose `IsActive` echo is stale therefore shows a radio check while the bar says nothing is playing remotely. 0.3 must drop the `|| d.IsActive` and check against the owner id alone, and `DevicePickerItemsTests` must gain the case that pins it.
11. **Live is a different rail, not a track with a weird duration.** DVR → the rail maps the seekable WINDOW with an accent live-edge tick; radio (no window) → the rail is replaced by a 2-DIP breathing line; the right slot carries the LIVE word-mark or "GO LIVE −m:ss" inside a fixed 104-DIP reservation so the swap moves nothing (`SeekBar.cs:216-233`, `:476-511`, `PlayerBar.cs:1250-1354`).
12. **The video split button reserves its width before it has anything to show.** The slot is mounted whenever a video *could* exist and is faded/inerted by `Opacity`+`HitTestVisible`, never by presence — an async `hasVideo` must not reflow the row (`PlayerBar.cs:526-533`, `:561-568`).
13. **Widening commits instantly; narrowing holds through a 24-DIP dip.** Structural controls may not chatter at a threshold during a pointer resize (`PlayerBarResponsiveLayout.cs:37-44`).
14. **Everything that leaves the row is reachable in the "⋯" overflow, in a fixed order, and the menu opens upward as a plain `MenuFlyout`** (never a `CommandBarFlyout` — its second clip fought the reveal) (`PlayerBar.cs:441-469`, `:989-992`, `:1087-1091`).
15. **The bar keeps the transport when a video plays in a pop-out window or an in-window card; it yields only to full-bleed fullscreen, where the shell unmounts it entirely** (`PlayerBar.cs:154-165`, `WaveeShell.cs:1344-1347`, `App/PlacementCore.cs:451-458`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
PlayerBar : Component                                              Features/Shell/PlayerBar.cs:30    breakpoint owner: reads Viewport.Size,
│                                                                                                    folds it to a PlayerBarLayout signal
├─ UseContextSignal(Viewport.Size)                                 PlayerBar.cs:36                    window width in DIP (= OS DPI x app zoom)
├─ UseSignal(PlayerBarLayout.Initial(w))                           PlayerBar.cs:38                    the coarse layout record
├─ UseSignalEffect -> PlayerBarLayout.Resolve(w, prev, init)       PlayerBar.cs:41-57                 hysteresis; logs playerbar.layout_band
└─ Embed.Comp(PlayerBarContent(layout))                            PlayerBar.cs:59                    layout passed as a SIGNAL (props freeze)

PlayerBarContent : Component                                       PlayerBar.cs:63
├─ [no bridge] BoxEl Height=72 — NOTHING else                       PlayerBar.cs:138-140               no top edge, no row; see W22
└─ BoxEl  root  Direction=1 Height=72 ClipToBounds IsolateLayout    PlayerBar.cs:646-660               layout firewall; NO Fill, NO shadow
   ├─ topEdge                                                      PlayerBar.cs:626-632
   │   ├─ [rest]    BoxEl Height=1 Fill=(dark: Tok.StrokeDividerDefault | light: #0F000000)
   │   └─ [busy]    ProgressBar.Indeterminate(L.TopEdgeWidth)      ..\fluent-gpu\src\FluentGpu.Controls\ProgressBar.cs:132  3 DIP band, 2 s loop
   └─ BoxEl  row  Key="player-row" Direction=0 Grow=1              PlayerBar.cs:639-644
      │           AlignItems=Center Gap=L.RowGap Padding=(L.RowPad,0,L.RowPad,0) Animate=MoveMotion
      │
      ├─ BoxEl  left  Key="left" Width=L.LeftW Shrink=0            PlayerBar.cs:363-375               drag source (current track)
      │   │      Direction=0 AlignItems=Center Gap=L.LeftGap ClipToBounds Animate=MoveMotion
      │   │      .WithContextMenu(Menus.NowPlaying)                PlayerBar.cs:378-380               -> 01-track-row.md (track menu)
      │   ├─ BoxEl  art  Key="art" W=H=L.ArtSize Shrink=0          PlayerBar.cs:344-352               Animate=ItemMotion; click -> context route
      │   │   └─ Surfaces.Artwork(track.Image, seed, size, size, 6f, scale: Viewport.Scale)   Design/Surfaces.cs:238
      │   ├─ BoxEl  metaCol  Key="meta" Direction=1 Grow=1 Basis=0  PlayerBar.cs:331-340              shared hover gate (titleHover)
      │   │   │      MinWidth=0 Shrink=1 Gap=2 Justify=Center ClipToBounds Animate=MoveMotion
      │   │   ├─ [remote only] BoxEl Key="remote-device-line" Animate=ItemMotion   PlayerBar.cs:307-312
      │   │   │   └─ RemoteDeviceLine : Component                  PlayerBar.cs:1416-1467             13 DIP; opens the device picker
      │   │   ├─ titleEl  Key="np-title"                           PlayerBar.cs:266-304
      │   │   │   ├─ [marquee on]  Marquee.Of(Prop.Of(NowPlaying(b).Title), Style{14/700,…}, scrollWhen: titleHover)
      │   │   │   └─ [marquee off] BoxEl ClipToBounds + TextEl 14/700 Trim=CharacterEllipsis
      │   │   └─ artists  Key="np-artists" [showSubtitle && track != null && err == null]   PlayerBar.cs:314-329
      │   │       ├─ [marquee on]  Marquee.Content(() => NowPlayingArtistLinks(), Style{…}, scrollWhen: titleHover)
      │   │       └─ [marquee off] BoxEl + NowPlayingArtistLinks(compact:true)  Key="npartists:c"
      │   │           NowPlayingArtistLinks : Component            PlayerBar.cs:850-893
      │   │           ├─ [no artists] BoxEl Direction=0 — EMPTY    PlayerBar.cs:861-862               the line still occupies Gap=2
      │   │           ├─ NavSpan(name) -> NowPlayingMetaLink       PlayerBar.cs:897-936               12 DIP, Secondary -> Primary on hover
      │   │           ├─ TextEl(", ") 12 Secondary                 PlayerBar.cs:839
      │   │           └─ [compact && artists.Count > 1] ArtistMoreButton("+N", shown:1)  Components/TrackRow.cs:20  -> 01-track-row.md
      │   │               (exactly ONE artist in compact mode -> the ordinary full list, no chip: PlayerBar.cs:864)
      │   └─ [showLike && active] Transport(Heart|HeartFill) Key="like" BlocksDragArm=true   PlayerBar.cs:354-361
      │
      ├─ BoxEl  centre  Key="centre" Grow=1 Shrink=1 MinWidth=0     PlayerBar.cs:424-438
      │   │      Direction=0 AlignItems=Center Justify=Start Gap=L.ClusterGap Animate=MoveMotion
      │   │      DropTarget = Drop.Target<WaveeResourceDragPayload>(Resource, Spotlight)  -> PLAY NEXT
      │   ├─ BoxEl  transport  Key="transport" Gap=0 Animate=MoveMotion    PlayerBar.cs:397-401
      │   │   ├─ [ownsTransport && showPrevNext] Transport(Icons.Previous) Key="prev"     PlayerBar.cs:385-387
      │   │   ├─ [ownsTransport]                 Primary(Play|Pause)       Key="primary"  PlayerBar.cs:388-392
      │   │   └─ [ownsTransport && showPrevNext] Transport(Icons.Next)     Key="next"     PlayerBar.cs:393-395
      │   └─ BoxEl  seekRow  Key="seek-row" Grow=1 Gap=L.SeekGap Animate=MoveMotion       PlayerBar.cs:415-419
      │       ├─ [showTimesElapsed]  BoxEl Key="elapsed"  -> TimeText(b, remaining:false) PlayerBar.cs:408-409
      │       ├─ BoxEl Key="seek" Grow=1 MinWidth=0       -> SeekBar(b)                   PlayerBar.cs:410-411
      │       └─ [showTimesRemaining] BoxEl Key="remaining" -> TimeText(b, remaining:true) PlayerBar.cs:412-413
      │
      └─ BoxEl  right  Key="right" Shrink=0 Justify=End Gap=L.RightGap Animate=MoveMotion PlayerBar.cs:609-614
          ├─ [showShuffleRepeat] Transport(Shuffle)  Key="shuffle"                        PlayerBar.cs:474-475
          ├─ [showShuffleRepeat] Transport(RepeatAll|RepeatOne) Key="repeat"              PlayerBar.cs:476-478
          ├─ [showVolumeButton]  BoxEl Key="volume" -> VolumeButton(popup:!showVolumeSlider)  PlayerBar.cs:482-491
          │                       inner Key = ("volume-inline-"|"volume-popup-")+box+"-"+glyph   PlayerBar.cs:489
          │                       VolumeButton : Component          PlayerBar.cs:1368-1414
          │                       └─ [popup] Overlay -> VolumePopup 52x168, vertical Slider 124x32  PlayerBar.cs:1527-1548
          ├─ [showVolumeSlider]  BoxEl Key="volume-slider" -> Slider.Create(b.Volume, len 96, thick 22)  PlayerBar.cs:492-498
          ├─ [showLyrics && active] Transport(WaveeIcons.Lyrics) Key="lyrics"             PlayerBar.cs:499-505
          ├─ [active && showQueue] BoxEl Key="video" Direction=0 Opacity=hasVideo?1:0     PlayerBar.cs:534-588
          │   ├─ ToolTip.Wrap(Transport(Icons.Movie, 32/16), "Switch to video|audio")
          │   └─ ToolTip.Wrap(Transport(ChevronDownSmall, 20 wide x 32 tall, glyph 10), "Where to play the video")
          │        -> Overlay MenuFlyout TopEdgeAlignedLeft = VideoPlacementMenu.Items    Features/Video/VideoPlacementMenu.cs:37
          ├─ [showQueue]   Transport(Icons.Queue)    Key="queue"                          PlayerBar.cs:596-599
          ├─ [showDevices] DevicesButton : Component Key="devices"                        PlayerBar.cs:600-601, :1477-1525
          │                 └─ Overlay MenuFlyout TopEdgeAlignedRight = DevicePickerMenu  PlayerBar.cs:1361-1366
          ├─ [showExpand]  Transport(Icons.ChevronUp) Key="expand"                        PlayerBar.cs:602-605
          └─ [overflow]    MoreButton -> PlayerMoreMenu Key="more#<hash>"                 PlayerBar.cs:606-607, :982-1105
                            shown when overflowCommands.Count > 0 || volumeInOverflow,
                            volumeInOverflow = !ShowVolumeButton && active                PlayerBar.cs:454, :606

overflowCommands, in BUILD order (this IS the menu order)                                 PlayerBar.cs:441-469, :591-595
  1 Previous / 2 Next      [ownsTransport && !ShowPrevNext]         Enabled = canTransport
  3 Shuffle / 4 Repeat     [!ShowShuffleRepeat]  ToggleButton       Enabled = canTransport
  (Like)                   [!showLike && active] — DEAD, see §9
  5 Lyrics                 [!ShowLyrics && ui != null && active]    ToggleButton
  6 Queue                  [!ShowQueue]                             Enabled = ui != null
  7 Now playing            [!ShowExpand]                            Enabled = ui != null
  8 Switch to video ▸      [!(active && ShowQueue) && active && hasVideo]  ToggleButton + Flyout
                            — unlike the inline slot, this row is NOT reserved: no video, no row
  9 Mute / Unmute          appended at OPEN time  [volumeInOverflow]  PlayerBar.cs:1077-1085

SeekBar : Component                                                Features/Shell/SeekBar.cs:44
└─ BoxEl root Grow=1 Height=32 Role=Slider  OnPointerDown/OnDrag/OnClick/OnDragCanceled   SeekBar.cs:291-307
   ├─ BoxEl stack ZStack Grow=1 Height=32                          SeekBar.cs:272-277
   │   ├─ BoxEl rail Height=4 r2 ClipToBounds ZStack                SeekBar.cs:235-245
   │   │   ├─ BoxEl fill Grow=1 Height=4 r0 TransformOriginX=0 Transform=Scale(displayFrac,1)  SeekBar.cs:198-210
   │   │   └─ [dvr] edge tick: 2x4 accent, right-aligned            SeekBar.cs:216-231
   │   └─ BoxEl thumb 22x22 r10 Opacity=0 HoverOpacity=1 Transform=Translate(x,0)          SeekBar.cs:258-270
   │       └─ BoxEl inner dot 12x12 r6 Scale 0.86 (hover x1.357 / press x0.826)            SeekBar.cs:247-256
   ├─ [canAdvance] SeekTicker (UseInterval at the pixel dwell, clamp(span/railPx, 33, 250) ms;
   │               100 ms fallback while span or width is unknown)                          SeekBar.cs:286-288, :425-451
   │               canAdvance = track != null && err == null && !IsLoading && playing && !IsBuffering
   └─ [live, no window] LiveLine : Component — 2-DIP accent rule, opacity 0.55<->1 @3 s     SeekBar.cs:282, :476-511

TimeText : Component                                               PlayerBar.cs:1139-1228
├─ [track]  BoxEl W=44 MinW=44  Caption(bound Prop) Secondary; right slot toggles -remaining/duration
│           elapsed slot Justify=End (digits hug the rail), remaining slot Justify=Start   PlayerBar.cs:1214
└─ [live]   LiveSlot W=104      -> LivePill ("LIVE" word-mark + 6-DIP red dot) | GoLiveButton ("GO LIVE −m:ss")
                                                                   PlayerBar.cs:1250-1354
```

### 1.2 The same tree in 0.3 terms — `Shell/+Shell.PlayerBar.UI.cs`

`Component` props freeze at mount, so the column marked **live** names the ONLY channel by which data reaches that node.

| 0.3 node | Kind | Inputs | How data stays live |
|---|---|---|---|
| `Shell.PlayerBar()` | `sealed class PlayerBar : Component` | none | reads `Viewport.Size` context signal; owns `Signal<PlayerBarLayout>` |
| `Shell.PlayerBarContent(IReadSignal<PlayerBarLayout>)` | Component | the layout **signal** | `_layout.Value` read in `Render` — never the struct by value |
| `Shell.NowPlayingCluster(in PlayerBarLayout L)` | static `Element` | `L` by ref | rebuilt by the parent's render; children keyed (`"art"`, `"meta"`, `"like"`) |
| `Shell.NowPlayingTitle()` | static `Element` (Marquee) | `Prop.Of(() => …)` thunk | **thunk**, read inside — a frozen string would stick at mount (`PlayerBar.cs:224-228`) |
| `Shell.NowPlayingArtists()` | Component | none (reads `Playback.Current` + `Edges.TrackArtists`) | signal read inside `Render` |
| `Shell.RemoteDeviceLine()` | Component | none | reads the device signals in `Render`; **`Key="remote-device-line"`** so the keyed insert cannot shift the title slot |
| `Shell.SeekBar()` | Component (stateful) | none | `enabled` **derived in Render** from signals, never a ctor flag (`SeekBar.cs:126-131`); fill/thumb are compositor binds on its own `FloatSignal` |
| `Shell.SeekTicker{Owner}` | Component | `Owner` field | mounted/unmounted by the parent on the play/pause edge |
| `Shell.TimeText(bool remaining, ColorF? ink)` | Component | `remaining`, `ink` (structural, fixed for life) | the digits are a bound `Prop<string>`; the show-remaining preference is read **inside the thunk** |
| `Shell.VolumeButton(bool popup, float box, float glyph)` | Component | frozen ctor args | parent supplies `Key = "volume-" + popup + box + glyph` so a tier change **remounts** it (`PlayerBar.cs:489`) |
| `Shell.DevicesButton(float box, float glyph, DevicePickerScope)` | Component | frozen ctor args | accent + roster + active id read in `Render` (never ctor args) (`PlayerBar.cs:1478-1481`) |
| `Shell.PlayerMoreMenu(commands, includeVolume, box, glyph)` | Component | frozen list | parent computes a content hash and passes it as `Key` — a changed command set **remounts** (`PlayerBar.cs:993-1022`) |
| `Shell.VolumePopup()` | Component | none | signal-bound vertical slider |
| `Shell.LiveLine()` | Component | none | `UseKeyframes` re-seeded by `DepKey.From(breathe)` |

CORE (pure, `Shell/Shell.cs`): `PlayerBarTier`, `PlayerBarResponsiveLayout`, `PlayerBarLayout`, `DevicePickerRow(Kind)`, `DevicePickerModel`. CORE (pure, `Playback/Playback.cs`): `TimeFormat`, `LiveRail`, `LiveEdgeState`. CORE (pure, `Shell/Video.cs`, A13, owner K): `PlacementCore`/`TransportOwner`.

---

## 2. Wireframes

Scale: **8 DIP per monospace character** unless a wireframe says otherwise. `≈≈` inside the seek rail marks an elision; the rail's true DIP width is annotated. Every width arithmetic below is
`centre = viewportW − 2·RowPad − LeftW − 2·RowGap − rightW`, `rightW = Σ(children) + (n−1)·RightGap`, `seek = centre − transportW − ClusterGap − (times + gaps)`.

Tier thresholds (`PlayerBarResponsiveLayout.cs:22-27`, pinned by `PlayerBarResponsiveLayoutTests.cs:8-20`):
`Minimal < 440 ≤ Compact < 760 ≤ Medium < 900 ≤ Comfortable < 1100 ≤ Wide < 1240 ≤ Full`.
Hysteresis: widening commits at the threshold; narrowing keeps the current tier until `width + 24 < threshold` (`:37-44`).

---

### W1 — Full, playing, all commands inline @ 1440 DIP

`right = 32+32+32+96+32+52+32+32+32 = 372, +8·2 = 388` · `centre = 1440−24−260−16−388 = 752` · `transport = 32+40+32 = 104` · `seekRow = 752−104−4 = 644` · `seek rail = 644−44−44−12 = 544`
*(no "⋯" at Full: every command is inline, `overflowCommands.Count == 0`)*

```
╞══════════════════════════════════════════ 1-DIP hairline, full bleed (Tok.StrokeDividerDefault / #0F000000) ══════════════════════════════════════════╡
│ 12 │<──────────────── left 260 ────────────────>│8│<──────────────────── centre 752 ────────────────────>│8│<────────── right 388 ──────────>│ 12 │
│    ┌────────┐                                    ┃                                                       ┃                                        │
│    │ cover  │  Song title that is long enough…   ┃  ⏮   ▶/⏸   ⏭   1:07 ├──────●≈≈≈≈≈≈≈──────┤ -2:34        ┃  🔀  🔁  🔊 ├────●────┤  ♪  🎬▾  ☰  🖳  ⌃ │
│    │  48    │  Artist One, Artist Two        ♥   ┃  32   40   32   44        seek 544         44          ┃  32  32  32     96      32  52  32 32 32│
│    └────────┘                                    ┃  └── gap 0 ──┘ ↔4↔ ↔6↔              ↔6↔               ┃  ↔2↔ between every right-cluster child   │
│     ↔8↔  meta 164 (title 14/700 · gap 2 · artists 12/16)                                                  ┃                                        │
```

Row is 71 DIP tall (72 − the 1-DIP top edge), `AlignItems=Center`. Art radius 6. Heart 32×32 at the left cluster's trailing edge.

---

### W2 — Wide @ 1150 DIP (expand/chevron falls into "⋯")

`ShowExpand=false` → the only overflow command is **Now playing**, so "⋯" appears (32). `right = 32+32+32+96+32+52+32+32+32 = 372, +16 = 388` · `centre = 1150−24−240−16−388 = 482` · `seekRow = 374` · `seek rail = 374−88−12 = 274`

```
╞════════════════════════════════════════ hairline ════════════════════════════════════════╡
│12│<────────── left 240 ─────────>│8│<────────── centre 482 ─────────>│8│<── right 388 ──>│12│
│  ┌──────┐                         ┃                                   ┃                     │
│  │ 48   │ Title…                  ┃ ⏮  ▶  ⏭  0:42 ├────●─────┤ -3:12  ┃ 🔀 🔁 🔊├──96──┤ ♪ 🎬▾ ☰ 🖳 ⋯│
│  │      │ Artist                ♥ ┃                 seek 274          ┃                     │
│  └──────┘                         ┃                                   ┃                     │
```

---

### W3 — Comfortable @ 950 DIP (queue + video + expand in "⋯"; volume becomes a popup glyph)

`ShowQueue=false`, `ShowVolumeSlider=false` → volume button is the **popup** form; the video slot is gone too (it rides `showQueue`) and falls into "⋯" only when `hasVideo`.
**Comfortable is NOT `wide`**, so it takes the *Medium* spacing rungs — `RowPad 8`, `RowGap 6`, `ClusterGap 4`, `SeekGap 6`, `RightGap 2` (`PlayerBarResponsiveLayout.cs:163-168`; `wide = tier >= Wide`).
`right = 32(shuffle)+32(repeat)+32(volume)+32(lyrics)+32(devices)+32(⋯) = 192, +5·2 = 202` · `centre = 950−16−230−12−202 = 490` · `transport = 104` · `seekRow = 490−104−4 = 382` · `seek rail = 382−44−44−12 = 282`

```
╞════════════════════════════════ hairline ════════════════════════════════╡
│8│<───── left 230 ─────>│6│<───────── centre 490 ────────>│6│<right 202>│8│
│ ┌──────┐                ┃                                ┃              │
│ │ 48   │ Title…         ┃ ⏮  ▶  ⏭  0:42├────●────┤ -3:12  ┃ 🔀 🔁 🔊 ♪ 🖳 ⋯│
│ │      │ 🖳 Playing on … ┃            seek 282            ┃              │
│ │      │ Artist       ♥ ┃                                 ┃              │
│ └──────┘                ┃                                 ┃              │
```

The remote line appears **only** from Comfortable up (`ShowRemoteDeviceLine: comfortable`, `PlayerBarResponsiveLayout.cs:138`) and only when a foreign device owns playback; it sits ABOVE the title inside `metaCol` (13 DIP, `Gap=2`).

---

### W4 — Medium @ 800 DIP (shuffle/repeat gone; both time labels stay)

`right = 32(volume)+32(lyrics)+32(devices)+32(⋯) = 128, +3·2 = 134` · `centre = 800−16−220−12−134 = 418` · `seekRow = 310` · `seek rail = 210`

```
╞══════════════════════════ hairline ══════════════════════════╡
│8│<──── left 220 ───>│6│<────── centre 418 ─────>│6│<r 134>│8│
│ ┌──────┐             ┃                          ┃          │
│ │ 48   │ Title…      ┃ ⏮  ▶  ⏭ 0:42├───●──┤-3:12 ┃ 🔊 ♪ 🖳 ⋯ │
│ │      │ Artist    ♥ ┃           seek 210       ┃          │
│ └──────┘             ┃                          ┃          │
```

---

### W5 — Compact @ 600 DIP (no prev/next inline, elapsed gone, primary drops to 36/18)

`right = 32(devices)+32(⋯) = 64, +1·1 = 65` · `centre = 600−16−172−8−65 = 339` · `transport = 36` · `seekRow = 339−36−3 = 300` · `seek rail = 300−44−5 = 251`

```
╞═══════════════ hairline ═══════════════╡
│8│<── left 172 ──>│4│<─ centre 339 ─>│4│<65>│8│
│ ┌────┐            ┃                  ┃      │
│ │ 40 │ Title…     ┃  ▶  ├──●───┤-3:12┃ 🖳 ⋯ │
│ │    │ Artist   ♥ ┃ 36   seek 251    ┃      │
│ └────┘            ┃                  ┃      │
```

---

### W6 — Minimal @ 360 DIP (and the 300-DIP floor)

`right = 32+32 = 64, gap 0` · `centre = 360−12−132−6−64 = 146` · `seek rail = 146−36−2 = 108`.
At the 300-DIP floor the same arithmetic leaves **48 DIP** of rail — the documented trade (`PlayerBarResponsiveLayout.cs:57-61`, pinned by `MinimalTier_LeavesTheSeekBarRoomAtThe300DipFloor`).

```
╞════════ hairline ════════╡
│6│<─left 132─>│3│<c 146>│3│<64>│6│
│ ┌────┐        ┃        ┃      │
│ │ 40 │ Title… ┃ ▶ ├──┤ ┃ 🖳 ⋯ │
│ │    │ Art… ♥ ┃36 s108 ┃      │
│ └────┘        ┃        ┃      │
```

---

### W7 — Idle / no track (`PlayerState.NoTrack`) @ 1440

Title = `player.nothingPlaying` in `Tok.TextSecondary`; **no** artists line (`track != null` gate, `PlayerBar.cs:314`); **no** heart (`showLike = ShowLikeSlot && active`, `:188`); art tile = the opaque placeholder (no image); prev/next/primary **disabled** (`Tok.TextDisabled`, `canTransport=false`, `primaryEnabled=false`); seek rail painted with `RailFillDisabled` and a fill scaled to ~0 (`SeekBar.cs:182-186, 205-209`); Devices and the toggles stay live.

```
╞══════════════════════════════════════ hairline ══════════════════════════════════════╡
│    ┌────────┐                                ┃                                       ┃
│    │ blank  │  Nothing playing   (Secondary) ┃ ⏮  ▶  ⏭  0:00 ├─ empty rail ─┤ 0:00    ┃ 🔀 🔁 🔊├──┤ ☰ 🖳 ⌃│
│    │  48    │                                ┃ (all three dimmed to TextDisabled)     ┃
│    └────────┘                                ┃                                       ┃
```
*(Lyrics and the video slot are absent — both require `active`.)*

---

### W8 — Loading / buffering / reconnecting: the top edge sweeps

```
╞═══════ 3-DIP indeterminate band (accent) — two indicators, 40 % and 60 % of max(2400, barW), 2 s loop ═══════╡
   ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  →  sweeps left→right, easing KeySpline(0.4,0,0.6,1)
│  row content unchanged, but 2 DIP shorter (row = 72 − 3) and therefore 1 DIP higher on its centre line
```
While `IsLoading` the title shows the resolved title if there is one, else `player.loading`; the primary is **disabled** during Loading but stays enabled while merely Buffering (`PlayerBar.cs:174-177`).

---

### W9 — Error

Title = the bridge's error string (or `player.cannotPlay`), coloured `Critical = rgba(0.93,0.42,0.45,1)` (`PlayerBar.cs:89`, `:737`); the artists line is suppressed (`err == null` gate); the primary shows **Play** and is **enabled** — clicking it invokes the retry action (`PlayerBar.cs:390`, `:699-704`).

```
│  ┌──────┐                                  ┃
│  │ 48   │ Can't play this track  (#ED6B73) ┃  ⏮  ▶  ⏭   …
│  │      │ (no artists line)                ┃      ↑ enabled = retry
```

---

### W10 — Reconnecting (`RecoveryKind == Network`)

Title = `player.reconnecting` ("Connection lost — reconnecting…") in `Tok.TextSecondary`; artists line still shown (track non-null, err null); `canTransport` stays **true**; the top edge sweeps (W8).

---

### W11 — Playing on another device @ 1240 (Comfortable+)

```
│  ┌──────┐                               ┃
│  │ 48   │ 🖳 Playing on Living Room TV   ┃   ← 13 DIP line, accent@88 %, 12/16 weight 600, ellipsised
│  │      │ Song title                    ┃      click anywhere on it = open the device picker
│  │      │ Artist                      ♥ ┃
│  └──────┘                               ┃   right cluster: the 🖳 Devices button is LATCHED
                                               (accent glyph over Tok.FillSubtleSecondary)
```
The transport keeps working (commands are forwarded to the remote device); only the *look* changes.

---

### W12 — Live with a DVR window (`SeekRailMode.Dvr`), behind the edge

```
│ 1:04:22 ├────────────────●───────────────┤▌  [GO LIVE −1:20]
│  ↑ elapsed since TUNE-IN (hours rung; slot grows past 44)   ↑ 104-DIP reserved slot, right-aligned
│                    rail maps the WINDOW ────────────────┘▌ = 2-DIP accent live-edge tick
```
At the edge the same slot shows the LIVE mark instead, and the rail **snaps full** (`LiveRail.DisplayFrac`, `SeekBar.cs:89-94`):

```
│ 0:41:05 ├████████████████████████████████┤▌  [• LIVE]
                                               ↑ 6-DIP red dot (Tok.SystemFillCritical) + 14-DIP r2 1-px accent-bordered word-mark, 9/12/600
```

---

### W13 — Live radio, nothing to rewind (`SeekRailMode.Line`)

```
│ 0:12:33  ════════════════════════════════════  [• LIVE]
│           ↑ 2-DIP full-width accent rule, r1, breathing opacity 0.55↔1 over 3 s;
│             HitTestVisible=false, Role=None — it is not a slider
```

---

### W14 — Seek: hover, and drag in progress

```
rest      ├──────────────────────────────────────┤        thumb Opacity 0
hover     ├───────────●══════════════════════════┤        thumb fades in (22 ring + 12 dot at 0.86)
                      ◯ 22-DIP ring, 1-px ControlElevationBorder, ControlSolid fill
press     ├───────────◉══════════════════════════┤        inner dot 0.86→0.71 (250 ms), value fill → AccentTertiary
drag      ├──────────────────────●═══════════════┤        fill follows the FINGER; the 1 Hz tick is ignored (scrub gate)
```
There is **no hover time bubble on the seek bar in 0.2.9** — `SeekBar` is bespoke and wires no `ToolTip`; the only slider tooltip in this surface is the volume rail's percentage bubble (§6). Do not invent one.

---

### W15 — Volume: inline rail (Wide+) vs popup (Medium/Comfortable)

```
Wide+     🔊 ├──────●──────┤        glyph 32/16 (click = MUTE toggle) + Slider 96 long x 22 thick,
                96 DIP              rail 4 DIP r2, thumb ring 22, inner dot 12 @0.86; tooltip "72%"
                                    (converter: `$"{clamp(round(v*100),0,100)}%"` — NO space, PlayerBar.cs:107)

Medium    🔊                        click opens a popup ABOVE the glyph (FlyoutPlacement.TopCenter,
           ┌────┐                   PopupChrome.Popup): 52 x 168 box, padding 10/14,
           │ ▲  │                   vertical Slider length 124, thickness 32, NO value tooltip
           │ █  │
           │ █  │
           └────┘
Compact-   ⋯ menu carries "Mute" / "Unmute" instead (built at open time from the live signals) —
           only while `active`: an idle bar below Medium has NO volume affordance at all
           (`volumeInOverflow = !ShowVolumeButton && active`, PlayerBar.cs:454)
```

---

### W16 — Device picker open (`FlyoutPlacement.TopEdgeAlignedRight`, light-dismiss, focus trap)

```
                       ┌─────────────────────────────────┐
                       │ This computer            (dim)  │  Header row = a DISABLED command row
                       │ ● 🖳 System default             │  radio, checked when we are the output & nothing picked
                       │ ○ 🔈 Speakers (Realtek)         │
                       │ ○ 🎧 WH-1000XM5                 │
                       │ ───────────────────────────────  │  Separator
                       │ Spotify Connect          (dim)  │
                       │ ○ 📱 iPhone                     │
                       │ ● 📺 Living Room TV             │  checked = the ACTIVE connect device
                       └─────────────────────────────────┘
                                              ▲ anchored on the 🖳 button, opens UPWARD
empty Connect roster (the --fake case):
                       │ Spotify Connect          (dim)  │
                       │ No devices found         (dim)  │
                       │ Open Spotify on another device  │
unsupported local playback: every "This computer" row is DISABLED and carries the accelerator text "Unavailable"
a Connect device owns playback: NO "This computer" row is checked — every local check is
    `localSupported && weAreActiveOutput && …` (DevicePickerModel.cs:61,65), and `weAreActiveOutput`
    is `RemoteDevice(b) is null` (PlayerBar.cs:770)
LONG roster: the flyout is a plain MenuFlyout with NO max height and NO scroll cap — 20 Connect
    devices is a 20-row menu; see §9 "Traps"
```
The Connect glyphs come from `DeviceGlyph` (`PlayerBar.cs:807-813`) and the local ones from `LocalGlyph` (`:798-804`).
`LocalGlyph`'s `Hdmi → Icons.TvMonitor` branch is **unreachable from this picker**: `FilterForPicker` drops every
`DigitalAudioDisplayDevice` endpoint before the model sees it (`LocalAudioDeviceService.cs:94`). Keep the mapping (other
callers may not filter) but do not draw a TV glyph in a "This computer" mock.

---

### W17 — The "⋯" overflow at Compact (order is fixed)

```
            ┌──────────────────────────────┐
            │ ⏮  Previous                  │   only when prev/next are not inline
            │ ⏭  Next                      │
            │ 🔀 Shuffle            [✓]    │   ToggleMenuFlyoutItem (E73E check column)
            │ 🔁 Repeat             [✓]    │
            │ ♪  Lyrics             [ ]    │
            │ ☰  Queue                     │
            │ ⌃  Now playing               │
            │ 🎬 Switch to video       ▸   │   cascading submenu = VideoPlacementMenu.Items
            │ 🔊 Mute                      │   appended at OPEN time when the volume is in overflow
            └──────────────────────────────┘
                                    ▲ anchored on ⋯, TopEdgeAlignedRight
```

---

### W18 — Video split button and its placement menu

```
right cluster:   … ♪ │🎬│▾│ ☰ 🖳 ⌃ …        primary 32x32 (movie glyph, LATCHED while video is live anywhere)
                       └─┴ 20 wide x 32 tall, glyph 10 — the ONE documented exception to the 32-DIP floor

                 ┌────────────────────────────────────────────┐
                 │ ⊞ ● Dock in rail       Needs a wider window │  radio-checked against the resolved placement;
                 │ ⧉ ○ Play in a mini player                  │  icons: SplitView · BackToWindow · Movie ·
                 │ 🎬 ○ Play in a separate window              │  FullScreen · Cancel (VideoPlacementMenu.cs:53-73)
                 │        Not available on this display setup │  disabled rows keep their REASON in the
                 │ ⛶ ○ Full screen                        F11 │  accelerator column (a disabled row cannot
                 │ ────────────────────────────────────────── │  carry a tooltip); "mini player" has NO reason
                 │ ☑ Always on top                            │  string — when disallowed it is simply dim
                 │ ────────────────────────────────────────── │
                 │ ✕ Turn off video                           │
                 └────────────────────────────────────────────┘
                   ▲ TopEdgeAlignedLeft, anchored on the PAIR
Exactly four rungs at most: the F11 row is present only because the bar passes `includeFullscreen: true`
(PlayerBar.cs:550), and "F11" shows only while that rung is ALLOWED — otherwise the reason replaces it.
The "Always on top" toggle AND its separator exist only while the RESOLVED placement is `Detached`
(VideoPlacementMenu.cs:63-69); "Turn off video" AND its separator only while `PlacementCore.IsActive` (:70-74).
So the resting menu on a fresh, video-off track is FOUR rows and no separators.
when hasVideo == false: the whole 52-DIP slot stays mounted at Opacity 0, HitTestVisible false — no reflow when it resolves
```

---

### W19 — A resource dragged over the transport (drop = PLAY NEXT)

```
page content behind: dimmed by the engine's spotlight scrim (black @ 0.55 group alpha, rounded cutouts),
CLIPPED to the content region — the player bar and title bar stay fully lit (WaveeShell.PublishScrimClip)

│  ┌──────┐                    ┃╔══════════════════════════════════════════╗┃
│  │ 48   │ Title…             ┃║ ⏮  ▶  ⏭   0:42 ├───●────┤ -3:12          ║┃   drag chip caption: "Play next"
│  │      │ Artist           ♥ ┃╚══════════════════════════════════════════╝┃   refusal (artist payload): "Can't add an artist"
```

---

### W20 — Marquee on hover / compact artists

```
idle       Title that is longer than the column…          ← static, RIGHT edge fade only (24-DIP band, smoothstep)
hovered    …tle that is longer than the column, cont…      ← BOTH lines scroll together (PingPong), both edges fade
           ↑ pointer anywhere over metaCol (title link recolours to Tok.AccentTextPrimary)

marquee disabled (appearance.marquee.enabled = false):
           Title that is longer than the colu…            ← CharacterEllipsis
           Artist One  (+2)                               ← first artist (trimmed) + the "+N" chip, ONLY when
                                                            artists.Count > 1; Gap 4 between them
                        └─ click → MenuFlyout of EVERY credited artist, BottomEdgeAlignedLeft
                           (TrackRow.cs:49) — from the dock that is DOWNWARD off the window, so the
                           overlay service re-fits it upward; 0.3 should anchor it TopEdgeAlignedLeft here.

no artists at all (a module playable with no credits, a local file with none):
           Title
           ␣                                              ← NowPlayingArtistLinks renders an EMPTY row
                                                            (PlayerBar.cs:861-862); the 2-DIP Gap still applies,
                                                            so the title sits ~7 DIP above the block's centre.
                                                            It is NOT the same as the err/NoTrack suppression.
```

---

### W21 — Transport not owned (`ownsTransport == false`)

The centre renders an empty transport group and an empty seek row (`PlayerBar.cs:385-413`). **Unreachable in the main window today**: the only non-owning placement is `Fullscreen`, and `WaveeShell` unmounts the whole bar for the duration (`WaveeShell.cs:1344-1347`). Keep the guard; it is the structural half of the single-transport rule.

Note the overflow follows: `!showPrevNext` still pushes Previous/Next into "⋯" **only when `ownsTransport`** (`PlayerBar.cs:442`), so a non-owning bar's "⋯" carries no transport verbs either.

---

### W22 — No bridge (`PlaybackBridge.Slot` unresolved)

```
╞═════════════════════ nothing at all — not even the hairline ═════════════════════╡
│                          an empty 72-DIP band over Mica                          │
```
`PlayerBar.cs:138-140` returns `new BoxEl { Height = WaveeSize.PlayerBarH }` — **no top edge, no row, no clip**. This is
the pre-provider frame in a harness/test host, and the only state in which the dock's seam is missing. 0.3 must keep the
height (the shell reserves it) and should keep the hairline too, so a bridge-less frame is not visually seamless.

---

### W23 — A mosaic cover (Liked Songs / a generated collection)

`Surfaces.Artwork` short-circuits before the image path: `≥ 4` `MosaicTiles` → a 2×2 `Mosaic` in the 48/40 tile; `1–3`
tiles → the first tile drawn as a single cover (`Surfaces.cs:240-245`). The radius, the size and the placeholder ramp are
unchanged; nothing else in the bar branches on it. Do not draw a mosaic *frame* or a count badge.

---

## 3. Tokens

| element | size (DIP) | padding / gap | radius | type style | colour / brush token | material / elevation | source |
|---|---|---|---|---|---|---|---|
| dock root | H 72, W = window | — | 0 | — | **none** (paint-site omission over Mica) | none — explicitly no `Elevation.DockTop` | `WaveeTokens.cs:56`; `PlayerBar.cs:646-660` |
| top edge, rest | H 1, full bleed | — | 0 | — | dark `Tok.StrokeDividerDefault` (#15FFFFFF) · light literal `#0F000000` | — | `PlayerBar.cs:628-632` |
| top edge, busy | H 3, W `max(2400, barW)` | — | 1.5 (indicator) / 0.5 (track) | — | `Tok.AccentDefault`; track hidden (Opacity 0) | — | `ProgressBar.cs:36-44`; `PlayerBarResponsiveLayout.cs:116,169` |
| row | H 71 (72−1) | padding `(RowPad,0,RowPad,0)`, gap `RowGap` | — | — | — | — | `PlayerBar.cs:639-644` |
| RowPad / RowGap | Wide+ 12 / 8 · Medium 8 / 6 · Compact 8 / 4 · Minimal 6 / 3 | — | — | — | — | — | `PlayerBarResponsiveLayout.cs:163-164` |
| ClusterGap / LeftGap / SeekGap / RightGap | Medium+ 4 / 8 / 6 / 2 · Compact 3 / 6 / 5 / 1 · Minimal 2 / 4 / 4 / 0 | — | — | — | — | — | `PlayerBarResponsiveLayout.cs:165-168` |
| left cluster | W = `LeftW`: 260 / 240 / 230 / 220 / 172 / 132 | gap `LeftGap` | 0 | — | — | — | `PlayerBarResponsiveLayout.cs:153-161` |
| cover | 48 (Medium+) / 40 | — | **6** | — | image; placeholder = `WatchedPlaceholder(url)` (opaque, cover-graded) | static tile — below `ShimmerMinEdge` 80, so **no breathe** | `PlayerBar.cs:344-352`; `Surfaces.cs:207-219,238` |
| meta column | W = `LeftW − art − heart − 2·LeftGap` (164 / 144 / 134 / 124 / **88** / 52) | gap 2, `Justify=Center` | — | — | — | — | `PlayerBar.cs:331-340` |
| title | — | — | — | **14 / weight 700** | `Tok.TextPrimary`; link-hover `Tok.AccentTextPrimary`; idle+reconnecting `Tok.TextSecondary`; error `#ED6B73` | — | `PlayerBar.cs:270-282`, `:89`, `:732-739` |
| artist name | — | `Gap 4` (compact), ", " separators | — | **12** (`Ui.Caption` metrics 12/16) | `Tok.TextSecondary` → `Tok.TextPrimary` on hover | — | `PlayerBar.cs:839`, `:931-932` |
| "+N" chip | — | padding `(2,0,2,0)` | — | Caption 12 weight 600 | `Tok.TextTertiary` | — | `Components/TrackRow.cs:54-63` |
| remote device line | H **13** | gap 4 | — | 12 / 16 / weight 600 | `Tok.AccentDefault` @ A=0.88 → full accent on hover | — | `PlayerBar.cs:1442-1464` |
| heart | 32 × 32, glyph 16 | — | 4 (`Radii.Control`) | — | off `Tok.TextSecondary`; on `Tok.AccentDefault` over `Tok.FillSubtleSecondary` | — | `PlayerBar.cs:354-361`, `:958-980` |
| secondary transport (prev/next/shuffle/repeat/lyrics/queue/devices/expand/volume/⋯) | **32 × 32, glyph 16** at every tier | — | 4 | — | rest `Tok.TextSecondary` → hover `Tok.TextPrimary`; disabled `Tok.TextDisabled`; latched accent glyph over `Tok.FillSubtleSecondary` / hover `…Tertiary` | none (unpainted at rest) | `PlayerBarResponsiveLayout.cs:103-104,146-147`; `PlayerBar.cs:958-980` |
| primary play/pause | **40 × 40, glyph 20** (Medium+) / **36 × 36, glyph 18** | — | — (transparent) | — | glyph `Tok.TextPrimary`; pressed `Tok.TextSecondary`; disabled `Tok.TextDisabled`; **no plate** | none | `PlayerBarResponsiveLayout.cs:105-108,148-149`; `PlayerBar.cs:1109-1126` |
| video split chevron | **20 wide × 32 tall, glyph 10** | — | 4 | — | as secondary transport, never latched | — | `PlayerBar.cs:98`, `:583-584` |
| seek hit row | Grow, H **32** (`SliderHorizontalHeight`) | — | — | — | — | — | `SeekBar.cs:46`, `:291-293` |
| seek rail | H **4**, Grow | — | **2** | — | `Slider.DefaultStyle.RailFill` = `Tok.FillControlStrong`; disabled `…StrongDisabled` | — | `Slider.cs:60-63,93-94`; `SeekBar.cs:235-245` |
| seek value fill | H 4, scaled from the left | — | **0** (square — a scaled rounded rect shows cap slivers) | — | `Tok.AccentDefault` → hover `AccentSecondary` → pressed `AccentTertiary`; disabled `AccentDisabled` | — | `SeekBar.cs:198-210`, `:182-188` |
| DVR live-edge tick | 2 × 4 | right-aligned in the rail | 0 | — | `Tok.AccentDefault` | — | `SeekBar.cs:216-231` |
| seek thumb ring | **22 × 22** | — | **10** | — | `Tok.FillControlSolid`, 1-px `Tok.ControlElevationBorder`; Opacity 0 → 1 on hover | — | `Slider.cs:70-79`; `SeekBar.cs:258-270` |
| seek inner dot | **12 × 12**, scale 0.86 rest | — | 6 | — | `Tok.AccentDefault` / Secondary / Tertiary / Disabled | — | `Slider.cs:71,81-84`; `SeekBar.cs:247-256` |
| live line (radio) | Grow × **2** | — | 1 | — | `Tok.AccentDefault` | — | `SeekBar.cs:47`, `:501-507` |
| time label | W **44** (MinWidth 44; unbounded while live) | `Justify=End` (elapsed) / `Start` (remaining) | 4 (hover plate) | Caption 12/16 secondary | `Tok.TextSecondary`; hover plate `Tok.FillSubtleSecondary`, pressed `…Tertiary` (right slot only) | — | `PlayerBar.cs:1208-1227`, `:1214` |
| live slot | W **104**, right-aligned | — | — | — | — | — | `PlayerBar.cs:1255`, `:1272` |
| LIVE word-mark | H 14, dot 6 × 6 | padding `(2,0,2,0)`, gap 2 | 2 (box) / 3 (dot) | 9 / 12 / weight 600 | border + text `WaveeAccent.Decor` (= `Tok.AccentTextPrimary`); dot `Tok.SystemFillCritical` | — | `PlayerBar.cs:1319-1354` |
| GO LIVE button | H 20, MinWidth 44 | padding `(4,0,4,0)` | 4 | 9 / 12 / weight 600 | text `WaveeAccent.Decor`; hover `Tok.FillSubtleSecondary`, pressed `…Tertiary` | — | `PlayerBar.cs:1282-1309` |
| volume rail (inline) | 96 long × 22 thick | — | 2 (rail) | — | stock `Slider.DefaultStyle` | — | `PlayerBar.cs:104`, `:496-497` |
| volume popup | 52 × 168 | padding `(10,14,10,14)` | popup chrome | — | `PopupChrome.Popup` (engine flyout surface) | flyout elevation (ch. 19) | `PlayerBar.cs:1536-1546` |
| menus (device picker, ⋯, video) | — | — | — | — | stock `MenuFlyout` | `MenuPopupThemeTransition` clip-reveal | `PlayerBar.cs:1087-1091`, `:551-555` |

Spacing constants: `Spacing.XXS = 2`, `Spacing.XS = 4` (`..\fluent-gpu\src\FluentGpu.Engine\Dsl\Spacing.cs:11-12`); `Radii.Control = 4` (`Radii.cs:11`).

### 3.1 Glyph codes (Segoe Fluent unless noted)

Every glyph the bar can draw, so a 0.3 mock cannot invent a lookalike. Sources: `..\fluent-gpu\src\FluentGpu.Controls\Icons.cs` (generated table) and `src/apps/Wavee/Design/Glyphs.cs` for the app font.

| slot | glyph | code | notes |
|---|---|---|---|
| previous / next | `Icons.Previous` / `Icons.Next` | E892 / E893 | 16 DIP |
| primary | `Icons.Play` / `Icons.Pause` | E768 / E769 | 20 (Medium+) / 18; **Play** also in `Error` (retry) |
| shuffle | `Icons.Shuffle` | E8B1 | latched when `IsShuffle` |
| repeat | `Icons.RepeatAll` / `Icons.RepeatOne` | E8EE / E8ED | glyph swaps at `RepeatMode.Track`; latched for any non-Off |
| like | `Icons.Heart` / `Icons.HeartFill` | EB51 / EB52 | latched (accent + plate) while saved |
| volume | `Icons.Volume` / `Icons.Mute` | E767 / E74F | mute glyph when `OutputMuted \|\| Volume ≤ 0.001` (`PlayerBar.cs:1383`) |
| lyrics | `WaveeIcons.Lyrics` | E902, **`WaveeIcons.Font`** | app font — not Segoe (`Glyphs.cs:14`) |
| queue | `Icons.Queue` | E93C | |
| devices | `Icons.Devices` | E772 | also the remote line's 12-DIP leading glyph |
| expand / now playing | `Icons.ChevronUp` | E70E | |
| overflow | `Icons.More` | E712 | |
| video primary | `Icons.Movie` | E8B2 | latched whenever video is live anywhere |
| video chevron | `Icons.ChevronDownSmall` | E96E | 10 DIP, never latched |
| picker · local | `Icons.Speakers` / `Icons.Headphones` / `Icons.TvMonitor` / `Icons.ThisPc` | E7F5 / E7F6 / E7F4 / E977 | TvMonitor unreachable — see W16 |
| picker · Connect | `Icons.CellPhone` / `Icons.Speakers` / `Icons.TvMonitor` / `Icons.ThisPc` | E8EA / E7F5 / E7F4 / E977 | Phone / Speaker / Tv / everything else |
| video menu | `Icons.SplitView` / `Icons.BackToWindow` / `Icons.Movie` / `Icons.FullScreen` / `Icons.Cancel` | — | `VideoPlacementMenu.cs:53-73` |
| menu check column | the engine's `ToggleMenuFlyoutItem` check | E73E | shuffle/repeat/lyrics/video/always-on-top rows |

---

## 4. Colour & material

| what | input → function | where applied | transition |
|---|---|---|---|
| dock body | *nothing* — the shell root paints nothing and the window carries live DWM Mica | the whole 72-DIP band | n/a; a page tint composites underneath via `ShellMaterialLayer` (ch. 18) |
| top seam, dark | `Tok.StrokeDividerDefault` (#15FFFFFF — white alpha, so Mica reads through) | 1-DIP top edge | swaps instantly with the theme (`Prop.Of` re-evaluates on `Tok.Epoch`) |
| top seam, light | **literal** `ColorF.FromRgba(0,0,0,0x0F)` — deliberately *not* `Tok.StrokeCardDefault`, which every seeded light preset resolves to an OPAQUE grey | 1-DIP top edge | as above (`PlayerBar.cs:619-631`) |
| cover placeholder | `url → CoverColorPlane.Current.Watch(url) → Surfaces.PlaceholderFor(url)` | the 48/40 art tile behind the image | the image cross-fades in over it when decoded; the tile is **opaque** so the desktop never reads through (`Surfaces.cs:107-111,207-219`) |
| cover decode | `decodePx = ImageDecodeScale.For(max(w,h), Viewport.Scale)` where `Viewport.Scale = OS DPI × app zoom` | the art `ImageEl` | re-decodes on a zoom/monitor change (`PlayerBar.cs:123-126`, `Surfaces.cs:258-266`) |
| latched toggle plate | `Tok.FillSubtleSecondary` (rest) / `Tok.FillSubtleTertiary` (hover) | shuffle, repeat, lyrics, queue, devices, expand, heart, video primary | `BrushTransitionMs = 83` cross-fade on the toggle edge (`PlayerBar.cs:966-973`) |
| accent | `Tok.AccentDefault` (the app palette's accent; Wavee ships the neutral palette — the cover palette is **not** read by this surface) | value fill, thumb dot, latched glyphs, remote line, live tick | re-renders on `Tok.Epoch` via `RethemeAll`, then cross-fades per `BrushTransitionMs` where set |
| accent as CONTENT | `WaveeAccent.Decor` = `Tok.AccentTextPrimary` (contrast-corrected ink) | LIVE word-mark, GO LIVE text, hovered title link | instant |
| error ink | app-local literal `ColorF(0.93,0.42,0.45,1)` ≈ `#ED6B73` | the title while `PlayerState.Error` | instant (a `Prop`-bound colour) |
| live dot | `Tok.SystemFillCritical` — the app's ONE red, **static** (a blinking dot would keep the frame loop awake forever) | the 6-DIP dot beside LIVE | none, by design |
| on-media variant | `TimeText(ink:)` / `GoLiveButton(ink:)` / `LivePill(ink:)` take an ink override; the immersive stage passes `StageInk.InkTertiary` and glass hover/press (`WaveeOnMedia.GlassHover/GlassPressed`) | the same components mounted on the stage (ch. 21) | n/a here — the bar always passes `null` (token ink) |
| drag scrim | `DragVisualTok.ScrimColor` black @ `ScrimOpacity 0.55` group alpha, rounded cutouts per destination | the CONTENT region only — the bar and title bar are outside the published scrim clip | fades with the drag (engine) |

Light/dark differences are limited to: the top seam (above), the subtle-fill ramp flipping black↔white alpha, and `Tok.TextPrimary/Secondary/Disabled`. There are no bar-local light/dark branches beyond `PlayerBar.cs:631`.

---

## 5. Motion

All motion below samples the engine's frame clock (`FrameTime` / `AnimScheduler`) **except** the two `Environment.TickCount64` uses in `SeekBar` called out in the last two rows — those are the position-interpolation anchor, not an animation clock, and 0.3 must replace them (see §9).

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| breakpoint change (tier crosses) | `left`, `centre`, `right`, `row`, `meta`, `transport`, `seek-row`, `primary`, `seek` | Bounds (FLIP) | old → new rect | **167 ms** (`Motion.ControlFast`) | `FluentPopOpen` = cubic-bezier(0,0,0,1) | none | engine `ReducedSnap` parks at end | `PlayerBar.cs:71-74` |
| a command enters/leaves the row | `art`, `like`, `prev`, `next`, `shuffle`, `repeat`, `volume`, `volume-slider`, `lyrics`, `video`, `queue`, `expand`, `more`, `elapsed`, `remaining`, `remote-device-line` | Bounds + Opacity, `SizeMode.Reflow` | enter 0 → 1 opacity **and** width 0 → natural (neighbours slide over) | enter **250 ms** (`ControlNormal`), exit **167 ms** | `SmoothOut` = cubic-bezier(0.22,1,0.36,1) | none | opacity still cross-fades; motion parks | `PlayerBar.cs:80-86` |
| hover a transport button | the button box | composited scale | 1 → **1.07** | 83 ms (`InteractionAnim.ControlFasterMs`) | `FluentPopOpen` | — | `ScaleTier.Hover` returns **1f** → transform skipped | `PlayerBar.cs:974`; `WaveeMotion.cs:48,179` |
| press a transport button | the button box | composited scale | 1 → **0.92** | 83 ms | `FluentPopOpen` | — | 1f (skipped) | same |
| toggle latches (shuffle on, repeat, like, lyrics, queue, devices, expand, video) | button `Fill` | brush | transparent ↔ `FillSubtleSecondary` | **83 ms** | linear-light cross-fade | — | unaffected (a colour, not motion) | `PlayerBar.cs:973` |
| like edge: **same track**, unsaved → saved | the heart node | Opacity 0→1, ScaleX/Y **0.25→1**, BlurSigma **2→0** | — | **250 ms** (`Expressive.Fast`) | `EaseInOut` | none | `IconSwapIn` returns immediately — no animation at all | `PlayerBar.cs:199-206`; `MotionRecipes.cs:92-99` |
| loading / buffering / reconnecting | top edge indicators | TranslateX | indicator1 start→end over 0–75 % then hold; indicator2 holds 0–37.5 % then start→end | **2000 ms**, looping | KeySpline(0.4,0,0.6,1) | the two indicators are phase-offset by design | **NOT honoured** — `ProgressBar` seeds through `AnimScheduler.Keyframes` (raw), which has no reduced-motion gate; only `KeyframesMotion` does (`AnimScheduler.Timeline.cs:61-70`). A reduced-motion user still gets a sweeping top edge. 0.3 should route it through a motion token or flatten the track. | `ProgressBar.cs:46-49,181-232`; `ProgressBar.cs:27-29` (MinHeight 3, TrackHeight 1) |
| hover the meta column | both now-playing lines | TranslateX | 0 → −(overflow) → 0 (PingPong) | one traversal = `min(CycleMs 10000, distance/18 px·s⁻¹ ×1000)`; tail pause **2500 ms**; start delay 350 ms | engine marquee ramp | both lines share ONE gate, so they stay phase-locked | engine marquee honours reduced motion | `PlayerBar.cs:92-93,277-291,319-328`; `Marquee.cs:35-47` |
| marquee edge fade | the marquee viewport | `EdgeFade` | right band at rest; both bands while scrolling | — | smoothstep | band = `min(24, 0.3 × viewportW)` | unaffected | `Marquee.cs:117-122,291-307` |
| playing | seek fill + thumb | `_displayFrac` → `Transform` (Scale / Translate) | previous → interpolated position | one write per **pixel dwell** = `clamp(durationMs / railPx, 33, 250)` ms | none (a plain value set — no tween) | — | unaffected (it is the playhead) | `SeekBar.cs:107-120,425-435,441-451` |
| hover the seek row | thumb | Opacity | 0 → 1 (only when enabled) | 83 ms default | `FluentPopOpen` | — | opacity, not motion | `SeekBar.cs:265` |
| hover the seek row | inner dot | scale | 0.86 → 0.86 × 1.3570 (= 1.167 absolute) | **250 ms** | `FluentPopOpen` | — | `HoverScale` is a raw float here (not a `ScaleTier`) — it still animates under reduced motion (known residual) | `SeekBar.cs:190,253-254` |
| press the seek row | inner dot | scale | 0.86 → 0.86 × 0.8256 (= 0.71 absolute) | **250 ms** | `FluentPopOpen` | — | as above | `SeekBar.cs:191,254` |
| live radio playing **and** window active **and** motion not reduced | `LiveLine` | Opacity keyframes | 0.55 → 1 → 0.55 | **3000 ms**, looping | keyframe-linear | — | the looping track is **replaced in place** by a finite flat track (opacity → 1) so the loop count falls to zero and the frame loop quiesces | `SeekBar.cs:476-511` |
| flyout open (device picker, ⋯, video placement, volume popup) | the popup | engine `MenuPopupThemeTransition` clip-reveal, growing from the anchor edge | — | engine | engine | — | engine | `PlayerBar.cs:989-992,1087-1091` |
| hover the right-hand time label | the label plate | Fill | transparent → `FillSubtleSecondary` | default | — | — | colour | `PlayerBar.cs:1216-1217` |
| hover GO LIVE | plate + scale | Fill + scale 1 → 1.02 / 0.98 | — | 83 ms | `FluentPopOpen` | — | `ScaleSubtle` → 1f | `PlayerBar.cs:1293-1296` |
| hover / press the VOLUME rail (inline **and** popup) | its inner dot | scale | 0.86 → 1.167 / 0.71 | **250 ms** | `FluentPopOpen` | — | raw floats from `Slider.DefaultStyle`, same residual as the seek dot | `Slider.cs:83-86`; `PlayerBar.cs:496-497,1542-1544` |
| hover the ART / TITLE / an ARTIST name | the glyph or text ink only | Color | Secondary → Primary (artist), token → `AccentTextPrimary` (title) | `BrushTransitionMs` default | — | — | colour, unaffected | `PlayerBar.cs:273,281,931-932` |
| hover the REMOTE DEVICE line | glyph + text ink | Color | accent @ A=0.88 → full accent | default | — | — | colour | `PlayerBar.cs:1451,1459` |
| the "⋯" command set changes (a tier crossing, a toggle flip, a sub-flyout row changing shape) | `PlayerMoreMenu` | **remount** | — | instant | — | — | n/a | `PlayerBar.cs:993-1022` — a hash `Key`, not an animation; the mounted menu never cross-fades |

**Clock exceptions to fix in 0.3** — `SeekBar` anchors its interpolation on `Environment.TickCount64` (`SeekBar.cs:65,86,161-174,407`). The 0.3 plan already publishes `PosMs + (now − PosQpc)` (`wavee-0.3-implementation.md` §4.7), so the port must use the QPC sample (`PlaybackBridge.LastPositionSample` is the 0.2.9 equivalent, already QPC-stamped) and delete the TickCount64 anchor.

---

## 6. Interaction

**Now-playing cluster**
- Art tile: `Cursor.Hand` + `Role=Hyperlink` when a route exists; click → `PlayableLinks.RouteFor(track, LinkSlot.Art) ?? RichText.RouteForUri(b.CurrentContext)` — the playback CONTEXT, resolved with `Peek()` at click time (`PlayerBar.cs:239-245,824-825`).
- Title: click → `LinkSlot.Title` (the album, or a module's own page). Hover recolours to `Tok.AccentTextPrimary` **and** starts both marquees. `Role=Hyperlink`, focusable. The click target is the marquee viewport itself — no wrapper (`PlayerBar.cs:292-304`).
- Artists: each name is its own link (`"artist:" + uri`); a module playable collapses the whole line to ONE publisher link; a name with no route is inert (not styled-and-dead) (`PlayerBar.cs:830-845`, `Actions/PlayableLinks.cs:44-58`).
- Hover anywhere on `metaCol` sets `titleHover` (scroll both lines); `OnPointerExit` clears it.
- **Drag**: the whole left cluster is `Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForTrack(b.CurrentTrack.Peek()))` — the payload factory peeks at promotion time. `Draggable` is **`null` when `track is null`** (`PlayerBar.cs:370`), so an idle bar is not a drag handle at all. The heart sets `BlocksDragArm = true` so a like-press never lifts the track (`PlayerBar.cs:368-373,361`).
- **Right-click / Menu key / long-press** on the cluster: `Menus.NowPlaying(acts, currentTrack)` = `Menus.Tracks` over `ActionTarget.ForNowPlaying` (`Actions/ActionTarget.cs:69-70`), i.e. the standard track menu with no `Host` (no Remove rows). It opens with the shared **`ContextMenuHeader`** — cover art, the title, and `"<artists> · <album>"` as the subtitle (`Actions/Menus.cs:1115-1125`) — then the strip **[Play · Play next · Add to queue · Save]**, then rows *Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸ · Credits · Song radio*. Exact rows are ch. 01's contract; the header is not optional here.
- The context menu is wired only when **both** `ActionServices` and the `Overlay.Service` resolve (`PlayerBar.cs:378`); otherwise the cluster has no right-click menu at all.

**Transport**
- Primary: Play/Pause toggle; in `Error` it is a **retry** (`InvokePlaybackErrorAction`); in `NoTrack`/`Loading` it is disabled and inert (`PlayerBar.cs:699-704`).
- All intents are **optimistic**: the signal is written first, then the async command is issued — `TogglePlayPause`, `ToggleShuffle`, `CycleRepeat` (`PlayerBar.cs:687-714`). Repeat cycles Off → Context → Track → Off.
- Every transport button sets `AllowFocusOnInteraction = false`: clicking does **not** move focus, so the global Space shortcut keeps working after a click (`PlayerBar.cs:975`).
- Keyboard: **Space** = play/pause anywhere in the shell unless focus is a text editor — `AutomationRole.Text` or a char-handling node (`WaveeShell.cs:2087`, `:2133-2147`); it routes to the **same** `PlayerBarContent.TogglePlayPause`. Bare Space is deliberately *not* an accelerator (the dispatcher only matches Ctrl/Alt or F-keys), so it is handled in `OnShellKey`. The command palette exposes `playback.playPause / next / previous / shuffle / repeat` (`WaveeCommands.cs:202-206`). **F11** is a bare shell accelerator (`VideoFullscreenChord`, `WaveeShell.cs:2041`, handler at `:895`) — it toggles video fullscreen. Tab reaches every focusable button; the engine's focus rectangle is the shared visual (ch. 00).

**Seek bar**
- Click-anywhere: `OnPointerDown` jumps to the press point and opens the scrub gate; `OnDrag` follows the finger (unclamped local X, clamped here); `OnClick` is the **drag-end commit** — exactly one `SeekAsync` per gesture; `OnDragCanceled` releases the gate and re-derives (`SeekBar.cs:373-417`).
- A DVR commit maps through `LiveRail.Seek(start, end, frac)`, clamped INTO the window at both ends; an ordinary commit is `clamp(frac × duration)` (`SeekBar.cs:395-411`).
- `enabled` = `CurrentTrack != null && Error == null && !IsLoading && CanSeek`; when disabled the handlers are not wired and `Cursor` is null (`SeekBar.cs:131,295-305`).
- `Role = AutomationRole.Slider`; the radio LiveLine deliberately sets `Role = None` and `HitTestVisible = false` (`SeekBar.cs:496-498`).
- **No tooltip / no time bubble** on the seek bar (verified: `SeekBar.cs` contains no `ToolTip` call).

**Time labels**
- Left label = elapsed (or elapsed-since-tune-in while live). Right label toggles **−remaining ↔ duration** on click; the choice persists to `playerbar.duration.remaining` (default **true**) and bumps `PlayerBarPrefs.Epoch` so the Settings toggle and the stage's label stay in sync (`PlayerBar.cs:1200-1207`, `Platform/AppSettings.cs:58`).
- `−0:00` is never shown: the minus sign belongs only to an actual remainder (`PlayerBar.cs:1196-1198`).
- **GO LIVE** click = `b.GoLive()` — a committed seek to `LiveWindow.LiveEdgeMs` that also resets the hysteresis machine to AtEdge immediately (`PlaybackBridge.cs:1732-1743`).

**Volume**
- Wide+: the glyph click toggles **mute** (`VolumeButton.ToggleMute`: with a real output device it mutes the Windows session; on the fake path it is a software 0 ⇄ 0.7 toggle). The inline rail drags the value; its thumb tooltip shows `"{0..100}%"` (`PlayerBar.cs:105-108,1398-1413`).
- Medium/Comfortable: the glyph opens the vertical popup (`FlyoutPlacement.TopCenter`, focus trap, light dismiss), whose slider has **no** value tooltip.
- Below Medium: the ⋯ menu carries Mute/Unmute, labelled from the live state read at OPEN time (`PlayerBar.cs:1077-1085`).
- The glyph is `Icons.Mute` when `OutputMuted || Volume ≤ 0.001`, else `Icons.Volume` (`PlayerBar.cs:1383`).
- The slider value is the **linear slider position** everywhere (wire, settings, UI); the cubic taper is applied only at the amplitude boundary (`docs/plans/wavee/volume-curve-and-putstate-plan.md` §1.2-1.4, `Backend/Audio/VolumeTaper.cs`).

**Device picker**
- Opens upward, `TopEdgeAlignedRight`, focus trap + light dismiss, `ConstrainToRootBounds = false`. It re-renders **while open** (the body is a component reading the bridge signals, not an open-time snapshot) (`PlayerBar.cs:1361-1366,1503-1512`).
- Rows (order fixed by `DevicePickerModel.Build`): header *This computer* (disabled) · radio **System default** (`Icons.ThisPc`) · one radio per local endpoint with a form-factor glyph (Speakers `E7F5` / Headphones `E7F6` / TvMonitor `E7F4` / ThisPc `E977`) · separator · header *Spotify Connect* (disabled) · one radio per Connect device, `ThisDevice` filtered out (Phone `E8EA` / Speaker / Tv / Computer) · when the roster is empty, two disabled rows: `player.noDevices` + `player.noDevicesHint` (`DevicePickerModel.cs:41-87`).
- The separator + *Spotify Connect* header are **unconditional** — they render even when the Connect section is only the two empty rows, and even when the local section is empty.
- Checks: a local row is checked only when `localSupported && weAreActiveOutput && <id matches>` (`DevicePickerModel.cs:61,65-67`), so a remote-owned session leaves the whole "This computer" section unchecked. A Connect row is checked when `id == activeConnectId` **or `d.IsActive`** (`:78`) — the one place the raw roster flag still votes; see §0 #10.
- Labels are capped at **48 chars** — `label[..47].TrimEnd() + "…"`, so the ellipsis is inside the cap (`DevicePickerModel.cs:36-39`). Upstream of the cap, `AudioDeviceNaming.Shorten` already prefers `DeviceDesc` and strips the "(… Adapter …)" suffix; `FilterForPicker` then restores the FULL name on duplicate short names (`LocalAudioDeviceService.cs:90-105`).
- When local playback is unsupported, every "This computer" row is disabled and carries the accelerator text `player.unavailable` (a disabled `MenuFlyoutItem` cannot carry a tooltip) (`DevicePickerModel.cs:56`).
- Clicking a local row → `LocalOutputs.SelectAsync(id)` (route first, then transfer playback home if a remote device owns it). Clicking a Connect row → `DeviceControl.TransferAsync(id)` (`PlayerBar.cs:786-793`, `LocalAudioDeviceService.cs:109-123`).
- The picker also opens **by itself** when the bridge bumps `DevicePickerRequest` (the critical "Playback isn't supported on this device yet / Choose device" toast's action, `PlaybackBridge.cs:955-956`, `:1040-1049`) — only the `DevicePickerScope.Bar` instance responds, and only for a request that post-dates its mount (`PlayerBar.cs:1500,1516-1521`).
- The **remote device line** opens the same `DevicePickerMenu`, but anchored on ITSELF with `TopEdgeAlignedRight` (`PlayerBar.cs:1433-1438`) — i.e. above the LEFT cluster, right-aligned to the line. It is a second anchor for one menu, not a second menu.

**Video split button**
- Primary click = symmetric toggle (`b.ToggleVideo`) — off opens at the user's preferred placement, on turns it off from any placement; it lights whenever video is live **anywhere**.
- Chevron click, or right-click / Menu key anywhere on the pair, opens the placement menu (`OnContextRequested`, `PlayerBar.cs:570`).
- Both halves are passed `enabled: hasVideo` (`PlayerBar.cs:576,583`), so while the reserved slot is invisible they are also disabled — `Opacity 0` + `HitTestVisible false` + `IsEnabled false`, belt and braces.
- Tooltips: primary `player.switchToVideo` / `player.switchToAudio`; chevron `player.videoOptions` ("Where to play the video"). These are the only **`ToolTip.Wrap`** tooltips in the bar; the inline volume rail's "NN%" bubble is the engine `Slider`'s own thumb tooltip, not one of these.

**Drop target (the centre)**
- Accepts `WaveeDragKinds.Resource` payloads where `CanCopyTracks`; the caption is `drag.playNext` ("Play next"); refusals say `drag.cantAddArtist` or `drag.nothingToAdd`. The drop **front-inserts** into the user queue (never an immediate playback change) and toasts `detail.addedFirstToQueue` / `detail.addedToQueue` with a success severity (`PlayerBar.cs:424-438,663-684`).

**Accessibility**
- `Role` is set on every interactive node (`Button`, `Hyperlink`, `Slider`, `Text`, `None` for the live line) and every button is focusable. The engine's UIA backend is still a scaffold (`..\fluent-gpu\src\FluentGpu.Windows\Uia\Placeholder.cs`) and `Element` has no name property (`Element.cs:300` is the only automation field), so **no accessible names are exposed today** — the ToolTip texts on the video pair are the only human-readable labels in the bar. Keep the roles; treat names as a known gap, not a thing to invent per-node.

**Localised strings used by this surface** (`src/apps/Wavee/assets/loc/en-US.json`): `player.nothingPlaying`, `player.loading`, `player.cannotPlay`, `player.reconnecting`, `player.previous`, `player.next`, `player.shuffle`, `player.repeat`, `player.like`, `player.mute`, `player.unmute`, `player.lyrics`, `player.queue`, `player.nowPlaying`, `player.playingOn` ("Playing on {device}"), `player.thisComputer`, `player.systemDefault`, `player.spotifyConnect`, `player.unavailable`, `player.noDevices`, `player.noDevicesHint`, `player.chooseDevice`, `player.localPlaybackUnsupported`, `player.switchToVideo`, `player.switchToAudio`, `player.videoOptions`, `player.dockInRail`, `player.videoMiniPlayer`, `player.videoInSeparateWindow`, `player.videoFullScreen`, `player.videoAlwaysOnTop`, `player.turnOffVideo`, `player.videoNeedsWiderWindow`, `player.videoNoSecondWindow`, `player.videoNoFullscreen`, `play.live` ("LIVE"), `play.goLive` ("GO LIVE"), `play.behind` ("−{time}", U+2212 MINUS), `drag.playNext`, `drag.cantAddArtist`, `drag.nothingToAdd`.

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| cover | `b.CurrentTrack.Value.Image` | `Playback.Current` → `Track.ImageId` (`TrackFields.Image`) | `t.Knows(TrackFields.Image)`; until then the opaque placeholder tile (never an empty hole) |
| title | `NowPlaying(b).Title`, with a guard that a placeholder track seeding `Title == Uri` never reaches the user (`PlayerBar.cs:731`) | `Track.TitleId` (`TrackFields.Title`) | `t.Knows(TrackFields.Title)` else `player.loading` — **never** render a uri |
| artist names + links | `track.Artists` (`ArtistRef[]`) | `Edges.TrackArtists.Targets(slot)` → `Artist.NameId` + `Artist.Uri` | edge `State == complete`; a partial edge must not render a half credit list |
| heart state | `lib.IsSaved(track.Uri)` | `User.Me.Likes(t)` — the `Edges.Liked` reverse index (plan §4.14) | always answerable (no bool column, no probe); pending bit = optimistic |
| play/pause glyph | `b.IsPlaying` | `Playback.PhaseSignal` (`Phase.Playing`) | immediate |
| prev/next enabled | `active \|\| buffering \|\| reconnecting` (`canTransport`) | **GAP** — see below | — |
| position / fill | `b.PositionMs`, `b.PositionFrac`, `b.LastPositionSample` | `Playback.PositionMs` + `State.PosQpc` (`PosMs + (now − PosQpc)`) | `Phase == Playing` for interpolation; else the reported value |
| duration / remaining | `b.DurationMs` | `Track.DurationMs` (`TrackFields.Duration`) | `t.Knows(TrackFields.Duration)`; 0 → the seek bar is disabled, not a full grey rail |
| shuffle / repeat | `b.IsShuffle`, `b.Repeat` | `Playback.State.Shuffle/Repeat` — **not published as signals** by plan §4.8 | **GAP** |
| volume / mute | `b.Volume` (`FloatSignal`), `b.OutputMuted` | `Playback.Volume`; mute **GAP** | — |
| buffering / loading / error / reconnecting | `b.IsBuffering`, `b.IsLoading`, `b.Error`, `b.RecoveryKind` | `Phase.Loading` + `Pending.Load` (D21) cover part of it | **GAP** (buffering, error, recovery kind) |
| "Playing on X" + Devices lit | `RemoteDevice(b)` over `b.Devices` + `b.ActiveDeviceId` | `State.Owner == Owner.Foreign` + `State.ForeignDeviceSlot` | owner fold is model-side (plan §4.7 `Ownership.Fold`); the **device row itself** is a GAP |
| device picker rows | `b.Devices`, `b.ActiveDeviceId`, `b.LocalOutputs.Devices/SelectedOutputId`, `b.LocalPlaybackSupported` | **GAP** | — |
| video presence | `b.CurrentTrackHasVideo` | `Track` flag `HasVideo` + `VideoCounterpart` slot (`TrackFields.Video`, plan §4.2) | `t.Knows(TrackFields.Video)`; the slot is **reserved regardless** — readiness controls opacity, not presence |
| video placement / transport owner | `b.VideoSurface` → `PlacementCore.Resolve/TransportOwnerOf` | `Shell/Video.cs` CORE (A13, owner K, Wave 4): `PlacementCore` + `PlacementState` | — |
| live window / DVR / behind-edge | `b.Live`, `b.IsLive`, `b.IsBehindLive`, `b.TunedInAtMs` | **GAP** | — |
| rail open + mode (queue/lyrics/NPV latches) | `ShellUi.RailOpen`, `ShellUi.Mode` | `Shell` UI state (ch. 18/21) | immediate |
| marquee on/off, show-remaining | `WaveeSettings.MarqueeEnabled`, `…PlayerBarShowRemaining` + `AppearancePrefs.Epoch` / `PlayerBarPrefs.Epoch` | `Platform` settings + an epoch signal | immediate |

The bar **demands nothing**: it renders whatever the playback model publishes. There is no page-side fetch window here and there must not be one. Two derived facts must stay on the model, never re-derived in the UI: **`IsBehindLive`** (hysteresis, `LiveEdgeState`) and **`TransportOwner`** (`PlacementCore`) — both were UI-side comparisons once and both produced shipped flicker bugs (`PlayerBar.cs:1245-1249`, `:154-165`).

### DATA GAPS

| what the bar shows | 0.2.9 source | proposed 0.3 home |
|---|---|---|
| **Connect device roster** (id, name, kind, volume, isActive) | `IConnectDevices.DevicesChanged` → `PlaybackProjection.cs:1477` → `Signal<IReadOnlyList<PlaybackDevice>>` | a `DeviceTable` in `Playback/Playback.Host.cs` (columns `Name: StringId`, `Kind: byte`, `Volume: ushort`, `Flags`) + `Signal<uint> DevicesChanged`; `State.ForeignDeviceSlot` already assumes slots exist |
| **Active Connect device id** (owner-derived, no `IsActive` fallback) | `PlaybackBridge.ActiveDeviceId` folded from `PlaybackOwnership` | `Playback.State.Owner/ForeignDeviceSlot` + a published `Signal<int> ActiveDeviceSlot` |
| **Local audio endpoints** (id, name, form factor, isDefault) + selection + `LocalPlaybackSupported` + `OutputMuted` | `LocalAudioDeviceService` (own monitor, `FilterForPicker`) | keep the service in `Playback/Playback.Audio.cs` (SHELL) and publish `Signal<IReadOnlyList<LocalAudioDevice>>`, `Signal<string?> SelectedOutputId`, `Signal<bool> Supported/Muted` |
| **Shuffle / repeat / buffering / error / recovery kind / canSeek / canSkipPrev / canSkipNext** | 8 bridge signals | add to `Playback.Host.Publish()` — the plan publishes only Current/Phase/Position/Volume (§4.8) |
| **Live timeline** (`IsLive`, `SeekableStart/EndMs`, `BehindMs`, `HasWindow`, `TunedInAtMs`) + `IsBehindLive` | `IPlaybackState.Live` → `PlaybackBridge.Live` + the `LiveEdgeState` fold | a `LiveWindow` struct on `Playback.State` + `Signal<LiveWindow> Live`, `Signal<bool> IsBehindLive`, `Signal<long> TunedInAtMs` |
| **Video placement state** (`PlacementState`, `Available`, resolved placement, `TransportOwner`, host capability) | `PlaybackBridge.VideoSurface` + `PlacementCore` | `Playback/Playback.Video.cs` (SHELL, decode/host, owner H): `Signal<PlacementState>`; the pure `PlacementCore` is `Shell/Video.cs` CORE (A13, owner K) |
| **Playback context uri** (the art tile's route) | `PlaybackBridge.CurrentContext` | `Playback.State` context `EntityUri` + `Signal` |
| **Module playable link table** (which slot opens which page) | `PlayableLinks` + `ModulePages` resolve cache | `Platform/Modules.cs` lookup; `PlayableLinks` ports as a pure rule |
| **Device picker request** (toast → open the picker) | `Signal<int> DevicePickerRequest` | one `Signal<int>` on `Playback.Host` |
| **Cover placeholder tint** | `CoverColorPlane.Watch(url)` (graded light/dark pair per cover) | ch. 00's cover-palette store; the bar only needs `PlaceholderFor(url)` |

---

## 8. Pure rules to port verbatim

| rule | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `PlayerBarResponsiveLayout` (`CompactW 440`, `MediumW 760`, `ComfortableW 900`, `WideW 1100`, `FullW 1240`, `NarrowHysteresis 24`, `Nominal`, `Resolve`) | `Features/Shell/PlayerBarResponsiveLayout.cs:20-45` | which tier a width is, and the asymmetric widen/narrow rule | `src/apps/Wavee.Tests/PlayerBarResponsiveLayoutTests.cs` (11 width cases + the 5 hysteresis cases) | `Shell/Shell.cs` — CORE section "player bar tiers" |
| `PlayerBarLayout` (the 27-field record + `ForTier` + the floors `MinButtonBox 32`, `MinButtonGlyph 16`, `MinPrimaryBox 36`, `MinPrimaryGlyph 18`, `PrimaryBoxRoomy 40`, `PrimaryGlyphRoomy 20`, `TopEdgeWidthFloor 2400`) | `PlayerBarResponsiveLayout.cs:62-170` | every metric and every show/hide flag per tier | same file (floors, flatness, the 300-DIP balance, `TopEdgeWidth` ultrawide cases) | `Shell/Shell.cs` — same section |
| `DevicePickerModel` + `DevicePickerRow`/`RowKind` + `MaxLabelChars 48` | `App/DevicePickerModel.cs:8-88` | the two-section picker composition, checks, disabled rows, empty state | `src/apps/Wavee.Tests/DevicePickerItemsTests.cs` | `Shell/Shell.cs` — CORE section "device picker rows" |
| `LocalAudioDeviceService.FilterForPicker` | `App/LocalAudioDeviceService.cs:90-105` | hides display-audio sinks; disambiguates duplicate names | `DevicePickerItemsTests.cs:119-134` | `Playback/Playback.cs` CORE (the service itself stays SHELL in `Playback.Audio.cs`) |
| `AudioDeviceNaming.Shorten(deviceDesc, friendlyName)` | `SpotifyLive/Audio` (source-included) | the endpoint's DISPLAY NAME — `DeviceDesc` first, else strip the "(… Adapter …)" suffix off the FriendlyName; this is what the picker rows actually read | `DevicePickerItemsTests.cs:108-117` (6 cases) | `Playback/Playback.cs` CORE — **missing from the original §8 list** |
| `LocalAudioDeviceService.Fold(AudioEndpointFormFactor)` | `App/LocalAudioDeviceService.cs:142-152` | form factor → `LocalAudioDeviceKind` → the picker glyph | (covered indirectly) | `Playback/Playback.cs` CORE |
| `LiveWindow.HasWindow` / `MinWindowMs = 30_000` | `../Wavee.Core/Playback/Playback.cs:39-44` | **Dvr vs Line** — a seekable span under 30 s is NOT a window, so the rail becomes the breathing line | `Backend/LiveRailTests.cs`, `Backend/NowPlayingLiveWindowTests.cs` | `Playback/Playback.cs` CORE |
| `TimeFormat.Clock` (`m:ss` below an hour, `h:mm:ss` at/above, negative clamps to 0, hours never wrap) | `Backend/Playback/TimeFormat.cs` | every transport digit in the app | `src/apps/Wavee.Tests/TimeFormatTests.cs` | `Playback/Playback.cs` CORE |
| `LiveRail` (`Frac`, `DisplayFrac`, `Seek`, `BehindAt`, + the `in LiveWindow` overloads at `:78-89`) | `Backend/Playback/LiveRail.cs` | the DVR rail's arithmetic and its two degenerate edges | `src/apps/Wavee.Tests/Backend/LiveRailTests.cs` | `Playback/Playback.cs` CORE |
| `LiveEdgeState` (`EnterBehindMs 15000`, `ReturnToEdgeMs 5000`, `ConfirmReports 2`, `Next`) | `Backend/Playback/LiveEdgeState.cs` | AT EDGE vs BEHIND, with hysteresis — the flicker fix | `src/apps/Wavee.Tests/Backend/LiveEdgeStateTests.cs` | `Playback/Playback.cs` CORE |
| `PlacementCore` (`Resolve`, `IsActive`, `Allows`, `TransportOwnerFor/Of`, `OwnsTransport`, `TransportClaimants`) | `App/PlacementCore.cs` | who owns the transport; which placement is live | `src/apps/Wavee.Tests/PlacementCoreTests.cs` | `Shell/Video.cs` CORE (A13, owner K — used by `+Shell.PlayerBar.UI.cs` and ch. 24) |
| `PlayableLinks` (`RouteFor`, `LabelFor`, `IsModule`, `LinkSlot`) | `Actions/PlayableLinks.cs` | where the art / title / artist span navigates | `src/apps/Wavee.Tests/Actions/PlayableLinksTests.cs` | `Shell/Shell.cs` CORE (beside the action table) |
| `VolumeTaper.Amplitude` (cubic) | `Backend/Audio/VolumeTaper.cs` | slider position → amplitude, at the amplitude boundary only | `src/apps/Wavee.Tests/Audio/VolumeTaperTests.cs` | `Playback/Playback.Audio.cs` (pure static; keep the UI linear) |
| `SeekGate` (`Decide`, `ReportedPositionMs`) | `SpotifyLive/Audio` (source-included) | whether a seek is applied or parked (the launch-restore deadlock) | `src/apps/Wavee.Tests/SeekGateTests.cs` | `Playback/Playback.cs` CORE |

---

## 9. Re-author notes

**Must not be simplified**

1. The **six-tier ladder with flat button metrics**. Do not "simplify" to three breakpoints or re-introduce a size ramp: three of six tiers once shipped sub-minimum hit targets and the test file exists to stop it happening again.
2. The **scrub gate + interpolation + pixel quantisation** triple in `SeekBar.Recompute/Publish`. Removing quantisation costs a full-window Present per ticker frame; removing the gate brings back the 1 Hz snap-back; removing interpolation brings back the stepping playhead.
3. `TimeText`'s **bound text channel**. Reading `PositionMs` in `Render` re-renders the whole label (box, fills, handler) at tick rate — it showed up in the steady-churn census on essentially every frame (`PlayerBar.cs:1172-1185`).
4. The **104-DIP live slot reservation** and the **44-DIP time slot**. Both exist so a state swap moves nothing; the rail sits in a `Grow` slot with a bounds `LayoutTransition`, so a slot that changes width FLIPS the whole rail sideways.
5. **Reserving the video slot before `hasVideo` resolves.** Presence via `Visible` collapses layout; `Opacity` + `HitTestVisible` is the right channel.
6. The **keys** `np-title` / `np-artists`. The engine matches an unkeyed child against the old child at the same absolute index, and the remote-device line is a keyed insert that can precede them — without the keys, entering remote mode made the title slot reuse the artists' mounted `MarqueeHost` (issue #139).
7. `Marquee.TriggerMode.Hover`, not `PauseOnHover`. They are opposites.
8. **One transport per window** via the single derived `TransportOwnerNow` signal — a local visibility bool is exactly what let two owners render.
9. The **overflow ordering** and the fact that *everything* dropped from the row appears there (including the video ladder as a cascading submenu).

**Traps**

- **Props freeze at mount.** Five live instances of this trap in the bar: the layout arrives as a `Signal`; marquee text/colour arrive as `Prop.Of` thunks; `SeekBar.enabled` is derived in `Render` (a ctor flag would stick at `false` forever); `VolumeButton` is remounted by a `Key` that encodes `popup+box+glyph`; `PlayerMoreMenu` is remounted by a `Key` that hashes the whole command set **including each command's sub-flyout rows** (`PlayerBar.cs:1003-1016`).
- **`ReuseGuard`**: the `MoreButton` hash must fold anything that can change the menu's shape, or the mounted component shows a stale cascading menu until an unrelated change bumps the hash.
- **Imperative seed over remount**: the heart's pop is played on the captured node handle (`Context.Anim.IconSwapIn`), because a keyed remount of a focusable button would reset its hover/focus mid-toggle (`PlayerBar.cs:192-206`).
- **Zero-alloc frames vs per-row richness** — how 0.2.9 reconciled them here: the *bar* is rich (marquees, links, menus, drag) but re-renders only on low-frequency signals; everything hot is either a compositor bind (`_displayFrac`, `b.Volume`) or an isolated ~1 Hz leaf (`TimeText`). `IsolateLayout = true` on the dock root is the layout firewall that keeps a title change from re-solving the window (`PlayerBar.cs:652-658`). Port all three mechanisms together — they are one design.
- **`Environment.TickCount64`** appears three times in `SeekBar` and once in `PlaybackBridge.SeekLatch`. The UI-facing ones must become the QPC sample in 0.3 (§5).
- **Dead branch**: `if (!showLike && active)` (`PlayerBar.cs:452`) can never fire — `showLike == (ShowLikeSlot && active)` and `ShowLikeSlot` is always true. Drop it in 0.3 rather than porting it.
- **Two more dead flags**: `bool showLeft = true` and `bool showArtwork = true` (`PlayerBar.cs:210-211`) are literals with no tier behind them — they gate `rowKids.Add(left)` and the art tile. Drop both; the art tile and the identity cluster are non-negotiable #3 anyway.
- **`ElapsedSinceTuneIn` has a fallback that is easy to lose**: when `TunedInAtMs <= 0` (the first tick of a stream whose live-ness has not landed) it returns the **reported position**, not 0 — that is what keeps the live elapsed label from blinking to `0:00` on the way in (`PlayerBar.cs:1234-1240`). It also reads a wall clock (`DateTimeOffset.UtcNow`), which is correct here (it is a date difference, not an animation) but must not be confused with the TickCount64 anchor above.
- **A device-picker flyout has no height cap.** It is a plain `MenuFlyout` over `roster + local endpoints + 4 fixed rows`; a user with a dozen Connect speakers gets a dozen rows and the overlay's own fitting is the only limit. 0.3 should give it a max height + scroll, or the picker is the one place in the bar that can grow past the window.
- **Stale doc comments (CODE WINS)**: `Primary`'s XML doc says "a filled accent circle" — the code paints **no plate at all** (`Fill/HoverFill/PressedFill = Transparent`, glyph in `Tok.TextPrimary`, hover/press = scale only, `PlayerBar.cs:1107-1126`). `SeekBar.cs:238` says the rail is "dimmed for a media line" — it is the stock `RailFill`, undimmed. Reproduce the code.
- **Env-var diagnostics**: `WAVEE_PLAYERBAR_DIAG` gates `playerbar.layout_band` / `playerbar.render` / `seekbar.render` / `seekbar.bounds` (`PlayerBar.cs:32,66`, `SeekBar.cs:48`). CLAUDE.md forbids env switches for behaviour/verification — in 0.3 make these always-on structured log lines (they are cheap and low-frequency) or drop them.
- **Zoom is width**: `Viewport.Size` is post-zoom DIP, so app zoom moves the bar between tiers. A 1920-px window at 150 % zoom is a 1280-DIP viewport = **Wide**, not Full.

**Where the plan is wrong or too thin for this surface**

1. **§2's line budget for `Shell.UI.cs` (2,800) was not survivable when this chapter was first written.** This chapter alone is 2,230 lines of 0.2.9 UI, and `Shell.UI.cs` is also the whole shell frame (ch. 18), overlays (ch. 19) and the toolbar. **Settled**: the named partial `+Shell.PlayerBar.UI.cs` (2,000 lines, owner I, Wave 4) now holds the bar.
2. **§4.8 `Playback.Host` publishes four signals** (Current, Phase, PositionMs, Volume). The player bar alone needs ~18 more (§7 DATA GAPS). Land that list before Wave 4 starts, or owner I will invent bridges.
3. **§4.7's `State` has no `Buffering`, no `Error`, no `Live`, no `CanSeek`, and no placement.** `PlayerState` (NoTrack/Loading/Reconnecting/Error/Active) is a five-way derivation over exactly those; without them the bar cannot render its own state machine.
4. **Devices have no representation anywhere in §4.** `State.ForeignDeviceSlot` implies a device table that §4.1's `EntityKind` does not contain. Decide: a non-entity `DeviceTable` on `Playback.Host` (recommended — devices are session state, not catalogue entities) or a new `EntityKind.Device`.
5. **§4.12's row sketch** treats the artist line as a precomputed `ArtistLineId` (P11). The player bar needs **per-name links**, so it must read `Edges.TrackArtists` directly; the concatenated line is not sufficient here.
6. **§2's tree had no home for `PlacementCore` / video placement when this chapter was first written**; `Playback.Video.cs` is SHELL only (decode/host, owner H, Wave 3). **Settled (A13):** the pure part is `Shell/Video.cs` CORE (`PlacementCore` + `PlacementState`), owner K, Wave 4 — not `Playback.cs`.
7. **`FormatSplitButton` is not a player-bar control.** It lives in `Components/FormatSplitButton.cs` and is used only by `Features/Detail/TrackVersionsPanel.cs:322` (a 30×28 + 20×28 split pill, `Tok.FillControlDefault`, 1-px `Tok.StrokeControlDefault`, `Radii.Control` on the outer corners, `Icons.Play` 11 DIP / `Icons.ChevronDown` 9 DIP, opening a radio ladder of "<format>   <n> kbps" rows + "Use default quality", `BottomEdgeAlignedRight`). The bar shows **no format/bitrate indicator at all** — `StreamFormat`/`StreamBitrateKbps` are read only by the VU/Winamp decks (ch. 23) and `StageIdentity` (ch. 21). Whoever owns the versions drawer should take it; it is documented here only because it was routed to this chapter.
8. **`Components/Equalizer.cs` and `App/EqualizerSettings.cs` are not player-bar surfaces either**: `WaveeEqualizer` is used by track rows, cards, Home and the sidebar (ch. 01/02/11/25); `EqualizerSettings` is the persisted 10-band DSP vector behind Settings ▸ Audio (ch. 27).
9. **Drift, `popout-player-drag-autohide-plan.md`**: that plan is engine-side and never states the bar's own rule; the code's rule is that the bar **keeps** the transport for PopOut *and* Docked/Floating and yields only to Fullscreen (`PlayerBar.cs:154-165`). Code wins.
10. **Drift, `volume-curve-and-putstate-plan.md` §1.2** cites `PlayerBar.cs:415,841,1021` and `PlayerBar.cs:824` for the mute threshold; those line numbers are stale (the live sites are `:496`, `:1383`, `:1398-1413`). The *rules* in it — linear slider everywhere, cubic taper only at the amplitude boundary, coalesced put-state — are current.
11. **The picker's `|| d.IsActive` contradicts the ownership contract** (`DevicePickerModel.cs:78`). `docs/plans/wavee/…` has no note of it and neither did §0 until this audit. Decide before Wave 4: drop the fallback and check against the owner id alone (recommended — it is the same fix `RemoteDevice` already took after the 2026-09-11 Connect incident), and add the regression case to `DevicePickerItemsTests`.
12. **The top-edge sweep ignores reduced motion.** It is an engine gap (`ProgressBar` seeds raw `Keyframes`), not an app decision — so "the bar honours reduced motion" is only true for the scale tiers, the like pop, the marquee and the live line. Either fix it engine-side or flatten the band in 0.3.

**Line budget**

| | lines |
|---|---|
| 0.2.9 (`PlayerBar.cs` + `PlayerBarResponsiveLayout.cs` + `SeekBar.cs`) | **2,230** |
| plan §2 target, the named partial `+Shell.PlayerBar.UI.cs` alone | **2,000** (owner I, Wave 4) |
| honest estimate for the player bar alone in 0.3 | **1,900–2,100** UI lines in `+Shell.PlayerBar.UI.cs` + **~230** CORE lines in `Shell.cs` (tiers + picker rows) + ~120 CORE lines moving into `Playback.cs` (TimeFormat/LiveRail/LiveEdgeState already exist) |

The only real savings are the dead like-overflow branch, the env-gated diag blocks, and some comment volume; nothing structural can be removed without losing a behaviour named in §0.

**Now in the §2 tree**: the named partial `+Shell.PlayerBar.UI.cs` for the bar (above, owner I, Wave 4), which also reads the device roster (a read of `Spotify.Connect`'s cluster, not a new store); a home for `PlacementState` in `Shell/Video.cs` CORE (A13, owner K, Wave 4); and the rail open/mode state the bar's four toggles write is the named `Shell.Ui` section of `Shell.cs` (`RailOpen`, `Mode`, `RailWidth`, `DockedVideoHeight(+Pinned)`, `ActiveStagePlayable`, `RailFits`, `ImmersiveLyrics`, `Toggle`, `CanFitRail`).

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`.
**Modes** — `--fake` cannot play anything: `Services.CreateFake` wires `UnsupportedPlaybackPlayer` with **no** `LocalPlayback` and a `NoConnectDevices` roster (`App/Services.cs:584-631`), so a play intent only raises the "Playback isn't supported on this device yet / Choose device" toast, and `b.LocalOutputs` is null (mute is the software 0 ⇄ 0.7 toggle). Tags below:
`[F]` = `--fake`; `[P]` = **`--real-backend`**, no login, playing a local file via the toolbar **Play ▸ File…** — `CreateReal` attaches `player.LocalPlayback = preLogin.Controller` (`App/Services.cs:760-774`); `[L]` = logged in; `[LR]` = logged in **and** playing a live/DVR source.

| # | tag | check |
|---|---|---|
| 1 | F | Window at 1440×900, Home route: the dock is exactly **72 DIP** tall and paints no fill — the Mica/page wash reads straight through it. Static capture, measure against the window bottom. |
| 2 | F | Exactly **one** 1-DIP line at the dock's top edge; no second stroke above it from the content region and no shadow. Static capture, 400 % crop of the seam. |
| 3 | F | Toggle the theme (Ctrl+palette → theme): the seam is white-alpha in dark, black @ 6 % in light; it never becomes an opaque grey bar. |
| 4 | F | Idle state: title reads "Nothing playing" in secondary ink, **no** artist line, **no** heart, prev/play/next dimmed, empty rail. Static capture. |
| 5 | F | Resize 1440 → 1250 → 1239: the Expand (⌃) chevron leaves the row at 1239 and a "⋯" appears; the neighbours **slide** into place (no snap). Frame recording. |
| 6 | F | Resize back up 1216 → 1240: the chevron returns exactly at 1240, not before (widening commits immediately). |
| 7 | F | Narrowing hysteresis: from Full, the bar stays Full until width < 1216 (1240 − 24). Drag the frame slowly and watch the ⌃ button. |
| 8 | F | Repeat #6/#7 at each threshold: 1100 (queue + volume slider + video), 900 (shuffle/repeat + remote line), 760 (volume glyph + lyrics + elapsed + prev/next), 440 (remaining label). |
| 9 | F | At every width from 1440 down to 360, measure a secondary transport button: **32 × 32**, glyph 16. It never shrinks. |
| 10 | F | The primary is 40 × 40 above 760 and 36 × 36 below it; its glyph is 20 / 18. |
| 11 | F | At 360 DIP the row still shows: art 40, title, artist line, heart, primary, a live seek rail, Devices, "⋯". Nothing else. |
| 12 | F | The art tile is 48 DIP with a 6-DIP radius above 760 and 40 DIP below it. 400 % crop of the corner. |
| 13 | F | Hover each right-cluster button: the glyph brightens to primary ink and the box scales to 1.07 over ~83 ms; no plate appears on an unlatched button. Hover capture + frame recording. |
| 14 | F | Press and hold a transport button: scale 0.92, no fill. |
| 15 | F | Click Queue: the button latches — accent glyph over a subtle plate that **cross-fades** in (not a snap), and the rail opens. Frame recording at 60 fps. |
| 16 | F | Click Queue again: it unlatches and the rail closes (`ShellUi.Toggle` semantics). |
| 17 | F | Devices button at 1440: click → the picker opens **upward**, right-aligned to the button, with the clip-reveal growing from the anchor edge. Frame recording. |
| 18 | F | Picker contents in `--fake`: "This computer" header + "System default" (radio) + any local endpoints; separator; "Spotify Connect" header + "No devices found" + "Open Spotify on another device", the last two dimmed. |
| 19 | F | Esc and a click outside both dismiss the picker; focus returns to the button. |
| 20 | F | Press Play on an idle bar in `--fake`: the "Playback isn't supported…" toast appears with a **Choose device** action; clicking it opens exactly ONE picker (the bar's). |
| 21 | F | At 600 DIP open "⋯": rows are Previous, Next, Shuffle, Repeat, Lyrics, Queue, Now playing, (Video ▸), Mute — in that order, with check marks on the latched toggles. |
| 22 | F | The "⋯" menu opens upward, right-aligned, as a plain MenuFlyout — no empty-then-fill flash. Frame recording. |
| 23 | F | Toggle shuffle from inside the "⋯" menu at 600 DIP, reopen it: the check mark reflects the new state (the menu remounts on the command-set hash). |
| 24 | F | Tab through the bar: every button takes a focus rectangle; clicking a button does **not** steal focus (press Space afterwards — it still reaches the shell play/pause). |
| 25 | F | Drag a track row from any page over the centre cluster: the page dims behind a scrim, the **bar stays fully lit**, and the chip caption reads "Play next". Frame recording. |
| 26 | F | Drag an **artist** card over the centre: the caption reads "Can't add an artist". |
| 27 | P | Play a local file: title, artist line and cover appear; the heart slot appears; prev/play/next and the seek bar become live. Static capture. |
| 28 | P | The seek fill advances **smoothly** (no once-per-second step) — frame recording over 5 s, diff consecutive frames: the fill edge moves by ~1 px per step, never in 1-second jumps. |
| 29 | P | Hover the seek row: the 22-DIP thumb ring fades in with a 12-DIP accent dot at 0.86 scale; the rail stays 4 DIP tall. Hover capture at 400 %. |
| 30 | P | Press and hold the thumb: the inner dot shrinks (0.71 relative) and the value fill darkens to the accent-tertiary rung. |
| 31 | P | Click anywhere on the rail: the playhead jumps to the press point **immediately** and one seek is issued (watch the log / the position label — not a series of seeks). |
| 32 | P | Drag the thumb across a 1-second tick boundary: the fill does **not** snap back to the reported position mid-drag. Frame recording. |
| 33 | P | Release the drag: the position label matches the drop point and the fill stays there while the next tick lands. |
| 34 | P | Pause, then drag: the fill still follows the finger and stays at the drop point (the ticker is unmounted while paused). |
| 35 | P | Time labels: left is elapsed, right is "-m:ss" remaining, each 44 DIP wide; the seek rail's width does **not** change when the digits change from 0:09 to 0:10. |
| 36 | P | Click the right label: it toggles to the track duration (no minus sign); relaunch the app — the choice persisted. |
| 37 | P | At the exact end of a track the right label reads "0:00", never "-0:00". |
| 38 | P | Hover the now-playing text: **both** lines start scrolling together, ping-pong, with a pause at the tail; move the pointer away and both stop and reset. Frame recording. |
| 39 | P | With the pointer nowhere near the bar, neither line moves at any time (the fixed S2 #12 bug). 30-second frame recording of a long title. |
| 40 | P | The idle long title shows a right-edge fade (not a hard clip); while scrolling both edges fade. 400 % crop. |
| 41 | P | Settings ▸ turn marquee off: the title ellipsises and the artists line becomes "first artist + (+N)"; the +N chip opens a flyout listing every credited artist. |
| 42 | P | Click the title → the album page opens; click the cover → the playback context opens; click an artist name → that artist page. Each hovers to a distinct colour (title accent, artist primary). |
| 43 | P | Right-click the now-playing cluster: the track menu appears with the four-verb strip and **no** "Remove from…" row. |
| 44 | P | Press on the heart and move 10 px: the track is **not** picked up as a drag (the heart blocks the drag arm); press on the cover and move: the drag chip appears. |
| 45 | P | Volume at 1440: the glyph + a 96-DIP rail; drag the rail → a "NN %" bubble follows the thumb; at 0 the glyph becomes the mute glyph. |
| 46 | P | Click the volume glyph at 1440: it mutes/unmutes (glyph swaps) without opening anything. |
| 47 | P | At 950 DIP the rail is gone; clicking the glyph opens a 52 × 168 popup above it with a vertical rail and **no** value bubble. |
| 48 | P | Start a track and watch the top edge: the 3-DIP indeterminate sweep runs while loading/buffering and reverts to the 1-DIP hairline once playing. Frame recording. |
| 49 | P | While the sweep runs the row content shifts up by exactly 1 DIP (72 − 3 vs 72 − 1) and shifts back — confirm it is not more than that. |
| 50 | L | Like the playing track from the bar: the heart swaps to the filled glyph and **pops** (scale 0.25 → 1 with a blurred cross-fade, ~250 ms). Frame recording. Unlike: a plain swap, no pop. |
| 51 | L | Change track while liked: the filled heart appears with **no** pop (the pop is same-track only). |
| 52 | L | Start playback on another Connect device: at ≥ 900 DIP the "🖳 Playing on <name>" line appears **above** the title in accent ink, the Devices button latches, and the layout does not jump (the line animates in). Frame recording. |
| 53 | L | Narrow to 850 DIP while remote: the line disappears (Comfortable-only) but the Devices button stays latched. |
| 54 | L | Click the "Playing on…" line: the same device picker opens. |
| 55 | L | In the picker, the active Connect device carries the radio check and the "This computer" rows do not; transfer back and the checks swap. |
| 56 | L | A device name longer than 48 characters is capped with an ellipsis in the picker. |
| 57 | L | Play a track that has a music video: the 🎬 split button **fades in** with zero layout movement anywhere else in the row (nothing slides). Frame recording — diff the seek-bar right edge before/after. |
| 58 | L | Click 🎬: the video opens at the preferred placement and the primary half latches; the chevron never latches. |
| 59 | L | Click the chevron: the ladder opens upward with the current placement radio-checked; an unavailable rung is dimmed with its reason in the accelerator column. |
| 60 | L | Right-click anywhere on the 🎬▾ pair: the same ladder opens. |
| 61 | L | Hover each half: the tooltips read "Switch to video" / "Switch to audio" and "Where to play the video" after ~800 ms. |
| 62 | L | Send the video to a pop-out window: the bar **keeps** its transport and seek row (they must not disappear). |
| 63 | L | Go fullscreen (F11): the whole bar unmounts; leave fullscreen: it returns with the same seek position and no remount flicker. |
| 64 | LR | Play a DVR live source: the rail maps the window, a 2-DIP accent tick marks the right end, the right slot shows the LIVE word-mark with a static red dot, and the left label counts elapsed-since-tune-in (hours field once past 59:59). |
| 65 | LR | Scrub back more than ~15 s: after two reports the slot becomes "GO LIVE −m:ss"; the seek row's layout does **not** move during the swap (both states are right-aligned in a 104-DIP slot). Frame recording. |
| 66 | LR | Sit at the live edge for 60 s: the slot never flickers between LIVE and GO LIVE (the hysteresis machine). |
| 67 | LR | Click GO LIVE: the button disappears immediately (not after the seek lands) and the rail snaps full. |
| 68 | LR | Play an internet radio station with no window: the rail is replaced by a 2-DIP accent line breathing 0.55 ↔ 1 over 3 s; it cannot be dragged and shows no thumb. Frame recording. |
| 69 | LR | Pause the radio: the line stops breathing (flat at opacity 1). Minimize and restore: it is not animating while minimized (check the frame log). |
| 70 | F | Enable Windows "reduce motion": hover/press scales collapse to none, the like pop does not play, the marquee stops scrolling on hover, the breathing line goes flat — but the marquee edge fade and every colour cross-fade still work. **Known non-conformance to reproduce or fix, not to silently drop:** the seek/volume inner dot still scales (raw `Slider` floats) and the top-edge sweep still sweeps (`ProgressBar` raw keyframes). |
| 71 | F | At 950 DIP (Comfortable) measure the row gutter and the inter-cluster gaps: **RowPad 8, RowGap 6** — the *Medium* rungs, not the Wide 12/8. Comfortable is not `wide`. 400 % crop of the left gutter at 1150 vs 950. |
| 72 | F | At 600 DIP the left cluster's metadata column is **88 DIP** wide (172 − 40 art − 32 heart − 2×6 gap); the title ellipsis lands there. Measure, do not eyeball. |
| 73 | F | Widen 1240 → 1250 with a video playing at Full: the right cluster is 9 children (shuffle, repeat, volume glyph, 96 rail, lyrics, 🎬▾, queue, devices, ⌃) with **2-DIP** gaps and **no "⋯"**. Count them. |
| 74 | F | On an idle (`NoTrack`) bar at Full there is **no** lyrics button, **no** video slot and **no** "⋯" — overflow is empty because every `active`-gated command is absent. Static capture. |
| 75 | F | Play a track with exactly ONE credited artist, marquee OFF: the subtitle is the plain artist link with **no** "+1" chip (the chip needs `Count > 1`). |
| 76 | F | Play something with **no** credited artists: the second line is empty but the 2-DIP gap is still spent — the title does not re-centre. Compare against the `NoTrack` capture, where the line is genuinely absent. |
| 77 | F | Open the "+N" flyout from the compact artists line: it lists **every** credited artist and each row navigates. Note its anchor edge (0.2.9 asks for `BottomEdgeAlignedLeft` from a bottom dock — confirm 0.3 opens it upward). |
| 78 | F | Right-click the now-playing cluster: the menu opens with the **header** (cover + title + "artists · album") above the four-verb strip. A header-less menu is a regression. |
| 79 | L | With a Connect device active, open the picker: **no** "This computer" row carries a radio check, and the active Connect row does. Transfer home: the checks swap in the same open menu (it re-renders live). |
| 80 | L | Transfer playback away and then stop it on the other device (owner → `Nobody`): the "Playing on…" line disappears and the Devices button unlatches **even though the device is still listed** in the picker. This is the owner-derived id, not the roster. |
| 81 | L | With ≥ 10 Connect devices visible, open the picker: note whether it overruns the window. 0.2.9 has no height cap — 0.3 must (see §9). |
| 82 | L | Click 🎬 on a track whose video can only play in one surface: the ladder shows the unavailable rungs dim, each with its own reason text — "Needs a wider window" / "Not available on this display setup" / "Not available here". The **mini player** rung has no reason string: when dim it is simply dim. |
| 83 | L | With video OFF, open the ladder: it is exactly **four** rows and **no** separators (no "Always on top", no "Turn off video"). Turn video on in the pop-out: the ladder grows both extra rows and both separators. |
| 84 | F | Mount the shell with no `PlaybackBridge` (harness/test host): the dock is an empty 72-DIP band with **no hairline** in 0.2.9. Decide deliberately for 0.3 — keeping the seam is the better answer. |
| 85 | P | Play a Liked-Songs-style mosaic cover: the 48-DIP tile is a 2×2 mosaic at radius 6, with no frame and no badge. |

---

## 11. Audit log

Adversarial re-read of the 0.2.9 sources against this chapter (2026-09-12). One line per correction; `wrong` = a value or
claim that contradicts the code, `missing` = a state / element / rule the chapter did not cover, `unverified` = a
reference that did not resolve, `overclaim` = true in spirit but stated more strongly than the code supports.

| # | kind | section | correction |
|---|---|---|---|
| 1 | **overclaim** | §0 #10 | "the picker's checks all come from `RemoteDevice(b)` … **no** fallback to `IsActive`" is false for the picker. `DevicePickerModel.cs:78` checks `id == activeConnectId \|\| d.IsActive`. Rewrote #10 to name the exception, added the owner-id table (`""` for every `Nobody` cause, including `FromForeign`, `PlaybackProjection.cs:229-234`), and added §9 item 11 + parity #80. |
| 2 | **wrong** | §2 W3 | Comfortable was costed with the *Wide* spacing rungs. `wide = tier >= Wide`, so Comfortable takes RowPad 8 / RowGap 6 (`PlayerBarResponsiveLayout.cs:163-164`). centre 478 → **490**, seekRow 370 → **382**, rail 270 → **282**; the wireframe gutters were 12/8 and are now 8/6. |
| 3 | **wrong** | §3 | meta column at Compact listed as 92; it is `172 − 40 art − 32 heart − 2×6 LeftGap` = **88**. |
| 4 | **wrong** | §2 W15, §10 #45 | the volume tooltip string is `72%` — `$"{clamp(round(v*100),0,100)}%"`, no space (`PlayerBar.cs:107`). |
| 5 | **wrong** | §6 | `WaveeCommands.cs:142-150` does not hold the playback commands; they are at `:202-206`. Also pinned the real Space handler (`WaveeShell.cs:2087`, `:2133-2147`) and the F11 chord (`:2041`, handler `:895`). |
| 6 | **wrong** | §10 preamble | `App/Services.cs:571-576` is `CensusLine`, not `CreateFake` (`:584-631`); the pre-login local-media path is `:760-774`, not `:735-751`. Added that `[P]` needs `--real-backend`, and that `--fake` has a null `LocalOutputs` (software mute). |
| 7 | **missing** | §1.1, §2 W22 | the **bridge-null** render — `new BoxEl { Height = 72 }` with no top edge and no row (`PlayerBar.cs:138-140`). Added to the tree, a wireframe, and parity #84. |
| 8 | **missing** | §1.1 | the whole **overflow build order with its per-row gates** existed only as a picture (W17). Added the ordered list with each guard, including `volumeInOverflow = !ShowVolumeButton && active` and the fact that the video overflow row is **not** reserved (no `hasVideo`, no row), unlike the inline slot. |
| 9 | **missing** | §1.1, §2 W20 | the "+N" chip needs `artists.Count > 1`; with exactly one artist the compact path renders the ordinary list. Added the **no-artists** state (an empty row that still spends the 2-DIP gap, `PlayerBar.cs:861-862`) and the chip flyout's `BottomEdgeAlignedLeft` anchor wart. Parity #75-#77. |
| 10 | **missing** | §1.1, §3 | the time slots' **alignment** (elapsed `Justify=End`, remaining `Justify=Start`, `PlayerBar.cs:1214`) — the reason the digits hug the rail. |
| 11 | **missing** | §1.1 | `SeekTicker`'s mount predicate (`canAdvance` = track ∧ no error ∧ not loading ∧ playing ∧ not buffering) and the **100 ms fallback** interval while span or width is unknown (`SeekBar.cs:433`). |
| 12 | **missing** | §1.1 | the exact `VolumeButton` remount key (`"volume-inline-"` / `"volume-popup-"` + box + `"-"` + glyph), not the paraphrase in §1.2. |
| 13 | **missing** | §3.1 | **no glyph-code table existed** for the bar's own glyphs. Added one for all 16 slots plus the video-menu icons, verified against the generated `Icons` table. |
| 14 | **missing** | §5 | four motion rows: the volume rail's inner-dot ramp (inline *and* popup), the art/title/artist ink hovers, the remote-line ink hover, and the `PlayerMoreMenu` **remount** (a hash `Key`, deliberately not a transition). |
| 15 | **wrong** | §5 | the top-edge sweep's reduced-motion cell said "engine loop policy". `ProgressBar` seeds raw `AnimScheduler.Keyframes`, which has **no** reduced-motion gate (only `KeyframesMotion` does, `AnimScheduler.Timeline.cs:61-70`) — the sweep runs regardless. Corrected the cell, added §9 item 12, rewrote parity #70. |
| 16 | **missing** | §6 | the now-playing context menu opens with the shared **`ContextMenuHeader`** (cover + title + "artists · album", `Menus.cs:1115-1125`); the chapter listed only the strip and the rows. Parity #78. |
| 17 | **missing** | §6 | `Draggable` is **null** when `track is null` (`PlayerBar.cs:370`) — an idle cluster is not a drag handle; and the context menu is wired only when both `ActionServices` *and* the overlay service resolve (`:378`). |
| 18 | **missing** | §6, §2 W16 | the picker's unconditional Connect separator + header; the `weAreActiveOutput` gate that leaves the whole local section unchecked under a remote owner; the label-cap arithmetic (`[..47] + "…"`); and that `LocalGlyph`'s `Hdmi → TvMonitor` rung is unreachable because `FilterForPicker` drops display audio first. Parity #79. |
| 19 | **missing** | §6 | the remote-device line opens the **same** picker anchored on itself (`TopEdgeAlignedRight`, above the left cluster, `PlayerBar.cs:1433-1438`). |
| 20 | **missing** | §6 | both video split halves are also `IsEnabled = hasVideo`, not only transparent and inert. Clarified that the video tooltips are the only `ToolTip.Wrap` ones (the slider bubble is the engine `Slider`'s). |
| 21 | **missing** | §2 W18 | the placement ladder's **icons**; that "mini player" carries **no** reason string; and that the "Always on top" / "Turn off video" rows *and their separators* are conditional — a video-off ladder is four rows with no separators (`VideoPlacementMenu.cs:53-74`). Parity #82-#83. |
| 22 | **missing** | §8 | three pure rules with tests that were absent: `AudioDeviceNaming.Shorten` (6 cases, `DevicePickerItemsTests.cs:108-117`), `LocalAudioDeviceService.Fold`, and `LiveWindow.HasWindow` / `MinWindowMs = 30_000` — the Dvr-vs-Line threshold, which the chapter never stated. |
| 23 | **missing** | §9 | two more dead flags (`showLeft`, `showArtwork`, `PlayerBar.cs:210-211`); `ElapsedSinceTuneIn`'s position fallback when `TunedInAtMs <= 0`; and the device picker's absent height cap. |
| 24 | **missing** | §2 W23 | the mosaic-cover branch in `Surfaces.Artwork` (≥ 4 tiles → 2×2; 1-3 → first tile), a state the bar hits on a generated collection. Parity #85. |
| 25 | **missing** | §2 W21 | the non-owning bar also suppresses the Previous/Next **overflow** rows (the `ownsTransport &&` guard at `PlayerBar.cs:442`). |
| 26 | **missing** | §10 | measurement checks the list had no equivalent for: the Comfortable spacing rungs (#71), the 88-DIP Compact meta column (#72), the 9-child Full right cluster (#73), the idle-bar absences (#74), the long-roster picker (#81). |

**Verified correct, left alone** (spot-checked with file:line): every tier threshold and the direction of the 24-DIP
hysteresis; the W1/W2/W4/W5/W6 arithmetic; flat 32/16 secondaries and the 40/20 ↔ 36/18 primary; the 72-DIP dock with no
fill and no shadow; the 1-DIP vs 3-DIP top edge and the 1-DIP centre-line shift; `ProgressBar`'s 40/60 % indicators,
2000 ms loop, `KeySpline(0.4,0,0.6,1)` and hidden track; `Slider.DefaultStyle` rail 4 / r2, ring 22 / r10, dot 12 @ 0.86,
hover ×1.3570 (= 1.167) and press ×0.8256 (= 0.71) at 250 ms; `Motion.ControlFast 167` / `ControlNormal 250` /
`WaveeMotion.Faster 83`; `ScaleEmphatic 1.07/0.92` and `ScaleSubtle 1.02/0.98`; `FluentPopOpen` = cubic-bezier(0,0,0,1)
and `SmoothOut` = (0.22,1,0.36,1); the 83 ms default hover duration; marquee `Speed 18`, `CycleMs 10000`,
`EndPauseMs 2500`, `StartDelayMs 350`, fade band `min(24, 0.3·viewportW)` with smoothstep, and that `Marquee` DOES honour
reduced motion; `Critical` ≈ `#ED6B73`; `WaveeAccent.Decor == Tok.AccentTextPrimary`; `LiveSlotWidth 104`, the 44-DIP
time slot, the 14-DIP LIVE mark and its 6-DIP `SystemFillCritical` dot; `LiveLine` 0.55 ↔ 1 over 3000 ms gated on
playing ∧ window-active ∧ not-reduced; `EnterBehindMs 15000` / `ReturnToEdgeMs 5000` / `ConfirmReports 2`;
`MaxLabelChars 48`; `PlacementCore.TransportOwnerFor` at `:451-458` and the shell's fullscreen unmount at
`WaveeShell.cs:1347`; the 27-field `PlayerBarLayout`; all 41 localisation keys in §6 resolving in
`assets/loc/en-US.json`; `FormatSplitButton` (30×28 + 20×28, Play 11 / ChevronDown 9, `BottomEdgeAlignedRight`) used only
by `TrackVersionsPanel.cs:322`; and `WaveeEqualizer` / `EqualizerSettings` having no player-bar call site.

**token-reconcile (2026-09-12):** `Tok.FillControlStrong` and `Tok.ControlElevationBorder`, both named here but missing from the first build of `00-design-system.md §12.1`, are now indexed there. The chapter's `#ED6B73` transport-error literal (`:613`) already stands as a §12.2 magic-number row and is unchanged here.

**consistency 2026-09-12:** header and §1.2 named only `Shell.UI.cs` + `Shell.cs`. Plan §2 settles the bar into the named partial `+Shell.PlayerBar.UI.cs` (2,000, owner I, Wave 4); `TimeFormat`/`LiveRail`/`LiveEdgeState` land in `Playback/Playback.cs` CORE (owner G, Wave 3); and `PlacementCore`/`PlacementState` land in `Shell/Video.cs` CORE (A13, owner K, Wave 4), not `Playback.cs`. Header, §1.2's heading and CORE line, §7's video-placement rows, the §9 line-budget table and bullets 1 and 6 all corrected to match.
