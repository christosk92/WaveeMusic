# Playback engine audit — packets, seeking, scrubbing, normalization, EQ, volume, quality, robustness

Repo state audited: **app** `C:\wavee\waveemusic` HEAD `c060e9b7d0ea133ebe9431c6805fd0fce56adb13` (branch `main`); **engine** `C:\wavee\fluent-gpu` HEAD `c50e900df35491818a7a98e47520a8742ee18998` (branch `main`). Audit date 2026-10-02. Read-only: no build, no test, no run; every claim below carries a `file:line` from those two trees. Paths are repo-relative; `[engine]` = `C:\wavee\fluent-gpu\src\`, `[app]` = `C:\wavee\waveemusic\src\apps\Wavee\`. Where a number is derived from constants rather than measured, the derivation is shown and the line says **derived**; where a mechanism is clear but its trigger in the field is not, the line says **inferred**. The PlayPlay derivation is private and is not described here (it enters this pipeline only as the `_decrypt` callback at `[app]Spotify/Spotify.Audio.Stream.cs:1783`, which turns a 64 KiB ciphertext chunk at a chunk-aligned file offset into plaintext).

Severity: **P0** audible bug / crash · **P1** correctness · **P2** smoothness / performance · **P3** polish. Finding ids: `H-` halts, `S-` seek, `U-` seek-bar UI, `P-` packets/streams/codecs, `N-` normalization, `E-` equalizer, `V-` volume, `Q-` quality, `R-` robustness.

How this was produced: seven read-only sweeps (stream layer, Ogg/Vorbis, FLAC, app audio shell, engine core/threads, engine DSP/WASAPI/power, seek-bar/reducer/Connect) whose claims were then re-read at the cited lines by the author before being written down. A claim that could not be re-verified is not in this document.

Contents: §0 top priority — audio halts under heavy computer use · §1 findings table · §2 P0/P1 write-ups · §3 seek redesign · §4 scrubbing design · §5 normalization, EQ, volume verdicts · §6 quality upgrades · §7 quick wins · §8 proposed tests · §9 interplay with the flagship FFT plan · §10 work packages · §11 decoder: keep, specialise, or replace?

---

## 0. Top priority — "during heavy computer use, audio halts"

### 0.1 The continuity chain as built

Audio keeps flowing only while **two** buffers stay non-empty: the per-voice decoded-PCM ring (filled by a producer thread, drained by the RT thread) and, upstream of it, the compressed read-ahead ring (filled by HTTP/disk on the ThreadPool, drained by the decoder on the producer thread). Everything else — the clock thread, the app's 200 ms tick, the reducer, the UI — is bookkeeping and can be late without a dropout. The table is the verified map.

| Thread | Created at | Priority / scheduling class | Waits on | Per-iteration work | What stalls it |
|---|---|---|---|---|---|
| `FluentGpu.AudioRT` | `[engine]FluentGpu.Engine/Media/Playback/Audio/AudioFeedThread.cs:488` | `ThreadPriority.Highest` **+ MMCSS "Pro Audio"** (`:522` → `[engine]FluentGpu.Windows/Wasapi/MmcssProAudio.cs:21`; no `AvSetMmThreadPriority`, so the task default) | WASAPI period event **or** control wake, timeout 10 ms (1 ms after a full burst) — `AudioFeedThread.cs:528` → `PcmAudioPlayer.cs:1332-1336` → `WasapiAudioDevice.cs:219-233` `WaitForMultipleObjects` | `RenderBurst` ≤ 3 blocks of 10 ms (`:335-346`) → `RtRenderOnce` → `RenderBlock` (`PcmAudioPlayer.cs:1456-1547`): copy+mix only, alloc/lock-free (`AudioTripwire`) | Nothing at normal priorities; a GC suspension (all managed threads stop) |
| `FluentGpu.AudioProducer` (one per voice ring) | `[engine]…/Audio/RingAudioSource.cs:70-84` | **`ThreadPriority.AboveNormal` only** (`:79`) — no MMCSS, no power-throttling opt-out | `_producerWake.WaitOne(20)` (`:99`) + the RT low-water wake at half the target (`AudioFeedThread.cs:307-309`, `RingAudioSource.cs:327-339`) | `ApplyQueuedSeek` + `PumpAhead` (`:161-191`): decode in 20 ms chunks until 500 ms is buffered. **This is where the decoder, the decrypt and the byte waits run.** | Any thread at priority ≥ 9 holding every core; EcoQoS/efficiency-core placement; a byte wait (next row) |
| ↳ inside the producer: the byte wait | `[app]Spotify/Spotify.Audio.Stream.cs:933-975` (`Ring.ReadAt`) via `[app]Playback/Playback.Audio.cs:1361-1370` (`ReadAtEpoch`) | (producer thread) | `Monitor.Wait(_wake, 4 ms)` loop (`:966`, `PollMs = 4` `:840`) up to `DefaultWaitMs = 8_000` (`:832`), then `Starved` and the caller asks again (`Playback.Audio.cs:1366`) — i.e. unbounded | copies what is resident, otherwise `Advance(demand)` plans a range | The fetch loop (next row) being late |
| Wavee fetch loop (one per process) | `[app]Spotify/Spotify.Audio.Stream.cs:674-703` — `Task.Run(LoopAsync)` | **ThreadPool, Normal** | `Channel.Reader.WaitToReadAsync` | `ServeAsync`: `FillFromDisk` (synchronous 64 KiB disk reads, `:1762-1790` → `Wavee.Sdk/Streams/ChunkDiskCache.cs:409-430`, `OpenShared` FileStream `:353-354`) then `FetchRangeAsync` (HttpClient, ≤ 512 KiB, `:533`) | CPU scheduling at Normal; disk saturation; network |
| `FluentGpu.AudioWorker` | `AudioFeedThread.cs:486` | `AboveNormal` | `_workerWake.WaitOne()` (`:546`) | seek mailbox + retire queue (`:395-406`); it pumps only rings **without** a dedicated producer — on-box every ring has one (`:217`, `:233`), so it is not on the continuity path | — |
| `FluentGpu.AudioClock` | `AudioFeedThread.cs:487` | **Normal** (default) | `Thread.Sleep(15)` (`:562`) | `ControlTickOnce` → `TickControl` → `Advance` (`PcmAudioPlayer.cs:1373-1450`): state machine, `ReconcileEffects`, `PublishPosition`, xrun signal | Normal-priority load. Not on the steady-state continuity path, **but** every `Buffering/Ready → Playing` flip (open, seek, prepared-replace) happens only here (`:1397-1408`), and `RtRenderOnce` refuses to render until the state is `Playing`/`Stalled` (`:1289`) |
| `FluentGpu.AudioDevice` (cold) | `[engine]…/Audio/AudioDeviceController.cs:74` | Normal | `_wake.WaitOne(wait)` | device rebuilds | — |
| Wavee 200 ms tick | `[app]Playback/Playback.Audio.cs:81` (`TickMs = 200`), `:2671` (`System.Threading.Timer`) | **ThreadPool, Normal** | timer | starve fold (`FoldStall` `:2868-2899`), endgame/gapless arm (`:2730-2760`), xrun drain (`:2903-2941`), parked seek (`:2827-2834`), state → reducer signals | Normal load; **it decides the gapless hand-off** |
| Wavee pump chain `s_tail` | `Playback.Audio.cs:735-750` (`ContinueWith(..., TaskScheduler.Default)`) | **ThreadPool, Normal** | task chain | Load / Seek / Prepare / Pause / Resume — strictly serialized | Normal load; a blocked op ahead in the chain |
| `Wavee.AudioKeys` | `[app]Spotify/Spotify.Audio.KeyStore.cs:268-269` | `BelowNormal` | queue | key-store writes | — |
| `ChunkDiskCache` writer | `Wavee.Sdk/Streams/ChunkDiskCache.cs:104-115, 188` | dedicated low-priority | queue | chunk commits (off the fetch path) | — |

**Buffers and their numbers** (all verified; ms at 48 kHz):

| Buffer | Size / watermarks | Where |
|---|---|---|
| WASAPI shared render buffer | **100 ms** (`hnsBuffer = 100 * 10_000`), event-driven, periodicity 0, no `IAudioClient3` | `[engine]FluentGpu.Windows/Wasapi/WasapiAudioDevice.cs:468-469` |
| RT block | 10 ms, ≤ 3 blocks per wake (burst cap) | `AudioFeedThread.cs:137-138`, `:335-346` |
| Decoded-PCM ring (per voice) | capacity **1000 ms**, target ahead **500 ms**, low-water edge at **250 ms**, pump chunk 20 ms | `AudioFeedThread.cs:137-138` (ms ctor, used on-box at `WasapiPcm.cs:58`), `:216` (`_blockFrames * 2`), `RingAudioSource.cs:330` (`_targetFloats / 2`) |
| Prepared-voice ring (gapless next / seek-prepare) | capacity 1 s, target 0.5 s, readiness 0.5 s (0.1 s for a seek prepare) | `PcmAudioPlayer.cs:75-78` |
| Start / post-seek readiness (`StartupReadinessFrames`) | `max(rate/10, deviceCapacity + 2·block)` = **max(100 ms, 100 + 20 ms) = 120 ms** — used by `BufferingReady` (`:1348-1359`) for the first start and after a seek | `PcmAudioPlayer.cs:1338-1346` |
| **Resume threshold after a mid-play starve** | `PcmReady(StartupReadinessFrames)` asks each voice for `RecoveryFrames(threshold)`; every voice is wrapped in `WsolaAudioSource` (`:629`, `:711`) whose `RecoveryFrames => Math.Min(requested, _hop)` with `_hop = rate / 50` — so the effective threshold is **20 ms (one hop), not 120 ms** | `CrossfadeMixer.cs:246-258` (`:253-255`), `WsolaAudioSource.cs:34, 72` |
| Compressed read-ahead ring | 64 KiB slots (`ChunkDiskCache.cs:69`); window = `ReadAheadSeconds × byteRate`, capped 16 MiB, ≥ 8 slots + 4 kept behind (`Stream.cs:918-926`); tiers **10 s metered / 600 s when measured throughput ≥ 3× byte rate / 30 s otherwise** (`:921`); FLAC 30 s for the first 10 s of playhead, then 90 s (`Spotify.Audio.ReadAhead.cs`); shared budget **24 MiB** across live+prepared+retiring (`Stream.cs:158`), prepared ≤ 30 s (`:162`) | as cited |
| HTTP range | ≤ 512 KiB, landed slot-by-slot | `Stream.cs:533`, `:723-724` |
| Byte wait bound | 8 s per `ReadAt`, polled every 4 ms, then re-asked forever; "Reconnecting" to the UI after 1.5 s, `Fault.Network` after 90 s (3.25 s when the mirrors refuse) | `Stream.cs:832, 840`; `Playback.Audio.cs:157-181` |

Derived headroom at 48 kHz, 320 kbps Vorbis: the producer may be absent for **≤ 500 ms** (the ring's target depth — more only if it had just been topped up; the ring's *capacity* is 1 s but `PumpAhead` stops at the 500 ms target, `RingAudioSource.cs:166`) before the RT thread finds the ring empty; the RT thread itself may be late by **≤ 100 ms** (the device buffer) before the user hears a gap; the fetch loop may be late by the compressed window — **30 s or more** on a healthy link, 10 s metered. The decoder's own cost is small: the app already measures it as audio-seconds-per-wall-second (`Playback.Audio.cs:4243-4247`, `XRealtime`), typically tens of × real time for Vorbis.

### 0.2 What happens when the PCM ring runs dry — the amplifier

This is the part that turns a hiccup into a halt, and it is fully verified.

1. `RenderBlock` asks the mixer how many frames every audible ring can supply. When the active ring is **completely** empty it enters starvation phase 1 and returns 0 **without writing silence**: `int readable = _mixer.ReadableFrames(frames, out var waitingFor); if (readable <= 0 && waitingFor is not null) { _starvedRing = waitingFor; _starvationPhase = 1; return 0; }` — `[engine]PcmAudioPlayer.cs:1507-1513`; `ReadableFrames` at `CrossfadeMixer.cs:220-243`. The device keeps playing whatever is left in its 100 ms buffer.
2. Every RT wake now runs `RecoverStarvation` (`:1290`, `:1294-1329`). Phase 1: if the ring has refilled to the recovery threshold — intended as `StartupReadinessFrames` = 120 ms (`_mixer.PcmReady(StartupReadinessFrames)`, `:1299`) but **actually 20 ms**, because `PcmReady` defers to `WsolaAudioSource.RecoveryFrames(threshold) => Math.Min(requested, _hop)` and every voice is WSOLA-wrapped (`CrossfadeMixer.cs:253-255`, `WsolaAudioSource.cs:72`, `PcmAudioPlayer.cs:629`) — playback resumes with no device stop. Otherwise it waits for the device buffer to drain to **empty** (`buffered.WritableFrames >= buffered.CapacityFrames`, `:1300-1302`) — i.e. until silence has already started — then **stops and resets the WASAPI client** (`_out.Stop(); … resettable.Reset();` `:1305-1307`), bumps the render epoch and enters phase 2.
3. Phase 2 (`:1316-1328`): nothing is rendered until the ring again holds **20 ms** (same `PcmReady`); then a 5 ms fade-in is armed (`_format.SampleRate / 200`, `:1326`) and `_startRequested` makes the next `SubmitPending` call `_out.Start()` (`:1566-1572`). The frames of silence between the stop and the restart are recorded as one xrun incident (`RecordStarvedFrames`, `:1317-1321` → `RingAudioSource.cs:308-313` → `AudioFeedThread.FeedOnce` `:292-301`) and reach the app log as `[audio] xrun … gapMs=… ringFramesAtMiss=… gcPauseTicksDelta=…` (`[app]Playback.Audio.cs:2903-2932`).
4. In parallel, the clock thread publishes `Stalled` (`Advance`, `:1419`), the app tick posts `AudioSignal.Buffering` (`Playback.Audio.cs:2824`), the reducer sets `Buffering = true` (`Playback.cs:1978-1980`) and the seek bar stops advancing (`SeekRail.Advances`, `Shell.PlayerBar.cs:494-495`). Recovery reverses it: `Stalled → Playing` on the clock thread (`:1414`), `Started` re-posted by the tick (`Playback.Audio.cs:2809`).

Why this amplifies: for a producer that is **absent** for less than ~100 ms the design works perfectly — the ring refills, phase 1 resumes seamlessly and the device buffer hid the whole thing. For a producer that is absent longer, the device is stopped and restarted (one gap plus restart latency, benign). But for a producer that is **slow** rather than absent (say ~1× real time because it is being descheduled) the 20 ms threshold is the problem: the RT thread resumes as soon as two blocks exist, renders up to three blocks in one wake (`RenderBurst`, `AudioFeedThread.cs:335-346`), empties the ring again, the device drains its ~20–30 ms, and the client is stopped and reset again — a **Stop/Reset/Start cycle every few tens of milliseconds**, each with a 5 ms fade-in, until the producer gets ahead. The listener hears a buzzing stutter rather than a pause. There is no hysteresis between "stop" and "resume" (`PcmReady` is the only gate for both), and no recovery path that keeps the device running on silence. The spec's intent was the 120 ms cushion (`..\fluent-gpu\docs\plans\media-playback-api-spec.md` §7.9 as reported by the engine sweep; not re-read here) — the WSOLA wrapper silently replaced it.

### 0.3 Root-cause hypotheses, ranked

Each hypothesis names the mechanism (verified in code), the trigger (how likely under "heavy computer use"), and the one log field that confirms or refutes it. The `audio.glitch` event (one structured Warning per underrun incident; it replaced the old `[audio] xrun` line) carries the two discriminating fields: `ringFramesAtMiss` (0 ⇒ the producer fell behind; > 0 ⇒ the RT thread itself was late) and `gcTicks` (the engine's `gcPauseTicksDelta`; > 0 ⇒ a GC suspension in the window), plus `stallMs`, `posMs`, `track` and a `verdict`. Every session also ends with an `audio.session.summary` event (incidents, producerStarves, deviceLate, gcImplicated, byteWaits, framesLost, longestStallMs, verdict). Triage the field reports with the `audio.glitch` events first (log viewer, category `audio`), then the session summary.

**H1 — the producer thread is not scheduling-immune (most likely; mechanism verified, trigger inferred).** The RT thread is protected by MMCSS (realtime priority range); the thread that actually has to keep producing — `FluentGpu.AudioProducer` — is a plain `AboveNormal` managed thread (`RingAudioSource.cs:79`, base priority 9 in a `NORMAL_PRIORITY_CLASS` process). It loses the CPU to anything at ≥ 9: a game or encoder whose worker threads run `AboveNormal`/`High`, the dynamic boosts Windows gives a foreground window's GUI threads on wakeup, and on hybrid CPUs it can be placed on saturated efficiency cores regardless of priority. 500 ms of being descheduled empties the ring; a sustained ~1× real-time share triggers the §0.2 stutter loop. Confirms as: `xrun … ringFramesAtMiss=0 gcPauseTicksDelta=0` with no `audio.underrun` (byte-wait) line nearby. Fix: F1 + F2 + F3 below.

**H2 — the §0.2 recovery has no hysteresis and stops/starts the device per cycle (verified).** Independent of the cause: once the device buffer has drained the client is stopped and reset, and it is restarted the moment 20 ms exists — under a slow producer that is a stop/start loop (the buzzing stutter), under an absent one it is a gap plus a device restart. There is no "keep the device running on silence, resume behind a real cushion" path. Fix: F2.

**H3 — a byte wait inside the producer (mechanism verified; the trigger is network or disk, not CPU).** When the decoder reaches a slot the fetch loop has not landed, `Ring.ReadAt` blocks the **producer** for up to 8 s per round (`Stream.cs:966-975`) and the PCM ring drains in 500 ms. The fetch loop runs on the Normal-priority ThreadPool (`Stream.cs:686`) and does its disk-cache reads synchronously there (`FillFromDisk` `:1762-1770`, `TryReadChunk` under a per-file lock `ChunkDiskCache.cs:409-430`); heavy disk or network use elsewhere (a build, a sync client, a download) lengthens each range. The compressed window is normally 30 s or more so this only bites after a seek, at a track start, or when the link's throughput falls below the byte rate for the whole window. Confirms as: `audio.underrun file=… waitMs=8000 stallMs=…` and/or `audio.starve … reconnecting` lines; the UI shows "Reconnecting". Fix: F3 (a deeper PCM ring hides more), F8 (prefetch around a seek — §3), and the diagnostics in §0.5.

**H4 — GC suspension (mechanism verified, severity bounded).** Both repos run workstation concurrent GC (`Directory.Build.props:14-17` in each) and `SustainedLowLatency` is set once at start (`[app]App.cs:79`) and per live session (`PcmAudioPlayer.cs:1123, 1133-1142`, restored at `:1834-1838`). SLL suppresses *most* blocking gen-2 collections but not all (memory pressure, LOH allocation), and **every** GC still suspends all managed threads including the RT thread; a suspension > 100 ms is audible, and anything between 100 and 500 ms is a gap that the device buffer does not hide but the ring survives — a glitch, not a halt. The fetch path allocates per range (`Task`/`HttpClient` machinery, `GC.AllocateUninitializedArray(pinned: true)` 64 KiB and 512 KiB scratch, `Stream.cs:685-686`). Confirms as: `gcPauseTicksDelta > 0` on the xrun lines. Fix: F6.

**H5 — memory pressure → hard page faults on the producer path (inferred, cheap to test).** "Heavy use" often means a large working set elsewhere; Windows trims idle pages process-wide. The ring slots ahead of the playhead (24 MiB, pinned but **not** locked — `GC.AllocateUninitializedArray(pinned: true)` keeps the GC from moving them, it does not keep the OS from paging them, `Stream.cs:230`), the 192 KiB Vorbis probe window and the decoder's 0.3–0.7 MB tables (`Playback.Audio.cs:1373-1376`) are touched only when the playhead arrives. A hard fault under disk contention costs 10–100+ ms each on the producer thread. Confirms as: xruns with `ringFramesAtMiss=0`, no byte-wait line, `\Process(Wavee)\Page Faults/sec` high. Fix: F6 (optional `VirtualLock` of the PCM rings), F3.

**H6 — EcoQoS / power throttling (inferred; no opt-out exists).** Neither repo calls `SetProcessInformation(ProcessPowerThrottling)` or `SetThreadInformation(ThreadPowerThrottling)` (grep over both trees, §5 of the DSP sweep; the only `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` and `AboveNormal` hits outside audio are the compositor). On battery with the window in the background, Windows may throttle every non-MMCSS thread of the process — the producer, the fetch loop, the clock thread. Windows is documented to exempt processes it detects playing audio, so this is a secondary hypothesis; the opt-out costs one call. Fix: F4.

**H7 — transitions and seeks depend on Normal-priority threads (verified; "halts at every track change / after every seek under load").** `Buffering/Ready → Playing` happens only in `Advance` on the clock thread (`PcmAudioPlayer.cs:1397-1408`), which sleeps 15 ms between ticks and is at Normal priority; `RtRenderOnce` returns 0 until that flip (`:1289`). `SeekAsync` and `ReplacePreparedCoreAsync` complete through `Task.Delay(2)` polls on the ThreadPool (`:1203-1204, 1181, 797, 804, 816, 839, 847`), the app's gapless arm/commit runs on the 200 ms ThreadPool tick (`Playback.Audio.cs:2730-2760`), and the prepare chain holds a `SemaphoreSlim(3)` decoder slot with a **2 s timeout that throws** (`PcmAudioPlayer.cs:41-42, 99, 176-177`). Under load each of these stretches: a seek resumes hundreds of ms late, a gapless join is missed (`hardcut-b-open` in the log) and becomes a reopen, a seek storm can time out. Confirms as: `audio.seek.done … latencyMs=` large, `[gapless] hardcut-b-open wallGapMs=…`. Fix: F5, F7.

**H8 — MMCSS registration silently failing (verified mechanism, rare).** If `AvSetMmThreadCharacteristicsW("Pro Audio")` fails, the RT thread runs at managed `Highest` (priority 10) and the failure is only visible through `FormatSink` (`MmcssProAudio.cs:22-33`). Confirms as: the absence of `mmcss Pro Audio registration ok` in the session log. Fix: surface it on the Diagnostics card (§0.5).

Not a cause (checked): the WASAPI leaf is event-driven, not polled (`WasapiAudioDevice.cs:219-233`, `:469`); `Write` never blocks (`:254-258`); the RT path allocates nothing (`AudioTripwire`); `Thread.Sleep(15)` on the clock thread does not gate steady-state rendering (`RtRenderOnce` renders in `Playing` and `Stalled` without the clock thread, `:1289`); the compressed window is tens of seconds on a healthy link; the app's `RingSource.Read` blocks rather than returning 0, so the engine's `DecoderAudioSource` false-EOF latch (`AudioDecode.cs:401-408`, any 0-read ⇒ `Exhausted`) is not reachable from the stream path (`Playback.Audio.cs:1896-1915`).

### 0.4 Fixes

**F1 — register the producer (and the clock thread) with MMCSS.** The seam already exists: `IRtThreadCharacteristics.Enter()` (`AudioFeedThread.cs:14-18`) is implemented by `MmcssProAudio` for the RT thread only. Add a second task class and call it from the producer thread body.

```csharp
// [engine] Media/Playback/Audio/AudioFeedThread.cs — extend the seam (additive; NullRtThreadCharacteristics returns null for both)
public interface IRtThreadCharacteristics
{
    IDisposable? Enter();            // existing: "Pro Audio" for the RT feed thread
    IDisposable? EnterDecode();      // NEW: "Audio" — for decode-ahead producers and the clock thread
}

