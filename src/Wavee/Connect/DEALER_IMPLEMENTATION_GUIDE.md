# DealerClient Implementation Guide

How `DealerClient` and friends are organized in this codebase. The wire-level Spotify protocol is documented separately in [DEALER_PROTOCOL.md](DEALER_PROTOCOL.md) — this guide is about our C# implementation.

## Architecture

`DealerClient` exposes Spotify's Dealer WebSocket as a small surface of `IObservable<T>` streams plus reply / send methods. Internally it composes a few single-responsibility components.

```
DealerClient
├── IDealerConnection         (Connection/IDealerConnection.cs)
│   └── DealerConnection      (Connection/DealerConnection.cs)   — System.IO.Pipelines WebSocket wrapper
├── HeartbeatManager          (HeartbeatManager.cs)              — PeriodicTimer-driven PING + PONG-timeout detection
├── ReconnectionManager       (ReconnectionManager.cs)           — exponential backoff
├── MessageParser             (Protocol/MessageParser.cs)        — Utf8JsonReader-based, allocation-light
├── DealerJsonSerializerContext (Protocol/DealerJsonSerializerContext.cs) — source-generated JSON for outgoing PING / reply
├── AsyncWorker<DealerMessage>  / AsyncWorker<DealerRequest>     — non-blocking dispatch (Core/Utilities/AsyncWorker.cs)
├── SafeSubject<T>            (SafeSubject.cs)                   — IObservable that isolates subscriber exceptions
└── IRemoteStateRecorder?     (Diagnostics/IRemoteStateRecorder.cs) — optional capture of every dealer event
```

## Reactive surface

```csharp
public IObservable<DealerMessage>  Messages      { get; }   // hot, all MESSAGE-type frames
public IObservable<DealerRequest>  Requests      { get; }   // hot, all REQUEST-type frames
public IObservable<ConnectionState> ConnectionState { get; }
public IObservable<string?>        ConnectionId  { get; }   // updated when hm://pusher/v1/connections/* arrives
```

Filter with `.Where(...)` and subscribe; subscriptions are dispatched on the `AsyncWorker` so a slow subscriber can't block the WebSocket receive loop.

```csharp
dealer.Messages
    .Where(m => m.Uri.StartsWith("hm://connect-state/v1/cluster"))
    .Subscribe(msg => _ = HandleClusterAsync(msg));
```

REQUEST frames (Spotify Connect commands etc.) need a reply — use `dealer.SendReplyAsync(request.Key, success: true)` (or false) when done. There's a pending-request timeout timer to ensure REQUESTs that nobody handles still get a `success:false` reply, so the server doesn't think the device dropped them.

## Connection lifecycle

1. `ConnectAsync(session, httpClient)` resolves a dealer host through `apresolve.spotify.com`, gets an OAuth token from `Session.GetAccessTokenAsync(...)`, opens `wss://{host}/?access_token={token}`.
2. `HeartbeatManager.Start()` fires a PING every 30 s (default). Each PING sets `_waitingForPong = true`; an incoming PONG calls `RecordPong()` which clears it. If no PONG comes back within the configured timeout (3 s default), `HeartbeatTimeout` fires → `DealerClient` triggers a reconnect.
3. Inbound bytes flow `WebSocket → System.IO.Pipelines pipe → MessageParser.TryParse → DealerMessage / DealerRequest record → SafeSubject<T>.OnNext`.
4. Outbound: pre-encoded UTF-8 PING / PONG buffers (`PingMessageBytes`, `PongMessageBytes`) avoid per-tick allocation. Replies are serialized with `DealerJsonSerializerContext` (source-gen, AOT-safe).

## Message types (`Protocol/`)

```csharp
public enum MessageType : byte { Unknown, Ping, Pong, Message, Request }

public sealed record DealerMessage
{
    public required string Uri { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required byte[] Payload { get; init; }
}

public sealed record DealerRequest
{
    public required string Key          { get; init; }   // opaque; passed back in the reply
    public required string MessageIdent { get; init; }
    public required int    MessageId    { get; init; }   // parsed from Key when shaped "id/device"
    public required string SenderDeviceId { get; init; }
    public required JsonElement Command { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
```

Earlier iterations of this code used `ref struct` views over pooled buffers. They were reverted to `record` + materialized `byte[]` because consumers (PlaybackStateManager, LibraryChangeManager, etc.) need to keep the payload past the parse callback's stack frame, and the `ref struct` model required every consumer to copy explicitly. Records are simpler and the per-message allocation cost is dwarfed by the protobuf parse that follows.

## Backpressure and pre-subscription buffering

`DealerConnection` configures its `Pipe` with `pauseWriterThreshold = 1 MB`, `resumeWriterThreshold = 512 KB` — if a slow consumer can't keep up, the WebSocket reader pauses instead of growing memory unboundedly.

`DealerClient` also runs a pre-subscription queue: messages received before `PlaybackStateManager` subscribes (during the brief window between `ConnectAsync` returning and the higher-level managers wiring up) are buffered into a bounded `Channel<DealerMessage>` and flushed on first subscribe. Without this, the very first cluster snapshot (which arrives within milliseconds of the WebSocket opening) could be dropped.

## Diagnostics

Every dealer message, request, reply, connection-state change, and reconnect attempt is forwarded to the optional `IRemoteStateRecorder`. The WinUI app implements this in `Wavee.UI.WinUI/Services/RemoteStateRecorder.cs` (capped at 500 entries) and surfaces the log in the in-app Debug page. Pass `null` to disable.

## AOT / trim safety

- Outgoing JSON uses `DealerJsonSerializerContext` (`[JsonSourceGenerationOptions]`) — no reflection at runtime.
- Inbound parsing uses `Utf8JsonReader` directly — no `JsonSerializer.Deserialize` of arbitrary types.
- The whole `Wavee.csproj` treats every IL2xxx / IL3xxx warning as an error, so any reflection that sneaks in fails the build.

## Files

```
Connect/
├── DealerClient.cs               # The orchestrator — public sealed class, IAsyncDisposable
├── DealerClientConfig.cs         # Config (intervals, timeouts, reconnection policy)
├── DealerException.cs            # Top-level exception type + DealerFailureReason enum
├── HeartbeatManager.cs           # PING / PONG (internal)
├── ReconnectionManager.cs        # Exponential backoff (internal)
├── SafeSubject.cs                # IObservable that catches subscriber exceptions
├── ConnectStateHelpers.cs        # Shared helpers for cluster-state mutation
├── PlaybackStateHelpers.cs       # Shared helpers for player-state mutation
├── PlaybackState.cs              # Player-state record
├── DeviceStateManager.cs         # PutState announcer + volume sync
├── PlaybackStateManager.cs       # Reactive cluster-state aggregator
├── ConnectCommandClient.cs       # Generic Connect command sender with ack tracking
├── LibraryChangeManager.cs       # Listens for library / collection change messages
├── IPlaybackEngine.cs            # Abstraction the audio orchestrator implements
├── Commands/                     # PlayCommand, SeekCommand, TransferCommand, …
├── Connection/                   # IDealerConnection + DealerConnection (Pipelines WebSocket)
├── Diagnostics/                  # IRemoteStateRecorder + RemoteStateEvent
├── Events/                       # Gabo envelope + every playback-event type
└── Protocol/                     # DealerMessage, DealerRequest, MessageParser, MessageType, JSON context
```
