---
guide: connect-state
scope: Every code path that reads or writes Spotify Connect / dealer state — dealer message bus, device announce, cluster state, remote commands (Play/Pause/Seek/Transfer/Volume/Queue), queue derived from cluster, and the device picker / now-playing / connect-debug UI surfaces.
last_verified: 2026-05-13
verified_by: read+grep over src/Wavee/Connect, src/Wavee/Core/Session/Session.cs, src/Wavee.UI/Contracts, src/Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs, src/Wavee.UI.WinUI/Controls/Playback
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee Spotify Connect Inventory

This guide is for agents changing anything that touches Spotify Connect:
the dealer WebSocket, cluster state, this-device announce (PutState), remote
commands (Play/Pause/Seek/Transfer/SetVolume/SetOptions/Queue), the
cluster-derived queue, and the device-picker / volume / now-playing /
connect-debug UI surfaces. Use it to answer "who emits cluster updates?",
"where do I add a new remote command?", "where does the device picker get
its rows from?" without re-grepping.

Out of scope here:
- **Library sync / saves / pins** — see `.agents/guides/library-and-sync.md`
  for `LibraryChangeManager` (dealer push handler), `SpotifyLibraryService`
  collection sync, save/follow/pin write paths, etc. The dealer pipeline is
  shared; this guide focuses on the *playback* side.
- **How individual track / episode rows render** — see
  `.agents/guides/track-and-episode-ui.md`. Cluster-driven now-playing
  visuals (`IsPlaying` beam, equalizer, buffering ring) are driven by
  `TrackStateBehavior` — discussed there, not here.
- **Audio rendering** — the out-of-process AudioHost (Ogg decode, mixer,
  hardware output) doesn't speak the dealer protocol. CLAUDE.md describes
  the IPC contract at a high level.

Two wire-level companion docs already exist in the core library — don't
duplicate them here, just cross-link:

- `src/Wavee/Connect/DEALER_PROTOCOL.md` — Spotify dealer wire format and
  message catalogue.
- `src/Wavee/Connect/DEALER_IMPLEMENTATION_GUIDE.md` — implementation
  walk-through.

## How To Use This Guide

1. Skim the **Quick-find table** to locate your surface.
2. Read the **Core contracts** before adding observables or commands —
   nearly every "I need to know X about playback state" already exists on
   `PlaybackStateManager` or `IPlaybackStateService`.
3. Use the **Reactive flow** section to find which observable the change
   should attach to (raw dealer message vs. parsed cluster vs. UI shim).
4. If you add / remove a remote command or a new observable on
   `IPlaybackStateService`, update this file (and bump `last_verified`).

Useful re-verification commands:

```
rg -n "class DealerClient|class DeviceStateManager|class PlaybackStateManager|class ConnectCommandClient" src/Wavee/Connect
rg -n "interface IPlaybackStateService" src/Wavee.UI src/Wavee.UI.WinUI
rg -n "AddSingleton<I?(PlaybackStateService|PlaybackService|ConnectCommandExecutor|RemoteStateRecorder)>" src/Wavee.UI.WinUI
rg -n "\.SendAsync\b" src/Wavee/Connect/ConnectCommandClient.cs
rg -n "PutStateReason\.|new PutStateRequest" src/Wavee
```

## Quick-find Table