// [engine] FluentGpu.Windows/Wasapi/MmcssProAudio.cs
public IDisposable? EnterDecode()
{
    uint taskIndex = 0;
    nint handle = AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);   // MMCSS "Audio" task: elevated, below Pro Audio
    if (handle == 0) { WasapiAudioDevice.FormatSink?.Invoke($"mmcss Audio registration failed (win32 {Marshal.GetLastWin32Error()})"); return null; }
    return new Token(handle);
}

// [engine] RingAudioSource.cs — the producer takes an IRtThreadCharacteristics (threaded through AudioFeedThread.Wrap/WrapAdditional
// and PcmAudioPlayer.PrepareCoreAsync, which construct every ring) and registers itself for its lifetime
private void Produce()
{
    using var _ = _rt?.EnterDecode();      // null-safe: headless stays a plain thread
    try { while (!_producerCancellation.IsCancellationRequested) { ApplyQueuedSeek(); PumpAhead(); … } }
    …
}

// AudioFeedThread.ClockLoop — same registration, and AboveNormal as the floor when MMCSS is unavailable
_clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "FluentGpu.AudioClock", Priority = ThreadPriority.AboveNormal };
```

MMCSS "Audio" puts the producer in the same elevated band Windows uses for its own audio engine helpers; it is below "Pro Audio" (the RT thread keeps winning) and above every normal-class thread, boosted or not. MMCSS also exempts the thread from EcoQoS throttling, which is why F1 comes before F4.

**F2 — redesign starvation recovery: never stop the device; write silence; resume behind a real cushion with hysteresis; fade in.** Replace the two-phase machine (`PcmAudioPlayer.cs:1294-1329`) with: on an empty ring, submit **silence** for the block (the device clock and buffer keep running; the WASAPI engine would render silence for an unfed shared stream anyway, but submitting it keeps `_submittedFrames` and the position math honest), freeze the derived position by moving the device-frame origin back by the silence written, hold `_starvationPhase = 1`, and resume only when the ring holds a **resume cushion** `ResumeFrames` — 100 ms by default, doubled (up to the target) after every incident in the same session — through a 5 ms transport fade-in. The 120 ms `StartupReadinessFrames` rule stays for the first start and after a seek (`BufferingReady`, `:1348-1359`). `WsolaAudioSource.RecoveryFrames` (`WsolaAudioSource.cs:72`) is deleted: the hop-sized minimum was only ever a floor for the stretcher's own lookahead and must not cap the cushion (`Math.Max`, if anything).

```csharp
// [engine] PcmAudioPlayer.cs — RenderBlock, replacing :1507-1513 (RecoverStarvation :1294-1329 and the phase-2 arm at :1290 go away)
int readable = _mixer.ReadableFrames(frames, out var waitingFor);
if (readable <= 0 && waitingFor is not null)
{
    // Starved: keep the device running on silence. The incident's severity is the silence written; the content
    // timeline does not move (ConsumeSeq is untouched) and the derived position is frozen by rebasing the device origin.
    if (_starvationPhase == 0) { _starvationPhase = 1; _starvedRing = waitingFor; _starvedAt = Stopwatch.GetTimestamp(); }
    _starvedRing?.RecordStarvedFrames(frames);
    _mixBuf.AsSpan(0, frames * _format.Channels).Clear();
    _pendingFrames = frames; _pendingOffset = 0;
    Interlocked.Add(ref _deviceFrameOrigin, -frames);  // SessionAudioClock adds this to the device count (:331): position holds
    _mixer.PublishDrained(_mixer.ConsumeSeq);          // unchanged semantics for the Ended verdict
    return SubmitPending();                            // silence out; Start() still armed if the device never started
}
if (_starvationPhase != 0)
{
    // Hysteresis: resume only behind the cushion, never at the first block — a slow producer gets fewer, longer gaps
    // instead of a stop/start buzz. The cushion grows per incident (F3's GrowAhead widens the target in step).
    if (_mixer.PcmReady(_resumeFrames)) 
    {
        _starvationPhase = 0; _starvedRing = null; _starvedAt = 0;
        _resumeFrames = Math.Min(_resumeFrames * 2, _feed?.TargetAheadFrames ?? _resumeFrames);
        _transport.Retarget(0f, _mixer.ConsumeSeq, 1);                                        // from silence…
        _transport.Retarget(1f, _mixer.ConsumeSeq, Math.Max(1, _format.SampleRate / 200));    // …to unity over 5 ms
    }
    else { /* still starved: fall through to the silence path above on the next call */ }
}
```

with `private int _resumeFrames = _format.SampleRate / 10;` reset at `SetVoice`, and `CrossfadeMixer.PcmReady` comparing `ring.BufferedFrames` (or the stretcher's `ReadableFrames`) against `Math.Min(thresholdFrames, ring.TargetFrames)` **without** the `RecoveryFrames` clamp (`CrossfadeMixer.cs:253`). `Advance`'s `Stalled` publication (`:1419`, `:1414`) stays, so the UI still learns about a stall that outlasts a tick. The tests in `AudioFeedRaceTests.cs` / `AudioPrefillTests.cs` that pin the old machine are rewritten to pin: silence is submitted while empty, the device is never stopped, resume waits for the cushion, the cushion doubles per incident, the position does not advance during the gap.

**F3 — a deeper, adaptive decode-ahead cushion with low/high watermarks.** The ms-sized constructor already exists; raise the on-box defaults and let the feed grow the target after an incident.

```csharp
// [engine] FluentGpu.Windows/Wasapi/WasapiPcm.cs:58 — on-box sizing
var feed = new AudioFeedThread(session, sampleRate: session.Format.SampleRate, rt: new MmcssProAudio(),
    blockMs: 10.0, aheadMs: 2000.0, ringMs: 4000.0, maxBlocksPerWake: 3);   // was 500 / 1000

