# Timecode output (#154): LTC on a second output, CLX as a module, over one playback timeline tap

> **Design and briefs.** Design board with the screens and spec diagrams:
> https://claude.ai/artifact/AuK3VbdYyxcUaHKUSpezrM (private until shared). Shareable brief for the reporters, as a
> page: https://claude.ai/artifact/CA3yb1AE568aNaoWhs87ug and as a PDF:
> [`briefs/154-timecode-brief.pdf`](briefs/154-timecode-brief.pdf). Builds on Mix (#48) for D6.

Target release: **0.4 "Drift"**. Starting code: `release/0.3.13` (the app and fluent-gpu). Every file:line below was read at that branch; "~" marks a line that is
within a few lines of the declaration. Anything not confirmed from code or from a fetched source is marked
**UNVERIFIED**.

## Cross-plan notes (0.4)

| Groundwork | Owner | Who uses it |
|---|---|---|
| **G1** local library (folders setting, tag scan, index, matching) | **#240** | #154 gets local tracks for free: they play through the same `PcmAudioSession`, so the tap covers them with no extra work |
| **G2a** playback timeline tap (what is audible, anchored to the device clock) | **#154 (this plan, phase 1)** | #48 reads it and publishes its transition into it; the CLX module and any future observer module read it |
| **G2b** aux audio output (a second, independent endpoint rendering generated PCM) | **#154 (this plan, phase 2)** | #154's LTC generator. #48 may reuse it later for a headphone cue (after 0.4) |
| **G2c** module observer hook (SDK capability `timeline`, host notification `playback/timeline`) | **#154 (this plan, phase 4)** | the bundled CLX module; third-party sync modules |
| Mix (#48 rev 3): Spotify's playlist Mix read, honoured and edited. The lens and recipe wire code, the auto planner, the preset-to-curve tables, the scheduling and the Connect metadata of a mixed context are #48's. Mixed joins play at **rate 1.0** in 0.4; beatmatching and any per-voice rate are after 0.4 | **#48** | #48 only *publishes into* G2a through `Playback.Timeline.SetTransition` (signature in section 3.2). It defines nothing in G2 |
| Custom palettes | **#184** | independent of the other three |

**What #48 publishes into the tap** (its section 3.9, rev 4; nothing else). Which track is the master during a
**mixed** join follows #48's owner decision D14, and both answers use the API as written:

| | #48 D14 (b), its recommendation: the player keeps the outgoing track until it is over | #48 D14 (a): flip at the overlap's start |
|---|---|---|
| Overlap's first frame | `Playback.Timeline.SetTransition(incoming, startMixFrame, endMixFrame, Master.Outgoing)`; identity unchanged | the same call with `Master.Incoming`, with the incoming identity starting at its in-point |
| Overlap's end | `ClearTransition()` and the incoming identity, starting at in-point + overlap | `ClearTransition()` |
| Cancel inside the window | `ClearTransition()` | `ClearTransition()` |

Windows are 2.5 to 16 s long and are scheduled about 2 s ahead. Captured in-points are 2874, 4144 and 7794 ms. The
published duration of a track in a mixed playlist is its effective end, which can be shorter than the file: its
audible end with (b), its mix-out point with (a).

Rate: nothing. #48 does not touch `AudioTimelineAnchor.Rate` in 0.4. What LTC and the `playback/timeline`
notification report in each case is in section 3.4 ("In a mixed playlist").

**Suggested 0.4 build order across the four plans:** #240 and #184 in parallel first (no shared files with this
plan) -> **#154 phase 1 (the timeline tap)** -> #48 (builds on the tap) -> #154 phases 2 to 5 (aux output, LTC,
observer hook, CLX module). Phases 2 and 4 of this plan touch no file #48 touches, so they can also run beside #48.

---

## 1. What was asked, and what exists today

### 1.1 The request

Issue #154 (milestone 0.4 Drift, `area: playback`, P3): output a **timecode signal alongside the normal audio**, so
lighting and VJ software (Resolume, TouchDesigner, ShowKontrol) can follow the song. The requesters added that
it must not replace the audio (comment by s0), that a two-PC setup is a likely use, and that **CLX** is their
preferred protocol while LTC and others have the wider installed base. The issue proposed a "Waveform encoding"
drop-down in Settings > Sound. The owner answered that features like this may fit better as extensions on a
Wavee SDK built on the playback-module basis.

### 1.2 The protocols (from the pages the issue links, fetched 2026-11-22)

| Protocol | Transport | What it carries | Source |
|---|---|---|---|
| **LTC** (SMPTE linear timecode) | an **audio signal** | hh:mm:ss:ff, usually 24, 25, 30 or 29.97 drop-frame | arcanewizards.com `/protocols/smpte`: "Audio-based SMPTE protocol"; frame rates "usually 24FPS, 25FPS, 30FPS and 29.97FPS (drop-frame)" |
| MTC | MIDI | the same SMPTE time | same page |
| Art-Net timecode | network (lighting) | the same SMPTE time | same page (port and packet layout not on that page) |
| TCNet | network (Pioneer, ShowKontrol) | not read | index page only |
| **CLX** (Clockworks) | **UDP port 3650**, unicast, broadcast or multicast `239.0.0.1` | per-deck position in **float seconds** (not frames), pitch, BPM, beat, length; track metadata; mixer state | `ClockworksTech/CLX-Spec` README (repo sidebar: Apache-2.0) |

CLX wire format as the spec README states it: every datagram is `[1-byte packet type][MessagePack map]`. Types:
`0x00` Control, `0x01` Deck ("sent at minimum 60Hz"; the notes also say it "may be throttled to 60fps"), `0x02`
Meta (on load or event), `0x03` Waveform request (to port 7000), `0x04` Event, `0x05` Binary. Deck keys: `Pitch`,
`Position` (seconds), `Position2` (beat.fraction), `NormalizedPosition`, `BPM`, `Length` (all float64), `EQLow`,
`EQMid`, `EQHigh` (float32), `Deck` (uint8, 0 = A), `Beat` (uint8, 1 to 4), `Ord` (uint8, optional). Meta keys:
`Deck`, `Title`, `Artist`, `Album`, `FilePath`. Receivers "will only parse the first object of a MessagePack
payload, and only if it is a Map". There is no version field. A joining client broadcasts a "magic packet"
`0x09` to ask for a Meta/Control resync; **its byte layout is not in the README (UNVERIFIED)**.

Verification tooling named in the issue: Arcane Wizards' **Timecode Toolbox** (MIT). Its README lists **Art-Net
timecode (in and out) and TCNet (in)** only, with MIDI timecode planned and "Local Audio Player (not LTC)"; the
CLX docs page says the Toolbox takes **CLX as an input**. The two disagree and neither was checked against the
running tool (**UNVERIFIED**). It does **not** read LTC, so the requesters' own tool can check our CLX but not our LTC.

The LTC bit layout in section 3.4 is written from memory of SMPTE ST 12-1 and was **not confirmed against the
standard in this research (UNVERIFIED)**. Confirm it against the standard before phase 3 ships. Do not copy code
from `libltc` (LGPL-3): the encoder below is written from the layout, not from that library.

### 1.3 What exists in the code

**The audio stack is a single stereo stream per session, shared mode.**

- `IAudioEndpoint` (`fluent-gpu/src/FluentGpu.Engine/Media/Playback/Audio/AudioClock.cs:161`) bundles one sink and
  one played-frames clock. `IAudioClockSource` (`MediaSeams.cs:360`) gives `TryGetPlayed(out frames, out qpc)`,
  `StreamLatencyFrames` and `MixRate`. `IBufferedAudioSink` (`MediaSeams.cs:338`) gives `WritableFrames`,
  `WaitForWritable`.
- The Windows leaf `WasapiAudioDevice` (`fluent-gpu/src/FluentGpu.Windows/Wasapi/WasapiAudioDevice.cs:20`) opens
  the console default or **a chosen endpoint id** (ctor `:98`), always in **shared mode** at the device's own mix
  format (`Initialize(AUDCLNT_SHAREMODE_SHARED, EVENTCALLBACK, 100 ms, ...)`, `:542`), and converts on `Write`
  for the device's channel count and sample type (`:507-537`). Nothing in the engine opens exclusive mode.
- `PcmAudioSession` (`PcmAudioPlayer.cs:428`) owns exactly **one** `_out` sink and `_clock` (`:438-439`). The RT
  render (`RenderBlock`, mixer -> master EQ/limiter -> spectrum tap -> master gain -> balance -> transport ramp
  -> level tap, `:2955-2970`) writes one interleaved buffer. There is no second bus, no insert point after the
  master, and no per-channel routing.
- Position is **derived from the device clock**, never stored: `AudioClockPosition.Project` (`AudioClock.cs:14`,
  `:78~`) extrapolates the last played-frame sample by QPC and subtracts the measured stream latency plus the DSP
  latency, so it tracks what is audible now. `PcmAudioSession.PublishPosition` (`:3241`) samples it on the
  control tick and pushes `sink.Position(...)`; with a rate source it maps clock frames to content frames
  (`ContentFrameAt`, `:845~`).
