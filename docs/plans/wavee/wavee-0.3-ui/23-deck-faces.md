# Deck faces (Record, Cassette, CD, iPod, Reel, VU, Winamp, WMP, Canvas) and their machines - 0.3 visual fidelity contract

> 0.2.9 sources (29 files, **5,394 lines**, all under `src/apps/Wavee/Features/Player/Deck/`):
> `NpvDeck.cs` (45) · `DeckHost.cs` (112) · `DeckClock.cs` (267) · `DeckSignals.cs` (54) · `DeckModels.cs` (60) ·
> `DeckFaces.cs` (48) · `DeckArt.cs` (125) · `DeckGesture.cs` (102) ·
> `Model/DeckInput.cs` (96) · `Model/DeckFrame.cs` (132) · `Model/PositionInterpolator.cs` (45) ·
> `Model/SpinIntegrator.cs` (53) · `Model/TonearmMachine.cs` (467) · `Model/RecordModel.cs` (100) ·
> `Model/TapeMachine.cs` (121) · `Model/DiscMachine.cs` (74) · `Model/MeterBallistics.cs` (64) ·
> `Model/LevelSynth.cs` (132) · `Model/ProgressModel.cs` (40) · `Model/DriftPath.cs` (18) ·
> `Faces/RecordDeck.cs` (731) · `Faces/IpodDeck.cs` (444) · `Faces/CassetteDeck.cs` (370) ·
> `Faces/WinampDeck.cs` (355) · `Faces/VuDeck.cs` (330) · `Faces/CdDeck.cs` (309) · `Faces/WmpDeck.cs` (243) ·
> `Faces/ReelDeck.cs` (243) · `Faces/CanvasDeck.cs` (214).
> Plus the option table this surface reads but does not own: `Features/Player/NpvPlayerCatalog.cs` (90) +
> `NpvPlayerPrefs.cs` (48), and six bundled alpha PNGs in `assets/deck/`.
> | 0.3 target: `Shell/Deck.cs` (machines, pure CORE) + `Shell/Deck.UI.cs` (host, clock, art, gesture, record family)
> + **`Shell/Deck.Faces.cs`** (the other eight faces — see §9, the plan's 2,400-line budget is ~2.2× short) | Wave 4 owner K
>
> Cross-references (do not re-specify): tokens, type ramp, cover palette (`CoverColorPlane` / `Surfaces.SchemeFor` /
> `WaveePalette.Accent` / `Lift`), materials, motion curves → `00-design-system.md`; the hero SLOT that mounts a deck,
> the 36-DIP header row, the Cover|Player selector, the player-style flyout, its thumbnails and the artwork context
> menu → `21-right-rail-npv-queue-stage.md` (§1.1:136, W2, W9, W10, §6.3); the rail frame, `RailOpen`/`RailWidth` and
> the docked-video arbitration that REPLACES this slot → `21-…` and `24-video-surfaces.md`; the seek/scrub contract the
> deck grips share with the bar → `20-player-bar.md`; `Motion.ReducedMotion` and the frame clock → `00-design-system.md` §5.
>
> **Doc drift (the CODE wins, one line each).** `npv-player-styles-implementation.md` says the clock reads
> `Environment.TickCount64` (:500,:522) — shipped code reads `FrameTime.NowMs` (`DeckClock.cs:204`, the frame's
> present time); it draws the reel flange as a `PathEl` annulus (:853) — shipped code tints `reel-flange-512.png`
> (`ReelDeck.cs:114`); it draws WMP's Alchemy rings with `ArcSpec` (:884) — shipped code uses hollow-SDF ring `BoxEl`s
> so the stroke colour can be a `Prop` (`WmpDeck.cs:171-182`); it gives `DeckGesture` a 100 ms audible `PreviewSeek`
> pump (:802) — shipped `Drain()` is empty and there is no preview on this branch (`DeckGesture.cs:77`, and the doc's
> own port note :1005-1012 admits it); it spells the host entrance "320 ms opacity+blur" (:44) — shipped is opacity
> only (`DeckHost.cs:33-36`); it adds a 120 ms `1 → 1.006 → 1` disc pop to the needle drop (:824) — shipped fires only
> the three puff tracks (`RecordDeck.cs:601-623`); it gives the VU clocks a `Shadow(6,0,0,amber .6)` glow (:879) —
> `TextEl` has no shadow channel, so shipped paints a radial wash BEHIND the glyphs (`VuDeck.cs:220-230`); it lists an
> `o-body-red` iPod (mockup `.html:304`) — shipped slug is `u2` (`NpvPlayerCatalog.cs:48`). Everything else in the doc's
> Part 3 matches the code constant-for-constant.

---

## 0. The non-negotiables

1. **A deck is a machine, not an animation.** Every moving part goes through a physical integrator with real time
   constants: the platter is a first-order angular-velocity lag (τ↑ 0.23 s / τ↓ 0.53 s = an SL-1200's 0.7 s spin-up and
   1.6 s electronic brake, `RecordModel.cs:17-21`, `SpinIntegrator.cs:40-49`), the tape hubs run at constant LINEAR
   speed so the angular rate falls as the take-up pack grows (`TapeMachine.cs:113-118`), the CD spindle is CLV
   (3000 → 1200 °/s across the disc, `DiscMachine.cs:71`), the VU needles are ballistic (τ 0.3 s VU / 0.05 s PPM,
   `MeterBallistics.cs:53`). Replacing any of these with a linear tween is the regression.
2. **The tonearm tells the transport's whole story in the medium's own vocabulary.** 20 phases and one const block of
   29 names — 28 durations plus `BufferLeadInFrac` — beside `Rpm45Threshold` and `SeekDeadZoneDeg`
   (`TonearmMachine.cs:10-52,105-115`): a pause is a 450 ms cue-lever LIFT then a brake, a skip inside the album is a
   300 ms lift + a 900 ms recue swing that **never stops the platter**, a new album is lift → 1200 ms return → 600 ms
   sleeve-in → 350 ms cover swap → 600 ms slide-out with the brake tripping 700 ms into the return, the queue ending is
   a linear run-out ride, 1800 ms (33⅓) / 1333 ms (45) of locked groove and a 1400 ms auto-return. All of it is in one
   pure `Step`/`Sample` pair with 1,186 lines of tests behind it.
3. **A deck mounted mid-song was already playing.** `TonearmMachine.Seed` (`:131-143`) starts in the groove at
   `AngleOf(frac)` with the platter at speed (`RecordModel.cs:43`); it never replays the 3-second cueing sequence.
   Cover → Player at 2:14 shows a record at 2:14.
4. **The face is built exactly once per mount; the 30 Hz path writes signals only.** Faces are plain static functions,
   not components (`DeckFaces.cs:10-18`); everything that moves is a `Transform`/`Opacity`/`Text` bind over the
   `DeckSignals` slab captured at build (`DeckSignals.cs:8-15`). A tick costs one compositor pass, zero re-renders,
   zero allocations. Any face that re-renders per tick is a rebuild failure, not a style choice.
5. **Every signal write is value-gated at a PERCEPTUAL quantum**: angle at one rim pixel
   (`360/(π·side·0.70)` ≈ 0.505° at side 324, `DeckHost.cs:109`), arm angle at half that, progress at 1/1024, lift at
   0.01, slide/aux at 0.005, bands/peaks at 1/64 (`DeckClock.cs:244-254`). A tick that moves nothing writes nothing,
   the DrawList stays byte-identical and the host elides the Present. That is why a 30 Hz deck is free.
6. **One ticker per deck, and it stops.** `UseInterval` at 33.33 ms gated on ONE bool — `!reduced && railOpen &&
   (playing || buffering || !settled)` (`DeckClock.cs:128-129`) — and `UseInterval` itself ANDs `UseIsActive`
   (minimized / parked). When it is off, a STATE CHANGE is the clock (the same hook, same order, dep-keyed:
   `:133-134`). A model that lies in `IsSettled` either burns a timer forever or freezes mid-animation.
7. **Motion samples the frame clock.** `FrameTime.NowMs` = `FrameClock.PresentQpc` (`FrameTime.cs:22-27`), never
   `Environment.TickCount64`, whose 15.6 ms quantum makes a platter step instead of glide. The `dt` is clamped to
   [0.001, 0.040] s so a resume-from-sleep tick cannot integrate ten minutes into the platter (`DeckClock.cs:209`).
8. **The playhead is interpolated, never the raw 1 Hz report.** `PositionInterpolator` re-anchors on every reported
   position and extrapolates wall-clock in between (`PositionInterpolator.cs:33-39`); a committed seek WINS outright
   so the medium is at the target before the acknowledgement lands.
9. **Somebody else moving the playhead is an EDGE, not a teleport.** A reported jump > 2500 ms with no seek of our own
   is synthesized into a seek target (`DeckClock.cs:40,96-98`), so a Connect device / media key / lock-screen scrub
   makes the arm lift, swing and lower instead of the groove sliding.
10. **The artwork changes when the MECHANISM says so, not when the track changes.** `CoverGen` bumps on the
    `SleeveIn → CoverSwap` edge — the instant the record is fully HIDDEN in its sleeve, not halfway through the slide
    (`TonearmMachine.cs:314-315`), mid-eject (`TapeMachine.cs:79`, `DiscMachine.cs:45`) or on the album edge
    (`ProgressModel.cs:21`); a same-album advance keeps the same cover. Cover leaves are KEYED on the generation, so the
    remount IS the 300 ms cross-fade (`RecordDeck.cs:41-44,578-588`).
11. **Textures instead of gradients the renderer does not have.** Grooves, wood grain, CD rainbow, marble, reel flange
    and the Winamp title stripe ship as bundled alpha PNGs tinted at draw time (`DeckArt.cs:73-84`); every one has a
    hairline-ring / flat-fill fallback underneath so a missing file never paints a hole
    (`DeckArt.cs:83`, `ReelDeck.cs:122`).
12. **The grips are one gesture.** The record's headshell and the iPod's click wheel write the SAME two bridge signals
    (`ScrubTargetMs` live, `SeekTargetMs` committed) through ONE `DeckGesture` per host (`DeckGesture.cs:19-21`); the
    models react to those two facts and know nothing about pointers. The wheel is geared: one full turn = a quarter of
    the track (`IpodDeck.cs:417`).
13. **The deck is a layout firewall.** `Width = Height = side`, `ClipToBounds`, `IsolateLayout`, `Corners = Radii.Card`
    (`DeckHost.cs:76-82`). Nothing inside — a re-rendered cover leaf, a 1 Hz clock `TextEl`, a whole face rebuilt on an
    option flip — can move anything below the hero. "Everything below the hero never moves" is declared, not hoped for.
14. **Reduced motion is a VALUE, never a hook branch.** Every phase collapses to ≤150 ms (`TonearmMachine.cs:121`), the
    platter target goes to 0 (`:379`), the hover bob flattens (`:374`), the Canvas drift is cancelled in place
    (`CanvasDeck.cs:120-127`) — but the 320 ms deck entrance and the 300 ms cover cross-fades are deliberately KEPT,
    because they are cross-fades between two hero contents and snapping them reads as a glitch (`DeckHost.cs:30-32`).
15. **A deck is a physical object, not a themed surface.** The palettes are literal hex (`RecordDeck.cs:32-37`,
    `CassetteDeck.cs:98-104`, …); exactly four things are theme- or cover-bound: the deck ground behind a 7" spindle
    hole and the CD clamp hole (`Tok.FillCardSecondary`), the album-colour vinyl and the WMP accent
    (`WaveePalette.Accent`), and every cover slot's placeholder (`NowPlayingPanel.HeroWashColor`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
NowPlayingHeroTile (Ch.21)                                    Features/Player/NowPlayingPanel.cs:506-566
└── hero slot, Key "deck:<slug>@<int side>"                   :547   side = round((RailWidth − 2·S)/4)·4
    └── NpvDeck.Create(track, preset, side)                   NpvDeck.cs:30-37   `track` is only proof-of-play
        └── Embed.Comp(() => new DeckHost { Preset, Side })   :36   Key = "deck:" + preset.Slug   (:44)
            │
            └── DeckHost : Component                          DeckHost.cs:28-112
                │   re-renders on: prefs Epoch (:62) and context only. A PRESET change is a REMOUNT (the Key).
                │   no PlaybackBridge in context → a BARE BoxEl(side, side, Shrink 0): no corners, no clip,
                │     no IsolateLayout, no entrance, no clock, no face                         :66
                │   owns: DeckSignals _sig (:53) · IDeckModel _model (:54, created ONCE :70) · DeckGesture (:51)
                │   ⚠ the model is created once from the FIRST render's options: `ballistics` (VU) and `vis`
                │     (Winamp) are MODEL options and therefore do NOT apply until a remount — see §6.4(4)
                │   BoxEl  W=H=side · Shrink 0 · ClipToBounds · IsolateLayout · Corners Radii.Card(8)
                │          Animate = Entrance (320 ms opacity, SmoothOut, Enter 0)        :33-36, :75-84
                ├── face = DeckFaces.Create(preset, settings, side, sig, bridge, host)    DeckFaces.cs:28-47
                │     ├ Record | Turntable | Zune | Picture → RecordDeck.Build(…, RecordVariant)   :32-35
                │     ├ Cassette → CassetteDeck.Build   ├ Reel → ReelDeck.Build    ├ Cd → CdDeck.Build
                │     ├ Ipod → IpodDeck.Build           ├ Winamp → WinampDeck.Build ├ Vu → VuDeck.Build
                │     ├ Wmp → WmpDeck.Build             ├ Canvas → CanvasDeck.Build
                │     └ (unknown id) → blank BoxEl(side, side)                                     :46
                └── Embed.Comp(() => new DeckClock{…}) Key "deck-clock"   DeckHost.cs:87-96
                      DeckClock : Component → BoxEl 0×0, HitTestVisible false               DeckClock.cs:136
                        UseEffect(posMs)      ~1 Hz anchor + remote-jump synthesis          :91-101
                        UseEffect(track.Uri)  boundary classify + EAGER Tick                :105-112
                        WAKE subscriptions (read for their edge, never used in Render):     :121-124
                          SeekTargetMs · ScrubTargetMs · Repeat · NpvPlayerPrefs.Epoch
                          — a committed seek, a drag beginning, a repeat flip or a 33→45 flip must re-fold a
                            deck that is paused-and-settled (the ticker is OFF there; the effect below is the clock)
                        UseContext(ShellUi.Slot) → RailOpen (null ⇒ treated as OPEN)        :87,127
                        UseInterval(Tick, 33.33 ms, enabled: run)                           :129
                        UseEffect(!run → Tick) — the state-change clock                     :133-134
                        Tick(): Gesture.Drain → Fold → Model.Tick → Runtime.Batch(WriteCore) → Thump
                                                                                            :200-234

DeckSignals (the ONE channel, a fixed slab)                   DeckSignals.cs:21-54
  Frac 0 · Angle0 0 · Angle1 −34 · Lift 1 · Slide 1 · Aux0 0 · Aux1 0     FloatSignal      :27-33
  Bands[24] · Peaks[24]                                                   FloatSignal[]     :36-39
  CoverGen  Signal<int>      the artwork-swap instant (NOT a track change)                  :43
  Phase     Signal<DeckPhaseName>  medium-agnostic caption/diagnostics                      :46

DeckModels.Create(preset, settings, bridge, seed)              DeckModels.cs:29-46
  Record/Turntable/Zune/Picture → RecordModel(in seed, variant)        TonearmMachine + SpinIntegrator
  Cassette → TapeModel(Cassette)   Reel → TapeModel(Reel)              2 × SpinIntegrator
  Cd → DiscModel()                                                     1 × SpinIntegrator (CLV)
  Vu → MeterModel(ppm: slug "ballistics"=="ppm", Levels(bridge))       2 × first-order lag   ← option frozen here
  Winamp → LevelModel(19, scope: slug "vis"=="scope", Levels(bridge))                        ← option frozen here
  Wmp → LevelModel(24, false, Levels(bridge))
  _ (Ipod, Canvas ONLY — Picture runs RecordModel(Picture), :35) → ProgressModel()      :45
  Levels(bridge): a PULL, `bridge.Levels?.Peek()` — written on the AUDIO PUMP THREAD, never subscribed   :54-59
```

**Face node trees** (each is a `Canvas.Create(side, side, …)`; every child is a `CanvasChild(x, y, el)` in absolute
DIP; `‖` marks a BOUND channel, i.e. a `Prop.Of(...)` thunk over the slab, and `#` marks a keyed leaf `Component`):

```
RecordDeck.Build(variant)                                                    RecordDeck.cs:46-201
├── [Turntable only] wood-grain-1024.png tinted White(1)        :81      texture
├── [Turntable only] plinth gradient #6B4A2E .88 → #4A301B .96  :82-89
├── [Turntable only] felt mat .77s radial #3A3D44→#2B2E34       :92-99   + rings 4 px #1F2126, 2 px #6D7078
├── [Turntable only] strobe box .16s×.05s #1A1C20 + #FF5A2A dot with a 10-blur glow   :104-120
├── [sleeve on, non-Zune] sleeve .64s, rot −2.5° (−4° Turntable), Fill Black(.35), Shadow(30,10,0,Black .25)
│   └── # SleeveArt  Key "sleeve-art"  → keyed on CoverGen, 300 ms CoverFade         :127-139, :559-589
├── Platter(d) — the slide-out wrapper                                      :207-238
│   ‖ Transform  Translation(−0.34·d·(1−SlideOut(Slide)), 0) · Scale(0.96+0.04·e)   :223-228
│   ‖ Opacity    Clamp01(Slide · 1.6)                                                :230
│   ├── Disc(d) — the ONE rotating node  ‖ Transform Rotation(Angle0·π/180)          :356-366
│   │   ├ [Picture] # SleeveArt round "picture-art" + Black(.10) wash, 1 px White(.06), Shadow(34,14,0,Black .45)
│   │   ├ [else]    body circle, Fill per finish ‖(album: AlbumVinyl thunk)           :269-290, :382-391
│   │   ├ [splatter] 6 static radial dots .04d at (.30,.25)(.70,.40)(.60,.78)(.24,.66)(.82,.70)(.45,.12)  :292-301
│   │   ├ [marble]  marble-1024.png tinted White(.9)                                 :304-305
│   │   ├ grooves-1024.png tinted per finish (White .12/.08/.035, Black .08/.18)      :309-316
│   │   ├ sheen: radial 3-stop White(.10)→transparent@.55→White(.06), centre (.30,.25) r (.9,.9)  :321-331
│   │   ├ [non-Picture] # SleeveArt round .34d "label-art" + ring 2 px Black(.6) (Zune 3 px White .9)  :335-337
│   │   └ spindle .05d #E6E9EF + 1 px Black(.5)  |  7": hole .20d filled with deckBg ‖   :341-354
│   └── # RecordThump Key "thump" — .08d ring, Opacity 0 at rest, 3 one-shot tracks    :234-235, :594-644
├── [non-Zune] Tonearm(...)  ‖ outer Rotation(Angle1)                       :399-527
│   └── lift wrapper ‖ Translation(0, −0.015·armH·l) · Scale(1+0.015·l), l = min(Lift, 1.4)   :444-449
│       ├ shadow-only tube twin ‖ Opacity 0.45·Clamp01(Lift)   (ShadowSpec is not bindable)   :454-463
│       ├ tube (gradient), pivot (radial + Shadow(8,3,0,Black .35)), headshell (rot −18°) + red stylus 2×8
│       └ hit box 0.44·armW × 0.20·armH, transparent, CursorId.Hand, OnPointerDown/OnDrag/OnPointerReleased :502-512
├── [non-Zune] arm rest post .03s × .09s                                    :150-157
└── [Zune] # ZuneType Key "zune-type:<accent>" + 3 px progress rail ‖ Scale(Frac,1)   :164-193, :649-730

CassetteDeck.Build                                                           CassetteDeck.cs:28-164
└── ground gradient #2B2F3A → #1C1F27                                        :155-161
    └── shell wrapper ‖ Translation(0, −0.4·s·(1−Slide)) ‖ Opacity Slide      :143-151
        └── shell body .86s × .56s at (.07s,.21s), Corners 6, Fill/Acrylic per option, Shadow(30,14,0,Black .5)
            ├── label surface .90×.30 of the shell + # CassetteLabel Key "cassette-label:<style>"  :54-68, :302-368
            ├── side badge A|B, .10·labelW circle                              :117-118, :265-289
            ├── window .62×.34 of the shell, #14151A, 2 px White(.08), ClipToBounds   :71-90
            │   ├── Hub L at 31 % shellW: pack .26s ‖ Scale(Aux0) + reel .11s ‖ Rotation(Angle0)   :168-217
            │   └── Hub R at 69 % shellW: pack ‖ Scale(Aux1) + reel ‖ Rotation(Angle1)
            ├── tape strip .60×.025 #3A2B1C · foot .48×.16 with 4 drive holes · 4 screws ·
            └── "wavee · c-60 · type i" .018s                                  :121-136

ReelDeck.Build                                                               ReelDeck.cs:28-80
└── ground #2A2D33 → #1B1D22 · 2 × Reel(.40s) at y .07s, x .06s / s−.06s−reel
    │   Reel = pack .92·d ‖ Scale(Aux0|Aux1) → flange reel-flange-512.png ‖ Rotation(Angle0|Angle1)
    │          → fallback rim ring .012s White(.22) → hub .22·d radial #E9EBEF→#8A8F99      :84-136
    ├── 2 guides .05s · head block .36s × .18s with 3 heads                   :139-184
    ├── threaded tape PathEl "M26 44 L28 74 L36 80 L64 80 L72 74 L74 44", 100-unit view box,
    │   stroke #5A4028 w 1.2 (view units ⇒ ×side/100)                         :25-26, :66-77
    └── counter .20s × .072s #1A0D00 → # ReelCounter Key "reel-counter" ‖ bound 4-digit text  :186-241

CdDeck.Build (format: cd | md)                                               CdDeck.cs:25-67
├── ground #22252C → #14161B · caption "COMPACT DISC · DIGITAL AUDIO" | "MD · ATRAC", CharSpacing 180
├── [cd]  tray .72s at (.14s,.12s) ‖ Translation(0, .3s·(1−Slide)) ‖ Opacity Slide   :80-88
│         └── Disc(d) ‖ Rotation(Angle0): plate #101216 → # DiscArt Key "disc-art" → cd-rainbow-512.png
│             @ Opacity .75 → inner ring .23d → clamp hole .17d filled Tok.FillCardSecondary  :207-259
│         rail 1 × .47d White(.25) · sled .03d #FF3B30 + Shadow(8,0,0,#FF3B30 .85) ‖ Translation(0, Aux0·d)  :95-111
└── [md]  cartridge .62s × (72/68) with a rect window .50×.56 clipping a disc at 112 % of the window,
          chrome shutter, paper strip → # MdLabel Key "md-label"               :117-201, :282-308

IpodDeck.Build                                                               IpodDeck.cs:35-91
└── ground #3A3F4A → #22252C
    ├── body .58s × .96s at (.21s,.02s), Corners .055s, gradient per body, Shadow(34,16,0,Black .5)
    ├── Lcd(w=.84·bodyW, h=.43·bodyH)                                         :95-190
    │   ├ title bar .14h gradient #F4F6F8→#C7CDD6 + "Now Playing" 700 + 1 px #8B93A3 rule
    │   ├ transport glyph ‖ bound Text (Icons.Play | Icons.Pause) · battery .09w × .44·barH
    │   ├ # IpodScreen Key "ipod-screen" — art .34w + 3 metadata lines (re-renders on TRACK)  :147-150, :235-293
    │   ├ progress trough .92w × .08h + fill ‖ Scale(frac(),1) + diamond thumb ‖ Translation(frac()·progW, 0)
    │   └ 2 clocks, 1 Hz bound Text, FIXED width .30w                         :182-183, :194-205
    └── # IpodWheel Key "ipod-wheel:<body>" .74·bodyW — MENU / ⏮ / ⏭ / ⏯ captions + centre button
        angular scrub on the wheel itself (Role Slider), each caption its own hit node   :77-84, :307-444

VuDeck.Build                                                                 VuDeck.cs:44-73
└── ground #1A1B20 → #0F1013
    ├── Meter(LEFT) ‖ Rotation(Angle0) · Meter(RIGHT) ‖ Rotation(Angle1)      :65-66, :77-165
    │   lamp radial wash → InkScale PathEl → RedArc PathEl → RedTicks PathEl → 10 numeric TextEls
    │   → "LEFT"/"RIGHT" → "VU" → needle 1.5 × .78h origin (.5,1) → pivot cap .08w
    ├── knob .09s at (.5, .60s)                                               :67, :250-265
    └── amber LCD .90s × .20s: 2 clocks (bound, 1 Hz) + NOW line + FORMAT cell  :68, :169-234

WinampDeck.Build                                                             WinampDeck.cs:35-56
└── ground #0F1218 · two 275:116 windows at x .04s, w .92s
    ├── MainWindow at y .10s   — title bar (winamp-tbar.png tinted wa3) · time LCD (bound) ·
    │   vis box → Bars(19) | Scope(24) · stitle Marquee · kbps/kHz/STEREO cells · position bar +
    │   thumb ‖ Translation(Frac·(posW−12), 0) · 5 transport keys                :60-137
    └── EqWindow at y .10s + h + .02s — title bar + the SAME 19 bands at a larger geometry + 10-line grid  :141-162

WmpDeck.Build (preset: bars | alchemy | battery)                             WmpDeck.cs:31-100
└── ground #04060C
    ├── BarsAndWaves: 24 bars ‖ Scale(1, band) above the mid line + 24 reflections @ .35 +
    │                 32 wave dots ‖ Translation(0, (peak−.5)·.36s)             :106-149
    ├── Alchemy: 3 rings × 3 trails ‖ Rotation(Frac·τ·6(r+1) − .15·trail) · Scale(s, .8s)  :156-187
    ├── Battery: 6×6 cells ‖ Scale(.15 + .7·band)                               :192-219
    ├── corner cover .22s ‖ bound ImageEl Source + Shadow(20,8,0,Black .6)       :57-71
    ├── preset caption (Loc.Bind of the choice LabelKey), CharSpacing 140        :72-84
    └── 2 px progress line ‖ Scale(Frac,1), Fill ‖ colour()                      :85-97

CanvasDeck.Build                                                             CanvasDeck.cs:32-63
└── ground #05070C
    ├── # CanvasArtwork Key "cv:<slow|fast>:<soft|strong>"  → inner Key "cv:<gen>"  :43-46, :76-186
    │   ├ bleed 1.5s at (−.25s,−.25s): BakedBlur(44,.25), Saturation 1.5, DecodePx 256, Opacity 1 | .6
    │   └ drift frame .78s at (.11s,.11s), Corners 6, Shadow(60,24,0,Black .45), ClipToBounds
    │     └ 118 % wrapper — the ANIMATED node (OnRealized captured), 4 looping keyframe tracks   :161-177
    └── seek line .78s × 2 built OUTSIDE the component so its bind survives every album change   :49-61
```

### 1.2 The same tree in 0.3 terms

`Shell/Deck.cs` is CORE (engine-free, unit-tested); `Shell/Deck.UI.cs` and `Shell/Deck.Faces.cs` are UI. Props freeze
at mount — the column marked **how data reaches it** is the only way a value can change after that.

| 0.2.9 node | 0.3 home | shape | inputs | how data reaches it |
|---|---|---|---|---|
| `DeckInput`, `DeckBoundary`, `DeckBoundaryRules`, `PlaybackPhase` | `Deck.cs` CORE | `readonly record struct` + static class | `in Playback.State`-derived scalars | value, folded per tick |
| `DeckFrame`, `DeckPhaseName`, `IDeckModel`, `DeckEase`, `RecordVariant` | `Deck.cs` CORE | records / interface / static | — | — |
| `PositionInterpolator`, `SpinIntegrator` | `Deck.cs` CORE | `struct` | — | — |
| `TonearmMachine` + `TonearmState/Frame/Phase`, `TonearmGeometry` | `Deck.cs` CORE | static + `readonly record struct` | `in DeckInput` | — |
| `RecordModel`, `TapeModel`, `DiscModel`, `MeterModel`, `LevelModel`, `ProgressModel`, `DriftPath` | `Deck.cs` CORE | `sealed class : IDeckModel` | ctor options + `Func<(rms,peak)?>` | the level tap is a PULL delegate (never a subscription) |
| `DeckModels.Create` | `Deck.cs` CORE (`Deck.ModelFor`) | static switch | preset id, settings, level tap, seed | — |
| `DeckSignals` | `Deck.UI.cs` | `sealed class`, fixed slab | — | the ONE channel; faces bind, clock writes |
| `DeckClock` | `Deck.UI.cs` | `sealed class : Component` | `Playback` signals, `IDeckModel`, `DeckSignals`, `DeckGesture`, `Func<float> Rpm`, `Func<float> AngleQuantumDeg` | **required props are Funcs, not values** — rpm and the quantum must be re-read per tick |
| `DeckHost` | `Deck.UI.cs` | `sealed class : Component` | `Preset` (frozen), `Side` (frozen), context | preset change = **Key remount**; option flip = prefs `Epoch` re-render (face rebuilt in place); rail resize = Key remount at 4-DIP quantisation |
| `DeckGesture` | `Deck.UI.cs` | `sealed class` | scrub/seek signals + `CommitSeek` | one per host, handed to faces as `host.Gesture` |
| `DeckArt` | `Deck.UI.cs` | static | url, size, corners, placeholder, blurhash | `Prop.Of` thunks for the two cover-derived colours |
| `DeckFaces.Create` | `Deck.Faces.cs` | static switch | preset, settings, side, sig, playback, host | rebuilt whole on an option flip; NEVER on a track change |
| `RecordDeck` + `SleeveArt` / `RecordThump` / `ZuneType` | `Deck.Faces.cs` | static + 3 nested `Component`s | `DeckSignals`, `DeckHost` | `SleeveArt` keys on `CoverGen`; `RecordThump` subscribes `host.ThumpRequested` in `UseLayoutEffect`; `ZuneType` reads `CoverGen` + the live track |
| `CassetteDeck` + `CassetteLabel` | `Deck.Faces.cs` | static + nested | same | label keyed `"cassette-label:" + style` (style is a frozen field) |
| `ReelDeck` + `ReelCounter` | `Deck.Faces.cs` | static + nested | same | counter renders once; the playhead arrives as a bound `Prop<string>` |
| `CdDeck` + `DiscArt` / `MdLabel` | `Deck.Faces.cs` | static + nested | same | both keyed leaves read `CoverGen` |
| `IpodDeck` + `IpodScreen` / `IpodWheel` | `Deck.Faces.cs` | static + 2 nested | same | wheel keyed `"ipod-wheel:" + body`; screen re-renders on TRACK |
| `WinampDeck`, `VuDeck`, `WmpDeck` | `Deck.Faces.cs` | static only | same | zero components: every state is a bound channel |
| `CanvasDeck` + `CanvasArtwork` | `Deck.Faces.cs` | static + nested | same | outer Key carries the two options; inner Key is `CoverGen` |
| `NpvPlayerCatalog` / `NpvPlayerPrefs` | **`Platform/Design.cs` CORE** (proposed — see §9) | static tables + clamped accessors + `Signal<int> Epoch` | `IAppSettings` | every read subscribes `Epoch`; every write clamps, persists, logs, bumps once |

**Which options reach a MOUNTED deck, and how.** Three different lifetimes — the re-author must keep them straight:

| option class | examples | how it applies in 0.2.9 | 0.3 rule |
|---|---|---|---|
| face geometry / palette | finish, size, sleeve, shell, label, side, reel, format, body, lcd, skin, face, preset, colour, drift, bleed, accent | prefs `Epoch` re-renders `DeckHost` → the face is REBUILT in place (keyed leaves carrying the option remount) | keep |
| live per-tick delegate | `rpm` (33⇄45) | `DeckClock.Rpm` is a `Func<float>` re-read every tick; `RecordModel.ApplySpeedRamp` slews both τ to 0.17 s for 600 ms | keep |
| MODEL construction | `ballistics` (VU τ 0.3 ⇄ 0.05 s), `vis` (Winamp spectrum ⇄ scope synth) | **frozen at model creation** (`DeckHost.cs:70` only builds when `_model is null`) — flipping them restyles the FACE but leaves the old physics until a remount | **fix in 0.3**: put both in the host Key, or rebuild the model when the option hash changes (§6.4(4)) |

---

## 2. Wireframes

Scale for every deck wireframe: **1 char = 8 DIP wide, 1 row = 16 DIP tall**, so the default 324-DIP square is
40 chars × 20 rows. Coordinates in the margins are DIP at `side = 324`; the formula beside each is the general one.

### W1 — Record (default preset), playing, 12" LP, sleeve on, black finish @ side 324

```
 x:0        77.8                        304.6      formula (side = s)
 ┌────────────────────────────────────────┐  y 0    deck  BoxEl s×s, Corners 8, ClipToBounds, IsolateLayout
 │                                  ▮     │  19.4   rest post .03s×.09s at (.845s,.06s), Corners 3
 │   ┌──────────────────────┐      ╭┼╮    │  38.9   sleeve .64s at (.06s,.12s), rot −2.5°, Corners 4
 │   │ sleeve art (.64s)    │      │┃│    │         arm box .22s × .92s at (.75s,.02s), origin (.5,.08)
 │   │  ╭───────────────────┼──╮   │┃│    │  51.8   disc D = .70s at (.24s,.16s)   [7": .52s at (.33s,.25s)]
 │   │  │      ·grooves·    │  │   │┃│    │         pivot ⌀ .44·armW centred on the origin
 │   │  │   ╭───────────╮   │  │   │┃│    │
 │   │  │   │  label    │   │  │   │┃│    │  116    label = .34·D, cover art, ring 2 px Black(.6)
 │   │  │   │  ⊙ .05D   │   │  │   │┃│    │  155    spindle .05D #E6E9EF (7": .20D hole = deck ground)
 │   │  │   ╰───────────╯   │  │   │┃│    │
 │   └──┼───────────────────╯  │   │┃│    │  194
 │      │      ·sheen·         │   │▚│    │         sheen radial (.30,.25) White .10 → 0 → White .06
 │      ╰─────────────────────╯    │▚│    │  246    headshell .22·armW × .12·armH at (.39,.69), rot −18°
 │                                 ╰┴╯    │  278.6  ↑ hit box .44·armW × .20·armH at (.28,.64) = 31×60 DIP
 │                                        │
 └────────────────────────────────────────┘  y 324
   arm angle = −20 + 17·frac   (rest −34)      platter ω = rpm·6 → 200 °/s @33⅓, 270 °/s @45
```

Bound channels on this frame: `Angle0` → disc `Rotation`; `Angle1` → arm `Rotation`; `Lift` → the lift wrapper's
translate+scale AND the shadow twin's opacity; `Slide` → the platter wrapper's translate+scale+opacity. Four writes a
tick, three of them usually elided by the quantum.

### W2 — Record, PAUSED (arm lifted in place, platter braking) @ 324

```
 ┌────────────────────────────────────────┐   Lift 0 → 1 over LiftMs 450 on cubic-bezier(.2,0,0,1)
 │                                  ▮     │   the arm does NOT return to the rest: FromDeg = ToDeg = armNow
 │   ┌──────────────────────┐      ╭┼╮    │   lift wrapper: translate −0.015·armH·l = −4.5 DIP at l = 1
 │   │                      │      │┃│    │                  scale 1 + 0.015·l = 1.015
 │   │  ╭───────────────────┼──╮   │┃│    │   shadow twin opacity 0 → 0.45 (10 blur, 10 dy, Black .45)
 │   │  │  (platter slowing)│  │   │┃│    │   platter: target 0, τ↓ 0.53 s ⇒ 95 % braked by 1.6 s,
 │   │  │   ╭───────────╮   │  │   │┃│    │            snapped to an exact 0 below 0.05 °/s
 │   │  │   │  label    │   │  │   │┃│    │   phase name: Pausing → Paused
 │   │  │   ╰───────────╯   │  │   │┃│    │   ticker: keeps running until IsSettled (platter at rest), then
 │   └──┼───────────────────╯  │   │▚│    │            stops — a paused deck costs ZERO frames
 │      ╰─────────────────────╯    │▚│    │
 │                                 ╰┴╯    │   resume: SpinUp 700 ms (platter on, arm still up) → Lower 900 ms
 └────────────────────────────────────────┘            on cubic-bezier(.2,.6,.3,1) → Tracking + ONE Thump
```

### W3 — Record, BUFFERING (hover bob) and the two seek poses @ 324

```
 hover (Buffering from Tracking/Lower/SpinUp)         lift = 1 + 0.4·(0.5 − 0.5·cos(2π·t/1800))
 ┌────────────────────────────────────────┐           ⇒ 1 → 1.4 → 1 every 1800 ms, clamped at 1.4 by the
 │   platter KEEPS TURNING (PlatterOn)     │            face (min(Lift,1.4)); the stylus never touches down
 │   arm hangs at armNow, bobbing ↕        │           at frac < 0.02 the arm waits over the LEAD-IN instead
 │                                         │           reduced motion: flat 1.0, no bob
 └────────────────────────────────────────┘           clears ONLY when !Buffering && Advancing → Lower 900 ms

 committed seek from Tracking                          committed seek while Paused
 ┌───────────────────┐                                 ┌───────────────────┐
 │ SeekLift 400 ms   │  lift to 1                      │ (already up)      │
 │ SeekSwing 300 +   │  600·|Δ|/17 ms, ease Std        │ SeekSwing straight across, stays up
 │ SeekLower 700 ms  │  damped descent, Thump          │ → Paused at ToDeg │
 └───────────────────┘                                 └───────────────────┘
 |Δ| = full span (17°) ⇒ 900 ms swing · |Δ| = quarter span ⇒ 450 ms · |Δ| < 0.15° ⇒ NO movement at all

 GUARDS (both are silent, both are deliberate — `TonearmMachine.cs:214,230`):
 · a committed seek is honoured ONLY from Tracking or Paused. Arriving during Cue / SwingToLead / Lower / SpinUp /
   SleeveIn / RunOut it is IGNORED as an edge — the leg finishes and `Tracking` then simply draws `AngleOf(frac)`
   at the new position (the medium arrives without a swing). Do not "fix" this into a queued seek.
 · a headshell drag needs `RecordOut`: dragging while the record is in its sleeve (Idle / mid-change) does nothing.
 · `DurationMs == 0` (unknown / live): `Frac` is 0, the arm parks at the lead-in, and the record grip's `MsAt`
   returns 0 — so a release COMMITS a seek to 0. The wheel refuses instead (`IpodDeck.cs:394,406`). 0.3 must make
   both grips refuse: gate the grip on `DurationMs > 0`, not only on `CanSeek`.
```

### W4 — Record, headshell DRAG in progress @ 324 (the scrub gesture)

```
 ┌────────────────────────────────────────┐   1. OnPointerDown on the 31×60 hit box (CursorId.Hand)
 │                                  ▮     │      MsAt(p): arm-local → deck space (ArmLocalToDeck, origin
 │   ┌──────────────────────┐      ╭┼╮    │      (.5,.08), arm angle Peeked) → FracFromDeckPoint →
 │   │                      │      │┃│    │      frac·DurationMs → Gesture.Begin(ms)
 │   │  ╭──────────────╮    │      │┃│    │   2. the machine sees ScrubTargetMs ≠ null → SkipLift 300 ms
 │   │  │   ╭──────╮   │    │   ╭──┤┃├──╮ │      → Dragging; arm = AngleOf(FracOf(ScrubTargetMs))
 │   │  │   │label │   │  ← │   │ ▓▓▓▓▓ │ │   3. OnDrag → Gesture.Move: ScrubTargetMs rewritten each sample
 │   │  │   ╰──────╯   │    │   ╰──┬┃┬──╯ │      (the hit box is INSIDE the rotating arm, so it follows)
 │   └──┼──────────────╯    │      │▚│    │   4. OnPointerReleased → Gesture.Commit(grip.LastMs):
 │      ╰───────────────────╯      ╰┴╯    │      SeekTargetMs = target; CommitSeek(target); scrub cleared
 └────────────────────────────────────────┘   5. Dragging + ScrubTargetMs null → SeekSwing from ToDeg (where
   rim = position 0 · label edge = position 1      the headshell WAS) to the committed target, then Lower 700
   groove band = .34R … 1.0R of the record         if PlayWhenReady, else park Paused at the target
```

No audible preview while dragging on this branch: the medium follows the finger, the audio moves once on release
(`DeckGesture.cs:16-18`). A drag is abandoned silently if the track or the active device changes under it (`:90-93`).

### W5 — Record, CHANGING RECORD (a new album) — the full 5.6 s choreography @ 324

```
 t=0     Lift 450 (300 if the user skipped)   arm rises from armNow
 t=450   SwingToRest 1200, ease Std           arm travels to −34°; brake trips at t = 450+700 = 1150
 t=1650  SleeveIn 600                         Slide 1 → 0: the record slides BACK into the sleeve,
 ┌────────────────────────────────────────┐   translate −0.34·D·(1−e) and scale 0.96..1.0 with it
 │   ┌────────────────╮ ← record returning │   opacity Clamp01(Slide·1.6) fades it out over the last 40 %
 │   │  ╭─────────────┼──╮      ▮          │
 │   │  │  disc       │  │     ╭┼╮ arm at  │  t=2250  CoverGen++  ← the artwork changes HERE, mid-sleeve
 │   │  ╰─────────────┼──╯     │┃│ its rest│          CoverSwap 350: sleeve/label/Zune type re-render and
 │   └────────────────╯        ╰┴╯         │          cross-fade (300 ms opacity, Enter 0, SmoothOut)
 └────────────────────────────────────────┘  t=2600  SlideOut 600 on cubic-bezier(.2,.7,.2,1) (if playing)
 t=3200  Cue 300 (platter on) → SwingToLead 1200 → Lower 900 → Tracking + Thump   (cold start total 3000 ms)
 TOTALS: 450+1200+600+350+600+300+1200+900 = 5600 ms to Tracking on a natural run-through, 5450 ms when the user
 SKIPPED (SkipLiftMs 300 instead of LiftMs 450). "Cold start 3000 ms" is only the tail from SlideOut onward.
 Same album, next track: Lift 450|300 → RecueSwing 900 → Lower 900 (2250|2100 ms). The platter NEVER stops.
 CoverGen unchanged.
```

### W6 — Record, QUEUE END → run-out → locked groove → auto-return @ 324

```
 RunOut     400 + 800·|Δ|/17 ms, LINEAR (a groove, not a swing), lift stays 0 — the stylus is still down
 LockedGroove  1800 ms @ 33⅓ · 1333 ms @ 45 (Rpm > 40 is the 45 test), arm parked at −3°
 Lift       500 ms, platter brake scheduled at +500 into the swing
 SwingToRest 1400 ms, ease Std → Stopped: record still on the platter, arm on its rest, platter braked
 ┌────────────────────────────────────────┐
 │                                  ▮     │   Phase names, in order: Playing → RunOut → LockedGroove →
 │      ╭──────────────────────╮   ╭┼╮    │   AutoReturn (both the Lift and the SwingToRest, because
 │      │   disc still out     │   │┃│    │   AfterRest == Stopped) → Stopped
 │      ╰──────────────────────╯   ╰┴╯    │   Pressing play from Stopped is a PLAIN CUE (300 ms) —
 └────────────────────────────────────────┘   no sleeve, no slide-out: the record never left.
```

### W7 — Record, ERROR / unavailable, and NO TRACK @ 324

```
 error (bridge Error ≠ null)                     no track (HasTrack false)
 Lift 450 → SwingToRest 1200 (ErrorSwing),       Lift 450 → SwingToRest 1200 → SleeveIn 600 → Idle
 platter off WITH the swing (offset 0)           record in the sleeve, platter off, arm on the rest
 ┌────────────────────────┐                      ┌────────────────────────────────────────┐
 │  record ON the platter │                      │   ┌──────────────────────┐        ▮    │
 │  arm at rest, stopped  │                      │   │  sleeve (art only)   │       ╭┼╮   │
 │  Phase = Unavailable   │                      │   │                      │       │┃│   │
 └────────────────────────┘                      │   └──────────────────────┘       ╰┴╯   │
 recovery: !Error && PlayWhenReady → Cue 300     └────────────────────────────────────────┘
 (no sleeve step — the record never left)        In practice the hero slot UNMOUNTS the deck when the track
                                                 goes away (`NowPlayingHeroTile` returns an empty BoxEl,
                                                 NowPlayingPanel.cs:528) — this pose is reachable only when
                                                 the track clears while the deck is alive.
```

### W8 — Turntable variant @ 324 (the same machine, a plinth around it)

```
 ┌────────────────────────────────────────┐  y 0     wood-grain-1024.png tinted White(1), then a vertical
 │  ┌──────────────────────────────────╮  │          stain #6B4A2E @ .88 → #4A301B @ .96 OVER it, Corners 8
 │  │      ╭────────────────────╮   ▮  │  │  40.5   felt mat .77s at (.205s,.125s), radial #3A3D44→#2B2E34
 │  │     ╱  ╭──────────────╮    ╲ ╭┼╮ │  │          + ring 4 px #1F2126 (outer) + ring 2 px #6D7078 (inset 8)
 │  │    │   │   record     │     ││┃│ │  │  51.8   the record sits on the mat, same geometry as W1
 │  │    │   │  ╭──────╮    │     ││┃│ │  │
 │  │    │   │  │label │    │     ││┃│ │  │         arm ink is CHROME here: tube gradient #5D6470 → #F2F4F7
 │  │    │   │  ╰──────╯    │     ││┃│ │  │         @.4 → #C3C8D1 @.6 → #5D6470; headshell #2B2E34 with a
 │  │     ╲  ╰──────────────╯    ╱│▚│ │  │         1 px #7A808A border; rest post #C9CED8
 │  │      ╰────────────────────╯ ╰┴╯ │  │  246
 │  │ ▪▪▪▪▪▪▪▪▪▪▪▪▪▪▪▪▪▪▪▪ ●           │  │  278.6  strobe window .16s × .05s at (.08s,.86s), #1A1C20,
 │  └──────────────────────────────────╯  │          Corners 2, with a .025s #FF5A2A dot + Shadow(10,0,0)
 └────────────────────────────────────────┘  324     (the dot does not blink — it is a lit pitch lamp)
 sleeve rotation is −4° here (−2.5° on the bare Record), deck ground = #4A301B
```

### W9 — Zune variant @ 324 (no arm, no sleeve, no rest)

```
 ┌────────────────────────────────────────┐  y 0     deck ground #000000, Corners 0 (the host still clips to 8)
 │                        listen to your  │  25.9   type block at (.62s,.08s), width .34s
 │  ╭──────────────────╮  ┌─────────┐     │          title lowercase, .11s = 35.6 DIP, weight 300,
 │ ╱   ·grooves·        ╲ │ h e a r │     │          "Segoe UI Variable Display"; the LAST THIRD of the
 ││   ╭────────────╮     ││   t     │     │          string carries the accent colour as a SECOND run
 │││  │   label    │    │││ artist ·│     │  71.3   second line .036s = 11.7 DIP, #9AA1AD, then a FIXED
 │││  │    ⊙       │    │││  0:00   │     │          .10s slot holding the 1 Hz bound clock
 │ ╲   ╰────────────╯   ╱ └─────────┘     │         disc D = .62s at (.08s,.22s) — no 7"/12" choice here
 │  ╰──────────────────╯                  │  222.6
 │                                        │
 │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░     │  285.1  bar .86s × 3 DIP at (.08s,.88s): track #333333,
 └────────────────────────────────────────┘  324     fill = accent ‖ Scale(Frac,1) about origin (0,.5)
 accents: pink #F0568C (default) · orange #FF7A1A · green #8CBF26 · blue #1BA1E2  (flipping one REMOUNTS the block)
 the label ring is 3 px White(.9) here instead of 2 px Black(.6)
```

### W10 — Picture disc and the 7" single @ 324

```
 Picture (variant)                                7" single (size option, Record/Turntable/Picture)
 ┌────────────────────────┐                       ┌────────────────────────┐
 │  the cover IS the disc │  no body fill,        │   D = .52s at (.33s,.25s)   — smaller, further right
 │  Black(.10) wash over  │  no label, no         │   spindle hole = .20·D FILLED WITH THE DECK GROUND
 │  it, 1 px White(.06),  │  grooves texture;     │   (Tok.FillCardSecondary bound, or the plinth brown),
 │  Shadow(34,14,0,.45),  │  spindle is WHITE     │   so the hole genuinely shows what is underneath
 │  sheen still rotates   │  and the ring is      │   grooves/sheen/label unchanged
 └────────────────────────┘  drawn on the label   └────────────────────────┘
 Sleeve OFF (either family): the record moves to px = .15s — it sits where the sleeve was, not where it landed.
```

### W11 — Cassette @ 324, playing, black shell, Type I label

```
 x:0   22.7                                301.3
 ┌────────────────────────────────────────┐  y 0     ground #2B2F3A → #1C1F27 (vertical)
 │                                        │  68.0   shell .86s × .56s at (.07s,.21s), Corners 6,
 │   ┌────────────────────────────────┐   │          Fill #1D1F24, Shadow(30,14,0,Black .5)
 │   │ ⊙ ┌──────────────────────┐  ⊙  │   │  78.9   label .90 × .30 of the shell at (.05,.06)
 │   │   │ TRACK TITLE       ╭─╮│     │   │          title  H·.26 = 14.1 DIP, weight 700, Courier New
 │   │   │ artist · album    │A││     │   │          second H·.20 = 10.9 DIP @ #23262B .72
 │   │   └──────────────────────┘     │   │          badge ⌀ .10·labelW = 25.1 at the label's right edge
 │   │      ┌────────────────────┐    │   │  140.6  window .62 × .34 of the shell at (.19,.40), #14151A,
 │   │      │  ╭───╮      ╭───╮  │    │   │          2 px White(.08), Corners 5, ClipToBounds
 │   │      │  │ ✳ │      │ ✳ │  │    │   │          hubs at 31 % / 69 % of shellW, vertically centred
 │   │      │  ╰───╯      ╰───╯  │    │   │          pack ⌀ .26s = 84.2 ‖ Scale(Aux0|Aux1)
 │   │      └────────────────────┘    │   │  202.3  reel ⌀ .11s = 35.6, 3 spokes at 0/60/120°, white ring
 │   │ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬       │   │  215.0  tape strip .60 × .025 of the shell, #3A2B1C
 │   │ ⊙  ┌──────────────────┐    ⊙   │   │  220.8  foot .48 × .16 with 4 drive holes, Black(.25)
 │   │ wavee · c-60 · type i │    │   │   │  224.0  brand text .018s = 5.8 DIP
 │   └────────────────────────────────┘   │  249.4
 └────────────────────────────────────────┘  324
 pack radii (% of s): rL = 26 − 13p, rR = 13 + 13p  ⇒ Aux0 = 1 − 0.5p, Aux1 = 0.5 + 0.5p
 ω = −wind·900/r °/s: at p=0 the supply hub turns 34.6 °/s and the take-up 69.2 °/s; they swap by p=1
```

### W12 — Cassette, EJECT on a new album, and the four shells @ 324

```
 t=0     Slide 1 → 0 over 500 ms: the whole shell rises 0.4·s = 129.6 DIP and fades to 0
 t=500   CoverGen++ → the label re-letters (keyed remount, not a text write)
 t=1000  Slide 0 → 1: it drops back in
 ┌────────────────────────────────────────┐   shells:
 │      ┌────────────────────────────┐    │   black  #1D1F24 flat
 │      │  shell, rising + fading    │    │   clear  #BECDE6 @ .28 + AcrylicSpec(#BECDE6, .28, blur 18,
 │      └────────────────────────────┘    │          noise .02, lum .92) + 1 px White(.35) border
 │                                        │   cream  #E9E2D0 flat (brand text goes Black .40)
 │        (the slot it came out of)       │   smoke  #464854 @ .85 flat
 └────────────────────────────────────────┘   labels: type1 = Courier New on #F2E9D8 · chrome = UPPERCASE
 The eject is TapeModel's own two-stage timer, not the tonearm machine's.    Segoe UI on a #E8E8EC→#B9BCC4
 A committed seek (or a > 2.5 s jump) instead fast-winds ×4 for 900 ms.       gradient · hand = Segoe Print on
                                                                             #FFFDF5 with 3 hairline rules #9FC0E8
```

### W13 — Reel-to-reel @ 324

```
 x:0   19.4                 175.0        304.6
 ┌────────────────────────────────────────┐  y 0     ground #2A2D33 → #1B1D22
 │                                        │  22.7   reels ⌀ .40s = 129.6 at y .07s, x .06s and s−.06s−d
 │  ╭──────────────╮      ╭──────────────╮│          pack ⌀ .92·d = 119.2 ‖ Scale(Aux0|Aux1), #4A3520
 │ ╱ ╱▔▔▔▔▔▔▔▔▔╲ ╲       ╱ ╱▔▔▔▔▔▔▔▔╲ ╲ ││          flange = reel-flange-512.png ‖ Rotation, tinted
 ││ │ ╭───────╮ │ │     ││ │ ╭──────╮│ │ ││          alu #C9CDD4 | black #2A2C33 | clear #C8D7F0 @ .45
 ││ │ │  hub  │ │ │     ││ │ │ hub  ││ │ ││          fallback rim ring .012s White(.22) UNDER the flange
 ││ │ ╰───────╯ │ │     ││ │ ╰──────╯│ │ ││          hub ⌀ .22·d radial #E9EBEF → #8A8F99
 │ ╲ ╲▁▁▁▁▁▁▁▁▁╱ ╱       ╲ ╲▁▁▁▁▁▁▁▁╱ ╱ ││
 │  ╰──────────────╯      ╰──────────────╯│  152.3
 │        ╲                      ╱        │         threaded tape: PathEl in a 100-unit view box,
 │         ◉                    ◉         │  238.1  stroke #5A4028 width 1.2 view units (= 3.9 DIP at 324,
 │          ╲__________________╱          │          the view-box fit scales the stroke with the geometry)
 │         ┌────────────────────┐         │  236.5  guides ⌀ .05s at x .26s and .74s
 │         │  ▮   ▮   ▮         │  ┌────┐ │  282.9  head block .36s × .18s: 3 heads in a machined housing
 │         └────────────────────┘  │0142│ │          counter .20s × .072s, #1A0D00, Consolas 600,
 └────────────────────────────────────────┘  324     #FFB000, CharSpacing 120, 4 digits = seconds mod 10000
 pack radii: rL = 92 − 48p, rR = 44 + 48p (% of s) ⇒ Aux0 = 1 − .5217p, Aux1 = .4783 + .5217p
 ω = −wind·6000/r: supply 65.2 °/s at p=0, take-up 136.4 °/s; fast-wind ×5 for 900 ms after a seek
```

### W14 — CD @ 324 (format: cd)

```
 ┌────────────────────────────────────────┐  y 0     ground #22252C → #14161B
 │ COMPACT DISC · DIGITAL AUDIO           │  16.2   caption .021s = 6.8 DIP, weight 600, CharSpacing 180,
 │                                        │          White(.45), at (.06s,.05s), width .80s
 │      ╭────────────────────────╮        │  38.9   tray/disc D = .72s = 233.3 at (.14s,.12s)
 │     ╱    ·cover art, round·    ╲       │          plate #101216 + Shadow(30,14,0,Black .4)
 │    │    ╭──────────────────╮    │      │          cover art clipped to the circle (DiscArt, keyed CoverGen)
 │    │   │   ·cd-rainbow·     │   │      │          cd-rainbow-512.png at Opacity .75 over it
 │    │   │    ╭────────╮      │   │      │  155.5  inner ring .23D, 0.008D stroke, White(.18)
 │    │   │    │  ⊙hole │      │   │      │          clamp hole .17D filled Tok.FillCardSecondary + a
 │    │   │    ╰────────╯      │   │      │          0.012D White(.55) border
 │    │    ╰──────────┼───────╯    │      │          rail: 1 × .47D hairline White(.25) straight down
 │     ╲              ●            ╱      │  198     sled ⌀ .03D #FF3B30 + Shadow(8,0,0,#FF3B30 .85)
 │      ╰─────────────┼──────────╯        │  272.2   ‖ Translation(0, Aux0·D), Aux0 = (20 + 26p)/100
 │                    ┊                   │          ⇒ the sled walks 46.7 → 107.3 DIP down the rail
 └────────────────────────────────────────┘  324     spindle ω: 3000 → 1200 °/s (500 → 200 rpm) across p
```

### W15 — MiniDisc @ 324 (format: md) and the CD tray eject

```
 ┌────────────────────────────────────────┐  y 0     caption reads "MD · ATRAC"
 │ MD · ATRAC                             │  55.7   cartridge .62s × (72/68 of that) centred,
 │      ┌───────────────────────┐         │          gradient 160° #283C78 @.85 → #141E46 @.90,
 │      │ ┌─────────┐ ┌───────┐ │         │          Corners 5, Shadow(30,14,0,Black .45)
 │      │ │ ▒▒▒▒▒▒▒ │ │shutter│ │         │  81.2   window .50 × .56 of the shell at (.14,.12), #0A0F20,
 │      │ │ ▒ disc▒ │ │#C9CCD3│ │         │          Corners 3, ClipToBounds — the disc inside is 112 % of
 │      │ │ ▒▒▒▒▒▒▒ │ │ →     │ │         │          the window, so the slot shows a SLICE of it
 │      │ └─────────┘ │#8A8F99│ │         │  72.7   shutter .30 × .64 of the shell at (.64,.08)
 │      │             └───────┘ │         │
 │      │  ┌───────────────────┐│         │  230.1  paper strip .84 × .14 at the bottom, #F4EFE6,
 │      │  │ TITLE · ARTIST    ││         │          MdLabel: uppercase, weight 600, CharSpacing 60,
 │      │  └───────────────────┘│         │          H·.44 size, one line, CharacterEllipsis
 │      └───────────────────────┘         │  268.4
 └────────────────────────────────────────┘  324
 eject (new album, either format): Slide 1 → 0 over 500 ms = the disc translates DOWN 0.3·s = 97.2 and fades,
 CoverGen++ at the bottom of the arc, then 500 ms back. Same two writes as the cassette, opposite direction.
```

### W16 — iPod Classic @ 324, silver body, white LCD

```
 x:0     68.0                      256.0
 ┌────────────────────────────────────────┐  y 0     ground #3A3F4A → #22252C
 │        ┌────────────────────┐          │  6.5    body .58s × .96s at (.21s,.02s), Corners .055s = 17.8,
 │        │┌──────────────────┐│          │  22.0    gradient #F7F7F9 → #CFD2D8, Shadow(34,16,0,Black .5)
 │        ││▶ Now Playing  ▭  ││          │  40.7   LCD .84·bodyW × .43·bodyH at (+.08bw, +.05bh), #DFE6EA,
 │        ││──────────────────││          │          2 px #2A2F38 border, Corners 3
 │        ││ ▤   Track title  ││          │          title bar .14h gradient + 1 px #8B93A3 rule;
 │        ││ art artist       ││          │          glyph .03w in, battery .09w × .44barH at the right
 │        ││ .34w  album      ││          │  75.4   art .34·w at (.04w,.20h); text column at .42w, width .54w
 │        ││ ▓▓▓▓▓▓◆──────────││          │  133.0  progress .92w × .08h at y .91h−h_p, 1 px #2A2F38,
 │        ││ 1:37        -2:11││          │          fill gradient #7DB2EA → #2A6BC4 ‖ Scale(frac,1),
 │        │└──────────────────┘│          │  155.7  diamond thumb (rot 45°) ‖ Translation(frac·progW,0)
 │        │   ╭────────────╮   │          │  164.4  clocks: Consolas, FIXED width .30w, 1 Hz bound text
 │        │  ╱    MENU      ╲  │          │         wheel ⌀ .74·bodyW = 139.1, centred, bottom at .955·bodyH
 │        │ │ ⏮   ╭───╮   ⏭ │  │          │  234.0  centre button ⌀ .36·wheelD = 50.1, its OWN hit node
 │        │ │     │ ⏯ │     │  │          │         captions: MENU (inert), ⏮ ⏭ ⏯ (Previous/Next/Toggle)
 │        │  ╲    ╰───╯    ╱   │          │  292.6  wheel Role = Slider; a TAP is not a seek (only a drag
 │        │   ╰────────────╯   │          │          that actually moved commits)
 │        └────────────────────┘          │  317.5
 └────────────────────────────────────────┘  324
 bodies: silver (light gradient, #8D939D captions) · black (#2A2C31→#0F1013, C9CED8 @ .75 captions) ·
 u2 (black body, wheel radial #D8262B → #8F1418).  LCD green option: plate #D6E6C4, ink #1C2A14.
```

### W17 — iPod, click-wheel SCRUB in progress @ 324

```
        ╭────────────╮      angle = atan2(y − r, x − r) on each pointer sample
       ╱    MENU      ╲     Δ = a − lastAngle, unwrapped across the ±π seam (±τ)
      │ ⏮     ↻     ⏭ │     dragFrac += Δ/τ · 0.25    ⇒ ONE FULL TURN = A QUARTER OF THE TRACK
      │    ╭─────╮    │     Gesture.Move((long)(dragFrac · duration)) → ScrubTargetMs
       ╲   │  ⏯  │   ╱      the LCD's bar and thumb read ScrubTargetMs FIRST (frac() in IpodDeck.cs:55-63),
        ╰──┴─────┴──╯       so the screen follows the finger before any audio moves
 progress ▓▓▓▓▓▓▓▓▓▓◆───    release → Commit only if _moved; otherwise Cancel (a rim tap must not seek)
 elapsed/remaining clocks follow the scrub too (PlayheadMs = Scrub ?? Seek ?? Position, IpodDeck.cs:207-208)
```

### W18 — Hi-fi VU @ 324, ivory face

```
 x:0  16.2                  168.5        307.8
 ┌────────────────────────────────────────┐  y 0     ground #1A1B20 → #0F1013
 │  ┌──────────────┐    ┌──────────────┐  │  38.9   two meters .43s × (.82 of that) at y .12s, x .05s / right
 │  │LEFT          │    │RIGHT         │  │          plate #EFE6CF, Corners (6,6,14,14), 1 px Black(.5),
 │  │ -20 -7 -3 0 +2    │ -20 -7 -3 0+2│  │          Shadow(20,8,0,Black .5)
 │  │ ╲ ╲ │ │ ╱ ╱ ╱│    │ ╲ ╲ │ │ ╱ ╱ ╱│  │          lamp = radial glow (1,200/255,120/255) @ .25 rising
 │  │  ╲  ╲│╱ ╱▂▂  │    │  ╲  ╲│╱ ╱▂▂  │  │          from centre (.5,1), radius (.6,.4)
 │  │      ╲│╱     │    │      ╲│╱     │  │          scale = 3 interned PathEls in a 100×82 view box:
 │  │   VU  ●      │    │   VU  ●      │  │  141.1   arc M14 62 A40 40 0 0 1 86 62 (ink, 1.2), red arc
 │  └──────────────┘    └──────────────┘  │  153.1   M68 32 A40 40 0 0 1 86 62 (3), red ticks (1)
 │               ╭───╮                    │  194.4  needle 1.5 × .78h, origin (.5,1), ‖ Rotation(Angle0|1)
 │               │ ▏ │  knob .09s         │          pivot cap ⌀ .08w radial #555555 → #111111
 │               ╰───╯                     │  223.6  knob .09s at the centre, radial #8A8F99 → #2B2E35
 │  ┌──────────────────────────────────┐  │  230.0  LCD .90s × .20s at (.05s, .91s−h), #1A0D00,
 │  │ 01:37                     -02:11 │  │          1 px #3A2000, Corners 4, amber #FFB000 bloom BEHIND
 │  │ TITLE · ARTIST        FLAC · 320 │  │          the glyphs (radial, centre (.5,.35), a = .12)
 │  └──────────────────────────────────┘  │  294.8  clocks .055s Consolas CharSpacing 60; the two info
 └────────────────────────────────────────┘  324     cells .028s CharSpacing 140 at amber @ .85
 faces: ivory (plate #EFE6CF, ink #222222, red #C8321E) · blue (#0A2A5A / #9FD6FF / #FF6A4A, glow 120,200,255)
        · black (#141519 / #F0F0F0 / #FF4A3A, glow white @ .12)
 ballistics is a MODEL option: the same pixels, τ 0.3 s (VU) or 0.05 s (PPM)
```

### W19 — Winamp @ 324, base skin, spectrum

```
 x:0 13.0                                311.1
 ┌────────────────────────────────────────┐  y 0     ground #0F1218; both windows .92s wide at x .04s,
 │ ┌────────────────────────────────────┐ │  32.4    aspect 275:116 ⇒ h = .388s = 125.7
 │ │▨▨▨▨▨▨▨▨▨▨ WINAMP ▨▨▨▨▨▨▨▨▨▨▨▨▨▨▨▨▨│ │  47.5   title bar .12h: winamp-tbar.png tinted wa3, with a
 │ │  ┌────────┐ ┌──────────┐          │ │          wa1-filled chip carrying "WINAMP" (CharSpacing 240)
 │ │  │ 01:37  │ │▁▃▅▇▅▃▁▃▅▇│          │ │  57.5   time LCD .32w × .30h at (.14w,.20h), Consolas 700
 │ │  └────────┘ └──────────┘          │ │          .064s = 20.7 DIP, black plate, colour = the skin's lcd
 │ │  ┌──────────────────────────────┐ │ │  67.9   19-bar analyser .34w × .30h at .50w (or a 24-dot scope)
 │ │  │ 1. Artist - Title (3:56) *** │ │ │  83.0   stitle .80w × .12h: a Marquee, Loop, 22 DIP/s, gap 24
 │ │  └──────────────────────────────┘ │ │  86.7   kbps · format · STEREO cells (small = .024s)
 │ │  320 kbps   FLAC          STEREO  │ │  135.5  position bar .88w × .06h black + a 12-DIP thumb
 │ │ ▬▬▬▬▬▬▬▓▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ │ │          ‖ Translation(Frac·(posW−12), 0)
 │ │ ▪▪ ▪▪ ▪▪ ▪▪ ▪▪                     │ │  145.6  5 transport keys (Prev / Play / Pause / Stop→Pause / Next)
 │ └────────────────────────────────────┘ │  158.1
 │ ┌────────────────────────────────────┐ │  164.6  EQ window: the SAME 19 band signals at a bigger
 │ │▨▨▨ WINAMP SPECTRUM ANALYZER ▨▨▨▨▨▨▨│ │          geometry (gap 2, pad 3 ⇒ 11.5-DIP bars) + a 10-line
 │ │ ┌────────────────────────────────┐ │ │          White(.06) grid drawn OVER them
 │ │ │▁▃▅▇▆▄▂▁▃▅▇▆▄▂▁▃▅▇▆ ← peak caps │ │ │         bar gradient #FF4A4A → #FFD24A @.45 → lcd;
 │ │ └────────────────────────────────┘ │ │          each bar ‖ Scale(1, max(band,.02)) about its bottom,
 │ └────────────────────────────────────┘ │  290.3   each cap ‖ Translation(0, inner·(1−peak))
 └────────────────────────────────────────┘  324
 skins: base wa1 #3B4459 wa2 #222A3A wa3 #6E7A97 lcd #00FF00 · modern #4A4F57/#2B2E34/#8C9199/#E5F3FF ·
        dark #1A1C22/#0E0F13/#5B5F6B/#FF8A00.  Frame: wa2 fill, 1 px black outline, 1 px wa3 @ .45 inner bevel LAST.
 Oscilloscope option: both windows become 24 one-pixel dots ‖ Translation(0, (band − .5)·h); titles read
 "WINAMP OSCILLOSCOPE".  Writes per tick: 19 bars + 19 caps + frac ≈ 39.
```

### W20 — WMP visualizer @ 324 — the three presets

```
 bars & waves (default)                alchemy                        battery
 ┌──────────────────────────┐  ┌──────────────────────────┐  ┌──────────────────────────┐
 │  ▏▍▋█▋▍▏▍▋█▋▍▏▍▋█▋▍▏▍▋█  │  │        ╭─────────╮       │  │ ▪ ▪ ▪ ▪ ▪ ▪              │
 │ ·•·•·•·•·•·•·•·•·•·•·•·  │  │     ╭──┼─────────┼──╮    │  │ ▪ ▪ ▪ ▪ ▪ ▪   6×6 cells, │
 │  ▔▁▔▁▔▁▔▁▔▁▔▁▔▁▔▁▔▁▔▁▔▁  │  │   ╭─┼──┼─────────┼──┼─╮  │  │ ▪ ▪ ▪ ▪ ▪ ▪   each 54 DIP │
 │                          │  │   ╰─┼──┼─────────┼──┼─╯  │  │ ▪ ▪ ▪ ▪ ▪ ▪   ‖Scale(.15  │
 │  ▤ cover .22s            │  │     ╰──┼─────────┼──╯    │  │ ▪ ▪ ▪ ▪ ▪ ▪    + .7·band) │
 │                 Bars &   │  │        ╰─────────╯       │  │ ▪ ▪ ▪ ▪ ▪ ▪   Opacity .7  │
 │  ▬▬▬▬▬▬▬▬▬▬▬▬─────────── │  │  ▬▬▬▬▬▬▬▬▬▬▬────────────  │  │ ▬▬▬▬▬▬▬▬▬▬───────────── │
 └──────────────────────────┘  └──────────────────────────┘  └──────────────────────────┘
 24 bars, bw = s/24 = 13.5,     3 rings ⌀ 2s(.18+.10r) =      cw = ch = s/6 = 54, cell = 54
 body = bw − 4 = 9.5, maxH      116.6 / 181.4 / 246.2,        band index (i·3 + j) mod 24
 = .55s = 178.2 above the mid   BorderWidth 2, 3 trailing     ⇒ the grid pulses in diagonal
 line, reflection .6·maxH at    copies each at Opacity        bands rather than per column
 Opacity .35 (bars are .85);    .9 / .35 / .15, lag .15 rad
 32 wave dots ⌀ 2 White(.85)    ‖ Rotation(Frac·τ·6(r+1))
 ‖ Translation(0,(peak−.5)·     · Scale(1+.25band, .8×that)
 .36s)  = ±58.3 DIP             ⇒ 6 / 12 / 18 turns per track
 common furniture: cover .22s at (.05s, .92s−cover) with Shadow(20,8,0,Black .6) and a BOUND ImageEl Source;
 preset caption (Loc.Bind of the choice's LabelKey) at the right, .034s·.72 = 7.9 DIP, White(.45), CharSpacing 140;
 a 2 px full-width line at the very bottom, track White(.12), fill ‖ colour() ‖ Scale(Frac,1).
 colour option: "accent" = Tok.AccentDefault · "cover" = WaveePalette.Accent(SchemeFor(coverUrl)) — read INSIDE
 the thunk, so a theme change, a late grading and a track change all repaint without rebuilding the face.
```

### W21 — Canvas drift @ 324

```
 ┌────────────────────────────────────────┐  y −81   bleed: ONE 1.5s = 486-DIP ImageEl at (−.25s,−.25s),
 │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│          BakedBlur(σ 44, .25), Saturation 1.5, DecodePx 256,
 │▒▒▒▒▒ blurred, over-saturated cover ▒▒▒▒│          Opacity 1.0 (strong) | 0.6 (soft)
 │▒▒┌──────────────────────────────┐▒▒▒▒▒▒│  35.6   drift frame .78s = 252.7 at (.11s,.11s), Corners 6,
 │▒▒│                              │▒▒▒▒▒▒│          Shadow(60,24,0,Black .45), ClipToBounds
 │▒▒│      the cover, crisp,       │▒▒▒▒▒▒│          inner wrapper 118 % = 298.2 at (−.09·frame, −.09·frame)
 │▒▒│      drifting inside a       │▒▒▒▒▒▒│          — the ANIMATED node; the extra 18 % is the pan headroom
 │▒▒│      118 % box               │▒▒▒▒▒▒│          so no edge is ever exposed
 │▒▒│                              │▒▒▒▒▒▒│         tracks (loop, mirrored 0→½→1→½→0, EaseInOut):
 │▒▒└──────────────────────────────┘▒▒▒▒▒▒│          TranslateX 0 → −12.6 → +10.1 DIP
 │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│          TranslateY 0 → +7.6 → −10.1 DIP
 │   ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬────────────────     │  305.8   ScaleX/Y 1 → 1.06 → 1.02
 └────────────────────────────────────────┘  324     over 40 s (slow) | 16 s (fast)
 seek line .78s × 2 at (.11s, .95s−2): track White(.35), fill White ‖ Scale(Frac,1) — built OUTSIDE
 CanvasArtwork so its bind thunk survives every album change and every play/pause re-render above it.
 gate = playing && active && !reducedMotion; a flip CANCELS the four channels (freeze in place), never
 re-seeds a flat track. A new album re-keys the whole canvas ("cv:<gen>") and it cross-fades in over 300 ms.
```

### W22 — Rail width breakpoints (the only layout breakpoint this surface has)

```
 rail 200 (RailMinW)        rail 340 (RailDefaultW)       rail 500 (RailMaxW)
 content 184 → side 184     content 324 → side 324        content 484 → side 484
 ┌──────────────────┐       ┌────────────────────────┐    ┌──────────────────────────────────┐
 │ every dimension  │       │  the reference deck    │    │  the same deck, 1.494× larger    │
 │ is a FRACTION of │       │                        │    │                                  │
 │ side: the deck   │       │                        │    │                                  │
 │ scales, it does  │       │                        │    │                                  │
 │ not reflow       │       │                        │    │                                  │
 └──────────────────┘       └────────────────────────┘    └──────────────────────────────────┘
 side = round((RailWidth − 2·Spacing.S)/4)·4  (NowPlayingPanel.cs:541-542) — quantised to the 4-DIP grid
 because it is part of the remount Key ("deck:<slug>@<int side>"), so a splitter drag causes a quarter of the
 remounts a per-DIP key would. NO HYSTERESIS: there is no threshold, only the quantum. The angle quantum
 follows: 360/(π·side·0.70) = 0.890° at 184, 0.505° at 324, 0.338° at 484.

 WHAT DOES NOT SCALE (the honest list — "everything is a fraction of side" is true of POSITIONS, not of these):
 a. absolute DIP sizes: Winamp thumb 12 · Winamp bar gap/pad 1-3 · Winamp scope dot 1.5 · WMP wave dot 2 ·
    WMP bar inset (`bw − 4`) · VU needle width 1.5 · Zune bar height 3 · Canvas seek line and WMP progress line 2 ·
    the record stylus 2×8 · the cassette's 3 hand-written rules 1 · every 1-2 px border and hairline ·
    the iPod battery's `battH − 2` fill.
 b. EVERY corner radius: deck root 8 · sleeve 4 · headshell 3 · rest post 3 · strobe 2 · cassette shell 6,
    window 5, label 3, foot 3 · reel head block 6, heads 2, counter 3 · MD 5/3/2 · iPod LCD 3, progress 2,
    artwork 2 · VU meter (6,6,14,14) and LCD 4 · Canvas frame 6 · WMP cover 3. (Only the iPod BODY's
    `0.055s` radius scales.)
 c. EVERY `ShadowSpec` (blur/dy are DIP): 14 of them, §4.2.
 d. Padding/spacing literals: the Winamp title chip's 8, its cells' (4,1,4,1), the Marquee's 22 DIP/s + 24 gap,
    the felt mat's 8-DIP ring inset, the cassette window's 2 px inner border.
 e. TYPE FLOORS. iPod, VU, Winamp and WMP size their text `MathF.Max(floor, k·side)`. At side 184 most floors
    BIND and the type stops shrinking — it grows relative to the deck: Winamp small 4.4→6, stitle 4.8→6;
    VU tick 4.0→5, "VU" 7.4→8, channel 4.8→6, info 5.2→6; iPod chrome 4.8→6, clock 4.4→6, title 5.3→7,
    lines 4.8→6, wheel caption 4.6→6, glyph 5.9→7; WMP caption 6.3→8. The iPod's three metadata lines are
    then 7 DIP type in a 48-DIP column — the FIRST thing to check at rail 200. Cassette, Reel, CD, Zune and
    Canvas have no floors at all and keep scaling linearly.
```

### W23 — Reduced motion, every deck at once

```
 Record     no rotation at all (platter target = 0 ⇒ the disc never turns), every phase collapses to ≤150 ms,
            hover bob flat at 1.0, the needle-drop puff never fires (Thump is suppressed in Sample)
 Cassette   hubs still turn (TapeModel does not read ReducedMotion) — see §9, this is a KNOWN GAP
 Reel       likewise
 CD         likewise
 VU         needles still move (MeterModel does not read ReducedMotion) — KNOWN GAP
 Winamp/WMP bands still move (LevelModel does not read ReducedMotion) — KNOWN GAP
 Canvas     drift CANCELLED (frozen in place), bleed and frame still painted
 ALL        the ticker is OFF (`run = !reduced && …`), so the above only moves on a state CHANGE — the
            state-change effect folds one tick per transport edge. The 320 ms deck entrance and the 300 ms
            cover cross-fades are KEPT deliberately.
 MOUNT      the seed honours it too: `RecordModel` skips the "already at speed" platter when the seed says
            reduced (`RecordModel.cs:43`), so a deck mounted mid-song under reduced motion shows a STILL
            record at the right angle rather than one that starts turning.
```

### W24 — Loading / reveal / no-art states (any deck)

```
 art not yet decoded                     art decoded, first paint            no cover url at all
 ┌──────────────────┐                    ┌──────────────────┐                ┌──────────────────┐
 │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │ the cover slot     │  ▒▒ the artwork  │ ImageEl swaps  │  ▓▓ wash only ▓▓ │
 │  ▓ HeroWash   ▓  │ paints its         │  ▒▒ (+ BlurHash  │ in place; no   │  (Lerp(FillCard  │
 │  ▓ placeholder▓  │ Placeholder =      │  ▒▒  if present) │ remount, no    │   Secondary,     │
 │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │ HeroWashColor(url) │                  │ re-render      │   Lift(Accent)   │
 └──────────────────┘                    └──────────────────┘                │   , .18 dark))   │
 The deck's own ENTRANCE (320 ms opacity) is the only reveal choreography;    └──────────────────┘
 there is no skeleton pass, because a deck's geometry is complete before any pixel of art exists.
 Decode ladder (DeckArt.DecodeFor): px = size·1.5 → 64 | 128 | 256 | 512. A .34D label at side 324 is 77 DIP
 ⇒ decoded at 128; the Canvas drift frame at 298 DIP ⇒ 512; the WMP corner cover is pinned at 128.
 The wash EXACTLY (NowPlayingPanel.cs:203-210): Lerp(Tok.FillCardSecondary, accent, dark ? .18 : .10) where
 accent = Lift(Accent(scheme)) once the grading has landed and Tok.AccentDefault when there is no url or no
 scheme yet — so the "no cover" square is a theme-accent tint, not a graded one, and the two are one function.
 The record's SLEEVE is the one slot that is not empty without art: Fill Black(.35) under the leaf, so a
 sleeve with no art is still a sleeve (RecordDeck.cs:137). WMP's corner cover opts OUT of the wash entirely
 (flat #14171F, WmpDeck.cs:68), which is the only place a deck shows a plate instead of a tint.
```

### W25 — Hover / press / focus, every interactive part

```
 record headshell grip   31 × 60 DIP transparent box INSIDE the rotating arm · CursorId.Hand ·
                         no hover paint at all (the arm is the affordance) · no focus visual · not focusable
 iPod wheel              the whole 139-DIP circle · CursorId.Hand · Role = Slider · no hover paint
 iPod centre button      50 DIP · CursorId.Hand · Role = Button · Focusable · no hover paint
 iPod ⏮ ⏭ ⏯ captions     capH·1.4 × capH hit boxes · CursorId.Hand · Role = Button · Focusable
 iPod MENU caption       inert (no onClick ⇒ no cursor, Role None)
 Winamp transport keys   btnW × btnH (14.3 × 9.7 at side 324) · CursorId.Hand · Role = Button · Focusable ·
                         no hover paint, no pressed paint — they are 1990s bitmap keys by design
 everything else         HitTestVisible false or plain non-interactive boxes; the deck's right-click menu
                         belongs to the hero WRAPPER (Ch.21), not to any face
 NOTE: none of these carries an AutomationName, and the engine's focus ring is the only focus visual. See §6.
```

### W26 — The deck inside its slot (for placement only — Ch.21 owns everything above the dashed line)

```
 rail 340                                        NowPlayingHeroTile, Padding (8, 8, 8, 0), Gap 8
 ┌──────────────────────────────────────┐
 │ NOW PLAYING     [▣ Cover│◉ Record] ⚙ │  36     NpvHeaderRow.Height
 ├ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┤  8      Gap
 │ ┌──────────────────────────────────┐ │
 │ │                                  │ │  324    the deck: W = H = side, Corners 8, ClipToBounds,
 │ │            the deck              │ │         IsolateLayout, 320 ms opacity entrance
 │ │                                  │ │         (a 0×0 DeckClock is its second child)
 │ └──────────────────────────────────┘ │
 └──────────────────────────────────────┘
   the scrolled NowPlayingPanel body starts here and NEVER moves because of anything the deck does
```

### W27 — The six poses a deck can MOUNT in (`TonearmMachine.Seed`, `:131-143`)

The record family is the only deck that decides anything at mount; the rest converge within a tick or two. This is a
pure function of the seeded `DeckInput` and it is evaluated ONCE, in the host's first render (`DeckHost.cs:70`), so
every Cover→Player flip, every preset change and every rail-resize re-key re-runs it against the CURRENT transport.

```
 !HasTrack            → Initial            arm on the rest, platter off, record IN the sleeve (Slide 0), Idle
 Error                → Unavailable        record ON the platter, arm at the rest, platter off
 Advancing            → Tracking           arm at AngleOf(frac), Lift 0, platter ON and ALREADY AT SPEED
 || (PWR && !Buffer)                       (ω = rpm·6 seeded directly — unless ReducedMotion, RecordModel.cs:43)
 Buffering            → Hover              arm at AngleOf(frac), lifted and bobbing, platter ON
 QueueEnded           → Stopped            arm at the rest, record still out, platter off — an auto-returned deck,
                                           NOT a deck paused mid-groove
 otherwise            → Paused             arm at AngleOf(frac), Lift 1, platter off
 ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
 NO OTHER DECK HAS A SEED. TapeModel/DiscModel start with both hubs at rest and spin up over ~0.3-0.6 s; the
 CD's sled and the packs are pure functions of Frac, so they are correct on the FIRST tick; MeterModel starts
 both needles at −45° and sweeps up; LevelModel starts every band at the floor. That difference is visible:
 flipping Cover→Player mid-song shows a record already at speed but a cassette whose hubs take half a second
 to reach theirs. It is the 0.2.9 behaviour — reproduce it, do not "fix" it with a fake seed.
```

### W28 — Degenerate inputs (what a deck does when something is missing)

```
 no PlaybackBridge in context   DeckHost returns a BARE BoxEl(side, side) — no corners, no clip, no entrance,
                               no clock, no face (DeckHost.cs:66). Reachable in tests/harnesses, never in the app.
 no ShellUi in context          side falls back to the 324 literal (NowPlayingPanel.cs:516,541) and RailOpen
                               reads TRUE, so the ticker runs (DeckClock.cs:127).
 no track                       the hero slot returns an empty BoxEl and the deck is UNMOUNTED entirely
                               (NowPlayingPanel.cs:528) — W7's "no track" pose only happens if the track clears
                               while the deck is alive.
 DurationMs 0                   Frac 0 everywhere (DeckInput.cs:50): arm at the lead-in, sled at 20 % of D, packs
                               at full/empty, every bar at 0, clocks at 0:00 / -0:00, counter 0000. See W3's
                               guards for what the two grips do.
 no level tap (Connect,         MeterModel breathes (−0.42 ± 3 dB, 10.5 s period); LevelModel runs env = 0.8
 remote device, --fake)         while playing and 0 when not. NEVER a flat/dead meter: absence is a real answer.
 missing deck texture           ImageEl.Placeholder is TRANSPARENT for every texture (DeckArt.cs:83), so what is
                               UNDER it shows: the vinyl body + sheen (grooves), the plinth stain (wood grain),
                               the brown pack (marble/CD rainbow), the White(.22) rim ring (reel flange,
                               ReelDeck.cs:122), the wa1 title-bar fill (winamp-tbar). Never a grey plate.
 title/artist/album unknown     0.2.9 renders "" — a blank label, a blank Zune title, "1.  - " on Winamp. This is
                               the one place 0.3 must behave DIFFERENTLY (see §7): keep the previous generation's
                               text until the new one is known, because these leaves only re-render on CoverGen.
```

---

## 3. Tokens

`s` = the deck's square edge in DIP (324 by default). Every "size" below is the literal formula from the code; the
parenthesised number is its value at s = 324. Colours written as `#RRGGBB @a` are literal `ColorF`s constructed by each
face's own private `Hex(uint, float)` helper — a deck is a physical object and deliberately does not read theme ink.

### 3.1 The shell (host, clock, slab)

| element | size | padding/gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| deck root | `side × side`, `Shrink 0` | — | `Radii.Card` = 8, `ClipToBounds`, `IsolateLayout` | — | none (the face paints) | `Animate` = 320 ms opacity tween | `DeckHost.cs:75-84` |
| default side | `RailDefaultW(340) − 2·Spacing.S(8)` = **324** | — | — | — | — | — | `NpvDeck.cs:18` |
| live side | `round((RailWidth − 16)/4)·4` | — | — | — | — | — | `NowPlayingPanel.cs:541-542` |
| ticker | `0 × 0`, `HitTestVisible false` | — | — | — | — | 33.33 ms (`1000/AmbientPowerPolicy.PluggedLoopHz`) | `DeckClock.cs:35,136` |
| angle quantum | `360/(π·side·0.70)` (0.505°) | — | — | — | — | — | `DeckHost.cs:109` |
| slab | 7 `FloatSignal` + 2×24 `FloatSignal` + 2 `Signal` | — | — | — | — | — | `DeckSignals.cs:27-46` |
| rpm | `"45" ? 45 : 33.333` → ω = rpm·6 (200 / 270 °/s) | — | — | — | — | — | `DeckHost.cs:103` |

### 3.2 Record family (`RecordDeck.cs`) — geometry

| element | size | position | radius | type style | colour | material / elevation | source |
|---|---|---|---|---|---|---|---|
| disc D, 12" | `0.70·s` (226.8) | `(0.24s, 0.16s)` | circle | — | per finish | `Shadow(34,14,0,#000 @.45)` | `:69-71,277` |
| disc D, 7" | `0.52·s` (168.5) | `(0.33s, 0.25s)` | circle | — | — | — | `:69-71` |
| disc D, Zune | `0.62·s` (200.9) | `(0.08s, 0.22s)` | circle | — | — | — | `:69-71` |
| disc x, sleeve off | — | `px = 0.15s` | — | — | — | — | `:72` |
| sleeve | `0.64·s` (207.4) | `(0.06s, 0.12s)`, rot −2.5° (Turntable −4°) | 4 | — | `Black @.35` under the art | `Shadow(30,10,0,#000 @.25)`, `ClipToBounds` | `:126-139` |
| label | `0.34·D` (77.1) | centred on the disc | circle | — | cover art | ring 2 px `Black @.6` (Zune 3 px `White @.9`) | `:335-337` |
| spindle | `0.05·D` (11.3) | centred | circle | — | `#E6E9EF` (Picture: `White`) | 1 px `Black @.5` | `:341-353` |
| 7" hole | `0.20·D` (33.7) | centred | circle | — | **deckBg (bound)** | — | `:341-344` |
| grooves | `D × D` | on the disc | circle | — | `grooves-1024.png` tinted: black `White @.035` · clear `White @.12` · album `White @.08` · splatter `Black @.08` · marble `Black @.18` | texture | `:309-316` |
| sheen | `D × D` | on the disc | circle | — | radial `White @.10 → transparent @.55 → White @.06`, centre (.30,.25), radius (.9,.9) | — | `:321-331` |
| splatter dots | `0.04·D` (9.1) ×6 | (.30,.25)(.70,.40)(.60,.78)(.24,.66)(.82,.70)(.45,.12) | circle | — | `#FFD166 / #EF476F / #06D6A0` radial to @.75 | — | `:292-301,369-377` |
| thump ring | `0.08·D` (18.1) | centred | circle | — | border `White @.7`, `max(1, size·0.08)` = 1.5 | `Opacity 0` at rest | `:213,630-642` |
| arm box | `0.22s × 0.92s` (71.3 × 298.1) | `(0.75s, 0.02s)`, origin (.5,.08) | — | — | — | — | `:148` |
| arm tube | `0.09·armW × 0.62·armH` (6.4 × 184.8) (Turntable `0.07·armW` = 5.0) | `(0.455·armW, 0.09·armH)` | 4 | — | linear `#9AA1AD → #CFD4DD → #9AA1AD`; Turntable `#5D6470 → #F2F4F7 @.4 → #C3C8D1 @.6 → #5D6470` | shadow twin `Shadow(10,10,0,#000 @.45)` bound to `0.45·Lift` | `:408-415,454-471` |
| arm pivot | `0.44·armW` (31.4) | on the rotation origin | circle | — | radial arm → armDark | `Shadow(8,3,0,#000 @.35)` | `:417,474-482` |
| headshell | `0.22·armW × 0.12·armH` (15.7 × 35.8) | `(0.39·armW, 0.69·armH)`, rot −18° | 3 | — | `#CFD4DD` (Turntable `#2B2E34` + 1 px `#7A808A`) | — | `:418,483-499` |
| stylus | `2 × 8` | bottom-centre of the headshell | — | — | `#DD3333` | — | `:496-497` |
| grip hit box | `0.44·armW × 0.20·armH` (31.4 × 59.6) | `(0.28·armW, 0.64·armH)` | — | — | transparent | `CursorId.Hand` | `:419,502-512` |
| rest post | `0.03s × 0.09s` (9.7 × 29.2) | `(0.845s, 0.06s)` | 3 | — | `#9AA1AD @.6` (Turntable `#C9CED8`) | — | `:150-157` |
| plinth | `s × s` | 0,0 | 8 | — | `wood-grain-1024.png` tinted `White(1)` + vertical `#6B4A2E @.88 → #4A301B @.96` | — | `:81-89` |
| felt mat | `0.77s` (249.5) | `(0.205s, 0.125s)` | circle | — | radial `#3A3D44 → #2B2E34` | rings 4 px `#1F2126`, 2 px `#6D7078` inset 8 | `:91-101` |
| strobe | `0.16s × 0.05s` (51.8 × 16.2) | `(0.08s, 0.86s)` | 2 | — | box `#1A1C20`, dot `#FF5A2A` ⌀ `strobeH·0.5` | dot `Shadow(10,0,0,#FF5A2A @.9)` | `:104-120` |
| Zune type block | width `0.34s` (110.2) | `(0.62s, 0.08s)` | — | title `0.11s` (35.6) w300 "Segoe UI Variable Display"; line `0.036s` (11.7) | title `White` + accent tail; line `#9AA1AD` | `ClipToBounds`, gap `0.02s` | `:164-167,664-717` |
| Zune clock slot | width `0.10s` (32.4), `Shrink 0` | inside the line | — | `0.036s` | `#9AA1AD` | bound `Text` | `:709-714` |
| Zune bar | `0.86s × 3` (278.6 × 3) | `(0.08s, 0.88s)` | — | — | track `#333333`, fill = accent | `Scale(Frac,1)` origin (0,.5) | `:170-193` |

**Record finishes** (`:279-290`): black `#15171C` · clear `rgba(40,44,54,.55)` (a plain translucent fill, NOT acrylic —
the node rotates 30×/s) · album = the cover accent × 0.55 (bound) · splatter `#F5F1EA` · marble `#6B4FA8` +
`marble-1024.png` tinted `White @.9`. **Zune accents** (`:546-552`): pink `#F0568C` (default) · orange `#FF7A1A` ·
green `#8CBF26` · blue `#1BA1E2`.

### 3.3 Cassette (`CassetteDeck.cs`)

| element | size (fraction) | position | radius | type | colour | source |
|---|---|---|---|---|---|---|
| ground | `s × s` | 0,0 | — | — | vertical `#2B2F3A → #1C1F27` | `:155-161` |
| shell | `0.86s × 0.56s` (278.6 × 181.4) | `(0.07s, 0.21s)` | 6 | — | black `#1D1F24` · clear `#BECDE6 @.28` + `AcrylicSpec(#BECDE6, .28, 18, .02, .92)` + 1 px `White @.35` · cream `#E9E2D0` · smoke `#464854 @.85` | `:92-111` |
| shell shadow | — | — | — | — | `Shadow(30,14,0,#000 @.5)` | `:111` |
| label | `0.90 × 0.30` of the shell (250.8 × 54.4) | `(0.05, 0.06)` of the shell | 3 | title `H·0.26` (14.1) w700; second `H·0.20` (10.9) w400 | type1 `#F2E9D8`/Courier New · chrome gradient `#E8E8EC → #B9BCC4`/Segoe UI UPPERCASE · hand `#FFFDF5`/Segoe Print + 3 rules `#9FC0E8` | `:42-68,291-368` |
| label ink | — | — | — | — | — | `#23262B`, second line @.72 | `:329,363` |
| side badge | `0.10·labelW` (25.1) | label right edge, vertically centred | circle | `d·0.52` (13.1) w700 | fill `#23262B @.10`, border 1 px `#23262B @.45`, glyph `#23262B` | `:48,117,265-289` |
| window | `0.62 × 0.34` of the shell (172.7 × 61.7) | `(0.19, 0.40)` | 5 | — | `#14151A`, 2 px `White @.08`, `ClipToBounds` | `:44-45,71-90` |
| tape pack | `0.26s` (84.2) | hub centres 31 % / 69 % shellW, `winH/2` | circle | — | `#3A2B1C` + grooves texture tinted `#2A1D11 @.55` | `:46,168-184` |
| reel | `0.11s` (35.6) | same centres | circle | — | border `0.014s` (4.5) `White @.92` | `:47,186-199` |
| spoke ×3 | `0.10s × 0.02s` (32.4 × 6.5) at 0/60/120° | reel centre | — | — | `White` | `:186,204-206,219-228` |
| reel hub | `0.035s` (11.3) | reel centre | circle | — | `White` | `:186,207` |
| tape strip | `0.60 × 0.025` of the shell (167.2 × 4.5) | `(0.20, 0.81)` | 0 | — | `#3A2B1C` | `:121` |
| foot | `0.48 × 0.16` of the shell (133.7 × 29.0) | `(0.26, shellH − 0.16·shellH)` | 3 | — | `#000 @.25` + 4 holes `h·0.34` at `#000 @.45` | `:122,240-261` |
| screws ×4 | `0.012s` (3.9) | insets `0.03·shellW` / `0.04·shellH` | circle | — | `White @.22` | `:123-126,263` |
| brand text | width `0.17s` (55.1), height `0.030s` | `(0.02·shellW, 0.86·shellH)` | — | `0.018s` (5.8) w500 | `White @.35` (cream shell: `Black @.40`) | `:127-136` |

### 3.4 Reel-to-reel (`ReelDeck.cs`)

| element | size | position | colour | source |
|---|---|---|---|---|
| ground | `s × s` | — | vertical `#2A2D33 → #1B1D22` | `:52-58` |
| reel | `0.40s` (129.6) | y `0.07s`; x `0.06s` and `s − 0.06s − d` | — | `:35-37,59-60` |
| pack | `0.92·d` (119.2) | centred | `#4A3520` + grooves tinted `#2A1D11 @.55` | `:86,88-101` |
| flange | `d` | 0,0 of the reel | `reel-flange-512.png` tinted alu `#C9CDD4` · black `#2A2C33` · clear `#C8D7F0 @.45` | `:39-44,104-115` |
| rim fallback ring | `d`, stroke `0.012s` (3.9) | 0,0 | `White @.22` | `:122` |
| hub | `0.22·d` (28.5) | centred | radial `#E9EBEF → #8A8F99` | `:86,123-134` |
| guide ×2 | `0.05s` (16.2) | x `0.26s` / `0.74s`, y `s − 0.24s − d/2` | `#1E2026` + border `d·0.16` `#A9AEB8` | `:46,61-62,139-148` |
| head block | `0.36s × 0.18s` (116.6 × 58.3) | centred, y `s − 0.09s − h` | vertical `#3B3F47 → #23262C`, `Shadow(18,8,0,#000 @.45)`, Corners 6 | `:47,63,151-173` |
| head ×3 | `w·0.14 × h·0.52` (16.3 × 30.3) | evenly spaced | vertical `#B9BEC8 → #6B7079`, 1 px `#000 @.35`, Corners 2 | `:153-184` |
| tape path | `PathEl`, 100-unit view box, stroke 1.2 (≈3.9 DIP at 324) | full deck | `#5A4028`, round cap/join | `:25-26,66-77` |
| counter | `0.20s × 0.072s` (64.8 × 23.3) | `(s − 0.05s − w, s − 0.055s − h)` | `#1A0D00`, 1 px `#000 @.6`, Corners 3; digits `H·0.56` (13.1) w600 Consolas `#FFB000` CharSpacing 120 | `:48,78,186-241` |

### 3.5 CD / MiniDisc (`CdDeck.cs`)

| element | size | position | colour | source |
|---|---|---|---|---|
| ground | `s × s` | — | vertical `#22252C → #14161B` | `:33-39` |
| caption | width `0.80s`, height `0.034s`, size `0.021s` (6.8) w600 CharSpacing 180 | `(0.06s, 0.05s)` | `White @.45` | `:41-52` |
| disc D | `0.72s` (233.3) | `(0.14s, 0.12s)` | plate `#101216`, `Shadow(30,14,0,#000 @.4)` | `:73-74,212-220` |
| rainbow | `D` | on the disc | `cd-rainbow-512.png` untinted, in a box at `Opacity 0.75` | `:223-230` |
| inner ring | `0.23·D` (53.7), stroke `0.008·D` (1.9) | centred | `White @.18` | `:209,234-235` |
| clamp hole | `0.17·D` (39.7), border `0.012·D` (2.8) | centred | **`Tok.FillCardSecondary`** + `White @.55` | `:209,236-245` |
| sled rail | `1 × 0.47·D` (109.6) | from the spindle down | `White @.25` | `:95-101` |
| sled | `0.03·D` (7.0) | spindle, `Translation(0, Aux0·D)` | `#FF3B30` + `Shadow(8,0,0,#FF3B30 @.85)` | `:76,102-111` |
| MD shell | `0.62s × (72/68 of that)` (200.9 × 212.7) | centred | linear 160° `#283C78 @.85 → #141E46 @.90`, Corners 5, `Shadow(30,14,0,#000 @.45)` | `:119-120,154-165` |
| MD window | `0.50 × 0.56` of the shell (100.4 × 119.1) | `(0.14, 0.12)` | `#0A0F20`, Corners 3, `ClipToBounds`; the disc inside is `winW·1.12` | `:121-123,128-152` |
| MD shutter | `0.30 × 0.64` of the shell (60.3 × 136.1) | `(0.64, 0.08)` | vertical `#C9CCD3 → #8A8F99`, 1 px `#000 @.25`, Corners 3 | `:171-180` |
| MD strip | `0.84 × 0.14` of the shell (168.8 × 29.8) | bottom, inset `0.04·shellH` | `#F4EFE6`, Corners 2; label `H·0.44` (13.1) w600 CharSpacing 60 `#23262B` UPPERCASE | `:124,181-195,282-308` |

### 3.6 iPod (`IpodDeck.cs`)

| element | size | position | colour / type | source |
|---|---|---|---|---|
| ground | `s × s` | — | vertical `#3A3F4A → #22252C` | `:87-90` |
| body | `0.58s × 0.96s` (188.0 × 311.0) | `(0.21s, 0.02s)` | silver `#F7F7F9 → #CFD2D8` · dark `#2A2C31 → #0F1013`; Corners `0.055s` (17.8), `Shadow(34,16,0,#000 @.5)` | `:46,67-75` |
| LCD | `0.84·bodyW × 0.43·bodyH` (157.9 × 133.7) | `(+0.08·bodyW, +0.05·bodyH)` | white `#DFE6EA` ink `#1C2230` · green `#D6E6C4` ink `#1C2A14`; 2 px `#2A2F38`, Corners 3 | `:42-48,186-189` |
| title bar | `w × 0.14h` (18.7) | 0,0 | vertical `#F4F6F8 → #C7CDD6` + 1 px `#8B93A3` rule; "Now Playing" `max(6, w·0.053)` = 8.4 w700 | `:98,111-128` |
| transport glyph | `max(6, chrome·0.92)` (7.7), slot `chrome·1.4` | `(0.03w, 0.22·barH)` | `Theme.IconFont`, `#1C2230`, bound `Icons.Play`/`Icons.Pause` | `:130-136` |
| battery | `0.09w × 0.44·barH` (14.2 × 8.2) | right, `0.28·barH` | 1 px `#333333`, fill `#3AA757` at 70 % | `:107,137-145` |
| artwork | `0.34w` (53.7) | `(0.04w, 0.20h)` | cover, Corners 2 | `:250,277-278` |
| text column | width `0.54w` (85.3), gap `max(1, h·0.012)` | `(0.42w, 0.22h)` | title `max(7, w·0.059)` (9.3) w700 ink; artist + album `max(6, w·0.053)` (8.4) ink @.78 | `:251-283` |
| progress | `0.92w × 0.08h` (145.3 × 10.7) | `(0.04w, 0.91h − h_p)` | trough `#F6F9FB` + 1 px `#2A2F38`, Corners 2; fill `#7DB2EA → #2A6BC4` | `:104-105,152-166` |
| thumb | `max(4, progW·0.06) × progH·1.3` (8.7 × 13.9), rot 45° | rides the fill | `#E9EEF4` + 1 px `#2A2F38` | `:106,169-181` |
| clocks | width `0.30w` (47.4), size `max(6, w·0.049)` (7.7), height `ceil(size·1.45)` | `(0.04w | right, 0.99h − clockH)` | Consolas, ink; elapsed left, `-remaining` right | `:100-103,182-205` |
| wheel | `0.74·bodyW` (139.1) | centred, bottom at `0.955·bodyH` | silver radial `#F3F3F5 → #DCDEE3` · dark `#2F3239 → #1A1C21` · u2 `#D8262B → #8F1418`; 1 px `#000 @.15` | `:49-51,331-335,356-361` |
| centre button | `0.36·d` (50.1) | centred | dark `#3A3D45 → #1F2126` · light `#FFFFFF → #E6E8EC`; 1 px `#000 @.2` | `:325,336-353` |
| captions | `max(6, d·0.058)` (8.1), height `ceil(cap·1.5)` (13) | MENU top `0.09d`; ⏮ ⏭ at `0.08d` sides; ⏯ bottom `0.08d` | dark `#C9CED8 @.75` · light `#8D939D`; MENU w700 CharSpacing 80, glyphs `max(7, d·0.075)` (10.4) `Theme.IconFont` | `:326-329,340-345,370-386` |

### 3.7 Hi-fi VU (`VuDeck.cs`)

| element | size | position | colour / type | source |
|---|---|---|---|---|
| ground | `s × s` | — | vertical `#1A1B20 → #0F1013` | `:71` |
| meter | `0.43s × (0.82 of that)` (139.3 × 114.2) | y `0.12s`; x `0.05s` / `s − 0.05s − w` | plate ivory `#EFE6CF` · blue `#0A2A5A` · black `#141519`; Corners (6,6,14,14), 1 px `#000 @.5`, `Shadow(20,8,0,#000 @.5)` | `:48,58-59,65-66,158-164` |
| lamp | `w × h` | 0,0 | radial glow ivory `(1, .784, .471) @.25` · blue `(.471, .784, 1) @.25` · black `white @.12`, centre (.5,1), radius (.6,.4) | `:51-56,90-98` |
| dial arc | view-box path `M14 62 A40 40 0 0 1 86 62`, stroke 1.2 view units (1.67 DIP) | 100×82 box fitted to `w × h` | ink ivory `#222222` · blue `#9FD6FF` · black `#F0F0F0` | `:37,99-103,307` |
| red arc | `M68 32 A40 40 0 0 1 86 62`, stroke 3 (4.2 DIP), butt cap | same | red ivory `#C8321E` · blue `#FF6A4A` · black `#FF4A3A` | `:38,104-108` |
| ticks | radius 35→40 view units at −45°+10i°, stroke 1 (1.39) | same | ink, or red for values > 0 | `:39,109-113,304-317` |
| tick labels | `max(5, 5·w/100)` (7.0) | `(50 + 30 sin a, 62 − 30 cos a)·k`, centred by `len·size·0.30` | ink, red above 0; values −20 −10 −7 −5 −3 −1 0 +1 +2 +3 | `:35,116-131` |
| "VU" | `max(8, s·0.040)` (13.0) w700 CharSpacing 100 | centred at `0.52h` | ink | `:84,138-143` |
| channel label | `max(6, s·0.026)` (8.4) w700 | `(0.06w, 0.06h)` | ink @ 70 % | `:85,133-137` |
| needle | `1.5 × 0.78h` (89.1), origin (.5, 1) | `(w/2 − 0.75, 0.90h − needleH)` | ink, `HitTestVisible false` | `:83,145-150` |
| pivot cap | `0.08w` (11.1) | `(centre, 0.94h − pivot)` | radial `#555555 → #111111` | `:82,151-156` |
| knob | `0.09s` (29.2) | `((s−d)/2, 0.60s)` | radial `#8A8F99 → #2B2E35`, centre (.4,.35) r (.7,.7); pointer 2 × `0.30d`; `Shadow(8,3,0,#000 @.6)` | `:61,67,250-265` |
| LCD | `0.90s × 0.20s` (291.6 × 64.8) | `(0.05s, 0.91s − h)` | `#1A0D00`, 1 px `#3A2000`, Corners 4; amber `#FFB000` | `:60,68,213-233` |
| LCD bloom | `w × h` | behind the glyphs | radial amber @.12 → @0, centre (.5,.35), radius (.7,.8) | `:222-230` |
| clocks | width `0.34·lcdW` (99.1), size `max(10, s·0.055)` (17.8) | top row, space-between, pad `(0.04w, 0.03h)` | amber, Consolas, CharSpacing 60, `mm:ss` / `-mm:ss` | `:172-175,236-248,277-278` |
| info cells | `max(6, s·0.028)` (9.1) CharSpacing 140 | bottom row, space-between, gap 8 | amber @.85; left = `TITLE · ARTIST` uppercase (grows, ellipsis), right = `FORMAT · N KBPS` | `:173,193-208,280-298` |

### 3.8 Winamp (`WinampDeck.cs`)

| element | size | position | colour / type | source |
|---|---|---|---|---|
| ground | `s × s` | — | `#0F1218` | `:55` |
| window | `0.92s × (116/275 of that)` (298.1 × 125.7) | x `0.04s`; main y `0.10s`, EQ y `main + h + 0.02s` | fill wa2, 1 px `#000`, + a 1 px `wa3 @.45` inner bevel added LAST | `:46-54,168-180` |
| title bar | `w × 0.12h` (15.1) | 0,0 | `winamp-tbar.png` tinted wa3 over a wa1 fill; caption `max(6, s·0.024)` (7.8) `White` CharSpacing 240, on a wa1 chip with 8 px side padding | `:63-64,184-212` |
| time LCD | `0.32w × 0.30h` (95.4 × 37.7) | `(0.14w, 0.20h)` | black plate, digits `max(9, s·0.064)` (20.7) w700 Consolas CharSpacing 60, colour = lcd | `:65,68,81-94` |
| analyser box | `0.34w × 0.30h` (101.4 × 37.7) | `(0.50w, 0.20h)` | black, 1 px `#000`, `ClipToBounds` | `:69,95-100` |
| bars (main) | 19 bars in `(visW−2) × (timeH−2)` (99.4 × 35.7); **gap 1, pad 2** ⇒ `barW = (99.4 − 2·2 − 1·18)/19` = **4.1** | bottom-aligned (`AlignItems.End`) | gradient 90° `#FF4A4A → #FFD24A @.45 → lcd`; caps 1 px `White @.9` | `:99,216-257` |
| bars (EQ) | 19 bars in `(0.88w−2) × (0.68h−2)` (260.3 × 83.5) at `(0.06w, 0.18h)`; **gap 2, pad 3** ⇒ `barW` = **11.5** | — | same gradient; grid = 10 hairlines `White @.06` OVER them | `:146-159,279-288` |
| scope | 24 dots `1.5 × 1.5`, spacing `(w − 1.5)/23` | centre line | fill lcd | `:29,261-277` |
| stitle | `0.80w × 0.12h` (238.5 × 15.1) | `(0.14w, 0.54h)` | black plate; `Marquee` `max(6, s·0.026)` (8.4) Arial, speed 22, gap 24, FadeBand 0, StartDelay 0, Loop/Always | `:70,101-115` |
| cells | height `ceil(size·1.5)`, pad (4,1,4,1) | kbps `(0.14w, 0.69h)`, format `(0.34w, 0.69h)` | black plate, lcd text `max(6, s·0.024)`, no CharSpacing | `:116-117,290-295` |
| STEREO cell | width `small·4.2`, height `ceil(small·1.5)` | `(w − 0.08w − small·4.2, 0.69h)`, right-justified | **no black plate** (a bare box), lcd text, CharSpacing **100** | `:118-123` |
| position bar | `0.88w × 0.06h` (262.3 × 7.5) | `(0.06w, 0.82h)` | `#000` | `:71,124` |
| thumb | `12 × 0.108h` (13.6) | `posY − 0.4·posH`, `Translation(Frac·(posW−12), 0)` | linear `#8A92A6 → #4E5568`, 1 px `#000` | `:72,125-132` |
| transport keys ×5 | `max(6, s·0.044) × max(5, s·0.030)` (14.3 × 9.7), gap 2 | `(0.06w, 0.90h)` | vertical `#5A6480 → #38405A`, 1 px `#000` | `:73,133,299-318` |

### 3.9 WMP (`WmpDeck.cs`) and Canvas (`CanvasDeck.cs`)

| element | size | position | colour | source |
|---|---|---|---|---|
| WMP ground | `s × s` | — | `#04060C` | `WmpDeck.cs:99` |
| bars | 24 × `(s/24 − 4)` (9.5) wide, `0.55s` (178.2) tall + reflection `0.6·maxH` (106.9) | from the mid line | `colour()` bound, Opacity .85 / .35 | `:106-131` |
| wave dots | 32 × ⌀2 | across `s`, mid line | `White @.85`, deflection `(peak − .5)·0.36s` (±58.3) | `:133-146` |
| alchemy rings | ⌀ `2s(0.18 + 0.10r)` = 116.6 / 181.4 / 246.2, border 2 | centred | `colour()` bound; trails at Opacity .9 / .35 / .15 | `:162-186` |
| battery cells | 6×6 of `min(s/6, s/6)` = 54 | grid | `colour()` bound, Opacity .7 | `:192-218` |
| WMP cover | `0.22s` (71.3) | `(0.05s, 0.92s − cover)` | bound `Source`, Corners 3, `DecodePx 128`, placeholder `#14171F`, `Shadow(20,8,0,#000 @.6)` | `:45,57-71` |
| WMP caption | width `0.42s`, size `max(8, s·0.034)·0.72` (7.9) CharSpacing 140 | right, `0.92s − capSize·1.4` | `White @.45`, `Loc.Bind(choice.LabelKey)` | `:46,72-84` |
| WMP progress | `s × 2` | `y = s − 2` | track `White @.12`, fill `colour()` `Scale(Frac,1)` | `:85-97` |
| Canvas ground | `s × s` | — | `#05070C` | `CanvasDeck.cs:62` |
| bleed | `1.5s` (486) | `(−0.25s, −0.25s)` | cover at `BakedBlur(44, .25)`, `Saturation 1.5`, `DecodePx 256`, Opacity 1.0 (strong) / 0.6 (soft) | `:36,45,104,143-157` |
| drift frame | `0.78s` (252.7), inner `1.18·frame` (298.2) | `(0.11s, 0.11s)`, inner at `−0.09·frame` | Corners 6, `Shadow(60,24,0,#000 @.45)`, `DecodePx 512` | `:103-105,161-182` |
| Canvas seek line | `0.78s × 2` | `(0.11s, 0.95s − 2)` | track `White @.35`, fill `White` `Scale(Frac,1)` | `:38,49-61` |
| WMP band→node map | — | — | bars/reflections: band `i` (0-23, 1:1). Wave dots: `Peaks[i % 24]`, so dots 24-31 REPEAT peaks 0-7 — the trace is deliberately not 32 distinct values. Alchemy: `Bands[(r·7 + trail) % 24]` (9 rings ⇒ bands 0,1,2,7,8,9,14,15,16). Battery: `Bands[(i·3 + j) % 24]` ⇒ the grid pulses in diagonals, not columns. | `:139,168,204` |

### 3.10 Shared art helpers (`DeckArt.cs`)

| helper | contract | source |
|---|---|---|
| `Cover(url, size, corners, placeholder, blurHash)` | `ImageEl`, `Fit = Cover`, `DecodePx = DecodeFor(size)` | `:36-47` |
| `DecodeFor(size)` | `px = size·1.5` → 64 / 128 / 256 / 512 rungs | `:118-124` |
| `Circle(d, fill)` / `Ring(d, w, c)` | filled disc / hollow-SDF ring (composites over what is under it) | `:50-69` |
| `Texture(file, w, h, corners, tint)` | `ImageEl` with `ColorOverlay = tint` and `Placeholder = ColorF.Transparent` (so a missing decode shows the fallback UNDER it, never a grey plate) | `:73-84` |
| `AssetPath(file)` | `AppContext.BaseDirectory/assets/deck/<file>`, memoized in a `Dictionary<string,string>` (stable string = stable image-cache key) | `:87-93` |
| `CoverUrl(track)` | `ImageSource.Normalize(track.Image.Url)` — the SAME normalisation the hero tile uses, so both hit one decode and one colour-plane entry | `:97-98` |
| `CoverWash(url)` / `CoverAccent(url, fallback)` | bound `Prop<ColorF>`; both `Watch(url)` INSIDE the thunk so the bind subscribes, not the face | `:103-113` |
| assets | `grooves-1024.png` (115 KB) · `wood-grain-1024.png` (**1.07 MB** — a 512 version is a known follow-up) · `marble-1024.png` (**382 KB**) · `cd-rainbow-512.png` (50 KB) · `reel-flange-512.png` (4.5 KB) · `winamp-tbar.png` (145 B, 256×16 stripes) | `assets/deck/` |
| `CoverWash` / `CoverAccent` | **UNUSED by all nine faces in 0.2.9** — every cover slot passes `NowPlayingPanel.HeroWashColor(url)` as a VALUE (the leaf re-renders on `CoverGen` anyway, `RecordDeck.cs:573-575`) and `WmpDeck`/`RecordDeck` each resolve the live accent inside their own thunk because the url changes under a face that was built once (`WmpDeck.cs:229-237`). Port them only if 0.3 actually binds a fixed url. | `:103-113` |

### 3.11 Text slots — case, trim and what a long string does

Every string a deck draws is one line. There is no wrapping anywhere on this surface; what differs is the OVERFLOW,
and it is not uniform — three slots CLIP (the glyphs are simply cut), six ellipsize, one scrolls forever.

| slot | case | width | overflow | source |
|---|---|---|---|---|
| Zune title (2 runs: base + accent tail) | `ToLowerInvariant` | the 0.34s block, `ClipToBounds` | **`TextTrim.Clip`** — a long title is cut mid-glyph, never "…"; a title < 3 chars gets NO accent run (`cut = len`) | `RecordDeck.cs:665,671,686-695` |
| Zune artist line | as-is + `" · "` | fills, beside a FIXED 0.10s clock slot | `Clip` | `:704-714` |
| cassette label title / second | `chrome` ⇒ `ToUpperInvariant` | `labelW − 2·pad − badgeD` | `CharacterEllipsis` | `CassetteDeck.cs:317-321,345-366` |
| cassette second line | — | — | composed `artist · album`, or whichever exists | `:315-316` |
| cassette brand "wavee · c-60 · type i" | literal | 0.17s | `Clip` | `:127-136` |
| MD paper strip | `ToUpperInvariant` | `stripW·0.94` | `CharacterEllipsis`; composed `title · artist`, or whichever exists | `CdDeck.cs:291-306` |
| CD caption | literal | 0.80s | `Clip` | `CdDeck.cs:41-52` |
| iPod title / artist / album | as-is | `0.54w` each | `CharacterEllipsis`, `NoWrap`; artist is `Artists[0].Name` when there is exactly ONE, `DetailFormat.ArtistNames` otherwise | `IpodDeck.cs:258-292` |
| iPod "Now Playing" | literal | centred | `Clip` | `:121-124` |
| VU NOW line | `ToUpperInvariant` | grows (`Grow 1, MinWidth 0`) | `CharacterEllipsis`; **first artist only** (`Artists[0].Name`), never the joined list | `VuDeck.cs:197-202,280-286` |
| VU FORMAT cell | `ToUpperInvariant` | `Shrink 0` | no trim — it wins the row; `"FORMAT · N KBPS"` / `"N KBPS"` / `""` | `:203-207,290-298` |
| Winamp stitle | as-is | 0.80w plate | **`Marquee`, Loop/Always** — it never stops, long or short; `"1. <first artist> - <title> (m:ss) *** "`, or `"1. Wavee *** "` with no track | `WinampDeck.cs:101-115,329-335` |
| Winamp title-bar caption | literal | chip | `Clip` | `:202-206` |
| Winamp kbps / kHz cells | — | auto | `"--- "` when the bridge has no fact (never an invented number) | `:337-349` |
| WMP preset caption | `Loc.Bind` | 0.42s | `CharacterEllipsis` | `WmpDeck.cs:78-82` |
| reel counter | digits | fixed | `sec mod 10000`, `D4` — it WRAPS at 2:46:40 rather than growing | `ReelDeck.cs:218-229` |

---

## 4. Colour & material

### 4.1 The four cover-derived channels (everything else on a deck is a literal)

| channel | input → function | where applied | how it transitions |
|---|---|---|---|
| **cover wash** (placeholder) | `url → CoverColorPlane.Watch(url)` (subscribe) → `Surfaces.SchemeFor(url)` → `WaveePalette.Lift(WaveePalette.Accent(scheme))` → `ColorF.Lerp(Tok.FillCardSecondary, accent, dark ? 0.18 : 0.10)` | `NowPlayingPanel.HeroWashColor` (`:203-210`), passed as `ImageEl.Placeholder` by every cover slot: record sleeve/label/picture disc (`RecordDeck.cs:575`), CD disc art (`CdDeck.cs:277`), iPod artwork (`IpodDeck.cs:278`), Canvas bleed + frame (`CanvasDeck.cs:110,153,175`) | a VALUE, resolved in the leaf's own render; the leaf re-renders on `CoverGen`, so a late grading lands on the next swap (WMP's corner cover uses a flat `#14171F` instead — `WmpDeck.cs:68`) |
| **album-colour vinyl** | live `bridge.CurrentTrack` → `DeckArt.CoverUrl` → `Watch(url)` → `SchemeFor` → `Accent` → `c × 0.55` (deepened: dyed vinyl, not paint); fallback `#1C4F9A` | the record body's `Fill` when finish = "album" (`RecordDeck.cs:282,382-391`) | a BOUND `Prop<ColorF>` read inside the thunk — a track change or a late grading repaints ONE fill and never rebuilds the face |
| **WMP accent** | `colour` option: `"cover"` → the same chain, no deepening, fallback `Tok.AccentDefault`; `"accent"` → `Tok.AccentDefault` | every bar / ring / cell `Fill` and the progress fill (`WmpDeck.cs:41-47,229-237`) | bound; a theme switch AND an album change both repaint without a rebuild |
| **deck ground** | `Tok.FillCardSecondary` (bound `Prop`) on Record/Picture; `#000000` on Zune; `#4A301B` on Turntable | the 7" spindle hole, so the hole shows the card it sits on (`RecordDeck.cs:62-66,344`); the CD clamp hole uses `Tok.FillCardSecondary` directly (`CdDeck.cs:242`) | bound on the record family (a re-theme repaints the hole); a static token read on the CD |

