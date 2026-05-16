---
guide: playback
scope: Local playback runtime — playback orchestrator, queue + context, track resolution, AudioHost IPC, decryption, DSP/equalizer, prefetch, local-file playback, video playback, and the UI playback service.
last_verified: 2026-05-16
verified_by: read+grep over src/Wavee/Audio, src/Wavee/AudioIpc, src/Wavee/Core/Audio, src/Wavee.AudioHost, src/Wavee.Playback.Contracts, src/Wavee.UI.WinUI/Data/Contexts (playback bits), src/Wavee.Local
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee Playback Runtime Inventory

This guide is for agents changing anything in the playback runtime —
context loading, queue, prefetch, seek, crossfade, equalizer, IPC contract,
decode / decrypt pipeline, audio key resolution, local-file playback, video
playback, SMTC integration, or the UI-side playback service. Use it to
answer "where is prefetch decided?", "how do I add a new IPC command?",
"who routes a queue mutation to the audio engine?" without grepping across
two projects.

The playback runtime spans two processes (see `CLAUDE.md` "Process model"):

- **UI process** — orchestrator, context + track resolution, audio-key
  manager, IPC client, the UI playback service that views bind to.
- **AudioHost** — out-of-process audio engine: streaming download, decrypt,
  decode, DSP (volume / normalization / EQ / crossfade / compressor /
  limiter), device output. *No project references on `Wavee.*`* —
  `IpcMessages.cs` is source-included.

Out of scope here:
- **Cluster / remote-device commands, dealer wiring, transfer, volume
  pushed by Spotify** — see `.agents/guides/connect-state.md`. The
  `IPlaybackStateService` interface is shared; that guide covers its
  remote-device fields, this one covers its local-state fields.
- **How individual track / episode rows render** — see
  `.agents/guides/track-and-episode-ui.md` for the now-playing visuals on
  rows (`TrackStateBehavior` etc.).
- **Library save state / heart button** — see
  `.agents/guides/library-and-sync.md`.
- **Wire-level protocol references** — `src/Wavee/Connect/DEALER_PROTOCOL.md`
  and `src/Wavee/Connect/DEALER_IMPLEMENTATION_GUIDE.md` already exist for
  dealer; the AP / Mercury / SpClient / Pathfinder docs live alongside the
  core library.

## How To Use This Guide

1. Skim the **Quick-find table** to locate the file you need.
2. Read the **Core contracts** section if you're adding or changing IPC
   messages, queue semantics, decoders, or sinks — most extension points
   already exist.
3. The **IPC contract** section is the authoritative list of message
   discriminators; edit `IpcMessages.cs` only.
4. The **Pipeline diagram** explains where in the audio loop a change
   lands (orchestrator vs IPC vs engine vs DSP).
5. If you add / remove a command, message type, processor, decoder, sink,
   or extension point, update this file and bump `last_verified` in the
   frontmatter.

Useful re-verification commands:

```
rg -n "class PlaybackOrchestrator|class TrackResolver|class ContextResolver|class PlaybackQueue|class AudioPipelineProxy|class AudioProcessManager" src/Wavee
rg -n "class AudioHostService|class AudioEngine|class LazyProgressiveDownloader|class AudioDecryptStream|class AudioProcessingChain|class EqualizerProcessor|class CrossfadeProcessor|class NormalizationProcessor|class CompressorProcessor|class LimiterProcessor|class VolumeProcessor" src/Wavee.AudioHost
rg -n "interface IAudioDecoder|interface IAudioProcessor|interface IAudioSink|interface IDeviceSelectableSink|interface ITrackStream" src/Wavee.AudioHost
rg -n "class IpcMessage|public sealed record .*Command\b|public sealed record .*Event\b" src/Wavee.Playback.Contracts
rg -n "class AudioKeyManager|class AudioHostPlayPlayKeyDeriver" src/Wavee/Core/Audio
rg -n "interface ILocalMediaPlayer|class LocalUri|class LocalFilePathStream" src/Wavee src/Wavee.Local
rg -n "class PlaybackService|class PlaybackStateService|class PlayerBarViewModel|class LocalPlaybackProgressTracker|class MediaTransportControlsService" src/Wavee.UI.WinUI
```

## Quick-find Table

