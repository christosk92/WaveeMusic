# Gapless playback — diagnosis

Status: **FIXED** — see “As fixed” at the end. Wave-0 Agent B. Code-anchored findings for the four known gapless killers. PlayPlay decrypt is treated as an opaque body-supplier; those trees were not read.

**Default user path is the broken 0 ms path.** `WaveeSettings.CrossfadeEnabled` defaults to `false` (`src/apps/Wavee/Platform/AppSettings.cs:152`). `SetCrossfade` then stores `_crossfadeEnabled = enabled && _crossfadeMs > 0` (`FluentMediaAudioHost.cs:481–482`). A listener who never opens Playback settings never reaches `CommitCrossfade`.

---

## Pipeline summary (decode → mixer → WASAPI)

```
CDN / local / RSS bytes
  → SpotifyAudioStream / PlainHttp / LocalFile   (app fetch + in-proc AES-CTR)
  → SpotifyMediaByteSource.OpenDecodeStream      (SkipStream past Spotify 0xA7 / Ogg header)
  → SpotifyEngineAudioDecoder                    (NVorbis / FlacBox / NLayer → f32)
       └─ LinearResampler into the FIXED mix format (device rate, stereo)
  → DecoderAudioSource                           (engine voice; Gapless copied from decoder)
  → PcmAudioSession / CrossfadeMixer             (voices + EQ + limiter)
  → AudioFeedThread (MMCSS Pro Audio)            (decode↔RT ring; xrun → silence)
  → WasapiAudioDevice                            (shared-mode IAudioClient, 100 ms buffer)
```

| Stage | Owner | File:line |
|---|---|---|
| Stack composition | `AudioPlaybackStack` builds `FluentMediaAudioHost` and applies persisted DSP | `AudioPlaybackStack.cs:54–71` |
| Host seam | `IAudioHost` + `IPreparedAudioHost` + `IAudioDspControl` | `Backend/AudioHost.cs:124–167` |
| Decode edge | `SpotifyEngineAudioDecoder` — Vorbis default, FLAC, MP3; **no AAC** | `FluentMediaAudioHost.cs:62–63, 111–165, 1134–1138` |
| Mix format | `PcmAudioPlayer.OpenAsync` opens the endpoint first and binds the decoder to `endpoint.Sink.Format` | `PcmAudioPlayer.cs:100–141` |
| Device leaf | `WasapiPcm.CreateBackend` probes shared-mode mix format; one `WasapiAudioDevice` **per session** | `WasapiPcm.cs:17–58` |
| WASAPI open | `AUDCLNT_SHAREMODE_SHARED`, event-driven, `hnsBuffer = 100 ms` | `WasapiAudioDevice.cs:287–301` |
| Prepare-next | Controller `SchedulePreparedNext` → `PrepareNextAsync` / `SupplyNextBodyAsync` | `PlaybackController.cs:2205–2296`, `FluentMediaAudioHost.cs:882–938` |
| Hand-off | 200 ms `Tick` → `CommitCrossfade` **only if** `_crossfadeMs > 0` | `FluentMediaAudioHost.cs:1079–1086, 987–1036` |
| Natural end | Mixer drained → `PlaybackState.Ended` → `AutoAdvanceAsync` → `LocalNextAsync` → **new** `LoadAndPlayCurrentAsync` | `PcmAudioPlayer.cs:679–686`, `PlaybackController.cs:2411–2414, 1569–1580, 1726–1816` |

The engine already has a sample-clock `VoiceScheduler` with a real gapless butt-join (`TransitionOutcome.Gapless`, overlap 0, `GainEnvelope.Constant`) at `VoiceScheduler.cs:268–281`. **Wavee does not use it.** `_effects.CrossfadeMs` is published (`FluentMediaAudioHost.cs:483–484`) but no `QueuePlaybackCoordinator` is wired.

---

## 1. Prepare-next scheduling

**Verdict: UNCERTAIN (needs runtime measurement)** for “does the chain finish before the boundary on a short track / seek-to-end.” Routing of UserQueue and autoplay through prepared-next is **VERIFIED-OK**. Consuming that prepared voice on the default 0 ms path is **BROKEN** (see killer 4).