| Surface | Host file:line | DTO / Contract | Source binding |
| --- | --- | --- | --- |
| Dealer message bus | `src/Wavee/Connect/DealerClient.cs` (`Messages`) | `IObservable<DealerMessage>` | Single hot subject; every other manager filters by URI. |
| Dealer requests inbound | `DealerClient.cs` (`Requests`) | `IObservable<DealerRequest>` | Remote commands targeting this device (Play / Pause / Seek / Transfer / Queue) arrive here. |
| Connection state machine | `DealerClient.cs` (`ConnectionState`) | `IObservable<ConnectionState>` | Disconnected → Connecting → Connected. |
| Connection-ID stream | `DealerClient.cs` (`ConnectionId`) | `IObservable<string?>` | Sourced from `hm://pusher/v1/connections/<id>` notifications; required for device announce. |
| Device announce | `src/Wavee/Connect/DeviceStateManager.cs` | `PutStateRequest` proto | `DeviceStateManager.AnnounceAsync` (PUT to `/connect-state/v1/devices/<deviceId>`). |
| Remote volume from server | `DeviceStateManager.cs` (subscribes to `hm://connect-state/v1/connect/volume`) | `int Volume` (0–65535) | `DeviceStateManager.Volume` observable. |
| Cluster snapshot | `src/Wavee/Connect/PlaybackStateManager.cs` (`StateChanges`) | `PlaybackState` record / `Cluster` proto | Parsed via `PlaybackStateHelpers.TryParseCluster` / `TryParseClusterUpdate`. |
| Remote command client | `src/Wavee/Connect/ConnectCommandClient.cs` (`SendAsync`) | typed `*Command` records | POST to `/connect-state/v1/player/command/from/<from>/to/<to>`. |
| Library-change manager (cross-ref) | `src/Wavee/Connect/LibraryChangeManager.cs` | `LibraryChangeEvent` | Cross-link only; full coverage in library-and-sync guide. |
| Gabo telemetry envelopes | `src/Wavee/Connect/Events/EventService.cs`, `Events/GaboEnvelopeFactory.cs` | gabo proto envelopes | Posts to `https://spclient.wg.spotify.com/gabo-receiver-service/v3/events/`. |
| Remote-state recorder (debug) | `src/Wavee/Diagnostics/IRemoteStateRecorder.cs` | `RemoteStateEvent` | Off by default; toggled by the Debug page; capped at 500 entries. |
| `Session` aggregator | `src/Wavee/Core/Session/Session.cs` | lazy `Dealer` / `DeviceState` / `PlaybackState` accessors | Single instance per session; constructed in `AppLifecycleHelper.ConfigureHost`. |
| UI playback service interface | `src/Wavee.UI/Contracts/IPlaybackStateService.cs` | `IPlaybackStateService` | Framework-neutral DTOs / enums; consumed by view models. |
| UI playback service implementation | `src/Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs` | bridges `PlaybackStateManager.StateChanges` → `INotifyPropertyChanged` | DI singleton; injected into every player-related view model. |
| UI command service | `src/Wavee.UI.WinUI/Data/Contexts/PlaybackService.cs` + `ConnectCommandExecutor.cs` | orchestrator: retry, buffering, notifications, command dedupe | UI calls go through here, not directly to `ConnectCommandClient`. |
| Device picker / volume slider | `src/Wavee.UI.WinUI/Controls/Playback/AudioOutputPicker.xaml(.cs)` | `ConnectDevice` rows + slider value | `IPlaybackStateService.AvailableConnectDevices`, `Volume`, transfer on row click. |
| Active-device label | `src/Wavee.UI.WinUI/Controls/PlayerBar/PlayerBar.xaml` + sidebar player | `string ActiveDeviceName`, `DeviceType ActiveDeviceType` | `PlayerBarViewModel`, reads `IPlaybackStateService`. |
| Now-playing track display | `Controls/PlayerBar/PlayerBar.xaml` + `Controls/SidebarPlayer/SidebarPlayerWidget.xaml` + `Controls/SidebarPlayer/ExpandedNowPlayingLayout.xaml` + `Windows/PlayerFloatingWindow.xaml.cs` | current `ITrackItem`-like adapter | `PlayerBarViewModel.CurrentTrack`. |
| Queue (from cluster) | `src/Wavee.UI.WinUI/Controls/Queue/QueueControl.xaml:17` | `QueueDisplayItem` | `PlaybackStateService.RawNextQueue` + current playback metadata. |
| Connect-state debug view | `src/Wavee.UI.WinUI/Controls/Settings/ConnectStateSection.xaml:90` | `RemoteStateEvent` list | `IRemoteStateRecorder.FilteredEvents`; populated only when the user opts in. |

## Core contracts

### `DealerClient` — `src/Wavee/Connect/DealerClient.cs`

The single owner of the dealer WebSocket. Every other Connect class
subscribes to its observables.

Public surface:
- `IObservable<DealerMessage> Messages` — every inbound dealer message
  after URI filtering. Hot subject; never replays.
- `IObservable<DealerRequest> Requests` — remote commands targeting this
  device (when Wavee is the Connect *target*, not just a controller).