| Surface | Host file:line | DTO / Contract | Notes |
| --- | --- | --- | --- |
| Playback orchestrator | `src/Wavee/Audio/PlaybackOrchestrator.cs` | queue + context state + transitions | Single owner of queue/context/prefetch/auto-advance in the UI process. Companion `PlaybackOrchestrator.Metrics.cs` carries timing instrumentation. |
| Context resolver | `src/Wavee/Audio/ContextResolver.cs` | context URIs → flat track lists | Pagination + retry; unavailable-context cooldown so dead contexts don't hammer Pathfinder. |
| Track resolver | `src/Wavee/Audio/TrackResolver.cs` | `ResolvedTrack` | CDN URL + audio key + codec + normalization + head-data; head data cached aggressively (10-year TTL). |
| Resolved-track shape | `src/Wavee/Audio/ResolvedTrack.cs` | output of TrackResolver | Carried over IPC inside `PlayResolvedTrackCommand`. |
| Track-resolution status | `src/Wavee/Audio/TrackResolution.cs` | resolve-time failure reasons | Drives the "track unavailable" UI states. |
| Playback queue | `src/Wavee/Audio/Queue/PlaybackQueue.cs` | shuffle / user-queue / post-context | Thread-safe; emits `NeedsMoreTracks` so the orchestrator pages the context. Item type is `QueueTrack` (`Queue/QueueTrack.cs`). |
| Queue item interface | `src/Wavee/Audio/Queue/IQueueItem.cs` | `IQueueItem` | Adapter shape for orchestrator-internal items. |
| Context filter / sort | `src/Wavee/Audio/ContextFilter.cs`, `ContextSorter.cs`, `ContextSortOrder.cs` | client-side filter / sort over context tracks | Used for "sort by" pickers on playlist / album pages. |
| Normalization data | `src/Wavee/Audio/NormalizationData.cs` (+ `src/Wavee.AudioHost/Audio/NormalizationData.cs`) | per-track loudness | UI side passes to engine; engine consumes in `NormalizationProcessor`. |
| AudioHost IPC contract | `src/Wavee.Playback.Contracts/IpcMessages.cs` | every command + event message, kebab-case `type` discriminators | Length-prefixed JSON over named pipe; both sides see the same file (source-included into AudioHost). |
| UI-process IPC client | `src/Wavee/AudioIpc/AudioPipelineProxy.cs` | implements UI side of `IPlaybackEngine` | RTT tracking, state freshness checks, reconnect, command/event correlation. |
| AudioHost process supervisor | `src/Wavee/AudioIpc/AudioProcessManager.cs` | child-process lifecycle | Spawns AudioHost.exe, watches for crash, restarts. |
| AudioHost entry point | `src/Wavee.AudioHost/AudioHostService.cs` | command dispatcher | Pipe listener, config validation, device watcher, PlayPlay emulator lifecycle. |
| Audio engine | `src/Wavee.AudioHost/Audio/AudioEngine.cs` | per-track playback loop | Owns the running track, decoder, processing chain, and sink. Position publish cadence ~5 s (UI interpolates at 1 Hz). |
| Audio settings | `src/Wavee.AudioHost/Audio/AudioSettings.cs` | engine-level config (sample rate, buffer sizing, crossfade, normalization) | Plumbed in from `set_audio_settings` IPC. |
| Audio key manager | `src/Wavee/Core/Audio/AudioKeyManager.cs` | `Task<AudioKey>` | AP-protocol track keys; 2.5 s timeout × 5 retries; disk cache; PlayPlay fallback when the deriver is registered. |
| PlayPlay key deriver (UI) | `src/Wavee/Core/Audio/AudioHostPlayPlayKeyDeriver.cs` | obfuscated key + pack JSON → AES key | Routes the request to AudioHost via `derive_playplay_key` IPC. |
| PlayPlay key emulator (AudioHost) | `src/Wavee.AudioHost/PlayPlay/PlayPlayKeyEmulator.cs` (proprietary; `PlayPlayKeyEmulator.Stub.cs` ships in public source) | LoadLibrary + `vm_runtime_init` + `vm_object_transform` | Runtime asset lives at `%LOCALAPPDATA%\Wavee\PlayPlay\packs\<id>\Spotify.dll`. See `CLAUDE.md` "Audio runtime support pack provisioning". |
| Progressive downloader | `src/Wavee.AudioHost/Audio/Streaming/LazyProgressiveDownloader.cs` | head-data + lazy CDN | Instant-start: serves head file immediately, defers CDN range fetches in the background. Opens the local cache file directly if `LocalCacheFileId` is set, gated on `audioKey is { Length: 16 }`. On local-cache hits performs a 4-byte OggS magic check after decryption and auto-deletes + throws on mismatch (see "Persistent audio cache" below). |
| Eager progressive downloader | `src/Wavee.AudioHost/Audio/Streaming/ProgressiveDownloader.cs` | classic range-fetch loop + persistent-cache writer | Used when head-data fast path doesn't apply. Owns the `PersistToCacheAsync` / `CopyTempFileForPersistentCacheAsync` path that produces `.enc` files in `%LOCALAPPDATA%\Wavee\AudioCache\audio\`. Snapshots head bytes under `lock (_tempFile)` and re-encrypts in memory before writing — protects against the position race with concurrent BASS reads. |
| Buffered HTTP stream | `src/Wavee.AudioHost/Audio/Streaming/BufferedHttpStream.cs` | HTTP byte stream with range support | Reused by both downloaders. |
| Decrypt stream | `src/Wavee.AudioHost/Audio/Streaming/AudioDecryptStream.cs` | AES-128-CTR wrapper over the encrypted Ogg bytes; null key = pass-through | Encrypts from byte 0 — see memory `reference_spotify_audio_offset_zero`. Constructor logs `keyFp=<SHA256-prefix>` (or `pass-through`) at DEBUG when an `ILogger` is supplied. NOTE: a separate Core-side `src/Wavee/Core/Crypto/AudioDecryptStream.cs` is proprietary and may be absent in public clones; that one is unused by AudioHost. |
| Skip stream helper | `src/Wavee.AudioHost/Audio/Decoders/SkipStream.cs` | seekable forward-skip wrapper | Lets decoders skip past container headers. |
| File-id type | `src/Wavee.AudioHost/Audio/Streaming/FileId.cs` | base16 file ID parsing | One file-id per encoded variant. |
| URL-aware stream | `src/Wavee.AudioHost/Audio/Streaming/UrlAwareStream.cs` | streams that need URL refresh on expiry | CDN URLs expire ~1 hour. |
| Audio processing chain | `src/Wavee.AudioHost/Audio/Processors/AudioProcessingChain.cs` | per-buffer DSP pipeline | Standard order: Normalization → Volume → EQ → Crossfade → Compressor → Limiter. |
| Volume processor | `src/Wavee.AudioHost/Audio/Processors/VolumeProcessor.cs` | linear gain | Driven by `set_volume` IPC. |
| Normalization processor | `src/Wavee.AudioHost/Audio/Processors/NormalizationProcessor.cs` | per-track loudness compensation | Uses `NormalizationData` from the resolved-track payload. |
| Equalizer processor | `src/Wavee.AudioHost/Audio/Processors/EqualizerProcessor.cs` | 10-band parametric, biquad IIR | Live gain updates via `set_equalizer` IPC; enable / disable toggle. |
| Crossfade processor | `src/Wavee.AudioHost/Audio/Processors/CrossfadeProcessor.cs` | gapless / overlapping transitions | Seam between the running track and the prefetched next track. |
| Compressor / Limiter | `Processors/CompressorProcessor.cs`, `Processors/LimiterProcessor.cs` | optional dynamics | Tail end of the chain — protects against clip on +gain EQ. |
| Decoder registry | `src/Wavee.AudioHost/Audio/Decoders/AudioDecoderRegistry.cs` | format → `IAudioDecoder` map | Where new decoders register themselves. |
| BASS decoder | `src/Wavee.AudioHost/Audio/Decoders/BassDecoder.cs` | ManagedBass-based | MP3 / FLAC / WAV / etc. for local files. |
| BASS plugin loader | `src/Wavee.AudioHost/Audio/Decoders/BassPluginLoader.cs` | loads BASS plugins | Bass.dll + bassflac.dll etc. on startup. |
| Vorbis decoder | `src/Wavee.AudioHost/Audio/Decoders/VorbisDecoder.cs` | NVorbis-backed | Used for Spotify Ogg post-decryption. |
| Stub decoder | `src/Wavee.AudioHost/Audio/Decoders/StubDecoder.cs` | no-op | Test / fallback. |
| Audio sink factory | `src/Wavee.AudioHost/Audio/Sinks/AudioSinkFactory.cs` | builds the right sink for the platform | PortAudio in prod, stub in tests. |
| PortAudio sink | `src/Wavee.AudioHost/Audio/Sinks/PortAudioSink.cs` | `IDeviceSelectableSink` | Live device switch via `set_output_device` IPC. |
| Stub sink | `src/Wavee.AudioHost/Audio/Sinks/StubAudioSink.cs` | no-op | Test / preview-mode. |
| Windows device watcher | `src/Wavee.AudioHost/Audio/WindowsAudioDeviceWatcher.cs` | listens for OS audio-device changes | Push events back over IPC so the device picker UI refreshes. |
| Audio buffer / format | `src/Wavee.AudioHost/Audio/Abstractions/AudioBuffer.cs`, `AudioFormat.cs` | format / sample-block primitives | Common shape across decoders / processors / sinks. |
| Audio engine abstractions | `src/Wavee.AudioHost/Audio/Abstractions/IAudioDecoder.cs`, `IAudioProcessor.cs`, `IAudioSink.cs`, `IDeviceSelectableSink.cs`, `ITrackStream.cs`, `TrackMetadata.cs` | extension points | The plug-in interfaces for new decoders / processors / sinks. |
| Local media player interface | `src/Wavee/Audio/ILocalMediaPlayer.cs` | local file playback contract | Implemented in WinUI; routes `wavee:local:*` URIs around AudioKey + decryption. |
| Local URI scheme | `src/Wavee.Local/LocalUri.cs` | `wavee:local:track:<sha1>` / album / artist | `LocalUri.IsLocal(uri)` checks; orchestrator branches on this. |
| Local file stream | `src/Wavee.Local/Playback/LocalFilePathStream.cs` | path-passthrough marker | Signals BASS to use direct file access (no in-memory buffer). |
| Video playback contract | `src/Wavee/Audio/ISpotifyVideoPlayback.cs` | PlayReady-protected manifest playback | Implemented in WinUI; uses `MediaPlayerElement` for frame rendering. |
| Video playback target | `src/Wavee/Audio/SpotifyVideoPlaybackTarget.cs` | identifies the surface that should render video | Picked from playback metadata (`track_player=video`). |
| UI playback service (commands) | `src/Wavee.UI.WinUI/Data/Contexts/PlaybackService.cs` | command dispatcher | Routes commands to Connect (remote-device path) or local (`AudioPipelineProxy`); retry, buffering state, error toasts. |
| UI playback state (bridge) | `src/Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs` | `INotifyPropertyChanged` over the unified state | Cluster fields are documented in `connect-state.md`; this guide covers local fields (position, buffering, audio device, error, restrictions). |
| Local progress tracker | `src/Wavee.UI.WinUI/Services/LocalPlaybackProgressTracker.cs` | persists position every ~5 s | Marks watched at ≥90 % completion; writes a row to `local_plays`. |
| OS media transport (SMTC) | `src/Wavee.UI.WinUI/Services/MediaTransportControlsService.cs` *(or wherever SMTC integration lives — grep `SystemMediaTransportControls`)* | OS media keys + "Now Playing" tile | Maps OS Play / Pause / Next / Prev to `IPlaybackStateService`. |
| PlayerBar view model | `src/Wavee.UI.WinUI/ViewModels/PlayerBarViewModel.cs` | display state + 1 Hz interpolation timer | Closes the gap between 5 s position ticks; owns video/audio toggle. |
| First-time playback dialog | `src/Wavee.UI.WinUI/Controls/Playback/` + the first-play behaviour in `TrackBehavior` (see track-and-episode-ui guide) | onboarding flow | Cross-link only — the dialog lives near the row controls. |