Everything else — 11 face palettes, ~90 literal hex values — is deliberately theme-independent (`RecordDeck.cs:31-37`
states the rule). In practice this means **a deck looks identical in light and dark**, which is correct for a physical
object and is the reason the deck sits in a `ClipToBounds` card rather than blending with the rail.

### 4.2 Materials actually used

| material | where | parameters | why not something else |
|---|---|---|---|
| `AcrylicSpec` | cassette "clear" shell only | `(#BECDE6, .28, blur 18, noise .02, lum .92, tint #BECDE6 @.28)` (`CassetteDeck.cs:106-108`) | it is the one part that genuinely reads THROUGH to the deck ground; the other three shells are plates |
| flat translucent fill | record "clear" finish | `rgba(40,44,54,.55)` (`RecordDeck.cs:286`) | the node rotates 30×/s — a per-node backdrop layer would resample and re-blur the plinth every frame for a tint a fill already gives |
| `BakedBlurSpec` | Canvas bleed | `(σ 44, .25)` + `Saturation 1.5` (`CanvasDeck.cs:154-155`) | derives a persistent blurred bitmap ONCE; the bleed stays an ordinary textured quad |
| `ShadowSpec` | 14 places | record disc `(34,14,0,#000 @.45)` · sleeve `(30,10,0,@.25)` · arm pivot `(8,3,0,@.35)` · arm tube twin `(10,10,0,@.45)` · cassette/MD/CD `(30,14,0,@.5/.45/.4)` · reel head `(18,8,0,@.45)` · iPod body `(34,16,0,@.5)` · VU meter `(20,8,0,@.5)` · VU knob `(8,3,0,@.6)` · WMP cover `(20,8,0,@.6)` · Canvas frame `(60,24,0,@.45)` · strobe dot `(10,0,0,#FF5A2A @.9)` · CD sled `(8,0,0,#FF3B30 @.85)` | `ShadowSpec` is **not a bindable channel**; that is why the tonearm's lift-shadow is a separate transparent twin whose OPACITY is bound (`RecordDeck.cs:453-463`) |
| gradients | everywhere | `GradientSpec.Vertical`, `Ui.GradientDown`, `Ui.RadialGradient(center, radius, stops)`, `Ui.LinearGradient(angle, …)`, and one explicit `new GradientSpec(Linear, 160°, …)` for the MiniDisc | the renderer has **no conic and no repeating gradient** — those six shapes ship as alpha PNGs instead (§3.10) |
| `PathEl` + `PathGeometryTable` | VU scale (3 paths, interned once at type init), reel tape (1 path minted once) | view box 100×82 (VU) / 100×100 (reel); the view-box fit scales the STROKE with the geometry (`Element.cs:582-586`) | 3 interned paths instead of 30 boxes; parsed and tessellated exactly once per process |