- `IObservable<ConnectionState> ConnectionState` — three-state machine
  (`Disconnected`, `Connecting`, `Connected`). UI surfaces that need to
  hide the device picker when offline subscribe here.
- `IObservable<string?> ConnectionId` — the `hm://pusher/v1/connections/<id>`
  payload. `DeviceStateManager` waits for this before doing its first
  PutState announce.
- `Task SubscribeAsync(string uri)` / `UnsubscribeAsync(string uri)` —
  explicit topic subscriptions. Currently consumed by the managers; not
  called from UI.
- `Task SendReplyAsync(DealerRequest request, bool success)` — ack a
  remote command. `DeviceStateManager` and the player layer ack their own
  requests; UI doesn't touch this directly.
- `IObservable<DealerMessage> Pings` — heartbeat for diagnostics.

### `DeviceStateManager` — `src/Wavee/Connect/DeviceStateManager.cs`

Owns this-device's identity on the network: announces capabilities,
maintains the local view of remote-set volume, fields incoming volume
commands.

Public surface:
- `Task AnnounceAsync(PutStateReason reason, PlayerState? playerState = null)` —
  PUT to `/connect-state/v1/devices/<deviceId>` with a populated
  `PutStateRequest`. `reason` is one of `SPIRC_HELLO`, `PUT_STATE_REASON_NEW_DEVICE`,
  `PUT_STATE_REASON_PLAYER_STATE_CHANGED`, `PUT_STATE_REASON_VOLUME_CHANGED`,
  etc. (see `PutStateReason` in `Protocol/Generated/Connect.cs`).