### Trigger — not remaining-ms

`PrepareNextAsync` is **not** armed from a time-remaining threshold. The controller calls `SchedulePreparedNext` when the session identity changes:

| Reason | Call site |
|---|---|
| `"after-start"` | `LoadAndPlayCurrentAsync` after `LoadFastStart` / `Load` — `PlaybackController.cs:1799, 1815` |
| `"session-changed"` | `EmitSnap` (queue edit, continuation apply, …) — `2164` |
| `"after-handoff"` | `CommitPreparedTransitionAsync` after a `Started` transition — `2398` |
| `"transition-missed"` | host reported `AudioTransitionKind.Missed` — `2348` |

There is no `PositionMs` / remaining-ms predicate. The engine’s `VoiceScheduler.NeedsPrepare` (`clock ≥ EndingSoonFrame`, `VoiceScheduler.cs:172–175`) is unused.

`SchedulePreparedNext` snapshots `_session.PreviewNext()` and fire-and-forgets `ResolvePreparedNextAsync` (`2205–2258`).

### Timing walk

**Short track (<30 s).** Prepare starts at track start, so the budget is the *full* duration, not a late window. That is better than an ending-soon trigger *if* the chain finishes. The chain is:

1. `ResolveFastAsync` — clear head (≤80 KiB, `HeadFileClient.cs:16`) + parallel body task (CDN + key/PlayPlay).
2. `PrepareNextAsync` — enqueued on the host `_tail` pump (`FluentMediaAudioHost.cs:496–505, 882–891`). `Backend.PrepareAsync` `Task.Run`s `decoder.TryOpen` (header parse + resampler arm) — `PcmAudioPlayer.cs:30–47`.
3. `await pendingBody` — key + CDN; **serialized behind** step 2 on the controller task (`PlaybackController.cs:2290–2296`).
4. `SupplyNextBodyAsync` — again on `_tail`; attaches the encrypted body to the already-opened head stream (`FluentMediaAudioHost.cs:918–937`).

Decoder “prime” is **TryOpen succeeded**, not “first PCM sitting in the RT ring.” `AudioPreparedItem.IsReady` is hardcoded `true` at construction (`QueuePreparation.cs:70–86`). A cold AP-key / PlayPlay derive that exceeds the short track’s length still misses. **Needs a listening pass** (`[gapless] arm` / `prepare-primed` / `next-body` timestamps).

**Seek to near end.** `SeekAsync` → `EmitSeeked` → `_currentHost.Seek` (`PlaybackController.cs:686–687, 1711–1716`). The host seek is `_session.SeekAsync` on `_tail` (`FluentMediaAudioHost.cs:440–443`). It does **not** cancel or re-schedule prepare (unlike `VoiceScheduler.Invalidate`, `VoiceScheduler.cs:211–223`). If prepare already completed at track start, the slot is still there. The 200 ms `Tick` is the next decision:

- `_crossfadeMs > 0` and primed → `CommitCrossfade` on the next tick (up to ~200 ms late).
- `_crossfadeMs == 0` (default) → no commit; wait for `Ended` → hard-cut reload. Seek-to-end has **no** extra prepare time and does not need it — the prepared voice is simply thrown away.

### Await chains vs playback

`ResolvePreparedNextAsync` is **not** on the RT feed thread. It *does* serialize with other host ops on `_tail` (`Load`, `SupplyBody`, `Seek`, `SoftReloadAsync`, `PrepareNext`, `SupplyNextBody`). A mid-track device-rate soft reload can delay `PrepareNextCoreAsync` / body attach. The RT mixer keeps playing the current session during that wait.

`FastStartBodySupplyGrace` (250 ms, `PlaybackController.cs:134, 426–438`) delays **current-track** body supply so the clear head can queue first PCM. It does not apply to prepared-next.

### UserQueue and autoplay

`PlaybackSession.PreviewNext` (`PlaybackSession.cs:78–108`) order:

1. Repeat-track → current item (and `allowOverlap` is forced false when `Repeat == Track`, `PlaybackController.cs:2219`).
2. **User-queue head** (first playable).
3. Context rows after the cursor, including the **autoplay tail** (same `_context` list; `QueueProvider` marks them).
4. Repeat-context wrap.
5. Pause delimiter → `null` (no prepare across a pause).

So UserQueue and autoplay **do** route through prepared-next. `PreparedTransitionTests.QueueEdit_CancelsOldIdentity_AndOnlyNewTokenCanAdvance` covers a play-next insert.

Caveats:

- Autoplay *prefetch* is count-keyed, not time-keyed: `MaybeStartContinuationFetch` skips while `RemainingInContext > 5` (`PlaybackController.cs:1864–1870`). The last page of a context can therefore have no next item to prepare until the continuation lands; `EmitSnap` then re-schedules. If that resolve finishes after `Ended`, the boundary is a hard cut.
- `MediaSwitchLogic.AllowCrossfade` is **Audio→Audio only** (`MediaSwitchLogic.cs:52–53`). Local-file↔audio and any video boundary refuse overlap (`allowOverlap = false`, `PlaybackController.cs:2219–2221`). Episodes prepare but never overlap (`PreparedTransitionTests.EpisodesPrepareGaplessButNeverRequestOverlap`).
- Manual Next / row-click does **not** promote the prepared slot: `IPreparedAudioHost` docs say so (`AudioHost.cs:157–160`). `LocalNextAsync` always `LoadAndPlayCurrentAsync`.

---

## 2. Format changes (44.1 k → 48 k)

**Verdict: VERIFIED-OK** for consecutive tracks *inside one live session* — the mixer graph is fixed-rate; the decoder resamples. **BROKEN** as a *boundary* property on the default 0 ms path, because that path opens a **new** `IAudioClient` every track (killer 4), not because the codec rate changed.

### Fixed mix + resample (same session)

`PcmAudioPlayer.OpenAsync` (`PcmAudioPlayer.cs:100–104`):

> Open the endpoint FIRST and adopt the rate the hardware actually clocks at … Binding the decode/graph target to `mix` keeps `decoder.targetRate == device rate`.

`SpotifyEngineAudioDecoder.TryOpen` (`FluentMediaAudioHost.cs:147–149`):

```
_resampler = srcRate != target.SampleRate ? new LinearResampler(srcRate, target.SampleRate, target.Channels) : null;
```

`PrepareNextCoreAsync` targets `session.Format` (`FluentMediaAudioHost.cs:903–904`). A 44.1 k Vorbis track followed by a 48 k track (or the reverse) is resampled into the same stereo mix. The WASAPI client is **not** re-`Initialize`d for a codec-rate change.

`WasapiFormatNegotiation` (`WasapiFormatNegotiation.cs:12–20`): one internal mix format; shared mode matches the device rate; layout is fixed stereo.

### When the device *does* reopen (audible gap by construction)

| Event | What happens |
|---|---|
| New `Load` / `OpenSessionAsync` | `endpointFactory: fmt => new WasapiAudioDevice(fmt)` (`WasapiPcm.cs:42`). Fresh `Activate` + `IAudioClient.Initialize` + `Start`. This is the 0 ms / miss / manual-next path. |
| Default-endpoint change | `AudioDeviceController.OnDefaultDeviceChanged` → `PcmAudioSession.RebuildSink` (`AudioDeviceController.cs:78–88`, `PcmAudioPlayer.cs:799–838`). **Stop old sink, swap, Start new.** Sources/position survive; a rate change then `DeviceFormatChanged` → `SoftReloadAsync` (another session open). |
| Same-endpoint control-panel rate change | No WASAPI notification; self-corrects on the next load (`FluentMediaAudioHost.cs:661–662`). |

`RebuildSink` explicitly `oldSink.Stop()` then `EnsureStarted()` on the new client (`PcmAudioPlayer.cs:808–837`). That is a device reopen.

### Shared vs exclusive, buffer

