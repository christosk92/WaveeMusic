# Spotify Connect Remote-State Inspector

## Context

As Wavee evolves we keep extending what we push to Spotify Connect (PutState payloads,
PlayerState fields, video provider switches, etc.) but the actual wire traffic is invisible —
we only see what we *intended* to send. When the UI behaves wrongly (Recently Played not
recording, ghost devices, wrong track active) we have to reach for log lines, and there's no
way to see Spotify's actual response (the Cluster) side-by-side with our request.

The 2026-04-28 Recently Played fix proved how expensive opaque wire traffic is: it took weeks
to discover that `envelope.context_fragments` had to mirror desktop exactly, partly because we
couldn't *see* what we were sending vs. what desktop sent. A live inspector for Connect-state
traffic makes the next debugging session take minutes instead of weeks.

**Goal:** Add a "Connect" tab to Settings (sibling of Diagnostics) that records every
Spotify Connect remote-state interaction and lets the developer expand any row to see the
full protobuf as JSON. Captured sources:

1. **PutState** — outbound request + inbound response (parsed Cluster)
2. **ClusterUpdate** — dealer pushes on `hm://connect-state/v1/cluster*`
3. **VolumeCommand** — dealer pushes on `hm://connect-state/v1/connect/volume`
4. **ConnectionId lifecycle** — dealer ConnectionId acquired / changed
5. **Dealer commands** — inbound remote commands (play/pause/seek/transfer/...) + the reply
   we send back
6. **Subscriptions** — every `Where(uri.StartsWith(...))` filter registered on the dealer
   `.Messages` stream (registered once at startup; recorded so we can see who's listening to
   what)
7. **Dealer lifecycle** — Connecting / Connected / Disconnected transitions of the dealer
   websocket

## Architecture

Three layers, with **one chokepoint per data flow** so the recorder hooks are concentrated:

1. **Domain** — `IRemoteStateRecorder` interface + `RemoteStateEvent` record live in
   `Wavee/Connect/Diagnostics/`. Connect-layer code depends on the interface only.

2. **Hooks (single chokepoints):**

   | Source | Chokepoint file:line | Why one place is enough |
   |---|---|---|
   | PutState out + response | `Wavee/Core/Http/SpClient.cs:434` (`PutConnectStateAsync`) | Both `PlaybackStateManager.PublishStateAsync` (line 1017) and `DeviceStateManager.UpdateStateAsync` (line 222) call this method — capture once |
   | Cluster pushes | `Wavee/Connect/PlaybackStateManager.cs:417` (`OnClusterUpdate`) | Single subscriber to all `hm://connect-state/v1/cluster*` dealer messages |
   | Volume commands | `Wavee/Connect/DeviceStateManager.cs:133` (`OnVolumeMessage`) | Single subscriber to volume URI |
   | Dealer commands in | `Wavee/Connect/Commands/ConnectCommandHandler.cs:152` (`OnDealerRequest`) | Single dispatcher into the typed-command observables |
   | Dealer replies out | `Wavee/Connect/DealerClient.cs:248` (`SendReplyAsync`) | Sole place every reply JSON is built and sent |
   | Dealer lifecycle | `Wavee/Connect/DealerClient.cs:29` (`_connectionState` BehaviorSubject) | All transitions go through `OnNext` — subscribe to `DealerClient.ConnectionState` from Session |
   | ConnectionId | `Wavee/Core/Session/Session.cs` (already owns dealer) | Subscribe to `_dealerClient.ConnectionId` |
   | Subscriptions | each manager's ctor at the `.Where(uri.StartsWith(...))` site | Static one-shot record at registration time — there's no spsub batch protocol; subscriptions are just client-side filters |

3. **UI (Wavee.UI.WinUI)** — `RemoteStateRecorder` (DispatcherQueue-marshalling ring buffer
   modeled on `InMemorySink`), `ConnectStateViewModel`, `ConnectStateSection.xaml` UserControl
   added as a sibling tab to the existing `DiagnosticsSettingsSection`.

The recorder is plumbed through `Session.Create` as an **optional** parameter (default null).
When null, all hooks compile to a single null check + return — zero overhead in non-debug
builds.

## Tech Stack

- C#/.NET 8, WinUI 3, CommunityToolkit Segmented control
- `Google.Protobuf.JsonFormatter` for protobuf → JSON (already used at
  PlaybackStateManager.cs:1014); `JsonRichTextBlock` (Wavee.UI.WinUI/Controls/JsonRichTextBlock.cs)
  for syntax-coloured display
- Reuse `InMemorySink` ring-buffer + DispatcherQueue batching pattern
  (Wavee.UI.WinUI/Services/InMemoryLoggerProvider.cs)

---

## File Map

**Create:**
- `Wavee/Connect/Diagnostics/IRemoteStateRecorder.cs` — interface + extension helpers
- `Wavee/Connect/Diagnostics/RemoteStateEvent.cs` — record + `RemoteStateEventKind` enum + `RemoteStateDirection` enum
- `Wavee.UI.WinUI/Services/RemoteStateRecorder.cs` — production impl
- `Wavee.UI.WinUI/ViewModels/ConnectStateViewModel.cs` — filter / pause / clear
- `Wavee.UI.WinUI/Controls/Settings/ConnectStateSection.xaml` (+ .xaml.cs) — UI

