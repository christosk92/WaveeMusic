# Playback smoothness — halts, seeking, scrubbing, normalization, quality — design & implementation plan

Repo: `C:\wavee\waveemusic` (app, HEAD `c060e9b7`), engine: `C:\wavee\fluent-gpu` (HEAD `c50e900d`). All paths below are absolute or repo-relative to those two roots; `[app]` = `src/apps/Wavee/`, `[engine]` = `src/FluentGpu.Engine/Media/Playback/`, `[win]` = `src/FluentGpu.Windows/`. Every claim carries a `file:line` from the current trees or a **symbol** when the edit lands after the flagship plan (#166) has moved lines; the evidence behind each audit finding id (`H-`, `S-`, `U-`, `P-`, `N-`, `E-`, `V-`, `Q-`, `R-`) is in `docs/plans/wavee/playback-audit-and-scrubbing.md` §1. Issue: **#167** (milestone 0.3 Crest). CHANGELOG bullets end ` (#167)`; app commit bodies carry `Fixes #167`; the engine repo's commit carries `Fixes christosk92/WaveeMusic#167`. Revision 2 (2026-10-02): every verdict of the Opus 5.5 verification is folded in — see §8.

> Conventions. **CORE** = engine-free (`System` only), alloc-free after warm-up on every tick path, `public` because `Wavee.Tests` is a plain `ProjectReference` (no `InternalsVisibleTo`). **SHELL** = the pump / composition. **UI** = binds and never decides. Blocks that edit `PcmAudioPlayer.cs`, `MediaEffects.cs` or `Diagnostics.UI.cs` are anchored by symbol against the **post-flagship** tree (flagship waves E/D/U land first — §6 X1); the flagship's actual engine shape is stated in §4.3 (X2), not assumed. Where a block REPLACES code, the replaced symbol or range is stated.

---

## 0. The owner's ask, restated as requirements

1. **Playback never halts under heavy computer use.** The threads audio continuity depends on are scheduling-immune; a starved ring produces a short, bounded gap — never a stop/start stutter loop, never a device restart; every incident is counted and attributed in the log and on the Diagnostics page. (**D1**: H-1 + F2 + telemetry ship together. **D2**: hybrid-core laptops are the baseline; F4's power-throttling opt-out lands in the first wave.)
2. **Seeking is silent and instant.** A seek inside already-decoded audio is a 5 ms equal-power crossfade applied on the RT thread; a seek into fetched bytes opens a second decoder and swaps voices under the same crossfade while the old audio keeps playing; a far seek is network-bound with no snap-back. (**D3**: design A with the single-decoder "seek marker" fallback when no `_decoderSlots` lease is free.)
3. **Audible scrubbing** like a DAW: 60 ms Hann grains at the thumb, rate-following, −6 dB, parked to silence after 150 ms of rest; one real seek on release; keyboard scrubbing with a 5/15/30 s accelerating ladder that commits once the key goes idle; Connect scrubs visually and sends one `seek_to`; video shows keyframe previews; while paused the scrub is visual-only (the device is stopped). (**D4**.)
4. **Normalization** gains Quiet/Normal/Loud (−23/−14/−11 LUFS) and album mode — for Spotify Ogg (header bytes 152/156), lossless (catalogue params) and local files (ReplayGain tags) alike; Loud limits; a toggle or mode change is audible on the playing track, ramped. (**D5**.)
5. **First-launch volume ≈ −6 dB**: `SavedVolume` default **0.794** (`0.794³ = 0.5005`, −6.01 dB) instead of 0.7 (−9.3 dB); `UnmuteDefault` follows it. (**D6**.)
6. **The limiter moves before the master volume**, so tone does not change with the slider; the post-volume chain is attenuation-only (§2.6). (**D7**.)
7. **Decode-ahead 2 s plus 1 s kept behind** for instant backward seeks (ring capacity 4 s) — for the live voice **and** every prepared voice. (**D8**.)
8. Every P1 of the audit is fixed (H-1…H-4, S-1, S-2, U-1…U-4, P-1…P-3); F1–F8 land; a 64-tap windowed-sinc polyphase resampler replaces linear interpolation; the Vorbis setup parse is cached by hash; the load-test harness reproduces "heavy use" deterministically and on a real device.
9. No legacy paths: `RecoverStarvation`, the two-phase starvation machine, `WsolaAudioSource.RecoveryFrames`, `LinearResampler`, the per-session GC latency capture/restore, `PlayerSeekAccumulator` and the `Task.Delay(2)` control-plane polls are deleted, not flagged off. (`SeekRail.HoldsDrop`/`CommitHoldMs` stay — §4.13.)
10. **No source-text tests**; every rule is a pure class with its own test; engine RT paths carry zero-alloc pins; all UI text goes through loc keys (en-US, nl, ko-KR).

---

## 1. Current behaviour map (by audit finding)

### 1.1 Continuity chain (H-1, H-2, H-3, H-5…H-13)
- RT thread `FluentGpu.AudioRT`: `Highest` + MMCSS "Pro Audio" — `[engine]Audio/AudioFeedThread.cs:488, 522`; `[win]Wasapi/MmcssProAudio.cs:21`. Waits on the WASAPI period event — `[win]Wasapi/WasapiAudioDevice.cs:219-233` (`WaitOne(Math.Max(1, timeoutMs))` at `:226`, `(uint)Math.Max(0, timeoutMs)` at `:232` — a negative timeout is NOT infinite today).
- Producer `FluentGpu.AudioProducer`: `AboveNormal`, no MMCSS — `[engine]Audio/RingAudioSource.cs:70-84`; pumps 20 ms chunks to a 500 ms target (`:161-191`; sizing `AudioFeedThread.cs:137-138, 216`; on-box `[win]Wasapi/WasapiPcm.cs:58`); prepared voices get their own 1 s / 0.5 s ring at `[engine]Audio/PcmAudioPlayer.cs:75-76`, which `AudioFeedThread.Wrap`/`WrapAdditional` reuse as-is (`:216, 232` — `inner as RingAudioSource ?? new …`).
- Clock thread `FluentGpu.AudioClock`: Normal, `Thread.Sleep(15)` — `AudioFeedThread.cs:487, 562`; the only place `Buffering/Ready → Playing` happens — `PcmAudioPlayer.cs:1397-1408`; the RT thread refuses to render until then — `:1289`.
- Starvation: `RenderBlock` enters phase 1 on an empty ring without writing silence — `:1507-1513`; `RecoverStarvation` — `:1294-1329` — resumes at `PcmReady(StartupReadinessFrames)` which `WsolaAudioSource.RecoveryFrames` clamps to one 20 ms hop — `[engine]Audio/CrossfadeMixer.cs:253-255`, `[engine]Audio/WsolaAudioSource.cs:72`; stops/resets the device when the buffer drains — `:1305-1307`; restarts it — `:1566-1572`. A dead sink (`WritableFrames == -1`) never satisfies the drain test in phase 2 (`:1278-1280`) or starvation phase 1 (`:1300-1302`); phase 1 returns early at `:1501` and phase 2 at `:1277`, so `RecordSinkFailure` (`:1580`) is unreachable from either.
- Control plane polls `Task.Delay(2)` — `:746-747, 816, 839, 847, 1203-1204`; `RingAudioSource.cs:118-128`. Decoder leases `SemaphoreSlim(3)` with a 2 s throwing wait — `:41-42, 99, 176-177`; a lease is released by `DecoderAudioSource.Dispose` (`[engine]Audio/AudioDecode.cs:413-422`), which also **closes the byte source**.
- `RingAudioSource._readFrames` is seeded only by `ApplySeek` (`:246`); `PrepareCoreAsync` seeks the decoder BEFORE building the ring (`PcmAudioPlayer.cs:64-75`), so a prepared ring's `PositionFrames` reads from 0 — the real start is `AudioPreparedItem.StartPositionFrames` (`[engine]QueuePreparation.cs:93-94`).
- EQ mutated from the clock thread — `:1035, 1045, 1076, 1083` → `[engine]Audio/DspStages.cs:144-181`. `MixInto` runs on a struct COPY (`CrossfadeMixer.cs:312-313` `var v = _voices[i]; v.MixInto(…)`), so per-voice state written inside it is lost.
- No power-throttling opt-out anywhere (grep both repos). `App.cs:79` pins `SustainedLowLatency`; the engine re-captures it per session — `:1133-1142, 1834-1838`.
- Telemetry today: RT incident queue `AudioFeedThread.cs:99-106, 355-388`; app drain `[app]Playback/Playback.Audio.cs:2903-2941`; `Metrics` `:4158-4200`; stream `Stats` `[app]Spotify/Spotify.Audio.Stream.cs:100-131`; `Log.Event(level, category, eventId, message, operationId, elapsedMs, ex, params ReadOnlySpan<WaveeLogField>)` — `[app]Platform/Platform.cs:713-714`, `WaveeLogLevel.{Trace,Debug,Info,Warning,Error,Critical}` `:577`, `WaveeLogField.Of` `:582-585`.

### 1.2 Seeking (S-1…S-13, U-1…U-3, U-5, U-8, U-11)
- UI → reducer → pump: `[app]Shell/Shell.PlayerBar.UI.cs:1727-1741` → `[app]Playback/Playback.Host.cs:1186` → `[app]Playback/Playback.cs:1863-1884` (`DoSeek`; `Restart` `:1742-1756` also emits `fx.Seek`) → `Playback.Host.cs:660` (`Execute`, which returns early when `!s_fx.Any`, `:643`; `Any` at `Playback.cs:1547-1550`) → `Playback.Audio.cs:608-616` (`Seek`) → `:2561-2604` (`SeekCoreAsync`/`ApplySeekAsync`) → engine `SeekAsync` `PcmAudioPlayer.cs:1176-1236` (the `IMediaSession` member, `[engine]MediaSeams.cs:453`, reached through `MediaPlayer.SeekAsync` `[engine]MediaPlayer.cs:227`).
- The drop-point hold: `SeekRail.CommitHoldMs = 750`, `HoldsDrop` — `[app]Shell/Shell.PlayerBar.cs:541-551`; released by the first report — `Shell.PlayerBar.UI.cs:1683`; pinned by `Wavee.Tests/ShellPlayerBarUiRulesTests.cs:741-746`.
- Position: `State.Position(now)` — `Playback.cs:1105-1112`; `AudioSignal.Seeked` — `:1990-1994`; `DoAudio` epoch guard — `:1950`; `SeekGate.ReportedPositionMs` — `[app]Playback/Playback.Transitions.cs:1450-1451`; the video host posts `Position` from its own tick — `[app]Playback/Playback.Video.cs:1316-1319`; the silent voice's seek posts `Seeked` — `Playback.Audio.cs:2663`; every load resets the parked seek — `:784-799`.
- Decoder seeks: Vorbis `Playback.Audio.cs:1775-1836` (planner `[app]Playback/Playback.Audio.Ogg.cs:641-749`, index `:300-602`, `Reader.Next` enum `:329`, hole drop `:371`); FLAC `:4050-4112` (planner `[app]Playback/Playback.Audio.Flac.cs:627-749`). The interrupt latch is one-shot (`TakeInterrupt` `:1339-1344`) and body-wide (`Ring.IsInterrupted` `Spotify.Audio.Stream.cs:1044`, consumed at `:956-960`).
- Stream: `Retarget` `Spotify.Audio.Stream.cs:997-1019`; `TryPlan` forward-only `:1266`; `Land` direct-mapped `:1077-1083`; `FillFromDisk` before `_inFlight` `:713-725`; `RangeTimeoutMs` `:537`, the CTS created at `:724` and passed as `cts.Token` to `FetchRangeAsync` (`:735`, signature `:1614`); `LowWaterBytes` `:1294-1299`; the fetch queue is `DropOldest` depth 8 (`:527-528`); `Body.ReadAt` `:1518` and `Ring.ReadAt` `:933` serve ONE reader (one cursor, one epoch).
- Prepared-item seams the redesign reuses: `PcmAudioPlayer.PrepareAtAsync` `:36-37, 75-79`; `TryAddCrossfadeVoice` `:703-730`; `SetVoiceEnvelope` `:734-735` (its `bool` result is ignored by callers today); `SetActiveVoice` `:675-686`; `GainEnvelope.Fade` `CrossfadeMixer.cs:80-90`; `AudioPreparedItem` `[engine]QueuePreparation.cs:75-125`; `PrepareContext.For` `:18-23`; `VoiceScheduler` issues ids from 1,000,000 (`[engine]Audio/VoiceScheduler.cs:84`) while the app issues its own from `s_nextVoiceId` (`Playback.Audio.cs:353, 2339, 2453`).

### 1.3 Scrubbing surfaces
- `BarSeekRail` — `Shell.PlayerBar.UI.cs:1540-1750` (`OnDown :1711`, `OnDragMove :1720`, `OnCommit :1727`, `OnCancel :1743`, the dwell ticker `UseInterval` `:1576`, `UseSignalEffect` `:1573`); the thumb ring is `HitTestVisible = false` (`:1638`); `SeekRail` rules — `Shell.PlayerBar.cs:477-556`; time labels — `Shell.PlayerBar.UI.cs:1808-1820`.
- Keyboard — `Shell.PlayerBar.UI.cs:645-658` (`PlayerKey(key, focused, handled, modified)` with `modified = e.Ctrl || e.Alt || e.Shift`, `Shell.PlayerBar.cs:450-458` — Shift is dropped), `[app]Shell/Shell.PlayerBar.Podcast.UI.cs:13-33` (`s_barSeek`, `BarSeekBy`), `PlayerSeekAccumulator` `Shell.PlayerBar.cs:425-446` (also used by `Wavee.Tests/PodcastPlayerBarTests.cs:17-49`). The engine delivers `OnKeyDown` only (`FluentGpu.Engine/Dsl/Element.cs:171-174`); there is no element key-up.
- Connect forward — `Playback.cs:1866`; video seek — `Playback.Video.cs:468` (`Seek(long ms, bool accurate = true)`; preview planner `:275-288`). The flagship stage will host `Shell.SeekBar()` and route transport keys from its root (X5).
- Engine mixer voice — `CrossfadeMixer.cs:111-177` (`MixVoice`, `MixInto`, `IsFinished :172-176`), `ReadableFrames :220-243`, `PcmReady :246-258`.

### 1.4 Normalization, EQ, volume (N-1…N-3, E-1…E-9, V-1, V-2, U-4)
- `NormalizationFactor` — `Playback.Audio.cs:1129-1135` (pinned by `Wavee.Tests/AudioAdapterTests.cs:140-147`); `GainFor`/header readers — `[app]Spotify/Spotify.Audio.cs:396-411, 1200-1207` (`HeaderGainBytes = 152`); lossless `NormalizationGain` — `:370-376`, applied at file choice `:462`; the Body carries track gain/peak only (`Spotify.Audio.Stream.cs:1420-1426`, learned at open `:2132` and from chunk 0 `:1810-1811`); `INormalizationSource` exposes `GainDb`/`Peak` only (`Playback.Audio.cs:1327-1333`), read by the Vorbis adapter at `:1520`; the toggle seam — `:412, 420-421`; `[app]Screens/Settings.UI.Playback.cs:39, 113-116`; seeding `SeedPlayback` `:75-85`; key `Platform.Keys.NormalizationEnabled` — `[app]Platform/Platform.cs:224`; engine `NormMode.Off` — `Playback.Audio.cs:146, 509, 1005`.
- EQ — `Playback.Audio.cs:707-716`; `Settings.cs:206-240`; engine `BuildGraphSpec` `PcmAudioPlayer.cs:250-267`; `EqStage` `DspStages.cs:103-234`; `BiquadCoeffs.Design` `[engine]Audio/Biquad.cs:17-103` (float record); `LimiterStage.DbToLinear` `DspStages.cs:381`.
- Render order — `PcmAudioPlayer.cs:1521-1533` (`_masterGain` → `_masterChannel` → `graph.RenderMaster` → `_transport.Apply` → `TapBlock`); limiter compiled last — `[engine]Audio/AudioGraphHost.cs:144-159`.
- Volume — taper `Playback.Audio.cs:300-308`; `SavedVolume` default 0.7 — `Platform.cs:215`; mute arm — `Playback.cs:2487-2515` (`UnmuteDefault 0.7` `:2490`); `Muted` fold over the host-static `s_sinkMuted` — `Playback.Host.cs:615, 697, 1028`; persistence per drain — `:661-667`; `SettingsEpoch`/`SettingsChanged` bumped on every `Settings.Set` — `Platform.cs:948-949` (UI-thread signal writes).

### 1.5 Resampler, decoder, MP3
- `LinearResampler` — `[engine]Audio/LinearResampler.cs:31-152` (`ResampleResult` lives there, `:9-17`); callers `[engine]Audio/AudioDecode.cs:148, 170-196, 223`, `[engine]Audio/DspStages.cs:390-404` (`ResampleStage`), `Playback.Audio.cs:1536, 1989, 3907` and `[app]Platform/Modules.Host.cs:2340, 2372, 2468` (AAC). **Every caller refills its hold only when it is empty** (`Playback.Audio.cs:1747` `if (_hold == 0 && !NextFrames())`, `:2139` `if (hold == 0)`, `:4012`, `Modules.Host.cs:2447` `while (_heldCount < srcCh)`), so a resampler that leaves frames unconsumed stalls them.
- Vorbis adapter open — `Playback.Audio.cs:1479-1540` (the window `_win` is needed to read the headers before `dec.Open(ident, setup, _gainLinear)` at `:1521`); `VorbisWorkingSet` pool of 2 — `:1373-1428`; `Vorbis.Decoder.Open` — `[app]Playback/Playback.Audio.Vorbis.cs:1467`.
- NLayer wrapper — `Playback.Audio.cs:1957-2000`; local-file sniff — `:3236-3250` (a 64-byte probe, re-sniff would need a second read).

### 1.6 Engine capabilities relied on (verified)
| need | where |
|---|---|
| MMCSS registration seam | `IRtThreadCharacteristics.Enter()` `AudioFeedThread.cs:14-18`; leaf `MmcssProAudio.cs:18-36`; test fake `FluentGpu.Engine.Tests/AudioManagerWakeTests.cs:210` |
| mixer command SPSC applied at block start, multi-producer safe | `PcmAudioPlayer.cs:305-315, 864-975` (`TryEnqueueMixerCmd` under `_mixerCmdProducerLock`, `DrainMixerCmds`, 64 slots) |
| per-voice chain + envelope + gain scalar | `CrossfadeMixer.cs:111-169`; `AudioGraphHost.BuildVoiceChain` `:46-59` (returns null when the spec has no per-voice effects) |
| prepared voice at a position | `PcmAudioPlayer.PrepareAtAsync` `:36-37` (readiness `rate/10` for seeks `:78`; ring built at `:75-76` AFTER the decoder seek at `:64-74`) |
| ring consumer-side head jump | `PcmRing.DiscardAllConsumerSide` `[engine]Audio/PcmRing.cs:96` |
| RT-safe wake carve-out (event `Set` outside the tripwire) | `AudioFeedThread.cs:303-306` |
| stage latency summed into the clock | `AudioGraphHost.cs:31-33` → `PcmAudioPlayer.cs:1538` → `[engine]Audio/AudioClock.cs:82, 95` |
| process-level Win32 init | `[win]Pal/Win32Platform.cs:35` (`SetProcessDpiAwarenessContext` in the `Win32App` ctor) — not used by this plan (F4 is scoped to the audio leaf, §4.1) |
| headless CLI arms | `[app]Screens/Diagnostics.Probe.cs:40-60, 115-130` |
| Diagnostics runtime page | `[app]Screens/Diagnostics.UI.cs:265-304` (`RuntimePageView`, `_refresh`, `body` list, `Card`/`Row`) |
| settings row + combo + seeding | `Settings.UI.Playback.cs:102-116` (`Row`, `Toggle`), `:324` (`ComboBox.Create` with descriptions), `SeedPlayback :75-85`; new keys go in `[app]Platform/Platform.Settings.cs` (X3; e.g. `:39`) |
| hooks in the rail | `UseSignalEffect` (`UI:1573`), `UseInterval(action, ms, enabled:)` (`UI:1576`), `UseEffect` (`UI:1343`) |

---

## 2. Model and thresholds

### 2.1 Threads after (all MMCSS-protected or irrelevant to continuity)
```
FluentGpu.AudioRT        Highest + MMCSS "Pro Audio"        renders; never blocks; waits INFINITE while held      (unchanged + R-3)
FluentGpu.AudioProducer  AboveNormal + MMCSS "Audio"        decodes ahead                                          (F1)
FluentGpu.AudioClock     AboveNormal + MMCSS "Audio"        state publish, FFT, position; no longer gates render   (F1/F5)
FluentGpu.AudioWorker    AboveNormal                        seek mailbox / retire                                  (unchanged)
process (audio leaf)     PROCESS_POWER_THROTTLING_EXECUTION_SPEED off                                              (F4)
```

### 2.2 Buffers after (48 kHz)
```
WASAPI shared buffer          100 ms                                         unchanged
RT block                      10 ms, ≤ 3 per wake                            unchanged
PCM ring, EVERY voice         capacity 4000 ms = ahead 2000 + behind 1000 + slack 1000   (D8; one RingSizing record on the backend, used by the feed AND PrepareCoreAsync)
   low-water producer wake    1000 ms (= ahead / 2)
   resume cushion             ResumeFrames: 100 ms, ×2 per incident (≤ ahead), halved after 30 s without one; never grown by a seek-caused rebuffer
   start readiness            120 ms (StartupReadinessFrames) — evaluated by the RT itself (F5)
Prepared-next ring            same sizing, readiness 500 ms
Seek-prepare ring             same sizing, readiness ONE block (rate/100 = 10 ms) set explicitly
Scrub grain ring              2 hops (60 ms) ahead
Compressed window             30 s+ (unchanged) + 2 slots protected behind the landing (S-11)
```

### 2.3 Starvation (F2) — numbers
```
starve: active ring empty ⇒ submit SILENCE for the block (inside the tripwire); record the span (submitIndex, frames) in the silence ledger;
        the content clock = raw device played − silence already played (X2); incident severity += frames; ONE xrun per incident
resume: ring ≥ ResumeFrames AND transport phase 0 ⇒ TransportRamp 0 → 1 over 5 ms; ResumeFrames = min(2 × ResumeFrames, TargetAheadFrames) unless the
        starve was a seek rebuffer (_seekRebufferActive); GrowAhead once per incident
device: never Stop()/Reset() on a starve; Start() only if it was never started (the existing _startRequested path)
UI    : Stalled published by the clock thread as today; SeekRail freezes (Advances false); the position holds because the content clock holds
```

### 2.4 Seek (D3)
```
target T (frames, mix domain), active voice ring R:
  if T ∈ [head − behindIntact, tail − fade − 1 block]      ⇒ CmdJumpWithinRing{Target}: the RT computes delta, reads [head, head+fade) as the tail, jumps by delta − fade,
                                                              blends tail × Out ⊕ fresh × In over 5 ms, resets the voice's WSOLA, publishes accepted/refused           (B)
  else if TryAcquireDecoderLease                           ⇒ PrepareAtAsync(view, T, lease) on a non-owning RingSource view; SwapToPreparedAsync (ONE compound cmd)  (A)
  else                                                     ⇒ SeekInPlaceAsync: fade-out-hold (phase 4, gain 0, still rendering), flush, decoder seek, fade-in at ≥ 1 block; no Stop/Reset  (fallback)
stale old audio: the pump starts an 80 ms timer at the request; if the prepared voice has no block by then, the old voice fades to silence (10 ms) and the new fades in from silence
coalescing: one in-flight seek prepare; a newer seek cancels it; the mailbox is last-write-wins and carries the load epoch (dropped when stale, cleared on Load)
position: no Reset; rebase at the device frame where the swap block becomes audible (block-exact, ≤ 10 ms); every Position/Seeked carries the seek generation
interrupt: Body.InterruptPendingRead only on the fallback path (A and B never touch the live producer)
```

### 2.5 Scrub (D4)
```
GrainMs 60 · HopMs 30 (50 % overlap, Hann COLA) · ScrubGainDb −6 via the voice GAIN SLOT · CoalesceMs 50 · self-park after 150 ms without a Retarget · MaxRate 4×
velocity: audio-ms per wall-ms (dimensionless), passed unchanged
cache: 2 s of RESIDENT PCM around the scrub point (Body.TryCopyResident); not resident ⇒ park
release: grain voice fades out (self-retires); the HELD main voice is replaced by a design-A swap at the release point (lease permitting) else SeekInPlace on the held voice
keyboard ladder (bar component): 5 s per repeat for 0.5 s, then 15 s, then 30 s; Shift = 1 s; commits when no repeat for GraceMs 250 (no key-up exists)
paused: visual-only (no grains; the device is stopped)
```

### 2.6 Volume and limiter (D6, D7) — the post-volume chain is attenuation-only (re-verified)
After the relocation the render order is `mixer → graph.RenderMaster (master EQ, LIMITER) → TapSpectrumBlock (flagship, pre-volume) → _masterGain → _masterChannel → _transport.Apply → TapBlock → Write`. Every stage after the limiter can only attenuate: `_masterGain` targets `_muted ? 0 : _volume` with `_volume ∈ [0,1]` (`SetVolume` clamps `:1246`; `TrySetVoice` now clamps `initialVolume` too, §4.3; `RebuildSink` reuses `_volume` `:1713`); `_masterChannel` is a constant-power pan (`cos(bal·π/2) ≤ 1`) or an average (`DspStages.cs:299-309`); `TransportRamp.At` interpolates between two values clamped to `[0,1]` (`[engine]Audio/TransportRamp.cs:38-52`); `Sanitize` clamps ±1 (`WasapiAudioDevice.cs:369-377`). Default volume: `SavedVolume = 0.794f` → `0.794³ = 0.50057` → **−6.01 dB**; `UnmuteDefault = 0.794f`.

### 2.7 Normalization (D5)
```
mode pregain: Quiet −9 dB · Normal 0 · Loud +3 dB   (header/catalogue gains are relative to −14 LUFS; −23/−14/−11 targets); applied ONCE, in NormalizationFactor
album mode  : use (album gain, album peak) when the source carries them — Spotify Ogg header @152/@156 via Body + INormalizationSource, lossless catalogue album params, ReplayGain album tags — else the track pair
factor      : 10^(clamp(gain + pregain, ±30)/20), capped so factor × peak ≤ LimiterCeilingLinear (0.8414 = −1.5 dB) — one constant shared by Ogg, lossless and LimiterSpec.Default
Loud        : the cap is lifted to 1.0 and the (pre-volume) limiter does the limiting
ReplayGain  : tags are −18 LUFS referenced ⇒ +4 dB to the −14 frame before the mode pregain
live change : each voice's GainStage ramps by (factor_new / factor_baked_for_that_voice) over 50 ms — no decoder reopen; baked factors tracked per voice id
```

---

## 3. State machines and data flow

### 3.1 Threads and priorities (after)
```
 UI thread ──Post(Input)──▶ reducer ──Effects──▶ Playback.Audio (pump chain, ThreadPool) ──cmds──▶ PcmAudioSession
                                                                                                │ mixer-command SPSC (64)
 fetch loop (ThreadPool) ──Land──▶ compressed ring ──ReadAt / ProbeAt──▶ decoder(s) ◀── FluentGpu.AudioProducer [MMCSS Audio] ──Write──▶ PcmRing (4 s)
                                                                                                                                           │ Read (copy)
 FluentGpu.AudioClock [MMCSS Audio] ──Advance/Publish/FFT──▶ signals                                           FluentGpu.AudioRT [Pro Audio] ─┘──▶ WASAPI
```

### 3.2 Starvation recovery (F2)
```
          ring empty                                        ring ≥ ResumeFrames ∧ phase 0
 Playing ───────────▶ Starved(silence out, span recorded) ─────────────────────────────────▶ Playing (5 ms fade-in; ResumeFrames ×2 unless seek rebuffer)
    ▲                     │ each block: silence submitted, ledger span extended, severity += frames (one xrun incident)
    └─────────────────────┘ (never Stop/Reset; clock thread mirrors Stalled/Playing for the UI; content clock holds)
```

### 3.3 Seek voice swap (design A) and fallback
```
 Seek(T, epoch, gen) ──mailbox (last write wins, epoch-stamped)──▶ SeekCoreAsync
   ├─ in-ring? ──▶ CmdJumpWithinRing{T} ──RT──▶ tail=[head,head+fade) ▸ jump(delta−fade) ▸ blend 5 ms ▸ WSOLA.Reset ▸ accepted/refused ──▶ Rebase on accept; Seeked(gen)
   ├─ lease? ──▶ PrepareAtAsync(view, T, lease) [producer: decoder seek, 1 block] ──▶ (80 ms stale timer: Out(old,10 ms) if no block yet)
   │        ──▶ CmdSwapVoice{new, outId, fade} ──RT──▶ at = ConsumeSeq + block: Add(new, In@at) + Env(old, Out@at) in ONE step ──▶ old retires; ownership of the Body
   │            handed to the new RingSource view under s_gate; Rebase at the swap block's submit index; Seeked(gen, StartPositionFrames)
   └─ else ──▶ SeekInPlaceAsync: CmdFadeOutHold (phase 4) ▸ wait fade end ▸ ring.SeekFrameAsync ▸ flush ▸ wait ≥ 1 block ▸ CmdFadeIn ▸ Rebase; Seeked(gen)
 newer Seek while preparing ⇒ cts.Cancel(); item disposed, lease released; the newest target runs
```

### 3.4 Scrub grains
```
 Idle ─down─▶ Pressed ─move─▶ Scrubbing ─(no Retarget 150 ms: the SOURCE parks itself)─ … ─move─▶ Scrubbing
   │           │ BeginScrub: Hold(main, 20 ms fade → HoldAtFrame); open the scrub decoder on a view (lease); add the grain voice (gain slot −6 dB)
   │           └ UpdateScrub(pos, v) ≤ 20 Hz ──Volatile──▶ ScrubGrainSource (producer thread): recentre from RESIDENT bytes; cut Hann grain at NextStart; OLA into the ring
   ├─up──▶ ScrubEnd(ms, epoch, gen): grain Out envelope (self-retire) ▸ design-A swap from the held voice at ms (lease) else release-without-fade + SeekInPlace ▸ Idle
   └─cancel─▶ one CmdReleaseVoice{main, fade 20 ms}; grain Out envelope ▸ Idle   (idempotent; also fired by the rail's unmount cleanup)
```

---

## 4. Real code

### 4.1 Engine — thread characteristics (F1) and power throttling (F4)

**`[engine]Audio/AudioFeedThread.cs:14-28`** — extend the seam with a default member so the test fake at `AudioManagerWakeTests.cs:210` keeps compiling (callers invoke it **through the interface**, never on the concrete type):

```csharp
public interface IRtThreadCharacteristics
{
    /// <summary>Register the CURRENT thread as a Pro-Audio RT thread; dispose the returned token to revert. May return null.</summary>
    IDisposable? Enter();
    /// <summary>Register the CURRENT thread as an elevated AUDIO thread (decode-ahead producers, the clock thread): above every
    /// normal-class thread, below the RT feed, exempt from power throttling. Default: no registration (headless, tests).</summary>
    IDisposable? EnterDecode() => null;
}
```

**`[win]Wasapi/MmcssProAudio.cs:17-36`** — add beside `Enter()`; `Enter()` sets `ProAudioRegistered` on success and clears it on failure (surfaced by §4.14; lives in **WP 0f** per X1):

```csharp
/// <summary>True after the last <see cref="Enter"/> succeeded — the Diagnostics "Playback health" card reads it.</summary>
public static volatile bool ProAudioRegistered;

public IDisposable? EnterDecode()
{
    uint taskIndex = 0;
    nint handle = AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
    if (handle == 0) { WasapiAudioDevice.FormatSink?.Invoke($"mmcss Audio registration failed (win32 {Marshal.GetLastWin32Error()})"); return null; }
    WasapiAudioDevice.FormatSink?.Invoke($"mmcss Audio registration ok taskIndex={taskIndex}");
    return new Token(handle);
}
```

**`[engine]Audio/RingAudioSource.cs`** — the producer registers through an **instance** seam handed in at construction (no static: parallel tests build rings concurrently):

```csharp
// ctor (replaces :134-141)
public RingAudioSource(IAudioSource inner, int channels, int ringFrames = 8192, int targetAheadFrames = 4096, int pumpFrames = 1024,
                       int keepBehindFrames = 0, long startFrames = 0, IRtThreadCharacteristics? rt = null)
{
    _inner = inner; _rt = rt;
    _channels = Math.Max(1, channels);
    _ring = new PcmRing(Math.Max(ringFrames, targetAheadFrames + keepBehindFrames + pumpFrames) * _channels, keepBehindFrames * _channels);
    _targetFloats = Math.Min(_ring.CapacityFloats - keepBehindFrames * _channels, targetAheadFrames * _channels);
    _pump = new float[Math.Max(1, pumpFrames) * _channels];
    _readFrames = Math.Max(startFrames, inner.PositionFrames);   // V-PE3: the content cursor starts where the (already sought) decoder is
}

// Produce() :90-113 — first statement inside the try
using var rtToken = _rt?.EnterDecode();
```

`AudioFeedThread.Wrap`/`WrapAdditional` (`:216, 232`) pass `_rt` and the D8 sizing; `PcmAudioPlayer.PrepareCoreAsync` (`:75`) passes the backend's `_rt` (§4.3). **`AudioFeedThread.cs:486-488`** (`Start`): the clock thread gets `Priority = ThreadPriority.AboveNormal`; **`:553-566`** (`ClockLoop`): first statement `using var rtToken = _rt.EnterDecode();`. **`[win]Wasapi/WasapiPcm.cs:36-70`** (`CreateBackend`, **WP 1a**): one `MmcssProAudio` instance is created once, passed to the `PcmAudioPlayer` ctor (`rt:`) and to the feed; the feed is sized per D8 through one record the backend also exposes:

```csharp
var rt = new MmcssProAudio();
var sizing = new RingSizing(BlockMs: 10.0, AheadMs: 2000.0, RingMs: 4000.0, KeepBehindMs: 1000.0);   // D8 (engine record, §4.2)
PowerThrottling.OptOut();                                                                           // F4 — scoped to the audio leaf, once per process
return new PcmAudioPlayer(format, endpointFactory: fmt => new WasapiAudioDevice(fmt), effects: effects, maxBlock: maxBlock,
    driveWithOwnThread: false, decoderFactory: decoderFactory, rt: rt, ringSizing: sizing,
    onSessionCreated: session =>
    {
        var feed = new AudioFeedThread(session, sampleRate: session.Format.SampleRate, rt: rt, sizing);
        … // watcher/controller unchanged (:59-69)
    });
```

**`[win]Wasapi/PowerThrottling.cs`** — NEW (the audio leaf, not every FluentGpu app; one call — `IGNORE_TIMER_RESOLUTION` is moot without `timeBeginPeriod` and is not requested):

```csharp
namespace FluentGpu.Windows.Wasapi;

/// <summary>Opt the process out of EcoQoS execution-speed throttling (D2): the decode-ahead and clock threads must not be slowed on
/// battery while the window is in the background. One hint; a failing call (pre-1709 Windows) is not an error. Idempotent.</summary>
internal static unsafe partial class PowerThrottling
{
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    private const uint Version1 = 1, ExecutionSpeed = 0x1, ProcessPowerThrottlingClass = 4;
    private static int s_done;

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int SetProcessInformation(nint process, uint infoClass, void* info, uint size);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();

    public static void OptOut()
    {
        if (Interlocked.Exchange(ref s_done, 1) != 0) return;
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = Version1, ControlMask = ExecutionSpeed, StateMask = 0 };   // 0 under a set control bit = never throttle
        if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE)) == 0)
            WasapiAudioDevice.FormatSink?.Invoke($"power-throttling opt-out failed (win32 {Marshal.GetLastWin32Error()})");
    }
}
```

**R-3 (`[win]Wasapi/WasapiAudioDevice.cs:219-233`, WP 1a)** — a negative timeout means INFINITE, never a 0 ms spin or a 1 ms poll:

```csharp
public void WaitForWritable(WaitHandle controlWake, int timeoutMs)
{
    if (!IsReady || _event == HANDLE.NULL) { controlWake.WaitOne(timeoutMs < 0 ? -1 : Math.Max(1, timeoutMs)); return; }
    HANDLE* handles = stackalloc HANDLE[2];
    handles[0] = _event; handles[1] = (HANDLE)controlWake.SafeWaitHandle.DangerousGetHandle();
    WaitForMultipleObjects(2, handles, false, timeoutMs < 0 ? 0xFFFFFFFFu /* INFINITE */ : (uint)timeoutMs);
}
```

and `NullAudioSink.WaitForWritable` (`AudioClock.cs:269`) → `controlWake.WaitOne(timeoutMs < 0 ? -1 : timeoutMs)`. `PcmAudioSession.WaitForOutput` passes `-1` when `_transportPhase == 3 && !_started` (§4.3).

### 4.2 Engine — the ring: keep-behind, skip, rewind (D8, seek B)

**`[engine]Audio/PcmRing.cs`** — a protected span behind the consumer whose *intact* extent is tracked exactly (V-PE11):

```csharp
private readonly int _keepBehind;   // floats the producer leaves intact behind the consumer's head
private long _maxHead;              // consumer-owned: the furthest head ever reached since the last flush/clear
private long _floor;                // consumer-owned: nothing below this is intact (set to the tail on a flush, 0 on Clear)

public PcmRing(int minFloats, int keepBehindFloats = 0) { … as today …; _keepBehind = Math.Clamp(keepBehindFloats, 0, _buf.Length / 2); }

/// <summary>Free floats the producer may write: capacity − unread − the protected span behind the head.</summary>
public int FreeFloats => _buf.Length - AvailableFloats - _keepBehind;

public int Write(ReadOnlySpan<float> src)
{
    long tail = _tail;
    long head = Volatile.Read(ref _head);
    int free = _buf.Length - (int)(tail - head) - _keepBehind;              // the behind span is never overwritten
    int n = Math.Min(src.Length, free);
    if (n <= 0) return 0;
    … // copy + Volatile.Write(ref _tail, tail + n) as today (:59-64)
}

public int Read(Span<float> dst) { … as today …; if (head + n > _maxHead) _maxHead = head + n; Volatile.Write(ref _head, head + n); return n; }

/// <summary>Floats behind the head that are still intact: the producer may have overwritten anything older than keepBehind behind the
/// FURTHEST head, and nothing before the last flush/clear exists at all.</summary>
public int BehindFloats { get { long h = Volatile.Read(ref _head); long oldest = Math.Max(_floor, _maxHead - _keepBehind); return (int)Math.Max(0, h - oldest); } }

/// <summary>CONSUMER (RT): jump the head forward within the published data.</summary>
public bool TrySkipConsumerSide(int floats) { long head = _head; if (floats < 0 || floats > (int)(Volatile.Read(ref _tail) - head)) return false; Volatile.Write(ref _head, head + floats); if (head + floats > _maxHead) _maxHead = head + floats; return true; }

/// <summary>CONSUMER (RT): move the head back into the intact behind span.</summary>
public bool TryRewindConsumerSide(int floats) { long head = _head; if (floats < 0 || floats > BehindFloats) return false; Volatile.Write(ref _head, head - floats); return true; }

public void DiscardAllConsumerSide() { long t = Volatile.Read(ref _tail); Volatile.Write(ref _head, t); _maxHead = t; _floor = t; }
public void Clear() { Volatile.Write(ref _head, 0); Volatile.Write(ref _tail, 0); _maxHead = 0; _floor = 0; }
```

**`[engine]Audio/RingAudioSource.cs`** — RT members (copy-only, `AssertRtFirewall`), the readiness event with the loss-free wait protocol (V-PE10), and the per-incident xrun latch (V-PE30):

```csharp
public int KeptBehindFrames => _ring.BehindFloats / _channels;
/// <summary>RT: move the read cursor by <paramref name="deltaFrames"/> (negative = rewind into the kept span). The content cursor
/// moves with it; the producer is untouched (its decoder keeps decoding ahead).</summary>
public bool RtTryJump(int deltaFrames)
{
    AssertRtFirewall();
    bool ok = deltaFrames >= 0 ? _ring.TrySkipConsumerSide(deltaFrames * _channels) : _ring.TryRewindConsumerSide(-deltaFrames * _channels);
    if (ok) Interlocked.Add(ref _readFrames, deltaFrames);
    return ok;
}
/// <summary>"The ring holds ≥ ReadyMinimum frames (or the producer is done)" — a MANUAL-reset kernel event. Waiters use
/// Reset → check → wait so a Set between the check and the wait is never lost; the producer Sets after every pump.</summary>
public readonly ManualResetEvent ReadyWake = new(false);
public int ReadyMinimum;   // Volatile; a waiter writes it before its loop
public bool IsReady(int minFrames) => !HasPendingFlush && (BufferedFrames >= minFrames || ProducerDone);
// Produce(): after PumpAhead():  if (IsReady(Volatile.Read(ref ReadyMinimum))) ReadyWake.Set();
// Dispose(): ReadyWake.Dispose() after the producer's finally (the same place _producerWake is disposed, :111)

// V-PE30: one xrun per incident — Read() latches _starve only on the FIRST short read of an incident; frames accrue every block
private int _inIncident;                       // RT-owned
public int Read(Span<float> dst, int channels)
{
    … as today :266-273 …
    if (got < dst.Length && !_producerDone)
    {
        if (_inIncident == 0) { _inIncident = 1; Interlocked.Exchange(ref _starve, 1); }
        Interlocked.Add(ref _starveFrames, (dst.Length - got) / channels);
    }
    else _inIncident = 0;
    return frames;
}
public void GrowAhead() => Volatile.Write(ref _targetFloats, Math.Min(_ring.CapacityFloats - KeepBehindFloats, _targetFloats * 2));   // called once per incident (FeedOnce, on the latch edge)
```

**`[engine]Audio/AudioFeedThread.cs`** — one sizing record replaces the ms parameters (used by the feed, by `PrepareCoreAsync` and by the headless silent session):

```csharp
/// <summary>Time-domain ring sizing (spec §7.9, D8): the same four numbers for the live, prepared and seek voices.</summary>
public readonly record struct RingSizing(double BlockMs = 10.0, double AheadMs = 500.0, double RingMs = 1000.0, double KeepBehindMs = 0.0)
{
    public static RingSizing Default => new();
    public int BlockFrames(int rate) => Math.Max(1, (int)Math.Round(BlockMs * rate / 1000.0));
    public int AheadFrames(int rate) => Math.Max(1, (int)Math.Round(AheadMs * rate / 1000.0));
    public int RingFrames(int rate) => Math.Max(AheadFrames(rate) + KeepBehindFrames(rate) + 2 * BlockFrames(rate), (int)Math.Round(RingMs * rate / 1000.0));
    public int KeepBehindFrames(int rate) => Math.Max(0, (int)Math.Round(KeepBehindMs * rate / 1000.0));
}
public AudioFeedThread(PcmAudioSession session, int sampleRate, IRtThreadCharacteristics? rt, RingSizing sizing, int maxBlocksPerWake = 3) … // the existing ms ctor (:137-145) forwards with RingSizing(blockMs, aheadMs, ringMs)
public int TargetAheadFrames => _targetAheadFrames;   // WP 0f (X1)
// Wrap :216 / WrapAdditional :232:
var ring = inner as RingAudioSource ?? new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2, _keepBehindFrames, rt: _rt);
// FeedOnce :297-302: on `gapFrames > 0 && !suppress && rings[i].Ring.ConsumeStarve()` (the incident EDGE) → RecordXrun(...) + rings[i].Ring.GrowAhead() once
```

### 4.3 Engine — `PcmAudioSession` (anchored by symbol on the post-flagship tree — X2)

**The flagship's actual shape (binding, from the flagship plan's engine wave E):** `TapBlock(buf, frames)` is unchanged — RMS/peak, post-everything, still called at the end of `RenderBlock`. A NEW `TapSpectrumBlock(buf, frames, ctx.StartFrame)` is called **immediately before `_masterGain.Process`** and pushes into a `SpectrumRing` with its own monotonic `Written`. The FFT window is computed on the clock thread in the **content domain**: `end = PlayedFrames − StreamLatencyFrames − graph latency − userOffset + FftSize/2`, re-armed on a `StartFrame` discontinuity or a `RenderEpoch` change. `MediaEffects.cs` carries the two-tier demand and double-buffered magnitudes. Nothing in this plan edits `MediaEffects.cs` or `TapBlock`/`TapSpectrumBlock`; D7's reorder places `RenderMaster` before `_masterGain`, so `TapSpectrumBlock` stays post-EQ/limiter, pre-volume — intended by both plans.

**Cross-plan note (X2).** `PlayedFrames` must stay a correct **content** clock. F2 therefore does **not** shift `_deviceFrameOrigin`; it records silence spans and the presentation clock subtracts the spans already played. `DevicePaddingFrames` and the flagship's `OutputDelayFrames` are based on **raw** device-played frames (`RawPlayedFrames`, new). F2's silence path calls `TapBlock(silence, frames)` (the level meter shows the dropout) and **does not** call `TapSpectrumBlock`: the content clock does not advance during silence, so the flagship's content-domain window holds on the last real frames by construction — no `StartFrame` discontinuity is produced and no re-arm fires. Stated choice.

**F2 — starvation.** Replace the body of `RenderBlock` from the `ReadableFrames` call to the `return 0` of the starvation branch (today `:1507-1513`); delete `RecoverStarvation` (`:1294-1329`) and the `_starvationPhase == 2` arm in `RtRenderOnce` (`:1290`); delete `_starvedAt`. New fields: `private int _resumeFrames; private long _lastIncidentTick;` (`_resumeFrames = _format.SampleRate / 10` in `TrySetVoice` and after a swap); the silence ledger:

```csharp
/// <summary>Silence the RT submitted during starvation, by device submit index (X2): the presentation clock subtracts the spans the
/// device has already played, so PlayedFrames stays a CONTENT clock while the raw device count keeps running. 64 spans, RT-owned
/// writes, control-side reads through Volatile cursors; a full ledger coalesces into the newest span.</summary>
private struct SilenceSpan { public long SubmitIndex; public int Frames; }
private readonly SilenceSpan[] _silence = new SilenceSpan[64];
private int _silenceHead, _silenceTail;          // SPSC: RT writes tail, control reads
private long _silencePlayedCache;                // control-side: frames of silence already played at the last sample
private long _rawPlayedFrames;                   // control-side: the device's own played count at the last sample

// RenderBlock — the starvation branch
int readable = _mixer.ReadableFrames(frames, out var waitingFor);
if (readable <= 0 && waitingFor is not null)
{
    if (_starvationPhase == 0) { _starvationPhase = 1; _starvedRing = waitingFor; }
    return RenderSilence(frames, waitingFor);
}
if (_starvationPhase != 0)
{
    // Resume behind the cushion and only while the transport is running (phase 0): a pause fade must not be retargeted away.
    if (!_mixer.PcmReady(_resumeFrames) || Volatile.Read(ref _transportPhase) != 0) return RenderSilence(frames, _starvedRing);
    _starvationPhase = 0; _starvedRing = null;
    if (!_seekRebufferActive) _resumeFrames = Math.Min(_resumeFrames * 2, _feed?.TargetAheadFrames ?? _resumeFrames);   // V-PE10: a seek rebuffer is not an incident
    _lastIncidentTick = Environment.TickCount64;
    _transport.Retarget(0f, _mixer.ConsumeSeq, 1);
    _transport.Retarget(1f, _mixer.ConsumeSeq, Math.Max(1, _format.SampleRate / 200));
}

private int RenderSilence(int frames, RingAudioSource? ring)
{
    var silence = _mixBuf.AsSpan(0, frames * _format.Channels);
    AudioTripwire.BeginBlock();
    silence.Clear();
    ring?.RecordStarvedFrames(frames);                                 // severity; the ring's own Read latch made this ONE incident (V-PE30)
    TapBlock(silence, frames);                                         // level meter sees the dropout; the spectrum tap is NOT fed (content did not move)
    AudioTripwire.EndBlock();
    _pendingFrames = frames; _pendingOffset = 0;
    _mixer.PublishDrained(_mixer.ConsumeSeq);
    int tail = _silenceTail, next = (tail + 1) & 63;
    if (next == Volatile.Read(ref _silenceHead)) _silence[(tail - 1) & 63].Frames += frames;            // full: coalesce
    else { _silence[tail] = new SilenceSpan { SubmitIndex = _submittedFrames, Frames = frames }; Volatile.Write(ref _silenceTail, next); }
    return SubmitPending();
}

// SessionAudioClock (:323-334) — the CONTENT clock subtracts played silence; a new RawPlayedFrames keeps the device truth for padding/delay
public bool TryGetPlayed(out long frames, out long qpc)
{
    bool valid = session._clock.TryGetPlayed(out long deviceFrames, out qpc);
    long raw = deviceFrames + Interlocked.Read(ref session._deviceFrameOrigin);
    Interlocked.Exchange(ref session._rawPlayedFrames, raw);
    frames = Math.Clamp(raw - session.SilencePlayedBefore(raw), 0, session.SubmittedFrames);
    return valid;
}
private long SilencePlayedBefore(long rawPlayed)   // control thread: walk the spans; drop the ones fully played; a partial span counts its played part
{
    long sum = _silencePlayedCache; int head = _silenceHead, tail = Volatile.Read(ref _silenceTail);
    while (head != tail)
    {
        var s = _silence[head];
        if (rawPlayed >= s.SubmitIndex + s.Frames) { sum += s.Frames; head = (head + 1) & 63; _silencePlayedCache = sum; Volatile.Write(ref _silenceHead, head); continue; }
        if (rawPlayed > s.SubmitIndex) sum += rawPlayed - s.SubmitIndex;
        break;
    }
    return sum;
}
public int DevicePaddingFrames => (int)Math.Clamp(SubmittedFrames - Interlocked.Read(ref _rawPlayedFrames), 0L, int.MaxValue);   // raw (X2)
```

`CmdReset` (`:950-968`) also clears the ledger (`_silenceHead = _silenceTail = 0; _silencePlayedCache = 0;`). The clock thread decays the cushion: in `Advance` `Playing`, `if (_resumeFrames > _format.SampleRate / 10 && Environment.TickCount64 - _lastIncidentTick > 30_000) { _resumeFrames = Math.Max(_format.SampleRate / 10, _resumeFrames / 2); _lastIncidentTick = Environment.TickCount64; }`. **`CrossfadeMixer.PcmReady:246-258`** loses the `RecoveryFrames` clamp (`int recovery = thresholdFrames;` … `< Math.Min(recovery, ring.TargetFrames)`); **`WsolaAudioSource.cs:72`** `RecoveryFrames` is deleted.

**H-3 — dead sink, all phases (V-PE8).** At the top of `RtRenderOnce`, before the phase gate (`:1274`):

```csharp
int phase = Volatile.Read(ref _transportPhase);
if (_out is IBufferedAudioSink probe && probe.WritableFrames < 0 && phase is 1 or 2)
{
    _pendingFrames = _pendingOffset = 0;                               // drop what a dead device will never accept
    RecordSinkFailure();                                               // the controller's non-restamping rebuild request
    try { _out.Stop(); } catch (AudioDeviceLostException) { }
    _started = false;
    Volatile.Write(ref _transportPhase, 3);                            // the fade is over as far as this device is concerned
    _phaseWake.Set();
    return 0;
}
```

**F5 — render before the clock thread flips (V-PE29).** The RT evaluates readiness itself; no control flag:

```csharp
// :1289 — replaced
bool mayRender = _state is PlaybackState.Playing or PlaybackState.Stalled
    || (_state is PlaybackState.Ready or PlaybackState.Buffering && _playRequested && !_transportHoldRequested && _mixer.PcmReady(StartupReadinessFrames));
if (phase == 3 || !mayRender || (!_playRequested && phase is not (1 or 4))) return 0;
```

`_state`, `_playRequested`, `_started`, `_transportHoldRequested` become `volatile` fields (byte-enum/bool — `Volatile.Read` on a `byte` enum does not compile, V-PE27). `Advance`'s `Ready`/`Buffering` arms call `PublishPosition(sink)` and `PublishVisualizer()` when `_started`.

**F7 — signalled completions (V-PE9).** Manual-reset kernel events, the Reset→check→wait protocol, a 20 ms recheck and no success-on-timeout:

```csharp
private readonly ManualResetEvent _appliedWake = new(false);   // RT: Set() at the end of DrainMixerCmds when ≥ 1 command applied (outside the tripwire, like the low-water wake)
private readonly ManualResetEvent _phaseWake = new(false);     // RT: Set() whenever _transportPhase changes

/// <summary>Await a kernel event without a timer tick: the pool's registered-wait thread completes the TCS when the handle signals or
/// <paramref name="timeoutMs"/> elapses (false). The registration is always unregistered.</summary>
internal static async Task<bool> WaitAsync(WaitHandle handle, int timeoutMs, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    RegisteredWaitHandle reg = ThreadPool.RegisterWaitForSingleObject(handle, static (s, timedOut) => ((TaskCompletionSource<bool>)s!).TrySetResult(!timedOut), tcs, timeoutMs, executeOnlyOnce: true);
    using var ctr = ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs);
    try { return await tcs.Task.ConfigureAwait(false); }
    finally { reg.Unregister(null); }
}

/// <summary>Loop until <paramref name="condition"/> holds or the session is disposed; the event is RESET before each check so a Set
/// that lands between the check and the wait wakes the next iteration; a 20 ms recheck bounds any lost wake-up with several waiters.</summary>
private async ValueTask UntilAsync(ManualResetEvent wake, Func<bool> condition, CancellationToken ct)
{
    while (!_disposed)
    {
        wake.Reset();
        if (condition()) return;
        await WaitAsync(wake, 20, ct).ConfigureAwait(false);
    }
    throw new ObjectDisposedException(nameof(PcmAudioSession));
}
private ValueTask WaitAppliedAsync(long sequence, CancellationToken ct) => UntilAsync(_appliedWake, () => Interlocked.Read(ref _appliedSequence) >= sequence, ct);
private ValueTask WaitPhaseAsync(int phase, CancellationToken ct) => UntilAsync(_phaseWake, () => Volatile.Read(ref _transportPhase) == phase, ct);
private static async ValueTask WaitRingReadyAsync(RingAudioSource ring, int minFrames, CancellationToken ct)
{
    Volatile.Write(ref ring.ReadyMinimum, minFrames);
    while (true) { ring.ReadyWake.Reset(); if (ring.IsReady(minFrames)) return; if (!await WaitAsync(ring.ReadyWake, 20, ct).ConfigureAwait(false)) ct.ThrowIfCancellationRequested(); }
}
```

`PostMixerCommandAsync` (`:833-842`), `FadeOutAsync`'s phase poll (`:746-747`), `ReplacePreparedCoreAsync`'s `TrySetVoice` retry (`:813-817`) and the `BufferingReady` poll (`:1203-1204`) all route through these; every `Task.Delay(2)` in the file goes.

**H-4 — EQ through the command queue (V-PE25, V-PE31).** `MixerCmd` gains `public float Linear; public BiquadCoeffs[]? Coeffs; public BiquadBand[]? Bands;` and kinds `CmdSetEq = 10, CmdSetVoiceGain = 11, CmdJumpWithinRing = 12, CmdSwapVoice = 13, CmdHoldVoice = 14, CmdReleaseVoice = 15, CmdFadeOutHold = 16`. Every voice chain is `[GainStage(1f), EqStage(identity)]` — `AudioGraphHost.BuildVoiceChain` (`:46-59`) always emits both, the EQ stage `Bypassed` when the spec has no bands, so enabling the EQ later can ramp from identity (E-6) and the gain slot exists for preamp × normalization. `ReconcileEffects` (`:1022-1061`) designs coefficients off-thread and enqueues `CmdSetEq{Id = ActiveVoiceIdValue, Coeffs, Bands, Linear = preamp}` on every band change (gain-only or topology); a failed enqueue sets `_eqDirty = true` and the next tick retries; `DrainMixerCmds` applies `FindEq(chain).AdoptPending(bands, coeffs)` (which first **commits** a pending cascade if a ramp is mid-flight, then seeds `_statePending` from `_stateActive` and restarts the 256-sample cross-ramp) and `FindGain(chain).SetTargetLinear(c.Linear × voiceNormDelta, DefaultRampSamples)`. `EqStage` is sized once for `MaxBands = 16`; `SetBands`/`SetBandGain` become control-side design helpers that return `BiquadCoeffs[]`. The preamp is `LimiterStage.DbToLinear(-Math.Max(0f, maxBandGainDb))` recomputed on every band-gain change (V-PE25). `RebaseReplayGain` (`:1063-1071`) becomes `CmdSetVoiceGain` per voice; `_masterChannel.SetTargetBalance` (`:1050`) moves to the RT via a `Volatile` target read in `RenderBlock`.

**D7 — limiter pre-volume.** The master section of `RenderBlock` (today `:1529-1532`) becomes:

```csharp
graph.RenderMaster(buf, frames, ctx);                                   // master EQ + TERMINAL LIMITER, pre-volume (D7)
TapSpectrumBlock(buf, frames, ctx.StartFrame);                          // flagship (unchanged call): post-EQ/limiter, pre-volume
_masterGain.Process(buf, buf, frames, ctx);                             // ≤ 1
_masterChannel.Process(buf, buf, frames, ctx);                          // ≤ 1
_transport.Apply(buf, frames, _format.Channels, ctx.StartFrame);        // ∈ [0,1]
TapBlock(buf, frames);                                                  // flagship (unchanged): RMS/peak post-everything
```

`TrySetVoice` clamps `initialVolume` (`_volume = Math.Clamp(initialVolume, 0f, 1f)`). **H-13.** Delete `EnterSustainedLowLatency`, `_prevGcLatencyMode` and the restore. **H-8 (V-PE33).** `_decoderSlots.WaitAsync(TimeSpan.FromSeconds(10), ct)`; on timeout throw `InvalidOperationException($"Audio decoder producers did not retire: {DescribeProducers()}")` where `DescribeProducers()` lists `_feed.RingsSnapshot` entries (`VoiceId`, `Ring.Inner.GetType().Name`, `BufferedFrames`, `ProducerDone`) — the session never proceeds without a lease. **`TryAcquireDecoderLease(out IDisposable lease)`** = `_decoderSlots.Wait(0)` → `new DecoderLease(_decoderSlots)`; **`PrepareAtAsync(next, ctx, positionFrames, lease, ct)`** (V-PE13) hands that lease to the `DecoderLease` chain (`BuildTrimmedVoice(…, lifetime: lease)`) and disposes it on every failure path — the existing overload acquires its own. **R-3.** `WaitForOutput` passes `-1` when `_transportPhase == 3 && !_started`. **R-11.** The RT-read control fields are `volatile`. **S-6.** `PublishPosition` returns early while `_replacementGate.CurrentCount == 0`. **D8 sizing (V-PE5).** The backend holds `RingSizing _sizing` (ctor `ringSizing:`); `PrepareCoreAsync` (`:75-78`) becomes:

```csharp
int rate = ctx.Format.SampleRate;
ring = new RingAudioSource(voice, ctx.Format.Channels, _sizing.RingFrames(rate), _sizing.AheadFrames(rate), _sizing.BlockFrames(rate) * 2,
    _sizing.KeepBehindFrames(rate), startFrames: achieved, rt: _rt);
ring.StartProducer();
int readyFrames = forSeek ? Math.Max(1, rate / 100) : Math.Max(1, rate / 2);             // seek: ONE block (V-PE14); next: 500 ms
await ring.WaitUntilReadyAsync(readyFrames, ct).ConfigureAwait(false);
return new AudioPreparedItem(ring, decoder.Gapless, info.Loudness, totalFrames, info.Duration, rate, achieved, readyFrames);
```

### 4.4 Engine — seek: `IMediaSession.SeekAsync` kept as the dispatcher; `TryJumpWithinRing`, `SwapToPreparedAsync`, `SeekInPlaceAsync` (D3)

`SeekAsync(TimeSpan, SeekMode)` stays (V-PE16: `IMediaSession`, `MediaPlayer.SeekAsync`, 15 tests) and dispatches: `_feed is null` → the existing direct-decoder branch (`:1206-1216`, single-thread pull path); else `TryJumpWithinRing(frame)` → `SeekInPlaceAsync(frame, ct)`. Design A is driven by the app's pump through `SwapToPreparedAsync` because only the app owns the byte source.

```csharp
/// <summary>Seek B: post the ABSOLUTE target; the RT decides (it owns the ring cursor): accepted when the target lies inside the intact
/// behind span or the decoded ahead span minus one fade and one block. The caller awaits the verdict and rebases only on accept.</summary>
public async ValueTask<bool> TryJumpWithinRingAsync(long targetFrame, CancellationToken ct)
{
    if (_disposed || ActiveRing() is null) return false;
    Volatile.Write(ref _jumpVerdict, 0);
    long seq = await PostMixerCommandAsync(new MixerCmd { Kind = CmdJumpWithinRing, Id = ActiveVoiceIdValue, Position = targetFrame, Frames = Math.Max(1, _format.SampleRate / 200) }, ct).ConfigureAwait(false);
    await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
    if (Volatile.Read(ref _jumpVerdict) != 1) return false;
    RebaseAtSubmit(Volatile.Read(ref _jumpSubmitIndex), targetFrame);
    _sink?.Position(TimeSpan.FromSeconds((double)targetFrame / _format.SampleRate));
    return true;
}
private int _jumpVerdict; private long _jumpSubmitIndex;   // RT writes, control reads

// DrainMixerCmds — case CmdJumpWithinRing (RT): everything the control side guessed is decided HERE, on the thread that owns the cursor
case CmdJumpWithinRing:
{
    ref MixVoice v = ref VoiceRef(c.Id);                                                  // CollectionsMarshal.AsSpan(_voices)[i] — by REF, never a copy (V-PE12)
    var ring = SourceRing(v.Src)!; int fade = c.Frames, block = _feed!.BlockFrames;
    long delta = c.Position - ring.PositionFrames;
    bool ok = delta >= -ring.KeptBehindFrames && delta <= ring.BufferedFrames - fade - block;
    if (ok)
    {
        ring.Read(_jumpTail.AsSpan(0, fade * _format.Channels), _format.Channels);         // the tail = the audio that WOULD have played next: [head, head+fade)
        ok = ring.RtTryJump((int)(delta - fade));                                           // head is now at target
        if (ok) { v.Blend = new JumpBlend(_jumpTail, fade); (v.Src as WsolaAudioSource)?.Reset(c.Position); _jumpSubmitIndex = _submittedFrames + _pendingFrames; }
    }
    Volatile.Write(ref _jumpVerdict, ok ? 1 : 2);
    break;
}
// MixInto (by ref): while v.Blend.Remaining > 0 the first `fade` fresh frames are mixed as fresh × In(p) + tail × Out(p); the blend state lives in the MixVoice slot
```

```csharp
/// <summary>Seek A: a prepared voice at the target (opened by the pump via PrepareAtAsync on a non-owning byte-source view) replaces the active
/// voice under a 5 ms equal-power pair applied in ONE compound RT command (V-PE15): the RT resolves "next block start" itself. The active
/// voice keeps playing until that block. No Stop/Reset. Returns the prepared item's StartPositionFrames (V-PE3/V-PA21).</summary>
public async ValueTask<long> SwapToPreparedAsync(IPreparedItem prepared, long seekGeneration, CancellationToken ct)
{
    if (prepared.MixRate != Format.SampleRate || prepared.AudioVoice is not RingAudioSource ring || prepared is not AudioPreparedItem audio)
        throw new InvalidOperationException("The prepared voice does not match this session's format.");
    await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        await WaitRingReadyAsync(ring, _feed?.BlockFrames ?? 480, ct).ConfigureAwait(false);
        ArmSeekRebufferSuppression();
        long id = Interlocked.Increment(ref _nextEngineVoiceId);                          // ≥ 1_000_000: disjoint from the app's ids (V-PE26)
        var src = new WsolaAudioSource(_feed is not null ? _feed.WrapAdditional(ring, id) : ring, _format.SampleRate, _format.Channels) { Rate = PlaybackRate };
        var cmd = new MixerCmd { Kind = CmdSwapVoice, Id = ActiveVoiceIdValue, Frames = Math.Max(1, _format.SampleRate / 200),
            Voice = new MixVoice { Id = id, Src = src, Env = GainEnvelope.Constant /* resolved on RT */, StartFrame = -1, ReplayGainScalar = 1f, Chain = BuildVoiceChain() } };
        if (!TryEnqueueMixerCmd(cmd, out long seq)) { _feed?.EnqueueRetire(ring); throw new InvalidOperationException("Audio transition was not admitted."); }
        await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
        audio.TransferOwnership();
        _rateSources[id] = src; _voiceStarts[id] = Volatile.Read(ref _swapAtFrame);
        SetActiveVoice(id, ring, prepared.Duration, prepared.TotalFrames);
        _resumeFrames = _format.SampleRate / 10;
        long achieved = audio.StartPositionFrames;
        RebaseAtSubmit(Volatile.Read(ref _swapSubmitIndex), achieved);
        _sink?.Position(TimeSpan.FromSeconds((double)achieved / _format.SampleRate));
        return achieved;
    }
    finally { _replacementGate.Release(); }
}
private long _nextEngineVoiceId = 1_000_000, _swapAtFrame, _swapSubmitIndex;

// DrainMixerCmds — case CmdSwapVoice (RT): ONE step, no window in which only half the swap exists
case CmdSwapVoice:
{
    long at = _mixer.ConsumeSeq + _feed!.BlockFrames;
    var voice = c.Voice; voice.StartFrame = at; voice.Env = GainEnvelope.Fade(FadeKind.In, at, c.Frames, CrossCurve.EqualPower);   // LUT built off-RT? NO — see below
    _mixer.AddVoice(in voice);
    _mixer.TrySetVoiceEnvelope(c.Id, _outFades.Rent(at, c.Frames));                      // Out LUT from a pre-built pool (no RT allocation)
    Volatile.Write(ref _swapAtFrame, at); Volatile.Write(ref _swapSubmitIndex, _submittedFrames + _pendingFrames + _feed.BlockFrames);
    break;
}
```

The envelopes cannot be built on the RT (`GainEnvelope.Fade` allocates a LUT). The session pre-builds **two 240-frame LUTs** at construction (`_fadeIn5`, `_fadeOut5`, equal-power) and `GainEnvelope` gains `WithStart(long frame)` returning a struct-sized copy sharing the LUT (`new GainEnvelope(kind, frame, frames, lut)` — a 48-byte class allocation; to keep the RT alloc-free `GainEnvelope` gets a mutable `FadeStartFrame` setter used ONLY by the RT on a per-command pre-allocated pair: the command carries `InEnv`/`OutEnv` instances created by the control side with `FadeStartFrame = -1`, and the RT stamps `at` into both before installing them). `MixVoice.IsFinished` (`CrossfadeMixer.cs:172-176`) retires the outgoing voice once its Out window has passed; `RenderBlock` hands its ring to the worker.

```csharp
/// <summary>Seek fallback (no decoder lease): the single-decoder "seek marker". The fade-out HOLDS the transport in phase 4 (gain 0, still
/// rendering through F2's silence path — never phase 3, which renders nothing, :1289); the seek is applied after the fade has ENDED
/// (V-PE17); the fade-in follows the first block. The device is never stopped or reset.</summary>
public async ValueTask<long> SeekInPlaceAsync(long targetFrame, CancellationToken ct)
{
    long revision = Interlocked.Increment(ref _seekRevision);
    await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        if (ActiveRing() is not { } ring) throw new InvalidOperationException("No active ring.");
        ArmSeekRebufferSuppression();
        long seq = await PostMixerCommandAsync(new MixerCmd { Kind = CmdFadeOutHold, Frames = Math.Max(1, _format.SampleRate / 200) }, ct).ConfigureAwait(false);
        await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
        await WaitPhaseAsync(4, ct).ConfigureAwait(false);                                  // phase 1 → 4 when ConsumeSeq > EndFrame: the fade is OVER
        long achieved = await ring.SeekFrameAsync(targetFrame).ConfigureAwait(false);      // producer applies; RT flushes on its next wake
        if (revision != Interlocked.Read(ref _seekRevision)) return achieved;
        await WaitRingReadyAsync(ring, _feed?.BlockFrames ?? 480, ct).ConfigureAwait(false);
        long rate = await PostMixerCommandAsync(new MixerCmd { Kind = CmdResetRate, Id = ActiveVoiceIdValue, Position = achieved }, ct).ConfigureAwait(false);
        await WaitAppliedAsync(rate, ct).ConfigureAwait(false);
        FadeIn(TimeSpan.FromMilliseconds(5));                                               // phase 4 → 0
        RebaseAtSubmit(_submittedFrames, achieved);
        _sink?.Position(TimeSpan.FromSeconds((double)achieved / _format.SampleRate));
        return achieved;
    }
    finally { _replacementGate.Release(); }
}

/// <summary>Anchor the derived position at the device frame where the change becomes audible: the block submitted at <paramref name="submitIndex"/>
/// is heard when the device's played count reaches it (device played = submit index − origin). Block-exact (≤ 10 ms), not sample-exact (V-PE32).</summary>
private void RebaseAtSubmit(long submitIndex, long positionFrames)
{
    _activeMixerStart = 0;
    _position.Rebase(submitIndex - Interlocked.Read(ref _deviceFrameOrigin), positionFrames);
    _clockAnchorPosition = positionFrames;
    _sink?.SettleTransport();
}
```

`CmdFadeOutHold`: `_transport.Retarget(0f, ConsumeSeq, c.Frames); _transportPhase = 1; _holdNoStop = true;`. `RenderBlock`'s phase-1 completion (`:1541-1545`) checks `_holdNoStop`: `_transportPhase = 4` (held-running, gain 0; `RtRenderOnce` renders in phase 4 exactly as in 0) instead of 2, and sets `_phaseWake`. `CmdFadeIn` from phase 4 clears `_holdNoStop` and goes to phase 0. Helpers: `ActiveRing()` = the ring tagged `ActiveVoiceIdValue` in `_feed.RingsSnapshot` (else `_voice as RingAudioSource`); `VoiceRef(id)` = `ref CollectionsMarshal.AsSpan(_voices)[i]` exposed by `CrossfadeMixer.VoiceRef(long id)`; `SourceRing` exists (`:439-440`).

### 4.5 Engine — voice chain gain slot, `MixVoice.Held`, scrub API (D4)

`MixVoice` (`CrossfadeMixer.cs:111-125`) gains `public bool Held; public long HoldAtFrame; public JumpBlend Blend;`. `Render` (`:310-314`) iterates `ref var v = ref span[i]` so writes persist (V-PE12). `MixInto` returns without reading while `Held` and `ctx.StartFrame >= HoldAtFrame` (the fade-out that precedes the hold completes first); `IsFinished` (`:172-176`) returns **false** for a held voice (V-PE18: never retired under a hold); `ReadableFrames`/`PcmReady` skip held voices.

```csharp
// PcmAudioSession — control thread. The scrub gain goes through the voice GAIN SLOT (V-PE19); the normalization scalar stays untouched.
public const float ScrubGainLinear = 0.501f;   // −6 dB (D4)

public async ValueTask BeginScrubAsync(IAudioSource grainVoice, long grainVoiceId, CancellationToken ct)
{
    int fade = _format.SampleRate / 50;                                                                        // 20 ms
    // ONE command: Out envelope on the active voice + Held after it (HoldAtFrame = at + fade), + the grain voice added with its In envelope and gain slot at −6 dB.
    var cmd = new MixerCmd { Kind = CmdHoldVoice, Id = ActiveVoiceIdValue, Frames = fade, Linear = ScrubGainLinear,
        Voice = new MixVoice { Id = grainVoiceId, Src = grainVoice, Env = GainEnvelope.Constant, StartFrame = -1, ReplayGainScalar = 1f, Chain = BuildVoiceChain() } };
    long seq = await PostMixerCommandAsync(cmd, ct).ConfigureAwait(false);
    await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
}
// RT: at = ConsumeSeq + block; active.Env = Out@at (pooled LUT); active.Held = true; active.HoldAtFrame = at + fade; grain.StartFrame = at; grain.Env = In@at; FindGain(grain.Chain).SetLinear(c.Linear); AddVoice

/// <summary>Cancel: ONE command releases the hold and fades the main voice back in (V-PE18); the grain voice fades out and self-retires through its
/// Out envelope (no RemoveVoiceAsync). Idempotent: a second call finds no held voice and returns.</summary>
public async ValueTask CancelScrubAsync(long grainVoiceId, CancellationToken ct)
{
    long seq = await PostMixerCommandAsync(new MixerCmd { Kind = CmdReleaseVoice, Id = ActiveVoiceIdValue, Position = grainVoiceId, Frames = _format.SampleRate / 50 }, ct).ConfigureAwait(false);
    await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
    RebaseAtSubmit(_submittedFrames, ActiveRing()?.PositionFrames ?? 0);                                    // the position is where the hold left the content cursor (V-PE19)
}
// RT: at = ConsumeSeq + block; main.Held = false; main.Env = In@at; grain.Env = Out@at (self-retires)

/// <summary>Release WITHOUT a fade-in (V-PA5 non-promote path): the held voice is about to be sought in place; it stays at gain 0 until SeekInPlaceAsync fades it in.</summary>
public ValueTask ReleaseHeldSilentAsync(long grainVoiceId, CancellationToken ct)
    => PostAndWait(new MixerCmd { Kind = CmdReleaseVoice, Id = ActiveVoiceIdValue, Position = grainVoiceId, Frames = 0 }, ct);   // Frames 0 ⇒ Held=false, Env stays Out(complete); CmdFadeOutHold-equivalent state (phase 4)
```

The grain voice's gain slot is the only −6 dB; a commit never promotes the grain voice (V-PA5: the release is a design-A swap from the held voice), so nothing "stays at −6 dB". **`[engine]Audio/ScrubGrain.cs`** — NEW, pure (`namespace FluentGpu.Media; using System;`): the Hann `Window`, `NextStart` (velocity clamp ±4×, snap after 2 hops) and `OverlapAdd(grain, carry, dst, hopFrames, channels)` exactly as in the audit §4.3 — with `OverlapAdd` guarding `dst.Length >= hopFrames * channels` (V-PA29).

### 4.6 Engine — the polyphase windowed-sinc resampler (Q-1; V-PE1, V-PE2, V-PE21, V-PE22, V-PE23, V-PE36)

**`[engine]Audio/PolyphaseResampler.cs`** — NEW. `ResampleResult` moves here from `LinearResampler.cs:9-17` unchanged (V-PE23); `LinearResampler.cs` and `LinearResamplerTests.cs` are deleted; `ResampleStage` (`DspStages.cs:390-404`) wraps the new type; the callers (`AudioDecode.cs:148, 170-196, 223`; `Playback.Audio.cs:1536, 1989, 3907`; `Modules.Host.cs:2340, 2372, 2468`) change the type name only — the contract stays "the caller retains `src[Consumed..]`", and **the resampler keeps the history itself** so a caller that refills only when its hold is empty is never stalled (V-PE1).

```csharp
namespace FluentGpu.Media;

/// <summary>Windowed-sinc polyphase SRC. Taps = 64, Kaiser β = 9, cutoff 0.5·min(fs)/fs_in: 0.0 dB at 20 kHz and −99 dB at 24.1 kHz for 44.1→48 (V-PE22).
/// Exact rational ratios (l ≤ 1024 after reduction: every standard rate pair) step one phase per output with no drift; other ratios interpolate
/// between InterpPhases+1 rows. The (Taps−1)-frame HISTORY lives inside the resampler: Process consumes every input frame it can and keeps the
/// tail, so a caller that refills only on an empty hold is never stalled (V-PE1). Group delay is absorbed: the first D output frames of a
/// stream are discarded and <see cref="Flush"/> emits the trailing D, so trims and joins are not shifted (V-PE36) — LatencySamples is 0.</summary>
public sealed class PolyphaseResampler
{
    public const int Taps = 64, InterpPhases = 256;
    private const int H = Taps - 1;                        // history frames kept between calls
    private readonly int _from, _to, _channels, _phases, _stepNum;
    private readonly bool _exact;
    private readonly float[] _taps;                        // [(_phases (+1 when interpolated)) × Taps], phase-major; row p = kernel for fractional offset p/_phases
    private readonly float[] _hist;                        // H frames × channels: the frames just before the next block
    private long _acc;                                     // exact: next output position in units of 1/_phases of a VIRTUAL frame (virtual frame 0 = oldest history frame)
    private double _pos;                                   // interpolated: same, in virtual frames
    private int _skipOut;                                  // group-delay pre-roll still to discard (output frames)

    public PolyphaseResampler(int fromRate, int toRate, int channels)
    {
        _from = fromRate; _to = toRate; _channels = Math.Max(1, channels);
        int g = Gcd(fromRate, toRate), l = toRate / g, m = fromRate / g;
        _exact = l <= 1024; _phases = _exact ? l : InterpPhases; _stepNum = _exact ? m : 0;
        _taps = BuildTaps(_exact ? _phases : _phases + 1, 0.5 * Math.Min(fromRate, toRate) / fromRate);
        _hist = new float[H * _channels];
        Reset();
    }
    public bool IsActive => _from != _to;
    public int LatencySamples => 0;                                                                       // absorbed (V-PE36)
    public int MaxOutFrames(int inFrames) => IsActive ? (int)Math.Ceiling(inFrames * (double)_to / _from) + 1 : inFrames;
    public int SrcFramesForOutput(int wantOut) => IsActive ? Math.Max(1, (int)Math.Floor((wantOut - 1) * (double)_from / _to)) : wantOut;
    public void Reset()
    {
        Array.Clear(_hist);
        _acc = (long)H * _phases; _pos = H;                                                              // the first output centres on virtual frame H = src[0]
        _skipOut = IsActive ? (int)Math.Round((Taps - 1) / 2.0 * _to / _from) : 0;                       // discard the kernel's leading half (V-PE36)
    }

    /// <summary>Row p holds h[k] = sinc_c(k − D + p/phases) · kaiser(k − D + p/phases), D = (Taps−1)/2 — the CAUSAL kernel for an output at fractional
    /// offset p/phases past input frame i, applied to x[i − k] (V-PE2: argument k − D + frac, not k − D − frac). Unity DC gain per row.</summary>
    private static float[] BuildTaps(int rows, double cutoff)
    {
        const double beta = 9.0; double d = (Taps - 1) / 2.0, i0b = BesselI0(beta);
        var t = new float[rows * Taps];
        for (int p = 0; p < rows; p++)
        {
            double frac = (double)p / (rows == InterpPhases + 1 ? InterpPhases : rows), sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double x = k - d + frac;
                double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                double w = x / (d + 0.5);                                                                 // Kaiser argument ∈ (−1, 1) over the kernel support
                double kaiser = Math.Abs(w) < 1 ? BesselI0(beta * Math.Sqrt(1 - w * w)) / i0b : 0;
                double v = sinc * kaiser; t[p * Taps + k] = (float)v; sum += v;
            }
            for (int k = 0; k < Taps; k++) t[p * Taps + k] = (float)(t[p * Taps + k] / sum);
        }
        return t;
    }
    private static double BesselI0(double x) { double s = 1, term = 1, q = x * x / 4; for (int k = 1; k < 60; k++) { term *= q / ((double)k * k); s += term; if (term < 1e-13 * s) break; } return s; }
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>Virtual input frame v ∈ [0, H + inFrames): v &lt; H is history, else src[v − H].</summary>
    private float At(ReadOnlySpan<float> src, int v, int c) => v < H ? _hist[v * _channels + c] : src[(v - H) * _channels + c];

    /// <summary>Resample <paramref name="inFrames"/> frames of <paramref name="src"/> into <paramref name="dst"/>. Consumes everything it can:
    /// when the input runs out every frame is consumed and the last H frames of (hist ++ src) become the history; when dst fills, the frames no
    /// future output can touch are consumed and the history is rebuilt the same way. Callers retain src[Consumed..] as the next prefix.</summary>
    public ResampleResult Process(ReadOnlySpan<float> src, int inFrames, Span<float> dst)
    {
        int ch = _channels;
        if (!IsActive) { int n = Math.Min(inFrames * ch, Math.Min(src.Length, dst.Length)); src[..n].CopyTo(dst); return new ResampleResult(n / ch, n / ch); }
        if (inFrames * ch > src.Length) inFrames = src.Length / ch;
        int maxOut = dst.Length / ch, outFrames = 0, last = H + inFrames - 1;                               // the newest virtual frame available
        while (outFrames < maxOut)
        {
            int center; float frac;
            if (_exact) { center = (int)(_acc / _phases); frac = 0; } else { center = (int)Math.Floor(_pos); frac = (float)(_pos - center); }
            if (center > last) break;                                                                      // need the next block
            int row = _exact ? (int)(_acc % _phases) : (int)(frac * InterpPhases);
            float lerp = _exact ? 0f : frac * InterpPhases - row;
            ReadOnlySpan<float> r0 = _taps.AsSpan(row * Taps, Taps), r1 = _exact ? r0 : _taps.AsSpan((row + 1) * Taps, Taps);   // row + 1 ≤ InterpPhases: no wrap (V-PE22)
            for (int c = 0; c < ch; c++)
            {
                float a0 = 0f, a1 = 0f;
                for (int k = 0; k < Taps; k++) { float s = At(src, center - k, c); a0 += r0[k] * s; if (!_exact) a1 += r1[k] * s; }   // center − k ≥ center − H ≥ 0 by construction
                float y = _exact ? a0 : a0 + (a1 - a0) * lerp;
                if (_skipOut == 0) dst[outFrames * ch + c] = y;
            }
            if (_skipOut > 0) _skipOut--; else outFrames++;
            if (_exact) _acc += _stepNum; else _pos += (double)_from / _to;
        }
        // Consume: every src frame older than (next center − H) is dead; when the input ran out that is all of them.
        int nextCenter = _exact ? (int)(_acc / _phases) : (int)Math.Floor(_pos);
        int consumed = nextCenter > last ? inFrames : Math.Clamp(nextCenter - H - H, 0, inFrames);          // src index j is virtual H + j; dead when H + j < nextCenter − H
        // Rebuild the history = virtual frames [consumed, consumed + H)
        for (int v = 0; v < H; v++) for (int c = 0; c < ch; c++) _hist[v * ch + c] = At(src, consumed + v, c);   // At reads the OLD hist for v < H − consumed… copy forward first:
        // (implementation note: when consumed < H the source and destination overlap inside _hist; copy via a stack scratch of H×ch floats — 63 × 2 = 126 floats)
        if (_exact) _acc -= (long)consumed * _phases; else _pos -= consumed;
        return new ResampleResult(outFrames, consumed);
    }

    /// <summary>EOF: push D zero frames through so the trailing half kernel is emitted (V-PE21/V-PE36). Returns the frames produced.</summary>
    public int Flush(Span<float> dst)
    {
        Span<float> zeros = stackalloc float[((Taps + 1) / 2) * _channels];
        int produced = 0, consumedTotal = 0; ReadOnlySpan<float> z = zeros;
        while (consumedTotal < (Taps + 1) / 2 && produced < dst.Length / _channels)
        {
            var r = Process(z[(consumedTotal * _channels)..], (Taps + 1) / 2 - consumedTotal, dst[(produced * _channels)..]);
            produced += r.Produced; consumedTotal += r.Consumed;
            if (r.Produced == 0 && r.Consumed == 0) break;
        }
        return produced;
    }
}
```

Decoders call `Flush` once when the inner source reports EOF and emit the frames before their own `_eof` (the three app adapters and `WavAudioDecoder.Read:182-194`). General-ratio test ratio: **44056 → 48000** (gcd 8, l = 6000 > 1024 — truly interpolated; 44.1→96 is 147:320 and exact, V-PE22).

### 4.7 Engine — limiter lookahead and true-peak (E-3; V-PE20), preamp (E-4; V-PE25), denormals (E-2), double biquad (E-1; V-PE31)

`LimiterStage` (`DspStages.cs:320-382`) with a deque-min window of L+1 frames including the frame about to leave, a channel-sized delay line, and the 4-point inter-sample estimate; `LimiterSpec` (`AudioGraph.cs:197`) gains `LookaheadMs = 2f`; `BuildStage`/`Compile` pass `channels`:

```csharp
public sealed class LimiterStage : IDspStage
{
    private readonly int _channels, _lookahead;                   // frames
    private readonly float[] _delay;                              // (_lookahead + 1) × channels, circular — the extra slot is the frame being stored this call
    private readonly float[] _peak;                               // per-frame linked true-peak estimate, same indexing
    private readonly int[] _dq; private int _dqHead, _dqTail;     // monotonic deque of frame indices with decreasing peak (window max in O(1) amortised)
    private int _write; private long _frameNo;
    private float _ceiling, _gain = 1f, _releaseCoeff, _attackStep;
    private float _pm2, _pm1;                                     // the last two INPUT linked peaks for the cubic estimate

    public LimiterStage(float ceilingDbTp = -1.5f, float releaseMs = 50f, int mixRate = 48000, float lookaheadMs = 2f, int channels = 2)
    {
        _channels = Math.Max(1, channels); _ceiling = DbToLinear(ceilingDbTp);
        _lookahead = Math.Max(1, (int)(lookaheadMs * 0.001f * mixRate));
        _delay = new float[(_lookahead + 1) * _channels]; _peak = new float[_lookahead + 1]; _dq = new int[_lookahead + 2];
        _releaseCoeff = MathF.Exp(-1f / MathF.Max(1f, releaseMs * 0.001f * mixRate));
        _attackStep = 1f / _lookahead;
    }
    public int LatencySamples => _lookahead;
    public bool Bypassed { get; set; }

    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int ch = _channels;
        if (Bypassed) { if (!src.Overlaps(dst)) src[..(frames * ch)].CopyTo(dst); return frames; }
        for (int f = 0; f < frames; f++)
        {
            int b = f * ch;
            // 1. linked sample peak of the incoming frame, and the inter-sample peak between the previous two and this one via the 4-point
            //    half-sample interpolator (−x[n−2] + 9x[n−1] + 9x[n] − x[n+1]) / 16 — no false peak on a low-frequency sine (V-PE20)
            float p0 = 0f; for (int c = 0; c < ch; c++) p0 = MathF.Max(p0, MathF.Abs(src[b + c]));
            float inter = MathF.Abs((-_pm2 + 9f * _pm1 + 9f * p0 - (f + 1 < frames ? LinkedPeak(src, (f + 1) * ch, ch) : p0)) / 16f);
            float peak = MathF.Max(p0, inter); _pm2 = _pm1; _pm1 = p0;
            // 2. store, then compute the gain the WHOLE window (L+1 entries, the leaving frame included) needs
            int slot = _write; _peak[slot] = peak; PushMax(slot, peak);
            float need = _peak[_dq[_dqHead]] > _ceiling ? _ceiling / _peak[_dq[_dqHead]] : 1f;
            if (need < _gain) _gain = MathF.Max(need, _gain - _attackStep * (1f - need));                     // linear attack across the lookahead
            else { _gain = need + (_gain - need) * _releaseCoeff; if (_gain > 1f - 1e-6f) _gain = 1f; }        // release, snapped (E-2)
            // 3. output the frame that entered _lookahead frames ago, store this one
            int leave = (slot + 1) % (_lookahead + 1);
            for (int c = 0; c < ch; c++) { dst[b + c] = _delay[leave * ch + c] * _gain; _delay[slot * ch + c] = src[b + c]; }
            PopExpired(leave); _write = leave; _frameNo++;
        }
        return frames;
    }
    private static float LinkedPeak(ReadOnlySpan<float> s, int b, int ch) { float p = 0f; for (int c = 0; c < ch; c++) p = MathF.Max(p, MathF.Abs(s[b + c])); return p; }
    private void PushMax(int slot, float peak) { while (_dqTail != _dqHead && _peak[_dq[(_dqTail - 1 + _dq.Length) % _dq.Length]] <= peak) _dqTail = (_dqTail - 1 + _dq.Length) % _dq.Length; _dq[_dqTail] = slot; _dqTail = (_dqTail + 1) % _dq.Length; }
    private void PopExpired(int leavingSlot) { if (_dqTail != _dqHead && _dq[_dqHead] == leavingSlot) _dqHead = (_dqHead + 1) % _dq.Length; }
    public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}
```

The guarantee: the frame leaving the delay line has been in the window for all L+1 of the last gain computations, each bounded by `need ≤ ceiling / peak(frame)`, so `|out| ≤ ceiling`. **Preamp (E-4, V-PE25):** one gain slot per voice (§4.3): `ReconcileEffects` sends `Linear = LimiterStage.DbToLinear(-Math.Max(0f, maxBandGainDb))` with every `CmdSetEq`; the RT multiplies it by the voice's normalization delta. **E-1/E-2 (`Biquad.cs`):** `BiquadCoeffs` becomes `readonly record struct BiquadCoeffs(double B0, double B1, double B2, double A1, double A2)` (full-precision coefficients; `AudioGraphTests` compare with a `1e-9` tolerance), `BiquadState` holds `double _x1, _x2, _y1, _y2`, and `Process` computes in double, flushes `if (Math.Abs(y) < 1e-25) y = 0;` **before** storing `_y1 = y`, and returns `(float)y`.

### 4.8 App — the Vorbis setup-parse cache (V-PA35)

`Vorbis.Decoder` (`Playback.Audio.Vorbis.cs:1467`) gains `public ulong SetupHash { get; private set; }` (64-bit FNV-1a over the identification + setup bytes, computed inside `Open`) and `Open(ident, setup, gain)` returns early — resetting only the overlap state and storing the gain — when `SetupHash` matches and the tables are built. `VorbisWorkingSet` (`Playback.Audio.cs:1373-1428`) is split: the **window** pool stays as is (the adapter needs `_win` to read the headers, `:1488-1498`), and a separate **decoder pool** (`VorbisDecoderPool.Rent(ulong setupHash)`, capacity 4, prefers a decoder whose `SetupHash` matches) is consulted **after** the three header packets are read (`:1510-1517`): the adapter hashes `ident ++ setup`, rents by hash, then calls `dec.Open` (a hit skips the parse). `Dispose` returns both. The open logs `setupMs=` and `setupHit=0|1` in `audio.open`.

### 4.9 App — reducer (`[app]Playback/Playback.cs`)

**State** (`:943-1000`): `public uint SeekGen; public long RemoteSeekAtMs, RemoteSeekTargetMs; public bool SinkMuted; public ScrubModel Scrub; public int ScrubPosMs; public long VolumeDirtySinceMs;`. **Input** (`:1250-1258`): `uint gen = 0` → `public readonly uint Gen;`. **Effects** (`:1540-1550` region): `public uint SeekGen; public bool ScrubBegin, ScrubMove, ScrubEnd, ScrubCancel, PersistVolume; public int ScrubMs; public double ScrubVelocity;` and **`Any` includes them** (V-PA1: `… || ScrubBegin || ScrubMove || ScrubEnd || ScrubCancel || PersistVolume || PrepareLost || …`). **InputKind**: `ScrubBegin, ScrubMove, ScrubEnd, ScrubCancel, PrepareLost` (no `ScrubTick`/`ScrubPark` — the grain source parks itself, V-PA23).

```csharp
// :1286-1310 named constructors
public static Input Audio(AudioSignal signal, uint epoch, long nowMs = 0, long arg = 0, uint gen = 0) => new(InputKind.AudioSignal, intArg: (int)signal, longArg: arg, epoch: epoch, nowMs: nowMs, gen: gen);
public static Input ScrubBegin(int ms, long nowMs) => new(InputKind.ScrubBegin, intArg: ms, nowMs: nowMs);
public static Input ScrubMove(int ms, long nowMs) => new(InputKind.ScrubMove, intArg: ms, nowMs: nowMs);
public static Input ScrubEnd(int ms, long nowMs) => new(InputKind.ScrubEnd, intArg: ms, nowMs: nowMs);
public static Input ScrubCancel() => new(InputKind.ScrubCancel);
public static Input PrepareLost(uint epoch) => new(InputKind.PrepareLost, epoch: epoch);                 // V-PA33

/// <summary>THE one seek emitter (V-PA2): DoSeek and Restart both go through it, so every local seek bumps the generation the pump stamps on its reports.</summary>
static void EmitSeek(ref State s, ref Effects fx, int ms, long nowMs)
{
    s.PosMs = ms; s.PosQpc = nowMs;
    s.SeekGen++;
    Bump(ref s);
    fx.Seek = true; fx.SeekMs = ms; fx.SeekEpoch = s.LoadEpoch; fx.SeekGen = s.SeekGen;                       // U-1: posted under the LOAD epoch
    Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
    fx.SmtcTimeline = true;
}

// DoSeek :1863-1884 — the body from `if (!s.RoutesLocal)` on
if (!s.RoutesLocal)
{
    int remote = SeekTarget.Clamp(i.IntArg, s.DurationMs);
    s.PosMs = remote; s.PosQpc = i.NowMs; s.RemoteSeekAtMs = i.NowMs; s.RemoteSeekTargetMs = remote;        // U-3: optimistic, frame clock (V-PA10)
    Forward(ref s, RemoteCmd.SeekTo, ref fx, remote, false);
    return;
}
int ms = SeekTarget.Clamp(i.IntArg, s.DurationMs);
if (s.Parked || s.Error != Fault.None) { … unchanged … }
ReportSeeked(ref s, ref fx, s.Position(i.NowMs), ms);
EmitSeek(ref s, ref fx, ms, i.NowMs);

// Restart :1742-1756 — replace `s.PosMs = 0; … fx.SeekEpoch = s.Epoch;` with
EmitSeek(ref s, ref fx, 0, i.NowMs);
if (registered) ReportStart(ref s, ref fx, why, 0);

// DoAudio :1948-1994 — generation guards
case AudioSignal.Position: if (i.Gen != s.SeekGen) break; … unchanged …
case AudioSignal.Seeked:   if (i.Gen != s.SeekGen) break; s.PosMs = (int)i.LongArg; s.PosQpc = i.NowMs; fx.SmtcTimeline = true; break;
case AudioSignal.Buffering: if (s.Buffering) break; s.PosMs = s.Position(i.NowMs); s.PosQpc = i.NowMs; s.Buffering = true; break;   // U-5

// State.Position :1105-1112
if (Phase != Phase.Playing || Buffering) return PosMs;

// MirrorRemote (:2233-2239 region) — V-PA10: hold the optimistic remote position while it is fresh and the cluster still disagrees
int proj = clusterProjectedPositionMs;
bool holdRemote = s.RemoteSeekAtMs > 0 && i.NowMs - s.RemoteSeekAtMs < 2_000 && Math.Abs(proj - s.RemoteSeekTargetMs) > 1_500;
if (!holdRemote) { s.PosMs = proj; s.PosQpc = i.NowMs; s.RemoteSeekAtMs = 0; }
// DoCluster's ClusterAck for our own SeekTo command (CommandAttribution) also clears RemoteSeekAtMs.

// DoMute :2498-2503 (U-4, V-PA32)
if (!foreign && (i.IntArg & Input.SinkBit) != 0)
{
    fx.Mute = true; fx.MuteOn = muted; s.SinkMuted = muted;
    if (!muted && s.Volume <= MuteFloor)
    {
        float back = s.MuteRestoreVolume > MuteFloor ? s.MuteRestoreVolume : UnmuteDefault;
        var restore = new Input(InputKind.SetVolume, intArg: Input.WireVolume(back), nowMs: i.NowMs);
        DoVolume(ref s, in restore, ref fx);
    }
    return;
}
// DoVolume (local, sink present): if (s.SinkMuted && wire > Input.WireVolume(MuteFloor)) { fx.Mute = true; fx.MuteOn = false; s.SinkMuted = false; }
// :2490 — public const float UnmuteDefault = 0.794f;   (D6)
// DoVolume (local): s.VolumeDirtySinceMs = s.VolumeDirtySinceMs == 0 ? i.NowMs : s.VolumeDirtySinceMs;   (V-1 / V-PA11)
// DoTick :2653: if (s.VolumeDirtySinceMs != 0 && i.NowMs - s.VolumeDirtySinceMs >= 500) { fx.PersistVolume = true; fx.VolumeValue = s.Volume; s.VolumeDirtySinceMs = 0; }

// PrepareLost (V-PA33): case InputKind.PrepareLost: if (i.Epoch != s.LoadEpoch) break; s.NextArmed = false; ArmNext(ref s, ref fx); break;

// scrub arms (wave 3 — WP 3d; the model is a CORE struct from §4.10). Gated on local AUDIO (s.Kind is the PlayableKind field, :930).
case InputKind.ScrubBegin: { var e = s.Scrub.Down(i.IntArg, i.NowMs); s.ScrubPosMs = i.IntArg; fx.ScrubBegin = e.Begin && s.RoutesLocal && s.Kind != PlayableKind.Video && s.Phase == Phase.Playing; fx.ScrubMs = i.IntArg; break; }
case InputKind.ScrubMove:  { var e = s.Scrub.Move(i.IntArg, i.NowMs); s.ScrubPosMs = i.IntArg; if (e.Move && s.RoutesLocal) { fx.ScrubMove = true; fx.ScrubMs = i.IntArg; fx.ScrubVelocity = e.Velocity; } break; }
case InputKind.ScrubEnd:
{
    var e = s.Scrub.Up(i.IntArg, i.NowMs);
    if (!e.Commit) break;
    int target = SeekTarget.Clamp(i.IntArg, s.DurationMs);
    if (s.RoutesLocal && s.Kind != PlayableKind.Video && fxScrubWasAudible)   // the pump holds the main voice: the release IS the seek (V-PA5)
    {
        ReportSeeked(ref s, ref fx, s.Position(i.NowMs), target);
        s.PosMs = target; s.PosQpc = i.NowMs; s.SeekGen++; Bump(ref s);
        fx.ScrubEnd = true; fx.SeekMs = target; fx.SeekEpoch = s.LoadEpoch; fx.SeekGen = s.SeekGen;      // ScrubEnd INSTEAD of Seek
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged); fx.SmtcTimeline = true;
    }
    else { var seek = Input.Seek(target, i.NowMs); DoSeek(ref s, in seek, ref fx); }                        // remote / video / paused: one ordinary seek
    break;
}
case InputKind.ScrubCancel: { var e = s.Scrub.Cancel(); if (e.Cancel && s.RoutesLocal) fx.ScrubCancel = true; break; }
```

`fxScrubWasAudible` is `s.Scrub.Audible`, set by the `ScrubBegin` arm to the value of `fx.ScrubBegin`. **`Playback.Host.cs`**: `Execute` (`:641-707`) dispatches `if (s_fx.ScrubBegin) Audio.ScrubBegin(s_fx.ScrubMs); if (s_fx.ScrubMove) Audio.ScrubMove(s_fx.ScrubMs, s_fx.ScrubVelocity); if (s_fx.ScrubEnd) Audio.ScrubEnd(s_fx.SeekMs, s_fx.SeekEpoch, s_fx.SeekGen); if (s_fx.ScrubCancel) Audio.ScrubCancel();` **before** the `Seek` line (`:660`, which becomes `Audio.Seek(s_fx.SeekMs, s_fx.SeekEpoch, s_fx.SeekGen)` / `Video.Seek(s_fx.SeekMs, accurate: true, s_fx.SeekGen)`); `if (s_fx.PersistVolume && Platform.Settings.Get(Platform.Keys.RememberVolume)) Platform.Settings.Set(Platform.Keys.SavedVolume, s_fx.VolumeValue);` replaces the per-drain write at `:665-666` (UI thread, debounced by the reducer — V-PA11), and the host's shutdown arm (the one that clears `s_sinkMuted`, `:1317`) flushes a dirty volume once. `Publish()` (`:1009-1042`) adds `ScrubPositionMs.Value = s_state.Scrub.Current != ScrubModel.State.Idle ? s_state.ScrubPosMs : -1;` and `Muted.Value = … s_state.SinkMuted || …` (the host static `s_sinkMuted` goes). New posts: `ScrubBegin(int ms)`, `ScrubMove(int ms)`, `ScrubEnd(int ms)`, `ScrubCancel()` (`Post(Input.ScrubX(…, FrameNowMs()))`). Every load effect carries `s_fx.SeekGen` so the pump seeds `s_pendingSeekGen` (V-PA2): `Audio.Load(…, seekGen: s_fx.SeekGen)` / `Video.Load(…, seekGen)`.

### 4.10 App — `[app]Playback/Playback.Scrub.cs` (NEW, CORE)

```csharp
namespace Wavee;

/// <summary>The scrub gesture as a value (D4): Idle → Pressed → Scrubbing; Up and Cancel return to Idle at once (V-PA6: the optimistic PosMs and
/// the seek generation cover the commit — no Committing state, no landing hand-shake). Moves are coalesced to ≤ 20 per second for the audio side;
/// the velocity is audio-ms per wall-ms (dimensionless), passed through unchanged (V-PA9). Parking is the grain source's own business.</summary>
public struct ScrubModel
{
    public enum State : byte { Idle, Pressed, Scrubbing }
    public const int CoalesceMs = 50, PromoteWithinMs = 250;
    public State Current; public bool Audible;
    public long PositionMs, LastMoveAtMs, LastSentAtMs; public double Velocity;
    public readonly record struct Effects(bool Begin, bool Move, bool Commit, bool Cancel, long PositionMs, double Velocity);

    public Effects Down(long ms, long now) { Current = State.Pressed; PositionMs = ms; LastMoveAtMs = LastSentAtMs = now; Velocity = 0; return new(Begin: true, false, false, false, ms, 0); }
    public Effects Move(long ms, long now)
    {
        if (Current == State.Idle) return default;
        double dt = Math.Max(1, now - LastMoveAtMs);
        Velocity = 0.5 * Velocity + 0.5 * ((ms - PositionMs) / dt);
        PositionMs = ms; LastMoveAtMs = now; Current = State.Scrubbing;
        if (now - LastSentAtMs < CoalesceMs) return default;
        LastSentAtMs = now;
        return new(false, Move: true, false, false, ms, Velocity);
    }
    public Effects Up(long ms, long now) { if (Current == State.Idle) return default; Current = State.Idle; PositionMs = ms; return new(false, false, Commit: true, false, ms, 0); }
    public Effects Cancel() { bool live = Current != State.Idle; Current = State.Idle; Audible = false; return live ? new(false, false, false, Cancel: true, PositionMs, 0) : default; }
}

/// <summary>Keyboard scrubbing (D4) without a key-up (V-PA22): every press or OS repeat steps the VISUAL target up the ladder — 5 s for the first
/// 0.5 s held, then 15 s, then 30 s; Shift = 1 s fine; the target commits from <see cref="Tick"/> once no repeat has arrived for GraceMs.
/// PURE; lives in the bar component (replaces PlayerSeekAccumulator, Shell.PlayerBar.cs:425-446).</summary>
public struct KeyboardScrubLadder
{
    public const int FineMs = 1_000, StepMs = 5_000, MidMs = 15_000, FastMs = 30_000, MidAfterMs = 500, FastAfterMs = 1_500, GraceMs = 250;
    public long HeldSinceMs, LastKeyMs; public int TargetMs; public bool Active;

    public int Step(int direction, bool fine, int fromMs, int durationMs, long nowMs)
    {
        if (!Active || nowMs - LastKeyMs > GraceMs) { Active = true; HeldSinceMs = nowMs; TargetMs = fromMs; }
        LastKeyMs = nowMs;
        long held = nowMs - HeldSinceMs;
        int step = fine ? FineMs : held >= FastAfterMs ? FastMs : held >= MidAfterMs ? MidMs : StepMs;
        TargetMs = (int)Math.Clamp(TargetMs + (long)direction * step, 0, Math.Max(0, durationMs));
        return TargetMs;
    }
    /// <summary>The bar's 50 ms tick: the committed target once the key has been idle for GraceMs, else −1.</summary>
    public int Tick(long nowMs) { if (!Active || nowMs - LastKeyMs < GraceMs) return -1; Active = false; return TargetMs; }
}
```

### 4.11 App — the pump (`[app]Playback/Playback.Audio.cs`): mailbox, design A + fallback, ownership hand-over, S-1, S-5, tick latch

```csharp
// §4 statics (near :346-350)
static long s_seekMailbox = -1;                      // packed (epoch << 32 | ms), Interlocked (V-PA28); -1 = empty
static uint s_seekMailboxGen, s_pendingSeekGen;
static CancellationTokenSource? s_seekPrepare;      // Volatile.Read/Write everywhere (V-PA28)
static int s_tickBusy;                               // H-11
static readonly Dictionary<long, float> s_bakedFactor = new();   // voice id → the factor its decoder folded (V-PA15), under s_gate

public static void Seek(int ms, uint epoch = 0, uint gen = 0)
{
    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    Spotify.Audio.Stream.Stats before = Spotify.Audio.Stream.Stats.Read();
    lock (s_gate) { if (epoch != 0 && epoch != s_loadEpoch) return; s_pendingSeekMs = ms; s_pendingSeekGen = gen; }     // stale load: drop (V-PA28)
    long packed = ((long)(epoch == 0 ? s_loadEpoch : epoch) << 32) | (uint)ms;
    bool queued = Interlocked.Exchange(ref s_seekMailbox, packed) >= 0;
    Volatile.Write(ref s_seekMailboxGen, gen);
    Volatile.Read(ref s_seekPrepare)?.Cancel();                                                                      // a newer seek supersedes an in-flight prepare
    if (!queued) Enqueue(() => SeekCoreAsync(t0, before));                                                           // ONE chain op reads the newest target (S-4)
}
// LoadCoreAsync :784-799 adds: Interlocked.Exchange(ref s_seekMailbox, -1); s_pendingSeekGen = seekGen;  (V-PA2/V-PA28; `Load` gains `uint seekGen`)

static async Task SeekCoreAsync(long t0, Spotify.Audio.Stream.Stats before)
{
    long packed = Interlocked.Exchange(ref s_seekMailbox, -1);
    if (packed < 0) return;
    uint epoch = (uint)(packed >> 32); int ms = (int)(uint)packed; uint gen = Volatile.Read(ref s_seekMailboxGen);
    PcmAudioSession? sess; bool silent; PcmAudioPlayer? backend; IMediaByteSource? bytes; Opened opened; IAudioDecoder? activeDecoder;
    lock (s_gate) { if (epoch != s_loadEpoch) return; sess = s_session; silent = s_silent; backend = s_backend; bytes = s_bytes; opened = s_opened; activeDecoder = s_activeDecoder; }
    if (silent && sess is not null) { if (await SilentSeekAsync(sess, ms, epoch, gen).ConfigureAwait(false) && t0 != 0) RecordSeek(ms, t0, before); return; }
    if (sess is null || backend is null) return;                                                                  // parked; the Ready arm applies it (:2827-2834)
    bool abandoned = AbandonPendingJoin(sess, "seek");
    long target = GaplessJoinClock.MsToFrames(ms, sess.Format.SampleRate);
    long achieved;
    if (await sess.TryJumpWithinRingAsync(target, CancellationToken.None).ConfigureAwait(false)) achieved = target;             // B
    else if (bytes is RingSource owner && backend.TryAcquireDecoderLease(out IDisposable lease))                                // A
    {
        var cts = new CancellationTokenSource(); Volatile.Write(ref s_seekPrepare, cts);
        IPreparedItem? item = null; RingSource? view = null;
        try
        {
            view = new RingSource(owner.Body, ownsBody: false);                                                     // a second reader: Body.ProbeAt/ProbeRange, no cursor/epoch moves (V-PA20)
            IAudioDecoder decoder = CreateDecoderFor(opened);
            s_decoderForOpen.Value = decoder;
            MediaSource source = MediaSource.FromPull(view).WithKind(MediaKind.PcmAudio);
            PrepareContext ctx = PrepareContext.For(sess.Format, sess.NormalizationMode, sess.ReferenceLufsValue);
            var staleTimer = StaleTimerAsync(sess, cts.Token);                                                      // V-PE14: fades the OLD voice if no block within 80 ms
            item = await backend.PrepareAtAsync(source, ctx, target, lease, cts.Token).ConfigureAwait(false);      // the lease travels with the item (V-PE13/V-PA3)
            s_decoderForOpen.Value = null;
            lock (s_gate)
            {
                // Hand the Body over BEFORE the swap (V-PA4): the retiring voice's Dispose must not close what the new voice reads.
                owner.ReleaseOwnership(); view.TakeOwnership();
                s_bytes = view; s_activeDecoder = decoder; s_bakedFactor[NextVoiceIdHint] = ((IGainFolding)decoder).AppliedGainLinear;
            }
            achieved = await sess.SwapToPreparedAsync(item, gen, cts.Token).ConfigureAwait(false);
            lock (s_gate) s_activePrimaryId = sess.ActiveVoiceIdValue;
            await staleTimer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { if (item is not null) await item.DisposeAsync().ConfigureAwait(false); else lease.Dispose(); return; }
        catch { if (item is not null) await item.DisposeAsync().ConfigureAwait(false); else lease.Dispose(); throw; }
        finally { if (ReferenceEquals(Volatile.Read(ref s_seekPrepare), cts)) Volatile.Write(ref s_seekPrepare, null); s_decoderForOpen.Value = null; }
    }
    else
    {
        (bytes as RingSource)?.InterruptPendingRead();                                                              // V-PA19: only the fallback interrupts the live producer
        achieved = await sess.SeekInPlaceAsync(target, CancellationToken.None).ConfigureAwait(false);
    }
    AfterSeekLanded(ms, (int)(achieved * 1000L / sess.Format.SampleRate), epoch, gen, t0, before, abandoned);
}

static async Task StaleTimerAsync(PcmAudioSession sess, CancellationToken ct)
{
    try { await Task.Delay(80, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
    if (!sess.SwapLanded) await sess.FadeActiveToSilenceAsync(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);   // a 10 ms Out on the active voice (SetVoiceEnvelope result checked)
}

static void AfterSeekLanded(int requestedMs, int achievedMs, uint epoch, uint gen, long t0, Spotify.Audio.Stream.Stats before, bool joinAbandoned)
{
    lock (s_gate)
    {
        if (s_pendingSeekGen == gen) { s_pendingSeekMs = -1; s_pendingSeekQueued = false; }
        s_activeStartMs = 0;
        if (s_session is { } sess) { s_activeJoinFrame = GaplessJoinClock.JoinFrameFor(sess.SampleClock, s_activeDurMs, achievedMs, sess.Format.SampleRate); s_anchorClock = sess.SampleClock; s_anchorPlayheadMs = Math.Max(0, achievedMs); }
        if (s_activeDurMs > 0 && achievedMs < s_activeDurMs - EndingSoonMs(EffectiveFadeMs, s_activeDurMs)) { s_lastEndgameAskMs = -1; s_endgameAskCount = 0; }
    }
    if (t0 != 0) RecordSeek(requestedMs, t0, before);
    if (joinAbandoned) Post(Input.PrepareLost(epoch));                                                               // S-5 (V-PA33): only when a join was really dropped
    PostSignal(AudioSignal.Seeked, epoch, achievedMs, gen);                                                            // U-11: the ACHIEVED position
}
```

`RingSource` (`:1868-1875`) gains `ownsBody` (ctor flag), `ReleaseOwnership()`/`TakeOwnership()` (`Interlocked.Exchange` on an int), and routes `ReadAt`/`Retarget`/`ResumeFrom` to `Body.ProbeAt`/`Body.ProbeRange`/no-op while not owning (§4.16); `Cancel()`/`Close()` dispose the Body only when owning. `PostSignal(signal, epoch, arg, gen)` threads `gen` into `Input.Audio(…, gen)`; the tick's `Position` post (`:2810-2814`) passes `s_pendingSeekGen`; `SilentSeekAsync` (`:2663`) and the video host's posts (`Playback.Video.cs:1316-1319`, via `Video.Seek(ms, accurate, gen)` storing `s_videoSeekGen`) carry it too (V-PA2). `Tick` (`:2707`) wraps its body in `if (Interlocked.CompareExchange(ref s_tickBusy, 1, 0) != 0) return; try { … } finally { Volatile.Write(ref s_tickBusy, 0); }`. `AbandonPendingJoin` returns whether a join was actually dropped. **S-1 (V-PA16)** — the Vorbis adapter's `Seek` (`:1814-1836`): when the landing window fails and `_interrupted`, latch `_needsLanding = true` with the plan and target instead of `_eof`; `Read` (`:1739-1760`) serves `Silence` while `_needsLanding`, retries `ReadWindowAt(plan.Offset, plan.WindowBytes, landing: true)` on each call and, once it succeeds, runs `PeekLanding(); _dec.Prime(); _clock = VorbisClock.At(start); _clock.Target = target; _needsLanding = false;`. **S-7**: `Ogg.Reader.Next` (`Playback.Audio.Ogg.cs:329`) gains `Hole`, returned at `:371` after dropping the open packet; `NextFrames` (`:1682-1696`) calls `dec.Prime()` on `Hole` or a non-`Ok` decode, and `VorbisClock.Pad` emits the missing frames as silence once the next granule pins the gap. **V-PA15** — every adapter implements `IGainFolding { float AppliedGainLinear { get; } }`; `s_bakedFactor` is written at open, at the join (`:2339`), at the crossfade (`:2453`) and at a swap; `SetNormalization` ramps each live voice by `now / baked[voice]`.

### 4.12 App — `[app]Playback/Playback.Audio.Scrub.cs` (NEW, SHELL)

```csharp
public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>The scrub voice: a second decoder over a NON-OWNING byte-source view feeding a 2 s PCM cache around the scrub point, filled from RESIDENT
        /// bytes only (V-PA20/V-PA30 — a non-resident target parks the grains); Read cuts Hann grains on the ring's producer thread; it parks itself after
        /// 150 ms without a Retarget (V-PA23). Never promoted (V-PA5); retired by the ring when its Out envelope completes (V-PA25).</summary>
        public sealed class ScrubGrainSource : IAudioSource, IDisposable
        {
            readonly IAudioDecoder _dec; readonly RingSource _view; readonly IDisposable _lease; readonly int _rate, _ch, _grain, _hop;
            readonly float[] _cache, _grainBuf, _carry;
            long _cacheStart = long.MinValue, _target, _lastGrain, _readFrames, _lastRetargetTicks;
            double _velocity; volatile bool _parked;
            public const int CacheMs = 2_000, ParkAfterMs = 150;
            public int HopFrames => _hop;

            public ScrubGrainSource(IAudioDecoder dec, RingSource view, IDisposable lease, int rate, int channels, long startFrame)
            {
                _dec = dec; _view = view; _lease = lease; _rate = rate; _ch = channels;
                _grain = rate * ScrubGrain.GrainMs / 1000; _hop = rate * ScrubGrain.HopMs / 1000;
                _cache = new float[rate * CacheMs / 1000 * channels]; _grainBuf = new float[_grain * channels]; _carry = new float[_hop * channels];
                _target = startFrame; _lastGrain = startFrame;                                                   // V-PA29: never MinValue
                _lastRetargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            public void Retarget(long frame, double velocity) { Volatile.Write(ref _target, frame); Volatile.Write(ref _velocity, velocity); Volatile.Write(ref _lastRetargetTicks, System.Diagnostics.Stopwatch.GetTimestamp()); _parked = false; }

            public int Read(Span<float> dst, int channels)
            {
                int want = dst.Length / _ch; if (want < _hop) return 0;                                             // the ring asks for ≥ one hop (pump = hop)
                if (System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastRetargetTicks)).TotalMilliseconds > ParkAfterMs && !_parked) { _parked = true; Array.Clear(_carry); }
                if (_parked) { dst[..(_hop * _ch)].Clear(); _readFrames += _hop; return _hop; }
                long target = Volatile.Read(ref _target); int cacheFrames = _cache.Length / _ch;
                if (target - _grain < _cacheStart || target + 2 * _grain > _cacheStart + cacheFrames)
                    if (!TryRecenterResident(target, cacheFrames)) { _parked = true; Array.Clear(_carry); dst[..(_hop * _ch)].Clear(); _readFrames += _hop; return _hop; }
                long start = Math.Clamp(ScrubGrain.NextStart(target, _lastGrain, Volatile.Read(ref _velocity), _hop), _cacheStart, _cacheStart + cacheFrames - _grain);
                _lastGrain = start;
                _cache.AsSpan((int)(start - _cacheStart) * _ch, _grain * _ch).CopyTo(_grainBuf);
                ScrubGrain.Window(_grainBuf, _grain, _ch);
                int produced = ScrubGrain.OverlapAdd(_grainBuf, _carry, dst, _hop, _ch);
                _readFrames += produced; return produced;
            }
            /// <summary>Recentre from RESIDENT bytes only: the decoder is sought (Body.ProbeAt serves what the ring holds; a miss returns 0 instead of waiting)
            /// and decodes up to 2 s; a short decode fills the cache as far as it got. False when nothing at the target is resident.</summary>
            bool TryRecenterResident(long target, int cacheFrames)
            {
                long from = Math.Max(0, target - cacheFrames / 2);
                _view.ResidentOnly = true;
                if (_dec.Seek(from) < 0) return false;
                int got = 0; while (got < cacheFrames) { int n = _dec.Read(_cache.AsSpan(got * _ch)); if (n <= 0) break; got += n; }
                _view.ResidentOnly = false;
                if (got < _grain) return false;
                if (got < cacheFrames) _cache.AsSpan(got * _ch).Clear();
                _cacheStart = from; return true;
            }
            public long PositionFrames => _lastGrain;
            public bool Exhausted => false;                                                                           // retired by its envelope, never by EOF
            public GaplessInfo Gapless => GaplessInfo.None;
            public ReplayGainInfo Loudness => default;
            public void Dispose() { try { (_dec as IDisposable)?.Dispose(); } finally { _lease.Dispose(); } }        // the ring's retire path calls this (V-PA3): the lease dies with the source
        }

        static ScrubGrainSource? s_scrub; static long s_scrubVoiceId;

        public static void ScrubBegin(int ms) => Enqueue(async () =>
        {
            PcmAudioSession? sess; PcmAudioPlayer? backend; Opened opened; IMediaByteSource? bytes;
            lock (s_gate) { sess = s_session; backend = s_backend; opened = s_opened; bytes = s_bytes; }
            if (sess is null || backend is null || bytes is not RingSource owner || s_silent || sess.CurrentState != PlaybackState.Playing) return;   // paused ⇒ visual-only (V-PA41)
            if (!backend.TryAcquireDecoderLease(out IDisposable lease)) return;                                      // no lease: visual-only
            try
            {
                IAudioDecoder dec = CreateDecoderFor(opened);
                var view = new RingSource(owner.Body, ownsBody: false);
                if (!dec.TryOpen(view, sess.Format, out _)) { lease.Dispose(); return; }
                long start = GaplessJoinClock.MsToFrames(ms, sess.Format.SampleRate);
                var grains = new ScrubGrainSource(dec, view, lease, sess.Format.SampleRate, sess.Format.Channels, start);
                long id = sess.NextEngineVoiceId();                                                                   // ≥ 1_000_000 (V-PE26)
                var ring = new RingAudioSource(grains, sess.Format.Channels, ringFrames: sess.Format.SampleRate / 5, targetAheadFrames: 2 * grains.HopFrames, pumpFrames: grains.HopFrames, startFrames: start, rt: backend.ThreadCharacteristics);
                ring.StartProducer();
                await sess.BeginScrubAsync(ring, id, CancellationToken.None).ConfigureAwait(false);
                lock (s_gate) { s_scrub = grains; s_scrubVoiceId = id; }
            }
            catch (Exception ex) { Log.Warn("audio", "scrub begin failed — visual-only", ex); lease.Dispose(); }     // V-PA25: never a track fault
        });
        public static void ScrubMove(int ms, double velocity)
        {
            ScrubGrainSource? g; int rate; lock (s_gate) { g = s_scrub; rate = s_session?.Format.SampleRate ?? 48000; }
            g?.Retarget(GaplessJoinClock.MsToFrames(ms, rate), velocity);                                              // velocity unchanged (V-PA9)
        }
        /// <summary>The release IS the seek (V-PA5): the grain voice fades out and self-retires; the HELD main voice is replaced by a design-A swap at the
        /// release point (the bytes are resident, so the prepare lands in ≈ 20 ms and keeps the D8 ring, TrimmingSource and gapless); with no lease free the
        /// hold is released WITHOUT a fade-in and the seek runs in place on the held voice.</summary>
        public static void ScrubEnd(int ms, uint epoch, uint gen) => Enqueue(async () =>
        {
            ScrubGrainSource? g; long id; PcmAudioSession? sess;
            lock (s_gate) { g = s_scrub; id = s_scrubVoiceId; sess = s_session; s_scrub = null; }
            if (sess is null) return;
            lock (s_gate) { s_pendingSeekMs = ms; s_pendingSeekGen = gen; }
            if (g is not null)
            {
                await sess.ReleaseHeldSilentAsync(id, CancellationToken.None).ConfigureAwait(false);                   // grain voice Out (self-retires); main stays silent + un-held
                Interlocked.Exchange(ref s_seekMailbox, ((long)epoch << 32) | (uint)ms); Volatile.Write(ref s_seekMailboxGen, gen);
                await SeekCoreAsync(0, default).ConfigureAwait(false);                                                  // inline: A (swap from the silent voice) or fallback (fade-in after the in-place seek)
            }
        });
        public static void ScrubCancel() => Enqueue(async () =>
        {
            ScrubGrainSource? g; long id; PcmAudioSession? sess;
            lock (s_gate) { g = s_scrub; id = s_scrubVoiceId; sess = s_session; s_scrub = null; }
            if (g is null || sess is null) return;                                                                      // idempotent (V-PA24)
            await sess.CancelScrubAsync(id, CancellationToken.None).ConfigureAwait(false);                              // ONE command: release + fade-in; grain Out
        });
    }
}
```

The fallback after a scrub: `SeekInPlaceAsync` sees phase 4 already (the hold left the transport at gain 0) and skips its own fade-out. `backend.ThreadCharacteristics` exposes the `IRtThreadCharacteristics?` the backend was built with.

### 4.13 App — seek bar + keyboard + Connect + video (`Shell/Shell.PlayerBar.UI.cs`, `Shell/Shell.PlayerBar.cs`, `Shell/Shell.PlayerBar.Podcast.UI.cs`, `Playback/Playback.Video.cs`) — hosted in the bar, the flagship stage and the video overlay alike (X5)

- `BarSeekRail.OnDown` (`UI:1711-1718`): after `_scrubFrac.Value = …` → `Playback.ScrubBegin(TargetMs())` where `TargetMs() = (int)Math.Clamp(SeekRail.CommitTargetMs(_scrubFrac.Peek(), Playback.DurationMs.Peek(), Playback.Live.Peek()), 0L, int.MaxValue)`. `OnDragMove` (`:1720-1725`): `Playback.ScrubMove(TargetMs())`. `OnCommit` (`:1727-1741`): `Playback.ScrubEnd(TargetMs())` replaces `s_barSeek.Reset(); Playback.SeekTo(…)`; `_committedAtMs` and `HoldsDrop` **stay** (V-PA12) — the hold is released by a `Seeked` whose generation matches (the model's `PositionMs` publication after that drain) or by `CommitHoldMs`, never by the committing drain's own publish: `OnReport` (`:1683`) becomes `if (_committedAtMs > 0L && !_scrubbing.Peek() && Playback.LastSeekLandedGen.Peek() == _committedGen) _committedAtMs = 0L;` with `_committedGen` captured from `Playback.SeekGen` at commit. `OnCancel` (`:1743-1749`): `Playback.ScrubCancel()`. **Unmount cleanup (V-PA24/X5)**: `UseEffect(() => () => { if (_scrubbing.Peek()) Playback.ScrubCancel(); })` mounted once. U-8: `CommitTargetMs` (`RU:536-539`) applies `SeekTarget.Clamp((int)…, (int)durationMs)` for tracks (ints — V-PA27).
- Time labels (U-7): `BarTimeText.Label` (`UI:1808-1820`) reads `Playback.ScrubPositionMs.Value` when ≥ 0; the tooltip is attached to the **rail container** `BoxEl` (the hit-testable root, `UI:1659-1673`), not the `HitTestVisible = false` thumb (V-PA40), bound to the same text.
- Keyboard (V-PA22): `PlayerKey(key, focusedContainer, handled, modified, shift)` (`RU:450-458`) takes `modified = e.Ctrl || e.Alt` and `shift = e.Shift` separately and returns `SeekBack/SeekForward` (fine when `shift`); the bar's handler (`UI:645-658`) calls `s_ladder.Step(dir, fine: e.Shift, from: Playback.Snap().Position(now), duration, now)` and writes the visual target to `Playback.ScrubPositionMs`-style preview (`s_keyPreviewMs` signal read by the rail/labels); a `UseInterval(_keyTick, 50f, enabled: s_ladderActive)` on the bar calls `s_ladder.Tick(now)` and commits `Playback.SeekTo(target)` once. `PlayerSeekAccumulator`, `s_barSeek` and `BarSeekBy`'s use of it (`Podcast.UI.cs:13-33`) are replaced by the ladder (the ±10 buttons call `Playback.SeekTo(SeekTarget.Clamp(pos ± 10_000, duration))` directly); `Wavee.Tests/PodcastPlayerBarTests.cs:17-49` is rewritten against `KeyboardScrubLadder`. The flagship stage routes the same keys to the same handler (X5).
- Connect: the reducer gates `fx.Scrub*` on `RoutesLocal` (§4.9), so a foreign owner gets the visual scrub and exactly one forwarded `seek_to` from `DoSeek`'s remote branch with U-3's optimistic position.
- Video (`Playback.Video.cs:468`): `Seek(long ms, bool accurate = true, uint gen = 0)` stores `gen` for its `Position`/`Seeked` posts; `Execute` dispatches `Video.ScrubPreview(ms)` on `fx.ScrubMove` when `s_hostKind == Video` — a ≤ 10 Hz preview seek to the nearest buffered keyframe via the planner (`:275-288`), audio muted for the gesture; `fx.ScrubEnd`/`fx.Seek` → the accurate `Video.Seek`.

### 4.14 App — glitch telemetry and the Diagnostics card (D1; V-PA26/X4, V-PA31)

**`[app]Playback/Playback.Glitch.cs`** — NEW, CORE, `System` only, written on the tick thread and read on the UI thread, so every read goes through one lock:

```csharp
namespace Wavee;

/// <summary>Per-session glitch ledger (D1). Primitives in, a snapshot out; the tick thread records, the UI reads — one lock, no engine types.</summary>
public sealed class GlitchLedger
{
    readonly object _gate = new();
    int _incidents, _producerStarves, _deviceLate, _gcImplicated, _byteWaits;
    long _framesLost, _longestStallMs, _lastStallMs, _lastStallAtUnixMs, _longestByteWaitMs;

    public readonly record struct Snapshot(int Incidents, int ProducerStarves, int DeviceLate, int GcImplicated, int ByteWaits,
        long FramesLost, long LongestStallMs, long LastStallMs, long LastStallAtUnixMs, long LongestByteWaitMs, string Verdict);

    /// <summary>One RT incident: <paramref name="gapFrames"/> of silence, the ring's fill at the miss, the GC pause ticks in the window.</summary>
    public void Record(int gapFrames, int ringFramesAtMiss, long gcPauseTicks, int sampleRate, long nowUnixMs)
    {
        long stallMs = sampleRate > 0 ? gapFrames * 1000L / sampleRate : 0;
        lock (_gate)
        {
            _incidents++; _framesLost += gapFrames; _lastStallMs = stallMs; _lastStallAtUnixMs = nowUnixMs;
            if (stallMs > _longestStallMs) _longestStallMs = stallMs;
            if (ringFramesAtMiss == 0) _producerStarves++; else _deviceLate++;
            if (gcPauseTicks > 0) _gcImplicated++;
        }
    }
    public void RecordByteWait(long stallMs) { lock (_gate) { _byteWaits++; if (stallMs > _longestByteWaitMs) _longestByteWaitMs = stallMs; } }
    public void Reset() { lock (_gate) { _incidents = _producerStarves = _deviceLate = _gcImplicated = _byteWaits = 0; _framesLost = _longestStallMs = _lastStallMs = _lastStallAtUnixMs = _longestByteWaitMs = 0; } }
    public Snapshot Read() { lock (_gate) return new(_incidents, _producerStarves, _deviceLate, _gcImplicated, _byteWaits, _framesLost, _longestStallMs, _lastStallMs, _lastStallAtUnixMs, _longestByteWaitMs, VerdictLocked()); }

    /// <summary>A loc KEY, not English (V-PA34): the card translates it.</summary>
    string VerdictLocked() => _incidents == 0 ? "clean" : _gcImplicated * 2 >= _incidents ? "gcPauses" : _byteWaits > 0 && _producerStarves > 0 ? "byteStarved" : _producerStarves > _deviceLate ? "producerStarved" : "deviceLate";
}
```

Wiring in `Playback.Audio.cs`: `DrainXruns` (`:2903-2932`) calls `s_ledger.Record(ev.GapFrames, ev.RingFrames, ev.GcPauseTicksDelta, rate, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())` per event and emits one structured event per incident with the real signature (`Platform.cs:713-714`):

```csharp
Log.Event(WaveeLogLevel.Warning, "audio", "audio.glitch", "underrun", null, (long)gapMs, null,
    WaveeLogField.Of("stallMs", (long)gapMs), WaveeLogField.Of("ringFramesAtMiss", ev.RingFrames), WaveeLogField.Of("gcTicks", ev.GcPauseTicksDelta),
    WaveeLogField.Of("posMs", posMs), WaveeLogField.Of("track", s_id.Text), WaveeLogField.Of("verdict", s_ledger.Read().Verdict));
```

`FoldStall` (`:2868-2899`) calls `s_ledger.RecordByteWait(stallMs)` on the `Recovering` edge; `RetireXruns` emits `audio.session.summary` with the snapshot's fields and `Reset()`s. `Metrics` (`:4158-4200`) gains `GlitchIncidents, LongestStallMs, GlitchVerdictKey, MmcssRegistered, RingTargetMs, RingFillMs, DevicePaddingMs` (`MmcssRegistered` = `MmcssProAudio.ProAudioRegistered`; `RingTargetMs`/`RingFillMs` from the feed's `TargetAheadFrames` and `sess.BufferedFrames`; `DevicePaddingMs` from the raw `DevicePaddingFrames`). **`PlaybackHealthView`** (X4) lives on `RuntimePageView` (`Diagnostics.UI.cs:265-304`), inserted into `body` right after `StatusSection(status)` and beside the flagship's `StageCard`, refreshing itself:

```csharp
sealed class PlaybackHealthView : Component
{
    readonly Signal<Playback.Audio.Metrics> _m = new(default);
    public override Element Render()
    {
        UseInterval(() => _m.Value = Playback.Audio.Metrics.Read(), 1000f, enabled: true);
        var m = _m.Value; var inv = CultureInfo.InvariantCulture;
        return Card(Loc.Get(Strings.Diagnostics.Playback.Health),
            Row(Loc.Get(Strings.Diagnostics.Playback.Verdict), Loc.Get("diagnostics.playback.verdict." + m.GlitchVerdictKey)),
            Row(Loc.Get(Strings.Diagnostics.Playback.Incidents), m.GlitchIncidents.ToString(inv)),
            Row(Loc.Get(Strings.Diagnostics.Playback.LongestStall), Loc.Format(Strings.Diagnostics.Playback.Ms, m.LongestStallMs)),
            Row(Loc.Get(Strings.Diagnostics.Playback.Mmcss), Loc.Get(m.MmcssRegistered ? Strings.Diagnostics.Playback.MmcssOn : Strings.Diagnostics.Playback.MmcssOff)),
            Row(Loc.Get(Strings.Diagnostics.Playback.RingFill), Loc.Format(Strings.Diagnostics.Playback.MsOfMs, m.RingFillMs, m.RingTargetMs)),
            Row(Loc.Get(Strings.Diagnostics.Playback.DeviceBuffer), Loc.Format(Strings.Diagnostics.Playback.Ms, m.DevicePaddingMs)),
            Row(Loc.Get(Strings.Diagnostics.Playback.Decode), Loc.Format(Strings.Diagnostics.Playback.XRealtime, m.DecodeXRealtime)),
            Row(Loc.Get(Strings.Diagnostics.Playback.LastSeek), m.LastSeekLatencyMs >= 0 ? Loc.Format(Strings.Diagnostics.Playback.MsKind, m.LastSeekLatencyMs, Loc.Get("diagnostics.playback.seekKind." + m.LastSeekKind)) : null));
    }
}
```

`RuntimePageView.Render` adds `body.Add(Embed.Comp(static () => new PlaybackHealthView()));` after `StatusSection`. Every string is a loc key in `assets/loc/{en-US,nl,ko-KR}.json` (`diagnostics.playback.*`, including the five verdict keys and the three seek kinds) with real nl/ko translations; the `Strings` constants are generated from `en-US.json` (verified).

### 4.15 App — normalization modes, album mode, settings, default volume (D5, D6; V-PA13, V-PA14, V-PA15, V-PA18, V-PA34)

**`[app]Spotify/Spotify.Audio.cs`**: `HeaderGainBytes = 160` (`:396`); `HeadAlbumGainDb(head) => head.Length >= 156 ? SaneGain(BitConverter.ToSingle(head[152..156])) : 0f` and `HeadAlbumPeak(head) => head.Length >= 160 ? SanePeak(BitConverter.ToSingle(head[156..160])) : 0f` beside `:1200-1207`; `GainFor` returns `(GainDb, Peak, AlbumGainDb, AlbumPeak)`; `Opened` (`:164-167`) gains `float AlbumGainDb = 0f, float AlbumPeak = 0f`; the lossless choice (`:462`) keeps **`NormalizationGain(loudnessDb, truePeakDb)` at the −14 reference with no mode** (V-PA14: the mode is applied once, in `NormalizationFactor`) and passes the album pair from `lossless.DefaultFileNormalizationParams`'s album fields when present. **`[app]Spotify/Spotify.Audio.Stream.cs`** (V-PA13): `Body` gains `_albumGainBits/_albumPeakBits` (`AlbumGainDb`/`AlbumPeak`, beside `:1420-1426`), set at open from `GainFor` (`:2132`) and from chunk 0 in `Observe` (`:1810-1811`); `INormalizationSource` (`Playback.Audio.cs:1327-1333`) gains `AlbumGainDb`/`AlbumPeak`; `RingSource` forwards them.

**`[app]Playback/Playback.Audio.cs:1117-1135`** — REPLACED (the app-level `Opened` at `:3087-3089` also gains `AlbumGainDb`/`AlbumPeak`):

```csharp
public enum NormalizationMode : byte { Quiet = 0, Normal = 1, Loud = 2 }          // PERSISTED ints — append only
/// <summary>The limiter's ceiling (−1.5 dB), shared with the engine's LimiterSpec.Default: every normalization cap lands here (N-1).</summary>
public const float LimiterCeilingLinear = 0.84139514f;
/// <summary>ReplayGain tags are referenced to −18 LUFS; the −14 frame everything else uses is +4 dB (V-PA18).</summary>
public const float ReplayGainToSpotifyDb = 4f;
public static float PregainDb(NormalizationMode mode) => mode switch { NormalizationMode.Quiet => -9f, NormalizationMode.Loud => 3f, _ => 0f };

/// <summary>ONE linear factor (D5): the chosen pair (album when asked and present, else track) + the mode's pregain, clamped ±30 dB, capped so factor × peak
/// ≤ the limiter ceiling — except Loud, which drives the (pre-volume) limiter as Spotify's does. Off, or a non-finite figure, ⇒ exactly 1. PURE.
/// The two-argument form keeps AudioAdapterTests.cs:140-147 compiling (Normal, track pair).</summary>
public static float NormalizationFactor(bool enabled, float gainDb, float peak = 0f) => NormalizationFactor(enabled, NormalizationMode.Normal, false, gainDb, peak);
public static float NormalizationFactor(bool enabled, NormalizationMode mode, bool album, float gainDb, float peak, float albumGainDb = 0f, float albumPeak = 0f)
{
    if (!enabled) return 1f;
    bool useAlbum = album && float.IsFinite(albumGainDb) && albumGainDb != 0f;
    float g = useAlbum ? albumGainDb : gainDb, p = useAlbum ? albumPeak : peak;
    if (!float.IsFinite(g)) return 1f;
    float factor = MathF.Pow(10f, Math.Clamp(g + PregainDb(mode), -MaxGainDb, MaxGainDb) / 20f);
    float cap = mode == NormalizationMode.Loud ? 1f : LimiterCeilingLinear;
    if (p > 0f && float.IsFinite(p) && factor * p > cap) factor = cap / p;
    return factor;
}
internal static float GainLinear(float gainDb, float peak = 0f, float albumGainDb = 0f, float albumPeak = 0f)
    => NormalizationFactor(Platform.Settings.Get(Platform.Keys.NormalizationEnabled), (NormalizationMode)Platform.Settings.Get(Platform.Keys.NormalizationMode),
        Platform.Settings.Get(Platform.Keys.NormalizationAlbum), gainDb, peak, albumGainDb, albumPeak);

/// <summary>Live (D5): every live voice's gain slot ramps by now/baked for THAT voice (V-PA15) — the decoders keep their folded factor.</summary>
public static void SetNormalization()
{
    bool enabled = Platform.Settings.Get(Platform.Keys.NormalizationEnabled);
    var mode = (NormalizationMode)Platform.Settings.Get(Platform.Keys.NormalizationMode);
    bool album = Platform.Settings.Get(Platform.Keys.NormalizationAlbum);
    PcmAudioSession? sess; Opened opened; KeyValuePair<long, float>[] baked;
    lock (s_gate) { sess = s_session; opened = s_opened; baked = s_bakedFactor.ToArray(); }
    if (sess is null) return;
    float now = NormalizationFactor(enabled, mode, album, opened.GainDb, opened.Peak, opened.AlbumGainDb, opened.AlbumPeak);
    foreach (var (voiceId, bakedFactor) in baked) if (bakedFactor > 0f) sess.SetVoiceGain(voiceId, now / bakedFactor, rampMs: 50);
    Log.Info("audio", $"normalization enabled={enabled} mode={mode} album={album} factor={now:0.###}");
}
```

`Boot` (`:412`) becomes `Settings.ApplyNormalization ??= static () => SetNormalization();` with `Settings.ApplyNormalization` typed `Action?` (V-PA27). **ReplayGain (N-3, V-PA18)**: both adapters parse `REPLAYGAIN_TRACK_GAIN/_PEAK/_ALBUM_GAIN/_ALBUM_PEAK` in their `TryOpen` — FLAC's `ParseVorbisComment` (`Playback.Audio.Flac.cs:254-273`) keeps the four ranges; the Vorbis adapter parses the comment header it already reads (`Playback.Audio.cs:1516`) — and when the catalogue carried nothing (`GainDb == 0`), fold `GainLinear(g + ReplayGainToSpotifyDb, p, ag + ReplayGainToSpotifyDb, ap)`. **Keys** (`[app]Platform/Platform.Settings.cs`, X3/V-PA34): `NormalizationMode = new("playback.normalization.mode", 1)`, `NormalizationAlbum = new("playback.normalization.album", false)`; `SavedVolume` default `0.794f` stays where it is declared (`Platform.cs:215`). **Settings** (`Settings.UI.Playback.cs`): `s_normMode` declared beside `s_quality` and seeded in `SeedPlayback` (`:75-85`: `s_normMode.Value = Math.Clamp(s.Get(Platform.Keys.NormalizationMode), 0, 2);`); after the normalization row (`:113-116`):

```csharp
Row(Loc.Get(Strings.Settings.Playback.NormalizationMode), Loc.Get(Strings.Settings.Playback.NormalizationModeSub),
    ComboBox.Create([Loc.Get(Strings.Settings.Playback.NormQuiet), Loc.Get(Strings.Settings.Playback.NormNormal), Loc.Get(Strings.Settings.Playback.NormLoud)],
        s_normMode, width: 200f, itemDescriptions: [Loc.Get(Strings.Settings.Playback.NormQuietSub), Loc.Get(Strings.Settings.Playback.NormNormalSub), Loc.Get(Strings.Settings.Playback.NormLoudSub)],
        onChange: static i => { Platform.Settings.Set(Platform.Keys.NormalizationMode, i); ApplyNormalization?.Invoke(); }),
    RowGlyph(Tab.Playback, "normalizationMode")),
Row(Loc.Get(Strings.Settings.Playback.NormalizationAlbum), Loc.Get(Strings.Settings.Playback.NormalizationAlbumSub),
    Toggle(Platform.Keys.NormalizationAlbum, afterWrite: static _ => ApplyNormalization?.Invoke()), RowGlyph(Tab.Playback, "normalizationAlbum")),
```

with catalog rows in `Settings.cs:150-152` (`new(Tab.Playback, "Audio", "normalizationMode", "Speaker")`, `new(Tab.Playback, "Audio", "normalizationAlbum", "MusicAlbum")` — distinct glyphs), and loc keys `settings.playback.{normalizationMode,normalizationModeSub,normQuiet,normQuietSub,normNormal,normNormalSub,normLoud,normLoudSub,normalizationAlbum,normalizationAlbumSub}` in the three files (the `normalizationSub` text drops "applies from the next song").

### 4.16 App — stream layer (`[app]Spotify/Spotify.Audio.Stream.cs`; V-PA19, V-PA20, V-PA36)

- **Second reader (V-PA20).** `Body.ProbeAt(long offset, Span<byte> dst, int waitMs, bool residentOnly)`: copies resident bytes via `Ring.TryCopy` (`:1301`) **without** touching the cursor, the epoch or the interrupt; on a miss with `residentOnly` returns 0 at once; otherwise enqueues a `RangeRequest { Probe = true, ViewOnly = true }` behind the live fill (not at the queue front, never cancelling the fill) and waits on `_wake` up to `waitMs`. `Body.ProbeRange(offset, bytes)` is the same enqueue without a copy. A non-owning `RingSource` (`ownsBody: false`) routes `ReadAt` → `ProbeAt(…, DefaultWaitMs, ResidentOnly)`, `Retarget` → `ProbeRange` (resident ⇒ nothing), `ResumeFrom` → no-op, `InterruptPendingRead` → no-op; after `TakeOwnership` it behaves exactly like today's owner. `ResidentOnly` is a public bool the scrub source toggles around its recentre (V-PA30).
- S-2: `RangeIdleTimeoutMs = 8_000` replaces `RangeTimeoutMs` (`:537`); the `CancellationTokenSource` created at `:724` is **passed into** `FetchRangeAsync` (`:1614` gains a `CancellationTokenSource cts` parameter; the call at `:735` passes it) and the body loop (`:1647`) re-arms `cts.CancelAfter(RangeIdleTimeoutMs)` after every `n > 0` (V-PA36); `Ring.ReadAt`'s starve branch (`:969-975`) calls `_fetch.CancelIfPending(fileOffset)`.
- S-12: register `_inFlight` **before** `FillFromDisk` (`:713-725`) and re-check `req.Epoch != body.Epoch` after it.
- S-13: `LowWaterBytes` (`:1298`) → `Math.Max(SlotBytes, Math.Max(librespot, Math.Max(_windowBytes / 2, _windowBytes - Fetcher.MaxRangeBytes)))`.
- P-6: `FetchRangeAsync` (`:1656-1682`) returns `new FetchResult(landed)` when any slot landed before a fault; `_refusedAt` only when `landed == 0`.
- S-10: `Retarget` (`:1013`) treats a probe inside `[_pendingStart, _pendingEnd)` with the fill's epoch as resident (no cancel).
- S-11 / D8: `Retarget` plans `KeepBehindSlots / 2` slots behind the landing after the probe; `Land` (`:1077-1083`) refuses to overwrite a slot whose chunk lies in `[cursorChunk − KeepBehindSlots, cursorChunk)`.
- Landing-time page index: `Body.Observe` (`:1790-1814`) walks each landed slot with `Ogg.FindPage` and appends **container offsets** (`fileOffset − Skip`, V-PA36) and granules to a **grow-only** `long[]` pair with a published count (`Volatile.Write(ref _indexCount, n)` after the writes — no copy-on-write); `Body.PageIndexSnapshot()` returns `(ReadOnlySpan<long> offsets, ReadOnlySpan<long> granules)` up to the count; the Vorbis adapter seeds `Ogg.BeginSeek` through `PageIndex.Merge(…)` (`Playback.Audio.Ogg.cs:300-602`). FLAC keeps its own `_seekPoints` (`Playback.Audio.cs:3845`) — fed from frame headers found in landed slots by the FLAC adapter, not by `Body`.
- H-12: `WidenWhenProven` (`:1543-1549`) sets a `Volatile` `_growRequested` flag the fetch loop polls at the top of `ServeAsync` (never a channel post — `DropOldest` could drop it, `:527-528`).
- R-6: `Stats` gains `Faults`.

### 4.17 Codec fixes (P-1, P-2, P-4, P-5, P-8, S-9; V-PA17, V-PA37)

- P-1 (`Playback.Audio.Flac.cs:490-552`, `Kernels.cs`): the **wide path** — when `bps > 32` the side subframe reads its warm-ups via `ReadLong`, restores FIXED/LPC in `long` (the `long` accumulator path already exists for high bps, `Kernels.cs:498-545`), and decorrelates in `long` before the float scale; `Unsupported` is no longer emitted (a skipped frame would play near-silent, `:3983`).
- P-2 (`Playback.Audio.Vorbis.cs:629-630`): `p = 0.5f * (1f - w); q = 0.5f * (1f + w);`.
- P-4 (`Kernels.cs:766-770`): an explicit per-count table (RFC 9639 §9.1.3): 3 = FL FR FC; 4 = FL FR BL BR; **5 = FL FR FC BL BR**; 6 = FL FR FC LFE BL BR; **7 = FL FR FC LFE BC SL SR**; **8 = FL FR FC LFE BL BR SL SR**; weights: FL/FR 1, FC 0.7071 to both, BL/SL → L 0.7071, BR/SR → R 0.7071, **BC 0.5 to each side**, LFE dropped.
- P-5 (`Playback.Audio.cs:3236-3250`): on `ID3` the probe **re-reads** at `10 + syncsafe(size) (+ 10 when the footer flag, byte 5 bit 4, is set)` and sniffs that head; the FLAC adapter's `TryOpen` skips the same prefix.
- P-8 (`Playback.Audio.Flac.cs:352, 365`): `(b[1] & 0xFE) != 0xF8`.
- S-9 (`Playback.Audio.cs:3902-3903`): `if (2L * _si.MaxFrame > _win.Length) Array.Resize(ref _win, (int)Math.Min(2L * _si.MaxFrame, 16L << 20));`; `Observe` (`Flac.cs:722-723`) treats `Overrun` as "skip candidate" while `window.Length - at >= MaxFrame`.
- P-12: **dropped** — the libvorbis single-entry rule could not be verified against its source here; the existing errata handling (`:391`, pinned at `VorbisTests.cs:413-436`) stands.

### 4.18 Load-test harness (D1 verification; V-PE34, V-PA39)

**`[engine]Audio/AudioStressModel.cs`** — NEW, pure (`namespace FluentGpu.Media; using System; using System.Collections.Generic;`): the policy-level model now carries the **device buffer**: silence is audible only once `device` (initially `DeviceBufferMs`) has drained; each RT block refills the device when it consumes; the producer schedule has `Rate` and `Absences`. Scenarios that mean something with a 2 s target: **absence 2.5 s** ⇒ legacy = stop/start cycles, cushion = one incident of ≤ 500 ms; **producer at 0.8× for 10 s then 1.5×** ⇒ legacy = the stutter loop (> 2 resumes/s), cushion = ≤ 2 incidents, gaps bounded by the doubling cushion. The model is **also** driven against the real session: `StarvationRecoveryTests` builds a headless session (`HeadlessAudioEndpoint`, `SignalGeneratorSource` behind a `RingAudioSource` whose producer is pumped by hand) and scripts `WorkerPumpOnce`/`FeedOnce` per the schedule, asserting on `RenderBlock`'s behaviour (silence submitted, no `Stop`, `PlayedFrames` content clock frozen, `ResumeFrames` doubling). `IStarvationPolicy`/`StarvationVerdict` stay as the pure contract the two policies implement.

**`--stress-audio`** (`[app]Screens/Diagnostics.Probe.cs`, a `TryRunCliArm` case beside `:40-60`): `--burners <n per class> --memory-mib <n> --minimize --battery-saver --seconds <n> [--file <path> | --track <uri>]`. The on-box gate plays through the **real WASAPI device** (V-PA39): `--file` opens a local file through the normal `FileByteSource` path, `--track` a logged-in Spotify track; without either the run uses the `--fake` silent voice through `PacedSilentEndpoint` and is labelled `smoke` in its output (CI only, not the gate). It starts the burner threads (`BelowNormal/Normal/AboveNormal/Highest`), the allocator (`memory-mib` of `byte[]` touched every 50 ms), minimizes via the PAL, emulates battery saver by applying `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` **on** (the inverse of F4), plays for `seconds`, prints the `GlitchLedger.Snapshot` and exits 0 when `Incidents == 0` (`--allow-one` tolerates 1).

---

## 5. Tests (pure classes and the deterministic engine harness; no source text)

**App (`Wavee.Tests`)**
| Test class | Pins |
|---|---|
| `GlitchLedgerTests` (new) | verdict keys over primitive inputs; longest/last stall; GC attribution; byte-wait fold; `Reset`; concurrent `Record`/`Read` under the lock |
| `ScrubModelTests` (new) | `Down` begins; `Move` coalesces to ≤ 1 per 50 ms and smooths velocity (dimensionless, unchanged); `Up` commits once and returns to Idle; `Cancel` from every live state; `Audible` cleared on cancel |
| `KeyboardScrubLadderTests` (new) | 5/15/30 s by hold time; Shift = 1 s; clamp to duration; `Tick` commits only after 250 ms idle and exactly once; a re-press within the grace continues the ladder |
| `PlaybackReducerSeekTests` (new) | `EmitSeek` from `DoSeek` AND `Restart` bumps `SeekGen` and stamps `fx.SeekEpoch = LoadEpoch`; `Seeked`/`Position` with the current gen land, older ones are dropped; `DoEndingSoon` still fires from a current-gen `Position`; the remote branch writes `PosMs/PosQpc/RemoteSeekAtMs/RemoteSeekTargetMs`; the cluster is held while fresh and > 1.5 s away, adopted after 2 s or within tolerance, cleared by `ClusterAck`; `Position()` frozen while `Buffering`; `PrepareLost` with a stale epoch is dropped, a current one re-arms; `ScrubEnd` (local audio, audible) yields `fx.ScrubEnd` and no `fx.Seek`; `ScrubEnd` (remote/video/paused) yields one `fx.Seek`; every scrub slot sets `Effects.Any` |
| `MuteRulesTests` (new) | unmute at volume 0 restores; a raise clears `SinkMuted` and emits `MuteOn == false` only then; `UnmuteDefault == 0.794f`; `DoTick` emits `PersistVolume` once ≥ 500 ms after the last change |
| `ShellPlayerBarUiRulesTests` (extend) | `CommitTargetMs` runs through `SeekTarget.Clamp` (ints); `HoldsDrop`/`CommitHoldMs` unchanged (`:741-746` stays green); `PlayerKey(…, modified, shift)` maps Shift+Left/Right to the fine intents and Ctrl/Alt to None |
| `PodcastPlayerBarTests` (rewrite) | the ±10 buttons seek directly; the ladder replaces `PlayerSeekAccumulator` |
| `AudioAdapterTests` (extend) | the two-argument `NormalizationFactor` still equals the Normal/track result (`:140-147` kept); pregains −9/0/+3; album pair only when asked and present; cap at `LimiterCeilingLinear` for Quiet/Normal, 1.0 for Loud; `HeaderGainBytes = 160` parse of four floats; `GainLinear(g + 4, …)` for ReplayGain; two seeks 5 ms apart with the first landing interrupted leave `Exhausted == false`, `Read` serves silence until the landing re-reads, then lands the second target; a page hole re-primes and pads the clock; a reopen with the same setup hash allocates nothing (the existing allocation gate pattern, not a timing claim) |
| `AudioStreamTests` (extend) | idle-timeout settles a reply that stalls after one slot at 8 s; `LowWaterBytes ≥ window/2` incl. the 512 KiB floor; `_inFlight` registered before the disk fill; `landed > 0` never flips `Refusing`; a probe inside the pending range does not cancel it; behind slots survive `Land`; `ProbeAt` serves resident bytes without moving the cursor/epoch and returns 0 at once with `residentOnly`; the landing-time index stores container offsets and is grow-only with a published count; `GrowRequested` is polled, never dropped |
| `FlacTests` (extend) | 32-bit L/S, S/R, M/S frames decode bit-exactly through the `long` side path; the 3/4/5/6/7/8-channel fold-down weights (5 = FL FR FC BL BR, 7 = FL FR FC LFE BC SL SR, 8 = FL FR FC LFE BL BR SL SR; BC 0.5 per side); an ID3v2-prefixed open re-reads at `10 + size (+10)`; 15-bit sync rejects `0xFA/0xFB`; a `MaxFrame` of 48 KiB seeks without decode-forward; `REPLAYGAIN_*` tags parsed |
| `VorbisTests` (extend) | synthetic floor-0 (orders 2 and 3) through the new **public pure seam `Vorbis.RenderFloor0(…)`** against the spec in `double`, ≤ 1e-5 relative (the Xiph vectors carry no floor 0); a conformance pass over the vendored Xiph vectors using the repo's **3 s reference convention** (`Fixtures/ogg/README.md`; no full-length `.s16`); a 160 kbps rung fixture; a Spotify-wrapped (167-byte prefix) synthetic file through the container-offset path |
| `VorbisFuzzTests` (new, `[Trait("Category","Fuzz")]`) | 10 000 mutations per fixture through `Ogg.Reader.NextPacket`, `Vorbis.Decoder.Open`, `DecodePacket`: no exception, no out-of-range, setup allocation ≤ 32 MB (P-11) |
| `Mp3DecoderTests` (new) | over a NEW fixture `Fixtures/mp3/sine-440.mp3` (ffmpeg libmp3lame, documented in the fixtures README): decodes, gapless numbers from the LAME tag, and the **assertion** `two hundred frames allocate < 64 KiB` (an assertion, not a measurement) |
| `AudioStressTests` (new, app side) | `--stress-audio` option parsing; `smoke` labelling without `--file`/`--track`; the ledger summary format |

**Engine (`FluentGpu.Engine.Tests`)**
| Test class | Pins |
|---|---|
| `StarvationRecoveryTests` (new) | drives the REAL session: a headless endpoint + a hand-pumped ring; an empty active ring makes `RenderBlock` submit silence (inside the tripwire), never call `Stop`, record ONE incident with growing severity, and keep `PlayedFrames` (content) frozen while `RawPlayedFrames` advances; resume waits for `ResumeFrames` and phase 0 and fades in over 5 ms; `ResumeFrames` doubles per incident (not for a seek rebuffer), decays after 30 s; `GrowAhead` fires once per incident; the policy model: 2.5 s absence ⇒ cushion = 1 incident ≤ 500 ms, legacy = stop/start cycles; 0.8× then 1.5× ⇒ cushion ≤ 2 incidents, legacy = > 2 resumes/s |
| `DeadSinkTests` (new) | a `FailingAudioSink` double whose `WritableFrames` flips to −1 after N writes: mid-fade (phase 1 and phase 2) the session drops pending frames, reports exactly one sink failure, `Stop`s once and reaches phase 3 within two wakes |
| `EqStageCommandTests` (new) | changes land only via `CmdSetEq` at a block boundary; a mid-ramp `AdoptPending` commits the pending cascade first; no reallocation on a live stage (16-band capacity); enable ramps from identity on a voice built with the EQ off (the chain always has an EQ stage); a failed enqueue is retried on the next tick; the preamp recomputes on a gain-only band edit |
| `SeekVoiceSwapTests` (new) | `TryJumpWithinRingAsync` is decided on the RT (accept/refuse published), reads the tail `[head, head+fade)`, jumps `delta − fade`, blends by ref, resets WSOLA; the equal-power pair is **power-complementary** (cos² + sin² = 1 within 1e-6); `SwapToPreparedAsync` is one compound command (no block renders with half a swap), returns `StartPositionFrames`, keeps the old voice at unity until the swap block and retires it off-RT; a cancelled prepare disposes the item and releases its lease exactly once; the 80 ms stale timer fades the old voice when the prepare is late; `SeekInPlaceAsync` waits for the fade end (phase 4), never calls `Stop`/`Reset`; `RebaseAtSubmit` anchors within one block; the `IMediaSession.SeekAsync` dispatcher still passes the 15 existing tests |
| `PcmRingKeepBehindTests` (new) | the producer never overwrites the behind span; `BehindFloats` never overstates (after a flush it is 0; after `maxHead − keepBehind` it is exact); `TrySkip`/`TryRewind` bounds; SPSC invariants |
| `PolyphaseResamplerTests` (new) | 44.1→48: passband ripple ≤ 0.1 dB to 20 kHz, images ≤ −90 dB at 24.1 kHz (FFT oracle); a linear ramp resamples to the correctly-timed ramp (no mirror: error ≤ 1e-4 — V-PE2); callers that refill only on an empty hold never stall (`Consumed == inFrames` when the input runs out); block-size independence (identical output at 1/480/4096-frame pumps); the exact ratio has no drift over 10 minutes; `Flush` emits the trailing half kernel; total emitted = ⌊in × to/from⌋ ± 1 after `Flush` (V-PE36); **44056 → 48000** takes the interpolated path with images ≤ −75 dB; `InterpPhases + 1` rows, no wrap |
| `LimiterLookaheadTests` (new) | a 50 Hz sine at 0.8 gets **0 dB** of gain reduction; no output sample exceeds the ceiling for a +6 dB square, a single-sample spike and a 19 kHz inter-sample-peak tone; the attack is a ramp over the lookahead; `LatencySamples` equals the delay; the release snaps to 1; mono/stereo/6-channel delay lines |
| `ScrubGrainTests` (new) | Hann COLA at 50 % overlap sums to unity ±1e-5; `NextStart` velocity following, clamp ±4×, snap after a jump; `OverlapAdd` of unity-velocity grains matches a straight decode **within 1e-6** (float), and refuses a short `dst` |
| `ThreadCharacteristicsTests` (new) | a recording fake passed as the INSTANCE seam sees `EnterDecode()` once per producer thread and once for the clock thread, reverted on exit; `NullRtThreadCharacteristics.EnterDecode()` is null through the interface |
| `BiquadDoubleStateTests` (new) | 31 Hz +12 dB on 60 s of −60 dBFS noise: noise floor ≤ −120 dB relative; subnormal flush updates `_y1`; `AudioGraphTests` coefficient pins hold within 1e-9 as doubles |
| Zero-alloc pins (extend `AudioVisualizerDemandTests` pattern) | `RenderSilence` (inside the tripwire); the `CmdJumpWithinRing`/`CmdSwapVoice`/`CmdHoldVoice` RT arms; `ScrubGrain.*`; `PcmRing.TrySkip/TryRewind`; `PolyphaseResampler.Process`; `LimiterStage.Process`; **`TapSpectrumBlock`** after the flagship's wave E |

**On-box (not CI):** `--stress-audio --file <flac> --burners 2 --memory-mib 2048 --minimize --seconds 60` on a real device reports `Incidents == 0`; `--battery-saver` likewise; `audio.seek.done latencyMs` ≤ 80 for ring/indexed seeks; `audio.glitch` absent over a one-hour album with a Release build compiling.

---

## 6. Fixed API contract + merge order (X1) + work packages

**Fixed API contract every WP codes against (from §4):**
engine — `IRtThreadCharacteristics.EnterDecode()`; `MmcssProAudio.{EnterDecode, ProAudioRegistered}`; `PowerThrottling.OptOut`; `RingSizing`; `AudioFeedThread(session, sampleRate, rt, RingSizing, maxBlocksPerWake)`, `TargetAheadFrames`; `RingAudioSource(inner, channels, ringFrames, targetAheadFrames, pumpFrames, keepBehindFrames, startFrames, rt)`, `.{KeptBehindFrames, RtTryJump, ReadyWake, ReadyMinimum, IsReady, GrowAhead}`; `PcmRing(minFloats, keepBehindFloats)`, `.{BehindFloats, TrySkipConsumerSide, TryRewindConsumerSide}`; `PcmAudioPlayer(…, rt, ringSizing)`, `.{TryAcquireDecoderLease, PrepareAtAsync(next, ctx, positionFrames, lease, ct), ThreadCharacteristics}`; `PcmAudioSession.{SeekAsync (dispatcher), TryJumpWithinRingAsync, SwapToPreparedAsync, SeekInPlaceAsync, BeginScrubAsync, CancelScrubAsync, ReleaseHeldSilentAsync, SetVoiceGain, FadeActiveToSilenceAsync, SwapLanded, NextEngineVoiceId, RawPlayedFrames, ScrubGainLinear}`; `IPreparedItem.IsReadyFor`; mixer commands `CmdSetEq, CmdSetVoiceGain, CmdJumpWithinRing, CmdSwapVoice, CmdHoldVoice, CmdReleaseVoice, CmdFadeOutHold`; `MixerCmd.{Linear, Coeffs, Bands}`; `MixVoice.{Held, HoldAtFrame, Blend}`; `CrossfadeMixer.VoiceRef`; `EqStage.AdoptPending`, `EqStage.MaxBands`; `LimiterSpec.LookaheadMs`; `LimiterStage(ceiling, release, mixRate, lookaheadMs, channels)`; `PolyphaseResampler.{Process, Flush, Reset, IsActive, LatencySamples}` + `ResampleResult`; `ScrubGrain.{Window, NextStart, OverlapAdd, GrainMs, HopMs}`; `AudioStressModel`, `IStarvationPolicy`, `StarvationVerdict`; `BiquadCoeffs` (double).
app — `Playback.Audio.{Seek(ms, epoch, gen), Load(…, seekGen), ScrubBegin, ScrubMove(ms, velocity), ScrubEnd(ms, epoch, gen), ScrubCancel, SetNormalization(), NormalizationMode, NormalizationFactor (2-arg + full), LimiterCeilingLinear, ReplayGainToSpotifyDb, IGainFolding, ScrubGrainSource, Metrics.{GlitchIncidents, LongestStallMs, GlitchVerdictKey, MmcssRegistered, RingTargetMs, RingFillMs, DevicePaddingMs}}`; `GlitchLedger.{Record(primitives), RecordByteWait, Reset, Read}`; `RingSource(body, ownsBody)`, `.{ReleaseOwnership, TakeOwnership, ResidentOnly}`; `Body.{ProbeAt, ProbeRange, PageIndexSnapshot, AlbumGainDb, AlbumPeak}`; `INormalizationSource.{AlbumGainDb, AlbumPeak}`; `Ogg.Reader.Next.Hole`; `PageIndex.Merge`; `Vorbis.Decoder.SetupHash`, `VorbisDecoderPool.Rent(hash)`, `Vorbis.RenderFloor0` (pure seam); `ScrubModel`, `KeyboardScrubLadder`; `Playback.{ScrubBegin, ScrubMove, ScrubEnd, ScrubCancel, ScrubPositionMs, LastSeekLandedGen, SeekGen}`; `Input.{Gen, ScrubBegin, ScrubMove, ScrubEnd, ScrubCancel, PrepareLost(epoch)}`; `State.{SeekGen, RemoteSeekAtMs, RemoteSeekTargetMs, SinkMuted, Scrub, ScrubPosMs, VolumeDirtySinceMs}`; `Effects.{SeekGen, ScrubBegin, ScrubMove, ScrubEnd, ScrubCancel, ScrubMs, ScrubVelocity, PersistVolume}` (all in `Any`); `EmitSeek`; `Platform.Keys.{NormalizationMode, NormalizationAlbum}` in `Platform.Settings.cs`; `Spotify.Audio.{HeadAlbumGainDb, HeadAlbumPeak}`, both `Opened` records `.{AlbumGainDb, AlbumPeak}`; `PlayerKey(key, focused, handled, modified, shift)`; `Video.Seek(ms, accurate, gen)`, `Video.ScrubPreview`; `PlaybackHealthView`.

**Merge order (X1 — binding, shared with #166):**
1. Flagship **E** in parallel with playback **0c, 0d, 0e, 0f, 0g** (flagship E does not touch `AudioFeedThread.cs`, so 0f's additive edits there need no gate).
2. Flagship **D** (D0, then D1/D2/D3).
3. Flagship **U**.
4. Playback **0a + 0b**, re-anchored on the post-D3/U8 tree; the card on `RuntimePageView`.
5. Playback **1a / 1b** (engine, after E) and **1c** (after D0).
6. Playback **wave 2**, after flagship U6.
7. Playback **wave 3**.
8. Playback **wave 4** (4c after D3).
9. Playback **wave 5**, after U8.

All edits are anchored by **symbol** against the post-flagship tree. Within a wave, every file has exactly one owner; where two WPs of one wave need the same file they are marked **sequential**.

| WP | files (edit only these) | work | wave / gate |
|---|---|---|---|
| **0c Reducer** | `Playback/Playback.cs` (`EmitSeek`; `DoSeek`, `Restart`, `DoAudio` gen guards, `Position`, `MirrorRemote` hold, `DoMute`/`DoVolume`/`SinkMuted`/`UnmuteDefault`, `DoTick` volume persist, `PrepareLost` arm, `Input.Gen`/named ctors, `Effects` slots + `Any`), `Playback/Playback.Host.cs` (`Execute`: `PersistVolume`, gen args, shutdown flush; `Publish`: `SinkMuted`, `LastSeekLandedGen`), `Playback/Playback.Transitions.cs` (`ArmNext` re-emit after `PrepareLost`, `:852-888`), `Playback/Playback.Os.cs` (U-10), `Shell/Shell.PlayerBar.cs` (`CommitTargetMs` clamp only) | §4.9 (U-1…U-5, U-8, V-1, V-PA2 reducer half, V-PA10, V-PA11, V-PA32, V-PA33) | 0 — step 1 |
| **0d Codecs** | `Playback/Playback.Audio.Vorbis.cs` (P-2; `RenderFloor0` pure seam), `Playback/Playback.Audio.Flac.cs` (P-8, S-9 `Observe`, P-1 wide path), `Playback/Playback.Audio.Flac.Kernels.cs` (P-1 `long` side path, P-4 table) | §4.17 (not `Playback.Audio.cs`) | 0 — step 1 |
| **0e Stream** | `Spotify/Spotify.Audio.Stream.cs` (S-2 idle timeout + CTS param, S-12, S-13, P-6, H-12 polled flag, R-6) | §4.16 wave-0 items | 0 — step 1 |
| **0f Engine hygiene** | `[win]Wasapi/MmcssProAudio.cs` (`ProAudioRegistered`), `[engine]Audio/AudioFeedThread.cs` (`TargetAheadFrames`, `RingSizing` record — additive), `[engine]Audio/Biquad.cs` (double coeffs/state, flush), `[engine]Audio/CrossfadeMixer.cs` (`PcmReady` clamp removed, `:246-258`), `[engine]Audio/WsolaAudioSource.cs` (`:72` deleted, Q-2 blend, R-9), NEW `[win]Wasapi/PowerThrottling.cs` | §4.1 F4 leaf, §4.3 H-2 half, §4.7 E-1/E-2 | 0 — step 1 |
| **0g Tests (wave 0)** | NEW `Wavee.Tests/{PlaybackReducerSeekTests, MuteRulesTests}.cs`; extend `ShellPlayerBarUiRulesTests.cs`, `AudioStreamTests.cs`, `FlacTests.cs`, `VorbisTests.cs`; engine NEW `BiquadDoubleStateTests.cs`, `AudioGraphTests.cs` tolerance | §5 | 0 — step 1 |
| **0a Pump telemetry + S-1 + parking** | `Playback/Playback.Audio.cs` (ALL wave-0 edits to this file: `_needsLanding` S-1, tick latch, parked target + gen seed in `Load`, `DrainXruns`/`FoldStall` ledger hooks + `audio.glitch`/`audio.session.summary`, `Metrics` fields, the FLAC-adapter ranges P-5 re-sniff / S-9 window / `:3983`), NEW `Playback/Playback.Glitch.cs`, NEW `Wavee.Tests/GlitchLedgerTests.cs` | §4.11 (S-1, H-11), §4.14 ledger, §4.17 adapter bits | 0 — step 4 (after D3/U8) |
| **0b Health card** | `Screens/Diagnostics.UI.cs` (`PlaybackHealthView` on `RuntimePageView`, by symbol), `assets/loc/{en-US,nl,ko-KR}.json` (`diagnostics.playback.*`) | §4.14 card (X4) | 0 — step 4 |
| **1a Threads + rings + device wait** | `[engine]Audio/AudioFeedThread.cs` (`EnterDecode` seam, clock priority + registration, ctor over `RingSizing`, `Wrap`/`WrapAdditional` pass `_rt` + sizing, `FeedOnce` incident-edge `GrowAhead`), `[engine]Audio/RingAudioSource.cs` (ctor `keepBehind/startFrames/rt`, `Produce` registration, `RtTryJump`, `ReadyWake`/`IsReady`, per-incident latch, `GrowAhead`, dispose), `[engine]Audio/PcmRing.cs`, `[win]Wasapi/MmcssProAudio.cs` (`EnterDecode`), `[win]Wasapi/WasapiPcm.cs` (`CreateBackend`), `[win]Wasapi/WasapiAudioDevice.cs` (`WaitForWritable` INFINITE), `[engine]Audio/AudioClock.cs` (`NullAudioSink.WaitForWritable`); tests `ThreadCharacteristicsTests`, `PcmRingKeepBehindTests`, `AudioBufferSizingTests` updates | §4.1, §4.2 | 1 — step 5, after flagship E |
| **1b Session** | `[engine]Audio/PcmAudioPlayer.cs` (by symbol: ctor `rt`/`ringSizing`; `PrepareCoreAsync` sizing/readiness/lease overload; `TryAcquireDecoderLease`; `RenderBlock` F2 + D7 order; `RenderSilence`; silence ledger + `SessionAudioClock` content clock + `RawPlayedFrames`/`DevicePaddingFrames`; `RtRenderOnce` H-3 + F5 + phase 4; `WaitAsync`/`UntilAsync`/`WaitAppliedAsync`/`WaitPhaseAsync`/`WaitRingReadyAsync`; `ReconcileEffects` → `CmdSetEq` + preamp + `CmdSetVoiceGain`, balance on RT; `DrainMixerCmds` new kinds; `TrySetVoice` clamp; H-13 delete; H-8; R-3; R-11; S-6), `[engine]Audio/DspStages.cs` (`EqStage` fixed capacity + `AdoptPending` + design helpers; `:103-234` only), `[engine]Audio/AudioGraphHost.cs` (`BuildVoiceChain` always `[Gain, Eq]`); tests `StarvationRecoveryTests`, `DeadSinkTests` (+ `FailingAudioSink` double), `EqStageCommandTests` | §4.3 | 1 — step 5, after flagship E |
| **1c Stress model + runner** | NEW `[engine]Audio/AudioStressModel.cs`, NEW `FluentGpu.Engine.Tests/AudioStressTests.cs`; app `Screens/Diagnostics.Probe.cs` (`--stress-audio`), NEW `Wavee.Tests/AudioStressTests.cs` | §4.18 | 1 — step 5, after flagship D0 |
| **2a Seek engine** | `[engine]Audio/PcmAudioPlayer.cs` (`SeekAsync` dispatcher, `TryJumpWithinRingAsync`, `SwapToPreparedAsync`, `SeekInPlaceAsync`, `RebaseAtSubmit`, `FadeActiveToSilenceAsync`/`SwapLanded`, `CmdJumpWithinRing`/`CmdSwapVoice`/`CmdFadeOutHold`, pooled fade LUTs, `NextEngineVoiceId`, `ThreadCharacteristics`), `[engine]Audio/CrossfadeMixer.cs` (`VoiceRef`, `Render` by ref, `JumpBlend` in `MixInto`, `GainEnvelope` RT-stampable start, `MixVoice` fields), `[engine]QueuePreparation.cs` (`IsReadyFor`) | §4.4 | 2 — step 6, after flagship U6; sequential after 1b |
| **2b Seek pump** | `Playback/Playback.Audio.cs` (mailbox, `SeekCoreAsync` A/B/fallback, `StaleTimerAsync`, `AfterSeekLanded`, Body ownership hand-over, `RingSource` `ownsBody`/`ProbeAt` routing/`ResidentOnly`, `IGainFolding` + `s_bakedFactor`, S-7 `Prime`/`Pad`, decoder pool rent after headers, index seed, `SilentSeekAsync` gen, `PostSignal` gen), `Playback/Playback.Audio.Vorbis.cs` (`SetupHash`), `Playback/Playback.Audio.Ogg.cs` (`Next.Hole` `:329/:371`, `PageIndex.Merge`), `Playback/Playback.Endgame.cs` | §4.8, §4.11 | 2 — step 6 |
| **2c Stream second reader + index** | `Spotify/Spotify.Audio.Stream.cs` (`Body.ProbeAt`/`ProbeRange`, view-only requests, S-10, S-11 behind + protected slots, landing-time index (grow-only, container offsets), `PageIndexSnapshot`) | §4.16 wave-2 items | 2 — step 6 |
| **2d Hold by generation + video gen** | `Shell/Shell.PlayerBar.UI.cs` (`OnReport` releases the hold only on a matching `LastSeekLandedGen`; `_committedGen` captured at commit), `Playback/Playback.Video.cs` (`Seek(ms, accurate, gen)`, gen on its posts), `Playback/Playback.Host.cs` (`Video.Seek` gen dispatch, `LastSeekLandedGen` publish) | §4.13 hold rule, V-PA2 video | 2 — step 6 |
| **2e Tests (wave 2)** | NEW `FluentGpu.Engine.Tests/SeekVoiceSwapTests.cs`; extend `Wavee.Tests/PlaybackReducerSeekTests.cs`, `AudioStreamTests.cs`, `AudioAdapterTests.cs` (interrupted landing, hole, setup hash) | §5 | 2 — step 6 |
| **3a Scrub CORE** | NEW `Playback/Playback.Scrub.cs`; NEW `Wavee.Tests/{ScrubModelTests, KeyboardScrubLadderTests}.cs` | §4.10 | 3 — step 7 |
| **3b Scrub engine** | `[engine]Audio/PcmAudioPlayer.cs` (`BeginScrubAsync`, `CancelScrubAsync`, `ReleaseHeldSilentAsync`, `CmdHoldVoice`/`CmdReleaseVoice`), `[engine]Audio/CrossfadeMixer.cs` (`Held`/`HoldAtFrame` in `MixInto`/`IsFinished`/`ReadableFrames`/`PcmReady`), NEW `[engine]Audio/ScrubGrain.cs`, NEW `FluentGpu.Engine.Tests/ScrubGrainTests.cs` | §4.5 | 3 — step 7; sequential after 2a |
| **3c Scrub SHELL** | NEW `Playback/Playback.Audio.Scrub.cs`, NEW `Wavee.Tests/ScrubGrainSourceTests.cs` | §4.12 | 3 — step 7 |
| **3d Scrub arms + UI + keyboard + video** | `Playback/Playback.cs` (scrub arms, `Scrub`/`ScrubPosMs` state, scrub slots in `Any`), `Playback/Playback.Host.cs` (scrub posts, `Execute` scrub dispatch BEFORE `Seek`, `ScrubPositionMs`), `Shell/Shell.PlayerBar.UI.cs` (scrub wiring, unmount cleanup, labels, rail tooltip, keyboard ladder + 50 ms tick), `Shell/Shell.PlayerBar.cs` (`PlayerKey` shift; delete `PlayerSeekAccumulator`), `Shell/Shell.PlayerBar.Podcast.UI.cs` (ladder replaces `s_barSeek`), `Playback/Playback.Video.cs` (`ScrubPreview`); tests `PodcastPlayerBarTests` rewrite, `ShellPlayerBarUiRulesTests` (`PlayerKey`) | §4.9 arms, §4.13 | 3 — step 7 |
| **4a Resampler** | NEW `[engine]Audio/PolyphaseResampler.cs` (with `ResampleResult`); DELETE `LinearResampler.cs`, `LinearResamplerTests.cs`; `[engine]Audio/AudioDecode.cs`; `Playback/Playback.Audio.cs` (`:1536`, `:1989`, `:3907` + EOF `Flush` at the three adapters); `Platform/Modules.Host.cs` (AAC `:2340, 2372, 2468`); NEW `PolyphaseResamplerTests.cs` | §4.6 | 4 — step 8 |
| **4b Limiter + ResampleStage** | `[engine]Audio/DspStages.cs` (`LimiterStage :320-382`; `ResampleStage :390-404` retargeted), `[engine]Audio/AudioGraph.cs` (`LimiterSpec.LookaheadMs`), `[engine]Audio/AudioGraphHost.cs` (`Compile`/`BuildStage` pass channels); NEW `LimiterLookaheadTests.cs` | §4.7 | 4 — step 8 |
| **4c Normalization modes + settings + default volume** | `Spotify/Spotify.Audio.cs`, `Spotify/Spotify.Audio.Stream.cs` (album bits in `Body`/`Observe`), `Playback/Playback.Audio.cs` (normalization region, `Opened` album fields, Vorbis comment RG parse, `Boot` lambda), `Playback/Playback.Audio.Flac.cs` (`:254-273` RG tags), `Platform/Platform.Settings.cs` (keys), `Platform/Platform.cs` (`:215` default), `Screens/Settings.cs`, `Screens/Settings.UI.Playback.cs`, `assets/loc/*.json`; extend `AudioAdapterTests`, `FlacTests` | §4.15 | 4 — step 8, after flagship D3; **sequential after 4a** (`Playback.Audio.cs`) |
| **4d Conformance + fuzz + MP3** | `Wavee.Tests/Fixtures/ogg/xiph/*` + README, `Wavee.Tests/Fixtures/mp3/sine-440.mp3` + README, NEW `VorbisFuzzTests.cs`, `Mp3DecoderTests.cs`; extend `VorbisTests.cs` | §5 | 4 — step 8 |
| **5 Docs** | `.claude/skills/wavee/audio-handoff.md`, `.claude/skills/wavee/palette-shortcuts.md` (the keyboard ladder), CHANGELOG ` (#167)` | — | 5 — step 9, after flagship U8 |

**Orchestrator verification (per wave; one Debug + Release build and one test run at the end of each wave — the skip-mid-iteration rule)**
1. `dotnet build Wavee.slnx` and `-c Release` clean; engine `dotnet build src/FluentGpu.slnx` Debug + Release and the VerticalSlice gate for waves 1–4.
2. `dotnet test` both repos, Debug and Release; grep that `RecoverStarvation`, `RecoveryFrames`, `LinearResampler`, `PlayerSeekAccumulator`, `EnterSustainedLowLatency`, `Task.Delay(2)` (in `PcmAudioPlayer.cs`/`RingAudioSource.cs`), `#TBD-playback` have zero references after their wave.
3. Wave 0/1 on-box: `--stress-audio --file <flac> --burners 2 --memory-mib 2048 --minimize --seconds 60` → `Incidents == 0`; the health card shows the MMCSS line as registered and the ring at `≈ 2000 / 2000 ms`; no `audio.glitch` during a one-hour album with a Release build compiling.
4. Wave 2 on-box: a logged-in track — drag anywhere inside the decoded span → no silence, `audio.seek.done latencyMs ≤ 30`; into fetched bytes → old audio continues until the swap, `≤ 80`; a far seek → old audio ≤ 80 ms then silence then the new position; seek storms land the last target only; the thumb never steps back; Connect owner: visual jump, one `seek_to`, no snap-back; the same in the flagship stage.
5. Wave 3 on-box: hold and drag → grains at −6 dB that stop after 150 ms of rest; release → the new position with no gap; Escape/unmount cancels and the track resumes where it was held; hold Left/Right → 5/15/30 s steps, one seek after the key goes idle; paused → visual-only.
6. Wave 4 on-box: a 44.1 kHz track on a 48 kHz device plays a 15 kHz sweep with no aliasing; a 50 Hz tone at 0.8 is untouched by the limiter; "proof" EQ limits without distortion; Quiet/Normal/Loud change the playing track within 50 ms; album mode keeps inter-track dynamics; a fresh profile plays at ≈ −6 dB.

---

## 7. Decisions (decided 2026-10-02) + critical files

| # | decision | chosen |
|---|---|---|
| D1 | first fixes | **H-1 (MMCSS "Audio" for the producer) + F2 (silence spans, content clock held, adaptive cushion, never Stop/Reset) + glitch telemetry (ledger, `audio.glitch`/`audio.session.summary`, `PlaybackHealthView` on `RuntimePageView`)** — waves 0–1 |
| D2 | hybrid-core laptops | **assumed**; F4 lands in wave 0 (0f), scoped to the audio leaf |
| D3 | seek design | **A** (second decoder on a non-owning byte-source view, one compound swap command) with the **single-decoder "seek marker" fallback** when no lease is free; the RT-decided in-ring jump (B) first |
| D4 | scrub defaults | **grain 60 ms, hop 30 ms, −6 dB (gain slot), self-park after 150 ms, keyboard 5/15/30 s committing on 250 ms idle** |
| D5 | normalization | **Quiet/Normal/Loud (−23/−14/−11) + album mode now** (Ogg header, lossless params, ReplayGain tags); the mode is applied once; Loud lifts the cap and the pre-volume limiter limits |
| D6 | first-launch volume | **`SavedVolume = 0.794f`** (−6.01 dB) and `UnmuteDefault = 0.794f` |
| D7 | limiter placement | **pre-volume**; `TapSpectrumBlock` stays immediately before `_masterGain` (post-EQ/limiter, pre-volume); post-limiter stages are attenuation-only (§2.6, `TrySetVoice` clamps) |
| D8 | decode rings | **2 s ahead + 1 s kept behind** via one `RingSizing` used by the feed and every prepared/seek voice |

### Critical Files for Implementation
- `C:\wavee\fluent-gpu\src\FluentGpu.Engine\Media\Playback\Audio\PcmAudioPlayer.cs` — by symbol: `RenderBlock`, `RtRenderOnce`, `RecoverStarvation` (deleted), `SeekAsync` (dispatcher), `SwapToPreparedAsync`/`SeekInPlaceAsync`/`TryJumpWithinRingAsync` (new), `ReconcileEffects`, `DrainMixerCmds`, `SessionAudioClock`, `PrepareCoreAsync` — one WP per wave (1b → 2a → 3b), after flagship E
- `…\Audio\AudioFeedThread.cs` (`IRtThreadCharacteristics`, `RingSizing`, `Start`, `ClockLoop`, `Wrap`), `RingAudioSource.cs`, `PcmRing.cs`, `CrossfadeMixer.cs`; `FluentGpu.Windows\Wasapi\{MmcssProAudio, WasapiPcm, WasapiAudioDevice}.cs`, new `PowerThrottling.cs`
- `C:\wavee\waveemusic\src\apps\Wavee\Playback\Playback.Audio.cs` (`Seek`, `SeekCoreAsync`, `Tick`, `DrainXruns`, the Vorbis adapter, `NormalizationFactor`, `Metrics`); new siblings `Playback.Glitch.cs`, `Playback.Scrub.cs`, `Playback.Audio.Scrub.cs`
- `…\Playback\Playback.cs` (`EmitSeek`, `DoSeek`, `Restart`, `DoAudio`, `DoMute`, `DoTick`, `Position`, `Input`, `Effects.Any`) and `Playback.Host.cs` (`Execute`, `Publish`)
- `…\Shell\Shell.PlayerBar.UI.cs` (`BarSeekRail`, labels, keys), `Shell.PlayerBar.cs` (`SeekRail`, `PlayerKey`), `Shell.PlayerBar.Podcast.UI.cs`
- `…\Spotify\Spotify.Audio.Stream.cs` (`Fetcher`, `Ring`, `Body` incl. `ProbeAt`) and `Spotify.Audio.cs`
- `…\Audio\DspStages.cs`, `Biquad.cs`, new `PolyphaseResampler.cs`, `ScrubGrain.cs`, `AudioStressModel.cs`; `…\Screens\Diagnostics.UI.cs` (`RuntimePageView`), `Platform\Platform.Settings.cs`

---

## 8. Verification log (Opus 5.5 verdicts → revision 2)

All 77 verdicts are **FIXED**; none is disputed. X1–X5 are applied. "Where" names the section; the line index at the end of this table maps sections to lines in this file.

| ID | Status | Change | Where |
|---|---|---|---|
| X1 | FIXED | §6 rewritten in the binding merge order; 0f gained `ProAudioRegistered`/`TargetAheadFrames`/`RingSizing`; 1a has no E gate on `AudioFeedThread.cs` content but is still sequenced after E | §6 |
| X2 | FIXED | §4.3 header states the actual flagship shape (`TapBlock` unchanged, new `TapSpectrumBlock` pre-`_masterGain`, content-domain window); F2 records silence spans instead of shifting `_deviceFrameOrigin`; `PlayedFrames` is the content clock; `DevicePaddingFrames`/`OutputDelayFrames` use raw played frames; "stamp with `ctx.StartFrame`" and "TapBlock pushes into the FFT ring" removed | §4.3, §2.6 |
| X3 | FIXED | new keys in `Platform/Platform.Settings.cs` | §4.15, §6 (4c) |
| X4 | FIXED | `PlaybackHealthView` on `RuntimePageView` with `UseInterval(1000f)` over `Metrics.Read()` and `Row` | §4.14, §6 (0b) |
| X5 | FIXED | scrub/keyboard/cleanup are component-local to `BarSeekRail` (hosted anywhere), unmount cleanup cancels, the stage routes the same keys | §4.13 |
| V-PE1 | FIXED | history kept inside the resampler; `Consumed = inFrames` when the input runs out, `clamp(nextCenter − 2H, 0, inFrames)` when dst fills; history rebuilt from the virtual buffer | §4.6 |
| V-PE2 | FIXED | rows built with `x = k − D + frac` (causal kernel applied to `x[i − k]`); ramp test added | §4.6, §5 |
| V-PE3 | FIXED | ring ctor seeds `_readFrames` from `startFrames`/`inner.PositionFrames`; `SwapToPreparedAsync` returns `StartPositionFrames`; the jump is RT-decided; promote removed; `RingAudioSource.cs` owned by 1a (wave 1) and untouched in wave 2 (the wave-2 needs are in `PcmAudioPlayer.cs`) | §4.2, §4.4, §6 |
| V-PE4 | FIXED | negative timeout → `INFINITE`/`WaitOne(-1)` in `WasapiAudioDevice.WaitForWritable` and `NullAudioSink`; `WasapiAudioDevice.cs` in 1a | §4.1, §6 |
| V-PE5 | FIXED | `RingSizing` on the backend, used by `AudioFeedThread` and `PrepareCoreAsync`; `:75-78` in 1b | §4.2, §4.3, §6 |
| V-PE6 | FIXED | silence ledger + `SilencePlayedBefore`; no origin shift | §4.3 |
| V-PE7 | FIXED | post-flagship shape restated (a–h); zero-alloc pin renamed `TapSpectrumBlock`; the silence path feeds `TapBlock` only — stated choice | §4.3, §5 |
| V-PE8 | FIXED | dead-sink handling moved before the phase gate in `RtRenderOnce`: drop pending, `RecordSinkFailure`, `Stop`, phase 3 | §4.3 |
| V-PE9 | FIXED | manual-reset events, `UntilAsync` Reset→check→wait with 20 ms recheck, never success on timeout; registration unregistered in `finally` | §4.3 |
| V-PE10 | FIXED | `ReadyWake` manual-reset with `IsReady(min)` = `!HasPendingFlush && (Buffered ≥ min || ProducerDone)`; seek paths arm suppression; doubling skipped under `_seekRebufferActive`; disposed with the ring | §4.2, §4.3 |
| V-PE11 | FIXED | `_maxHead`/`_floor`; `BehindFloats = head − max(floor, maxHead − keepBehind)` ≥ 0; reset on flush/clear | §4.2 |
| V-PE12 | FIXED | absolute target posted; RT computes/checks delta, reads `[head, head+fade)`, jumps `delta − fade`, resets WSOLA, publishes accept/refuse; blend state in the mixer slot by ref (`VoiceRef`, `Render` by ref) | §4.4, §4.5 |
| V-PE13 | FIXED | `PrepareAtAsync(…, lease, ct)` overload; the lease rides the `DecoderLease` chain and is disposed on every failure path | §4.3, §4.11 |
| V-PE14 | FIXED | 80 ms stale timer runs in the pump from the request (`StaleTimerAsync`); seek readiness `rate/100` set explicitly | §4.3, §4.11 |
| V-PE15 | FIXED | `CmdSwapVoice` compound command resolves "next block" on the RT and installs both envelopes in one step; enqueue results checked, rollback retires the ring | §4.4 |
| V-PE16 | FIXED | `IMediaSession.SeekAsync` kept as the dispatcher (direct-decoder branch → B → fallback) | §4.4 |
| V-PE17 | FIXED | `SeekInPlaceAsync` waits for phase 4 (fade END); hold = phase 4 at gain 0 rendering through F2; phase 3 untouched | §4.4 |
| V-PE18 | FIXED | `HoldAtFrame` checked before retirement (`IsFinished` false while held); release + fade-in is one `CmdReleaseVoice`; grain voices self-retire via their Out envelope | §4.5 |
| V-PE19 | FIXED | scrub gain through the gain slot; normalization scalar untouched; promote removed (V-PA5); cancel rebases; `MixerCmd.Linear` float | §4.5 |
| V-PE20 | FIXED | 4-point half-sample inter-sample estimate; `need` over L+1 incl. the leaving frame before storing; channel-sized delay; deque-min | §4.7 |
| V-PE21 | FIXED | by the V-PE1 model; `Flush` loops `Process` over D zero frames; indexing never negative | §4.6 |
| V-PE22 | FIXED | Taps 64, cutoff `0.5·min(fs)/fs_in`, β 9; `InterpPhases + 1` rows; general-ratio test 44056→48000 | §4.6, §5 |
| V-PE23 | FIXED | `ResampleResult` moves to the new file; `ResampleStage` retargeted (4b); `Modules.Host.cs` in 4a | §4.6, §6 |
| V-PE24 | FIXED | `WasapiPcm.cs` moved to 1a | §6 |
| V-PE25 | FIXED | one gain slot per voice; `CmdSetEq` carries the preamp, recomputed on every band change; `LimiterStage.DbToLinear` reference | §4.3, §4.7 |
| V-PE26 | FIXED | engine ids from 1,000,000 (`NextEngineVoiceId`); the app's scrub voice takes its id from the session | §4.4, §4.12 |
| V-PE27 | FIXED | `partial` class; `volatile` fields instead of `Volatile.Read` on byte enums; `Math.Abs` on long/double; no unused `_mixRate`/`_stepDen`; complete `LimiterStage`; `ActiveRing`/`VoiceRef`/`SwapLanded`/`FadeActiveToSilenceAsync`/`SetVoiceGain`/command constants defined; `AudioStressModel` namespace/usings | §4.3, §4.4, §4.7, §4.18 |
| V-PE28 | FIXED | one `EXECUTION_SPEED` call (the 0x4 flag dropped as moot); scoped to the audio leaf (`PowerThrottling` in `FluentGpu.Windows/Wasapi`, called from `CreateBackend`) | §4.1 |
| V-PE29 | FIXED | the RT evaluates `_mixer.PcmReady(StartupReadinessFrames)` itself; `_prefillPassed` removed | §4.3 |
| V-PE30 | FIXED | resume only in phase 0; silence path inside the tripwire; one xrun per incident (ring latch); `GrowAhead` once per incident edge; `_resumeFrames` halves after 30 s | §4.2, §4.3 |
| V-PE31 | FIXED | voice chain always `[Gain, Eq]` (E-6 possible); `AdoptPending` commits a pending cascade first; failed enqueue retried via `_eqDirty`; flush updates `_y1`; `BiquadCoeffs` double | §4.3, §4.7 |
| V-PE32 | FIXED | `RebaseAtSubmit` anchors at the swap block's submit index (device played = submit − origin); "block-exact" | §4.4 |
| V-PE33 | FIXED | never proceeds without a lease; 10 s wait then a typed error listing `RingsSnapshot` producers | §4.3 |
| V-PE34 | FIXED | device modelled; scenarios 2.5 s absence and 0.8×→1.5×; the model also drives the real session; power-complementary assertion; `OverlapAdd` within 1e-6; `EnterDecode` via the interface; `FailingAudioSink` double; instance seam, no static | §4.18, §5 |
| V-PE35 | FIXED | `Playback.Audio.cs` 4a → 4c sequential; `RingAudioSource.cs` owned by 1a; `WasapiAudioDevice.cs` in 1a | §6 |
| V-PE36 | FIXED | `LatencySamples = 0`; D output frames discarded at stream start; `Flush` tail kept | §4.6 |
| V-PA1 | FIXED | scrub slots (and `PersistVolume`, `PrepareLost`) in `Effects.Any`; `ScrubCommit` deleted; `ScrubPark` not declared (the source parks itself — V-PA23) | §4.9 |
| V-PA2 | FIXED | `EmitSeek` used by `DoSeek` and `Restart`; gen threaded through Video and Silent; the pump seeded from every load (`Load(…, seekGen)`) | §4.9, §4.11, §4.13 |
| V-PA3 | FIXED | lease handed to `PrepareAtAsync`; A path disposes the item/lease on every exception or cancel; the scrub lease dies in `ScrubGrainSource.Dispose` | §4.11, §4.12 |
| V-PA4 | FIXED | `RingSource.ReleaseOwnership`/`TakeOwnership` under `s_gate` before the swap; `s_bytes`, `s_activeDecoder`, `s_activePrimaryId` repointed; no promote path | §4.11 |
| V-PA5 | FIXED | `ScrubEnd` arm emits `fx.ScrubEnd` instead of `fx.Seek`; `Execute` dispatches scrub before `Seek`; promote replaced by a design-A swap at release; non-lease path releases Held without fade-in and seeks in place | §4.9, §4.12 |
| V-PA6 | FIXED | `Committing`/`Landed`/`Generation` deleted; `Up` → Idle | §4.10 |
| V-PA7 | FIXED | scrub arms moved to 3d; `PlayerSeekAccumulator` deletion + Podcast files + `PodcastPlayerBarTests` in 3d; `HoldsDrop`/`CommitHoldMs` kept (`ShellPlayerBarUiRulesTests` stays green); `ProAudioRegistered`/`TargetAheadFrames` in 0f; `AudioAdapterTests` kept compiling via the 2-arg `NormalizationFactor` and extended in 4c | §6, §4.15 |
| V-PA8 | FIXED | `Playback.Audio.cs` single owner per wave (0a; 2b; 4a then 4c); `Ogg.cs` `:329/:371` + `Merge` in 2b; `Playback.Video.cs` in 2d and 3d | §6 |
| V-PA9 | FIXED | velocity passed unchanged (dimensionless) | §4.12, §4.10 |
| V-PA10 | FIXED | `RemoteSeekAtMs` (frame clock) + `RemoteSeekTargetMs`; hold while `now − at < 2000 && |proj − target| > 1500`; cleared on tolerance, expiry or `ClusterAck` | §4.9 |
| V-PA11 | FIXED | volume persisted from the reducer's `DoTick` via `fx.PersistVolume` (UI drain), flushed on shutdown | §4.9 |
| V-PA12 | FIXED | `HoldsDrop` kept; released by a matching-generation landing or the timeout | §4.13 |
| V-PA13 | FIXED | album bits on `Body` (open + `Observe`) and `INormalizationSource`; four figures passed; `Stream.cs` in 4c | §4.15, §6 |
| V-PA14 | FIXED | lossless `NormalizationGain` stays at −14 with no mode; the mode is applied once in `NormalizationFactor` | §4.15 |
| V-PA15 | FIXED | `IGainFolding.AppliedGainLinear`; baked factor tracked per voice id (live, join, prepared, swapped); each voice ramps by `now/baked` | §4.11, §4.15 |
| V-PA16 | FIXED | `_needsLanding` latch; `Read` serves silence until the landing re-read succeeds, then `PeekLanding`/`Prime`/clock | §4.11 |
| V-PA17 | FIXED | explicit per-count table with the RFC 9639 §9.1.3 orders (5/7/8 corrected), BC 0.5 per side | §4.17 |
| V-PA18 | FIXED | tags parsed in both adapters' `TryOpen`; `GainLinear(g + 4, p, ag + 4, ap)` | §4.15 |
| V-PA19 | FIXED | `InterruptPendingRead` only on the fallback path | §4.11 |
| V-PA20 | FIXED | `Body.ProbeAt`/`ProbeRange` for the non-owning view (no cursor/epoch moves); `ResumeFrom` no-op until owned; scrub recentres resident-only else parks; `Stream.cs` in 2c (`ProbeAt`) with 2b/3c consuming it | §4.16, §4.12, §6 |
| V-PA21 | FIXED | = V-PE3 | §4.4 |
| V-PA22 | FIXED | ladder commits from a 50 ms `UseInterval` tick after 250 ms idle; ladder lives in the bar component; `PlayerKey` takes `modified` and `shift` separately | §4.10, §4.13 |
| V-PA23 | FIXED | `ScrubGrainSource` parks itself after 150 ms without `Retarget`; `ScrubTick`/`ScrubPark` removed from the reducer | §4.12, §4.9 |
| V-PA24 | FIXED | mount-once `UseEffect` cleanup cancels a live scrub; `ScrubCancel` idempotent | §4.13, §4.12 |
| V-PA25 | FIXED | `ScrubBegin` wrapped in try/catch (lease released, logged, never a fault); no `g.Dispose()` — the ring retires it | §4.12 |
| V-PA26 | FIXED | = X4 | §4.14 |
| V-PA27 | FIXED | `s.Kind != PlayableKind.Video`; `SeekTarget.Clamp` with ints; `WaveeLogLevel.Warning`, `null` ex, `WaveeLogField.Of`; `HopFrames` declared; the bogus cite replaced by `Playback.cs:1547-1550`; `ApplyNormalization` → `Action?` with `Boot`'s lambda | §4.9, §4.12, §4.13, §4.14, §4.15 |
| V-PA28 | FIXED | mailbox packs epoch + ms, `Interlocked.Exchange`, dropped when stale, cleared in `Load`; `s_seekPrepare` via `Volatile` | §4.11 |
| V-PA29 | FIXED | `_lastGrain = startFrame`; `dst` length guarded; `_carry` cleared on park | §4.12, §4.5 |
| V-PA30 | FIXED | recentre reads resident bytes only (`ResidentOnly`), interruptible; otherwise park | §4.12, §4.16 |
| V-PA31 | FIXED | `GlitchLedger` is `System`-only over primitives, one lock, snapshot reads | §4.14 |
| V-PA32 | FIXED | `State.SinkMuted` drives `MuteOn == false` only when muted; `UnmuteDefault = 0.794f` | §4.9 |
| V-PA33 | FIXED | `InputKind.PrepareLost` + `Input.PrepareLost(epoch)` + arm; dropped when stale; posted only when a join was abandoned | §4.9, §4.11 |
| V-PA34 | FIXED | `s_normMode` declared and seeded; all text via loc keys (card values included) with nl/ko; keys in `Platform.Settings.cs`; distinct glyphs | §4.14, §4.15 |
| V-PA35 | FIXED | decoder pool separate from the window pool; rent by hash after the headers are read | §4.8 |
| V-PA36 | FIXED | `GrowRequested` polled flag; container offsets; grow-only array + published count; FLAC `_seekPoints` kept separate; the CTS passed into `FetchRangeAsync` | §4.16 |
| V-PA37 | FIXED | P-5 re-reads at `10 + size (+10)`; P-1 is the `long` path (no `Unsupported`); P-12 dropped as unverified | §4.17 |
| V-PA38 | FIXED | MP3 fixture added; the 64 KiB check is an assertion; 3 s reference convention kept; `RenderFloor0` pure seam; "zero allocations on reopen" replaces "≥ 10× faster" | §5 |
| V-PA39 | FIXED | the on-box gate runs through the real device (`--file`/`--track`); `--fake` runs are labelled `smoke` | §4.18, §5 |
| V-PA40 | FIXED | tooltip on the rail container | §4.13 |
| V-PA41 | FIXED | scrub while paused is visual-only; stage keyboard per X5; shortcut table path `.claude/skills/wavee/palette-shortcuts.md` | §4.9, §4.12, §6 (WP 5) |

**Section → line index (this file):**

| Section | Line |
|---|---|
| 0. The owner's ask, restated as requirements | 9 |
| 1. Current behaviour map (by audit finding) | 24 |
| 1.1 Continuity chain (H-1, H-2, H-3, H-5…H-13) | 26 |
| 1.2 Seeking (S-1…S-13, U-1…U-3, U-5, U-8, U-11) | 37 |
| 1.3 Scrubbing surfaces | 45 |
| 1.4 Normalization, EQ, volume (N-1…N-3, E-1…E-9, V-1, V-2, U-4) | 51 |
| 1.5 Resampler, decoder, MP3 | 57 |
| 1.6 Engine capabilities relied on (verified) | 62 |
| 2. Model and thresholds | 80 |
| 2.1 Threads after (all MMCSS-protected or irrelevant to continuity) | 82 |
| 2.2 Buffers after (48 kHz) | 91 |
| 2.3 Starvation (F2) — numbers | 105 |
| 2.4 Seek (D3) | 115 |
| 2.5 Scrub (D4) | 128 |
| 2.6 Volume and limiter (D6, D7) — the post-volume chain is attenuation-only (re-verified) | 138 |
| 2.7 Normalization (D5) | 141 |
| 3. State machines and data flow | 153 |
| 3.1 Threads and priorities (after) | 155 |
| 3.2 Starvation recovery (F2) | 164 |
| 3.3 Seek voice swap (design A) and fallback | 172 |
| 3.4 Scrub grains | 183 |
| 4. Real code | 194 |
| 4.1 Engine — thread characteristics (F1) and power throttling (F4) | 196 |
| 4.2 Engine — the ring: keep-behind, skip, rewind (D8, seek B) | 301 |
| 4.3 Engine — `PcmAudioSession` (anchored by symbol on the post-flagship tree — X2) | 397 |
| 4.4 Engine — seek: `IMediaSession.SeekAsync` kept as the dispatcher; `TryJumpWithinRing`, `SwapToPreparedAsync`, `SeekInPlaceAsync` (D3) | 567 |
| 4.5 Engine — voice chain gain slot, `MixVoice.Held`, scrub API (D4) | 693 |
| 4.6 Engine — the polyphase windowed-sinc resampler (Q-1; V-PE1, V-PE2, V-PE21, V-PE22, V-PE23, V-PE36) | 729 |
| 4.7 Engine — limiter lookahead and true-peak (E-3; V-PE20), preamp (E-4; V-PE25), denormals (E-2), double biquad (E-1; V-PE31) | 855 |
| 4.8 App — the Vorbis setup-parse cache (V-PA35) | 914 |
| 4.9 App — reducer (`[app]Playback/Playback.cs`) | 918 |
| 4.10 App — `[app]Playback/Playback.Scrub.cs` (NEW, CORE) | 1015 |
| 4.11 App — the pump (`[app]Playback/Playback.Audio.cs`): mailbox, design A + fallback, ownership hand-over, S-1, S-5, tick latch | 1068 |
| 4.12 App — `[app]Playback/Playback.Audio.Scrub.cs` (NEW, SHELL) | 1163 |
| 4.13 App — seek bar + keyboard + Connect + video (`Shell/Shell.PlayerBar.UI.cs`, `Shell/Shell.PlayerBar.cs`, `Shell/Shell.PlayerBar.Podcast.UI.cs`, `Playback/Playback.Video.cs`) — hosted in the bar, the flagship stage and the video overlay alike (X5) | 1284 |
| 4.14 App — glitch telemetry and the Diagnostics card (D1; V-PA26/X4, V-PA31) | 1292 |
| 4.15 App — normalization modes, album mode, settings, default volume (D5, D6; V-PA13, V-PA14, V-PA15, V-PA18, V-PA34) | 1363 |
| 4.16 App — stream layer (`[app]Spotify/Spotify.Audio.Stream.cs`; V-PA19, V-PA20, V-PA36) | 1425 |
| 4.17 Codec fixes (P-1, P-2, P-4, P-5, P-8, S-9; V-PA17, V-PA37) | 1438 |
| 4.18 Load-test harness (D1 verification; V-PE34, V-PA39) | 1448 |
| 5. Tests (pure classes and the deterministic engine harness; no source text) | 1456 |
| 6. Fixed API contract + merge order (X1) + work packages | 1495 |
| 7. Decisions (decided 2026-10-02) + critical files | 1551 |
| Critical Files for Implementation | 1564 |
| 8. Verification log (Opus 5.5 verdicts → revision 2) | 1575 |