- **Shared mode only:** `client->Initialize(AUDCLNT_SHAREMODE_SHARED, StreamFlagsEventCallback, hnsBuffer, 0, mix, null)` with `hnsBuffer = 100 * 10_000` (100 ms). Periodicity is 0. (`WasapiAudioDevice.cs:287–290`)
- `GetBufferSize` → `_bufferFrames` (`300–301`). Typical shared period ~10 ms; Write waits on the event when the 100 ms buffer is full (`124–133`).
- Exclusive mode is not implemented.

A 44.1→48 k pair that *crossfades* in one session is format-safe. The same pair on the default (crossfade off) path pays a full client bring-up regardless of rates.

---

## 3. Decode pre-roll / trim

**Verdict: BROKEN.** Encoder delay / end padding are not applied on the live Wavee path. The engine has `TrimmingSource` and `GaplessInfo`; the Spotify decoder reports `GaplessInfo.None` and production `Open`/`Prepare` never wrap the trim decorator.

### Codecs in use

`KindOf` (`FluentMediaAudioHost.cs:1134–1138`):

| `AudioFormat` | Decoder |
|---|---|
| `OggVorbis96/160/320` (default) | `VorbisSampleSource` → vendored NVorbis |
| `Flac` / `Flac24` | `FlacSampleSource` → FlacBox |
| `Mp3` | `Mp3SampleSource` → NLayer (external RSS / local) |

No AAC leaf. Spotify CDN music is Ogg Vorbis. Container skip is `DetectSkipOffset` (Spotify 0xA7 vs raw `OggS`/`fLaC`) — `FluentMediaAudioHost.cs:1156–1165` — **not** encoder-delay trim.

### Declared gapless info

```csharp
public GaplessInfo Gapless => GaplessInfo.None;   // FluentMediaAudioHost.cs:128
```

`PcmAudioPlayer.PrepareAsync` / `OpenAsync` wrap `new DecoderAudioSource(decoder, loudness)` and pass `decoder.Gapless` through (`PcmAudioPlayer.cs:45–47, 132–140`). `DecoderAudioSource` copies it (`AudioDecode.cs:370`) and **reads every sample the decoder emits**.

`TrimmingSource` (`AudioSources.cs:135–191`) skips `LeadInFrames` and stops `TrailPadFrames` early. It is used in engine tests (`AudioGraphTests`, `VoiceSchedulerTests`) and **not** in `PcmAudioPlayer.OpenAsync` / `PrepareAsync`.

### What NVorbis actually does

NVorbis overlap-adds Vorbis windows and, “per the spec, do not decode more samples than the last granulePosition” (`StreamDecoder.cs:582–583`). That is **last-page EOS padding**, not a Wavee-owned `TrailPadFrames`. There is no Opus-style pre-skip field on Vorbis; Spotify gapless historically depends on granule accounting plus (sometimes) explicit encoder delay. Wavee never reads such a field into `GaplessInfo`.

FlacBox / NLayer: no delay/padding trim in `SampleSource.cs`.

**PCM enters the mixer** at `DecoderAudioSource.Read` → (RT) `RingAudioSource` → `CrossfadeMixer.Render` → `WasapiAudioDevice.Write`. Leading priming samples of track N+1 are mixed as audio. Gapless is impossible even with perfect scheduling until lead-in / trail-pad are applied.

Whether NVorbis granule EOS is “close enough” on Spotify files is **UNCERTAIN** without a PCM dump; the contract is still broken.

---

## 4. The 0 ms path

**Verdict: BROKEN.** `_crossfadeMs == 0` does **not** take a sample-continuous hand-off. It never calls `CommitCrossfade`. The stream stops (session dispose) and a new WASAPI client starts.

### The comment vs the branch

`SetCrossfade` (`FluentMediaAudioHost.cs:479–484`):

```csharp
_crossfadeMs = Math.Clamp(durationMs, 0, MaxCrossfadeMs);
_crossfadeEnabled = enabled && _crossfadeMs > 0;
// Publish to the engine effects surface … 0 == gapless.
_effects.CrossfadeMs.Value = _crossfadeEnabled ? _crossfadeMs : 0f;
```

The comment claims `0 == gapless`. The Tick commit predicate requires the opposite (`1079–1082`):