// [engine] RingAudioSource.cs — adaptive target: after an xrun with ringFramesAtMiss == 0, double the ahead depth up to the capacity
public void GrowAhead() => Volatile.Write(ref _targetFloats, Math.Min(_ring.CapacityFloats, _targetFloats * 2));
// called from AudioFeedThread.FeedOnce where RecordXrun fires with rings[i].Ring.BufferedFrames == 0 (:297-302)
```

Memory: 4 s × 48 kHz × 2 ch × 4 B = 1.5 MiB per voice, three voices max — negligible next to the 24 MiB compressed budget. The prepared ring's `readyFrames` (`PcmAudioPlayer.cs:78`) stays at 0.5 s so a hand-off is not delayed. The low-water edge (`_targetFloats / 2`) scales with the target automatically.

**F4 — opt the process out of power throttling and timer coalescing.** One call at startup in the Windows PAL (the engine's `Win32Platform` already owns the process-level Win32 setup), plus per-thread for the producer and clock threads as belt and braces.

```csharp
// [engine] FluentGpu.Windows/Pal/Win32Platform.cs (startup) — PROCESS_POWER_THROTTLING_STATE
[StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1, PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1, PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
static void OptOutOfPowerThrottling()
{
    var state = new PROCESS_POWER_THROTTLING_STATE
    {
        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
        StateMask = 0,   // 0 under a set control bit = "never throttle", "honour our timer resolution while backgrounded"
    };
    _ = SetProcessInformation(GetCurrentProcess(), PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
}
```

Do this only while a session is live if battery life in the idle app matters (a `Playback.Audio` hook on first session / last dispose); the simplest correct version is process-wide at start.

**F5 — take the Normal-priority clock thread off the resume path.** Two changes: (a) F1's `AboveNormal` + MMCSS "Audio" for the clock thread; (b) let the RT thread render as soon as play is requested and the ring is ready, publishing `Playing` from the clock thread afterwards (state is an *observation*, not a gate):

```csharp
// [engine] PcmAudioPlayer.cs:1289 — today
if (phase == 3 || (_state is not (PlaybackState.Playing or PlaybackState.Stalled)) || (!_playRequested && phase != 1)) return 0;
// proposed: Ready/Buffering may render once the prefill gate has passed — the clock thread's Publish(Playing) follows
bool mayRender = _state is PlaybackState.Playing or PlaybackState.Stalled
              || (_state is PlaybackState.Ready or PlaybackState.Buffering && _playRequested && !_transportHoldRequested && Volatile.Read(ref _prefillPassed) != 0);
if (phase == 3 || !mayRender || (!_playRequested && phase != 1)) return 0;
```

where `_prefillPassed` is set by `BufferingReady()` on the control side (it is computed there already, `:1397`) **and** by `SeekAsync` right before `Publish(Ready)` (`:1231`). The ≤ 15.6 ms + scheduling delay between "ready" and "audible" disappears from every seek and track change.

**F6 — GC and memory.** (a) Keep SLL but stop re-entering/restoring it per session: `App.cs:79` already sets it once; the per-session capture/restore (`PcmAudioPlayer.cs:1133-1142`, `1834-1838`) is redundant on the app and a hazard if two sessions overlap (A captures SLL, B captures SLL — fine today only because the process-wide value never differs). (b) Allocation on the fetch path: pool the per-range `CancellationTokenSource` (`Stream.cs:724`) and audit `FetchRangeAsync` for per-chunk arrays. (c) Optional: `VirtualLock` the PCM rings (≤ 4.5 MiB with F3) after raising the minimum working set with `SetProcessWorkingSetSize`; measure page faults first (H5 is inferred).

**F7 — replace the `Task.Delay(2)` polls with signalled completions.** `SeekAsync`'s "wait until the ring is ready" (`PcmAudioPlayer.cs:1203-1204`), `WaitAppliedAsync` (`:844-850`), `PostMixerCommandAsync` (`:833-842`), `FadeOutAsync`'s phase wait (`:746-747`) and `RingAudioSource.WaitUntilReadyAsync(int)` (`RingAudioSource.cs:118-128`) all poll with `Task.Delay(2)`, which fires at the system timer resolution (15.6 ms unless some other process has raised it) and lands on the ThreadPool. Give the producer a `ManualResetValueTaskSource`-style "ready" latch it sets when `BufferedFrames >= minimum` (it already computes this at `:98`), and give the mixer command queue an applied-sequence `AsyncAutoResetEvent` pulsed from `DrainMixerCmds` (`:970`, one `Set()` per drain — non-blocking, allowed on the RT thread as the one carved-out exception like the low-water wake, `AudioFeedThread.cs:303-306`). Each seek then saves 3–5 timer quanta (≈ 50–80 ms).

**F8 — (app) make the gapless hand-off and the parked seek independent of the 200 ms ThreadPool tick** — covered in §3/§10; the engine's `VoiceScheduler` sample-clock join is the natural home.

### 0.5 Glitch telemetry — make field reports measurable

What exists: the RT-side incident queue (`AudioFeedThread.cs:99-106, 355-388`: timestamp, `GapFrames`, `RingFrames`, `VoiceId`, `GcPauseTicksDelta`), the per-session counters `XrunCount`/`XrunFramesLost` (`PcmAudioPlayer.cs:509, 521`), the app's `[audio] xrun` warning per incident and `[audio-work]` line (`Playback.Audio.cs:2903-2941`), the `Metrics` record with `Xruns`/`XrunFramesLost` (`:4158-4175`), the stream `Stats` door (`Stream.cs:100-131`: ring waits/starves, cache hits, probes, CDN bytes), the body's `StallMs`, and the seek telemetry `audio.seek.done kind= latencyMs=` (`:4286-4298`). What is missing is a **per-session summary** the user can read and a structured event.

Add (all pure, engine-free, in `[app]Playback/Playback.Audio.cs` §21 `Metrics` + a new pure `GlitchLedger`):

```csharp
// [app] Playback/Playback.Glitch.cs — CORE, System only, alloc-free after construction
public sealed class GlitchLedger
{
    public int Incidents, ProducerStarves, DeviceLate, GcImplicated, ByteWaits;
    public long FramesLost, LongestStallMs, LastStallMs, LastStallAtUnixMs;
    public long LongestByteWaitMs;

    /// <summary>Fold one RT incident. PURE.</summary>
    public void Record(in AudioFeedThread.XrunEvent ev, int rate, long nowUnixMs)
    {
        Incidents++;
        FramesLost += ev.GapFrames;
        long stallMs = rate > 0 ? ev.GapFrames * 1000L / rate : 0;
        LastStallMs = stallMs; LastStallAtUnixMs = nowUnixMs;
        if (stallMs > LongestStallMs) LongestStallMs = stallMs;
        if (ev.RingFrames == 0) ProducerStarves++; else DeviceLate++;
        if (ev.GcPauseTicksDelta > 0) GcImplicated++;
    }

    /// <summary>Fold the stream layer's byte-wait counters (the producer blocked on bytes). PURE.</summary>
    public void RecordByteWait(long stallMs) { ByteWaits++; if (stallMs > LongestByteWaitMs) LongestByteWaitMs = stallMs; }

    /// <summary>The one-line verdict a Diagnostics card and a bug report need. PURE.</summary>
    public string Verdict() => Incidents == 0 ? "clean"
        : GcImplicated * 2 >= Incidents ? "gc-pauses"
        : ByteWaits > 0 && ProducerStarves > 0 ? "network/disk-starved"
        : ProducerStarves > DeviceLate ? "producer-starved (cpu)"
        : "device-late";
}
```

Wire: `DrainXruns` (`Playback.Audio.cs:2903`) calls `s_ledger.Record(ev, rate, …)` per incident; `FoldStall` (`:2868`) calls `RecordByteWait(stallMs)` on the `Recovering` edge; emit **`Log.Event(Warn, "audio", "audio.glitch", …)`** with `stallMs`, `ringFramesAtMiss`, `gcTicks`, `verdict`, `posMs`, `trackId`, plus **`audio.session.summary`** at session end with the ledger. Show a **"Playback health" card** on the Diagnostics page: incidents, longest stall, verdict, MMCSS state (H8: read it from the `FormatSink` line or expose a `bool MmcssRegistered` on `AudioFeedThread`), producer/clock thread priorities, ring depth target and current fill (`PcmAudioSession.BufferedFrames`, `:361-370`), device buffer frames (`DevicePaddingFrames`, `:501`), decode × real-time per codec (`CodecXRealtime`, `:4271`), and the stream `Stats`. Tests: `GlitchLedgerTests` over synthetic `XrunEvent`s (verdict table, longest/last, GC attribution) — pure, no source text.

### 0.6 A deterministic load-test harness

Goal: reproduce "heavy computer use" on demand and report underruns, so a fix can be measured rather than believed. Two layers.

**Layer 1 — pure engine stress model (xUnit, `FluentGpu.Engine.Tests`).** The feed is already drivable without threads (`FeedOnce` / `WorkerPumpOnce` / `ControlTickOnce`, `AudioFeedThread.cs:281, 395, 456`). Add a `StressSchedule` that models a producer being descheduled: a scripted sequence of (producer-runs-for N ms, producer-absent-for M ms) windows driven against a headless session with `HeadlessAudioEndpoint` and a `SignalGeneratorSource` behind a `RingAudioSource` whose producer is pumped manually. Assert: with F2+F3, an absence of 600 ms produces exactly one incident of ≤ 110 ms gap and no device stop; a sustained 1.2× real-time producer never produces the stutter loop (incident count ≤ 1 per 10 s); with the old constants the same script shows the §0.2 loop (a regression pin). Also a `GcSuspensionModel` that stops *both* producer and RT for T ms and asserts the gap equals max(0, T − device buffer).

**Layer 2 — on-box stress runner (VerticalSlice phase, or a `--stress-audio` flag in the app's headless probe `[app]Screens/Diagnostics.Probe.cs`).** A pure `LoadProfile` record (burner threads per priority class, memory pressure MiB, minimize window, battery-saver emulation) and a runner that: spins N `Thread`s at each of `BelowNormal/Normal/AboveNormal/Highest` doing tight arithmetic (N = logical cores per class), allocates and touches a `byte[]` ring of the requested size every 50 ms (memory pressure + GC churn), minimizes the window via the PAL, optionally toggles `PROCESS_POWER_THROTTLING` **on** for the process to emulate battery saver, plays a known test tone or a cached track for 60 s, then prints the `GlitchLedger` summary and the per-incident lines. Pass criterion: 0 incidents at Normal/BelowNormal load with F1–F3; ≤ 1 at AboveNormal load; the baseline (today) is expected to fail at AboveNormal and to show the stutter loop. This belongs in `ops/` or the probe, never in the shipping UI.

---

## 1. Findings table

No P0 was found: nothing read guarantees a crash or an always-audible defect on the default path. The P1 rows are correctness failures that are audible or user-visible under a specific but realistic condition. File keys: `PA` = `[app]Playback/Playback.Audio.cs`, `PC` = `[app]Playback/Playback.cs`, `PH` = `[app]Playback/Playback.Host.cs`, `PT` = `[app]Playback/Playback.Transitions.cs`, `ST` = `[app]Spotify/Spotify.Audio.Stream.cs`, `SA` = `[app]Spotify/Spotify.Audio.cs`, `OG` = `[app]Playback/Playback.Audio.Ogg.cs`, `VO` = `[app]Playback/Playback.Audio.Vorbis.cs`, `FL` = `[app]Playback/Playback.Audio.Flac.cs`, `FK` = `[app]Playback/Playback.Audio.Flac.Kernels.cs`, `UI` = `[app]Shell/Shell.PlayerBar.UI.cs`, `RU` = `[app]Shell/Shell.PlayerBar.cs`, `PS` = `[engine]…/Audio/PcmAudioPlayer.cs`, `FT` = `[engine]…/Audio/AudioFeedThread.cs`, `RS` = `[engine]…/Audio/RingAudioSource.cs`, `CM` = `[engine]…/Audio/CrossfadeMixer.cs`, `WS` = `[engine]…/Audio/WsolaAudioSource.cs`, `DS` = `[engine]…/Audio/DspStages.cs`, `BQ` = `[engine]…/Audio/Biquad.cs`, `LR` = `[engine]…/Audio/LinearResampler.cs`, `AD` = `[engine]…/Audio/AudioDecode.cs`, `WD` = `[engine]FluentGpu.Windows/Wasapi/WasapiAudioDevice.cs`, `WN` = `…/WasapiFormatNegotiation.cs`, `ME` = `[engine]…/MediaEffects.cs`, `CD` = `Wavee.Sdk/Streams/ChunkDiskCache.cs`.

| ID | Sev | Area | Where | What is wrong | Evidence | Fix |
|---|---|---|---|---|---|---|
| H-1 | P1 | halts / threads | `RS:79`, `FT:487` | The decode-ahead producer and the clock thread are plain managed threads (AboveNormal, Normal) with no MMCSS and no power-throttling opt-out; only the RT thread is protected | `Priority = ThreadPriority.AboveNormal` (`RS:79`); `_rt.Enter()` only in `RtLoop` (`FT:522`); grep: no `AvSetMmThreadCharacteristics` elsewhere | §0.4 F1 (MMCSS "Audio" for producers + clock), F4 |
| H-2 | P1 | halts / underrun | `CM:253-255`, `WS:72`, `PS:1294-1329` | Starvation recovery resumes at one WSOLA hop (20 ms), not the intended 120 ms cushion, with no hysteresis, and stops/resets/starts the WASAPI client per cycle: a slow producer produces a stop/start stutter loop | `RecoveryFrames => Math.Min(requested, _hop)`; `_hop = sampleRate / 50`; `_out.Stop(); … Reset();` (`PS:1305-1307`) then `Start()` (`:1566-1572`) | §0.4 F2 |
| H-3 | P1 | halts / device loss | `PS:1278-1280`, `:1300-1302`, `WD:190` | A dead sink returns `WritableFrames == -1`, which is never `>= CapacityFrames`, so transport phase 2 and starvation phase 1 wait forever; `RecordSinkFailure` is reachable only from `RenderBlock`/`SubmitPending` (`:1501`, `:1558`): `FadeOutAsync`/`PauseAsync`/`SeekAsync` hang and no rebuild is requested until a device notification | `bool drained = buffered.WritableFrames >= buffered.CapacityFrames` | treat `< 0` as drained and call `RecordSinkFailure()` at both sites |
| H-4 | P1 | EQ / thread safety | `PS:1035, 1045, 1076, 1083` vs `DS:144-181` and `:184-231` | `ReconcileEffects` (clock thread, `PS:1379`) mutates the live `EqStage` the RT thread is filtering: `SetBands` with a count change reallocates the state arrays (`DS:149-153`) under a running `Process` (index out of range, block dropped), and `StartRamp`'s `Array.Copy` and the `(_active,_pending)` swap race the RT loop (click / coefficient step) | `if (!eq.Enabled.Peek() …) { _voiceEq.SetBands(ReadOnlySpan<BiquadBand>.Empty, …) }` | route EQ changes through the mixer command SPSC (`PS:310-312, 864-880`, applied in `DrainMixerCmds`) carrying precomputed `BiquadCoeffs[]`; never reallocate a live stage |
| S-1 | P1 | seek / Vorbis | `PA:1814-1823` vs `:4083-4096` | A second seek's `InterruptPendingRead` (`PA:608-616`) makes seek #1's landing `ReadWindowAt` fail, so `_eof = true`; `Read` returns 0 (`:1741`), `DecoderAudioSource` latches EOF (`AD:401-408`), the ring becomes `Exhausted`, `Ended` fires and the reducer advances the track mid-scrub. The FLAC arm deliberately does not latch (`:4093`) | `_eof = true; Log.Warn("audio", $"audio.seek.short codec=vorbis …")` | mirror FLAC: on `_interrupted`, keep `_eof` false and let `Read` serve `Silence` until the next `Seek` |
| S-2 | P1 | stream / timeouts | `ST:537`, `:724`, `:1645-1654` | The only range deadline is a 60 s **total** `CancellationTokenSource`; a socket that stops sending mid-range is abandoned only at 60 s, while the ring starves in 8 s rounds and the UI says "Reconnecting" | `const int RangeTimeoutMs = 60_000;` / `cts = new CancellationTokenSource(RangeTimeoutMs);` | idle timeout (`cts.CancelAfter(8_000)` re-armed after every successful `ReadAsync`), and let a starving `ReadAt` cancel an in-flight range whose span contains `Want` |
| U-1 | P1 | reducer / seek | `PC:1881-1884` vs `:1950` | `DoSeek` bumps the epoch then stamps `fx.SeekEpoch = s.Epoch`; `DoAudio` drops any signal whose epoch is not `LoadEpoch`, so every user seek's `Seeked` confirmation is discarded; `PosQpc` stays at the issue time and the painted position runs ahead of audio by the seek latency until the next 1 s `Position` sample steps it back | `Bump(ref s); … fx.SeekEpoch = s.Epoch;` / `if (i.Epoch != s.LoadEpoch) return;` | stamp `fx.SeekEpoch = s.LoadEpoch` (or always post under `s_loadEpoch`) and add a seek generation (§3.4) |
| U-2 | P1 | pump / position | `PA:2561-2576`, `:2801-2814`, `PC:1967-1972` | Only a session-less seek parks `s_pendingSeekMs`; a normal seek lets `ActivePositionMs()` keep reading the engine's pre-seek clock, and the tick's 1 s `Position` sample can land inside the seek window, so `PosMs` regresses (the thumb snaps back). A double seek on the serialized chain always lands A's position after B was shown | `if (p is null or sess is null) { lock (s_gate) s_pendingSeekMs = ms; return; }` | park the target for **every** seek (`SeekGate.ReportedPositionMs` already does the right thing when it is parked); drop `Position` reports stamped before the latest seek generation |
| U-3 | P1 | reducer / Connect | `PC:1866`, `PH:1025`, `UI:1683` | A seek while another device owns playback only forwards `seek_to`; `PosMs/PosQpc` are untouched, and the UI's drop-point hold is released by the committing drain's own `PositionMs` publish, so the thumb snaps back to the mirror's extrapolated old position until the cluster echoes (0.3–2 s) | `if (!s.RoutesLocal) { Forward(…RemoteCmd.SeekTo…); return; }` | set `s.PosMs = SeekTarget.Clamp(i.IntArg, s.DurationMs); s.PosQpc = i.NowMs;` in the forward branch; release the UI hold only on a report within ±250 ms of the target |
| U-4 | P1 | volume / mute | `PC:2498-2503`, `PH:1028, 1193` | With the sink muted and the volume at 0, unmute is a no-op (`Muted` folds `s_sinkMuted` or `Volume <= MuteFloor`); dragging the volume up never clears the sink mute | `if (!foreign && (i.IntArg & Input.SinkBit) != 0) { fx.Mute = true; fx.MuteOn = muted; return; }` | when unmuting at `Volume <= MuteFloor` restore `MuteRestoreVolume`/`UnmuteDefault`; in `DoVolume` emit `Mute=false` when `v > MuteFloor` |
| P-1 | P1 | FLAC | `FL:821-822`, `FK:171, 187` | The side channel of a 32-bit stereo stream is 33 bits (RFC 9639 §9.2); the bit reader truncates `Read(33)` to 32 bits and `ReadSigned(33)` shifts by a masked −1: full-scale garbage with a passing CRC-16 | `int bps = h.Bps + (… ? 1 : 0);` / `(int)(Read(n) << (32 - n)) >> (32 - n)` | `if (bps > 32) return FrameResult.Unsupported;` (skip + one-shot log) or a `long` side-channel path |
| P-2 | P1 | Vorbis | `VO:629-630` | Floor 0, even order: the spec's `p = (1−cos ω)/2 ∏…`, `q = (1+cos ω)/2 ∏…` (Vorbis I §6.2.3) is coded without the ½, so the curve is √2 too quiet (tens of dB on loud bands); the odd branch is correctly normalised | `p = 1f - w; q = 1f + w;` | `p = 0.5f * (1f - w); q = 0.5f * (1f + w);` + a synthetic floor-0 test (no fixture exists) |
| P-3 | P1 | stream / HTTP 200 hosts | `ST:284-291`, `:1638-1643`, `:1674-1682`, `PA:169` | A host that ignores `Range` (200) is served by re-reading from byte 0 and skipping; past `MaxSkipBytes = 4 MiB` the range is refused, `Refusing` is set and `Fault.Network` fires after 6 s. Podcast enclosures only (Spotify's CDN always 206s) | `MaxSkipBytes = 4L << 20` | keep the one 200 reply open as a sequential body ("streaming body" mode) |
| H-5 | P2 | halts / power | both repos | No `SetProcessInformation(ProcessPowerThrottling)` / `SetThreadInformation(ThreadPowerThrottling)` anywhere; every non-MMCSS audio thread is eligible for EcoQoS on battery in the background | grep (§0.3 H6) | §0.4 F4 |
| H-6 | P2 | halts / transitions | `PS:1289`, `:1397-1408`, `FT:487, 562` | `Buffering/Ready → Playing` happens only in `Advance` on the Normal-priority clock thread; the RT thread renders nothing until then, so every start, seek and prepared replace waits a 15.6 ms+ tick (more under load) | `if (phase == 3 … _state is not (Playing or Stalled) …) return 0;` | §0.4 F5 |
| H-7 | P2 | halts / control plane | `PS:746-747, 816, 839, 847, 1203-1204`, `RS:118-128` | Transport, replace and seek completion poll with `Task.Delay(2)` (fires at the 15.6 ms system tick, on the ThreadPool): 3–6 hops per seek, more per hand-off | `await Task.Delay(2).ConfigureAwait(false);` | §0.4 F7 |
| H-8 | P2 | lifecycle | `PS:41-42, 99, 176-177`, `AD:420`, `RS:110, 368` | Three decoder leases and a 2 s `WaitAsync` that **throws** `TimeoutException`; a lease is released only when the producer's `finally` disposes the inner source, so producers blocked in an uncancellable read hold them: a seek storm or a stuck prepare makes the next open fail | `SemaphoreSlim _decoderSlots = new(3, 3)` | count only in-flight open/prepare calls, or raise the count and log the stuck producer |
| H-9 | P2 | halts / headroom | `FT:137-138`, `WasapiPcm.cs:58`, `PS:1399-1400` | 500 ms of decoded PCM is the entire cushion; `BufferHealth` is a hard-coded 30 s constant | `aheadMs = 500.0, ringMs = 1000.0`; `sink.Buffer(new BufferHealth(…, TimeSpan.FromSeconds(30), …))` | §0.4 F3; publish real `BufferHealth` from `ring.BufferedFrames` |
| H-10 | P2 | halts / ThreadPool | `PA:735-750`, `:578`, `ST:686`, `:2089`, `[app]Spotify/Spotify.Audio.KeyBook.cs:120` | The pump chain, prepares, the fetch loop and the 200 ms tick all run on the shared ThreadPool; the chain and prepares **block** inside (`pending.Wait`, `WaitForFirstBody`, `Done.Wait`) | `s_tail.ContinueWith(…, TaskScheduler.Default).Unwrap()`; `_loop = Task.Run(LoopAsync)` | a dedicated long-running thread for the chain; `LongRunning` prepares; the fetch loop on its own thread |
| H-11 | P2 | pump / tick | `PA:2671, 2707-2859` | The 200 ms `Timer` callback has no re-entrancy guard; a slow tick (xrun log burst) overlaps the next and the gapless commit reads shared state unlocked | `s_ticker ??= new Timer(static _ => Tick(), …)` | `Interlocked.CompareExchange` latch at the top of `Tick` |
| H-12 | P2 | stream / alloc | `ST:1543-1549`, `:1136-1202` | `WidenWhenProven` runs inside `Body.ReadAt` (on the producer thread) and `Ring.Grow/Rehash` rent slots and allocate four arrays under `_gate` (≈ 7 MB for a FLAC at the 10 s mark) | `Ring.Grow` from `ReadAt` | dispatch `Grow` to the fetch loop |
| H-13 | P2 | GC | `PS:1133-1142`, `:1834-1838` | Per-session `SustainedLowLatency` capture/restore is not ref-counted; benign in Wavee only because `App.cs:79` pins the mode process-wide | `_prevGcLatencyMode = GCSettings.LatencyMode;` | set once at backend construction; delete the per-session code |
| S-3 | P2 | seek / latency | `PS:1275-1288`, `:1187`, `:1498-1503` | Every seek fades 5 ms then waits for the **whole** device buffer (kept ≈ full by `RenderBlock`) to drain before `Stop/Reset`, then rebuffers 120 ms, then waits for the clock thread: ≈ 250–400 ms of silence floor for a ring-resident seek (§3.1) | `bool drained = … WritableFrames >= CapacityFrames` | §3 (seek as a voice swap) |
| S-4 | P2 | seek / coalescing | `PA:608-616`, `:735-752`; `PS:1179, 1184` | No app-side coalescing: every drag/keyboard event is a full serial engine seek; the chain serialises them so the engine's `_seekRevision` can never supersede | `Enqueue(() => SeekCoreAsync(ms, epoch, t0, before));` | last-write-wins mailbox; one in-flight seek; cancel the previous prepare |
| S-5 | P2 | gapless | `PA:2574`, `:2598-2600`; `PT:888, 852` | A seek inside the last 1.5 s abandons the committed join and nothing re-prepares (`DoEndingSoon` returns because `EndingSoon` is already set): hard cut at the boundary | `AbandonPendingJoin(sess, "seek")` | post a "prepare lost" input that clears `NextArmed` so `ArmNext` re-emits |
| S-6 | P2 | seek / position | `PS:962`, `:1222-1224`, `:1614-1626` | Between `CmdReset` (zeroes `_submittedFrames` on RT) and `_position.Rebase` (control thread) `PublishPosition` projects with `SubmittedFrameLimit = 0`, so the published position dips to the previous anchor for 100–300 ms | `_submittedFrames = _playedFrames = _deviceFrameOrigin = 0;` | suppress `PublishPosition` while `_replacementGate.CurrentCount == 0`, or snapshot origin+anchor atomically |
| S-7 | P2 | Ogg / resync | `OG:371`; `PA:1686, 1691`; `VO:1540-1548` | A page-sequence hole or a bad packet is swallowed: the open packet is dropped and the adapter `continue`s without re-priming the decoder, so the next good packet overlap-adds onto a window two packets back (click) and the clock runs short until the next granule pins it | `if (_seqKnown && p.Sequence != _expectSeq) _packetLen = 0;` | surface `Next.Hole`; on hole/bad packet call `dec.Prime()` and pad the clock gap |
| S-8 | P2 | Vorbis / seek | `OG:658, 671`; `PA:1797` | With no total length (`_tail < 0` and no duration) the planner takes the Linear tier from the **first** audio page and decodes forward unbounded (module streams only) | `p.Tier = p.HiGranule > 0 ? SeekTier.Estimate : SeekTier.Linear` | bisect on bytes when the stream length is known; start from the index's best Lo |
| S-9 | P2 | FLAC / seek | `PA:3902-3903`, `FL:722-723`, `PA:4065` | The probe window is grown only when `MaxFrame > 64 KiB`; `Observe` needs ≥ 2 × `MaxFrame`, so for `MaxFrame` in (32, 64] KiB a probe returns `NoFrame`, the adapter breaks and decodes forward from the bracket's low edge (the file start without a SEEKTABLE) | `if (_si.MaxFrame > (uint)_win.Length) Array.Resize(…)` | size the window to `2 × MaxFrame`; treat `Overrun` as "skip candidate" |
| S-10 | P2 | stream / seek | `ST:1013`, `:1239` | A seek into the range currently in flight cancels it and re-requests the same bytes (`Holds` ignores `IsPending`) | `resident = residentElsewhere or Holds(start, end);` | when the probe lies inside the pending range, bump the epoch without cancelling |
| S-11 | P2 | stream / seek | `ST:1266`, `:1077-1083` | Post-seek prefetch is forward only; nothing behind the landing is protected (direct-mapped slots overwrite on collision), so the typical "overshot, drag back a little" costs a round trip | `TryPlan` from `AlignDown(Cursor)` | plan 2 slots behind the landing; protect the keep-behind slots in `Land` (§3.3) |
| S-12 | P2 | stream / seek | `ST:713-725` | `FillFromDisk` runs **before** `_inFlight` is registered, so a seek during it cannot cancel the stale fill; the probe queues behind a 512 KiB stale range | epoch check, then `FillFromDisk`, then `lock { _inFlight = cts }` | register `_inFlight` first; re-check the epoch after the disk fill |
| S-13 | P2 | stream / metered | `ST:1294-1299`, `:922-927` | For windows ≤ 512 KiB (metered 320k/160k, 96k, prepared) the hysteresis low-water collapses to `max(64 KiB, librespot ≈ 80 KiB)`: the refill is one full 512 KiB range requested with 1.6–2 s of audio left | `Math.Max(SlotBytes, Math.Max(librespot, _windowBytes - Fetcher.MaxRangeBytes))` | `LowWater ≥ _windowBytes / 2`, or cap the range at half the window |
| P-4 | P2 | FLAC | `FK:766-770` | Multichannel fold-down uses the wrong RFC §9.1.3 order for 4 ch (BL treated as centre, BR dropped) and 7 ch | `int c3 = channels >= 3 ? 2 : -1;` | a per-channel-count weight table |
| P-5 | P2 | FLAC / local | `PA:3552-3553`, `FL:171` | A FLAC with an ID3v2 prefix is sniffed as MP3 and never opens; libFLAC skips the tag | `if (… head[0] == 'I' && head[1] == 'D' && head[2] == '3') return Format.Mp3;` | parse the syncsafe size and re-sniff after it |
| P-6 | P2 | stream / faults | `ST:1656-1682`, `:1444`, `PA:169` | A wire fault after some slots landed, followed by refusals, sets `_refusedAt`, so `Refusing` and the pump's 6 s fast-fail apply to a slow-but-alive link | `Volatile.Write(ref _refusedAt, …)` after `_landedAt` | return `FetchResult(landed)` when any slot landed; set `_refusedAt` only when nothing landed |
| P-7 | P2 | stream / external | `ST:2184`, `:1308`, `:1267`, `:978` | An external body with neither `Content-Length` nor a probed length keeps `duration × nominal rate` as a hard wall: reads past it miss forever, 8 s starve loop, `Fault.Network` at 90 s | `AdoptLength(-1)` returns early (`:1875`) | while `!LengthKnown`, serve/plan beyond the estimate; a 0-byte reply at the asked offset establishes EOF |
| N-1 | P2 | normalization | `PA:1133`; `SA:373-374`; `DS:328` | The Ogg boost cap is `factor × peak ≤ 1.0` (0 dBFS) and the lossless cap is −1 dBFS, while the terminal limiter's ceiling is −1.5 dB with instant attack: every boosted track whose post-gain peaks land in (−1.5, 0] dBFS is limited per sample (pumping on transients) | `if (peak > 0f && … factor * peak > 1f) factor = 1f / peak;` | cap at the limiter ceiling (0.841) in one shared constant |
| N-2 | P2 | normalization | `SA:396, 405-411`, `:370-376` | Only "Normal" exists: pregain 0 over the header's `track_gain_db`; no Quiet (−23) / Loud (−11), no album mode: `album_gain_db` @152 / `album_peak` @156 are never read (`HeaderGainBytes = 152`) | `public const int HeaderGainBytes = 152;` | §5.1 |
| N-3 | P2 | normalization / local | `FL:266-271`, `PA:3916-3917`, `:3247` | `REPLAYGAIN_*` Vorbis comments are skipped; local files open with `GainDb 0, Peak 0`: no loudness for dropped FLAC/Ogg | `ParseVorbisComment` keeps only TITLE/ARTIST/… | parse the four tags into `GainLinear(gainDb, peak)` |
| E-1 | P2 | EQ / numerics | `BQ:110-125` | Direct Form I in `float` with 31/62 Hz poles (r ≈ 0.999): the recursion's round-off gain is ≈ +70 dB over float rounding, ≈ −66 dB LF noise relative to the signal, worse at 96/192 kHz (derived, not measured) | `float _x1, _x2, _y1, _y2;` | `double` state (coefficients stay float) or a TPT SVF |
| E-2 | P2 | EQ / limiter | `BQ:120-122`, `DS:373` | No denormal protection: the biquad state decays into subnormals on digital silence and the limiter release passes through the subnormal range for ≈ 0.8 s after every event; .NET does not set FTZ/DAZ: RT CPU spikes | `_gain = targetGain + (_gain - targetGain) * _releaseCoeff;` | flush tiny values to 0; snap `_gain` to 1 within 1e-6 |
| E-3 | P2 | limiter | `DS:363-376`, `:328`, `AudioGraph.cs:197` | Instant attack, zero lookahead, sample-peak detection: an instantaneous per-sample gain drop is a hard-knee waveshaper on transients; inter-sample peaks up to ≈ +3 dB pass; the parameter is named `dBTP` but is dBFS sample peak | `if (targetGain < _gain) _gain = targetGain;` | 2–5 ms lookahead + attack smoothing (report `LatencySamples`), 4× oversampled peak; or rename |
| E-4 | P2 | EQ / headroom | `PS:250-267`, `PA:707-716` | ±12 dB per band with no preamp/headroom: heavy boosts drive the brickwall continuously | `BuildGraphSpec`: `EqSpec` only | automatic preamp `−max(0, max band gain)` as a per-voice `GainSpec` |
| E-5 | P2 | EQ / threads | `PS:1063-1071`, `:1050` | `RebaseReplayGain` writes the RT-owned voice list and `SetTargetBalance` writes a struct param from the clock thread (dead today: `NormMode.Off`; balance unused) | `span[i].ReplayGainScalar = rg;` over `_mixer.VoicesSpan` | mixer commands |
| Q-1 | P2 | quality / SRC | `LR:117`; `PA:1536, 1989, 3907` | Linear interpolation is the only resampler and every 44.1 kHz Spotify source on a 48 kHz device goes through it: sinc² response, ≈ −3.4 dB at 15 kHz, first images at ≈ −22 dB | `dst[ob + c] = s0 + (s1 - s0) * frac;` | §6.1 windowed-sinc polyphase |
| Q-2 | P2 | WSOLA | `WS:141, 148, 156` | Returning to rate 1.0 discards the pending overlap tail without a blend: a discontinuity at the speed toggle | `_hasTail = !unity;` | blend the first unity hop against `_tail` |
| Q-3 | P2 | channels | `AD:240-241` | The engine WAV decoder folds > 2 channels by dropping C/LFE/surround (`ChannelStage`'s averaging is unreachable: the graph is fixed stereo) | `float r = _srcChannels >= 2 ? SampleAt(inBase, 1) : l;` | ITU-R BS.775 fold-down |
| V-1 | P2 | volume / perf | `PH:661-667`; `[app]Platform/Platform.cs:940-949` | Every drain with `fx.Volume` writes the registry (`Settings.Set(SavedVolume)`), bumps `SettingsEpoch` (re-rendering every settings subscriber) and announces a PUT (debounced 50 ms ⇒ ≈ 20/s during a drag) | `Platform.Settings.Set(Platform.Keys.SavedVolume, s_fx.VolumeValue);` | persist on drag end; coalesce the PUT leading+trailing 400 ms |
| V-2 | P2 | volume / Connect | `PC:1892-1899`, `:2253`; `PH:922-926` | Remote volume: one HTTP PUT per drain, and `MirrorRemote` adopts the cluster's volume from every cluster with no "recent local command" window, so the slider snaps back on a stale echo mid-drag | `if (r.Volume >= 0) s.MirrorVolume = …` | trailing coalescer; ignore cluster volume for ≈ 1 s after a local send |
| U-5 | P2 | position | `PC:1105-1112`, `:1978-1988` | `Position(now)` extrapolates through a buffering stall (`Phase` stays `Playing`), then `Buffered` re-anchors `PosQpc` against the stale `PosMs`: the painted position jumps back by the stall length | `if (Phase != Phase.Playing) return PosMs;` | also return `PosMs` while `Buffering`; freeze `PosMs` on the `Buffering` edge |
| U-6 | P2 | keyboard | `UI:645-658`, `[app]Shell/Shell.PlayerBar.Podcast.UI.cs:16-33`, `RU:425-446` | Held arrow keys issue an engine seek (and a PUT) per OS key repeat (~30/s); the accumulator makes a 1 s hold jump ≈ 300 s | only `Toggle` checks `!e.IsRepeat` | §4.5 keyboard scrubbing |
| U-7 | P2 | seek bar | `UI:1808-1820`, `:1660-1673` | No scrub preview on the time labels and no hover tooltip for tracks (episodes get a chapter timestamp) | `BarTimeText.Label` reads `Playback.PositionMs.Value` only | §4.6 |
| R-1 | P2 | crypto / perf | `SA:828-852` | AES-CTR decrypts one 16-byte block per `EncryptEcb` P/Invoke and creates an `Aes` + `key.ToArray()` per call (≈ 4096 calls per 64 KiB slot; on the fetch task) | `using Aes aes = Aes.Create(); … aes.EncryptEcb(counter, keystream, PaddingMode.None)` in the per-block loop | one `Aes` per body; encrypt a 64 KiB counter block at once; vectorised XOR |
| R-2 | P2 | shutdown | `PA:524-525` | `s_tail.Wait(5_000)` and `DisposeAsync().AsTask().Wait(5_000)` on the UI thread at exit; a chain blocked in a starving open holds the UI 5 s | `try { s_tail.Wait(5_000); } catch { }` | close bytes first (`ReleaseBlockedChain`), then wait |
| U-8 | P3 | seek bar | `PC:252-262`, `RU:536-539` | Drag-to-end holds `frac = 1.0` but `DoSeek` lands at `duration − 750 ms`: a visible step back after the hold | `TailGuardMs = 750` | run the UI commit target through `SeekTarget.Clamp` |
| U-9 | P3 | volume keys | `UI:655-656` vs `Slider.cs:373-374` | Dock Up/Down = ±0.05 but the focused `Slider` steps `range/100` | — | `SmallChange = 0.05f` |
| U-10 | P3 | SMTC | `[app]Playback/Playback.Os.cs:165` | `PlaybackRate` pinned to 1.0; the OS flyout drifts at episode speeds between pushes | `smtc.PlaybackRate = 1.0;` | push `ContentRate` |
| U-11 | P3 | seek | `PA:2603` vs `PS:1201, 1214` | `Seeked` carries the **requested** ms, not the achieved frame | `PostSignal(AudioSignal.Seeked, …, ms)` | report `achieved` |
| E-6 | P3 | EQ | `DS:156` | A topology change (enable with non-flat gains) swaps coefficients instantly: a click | `_rampRemaining = 0;   // fresh topology — no ramp source` | ramp from identity coefficients |
| E-7 | P3 | EQ | `BQ:25`, `ME:32` | Shelves use the Q-form α with the default Q = 1 (overshoot); no S-slope or Q↔BW conversion. Irrelevant today (peaking only) | `alpha = sw / (2.0 * q)` | add the S form when shelves are exposed |
| E-8 | P3 | limiter / perf | `DS:363-376` | Processes every sample even when the block peak is far below the ceiling and `_gain == 1` | — | one max-abs pass, early copy |
| E-9 | P3 | limiter / design | `PS:1529-1531` | The limiter sits after master volume and balance, so limiting depends on the slider (below ≈ 0.84 it never engages): defensible for DAC protection, but the spec's "catch a positive gain or EQ boost" intent wants it pre-volume | `_masterGain.Process`, `_masterChannel.Process`, `graph.RenderMaster` | document, or move pre-volume (the ≤ 1 master gain keeps the guarantee) |
| Q-4 | P3 | WASAPI | `WN:41, 47` | int16/int24 conversion truncates with no dither (rare: the shared-mode mix format is almost always float32) | `(short)(s * 32767f)` | TPDF dither for 16-bit endpoints |
| Q-5 | P3 | WASAPI | `WD:422`, `MmDeviceWatcher.cs:129` | `eConsole` role; media players conventionally follow `eMultimedia` (identical on default setups) | `GetDefaultAudioEndpoint(eRender, eConsole, …)` | `eMultimedia` |
| Q-6 | P3 | device switch | `PS:1699`, `:1730`, `:1693-1708` | `oldSink.Stop()` is an abrupt cut of ≈ 100 ms queued audio; the rendered-unsubmitted block is dropped; in the `onRebuilt is null` branch the mixer resumes ≈ 100 ms ahead of the re-anchored position (Wavee takes the hold+reload branch, which avoids it) | `try { oldSink.Stop(); } catch { }` | fade the old sink over one block; rewind `ConsumeSeq` by the dropped frames |
| Q-7 | P3 | SRC / gapless | `LR:109, 123` | The last partial output frame is never flushed at EOF; each track owns a fresh resampler, so a butt-join loses < 1 source frame | `if (i1 > inFrames - 1) break;` | a proper SRC with an EOF pad fixes it for free |
| R-3 | P3 | RT / idle | `FT:528`, `PS:1332-1336` | While paused (phase 3, device stopped) the MMCSS thread still wakes every 10 ms to do nothing | `_session.WaitForOutput(_outputWake, … _blockPeriodMs)` | `Timeout.Infinite` when `_transportPhase == 3 && !_started` |
| R-4 | P3 | effects | `ME:89, 181, 211` | `PreservePitchOnRate` has no consumer; WSOLA is unconditional | grep | remove or wire |
| R-5 | P3 | FLAC | `PA:3864, 3992-4000, 4055` | `_skipSamples` is never set above zero; the block that uses it is unreachable | `_skipSamples = 0;` | delete |
| R-6 | P3 | stream / stats | `ST:587`, `:109-112` | `Fetcher.Faults` is not exposed in `Stream.Stats`; the ping sample includes dead-mirror attempts (`:731`, `:1637`) | — | expose; measure from the successful mirror's start |
| P-8 | P3 | FLAC | `FL:352, 365` | The 15-bit sync (RFC 9639 §9.1) is checked as 14 bits (`0xFA/0xFB` accepted); CRC-8 + STREAMINFO + CRC-16 still reject | `(b[1] & 0xFC) != 0xF8` | mask `0xFE` |
| P-9 | P3 | Ogg / index | `OG:514-518`, `:547-559` | `Decimate` drops the former last entry; the next appended page is then marked `Contiguous` with a non-adjacent one: one extra page decoded on a resolved seek (never a wrong sample) | `_e[_n].Contiguous = contiguous && _n > 0;` | clear `Contiguous` after a decimation that dropped the last entry |
| P-10 | P3 | Ogg / probe | `OG:701-702` | `Observe` ignores `truncatedAt`; a false `OggS` claiming a body past the window ends the page walk early | `if (at < 0) break;` | continue from `truncatedAt + 1` while ≥ `MaxPageBytes` remain |
| P-11 | P3 | Vorbis / hostile input | `VO:1745-1746`, `:1792-1797` | A hostile setup header can allocate hundreds of MB (`_mults` up to 2²⁶ uints) before being refused | `lookupValues > (1 << 26)` | bound `lookupValues` by what `vqLen + entries·dims ≤ 1<<25` implies |
| P-12 | P3 | Vorbis / codebooks | `VO:391-395` | A single-used-entry codebook is special-cased only when its length is 1; libvorbis decodes any single-entry book by consuming one bit | `if (used == 1 && len[last] == 1)` | drop the length condition |
| P-13 | P3 | sniff | `PA:3549`, `:3235` | `SniffFormat` recognises `OggS` only at byte 0; a raw Spotify dump saved locally (167-byte header) falls through to MP3 | `if (head[..4].SequenceEqual("OggS"u8))` | also test offset 0xa7 (probe ≥ 0xab bytes) |
| P-14 | P3 | tail scan | `ST:1931-1940` | The CDN tail granule is taken without a CRC; only `PlausibleTail` guards it | backward `OggS` scan | confirm with `Ogg.TryParsePage` when the page is inside the buffer |
| R-7 | P3 | Vorbis / perf | `VO:800` | Mono residue-2 pays two integer divisions per coefficient (local mono files only) | `spec[off % ch][off / ch] += v[d];` | a `ch == 1` branch |
| R-8 | P3 | stream / head | `ST:1769-1770`, `:1591`, `:1597` | The head splice proof never completes when chunk 1 lands from disk before chunk 0 from the CDN; `[64K, 80K)` is served from the head forever (harmless) | — | prove any overlap |
| R-9 | P3 | WSOLA / perf | `WS:94, 203-211` | After any non-unity rate the voice never returns to the byte-exact fast path until a seek `Reset` | `_rate == 1 && _ready == 0 && !_hasTail && _inputFrames == 0` | re-enter the fast path when the lookahead is drained |
| R-10 | P3 | RT / transitions | `CM:93-96` | `GainAt` performs an interlocked `TryCommit` per sample during a transition (~1000/block) | `gate.TryCommit()` in `GainAt` | commit once per block in `MixInto` |
| R-11 | P3 | memory model | `PS:385-386, 1289` | `_state`, `_playRequested`, `_started` are plain fields written on control threads and read on RT (fine on x64; `Volatile` them for ARM64) | — | `Volatile.Read/Write` |

Counts: **P0 0 · P1 13 · P2 42 · P3 28** (83 findings).

Verified correct, so that nobody re-audits them (evidence in §0, §5 and the sweep reports): Ogg CRC-32 and page/lacing semantics (`OG:107-173, 188-215, 361-424`); Vorbis headers, codebooks, floor 1, residues 0/1/2, window shape and block-switch overlap, coupling, the one-packet pre-roll after a seek and the sample-accurate landing (`VO:199-216, 334-463, 469-722, 735-877, 911-920, 1222-1262, 1493-1499, 1550-1553, 1627-1667`; `PA:1178-1192, 1826`); the first-page lead-in and EOS truncation becoming `GaplessInfo` (`PA:1254-1260, 1529-1535`); FLAC metadata, frame header, subframes, Rice, decorrelation (16–24 bit), float scaling, CRC-16, resync, seek planner convergence (`FL:168-315, 361-450, 490-589, 627-749`; `FK:325-388, 590-677, 717`); AES-CTR offset math for arbitrary offsets and the 0xa7 skip living in `Body` (`SA:838-891`; `ST:1391, 1414, 1524, 1569, 1575`); the 206/200 reply rule and EOF-only-when-known (`ST:324-337, 978, 1308, 1522, 1638-1643`); RBJ coefficients for all six types, per band×channel state, the dual-cascade cross-ramp (`BQ:30-102`; `DS:117, 152, 176-231`); the gain ramp, mute as a ramp, the limiter's hard ceiling guarantee (`DS:91-98`; `PS:1521`; `DS:371-375`); WASAPI event-driven open, `Reset` after `Stop`, device-lost handling, exact clock math (`WD:219-233, 469, 481`; `PS:955-957`; `WasapiPositionMath.cs:18`); the engine's ReplayGain path disabled so normalization is applied exactly once (`PA:146, 1005`; `AudioSources.cs:19-25`); the late header gain reaching the first sample (`ST:1790-1814`; `PA:1520-1521`); the starve rule never ending a track (`PA:157-181, 1366, 1909`); remote seeks never reaching the pump (`PC:1866`); one committed seek per gesture with the scrub gate (`RU:519-520`; `UI:1695-1704, 1727-1741`).

---

## 2. P1 write-ups with fix sketches

H-1 … H-4 are written up in §0.4 (F1, F2, H-3's two-line fix below, and the EQ command routing). The rest follow.

### H-3 — dead sink wedges the transport (`PcmAudioPlayer.cs:1275-1288, 1294-1302`)

```csharp
// RtRenderOnce, phase 2 (:1278-1280) — and the same shape in RecoverStarvation (:1300-1302)
int writable = _out is IBufferedAudioSink buffered ? buffered.WritableFrames : int.MaxValue;
if (writable < 0) { RecordSinkFailure(); writable = int.MaxValue; }          // a dead sink IS drained: fall through to Stop → phase 3
bool drained = _out is IBufferedAudioSink b2 ? writable >= b2.CapacityFrames : PlayedFrames >= _fadeTailSubmitted;
```

`RecordSinkFailure` (`:1580-1585`) already debounces to the controller's non-restamping policy, so calling it from here is safe. Test: a `HeadlessAudioEndpoint(ready: false)` mid-fade must reach phase 3 within two RT wakes and raise exactly one `ReportSinkFailure`.

### S-1 — Vorbis adapter: an interrupted landing must not end the track (`Playback.Audio.cs:1814-1823`)

```csharp
if (!ReadWindowAt(plan.Offset, plan.WindowBytes, landing: true))
{
    // Mirror the FLAC arm (:4083-4096): the bytes did not arrive (an interrupt for the NEXT seek of a scrub, a
    // superseding load, a starving ring). Not EOF — the next Seek re-lands, and until then Read hands out silence.
    _eof = _ra is null && _src.Length is { } len && plan.Offset + plan.WindowBytes >= len;   // a true end only
    Log.Warn("audio", $"audio.seek.short codec=vorbis target={target} page={plan.Offset} eof={(_eof ? 1 : 0)} interrupted={(_interrupted ? 1 : 0)}");
    return VorbisClock.MixFrameOf(target, _rate, _target.SampleRate, _origin);
}
```

`Read` already serves `Silence(dst, want, ch)` while `_interrupted` is latched (`:1751`), so nothing else changes. Test (pure, over the `AudioAdapterTests` fixture harness): two seeks issued 5 ms apart where the first landing read returns `InterruptedRead` must leave `Exhausted == false` and land the second target exactly.

### S-2 — range fetch: idle timeout, not total timeout (`Spotify.Audio.Stream.cs:537, 724, 1645-1654`)

```csharp
const int RangeIdleTimeoutMs = 8_000;          // the same bound the ring's ReadAt uses — a link that sends nothing for 8 s is dead
…
cts = new CancellationTokenSource(RangeIdleTimeoutMs);
…
// FetchRangeAsync's body loop (:1647): every successful read re-arms the idle deadline
int n = await reply.ReadAsync(dst.Slice(total, want - total), ct).ConfigureAwait(false);
if (n > 0) cts.CancelAfter(RangeIdleTimeoutMs);
```

plus, in `Ring.ReadAt`'s starve branch (`:969-975`): `if (_fetch.IsPending(fileOffset)) _fetch.CancelInFlight();` so the retry starts at the ring's 8 s bound rather than the fetcher's. Test (pure `StarvePolicy`/`Fetcher` logic): a reply that delivers one slot then stalls is settled `Stale` at idle + ε, not at 60 s.

### U-1 — the dropped `Seeked` (`Playback.cs:1881-1884`, `:1950`)

```csharp
// DoSeek
ReportSeeked(ref s, ref fx, s.Position(i.NowMs), ms);
s.PosMs = ms; s.PosQpc = i.NowMs;
s.SeekGen++;                                   // NEW: the seek generation (see §3.4)
Bump(ref s);
fx.Seek = true; fx.SeekMs = ms; fx.SeekEpoch = s.LoadEpoch; fx.SeekGen = s.SeekGen;
```

and in `DoAudio` the `Seeked`/`Position` arms compare `i.SeekGen` (carried by the pump from `Audio.Seek(ms, epoch, gen)`) against `s.SeekGen`, dropping older ones. Test: `PlaybackReducerTests` — a `Seeked` posted under the load epoch after a user seek updates `PosMs/PosQpc`; a `Position` stamped with the previous generation is ignored.

### U-2 — park every seek target (`Playback.Audio.cs:608-616, 2561-2576`)

```csharp
public static void Seek(int ms, uint epoch = 0, uint gen = 0)
{
    …
    lock (s_gate) { s_pendingSeekMs = ms; s_pendingSeekGen = gen; }    // ActivePositionMs reports the TARGET from now on
    (live as RingSource)?.InterruptPendingRead();
    Enqueue(() => SeekCoreAsync(ms, epoch, gen, t0, before));
}
```

`ApplySeekAsync` already clears `s_pendingSeekMs` after the engine lands (`:2587`), and `SeekGate.ReportedPositionMs` (`Playback.Transitions.cs:1450-1451`) already prefers a parked target. The tick's `Position` post carries `gen`; the reducer drops stale ones (U-1).

### U-3 — optimistic remote seek (`Playback.cs:1866`)

```csharp
if (!s.RoutesLocal)
{
    int target = SeekTarget.Clamp(i.IntArg, s.DurationMs);
    s.PosMs = target; s.PosQpc = i.NowMs; s.RemoteSeekAtMs = i.NowMs;           // optimistic: the mirror snapshot is overwritten by the next cluster anyway (:2238-2239)
    Forward(ref s, RemoteCmd.SeekTo, ref fx, target, false);
    return;
}
```

and `MirrorRemote` ignores a cluster position older than `RemoteSeekAtMs` (the cluster's `server_timestamp_ms` is already parsed, `:2188-2219`). Test: a remote seek followed by a stale cluster keeps `PosMs` at the target; a cluster newer than the seek adopts the cluster.

### U-4 — unmute at zero volume (`Playback.cs:2498-2503`)

```csharp
if (!foreign && (i.IntArg & Input.SinkBit) != 0)
{
    fx.Mute = true; fx.MuteOn = muted;
    if (!muted && s.Volume <= MuteFloor)
    {
        var restore = new Input(InputKind.SetVolume, intArg: Input.WireVolume(s.MuteRestoreVolume > MuteFloor ? s.MuteRestoreVolume : UnmuteDefault), nowMs: i.NowMs);
        DoVolume(ref s, in restore, ref fx);
    }
    return;
}
// DoVolume (local, sink present): if (wire > Input.WireVolume(MuteFloor) && s_sinkMuted) { fx.Mute = true; fx.MuteOn = false; }
```

Test: `MuteRulesTests` — (volume 0, sink muted) + unmute ⇒ `fx.Mute=false` and `Volume == UnmuteDefault`; (sink muted) + volume 0.3 ⇒ `fx.MuteOn == false`.

### P-1 — FLAC 33-bit side channel (`Playback.Audio.Flac.cs:490, 821-822`)

Minimal and honest: `DecodeSubframe` starts with `if (bps > 32) return FrameResult.Unsupported;`, the adapter treats `Unsupported` like a bad frame (skip, resync, one-shot `Log.Warn("audio", "flac: 32-bit decorrelated stereo is not supported")`). The complete fix is a `long`-sample path for the side subframe (read 33-bit warm-ups via `ReadLong`, 64-bit FIXED/LPC storage for that channel, decorrelate in `long`) — libFLAC's `FLAC__lpc_restore_signal_wide_33bit`. Test: a synthetic 32-bit L/S frame (the `FlacTests` builder already synthesises variable/32-bit vectors, `FlacTests.cs:1541-1643`) must decode bit-exactly or be reported `Unsupported`, never emit garbage.

### P-2 — floor 0 even order (`Playback.Audio.Vorbis.cs:629-630`)

```csharp
p = 0.5f * (1f - w);
q = 0.5f * (1f + w);
```

Test: a synthetic floor-0 setup (order 2 and 3, one coefficient each) rendered against the spec's pseudocode evaluated in `double`; max error ≤ 1e-5 relative.

### P-3 — HTTP-200 hosts (`Spotify.Audio.Stream.cs:284-291, 1690-1699`)

Add a "streaming body" mode to `Body`: on the first 200 to a non-zero `Range`, keep that reply's stream open and serve subsequent slot-aligned ranges by reading forward from it (slot by slot into `Land`), falling back to the skip path only when a seek goes backwards. The `ModuleByteStream`/`IcySource` paths already model a sequential body; the shape exists. Test: a fake handler that answers 200 to every range must deliver a 10 MiB body with one request and no refusal.

---

## 3. Seeking — the path today, its latency budget, and the redesign

### 3.1 Today: pointer release → first new sample audible (ring-resident Vorbis seek, 48 kHz device)

| # | Step | Thread | Where | Cost (derived) |
|---|---|---|---|---|
| 1 | `OnCommit` → `Playback.SeekTo` → `Post(Input.Seek)` → next UI drain → `DoSeek` → `Execute` → `Audio.Seek(ms, epoch)` | UI | `UI:1727-1741`, `PH:1186`, `PC:1863-1884`, `PH:660` | 0–16 ms (one frame) |
| 2 | `Stats.Read`, `InterruptPendingRead`, `Enqueue(SeekCoreAsync)` — behind any chain op in flight | UI → ThreadPool | `PA:608-616`, `:735-750` | < 1 ms idle; hundreds of ms behind a blocking `Open` |
| 3 | `AbandonPendingJoin`, `p.SeekAsync(ms, Accurate)` | pool | `PA:2561-2583` | < 1 ms |
| 4 | Engine: `_seekRevision++`, suppression armed, `_replacementGate`, `FadeOutAsync(5 ms)`: `CmdFadeOut` applied by RT (≤ 10 ms), 5 ms fade rendered **behind ≈ 90–100 ms of already-submitted old audio**, phase 2 waits until the device buffer is **empty**, then `Stop()`; the caller polls `Task.Delay(2)` | pool ↔ RT | `PS:1176-1188`, `:1275-1288`, `:1498-1503`, `:746-747` | **≈ 100–120 ms** (old audio keeps playing, then the fade, then the poll quantum) |
| 5 | `CmdReset` (Stop + `Reset`, counters zeroed) + `WaitAppliedAsync`; `CmdSeekAnchor` + `WaitAppliedAsync` | pool ↔ RT | `PS:1189-1192`, `:950-968`, `:929-945`, `:844-850` | 2 × (≤ 10 ms RT wake + 2–15.6 ms poll) ≈ 25–50 ms |
| 6 | `ring.SeekFrameAsync` → producer wakes (`WaitOne(20)`) → `TrimmingSource.SeekFrame` → `DecoderAudioSource.SeekFrame` → `VorbisAudioDecoder.Seek`: index bracket (0 probes when resident), `ReadWindowAt` (ring hit), `PeekLanding`, `Prime`, decode ≈ 1–1.5 pages of packets up to the target, `Admit` drops the pre-target frames | producer | `RS:207-248`, `PA:1775-1836`, `OG:641-664`, `PA:1201-1223, 1677-1704` | 0–20 ms wake + ≈ 20–40 ms CPU at the decoder's ≥ 50× real-time floor |
| 7 | `_flushRequest` → RT `RtConsumeFlush` (next wake) → producer `PumpAhead` after its next 20 ms poll (the low-water edge is already latched, `RS:327-339`) → 120 ms of PCM decoded (≈ 3 ms) | RT / producer | `RS:254-263`, `:164`, `:99` | ≤ 10 + ≤ 20 + 3 ms |
| 8 | Poll `BufferingReady()` (≥ 120 ms) via `Task.Delay(2)`; `CmdResetRate` + `WaitAppliedAsync`; `Rebase`; `FadeIn(5 ms)`; `Publish(Ready)` | pool ↔ RT | `PS:1203-1232`, `:1338-1359` | 2–15.6 + ≤ 10 + 2–15.6 ms |
| 9 | Clock thread `Advance`: `Ready` + play → `EnsureStarted`, `Publish(Playing)` | `FluentGpu.AudioClock` | `PS:1406-1408`, `FT:562` | 0–15.6 ms (+ oversleep under load) |
| 10 | RT `RenderBlock` → `SubmitPending` → `_out.Start()`; first block audible after the device period | RT | `PS:1566-1572` | ≈ 10–20 ms |
| 11 | Back on the chain: anchors, `RecordSeek` (`audio.seek.done latencyMs=`), `PostSignal(Seeked)` → UI | pool → UI | `PA:2585-2603` | next frame (and today dropped by U-1) |

Total, ring-resident: **≈ 250–400 ms of silence** (the 100 ms drain + 120 ms rebuffer + 4–6 timer quanta + a clock tick dominate; the decoder itself is ≈ 30 ms). A far seek adds 1–3 CDN probes × (RTT + 64–128 KiB) ≈ 80–300 ms each, during which the old position is already silent. The app's own `audio.seek.done latencyMs` measures steps 2–8 (it stops before the clock flip and the device start), so field logs read ≈ 40–60 ms lower than what is heard. Nothing in this path is a click: the fade-out, the hardware reset on silence and the fade-in from exactly zero are correct (`PS:924-925`). The costs are structural.

### 3.2 Target

| Case | Today (silence) | Target |
|---|---|---|
| Seek within already-decoded PCM (ahead ≤ 2 s, behind ≤ 1 s with the keep-behind ring) | 250–400 ms | **≤ 15 ms, no silence** (an RT-side ring jump under a 5 ms equal-power crossfade) |
| Seek into fetched-but-not-decoded bytes (the 30 s+ compressed window, or the disk cache) | 250–400 ms | **≈ 50–80 ms, no silence** (voice swap; the old voice plays until the new one is ready) |
| Far seek (CDN probes) | 400–1000 ms | network-bound (1 probe typical with the landing-time index), old audio continues ≤ 80 ms then a 10 ms fade to silence; the UI never snaps back |
| Double seek / seek storm | N serial full seeks | one in-flight prepare, last-write-wins, the previous one cancelled |

### 3.3 Design

**A. Seek as a voice swap, not a transport stop.** The engine already has every primitive: `PrepareAtAsync(source, ctx, positionFrames)` opens an independent decoder at a position and fills a ring to a 100 ms readiness for seeks (`PS:36-37, 75-79`), `TryAddCrossfadeVoice` installs a second voice with an envelope (`:703-730`), `GainEnvelope.Fade` builds equal-power LUTs off-RT (`CrossfadeMixer.cs:80-90`), `SetVoiceEnvelope` retargets the outgoing voice (`:734-735`), `SetActiveVoice` re-points transport (`:675-686`), and the retire path disposes the old ring off-RT (`FT:247-265`). A seek becomes:

```csharp
// [engine] PcmAudioPlayer.cs — the new SeekAsync (replaces :1176-1236)
public async ValueTask SeekAsync(TimeSpan to, SeekMode mode)
{
    long revision = Interlocked.Increment(ref _seekRevision);
    long frame = ClampToDuration(to);
    // 1. Instant path: the target lies inside the active ring's decoded span → RT-side jump (B below).
    if (TryJumpWithinRing(frame)) { RebaseTo(frame); return; }
    // 2. Prepare a second decoder at the target while the current voice keeps playing (no fade, no Stop, no Reset).
    using var cts = new CancellationTokenSource();
    _seekPrepare?.Cancel();                                   // last write wins: a newer seek cancels the older prepare (S-4)
    _seekPrepare = cts;
    IPreparedItem item;
    try { item = await _backend.PrepareAtAsync(_source, PrepareContext.For(_format, _norm, _refLufs), frame, cts.Token).ConfigureAwait(false); }
    catch (OperationCanceledException) { return; }            // superseded
    if (revision != Interlocked.Read(ref _seekRevision)) { item.Dispose(); return; }
    // 3. Swap at the next block boundary: incoming fades in over SeekFadeFrames (5 ms), the outgoing fades out over the
    //    same window — equal-power, so the sum stays at unity. ConsumeSeq is the mixer clock; the envelopes are LUTs.
    long at = _mixer.ConsumeSeq + _feed!.BlockFrames;         // one block ahead: the RT applies both commands before `at`
    int fade = Math.Max(1, _format.SampleRate / 200);
    var incoming = GainEnvelope.Fade(FadeKind.In, at, fade, CrossCurve.EqualPower);
    var outgoing = GainEnvelope.Fade(FadeKind.Out, at, fade, CrossCurve.EqualPower);
    long id = NextVoiceId();
    if (!TryAddCrossfadeVoice(item.AudioVoice!, incoming, at, replayGain: 1f, chain: BuildVoiceChain(), id)) { item.Dispose(); return; }
    SetVoiceEnvelope(ActiveVoiceIdValue, outgoing);           // the old voice retires itself when its fade completes (MixVoice.IsFinished)
    SetActiveVoice(id, item.AudioVoice!, item.Duration, item.TotalFrames);
    RebaseTo(frame);                                          // _position.Rebase(playedNow, frame) — no Reset(): the device clock keeps counting
    _sink?.Position(TimeSpan.FromSeconds((double)frame / _format.SampleRate));
}
```

Policy for a far seek: if the prepare has not produced its first block within `MaxStaleMs = 80`, fade the old voice to silence over 10 ms (it is wrong to keep playing the old position for a second) and fade the new one in from silence when it lands. Costs: a second decoder (the Vorbis working-set pool already holds two, `PA:1380-1428`; FLAC windows are 64 KiB) and one `_decoderSlots` lease for the duration of the prepare — with the mailbox there is at most one seek prepare in flight, so the three leases are live + prepared-next + seek (H-8's timeout must become a logged wait, not a throw). The device is never stopped, so H-3/H-6/H-7 leave the seek path entirely; F5/F7 remain worth doing for start-up and hand-off.

**B. Instant seeks inside the decoded range.** `PcmRing` is a plain SPSC float ring (`PcmRing.cs`, head/tail monotonic). Give it a consumer-side `TrySkip(frames)` (advance the head within `AvailableFloats` — already what `DiscardAllConsumerSide` does for the whole buffer) and a `KeepBehindFloats` region the producer must not overwrite (the producer's free-space computation subtracts it), plus `TryRewind(frames)` for the consumer. With F3's 4 s capacity: 2 s ahead, 1 s behind, 1 s slack. The RT applies the jump as a mixer command (`CmdJumpWithinRing{Frames}`) at a block boundary under the same 5 ms equal-power envelope pair (outgoing = the last block's tail, which `MixInto` can keep in a 240-frame scratch; incoming = the post-jump block). `RingAudioSource.PositionFrames` and the decoder-side cursor must both move: the ring tells the producer "the consumer skipped N" through a counter the producer folds into `_inner`'s position accounting (the inner decoder is unaffected — it keeps decoding ahead from where it was; a forward jump simply consumes decoded PCM sooner, a backward jump re-reads kept PCM). Vorbis/FLAC pre-roll is irrelevant here because no decoder seek happens.

**C. A page/granule index for O(1) seeks into fetched data.** The Ogg `PageIndex` (`OG:300-602`: 4096 entries, `Add` as pages are parsed, `Bracket` by binary search, `Adjacent` ⇒ `Resolved`) already makes seeks back into *played* material 0-probe. Extend it to *fetched* material: in `Body.Observe` (`ST:1790-1814`, the one funnel for landed bytes — CDN and disk alike), walk the landed 64 KiB slot for `OggS` pages with `Ogg.FindPage` (CRC-32 slicing-by-8 over 64 KiB ≈ 30–60 µs on the fetch thread) and append `(fileOffset, granule)` to a body-owned index the decoder's planner consults first; a page straddling a slot boundary is completed when the next slot lands (keep the 27-byte header tail). The planner then resolves any target inside the compressed window — 30 s or more — with **zero probes** and a ring-resident landing. For FLAC do the same with frame headers (sync + CRC-8 + STREAMINFO cross-check, `FL:361-450`) into the existing 1024-point table shape (`PA:3845`). Thread model: the fetch loop appends under the body's gate; the producer reads a published immutable snapshot (`Volatile` swap of a sorted array) — both off-RT. Also fix S-10/S-11/S-12: a probe inside the pending range does not cancel it; `Retarget` plans two slots behind the landing and `Land` protects the four keep-behind slots from direct-mapped overwrite (the "4 kept behind" today is a sizing allowance only, `ST:826, 1077-1083`).

**D. Correct Vorbis pre-roll — keep what exists.** One primed packet plus the landing page's packets up to the target, sample-accurate via `Admit` (`PA:1178-1192, 1826`; `VO:1493-1499`) — verified. The one gap is S-7 (a hole or bad packet must re-prime, `dec.Prime()`, and the clock must pad the gap). With design A the pre-roll runs on the *second* decoder while the first keeps playing, so its cost is invisible.

**E. The micro-crossfade.** Equal-power (`cos/sin(p·π/2)`), 5 ms (240 frames at 48 kHz) for in-ring jumps and prepared swaps; 10 ms when fading to/from silence (far seek timeout). `TransportRamp` stays for pause/resume (20 ms smoothstep) and is no longer touched by seeks.

**F. Optimistic UI position, no snap-back.** (i) fix U-1 so `Seeked` lands; (ii) park the target for every seek (U-2) so `ActivePositionMs` reports it immediately; (iii) a **seek generation** stamped by the reducer on `Input.Seek`, carried by `Audio.Seek` into the pump, and stamped on every `Position`/`Seeked` post — `DoAudio` drops anything older than `s.SeekGen`; (iv) the remote branch sets `PosMs/PosQpc` (U-3); (v) `Position()` freezes while `Buffering` (U-5); (vi) the engine suppresses `PublishPosition` while `_replacementGate` is held (S-6) — with design A there is no `Reset`, so the dip disappears anyway; (vii) the UI hold (`CommitHoldMs`) is released only by a report whose generation matches **and** whose position is within ±250 ms of the committed target, or by the 750 ms timeout.

### 3.4 Latency budget after

| Step | Resident (A) | In-ring (B) |
|---|---|---|
| UI drain | 0–16 ms | 0–16 ms |
| Mailbox + chain hop | < 1 ms | < 1 ms |
| `PrepareAtAsync`: decoder seek (index hit, ring hit) + first block | ≈ 20–40 ms | — |
| Mixer command applied at the next block boundary | ≤ 10 ms | ≤ 10 ms |
| Equal-power crossfade | 5 ms (audio continuous) | 5 ms (audio continuous) |
| **Silence heard** | **0 ms** | **0 ms** |
| **New position audible after** | **≈ 40–70 ms** | **≈ 5–25 ms** |

The engine's `PositionTracker` keeps counting (no `Reset`), so the reported position is continuous: it reads the old voice's position until the swap block and the new one after — exactly what the user sees on the bar.

---

## 4. Scrubbing — audible, DAW-style

### 4.1 Behaviour

While the thumb is held and moving, the listener hears **grains** of the audio under the thumb — short windowed slices (default 60 ms, Hann/sin² window, 50 % overlap) whose playback rate follows the drag velocity, so a slow drag sounds like slow motion and a fast drag like a chirp, at a smoothed scrub level (−6 dB from the current volume, 20 ms ramps). When the thumb stops moving for 150 ms the grains fade to silence over 10 ms (the DAW "jog stop"); moving again resumes them. The main voice is held (not consumed, not audible) for the whole gesture. On release, **one real seek** is committed through §3's voice swap — and because the scrub decoder is already parked near the release point, the commit is normally a promotion of that decoder, i.e. zero extra latency. On cancel (capture loss, Escape) the main voice fades back in at its held position and the scrub voice is dropped. Keyboard scrubbing is visual-only with accelerating steps and one seek on key-up; Connect scrubbing is visual-only with one `seek_to`; video scrubbing shows keyframe previews.

### 4.2 State machine (pure, engine-free)

```
Idle ──pointer down──▶ Pressed(frac)     thumb jumps; ScrubBegin(ms); main voice Hold(fade 20 ms); scrub voice prepared at ms
Pressed ──move──▶ Scrubbing              grains on; ScrubMove(ms, velocity) coalesced to ≤ 20 Hz
Scrubbing ──no move 150 ms──▶ Parked     grains fade to silence (10 ms); the scrub decoder stays at ms
Parked ──move──▶ Scrubbing
Pressed|Scrubbing|Parked ──release──▶ Committing   one CommitSeek(ms): promote the scrub voice if |ms − scrubPos| < Promote (250 ms), else §3 seek
Committing ──Seeked(gen)──▶ Idle
Pressed|Scrubbing|Parked ──cancel──▶ Idle          main voice Release(fade 20 ms); scrub voice dropped
```

```csharp
// [app] Playback/Playback.Scrub.cs — CORE (System only), alloc-free, unit-tested
public struct ScrubModel
{
    public enum State : byte { Idle, Pressed, Scrubbing, Parked, Committing }
    public const int CoalesceMs = 50, ParkAfterMs = 150, PromoteWithinMs = 250;

    public State Current;
    public long PositionMs, LastMoveAtMs, LastSentAtMs, Generation;
    public double VelocityMsPerMs;                 // audio-ms per wall-ms: +1 = real time forward, −1 = reverse

    public readonly record struct Effects(bool Begin, bool Move, bool Park, bool Commit, bool Cancel, long PositionMs, double Velocity, long Generation);

    public Effects Down(long ms, long now)
    {
        Current = State.Pressed; PositionMs = ms; LastMoveAtMs = LastSentAtMs = now; VelocityMsPerMs = 0; Generation++;
        return new(Begin: true, false, false, false, false, ms, 0, Generation);
    }

    public Effects Move(long ms, long now)
    {
        if (Current is State.Idle or State.Committing) return default;
        double dt = Math.Max(1, now - LastMoveAtMs);
        VelocityMsPerMs = 0.5 * VelocityMsPerMs + 0.5 * ((ms - PositionMs) / dt);   // one-pole smoothing of the drag velocity
        PositionMs = ms; LastMoveAtMs = now;
        Current = State.Scrubbing;
        if (now - LastSentAtMs < CoalesceMs) return default;                           // ≤ 20 intents per second reach the audio side
        LastSentAtMs = now;
        return new(false, Move: true, false, false, false, ms, VelocityMsPerMs, Generation);
    }

    /// <summary>The UI's dwell ticker calls this; it is what parks the grains when the finger rests.</summary>
    public Effects Tick(long now)
    {
        if (Current == State.Scrubbing && now - LastMoveAtMs >= ParkAfterMs) { Current = State.Parked; return new(false, false, Park: true, false, false, PositionMs, 0, Generation); }
        return default;
    }

    public Effects Up(long ms, long now)
    {
        if (Current == State.Idle) return default;
        Current = State.Committing; PositionMs = ms;
        return new(false, false, false, Commit: true, false, ms, 0, Generation);
    }

    public Effects Cancel() { bool live = Current != State.Idle; Current = State.Idle; return live ? new(false, false, false, false, Cancel: true, PositionMs, 0, Generation) : default; }
    public void Landed(long generation) { if (Current == State.Committing && generation == Generation) Current = State.Idle; }
}
```

### 4.3 Grains — the pure scheduler and the producer-side source

```csharp
// [engine] Media/Playback/Audio/ScrubGrain.cs — pure math, zero-alloc, engine tests
public static class ScrubGrain
{
    /// <summary>Hann window over <paramref name="n"/> frames, applied in place to interleaved PCM. Alloc-free.</summary>
    public static void Window(Span<float> pcm, int frames, int channels)
    {
        for (int f = 0; f < frames; f++)
        {
            float w = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * (f + 0.5f) / frames);
            int b = f * channels;
            for (int c = 0; c < channels; c++) pcm[b + c] *= w;
        }
    }

    /// <summary>Where the next grain starts, in source frames: the scrub position plus the velocity-driven advance since the
    /// last grain. |velocity| ≤ MaxRate keeps a fast drag a chirp instead of noise; a parked thumb advances nothing.</summary>
    public static long NextGrainStart(long targetFrame, long lastGrainStart, double velocity, int hopFrames, double maxRate = 4.0)
    {
        double rate = Math.Clamp(velocity, -maxRate, maxRate);
        long advanced = lastGrainStart + (long)Math.Round(hopFrames * rate);
        // Never drift more than one grain from the pointer: the pointer is the truth, the velocity only shapes it.
        return Math.Abs(advanced - targetFrame) > 2L * hopFrames ? targetFrame : advanced;
    }
}
```

```csharp
// [app] Playback/Playback.Audio.Scrub.cs — SHELL: the scrub voice. An IAudioSource the engine's RingAudioSource wraps,
// so the grain decode runs on the ring's own producer thread and the RT thread only copies (the existing firewall).
sealed class ScrubGrainSource : IAudioSource
{
    readonly IAudioDecoder _dec;           // a second decoder instance (PrepareAt opened it at the press position)
    readonly int _rate, _ch;
    readonly float[] _window, _cache;      // the 2 s PCM cache around the scrub point + one grain's scratch
    long _cacheStart;                      // source frame of _cache[0]
    long _target, _lastGrain;              // Volatile: the UI side writes _target/_velocity; the producer reads them
    double _velocity;
    int _grainFrames, _hopFrames, _emitted;
    volatile bool _parked, _linear;        // Parked: emit silence; Linear: promoted — stream straight PCM (no windowing)

    public void Retarget(long frame, double velocity) { Volatile.Write(ref _target, frame); Volatile.Write(ref _velocity, velocity); _parked = false; }
    public void Park() => _parked = true;
    public void Promote() => _linear = true;   // the commit landed on us: become the main voice without re-decoding

    public int Read(Span<float> dst, int channels)
    {
        if (_linear) return _dec.Read(dst);                    // promoted: plain decode from the cache position onward
        if (_parked) { dst.Clear(); return dst.Length / channels; }
        long target = Volatile.Read(ref _target);
        if (target < _cacheStart || target + _grainFrames > _cacheStart + _cache.Length / _ch) Recenter(target);   // one decoder seek + ≤ 2 s decode
        long start = ScrubGrain.NextGrainStart(target, _lastGrain, Volatile.Read(ref _velocity), _hopFrames);
        _lastGrain = start;
        var grain = _cache.AsSpan((int)(start - _cacheStart) * _ch, _grainFrames * _ch);
        grain.CopyTo(_window);
        ScrubGrain.Window(_window, _grainFrames, _ch);
        // 50 % overlap-add into dst: this grain's second half sums with the next grain's first half (the producer keeps the
        // overlap tail in _window's upper half between calls). Returns hopFrames per call.
        OverlapAdd(_window, dst, channels);
        return _hopFrames;
    }
    …
}
```

Threading: the UI thread writes `_target/_velocity` (two `Volatile` stores, ≤ 20 Hz); the producer thread (`FluentGpu.AudioProducer`, MMCSS "Audio" after F1) does the decoding, the recentring and the windowing into the ring; the RT thread mixes the ring like any voice — copy only, inside the tripwire. A recentre costs one decoder seek plus up to 2 s of decode (≈ 40–60 ms for Vorbis); grains keep playing from the ring's buffered hops meanwhile, so the 2-grain-deep ring (`targetAheadFrames = 2 × hop`) is sized exactly to hide it.

### 4.4 Engine API

```csharp
// PcmAudioSession (control thread)
public ValueTask BeginScrubAsync(IAudioSource grainSource, long voiceId)   // Hold the active voice (MixVoice.Held: MixInto returns without reading), add the grain voice at −6 dB
public void UpdateScrub(long positionFrames, double velocity)             // forwards to the grain source's Retarget (Volatile)
public ValueTask EndScrubAsync(bool commit, long positionFrames)          // commit near the grain decoder: Promote() + SetActiveVoice; else §3 seek; cancel: Release the held voice (fade 20 ms), remove the grain voice
```

`MixVoice.Held` is one new `bool` on the struct (`CrossfadeMixer.cs:111-125`) checked at the top of `MixInto`; a held voice is excluded from `ReadableFrames`/`PcmReady`/`IsDrained` so a scrub never trips the starvation or end verdicts.

### 4.5 Keyboard scrubbing

Arrow Left/Right = ±5 s visual step (the thumb and the time label move, the time label shows the target, no audio); a **held** key accelerates — 5 s per repeat for the first 0.5 s, then 15 s, then 30 s — and commits **one** seek on key-up (fixes U-6; `PlayerSeekAccumulator`, `RU:425-446`, becomes the pure ladder with an `IsRepeat` gate). Shift+Arrow = ±1 s fine step; Home/End = start/end; Ctrl+Arrow = chapter for episodes. A 250 ms post-key-up grace lets the next press continue the accumulation. Tests: the ladder is pure (`KeyboardScrubTests`).

### 4.6 Seek bar UI changes (`BarSeekRail`, `UI:1540-1750`)

`OnDown` → `Playback.ScrubBegin(ms)` (reducer input; the model runs in the UI thread, effects go to `Audio.BeginScrub`); `OnDragMove` → `Playback.ScrubMove(ms, now)`; the dwell ticker (already mounted) calls `Playback.ScrubTick(now)`; `OnCommit` → `Playback.ScrubEnd(ms)`; `OnCancel` → `Playback.ScrubCancel()`. `BarTimeText` reads a `Playback.ScrubPositionMs` signal while a scrub is live (U-7), and a thumb tooltip shows the time at the pointer for tracks (episodes already show the chapter timestamp). The `CommitHoldMs` hold is replaced by the seek-generation rule (§3.3 F).

### 4.7 Connect and video

**Connect (another device owns playback):** `ScrubBegin` is visual-only — no grain voice (there is no local audio); the thumb and label follow the pointer; `ScrubEnd` forwards one `seek_to` with the optimistic `PosMs/PosQpc` write (U-3); cluster positions older than the seek are ignored (`CommandAttribution.WindowMs = 10 s` exists for the attribution; use the same window for the position). **Video:** the video host already plans preview seeks to the nearest buffered keyframe (`Playback.Video.cs:275-288, 486-504`); scrubbing issues those previews at ≤ 10 Hz with the audio muted, and one accurate seek on release — grains are not attempted through Media Foundation.

### 4.8 Pure-core tests

`ScrubModelTests`: Down jumps and begins; Move coalesces to ≤ 1 per 50 ms; Tick parks after 150 ms of rest and a Move un-parks; Up commits once with the latest position; Cancel from every live state; a `Landed` with a stale generation is ignored. `ScrubGrainTests` (engine): `Window` sums to unity at 50 % overlap (Hann COLA), `NextGrainStart` follows velocity, clamps at ±4×, snaps to the pointer after a jump, and never allocates. `ScrubGrainSourceTests` (app, with a synthetic decoder): grains are cut from the cache, a target outside the cache recentres once, `Park` emits silence, `Promote` streams linear PCM from the last grain position.

---

## 5. Normalization, EQ and volume — verdicts with the reference math

### 5.1 Normalization

**What runs today (verified).** The engine's own ReplayGain path is off: `EngineNormalization = NormMode.Off` (`PA:146`), `session.SetVoice(…, NormMode.Off, -18f, volume)` (`:1005`), and `ReplayGain.ScalarLinear(…, Off) → 1` (`AudioSources.cs:21`) — so the engine's RG-2.0 formula (`gainDb + (referenceLufs − (−18))`, `:22-24`, which would have added +4 dB at the −14 default) is never applied. Normalization is one linear factor folded into each decoder's sample conversion: `NormalizationFactor(enabled, gainDb, peak) = 10^(clamp(gainDb, ±30)/20)`, capped so `factor × peak ≤ 1.0` when the peak is known (`PA:1129-1135`; the Vorbis interleave multiplies by it, `VO:1643-1659`; FLAC folds it into the int→float scale, `FK:717`). The figure comes from `GainFor` (`SA:396-411`): the catalogue's lossless normalization params when present, otherwise — Ogg only — the Spotify header's `track_gain_db` (float at byte 144) and `track_peak` (byte 148), sanity-clamped (`SaneGain` ±30 dB, `SanePeak` 0 < p ≤ 4). For lossless the app computes `NormalizationGain(loudnessDb, truePeakDb) = min(−14 − loudness, −1 − truePeak)` (`SA:370-376`). The setting `playback.normalization` defaults on (`Platform.cs:224`) and is read at decoder construction (`PA:1117-1118`) — a toggle applies from the next track.

**Against Spotify's documented behaviour.** Spotify normalises to −14 LUFS in "Normal", −23 in "Quiet", −11 in "Loud" (Spotify's published figures, with the header gains expressed relative to the Normal reference), is peak-aware so a positive gain never clips, and offers album mode so inter-track dynamics within an album survive. Verdict:

| Item | Verdict |
|---|---|
| Track gain → linear factor, peak-capped so it never clips | **correct** (librespot `get_factor`, Basic method) |
| Applied exactly once, at the decode edge, before the mixer | **correct** (no double application; late header gain reaches the first sample, `ST:1790-1814`) |
| Target level | **Normal only** (−14). No Quiet (−9 dB pregain) / Loud (+3 dB pregain with a limiter) — N-2 |
| Album vs track mode | **missing**: `album_gain_db` @152 / `album_peak` @156 are inside the 0xa7 header but `HeaderGainBytes = 152` stops short — N-2 |
| Clip protection when the gain is positive | **correct but inconsistent with the limiter**: the Ogg cap is 0 dBFS, lossless −1 dBFS, the brickwall ceiling −1.5 dB with zero attack ⇒ boosted tracks are limited per-sample — N-1. One constant (`LimiterCeilingLinear = 0.8414`) should feed all three |
| Local files (ReplayGain tags) | **missing** — N-3 |
| Where in the graph | per-voice, pre-mix (decoder) — the right place; a crossfade sums two already-normalised voices; the limiter is post-volume (E-9) |

Proposal (N-2): keep the decoder fold; add `NormalizationMode { Quiet = −9, Normal = 0, Loud = +3 } dB` pregain in `NormalizationFactor` and an `AlbumMode` flag that selects the album pair when the header has it; extend `HeaderGainBytes` to 160 and parse the two extra floats in `GainFor`; in Loud mode cap at the limiter ceiling (Spotify's Loud uses limiting). The toggle becoming live mid-track needs a per-voice gain ramp — the engine's `MixVoice.ReplayGainScalar` is exactly that slot, so route it through a `CmdSetReplayGain` mixer command (also fixing E-5) rather than re-opening the decoder.

### 5.2 Equalizer

**Design math (verified against the RBJ Audio EQ Cookbook).** `BiquadCoeffs.Design` (`BQ:17-103`): `A = 10^(dB/40)`, `ω0 = 2πf0/Fs`, `α = sin ω0/(2Q)`; peaking `b0 = 1+αA, b1 = −2cos ω0, b2 = 1−αA, a0 = 1+α/A, a1 = −2cos ω0, a2 = 1−α/A` ✓; low shelf `b0 = A((A+1) − (A−1)cos ω0 + 2√A α)`, `b1 = 2A((A−1) − (A+1)cos ω0)`, `b2 = A((A+1) − (A−1)cos ω0 − 2√A α)`, `a0 = (A+1) + (A−1)cos ω0 + 2√A α`, `a1 = −2((A−1) + (A+1)cos ω0)`, `a2 = (A+1) + (A−1)cos ω0 − 2√A α` ✓; high shelf the cos-sign-flipped twin ✓; LP/HP/notch ✓; normalised by `a0` in `double`, cast once ✓; `f0` clamped to `[1, Fs/2 − 1]`, `Q ≤ 0 → 1e-4` ✓. State is per (band × channel) Direct Form I (`DS:117, 152, 207`; `BQ:110-125`) ✓. A gain change recomputes the band and cross-fades two full cascades over 256 samples with the pending state seeded from the active one (`DS:168-181, 196-231`) — zipper-free ✓ (pinned by `AudioGraphTests.cs:183-202`). Sample-rate change: the whole session is rebuilt at the new rate (`RebuildSink` → `_formatRequiresReload` → host reload), so coefficients are recomputed by construction ✓. App table: 10 ISO bands 31–16 k, peaking, Q = 1, ±12 dB, no preamp (`PA:707-716`; `ME:32-34`).

**Incorrect or missing:** H-4 (the live stage is mutated from the clock thread — the only correctness bug), E-1 (float DF-I at 31/62 Hz; `double` state fixes it), E-2 (denormals), E-4 (no headroom: `preamp = −max(0, max gain)` automatically, or a user preamp), E-6 (topology enable is an instant coefficient swap), E-7 (shelf Q form; unused). Presets (`Screens/Settings.cs:216-226`, per the shell sweep): flat, bass [6,5,4,2,0…], treble [0…1,2,3,4,5], vocal, radio, proof (±12 alternating). None exceeds +6 dB except "proof"; with E-4's automatic preamp none would clip.

**Limiter (`DS:320-382`).** Ceiling −1.5 dB (linear 0.8414), one-pole release τ = 50 ms, instant attack, sample-peak, channel-linked, always on (`AudioGraphHost.cs:156`). The hard guarantee `|out| ≤ ceiling` holds every sample ✓. But zero attack with no lookahead is a waveshaper on the attack edge (E-3) and sample-peak misses inter-sample peaks — the name `dBTP` is wrong. Proposal: 2 ms lookahead (96 frames) with a linear-attack gain smoother, `LatencySamples = lookahead` (the plumbing to the position tracker exists: `AudioGraphHost.cs:31-33` → `PS:1538` → `AudioClock.cs:82`), optional 4× oversampled peak detector for true peak; snap the release to 1.0 (E-2). Cost ≈ 6–10 MAC/sample — negligible.

### 5.3 Volume

**Mapping.** The UI slider is linear 0..1; the amplitude is the **cubic taper** `p³` at the sink edge (`PA:300-308`): 50 % → −18 dB, 25 % → −36 dB, 10 % → −60 dB. A cubic taper is a standard perceptual approximation (steeper than the common `dB = 60·(p−1)` linear-in-dB law at the bottom, gentler at the top); it is fine. The engine applies it once as `_masterGain` with a 512-sample (10.7 ms) linear-amplitude ramp retargeted every block (`PS:1521`, `AudioGraph.cs:171`, `DS:91-98`) — zipper-free ✓; mute is a ramp to 0 and back (`:1521`, `:1246-1248`) ✓; pause/resume are 20 ms smoothstep ramps (`:1150, 1165`; `TransportRamp.cs:43`) ✓. **No double application**: the app never touches `ISimpleAudioVolume`/`IAudioEndpointVolume` (grep), so the OS session slider multiplies once on top, as for every app ✓. Video receives the same tapered amplitude (`PH:664`) ✓.

**Connect.** The wire carries the slider position `round(v × 65535)` and the inverse `/65535f` (`PC:281, 1362-1363, 1901`); a sub-step gate drops `|Δ| × 65535 < 1` (`:1902`) ✓; the taper is local, so a 50 % slider means −18 dB here and whatever the remote client's own curve says there — a perceptual mismatch that is inherent to the protocol (the wire has no dB). Mute has no wire flag: a foreign device is "muted" as volume 0 with `MuteRestoreVolume`/`UnmuteDefault 0.7` (`:2487-2515`) ✓. Startup: `SavedVolume` default 0.7 when `RememberVolume` (default true), else 1.0 (`Platform.cs:214-215`) — 0.7 is −9.3 dB of amplitude, so the first launch of a normalised (−14 LUFS) stream plays at ≈ −23 LUFS: quiet but safe; a design choice to confirm.

**Incorrect:** U-4 (unmute at zero), V-1 (a registry write, a settings-epoch fan-out and a PUT per drain while dragging), V-2 (remote PUT per drain, cluster echo snap-back). The 10.7 ms mute ramp is short for a full-scale bass-heavy signal (a 20–30 ms ramp is the usual choice) — P3, included in the quick wins.

---

## 6. Quality upgrades — cost and benefit

| Upgrade | Why | Design | Cost | Benefit |
|---|---|---|---|---|
| **Windowed-sinc polyphase SRC** replacing `LinearResampler` (Q-1) | Every 44.1 kHz Spotify stream on a 48 kHz Windows mix format passes through linear interpolation today (`PA:1536, 1989, 3907`): sinc² droop (−3.4 dB at 15 kHz) and images at −22 dB. This is the single largest *audible* quality defect in the pipeline. | A `PolyphaseResampler` with the same `Process(src, inFrames, dst) → ResampleResult` contract (`LR:84-151`) so the three decoder call sites do not change. Fixed rational ratios get an exact phase table (44.1→48 = 147:160, 160 phases × 32 taps, Kaiser β ≈ 9, cutoff 0.45·min(fs) ⇒ > 90 dB image rejection, ±0.05 dB passband to 20 kHz); other ratios use 256 phases with linear phase interpolation (interpolation error ≈ −80 dB). `LatencySamples = taps/2` (reported into the clock, `AudioGraphHost.cs:31-33`). EOF flush pads half a kernel so the last frames are emitted (fixes Q-7 for free). Reset on seek stays. | ≈ 32 MAC per output sample per channel ≈ 3 M MAC/s at 48 kHz stereo — negligible (SIMD across taps with `Vector128`). ~300 lines + an FFT-based test oracle (a swept sine; measure images and droop). Engine wave, one file. | Transparent 44.1→48 (and 48→44.1, 96→48) conversion; removes the only non-transparent DSP in the default path. |
| **Limiter lookahead + true-peak** (E-3) | Zero-attack gain drops distort transients; sample-peak misses inter-sample overs. | 2 ms lookahead delay line, gain computed on the look-ahead window with a linear attack to the minimum; release 50 ms as today; 4× oversampled peak (a 4-tap half-band pair) for true peak; `LatencySamples = lookahead`. | ≈ 8–12 ops/sample; 96 frames of latency reported to the clock (the position math already subtracts stage latency). | A limiter that limits instead of clipping; honest `dBTP`. |
| **`double` biquad state + denormal flush** (E-1, E-2) | Float DF-I at 31/62 Hz accumulates round-off; subnormals cost RT CPU on silence. | `double _x1,_x2,_y1,_y2` (coefficients stay float); `if (Math.Abs(y) < 1e-25) y = 0` or an alternating ±1e-20 DC injection; limiter `_gain` snaps to 1 within 1e-6. | Free. | Clean low-shelf/31 Hz boosts; no CPU spikes on fades and gaps. |
| **EQ preamp** (E-4) and **ramped topology enable** (E-6) | Boosts hit the brickwall; enabling the EQ clicks. | Automatic `GainSpec(−max(0, maxBoost))` ahead of `EqSpec`; `SetBands` from an empty set ramps from identity coefficients. | Trivial. | No pumping with boosted presets; no click on enable. |
| **Exclusive mode / bit-perfect** | Lossless listeners ask for it; it bypasses the Windows mixer and the SRC entirely when the device is opened at the source rate. | Opt-in "Bit-perfect (exclusive)" for FLAC: `AUDCLNT_SHAREMODE_EXCLUSIVE`, `IsFormatSupported` at the source rate/bit depth, event-driven with aligned buffers (`AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` retry), a per-track device reopen when the rate changes (a gap — exactly what the shared-mode design avoids), and other apps go silent. | Large: a second device path, format negotiation, reopen-on-rate-change, UX for the "device is busy" cases. | Real only for users who can hear the difference between a 90 dB SRC and none; with the polyphase SRC in place the shared path is already transparent at 16/24-bit. **Verdict: not now; revisit after Q-1 ships.** |
| **Device rate follows the source** (alternative to exclusive) | Avoid SRC without exclusive mode. | Not viable in the fixed-mix-format design: shared mode adopts the device rate (`WN:22-26`), and reopening per track is a reopen gap. `AUTOCONVERTPCM` would move the SRC into Windows (its SRC is good) but still per-stream-rate = per-track client. | — | Skip. |
| **WSOLA** (Q-2, R-9) | Click when returning to 1×; never returns to the byte-exact fast path. | Blend the first unity hop against `_tail`; re-enter the fast path when `_inputFrames` drains. Parameters (40 ms window, 20 ms hop, ±10 ms search, stride-4 correlation) are standard. | Trivial. | Clean speed toggles on podcasts. |
| **Crossfade / gapless** | Verified: equal-power curves (`CM:7-31`), per-voice envelopes as LUTs, `TrimmingSource` for encoder delay/padding, `GaplessJoinClock` frame math. Open items: S-5 (seek in the last 1.5 s kills the join), and the 0 ms butt-join's dependence on an exact trimmed length (the Vorbis tail is learned late; FLAC's STREAMINFO is exact). | Fix S-5; make the join arm re-read `ExactVoiceEndFrame` once the producer reaches EOF (the engine exposes it, `PS:477-498`). | Small. | No hard cuts after a late seek; sample-accurate joins on VBR Vorbis. |
| **Channel fold-down** (Q-3, P-4) | > 2 channels are dropped (engine WAV) or mis-ordered (FLAC 4/7 ch). | ITU-R BS.775: `L = FL + 0.707·C + 0.707·SL (+ 0.5·LFE optional)`, mirrored for R, with a per-layout table. | Trivial. | Correct 5.1 local files. |
| **Dither for integer endpoints** (Q-4) | Truncation on 16-bit devices. | TPDF dither (two uniform draws, ±1 LSB) in `ConvertBlock` for 16-bit only. | Trivial; rare in practice. | Correctness for the rare int16 shared format. |

---

## 7. Quick wins (≤ 1 hour each)

1. **H-2 half-fix** — `CrossfadeMixer.PcmReady` (`CM:253-255`): drop the `RecoveryFrames` clamp so recovery really waits for `StartupReadinessFrames`; delete `WsolaAudioSource.RecoveryFrames` (`WS:72`). Pin with a test. (The full F2 follows in wave 1.)
2. **H-3** — `PS:1278-1280`, `:1300-1302`: `WritableFrames < 0` ⇒ drained + `RecordSinkFailure()`.
3. **U-1** — `PC:1884`: `fx.SeekEpoch = s.LoadEpoch`.
4. **U-2 half** — `PA:608-616`: park `s_pendingSeekMs = ms` before enqueueing.
5. **U-3** — `PC:1866`: write `PosMs/PosQpc` in the forward branch.
6. **U-4** — `PC:2498-2503` + `DoVolume`: restore the volume on unmute at zero; clear the sink mute on a volume raise.
7. **U-5** — `PC:1105-1112`, `:1978-1981`: freeze `Position()` while buffering.
8. **U-6** — `UI:645-658`: ignore `IsRepeat` for seek keys until §4.5 lands.
9. **U-8** — `RU:536-539`: commit through `SeekTarget.Clamp`.
10. **S-1** — `PA:1821`: do not latch `_eof` on an interrupted landing (mirror `:4093`).
11. **S-2** — `ST:537, 724, 1647`: idle timeout instead of a 60 s total.
12. **S-12** — `ST:713-725`: register `_inFlight` before `FillFromDisk`; re-check the epoch after it.
13. **S-13** — `ST:1298`: `LowWater ≥ _windowBytes / 2`.
14. **P-2** — `VO:629-630`: the two `0.5f *`.
15. **P-1 (guard)** — `FL:490`: `if (bps > 32) return FrameResult.Unsupported;` + adapter skip.
16. **P-8** — `FL:352, 365`: mask `0xFE`.
17. **E-2** — `BQ:120-122`, `DS:373`: denormal flush + release snap.
18. **F3 constants** — `WasapiPcm.cs:58`: `aheadMs: 2000, ringMs: 4000`; `FT:487`: clock thread `AboveNormal`.
19. **F4** — one `SetProcessInformation(ProcessPowerThrottling)` call at startup in `Win32Platform`.
20. **H-11** — `PA:2707`: `Interlocked.CompareExchange` re-entrancy latch on `Tick`.
21. **V-1 half** — `PH:665-666`: persist `SavedVolume` on drag end (or debounce 500 ms).
22. **Diagnostics** — surface `mmcss … registration ok/failed` (from `FormatSink`) and the current ring fill (`PcmAudioSession.BufferedFrames`) on the playback diagnostics page.
23. **Mute ramp** — `PS:1521`: use `3 × DefaultRampSamples` (≈ 32 ms) when the target is 0 or the previous target was 0.
24. **U-10** — `Playback.Os.cs:165`: `smtc.PlaybackRate = s.ContentRate`.

---

## 8. Proposed tests

All app tests are pure-class tests over `System`-only types (no engine, no source text); engine tests use the existing deterministic harness (`HeadlessAudioEndpoint`, `SignalGeneratorSource`, `FeedOnce`/`WorkerPumpOnce`/`ControlTickOnce`) and the zero-alloc pattern of `AudioVisualizerDemandTests.cs:147-162`.

**App (`Wavee.Tests`)**
- `GlitchLedgerTests` — verdict table (clean / gc-pauses / network-or-disk / producer-starved / device-late), longest and last stall, byte-wait fold.
- `ScrubModelTests`, `KeyboardScrubTests` — §4.8 and the accelerating ladder with an `IsRepeat` gate and one commit on key-up.
- `PlaybackReducerSeekTests` — `Seeked` under the load epoch is applied after a user seek; a `Position`/`Seeked` with a stale seek generation is dropped; the remote forward branch writes `PosMs/PosQpc`; a stale cluster does not overwrite an optimistic remote seek; `Position()` is frozen while `Buffering`; the `Buffering` edge freezes `PosMs`.
- `MuteRulesTests` — unmute at zero restores; a volume raise clears the sink mute; foreign mute/unmute round trip.
- `SeekRailTests` additions — the hold is released only by a matching-generation report within ±250 ms or by the timeout; the commit target runs through `SeekTarget.Clamp`.
- `NormalizationFactorTests` additions — the boost cap equals the limiter ceiling; Quiet/Normal/Loud pregains; album pair selection; `HeaderGainBytes = 160` parse of the four floats.
- `StarvePolicyTests` additions — an idle-timeout verdict for a reply that stalls after one slot.
- `RingLowWaterTests` — `LowWaterBytes ≥ window/2` for every window size including the 512 KiB floor.
- `VorbisAdapterTests` additions — two seeks 5 ms apart with the first landing interrupted leave `Exhausted == false` and land the second target; a page hole re-primes and pads the clock.
- `FlacTests` additions — 32-bit L/S, S/R, M/S frames decode exactly or report `Unsupported`; 4-ch and 7-ch fold-down weights; ID3v2-prefixed open.
- `VorbisTests` additions — synthetic floor-0 (orders 2 and 3) against the spec in `double`; a conformance pass over the Xiph test vectors (§11.4).

**Engine (`FluentGpu.Engine.Tests`)**
- `StarvationRecoveryTests` — with an empty ring: silence is submitted, the device is never stopped, the position does not advance, resume waits for the cushion, the cushion doubles per incident; the §0.2 stutter script (producer at 1.0–1.2× real time) yields ≤ 1 incident per 10 s; the legacy constants reproduce the loop (a regression pin for the harness itself).
- `DeadSinkTests` — `HeadlessAudioEndpoint(ready: false)` mid-fade reaches phase 3 in two wakes and reports exactly one sink failure.
- `EqStageCommandTests` — EQ changes are applied only at a block boundary; a count change on a live stage never reallocates (the stage is sized for the max band count once); the cross-ramp pin from `AudioGraphTests.cs:183-202` still holds.
- `SeekVoiceSwapTests` — the old voice keeps producing until the swap block; the equal-power sum stays within 0.1 dB of unity across the 5 ms window; the landing is sample-exact; a superseded prepare is disposed; `MaxStaleMs` fades the old voice to silence.
- `PcmRingSkipRewindTests` — `TrySkip`/`TryRewind` within the kept region, SPSC invariants, producer never overwrites the keep-behind span.
- `PolyphaseResamplerTests` — a 1 kHz–20 kHz sweep at 44.1→48: passband ripple ≤ 0.1 dB, images ≤ −85 dB (FFT oracle), block-size independence (byte-identical across 1/480/4096-frame pumps), EOF flush emits the trailing half kernel, `LatencySamples` matches the measured group delay.
- `LimiterLookaheadTests` — no output sample exceeds the ceiling; a +6 dB square wave's attack edge is a ramp of exactly `lookahead` samples; the reported latency equals the delay line.
- `ScrubGrainTests` — Hann COLA at 50 % overlap sums to unity; `NextGrainStart` velocity following and snap; zero allocations across 1000 grains.
- Zero-alloc pins — `ScrubGrainSource.Read`, `PcmRing.TrySkip/TryRewind`, `GlitchLedger.Record`, the F2 silence path in `RenderBlock` (inside the existing `AudioTripwire` scope), and `TapBlock` after the flagship ring lands.
- `ThreadCharacteristicsTests` — `IRtThreadCharacteristics.EnterDecode()` is called once per producer thread and reverted on exit (a recording fake).

**On-box (not CI)** — the §0.6 load runner, reporting the ledger; the `audio.seek.done latencyMs` distribution before and after §3.

---

## 9. Interplay with the fullscreen flagship FFT plan

The flagship plan (`docs/plans/wavee/fullscreen-flagship-implementation.md`, §1.2) adds an SPSC magnitude ring filled from `PcmAudioSession.TapBlock` (`PS:1594-1612`, called at `:1533`), a second demand tier and double-buffered magnitudes in `MediaEffects.cs` (`PublishVisualizerFrame` `:145-153`, `VisualizerFrame` `:17-21`, the lease/epoch machinery `:100-167`), and an `OutputDelayFrames` derived from submitted-vs-played frames (`:355-357, 501`) plus `StreamLatencyFrames`.

Where this audit's proposals touch the same code:

| This audit | Flagship | Conflict | Resolution |
|---|---|---|---|
| F2 edits `RenderBlock` `:1507-1513` (starvation) and adds a silence-submit path | edits the `TapBlock(buf, frames)` call at `:1533` and `TapBlock` itself | same method, different lines | **Land the flagship's engine wave first** (it is in flight); F2 then rebases. F2's silence block must still reach `TapBlock` (RMS 0 is the right visual for a dropout), so the silence path calls `TapBlock(silence, frames)` before `SubmitPending` — one extra line in F2, no change to the flagship code. |
| F2 shifts `_deviceFrameOrigin` by the silence written so the derived position freezes | `OutputDelayFrames ≈ (SubmittedAtTap − PlayedNow) + StreamLatencyFrames` | the flagship must not read `_submittedFrames` as a content clock | Stamp each tapped block with **`ctx.StartFrame` (the mixer content clock, `:1518`)**, not `_submittedFrames`; compute the output delay from the device padding (`DevicePaddingFrames`, `:501`, which is submitted − played and therefore unaffected by the origin shift) plus `StreamLatencyFrames`. The plan's own note ("the SPSC ring entry should stamp that, not `ctx.StartFrame`") should be reversed for this reason — silence blocks advance submitted frames but not content. |
| F1/F3/F5 edit `AudioFeedThread.cs` (ctor defaults, `ClockLoop`, `Start`) and `RingAudioSource.cs` | the FFT runs on the `FluentGpu.AudioClock` thread (`ControlTickOnce` → `PublishVisualizer` `:1107-1114`) | F1 raises that thread to AboveNormal + MMCSS "Audio" — **helps** the FFT's cadence | none; note that the FFT (2048-point, Hann, 48 bands) costs ≈ 50–100 µs per publish and is fine on the clock thread at 60 Hz. The F5 change (RT may render before `Playing`) means `PublishVisualizer` must also run in the `Ready`/`Buffering` arms once the RT has started rendering, or the first ~15 ms of a track show no spectrum — call it wherever `PublishPosition` is called. |
| §3 design A removes `CmdReset` from the seek path | `OutputDelayFrames` after a seek | the plan may assume `_submittedFrames` restarts at 0 on a seek | it no longer does; the padding-based formula above is seek-invariant. |
| F7 replaces `Task.Delay(2)` polls with signalled completions | — | none | — |
| H-4 routes EQ changes through the mixer command SPSC (`:310-312, 864-880`) | the plan adds no mixer commands | the 64-slot queue gains one command kind | none. |
| §4 scrubbing adds `MixVoice.Held` and a grain voice | the tap sees the mix — grains are tapped like any voice | none; a scrub shows its grains in the visualizer, which is desirable | — |

Sequencing: flagship engine wave (TapBlock ring, MediaEffects) → this audit's wave 0 (quick wins; none touch `TapBlock`/`MediaEffects`) → wave 1 (F1–F5, F7; `PcmAudioPlayer.cs` edits rebased onto the flagship's `RenderBlock`) → wave 2 (seek) → wave 3 (scrubbing) → wave 4 (quality). One owner per file per wave (see §10); `PcmAudioPlayer.cs` is the one file both plans edit, so its edits are serialised, never parallel.

---

## 10. Work packages — disjoint files, in waves

Each package names every file it may touch; no two packages in the same wave share a file. The orchestrator alone builds and tests (Debug + Release for both repos; the engine's VerticalSlice for engine waves).

**Wave 0 — telemetry and quick wins (all P1 fixes that are one-liners; ≈ 1 day, 5 agents)**
- WP-0a `[app]Playback/Playback.Glitch.cs` (new: `GlitchLedger`), `Playback/Playback.Audio.cs` §12 (`DrainXruns`, `FoldStall` hooks, `audio.glitch` / `audio.session.summary` events, the `Tick` re-entrancy latch, the parked target in `Seek`), `Screens/Diagnostics.UI.cs` (the "Playback health" card), tests `GlitchLedgerTests`.
- WP-0b `[engine]…/Audio/CrossfadeMixer.cs` (`PcmReady` without the clamp), `WsolaAudioSource.cs` (delete `RecoveryFrames`, Q-2 blend, R-9 fast-path return), `PcmAudioPlayer.cs:1275-1302` only (H-3), tests in `AudioFeedRaceTests`/`AudioPrefillTests`.
- WP-0c `[app]Playback/Playback.cs` (U-1, U-3, U-4, U-5), `Playback/Playback.Host.cs` (V-1 persist-on-release), `Shell/Shell.PlayerBar.cs` (U-8), `Shell/Shell.PlayerBar.UI.cs` (U-6 repeat gate, U-9), `Playback/Playback.Os.cs` (U-10), tests `PlaybackReducerSeekTests`, `MuteRulesTests`.
- WP-0d `[app]Playback/Playback.Audio.Vorbis.cs` (P-2, P-12), `Playback/Playback.Audio.Flac.cs` + `.Kernels.cs` (P-1 guard, P-4, P-8), the Vorbis adapter region of `Playback.Audio.cs:1775-1836` (S-1) — note WP-0a also edits `Playback.Audio.cs`: give WP-0d the adapter region by line range and WP-0a §11–§12 only, or merge them into one agent. Tests in `VorbisTests`, `FlacTests`, `AudioAdapterTests`.
- WP-0e `[app]Spotify/Spotify.Audio.Stream.cs` (S-2 idle timeout, S-12 in-flight registration, S-13 low water, P-6 landed-vs-refused, R-6 stats), tests `AudioStreamTests`.
- WP-0f `[engine]FluentGpu.Windows/Pal/Win32Platform.cs` (F4 power-throttling opt-out), `FluentGpu.Windows/Wasapi/WasapiPcm.cs:58` (F3 constants), `…/Audio/Biquad.cs` + `DspStages.cs:363-376` (E-2).

**Wave 1 — the halts (engine; after the flagship engine wave; ≈ 2 days, 3 agents)**
- WP-1a `[engine]…/Audio/AudioFeedThread.cs` (seam `EnterDecode`, clock thread priority, `TargetAheadFrames` getter, `GrowAhead` hook in `FeedOnce`), `RingAudioSource.cs` (producer MMCSS registration, `GrowAhead`, a signalled "ready" latch for F7), `FluentGpu.Windows/Wasapi/MmcssProAudio.cs` (`EnterDecode`), tests `ThreadCharacteristicsTests`, `AudioBufferSizingTests` updates.
- WP-1b `[engine]…/Audio/PcmAudioPlayer.cs` (F2 starvation redesign, F5 render-before-Playing gate, F7 signalled completions, H-4 EQ command routing + `CmdSetReplayGain`, H-8 lease accounting, H-13 removal, S-6 publish suppression, R-3 idle wait, R-11 volatiles), `DspStages.cs:103-234` (`EqStage` fixed-capacity state, command-applied changes, E-6 identity ramp), tests `StarvationRecoveryTests`, `EqStageCommandTests`, `DeadSinkTests`.
- WP-1c `[engine]FluentGpu.Engine.Tests/AudioStressTests.cs` (new: the §0.6 layer-1 stress model) and the on-box runner behind the app's headless probe (`[app]Screens/Diagnostics.Probe.cs`, a `--stress-audio` arm) — the runner is app-side, the model engine-side; disjoint from 1a/1b.

**Wave 2 — seeking (≈ 3 days, 4 agents)**
- WP-2a `[engine]…/Audio/PcmAudioPlayer.cs` (§3 design A `SeekAsync`, `TryJumpWithinRing`, the swap commands) — sequential after WP-1b (same file) — `PcmRing.cs` (`TrySkip`, `TryRewind`, keep-behind), `CrossfadeMixer.cs` (`CmdJumpWithinRing` envelope pair), tests `SeekVoiceSwapTests`, `PcmRingSkipRewindTests`.
- WP-2b `[app]Playback/Playback.Audio.cs` (seek mailbox, `PrepareAt` plumbing, S-5 "prepare lost" input, generation stamping on `Position`/`Seeked`, U-11 achieved frame), `Playback/Playback.Endgame.cs`, tests `PlaybackAudioTests`.
- WP-2c `[app]Spotify/Spotify.Audio.Stream.cs` (landing-time page index on the body, S-10 pending-range probe, S-11 behind-prefetch + protected keep-behind slots, H-12 `Grow` off the producer), `Playback/Playback.Audio.Ogg.cs` (index API: `PageIndex.AddExternal`, snapshot publication; P-9, P-10), the FLAC adapter region (S-9 window sizing, frame-header index), tests `OggTests`, `AudioStreamTests`.
- WP-2d `[app]Playback/Playback.cs` + `Playback/Playback.Transitions.cs` (`SeekGen` on state/inputs/effects, `MirrorRemote` window, `SeekGate` generation), `Shell/Shell.PlayerBar.cs` + `Shell/Shell.PlayerBar.UI.cs` (hold rule by generation ±250 ms, time-label preview, tooltip), tests `SeekRailTests`, `PlaybackReducerSeekTests`.

**Wave 3 — scrubbing (≈ 3 days, 4 agents)**
- WP-3a `[app]Playback/Playback.Scrub.cs` (new CORE `ScrubModel`, `KeyboardScrubLadder`), tests `ScrubModelTests`, `KeyboardScrubTests`.
- WP-3b `[engine]…/Audio/ScrubGrain.cs` (new pure window/scheduler), `CrossfadeMixer.cs` (`MixVoice.Held`), `PcmAudioPlayer.cs` (`BeginScrubAsync`/`UpdateScrub`/`EndScrubAsync`) — sequential after WP-2a — tests `ScrubGrainTests`, `AudioScrubSessionTests`.
- WP-3c `[app]Playback/Playback.Audio.Scrub.cs` (new SHELL `ScrubGrainSource`, the scrub session over `PrepareAtAsync`, promote-on-commit), tests `ScrubGrainSourceTests` with a synthetic decoder.
- WP-3d `[app]Shell/Shell.PlayerBar.UI.cs` (rail wiring, label preview, tooltip), `Shell/Shell.PlayerBar.Podcast.UI.cs` + `Shell/Shell.PlayerBar.cs` (keyboard ladder), `Playback/Playback.Host.cs` (`ScrubBegin/Move/Tick/End/Cancel` inputs and effects), `Playback/Playback.cs` (the Connect visual-only arm), `Playback/Playback.Video.cs` (throttled preview seeks while scrubbing). Note WP-3d and WP-2d both touch the player-bar files: wave 3 runs after wave 2.

**Wave 4 — quality (≈ 3 days, 3 agents)**
- WP-4a `[engine]…/Audio/PolyphaseResampler.cs` (new), `LinearResampler.cs` (delete; callers in `AudioDecode.cs` and the three app decoder sites switch to the new type), tests `PolyphaseResamplerTests`, `LinearResamplerTests` removed.
- WP-4b `[engine]…/Audio/DspStages.cs` (limiter lookahead/true-peak, E-4 preamp spec, E-8 shortcut), `Biquad.cs` (`double` state), `AudioGraph.cs` (`LimiterSpec` lookahead field), tests `LimiterLookaheadTests`, `AudioGraphTests` updates.
- WP-4c `[app]Spotify/Spotify.Audio.cs` (`HeaderGainBytes = 160`, album pair, mode pregains, the shared ceiling constant), `Playback/Playback.Audio.cs` normalization region (`NormalizationFactor` modes, live gain via `CmdSetReplayGain`), `Playback/Playback.Audio.Flac.cs` + the Vorbis comment reader (ReplayGain tags), `Screens/Settings.cs` (mode + album rows), loc keys, tests `NormalizationFactorTests`, `FlacTests`.

Deferred (not scheduled): P-3 streaming-body mode, P-7 unknown-length external bodies, exclusive mode, int16 dither, `eMultimedia` role.

---

## 11. Decoder: keep, specialise, or replace?

The question was whether to hand-roll a tiny, high-performance, Spotify-specific Ogg/Vorbis decoder instead of using NAudio/NVorbis. The answer starts from a fact: **Wavee already has one.** `Playback/Playback.Audio.Vorbis.cs` (2087 lines) is a from-scratch Vorbis I decoder, `Playback.Audio.Ogg.cs` (787) its own Ogg layer, `Playback.Audio.Flac*.cs` (1829) its own FLAC; NVorbis and NAudio are referenced nowhere, and the only third-party decoder is NLayer 1.15.0 for MP3 (`Wavee.csproj:189`). So the real question is whether what exists is correct, fast, and whether anything Spotify-specific remains worth doing.

### 11.1 Correctness against the Vorbis I spec

| Area | Verdict | Evidence |
|---|---|---|
| Floor 1 | **correct** | parse `VO:1849-1892`; X-list sort, duplicate rejection, low/high neighbour `:652-680`; post decode with the "end-of-packet ⇒ unused" rule `:469-504`; `render_point`/unwrap with the step-2 flag `:508-539`; `render_line` `:563-578`; the 256-literal inverse-dB table `:682-722` (§7.2.2–4, §9.2.4–7) |
| Floor 0 | **one bug** | odd order correct `:620-626`; even order lacks the spec's ½ on `p` and `q` `:629-630` (P-2). Never produced by libvorbis/Spotify; local pre-2002 files only; no test or fixture exists |
| Residue 0 / 1 / 2 | **correct** | begin/end clamped to the actual size `:735-736, 815-816`; 8 passes, classwords in pass 0, class map most-significant-first `:1948-1956`; cascade bits `:1921-1926`; type 0 stride `psize/dims` `:859-866`; type 1 sequential `:870-877`; type 2 interleave with the stereo fast path `:766-801`; per-channel do-not-decode and "all unused ⇒ skip" `:834, 847, 1597`; coupled-pair propagation `:1573-1577` (§8.6.2–3) |
| Codebooks (lookup 1 and 2) | **correct** | canonical codeword assignment (stb `compute_codewords`) `:334-357`; 10-bit bit-reversed fast table + left-aligned sorted fallback with prefix re-check `:377-463`; `lookup1_values`, `sequence_p`, `float32_unpack` `:152-158, 1784-1815`; the single-entry errata case `:391` (P-12: only when the length is 1 — libvorbis accepts any length) |
| Window shapes, long/short transitions | **correct** | `w[i] = sin(π/2·sin²((i+½)/(n/2)·π/2))` `:911-920`; left/right boundaries `:1550-1553`; the previous block's `[n/2, n)` saved `:1662-1663`, `_tailLen`/`_lapLen` `:1664-1665`; output `blocksize(prev)/4 + blocksize(cur)/4` for every size pair (worked through in the Ogg/Vorbis sweep) |
| Channel coupling | **correct** | the four magnitude/angle cases fold to the spec's table `:1222-1262`; applied last step first `:1606-1607`, on residues before the floor product `:1609-1624` (§4.3.5) |
| First packet and post-seek pre-roll | **correct** | `Prime()` at open and after every seek `:1493-1499, 1487`; `PA:1826`; the first decoded packet emits 0 frames and only seeds the overlap `:1627, 1661-1667`; one primed packet is a complete pre-roll even across a size switch (the right slope's shape is fixed by the previous packet's own `nextLong` flag) |
| End-of-stream trimming by granule | **correct** | a packet is placed as `granule − frames` when it ends a page (`PA:1182`); the EOS granule truncates via `TrimTail` (`OG:774-782`, `PA:1187`); the first page's shortfall becomes `GaplessInfo.LeadInFrames` (`PA:1254-1260, 1529-1535`) |
| Corrupt packets | **weak** | a bad/truncated packet returns early without touching the overlap state `:1540-1548` and the adapter `continue`s `PA:1686, 1691`; a page hole only drops the open packet `OG:371`. The result is one packet of wrong overlap (a click) and a clock that runs short until the next granule pin (S-7). libvorbisfile reports `OV_HOLE` and restarts synthesis. Fix: surface the hole, `Prime()` on it, pad the clock gap. Resync of the container itself is correct (`PA:1649-1656`) |

Net: two real defects (P-2 in a code path Spotify never exercises; S-7 in a path only a damaged stream exercises) in a decoder that otherwise matches the spec clause by clause and the ffmpeg oracle within 2 LSB over 3 s of two fixtures (`VorbisTests.cs:156-185`).

### 11.2 Performance

**Cost per second of 44.1 kHz stereo, derived from the hot loops.** libvorbis at Spotify's rungs uses a 256/2048 block pair (fixtures confirm, `Fixtures/ogg/README.md`), so steady state is ≈ 43 long packets per second (1024 output frames each), more only around transients. Per packet, per channel: floor 1 decode + render ≈ 2 µs (≤ 250 posts, line segments, table lookups); residue 2 — the hottest loop (`VO:735-803`) — at 320 kbps ≈ 7.4 kbit per packet ≈ 1500–3000 codewords through `DecodeScalar` (`:450-461`: one 10-bit table load on the fast path, a binary search for codes longer than 10 bits) each followed by `dims` adds (2 for most books at 320k), ≈ 15–30 µs; IMDCT 2048 (`:926-1130`, stb's structure, Vector128 step-3 butterflies, scalar fused last stages) ≈ 10–15 µs per channel; window/overlap-add, uncoupling, interleave (Vector256/128, `:1205-1333`) ≈ 2–3 µs. Total ≈ 60–110 µs per packet ⇒ **≈ 3–5 ms of CPU per second of audio at 320 kbps, i.e. 200–350× real time on one modern core (≈ 0.3–0.5 % of a core)**; at 96 kbps the residue is ≈ 3× lighter ⇒ ≈ 400–600×. The repo's own throughput test pins a conservative CI floor of **≥ 50× real time** (`VorbisTests.cs:985-993`) and **zero allocation across 200 packets** (`:933-958`; a second `Open` of the same setup also allocates nothing, `:967-977`). The app measures decode throughput live per codec (`PA:4243-4247`, `CodecXRealtime`).

**Hottest loops and SIMD.** (1) Residue 2 is inherently serial per codeword (the Huffman decode is a data dependency); the VQ adds are 2–4 wide, below SIMD width — nothing to gain at 320k, a little at 96k (dims 4–8 books). (2) The IMDCT's `Step3Ld654` fused stages and the final permutation/twiddle pass are scalar (`:1035+`); vectorising them is a ≈ 20–30 % IMDCT gain, i.e. ≈ 5 % of a decode that is already < 1 % of a core. (3) Huffman is a single table load on the fast path; the slow path (codes > 10 bits) is rare in libvorbis books. (4) Floor-1 render is a gather over the dB table — not vectorisable cheaply. **Verdict: no missed SIMD worth the risk.**

**Against the alternatives.** stb_vorbis / libvorbis (native) decode at ≈ 300–600× real time on the same core — the same class as this decoder. NVorbis (managed) historically allocates per packet (residue buffers, LINQ in setup) and runs ≈ 50–150× with gen-0 churn; adopting it would *regress* the zero-allocation invariant the engine's producer thread relies on and add a dependency with its own seek/granule semantics to reconcile with `VorbisClock`.

**Could decode speed cause the halts?** No. Even at the 50× floor, one second of audio costs 20 ms; the ring holds 500 ms. To empty it the producer must be denied the CPU for ≈ 500 ms, which is scheduling (§0.3 H1), or be blocked on bytes (H3) — not arithmetic. The decoder's cost is invisible in the halt analysis.

### 11.3 Spotify-specific specialisation

| Idea | Verdict | Why |
|---|---|---|
| Cache parsed setup headers per (rung, codebook hash) across tracks | **worth doing, small** | Spotify's libvorbis setup is byte-identical within a rung (38–44 books, 420 KB of VQ lattices at 96k per the fixture notes). The `VorbisWorkingSet` pool already keeps the grow-only *tables* across tracks so a re-`Open` allocates nothing (`PA:1373-1428`; pinned at `VorbisTests.cs:967-977`) — but the *parse* (canonical codes, fast tables, VQ unpack) still runs per `Open`, ≈ 0.5–2 ms (derived). With §3's design A opening a second decoder per committed seek and §4 re-centring a scrub decoder, a hash-keyed cache of the parsed `Decoder` state (key = SHA-256 of the setup packet; invalidate on a mismatch) turns that into ≈ 0.1 ms. Measure first: log the setup-parse time in `audio.open`. |
| A fast path for the fixed 44.1 kHz stereo layout | **already present** | `ch == 2` residue interleave (`VO:766-781`), `InterleaveStereo` with the gain fold (`:1287-1333`), block sizes fixed per `Open` with precomputed IMDCT tables and windows in the working set. |
| Drop floor 0 / residue 0/1 for Spotify streams | **no** | They cost nothing when the setup does not use them (dispatch is per setup, `:1559-1562, 1591-1597`); removing them saves code, not time, and breaks dropped local files. Fix P-2 instead and add the missing test. |
| Decode ahead on a worker so seeks are served from PCM | **yes — but it is the engine ring (§3 B, F3), not the decoder** | `RingAudioSource` is that worker; the change is a 2 s ahead / 1 s behind ring with consumer skip/rewind. |
| Landing-time page index | **yes (§3 C)** | indexing fetched slots makes any seek into the compressed window 0-probe; the `PageIndex` structure exists (`OG:300-602`). |

### 11.4 Test coverage

Present (`Wavee.Tests`): `VorbisTests.cs` (18 facts: ffmpeg oracle ≤ 2 LSB on `pink-320` and `vbr-q8` for the first 3 s, SNR on `sine-440`, block switching on `sweep-48k`, header fields, codebook errata, the allocation gate, the 50× throughput floor, vector-vs-scalar kernel parity, impulse IMDCT at 64/128/256/2048); `OggTests.cs` (8: CRC-32 against a bit-serial reference and every fixture page; in-memory spanning, zero-length, hole, foreign-serial and bad-CRC pages; seek probe bounds cold ≤ 2–3, warm ≤ 1, 0 after a play-through); `AudioAdapterTests.cs` (15: lead-in, EOS trim, sample-exact seek landing byte-equal to a linear decode, interrupt handling); `FlacTests.cs` (44: 22 vendored vectors + synthetic variable-blocksize/32-bit/65535-block/655350 Hz streams, MD5 oracle, bit reader vs reference over every width, SIMD parity, seek probe count ≤ 4). Fixtures: six libvorbis files at 320k/96k/VBR/mono/48k/100 ms pages (`Fixtures/ogg/README.md`).

Missing: (1) the **Xiph conformance vectors** (libvorbis's `test/` suite and the public "vorbis-test-vectors": chained streams, 5.1, 8-bit/128 kHz extremes, floor 0 from early Xiph encoders) decoded against reference PCM over their full length — today the oracle covers 3 s of two files; (2) a **160 kbps rung fixture** (96 and 320 exist) and a synthetic **Spotify-wrapped** file (167-byte prefix then `OggS`) exercising the container-offset path end to end; (3) **floor 0 and residue 0/1** fixtures — no modern encoder emits them, so synthesise packets from the spec in the test (the existing `FlacTests` builder pattern); (4) a **fuzz harness** over `Ogg.Reader.NextPacket`, `Vorbis.Decoder.Open` and `DecodePacket` (a SharpFuzz/libFuzzer-style loop seeded with the fixtures, mutating bytes, asserting "no exception, no out-of-bounds, bounded allocation") — the setup parser validates indices and the bit reader is bounds-proven, but P-11 (a hostile header can allocate 256 MB) shows what only a fuzzer finds; (5) **hole/bad-packet behaviour** once S-7 is implemented (re-prime, clock padding); (6) **two-seek interrupt** (S-1).

### 11.5 NLayer (MP3, local files only)

NLayer is a managed MPEG-1/2/2.5 Layer I–III decoder. The app drives it through `ByteSourceStream` (`PA:2202-2250`) and pulls into **one** reused `float[]` (`_pull`, `PA:1965` — "never one per block"), folds the gain and conforms channels in `PullConform` (scalar loop, R-7-class nit), and reads LAME/Xing gapless numbers itself before handing over the stream (`Mp3Tag`, 529-sample convention, `PA:2039-2051`). The package ships as a DLL without source in this checkout, so its internal per-frame allocation could not be verified here; from its design (pre-allocated layer buffers, a frame reader) steady-state churn is expected to be small gen-0, if any. It plays only local MP3 files (Spotify never serves MP3), on the producer thread. **Verdict: not a perf or GC risk worth a rewrite.** Add one measurement test (`Mp3_decoding_two_hundred_frames_allocates_under_N_bytes`) so the number is known, and revisit only if the §0.5 ledger ever attributes incidents to GC during local MP3 playback.

### 11.6 Recommendation

**Keep the hand-rolled decoder; do not replace it with NVorbis or NAudio; specialise only where §3/§4 create new demand.** Rationale: the decoder already is the small, fast, Spotify-shaped implementation the question imagines — spec-correct clause by clause (two defects, both outside Spotify's path, fixed in wave 0), zero-allocation after open (test-pinned), ≈ 200–350× real time (native class; 3–5× faster than the managed alternative with none of its GC churn), with a sample-exact seek landing and a one-packet pre-roll. Replacing it would cost months and regress the engine's RT invariants for no audible gain; stripping floor 0 / residue 0/1 would save nothing. What is worth doing: fix P-2 and S-7, add a setup-parse cache keyed by hash (because the seek redesign opens decoders more often), and raise the test floor with the Xiph conformance vectors, a full-length oracle and a fuzz loop. The halts are not a decoder problem.

---

## 12. Decisions (2026-10-02)

The owner accepted every recommendation in this audit. D1 ship H-1 + F2 + the glitch telemetry together; D2 assume hybrid-core laptops, F4 in the first wave; D3 seek design A with the single-decoder "seek marker" fallback when no decoder lease is free; D4 scrub defaults 60 ms / −6 dB / 150 ms park / 5-15-30 s ladder; D5 Quiet/Normal/Loud + album mode now, Loud limits; D6 first-launch volume at the taper position giving ≈ −6 dB (`SavedVolume = 0.794` → 0.5005); D7 limiter moved pre-volume (post-volume chain re-verified attenuation-only); D8 2 s decode-ahead + 1 s kept behind. The implementation-grade plan is `docs/plans/wavee/playback-smoothness-implementation.md`; its waves that touch `PcmAudioPlayer.cs` `RenderBlock`/`TapBlock`, `AudioFeedThread.cs` or `MediaEffects.cs` are sequenced after the flagship plan's engine wave E, with the `OutputDelayFrames` correction from §9 carried as an explicit cross-plan note.