## Core contracts

### Playback engine

`IPlaybackEngine` (UI → AudioHost) is implemented by
`AudioPipelineProxy` (`src/Wavee/AudioIpc/AudioPipelineProxy.cs`). Every
playback command and state observation crosses the pipe through this
class. Public surface (typical names — re-confirm in code):

- `PlayResolvedAsync(PlayResolvedTrackCommand)` — start a resolved track.
- `PrepareNextAsync(PlayResolvedTrackCommand)` — pre-stage the next
  track for gapless / fast-skip.
- `PauseAsync` / `ResumeAsync` / `SeekAsync` / `StopAsync`.
- `SetVolumeAsync` / `SetEqualizerAsync` / `SetNormalizationAsync` /
  `SetCrossfadeAsync`.
- `SetOutputDeviceAsync` (`IDeviceSelectableSink`).
- `DerivePlayPlayKeyAsync` (round-trip RPC).
- `IObservable<EngineStateUpdate> StateUpdates` — pushes from AudioHost
  arrive here; orchestrator subscribes.
- `IObservable<EngineError> Errors`, `IObservable<TrackFinished> Finished`.

The orchestrator (`src/Wavee/Audio/PlaybackOrchestrator.cs`) is the
single subscriber and never bypasses `IPlaybackEngine`.