```csharp
&& _prepItem is { IsReady: true } item && _prepOverlap && _crossfadeMs > 0 && _activeDurMs > 0
```

So 0 ms (or crossfade disabled) **skips** the mixer hand-off. The prepared voice sits unused until `DisposeSessionAsync` → `DisposePreparedSlotAsync` (`840–877`).

### What `CommitCrossfade` would do at 0 ms (it is never called)

```csharp
int fadeFrames = _crossfadeMs * sess.Format.SampleRate / 1000;   // 0
sess.AddCrossfadeVoice(..., GainEnvelope.Fade(FadeKind.In, start, fadeFrames, ...), ...);
sess.SetVoiceEnvelope(_activePrimaryId, GainEnvelope.Fade(FadeKind.Out, start, fadeFrames, ...));
```

`GainEnvelope.Fade` with `fadeFrames <= 0` returns `Constant` (full gain) — `CrossfadeMixer.cs:77`. That is **two voices at unity**, not a butt-join. The engine’s real 0-overlap path is `VoiceScheduler.Commit` (`268–281`): add B at `joinEnd` with `GainEnvelope.Constant`, do **not** fade A out early, never truncate A.

### What actually happens at 0 ms

1. Track A plays until the mixer is drained (`PcmAudioPlayer.cs:681–686` → `PlaybackState.Ended`).
2. `Tick` emits `AudioHostSignalKind.Ended` (`FluentMediaAudioHost.cs:1115–1125`).
3. `OnHostSignal` → `AutoAdvanceAsync` → `LocalNextAsync` → `_session.Next()` + `LoadAndPlayCurrentAsync` (`PlaybackController.cs:2411–2414, 1569–1580`).
4. `LoadFastStart` → `DisposeSessionAsync` (Stop + release `IAudioClient`) → new `OpenAsync` → new `WasapiAudioDevice` → `IAudioClient.Start`.

That is stop/start of the render stream. Audible gap by construction (device period + 100 ms shared buffer + decode prefill `_prefillPumps = 1`, `PcmAudioPlayer.cs:247, 659–665`).

### Render clock / position across the boundary

**Crossfade commit (ms > 0):** `_activeStartMs = rawPos` (`FluentMediaAudioHost.cs:1016`). `PositionMs = RawPositionMs - _activeStartMs` (`384–387`) jumps to ≈0 for B. The mixer `SampleClock` (`ConsumeSeq`) is **continuous**. `IAudioClock` on the same client keeps ticking. UI/Connect see a track-relative reset; the hardware clock does not.

**0 ms hard cut:** `_clockStale = true` on `Load`/`Stop` (`405–406, 428–431`) so `RawPositionMs` reports 0 until `OpenSessionAsync` clears it (`632–633`). New session: `_activeStartMs = 0`, new `IAudioClock` from 0. There is no sample-continuous position across the boundary — the old clock dies with the old client.

---

## Instrumentation added

Hand-off logs on `FluentMediaAudioHost` only (plus a read-only `PcmAudioSession.XrunCount` forwarder). No new env flags, no RT-path allocations, no behavior change. Strings are built only when `WaveeLog` Info is enabled (existing interpolated-handler pattern, same as `[posdiag]`).

### Events (`[gapless]` prefix)

| Event | When | Fields |
|---|---|---|
| `prepare-primed` | `PrepareNextCoreAsync` finished | token, `IsReady`, `leadIn`/`trailPad` (will be 0/0 today), overlap, duration |
| `next-body` | `SupplyNextBodyAsync` | token, `attached=1/0` |
| `arm` | first Tick inside the last `max(crossfadeMs, 2000)` ms | `remainMs`, `fadeMs`, `overlap`, `primed`, `body`, `reason`, mixer `clock`, `xruns` |
| `commit-crossfade` | `CommitCrossfade` | mixer `clock` (B’s first mixed frame), `fadeFrames`, `fadeMs`, `raw`, `primed`, `body`, `xruns`, `xrunDelta` |
| `ended` | first `PlaybackState.Ended` | A’s final mixer `clock`, `raw`/`pos`, prep readiness, `inFlight`, `xruns`/`xrunDelta` |
| `hardcut-b-open` | next `OpenSessionAsync` after an Ended that did not commit | B mixer `clock` (≈0), **`wallGapMs`** (Ended → new session), `aEndClock` |