### 4.3 On-media ink

There is none in the engine's sense: no deck paints app ink over artwork. The two places text sits on a
cover-derived surface are the cassette label and the MiniDisc strip, and both paint their own paper (`#F2E9D8` /
`#FFFDF5` / a chrome gradient / `#F4EFE6`) under `#23262B` ink. The Zune's title sits on `#000000`. The iPod's ink is
the LCD's own (`#1C2230` / `#1C2A14`).

---

## 5. Motion

Every row below is driven from `DeckClock`'s 33.33 ms fold unless the "driver" says otherwise. `FrameTime.NowMs`
(= `FrameClock.PresentQpc`) is the clock everywhere; `Environment.TickCount64` appears nowhere in this surface.

### 5.1 Host / choreography

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| deck mount (Cover→Player, preset change, rail resize re-key) | deck root | Opacity | 0 → 1 | 320 ms | `Easing.SmoothOut` | — | **KEPT** (a cross-fade, not decoration) | `DeckHost.cs:33-36` |
| `CoverGen` bump | sleeve / label / picture / disc / MD label / cassette label / Zune type / Canvas canvas | Opacity (a keyed REMOUNT) | 0 → 1 | 300 ms | `SmoothOut` | — | **KEPT** | `RecordDeck.cs:41-44`, `CanvasDeck.cs:80-83` |
| `Thump` edge (one tick) | the .08D dust ring | Opacity | 0.9 → 0 | 500 ms | Linear → SmoothOut | fires once per stylus landing | suppressed (`Thump` is false under reduced motion) | `RecordDeck.cs:601-623`, `TonearmMachine.cs:380` |
| same | same | ScaleX / ScaleY | 0.2 → 1.6 | 500 ms | `SmoothOut` both keys | — | suppressed | `RecordDeck.cs:602` |