- Two RT-safe taps already exist and are the precedent for the timeline tap: `AudioLevelMailbox`
  (`AudioLevelMailbox.cs:7`, a version-counter seqlock, single writer, readers never wait) and `SpectrumRing`
  (`SpectrumRing.cs:18`, SPSC ring with an arm epoch). Both publish to the control thread, never from it.
- Speed: WSOLA rate sources per voice (`_rateSources`, `PcmAudioPlayer.cs:829-830`), `PlaybackRate` (`:834`),
  `SetRate` (`:2623`). In the app, speed applies to **episodes only** (`Playback.SpeedApplies`,
  `WaveeMusic/src/apps/Wavee/Playback/Playback.Speed.cs:18`): a music track always plays at 1.0 today.
- A session is **not** process-lifetime: a track switch may build a new session and hand the running stream over
  (`HandOverOutputAsync`, `:638`; `KeepOutput`, `:251`). Inside a session, gapless joins and crossfades add voices
  (`TryAddCrossfadeVoice`), and a device change rebuilds only the sink (`RebuildSink`, `:3320`).

**Track identity lives in the app, not the engine.** `Playback.Audio` (`Playback/Playback.Audio.cs`) keeps the
live session (`s_session`, `:489`), the playing id (`s_id`), where the active voice started on the session clock
(`s_activeStartMs`) and the anchors (`s_anchorClock`, `:516`). The reported position is `ActivePositionMs()`
(`:3964`): the engine position minus `s_activeStartMs` (tracks), or `ContentPositionFrames` (episodes). It is
published by a **200 ms** timer (`TickMs`, `:89`; `Tick`, `:3985`). Identity flips to the incoming track **at the
start of a crossfade** (`CommitCrossfade`, `:3465~`, sets `s_id = s_prepId` and `s_activeStartMs = rawPos` when the
fade is committed) and at the join frame of a gapless join (`AnnounceGaplessJoin`, `:3419~`).

**Device choice.** `Playback.Audio.Devices` (`:218`) is the roster (`LocalAudioDevice`, `:271`), filled from
`WasapiPcm.EnumerateEndpoints()` (`WasapiPcm.cs:17`); `OpenOutput` (`:394~`) opens the chosen id or the Windows
default; the saved choice is `Platform.Keys.OutputDeviceId` (`Platform/Platform.cs:239`). Settings shows it as
dial `sound.outputDevice` (`Screens/SettingsSpec.Sound.cs:83-87`).

**Headless.** `--inspect` and `--silent` render into `PacedSilentEndpoint` (`Playback.Audio.cs:4898`,
`UseSilentEndpoint`, `:698~`), which runs the whole graph with no device.

**Modules source audio; nothing observes or outputs.** A module is an out-of-process executable speaking
JSON-RPC over stdio (`docs/guide/playback-modules.md` sections 1 and 4). The method table
(`src/apps/Wavee.Sdk/Protocol/ModuleMethods.cs:9-77`) has `playback/match`, `playback/resolve`, `stream/*`,
`module/page`, and module->host notifications `playback/metadata`, `playback/expired`, `module/status`. The
capability constants are `playback`, `match`, `metadata`, `fallback` (`Platform/Modules.Host.cs:299-301`). There
is **no** host->module "what is playing" notification, no output capability, and a module process is stopped
after 10 idle minutes unless it holds a stream lease (`IdleSweepAsync`, `Modules.Host.cs:676`;
`AcquireStreamLease`, `:527`). Host services a module may call are registered per method with a permission
(`ModuleHostServices.Register`, `:968`).

**Settings are one declared spec that all three themes render.** `SettingsSpec.Categories`
(`Screens/SettingsSpec.cs:339`) lists categories; each is groups of dials built with `Make(id, kind, ...)`. All
three themes iterate `body.Groups` generically (`Themes/WinUi/.../WinUiSettings.Rows.cs:48`,
`Themes/Classic/.../ClassicSettings.Body.cs:94`, `Themes/Zune/.../ZuneSettings.Body.cs:55`) and special-case a
few dial ids (`sound.outputDevice`: WinUI `Rows.cs:265`, Classic `Body.cs:227`, Zune `Body.cs:304`).

Not present anywhere in either repo: LTC, SMPTE, timecode, CLX, MessagePack, a second output stream.

---

## 2. Product decisions for the owner

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D1 | **Built in, or a module?** | (a) everything built in; (b) everything a module; (c) split | **(c) Split.** The tap, the aux output and **LTC are built in**; **CLX is a bundled first-party module** on a new observer hook. Reasoning in 2.1 |
| D2 | Which protocols in 0.4 | LTC; CLX; Art-Net; MTC; TCNet | **LTC + CLX.** LTC is the universal one (the issue's title asks for a waveform); CLX is what the requesters use and what their Toolbox can check. Art-Net timecode and MTC are small follow-up modules (after 0.4) |
| D3 | Where LTC comes out | (a) a **second output device** (a spare interface output, or a virtual cable into Resolume on the same PC); (b) one channel of the main device (music mono left, LTC right); (c) channels 3+ of a multichannel interface | **(a) in 0.4.** It needs no change to the main RT render path. (b) and (c) need a post-master insert or a multichannel mix format in the engine: after 0.4 |
| D4 | What the timecode means | (a) **track-relative**: every track starts at a start timecode; (b) continuous across the queue; (c) a per-track or per-playlist cue sheet (track X starts at 02:00:00:00) | **(a) in 0.4**, with a start-hour choice (00:00:00:00 or 01:00:00:00, the usual show convention; **UNVERIFIED** which the requesters prefer: ask them). (c) is what prepared shows want with LTC, since LTC cannot name the track: after 0.4. CLX already carries title and artist, so CLX users are not blocked |
| D5 | LTC frame rate | 24, 25, 29.97 drop-frame, 30 | Offer all four; default **30** (the common DJ/VJ default; **UNVERIFIED**, ask the requesters) |
| D6 | During an overlap, which track is "the" timecode | (a) the incoming track from the overlap's start; (b) the outgoing track until it is over; (c) a flip at the midpoint | **The timecode names the track the player names**, so there is one truth on screen and on the wire. **Plain crossfade and gapless join:** (a), unchanged: it is how `s_id` flips today. **Mixed join (#48):** the answer follows **#48's decision D14** and may differ from a plain crossfade. With #48's recommended D14 (b) the player keeps the outgoing track until it is over, #48 passes `Master.Outgoing`, and LTC **stays on the outgoing track through the whole overlap, then relocates once** to the incoming track at in-point + overlap. With D14 (a) it is (a) here: LTC relocates at the overlap's first frame to the incoming track's in-point. Nothing in G2 changes with the choice: `SetTransition`'s `master` argument and the moment #48 publishes the incoming identity carry it. A midpoint flip (c), which the DJ R&D session may prefer, is also expressible (a second `SetTransition` call with the other master plus the identity publish at that frame) but would make the timecode disagree with the player bar for half the window: not recommended for 0.4 |
| D7 | On pause | (a) LTC goes silent; (b) keep sending the same frame | **(a)**: a stopped reader is the normal "transport stopped" state. CLX keeps sending Deck packets with a fixed position (the spec sends continuously) |
| D8 | Level and trim | fixed, or user dials | Two dials: **level** (default -12 dBFS, **UNVERIFIED** as the best default) and **offset** (-500..+500 ms, as `Platform.Keys.StageSyncOffsetMs` already does for the visualizer) |
| D9 | Exclusive mode | shared; exclusive | **Shared** (what the engine opens everywhere). Exclusive is not needed for a 2.4 kHz square wave; see risks for the enhancements caveat |

### 2.1 Module or built in, weighed honestly

What a module can and cannot do today, from the code:

- A module is a separate full-trust process. It **could** open its own WASAPI device and generate LTC itself:
  the LTC device has its own clock in either design, so "in the app's process" buys no sample lock. What it
  needs is the **playhead with a clock anchor**, and no such thing reaches a module today.
- stdio JSON-RPC has scheduling jitter of milliseconds and no bound. That rules out pushing *audio* or
  *per-frame positions* to a module. It does **not** rule out pushing an **anchor**: "at QPC tick T the playhead
  was at P microseconds, moving at rate R". QPC is one counter for the whole machine
  (`Stopwatch.GetTimestamp()` in a .NET module reads the same counter), so the module extrapolates locally and
  the transport delay drops out. One message per change plus a 1 Hz keepalive is enough.
- A module has no Settings rows, no device picker and no access to the engine's WASAPI leaf (it references only
  `Wavee.Sdk`). An LTC module would have to ship its own WASAPI interop, its own device list and a setup card,
  and the app would still need the tap and the observer hook. That is more work than built-in LTC, with a worse
  Settings experience.

So the split is: **what needs a device and Settings is built in (LTC); what is "send the playhead somewhere over
the network" is a module (CLX now; Art-Net, MTC, OSC, TCNet later, by anyone).** The observer hook is the SDK
piece the owner described, and CLX is its first real consumer, shipped in the box so the path is proven.

The trust question raised in the thread (vetted store or use-at-your-own-risk) is not decided here: 0.4 ships
only the bundled first-party CLX module (`publisher == "wavee"`), which is the posture the module guide already
has for YouTube, Twitch and Radio. An observer module sees track titles and positions; the capability is
declared in the manifest so a later install prompt can name it.