`reason` on `arm`: `0` = will commit (ready + overlap + fade>0), `1` = zero-ms / disabled, `2` = decoder not primed, `3` = overlap not allowed, `4` = no prepared token.

`xruns` is `AudioFeedThread.XrunCount` (ring underrun → silence on the RT feed). `xrunDelta` is the count since `arm`. A rise around the boundary is an under-run.

Mixer `clock` is `PcmAudioSession.SampleClock` (`ConsumeSeq`) — frames handed to the sink, i.e. the render-client write domain. A successful gapless commit would show B starting at A’s last clock with `wallGapMs` absent (no `hardcut-b-open`). A hard cut shows `ended` then `hardcut-b-open` with a positive `wallGapMs`.

### How to read it

The in-app **playback-diagnostics** page (`PlaybackRuntimeDiagnosticsPage`, route `playback-diagnostics`) is the PlayPlay *runtime provision* report. It does **not** show these lines.

Read the **Diagnostics panel** (live log ring) or the session log file:

1. Settings → Diagnostics (or the log folder via the panel’s “open folder”).
2. Filter search: `gapless`.
3. Level: Info (default). No env flag required.

Listening pass:

- Crossfade **off** (default): expect `arm reason=1`, then `ended primed=0|1`, then `hardcut-b-open wallGapMs=…` — never `commit-crossfade`.
- Crossfade **on**, 5 s: expect `prepare-primed` / `next-body` well before `arm reason=0`, then `commit-crossfade`. `xrunDelta` should stay 0.
- Short track / seek-to-end: compare `prepare-primed` / `next-body` timestamps to `arm` / `ended`. If `arm primed=0` or `body=0`, scheduling lost the race.

---

## Fix design

### 1. Remaining-ms-keyed prepare (and keep the start-of-track warm)

Keep today’s `after-start` warm — it is the right budget for short tracks. **Add** a sample-clock / remaining-ms re-arm:

- Drive prepare from `VoiceScheduler.NeedsPrepare` (or a host Tick equivalent): `endingSoon = overlapMs + worstCasePrimeMs` (key + CDN + `TryOpen` + ring prefill). Suggested worst-case margin: ≥ 8 s, ≥ full duration on tracks shorter than that.
- On **seek**, bump an epoch and `Invalidate` the slot if the new remaining time is below the margin; re-prepare immediately (the engine scar fix at `VoiceScheduler.cs:211–223`).
- When autoplay/continuation lands, `EmitSnap` already re-schedules; also trigger prepare if remaining < margin even when `RemainingInContext > 5`.
- Do not serialize `PrepareNext` behind `SoftReload` more than necessary: body attach can stay on `_tail`, but `TryOpen` should keep running on the worker pool (already `Task.Run`).

### 2. Never reopen the device mid-queue

The graph is already fixed-rate. The fix is **one `PcmAudioSession` / one `IAudioClient` for the whole audio queue**:

- Promote prepared-next via `AddCrossfadeVoice` (or `VoiceScheduler.Commit`) instead of `DisposeSessionAsync` + `OpenAsync`.
- Keep `RebuildSink` only for a real default-endpoint change.
- `SoftReloadAsync` stays the rate-mismatch recovery for a *device* switch, not a track change.

### 3. Codec delay / padding trim

- Parse Vorbis granule / Spotify gapless fields in `SpotifyEngineAudioDecoder.TryOpen` into `GaplessInfo` (lead-in, trail-pad, `ExactFrames` when known). FLAC: STREAMINFO total samples vs decoded count. MP3: encoder delay from Xing/LAME if present.
- Wrap `DecoderAudioSource` in `TrimmingSource` inside `PcmAudioPlayer.OpenAsync` and `PrepareAsync` (one place, every codec).
- Until headers are trusted, a conservative Vorbis lead-in (typical 256–2048 samples at source rate, converted to mix frames) is better than `None`.

### 4. Seam-correct 0 ms hand-off