**Modify:**
- `Wavee/Core/Http/SpClient.cs` — accept recorder, hook PutConnectStateAsync
- `Wavee/Connect/PlaybackStateManager.cs` — accept recorder, hook OnClusterUpdate, register subscription
- `Wavee/Connect/DeviceStateManager.cs` — accept recorder, hook OnVolumeMessage, register subscription
- `Wavee/Connect/DealerClient.cs` — accept recorder, hook SendReplyAsync (and ideally connection-state observable subscription is in Session, not here)
- `Wavee/Connect/Commands/ConnectCommandHandler.cs` — accept recorder, hook OnDealerRequest, register subscription
- `Wavee/Connect/LibraryChangeManager.cs` — register subscription URIs
- `Wavee/Connect/ConnectCommandClient.cs` — register subscription URIs
- `Wavee/Core/Session/Session.cs` — thread recorder through; subscribe to dealer lifecycle + ConnectionId here
- `Wavee.UI.WinUI/Helpers/Application/AppLifecycleHelper.cs` — register RemoteStateRecorder singleton, pass to Session.Create
- `Wavee.UI.WinUI/Views/SettingsPage.xaml` — add `<SegmentedItem Tag="connect"/>`
- `Wavee.UI.WinUI/Views/SettingsPage.xaml.cs` — add `"connect"` case in ShowSection

---

## Tasks

### Task 1 — Domain types

**Files:**
- Create: `Wavee/Connect/Diagnostics/RemoteStateEvent.cs`
- Create: `Wavee/Connect/Diagnostics/IRemoteStateRecorder.cs`

**Why this design:** the recorder is push-only and the consumer (UI) reads the buffer. The
interface stays in Wavee (the Connect layer) so tests in Wavee.Tests don't need a UI dep.

```csharp
// Wavee/Connect/Diagnostics/RemoteStateEvent.cs
using System;
using System.Collections.Generic;

namespace Wavee.Connect.Diagnostics;

public enum RemoteStateEventKind
{
    PutStateRequest,           // outbound to spclient
    PutStateResponse,          // response bytes from spclient (parsed Cluster)
    ClusterUpdate,             // dealer push on hm://connect-state/v1/cluster
    PutStateResponseEcho,      // dealer message tagged X-Wavee-Echo=self (informational)
    VolumeCommand,             // dealer push on hm://connect-state/v1/connect/volume
    DealerCommand,             // dealer REQUEST: play/pause/seek/transfer/...
    DealerReply,               // outbound reply we send back to the dealer (success/failure)
    DealerLifecycle,           // websocket Connecting / Connected / Disconnected
    ConnectionIdAcquired,      // dealer ConnectionId observable - first value
    ConnectionIdChanged,       // dealer ConnectionId observable - replacement
    SubscriptionRegistered,    // a manager subscribed to a URI prefix on dealer.Messages
}

public enum RemoteStateDirection { Outbound, Inbound, Internal }

public sealed record RemoteStateEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required RemoteStateEventKind Kind { get; init; }
    public required RemoteStateDirection Direction { get; init; }
    public required string Summary { get; init; }
    public string? CorrelationId { get; init; }              // PutState corrId / dealer key — used to pair req/resp
    public long? ElapsedMs { get; init; }
    public int? PayloadBytes { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? JsonBody { get; init; }                   // protobuf rendered as JSON, or raw JSON for dealer msgs
    public string? Notes { get; init; }
}
```

```csharp
// Wavee/Connect/Diagnostics/IRemoteStateRecorder.cs
using System.Collections.Generic;

namespace Wavee.Connect.Diagnostics;

public interface IRemoteStateRecorder
{
    void Record(RemoteStateEvent evt);
}

public static class RemoteStateRecorderExtensions
{
    public static void Record(
        this IRemoteStateRecorder? recorder,
        RemoteStateEventKind kind,
        RemoteStateDirection direction,
        string summary,
        string? correlationId = null,
        long? elapsedMs = null,
        int? payloadBytes = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? jsonBody = null,
        string? notes = null)
    {
        if (recorder is null) return;
        recorder.Record(new RemoteStateEvent
        {
            Timestamp = System.DateTimeOffset.UtcNow,
            Kind = kind,
            Direction = direction,
            Summary = summary,
            CorrelationId = correlationId,
            ElapsedMs = elapsedMs,
            PayloadBytes = payloadBytes,
            Headers = headers,
            JsonBody = jsonBody,
            Notes = notes,
        });
    }
}
```

**Steps:**
- [ ] Create `Wavee/Connect/Diagnostics/` folder
- [ ] Write the two files exactly as above

**Commit:** `feat(connect): add IRemoteStateRecorder + RemoteStateEvent diagnostic types`

---

### Task 2 — Hook PutState chokepoint in SpClient

**Why here:** every PutConnectStateAsync call goes through this single method
(SpClient.cs:434). Hooking here covers BOTH `PlaybackStateManager.PublishStateAsync`
(PlaybackStateManager.cs:1017) and `DeviceStateManager.UpdateStateAsync`
(DeviceStateManager.cs:222).

**Files:**
- Modify: `Wavee/Core/Http/SpClient.cs`

**Steps:**

- [ ] Add `using Wavee.Connect.Diagnostics;` at top.

- [ ] Add an optional `IRemoteStateRecorder? remoteStateRecorder = null` trailing parameter
      to each `SpClient` constructor. Store in a `_remoteStateRecorder` field. If multiple
      ctors chain, only the most-derived needs the param — chain `: this(...)` for the others.