---

## 3. Architecture

```
 fluent-gpu (engine)                         WaveeMusic (app)                          out of process
┌────────────────────────────────┐   ┌──────────────────────────────────────┐   ┌─────────────────────────┐
│ PcmAudioSession                │   │ Playback.Audio (pump, identity)      │   │ wavee.clx module        │
│  PublishPosition (ctl tick)    │   │   load / join / crossfade / seek ────┼─┐ │  OnTimelineAsync(state) │
│   └► AudioTimelineMailbox ─────┼──►│ Playback.Timeline  (G2a)             │ │ │  60 Hz Deck packets     │
│      (seqlock, QPC anchor)     │   │   TryRead(out Snapshot)  any thread  │◄┘ │  UDP 239.0.0.1:3650     │
│                                │   │   SetTransition(...)  ◄── #48        │   └──────────▲──────────────┘
│ AuxAudioOutput  (G2b)          │   │        │                │            │              │ playback/timeline
│  own thread, own endpoint      │   │        ▼                ▼            │              │ (JSON-RPC notification:
│  IAuxAudioGenerator.Render ◄───┼───┤ Timecode.LtcGenerator   ModuleHost ──┼──────────────┘  on change + 1 Hz)
│  AuxBlock.FirstFrameAudibleQpc │   │  (chases the snapshot)  timeline fan-out (G2c)       │
└───────────┬────────────────────┘   │ Timecode.Host: settings ► open/close │
            ▼                        │ Settings ▸ Sound ▸ Timecode          │
   WasapiAudioDevice(endpointId)     │ Diagnostics ▸ Playback: Timecode     │
   (the LTC device, shared mode)     └──────────────────────────────────────┘
```

### 3.1 Threads

| Thread | Exists | Does | Allocation |
|---|---|---|---|
| RT feed (`AudioFeedThread`, MMCSS Pro Audio) | yes | renders the music. **Unchanged by this plan** | zero |
| Engine control tick (clock poll, `PublishPosition`) | yes | additionally publishes `AudioTimelineAnchor` into the mailbox: a few field writes | zero |
| App pump (`Playback.Audio` chain, 200 ms tick) | yes | publishes identity (track, start, duration, transition) into `Playback.Timeline` at the sites that already change it | zero on the tick; a publish on a track change may touch a string that already exists |
| **Aux output thread** ("FluentGpu.AudioAux") | **new, engine** | waits for the aux device, calls `IAuxAudioGenerator.Render`, writes | zero after start |
| Module fan-out (thread pool, on `Timeline.Changed` + a 1 s timer) | new, app | serializes one `TimelineState` per change per observer module | allocates (a JSON message per change: not a steady-frame path) |
| UI thread | yes | Settings rows, diagnostics page | nothing per frame: the diagnostics page polls at 1 s like `HealthPollMs` |

Nothing in this plan runs on, locks against, or adds work to the RT feed thread.

### 3.2 G2a: the playback timeline tap

**Engine half** (new `fluent-gpu/src/FluentGpu.Engine/Media/Playback/Audio/AudioTimeline.cs`; publish site in
`PcmAudioPlayer.cs` `PublishPosition`, `:3241`):

```csharp
namespace FluentGpu.Media;

/// <summary>What ONE voice of the session is playing, anchored to the device clock: the voice named by
/// <see cref="VoiceId"/> (the session's active voice, the one the app last made active with SetActiveVoice).
/// Every field but <see cref="MixFrame"/> is that voice's, never another voice's and never a session-wide value.
/// Extrapolate with the *At methods; never store a position.</summary>
public readonly record struct AudioTimelineAnchor(
    long Qpc100ns,        // the instant the anchor is true for (same domain as IAudioClockSource.TryGetPlayed's qpc)
    double ContentFrame,  // the session position PublishPosition publishes today, AUDIBLE (stream + DSP latency subtracted)
    double SourceFrame,   // the audible frame INSIDE the active voice's own source: WsolaAudioSource.SourceFrameAt of that voice
                          // (so it starts at the voice's start position, not at 0); = ContentPositionFrames
    double MixFrame,      // the audible frame in the mixer domain (PcmAudioSession.SampleClock / ConsumeSeq): where scheduled
                          // envelopes and transitions live. Advances at MixRate whatever any voice's rate is
    int MixRate,          // frames per second of all three
    double Rate,          // source frames per output frame of the active voice. In 0.4 the session has ONE rate
                          // (PlaybackRate, written into every voice each block), so this is that number
    bool Advancing,       // false while paused, held, starved or buffering
    long VoiceId,         // PcmAudioSession.ActiveVoiceIdValue
    long Epoch)           // bumps on a discontinuity: seek, active-voice change, rate change, start/stop, device rebuild
{
    public double ContentFrameAt(long qpc100ns) => Advancing ? ContentFrame + Elapsed(qpc100ns) * Rate : ContentFrame;
    public double SourceFrameAt(long qpc100ns) => Advancing ? SourceFrame + Elapsed(qpc100ns) * Rate : SourceFrame;
    public double MixFrameAt(long qpc100ns) => Advancing ? MixFrame + Elapsed(qpc100ns) : MixFrame;
    /// <summary>The instant a mixer frame (a scheduled transition edge) becomes audible, seen from this anchor.</summary>
    public long QpcOfMixFrame(long mixFrame) => Qpc100ns + (long)((mixFrame - MixFrame) * 1e7 / MixRate);
    double Elapsed(long qpc100ns) => (qpc100ns - Qpc100ns) / 1e7 * MixRate;
}

public sealed partial class PcmAudioSession
{
    /// <summary>Any thread, wait-free, allocation-free. False until the first anchor was published.</summary>
    public bool TryReadTimeline(out AudioTimelineAnchor anchor);
}
```

The mailbox is the `AudioLevelMailbox` discipline widened to a struct: one writer (the control tick), an odd/even
version counter, `Volatile` payload writes, a full fence before the validating re-read. The publisher computes the
value `PublishPosition` already computes (`contentFrame`, `:3255-3258`) and decides the epoch itself: it bumps when
`VoiceId`, `Rate` or `Advancing` changed, or when the new source frame differs from the old anchor's extrapolation
by more than 2 ms. That catches seeks, in-ring jumps, swaps and rebuilds without hooking each of the seventeen
mixer commands.

**Which voice.** The anchor describes the session's **active voice**: `SourceFrame` is read through that voice's
own `WsolaAudioSource.SourceFrameAt` (`WsolaAudioSource.cs:270`, the same call `ContentFrameAt` makes at
`PcmAudioPlayer.cs:845~` through `_activeRateSource`, which `SetActiveVoice` points at that voice's source,
`:1157`). `Rate` is the session's one rate: `RenderBlock` writes `PlaybackRate` into every voice's
`WsolaAudioSource` each block (`PcmAudioPlayer.cs:2905-2906`), and nothing in 0.4 changes that. It is 1.0 for
every music track, a #48 mixed join included, and the episode speed for an episode; a speed change is a step, so
it bumps the epoch like any other discontinuity.

**A voice that starts past its head (an in-point).** `SourceFrame` is the voice's own source frame, so an
incoming track that #48 starts at its in-point (2874 ms in one captured join; #48's engine item E3 prepares the
voice there) reads 2874 ms at its first audible frame with no arithmetic
(`WsolaAudioSource.InitialSourceFrame`, `:51`, is inside `SourceFrameAt`). `ContentFrame` keeps today's
session-continuous meaning for the identities that subtract a start.

**A transition's edges as time.** #48 schedules a join at an exact mixer frame about 2 s ahead, with a window of
2.5 to 16 s, and reports both edges in mixer frames. `MixFrame` is what turns them into time:
`QpcOfMixFrame(startMixFrame)` is when that edge is heard (it may already lie slightly in the past when the
announce arrives), valid while the anchor's epoch holds and re-derived from each new anchor (a pause in between simply moves
it). `MixFrame` is defined from the mixer's `ConsumeSeq` minus what is still queued in the device
(`OutputDelayFrames`, `:794`), **not** from `AudioClockPosition.Project`, whose domain a seek re-bases
(`Rebase`, `AudioClock.cs:52~`). **To confirm in phase 1:** `PublishPosition` returns early while a seek or
replacement holds `_replacementGate` (`:3245`), and the visualizer publish runs "Playing only" (`:2032` comment);
the anchor must also be published once on the transition to paused or held, so `Advancing = false` is not stale
for a whole tick. If the control tick does not run while paused, add one publish in the pause path.

**App half** (new `WaveeMusic/src/apps/Wavee/Playback/Playback.Timeline.cs`):