Wire the unused engine join:

- `_crossfadeMs == 0` && prepared && overlap allowed → `VoiceScheduler` `TransitionKind.Gapless` / `Commit` at `joinEnd` with `GainEnvelope.Constant` (`VoiceScheduler.cs:268–281`).
- Do **not** send 0 through today’s `CommitCrossfade` (that becomes dual-voice unity).
- If not ready by `joinEnd`: `DegradedGapless` (wait for A’s last sample) or `DegradedMicroGap` (declick in) — never `LoadAndPlayCurrent` if the slot is still filling.
- Emit `AudioTransitionKind.Started` so `CommitPreparedTransitionAsync` advances the session **without** reloading (`PreparedTransitionTests.NaturalHandoff_AdvancesExactPreparedItem_Once_WithoutReload`).
- Rebase `PositionMs` with `_activeStartMs` the same way as today’s fade commit.

### 5. Route every audio advance through prepared-next

UserQueue / autoplay already preview correctly. Close the holes:

- Default crossfade-off must still **commit** the prepared voice (killer 4).
- Manual Next may stay immediate (documented), but if the prepared token matches `PreviewNext`, promote it instead of `LoadAndPlayCurrent`.
- When continuation/autoplay appends the first next item inside the ending-soon window, prepare immediately (do not wait for the next `after-start`).

---

## Tests to write

### Wavee.Tests (headless, no device)

1. **`MediaSwitchLogic`** — already covers `AllowCrossfade` Audio→Audio only (`MediaSwitchLogicTests.cs`). Add: Audio→Audio remains true when both sides are music URIs; document that LocalFile is a hard cut (already `AllowCrossfade_False_ForLocalToLocal`).
2. **Prepare-next decision table** (pure, next to `SchedulePreparedNext` / extract a function):
   - Linear context B is prepared with `allowOverlap=true`.
   - User-queue head wins over context (`PreviewNext` order).
   - Autoplay tail (provider `autoplay` in Upcoming) is prepared.
   - `RepeatMode.Track` → `allowOverlap=false`.
   - Episode / podcast → prepared, `allowOverlap=false` (exists).
   - Video next → no prepare.
   - `CanPrepareNext == false` → no prepare (exists in `MediaProviderSeamTests`).
   - Signature stable → second `SchedulePreparedNext` is a no-op.
   - Seek does **not** change the signature today (pin current behavior); after the fix, seek-into-ending-soon re-prepares.
3. **0 ms must still emit a transition** (after the fix): a fake `IPreparedAudioHost` that records `Prepare`/`Supply` should see `Started` with `EffectiveFadeMs=0` and **zero** extra `LoadFastStart` on natural end. Today’s `PreparedTransitionTests.NaturalHandoff_*` only covers a host-emitted `Started` (5 s fade) — add a 0 ms case that fails against current `Tick` (documents the bug) then flips when the join is wired.
4. **`GainEnvelope.Fade(…, 0)` is `Constant`** — pin so nobody “fixes” 0 ms by calling today’s `CommitCrossfade`.

### Headless synthetic-PCM continuity probe

**Feasible** in `FluentGpu.Engine.Tests` / VerticalSlice, not in Wavee.Tests (no WASAPI).

- `PcmAudioPlayer` + `HeadlessAudioEndpoint` + two `MemoryAudioSource` ramps (already in `AudioGraphTests.cs:125–126`).
- Drive `VoiceScheduler` with `TransitionKind.Gapless`, `overlap=0`, known `GaplessInfo`.
- Assert: mixer output is sample-continuous (last sample of A, first *trimmed* sample of B, no zero run longer than 0).
- Second probe: `fadeFrames=0` through Wavee’s `CommitCrossfade` shape (two `Constant` voices) — expect overlap energy, proving why that branch must not be the 0 ms path.
- **Not feasible** headlessly: real `IAudioClient` Start/Stop gap, shared-mode padding, Spotify granule vs `GaplessInfo.None`. That is the `[gapless] wallGapMs` / `xrunDelta` listening pass above.