### IPC discriminators (`Wavee.Playback.Contracts/IpcMessages.cs`)

Every message is a record with a kebab-case `type` discriminator. Both
sides parse via the same source file. Catalogue (re-grep
`public sealed record .*Command|.*Event` to keep this current):

**Commands (UI → AudioHost):**
- `play_resolved` — start with a fully-resolved track payload.
- `play_track` — start with a deferred URI; AudioHost asks for resolution
  back via RPC.
- `prepare_next` — pre-stage gapless next-track.
- `seek` — to absolute ms.
- `pause`, `resume`, `stop`.
- `set_volume` (0.0–1.0), `set_equalizer` (enabled + band gains),
  `set_normalization`, `set_crossfade`, `set_audio_settings`,
  `set_output_device` (device id), `switch_quality` (preferred bitrate).
- `add_to_queue` — engine-side queue ops for instant transitions
  (orchestrator owns the canonical queue).
- `derive_playplay_key` — RPC: UI sends obfuscated bytes + pack JSON,
  AudioHost returns the AES key.
- `shutdown` — clean exit.

**Events (AudioHost → UI):**
- `state_update` — periodic snapshot (position, playing/paused,
  buffering, duration, current track ID). Cadence ≈ 5 s; UI interpolates
  at 1 Hz between ticks.
- `track_finished` — emitted when the current track reaches end-of-stream
  or is preempted by a skip-next. Carries the finished URI + duration so
  the orchestrator can dispatch auto-advance.
- `error` — typed error code + message; routed to a notification.
- `command_result` — RPC reply correlator.
- `audio_devices_changed` — fired by `WindowsAudioDeviceWatcher`; lets
  the device-picker UI refresh without polling.

**Wire format:** 4-byte big-endian length prefix + UTF-8 JSON payload.
See `CLAUDE.md` "IPC contract" for the rationale and the
source-include-vs-project-ref dichotomy.

### AudioHost extension points

All in `src/Wavee.AudioHost/Audio/Abstractions/`:

- `IAudioDecoder` — input format → `AudioBuffer` stream. Register via
  `AudioDecoderRegistry`.
- `IAudioProcessor` — transforms an `AudioBuffer` in place / chained.
  Order is controlled by `AudioProcessingChain`.
- `IAudioSink` — writes `AudioBuffer` to a device. Singleton per engine.
- `IDeviceSelectableSink` — sink subset that supports `SetOutputDevice`
  at runtime.
- `ITrackStream` — input to decoders; supports `Read` / `Seek` /
  `Length` semantics.