```csharp
namespace Wavee;

public static partial class Playback
{
    /// <summary>What is audible on THIS PC, for anything that must follow the playhead off the UI thread.</summary>
    public static class Timeline
    {
        [Flags] public enum Flags : byte { None = 0, Playing = 1, Transition = 2, Coarse = 4 }
        public enum Master : byte { Outgoing, Incoming }

        public readonly struct Snapshot
        {
            public readonly EntityId Track;            // the master track (EntityId.None when nothing local plays)
            public readonly long AnchorQpc100ns;       // the instant PositionMicros is true for
            public readonly long PositionMicros;       // position inside Track, audible, at AnchorQpc100ns
            public readonly long DurationMs;           // 0 when unknown (live). In a mixed playlist: the track's effective end (#48 D14), not the file's length
            public readonly double Rate;               // 1.0 for music (a #48 mixed join included); the episode speed
            public readonly Flags State;
            public readonly long Epoch;                // changes on any discontinuity (engine epoch + identity changes)
            public readonly EntityId Incoming;         // valid with Flags.Transition
            public readonly long TransitionStartQpc100ns, TransitionEndQpc100ns;
            public readonly Master MasterVoice;        // valid with Flags.Transition
            public long PositionMicrosAt(long qpc100ns);   // extrapolates; never below 0; NOT clamped to DurationMs (a mixed
                                                           // track still sounds past its mix-out); holds when not Playing
        }

        /// <summary>Any thread, wait-free, allocation-free.</summary>
        public static bool TryRead(out Snapshot snapshot);

        /// <summary>Raised on the pump chain when Epoch changes (track, seek, pause, resume, rate, transition set or
        /// cleared) and when the published duration changes (a mix plan landing mid-track; Epoch stays). Never per tick.</summary>
        public static event Action? Changed;

        /// <summary>#48's planner: a transition is audible between two session mix frames (PcmAudioSession.SampleClock
        /// domain; the start may lie slightly behind the audible edge or ahead of it); <paramref name="master"/> says which track the timecode names
        /// during it. Calling it again inside the window with the other master moves the flip (a midpoint flip is
        /// two calls). Pump chain.</summary>
        public static void SetTransition(EntityId incoming, long startMixFrame, long endMixFrame, Master master);
        public static void ClearTransition();

        // pump chain only: identity, called where Playback.Audio already changes s_id / s_activeStartMs.
        // sourceDomain: false = position is the anchor's ContentFrame minus startMs (today's tracks; startMs is signed);
        //               true  = position is the anchor's SourceFrame, startMs ignored (today's episodes: required whenever
        //                       the rate is not 1.0).
        // durationMs is what the reducer holds: the file's length, or the track's EFFECTIVE END while #48 has a mix planned (its D14).
        internal static void PublishIdentity(PcmAudioSession? session, EntityId track, long startMs, long durationMs, bool sourceDomain);
    }
}
```

`TryRead` composes two seqlock reads: the app's identity record (track, start offset, duration, transition) and
the live session's `TryReadTimeline`. Position = the anchor's `ContentFrame` time minus the identity's start (the
same subtraction `ActivePositionMs` does at `:3980`), or the anchor's `SourceFrame` time for a source-domain
identity (what episodes read today through `ContentPositionFrames`, `:3972`). A non-zero start position of an
incoming track is representable both ways: in the source domain it is simply what `SourceFrame` reads; in the
clock domain `startMs` is signed, so `startMs = sessionPositionAtStart - cueMs` gives `cueMs` at the first frame.
At rate 1.0 the two agree, so #48's announce can publish either: its section 3.6 swaps identity the way
`CommitCrossfade` does with the playhead anchored at the incoming track's start offset, which in this signature
is `startMs = rawPos - offsetMs`, where `offsetMs` is `InStartMs` when it publishes at the overlap's start
(#48 D14 (a)) and `InStartMs + OutDurationMs` when it publishes at the overlap's end (D14 (b)). `durationMs` is passed through as the reducer holds it, so in a mixed playlist
the snapshot's `DurationMs` is the track's effective end; a new duration for the same track (a mix plan landing or being
withdrawn mid-track) re-publishes identity without an epoch bump. `Snapshot.Rate` is the anchor's `Rate`, and
the transition's two QPC fields come from `QpcOfMixFrame`. Identity is published
at the sites that already write those fields: the two session installs (`Playback.Audio.cs:1212-1225`,
`:1297-1305`), teardown (`:1415-1432`), `AnnounceGaplessJoin` (`:3430-3447`), `CommitCrossfade` (`:3490-3502`)
and the seek re-anchor (`:3846`). A crossfade without #48 sets `Flags.Transition` itself from the fade's start
frame and length, with `Master.Incoming` (decision D6).

`Flags.Coarse` is for a music video that owns the deck: the song's audio session is parked (`s_parked`, `:4446`)
and the sound comes from the video engine. 0.4 publishes the video's position signal with a wall-clock anchor
and this flag. **UNVERIFIED** how accurate that is against the audible video sound; LTC keeps running from it
and the diagnostics row shows "coarse". When playback is on another Connect device, `Track` is `None` and no
timecode is sent (there is no local sound to sync to).

### 3.3 G2b: the aux audio output (engine work, fluent-gpu)

New `fluent-gpu/src/FluentGpu.Engine/Media/Playback/Audio/AuxAudioOutput.cs`, plus a factory on the Windows leaf.

```csharp
namespace FluentGpu.Media;

/// <summary>One block of an aux stream. The generator slaves itself to the main playback by time, not by sample count.</summary>
public readonly record struct AuxBlock(
    long FirstFrameAudibleQpc100ns,  // when the first frame of this block leaves the aux device (queue + stream latency included)
    int SampleRate, int Channels, long BlockIndex);

/// <summary>Fills an aux stream. Called on the aux thread: no allocation, no lock, no I/O.</summary>
public interface IAuxAudioGenerator
{
    void Render(Span<float> interleaved, int frames, in AuxBlock block);
}

public readonly record struct AuxOutputStats(long Blocks, long Underruns, long StreamLatencyFrames, int SampleRate, int Channels);

/// <summary>A second, independent output: its own endpoint, its own thread, its own clock. It never touches the
/// music session. A lost device goes Retrying on the AudioDeviceRecoveryPolicy ladder and reopens.</summary>
public sealed class AuxAudioOutput : IDisposable
{
    public AuxAudioOutput(Func<IAudioEndpoint> open, IAuxAudioGenerator generator,
        IRtThreadCharacteristics? rt = null, int blockFrames = 480);
    public IReadSignal<AudioDeviceState> State { get; }
    public AuxOutputStats Stats { get; }
    public void Start();
    public void Stop();      // ramps the generator's last block to zero, stops the device
    public void Dispose();
}
```

```csharp
// fluent-gpu/src/FluentGpu.Windows/Wasapi/WasapiPcm.cs
public static AuxAudioOutput CreateAuxOutput(string endpointId, IAuxAudioGenerator generator);
```

The loop: `WaitForWritable` on the endpoint's own event (the endpoint must be an `IBufferedAudioSink`, which
`WasapiAudioDevice` is), every block read `TryGetPlayed(out played, out qpc)` and compute
`FirstFrameAudibleQpc = qpc + (written - played + StreamLatencyFrames) * 1e7 / rate`, call `Render`, `Write`.
`TryGetPlayed` is a kernel transition the main RT thread deliberately avoids (`MediaSeams.cs:364`); on the aux
thread it is acceptable because a late LTC block costs a reader a few frames of freewheel, not an audible
glitch. The thread registers through `IRtThreadCharacteristics.EnterDecode()` (the "Audio" MMCSS class,
`AudioFeedThread.cs:25`), below the music's RT feed. It reuses `WasapiAudioDevice` unchanged: shared mode, the
device's own mix format and channel count, typed `AudioDeviceLostException` on loss.

Why not a second voice or bus inside `PcmAudioSession`: the session has one sink and its lifetime is a track or a
hand-over, while timecode must survive track switches; and anything added to `RenderBlock` is on the RT path
with the AudioTripwire around it. A separate object keeps the music path byte-identical.

### 3.4 LTC (app, built in)

New files under `WaveeMusic/src/apps/Wavee/Playback/`: `Timecode.cs` (rates, drop-frame math, the encoder),
`Timecode.Ltc.cs` (the generator and the self-check reader), `Timecode.Host.cs` (settings -> open/close).

**Frame.** 80 bits, sent LSB first, bi-phase mark: a transition at every bit boundary, one more in the middle of
a `1`. Layout (**UNVERIFIED against SMPTE ST 12-1, see 1.2**): frame units 0-3, frame tens 8-9, drop-frame flag
10, seconds units 16-19, seconds tens 24-26, minutes units 32-35, minutes tens 40-42, hours units 48-51, hours
tens 56-57, user bits in the eight 4-bit gaps (sent as zero), sync word in 64-79 = `0011 1111 1111 1101`. The
polarity-correction bit (27 at 30 and 24 fps, 59 at 25 fps) makes the count of ones even, so every frame starts
on the same edge.