### 5.2 The tonearm machine — the complete duration table (`TonearmMachine.cs:105-110`)

| leg | duration (ms) | arm curve | lift curve | platter | phase name |
|---|---|---|---|---|---|
| `SlideOut` | 600 | arm parked at −34° | 1 | off → on at the end | Cueing |
| `Cue` | 300 | −34° | 1 | ON | Cueing |
| `SwingToLead` | 1200 | −34° → −20°, `Std` = cubic-bezier(.4,0,.2,1) | 1 | on | Cueing |
| `Lower` | 900 (700 after a seek) | holds `ToDeg` | `1 − Damped(t)`, Damped = cubic-bezier(.2,.6,.3,1) | on | NeedleDown |
| `Tracking` | ∞ | `AngleOf(frac)` = −20 + 17·frac | 0 | on | Playing |
| `Lift` | 450 · 400 (seek) · 300 (skip/drag) · 500 (auto-return) | holds `FromDeg` | `LiftFrom + (1−LiftFrom)·LiftUp(t)`, LiftUp = cubic-bezier(.2,0,0,1) | unchanged (or a scheduled brake) | Pausing / Seeking / NextTrack / ChangingRecord / AutoReturn / Error / Buffering (by destination) |
| `SpinUp` | 700 | holds | 1 | ON | SpinningUp |
| `SeekSwing` | `300 + 600·|Δ|/17` (300…900) | `Std` | 1 | per `ResumeDown` | Seeking |
| `RecueSwing` | 900 | `Std` to −20° | 1 | **never off** | NextTrack |
| `SwingToRest` | 1200 (change/error) · 1400 (auto-return) | `Std` to −34° | 1 | brake at +700 (change) / +0 (error) / +500 after the lift (auto-return) | ChangingRecord / Error / AutoReturn |
| `SleeveIn` | 600 | −34° | 1 | off | ChangingRecord |
| `CoverSwap` | 350 | −34° | 1 | off | ChangingRecord |
| `RunOut` | `400 + 800·|Δ|/17` | **LINEAR** (a groove, not a swing) | 0 | on | RunOut |
| `LockedGroove` | 1800 (33⅓) · 1333 (45, `Rpm > 40`) | parked at −3° | 0 | on | LockedGroove |
| `Hover` | ∞ | holds `ToDeg` | `1 + 0.4·(0.5 − 0.5·cos(2π·t/1800))` ⇒ 1 → 1.4 → 1 | ON | Buffering |
| `Dragging` | ∞ | `AngleOf(FracOf(ScrubTargetMs))` | 1 | unchanged | Seeking |
| `Paused` / `Stopped` / `Idle` / `Unavailable` | ∞ | `ToDeg` (Paused/SpinUp) else −34° | 1 | off | Paused / Stopped / Idle / Unavailable |
| reduced motion | every leg above | `Dur(ms) = min(ms, 150)` | — | target 0 (no rotation at all) | — |
| thump window | 120 ms after `Lower` completes | — | — | — | — |
| seek dead zone | `|Δ| < 0.15°` ⇒ no movement | — | — | — | — |