A Wavee-level probe that feeds synthetic PCM through `FluentMediaAudioHost` would need a test seam to inject `IAudioDecoder` / skip CDN. Prefer the engine probe + host Tick unit test with a fake `PcmAudioSession` if one is extracted; do not stand up WASAPI in CI.

---

## As fixed

Date: 2026-08-14. All four killers addressed; the agent-facing contract now lives in `.claude/skills/wavee/audio-handoff.md`.

| Killer | Verdict then | As fixed | Covering test |
|---|---|---|---|
| **1. Prepare-next scheduling** | UNCERTAIN — trigger was identity-change-keyed only; a seek never re-armed | Rules extracted to the pure `PreparedNextPolicy`: `EndingSoonMarginMs = overlapMs + 8 s` (clamped to duration on short tracks), a remaining-ms re-arm on top of the retained start-of-track warm, and a seek that lands inside the window re-prepares. The signature dedupe makes redundant re-arms free. | the 15 `PreparedNextPolicyTests` |
| **2. Format change (44.1 k → 48 k)** | VERIFIED-OK as a format matter; the real gap was the per-track device reopen | No format work needed. The reopen is gone: a natural advance is a **mixer edit** on the live session, and `RebuildSink`/`SoftReloadAsync` are reserved for a real endpoint change. | `[gapless] hardcut-b-open` absence in the listening pass |
| **3. Decode pre-roll / trim** | BROKEN — `Gapless => GaplessInfo.None`; `TrimmingSource` unwired | `ResolveGapless` fills `GaplessInfo` (FLAC STREAMINFO; MP3 via the new `Mp3GaplessProbe` Xing/LAME parse with the 529-sample decoder-delay convention), and `TrimmingSource` wraps the decoder in **both** `PcmAudioPlayer.OpenAsync` and `PrepareAsync`. | `GaplessJoinTests.TheJoinTrimsTheIncomingCodecPriming_SoTheSeamCarriesRealAudio` |
| **4. The 0 ms path** | BROKEN — the commit predicate required `_crossfadeMs > 0`, so the default path drained → `Ended` → full session reopen | Two-phase butt-join: `CommitGaplessJoin` adds B's prepared voice to the live mixer at A's natural-end frame with `GainEnvelope.Constant`; `AnnounceGaplessJoin` then emits `Started` with `EffectiveFadeMs = 0` so the session advances **without** reloading. Not-ready degrades by *holding* `Ended` (bounded) and promoting when the slot lands — never a reload mid-fill. | `GaplessJoinTests.ConstantVoiceAtTheNaturalEndFrame_IsSampleContinuous`, `…_PreparedTransition` 0 ms case |

The trap that motivated killer 4's design is pinned by two tests: `Fade_WithZeroFrames_IsTheConstantEnvelope` (a zero-length fade *is* unity gain) and `TwoConstantVoicesOverlapping_SumTheirEnergy_WhichIsWhyZeroMsMustNotCrossfade`. Anyone who "simplifies" gapless into `CommitCrossfade(0)` has to delete both.

### Instrumentation kept

The Wave-0 `[gapless]` events stayed and gained the join/degrade states (`commit-join`, `join-live`, `join-abandoned`, `promote-at-end`, `ended-hold`, `rearm`). `arm reason` is now **0** = will commit, **2** = not primed, **3** = overlap refused, **4** = no token — the old **1** (zero-ms/disabled) is gone because 0 ms now commits. Reading guide in the skill doc.

### Not done — final acceptance is a listening pass

Everything above is verified headlessly (mixer continuity, trim, decision table). What CI structurally cannot check is the thing the user hears: the real `IAudioClient` boundary, shared-mode padding, and whether Spotify's Vorbis bodies actually carry usable granule/gapless data (the probe currently returns `None` for Vorbis, so those tracks rely on the join alone, without trim). **Acceptance: play a continuous album end-to-end** and confirm `arm reason=0` → `commit-join` → `join-live` with no `hardcut-b-open` and `xrunDelta=0`. A Vorbis lead-in default was deliberately *not* guessed — if the listening pass reveals priming clicks on Spotify tracks, that is the next change, with a measured value rather than a folklore constant.