```csharp
namespace Wavee;

public enum LtcRate : byte { Fps24, Fps25, Fps2997Drop, Fps30 }

public static class Timecode
{
    public static double FramesPerSecond(LtcRate r) => r switch
    { LtcRate.Fps24 => 24, LtcRate.Fps25 => 25, LtcRate.Fps2997Drop => 30000.0 / 1001.0, _ => 30 };
    static int Nominal(LtcRate r) => r switch { LtcRate.Fps24 => 24, LtcRate.Fps25 => 25, _ => 30 };

    /// <summary>Frame count since 00:00:00:00 -> the label on that frame. Drop-frame skips labels :00 and :01 at the
    /// start of every minute that is not a multiple of ten (17 982 frames per ten minutes).</summary>
    public static void Label(long frame, LtcRate rate, out int h, out int m, out int s, out int f)
    {
        int n = Nominal(rate);
        if (rate == LtcRate.Fps2997Drop)
        {
            long tens = frame / 17982, rest = frame % 17982;
            frame += 18 * tens + (rest < 2 ? 0 : 2 * ((rest - 2) / 1798));
        }
        f = (int)(frame % n); long sec = frame / n;
        s = (int)(sec % 60); m = (int)(sec / 60 % 60); h = (int)(sec / 3600 % 24);
    }

    /// <summary>The 80-bit word for a label: bits 0-63 in <paramref name="lo"/>, the sync word in <paramref name="hi"/>.</summary>
    public static void Pack(int h, int m, int s, int f, LtcRate rate, out ulong lo, out ushort hi)
    {
        lo = (ulong)(f % 10) | (ulong)(f / 10) << 8 | (ulong)(s % 10) << 16 | (ulong)(s / 10) << 24
           | (ulong)(m % 10) << 32 | (ulong)(m / 10) << 40 | (ulong)(h % 10) << 48 | (ulong)(h / 10) << 56;
        if (rate == LtcRate.Fps2997Drop) lo |= 1UL << 10;
        hi = 0xBFFC;                                            // bits 64-79 = 0011 1111 1111 1101, bit 64 first
        int ones = System.Numerics.BitOperations.PopCount(lo) + System.Numerics.BitOperations.PopCount(hi);
        if ((ones & 1) != 0) lo |= 1UL << (rate == LtcRate.Fps25 ? 59 : 27);
    }
}

/// <summary>Bi-phase mark modulator. 160 half-bit cells per frame; the cell clock is a phase accumulator, so
/// 29.97 fps, a 44.1 kHz device and a varispeed all need no special case. No allocation.</summary>
public struct LtcEncoder
{
    ulong _lo; ushort _hi; int _cell; double _phase; float _level;
    public long Frame;                                          // the frame being sent
    public LtcRate Rate;

    public void Relocate(long frame, double cellPhase, float amplitude)
    {
        Frame = frame; _cell = (int)(cellPhase * 160); _phase = cellPhase * 160 - _cell;
        _level = _level < 0 ? -amplitude : amplitude; Load();
    }

    /// <summary>Position inside the timeline, in frames with a fraction: what the reader will decode next.</summary>
    public readonly double FramePosition => Frame + (_cell + _phase) / 160.0;

    public void Render(Span<float> dst, int frames, int channels, double cellsPerSample)
    {
        for (int i = 0; i < frames; i++)
        {
            _phase += cellsPerSample;
            while (_phase >= 1.0) { _phase -= 1.0; Step(); }
            for (int c = 0; c < channels; c++) dst[i * channels + c] = _level;
        }
    }

    void Step()
    {
        int bit = _cell >> 1;
        bool one = bit < 64 ? (_lo >> bit & 1) != 0 : (_hi >> (bit - 64) & 1) != 0;
        if ((_cell & 1) == 0 || one) _level = -_level;          // every bit boundary; mid-bit only for a 1
        if (++_cell == 160) { _cell = 0; Frame++; Load(); }
    }

    void Load()
    {
        Timecode.Label(Frame, Rate, out int h, out int m, out int s, out int f);
        Timecode.Pack(h, m, s, f, Rate, out _lo, out _hi);
    }
}
```

**The generator chases the timeline; it is never sample-locked to it** (two devices, two crystals):

```csharp
sealed class LtcGenerator : IAuxAudioGenerator
{
    public void Render(Span<float> dst, int frames, in AuxBlock b)
    {
        if (!Playback.Timeline.TryRead(out var t) || (t.State & Playback.Timeline.Flags.Playing) == 0)
        { FadeToSilence(dst, frames, b.Channels); _locked = false; return; }

        double fps = Timecode.FramesPerSecond(_rate);
        double seconds = (t.PositionMicrosAt(b.FirstFrameAudibleQpc100ns + _offsetTicks)) / 1e6;
        double target = _startFrames + seconds * fps;                    // where the reader should be when this block is heard
        double error = target - _enc.FramePosition;                      // in LTC frames
        if (!_locked || t.Epoch != _epoch || Math.Abs(error) > 0.5)
        { _enc.Relocate((long)Math.Floor(target), target - Math.Floor(target), _amplitude); _epoch = t.Epoch; _locked = true; _relocations++; error = 0; }
        double nominal = fps * 160.0 * t.Rate / b.SampleRate;            // cells per sample at the playing rate
        double slew = Math.Clamp(error * 0.05, -0.002, 0.002);           // absorb clock drift, at most 0.2 %
        _enc.Render(dst, frames, b.Channels, nominal * (1.0 + slew));
        _reader?.Feed(dst, frames, b.Channels);                          // self-check, only while leased (section 3.7)
    }
}
```

Behaviour that falls out of this, per event:

| Event | Tap | LTC | CLX |
|---|---|---|---|
| Seek | epoch changes, new anchor | relocates on the next block (about 10 ms plus the aux queue): readers see a jump, which is what a relocated transport looks like | next Deck packet carries the new position |
| Pause / buffering / starvation | `Playing` clears | 5 ms ramp to silence | Deck packets continue, position fixed |
| Resume | epoch changes | relocates and runs | continues |
| Gapless join | identity flips at the join frame | relocates to the new track's start timecode | Meta for the new track, Deck from 0 |
| Crossfade (today) | `Transition`, `Master.Incoming` from the fade's start | relocates to the incoming track at the fade's start | same |
| #48 mixed join (rate 1.0, window 2.5 to 16 s), **#48 D14 (b)** | overlap start: `Transition` with `Master.Outgoing`, identity unchanged; overlap end: `ClearTransition` and the incoming identity at in-point + overlap | stays on the outgoing track through the overlap, to its audible end; then **one** relocation to the incoming track at in-point + overlap | Deck and Meta stay on the outgoing track through the overlap; then Meta for the incoming track and `Position` from in-point + overlap. `Length` = the track's audible end |
| the same join, **#48 D14 (a)** | overlap start: identity flips to the incoming track at its in-point, `Transition` with `Master.Incoming`; `ClearTransition` at the close-out | **one** relocation at the overlap's first frame, from the outgoing track's mix-out to the incoming track's in-point; the outgoing track's fading tail has no timecode | Meta for the incoming track at the overlap's start; `Position` from the in-point; `Length` = its mix-out |
| Episode speed 0.5x to 3x | `Rate` | varispeed LTC. Readers tolerate some speed deviation; how much is reader-specific (**UNVERIFIED**). Outside 0.8x to 1.25x the generator sends real-time frames and relocates as needed instead | position advances faster |
| Device rebuild on the main output | epoch changes | relocates | unaffected |
| LTC device unplugged | n/a | `AuxAudioOutput.State` = Retrying then Faulted; the Settings row says "Not responding" like the main picker | unaffected |

**In a mixed playlist (#48).** Timecode stays **track-relative and true to the file**: the label is always
start timecode + the position inside the track's own audio, so a cue programmed at 1:00 of a song fires at 1:00
whether or not the playlist is mixed. The generator follows whatever identity the tap holds, so which track it
names during the overlap is #48's decision D14 (this plan's D6), with no code of its own for either answer:

| | #48 D14 (b), recommended there: the player keeps the outgoing track until it is over | #48 D14 (a): the player flips at the overlap's start |
|---|---|---|
| During the overlap | LTC keeps running on the **outgoing** track, without a break, to its audible end | LTC is already on the **incoming** track |
| The one relocation | at the overlap's **end**, to the incoming track at in-point + overlap: with in-point 2874 ms, an 8 s overlap and a 01:00:00:00 start at 30 fps, the first label is 01:00:10:26 | at the overlap's **first frame**, to the incoming track at its in-point: the first label is 01:00:02:26 |
| Labels never sent for that play | the incoming track's from its head to in-point + overlap (it was audible, fading in, for the last `overlap` of that) | the incoming track's before its in-point; the outgoing track's fading tail |
| Reported duration (`Snapshot.DurationMs`, `TimelineState.DurationMs`, CLX `Length`, the divisor of `NormalizedPosition`) | the track's **audible end** (mix-out start + overlap) | the **mix-out point** |
| `playback/timeline` during the overlap | `Uri` = the outgoing track, its position, `Transition { IncomingUri, StartTicks, EndTicks, Master = "outgoing" }`; at the end one notification with `Uri` = the incoming track, `PositionMicros` = in-point + overlap, `Transition = null` | `Uri` = the incoming track, `PositionMicros` from its in-point, `Transition { IncomingUri = the same track, ..., Master = "incoming" }` until the close-out |

Common to both:

- the duration is the reducer's, so it is the number the player bar and Wavee's Connect state show, and it can
  be shorter than the file. It can change while the track plays (a mix plan landing, the user switching Mix off):
  observers get one notification with the same epoch, and LTC, which never reads the duration, is unaffected;
- with (b) the engine's active voice must stay the outgoing voice until the overlap's end, because the anchor
  describes the active voice: #48 moves `SetActiveVoice` together with its identity publish (its section 3.6a
  item 4 is the statement; unbuilt, so not traced);
