# Fullscreen Now Playing — flagship layout + real-time visualizers — design & implementation plan

Repo: `C:\wavee\waveemusic` (app, HEAD `c060e9b7`), engine: `C:\wavee\fluent-gpu` (HEAD `c50e900d`). All paths below are absolute or repo-relative to those two roots. Every claim carries a `file:line` from the current trees. **Revision 2 (2026-10-02):** every finding of the Opus 5.5 review is resolved and logged in §8. Issue: **#166** (milestone 0.3 Crest). The approved architecture guardrails this plan fills in are `C:\Users\ChristosKarapasias\.claude-legacy\plans\design-this-fully-using-adaptive-lake.md` (§A engine, §B data, §C app UI); the visual + interaction spec is the Flagship board (`…\scratchpad\canvas\project\Flagship.dc.html`, 1920×1080), with `Visualizers.dc.html` for the eight faces' math and `Main.dc.html` for the aspect classes. The prototypes are translated to engine primitives, never ported as CSS.

> Conventions used below. **CORE** = `Shell/X.cs`, engine-free (`System` only), alloc-free after warm-up on every tick path, `public` because `Wavee.Tests` is a plain `ProjectReference` (no `InternalsVisibleTo`). **UI** = `Shell/X.UI.cs`, binds and never decides. **SHELL** = `Shell/X.Host.cs`. File headers carry `Role / Owner / Wave / Budget / Spec` exactly as `Shell/Stage.cs:1-8` does today. Each code block compiles against the sources as they are at the two HEADs above; where a block REPLACES code, the replaced line range is stated. Deviations from the approved guardrails are marked **DEVIATION** inline and collected in §7.

---

## 0. The owner's ask, restated as requirements

1. **True fullscreen.** Entering the stage calls the engine's window fullscreen (the video precedent `Features/Video/Video.UI.cs:1213-1237`), restores the prior placement on exit, and unmounts the shell chrome and the docked player bar — no second transport, no 48/72-DIP pass-through bands.
2. **Fluent flagship layout.** A 48-DIP top bar with a `SelectorBar` pill — **Lyrics | Visualizer | Up next | Artist** — centred, the brand line left, a gallery toggle / mini-player / "Exit full screen Esc" cluster right; an acrylic transport card (seek + times, play cluster, like/mute/volume/device/more) bottom; the identity (cover, title, meta, chips) left; the pane right.
3. **Morphing cover.** One node: 560-square hero (desktop) in Lyrics / Up next / Artist, a 96-square thumbnail at top-left (inside a 460×136 acrylic now-playing card) in Visualizer. The move is a layout FLIP on the same node; the title/meta/chips reflow with it.
4. **Art-derived, per-surface accent.** The stage's `AccentSet` comes from the cover palette and is passed into control styles; the global `Tok.SetAccent` is never called. Large accent fills cross-fade on a bound `Prop<ColorF>`.
5. **Transport card = acrylic + the stock controls.** Seek is the player bar's own `Shell.SeekBar()` (`BarSeekRail`, stretched by the card's layout — D7); volume is the WinUI `Slider` at a fixed 128 DIP tinted with the surface accent, as are the toggles and the selector pill. The seek rail keeps the bar's own look (its palette is the bar's; no accent seam exists and none is added).
6. **Eight visualizers with a gallery.** Field, Halo, Horizon, Matrix, Aurora, Spectrum, Pulse, Tape — every face ships; the gallery (an acrylic pane, 444 wide, 2-column `ItemsView` with `SelectorVisual.Border`/`Check`) shows **live mini previews** of all eight, plus settings: **Sensitivity** (slider 0.3–1.5), **Lyrics over visualizer** (toggle), **Reduce motion** (toggle), **Sync offset** (slider, ms).
7. **Idle.** After 3 s without pointer/key/wheel activity (the engine's `PlayerChromeTiming.Default`, D3) the chrome (top bar, transport card, gallery pane) unmounts via `Flow.Show` exit, the cursor is hidden, and a 3-DIP accent hairline progress bar shows along the bottom edge; any pointer move / key / wheel wakes it; an open menu, the tip, a scrub or pause hold it.
8. **TeachingTip** on first entry into Visualizer mode ("Pick your visualizer"), anchored to the gallery toggle, dismissed by "Got it" / ✕, never shown again (persisted).
9. **Adaptive layout** with four aspect classes — desktop, ultrawide (≥ 2:1), portrait (≤ 4:5), compact (height ≤ 460 at ≥ 1:1) — derived from the prototypes' container queries, art sized from height, no clipped control at any class.
10. **Deck faces on the real FFT.** `Deck.LevelModel`'s synthesised sine bands are deleted; the rail deck reads the same spectrum frames the stage does.
11. **Perf fixes carried in.** No 33 ms drift interval re-compositing the window; the shell body stops recording under the stage; current-track reads are narrowed to the playing slot's row; the backdrop is baked once per track with the old image kept until the bake lands; one `EdgeFadeSpec` per viewport; stage lyrics render with blur strength forced to 0.
12. **Engine audio.** A real radix-2 FFT (2048, Hann, 48 log bands, dB with +3 dB/oct tilt) on the AudioClock thread, fed by an SPSC ring the RT thread fills only while a **spectrum** lease exists; latency-aligned by `OutputDelayFrames` plus a user sync offset; double-buffered magnitudes behind the existing seqlock; pre-master-gain so the picture does not follow the volume slider.
13. **Engine drawing.** A `SeriesEl` element for dynamic polyline/area geometry from a bound sample source (no per-frame `PathData`), used by Horizon and Aurora; registered in the canon (`SPEC-INDEX.md` §2 + ownership map), TerraFX-free in Controls, zero-alloc in VerticalSlice phases 6–13, screenshot-checked.
14. **Occlusion.** A `WindowOccluded` input hook exposes the primary swapchain's `IsOccluded` (`Frame.OccludedLatched || Frame.LastPresentStoodDown`); visualizers pause when the window is minimized / cloaked / hidden (and DXGI-occluded where the swapchain reports it — **not** on Wavee's composition swapchain, where "covered by another window" is not observable, §1.3) but **not** on alt-tab deactivation.
15. **Data.** Kind-237 waveform kept as **three bands at 50 Hz** in a banded edge; the drawer's 220 columns derived by a pure helper. A new spclient route to `/audio-attributes/v1/audio-analysis/{id}` decoded by hand into a `TrackBeats` edge (beats/bars/sections), with a tempo-grid fallback from the Audio edge (kind 222) phase-locked to position. A wave-0 probe confirms the endpoint before anything depends on it.
16. **Settings persist** through `Platform.Keys` + `Prefs` epochs: mode, visualizer kind, sensitivity, lyrics overlay, sync offset, tip-seen. Appearance rows in `Settings.UI.Appearance.cs`; loc keys in `en-US`, `nl`, `ko-KR`.
17. **Diagnostics.** A "Fullscreen & visualizer" card (FFT ms/publish, lease tiers, output-delay frames, data source) and `Log.Event` keys `stage.enter` / `stage.exit` / `viz.lease` / `viz.analysis`.
18. **Tests** for every pure class (app) and for the FFT / ring / bands / latency / zero-alloc (engine). No source-text tests. Existing Stage / Deck / Shell tests updated, obsolete ones deleted.
19. **No legacy paths.** `Stage.cs` / `Stage.UI.cs` are replaced outright; `Band.CaptionH/PlayerBarH`, `DriftClock`, `Drift`, the pass-through bands, `Pane.Toggle`, the fake deck bands, `Edges.TrackWaveform`'s 220-column decode are deleted, not kept behind a flag.

---

## 1. Current behaviour map

### 1.1 The stage today (`Shell/Stage.cs` 491 lines, `Shell/Stage.UI.cs` 999 lines — both replaced outright)

**CORE `Stage.cs`** (header `:1-8`: Role CORE / Owner K / Wave 4 / Budget 500 / Spec ch 21 §9.5):
- `Stage.Control` flags (`:51-62`) and `Stage.Layout` (`:79-356`) — the wide⇄compact allocator: `WideEnterW 600` (`:93`), `PromotionHysteresisW 40` (`:96`), `WideColumnW 352` (`:103`), `WideArtW 300` (`:122`, **the 300-DIP art cap**), `CompactArtW 64` (`:125`), the height fold ladder (`ColumnChromeH` `:194-200`, `Resolve` `:320-349`), the scrim alpha ladder (`:224-266`).
- `Stage.Pane` (`:363-374`): `Lyrics = 0`, `Queue = 1`, a static `Signal<int> Current` and `Toggle()`. Read by `Lyrics.UI.cs:65-66` (`s_stageVisible`) and `StageTests.cs:341-350`.
- `Stage.Band` (`:384-405`): **`CaptionH 48`** (`:387`) and **`PlayerBarH 72`** (`:389`) — the two pass-through bands; `TopBandH 88 / CompactTopBandH 56` (`:391`), `PivotBandH 72` (`:393`), `VolumeTrackW` (`:396`), `BodyH`/`ColumnAvailH` (`:401-404`).
- `Stage.Transport` (`:412-423`): `PrimaryEnabled`, `ShowsQualityBadge`, `UsesTitle` — the three predicates survive (moved, see §4.6).
- `Stage.DriftClock` (`:430-457`) and `Stage.Drift` (`:462-490`): the 37 s / 53 s sinusoid pose, `IntervalMs = 33` (`:465`), `Runs`/`Worth` gates.

**UI `Stage.UI.cs`** (header `:1-9`: Role UI / Owner K / Wave 4 / Budget 1150):
- Mount point `Stage.View()` (`:48`) → `SurfaceCore` (`:244-403`); `EnterTerminal`/`ExitTerminal` (`:52-57`) read `Design.Reduced` as a value. `QueuePaneBody` (`:60`) is installed by `Entities/Queue.UI.cs:65`; `NowPlayingMenu` (`:64`) by `Platform/Actions.UI.cs:958`. Both installers are kept (§6 contract).
- `SurfaceCore.Render` (`:251-328`): the layout effect (`:258-267`), `UseInterval(DriftTick, Drift.IntervalMs, enabled: runDrift)` **at `:282` — the 33 ms recomposite**; Escape handled on the root's `OnKeyDown` (`:290-295`) by writing `Shell.Ui.ImmersiveLyrics.Value = false`; the two pass-through bands `new BoxEl { Height = Band.CaptionH, HitTestPassThrough = true }` at **`:304`** and `Height = Band.PlayerBarH` at **`:325`**.
- `Backdrop` (`:330-369`): `Ui.Image(art, ImageFit.Cover, …, transition: ImageTransition.Fade(220f))` with `BakedBlur = new BakedBlurSpec(Drift.SigmaDip, Drift.ResolutionScale)` (`:333-338`) — baked once per art, but the `ImageEl` is **swapped** on a url change (the Fade reveal shows the new crisp placeholder before the bake lands = the "crisp flash"); the 1.30× paint overscale (`:351-356`); `DriftTick` writes `NodePaint.LocalTransform` directly (`:374-390`).
- `CurrentTrack()` (`:219-224`) reads **`Entities.Current.Tracks.Changed.Value`** (`:222`) — every track-table publication re-renders every reader (`SurfaceCore`, `IdentityCore` `:472`, `MetaLink` `:649`, `PanesCore` `:884`, `QueueSkin` `:931`). `CurrentAccent(track)` (`:227-232`) subscribes `Palette.Watch(url)` and returns `Design.StageInk.Accent(url)`.
- `TopBar` (`:408-419`), `SecondaryLineFab` (`:426-438`), `StageBody` (`:442-460`), `IdentityCore` (`:466-502`), `WideColumn`/`CompactHeader` (`:508-564`), `IdentityRow` (`:567-602`), `SeekBlock` (`:605-626`, reuses `Shell.SeekBar()` / `Shell.TimeText`), `QualityBadge` (`:629-641`), `MetaLink` (`:644-684`), `TransportRow` (`:687-703`), `VolumeRow` (`:709-742`, `Slider.Create(level, …, length: Band.VolumeTrackW, thickness: 20f, style: style)` at `:737` — a **fixed** length), `MuteGlyph`, `DeviceLine` (`:756-814`, a `MenuFlyout` over `Shell.DevicePickerMenuItems`), `OverflowButton` (`:832-873`).
- `PanesCore` (`:879-923`): both panes mounted, a 250 ms opacity cross-fade (`:898`, `:904`), `Lyrics.StagePane()` at `:899` (its column is **capped at 700**: `Lyrics.UI.cs:56` `StageColumnMaxW = 700f`, `:88`), the pivot links (`:910-919`). `QueueSkin` (`:927-975`), `AutoplayRow` (`:978-998`).

**Shell seams the stage hangs off** (verified here; the rest in §1.2):
- Mount: `Shell/Shell.UI.cs:500-504` — `Flow.Show(Ui.ImmersiveLyrics…)` with `Enter = Stage.EnterTerminal, Exit = Stage.ExitTerminal, Children = [Stage.View()]`.
- Reused by the new stage, unchanged: `Shell.SeekBar(PlayerChromeFeed? feed = null)` (`Shell/Shell.PlayerBar.UI.cs:87`), `Shell.TimeText(bool remaining, ColorF? ink = null)` (`:92`), `Shell.TogglePlayPause(string cause)` (`:56`), `Shell.ToggleShuffle()` (`:58`), `Shell.CycleRepeat()` (`:61`), `internal static PlayerBarFacts TransportFacts(bool track = true)` (`:66`), `Shell.PlayerBarFacts(PlayerState State, bool CanTransport, bool PrevEnabled, bool NextEnabled, PrimaryVerb Primary)` (`Shell/Shell.PlayerBar.cs:90-95`), `Shell.DevicePicker.IsRemote(Playback.Owner, int, ReadOnlySpan<Playback.Devices.Row>)` (`Shell/Shell.DevicePicker.cs:41`), `Shell.DeviceRoster.RemoteSlot` (`Shell/Shell.PlayerBar.cs:376`), `Shell.GoTo(in Route, NavOrigin?, NavTransitionKind)` (`Shell/Shell.Host.cs:808`), `Controls.ArtUrl(StringId)` (`Platform/Controls.cs:92`), `Controls.Artwork(string? url, float width, float height, float corners, string? morphKey = null, int decodePx = 0, float saturation = 1f, float scale = 1f, string? blurHash = null)` (`:202-203` — **`morphKey` tags a shared-element participant and mounts no shimmer**, `:198-201`), `Palette.Watch(ReadOnlySpan<char>)` (`Entities/Palette.cs:415`), `Design.StageInk.Accent(ReadOnlySpan<char>)` (`Platform/Design.cs:644`), `Design.StageInk.ArtStandIn` (`:652`), `Design.Reduced => FgMotion.ReducedMotion` (`:1515`), `Design.Motion.{Faster 83, Fast 167, Standard 250, Slow 333}` (`:1486-1495`), `Design.Entrance.Row(int)` (`:1560`), `Design.FrameTime.NowQpc` (`:1637`).
- Track rows: `Track(int slot)` (`Entities/Track.cs:294`) with `Slot` (`:298`), `IsValid` (`:303`), `Id` (`:324`), `Title` (`:335`), `ArtistLineId` (`:337`), `ImageId` (`:338`), `Album` (`:341`), `DurationMs` (`:343`). **There is no per-row change signal**: `Publishable.Changed : Signal<uint>` is one per table (`Entities/Entities.cs:897`), and the per-row `Version : Column<uint>` (`:964`) "bumps on every write to the row". `Entities.TableFor(EntityKind)` (`:2275`). The deck already narrows by subscribing to `table.Changed` only while a row is unfilled (`Shell/Deck.UI.cs:575-578` `WatchRow`); the engine's `Memo<T>` is equality-gated (`..\fluent-gpu\src\FluentGpu.Engine\Foundation\Signals\Memo.cs:27, :75` — "EQUAL ⇒ silence"), which is what §4.6 builds the slot-row memo on.

### 1.2 Engine audio today (`..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\…`, all `namespace FluentGpu.Media;`)

Corrections to the fact sheet, verified: `PublishVisualizer` is a private method of **`PcmAudioSession`** (`Audio/PcmAudioPlayer.cs:1107-1114`), not of `AudioFeedThread`; it is called from `Advance` in the `Playing` case only (`:1418-1423`, after `PublishPosition`). `StreamLatencyFrames` is on the clock seam `IAudioClockSource` (`MediaSeams.cs:352`) and forwarded by the nested `SessionAudioClock` (`PcmAudioPlayer.cs:323-334`, `:326`); `PositionTracker` is at `:467`. `TapBlock` has one definition (`:1594-1612`); `:1533` is its call inside `RenderBlock`.

- **Threads** (`Audio/AudioFeedThread.cs:486-488`): `FluentGpu.AudioWorker`, **`FluentGpu.AudioClock`** (`ClockLoop` `:553-566`, `ControlTickOnce` + `Thread.Sleep(15)` at `:562` ⇒ ~60 Hz), **`FluentGpu.AudioRT`** (`RtLoop` `:518-532`, ~10 ms blocks, up to 3 per wake). `ControlTickOnce` (`:456-464`) → `TickControl` → `Advance(renderInline:false)` → `PublishPosition` (`:1614-1626`, refreshes `_playedFrames` via `Interlocked.Exchange` at `:1619-1620`) → `PublishVisualizer`. So the FFT home is the clock thread, where allocation and locks are legal; `_playedFrames` is fresh at that moment.
- **RenderBlock** (`:1456-1547`): `buf = _mixBuf.AsSpan(0, frames * _format.Channels)` (`:1517`), `AudioTripwire.BeginBlock()` (`:1520`), `_mixer.Render(buf, frames, ctx)` (`:1522`), **`_masterGain.Process(buf, buf, frames, ctx)` at `:1529`** (`_masterGain` is `GainStage` `:299`, target `_muted ? 0f : _volume` at `:1521`), `_masterChannel.Process` (`:1530`), `graph.RenderMaster` (`:1531`), `_transport.Apply` (`:1532`), **`TapBlock(buf, frames)` at `:1533`**, `AudioTripwire.EndBlock()` (`:1535`). Block sizes vary (clamped `:1497-1516`); a block is tapped exactly once (`_pendingFrames` early return `:1471`); `_pendingFrames == 0` whenever a block is tapped. `_volume`/`_muted` are `:414-415`; `SetMuted` `:1248`.
- **TapBlock** (`:1594-1612`): `if (_liveEffects is not AudioEffects ae) return; long epoch = ae.VisualizerDemand(Volatile.Read(ref _visualizerSource)); if (epoch == 0) return;` then RMS + peak over the interleaved block, `_meterSamples += n`, `_tap.Publish(rms, peak, epoch)`. Post-everything; alloc-free; inside the tripwire (`Audio/AudioTripwire.cs:35-45`: 0 managed bytes, 0 locks, 0 blocking calls, ≤ 50 ms, all `[Conditional("FG_AUDIO_TRIPWIRE")]`).
- **AudioLevelMailbox** (`Audio/AudioLevelMailbox.cs:1-29`): an `internal struct` seqlock with one packed slot (`_version, _bits, _epoch`); `Publish(float rms, float peak, long epoch)` (`:10-17`), `TryRead(out rms, out peak, out epoch, out version)` (`:18-28`). It cannot carry an array.
- **AudioEffects** (`MediaEffects.cs:98-185`): `_visualizerGate` (`:100`, "never acquired on the audio render thread"), `_visualizerConsumers` (`:101`), `AcquireVisualizer()` (`:108-119`), `ReleaseVisualizer` (`:121-129`), `BindVisualizerSource()` (`:131-140`), `VisualizerDemand(long source)` (`:142-143`), **`internal bool PublishVisualizerFrame(long source, long epoch, float rms, float peak)` (`:145-153`) which hard-codes `ReadOnlyMemory<float>.Empty`**, `VisualizerLease` (`:155-159`), `VisualizerView` (`:163-167`, `Value`/`Peek()` under the gate), `_visualizer` (`:182`), `Visualizer => _visualizerView` (`:184`). `VisualizerFrame(ReadOnlyMemory<float> Magnitudes, float Rms, float Peak)` with `Silence` (`:17-21`). `IAudioEffects` (`:74-94`) exposes `Visualizer` + `AcquireVisualizer()`; `NullAudioEffects` (`:189-215`) returns a shared `EmptyLease`.
- **Session wiring**: `BindEffects(IAudioEffects)` (`:1003-1009`) → `ActivateVisualizerSource()` (`:1014-1018`). `Format => _format` (`:469`), `MixFormat(int SampleRate, int Channels)` (`MediaSeams.cs:280`). `SubmittedFrames`/`PlayedFrames`/`RenderEpoch` (`:354-359`), `ConsumeSeqFrames => _mixer.ConsumeSeq` (`:690`), `_pendingFrames` (`:335`, RT-owned). **Frame domains**: `RenderBlock` builds `ctx = new BlockCtx(_mixer.ConsumeSeq, …)` at `:1518` BEFORE `_mixer.Render` advances `ConsumeSeq` (`CrossfadeMixer.cs:316`) — `BlockCtx.StartFrame` (`AudioGraph.cs:19-20`, a `ref struct`, only the `long` may leave the block) is the CONTENT index of `buf[0]`. `CmdReset` (`:950-968`) zeroes `_submittedFrames = _playedFrames = _deviceFrameOrigin = 0` (`:962`) AND `_mixer.ConsumeSeq = 0` (`:966`) and bumps `RenderEpoch` (`:967`); `RecoverStarvation` (`:1294-1315`) re-anchors `_deviceFrameOrigin` and bumps `RenderEpoch` (`:1311-1312`) without zeroing; `RebuildSink` bumps `RenderEpoch` and zeroes the submitted/played counters (`:1727-1728`) but NOT `ConsumeSeq` — the one place the two domains diverge, which §2.7's `domainOffset` absorbs. The master chain's latency is `_graph.Live.TotalLatencySamples` (`AudioGraphHost.cs:20-33`; folded into the position at `:1538`).
- **Ring idiom to mirror**: `Audio/PcmRing.cs` — SPSC, power-of-two mask, monotonic `long` head/tail with `Volatile` fences (`:14-97`).
- **No FFT exists anywhere** (grep over `src/**/*.cs` for fft/magnitudes/spectrum/hann/twiddle: only the reserved `Magnitudes` field and `Controls/Charts/Waveform.cs:8` `WaveformModel(ReadOnlyMemory<float> Peaks, int Bars = 64)`, a static bar strip).
- **App side**: `Playback.Audio.Levels => s_effects.Visualizer` (`src/apps/Wavee/Playback/Playback.Audio.cs:225`), `AcquireLevels() => s_effects.AcquireVisualizer()` (`:229`), `static readonly AudioEffects s_effects` (`:312`), `static PcmAudioSession? s_session` (`:317`) under `s_gate` (`:311`). The media-spec's own intent is already "the `Tap` node's lock-free ring; a non-RT tick runs the FFT" (`..\fluent-gpu\docs\plans\media-playback-api-spec.md:783-784`); `SPEC-INDEX.md:97` owns the media API row; `docs/design/subsystems/media-pipeline.md` does not mention the visualizer today.
- **Tests to extend**: `FluentGpu.Engine.Tests` is xUnit v3 with `InternalsVisibleTo` from the engine (`FluentGpu.Engine.csproj`), `ConcurrentGarbageCollection=false`; the zero-alloc pattern is `AudioVisualizerDemandTests.cs:147-162` (`Mailbox_SteadyPublishAndRead_AllocateNothing`); the headless session factory is `:249-259` (`HeadlessAudioEndpoint`, `SignalGeneratorSource(2, 48000, 440, 0.3f, 480000)`, `maxBlock: 512`); the control-side publish needs `ConnectSignals(new MediaSignalSink(new MediaPlayerCore()))` + `PlayAsync()` + `PumpAudio(512)` ×8 (`AudioEffectsLiveTests.cs:12-25`, `:71-84`).

### 1.3 Shell seams (`Shell/Shell.cs`, `Shell/Shell.UI.cs`, `Shell/Video.UI.cs`, entry points)

- **The mount** (`Shell.UI.cs:496-513`): `ZStack(tinted, Flow.Show(static () => Ui.ImmersiveLyrics.Value, new BoxEl { …, Enter = Stage.EnterTerminal, Exit = Stage.ExitTerminal, Children = [Stage.View()] }), Overlays(), FileDropLayer(), CommandPalette(), Video.PipLayer(), Video.FullscreenLayer(), …)`. `tinted` (`:550`) is `new BoxEl { Grow = 1f, ZStack = true, …, Children = [MaterialLayer(), column] }`; `column` (`:457-469`) carries `OnKeyDown = OnShellKey` and `Children = [Chord(…)×14, Flow.Show(s_chromeMounted, ChromeRow()), ContentRegion(vp), Flow.Show(s_chromeMounted, PlayerBarDock())]`. `ContentRegion(IReadSignal<Size2> vp)` (`:521-…`) is `ZStack(new BoxEl { MorphId = ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true, … })`.
- **The chrome predicate** `s_chromeMounted` (`:647-648`): `FrameRules.ChromeMounted(Video.PlacementCore.Resolve(Video.State.Surface.Value))`; `FrameRules.ChromeMounted(Video.SurfacePlacement resolved) => resolved != Video.SurfacePlacement.Fullscreen` (`Shell.cs:1883`). Nothing hides the chrome for the stage today — the stage paints over the content region only.
- **`Ui.ImmersiveLyrics`** (`Shell.cs:1098`, `Signal<bool>`) — writers: `Rail.UI.cs:267` (⛶, true), `Shell.UI.cs:783` (Esc, false), `Stage.UI.cs:294` and `:417` (false). Readers: `Shell.UI.cs:500`, `:779`, `Lyrics.UI.cs:61`, `:66`, `Video.UI.cs:758` (the docked video hole is "covered"), `ShellFrameRulesTests.cs:85` (the `EscapeAction` enum only). The name stays (see D12) — it is the one shell fact "the fullscreen stage is open".
- **Keys**: F11 is an accelerator chord `FullscreenChord = new(Keys.F11, KeyModifiers.None)` (`Shell.UI.cs:176`), mounted as `Chord(FullscreenChord, ToggleVideoFullscreen)` (`:469`; `Chord` builds a zero-size `BoxEl { Accelerator = chord, OnClick = onFire }`, `:186-187`); `ToggleVideoFullscreen()` (`:761-770`) switches on `FrameRules.F11(Video.State.Resolved == Video.SurfacePlacement.Fullscreen, Video.State.IsActive)` (`Shell.cs:1907-1908`: Exit if fullscreen, Enter if a video is active, else None). Esc and Space bubble to `OnShellKey` (`:772-798`): `FrameRules.Escape(false, PaletteOpen.Peek(), Ui.ImmersiveLyrics.Peek(), videoFullscreen)` (`Shell.cs:1892-1897`) → `CloseImmersiveLyrics` sets the signal false. The stage root ALSO handles Esc (`Stage.UI.cs:290-295`). `Keys.F11 = 122`, `Keys.Escape = 27` are `const int`s on `FluentGpu.Foundation.Keys` (`..\fluent-gpu\src\FluentGpu.Engine\Foundation\Events.cs:213`); `KeyEventArgs { int KeyCode; KeyModifiers Mods; bool Handled; … }` (`:81`). Pointer press carries `ClickCount` (`Element.cs:188` `OnPointerPressed : Action<PointerEventArgs>?`, `Events.cs:117`).
- **The video fullscreen precedent** (`Video.UI.cs:1198-1262`, `FullscreenSurface`): `priorOsFullscreen = UseRef(false)`; `UseLayoutEffect(() => { priorOsFullscreen.Value = hooks.IsWindowFullscreen?.Invoke() ?? false; if (!priorOsFullscreen.Value) hooks.WindowSetFullscreen?.Invoke(true); return () => { if (!priorOsFullscreen.Value) hooks.WindowSetFullscreen?.Invoke(false); }; }, DepKey.Empty)` (`:1213-1218`); a focus scope with `PushFocusScope`/`PopFocusScope`/`RestoreFocus` (`:1221-1236`); Esc/F on the root (`:1247-1253`). `InputHooks.IsWindowFullscreen : Func<bool>?` / `WindowSetFullscreen : Action<bool>?` (`..\fluent-gpu\…\Hooks\Context.cs:264-265`), host-wired to `_window.IsFullscreen` / `_window.SetFullscreen` (`Hosting\AppHost.cs:2945-2946`); `Win32Platform.SetFullscreen` (`FluentGpu.Windows\Pal\Win32Platform.cs:944-981`) saves style/rect/zoomed and restores them. `Video.State.Resolved` (`Video.Host.cs:84`), `IsActive` (`:87`), `EnterFullscreen()/ExitFullscreen()` (`:129-130`).
- **Entry points today**: only the Lyrics rail header ⛶ (`Rail.UI.cs:267`, `HeaderButton(Icons.FullScreen, …, static () => Shell.Ui.ImmersiveLyrics.Value = true)`). The player-bar art (`Shell.PlayerBar.UI.cs:393-402`) navigates to the context on click and carries **no `MorphId`**; the Expand slot (`:597-600`) toggles `RailMode.NowPlaying`. `Controls.Artwork(…, morphKey:)` tags an image as a shared-element participant (`Platform/Controls.cs:198-203`); `Design.MorphKeys.For` knows album/playlist keys only, no track key.
- **Shared-element flight**: `SharedTransition.Begin : Context<Action<string>?>` (`..\fluent-gpu\…\Hooks\SharedTransition.cs`); `Begin(key)` snapshots the live tagged source only when `Paint.ImageId != 0` — an IMAGE node (`Animation\ConnectedAnimation.cs:246-250`); the overlay flies when a like-tagged live node appears (`Tick65`); snapshots expire after 30 frames; `ReducedMotion` skips capture. **Reverse capture is not implemented** (`CaptureOnLeave` `:238-241` only removes the tag; `OnNodeParked` `:231` is a no-op), so the exit flight needs an explicit `Begin` before the stage unmounts.
- **Idle machine**: `FluentGpu.Controls.Media.PlayerChromeVisibility` (`..\fluent-gpu\src\FluentGpu.Controls\Media\PlayerChromeVisibility.cs`, 304 lines): `PlayerChromeTiming(IdleHideMs, TouchIdleHideMs, KeyboardIdleHideMs, LeaveHideMs, CursorTrailMs, DeadzoneDip)` with `Default` = 3000/4000/4000/150/400/3; ctor `(in PlayerChromeTiming timing, double nowMs)` (`:73`); inputs `PointerMoved(x, y, nowMs)`, `Activity(ChromeActivity, nowMs)`, `Tapped`, `PointerLeft`, `SetPointerOverControls(bool, nowMs)`, `SetScrubbing`, `SetMenuOpen`, `SetKeyboardFocusInControls`, `SetPlayback(ChromePlayback, nowMs)`, `SetCursorMayHide(bool, nowMs)`; outputs `ChromeVisible`, `CursorHidden`, `NextWakeMs`; `bool Tick(double nowMs)`. It is a pure clocked machine: the owner arms ONE timer at `NextWakeMs` (`+∞` = nothing pending). The engine's own owner is `MediaPlayerElement` (`MediaPlayerElement.cs:383-416, :726-730`): `_wake = UseTimeout(_onWake, delay, DepKey.Empty)`, the machine is created with `_wake.NowMs` (the HOST TIMER clock — `TimerHandle.NowMs`, `RenderContext.Timers.cs:54` → `HostTimerQueue.NowMs`, Stopwatch ms on a real window, the frame clock headless), and `Sync()` calls `vis.Tick(now)`, IGNORES its bool, publishes `ChromeVisible` value-gated, applies the cursor EVERY sync, and re-arms `_wake.RestartIn(due − now)` from `NextWakeMs`. Initial state: visible, Playing, cursor may NOT hide. `PlayerChromeFeed` (`PlayerChromeFeed.cs:19-48`: `Activity/SetPointerOverControls/SetPressed/SetScrubbing/WindowMoveStarted`) forwards to an `internal MediaPlayerElement? Owner` — an app-created feed is INERT (no `SetMenuOpen` either), so `Shell.SeekBar(PlayerChromeFeed?)` (`Shell.PlayerBar.UI.cs:87`; `BarSeekRail` calls `_chromeFeed?.SetScrubbing(…)` at `:1715/:1739/:1746`) cannot drive the stage's machine as-is (§4.12 adds an `onScrubbing` seam). Cursor: `InputHooks.SetCursorOverride : Action<object, CursorId?>?` (`Context.cs:247`), `CursorId.Hidden = new(11)` (`Foundation\Platform.cs:26`). `Design.FrameTime.NowMs` (`Platform/Design.cs:1634-1644`) is a `long` on the PREDICTED present time — not the timer clock.
- **Occlusion**: the DXGI latch is per target, on `D3D12Swapchain` (`D3D12Device.cs:4587`, `: ISwapchain` only) — `internal readonly TargetFrameState Frame` (`:4619`; `TargetFrameState.cs:46` `internal bool OccludedLatched, LastPresentStoodDown, …`). `Present(D3D12Swapchain target)` (`:3277`): a covered/iconic/cloaked HWND stands the present down (`:3297` → `StandDownPresent`, `:3463-3469` sets `Frame.LastPresentStoodDown`); `DXGI_STATUS_OCCLUDED` from `Present` sets `Frame.OccludedLatched` (`:3340-3345`), cleared by the `PRESENT_TEST` probe (`:3305-3316`). **It is NOT reliably returned for `CreateSwapChainForComposition`** (`:3443-3445`; the comments at `:3338-3339` say composition swapchains "often never return it"), and Wavee IS composited: `FluentApp.cs:331` `new D3D12Device(strings, composited: o.Mica, …)` with `Mica = true` (`:739`), `Shell.Host.cs:249-263` never clears it. So "covered by another window" is NOT observable for Wavee; minimized / cloaked / hidden IS (`IsHwndCovered` `:3456-3461` + the stand-down). `ISwapchain.LastPresentStoodDown => false` (`Seams\Rhi\Rhi.cs:333`, a default interface member; `D3D12Swapchain :4892`; `HeadlessSwapchain :494`); `D3D12Device :3451` only forwards the primary's. `internal D3D12Swapchain? PrimarySwapchain` exists (`:317`, set at `:561`); `HeadlessGpuDevice` keeps a private `_primarySwapchain` (`:174`, set in `CreateSwapchain` `:176-182`). `AppHost` holds `_swapchain : ISwapchain` (`:380`) and already reads `_swapchain.LastPresentStoodDown` per frame (`:4513`). `UseIsActive()` folds only minimized/hidden/suspended (`AppHost.cs:3652-3665`); nothing exposes occlusion to the UI. `InputHooks` members are instance fields set in the `AppHost` ctor (`:2936-2958`); `WindowChromeEpoch : Signal<int>?` (`Context.cs:293`) is the bumped-then-pull precedent.
- **Chrome accent**: `AccentSet.From(ColorF)` (`Dsl\Tokens.cs:50-71`) is pure and theme-aware at call time; `Tok.SetAccent` (`:295-317`) bumps `Tok.Epoch` and `AppHost.Paint` runs `RethemeAll()` over every mounted component (`Hosting\AppHost.cs:3946-3954`, `Reconciler.cs:624-639`) — a whole-app re-render, which is why it is never used for a per-surface accent.

### 1.4 Lyrics seams (`Shell/Lyrics.cs`, `Shell/Lyrics.UI.cs`, `Shell/Lyrics.Host.cs`)

- `Lyrics.StagePane()` (`Lyrics.UI.cs:77-92`) is parameterless; `StageColumnMaxW = 700f, StageColumnGutter = 48f, StagePivotBandH = 72f` (`:56`); it mounts `new ViewCore(large: true, onMedia: true, visible: s_stageVisible)` with `s_stageVisible = Shell.Ui.ImmersiveLyrics.Value && Stage.Pane.Current.Value == Stage.Pane.Lyrics` (`:65-66`).
- `ViewCore` (`:146-194`): `internal ViewCore(bool large, bool onMedia, Func<bool> visible, EntityId readerEpisode = default)` (`:183`), fields `Large`, `InkMode = new Ink(onMedia)` (`:168`, `:187`), `_visible`; the `Ink(bool OnMedia)` record (`:104-134`) resolves every colour from `Design.StageInk` at consumption and has no art accent (`Accent => RingFill` = plain ink on media, `:120-123`). **No accent reaches the lyrics view today.**
- Blur strength: `int strength = Prefs.BlurStrength(GpuProfile.IsWeak); _strength = strength; float newScale = BlurPolicy.Scale(strength);` (`:319-321`) → `_dofScale`, `HaloScale` (`:322-330`). `Lyrics.Prefs.BlurStrength(bool weakGpu) => BlurPolicy.Resolve(global::Wavee.Prefs.Lyrics.BlurStrength(), weakGpu)` (`Lyrics.cs:1131`); `BlurPolicy { Auto = -1; WeakGpuDefault = 40; StrongGpuDefault = 100; Resolve(int setting, bool weakGpu); Scale(int); Enabled(int) }` (`:164-184`). `DriveDofRamp` (`Lyrics.UI.cs:1060-1101`) snaps every σ to 0 when `!BlurPolicy.Enabled(_strength)`. `GpuProfile.IsWeak` (`..\fluent-gpu\…\Foundation\GpuProfile.cs:38`).
- `LineRow` (`:1766-1945`): ctor `(ViewCore owner, int index, Line line, Signal<int> emphasis, FloatSignal? glowFade)` (`:1783`); `Render` computes `isActive` (`:1830`), builds `textEl`, then `dofContent = new BoxEl { Direction = 1, Blur = blur, OnRealized = _onDof, Children = secondaryText is null ? [textEl] : [textEl, SecondaryText(secondaryText)] }` (`:1911-1917`), and the root `new BoxEl { Direction = 1, ScaleX/ScaleY = scale, Opacity = _seedOpacity, Padding = new Edges4(m.SidePad, m.RowPad + reserve, m.SidePad, m.RowPad), …, Children = [dofContent] }` (`:1919-1939`). The ink flip uses `BrushTransitionMs = Design.Motion.Fast` (`:1896`).
- The active line: `Lyrics.ResolveLine(IReadOnlyList<Line> lines, long nowMs)` (`Lyrics.cs:273`, the ONE binary search), `LeadMs = 140` (`:266`); `ViewCore.OnFrame` resolves `active = ResolveLine(doc.Lines, nowMs + (IsPodcast ? 0 : LeadMs))` (`Lyrics.UI.cs:1506`) and writes `_activeLine` (`:1514`, a private `Signal<int>`). The doc is a private `Doc? _doc` (`:259`). The store door is `Lyrics.Store.Doc(Track) / Doc(string trackId)` (`Lyrics.Host.cs:1933-1941`, "one dictionary read — safe from a bound thunk"), `Store.Changed : Signal<uint>` (`:1905`), `Store.Ensure(Track)` (`:1952`), `Store.IdOf(Track)` (`:1925`). `Line(long StartMs, string Text, IReadOnlyList<Syllable> Syllables, long? EndMs = null, string? Translation = null, string? Romanization = null, bool IsWordByWord = false)` (`Lyrics.cs:75`); `Doc(string TrackId, bool IsSynced, IReadOnlyList<Line> Lines, SyncKind Sync = SyncKind.Line, string? Provider = null, long OffsetMsApplied = 0)` (`:88`).

### 1.5 The rail deck (`Shell/Deck.cs`, `Deck.UI.cs`, `Deck.Faces.cs`)

- `Deck.IModel { Frame Tick(in Input, float dtSec); ReadOnlySpan<float> Bands; ReadOnlySpan<float> Peaks; bool IsSettled; }` (`Deck.cs:77-93`). `MeterModel(bool ppm, Func<(float rms, float peak)?> levels)` (`:1134-1190`) — **when the tap is null it fakes a breathing −18 dB needle** (`:97`: `RmsToDb(0.12f) + 3f * MathF.Sin(BreatheOmega * _t)`). `LevelModel(int bands, bool scope, Func<(float rms, float peak)?> levels, float? seed = null)` (`:1198-1324`) — **the bands are synthesised** from `env`/`rmsN` with `MathF.Sin(_t * (3f + 0.37f * i) + _seed + i)` and `Hash(i, _t)` (`:173-196`), and "scope" mode parks every band at `0.5 + 0.38·sin…` (`:175-180`). `UpdatePeak` (`:214-229`: hold 400 ms, fall 1.2/s) survives. `Models.Create(in Rail.PlayerCatalog.Preset preset, Func<(float rms, float peak)?> levels, in Input seed)` (`:1479-1494`; `WinampBands = 19` `:1468`, `WmpBands = 24` `:1470`); `ModelOptionSlug` (`:1520-1525`: Vu → "ballistics", Winamp → "vis"). `ClockRules.TickMs = 1000f / Design.Cadence.PluggedLoopHz` (`:1539`; `PluggedLoopHz = 30`, `Platform/Design.cs:1667`), `ShouldTick` (`:1568-1569`), `LoopsMayRun` (`:1574`).
- `Deck.UI.cs`: `Slab` (`:77-107`, `BandCount = 24`, `FloatSignal[] Bands, Peaks`), `LevelTap` (`:120-127`, pulls `Playback.Audio.Levels.Peek()` when `Supported && Owner.Us`), `Host.UsesLevels` (`:147`), the model build `Model = Models.Create(in Preset, LevelTap, Seed(...))` (`:163`), `Clock` (`:229-403`: `UseInterval(_tick, ClockRules.TickMs, run)` `:263`, `TickCore` `:318-346` → `rt.Batch(_write)` `:344`, `WriteCore` `:348-369` quantising each band to 1/64, `LeaseLevels` `:383-392` → `Playback.Audio.AcquireLevels()`). `Deck.Faces.cs`: the bound-transform pattern (`WinampFace.Bars` `:1116-1145`: `Transform = Prop.Of(() => Affine2D.Scale(1f, MathF.Max(band.Value, 0.02f)))` with `TransformOriginY = 1f`); the Winamp scope read `bool scope = Rail.PlayerPrefs.ChoiceSlug(in preset, "vis") == "scope"` (`:986`); the "vis" option row `Seg("vis", "player.opt.analyser", ("spectrum", …), ("scope", …))` (`Rail.cs:160`).
- Tests: `DeckModelTests.cs:698-758` (`A_ppm_is_faster…`, `Level_bands_stay_in_range…`, `A_silent_spectrum_relaxes…`, `A_paused_SCOPE_deck_settles_too`, `A_null_level_tap_is_a_real_answer`), `DeckClockRulesTests.cs:38-47` (`A_paused_scope_deck_settles_so_the_gate_closes`), `:151-177` (`DeckModelOptionsTests`), `RailTests.cs:229-237` (`Every_preset_maps_to_a_deck_model` — `Models.Create(p, static () => null, seed)` signature pinned).

### 1.6 Spotify data (`Spotify/*`, `Entities/*`)

- **Kind 237** (`Spotify/Spotify.Decode.Traits.cs:191-243`, `Waveform(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)`): reads fields 3/4/5 (low/mid/high byte envelopes; f1 = 44100, f2 = 20 ms hop are skipped), reduces to `WaveformColumns = 220` (`:29`) by sum-of-band-max normalised to the loudest column (`:218-228`, `BandMax` `:233-242`), stages `run.Relation = TraitRelation.TrackWaveform; run.Bytes = s.AddText(columns)` (`:221-229`). Reference lengths: `band_low` 12,886 samples, others 12,466 (`:187-190`). Registration `case Ext.Waveforms: Waveform(payload, entityUri, s)` (`Spotify.Decode.cs:1246`; `Ext.Waveforms = 237` `:78`). Commit `TraitRelation.TrackWaveform` arm (`Entities/Edges.cs:1200-1210`) → `edges.TrackWaveform.Replace(parent, targets, magnitudes, EdgeState.Complete, magnitudes.Length)`; the edge `public readonly EdgeTable<byte> TrackWaveform = new();` (`Edges.cs:890-893`); `TraitRelation { TrackCredits, TrackVersions, TrackWaveform, AlbumRecommendations }` (`:1071`); `StagedTraitRun { StagedId Parent; TraitRelation Relation; int Start, Length; TextRef Bytes; }` (`:1085-1091`); `Staging.AddText(ReadOnlySpan<byte>)` (`Entities/Entities.cs:1853`), `Staging.Utf8(TextRef)` (`:1867`). Consumer: the drawer's `PeaksFor` (`Entities/Track.Drawer.cs:181-193`) → `DrawerRules.Peaks(ReadOnlySpan<byte>, Span<float>)` (`:783-789`) → `Waveform.Create(new WaveformModel(peaks, DrawerRules.WaveBars /* 64 */), key: "waveform")` (`:440`, `:682`). Fake seed: `Entities.Fake.Album.cs:535` (empty run) and `:562-569` (220-column triangle). Test: `DecodeTests.cs:1548-1580`.
- **Edge machinery**: `FetchEdge` (`Entities/Fetch.Routes.cs:63-114`, the track traits at `:79-80`), `SpclientRoute` (`:153-183`), `FetchRoute.Spclient(SpclientRoute rest, uint groups, uint primary = 0)` (`:196-197`), `ForEdge` (`:422-455`, `FetchEdge.TrackWaveform => FetchRoute.Metadata(ThreeBandWaveforms, 0)` `:445`), `Fetch.EdgeTableOf` (`Fetch.Edges.cs:166-205`), `ParentTableOf` (`:208-223`), `Entities.EnsureEdge(FetchEdge, int parent, …)` (`:237`). `EdgeDoorTests.Every_door_relation_names_a_table_and_a_parent_table` (`Wavee.Tests/EdgeDoorTests.cs:406-417`) sweeps every `FetchEdge`; `FetchRoutesTests.cs:121-136` is the transport theory. The read idiom (`Track.Drawer.cs:581-615`): `uint epoch = Entities.ScopeEpoch.Value; _ = scope.Edges.TrackCredits.Changed.Value; UseEffect(demand, DepKey.From(slot, (int)epoch));` then synchronous `Payload/Targets/State/Version/Readiness` reads.
- **Kind 222** (`Spotify.Decode.cs:1263-1303`, `AudioAttributes`): tempo (double BPM → `row.Tempo = (ushort)(tempo * 10 + 0.5)`), `Key`, `Camelot`, `CamelotColor`; columns on `TrackTable` (`Entities/Track.cs:185-192`), `Track.Tempo` (`:382`, ×10), `Knows(TrackFields.Audio)`; route `FetchRoute.Metadata(AudioAttributes, (uint)TrackFields.Audio)` (`Fetch.Routes.cs:221`). **No beats on this kind.**
- **spclient**: `Get(scoped ReadOnlySpan<char> path, ApiHost host, HeaderSet headers, CancellationToken ct)` (`Spotify/Spotify.Api.cs:1956-1960`), `CommonJson` (`:1951`), `ArtistTopTracksExtended(string artistUri, ct)` (`:2031-2038`, the JSON-by-uri precedent with `PathWriter`), `Serves(in FetchRoute)` (`:698-712`), `AnswerRest(SpclientRoute rest, string uri, Staging s, ref FetchOutcome outcome, uint groups = 0)` (`:1163-1195`; `IdOf(string uri)` `:1211` = base62), `AnswerEdge` (`Spotify.Api.Library.cs:176-221`, default arm → `AnswerRest`). JSON helpers `Enter/EnterArray/Fields/Next/Element/End/SkipValue/Num/Flag/OneText/OneNumber` (`Spotify.Decode.Pathfinder.cs:46-139`); the hand decoder exemplar `ArtistTopTracks` (`Spotify.Decode.Entry.cs:102-127`). **Nothing in the repo calls `/audio-attributes/v1/audio-analysis/`** — the wire shape is unverified (§4.9 wave-0 probe).
- **Queue / artist / credits / format**: `Stage.QueuePaneBody` is installed by `Queue.InstallUi` (`Entities/Queue.UI.cs:62-73`, `:65`) with the queue's own `StagePane : QueueSurface` (`:954`); the rail artist body reads `artist.Knows(ArtistFields.Stats)`, `Name/ImageId/HeroImageId/BioLeadId/MonthlyListeners/Followers` and `Controls.ArtistAboutCard(artist, AboutLayout.Rail)` (`Rail.UI.cs:471-523`, `:592-594`; `Platform/Controls.ArtistAbout.cs:35-143`), demanded by `Entities.Ensure(new Artist(slot), ArtistFields.Overview)` (`:484`); credits `Edges.TrackCredits : EdgeTable<CreditEdge>` (`Edges.cs:883-886`) rendered by `Track.Drawer.cs:617-660`; `Playback.StreamFormat : Signal<StringId>` (`Playback/Playback.Host.cs:168`) is a badge string ("OGG 320", "FLAC 44.1/16").
- **`--fake`**: `Platform.Args.Fake` (`Platform/Platform.cs:862`, `:883-890`); `Entities.SeedFake` (`App.cs:165`), `Playback.Audio.UseSilentEndpoint()` (`:190`, a paced silent endpoint running the real graph — `Levels` ≈ 0), lyrics skipped (`:227`), `Spotify.BootFake()` (`:229`). Tracks `spotify:track:tr0…tr165` (`Entities.Fake.cs:174`, `TrackCount = 166`), queue seeded 12 rows (`:430-447`), `Tempo`/`Audio` known on every seeded track (`:207-208`). `Playback.OwnerSignal` stays `Nobody` until the first local play, then `Us` (`Playback.cs:626`, `:762`).

### 1.7 Settings, loc, diagnostics, tests, headers

- **Settings**: `SettingKey<T>(string Name, T Default)` + `IAppSettings { T Get<T>(SettingKey<T>); void Set<T>(SettingKey<T>, T); }` (`Platform/Platform.cs:40-50`); the registry backing supports bool/int/long/float/double/string only (`Platform.Host.cs:463-532`) — enums store as `int`. Keys live in `Platform.Keys` (`Platform.cs:71`) and the Wave-6 partial (`Platform/Platform.Settings.cs:35-41`, `StageRects`). The epoch pattern (`Platform/Prefs.cs:12-22`): `_ = Epoch.Value; return Platform.Settings.Get(key);` — classes `Appearance` (`:55`), `Lyrics` (`:146-229`), `DetailHero` (`:236`). Appearance rows: `Settings.UI.Appearance.cs:67-132` (`Row(label, sub, control, RowGlyph(Tab.Appearance, "rowId"))`, `SectionHeader`, the lyrics section `:112-116`), the slider pattern `LyricsBlurControl` (`:661-689`) with `s_lyricsBlurSlider` (`:44`) seeded in `SeedAppearance()` (`:62-63`); `Toggle(SettingKey<bool>, afterWrite)` (`Settings.UI.cs:296-302`) and `AppearanceToggle` (`Settings.UI.Appearance.cs:251-252`); the int-enum `ComboBox.Create([labels], s_trayMode, width: 260f, onChange: …)` pattern (`Settings.UI.cs:437-453`). **Catalog registration is required**: `Settings.Catalog.Rows` (`Screens/Settings.cs:138-146`, `new(Tab.Appearance, "Lyrics", "lyricsBlur", "Filter")`), glyph unique within a section (`SettingsCatalogTests`).
- **Loc**: nested JSON `assets/loc/en-US.json` (3,477 lines), `nl.json`, `ko-KR.json` (1,247 each; per-key fallback to en-US by design, `nl.json:3`). Code reads `Loc.Get(Strings.X.Y)` (generated consts; a key missing from en-US is a CS0117). `settings.appearance.lyricsBlurAuto` is `en-US.json:1216`; nl/ko `settings.appearance` blocks open at `:468`; the `player` object opens at `en-US.json:1646`, `diagnostics` at `:3176`; the last top-level object `tray` (`:3462`) closes the file at `:3476-3477`. FLLOC003/004 are warnings only (`Wavee.csproj:225`).
- **Diagnostics**: `Card(string title, params Element[] rows)` (`Screens/Diagnostics.UI.cs:204-214`), `Row(string label, string? value, float labelWidth = RuntimeLabelWidth)` (`:217-225`), `Body/Caption` (`:235-237`); `RuntimePageView.Render` (`:717-753`) builds `body` and `return PageFrame(Icons.MusicNote, …, "playback-diagnostics", body)`; the model-side pattern is a record struct + a `Note…` writer bumping a `Signal<int>` (`Screens/Diagnostics.cs:329-351`, `Connect.Version`).
- **Logging**: `Log.Event(WaveeLogLevel level, string category, string eventId, string message, string? operationId = null, long elapsedMs = -1, Exception? ex = null, params ReadOnlySpan<WaveeLogField> fields)` (`Platform.cs:713-718`); `WaveeLogField.Of(name, string?|int|long|double|bool)` (`:580-588`); the `Rail.NpvDiagnostics` shape (`Shell/Rail.cs:313-338`).
- **Tests**: `Wavee.Tests` is xUnit v3, `ProjectReference` to `Wavee`, no `InternalsVisibleTo`, `ConcurrentGarbageCollection=false` (`Wavee.Tests.csproj`). `StageTests.cs` (`StageLayoutTests`, `StagePaneTests :336-352`), `StageSurfaceTests.cs` (`StageBandTests :17-51`, `StageTransportTests`, `StageDriftClockTests :89-149`, `StageDriftTests`), `ShellFrameRulesTests.cs:62-70` (`ChromeMounted` theory) and `:72-104` (`Escape`/`Space`/`F11`), `DeckModelTests.cs`, `DeckClockRulesTests.cs`, `LyricsSurfaceRulesTests.cs`, `DecodeTests.cs` (`:751-777` kind 222, `:1548-1580` kind 237; fixtures from the generated `Wavee.Protocol`/`Wavee.Waveforms` messages).
- **Headers**: line 1 `// ── Shell/X.cs ───…` padded to 118 columns, a contents line, `//`, `Role: / Owner: / Wave: / Budget: / Spec:`, a rule line, prose; LF, no BOM (`Stage.cs:1-10`, `Deck.cs:1-9`).

### 1.8 Engine drawing today — why `SeriesEl` is required

- `PolylineStrokeEl` (`..\fluent-gpu\src\FluentGpu.Engine\Dsl\Element.cs:546-588`, `ElementTypeId => 11`) holds **four inline points** (`P0..P3`, `PointCount`), all static props; the HLSL draws exactly three capsule segments (`FluentGpu.Windows\D3D12\PolylineStrokePipeline.cs:98-112`). `PathEl` (`:603-675`, id 16) takes a `PathData` (an immutable class whose ctor copies two arrays, `Foundation\PathGeometry.cs:94-153`), realized at record time through `PathRealizationCache.Shared` keyed by `(GeometryId, ContentEpoch, …)` (`Render\PathRealizationCache.cs:20-21`, `:113-121`) — a per-frame geometry would mint a new epoch and a cache MISS every frame, tessellating synchronously on the record thread; **`PathRealizationCache.Shared.BeginFrame` has zero production callers**, so the slab only grows. Element ids 1–17 are taken (`:41`; `Scene\NodeDescriber.cs:55-75`); **18 is free**. `VisualKind` is `None..ListRow = 9` (`Scene\Columns.cs:6`); **10 is free**. `DrawOp` runs to `CompositeSlice = 23` (`Render\DrawList.cs:7-30`); **24 is free**.
- The DrawList is a fixed-size POD stream (`WriteOp`/`WritePayload<T> where T : unmanaged`, `DrawList.cs:808-824`); the headless translate buffer caps a payload at 1024 B (`Headless\Rhi\HeadlessGpuDevice.cs:428-429`); every stream walker frames bodies through `RepaintStreamSafety.TryBodySize` (`Seams\Rhi\RepaintPolicy.cs:72-104`) and stops on an unknown op. Out-of-line data is only ever referenced by offsets into the render-thread-owned `PathRealizationCache.Shared` slab (`FillPathCmd.VtxStart…`, `DrawList.cs:397-410`).
- A bound payload channel exists as a precedent: `ListRowEl.Cells : Prop<RowCells>` (`Dsl\ListRowEl.cs:36-48`; `RowCells(RowCell[] array, int count)` with `AsSpan()`), wired by `BindListRowCells` (`Reconciler\Reconciler.ListRow.cs:86-103`: `new BindEffect<RowCells>(Runtime, lr, static e => e is ListRowEl x ? x.Cells : default)` → `AddBinding(node, fx.Start(() => { …; WriteRowCells(node, current.AsSpan(), …); _scene.Mark(node, NodeFlags.PaintDirty); }))`), stored in a pooled per-node array (`Scene\SceneStore.RowCells.cs:21-38`, `NoteCaptureChanged(idx)` first), captured by the snapshot (`Scene\SceneRecordingSnapshot.cs:543-552`, `RowCellsCapture` `:982`), parity-checked (`SceneRecordingSnapshot.Parity.cs:211-215`). `Prop<T>` (`Foundation\Signals\Prop.cs:32-90`) accepts a `T`, a `Func<T>` (via `Prop.Of`), a `Signal<T>` or `Memo<T>`; `T` cannot be a ref struct.
- Every site a new leaf element touches: the record (`Dsl/`), `BindFlip`/`RecordChanged` (`Reconciler.cs:1014-1043`), `WriteColumns` (`:4738` switch; polyline arm `:5312-5349`), `BindNode` (`:2441`, tail `:2775-2785`), `IsRecyclable` (`:3822-3856`), `SkeletonDeriver` (`Hooks\SkeletonDeriver.cs:112-116`), `NodeDescriber` (`:55-75`), `SceneStore` slab + `FreeSubtreeCore` (`Scene\SceneStore.cs:157-158`, `:496-514`, setters `:1812-1830`), the snapshot (`:52-53`, `:385-414`, `:516-534`, `:822-823`, `:924-976`, parity `:176-177`), the recorder (`Render\SceneRecorder.cs:2581-2646`), `DrawList` (enum, stats `:104-209`, cmd, writer `:729-737`), `RepaintPolicy.TryBodySize`, `DrawOpTranslate.Apply` (`Render\DrawOpTranslate.cs:17-49`), `SliceOpBounds.TryGet` (`Render\Tiles\SliceOpBounds.cs:19-57`), `HeadlessGpuDevice` (`:25`, `:74`, `:240`, `:311-424`), D3D12 (`PrimKind` `:181`, `BoundPipe` `:236`, lists `:142-147`, `s_pipeStageNames` `:577-578`, creation `:604-657`, `BeginPipesFrame` `:1812-1822`, `ClearInsts` `:1899`, decode `:2154-2177`, replay `:3044-3050`, dispose `:4485`/`:4562`), VerticalSlice (`Harness\Asserts.cs:185-211` `DrawPayloadSize`, `Suites\PathSuite.cs:846-927` `StreamSizeGate`, `Suites\ControlsSuite.cs:10638-10676` `DecodeVideoLayerNesting`, `Harness\SuiteRegistry.cs:36-78`), the gallery `Scenes\ShotScene.cs:55-70`/`:227-235`, and the canon (`docs/design/subsystems/README.md:64-97` opcode rows, `scene-memory.md:735-791` enum + `:263` column row, `gpu-renderer.md` §3.1/§5, `SPEC-INDEX.md` §2). `check-canon.ps1` is a stale-token grep (8 regexes, `:37-78`); it rejects a `…Bind` prop spelling and "bind wiring is mount-only".
- Motion facts that bind the design: `Element.Transition` is a `MotionTokenDef?` (`Element.cs:58`), **not** an implicit tween for bound channels — `BindNode` writes `paint.Opacity = next` directly (`Reconciler.cs:2441-2540`); it is read as the Enter/Exit/While* token by `WriteAnchorColumns`/`SynthesizeDeclarative` (`Reconciler.cs:4269-4326`, `:4620`, BoxEl `:4973-4990`) and by `Interaction.Interactive` (`FluentGpu.Controls\Interaction.cs:137`, "caller-set Transition wins"), despite the doc comment at `Element.cs:54-58`. `AnimBake` (`Reconciler\AnimBake.cs:18`) has **zero callers** (its header says "INERT until wired"). Declarative motion (`Enter/Exit/Layout/While*`) is baked for `BoxEl` (`:4973-4981`) AND for `ComponentEl` anchors (`WriteAnchorColumns` `:4308-4326`, called from `MountComponent` `:1192`): `Embed.Comp(...) with { Enter = …, Exit = … }` animates. `Remove(node)` (`:4106-4137`) checks a transition on the REMOVED ROOT only (`:4112-4113`) and `UnmountSubtree` cancels every descendant's (`:4159-4164`), so an `Exit` on a component's rendered root under a `Flow.Show` whose child is a bare `ComponentEl` never plays — put `Exit` on the embed or on a `BoxEl` the Show owns. `UseSpringValue` does not exist (`UseAnimatedValue` at `RenderContext.cs:945` steps per render). Hooks: `UseSpring(AnimChannel, float to, SpringParams, DepKey)`, `UseTransition(…)`, `UseKeyframes(AnimChannel, Keyframe[], float durationMs, bool loop, DepKey, Cadence?)` (`RenderContext.cs:973-974` → `AnimEngine.Keyframes`, `Animation\AnimScheduler.Timeline.cs:35-55`, which **never consults reduced motion** — only the one-shot `KeyframesMotion` `:61-66` snaps; a looping keyframe set must be swapped for rest keys by the caller), `UseDrivenAnimation(AnimChannel, Keyframe[], Func<float> source, float min, float max, DepKey)` — registers its source in a grow-only `DrivenClockTable` on every deps change (`Animation\AnimTypes.cs:65-72`), so its `deps` must be stable. `Context.Anim.Spring(node, channel, to, in SpringParams, …)` retargets by rebase (`Animation\AnimScheduler.cs:368`). `EnterExit(float Dx = 0f, float Dy = 0f, float Sx = 1f, float Sy = 1f, float Opacity = 1f, bool Active = false, float Blur = 0f, float DelayMs = 0f)` (`Foundation\LayoutTransition.cs:84-86`; `Active` must be true) and `LayoutTransition(TransitionChannels Channels, TransitionDynamics Dynamics = default, SizeMode Size = SizeMode.Auto, EnterExit Enter = default, EnterExit Exit = default, …)`; `TransitionDynamics(DynamicsKind Kind = Spring, float Response = 0.30f, float DampingRatio = 0.85f, float DurationMs = 0f, EasingSpec Easing = default)` (`:57`) with `Spring(float response = 0.30f, float dampingRatio = 0.85f)` (`:64`) / `Tween(float ms, Easing)`. `Motion.ReducedMotion` (`Dsl\Motion.cs:24`). `MotionTok.ControlNormal/StandardEnter` are `MotionTokenDef`s (`Animation\MotionTok.cs:192-195`). `UseInterval(Action tick, float ms, bool enabled = true)` auto-pauses on `UseIsActive()` (`Hooks\RenderContext.Timers.cs:326-333`); `UseTimeout(Action, float ms, DepKey deps = default) : TimerHandle` (`Component.cs:112`) with `TimerHandle.RestartIn(float ms)`, `Cancel()`, `NowMs` (`RenderContext.Timers.cs:30-54`). `UseMeasuredWidth(float quantum = 0f) : IReadSignal<float>` / `UseMeasuredBounds` (`Component.cs:118-121`, `RenderContext.Measure.cs:118/:137`; first value 0, consumer re-renders next frame). `Element.Visible = false` (`Element.cs:38`) collapses the subtree out of layout, paint and hit-test AND feeds the activation signal: `SetSubtreeHidden` (`Reconciler\Reconciler.Presence.cs:73-83`) walks every component below the collapsed node and writes `entry.ActiveSig.Value = !entry.Parked && !entry.Hidden` (`:78-79`; the signal is created with the same formula at `Reconciler.cs:1163`), so `UseIsActive()` reads false, `UseActivation(onDeactivated:)` fires, and every `UseInterval` below pauses (`RenderContext.Timers.cs:331-335`) — "presence and KeepAlive parking are two edges into the ONE activation signal" (`component-props-contract.md:68-72`). Renders are NOT suppressed (props keep landing, bindings settle — `Presence.cs:17-19`), and `UseTimeout`/`UseKeyframes` have no active-gating at all (`:70-72`). A bound `Visible` on a `MorphId` node trips the DEBUG `BindContract` assert.
- Layout + input facts that bind the design: a ZStack child's **vertical** axis is `AlignSelf` (fallback `AlignItems`) and its **horizontal** axis is `JustifySelf` (fallback `Justify` mapped Center/End/else-Start) — `Layout\FlexLayout.cs:1279-1286, :1300-1301, :1328-1332`; an auto-sized Center/End child takes its desired size on that axis, a Start/Stretch/Auto child fills the slot; `FlexAlign { Auto, Start, Center, End, Stretch }` (`Foundation\LayoutTypes.cs:7`). `AlignSelf/JustifySelf/HitTestVisible/HitTestPassThrough/OnKeyDown/OnHoverMove/OnPointerExit/OnPointerWheel` live on **`BoxEl`** (and the sized leaves: `ImageEl`, `ScrollEl`), not on `Element` (`Element.cs:473-480`, `:171-236`, `:286-300`); only `Key/Visible/Enter/Exit/Stagger` are on `Element` (`:38-73`). `HitTestVisible = false` **prunes the whole subtree** (`SceneStore.cs:2094-2119`, `InputDispatcher.cs:2816-2818`, `:4072-4074`, `:4127-4129` — the `Element.cs:286-295` doc is stale); `HitTestPassThrough = true` (`:296-300`) makes a wrapper yield to what is BEHIND it where none of its OWN children are hit (`InputDispatcher.cs:3952-3957`, `:4120`, `:4189`) — the `MediaPlayerElement.cs:1011-1024` chrome-layer idiom. `OnHoverMove : Action<Point2>?` (`:229`), `OnPointerExit : Action?` (`:236`), `OnPointerWheel : Action<WheelEventArgs>?` (`:226`; `WheelEventArgs { Point2 Local; float Delta, DeltaX; KeyModifiers Mods; bool Handled; }` `Events.cs:131-138`), `OnKeyDown : Action<KeyEventArgs>?` (`:171`; `KeyEventArgs { int KeyCode; KeyModifiers Mods; bool IsRepeat; bool Handled; Shift/Ctrl/Alt }` `Events.cs:81-96`; `Keys.Escape 27, Space 32, Left 37, Up 38, Right 39, Down 40, F11 122` `:213-224`). `DepKey.From` has `(int)`, `(int,int)`, `(int,int,int,int)`, `(long)`, `(long,long)`, `(float…)`, `(bool)`, `(NodeHandle)`, `(float,int)` and implicit conversions from `int/long/float/bool/string?/NodeHandle` and the matching tuples — **no three-int form** (`Hooks\DepKey.cs:35-67`; three values → `(a, b, c, 0)` or `HashCode.Combine`). `Flow.Show(Func<bool>, Element then, Element? @else = null)` is the only overload (`Hooks\ControlFlow.cs:150`). `Canvas.Create(float width, float height, IReadOnlyList<CanvasChild> children) : BoxEl` with `CanvasChild(float X, float Y, Element Child)` (`FluentGpu.Controls\Canvas.cs:7, :16`; each child is wrapped in an `OffsetX/OffsetY` box). `ItemsView.Create(int, Func<int, Element>, RepeatLayout, ListOptions?)` and `AppBarToggleButton.Create` capture their arguments at mount (`ItemsView.cs:611-617`, `ComponentEl.cs:27-29`): a changed template needs a `Key` remount or `CountSignal`. `ScrollEl { Content, Horizontal, Grow, MinHeight, ScrollKey, AutoEdgeFade, … }` (`Element.cs:953-1049`). `TeachingTip.PlacementMode` is NESTED in `TeachingTip` (`TeachingTip.cs:48`); `Show(IOverlayService, Func<NodeHandle>, Action<TeachingTip>) : OverlayHandle` (`:369`), `tip.Closed : Action<CloseReason>?` set inside `configure` (`:143`, invoked after the close animation, `OverlayHost.cs:806-807`); `OverlayHandle.IsOpen` goes false at close START (`OverlayHost.cs:468`). `Button.ButtonPalette.ForAccent(ColorF)` is nested in `Button` (`Button.cs:119, :179`).
- Control facts that bind the design: `SelectorBar.Create(IReadOnlyList<string> items, Signal<int>? selectedIndex = null, Action<int>? onChange = null, TemplateParts? parts = null, IReadOnlyList<string?>? icons = null, SelectorBarStyle? style = null)` (`FluentGpu.Controls\SelectorBar.cs:40-42`), live props (`:42`, `:96`), pill fill hard-coded `Tok.AccentDefault` (`:215`) → recolour through `TemplateParts` on `SelectorBar.PartPill` (`:26`; `TemplateParts.Set<T>(string, Func<T,T>)`, `Dsl\TemplateParts.cs:47-60`). `Slider.Create(FloatSignal? value = null, Action<float>? onChange = null, SliderOptions? options = null, float length = 200f, float thickness = 32f, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)` (`Slider.cs:306-310`; `Style` fields `ValueFill*`, `ThumbFill*`, `RailFill*`, `ThumbRing`, `ThumbBorder : GradientSpec?` `:58-88`; `Length` is a live `[Prop]` `:345`; no fluid mode). `ToggleSwitch.Create(Signal<bool>? isOn = null, Action<bool>? onChange = null, string? header = null, string? onContent = null, string? offContent = null, bool isEnabled = true, Style? style = null, TemplateParts? parts = null)` (`ToggleSwitch.cs:130-135`; `Style.OnFill/OnHover/OnPressed/OnKnob`, `MinWidth = 154`). `TeachingTip.Show(IOverlayService overlay, Func<NodeHandle> target, Action<TeachingTip> configure) : OverlayHandle` (`TeachingTip.cs:369-375`; fields `Title`, `Subtitle`, `Body`, `ActionButtonContent`, `ActionButtonClick`, `ActionButtonIsAccent`, `CloseButtonContent`, `IsLightDismissEnabled`, `PreferredPlacement`, `Closed : Action<CloseReason>?` `:81-147`). `ItemsView.Create(int itemCount, Func<int, Element> itemTemplate, RepeatLayout layout, ListOptions? options = null)` (`ItemsView.cs:608-609`), `RepeatLayout.Grid(int columns, float itemHeight, float gap = 0f)` (`ListOptions.cs:40`), `ListOptions { SelectionMode, Selection, IsItemInvokedEnabled, OnInvoked, Selector = SelectorVisual.Border, KeyOf, CountSignal, … }` (`:316-410`), `SelectorVisual { AccentPill, Check, FullRow, Border, None }` (`SelectorVisuals.cs:20`). `AppBarButton.Create(string glyph, string label, Action onClick, bool enabled = true, Style? style = null, bool isCompact = false, KeyAccelerator? accelerator = null) : BoxEl` (`AppBarButton.cs:63-64`, stateless); `AppBarToggleButton.Create(string glyph, string label, Signal<bool>? isChecked = null, Action<bool>? onChange = null, bool isEnabled = true, bool isCompact = false)` freezes glyph/label (`:19-29`, `:41-49`) and hard-wires `Tok.Accent*` (`:63-89`). `WithContextMenu(this BoxEl el, IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions? options = null)` (`ContextMenu.cs:273-274`); `MenuFlyout.Create(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = …, TemplateParts? parts = null, bool focusFirst = false)` (`MenuFlyout.cs:98`); `IconButton.Create(string glyph, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ControlSize size = ControlSize.Medium) : BoxEl` (`IconButton.cs:78`). `Button.Create(label, onClick, ButtonAppearance appearance = Standard, ControlSize size = Medium, string? glyph = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ButtonPalette? palette = null)` (`Button.cs:258-260`) with `Button.ButtonPalette.ForAccent(ColorF)` (`:179-188`) — the one control that takes an `AccentSet`. Effects: `AcrylicSpec(ColorF Tint, float TintOpacity, float BlurSigma, float NoiseOpacity, float LuminosityOpacity, ColorF Fallback, float FeatherTop = 0f)` on `BoxEl.Acrylic` (`Foundation\Effects.cs:158`, `Element.cs:153`); `BakedBlurSpec(float SigmaDip, float ResolutionScale = 0.5f)` on `ImageEl.BakedBlur` (`:237`, `Element.cs:906`); `EdgeFadeSpec(EdgeMask Edges, float band, …)` + `EdgeFadeSpec.Vertical(float band = 24f)` on `BoxEl.EdgeFade` (`:278-281`, `Element.cs:168`); `GradientSpec(GradientShape Shape, float AngleDeg, GradientStop[] Stops)` with `MaxStops = 4` (`:89-98`) on `BoxEl.Gradient` (`Element.cs:141`); `Elevation.Card/Dialog/Flyout/Tooltip` (`Dsl\Elevation.cs:18-41`). Type ramp: `Ui.Caption 12/16`, `Body 14/20`, `BodyStrong 14/20/600`, `BodyLarge 18/24`, `Subtitle 20/28/600`, `Title 28/36/600`, `TitleLarge 40/52/600`, `Display 68/92/600` (`Dsl\Typography.cs:39-46`); `Radii.Control 4 / Card 8 / Pill 16 / Full 999`, `Radii.Circle(d)` (`Dsl\Radii.cs:8-29`); `Spacing XXS 2 … XXXL 32` (`Dsl\Spacing.cs:11-18`). Icons available (`FluentGpu.Controls\Icons.cs`): `FullScreen`, `BackToWindow`, `Queue`, `Contact`, `Equalizer`, `Audio`, `Devices`, `Settings`, `Check`, `Cancel`, `Volume`, `Mute`, `Heart`, `HeartFill`, `Shuffle`, `RepeatAll`, `RepeatOne`, `Play`, `Pause`, `Previous`, `Next`, `More`, `ChevronDown`, `Globe`, `DockLeft`, `ViewGrid`, `MusicNote`, `Speakers`, `Headphones`, `TvMonitor`, `ThisPc`, `Clock`, `Document`.

---

## 2. Proposed model

### 2.1 One stage, four modes, eight faces, one accent

```
 Ui.ImmersiveLyrics (Signal<bool>, Shell.cs:1098)  ──► Flow.Show mounts Stage.View()           [unchanged mount site]
      │                                               ├─ UseLayoutEffect: WindowSetFullscreen(true) … restore on unmount   (§4.6, the Video.UI.cs:1213-1218 shape)
      │                                               ├─ FrameRules.ChromeMounted(resolved, immersive) ⇒ ChromeRow + PlayerBarDock UNMOUNT
      │                                               └─ ContentRegion.Visible = !ImmersiveLyrics  ⇒ the shell body stops recording AND goes inactive (its UseIntervals pause)
 Prefs.Stage.Mode()        ∈ Stage.Mode { Lyrics, Visualizer, Queue, Artist }    persisted int (Platform.Keys.StageMode)
 Prefs.Stage.Visualizer()  ∈ Visualizer.Kind { Field, Halo, Horizon, Matrix, Aurora, Spectrum, Pulse, Tape }   persisted int
 Prefs.Stage.{Sensitivity, LyricsOverlay, SyncOffsetMs, Calm, GalleryOpen, TipSeen}                           persisted
 Stage.Layout.Resolve(w, h, mode, galleryOpen, prev)  ──► Aspect + every DIP the renderer lays out
 Visualizer.Clock (30 Hz, UI)  ◄── Playback.Audio.CopySpectrum / Levels / TrackWaveform / TrackBeats / Tempo
 Stage accent  = Design.StageInk.Accent(coverUrl) ──► AccentSet.From(accent) ──► control Styles + bound Prop<ColorF> fills
```

**Modes.** `Stage.Mode : byte { Lyrics = 0, Visualizer = 1, Queue = 2, Artist = 3 }` — persisted ints, append-only, `Stage.ModeRules.Coerce(int)` maps anything else to `Lyrics`. The mode replaces `Stage.Pane` (`Stage.cs:363-374`, deleted). Last mode wins ACROSS sessions now (it is a preference, not session state) — **D1**.

**(Superseded at integration — see D16: the stage follows the app theme.)** **The stage is dark-only (O7, D16).** It is immersive media: in BOTH app themes it paints the dark stage ink and material — every colour comes from `Design.StageInk` (`Platform/Design.cs:608-653`, the `Live` arm resolved by `StageInk.Arm(ThemeKind)`), the backdrop is the blurred cover under a 0.22–0.56 black scrim, the acrylic cards are `AcrylicSpec.InAppBase` over that scrim, and `AccentSet.From(accent)` is derived for a dark surface. Nothing in the stage reads `Tok.*` theme colours; a light-theme app shows the same stage.

**Faces.** `Visualizer.Kind : byte { Field = 0, Halo = 1, Horizon = 2, Matrix = 3, Aurora = 4, Spectrum = 5, Pulse = 6, Tape = 7 }`, default **Horizon** (the prototype's `startViz`). Each kind declares what it needs (`Visualizer.Catalog.NeedsOf(kind)`):

| kind | source tier | engine lease | with no live FFT (Connect / `--fake` / remote / muted) |
|---|---|---|---|
| Field | `Breath` (RMS) | Level | breath holds at its rest opacity |
| Halo | `Spectrum` | Spectrum | ring bars rest at 0.1 scale, glow at rest |
| Horizon | `Precomputed` (kind 237 bands) | none | needs only the edge; empty edge ⇒ flat hairline |
| Matrix | `Spectrum` | Spectrum | all dots dim, peak dots at row 0 |
| Aurora | `Spectrum` | Spectrum | ribbons drift at their base amplitude (the `t` terms keep moving) |
| Spectrum | `Spectrum` | Spectrum | bars at the 0.02 floor, caps at 0 |
| Pulse | `Beats` (TrackBeats) → `TempoGrid` (kind 222) | none | both absent ⇒ rings paused at rest, cover at scale 1 |
| Tape | `Breath` (RMS) + position | Level | reels keep spinning from position; meter at 0 |

**The one Field under everything.** The Field (four cover-colour blobs, drifting) is ALWAYS the stage backdrop under Lyrics / Queue / Artist (the prototype's `.vz.base`), at `baseOp = 0.62 + low·0.2`; in Visualizer mode the chosen face takes the stage and the base Field's opacity goes to 0.9 (prototype `renderVals.baseOp`). So the drift that `Stage.Drift` did with a 33 ms interval on a 1.3× blurred cover is replaced by four `BoxEl` blobs on engine keyframes (`UseKeyframes(AnimChannel.TranslateX/Y/ScaleX/ScaleY, keys, 28–36 s, loop: true, DepKey.Empty)`), zero ticks, and the blurred cover (`BakedBlurSpec(80, 0.5)`, baked once per track) sits under them static.

### 2.2 Aspect classes and every number (derived, not invented)

Thresholds come from the prototypes' container queries (`Main.dc.html:142-170`): ultrawide ≥ 2:1, portrait ≤ 4:5, compact = height ≤ 460 at ≥ 1:1; the stage's own `WideEnterW = 600` (`Stage.cs:93`) survives as the floor under which Desktop demotes to Compact regardless of height. Hysteresis is a ratio band of 0.05 around each edge (the same asymmetry as today's `PromotionHysteresisW` — the richer class is entered late and left at once).

```
 ratio = W / H
 Portrait   : ratio ≤ 0.80  (enter) / leave at ratio ≥ 0.85
 Ultrawide  : ratio ≥ 2.00  (enter) / leave at ratio ≤ 1.95
 Compact    : H ≤ 460 && ratio ≥ 1.0   (enter) / leave at H ≥ 484 (= 460 + FoldHysteresisH 24)   OR   W < WideEnterW 600
 Desktop    : everything else
```

| | Desktop (1920×1080 ref) | Ultrawide (3440×1440) | Portrait (900×1600) | Compact (1100×440) |
|---|---|---|---|---|
| hero art | `Q4(min(0.28·W, 0.82·H − 340))` → **536** at 1080p, clamp [168, 640] | `Q4(min(0.21·W, 0.82·H − 340))` → 722→**640** cap | `Q4(min(0.14·H, 0.26·W))` → 224 | `Q4(min(0.62·H, W − 420))` → 272 |
| thumb art (Visualizer mode) | 96 | 96 | 64 | 64 |
| identity column X / top | `PadX = Q4(0.058·W)` → **108**, `IdentityTop = Q4(0.122·H)` → **128** (the prototype's `.cv` 112/132 quantised); title at `(PadX, IdentityTop + hero + 24)` = (108, **688**) | `PadX = Q4(0.05·W)`, `Top = Q4(0.12·H)` | identity ROW at top: art left at `(PadX, IdentityTop)`, title/meta right of it at `(PadX + hero + 24, IdentityTop)` | art left, title/meta right of it at `(PadX + hero + 24, IdentityTop)`; the transport card sits RIGHT of the art (`X = PadX + hero + 24`), no pane |
| pane (right column) | `X = PadX + hero + Q4(0.05·W)` → **740** at 1920×1080, `Right = Q4(0.05·W)` (96), `Top = 88`, `Bottom = 168` | same, plus a 19 %-wide side column reserved (the gallery docks there) | full width under the identity row, `Top = IdentityTop + art + 24`, `Bottom = 168` | hidden (`ShowPane = false`); the caption line replaces it |
| title size / line | `TitleLarge` 40/52 (hero) · `Subtitle` 20/28 (thumb) | 40/52 · 20/28 | 28/36 (`Title`) | 24/30 |
| meta size | 18/24 (`BodyLarge`) · 14/20 | 18/24 | 14/20 | 14/20 |
| chips row | shown | shown | shown | hidden |
| transport card | `Pad 24`, `H 112`, full width | same | same | `H 96`, volume slider hidden (mute glyph only) |
| gallery pane | `W 444`, `Top 64`, `Bottom 160`, `Right 24` | docked in the side column, `W = 0.19·W` | full-width sheet, `H = 0.55·H` from the bottom | not offered |
| SelectorBar | labels + icons, 128/item | labels + icons | icons only (`items` = "" with icons) | icons only |

`Q4(x)` = `Floor(x/4)·4` (`ArtQuantum = 4`, `Stage.cs:183`, kept). The transport card and the top bar are the only authored heights; everything else is a fraction of the viewport, so there is no fold ladder any more — the `Control` flags enum and the height ladder (`Stage.cs:51-62`, `:141-213`) are deleted.

**Cover morph.** The hero and the thumb are ONE `BoxEl` (`Key = "stage:cover"`) whose `Width/Height` and position come from `Stage.Layout` for the current mode; the move is `Layout = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Spring(0.45f, 0.90f), SizeMode.ScaleCorrect)` (a FLIP on the same node — the engine's `Element.Layout`, `Element.cs:76`). Title/meta/chips sit in a sibling column whose `Layout` is `TransitionChannels.Position` with the same spring. Reduced motion is a value inside the slab (the FLIP snaps under `Motion.ReducedMotion` by the engine's own policy), so no branch.

### 2.3 Idle

The engine's `PlayerChromeVisibility` runs the clock; the stage owns one instance in `SurfaceCore`, created LAZILY in `Render` with the HOST TIMER clock (`_wake.NowMs` of a `UseTimeout` handle — the `MediaPlayerElement.cs:726-730` shape; never `0`, never `Design.FrameTime`), armed with `PlayerChromeTiming.Default` (idle 3000 ms — the TeachingTip copy says "after three seconds"; **D3** keeps the engine default over the prototype's 2800). Inputs: the root's `OnHoverMove` → `PointerMoved`, `OnPointerExit` → `PointerLeft`, `OnKeyDown` → `Activity(Keyboard)`, `OnPointerWheel` → `Activity(Pointer)`, the transport card / gallery / top bar roots → `SetPointerOverControls`, the device flyout, the "…" menu and the TeachingTip while open → `SetMenuOpen(true)`, a seek drag → `SetScrubbing` (through the new `Shell.SeekBar(onScrubbing:)` seam, §4.12), paused ⇒ `SetPlayback(Paused)` (the machine never hides while paused). After EVERY input and on every wake the owner runs `Sync()`: `_idle.Tick(now)` (its bool is ignored), `_chrome.SetIfChanged(_idle.ChromeVisible)`, the cursor override applied from `_idle.CursorHidden`, and the one-shot timer re-armed at `_idle.NextWakeMs` (`+∞` = nothing pending, no timer) — exactly `MediaPlayerElement.Sync` (`:390-416`). Outputs: `ctx.Chrome : Signal<bool>` drives `Flow.Show(() => ctx.Chrome.Value, …)` around the top bar, the transport card and the gallery — a real unmount whose Show child is a `BoxEl` carrying `Exit = new EnterExit(Dy: ±8, Opacity: 0, Active: true)` (the bare-`ComponentEl` exit rule, §1.8), never alpha 0 — and `_cursorHidden` drives `hooks.SetCursorOverride(this, CursorId.Hidden / null)`. The hairline (3 DIP accent, bottom edge, bound `Transform = ScaleX(position fraction)` from the left edge) is mounted while `!_chrome` only. **D4 (revised)**: ONE `UseTimeout` re-armed from `NextWakeMs`, not a polling interval — it runs only while the machine has a pending deadline, so an idle-ineligible stage (paused, over controls, menu open) costs nothing.

### 2.4 Accent

`accent = Design.StageInk.Accent(coverUrl)` (`Design.cs:644`) is synchronous; `Palette.Watch(url).Value` (`Palette.cs:415`) is the wake-up. The stage derives `AccentSet.From(accent)` ONCE per (url, `Tok.Epoch`) in `SurfaceCore` and provides it inside `Ctx.Provide(Stage.StageContext, ctx, …)`; every control Style is built from the set (`Slider.DefaultStyle with { ValueFill = set.Fill, ThumbFill = set.Fill, … }`, `ToggleSwitch.DefaultStyle with { OnFill = set.Fill, OnHover = set.FillSecondary, OnPressed = set.FillTertiary, OnKnob = set.Ink }`, the SelectorBar pill via `TemplateParts[SelectorBar.PartPill] = b => b with { Fill = set.Fill }`; the 56-DIP play disc is a hand-built circular `BoxEl` filled with `set.Fill/FillSecondary/FillTertiary` — `Button.Create` has no circular variant, so `ButtonPalette.ForAccent` is NOT used). Large fills (the hairline, the ring strokes, the Matrix peak dots, the Halo glow) are bound `Prop<ColorF>` over `Slab.Accent : Signal<ColorF>`; a track change writes the TARGET (`Stage.AccentSignal`) and `Visualizer.Clock` cross-fades `Slab.Accent` LINEARLY from the colour captured at the change over `elapsed / Stage.Tone.CrossFadeMs` (600 ms) — seeded with the current accent at mount, snapped to the target the moment the clock is not running (occluded, reduced motion, settled), because a bound channel snaps (§1.8). Control Styles are LIVE props (`SelectorBar.cs:42,96`, `Slider.cs:309,347`, `ToggleSwitch.cs:133,159`), so a re-render with a new Style re-skins in place — no `Key` remount needed (**the risk in the approved plan's "control style freezing" is resolved by evidence**). `Tok.SetAccent` is never called.

### 2.5 Demand tiers and leases

```
 Visualizer.Demand.For(kind, visualizerMode, stageUp, playing, ownerUs, audioSupported, occluded, reduced)
   !(stageUp && playing && ownerUs && audioSupported && !occluded && !reduced) ? Tier.None
   : !visualizerMode                      ? Tier.Level        ← Lyrics/Queue/Artist: only the base Field's breath is visible (V-U55)
   : NeedsOf(kind) is Spectrum            ? Tier.Spectrum
   : NeedsOf(kind) is Breath              ? Tier.Level
   : Tier.None                                                ← Horizon (precomputed) / Pulse (beats) hold NO engine lease
```
- `Tier.Level` → `Playback.Audio.AcquireLevels()` (today's lease). `Tier.Spectrum` → `Playback.Audio.AcquireSpectrum()` (new; the engine's spectrum lease ALSO implies the level tap, and `SpectrumInfo.WindowRms` carries the PRE-gain RMS of the analysed window so Tape/Field under a spectrum lease are volume-independent too — under a plain level lease they read the post-gain `Levels.Rms`, which IS volume-dependent: the one scoped exception to the independence claim, V-E19).
- The gallery's eight previews hold at most ONE extra lease: the gallery clock reads the same frame the stage clock copied (`Visualizer.Clock` publishes one `Frame` per tick into its `Slab`; previews bind the same slab at preview scale), so previews cost nodes, not analysis.
- Occlusion: `hooks.WindowOccluded?.Value ?? false` (new engine signal, §4.4) — occluded or minimised ⇒ `Tier.None` and the 30 Hz clock stops (`UseInterval(enabled: run)`); window deactivation (alt-tab) does NOT stop it (`IsWindowActive` is deliberately not an input), so a second monitor keeps the ambient.
- `reduced` = `Design.Reduced` (OS). `calm` (the gallery's "Reduce motion" toggle, `Prefs.Stage.Calm()`) is NOT a `Demand.For` argument: it is a VALUE inside the model's `Input` (gain ×0.55, attack 0.25, kick ×0.3, no beat scale), never a lease input and never a hook branch.
- **Prefs never on a hot path (O8).** `Prefs.Stage.*()` is read in ONE `UseSignalEffect` per owner (keyed by `Prefs.Stage.Epoch`) into signals/fields — `SurfaceCore` publishes `Kind`, `GalleryOpen`, `LyricsOverlay`, `Calm`, `Sensitivity`, `SyncOffsetMs` on `StageCtx`; the clock's tick, every bind thunk, every band loop and the gallery/face/top-bar renders read those, never the registry.

### 2.6 Data sources and fallbacks

```
 live FFT (local playback, Owner.Us, Audio.Supported, lease held, !muted)
   └─ else precomputed:  TrackWaveform (kind 237, three bands @ 20 ms)   → Horizon, and a Horizon-derived "level" for Tape/Field breath
         └─ else beats:  TrackBeats (audio-analysis) → Pulse;  else TempoGrid (Track.Tempo ×10, kind 222) phase-locked to position
               └─ else Field breath at rest (fieldOp 0.62, no kick)
```
- `Visualizer.Source : byte { Live, Precomputed, TempoGrid, Breath }` is published per tick (`Slab.Source`) for the diagnostics card and the gallery's "source" caption.
- Precomputed "level": `WaveformBands.LevelAt(payload, posMs) = (low + mid + high)/(3·255)` at the playhead sample — what Tape's meter and Field's breath use under Connect when the edge is present.
- Muted (or volume 0): the engine publishes `SpectrumInfo.Muted = true`; the UI treats it as `Breath` (the bands decay to the floor through the normal release).
- `--fake`: `Audio.Supported` is true (the silent endpoint) but the RMS is ~0 and the spectrum is silence; `Owner.Us` only after a play. The fake seeds the banded waveform for the drawer fixtures (`Entities.Fake.Album.cs:562-569`, migrated) and `Tempo` for every track (`Entities.Fake.cs:207-208`), so Horizon and Pulse fall back as the approved plan expects and Field breathes at rest.

### 2.7 Latency alignment (engine)

```
 ring index space  : CONTENT frames — BlockCtx.StartFrame (AudioGraph.cs:19-20 = the mixer's ConsumeSeq at the block start,
                     CrossfadeMixer.cs:203) is what the RT arms the ring with (SpectrumRing.Arm(startFrame)); re-armed on a
                     demand edge, a RenderEpoch change (CmdReset :967, RecoverStarvation :1312, RebuildSink :1727) or a block
                     discontinuity (startFrame ≠ the previous block's end)
 audible (content) = PlayedFrames + (ConsumeSeqFrames − SubmittedFrames − _pendingFrames)     ← submitted→content domain shift: 0 after
                                                                                                 CmdReset (both counters zero, :962/:966),
                                                                                                 non-zero only after RebuildSink (:1728 zeroes
                                                                                                 submitted/played, ConsumeSeq keeps counting)
                     − _clock.StreamLatencyFrames                                               (IAudioClockSource, MediaSeams.cs:352)
                     − _graph.Live.TotalLatencySamples                                           (the master chain's own latency, AudioGraphHost.cs:20-33)
 user offset       = round(SpectrumOffsetMs · SampleRate / 1000)                                ← AudioEffects.SpectrumOffsetMs (Prefs.Stage.SyncOffsetMs), positive = earlier
 window end (excl) = min(audible − userOffset + FftSize/2, ring.NewestContent)                 ← centred on the audible instant (V-E24); a negative
                                                                                                 offset SATURATES at the newest rendered sample (V-E5)
 analyse           = the 2048 samples ending there, iff they lie after the arm, are written, and are not inside the producer's
                     next block or across a re-arm (SpectrumRing.TryCopyContentWindow)
```
Why this is exact: every rendered frame is tapped exactly once with its content index (`RenderBlock` builds `ctx` from `_mixer.ConsumeSeq` BEFORE
`_mixer.Render` advances it, `:1518-1522`), so the ring's content→index mapping is a pure offset per arm. `_playedFrames` is refreshed by
`PublishPosition` (`:1619-1620`) immediately before `PublishVisualizer` (`:1423`) on the same thread, bounded by `SubmittedFrames`. The
engine's own position pipeline subtracts the same two latencies (`AudioClock.cs:81-82, :95` via `_position.ExtraLatencySamples = graph.TotalLatencySamples`
at `:1538`), so the picture and the lyrics agree on "now". The ring holds `RoundUpToPowerOf2(1.5 · rate)` mono samples (131,072 at 48 kHz =
2.7 s): device latency (Bluetooth ≈ 200–400 ms) plus the ±500 ms user offset plus the RT burst, with one `maxBlock` of torn-read guard.

**Cross-plan note (issue #167, `playback-smoothness-implementation.md`).** `PcmAudioSession.OutputDelayFrames` (= `max(0, Submitted − Played +
StreamLatencyFrames)`) is kept ONLY as a diagnostic on the Diagnostics card; the spectrum is NOT aligned by it. The playback plan's F2 submits
silence and freezes `Played` as a content clock — the content-domain alignment above survives that, because `PlayedFrames` stays the audible
content position. The raw endpoint queue the playback plan reports is `CapacityFrames − WritableFrames` (`IBufferedAudioSink`, `MediaSeams.cs:333-336`;
negative on `NullAudioSink`/device failure → clamp ≥ 0; a COM call on WASAPI, so never per tick). The tap is anchored "immediately before
`_masterGain.Process`" BY SYMBOL (X2): after D7 moves the limiter and EQ ahead of `_masterGain`, the spectrum is post-EQ/limiter and pre-volume —
intended, the visualizer reflects the EQ; the volume-independence claim holds. The playback audit's §9 statement that the visualizer window is
aligned by `OutputDelayFrames` is superseded by the content-domain alignment above — recorded HERE only; the audit document is not edited (V-E11).

### 2.8 The FFT

`SpectrumAnalyzer(sampleRate, fftSize = 2048, bandCount = 48)`: periodic Hann window, radix-2 iterative real FFT (the 2048-point complex FFT with `im = 0`; twiddles and bit-reversal precomputed), band power = Σ over the band's bins of `re² + im²`, normalised by `4 / (N · Σw²)` (Parseval: the one-sided energy of a windowed unit sine is `N·Σw²/4`, so a sine whose main lobe falls inside one band reads **0 dB in that band**, not in its peak bin — a Hann lobe spans ±2 bins and its summed energy is 1.5× the peak bin's), log-spaced band edges from `MinHz = 40` to `MaxHz = min(16000, 0.45·sampleRate)` (`edge(b) = MinHz · (MaxHz/MinHz)^(b/48)`, every band ≥ 1 bin; bin 0 — DC — is in no band), `dB = 10·log10(power·norm + 1e-12) + 3·log2(fc/MinHz)` (the +3 dB/oct pink-tilt), THEN clamped to `[-80, +6]` — so a full-scale sine above ≈ 160 Hz hits the +6 ceiling because of the tilt; the tests use −30 dBFS or ≤ 100 Hz for the 0 dB check. Cost ≈ 11 k butterflies per publish, well under 100 µs; it runs ~60×/s on `FluentGpu.AudioClock`.

UI normalisation (CORE `Visualizer.Bands`, in this order every tick): `u = clamp((dB − Floor) / (Ceiling − Floor), 0, 1)` with `Floor = −60`, `Ceiling = −6`; **AGC** — a slow running maximum of the frame's max unit value (`agc = max(frameMax, agc·0.995)`, floored at 0.40) normalises the frame by `min(2, 1/agc)` so a quiet master and a loud one fill the same range; THEN `u = min(1, u · sensitivity · calmGain)` (sensitivity 0.3–1.5, calm ×0.55) so the user's gain is applied to an already-normalised frame (V-U32); then the prototype's follower (`attack 0.55 / release 0.10`, calm attack 0.25) and peak hold (10 ticks, fall 0.018/tick) — the constants from `Flagship.dc.html:476-488`.

### 2.9 Reduced motion

- `Design.Reduced` ⇒ `Tier.None` (no leases, no 30 Hz clock); faces render their rest pose; the Field blobs pass REST keyframes with `loop: false` under reduced motion (`UseKeyframes(ch, reduced ? s_rest : keys, ms, loop: !reduced, DepKey.From(index, w, h, reduced ? 1 : 0))`) — the engine does **not** snap looping keyframes (`AnimScheduler.Timeline.cs:35-55` never consults reduced motion, §1.8); the cover FLIP snaps (the layout engine's own policy); Enter/Exit keep fades (`KeepFade` tokens); the accent cross-fade snaps to its target (the clock is not running).
- `Prefs.Stage.Calm()` ⇒ gain 0.55, attack 0.25, kick ×0.3, Pulse rings stop expanding (a static ring at 0.6 opacity), Halo/Matrix keep moving — the prototype's `calm` semantics (`Flagship.dc.html:476-490`).

### 2.10 Budgets

| | nodes | ticks | alloc |
|---|---|---|---|
| Field | 4 blobs + 1 wash | 0 (keyframes) + 1 bound opacity | 0 |
| Halo | 72 bars + glow + cover = 74 bound | 30 Hz slab | 0 |
| Horizon | 3 `SeriesEl` + past veil + head = 5 | 30 Hz slab (3 series × 181) | 0 |
| Matrix | 32 columns × (dim track + lit bar + peak dot) = 96 (64 bound) + 12 static row carvers = 108 | 30 Hz | 0 |
| Aurora | 3 `SeriesEl` | 30 Hz (3 × 65) | 0 |
| Spectrum | 48 bars + 48 caps + 48 reflections = 144 bound | 30 Hz | 0 |
| Pulse | 4 rings + glow + cover = 6 bound | 30 Hz | 0 |
| Tape | 2 packs + 2 hubs + tape path (static) + 20 meter cells = 26 | 30 Hz | 0 |
| gallery previews | 8 × ≤ 60 (previews cap Halo at 36 bars, Spectrum at 24 bars without reflections, Matrix at 16 columns + 12 carvers) | the same slab | 0 |

Every face is ≤ 600 animated nodes (the largest is Spectrum at 144); the whole stage with the gallery open is ≤ 700. Every tick: `Peek` only, scalar maths, `FloatSignal.SetIfChanged` inside one `Runtime.Batch`.

### 2.11 Persistence — the keys (Platform.Settings.cs partial, **D5**)

```
 stage.mode                int    0  (Stage.Mode.Lyrics)     Coerce → Lyrics
 stage.visualizer          int    2  (Visualizer.Kind.Horizon)
 stage.sensitivity         float  1.0   clamp [0.3, 1.5]
 stage.lyricsOverlay       bool   true
 stage.syncOffsetMs        int    0     clamp [−500, 500]
 stage.calm                bool   false
 stage.galleryOpen         bool   true  (the prototype starts with the pane open)
 stage.tipSeen             bool   false
```
One epoch `Prefs.Stage.Epoch` (reader/writer pairs per key, the `Prefs.Lyrics` shape). `Platform.Keys.AutoplayEnabled` stays the Up-next autoplay source.

### 2.12 `SeriesEl` — the chunked fixed-POD design (**D6**, with evidence)

The guardrail asks for "a new DrawOp and pipeline variant" for a bound sample source of N ≤ 512 with a ≤ 4-stop gradient and no `PathData` per frame. Three engine facts shape it (§1.8): the stream is fixed-size POD, the headless translate buffer caps a body at 1024 B, and the only out-of-line store is a render-thread cache that grows per epoch. So `DrawSeriesCmd` carries **32 samples inline** and a series of N samples is recorded as ⌈(N−1)/31⌉ chunks (31 new samples + 1 shared edge sample each; 181 points → 6 chunks, 65 → 3), each with its own cull rect. Nothing variable-length rides the stream, clean-span reuse stays valid (the data is IN the span), `DrawOpTranslate`/`SliceOpBounds`/`TryBodySize` are plain arms, and the pipeline is one `DrawInstanced(64, chunkCount)` triangle-strip pass with `SV_VertexID` expansion (2 vertices per sample; instancing restarts the strip per chunk). The gradient (≤ 4 stops) rides the command as colours + offsets and is evaluated by amplitude in the vertex shader (a baseline-to-peak ramp, the prototype's `linear-gradient(0deg, c2, c1 70%, #fff)`). Three shapes: `Baseline` (area from the baseline up), `Mirrored` (area ± amplitude about the baseline — Horizon), `Stroke` (a constant-width ribbon around the polyline — the gallery previews' outlines and `Charts/Waveform` later). No AA fringe in v1 (the fills are soft gradients; documented in the canon row). `SeriesEl.Samples : Prop<SeriesSamples>` is the bound source (`SeriesSamples(float[] Array, int Count, uint Version)`, value-equal by array reference + count + version), wired exactly like `ListRowEl.Cells`.

---

## 3. Component trees, wireframes, data flow

### 3.1 The stage tree (after)

```
Shell.UI.cs ZStack (:496)
└─ Flow.Show(Ui.ImmersiveLyrics, BoxEl{ Enter/Exit = Stage.EnterTerminal/ExitTerminal, Children = [Stage.View()] })
   └─ SurfaceCore : Component                                   Shell/Stage.UI.cs
      ├─ _ctx : StageCtx (ONE instance of SIGNALS: Layout, Mode, Kind, GalleryOpen, LyricsOverlay, Calm, Sensitivity, SyncOffsetMs,
      │                   Chrome, HasTimedLyrics, Accent, Palette, TrackKey, GalleryButton, Slab + the actions)
      ├─ effects: viewport → Layout.SetIfChanged · Prefs.Stage epoch → the preference signals (the ONLY Prefs.Stage reads) ·
      │           Tracks.Changed → TrackKey.SetIfChanged (the one track subscription) · (cover, Tok.Epoch) → Accent/Palette/AccentSignal
      ├─ hooks: WindowSetFullscreen(true) at mount / restore at unmount; PushFocusScope; SetCursorOverride
      ├─ idle:  PlayerChromeVisibility on _wake.NowMs (UseTimeout), Sync() after every input, re-armed from NextWakeMs → ctx.Chrome, cursor
      ├─ Ctx.Provide(Stage.StageContext, _ctx, …)
      └─ BoxEl root (Width/Height = L.W/L.H, ZStack, ClipToBounds, Focusable, Fill = Ink.Floor; OnHoverMove/OnPointerExit/OnPointerWheel/OnKeyDown)
         ├─ Embed Backdrop                 [Key "stage:backdrop"]  ZStack, HitTestVisible = false
         │  ├─ Embed BackdropArt(url)        ← TWO ImageEls (previous + current, Key = "bd:" + url), BakedBlurSpec 80/0.5, the old stays until the new fades in
         │  ├─ Visualizer.FieldFace(slab, opacity: slab.BaseFieldOp)  [Key "stage:field"]   ← the four blobs on keyframes (rest keys under reduced motion)
         │  └─ BoxEl scrim  Fill = Shade(mode == Visualizer ? 0.22 : 0.56) static, BrushTransitionMs 600
         ├─ Flow.Show(mode == Visualizer, BoxEl "stage:face" { Stretch/Stretch, Enter scale .97 fade, Exit fade })
         │  └─ Embed FaceHost → BoxEl (Width = W − FaceRight) → BoxEl { Key = "viz:" + kind, Animate }   ← the KEYED CHILD remounts per kind
         │       └─ Visualizer.Face(kind, slab, palette, FaceSpec)
         ├─ BoxEl smoke "stage:smoke" (Height 440, AlignSelf End · JustifySelf Stretch, gradient)  Visible bound mode == Visualizer
         ├─ Embed NowPlayingCard "stage:npc"  → Flow.Show(Visualizer && ShowChips, Acrylic 460×136 at (24, 64), Start/Start)
         ├─ Embed Hero "stage:identity"     ZStack, HitTestPassThrough (the MetaLink inside is clickable)
         │  ├─ BoxEl "stage:cover"  Start/Start, Margin (CoverX, CoverY), Width/Height = CoverSize(mode)   Layout FLIP (Bounds, ScaleCorrect)
         │  │  └─ Controls.Artwork(url, size, size, corners, morphKey: Stage.Entry.MorphKey, decodePx: 512)
         │  └─ BoxEl "stage:titles" Start/Start, Margin (TitleX, TitleY)   Layout FLIP (Position)
         │     ├─ TextEl title (TitleLarge / Subtitle by mode)
         │     ├─ Embed MetaLink(Props size/line/accent) "Artist · Album"   (Close(begin,"navigate") before GoTo)
         │     └─ Embed Chips "stage:chips" (if ShowChips)  [format badge][Synced lyrics]{bpm}
         ├─ Embed PaneHost "stage:pane"     Start/Start at (PaneX, PaneTop), PaneW × PaneH, Visible bound ShowPane && ShowsPane(mode)
         │  └─ Flow.KeepAlive(mode, m => "pane:" + m, m => m switch {
         │        Lyrics  => PaneFrame(Lyrics.StagePane(slab.Accent))      (ViewCore large/onMedia/onStage, blur forced 0)
         │        Queue   => PaneFrame(Embed QueuePane)                    (header + Autoplay toggle + Stage.QueuePaneBody())
         │        Artist  => PaneFrame(Embed ArtistPane)                   (hero image, Go to artist, bio card, credits)
         │        Visualizer => empty })      PaneFrame: Enter Dy 40 fade, Exit fade
         ├─ Flow.Show(ShowsCaption(mode, overlay, hasTimed), BoxEl "stage:caption" { AlignSelf End · JustifySelf Stretch, Margin above the transport, Enter Dy 12 / Exit fade })
         │  └─ Embed CaptionHost → column → BoxEl { Key = "caption:" + line } (active line + secondary)
         ├─ Embed ChromeHost "stage:chrome"  ZStack, HitTestPassThrough
         │  ├─ Flow.Show(chrome, BoxEl "chrome:top" { AlignSelf Start · JustifySelf Stretch, H 48, Enter/Exit Dy −8 })      → Embed TopBar (Grow)
         │  │     brand · SelectorBar(Lyrics|Visualizer|Up next|Artist, pill via TemplateParts) · [gallery toggle → ctx.GalleryButton][exit]
         │  ├─ Flow.Show(chrome, BoxEl "chrome:transport" { AlignSelf End · JustifySelf Stretch, Margin (TransportLeft, 0, 24, 24), Enter/Exit Dy +8 }) → Embed TransportCard (Grow)
         │  │     SeekRow(time · Shell.SeekBar(onScrubbing) · time) · r2(context | shuffle prev PLAY next repeat | like mute volume device more)
         │  └─ Flow.Show(chrome && Visualizer && galleryOpen && ShowGallery, BoxEl "chrome:gallery" { side: Start/End · sheet: End/Stretch, Enter/Exit Dx 32 | Dy 32 }) → Embed GalleryHost
         │        └─ Visualizer.Gallery(ctx, L, close): header ✕ · ScrollEl[ ItemsView.Create(8, tile, Grid(2, 128, 10)) keyed by palette · Settings rows on ctx signals ]
         ├─ Flow.Show(!chrome, BoxEl "stage:hairline" { AlignSelf End · JustifySelf Stretch, H 3 })  → BoxEl Grow, Fill = slab.Accent, Transform = ScaleX(frac)
         ├─ Embed LyricFacts "stage:facts"     (zero-size; writes ctx.HasTimedLyrics, ensures lyrics)
         └─ Embed Visualizer.Clock "stage:clock" (zero-size, 30 Hz UseInterval gated on a settled SIGNAL, leases by the visible consumer, writes the slab inside Runtime.Batch)
TeachingTip.Show(overlay, () => ctx.GalleryButton.Peek(), …) once, when mode == Visualizer && !TipSeen — holds the chrome while open
```

### 3.2 Desktop 1920×1080 — Lyrics mode

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ▣ Wavee  Now playing · full screen        [≡ Lyrics] [▥ Visualizer] [≣ Up next] [◯ Artist]      ⚙ ⧉ [⤢ Exit full screen Esc] │ 48
│                                                 ‾‾‾‾‾‾‾ accent pill                                                             │
│                                                                                                                              │
│            ┌──────────────────────────┐        ┌────────────────────────────────────────────────────────┐ 88                   │
│            │                          │        │  Streetlights hum a quiet tune                    .36 │                      │
│            │                          │        │                                                        │                      │
│   108      │        cover 536         │  740   │ ▌불 꺼진 도시 위로                                 1.0 │ ← accent pill 3×28   │
│   (top 128)│     (Layout FLIP node)   │        │  Over the city, lights all out    (secondary 20/28)    │   44/54 Display 600  │
│            │                          │        │                                                        │                      │
│            │                          │        │  We dive in where the river bends                 .36 │                      │
│            └──────────────────────────┘        │  우리는 천천히 헤엄쳐                              .36 │                      │
│ 688        Night Swim                  40/52   │  …                                                     │                      │
│ 744        Nara Vale · Undertow        18/24   │  (ScrollEl, EdgeFadeSpec.Vertical 14%/26%)             │                      │
│ 780        [▮ OGG 320] [≡ Synced lyrics]       └────────────────────────────────────────────────────────┘ 168                  │
│                                                                                                                              │
│ ┌──────────────────────────────────────────────────── acrylic transport card 112 ─────────────────────────────────────────┐ │
│ │  0:12 ────────●────────────────────────────────────────────────────────────────────────────────────────────────  3:58  │ │ 24
│ │ Playing from album           ⇄   ◀◀   (▶ 56)   ▶▶   ⟳                          ♡  🔈 ───●────  [📱 This PC ▾]  ⋯    │ │
│ │ Undertow                                                                                                                │ │
│ └─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
 backdrop: blurred cover (σ80, static) under 4 drifting Field blobs (keyframes) under a 0.56 scrim
```

### 3.3 Desktop — Visualizer mode (Horizon) with the gallery pane

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ▣ Wavee  Now playing · full screen        [≡ Lyrics] [▥ Visualizer] [≣ Up next] [◯ Artist]      [⚙ on] ⧉ [⤢ Exit Esc] │
│ ┌ acrylic 460×136 ──────────────────────────┐                                            ┌ acrylic gallery 444 ──────┐ 64
│ │ ┌────┐  Night Swim              20/28     │                                            │ Visualizer              ✕ │
│ │ │ 96 │  Nara Vale · Undertow    14/20     │                                            │ ┌─────────┐ ┌─────────┐   │
│ │ └────┘  [OGG 320][Synced][112 BPM][8A]    │                                            │ │ Field   │ │ Halo    │   │ 78-px live
│ └───────────────────────────────────────────┘                                            │ │ Ambient │ │ Spectrum│   │ previews
│                                                                                          │ └─────────┘ └─────────┘   │
│                      ▁▂▃▅▆▇█▇▆▅▃▂▁▂▃▅▇█▇▅▃▂▁▁▂▃▅▆▇█▇▆▅▃▂▁ ← low  (SeriesEl Mirrored)      │ ┌─────────┐ ┌─────────┐   │
│   past veil ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│ ▂▃▄▅▄▃▂▁▂▃▄▅▄▃▂▁ ← mid, hi                        │ │ Horizon✓│ │ Matrix  │   │ accent 2-px
│                                     │ playhead (2 px white)                               │ └─────────┘ └─────────┘   │ border + ✓
│                                                                                          │ ┌─────────┐ ┌─────────┐   │
│                                                                                          │ │ Aurora  │ │Spectrum │   │
│                         "Streetlights hum a quiet tune"      36/44 overlay caption       │ └─────────┘ └─────────┘   │
│                              secondary line 18/24                                        │ ┌─────────┐ ┌─────────┐   │
│                                                                                          │ │ Pulse   │ │ Tape    │   │
│ ┌──────────────────────────────── transport card ───────────────────────────┐            │ └─────────┘ └─────────┘   │
│ │  0:12 ──────●───────────────────────────────────────────────────  3:58    │            │ Settings                  │
│ │ Playing from album  ⇄ ◀◀ (▶) ▶▶ ⟳           ♡ 🔈 ──●── [This PC ▾] ⋯     │            │ ◎ Sensitivity   ───●──    │
│ └───────────────────────────────────────────────────────────────────────────┘            │ ▭ Lyrics over visualizer ●│
│                                                                                          │ ∿ Reduce motion         ○ │
│                                                                                          │ ◷ Sync offset   ───●── ms │
└──────────────────────────────────────────────────────────────────────────────────────────┴───────────────────────────┘ 160
 ↑ the stage face is right-inset 492 while the pane is open (prototype .W.m-viz.pane .stg { right: 492px })
 TeachingTip "Pick your visualizer" anchored to [⚙], first entry only
```

### 3.4 Desktop — Up next and Artist panes (right region, 768→1824 × 88→912)

```
 Up next                                  Artist
 ┌──────────────────────────────────────┐ ┌──────────────────────────────────────────┐
 │ Up next                 ∞ Autoplay ● │ │ ┌──────────── hero 240 ────────────────┐ │
 │ NOW PLAYING                          │ │ │ Artist                                │ │
 │ ▌[48] Night Swim      Undertow  3:58 ≡│ │ │ Nara Vale   2,481,306 monthly        │ │
 │ NEXT FROM UNDERTOW                   │ │ │               [Follow] [Go to artist] │ │
 │  [48] Low Tide        Undertow  4:12 ⋯│ │ └──────────────────────────────────────┘ │
 │  [48] Paper Moons     Undertow  3:21 ⋯│ │ ┌ bio card ───────────────────────────┐ │
 │  [48] Glasshouse      Undertow  4:40 ⋯│ │ │ Seoul-born, London-based … (70ch)   │ │
 │  (Stage.QueuePaneBody — Queue.UI.cs) │ │ └─────────────────────────────────────┘ │
 │                                      │ │ ┌ On tour ──────┐ ┌ Credits · Night Swim ┐│
 │                                      │ │ │ OCT 14 Seoul  │ │ Performed by  Nara V.││
 │                                      │ │ │ NOV 2  Tokyo  │ │ Written by    N., J. ││
 │                                      │ │ │ NOV 19 London │ │ Produced by   Ory L. ││
 └──────────────────────────────────────┘ └─└───────────────┘ └──────────────────────┘┘
 rows: 64 high, 48 art, grid 48 | 1fr | 220 | 64 | 32        Artist = Controls.ArtistAboutCard(artist, AboutLayout.Rail) + Edges.TrackCredits + ArtistConcerts edge rows
```

### 3.5 Ultrawide 3440×1440, Portrait 900×1600, Compact 1100×440

```
 Ultrawide (ratio 2.39)                                           Portrait (0.56)                    Compact (2.5, H 440)
┌───────────────────────────────────────────────────────────────┐ ┌─────────────────────────┐        ┌────────────────────────────────────────────┐
│ brand            [Lyrics][Visualizer][Up next][Artist]   ⚙ ⤢ │ │ brand  [≡][▥][≣][◯] ⚙ ⤢│        │ brand   [≡][▥][≣][◯]                 ⚙ ⤢  │
│  ┌──────────┐   ┌─────────────────────────────┐ ┌───────────┐│ │ ┌────┐ Night Swim 28/36 │        │ ┌──────────┐ Night Swim        24/30     │
│  │          │   │ lyrics / face / queue       │ │ gallery   ││ │ │224 │ Nara Vale·Undertow│        │ │  272     │ Nara Vale · Undertow      │
│  │ cover 640│   │                             │ │ docks in  ││ │ └────┘ [OGG 320][Synced]│        │ │          │ "Streetlights hum a…" caption│
│  │          │   │                             │ │ the 19 %  ││ │ ┌─────────────────────┐ │        │ │          │ ⇄ ◀◀ (▶52) ▶▶ ⟳  ♡ 🔈 ⋯    │
│  └──────────┘   │                             │ │ side col  ││ │ │ pane full width     │ │        │ └──────────┘ 0:12 ────●──────────── 3:58 │
│  Night Swim     │                             │ │ (previews ││ │ │ lyrics 20–40 cqw    │ │        └────────────────────────────────────────────┘
│  Nara Vale      │                             │ │  stacked) ││ │ │                     │ │         no pane, no gallery, no chips; the caption line
│  [chips]        └─────────────────────────────┘ └───────────┘│ │ └─────────────────────┘ │         is the lyric; transport card 96 high, no volume slider
│ ┌──── transport card ─────────────────────────────────────┐  │ │ ┌ transport (volume ↓)┐ │
│ └─────────────────────────────────────────────────────────┘  │ │ └─────────────────────┘ │
└───────────────────────────────────────────────────────────────┘ │ gallery = bottom sheet │
                                                                  │ 55 % H when open        │
                                                                  └─────────────────────────┘
```

### 3.6 The engine data flow (RT → clock → UI)

```
 FluentGpu.AudioRT (RenderBlock, PcmAudioPlayer.cs:1456-1547)
   _mixer.Render(buf, frames, ctx) ──► TapSpectrumBlock(buf, frames, ctx.StartFrame)   [NEW, immediately BEFORE _masterGain.Process — by symbol]
                              │  if SpectrumDemand(source) == 0 → return      (two Volatile reads, no lock)
                              │  Arm(startFrame) on a demand edge / RenderEpoch change / block discontinuity
                              │  ring.Write(interleaved → mono L+R/2)            SpectrumRing (SPSC, 2^17 floats, monotonic index, content base)
   _masterGain.Process …  ──► TapBlock(buf, frames)  (UNCHANGED: RMS/peak post-everything → AudioLevelMailbox)

 FluentGpu.AudioClock (Advance → PublishPosition → PublishVisualizer, :1418-1423)
   PublishVisualizer():  level frame as today (Magnitudes stays EMPTY — O2)  +  PublishSpectrum():
      audible = Played + domainShift − StreamLatencyFrames − TotalLatencySamples;  end = min(audible − offset + 1024, ring.NewestContent)
      ring.TryCopyContentWindow(end, _specWindow[2048])  ──► SpectrumAnalyzer.Analyze(window, _specBands[48])   (Hann · FFT · 48 log bands · dB +3 dB/oct)
      ae.PublishSpectrum(source, epoch, bands, muted, windowRms, alignFrames, fftMs)   ← lock(_visualizerGate): write BACK buffer, swap, seq++

 UI thread, 30 Hz (Visualizer.Clock, UseInterval)
   Playback.Audio.CopySpectrum(_db[48], out info)   ← lock(_visualizerGate): memcpy FRONT buffer (no tear) — THE ONLY spectrum read path
   Visualizer.Bands.Normalize → Follow → PeakHold   (CORE, preallocated float[48] ×3)
   per-kind fold (Halo/Matrix/Spectrum/Aurora/Horizon/Pulse/Tape/Field)  → Frame   (CORE, zero alloc)
   Runtime.Batch(write):  FloatSignal.SetIfChanged ×N  +  SeriesSource.Version++ ×6     (ONE frame request)
        │
        ├─ bound Transform/Opacity/Fill props on BoxEls   → compositor-only column writes (no render, no layout)
        └─ SeriesEl.Samples thunk (reads Version) → BindSeriesSamples → scene.SetSeriesSamples(node, span) → MarkRecordDirty
                 → SceneRecordingSnapshot captures the pooled copy → SceneRecorder emits ⌈(N−1)/31⌉ DrawSeriesCmd (32 samples each)
                 → D3D12 SeriesPipeline: StructuredBuffer<Inst>, DrawInstanced(64, chunks), SV_VertexID strip, gradient by amplitude
```

---

## 4. Real code

Every block below compiles against HEAD `c060e9b7` (app) / `c50e900d` (engine). "Replaces `:a-b`" means the cited lines are deleted and the block takes their place; "insert after `:n`" means the block goes between `:n` and `:n+1`. Engine files use file-scoped `namespace FluentGpu.Media;` etc. with explicit `using`s (the convention in `Media/Playback/Audio/*`, §1.2); app files use `namespace Wavee;` with implicit `System` usings (`Directory.Build.props:8-12`).

### 4.1 Engine — `SpectrumAnalyzer` and `SpectrumRing` (WP-E1)

**NEW `..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\Audio\SpectrumAnalyzer.cs`**

```csharp
using System;
using System.Numerics;

namespace FluentGpu.Media;

/// <summary>
/// The visualizer's spectrum analyzer (spec §7.8: "the Tap node's lock-free ring; a non-RT tick runs the FFT"). A
/// radix-2 iterative FFT over a Hann-windowed 2048-sample mono window, reduced to 48 log-spaced bands between 40 Hz
/// and min(16 kHz, 0.45·rate), reported in dB with a +3 dB/octave pink tilt and clamped to [−80, +6]. Every table
/// (window, twiddles, bit-reversal, band edges, tilt) is built ONCE in the constructor; <see cref="Analyze"/>
/// allocates nothing. Pure math, AOT-safe. CONTROL-THREAD ONLY: the RT path only fills the <see cref="SpectrumRing"/>.
/// Normalisation (Parseval): band power is Σ|X_k|² over the band's bins × 4/(N·Σw²), so a full-scale sine whose Hann
/// main lobe falls inside ONE band reads 0 dB in that band (before the tilt) — the one-sided windowed-signal energy
/// N·Σw²·A²/4 maps to A². The clamp to [FloorDb, CeilingDb] is applied AFTER the tilt.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int DefaultFftSize = 2048;
    public const int DefaultBandCount = 48;
    public const float MinHz = 40f;
    public const float MaxHzCap = 16_000f;
    public const float FloorDb = -80f;
    public const float CeilingDb = 6f;
    public const float TiltDbPerOctave = 3f;

    private readonly int _n, _bands, _rate;
    private readonly float[] _window, _re, _im, _cos, _sin, _tilt;
    private readonly int[] _bitRev, _bandLo, _bandHi;   // bin range [lo, hi) per band
    private readonly float _norm;

    /// <summary>Window length in samples (a power of two).</summary>
    public int FftSize => _n;
    /// <summary>Bands per analysis.</summary>
    public int BandCount => _bands;
    /// <summary>The rate the band edges were laid out for; a session re-creates the analyzer when its rate changes.</summary>
    public int SampleRate => _rate;
    /// <summary>The top band's upper edge in Hz.</summary>
    public float MaxHz { get; }

    public SpectrumAnalyzer(int sampleRate, int fftSize = DefaultFftSize, int bandCount = DefaultBandCount)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fftSize < 16 || !BitOperations.IsPow2(fftSize)) throw new ArgumentOutOfRangeException(nameof(fftSize), "a power of two ≥ 16");
        if (bandCount < 1 || bandCount > fftSize / 4) throw new ArgumentOutOfRangeException(nameof(bandCount));
        _n = fftSize;
        _bands = bandCount;
        _rate = sampleRate;
        _window = new float[fftSize];
        _re = new float[fftSize];
        _im = new float[fftSize];
        _cos = new float[fftSize / 2];
        _sin = new float[fftSize / 2];
        _bitRev = new int[fftSize];
        _bandLo = new int[bandCount];
        _bandHi = new int[bandCount];
        _tilt = new float[bandCount];

        double sumSq = 0;
        for (int i = 0; i < fftSize; i++)
        {
            double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / fftSize));   // periodic Hann
            _window[i] = (float)w;
            sumSq += w * w;
        }
        _norm = (float)(4.0 / (fftSize * sumSq));   // Parseval: Σ_band |X_k|² of a unit sine → 1.0 → 0 dB (the lobe's energy, not the peak bin)

        for (int k = 0; k < fftSize / 2; k++)
        {
            double a = -2.0 * Math.PI * k / fftSize;
            _cos[k] = (float)Math.Cos(a);
            _sin[k] = (float)Math.Sin(a);
        }
        int log2n = BitOperations.Log2((uint)fftSize);
        for (int i = 0; i < fftSize; i++) _bitRev[i] = (int)(ReverseBits((uint)i) >> (32 - log2n));

        MaxHz = MathF.Min(MaxHzCap, 0.45f * sampleRate);
        double binHz = (double)sampleRate / fftSize;
        double ratio = MaxHz / MinHz;
        int prevHi = Math.Max(1, (int)Math.Round(MinHz / binHz));
        for (int b = 0; b < bandCount; b++)
        {
            double fLo = MinHz * Math.Pow(ratio, (double)b / bandCount);
            double fHi = MinHz * Math.Pow(ratio, (double)(b + 1) / bandCount);
            int lo = Math.Max(prevHi, (int)Math.Round(fLo / binHz));
            int hi = Math.Max(lo + 1, (int)Math.Round(fHi / binHz));
            hi = Math.Min(hi, fftSize / 2);
            lo = Math.Min(lo, hi - 1);
            _bandLo[b] = lo;
            _bandHi[b] = hi;
            prevHi = hi;
            double fc = Math.Sqrt(fLo * fHi);
            _tilt[b] = (float)(TiltDbPerOctave * Math.Log2(fc / MinHz));
        }
    }

    /// <summary>The band whose [lo, hi) bin range holds <paramref name="hz"/>, or −1 outside every band.</summary>
    public int BandOf(float hz)
    {
        int bin = (int)Math.Round(hz * _n / (double)_rate);
        for (int b = 0; b < _bands; b++) if (bin >= _bandLo[b] && bin < _bandHi[b]) return b;
        return -1;
    }

    /// <summary>The geometric centre of band <paramref name="b"/> in Hz.</summary>
    public float BandCenterHz(int b)
    {
        double binHz = (double)_rate / _n;
        return (float)Math.Sqrt(_bandLo[b] * binHz * (_bandHi[b] * binHz));
    }

    /// <summary>Analyse one window. <paramref name="mono"/> must be exactly <see cref="FftSize"/> samples;
    /// <paramref name="bandsDb"/> receives <see cref="BandCount"/> values in [FloorDb, CeilingDb]. Zero allocation.</summary>
    public void Analyze(ReadOnlySpan<float> mono, Span<float> bandsDb)
    {
        if (mono.Length != _n) throw new ArgumentException("the window must be exactly FftSize samples", nameof(mono));
        if (bandsDb.Length < _bands) throw new ArgumentException("needs BandCount slots", nameof(bandsDb));
        float[] re = _re, im = _im, w = _window;
        int[] rev = _bitRev;
        int n = _n;
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            re[j] = mono[i] * w[i];
            im[j] = 0f;
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int k = 0, t = 0; k < half; k++, t += step)
                {
                    float c = _cos[t], s = _sin[t];
                    int a = start + k, b = a + half;
                    float tr = re[b] * c - im[b] * s;
                    float ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
        float norm = _norm;
        for (int b = 0; b < _bands; b++)
        {
            float p = 0f;
            for (int k = _bandLo[b], hi = _bandHi[b]; k < hi; k++) p += re[k] * re[k] + im[k] * im[k];
            float db = 10f * MathF.Log10(p * norm + 1e-12f) + _tilt[b];
            bandsDb[b] = Math.Clamp(db, FloorDb, CeilingDb);
        }
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }
}
```

**NEW `..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\Audio\SpectrumRing.cs`**

```csharp
using System;
using System.Numerics;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// The spectrum tap's single-producer / single-consumer MONO sample ring (spec §7.8). The RT render thread
/// <see cref="Write"/>s each rendered block (downmixed L+R/2) under a spectrum lease; the control tick
/// <see cref="TryCopyContentWindow"/>s the latency-aligned window by CONTENT frame (the mixer-domain
/// <c>BlockCtx.StartFrame</c> index space). Unlike <see cref="PcmRing"/> the reader never consumes: it addresses a window
/// ending at any content frame the writer has passed. The write index is MONOTONIC for the ring's life (never reset);
/// <see cref="Arm"/> publishes where the current content domain begins (ring index + content base + an arm epoch), so a
/// re-arm (demand edge, device rebuild, a block discontinuity) never makes an old window look valid — the reader rejects
/// anything before the arm or across an arm. Power-of-two capacity, <see cref="Volatile"/> publish/acquire plus one full
/// fence (the <c>AudioLevelMailbox</c> discipline), zero allocation on either side, no lock, no syscall.
/// </summary>
public sealed class SpectrumRing
{
    private readonly float[] _buf;
    private readonly int _mask, _maxBlock;
    private long _written;      // mono samples written, monotonic; the RT thread owns the write
    private long _armedAt;      // ring index of the first sample after the last Arm
    private long _armedBase;    // that sample's CONTENT frame
    private long _armEpoch;     // bumps per Arm (published last)

    /// <summary>Create a ring of at least <paramref name="minSamples"/> mono samples (rounded up to a power of two).
    /// <paramref name="maxBlock"/> is the largest block the producer writes in one call: the torn-read guard reserves it.</summary>
    public SpectrumRing(int minSamples, int maxBlock)
    {
        uint cap = BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, minSamples));
        _buf = new float[cap];
        _mask = (int)cap - 1;
        _maxBlock = Math.Max(1, maxBlock);
    }

    /// <summary>Capacity in mono samples.</summary>
    public int Capacity => _buf.Length;

    /// <summary>Mono samples written over the ring's life. Safe from either thread.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>The arm count (0 = never armed). Safe from either thread.</summary>
    public long ArmEpoch => Volatile.Read(ref _armEpoch);

    /// <summary>PRODUCER (RT): declare that the NEXT sample written is content frame <paramref name="contentBase"/>. Called on
    /// a demand edge, a render-epoch change and a block discontinuity (<c>TapSpectrumBlock</c>). The fields are written
    /// first and the epoch last; the reader checks the epoch around its whole read.</summary>
    public void Arm(long contentBase)
    {
        _armedAt = _written;
        _armedBase = contentBase;
        Volatile.Write(ref _armEpoch, _armEpoch + 1);
    }

    /// <summary>The newest content frame written (exclusive) in the CURRENT arm's domain — the reader's saturation point for
    /// a negative sync offset. Safe from either thread (a concurrent re-arm is caught by the reader's epoch check).</summary>
    public long NewestContent
    {
        get
        {
            long epoch = Volatile.Read(ref _armEpoch);
            long at = Volatile.Read(ref _armedAt), b = Volatile.Read(ref _armedBase), w = Volatile.Read(ref _written);
            return epoch == 0 ? 0L : b + (w - at);
        }
    }

    /// <summary>PRODUCER (RT): downmix <paramref name="interleaved"/> (<paramref name="channels"/> per frame) to mono and
    /// append. Wraps freely — the reader validates its own window. Alloc-free.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0) return;
        int frames = interleaved.Length / channels;
        if (frames <= 0) return;
        long w = _written;   // only this thread writes it — a plain read is fine
        float[] buf = _buf;
        int mask = _mask;
        if (channels == 2)
        {
            for (int f = 0, i = 0; f < frames; f++, i += 2)
                buf[(int)((w + f) & mask)] = 0.5f * (interleaved[i] + interleaved[i + 1]);
        }
        else
        {
            float inv = 1f / channels;
            for (int f = 0; f < frames; f++)
            {
                float acc = 0f;
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++) acc += interleaved[baseIdx + c];
                buf[(int)((w + f) & mask)] = acc * inv;
            }
        }
        Volatile.Write(ref _written, w + frames);   // publish
    }

    /// <summary>CONSUMER (control): copy the <c>dst.Length</c> samples ending at content frame <paramref name="contentEndExclusive"/>
    /// (in the current arm's domain). False when the window starts before the arm, is not yet written, is already (or
    /// about to be — one <c>maxBlock</c> of guard) overwritten, was overwritten DURING the copy, or straddles a re-arm —
    /// the caller simply skips this tick. Alloc-free.</summary>
    public bool TryCopyContentWindow(long contentEndExclusive, Span<float> dst)
    {
        int n = dst.Length;
        if (n <= 0 || n > _buf.Length - _maxBlock) return false;
        long epoch = Volatile.Read(ref _armEpoch);
        if (epoch == 0) return false;
        long armedAt = Volatile.Read(ref _armedAt), armedBase = Volatile.Read(ref _armedBase);
        long written = Volatile.Read(ref _written);
        long end = armedAt + (contentEndExclusive - armedBase);     // content → ring index
        long start = end - n;
        if (start < armedAt || end > written) return false;          // before this arm / not yet written
        if (start < written - _buf.Length + _maxBlock) return false; // overwritten, or inside the producer's next block
        float[] buf = _buf;
        int s = (int)(start & _mask);
        int first = Math.Min(n, buf.Length - s);
        buf.AsSpan(s, first).CopyTo(dst);
        if (first < n) buf.AsSpan(0, n - first).CopyTo(dst[first..]);
        Thread.MemoryBarrier();   // payload reads complete before the validation reads (ARM64) — AudioLevelMailbox.cs:24
        long after = Volatile.Read(ref _written);
        return start >= after - _buf.Length + _maxBlock && Volatile.Read(ref _armEpoch) == epoch;
    }
}
```

### 4.2 Engine — the two-tier lease, double-buffered magnitudes, `OutputDelayFrames` (WP-E2)

**`..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\MediaEffects.cs`**

Insert after `VisualizerFrame` (after `:21`):
```csharp
/// <summary>What accompanied the last published spectrum: a monotonic <paramref name="Sequence"/> (0 = nothing yet),
/// the band count, whether the session was muted/silenced (the UI shows its idle breath), the PRE-gain RMS of the
/// analysed window (what Tape's meter and Field's breath use — the level tap's RMS is post-volume), how far behind the
/// newest rendered sample the window ended (frames — a diagnostic of the alignment actually applied), the analysis
/// cost, and whether a spectrum lease is live at all.</summary>
public readonly record struct SpectrumInfo(long Sequence, int BandCount, bool Muted, float WindowRms, long AlignFrames, float FftMs, bool Live);
```

Replace the `IAudioEffects` tail `:90-93` (`Visualizer` + `AcquireVisualizer`) with:
```csharp
    /// <summary>The published LEVEL frame (RMS/peak every control tick). <see cref="VisualizerFrame.Magnitudes"/> stays
    /// EMPTY (reserved): the spectrum is read ONLY through <see cref="CopySpectrum"/>, so a bound frame never tears.</summary>
    IReadSignal<VisualizerFrame> Visualizer { get; }
    /// <summary>Keep LEVEL analysis (RMS/peak) active while a visible consumer needs it. Dispose when hidden or inactive.</summary>
    IDisposable AcquireVisualizer();
    /// <summary>Keep SPECTRUM analysis (the FFT tier) active while a visible consumer needs it; implies the level tap.
    /// Dispose when hidden, paused-for-good, occluded or under reduced motion.</summary>
    IDisposable AcquireSpectrum();
    /// <summary>Copy the latest band magnitudes (dB, <see cref="SpectrumAnalyzer.FloorDb"/>..<see cref="SpectrumAnalyzer.CeilingDb"/>)
    /// into <paramref name="destination"/> without tearing (one lock, one memcpy of the FRONT buffer). Returns the count
    /// copied — 0 before the first publish or without a lease. THE ONLY spectrum read path; the UI PULLS it on its own
    /// cadence, nothing pushes.</summary>
    int CopySpectrum(Span<float> destination, out SpectrumInfo info);
    /// <summary>The user's playback-sync offset in milliseconds (positive reads the window EARLIER, for a device that
    /// adds latency the clock cannot see, e.g. Bluetooth). Clamped to ±500. Cross-thread safe.</summary>
    float SpectrumOffsetMs { get; set; }
```

In `AudioEffects`, insert after `:102` (`private long _nextVisualizerEpoch, _visualizerEpoch, _visualizerSource;`):
```csharp
    // ── the SPECTRUM tier: a second demand count over the SAME source token; magnitudes are double-buffered ───────────
    private int _spectrumConsumers;
    private long _spectrumEpoch;                        // 0 = no spectrum demand; rotates with the source like _visualizerEpoch
    private readonly float[][] _magnitudes = [new float[SpectrumAnalyzer.DefaultBandCount], new float[SpectrumAnalyzer.DefaultBandCount]];
    private int _magnitudeFront;                        // the readable buffer (under _visualizerGate)
    private int _magnitudeCount;                        // 0 = nothing published since the lease began
    private long _spectrumSequence;
    private bool _spectrumMuted;
    private float _spectrumWindowRms, _spectrumFftMs;
    private long _spectrumAlignFrames;
    private int _spectrumOffsetMsBits;                  // float bits, Volatile: written by the UI, read on the clock thread
```

Replace `BindVisualizerSource` `:131-140` with:
```csharp
    internal long BindVisualizerSource()
    {
        lock (_visualizerGate)
        {
            long source = ++_visualizerSource;
            _magnitudeCount = 0;
            _visualizer.Value = VisualizerFrame.Silence;
            Volatile.Write(ref _visualizerEpoch, _visualizerConsumers == 0 ? 0 : ++_nextVisualizerEpoch);
            Volatile.Write(ref _spectrumEpoch, _spectrumConsumers == 0 ? 0 : ++_nextVisualizerEpoch);
            return source;
        }
    }
```

`PublishVisualizerFrame` (`:145-153`) is UNCHANGED: the level frame keeps `ReadOnlyMemory<float>.Empty` (O2 — `Magnitudes`
is reserved; `VisualizerFrame`'s record equality compares the memory by reference and the existing tests assert
`VisualizerFrame.Silence` equality, `AudioVisualizerDemandTests.cs:30/49/68/177/220/223/246`). Insert after it (after `:153`):
```csharp
    /// <inheritdoc/>
    public float SpectrumOffsetMs
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _spectrumOffsetMsBits));
        set => Volatile.Write(ref _spectrumOffsetMsBits, BitConverter.SingleToInt32Bits(Math.Clamp(value, -500f, 500f)));
    }

    /// <inheritdoc/>
    public IDisposable AcquireSpectrum()
    {
        IDisposable level = AcquireVisualizer();          // the FFT tier implies the level tap (Tape's meter, Field's breath)
        lock (_visualizerGate)
        {
            if (_spectrumConsumers++ == 0)
            {
                _magnitudeCount = 0;
                Volatile.Write(ref _spectrumEpoch, ++_nextVisualizerEpoch);
            }
        }
        return new SpectrumLease(this, level);
    }

    private void ReleaseSpectrum()
    {
        lock (_visualizerGate)
        {
            if (--_spectrumConsumers != 0) return;
            Volatile.Write(ref _spectrumEpoch, 0);
            _magnitudeCount = 0;                            // CopySpectrum returns 0 from here on; the level frame is untouched
        }
    }

    /// <summary>The RT thread's gate for the spectrum ring: the current spectrum epoch iff <paramref name="source"/> is the
    /// bound session, else 0. Two volatile reads, no lock.</summary>
    internal long SpectrumDemand(long source)
        => source == Volatile.Read(ref _visualizerSource) ? Volatile.Read(ref _spectrumEpoch) : 0;

    /// <summary>Control-thread publish of one analysed window (never RT). Writes the BACK buffer, swaps, bumps the
    /// sequence; false when the lease or the source has moved on. Never touches <see cref="_visualizer"/> (O2).</summary>
    internal bool PublishSpectrum(long source, long epoch, ReadOnlySpan<float> bandsDb, bool muted, float windowRms, long alignFrames, float fftMs)
    {
        lock (_visualizerGate)
        {
            if (epoch == 0 || epoch != _spectrumEpoch || source != _visualizerSource) return false;
            int back = _magnitudeFront ^ 1;
            int n = Math.Min(bandsDb.Length, _magnitudes[back].Length);
            bandsDb[..n].CopyTo(_magnitudes[back]);
            _magnitudeFront = back;
            _magnitudeCount = n;
            _spectrumSequence++;
            _spectrumMuted = muted;
            _spectrumWindowRms = windowRms;
            _spectrumAlignFrames = alignFrames;
            _spectrumFftMs = fftMs;
            return true;
        }
    }

    /// <inheritdoc/>
    public int CopySpectrum(Span<float> destination, out SpectrumInfo info)
    {
        lock (_visualizerGate)
        {
            int n = Math.Min(_magnitudeCount, destination.Length);
            if (n > 0) _magnitudes[_magnitudeFront].AsSpan(0, n).CopyTo(destination);
            info = new SpectrumInfo(_spectrumSequence, n, _spectrumMuted, _spectrumWindowRms, _spectrumAlignFrames, _spectrumFftMs, _spectrumEpoch != 0);
            return n;
        }
    }

    private sealed class SpectrumLease(AudioEffects owner, IDisposable level) : IDisposable
    {
        private AudioEffects? _owner = owner;
        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseSpectrum();
            level.Dispose();   // idempotent (VisualizerLease)
        }
    }
```

In `NullAudioEffects`, insert after `:194` (`public IDisposable AcquireVisualizer() => NoVisualizer;`):
```csharp
    /// <inheritdoc/>
    public IDisposable AcquireSpectrum() => NoVisualizer;
    /// <inheritdoc/>
    public int CopySpectrum(Span<float> destination, out SpectrumInfo info) { info = default; return 0; }
    /// <inheritdoc/>
    public float SpectrumOffsetMs { get; set; }
```

**`..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\Audio\PcmAudioPlayer.cs`**

Insert after `:382` (`public long MeterSamples => …`):
```csharp
    // ── spectrum tap (spec §7.8): a PRE-master-gain mono ring the RT fills only under a SPECTRUM lease; the control tick
    //    analyses the latency-aligned window and publishes bands through AudioEffects (never from the RT path) ──────────
    private SpectrumRing? _spectrumRing;                 // created by the control thread on the first lease; the RT reads it with Volatile
    private long _spectrumEpochSeen, _spectrumRenderEpochSeen, _spectrumNextStart;   // RT-only: the re-arm triggers (demand edge, device rebuild, a block discontinuity)
    private bool _spectrumArmed;                         // RT-only
    private SpectrumAnalyzer? _spectrumAnalyzer;         // control-thread only
    private float[]? _spectrumWindow, _spectrumBands;    // control-thread only
    private long _spectrumPublishes;
    private const float SpectrumRingSeconds = 1.5f;      // rounded up to a power of two by the ring (131 072 at 48 kHz = 2.7 s): device latency + a ±500 ms offset + the RT burst
    /// <summary>Spectrum windows analysed and published (diagnostics).</summary>
    public long SpectrumPublishes => Interlocked.Read(ref _spectrumPublishes);
    /// <summary>DIAGNOSTIC ONLY (cross-plan note, §2.7): frames queued between the mixer's newest submitted sample and the
    /// one the listener hears — submitted − played + the endpoint's measured latency. The spectrum window is NOT aligned
    /// by this; it is aligned in the content domain from <see cref="PlayedFrames"/> (<c>PublishSpectrum</c>).</summary>
    public long OutputDelayFrames => Math.Max(0L, SubmittedFrames - PlayedFrames + _clock.StreamLatencyFrames);
```

In `RenderBlock`, insert IMMEDIATELY BEFORE the line `_masterGain.Process(buf, buf, frames, ctx);` (anchor by symbol — it is unique in the
file; today `:1529`, after `_mixer.PublishDrained(_mixer.ConsumeSeq);` `:1528`; the playback plan's D7 later moves the limiter and EQ
ahead of `_masterGain`, and the tap stays glued to `_masterGain`, so it ends up post-EQ/limiter and pre-volume BY DESIGN — X2):
```csharp
        TapSpectrumBlock(buf, frames, ctx.StartFrame);   // PRE-gain: the picture must not follow the volume slider (mute rides a flag instead)
```

Replace `PublishVisualizer` `:1107-1114` with:
```csharp
    private void PublishVisualizer()
    {
        if (_liveEffects is not AudioEffects ae) return;
        if (_tap.TryRead(out float rms, out float peak, out long epoch, out long version) && version != _tapReadVersion)
        {
            _tapReadVersion = version;
            ae.PublishVisualizerFrame(Volatile.Read(ref _visualizerSource), epoch, rms, peak);
        }
        PublishSpectrum(ae);
    }

    /// <summary>Control thread (~60 Hz, Playing only — the same cadence as the level publish): analyse the window that is
    /// AUDIBLE now and publish the bands. Allocation is legal HERE (first-lease arming, a sample-rate change); the steady
    /// state allocates nothing. Alignment is done in the CONTENT domain (O1, §2.7): the ring is indexed by
    /// <c>BlockCtx.StartFrame</c> (the mixer's <c>ConsumeSeq</c> at the block start) and the audible frame is
    /// <see cref="PlayedFrames"/> brought into that domain, minus the endpoint's measured latency and the master chain's
    /// own latency, minus the user offset, plus half a window so the window is CENTRED on the audible instant (V-E24).
    /// A negative user offset saturates at the newest rendered sample (V-E5).</summary>
    private void PublishSpectrum(AudioEffects ae)
    {
        long source = Volatile.Read(ref _visualizerSource);
        long epoch = ae.SpectrumDemand(source);
        if (epoch == 0) return;
        int rate = _format.SampleRate;
        if (_spectrumAnalyzer is null || _spectrumAnalyzer.SampleRate != rate)
        {
            _spectrumAnalyzer = new SpectrumAnalyzer(rate);
            _spectrumWindow = new float[_spectrumAnalyzer.FftSize];
            _spectrumBands = new float[_spectrumAnalyzer.BandCount];
        }
        var ring = _spectrumRing;
        if (ring is null)
        {
            ring = new SpectrumRing((int)(rate * SpectrumRingSeconds), _maxBlock);
            Volatile.Write(ref _spectrumRing, ring);      // the RT starts filling (and arms) on its next block
            return;
        }
        // PlayedFrames counts SUBMITTED frames, which restart at 0 on RebuildSink (:1727-1728) while the mixer's ConsumeSeq
        // does not (CmdReset :962-967 resets both together); the difference, less the rendered-but-unsubmitted remainder,
        // is the submitted→content domain offset (0 in the common case).
        long domainOffset = ConsumeSeqFrames - SubmittedFrames - Volatile.Read(ref _pendingFrames);
        long audible = PlayedFrames + domainOffset - _clock.StreamLatencyFrames - _graph.Live.TotalLatencySamples;
        long offsetFrames = (long)Math.Round(ae.SpectrumOffsetMs * rate / 1000.0);
        long end = Math.Min(audible - offsetFrames + _spectrumAnalyzer.FftSize / 2, ring.NewestContent);
        if (!ring.TryCopyContentWindow(end, _spectrumWindow!)) return;   // not yet filled after an arm, lapped, torn or re-armed: skip this tick
        long t0 = Stopwatch.GetTimestamp();
        _spectrumAnalyzer.Analyze(_spectrumWindow!, _spectrumBands!);
        float fftMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        double sumSq = 0;
        foreach (float v in _spectrumWindow!) sumSq += (double)v * v;
        float windowRms = (float)Math.Sqrt(sumSq / _spectrumWindow.Length);   // PRE-gain (V-E19): the tap sits before _masterGain
        bool muted = _muted || _volume <= 0.0005f;
        if (ae.PublishSpectrum(source, epoch, _spectrumBands!, muted, windowRms, ring.NewestContent - end, fftMs))
            Interlocked.Increment(ref _spectrumPublishes);
    }
```

Insert after `TapBlock` (after `:1612`):
```csharp
    /// <summary>RT: feed the spectrum ring with the PRE-gain mix. Demand-gated exactly like <see cref="TapBlock"/> (two
    /// volatile reads, no lock), alloc-free, no blocking. <paramref name="startFrame"/> is <c>BlockCtx.StartFrame</c> — the
    /// content frame of <c>buf[0]</c>. The ring is (re-)ARMED with that base on a demand edge, on a device rebuild
    /// (<see cref="RenderEpoch"/> — CmdReset :967, RecoverStarvation :1312, RebuildSink :1727) and on any block
    /// discontinuity (a frame the RT did not render through this tap), so the reader's content→ring mapping is exact.</summary>
    private void TapSpectrumBlock(ReadOnlySpan<float> buf, int frames, long startFrame)
    {
        if (_liveEffects is not AudioEffects ae) return;
        long epoch = ae.SpectrumDemand(Volatile.Read(ref _visualizerSource));
        if (epoch == 0) { _spectrumArmed = false; return; }
        var ring = Volatile.Read(ref _spectrumRing);
        if (ring is null) return;                              // the control thread has not created it yet
        long renderEpoch = Volatile.Read(ref _renderEpoch);
        if (!_spectrumArmed || epoch != _spectrumEpochSeen || renderEpoch != _spectrumRenderEpochSeen || startFrame != _spectrumNextStart)
        {
            _spectrumArmed = true;
            _spectrumEpochSeen = epoch;
            _spectrumRenderEpochSeen = renderEpoch;
            ring.Arm(startFrame);
        }
        ring.Write(buf[..(frames * _format.Channels)], _format.Channels);
        _spectrumNextStart = startFrame + frames;
    }
```
(`_renderEpoch`, `_submittedFrames`, `_playedFrames` are the existing `long`s at `:316`; `_pendingFrames` the `int` at `:335`; `_maxBlock` the
ctor's block cap (`:1497`); `ConsumeSeqFrames => _mixer.ConsumeSeq` `:690`; `_graph.Live.TotalLatencySamples` is the compiled master
chain's summed latency (`AudioGraphHost.cs:20-33`, the same value `_position.ExtraLatencySamples` takes at `:1538`); `_muted`/`_volume`
at `:414-415`; `Stopwatch` via the file's `using System.Diagnostics;` `:3`. `BlockCtx` is a `ref struct` (`AudioGraph.cs:17-36`) —
only its `long StartFrame` is passed, never the struct.)

**`src/apps/Wavee/Playback/Playback.Audio.cs`** — insert after `:229` (`AcquireLevels`):
```csharp
        /// <summary>Take a SPECTRUM lease (the engine's FFT tier; it implies the level tap). Dispose to release — the RT
        /// stops filling the ring and the clock thread stops analysing the moment the last lease goes.</summary>
        public static IDisposable AcquireSpectrum() => s_effects.AcquireSpectrum();

        /// <summary>Copy the latest band magnitudes (dB) tear-free. 0 ⇒ nothing published yet (no lease, not playing,
        /// or the window is not yet latency-aligned).</summary>
        public static int CopySpectrum(Span<float> into, out SpectrumInfo info) => s_effects.CopySpectrum(into, out info);

        /// <summary>Mirror of <c>Prefs.Stage.SyncOffsetMs</c> onto the analysis window (positive = read earlier).</summary>
        public static void SetSpectrumOffsetMs(float ms) => s_effects.SpectrumOffsetMs = ms;

        /// <summary>Diagnostics: the live session's endpoint queue depth (frames, diagnostic only — §2.7) and spectrum publish
        /// count; (0, 0) without one.</summary>
        public static (long DelayFrames, long Publishes) SpectrumDiagnostics()
        {
            lock (s_gate) { return s_session is { } s ? (s.OutputDelayFrames, s.SpectrumPublishes) : (0L, 0L); }
        }
```
and in `SeedFromSettings()` (`:502-515`, WP-D3 — V-D10: the persisted offset must reach the engine after a restart), after the
`s_volume = …` line:
```csharp
            // The stage's analysis-window offset (Prefs.Stage.SyncOffsetMs; written live by the gallery/Appearance sliders).
            s_effects.SpectrumOffsetMs = Math.Clamp(Platform.Settings.Get(Platform.Keys.StageSyncOffsetMs), -500, 500);
```

### 4.3 Engine — `SeriesEl` (WP-E3)

#### 4.3.1 Foundation spec — `..\fluent-gpu\src\FluentGpu.Engine\Foundation\Effects.cs`, insert after `PathSpec`'s closing brace (`:63` — the record is declared `:49-63`; `:52` is only its last parameter line) and before `ClipPathSpec`'s doc comment (`:65`)

```csharp
/// <summary>How a <c>SeriesEl</c> turns its samples into geometry (gpu-renderer.md §3.1 <c>DrawSeriesCmd</c>).</summary>
public enum SeriesShape : byte
{
    /// <summary>An area from the baseline up to each sample (bars-as-one-ribbon; Aurora).</summary>
    Baseline = 0,
    /// <summary>An area ± each sample about the baseline (a waveform; Horizon).</summary>
    Mirrored = 1,
    /// <summary>A constant-width ribbon through the sample polyline (outlines, Charts/Waveform later).</summary>
    Stroke = 2,
}

/// <summary>The STATIC half of a <c>SeriesEl</c>: shape, colour or ≤ 4-stop gradient by amplitude, stroke thickness,
/// the baseline (a fraction of the box height; NaN = the shape's default — 1 for Baseline/Stroke, 0.5 for Mirrored)
/// and the amplitude (the box-height fraction a sample of 1.0 reaches). The SAMPLES live in their own pooled side
/// table (<c>SceneStore.Series.cs</c>), never in this struct. POD; the gradient's stops array is copied by the snapshot.</summary>
public readonly record struct SeriesSpec(SeriesShape Shape, ColorF Color, GradientSpec? Gradient, float Thickness,
                                         float Baseline, float Amplitude, float Opacity)
{
    /// <summary>The most samples one node carries; extras are dropped at write time.</summary>
    public const int MaxSamples = 512;
    /// <summary>Samples per recorded chunk (31 new + 1 shared edge sample).</summary>
    public const int ChunkSamples = 32;
}
```

#### 4.3.2 The element — NEW `..\fluent-gpu\src\FluentGpu.Engine\Dsl\SeriesEl.cs`

```csharp
using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>A fixed-identity array+count+version view over ≤ <see cref="SeriesSpec.MaxSamples"/> floats — the
/// <c>RowCells</c>/<c>TextSpans</c> shape for a numeric series. <see cref="SeriesEl.Samples"/> binds this AS ONE channel;
/// the reconciler copies it into a scene-owned array on every fire (Reconciler.Series.cs), so refilling a reused
/// buffer for the next tick never mutates what the scene already committed. Equal by array IDENTITY, count and
/// version: a producer bumps <see cref="Version"/> after rewriting the buffer and the bind effect re-fires.</summary>
public readonly struct SeriesSamples(float[] array, int count, uint version) : IEquatable<SeriesSamples>
{
    public readonly float[] Array = array;
    public readonly int Count = count;
    public readonly uint Version = version;
    public static readonly SeriesSamples Empty = new(System.Array.Empty<float>(), 0, 0u);
    public ReadOnlySpan<float> AsSpan() => Array is null ? default : Array.AsSpan(0, Math.Min(Count, Array.Length));
    public bool Equals(SeriesSamples other) => ReferenceEquals(Array, other.Array) && Count == other.Count && Version == other.Version;
    public override bool Equals(object? obj) => obj is SeriesSamples o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Array is null ? 0 : RuntimeHelpers.GetHashCode(Array), Count, Version);
    public static bool operator ==(SeriesSamples a, SeriesSamples b) => a.Equals(b);
    public static bool operator !=(SeriesSamples a, SeriesSamples b) => !a.Equals(b);
}

/// <summary>Dynamic geometry from a BOUND sample source: a baseline area, a mirrored area or a stroke ribbon through
/// N ≤ 512 samples (0..1), with a solid colour or a ≤ 4-stop gradient by amplitude. No <c>PathData</c>, no
/// tessellation cache: the recorder emits fixed-size <c>DrawSeriesCmd</c> chunks straight from the scene-owned
/// sample copy (gpu-renderer.md §3.1), so a per-frame rewrite costs one bind fire and one re-record of this node.
/// A leaf: no children, no pointer handlers, no declarative motion of its own (wrap it in a <see cref="BoxEl"/> for
/// Enter/Exit/Layout, exactly like <see cref="PathEl"/>).</summary>
public sealed record SeriesEl : Element
{
    public override ushort ElementTypeId => 18;

    /// <summary>The samples (0..1 heights). Static, a <c>Prop.Of(() => …)</c> thunk that reads a version signal and
    /// returns a view over a reused buffer, or a signal — like every other <see cref="Prop{T}"/> channel.</summary>
    public Prop<SeriesSamples> Samples { get; init; } = SeriesSamples.Empty;
    public SeriesShape Shape { get; init; } = SeriesShape.Baseline;
    /// <summary>The fill when <see cref="Gradient"/> is null.</summary>
    public ColorF Color { get; init; } = ColorF.FromRgba(255, 255, 255);
    /// <summary>≤ 4 stops BY AMPLITUDE (offset 0 = the baseline, 1 = a sample of 1.0); <c>Shape</c>/<c>AngleDeg</c> are ignored.</summary>
    public GradientSpec? Gradient { get; init; }
    /// <summary>Stroke width in DIP (<see cref="SeriesShape.Stroke"/> only).</summary>
    public float Thickness { get; init; } = 2f;
    /// <summary>Baseline as a fraction of the box height; NaN = the shape's default.</summary>
    public float Baseline { get; init; } = float.NaN;
    /// <summary>The box-height fraction a sample of 1.0 reaches.</summary>
    public float Amplitude { get; init; } = 1f;
    public float Opacity { get; init; } = 1f;

    // layout (the PolylineStrokeEl block, Element.cs:572-587)
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
}
```
(`WGPU0003` validates id 18 is unique; `SeriesElDiff` is generated automatically — `SourceGen/Engine/DiffPropsGenerator.cs`.)

#### 4.3.3 Scene — `Scene\Columns.cs`: the `VisualKind` enum (anchor by symbol — the engine tree is dirty in this file; today `:6`, `None..ListRow = 9`) gains `Series = 10` (append `, Series = 10` inside the enum).

`Scene\SceneStore.cs` — insert after `:158` (`_paths`):
```csharp
    private readonly ColdSlab<SeriesSpec> _series = new();   // VisualKind.Series' static half; the samples ride SceneStore.Series.cs
```
In `FreeSubtreeCore`, inside the `SparsePaint` block after `_paths.Remove(idx);` (`:501`): `_series.Remove(idx);` and after the ListRow line (`:527-528`):
```csharp
        if (_paint[idx].VisualKind == VisualKind.Series && _seriesSamples.Count != 0) _seriesSamples.Remove(idx);
```
Insert after `ClearPath` (`:1830`):
```csharp
    public void SetSeries(NodeHandle h, in SeriesSpec spec)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _series.GetOrAdd(idx) = spec;
        MarkRecordDirty(idx);
    }
    public bool TryGetSeries(NodeHandle h, out SeriesSpec spec) => _series.TryGet((int)h.Raw.Index, out spec);
    public void ClearSeries(NodeHandle h) { int idx = (int)h.Raw.Index; _series.Remove(idx); MarkRecordDirty(idx); }
```

NEW `Scene\SceneStore.Series.cs`:
```csharp
using System;
using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

public sealed partial class SceneStore
{
    /// <summary>SeriesEl's per-node sample payload: sparse (O(series nodes)), grow-only pooled <c>float[]</c> capacity
    /// (never shrinks), keyed by node INDEX — the exact <c>_rowCells</c> discipline (SceneStore.RowCells.cs). A write
    /// is a CAPTURED side-table write: <see cref="NoteCaptureChanged"/> first (P8), then <see cref="MarkRecordDirty"/>
    /// so the recorder re-emits this node's span.</summary>
    private readonly Dictionary<int, (float[]? Arr, int Count)> _seriesSamples = new();

    public void SetSeriesSamples(NodeHandle node, ReadOnlySpan<float> samples)
    {
        int idx = (int)node.Raw.Index;
        NoteCaptureChanged(idx);
        int n = Math.Min(samples.Length, SeriesSpec.MaxSamples);
        if (n == 0)
        {
            if (_seriesSamples.Remove(idx)) MarkRecordDirty(idx);
            return;
        }
        _seriesSamples.TryGetValue(idx, out var slot);
        if (slot.Arr is null || slot.Arr.Length < n) slot.Arr = new float[Math.Max(64, n)];
        samples[..n].CopyTo(slot.Arr);
        slot.Count = n;
        _seriesSamples[idx] = slot;
        MarkRecordDirty(idx);
    }

    public bool TryGetSeriesSamples(NodeHandle h, out ReadOnlySpan<float> samples)
    {
        if (_seriesSamples.TryGetValue((int)h.Raw.Index, out var slot) && slot.Arr is not null)
        {
            samples = slot.Arr.AsSpan(0, slot.Count);
            return true;
        }
        samples = default;
        return false;
    }
}
```

`Scene\SceneRecordingSnapshot.cs` — insert after `:53` (`_path`): `private readonly SnapshotColumn<SeriesSpec> _series = new(); private readonly SnapshotColumn<SeriesSamplesCapture> _seriesSamples = new();`. After `_rowCells.BeginCapture();` (`:273`): `_seriesSamples.BeginCapture();`; after `_rowCells.EndCapture();` (`:337`): `_seriesSamples.EndCapture();`. In `ClearSparseRows` after `_path.Remove(index);` (`:397`): `_series.Remove(index); _seriesSamples.Remove(index);`. In `CaptureNode`'s `SparsePaint` block after the `_path.Set` line (`:521`): `if (source.TryGetSeries(node, out SeriesSpec series)) CopySeries(ref _series.Set(index), in series);`; after the ListRow capture block (after `:552`):
```csharp
        if (_paint[index].VisualKind == VisualKind.Series && source.TryGetSeriesSamples(node, out var seriesSamples))
        {
            ref var sc = ref _seriesSamples.Set(index);
            if (sc.Samples is null || sc.Samples.Length < seriesSamples.Length) sc.Samples = new float[Math.Max(64, seriesSamples.Length)];
            seriesSamples.CopyTo(sc.Samples);
            sc.Count = seriesSamples.Length;
        }
```
After `TryGetPath` (`:823`):
```csharp
    public bool TryGetSeries(NodeHandle node, out SeriesSpec value) => _series.TryGet((int)node.Raw.Index, out value);
    /// <summary>The recorder's read of a captured series (the render-thread twin of <c>SceneStore.TryGetSeriesSamples</c>).</summary>
    public bool TryGetSeriesSamples(NodeHandle node, out ReadOnlySpan<float> samples)
    {
        if (_seriesSamples.TryGet((int)node.Raw.Index, out var c) && c.Samples is not null) { samples = c.Samples.AsSpan(0, c.Count); return true; }
        samples = default;
        return false;
    }
```
In `BeginSparseCapture` after `_path.BeginCapture();` (`:933`): `_series.BeginCapture();`; in `EndSparseCapture` after `_path.EndCapture();` (`:959`): `_series.EndCapture();`. After the `RowCellsCapture` struct (`:982`): `private struct SeriesSamplesCapture { public float[]? Samples; public int Count; }`. Next to `CopyGradient` (`:915-922`):
```csharp
    private static void CopySeries(ref SeriesSpec target, in SeriesSpec source)
    {
        if (source.Gradient is { } g)
        {
            GradientSpec copy = target.Gradient ?? default;
            CopyGradient(ref copy, in g);           // pools the stops array exactly as the BoxEl gradient columns do
            target = source with { Gradient = copy };
        }
        else target = source;
    }
```
`Scene\SceneRecordingSnapshot.Parity.cs` — after the `_path` line (`:177`):
```csharp
        if (!SparseEqual(_series, other._series, i, SeriesEqual, out which)) return Fail(out mismatch, $"n#{i} SeriesSpec ({which})");
```
after the row-cells block (after `:215`):
```csharp
        bool hasSeries = TryGetSeriesSamples(node, out var mineSeries);
        bool otherSeries = other.TryGetSeriesSamples(node, out var theirSeries);
        if (hasSeries != otherSeries) return Fail(out mismatch, $"n#{i} series samples presence");
        if (hasSeries && !mineSeries.SequenceEqual(theirSeries)) return Fail(out mismatch, $"n#{i} series samples");
```
and the comparer beside `GradientEqual`:
```csharp
    private static bool SeriesEqual(in SeriesSpec a, in SeriesSpec b)
    {
        if (a.Shape != b.Shape || !a.Color.Equals(b.Color) || a.Thickness != b.Thickness || !a.Baseline.Equals(b.Baseline)
            || a.Amplitude != b.Amplitude || a.Opacity != b.Opacity || (a.Gradient is null) != (b.Gradient is null)) return false;
        if (a.Gradient is not { } ga || b.Gradient is not { } gb) return true;
        return GradientEqual(in ga, in gb);
    }
```

#### 4.3.4 Reconciler — `Reconciler\Reconciler.cs`

`BindFlip` (`:1014-1026`), before `_ => null`: `SeriesEl x => b is SeriesEl y ? SeriesElDiff.FirstBoundFlip(x, y) : null,`. `RecordChanged` (`:1031-1043`), before `_ => true`: `SeriesEl x => b is not SeriesEl y || SeriesElDiff.AnyChanged(x, y),`. `IsRecyclable` (`:3822-3856`), before `case PathEl pe:`: `case SeriesEl se: return !se.Samples.IsBound;`. `BindNode` tail: insert before `else if (el is ListRowEl lr)` (`:2782`):
```csharp
        else if (el is SeriesEl se)
        {
            BindSeriesSamples(node, se);   // Reconciler.Series.cs — the bound sample-source channel
        }
```
`WriteColumns` (`:4738` switch): insert before `case ListRowEl lr:` (`:5386`):
```csharp
            case SeriesEl se:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Series;
                paint.Opacity = se.Opacity;
                _scene.SetSeries(node, new SeriesSpec(se.Shape, se.Color, se.Gradient, se.Thickness, se.Baseline, se.Amplitude, se.Opacity));
                if (!se.Samples.IsBound) _scene.SetSeriesSamples(node, se.Samples.Value.AsSpan());
                // else: the bound path defers to BindSeriesSamples' mount-time effect (the ListRowEl.Cells deferral).
                ref LayoutInput li = ref _scene.Layout(node);
                li.Margin = se.Margin;
                li.Width = se.Width; li.Height = se.Height;
                li.MinW = se.MinWidth; li.MinH = se.MinHeight; li.MaxW = se.MaxWidth; li.MaxH = se.MaxHeight;
                li.FlexGrow = se.Grow; li.FlexShrink = se.Shrink; li.FlexBasis = se.Basis;
                li.AlignSelf = se.AlignSelf; li.JustifySelf = se.JustifySelf;
                break;
            }
```
NEW `Reconciler\Reconciler.Series.cs`:
```csharp
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// SeriesEl.Samples : Prop<SeriesSamples> — the bound sample-source channel. The ListRowEl.Cells shape
// (Reconciler.ListRow.cs:86-103): ONE BindEffect wired at mount, re-wired in place on a bound→bound re-render
// (Reconciler.Rewire.cs); every fire copies the CURRENT view into the scene-owned pooled array (SceneStore.Series.cs)
// and marks the record dirty. The thunk's version read is what subscribes it; nothing allocates after the first fire.
public sealed partial class TreeReconciler
{
    private void BindSeriesSamples(NodeHandle node, SeriesEl se)
    {
        if (!se.Samples.IsBound) return;
        var fx = new BindEffect<SeriesSamples>(Runtime, se, static e => e is SeriesEl x ? x.Samples : default);
        AddBinding(node, fx.Start(() =>
        {
            NodeBindingFireCount++;
            if (!_scene.IsLive(node)) return;
            SeriesSamples current = fx.Read();
            _scene.SetSeriesSamples(node, current.AsSpan());   // NoteCaptureChanged + MarkRecordDirty inside
            NodeBindingWriteCount++;
            _scene.Mark(node, NodeFlags.PaintDirty);
        }));
    }
}
```
`Hooks\SkeletonDeriver.cs` after the `PathEl` case (`:115-116`): `case SeriesEl se: return Bar(s, se.Width, se.Height, se.Grow, default, default, FlexAlign.Auto);`. `Scene\NodeDescriber.cs:72`: `18 => "Series",`.

#### 4.3.5 DrawList — `Render\DrawList.cs`

Enum (`:7-60`, the member list ends with `CompositeSlice = 23`): append `DrawSeries = 24,` after `CompositeSlice = 23,`. `DrawListOpcodeStats` (`:104-209`): field `public int DrawSeries;` after `PushStencilClip, PopStencilClip;`; `Add(DrawOp)`: `case DrawOp.DrawSeries: DrawSeries++; break;`; `Add(in stats)`: `DrawSeries += other.DrawSeries;`; `Minus`: `DrawSeries = DrawSeries - other.DrawSeries,`; `ToString`: append ` series={DrawSeries}`. Insert after `StrokePathCmd` (`:410`):
```csharp
/// <summary>32 inline samples (C# inline array) — the chunk payload of <see cref="DrawSeriesCmd"/>.</summary>
[System.Runtime.CompilerServices.InlineArray(32)]
public struct Samples32 { private float _e0; }

/// <summary>One CHUNK of a sample series (DrawOp.DrawSeries, gpu-renderer.md §3.1). A series of N samples is recorded as
/// ⌈(N−1)/31⌉ chunks of ≤ 32 samples sharing one edge sample, so nothing variable-length rides the stream (every
/// walker frames it through RepaintStreamSafety.TryBodySize) and clean-span reuse stays valid — the data IS the span.
/// Sample x = X0 + i·Dx (node-local); heights are fractions of Rect.H (Mirrored: of Rect.H/2) about Baseline (a fraction
/// of Rect.H) scaled by Amplitude, clamped to the Rect by the vertex shader; Shape 0 = baseline area, 1 = mirrored area,
/// 2 = a Thickness-wide stroke ribbon. ≤ 4 gradient stops BY
/// AMPLITUDE (C0..C3 at O0..O3; StopCount 1 = solid). A plain struct, not a record: the inline array must not enter a
/// generated Equals. Self-describing POD: a slice translation patches Transform only (Render/DrawOpTranslate).</summary>
public struct DrawSeriesCmd
{
    public RectF Rect;            // THIS chunk's box (cull / slice bounds), node-local — the full node box's Y/H, the chunk's X span
    public Affine2D Transform;
    public float Opacity;
    public int Shape, Count, Total, Index;
    public float X0, Dx, Baseline, Amplitude, Thickness;
    public ColorF C0, C1, C2, C3;
    public float O0, O1, O2, O3;
    public int StopCount;
    public Samples32 S;
}
```
Writer, insert after `StrokePath(...)` (after `:806`):
```csharp
    /// <summary>Record a sample series as chunked <see cref="DrawSeriesCmd"/>s. <paramref name="rect"/> is the node box;
    /// <paramref name="samples"/> beyond <see cref="SeriesSpec.MaxSamples"/> are dropped. Alloc-free.</summary>
    public void Series(in RectF rect, in SeriesSpec spec, ReadOnlySpan<float> samples, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        int n = Math.Min(samples.Length, SeriesSpec.MaxSamples);
        if (n < 2 || rect.W <= 0f || rect.H <= 0f) return;
        float dx = rect.W / (n - 1);
        int stops = 1;
        ColorF c0 = spec.Color, c1 = spec.Color, c2 = spec.Color, c3 = spec.Color;
        float o0 = 0f, o1 = 1f, o2 = 1f, o3 = 1f;
        if (spec.Gradient is { } g && g.Stops is { Length: > 0 } st)
        {
            stops = Math.Min(st.Length, GradientSpec.MaxStops);
            c0 = st[0].Color; o0 = st[0].Offset;
            if (stops > 1) { c1 = st[1].Color; o1 = st[1].Offset; }
            if (stops > 2) { c2 = st[2].Color; o2 = st[2].Offset; }
            if (stops > 3) { c3 = st[3].Color; o3 = st[3].Offset; }
        }
        float baseline = float.IsNaN(spec.Baseline) ? (spec.Shape == SeriesShape.Mirrored ? 0.5f : 1f) : spec.Baseline;
        for (int start = 0; start < n - 1; start += SeriesSpec.ChunkSamples - 1)
        {
            int count = Math.Min(SeriesSpec.ChunkSamples, n - start);
            var cmd = new DrawSeriesCmd
            {
                Rect = new RectF(rect.X + start * dx, rect.Y, (count - 1) * dx, rect.H),
                Transform = transform, Opacity = opacity,
                Shape = (int)spec.Shape, Count = count, Total = n, Index = start,
                X0 = rect.X + start * dx, Dx = dx, Baseline = baseline, Amplitude = spec.Amplitude, Thickness = spec.Thickness,
                C0 = c0, C1 = c1, C2 = c2, C3 = c3, O0 = o0, O1 = o1, O2 = o2, O3 = o3, StopCount = stops,
            };
            for (int i = 0; i < count; i++) cmd.S[i] = samples[start + i];
            WriteOp(DrawOp.DrawSeries);
            WritePayload(in cmd);
            PushSort(sortKey);
        }
    }
```
`Render\SceneRecorder.cs` — insert before `case VisualKind.Path:` (`:2596`):
```csharp
            case VisualKind.Series:
            {
                if (!maybeSparsePaint || !overlapsRecordClip) break;
                if (!scene.TryGetSeries(node, out var ss) || !scene.TryGetSeriesSamples(node, out var seriesSamples) || seriesSamples.Length < 2) break;
                dl.Series(local, in ss, seriesSamples, world, opacity, key);
                float seriesHalo = ss.Shape == SeriesShape.Stroke ? ss.Thickness : 0f;
                result.Include(world.TransformBounds(new RectF(local.X - seriesHalo, local.Y - seriesHalo, local.W + 2f * seriesHalo, local.H + 2f * seriesHalo)));
                break;
            }
```
`Seams\Rhi\RepaintPolicy.cs` (`TryBodySize`, before the `CompositeSlice` case): `case DrawOp.DrawSeries: body = Unsafe.SizeOf<DrawSeriesCmd>(); break;`. `Render\DrawOpTranslate.cs` (`Apply`, after the `StrokePath` arm): `case DrawOp.DrawSeries: { var c = Read<DrawSeriesCmd>(payload); c.Transform = Move(c.Transform, dx, dy); Write(payload, c); break; }`. `Render\Tiles\SliceOpBounds.cs` (after the `StrokePath` arm): `case DrawOp.DrawSeries: { var c = Read<DrawSeriesCmd>(payload); return Box(c.Rect, c.Transform, c.Shape == 2 ? RepaintCull.StrokeHalo(c.Thickness) : RepaintCull.AaHaloDip, out bounds); }`.

`Headless\Rhi\HeadlessGpuDevice.cs`: field `private readonly List<DrawSeriesCmd> _series = new(16);` after `:34`; accessor after `:102`: `/// <summary>Series chunks (DrawOp.DrawSeries) recorded this frame, in emission order.</summary> public IReadOnlyList<DrawSeriesCmd> LastSeries => _series;`; clear after `:249`: `_series.Clear();`; decode arm after the `StrokePath` case (`:402-405`):
```csharp
                case DrawOp.DrawSeries:
                    _series.Add(MemoryMarshal.Read<DrawSeriesCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawSeriesCmd>();
                    break;
```

#### 4.3.6 D3D12 — NEW `..\fluent-gpu\src\FluentGpu.Windows\D3D12\SeriesPipeline.cs`

```csharp
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Instance record for one series chunk — the GPU twin of <see cref="DrawSeriesCmd"/>, 72 floats (288 B),
/// laid out exactly as the HLSL <c>Inst</c> below.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SeriesInstance
{
    public float RectX, RectY, RectW, RectH;
    public float M11, M12, M21, M22;
    public float Dx, Dy, Opacity, Shape;
    public float Count, X0, SampleDx, Baseline;
    public float Amplitude, Thickness, StopCount, Pad0;
    public float C0R, C0G, C0B, C0A, C1R, C1G, C1B, C1A, C2R, C2G, C2B, C2A, C3R, C3G, C3B, C3A;
    public float O0, O1, O2, O3;
    public Samples32 S;

    public static SeriesInstance From(in DrawSeriesCmd c)
    {
        var i = new SeriesInstance
        {
            RectX = c.Rect.X, RectY = c.Rect.Y, RectW = c.Rect.W, RectH = c.Rect.H,
            M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
            Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity, Shape = c.Shape,
            Count = c.Count, X0 = c.X0, SampleDx = c.Dx, Baseline = c.Baseline,
            Amplitude = c.Amplitude, Thickness = c.Thickness, StopCount = c.StopCount, Pad0 = 0f,
            C0R = c.C0.R, C0G = c.C0.G, C0B = c.C0.B, C0A = c.C0.A, C1R = c.C1.R, C1G = c.C1.G, C1B = c.C1.B, C1A = c.C1.A,
            C2R = c.C2.R, C2G = c.C2.G, C2B = c.C2.B, C2A = c.C2.A, C3R = c.C3.R, C3G = c.C3.G, C3B = c.C3.B, C3A = c.C3.A,
            O0 = c.O0, O1 = c.O1, O2 = c.O2, O3 = c.O3,
        };
        for (int k = 0; k < 32; k++) i.S[k] = c.S[k];
        return i;
    }
}

/// <summary>The series lane (gpu-renderer.md §3.1 DrawSeriesCmd): one <c>DrawInstanced(64, chunks)</c> triangle-strip
/// pass whose vertex shader expands <c>SV_VertexID</c> into (sample, side) pairs — no vertex buffer, no tessellation,
/// no realization cache. Rides the shared SDF root signature (viewport constants b0, instance SRV t0) and the shared
/// TRIANGLESTRIP topology so it participates in the five-pipe shared-state dedup like Polyline; its PSO declares NO
/// input layout, so the shared quad VB being bound is inert. Colour = the ≤ 4-stop ramp by amplitude evaluated PER
/// PIXEL (the VS passes a signed amplitude coordinate that interpolates through 0 at the baseline, so interior stops
/// survive and a Mirrored column is not flat), premultiplied, SrcOver. Mirrored amplitude is measured against HALF the
/// height (a sample of 1.0 reaches the top/bottom edge from a 0.5 baseline) and every vertex is clamped to the chunk
/// rect, so the geometry never escapes the Rect that Cull, SliceOpBounds and damage are computed from. No AA fringe in
/// v1 (documented in the canon row).</summary>
internal sealed unsafe class SeriesPipeline : IDisposable
{
    private const int MaxInstances = 2048;   // per-FRAME policy cap (a 512-sample series is 17 chunks)

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    private UploadArena _arena = null!;
    private int _cursor;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst {
    float4 rect;
    float4 m;
    float2 t; float opacity; float shape;
    float count; float x0; float dx; float baseline;
    float amplitude; float thickness; float stopCount; float pad0;
    float4 c0; float4 c1; float4 c2; float4 c3;
    float4 offsets;
    float4 s[8];
};
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut
{
    float4 pos : SV_Position;
    float amp : TEXCOORD0;                  // signed amplitude coordinate: +s at the top edge, −s at the bottom (Mirrored), 0 at the baseline
    nointerpolation uint iid : TEXCOORD1;   // the instance, so the PS reads the ramp + opacity
};

float sampleAt(Inst it, int i)
{
    i = clamp(i, 0, (int)it.count - 1);
    return saturate(it.s[i >> 2][i & 3]);
}

float4 ramp(Inst it, float a)
{
    int n = (int)it.stopCount;
    if (n <= 1 || a <= it.offsets.x) return it.c0;
    if (a <= it.offsets.y || n == 2) return lerp(it.c0, it.c1, saturate((a - it.offsets.x) / max(it.offsets.y - it.offsets.x, 1e-4)));
    if (a <= it.offsets.z || n == 3) return lerp(it.c1, it.c2, saturate((a - it.offsets.y) / max(it.offsets.z - it.offsets.y, 1e-4)));
    return lerp(it.c2, it.c3, saturate((a - it.offsets.z) / max(it.offsets.w - it.offsets.z, 1e-4)));
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    int n = (int)it.count;
    int i = min((int)(vid >> 1), n - 1);
    int side = (int)(vid & 1);
    float s = sampleAt(it, i);
    float H = it.rect.w, top = it.rect.y, bottom = it.rect.y + H;
    float baseY = top + it.baseline * H;
    // Mirrored measures its amplitude against HALF the height, so a sample of 1.0 reaches the edge from a 0.5 baseline
    float amp = it.amplitude * (it.shape < 1.5 && it.shape >= 0.5 ? 0.5 * H : H);
    float x = it.x0 + i * it.dx;
    float2 p; float a;
    if (it.shape < 0.5)       { p = float2(x, side == 0 ? baseY - amp * s : baseY); a = side == 0 ? s : 0.0; }
    else if (it.shape < 1.5)  { p = float2(x, side == 0 ? baseY - amp * s : baseY + amp * s); a = side == 0 ? s : -s; }
    else
    {
        float sPrev = sampleAt(it, i - 1), sNext = sampleAt(it, i + 1);
        float2 tangent = normalize(float2(2.0 * it.dx, -(sNext - sPrev) * amp));
        float2 normal = float2(-tangent.y, tangent.x) * (it.thickness * 0.5);
        float2 c = float2(x, baseY - amp * s);
        p = side == 0 ? c + normal : c - normal; a = s;
    }
    p.y = clamp(p.y, top, bottom);   // never escape the chunk rect (Cull / SliceOpBounds / damage are computed from it)
    float2 world = float2(it.m.x * p.x + it.m.z * p.y + it.t.x, it.m.y * p.x + it.m.w * p.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.amp = a;
    o.iid = iid;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    Inst it = gInst[i.iid];
    float4 col = ramp(it, abs(i.amp));   // per pixel: |amp| runs baseline → peak on BOTH sides of a Mirrored column
    col.a *= it.opacity;
    return float4(col.rgb * col.a, col.a);
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared, UploadArena arena)
    {
        _shared = shared;
        _arena = arena;
        BuildPipeline(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    private void BuildPipeline(ID3D12Device* device)
    {
        ID3DBlob* vs = ShaderCompiler.Compile(Hlsl, "VSMain", "vs_5_1", "series");
        ID3DBlob* ps = ShaderCompiler.Compile(Hlsl, "PSMain", "ps_5_1", "series");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _shared.RootSignature;
        pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
        pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
        pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = null, NumElements = 0 };   // SV_VertexID only
        pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets = 1;
        pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        pd.SampleDesc.Count = 1;
        pd.SampleMask = uint.MaxValue;
        pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
        pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
        pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        pd.DepthStencilState.DepthEnable = BOOL.FALSE;
        pd.DepthStencilState.StencilEnable = BOOL.FALSE;
        ID3D12PipelineState* pso;
        Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Series.CreateGraphicsPipelineState");
        _pso = pso;
        vs->Release();
        ps->Release();
    }

    public void BeginFrame(int slot) { _ = slot; _cursor = 0; _dropped = 0; }

    /// <summary>Record one run (the Polyline contract: shared state and the PSO rebind independently; false when full).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<SeriesInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true)
    {
        int count = Math.Min(instances.Length, MaxInstances - _cursor);
        if (count <= 0) { _dropped += instances.Length; return false; }
        if (!_arena.TryReserve(count * sizeof(SeriesInstance), out byte* dst, out ulong gva))
        { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        SeriesInstance* slot = (SeriesInstance*)dst;
        for (int i = 0; i < count; i++) slot[i] = instances[i];
        _cursor += count;
        if (bindSharedState)
        {
            cmd->SetGraphicsRootSignature(_shared.RootSignature);
            _shared.SetViewportConstants(cmd, vpW, vpH);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            var qv = _shared.QuadView;
            cmd->IASetVertexBuffers(0, 1, &qv);
        }
        if (bindPipelineState) cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRootShaderResourceView(1, gva);
        cmd->DrawInstanced(2u * SeriesSpec.ChunkSamples, (uint)count, 0, 0); GpuDrawCount.Frame++;
        return true;
    }

    public void Dispose()
    {
        if (_pso != null) _pso->Release();
    }
}
```
(`using FluentGpu.Foundation;` for `SeriesSpec`; `Samples32`/`DrawSeriesCmd` come from `FluentGpu.Render`. `ShaderCompiler.Compile(string, string, string, string)` is the call `PolylineStrokePipeline.cs:140-141` makes.)

**`..\fluent-gpu\src\FluentGpu.Windows\D3D12\D3D12Device.cs`**: after `:147` (`_pathDraws`): `private SeriesPipeline? _seriesPipe; private readonly List<SeriesInstance> _seriesInsts = new();`. `PrimKind` (`:181`): `…, Path, Series }`. `BoundPipe` (`:236`): `…, Path, Series }`. `s_pipeStageNames` (`:577-578`): append `"series"`. Creation (`:604-657`): `SeriesPipeline? seriesPipe = null;` beside the others, `tasks[10] = Stage(10, () => { var p = new SeriesPipeline(); p.Init(_device, sdf, arena); seriesPipe = p; });` after `tasks[9]`, and `_seriesPipe = seriesPipe;` in the publish block. `BeginPipesFrame` (`:1812-1822`): `_seriesPipe!.BeginFrame(slot);`. `ClearInsts` (`:1899`): add `_seriesInsts.Clear();`. Decode (`:2132-2413`), after the `DrawPolylineStroke` arm:
```csharp
                case DrawOp.DrawSeries:
                {
                    var c = MemoryMarshal.Read<DrawSeriesCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawSeriesCmd>();
                    float halo = c.Shape == 2 ? RepaintCull.StrokeHalo(c.Thickness) : RepaintCull.AaHaloDip;
                    if (Cull(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, halo)) break;
                    CoverPendingText(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H, c.Transform.M11, c.Transform.M12,
                             c.Transform.M21, c.Transform.M22, c.Transform.Dx, c.Transform.Dy, halo);
                    _seriesInsts.Add(SeriesInstance.From(in c));
                    PushRun(PrimKind.Series);
                    break;
                }
```
Replay (`:3016-3162`): declare `var seriesSpan = CollectionsMarshal.AsSpan(_seriesInsts);` and `int sec = 0;` beside the others, and after the `PrimKind.Polyline` case:
```csharp
                    case PrimKind.Series:
                        bool bindSeriesShared = !_sharedSdfStateBound;
                        bool bindSeriesPso = _boundPipe != BoundPipe.Series;
                        if (stencilTest) _frameStencilFallback += count;   // uncovered pipeline: scissor-clipped only
                        NoteSdfPipeBind(_seriesPipe!.Record(_cmdList, seriesSpan.Slice(sec, count), lw, lh, bindSeriesShared, bindSeriesPso),
                            bindSeriesShared, bindSeriesPso, BoundPipe.Series);
                        sec += count; break;
```
Dispose: `_seriesPipe?.Dispose(); _seriesPipe = null;` after `:4485` (device-lost) and `_seriesPipe?.Dispose();` after `:4560` (final dispose). Diagnostics: `DroppedInstanceCount()` (`:2997-3001`) gains `+ (_seriesPipe?.DroppedInstances ?? 0)`, and `PublishDecodeDiagnostics()` (`:1655`) gains `Diag.Set("series", "dropped", _seriesPipe?.DroppedInstances ?? 0);` beside the path line at `:1690` (V-E16).

#### 4.3.7 VerticalSlice + screenshot

- `Harness\Asserts.cs` `DrawPayloadSize` (`:185-211`): `DrawOp.DrawSeries => Unsafe.SizeOf<DrawSeriesCmd>(),`.
- `Suites\ControlsSuite.cs` `DecodeVideoLayerNesting` (anchor by symbol — this file is dirty in the engine tree; today `:10638-10676`): `case DrawOp.DrawSeries: pos += Unsafe.SizeOf<DrawSeriesCmd>(); break;`.
- `Suites\PathSuite.cs` `StreamSizeGate` (`:846-927`): after `dl.StrokePath(...)` (`:879`) add `dl.Series(rect, new SeriesSpec(SeriesShape.Baseline, color, null, 2f, float.NaN, 1f, 1f), [0f, 0.5f, 1f], identity, 1f);`; extend `decodedAll` with `&& dev.LastSeries.Count == 1` and the detail string with `$"series={dev.LastSeries.Count} "`.
- NEW `Suites\SeriesSuite.cs` (GLOBAL namespace, exactly like `ListRowSuite.cs:1-24`; registered in `Harness\SuiteRegistry.cs:36-78` as `new("series", "series", SeriesSuite.Run),` after the `path` entry). It also carries the occlusion-hook gate (O3: one WP owns `HeadlessGpuDevice.cs`, so one suite checks both):
```csharp
using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>SeriesEl gates: chunking arithmetic, the overlap sample, the three shapes' baselines, headless decode, the
/// bound sample source's zero-allocation steady state, the empty-write release — and the WindowOccluded hook gate.</summary>
static class SeriesSuite
{
    public static void Run(StringTable strings)
    {
        RecordChecks();
        BoundChecks(strings);
        OcclusionCheck(strings);
    }

    static float[] Ramp(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = (float)i / (n - 1); return a; }

    static HeadlessGpuDevice Decode(DrawList dl)
    {
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400f, 200f), 1f, ColorF.Transparent));
        return dev;
    }

    /// <summary>The writer + the headless decode, driven directly (the PathSuite.StreamSizeGate shape, PathSuite.cs:846-927).</summary>
    static void RecordChecks()
    {
        var white = ColorF.FromRgba(255, 255, 255);
        foreach ((int n, int chunks) in new[] { (2, 1), (32, 1), (33, 2), (65, 3), (181, 6), (512, 17) })
        {
            var dl = new DrawList();
            dl.Series(new RectF(0f, 0f, 310f, 100f), new SeriesSpec(SeriesShape.Mirrored, white, null, 2f, float.NaN, 1f, 1f), Ramp(n), Affine2D.Identity, 1f);
            var dev = Decode(dl);
            bool count = dev.LastSeries.Count == chunks;
            bool overlap = true, index = true, total = true;
            for (int c = 0; c < dev.LastSeries.Count; c++)
            {
                var cmd = dev.LastSeries[c];
                index &= cmd.Index == c * 31;
                total &= cmd.Total == n;
                if (c > 0) { var prev = dev.LastSeries[c - 1]; overlap &= prev.S[prev.Count - 1] == cmd.S[0]; }   // a local: an inline array cannot be indexed on an rvalue
            }
            Check($"gate.series.record.chunks[{n}]", count && overlap && index && total,
                  $"chunks={dev.LastSeries.Count} expected={chunks} overlap={overlap} index={index} total={total}");
        }
        {
            var rect = new RectF(0f, 0f, 64f, 40f);
            var dl = new DrawList();
            dl.Series(rect, new SeriesSpec(SeriesShape.Baseline, white, null, 2f, float.NaN, 1f, 1f), Ramp(4), Affine2D.Identity, 1f);
            dl.Series(rect, new SeriesSpec(SeriesShape.Mirrored, white, null, 2f, float.NaN, 1f, 1f), Ramp(4), Affine2D.Identity, 1f);
            dl.Series(rect, new SeriesSpec(SeriesShape.Stroke, white, null, 3f, float.NaN, 1f, 1f), Ramp(4), Affine2D.Identity, 1f);
            var dev = Decode(dl);
            bool ok = dev.LastSeries.Count == 3 && dev.LastSeries[0].Baseline == 1f && dev.LastSeries[1].Baseline == 0.5f
                   && dev.LastSeries[2].Baseline == 1f && dev.LastSeries[2].Shape == 2 && dev.LastSeries[2].Thickness == 3f
                   && dev.LastSeries[0].Dx == 64f / 3f;
            Check("gate.series.record.shapes", ok, $"n={dev.LastSeries.Count}");
        }
        {
            // a 1-sample series paints nothing; an empty one paints nothing
            var dl = new DrawList();
            dl.Series(new RectF(0f, 0f, 64f, 40f), new SeriesSpec(SeriesShape.Baseline, white, null, 2f, float.NaN, 1f, 1f), [0.5f], Affine2D.Identity, 1f);
            dl.Series(new RectF(0f, 0f, 64f, 40f), new SeriesSpec(SeriesShape.Baseline, white, null, 2f, float.NaN, 1f, 1f), [], Affine2D.Identity, 1f);
            Check("gate.series.record.degenerate", Decode(dl).LastSeries.Count == 0, "1 and 0 samples emit no chunk");
        }
    }

    /// <summary>The ListRowSuite.Mount body (ListRowSuite.cs:91-100) with the device kept so the gates can read LastSeries / the swapchain.</summary>
    static AppHost Mount(StringTable strings, HeadlessPlatformApp app, HeadlessGpuDevice device, Component probe, string title, float w, float h)
    {
        var fonts = new HeadlessFontSystem(strings);
        var window = new HeadlessWindow(new WindowDesc(title, new Size2(w, h), 1f));
        window.Show();
        var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();
        return host;
    }

    /// <summary>A bound source rewritten every frame: the change frame may pay a bounded one-time cost; steady frames
    /// allocate 0 bytes in phases 6–13 (the ListRowSuite.cs:135-161 gate shape). Release gate: the frame after an
    /// empty write emits NO series chunk.</summary>
    static void BoundChecks(StringTable strings)
    {
        var source = new SeriesSource(181);
        using var app = new HeadlessPlatformApp();
        var dev = new HeadlessGpuDevice();
        var host = Mount(strings, app, dev, new SeriesProbe(source), "series", 320f, 120f);
        for (int k = 0; k < 4; k++) { source.Fill(k * 0.01f); host.RunFrame(); }
        source.Fill(0.5f);
        var change = host.RunFrame();
        for (int k = 0; k < 3; k++) { source.Fill(0.5f + k * 0.001f); host.RunFrame(); }
        source.Fill(0.75f);
        var steady = host.RunFrame();
        Check("gate.series.bound.zero-alloc", steady.HotPhaseAllocBytes == 0 && steady.Rendered,
              $"changeFrameAlloc={change.HotPhaseAllocBytes}B steadyAlloc={steady.HotPhaseAllocBytes}B rendered={steady.Rendered}");
        Check("gate.series.bound.draws", dev.LastSeries.Count == 6, $"chunks={dev.LastSeries.Count} expected=6 (181 samples)");
        source.Clear();
        host.RunFrame();
        host.RunFrame();
        Check("gate.series.bound.empty-releases", dev.LastSeries.Count == 0, $"chunks={dev.LastSeries.Count} after Clear()");
        host.Dispose();
    }

    /// <summary>The headless swapchain's Occluded flag reaches InputHooks.WindowOccluded within a frame, both ways.</summary>
    static void OcclusionCheck(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var dev = new HeadlessGpuDevice();
        var probe = new OcclusionProbe();
        var host = Mount(strings, app, dev, probe, "occlusion", 16f, 16f);
        host.RunFrame();
        bool seeded = probe.Seen == false;
        dev.PrimarySwapchain!.Occluded = true;
        host.RunFrame(); host.RunFrame();
        bool rose = probe.Seen == true;
        dev.PrimarySwapchain.Occluded = false;
        host.RunFrame(); host.RunFrame();
        bool fell = probe.Seen == false;
        Check("gate.occlusion.hook-follows-swapchain", seeded && rose && fell, $"seeded={seeded} rose={rose} fell={fell}");
        host.Dispose();
    }

    /// <summary>The producer side the app mirrors (Visualizer.UI.cs SeriesSource): one buffer, one version signal.</summary>
    sealed class SeriesSource(int n)
    {
        public readonly float[] Buffer = new float[n];
        public readonly Signal<uint> Version = new(0u);
        public int Count = n;
        public void Fill(float v) { for (int i = 0; i < Buffer.Length; i++) Buffer[i] = v + 0.2f * (i % 5) / 5f; Version.Value = Version.Peek() + 1; }
        public void Clear() { Count = 0; Version.Value = Version.Peek() + 1; }
        public SeriesSamples Current => new(Buffer, Count, Version.Value);   // reading .Value subscribes the bind
    }

    sealed class SeriesProbe(SeriesSource source) : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 320f, Height = 120f,
            Children = [new SeriesEl { Width = 320f, Height = 120f, Shape = SeriesShape.Mirrored, Samples = Prop.Of(() => source.Current) }],
        };
    }

    sealed class OcclusionProbe : Component
    {
        public bool? Seen;
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            Seen = hooks.WindowOccluded?.Value;   // .Value subscribes: the host's SetIfChanged re-renders this probe
            return new BoxEl { Width = 16f, Height = 16f };
        }
    }
}
```
(`HeadlessPlatformApp` is `IDisposable` and is `using`-scoped exactly as `ListRowSuite.cs:119`; `AppHost.RunFrame() : FrameStats` (`AppHost.cs:3325`, `FrameStats(int DrawCommandCount, int ClicksHandled, long HotPhaseAllocBytes, bool Rendered)` `:37`); the host is disposed with `host.Dispose()` (`ListRowSuite.cs:131/:161/:190`) — there is no `window.Close()`; `AppHost` exposes no device, so the suite keeps its own `HeadlessGpuDevice` reference.)

- `..\fluent-gpu\src\FluentGpu.WindowsApp\Scenes\ShotScene.cs` (`:55-70` switch): add `"series" => CenterShot(SeriesShot()),` and
```csharp
    static readonly float[] s_wave = Wave(181), s_ribbon = Ribbon(65);
    static float[] Wave(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = 0.15f + 0.8f * MathF.Abs(MathF.Sin(i * 0.19f)) * (0.6f + 0.4f * MathF.Sin(i * 0.031f)); return a; }
    static float[] Ribbon(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = 0.5f + 0.35f * MathF.Sin(i * 0.21f) + 0.1f * MathF.Sin(i * 0.53f); return a; }
    static Element SeriesShot() => new BoxEl
    {
        Width = 720f, Height = 360f, Direction = 1, Gap = 12f, Fill = ColorF.FromRgba(0x16, 0x14, 0x14),
        Children =
        [
            new SeriesEl { Width = 720f, Height = 150f, Shape = SeriesShape.Mirrored, Samples = new SeriesSamples(s_wave, s_wave.Length, 1u), Color = ColorF.FromRgba(0x2f, 0xc6, 0xdd) },
            new SeriesEl { Width = 720f, Height = 150f, Shape = SeriesShape.Baseline, Samples = new SeriesSamples(s_ribbon, s_ribbon.Length, 1u),
                           Gradient = new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, ColorF.FromRgba(0x2f, 0x9a, 0x6d, 200)), new GradientStop(0.7f, ColorF.FromRgba(0xf0, 0xa9, 0x3b, 200)), new GradientStop(1f, ColorF.FromRgba(255, 255, 255))]) },
            new SeriesEl { Width = 720f, Height = 36f, Shape = SeriesShape.Stroke, Thickness = 3f, Samples = new SeriesSamples(s_ribbon, s_ribbon.Length, 1u), Color = ColorF.FromRgba(255, 255, 255) },
        ],
    };
```
Run: `dotnet run --project src/FluentGpu.WindowsApp -- --screenshot series.png --shot series`.

#### 4.3.8 Canon (run `powershell -File docs\design\check-canon.ps1` after)

- `docs/design/subsystems/README.md` §2.1 after row `:74`: `| **DrawSeriesCmd** (\`DrawSeries\`=24, AS-BUILT 2026-10) — struct SHAPE + raster (the chunked sample-series lane: ≤32 inline samples per op, ⌈(N−1)/31⌉ ops per \`SeriesEl\`, \`SV_VertexID\` strip expansion, ≤4-stop amplitude ramp, no AA fringe) | **gpu-renderer.md §3.1** (shape) + §5.3 (raster) / scene-memory.md §4.1 (enum registration) + §2.4 (\`VisualKind.Series\`) + §2.2 (\`_series\` \`ColdSlab<SeriesSpec>\` + the pooled \`_seriesSamples\` side table) |`.
- `docs/design/subsystems/scene-memory.md`: the `DrawOp` list (`:735-791`) gains `DrawSeries, \ = 24 (AS-BUILT 2026-10): one chunk of a bound sample series (DrawSeriesCmd, gpu-renderer.md §3.1)`; §2.4 gains `VisualKind.Series = 10`; §2.2 gains the two rows (`_series` ColdSlab, SparsePaint-gated; `_seriesSamples` pooled `float[]`, VisualKind-gated capture, the `_rowCells` discipline).
- `docs/design/subsystems/gpu-renderer.md` §3.1: the `DrawSeriesCmd` field list exactly as in §4.3.5; §5.3 (new): "the series lane — no tessellation, an instanced strip; v1 has no AA fringe".
- `docs/design/SPEC-INDEX.md` §2: `| **Sample-series primitive (\`SeriesEl\` / \`DrawSeriesCmd\` = 24)** *(AS-BUILT 2026-10)* | \`subsystems/gpu-renderer.md\` §3.1 (shape + raster) · \`subsystems/scene-memory.md\` §2.2/§2.4/§4.1 (columns, VisualKind, enum) · \`subsystems/reconciler-hooks.md\` §0bis (the bound \`Samples\` channel is a plain \`Prop<T>\` bind) | **Canonical value:** \`DrawSeriesCmd(RectF Rect, Affine2D Transform, float Opacity, int Shape, Count, Total, Index, float X0, Dx, Baseline, Amplitude, Thickness, ColorF C0..C3, float O0..O3, int StopCount, Samples32 S)\`; a series of N samples is ⌈(N−1)/31⌉ chunks sharing one edge sample; the stream stays fixed-POD. |`.
- `docs/guide/components-elements-layout.md`: a "SeriesEl" paragraph (usage, the bound source idiom, the three shapes).
- `docs/design/subsystems/pal-rhi.md` (O3/V-E17 — the canon owner of `ISwapchain`): a row for `ISwapchain.IsOccluded` (per-target: `Frame.OccludedLatched || Frame.LastPresentStoodDown` on D3D12; `Occluded || PresentStandDown` headless) and for the host's `InputHooks.WindowOccluded` publication, with the composition-swapchain caveat spelled out.

### 4.4 Engine — `InputHooks.WindowOccluded` (folded into WP-E3 — O3)

**What is observable.** On a composition swapchain (Wavee: `FluentApp.cs:331` with `Mica = true` `:739` → `CreateSwapChainForComposition`, `D3D12Device.cs:1250-1251`) DXGI does NOT reliably return `DXGI_STATUS_OCCLUDED` (`:3443-3445`, `:3338-3339`), so **"covered by another window" is NOT observable for Wavee**. What IS observable is the present stand-down for a minimized / cloaked / hidden HWND (`IsHwndCovered` `:3456-3461` → `StandDownPresent` `:3463-3469` sets `Frame.LastPresentStoodDown`) — and the latch where a backend does return it. `IsOccluded` therefore folds BOTH flags (O4). The alt-tab case stays false (an inactive, uncovered window presents normally).

- `..\fluent-gpu\src\FluentGpu.Engine\Seams\Rhi\Rhi.cs`, inside `ISwapchain` (`:251-384`), insert after `:333` (`bool LastPresentStoodDown => false;`):
```csharp
    /// <summary>True while THIS target is not being shown: the backend's DXGI occlusion latch is set (DXGI_STATUS_OCCLUDED —
    /// a fully covered HWND swapchain; NOT reliably reported for composition swapchains) OR its last present stood down
    /// (minimized / cloaked / hidden). A pure read for the host's per-frame publication (<c>InputHooks.WindowOccluded</c>).
    /// Window deactivation (alt-tab) is NOT folded in. Default false (synchronous backends).</summary>
    bool IsOccluded => false;
```
- `..\fluent-gpu\src\FluentGpu.Windows\D3D12\D3D12Device.cs`, on **`D3D12Swapchain`** (`:4587`, `: ISwapchain`), insert after `:4892` (`public bool LastPresentStoodDown => Frame.LastPresentStoodDown;`): `/// <inheritdoc/> public bool IsOccluded => Frame.OccludedLatched || Frame.LastPresentStoodDown;` (`Frame` is the `internal readonly TargetFrameState` at `:4619`; both flags are `TargetFrameState.cs:46`, written on the render thread at `:3316/:3343` and `:3466`; a one-frame-stale bool read is the intended semantics). The device-level `:3451` forward is NOT touched.
- `..\fluent-gpu\src\FluentGpu.Engine\Headless\Rhi\HeadlessGpuDevice.cs`: on **`HeadlessSwapchain`** (`:466`), insert after `:494` (`LastPresentStoodDown => PresentStandDown;`): `/// <summary>Test seam: a modelled DXGI occlusion latch.</summary> public bool Occluded { get; set; }` and `/// <inheritdoc/> public bool IsOccluded => Occluded || PresentStandDown;`. On the device, after the `_primarySwapchain` field (`:174`): `/// <summary>The first swapchain created (the window's) — the gates' door to its occlusion / stand-down seams.</summary> public HeadlessSwapchain? PrimarySwapchain => _primarySwapchain;` (V-E22; `D3D12Device` already has `internal D3D12Swapchain? PrimarySwapchain` at `:317`).
- `..\fluent-gpu\src\FluentGpu.Engine\Hooks\Context.cs`, insert after `:293` (`WindowChromeEpoch`):
```csharp
    /// <summary>True while this window is not being shown — the primary swapchain's <c>IsOccluded</c>: minimized / cloaked /
    /// hidden (the present stand-down), or the DXGI occlusion latch where the backend reports one (not on composition
    /// swapchains). Not merely inactive: alt-tab keeps it false so a second-monitor ambient keeps running. Published by
    /// the host once per frame; null in a host-less tree.</summary>
    public Signal<bool>? WindowOccluded;
```
- `..\fluent-gpu\src\FluentGpu.Engine\Hosting\AppHost.cs`: a field `private readonly Signal<bool> _windowOccluded = new(false);` beside the other hook signals; in the ctor after `:2955` (`_inputHooks.WindowChromeEpoch = chromeEpoch;`): `_inputHooks.WindowOccluded = _windowOccluded;`; in `Paint`, immediately before `bool themeChanged = …` (`:3946`): `_windowOccluded.SetIfChanged(_swapchain.IsOccluded);` (`_swapchain : ISwapchain` `:380` — the same per-frame UI-thread read the stood-down counter takes at `:4513`; an unchanged value notifies nobody).
- Gate: `gate.occlusion.hook-follows-swapchain` in `SeriesSuite.OcclusionCheck` (§4.3.7). Unit test: `HeadlessSwapchain_IsOccluded_FoldsTheLatchAndTheStandDown` in `HeadlessGpuDeviceSeriesTests.cs` (§5.1).

### 4.5 App data (WP-D1, WP-D2, WP-D0 probe)

#### 4.5.1 Kind 237 — three bands kept (WP-D1)

**NEW `src/apps/Wavee/Entities/Waveform.cs`** (CORE, engine-free):
```csharp
// ── Entities/Waveform.cs ───────────────────────────────────────────────────────────────────────────────────────────
// WaveformBands (the kind-237 payload contract + its pure readers), BeatGrid (the audio-analysis grid + the tempo fallback)
//
// Role: CORE
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5
//
// ONE payload, two readers. Kind 237 lands as N `WaveSample` triples (low/mid/high, 0..255) — the `EdgeTable<WaveSample>`
// payload of Edges.TrackWaveform, so Count == Total == N (O6) — at most MaxSamples per track (longer answers are
// max-pooled onto it), indexed by the track's DURATION rather than a fixed hop (V-D17). The track drawer's 220 columns
// and the stage's Horizon both derive from it HERE, at read time — no lossy reduction at decode. `public` because
// Wavee.Tests is a ProjectReference with no InternalsVisibleTo. No allocation after warm-up: every reader takes the
// caller's span.

namespace Wavee;

/// <summary>One kind-237 sample: the three band envelopes (0..255) at one instant. 3 bytes, unmanaged — the
/// <c>EdgeTable&lt;WaveSample&gt;</c> payload (the <c>FormatEdge</c> payload-only precedent, Edges.cs:716/:847).</summary>
public readonly record struct WaveSample(byte Low, byte Mid, byte High);

public static class WaveformBands
{
    /// <summary>The wire's nominal hop (<c>ThreeBandWaveforms.hop_ms</c> = 20 on every observed answer). INFORMATIONAL — readers
    /// index by duration (<see cref="IndexAt"/>), never by this, because long answers are decimated onto <see cref="MaxSamples"/>.</summary>
    public const int NominalHopMs = 20;
    /// <summary>The most samples kept per track: 8 B/sample in the edge arena (3 payload + 4 targets + 1 pending) ⇒ 32 KB. A
    /// 4-minute answer (≈ 12,000 hops) is max-pooled 3:1 (≈ 59 ms per sample — finer than Horizon's 66 ms per point).</summary>
    public const int MaxSamples = 4096;
    /// <summary>The drawer's column count (was <c>Spotify.Decode.WaveformColumns</c>).</summary>
    public const int Columns = 220;

    /// <summary>Band fold for sample <paramref name="i"/> of <paramref name="n"/>: the MAX of <paramref name="band"/> over its share
    /// (max-pooling when decimating; one sample — nearest neighbour — when the band is shorter than n). 0 for an empty band.</summary>
    public static byte Fold(ReadOnlySpan<byte> band, int i, int n)
    {
        if (band.IsEmpty || n <= 0) return 0;
        int from = (int)((long)i * band.Length / n);
        int to = Math.Max(from + 1, (int)((long)(i + 1) * band.Length / n));
        byte m = 0;
        for (int k = from; k < to && k < band.Length; k++) if (band[k] > m) m = band[k];
        return m;
    }

    /// <summary>The sample under <paramref name="positionMs"/> of a <paramref name="durationMs"/>-long track (clamped) — index by
    /// DURATION, so decimated and full-rate answers read alike. 0 when either is unknown.</summary>
    public static int IndexAt(long positionMs, long durationMs, int n)
        => n <= 0 || durationMs <= 0 ? 0 : (int)Math.Clamp(positionMs * n / durationMs, 0L, n - 1L);

    /// <summary>The drawer's reduction: per column the MAX of each band over the column's share, SUMMED, normalised so the
    /// loudest column is 1.0 — the decoder's old arithmetic run at read time over the KEPT samples. On a decimated track a
    /// column is within one quantisation step of the old decode (the kept samples are themselves per-band maxima), so this
    /// is an equivalent, not a bit-identical, reproduction. False for silence.</summary>
    public static bool ToColumns(ReadOnlySpan<WaveSample> samples, Span<float> into)
    {
        into.Clear();
        int cols = into.Length, n = samples.Length, peak = 0;
        if (cols == 0 || n == 0) return false;
        Span<int> sums = cols <= 1024 ? stackalloc int[cols] : new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int from = (int)((long)c * n / cols);
            int to = Math.Max(from + 1, Math.Min(n, (int)((long)(c + 1) * n / cols)));
            int l = 0, m = 0, h = 0;
            for (int i = from; i < to; i++)
            {
                var s = samples[i];
                if (s.Low > l) l = s.Low;
                if (s.Mid > m) m = s.Mid;
                if (s.High > h) h = s.High;
            }
            int sum = l + m + h;
            sums[c] = sum;
            if (sum > peak) peak = sum;
        }
        if (peak <= 0) return false;
        for (int c = 0; c < cols; c++) into[c] = sums[c] / (float)peak;
        return true;
    }

    /// <summary>The three bands at a position, 0..1 each; false for an empty payload.</summary>
    public static bool At(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs, out float low, out float mid, out float high)
    {
        low = mid = high = 0f;
        if (samples.IsEmpty) return false;
        var s = samples[IndexAt(positionMs, durationMs, samples.Length)];
        low = s.Low / 255f; mid = s.Mid / 255f; high = s.High / 255f;
        return true;
    }

    /// <summary>A combined 0..1 level at a position — the Connect / <c>--fake</c> stand-in for the live RMS.</summary>
    public static float LevelAt(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs)
        => At(samples, positionMs, durationMs, out float l, out float m, out float h) ? (l + m + h) / 3f : 0f;
}

/// <summary>The audio-analysis beat grid (Edges.TrackBeats: one <c>uint</c> per beat — start ms in bits 0..30, bit 31 =
/// a bar's downbeat) and the kind-222 tempo fallback. Pure; the stage's Pulse and the Horizon ticks read it.</summary>
public static class BeatGrid
{
    public const int MaxBeats = 8192, MaxBars = 2048;
    public const uint DownbeatBit = 0x8000_0000u, MsMask = 0x7FFF_FFFFu;
    public const int DownbeatToleranceMs = 40;

    public static uint StartMs(uint packed) => packed & MsMask;
    public static bool IsDownbeat(uint packed) => (packed & DownbeatBit) != 0;

    /// <summary>Fold the bars in: the beat nearest each bar start (within the tolerance) is flagged a downbeat. Both
    /// spans are in wire order (ascending); linear, no allocation.</summary>
    public static void MarkDownbeats(Span<uint> beats, ReadOnlySpan<uint> bars)
    {
        if (beats.IsEmpty) return;
        int j = 0;
        foreach (uint bar in bars)
        {
            long t = bar & MsMask;
            while (j + 1 < beats.Length && (beats[j + 1] & MsMask) <= t) j++;
            int best = j;
            if (j + 1 < beats.Length && Math.Abs((long)(beats[j + 1] & MsMask) - t) < Math.Abs((long)(beats[j] & MsMask) - t)) best = j + 1;
            if (Math.Abs((long)(beats[best] & MsMask) - t) <= DownbeatToleranceMs) beats[best] |= DownbeatBit;
        }
    }

    /// <summary>The beat under <paramref name="positionMs"/>: its index (−1 before the first beat), the 0..1 phase inside it
    /// and the local period. False for an empty grid. Binary search; no allocation.</summary>
    public static bool Phase(ReadOnlySpan<uint> beats, long positionMs, out int index, out float phase, out float periodMs)
    {
        index = -1; phase = 0f; periodMs = 0f;
        if (beats.IsEmpty) return false;
        int lo = 0, hi = beats.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if ((beats[mid] & MsMask) <= positionMs) { index = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (index < 0) { periodMs = beats.Length > 1 ? (beats[1] & MsMask) - (beats[0] & MsMask) : 500f; return true; }
        long cur = beats[index] & MsMask;
        long next = index + 1 < beats.Length ? (beats[index + 1] & MsMask) : cur + (index > 0 ? cur - (beats[index - 1] & MsMask) : 500);
        periodMs = Math.Max(1f, next - cur);
        phase = Math.Clamp((positionMs - cur) / periodMs, 0f, 1f);
        return true;
    }

    /// <summary>The tempo-grid fallback (kind 222, <c>Track.Tempo</c> ×10): a constant period phase-locked to position
    /// (beat 0 at 0 ms). False when the tempo is unknown. Double arithmetic: a long modulo of a truncated float period
    /// drifts a beat every few minutes (V-D18).</summary>
    public static bool TempoPhase(ushort tempoX10, long positionMs, out float phase, out float periodMs)
    {
        if (tempoX10 == 0) { phase = 0f; periodMs = 0f; return false; }
        periodMs = 600_000f / tempoX10;
        double p = positionMs / (double)periodMs;
        phase = (float)(p - Math.Floor(p));
        return true;
    }
}
```

**`src/apps/Wavee/Spotify/Spotify.Decode.Traits.cs`** — delete `WaveformColumns` (`:27-29`); replace `Waveform` `:191-243` with:
```csharp
    /// <summary>Kind 237, THREE-BAND WAVEFORM: f3/f4/f5 are the low/mid/high byte envelopes (f1 = 44100 and f2 = the 20 ms
    /// hop are skipped — constant on every observed answer). Kept as N <see cref="WaveSample"/> triples, N = the longest
    /// band capped at <see cref="WaveformBands.MaxSamples"/> (a longer answer is MAX-POOLED onto it, never rejected — V-D9;
    /// the shorter bands are folded onto the same axis); the edge's Count and Total are N. Silence (no bands, or every
    /// byte 0 — V-D6) stages an EMPTY run: a Complete "no waveform" answer.</summary>
    public static void Waveform(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
    {
        var track = Identity(s, entityUri);
        if (track.IsEmpty) return;
        ReadOnlySpan<byte> low = default, mid = default, high = default;
        var r = new ProtoReader(proto);
        while (r.Next())
        {
            if (r.Wire != 2) { r.Skip(); continue; }
            switch (r.Field)
            {
                case 3: low = r.Bytes(); break;
                case 4: mid = r.Bytes(); break;
                case 5: high = r.Bytes(); break;
                default: r.Skip(); break;
            }
        }
        ref var run = ref s.TraitRuns.Add();
        run.Parent = track;
        run.Relation = TraitRelation.TrackWaveform;
        run.Start = s.Traits.Count;
        run.Length = 0;
        int n = Math.Min(WaveformBands.MaxSamples, Math.Max(low.Length, Math.Max(mid.Length, high.Length)));
        if (n == 0) return;                                              // no bands: an empty waveform is the answer
        byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(3 * n);
        try
        {
            Span<byte> bytes = scratch.AsSpan(0, 3 * n);
            Span<WaveSample> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, WaveSample>(bytes);
            for (int i = 0; i < n; i++)
                samples[i] = new WaveSample(WaveformBands.Fold(low, i, n), WaveformBands.Fold(mid, i, n), WaveformBands.Fold(high, i, n));
            if (bytes.IndexOfAnyExcept((byte)0) < 0) return;           // silence: every sample 0 — stage NOTHING (DecodeTests:1576-1579)
            run.Bytes = s.AddText(bytes);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(scratch); }
    }
```
**`src/apps/Wavee/Entities/Edges.cs`** (ALL of this file's edits are WP-D1's — V-D13):
- The relation block `:881-896`: `TrackWaveform` (`:885-888`) becomes `EdgeTable<WaveSample>` and gains `TrackBeats` after it:
```csharp
    /// <summary>Extension kind 237 kept as <see cref="WaveSample"/> triples (low/mid/high, 0-255) — at most
    /// <see cref="WaveformBands.MaxSamples"/> per track (a longer answer is max-pooled), indexed by DURATION. Parent = track
    /// slot; payload-only, targets unused; Count = Total = N. The drawer's 220 columns and the stage's Horizon derive from it
    /// at read time (<see cref="WaveformBands"/>).</summary>
    public readonly EdgeTable<WaveSample> TrackWaveform = new();
    /// <summary>The audio-analysis BEAT GRID: one <c>uint</c> per beat (start ms in bits 0..30, bit 31 = a bar's downbeat —
    /// <see cref="BeatGrid"/>), Count = Total = beat count. Parent = track slot; payload-only. An EMPTY Complete run means
    /// "the service has no grid for this track" and Pulse falls back to the tempo grid.</summary>
    public readonly EdgeTable<uint> TrackBeats = new();
```
- `TraitRelation` (`:1071`): `{ TrackCredits, TrackVersions, TrackWaveform, AlbumRecommendations, TrackBeats }` (APPENDED — the staged byte values of the existing four do not move).
- The `StagedTraitRun` doc (`:1083-1084`): "A waveform carries no members: its `WaveSample` triples ride `Bytes` in the staging arena; so does a beat grid (`uint`s)." The `CommitTraits` doc (`:1149-1151`): "(credits, versions, waveform, beats, recommendations)".
- A dedicated targets array beside `s_traitTargets` (`:1109-1114`) — NEVER `GrowTraits` (V-D8: one 4,096-wide run would permanently double every trait scratch array, `:1237-1247`): `static readonly int[] s_waveTargets = new int[BeatGrid.MaxBeats];   // 8192 ≥ WaveformBands.MaxSamples; all-zero and never written — payload-only relations`.
- The commit arm `:1200-1210` becomes, and the beats arm follows it (BEFORE `case TraitRelation.AlbumRecommendations:` `:1211`):
```csharp
                case TraitRelation.TrackWaveform:
                    {
                        int parent = s.Slot(Current.Tracks, in run.Parent);
                        if (parent == Table.None) break;
                        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, WaveSample>(s.Utf8(run.Bytes));
                        edges.TrackWaveform.Replace(parent, s_waveTargets.AsSpan(0, samples.Length), samples, EdgeState.Complete, samples.Length);
                        break;
                    }
                case TraitRelation.TrackBeats:
                    {
                        int parent = s.Slot(Current.Tracks, in run.Parent);
                        if (parent == Table.None) break;
                        var beats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(s.Utf8(run.Bytes));
                        edges.TrackBeats.Replace(parent, s_waveTargets.AsSpan(0, beats.Length), beats, EdgeState.Complete, beats.Length);
                        break;
                    }
```
(`EdgeTable.Replace` requires `payload.Length == targets.Length` for a non-empty payload, `:337-338`; both decoders cap their runs at the array's size: 4,096 samples / 8,192 beats.)

**`src/apps/Wavee/Entities/Track.Drawer.cs`** — `PeaksFor` `:181-193` becomes:
```csharp
        float[]? PeaksFor(Edges edges, int parent, uint epoch)
        {
            if (edges.TrackWaveform.State(parent) == EdgeState.Unknown) return null;
            uint version = edges.TrackWaveform.Version(parent);
            if (_peaks is null || _peaksParent != parent || _peaksVersion != version || _peaksEpoch != epoch)
            {
                var columns = new float[WaveformBands.Columns];
                _peaks = WaveformBands.ToColumns(edges.TrackWaveform.Payload(parent), columns) ? columns : [];
                (_peaksParent, _peaksVersion, _peaksEpoch) = (parent, version, epoch);
            }
            return _peaks.Length == 0 ? null : _peaks;
        }
```
Delete `DrawerRules.Peaks` (`:783-789`) and its test `TrackDrawerRulesTests.cs:265-274` (`Waveform_magnitudes_scale_to_unit_peaks_within_the_buffer`, V-D4); `:682` stays (`WaveBars = 64`).

**`Entities.Fake.Album.cs`** (V-D7): `:64` `const int Waveform = Spotify.Decode.WaveformColumns;` → `const int ZeroTargets = 8;` (the `zeros` spans at `:467` and `:507` become `stackalloc int[ZeroTargets]` — `Tags` needs ≤ 2 and `Formats` ≤ 5 targets) plus `static readonly int[] s_waveTargets = new int[WaveformBands.MaxSamples];` in `AlbumSeed`. `Traits()` (`:530-538`, the plain rows) gains `e.TrackBeats.ReplaceRun(track, default, default);` after the waveform line. In `Drawer(...)`, `:562-569` becomes (only the four drawer rows get bands; every other row stays an EMPTY Complete waveform):
```csharp
            // waveform: WaveSample triples, a deterministic triangle envelope — low full, mid 0.7×, high 0.45×; N from the row's
            // duration at the wire's 20 ms hop, capped like a real answer
            int n = Math.Clamp((int)(new Track(track).DurationMs / WaveformBands.NominalHopMs), 64, WaveformBands.MaxSamples);
            WaveSample[] wave = System.Buffers.ArrayPool<WaveSample>.Shared.Rent(n);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    int phase = (i * 3 + pool) % 64;
                    byte low = (byte)(60 + (phase < 32 ? phase : 63 - phase) * 6);
                    wave[i] = new WaveSample(low, (byte)(low * 0.7f), (byte)(low * 0.45f));
                }
                e.TrackWaveform.Replace(track, s_waveTargets.AsSpan(0, n), wave.AsSpan(0, n), EdgeState.Complete, n);
            }
            finally { System.Buffers.ArrayPool<WaveSample>.Shared.Return(wave); }
            e.TrackBeats.ReplaceRun(track, default, default);   // no grid under --fake: Pulse takes the seeded tempo
```
Tests (WP-D1): `EntitiesFakeAlbumTests.cs:234` (`Assert.Equal(Spotify.Decode.WaveformColumns, e.TrackWaveform.Count(t.Slot))`) becomes `Assert.InRange(e.TrackWaveform.Count(t.Slot), 64, WaveformBands.MaxSamples); Assert.Equal(e.TrackWaveform.Count(t.Slot), e.TrackWaveform.Total(t.Slot));` (V-D3).

#### 4.5.2 The audio-analysis route, decoder and `TrackBeats` edge (WP-D2)

- `Entities/Fetch.Routes.cs`: `FetchEdge` — insert `TrackBeats,` after `TrackWaveform,` (`:80`); `SpclientRoute` — append `AudioAnalysis,` (`:153-183`); `ForEdge` — add `FetchEdge.TrackBeats => FetchRoute.Spclient(SpclientRoute.AudioAnalysis, 0),` after the `TrackWaveform` arm (`:445`).
- `Entities/Fetch.Edges.cs`: `EdgeTableOf` — `FetchEdge.TrackBeats => e.TrackBeats,` after `TrackWaveform` (`:178`); `ParentTableOf` (`:219`) — `FetchEdge.TrackCredits or FetchEdge.TrackVersions or FetchEdge.TrackWaveform or FetchEdge.TrackBeats => scope.Tracks,`.
- The `TrackBeats` edge, the `TraitRelation.TrackBeats` member and the beats commit arm are WP-D1's (`Entities/Edges.cs`, §4.5.1 — V-D13); WP-D2 edits neither `Edges.cs` nor `Track.cs`.
- `Spotify/Spotify.Api.cs`: `Serves` (`:707-712`) — append `or SpclientRoute.AudioAnalysis` to the `RouteTransport.Spclient` arm; `AnswerRest` (`:1162-1195`) — add before `case SpclientRoute.LikedContentFilters:`:
```csharp
                case SpclientRoute.AudioAnalysis:
                    result = AudioAnalysis(IdOf(uri), ApiHost.Spclient, ct);
                    if (result.Ok && result.Body.Length > 0) Decode.AudioAnalysis(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
                    break;
```
and after `ArtistTopTracksExtended` (`:2031-2038`), the fetch plus the PROBE (WP-D2 owns both — the probe's Diagnostics arms are WP-D0's, §4.5.3):
```csharp
        /// <summary>`/audio-attributes/v1/audio-analysis/{base62}` — the track's beat/bar grid as JSON (CommonJson, the
        /// <see cref="FriendPresence"/> PathWriter shape, :1985-1992). <paramref name="host"/> is <see cref="ApiHost.Spclient"/>
        /// (apresolve) for the data path; the probe also tries <see cref="ApiHost.SpclientWg"/>.</summary>
        public static Result AudioAnalysis(string trackId, ApiHost host, CancellationToken ct)
        {
            Span<char> path = stackalloc char[128];
            var w = new PathWriter(path);
            w.Append("/audio-attributes/v1/audio-analysis/");
            w.AppendEscaped(trackId);
            return Get(w.Written, host, CommonJson, ct);
        }

        /// <summary>The wave-D probe (§4.5.3; blocking — api threads only, never the loop thread): ask BOTH spclient hosts, walk
        /// the WHOLE body, and report one JSON line — `{"ev":"analysis","host":…,"status":…,"bytes":…,"beats":N,"bars":N,
        /// "keys":[…top-level members…],"verdict":"A"|"B"|"C"}`. Verdict A = 200 and beats &gt; 0 (the decoder as written);
        /// B = 200 with another shape (the top-level keys say which); C = no 200 from either host, or non-JSON.</summary>
        public static string AudioAnalysisProbe(string trackUri, CancellationToken ct)
        {
            string id = IdOf(trackUri);
            string line = "";
            foreach (ApiHost host in (ReadOnlySpan<ApiHost>)[ApiHost.Spclient, ApiHost.SpclientWg])
            {
                Result r = AudioAnalysis(id, host, ct);
                int beats = 0, bars = 0;
                var keys = new System.Collections.Generic.List<string>(8);
                bool json = r.Body.Length > 0 && ProbeWalk(r.Bytes, keys, ref beats, ref bars);
                string verdict = r.Status == 200 && json && beats > 0 ? "A" : r.Status == 200 && json ? "B" : "C";
                var sb = new System.Text.StringBuilder(256);
                sb.Append("{\"ev\":\"analysis\",\"host\":\"").Append(host == ApiHost.Spclient ? "spclient" : "wg")
                  .Append("\",\"status\":").Append(r.Status).Append(",\"bytes\":").Append(r.Body.Length)
                  .Append(",\"beats\":").Append(beats).Append(",\"bars\":").Append(bars).Append(",\"keys\":[");
                for (int i = 0; i < keys.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(System.Text.Json.JsonEncodedText.Encode(keys[i])).Append('"'); }
                sb.Append("],\"verdict\":\"").Append(verdict).Append("\"}");
                line = sb.ToString();
                if (verdict != "C") break;            // the first host that answers decides; C on both prints the LAST (wg) line
            }
            return line;
        }

        /// <summary>Full-body walk: every top-level member name, and the element counts of the `beats` and `bars` arrays
        /// (wherever the array sits at depth 1). False for non-JSON.</summary>
        static bool ProbeWalk(ReadOnlySpan<byte> body, System.Collections.Generic.List<string> keys, ref int beats, ref int bars)
        {
            try
            {
                var r = new System.Text.Json.Utf8JsonReader(body);
                if (!r.Read() || r.TokenType != System.Text.Json.JsonTokenType.StartObject) return false;
                while (r.Read() && r.TokenType == System.Text.Json.JsonTokenType.PropertyName)
                {
                    string name = r.GetString() ?? "";
                    keys.Add(name);
                    r.Read();
                    if (r.TokenType == System.Text.Json.JsonTokenType.StartArray && name is "beats" or "bars")
                    {
                        int count = 0;
                        int depth = r.CurrentDepth;
                        while (r.Read() && !(r.TokenType == System.Text.Json.JsonTokenType.EndArray && r.CurrentDepth == depth))
                            if (r.CurrentDepth == depth + 1 && r.TokenType is System.Text.Json.JsonTokenType.StartObject or System.Text.Json.JsonTokenType.Number) count++;
                        if (name == "beats") beats = count; else bars = count;
                    }
                    else r.TrySkip();
                }
                return true;
            }
            catch (System.Text.Json.JsonException) { return false; }
        }
```
- NEW `src/apps/Wavee/Spotify/Spotify.Decode.Analysis.cs`:
```csharp
// ── Spotify/Spotify.Decode.Analysis.cs ─────────────────────────────────────────────────────────────────────────────
// the audio-analysis JSON → Edges.TrackBeats
//
// Role: CORE
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5.2

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>`{"beats":[{"start":s,"duration":d,"confidence":c},…],"bars":[…],…}` → every beat's start in ms with
        /// the bars folded in as downbeat flags (<see cref="BeatGrid.MarkDownbeats"/>). Unknown members are skipped; a 2xx
        /// with no beats stages an EMPTY run — a real "no grid" answer, never a re-ask. Non-JSON (a captive portal, a
        /// truncated body) stages NOTHING (the door re-asks later) — the reader throws <see cref="JsonException"/>, caught here (V-D19).</summary>
        public static void AudioAnalysis(ReadOnlySpan<byte> json, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            uint[] beats = ArrayPool<uint>.Shared.Rent(BeatGrid.MaxBeats);
            uint[] bars = ArrayPool<uint>.Shared.Rent(BeatGrid.MaxBars);
            int nb = 0, nbars = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;
                for (int root = r.CurrentDepth; Next(ref r, root);)
                {
                    if (r.ValueTextEquals("beats"u8)) nb = ReadStarts(ref r, beats.AsSpan(0, BeatGrid.MaxBeats));
                    else if (r.ValueTextEquals("bars"u8)) nbars = ReadStarts(ref r, bars.AsSpan(0, BeatGrid.MaxBars));
                    else SkipValue(ref r);
                }
            }
            catch (JsonException)
            {
                ArrayPool<uint>.Shared.Return(beats);
                ArrayPool<uint>.Shared.Return(bars);
                return;                                                  // garbage: no run, no state change
            }
            try
            {
                ref var run = ref s.TraitRuns.Add();
                run.Parent = track;
                run.Relation = TraitRelation.TrackBeats;
                run.Start = s.Traits.Count;
                run.Length = 0;
                if (nb == 0) return;
                BeatGrid.MarkDownbeats(beats.AsSpan(0, nb), bars.AsSpan(0, nbars));
                run.Bytes = s.AddText(MemoryMarshal.AsBytes(beats.AsSpan(0, nb)));
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(beats);
                ArrayPool<uint>.Shared.Return(bars);
            }
        }

        /// <summary>`[{"start": seconds, …}, …]` → start ms in wire order; stops at the span's capacity.</summary>
        static int ReadStarts(ref Utf8JsonReader r, Span<uint> into)
        {
            int n = 0;
            if (!EnterArray(ref r)) return 0;
            for (int list = r.CurrentDepth; Element(ref r, list);)
            {
                double start = double.NaN;
                for (int item = r.CurrentDepth; Next(ref r, item);)
                {
                    if (!r.ValueTextEquals("start"u8)) { SkipValue(ref r); continue; }
                    r.Read();
                    if (r.TokenType == JsonTokenType.Number) start = r.GetDouble(); else r.Skip();
                }
                if (!double.IsNaN(start) && start >= 0 && n < into.Length) into[n++] = (uint)Math.Min(start * 1000.0 + 0.5, BeatGrid.MsMask);
            }
            return n;
        }
    }
}
```
(`Identity`, `Next`, `EnterArray`, `Element`, `SkipValue` are the existing private helpers of the same partial class, `Spotify.Decode.Pathfinder.cs:46-103`.)
- Tests (WP-D2): `FetchRoutesTests.cs:121-136` gains `[InlineData(FetchEdge.TrackBeats, RouteTransport.Spclient)]`; `EdgeDoorTests:406` passes by construction once the three maps have the arm; NEW `src/apps/Wavee.Tests/AudioAnalysisDecodeTests.cs` holds `AudioAnalysis_stages_beats_in_ms_with_bars_as_downbeats_and_an_empty_answer_is_complete` and `AudioAnalysis_ignores_non_json` (§5) — `DecodeTests.cs` belongs to WP-D1 only (V-D13).

#### 4.5.3 The audio-analysis probe (WP-D0 — written in wave D, RUN at the final verification — O5)

The repo has no code that calls `/audio-attributes/v1/audio-analysis/`; the JSON shape above is the public Web-API analysis shape and is **unverified for the spclient host**. The probe is a headless script verb. **Owner decision (O5):** there is NO build between waves, so the probe's code lands with wave D and the probe RUNS once at the final verification (§6.3 step 2a). WP-D2 is implemented regardless, with the tempo fallback live. If the probe returns verdict **C**, the orchestrator deletes the D2 decoder (`Spotify.Decode.Analysis.cs`), the route (`SpclientRoute.AudioAnalysis` + the `Serves`/`AnswerRest` arms + `FetchEdge.TrackBeats` + its two map arms) and the `TrackBeats` edge (+ its `TraitRelation` member and commit arm) THEN, before the final build — `BeatGrid.TempoPhase` and Pulse stay; `--fake` already seeds `TrackBeats` empty (V-D24). Verdict **B** = adapt `ReadStarts`' member names to the reported top-level keys (same skeleton).

- `src/apps/Wavee/Screens/Diagnostics.Headless.cs` (CORE — the executor is the SHELL's): `Verb` (`:78-82`) — append `Analysis` (after `Quit`); `VerbOf` (`:147-155`) — add `"analysis" => Verb.Analysis,`; the grammar comment (`:16-30`) gains a line `//   analysis <spotify:track:id>   (prints one {"ev":"analysis",…,"verdict":"A|B|C"} line — the audio-analysis endpoint probe)`; `TryParseText`'s verb switch (`:175-327`, copy the `Verb.Queue` arm `:209-215`) — add
```csharp
                case Verb.Analysis:
                    {
                        string uri = argc == 1 ? line[parts[1]].ToString() : "";
                        // IsSpotifyUri(containers: false) still admits episodes (:359); the analysis endpoint is track-only (V-D20/V-U52)
                        if (!IsSpotifyUri(uri, containers: false) || EntityUri.KindOf(uri.AsSpan()) != EntityKind.Track)
                        { error = "analysis wants spotify:track:<22-char id>"; return false; }
                        cmd = new Command(Verb.Analysis, text, Arg0: uri, Id: id);
                        return true;
                    }
```
(`EntityUri.KindOf(ReadOnlySpan<char>)` is the pure kind test, `Entities.cs:652` — the `Spotify.Decode.Traits.cs:174` idiom.)
- `src/apps/Wavee/Screens/Diagnostics.Probe.cs` (the executor switch `:592-661`, beside `case Headless.Verb.Status:` `:643`). The HTTP call BLOCKS, so it runs on an api thread through `Spotify.Api.Run` and posts its line back to the loop — the `RequestMembers` shape (`:696-736`); `Emit` is loop-thread only:
```csharp
                case Headless.Verb.Analysis:
                    {
                        string uri = c.Arg0;
                        bool queued = Spotify.Api.Run(() =>
                        {
                            string line = Spotify.Api.AudioAnalysisProbe(uri, CancellationToken.None);   // blocking: api thread
                            s_loop.Post(() => Emit(line));
                        });
                        if (!queued) { error = "api queue full — retry"; return false; }
                        return true;
                    }
```
- Run recipe (the ONLY sanctioned way to run headless, `ops/headless/Invoke-WaveeHeadless.ps1:1-45`; `.claude/skills/wavee/probes.md` is stale). NEW `ops/headless/analysis.wh`:
```
# the audio-analysis endpoint probe (fullscreen-flagship-implementation.md §4.5.3) — the runner itself waits for the session
wait online timeout 60000
analysis spotify:track:4cOdK2wGLETKBW3PvgPWqT
sleep 4000
quit
```
  — there is NO `login` line: the script runner gates a file script on being online by itself (`Diagnostics.Probe.cs:437-438`; a login failure exits 67/75/77), and `online` is a wait CONDITION, not a verb (`Diagnostics.Headless.cs:494-519`). The `sleep` gives the posted line time to land before `quit`.
  `powershell -File ops\headless\Invoke-WaveeHeadless.ps1 ops\headless\analysis.wh -Configuration Release` on a machine WITHOUT a packaged Wavee; with one installed the script refuses without `-Profile <dir>` (`:114-120`), and a profile has no credential until one was saved into it — run `login-smoke.wh -Store -Profile <dir>` once first. The JSONL lands under `artifacts\headless`; read the `"ev":"analysis"` line's `verdict`.
- Verdicts: **(A)** `"verdict":"A"` (200, `beats > 0`) → WP-D2 as written. **(B)** `200` with another shape (`"keys"` names the members) → WP-D2's `ReadStarts` member names adapted. **(C)** no 200 from either host, or non-JSON → the O5 deletion above; Pulse runs on the tempo grid alone; the probe verb stays in the tree for a later retry.

### 4.6 Settings, `Prefs.Stage`, Appearance rows, loc (WP-D3)

**`src/apps/Wavee/Platform/Platform.Settings.cs`** — insert after `StageRects` (`:39`), inside `partial class Keys`:
```csharp
        // ── the fullscreen stage (docs/plans/wavee/fullscreen-flagship-implementation.md §2.11) — all written through Prefs.Stage ──
        /// <summary>Stage.Mode as an int (0 Lyrics · 1 Visualizer · 2 Queue · 3 Artist); unknown ⇒ Lyrics.</summary>
        public static readonly SettingKey<int> StageMode = new("stage.mode", 0);
        /// <summary>Visualizer.Kind as an int (0 Field … 7 Tape); unknown ⇒ Horizon (2).</summary>
        public static readonly SettingKey<int> StageVisualizer = new("stage.visualizer", 2);
        /// <summary>Band gain, clamped [0.3, 1.5].</summary>
        public static readonly SettingKey<float> StageSensitivity = new("stage.sensitivity", 1f);
        /// <summary>The active lyric line is drawn over the visualizer.</summary>
        public static readonly SettingKey<bool> StageLyricsOverlay = new("stage.lyricsOverlay", true);
        /// <summary>Analysis-window offset in ms (positive = earlier), clamped ±500.</summary>
        public static readonly SettingKey<int> StageSyncOffsetMs = new("stage.syncOffsetMs", 0);
        /// <summary>Calmer faces: ×0.55 gain, slower attack, no beat kick (the gallery's "Reduce motion").</summary>
        public static readonly SettingKey<bool> StageCalm = new("stage.calm", false);
        /// <summary>The gallery pane is open in Visualizer mode.</summary>
        public static readonly SettingKey<bool> StageGalleryOpen = new("stage.galleryOpen", true);
        /// <summary>The "Pick your visualizer" tip has been dismissed once.</summary>
        public static readonly SettingKey<bool> StageTipSeen = new("stage.tipSeen", false);
```

**NEW `src/apps/Wavee/Platform/Prefs.Stage.cs`** (V-D21 — the `Prefs.Player.cs` precedent: a partial per family; `namespace Wavee;` + `public static partial class Prefs`, `Prefs.Player.cs:16-18`):
```csharp
// ── Platform/Prefs.Stage.cs ────────────────────────────────────────────────────────────────────────────────────────
// Prefs.Stage — the fullscreen stage's preference family (mode, visualizer, sensitivity, lyrics overlay, sync offset,
// calm, gallery, tip) behind ONE epoch
//
// Role: CORE
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §2.11, §4.6
//
// Every reader is the Prefs.cs:12-22 two-liner (`_ = Epoch.Value; return Platform.Settings.Get(key)`), every writer
// clamps through the CORE rule owner and bumps once. INSIDE `Prefs`, bare `Stage` binds to THIS class, so the CORE owners
// are spelled `global::Wavee.Stage` / `global::Wavee.Visualizer` (the Lyrics.cs:1097 idiom in reverse).

using FluentGpu.Signals;

namespace Wavee;

public static partial class Prefs
{
    /// <summary>The fullscreen stage's epoch: mode, visualizer, sensitivity, lyrics overlay, sync offset, calm, gallery,
    /// tip. One bump per write. Read ONLY from render/effect code (never a tick, a bind thunk or a band loop — O8).</summary>
    public static class Stage
    {
        /// <inheritdoc cref="Stage"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        public static int Mode() { _ = Epoch.Value; return global::Wavee.Stage.ModeRules.Coerce(Platform.Settings.Get(Platform.Keys.StageMode)); }
        public static void SetMode(int mode) { Platform.Settings.Set(Platform.Keys.StageMode, global::Wavee.Stage.ModeRules.Coerce(mode)); Bump(); }
        public static int Visualizer() { _ = Epoch.Value; return global::Wavee.Visualizer.Catalog.Coerce(Platform.Settings.Get(Platform.Keys.StageVisualizer)); }
        public static void SetVisualizer(int kind) { Platform.Settings.Set(Platform.Keys.StageVisualizer, global::Wavee.Visualizer.Catalog.Coerce(kind)); Bump(); }
        public static float Sensitivity() { _ = Epoch.Value; return global::Wavee.Visualizer.Bands.ClampSensitivity(Platform.Settings.Get(Platform.Keys.StageSensitivity)); }
        public static void SetSensitivity(float v) { Platform.Settings.Set(Platform.Keys.StageSensitivity, global::Wavee.Visualizer.Bands.ClampSensitivity(v)); Bump(); }
        public static bool LyricsOverlay() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageLyricsOverlay); }
        public static void SetLyricsOverlay(bool on) { Platform.Settings.Set(Platform.Keys.StageLyricsOverlay, on); Bump(); }
        public static int SyncOffsetMs() { _ = Epoch.Value; return System.Math.Clamp(Platform.Settings.Get(Platform.Keys.StageSyncOffsetMs), -500, 500); }
        public static void SetSyncOffsetMs(int ms) { Platform.Settings.Set(Platform.Keys.StageSyncOffsetMs, System.Math.Clamp(ms, -500, 500)); Bump(); }
        public static bool Calm() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageCalm); }
        public static void SetCalm(bool on) { Platform.Settings.Set(Platform.Keys.StageCalm, on); Bump(); }
        public static bool GalleryOpen() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageGalleryOpen); }
        public static void SetGalleryOpen(bool on) { Platform.Settings.Set(Platform.Keys.StageGalleryOpen, on); Bump(); }
        public static bool TipSeen() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageTipSeen); }
        public static void SetTipSeen() { Platform.Settings.Set(Platform.Keys.StageTipSeen, true); Bump(); }
    }
}
```
**`src/apps/Wavee/Platform/Prefs.cs`** — `BumpAll()` (`:279-286`) gains `Stage.Bump();` after `PlayerBar.Bump();`; **`src/apps/Wavee.Tests/PrefsTests.cs:66-77`** (`BumpAll_moves_every_epoch`) captures `Prefs.Stage.Epoch.Peek()` beside the five and asserts it moved by exactly one.

**`src/apps/Wavee/Screens/Settings.cs`** — `Catalog.Sections` (`:69-99`): insert `new(Tab.Appearance, "Fullscreen", "TvMonitor"),` between the `"Lyrics"` row (`:80`) and the `"Now playing"` row (`:81`) — `SectionGlyph` (`:192-198`) THROWS for an unregistered section (V-D1); `TvMonitor` is mapped in `Settings.UI.cs:334` and is not a row glyph in this section (`SettingsCatalogTests.NoRow_ReusesItsOwnSectionsGlyph`). `Catalog.Rows` (`:128-146`), insert after the `lyricsBlur` row (`:132`) — all five glyph names are in the `Glyph(string)` map (`Settings.UI.cs:323-341`) and distinct within the section:
```csharp
            new(Tab.Appearance, "Fullscreen", "stageVisualizer", "Equalizer"),
            new(Tab.Appearance, "Fullscreen", "stageSensitivity", "Audio"),
            new(Tab.Appearance, "Fullscreen", "stageLyricsOverlay", "Document"),
            new(Tab.Appearance, "Fullscreen", "stageSyncOffset", "Clock"),
            new(Tab.Appearance, "Fullscreen", "stageCalm", "RefineSparkle"),
```

**`src/apps/Wavee/Screens/Settings.UI.Appearance.cs`** — statics after `s_lyricsBlurOptions` (`:53-57`):
```csharp
    static readonly Signal<int> s_stageVisualizer = new((int)Visualizer.Kind.Horizon);
    static readonly FloatSignal s_stageSensitivity = new(1f), s_stageSyncOffset = new(0f);
    static readonly Slider.SliderOptions s_stageSensitivityOptions = new()
    {
        Min = 0.3f, Max = 1.5f, Step = 0.05f, TickFrequency = 0.3f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };
    static readonly Slider.SliderOptions s_stageSyncOptions = new()
    {
        Min = -500f, Max = 500f, Step = 10f, TickFrequency = 250f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms",
    };
```
`SeedAppearance()` (`:62-63`) becomes a block body that ALSO seeds `s_stageVisualizer.Value = Prefs.Stage.Visualizer(); s_stageSensitivity.Value = Prefs.Stage.Sensitivity(); s_stageSyncOffset.Value = Prefs.Stage.SyncOffsetMs();`. **`Settings.UI.cs` `SettingsPageView.Render`** (`:135-136`, beside `_ = Prefs.PlayerBar.Epoch.Value;`): add `_ = Prefs.Stage.Epoch.Value;` so a gallery write re-renders the page (V-D14; `AppearanceTab()` is a static method called from that render, `:67`/`Settings.UI.cs:101`). In `AppearanceTab()` after the lyrics row (`:115-116`) — the three value controls are CHILD COMPONENTS that re-seed their signal from the preference on every epoch (the `NpvStylePicker` pattern, `:694-705`), so a change made in the stage's gallery shows here LIVE:
```csharp
        // ── the fullscreen stage: the gallery's four settings, mirrored here with the rest of Appearance ──
        kids.Add(SectionHeader(Loc.Get(Strings.Stage.Settings.Title), SectionGlyph(Tab.Appearance, "Fullscreen"), Loc.Get(Strings.Stage.Settings.Subtitle)));
        kids.Add(Row(Loc.Get(Strings.Stage.Settings.Visualizer), Loc.Get(Strings.Stage.Settings.VisualizerSub),
            Embed.Comp(static () => new StageVisualizerPicker()), RowGlyph(Tab.Appearance, "stageVisualizer")));
        kids.Add(Row(Loc.Get(Strings.Stage.Settings.Sensitivity), Loc.Get(Strings.Stage.Settings.SensitivitySub),
            Embed.Comp(static () => new StageSensitivitySlider()), RowGlyph(Tab.Appearance, "stageSensitivity")));
        kids.Add(Row(Loc.Get(Strings.Stage.Settings.LyricsOverlay), Loc.Get(Strings.Stage.Settings.LyricsOverlaySub),
            StageToggle(Platform.Keys.StageLyricsOverlay), RowGlyph(Tab.Appearance, "stageLyricsOverlay")));
        kids.Add(Row(Loc.Get(Strings.Stage.Settings.SyncOffset), Loc.Get(Strings.Stage.Settings.SyncOffsetSub),
            Embed.Comp(static () => new StageSyncSlider()), RowGlyph(Tab.Appearance, "stageSyncOffset")));
        kids.Add(Row(Loc.Get(Strings.Stage.Settings.Calm), Loc.Get(Strings.Stage.Settings.CalmSub),
            StageToggle(Platform.Keys.StageCalm), RowGlyph(Tab.Appearance, "stageCalm")));
```
and the helpers beside `LyricsBlurControl` / `NpvStylePicker` (`:661`, `:694`):
```csharp
    static string[] VisualizerLabels() =>
    [
        Loc.Get(Strings.Stage.Viz.Field), Loc.Get(Strings.Stage.Viz.Halo), Loc.Get(Strings.Stage.Viz.Horizon), Loc.Get(Strings.Stage.Viz.Matrix),
        Loc.Get(Strings.Stage.Viz.Aurora), Loc.Get(Strings.Stage.Viz.Spectrum), Loc.Get(Strings.Stage.Viz.Pulse), Loc.Get(Strings.Stage.Viz.Tape),
    ];
    /// <summary>The two toggles read `Platform.SettingsChanged` through `Toggle` as every Appearance toggle does; the bump wakes the stage.</summary>
    static Element StageToggle(SettingKey<bool> key) => Toggle(key, afterWrite: static _ => Prefs.Stage.Bump());

    sealed class StageVisualizerPicker : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_stageVisualizer.Value = Prefs.Stage.Visualizer());   // the gallery's pick lands here live
            return ComboBox.Create(VisualizerLabels(), s_stageVisualizer, width: 220f,
                onChange: static i => { if ((uint)i < (uint)Visualizer.Catalog.Count) Prefs.Stage.SetVisualizer(i); });
        }
    }
    sealed class StageSensitivitySlider : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_stageSensitivity.SetIfChanged(Prefs.Stage.Sensitivity()));
            return Slider.Create(s_stageSensitivity, static v => Prefs.Stage.SetSensitivity(v), s_stageSensitivityOptions, length: 180f);
        }
    }
    sealed class StageSyncSlider : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_stageSyncOffset.SetIfChanged(Prefs.Stage.SyncOffsetMs()));
            return Slider.Create(s_stageSyncOffset, static v =>
            {
                int ms = (int)MathF.Round(v / 10f) * 10;
                Prefs.Stage.SetSyncOffsetMs(ms);
                Playback.Audio.SetSpectrumOffsetMs(ms);
            }, s_stageSyncOptions, length: 180f);
        }
    }
```
(`Prefs.Stage.Set*` bumps `Prefs.Stage.Epoch`, which `SettingsPageView.Render` now reads — no separate `Bump()` of the page's own epoch is needed; `FloatSignal.SetIfChanged` keeps a slider mid-drag from fighting its own write.)

**Loc — `assets/loc/en-US.json`**: insert a new top-level object before `"tray": {` (`:3462`); nl/ko insert the same object (translated) before their `"person": {` (`:1175`). Keys → `Strings.Stage.*` (`Strings.Stage.Mode.Lyrics`, `Strings.Stage.NextFrom(context)`, `Strings.Stage.Viz.Field`, `Strings.Stage.VizSub.Field`, `Strings.Stage.Settings.Title`, `Strings.Stage.Diag.Fft`, …):

| key | en-US | nl | ko-KR |
|---|---|---|---|
| `stage.brand` | Now playing · full screen | Nu speelt · volledig scherm | 지금 재생 중 · 전체 화면 |
| `stage.mode.lyrics` | Lyrics | Songtekst | 가사 |
| `stage.mode.visualizer` | Visualizer | Visualizer | 시각화 |
| `stage.mode.queue` | Up next | Hierna | 다음 재생 |
| `stage.mode.artist` | Artist | Artiest | 아티스트 |
| `stage.exit` | Exit full screen | Volledig scherm afsluiten | 전체 화면 종료 |
| `stage.gallery` | Visualizer gallery and settings | Visualisatiegalerij en instellingen | 비주얼라이저 갤러리 및 설정 |
| `stage.galleryTitle` | Visualizer | Visualisatie | 비주얼라이저 |
| `stage.closePanel` | Close panel | Paneel sluiten | 패널 닫기 |
| `stage.settingsHeader` | Settings | Instellingen | 설정 |
| `stage.sensitivity` | Sensitivity | Gevoeligheid | 감도 |
| `stage.lyricsOverlay` | Lyrics over visualizer | Songtekst over visualisatie | 비주얼라이저 위에 가사 표시 |
| `stage.reduceMotion` | Reduce motion | Minder beweging | 동작 줄이기 |
| `stage.syncOffset` | Sync offset | Synchronisatie-offset | 동기화 오프셋 |
| `stage.upNext` | Up next | Hierna | 다음 재생 |
| `stage.nextFrom` | Next from {context} | Hierna uit {context} | {context}의 다음 곡 |
| `stage.playingFrom` | Playing from {kind} | Afspelen uit {kind} | {kind}에서 재생 중 |
| `stage.kind.album` / `playlist` / `artist` / `show` / `search` | album / playlist / artist / podcast / search | album / afspeellijst / artiest / podcast / zoekopdracht | 앨범 / 플레이리스트 / 아티스트 / 팟캐스트 / 검색 |
| `stage.syncedLyrics` | Synced lyrics | Gesynchroniseerde songtekst | 싱크 가사 |
| `stage.bpm` | {bpm} BPM | {bpm} BPM | {bpm} BPM |
| `stage.monthlyListeners` | {count} monthly listeners | {count} maandelijkse luisteraars | 월간 청취자 {count}명 |
| `stage.goToArtist` | Go to artist | Naar artiest | 아티스트로 이동 |
| `stage.credits` | Credits · {title} | Credits · {title} | 크레딧 · {title} |
| `stage.tip.title` | Pick your visualizer | Kies je visualizer | 시각화를 선택하세요 |
| `stage.tip.body` | Eight styles, all tinted from the album art. Choose one on the right. Wavee hides the controls after three seconds; move the mouse to bring them back. | Acht stijlen, allemaal gekleurd naar de albumhoes. Kies er rechts een. Wavee verbergt de bediening na drie seconden; beweeg de muis om die terug te halen. | 앨범 아트 색조를 입힌 여덟 가지 스타일입니다. 오른쪽에서 하나를 고르세요. Wavee는 3초 후 컨트롤을 숨기며, 마우스를 움직이면 다시 나타납니다. |
| `stage.tip.gotIt` | Got it | Begrepen | 확인 |
| `stage.viz.field` … `tape` | Field · Halo · Horizon · Matrix · Aurora · Spectrum · Pulse · Tape | Veld · Halo · Horizon · Matrix · Aurora · Spectrum · Puls · Tape | 필드 · 헤일로 · 호라이즌 · 매트릭스 · 오로라 · 스펙트럼 · 펄스 · 테이프 |
| `stage.vizSub.field` | Ambient · cover colours | Ambient · hoeskleuren | 앰비언트 · 커버 색상 |
| `stage.vizSub.halo` | Spectrum around the cover | Spectrum rond de hoes | 커버 주위의 스펙트럼 |
| `stage.vizSub.horizon` | Waveform from Spotify | Golfvorm van Spotify | Spotify의 파형 |
| `stage.vizSub.matrix` | Dot matrix · live | Puntmatrix · live | 도트 매트릭스 · 라이브 |
| `stage.vizSub.aurora` | Ribbons · low, mid, high | Linten · laag, midden, hoog | 리본 · 저음, 중음, 고음 |
| `stage.vizSub.spectrum` | 48 bars · peak caps | 48 balken · piekmarkers | 48 막대 · 피크 캡 |
| `stage.vizSub.pulse` | Beat-locked · calm | Op de beat · rustig | 비트 동기화 · 차분함 |
| `stage.vizSub.tape` | Reels · level meter | Spoelen · niveaumeter | 릴 · 레벨 미터 |
| `stage.source.precomputed` / `tempo` / `breath` | From Spotify / Tempo grid / Idle | Van Spotify / Temporaster / Rust | Spotify 데이터 / 템포 그리드 / 대기 |
| `stage.settings.title` | Full screen | Volledig scherm | 전체 화면 |
| `stage.settings.subtitle` | The full-screen Now Playing view and its visualizers | De weergave Nu speelt op volledig scherm en de visualizers | 전체 화면 지금 재생 중 보기와 시각화 |
| `stage.settings.visualizer` / `visualizerSub` | Visualizer / Which visualizer the full-screen view opens with | Visualisatie / Met welke visualisatie de volledig-schermweergave opent | 비주얼라이저 / 전체 화면 보기가 시작될 때 사용할 비주얼라이저 |
| `player.fullscreen` (in the existing `player` object) | Full screen | Volledig scherm | 전체 화면 |
| `stage.settings.sensitivity` / `sensitivitySub` | Sensitivity / How strongly the visualizers react to the music | Gevoeligheid / Hoe sterk de visualizers op de muziek reageren | 감도 / 시각화가 음악에 반응하는 강도 |
| `stage.settings.lyricsOverlay` / `lyricsOverlaySub` | Lyrics over the visualizer / Show the current line over the visualizer | Songtekst over de visualizer / Toon de huidige regel over de visualizer | 시각화 위에 가사 표시 / 시각화 위에 현재 가사 줄 표시 |
| `stage.settings.syncOffset` / `syncOffsetSub` | Sync offset / Shift the visualizers earlier or later to match what you hear — Bluetooth headphones usually need a positive value | Synchronisatie-offset / Verschuif de visualizers vroeger of later zodat ze kloppen met wat je hoort — Bluetooth-koptelefoons hebben meestal een positieve waarde nodig | 동기화 오프셋 / 들리는 소리에 맞게 시각화를 앞뒤로 조정합니다 — 블루투스 헤드폰은 보통 양수 값이 필요합니다 |
| `stage.settings.calm` / `calmSub` | Reduce motion / Calmer visualizers: smaller swings, no beat flashes | Minder beweging / Rustigere visualizers: kleinere uitslagen, geen beatflitsen | 동작 줄이기 / 더 차분한 시각화: 움직임 축소, 비트 플래시 없음 |
| `stage.diag.title` | Fullscreen & visualizer | Volledig scherm en visualizer | 전체 화면 및 시각화 |
| `stage.diag.mode` / `fft` / `leases` / `delay` / `source` / `publishes` | Mode / FFT per publish / Lease tier / Output delay / Data source / Spectrum publishes | Modus / FFT per publicatie / Lease-niveau / Uitvoervertraging / Gegevensbron / Spectrumpublicaties | 모드 / 게시당 FFT / 리스 단계 / 출력 지연 / 데이터 소스 / 스펙트럼 게시 수 |

(JSON nesting: `"stage": { "brand": …, "mode": { "lyrics": …, … }, "kind": { … }, "tip": { … }, "viz": { … }, "vizSub": { … }, "source": { … }, "settings": { … }, "diag": { … } }`. Only keys a code path reads are listed (V-D22 pruned `on/off`, `nowPlaying`, `key`, `follow/following`, `onTour`, `tickets`, `source.live`); the nl/ko wording follows the existing `player.videoExitFullScreen` (nl `:993` "Volledig scherm afsluiten") and `player.style.wmpShort` (nl `:1033` "Visualisatie", ko `:1033` "비주얼라이저"). Formatted keys (`{context}`, `{kind}`, `{bpm}`, `{count}`, `{title}`) generate a typed METHOD that already formats — `Strings.Stage.PlayingFrom(kind)` returns the final string; wrapping it in `Loc.Get` renders `[…]` (V-D15; the generator's `EmitLeaf`, `LocalizationKeysGenerator.cs:267-302`). DELETED in all three files (V-U39, with the Winamp "vis" row, D8): `player.opt.analyser` (en `:1861`, nl/ko `:1052`), `player.choice.spectrum` (en `:1900`, nl/ko `:1090`), `player.choice.oscilloscope` (en `:1901`, nl/ko `:1091`); the persisted registry value behind `Rail.PlayerPrefs.ChoiceSlug(preset, "vis")` has no reader after this and is left in place (no migration code — it is one orphaned string). The existing `player.*` keys the old stage used — `NothingPlaying`, `Shuffle`, `Repeat`, `Previous`, `Next`, `Mute`, `Unmute`, `Autoplay`, `AutoplayHint`, `QueueEmpty`, `SystemDefault`, `PlayingOn`, `CloseLyricsHint`, `ExpandLyrics` — stay in use.)

### 4.7 App CORE — NEW `src/apps/Wavee/Shell/Stage.cs` (replaces the whole file, WP-U1)

```csharp
// ── Shell/Stage.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// Stage.Mode/ModeRules, Stage.Aspect + Stage.Layout (the fullscreen allocator), Stage.Transport, Stage.Tone, Stage.Entry
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 420 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2, §4.7
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FULLSCREEN STAGE'S CORE — and nothing that paints:
//
//   • `Stage.Mode`      the four panes as a PERSISTED preference (Prefs.Stage); `ModeRules.Coerce` is the append-only int rule.
//   • `Stage.Layout`    the PURE allocator: four ASPECT classes with hysteresis (the prototypes' container queries,
//                       Main.dc.html:142-170) and every DIP the renderer lays out as a fraction of the viewport; the
//                       cover's two sizes (hero / thumb) that the one morphing node moves between.
//   • `Stage.Transport` the three predicates the transport card keeps (byte-identical to the previous stage).
//   • `Stage.Tone`      the accent cross-fade and scrim constants (alphas live here, colours in Design.StageInk — ch 00 §4.4;
//                       named Tone because the UI files alias `Ink = Wavee.Design.StageInk`, V-U1).
//   • `Stage.Entry`     who may enter (not over fullscreen video — an EMPTY stage is allowed, as the rail ⛶ does today) and
//                       the morph key the bar art and the hero share.
//
// Rules: `System`-only — no Element, no signal, no entity read — so Wavee.Tests drives the real arithmetic. No
// allocation after warm-up (P8); no LINQ, no closures, no async, no boxing (P9).

namespace Wavee;

public static partial class Stage
{
    // ── 1. the four modes ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the right-hand region shows. PERSISTED ints (Platform.Keys.StageMode) — append only.</summary>
    public enum Mode : byte { Lyrics = 0, Visualizer = 1, Queue = 2, Artist = 3 }

    public static class ModeRules
    {
        public const int Count = 4;
        /// <summary>A stored/hand-edited int → a real mode; anything else is Lyrics.</summary>
        public static int Coerce(int stored) => (uint)stored < (uint)Count ? stored : (int)Mode.Lyrics;
        /// <summary>Visualizer mode has no pane: the face takes the stage and the caption shows the lyric.</summary>
        public static bool ShowsPane(Mode m) => m != Mode.Visualizer;
        /// <summary>The gallery is a Visualizer-mode affordance: toggled from another mode it first switches the mode (the
        /// prototype's <c>togglePane</c>, Flagship.dc.html:555).</summary>
        public static (Mode Mode, bool Open) ToggleGallery(Mode current, bool open)
            => current == Mode.Visualizer ? (current, !open) : (Mode.Visualizer, true);
        /// <summary>The overlay caption shows only on the face, only when the user wants it, only with a timed line.</summary>
        public static bool ShowsCaption(Mode m, bool overlayOn, bool hasTimedLyrics) => m == Mode.Visualizer && overlayOn && hasTimedLyrics;
    }

    // ── 2. the allocator ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The four layout classes. Desktop is the 1920×1080 board; the others are the prototypes' container queries.</summary>
    public enum Aspect : byte { Desktop = 0, Ultrawide = 1, Portrait = 2, Compact = 3 }

    /// <summary>Every DIP the renderer lays out, resolved from (W, H). One structure, one <see cref="Aspect"/> flag: a
    /// call site never re-derives a breakpoint. Demotion is immediate; promotion needs the hysteresis reserve, so a
    /// drag across an edge flips once per crossing.</summary>
    public readonly record struct Layout(
        Aspect Aspect, float W, float H,
        float HeroArt, float ThumbArt, float PadX, float IdentityTop,
        float PaneX, float PaneRight, float PaneTop, float PaneBottom,
        float GalleryW, float GalleryH, float TransportH,
        bool ShowPane, bool ShowChips, bool ShowGallery, bool ShowVolume, bool IconOnlySelector)
    {
        // ── authored constants (the prototype's chrome, Flagship.dc.html:184-237) ──────────────────────────────────
        public const float TopBarH = 48f;
        public const float TransportDesktopH = 112f, TransportCompactH = 96f;
        public const float Pad = 24f;
        public const float NowPlayingCardX = 24f, NowPlayingCardY = 64f, NowPlayingCardW = 460f, NowPlayingCardH = 136f;
        public const float GalleryDesktopW = 444f, GalleryTop = 64f, GalleryBottom = 160f, GalleryRight = 24f;
        /// <summary>How far the face is inset on the right while the gallery pane is open (pane + 2 × 24).</summary>
        public const float GalleryInset = GalleryDesktopW + 2f * Pad;
        public const float HairlineH = 3f;
        public const float SelectorItemW = 128f, SelectorIconOnlyW = 44f;
        public const float ThumbDesktop = 96f, ThumbSmall = 64f;
        public const float HeroMin = 168f, HeroMax = 640f, SmallHeroMin = 96f, SmallHeroMax = 320f;
        /// <summary>The art is quantised to this grid so a resize pixel re-renders nothing (the previous stage's rule, kept).</summary>
        public const float ArtQuantum = 4f;

        // ── thresholds (§2.2) ──────────────────────────────────────────────────────────────────────────────────────
        /// <summary>Under this width the stage is Compact whatever the height (the previous stage's one threshold, kept).</summary>
        public const float WideEnterW = 600f;
        public const float PortraitEnter = 0.80f, PortraitLeave = 0.85f;
        public const float UltrawideEnter = 2.00f, UltrawideLeave = 1.95f;
        public const float CompactEnterH = 460f, CompactLeaveH = 484f;   // + the previous stage's FoldHysteresisH 24

        public static float Q4(float v) => MathF.Floor(v / ArtQuantum) * ArtQuantum;

        /// <summary>The class for (w, h) given the previous one (hysteresis). A degenerate size keeps the previous class.</summary>
        public static Aspect ClassOf(float w, float h, Aspect? previous)
        {
            if (w <= 0f || h <= 0f) return previous ?? Aspect.Desktop;
            if (w < WideEnterW) return Aspect.Compact;
            float ratio = w / h;
            bool wasCompact = previous == Aspect.Compact, wasPortrait = previous == Aspect.Portrait, wasUltra = previous == Aspect.Ultrawide;
            if (ratio >= 1f && (h <= CompactEnterH || (wasCompact && h < CompactLeaveH))) return Aspect.Compact;
            if (ratio <= PortraitEnter || (wasPortrait && ratio < PortraitLeave)) return Aspect.Portrait;
            if (ratio >= UltrawideEnter || (wasUltra && ratio > UltrawideLeave)) return Aspect.Ultrawide;
            return Aspect.Desktop;
        }

        public static Layout Seed(float w, float h) => Resolve(w, h, null);

        /// <summary>The live resolve. Every fraction is the prototype's (Main.dc.html:46 `--hw:min(28cqw, 82cqh − 340px)` and
        /// the three container queries at :142-170); the hero is clamped and quantised.</summary>
        public static Layout Resolve(float w, float h, Layout? previous)
        {
            w = MathF.Max(0f, w);
            h = MathF.Max(0f, h);
            var a = ClassOf(w, h, previous?.Aspect);
            switch (a)
            {
                case Aspect.Ultrawide:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.21f * w, 0.82f * h - 340f)), HeroMin, HeroMax);
                    float padX = Q4(0.05f * w);
                    return new Layout(a, w, h, hero, ThumbDesktop, padX, Q4(0.12f * h),
                        PaneX: padX + hero + Q4(0.045f * w), PaneRight: Q4(0.04f * w), PaneTop: 88f, PaneBottom: 168f,
                        GalleryW: Q4(0.19f * w), GalleryH: MathF.Max(0f, h - GalleryTop - GalleryBottom), TransportH: TransportDesktopH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: true, IconOnlySelector: false);
                }
                case Aspect.Portrait:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.14f * h, 0.26f * w)), SmallHeroMin, SmallHeroMax);
                    float padX = Q4(0.07f * w);
                    float identityTop = TopBarH + 44f;
                    return new Layout(a, w, h, hero, ThumbSmall, padX, identityTop,
                        PaneX: padX, PaneRight: padX, PaneTop: identityTop + hero + Pad, PaneBottom: 168f,
                        GalleryW: w, GalleryH: Q4(0.55f * h), TransportH: TransportDesktopH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: w >= 700f, IconOnlySelector: true);
                }
                case Aspect.Compact:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.62f * h, w - 420f)), SmallHeroMin, SmallHeroMax);
                    const float padX = 28f;
                    return new Layout(a, w, h, hero, ThumbSmall, padX, TopBarH + 28f,
                        PaneX: padX + hero + 28f, PaneRight: padX, PaneTop: TopBarH + 28f, PaneBottom: TransportCompactH + Pad,
                        GalleryW: 0f, GalleryH: 0f, TransportH: TransportCompactH,
                        ShowPane: false, ShowChips: false, ShowGallery: false, ShowVolume: false, IconOnlySelector: true);
                }
                default:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.28f * w, 0.82f * h - 340f)), HeroMin, HeroMax);
                    float padX = Q4(0.058f * w);
                    return new Layout(Aspect.Desktop, w, h, hero, ThumbDesktop, padX, Q4(0.122f * h),
                        PaneX: padX + hero + Q4(0.05f * w), PaneRight: Q4(0.05f * w), PaneTop: 88f, PaneBottom: 168f,
                        GalleryW: GalleryDesktopW, GalleryH: MathF.Max(0f, h - GalleryTop - GalleryBottom), TransportH: TransportDesktopH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: true, IconOnlySelector: false);
                }
            }
        }

        // ── derived reads the renderer uses ─────────────────────────────────────────────────────────────────────────

        /// <summary>The ONE cover node's size for a mode: the hero everywhere but the Visualizer thumb.</summary>
        public float CoverSize(Mode mode) => mode == Mode.Visualizer ? ThumbArt : HeroArt;
        public float CoverX(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardX + 20f : PadX;
        public float CoverY(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardY + 20f : IdentityTop;
        /// <summary>Portrait and Compact lay the identity out as a ROW (art left, title right of it); Desktop/Ultrawide stack it.</summary>
        public bool IdentityIsRow => Aspect is Aspect.Portrait or Aspect.Compact;
        /// <summary>The title column: beside the thumb in Visualizer mode; beside the hero on the small classes (V-U23); under it otherwise.</summary>
        public float TitleX(Mode mode) => mode == Mode.Visualizer ? CoverX(mode) + ThumbArt + 16f : IdentityIsRow ? PadX + HeroArt + Pad : PadX;
        public float TitleY(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardY + 18f : IdentityIsRow ? IdentityTop : IdentityTop + HeroArt + Pad;
        public float TitleW(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardW - (ThumbArt + 56f)
            : IdentityIsRow ? MathF.Max(160f, W - TitleX(mode) - PadX) : MathF.Max(HeroArt, 240f);
        /// <summary>Compact: the transport card sits RIGHT of the art, not under it (V-U23). Everywhere else it spans the width at Pad.</summary>
        public float TransportLeft => Aspect == Aspect.Compact ? PadX + HeroArt + Pad : Pad;
        /// <summary>The face's right inset while the gallery is open (the prototype's <c>.pane .stg { right: 492px }</c>) — THIS
        /// layout's gallery width plus the two gutters (Ultrawide's docked column is 19 % of W, not 444 — V-U45).</summary>
        public float FaceRight(bool galleryOpen) => galleryOpen && ShowGallery && Aspect != Aspect.Portrait ? GalleryW + 2f * Pad : 0f;
        /// <summary>The pane's width from its two edges; never negative.</summary>
        public float PaneW => MathF.Max(0f, W - PaneX - PaneRight);
        public float PaneH => MathF.Max(0f, H - PaneTop - PaneBottom);

        /// <summary>Title type per aspect and mode: the hero reads TitleLarge 40/52, the thumb Subtitle 20/28; small
        /// classes step down (Title 28/36 · 24/30).</summary>
        public (float Size, float Line) TitleFont(Mode mode)
        {
            if (mode == Mode.Visualizer) return (20f, 28f);
            return Aspect switch { Aspect.Portrait => (28f, 36f), Aspect.Compact => (24f, 30f), _ => (40f, 52f) };
        }
        public (float Size, float Line) MetaFont(Mode mode)
            => mode != Mode.Visualizer && Aspect is Aspect.Desktop or Aspect.Ultrawide ? (18f, 24f) : (14f, 20f);

        /// <summary>A monotone "how much is on screen" score for the narrowing-never-adds test: the five affordance flags and the
        /// transport height. The hero is EXCLUDED on purpose — it is a per-class formula (0.28·W vs Ultrawide's 0.21·W) and
        /// legitimately shrinks when a wider window promotes to Ultrawide (V-U24).</summary>
        public int Richness => (ShowPane ? 1 : 0) + (ShowChips ? 1 : 0) + (ShowGallery ? 1 : 0) + (ShowVolume ? 1 : 0)
            + (IconOnlySelector ? 0 : 1) + (int)(TransportH * 0.01f);
    }

    // ── 3. what the transport may do (unchanged from the previous stage) ────────────────────────────────────────────

    public static class Transport
    {
        public static bool PrimaryEnabled(bool hasTrack, bool loading) => hasTrack && !loading;
        /// <summary>The format chip names the PLAYING stream or nothing: no published format, or another Connect device
        /// is active, ⇒ no chip. Silence is the correct answer, not a fallback.</summary>
        public static bool ShowsQualityBadge(bool hasFormat, bool remoteActive) => hasFormat && !remoteActive;
        /// <summary>The identity title: the track's own title, or "nothing playing" when it is absent or still EQUALS the
        /// uri (a placeholder row before its metadata landed — never surface a raw uri).</summary>
        public static bool UsesTitle(string? title, string? uri) => title is { Length: > 0 } && title != uri;
    }

    // ── 4. tone arithmetic (alphas and the cross-fade; the colours are Design.StageInk's) ────────────────────────────

    public static class Tone
    {
        /// <summary>The accent cross-fade on a track change (the prototype's <c>transition: --acc 1s</c>, tightened to the
        /// Fluent "slow" neighbourhood so a skip-skip-skip never lags the art).</summary>
        public const float CrossFadeMs = 600f;
        /// <summary>The scrim over the backdrop: deep under lyrics/queue/artist, light under a face.</summary>
        public const float ScrimA = 0.56f, ScrimVisualizerA = 0.22f;
        /// <summary>The bottom smoke under the face so the transport card reads (prototype <c>.smk</c>).</summary>
        public const float SmokeH = 440f, SmokeA = 0.60f;
        /// <summary>The base Field's opacity: breathing under a pane, near-full under a face.</summary>
        public const float BaseFieldA = 0.62f, BaseFieldBreathA = 0.20f, BaseFieldVisualizerA = 0.90f;
        /// <summary>The LINEAR cross-fade position for a change that began <paramref name="elapsedMs"/> ago: 0 at the change,
        /// 1 at <see cref="CrossFadeMs"/> and after. The clock lerps from the colour CAPTURED at the change, so the fade
        /// completes in exactly CrossFadeMs whatever the tick rate (an exponential step never reaches the target — V-U17).</summary>
        public static float Progress(float elapsedMs) => elapsedMs <= 0f ? 0f : MathF.Min(1f, elapsedMs / CrossFadeMs);
    }

    // ── 5. entry ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Entry
    {
        /// <summary>The shared-element key the player bar's art and the stage hero both carry (an IMAGE node — the
        /// engine captures only image nodes, ConnectedAnimation.cs:246-250).</summary>
        public const string MorphKey = "stage:art";
        /// <summary>Never over a fullscreen VIDEO (F11 owns that). Nothing playing is NOT a bar: the rail ⛶ opens an empty
        /// stage today (Rail.UI.cs:253-255, "a track with no lyrics still opens a full stage") and so does every door here (V-U53).</summary>
        public static bool CanEnter(bool videoFullscreen) => !videoFullscreen;
    }
}
```

### 4.8 App CORE — NEW `src/apps/Wavee/Shell/Visualizer.cs` (WP-U3)

```csharp
// ── Shell/Visualizer.cs ────────────────────────────────────────────────────────────────────────────────────────────
// Visualizer.Kind/Need/Tier/Source, Catalog, Demand, Bands, the eight face models (Field, Halo, Horizon, Matrix, Aurora,
// Spectrum, Pulse, Tape) and the ONE alloc-free Model.Tick the clock folds every 30 Hz tick through
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 520 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2.5-§2.10, §4.8
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT A FACE IS MADE OF. The engine publishes 48 log bands in dB (SpectrumAnalyzer) ~60×/s; the UI PULLS them at
// 30 Hz into `Model.Db` and this file turns them into every scalar a face binds: `Bands.Unit` (dB → 0..1 with the
// sensitivity and the calm gain), `Bands.Follow` (the prototype's 0.55/0.10 attack/release), `Bands.Peaks` (hold 10
// ticks, fall 0.018), the low/mid/high averages, the Halo/Matrix/Spectrum index maps, the Aurora ribbons and the
// Horizon window written into preallocated series buffers, the Pulse beat phase (TrackBeats → tempo grid), the Tape's
// area-conserving reels, the Field's breath. Every constant is the prototype's (Flagship.dc.html:472-523, 568-588;
// Visualizers.dc.html:235-320) and is named here ONCE.
//
// NOTHING IS FAKED. Without a live spectrum the band arrays DECAY to the floor (Halo/Matrix/Spectrum rest); the
// Horizon reads the precomputed kind-237 bands; Pulse reads the beat grid or the tempo grid; Field and Tape breathe
// on the precomputed level when there is one and rest otherwise. `Source` says which, every tick, for the gallery
// caption and the diagnostics card.
//
// Rules: `System`-only; the model owns its arrays (allocated once in the constructor) and `Tick` allocates nothing
// (P8); no LINQ, no closures, no async, no boxing (P9). `public` because Wavee.Tests is a ProjectReference.

namespace Wavee;

public static partial class Visualizer
{
    // ── 1. the vocabulary ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The eight faces. PERSISTED ints (Platform.Keys.StageVisualizer) — append only.</summary>
    public enum Kind : byte { Field = 0, Halo = 1, Horizon = 2, Matrix = 3, Aurora = 4, Spectrum = 5, Pulse = 6, Tape = 7 }

    /// <summary>What a face needs to move.</summary>
    public enum Need : byte { None = 0, Breath = 1, Spectrum = 2, Precomputed = 3, Beats = 4 }

    /// <summary>The engine lease a face holds while live (§2.5).</summary>
    public enum Tier : byte { None = 0, Level = 1, Spectrum = 2 }

    /// <summary>Where this tick's motion came from (the gallery caption, the diagnostics card).</summary>
    public enum Source : byte { Breath = 0, Live = 1, Precomputed = 2, TempoGrid = 3 }

    public static class Catalog
    {
        public const int Count = 8;
        /// <summary>A stored/hand-edited int → a real kind; anything else is Horizon (the board's default).</summary>
        public static int Coerce(int stored) => (uint)stored < (uint)Count ? stored : (int)Kind.Horizon;
        public static Need NeedsOf(Kind k) => k switch
        {
            Kind.Field or Kind.Tape => Need.Breath,
            Kind.Halo or Kind.Matrix or Kind.Aurora or Kind.Spectrum => Need.Spectrum,
            Kind.Horizon => Need.Precomputed,
            Kind.Pulse => Need.Beats,
            _ => Need.None,
        };
        /// <summary>Horizon and Aurora draw through SeriesEl; everything else is bound BoxEls.</summary>
        public static bool UsesSeries(Kind k) => k is Kind.Horizon or Kind.Aurora;
    }

    // ── 2. demand ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Demand
    {
        /// <summary>The lease tier for what is VISIBLE (§2.5): outside Visualizer mode only the base Field's breath shows, so the
        /// level tier is enough whatever face is selected (V-U55). Alt-tab is deliberately NOT an input; occlusion and
        /// reduced motion are.</summary>
        public static Tier For(Kind kind, bool visualizerMode, bool stageUp, bool playing, bool ownerUs, bool audioSupported, bool occluded, bool reduced)
        {
            if (!stageUp || !playing || !ownerUs || !audioSupported || occluded || reduced) return Tier.None;
            if (!visualizerMode) return Tier.Level;
            return Catalog.NeedsOf(kind) switch { Need.Spectrum => Tier.Spectrum, Need.Breath => Tier.Level, _ => Tier.None };
        }

        /// <summary>Does the 30 Hz clock run? While the stage is up and not occluded/reduced, and either playing or
        /// something is still settling (a release tail, the reels coasting).</summary>
        public static bool Ticks(bool stageUp, bool playing, bool settled, bool occluded, bool reduced)
            => stageUp && !occluded && !reduced && (playing || !settled);
    }

    // ── 3. bands ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Bands
    {
        public const int Count = 48;                       // = SpectrumAnalyzer.DefaultBandCount
        public const float FloorDb = -60f, CeilingDb = -6f;
        public const float MinSensitivity = 0.3f, MaxSensitivity = 1.5f, CalmGain = 0.55f;
        public const float Attack = 0.55f, CalmAttack = 0.25f, Release = 0.10f;
        public const int PeakHoldTicks = 10;
        public const float PeakFall = 0.018f;
        /// <summary>Auto-gain: a slow running maximum of the frame max; the frame is normalised by max(agc, AgcFloor).</summary>
        public const float AgcDecay = 0.995f, AgcFloor = 0.40f, AgcMaxGain = 2f;

        public static float ClampSensitivity(float v) => float.IsFinite(v) ? Math.Clamp(v, MinSensitivity, MaxSensitivity) : 1f;

        /// <summary>dB → 0..1 through the floor/ceiling window. RAW: the AGC and the user gain are applied AFTER (V-U32).</summary>
        public static float Unit(float db) => MathF.Min(1f, MathF.Max(0f, (db - FloorDb) / (CeilingDb - FloorDb)));

        /// <summary>The user's gain on an AGC-normalised value: sensitivity (0.3–1.5) × the calm reduction.</summary>
        public static float Gain(float sensitivity, bool calm) => sensitivity * (calm ? CalmGain : 1f);

        /// <summary>One AGC step: returns the gain to apply to this frame's unit values.</summary>
        public static float Agc(ref float agc, float frameMax)
        {
            agc = MathF.Max(frameMax, agc * AgcDecay);
            return MathF.Min(AgcMaxGain, 1f / MathF.Max(agc, AgcFloor));
        }

        /// <summary>The prototype's follower: fast up (0.55; 0.25 calm), slow down (0.10).</summary>
        public static void Follow(ReadOnlySpan<float> target, Span<float> level, bool calm)
        {
            float atk = calm ? CalmAttack : Attack;
            int n = Math.Min(target.Length, level.Length);
            for (int i = 0; i < n; i++)
            {
                float v = level[i], t = target[i];
                level[i] = v + (t - v) * (t > v ? atk : Release);
            }
        }

        /// <summary>Release every band toward 0 (no live frame this tick).</summary>
        public static void Decay(Span<float> level)
        {
            for (int i = 0; i < level.Length; i++) level[i] -= level[i] * Release;
        }

        /// <summary>Peak caps: hold 10 ticks at the band's level, then fall 0.018 per tick, never below the band.</summary>
        public static void Peaks(ReadOnlySpan<float> level, Span<float> peak, Span<int> hold)
        {
            int n = Math.Min(level.Length, Math.Min(peak.Length, hold.Length));
            for (int i = 0; i < n; i++)
            {
                if (level[i] >= peak[i]) { peak[i] = level[i]; hold[i] = PeakHoldTicks; }
                else if (hold[i] > 0) hold[i]--;
                else peak[i] = MathF.Max(level[i], peak[i] - PeakFall);
            }
        }

        public static float Average(ReadOnlySpan<float> v, int from, int to)
        {
            to = Math.Min(to, v.Length);
            if (to <= from) return 0f;
            float s = 0f;
            for (int i = from; i < to; i++) s += v[i];
            return s / (to - from);
        }

        public static bool Settled(ReadOnlySpan<float> level, float eps = 0.005f)
        {
            for (int i = 0; i < level.Length; i++) if (level[i] > eps) return false;
            return true;
        }
    }

    // ── 4. the faces' arithmetic (every constant from the prototype) ───────────────────────────────────────────────

    public static class Halo
    {
        public const int Bars = 72;
        /// <summary>Bar j reads band round(m/35 · 47 · 0.85), m mirrored about the top (Flagship.dc.html:572).</summary>
        public static int BandOf(int j) { int m = j < 36 ? j : 71 - j; return (int)MathF.Round(m / 35f * (Bands.Count - 1) * 0.85f); }
        public static float Scale(float level) => 0.1f + 0.9f * level;
        public static float AngleDeg(int j) => j * 360f / Bars;
        public static float GlowOpacity(float low) => 0.16f + low * 0.4f;
        /// <summary>The bar colour mixes c2→c1 by |sin(πj/72)| (HALO_C).</summary>
        public static float MixOf(int j) => MathF.Abs(MathF.Sin(j / (float)Bars * MathF.PI));
    }

    public static class Matrix
    {
        public const int Columns = 32, Rows = 12;
        public static int BandOf(int c) => (int)MathF.Floor(c / 31f * (Bands.Count - 1));
        public static int LitRows(float level) => Math.Clamp((int)MathF.Round(level * Rows), 0, Rows);
        public static int PeakRow(float peak) => Math.Clamp((int)MathF.Ceiling(peak * Rows), 1, Rows);
    }

    public static class Spectrum
    {
        public const int Bars = Bands.Count;
        public const float FloorScale = 0.02f;
        public static float Scale(float level) => MathF.Max(FloorScale, level);
    }

    public static class Aurora
    {
        public const int Points = 65;
        /// <summary>One ribbon as 0..1 heights (the prototype's SVG y, 300 tall: height = (300 − y)/300).</summary>
        public static void Ribbon(Span<float> into, float ampFrac, float f, float speed, float phase, float baseFrac, float t)
        {
            for (int k = 0; k < into.Length; k++)
            {
                float u = k / (float)(into.Length - 1) * MathF.Tau;
                float wave = MathF.Sin(u * f + t * speed + phase) * 0.6f + MathF.Sin(u * f * 2.3f - t * speed * 0.7f + phase * 2f) * 0.4f;
                into[k] = Math.Clamp(1f - baseFrac + ampFrac * wave, 0f, 1f);
            }
        }
        public static void Fill(Span<float> low, Span<float> mid, Span<float> high, float l, float m, float h, float t)
        {
            Ribbon(low, (30f + 120f * l) / 300f, 1.2f, 0.6f, 0f, 210f / 300f, t);
            Ribbon(mid, (20f + 90f * m) / 300f, 1.8f, 0.9f, 1.7f, 180f / 300f, t);
            Ribbon(high, (12f + 70f * h) / 300f, 2.6f, 1.3f, 3.1f, 160f / 300f, t);
        }
    }

    public static class Horizon
    {
        public const int Points = 181;
        public const float SpanMs = 12_000f;
        /// <summary>The SeriesEl amplitude the prototype used (M·0.92).</summary>
        public const float Amplitude = 0.92f;
        /// <summary>The 12 s window about the playhead, three mirrored series: Σ·0.55, (mid+high)·0.6, high·0.75. Indexed by
        /// the track's duration (<see cref="WaveformBands.IndexAt"/>); outside [0, duration) the series is 0.</summary>
        public static void Fill(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs, Span<float> low, Span<float> mid, Span<float> high)
        {
            if (samples.IsEmpty || durationMs <= 0) { low.Clear(); mid.Clear(); high.Clear(); return; }
            int n = samples.Length;
            for (int k = 0; k < Points; k++)
            {
                float t = positionMs - SpanMs * 0.5f + SpanMs * k / (Points - 1);
                if (t < 0f || t >= durationMs) { low[k] = mid[k] = high[k] = 0f; continue; }
                var s = samples[WaveformBands.IndexAt((long)t, durationMs, n)];
                float a = s.Low / 255f, b = s.Mid / 255f, c = s.High / 255f;
                low[k] = MathF.Min(1f, (a + b + c) * 0.55f);
                mid[k] = MathF.Min(1f, (b + c) * 0.6f);
                high[k] = MathF.Min(1f, c * 0.75f);
            }
        }
    }

    public static class Pulse
    {
        public const int Rings = 4;
        public static float Kick(float phase) => MathF.Exp(-6f * phase);
        /// <summary>Ring r's 0..1 progress: four rings a beat apart, each four beats long (Flagship.dc.html:291, 587).</summary>
        public static float RingProgress(int beatIndex, float phase, int ring) => (((beatIndex + ring) & 3) + phase) / Rings;
        public static float RingOpacity(float progress) => 0.6f * (1f - progress);
        public static float RingScale(float progress) => 1f + 1.3f * progress;
        public static float CoverScale(float kick) => 1f + kick * 0.025f;
        public static float GlowOpacity(float low) => 0.14f + low * 0.3f;
    }

    public static class Tape
    {
        public const float R0 = 34f, R1 = 128f, Flange = 140f, HubR = 30f;
        /// <summary>Tape speed in prototype units; the hub turns Speed/radius rad/s.</summary>
        public const float Speed = 70f, SpeedEase = 0.08f;
        public const int MeterCells = 20;
        /// <summary>Area-conserving packs: r² trades linearly with progress.</summary>
        public static (float Left, float Right) Radii(float progress)
        {
            float p = Math.Clamp(progress, 0f, 1f);
            float a = R0 * R0, b = R1 * R1 - R0 * R0;
            return (MathF.Sqrt(a + b * (1f - p)), MathF.Sqrt(a + b * p));
        }
        public static float AngleStepDeg(float speed, float radius, float dtSec) => speed / MathF.Max(radius, 1f) * 57.29578f * dtSec;
        public static int Lit(float level) => Math.Min(MeterCells, (int)MathF.Round(level * MeterCells * 1.6f));
        /// <summary>0 blue, 1 green, 2 orange (cells 0-11, 12-16, 17-19).</summary>
        public static int Zone(int cell) => cell < 12 ? 0 : cell < 17 ? 1 : 2;
    }

    public static class Field
    {
        public const int Blobs = 4;
        /// <summary>Keyframe periods per blob (s) and the drift targets as fractions of W/H plus the end scale.</summary>
        public static readonly float[] PeriodSec = [30f, 36f, 33f, 28f];
        public static readonly (float Dx, float Dy, float Scale)[] Drift = [(0.16f, 0.12f, 1.15f), (-0.14f, 0.14f, 0.9f), (0.18f, -0.12f, 1.1f), (-0.15f, -0.13f, 1.2f)];
        public static float FaceOpacity(float low) => 0.75f + low * 0.25f;
        public static float BaseOpacity(float low, bool visualizer) => visualizer ? Stage.Tone.BaseFieldVisualizerA : Stage.Tone.BaseFieldA + low * Stage.Tone.BaseFieldBreathA;
    }

    // ── 5. the fold ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What one tick reads. The band/beat spans travel beside it (a readonly record struct cannot hold a span).</summary>
    public readonly record struct Input(
        bool Playing, bool Calm, float Sensitivity, bool HaveLive, bool Muted, float Rms,
        long PositionMs, long DurationMs, bool HaveBands, bool HaveBeats, ushort TempoX10, bool Visualizer);

    /// <summary>Every scalar the slab writes after a tick.</summary>
    public readonly record struct Frame(
        float Low, float Mid, float High, float Level, float Kick, int BeatIndex, float BeatPhase,
        float FaceFieldOp, float BaseFieldOp, float GlowOp, float BeatScale,
        float ReelL, float ReelR, float AngleL, float AngleR, int MeterLit, Source Source);

    /// <summary>THE model: owns every preallocated array; <see cref="Tick"/> allocates nothing.</summary>
    public sealed class Model
    {
        public readonly float[] Target = new float[Bands.Count], Level = new float[Bands.Count], Peak = new float[Bands.Count];
        readonly int[] _hold = new int[Bands.Count];
        public readonly float[] HorizonLow = new float[Horizon.Points], HorizonMid = new float[Horizon.Points], HorizonHigh = new float[Horizon.Points];
        public readonly float[] AuroraLow = new float[Aurora.Points], AuroraMid = new float[Aurora.Points], AuroraHigh = new float[Aurora.Points];
        float _t, _agc = Bands.AgcFloor, _speed, _angleL, _angleR, _kick;
        public bool IsSettled { get; private set; } = true;

        /// <summary><paramref name="liveDb"/>: the 48 dB bands when <see cref="Input.HaveLive"/> (else ignored);
        /// <paramref name="bands"/>: the kind-237 samples or empty; <paramref name="beats"/>: the TrackBeats payload or empty.
        /// <see cref="Input.Rms"/> is the PRE-gain window RMS under a spectrum lease (SpectrumInfo.WindowRms), the level tap's
        /// RMS under a level lease.</summary>
        public Frame Tick(in Input input, ReadOnlySpan<float> liveDb, ReadOnlySpan<WaveSample> bands, ReadOnlySpan<uint> beats, float dtSec)
        {
            _t += dtSec;
            bool live = input.HaveLive && !input.Muted && liveDb.Length >= Bands.Count;
            float userGain = Bands.Gain(Bands.ClampSensitivity(input.Sensitivity), input.Calm);

            // 1. bands: live → unit → AGC → the user's gain → follow; otherwise release toward the floor (nothing is synthesised)
            if (live && input.Playing)
            {
                float frameMax = 0f;
                for (int i = 0; i < Bands.Count; i++) { float u = Bands.Unit(liveDb[i]); Target[i] = u; if (u > frameMax) frameMax = u; }
                float gain = Bands.Agc(ref _agc, frameMax) * userGain;          // AGC first, then sensitivity/calm (V-U32)
                for (int i = 0; i < Bands.Count; i++) Target[i] = MathF.Min(1f, Target[i] * gain);
                Bands.Follow(Target, Level, input.Calm);
            }
            else Bands.Decay(Level);
            Bands.Peaks(Level, Peak, _hold);

            // 2. the three energies and the level: live from the bands, else the precomputed waveform at the playhead
            float low, mid, high, level;
            Source source;
            if (live && input.Playing)
            {
                low = Bands.Average(Level, 0, 8); mid = Bands.Average(Level, 8, 28); high = Bands.Average(Level, 28, 48);
                level = MathF.Min(1f, input.Rms * 3.5f);
                source = Source.Live;
            }
            else if (input.HaveBands && input.Playing && WaveformBands.At(bands, input.PositionMs, input.DurationMs, out float bl, out float bm, out float bh))
            {
                low = MathF.Min(1f, bl * userGain); mid = MathF.Min(1f, bm * userGain); high = MathF.Min(1f, bh * userGain);
                level = (bl + bm + bh) / 3f;
                source = Source.Precomputed;
            }
            else { low = mid = high = level = 0f; source = Source.Breath; }

            // 3. the beat: the grid, else the tempo, else nothing
            int beatIndex = 0; float phase = 0f; bool hasBeat;
            if (input.HaveBeats && BeatGrid.Phase(beats, input.PositionMs, out beatIndex, out phase, out _)) { hasBeat = true; if (beatIndex < 0) beatIndex = 0; }
            else if (BeatGrid.TempoPhase(input.TempoX10, input.PositionMs, out phase, out float periodMs)) { hasBeat = true; beatIndex = periodMs > 0f ? (int)(input.PositionMs / periodMs) : 0; if (source == Source.Breath) source = Source.TempoGrid; }
            else hasBeat = false;
            float kickTarget = hasBeat && input.Playing ? Pulse.Kick(phase) * (input.Calm ? 0.3f : 1f) : 0f;
            _kick = kickTarget > _kick ? kickTarget : _kick + (kickTarget - _kick) * 0.35f;

            // 4. series: Horizon from the payload (empty ⇒ flat), Aurora always drifting
            Horizon.Fill(bands, input.PositionMs, input.DurationMs, HorizonLow, HorizonMid, HorizonHigh);
            Aurora.Fill(AuroraLow, AuroraMid, AuroraHigh, low, mid, high, _t);

            // 5. tape: eased speed, area-conserving radii, hubs turning at Speed/radius
            _speed += ((input.Playing ? Tape.Speed : 0f) - _speed) * Tape.SpeedEase;
            if (_speed < 0.05f) _speed = 0f;
            float progress = input.DurationMs > 0 ? input.PositionMs / (float)input.DurationMs : 0f;
            var (rL, rR) = Tape.Radii(progress);
            _angleL = (_angleL + Tape.AngleStepDeg(_speed, rL, dtSec)) % 360f;
            _angleR = (_angleR + Tape.AngleStepDeg(_speed, rR, dtSec)) % 360f;

            IsSettled = !input.Playing && Bands.Settled(Level) && _kick < 0.002f && _speed == 0f;
            return new Frame(low, mid, high, level, _kick, beatIndex, phase,
                Field.FaceOpacity(low), Field.BaseOpacity(low, input.Visualizer), Halo.GlowOpacity(low), Pulse.CoverScale(_kick),
                rL, rR, _angleL, _angleR, Tape.Lit(level), source);
        }
    }

    /// <summary>The clock's cadence — Design.Cadence.PluggedLoopHz (30), restated so this file stays System-only; a test pins the two.</summary>
    public const int TickHz = 30;
    public const float TickMs = 1000f / TickHz;
    public const float DtMinSec = 0.001f, DtMaxSec = 0.080f;
    public static float DeltaSec(long lastTickMs, long nowMs)
        => lastTickMs == 0L ? TickMs / 1000f : Math.Clamp((nowMs - lastTickMs) / 1000f, DtMinSec, DtMaxSec);
}
```

### 4.9 App UI — NEW `src/apps/Wavee/Shell/Visualizer.UI.cs` (WP-U4)

```csharp
// ── Shell/Visualizer.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Slab, SeriesSource, Palette, Clock (30 Hz, leases, demands), the eight faces (stage + preview scale), Gallery
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 1100 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2.10, §3.1, §4.9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE DECK PATTERN, ON THE STAGE. ONE `Clock` component ticks at 30 Hz (`UseInterval`, auto-paused when parked), pulls
// the engine's bands (`Playback.Audio.CopySpectrum`), folds them through the CORE `Visualizer.Model` and writes the
// `Slab` inside ONE `Runtime.Batch` — FloatSignals for scalars, `SeriesSource.Version` bumps for the six series. Every
// face is built ONCE and binds `Transform`/`Opacity`/`Fill` props over the slab (compositor-only writes, zero
// re-render); Horizon and Aurora bind `SeriesEl.Samples` over a `SeriesSource`. A preview is the SAME face at a smaller
// size with fewer parts, bound to the SAME slab — eight previews cost nodes, never analysis. Nothing here decides: the
// tick's inputs and every constant come from `Visualizer` (CORE) and `Stage` (CORE).

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    // ══ 1. THE SLAB ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A reusable sample buffer + a version the bound <c>SeriesEl.Samples</c> thunk reads (the engine's
    /// SeriesSamples contract: equal by array identity, count and version).</summary>
    public sealed class SeriesSource(int capacity)
    {
        public readonly float[] Buffer = new float[capacity];
        public readonly Signal<uint> Version = new(0u);
        public int Count = capacity;
        /// <summary>The bound view — reading <see cref="Version"/>.Value is what subscribes the bind.</summary>
        public SeriesSamples Current => new(Buffer, Count, Version.Value);
        public void Publish() => Version.Value = Version.Peek() + 1;
    }

    /// <summary>Every signal a face can bind. One per stage; the gallery previews share it.</summary>
    public sealed class Slab
    {
        public readonly FloatSignal[] Bands = Make(Visualizer.Bands.Count), Peaks = Make(Visualizer.Bands.Count);
        public readonly FloatSignal Low = new(0f), Mid = new(0f), High = new(0f), Level = new(0f), Kick = new(0f), BeatPhase = new(0f);
        public readonly FloatSignal FaceFieldOp = new(0.75f), BaseFieldOp = new(Stage.Tone.BaseFieldA), GlowOp = new(0.16f), BeatScale = new(1f);
        public readonly FloatSignal ReelL = new(Visualizer.Tape.R1), ReelR = new(Visualizer.Tape.R0), AngleL = new(0f), AngleR = new(0f);
        public readonly FloatSignal[] Rings = Make(Pulse.Rings);
        public readonly Signal<int> BeatIndex = new(0), MeterLit = new(0);
        public readonly Signal<Source> Source = new(Visualizer.Source.Breath);
        public readonly SeriesSource HorizonLow = new(Horizon.Points), HorizonMid = new(Horizon.Points), HorizonHigh = new(Horizon.Points);
        public readonly SeriesSource AuroraLow = new(Aurora.Points), AuroraMid = new(Aurora.Points), AuroraHigh = new(Aurora.Points);
        /// <summary>The accent cross-fade's live value (Stage.Tone.CrossFadeMs) — the hairline, the ring strokes, the glow tint.</summary>
        public readonly Signal<ColorF> Accent = new(ColorF.FromRgba(255, 158, 196));
        /// <summary>Diagnostics (read by Screens/Diagnostics.UI.cs's card).</summary>
        public float LastFftMs; public long LastAlignFrames, LastSequence; public Tier LastTier;

        static FloatSignal[] Make(int n)
        {
            var a = new FloatSignal[n];
            for (int i = 0; i < n; i++) a[i] = new FloatSignal(0f);
            return a;
        }
    }

    /// <summary>The colours a face paints with, derived once per (cover, Tok.Epoch) in Stage.UI.cs and published on
    /// <see cref="Stage.StageCtx.Palette"/>: the accent, a complementary partner, and the four blob tints from the cover's
    /// scheme (<c>Wavee.Scheme</c>, Entities/Palette.cs:67-72 — ARGB uints, converted by <c>Design.Palette.ToColor</c>, Design.cs:677).</summary>
    public readonly record struct Palette(ColorF Accent, ColorF C1, ColorF C2, ColorF F1, ColorF F2, ColorF F3, ColorF F4)
    {
        public static Palette From(ColorF accent, Scheme? scheme)
        {
            var (h, s, v) = accent.ToHsv();
            ColorF c2 = ColorF.FromHsv((h + 150f) % 360f, MathF.Min(1f, s * 0.9f + 0.1f), MathF.Min(1f, v * 0.95f + 0.05f));
            if (scheme is { IsEmpty: false } sc)
                return new Palette(accent, accent, c2, Design.Palette.ToColor(sc.BackgroundBase), Design.Palette.ToColor(sc.BackgroundTintedBase),
                                   Design.Palette.ToColor(sc.TextBrightAccent), Design.Palette.ToColor(sc.TextSubdued));
            ColorF dim = ColorF.FromHsv(h, s * 0.6f, v * 0.35f), deep = ColorF.FromHsv((h + 40f) % 360f, s * 0.7f, v * 0.25f);
            return new Palette(accent, accent, c2, dim, accent, c2, deep);
        }
    }

    // ══ 2. THE CLOCK ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Mounted ONCE by Stage.UI.cs's SurfaceCore (zero-size), inside its <c>Ctx.Provide(Stage.StageContext, …)</c>.
    /// Holds the lease the VISIBLE consumer needs, demands the precomputed edges for the current track, and folds one
    /// <see cref="Model.Tick"/> per 30 Hz tick into the slab. Every preference it needs arrives as a SIGNAL on the context
    /// (SurfaceCore's one epoch effect writes them — O8): the tick Peeks, never reads the registry.</summary>
    public sealed class Clock : Component
    {
        readonly Model _model = new();
        readonly float[] _db = new float[Bands.Count];
        readonly Action _tick, _tickCore, _write;
        readonly Func<Action?> _lease;
        readonly Signal<bool> _settled = new(true);   // the deck's mirror (Deck.UI.cs:233, :378-379): the gate reads a SIGNAL, never _model.IsSettled
        Stage.StageCtx? _ctx;
        InputHooks? _hooks;                           // captured in Render (V-U42), read by the lease effect
        Deck.PositionInterpolator _pos;
        bool _anchored, _advancing, _run;
        long _lastTickMs;
        int _lastReportMs = int.MinValue;
        Frame _pending;
        bool _haveLive;
        ColorF _accentTarget, _fadeFrom;
        long _fadeStartMs = -1;                       // −1 = seeded, nothing to fade

        public Clock()
        {
            _tickCore = TickCore;
            _tick = () => Reactive.Untrack(_tickCore);
            _write = WriteCore;
            _lease = Lease;
        }

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext)!;
            _ctx = ctx;
            _hooks = UseContext(InputHooks.Current);
            bool playing = Playback.IsPlaying.Value;
            bool occluded = _hooks.WindowOccluded?.Value ?? false;
            bool run = Demand.Ticks(Shell.Ui.ImmersiveLyrics.Value, playing, _settled.Value, occluded, Design.Reduced);
            _run = run;
            UseInterval(_tick, TickMs, run);
            UseEffect(_lease);
            UseEffect(OnPosition);
            UseEffect(DemandEdges);
            // the accent TARGET: seed the slab on the first run; afterwards capture the fade's start colour and time (V-U17)
            UseSignalEffect(() =>
            {
                var target = Stage.AccentSignal.Value;
                if (target.Equals(_accentTarget)) return;
                _accentTarget = target;
                if (_fadeStartMs < 0 || !_run) { ctx.Slab.Accent.Value = target; _fadeStartMs = 0; return; }   // mount, or the clock is not running: snap
                _fadeFrom = ctx.Slab.Accent.Peek();
                _fadeStartMs = Design.FrameTime.NowMs;
            });
            // the clock stopped mid-fade (settled / occluded / reduced): land on the target
            UseEffect(() => { if (!run && !ctx.Slab.Accent.Peek().Equals(_accentTarget)) ctx.Slab.Accent.Value = _accentTarget; }, DepKey.From(run));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        /// <summary>The ONE lease: the tier the VISIBLE consumer needs — the selected face in Visualizer mode, the base Field's
        /// breath otherwise (V-U55) — under the stage/playing/owner/supported/occluded/reduced gates (Visualizer.Demand.For).
        /// Spectrum implies level (the engine's AcquireSpectrum does that).</summary>
        Action? Lease()
        {
            var ctx = _ctx!;
            var tier = Demand.For(ctx.Kind.Value, ctx.Mode.Value == Stage.Mode.Visualizer, Shell.Ui.ImmersiveLyrics.Value, Playback.IsPlaying.Value,
                                  Playback.OwnerSignal.Value == Playback.Owner.Us, Playback.Audio.Supported.Value,
                                  _hooks?.WindowOccluded?.Value ?? false, Design.Reduced);
            ctx.Slab.LastTier = tier;
            Stage.Diagnostics.NoteLease(tier);
            if (tier == Tier.None) return null;
            IDisposable lease = tier == Tier.Spectrum ? Playback.Audio.AcquireSpectrum() : Playback.Audio.AcquireLevels();
            return lease.Dispose;
        }

        /// <summary>Re-anchor the playhead on every host report (the deck's PositionInterpolator discipline).</summary>
        void OnPosition()
        {
            int pos = Playback.PositionMs.Value;
            bool playing = Playback.IsPlaying.Value;
            bool buffering = Playback.Buffering.Value || Playback.PhaseSignal.Value == Playback.Phase.Loading;
            _ = Playback.Current.Value;
            if (pos != _lastReportMs || !_anchored)
            {
                _pos.Anchor(Design.FrameTime.NowMs, pos);
                _anchored = true;
                _lastReportMs = pos;
            }
            _advancing = playing && !buffering;
        }

        /// <summary>The precomputed sources for the playing track: the banded waveform, the beat grid, the audio group.</summary>
        void DemandEdges()
        {
            var cur = Playback.Current.Value;
            _ = Entities.ScopeEpoch.Value;
            if (cur.Kind != EntityKind.Track || cur.IsNone) return;
            var t = new Track(cur.Slot);
            if (!t.IsValid) return;
            Entities.EnsureEdge(FetchEdge.TrackWaveform, t.Slot);
            Entities.EnsureEdge(FetchEdge.TrackBeats, t.Slot);
            if (!t.Knows(TrackFields.Audio)) Entities.Ensure(t, TrackFields.Audio);
        }

        void TickCore()
        {
            var ctx = _ctx!;
            var slab = ctx.Slab;
            long now = Design.FrameTime.NowMs;
            if (!_anchored) { _pos.Anchor(now, Playback.PositionMs.Peek()); _anchored = true; }
            float dt = DeltaSec(_lastTickMs, now);
            _lastTickMs = now;

            int n = Playback.Audio.CopySpectrum(_db, out SpectrumInfo info);
            _haveLive = n == Bands.Count && info.Live && info.Sequence != 0;
            slab.LastFftMs = info.FftMs; slab.LastAlignFrames = info.AlignFrames; slab.LastSequence = info.Sequence;
            // the level: the analysed window's PRE-gain RMS under a spectrum lease, the (post-gain) level tap otherwise
            float rms = _haveLive ? info.WindowRms : Playback.Audio.Levels.Peek().Rms;

            var cur = Playback.Current.Peek();
            long dur = Playback.DurationMs.Peek();
            long pos = _pos.Estimate(now, _advancing, null, null, dur);
            ReadOnlySpan<WaveSample> bands = default; ReadOnlySpan<uint> beats = default; ushort tempo = 0;
            bool haveBands = false, haveBeats = false;
            if (cur.Kind == EntityKind.Track && !cur.IsNone)
            {
                var edges = Entities.Current.Edges;
                if (edges.TrackWaveform.State(cur.Slot) == EdgeState.Complete) { bands = edges.TrackWaveform.Payload(cur.Slot); haveBands = bands.Length > 0; }
                if (edges.TrackBeats.State(cur.Slot) == EdgeState.Complete) { beats = edges.TrackBeats.Payload(cur.Slot); haveBeats = beats.Length > 0; }
                var t = new Track(cur.Slot);
                if (t.IsValid && t.Knows(TrackFields.Audio)) tempo = t.Tempo;
            }
            // every preference is a PEEK of a context signal (O8) — never Prefs.* on the tick
            var input = new Input(Playback.IsPlaying.Peek(), ctx.Calm.Peek(), ctx.Sensitivity.Peek(), _haveLive, info.Muted, rms,
                                  pos, dur, haveBands, haveBeats, tempo, ctx.Mode.Peek() == Stage.Mode.Visualizer);
            _pending = _model.Tick(in input, _db, bands, beats, dt);
            if (Context.Runtime is { } rt) rt.Batch(_write); else WriteCore();
        }

        /// <summary>ONE batch → ONE frame request, however many signals moved. Peek-compare before every write.</summary>
        void WriteCore()
        {
            var s = _ctx!.Slab; var f = _pending; var m = _model;
            for (int i = 0; i < Bands.Count; i++) { Set(s.Bands[i], Q(m.Level[i], 1f / 128f)); Set(s.Peaks[i], Q(m.Peak[i], 1f / 128f)); }
            Set(s.Low, Q(f.Low, 1f / 256f)); Set(s.Mid, Q(f.Mid, 1f / 256f)); Set(s.High, Q(f.High, 1f / 256f)); Set(s.Level, Q(f.Level, 1f / 256f));
            Set(s.Kick, Q(f.Kick, 1f / 256f)); Set(s.BeatPhase, Q(f.BeatPhase, 1f / 256f));
            Set(s.FaceFieldOp, Q(f.FaceFieldOp, 1f / 256f)); Set(s.BaseFieldOp, Q(f.BaseFieldOp, 1f / 256f));
            Set(s.GlowOp, Q(f.GlowOp, 1f / 256f)); Set(s.BeatScale, Q(f.BeatScale, 1f / 4096f));
            Set(s.ReelL, Q(f.ReelL, 0.25f)); Set(s.ReelR, Q(f.ReelR, 0.25f)); Set(s.AngleL, Q(f.AngleL, 0.5f)); Set(s.AngleR, Q(f.AngleR, 0.5f));
            for (int r = 0; r < Pulse.Rings; r++) Set(s.Rings[r], Q(Pulse.RingProgress(f.BeatIndex, f.BeatPhase, r), 1f / 256f));
            if (s.BeatIndex.Peek() != f.BeatIndex) s.BeatIndex.Value = f.BeatIndex;
            if (s.MeterLit.Peek() != f.MeterLit) s.MeterLit.Value = f.MeterLit;
            if (s.Source.Peek() != f.Source) { s.Source.Value = f.Source; Stage.Diagnostics.NoteSource(f.Source); }
            Copy(m.HorizonLow, s.HorizonLow); Copy(m.HorizonMid, s.HorizonMid); Copy(m.HorizonHigh, s.HorizonHigh);
            Copy(m.AuroraLow, s.AuroraLow); Copy(m.AuroraMid, s.AuroraMid); Copy(m.AuroraHigh, s.AuroraHigh);
            // the accent cross-fade rides the same tick (a bound Fill snaps, §1.8): LINEAR from the captured start over
            // elapsed / Stage.Tone.CrossFadeMs, landing exactly on the target (V-U17)
            if (_fadeStartMs > 0)
            {
                float p = Stage.Tone.Progress(_lastTickMs - _fadeStartMs);
                var next = p >= 1f ? _accentTarget : ColorF.Lerp(_fadeFrom, _accentTarget, p);
                if (!next.Equals(s.Accent.Peek())) s.Accent.Value = next;
                if (p >= 1f) _fadeStartMs = 0;
            }
            bool settled = m.IsSettled;
            if (_settled.Peek() != settled) _settled.Value = settled;   // the gate's mirror (Deck.UI.cs:378-379)
        }

        static void Set(FloatSignal s, float v) { if (v != s.Peek()) s.Value = v; }
        static float Q(float v, float q) => MathF.Round(v / q) * q;
        static void Copy(float[] from, SeriesSource to) { from.AsSpan().CopyTo(to.Buffer); to.Count = from.Length; to.Publish(); }
    }

    // ══ 3. THE FACES ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The box a face fills and whether it is a gallery preview (fewer parts, no cover).</summary>
    public readonly record struct FaceSpec(float W, float H, bool Preview, string? CoverUrl);

    /// <summary>Build a face ONCE for a slab; everything that moves is a bound prop over it. The stage remounts per kind
    /// (a keyed CHILD box, <c>Key = "viz:" + kind</c>), the gallery mounts eight at preview scale.</summary>
    public static Element Face(Kind kind, Slab slab, in Palette pal, in FaceSpec spec) => kind switch
    {
        Kind.Field => FieldFace(slab, pal, spec),
        Kind.Halo => HaloFace(slab, pal, spec),
        Kind.Horizon => HorizonFace(slab, pal, spec),
        Kind.Matrix => MatrixFace(slab, pal, spec),
        Kind.Aurora => AuroraFace(slab, pal, spec),
        Kind.Spectrum => SpectrumFace(slab, pal, spec),
        Kind.Pulse => PulseFace(slab, pal, spec),
        _ => TapeFace(slab, pal, spec),
    };

    static float Min(in FaceSpec s) => MathF.Min(s.W, s.H);
    static float Max(in FaceSpec s) => MathF.Max(s.W, s.H);

    // ── Field: four blobs on 28–36 s keyframes, breathing opacity, ONE bound channel ──────────────────────────────────

    /// <summary>Also the stage's always-on backdrop layer (Stage.UI.cs Backdrop): <paramref name="opacity"/> is the slab's
    /// Face/BaseFieldOp as a Prop.</summary>
    public static Element FieldFace(Slab slab, in Palette pal, in FaceSpec spec, Prop<float>? opacity = null)
    {
        float d = 1.1f * Max(in spec);
        var kids = new List<CanvasChild>(Field.Blobs);
        ColorF[] tints = [pal.F1, pal.F2, pal.F3, pal.F4];
        (float X, float Y)[] corners = [(-0.55f, -0.62f), (1f - 0.55f, -0.62f), (-0.55f, 1f - 0.66f), (1f - 0.58f, 1f - 0.64f)];
        for (int i = 0; i < Field.Blobs; i++)
        {
            float x = corners[i].X * Max(in spec), y = corners[i].Y * Max(in spec);
            // RE-PUSHED props (component-props contract): a new palette recolours the mounted blob; the keyframes keep running.
            kids.Add(new CanvasChild(x, y, Embed.Comp(new Blob.Props(i, d, tints[i], spec.W, spec.H), static () => new Blob()) with { Key = "blob:" + i }));
        }
        return new BoxEl
        {
            Width = spec.W, Height = spec.H, ClipToBounds = true, HitTestVisible = false,
            Opacity = opacity ?? (Prop<float>)slab.FaceFieldOp,
            Children = [Canvas.Create(spec.W, spec.H, kids)],
        };
    }

    /// <summary>One blob: a radial-gradient disc on looping ping-pong keyframes (translate + scale) — zero ticks. The
    /// keyframes target this component's own root (the hooks' HostNode contract), which is why each blob is a component.
    /// Under reduced motion the loop is replaced by REST keys (the engine does not snap looping keyframes — §1.8, V-E10).</summary>
    sealed class Blob : Component
    {
        public sealed record Props(int Index, float Diameter, ColorF Tint, float W, float H);
        static readonly Keyframe[] s_restTranslate = [new Keyframe(0f, 0f), new Keyframe(1f, 0f)];
        static readonly Keyframe[] s_restScale = [new Keyframe(0f, 1f), new Keyframe(1f, 1f)];
        public override Element Render()
        {
            var p = UseProps<Props>();
            var (dx, dy, sc) = Field.Drift[p.Index];
            float ms = Field.PeriodSec[p.Index] * 1000f;
            bool reduced = Design.Reduced;
            var key = DepKey.From(p.Index, (int)p.W, (int)p.H, reduced ? 1 : 0);   // the 4-int form — there is no 3-int DepKey (V-U3)
            UseKeyframes(AnimChannel.TranslateX, reduced ? s_restTranslate : [new Keyframe(0f, 0f), new Keyframe(0.5f, dx * p.W, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.TranslateY, reduced ? s_restTranslate : [new Keyframe(0f, 0f), new Keyframe(0.5f, dy * p.H, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.ScaleX, reduced ? s_restScale : [new Keyframe(0f, 1f), new Keyframe(0.5f, sc, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.ScaleY, reduced ? s_restScale : [new Keyframe(0f, 1f), new Keyframe(0.5f, sc, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)], ms, loop: !reduced, key);
            return new BoxEl
            {
                Width = p.Diameter, Height = p.Diameter, Corners = Radii.Circle(p.Diameter), HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, p.Tint), new GradientStop(0.55f, p.Tint with { A = 0.45f }), new GradientStop(1f, p.Tint with { A = 0f })]),
            };
        }
    }

    // ── Halo: 72 (36 preview) bars around the cover, the glow on the bass, the cover on the kick ───────────────────────

    static Element HaloFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float m = Min(in spec);
        float cx = spec.W * 0.5f, cy = spec.H * 0.46f;
        float r = 0.17f * m, barW = MathF.Max(1f, 0.0076f * m), barH = 0.11f * m, cover = 0.22f * m;
        int bars = spec.Preview ? Halo.Bars / 2 : Halo.Bars;
        var kids = new List<CanvasChild>(bars + 2)
        {
            new CanvasChild(cx - 0.45f * m, cy - 0.45f * m, new BoxEl
            {
                Width = 0.9f * m, Height = 0.9f * m, Corners = Radii.Circle(0.9f * m), HitTestVisible = false,
                Opacity = slab.GlowOp,
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, pal.C1), new GradientStop(1f, pal.C1 with { A = 0f })]),
            }),
        };
        for (int j = 0; j < bars; j++)
        {
            int src = spec.Preview ? j * 2 : j;
            var band = slab.Bands[Halo.BandOf(src)];
            ColorF c = ColorF.Lerp(pal.C2, pal.C1, Halo.MixOf(src));
            // wrapper: bottom at the centre, rotated about it; inner bar: the top barH of the wrapper, scaled about its own bottom
            kids.Add(new CanvasChild(cx - barW * 0.5f, cy - (r + barH), new BoxEl
            {
                Width = barW, Height = r + barH, Direction = 1, HitTestVisible = false,
                Rotation = Halo.AngleDeg(src), TransformOriginX = 0.5f, TransformOriginY = 1f,
                Children =
                [
                    new BoxEl
                    {
                        Width = barW, Height = barH, Shrink = 0f, Corners = Radii.Circle(barW), Fill = c,
                        TransformOriginX = 0.5f, TransformOriginY = 1f,
                        Transform = Prop.Of(() => Affine2D.Scale(1f, Halo.Scale(band.Value))),
                    },
                ],
            }));
        }
        if (!spec.Preview)
            kids.Add(new CanvasChild(cx - cover * 0.5f, cy - cover * 0.5f, new BoxEl
            {
                Width = cover, Height = cover, Corners = CornerRadius4.All(0.064f * m), ClipToBounds = true, Shadow = Elevation.Dialog,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Transform = Prop.Of(() => Affine2D.Scale(slab.BeatScale.Value, slab.BeatScale.Value)),
                Children = [Controls.Artwork(spec.CoverUrl, cover, cover, 0.064f * m, decodePx: 512)],
            }));
        return FaceFrame(spec, kids);
    }

    // ── Horizon: three mirrored SeriesEls over the precomputed bands, a past veil, the playhead ───────────────────────

    static Element HorizonFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float top = 0.28f * spec.H, h = 0.44f * spec.H;
        var lo = slab.HorizonLow; var mi = slab.HorizonMid; var hi = slab.HorizonHigh;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, top, new BoxEl
            {
                Width = spec.W, Height = h, ZStack = true, HitTestVisible = false,
                Children =
                [
                    // Mirrored amplitude is measured against HALF the height (§4.3.6): 0.92 ⇒ a sample of 1.0 reaches 92 % of the way to the edge
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Mirrored, Amplitude = Horizon.Amplitude, Color = pal.C2, Samples = Prop.Of(() => lo.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Mirrored, Amplitude = Horizon.Amplitude, Color = pal.C1, Samples = Prop.Of(() => mi.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Mirrored, Amplitude = Horizon.Amplitude, Color = ColorF.Lerp(pal.C1, ColorF.FromRgba(255, 255, 255), 0.75f), Samples = Prop.Of(() => hi.Current) },
                    // the past veil: the LEFT half (horizontal = JustifySelf), full height (vertical = AlignSelf) — V-U9
                    new BoxEl { Width = spec.W * 0.5f, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, Gradient = GradientRight(new GradientStop(0f, ColorF.FromRgba(14, 12, 14, 204)), new GradientStop(1f, ColorF.FromRgba(14, 12, 14, 77))) },
                ],
            }),
            new CanvasChild(spec.W * 0.5f - 1f, top - 0.03f * spec.H, new BoxEl { Width = 2f, Height = h + 0.06f * spec.H, Corners = Radii.Circle(2f), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false }),
        ]);
    }

    // ── Matrix: 32 (16 preview) columns × 12 rows — THREE nodes per column (dim track, lit bar, peak dot) + 12 static row
    //    carvers that cut every bar into "dots" (V-U35: a dot per cell was ~1,700 nodes; this is 108) ─────────────────────

    static Element MatrixFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        int cols = spec.Preview ? Matrix.Columns / 2 : Matrix.Columns;
        float left = 0.14f * spec.W, top = 0.20f * spec.H, w = spec.W - 2f * left, h = spec.H - top - 0.28f * spec.H;
        float cw = w / cols, rh = h / Matrix.Rows, bar = MathF.Max(2f, cw * 0.44f), carve = MathF.Max(1f, rh * 0.42f);
        var kids = new List<CanvasChild>(cols * 3 + Matrix.Rows);
        for (int c = 0; c < cols; c++)
        {
            int src = spec.Preview ? c * 2 : c;
            var band = slab.Bands[Matrix.BandOf(src)]; var peak = slab.Peaks[Matrix.BandOf(src)];
            float x = left + c * cw + (cw - bar) * 0.5f;
            // the dim track: the whole column at 16 %
            kids.Add(new CanvasChild(x, top, new BoxEl { Width = bar, Height = h, Corners = Radii.Circle(bar), Fill = ColorF.FromRgba(255, 255, 255, 41), HitTestVisible = false }));
            // the lit bar: scaled from the BOTTOM to LitRows/Rows — quantised to whole rows so it steps like a dot column
            kids.Add(new CanvasChild(x, top, new BoxEl
            {
                Width = bar, Height = h, Corners = Radii.Circle(bar), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false,
                TransformOriginX = 0.5f, TransformOriginY = 1f,
                Transform = Prop.Of(() => Affine2D.Scale(1f, Matrix.LitRows(band.Value) / (float)Matrix.Rows)),
            }));
            // the peak dot: one accent cell, translated to its row
            kids.Add(new CanvasChild(x, top, new BoxEl
            {
                Width = bar, Height = rh - carve, Corners = Radii.Circle(bar), Fill = Prop.Bind(slab.Accent), HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(0f, (Matrix.Rows - Matrix.PeakRow(peak.Value)) * rh)),
            }));
        }
        // the row carvers: static strips of the stage floor between rows, ABOVE the columns — one per row boundary
        for (int rr = 1; rr < Matrix.Rows; rr++)
            kids.Add(new CanvasChild(left - bar, top + rr * rh - carve * 0.5f, new BoxEl { Width = w + 2f * bar, Height = carve, Fill = Ink.Floor, HitTestVisible = false }));
        return FaceFrame(spec, kids);
    }

    // ── Aurora: three baseline SeriesEls, screen-ish by alpha ──────────────────────────────────────────────────────────

    static Element AuroraFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float h = 0.62f * spec.H;
        var lo = slab.AuroraLow; var mi = slab.AuroraMid; var hi = slab.AuroraHigh;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, spec.H - h, new BoxEl
            {
                Width = spec.W, Height = h, ZStack = true, HitTestVisible = false,
                Children =
                [
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Baseline, Color = pal.C2 with { A = 0.80f }, Samples = Prop.Of(() => lo.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Baseline, Color = pal.C1 with { A = 0.60f }, Samples = Prop.Of(() => mi.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Baseline, Color = pal.Accent with { A = 0.50f }, Samples = Prop.Of(() => hi.Current) },
                ],
            }),
        ]);
    }

    // ── Spectrum: 48 (24 preview) bars + caps + a dimmer reflection ───────────────────────────────────────────────────

    static Element SpectrumFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        int bars = spec.Preview ? Spectrum.Bars / 2 : Spectrum.Bars;
        float left = 0.09f * spec.W, w = spec.W - 2f * left, gap = 0.0045f * spec.W, bw = (w - gap * (bars - 1)) / bars;
        float h = 0.42f * spec.H, bottom = 0.30f * spec.H, top = spec.H - bottom - h;
        var grad = new GradientSpec(GradientShape.Linear, 90f, [new GradientStop(0f, ColorF.FromRgba(255, 255, 255)), new GradientStop(0.3f, pal.C1), new GradientStop(1f, pal.C2)]);
        var kids = new List<CanvasChild>(bars * 3);
        for (int i = 0; i < bars; i++)
        {
            int src = spec.Preview ? i * 2 : i;
            var band = slab.Bands[src]; var peak = slab.Peaks[src];
            float x = left + i * (bw + gap);
            kids.Add(new CanvasChild(x, top, new BoxEl { Width = bw, Height = h, Gradient = grad, Corners = new CornerRadius4(bw * 0.35f, bw * 0.35f, bw * 0.1f, bw * 0.1f), TransformOriginX = 0.5f, TransformOriginY = 1f, HitTestVisible = false,
                                                        Transform = Prop.Of(() => Affine2D.Scale(1f, Spectrum.Scale(band.Value))) }));
            kids.Add(new CanvasChild(x, top, new BoxEl { Width = bw, Height = MathF.Max(2f, 0.005f * spec.H), Corners = Radii.Circle(2f), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false,
                                                        Transform = Prop.Of(() => Affine2D.Translation(0f, h * (1f - peak.Value) - 2f)) }));
            if (!spec.Preview)
                kids.Add(new CanvasChild(x, top + h + 0.006f * spec.H, new BoxEl { Width = bw, Height = h, Gradient = grad, Opacity = 0.22f, TransformOriginX = 0.5f, TransformOriginY = 0f, HitTestVisible = false,
                                                                                 Transform = Prop.Of(() => Affine2D.Scale(1f, 0.45f * Spectrum.Scale(band.Value))) }));
        }
        return FaceFrame(spec, kids);
    }

    // ── Pulse: four accent rings a beat apart, the glow, the cover on the kick ────────────────────────────────────────

    static Element PulseFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float m = Min(in spec), cx = spec.W * 0.5f, cy = spec.H * 0.46f, cover = 0.26f * m, ring = MathF.Max(1f, 0.003f * m);
        var kids = new List<CanvasChild>(Pulse.Rings + 2)
        {
            new CanvasChild(cx - 0.45f * m, cy - 0.45f * m, new BoxEl
            {
                Width = 0.9f * m, Height = 0.9f * m, Corners = Radii.Circle(0.9f * m), HitTestVisible = false, Opacity = Prop.Of(() => Pulse.GlowOpacity(slab.Low.Value)),
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, pal.C1), new GradientStop(1f, pal.C1 with { A = 0f })]),
            }),
        };
        for (int r = 0; r < Pulse.Rings; r++)
        {
            var p = slab.Rings[r];
            kids.Add(new CanvasChild(cx - cover * 0.5f, cy - cover * 0.5f, new BoxEl
            {
                Width = cover, Height = cover, Corners = CornerRadius4.All(0.077f * m), BorderWidth = ring, BorderColor = Prop.Bind(slab.Accent), HitTestVisible = false,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Opacity = Prop.Of(() => Pulse.RingOpacity(p.Value)),
                Transform = Prop.Of(() => Affine2D.Scale(Pulse.RingScale(p.Value), Pulse.RingScale(p.Value))),
            }));
        }
        if (!spec.Preview)
            kids.Add(new CanvasChild(cx - cover * 0.5f, cy - cover * 0.5f, new BoxEl
            {
                Width = cover, Height = cover, Corners = CornerRadius4.All(0.077f * m), ClipToBounds = true, Shadow = Elevation.Dialog,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f, Transform = Prop.Of(() => Affine2D.Scale(slab.BeatScale.Value, slab.BeatScale.Value)),
                Children = [Controls.Artwork(spec.CoverUrl, cover, cover, 0.077f * m, decodePx: 512)],
            }));
        return FaceFrame(spec, kids);
    }

    // ── Tape: two area-conserving packs, two hubs, the tape, a 20-cell level meter ───────────────────────────────────────

    static Element TapeFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        // the prototype's 960×540 board, scaled to fit (xMidYMid meet)
        float k = MathF.Min(spec.W / 960f, spec.H / 540f), ox = (spec.W - 960f * k) * 0.5f, oy = (spec.H - 540f * k) * 0.5f;
        float flange = Tape.Flange * k, hub = Tape.HubR * k, pack = Tape.R1 * k;
        var kids = new List<CanvasChild>(8 + Tape.MeterCells)
        {
            Reel(ox + 300f * k, oy + 250f * k, flange, pack, hub, slab.ReelL, slab.AngleL, pal.Accent),
            Reel(ox + 660f * k, oy + 250f * k, flange, pack, hub, slab.ReelR, slab.AngleR, pal.C2),
            new CanvasChild(ox + 268f * k, oy + 250f * k, new PolylineStrokeEl
            {
                Width = 424f * k, Height = 184f * k, P0 = new Point2(0f, 95f * k), P1 = new Point2(202f * k, 180f * k), P2 = new Point2(222f * k, 180f * k), P3 = new Point2(424f * k, 95f * k),
                PointCount = 4, Color = ColorF.FromRgba(0x7a, 0x66, 0x56), Thickness = 3f * k, RoundCaps = false,
            }),
            new CanvasChild(ox + 468f * k, oy + 424f * k, new BoxEl { Width = 24f * k, Height = 12f * k, Corners = CornerRadius4.All(2f * k), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false }),
        };
        if (!spec.Preview)
        {
            float mLeft = 0.14f * spec.W, mW = spec.W - 2f * mLeft, mGap = 0.005f * spec.W, cell = (mW - mGap * (Tape.MeterCells - 1)) / Tape.MeterCells, mH = MathF.Max(3f, 0.022f * spec.H);
            ColorF[] zone = [pal.C2, pal.Accent, pal.C1];
            for (int i = 0; i < Tape.MeterCells; i++)
            {
                int cellIndex = i; ColorF on = zone[Tape.Zone(i)];
                kids.Add(new CanvasChild(mLeft + i * (cell + mGap), spec.H - 0.25f * spec.H, new BoxEl
                {
                    Width = cell, Height = mH, Corners = Radii.Circle(2f), HitTestVisible = false,
                    Fill = Prop.Of(() => slab.MeterLit.Value > cellIndex ? on : ColorF.FromRgba(255, 255, 255, 26)),
                }));
            }
        }
        return FaceFrame(spec, kids);
    }

    /// <summary>One reel: flange ring, the pack (radius bound as a scale of its max), the hub with three spokes (bound rotation).</summary>
    static CanvasChild Reel(float cx, float cy, float flange, float pack, float hub, FloatSignal radius, FloatSignal angleDeg, ColorF accent)
    {
        float scaleK = 1f / Tape.R1;
        return new CanvasChild(cx - flange, cy - flange, new BoxEl
        {
            Width = 2f * flange, Height = 2f * flange, ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children =
            [
                new BoxEl { Width = 2f * flange, Height = 2f * flange, Corners = Radii.Circle(2f * flange), BorderWidth = 2f, BorderColor = ColorF.FromRgba(255, 255, 255, 217) },
                new BoxEl
                {
                    Width = 2f * pack, Height = 2f * pack, Corners = Radii.Circle(2f * pack), Fill = ColorF.FromRgba(0x2b, 0x26, 0x21), BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 31),
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Scale(radius.Value * scaleK, radius.Value * scaleK)),
                },
                new BoxEl
                {
                    Width = 2f * hub, Height = 2f * hub, ZStack = true, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Rotation(angleDeg.Value * (MathF.PI / 180f))),
                    Children =
                    [
                        new BoxEl { Width = 2f * hub, Height = 2f * hub, Corners = Radii.Circle(2f * hub), Fill = ColorF.FromRgba(0x11, 0x11, 0x11), BorderWidth = 3f, BorderColor = accent },
                        Spoke(hub, accent, 0f), Spoke(hub, accent, 120f), Spoke(hub, accent, 240f),
                    ],
                },
            ],
        });
    }

    /// <summary>A spoke: horizontally centred (JustifySelf) at the TOP of the hub box (AlignSelf), rotated about the hub centre — V-U9.</summary>
    static BoxEl Spoke(float hub, ColorF c, float deg) => new()
    {
        Width = 5f, Height = hub * 0.7f, Corners = Radii.Circle(5f), Fill = c, JustifySelf = FlexAlign.Center, AlignSelf = FlexAlign.Start,
        Rotation = deg, TransformOriginX = 0.5f, TransformOriginY = hub / (hub * 0.7f),   // rotate about the hub centre
        Margin = new Edges4(0f, hub * 0.3f, 0f, 0f),
    };

    // ── the frame every face shares ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clipped box a face fills; <c>Canvas.Create</c> takes the children list directly (IReadOnlyList&lt;CanvasChild&gt;,
    /// Canvas.cs:16) — no wrapper record (V-U2). Named FaceFrame: <see cref="Frame"/> is the model's output record.</summary>
    static Element FaceFrame(in FaceSpec spec, List<CanvasChild> kids) => new BoxEl
    {
        Width = spec.W, Height = spec.H, ClipToBounds = true, HitTestVisible = false,
        Children = [Canvas.Create(spec.W, spec.H, kids)],
    };

    // ══ 4. THE GALLERY ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The acrylic pane: title row, then a SCROLLER holding the 2-column gallery of live previews and the four
    /// settings rows (the grid + settings run to ≈ 856 DIP at 1080p against a 856-DIP pane and overflow at 900p — V-U21).
    /// Mounted by Stage.UI.cs's GalleryHost. Every preference is a SIGNAL on the context (O8): the tiles' selection border
    /// and check disc bind <c>ctx.Kind</c>; the slider and toggle controls take the context's own signals (seeded ONCE by
    /// SurfaceCore's epoch effect and written by the controls while dragging — a write round-trips through Prefs.Stage →
    /// Epoch → the same effect, which <c>SetIfChanged</c>s the clamped value back). <c>ItemsView.Create</c> freezes its
    /// template at mount (ItemsView.cs:611-617), so the grid is KEYED by the palette + accent so a cover change remounts
    /// the eight tiles with the new colours (V-U20).</summary>
    public static Element Gallery(Stage.StageCtx ctx, Stage.Layout layout, Action close)
    {
        var slab = ctx.Slab;
        var p = ctx.Palette.Value;
        var accent = ctx.Accent.Value;
        var kindSig = ctx.Kind;
        float tileW = (layout.GalleryW - 40f - 10f) * 0.5f, previewH = 78f, tileH = previewH + 50f;
        Element tile(int i)
        {
            var kind = (Kind)i;
            return new BoxEl
            {
                Direction = 1, Gap = 6f, Padding = new Edges4(6f, 6f, 6f, 8f), Corners = CornerRadius4.All(6f), Fill = ColorF.FromRgba(255, 255, 255, 10),
                HoverFill = ColorF.FromRgba(255, 255, 255, 19), BrushTransitionMs = Design.Motion.Fast, Cursor = CursorId.Hand,
                BorderWidth = 2f, BorderColor = Prop.Of(() => kindSig.Value == kind ? accent.Fill : ColorF.FromRgba(255, 255, 255, 15)),
                Children =
                [
                    new BoxEl
                    {
                        Width = tileW - 12f, Height = previewH, Corners = Radii.ControlAll, ClipToBounds = true, ZStack = true,
                        Gradient = new GradientSpec(GradientShape.Linear, 135f, [new GradientStop(0f, p.F1), new GradientStop(1f, p.F4)]),
                        Children =
                        [
                            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = ColorF.FromRgba(14, 12, 14, 115) },
                            Face(kind, slab, in p, new FaceSpec(tileW - 12f, previewH, Preview: true, CoverUrl: null)),
                            // the check disc: TOP (AlignSelf) RIGHT (JustifySelf) — V-U9
                            new BoxEl
                            {
                                Width = 20f, Height = 20f, Corners = Radii.Circle(20f), Fill = accent.Fill, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 8f, 8f, 0f),
                                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Visible = Prop.Of(() => kindSig.Value == kind),
                                Children = [new TextEl(Icons.Check) { Size = 12f, FontFamily = Theme.IconFont, Color = accent.Ink }],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Padding = new Edges4(4f, 0f, 4f, 0f),
                        Children =
                        [
                            new TextEl(Loc.Get(NameKey(kind))) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink },
                            new TextEl(Prop.Of(() => SubtitleOf(kind, kindSig.Value, slab.Source.Value))) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            };
        }
        var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary, ThumbRing = ColorF.FromRgba(0x45, 0x45, 0x45), RailFill = ColorF.FromRgba(255, 255, 255, 139) };
        var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = 40f, OffBorder = ColorF.FromRgba(255, 255, 255, 153), OffKnob = ColorF.FromRgba(255, 255, 255, 204) };
        return new BoxEl
        {
            Width = layout.GalleryW, Height = layout.GalleryH, Direction = 1, Gap = 10f, Padding = new Edges4(20f, 14f, 20f, 20f), Corners = Radii.CardAll,
            Acrylic = AcrylicSpec.InAppBase, Shadow = Elevation.Flyout, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.GalleryTitle)) { Size = 20f, LineHeight = 28f, Weight = 600, FontFamily = "Segoe UI Variable Display", Color = Ink.Ink },
                        ToolTip.Wrap(IconButton.Create(Icons.Cancel, close, size: ControlSize.Small), Loc.Get(Strings.Stage.ClosePanel)),
                    ],
                },
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stagegallery",
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = 10f,
                        Children =
                        [
                            ItemsView.Create(Catalog.Count, tile, RepeatLayout.Grid(2, tileH, 10f), new ListOptions
                            {
                                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None, IsItemInvokedEnabled = true,
                                OnInvoked = static i => { Prefs.Stage.SetVisualizer(i); Stage.Diagnostics.NotePick((Kind)i); },
                                KeyOf = static i => "viz:" + i, Grow = 0f,
                            }) with { Key = "gallery:grid:" + HashCode.Combine(p, accent.Fill) },   // a new palette REMOUNTS the frozen template
                            new TextEl(Loc.Get(Strings.Stage.SettingsHeader)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, Margin = new Edges4(0f, 6f, 0f, 0f) },
                            SettingRow(Icons.Audio, Loc.Get(Strings.Stage.Sensitivity),
                                Slider.Create(ctx.Sensitivity, static v => Prefs.Stage.SetSensitivity(v), SensitivityOptions, length: 150f, thickness: 24f, style: sliderStyle)),
                            SettingRow(Icons.Document, Loc.Get(Strings.Stage.LyricsOverlay),
                                ToggleSwitch.Create(ctx.LyricsOverlay, static on => Prefs.Stage.SetLyricsOverlay(on), style: toggleStyle)),
                            SettingRow(Icons.RefineSparkle, Loc.Get(Strings.Stage.ReduceMotion),
                                ToggleSwitch.Create(ctx.Calm, static on => Prefs.Stage.SetCalm(on), style: toggleStyle)),
                            SettingRow(Icons.Clock, Loc.Get(Strings.Stage.SyncOffset),
                                Slider.Create(ctx.SyncOffsetMs, static v => { int ms = (int)MathF.Round(v / 10f) * 10; Prefs.Stage.SetSyncOffsetMs(ms); Playback.Audio.SetSpectrumOffsetMs(ms); }, OffsetOptions, length: 150f, thickness: 24f, style: sliderStyle)),
                        ],
                    },
                },
            ],
        };
    }

    static readonly Slider.SliderOptions SensitivityOptions = new() { Min = Bands.MinSensitivity, Max = Bands.MaxSensitivity, Step = 0.05f, IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" };
    static readonly Slider.SliderOptions OffsetOptions = new() { Min = -500f, Max = 500f, Step = 10f, IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms" };

    static Element SettingRow(string glyph, string label, Element control) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = Spacing.M, MinHeight = 48f,
        Padding = new Edges4(16f, 0f, 12f, 0f), Corners = Radii.ControlAll, Fill = ColorF.FromRgba(255, 255, 255, 13), BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 15),
        Children =
        [
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary }, new TextEl(label) { Size = 14f, LineHeight = 20f, Color = Ink.Ink }] },
            control,
        ],
    };

    public static string NameKey(Kind k) => k switch
    {
        Kind.Field => Strings.Stage.Viz.Field, Kind.Halo => Strings.Stage.Viz.Halo, Kind.Horizon => Strings.Stage.Viz.Horizon, Kind.Matrix => Strings.Stage.Viz.Matrix,
        Kind.Aurora => Strings.Stage.Viz.Aurora, Kind.Spectrum => Strings.Stage.Viz.Spectrum, Kind.Pulse => Strings.Stage.Viz.Pulse, _ => Strings.Stage.Viz.Tape,
    };
    static string SubKey(Kind k) => k switch
    {
        Kind.Field => Strings.Stage.VizSub.Field, Kind.Halo => Strings.Stage.VizSub.Halo, Kind.Horizon => Strings.Stage.VizSub.Horizon, Kind.Matrix => Strings.Stage.VizSub.Matrix,
        Kind.Aurora => Strings.Stage.VizSub.Aurora, Kind.Spectrum => Strings.Stage.VizSub.Spectrum, Kind.Pulse => Strings.Stage.VizSub.Pulse, _ => Strings.Stage.VizSub.Tape,
    };
    /// <summary>The tile's second line: the face's blurb, or — while it is the active kind falling back — where its motion comes from.
    /// <paramref name="current"/> is the context's kind SIGNAL value (never a registry read — O8).</summary>
    static string SubtitleOf(Kind k, Kind current, Source live)
    {
        if (current != k || live == Source.Live) return Loc.Get(SubKey(k));
        return Loc.Get(live switch { Source.Precomputed => Strings.Stage.Source.Precomputed, Source.TempoGrid => Strings.Stage.Source.Tempo, _ => Strings.Stage.Source.Breath });
    }
}
```
Notes for the implementer: `Canvas.Create(float width, float height, IReadOnlyList<CanvasChild> children) : BoxEl` / `CanvasChild(float X, float Y, Element Child)` (`FluentGpu.Controls\Canvas.cs:7, :16` — each child is wrapped in an `OffsetX/OffsetY` box); the faces build a `List<CanvasChild>` and hand it over directly. `Scheme` is `Wavee.Scheme` (`Entities/Palette.cs:67-72`, ARGB `uint`s; `Design.SchemeFor(ReadOnlySpan<char>) : Scheme?` `Design.cs:1041`) and `Design.Palette.ToColor(uint)` (`Design.cs:677`) is the conversion. `AcrylicSpec.InAppBase` and `Elevation.Flyout` are the gallery/transport materials (`Foundation/Effects.cs:158` statics; `Dsl/Elevation.cs:33`). `Prop.Bind(slab.Accent)` is the interface-typed signal bind (`Prop.cs:23-30`). `Ink.Floor` in `MatrixFace` is the file's `using Ink = Wavee.Design.StageInk;` alias (the stage floor colour, `Design.cs:608-653`). `ScrollEl { Grow, MinHeight, AutoEdgeFade, ScrollKey, Content }` is `Element.cs:953-1049`; the grid has `Grow = 0f` so it lays out at its content height (4 rows) inside the pane's scroller.

### 4.10 App UI — NEW `src/apps/Wavee/Shell/Stage.UI.cs` (replaces the whole file, WP-U2)

```csharp
// ── Shell/Stage.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// StageCtx (the stage's signals), SurfaceCore (fullscreen, focus scope, idle, accent, the track-key signal), Backdrop, Hero
// (the morphing cover), PaneHost (Lyrics · Up next · Artist under KeepAlive), the overlay caption, ChromeHost → TopBar,
// TransportCard, GalleryHost, the hairline, the TeachingTip, Open/Close
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 1200 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §3, §4.10
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FULLSCREEN STAGE. `Stage.View()` is what the shell mounts while `Shell.Ui.ImmersiveLyrics` is up (Shell.UI.cs:500).
// Mount = enter: the window goes borderless-fullscreen (the Video.UI.cs:1213-1218 shape), the shell unmounts its chrome
// (FrameRules.ChromeMounted) and collapses its body (ContentRegion.Visible), a focus scope takes the keys. Unmount = exit.
//
//   • ONE context instance of SIGNALS (`StageCtx`) for the stage's life; every layer is a component on the signals it reads,
//     so SurfaceCore's tree is static and a track / preference / layout change re-renders only what reads it.
//   • ONE layout signal (`Stage.Layout`), written only on a real change; every DIP comes from it.
//   • ONE cover node (`stage:cover`) morphs between the hero and the thumb with a Layout FLIP; the title column follows.
//   • The accent is PER SURFACE: `AccentSet.From(cover accent)` into control Styles; big fills bind `slab.Accent`, which the
//     clock cross-fades (a bound channel snaps — §1.8). `Tok.SetAccent` is never called.
//   • Idle is the engine's PlayerChromeVisibility on the host timer clock, one timer re-armed from NextWakeMs; chrome UNMOUNTS
//     through Flow.Show whose child BoxEls carry the Enter/Exit (never alpha 0); the cursor hides through SetCursorOverride;
//     a hairline progress bar shows while hidden.
//   • ZStack placement: vertical = AlignSelf, horizontal = JustifySelf (FlexLayout.cs:1279-1286). Full-bleed wrappers over
//     interactive content are HitTestPassThrough, never HitTestVisible = false (which prunes the subtree).
//   • Preferences are read ONCE per epoch into the context's signals; no tick, bind thunk or child render touches Prefs.Stage.
//   • Nothing here decides: modes, aspect classes, demand, visuals are `Stage` (CORE) and `Visualizer` (CORE).
//
// Mounted from other files: `Lyrics.StagePane(accent)` (the reading column), `Stage.QueuePaneBody` (Queue.UI.cs:65),
// `Stage.NowPlayingMenu` (Actions.UI.cs:958), `Shell.SeekBar(onScrubbing:)`/`Shell.TimeText` (the bar's own seek — never a fork),
// `Shell.PlayerKey`/`Shell.BarSeekBy` (the bar's key map).

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Stage
{
    // ══ 0. MOUNT POINT + SEAMS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The fullscreen stage. Mount it while <c>Shell.Ui.ImmersiveLyrics</c> is true (Shell.UI.cs:500).</summary>
    public static Element View() => Embed.Comp(static () => new SurfaceCore());

    public static EnterExit EnterTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.03f, Sy: 1.03f, Opacity: 0f, Active: true);
    public static EnterExit ExitTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.02f, Sy: 1.02f, Opacity: 0f, Active: true);

    /// <summary>The Up-next rows (Queue.UI.cs:65 installs it). Null renders the empty sentence.</summary>
    public static Func<Element>? QueuePaneBody { get; set; }
    /// <summary>The now-playing context menu (Actions.UI.cs:958 installs it); asked at OPEN time.</summary>
    public static Func<ContextMenuModel?>? NowPlayingMenu { get; set; }

    /// <summary>The accent TARGET for the playing cover. The clock eases <c>Slab.Accent</c> toward it (Stage.Tone.CrossFadeMs).</summary>
    public static readonly Signal<ColorF> AccentSignal = new(ColorF.FromRgba(0xff, 0x9e, 0xc4));

    /// <summary>What every stage child reads — ambient, many consumers ⇒ context (component-props-contract §3). ONE instance for
    /// the stage's life (SurfaceCore's field), holding SIGNALS: a consumer re-renders on the signals it reads, never on
    /// SurfaceCore's own renders (V-U50). Every preference is mirrored here by SurfaceCore's one epoch effect (O8); the
    /// playing track is ONE key signal (slot ≪ 32 | row version — the §2 narrowing, V-U34). The actions are set once in
    /// SurfaceCore's constructor.</summary>
    public sealed class StageCtx
    {
        public readonly Signal<Layout> Layout = new(Stage.Layout.Seed(1920f, 1080f));
        public readonly Signal<Mode> Mode = new(Stage.Mode.Lyrics);
        public readonly Signal<Visualizer.Kind> Kind = new(Visualizer.Kind.Horizon);
        public readonly Signal<bool> GalleryOpen = new(true), LyricsOverlay = new(true), Calm = new(false);
        public readonly FloatSignal Sensitivity = new(1f), SyncOffsetMs = new(0f);
        /// <summary>The chrome is mounted (the idle machine's output) · the playing track has timed lyrics (LyricFacts writes it).</summary>
        public readonly Signal<bool> Chrome = new(true), HasTimedLyrics = new(false);
        public readonly Signal<AccentSet> Accent = new(AccentSet.From(ColorF.FromRgba(0xff, 0x9e, 0xc4)));
        public readonly Signal<Visualizer.Palette> Palette = new(Visualizer.Palette.From(ColorF.FromRgba(0xff, 0x9e, 0xc4), null));
        /// <summary>(slot ≪ 32) | the row's Version for the playing TRACK; 0 = nothing playing / not a track.</summary>
        public readonly Signal<long> TrackKey = new(0L);
        /// <summary>The gallery toggle's realized node — the TeachingTip's anchor (V-U16).</summary>
        public readonly Signal<NodeHandle> GalleryButton = new(default);
        public readonly Visualizer.Slab Slab = new();
        public Action<Mode> SetMode = static _ => { };
        public Action ToggleGallery = static () => { }, Exit = static () => { }, Activity = static () => { };
        /// <summary>The idle machine's holds: pointer over a control · a menu/tip/flyout open (counted) · a seek scrub.</summary>
        public Action<bool> OverControls = static _ => { }, MenuOpen = static _ => { }, Scrubbing = static _ => { };
        /// <summary>The playing track's row from the key — <c>Value</c> subscribes the caller to the KEY (one write per slot/version change).</summary>
        public Track RowValue() { int slot = (int)(TrackKey.Value >> 32); return slot > 0 ? new Track(slot) : default; }
        public Track RowPeek() { int slot = (int)(TrackKey.Peek() >> 32); return slot > 0 ? new Track(slot) : default; }
    }
    public static readonly Context<StageCtx?> StageContext = new(null);

    /// <summary>Enter from any entry point. <paramref name="begin"/> is the caller's <c>UseContext(SharedTransition.Begin)</c>
    /// (null when headless): the bar art's flight is captured BEFORE the mount so the hero receives it. Nothing playing opens
    /// an EMPTY stage, exactly as the rail ⛶ does today (V-U53).</summary>
    public static void Open(Action<string>? begin, string cause)
    {
        if (!Entry.CanEnter(Video.State.Resolved == Video.SurfacePlacement.Fullscreen)) return;
        if (Shell.Ui.ImmersiveLyrics.Peek()) return;
        begin?.Invoke(Entry.MorphKey);
        Shell.Ui.ImmersiveLyrics.Value = true;
        Diagnostics.NoteEnter(cause);
    }

    /// <summary>Exit. The explicit <c>Begin</c> is REQUIRED: reverse capture is not implemented (ConnectedAnimation.cs:238-241),
    /// so the hero is captured here and the re-mounted bar art receives the flight.</summary>
    public static void Close(Action<string>? begin, string cause)
    {
        if (!Shell.Ui.ImmersiveLyrics.Peek()) return;
        begin?.Invoke(Entry.MorphKey);
        Shell.Ui.ImmersiveLyrics.Value = false;
        Diagnostics.NoteExit(cause);
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };
    const string DisplayFace = "Segoe UI Variable Display";

    // ══ 1. THE SURFACE ══════════════════════════════════════════════════════════════════════════════════════════════

    sealed class SurfaceCore : Component
    {
        readonly StageCtx _ctx = new();
        readonly Signal<int> _selector = new(0);
        readonly FloatSignal _frac = new(0f);
        readonly Action _onWake;
        PlayerChromeVisibility? _idle;        // created in Render with the host timer clock (MediaPlayerElement.cs:729-730) — never with 0 (V-U10)
        TimerHandle _wake;
        double _armedDueMs = double.PositiveInfinity;
        int _menusOpen;                       // the device flyout, the "…" menu and the TeachingTip each count one (V-U22)
        InputHooks? _hooks;
        Action<string>? _begin;
        bool _cursorHidden;
        OverlayHandle? _tip;

        public SurfaceCore()
        {
            _onWake = () => { _armedDueMs = double.PositiveInfinity; Sync(); };
            _ctx.SetMode = m => { Prefs.Stage.SetMode((int)m); Diagnostics.NoteMode(m); };
            _ctx.ToggleGallery = () =>
            {
                var (mode, open) = ModeRules.ToggleGallery(_ctx.Mode.Peek(), _ctx.GalleryOpen.Peek());
                if (mode != _ctx.Mode.Peek()) Prefs.Stage.SetMode((int)mode);
                Prefs.Stage.SetGalleryOpen(open);
            };
            _ctx.Exit = () => Close(_begin, "exit-button");
            _ctx.Activity = () => { _idle?.Activity(ChromeActivity.Pointer, Now()); Sync(); };
            _ctx.OverControls = over => { _idle?.SetPointerOverControls(over, Now()); Sync(); };
            _ctx.MenuOpen = open => { _menusOpen = Math.Max(0, _menusOpen + (open ? 1 : -1)); _idle?.SetMenuOpen(_menusOpen > 0, Now()); Sync(); };
            _ctx.Scrubbing = on => { _idle?.SetScrubbing(on, Now()); Sync(); };
        }

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            _hooks = hooks;
            var overlay = UseContext(Overlay.Service);
            _begin = UseContext(SharedTransition.Begin);
            var vp = UseContextSignal(Viewport.Size);
            var ctx = _ctx;

            // ── the ONE layout signal, equality-gated (Signal<T>.SetIfChanged) ──
            UseSignalEffect(() => { var size = vp.Value; ctx.Layout.SetIfChanged(Layout.Resolve(size.Width, size.Height, ctx.Layout.Peek())); });

            // ── EVERY preference, once per Prefs.Stage epoch, into the context's signals (O8) — the only Prefs.Stage reads on the stage ──
            UseSignalEffect(() =>
            {
                var m = (Mode)Prefs.Stage.Mode();
                ctx.Mode.SetIfChanged(m);
                _selector.SetIfChanged((int)m);
                ctx.Kind.SetIfChanged((Visualizer.Kind)Prefs.Stage.Visualizer());
                ctx.GalleryOpen.SetIfChanged(Prefs.Stage.GalleryOpen());
                ctx.LyricsOverlay.SetIfChanged(Prefs.Stage.LyricsOverlay());
                ctx.Calm.SetIfChanged(Prefs.Stage.Calm());
                ctx.Sensitivity.SetIfChanged(Prefs.Stage.Sensitivity());
                ctx.SyncOffsetMs.SetIfChanged(Prefs.Stage.SyncOffsetMs());
            });

            // ── the playing ROW, narrowed (§2 perf fix): ONE subscription to Tracks.Changed, ONE write when the slot or its
            //    Version moved; every consumer reads ctx.TrackKey and re-renders only then (V-U12: no hook inside a factory) ──
            UseSignalEffect(() =>
            {
                var r = Playback.Current.Value;
                _ = Entities.Current.Tracks.Changed.Value;
                long key = 0L;
                if (r.Kind == EntityKind.Track && !r.IsNone)
                {
                    var t = Entities.Current.Tracks;
                    uint version = (uint)r.Slot < (uint)t.Count ? t.Version[r.Slot] : 0u;
                    key = ((long)r.Slot << 32) | version;
                }
                ctx.TrackKey.SetIfChanged(key);
            });
            var track = ctx.RowValue();

            // ── REAL fullscreen with prior-state restore (Video.UI.cs:1213-1218) ──
            var priorFs = UseRef(false);
            UseLayoutEffect(() =>
            {
                priorFs.Value = hooks.IsWindowFullscreen?.Invoke() ?? false;
                if (!priorFs.Value) hooks.WindowSetFullscreen?.Invoke(true);
                return () => { if (!priorFs.Value) hooks.WindowSetFullscreen?.Invoke(false); };
            }, DepKey.Empty);

            // ── a focus scope at the root; focus returns to whoever opened the stage (Video.UI.cs:1221-1236) ──
            var priorFocus = UseRef<NodeHandle>(default);
            UseLayoutEffect(() =>
            {
                if (Context.HostNode.IsNull) return null;
                var root = Context.HostNode;
                priorFocus.Value = hooks.GetFocus?.Invoke() ?? default;
                hooks.PushFocusScope?.Invoke(root);
                hooks.FocusNode?.Invoke(root, false);
                return () =>
                {
                    hooks.PopFocusScope?.Invoke(root);
                    var back = priorFocus.Value;
                    priorFocus.Value = default;
                    if (!back.IsNull) hooks.RestoreFocus?.Invoke(back);
                };
            }, DepKey.Empty);

            // ── unmount: release the cursor override, close the tip ──
            UseEffect(() => () =>
            {
                if (_cursorHidden) { hooks.SetCursorOverride?.Invoke(this, null); _cursorHidden = false; }
                _tip?.Close();
                _tip = null;
            }, DepKey.Empty);

            // ── the accent set + palette, derived once per (cover, theme); the TARGET the clock cross-fades toward ──
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            if (url.Length > 0) _ = Palette.Watch(url).Value;
            int themeEpoch = Tok.Epoch;                    // RethemeAll re-renders this component on a theme flip (§1.3)
            ColorF accentBase = Ink.Accent(url);
            UseEffect(() =>
            {
                ctx.Accent.Value = AccentSet.From(accentBase);
                ctx.Palette.Value = Visualizer.Palette.From(accentBase, Design.SchemeFor(url));
                AccentSignal.SetIfChanged(accentBase);
            }, DepKey.From(url.GetHashCode(), themeEpoch, accentBase.GetHashCode(), 0));   // four ints: there is no 3-int DepKey (V-U3)

            // ── idle: the engine machine on the HOST TIMER clock, one timer re-armed from NextWakeMs (MediaPlayerElement.cs:390-416, :726-730) ──
            _wake = UseTimeout(_onWake, 1_000f, DepKey.Empty);                           // arms once at mount — harmless (Sync re-arms)
            var idle = _idle ??= new PlayerChromeVisibility(PlayerChromeTiming.Default, _wake.NowMs);
            bool playing = Playback.IsPlaying.Value;
            UseEffect(() => { idle.SetPlayback(playing ? ChromePlayback.Playing : ChromePlayback.Paused, Now()); idle.SetCursorMayHide(true, Now()); Sync(); }, DepKey.From(playing));

            // ── the hairline fraction from the coarse report (1 Hz is plenty for a 3-DIP line) ──
            UseSignalEffect(() => { int d = Playback.DurationMs.Value, p = Playback.PositionMs.Value; _frac.SetIfChanged(d > 0 ? Math.Clamp(p / (float)d, 0f, 1f) : 0f); });

            // ── the TeachingTip, once: anchored to the gallery toggle's realized node (a SIGNAL, V-U16); it holds the chrome while open ──
            UseSignalEffect(() =>
            {
                var anchor = ctx.GalleryButton.Value;
                if (ctx.Mode.Value != Mode.Visualizer || anchor.IsNull || _tip is not null || Prefs.Stage.TipSeen()) return;
                ctx.MenuOpen(true);
                _tip = TeachingTip.Show(overlay, () => ctx.GalleryButton.Peek(), tip =>
                {
                    tip.Title = Loc.Get(Strings.Stage.Tip.Title);
                    tip.Body = Loc.Get(Strings.Stage.Tip.Body);
                    tip.ActionButtonContent = Loc.Get(Strings.Stage.Tip.GotIt);
                    tip.ActionButtonIsAccent = true;
                    tip.ActionButtonClick = () => _tip?.Close();
                    tip.PreferredPlacement = TeachingTip.PlacementMode.Bottom;   // nested enum (TeachingTip.cs:48, V-U5)
                    tip.Closed = _ => { Prefs.Stage.SetTipSeen(); _tip = null; ctx.MenuOpen(false); };
                });
            });

            var L = ctx.Layout.Value;
            var fade = new EnterExit(Opacity: 0f, Active: true);
            return Ctx.Provide(StageContext, ctx, new BoxEl
            {
                Width = L.W, Height = L.H, ZStack = true, ClipToBounds = true, Focusable = true, Fill = Ink.Floor,   // explicit size (V-U49)
                OnHoverMove = p => { idle.PointerMoved(p.X, p.Y, Now()); Sync(); },
                OnPointerExit = () => { idle.PointerLeft(Now()); Sync(); },
                OnPointerWheel = _ => { idle.Activity(ChromeActivity.Pointer, Now()); Sync(); },   // wheel = activity (V-U22)
                OnKeyDown = OnKey,
                Children =
                [
                    Embed.Comp(static () => new Backdrop()) with { Key = "stage:backdrop" },
                    // the face: the Show's child carries its own Enter/Exit (the bare-ComponentEl rule, §1.8 — V-U31); FaceHost keys a CHILD per kind
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer, new BoxEl
                    {
                        Key = "stage:face", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Enter = new EnterExit(Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.StandardEnter,
                        Children = [Embed.Comp(static () => new FaceHost())],
                    }),
                    // bottom smoke under the face: BOTTOM (AlignSelf) across the width (JustifySelf) — V-U9
                    new BoxEl
                    {
                        Key = "stage:smoke", Height = Tone.SmokeH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(Tone.SmokeA))),
                        Visible = Prop.Of(() => ctx.Mode.Value == Mode.Visualizer),
                    },
                    Embed.Comp(static () => new NowPlayingCard()) with { Key = "stage:npc" },
                    Embed.Comp(static () => new Hero()) with { Key = "stage:identity" },
                    Embed.Comp(static () => new PaneHost()) with { Key = "stage:pane" },
                    Flow.Show(() => ModeRules.ShowsCaption(ctx.Mode.Value, ctx.LyricsOverlay.Value, ctx.HasTimedLyrics.Value), new BoxEl
                    {
                        Key = "stage:caption", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Margin = new Edges4(80f, 0f, 80f, L.TransportH + 2f * Layout.Pad + 16f), AlignItems = FlexAlign.Center,
                        Enter = new EnterExit(Dy: 12f, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.ControlNormal,
                        Children = [Embed.Comp(static () => new CaptionHost())],
                    }),
                    Embed.Comp(static () => new ChromeHost()) with { Key = "stage:chrome" },
                    // the hairline while the chrome is hidden: BOTTOM edge, full width; progress = a bound ScaleX from the left (V-U49)
                    Flow.Show(() => !ctx.Chrome.Value, new BoxEl
                    {
                        Key = "stage:hairline", Height = Layout.HairlineH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                        Children = [new BoxEl { Grow = 1f, Height = Layout.HairlineH, Fill = Prop.Bind(ctx.Slab.Accent), TransformOriginX = 0f, TransformOriginY = 0.5f,
                                                Transform = Prop.Of(() => Affine2D.Scale(_frac.Value, 1f)) }],
                    }),
                    Embed.Comp(static () => new LyricFacts()) with { Key = "stage:facts" },
                    Embed.Comp(static () => new Visualizer.Clock()) with { Key = "stage:clock" },
                ],
            });
        }

        /// <summary>Esc / F11 close; everything else is the player bar's own key map (Shell.PlayerBar.UI.cs:645-659 via
        /// Shell.PlayerKey, Shell.PlayerBar.cs:448-459) so Space / ← → / ↑ ↓ work in fullscreen (O9). Media keys need nothing:
        /// they arrive through SMTC regardless of focus (Playback.Os.cs:309-323). Any key is idle activity.</summary>
        void OnKey(KeyEventArgs e)
        {
            _idle?.Activity(ChromeActivity.Keyboard, Now());
            Sync();
            if (e.Handled) return;
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.Escape) { e.Handled = true; Close(_begin, "escape"); return; }
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.F11) { e.Handled = true; Close(_begin, "f11"); return; }
            var intent = Shell.PlayerKey(e.KeyCode, focusedContainer: true, e.Handled, e.Ctrl || e.Alt || e.Shift);
            if (intent == Shell.PlayerKeyIntent.None) return;
            e.Handled = true;
            switch (intent)
            {
                case Shell.PlayerKeyIntent.SeekBack: Shell.BarSeekBy(-10_000); break;
                case Shell.PlayerKeyIntent.SeekForward: Shell.BarSeekBy(10_000); break;
                case Shell.PlayerKeyIntent.VolumeDown: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() - .05f, 0f, 1f)); break;
                case Shell.PlayerKeyIntent.VolumeUp: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() + .05f, 0f, 1f)); break;
                case Shell.PlayerKeyIntent.Toggle: if (!e.IsRepeat) Shell.TogglePlayPause("stage.space"); break;
            }
        }

        /// <summary>MediaPlayerElement.Sync (:390-416), verbatim in shape: tick (the bool is IGNORED — V-U10), publish the chrome
        /// value-gated, apply the cursor, re-arm the one timer at NextWakeMs (+∞ = nothing pending, no timer — V-U48).</summary>
        void Sync()
        {
            if (_idle is not { } vis) return;
            double now = _wake.NowMs;
            vis.Tick(now);
            _ctx.Chrome.SetIfChanged(vis.ChromeVisible);
            if (vis.CursorHidden != _cursorHidden)
            {
                _cursorHidden = vis.CursorHidden;
                _hooks?.SetCursorOverride?.Invoke(this, _cursorHidden ? CursorId.Hidden : null);
            }
            double due = vis.NextWakeMs;
            if (double.IsPositiveInfinity(due)) { _armedDueMs = double.PositiveInfinity; return; }
            if (due < _armedDueMs - 1.0)
            {
                _armedDueMs = due;
                _wake.RestartIn((float)Math.Max(0.0, due - now));
            }
        }

        double Now() => _wake.NowMs;

    }

    // ══ 1b. THE LAYERS (each a component on the context's SIGNALS — SurfaceCore's tree is static, V-U50) ═════════════

    /// <summary>Backdrop: the baked cover (old layer kept under the new), the base Field, the scrim.</summary>
    sealed class Backdrop : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var mode = ctx.Mode.Value;
            var pal = ctx.Palette.Value;
            var track = ctx.RowValue();
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    Embed.Comp(new BackdropArt.Props(url), static () => new BackdropArt()),
                    Visualizer.FieldFace(ctx.Slab, in pal, new Visualizer.FaceSpec(L.W, L.H, Preview: false, CoverUrl: null), opacity: ctx.Slab.BaseFieldOp) with { Key = "stage:field" },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Fill = Shade(mode == Mode.Visualizer ? Tone.ScrimVisualizerA : Tone.ScrimA), BrushTransitionMs = Tone.CrossFadeMs,   // a static fill cross-fades; a bound one would snap
                    },
                ],
            };
        }
    }

    /// <summary>The morphing hero + its title column (one node each, Layout FLIP). The wrapper is PASS-THROUGH, not
    /// hit-invisible: the MetaLink inside must stay clickable (V-U8).</summary>
    sealed class Hero : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var mode = ctx.Mode.Value;
            var set = ctx.Accent.Value;
            var track = ctx.RowValue();
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            float size = L.CoverSize(mode);
            float corners = mode == Mode.Visualizer ? Radii.Control : Radii.Card;
            var flip = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Spring(0.45f, 0.90f), SizeMode.ScaleCorrect);
            var move = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(0.45f, 0.90f));
            var (ts, tl) = L.TitleFont(mode);
            string uri = track.IsValid ? track.Uri.Text : "";
            string title = track.IsValid ? track.Title : "";
            string shown = Transport.UsesTitle(title, uri) ? title : Loc.Get(Strings.Player.NothingPlaying);
            var titleKids = new List<Element>(3)
            {
                new TextEl(shown) { Size = ts, LineHeight = tl, Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Embed.Comp(new MetaLink.Props(L.MetaFont(mode).Size, L.MetaFont(mode).Line, set.Text), static () => new MetaLink()),
            };
            if (L.ShowChips) titleKids.Add(Embed.Comp(static () => new Chips()) with { Key = "stage:chips" });
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:cover", Width = size, Height = size, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(L.CoverX(mode), L.CoverY(mode), 0f, 0f), Corners = CornerRadius4.All(corners), Shadow = Elevation.Dialog, ClipToBounds = true,
                        Layout = flip, HitTestVisible = false,
                        Children = [Controls.Artwork(url.Length > 0 ? url : null, size, size, corners, morphKey: Entry.MorphKey, decodePx: 512)],
                    },
                    new BoxEl
                    {
                        Key = "stage:titles", Direction = 1, Gap = Spacing.XS, Width = L.TitleW(mode), AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(L.TitleX(mode), L.TitleY(mode), 0f, 0f), Layout = move,
                        Children = titleKids.ToArray(),
                    },
                ],
            };
        }
    }

    /// <summary>The format chip, "Synced lyrics", and (outside Visualizer mode) BPM from the Audio group. Its own component so
    /// the format / device / owner signals re-render THIS row only (V-U50).</summary>
    sealed class Chips : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var mode = ctx.Mode.Value;
            var track = ctx.RowValue();
            bool hasTimed = ctx.HasTimedLyrics.Value;
            var kids = new List<Element>(4);
            var fmt = Playback.StreamFormat.Value;
            bool remote = Shell.DevicePicker.IsRemote(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows);
            if (Transport.ShowsQualityBadge(!fmt.IsEmpty, remote)) kids.Add(Chip(Icons.Equalizer, Entities.Strings.Resolve(fmt)));
            if (hasTimed) kids.Add(Chip(Icons.Document, Loc.Get(Strings.Stage.SyncedLyrics)));
            if (mode != Mode.Visualizer && track.IsValid && track.Knows(TrackFields.Audio) && track.Tempo > 0)
                kids.Add(Chip(null, Strings.Stage.Bpm(((track.Tempo + 5) / 10).ToString(System.Globalization.CultureInfo.InvariantCulture))));   // a formatted key: the generated method already formats (V-D15)
            return new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Margin = new Edges4(0f, Spacing.S, 0f, 0f), HitTestVisible = false, Children = kids.ToArray() };
        }

        static BoxEl Chip(string? glyph, string text) => new()
        {
            Height = 24f, Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Shrink = 0f,
            Corners = Radii.ControlAll, Fill = Ink.GlassRest, BorderWidth = 1f, BorderColor = Ink.Stroke,
            Children = glyph is null
                ? [new TextEl(text) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary }]
                : [new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary }, new TextEl(text) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary }],
        };
    }

    /// <summary>The acrylic now-playing card behind the thumb (Visualizer mode): TOP-LEFT (AlignSelf Start · JustifySelf Start).</summary>
    sealed class NowPlayingCard : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Children =
                [
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && ctx.Layout.Value.ShowChips, new BoxEl
                    {
                        Width = Layout.NowPlayingCardW, Height = Layout.NowPlayingCardH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(Layout.NowPlayingCardX, Layout.NowPlayingCardY, 0f, 0f), Corners = Radii.CardAll,
                        Acrylic = AcrylicSpec.InAppBase, Shadow = Elevation.Card, HitTestVisible = false,
                        Enter = new EnterExit(Dy: -8f, Opacity: 0f, Active: true, DelayMs: 150f), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                    }),
                ],
            };
        }
    }

    /// <summary>The pane region: KeepAlive keeps the lyrics view's state across mode switches. TOP-LEFT at (PaneX, PaneTop).</summary>
    sealed class PaneHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var slab = ctx.Slab;
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = L.PaneW, Height = L.PaneH, ClipToBounds = true,
                Margin = new Edges4(L.PaneX, L.PaneTop, 0f, 0f),
                Visible = Prop.Of(() => ctx.Layout.Value.ShowPane && ModeRules.ShowsPane(ctx.Mode.Value)),
                Children =
                [
                    Flow.KeepAlive(() => ctx.Mode.Value, static m => "pane:" + (int)m, m => m switch
                    {
                        Mode.Lyrics => PaneFrame(Lyrics.StagePane(slab.Accent)),
                        Mode.Queue => PaneFrame(Embed.Comp(static () => new QueuePane())),
                        Mode.Artist => PaneFrame(Embed.Comp(static () => new ArtistPane())),
                        _ => new BoxEl { HitTestVisible = false },
                    }),
                ],
            };
        }

        static Element PaneFrame(Element body) => new BoxEl
        {
            Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(500f, Easing.FluentDecelerate),
                Enter: new EnterExit(Dy: 40f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
            Children = [body],
        };
    }

    /// <summary>Writes <c>ctx.HasTimedLyrics</c> and keeps the playing track's lyrics ensured — the ONE reader of
    /// <c>Lyrics.Store.Changed</c> on the stage (V-U50: the surface itself never subscribes to it).</summary>
    sealed class LyricFacts : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _ = Lyrics.Store.Changed.Value;
            var track = ctx.RowValue();
            bool hasTimed = track.IsValid && Lyrics.Store.Doc(track) is { IsSynced: true, Lines.Count: > 0 };
            UseEffect(() => ctx.HasTimedLyrics.SetIfChanged(hasTimed), DepKey.From(hasTimed));
            UseEffect(() => { if (track.IsValid) Lyrics.Store.Ensure(track); }, DepKey.From(track.Slot));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    // ══ 2. THE BACKDROP ART (the crisp-flash fix) ═══════════════════════════════════════════════════════════════════

    /// <summary>The blurred cover is baked ONCE per track (BakedBlurSpec) and the PREVIOUS cover stays mounted under
    /// the new one, so a track change fades blur-to-blur and never shows a crisp placeholder.</summary>
    sealed class BackdropArt : Component
    {
        public sealed record Props(string Url);
        string _shown = "", _previous = "";
        public override Element Render()
        {
            var p = UseProps<Props>();
            if (p.Url != _shown) { _previous = _shown; _shown = p.Url; }
            var kids = new List<Element>(2);
            if (_previous.Length > 0) kids.Add(Cover(_previous) with { Key = "bd:" + _previous });
            if (_shown.Length > 0) kids.Add(Cover(_shown) with { Key = "bd:" + _shown });
            return new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestVisible = false, Children = kids.ToArray() };
        }
        static ImageEl Cover(string url) => Ui.Image(url, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f, placeholder: Ink.ArtStandIn(url),
                                                      transition: ImageTransition.Fade(Ink.CrossFadeMs)) with
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, BakedBlur = new BakedBlurSpec(80f, 0.5f),
        };
    }

    // ══ 3. THE FACE HOST ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chosen face, remounted per kind on a KEYED CHILD (a Key on a component's root is inert in a single-child slot —
    /// V-U11), inset on the right while the gallery is open; a 0.55 s scale/fade per kind. Reads the context's signals only (O8).</summary>
    sealed class FaceHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var kind = ctx.Kind.Value;
            var pal = ctx.Palette.Value;
            float w = L.W - L.FaceRight(ctx.GalleryOpen.Value);
            var track = ctx.RowValue();
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            return new BoxEl
            {
                Width = w, Height = L.H, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "viz:" + (int)kind, Width = w, Height = L.H,
                        Animate = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(550f, Easing.FluentDecelerate),
                            Enter: new EnterExit(Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
                        Children = [Visualizer.Face(kind, ctx.Slab, in pal, new Visualizer.FaceSpec(w, L.H, Preview: false, CoverUrl: url.Length > 0 ? url : null))],
                    },
                ],
            };
        }
    }

    // ══ 4. THE OVERLAY CAPTION (Visualizer mode) ════════════════════════════════════════════════════════════════════

    /// <summary>The active lyric line over the face: resolved on a 10 Hz interval with the lead the lyrics view uses
    /// (Lyrics.ResolveLine, LeadMs), keyed per line so each line enters with the prototype's rise.</summary>
    sealed class CaptionHost : Component
    {
        readonly Signal<int> _active = new(-1);
        readonly Action _tick;
        Deck.PositionInterpolator _pos;
        bool _anchored, _advancing;
        int _lastReport = int.MinValue;
        Lyrics.Doc? _doc;

        public CaptionHost() => _tick = () => Reactive.Untrack(Tick);

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _ = Lyrics.Store.Changed.Value;
            var track = ctx.RowValue();
            _doc = track.IsValid ? Lyrics.Store.Doc(track) : null;
            UseSignalEffect(() =>
            {
                int pos = Playback.PositionMs.Value;
                _advancing = Playback.IsPlaying.Value && !Playback.Buffering.Value;
                if (pos != _lastReport || !_anchored) { _pos.Anchor(Design.FrameTime.NowMs, pos); _anchored = true; _lastReport = pos; }
            });
            UseInterval(_tick, 100f, enabled: _doc is not null);
            int active = _active.Value;
            var line = _doc is { } d && (uint)active < (uint)d.Lines.Count ? d.Lines[active] : null;
            // the root is a stable column; the KEYED CHILD remounts per line so each line enters with the rise (V-U14: a root Key is inert)
            var root = new BoxEl { Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false };
            if (line is null) return root;
            string? secondary = Prefs.Lyrics.SecondaryLine() switch { Prefs.Lyrics.Translation => line.Translation, Prefs.Lyrics.Romanization => line.Romanization, _ => null };
            var kids = new List<Element>(2)
            {
                new TextEl(line.Text) { Size = 36f, LineHeight = 44f, Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MaxWidth = 1100f },
            };
            if (secondary is { Length: > 0 }) kids.Add(new TextEl(secondary) { Size = 18f, LineHeight = 24f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, MaxWidth = 1100f });
            return root with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "caption:" + active, Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center,
                        Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(500f, Easing.FluentDecelerate), Enter: new EnterExit(Dy: 12f, Opacity: 0f, Active: true)),
                        Children = kids.ToArray(),
                    },
                ],
            };
        }

        void Tick()
        {
            if (_doc is not { } d || !_anchored) return;
            long pos = _pos.Estimate(Design.FrameTime.NowMs, _advancing, null, null, Playback.DurationMs.Peek());
            int active = Lyrics.ResolveLine(d.Lines, pos + Lyrics.LeadMs);
            if (active != _active.Peek()) _active.Value = active;
        }
    }

    // ══ 5. THE CHROME (top bar · transport card · gallery) ══════════════════════════════════════════════════════════

    /// <summary>Always mounted; each chrome piece sits under its OWN Flow.Show whose child is a BoxEl carrying the Enter/Exit
    /// (a bare ComponentEl root never plays its exit, Reconciler.cs:4106-4137 — V-U31) and the ZStack placement
    /// (vertical = AlignSelf, horizontal = JustifySelf — V-U9). The wrapper is PASS-THROUGH so empty chrome area yields to the
    /// stage root's pointer handlers while the bars stay clickable (V-U8).</summary>
    sealed class ChromeHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            bool sheet = L.Aspect == Aspect.Portrait;
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestPassThrough = true,
                Children =
                [
                    // TOP bar: top edge, full width
                    Flow.Show(() => ctx.Chrome.Value, new BoxEl
                    {
                        Key = "chrome:top", Height = Layout.TopBarH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Direction = 0,
                        Enter = new EnterExit(Dy: -8f, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: -8f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                        Children = [Embed.Comp(static () => new TopBar())],
                    }),
                    // TRANSPORT card: bottom edge; full width at Pad, or right of the art on Compact (Layout.TransportLeft — V-U23)
                    Flow.Show(() => ctx.Chrome.Value, new BoxEl
                    {
                        Key = "chrome:transport", Height = L.TransportH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0,
                        Margin = new Edges4(L.TransportLeft, 0f, Layout.Pad, Layout.Pad),
                        Enter = new EnterExit(Dy: 8f, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: 8f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                        Children = [Embed.Comp(static () => new TransportCard())],
                    }),
                    // GALLERY pane: a right-docked column (top-aligned, JustifySelf End) or Portrait's bottom sheet (AlignSelf End, full width)
                    Flow.Show(() => ctx.Chrome.Value && ctx.Mode.Value == Mode.Visualizer && ctx.GalleryOpen.Value && ctx.Layout.Value.ShowGallery, new BoxEl
                    {
                        Key = "chrome:gallery", Direction = 0,
                        AlignSelf = sheet ? FlexAlign.End : FlexAlign.Start, JustifySelf = sheet ? FlexAlign.Stretch : FlexAlign.End,
                        Margin = sheet ? new Edges4(0f, 0f, 0f, L.TransportH + 2f * Layout.Pad) : new Edges4(0f, Layout.GalleryTop, Layout.GalleryRight, Layout.GalleryBottom),
                        Enter = new EnterExit(Dx: sheet ? 0f : 32f, Dy: sheet ? 32f : 0f, Opacity: 0f, Active: true), Exit = new EnterExit(Dx: sheet ? 0f : 32f, Dy: sheet ? 32f : 0f, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                        Children = [Embed.Comp(static () => new GalleryHost())],
                    }),
                ],
            };
        }
    }

    /// <summary>48 DIP: brand · the SelectorBar (pill tinted through TemplateParts) · gallery toggle · exit. Fills its wrapper (Grow).</summary>
    sealed class TopBar : Component
    {
        readonly Signal<int> _index = new(0);
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var set = ctx.Accent.Value;
            UseSignalEffect(() => _index.SetIfChanged((int)ctx.Mode.Value));
            var parts = UseMemo(() => { var p = new TemplateParts(); p.Set<BoxEl>(SelectorBar.PartPill, b => b with { Fill = set.Fill }); return p; }, DepKey.From(set.GetHashCode()));
            string[] items = L.IconOnlySelector ? ["", "", "", ""] : [Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)];
            string?[] icons = [Icons.Document, Icons.Equalizer, Icons.Queue, Icons.Contact];
            var style = new SelectorBarStyle { RestColor = Ink.InkSecondary, SelectedColor = Ink.Ink, HoverColor = Ink.Ink, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, SelectedWeight = 600, ItemHeight = 36f, ItemGap = 4f };
            bool galleryOn = ctx.Mode.Value == Mode.Visualizer && ctx.GalleryOpen.Value;
            return new BoxEl
            {
                Grow = 1f, Height = Layout.TopBarH, Direction = 0, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.L, 0f, Spacing.S, 0f), Gap = Spacing.S,
                Gradient = GradientDown(new GradientStop(0f, Shade(0.42f)), new GradientStop(1f, Shade(0f))),
                OnHoverMove = _ => ctx.OverControls(true), OnPointerExit = () => ctx.OverControls(false),
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        Children =
                        [
                            new BoxEl { Width = 18f, Height = 18f, Corners = CornerRadius4.All(4.5f), Fill = Prop.Bind(ctx.Slab.Accent), Shrink = 0f },
                            new TextEl("Wavee") { Size = 12f, LineHeight = 16f, Weight = 600, Color = Ink.Ink },
                            new TextEl(Loc.Get(Strings.Stage.Brand)) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                    SelectorBar.Create(items, _index, onChange: i => ctx.SetMode((Mode)Math.Clamp(i, 0, ModeRules.Count - 1)), parts: parts, icons: icons, style: style),
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XS,
                        Children =
                        [
                            ToolTip.Wrap(new BoxEl
                            {
                                Width = 40f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                                Fill = galleryOn ? Ink.GlassHover : Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                                OnClick = ctx.ToggleGallery, OnRealized = h => ctx.GalleryButton.SetIfChanged(h),   // the TeachingTip's anchor SIGNAL (V-U16)
                                Children = [new TextEl(Icons.Equalizer) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.Ink }],
                            }, Loc.Get(Strings.Stage.Gallery)),
                            Button.Create(Loc.Get(Strings.Stage.Exit), ctx.Exit, ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.BackToWindow),
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>The acrylic transport card: seek row (the bar's own seek + times) and the three clusters. Fills its wrapper (Grow);
    /// reads the playing row through the context's key (V-U34), never Tracks.Changed.</summary>
    sealed class TransportCard : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var set = ctx.Accent.Value;
            var overlay = UseContext(Overlay.Service);
            var track = ctx.RowValue();
            bool playing = Playback.IsPlaying.Value;
            bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
            var tf = Shell.TransportFacts();
            bool primary = Transport.PrimaryEnabled(track.IsValid, loading);
            var lib = Controls.Library;
            string uri = track.IsValid ? track.Uri.Text : "";
            string title = track.IsValid ? track.Title : "";
            bool saved = lib is not null && uri.Length > 0 && lib.IsSaved(uri);
            Action? like = lib is { } seam && uri.Length > 0 ? () => seam.ToggleSaved(uri, title) : null;
            var repeat = Playback.Repeat.Value;
            EntityId contextId = Playback.ContextUri.Value;
            Queue.ContextWire wire = Playback.ContextLabel.Value;
            string? source = Queue.ContextName(contextId, in wire);

            var right = new List<Element>(5) { Heart(saved, like, set.Fill), Embed.Comp(static () => new MuteGlyph()) };
            if (L.ShowVolume) right.Add(Embed.Comp(new VolumeRow.Props(set, 128f), static () => new VolumeRow()));
            right.Add(Embed.Comp(static () => new DeviceButton()));
            right.Add(new BoxEl
            {
                Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, ClickRequestsContext = true,
                Children = [new TextEl(Icons.More) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.Ink }],
            }.WithContextMenu(overlay,
                () => { var m = NowPlayingMenu?.Invoke(); if (m is not null) ctx.MenuOpen(true); return m; },           // the "…" menu holds the chrome (V-U22)
                new ContextMenuOptions { OnClosed = _ => ctx.MenuOpen(false) }));                                       // ContextMenu.cs:43

            return new BoxEl
            {
                Grow = 1f, Height = L.TransportH, Direction = 1, Padding = new Edges4(20f, 10f, 20f, 0f), Corners = Radii.CardAll,
                Acrylic = AcrylicSpec.InAppBase, Shadow = Elevation.Card, ClipToBounds = true,
                OnHoverMove = _ => ctx.OverControls(true), OnPointerExit = () => ctx.OverControls(false),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        // the bar's own seek; its scrub edge holds the chrome through the new onScrubbing seam (§4.12 — a PlayerChromeFeed is inert without a MediaPlayerElement owner)
                        Children = [Shell.TimeText(remaining: false, ink: Ink.InkSecondary), new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [Shell.SeekBar(onScrubbing: ctx.Scrubbing)] }, Shell.TimeText(remaining: true, ink: Ink.InkSecondary)],
                    },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Height = 58f,
                        Children =
                        [
                            new BoxEl
                            {
                                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1,
                                Children =
                                [
                                    new TextEl(Strings.Stage.PlayingFrom(Queue.ContextKindLabel(contextId))) { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },   // a formatted key: the generated method formats; Loc.Get would render "[…]" (V-D15)
                                    new TextEl(source ?? "") { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                ],
                            },
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
                                Children =
                                [
                                    ToolTip.Wrap(Satellite(Icons.Shuffle, Shell.ToggleShuffle, tf.CanTransport, Playback.Shuffle.Value, set.Fill), Loc.Get(Strings.Player.Shuffle)),
                                    ToolTip.Wrap(Glyph(Icons.Previous, static () => Playback.Previous(), 40f, 17f, tf.PrevEnabled), Loc.Get(Strings.Player.Previous)),
                                    Play(playing, primary, 56f, set),
                                    ToolTip.Wrap(Glyph(Icons.Next, static () => Playback.Next(), 40f, 17f, tf.NextEnabled), Loc.Get(Strings.Player.Next)),
                                    ToolTip.Wrap(Satellite(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll, Shell.CycleRepeat, tf.CanTransport, repeat != RepeatMode.Off, set.Fill), Loc.Get(Strings.Player.Repeat)),
                                ],
                            },
                            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = 6f, Children = right.ToArray() },
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>The 56-DIP filled play disc: a hand-built circular BoxEl on the surface's AccentSet (Button.Create has no circular
    /// variant, so <c>ButtonPalette.ForAccent</c> is not used — V-U46).</summary>
    static Element Play(bool playing, bool enabled, float box, AccentSet set) => new BoxEl
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(box),
        Fill = enabled ? set.Fill : Ink.ScrimRest, HoverFill = enabled ? set.FillSecondary : Ink.ScrimRest, PressedFill = enabled ? set.FillTertiary : Ink.ScrimRest,
        BrushTransitionMs = Design.Motion.Faster, Shadow = enabled ? Elevation.Card : null,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        OnClick = static () => Shell.TogglePlayPause("stage.button"), Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(playing ? Icons.Pause : Icons.Play) { Size = 22f, FontFamily = Theme.IconFont, Color = enabled ? set.Ink : Ink.InkTertiary }],
    };

    /// <summary>A plateless 40-DIP glyph button (glass on hover).</summary>
    static BoxEl Glyph(string glyph, Action? onClick, float box, float glyphSize, bool enabled = true, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = enabled ? Ink.GlassHover : Ink.GlassRest, PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Theme.IconFont, Color = enabled ? Ink.InkSecondary : Ink.InkTertiary, HoverColor = enabled ? Ink.Ink : Ink.InkTertiary }],
    };

    /// <summary>Shuffle / repeat: accent-filled while latched (the prototype's <c>.ab.chk</c>).</summary>
    static BoxEl Satellite(string glyph, Action onClick, bool enabled, bool latched, ColorF accent) => new()
    {
        Width = 40f, Height = 40f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = latched && enabled ? accent : Ink.GlassRest, HoverFill = latched && enabled ? accent with { A = 0.9f } : enabled ? Ink.GlassHover : Ink.GlassRest,
        PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = !enabled ? Ink.InkTertiary : latched ? ColorContrast.PickContrast(accent) : Ink.InkSecondary, HoverColor = !enabled ? Ink.InkTertiary : latched ? ColorContrast.PickContrast(accent) : Ink.Ink }],
    };

    static BoxEl Heart(bool saved, Action? onLike, ColorF accent) => new()
    {
        Width = 40f, Height = 40f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = onLike is null ? Ink.GlassRest : Ink.GlassHover, PressedFill = onLike is null ? Ink.GlassRest : Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = onLike is not null, AllowFocusOnInteraction = false, OnClick = onLike, Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
        Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = 18f, FontFamily = Theme.IconFont, Color = saved ? accent : Ink.InkSecondary, HoverColor = saved ? accent : Ink.Ink }] }],
    };

    sealed class MuteGlyph : Component
    {
        public override Element Render()
        {
            bool muted = Playback.Muted.Value;
            return ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, static () => Playback.ToggleMute(), 40f, 17f), Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>The stock Slider in the surface accent at a fixed length (re-pushed props: a new accent re-skins in place).</summary>
    sealed class VolumeRow : Component
    {
        public sealed record Props(AccentSet Set, float Length);
        static readonly Slider.SliderOptions Options = new() { ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" };
        readonly FloatSignal _level = new(Playback.Volume.Peek());
        public override Element Render()
        {
            var p = UseProps<Props>();
            UseSignalEffect(() => _level.SetIfChanged(Playback.Volume.Value));
            var style = Slider.DefaultStyle with
            {
                RailFill = ColorF.FromRgba(255, 255, 255, 139), RailFillDisabled = ColorF.FromRgba(255, 255, 255, 60),
                ValueFill = p.Set.Fill, ValueFillPointerOver = p.Set.FillSecondary, ValueFillPressed = p.Set.FillTertiary, ValueFillDisabled = p.Set.Fill with { A = 0.3f },
                ThumbRing = ColorF.FromRgba(0x45, 0x45, 0x45), ThumbFill = p.Set.Fill, ThumbFillPointerOver = p.Set.FillSecondary, ThumbFillPressed = p.Set.FillTertiary, ThumbFillDisabled = p.Set.Fill with { A = 0.3f },
                ThumbBorder = GradientSpec.Solid(Ink.Stroke),
            };
            return Slider.Create(_level, static v => Playback.SetVolume(v), Options, length: p.Length, thickness: 32f, style: style);
        }
    }

    /// <summary>"This PC ▾" — the two-section device picker over the SAME items the player bar builds (Shell.DevicePickerMenuItems).</summary>
    sealed class DeviceButton : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            _ = Playback.Devices.Changed.Value;
            var rows = Playback.Devices.Rows;
            int slot = Shell.DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
            string name = slot >= 0 ? rows[slot].Name ?? "" : Loc.Get(Strings.Player.SystemDefault);
            string glyph = slot >= 0 ? Icons.Devices : Icons.ThisPc;
            if (slot < 0)
            {
                string? selected = Playback.Audio.SelectedOutputId.Value;
                foreach (var d in Playback.Audio.Devices.Value)
                    if (selected is { Length: > 0 } && string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase)) { name = d.Name; break; }
            }
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                Playback.PickerOpened();
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(DeviceItems(), () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                ctx.MenuOpen(true);                                                          // the flyout holds the chrome (V-U22)
                opened.ClosedAction = () => { handle.Value = null; ctx.MenuOpen(false); };
                handle.Value = opened;
            }
            return new BoxEl
            {
                Height = 32f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = new Edges4(11f, 0f, 10f, 0f), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster, BorderWidth = 1f, BorderColor = Ink.Stroke,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h, MaxWidth = 220f,
                Children =
                [
                    new TextEl(glyph) { Size = 14f, FontFamily = Theme.IconFont, Color = Ink.Ink },
                    new TextEl(name) { Size = 14f, LineHeight = 20f, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary },
                ],
            };
        }
        static List<MenuFlyoutItem> DeviceItems()
            => Shell.DevicePickerMenuItems(Playback.OwnerSignal.Peek(), Playback.ActiveDeviceSlot.Peek(), Playback.Devices.Rows,
                Playback.Audio.Devices.Peek(), Playback.Audio.SelectedOutputId.Peek(), Playback.Audio.Supported.Peek(),
                Playback.Audio.PlayingOpened, Shell.PickerAskedQuality());
    }

    /// <summary>"Artist · Album" (the previous stage's MetaLink with re-pushed type + colour). Subscribed to ITS row through the
    /// context's key (V-U34) and to Albums.Changed for the album title. Navigation CLOSES the stage first (V-U29).</summary>
    sealed class MetaLink : Component
    {
        public sealed record Props(float Size, float Line, ColorF Accent);
        readonly Action _go;
        Action<string>? _begin;
        public MetaLink() => _go = Go;
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _begin = UseContext(SharedTransition.Begin);
            var p = UseProps<Props>();
            var hover = UseSignal(false);
            var track = ctx.RowValue();
            _ = Entities.Current.Albums.Changed.Value;
            string artists = track.IsValid ? Entities.Strings.Resolve(track.ArtistLineId) : "";
            string album = track.IsValid && track.Album.IsValid ? track.Album.Title : "";
            string line = artists.Length > 0 && album.Length > 0 ? artists + " · " + album : artists.Length > 0 ? artists : album;
            if (line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            bool enabled = Shell.LinkFor(track, Shell.LinkSlot.Artist).Kind != Shell.RouteKind.NotFound;
            bool lit = enabled && hover.Value;
            return new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, ClipToBounds = true, Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnClick = enabled ? _go : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null, OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled, AllowFocusOnInteraction = false,
                Children = [new TextEl(line) { Size = p.Size, LineHeight = p.Line, Color = lit ? p.Accent : Ink.InkSecondary, Underline = lit, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
            };
        }
        void Go()
        {
            var r = Playback.Current.Peek();
            if (r.Kind != EntityKind.Track) return;
            var route = Shell.LinkFor(new Track(r.Slot), Shell.LinkSlot.Artist);
            if (route.Kind == Shell.RouteKind.NotFound) return;
            Close(_begin, "navigate");          // leave the stage BEFORE navigating (V-U29): the shell body is collapsed under it
            Shell.GoTo(route);
        }
    }

    // ══ 6. THE GALLERY MOUNT ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The gallery pane's body (placement + Enter/Exit live on ChromeHost's wrapper). Re-renders on the layout, palette and
    /// accent signals the gallery reads; the pane's controls take the context's own preference signals (O8).</summary>
    sealed class GalleryHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            return new BoxEl
            {
                OnHoverMove = _ => ctx.OverControls(true), OnPointerExit = () => ctx.OverControls(false),
                Children = [Visualizer.Gallery(ctx, L, close: static () => Prefs.Stage.SetGalleryOpen(false))],
            };
        }
    }

    // ══ 7. UP NEXT · ARTIST ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Up next" + the Autoplay ToggleSwitch, then the queue's own rows (QueuePaneBody).</summary>
    sealed class QueuePane : Component
    {
        readonly Signal<bool> _autoplay = new(true);   // seeded once, mirrored from the setting by the effect — never a new signal per render
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            UseSignalEffect(() => { _ = Platform.SettingsChanged.Value; _autoplay.SetIfChanged(Platform.Settings.Get(Platform.Keys.AutoplayEnabled)); });
            EntityId contextId = Playback.ContextUri.Value;
            Queue.ContextWire wire = Playback.ContextLabel.Value;
            UseEffect(static () => Queue.EnsureContext(Queue.ContextTarget(Playback.ContextUri.Peek(), Playback.ContextLabel.Peek())),
                      DepKey.From(contextId.GetHashCode() ^ (wire.Referrer.GetHashCode() * 31)));
            var toggle = ToggleSwitch.DefaultStyle with { OnFill = ctx.Accent.Fill, OnHover = ctx.Accent.FillSecondary, OnPressed = ctx.Accent.FillTertiary, OnKnob = ctx.Accent.Ink, MinWidth = 40f };
            Element? rows = QueuePaneBody?.Invoke();
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.UpNext)) { Size = 28f, LineHeight = 36f, Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink },
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                                Children = [new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = 14f, LineHeight = 20f, Color = Ink.Ink },
                                            ToggleSwitch.Create(_autoplay, static on => Platform.Settings.Set(Platform.Keys.AutoplayEnabled, on), style: toggle)],
                            },
                        ],
                    },
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stagequeue",
                        Content = rows ?? new BoxEl { Padding = new Edges4(0f, Spacing.XXL, 0f, 0f), Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty)) { Size = 14f, LineHeight = 20f, Color = Ink.InkTertiary }] },
                    },
                ],
            };
        }
    }

    /// <summary>The artist: the rail's about card over the hero image, Follow / Go to artist, and this track's credits.
    /// The artist slot is resolved exactly as the rail's NowPlayingBody does (Rail.UI.cs:471-484).</summary>
    sealed class ArtistPane : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var begin = UseContext(SharedTransition.Begin);
            var track = ctx.RowValue();
            _ = Entities.Current.Artists.Changed.Value;
            uint epoch = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Edges.TrackCredits.Changed.Value;
            var artist = Rail.NowPlayingArtist(track);        // the rail's resolver, made internal (§4.12)
            UseEffect(() => { if (artist.IsValid) Entities.Ensure(artist, ArtistFields.Overview); if (track.IsValid) Entities.EnsureEdge(FetchEdge.TrackCredits, track.Slot); }, DepKey.From(artist.Slot, track.Slot, (int)epoch, 0));
            var kids = new List<Element>(3);
            if (artist.IsValid)
            {
                kids.Add(new BoxEl
                {
                    Height = 240f, Corners = Radii.CardAll, ClipToBounds = true, ZStack = true, BorderWidth = 1f, BorderColor = Ink.Stroke,
                    Children =
                    [
                        // Controls.Artwork returns Element (no Align/Justify): a stretched BoxEl hosts it (V-U6)
                        new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                                    Children = [Controls.Artwork(Controls.ArtUrl(artist.HeroImageId.IsEmpty ? artist.ImageId : artist.HeroImageId), 1200f, 240f, 0f, decodePx: 1024)] },
                        // the caption gradient: BOTTOM (AlignSelf End) across the width (JustifySelf Stretch) — V-U9
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.L, Padding = new Edges4(Spacing.XXL, Spacing.XL, Spacing.XXL, Spacing.XL),
                            Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(0.72f))),
                            Children =
                            [
                                new BoxEl
                                {
                                    Grow = 1f, Direction = 1, MinWidth = 0f,
                                    Children =
                                    [
                                        new TextEl(Loc.Get(Strings.Stage.Mode.Artist)) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary },
                                        new TextEl(artist.Name) { Size = 28f, LineHeight = 36f, Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                        new TextEl(Strings.Stage.MonthlyListeners(FormatCache.Int((int)Math.Min(artist.MonthlyListeners, int.MaxValue)))) { Size = 14f, LineHeight = 20f, Color = Ink.InkSecondary },   // FormatCache.Int(int) is a method (FormatCache.cs:61, V-U6)
                                    ],
                                },
                                Button.Create(Loc.Get(Strings.Stage.GoToArtist), () => { var route = Shell.LinkFor(track, Shell.LinkSlot.Artist); if (route.Kind == Shell.RouteKind.NotFound) return; Close(begin, "navigate"); Shell.GoTo(route); },   // V-U29
                                              ButtonAppearance.Standard, ControlSize.Small),
                            ],
                        },
                    ],
                });
                if (!artist.BioLeadId.IsEmpty)
                    kids.Add(Card(new TextEl(Entities.Strings.Resolve(artist.BioLeadId)) { Size = 14f, LineHeight = 20f, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = 6, Trim = TextTrim.CharacterEllipsis }));
            }
            kids.Add(Credits(track));
            return new ScrollEl { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageartist", Content = new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids.ToArray() } };
        }

        static Element Card(params Element[] body) => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L), Corners = Radii.CardAll,
            Fill = ColorF.FromRgba(255, 255, 255, 13), BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 15), Children = body,
        };

        /// <summary>Credits · {title}: name · role rows from Edges.TrackCredits (the drawer's source, Track.Drawer.cs:617-636).</summary>
        static Element Credits(Track track)
        {
            if (!track.IsValid) return new BoxEl { HitTestVisible = false };
            var credits = Entities.Current.Edges.TrackCredits;
            var payload = credits.Payload(track.Slot);
            if (payload.Length == 0) return new BoxEl { HitTestVisible = false };
            var rows = new List<Element>(payload.Length + 1) { new TextEl(Strings.Stage.Credits(track.Title)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink } };
            for (int i = 0; i < payload.Length && i < 12; i++)
            {
                var c = payload[i];
                rows.Add(new BoxEl
                {
                    Direction = 0, Gap = Spacing.L,
                    Children = [new TextEl(Entities.Strings.Resolve(c.Role)) { Size = 14f, LineHeight = 20f, Color = Ink.InkSecondary, Width = 140f, Shrink = 0f },
                                new TextEl(Entities.Strings.Resolve(c.Name)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                });
            }
            return Card(rows.ToArray());
        }
    }
}
```
Notes for the implementer: (1) `ChromeActivity`, `ChromePlayback`, `PlayerChromeTiming`, `PlayerChromeVisibility` are `FluentGpu.Controls.Media` (`PlayerChromeVisibility.cs:8-83`); `TeachingTip.PlacementMode` is NESTED in `TeachingTip` (`TeachingTip.cs:48`); `TimerHandle` is `FluentGpu.Hooks` (`RenderContext.Timers.cs:30-54`); `ContextMenuOptions` is `FluentGpu.Controls` (`ContextMenu.cs:31-43`). (2) `OnHoverMove : Action<Point2>?`, `OnPointerExit : Action?`, `OnPointerWheel : Action<WheelEventArgs>?` (`Element.cs:226-236`). (3) `Queue.ContextKindLabel(EntityId)` is a new helper in `Entities/Queue.UI.cs` beside `ContextName` (`:162`; it is UI — it reads loc — V-U26): `=> Loc.Get(context.Kind switch { EntityKind.Album => Strings.Stage.Kind.Album, EntityKind.Playlist => Strings.Stage.Kind.Playlist, EntityKind.Artist => Strings.Stage.Kind.Artist, EntityKind.Show => Strings.Stage.Kind.Show, _ => Strings.Stage.Kind.Search })`, and the card renders `new TextEl(Strings.Stage.PlayingFrom(Queue.ContextKindLabel(contextId)))` — NO `Loc.Get` around a formatted key (V-D15/V-U25). (4) `Rail.NowPlayingArtist(Track)` is the rail's existing artist-slot resolution (`Rail.UI.cs:471-484`) extracted into an `internal static Artist` helper by WP-U6. (5) `ColorContrast.PickContrast` is the engine helper `AccentSet.From` uses (`Tokens.cs:53`). (6) `FormatCache.Int(int) : string` is the engine's cached formatter (`FluentGpu.Foundation\FormatCache.cs:61`). (7) `Shell.PlayerKey` / `Shell.PlayerKeyIntent` are `Shell.PlayerBar.cs:448-450` (on `Shell` itself); `Shell.BarSeekBy(int)` becomes `internal` in WP-U6 (§4.12). (8) Every `Prefs.Stage.*()` READ on this file is inside SurfaceCore's epoch effect or the tip effect; the `Set*` calls are writes (O8).

### 4.10b App SHELL — NEW `src/apps/Wavee/Shell/Stage.Host.cs` (WP-U2 — the stage's own files, V-U38)

```csharp
// ── Shell/Stage.Host.cs ────────────────────────────────────────────────────────────────────────────────────────────
// Stage.Diagnostics — the structured log events (stage.enter/exit, viz.lease, viz.analysis, viz.pick) and the snapshot
// the Diagnostics page's "Fullscreen & visualizer" card reads
//
// Role: SHELL
// Owner: K
// Wave: 7
// Budget: 120 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.10b, §4.14

using FluentGpu.Signals;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The Rail.NpvDiagnostics shape (Rail.cs:313-338): `Note…` writers that log and bump one version signal.</summary>
    public static class Diagnostics
    {
        public const string Category = "stage";
        public static readonly Signal<int> Version = new(0);
        public static bool IsOpen { get; private set; }
        public static Mode LastMode { get; private set; }
        public static Visualizer.Tier LastTier { get; private set; }
        public static Visualizer.Source LastSource { get; private set; }
        public static Visualizer.Kind LastKind { get; private set; }
        public static long EnteredAtMs { get; private set; }

        static void Bump() => Version.Value = Version.Peek() + 1;

        public static void NoteEnter(string cause)
        {
            IsOpen = true; EnteredAtMs = Design.FrameTime.NowMs;
            Log.Event(WaveeLogLevel.Info, Category, "stage.enter", "fullscreen stage opened via " + cause,
                fields: [WaveeLogField.Of("cause", cause), WaveeLogField.Of("mode", LastMode.ToString()), WaveeLogField.Of("kind", LastKind.ToString())]);
            Bump();
        }

        public static void NoteExit(string cause)
        {
            IsOpen = false;
            long dwellMs = EnteredAtMs == 0 ? 0 : Design.FrameTime.NowMs - EnteredAtMs;
            Log.Event(WaveeLogLevel.Info, Category, "stage.exit", "fullscreen stage closed via " + cause, elapsedMs: dwellMs,
                fields: [WaveeLogField.Of("cause", cause), WaveeLogField.Of("mode", LastMode.ToString())]);
            Bump();
        }

        public static void NoteMode(Mode mode)
        {
            if (LastMode == mode) return;
            LastMode = mode;
            Log.Event(WaveeLogLevel.Debug, Category, "stage.mode", "stage mode " + mode, fields: [WaveeLogField.Of("mode", mode.ToString())]);
            Bump();
        }

        public static void NotePick(Visualizer.Kind kind)
        {
            LastKind = kind;
            Log.Event(WaveeLogLevel.Info, Category, "viz.pick", "visualizer " + kind, fields: [WaveeLogField.Of("kind", kind.ToString())]);
            Bump();
        }

        public static void NoteLease(Visualizer.Tier tier)
        {
            if (LastTier == tier) return;
            LastTier = tier;
            Log.Event(WaveeLogLevel.Debug, Category, "viz.lease", "visualizer lease " + tier, fields: [WaveeLogField.Of("tier", tier.ToString())]);
            Bump();
        }

        public static void NoteSource(Visualizer.Source source)
        {
            if (LastSource == source) return;
            LastSource = source;
            Log.Event(WaveeLogLevel.Debug, Category, "viz.analysis", "visualizer data source " + source, fields: [WaveeLogField.Of("source", source.ToString())]);
            Bump();
        }
    }
}
```

### 4.11 Lyrics on the stage (WP-U5)

**`src/apps/Wavee/Shell/Lyrics.cs`** — `BlurPolicy` (`:164-184`) gains the stage rule, and `Prefs.BlurStrength` (`:1131`) takes the surface:
```csharp
        /// <summary>The STAGE never blurs: the fullscreen surface's depth cue is the scrim and the accent pill, and every
        /// non-active line being its own blur layer was the stage's largest per-frame cost (§1.1). 0 ⇒ Enabled(0) is false
        /// and DriveDofRamp snaps every σ to 0 in one pass.</summary>
        public static int ResolveFor(int setting, bool weakGpu, bool onStage) => onStage ? 0 : Resolve(setting, weakGpu);
```
```csharp
        /// <summary>Reactive read of the blur strength RESOLVED (0..100) for a surface: the stored −1 means AUTO; the stage is always 0.</summary>
        public static int BlurStrength(bool weakGpu, bool onStage) => BlurPolicy.ResolveFor(global::Wavee.Prefs.Lyrics.BlurStrength(), weakGpu, onStage);
```
(the one-argument `BlurStrength(bool weakGpu)` is deleted; its only caller is `Lyrics.UI.cs:319`).

**`src/apps/Wavee/Shell/Lyrics.UI.cs`**
- `:56`: `const float StageColumnMaxW = 700f, StageColumnGutter = 48f, StagePivotBandH = 72f;` → `const float StageColumnMaxW = 1000f, StageColumnGutter = 24f;` (the prototype's `.ln { max-width: 1000px }`; the pivot band is gone).
- `:65-66`: `s_stageVisible = static () => Shell.Ui.ImmersiveLyrics.Value && global::Wavee.Prefs.Stage.Mode() == (int)Stage.Mode.Lyrics;` (`Prefs` inside `Lyrics` is `Lyrics.Prefs`, `Lyrics.cs:1094`; this is a render-time read of a preference, allowed by O8).
- `StagePane` (`:77-92`) takes the accent signal and drops the pivot reserve:
```csharp
    /// <summary>The lyrics pane's BODY inside the stage's pane region (the frame, the KeepAlive and the entrance are
    /// Stage.UI.cs's). The SAME view at stage metrics on the stage's ink, with the ART ACCENT for the active-line pill.</summary>
    public static Element StagePane(IReadSignal<ColorF> accent) => new BoxEl
    {
        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, Justify = FlexJustify.Start, AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(0f, 0f, StageColumnGutter, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, MaxWidth = StageColumnMaxW,
                Children = [Embed.Comp(() => new ViewCore(large: true, onMedia: true, visible: s_stageVisible, accent: accent, onStage: true)) with { Key = "lyrics:stage" }],
            },
        ],
    };
```
- `ViewCore` ctor (`:183`, `internal ViewCore(bool large, bool onMedia, Func<bool> visible, EntityId readerEpisode = default)`) APPENDS two optional parameters AFTER `readerEpisode` — `Lyrics.Transcript.cs:208` calls it POSITIONALLY with four arguments (V-U28): `internal ViewCore(bool large, bool onMedia, Func<bool> visible, EntityId readerEpisode = default, IReadSignal<ColorF>? accent = null, bool onStage = false)`, with fields `internal readonly IReadSignal<ColorF>? Accent; internal readonly bool OnStage;` assigned in the ctor. The accent signal is a mount-time instance (the slab's `Accent`, stable for the stage's life) — the contract the Slider/ToggleSwitch signal instances follow. `onStage` is an EXPLICIT flag (V-U51): `onMedia` is also true for the rail's on-media mode, which keeps its blur.
- `:319`: `int strength = Prefs.BlurStrength(GpuProfile.IsWeak, OnStage);`
- `LineRow.Render` (`:1911-1917`): the DoF wrapper gains the accent pill (a 3 × 28 bar in the left gutter, visible on the active row, colour cross-faded through `BrushTransitionMs` like the ink flip at `:1896`):
```csharp
            // Own DoF on a persistent INNER wrapper … (comment unchanged)
            var textColumn = new BoxEl { Direction = 1, Children = secondaryText is null ? [textEl] : [textEl, SecondaryText(secondaryText)] };
            Element dofContent = owner.Accent is { } accentSig
                ? new BoxEl
                {
                    ZStack = true, Blur = blur, OnRealized = _onDof,
                    Children =
                    [
                        textColumn,
                        // THE PILL (ListView accent bar): in the gutter, vertically centred on the row; accent on the active line, else clear.
                        new BoxEl
                        {
                            Width = 3f, Height = 28f, Corners = Radii.Circle(3f), HitTestVisible = false,
                            AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, OffsetX = -(m.SidePad - Spacing.S),   // v-centre · left (V-U9)
                            Fill = isActive && !neutral ? accentSig.Value : ColorF.Transparent, BrushTransitionMs = Design.Motion.Fast,
                        },
                    ],
                }
                : new BoxEl { Direction = 1, Blur = blur, OnRealized = _onDof, Children = textColumn.Children };
```
(`accentSig.Value` is evaluated ONLY on the active, non-neutral row — the `&&` short-circuits before the read on every other row — so exactly ONE row subscribes to the slab's cross-fade and re-renders at 30 Hz for 600 ms per track change, then settles (V-U51). `Radii.Circle`, `Spacing.S` are the engine tokens already imported by this file. The pill is LEFT (JustifySelf Start) and vertically CENTRED (AlignSelf Center) in the ZStack — V-U9.)

### 4.12 Shell integration (WP-U6)

`Shell.cs` / `Shell.UI.cs` are being edited concurrently: every anchor below is BY SYMBOL (today's lines in parentheses).

**`src/apps/Wavee/Shell/Shell.cs`** — `FrameRules` (`public static class FrameRules`, `:1857`):
```csharp
        /// <summary>FULL-SCREEN VIDEO OR THE FULLSCREEN STAGE UNMOUNTS THE CHROME ROW AND THE PLAYER BAR — one derived
        /// predicate drives both Flow.Show boundaries, so there is exactly one transport on screen.</summary>
        public static bool ChromeMounted(Video.SurfacePlacement resolved, bool immersive) => resolved != Video.SurfacePlacement.Fullscreen && !immersive;   // replaces the one-arg ChromeMounted (:1897)

        public enum FullscreenToggle : byte { None, Enter, Exit, CloseStage }                                                 // replaces :1919
        /// <summary>F11: the stage closes if it is up; else video fullscreen toggles as before; else nothing.</summary>
        public static FullscreenToggle F11(bool isFullscreen, bool videoActive, bool stageUp)                                // replaces :1921-1922
            => stageUp ? FullscreenToggle.CloseStage : isFullscreen ? FullscreenToggle.Exit : videoActive ? FullscreenToggle.Enter : FullscreenToggle.None;
```
(`Escape` (`:1906`) is unchanged — `CloseImmersiveLyrics` already wins over video.)

**`src/apps/Wavee/Shell/Shell.UI.cs`**
- `s_chromeMounted` (`:647-648`): `static readonly Func<bool> s_chromeMounted = static () => FrameRules.ChromeMounted(Video.PlacementCore.Resolve(Video.State.Surface.Value), Ui.ImmersiveLyrics.Value);`
- The column's `ContentRegion(vp),` child (`:482`): `ContentRegion(vp) with { Visible = Prop.Of(static () => !Ui.ImmersiveLyrics.Value) },` — the shell body COLLAPSES while the stage is up: out of layout, paint and hit-test, AND its whole subtree goes INACTIVE — `SetSubtreeHidden` writes `ActiveSig = !Parked && !Hidden` on every component below (`Reconciler.Presence.cs:73-83`, `Reconciler.cs:1163`), so every `UseInterval` under the content region pauses (the rail deck's 30 Hz clock, the rail lyrics view's tickers, the pages' pollers — desirable: nothing under the stage spends a tick) and every `UseActivation` fires its deactivated/activated edges on stage ENTER/EXIT exactly as on a KeepAlive tab switch. The five consumers were reviewed (V-U37) and none needs gating:
  - `Settings.UI.cs:133` `UseActivation(onDeactivated: static () => EnterPlayback(false))` — despite the name, `EnterPlayback(false)` (`Settings.UI.Playback.cs:89-95`) is the Playback TAB's teardown: it nulls `s_voAnchor` and closes the video-overrides flyout (`s_voHandle`). It never touches playback state. ENTER: an open overrides flyout closes — desirable (no overlay may float above the stage). EXIT: nothing (no `onActivated`). **Benign.**
  - `Show.Page.cs:157` `UseActivation(onActivated: Reopen)` — `Reopen` (`:229-240`) ensures the edge when Unknown, else asks `ListFreshness.Decide` and refreshes `ShowEpisodes` only when the verdict is not `Paint` (Prefetch or Visible priority). ENTER: nothing. EXIT: a freshness-GATED stale-while-revalidate, the same one a Back/forward return runs. **Benign (desirable: the page comes back fresh).**
  - `Profile.Page.cs:240` `UseActivation(onActivated: _demand)` — `Demand` (`:562-567`) runs `ProfileAsk.Apply(u, ProfileAsk.Plan(u, ProfileSurface.Page))`, the SWR re-ask ("a revisit re-reads, stale keeps rendering"). ENTER: nothing. EXIT: the same re-read a keep-alive return does. **Benign.**
  - `Recents.Page.cs:271` `UseActivation(onActivated: _activated, onDeactivated: _deactivated)` — ENTER: `OnDeactivated` (`:477-482`) erases the cached drawer geometry and `_expandedRow = -1`. EXIT: `OnActivated` (`:470-475`) `CollapseExpanded()` + a Prefetch `RefreshEdge(Recents)` when loaded. The one visible consequence: an expanded Recents drawer is collapsed when the stage comes down — identical to switching tabs, and the drawer's own §9.2 rule ("the first return frame cannot replay the old drawer"). **Benign.** (Its two `UseTimeout`s keep firing under the stage — `UseTimeout` has no active-gating, `Presence.cs:70-72` — which is the status quo.)
  - `Design.cs:2575` `CoverShellTintBinder` `UseActivation(onActivated: () => Publish(isClaim: true))` — ENTER: nothing (no `onDeactivated`; the stage paints its own floor over the shell material). EXIT: the page RE-CLAIMS the shell material tint (`ShellMaterial.Publish(…, isClaim: true, …)`, `:2562`) — exactly what a page coming back from under an overlay should do. **Desirable.**
  Decision: keep the collapse; no stage predicate is added to any page and no other recording-stop mechanism is needed. The bound `Visible` goes on the OUTER ZStack (`s_contentRegion`, `:637-644`, no `MorphId`); the content row's `MorphId` (`:525`) stays on an inner node with an unbound `Visible` (the `BindContract` rule guards the tagged node itself).
- `ToggleVideoFullscreen` (`:763-770`) becomes:
```csharp
    /// <summary>F11 — closes the fullscreen stage if it is up; otherwise toggles video fullscreen while a video is active.</summary>
    static void ToggleVideoFullscreen()
    {
        switch (FrameRules.F11(Video.State.Resolved == Video.SurfacePlacement.Fullscreen, Video.State.IsActive, Ui.ImmersiveLyrics.Peek()))
        {
            case FrameRules.FullscreenToggle.Enter: Video.State.EnterFullscreen(); break;
            case FrameRules.FullscreenToggle.Exit: Video.State.ExitFullscreen(); break;
            case FrameRules.FullscreenToggle.CloseStage: Stage.Close(null, "f11-shell"); break;   // the stage root normally takes F11 first; this is the unfocused fallback (no flight) — V-U43
        }
    }
```
- `OnShellKey` (`:774-798`), the `CloseImmersiveLyrics` arm: `case FrameRules.EscapeAction.CloseImmersiveLyrics: Stage.Close(null, "escape-shell"); e.Handled = true; break;`
- The mount (`:500-505`) is unchanged.

**`src/apps/Wavee/Shell/Rail.UI.cs`**
- `LyricsHeader.Render` (`:256`), the ⛶ at `:267`: `kids.Add(HeaderButton(Icons.FullScreen, Loc.Get(…ExpandLyrics…), () => Stage.Open(begin, "rail-header")));` with `var begin = UseContext(SharedTransition.Begin);` read in `Render` — the lambda is no longer `static`. It stays UNCONDITIONAL (nothing playing opens an empty stage, as today — `:253-255`).
- `NowPlayingBody` (`:471-484`): extract the artist-slot resolution into `internal static Artist NowPlayingArtist(Track track)` (returns `default` when unknown) and call it from both places.

**`src/apps/Wavee/Shell/Shell.PlayerBar.UI.cs`**
- `SeekBar` (`:87`) gains the stage's scrub seam — a `PlayerChromeFeed` is inert without a `MediaPlayerElement` owner (`PlayerChromeFeed.cs:21`), so the stage cannot use it: `public static Element SeekBar(PlayerChromeFeed? feed = null, Action<bool>? onScrubbing = null) => Embed.Comp(() => new BarSeekRail(feed, onScrubbing));` and `BarSeekRail` (`:1522`) stores `_onScrubbing` and calls it beside `_chromeFeed?.SetScrubbing(…)` at the three sites (`:1715` true; `:1739`, `:1746` false). OnMedia's `Shell.SeekBar(Feed)` (`OnMedia.UI.cs:279`) and the bar's own `SeekBar()` compile unchanged.
- The art (`:393-402`) gains the morph tag ONLY — its single click keeps today's navigation, and there is NO double-click entry (D16: the first click of a double-click would still navigate, `InputDispatcher.DoubleClickMs = 500`; the Expand button beside the art is the bar's door and the flight captures the TAGGED art wherever the click lands):
```csharp
                Children = [Controls.Artwork(BarArtUrl(faceRef), L.ArtSize, L.ArtSize, 6f, morphKey: Stage.Entry.MorphKey, scale: artScale)],
```
(`Controls.Artwork` with a `morphKey` mounts no shimmer and a frozen placeholder, `Platform/Controls.cs:198-203` — the bar art is 48 DIP, the trade is accepted and noted in D9.)
- The Expand slot (`:597-600`) opens the stage: `BarButton(Icons.FullScreen, () => Stage.Open(begin, "bar-expand"), true, false, box, glyph)` with `var begin = UseContext(SharedTransition.Begin);` in `PlayerBarRoot.Render` and the tooltip `Loc.Get(Strings.Player.Fullscreen)` (NEW key `player.fullscreen`, §4.6) — the `RailMode.NowPlaying` toggle moves to the "…" overflow (`PlayerBarRules.Overflow`, `Shell/Shell.PlayerBar.cs:320-321`, `OverflowCommand.NowPlaying` `:60`), so the now-playing rail keeps a door; `ShellPlayerBarUiRulesTests.cs:274/:512` and `PodcastPlayerBarTests.cs:87` pin the overflow order and are updated with it (V-U27).

**`src/apps/Wavee/Shell/Shell.PlayerBar.Podcast.UI.cs`** — `static void BarSeekBy(int deltaMs)` (`:16`) becomes `internal static` so the stage root's key map (O9, §4.10 `OnKey`) seeks exactly as the bar does.

**`src/apps/Wavee/Shell/Video.UI.cs:758`** — unchanged (`covered = resolved == Docked && Shell.Ui.ImmersiveLyrics.Value` still reads the same fact).

**`src/apps/Wavee/Entities/Queue.UI.cs`** (UI — it reads loc; not `Queue.cs`, V-U26) — beside `ContextName` (`:162`): `public static string ContextKindLabel(EntityId context) => Loc.Get(context.Kind switch { EntityKind.Album => Strings.Stage.Kind.Album, EntityKind.Playlist => Strings.Stage.Kind.Playlist, EntityKind.Artist => Strings.Stage.Kind.Artist, EntityKind.Show => Strings.Stage.Kind.Show, _ => Strings.Stage.Kind.Search });` (the `stage.kind.*` nouns are NEW in the three loc files, §4.6 — there is no `entity.*` block, V-U7).

**Docs (V-U54)**: `docs/plans/wavee/wavee-0.3-ui/18-shell-frame.md:1140` (the F11 row: "toggle video fullscreen **only while a video is active**") becomes "closes the fullscreen stage if it is up (the stage root handles it first; `ToggleVideoFullscreen` is the unfocused fallback); otherwise toggles video fullscreen only while a video is active"; the new `docs/guide/fullscreen-visualizers.md` (§7.5) carries a "Keys on the stage" table: Esc / F11 close · Space / ← → / ↑ ↓ as the player bar · media keys via SMTC. There is no `docs/guide/shortcuts.md`.

### 4.13 Deck migration (WP-U7)

**`src/apps/Wavee/Shell/Deck.cs`** (anchors BY SYMBOL — V-U40)
- NEW delegate beside `IModel` (`:79-93`): `/// <summary>A pull of the 0..1 band levels the rail's own fold produced this tick (Deck.SpectrumFold.Levels) — empty when there is no live spectrum.</summary> public delegate ReadOnlySpan<float> SpectrumPull();`
- `MeterModel` (`public sealed class MeterModel : IModel`, `:1134`): the fake breath is deleted — in `MeterModel.Tick(in Input input, float dtSec)` (`:1157`) the else-branch `:1170-1176` (`var sample = _levels?.Invoke(); float dbL = sample is { } s ? RmsToDb(s.rms) : RmsToDb(0.12f) + 3f * MathF.Sin(BreatheOmega * _t); float dbR = dbL + 1.5f * MathF.Sin(RightOffsetOmega * _t);`) becomes
```csharp
                var sample = _levels?.Invoke();
                if (sample is { } s)
                {
                    float dbL = RmsToDb(s.rms);
                    targetL = DbToDeg(dbL);
                    targetR = DbToDeg(dbL + 1.5f * MathF.Sin(RightOffsetOmega * _t));   // the stereo spread stays a derivation of ONE real level
                }
                else { targetL = RestDeg; targetR = RestDeg; }                            // no tap ⇒ the needles rest; nothing is invented
```
(`const float BreatheOmega = 0.6f;` (`:1137`) is deleted.)
- `LevelModel` (`:1198-1324`) is REPLACED by a real-band model:
```csharp
    /// <summary>The analyser decks (Winamp 19, WMP 24): the stage's 48 folded band levels remapped onto N bars, with the deck's
    /// own peak caps (hold 400 ms, fall 1.2/s). No tap ⇒ the bars release to the floor and the model settles; nothing is synthesised.</summary>
    public sealed class LevelModel : IModel
    {
        const float PeakHoldMs = 400f, PeakFallPerSec = 1.2f, FloorLevel = 0.02f, SettleEps = 0.005f, Release = 0.12f;
        readonly int _bandCount;
        readonly SpectrumPull? _spectrum;
        readonly float[] _bands, _peaks, _peakHoldMs;
        bool _settled;

        public LevelModel(int bands, SpectrumPull? spectrum)
        {
            _bandCount = bands;
            _spectrum = spectrum;
            _bands = new float[bands]; _peaks = new float[bands]; _peakHoldMs = new float[bands];
            for (int i = 0; i < bands; i++) _bands[i] = FloorLevel;
        }

        public ReadOnlySpan<float> Bands => _bands;
        public ReadOnlySpan<float> Peaks => _peaks;
        public bool IsSettled => _settled;

        public Frame Tick(in Input input, float dtSec)
        {
            bool playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);
            ReadOnlySpan<float> live = _spectrum is { } pull ? pull() : default;
            bool have = playing && live.Length >= 2;
            for (int i = 0; i < _bandCount; i++)
            {
                float target = have ? MathF.Max(FloorLevel, Remap(live, i, _bandCount)) : FloorLevel;
                _bands[i] += (target - _bands[i]) * (target > _bands[i] ? 0.5f : Release);
                if (!playing && _bands[i] < FloorLevel + SettleEps) _bands[i] = FloorLevel;
                UpdatePeak(i, _bands[i], dtSec);
            }
            bool settled = !playing;
            if (settled) for (int i = 0; i < _bandCount; i++) if (_bands[i] > FloorLevel) { settled = false; break; }
            _settled = settled;
            return new Frame(input.Frac, 0f, 0f, 0f, 1f, 0f, 0f, 0, false, playing ? PhaseName.Playing : PhaseName.Paused);
        }

        /// <summary>Bar i of N reads the MAX of the source bands that fall in its share (48 → 19/24 never drops a peak).</summary>
        public static float Remap(ReadOnlySpan<float> source, int i, int n)
        {
            int from = (int)((long)i * source.Length / n), to = Math.Max(from + 1, (int)((long)(i + 1) * source.Length / n));
            float m = 0f;
            for (int k = from; k < to && k < source.Length; k++) if (source[k] > m) m = source[k];
            return m;
        }

        void UpdatePeak(int i, float band, float dtSec) { /* unchanged body — the existing LevelModel.UpdatePeak, Deck.cs:1293 */ }
    }
```
- `Models.Create` (`:1479-1494`) gains a `SpectrumPull? spectrum = null` parameter (RailTests' 3-arg call keeps compiling): `Rail.PlayerCatalog.Winamp => new LevelModel(WinampBands, spectrum)`, `Rail.PlayerCatalog.Wmp => new LevelModel(WmpBands, spectrum)`. `ModelOptionSlug` (`:1520-1525`): the Winamp arm is deleted (Vu only).
- **`Rail.cs:160`**: the `Seg("vis", "player.opt.analyser", ("spectrum", …), ("scope", …))` option row is deleted (the oscilloscope was a synthesised trace — there is no time-domain tap; its three loc keys go with it, §4.6); **`Deck.Faces.cs:986`** `bool scope = Rail.PlayerPrefs.ChoiceSlug(in preset, "vis") == "scope";` and the scope branch of `WinampFace` are deleted (`ScopeDots` `:980` goes).
- **`Deck.UI.cs`**: `LevelTap` (`:120-127`) stays for the VU; add beside it
```csharp
        /// <summary>The analyser decks read the rail's OWN 30 Hz fold of the engine bands (Deck.SpectrumFold.Pull, called from the
        /// deck clock's tick) — the real FFT, independent of whether the stage is open; nothing synthesises bands any more.</summary>
        static readonly Deck.SpectrumPull SpectrumTap = static () => Deck.SpectrumFold.Levels;
```
`Model = Models.Create(in Preset, LevelTap, Seed(...), SpectrumTap)` (`:163`); `UsesLevels` (`:147`) splits into `UsesLevels => Preset.Id is Vu` and `UsesSpectrum => Preset.Id is Winamp or Wmp`; `LeaseLevels` (`:383-392`) acquires `Playback.Audio.AcquireSpectrum()` when `UsesSpectrum`, else `AcquireLevels()`; the deck `Clock` caches the gain ONCE per epoch — `float _gain = 1f;` + `UseSignalEffect(() => _gain = Visualizer.Bands.Gain(Prefs.Stage.Sensitivity(), false));` in `Render` (O8: never a registry read on the tick) — and `TickCore` (`:318-346`) calls `Deck.SpectrumFold.Pull(_gain)` before `h.Model.Tick` when `UsesSpectrum`. NEW in `Deck.UI.cs`:
```csharp
    /// <summary>The rail's own 48-band fold (one per process): CopySpectrum → Visualizer.Bands.Unit → AGC → gain → Follow into Levels.
    /// Shared by every analyser deck mounted, so the follower is advanced ONCE per published spectrum (gated on
    /// SpectrumInfo.Sequence) and the no-live release ONCE per frame (gated on the frame clock) — two decks never double the
    /// attack or the decay (V-D25).</summary>
    public static class SpectrumFold
    {
        static readonly float[] s_db = new float[Visualizer.Bands.Count], s_target = new float[Visualizer.Bands.Count], s_levels = new float[Visualizer.Bands.Count];
        static long s_seq, s_decayedAtMs;
        static float s_agc = Visualizer.Bands.AgcFloor;
        public static ReadOnlySpan<float> Levels => s_levels;
        /// <param name="gain">The deck clock's cached Visualizer.Bands.Gain(sensitivity, calm: false).</param>
        public static void Pull(float gain)
        {
            int n = Playback.Audio.CopySpectrum(s_db, out SpectrumInfo info);
            if (n < Visualizer.Bands.Count || !info.Live || info.Muted)
            {
                long now = Design.FrameTime.NowMs;
                if (now != s_decayedAtMs) { s_decayedAtMs = now; Visualizer.Bands.Decay(s_levels); }   // once per frame, however many decks pull
                return;
            }
            if (info.Sequence == s_seq) return;                                                        // this publish was already folded by another deck
            s_seq = info.Sequence;
            float frameMax = 0f;
            for (int i = 0; i < n; i++) { float u = Visualizer.Bands.Unit(s_db[i]); s_target[i] = u; if (u > frameMax) frameMax = u; }
            float g = Visualizer.Bands.Agc(ref s_agc, frameMax) * gain;
            for (int i = 0; i < n; i++) s_target[i] = MathF.Min(1f, s_target[i] * g);
            Visualizer.Bands.Follow(s_target, s_levels, false);
        }
    }
```
(`using FluentGpu.Media;` for `SpectrumInfo`.) Tests: §5.

### 4.14 The Diagnostics card (WP-U8)

**`src/apps/Wavee/Screens/Diagnostics.UI.cs`** — on `RuntimePageView` (the playback-diagnostics page, `sealed class RuntimePageView : Component` `:265-305` — X4: both plans' cards land here), in `Render` after `body.Add(UpdatesSection(...))`: `body.Add(StageCard());`. The card is a `static` in the same `Diagnostics` partial, beside `StatusSection` (`Card`/`Row` are private to the partial, `:200-213`):
```csharp
        /// <summary>"Fullscreen & visualizer": the stage's mode/kind, the lease tier, the FFT cost per publish, the alignment
        /// applied to the last window (frames behind the newest rendered sample) and the endpoint's queue depth (diagnostic
        /// only — §2.7), the data source, and the publish count.</summary>
        static Element StageCard()
        {
            _ = Stage.Diagnostics.Version.Value;
            var (queueFrames, publishes) = Playback.Audio.SpectrumDiagnostics();
            Span<float> scratch = stackalloc float[Visualizer.Bands.Count];
            int n = Playback.Audio.CopySpectrum(scratch, out SpectrumInfo info);
            string delay = n == 0 ? queueFrames.ToString(CultureInfo.InvariantCulture) + " frames queued"
                : info.AlignFrames.ToString(CultureInfo.InvariantCulture) + " frames behind newest · " + queueFrames.ToString(CultureInfo.InvariantCulture) + " queued";
            return Card(Loc.Get(Strings.Stage.Diag.Title),
                Row(Loc.Get(Strings.Stage.Diag.Mode), Stage.Diagnostics.IsOpen ? Stage.Diagnostics.LastMode + " · " + Stage.Diagnostics.LastKind : null),
                Row(Loc.Get(Strings.Stage.Diag.Leases), Stage.Diagnostics.LastTier.ToString()),
                Row(Loc.Get(Strings.Stage.Diag.Fft), n == 0 ? null : info.FftMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms"),
                Row(Loc.Get(Strings.Stage.Diag.Delay), delay),
                Row(Loc.Get(Strings.Stage.Diag.Source), Stage.Diagnostics.LastSource.ToString()),
                Row(Loc.Get(Strings.Stage.Diag.Publishes), publishes.ToString(CultureInfo.InvariantCulture)));
        }
```
(`using FluentGpu.Media;` for `SpectrumInfo`; `CultureInfo` is already imported there.)

---

## 5. Tests (pure classes only; no engine mount in Wavee.Tests, no source text anywhere)

### 5.1 Engine — `..\fluent-gpu\src\FluentGpu.Engine.Tests` (xUnit v3, `InternalsVisibleTo` present)

**NEW `SpectrumAnalyzerTests.cs`** (V-E4: no DC test — bin 0 is in no band; the 0 dB check is made where the +3 dB/oct tilt cannot push a band into the +6 ceiling)
- `A_1kHz_sine_peaks_in_its_band` (Theory 44100/48000): synthesize `sin(2π·1000·n/rate)` amplitude 1, `Analyze`; `argmax(out) == BandOf(1000)`; every band more than two away is ≥ 30 dB below the peak.
- `A_1kHz_sine_peaks_in_its_band_at_96k` — the same at 96 000 with a ≥ 25 dB isolation criterion (the 46.9 Hz bins make the low bands one bin wide and leak more).
- `A_minus30dBFS_sine_at_80Hz_reads_minus30_plus_the_tilt` — amplitude `10^(−30/20)`, 80 Hz, 48 000: the peak band within ±1.5 dB of `−30 + 3·log2(fc/40)` where `fc = BandCenterHz(BandOf(80))` (unclamped territory: ≈ −27 dB).
- `A_full_scale_sine_at_60Hz_reads_about_zero_dB` — amplitude 1 at 60 Hz: the peak band within ±1.5 dB of `0 + 3·log2(fc/40)` (≈ +1.75 dB, under the +6 ceiling; above ≈ 160 Hz a full-scale sine would clamp).
- `Silence_clamps_to_the_floor` — zeros → every band `== FloorDb`.
- `Band_edges_are_monotone_and_cover_the_range` — `BandCenterHz(b)` strictly increasing; `BandCenterHz(0) ≥ 40`, `BandCenterHz(47) ≤ MaxHz`; sweeping `hz` over `[MinHz, MaxHz)` in 1 Hz steps, `BandOf(hz)` is non-decreasing, skipping any −1.
- `Analyze_steady_state_allocates_nothing` — warm 100 calls, then `GC.GetAllocatedBytesForCurrentThread()` delta over 1000 calls `== 0` (the `Mailbox_SteadyPublishAndRead_AllocateNothing` shape, `AudioVisualizerDemandTests.cs:147-162`).
- `Constructor_rejects_a_non_power_of_two_and_too_many_bands`.

**NEW `SpectrumRingTests.cs`** (the content-domain API, §4.1)
- `Write_downmixes_stereo_to_mono` — `[L,R,…]` → `(L+R)/2` at the right indices; `Written` advances by frames and never resets.
- `Arm_maps_content_frames_onto_the_ring` — `Arm(1000)`, write 4 096 mono samples, `TryCopyContentWindow(1000 + 2048, dst[2048])` returns the first 2 048 written samples; `NewestContent == 5096`.
- `A_window_before_the_arm_is_rejected` — write 100, `Arm(500)`, write 100: a window ending at content 540 → false (it would start before the arm); ending at 600 → true.
- `A_window_inside_the_guard_band_is_rejected` — capacity 64, `maxBlock` 8, `Arm(0)`, write 200: a window of 16 ending at content 150 → false (`start 134 < 200 − 64 + 8`); ending at 200 → true.
- `A_window_reads_across_the_wrap` — content equality across the buffer boundary.
- `A_rearm_invalidates_the_previous_domain` — `Arm(0)`, write 4 096, a window ending at 2 048 is true; `Arm(90_000)` with no further writes: the same call is false, and `NewestContent == 90_000`.
- `A_window_past_the_newest_content_is_rejected` — `NewestContent + 1` → false (the publisher's `Math.Min` saturation is what keeps a negative offset valid).
- `Write_and_copy_steady_allocate_nothing`.

**`AudioVisualizerDemandTests.cs`** (extend — O2: `VisualizerFrame.Magnitudes` stays EMPTY; V-E14: arm through the control side)
- `SpectrumLease_IsReferenceCounted_AndImpliesTheLevelTap` — `AcquireSpectrum()` makes `VisualizerDemand(source) != 0` and `SpectrumDemand(source) != 0`; disposing the last spectrum lease zeroes `SpectrumDemand` and (with no level lease) `VisualizerDemand`; a second dispose is a no-op.
- `PublishSpectrum_SwapsBuffersAndNeverTearsACopy` — publish `[0..47]`, `CopySpectrum` → equal, `Sequence == 1`; publish again → `Sequence == 2`, the copy is the new values; `info.WindowRms`/`AlignFrames`/`FftMs` are the published values; `Visualizer.Peek().Magnitudes.IsEmpty` throughout (the level frame is untouched).
- `PublishSpectrum_IsRejectedForAStaleEpochOrSource` (the `:44-69` shape).
- `ReleasingTheSpectrumLease_ZeroesCopySpectrum_AndKeepsTheLevelFrameUnderALevelLease` — hold `AcquireVisualizer()` AND `AcquireSpectrum()`, publish a level frame and a spectrum; dispose the spectrum lease: `CopySpectrum` returns 0 with `Live == false`, `Visualizer.Peek()` still carries the RMS/peak (the extra level lease keeps the tap alive — V-E14).
- `PcmRendering_FillsTheSpectrumRingOnlyUnderASpectrumLease` — `CreateSession(effects)` (`:249-259`) + `ConnectSignals(new MediaSignalSink(new MediaPlayerCore()))` + `PlayAsync()`; with NO spectrum lease `PumpAudio(512)` ×12 → `SpectrumPublishes == 0`, `CopySpectrum == 0`; then hold `AcquireSpectrum()` and `PumpAudio(512)` ×12 again → `CopySpectrum` returns 48 with the 440 Hz band the maximum (`new SpectrumAnalyzer(48000).BandOf(440)`), `SpectrumPublishes > 0`, `info.WindowRms ≈ 0.3/√2` (the generator's amplitude) within 10 %.
- `SpectrumOffsetMs_IsClampedToPlusMinus500`.
- `OutputDelayFrames_IsSubmittedMinusPlayedPlusLatency_Diagnostic` — on the headless endpoint after N blocks: `OutputDelayFrames == max(0, SubmittedFrames − PlayedFrames + StreamLatencyFrames)`.
- `RenderBlock_UnderASpectrumLease_AllocatesNothing` — the `AudioGraphTests.cs:287-300` shape: `BindEffects(fx)`, hold `AcquireSpectrum()`, `PlayAsync()`, `PumpAudio(512)` ×4 FIRST (the control tick creates the ring and the RT arms it — the one-time allocations), then 2 000 warm `RenderBlock(256)`, `GC.Collect()`, and the delta over 5 000 `RenderBlock(256)` `== 0` (the ring write and the arm are on the RT path).

**NEW `HeadlessGpuDeviceSeriesTests.cs`**: `DrawSeriesCmd_IsUnmanagedAndUnder1024Bytes` (`Unsafe.SizeOf<DrawSeriesCmd>() <= 1024`), `Series_Writer_ChunksAsCeilNMinus1Over31` (DrawList + `HeadlessGpuDevice.LastSeries`, the same facts as `gate.series.record.chunks`), `TryBodySize_KnowsDrawSeries`, `HeadlessSwapchain_IsOccluded_FoldsTheLatchAndTheStandDown` (`Occluded`, `PresentStandDown` → `IsOccluded`; the device's `PrimarySwapchain` is the first `CreateSwapchain`).

### 5.2 App — `src/apps/Wavee.Tests`

**NEW `StageLayoutTests.cs`** (replaces `StageTests.cs`' `StageLayoutTests` + `StagePaneTests`; delete `StageTests.cs`)
- `The_four_classes_are_the_prototypes_queries` (Theory): (1920,1080)→Desktop, (3440,1440)→Ultrawide, (900,1600)→Portrait, (1100,440)→Compact, (599,1000)→Compact, (1200,500)→**Ultrawide** (ratio 2.4 — V-U24), (1500,900)→Desktop.
- `Class_edges_are_hysteretic`: ratio 2.00 from Desktop → Ultrawide; 1.97 from Ultrawide stays; 1.94 → Desktop. Height 460→Compact; 470 from Compact stays; 484 → Desktop. Ratio 0.80→Portrait; 0.83 stays; 0.85→Desktop.
- `A_degenerate_size_keeps_the_previous_class`.
- `The_hero_is_the_prototypes_formula_quantised_and_clamped`: 1920×1080 → 536; 3440×1440 → 640 (cap); 1280×720 → min(358.4, 250.4)=250.4→248; a very short window → 168.
- `Desktop_1080p_lands_the_prototypes_numbers` — `PadX == 108`, `IdentityTop == 128`, `TitleY(Lyrics) == 688`, `PaneX == 740`, `PaneRight == 96` (V-U44).
- `Small_classes_lay_the_identity_out_as_a_row` — Portrait and Compact: `TitleX(Lyrics) == PadX + HeroArt + Pad`, `TitleY(Lyrics) == IdentityTop`; Compact: `TransportLeft == PadX + HeroArt + Pad`; Desktop: `TransportLeft == Pad` (V-U23).
- `FaceRight_is_this_layouts_gallery_plus_two_gutters` — Desktop `444 + 48`; Ultrawide `GalleryW + 48` (≠ 492); Portrait 0; closed 0 (V-U45).
- `Narrowing_never_adds` (`Richness` monotone over a width sweep at fixed heights 1080/900/700, and a height sweep at fixed widths 1920/1200/800 — the hero is excluded from `Richness` because it legitimately shrinks across the Ultrawide promotion, V-U24).
- `Compact_hides_the_pane_chips_gallery_and_volume`; `Portrait_is_icon_only_and_sheets_the_gallery`.
- `The_cover_has_exactly_two_sizes_per_layout` (`CoverSize(Visualizer) == ThumbArt`, every other mode `== HeroArt`); `The_pane_never_goes_negative`.
- `Title_type_steps_down_by_class_and_mode`.
- `ModeRules_coerce_toggle_and_caption` — `Coerce(-1) == 0`, `Coerce(4) == 0`, `Coerce(2) == 2`; `ToggleGallery(Lyrics, false) == (Visualizer, true)`, `ToggleGallery(Visualizer, true) == (Visualizer, false)`; `ShowsCaption` truth table.
- `Entry_rules` — `CanEnter(videoFullscreen: true) == false`, `CanEnter(false) == true` (an empty stage is allowed).
- `Tone_progress_is_linear_and_lands_at_CrossFadeMs` — `Progress(0) == 0`, `Progress(300) == 0.5`, `Progress(600) == 1`, `Progress(900) == 1`, `Progress(−1) == 0` (V-U17).

**`StageSurfaceTests.cs`** — delete `StageBandTests` (`:17-51`), `StageDriftClockTests` (`:89-149`), `StageDriftTests`, the `B`/`D`/`L` aliases; keep `StageTransportTests` verbatim (the three predicates are unchanged).

**NEW `VisualizerModelTests.cs`**
- `Catalog_coerces_and_needs` (Theory over the 8 kinds): `NeedsOf` table of §2.1; `Coerce(99) == Horizon`; `UsesSeries` for Horizon/Aurora only.
- `Demand_tiers` — every gate (stageUp/playing/ownerUs/supported/occluded/reduced) false ⇒ `None`; in Visualizer mode: Spectrum kinds ⇒ `Spectrum`, Field/Tape ⇒ `Level`, Horizon/Pulse ⇒ `None`; OUTSIDE Visualizer mode every kind ⇒ `Level` (the base Field's breath is the only consumer — V-U55). `Ticks` runs while settling even when paused; stops when occluded/reduced.
- `Bands_unit_gain_follow_peaks_agc` — `Unit(-60) == 0`, `Unit(-6) == 1`, `Unit(-33) ≈ 0.5`; `Gain(1, calm: true) == 0.55`, `Gain(1.5, false) == 1.5`; `Follow` moves 0.55 up / 0.10 down; `Peaks` hold 10 ticks then fall 0.018; `Agc` gain ≤ 2 and `≥ 0.5`.
- `Model_applies_sensitivity_after_the_agc` — the same quiet frame (every band −40 dB) at sensitivity 1.0 and 1.5: the 1.5 run's `Level` is 1.5× the 1.0 run's (clamped at 1) after the AGC normalised both identically (V-U32).
- `Model_live_bands_move_and_release_to_the_floor` — a `liveDb` ramp with `HaveLive`: `Level` rises; then `HaveLive = false` for 60 ticks → `Bands.Settled(Level)`, `IsSettled` when paused.
- `Model_never_invents_bands_without_a_live_frame` — `HaveLive = false, HaveBands = true`: every `Level[i] <= 0.005` after warm-up while `Low/Mid/High` come from the bands (`Source.Precomputed`).
- `Model_source_ladder` — live → `Live`; bands only → `Precomputed`; tempo only → `TempoGrid`; nothing → `Breath`.
- `Model_beats_from_the_grid_then_the_tempo` — a 120 BPM grid: `BeatPhase` at 250 ms ≈ 0.5, `Kick` = e^{-3}; tempo 1200 (×10) with no grid gives the same phase.
- `Model_horizon_window_is_12_seconds_about_the_playhead` — a 3-minute track (`DurationMs 180_000`) with 9 000 `WaveSample`s and one spike at 10 s (index 500): at pos 10 s the spike is at index 90 of `HorizonLow`; at pos 4 s at index 180; at pos 16 s at index 0; before the start the series is 0 — indexed by DURATION, so the same test over a 4 096-sample decimated payload (spike at index 227) passes too (V-D17).
- `Model_aurora_drifts_even_at_rest` — two ticks at rest differ in `AuroraLow`.
- `Model_tape_reels_conserve_area_and_coast` — `Radii(0) == (R1, R0)`, `Radii(1) == (R0, R1)`, `rL² + rR²` constant; after pause the speed decays to exactly 0 and `IsSettled` becomes true.
- `Model_calm_halves_the_gain_and_the_kick`.
- `Model_tick_allocates_nothing` — warm 100 ticks, `GC.GetAllocatedBytesForCurrentThread()` delta over 1000 == 0.
- `TickHz_matches_Design_Cadence` — `Visualizer.TickHz == Design.Cadence.PluggedLoopHz`.
- `Halo_Matrix_Spectrum_index_maps_stay_in_range` (Theories over j/c/i).

**NEW `WaveformBandsTests.cs`** (WP-D1)
- `Fold_is_the_max_over_the_samples_share_and_nearest_neighbour_when_upsampling` — a 12-long band onto n = 4 → each sample is the max of 3; a 3-long band onto n = 9 → `[b0,b0,b0,b1,b1,b1,b2,b2,b2]`; an empty band → 0.
- `IndexAt_indexes_by_duration_and_clamps` — `IndexAt(90_000, 180_000, 4096) == 2048`; `IndexAt(−5, …) == 0`; `IndexAt(999_999, 180_000, 4096) == 4095`; `durationMs 0` → 0.
- `ToColumns_is_the_decoders_old_reduction_at_read_time` (V-D16 — equivalent, not bit-identical: the kept samples are per-band maxima): a spike `(200,200,200)` in the last sample lands in the last column at 1.0; a first sample `(10,0,0)` gives column 0 = `10/600`; silence → false; an empty span → false.
- `At_and_LevelAt_read_the_sample_under_the_playhead`.
- `BeatGrid_phase_binary_search_and_downbeats` — before the first beat `index == -1`; between beats phase ∈ (0,1); `MarkDownbeats` flags the nearest beat within 40 ms and nothing beyond; `TempoPhase(0)` is false; `TempoPhase(1200, 90_000_250)` ≈ 0.5 (no drift at 25 hours — V-D18).

**`DecodeTests.cs`** (WP-D1 only — V-D13)
- Rewrite `A_waveform_is_reduced_once_to_220_columns…` (`:1548-1580`) as `A_waveform_lands_as_WaveSample_triples_capped_at_MaxSamples`: `Count == Total == WaveformBands.MaxSamples` (the 12 886-sample fixture is max-pooled 3:1), the spike `(200,200,200)` lands in the LAST sample of `Payload`, `Payload[0].Low == 10`, and `WaveformBands.ToColumns(payload, new float[220])` gives `columns[^1] == 1f`; a 2 000-sample fixture lands un-decimated (`Count == 2000`); the silence arm (`:1576-1579`) unchanged — all-zero bands stage an EMPTY Complete run (V-D6).

**NEW `AudioAnalysisDecodeTests.cs`** (WP-D2 — V-D13)
- `AudioAnalysis_stages_beats_in_ms_with_bars_as_downbeats_and_an_empty_answer_is_complete` — a hand-written JSON (`{"beats":[{"start":0.5,"duration":0.5,"confidence":0.9},{"start":1.0,…},{"start":1.5,…}],"bars":[{"start":1.02,…}],"meta":{}}`) → `TrackBeats.Payload(slot)` = `[500, 1000|Downbeat, 1500]`; `{"beats":[]}` → Complete with Count 0.
- `AudioAnalysis_ignores_non_json` — `"not json"` and a truncated `{"beats":[{"start":` → no run, `State == Unknown` (V-D19).

**`FetchRoutesTests.cs:121-136`** (WP-D2) — add `[InlineData(FetchEdge.TrackBeats, RouteTransport.Spclient)]`. **`EdgeDoorTests`** — unchanged (sweeps the enum). **`EntitiesFakeAlbumTests.cs:234`** (WP-D1) — the `WaveformColumns` assertion becomes the `InRange` + `Total` pair of §4.5.1 (V-D3). **`TrackDrawerRulesTests.cs:265-274`** (WP-D1) — deleted with `DrawerRules.Peaks` (V-D4).

**`ShellFrameRulesTests.cs`** — the `ChromeMounted` theory (`:69`) becomes two-input (`ChromeMounted(resolved, immersive)`: fullscreen video OR immersive ⇒ false); the `F11` facts (`:96-103`) gain `F11_closes_the_stage_before_anything_else` (`F11(false, false, stageUp: true) == CloseStage`, `F11(true, true, true) == CloseStage`, the three old facts with `stageUp: false`). **`ShellPlayerBarUiRulesTests.cs:274/:512`** and **`PodcastPlayerBarTests.cs:87`** (WP-U6) — the overflow order gains `OverflowCommand.NowPlaying` where the Expand slot used to toggle the rail (V-U27).

**`DeckModelTests.cs:698-758`** — `A_ppm_is_faster…` keeps its shape (a live tap); `The_meter_rests_without_a_tap` (new: `levels: static () => null` → both angles → `RestDeg`, `IsSettled`); `Level_bands_stay_in_range_and_peaks_never_sit_below_their_band` with `new Deck.LevelModel(24, static () => new float[] { … 48 values … })`; `A_silent_spectrum_relaxes_to_the_floor_and_settles` with `spectrum: null`; delete `A_paused_SCOPE_deck_settles_too` and `A_null_level_tap_is_a_real_answer` (rewritten as `A_null_spectrum_pull_rests_at_the_floor`); NEW `Remap_takes_the_max_of_each_bars_share`. **`DeckClockRulesTests.cs:38-47`** → `new Deck.LevelModel(Deck.Models.WinampBands, spectrum: null)`; `:151-177` `DeckModelOptionsTests` → only the VU is a model option. **`RailTests.cs:229-237`** unchanged. (`Deck.SpectrumFold` is UI — it reads `Playback.Audio` — and is covered by the `--fake`/real-account walk, not a unit test.)

**`SettingsCatalogTests`** — passes by construction (one new section "Fullscreen"/TvMonitor, five new rows with unique glyphs in it; `TvMonitor` is not reused by a row of that section). **`PrefsTests.cs:66-77`** (`BumpAll_moves_every_epoch`) — captures and asserts `Prefs.Stage.Epoch` beside the five (V-D21). **`PlatformTests`** — NEW `Prefs_Stage_clamps_and_bumps_once` over `MemoryAppSettings`: `SetSensitivity(9)` stores 1.5; `SetSyncOffsetMs(-900)` stores −500; `SetMode(7)` stores 0; each write bumps `Prefs.Stage.Epoch` exactly once.

---

## 6. Fixed API contract, work packages, verification

### 6.1 The contract every WP codes against

**Engine (`FluentGpu.Media`)**: `SpectrumAnalyzer(int sampleRate, int fftSize = 2048, int bandCount = 48)` with `FftSize`, `BandCount`, `SampleRate`, `MaxHz`, `BandOf(float hz)`, `BandCenterHz(int)`, `Analyze(ReadOnlySpan<float> mono, Span<float> bandsDb)`, consts `DefaultFftSize/DefaultBandCount/MinHz/MaxHzCap/FloorDb/CeilingDb/TiltDbPerOctave`; `SpectrumRing(int minSamples, int maxBlock)` with `Capacity`, `Written`, `ArmEpoch`, `NewestContent`, `Arm(long contentBase)`, `Write(ReadOnlySpan<float>, int channels)`, `TryCopyContentWindow(long contentEndExclusive, Span<float> dst)`; `SpectrumInfo(long Sequence, int BandCount, bool Muted, float WindowRms, long AlignFrames, float FftMs, bool Live)`; `IAudioEffects.AcquireSpectrum() : IDisposable`, `CopySpectrum(Span<float>, out SpectrumInfo) : int`, `SpectrumOffsetMs : float { get; set; }`; `AudioEffects.SpectrumDemand(long) : long` (internal), `PublishSpectrum(long source, long epoch, ReadOnlySpan<float> bandsDb, bool muted, float windowRms, long alignFrames, float fftMs) : bool` (internal); `PcmAudioSession.OutputDelayFrames : long` (diagnostic), `SpectrumPublishes : long`. `VisualizerFrame.Magnitudes` stays EMPTY (O2).
**Engine (DSL/scene/render)**: `FluentGpu.Foundation.SeriesShape { Baseline, Mirrored, Stroke }`, `SeriesSpec(SeriesShape Shape, ColorF Color, GradientSpec? Gradient, float Thickness, float Baseline, float Amplitude, float Opacity)` + `MaxSamples = 512`, `ChunkSamples = 32` (Mirrored amplitude is against HALF the height); `FluentGpu.Dsl.SeriesSamples(float[] array, int count, uint version)` + `AsSpan()`, `Empty`; `FluentGpu.Dsl.SeriesEl : Element` (`ElementTypeId 18`, `Samples : Prop<SeriesSamples>`, `Shape`, `Color`, `Gradient`, `Thickness`, `Baseline`, `Amplitude`, `Opacity`, layout block); `VisualKind.Series = 10`; `SceneStore.SetSeries/TryGetSeries/ClearSeries/SetSeriesSamples/TryGetSeriesSamples`; `SceneRecordingSnapshot.TryGetSeries/TryGetSeriesSamples`; `DrawOp.DrawSeries = 24`, `Samples32`, `DrawSeriesCmd`, `DrawList.Series(in RectF, in SeriesSpec, ReadOnlySpan<float>, in Affine2D, float opacity, ulong sortKey = 0)`, `HeadlessGpuDevice.LastSeries`, `HeadlessGpuDevice.PrimarySwapchain : HeadlessSwapchain?`, `HeadlessSwapchain.Occluded { get; set; }`, `ISwapchain.IsOccluded` (D3D12Swapchain: `Frame.OccludedLatched || Frame.LastPresentStoodDown`; headless: `Occluded || PresentStandDown`), `InputHooks.WindowOccluded : Signal<bool>?`, `D3D12 SeriesPipeline`/`SeriesInstance`.
**App CORE**: `Stage.Mode`, `Stage.ModeRules.{Count, Coerce, ShowsPane, ToggleGallery, ShowsCaption}`, `Stage.Aspect`, `Stage.Layout.{Seed, Resolve, ClassOf, Q4, CoverSize, CoverX, CoverY, IdentityIsRow, TitleX, TitleY, TitleW, TransportLeft, FaceRight, PaneW, PaneH, TitleFont, MetaFont, Richness, consts}`, `Stage.Transport.{PrimaryEnabled, ShowsQualityBadge, UsesTitle}`, `Stage.Tone.{CrossFadeMs, ScrimA, ScrimVisualizerA, SmokeH, SmokeA, BaseFieldA, BaseFieldBreathA, BaseFieldVisualizerA, Progress}`, `Stage.Entry.{MorphKey, CanEnter(bool videoFullscreen)}`; `Visualizer.{Kind, Need, Tier, Source}`, `Visualizer.Catalog.{Count, Coerce, NeedsOf, UsesSeries}`, `Visualizer.Demand.{For(Kind, bool visualizerMode, bool stageUp, bool playing, bool ownerUs, bool audioSupported, bool occluded, bool reduced), Ticks}`, `Visualizer.Bands.{Count, ClampSensitivity, Unit(float db), Gain(float sensitivity, bool calm), Agc, Follow, Decay, Peaks, Average, Settled, consts}`, `Visualizer.{Halo, Matrix, Spectrum, Aurora, Horizon.Fill(ReadOnlySpan<WaveSample>, long positionMs, long durationMs, …), Pulse, Tape, Field}` statics, `Visualizer.Input`, `Visualizer.Frame`, `Visualizer.Model.{Target, Level, Peak, HorizonLow/Mid/High, AuroraLow/Mid/High, IsSettled, Tick(in Input, ReadOnlySpan<float> liveDb, ReadOnlySpan<WaveSample> bands, ReadOnlySpan<uint> beats, float dtSec)}`, `Visualizer.{TickHz, TickMs, DeltaSec}`; `WaveSample(byte Low, byte Mid, byte High)`; `WaveformBands.{NominalHopMs, MaxSamples = 4096, Columns, Fold, IndexAt(long positionMs, long durationMs, int n), ToColumns(ReadOnlySpan<WaveSample>, Span<float>), At, LevelAt}`; `BeatGrid.{MaxBeats, MaxBars, DownbeatBit, MsMask, StartMs, IsDownbeat, MarkDownbeats, Phase, TempoPhase}`; `Deck.SpectrumPull`, `Deck.LevelModel(int bands, SpectrumPull? spectrum)` + `Remap`, `Deck.Models.Create(in Preset, Func<(float rms, float peak)?> levels, in Input seed, SpectrumPull? spectrum = null)`.
**App UI/SHELL**: `Stage.View()`, `Stage.Open(Action<string>? begin, string cause)`, `Stage.Close(Action<string>? begin, string cause)`, `Stage.AccentSignal`, `Stage.StageCtx` (a CLASS of signals: `Layout, Mode, Kind, GalleryOpen, LyricsOverlay, Calm, Sensitivity, SyncOffsetMs, Chrome, HasTimedLyrics, Accent, Palette, TrackKey, GalleryButton, Slab`; actions `SetMode, ToggleGallery, Exit, Activity, OverControls, MenuOpen, Scrubbing`; `RowValue()/RowPeek()`), `Stage.StageContext`, `Stage.QueuePaneBody`, `Stage.NowPlayingMenu`, `Stage.EnterTerminal/ExitTerminal`, `Stage.Diagnostics.{Version, IsOpen, LastMode, LastTier, LastSource, LastKind, NoteEnter, NoteExit, NoteMode, NotePick, NoteLease, NoteSource}`; `Visualizer.Slab` (+ `LastFftMs, LastAlignFrames, LastSequence, LastTier`), `Visualizer.SeriesSource`, `Visualizer.Palette.From(ColorF, Scheme?)`, `Visualizer.Clock` (no props — reads `Stage.StageContext`), `Visualizer.FaceSpec`, `Visualizer.Face(Kind, Slab, in Palette, in FaceSpec)`, `Visualizer.FieldFace(Slab, in Palette, in FaceSpec, Prop<float>? opacity)`, `Visualizer.Gallery(Stage.StageCtx ctx, Stage.Layout layout, Action close)`, `Visualizer.NameKey(Kind)`; `Lyrics.StagePane(IReadSignal<ColorF> accent)`, `Lyrics.ViewCore(bool large, bool onMedia, Func<bool> visible, EntityId readerEpisode = default, IReadSignal<ColorF>? accent = null, bool onStage = false)` + `Accent`, `OnStage`, `Lyrics.BlurPolicy.ResolveFor(int, bool, bool)`, `Lyrics.Prefs.BlurStrength(bool weakGpu, bool onStage)`; `Shell.FrameRules.ChromeMounted(Video.SurfacePlacement, bool immersive)`, `Shell.FrameRules.F11(bool isFullscreen, bool videoActive, bool stageUp)` + `FullscreenToggle.CloseStage`; `Shell.SeekBar(PlayerChromeFeed? feed = null, Action<bool>? onScrubbing = null)`; `internal static void Shell.BarSeekBy(int)`; `Rail.NowPlayingArtist(Track) : Artist`; `Queue.ContextKindLabel(EntityId)` (Queue.UI.cs); `Playback.Audio.{AcquireSpectrum, CopySpectrum, SetSpectrumOffsetMs, SpectrumDiagnostics}`; `Prefs.Stage.*` (Platform/Prefs.Stage.cs, §4.6), `Platform.Keys.Stage*` (§4.6); `Deck.SpectrumFold.{Levels, Pull(float gain)}`; `Spotify.Api.{AudioAnalysis(string trackId, ApiHost host, ct), AudioAnalysisProbe(string trackUri, ct) : string}`, `Spotify.Decode.AudioAnalysis`, `FetchEdge.TrackBeats`, `SpclientRoute.AudioAnalysis`, `TraitRelation.TrackBeats`, `Edges.TrackWaveform : EdgeTable<WaveSample>`, `Edges.TrackBeats : EdgeTable<uint>`; `Strings.Stage.*` (§4.6 table, incl. `Strings.Stage.Kind.*`, `Strings.Stage.UpNext`), `Strings.Player.Fullscreen`.

### 6.2 Work packages (files are disjoint WITHIN a wave; one Sonnet agent per WP; NO builds between waves)

| WP | files — edit only these | work | wave |
|---|---|---|---|
| **E1** | NEW `..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\Audio\SpectrumAnalyzer.cs`, NEW `…\SpectrumRing.cs`; NEW `..\fluent-gpu\src\FluentGpu.Engine.Tests\SpectrumAnalyzerTests.cs`, NEW `…\SpectrumRingTests.cs` | §4.1, §5.1 | E |
| **E2** | `..\fluent-gpu\src\FluentGpu.Engine\Media\Playback\MediaEffects.cs`, `…\Media\Playback\Audio\PcmAudioPlayer.cs`; `..\fluent-gpu\src\FluentGpu.Engine.Tests\AudioVisualizerDemandTests.cs`; `..\fluent-gpu\docs\plans\media-playback-api-spec.md` (§7.8 AS-BUILT note — the canon owner of the media API, O3/V-E17) | §4.2 (two tiers, double buffer, the content-domain alignment, the pre-gain tap), §5.1 | E |
| **E3** | `..\fluent-gpu\src\FluentGpu.Engine\Foundation\Effects.cs`, NEW `…\Dsl\SeriesEl.cs`, `…\Scene\Columns.cs`, `…\Scene\SceneStore.cs`, NEW `…\Scene\SceneStore.Series.cs`, `…\Scene\SceneRecordingSnapshot.cs`, `…\Scene\SceneRecordingSnapshot.Parity.cs`, `…\Reconciler\Reconciler.cs`, NEW `…\Reconciler\Reconciler.Series.cs`, `…\Hooks\SkeletonDeriver.cs`, `…\Scene\NodeDescriber.cs`, `…\Render\DrawList.cs`, `…\Render\SceneRecorder.cs`, `…\Seams\Rhi\RepaintPolicy.cs`, `…\Render\DrawOpTranslate.cs`, `…\Render\Tiles\SliceOpBounds.cs`, `…\Headless\Rhi\HeadlessGpuDevice.cs` (the series arms AND `HeadlessSwapchain.Occluded/IsOccluded` + `PrimarySwapchain`), `…\Seams\Rhi\Rhi.cs` (`ISwapchain.IsOccluded`), `…\Hooks\Context.cs` (`WindowOccluded`), `…\Hosting\AppHost.cs` (the per-frame publication), NEW `..\fluent-gpu\src\FluentGpu.Windows\D3D12\SeriesPipeline.cs`, `…\D3D12\D3D12Device.cs` (the series arms AND `D3D12Swapchain.IsOccluded`), `..\fluent-gpu\src\FluentGpu.VerticalSlice\Harness\Asserts.cs`, `…\Harness\SuiteRegistry.cs`, `…\Suites\ControlsSuite.cs` (`DecodeVideoLayerNesting` only), `…\Suites\PathSuite.cs` (`StreamSizeGate` only), NEW `…\Suites\SeriesSuite.cs` (series gates + the occlusion gate), `..\fluent-gpu\src\FluentGpu.WindowsApp\Scenes\ShotScene.cs`, `..\fluent-gpu\docs\design\{SPEC-INDEX.md, subsystems\README.md, subsystems\scene-memory.md, subsystems\gpu-renderer.md, subsystems\pal-rhi.md}`, `..\fluent-gpu\docs\guide\components-elements-layout.md`; NEW `..\fluent-gpu\src\FluentGpu.Engine.Tests\HeadlessGpuDeviceSeriesTests.cs` | §4.3 + §4.4 (one WP owns `HeadlessGpuDevice.cs` and `D3D12Device.cs` — O3/V-E8), §5.1 | E |
| **D0 (probe)** | `src/apps/Wavee/Screens/Diagnostics.Headless.cs`, `src/apps/Wavee/Screens/Diagnostics.Probe.cs`, NEW `ops/headless/analysis.wh` | §4.5.3 — the verb and the script; RUNS at the final verification (O5). `Spotify.Api.AudioAnalysisProbe` itself is D2's | D |
| **D1** | NEW `src/apps/Wavee/Entities/Waveform.cs`, `src/apps/Wavee/Spotify/Spotify.Decode.Traits.cs`, `src/apps/Wavee/Entities/Edges.cs` (ALL of it: the `WaveSample` relation, `TrackBeats`, `TraitRelation.TrackBeats`, both arms, `s_waveTargets`, the three doc comments — V-D13), `src/apps/Wavee/Entities/Track.Drawer.cs`, `src/apps/Wavee/Entities/Entities.Fake.Album.cs`; `src/apps/Wavee.Tests/DecodeTests.cs`, NEW `src/apps/Wavee.Tests/WaveformBandsTests.cs`, `src/apps/Wavee.Tests/EntitiesFakeAlbumTests.cs`, `src/apps/Wavee.Tests/TrackDrawerRulesTests.cs` | §4.5.1, §5.2 | D |
| **D2** | `src/apps/Wavee/Entities/Fetch.Routes.cs`, `src/apps/Wavee/Entities/Fetch.Edges.cs`, `src/apps/Wavee/Spotify/Spotify.Api.cs` (`Serves`, the `AnswerRest` arm, `AudioAnalysis`, `AudioAnalysisProbe`, `ProbeWalk`), NEW `src/apps/Wavee/Spotify/Spotify.Decode.Analysis.cs`; `src/apps/Wavee.Tests/FetchRoutesTests.cs`, NEW `src/apps/Wavee.Tests/AudioAnalysisDecodeTests.cs` | §4.5.2 (implemented regardless of the probe — O5), §5.2 | D |
| **D3** | `src/apps/Wavee/Platform/Platform.Settings.cs`, NEW `src/apps/Wavee/Platform/Prefs.Stage.cs`, `src/apps/Wavee/Platform/Prefs.cs` (`BumpAll`), `src/apps/Wavee/Screens/Settings.cs`, `src/apps/Wavee/Screens/Settings.UI.cs` (the `Prefs.Stage.Epoch` read in `SettingsPageView.Render`), `src/apps/Wavee/Screens/Settings.UI.Appearance.cs`, `src/apps/Wavee/assets/loc/{en-US,nl,ko-KR}.json`, `src/apps/Wavee/Playback/Playback.Audio.cs` (the four statics of §4.2 + the `SeedFromSettings` push); `src/apps/Wavee.Tests/PlatformTests.cs`, `src/apps/Wavee.Tests/PrefsTests.cs` | §4.6, §5.2 | D |
| **U1** | `src/apps/Wavee/Shell/Stage.cs` (REPLACE); NEW `src/apps/Wavee.Tests/StageLayoutTests.cs`, DELETE `src/apps/Wavee.Tests/StageTests.cs`, `src/apps/Wavee.Tests/StageSurfaceTests.cs` (trim to `StageTransportTests`) | §4.7, §5.2 | U |
| **U2** | `src/apps/Wavee/Shell/Stage.UI.cs` (REPLACE), NEW `src/apps/Wavee/Shell/Stage.Host.cs` | §4.10, §4.10b | U |
| **U3** | NEW `src/apps/Wavee/Shell/Visualizer.cs`; NEW `src/apps/Wavee.Tests/VisualizerModelTests.cs` | §4.8, §5.2 | U |
| **U4** | NEW `src/apps/Wavee/Shell/Visualizer.UI.cs` | §4.9 | U |
| **U5** | `src/apps/Wavee/Shell/Lyrics.cs`, `src/apps/Wavee/Shell/Lyrics.UI.cs` | §4.11 | U |
| **U6** | `src/apps/Wavee/Shell/Shell.cs`, `src/apps/Wavee/Shell/Shell.UI.cs`, `src/apps/Wavee/Shell/Rail.UI.cs`, `src/apps/Wavee/Shell/Shell.PlayerBar.cs` (the `NowPlaying` overflow command), `src/apps/Wavee/Shell/Shell.PlayerBar.UI.cs` (`SeekBar(onScrubbing:)`, the art's morph tag, the Expand door), `src/apps/Wavee/Shell/Shell.PlayerBar.Podcast.UI.cs` (`BarSeekBy` → internal), `src/apps/Wavee/Entities/Queue.UI.cs` (`ContextKindLabel`); `src/apps/Wavee.Tests/ShellFrameRulesTests.cs`, `src/apps/Wavee.Tests/ShellPlayerBarUiRulesTests.cs`, `src/apps/Wavee.Tests/PodcastPlayerBarTests.cs` | §4.12, §5.2 | U |
| **U7** | `src/apps/Wavee/Shell/Deck.cs`, `src/apps/Wavee/Shell/Deck.UI.cs`, `src/apps/Wavee/Shell/Deck.Faces.cs`, `src/apps/Wavee/Shell/Rail.cs` (`:160` only); `src/apps/Wavee.Tests/DeckModelTests.cs`, `src/apps/Wavee.Tests/DeckClockRulesTests.cs` | §4.13, §5.2 | U |
| **U8** | `src/apps/Wavee/Screens/Diagnostics.UI.cs`, `CHANGELOG.md`, NEW `docs/guide/fullscreen-visualizers.md`, `CLAUDE.md` (the guide link line), `.claude/skills/wavee/SKILL.md` (guide link), `docs/plans/wavee/wavee-0.3-ui/18-shell-frame.md` (`:1140`, the F11 row) | §4.14, §7 | U |

Files are disjoint within every wave (checked: `Edges.cs` → D1 only; `Spotify.Api.cs` → D2 only; `Settings.UI.cs` → D3 only; `HeadlessGpuDevice.cs`/`D3D12Device.cs` → E3 only; `Stage.Host.cs` → U2 only; `Queue.UI.cs`, `Shell.PlayerBar*.cs` → U6 only; `Rail.cs` → U7, `Rail.UI.cs` → U6).

**Merge order (X1 — shared with `playback-smoothness-implementation.md`, issue #167):**
1. Flagship **E** (E1 · E2 · E3), in parallel with playback **0c/0d/0e/0f/0g**.
2. Flagship **D** (D0, then D1 / D2 / D3 in parallel).
3. Flagship **U** (U1–U8 in parallel).
4. Playback **0a + 0b**.
5. Playback **1a / 1b / 1c**.
6. Playback **2**.
7. Playback **3**.
8. Playback **4**.
9. Playback **5**.

The playback plan re-anchors on the post-flagship tree. There is NO build between waves (owner preference): the engine must land before the app is built (the app references `SeriesEl`, `AcquireSpectrum`, `WindowOccluded`), and the one Debug + Release build per repo happens in §6.3.

### 6.3 Orchestrator verification (ONE final Debug+Release build per repo; no builds between waves)

1. **Engine**: `dotnet build src/FluentGpu.slnx` and `-c Release` clean (`TreatWarningsAsErrors`; the DEBUG-only `BindContract`/`ReuseGuard` arms compile only in Debug). `dotnet run --project src/FluentGpu.VerticalSlice` → **ALL CHECKS PASSED**, including `gate.series.*`, `gate.occlusion.hook-follows-swapchain`, `gate.path.stream.sizes [table]`/`[headless-decode]`, and zero allocation in phases 6–13 (`gate.series.bound.zero-alloc` steady = 0). `dotnet run --project src/FluentGpu.WindowsApp -- --screenshot series.png --shot series` → three series (mirrored cyan wave, a gradient ribbon, a white stroke) over `#161414`. `powershell -File docs\design\check-canon.ps1` exits 0. `dotnet test src/FluentGpu.Engine.Tests --filter "FullyQualifiedName~Spectrum|FullyQualifiedName~AudioVisualizerDemand|FullyQualifiedName~Series"` in Debug AND Release.
2. **App**: `dotnet build Wavee.slnx` and `-c Release` clean. `dotnet test src/apps/Wavee.Tests --filter "FullyQualifiedName~Stage|FullyQualifiedName~Visualizer|FullyQualifiedName~Waveform|FullyQualifiedName~Deck|FullyQualifiedName~Decode|FullyQualifiedName~AudioAnalysis|FullyQualifiedName~ShellFrameRules|FullyQualifiedName~ShellPlayerBar|FullyQualifiedName~PodcastPlayerBar|FullyQualifiedName~Platform|FullyQualifiedName~Prefs|FullyQualifiedName~FetchRoutes|FullyQualifiedName~EdgeDoor|FullyQualifiedName~SettingsCatalog|FullyQualifiedName~EntitiesFakeAlbum|FullyQualifiedName~TrackDrawerRules"` in Debug AND Release. State plainly that the full suite did not run; offer it before a release.
2a. **The probe (O5)**: `powershell -File ops\headless\Invoke-WaveeHeadless.ps1 ops\headless\analysis.wh -Configuration Release` (`-Profile <dir>` + a stored credential when a packaged Wavee is installed, §4.5.3). Read the `"ev":"analysis"` line: **A** → done; **B** → adapt `ReadStarts`' member names to the reported `keys` and rebuild; **C** → delete the D2 decoder, route, edge and `TraitRelation` member (§4.5.3) and rebuild — the owner decided this deletion in advance (V-D24).
3. **`--fake` walk** (`dotnet run --project src/apps/Wavee -- --fake`): enter from the Lyrics rail ⛶, the player-bar art (double-click) and the Expand button; the title bar, nav and player bar are gone; Esc and F11 restore the windowed placement (and a maximized window stays maximized — `Win32Platform.cs:975-978`). Each of the four modes; each of the eight faces through the gallery — Field breathes at rest, Horizon shows the fake triangle bands, Pulse rings on the seeded tempo, Halo/Matrix/Aurora/Spectrum rest (no live FFT under `--fake`), the gallery captions say "From Spotify" / "Tempo grid" / "Idle" accordingly. Window at **1440×900**, **ultrawide (3440×1440 or a 2.4:1 resize)**, **portrait (900×1600)**, **640×400** (Compact: no pane, no gallery, caption line, 96-DIP transport without volume). Idle: controls unmount after 3 s, the cursor hides, the hairline shows; a mouse move brings everything back. The TeachingTip shows once on the first Visualizer entry and never again after "Got it". Settings ▸ Appearance ▸ Full screen mirrors the gallery's four settings live. Watch `--fg fps` and the Diagnostics card (mode/kind, tier `None` under `--fake`).
4. **Real account**: local playback → the Diagnostics card shows tier `Spectrum`, FFT ≈ 0.0x ms, a small non-zero "frames behind newest"; Halo/Matrix/Aurora/Spectrum move with the music and the sync offset slider visibly shifts them (a negative offset never blanks the picture — it saturates at the newest sample); mute shows the breath; the volume slider does NOT change the picture (the EQ does — by design, X2); a Connect session (play on another device) falls back to "From Spotify"/"Tempo grid" with no crash; **minimizing the window, or cloaking it (Win+D / a virtual-desktop switch), stops the clock** (`viz.lease` logs `None`) and restoring it resumes; alt-tab alone does NOT stop it; another maximized window on top does NOT stop it either — "covered" is not observable on Wavee's composition swapchain (O4, §4.4), and the plan does not claim it. Space / ← → / ↑ ↓ work on the stage as on the bar; Esc and F11 leave.
5. **Grep that removed symbols are gone** (both repos): `Band.CaptionH`, `Band.PlayerBarH`, `Stage.Pane`, `Stage.DriftClock`, `Stage.Drift`, `Drift.IntervalMs`, `WaveformColumns`, `DrawerRules.Peaks`, `BreatheOmega`, `ScopeDots`, `"vis"` (Rail.cs), `player.opt.analyser`, `player.choice.oscilloscope`, `LevelModel(int bands, bool scope`, `Hash(int i, float t)`, `ChromeMounted(Video.SurfacePlacement resolved) =>` (one-arg), `F11(bool isFullscreen, bool videoActive) ` (two-arg), `BlurStrength(bool weakGpu) ` (one-arg), `Lyrics.StagePane()` (parameterless), `EdgeTable<byte> TrackWaveform`.
6. Nothing is committed or pushed until the owner asks. Engine and app commits go to their own repos (§7.3).

---

## 7. Owner decisions, critical files, CHANGELOG, commits, docs

### 7.1 Decisions (recommendation first; "decided" = already fixed by the approved plan or forced by evidence)

| # | decision | recommendation |
|---|---|---|
| D1 | The stage's mode is a persisted PREFERENCE (`stage.mode`, re-opens where you left it) vs the old session-only `Stage.Pane` | **persisted** (decided — guardrail C10) |
| D2 | `SeriesEl` transport: chunked fixed-POD `DrawSeriesCmd` (32 samples/op, ⌈(N−1)/31⌉ ops) vs an out-of-line render-thread slab | **chunked** — the stream is fixed POD (`DrawList.cs:808-824`), the translate cap is 1024 B (`HeadlessGpuDevice.cs:428`), the only existing slab grows per epoch with no compaction caller (§1.8). Satisfies the guardrail (new DrawOp + pipeline, no `PathData`); no AA fringe in v1 |
| D3 | Idle timing: the engine's `PlayerChromeTiming.Default` 3000 ms vs the prototype's 2800 | **3000** (the tip copy says three seconds; one timing source) |
| D4 | The idle machine's timer: a 250 ms `UseInterval` while the stage is up vs a one-shot `UseTimeout` re-armed at `NextWakeMs` | **one-shot, re-armed from `NextWakeMs`** (REVISED after review — V-U10/V-U48): it is exactly the engine's own owner's shape (`MediaPlayerElement.Sync`, `:390-416`), it seeds the machine with the SAME clock the timer wakes on (`TimerHandle.NowMs`), and it costs nothing while no deadline is pending (paused, over controls, a menu open). The stage is never parked while mounted, so the pause concern behind the old choice does not apply |
| D5 | Where the eight keys live: `Platform.Settings.cs`'s `Keys` partial vs `Platform.cs` | **`Platform.Settings.cs`** (its header names it the landing zone; `Platform.cs` has a 1,100-line budget) |
| D6 | Spectrum tap position: PRE-master-gain (picture independent of the volume slider; mute/volume-0 published as a flag) vs post-gain normalised by the gain | **pre-gain** (decided — guardrail A2; normalising by gain amplifies noise and is undefined at 0). The RMS/peak tap stays post-everything, untouched |
| D7 | **DEVIATION** — the transport card's SEEK is the player bar's own `Shell.SeekBar()` (BarSeekRail), not a WinUI `Slider`; the VOLUME is the WinUI `Slider` | **keep `Shell.SeekBar()`**: `Slider` has no commit/drag-end callback (`Slider.cs:407-408, :471` — `onChange` fires per move), so a `Slider` seek would spam `Playback.SeekTo` or need a second scrub/commit machine that `BarSeekRail` already owns (the previous stage's "never a fork" rule, `Stage.UI.cs:24-25`) |
| D8 | **DEVIATION** — the Winamp "oscilloscope" option (`Rail.cs:160`) is DELETED, not re-implemented on the FFT | **delete**: there is no time-domain tap (and adding one for a 24-dot toy is not worth an RT ring); a scope drawn from the spectrum would be a new fake, which the plan forbids. The VU's no-tap "breath" (`Deck.cs:97`) is deleted for the same reason |
| D9 | The bar art becomes a `MorphId` participant (`Controls.Artwork(..., morphKey:)`), which mounts no shimmer and a frozen placeholder (`Controls.cs:198-203`) | **accept** for a 48-DIP thumbnail (the flight is the flagship moment); if the tint matters, WP-U6 may re-arm a watched placeholder in `Controls.Artwork`'s morph branch. The art's CLICK is unchanged (navigation); the flight is captured from whichever door opens the stage |
| D16 | The stage is **dark-only** (O7): both app themes paint the dark stage ink and material | **REVERSED at integration** — `StageInk.Live` is `Arm(Tok.Theme)` (`Design.cs:614`) and the engine has no per-subtree theme, so the stage's Fluent controls (Slider, ToggleSwitch, SelectorBar) cannot paint dark under a light app; the stage follows the app theme through `StageInk`'s two arms, and the gallery slider keeps the theme's rail/thumb ring |
| D17 | A double-click on the bar art as a stage door | **dropped** (V-U53): the first click of a double-click still fires the single-click navigation (`InputDispatcher.DoubleClickMs = 500`); deferring every single click by 500 ms is worse than one fewer door. The Expand ⛶ beside the art and the rail header ⛶ are the bar's doors; `Stage.Entry.CanEnter(videoFullscreen)` lets an EMPTY stage open, exactly as the rail does today |
| D18 | When the audio-analysis probe runs (O5) | **decided** — with the final verification, not between waves; D2 ships regardless with the tempo fallback live; verdict C ⇒ the orchestrator deletes the decoder/route/edge before the final build (§4.5.3, §6.3 step 2a) |
| D19 | Kind-237 storage (O6): `EdgeTable<WaveSample>` (3-byte triples, Count = Total = N) capped at `WaveformBands.MaxSamples = 4096` with max-pool decimation and DURATION indexing, vs the full-rate 3N byte payload | **decided** — 32 KB per track in the arena instead of ≈ 100–230 KB, no `GrowTraits` ballooning, no fixed-hop assumption; Horizon's 66 ms per point is still finer than the kept 59 ms per sample on a 4-minute track |
| D10 | Seek/position for the faces: `Deck.PositionInterpolator` re-anchored on each 1 Hz report vs the lyrics `MediaClock` | **`PositionInterpolator`** (already CORE-pure and tested; `MediaClock` is tuned for sub-frame lyric sync and lives in the lyrics view) |
| D11 | The Artist pane's "On tour" card | **omit in v1** (the concerts edge shape was not verified here; the pane ships hero + bio + Go to artist + credits) — a follow-up issue |
| D12 | Keep the shell fact's NAME `Ui.ImmersiveLyrics` (six readers across four files) vs renaming it `Ui.Stage` | **keep the name**, re-document it as "the fullscreen stage is open" (no legacy PATH remains; a rename is churn across files other WPs own) |
| D13 | The blob tints snap on a track change (static `Gradient`) while the accent-driven fills cross-fade | **accept for v1** (the blobs sit under a 0.56 scrim; a bound radial gradient would need a per-stop colour bind the engine does not have) |
| D14 | Loc for nl/ko: ship translations now (the table in §4.6) vs rely on the per-key fallback | **ship them** (the keys are few; FLLOC004 stays a warning either way) |
| D15 | audio-analysis endpoint verdict handling (§4.5.3 A/B/C) | **as written**: the probe decides; C keeps the `TrackBeats` machinery and gates the route off in `Serves` |

### 7.2 Critical files

Engine (`C:\wavee\fluent-gpu`):
- `src\FluentGpu.Engine\Media\Playback\Audio\PcmAudioPlayer.cs` (fields `:378-382`, `PublishVisualizer` `:1107-1114`, `RenderBlock` `:1517-1535`, `TapBlock` `:1594-1612`)
- `src\FluentGpu.Engine\Media\Playback\MediaEffects.cs` (`VisualizerFrame` `:17-21`, `IAudioEffects` `:74-94`, `AudioEffects` `:98-185`, `NullAudioEffects` `:189-215`)
- NEW `src\FluentGpu.Engine\Media\Playback\Audio\{SpectrumAnalyzer,SpectrumRing}.cs`
- NEW `src\FluentGpu.Engine\Dsl\SeriesEl.cs`, `src\FluentGpu.Engine\Foundation\Effects.cs` (`:52`), `Scene\Columns.cs` (`:6`), `Scene\SceneStore.cs` (`:158`, `:496-528`, `:1830`), NEW `Scene\SceneStore.Series.cs`, `Scene\SceneRecordingSnapshot.cs` (`:53`, `:273/:337`, `:397`, `:521`, `:552`, `:823`, `:933/:959`, `:982`), `Scene\SceneRecordingSnapshot.Parity.cs` (`:177`, `:215`), `Reconciler\Reconciler.cs` (`:1014-1043`, `:2782`, `:3840`, `:5386`), NEW `Reconciler\Reconciler.Series.cs`, `Render\DrawList.cs` (`:30`, `:104-209`, `:410`, `:806`), `Render\SceneRecorder.cs` (`:2596`), `Seams\Rhi\RepaintPolicy.cs` (`:72-104`), `Render\DrawOpTranslate.cs` (`:17-49`), `Render\Tiles\SliceOpBounds.cs` (`:19-57`), `Headless\Rhi\HeadlessGpuDevice.cs` (`:34`, `:102`, `:249`, `:405`, `:494`)
- `src\FluentGpu.Windows\D3D12\D3D12Device.cs` (`:147`, `:181`, `:236`, `:577-578`, `:604-657`, `:1655/:1690`, `:1812-1822`, `:1899`, `:2177`, `:2997-3001`, `:3050`, `:4485`, `:4560`; `D3D12Swapchain` `:4587`, `:4619`, `:4892`), NEW `src\FluentGpu.Windows\D3D12\SeriesPipeline.cs`
- `src\FluentGpu.Engine\Seams\Rhi\Rhi.cs` (`ISwapchain` `:251-384`, after `:333`), `src\FluentGpu.Engine\Hooks\Context.cs` (`:293`), `src\FluentGpu.Engine\Hosting\AppHost.cs` (`:2955`, `:3946`; `_swapchain` `:380`), `src\FluentGpu.Engine\Headless\Rhi\HeadlessGpuDevice.cs` (`:25`, `:73-74`, `:174-182`, `:240`, `:353-356`; `HeadlessSwapchain` `:466-494`)
- VerticalSlice: `Harness\Asserts.cs` (`:185-211`), `Harness\SuiteRegistry.cs` (`:36-78`), `Suites\PathSuite.cs` (`:846-927`), `Suites\ControlsSuite.cs` (`DecodeVideoLayerNesting`, by symbol), `Suites\ListRowSuite.cs` (`:1-15`, `:91-100` — the Mount shape copied), NEW `Suites\SeriesSuite.cs`; `src\FluentGpu.WindowsApp\Scenes\ShotScene.cs` (`:55-70`)
- Canon: `docs\design\SPEC-INDEX.md` §2, `docs\design\subsystems\{README,scene-memory,gpu-renderer,pal-rhi}.md`, `docs\plans\media-playback-api-spec.md` §7.8

App (`C:\wavee\waveemusic`):
- `src\apps\Wavee\Shell\Stage.cs`, `Shell\Stage.UI.cs` (REPLACED), NEW `Shell\Stage.Host.cs`, NEW `Shell\Visualizer.cs`, NEW `Shell\Visualizer.UI.cs`
- `src\apps\Wavee\Shell\Shell.cs` (`FrameRules` `:1857`: `:1897`, `:1919-1922`), `Shell\Shell.UI.cs` (`:482`, `:500-505`, `:647-648`, `:763-770`, `:774-798`), `Shell\Rail.UI.cs` (`:253-267`, `:471-484`), `Shell\Shell.PlayerBar.cs` (`:60`, `:320-321`, `:448-450`), `Shell\Shell.PlayerBar.UI.cs` (`:87`, `:393-402`, `:597-600`, `:645-659`, `:1522-1746`), `Shell\Shell.PlayerBar.Podcast.UI.cs` (`:16`)
- `src\apps\Wavee\Shell\Lyrics.cs` (`:164-184`, `:1131`), `Shell\Lyrics.UI.cs` (`:56`, `:65-66`, `:77-92`, `:183`, `:319`, `:1911-1917`), `Shell\Lyrics.Transcript.cs` (`:208`, unchanged — positional)
- `src\apps\Wavee\Shell\Deck.cs` (`:79-93`, `:1134-1176`, `:1198-1293`, `:1479-1494`, `:1520-1525`), `Shell\Deck.UI.cs` (`:120-127`, `:147`, `:163`, `:229-263`, `:318-346`, `:383-392`), `Shell\Deck.Faces.cs` (`:980-986`), `Shell\Rail.cs` (`:160`)
- NEW `src\apps\Wavee\Entities\Waveform.cs`, `Spotify\Spotify.Decode.Traits.cs` (`:27-29`, `:191-243`), `Entities\Edges.cs` (`:881-896`, `:1071`, `:1083-1084`, `:1109-1114`, `:1149-1151`, `:1200-1211`), `Entities\Track.Drawer.cs` (`:181-193`, `:783-789`), `Entities\Entities.Fake.Album.cs` (`:64`, `:467`, `:507`, `:530-538`, `:562-569`), `Entities\Fetch.Routes.cs` (`:80`, `:153-183`, `:445`), `Entities\Fetch.Edges.cs` (`:178`, `:219`), `Spotify\Spotify.Api.cs` (`:698-712`, `:1162-1195`, `:2031-2038`), NEW `Spotify\Spotify.Decode.Analysis.cs`, `Entities\Queue.UI.cs` (`:162`)
- `src\apps\Wavee\Platform\Platform.Settings.cs` (`:39`), NEW `Platform\Prefs.Stage.cs`, `Platform\Prefs.cs` (`:279-286`), `Playback\Playback.Audio.cs` (`:229`, `:502-515`), `Screens\Settings.cs` (`:80-81`, `:132`), `Screens\Settings.UI.cs` (`:135-136`), `Screens\Settings.UI.Appearance.cs` (`:53-57`, `:62-63`, `:115-116`, `:661-705`), `Screens\Diagnostics.UI.cs` (`:265-305`), `Screens\Diagnostics.Headless.cs` (`:16-30`, `:78-82`, `:147-155`, `:175-327`), `Screens\Diagnostics.Probe.cs` (`:592-661`), NEW `ops\headless\analysis.wh`
- `src\apps\Wavee\assets\loc\{en-US,nl,ko-KR}.json`
- Tests: NEW `StageLayoutTests.cs`, `VisualizerModelTests.cs`, `WaveformBandsTests.cs`, `AudioAnalysisDecodeTests.cs`; `StageSurfaceTests.cs`, `ShellFrameRulesTests.cs`, `ShellPlayerBarUiRulesTests.cs`, `PodcastPlayerBarTests.cs`, `DeckModelTests.cs`, `DeckClockRulesTests.cs`, `DecodeTests.cs`, `EntitiesFakeAlbumTests.cs`, `TrackDrawerRulesTests.cs`, `FetchRoutesTests.cs`, `PlatformTests.cs`, `PrefsTests.cs`; DELETE `StageTests.cs`

### 7.3 CHANGELOG (`CHANGELOG.md`, under `## [0.3.0] - unreleased` → `### Added`, after the Profile pages bullet `:34-39`)

```
- **A real full-screen Now Playing.** The lyrics stage is now a true full-screen view: the window goes borderless on
  its monitor, the title bar and the player bar step aside, and one transport card sits over the music. A selector
  switches between Lyrics, Visualizer, Up next and Artist while the cover morphs between its hero and thumbnail
  places; the accent follows the album art on every control; the controls hide after three seconds of idleness and
  a hairline keeps the progress visible. Eight visualizers — Field, Halo, Horizon, Matrix, Aurora, Spectrum, Pulse,
  Tape — move on a real spectrum analysis of what is playing, aligned to what you actually hear (with a sync offset
  for Bluetooth), and fall back to Spotify's waveform and beat data when the audio is not local. A gallery shows all
  eight live, with sensitivity, lyrics-overlay and calmer-motion switches; the window adapts to ultrawide, portrait
  and small sizes. The rail's analyser decks now read the same real spectrum. (#166)
```
(`### Changed`: "The full-screen view's lyrics no longer blur non-active lines; the Winamp deck's oscilloscope option is gone — there is no time-domain tap and the trace was synthesised. (#166)")

### 7.4 Commit messages (two repos; nothing is committed until the owner asks)

Engine (`C:\wavee\fluent-gpu`):
```
Spectrum tap, SeriesEl and the occlusion hook for Wavee's fullscreen visualizers

A real radix-2 spectrum analyzer (2048 Hann, 48 log bands, dB +3 dB/oct) runs
on the AudioClock thread over an SPSC mono ring the RT thread fills pre-master-
gain while a spectrum lease exists; publishes are double-buffered behind the
visualizer gate and latency-aligned by PcmAudioSession.OutputDelayFrames plus
a user offset. SeriesEl draws a bound sample series as chunked fixed-POD
DrawSeriesCmds through one instanced strip pipeline. InputHooks.WindowOccluded
exposes the DXGI occlusion latch per frame.

Registered in SPEC-INDEX §2 and the ownership map; VerticalSlice gate.series.*;
check-canon 0.

Fixes christosk92/WaveeMusic#166

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
```
App (`C:\wavee\waveemusic`):
```
Fullscreen Now Playing: true fullscreen, Fluent flagship layout, eight real-time visualizers

Replaces Stage.cs/Stage.UI.cs outright: window fullscreen with prior-state
restore, chrome and player bar unmounted, a SelectorBar for Lyrics | Visualizer
| Up next | Artist, a morphing cover, a per-surface art accent, an acrylic
transport card, idle auto-hide with cursor hide and a hairline, a TeachingTip,
four aspect classes. Visualizer.cs/Visualizer.UI.cs fold the engine's spectrum
into eight faces and a live gallery; kind 237 lands as three 20 ms bands and
/audio-attributes/v1/audio-analysis lands as a beat grid with a tempo fallback;
the deck's synthesised bands are gone.

Fixes #166

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
```

### 7.5 Docs

- NEW `docs/guide/fullscreen-visualizers.md` (WP-U8): title + one-line statement, then `## How it works, in one paragraph` (the §3.6 data flow), `## What the app declares` (modes, kinds, the demand tiers, the fallback ladder, the settings keys), `## Keys on the stage` (Esc / F11 close · Space / ← → / ↑ ↓ as the player bar · media keys via SMTC — V-U54), `## Diagnosing` (the card, the `stage.*`/`viz.*` log events, the sync offset — "a negative offset saturates at the newest sample", "the picture does not follow the volume slider but DOES follow the EQ — by design", "minimized/cloaked pauses the clock; a covering window does not — not observable on a composition swapchain"), `## Adding a face` (CORE fold → slab → bound props; budgets). Link it from `CLAUDE.md:82-84` and `.claude/skills/wavee/SKILL.md:74/:90`.
- `.claude/skills/wavee/SKILL.md`: a one-line pointer to the new guide beside `scrolling.md`.
- `docs/plans/wavee/wavee-0.3-ui/18-shell-frame.md:1140` (WP-U8): the F11 row gains the stage (§4.12).
- Engine: `docs/design/SPEC-INDEX.md` §2 + `subsystems/README.md` §2.1 + `scene-memory.md` + `gpu-renderer.md` as §4.3.8; `subsystems/pal-rhi.md` gains the `ISwapchain.IsOccluded` / `InputHooks.WindowOccluded` row with the composition caveat (E3 — O3/V-E17); `docs/plans/media-playback-api-spec.md` §7.8 (`:760-784`) gains "AS-BUILT 2026-10: the spectrum tier — `AcquireSpectrum`, `SpectrumRing` (content-domain), `SpectrumAnalyzer`, `CopySpectrum`; `OutputDelayFrames` is a diagnostic" (E2); `docs/guide/components-elements-layout.md` gains the `SeriesEl` paragraph.
- This plan's §1 is the before-map; after landing, the stale sentences in `Shell/Stage.*` headers (ch 21 references) are gone with the files.

---

## 8. Verification log (Opus 5.5 review → Fable fix pass, 2026-10-02)

Every verdict in `verdicts-flagship.md` (23 BLOCKER / 43 MAJOR / 38 MINOR + O1–O9 + X1–X4) is resolved below. **FIXED** = the doc was changed and the change re-verified against the source by symbol; **DISPUTED** = the verdict is not applied, with file:line evidence. Where a verdict offered alternatives, the one taken is named. Doc lines are of this revision.

### 8.1 Orchestrator and cross-plan decisions

| ID | resolution | what changed | doc lines |
|---|---|---|---|
| O1 | FIXED | `TapSpectrumBlock(buf, frames, ctx.StartFrame)`; the ring is ARMED (`SpectrumRing.Arm(contentBase)`, monotonic index) on a demand edge, a `RenderEpoch` change or a block discontinuity; the window end = `PlayedFrames + (ConsumeSeqFrames − SubmittedFrames − _pendingFrames) − StreamLatencyFrames − TotalLatencySamples − offset + FftSize/2`, clamped to `ring.NewestContent` (negative offsets saturate). `BlockCtx.StartFrame` is the MIXER-domain frame (`CrossfadeMixer.ConsumeSeq`, `AudioGraph.cs:19-20`, `:1518`); `RebuildSink` zeroes the submitted/played counters but not `ConsumeSeq` (`:1727-1728`), hence the domain shift term. Cross-plan note written; `OutputDelayFrames` kept as a diagnostic only | 231-265, 689-795, 996-1044, 1044-1070 |
| O2 | FIXED | `VisualizerFrame.Magnitudes` stays EMPTY; `PublishVisualizerFrame` unchanged from HEAD; `CurrentMagnitudes()` deleted; `PublishSpectrum`/`ReleaseSpectrum` never touch `_visualizer`; the test asserting `Magnitudes.Length == 48` replaced | 810-829, 861-864, 887-895, 904-934, 5580 |
| O3 | FIXED | E4 folded into E3 (one WP owns `HeadlessGpuDevice.cs` + `D3D12Device.cs`); `pal-rhi.md` (E3) and `media-playback-api-spec.md` (E2) added as canon owners | 1933, 1935-1959, 5665-5666, 5814 |
| O4 | FIXED | `ISwapchain.IsOccluded` on `D3D12Swapchain` (`:4587`, after `:4892`) = `Frame.OccludedLatched \|\| Frame.LastPresentStoodDown`; the composition-swapchain caveat stated in §0, §1.3, §4.4 and §6.3 step 4 (softened to minimized / cloaked / hidden); VerticalSlice gate `gate.occlusion.hook-follows-swapchain` | 24, 82, 1935-1959, 1854-1873, 5701 |
| O5 | FIXED | D0's code lands in wave D; the probe RUNS at the final verification (§6.3 step 2a); D2 implemented regardless; verdict C ⇒ the orchestrator deletes decoder/route/edge then — recorded as D18 | 2408-2450, 5699, 5724 |
| O6 | FIXED | `EdgeTable<WaveSample>` (3-byte triples, Count = Total = N), a dedicated `s_waveTargets` (never `GrowTraits`), DURATION indexing (`IndexAt(pos, dur, n)`), max-pool decimation onto `MaxSamples = 4096`, the all-zero early return — D19 | 1962-2238, 5725 |
| O7 | FIXED | "The stage is dark-only" paragraph in §2.1; D16 in §7.1 | 148-150, 5722 |
| O8 | FIXED | `StageCtx` holds the preference SIGNALS written by ONE epoch effect in `SurfaceCore`; the clock Peeks them; gallery/face/top-bar renders read the signals; the deck clock caches `_gain` per epoch for `SpectrumFold.Pull(gain)`; the gallery's sliders/toggles take the context's signals (seeded once) | 216, 4103-4126, 4200-4212, 3470-3505, 3895-3988, 5494-5524 |
| O9 | FIXED (Space / ← → / ↑ ↓) · DISPUTED (Shift+arrows, media keys) | The stage root routes `Shell.PlayerKey` (`Shell.PlayerBar.cs:448-459`) to `Shell.BarSeekBy` (made `internal`, `Shell.PlayerBar.Podcast.UI.cs:16`), `Playback.SetVolume`, `Shell.TogglePlayPause` — the bar's own map (`Shell.PlayerBar.UI.cs:645-659`). **No Shift+arrow binding exists anywhere** (`PlayerKey` returns `None` for ANY modifier, `:450-459`; the only modified arrows are the Alt+←/→ Back/Forward chords, `Shell.UI.cs:174-175`), so there is nothing to mirror; **media keys are not keyboard-routed** — they arrive through SMTC regardless of focus (`Playback.Os.cs:36-39`, `:309-323`) and already work on the stage. The playback plan's `BarSeekRail` changes reach the stage through `Shell.SeekBar(onScrubbing:)` | 4356-4379, 5406 |
| X1 | FIXED | The nine-step merge order written into §6.2 | 5682-5694 |
| X2 | FIXED | The tap anchored "immediately before `_masterGain.Process`" by symbol; EQ-dependence by design, volume-independence holds | 257-265, 977-982 |
| X3 | FIXED (already) | The eight keys live in `Platform/Platform.Settings.cs` (D5) | 2451-2471 |
| X4 | FIXED | The card goes on `RuntimePageView` (`Diagnostics.UI.cs:265-305`); the stale `:717-753` anchor corrected | 5526-5553 |

### 8.2 Engine (V-E)

| ID | resolution | what changed | doc lines |
|---|---|---|---|
| V-E1 | FIXED | `IsOccluded` implemented on `D3D12Swapchain` (after `:4892`), not `D3D12Device`; the `ISwapchain` interface placement (after `Rhi.cs:333`) was right and stays | 1935-1959 |
| V-E2 | FIXED | Stated plainly: the latch is not reliably returned on composition swapchains (`:3443-3445`), Wavee is composited (`FluentApp.cs:331/:739`); `IsOccluded` folds `LastPresentStoodDown` so minimized/cloaked/hidden IS observed; the real-account step no longer claims "covered" | 24, 82, 1937-1940, 5701 |
| V-E3 | FIXED | `SeriesSuite` rewritten: global namespace, `FluentGpu.Rhi.Headless`, the ListRowSuite `Mount` body, `RunFrame()`'s `FrameStats`, `host.Dispose()`, `SeriesProbe : Component`, `dev.LastSeries.Count == 0` after the frame following `Clear()`, `var prev = …; prev.S[prev.Count-1]`; record checks drive `DrawList.Series` directly (no unverified `LayoutTree`) | 1739-1906 |
| V-E4 | FIXED | `_norm = 4/(N·Σw²)` (Parseval; text at §2.8 and the class doc corrected); the DC test dropped; the 0 dB checks at −30 dBFS and at 60 Hz; the 96 kHz theory uses ≥ 25 dB; the sweep covers `[MinHz, MaxHz)` skipping −1 | 266-268, 516-519, 564-569, 5558-5567 |
| V-E5 | FIXED | `end = Math.Min(audible − offset + FftSize/2, ring.NewestContent)` — a negative offset saturates | 1029 |
| V-E6 | FIXED (both options) | Mirrored amplitude against HALF the height AND `p.y` clamped to the chunk rect in the VS | 1531-1535, 1592, 1605 |
| V-E7 | FIXED | The ramp moves to the PS: the VS passes a signed amplitude coordinate (`+s`/`−s`, 0 at the baseline) and `nointerpolation iid`; `ramp(it, abs(amp))` per pixel | 1560-1565, 1615-1621 |
| V-E8 | FIXED | E4 folded into E3 (O3) | 5666 |
| V-E9 | FIXED | `Magnitudes` never written (O2) | 810-829, 904-934 |
| V-E10 | FIXED | `Blob` passes rest keys with `loop: !reduced` and `DepKey.From(index, w, h, reduced ? 1 : 0)`; §2.9 corrected (the engine does not snap looping keyframes, `AnimScheduler.Timeline.cs:35-55`) | 272-274, 3588-3611 |
| V-E11 | FIXED | O1; the audit doc's §9 claim superseded in the cross-plan note only (the audit doc is not edited) | 257-265 |
| V-E12 | FIXED | `Thread.MemoryBarrier()` before the validation reads (the `AudioLevelMailbox.cs:24` spelling of the same full fence — the verdict named `Interlocked.MemoryBarrier()`); guard band `start ≥ after − Capacity + maxBlock`; `_written` monotonic — `ResetWriter` replaced by `Arm(contentBase)` publishing `_armedAt/_armedBase/_armEpoch`; a window before the arm or across a re-arm is rejected (no stale window on a demand edge) | 689-795 |
| V-E13 | FIXED | `BitOperations.RoundUpToPowerOf2((uint)max(16, minSamples))` with `minSamples = rate · 1.5` | 702, 1019 |
| V-E14 | FIXED | The alloc test arms through `BindEffects` + `AcquireSpectrum` + `PlayAsync` + `PumpAudio` first; the no-lease half uses `PumpAudio`; the release test holds an extra `AcquireVisualizer` lease | 5578-5587 |
| V-E15 | FIXED | Anchor: after `PathSpec`'s closing brace `:63` (declared `:49-63`), before `ClipPathSpec`'s doc comment | 1104 |
| V-E16 | FIXED | `(_seriesPipe?.DroppedInstances ?? 0)` in `DroppedInstanceCount()` (`:2997-3001`) + `Diag.Set("series", "dropped", …)` in `PublishDecodeDiagnostics` beside `:1690` | 1732 |
| V-E17 | FIXED | Canon owners `pal-rhi.md` (E3) and `media-playback-api-spec.md` §7.8 (E2) | 1933, 5665, 5814 |
| V-E18 | FIXED | `DrawOp :7-60`; dispose `:4560`; latch `:3340-3345`/`:3316`; `:962` is CmdReset's counter reset and `RecoverStarvation :1311-1312` bumps `RenderEpoch` (a re-arm trigger); `WaveformModel` at `FluentGpu.Controls\Charts\Waveform.cs:8-10`; `AnimBake` at `Reconciler\AnimBake.cs` | 66, 119, 1393, 1732 |
| V-E19 | FIXED | `SpectrumInfo.WindowRms` = the PRE-gain RMS of the analysed window; the clock uses it under a spectrum lease; the volume-independence claim scoped (a plain level lease reads the post-gain tap) | 802-807, 1036, 212 |
| V-E20 | FIXED | §1.8 corrected: `AnimBake` 0 callers; `Transition` also read at `Interaction.cs:137` and is a `MotionTokenDef?`; `ComponentEl` bakes declarative motion (`WriteAnchorColumns :4269-4326`); `TransitionDynamics.Spring(response, dampingRatio)`; `EdgeFadeSpec` `:278-285`/`:303-305`; `ItemsView.Create`/`AppBarToggleButton.Create` capture at mount | 123, 124 |
| V-E21 | FIXED | `using FluentGpu.Foundation;` in `SeriesPipeline.cs` | 1484-1491 |
| V-E22 | FIXED | `gate.occlusion.hook-follows-swapchain` (SeriesSuite) + `HeadlessGpuDevice.PrimarySwapchain` accessor + `HeadlessSwapchain_IsOccluded_FoldsTheLatchAndTheStandDown` unit test | 1854-1873, 1948, 1958 |
| V-E23 | FIXED | `Columns.cs` (`VisualKind`) and `ControlsSuite.cs` (`DecodeVideoLayerNesting`) anchored by symbol | 1204, 1737 |
| V-E24 | FIXED | `+ FftSize/2`: the window is centred on the audible instant | 1029, 245 |

### 8.3 Data + settings (V-D)

| ID | resolution | what changed | doc lines |
|---|---|---|---|
| V-D1 | FIXED | `new(Tab.Appearance, "Fullscreen", "TvMonitor"),` between "Lyrics" (`:80`) and "Now playing" (`:81`); the five rows anchored at `:132`; glyph map and catalog tests cited | 2523-2531 |
| V-D2 | FIXED | `stage.upNext` added (en/nl/ko) | 2623 |
| V-D3 | FIXED | `EntitiesFakeAlbumTests.cs:234` → `InRange(64, MaxSamples)` + `Count == Total`; the file added to D1 | 2237, 5641, 5668 |
| V-D4 | FIXED | `TrackDrawerRulesTests.cs:265-274` deleted with `DrawerRules.Peaks`; the file added to D1 | 2216, 5668 |
| V-D5 | FIXED | `EdgeTable<WaveSample>`: Count == Total == N (O6) | 2165-2177 |
| V-D6 | FIXED | `if (bytes.IndexOfAnyExcept((byte)0) < 0) return;` before `AddText` — all-zero bands stage an EMPTY Complete run | 2158 |
| V-D7 | FIXED | `const int ZeroTargets = 8` for the `:467`/`:507` spans; a static `s_waveTargets`; N from `DurationMs / NominalHopMs` clamped to [64, MaxSamples]; `TrackBeats` seeded empty in `Traits()`; only the four drawer rows get bands | 2218-2236 |
| V-D8 | FIXED | 32 KB per track in the arena; no `GrowTraits` (a dedicated `s_waveTargets`, `BeatGrid.MaxBeats` wide) — O6/D19 | 2179, 5725 |
| V-D9 | FIXED | Long answers are max-pooled onto `MaxSamples` (`WaveformBands.Fold`), never rejected | 1996-2007, 2149 |
| V-D10 | FIXED | `SeedFromSettings()` pushes `Platform.Keys.StageSyncOffsetMs` into `s_effects.SpectrumOffsetMs` (D3) | 1095-1100 |
| V-D11 | FIXED | O8 | see O8 |
| V-D12 | FIXED | The probe walks the WHOLE body (`ProbeWalk`: top-level keys, `beats`/`bars` element counts), tries `Spclient` then `SpclientWg`, reports a `"verdict"` kind `A/B/C` with A = `200 && beats > 0` | 2265-2293, 2292-2319 |
| V-D13 | FIXED | D1 owns ALL `Edges.cs` edits (relation, `TrackBeats` field next to `TrackWaveform` at `:881-896`, `TraitRelation`, both arms, docs); D2 drops `Edges.cs` and `Track.cs`; D2's tests in NEW `AudioAnalysisDecodeTests.cs`; the late arm anchored before `case TraitRelation.AlbumRecommendations:` | 2165-2177, 2243, 2406, 5668-5670 |
| V-D14 | FIXED | `StageVisualizerPicker` / `StageSensitivitySlider` / `StageSyncSlider` child components with `UseSignalEffect` re-seeding (the `NpvStylePicker` shape, `:694-705`); `_ = Prefs.Stage.Epoch.Value` in `SettingsPageView.Render` (`:135-136` — `AppearanceTab` is a static method called from it) | 2547-2604 |
| V-D15 | FIXED | `Loc.Get` dropped around `Strings.Stage.PlayingFrom(...)`; the rule stated in §4.6 | 4869, 2656 |
| V-D16 | FIXED | `ToColumns` reworded ("equivalent, not bit-identical"), the test renamed, the `ToColumns(payload, 220)` typo → `new float[220]` | 2013-2017, 5630 |
| V-D17 | FIXED | `IndexAt(positionMs, durationMs, n)`; `Horizon.Fill`/`At`/`LevelAt` take the duration; `Input.DurationMs` feeds them | 2008-2011, 3115, 3227 |
| V-D18 | FIXED | `double p = positionMs / (double)periodMs; phase = (float)(p − Math.Floor(p));` | 2106-2116 |
| V-D19 | FIXED | The reader walk is inside `try { … } catch (JsonException) { return; }`; the `"not json"` test | 2360, 2406 |
| V-D20 | FIXED | Recipe via `ops/headless/Invoke-WaveeHeadless.ps1`; no `login` line (the runner waits for online itself, `Diagnostics.Probe.cs:437-438`); `wait online timeout 60000`; `-Profile` for packaged builds; the track-only check (`EntityUri.KindOf`); the blocking `Get` runs through `Spotify.Api.Run` with `s_loop.Post(() => Emit(line))` | 2439-2448, 2425-2439 |
| V-D21 | FIXED | `Prefs.Stage` in NEW `Platform/Prefs.Stage.cs`; `BumpAll()` calls `Stage.Bump()`; `PrefsTests.cs:66-77` covers it; `global::Wavee.Prefs.Stage` noted | 2474, 2521, 2521 |
| V-D22 | FIXED | The nine unused keys pruned; nl "Volledig scherm afsluiten"; "Visualizer" → nl "Visualisatie", ko "비주얼라이저" | 2656, 2614 |
| V-D23 | FIXED | `Edges.cs:1083-1084` and `:1149-1151` doc comments updated; `Diagnostics.Headless.cs:16-30` grammar gains the `analysis` line; the line drift corrected throughout §4.5 | 2178, 2412 |
| V-D24 | FIXED | The verdict-C cleanup is a decided owner decision (O5/D18) | 2410, 5724 |
| V-D25 | FIXED | `SpectrumFold.Pull` folds ONCE per `info.Sequence` and decays ONCE per frame (gated on the frame clock); comment fixed | 5494-5524 |

### 8.4 App UI (V-U)

| ID | resolution | what changed | doc lines |
|---|---|---|---|
| V-U1 | FIXED | CORE `Stage.Ink` → `Stage.Tone` everywhere (the UI files alias `Ink = Wavee.Design.StageInk`) | 2865-2884 |
| V-U2 | FIXED | `Frame(...)` method → `FaceFrame`; faces build `List<CanvasChild>` and pass it to `Canvas.Create(w, h, IReadOnlyList<CanvasChild>)`; `CanvasChildEl`/`Unwrap` deleted; `CanvasChild.Child` is the property name | 3876-3884 |
| V-U3 | FIXED | `DepKey.From(a, b, c, 0)` everywhere a three-int key was written (Blob, the accent effect, ArtistPane) | 3599, 4276, 5132 |
| V-U4 | FIXED | `Palette.From(ColorF, Scheme?)` (`Wavee.Scheme`, `Entities/Palette.cs:67-72`) with `Design.Palette.ToColor(uint)` (`Design.cs:677`); no `ColorF.FromArgb` | 3348-3363 |
| V-U5 | FIXED | `TeachingTip.PlacementMode.Bottom` | 4300 |
| V-U6 | FIXED | `FormatCache.Int((int)…)`; `Controls.Artwork` wrapped in a stretched `BoxEl` | 5141, 5158 |
| V-U7 | FIXED | `stage.upNext`, `stage.kind.{album,playlist,artist,show,search}`, `player.fullscreen` added in all three files; no `entity.*` | 2623, 2626, 2648 |
| V-U8 | FIXED | `HitTestPassThrough = true` on the chrome wrapper, the gallery wrapper's host and the identity wrapper; `HitTestVisible = false` kept only on non-interactive layers | 4463, 4721 |
| V-U9 | FIXED | Vertical = `AlignSelf`, horizontal = `JustifySelf` applied to every layer: smoke/caption/hairline/transport (`End`/`Stretch`), top bar (`Start`/`Stretch`), gallery (`Start`/`End` side · `End`/`Stretch` sheet), the check disc (`Start`/`End`), the pill (`Center`/`Start`), the Horizon veil, the Tape spoke, the artist gradient | 4327, 4336, 4345, 4727, 4734, 4742, 3920, 3678, 3868, 5342, 5144 |
| V-U10 | FIXED (alternative clock) | The machine is created in `Render` with `_wake.NowMs` — the host TIMER clock a `UseTimeout` handle exposes, the clock `MediaPlayerElement` seeds with (`:729-730`) — not `0` and not `Design.FrameTime.NowMs` (a `long` on the predicted present time; `Design.cs:1634-1644`), so its `NextWakeMs` deadlines agree with the timer that wakes it; `Sync()` ALWAYS publishes `ChromeVisible` (value-gated) and applies the cursor, ignoring `Tick`'s bool (`:390-416`) | 194-197, 81, 4379-4403 |
| V-U11 | FIXED | `Key = "viz:" + kind` on a CHILD `BoxEl` of FaceHost's root | 4614-4646 |
| V-U12 | FIXED | No `UseComputed` in a factory: the track key is a `UseSignalEffect` in `SurfaceCore.Render` writing `ctx.TrackKey.SetIfChanged`; `Visualizer.Clock` has no props and reads the context | 4214-4229, 4351, 3367-3426 |
| V-U13 | FIXED | `_active` dropped; `_galleryButton` → `ctx.GalleryButton : Signal<NodeHandle>` written by the toggle's `OnRealized` | 3377, 4797 |
| V-U14 | FIXED | CaptionHost keys a CHILD (`"caption:" + active`) under a stable root | 4646-4708 |
| V-U15 | FIXED | `readonly Signal<bool> _settled` mirrored in `WriteCore` (the `Deck.UI.cs:233/:378-379` pattern); `run` reads `_settled.Value` | 3377, 3404, 3531 |
| V-U16 | FIXED | The anchor is a `Signal<NodeHandle>` read in a `UseSignalEffect`; the tip counts as an open menu (`ctx.MenuOpen(true/false)`) so the chrome holds while it is open | 4287-4304 |
| V-U17 | FIXED | Linear lerp from the colour captured at the change over `elapsed / CrossFadeMs` (`Stage.Tone.Progress`), seeded with the current accent at mount, snapped when the clock is not running; test `Tone_progress_is_linear_and_lands_at_CrossFadeMs` | 2881, 3410-3421, 3521-3530, 5606 |
| V-U18/19/36 | FIXED | O8 | see O8 |
| V-U20 | FIXED (keyed remount) | The `ItemsView` is keyed by `HashCode.Combine(palette, accent.Fill)` (the template freezes at mount, `ItemsView.cs:611-617`); selection reads `ctx.Kind` (over binding tile colours) | 3972 |
| V-U21 | FIXED (ScrollEl) | The grid and the four settings rows sit in a `ScrollEl` (over deriving the tile size) | 3958-3991 |
| V-U22 | FIXED (alternative seam) | `ctx.MenuOpen(±)` counted for the device flyout (`ClosedAction`), the "…" menu (`ContextMenuOptions.OnClosed`, `ContextMenu.cs:43`) and the tip; `OnPointerWheel` → `Activity`; scrubbing via a NEW `Shell.SeekBar(onScrubbing:)` seam — a `PlayerChromeFeed` cannot drive the stage's machine (its `Owner` is `internal MediaPlayerElement?`, `PlayerChromeFeed.cs:21`; an app-created feed is inert), so the feed path is DISPUTED and the seam replaces it | 4184, 4842-4844, 4992, 4312, 5398, 81 |
| V-U23 | FIXED | Portrait/Compact: `TitleX = PadX + HeroArt + Pad`, `TitleY = IdentityTop` (`IdentityIsRow`); Compact: `TransportLeft = PadX + HeroArt + Pad` consumed by the chrome wrapper | 2819-2829, 4735, 5598 |
| V-U24 | FIXED | `(1200,500)` → Ultrawide; the hero excluded from `Richness` (it legitimately shrinks across the Ultrawide promotion) | 2845-2850, 5593, 5600 |
| V-U25 | FIXED | = V-D15 | 4869 |
| V-U26 | FIXED | `ContextKindLabel` in `Entities/Queue.UI.cs`; U6 list corrected | 5410, 5676 |
| V-U27 | FIXED | `Shell.PlayerBar.cs`, `ShellPlayerBarUiRulesTests.cs`, `PodcastPlayerBarTests.cs` added to U6 | 5404, 5676 |
| V-U28 | FIXED | `accent` and `onStage` APPENDED after `readerEpisode`; §6.1 corrected | 5325, 5658 |
| V-U29 | FIXED | `Stage.Close(begin, "navigate")` before `Shell.GoTo` in `MetaLink.Go` and the artist pane's button | 5051, 5161 |
| V-U30 | FIXED | O7 | 148-150 |
| V-U31 | FIXED (BoxEl wrapper) | Every `Flow.Show` child is a `BoxEl` carrying `Enter/Exit` (face, caption, hairline, chrome:top/transport/gallery); the embeds sit inside (over putting `Exit` on the embed) | 4318-4324, 4726-4752 |
| V-U32 | FIXED | `Bands.Unit(db)` is raw; AGC first, then `Bands.Gain(sensitivity, calm)`; §2.8 documents the AGC and the order | 270, 2998-3003, 3206-3214 |
| V-U33 | FIXED | = V-D10 | 1095-1100 |
| V-U34 | FIXED | `TransportCard`, `Hero`, `Chips`, `MetaLink`, `ArtistPane`, `FaceHost`, `Backdrop`, `CaptionHost` read `ctx.RowValue()` (the track-key signal), never `Tracks.Changed` | 4810-4820, 5017-5031, 4115 |
| V-U35 | FIXED | Matrix = 3 nodes per column + 12 static row carvers (108; preview 60); §2.10 corrected | 3686-3724, 284, 289 |
| V-U37 | FIXED (reviewed; collapse kept, no gating) | My first dispute was WRONG and is withdrawn: presence collapse DOES feed activation — `SetSubtreeHidden` (`Reconciler.Presence.cs:73-83`) writes `ActiveSig = !Parked && !Hidden` (`:78-79`) on every component below, the signal is born with that formula (`Reconciler.cs:1163`), and `component-props-contract.md:68-72` names presence and parking "two edges into the ONE activation signal"; `UseInterval` folds `UseIsActive` (`RenderContext.Timers.cs:331-335`). So `ContentRegion.Visible = false` deactivates the whole shell body on stage enter and reactivates it on exit. Each of the five consumers was reviewed and is benign or desirable: `Settings.UI.cs:133` → `EnterPlayback(false)` only closes the video-overrides flyout and drops its anchor (`Settings.UI.Playback.cs:89-95`; never playback state) — desirable on enter; `Show.Page.cs:157` → `Reopen` is a `ListFreshness`-gated SWR refresh on exit (`:229-240`); `Profile.Page.cs:240` → `ProfileAsk` SWR re-ask on exit (`:562-567`); `Recents.Page.cs:271` → drawer geometry reset on enter, `CollapseExpanded` + Prefetch refresh on exit (`:470-482`; the expanded row collapses, as on a tab switch); `Design.cs:2575` → the page re-claims its shell material tint on exit (`:2562`) — desirable. The interval pause under the stage is wanted (the rail deck clock, the rail lyrics tickers, the pages' pollers). Decision: keep the collapse, add no stage predicate. §1.8 and §4.12 rewritten accordingly | 124, 137, 5370-5376 |
| V-U38 | FIXED | `Stage.Host.cs` → U2 only | 5205, 5672 |
| V-U39 | FIXED | `player.opt.analyser`, `player.choice.{spectrum,oscilloscope}` deleted in all three files; the persisted `npv.player.winamp.vis` registry value has no code key after the Seg row goes and is left in place (no reader) | 2656, 5485 |
| V-U40 | FIXED | `Deck.cs` anchors by symbol (`BreatheOmega :1137`, `MeterModel.Tick` else-branch `:1170-1176`, `UpdatePeak :1293`) | 5416-5431, 5481 |
| V-U41 | FIXED | The `SpectrumTap` comment says what the code does (the rail's own fold) | 5488-5490 |
| V-U42 | FIXED | `_hooks = UseContext(InputHooks.Current)` in `Clock.Render`; the lease effect reads the field | 3401, 3428-3441 |
| V-U43 | FIXED | `Stage.Close(null, "f11-shell")` | 5386 |
| V-U44 | FIXED | §2.2 and §3.2: PadX 108, IdentityTop 128, TitleY 688, PaneX 740; a test pins them | 181, 376, 5597 |
| V-U45 | FIXED | `FaceRight = GalleryW + 2·Pad` | 2828-2830, 5599 |
| V-U46 | FIXED | §3.1 tree rewritten to the code (StageCtx, signals, keyed children, chips, grid, BackdropArt, pass-through); the `ButtonPalette.ForAccent` claim dropped (hand-built disc); the `Demand.For` calm-argument claim removed | 315-365, 200, 215, 4894 |
| V-U47 | FIXED (claims dropped) | §0.5: seek = `Shell.SeekBar()` stretched by layout, volume = a fixed 128-DIP `Slider`; no "measured width", no accent-tinted seek rail (no seam exists and none is added) | 15 |
| V-U48 | FIXED | One `UseTimeout` re-armed from `NextWakeMs` — nothing runs while no deadline is pending (D4 revised) | 194-197, 4379-4403, 5716 |
| V-U49 | FIXED | Hairline progress = a bound `Transform = Scale(frac, 1)` with `TransformOriginX = 0`; the stage root has explicit `Width/Height` | 4309, 4348 |
| V-U50 | FIXED | `Backdrop`, `Hero`, `Chips`, `NowPlayingCard`, `PaneHost`, `LyricFacts` are components on the context's signals; `SurfaceCore` never reads `Lyrics.Store.Changed` or the device/format signals | 4405-4587 |
| V-U51 | FIXED | "ONE active row re-renders" (the `&&` short-circuits the signal read on every other row); an explicit `onStage` flag drives blur 0 | 5349, 5325 |
| V-U52 | FIXED | = V-D20 (track prefix via `EntityUri.KindOf`) | 2414-2422 |
| V-U53 | FIXED (double-click dropped) | No double-click entry (D17); `Entry.CanEnter(videoFullscreen)` opens an EMPTY stage like the rail ⛶; the Expand button is the bar's door | 2886-2896, 5399, 5723 |
| V-U54 | FIXED | `18-shell-frame.md:1140` updated (U8); the shortcut table goes in the new guide; no `docs/guide/shortcuts.md` | 5412, 5678, 5811 |
| V-U55 | FIXED | `Demand.For(kind, visualizerMode, …)`: outside Visualizer mode only the base Field's breath shows ⇒ `Tier.Level` | 2966-2976, 205 |

### 8.5 Counts

**FIXED: 117 · DISPUTED: 0.** (V-U37 was first disputed, then overturned by the orchestrator with `Reconciler.Presence.cs:78-79` / `Reconciler.cs:1163` and re-verified here — the dispute is withdrawn and the row is FIXED.) Two partial disputes remain recorded inside FIXED rows: O9's Shift+arrows / media keys (no such bindings exist; media keys ride SMTC) and V-U22's `PlayerChromeFeed` path (inert without a `MediaPlayerElement` owner — replaced by the `onScrubbing` seam).

