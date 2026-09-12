# Lyrics (view, immersive surface, ink, blur, clock, inspector) - 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Player/LyricsView.cs` (3,160) · `ImmersiveLyricsSurface.cs` (550) ·
> `LyricsInspectorDialog.cs` (564) · `LyricsMediaClock.cs` (213) · `LyricsInk.cs` (91) · `LyricsBlurPolicy.cs` (37) ·
> `NpvLyricsPeek.cs` (177) · `App/LyricsRowShape.cs` (62) · `App/LyricsSyncGate.cs` (26) · `Features/Player/FrameTime.cs` (27) ·
> `Backend/Lyrics/LyricsPeekClock.cs` (45) · lyrics slices of `Features/Player/StagePanes.cs:35-141` and
> `Features/Player/RightRail.cs:373-401` · plus `Backend/Lyrics/**` (19 files, **4,504 lines** — counted) which owner K
> also inherits. **Total ≈ 9,600 lines** (5,090 UI + 4,504 backend). | 0.3 target: `Shell/Lyrics.cs` (3,700),
> `Shell/Lyrics.UI.cs` (3,500), `Shell/Lyrics.Host.cs` (2,300) — **three** partials, ≈9,500 lines, not the plan's
> three at 5,100 (§9) — plus ≈600 for the inspector in `Screens/Diagnostics.UI.cs`. **`Shell/Lyrics.Stage.UI.cs` is
> dropped (arbitration 2026-09-12, A12):** the immersive stage is `Shell/Stage.cs` + `Shell/Stage.UI.cs`, chapter 21's,
> owner K — and the lyrics pane *inside* the stage lives in `Lyrics.UI.cs` |
> Wave 4, owner K, single — the plan keeps `Lyrics.cs`, `Lyrics.UI.cs` and `Lyrics.Host.cs` all under K; this
> chapter's earlier §9 ask to split the CORE + Host half to a second owner is not what the plan settled
>
> Cross-references (do not re-specify): tokens / type ramp / cover palette / motion curves → `00-design-system.md`;
> the rail frame, header chrome, NPV panel and queue pane → `21-right-rail-npv-queue-stage.md`; the shell overlay lane,
> window bands and page transitions → `18-shell-frame.md`; video placement and the video surfaces → `24-video-surfaces.md`;
> Settings rows that write the three lyrics keys → `27-settings-and-diagnostics.md`.
>
> Doc drift (code wins): `wavee-lyrics-canonical-design.md` §4.3 still specifies active 24 px Bold / neighbour ×0.75 scale /
> 30 % unplayed opacity / focal band 50 % / a 250 ms position timer / "no per-line blur in v1" — every one of those numbers
> was replaced (26–36 DIP @700, flat 0.98 scale, a two-branch opacity ladder, band 0.40/0.38, a per-frame `FrameClock.Tick`
> stepper, a per-line DoF σ ladder). `betterlyrics-parity-plan.md` / `wavee-betterlyrics-full-parity-canonical.md` keep the
> syllable "scale-pop" and the 3-D fan; both are refuted and deleted (see the banner at that doc's head). The
> `wavee-lyrics-timing-rework.md` §2.1 engine-side scroll spring was superseded by the latch + per-line cascade (§5 below).
> `LyricsView.cs:497,885` name a test `StageLayoutTests.TheLyricsReadingSurface_PaintsNoThemeInk` that no longer exists
> (source-text tests are banned by CLAUDE.md) — the ink discipline is now convention only.

---

## 0. The non-negotiables

1. **One document, two surfaces, one ink seam.** The same `LyricsView` renders the 340-DIP rail panel and the fullscreen
   stage; the ONLY differences are `large` (type/gutter/band/σ-ladder) and `onMedia` (which ink ladder). No second view,
   no theme branch inside the view (`LyricsView.cs:299-323`, `LyricsInk.cs:23-91`).
2. **The active line is the only crisp, full-brightness line.** Distance from it is carried by OPACITY + a per-line
   Gaussian self-blur, never by scale: every inactive row is a flat 0.98 (`LyricsView.cs:2689`), anchored at the LEFT
   margin (`TransformOriginX = 0f`, `:2866`), so the left edge of the column never breathes.
3. **Past ≠ future at the same distance.** A sung line settles dimmer (0.19 / 0.13 / 0.10) than an upcoming one
   (0.45 / 0.24 / 0.14 / 0.11 / 0.10) — `LyricLineView.OpacityOf` (`:2936-2943`). A single ramp is a regression.
4. **Blur is a measured ladder, per surface.** Rail σ = 0 / 1.25 / 2.5 / 4 / 5.5 / 6.5; stage σ = 0 / 1.2 / 2.0 / 2.6 / 3.0
   (`LyricsFx`, `:38-57`). A σ INCREASE snaps, a DECREASE eases with τ = 65 ms (`:1611`, `:1656-1665`). No active line ⇒
   σ 0 everywhere, never the ladder's maximum (`:1592`).
5. **The karaoke fill is a narrow, soft edge on a two-colour glyph run** — sung `Primary`, unsung `Primary @ 0.58`,
   feather 5 DIP (rail) / 7 DIP (stage) expressed as a per-line FRACTION of the measured reading-order length, clamped
   0.01–0.10, with a +2 % lead and pixel-quantised boundaries (`:2957-2973`, `:2201-2243`).
6. **The eye leads the voice by 140 ms, the fill does not.** Emphasis + scroll resolve at `now + LeadMs`; the wipe and
   the halo resolve at true `now` (`:106`, `:2105-2111`). Two indices — `_activeLine` and `_voiceLine` — and neither may
   drive the other.
7. **A line handoff is N springs on the LINES, not one spring on the viewport.** The viewport LATCHES instantly and each
   line carries a compensating translate that decays to 0: stagger 60 ms per rank (cap rank 4), every rank converging at
   0.48 s, ζ = 1, zero overshoot by construction (`:1713-1789`, `:1796-1837`).
8. **Motion samples the frame clock.** `FrameTime.NowQpc` = `FrameClock.PresentQpc` (`FrameTime.cs:22`); the media
   position comes from `LyricsMediaClock` mapping `(position, sampleQpc)` onto that clock, with a 250 ms snap threshold
   and ≤5 % slew (`LyricsMediaClock.cs:40-50`). `Environment.TickCount64` anywhere in this surface is the karaoke-stepping
   bug returning.
9. **A ≥5 s instrumental break retires the finished line and raises three breathing dots.** Focus advances to the next
   line (word-synced documents only), the anchor row buys `dot + 2·air` of extra top pad, and the dots fill like a
   karaoke gesture across the gap, ending one second before the next line (`:586-621`, `:739-753`, `:760-815`).
10. **The user can always take the scroll.** A wheel/touch drag detaches the follow, blurs go to 0, a "Resync" pill with
    a 4 s countdown ring appears, and idle re-attaches automatically (`:513-549`, `:1929-1962`).
11. **The stage is one backdrop + TWO paint layers.** σ80 baked-blur cover, drifting on two incommensurate sinusoids,
    under one continuous vertical scrim (0.76 → 0.46 plateau → 0.70) and one left-anchored column shade that feathers to
    exactly 0 over 260 DIP. No boxed veils anywhere (`ImmersiveLyricsSurface.cs:62-82,436-497`, `StageLayout.cs:217-239`).
12. **Every state is a real state, not a blank.** loading shimmer (bars at the exact side pad the first lines will take),
    "No lyrics available", "Nothing playing", unsynced-as-reading-block at smaller type, and the video note
    (`:1439-1469`, `:2522-2527`, `:1383-1435`, `:480-511`).
13. **Held notes bloom; short syllables do not.** A ≥700 ms syllable swells a blurred duplicate glyph run under the
    crisp one, peak ×0.75, melting into the note's end; a whole-line wash is not the effect (`:127-135`, `:2341-2357`).
14. **Zero re-render per frame.** The per-frame lane writes scene columns and a handful of value-gated signals; the row
    components re-render only when THEIR OWN packed emphasis changes (`:163`, `:1187-1194`, `:2663`).
15. **In 0.3, NOTHING in this surface is env-var-gated — not behaviour, not verification.** 0.2.9 carries three env
    seams and all three are RESOLVED in §9 (not left open): `WAVEE_LYRICS_DEBUG` (`LyricsView.cs:313-317`) is
    **deleted as an env var, not as a surface** — decision, Christos, 2026-09-12: the debug pill + overlay are KEPT,
    contrary to this chapter's own (a); they are re-homed on the SAME persisted `diag.developerMode` flag the
    inspector already reads (`RightRail.cs:382`) instead of the env var, so the surface still shows only under
    Developer mode but is never gated by an environment variable;
    `FG_STAGE_RECTS` (`ImmersiveLyricsSurface.cs:247-252`) becomes a Developer-mode `Signal<bool>` on exactly the
    `DeveloperMode.FpsOverlay` pattern (`App/DeveloperMode.cs:29-31,41-46`); `WAVEE_LYRICS_ADVANCE_PROBE`
    (`LyricsView.cs:71-96`, `WaveeApp.cs:34-40`) becomes the `--lyrics-advance-probe` CLI entry beside `--perf-bench`
    / `--startup-bench` / `--crash-probe` (`Program.cs:197-207`). The always-on `lyrics.clock` log line stays
    always-on and ungated (`:2081-2099`). The blur STRENGTH is a real setting with an Auto tier
    (`LyricsBlurPolicy.cs:20-36`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
RightRail (RailMode.Lyrics)                       Features/Player/RightRail.cs:114-141
├─ header  BoxEl h=44                             RightRail.cs:121-128        44 = WaveeSize.NavItemH
│   └─ LyricsHeaderKids(ui, settings)             RightRail.cs:373-401        title · [globe] · [code] · expand · close
│       ├─ TitleText(RailMode.Lyrics)             RightRail.cs:405-409        WaveeType.RailHeader = Ui.Subtitle 20/28/600
│       ├─ secondaryToggle (Icons.Globe)          RightRail.cs:395-397        only when LyricsPrefs.Available != 0
│       ├─ LyricsInspectorButton                  LyricsInspectorDialog.cs:38-52   only when DeveloperMode.Enabled
│       ├─ expand (Icons.FullScreen)              RightRail.cs:383-384        → ShellUi.ImmersiveLyrics = true
│       └─ close (Icons.ChromeClose)              RightRail.cs:449-457
└─ body = LyricsView(large:false, onMedia:false,  RightRail.cs:135-136
          visible: RailOpen && !ImmersiveLyrics)

WaveeShell                                        Features/Shell/WaveeShell.cs:1440-1455
└─ Flow.Show(ImmersiveLyrics) → ImmersiveLyricsSurface           ImmersiveLyricsSurface.cs:46
    ├─ caption band  h=48 pass-through             ImmersiveLyricsSurface.cs:208     TitleBar.ExpandedHeight
    ├─ body  ZStack, Fill = StageInk.Floor         ImmersiveLyricsSurface.cs:209-231
    │   ├─ Backdrop(...)                           ImmersiveLyricsSurface.cs:420-498
    │   │   ├─ overscale frame (paint ×1.30)       :441-467   declared Transform, viewport-bound
    │   │   │   └─ drift carrier (_driftNode)      :470-476   LocalTransform written by DriftTick
    │   │   │       └─ Ui.Image(cover) BakedBlur σ80 :422-429
    │   │   ├─ Scrim()   full-bleed gradient       :484       StageChrome.cs:85-89
    │   │   └─ ColumnShade() w=612 (wide only)     :486-495   StageChrome.cs:95-100
    │   ├─ Shield (childless hit absorber)         :283-292
    │   └─ content column
    │       ├─ TopBar  h=88 wide / 56 compact      :355-382   [spacer][globe ScrimFab 40][ExitFab 44]
    │       └─ StageBody(stage)                    :303-348
    │           ├─ identity wrapper w=352          :328-338   → StageIdentity   (chapter 21)
    │           └─ panes wrapper Grow=1            :339-345   → StagePanes
    │               ├─ pane:lyrics  (opacity x-fade)         StagePanes.cs:57-64
    │               │   └─ LyricsColumn: pad(0,0,48,72), MaxWidth 700   StagePanes.cs:96-123
    │               │       └─ LyricsView(large:true, onMedia:true,
    │               │            visible: ImmersiveLyrics && pane==Lyrics)  StagePanes.cs:118-119
    │               ├─ pane:queue   (chapter 21)             StagePanes.cs:65-74
    │               └─ Pivot  h=72  "Lyrics · Queue"         StagePanes.cs:127-140
    └─ player-bar band  h=72 pass-through          ImmersiveLyricsSurface.cs:232

LyricsView : Component                             LyricsView.cs:64-2528
├─ Render()                                        :325-465
│   ├─ reads LyricsPrefs.Epoch → _secondary        :344-346    republished signal, rows subscribe
│   ├─ reads LyricsBlurStrength → _dofScale + _dofScaleSignal  :350-373
│   ├─ subscribes IUpgradingLyricsProvider.LyricsUpgraded      :410-426
│   ├─ body  = LyricsDocHost  Key "lyrics-doc:"+trackId        :440-441
│   │   └─ Skel.Region(docLoadable, LyricsShimmer, content, FadeOnly)   :867-876
│   │       ├─ shimmer  LyricsShimmer(large, ink)              :1439-1469
│   │       ├─ empty/failed → Message("No lyrics available")   :870, :872, :874 · :2522-2527
│   │       └─ content  → LyricsContent(doc)                   :1323-1381
│   │            ├─ timed   → Virtual.Custom(n, LyricsMeasuredLayout, i => LyricLineView) :1345-1380
│   │            └─ untimed/suppressed → UnsyncedLyricsContent :1383-1435
│   ├─ ticker = LyricsTicker  Key "lyrics-ticker:"+trackId     :445-447, :3081-3138
│   │   └─ LyricsFrameStepper (mounted only while in motion)   :3146-3160
│   ├─ dots  = InterludeDots() overlay                          :643-713
│   ├─ resync= ResyncOverlay() overlay                          :513-549
│   ├─ unsync= UnsyncedBanner(b) overlay                        :480-511
│   └─ debug = DebugButton()/DebugOverlay() (now gated by diag.developerMode) :886-959   KEPT in 0.3, gate changed — §9 (a)
├─ OnFrame(...)  THE per-frame driver               :2005-2277
│   clock → active/voice → interlude → follow → dots → emphasis → σ ramp → cascade → glow → wipe
└─ LyricLineView : Component                        :2588-3006
    └─ row BoxEl (springs: Opacity, ScaleX, ScaleY) :2850-2874
        └─ dofContent BoxEl (Blur σ, cascade dy)     :2831-2848
            ├─ textEl ZStack [glow, main]            :2726-2815
            │   ├─ glow  BoxEl Opacity=bound fade → LineText(bloom) + GlyphWipe   :2748-2768 / :2780-2807
            │   └─ main  LineText(sung) + GlyphWipe(Before,After,Split,Softness,Lift) :2742-2747
            └─ SecondaryText (translation / romanization) :2899-2911

NpvLyricsPeek : Component (the rail's Details body)  NpvLyricsPeek.cs:17-177
└─ spine 3×112 + two 56-DIP slots (active, peek@0.38)  :76-103, :147-176

LyricsInspectorButton / LyricsInspector / LyricsInspectorBody  LyricsInspectorDialog.cs:38-564
└─ ContentDialog 548 wide → SelectorBar[Providers|Raw response|Parsed] + per-tab body
```

Pure/decision classes feeding the above: `LyricsFx` (`LyricsView.cs:32-58`), `LyricsBlurPolicy`, `LyricsSyncGate`,
`LyricsRowShape`, `LyricsMediaClock`, `LyricsPeekClock`, `LyricsPrefs` (`LyricsView.cs:3020-3072`),
`LyricsMeasuredLayout` (`LyricsView.cs:2536-2586`), `StageLayout` (chapter 21 owns the stage allocator).

### 1.2 The same tree in 0.3 terms

| Node | 0.3 home | Kind | Inputs (how data reaches it) |
|---|---|---|---|
| `Lyrics.Fx.Sigma(dist, large)` | `Shell/Lyrics.cs` | static (CORE) | plain ints — port verbatim |
| `Lyrics.BlurPolicy.Resolve/Scale/Enabled` | `Shell/Lyrics.cs` | static (CORE) | setting int + `GpuProfile.IsWeak` |
| `Lyrics.SyncGate.Suppressed(videoActive)` | `Shell/Lyrics.cs` | static (CORE) | bool from `Playback.VideoActive` |
| `Lyrics.RowShape.SameRows(a,b)` | `Shell/Lyrics.cs` | static (CORE) | two `LyricsDoc` |
| `Lyrics.MediaClock` | `Shell/Lyrics.cs` | class (CORE) | `(positionMs, sampleQpc, playing)`; queried at `FrameTime.NowQpc` |
| `Lyrics.PeekClock.ActiveAndPeek` | `Shell/Lyrics.cs` | static (CORE) | doc + nowMs |
| `Lyrics.Emphasis.Pack/Opacity/ResolveLine/AdvancePastInterlude/SungOut/Split/HeldGlow` | `Shell/Lyrics.cs` | static (CORE) | ints/longs only |
| `Lyrics.Cascade.Arm/Step` (the ζ=1 closed form + stagger + band) | `Shell/Lyrics.cs` | static (CORE) over caller-owned float spans | dt, delta, rank |
| `Lyrics.MeasuredLayout` | `Shell/Lyrics.UI.cs` | class implementing the engine's measured seam | estimate + band |
| `Lyrics.Ink` (mode struct) | `Shell/Lyrics.UI.cs` | readonly record struct | `OnMedia` bool; every rung resolves its token at consumption |
| `Lyrics.View` | `Shell/Lyrics.UI.cs` | `sealed class : Component` | ctor `(bool large, bool onMedia, Func<bool>? visible)` — **frozen at mount, and that is correct**: both call sites mount one instance per surface for the app's life |
| `Lyrics.Line` (row) | `Shell/Lyrics.UI.cs` | `sealed class : Component` | **frozen at mount**: index, the `LyricLine`, metrics, `large`, `ink`, the four report callbacks, the two `Func<int,float>` seams. **Live values only through signals**: `Signal<int> emphasis` (per line), `FloatSignal glowAlpha` (per line), `FloatSignal nowMs`, `Signal<int> secondary` (shared), `FloatSignal haloScale`, `Signal<LyricsFollowMode> followMode` (Peek only) |
| `Lyrics.Frame(...)` (the `OnFrame` driver) | `Shell/Lyrics.UI.cs` | method on the view | `SceneStore` + peeked playback signals; writes scene columns, never re-renders |
| `Lyrics.Ticker` / `Lyrics.Stepper` | `Shell/Lyrics.UI.cs` | two 0×0 components | `FrameClock.Tick` context signal; mounted only while `playing ‖ cascading ‖ detached` |
| `Lyrics.Stage` (`ImmersiveLyricsSurface`) | `Shell/Lyrics.UI.cs` | `sealed class : Component` | `Viewport.Size` signal, `StageLayout` signal (resolved in an effect), `AppearancePrefs.Epoch`, `LyricsPrefs.Available/Epoch` |
| `Lyrics.Peek` (NPV reel) | `Shell/Lyrics.UI.cs` (mounted by `Rail.UI.cs`) | `sealed class : Component` | **Key remount per track** (`"npv-lyrics:" + track.Id`) |
| `Lyrics.Prefs` (Epoch/Available/Clamp/Next/BitFor/Tooltip/Set) | `Shell/Lyrics.cs` | static + two `Signal<int>` | the one writer both headers and Settings call |
| `Lyrics.Fetch` (aggregator, sources, rerank, disk cache, upgrades) | `Shell/Lyrics.Host.cs` | SHELL | track identity → `LyricsDoc`; publishes upgrades |
| `Lyrics.Diagnostics` (the per-track store) | `Shell/Lyrics.Host.cs` | SHELL | bounded static, written by the fetch fan-out (§7 gap 7) |
| `Diagnostics.LyricsInspector` (button target + dialog, ≈600) | `Screens/Diagnostics.UI.cs` — Wave 6 owner S, **decided**, §9 (d) | UI | reads `Lyrics.Diagnostics`; the rail header's dev-mode glyph in `Rail.UI.cs` only calls `Open(overlay, trackId)` (`LyricsInspectorDialog.cs:45-50`) |
| `Diagnostics.StageRects : Signal<bool>` (the 0.3 home of `FG_STAGE_RECTS`) | `Screens/Diagnostics.cs` + a Developer row in `Screens/Settings.UI.cs`, §9 (b) | signal | read `.Value` inside `Lyrics.Stage.Render` — **never** a `static readonly bool` captured at class init |

**Props-freeze map (what must be a Signal / context / Key in 0.3):**

| Changing datum | Mechanism in 0.2.9 | Must stay |
|---|---|---|
| active-line emphasis (bucket + past + reserve bit) | `Signal<int>[]` one per line, value-gated | per-line Signal (a shared memo fans out to every realized row) `:157-194` |
| halo alpha | `FloatSignal[]` one per line, bound to the glow wrapper's `Opacity` | per-line Signal bound (not a static value) `:119-140`, `:2644-2656` |
| karaoke `nowMs` | one shared `FloatSignal` peeked by the row, written per frame | shared Signal, `Peek` inside the row `:153`, `:2731` |
| secondary-line mode | shared `Signal<int> _secondary` read with `.Value` by every row | shared Signal — a ctor int would never reach a mounted row `:242-253` |
| blur strength (halo σ on the ACTIVE row) | `FloatSignal _dofScaleSignal`, read `.Value` ONLY inside the `isActive` branch | Signal, and the conditional read is load-bearing `:255-269`, `:2800` |
| follow mode | `Signal<LyricsFollowMode>`, `Peek` in rows, `.Value` in the ticker + overlay | as-is `:141`, `:2695`, `:3098` |
| document identity | `Key = "lyrics-doc:" + trackId` on the host; row keys `"ll" + docEpoch + ":" + index` | Key remount; `_docEpoch` bumps on every non-same-shape swap `:278-293`, `:1029` |
| a same-shape upgrade (line→line, identical rows) | rows KEPT, arrays reused in place | keep; only `LyricsRowShape.SameRows` may authorise it `:1009-1081` |

---

## 2. Wireframes

Scale: **8 DIP per character** unless a title says otherwise. Rail wireframes are the default 340-DIP rail
(`ShellResponsiveLayout.cs:126`) in a 1180×760 window → rail body height 640 − 44 header = 596.

### W1 — Rail · timed word-by-word · playing · Following @340 (8 DIP/char)

```
┌────────────────────────────────────────────┐ ← rail, w 340, Corners (8,0,0,0), 1px StrokeCardDefault L+T
│ Lyrics                      🌐  </>  ⛶  ✕ │ header h 44, pad L12 R8, gap 4; title Subtitle 20/28/600
├────────────────────────────────────────────┤ each glyph button 32×32, r4, glyph 16
│                                            │  ← TopPad = viewportH·0.40 − 47/2 = 238 − 23 = 215 DIP
│                                            │
│  ░░░░░░░░░░░░░░░░░░░░░░░  σ6.5 · α0.10     │ bucket 6 rows (past side)
│  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒       σ4.0 · α0.10     │ dist 3 past
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓     σ2.5 · α0.13     │ dist 2 past
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓          σ1.25· α0.19     │ dist 1 past  (scale 0.98, origin x=0)
│                                            │
│  Every night in my dreams                  │ ★ ACTIVE, y-centre = 0.40·596 = 238 DIP
│  ███████████▒▒▒▒▒▒▒▒▒▒▒                    │   26/33/700, sung Primary | unsung Primary@0.58
│                                            │   row = 33 + 2·7 = 47 DIP, side pad 22, wrap box 296
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓      σ1.25· α0.45     │ dist 1 future
│  ▓▓▓▓▓▓▓▓▓▓▓▓            σ2.5 · α0.24     │ dist 2
│  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒    σ4.0 · α0.14     │ dist 3
│  ░░░░░░░░░░░░░░░         σ5.5 · α0.11     │ dist 4
│  ░░░░░░░░░░░░░░░░░░      σ6.5 · α0.10     │ dist ≥5
│                                            │
│░░░░░░░░░░░░ AutoEdgeFade band 40 ░░░░░░░░░░│ top+bottom feather, scrollbar suppressed
└────────────────────────────────────────────┘
```

### W2 — Rail · loading shimmer @340

```
┌────────────────────────────────────────────┐
│ Lyrics                            ⛶  ✕    │  (no 🌐 — LyricsPrefs.Available == 0)
├────────────────────────────────────────────┤
│                                            │  padTop 110
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬              │  219.3 × 22, r6, Fill = ink.Skeleton
│                                            │  gap 18
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                   │  183.6
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                │  204.0
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                      │  158.1
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                 │  193.8
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬                        │  147.9   (255 × [.86 .72 .80 .62 .76 .58])
│                                            │  padX 22 == RowSidePad → the bars sit where the lines will
└────────────────────────────────────────────┘  bars breathe: opacity 1 → 0.5 → 1 over 1000 ms
```

Bars are `AlignSelf.Start` (both surfaces are left-aligned — the centred fullscreen shimmer was refuted, `:1456`).
**The STAGE arm is a DIFFERENT set of numbers, not the rail's scaled:** base width **520**, ratios
`[.82 .66 .74 .58 .70 .50]` ⇒ **426.4 / 343.2 / 384.8 / 301.6 / 364.0 / 260.0**, rowH 32, gap 24, padX 64, padTop 150
(`LyricsView.cs:1441-1452`). The breath (`PulseMs 1000`, `PulseMin 0.5`) is the ENGINE's skeleton machinery applied to
whatever `shimmerSource` returns (`SkeletonStyle` defaults, `SkeletonRegion.cs:35`) — `LyricsShimmer` itself animates
nothing. The `SkeletonStyle` the view passes (`RowGap 18 | 14`, `BarRadius 6`, `TextRatio 0.86`, `:875`) is the DERIVED
shimmer's shape and is **inert here** because a `shimmerSource` is supplied; the visible gap is `LyricsShimmer`'s own
`18 | 24`. Do not port the 14.

### W3 — Rail · reveal in progress

Shimmer column fades OUT as an exit orphan under the real document, which fades 0→1 over
**250 ms `Easing.SmoothOut`** (`SkelReveal.FadeOnly`, `SkeletonRegion.cs:161-163` — `Expressive.Fast`). No translate, no
blur, no resize animation (`smoothResize: false`, `LyricsView.cs:876`). The two halves of the dissolve are EQUAL: the
shimmer orphan's own `ExitMs` defaults to the same `Expressive.Fast` 250 (`SkeletonStyle`, `SkeletonRegion.cs:35-36`) and
the view overrides neither. Under `Motion.ReducedMotion` the engine SNAPS the swap (no fade at all) — that is engine
policy, not a value this file authors. On the same frame the first landing HARD-LATCHES the active line
onto the focal band (`_scrollSnapped == false` ⇒ `LatchViewport`, `:2425-2433`), so the reveal never shows a document
scrolling into place.

The shimmer BRANCH is what the engine pulses: `Reconciler.cs:1474-1479` starts `SkeletonPulse(FirstChild(node), PulseMin,
PulseMs)` on the region's first child whatever that subtree is — which is why a custom `shimmerSource` breathes without
`LyricsShimmer` animating anything itself. The region also SUPPRESSES the enclosing scrollbar while branch 1 is up
(`Reconciler.cs:1472`), restoring it on Ready/Failed; the lyrics list sets `SuppressScrollBar` anyway, so this is
invisible here — but it is engine behaviour the 0.3 host inherits, not something to re-implement.

### W4 / W5 — Rail · empty, failed, nothing playing @340

```
┌────────────────────────────────────────────┐   ┌────────────────────────────────────────────┐
│ Lyrics                            ⛶  ✕    │   │ Lyrics                            ⛶  ✕    │
├────────────────────────────────────────────┤   ├────────────────────────────────────────────┤
│                                            │   │                                            │
│                                            │   │                                            │
│           No lyrics available              │   │             Nothing playing                │
│                                            │   │                                            │
└────────────────────────────────────────────┘   └────────────────────────────────────────────┘
 centred both axes, pad X 24 (Spacing.XXL),       same Message() box; string literal at :432
 14/20, ink.Secondary, wrapped   (:2522-2527)     (NOT localised — see §6)
```

A THIRD blank exists and is deliberately not a Message: before the contexts arrive
(`b is null || svc is null`) the view returns a bare `BoxEl { Grow = 1 }` — an empty panel, no text
(`:428`). It is one frame at startup; a "Nothing playing" there would flash a false claim.

### W6 — Rail · unsynced document @340

```
┌────────────────────────────────────────────┐
│ Lyrics                            ⛶  ✕    │
├────────────────────────────────────────────┤
│                                            │ top pad 26 (large: 44)
│  Sonnet 18, or a lyric with no timings     │ 19/26/700, colour ink.Primary @ 0.88
│  laid out as a reading block: left          │ rowPad 5 → inter-line gap 10, side pad 22
│  aligned, wrapped, never ellipsised.        │ plain ScrollEl, ScrollKey "lyrics:unsynced:"+trackId
│                                            │ AutoEdgeFade, scrollbar suppressed
│  Second stanza …                           │ NO active line, NO wipe, NO blur ladder
└────────────────────────────────────────────┘   (:1383-1435)
```

### W7 — Rail · timed document while a VIDEO is playing @340

```
┌────────────────────────────────────────────┐
│ Lyrics                            ⛶  ✕    │
├────────────────────────────────────────────┤
│        ┌──────────────────────────┐        │ note: top-docked, centred, pad (12,12,12,0)
│        │ Synced lyrics aren't      │        │ plate = ink.Plate, r4, pad (8,4,8,4)
│        │ available while a video…  │        │ 12/600 ink.Tertiary, 1 line, char-ellipsis
│        └──────────────────────────┘        │ loc: player.lyricsSyncUnavailableDuringVideo
│                                            │
│  The whole document renders in the UNSYNCED│ LyricsContent returns UnsyncedLyricsContent
│  treatment (19/26) and stays scrollable.   │ (:1329) — no highlight, no follow, no wipe
└────────────────────────────────────────────┘   (:480-511, LyricsSyncGate.cs:25)
```

### W8 — Rail · detached (user scrolled) → resync pill @340

```
┌────────────────────────────────────────────┐
│ Lyrics                            ⛶  ✕    │
├────────────────────────────────────────────┤
│  every line CRISP (σ suppressed → 0)       │ SuppressesDof(mode != Following) → targets 0,
│  opacity ladder unchanged                  │ eased down at τ 65 ms (:1576, :1601-1607)
│  ...                                       │
│  ...                                       │
│              ┌────────────────┐            │ pill: pad (11,7,13,7), r16, gap 8
│              │ ◕  Resync      │            │ ring 18 (stroke 1.69), ink.RingFill on RingTrack
│              └────────────────┘            │ label 12/16/600 ink.Primary
│                      ▲ 18 DIP above bottom │ Enter/Exit: Dy 4 + opacity 0, LayoutTransition.Fade
└────────────────────────────────────────────┘ (28 on the stage)             (:513-549)
```

The ring runs 1 → 0 over `ResyncIdleMs` 4000 ms, quantised to `ResyncProgressSteps` 120 rungs (`:136-137`, `:1946-1962`);
at 0 it self-resyncs. Clicking it resyncs immediately (`:535`).

### W9 — Rail · interlude (≥5 s instrumental gap) @340

```
┌────────────────────────────────────────────┐
│ …previous line, RETIRED onto the past       │ dist 1 past: α0.19, σ1.25 — no special "interlude look"
│  ladder                                     │
│                                            │
│        ● ● ●                               │ dots: 9 DIP, gap 8, left margin = RowSidePad 22
│                                            │ bottom edge sits InterludeDotLift 36 DIP above the band
│  ╭ reserved band: 9 + 2·8 = 25 DIP ────────╮│ the ANCHOR row carries it as extra TOP PAD (bit 3)
│  The next line, unsung, sharp, dist 0       ││ focal band, y = 238; the line itself does NOT move
└────────────────────────────────────────────┘
   fill: dot k brightens 0.58 → 1.0 across its third of (gapEnd − 1000 ms − gapStart)
   breath: scale 1 − 0.06(1−pulse), alpha ×(1 − 0.10(1−pulse)), sine period 2600 ms, on the MEDIA clock
   anchor: TWO empty Grow spacers split the panel at `band`, and the dots row's BOTTOM MARGIN buys the rest —
           m = (d + lift)/band − d, in which the viewport height cancels: **rail m = (9+36)/0.40 − 9 = 103.5 DIP**,
           stage m = (12+48)/0.38 − 12 = 145.9 DIP. One build-time constant, exact at every viewport size (`:659`).
   stage values: dot 12, air 10, reserve 32, lift 48, gap 10, left margin = RowSidePad 64
   the WHOLE dots subtree is `HitTestVisible = false` — explicitly decorative: not hittable, not focusable, no
   automation role (the engine names nodes from text and the dots have none), and the lyrics under it stay
   scrollable and clickable straight through (`:665`, and the A11Y note at `:577-581`)
```

### W10 — Rail header · hover / focus / latched @340 (2× horizontal zoom)

```
│ Lyrics                     🌐   </>   ⛶    ✕ │
│ ▲Subtitle 20/28/600         │     │    │    └ Tok.TextSecondary → HoverColor TextPrimary
│  Grow 1, NoWrap, ellipsis   │     │    └────── Icons.FullScreen, tip "Expand lyrics"
│                             │     └─────────── Icons.Code, tip "Inspect lyrics source" (dev mode only)
│                             └───────────────── Icons.Globe, tip = LyricsPrefs.Tooltip(mode)
│                                                latched ⇒ glyph Tok.AccentTextPrimary
│  each: 32×32, r4, Interaction.Subtle (hover fill 83 ms), Focusable, AllowFocusOnInteraction=false
```

### W11 — Immersive stage · WIDE @1180×760 (**16 DIP per char**)

```
╔═══════════════════════════════════════════════════════════════════════╗
║  caption band, h 48 — pass-through to the shell's own titlebar         ║
╠═══════════════════════════════════════════════════════════════════════╣ body h = 760−48−72 = 640
║ ▒▒▒▒ σ80 baked-blur cover, ×1.30 paint scale, drifting ▒▒▒▒▒▒▒▒▒▒▒▒▒▒ ║ scrim 0.76 at y=0
║ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ [🌐40] [⌄44]║ top band h 88, pad (16,16,16,12)
║ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░                                    ║ column shade: 0.26 held to
║ ░░ ┌───────────────┐ ░░░  Every night in my dreams                    ║ x=352, feathered to 0 at 612
║ ░  │               │  ░   ██████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒                   ║ ← ACTIVE, 36/46/700
║ ░  │  cover 232²   │  ░                                               ║   y-centre = 0.38·408 = 155
║ ░  │               │  ░   I see you, I feel you                       ║   (inside the lyrics viewport)
║ ░  └───────────────┘  ░   ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  σ1.2 α0.45              ║
║ ░   Title · artist ♥ ⋯ ░   ▒▒▒▒▒▒▒▒▒▒▒▒▒     σ2.0 α0.24              ║ stage σ ladder is FLATTER
║ ░   ──────●────────── ░    ░░░░░░░░░░░░░░    σ2.6 α0.14              ║ (0/1.2/2.0/2.6/3.0)
║ ░   1:02        −2:14 ░    ░░░░░░░░░        σ3.0 α0.11              ║
║ ░    ⤨  ⏮  ( ▶ )  ⏭  ⟳ ░                                             ║ identity column: chapter 21
║ ░   ── volume ──────  ░                                               ║ box w 352, gap 56, pad X 24
║ ░   ♪ This computer   ░                                               ║ pane region w = 1180−408 = 772
║ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓                       Lyrics · Queue         ║ pivot band h 72, End-justified,
╠═══════════════════════════════════════════════════════════════════════╣ scrim 0.70 at y=1
║  player-bar band, h 72 — pass-through to the docked player bar         ║
╚═══════════════════════════════════════════════════════════════════════╝
```

### W12 — Immersive stage · COMPACT @<600 wide (**16 DIP per char**)

```
╔═══════════════════════════╗  StageLayout.Resolve: width < 600 (or a promotion without
║ caption band h 48         ║  the 40-DIP reserve) ⇒ CompactStage
╠═══════════════════════════╣  Folded = Shuffle|Repeat|Volume|OutputDevice → the "…" overflow
║ [cover 64] Title      [⌄] ║  top band h 56 (CompactTopBandH)
║            artist         ║  identity is a full-bleed HEADER ROW (LayoutWidth 0), art 64
║ ─────────────────────────  ║  play 40, step 32
║  Every night in my dreams ║  the lyrics pane takes the whole width; the column still caps
║  █████████▒▒▒▒▒▒▒▒▒▒      ║  at MaxWidth 700 and keeps its 64-DIP gutters
║  I see you, I feel you    ║  NO column shade (bound width → 0 in compact)
║          Lyrics · Queue   ║  pivot band h 72
╠═══════════════════════════╣
║ player-bar band h 72      ║
╚═══════════════════════════╝
Hysteresis: demotion immediate; promotion needs 600 + 40 = 640 (StageLayout.cs:55-58,322-324).
Height ladder (wide only): device line folds, then the volume row, then the whole shape demotes when the
cover cannot keep 168 (StageLayout.cs:164-183,327-345); art is quantised to 4 DIP so a vertical drag
re-renders the mounted LyricsView at most once per 4 DIP.
```

### W13 — Immersive · the reading column @1180 (8 DIP/char, cropped)

```
        pane region 772 ──────────────────────────────────────────────────────────►
        ┌───────────────────────────────────────────────────────────┬──48 gutter──┐
        │ column, MaxWidth 700, Grow 1, LEFT-anchored               │ (trailing)  │
        │  ├── 64 ──┤                                     ├── 64 ──┤│             │
        │           Every night in my dreams                        │             │ row = 46 + 2·9 = 64
        │           ███████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒                  │             │ wrap box = 700−128 = 572
        │           Je te vois, je te sens                           │             │ secondary: 22.3/28.5/600
        │                                                           │             │ ink.Secondary, 3 DIP above
        └───────────────────────────────────────────────────────────┴─────────────┘
        bottom of the column reserves PivotBandH 72 as padding (StagePanes.cs:108), and the pivot
        band itself is a further 72 below it ⇒ lyrics viewport h = 640 − 88 − 72 − 72 = 408
```

### W14 — Immersive top bar @1180 (8 DIP/char)

```
                                                      ┌──40──┐  ┌──44──┐
… ▒▒▒▒ scrim 0.76 ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  │  🌐  │  │  ⌄   │
                                                      └──────┘  └──────┘
  spacer Grow 1, HitTestVisible=false          gap 8 ▲          ▲ pad R16, T16, B12
  🌐 ScrimFab 40: circle, Fill StageInk.ScrimRest, 1px StageInk.Stroke, glyph 16 InkSecondary→Ink,
     latched ⇒ glyph = StageChrome.AccentFor(track); hidden unless Available != 0 AND pane == Lyrics
  ⌄ ExitFab 44: circle, Fill StageInk.GlassPlate (ink @0.14), 1px Stroke, Elevation.Card shadow,
     glyph Icons.ChevronDown 18 at StageInk.Ink (HoverColor = Ink too — the way out never dims);
     tooltip "Close lyrics (Esc)"
  both: HoverScale 1.07 / PressScale 0.92, brush 83 ms (WaveeMotion.Faster)
  band pad is (16,16,16,12) — LEFT 16 as well — and AlignItems = Start, so both discs hang from the
  TOP of the 88-DIP band rather than centring in it (ImmersiveLyricsSurface.cs:374-377)
```

### W15 — One line, exploded (8 DIP/char, stage metrics)

```
row BoxEl                         Direction 1, Shrink 0, Justify Center, AlignItems Stretch
│  Padding (64, 9 [+reserve], 64, 9)    TransformOriginX 0, OriginY 0.5
│  ScaleX/ScaleY = 0.98|1.0 (springs)   Opacity = ladder (spring)   Cursor Hand, Role Button
└── dofContent BoxEl               Blur = live σ (LyricsView owns it), BlurCachePolicy HoldIfCached
    │                              ← the CASCADE writes LocalTransform.Dy on THIS node
    ├── textEl ZStack
    │   ├── glow  BoxEl  Opacity = bound per-line FloatSignal × ink.BloomScale, HitTestVisible false
    │   │    └── TextEl(bloom)  GlyphWipe(Before bloom, After bloom@0, Split, Softness, Lift)
    │   │        word-by-word: glyphs mount only while dist ≤ 2; σ driven by OnFrame (4.5 | 3)×scale×α
    │   │        line-synced : constant Blur (10 | 7)×haloScale on the ACTIVE row only, text @0.4
    │   └── main  TextEl(sung)  GlyphWipe(Before Primary, After Primary@0.58, Split, Softness, Lift)
    │              36/46/700, Wrap, MaxLines 0, Trim None   (line-synced: no wipe, colour + 167 ms fade)
    └── SecondaryText TextEl      size ×0.62, LH ×0.62, weight 600, ink.Secondary, Margin top 3
                                  no wipe, no glow, no lift — inherits blur/scale/opacity/cascade
```

### W16 — The wipe, at the boundary (8 DIP/char, rail)

```
  Every night in  my dreams
  ███████████████▓▒░░░░░░░░
  ◄── sung ──────►│◄ feather ►│◄── unsung ──►
   Primary α1.0   │ 5 DIP     │  Primary α0.58   (stage: 7 DIP)
                  │
   Split = Σ(char-weighted syllable progress) + 0.02, pixel-quantised: round(split·runW)/runW
   Softness = clamp(5 / runLen(line), 0.01, 0.10)      runLen = Σ widths of wrapped fragments
   Lift = 1.25 (rail) / 1.75 (stage): an UNSUNG glyph sits that low and rises as the feather passes
   settled endpoints (0 and 1) are written EXACTLY once and then frozen (pin-cache + plain-glyph batch)
```

### W17 — Inspector dialog · Providers tab (8 DIP/char)

```
┌──────────────────────────────────────────────────────────────────┐ ContentDialog, DialogWidth 548
│ Lyrics source inspector                                          │ title = player.lyricsInspector
├──────────────────────────────────────────────────────────────────┤ content width 492, gap 12
│ [Copy full report] [Save bundle…] [Refresh] [Re-fetch from prov…]│ Button.Standard ×3 + Button.Accent
│ Re-fetch drops this track from the memory AND disk lyric caches… │ 11/15 TextTertiary, wrapped
│ ┌ Providers ┬ Raw response ┬ Parsed ┐                            │ SelectorBar bound to _tab
│ └───────────┴──────────────┴────────┘                            │
│ amll won on timing (syllable) over lrclib                        │ 13/18/600 AccentTextPrimary
│ “Every night in my dreams” — Céline Dion                         │ 12/16 TextPrimary
│ album My Heart Will Go On · 274s · ISRC USSM19900123 · id 4X…    │ 11/15 TextTertiary
│ ───────────────────────────────────────────────────────────────  │ 1px StrokeCardDefault
│ ┌──────────────────────────────────────────────────────────────┐ │ card: pad 10, r6,
│ │ ● amll                              HIT · 412ms              │ │ Fill FillSubtleSecondary,
│ │ ★ CHOSEN — reranker score 0.93 (timing + text)               │ │ 1px border = AccentDefault
│ │ parsed: Syllable, 42 lines, 310 syllables, matched by         │ │ when winner, else StrokeCardDefault
│ │ Identity, prior 0.90                                          │ │ dot 8×8: Hit green (.30,.78,.45)
│ │ timing: clean                                                 │ │ Timeout amber, Error red,
│ │ [Raw (1)] [Parsed]                                            │ │ Skipped dim, Miss grey
│ └──────────────────────────────────────────────────────────────┘ │
│ ┌ ● lrclib   MISS · 980ms  not chosen — the provider had nothing… ┐│
├──────────────────────────────────────────────────────────────────┤
│                                                        [ Close ] │ CloseText = common.close
└──────────────────────────────────────────────────────────────────┘
```

### W18 / W19 — Inspector · Raw and Parsed tabs

```
RAW                                          PARSED
┌──────────────────────────────────────────┐ ┌──────────────────────────────────────────┐
│ (amll (1)) (lrclib (2))                  │ │ (Final (what the UI got)) (amll) (lrclib)│ Pill chips, Wrap 6
│ ┌──────────────────────────────────────┐ │ │ The document handed to LyricsView — the  │ 12/16 TextSecondary
│ │ GET https://…/amll/4X…ttml           │ │ │ winner AFTER the reranker's offset corr. │
│ │ ttml · 48,213 chars · showing the    │ │ │ provider amll · sync Syllable · isSynced │ 11/15 TextTertiary
│ │ first 4,000                 [Copy]   │ │ │ True · 42 lines · 310 syllables · offset │
│ │ ────────────────────────────────────  │ │ │ applied −120ms                           │
│ │ <tt xmlns="http://www.w3.org/ns/ttml"│ │ │ timing check: clean                      │ green when "clean…",
│ │ …                                    │ │ │ [Show syllables] [Copy parsed]           │ else Warn amber
│ │ Cascadia Code 11/15, TextPrimary,wrap│ │ │ ───────────────────────────────────────  │
│ └──────────────────────────────────────┘ │ │  0  00:12.480→00:16.020   3540ms  Every… │ mono cols
│  (Fill FillControlSecondary, 1px          │ │     [tr] Chaque nuit dans mes rêves      │ 26 / 130 / 52 / grow
│   StrokeControlDefault, pad 10, r6)       │ │     wa[12480-12610] ter[12610-12980] …   │ 10.5/14 TextTertiary,
└──────────────────────────────────────────┘ └──────────────────────────────────────────┘ indent 34
 on-screen caps: 4,000 raw chars, 300 lines; Copy always hands over the full capture (:72-75)
 parsed row colours (LineRow, :481-505): time column Bad red when out-of-order OR end < start, Warn amber
 when a non-first line has StartMs 0 OR LyricsTiming.IsSquashed, else TextSecondary; the duration column
 (52 wide, "217ms") is Warn when squashed, else TextTertiary; blank text renders "(blank)" in TextTertiary
```

### W19b — Inspector · the states that are NOT a report (8 DIP/char)

```
NO TRACK                         NO REPORT YET                    RE-FETCHING / STATUS
┌────────────────────────────┐   ┌────────────────────────────┐   ┌────────────────────────────┐
│ Nothing is playing. Start  │   │ [Copy…][Save…][Refresh][…] │   │ [Copy…][Working…][Refresh] │ Save+Re-fetch disabled,
│ a track and reopen this    │   │ Re-fetch drops this track… │   │ [Re-fetching…]             │ labels swap while busy
│ dialog.                    │   │ ┌ Providers ┬ Raw ┬ Parsed │   │ Re-fetch drops this track… │ (:143-146)
└────────────────────────────┘   │ No lyrics search has been  │   │ Nothing cached to save —   │ status line 12/16 in
 Note() 12/17 TextSecondary,     │ recorded for this track    │   │ re-fetching first…         │ Warn amber (:151-152)
 the ONLY child (no toolbar,     │ yet — the fetch may still  │   └────────────────────────────┘
 no tabs) — the dialog still     │ be in flight, or this is a │    after a save: "Saved 3 payload(s) to <folder>" and
 has its own Close (:101-102)    │ local/fake track. Hit      │    the folder is OPENED; with none captured the status
                                 │ Refresh, or Re-fetch…      │    says so LOUDLY rather than handing over an empty
                                 └────────────────────────────┘    bundle (:167-182)
 Raw tab with nothing captured: Note "No provider payload was captured for this track." + two Captions
 (the recorded note, and "Payloads are only captured when a fan-out actually runs…") (:338-343)
 Parsed tab with nothing: Note + the recorded note (:411-414); a focus naming a source with no document:
 "That provider produced no document." (:436)

 FIVE more states the tabs can land in, all real and all reachable:
  • report present but NO source ran → Note "No source ran for this track." under the identity block (:244-245).
    (The DEBUG overlay's twin of this is a plain Caption "(no sources ran)", LyricsView.cs:943-944.)
  • a payload past the 128 k CAPTURE cap → the caption reads
    "ttml · 412,880 chars · CAPTURE-TRUNCATED to 128,000 · showing the first 4,000" (:379-381). Two different
    truncations, named separately on purpose: one is the store's cap, one is the layout's.
  • a parsed document past 300 lines → a trailing Caption "…N more lines — use Copy parsed to see them all." (:475-476).
  • a line with no EndMs → the time column renders "00:12.480→  --:--.---" and the duration cell is EMPTY (not "0ms")
    (:494-499); a blank syllable prints "␣" in the syllable strip (:515).
  • a CANDIDATE focus (not Final) swaps the description line to
    "“amll” as parsed, BEFORE the reranker's offset correction." (:443-446) — the Final line is the only one that
    claims to be what the UI got.
 Re-fetch can also fail loudly, in the same amber status line: "This session has no re-fetchable lyrics provider
 (offline / not logged in)." when `Services.Lyrics` is not `ILyricsRefetch` (:187-190), and "Re-fetch failed — <Type>:
 <message>" on a throw (:201-209). Both leave the dialog usable; neither is a toast.
```

### W20 — NPV lyrics peek (rail ▸ Details) @340

```
│ ▌ Every night in my dreams              │ spine w 3, r1.5, Fill Tok.AccentDefault, full height 112
│ ▌ ██████████████████████                │ active slot h 56, opacity 1
│ ▌ I see you, I feel you                 │ peek slot  h 56, opacity 0.38
│ ▌                                       │ text: WaveeType.NpvLyric = Subtitle 20/28 in
                                            "Segoe UI Variable Display" @ weight 350, CharSpacing −6
  flip: Enter Dy +56 / Exit Dy −56 + opacity, MotionTok.ControlFast (150 ms, FluentStandard)
  clock: its OWN 100 ms UseInterval over PositionMs.Peek() + LyricsPeekClock.LeadMs 140 (see §5 note)
  click anywhere → ShellUi.Toggle(RailMode.Lyrics)
  video active ⇒ ONE 56-DIP row: spine at TextTertiary (half height, 28) + the same loc note, 12/Tertiary,
    NoWrap · MaxLines 1 · CharacterEllipsis (the reel's own slots wrap; this caption does not). Order matters:
    the `!show` early-out runs BEFORE the video branch (:67-68), so over an UNSYNCED or absent document a video
    shows NOTHING — the note is a state of a synced document, never a stand-in for a missing one
  text in a slot: MaxLines 2, Wrap, CharacterEllipsis (a long lyric wraps to two 28-DIP lines, then trims)
  HIDDEN ENTIRELY — a bare `new BoxEl()`, zero height, no spine, no gap — unless
  LyricsPeekClock.ShouldShow(doc): Lines.Count > 0 AND Sync ∈ {Line, Syllable}. An UNSYNCED or absent
  document leaves NO trace in the Details rail (NpvLyricsPeek.cs:58,67, LyricsPeekClock.cs:15-16);
  the 100 ms interval is not armed in that state either — stopping the clock IS the suppression (:63)
  before the first line: active −1 ⇒ the pair is (−1, 0), i.e. an EMPTY top slot over a faded first line;
  after the last: the last line holds and the peek slot is empty (LyricsPeekClock.cs:41-43)
  the reel runs its OWN UseResource fetch — `GetLyricsAsync(trackId)` keyed on the BARE track id (:52-56), a second
  call beside LyricsView's `trackId + "|" + artist` one. Both are served by the same provider cache today; in 0.3
  both must read the ONE Lyrics side table (§7 gap 1) rather than each planning a fetch
  the first paint seeds (active, peek) synchronously from `PositionMs.Peek()` rather than waiting a tick, so the reel
  never shows an empty pair for 100 ms (:69-73); a UseLayoutEffect re-ticks it on every `show` edge (:64)
```

### W21 — Lyrics-search debug overlay (now gated by diag.developerMode) — **KEPT in 0.3, gate changed**

> **Port this surface.** Decision, Christos, 2026-09-12: contrary to this chapter's own (a) below, the pill and the
> overlay are KEPT, not deleted — they survive as the 0.2.9 wireframe below draws them, re-homed on the same
> persisted `diag.developerMode` setting the inspector already uses (`RightRail.cs:382`, `AppSettings.cs:308`)
> instead of the `WAVEE_LYRICS_DEBUG` environment variable, which is the thing that is actually deleted. They still
> read the same `LyricsDiagnostics.ForTrack(trackId)` report the inspector reads (`LyricsView.cs:909` vs
> `LyricsInspectorDialog.cs:28`) and still render a strict SUBSET of W17 (identity block + per-source rows, no raw
> payloads, no parsed candidates, no re-fetch, no save bundle) — that overlap with the inspector is real, but it is
> not a reason to delete a surface Christos wants kept. The `// ink-scan: off|on` marker comments (`LyricsView.cs:880-885`)
> and the `_debugOpen` signal (`:317`) are KEPT as well, since they belong to the surface, not to the env var. Only
> the env-var *read* that gated `_debugOpen` is replaced by a `diag.developerMode` read — the same pattern the
> `LyricsInspectorButton` already uses one line above it in the header row.

```
┌────────────────────────────────────────────┐   closed: a pill bottom-right, pad 12 from both edges,
│ Lyrics search                     close ✕  │   "lyrics debug" 12/16/600 TextSecondary on
│ amll won on timing (syllable)              │   FillSolidBase@0.90, 1px StrokeCardDefault, r4
│ “Title” — Artist                           │
│ album X · 274s · ISRC …                    │   open: full-bleed FillSolidBase@0.97 over the stack,
│ ──────────────────────────────────────────  │   ScrollEl, pad 16, gap 8; per-source rows with the
│ ● amll   HIT · 412ms  ★ winner             │   same dot colours as W17; rerank score + reason line
│   rerank score 0.93 · timing+text          │   THIS PANEL KEEPS THEME INK on purpose (it is a plate,
└────────────────────────────────────────────┘   not the reading surface) — :880-885
 its two EMPTY states: no report ⇒ one wrapped 12/16 TextSecondary line, "No search recorded for this track yet — it
 may be a local/fake track, or the fetch is still in flight. Close and reopen to refresh." (:929-933); a report with
 no sources ⇒ "(no sources ran)" 12/16 TextSecondary (:943-944). A row's rerank line renders only for a HIT with a
 non-empty reason (:988-989) and its detail breadcrumb only when the trace carries one (:986-987)
```

### W22 — Secondary line on (translation) — row detail @340

```
│  Every night in my dreams          │ 26/33/700, wipe as usual
│  Chaque nuit dans mes rêves        │ 16.1/20.5/600, ink.Secondary, margin-top 3
│                                    │ the pair sits INSIDE dofContent, so it blurs, scales,
                                       fades and travels with the lyric (:2843-2847)
 A mode flip re-measures EVERY row: Mark(LayoutDirty|VirtualRangeDirty) + ResetScrollSnap
 ⇒ the next frame hard-latches the active line onto the band (:394-399)
```

### W23 — 0.3 ONLY · where the one retired env seam lands (Settings ▸ Diagnostics ▸ Developer)

Only `FG_STAGE_RECTS` retires into a settings row here — W21's pill and overlay are KEPT as their own surface (§9 (a),
2026-09-12) and are NOT redrawn as a settings row; this card hosts the stage-geometry toggle alone. It is the SAME expander card
and the SAME row shape as the Developer group already drawn in `27-settings-and-diagnostics.md` (its "Developer" block:
`Developer mode` / `FPS overlay` / `Archive Spotify realtime traffic` / `Simulate an update`), with ONE row added:

```
⟨⟩ Developer                                                                        every row here is isEnabled: dev
   Debugging and inspection tools for Wavee itself
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ ⚙  Developer mode          Shows lyrics inspector…                            (  ●)  │ ← existing (ch. 27; 0.3
│    drops "the API console," from this caption — Q7, 2026-09-12                        │   copy, console deleted)
│ 🕐 FPS overlay             Frame timing in the corner of the window            (  ●)  │ ← existing, the PATTERN
│ ▦  Stage geometry log      Logs the immersive lyrics stage's arranged rects    (  ●)  │ ← NEW, replaces
│    to `stage` on every bounds change: root · body · content · identity ·                   FG_STAGE_RECTS
│    panes · lyricscolumn · pivot                                                        │
│ 📄 Archive Spotify realtime traffic  …                                         (  ●)  │ ← existing
└────────────────────────────────────────────────────────────────────────────────────────┘
 toggle → Diagnostics.StageRects.Value (persisted as `diag.stageRects`, default false), on the exact
 DeveloperMode.FpsOverlay shape: persist then publish (App/DeveloperMode.cs:29-31,41-46)
 the stage READS it with .Value inside Render, so a flip re-renders the surface and installs or removes the SEVEN
 OnBoundsChanged callbacks (ImmersiveLyricsSurface.cs:183,211,222,331,342 + StagePanes.cs:113,129); the log text is
 unchanged — `rect <name> = x,y wxh` on category `stage` (:251-252)
 NOT a row: the advance probe. It owns the frame loop (host.RunFrame() + window.WaitForWork, WaveeNavProbe.cs:1845-1846)
 and refuses to run under --fake (:1854), so it can never be an in-app button. It is a CLI entry:
   Wavee.exe --lyrics-advance-probe [--probe-out <dir>] [--probe-playback-frames N] [--probe-lyrics-frames N]
 dispatched from Screens/Diagnostics.Probe.cs beside --perf-bench / --startup-bench / --crash-probe
 (Program.cs:197-207); the two EnvInt knobs it reads today (WaveeNavProbe.cs:1864,1868) become those two optional args.
```

---

## 3. Tokens

| element | size (DIP) | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| rail lyric row | h = 33 + 2·7 = 47 (1-line) | pad (22,7,22,7) | — | 26 / 33 / 700, Wrap, MaxLines 0, Trim None | `ink.Primary` (sung) / `@0.58` (unsung) | — | `LyricsView.cs:1312-1316,1336,2880-2889` |
| stage lyric row | h = 46 + 2·9 = 64 (1-line) | pad (64,9,64,9) | — | 36 / 46 / 700 | same | — | same |
| secondary line | ×0.62 of the above (16.1 / 22.3) | margin-top 3 | — | ×0.62 LH, weight 600 | `ink.Secondary` | — | `:2899-2919` |
| unsynced row (rail / stage) | 26 / 36 LH | pad (22,5,22,5) / (64,7,64,7) | — | 19 / 26 / 700 · 28 / 36 / 700 | `ink.Primary @0.88` | — | `:1391-1418` |
| unsynced block | — | top/bottom 26 (rail) / 44 (stage) | — | — | — | `AutoEdgeFade`, no scrollbar | `:1421-1434` |
| shimmer bar | 22 × w (rail) / 32 × w (stage) | gap 18 / 24, pad X 22 / 64, padTop 110 / 150 | 6 | — | `ink.Skeleton` (`FillSubtleSecondary` / `StageInk.Ink@0.12`) | pulse 1→0.5→1 @1000 ms | `:1439-1469`, `StageArm.cs:139-141` |
| resync pill | h ≈ 32 | pad (11,7,13,7), gap 8, bottom inset 18 / 28 | 16 | label 12 / 16 / 600 | `ink.Plate` + 1px `ink.PlateStroke`; hover `PlateHover`, press `PlatePressed` | `PressScale 0.98` | `:523-545` |
| resync ring | 18 (stroke 1.6875) | — | circle | — | `ink.RingFill` on `ink.RingTrack` | determinate | `:542-543`, `ProgressRing.cs:42` |
| video note | auto | pad (8,4,8,4); band pad (12,20/12,12,0) | 4 (`Radii.Control`) | 12 / — / 600, 1 line, ellipsis | text `ink.Tertiary`, plate `ink.Plate` | — | `:480-509` |
| interlude dot | 9 (rail) / 12 (stage) | gap 8 / 10; air 8 / 10; lift 36 / 48 | circle | — | `ink.Primary`, alpha bound 0.58→1 | — | `:616-621,706-712` |
| interlude reserve | 25 / 32 extra TOP pad on the anchor row | — | — | — | — | — | `:618`, `:2671` |
| "No lyrics" / "Nothing playing" | auto | pad X 24 | — | 14 / 20 | `ink.Secondary` | — | `:2522-2527` |
| rail header | h 44 | pad (12,0,8,0), gap 4 | — | 20 / 28 / 600 | `Tok.TextPrimary` | — | `RightRail.cs:121-128` |
| header glyph button | 32 × 32 | — | 4 | glyph 16 (`Theme.IconFont`) | `TextSecondary` → hover `TextPrimary`; latched `AccentTextPrimary` | `Interaction.Subtle` | `RightRail.cs:418-434,447` |
| stage top band | h 88 wide / 56 compact | pad (16,16,16,12), gap 8 | — | — | no veil of its own | — | `StageChrome.cs:69-75`, `ImmersiveLyricsSurface.cs:372-381` |
| stage secondary FAB | 40 | — | circle | glyph 16 | `StageInk.ScrimRest/Hover/Pressed` + 1px `Stroke` | hover 1.07 / press 0.92 | `StageChrome.cs:157-183` |
| stage exit FAB | 44 | — | circle | glyph 18 | `StageInk.GlassPlate` (ink @0.14/0.22/0.28) + 1px `Stroke` | **`Elevation.Card`** | `StageChrome.cs:200-226` |
| stage pivot band | h 72 | pad (24,0,24,16), gap 16 (`ContextBandLayout.PivotGap`) | — | — | — | `AlignItems = End` **and** `Justify = End` — the links hang from the BOTTOM-RIGHT corner, 16 DIP up, not centred in the 72 | `StagePanes.cs:127-140` |
| stage pivot link | auto | pad (8, 4, 8, 2) (`PivotPadX`, `Spacing.XS`, `Spacing.XXS`) | 4 | 14 / 20 / 600 (`WaveeCta.TextAction*`) | active `StageInk.Ink`, else `InkTertiary`, hover `Ink`; underline 2 DIP (`UnderlineHeight`) `accent` else transparent, `Margin.Top` 4 (`UnderlineGap`) | `Role = Tab`, underline brush fades 167 ms | `StageChrome.cs:326-352`, `ContextBandLayout.cs:50-66` |
| stage reading column | MaxWidth 700 | pad (0,0,48,72) | — | — | — | left-anchored, Grow 1 | `StagePanes.cs:96-123`, `ImmersiveLyricsSurface.cs:53-57` |
| stage identity column | 352 (content 304) | gap to pane 56, pad X 24 | — | — | — | — | `StageLayout.cs:65-88` |
| backdrop | viewport ×1.30 (paint) | — | — | — | cover image, `BakedBlurSpec(σ 80, scale 0.5)` | placeholder `StageInk.ArtStandIn`, fade 220 ms | `ImmersiveLyricsSurface.cs:62-65,422-429` |
| stage floor | full body | — | — | — | `StageInk.Floor` = `Tok.MediaStage` (#0A0A0A) dark / `PageToneNeutralLight` light | opaque | `StageArm.cs:47-52` |
| inspector dialog | 548 (content 492) | gap 12; cards pad 10, gap 5/6 | 6 | body 12/16; mono 11/15 "Cascadia Code" | theme rungs (`TextPrimary/Secondary/Tertiary`) | ContentDialog card | `LyricsInspectorDialog.cs:59-76,304-311` |
| NPV peek | 2 × 56 (or 1 × 56, or **0** — hidden) | gap 8, spine 3 | 1.5 | 20 / 28 / 350 Display, CharSpacing −6, MaxLines 2, char-ellipsis | `Tok.TextPrimary`, peek `Opacity 0.38`, spine `AccentDefault` (video: `TextTertiary`) | — | `NpvLyricsPeek.cs:19-22,147-176` |
| inspector status line | auto | — | — | 12 / 16 | `Warn` amber (0.92, 0.70, 0.25) | — | `LyricsInspectorDialog.cs:151-152` |
| inspector provider id / outcome | — | card gap 5 | — | 13 / 18 / **700** · outcome 11 / 18 / 600 | winner `AccentTextPrimary`, else `TextPrimary`; outcome `TextTertiary` | — | `LyricsInspectorDialog.cs:273-276` |
| inspector verdict line | — | — | — | 12 / 16 | winner `Good` green, else `TextSecondary` | — | `:277-281` |
| inspector "parsed:" / "timing:" lines | — | card gap 5 | — | 11 / 15 · 11 / 15 | `TextTertiary`; the timing line is `Warn` amber unless it starts with "clean" | — | `:288-295` |
| inspector payload card | — | pad 10, gap 6; head gap 4 | 6 | label 11 / 15 **mono** `TextSecondary`; body 11 / 15 mono `TextPrimary` wrapped | `FillControlSecondary` + 1px `StrokeControlDefault`; inner 1px `StrokeCardDefault` divider | — | `:371-405` |
| inspector parsed row | — | row gap 2; `HStack` gap 8 | — | index 11/16 mono w 26 · time 11/16 mono w 130 · duration 11/16 mono w 52 · text 12/16 wrapped | index + duration `TextTertiary`, time per the anomaly rules (W19), text `TextPrimary` (`(blank)` → `TextTertiary`) | `Shrink 0` on all three mono columns | `:481-505` |
| inspector sub-line / syllable strip | — | margin-left 34 | — | `[tr]`/`[ro]` 11 / 15 · syllables 10.5 / 14 mono | `TextTertiary` | — | `:507-531` |
| debug pill (gated by `diag.developerMode`, was `WAVEE_LYRICS_DEBUG`) — **KEPT, gate changed 2026-09-12** | auto | pad (8,4,8,4); inset 12 from right+bottom | 4 | 12 / 16 / 600 | `FillSolidBase@0.90` + 1px `StrokeCardDefault`, text `TextSecondary` | — | `LyricsView.cs:886-905` |
| debug overlay — **KEPT, gate changed 2026-09-12** (W21 banner, §9 a) | full bleed | pad 16, gap 8 | — | title 18 / 24 / 600; rows 14/20/600 + 12/16 | `FillSolidBase@0.97`; **theme ink on purpose** | `ScrollEl` | `LyricsView.cs:907-959` |
| 0.3 stage-geometry toggle row (W23) | — | — | — | — | — | no lyrics-owned tokens: it is chapter 27's ordinary Developer `SettingsRow` | `App/DeveloperMode.cs:29-31` (the pattern) |

Ink resolution (`LyricsInk.cs:34-90`): `Primary/Secondary/Tertiary` → `Tok.Text*` on the rail, `StageInk.Ink*` on the
stage; `Plate/PlateHover/PlatePressed/PlateStroke` → `FillSolidBase@0.92` / `FillSubtleSecondary` / `FillSubtleTertiary` /
`StrokeCardDefault` on the rail, `StageInk.Scrim*` / `Stroke` on the stage; `RingFill/RingTrack` → `AccentDefault` /
`StrokeControlDefault@0.55` vs `StageInk.Ink` / `Ink@0.30`; `Skeleton` → `FillSubtleSecondary` vs `StageInk.SkeletonBar`;
`Bloom` → `Tok.TextPrimary` vs (`StageInk.Ink` dark / `StageInk.Veil` light) with `BloomScale` 1.0 dark / **0.5 light**.

---

## 4. Colour & material

**4.1 The rail ink (theme).** `LyricsInk.Theme` resolves at the point of consumption, so a live theme flip repaints in
place. Dark: `TextPrimary` #FFFFFF, `TextSecondary` white @0.772, `TextTertiary` white @0.529. Light: `TextPrimary`
black @0.894 — which is why a "fully lit" interlude dot on the light rail lands a few percent under an opaque white
(noted at `LyricsView.cs:703-705`).

**4.2 The stage ink (polarity).** `StageInk.Live` is the ONE theme branch on the whole stage (`StageInk.cs:17-21`).
Dark arm delegates to `WaveeOnMedia` verbatim (white 1.0 / 0.80 / 0.60, glass ink@0.10/0.16, plate ink@0.14/0.22/0.28,
scrim black 0.55/0.745/0.863, stroke white@0.227). Light arm mirrors the same ALPHAS onto `Tok.MediaStage` (#0A0A0A) ink
over a `PageToneNeutralLight` veil (`StageArm.cs:47-96`). The alphas need no light variant — the sRGB transfer asymmetry
makes the light arm's contrast strictly higher (table at `StageLayout.cs:186-201`).

**4.3 The scrim system (two layers, no boxes).**
`Scrim()` = one vertical gradient over the whole body: `Veil@0.76` at 0 → `Veil@0.46` at 0.22 → `Veil@0.46` at 0.62 →
`Veil@0.70` at 1 (`StageChrome.cs:85-89`, values `StageLayout.cs:217-231`). The two equal interior stops are what make the
middle a plateau and each deepening a feather hundreds of DIP long.
`ColumnShade()` = one left-anchored horizontal gradient, width 352 + 260 = 612: `Veil@0.26` held to stop 352/612 = 0.575,
`Veil@0.0884` (0.26 × 0.34) at 0.809, `Veil@0` at 1 (`StageChrome.cs:95-100`, `StageLayout.cs:234-252`). It is a PAINT
layer bound to `stage.Wide` — 0 wide in the compact shape — and costs the pane region nothing.
The queue pane brings its own `PaneShade()` (0 → 0.096 at 0.22 → 0.24 at the window edge); the LYRICS pane brings none.

**4.4 The backdrop.** `track.Image.Url` → `ImageSource.Normalize` → `Ui.Image(ImageFit.Cover, decodePx 512,
placeholder StageInk.ArtStandIn(url), blurHash, ImageTransition.Fade(220 ms))` with
`BakedBlur = BakedBlurSpec(80, 0.5)` — the blur is baked ONCE per art change into a derived image, so every drift frame
is a pure transform write (`ImmersiveLyricsSurface.cs:62-65,420-435`). No art ⇒ a flat `StageInk.ArtStandIn(null)` box.
Oversize is a **paint** scale of 1.30 about the body's centre, declared and viewport-bound
(`Affine2D(1.30,0,0,1.30, cx(1−1.30), cy(1−1.30))`, `:462-467`); the drift carrier under it declares NO transform so the
per-frame write is unopposed (`:470-476`).

**4.5 On-media ink for the lyrics themselves.** Sung glyphs are `ink.Primary`; unsung are the same colour at
`UnsungAlpha 0.58` (`:2744`, `:2961`). The BLOOM is not the ink: on a dark stage it is the ink (white on black adds
luminance), on a light stage it is the VEIL at half weight, because blurred near-black under near-black subtracts
luminance and reads as a smudge (`LyricsInk.cs:71-90`).

**4.6 Transitions.** Theme flip → `Tok.Epoch` → `RethemeAll` → every mounted render re-runs and every fill/text diff
cross-fades (engine). Track change → the `Key`ed doc host remounts, the backdrop image cross-fades 220 ms, the drift
origin is NOT reset (it only resets when the drift is turned off, `:539-549`). Pane flip (Lyrics ⇄ Queue) → opacity
cross-fade `MotionTok.ControlNormal` (250 ms, FluentStandard), both panes stay mounted, `HitTestVisible` follows the
active one (`StagePanes.cs:57-74`).

---

## 5. Motion

Every wall stamp in this surface comes from `FrameTime.NowMs/NowQpc` (= `FrameClock.PresentQpc`, `FrameTime.cs:22`) or
from `Stopwatch.GetTimestamp()` (QPC). `Environment.TickCount64` appears **nowhere**. The two exceptions to "one step per
produced frame" are documented in the table (`NpvLyricsPeek`'s 100 ms interval and the stage's 33 ms drift interval).

| trigger | target | property | from → to | duration | easing / dynamics | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| active line changes | scroll viewport | `OffsetY` | old → `item.Y + reserve + (h−reserve)/2 − viewportH·band`, clamped | 0 (instant latch) | — | — | same | `:2422-2445,2486-2490` |
| active line changes | every line's `dofContent` | `LocalTransform.Dy` | `+delta` → 0 (ADDED, never assigned) | all ranks converge at **0.48 s** | ζ=1 closed form, rate `y = 7 / (0.48 − delay)` | 60 ms × min(i − (active−1), 4) | **delays all 0** ⇒ one rigid translate | `:1713-1789,1796-1837` |
| active line changes | row host | `ScaleX/ScaleY` | 0.98 ⇄ 1.0 | spring (ω=7.07 rad/s, response 0.889 s) | `SpringParams(100, 2√200, 2)` ζ=1 | — | **flat 1.0** (folded into the spring DepKey) | `:2689,2705,2711-2712` |
| active line changes | row host | `Opacity` | ladder rung ⇄ rung | spring response 0.18 s | `FromResponse(0.18, 1.0)` ζ=1 | — | unchanged (information) | `:2692,2709-2710` |
| active line changes (line-synced) | main `TextEl` | `Color` | `Secondary` ⇄ `Primary` | 167 ms (`WaveeMotion.Fast`) | BrushFade | — | keeps the fade | `:2808-2811` |
| voice line changes | outgoing glow wrapper | `Opacity` | live α → 0 | 240 ms (`GlowFadeMs`) | `EaseOutSine` | — | unchanged | `:123,2282-2306` |
| voice line (word-by-word) | live glow wrapper | `Opacity` | 0 → `0.75 · min(swell, syllable melt, LINE melt)` | swell `min(500, dur/2)`, syllable melt 320 ms into the NOTE's end, line melt 320 ms into the LINE's end | `EaseOutSine`, monotone min; **two** melts — `alphaOut` is computed against the line end and mins with the held-syllable envelope, so a held note at the end of a line dies on the line's clock | only syllables ≥ 700 ms | unchanged | `:127-135,2309-2357` (the line-end min at `:2313-2315,2324`) |
| voice line (line-synced) | live glow wrapper | `Opacity` | 0 → `min(in, out)` | in 240 ms, out 320 ms | `EaseOutSine` | — | unchanged | `:2329-2330` |
| every frame | voice row main + glow runs | `GlyphWipe.Split` | 0 → 1 | the line's own syllable timings, on the TRUE media clock | linear within a syllable, char-length-weighted across them; +0.02 lead; quantised to a device pixel | — | unchanged (information) | `:2201-2243,2988-3004` |
| every frame | both wipe layers | `GlyphWipe.Lift` | glyph sits 1.25 / 1.75 DIP low → 0 as the feather passes | — | — | — | **0** | `:2975-2986` |
| emphasis / suppression / realization | `dofContent` | `BlurSigma` | ladder rung | INCREASE: instant. DECREASE: τ = 65 ms (95 % in 200 ms) | `c += (target−c)(1 − e^(−dt/65))`, land at ≤0.01 | — | unchanged | `:1609-1676` |
| blur-strength slider | every line | `BlurSigma` | old × k → new × k | ONE pass (σ array refilled with NaN ⇒ adopt) | — | — | unchanged | `:350-373` |
| blur strength driven to **0** | every line | `BlurSigma` | rung → 0 | INSTANT, one pass, and the ramp then QUIESCES (`_dofRampPending = false`) | a dedicated `_dofScale <= 0` short-circuit — NOT the ordinary 65 ms decrease: the effect was turned off, it is not an incoming line sharpening | — | unchanged | `:1631-1645` |
| follow detaches / resyncs | every line | `BlurSigma` | rung → 0 | τ = 65 ms ease (decrease) | — | — | unchanged | `:1576,1601-1607,1917` |
| follow re-attaches | every line | `BlurSigma` | 0 → rung | instant (increase snaps) | — | — | unchanged | `:1656-1658` |
| resync (pill tap or 4 s idle) | scroll viewport | `OffsetY` | current → target | ~0.5 s | kernel Driven chase, ζ=1, half-life **110 ms**, settle 4 DIP/s | velocity-continuous on re-target | unchanged | `:2461-2467` |
| detach | resync pill | mount | Dy 4 + Opacity 0 → rest | spring 0.30 s / ζ 0.85 (`TransitionDynamics.Default`) | `LayoutTransition.Fade` | — | engine snaps the terminal, keeps the fade | `:537-539` |
| detach idle | ring | `Value` | 1 → 0 | 4000 ms | linear, quantised to 120 steps | — | unchanged | `:136-137,1946-1962` |
| interlude begins | dots row | mount | Sx/Sy 0.72 + Opacity 0 → rest | spring 0.30 s / 0.85 | `LayoutTransition.Fade` | — | engine snaps scale, keeps fade | `:679-681` |
| interlude, per frame | dots row inner | `LocalTransform` scale | 1 − 0.06(1−pulse) | sine period **2600 ms** on the MEDIA clock | `0.5 + 0.5·sin(2π·t/2600)` | — | **pulse pinned to 1** | `:590-592,771-776` |
| interlude, per frame | each dot | `Opacity` | 0.58 → 1.0 over its third of the gap, × breath α (0.10 depth) | gap − 1000 ms | linear per third | — | fill KEEPS running | `:765-783` |
| interlude, wrapped anchor | dots row inner | `LocalTransform.Dy` | 0 → −max(0, H/2 + air − lift) | per frame, gated 0.05 DIP | — | — | unchanged | `:795-814` |
| interlude reserve arm/release | anchor row | `Padding.Top` | +0 ⇄ +25 / +32 | absorbed by the next latch + cascade over 4 frames | — | — | unchanged | `:823-839,2153-2158` |
| immersive open | surface root | `Opacity` + `ScaleX/Y` | 0 + 1.03 → 1 | spring 0.30 s / 0.85 | Enter terminal | — | **fade only** (no scale) | `ImmersiveLyricsSurface.cs:98-105`, `WaveeShell.cs:1452-1453` |
| immersive close | surface root | `Opacity` + `ScaleX/Y` | 1 → 0 + 1.02 | same | Exit terminal | — | fade only | same |
| stage backdrop, 33 ms tick | drift carrier | `LocalTransform` | ±4 % of body per axis; scale 1 ± 0.02 | sinusoids **37 s** and **53 s** (incommensurate) | `dx = 0.04·w·sin(2πt/37)/1.30`, `s = 1 + 0.01(sinB − sinA)`; write gates 0.15 DIP / 0.0004 scale | — | **no ticker at all** (setting OR OS flag OR **no art**) | `ImmersiveLyricsSurface.cs:70-82,139,162-165,503-537` |
| drift turned off mid-flight | drift carrier | `LocalTransform` | current → `Identity` | instant, one write | the declared parent transform is what re-centres it; `_driftOriginQpc` is reset so a re-enable starts at t = 0 with no jump | — | this IS the reduced-motion path | `:162,539-549` |
| cover changes | backdrop image | cross-fade | old → new | 220 ms | `ImageTransition.Fade` | — | engine policy | `:425` |
| pane switch | lyrics / queue panes | `Opacity` | 0 ⇄ 1 | 250 ms | `MotionTok.ControlNormal` (FluentStandard) | — | token's own policy | `StagePanes.cs:61,69` |
| pane switch | the two pivot links | underline `Fill` + label `Color` | `accent` ⇄ transparent, `Ink` ⇄ `InkTertiary` | 167 ms (`WaveeMotion.Fast`) | BrushFade — the underline never appears/vanishes in one frame, and it is a COLOUR change, never a flown/animated bar | — | keeps the fade | `StageChrome.cs:340-351` |
| document ready | content root | `Opacity` | 0 → 1 | 250 ms (`Expressive.Fast`) | `Easing.SmoothOut`; shimmer exits over the same 250 ms as an orphan underneath | — | snaps | `:867-876`, `SkeletonRegion.cs:166-168` |
| NPV peek line change | the two slots | `Dy` + `Opacity` | ±56 / 0→1 | 150 ms | `MotionTok.ControlFast` | — | token policy | `NpvLyricsPeek.cs:24-25,155-156` |
| hover / press (header buttons, FABs) | fill + scale | `HoverFill`/`PressedFill`, `Hover/PressScale` | per tier | 83 ms brush (`WaveeMotion.Faster`) | `ScaleEmphatic` 1.07/0.92 (both FABs); the rail header buttons take `Interaction.Subtle` | — | tiers return 1.0 | `StageChrome.cs:132,171-172,214-215`, `RightRail.cs:447` |
| hover / press (**resync pill**) | fill + scale | `HoverFill`/`PressedFill`, **`PressScale` only** | rest ⇄ 0.98 | engine-default brush (**no explicit `BrushTransitionMs`**) | `WaveeMotion.ScaleSubtle.Press` = 0.98 — there is **no `HoverScale`**: the pill grows on press and never on hover | — | tier returns 1.0 | `:533-534` |
| upgrade arrives while PLAYING with a live active line | the whole document | swap | old doc → upgrade | **deferred** to the next active-line change | none — `_pendingUpgrade` is applied inside the handoff frame, before `activeChanged` is spent, and `OnFrame` returns | — | unchanged | `:1253-1257`, `:2124-2128` |
| upgrade arrives paused / with no doc / before the first line | the whole document | swap | old → upgrade | immediate | — | — | unchanged | `:1254-1255` |
| ticker gap (minimize, parked window) | cascade + σ ramp | dt | — | clamped to **100 ms** (`CascadeDtMaxMs`; the σ ramp clamps 0..100 too) | a resumed window catches up over a few frames instead of teleporting | — | unchanged | `:1724`, `:1622`, `:1800-1802` |
| handoff (lines far from the new active) | rows outside ±**24** of it (`CascadeWriteBand`) | `LocalTransform.Dy` | retired to 0 in ONE identity write at arm time | instant | they SNAP with the latch instead of easing — the band covers any viewport up to ~1800 DIP | — | unchanged | `:1725-1739,1761-1770` |
| voice handoff with a third halo still fading | the stale outgoing glow | `Opacity` | live α → 0 | instant (`FinishGlowOut`) | **at most two halos ever animate** | — | unchanged | `:2284` |

**Landing gates (how a motion ENDS, not how it runs).** The cascade lands a line EXACTLY — `comp = 0`, `vel = 0`, one
identity write — only when `|comp| < 0.5 DIP` **and** `|vel| < 20 DIP/s` (`CascadeLandDip` / `CascadeLandVel`,
`:1721-1722`, tested at `:1830-1831`). The velocity half is what stops a line that is merely passing through zero
mid-flight from being snapped down; the landing write is exempt from the 0.1 DIP write gate (it compares at 0.0005) so
the node ends on the exact value (`:1867-1868`). The σ ramp has the same shape: it lands when `c − target ≤ 0.01` and
that landing write is exempt from the 0.5 σ gate (compared at 0.001, `:1663,1671`). The interlude dots' own transform
gates are **0.002 on the breath scale and 0.05 DIP on the lift, compared TOGETHER** because both live in one matrix
(`:810`), and the dot alphas at 0.004 (≈1/255, `:593`).

**Frame gating.** `LyricsFrameStepper` subscribes `FrameClock.Tick` and calls `OnFrame` once per produced frame; it is
MOUNTED only while `playing || cascadeRunning || followMode != Following` (`:3128-3131`). Paused + following + settled =
completely quiescent, woken only by a `PositionMs` change through `LyricsTicker`'s effect (`:3103-3116`). During a main
content scroll (`Context.PeekMainScrollBusy`) the GLOW lane is deferred but the wipe and the whole core lane are not
(`:2130-2136`).

---

## 6. Interaction

**Click a lyric line** → `SeekToLine(index)`: reset follow state (back to Following), `b.CommitSeek(line.StartMs)`,
`RebaseClock`, `_scrollSnapped = false`, `ZeroCascade`, reset the wipe throttle (`:1992-2003`). Every row is
`Role = AutomationRole.Button`, `Focusable = true`, `AllowFocusOnInteraction = false`, `Cursor = Hand` (`:2868-2872`).
The row's automation NAME is its text content (the engine derives names from text).

**Hover on a lyric row**: nothing. There is deliberately no hover fill, no scale and no cursor change beyond `Hand` —
the reading surface must not shimmer under a travelling pointer.

**Wheel / touch drag in the list** → `OnScrollGeometryChanged` fires with `UserScrollActive`: cancel the resync deadline,
`ZeroCascade`, mode → `DetachedActive`; on release → `DetachedIdle` with a `wall + 4000 ms` deadline (`:1376-1378`,
`:1929-1944`). In both detached modes the DoF ladder eases to 0 and the dots + reserve are retired (`:1917`).

**Resync pill**: `Role Button`, focusable, `OnClick → BeginResync`; the countdown reaching 0 does the same thing
(`:535`, `:1954-1958`). After the programmatic chase lands, the mode returns to Following and the ladder snaps back.

**Rail header** (`RightRail.cs:373-401`), left to right: title · 🌐 secondary toggle (only when
`LyricsPrefs.Available != 0`; tooltip = the CURRENT state: `player.lyricsSecondaryOff|Translation|Romanization`;
cycles none → translation → romanization → none, SKIPPING layers the document lacks — `LyricsPrefs.Next`, `:3044-3054`;
the LATCHED arm keeps the accent on HOVER too — `HoverColor = active ? AccentTextPrimary : TextPrimary`, so a lit
toggle does not brighten under the pointer, `RightRail.cs:429-431`) ·
`</>` inspector (only when `DeveloperMode.Enabled`; tooltip `player.inspectLyrics`) · ⛶ expand
(tooltip `player.expandLyrics`; sets `ShellUi.ImmersiveLyrics = true`) · ✕ close (closes the RAIL; it is the ONE header
button with **no tooltip** — `RightRail.cs:456-464`). The globe's ACTIVE (accent) arm is
`(available & BitFor(mode)) != 0` — "a second line is actually on screen", never "the mode is non-zero", so a persisted
romanization preference over a translation-only document leaves the glyph unlit (`RightRail.cs:394-397`).

**When `Available` goes to 0** (and the toggle therefore disappears mid-session): a document with neither layer, a
document that is **not timed at all** — `PublishSecondaryAvailability` gates on `IsTimed`, so an UNSYNCED document's
translations are never offered, because `UnsyncedLyricsContent` has no rows to render them in (`:1145-1155`) — and
`ClearDocument`, which retires the capability BEFORE its own early-out so the button cannot linger over the next
track's lyrics-less state (`:1199-1203`).

**Immersive surface**: `Escape` closes it (`OnKeyDown`, `:186-190`); the root takes focus at mount and RE-PARKS focus on
itself whenever focus would go null, because an unhandled Escape clears focus and would disarm the keyboard exit
(`:155-158,191-205`). A childless `Shield` layer absorbs clicks anywhere the stage's own content is not, so a click on the
scrim can never reach the page underneath (`:254-292`). Top-bar controls: 🌐 (same cycle as the rail, shown only while
the LYRICS pane is up) and the chevron exit (tooltip `player.closeLyricsHint` = "Close lyrics (Esc)" — the tooltip is
where the keyboard shortcut is taught). Pivot links `Lyrics · Queue` are `Role = Tab`.

**Player bar**: the lyrics button (`WaveeIcons.Lyrics`) toggles the rail; in the overflow it is an
`AppBarCommandKind.ToggleButton` reporting `RailOpen && Mode == Lyrics` (`PlayerBar.cs:455-462,499-505`).

**NPV peek**: the whole two-row reel is one button (`Role Button`, `Cursor Hand`) → opens the lyrics rail
(`NpvLyricsPeek.cs:80-83,133`).

**Inspector dialog** — FOUR success toasts, all `InfoBarSeverity.Success`, all English literals in code:
"Lyrics report copied" (`:138`), "Lyrics evidence bundle saved" (`:180`), "`<sourceId>` payload copied" (`:386`) and
"Parsed lyrics copied" (`:463`). `Copy full report` (clipboard + toast), `Save bundle…` (chains a
re-fetch when nothing is cached, then writes a folder and opens it), `Refresh` (bumps the read epoch), `Re-fetch from
providers` (accent button; drops the memory AND disk cache and re-runs the fan-out). Tabs:
`Providers | Raw response | Parsed` via `SelectorBar`. Per-provider cards carry `Raw (n)` / `Parsed` buttons that switch
tab AND focus — each is DISABLED when there is nothing behind it and relabels `Raw (none)` / `Parsed (none)`
(`:299-302`). Parsed tab has `Show/Hide syllables` (a label that flips with state) and `Copy parsed`; its chips are
`Final (what the UI got)` + one per candidate, and `Raw`'s chips are `source (n)`, both `Wrap 6`. While a re-fetch is in
flight `Save bundle…` reads `Working…`, `Re-fetch from providers` reads `Re-fetching…`, and BOTH are disabled
(`:143-146`); a Warn-amber status line appears under the caption and is cleared on success (`:151-152`, `:194-210`).
`Save bundle…` CHAINS a re-fetch when nothing is cached rather than writing an evidence-free folder (`:157-165`), then
opens the folder. Close via the dialog's own Close button (`DefaultButton = Close`, no primary command).
**No right-click menus anywhere in this surface. No drag-and-drop. No inline edit. No selection model.**
**No keyboard shortcut anywhere except Escape on the immersive stage** — the rail panel has none, the pivot has none,
and lyric rows are reachable only by Tab.

**Localisation.** Used loc keys: `player.lyrics`, `player.resyncLyrics`, `player.lyricsSyncUnavailableDuringVideo`,
`player.expandLyrics`, `player.closeLyricsHint`, `player.inspectLyrics`, `player.lyricsInspector`,
`player.lyricsSecondaryOff|Translation|Romanization`, `player.queue`, `common.close`,
`settings.appearance.lyricsSecondary|lyricsBackdrop|lyricsBlur|lyricsBlurAuto`, `settings.lyrics.title|subtitle`.
**Not localised (English literals in code, a 0.3 defect to fix):** `"Nothing playing"` (`:432`),
`"No lyrics available"` (three call sites — `content`, `onFailed`, `onEmpty`: `:870`, `:872`, `:874`), every string in
the inspector dialog and the debug overlay — even though `lyrics.inspector.*` keys already exist in
`assets/loc/en-US.json:2533-2541` (`reportCopied`, `evidenceSaved`, `parsedCopied`, `refetchNote`, `raw`, `rawNone`,
`payloadsNote`) and are referenced by nothing. `player.closeLyrics` ("Close lyrics") also exists and is unused by this
surface — the stage's exit takes `closeLyricsHint` instead.

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read (proposed) | readiness predicate |
|---|---|---|---|
| the document (lines, order) | `Services.Lyrics.GetLyricsAsync(trackId)` → `LyricsDocument` (`Wavee.Core/Playback/Playback.cs:251-257`); the `UseResource` key is **`trackId + "\|" + artist`** (`:855`) while the host's remount `Key` is the track id alone (`:441`) — a metadata refresh that changes the first artist re-runs the fetch WITHOUT remounting the rows | `Lyrics.Doc(track)` → `LyricsDoc` from the Lyrics side table (§DATA GAPS 1) | `track.Knows(TrackFields.Lyrics)` — shimmer until then, never a partial document |
| the SAME document, for the NPV peek | a SECOND `UseResource` → `GetLyricsAsync(trackId)`, keyed on the bare track id (`NpvLyricsPeek.cs:52-56`) — an independent fetch from the view's, deduplicated only by the provider's own cache | the one `Lyrics.Doc(track)` read; the peek plans NO fetch of its own | `track.Knows(TrackFields.Lyrics)`; before that the peek is ABSENT, not a skeleton |
| per-line text | `LyricLine.Text` | `doc.Lines[i].Text` — a **`string`**, NOT a `StringId`, for any line that can carry a wipe (§9 (c)): the feather is a fraction of `GetRangeRects(text, …)` measured over that same instance with a `TextStyle` mirroring `LineText` (`:1554-1570`, `:2876-2889`). `StringId` is fine for the static strings (messages, banner, pill, pivot labels) | same bit |
| syllables (karaoke) | `LyricLine.Syllables` + `IsWordByWord` | `doc.Lines[i].Syllables` + flag | `doc.Sync == Syllable`; a line-synced doc renders with no wipe and is NOT a broken state |
| translation / romanization | `LyricLine.Translation/Romanization` | same fields | `doc.HasTranslation/HasRomanization` — computed at COMMIT, not scanned by the UI |
| sync kind / provider / offset | `LyricsDocument.Sync/Provider/OffsetMsApplied` | `doc.Sync/ProviderId/OffsetMs` | same bit |
| the "richer answer arrived" upgrade | `IUpgradingLyricsProvider.LyricsUpgraded` observable, filtered by `IsRicherLyrics` (`:1267-1294`) and then **held in `_pendingUpgrade` until the next active-line change** while playing (`:1253-1257`) | a `Signal<uint>` version on the Lyrics table + `LyricsAuthority` (Unsynced 1 < Line 2 < Syllable 3; a doc with ANY word-by-word line scores 3 regardless of `Sync`; equal rank ⇒ more syllables wins) | authority-gated commit; the UI re-reads on the version bump — **and the swap must still land on a handoff frame, not mid-line** |
| now-playing identity (fetch key) | `PlaybackBridge.Identity.Value.Track` (id + first artist) | `Playback.Current` → `Track` handle; `t.Knows(TrackFields.Identity)` | identity known before a fetch is planned |
| position for the wipe | `PlaybackBridge.LastPositionSample` = `(positionMs, sampleQpc)` (`PlaybackBridge.cs:239`) | `Playback.LastSample` signal — **must exist**; a bare `PositionMs` cannot drive this surface | always |
| playing / paused | `PlaybackBridge.IsPlaying` | `Playback.IsPlaying` | always |
| video suppression | `PlacementCore.IsActive(b.VideoSurface)` → `LyricsSyncGate` | `Playback.VideoActive` (derived on the model) | always |
| backdrop art + blurhash | `track.Image.Url` / `.BlurHash` | `t.ImageId`; **BlurHash is a gap** | `t.Knows(TrackFields.Image)`; the stand-in paints meanwhile |
| stage accent (4 jobs) | `Surfaces.ChromeSchemeFor(url)` → `WaveePalette.ChromeAccent` | cover palette — **gap**, see `00-design-system.md` | palette known ⇒ accent; else `Tok.AccentDefault` |
| secondary-line mode / blur strength / backdrop drift | `WaveeSettings.LyricsSecondaryLine` (0), `LyricsBlurStrength` (−1 auto), `LyricsAnimatedBackdrop` (true) (`Platform/AppSettings.cs:123,131,136`) | same three keys, read under `Lyrics.Prefs.Epoch` | always |
| GPU tier for Auto blur | `GpuProfile.IsWeak` (engine) | unchanged | always |
| inspector data | `LyricsDiagnostics.ForTrack/InspectionFor` statics | `Lyrics.Diagnostics` static store in `Lyrics.Host.cs` | developer surface; may be absent |

**DATA GAPS** (the plan's §4 model holds none of this):

1. **The lyrics document itself.** There is no column, edge or table for it. It is a managed object graph (lines with
   syllable lists and two optional strings), so it cannot be a `Column<T> where T : unmanaged`.
   *Proposal:* a per-scope side table `Lyrics { Dictionary<int /*track slot*/, LyricsDoc> Docs; Column<byte> Authority;
   Signal<uint> Changed; }` plus `TrackFields.Lyrics = 1 << 17` (and `All` widened from `0x1FFFF` to `0x3FFFF`), fetched
   through the ordinary `Fetch.Plan` path with its own provider switch. Source today: `Backend/Lyrics/AggregatingLyricsProvider.cs`
   (fan-out + rerank) over `Sources/*` and `LyricsDiskCache.cs`.
2. **`LastPositionSample` (position + QPC instant).** `Playback.Host.cs` in the plan lists "signals, Pending, ticker" but
   not this pair. Without it the media clock degrades to the 1 Hz snapshot and the karaoke steps. *Proposal:*
   `Playback.LastSample : Signal<(long PositionMs, long SampleQpc)>`, written from the same site as `PositionMs`.
3. **`VideoActive`.** A derived read of the one placement state, not a standalone flag (`LyricsSyncGate.cs:23-24`).
   *Proposal:* `Playback.VideoActive` computed in the reducer.
4. **Cover palette / chrome accent.** The stage spends one art-derived colour on four jobs. Gap in the plan's Track
   columns. *Proposal:* `Column<uint> PaletteChrome, PaletteText` filled at image-decode time (chapter 00 owns the
   derivation).
5. **BlurHash.** Used as the backdrop's decode-time placeholder. *Proposal:* `Column<StringId> BlurHash` in the hot image
   group.
6. **Secondary-layer availability.** 0.2.9 scans the document once per load (`:1129-1143`) and publishes
   `LyricsPrefs.Available`. Per CLAUDE.md ("derived facts live on the model") this must be two flags computed at commit
   time on `LyricsDoc`, not a UI scan.
7. **Diagnostics / inspection store** (per-source traces, raw payloads, parsed candidates, notes) — SHELL-side statics
   with caps: **128 k chars per payload, 6 payloads per source, 640 k per probe** (`LyricsDiagnostics.cs:109-111`) and,
   on the store itself, **24 recent probes, 256 distinct tracks (FIFO-evicted), 3 tracks' worth of INSPECTIONS**
   (`:198-203,222-223,249`). Not entity data; keep as a bounded static in `Lyrics.Host.cs`. The per-track report is
   published on EVERY search — in 0.2.9 the env gate only hid the PANEL, and in 0.3 deleting that panel (§9 (a)) changes
   nothing here — so the caps are load-bearing, not belt and braces. The one reader is the inspector, which in 0.3 lives
   in `Screens/Diagnostics.UI.cs` and reaches this store across the seam (§9 (d)).

---

## 8. Pure rules to port verbatim

| class | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `LyricsFx` | `Features/Player/LyricsView.cs:32-58` | the two σ ladders (rail / stage) by ring distance | *(none — UNVERIFIED: no test file)* | CORE section of `Shell/Lyrics.cs` |
| `LyricsBlurPolicy` | `Features/Player/LyricsBlurPolicy.cs` | auto-tier resolution (weak 40 / strong 100), 0..100 → 0..1 scale, `Enabled` | `src/apps/Wavee.Tests/LyricsBlurPolicyTests.cs` (7) — including `Enabled_IsFalseOnlyAtZero` | CORE of `Shell/Lyrics.cs`. **`Enabled` has NO production caller**: `LyricsView` gates on `_dofScale <= 0` (`:1631`) and Settings never asks. Port `Resolve`/`Scale`; either give `Enabled` the one call site it was written for or drop it — do not carry a tested rule nobody consults |
| `LyricsSyncGate` | `App/LyricsSyncGate.cs` | whether timed sync is suppressed | `Wavee.Tests/LyricsSyncGateTests.cs` (3) | CORE of `Shell/Lyrics.cs` |
| `LyricsRowShape` | `App/LyricsRowShape.cs` | may a document swap KEEP the mounted rows (text + word timing + both secondary layers) | `Wavee.Tests/LyricsRowShapeTests.cs` (6) | CORE of `Shell/Lyrics.cs` |
| `LyricsMediaClock` | `Features/Player/LyricsMediaClock.cs` | QPC → media-ms mapping; snap ≥250 ms, ≤5 % slew, converge ~1 s, pin while paused, monotone while playing, diagnostics | `Wavee.Tests/LyricsMediaClockTests.cs` (8) | CORE of `Shell/Lyrics.cs` |
| `LyricsPeekClock` | `Backend/Lyrics/LyricsPeekClock.cs` | `ShouldShow` (Lines > 0 AND Sync ∈ {Line, Syllable}), the binary search, and the reel's (active, peek) with the same 140 ms lead — including the before-first (−1, 0) and after-last (n−1, −1) rules | `Wavee.Tests/Lyrics/NpvLyricsPeekTests.cs` (7) | CORE of `Shell/Lyrics.cs` |
| `IsRicherLyrics` / `Richness` / `SyllableCount` | `Features/Player/LyricsView.cs:1267-1294` | whether an upgrade is worth taking: any word-by-word line ⇒ 3, else `Sync` (Syllable 3 / Line 2 / Unsynced 1 / None 0); an equal rank is broken by total syllable COUNT, and a lower rank is refused | *(none — UNVERIFIED)* | CORE of `Shell/Lyrics.cs` — **add tests**; this is §7's `LyricsAuthority` and it must not be re-derived |
| `LyricsPrefs` | `Features/Player/LyricsView.cs:3020-3072` | clamp, capability bit, the skip-missing-layer cycle, tooltip, the one writer | *(none — UNVERIFIED)* | CORE of `Shell/Lyrics.cs` (the two Signals are the only non-pure part) |
| `LyricsView.PackEmphasis` / `LyricLineView.OpacityOf` | `:1174-1181` / `:2936-2943` | the packed bucket/past/reserve word and the two opacity ladders | *(none — UNVERIFIED)* | CORE of `Shell/Lyrics.cs` — **add tests** |
| `ResolveLine` / `SungOutMs` / `AdvancePastInterlude` | `:2507-2518` / `:718-723` / `:739-753` | the binary search, the sung-out point, and the interlude advance + gap bounds | *(none — UNVERIFIED; plan §5 Wave 4 names "Lyrics.ResolveLine (ONE copy)")* | CORE of `Shell/Lyrics.cs` |
| `ComputeSplit` / `HeldSyllableGlow` / `EaseOutSine` | `:2988-3004` / `:2341-2357` / `:2359` | char-weighted karaoke fraction; the ≥700 ms held-note envelope | *(none — UNVERIFIED)* | CORE of `Shell/Lyrics.cs` |
| cascade math (`ArmCascade`/`DriveCascade` bodies) | `:1743-1837` | rank → delay → rate; the ζ=1 closed form; the j1 sign clamp; the landing gates; the write band | *(none — UNVERIFIED)* | CORE of `Shell/Lyrics.cs` as `Cascade.Arm/Step(Span<float> comp, vel, delay, rate, …)` |
| `SoftnessOfLine` clamp + `WipeSoftnessDipFor` | `:1512-1517`, `:2963-2973` | DIP feather → per-line fraction, clamped | *(none — UNVERIFIED)* | CORE of `Shell/Lyrics.cs` (the MEASUREMENT stays in UI) |
| `LyricsMeasuredLayout` | `:2536-2586` | top/bottom focal pad, content extent, window, item rect over an `ExtentTable` | *(none — UNVERIFIED)* | `Shell/Lyrics.UI.cs` (implements engine interfaces) |
| `StageLayout` | `Features/Player/StageLayout.cs` | wide/compact + fold ladder + scrim alphas | `Wavee.Tests/StageLayoutTests.cs` (29) | chapter 21's file; the lyrics surface only READS it |
| document pipeline | `Backend/Lyrics/{LyricsClean,LyricsText,LyricsTiming,LyricsWordFormats,LyricsCreditRules,LyricsReranker,LyricsQuery}.cs` + `AggregatingLyricsProvider`, `LyricsDiskCache`, `LyricsInspectionExport`, `Lyricify/{DESHelper,LyricCrypto}`, `Sources/{AmllTtmlDb,GreySources,LrcLib,SpotifyNativeLyrics}` — **19 files, 4,504 lines** | normalisation, credit stripping, timing anomalies, rerank, fan-out, disk cache, payload decryption | `Wavee.Tests/Lyrics/{LyricsCoreTests(33),LyricsCleanTests,LyricsWordFormatTests,LyricsQueryTests,LyricsAggregationTests,LyricsInspectionExportTests,LyricsDiskCacheTests,MusixmatchRichsyncFixtureTests}` (+ `Lyrics/Fixtures`) | CORE — needs its own file (§9 tree gap) |

---

## 9. Re-author notes

**Must not be simplified.**

* The **two indices**. Collapsing `_activeLine` and `_voiceLine` into one kills the karaoke fill on the line you are
  hearing (`:2101-2111`).
* The **latch + per-line cascade**. A single viewport spring is what 0.2.9 replaced: it re-chases from standstill in
  dense sections and never reaches the band. The ADD (not assign) on `comp[i]` is what makes a mid-flight re-target
  velocity-continuous, and the `j1` sign clamp is what guarantees zero overshoot (`:1772-1783`).
* The **directional σ model**. Increase snaps, decrease eases. Symmetric easing reads as the outgoing line refusing to
  leave.
* **Per-line emphasis signals.** A shared memo is not equivalent: it re-renders every realized row on every boundary
  (`:157-168`).
* The **same-shape upgrade path**. Reallocating the per-line signal arrays while rows stay mounted froze whole tracks at
  one emphasis; `LyricsRowShape.SameRows` + `_docEpoch` is the fix and both halves are required (`:278-291`, `:1009-1081`).
* **Realize the whole document** (`overscan: min(n, 400)`, `RealizeOverscanImmediately`). A 4-5 row overscan pops text
  shaping and blur layers in as the document travels (`:1363-1371`).
* The **glow rule**: a halo σ may never nest inside a DoF σ (`BlurPinKey` refuses a nested `PushLayer`), so the halo is
  suppressed whenever the row's own σ > 0.01 (`:2261-2271`) and the line-synced halo only ever paints at dist 0 (`:2795-2800`).
* **Mark `TransformDirty` only** for the cascade write. Adding `PaintDirty` re-records every in-flight row's glyph runs
  for the whole 0.48 s settle (`:1847-1852`).
* The **reserve is PAD, not MARGIN** — the measured-virtual seam feeds the extent table from the row's border box
  (`:601-606`) — and the follow centres the CONTENT box so the sung line does not drift when the band arms (`:2416-2422`).
* The **deferred upgrade.** A richer document that lands mid-line is HELD (`_pendingUpgrade`) and applied on the next
  active-line change, inside that handoff, with `OnFrame` returning immediately afterwards (`:1253-1257`, `:2124-2128`).
  Applying it the moment it arrives re-keys every row under the reader's eyes mid-sentence; this is what parity item 46
  ("does NOT jump or re-scroll") is actually testing. Paused / no document / before the first line ⇒ apply at once.
* The **two-halo cap.** `BeginGlowFades` finishes a third in-flight out-fade instantly, so at most two glow layers are
  ever animating (`:2284`). A general fade pool would put a blurred layer on every recently-sung row.
* The **cascade write band** (±24 rows) and the **100 ms dt clamp**. Both are bounds on cost that also make the thing
  correct: retiring out-of-band lines is what stops a line that leaves the band mid-flight keeping a stale transform
  forever, and the clamp is what stops a restored minimized window spending a 10-second gap as one integration step
  (`:1725-1739`, `:1724`, `:1800-1802`).
* The **always-on `lyrics.clock` log**, once every 30 s of wall time while a timed document is playing:
  `frames / zeroAdvanceFrames / maxStepMs / slewMs / snaps` from `LyricsMediaClock.ReadAndResetDiagnostics`
  (`:2081-2099`). It is not env-gated and must not become so — it is the only evidence that the karaoke-stepping bug
  has not returned.

**Traps.**

* Props freeze at mount: every live value in §1.2's table must stay a Signal. `Motion.ReducedMotion` must be read as a
  VALUE at each consumption point (four of them, `:1697-1707`) — never an early return, or the hook order shifts when a
  resize grip flips the OS flag.
* `ReuseGuard` will flag a row that re-mounts on a table publish. Lyric rows must NOT be rebuilt by anything except a
  document key change; the 0.3 entity-publish path must not touch them.
* Zero-allocation scroll frames vs per-row richness: 0.2.9 reconciles the two by (a) mounting every row once and never
  again, (b) writing only scene columns per frame (no `RequestRerender` anywhere in the driver), (c) gating every write
  (σ 0.5, transform 0.1 DIP, split 0.0008, softness 0.002, dot alpha 0.004), and (d) freezing settled endpoints so the
  host's skip-submit hash elides the frame entirely.
* The wipe's `Softness` is a fraction of the measured reading-order run length, so `MeasureRunLength`'s `TextStyle` must
  mirror `LineText` EXACTLY (`:1558-1562`, `:2876-2889`) — they are changed together or the feather re-scales.
* `Flow.Show` mirrors layout participation but NOT `HitTestPassThrough`: the resync/banner positioners must stay OUTSIDE
  it or a full-viewport hittable node kills scrolling (`:476-479`, `:518-521`).
* **A document is SEEDED at its true opening state, not at a constant.** `PrepareDocument` resolves the active line
  through the SAME `AdvancePastInterlude` the frame lane uses (`:1094`), then mints every per-line emphasis signal at its
  real packed value (`:1105`) — so a document that lands mid-track (or mid-BREAK) opens with the correct ladder and
  `PushEmphasis` is a silent no-op. Seeding "fully dim" fanned the whole document out on every load; an untimed document
  seeds active = −1 (no ladder, no blur, no wipe) (`:1091-1116`).
* **`ClearDocument` also resets the media clock** (`_clock.Reset(0, NowQpc, false)`) and `_lastSampleQpc` (`:1242-1243`),
  and retires `_pendingUpgrade` on BOTH of its exits (`:1208,1212`). A 0.3 port that keeps the clock across a track
  change re-treats the next document's first sample as a >250 ms disagreement.
* **The emphasis array can be transiently short.** `LyricsContent` hands a realized row `_emphasisFallback` — one shared
  `Signal<int>` pinned at bucket 6 — when its index is past `_lineEmphasis.Length` (`:164`, `:1353`). It is a
  resize-gap guard, not a state: a row must never be left holding it.
* **The rail header's ✕ closes the RAIL, not the stage**, and the stage's chevron closes the STAGE, not the rail — the
  rail underneath is left exactly as it was (`RightRail.cs:135-136` mounts it behind a visibility gate, not a branch).

**Where the plan is wrong or too thin.**

* **§2 line budget — and the exact replacement text.** `Lyrics.cs 1,800 + Lyrics.UI.cs 2,000 + Lyrics.Host.cs 1,300 =
  5,100` (plan `wavee-0.3-implementation.md:72`) against ≈9,600 lines of 0.2.9 source that Wave 4 owner K is told to
  port (plan `:744`: "LyricsView, Backend/Lyrics"). The backend alone is 4,504. This chapter does not merely flag the
  gap — it names the partials the honest ≈10,000 implies, so the plan's tree can be edited without a second decision:

  | plan `§2` line | replace with | why |
  |---|---|---|
  | `│   ├── Lyrics.cs Lyrics.UI.cs Lyrics.Host.cs   1,800 + 2,000 + 1,300` | `│   ├── Lyrics.cs Lyrics.UI.cs Lyrics.Host.cs   3,700 + 3,500 + 2,300` | the three partials below (**A12**: no `Lyrics.Stage.UI.cs`) |
  | `├── Shell/   13  ~21,600` | this chapter's contribution is **+4,400** (−5,100, +9,500); it adds **no** file to `Shell/` | the plan's `Shell/` subtotal is recomputed centrally from every chapter's rows (**A17**) — chapter 21's `Stage.cs` + `Stage.UI.cs` and chapter 24's `Video.{cs,UI.cs,Host.cs}` are the new files in that block, not a lyrics one |
  | `│   ├── … Diagnostics.UI.cs …` (`Screens/`, `13  ~13,700`) | `Screens/  13  ~14,300` | the inspector's ≈600 lands in `Diagnostics.UI.cs`, no new file |
  | plan `§5` Wave 4 owner K | **settled: K stays single owner** of all three lyrics files (`Lyrics.cs`, `Lyrics.UI.cs`, `Lyrics.Host.cs`), alongside Rail + Deck + Stage + Video — this chapter's earlier ask to split `Lyrics.cs` CORE + `Lyrics.Host.cs` (≈6,000, parse/rerank/sources/cache) to a second owner was not taken | the lyrics surface is the second-largest port in the wave after the sidebar, but the plan keeps one owner per entity's rule set rather than splitting a CORE/SHELL pair across two people |

  The three partials, named:
  `Shell/Lyrics.cs` **CORE ≈3,700** (ladders, clocks, gates, emphasis, cascade math, prefs, parsers, rerank, timing,
  clean, word formats, credits, query) · `Shell/Lyrics.UI.cs` **≈3,500** (view + row + measured layout + rail header
  slice + NPV peek — ≈2,600 — **plus ≈900**: `ImmersiveLyricsSurface`, the stage's backdrop/scrim host, and the lyrics
  column that mounts inside the stage's pane frame) · `Shell/Lyrics.Host.cs` **SHELL ≈2,300** (sources, aggregator,
  disk cache, diagnostics store, refetch). The inspector's ≈600 is NOT in this budget — see (d).

  **`Shell/Lyrics.Stage.UI.cs` is dropped, and the contested lines are counted exactly once (arbitration 2026-09-12,
  A12).** Its earlier ≈1,400 was "the immersive surface + panes + the stage's lyrics column". The **panes** half
  (≈500) was always chapter 21's `StagePanes` and is already inside that chapter's `Shell/Stage.UI.cs` at 1,150 —
  billing it here too was a double count. What is genuinely this chapter's, the immersive surface and the lyrics
  column (≈900), moves into `Lyrics.UI.cs`. The boundary: **chapter 21 owns the pane FRAME** (the switcher, the pane
  box, the scrim and shade ladders, `StageChrome` / `StageIdentity` / `StageLayout` / `StageArm` / `StageInk`);
  **this chapter owns what is drawn in it** (`Lyrics.View` in its `large`/`onMedia` arm, and the surface that hosts
  the whole stage). Net: this chapter's honest total falls **≈10,000 → ≈9,500**.
* **§4.12 (`Track.Row`) is not the model for a lyric row.** The lyrics list is a MEASURED, variable-height
  `Virtual.Custom` over a `RenderItem` path with per-row component state and per-row scene handles — not
  `ItemsView.CreateBound`. Wave 5's gate wording ("row binding proven by the ReuseGuard") must carve this out.
* **§4.13's "pages demand their whole model on mount" is already how this works** (one `GetLyricsAsync` per track, the
  whole document, no windowing) — keep it, and note that the UPGRADE stream is a second, authority-gated arrival, not a
  second fetch window.
* **§4.1/§4.2 have no place for a managed per-entity document.** The Lyrics side table (§7 gap 1) is a new concept the
  plan must name, or owner K will invent one.
* **Wave 4's gate** ("`--fake` shows the shell frame with an empty content host and a working sidebar") does not exercise
  lyrics at all. Add: a word-synced fake document in `Entities.SeedFake()` plus the advance probe below.
* **§5 owner split.** K owns Rail + Deck + Lyrics; the lyrics surface alone is the second-largest port in the wave after
  the sidebar. Split the lyrics BACKEND (parse/rerank/sources/cache) to its own owner.

**Line budget (honest).** 0.2.9: **≈9,600** (5,090 UI + **4,504** backend, counted 2026-09-12). Plan target: 5,100.
Honest estimate for a faithful port: **≈9,500**, split as the three partials above
(3,700 + 3,500 + 2,300), **plus ≈600 for the inspector inside `Screens/Diagnostics.UI.cs`** (A14, which also moves the
inspector out of the lyrics files for good) — which is why the plan's `Screens/` subtotal moves to ~14,300 and this
chapter contributes **+4,400** to the `Shell/` block without adding a file to it. The stage's own rows in that block
are chapter 21's (`Stage.cs` 500 + `Stage.UI.cs` 1,150) and the video's are chapter 24's; the `Shell/` subtotal is
recomputed centrally (A17). Carry these numbers into the plan's §2 verbatim; they are the whole content of that edit.

**Resolutions (these were the four open questions; none of them is open any more).**

**(a) `WAVEE_LYRICS_DEBUG` is DELETED as an env var; the pill + overlay it gated are KEPT; `WAVEE_LYRICS_ADVANCE_PROBE`
becomes a CLI entry.** CLAUDE.md bans env-var switches for behaviour *and* verification, and this surface has one of
each, so they get different answers. **Restated 2026-09-12 (Christos, plan §9.6 Q1): this chapter's own proposal to
delete the pill + overlay outright is superseded — kept, gate changed, not deleted.**

* The **debug pill + overlay** (`LyricsView.cs:313-317`, `:886-959`, `_debugOpen :317`, and the `// ink-scan: off|on`
  fence at `:880-885`) are **kept**. What is removed is only the environment-variable *read* that gated them; they
  move onto the SAME persisted setting the inspector already uses (`DeveloperMode.Enabled`, `RightRail.cs:382`, key
  `diag.developerMode`, `AppSettings.cs:308`) — `_debugOpen` becomes conditional on that flag instead of on
  `WAVEE_LYRICS_DEBUG`. Yes, they read the SAME `LyricsDiagnostics.ForTrack(trackId)` report the inspector reads
  (`:909` vs `LyricsInspectorDialog.cs:28`) and paint a strict subset of it (identity block + source rows; no raw
  payloads, no parsed candidates, no re-fetch, no bundle) — that overlap was this chapter's reason to propose deleting
  it as subsumed by the inspector, and Christos's answer is to keep it anyway: it is a real surface, not a duplicate,
  and the inspector's superset is not a reason to remove the smaller one. W21 stays in §2 as the CURRENT 0.3 surface,
  not the 0.2.9 record under a retirement banner, and its budget carries forward inside `Lyrics.UI.cs`/`Lyrics.cs`
  unchanged (§2 already counted these lines; nothing was ever subtracted for their deletion). The caps in §7 gap 7
  were never part of the env gate — the report is published on every search regardless — so none of this changes
  backend behaviour.
* The **advance probe** keeps every seam on the view (`ProbeSyncMode`, `ProbeActive`, `ProbeStep`, `ProbeForceSnapped`,
  the node/cascade readers, `:71-96`) — those are `internal` accessors, not switches, and P1's five assertions (one-frame
  latch, monotone comp decay, ≤0.55 s settle, top-first onset, zero hot-phase alloc — `WaveeNavProbe.cs:1826-1836`) are
  the ONLY evidence the latch + cascade is correct. What changes is the door: `WAVEE_LYRICS_ADVANCE_PROBE=1` becomes
  **`Wavee.exe --lyrics-advance-probe`**, dispatched from `Screens/Diagnostics.Probe.cs` next to the `--perf-bench`,
  `--startup-bench` and `--crash-probe` entries the app already has (`Program.cs:197-207`), with
  `--probe-out <dir>` / `--probe-playback-frames N` / `--probe-lyrics-frames N` replacing the `EnvInt` knobs
  (`WaveeNavProbe.cs:1864,1868`). It cannot be an in-app button: it drives the frame loop itself
  (`host.RunFrame()` + `window.WaitForWork(16)`, `:1845-1846`) and refuses `--fake` (`:1854`). `WaveeApp.cs:34-40`'s two
  env reads become reads of the parsed argument. The same door serves `WAVEE_LYRICS_OPEN` / `WAVEE_LIVE_LYRICS_SCROLL_PROBE`
  (`WaveeShell.cs:270`), which are the same seam by another name.
* The always-on **`lyrics.clock`** line is untouched and must never become a switch of any kind (see the must-not-simplify
  list above).

**(b) `FG_STAGE_RECTS` becomes a Developer-mode signal.** `Diagnostics.StageRects : Signal<bool>`, persisted as
`diag.stageRects` (default false) and flipped by one row in Settings ▸ Diagnostics ▸ Developer — the precedent is exact:
`DeveloperMode.FpsOverlay` is a `Signal<bool>` seeded from `WaveeSettings.FpsOverlay` and written persist-then-publish
(`App/DeveloperMode.cs:29-31,36-46`, `AppSettings.cs:309`). The stage reads it **`.Value` inside `Render`**, never as a
`static readonly bool` captured at class init (`ImmersiveLyricsSurface.cs:247`), so a flip re-renders the surface and
installs or removes the seven `OnBoundsChanged` callbacks — `root` `:183`, `body` `:211`, `content` `:222`, `identity`
`:331`, `panes` `:342`, `lyricscolumn` `StagePanes.cs:113`, `pivot` `StagePanes.cs:129`. The callback stays `null` when
off (that null is what keeps the cost at zero), the log line keeps its shape (`rect <name> = x,y wxh` on category
`stage`, `:251-252`), and the naming rationale at `:242-246` ports with it. Wireframe: W23.

**(c) No, and 0.3 does not need it.** The wiped run keeps the plain-`string` `TextEl` path. `MeasureRunLength` calls
`fonts.GetRangeRects(text, in style, …)` on `doc.Lines[i].Text` with a `TextStyle` that must mirror `LineText` exactly
(`:1554-1570`, `:2876-2889`), and the feather is a FRACTION of that measurement — an interning layer between the
measured string and the painted one is a silent re-scale of every wipe boundary. So: lyric line text is a `string` on
`LyricsDoc` (§7 corrected), `StringId` is used only for this surface's static strings (the two messages, the video
banner, the resync label, the pivot links, every inspector label). No engine request; if the engine's `StringId` path
later carries `GlyphWipe`, it is an optimisation to re-evaluate, not a dependency of this port.

**(d) Yes — the inspector moves to `Screens/Diagnostics.UI.cs` (Wave 6, owner S).** The dialog is 564 lines of developer
surface on a theme plate with a monospace face (`LyricsInspectorDialog.cs:33-34`) — it shares no ink, no metric and no
motion with the reading surface, and it belongs beside ~~the API console and~~ the log view (the API console is DELETED — plan §9.6 Q7, 2026-09-12), not inside `Lyrics.UI.cs`. The
split: the STORE stays SHELL-side in `Shell/Lyrics.Host.cs` (`Lyrics.Diagnostics`, §7 gap 7's caps); the DIALOG and its
header button become `Diagnostics.LyricsInspector.Open(overlay, trackId)` in `Screens/Diagnostics.UI.cs`; `Rail.UI.cs`
keeps only the dev-mode-gated glyph that calls it (`RightRail.cs:382`, `LyricsInspectorDialog.cs:38-52`), so the rail
header subscribes to nothing new. **Chapter 27 must carry the other half of this decision** (this chapter cannot edit
it): add `Diagnostics.LyricsInspector` to its unowned-types table (`27-settings-and-diagnostics.md:1720-1724`, beside
`FpsOverlay` and ~~the API-console helpers~~ — struck, plan §9.6 Q7, 2026-09-12: those helpers are DELETED), move `Diagnostics.UI.cs 1 700` to **2 300** in its budget line (`:1661`), and
add the W23 "Stage geometry log" row to its Developer group (`:375-379`). Until 27 says so, two chapters describe the
inspector and only this one names its file.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`, unless a row says
otherwise. "Rail" = window 1180×760, rail open on Lyrics (player-bar lyrics button). "Stage" = the same window with the
rail header's ⛶ pressed. Motion rows need a frame recording (60 fps screen capture) and a frame-by-frame diff, not a
still.

1. **Rail row rhythm.** Static capture, rail, a 2-line lyric visible: single-line row 47 DIP, two-line row 80 DIP, side
   pad 22, type 26/33/700.
2. **Stage row rhythm.** Static capture, stage: 64 / 110 DIP, side pad 64, type 36/46/700, column capped at 700.
3. **Focal band.** Freeze on any handoff: the active row's CONTENT-box centre sits at 0.40 (rail) / 0.38 (stage) of the
   lyrics viewport height, ±1 DIP.
4. **Left anchor.** Frame diff across a handoff: the left glyph edge of every visible row does not move horizontally.
5. **Inactive scale.** Measure a dist-1 row's cap height vs the active row's: ratio 0.98, not 0.75.
6. **Opacity ladder, future side.** Sample row alphas below the active line: 0.45 / 0.24 / 0.14 / 0.11 / 0.10.
7. **Opacity ladder, past side.** Above the active line: 0.19 / 0.13 / 0.10 — visibly dimmer than the same distance below.
8. **Rail σ ladder.** Edge-energy or visual check at ring 1..5: 1.25 / 2.5 / 4 / 5.5 / 6.5 — ring 1 barely soft, ring 5
   dissolved.
9. **Stage σ ladder is flatter.** Same rows on the stage: 1.2 / 2.0 / 2.6 / 3.0 — the verse around the line stays
   readable as shape.
10. **No active line ⇒ no blur.** Scrub to before the first line: every row crisp (not fully blurred).
11. **σ direction.** Recording of one handoff: the outgoing line softens within ~100 ms; the incoming line sharpens
    progressively over ~200 ms.
12. **Karaoke feather width.** Zoom capture of the boundary mid-word: a 5 DIP (rail) / 7 DIP (stage) band, not a
    word-wide wash.
13. **Unsung alpha.** Sample an unsung glyph on the active line: 0.58 of the sung colour.
14. **Wipe tracks the voice.** Recording with audio: the bright boundary sits on the syllable being sung (≤2 % ahead),
    never a word early.
15. **Wipe is smooth at 120 Hz.** Frame diff on a high-refresh panel: the split advances every frame, no 15.6 ms
    staircase.
16. **Per-word lift.** Zoom recording: unsung glyphs sit 1.25 / 1.75 DIP low and rise as the feather passes.
17. **Reduced motion kills the lift, keeps the fill.** Turn off "Show animations in Windows"; re-check 14 and 16.
18. **Held-note bloom.** Find a ≥700 ms held syllable: a soft halo swells under it and melts into the note's end; a
    short syllable produces none.
19. **Halo never nests.** Recording of a handoff: no halo appears on a near-but-not-active row.
20. **Line-synced documents.** Play a line-synced track: no wipe, colour crosses Secondary→Primary over ~167 ms, halo is
    a constant σ 7 / 10 on the ACTIVE row only.
21. **Lead time.** Recording with audio: the line rises into focus ~140 ms BEFORE its first syllable sounds, while its
    fill starts on time.
22. **Handoff is a cascade.** Frame diff mid-handoff: rows below the outgoing line start moving ~60 ms apart, all land
    together ≈0.48 s after onset, no overshoot.
23. **Reduced motion flattens the cascade.** Same recording with the OS flag: one rigid translate, same duration.
24. **Pause mid-handoff.** Hit pause during a handoff: the document finishes travelling instead of freezing off the band.
25. **Seek.** Click a lyric line: playback seeks to its start and the document HARD-LATCHES (no glide).
26. **Click target.** Hover a line: cursor is a hand, nothing lights up; Tab moves focus through lines.
27. **Detach.** Wheel-scroll the list: every line goes crisp within ~200 ms and the Resync pill fades up 18 DIP (rail) /
    28 DIP (stage) from the bottom.
28. **Countdown.** Watch the pill: the ring drains over 4 s in visible steps and then resyncs itself.
29. **Resync glide.** Tap the pill mid-song: the viewport eases back (~0.5 s, no overshoot) and the ladder snaps back.
30. **Interlude.** Play a track with a ≥5 s instrumental break: the finished line takes the ordinary past treatment and
    three dots appear above the next line.
31. **Interlude reserve.** Same moment: the upcoming line does NOT move; the space opens above it.
32. **Dots fill + breathe.** Recording across the break: the dots fill left-to-right, breathe at a 2.6 s period, and are
    gone one second before the next line starts.
33. **Reduced motion holds the dots still but keeps filling them.**
34. **Interlude on a line-synced doc.** Dots appear, focus does NOT advance.
35. **Secondary line.** On a track with a translation, the 🌐 button exists; press it: a smaller, dimmer line appears
    3 DIP under every translated lyric at 0.62× the type, and the document re-latches without a jump.
36. **Cycle skips missing layers.** On a translation-only document the cycle is none → translation → none.
37. **Toggle is hidden** on a document with neither layer (check a plain LRC track).
38. **Blur strength.** Settings ▸ Appearance ▸ Lyrics ▸ Lyrics blur: drag to 0 → every σ (ladder AND halo) is gone on the
    already-open surface, with no line change needed; the Auto link disappears and reappears.
39. **Auto tier.** Reset to Auto: strength resolves to 100 (or 40 on a weak-GPU box, per `GpuProfile.IsWeak`).
40. **Loading shimmer.** Start a fresh track with cold lyrics: six bars at 219/184/204/158/194/148 DIP (rail), rowH 22,
    gap 18, radius 6, at the lyric's own left margin, breathing 1→0.5→1 over 1 s.
41. **Reveal.** Recording of the shimmer→content swap: a 250 ms cross-dissolve with no translate and no blank frame; the
    first line is already parked on the band.
42. **Empty.** A track with no lyrics: centred "No lyrics available" at 14/20 in the secondary ink.
43. **Nothing playing.** Stop playback: centred "Nothing playing".
44. **Unsynced.** Play a track whose winner is unsynced: the whole document at 19/26 (rail) / 28/36 (stage) at 0.88
    alpha, top/bottom pad 26/44, scrollable, no highlight.
45. **Video suppression.** Start a music video with the lyrics rail open: the top-docked note appears, the document
    switches to the unsynced treatment, and scrolling still works.
46. **Upgrade.** Play a track where Spotify answers first and a word-synced source upgrades: the karaoke wipe appears
    within a second or two, and the document does NOT jump or re-scroll.
47. **Same-shape upgrade keeps working.** After that upgrade, the emphasis ladder still sweeps (no frozen document).
48. **Stage frame.** Stage at 1180×760: caption band 48 and player bar 72 still receive clicks; the body is 640 tall.
49. **Stage scrim.** Sample the backdrop luminance at y = 0, 0.22·H, 0.62·H, H: 0.76 / 0.46 / 0.46 / 0.70 veil.
50. **Column shade.** Sample across x = 0 → 612: 0.26 held to 352, then feathered to exactly 0 — no locatable edge.
51. **Drift.** 90 s recording of the stage backdrop: slow, non-repeating drift of ≤4 % with a ≤2 % scale wobble; no edge
    of the cover ever enters the frame.
52. **Drift off.** Settings ▸ Lyrics ▸ Animated lyrics backdrop off: the backdrop is perfectly still and re-centred.
53. **Reduced motion stops the drift** regardless of the setting.
54. **Stage exit.** The chevron FAB is 44 DIP with a card shadow and reads on a white cover; the 🌐 beside it is a 40 DIP
    scrim FAB. Escape closes the surface; Escape still closes it after clicking dead space.
55. **Stage breakpoint.** Drag the window across 600 wide: the identity column becomes a header row exactly once per
    crossing (promotion needs 640).
56. **Stage height ladder.** Shrink the window vertically: the device line folds, then the volume row, then the whole
    shape demotes — the cover shrinks in 4 DIP steps and no control is ever clipped away.
57. **Pane cross-fade.** Click "Queue" then "Lyrics": a 250 ms opacity cross-fade; the lyrics document does NOT reload or
    re-scroll, and the lyrics ticker parks while Queue is up (check `lyrics.clock` log gaps or CPU).
58. **One ticker.** Open the stage with the rail also on Lyrics: only one surface ticks (the rail's is parked by its
    visibility gate).
59. **NPV peek.** Rail ▸ Details on a line-synced track: two 56 DIP rows, the peek at 0.38 opacity, a 3 DIP accent spine,
    rows flipping ±56 DIP over 150 ms; click opens the lyrics rail.
60. **NPV peek during video:** one 56 DIP row with the tertiary spine and the sync note.
61. **Inspector.** Developer mode on → `</>` in the rail header → dialog 548 wide with three tabs, provider cards whose
    border is the accent for the winner, Raw showing monospace payloads capped at 4,000 chars, Parsed showing
    `mm:ss.mmm→mm:ss.mmm` + duration + text with `[tr]`/`[ro]` sub-lines.
62. **Inspector chrome is theme ink** (it is a dialog plate, not the reading surface) in both themes.
63. **Light theme, stage.** Flip to light: the stage floor, scrim and ink all invert together; lyric text stays legible
    on a white cover, and the bloom is visibly weaker (0.5×) rather than a black smudge.
64. **Light theme, rail.** The rail's lyrics use the theme text rungs, and the resync pill is a theme plate.
65. **Idle cost.** Play, follow, do not touch anything: the frame log shows no per-frame allocation and settled frames
    are elided (skip-submit) between line changes.
66. **Stage shimmer.** Open the stage on a cold track: six bars at 426/343/385/302/364/260 DIP, rowH 32, gap 24, at the
    64-DIP gutter, starting 150 DIP down — a DIFFERENT ratio set from the rail's, not the rail's scaled up.
67. **Shimmer ink flips.** Light theme, stage: the bars are `StageInk.Ink @ 0.12` (dark bars on the light veil), not the
    rail's `FillSubtleSecondary`.
68. **Peek is absent, not empty.** Rail ▸ Details on an UNSYNCED (or lyric-less) track: there is no spine, no 112-DIP
    reel and no gap where one would be — the block simply is not there, and no 100 ms interval is running.
69. **Globe over an unsynced document.** Play an unsynced track that carries a translation: the 🌐 toggle does NOT
    appear on either surface (an unsynced document has no rows to render a second line in).
70. **Globe retires with the document.** Stop playback with the rail on Lyrics: the 🌐 disappears on the same frame the
    document clears — it must not linger over "Nothing playing".
71. **Upgrade lands on a handoff.** Play a track whose word-synced upgrade arrives mid-line: nothing changes until the
    NEXT line takes focus, and then the wipe is there. Nothing re-shapes under the line you are reading.
72. **Upgrade while paused lands at once.** Pause mid-track on a line-synced document and wait for the upgrade: it
    applies immediately (there is no handoff to wait for).
73. **Cascade write band.** A very tall window (>1800 DIP of lyrics viewport): rows more than 24 away from the new
    active line snap with the latch instead of easing — a bound on the effect, not a glitch.
74. **Resume from minimize.** Minimize mid-handoff, wait 10 s, restore: the document catches up over a few frames and
    never teleports (the 100 ms dt clamp).
75. **Pill press only.** Hover the Resync pill: the fill changes, the pill does NOT grow. Press it: it shrinks to 0.98.
76. **Stage FABs hang from the top.** Stage at 1180×760: both discs sit 16 DIP below the top of the 88-DIP band, not
    centred in it.
77. **Drift with no art.** Play a track with no cover: the stand-in is perfectly still and no drift ticker runs.
78. **Inspector, cold.** Developer mode on, open the inspector on a track that was answered from the disk cache: the
    Raw tab explains that a cached answer carries no payload; `Save bundle…` chains a re-fetch instead of writing an
    empty folder, and both it and `Re-fetch` are disabled while `Re-fetching…` shows.
79. **Inspector, no track.** Stop playback and open it: one `Nothing is playing…` line, no toolbar and no tabs.
80. **Parsed-tab anomaly colours.** Find a track with a squashed line: its duration column reads e.g. `217ms` in amber;
    an out-of-order or end-before-start line reads red.
81. **Blur strength 0 is a snap, not an ease.** Recording of the slider reaching 0 on a PAUSED surface: every σ is gone
    on the next frame (one pass), not over ~200 ms — and nothing re-enters the ramp afterwards.
82. **Dots anchor arithmetic.** Resize the window vertically with a break running: the dots' bottom edge stays exactly
    `lift` (36 rail / 48 stage) above the focal band at every height — the anchor is a constant margin, not a per-frame
    re-read (103.5 rail / 145.9 stage).
83. **Dots are not hittable.** During a break, click and wheel-scroll straight THROUGH the dots: the lyric underneath
    takes the click and the list scrolls; Tab never lands on them.
84. **Peek over a video on an unsynced track.** Start a music video on a track whose lyrics are unsynced: the NPV peek is
    absent entirely — no reel, no sync note (the note is a synced document's state, not a missing document's).
85. **Held note at the end of a line.** Find a ≥700 ms syllable that ENDS the line: its bloom melts out on the line's
    own 320 ms window, not only the syllable's — it is never still lit as the next line rises.
86. **Pivot underline cross-fades.** Frame-diff a Lyrics⇄Queue click: the 2-DIP accent underline fades over ~167 ms under
    the new label and the old one fades out in place — it does not slide between the two links.
87. **Inspector, a giant payload.** Open the inspector on a word-synced TTML track after a re-fetch: the caption names
    BOTH truncations where they apply (`CAPTURE-TRUNCATED to 128,000` and `showing the first 4,000`), and Copy hands
    over the full captured text, not the 4,000.
88. **Inspector, no re-fetchable provider.** Sign out (or go offline) and press `Re-fetch from providers`: an amber
    status line says so and the dialog stays usable — no toast, no empty folder, no spinner that never ends.

The last three are **0.3 gates, not 0.2.9 comparisons** — they check the §9 resolutions landed, and each fails loudly on
the 0.2.9 build by design:

89. **No env-var reaches this surface — but the pill still exists, gated by Developer mode (restated 2026-09-12,
    Q1).** Launch 0.3 with `WAVEE_LYRICS_DEBUG=1`, `FG_STAGE_RECTS=1` and `WAVEE_LYRICS_ADVANCE_PROBE=1` all set and
    Developer mode OFF: the app starts normally, no "lyrics debug" pill appears bottom-right in either surface, no
    `stage` `rect …` lines appear in the log, and the ordinary lyrics ticker runs (the probe does not silence it).
    Set none of the env vars but turn Developer mode ON: the pill DOES appear in both surfaces — it was never
    deleted, only re-gated. Then launch with none set and `--lyrics-advance-probe`: the probe runs.
90. **Stage geometry log is a live toggle.** Developer mode on, stage open, Settings ▸ Diagnostics ▸ Developer: flipping
    `Stage geometry log` ON emits seven `rect <name> = …` lines on category `stage` within a frame or two of the next
    bounds change (root, body, content, identity, panes, lyricscolumn, pivot) WITHOUT restarting the app; flipping it
    OFF stops them, again with no restart. A restart-required toggle means it was read as a static, not a signal.
91. **The inspector is a Diagnostics surface.** The rail header's `</>` glyph still opens the same dialog (W17-W19b,
    unchanged pixel for pixel), but the type behind it lives in `Screens/Diagnostics.UI.cs`; the lyrics header's own code
    contains no dialog body. Verified by opening it from the rail AND by the file layout, not by a source-text test.

---

## 11. Audit log

Adversarial re-read of every 0.2.9 source against this chapter, 2026-09-12. Every number in §2/§3/§5 was re-derived from
the code (and, where the chapter quoted an engine value, from `..\fluent-gpu`): the σ ladders, both opacity ladders,
`UnsungAlpha 0.58`, the 5/7 DIP feather + 0.01/0.10 clamps, `WipeLeadFrac 0.02`, the 1.25/1.75 lift, the 0.98 flat
scale + `TransformOriginX 0`, the two springs (ω 7.07 / response 0.889; opacity response 0.18), `DofRampTauMs 65`,
the write gates (σ 0.5, transform 0.1, split 0.0008, softness 0.002, dot alpha 0.004), the cascade tuning
(60 ms × rank ≤ 4, 0.48 s, y = 7/(0.48 − delay)), `LeadMs 140`, `InterludeGapMs 5000` / exit 1000 / breath 2600 /
depths 0.06 and 0.10, dot 9|12 + air 8|10 + reserve 25|32 + lift 36|48 + the 145.9 stage margin, the resync 4000 ms /
120 steps / 110 ms half-life / 4 DIP per second, the row metrics 26/33/7/22 and 36/46/9/64, unsynced 19/26/5 and
28/36/7 at alpha 0.88 with 26|44 block pad, secondary x0.62 + 3 DIP, the 548/492 dialog, `ProgressRing` stroke 1.6875
(= 3.0 x 18/32), `AutoEdgeFade` band 40, `Expressive.Fast` 250 + `Easing.SmoothOut`, `MotionTok.ControlFast` **150** /
`ControlNormal` 250 / `WaveeMotion.Faster` 83 / `Fast` 167, `TransitionDynamics.Default` = spring(0.30, 0.85),
`Spacing` 4/8/12/16/24, `WaveeSize` 32/44/72, the stage bands 88/56/72, `WideEnterW 600` + hysteresis 40,
`WideColumnW 352` + `RegionGapW 56` + `ColumnPadX 24` + `MinArtW 168` + `ArtQuantum 4`, the scrim 0.76/0.46/0.46/0.70
at 0/0.22/0.62/1, the column shade 0.26 → 0.0884 → 0 across 612 at 0.575/0.809/1, `PaneShade` 0/0.096/0.24, the drift
37 s/53 s/4 %/2 %/33 ms/1.30/σ80/0.5, the on-media ladder (white 1.0/0.80/0.60, glass 0.10/0.16, plate 0.14/0.22/0.28,
scrim 0.55/0.745/0.863, stroke 0.227, skeleton 0.12, `MediaStage` #0A0A0A) and the theme rungs (dark 1.0/0.772/0.529,
light primary 0.894). All six test counts (7/3/6/8/7/29) match.
`StageLayoutTests.TheLyricsReadingSurface_PaintsNoThemeInk` is confirmed ABSENT.

| # | kind | section | correction |
|---|---|---|---|
| 1 | wrong | §5 hover/press row | The resync pill was listed as `ScaleSubtle 1.02/0.98` with an 83 ms brush. It sets **`PressScale` only** (0.98) and declares **no `HoverScale` and no `BrushTransitionMs`** (`:533-534`) — it never grows on hover. Split into two rows; parity item 75. |
| 2 | wrong | W19 | `"Cascada Code"` → **`Cascadia Code`** (`LyricsInspectorDialog.cs:77`). |
| 3 | wrong | §6 loc | `"No lyrics available"` cited as `:870,872,873`; the three call sites are `content` `:870`, `onFailed` `:872`, `onEmpty` `:874`. |
| 4 | wrong | header / §9 budget | Backend given as "≈4,350 lines"; counted **4,504** across 19 files, so the total is **≈9,600**, not 9,440. Header and the honest-budget paragraph corrected. |
| 5 | missing | W2 | The whole **stage shimmer arm**: base 520 (not 255) with a DIFFERENT ratio set `[.82 .66 .74 .58 .70 .50]` ⇒ 426/343/385/302/364/260 (`:1445,1452`). Also noted `AlignSelf.Start`, that the breath is the engine's `SkeletonStyle` default (1000 ms / 0.5) and not `LyricsShimmer`'s, and that the `SkeletonStyle` `RowGap 18\|14` the view passes is **inert** under a custom `shimmerSource` — the visible gap is 18 rail / 24 stage. Parity items 66-67. |
| 6 | missing | W4/W5 | The **third blank**: `b is null \|\| svc is null` ⇒ a bare `BoxEl { Grow = 1 }` with no text (`:428`). |
| 7 | missing | W14 | Top-band padding is `(16,16,16,12)` **including left 16**, and `AlignItems = Start` — the two FABs hang from the top of the 88-DIP band rather than centring. Exit glyph keeps `HoverColor = Ink`. Parity item 76. |
| 8 | missing | W20 / §3 | The peek's **hidden** state (a bare `BoxEl`, zero height, interval not armed) whenever `ShouldShow` is false — Lines 0 or Sync not in {Line, Syllable}; the slot's `MaxLines 2` + wrap + char-ellipsis; the before-first `(−1, 0)` and after-last `(n−1, −1)` pairs; the video spine's 28-DIP half height. Parity item 68. |
| 9 | missing | W19b (new) | The inspector's four non-report states — no track, no report yet, empty Raw, empty Parsed — plus the busy relabel/disable pair, the amber status line, the chained save, and the parsed-row anomaly colour rules. Parity items 78-80. |
| 10 | missing | §3 | Six token rows: inspector status line, provider id/outcome (13/18/**700**), verdict line, and the two `WAVEE_LYRICS_DEBUG` surfaces (pill + overlay), which had a wireframe but no token row. |
| 11 | missing | §5 / §7 / §8 / §9 | The **deferred upgrade** — a richer document arriving mid-line is held in `_pendingUpgrade` and applied inside the NEXT handoff, `OnFrame` returning immediately (`:1253-1257`, `:2124-2128`); paused / no doc / pre-first-line applies at once. Added as two motion rows, folded into §7's upgrade row, added to §9's must-not-simplify, and given parity items 71-72. The authority rule itself (`IsRicherLyrics`/`Richness` — any word-by-word line ⇒ 3, ties broken by syllable count) was missing from §8 entirely and is now a row marked UNVERIFIED / add tests. |
| 12 | missing | §5 / §9 | The **cascade write band ±24 rows** (out-of-band lines retired to identity at arm time) and the **100 ms dt clamp** on both the cascade and the σ ramp. §8 named the band; §5 and the parity list did not. Parity items 73-74. |
| 13 | missing | §5 / §9 | `BeginGlowFades` finishes a third in-flight out-fade instantly — **at most two halos ever animate** (`:2284`). |
| 14 | missing | §5 | The drift ticker is also gated on **`art.Length > 0`** (`:165`), and the "drift off" path writes `Identity` once and resets `_driftOriginQpc` so a re-enable starts at t = 0 (`:162,539-549`). Parity item 77. |
| 15 | missing | §6 | The globe's ACTIVE arm is `available & BitFor(mode)`, not "mode != 0"; the ✕ is the one header button with no tooltip; and `Available` is forced to 0 for a **non-timed** document (`PublishSecondaryAvailability` gates on `IsTimed`, `:1150-1155`) and retired by `ClearDocument` before its early-out (`:1199-1203`). Parity items 69-70. |
| 16 | missing | §6 | The inspector's disabled/relabelled buttons, the chip sets on Raw and Parsed, the Warn status line, the chained `Save bundle…`, and an explicit statement that **Escape on the stage is the only keyboard shortcut in the whole surface**. |
| 17 | missing | §7 | The document fetch key is `trackId + "\|" + artist` (`:855`) while the remount `Key` is the track id alone (`:441`) — an artist-metadata refresh re-fetches without remounting rows. |
| 18 | missing | §7 gap 7 | The diagnostics store's other caps: 6 payloads per source, 24 recent probes, 256 distinct tracks (FIFO), 3 tracks of inspections (`LyricsDiagnostics.cs:198-203,222-223,249`). |
| 19 | missing | §8 | `LyricsPeekClock.ShouldShow` and the reel's edge rules were not in the "decides" column; the backend test list omitted `LyricsDiskCacheTests` and `MusixmatchRichsyncFixtureTests`, and the pipeline row omitted `AggregatingLyricsProvider`, `LyricsDiskCache`, `LyricsInspectionExport`, `Lyricify/*` and `Sources/*`. |
| 20 | missing | §9 | The always-on **`lyrics.clock`** log line (every 30 s of wall time while a timed document plays, `:2081-2099`) — the only standing evidence that the karaoke-stepping bug has not returned, and per CLAUDE.md it must not become env-gated. |
| 21 | unverified | §3 shimmer row | "pulse 1 → 0.5 → 1 @1000 ms" is the engine's `SkeletonStyle` DEFAULT, not anything this file authors; the chapter now says so rather than implying `LyricsShimmer` animates. |
| 22 | overclaim | §0 non-negotiable 14 | "Zero re-render per frame" is exactly right for the per-frame lane, but three signals DO re-render on purpose, at human rates: `_secondary` (whole document, once per toggle, `:242-253`), `_dotsShown` (twice per interlude), and `_cascadeRunning` + `_followMode` (ticker only, twice per handoff). The claim is left standing — §1.2 and §5's frame-gating paragraph carry the detail — but it is flagged here so nobody "optimises" those three away. |

### Second adversarial pass — 2026-09-12

Independent re-read of every assigned source (`LyricsView.cs`, `ImmersiveLyricsSurface.cs`, `LyricsInk.cs`,
`LyricsBlurPolicy.cs`, `LyricsMediaClock.cs`, `LyricsInspectorDialog.cs`, `LyricsRowShape.cs`, `LyricsSyncGate.cs`,
`FrameTime.cs`, `NpvLyricsPeek.cs`, `LyricsPeekClock.cs`, `LyricsModel.cs`, `LyricsDiagnostics.cs`) plus the call sites
(`RightRail.cs`, `StagePanes.cs`, `StageChrome.cs`, `StageLayout.cs`, `NowPlayingPanel.cs`, `PlayerBar.cs`,
`WaveeShell.cs`, `SettingsPage.Appearance.cs`, `AppSettings.cs`, `PlaybackBridge.cs`, `Playback.cs`, `en-US.json`) and
the engine constants the chapter quotes (`Spacing` 4/8/12/16/24, `Radii.Control` 4, `WaveeCta.IconButtonSize` 32,
`WaveeSize.NavItemH` 44 / `PlayerBarH` 72, `TitleBar.ExpandedHeight` 48, `Ui.Subtitle` 20/28/600, `Expressive.Fast` 250,
`MotionTok.ControlFast` 150 / `ControlNormal` 250, `WaveeMotion.Faster` 83 / `Fast` 167 / `ScaleSubtle` 1.02-0.98 /
`ScaleEmphatic` 1.07-0.92, `DefaultAutoEdgeFadeBandDip` 40, `SkeletonStyle` 1000/0.5, `ContextBandLayout` 16/8/2/4,
`WaveeCta.TextAction*` 14/20/600). Every §2/§3/§5 number in the first pass's list re-derived and **confirmed**; the six
test counts (7/3/6/8/7/29) and the 19-file / 4,504-line backend re-counted and confirmed; `PaintsNoThemeInk` confirmed
still absent. New corrections below.

| # | kind | section | correction |
|---|---|---|---|
| 23 | wrong | header | `ImmersiveLyricsSurface.cs` is **550** lines, not 551 (the ≈5,090 UI / ≈9,600 total round unchanged). |
| 24 | wrong | W3 | `SkeletonRegion.cs:166-168` → **`:161-163`** (`SkelReveal.FadeOnly` → `Animate(Opacity, 0→1, Expressive.Fast, SmoothOut)`). Engine line numbers drift; re-check on every engine bump. |
| 25 | wrong | §1.1 | The "No lyrics available" call sites were still written `:870-873` in the anatomy tree (the first pass fixed only §6's copy) → `:870, :872, :874`. |
| 26 | missing | §5 | **The blur-strength-0 short-circuit.** `_dofScale <= 0` snaps every σ to 0 in ONE pass and lands `_dofRampPending = false` (`:1631-1645`) — NOT the ordinary 65 ms decrease, and the reason parity item 38 works on a paused surface. Added as a motion row + parity item 81. |
| 27 | missing | §5 | **The landing gates**, as a paragraph of their own: cascade `CascadeLandDip 0.5 DIP` **and** `CascadeLandVel 20 DIP/s` together (the velocity half is what stops a line passing through zero being snapped), the exact landing writes (0.0005 DIP transform, 0.001 σ) exempt from the 0.1/0.5 gates, and the dots' PAIRED transform gate (0.002 scale + 0.05 DIP lift compared together, because both live in one matrix). §9 listed the write gates but not the landing gates. |
| 28 | missing | §5 / §0 #13 | The word-by-word bloom is clamped by **two** melts: `0.75 · min(heldSyllableGlow, alphaOut)` where `alphaOut` eases over `GlowOutMs` into the **LINE's** end (`:2313-2315, 2324`). A held note that ends a line dies on the line's clock. Parity item 85. |
| 29 | missing | W3 | The shimmer orphan's own `ExitMs` is the SAME `Expressive.Fast` 250 (`SkeletonStyle` default) so the dissolve is symmetric; reduced motion makes the engine SNAP the swap; and `SkeletonPulse` is applied to `FirstChild(node)` — which is why a custom `shimmerSource` breathes (`Reconciler.cs:1474-1479`), confirming the first pass's claim rather than leaving it asserted. Also noted the region's scrollbar suppression while loading (`Reconciler.cs:1472`). |
| 30 | missing | W9 | The dots' **anchor arithmetic**: `m = (d + lift)/band − d`, viewport-independent — the chapter gave only the stage's 145.9 and never the **rail's 103.5** (`:659`). Added the stage's dot side pad (RowSidePad 64) and the fact that the whole dots subtree is `HitTestVisible = false` (decorative: no hit, no focus, no automation name — `:577-581, 665`). Parity items 82-83. |
| 31 | missing | W19b | Five more inspector states: a report with **zero sources** ("No source ran for this track.", `:244-245`); a payload past the **128 k capture cap** (`· CAPTURE-TRUNCATED to N` alongside the on-screen `showing the first 4,000`, `:379-381`); a parsed document past **300 lines** (the "…N more lines" caption, `:475-476`); a line with **no EndMs** (`  --:--.---` and an EMPTY duration cell, `:494-499`) and the blank-syllable `␣` (`:515`); and the **candidate** description line ("…BEFORE the reranker's offset correction", `:443-446`). Plus the two re-fetch FAILURE statuses — no re-fetchable provider (`:187-190`) and "Re-fetch failed — …" (`:201-209`). Parity items 87-88. |
| 32 | missing | §6 | The inspector fires **four** success toasts, not one: "Lyrics report copied", "Lyrics evidence bundle saved", "`<sourceId>` payload copied", "Parsed lyrics copied" (`:138, 180, 386, 463`) — all English literals, all with `lyrics.inspector.*` keys already sitting unused in `en-US.json`. |
| 33 | missing | §6 / W10 | The latched globe keeps the **accent on hover** (`HoverColor = active ? AccentTextPrimary : TextPrimary`, `RightRail.cs:429-431`): a lit toggle does not brighten under the pointer. The same rule holds for the stage's `ScrimFab` (`StageChrome.cs:179-180`). |
| 34 | missing | W20 / §7 | The NPV peek runs its **OWN** `UseResource` → `GetLyricsAsync(trackId)` on the bare track id (`NpvLyricsPeek.cs:52-56`) — a second fetch beside the view's `trackId\|artist` one, deduplicated only by the provider cache. Added as a §7 data row: in 0.3 the peek plans NO fetch and reads the one Lyrics table. Also added the synchronous first-paint seed from `PositionMs.Peek()` (`:69-73`) and the `show`-edge re-tick (`:64`). |
| 35 | missing | W20 | ORDERING: the `!show` early-out runs BEFORE the video branch (`:67-68`), so over an unsynced/absent document a playing video shows **nothing** — the sync note is a state of a *synced* document, never a stand-in for a missing one. Its caption is NoWrap / MaxLines 1 / CharacterEllipsis, unlike the reel's wrapping slots. Parity item 84. |
| 36 | missing | W21 | The debug overlay's two empty states: "No search recorded for this track yet — …" (`:929-933`) and "(no sources ran)" (`:943-944`); the rerank line renders only for a HIT with a reason (`:988-989`) and the detail breadcrumb only when the trace carries one (`:986-987`). |
| 37 | missing | §3 | Five token rows the table lacked: the pivot **link** box (pad 8/4/8/2, r4, `WaveeCta.TextAction*` 14/20/600, 2-DIP underline with a 4-DIP gap, `Role = Tab`) — and that the pivot BAND is `AlignItems = End` as well as `Justify = End`, so the links hang from the bottom-right rather than centring in the 72; plus the inspector's parsed/timing lines, payload card, parsed row and sub-line/syllable strip. |
| 38 | missing | §5 | The pivot underline + label are a **167 ms brush cross-fade** (`BrushTransitionMs = WaveeMotion.Fast`, `StageChrome.cs:340-351`), never a flown selection bar. Parity item 86. |
| 39 | missing | §8 | `LyricsBlurPolicy.Enabled` is unit-tested (`Enabled_IsFalseOnlyAtZero`) but has **no production caller** — `LyricsView` gates on `_dofScale <= 0` and Settings never asks. Flagged so 0.3 either gives it its call site or drops it instead of porting a rule nobody consults. |
| 40 | missing | §9 traps | Four traps: the document is SEEDED through `AdvancePastInterlude` at its real opening state (`:1094, 1105`) and an untimed one seeds active = −1; `ClearDocument` also resets the media clock and `_lastSampleQpc` (`:1242-1243`) and retires `_pendingUpgrade` on both exits; `_emphasisFallback` (a shared bucket-6 signal) is the array-resize-gap guard a row must never be left holding (`:164, 1353`); and the ✕/chevron close DIFFERENT surfaces (the rail survives behind the stage, gated by visibility, not branched away). |

### Completeness-critic pass — 2026-09-12

One finding, upheld: §9 ended in four OPEN questions, two of which conflicted with CLAUDE.md's env-var ban, and the
chapter's honest budget (10,000) was never turned into an edit the plan could take. All four are now decided in §9
before the fold, and each decision is carried into every other section that mentioned the thing. Verified against the
0.2.9 code first: `WAVEE_LYRICS_DEBUG` (`LyricsView.cs:313-317`, `:886-959`), `WAVEE_LYRICS_ADVANCE_PROBE`
(`LyricsView.cs:71-96`, `WaveeApp.cs:34-40`, `WaveeNavProbe.cs:1821-1890`), `FG_STAGE_RECTS`
(`ImmersiveLyricsSurface.cs:242-252` + its seven call sites), the inspector's dev-mode gate (`RightRail.cs:382`,
`LyricsInspectorDialog.cs:38-52`), the `DeveloperMode.FpsOverlay` precedent (`App/DeveloperMode.cs:29-46`,
`AppSettings.cs:308-309`), the existing CLI-subcommand pattern (`Program.cs:197-207`), and the wipe's measurement seam
(`LyricsView.cs:1550-1570`).

| # | kind | section | correction |
|---|---|---|---|
| 41 | missing | §9 (a) | **critic-fix:** `WAVEE_LYRICS_DEBUG` resolved — DELETED in 0.3, with the evidence that the inspector is a strict superset over the same `LyricsDiagnostics` store (`:909` vs `LyricsInspectorDialog.cs:28,244-245`) behind a real setting, and with the collateral named (the `_debugOpen` signal `:317` and the `// ink-scan: off\|on` fence `:880-885` go too). The probe half is resolved separately: the `internal` seams STAY, the env door becomes `--lyrics-advance-probe` + three optional args in `Screens/Diagnostics.Probe.cs` beside `--perf-bench` (`Program.cs:197-207`), because the probe owns the frame loop (`WaveeNavProbe.cs:1845-1846`) and refuses `--fake` (`:1854`) so it can never be a button. *(The pill+overlay half is superseded by Q1, see the closing note below: kept, not deleted — only the env var goes.)* |
| 42 | missing | §9 (b) | **critic-fix:** `FG_STAGE_RECTS` resolved — a `Diagnostics.StageRects : Signal<bool>` persisted as `diag.stageRects`, read `.Value` in `Render` (never a `static readonly bool`, which is what `:247` is today), installing or removing the SEVEN `OnBoundsChanged` callbacks enumerated with file:line. |
| 43 | missing | §9 (c) | **critic-fix:** the `StringId`/`GlyphWipe` question resolved app-side with no engine request: the wiped run keeps the plain-`string` path because the feather is a fraction of `GetRangeRects` over that same instance (`:1554-1570`, `:2876-2889`). §7's per-line-text row said "(StringId)" and is corrected. |
| 44 | missing | §9 (d) | **critic-fix:** the inspector's home decided — dialog + button target to `Screens/Diagnostics.UI.cs` (Wave 6 owner S), store stays `Shell/Lyrics.Host.cs`, rail keeps only the dev-mode glyph. §1.2's "a new file (§9)" row is replaced by two concrete rows, and the three edits chapter 27 owes (its unowned-types table `:1720-1724`, its `Diagnostics.UI.cs` budget `:1661`, its Developer group `:375-379`) are named with line numbers, since this pass may edit only chapter 22. |
| 45 | missing | §9 §2-budget | **critic-fix:** the honest ≈10,000 turned into the plan's actual edit — a four-partial split (`Lyrics.cs` 3,700 · `Lyrics.UI.cs` 2,600 · `Lyrics.Stage.UI.cs` 1,400 · `Lyrics.Host.cs` 2,300) plus ≈600 in `Diagnostics.UI.cs`, with the replacement lines for `wavee-0.3-implementation.md:72` and both subtotals (`Shell/` 13 → 14 files / ~21,600 → ~26,500; `Screens/` ~13,700 → ~14,300). The three separate "§2 has no file for…" bullets collapse into that table, and the stage's 1,400 is reconciled against its ≈2,100 total (chapter 21 owns `StageLayout`/`StageIdentity`/`StageChrome`/`StageInk`). *(Superseded in part by row 48: the split is now THREE partials.)* |
| 46 | missing | §0 #15 | **critic-fix:** non-negotiable 15 no longer grants "except the two developer seams that exist today" — it states the 0.3 rule (zero env gates in this surface), names all THREE 0.2.9 seams (the advance probe was missing from it), and points each at its resolution. |
| 47 | missing | W21 / W23 / §1.1 / §3 | **critic-fix:** W21 keeps its heading as the 0.2.9 record but carries a RETIRED-in-0.3 banner with the superset mapping; a new **W23** draws where the two surviving seams land (the Developer group row for the stage-rects toggle, and why the probe is a CLI entry and not a row); the §1.1 anatomy line and the two §3 debug token rows are marked 0.2.9-only. Parity items 89-91 added as 0.3 gates (no env var reaches the surface; the toggle is live, not restart-required; the inspector opens from the rail but lives in Diagnostics). |
| 48 | **arbitration** | header · §9 §2-budget · §9 line budget | arbitration 2026-09-12: **A12 drops `Shell/Lyrics.Stage.UI.cs`.** The immersive stage is `Shell/Stage.cs` (CORE) + `Shell/Stage.UI.cs`, chapter 21's, owner K, Wave 4, and it owns `StageChrome` / `StageIdentity` / `StagePanes` / `StageLayout` / `StageArm` / `StageInk` whole. The lyrics pane inside the stage lives in `Lyrics.UI.cs`, which is why the split is now **three** partials — `Lyrics.cs` 3,700 · `Lyrics.UI.cs` **3,500** · `Lyrics.Host.cs` 2,300 — and the honest total **≈10,000 → ≈9,500**. The ≈1,400 the dropped file carried splits as ≈900 genuinely this chapter's (the immersive surface + the stage's lyrics column, now inside `Lyrics.UI.cs`) and ≈500 that was **already** inside chapter 21's `Stage.UI.cs` 1,150 — the double count both chapters were told to remove. The `Shell/` subtotal line is restated as this chapter's **+4,400 delta with no new file**, because the block's subtotal is now recomputed centrally (A17). §1.2 needed no change: it already homed `Lyrics.Stage` in `Shell/Lyrics.UI.cs`. |

**Still UNVERIFIED after this pass** (stated in the chapter, not provable from source): the W11/W12/W13 *rendered* pixel
positions (they are arithmetic over verified constants, never captures); the "cover 232²" figure in W11 (chapter 21 owns
the art ladder); and the reference-capture provenance of every measured constant (luma 252/133/113/101/89/72, edge
energy 27/3.7/~0, the 2026-08-03 campaign) — the code cites those captures, and no capture is in this repo.

**consistency 2026-09-12:** header and §9's plan-table row asked for the CORE + Host half (`Lyrics.cs` + `Lyrics.Host.cs`) to be split off to a second owner. The plan keeps owner K single across all three lyrics files, alongside Rail/Deck/Stage/Video. Header's "Wave 4 owner K" line and the §9 plan-replacement table's Wave-4-owner row corrected to state single ownership.

**answers 2026-09-12: Q1 reverses this chapter's own §9 (a) proposal — the debug pill + overlay are KEPT, re-gated on `diag.developerMode` instead of `WAVEE_LYRICS_DEBUG`, which is the only thing actually deleted.** W21 is restated from "0.2.9 record, RETIRED in 0.3" to "KEPT in 0.3, gate changed"; W23 now hosts only the `FG_STAGE_RECTS` retirement, not the pill; §0 item 15, the §1.1 wireframe caption, both §3 debug token rows and parity item 89 are corrected to match. The pill's lines stay inside `Lyrics.cs`/`Lyrics.UI.cs` exactly as already budgeted — this decision changes the gate, not the line count.