- timing of the flip: the identity publish runs on the pump's 200 ms tick once the mixer has passed the frame,
  while that frame becomes audible one device queue later (100 ms buffer plus the stream latency), so the
  relocation lands within about a tick of the audible edge, early or late. `PositionMicrosAt` is therefore not
  clamped to the duration: for those milliseconds the outgoing track's timecode simply runs on. Today's crossfade
  and gapless joins have the same tolerance. Making the flip frame-exact is in "After 0.4";
- a cancel inside the window (a seek, a skip): with (b) nothing relocates because the identity never changed;
  with (a) it is an ordinary seek or skip on the incoming track.

**Latency between the two devices.** The tap's position is already "audible now" on the main device (stream and
DSP latency subtracted in `AudioClockPosition.Project`). `AuxBlock.FirstFrameAudibleQpc100ns` is "audible then" on
the aux device. Both lean on the latency each driver reports, which is reliable on wired interfaces and often
wrong on Bluetooth; the **offset dial** (`_offsetTicks`) is the user's correction, and the diagnostics page shows
both reported latencies so the number is not a guess.

**Lifecycle** (`Timecode.Host`): when `timecode.ltc.enabled` and a device id are set, open
`WasapiPcm.CreateAuxOutput(id, generator)` on the pump chain; close it when the setting clears or the app shuts
down (`Playback.Audio.Shutdown`). Under the silent backend (`Playback.Audio.SilentEndpoint`) the aux endpoint is
a `PacedSilentEndpoint` too, so the generator runs headless.

### 3.5 Settings (all three themes, one spec)

New group `timecode` in the Sound category (`Screens/SettingsSpec.Sound.cs`, after `output`), new keys in
`Platform/Platform.cs` beside `OutputDeviceId` (`:239`), verbs in `SettingsVerbs`, a fact in `ISettingsFacts`
(`Screens/SettingsFacts.cs:47~`) for the parent switch.

| Dial id | Kind | Key (default) | Notes |
|---|---|---|---|
| `sound.timecode` | Toggle | `timecode.ltc.enabled` (false) | parent of the next five. Enabled only when a device is chosen: `SettingsEnablement.Off(reason)` otherwise |
| `sound.timecode.device` | Choice, `liveChoices` | `timecode.ltc.deviceId` ("") | the `Playback.Audio.Devices` roster **minus the output the music is on**, first entry "Choose an output". No "Match Windows" entry: LTC on the speakers is a loud square wave |
| `sound.timecode.rate` | Choice | `timecode.ltc.rate` (3 = 30) | 24 · 25 · 29.97 drop-frame · 30 |
| `sound.timecode.start` | Choice | `timecode.startHour` (1) | 00:00:00:00 · 01:00:00:00 |
| `sound.timecode.level` | Slider | `timecode.ltc.levelDb` (-12) | -24..0 dB |
| `sound.timecode.offset` | Slider | `timecode.offsetMs` (0) | -500..+500 ms, applied to LTC and CLX |
| `sound.timecode.clx` | Toggle | `timecode.clx.enabled` (false) | starts the bundled CLX module (section 3.6). Independent of the LTC switch |
| `sound.timecode.status` | Fact | read-only | "01:02:14:07 · locked" / "Not responding" / "Waiting for playback", refreshed at 1 s |

Changing the music's output to the LTC device clears the LTC device (and says so in the row's caption), so the
two can never be the same endpoint.

**Classic** (`ClassicSettings.Body.cs`: section header with an icon per (category, group), `:162`; combo width per
dial id, `:227`). Add `("sound", "timecode") => Icons.<clock glyph>` and the device dial to the wide-combo list.

```
 Sound
 ─────────────────────────────────────────────────────────────────────────────
 ◷ Timecode
   Send timecode                                                     [  off ]
   For lighting and video software. Plays a timecode signal on a
   second output while the music plays as usual.
     Timecode output          [ Choose an output                       ▾ ]
     Frame rate               [ 30 fps                                 ▾ ]
     Each track starts at     [ 01:00:00:00                            ▾ ]
     Level                    ├────────●──────────┤   -12 dB
     Offset                   ├─────────●─────────┤   0 ms
     Now sending              Waiting for playback
   Send CLX on the network                                           [  off ]
   For Clockworks-compatible software on this network.
```

**WinUI** (`WinUiSettings.Rows.cs`: groups as card lists, `:48`; the parent toggle becomes an expander whose
children are the rows with `parent:`; `sound.outputDevice` has its own card at `:265`, and the timecode device
reuses the standard Choice row, not that card).

```
 Timecode
 ┌───────────────────────────────────────────────────────────────────────────┐
 │ ◷  Send timecode                                              Off  (○ )  ⌄│
 │    Plays a timecode signal on a second output                             │
 ├───────────────────────────────────────────────────────────────────────────┤
 │    Timecode output                          [ Choose an output        ⌄ ] │
 │    Frame rate                               [ 30 fps                  ⌄ ] │
 │    Each track starts at                     [ 01:00:00:00             ⌄ ] │
 │    Level                                    ──────●────────    -12 dB     │
 │    Offset                                   ───────●───────      0 ms     │
 │    Now sending                              Waiting for playback          │
 └───────────────────────────────────────────────────────────────────────────┘
 ┌───────────────────────────────────────────────────────────────────────────┐
 │ ⇄  Send CLX on the network                                    Off  (○ )   │
 └───────────────────────────────────────────────────────────────────────────┘
```

