# Wavee audio hand-off (gapless & crossfade) and the engine under it

How one track becomes the next without a hole in the sound, and what keeps the sound going while the machine is busy, the user seeks or drags the bar. Diagnosis + fix history: `docs/plans/wavee/gapless-findings.md` (the hand-off), `docs/plans/wavee/playback-audit-and-scrubbing.md` + `playback-smoothness-implementation.md` (continuity, seeking, scrubbing, DSP; #167). Code: the pump `src/apps/Wavee/Playback/Playback.Audio.cs` (+ `Playback.Audio.Scrub.cs`; the endgame rules `Playback.Endgame.cs`; `GaplessJoinClock` in `Playback.Transitions.cs`; the pure scrub/keyboard rules `Playback.Scrub.cs`; the glitch ledger `Playback.Glitch.cs`) and, in the sibling engine checkout, `src/FluentGpu.Engine/Media/Playback/Audio/` (`PcmAudioPlayer`, `CrossfadeMixer`, `RingAudioSource`, `AudioFeedThread`, `PolyphaseResampler`, `ScrubGrain`, `DspStages`, `VoiceScheduler`, `TrimmingSource` in `AudioSources.cs`) and `src/FluentGpu.Windows/Wasapi/`.

**The rule that shapes everything: one `IAudioClient` per audio queue.** A track change is a *mixer edit*, never a device reopen. Tearing down the WASAPI session and opening a new one costs the device period + the shared-mode buffer + decode prefill — an audible gap by construction, no matter how good the decode is. `RebuildSink` / `SoftReloadAsync` exist for a real endpoint change (the user switched output device), not for advancing the queue. The same rule holds for starvation and seeking below: **neither ever stops or resets the device.**

## The two boundary shapes

| Crossfade setting | Shape | Mechanism |
|---|---|---|
| `> 0 ms` | **Overlap.** A fades out while B fades in. | `CommitCrossfade` — `GainEnvelope.Fade` on both voices over the fade window. |
| `0 ms` (the default) | **Butt-join.** A's last sample is followed immediately by B's first. | `CommitGaplessJoin` — B's prepared voice is added to the *live* mixer at A's natural-end frame with `GainEnvelope.Constant`. |

**Never implement the 0 ms path by calling the crossfade commit with `fadeMs = 0`.** `GainEnvelope.Fade(…, fadeFrames: 0, …)` returns `Constant`, so a "zero-length fade" is two voices at *unity* for the whole tail — a doubled, summed overlap, not a join. `GaplessJoinTests` in `FluentGpu.Engine.Tests` pins both halves of this: the butt-join is sample-continuous, and the overlap counter-probe shows the summed energy you get if you take the shortcut.

### The 0 ms join, in two phases

1. **`CommitGaplessJoin`** — inside `GaplessCommitLeadMs` (1.5 s, several ticks) of A's end, B's voice joins the live mixer at A's estimated natural-end frame. A is never faded or truncated; the WASAPI client never stops. A seek invalidates the scheduled join frame (the natural end moved), so the join is re-scheduled.
2. **`AnnounceGaplessJoin`** — when the session clock crosses the join frame, the host emits `AudioTransitionKind.Started` with `EffectiveFadeMs = 0`, so `CommitPreparedTransitionAsync` advances the session **without reloading**. `PositionMs` rebases on `_activeStartMs` exactly as the fade commit does.

If the slot is not ready by the boundary, the host **holds the `Ended` signal** (bounded, `_endedHold`) while a prepare is in flight and promotes the moment it lands (`TryPromoteAtEnd`) — it must never fall back to a full reload while the slot is still filling. Only when nothing arrives does it hard-cut, and then `_gaplessHardCutPending` makes the next `OpenSession` log the measured `wallGapMs` so the failure is visible rather than merely audible.

## When the next track gets prepared

`PreparedNextPolicy` (pure, unit-tested in `Wavee.Tests/Audio/PreparedNextPolicyTests.cs`) owns the rules:

- **`Decide`** → *prepare?*, *may the boundary overlap?*, and the dedupe *signature*. Overlap needs music on **both** sides (episodes/podcasts prepare but never overlap), `repeat != Track`, and an Audio→Audio boundary (`MediaSwitchLogic.AllowCrossfade`). Video on either side, or a gated next, prepares nothing — a null signature is the cancel signal.
- **`EndingSoonMarginMs`** = `overlapMs + WorstCasePrimeMs` (8 s: key + CDN + `TryOpen` + ring prefill), clamped to the full duration on tracks shorter than the margin.
- **`SeekRequiresRearm`** — a seek that lands inside the window re-arms the prepare. This is what makes scrubbing into the last few seconds still hand off cleanly instead of falling back to a reload.

The start-of-track warm prepare is kept (it is the right budget for short tracks); the remaining-ms re-arm is *added* on top. The signature dedupe makes a redundant re-arm free.

## Encoder priming must be trimmed

A butt-join is only sample-accurate if the incoming track's codec priming is dropped. `GaplessInfo` is resolved per decoder in `ResolveGapless`:

| Codec | Source of truth |
|---|---|
| FLAC | STREAMINFO total samples → `ExactFrames`, `TailKnown` |
| MP3 | `Mp3GaplessProbe` (Xing/Info + LAME tag): lead-in = `delay + 529`, trail-pad = `padding − 529` (the 529-sample decoder/filterbank delay convention). Seekable streams only; a probe failure is never a playback failure. |
| other | `GaplessInfo.None` |

`TrimmingSource` wraps the decoder source in **both** `PcmAudioPlayer.OpenAsync` and `PrepareAsync`, so every codec goes through one trim point. If `Gapless` reports `None` where trim was expected, the seam will play priming silence as audio — check the probe before blaming the mixer.

## Sample rate is not the problem

The mixer graph is **fixed-rate**: `OpenAsync` opens the endpoint first and binds the decoder to `endpoint.Sink.Format`; when the source rate differs the adapter arms a **`PolyphaseResampler`** — 64-tap Kaiser-windowed sinc (β = 9, cutoff `0.5·min(from, to)/from`), exact phase tables for the usual ratios and 256 interpolated phases otherwise, alloc-free per block; 0.0 dB at 20 kHz and ≈ −99 dB at 24.1 kHz for 44.1 → 48. (`LinearResampler` is deleted: no linear-interpolation path remains.) `Process` consumes every input frame it can and keeps the 63-frame history inside, and **`Flush` is incremental — at EOF call it until it returns 0** (the tail is trimmed to ⌈in·to/from⌉); the Vorbis, FLAC, MP3 and AAC adapters all drain this way. A 44.1 kHz → 48 kHz track change does **not** reinitialise WASAPI. If you are chasing a gap at a rate change, the cause is a session reopen (above), not the rate.

## Why the sound does not stop (continuity, #167)

**A dry ring is a short, bounded silence — never a stop/start loop, never a device restart — and every incident is counted and attributed.** The threads audio depends on are scheduling-immune:

| Thread | Priority | Job |
|---|---|---|
| `FluentGpu.AudioRT` | `Highest` + MMCSS "Pro Audio" | Renders 10 ms blocks (≤ 3 per wake) from the mixer into WASAPI; never blocks, never allocates (the tripwire pins it). Decides "ready to start" itself (F5), so the clock thread no longer gates the first render. |
| `FluentGpu.AudioProducer` (one per ring) | `AboveNormal` + MMCSS "Audio" | Decodes ahead into the voice's PCM ring; registers through `EnterDecode()` for its whole life. |
| `FluentGpu.AudioClock` | `AboveNormal` + MMCSS "Audio" | Publishes state, the FFT and the position; registers the same way. |
| `FluentGpu.AudioWorker` | `AboveNormal` | Seek mailbox / voice retire. |
| the process | `PowerThrottling.OptOut()` | `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` off, once, in `WasapiPcm.CreateBackend` — no EcoQoS slowdown of any audio thread when the window is minimized or on battery. |

MMCSS goes through the `IRtThreadCharacteristics` **instance** seam (`Enter()` = Pro Audio, `EnterDecode()` = "Audio"; the default is a no-op for headless/tests — never a static, parallel tests build rings concurrently). `MmcssProAudio.ProAudioRegistered` tracks the RT thread only; `EnterDecode` never touches it. A device wait with a negative timeout is INFINITE, not a spin.

**Rings (D8).** Every voice — the live one, the prepared-next one and every seek-prepared one — is sized by one `RingSizing` (`WasapiPcm.CreateBackend`: block 10 ms, **2000 ms ahead, 4000 ms ring, 1000 ms kept behind**). The producer decodes 2 s ahead of the read head; the ring keeps 1 s of already-played audio intact *behind* it (`PcmRing(minFloats, keepBehindFloats)`, `BehindFloats` is the exactly-intact span) so a backward seek is a cursor move. `RingSizing` is a record struct: use `RingSizing.Default` (10/500/1000/0) or the full constructor — `new RingSizing()` is all zeros.

**Starvation recovery (F2).** When the active ring is empty the RT thread submits **silence** for the block (inside the tripwire), records the span in a silence ledger and holds the content timeline; one stall is **one incident** (`RingAudioSource` latches `_starve` on the first short read of the incident, later blocks only accrue frames into `XrunFramesLost`) and grows that ring's decode-ahead target once. Silence is queued only to a shallow padding floor (`SilencePaddingFloorBlocks = 2`) so a deep device queue does not stretch the audible gap. It resumes when the ring holds `ResumeFrames` — 100 ms, doubled after an incident up to the ring's ahead target, halved after 30 quiet seconds, never grown by a seek-caused rebuffer — **and** the transport is running (phase 0): a 5 ms `TransportRamp` fade-in. The device is never `Stop()`/`Reset()` on a starve. `PlayedFrames` stays a **content** clock (the presentation clock subtracts the silence spans the device already played; `RawPlayedFrames` is the device's own count, used for device padding), so the bar freezes with the audio and the spectrum holds. A dead sink is treated as drained and reported through `RecordDeviceLost()` — pause/seek/fade no longer hang on a device that is gone. Control waits are signalled (`RingAudioSource.ReadyWake`, `PcmAudioSession.WaitAsync`), not `Task.Delay(2)` polls.

## Seeking: three paths (D3)

`MediaPlayer.SeekAsync` stays the engine's dispatcher for parked/initial seeks. A user seek on a playing track is `Playback.Audio.Seek(ms, epoch, gen)` → a last-write-wins mailbox (stamped with the load epoch; dropped when stale, cleared on `Load`) → `SeekCoreAsync`, which tries in order:

| # | Path | When | What happens |
|---|---|---|---|
| 1 | **Ring jump** — `TryJumpWithinRingAsync`, `CmdJumpWithinRing` | the target is inside the ring: within the intact kept-behind second, or ahead within the decoded span minus one fade and one block | The RT thread decides (it owns the cursor): reads the next 5 ms as the tail, moves the cursor, blends tail × Out ⊕ fresh × In (equal power, 5 ms), resets the voice's WSOLA. No decoder, no I/O. Accepted/refused is published; a refusal moves nothing. |
| 2 | **Voice swap** — `SwapSeekAsync`, `PrepareAtAsync`, `SwapToPreparedAsync` | not in the ring, a decoder lease is free (`TryAcquireDecoderLease`; `_decoderSlots` = 3) | A **second decoder** opens at the target on a **non-owning `RingSource` view** of the live body (`Body.ProbeAt` — a second reader that moves no cursor or epoch), ready after ONE block; one compound command (`CmdSwapVoice`) adds it and fades the old voice in the same block (5 ms equal power). The old audio keeps playing until then. If the prepare has produced nothing after `SeekStaleMs = 80`, the old voice fades to silence over 10 ms and the new one comes in from silence. |
| 3 | **In place** — `SeekInPlaceAsync` | no lease free, or the swap failed | The "seek marker": fade-out-hold (voice held at gain 0, still rendering), flush, decoder seek, wait ≥ 1 block, fade-in. `Body.InterruptPendingRead` is used **only** here. No device Stop/Reset. |

Rules that bite:

- **Newest wins.** One seek prepare in flight; a newer seek cancels it. Once the Body's ownership is handed to the view (before the swap — the retiring voice's teardown closes its source and must not dispose what the new voice reads) the swap is no longer cancellable.
- **Position never snaps back.** No device Reset; the engine rebases at the block where the jump/swap becomes audible; every `Position`/`Seeked` carries the **seek generation** and the reducer drops reports older than the latest seek. The bar's drop-point hold releases when `Playback.SeekLandedAfter(committedGen, LastSeekLandedGen)` (wrap-safe, the gen is sampled *before* the commit post) or at `SeekRail.CommitHoldMs`. Connect owner: the reducer paints the target at once and forwards one `seek_to`; a bare newer cluster ack does not end the hold — only tolerance or expiry does.
- **Give a parked voice its level back.** The stale timer and a scrub release can leave the main voice held at gain 0; a seek that lands on the same voice (jump / in-place) or that **fails** calls `RestoreSilencedVoice` (5 ms fade-in). Skip it and the track plays on in silence.
- An interrupted landing read must not latch EOF (a second seek mid-landing used to end the track and advance the queue); the Vorbis decoder pool (`VorbisDecoderPool`, capacity 4) rents decoders by setup-header hash, so the swap's second decoder skips the setup parse.
- Log: `audio.seek.done to= kind= latencyMs=` (kind ring / disk / far), `audio.seek.stale-fade`, `audio.seek.swap.failed` (falls back to path 3), `audio.seek.failed`.

## Scrubbing (D4)

- **Gesture** — `ScrubModel` (pure, `Playback.Scrub.cs`; Idle → Pressed → Scrubbing) lives in the reducer; moves are coalesced to ≤ 20/s (`CoalesceMs = 50`), velocity is audio-ms per wall-ms. An audible gesture emits `ScrubBegin/Move/End/Cancel` instead of `Seek`; **`ScrubEnd` always ends in one real seek** (skipped only when a newer seek owns the outcome). Paused: visual-only (the device is stopped, no grains). Connect: visual-only, one `seek_to` on release. Video: `Video.ScrubPreview` — inaccurate (keyframe) seeks every 100 ms, muted during the gesture, unmuted by `ScrubPreviewEnd`.
- **Begin** — `BeginScrubAsync` holds the main voice (20 ms fade-out, then parked: `MixVoice.Held/HoldAtFrame`) and adds the **grain voice** (20 ms fade-in) with its gain slot at `ScrubGainLinear = 0.501` (−6 dB, × the live EQ preamp).
- **Grains** — `ScrubGrainSource` (`Playback.Audio.Scrub.cs`) runs on the grain ring's producer thread, one hop per call: **60 ms Hann grains, 30 ms hop** (50 % overlap, constant-overlap-add — `ScrubGrain.Window/NextStart/OverlapAdd`), rate-following up to 4×, cut from a 2 s cache of **resident** compressed bytes only (a non-owning view with `ResidentOnly`; a target that is not resident parks). It **parks itself after 150 ms** without a retarget — silence hops, no timer thread. Only Ogg Vorbis and FLAC have a grain path; other formats (AAC…) scrub visually.
- **Release** — the grain voice fades out (20 ms) and retires itself; the held main voice is replaced by an ordinary seek (a swap at the release point when a lease is free, else in place). **Cancel** (Escape, capture loss, the rail unmounting, `ScrubDrop()` from `Load`/`Stop`) is one `CmdReleaseVoice` with a 20 ms fade-in and is idempotent.
- **Keyboard** — `KeyboardScrubLadder` (numbers in `palette-shortcuts.md`): a visual target that commits as one seek 250 ms after the last press — there is no key-up event.
- A heavy scrub's grain-ring underruns count as xruns; if the health card misbehaves after one, that is the suspect.

## After the mixer: limiter, volume, normalization (D5–D7)

Per RT block: voices (gain slot → per-voice EQ) → mixer sum → `graph.RenderMaster` (master EQ + preamp, then the **lookahead limiter**) → `TapSpectrumBlock` (post-limiter, **pre-volume**: the picture must not follow the slider) → `_masterGain` (volume) → `_masterChannel` (balance) → `_transport` (pause/resume ramp) → `TapBlock` (level meter) → device.

- **The limiter runs before the volume**, so tone does not change with the slider. Everything after it can only attenuate (volume ∈ [0, 1], constant-power pan ≤ 1, ramp ∈ [0, 1]; `TrySetVoice` clamps `initialVolume`) — never add a gain above 1 after it. `LimiterSpec` default: ceiling −1.5 dB, release 50 ms, **lookahead 2 ms**. The lookahead delays the audio (96 frames at 48 kHz); it is in `TotalLatencySamples`, so the position clock compensates and the spectrum publish does not subtract it again. A compile with an equal spec reuses the live limiter; `CmdReset` resets it.
- **EQ is never mutated from the clock thread.** `ReconcileEffects` designs the coefficients off the RT thread (double-precision biquads) and posts `CmdSetEq` / `CmdSetVoiceGain` through the 64-slot mixer command queue, applied at block start; the voice chain is always `[Gain, Eq]` at a fixed capacity.
- **Normalization** — the mode is applied **once**, in `Playback.Audio.NormalizationFactor`: take the album pair when album mode is on and the source carries one, else the track pair; `factor = 10^(clamp(gain + pregain, ±30 dB) / 20)` with pregain Quiet −9 dB, Normal 0, Loud +3 dB (catalogue gains are relative to −14 LUFS, so the targets are −23 / −14 / −11 LUFS); cap `factor × peak ≤ LimiterCeilingLinear` (0.8414 = −1.5 dB, the one constant shared with `LimiterSpec.Default`) — **Loud lifts the cap** and lets the pre-volume limiter do the limiting. Sources: Spotify Ogg header (album pair at header bytes 152/156), the lossless catalogue params (kept at the −14 reference — no mode applied there), and ReplayGain tags on local files (−18 LUFS referenced, so `+ReplayGainToSpotifyDb` = +4 dB first). Settings keys: `NormalizationEnabled`, `NormalizationMode` (persisted ints, append-only: 0 Quiet, 1 Normal, 2 Loud), `NormalizationAlbum`. A change is **live**: `SetNormalization()` ramps each live voice's gain slot by `factor_now / factor_baked_for_that_voice` over 50 ms (`s_bakedFactor`, `IGainFolding`) — no decoder reopen, and never apply the mode a second time inside a decoder.
- **Default volume** — `SavedVolume` defaults to 0.794 and `UnmuteDefault` follows it; through the cubic taper that is 0.794³ ≈ 0.5005 → **−6 dB** (it was 0.7 → −9.3 dB).

## Reading the audio health log

| Event | Says |
|---|---|
| `audio.glitch` (Warning) | One per RT underrun incident (it replaces the old `[audio] xrun` line): `stallMs`, `gapFrames`, `ringFramesAtMiss` (**0 ⇒ the producer fell behind; > 0 ⇒ the RT thread itself was late**), `gcTicks` (> 0 ⇒ a GC suspension in the window), `posMs`, `state`, `voice`, `totalFramesLost`, `ageMs`, `track`, `verdict`. The event carries only the first block's shortfall; the open incident's tail is folded into the ledger's stall length (`GlitchLedger.Extend`). |
| `audio.session.summary` | One per session, Info `clean` or Warning `glitches`: `incidents`, `producerStarves`, `deviceLate`, `gcImplicated`, `byteWaits`, `framesLost`, `longestStallMs`, `longestByteWaitMs`, `track`, `verdict`. |

`verdict` is a key from `GlitchLedger`: `clean`, `gcPauses` (at least half the incidents had a GC pause), `byteStarved` (byte waits plus producer starves — network/disk), `producerStarved` (CPU), `deviceLate`. The Diagnostics page's **Playback health** card (`PlaybackHealthView`, 1 Hz over `Playback.Audio.Metrics.Read()`) shows the same ledger beside whether MMCSS registered, the ring fill against its target (healthy: ≈ 2000 / 2000 ms), the device buffer, decode × real time and the last seek (ms + kind). The ledger is per session, while `Metrics.Xruns` is the process total.

**`--stress-audio`** reproduces "heavy computer use" on demand and prints the same ledger: `Wavee.exe --stress-audio --file <flac|ogg|mp3> --burners 2 --memory-mib 2048 --minimize --seconds 60` plays through the **real WASAPI device** under spin threads at each priority class (`--burners n` per class), a churned `byte[]` (`--memory-mib`), a minimized stand-in window (`--minimize`) and, with `--battery-saver`, execution-speed throttling switched **on** (the inverse of the opt-out). Exit 0 = no incident (`--allow-one` tolerates one), 2 = incidents, 1 = could not complete, 64 = usage; `--track <spotify:track:id>` plays a logged-in track, and without a source (or with `--fake`) it is a smoke run through the silent voice — not the gate. The pure twin is `AudioStressModel` (engine) with `StarvationRecoveryTests` driving the real session.

## Reading the `[gapless]` log

Settings › Privacy & diagnostics › Logs › Log viewer, filter `gapless`. Info level, no env flag, no allocation on the RT path.

| Event | Says |
|---|---|
| `prepare-primed` | The slot opened: `ready`, `leadIn`/`trailPad` (trim actually resolved?), `overlap`, `dur`. |
| `next-body` | Whether the body was attached (`attached=1`) or the token had already moved on. |
| `arm` | The endgame opened. `reason` is the verdict: **0** = will commit, **2** = not primed, **3** = overlap refused, **4** = no token. Also carries `remainMs` and the xrun baseline. |
| `rearm` | The remaining-ms nudge fired (endgame opened with nothing prepared). |
| `commit-join` / `join-live` | The butt-join was scheduled, then went live at the join frame. |
| `commit-crossfade` | The overlap path committed (`fadeMs > 0`). |
| `join-abandoned` | A scheduled join was dropped, with the reason. |
| `promote-at-end` | The slot was consumed at `Ended` instead of at a scheduled join. |
| `ended-hold` | `Ended` is being held while a prepare finishes — the anti-reload guard working. |
| `hardcut-b-open` | **The failure case.** Carries the measured `wallGapMs` — the gap a listener heard. |

A healthy continuous album: `arm reason=0` → `commit-join` → `join-live`, no `hardcut-b-open`, `xrunDelta=0`, and an `audio.session.summary` of `clean`.
