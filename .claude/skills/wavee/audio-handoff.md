# Wavee audio hand-off (gapless & crossfade)

How one track becomes the next without a hole in the sound. Diagnosis + fix history: `docs/plans/wavee/gapless-findings.md`. Code: `src/apps/Wavee/SpotifyLive/Audio/FluentMediaAudioHost.cs` (the host), `src/apps/Wavee/Backend/PreparedNextPolicy.cs` (the rules), `src/FluentGpu.Engine/Media/Playback/Audio/` (mixer, `VoiceScheduler`, `TrimmingSource`).

**The rule that shapes everything: one `IAudioClient` per audio queue.** A track change is a *mixer edit*, never a device reopen. Tearing down the WASAPI session and opening a new one costs the device period + the shared-mode buffer + decode prefill — an audible gap by construction, no matter how good the decode is. `RebuildSink` / `SoftReloadAsync` exist for a real endpoint change (the user switched output device), not for advancing the queue.

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

The mixer graph is **fixed-rate**: `OpenAsync` opens the endpoint first and binds the decoder to `endpoint.Sink.Format`, arming a `LinearResampler` when the source rate differs. A 44.1 kHz → 48 kHz track change does **not** reinitialise WASAPI. If you are chasing a gap at a rate change, the cause is a session reopen (above), not the rate.

## Reading the `[gapless]` log

Settings → Diagnostics, filter `gapless`. Info level, no env flag, no allocation on the RT path.

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

A healthy continuous album: `arm reason=0` → `commit-join` → `join-live`, no `hardcut-b-open`, `xrunDelta=0`.