- [ ] At the top of `PutConnectStateAsync`, after the access-token / url construction
      (~SpClient.cs:447) and *before* serialization at SpClient.cs:488, capture the request:

```csharp
if (_remoteStateRecorder != null)
{
    var summary = $"PutState reason={request.PutStateReason} active={request.IsActive} " +
                  $"track={request.Device?.PlayerState?.Track?.Uri ?? "<none>"} " +
                  $"pos={request.Device?.PlayerState?.Position ?? 0}ms";
    string? json = null;
    try { json = Google.Protobuf.JsonFormatter.Default.Format(request); }
    catch (System.Exception ex) { _ = ex; /* leave json null */ }

    _remoteStateRecorder.Record(
        kind: RemoteStateEventKind.PutStateRequest,
        direction: RemoteStateDirection.Outbound,
        summary: summary,
        correlationId: request.MessageId.ToString(),
        jsonBody: json);
}
```

- [ ] After the response is received, capture it. Reuse whatever timing variable already
      exists (Stopwatch / TickCount64) — don't add a parallel one:

```csharp
if (_remoteStateRecorder != null)
{
    string? json = null;
    string? notes = null;
    try
    {
        var cluster = Wavee.Protocol.Connectstate.Cluster.Parser.ParseFrom(responseBytes);
        json = Google.Protobuf.JsonFormatter.Default.Format(cluster);
    }
    catch (System.Exception ex)
    {
        notes = $"failed to parse Cluster: {ex.Message}";
    }

    _remoteStateRecorder.Record(
        kind: RemoteStateEventKind.PutStateResponse,
        direction: RemoteStateDirection.Inbound,
        summary: $"PutState response corrId={request.MessageId} bytes={responseBytes?.Length ?? 0} elapsedMs={elapsedMs}",
        correlationId: request.MessageId.ToString(),
        elapsedMs: elapsedMs,
        payloadBytes: responseBytes?.Length ?? 0,
        jsonBody: json,
        notes: notes);
}
```

  *Note:* verify the Cluster type's full namespace by grepping `Cluster.Parser` —
  PlaybackStateHelpers.cs already uses it; copy the qualified name from there.

**Commit:** `feat(connect): record PutState requests + responses in SpClient`

---

### Task 3 — Hook inbound cluster pushes in PlaybackStateManager

**Files:**
- Modify: `Wavee/Connect/PlaybackStateManager.cs`

**Steps:**

- [ ] Add `using Wavee.Connect.Diagnostics;`.

- [ ] Add `IRemoteStateRecorder?` as optional trailing ctor param to **both** ctors at lines
      271 and 301. Store in `_remoteStateRecorder`. The bidirectional ctor chains via
      `: this(dealerClient, logger)` at line 307 — pass the recorder through that chain too.

- [ ] In the **primary ctor** (line 271, after the `_clusterSubscription` is created at line
      281), record the subscription:

```csharp
_remoteStateRecorder.Record(
    kind: RemoteStateEventKind.SubscriptionRegistered,
    direction: RemoteStateDirection.Internal,
    summary: "PlaybackStateManager → hm://connect-state/v1/cluster*, hm://connect-state/v1/put-state-response*");
```

- [ ] In `OnClusterUpdate` (line 417), after the parse succeeds and *before*
      `TryEmitConnectDeviceUpdate` at line 441, record the event:

```csharp
if (_remoteStateRecorder != null)
{
    string? json = null;
    try { json = JsonFormatter.Default.Format(cluster); }
    catch (Exception ex) { _logger?.LogTrace("recorder: cluster JSON format failed: {Err}", ex.Message); }

    var kind = isSelfEcho
        ? RemoteStateEventKind.PutStateResponseEcho
        : RemoteStateEventKind.ClusterUpdate;

    var corrId = (message.Headers != null && message.Headers.TryGetValue("X-Wavee-Corr", out var c)) ? c : null;

    var summary = $"Cluster #{clusterSeq} active={cluster.ActiveDeviceId ?? "<none>"} " +
                  $"track={cluster.PlayerState?.Track?.Uri ?? "<none>"} " +
                  $"selfEcho={isSelfEcho} uri={message.Uri}";

    _remoteStateRecorder.Record(
        kind: kind,
        direction: RemoteStateDirection.Inbound,
        summary: summary,
        correlationId: corrId,
        payloadBytes: message.Payload.Length,
        headers: message.Headers,
        jsonBody: json);
}
```

**Commit:** `feat(connect): record ClusterUpdate + subscription registration in PlaybackStateManager`

---

### Task 4 — Hook volume messages in DeviceStateManager

**Files:**
- Modify: `Wavee/Connect/DeviceStateManager.cs`

**Steps:**

- [ ] Add `using Wavee.Connect.Diagnostics;`.

- [ ] Add `IRemoteStateRecorder?` ctor param + `_remoteStateRecorder` field.

- [ ] After the volume subscription is created (DeviceStateManager.cs:92-95), register it:

```csharp
_remoteStateRecorder.Record(
    kind: RemoteStateEventKind.SubscriptionRegistered,
    direction: RemoteStateDirection.Internal,
    summary: "DeviceStateManager → hm://connect-state/v1/connect/volume");
```

- [ ] In `OnVolumeMessage` (line 133), after `var newVolume = (int)volumeCommand.Volume;`
      at line 153, record:

```csharp
if (_remoteStateRecorder != null)
{
    string? json = null;
    try { json = Google.Protobuf.JsonFormatter.Default.Format(volumeCommand); }
    catch (Exception ex) { _logger?.LogTrace("recorder: volume JSON format failed: {Err}", ex.Message); }

    _remoteStateRecorder.Record(
        kind: RemoteStateEventKind.VolumeCommand,
        direction: RemoteStateDirection.Inbound,
        summary: $"SetVolume volume={newVolume} (was {_deviceInfo.Volume})",
        payloadBytes: message.Payload.Length,
        headers: message.Headers,
        jsonBody: json);
}
```

**Note:** the resulting outbound `PutState(VolumeChanged)` is captured automatically by
Task 2's SpClient hook — don't double-record here.

**Commit:** `feat(connect): record inbound volume commands in DeviceStateManager`

---

### Task 5 — Hook dealer commands + replies

**Files:**
- Modify: `Wavee/Connect/Commands/ConnectCommandHandler.cs`
- Modify: `Wavee/Connect/DealerClient.cs`

**Steps:**

- [ ] In `ConnectCommandHandler.cs` add `using Wavee.Connect.Diagnostics;` and an
      `IRemoteStateRecorder?` optional ctor param + field.

- [ ] After the dealer-request subscription is created (ConnectCommandHandler.cs:89), register it:

```csharp
_remoteStateRecorder.Record(
    kind: RemoteStateEventKind.SubscriptionRegistered,
    direction: RemoteStateDirection.Internal,
    summary: "ConnectCommandHandler → hm://connect-state/v1/* (REQUEST messages)");
```

- [ ] In `OnDealerRequest` (line 152), after parsing the command (line 157, after the
      `command == null` early-return at line 159), record the inbound command:

```csharp
if (_remoteStateRecorder != null)
{
    string? json = null;
    try
    {
        // The raw request payload is already JSON on the wire — emit that.
        // If DealerRequest has a Payload field as bytes, decode UTF-8.
        json = request.Payload is { Length: > 0 } p
            ? System.Text.Encoding.UTF8.GetString(p)
            : null;
    }
    catch (Exception ex) { _ = ex; }

    _remoteStateRecorder.Record(
        kind: RemoteStateEventKind.DealerCommand,
        direction: RemoteStateDirection.Inbound,
        summary: $"DealerCommand endpoint={command.Endpoint} sender={command.SenderDeviceId ?? "<none>"} key={command.Key}",
        correlationId: command.Key,
        jsonBody: json);
}
```

  *Note:* check the actual property names on `DealerRequest` — search
  `class DealerRequest` in the codebase. If `request.Payload` doesn't exist, use whatever
  field carries the raw payload (likely `request.MessageBody` or similar).

- [ ] In `DealerClient.cs` add `using Wavee.Connect.Diagnostics;` and an
      `IRemoteStateRecorder?` optional ctor param + field.

- [ ] In `SendReplyAsync` (line 248), after `await _connection.SendAsync(replyJson, ...)`
      at line 271, record the reply:

```csharp
if (_remoteStateRecorder != null)
{
    var ident = _pendingRequests.TryGetValue(key, out var pending)
        ? pending.Request.MessageIdent
        : "<unknown>";
    _remoteStateRecorder.Record(
        kind: RemoteStateEventKind.DealerReply,
        direction: RemoteStateDirection.Outbound,
        summary: $"Reply key={key} result={result} ident={ident}",
        correlationId: key,
        jsonBody: replyJson);
}
```

  Place this **before** the `_pendingRequests.TryRemove` on line 274 — otherwise the
  pending tracking has been removed and we lose the message ident.

**Commit:** `feat(connect): record dealer commands + replies`

---

### Task 6 — Hook dealer lifecycle + ConnectionId in Session

**Files:**
- Modify: `Wavee/Core/Session/Session.cs`

**Steps:**

- [ ] Add `using Wavee.Connect.Diagnostics;`.

- [ ] Add `IRemoteStateRecorder? remoteStateRecorder = null` to `Session.Create` and the
      private Session ctor. Store in `_remoteStateRecorder`. Plumb through to:
      - `new SpClient(...)` (find construction site within Session)
      - `new PlaybackStateManager(...)` at Session.cs:367 (and any other `new
        PlaybackStateManager` calls — `grep PlaybackStateManager(`)
      - `new DeviceStateManager(...)` (search inside Session)
      - `new DealerClient(...)` (search inside Session)
      - `new ConnectCommandHandler(...)` (search inside Session)

- [ ] Just after the dealer is constructed, subscribe to dealer state + connection ID:

```csharp
// Dealer lifecycle: every Connecting/Connected/Disconnected transition.
_recorderDealerStateSub = _dealerClient.ConnectionState
    .DistinctUntilChanged()
    .Subscribe(s =>
    {
        _remoteStateRecorder.Record(
            kind: RemoteStateEventKind.DealerLifecycle,
            direction: RemoteStateDirection.Internal,
            summary: $"Dealer state → {s}");
    });

// Connection-ID acquired / changed.
string? lastConnectionId = null;
_recorderConnectionIdSub = _dealerClient.ConnectionId
    .Where(id => id != null)
    .Subscribe(id =>
    {
        var kind = lastConnectionId == null
            ? RemoteStateEventKind.ConnectionIdAcquired
            : RemoteStateEventKind.ConnectionIdChanged;
        var summary = lastConnectionId == null
            ? $"acquired connectionId={id}"
            : $"connectionId changed: {lastConnectionId} -> {id}";
        _remoteStateRecorder.Record(
            kind: kind,
            direction: RemoteStateDirection.Inbound,
            summary: summary);
        lastConnectionId = id;
    });
```

  Add the two field declarations near other subscription fields:

```csharp
private IDisposable? _recorderDealerStateSub;
private IDisposable? _recorderConnectionIdSub;
```

- [ ] In Session's existing dispose / cleanup path (search near line 1474 where
      `_playbackStateManager = null;` is set), dispose both subscriptions:

```csharp
_recorderDealerStateSub?.Dispose();
_recorderDealerStateSub = null;
_recorderConnectionIdSub?.Dispose();
_recorderConnectionIdSub = null;
```

**Commit:** `feat(connect): record dealer lifecycle + ConnectionId; thread recorder through Session`

---

### Task 7 — Register subscriptions in remaining managers

**Files:**
- Modify: `Wavee/Connect/LibraryChangeManager.cs`
- Modify: `Wavee/Connect/ConnectCommandClient.cs`

These two also subscribe to dealer URIs but don't otherwise need to record anything — only
the `SubscriptionRegistered` line for visibility into the routing table.

**Steps:**

- [ ] Add `using Wavee.Connect.Diagnostics;` and an optional `IRemoteStateRecorder?` ctor
      param to each. Update the call sites in Session (Task 6 plumbing).

- [ ] In `LibraryChangeManager.cs` after the dealer subscription is created (line ~41):

```csharp
_remoteStateRecorder.Record(
    kind: RemoteStateEventKind.SubscriptionRegistered,
    direction: RemoteStateDirection.Internal,
    summary: "LibraryChangeManager → hm://collection/, hm://playlist/, *collection-update*");
```

- [ ] In `ConnectCommandClient.cs` after the dealer subscription is created (line ~59):

```csharp
_remoteStateRecorder.Record(
    kind: RemoteStateEventKind.SubscriptionRegistered,
    direction: RemoteStateDirection.Internal,
    summary: "ConnectCommandClient → hm://connect-state/* (ack confirmation)");
```

**Commit:** `feat(connect): record subscription registration in remaining managers`

---

### Task 8 — UI ring-buffer recorder

**Files:**
- Create: `Wavee.UI.WinUI/Services/RemoteStateRecorder.cs`

**Pattern source:** copy structure from `Wavee.UI.WinUI/Services/InMemoryLoggerProvider.cs`
(lines 39-131) — same pendingLock + DispatcherQueue.TryEnqueue + ring-buffer cap.

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Wavee.Connect.Diagnostics;

namespace Wavee.UI.WinUI.Services;

public sealed class RemoteStateRecorder : IRemoteStateRecorder
{
    private const int MaxEntries = 500;
    private const int MaxJsonBodyChars = 64 * 1024;

    private readonly ObservableCollection<RemoteStateEvent> _entries = [];
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _pendingLock = new();
    private readonly List<RemoteStateEvent> _pending = [];
    private bool _flushQueued;
    private volatile bool _paused;

    public ObservableCollection<RemoteStateEvent> Entries => _entries;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public RemoteStateRecorder(DispatcherQueue? dispatcherQueue = null)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public void Record(RemoteStateEvent evt)
    {
        if (_paused) return;

        if (evt.JsonBody is { Length: > MaxJsonBodyChars } body)
        {
            evt = evt with
            {
                JsonBody = body[..MaxJsonBodyChars] + $"\n\n... [truncated, original was {body.Length} chars]"
            };
        }

        if (_dispatcherQueue == null)
        {
            AddEntry(evt);
            return;
        }

        lock (_pendingLock)
        {
            _pending.Add(evt);
            if (_pending.Count > MaxEntries)
                _pending.RemoveRange(0, _pending.Count - MaxEntries);

            if (_flushQueued) return;
            _flushQueued = true;
        }

        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FlushPending))
        {
            lock (_pendingLock) _flushQueued = false;
        }
    }

    public void Clear()
    {
        lock (_pendingLock) _pending.Clear();

        if (_dispatcherQueue != null)
            _dispatcherQueue.TryEnqueue(_entries.Clear);
        else
            _entries.Clear();
    }

    private void FlushPending()
    {
        List<RemoteStateEvent> batch;
        lock (_pendingLock)
        {
            batch = [.. _pending];
            _pending.Clear();
            _flushQueued = false;
        }

        foreach (var e in batch) AddEntry(e);
    }

    private void AddEntry(RemoteStateEvent e)
    {
        _entries.Insert(0, e);  // newest at top
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);
    }
}
```

**Commit:** `feat(ui): add RemoteStateRecorder ring-buffer service`

---

### Task 9 — DI wiring

**Files:**
- Modify: `Wavee.UI.WinUI/Helpers/Application/AppLifecycleHelper.cs`

**Steps:**

- [ ] Around AppLifecycleHelper.cs:509 (where `inMemorySink` is registered) add:

```csharp
.AddSingleton<Wavee.Connect.Diagnostics.IRemoteStateRecorder>(sp =>
    new Wavee.UI.WinUI.Services.RemoteStateRecorder(
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))
.AddSingleton<Wavee.UI.WinUI.Services.RemoteStateRecorder>(sp =>
    (Wavee.UI.WinUI.Services.RemoteStateRecorder)sp.GetRequiredService<Wavee.Connect.Diagnostics.IRemoteStateRecorder>())