- `IObservable<int> Volume` — locally observable volume (0–65535,
  Spotify's internal range). Updates when the server pushes a volume change
  *or* when local code sets the volume.
- `bool IsActive` — true when Wavee is the cluster's `active_device_id`
  (i.e. Wavee is playing).
- Internally subscribes to `DealerClient.Messages` for
  `hm://connect-state/v1/connect/volume` and applies the server-side change.

### `PlaybackStateManager` — `src/Wavee/Connect/PlaybackStateManager.cs`

The reactive snapshot of the cluster. Subscribes to the dealer cluster
update topic, deserializes `Cluster` / `ClusterUpdate` protobufs into the
immutable `PlaybackState` record, and emits snapshots.

Public surface:
- `IObservable<PlaybackState> StateChanges` — backed by a
  `BehaviorSubject`; replays the current state on subscribe.
- `IObservable<ChangeFlags> StateChangedHints` (or equivalent) — bitmask
  hint per emission (`TrackChanged`, `QueueChanged`, `OptionsChanged`,
  `ActiveDeviceChanged`, …) so consumers can filter cheaply.
- Helpers in `PlaybackStateHelpers.cs`: `TryParseCluster(bytes) →
  PlaybackState?`, `TryParseClusterUpdate(bytes) → ClusterDelta?`.

`PlaybackState` is an immutable record. New emissions allocate a fresh
record; consumers should compare references / fields rather than mutate.

### `ConnectCommandClient` — `src/Wavee/Connect/ConnectCommandClient.cs`

Sends commands to a remote device via Spotify's command bus. Wraps the
POST to `/connect-state/v1/player/command/from/<from>/to/<to>` with
serialization of the typed command records.

Command catalogue (one record per command, all in
`src/Wavee/Connect/Commands/`):

- `PlayCommand` — start playback (with optional context, track index,
  position).
- `PauseCommand` / `ResumeCommand`.
- `SeekCommand` — seek to position in current track.
- `SkipNextCommand` / `SkipPrevCommand`.
- `SetVolumeCommand` — 0–65535.
- `SetOptionsCommand` — shuffle, repeat (track/context), smart shuffle.
- `TransferCommand` — transfer playback to another device (carries the
  current `PlayerState` so the target picks up at the right point).
- `UpdateContextCommand` — change context without restarting (used for
  queue rearrangements that mutate the source).
- `AddToQueueCommand` / `SetQueueCommand` / `MoveCommand` / `RemoveCommand`
  — queue mutations.

All commands return an ack via the dealer; `ConnectCommandClient` tracks
ack correlation by command ID and exposes a `Task<bool>` for the caller.

### `IPlaybackStateService` — `src/Wavee.UI/Contracts/IPlaybackStateService.cs`

Framework-neutral interface. Lives in `Wavee.UI` (no WinUI deps) so it can
be implemented for tests / future surfaces.

Key observables / properties (the concrete implementation surfaces them as
`INotifyPropertyChanged` properties + `IObservable<>` streams):
- `CurrentTrack` (DTO, not `ITrackItem`).
- `IsPlaying`, `IsPaused`, `IsBuffering`, `Position`, `Duration`.
- `ShuffleEnabled`, `RepeatMode`, `SmartShuffleEnabled`.
- `Volume` (0–100 percent — NOT the protocol's 0–65535).
- `ActiveDeviceId`, `ActiveDeviceName`, `ActiveDeviceType`,
  `IsPlayingRemotely`.
- `AvailableConnectDevices` (`ObservableCollection<ConnectDevice>`).
- `RawNextQueue` (`IReadOnlyList<QueueItem>`), `CurrentQueueIndex`.
- `Restrictions` (which UI affordances should disable).

Command-side methods (all return `Task` and route through the executor):
- `PlayAsync`, `PauseAsync`, `ResumeAsync`, `SeekAsync`,
  `SkipNextAsync`, `SkipPrevAsync`.
- `SetVolumeAsync` (percent), `SetShuffleAsync`, `SetRepeatAsync`.
- `TransferToAsync(deviceId)`.
- `AddToQueueAsync`, `RemoveFromQueueAsync`, `MoveInQueueAsync`,
  `SetQueueAsync`.

### `Session` — `src/Wavee/Core/Session/Session.cs`

Lazy-init aggregator for every protocol manager. Constructed in
`AppLifecycleHelper.ConfigureHost`. Public:

- `Dealer` — the `DealerClient`. Null if Connect is disabled (rare;
  effectively always present in WinUI app).
- `DeviceState` — the `DeviceStateManager`. Null until the connection ID
  arrives.
- `PlaybackState` — the `PlaybackStateManager`.
- `Pathfinder`, `AudioKeyManager`, `Mercury`, `EventService` — out of scope
  for this guide.
- `IRemoteStateRecorder` — optional, off by default; the Debug page flips
  it on.

Don't construct these managers directly. The `Session` instance is the
single source.

## Protobuf types worth knowing

Generated into `src/Wavee/Protocol/Generated/`. Don't fully document — these
are the ones agents touch most.

| Type | File | Used for |
| --- | --- | --- |
| `Cluster` | `Connect.cs` | Full cluster snapshot (device list, active device ID, player state, timestamp). Sent by dealer + returned by `/connect-state/v1/devices`. |
| `ClusterUpdate` | `Connect.cs` | Incremental delta (reason, ack_id, devices_to_remove, … plus a fresh `Cluster`). |
| `Device` | `Connect.cs` | One device entry; nests `DeviceInfo` and `PlayerState`. |
| `DeviceInfo` | `Connect.cs` | Device metadata: name, type, brand, model, capabilities, current volume, aliases, audio output device. |
| `Capabilities` | `Connect.cs` | Per-device feature flags (`can_be_player`, `supports_transfer_command`, `supports_command_request`, `supports_lossless_audio`, `supports_hi_fi`, …). |
| `PutStateRequest` | `Connect.cs` | What `DeviceStateManager.AnnounceAsync` PUTs: device info, player state, member type, last command id + message id, started_playing_at, has_been_playing_for, client_side_timestamp, etc. |
| `PutStateReason` (enum) | `Connect.cs` | Why we're announcing (`SPIRC_HELLO`, `NEW_DEVICE`, `PLAYER_STATE_CHANGED`, `VOLUME_CHANGED`, `AUDIO_DRIVER_INFO_CHANGED`, …). |
| `PlayerState` | `Player.cs` | Track + position + options + restrictions + context + queue revision. Carried inside `Device` and inside `PutStateRequest`. |
| `ProvidedTrack` | `Player.cs` | Track in cluster (URI, name, artist URI/name, album URI/name, provider, metadata bag, restrictions). |
| `ContextPlayerOptions` | `Player.cs` | Shuffle, repeat-track, repeat-context, modes. |
| `Restrictions` | `Player.cs` | Why a command is disallowed (e.g. `disallow_pausing_reasons = ["paused_endless"]`). UI uses these to grey out controls. |

## Reactive flow

```
                       Spotify edge
                            │
                            ▼
       hm://pusher/v1/connections/<id> ──┐
       hm://connect-state/v1/connect/*  ─┤   raw dealer frames
       hm://connect-state/v1/cluster/*  ─┤
       …                                ─┘
                            │
                            ▼
                 ┌───────────────────────┐
                 │  DealerClient         │
                 │   .Messages (hot)     │   one subject; everyone filters
                 │   .Requests (hot)     │
                 │   .ConnectionState    │
                 │   .ConnectionId       │
                 └─────────┬─────────────┘
                           │
        ┌──────────────────┼─────────────────────────┐
        ▼                  ▼                         ▼
DeviceStateManager   PlaybackStateManager     LibraryChangeManager
  AnnounceAsync       StateChanges (BS)        Changes (subject)
  Volume (obs)        Cluster/ClusterUpdate    LibraryChangeEvent
  IsActive            parse → PlaybackState    (see library-and-sync)
                            │
                            ▼
              ┌─────────────────────────────┐
              │  PlaybackStateService (WinUI)│
              │  - INPC properties           │
              │  - ObservableCollection lists│
              │  - Maps cluster device list  │
              │    → ConnectDevice rows      │
              └──────────┬──────────────────┘
                         │
                         ▼
              XAML bindings everywhere
              (PlayerBar, SidebarPlayer,
               AudioOutputPicker, QueueControl,
               PlayerBarViewModel, …)
```

Commands flow the other direction:

```
UI button click
   → PlayerBarViewModel / ContextMenu / AudioOutputPicker
   → IPlaybackStateService.<Verb>Async(…)
   → PlaybackService / ConnectCommandExecutor (retry, dedupe, buffering)
   → ConnectCommandClient.SendAsync(<TypedCommand>)
   → POST /connect-state/v1/player/command/from/<this>/to/<target>
   → dealer ack arrives via DealerClient.Messages
   → Task<bool> completes
   → cluster update arrives shortly after via PlaybackStateManager
   → UI properties re-render
```

The local-vs-remote distinction:
- When Wavee is the **active device** (this-device), commands still go
  through the same path. They route to "this device" through Spotify's
  command bus rather than directly to local playback. The internal
  `LocalPlayerControl` path picks them up via the same cluster-update
  loop.
- When playing remotely, the queue and now-playing come from the cluster
  only. Wavee renders nothing locally.

## Framework split

Stay disciplined about which assembly owns what:

| Assembly | Owns |
| --- | --- |
| `Wavee` (core protocol) | `DealerClient`, `DeviceStateManager`, `PlaybackStateManager`, `ConnectCommandClient`, `ConnectCommand*` records, gabo telemetry, protobuf types. No UI references. |
| `Wavee.UI` (framework-neutral) | `IPlaybackStateService` interface, DTOs (`ConnectDevice`, `QueueItem`, `PlaybackContextInfo`, `AudioOutputDeviceDto`), enums (`DeviceType`, `RepeatMode`, `LocalContentKind`). No WinUI / XAML. |
| `Wavee.UI.WinUI` (desktop app) | `PlaybackStateService` (concrete `IPlaybackStateService` with `INotifyPropertyChanged`), `PlaybackService` (command orchestration / retry), `ConnectCommandExecutor` (wraps `ConnectCommandClient`), every XAML control / page / view model. |

When adding a new observable to the service: declare it on
`IPlaybackStateService` (in `Wavee.UI`), implement it in
`PlaybackStateService` (in `Wavee.UI.WinUI`), bind from XAML. Don't put
WinUI types into `Wavee.UI` and don't put XAML bindings against the core
manager directly.

## UI surfaces in detail

`src/Wavee.UI.WinUI/Controls/Playback/AudioOutputPicker.xaml(.cs)`
- Output-device picker flyout (the speaker-icon button in the player bar).
- Reads `IPlaybackStateService.AvailableConnectDevices`,
  `ActiveDeviceId`, `ActiveDeviceName`, `Volume`.
- Click a device row → calls `TransferToAsync(deviceId)`.
- Volume slider drags → throttled `SetVolumeAsync(percent)`.
- Includes local audio output devices via `IAudioOutputDeviceService`;
  switching to a local device works through the AudioHost path, not
  Connect. Connect devices and local devices live side by side in the
  same flyout.

`src/Wavee.UI.WinUI/Controls/PlayerBar/PlayerBar.xaml` and
`Controls/SidebarPlayer/SidebarPlayerWidget.xaml` /
`ExpandedNowPlayingLayout.xaml` / `MiniVideoPlayer/MiniVideoPlayer.xaml`
- Now-playing surfaces. All bind through `PlayerBarViewModel`, which reads
  `IPlaybackStateService` (current track / artists / artwork / position /
  duration / restrictions / device / video toggle).
- See `track-and-episode-ui.md` for the row/cell side of "this is the
  current track" visuals (the equalizer / pending-play beam / buffering
  ring on track rows live in `TrackStateBehavior`).

`src/Wavee.UI.WinUI/Controls/Queue/QueueControl.xaml`
- Queue tab in the right panel. Reads `RawNextQueue` + the current track
  for the "Now playing" row. The user-queue / next-up / queued-later /
  autoplay buckets are computed inside the control from the cluster's
  queue revision metadata.
- Queue mutations (Add to queue / Remove / Reorder) go through
  `IPlaybackStateService.AddToQueueAsync` / `RemoveFromQueueAsync` /
  `MoveInQueueAsync`.

`src/Wavee.UI.WinUI/Controls/Settings/ConnectStateSection.xaml(.cs)`
- Debug page section showing recent dealer messages and command flows.
- Source: `IRemoteStateRecorder.FilteredEvents` (when enabled).
- Toggle is in the Debug page; off by default to keep production memory
  flat.

## Change guidance

When adding a **new remote command**:
1. Define a new record in `src/Wavee/Connect/Commands/`.
2. Wire its serialization in `ConnectCommandClient.SendAsync`'s switch /
   dispatcher (if the existing pattern requires it).
3. Add a method on `IPlaybackStateService` (in `Wavee.UI`).
4. Implement the method in `PlaybackStateService` /
   `ConnectCommandExecutor` — route to `ConnectCommandClient` and surface
   errors via `INotificationService`.
5. Add a UI affordance + bind to the new method.

When adding a **new field surfaced from the cluster**:
1. If the proto already has the field, surface it through
   `PlaybackState` (the parsed record). Update
   `PlaybackStateHelpers.TryParseCluster` if needed.
2. Add a property to `IPlaybackStateService` and bind from XAML.
3. Don't read `Cluster` proto directly from view models — go through the
   service.

When adding a **new dealer URI subscription**:
1. If it's playback-related, add the filter in `DealerClient` (or the
   manager that already owns the topic, e.g. `DeviceStateManager` for
   volume).