**The other decks' phase names** (`DeckSignals.Phase` is medium-agnostic and carries all 18 `DeckPhaseName`s — the
record family reaches 16 of them through the two `NameOf` overloads; these three models publish the rest):

| model | names it can publish | rule | source |
|---|---|---|---|
| `TapeModel` | **`Winding`** · `Playing` · `Paused` | `Winding` whenever the 900 ms fast-wind window is open (wind > 1) — the ONE name no other model uses | `TapeMachine.cs:100` |
| `DiscModel` | `Buffering` · `Playing` · `Paused` | buffering wins over playing | `DiscMachine.cs:64` |
| `MeterModel` / `LevelModel` | `Playing` · `Paused` | nothing else — these two faces have no transport story | `MeterBallistics.cs:58`, `LevelSynth.cs:98` |
| `ProgressModel` | `Error` · `Idle` · `Buffering` · `Stopped` · `Seeking` · `Playing` · `Paused`, in that priority | the iPod and Canvas get the full transport vocabulary for free | `ProgressModel.cs:31-39` |

### 5.3 Per-tick integrators

| model | property | law | constants | settles when | source |
|---|---|---|---|---|---|
| platter | ω (°/s) → angle | `ω += (target − ω)(1 − e^(−dt/τ))`, `angle = (angle + ω·dt) mod 360` | τ↑ 0.23 s, τ↓ 0.53 s; during a 33⇄45 flip both become 0.17 s for 600 ms | `target == 0 && |ω| < 0.05` ⇒ ω snapped to an exact 0 | `RecordModel.cs:17-23,85-99`, `SpinIntegrator.cs:40-49` |
| tape hubs ×2 | ω | `ω = −wind·base/r` at constant LINEAR speed | cassette base 900, τ .25/.30; reel base 6000, τ .35/.40; `wind` = 4 (cassette) / 5 (reel) for 900 ms after a committed seek or a >2500 ms jump, else 1 | both hubs at rest and no eject running | `TapeMachine.cs:36-39,56-62,113-118` |
| tape packs | Aux0/Aux1 | `rL = 26 − 13p`, `rR = 13 + 13p` (cassette, % of s) · `92 − 48p`, `44 + 48p` (reel); published as `r / rMax` | rMax 26 / 92 | — | `TapeMachine.cs:49-50,107-109` |
| CD spindle | ω | CLV: `3000 − 1800·p` °/s while playing, 0 otherwise | τ .4/.6 | disc at rest and no eject | `DiscMachine.cs:14,29,71` |
| CD sled | Aux0 | `(20 + 26·p)/100` (a fraction of D) | — | — | `DiscMachine.cs:63` |
| VU needles | angle | `angle += (target − angle)(1 − e^(−dt/τ))`, `target = DbToDeg(RmsToDb(rms))` | τ 0.3 s (VU) / 0.05 s (PPM); rest −45°; clamp [−48°, +48°] | not playing and both within 0.25° of rest | `MeterBallistics.cs:11,29,53,62-63` |
| VU right channel | angle | `dbR = dbL + 1.5·sin(2.1·t)` dB (a derived stereo image from a MONO tap — label it honestly) | ω 2.1 rad/s ⇒ 2.99 s period | — | `MeterBallistics.cs:12,48` |
| VU "breathing" (no tap) | angle | `dbL = RmsToDb(0.12) + 3·sin(0.6·t)` ⇒ ≈ −0.42 ± 3 dB | ω 0.6 rad/s ⇒ 10.5 s period | — | `MeterBallistics.cs:11,47` |
| analyser bands | band i | `target = max(.02, env·(.35 + .45·sin(t(3+.37i)+seed+i)·sin(1.3t+.8i)) + rmsN·hash(i,t)·.15)`; attack 0.5, release 0.12 per tick | `env = clamp(peak·2.2)`, `rmsN = clamp(rms·3.5)`; no tap ⇒ `env = rmsN = playing ? .8 : 0`; floor .02 snapped within .005 | not playing and every band at the floor | `LevelSynth.cs:11-16,45-96` |
| analyser peaks | peak i | hold 400 ms at the last maximum, then fall 1.2 / s | — | — | `LevelSynth.cs:12-13,102-117` |
| scope mode | band i | `0.5 + 0.38·sin(i·0.5 + t·9)·sin(i·0.08 + t·2)·env` (a centred trace) | — | **NEVER** — the trace rests at 0.5, which is above the 0.02 floor `IsSettled` tests, so a paused Winamp-with-scope deck keeps `IsSettled == false` and the 30 Hz ticker NEVER stops (see §6.4(5)) | `LevelSynth.cs:63-71,88-96` |
| Canvas drift | TranslateX/Y, ScaleX/Y | 4 looping keyframe tracks, mirrored 0 → ½ → 1 → ½ → 0 | `(0,0,0,1) (0.5,−0.05,0.03,1.06) (1,0.04,−0.04,1.02)`, `Easing.EaseInOut`, 40 s (slow) / 16 s (fast) | — | `DriftPath.cs:8-17`, `CanvasDeck.cs:129-134,188-213` |