- `TrackMetadata` — adjunct info (duration, normalization, codec).

### UI-side service interfaces

- `IPlaybackEngine` — only `AudioPipelineProxy` should implement / consume
  this directly. Higher layers route through `PlaybackService`.
- `IPlaybackStateService` (`src/Wavee.UI/Contracts/`) — view models bind
  to this. Local-fields side covered here; remote-cluster side in
  `connect-state.md`.
- `ILocalMediaPlayer` (`src/Wavee/Audio/ILocalMediaPlayer.cs`) — WinUI
  implementation handles direct local-file playback when the runtime
  uses BASS without going through Spotify decode + decrypt.

## IPC contract details

`Wavee.Playback.Contracts/IpcMessages.cs` is consumed two ways:

1. **Project reference** by `Wavee` (and transitively by
   `Wavee.UI.WinUI`). Carries the binary `Wavee.Playback.Contracts.dll`.
2. **Source-include** by `Wavee.AudioHost` via
   `<Compile Include="..\Wavee.Playback.Contracts\IpcMessages.cs">`.
   AudioHost has **zero** project references to `Wavee.*` so a stale DLL
   can never land alongside `Wavee.AudioHost.exe`.

JSON is the wire format because type identity across assemblies doesn't
matter — both sides serialize / deserialize against the same source code,
and JSON parses against record shapes regardless of which assembly the
record lives in.

When adding a new command or event:
- Edit `IpcMessages.cs` only.
- Both sides recompile and the discriminator is picked up automatically.
- No DI registration needed at the contract layer; the consumer (e.g.
  `AudioPipelineProxy` for commands, `AudioHostService` for handlers)
  adds the case in its switch / dispatcher.

## Pipeline diagram

```
            Spotify URI                wavee:local:track:<sha1>
                │                              │
                ▼                              ▼
       TrackResolver (UI)          (skip resolve; pass LocalFilePathStream)
                │                              │
                └─────────► PlayResolvedTrackCommand ◄─┘
                              (CDN URL + key + codec
                               + LocalCacheFileId | LocalFilePath
                               + NormalizationData)
                                       │
                            AudioPipelineProxy (UI side)
                                       │
                              pipe (length-prefixed JSON)
                                       │
                            AudioHostService.OnCommand
                                       │
                                       ▼
                            AudioEngine.PlayAsync
                                       │
                ┌──────────────────────┼──────────────────────┐
                ▼                      ▼                      ▼
   LazyProgressiveDownloader   AudioKeyManager (UI,           (local file
   (head + lazy CDN)            via derive_playplay_key RPC    passthrough)
                │                if PlayPlay path active)
                │                      │
                └──── encrypted bytes ─┘
                          │
                          ▼
                 AudioDecryptStream  (AES-128-CTR, byte 0)
                          │
                          ▼
                 Decoder  (VorbisDecoder for Spotify Ogg | BassDecoder for local)
                          │
                          ▼
                 AudioProcessingChain
                  Normalization → Volume → EQ → Crossfade
                   → Compressor → Limiter
                          │
                          ▼
                 IAudioSink → PortAudio → device

   Out: state_update (~5 s) / track_finished / error / audio_devices_changed
        → AudioPipelineProxy.StateUpdates
        → PlaybackOrchestrator
        → IPlaybackStateService (INPC bridge)
        → PlayerBarViewModel.InterpolationTimer (1 Hz)
```

## Per-area notes

### Queue + context

The orchestrator owns three concurrent track lists:
- **Context** — flat track list expanded from the current context URI
  (playlist / album / artist discography / show / station). Paged via
  `ContextResolver`.
- **User queue** — explicit "Play next" inserts; consumed before
  resuming context.
- **Post-context queue** — "Add to queue" pushes; consumed after the
  context exhausts (autoplay can extend this).

Repeat modes (track vs context) and shuffle live in `PlaybackQueue`. The
queue emits a `NeedsMoreTracks` signal when its internal buffer falls
below threshold so the orchestrator pages the context.

Autoplay rollover (artist radio, "Made for you") plugs in at the
context-exhaustion boundary; the orchestrator requests a follow-on
context from Pathfinder.

### Prefetch

The orchestrator decides when to issue `prepare_next` based on the
running track's position and remaining duration. The exact thresholds
live near the top of `PlaybackOrchestrator.cs` (re-grep for
`Prefetch` constants when tuning). Dedup is per-next-URI so a
queue-reorder that re-confirms the same next track doesn't double-fire.

Prefetch contends with AudioKey HTTP for the same socket pool, so the
order matters: AudioKey first (small, latency-sensitive), then CDN
range-prefetch.

### Audio key resolution

`AudioKeyManager` (`src/Wavee/Core/Audio/AudioKeyManager.cs`) is the
canonical path:

1. AP request → wait up to 2.5 s.
2. Retry up to 5 times with exponential backoff on
   transient failures.