```

- [ ] Update the `Session.Create(...)` registration around line 548 to pass the recorder:

```csharp
.AddSingleton(sp => Session.Create(
    sp.GetRequiredService<SessionConfig>(),
    sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
    sp.GetService<ILogger<Session>>(),
    sp.GetService<Wavee.Connect.Diagnostics.IRemoteStateRecorder>()))
```

**Commit:** `feat(ui): register RemoteStateRecorder in DI and pass to Session`

---

### Task 10 — ViewModel for the Connect tab

**Files:**
- Create: `Wavee.UI.WinUI/ViewModels/ConnectStateViewModel.cs`

```csharp
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.Connect.Diagnostics;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ConnectStateViewModel : ObservableObject, IDisposable
{
    private readonly RemoteStateRecorder _recorder;

    public ObservableCollection<RemoteStateEvent> AllEvents => _recorder.Entries;
    public ObservableCollection<RemoteStateEvent> FilteredEvents { get; } = [];

    [ObservableProperty] private bool _showPutStateRequest = true;
    [ObservableProperty] private bool _showPutStateResponse = true;
    [ObservableProperty] private bool _showClusterUpdate = true;
    [ObservableProperty] private bool _showVolumeCommand = true;
    [ObservableProperty] private bool _showDealerCommand = true;
    [ObservableProperty] private bool _showDealerReply = true;
    [ObservableProperty] private bool _showDealerLifecycle = true;
    [ObservableProperty] private bool _showConnectionLifecycle = true;
    [ObservableProperty] private bool _showSubscriptions = true;
    [ObservableProperty] private bool _showSelfEcho = false;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isPaused;

    public ConnectStateViewModel(RemoteStateRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _recorder.Entries.CollectionChanged += OnSourceChanged;
        Rebuild();
    }

    partial void OnIsPausedChanged(bool value) => _recorder.Paused = value;
    partial void OnShowPutStateRequestChanged(bool _) => Rebuild();
    partial void OnShowPutStateResponseChanged(bool _) => Rebuild();
    partial void OnShowClusterUpdateChanged(bool _) => Rebuild();
    partial void OnShowVolumeCommandChanged(bool _) => Rebuild();
    partial void OnShowDealerCommandChanged(bool _) => Rebuild();
    partial void OnShowDealerReplyChanged(bool _) => Rebuild();
    partial void OnShowDealerLifecycleChanged(bool _) => Rebuild();
    partial void OnShowConnectionLifecycleChanged(bool _) => Rebuild();
    partial void OnShowSubscriptionsChanged(bool _) => Rebuild();
    partial void OnShowSelfEchoChanged(bool _) => Rebuild();
    partial void OnSearchTextChanged(string _) => Rebuild();

    [RelayCommand]
    private void Clear() => _recorder.Clear();

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        FilteredEvents.Clear();
        var search = (SearchText ?? string.Empty).Trim();
        foreach (var ev in _recorder.Entries)
        {
            if (!Matches(ev)) continue;
            if (!string.IsNullOrEmpty(search) &&
                !ev.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !(ev.JsonBody?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(ev.CorrelationId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            FilteredEvents.Add(ev);
        }
    }

    private bool Matches(RemoteStateEvent ev) => ev.Kind switch
    {
        RemoteStateEventKind.PutStateRequest        => ShowPutStateRequest,
        RemoteStateEventKind.PutStateResponse       => ShowPutStateResponse,
        RemoteStateEventKind.ClusterUpdate          => ShowClusterUpdate,
        RemoteStateEventKind.PutStateResponseEcho   => ShowSelfEcho,
        RemoteStateEventKind.VolumeCommand          => ShowVolumeCommand,
        RemoteStateEventKind.DealerCommand          => ShowDealerCommand,
        RemoteStateEventKind.DealerReply            => ShowDealerReply,
        RemoteStateEventKind.DealerLifecycle        => ShowDealerLifecycle,
        RemoteStateEventKind.ConnectionIdAcquired   => ShowConnectionLifecycle,
        RemoteStateEventKind.ConnectionIdChanged    => ShowConnectionLifecycle,
        RemoteStateEventKind.SubscriptionRegistered => ShowSubscriptions,
        _ => true,
    };

    public void Dispose() => _recorder.Entries.CollectionChanged -= OnSourceChanged;
}
```

**Commit:** `feat(ui): add ConnectStateViewModel`

---

### Task 11 — UI section (Connect tab content)

**Files:**
- Create: `Wavee.UI.WinUI/Controls/Settings/ConnectStateSection.xaml`
- Create: `Wavee.UI.WinUI/Controls/Settings/ConnectStateSection.xaml.cs`

```xml
<UserControl x:Class="Wavee.UI.WinUI.Controls.Settings.ConnectStateSection"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:diagnostics="using:Wavee.Connect.Diagnostics"
             xmlns:controls="using:Wavee.UI.WinUI.Controls">
    <Grid Padding="0,8,0,0" RowSpacing="8">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0" ColumnSpacing="8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <AutoSuggestBox Grid.Column="0"
                            QueryIcon="Find"
                            PlaceholderText="search summary, JSON, corr-id..."
                            Text="{x:Bind ViewModel.SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
            <ToggleButton Grid.Column="1"
                          Content="Pause"
                          IsChecked="{x:Bind ViewModel.IsPaused, Mode=TwoWay}"/>
            <Button Grid.Column="2"
                    Content="Clear"
                    Command="{x:Bind ViewModel.ClearCommand}"/>
        </Grid>

        <ScrollViewer Grid.Row="1" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
            <StackPanel Orientation="Horizontal" Spacing="12">
                <CheckBox Content="PutState req"   IsChecked="{x:Bind ViewModel.ShowPutStateRequest, Mode=TwoWay}"/>
                <CheckBox Content="PutState resp"  IsChecked="{x:Bind ViewModel.ShowPutStateResponse, Mode=TwoWay}"/>
                <CheckBox Content="ClusterUpdate"  IsChecked="{x:Bind ViewModel.ShowClusterUpdate, Mode=TwoWay}"/>
                <CheckBox Content="Volume"         IsChecked="{x:Bind ViewModel.ShowVolumeCommand, Mode=TwoWay}"/>
                <CheckBox Content="Dealer cmd"     IsChecked="{x:Bind ViewModel.ShowDealerCommand, Mode=TwoWay}"/>
                <CheckBox Content="Dealer reply"   IsChecked="{x:Bind ViewModel.ShowDealerReply, Mode=TwoWay}"/>
                <CheckBox Content="Dealer state"   IsChecked="{x:Bind ViewModel.ShowDealerLifecycle, Mode=TwoWay}"/>
                <CheckBox Content="ConnectionId"   IsChecked="{x:Bind ViewModel.ShowConnectionLifecycle, Mode=TwoWay}"/>
                <CheckBox Content="Subscriptions"  IsChecked="{x:Bind ViewModel.ShowSubscriptions, Mode=TwoWay}"/>
                <CheckBox Content="Self-echo"      IsChecked="{x:Bind ViewModel.ShowSelfEcho, Mode=TwoWay}"/>
            </StackPanel>
        </ScrollViewer>

        <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto">
            <ItemsRepeater ItemsSource="{x:Bind ViewModel.FilteredEvents, Mode=OneWay}">
                <ItemsRepeater.Layout>
                    <StackLayout Spacing="2"/>
                </ItemsRepeater.Layout>
                <ItemsRepeater.ItemTemplate>
                    <DataTemplate x:DataType="diagnostics:RemoteStateEvent">
                        <Expander HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" Padding="12,6">
                            <Expander.Header>
                                <Grid ColumnSpacing="10">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="76"/>
                                        <ColumnDefinition Width="20"/>
                                        <ColumnDefinition Width="160"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0"
                                               Text="{x:Bind Timestamp.LocalDateTime, Converter={StaticResource TimeFormatConverter}}"
                                               FontFamily="Consolas" FontSize="11"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                                    <TextBlock Grid.Column="1"
                                               Text="{x:Bind Direction, Converter={StaticResource DirectionGlyphConverter}}"
                                               FontFamily="Consolas" FontSize="11"/>
                                    <TextBlock Grid.Column="2"
                                               Text="{x:Bind Kind}"
                                               FontFamily="Consolas" FontSize="11"
                                               Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}"/>
                                    <TextBlock Grid.Column="3"
                                               Text="{x:Bind Summary}"
                                               FontFamily="Consolas" FontSize="11"
                                               TextTrimming="CharacterEllipsis"/>
                                    <TextBlock Grid.Column="4"
                                               Text="{x:Bind PayloadBytes, Converter={StaticResource BytesConverter}}"
                                               FontFamily="Consolas" FontSize="10"
                                               Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>
                                </Grid>
                            </Expander.Header>
                            <StackPanel Spacing="6">
                                <TextBlock Text="{x:Bind Notes}"
                                           Visibility="{x:Bind Notes, Converter={StaticResource StringToVisibilityConverter}}"
                                           Foreground="{ThemeResource SystemFillColorCautionBrush}"/>
                                <controls:JsonRichTextBlock Json="{x:Bind JsonBody}"/>
                            </StackPanel>
                        </Expander>
                    </DataTemplate>
                </ItemsRepeater.ItemTemplate>
            </ItemsRepeater>
        </ScrollViewer>
    </Grid>
</UserControl>
```

```csharp
using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class ConnectStateSection : UserControl, IDisposable
{
    public ConnectStateViewModel ViewModel { get; }

    public ConnectStateSection(ConnectStateViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public void Dispose() => ViewModel.Dispose();
}
```

**Steps:**
- [ ] Confirm or create the converters used:
      - `TimeFormatConverter` — DateTime → "HH:mm:ss.fff". Search Wavee.UI.WinUI/Converters
        for an existing one; if absent, add a small one.
      - `DirectionGlyphConverter` — Outbound → "↑", Inbound → "↓", Internal → "·".
      - `BytesConverter` — int? → "1.2 KB" or "—".
      - `StringToVisibilityConverter` — already likely present; reuse.
- [ ] Confirm `JsonRichTextBlock` exposes a `Json` property (DebugPage.xaml around line 168
      already binds to it — copy that property name).

**Commit:** `feat(ui): add ConnectStateSection control with expandable JSON rows`

---

### Task 12 — Wire the section into SettingsPage

**Files:**
- Modify: `Wavee.UI.WinUI/Views/SettingsPage.xaml`
- Modify: `Wavee.UI.WinUI/Views/SettingsPage.xaml.cs`

**Steps:**

- [ ] In `SettingsPage.xaml` line 56 (immediately after the existing `DiagnosticsItem`),
      insert:

```xml
<controls:SegmentedItem x:Name="ConnectItem" Content="Connect" Tag="connect"/>
```

- [ ] In `SettingsPage.xaml.cs`, add a `_connectSection` field next to `_diagnosticsSection`:

```csharp
private ConnectStateSection? _connectSection;
```

- [ ] In `ShowSection` (line 86-94), add a switch arm:

```csharp
"connect" => _connectSection ??= new ConnectStateSection(
    new ConnectStateViewModel(
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<RemoteStateRecorder>())),
```

  Add `using Wavee.UI.WinUI.Controls.Settings;`, `using Wavee.UI.WinUI.Services;`, and
  `using Wavee.UI.WinUI.ViewModels;` at top if missing.

**Commit:** `feat(ui): expose Connect state inspector tab in Settings`

---

### Task 13 — End-to-end verification

**Steps:**

- [ ] Build:
      ```
      dotnet build Wavee.UI.WinUI/Wavee.UI.WinUI.csproj
      ```
      Fix all errors (likely: missing usings, ctor-param order, unused converter resource keys).

- [ ] Run, sign in, open **Settings → Connect** tab.

- [ ] **Boot trace expected** within ~5 s of sign-in (newest at top, so read bottom-up):
      1. `SubscriptionRegistered` rows for each manager (PSM, DSM, ConnectCommandHandler,
         LibraryChangeManager, ConnectCommandClient).
      2. `DealerLifecycle → Connecting` then `→ Connected`.
      3. `ConnectionIdAcquired`.
      4. `PutStateRequest reason=NewConnection` (from DeviceStateManager).
      5. `PutStateResponse corrId=…` with parsed Cluster JSON.

- [ ] **Trigger a remote command** — from another Spotify client, hit Play/Pause/Skip
      against this device. A `DealerCommand endpoint=play` (or similar) row appears, then a
      `DealerReply key=… result=Success` immediately after, and a flurry of
      `PutStateRequest` events as local playback updates.

- [ ] **Trigger a transfer** — start playback on phone, then transfer to Wavee. Expect:
      `DealerCommand endpoint=transfer` → `DealerReply success` → `PutStateRequest
      reason=PlayerStateChanged`.

- [ ] **Trigger a ClusterUpdate** — switch active device on phone. Expect a
      `ClusterUpdate selfEcho=False` with the new active device in the JSON.

- [ ] **Trigger a volume command** — change volume from another client. Expect
      `VolumeCommand volume=N`.

- [ ] **Force a dealer disconnect** — disable Wi-Fi briefly. Expect
      `DealerLifecycle → Disconnected`, then `→ Connecting`, then `→ Connected`,
      then a fresh `ConnectionIdChanged` and a `PutStateRequest reason=NewConnection`.

- [ ] **Pause toggle** stops new events from being added.
      **Clear** empties the list.
      **Search** filters by summary / JSON / corr-id.
      **Self-echo OFF by default** — toggle on, see `PutStateResponseEcho` rows reappear.
      **Subscriptions filter** can be turned off to declutter the live view.

- [ ] **Memory cap** — leave running with playback active for ~5 min, confirm event count
      stops growing past 500.

- [ ] **Null-recorder regression** — temporarily comment out the recorder param in
      `Session.Create` registration, build, run. App should behave identically (Connect tab
      stays empty, no errors). Restore the registration.

---

## Out of scope (intentional)

- Persisting events across app restarts. The recorder is in-memory only. If a session-
  spanning trace is needed later, add an "export to JSONL" button.
- Command-handler ack timing histograms / metrics. Visual inspection of `elapsedMs` on
  individual events is enough for now.
- Replay / send-arbitrary-PutState UI. This task is read-only insight.
- Localizing the new tab. It's a developer tool; English-only.

---

## Critical files reference (for the executor)

- **Connect chokepoints:**
  - `Wavee/Core/Http/SpClient.cs:434` — PutConnectStateAsync (request + response)
  - `Wavee/Connect/PlaybackStateManager.cs:417` — OnClusterUpdate
  - `Wavee/Connect/DeviceStateManager.cs:133` — OnVolumeMessage
  - `Wavee/Connect/Commands/ConnectCommandHandler.cs:152` — OnDealerRequest
  - `Wavee/Connect/DealerClient.cs:248` — SendReplyAsync
  - `Wavee/Connect/DealerClient.cs:29` — `_connectionState` BehaviorSubject
- **Reuse patterns:**
  - `Wavee.UI.WinUI/Services/InMemoryLoggerProvider.cs:39-131` — ring-buffer pattern
  - `Wavee.UI.WinUI/Controls/JsonRichTextBlock.cs` — JSON viewer
  - `Wavee.UI.WinUI/Controls/Settings/DiagnosticsSettingsSection.xaml:257-320` — event-list layout
- **Settings tab structure:**
  - `Wavee.UI.WinUI/Views/SettingsPage.xaml:47-58`
  - `Wavee.UI.WinUI/Views/SettingsPage.xaml.cs:67-103`
- **DI:**
  - `Wavee.UI.WinUI/Helpers/Application/AppLifecycleHelper.cs:509` — sink registration site
  - `Wavee.UI.WinUI/Helpers/Application/AppLifecycleHelper.cs:548` — Session.Create registration