### 5.4 Scroll-linked, tickers and progress

| trigger | target | property | duration / rate | source |
|---|---|---|---|---|
| position (30 Hz) | Zune bar · iPod progress + thumb · Winamp thumb · WMP line · Canvas seek line | `Scale(Frac,1)` / `Translation(Frac·span, 0)` | quantised at 1/1024 of the track ⇒ ~4.3 writes/s on a 236 s track | `RecordDeck.cs:190`, `IpodDeck.cs:163,172`, `WinampDeck.cs:131`, `WmpDeck.cs:94`, `CanvasDeck.cs:58` |
| position (1 Hz effective) | Zune clock · iPod elapsed/remaining · Winamp time · VU clocks · reel counter | bound `Text` | one `FormatCache` entry per whole second — a mounted deck stops allocating after the first minute | `IpodDeck.cs:32-33,210-217`, `VuDeck.cs:41-42,271-278`, `WinampDeck.cs:32,322-327`, `ReelDeck.cs:210-229` |
| track change | Winamp `stitle` | `Marquee` Loop | 22 DIP/s, gap 24, no start delay, always on | `WinampDeck.cs:108-113` |
| alchemy | 3 rings × 3 trails | `Rotation(Frac·τ·6(r+1) − 0.15·trail)` | 6 / 12 / 18 full turns across one track (progress-driven, not time-driven) | `WmpDeck.cs:165-181` |
| `PositionMs` signal (~1 Hz) | interpolator | `Anchor(FrameTime.NowMs, posMs)` | — | `DeckClock.cs:91-101` |
| track uri change | boundary | `Classify(...)` then an EAGER `Tick()` — a skip must not wait 33 ms, and must not wait AT ALL when paused | — | `DeckClock.cs:105-112` |
| ticker gate flip | the state-change clock | one fold per transport edge | — | `DeckClock.cs:133-134` |

### 5.5 What is NOT animated (and must stay that way)

- No width/height is ever written at tick rate: every progress indicator is a **scale or a translate** about a pinned
  origin, precisely so a tick never enters layout (`RecordDeck.cs:182-191` states the rule).
- The only relayout inside a running deck is a bound `TextEl`'s scoped re-measure, and every such slot has a FIXED
  `Width` so it cannot move its neighbour (`RecordDeck.cs:709-714`, `IpodDeck.cs:193-205`, `VuDeck.cs:236-248`,
  `WinampDeck.cs:81-94`).
- The strobe dot does not blink, the battery does not drain, the Winamp keys do not depress, and no deck has a hover
  state. Adding any of them is new design, not parity.

---

## 6. Interaction

### 6.1 The complete interactive inventory