3. On hard failure (or when the `AudioHostPlayPlayKeyDeriver` is
   registered for the track's encryption family), fall back to the
   PlayPlay path:
   - UI sends the obfuscated key bytes + the runtime-pack JSON over IPC
     (`derive_playplay_key` command).
   - AudioHost calls the emulator (which `LoadLibrary`'s the runtime
     pack DLL) and returns the AES key.

The PlayPlay deriver is registered in
`AppLifecycleHelper.InitializeOutOfProcessAudioAsync` only if
`AudioRuntimeProvisioner` returned a usable `RuntimeAsset` (see
`CLAUDE.md` "Audio runtime support pack provisioning"). When the asset
isn't available, the deriver isn't registered and the manager falls back
to AP-only — which means most premium-encrypted tracks won't decrypt.
Public-source contributors hit this path; the stub builds + runs but
won't play encrypted Ogg.

Keys are cached on disk (per-track-ID). The cache survives restarts.

### Decoders

`VorbisDecoder` (NVorbis-vendored) is the canonical decoder for Spotify
Ogg streams after `AudioDecryptStream` has removed the AES layer.
`BassDecoder` (`ManagedBass` + `BassPluginLoader` for FLAC plugin) is
used for local files and any non-Ogg Spotify-side streams. Selection
happens in `AudioDecoderRegistry` based on the format advertised in the
`PlayResolvedTrackCommand`.

When adding a new format: implement `IAudioDecoder`, register it in
`AudioDecoderRegistry`, add the format discriminator to the resolved-
track shape so the right decoder picks up.

### DSP / processors

Order in `AudioProcessingChain`:

1. **Normalization** — per-track loudness compensation; ReplayGain-like
   based on `NormalizationData` from the resolved track.
2. **Volume** — user-driven linear gain.
3. **Equalizer** — 10-band parametric (biquad IIR). Off by default;
   the Settings page flips it on. Per-band gain limits are clamped to
   protect downstream stages from clipping.
4. **Crossfade** — gapless / overlapping transitions. Seam is driven by
   `prepare_next` arrival + remaining duration on the running track.
5. **Compressor** — optional dynamics; tames spikes when EQ adds gain.
6. **Limiter** — hard ceiling at 0 dBFS to prevent clip.

Adding a new processor: implement `IAudioProcessor`, insert it in the
chain at the appropriate stage in `AudioProcessingChain`. Each
processor mutates `AudioBuffer` in place; downstream code only sees the
post-processed buffer.

### Crossfade

The crossfade processor uses the prefetched next-track buffer as its B
input. Seam timing is controlled by `set_crossfade` IPC (duration; 0
means gapless). When the current track is N seconds from the end,
`CrossfadeProcessor` starts fading A out and the prepared B in. The
two-track buffer pair is managed internally.

### Equalizer

The `set_equalizer` IPC payload carries `{ enabled, bands[] }` where
each band entry is `{ frequencyHz, gainDb, q }` (re-confirm in
`EqualizerProcessor.cs`). Live updates apply per-buffer; no recompute is
required for steady-state playback. The Settings page persists the user's
preset and emits `set_equalizer` whenever they tweak a slider.

A characteristic startup log: `[AudioEngine] Equalizer installed:
enabled=True, bands=10, version=3, observedAudio=True` — confirms the
processor saw a real audio buffer (i.e. the chain was actually
exercised, not just constructed).

### Seek

Fast seek (commit `536f168`): byte-position bisection over the encoded
stream + predictive prefetch of the seek-target range, with a decoder-
recreate fallback when the codec state can't be recovered. Position
updates from the engine are suppressed during the in-flight seek so the
UI doesn't snap back to the pre-seek position on the next 5 s tick.

### Position cadence + interpolation

- AudioHost publishes `state_update` every ~5 s during steady playback,
  and immediately on any state change (play / pause / seek / stop /
  track-finished).
- `PlayerBarViewModel` runs a 1 Hz interpolation timer between server
  ticks so the progress bar moves continuously.
- A seek-in-flight guard ensures a stale `state_update` arriving during
  a drag doesn't yank the bar backwards.

### Persistent audio cache

Fully-downloaded Spotify audio is written to
`%LOCALAPPDATA%\Wavee\AudioCache\audio\<spotifyFileId>.enc` so future plays
skip CDN resolution. The on-disk format is **uniformly AES-128-CTR
encrypted from byte 0** — i.e. byte-identical to what the CDN serves. The
reader at `LazyProgressiveDownloader.InitializeCdnResourcesAsync` opens
the file and wraps it in `AudioDecryptStream(audioKey, file,
decryptionStartOffset: 0)`. Anything else in the file produces garbage at
byte 0 and BASS rejects with `FileFormat`.

**Write protocol — `ProgressiveDownloader.PersistToCacheAsync`:**
- Only fires after `IsFullyDownloaded` becomes true (download complete).
  Guarded by `Interlocked.Exchange` so it runs at most once per
  downloader instance.
- The temp file (in `%TEMP%`) carries `[clear head bytes from headData]
  [encrypted CDN body]`. The persist path must re-encrypt the clear head
  region so the cache file is uniformly encrypted from byte 0.
- **Refuses to persist** when `_clearHeadBytes > 0 && _persistentCacheAudioKey
  == null` — without a key we cannot re-encrypt the head, and a verbatim
  copy would produce a cache the reader can't decrypt. Emits a
  `LogWarning` so this silent failure mode is visible in logs.
- `CopyTempFileForPersistentCacheAsync` snapshots the clear head under
  `lock (_tempFile)`, calls `AudioDecryptStream.ApplySpotifyCtr` to
  re-encrypt the snapshot in place, writes it to the destination, then
  streams the encrypted body chunk-by-chunk, taking the `_tempFile` lock
  around each `Position = …; Read(…)` pair to match the
  `WriteToTempFile` / `ReadFromTempFile` protocol. **Do not** revert to
  async `ReadAsync` / `CopyToAsync` on `_tempFile` without the lock —
  BASS reads are still arriving concurrently and would race the position.
- Every persist emits one INFO log: `Persist {FileId}: clearHead=…B,
  key={present|null}, branch={re-encrypt-head|verbatim},
  tempHead16=<hex>` — the first 16 bytes of the temp file at offset 0,
  useful for post-mortem if a cache later fails the read-side magic check.

**Read protocol — `LazyProgressiveDownloader.InitializeCdnResourcesAsync`:**
- Local-cache shortcut is gated on `audioKey is { Length: 16 }`; without
  a real key we go to CDN.
- After constructing `_decryptStream`, peeks the first 4 decrypted bytes
  and checks for the Ogg-Vorbis magic `O g g S` (0x4F 67 67 53). On
  mismatch: logs a `LogWarning`, disposes the streams, **deletes the
  cache file**, and throws `InvalidOperationException`. The next
  playback attempt re-resolves; with the cache file gone, the deferred
  resolution returns a real CDN URL and the file is re-downloaded
  correctly.
- Why throw instead of falling through to CDN within the same call:
  `DeferredResolutionRegistry.CompleteFromCache` sets `CdnUrl = ""` on
  cache-hit deferred results — there is no CDN URL available to use.
  Surfacing the error and letting the retry path re-resolve is the
  minimum-change solution; one failed click per corrupt track, then
  self-healed.

When changing the persist or read path:
- Keep the on-disk format `[enc head][enc body]` from byte 0. The reader
  uses `decryptionStartOffset: 0` and any deviation will be a silent
  garble.
- Don't introduce reads from `_tempFile` outside `lock (_tempFile)`. The
  background download path is the only writer; persist + BASS share the
  read side.
- Cache files written by older code (pre-2026-05-13 commit `ed8d50a`)
  may have a clear head and trigger the new self-heal. That's expected —
  the heal runs once per file and the rebuilt cache is correct.

### Local files

`LocalUri.IsLocal(uri)` is the discriminator. The orchestrator skips
`TrackResolver` entirely for local URIs and instead builds a
`PlayResolvedTrackCommand` with `LocalFilePath` set. `LocalFilePathStream`
is a marker that BASS recognizes as "use the path directly" rather than
buffering bytes in memory — important for the 100 MB+ FLAC / WAV files
the local library scans.

The local progress tracker
(`LocalPlaybackProgressTracker`) subscribes to local-file playback and
persists position every 5 s into a `local_plays` table; marks watched at
≥ 90 % so local content shows up in the user's recent-played-with-progress
surfaces.

### Video

Spotify videos / music videos:
- `ISpotifyVideoPlayback` (UI-implemented) holds the
  `MediaPlayerElement` surface; `SpotifyVideoPlaybackTarget` resolves
  which surface should render based on the playback context.
- Video frames render via WinUI / WinRT (`MediaPlayerElement`), not the
  AudioHost. Audio for the video may still go through AudioHost.
- `PlayerBarViewModel.HasVideo` and the video-vs-audio toggle drive the
  switch; `TrackResolver` flags video tracks based on the
  `track_player=video` / `media.manifest_id` metadata pair.
- PlayReady decryption is part of the manifest playback (not Spotify's
  Ogg PlayPlay).

### SMTC / OS media transport

`MediaTransportControlsService` (grep for `SystemMediaTransportControls`
to confirm the exact filename) registers an `SMTC` adapter at startup
and proxies OS Play / Pause / Next / Prev keystrokes to
`IPlaybackStateService`. Now-playing metadata (title, artist, artwork)
is pushed onto SMTC whenever `PlaybackStateService.CurrentTrack`
changes so Windows' "Now Playing" overlay stays in sync.

## Framework split

| Assembly | Owns |
| --- | --- |
| `Wavee` (core) | `PlaybackOrchestrator`, `TrackResolver`, `ContextResolver`, `PlaybackQueue`, `QueueTrack`, `ContextFilter` / `ContextSorter`, `AudioKeyManager`, `AudioHostPlayPlayKeyDeriver`, `AudioPipelineProxy`, `AudioProcessManager`, `ILocalMediaPlayer`, `ISpotifyVideoPlayback`. |
| `Wavee.Playback.Contracts` | `IpcMessages.cs` — the wire contract. Project-referenced by `Wavee`, source-included into `Wavee.AudioHost`. |
| `Wavee.AudioHost` | `AudioHostService`, `AudioEngine`, downloaders, decrypt stream, decoders, processors, sinks, EQ, audio settings, device watcher, PlayPlay emulator. **Zero `Wavee.*` project references.** |
| `Wavee.Local` | `LocalUri`, `LocalFilePathStream`, file scanning, TMDB enrichment. |
| `Wavee.UI` (framework-neutral) | `IPlaybackStateService` interface, playback DTOs / enums (`QueueItem`, `RepeatMode`, …). |
| `Wavee.UI.WinUI` | `PlaybackService` (command dispatcher), `PlaybackStateService` (INPC bridge), `PlayerBarViewModel`, `LocalPlaybackProgressTracker`, `MediaTransportControlsService`, first-time playback dialog, video-frame `MediaPlayerElement` host. |

When adding playback functionality:
- New IPC message → `Wavee.Playback.Contracts` only.
- New decoder / processor / sink → `Wavee.AudioHost`.
- New orchestrator behaviour (queue, prefetch, autoplay) → `Wavee`.
- New view binding / command surface → `Wavee.UI.WinUI`, going through
  `IPlaybackStateService`.

## Change Guidance

When **adding a new IPC command or event**:
- Edit `IpcMessages.cs` only. Both sides pick it up automatically.
- Add a case to `AudioPipelineProxy` (for outbound commands) or
  `AudioHostService.OnCommand` (for inbound commands) and emit / handle
  the event symmetrically.
- Don't introduce a parallel side-channel; the pipe is the single bus.

When **changing queue / shuffle / repeat behaviour**:
- Mutations live in `PlaybackQueue`. Orchestrator entry points are
  typically `OnShuffleAsync` / `OnRepeatAsync` / queue-mutation handlers.
- Don't mutate queue state from UI directly — go through
  `IPlaybackStateService.AddToQueueAsync` / `RemoveFromQueueAsync` /
  `MoveInQueueAsync` so the cluster + local stay in sync.

When **tuning prefetch**:
- Threshold constants live in `PlaybackOrchestrator.cs` (re-grep for
  `Prefetch` / `prepare_next`). Tune by position-pct *and* remaining-time
  so both short and long tracks behave.
- Account for HTTP contention with `AudioKeyManager` — running them in
  parallel can starve the key request and stall playback.

When **changing position cadence**:
- AudioHost publishes from `AudioEngine` (search
  `PositionPublishIntervalMs` or similar). UI interpolation timer
  cadence is in `PlayerBarViewModel`. Both should stay independent —
  the engine cadence trades off bandwidth, the UI cadence trades off
  smoothness.

When **fixing AudioKey / PlayPlay**:
- Stub vs proprietary: `AudioKeyManager` works without PlayPlay (AP-only
  path), but most Spotify Premium audio requires the deriver. Check
  `AppLifecycleHelper.InitializeOutOfProcessAudioAsync` to see whether
  the provisioner produced a `RuntimeAsset` — if not, the deriver isn't
  registered and the fallback path can't fire.
- Per `CLAUDE.md`: token-bytes, expected SHA, RVAs, AES extraction
  strategy all live in the server-side manifest, not source. Don't
  hardcode pack data.

When **changing DSP**:
- Insertion point: `AudioProcessingChain`. Each processor implements
  `IAudioProcessor` and mutates `AudioBuffer` in place. Order matters
  (Normalization first so volume / EQ work against a stable baseline;
  Limiter last to catch any post-EQ clip).
- Live update: processors expose `Update*` methods (e.g.
  `SetEqualizerBands`) that the engine calls on receipt of the matching
  IPC command. No engine restart needed.

When **adding a new decoder or sink**:
- Decoder: implement `IAudioDecoder`, register in
  `AudioDecoderRegistry`, add the format hint to `PlayResolvedTrackCommand`
  so the engine picks the right decoder.
- Sink: implement `IAudioSink` (+ `IDeviceSelectableSink` if it can
  switch devices at runtime), wire into `AudioSinkFactory`.

When **changing local-file routing**:
- The `LocalUri.IsLocal(uri)` check in the orchestrator routes around
  resolution.
- The `LocalFilePathStream` marker in the decoder layer routes around
  BASS's in-memory buffer.
- The progress tracker is a separate concern — it persists locally
  regardless of the playback path, but its hook lives in the WinUI side
  (`LocalPlaybackProgressTracker`), not the engine.

When **changing video playback**:
- The PlayReady manifest path is entirely separate from the audio engine.
- Routing decision happens in the orchestrator based on the metadata
  flags on the resolved track. Don't try to push video frames through
  AudioHost.
- The video-vs-audio toggle on `PlayerBarViewModel` switches the
  rendering target without restarting playback (uses the same
  underlying playback session).

## Keeping This Guide Current

If you add, remove, or rename anything in the playback runtime:
1. Update the relevant Quick-find row and the affected section.
2. Re-run the re-verification commands at the top — each should produce
   ≥1 hit. Zero-hit lines mean a class was renamed and the guide is
   stale.
3. Update `last_verified` in the frontmatter.
4. If a surface moves between projects (e.g. a processor moves from
   `Wavee.AudioHost` to a new audio library), update the **Framework
   split** table as well.
