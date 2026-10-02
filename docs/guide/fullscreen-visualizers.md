# Full-screen Now Playing and its visualizers — developer guide

Wavee's full-screen stage is a true full-screen Now Playing view: the window goes borderless on its monitor, the title
bar, nav and player bar unmount, and one acrylic transport card sits over the music. Four modes (Lyrics, Visualizer,
Up next, Artist) share one morphing cover and one art-derived accent; in Visualizer mode one of eight faces moves on a
**real spectrum analysis of what is playing**, aligned to what the listener actually hears. This guide covers what the
app declares and how to diagnose it. The design, the code and every number are in
`docs/plans/wavee/fullscreen-flagship-implementation.md` (issue #166); the engine side is documented in the sibling
checkout — `..\fluent-gpu\docs\plans\media-playback-api-spec.md` §7.8 (the spectrum tier),
`..\fluent-gpu\docs\design\subsystems\pal-rhi.md` (`ISwapchain.IsOccluded` / `InputHooks.WindowOccluded`) and
`..\fluent-gpu\docs\guide\components-elements-layout.md` (`SeriesEl`).

The stage **follows the app theme**: its own colours come from `Design.StageInk`, whose two arms (dark ink over a dark
scrim, dark ink over a light veil) are picked by the active theme, and its Fluent controls paint their theme's chrome.
It cannot be pinned dark while the app is light: the engine has no per-subtree theme, so a slider or toggle on the stage
would still paint light-theme chrome. The global accent is never touched — the stage derives its own `AccentSet` from
the cover.

| Piece | File | Role |
|---|---|---|
| Modes, aspect classes, layout numbers, tone, entry rules | `Shell/Stage.cs` | CORE (engine-free, tested) |
| The stage tree, idle machine, key map, window fullscreen | `Shell/Stage.UI.cs` | UI |
| `Stage.Diagnostics` — the log events and the card's snapshot | `Shell/Stage.Host.cs` | SHELL |
| Kinds, demand tiers, bands normalisation, the eight face folds, `Model.Tick` | `Shell/Visualizer.cs` | CORE |
| `Slab`, `SeriesSource`, `Palette`, the 30 Hz `Clock`, the faces, the gallery | `Shell/Visualizer.UI.cs` | UI |
| Kind-237 waveform bands, beat grid | `Entities/Waveform.cs`, `Entities/Edges.cs`, `Spotify/Spotify.Decode.Analysis.cs` | data |
| Persisted preferences | `Platform/Prefs.Stage.cs` + the keys in `Platform/Platform.Settings.cs` | platform |
| The engine pulls (`AcquireSpectrum`, `CopySpectrum`, `SetSpectrumOffsetMs`, `SpectrumDiagnostics`) | `Playback/Playback.Audio.cs` | playback |
| The Diagnostics card | `Screens/Diagnostics.UI.cs` (`RuntimePageView.StageCard`) | UI |

## How it works, in one paragraph

While a spectrum lease is held, the engine's audio render thread copies every rendered block — **before** the master
gain — into a lock-free mono ring indexed in *content* frames. On the `FluentGpu.AudioClock` thread, ~60 times a second,
the engine picks the window centred on the audible instant (played frames, minus the device's stream latency and the
master chain's latency, minus the user's sync offset; it saturates at the newest rendered sample), runs a 2048-point
Hann FFT and folds it into 48 log-spaced bands in dB (+3 dB/oct tilt, clamped to −80…+6), and publishes them into a
double buffer. The UI never receives a push: `Visualizer.Clock` (one `UseInterval`, 30 Hz) *pulls* the front buffer with
`Playback.Audio.CopySpectrum`, normalises it in CORE (`Visualizer.Bands`: dB → 0..1, auto-gain, the user's sensitivity,
the prototype's 0.55/0.10 attack/release follower, peak holds), folds it per face through the one alloc-free
`Visualizer.Model.Tick`, and writes the result into the `Slab` inside **one** `Runtime.Batch`. Every face is built once
and *binds* `Transform` / `Opacity` / `Fill` props over the slab — compositor-only writes, no re-render, no layout;
Horizon and Aurora bind a `SeriesEl.Samples` thunk over a `SeriesSource`, which the engine draws as chunked
`DrawSeriesCmd`s. Nothing is faked: with no live spectrum the bands decay to the floor and the faces rest, or fall back
to Spotify's own data (below).

## What the app declares

### Modes and faces

`Stage.Mode : byte { Lyrics = 0, Visualizer = 1, Queue = 2, Artist = 3 }` and
`Visualizer.Kind : byte { Field = 0, Halo = 1, Horizon = 2, Matrix = 3, Aurora = 4, Spectrum = 5, Pulse = 6, Tape = 7 }`
are **persisted ints, append-only**; `Stage.ModeRules.Coerce` / `Visualizer.Catalog.Coerce` map anything unknown to
Lyrics / Horizon. The mode is a preference now (the stage re-opens where you left it), not session state. The Field is
*always* the backdrop under Lyrics, Up next and Artist; in Visualizer mode the chosen face takes the stage.

| Kind | Needs (`Catalog.NeedsOf`) | Engine lease | With no live FFT (Connect, `--fake`, muted, remote) |
|---|---|---|---|
| Field | `Breath` (RMS) | Level | breath holds at its rest opacity |
| Halo | `Spectrum` | Spectrum | ring bars rest at 0.1 scale, glow at rest |
| Horizon | `Precomputed` (kind-237 bands) | none | needs only the edge; an empty edge is a flat hairline |
| Matrix | `Spectrum` | Spectrum | all dots dim, peak dots at row 0 |
| Aurora | `Spectrum` | Spectrum | ribbons drift at their base amplitude |
| Spectrum | `Spectrum` | Spectrum | bars at the 0.02 floor, caps at 0 |
| Pulse | `Beats` → `TempoGrid` | none | both absent: rings paused, cover at scale 1 |
| Tape | `Breath` (RMS) + position | Level | reels keep spinning from position; meter at 0 |

### Demand tiers and leases

```
Visualizer.Demand.For(kind, visualizerMode, stageUp, playing, ownerUs, audioSupported, occluded, reduced)
  !(stageUp && playing && ownerUs && audioSupported && !occluded && !reduced) -> Tier.None
  !visualizerMode                                                              -> Tier.Level   (the base Field's breath)
  NeedsOf(kind) is Spectrum                                                    -> Tier.Spectrum
  NeedsOf(kind) is Breath                                                      -> Tier.Level
  otherwise (Horizon, Pulse)                                                   -> Tier.None    (no engine lease at all)
```

`Tier.Level` is `Playback.Audio.AcquireLevels()`; `Tier.Spectrum` is `Playback.Audio.AcquireSpectrum()` (which implies
the level tap). The lease is held by `Visualizer.Clock` for the *visible* consumer only; the gallery's eight previews
bind the same slab, so they cost nodes, never analysis. `Demand.Ticks` decides whether the 30 Hz clock runs: while the
stage is up and neither occluded nor reduced, and either playing or still settling (a release tail, coasting reels).
**Alt-tab is deliberately not an input**; occlusion and the OS reduced-motion setting are. The gallery's "Reduce motion"
(`Prefs.Stage.Calm`) is a *value* inside the model's `Input` (gain ×0.55, slower attack, no beat flash) — never a lease
input and never a hook branch.

### The fallback ladder (`Visualizer.Source { Breath, Live, Precomputed, TempoGrid }`)

```
live FFT  (local playback, ownerUs, Audio.Supported, lease held, not muted)
  -> kind-237 waveform bands (Edges.TrackWaveform, three 20 ms bands)          Horizon; the "level" for Tape / Field
       -> beats (Edges.TrackBeats, the audio-analysis route) -> else the tempo grid (kind 222, phase-locked to position)   Pulse
            -> Field breath at rest
```

The source is published per tick (`Slab.Source`) for the gallery captions ("From Spotify" / "Tempo grid" / "Idle") and
the Diagnostics card. Under Connect (play on another device) the stage falls back automatically — no crash, no lease.
Kind 237 is stored as `EdgeTable<WaveSample>` (three bytes per sample, capped at `WaveformBands.MaxSamples = 4096` by
max-pool decimation, indexed by *duration* so no fixed hop is assumed). The audio-analysis endpoint was probed before
anything depended on it (plan §4.5.3, `ops/headless/analysis.wh`); the tempo-grid fallback is live regardless.

### Layout, idle and accent

Four aspect classes, with hysteresis, from `Stage.Layout.Resolve(w, h, mode, galleryOpen, prev)` — Desktop,
Ultrawide (ratio ≥ 2), Portrait (ratio ≤ 0.8) and Compact (height ≤ 460 at ≥ 1:1, or width < 600: no pane, no gallery, a
96-DIP transport without the volume slider). Every authored number lives in `Stage.Layout`; the renderer lays out, it
never decides. Idle uses the engine's `PlayerChromeVisibility` with `PlayerChromeTiming.Default` (3000 ms): the top
bar, transport card and gallery unmount (`Flow.Show`), the cursor hides and a 3-DIP accent hairline shows the progress;
a pointer move, key or wheel brings everything back, and an open menu, the teaching tip, a scrub or a pause holds it.
The accent is `Design.StageInk.Accent(coverUrl)` → `AccentSet.From(accent)` once per (cover, `Tok.Epoch`); large fills
bind `Slab.Accent`, which the clock cross-fades over 600 ms on a track change (a bound channel snaps, so the fade is
the clock's).

### Settings keys (`Platform.Settings.cs`, one epoch: `Prefs.Stage.Epoch`)

| Key | Type | Default | Notes |
|---|---|---|---|
| `stage.mode` | int | 0 (Lyrics) | `Coerce`d |
| `stage.visualizer` | int | 2 (Horizon) | `Catalog.Coerce`d |
| `stage.sensitivity` | float | 1.0 | clamped to 0.3 – 1.5 |
| `stage.lyricsOverlay` | bool | true | lyrics over the visualizer |
| `stage.syncOffsetMs` | int | 0 | clamped to ±500; mirrored to the engine by `Playback.Audio.SetSpectrumOffsetMs` |
| `stage.calm` | bool | false | the "Reduce motion" switch |
| `stage.galleryOpen` | bool | true | |
| `stage.tipSeen` | bool | false | the first-Visualizer-entry TeachingTip |

Settings › Appearance › Full screen mirrors the gallery's four settings (plus the default visualizer). **Prefs are never
read on a hot path**: `SurfaceCore` reads them once per `Prefs.Stage.Epoch` into signals on `Stage.StageCtx`, and the
clock tick, every bind thunk and every band loop read those.

## Keys on the stage

| Key | Does |
|---|---|
| `Esc` | closes the stage |
| `F11` | closes the stage (the stage root takes it first; `Shell.ToggleVideoFullscreen` is the unfocused fallback — `Shell.FrameRules.F11(isFullscreen, videoActive, stageUp)`) |
| `Space` | play / pause, as on the player bar |
| `←` / `→` | seek −10 s / +10 s (`Shell.BarSeekBy`), as on the player bar |
| `↑` / `↓` | volume +5 % / −5 %, as on the player bar |
| media keys | arrive through SMTC regardless of focus — the stage needs no handler |

Any key, pointer move or wheel is idle activity. The doors in are the rail's lyrics header ⛶ and the player bar's Expand
⛶ (a single-click on the bar art still navigates — there is no double-click door); `Stage.Entry.CanEnter` lets an empty
stage open, exactly as the rail does. Esc and F11 restore the prior window placement — a maximized window stays
maximized.

## Diagnosing

**The card.** Settings › Privacy & diagnostics › Tools › Playback runtime opens the `playback-diagnostics` page; the
"Fullscreen & visualizer" card (`RuntimePageView.StageCard`) sits under the updates card and re-renders on every
`Stage.Diagnostics.Version` bump (Refresh re-reads the FFT figures):

| Row | Reads | Healthy on a real account |
|---|---|---|
| Mode | `Stage.Diagnostics.LastMode · LastKind`, a dash while the stage is closed | |
| Lease tier | `LastTier` (`None` / `Level` / `Spectrum`) | `Spectrum` on Halo/Matrix/Aurora/Spectrum, `Level` on Field/Tape, `None` on Horizon/Pulse; `None` under `--fake` |
| FFT per publish | `SpectrumInfo.FftMs` | ≈ 0.0x ms |
| Output delay | `SpectrumInfo.AlignFrames` ("frames behind newest") and the endpoint's queue depth | a small non-zero number; the queue depth is a *diagnostic*, the spectrum is not aligned by it |
| Data source | `LastSource` | `Live` on a spectrum face while playing locally; `Precomputed` on Horizon; `Precomputed` / `TempoGrid` under Connect |
| Spectrum publishes | `PcmAudioSession.SpectrumPublishes` | rising while a spectrum lease is held |

**Log events** (category `stage`, from `Stage.Diagnostics` in `Shell/Stage.Host.cs`; the Debug ones need the Verbose
level in the log viewer):

| Event | Level | Fields | Meaning |
|---|---|---|---|
| `stage.enter` | Info | `cause` (`rail-header`, `bar-expand`, …), `mode`, `kind` | the stage opened |
| `stage.exit` | Info | `cause` (`escape`, `f11`, `escape-shell`, `f11-shell`, …), `mode`; elapsed = dwell ms | the stage closed |
| `stage.mode` | Debug | `mode` | the selector changed |
| `viz.pick` | Info | `kind` | a face was chosen |
| `viz.lease` | Debug | `tier` | the held lease changed (`None` / `Level` / `Spectrum`) |
| `viz.analysis` | Debug | `source` | the data source changed |

**Things that look like bugs and are not.**

- *The picture does not follow the volume slider, but it does follow the EQ.* The tap sits immediately before the master
  gain, after the EQ/limiter. Mute (or volume 0) is published as a flag and shown as the idle breath. The one scoped
  exception: under a plain `Level` lease (Field/Tape outside a spectrum lease) the RMS is post-gain and *is* volume
  dependent; `SpectrumInfo.WindowRms` under a spectrum lease is pre-gain.
- *The sync offset.* Positive reads the analysis window earlier — Bluetooth headphones usually want a positive value. A
  negative offset **saturates at the newest rendered sample**, so it never blanks the picture; it just stops moving the
  window later than "now".
- *Minimizing or cloaking the window (Win+D, a virtual-desktop switch) pauses the clock* — `viz.lease` logs `None` and
  the lease is dropped — and restoring resumes it. A window **covering** Wavee does not pause it, and neither does
  alt-tab: "covered" is not observable on Wavee's composition swapchain, and the stage does not claim it
  (`InputHooks.WindowOccluded` follows `ISwapchain.IsOccluded`: minimized/cloaked/hidden only).
- *Reduced motion (the OS setting) stops the clock and the leases*; the faces render their rest pose, the cover FLIP
  snaps and the accent cross-fade lands on its target.
- *`--fake`*: `Audio.Supported` is true (the silent endpoint) but the spectrum is silence, so Halo/Matrix/Aurora/Spectrum
  rest, Horizon shows the fake triangle bands, Pulse rings on the seeded tempo and Field breathes at rest. The card's
  tier is `None`.

## Adding a face

1. **Name it.** Append a `Visualizer.Kind` int (never renumber — it is persisted), bump `Catalog.Count`, give it a
   `Catalog.NeedsOf` entry (and `UsesSeries` if it draws through `SeriesEl`), and add `stage.viz.<kind>` /
   `stage.vizSub.<kind>` to all three loc files (`en-US`, `nl`, `ko-KR`) and to the Settings picker's label list.
2. **Fold it in CORE** (`Visualizer.cs`). A static fold of the normalised bands (`Model.Level` / `Peak`, the low/mid/high
   averages, the position, the beat phase) into preallocated arrays or scalars on `Visualizer.Frame`, called from
   `Model.Tick`. `System`-only, no LINQ, no closures, no allocation after the constructor. Without a live spectrum it
   must *rest* (decay to the floor), never synthesise motion. Unit-test it in `Wavee.Tests/VisualizerModelTests.cs` —
   pure classes only, no engine mount, and never a test that reads production source text.
3. **Give it signals** (`Visualizer.UI.cs`). Scalars become `FloatSignal`s on `Slab`; a polyline becomes a
   `SeriesSource` (a fixed-capacity `float[]` plus a `Version` the bind thunk reads; `Publish()` bumps it). The clock
   writes them all inside its one `Runtime.Batch` with `SetIfChanged`.
4. **Build it once, bind it.** The face is a `Visualizer.Face(Kind, Slab, in Palette, in FaceSpec)` arm whose nodes bind
   `Prop.Of(() => slab.X.Value)` on `Transform` / `Opacity` / `Fill` (or `SeriesEl.Samples`). Component props freeze at
   mount, so a face never takes data as a field — only signals and thunks. Provide the gallery-preview variant (fewer
   parts, same slab).
5. **Mind the budgets.** At most 600 animated nodes per face (Spectrum, the largest, is 144) and about 700 for the whole
   stage with the gallery open; 30 Hz; scalar maths only; `Peek`, never `Value`, inside the tick; zero allocation per
   tick; previews cap their parts (Halo 36 bars, Spectrum 24 bars without reflections, Matrix 16 columns).
6. **Declare what it needs and nothing more.** The kind's `Need` is what `Demand.For` turns into a lease; a face that
   can run on kind-237, the beat grid or the level tap should not hold a spectrum lease.
7. **Walk it.** `dotnet run --project src/apps/Wavee -- --fake` for the rest pose and the caption, then a real local
   track for the live motion (the Diagnostics card should show the tier you declared), at 1440×900, ultrawide, portrait
   and 640×400.