| part | events | cursor | role / focus | what it does | source |
|---|---|---|---|---|---|
| record headshell grip (31 × 60 DIP at side 324, inside the rotating arm) | `OnPointerDown`, `OnDrag`, `OnPointerReleased` | `CursorId.Hand` | none, not focusable | `MsAt(p)` → `Gesture.Begin/Move` → `Gesture.Commit(grip.LastMs)` | `RecordDeck.cs:502-512` |
| iPod wheel (the whole ⌀139 circle) | `OnPointerDown`, `OnDrag`, `OnClick` (used as the drag-END edge), `OnDragCanceled` | `CursorId.Hand` | `AutomationRole.Slider`, not focusable | angular scrub, ¼ track per turn | `IpodDeck.cs:356-367,392-437` |
| iPod centre button (⌀50) | `OnClick` | `CursorId.Hand` | `Role.Button`, **Focusable** | `PlayerBar.TogglePlayPause` (optimistic flip + Pause/Resume) | `IpodDeck.cs:346-353,388` |
| iPod ⏮ | `OnClick` | Hand | Button, Focusable | `Player.PreviousAsync()` | `IpodDeck.cs:343,389` |
| iPod ⏭ | `OnClick` | Hand | Button, Focusable | `Player.NextAsync()` | `IpodDeck.cs:344,390` |
| iPod ⏯ (wheel bottom) | `OnClick` | Hand | Button, Focusable | TogglePlayPause | `IpodDeck.cs:345` |
| iPod MENU | — | none | `Role.None` | inert, decorative | `IpodDeck.cs:342,370-378` |
| Winamp key 1..5 | `OnClick` | Hand | Button, Focusable | Previous · Resume · Pause · **Pause** (Winamp's STOP has no streaming equivalent — the honest mapping) · Next | `WinampDeck.cs:299-318` |
| everything else | — | — | `HitTestVisible false` on the WMP/Canvas/VU/iPod-screen canvases and the VU needle | — | `WmpDeck.cs:148,186,218`, `CanvasDeck.cs:183`, `IpodDeck.cs:284`, `VuDeck.cs:147` |
| the hero wrapper (NOT a face) | `OnContextRequested` | — | — | the artwork context menu — **Ch.21 §6.3** | `NowPlayingPanel.cs:552-554` |

**There is no hover state anywhere on a deck**, and no pressed state: the only pointer feedback is the cursor. That
is deliberate — these are physical objects, and a lit hover plate on a 1990s bitmap key or a tonearm would break the
illusion. Do not "improve" this in 0.3.

### 6.2 The scrub contract (shared by both grips)

```
Begin(ms)   captures the gesture IDENTITY (track uri + active device id), clamps to [0, duration],
            sets ScrubTargetMs — so the medium follows the finger from the first frame        DeckGesture.cs:43-51
Move(ms)    rewrites ScrubTargetMs (identity re-checked every sample)                                     :54-58
Commit(ms)  SeekTargetMs = target (the deck keeps the arm THERE until the report converges),
            then bridge.CommitSeek(target) — the ONE commit path: it arms the bridge latch and
            publishes the position optimistically                                                          :61-69
Cancel()    clears the scrub; nothing audible happened, so there is nothing to put back                    :73
Enabled     CurrentTrack ≠ null && Error == null && CanSeek                                                :88
Current     _active && Enabled && same track uri && same active device                                     :90-93
```

The models never see a pointer: they react to `ScrubTargetMs ≠ null` (a drag is live) and `SeekTargetMs ≠ null` (a
committed seek awaiting its acknowledgement). That is exactly why the tonearm is unit-testable without an input stack.
`DeckClock` releases the committed latch when the reported position lands within 500 ms of the target, or after 2000 ms,
whichever comes first (`DeckClock.cs:219-223`).

The record grip maps a pointer sample through the arm's own rotation:
`ArmLocalToDeck(p + hitOrigin, armAngle.Peek(), …)` → `FracFromDeckPoint(x, y, platterCx, platterCy, D)` →
`frac · DurationMs` (`RecordDeck.cs:427-432`). The groove band is `0.34R … 1.00R`, **rim = position 0, label edge =
position 1** — the direction a stylus actually travels (`TonearmMachine.cs:449-454`).

The wheel differences the pointer's polar angle, unwraps the ±π seam and applies a 0.25 gear ratio
(`IpodDeck.cs:403-419`); a tap that never moved calls `Cancel`, not `Commit`, so touching the rim cannot fire a
pointless seek to where the playhead already is (`:421-430`).

### 6.3 Keyboard, accessibility, localisation

- **Keyboard:** nothing in a deck is keyboard-operable except the **nine** `Focusable` buttons (iPod centre + ⏮ ⏭ ⏯
  = four, Winamp ×5), which activate on the engine's standard button chord. There is **no keyboard scrub** on either
  grip, and no arrow-key nudge. The deck never traps focus and never takes focus on mount.
- **Commands (not a face, but they change this surface):** the command palette carries `settings.npvPresentation`
  (Cover ⇄ Player) and `settings.npvNextStyle` (cycle to the next of the twelve presets, wrapping) —
  `WaveeCommands.cs:213-214`, routed through `NpvPlayerPrefs.TogglePresentation` / `NextStyle` with the
  `SourcePalette` diagnostics source. `NextStyle` is a REMOUNT of this whole surface, so it must keep working in 0.3.
- **Automation:** roles are set (`Slider` on the wheel, `Button` on the nine keys) but **no `AutomationName` is set
  anywhere in the 29 files** — a screen reader announces unnamed buttons. Treat this as a defect to fix during the
  re-author, not as parity: give the wheel a name ("Seek"), the transport keys their existing player-bar names, and the
  deck root a name built from the preset's `LabelKey`.
- **Tooltips:** none. (The option rows that DO carry tooltips live in the flyout — Ch.21.)
- **Localisation:** the faces are almost entirely non-linguistic. Exactly four literal strings ship inside faces, and
  three are intentionally NOT localised because they are part of the device's own look: `"Now Playing"`
  (`IpodDeck.cs:121`), `"MENU"` (`:342`), `"WINAMP"` / `"WINAMP SPECTRUM ANALYZER"` / `"WINAMP OSCILLOSCOPE"`
  (`WinampDeck.cs:79,150`), `"STEREO"` (`:122`), `"COMPACT DISC · DIGITAL AUDIO"` / `"MD · ATRAC"` (`CdDeck.cs:41`),
  `"VU"` / `"LEFT"` / `"RIGHT"` (`VuDeck.cs:65-66,142`), `"wavee · c-60 · type i"` (`CassetteDeck.cs:127`), the reel
  counter's `"0000"` and Winamp's `"1. Wavee *** "` placeholder (`WinampDeck.cs:332`). The ONE localised string a face
  renders is the WMP preset caption, via `Loc.Bind(choice.LabelKey)` → `player.choice.barsWaves` |
  `player.choice.alchemy` | `player.choice.battery` (`WmpDeck.cs:36,78`).
- **Option labels** (flyout-owned, listed here because they name what each face draws):
  `player.style.{record,cassette,reel,cd,turntable,ipod,winamp,vu,zune,wmp,canvas,picture}` (+ the `*Short` variants),
  `player.opt.{finish,size,speed,sleeve,shell,label,side,reels,format,body,lcd,skin,analyser,face,needles,accent,preset,colour,drift,bleed}`
  (20 distinct option KEYS behind 28 option ROWS — `Finish`/`Size`/`Rpm`/`Sleeve` are shared instances reused by
  Record, Turntable, Picture and Zune), and `player.choice.*` — **45** distinct keys behind 53 choice entries.
  All three locales carry all of them, verified key-for-key: `en-US` / `nl` / `ko-KR` each hold 19 `player.style.*`
  (12 + 7 `*Short`), 20 `player.opt.*` and 45 `player.choice.*` (`assets/loc/{en-US,nl,ko-KR}.json`).

### 6.4 Known interaction defects in 0.2.9 (do not re-create)

1. The record grip wires `OnPointerDown/OnDrag/OnPointerReleased` but **no `OnDragCanceled`** (`RecordDeck.cs:509-511`),
   while the wheel wires all four (`IpodDeck.cs:363-366`). If a capture is lost without a release the gesture stays
   `_active` and `ScrubTargetMs` stays set, so the arm parks in `Dragging`. (Whether the engine synthesises a release
   on capture loss is UNVERIFIED — the fix is one line either way: wire `OnDragCanceled → Gesture.Cancel()`.)
2. `IpodWheel` keeps its drag state in instance fields rather than hooks (`IpodDeck.cs:315-318`) — safe only because the
   component is keyed on the body finish and nothing re-pushes into it. Keep the key if you keep the fields.
3. `RecordThump` subscribes `host.ThumpRequested` in a `UseLayoutEffect` with `DepKey.From(0)` and returns the
   unsubscribe (`RecordDeck.cs:611-628`) — the dep key must stay constant, or a re-render would re-subscribe.
4. **Two options never reach the mounted model.** `DeckHost` builds the model only when `_model is null` (`:70`), and
   the host Key carries only the preset slug and the side. So flipping **VU › needles (vu ⇄ ppm)** or
   **Winamp › analyser (spectrum ⇄ oscilloscope)** restyles the FACE in place but leaves the old physics running:
   the VU keeps its old τ, and the scope's 24 dots are fed spectrum values (0..1 envelopes) instead of a centred
   trace — visibly wrong (the dots sit near the bottom and jitter). Both only take effect on the next remount
   (preset change, rail resize, or a Cover→Player round trip). **Fix in 0.3**: either add the two option slugs to
   the host Key, or rebuild the model when the model-option hash changes. `rpm` is fine — it is a per-tick `Func`.
5. **A Winamp deck with the oscilloscope option never settles.** `LevelModel`'s scope branch parks every band at
   0.5 (`LevelSynth.cs:67`), and `IsSettled` is "not playing AND every band at the 0.02 floor" (`:88-96`), so it is
   permanently false: the 30 Hz ticker runs for as long as the rail is open, paused or not. Parity item 56 ("a
   paused deck costs zero frames") is therefore FALSE for exactly this one option combination in 0.2.9. Fix it in
   0.3 (settle when `!playing` and the trace is flat) rather than porting the burn.
6. **The record grip commits a seek to 0 when the duration is unknown.** `MsAt` multiplies the groove fraction by
   `DurationMs.Peek()`, and `DeckGesture.Enabled` checks `CanSeek` but not the duration (`RecordDeck.cs:431`,
   `DeckGesture.cs:88,97-101`). The click wheel refuses correctly (`IpodDeck.cs:394,406`). Gate both on
   `DurationMs > 0` in 0.3.
7. **`NpvPlayerCatalog.Option(preset, slug)` falls back to `Options[0]`, not to nothing** (`:87`). Asking a Zune for
   `"size"` returns the ACCENT option and therefore the slug `"pink"`; asking a Cassette for `"rpm"` returns
   `"black"`. 0.2.9 is safe only because every such read is guarded by a variant flag (`small = !zune && …`,
   `showSleeve = !zune && …`, `RecordDeck.cs:59-60`) or compares against a slug the fallback can never produce.
   Keep the guards, or make the 0.3 accessor return a `bool TryOption` and force the caller to say what it wants.

---

## 7. Data & readiness in 0.3 terms

A deck reads almost nothing from the entity graph: it is a view of the TRANSPORT plus one track's identity. The
readiness rule for this surface is therefore narrow and strict — **the machine runs regardless, the artwork and the
lettering wait.**

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| whether a deck exists at all | `NowPlayingHeroTile`: `bridge.CurrentTrack.Value is not null` | `Playback.Current.IsValid` | the slot mounts nothing without a current track (Ch.21) |
| arm angle / bar / sled / pack radii / thumb | `PositionMs` (interpolated) ÷ `DurationMs` | `Playback.PositionMs` + `Playback.State.PosQpc` interpolation ÷ **duration (GAP 6)** | duration 0 ⇒ `Frac` is 0, never a fiction (`DeckInput.cs:50`) — the arm parks at the lead-in rather than guessing |
| platter / hub / disc running | `IsPlaying`, `IsBuffering`, `Error` | `Playback.PhaseSignal` (needs Buffering + Failed, **GAP 5**) | immediate; no data dependency |
| run-out / locked groove | phase == Ended (derived: not playing within 1.5 s of the end) | `Phase.Ended` | immediate |
| boundary (recue vs change record) | `track.Uri` + `track.Album.Uri` + prev position/duration/phase + `Repeat == Track` | `Playback.Current` slot + `Track.Album` **slot** + the same prev values + `State.Repeat` | both slots must be valid; a 0/unknown album slot must read as "not the same album" (never as same) |
| sleeve / label / picture disc / CD disc / iPod artwork / Canvas bleed+frame | `track.Image?.Url` → `ImageSource.Normalize` | `Track.Image` (StringId) + `Knows(TrackFields.Image)` | until `Knows(Image)`: paint the cover wash placeholder only — never an empty rect, never a spinner |
| blur-hash first paint | `track.Image?.BlurHash` | **GAP 2** | optional; absent ⇒ the wash alone |
| album-colour vinyl · WMP "from cover" | `CoverColorPlane.Watch(url)` + `Surfaces.SchemeFor` + `WaveePalette.Accent` | **GAP 1** (see `00-design-system.md` DATA GAPS) | never blocks: the fallback (`#1C4F9A` / `Tok.AccentDefault`) paints immediately and the bind repaints when the grading lands |
| cassette label lines · MD strip · Zune type · iPod screen · Winamp stitle · VU NOW line | `track.Title`, `track.Album.Name`, and the artist line — which is **not one function**: `DetailFormat.ArtistNames(track.Artists)` on the cassette label (`CassetteDeck.cs:313`), the MD strip (`CdDeck.cs:292`) and the Zune (`RecordDeck.cs:666`); `Artists[0].Name` alone on the VU NOW line (`VuDeck.cs:284`) and the Winamp stitle (`WinampDeck.cs:333`); and `Artists[0].Name` when there is exactly one artist, else `ArtistNames`, on the iPod (`IpodDeck.cs:287-292`) | `Track.TitleId`, `Track.ArtistLineId` (plan §4.12), `Track.Album → Album.Title` | show the line only when `Knows(Title \| Artists)` and, for the album half, `Album.Knows(Title)`; 0.2.9 renders `""` for an unknown, which reads as a blank label — **0.3 must instead keep the previous generation's text until the new one is known**, because these leaves only re-render on `CoverGen` |
| Winamp stitle duration | `FormatCache.DurationMmSs(track.DurationMs)` | `Track.DurationMs` + `Knows(Duration)` | as above |
| VU FORMAT cell · Winamp kbps/kHz cells | `bridge.StreamFormat`, `StreamBitrateKbps` | **GAP 4** | absent ⇒ `"---"` (Winamp) / an empty cell (VU): the code states the fact it has and never invents "44.1 kHz" |
| VU needles · Winamp/WMP bands | `bridge.Levels` (`VisualizerFrame.Rms/Peak`, written on the audio pump thread) | **GAP 3** | absent is a REAL answer: the synth falls back to a level-free envelope (`env = playing ? 0.8 : 0`) and the option row is labelled "level-driven" |
| grip enablement | `CanSeek`, `Error`, `ActiveDeviceId` | **GAP 7** | a drag is refused (and an in-flight one abandoned) when any of the three changes |
| which deck, and its options | `NpvPlayerPrefs.Style/ChoiceSlug` over `IAppSettings`, `Epoch`-subscribed | **GAP 10** | settings are loaded in `Platform.Boot` before any UI; a missing key clamps to choice 0 |
| the run gate | `ShellUi.RailOpen`, `Motion.ReducedMotion`, `UseIsActive` | **GAP 11** for `RailOpen` | — |

### DATA GAPS

| # | what this surface shows | 0.2.9 source | proposed 0.3 column / edge / signal |
|---|---|---|---|
| 1 | the cover's accent (album-colour vinyl, WMP colour) and its wash (every placeholder) | `SpotifyLive/CoverColorPlane.cs` (image-keyed, 2 feeds, sqlite-backed json), `Surfaces.SchemeFor`, `WaveePalette.Accent/Lift` | an image-keyed side table in `Entities/Store.cs` (`cover_color(scope_id, image, dark_argb5, light_argb5, fetched_at)`) + `Entities.Palette(StringId image)` returning a 5-role scheme and a `Signal<uint>` per image. **This is chapter 00's gap too — the deck is simply the surface that fails ugliest without it** (a vinyl that never takes the album's colour). |
| 2 | the blur-hash first paint under every cover slot | `track.Image?.BlurHash` (wire) | `Column<StringId> ImageHash` on `TrackTable`, in the `Image` group of `TrackFields` |
| 3 | the RMS/peak level tap the VU, Winamp and WMP decks react to | `IAudioLevelSource.Levels` → `FluentMediaAudioHost` → `PlaybackBridge.Levels` (`IReadSignal<VisualizerFrame>`, written on the pump thread) | `Playback.Levels` as an `IReadSignal<VisualizerFrame>?` published by `Playback.Audio.cs`, documented **Peek-only** (a subscription there would schedule UI work from the pump). Null must stay a legal, handled answer (Connect, remote devices, `--fake`). |
| 4 | the stream's codec name and bitrate (VU FORMAT cell, Winamp kbps/kHz) | `PlaybackBridge.StreamFormat` / `StreamBitrateKbps`, set from the audio session | `StringId StreamFormat` + `int StreamBitrateKbps` on `Playback.State`, published in `Publish()` |
| 5 | buffering vs loading vs failed, as three distinguishable poses (hover bob · cue · retreat-and-brake) | `IsBuffering`, `Error`, and `DeckClock.PhaseOf`'s derivation | extend `Playback.Phase` to `{ Idle, Loading, Buffering, Playing, Paused, Ended, Failed }` **or** keep 5 and add `bool Buffering` + `StringId ErrorId` on `State`. Without this the record deck loses the hover bob and the error retreat — two of its five best moments. |
| 6 | the current item's duration (every deck's progress) | `PlaybackBridge.DurationMs` (the SESSION's duration, which for a local/module/live item is not the track column) | `int DurationMs` on `Playback.State`, seeded from `Track.DurationMs` and corrected by the audio session |
| 7 | the two UI-side seek facts both grips write | `PlaybackBridge.SeekTargetMs` / `ScrubTargetMs` (`Signal<long?>`, added for 0.2.9's decks), `CanSeek`, `ActiveDeviceId`, `CommitSeek` | `Playback.ScrubTargetMs` / `Playback.SeekTargetMs` (`Signal<long?>` in `Playback.Host.cs`), `State.CanSeek`, `State.ForeignDeviceSlot` (already in the plan), and `Playback.Post(new(InputKind.Seek, …))` behind a `CommitSeek(ms)` helper that also publishes optimistically |
| 8 | repeat-one, for the "the arm re-cues the same disc" edge | `bridge.Repeat == RepeatMode.Track` | `State.Repeat` is in the plan (`byte`); pin the constant (`Repeat.Track = 2`) in `Playback.cs` CORE |
| 9 | which preset + its per-option choices (the whole visual identity of the surface) | `WaveeSettings.NpvPresentation` / `NpvPlayerStyle` + runtime-built `npv.player.<preset>.<option>` int keys, `NpvPlayerCatalog` (12 presets × 1-4 options × 2-5 choices), `NpvPlayerPrefs.Epoch` | a `Platform/Design.cs` CORE section holding the catalog table verbatim (ids and slugs are PERSISTED — never renumber, never rename) and the clamped accessors + one `Signal<int> Epoch`. Ch.21 owns the picker UI; the table itself must land somewhere both chapters can read. |
| 10 | the rail-open fact that stops the ticker | `ShellUi.RailOpen` | `Shell.RailOpen` (`Signal<bool>`) in `Shell.cs` |
| 11 | the deck's square edge | `ShellUi.RailWidth` → `round((w − 16)/4)·4`, frozen at mount, part of the Key | `Shell.RailWidth` (`Signal<float>`); keep the 4-DIP quantisation and keep it in the Key |

Nothing on this surface needs a fetch of its own: a deck demands **no** entity data beyond what the playing track
already carries, so the "pages demand their whole model on mount" rule costs it one line (`Entities.Ensure(current,
TrackFields.Identity)` is already done by whoever started playback).

---

## 8. Pure rules to port verbatim

All of these are engine-free today (`Deck/Model/**` has no `using FluentGpu.*` — that is why `Wavee.Tests.csproj:671`
can glob the whole folder) and all of them belong in the **CORE section of `Shell/Deck.cs`**.

| class | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `DeckEase` | `Model/DeckFrame.cs:64-132` | the four CSS cubic-béziers evaluated exactly (Newton on x, 6 steps, bisection fallback) — `Std(.4,0,.2,1)`, `LiftUp(.2,0,0,1)`, `Damped(.2,.6,.3,1)`, `SlideOut(.2,.7,.2,1)` | `Player/Deck/DeckEaseTests.cs` (141) — oracle comparison, monotonicity, NaN, flat-tangent convergence | `Deck.cs` CORE |
| `DeckBoundaryRules` | `Model/DeckInput.cs:63-96` | which of the six boundary edges just happened (same-uri first; a null on either side is never a synthesized skip) | `DeckBoundaryRulesTests.cs` (121) — 19 cases incl. window edges, empty/null album uri | `Deck.cs` CORE — **change the uri comparisons to slot comparisons** (see §7) and keep every other branch identical |
| `PositionInterpolator` | `Model/PositionInterpolator.cs` | the smooth playhead: anchor + extrapolate, a committed seek wins outright, clamp to duration | `PositionInterpolatorTests.cs` (112) — incl. the unanchored-extrapolates-uptime trap | `Deck.cs` CORE |
| `SpinIntegrator` | `Model/SpinIntegrator.cs` | first-order angular lag + integrated, wrapped angle; the rest-epsilon snap that lets a deck settle | `SpinIntegratorTests.cs` (129) — 95 % in 700 ms up / 1600 ms down, seam wrap, direction | `Deck.cs` CORE |
| `TonearmMachine` + `TonearmState` / `TonearmFrame` / `TonearmPhase` | `Model/TonearmMachine.cs:10-432` | **the entire record-family choreography**: 20 phases, the 10-rule priority order, 30 durations, `Seed`, `Sample`, both `NameOf` overloads | `TonearmMachineTests.cs` (**1,186 lines**, 33 ms stepper, every expectation derived from the constants) | `Deck.cs` CORE — port line for line; this is the single highest-risk file in the chapter |
| `TonearmGeometry` | `Model/TonearmMachine.cs:438-467` | deck point → groove fraction (rim = 0, label = 1) and arm-local → deck space about the (.5,.08) pivot | `TonearmGeometryTests.cs` (194) | `Deck.cs` CORE |
| `RecordModel` | `Model/RecordModel.cs` | platter τ table, the 33⇄45 pitch slew (both τ → 0.17 s for 600 ms), the mount-time "already at speed" seed, `IsSettled` | **no own test file** — covered indirectly through `TonearmMachineTests` + `SpinIntegratorTests`; add one in 0.3 | `Deck.cs` CORE |
| `TapeModel` (+ `TapeKind`) | `Model/TapeMachine.cs` | pack radii, ω ∝ 1/r, the 900 ms fast-wind window, the two-stage eject, `CoverGen` | `TapeMachineTests.cs` (93) | `Deck.cs` CORE |
| `DiscModel` | `Model/DiscMachine.cs` | CLV target, sled fraction, the tray eject | `DiscMachineTests.cs` (60) | `Deck.cs` CORE |
| `MeterModel` | `Model/MeterBallistics.cs` | dB→degrees, the two ballistic τ, the derived right channel, the no-tap breathing | `MeterBallisticsTests.cs` (74) | `Deck.cs` CORE |
| `LevelModel` | `Model/LevelSynth.cs` | band synthesis from rms/peak, attack/release, peak hold+fall, scope mode, the floor snap that lets it settle | `LevelSynthTests.cs` (83) — **it does not cover `IsSettled` in SCOPE mode, which is why §6.4(5) shipped**; add that case first | `Deck.cs` CORE |
| `ProgressModel` | `Model/ProgressModel.cs` | the album-only `CoverGen` bump and the phase name for the two motion-free decks | none (add one: the same-album advance must NOT bump) | `Deck.cs` CORE |
| `DriftPath` | `Model/DriftPath.cs` | the Ken Burns keyframe table and its two durations | none (add one: the mirror produces 5 evenly spaced, seam-free keys) | `Deck.cs` CORE |
| `DeckInput` / `DeckFrame` / `IDeckModel` / `DeckPhaseName` / `RecordVariant` / `PlaybackPhase` | `Model/DeckInput.cs`, `Model/DeckFrame.cs` | the two value types every model is a function of | exercised by all of the above | `Deck.cs` CORE |
| `DeckClock.Fold` / `PhaseOf` / `Seed` | `DeckClock.cs:145-198` | **currently NOT pure** (reads `PlaybackBridge` + `FrameTime`) although it is the one function that guarantees "mounted mid-song" and "ticked mid-song" see identical inputs | none | split in 0.3: a pure `Deck.Fold(in TransportFacts f, ref PositionInterpolator p, long nowMs, …) → DeckInput` in CORE plus a three-line shell that reads the signals — then the boundary/seek/synthetic-jump logic becomes testable |
| `NpvPlayerCatalog` / `NpvPlayerPrefs` | `Features/Player/NpvPlayerCatalog.cs`, `NpvPlayerPrefs.cs` | the 12 presets, their option rows, the persisted slugs and the clamps | `NpvPlayerCatalogTests.cs`, `NpvPlayerPrefsTests.cs` | shared with Ch.21; propose `Platform/Design.cs` CORE (GAP 9) |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The tonearm's priority order.** `Step` evaluates ten rules in a fixed order — record leaving, error, boundary,
   queue end, live drag, committed seek, buffering, pause/resume, play-from-rest, current-leg timer
   (`TonearmMachine.cs:152-267`). Re-ordering any two of them changes behaviour in ways no screenshot shows: a pause
   arriving in the same tick as a track boundary, a seek during a buffer stall, an error during a record change.
2. **`LiftStaged` and the eager fold.** The clock folds a boundary edge eagerly and then ticks, and both calls can land
   on the same `NowMs`. The machine is idempotent within a tick precisely because `LiftStaged` matches on the lift's
   DESTINATION, not merely on "a lift" (`:274-275`) — and `CoverGen`, the one thing a repeated fold could double-count,
   is guarded separately (`:199-202`). There is a dedicated test (`Step_IsIdempotentWithinATick`).
3. **The deferred brake.** `PlatterOffAtMs` is applied FIRST in `Step`, before every rule — including the rules that
   return `s` untouched (`:158`). A record change brakes 700 ms into the return swing, an error brakes WITH the swing,
   the auto-return brakes 500 ms after the lift. "Stop the platter when the swing ends" is a different, worse deck.
4. **Same-album recue never stops the platter.** `LiftThen(..., platterOffAfterSwingMs: null, ...)` (`:192-193`). The
   test is named for it. If the platter brakes on a next-track inside an album, the deck is wrong.
5. **The lift RESUMES from where the arm is.** `LiftFrom` (`:349`) means a lift interrupted halfway does not snap to 0
   first. Same for `armNow`/`liftNow` being sampled before every transition.
6. **Value-gated writes at a perceptual quantum.** Without `Q()` a 24-band analyser wakes the frame loop 48 times a
   tick and a slow platter writes a sub-pixel angle forever (`DeckClock.cs:236-266`). Keep the quanta, keep the single
   `Runtime.Batch` per tick, and keep the `if (v != s.Peek())` guard inside `Set`.
7. **`IsSettled` must be honest.** `SpinIntegrator` snaps ω to an exact 0 below 0.05 °/s and `LevelModel` snaps the last
   half-percent to the floor (`LevelSynth.cs:81-83`) for exactly this reason — the 0.2.9 live-run notes record that
   `LevelModel` originally never settled because an exponential release asymptotes.
8. **Faces are functions, not components.** Every `in NpvPlayerCatalog.Preset` parameter is read into plain locals
   BEFORE the first bind thunk exists (`RecordDeck.cs:49-54`, and the same comment in all nine faces), because an `in`
   parameter cannot be captured. Keep that discipline: it is what makes a face's thunks allocation-free.
9. **Reading the live track INSIDE a thunk.** `AlbumVinyl`, `CoverAccent`, `WmpDeck.CoverUrl`, every clock and every
   Winamp/VU cell resolve the track inside the `Prop.Of` body (`RecordDeck.cs:379-391`, `WmpDeck.cs:229-237`). Reading
   it in the face body would subscribe `DeckHost` and rebuild the entire face on every track change — the exact
   opposite of "the medium reacts, the deck persists".
10. **The three keyed-leaf strategies are different on purpose.** `SleeveArt`/`DiscArt`/`MdLabel`/`CassetteLabel` key on
    `CoverGen` (the mechanism's swap instant); `IpodScreen` re-renders on the TRACK (an iPod's screen changes with the
    song, not with the album); `CanvasArtwork` keys its inner canvas on `CoverGen` and its outer self on the two
    options. Collapsing these into one rule breaks either the cassette (re-letters mid-eject) or the iPod (stale title).
11. **One `DeckGesture` per host.** Two grips on one face must not fight over `ScrubTargetMs` (`DeckGesture.cs:19-21`).
12. **The 0×0 clock is its own component.** `UseInterval` auto-pauses on `UseIsActive`; hanging the interval off the
    host would make every enable/disable edge re-render the host and therefore REBUILD the face, whose bind thunks must
    be captured exactly once (`DeckClock.cs:18-21`).

### 9.2 Traps

- **Props freeze at mount.** `DeckHost.Preset` and `DeckHost.Side` are frozen factory fields; the only ways data reaches
  a mounted deck are the `DeckSignals` slab, `UseContext`, the prefs `Epoch` re-render, and a `Key` remount. `DeckClock`
  takes `Func<float> Rpm` and `Func<float> AngleQuantumDeg` **as delegates, not values**, precisely because a 33→45 flip
  and a side change must be visible to a ticker that was constructed once (`DeckHost.cs:101-109`).
- **The keyed COVER leaves key twice.** `SleeveArt` / `DiscArt` / `CassetteLabel` / `MdLabel` keep a STABLE component
  key (`"sleeve-art"`, …) and re-render on `CoverGen`; the remount that actually plays the 300 ms cross-fade is an
  INNER `BoxEl` whose `Key` is `gen.ToString()` and which carries `Animate = CoverFade` (`RecordDeck.cs:578-587`).
  Putting the generation in the component key instead would work but discards the component's own state every swap —
  keep the two-level shape. `CanvasArtwork` is the deliberate exception: its inner canvas IS keyed `"cv:" + gen`.
- **`Option()` has no "absent" answer** — see §6.4(7). Every face option read must be reachable for that preset, or
  guarded by the variant flag, because the fallback returns another option's slug rather than empty.
- **`Key` discipline.** `"deck:" + slug + "@" + (int)side` (host — set by `NowPlayingHeroTile` at
  `NowPlayingPanel.cs:547`, which OVERRIDES the plain `"deck:" + slug` that `NpvDeck.KeyFor` puts on the element),
  `"deck-clock"`, `"sleeve-art"` / `"label-art"` /
  `"picture-art"` / `"thump"` / `"zune-type:" + accent` (record), `"cassette-label:" + style`, `"md-label"`,
  `"disc-art"`, `"reel-counter"`, `"ipod-screen"`, `"ipod-wheel:" + body`, `"cv:" + fast + ":" + strong` and the inner
  `"cv:" + gen`. Every key that carries an option exists because that option is a FROZEN factory field on the component
  behind it — drop the key and the option stops applying; drop the option from the key and `ReuseGuard` will not catch
  it, because the component legitimately did not re-render.
- **`ReuseGuard`** will fire if a face is rebuilt with different child identities under the same key — which is exactly
  what a careless "re-render the face on track change" refactor produces.
- **Zero-allocation ticks vs per-face richness.** 0.2.9 reconciles them by putting ALL richness in the build (up to ~70
  draw ops on Winamp) and ALL change in bound channels (1-2 writes for the record family, ~39 for Winamp, 80 for WMP
  bars). The arrays a model owns (`LevelModel._bands/_peaks/_peakHoldMs`) are allocated once in the constructor
  (`LevelSynth.cs:36-38`); the thump's keyframes are `static readonly` (`RecordDeck.cs:601-602`); the Canvas drift's
  four tracks are rebuilt only when the frame size changes (`CanvasDeck.cs:191-200`); `DeckArt.AssetPath` memoizes its
  path strings; `FormatCache` gives one string per whole second. Reproduce all six or the "free at 30 Hz" claim dies.
- **`ImageEl` has no `Opacity`** — the CD rainbow rides inside a `BoxEl` that does (`CdDeck.cs:223-230`). **`ShadowSpec`
  is not bindable** — the arm's lift shadow is a transparent twin whose opacity is bound. **A node owns exactly one
  transform** — the iPod thumb's 45° rotation lives on an inner box because the outer one carries the bound translate
  (`IpodDeck.cs:169-181`); the Canvas drift's −9 % inset rides the canvas positioning wrapper for the same reason
  (`CanvasDeck.cs:158-165`). **`TextEl` has no horizontal alignment** — the cassette badge centres its glyph with flex
  (`CassetteDeck.cs:274-276`), and the VU tick labels are centred by an estimated advance (`VuDeck.cs:124`).
- **A `Canvas` child with no explicit height is stretched**; three places set `AlignSelf = FlexAlign.Start` to stop it
  (`IpodDeck.cs:134-135`, `VuDeck.cs:129,136`).
- **`Canvas.Create` clips**; the record's `Offset()` helper exists because a disc's shadow must escape its own box
  (`RecordDeck.cs:531-534`).

### 9.3 Where the plan is wrong or too thin for this surface

- **§2's budget is the main problem.** `Deck.cs 800 + Deck.UI.cs 1,600 = 2,400` against 5,394 lines of 0.2.9 deck code
  that is already dense (no dead code, no legacy paths — the models alone are 1,342 lines and are ported verbatim by
  §8). See the budget table below.
- **§2 gives owner K five files for Rail + Deck + Lyrics (1,800 + 800 + 1,600 + 1,800 + 2,000 + 1,300 = 9,300 lines)
  against a real 0.2.9 footprint of roughly 5,400 (deck) + 9,440 (lyrics, per Ch.22) + the rail.** Wave 4 owner K is
  over-committed by more than 2×. Split the deck out as its own owner, or move Lyrics to a sixth owner.
- **§4.12/§4.13 are about entity rows and detail pages and say nothing that applies here.** This surface has no
  `ItemsView`, no virtualization, no edge reads and no `Entities.Ensure` of its own. The one plan rule that DOES apply
  is §4.8's `Playback.Host.cs` signal set — and as §7 shows, five of the facts a deck needs are not in it.
- **The plan has no home for a user-preference catalog.** `NpvPlayerCatalog` (persisted ids and slugs, 12 presets,
  20 option rows, 44 choices) + `NpvPlayerPrefs` must land in a CORE file both this chapter and Ch.21 can read; §2's
  tree lists `Platform/Design.cs` and `Platform/Controls.cs` with no description, so `Design.cs` is the natural home.
- **The plan's `Playback.Phase` cannot express this surface.** Five values with no Buffering and no Failed erase the
  hover bob, the error retreat and the "Unavailable" pose (GAP 5). That is three of the twenty tonearm phases.
- **`assets/deck/**` is not in §2's asset list** (§2 says "fonts, loc/*.json, deck media (moved as-is)" — good, but
  make sure the six PNGs and the `Wavee.csproj` `assets/**` copy rule survive Wave 0's `git mv`). `wood-grain-1024.png`
  is 1 MB and a 512 version would do (0.2.9 follow-up (6)).
- **The `--fake` gate (§5 Wave 5) must include the deck.** `dotnet run -- --fake` shows the shell and pages, but a deck
  only mounts when something is PLAYING and the Details rail is open — add "play a fake track, open the rail, flip to
  Player, cycle all twelve presets" to the Wave 4/5 gate or the whole surface ships unlaunched.

**Line budget**

| | lines |
|---|--:|
| 0.2.9 actual (29 files) | **5,394** |
| plan §2 target (`Deck.cs` 800 + `Deck.UI.cs` 1,600) | 2,400 |
| honest estimate — CORE (`Shell/Deck.cs`): the 13 model classes ported verbatim (1,342) + a pure `Fold` (~120) + the catalog if it lands here (~140) | **1,450-1,600** |
| honest estimate — UI (`Shell/Deck.UI.cs`): host 112 + clock 267 + signals 54 + art 125 + gesture 102 + dispatch 108 + the record family 731 | **1,450-1,600** |
| honest estimate — UI (`Shell/Deck.Faces.cs`): the other eight faces (2,508 lines today) | **2,300-2,500** |
| honest total | **5,200-5,700** |

Comment density in these files is high (the record face is ~25 % prose) and some of it can go, but the geometry
literals, the 30-entry timing table and the nine faces' node trees cannot. Plan for **three** files, not two, and use
§5's own escape hatch ("a file that passes its budget by 30 % gets a named partial").

### 9.4 Files/pages missing from the §2 tree

- `Shell/Deck.Faces.cs` (above).
- A home for `NpvPlayerCatalog`/`NpvPlayerPrefs`/`NpvDiagnostics` (the `npv` log category: `presentation.set`,
  `style.set`, `option.set`, `flyout.close` — always-on logs, no env-var switches).
- Test files: §2/§6 promise "every core file has a test file of the same name"; this surface currently has **ten**
  (`src/apps/Wavee.Tests/Player/Deck/*.cs`, 2,193 lines) plus two prefs/catalog suites. They port nearly 1:1 and must
  be scheduled in Wave 4, not Wave 5 — `TonearmMachineTests` is the only thing that proves the choreography.

---

## 10. Parity checklist

Reference build: `C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
**Common setup for every item unless stated otherwise:** launch, play any fake track, open the right rail (Details arm)
at the default docked width so the deck is 324 DIP, and use the hero header's `Cover | ‹Player›` switch and the gear
flyout to pick the preset. "Frame recording" means a screen capture at ≥60 fps that can be stepped frame by frame;
"static capture" means a single screenshot of both builds at the same playhead position.

**Note on `--fake`:** there is no audio level tap in fake mode, so VU / Winamp / WMP run the level-free fallback
(`env = 0.8` while playing). `LevelModel`'s seed is a fresh `Random` per mount (`LevelSynth.cs:34`), so the analyser's
exact bar SHAPES will differ between two runs of the SAME build — compare envelope, attack/release feel, peak-cap hold
and fall, and the floor, never a frame-for-frame bar match.

**Geometry and identity**

1. Deck square is exactly the rail content width, quantised to 4 DIP; at rail 340 both builds measure 324 × 324 with
   `Radii.Card` (8) corners. *(static capture, measure)*
2. All twelve presets appear in the flyout and each mounts a visibly different face; picking one also flips the
   presentation to Player. *(click through all 12, static capture each — a contact sheet)*
3. Record @324: disc ⌀226.8 at (77.8, 51.8); sleeve 207.4 at (19.4, 38.9) tilted −2.5°; arm box at x 243; rest post at
   (273.8, 19.4). *(static capture, overlay the two screenshots)*
4. Record label is 34 % of the disc and carries the album art with a 2 px near-black ring. *(static)*
5. 7" option: the disc shrinks to 168.5 and moves to (107, 81), and the centre hole becomes a 33.7-DIP window onto the
   card behind the deck (NOT a grey circle). *(static, in both light and dark)*
6. Sleeve OFF moves the record left to x = 48.6 rather than leaving a gap. *(static)*
7. Turntable adds the wood plinth, the 249.5 felt mat with its two rings, and the strobe window with a glowing orange
   dot at (25.9, 278.6); its arm is chrome and its sleeve tilt is −4°. *(static)*
8. Zune: black ground, no arm, no sleeve, no rest; lowercase title at 35.6 DIP whose last third is the accent colour;
   the accent swatch option changes it. *(static ×4 accents)*
9. Picture disc: the cover fills the whole disc, there is no label ring, and the spindle is white. *(static)*
10. Cassette @324: shell 278.6 × 181.4 at (22.7, 68.0); window 172.7 × 61.7; hubs centred at 31 %/69 % of the shell.
    *(static)*
11. All four cassette shells and all three label styles differ as specified (clear is genuinely translucent and shows
    the ground through it; hand-written has three blue rules). *(static ×7)*
12. Reel @324: two 129.6 flanges at y 22.7, guides at x 84.2/239.8, a 116.6 × 58.3 head block, and the threaded tape
    drawn IN FRONT of the guides and heads. *(static)*
13. CD @324: disc 233.3 at (45.4, 38.9), rainbow visible at 75 % over the art, clamp hole 39.7 filled with the card
    colour, sled rail 109.6 long. *(static)*
14. MiniDisc: the cartridge is 200.9 × 212.7, the window shows a SLICE of a disc bigger than the slot, and the paper
    strip reads `TITLE · ARTIST` in uppercase. *(static)*
15. iPod @324: body 188 × 311 with 17.8 corners, LCD 157.9 × 133.7, wheel ⌀139.1; all three bodies and both LCD tints
    differ. *(static ×6)*
16. VU @324: two 139.3 × 114.2 meters at y 38.9, ten numeric ticks reading −20 −10 −7 −5 −3 −1 0 +1 +2 +3 with the
    positives in red, a 29.2 knob at (147.4, 194.4) and a 291.6 × 64.8 amber LCD. *(static ×3 faces)*
17. Winamp @324: two 298.1 × 125.7 windows at y 32.4 and 164.6, striped title bars, 19 bars in each window's analyser,
    and the 10-line grid in the EQ window. *(static ×3 skins)*
18. Winamp oscilloscope option replaces both analysers with 24 dots and renames both title bars. *(static)*
19. WMP: all three presets (24 mirrored bars + 32 wave dots · 3 rings × 3 trails · 6×6 grid), the 71.3 corner cover and
    the 2 px bottom line. *(static ×3)*
20. Canvas: the blurred over-saturated bleed fills the square, the crisp framed copy is 252.7 at (35.6, 35.6) with a
    large soft shadow, and the seek line is 252.7 × 2 at y 305.8. *(static ×2 bleed strengths)*

**Machines and motion**

21. Record, playing: the platter turns at 200 °/s at 33⅓ and 270 °/s at 45 (count one full rotation: 1.80 s vs 1.33 s).
    *(frame recording with the rpm option flipped)*
22. Record, press play from a cold Stopped deck: cue 300 → swing 1200 → lower 900, and the stylus lands ONCE.
    *(frame recording, count frames to each landmark)*
23. Record, from Idle with the record in its sleeve: slide-out 600 precedes the cue, and the record grows the last 4 %
    as it clears the sleeve. *(frame recording)*
24. Record, pause: the arm lifts over 450 ms **in place** (the angle does not change) and the platter takes ~1.6 s to
    stop; the arm's drop shadow appears as it rises. *(frame recording)*
25. Record, resume: spin-up 700 then lower 900, and exactly one dust puff fires. *(frame recording)*
26. Record, needle drop: the puff ring expands 0.2 → 1.6 and fades 0.9 → 0 over 500 ms. *(frame recording)*
27. Record, next track INSIDE an album: lift 300 (skip) → recue 900 → lower 900, and the platter **never stops**.
    *(frame recording — watch the label's rotation continuously)*
28. Record, next track on a NEW album: lift → return 1200 → sleeve-in 600 → the artwork changes at the top of the
    sleeve → cover swap 350 → slide-out 600 → cue; the platter brakes 700 ms into the return. *(frame recording)*
29. Record, seek via the player bar: lift 400 → a swing whose duration scales with the distance (full span ≈ 900 ms,
    quarter span ≈ 450 ms) → lower 700. *(frame recording ×2 distances)*
30. Record, tiny seek (< 0.15° of arm travel): nothing moves at all. *(frame recording)*
31. Record, seek while PAUSED: the arm swings across without lowering and stays up. *(frame recording)*
32. Record, queue end: a LINEAR run-out ride, then the arm sits at −3° for 1.8 s (33⅓) or 1.33 s (45), then a 500 ms
    lift and a 1.4 s return to the rest with the platter braking mid-swing. *(frame recording ×2 rpm)*
33. Record, buffering: the arm lifts 400 and BOBS between lift 1.0 and 1.4 with an 1800 ms period while the platter
    keeps turning; it lowers only when audio actually resumes. *(frame recording — throttle or pause the fake feed)*
34. Record, Cover → Player mid-song: the deck appears already tracking at the right angle, with no cueing sequence.
    *(frame recording of the switch)*
35. Cassette/Reel: the supply pack visibly shrinks and the take-up pack grows across a track, and the two hub speeds
    cross over at the halfway point. *(static captures at 0 %, 50 %, 100 %)*
36. Cassette/Reel, after a seek: a ~900 ms burst of fast winding (×4 cassette, ×5 reel) before settling. *(frame
    recording)*
37. Cassette, new album: the shell rises 129.6 DIP and fades over 500 ms, the label re-letters at the top, and it drops
    back in. *(frame recording)*
38. CD: the spindle is visibly faster at the start of a track than at the end (500 → 200 rpm) and the red sled walks
    from 46.7 to 107.3 DIP down its rail. *(frame recording + static at 0 %/100 %)*
39. CD/MD, new album: the disc slides DOWN 97.2 DIP and fades, swaps, and returns. *(frame recording)*
40. VU: with no level tap both needles breathe slowly around the same point, the right lagging/leading the left on a
    ~3 s beat, and both return to −45° within a second of pausing. *(frame recording)*
41. VU, PPM option: the needles are visibly faster to react than in VU mode (τ 0.05 vs 0.3 s). *(frame recording, same
    transport moment)*
42. Winamp/WMP: peak caps hold 400 ms then fall at 1.2/s; when playback pauses every bar relaxes to the floor and
    stops. *(frame recording)*
43. WMP Alchemy: the rings rotate with PROGRESS, not with time — scrubbing the bar rotates them, pausing freezes them.
    *(frame recording while scrubbing)*
44. Canvas: the frame drifts and zooms on a 40 s (slow) / 16 s (fast) loop with no seam; pausing playback FREEZES it
    where it stands rather than snapping home. *(frame recording, 45 s)*
45. Canvas, new album: the whole canvas cross-fades in over 300 ms and the drift re-seeds. *(frame recording)*

**Behaviour, gestures, states**

46. Record headshell: dragging it lifts the arm 300 ms, the arm follows the pointer through the groove band (rim = 0,
    label = 1), and releasing swings to the target and lowers; nothing is audible until release. *(frame recording)*
47. iPod wheel: one full clockwise turn advances the playhead by exactly a quarter of the track; crossing 9 o'clock
    does not jump backwards; a tap on the rim does NOT seek. *(frame recording + read the elapsed clock)*
48. iPod ⏮ ⏭ ⏯ and the centre button work; MENU does nothing. *(click each)*
49. Winamp's five keys map to Previous / Play / Pause / Pause / Next. *(click each, watch the player bar)*
50. Every clock on every deck (Zune, iPod ×2, Winamp, VU ×2, reel counter) advances once a second, matches the player
    bar to the second, and follows a live scrub. *(frame recording during a drag)*
51. Nothing below the hero moves when a deck mounts, a cover swaps, a clock rolls 9:59 → 10:00, or an option is
    flipped. *(frame recording of the whole rail during each event)*
52. Flipping an option (e.g. finish, skin, shell) restyles the mounted deck IN PLACE — the platter does not restart and
    the arm does not re-cue. *(frame recording while changing an option mid-track)*
53. Changing the PRESET remounts: the new deck fades in over 320 ms and starts from the current transport (already
    playing, not cueing). *(frame recording)*
54. Closing the rail, minimising the window, or turning on reduced motion stops the deck entirely; reopening resumes
    without a cueing sequence. *(frame recording; also check the FPS overlay reports no deck frames)*
55. Reduced motion: nothing in the record family rotates or bobs, every transition snaps (≤150 ms), the Canvas drift
    freezes, but the deck's 320 ms entrance and the 300 ms cover cross-fades still play. *(frame recording with the
    system setting on)*
56. A paused-and-settled deck costs zero frames (no ambient presents attributable to the deck). *(FPS overlay /
    `frame.*` log lines, both builds, 30 s paused)*
57. A missing deck texture falls back rather than painting a hole: temporarily rename `assets/deck/grooves-1024.png`
    and confirm the record still reads as a record (body + sheen + label + ring) rather than showing a grey plate.
    *(static capture; restore the file afterwards)*
58. Cover art that has not decoded shows the cover WASH (a tinted card colour), not a flat grey, in every cover slot.
    *(static capture on first mount of a new album)*
59. Right-clicking the deck opens the artwork menu (Cover / ‹Player› radio + "Player style…"), identically in both
    presentations. *(click, static capture — Ch.21 owns the menu's contents)*
60. Every deck renders correctly at rail 200 (side 184) and rail 500 (side 484): all POSITIONS scale, and the two
    builds agree on what does not — the absolute DIP list, every corner radius, every `ShadowSpec` and the type
    FLOORS (W22 a-e). At 184 the iPod's three metadata lines are 7-DIP type in a 48-DIP column and the Winamp
    chrome sits at its 6-DIP floor: both builds must clip in exactly the same places. *(static capture at both
    widths, all twelve presets)*

**Edges, options and the known defects**

61. Mount poses (W27): with the deck on Cover, pause mid-song, then flip to Player — the record appears LIFTED at
    the right angle with the platter stopped, not cueing. Repeat while buffering (hovering), after the queue has
    ended (arm on the rest, record still out), and with an error (arm on the rest, platter off). *(static ×4)*
62. Mount asymmetry: flipping Cover→Player mid-song on a CASSETTE starts both hubs from rest and they take ~0.5 s
    to reach speed, while the record is already at speed. Both builds do this. *(frame recording ×2 presets)*
63. VU › needles and Winamp › analyser flipped on a MOUNTED deck: 0.2.9 restyles the face but keeps the old model
    (§6.4(4)) — 0.3 must apply them immediately. This is the one parity item where 0.3 is expected to DIFFER; note
    the 0.2.9 behaviour in the capture so the difference is deliberate, not a regression. *(frame recording)*
64. Winamp with the oscilloscope, paused, rail open: 0.2.9 keeps presenting at 30 Hz (§6.4(5)). 0.3 must settle.
    *(FPS overlay / `frame.*` lines, 30 s paused, both builds)*
65. A track with no known duration (or duration 0): every deck parks at its zero pose, both clocks read 0:00 /
    -0:00, the reel counter reads 0000, and NEITHER grip commits a seek (0.2.9's headshell does — §6.4(6) — so the
    expected 0.3 behaviour is "nothing happens"). *(click-drag each grip)*
66. Long metadata: a 90-character title on the Zune CLIPS mid-glyph (no ellipsis), on the cassette label and the
    MD strip it ellipsizes, and the Winamp stitle scrolls at 22 DIP/s with a 24-DIP gap and never stops. A track
    with six artists shows the joined list on the cassette/MD/Zune/iPod but only the FIRST artist on the VU strip
    and the Winamp title (§3.11). *(static ×6 + one frame recording of the marquee)*
67. A missing texture never paints a hole, for all six PNGs — not only `grooves` (item 57): rename each of
    `wood-grain`, `marble`, `cd-rainbow`, `reel-flange`, `winamp-tbar` in turn and confirm the fallback UNDER it
    (plinth stain, brown pack, plain plate, `White @.22` rim ring, `wa1` title fill). *(static ×6; restore after)*
68. The command palette's "next player style" cycles all twelve presets in catalog order and wraps, and each hop
    is a 320 ms fade-in of a deck that is already playing. *(frame recording, 12 hops)*


---

## 11. Audit log

Adversarial pass against the 29 files under `src/apps/Wavee/Features/Player/Deck/**` plus `NpvPlayerCatalog`,
`NpvPlayerPrefs`, `NowPlayingPanel` (hero slot), `PlaybackBridge` (seek API), `ShellResponsiveLayout`,
`AmbientPowerPolicy`, `WaveeCommands`, `assets/loc/*.json` and the ten test files. Every number in §2/§3/§5 was
re-derived from the source; the ones not listed below were confirmed correct (including all 29 tonearm constants,
the 5,394-line inventory, all ten test line counts, both rail breakpoints, the three angle quanta, the cassette/reel
pack-radius algebra and every face palette).

**wrong**

1. §2 W5 title — "the full 3.45 s choreography" was not a duration in the code. The new-album change runs
   450+1200+600+350+600+300+1200+900 = **5600 ms** to Tracking (5450 ms after a user skip). Title and a TOTALS line
   corrected; the existing "cold start 3000 ms" is only the tail from `SlideOut`.
2. §3.8 Winamp analyser — `gap` and `pad` were swapped in BOTH rows. `Bars(…, gap, pad)` is called `(1f, 2f)` for the
   main window and `(2f, 3f)` for the EQ (`WinampDeck.cs:99,155`), so the bars are **4.1 DIP** and **11.5 DIP**, not
   3.2 and 10.6. Fixed in §3.8 and in W19.
3. §1.1 `DeckModels` tree — "Picture-as-progress → ProgressModel()" is wrong: `Picture` maps to
   `RecordModel(in seed, RecordVariant.Picture)` (`DeckModels.cs:35`). Only Ipod and Canvas fall to `ProgressModel`.
4. §6.3 — "the six `Focusable` buttons (iPod centre + ⏮ ⏭ ⏯, Winamp ×5)" is nine, and §6.1 already said nine.
5. §6.3 / §7 GAP 9 — "`player.choice.*` (44 keys)" is **45** (counted in all three locale files); "20 option rows"
   is 20 distinct option KEYS behind 28 option ROWS (the `Finish`/`Size`/`Rpm`/`Sleeve` instances are shared).
6. §0(2) — "30 named durations" overstated the const block: 29 names, of which 28 are durations and one is
   `BufferLeadInFrac`; `Rpm45Threshold` and `SeekDeadZoneDeg` live just below it (`:112-115`).
7. §7 — the artist line is not one function. `DetailFormat.ArtistNames` on the cassette/MD/Zune, `Artists[0].Name`
   alone on the VU strip and the Winamp title, and a count-dependent mix on the iPod. Row rewritten; §3.11 tabulates it.
8. §2 W24 — the "no cover url" wash was written as `Lift(Accent)`; with no url `SchemeFor` returns null and the term
   is `Tok.AccentDefault` (`NowPlayingPanel.cs:203-210`). Corrected, with the graded case spelled out beside it.
9. §5.3 — two `MeterBallistics` line references were off by one (`:13,48`→`:12,48`, `:12,47`→`:11,47`).

**overclaim**

10. §2 W22 — "Exactly seven literals are NOT fractions of side" is a large undercount. EVERY corner radius, every one
    of the 14 `ShadowSpec`s, the Marquee's 22 DIP/s + 24-DIP gap, the Winamp chip/cell padding, the WMP bar inset
    (`bw − 4`), the WMP wave dot, the Winamp scope dot, the Zune bar height and the record stylus are absolute DIP —
    and, worse, the iPod/VU/Winamp/WMP type is `MathF.Max(floor, k·side)`, so at side 184 fourteen type floors BIND
    and the text stops scaling. Replaced with the five-group list (a-e) and a new parity item 60/66.
11. §0(10) — "`CoverGen` bumps mid-sleeve" reads as "halfway through the slide". It bumps on the
    `SleeveIn → CoverSwap` edge, i.e. with the record fully hidden (`TonearmMachine.cs:314-315`). Clarified.

**missing**

12. **§6.4(4) — two options never reach a mounted model.** `DeckHost` builds the model once (`:70`) and the host Key
    carries only preset+side, so VU › needles (`ballistics`) and Winamp › analyser (`vis`) restyle the face while the
    physics keep the OLD option until a remount — the oscilloscope in particular is then fed spectrum values. Added
    as a defect, as an "option lifetimes" table in §1.2, as a warning in the §1.1 tree, and as parity item 63.
13. **§6.4(5) — a Winamp oscilloscope deck never settles.** The scope trace rests at 0.5, `IsSettled` tests against
    the 0.02 floor, so `IsSettled` is permanently false and the 30 Hz ticker runs while the rail is open. This makes
    the chapter's own parity item 56 false for that combination in 0.2.9. Added to §5.3, §6.4, §8 (the missing test)
    and parity item 64.
14. **§6.4(6)/(7)** — the record grip commits a seek to 0 when `DurationMs` is 0 (the wheel refuses), and
    `NpvPlayerCatalog.Option()` silently falls back to `Options[0]` rather than to "absent". Both are re-author traps.
15. **W27 (new) — the six mount poses** of `TonearmMachine.Seed` (Idle / Unavailable / Tracking-at-speed / Hover /
    Stopped / Paused), plus the fact that no OTHER model has a seed, which is why a cassette's hubs spin up on a
    Cover→Player flip while the record is already turning.
16. **W28 (new) — degenerate inputs**: no bridge (a bare unclipped square, `DeckHost.cs:66`), no `ShellUi` (the 324
    fallback and `RailOpen == true`), no track (the slot unmounts at `NowPlayingPanel.cs:528`), duration 0, no level
    tap, and the per-texture fallback for all six PNGs.
17. **§3.11 (new) — text slots**: case, width, and the fact that overflow is NOT uniform (three `Clip`, six
    `CharacterEllipsis`, one permanent `Marquee`, plus the reel counter's `mod 10000` wrap). The Zune title clipping
    mid-glyph and the sub-3-character title losing its accent run were both unrecorded.
18. **§5.2 addendum — the other decks' phase names**, including `Winding` (`TapeMachine.cs:100`), which no other
    model publishes and which the duration table did not mention, and `ProgressModel`'s seven-name priority order.
19. **§1.1 — `DeckClock`'s four wake subscriptions** (`SeekTargetMs`, `ScrubTargetMs`, `Repeat`, prefs `Epoch`,
    `:121-124`) and its `ShellUi` read. Without them a paused-and-settled deck would not notice a committed seek, a
    drag beginning, or a 33→45 flip — the ticker is OFF in that state.
20. **W3 addendum — the two silent machine guards**: a committed seek is honoured only from `Tracking`/`Paused`
    (`:230`), and a drag needs `RecordOut` (`:214`).
21. **§3.9/§3.10 — WMP's band→node map** (wave dots reuse peaks 0-7, alchemy reads bands 0,1,2,7,8,9,14,15,16,
    battery reads `(i·3+j) % 24`), the real asset byte sizes, and the fact that `DeckArt.CoverWash`/`CoverAccent` are
    **unused** by all nine faces.
22. **§9.2 — the two-level cover key** (stable component key + inner `Key = gen` carrying `Animate`), and the fact
    that `NowPlayingHeroTile` OVERRIDES `NpvDeck.KeyFor`'s key with the `@side` variant.
23. **§2 W23 — reduced motion at MOUNT**: the seed skips the at-speed platter (`RecordModel.cs:43`).
24. **Parity items 61-68** for the above (mount poses, mount asymmetry, the two model options, the scope ticker,
    duration 0, long metadata, all six textures, the palette's next-style command).

**unverified**

25. `UseInterval` ANDing `UseIsActive` (§0(6), §9.1(12)) is asserted from `DeckClock`'s own doc comment; the engine
    hook was not read in this pass. Treat as engine behaviour to re-confirm in `..\fluent-gpu` before relying on it.
26. §6.4(1) still stands: whether the engine synthesises a release on capture loss (the record grip wires no
    `OnDragCanceled`) was not verified here either. The one-line fix is unchanged.
27. Line references into `NowPlayingPanel.cs` were 2-4 lines off throughout (`NowPlayingHeroTile` is `:506-566`,
    the empty-track return `:528`, the side computation `:541-542`); all four corrected. Ch.21 owns that file, so
    they will drift again — prefer the symbol names over the numbers.