2. If it's library-related, see `.agents/guides/library-and-sync.md` —
   `LibraryChangeManager` owns those subscriptions.
3. Update this guide's table.

When **changing PutState announce behaviour**:
- `DeviceStateManager.AnnounceAsync` is the only writer. The `reason`
  parameter matters for Spotify's anti-fraud / play-history pipeline —
  consult `CLAUDE.md` (gabo / Recently Played section) before changing
  the SDK/version/device-context fragments.
- Memory `project_recently_played_solved` (in this conversation's memory)
  documents which fragments matter and why.

When investigating **connect-state bugs**:
1. Turn on `IRemoteStateRecorder` from the Debug page so you can see
   every dealer message + HTTP call in `ConnectStateSection`.
2. Compare against the wire-level reference in
   `src/Wavee/Connect/DEALER_PROTOCOL.md`.
3. Check `Capabilities` on this device's `DeviceInfo` — most "command
   refused" issues come from a capability flag that doesn't match what
   the server expects.

## Keeping This Guide Current

If you add, remove, or rename a Connect-related surface:
1. Update the Quick-find table and the affected section.
2. Re-run the re-verification commands at the top — each should produce
   one hit per registered type (`DealerClient`, `DeviceStateManager`,
   `PlaybackStateManager`, `ConnectCommandClient`, `IPlaybackStateService`,
   `PlaybackStateService`, `PlaybackService`, `IRemoteStateRecorder`).
3. Update `last_verified` in the frontmatter.
4. If a class moves out of `src/Wavee/Connect/` or
   `src/Wavee.UI.WinUI/Data/Contexts/`, update its row instead of leaving
   stale paths.