**Zune** (`ZuneSettings.Body.cs` builds a `GroupBlock` per group, `:55`; combos that need the wide treatment are
listed at `:304`; Sound's bespoke sections are in `ZuneSettings.Sound.cs`). The group is a lowercase heading in
Zune's display type with the rows under it; nothing is shrunk to fit: a long device name wraps.

```
 timecode
 send timecode                                              OFF
 plays a timecode signal on a second output
   timecode output      choose an output  ▾
   frame rate           30 fps  ▾
   each track starts at 01:00:00:00  ▾
   level                ━━━━━━●━━━━━━━   -12 dB
   offset               ━━━━━━━●━━━━━━    0 ms
   now sending          waiting for playback
 send clx on the network                                    OFF
```

No layout jump: the child rows are always laid out (greyed while the parent is off), the status Fact has a
fixed line, and the WinUI expander uses the house expander the equalizer group already uses (`sound.equalizer`
with `parent:` children, `SettingsSpec.Sound.cs:60-78`). The settings search finds it by aliases
`["ltc", "smpte", "timecode", "clx", "resolume", "vj"]`. Strings go in `assets/loc/*.json` under
`settings.sound.timecode.*` for every culture; a change file goes in `changes/unreleased/` with `issues: [154]`.

### 3.6 G2c: the module observer hook, and the CLX module

**SDK** (`src/apps/Wavee.Sdk`): one capability, one notification, one virtual. Additive, so
`protocolVersion` stays 1 (the guide's rule: capabilities are declared, a module that does not declare
`timeline` never receives it).

```csharp
// Wavee.Sdk/Protocol/ModuleMethods.cs
/// <summary>host→module notification: the playback timeline changed (capability <c>timeline</c>).</summary>
public const string Timeline = "playback/timeline";

// Wavee.Sdk/Playback.cs
/// <param name="Uri">The playing item's uri (spotify:track:…, spotify:local:…, wavee:module:…); null = nothing plays here.</param>
/// <param name="PositionMicros">Position inside the item at <paramref name="AnchorTicks"/>.</param>
/// <param name="AnchorTicks">A <c>Stopwatch.GetTimestamp()</c> value: the same counter in every process on the machine.</param>
/// <param name="TicksPerSecond"><c>Stopwatch.Frequency</c> of the host, for a module not written in .NET.</param>
/// <param name="Rate">Content seconds per second (1.0 for music; an episode speed).</param>
/// <param name="Epoch">Changes on every discontinuity. Same epoch = the same straight line; only re-anchored.</param>
public sealed record TimelineState(string? Uri, string? Title, string? Artist, string? Album,
    long PositionMicros, long AnchorTicks, long TicksPerSecond, long DurationMs, double Rate, bool Playing,
    long Epoch, double? Bpm, TimelineTransition? Transition);

/// <param name="Master"><c>"outgoing"</c> or <c>"incoming"</c>: which item <see cref="TimelineState.Uri"/> names during the transition.</param>
public sealed record TimelineTransition(string IncomingUri, long StartTicks, long EndTicks, string Master);

// Wavee.Sdk/WaveeModule.cs
/// <summary>The app's playback timeline changed, or one second passed. Extrapolate between calls:
/// position = PositionMicros + (now − AnchorTicks) / TicksPerSecond × 1e6 × Rate while Playing.</summary>
public virtual ValueTask OnTimelineAsync(TimelineState state, CancellationToken ct) => default;
```

`ModuleRunner` dispatches the notification to `OnTimelineAsync`; `ModuleTestHost` gets a `PushTimeline(state)`
for hand-running a module; `SdkJsonContext` gains the two records.

**Host** (`Platform/Modules.Host.cs`): `ModuleCapabilities.Timeline = "timeline"`; a `TimelineFanOut` that
subscribes `Playback.Timeline.Changed`, runs a 1 s keepalive timer **only while an observer is enabled**, builds
one `TimelineState` (title, artist, album from the now-playing projection; `Bpm` from the track's decoded audio
attributes: **UNVERIFIED** accessor, #48 owns surfacing tempo to playback code) and sends it to each enabled
module that declares the capability. An enabled observer module is started when the first state is sent and
holds a lease so the idle sweep leaves it alone: generalize `AcquireStreamLease` (`:527`) to
`AcquireLease(string why)`, replacing the stream-only one (no second mechanism). "Enabled" in 0.4 is the host
setting `timecode.clx.enabled` for the id `wavee.clx`; a general per-module enable switch is after 0.4.

**CLX module** (new `src/apps/modules/Wavee.Module.Clx/`: `Program.cs`, `ClxModule.cs`, `MsgPack.cs`,
`wavee-module.json` with `"capabilities": ["timeline"]`, `publisher: "wavee"`; the csproj copies the Radio
module's, `PublishAot=true`, references only `Wavee.Sdk`):

- one `UdpClient`, destination `239.0.0.1:3650` (the spec's multicast address and port), TTL 1;
- on `OnTimelineAsync`: store the state; when the uri changed send **Meta** `0x02`
  (`Deck`, `Title`, `Artist`, `Album`, `FilePath` = "");
- a 60 Hz `PeriodicTimer` sends **Deck** `0x01` for deck 0 with `Position` extrapolated from the anchor,
  `Length`, `NormalizedPosition`, `BPM` (0 when unknown), `Pitch`, `Beat` = 1, `Position2` = 0, EQs = 0, `Ord`
  incrementing; and **Control** `0x00` at 1 Hz with `UpfaderA` = 1, `Crossfader` = 0, `Active` = 0
  (**UNVERIFIED**: the spec README gives `Active` as uint8 without saying whether it is a deck index or a
  flag; check the `cpp/` reference in the spec repo before coding);
- `MsgPack.cs` is a ~60-line writer for a flat map of str keys to float64 / float32 / uint8 / str values into a
  pooled buffer: no MessagePack package, no reflection (AOT);
- the `0x09` resync request is answered by re-sending Meta and Control once its layout is confirmed
  (**UNVERIFIED**, see 1.2); until then Meta is re-sent every 5 s so a late joiner still gets the title.

The doc `docs/guide/playback-modules.md` section 4 gains the `playback/timeline` row and a short "Observer
modules" section; section 3's capability table gains `timeline`.

### 3.7 Diagnostics and the self-check

- **Diagnostics > Playback** (`Screens/Diagnostics.Pages.cs`, `RuntimeNodes`, `:80`): a "Timecode" section,
  always there. Rows: tap (track, position, rate, epoch, flags), main output latency, LTC device, state, mix rate,
  reported latency, underruns, relocations, timecode being sent, **timecode read back**, error in ms, CLX
  module state and packets sent (through the existing `module/diagnostics` call). It is a `DiagnosticsPageProps`
  document, so all three themes show it through the common diagnostics view with no theme work.
- **`LtcReader`** (in `Timecode.Ltc.cs`): a bi-phase mark decoder over the generator's own output (edge
  intervals -> bits -> sync word -> label). It runs only while leased by the diagnostics page or the inspector
  verb, on the aux thread, allocation-free, and publishes its last label through a mailbox. It is the always-on
  proof that what we send decodes to the timecode we meant; it is diagnostics code in the app, not a test.
- **Inspector verb** `timecode` (new `Screens/Diagnostics.Inspect.Timecode.cs`, registered beside `transport`):
  `timecode do=status` replies `{tap{uri, positionMs, rate, playing, epoch, flags}, ltc{state, rate, sent,
  read, errorMs, relocations, underruns}, clx{state, packets}}`; `timecode do=set key=<dial> value=<v>` goes
  through the same `SettingsVerbs` as the Settings rows.
- Log lines under category `timecode` (always on): open, close, device lost, each relocation with its cause.

### 3.8 Files

| Repo | File | Change |
|---|---|---|
| fluent-gpu | `src/FluentGpu.Engine/Media/Playback/Audio/AudioTimeline.cs` | **new**: `AudioTimelineAnchor`, the mailbox |
| fluent-gpu | `src/FluentGpu.Engine/Media/Playback/Audio/PcmAudioPlayer.cs` | publish in `PublishPosition` and on pause; `TryReadTimeline` |
| fluent-gpu | `src/FluentGpu.Engine/Media/Playback/Audio/AuxAudioOutput.cs` | **new**: `AuxAudioOutput`, `IAuxAudioGenerator`, `AuxBlock`, `AuxOutputStats` |
| fluent-gpu | `src/FluentGpu.Windows/Wasapi/WasapiPcm.cs` | `CreateAuxOutput` |
| WaveeMusic | `engine.lock` | bump to the engine commit that has the two pieces |
| WaveeMusic | `src/apps/Wavee/Playback/Playback.Timeline.cs` | **new** (G2a app half) |
| WaveeMusic | `src/apps/Wavee/Playback/Playback.Audio.cs` | `PublishIdentity` calls at the identity sites listed in 3.2; `Timecode.Host` start/stop from `EnsureBackend` / `Shutdown` |
| WaveeMusic | `src/apps/Wavee/Playback/Timecode.cs`, `Timecode.Ltc.cs`, `Timecode.Host.cs` | **new** |
| WaveeMusic | `src/apps/Wavee/Platform/Platform.cs` | the seven keys |
| WaveeMusic | `src/apps/Wavee/Screens/SettingsSpec.Sound.cs`, `SettingsFacts.cs`, `SettingsSpec.Aliases.cs` | the group, verbs, fact, aliases |
| WaveeMusic | `Themes/Classic/.../ClassicSettings.Body.cs`, `Themes/WinUi/.../WinUiSettings.Rows.cs`, `Themes/Zune/.../ZuneSettings.Body.cs` | group icon and wide-combo registrations only |
| WaveeMusic | `src/apps/Wavee/Screens/Diagnostics.Pages.cs`, `Diagnostics.Inspect.Timecode.cs` (new), `Diagnostics.Inspect.cs` | the section and the verb |
| WaveeMusic | `src/apps/Wavee.Sdk/Protocol/ModuleMethods.cs`, `Protocol/SdkJsonContext.cs`, `Playback.cs`, `WaveeModule.cs`, `ModuleRunner.cs`, `ModuleTestHost.cs` | the observer hook |
| WaveeMusic | `src/apps/Wavee/Platform/Modules.Host.cs` | `ModuleCapabilities.Timeline`, `AcquireLease`, `TimelineFanOut` |
| WaveeMusic | `src/apps/modules/Wavee.Module.Clx/**`, `Wavee.slnx`, `src/apps/Wavee/Wavee.csproj` (module build/copy list, `:150~`) | **new** module |
| WaveeMusic | `assets/loc/*.json`, `changes/unreleased/*.md`, `docs/guide/playback-modules.md`, `docs/guide/agent-inspect.md` | strings, change file, docs, the verb |

---

## 4. Phases (all in 0.4)

Each phase merges on its own and leaves nothing half-shown: no Settings row appears before the thing behind it works.

| # | Phase | Size | Engine work | Ships |
|---|---|---|---|---|
| 1 | **Timeline tap (G2a)**: engine anchor + mailbox, `Playback.Timeline`, identity publishes, the diagnostics section's tap rows, `timecode do=status` (tap part) | **M** | **yes** (small: `AudioTimeline.cs`, two publish sites) | a visible, checkable playhead tap. **Unblocks #48** |
| 2 | **Aux output (G2b)**: `AuxAudioOutput`, `CreateAuxOutput`, loss and retry | **M** | **yes** (all of it) | engine capability, no app surface |
| 3 | **LTC**: `Timecode*.cs`, generator, reader, host, the Settings group in three themes, strings, change file, diagnostics rows | **L** | no | the feature the issue's title asks for |
| 4 | **Observer hook (G2c)**: SDK additions, host fan-out, lease generalization, module guide | **M** | no | SDK capability; the Radio/YouTube/Twitch modules are untouched |
| 5 | **CLX module** + its Settings switch and diagnostics rows | **M** | no | CLX on the network |

Order: 1 -> (2 and 4 in parallel) -> 3 -> 5. If 0.4 runs short, 4 and 5 move out **together** (the hook has no
user without a module); 1 to 3 alone are a complete LTC feature.

### After 0.4 (not started in 0.4, nothing of it half-built)

- LTC on one channel of the main device (music mono + LTC) and on channels 3+ of a multichannel interface: needs
  a post-master insert or a multichannel mix format in `PcmAudioSession` (engine).
- Exclusive mode and `AUDCLNT_STREAMOPTIONS_RAW` for the aux stream (bypass Windows enhancements).
- A cue sheet: a start timecode per track or per playlist, edited from the track menu; continuous timecode
  across a queue.
- Art-Net timecode, MTC, OSC and TCNet as further observer modules; CLX unicast targets, more than one deck
  (the outgoing and incoming track as decks A and B during a #48 mix), beat position from #48's beat grid.
- A general per-module enable switch and a module-settings channel; third-party observer modules and the
  trust prompt that names the `timeline` capability.
- The aux output as a headphone cue for #48.
- **Beatmatching (#48, after 0.4) and the tap.** A per-voice rate would have to keep the anchor truthful: `Rate`
  and `SourceFrame` read from the active voice's own `WsolaAudioSource` instead of the session's `PlaybackRate`, a
  tempo glide re-anchoring without bumping the epoch on every tick (or LTC would relocate through the whole
  glide), and the module fan-out sending more often than 1 Hz while the rate moves (a reader extrapolating a
  linear glide of `r'` per second over `dt` is off by `r' * dt^2 / 2`: 20 ms after one second of an 8 %-in-2-s glide).
- A frame-exact master flip: apply the identity change at `QpcOfMixFrame(startMixFrame)` instead of on the pump
  tick that announces it (needs the incoming identity handed to the tap at schedule time).
- Sample-accurate timecode while a music video owns the deck (if the coarse anchor proves not good enough).
- macOS: the anchor's tick domain becomes the platform's monotonic clock; the SDK field is already named
  neutrally (`AnchorTicks`, `TicksPerSecond`).

---

## 5. Risks, unknowns, verification

### 5.1 Risks and unknowns

| Risk | Why it matters | What the plan does |
|---|---|---|
| **LTC bit layout written from memory** | a wrong bit and no reader locks | marked UNVERIFIED; confirm against SMPTE ST 12-1 before phase 3; the self-check reader cannot catch it (it would share the mistake), so a real reader must confirm once (below) |
| **Windows audio enhancements on the LTC endpoint** (loudness EQ, spatial sound, a limiter) reshape a square wave; the Windows volume of that device scales it | readers lose lock | shared mode only in 0.4; the caption says to turn enhancements off for that output and set its volume to 100 %; RAW/exclusive after 0.4. **UNVERIFIED** how much real readers tolerate |
| **Reported latency is wrong** on Bluetooth and some USB devices | constant offset between sound and timecode | the offset dial; both reported latencies on the diagnostics page |
| **Two device clocks drift** | slow slide if unhandled | the generator chases the tap every block (0.2 % slew, relocate past half a frame) |
| **Varispeed LTC** at episode speeds | readers accept only some speed deviation | scale the cell clock within 0.8x to 1.25x, real-time frames with relocations outside; music is 1.0 today (`Playback.Speed.cs:18`) |
| **User points LTC at their speakers** | a loud 1 to 2.4 kHz buzz | the device list excludes the music's output, has no default, and the switch stays disabled until a device is chosen |
| **CLX details not in the README**: the `0x09` resync packet, the meaning of `Active`, the unit of `Pitch` | a receiver may show wrong mixer state | marked UNVERIFIED; read the spec repo's `cpp/` reference before phase 5; ask s0 (he offered, and maintains a CLX implementation) |
| **Timecode Toolbox may not read CLX** (its README and the docs page disagree), and does not read LTC | our planned outside check may not exist | ask the requesters which build reads CLX; LTC needs a different reader (Resolume Arena is named in the issue as a consumer; **UNVERIFIED** that it reads LTC from an audio input) |
| **Multicast from a child process** may be dropped by a firewall or picked up on the wrong network adapter | CLX silently reaches nobody | TTL 1, log the bound interface, packets-sent counter on the diagnostics page. **UNVERIFIED** behaviour under Windows Firewall |
| **`SourceFrame` when the active voice has no rate source** | `ContentFrameAt` falls back to mixer-clock arithmetic when `_activeRateSource` is null (`PcmAudioPlayer.cs:845~`); an in-point start would then need the voice's start frame added | **Not traced:** whether every voice, a crossfade or mix voice included, is wrapped in a `WsolaAudioSource` at rate 1.0 (`:1093` builds one on the primary path, `:1186` on the added-voice path; the conditions around both were not read). Phase 1 reads it and shows `SourceFrame` on the diagnostics page |
| **`MixFrame` is a new derivation** (mixer `ConsumeSeq` minus the device queue) | the audible time of a transition's edges depends on it; `AudioClockPosition.Project`'s domain is re-based by a seek and was not shown to equal the mixer domain | **Not traced** end to end; phase 1 checks `QpcOfMixFrame` against a crossfade's known start frame on the diagnostics page. A pause inside the window moves its end: readers re-derive it from each anchor |
| **The master flip is tick-timed** (#48's announce, and today's crossfade and gapless identity swaps, run on the 200 ms pump tick) | timecode names the wrong track, or runs past the mix-out, for up to about a tick around a join | accepted in 0.4 and stated in section 3.4; the frame-exact flip is after 0.4. **Not traced:** #48's pump is unbuilt, so that its identity swap, `SetActiveVoice` and `SetTransition` land on the same tick is its section 3.6's statement, not code |
| **Duration is the track's effective end in a mixed playlist** (its audible end or its mix-out, by #48 D14) and can change mid-track | a consumer that treats duration as the file's length (CLX `Length`, `NormalizedPosition`) sees a shorter, moving number | reported as the reducer holds it, the same number the player bar and Connect show; said in the module guide's `TimelineState` text. **UNVERIFIED** whether CLX receivers mind a `Length` that changes |
| **Pause publishes late** if the engine control tick does not run while paused | LTC keeps running for up to a tick after pause | confirm in phase 1; publish the anchor in the pause path if needed |
| **Music video on the deck** uses another audio path | timecode accuracy unknown there | `Flags.Coarse`, shown on the diagnostics page; measure in phase 3 |
| **Licensing** | LGPL code in an AOT single binary | no `libltc` code; the CLX spec is Apache-2.0 (per the repo page; the LICENSE text itself was not read) and we implement the wire format, not their code |
| **Spotify terms** | timecode is derived from the playhead only: no audio leaves the app, nothing is re-streamed | no new exposure beyond what playing the track already is. The CLX Meta packet puts track titles on the local network: say so in the switch's caption |
| **Module trust** | an observer module learns what the user plays | bundled first-party only in 0.4; capability is declared so a later prompt can name it |

### 5.2 Verification, per phase (no tests: Debug build + headless inspector + screenshots)

Common to every phase: `dotnet build src/apps/Wavee/Wavee.csproj -c Debug` clean (warnings are errors); phases
1 and 2 also `dotnet build src/FluentGpu.slnx` clean in fluent-gpu.

1. **Tap.** Headless: `mount ... route=home`, start the fake deck (`transport` verbs), `frames 120`,
   `timecode do=status` twice: `positionMs` advances by the frames run, `epoch` stays. Then `transport do=seek
   ms=60000` -> `positionMs` near 60000 and `epoch` changed; pause -> `playing:false` and position fixed over
   further frames; `transport do=next` -> `uri` changed, position near 0. With crossfade on, `flags` shows
   `Transition` during the fade. **To confirm first:** that the fake deck under `--inspect` runs a
   `PcmAudioSession` over the paced silent endpoint (`Playback.Audio.cs:919` suggests it; not traced end to end).
   Diagnostics > Playback rendered in Classic, WinUI and Zune; `lint min=error` clean.
2. **Aux output.** No app surface yet. The engine gallery or a `Diagnostics.Probe` arm opens an aux output on
   `BufferedAudioEndpoint` with a counting generator and logs `Stats`; on a real machine the same probe on a
   real endpoint logs open, block count, zero underruns over a minute, and survives unplugging the device
   (Retrying, then reopen). This is a log-and-look check, not a harness.
3. **LTC.** Headless: enable through `timecode do=set`, play, and `timecode do=status` must show
   `ltc.read == ltc.sent` (the reader decoding the generator's own output) and `errorMs` within one frame of the
   tap, for each of the four rates, including a run across a minute boundary at 29.97 (labels :00 and :01 absent)
   and after a seek, a pause/resume and a track change. Settings > Sound rendered in the three themes, light and
   dark, at each theme's board sizes, with the group off, on without a device, and on with a device;
   `lint min=error` clean; a `trace` on the parent switch shows no part moving at once (no jump); a `crossing`
   at each tier edge of the Settings page. **Once, on real hardware (owner or requesters, not automatable):**
   the LTC output through a virtual cable or a loopback into a real LTC reader shows the same timecode as the
   Settings status row and follows seek and pause.
4. **Observer hook.** Run a module by hand as the module guide's section 6 describes, with a throwaway module
   that logs `OnTimelineAsync`: the app log (`module.<id>`) shows one line per change and one per second while
   playing, none while nothing plays; the module is not idle-stopped after ten minutes of playback; turning the
   switch off stops the process.
5. **CLX.** `timecode do=status` shows `clx.packets` rising at about 60 per second while playing; a packet
   capture (or the spec repo's reference reader) shows type bytes `0x01`/`0x02`/`0x00` with decodable
   MessagePack maps; then the requesters check it in Timecode Toolbox or a Clockworks viewer (outside check,
   see the risk above).

Steady-state allocation: with LTC on and a track playing, the frame bench's hot-phase allocation figure
(`FrameStats.HotPhaseAllocBytes`, `docs/guide/frame-bench.md`) and the audio tripwire stay at their values with
LTC off; the aux thread and the mailbox reads allocate nothing after start.
