# Realtime capture — a causal flight recorder for Connect, queue and playback (Wavee 0.3)

Written 2026-09-23 against `C:\wavee\waveemusic` @ `2de0643c` (branch `main`, post 0.3-structure merge). Supersedes
the earlier one-file port of 0.2.9's `DealerArchive` (deleted in `99e398d7`) — that plan is abandoned per the owner's
2026-09-23 redirect; §1 below is the record of why. Style precedent: `library-rework-implementation.md` (real code,
file:line citations, ASCII wireframes, disjoint-file wave split); `scroll-feel-and-recording-defects-2026-09-16-implementation.md`
(an always-on trace + a Python decode tool, the closest thing 0.3 already ships to this shape).

Settings ▸ Developer ▸ "Archive Spotify realtime traffic" (`Platform.Keys.DealerArchiveEnabled`,
`src/apps/Wavee/Platform/Platform.cs:309`; row at `src/apps/Wavee/Screens/Settings.UI.cs:486`) is the toggle this
plan wires up — its subtitle's "2 GB / 90 days" promise is kept, but what it now switches on is a session-wide
capture, not a dealer-only one.

Rules baked in: **no legacy paths** (nothing from `_old/Wavee/Diagnostics/DealerArchive.cs` is reused as-is — it is
read once, in §1, to name what NOT to repeat); **no environment switches** (the one Settings toggle is the only
control surface); **props freeze at mount** (the Diagnostics tab this plan adds follows `Diagnostics.UI.cs`'s
existing remount-through-`Key` discipline); **no source-text tests** (every rotation/retention/redaction/id rule
below is a pure, engine-free class with its own test file); **every fix references its issue** (the CHANGELOG
bullet this lands with needs a real `#n` — none exists yet; §9 leaves the placeholder); **subagents never build,
test or touch git** — the orchestrator alone runs Debug + Release + `Wavee.Tests` once per wave (§8).

---

## 0. What the owner asked for, in one paragraph

Today a bug report is three disconnected artifacts: `wavee-YYYYMMDD.log` (prose, always on), the Settings toggle
(dead), and whatever the reporter remembers clicking. The ask is ONE capture, on by default only when the user
opts in, that can answer "what did the user do, and everything that happened because of it" — a causal tree, not a
frame dump — spanning input → action → playback → outbound HTTP/PUT → dealer echo → queue mutation → the UI surface
that re-rendered because of it. Remote-originated chains (a push nobody asked for) get their own root. Off, it costs
nothing; on, it is bounded, crash-safe, redacted, and has a real reader.

---

## 1. The old implementation's issues, read from git, and how this design avoids each

Read in full: `git show 99e398d7^:src/apps/_old/Wavee/Diagnostics/DealerArchive.cs` (626 lines),
`git show 99e398d7^:src/apps/_old/Wavee.Tests/DealerArchiveTests.cs` (318 lines),
`git show 99e398d7^:src/apps/_old/Wavee.Tests/Backend/DealerArchiveReplay.cs` (89 lines).

| # | What it did | Why it falls short of the new ask | This plan's answer |
|---|---|---|---|
| 1 | One `Queue<Pending>` of raw dealer frame bytes, tagged `{t, typ, uri, handled, n, off}` (`DealerArchive.cs:224-252,401-420`) | No notion of *why* a frame arrived, or what it caused. Two people staring at the ndjson still have to eyeball timestamps to guess "was this push a reaction to my play click?" | Every record carries `CauseId`/`Id`/`RootId` (§3). A frame is either a leaf under a known cause (rare — see the put-response case, item 3) or its own root (the common case: the server pushed unprompted). |
| 2 | Dealer-only. HTTP (`Wire.Handler`), the action registry (`Actions.cs:637`), `Playback.Step` transitions and `Queue` mutations are invisible to it | The exact debugging need ("why did the queue reorder, and what did the UI show") spans all four subsystems; a dealer-only capture answers a quarter of the question | One stream, one writer, multiple `CaptureKind`s (§3.1) fed from six choke points found by reading the real 0.3 code (§4) |
| 3 | `RecordPutResponse(msgId, reason, isActive, body)` is a bespoke method bolted directly onto the archive class for exactly one outbound flow (`DealerArchive.cs:159-173`) | Does not generalize: the next outbound flow worth capturing (a player command PUT, an autoplay resolve) means another one-off method | `CaptureEvent` is one shape for every kind; an outbound call site records `Begin(kind, causeId) → end id` and a response/echo records `End(id, ...)` against it — same two calls everywhere (§3.2) |
| 4 | `public static readonly DealerArchive Instance = new()` — a mutable singleton whose knobs (`MaxPendingBytes`, `Clock`, `SyncDrainForTests`) are `internal`-but-settable at runtime, and the Settings toggle calls straight into it (`Configure`/`SetEnabled`, `DealerArchive.cs:100-126`) | Bypasses the `Signal<T>` pattern the rest of 0.3 uses for live settings (`Settings.UI.cs`'s toggles, `Sidebar.cs`'s width signals); no single owner decides when it is constructed vs. configured | A `Signal<bool>` (`RealtimeCapture.EnabledSignal`, mirroring `Platform.cs`'s other settings signals) is the ONE thing every call site reads; the writer is constructed once at `HostOpenLog` time and only starts/stops its background thread on a signal transition (§3.4) |
| 5 | One byte budget (`MaxPendingBytes = 8 MB`) for every frame kind; a burst of noisy pushes can starve a rare, valuable put-state response the same as a chatty presence ping | No priority: the one frame that proves "did the server adopt our claim" can be dropped exactly like the hundredth `hm://collection/` diff | Two queues, not one: a small always-kept lane for `Cause`/`Effect` *root and link* events (never dropped — they are tiny, header-only), and a larger, droppable lane for full request/response BODIES (§3.5) |
| 6 | Rotation/retention/redaction are not separable from I/O: `MaybeDayRoll`/`SizeRoll`/`Prune` call `Directory`/`File` directly inside the same methods that decide (`DealerArchive.cs:486-608`) | CLAUDE.md's "no source-text tests" rule means a decision has to be a pure function of `(now, sizes, ages) → verdict` a test can call without touching disk — the old file never separated the two | `SegmentRotation`, `RetentionPlan`, `Redactor` are pure, engine-free classes (§6, §7) the shell calls; the shell is a thin, untested-by-source-reading I/O loop exactly like `Log`'s own `DrainFileQueue` (`Platform.Host.cs:692`) |
| 7 | No crash-safety story: a hard kill mid-write leaves a `.bin` whose last index row's declared length runs past EOF, and nothing detects it | A capture whose whole point is "read it back after a crash" has to survive the crash it is capturing | Every record is length-prefixed in its OWN segment (not a separate idx+bin pair) and the reader treats a short trailing record as "capture ended here", not an error (§6.2) — no separate index file to go out of sync with the blob |
& 8 | No redaction concept at all — dealer bytes were archived verbatim, which was fine when the only capture was dealer frames (no bearer tokens cross that socket) but is not fine once HTTP request/response bodies (bearer tokens, `Authorization` headers, cookies) are in scope | The new ask explicitly spans HTTP; verbatim capture of `Authorization: Bearer …` would turn "helps debug a crash" into "leaks the user's session" | `Redactor.Scrub` (§7.3) runs on every HTTP header/body before it reaches the writer — pure, tested against the literal header/token shapes `Wire.cs` already knows about |
| 9 | The replay helper (`DealerArchiveReplay.cs`) depends on retired 0.2.9 types (`Wavee.Backend.Realtime.WireEvent`, `StubTransport`, `DealerFrameParser`) that do not exist in 0.3's ref-struct `Spotify.DealerFrame`/`DealerMessage` | Not portable — a `ref struct` cannot cross an `IEnumerable<T>` yield boundary the old replay relied on | The reader lives outside the CLR entirely: `ops/tools/capture/decode.py` (§5) parses the wire format directly; nothing in-process needs to "replay" a ref struct |
| 10 | Ping/pong get a bespoke 5-minute keepalive-summary code path, distinct from every other record (`DealerArchive.cs:175-184,373-399`) | Special-casing one `CaptureKind` this way does not scale to the several new kinds this plan adds | Ping/pong are ordinary `DealerFrameIn`/`Out` events at `Priority.Low` (§3.5); the query tool collapses a low-priority run in its own display pass (§5.4) rather than the writer doing it |

---

## 2. The causal model

### 2.1 Shape

One record, one shape, for every kind of thing worth capturing:

```csharp
namespace Wavee.Diagnostics;

/// <summary>One event in the flight recorder. PURE DATA — no behaviour, no disk knowledge. `Fields` is a small
/// inline tagged union (not a Dictionary: this is constructed on every capture call when capture is ON, so it has
/// to be allocation-light) covering the handful of shapes every CaptureKind actually needs.</summary>
public readonly record struct CaptureEvent(
    long Seq,                 // monotonic, per-process — the ONLY total order a reader can trust across a day roll
    long Qpc,                 // Stopwatch.GetTimestamp() — sub-ms LOCAL ordering/duration within one segment
    long UnixMs,              // wall clock — the join key against wavee-YYYYMMDD.log's own UnixMs-stamped lines
    long Id,                  // this event's own id (0 for a record that nothing will ever reference as a cause)
    long CauseId,             // the event that directly caused this one; 0 = none
    long RootId,              // the top of this causal tree; equals Id when this event IS the root
    CaptureKind Kind,
    CapturePhase Phase,       // Begin | End | Point — a `Point` (a click, a UI re-render) has no matching End
    CaptureFields Fields);    // small struct: strings + ints + an optional payload-blob range (§3.3)
```

```csharp
public enum CaptureKind : byte
{
    // roots (Phase = Point unless noted)
    UserInput, ActionInvoke, Navigate, RemoteRoot,
    // causal chain (Phase = Begin/End pairs where a response/echo exists; Point otherwise)
    PlaybackCommand, PlaybackTransition,
    QueueMutation, QueueSeed,
    HttpCall,                 // Begin at send, End at response/exception — Wire.cs's existing choke point
    ConnectStatePut,          // Begin at Api.Send(ConnectStatePut), End at the Cluster response (or rejection)
    ConnectStatePutResponse,  // the Cluster echo itself (§2.5) — a Point under the ConnectStatePut's own Id
    DealerFrameIn, DealerFrameOut,
    RemoteRequest,            // an inbound dealer REQUEST (play/pause/skip/...): Begin at OnDealer, End at the ack we sent
    OwnershipTransition,      // wraps Ownership.Claim/Release/Fold/PutFailed/Tick — a Decision<OwnerFx>, never a Point
    UiRerender,
    // written as ordinary Points at their own choke points (§3.3's error/retry rows)
    DecodeFailed, FrameIgnored, Retry,
    // the ONE kind that is NEVER written — it is read-time-inferred (§2.5, §5): decode.py/the in-app tree derive
    // it from a ConnectStatePut Begin with no matching End within the sweep window
    EchoMissing,
}

public enum CapturePhase : byte { Point, Begin, End }
```

`CaptureFields` is a small fixed-shape struct (no boxing, no `object[]`): `string? A, B, C` (short labels: uri,
action id, endpoint), `long N0, N1` (byte counts, status codes, message ids), `int PayloadOffset, PayloadLength`
(a range into the segment's own payload blob — see §6.1; -1/0 when there is no body). Every `CaptureKind` documents
in a comment which of `A/B/C/N0/N1/Payload` it uses; this mirrors `WaveeLogField.Of(...)` (`Platform.Host.cs`), the
same "small named fields, not a dictionary" shape `Log.Event` already uses, so a reviewer already knows this idiom.

### 2.2 How the id flows across threads — the real answer, not a proposal to reconsider later

The instruction that spawned this plan asks explicitly whether `AsyncLocal<T>` is affordable here. It is not, for
the same reason `Platform.cs`'s own doc comments give for avoiding allocation on hot paths (P8/P10 in the wave
docs): `AsyncLocal<T>` allocates an `ExecutionContext` capture on every `await` it crosses and on every new thread a
value is read from, and 0.3's realtime paths are NOT async state machines — `Spotify.Session.Receive` is a
synchronous `while` loop on its own thread (`Spotify.Session.cs:1803-1822`), `Spotify.Connect.PublishNow` runs its
`Api.Send` synchronously on whatever thread called it, and `Playback.Step` is a synchronous fold called from
multiple different threads (the UI thread for a user action, the audio callback for `Advance`, a timer thread for
autoplay). An ambient thread-static would also be WRONG here, not just slow: several of the threads above are pool
threads that get reused, so a stale cause id from a previous, unrelated unit of work would silently leak onto the
next one that forgets to clear it.

**The actual answer: explicit propagation through the values that already cross those threads.** Every message type
that already carries state across one of these boundaries gains one more `long` field, `CauseId`, threaded through
by whichever call site constructs it — no new synchronization primitive, no thread-statics, no `AsyncLocal`:

- `Playback.Input` (`Playback.cs:1216`, a `readonly struct` already constructed at every call site: a UI action,
  a remote cluster fold, a timer tick) gains `public long CauseId { get; init; }`. The UI action call site
  (`Actions.cs:637`, `Execute`) mints or forwards the id (§2.4) and passes it into whichever `Playback.Input.*`
  factory it calls; a remote-originated `Input` (the cluster fold in `Spotify.Connect.cs`) stamps `CauseId = 0` and
  instead carries `RootId` = the dealer frame's own event id, minted at `Dispatch` (`Spotify.Session.cs:1828`).
- The mailbox item `Spotify.Connect`'s `Enqueue(new Item(delta, buffer, Current.Epoch))` (`Spotify.Connect.cs:706`)
  gains a `CauseId` field carried from whichever `PublishNow` call produced the PUT this response answers (0 for an
  unprompted push — the common case, item 2 in §1's table).
- `RequestArgs`/`Api.Send` (the one HTTP choke point, §4.2) is NOT touched — the cause id rides on
  `HttpRequestMessage.Options` (a `HttpRequestOptionsKey<long>`), which is the one field on that type built exactly
  for "carry caller context across `SendAsync`'s await without a thread-static". `Wire.cs`'s `LoggingHandler`
  reads it back in `Note()` — no signature change to any of the six `HttpClient` call sites.

One ambient convenience is still worth keeping, for the long tail of call sites that will never justify threading a
parameter (a `Log.Info` two frames deep in an entity file that wants to tag its own `UiRerender` point): a single
`[ThreadStatic] static long t_uiThreadCause` set/restored with a `using` guard ONLY around the UUI thread's
synchronous dispatch of one input event (mouse/key/action-palette), and read-only — nothing downstream ever sets
it, and it is explicitly NOT read from a background thread (the pool-thread leak this plan is refusing above). This
covers exactly the "something on the UI thread, synchronously, wants to tag its cause" case and nothing that
crosses a real boundary.

### 2.3 Typed decision records — no hand-written log call at every branch

The owner's ask is that "which branch ran and why" comes out structured, not as prose someone had to remember to
write at each `case`. The load-bearing fact that makes this cheap: **0.3's own decision points already return a
typed verdict**, because they are written as pure folds per CLAUDE.md's own house style — nothing new has to be
invented, only WRAPPED at the one call site each fold already has:

| Pure fold (already exists) | Its typed verdict | Call site to wrap (one place each) |
|---|---|---|
| `Ownership.Claim(ref s.Own, ClaimCause c, ...)` (`Playback.cs:750`) | `OwnerFx` (`[Flags]`, `:98-112`) + the `ClaimCause`/`ClaimPhase` it just set on `s.Own` | `DoRemote` (`:2196,2204,2213,2218,2261,2272`) and `DoTransfer`/local-play call sites — SIX call sites, all inside `Playback.cs`, all already passing a `ClaimCause` |
| `Ownership.Release(ref s.Own, ReleaseCause c)` (`:801`) | `OwnerFx` + `NobodyCause` | `DoTransfer` (`:2286`), `EndOfContext`/`Logout`/`LoadFailed` release sites |
| `Ownership.Fold(ref s, in ClusterFrame f, us)` (`:643`) | `OwnerFx` — F0-F6/P1-P4/A1-A3, each `return` in the switch IS the decision, already a distinct code path | the ONE cluster-apply call site in the playback host that calls `Fold` |
| `Ownership.PutFailed` / `Ownership.Tick` (`:783,798`) | `OwnerFx` | the PUT-failure and claim-expiry call sites |
| `Queue.DecideSeed(...)` (`Entities/Queue.cs:355`) | `SeedSource` enum | every call site already listed in §3.3's table |
| `AutoSkip.IsTerminal`/the skip decision (`Playback.cs:182-189`) | `bool` + the `Fault`/`LoadOrigin` it was computed from | the auto-skip call site in the load-failure path |
| `RemoteCmd` switch in `DoRemote` (`:2189-2277`) itself | the `case` taken IS the decision — no separate verdict type needed, the enum value is the record | wrap the whole `switch`, once |

**The mechanism, generalized once:** a tiny helper, `Capture.Decision(kind, causeId, verdict)`, where `verdict` is
`Enum` (constrained `where TVerdict : struct, Enum`). It is called with NO string formatting on the hot path —
`Enum` boxes on a struct enum, which is the one allocation this incurs, and it happens ONLY when `Capture.Enabled`
is true (the `Enabled` guard is checked FIRST, exactly as everywhere else in this plan), so the off-path is still one
branch. The boxed enum's `.ToString()` (a small, cached reflection call the runtime already optimizes for enums
with `[Flags]` or a small member count) is deferred until the WRITER thread formats the record for disk, never
computed on the calling thread — the calling thread only stores the boxed value and the numeric fields it already
had in hand (`s.Own.Kind`, `s.Own.Claim`, the `ClaimCause` parameter, etc.), matching `WaveeLogField.Of(...)`'s own
existing "value, formatted later" shape (`Platform.Host.cs`'s `Log.Event` calls already defer formatting to the
drain, `:692-750`).

```csharp
public static class Capture
{
    /// <summary>A structured decision: which pure fold ran, what typed verdict it returned, and the state it read
    /// to reach it — never free text. `before`/`after` are the enum values of whatever state field the fold just
    /// changed (e.g. `OwnerState.Kind` before/after `Ownership.Claim`), so a reader sees the STATE TRANSITION, not
    /// just the verdict flags.</summary>
    public static void Decision<TVerdict>(CaptureKind kind, long causeId, TVerdict verdict,
        Enum? before = null, Enum? after = null, string? reason = null) where TVerdict : struct, Enum
    { if (!Enabled) return; DecisionSlow(kind, causeId, verdict, before, after, reason); }
}
```

This is the SAME shape the task's brief asks for ("typed decision records emitted by the pure decision classes'
callers ... structured and not free text") applied to every fold in the table above, plus `RouteCommand`'s own
dispatch (§3.3's new rows below) — one wrapper per call site, not one per branch inside the fold.

### 2.4 Minting an id, and the zero-cost-when-off guarantee

```csharp
public static class Capture
{
    /// <summary>The ONE branch every call site pays when the toggle is off. Every other API on this class is a
    /// no-op-shaped wrapper around this check FIRST — nothing after it (id allocation, field formatting, the
    /// pure redaction pass) runs unless capture is on.</summary>
    public static bool Enabled => s_enabled;   // set by RealtimeCaptureHost.OnSettingsChanged (§3.4), never read raw
    ...
    /// <summary>Mint a new root id (a user input, or a dealer frame with no known parent). Returns 0 — a valid
    /// "no cause" sentinel — when capture is off, so a caller can unconditionally thread the return value through
    /// without a second `if (Capture.Enabled)` at the call site.</summary>
    public static long NewRoot(CaptureKind kind, string? a = null, string? b = null) { ... }

    /// <summary>Begin a Begin/End pair under `causeId` (0 = none). Returns the new event's own id (0 when off).</summary>
    public static long Begin(CaptureKind kind, long causeId, string? a = null, ...) { ... }

    /// <summary>Close a Begin previously returned by <see cref="Begin"/>. A no-op when `beginId == 0`.</summary>
    public static void End(long beginId, long n0 = 0, ...) { ... }

    /// <summary>A one-shot record with no matching End (a click, a re-render, an unprompted dealer push).</summary>
    public static void Point(CaptureKind kind, long causeId, ...) { ... }
}
```

Every field on `CaptureFields` above is a plain parameter, not a closure — so `Capture.Enabled == false` really is
one `volatile bool` load and a `ret`, matching the old file's own `if (!_enabled) return;` shape at every
`RecordInbound`/`RecordPutResponse` call (`DealerArchive.cs:129-157`), which is the one thing about the old design
worth keeping verbatim.

### 2.5 The complete loop, walked through one real click ("Add to queue")

This is the core use case the whole design has to answer for, so it is worth tracing through the ACTUAL 0.3 call
chain once, end to end, naming every id that ties one step to the next — every other cause (Play, transfer, a
remote command) is the same shape with different `CaptureKind`s.

**1 — UI input origin.** A click on a track row's "Add to queue" menu item resolves to `ActionId.AddToQueue` and
reaches `ActionRules.Execute(descriptor, services, in binding)` (`Actions.cs:637`) — the ONE dispatch point (§3.3's
table). `binding.Target`/`target` (an `ActionResolution`, already computed by `Resolve` two lines above) carries
the clicked row's `Uri` — the "target entity uri" the ask requires. **This is where the cause is minted:**

```csharp
long root = Capture.NewRoot(CaptureKind.ActionInvoke, a: descriptor.Id.ToString(), b: target.Target.Uri.Text);
using (Capture.AmbientUiCause(root))   // the §2.2 thread-static, scoped to this synchronous call only
{
    descriptor.Run(services, binding, target);
    Capture.End(root);   // closes the ROOT's own span once Run returns — everything it caused keeps its CauseId
}
```

`descriptor.Id` is the stable `ActionId` enum (`Actions.cs:34`, persisted-safe) — the exact "WaveeActionDescriptor
id" the ask names; `route`/`component` context (which page/pane this fired from) is `services`'s own identity,
already threaded to `Run` by the caller, and rides along as field `C`.

**2 — our handling.** `descriptor.Run` (`Actions.Table.cs:98`, `Run = static (s, _, t) => s.AddToQueue?.Invoke(t.Uri)`)
calls into the playback host, which does two things under the SAME ambient `root`:

- **The optimistic local mutation.** `Queue`'s splice (`Entities/Queue.Rules.cs`) inserts the row; the wrapper
  from §3.3's table emits `Capture.Point(QueueMutation, causeId: root, a: "insert", b: uri, n0: newPosition)` —
  the exact position it landed at, read straight off the splice's own return value, never recomputed.
- **The outbound request.** `DoRemote`'s sibling on the LOCAL side (`Playback.cs:2260-2264`, the `AddToQueue`
  case fires when this arrives as a REMOTE command; the local click's own path sets `fx.QueueAdd`/`Announce`
  directly) leads to `Spotify.Connect.PublishNow` (`Spotify.Connect.cs:656`, already wired in §3.3) sending a
  connect-state PUT whose body carries `PutStateRequest.MessageId` (`s_messageId`, `Spotify.Connect.cs:270`) —
  **this becomes `RootId`'s first correlation id**, captured on the `ConnectStatePut` Begin record as `N0`.

**3 — Spotify's echo, matched explicitly.** The response (§3.3's `ConnectStatePut` End) is a `Cluster`, decoded at
`Spotify.Connect.cs:687-706` into a `Decode.ClusterDelta` that ALREADY carries every field this matching needs,
because `Ownership.Fold`'s own correctness depends on them (C5, per that file's header comment) — nothing new to
decode:

| Correlation id | Where it already lives | What it proves |
|---|---|---|
| `messageId` (ours) | `Spotify.Connect.cs:638` (`++s_messageId`), stamped into the PUT and into `Playback.State.LastCommandMessageId` (`Playback.cs:2182`) | which of OUR commands this echo answers |
| `delta.PutMsgId` | `Decode.ClusterDelta.PutMsgId` (`Spotify.Decode.Connect.cs:137`), set at `Spotify.Connect.cs:692` (`delta.PutMsgId = messageId`) for a same-call-stack response | the response's own claim of which message it answers — compared against the sent `messageId` for an EXACT match (no timing race possible: it's the same HTTP round trip) |
| `delta.QueueRevision` | `Decode.ClusterDelta.QueueRevision` (`Spotify.Decode.Connect.cs:124`) | the server's version stamp for next_tracks — a mismatch (see below) diffs against OUR queue's own `QueueRevision`-equivalent (`Queue.cs`'s own edit sequence) |
| `LastCommandSentByDeviceId` / `LastCommandMessageId` on the WIRE `PutStateRequest`/`PlayerState` (the generated proto fields already exist: `Connect.cs`'s `LastCommandSentByDeviceId`/`LastCommandMessageId`, `Player.cs`'s `SessionCommandId`) | decoded alongside the cluster when the payload is a full `PlayerState`, not just the thin `ClusterDelta` today's decoder reads — **gap**: §3.3's `ConnectStatePut` capture should decode these two EXTRA fields for the capture record even though `Ownership.Fold` itself does not need them, so a REMOTE command's own echo (another controller's AddToQueue) is attributable to that controller's `SessionCommandId`, not just "not us" |
| `delta.ServerTimestampMs` | already read (`Spotify.Connect.cs:696`, `ObserveClusterTimestamp`) | orders our echo against a RACING device's echo (item below) |

A `ConnectStatePutResponse` capture record (a `Point`, `CauseId = the ConnectStatePut's own Id`) is written with:
`latencyMs = Qpc(response) - Qpc(request)` (computed by the wrapper, not a second clock read anywhere else),
`matched = delta.PutMsgId == messageId`, and — the field-level diff the ask requires — a small structural compare
between what the optimistic `QueueMutation` (step 2) recorded as the NEW row/position and what `delta`'s decoded
`next_tracks`/`QueueRevision` says landed server-side: `QueueEchoDiff.Compare(ours, theirs)` (a new pure class,
§6.5) returning an enum (`Exact`, `PositionShifted`, `RowMissing`, `RowsAdded`) rather than a free-text sentence.

**Timeout (no echo).** `Ownership.Tick`'s own `ClaimProtectMs` (5000 ms, `Playback.cs:637`) is already the
mechanism that notices "no verdict arrived" for a CLAIM; the same shape applies to a plain PUT: the `ConnectStatePut`
Begin record that never gets an `End` within a fixed window (the writer's own periodic sweep, §3.6) is emitted as
a `Point(EchoMissing, causeId: the Begin's Id, n0: elapsedMs)` when the segment rolls or on an explicit query — no
new timer thread; it is a read-time inference over already-written Begin records with no matching End, exactly like
the in-app tree view (§5.5) already has to tolerate a "still open" Begin for a session that is still recording.

**Racing echoes from another device.** `Ownership.Fold`'s P2 row (`Playback.cs:698-702`, "a push naming someone
else may predate our claim: remember it, decide at the verdict") is PRECISELY the race the ask names. Every
`Fold` call (whether or not it is this cause's own verdict) is captured as a `Decision<OwnerFx>` (§2.3) tagged with
`isVerdict`/`namesUs`/`namesForeign` (three booleans already computed locally at `Playback.cs:645-647`) — so a
racing push shows up in the story as a SIBLING record under the SAME `RootId` when it arrives while our claim is
`Protected` (P2's own window), and as its OWN new `RemoteRoot` otherwise (it named nobody we asked about).

**4 — the UI outcome.** `queue.panel.rows` (`Entities/Queue.UI.cs:464-486`) ALREADY computes, on every change, the
exact compact snapshot the ask wants (section, position, row key) — it is deliberately not rewritten here, only
tapped: the existing `Log.Info("queue", "queue.panel.rows " + dump)` call gains one more line,
`Capture.Point(UiRerender, causeId: Capture.AmbientOrLastQueueCause, a: dump)`, where `AmbientOrLastQueueCause` is
the most recent `QueueMutation`/`ConnectStatePutResponse`'s cause id the queue host saw (a single `long` field the
host already has room for, since it already owns `_lastDump` for de-duplication, `:462`) — so the panel's dump is
captured TWICE per cause when it changes twice (optimistic, then again if the echo corrects it), each tagged with
which of the two triggered it. The player bar / now-playing card follow the identical pattern at their own existing
"what changed" points (they are not traced line-by-line here — same mechanism, different existing dump site).

**The resulting story**, exactly the shape the ask describes:

```
root #7031  ActionInvoke AddToQueue  target=spotify:track:abc  10:44:02.001
├─ QueueMutation insert pos=3                                   10:44:02.002  (+1ms)
├─ UiRerender queue.panel.rows [...pos 3 has abc...]            10:44:02.004  (+3ms)   ← OPTIMISTIC render
├─ ConnectStatePut AddToQueue msgId=889                         10:44:02.006  (+5ms)
│   └─ ConnectStatePutResponse 200 OK matched=true pos=3 diff=Exact   10:44:02.198  (+197ms)   ← ECHO
└─ UiRerender queue.panel.rows [...pos 3 has abc...]            10:44:02.199  (+198ms)  ← ECHO-CONFIRMED render (unchanged)
```

or, the anomaly case:

```
root #7044  ActionInvoke AddToQueue  target=spotify:track:xyz  10:45:11.400
├─ QueueMutation insert pos=1                                   10:45:11.401
├─ UiRerender queue.panel.rows [...pos 1 has xyz...]            10:45:11.402
├─ ConnectStatePut AddToQueue msgId=902                         10:45:11.405
│   └─ ConnectStatePutResponse 200 OK matched=true pos=4 diff=PositionShifted   10:45:11.610  (+205ms)
├─ UiRerender queue.panel.rows [...pos 4 has xyz...]             10:45:11.611   ← corrected by the echo
└─ ⚠ ANOMALY EchoMismatch: we placed xyz at 1, Spotify echoed it at 4
```

---

## 3. Storage, the writer, and where it lives

### 3.1 File layout

```
%LOCALAPPDATA%\Wavee\logs\capture\
    capture-20260923.idx        one CaptureEvent per fixed-size record (see §6.1) — header fields only
    capture-20260923.blob       payload bytes the idx's PayloadOffset/PayloadLength point into (HTTP/PUT bodies,
                                 dealer frame bytes) — REDACTED before it ever reaches this file (§7.3)
    capture-20260923-2-143059.idx.gz / .blob.gz     a size-rolled segment, gzipped immediately (old design's
                                 SizeRoll, kept — it is the one mechanism the old file got right, §1 row summary)
```

Same root as `wavee-*.log` (`Platform.LogFolder`, `Platform.Host.cs:65`) — never a hardcoded `%LOCALAPPDATA%\Wavee`,
so a packaged run's `LocalCache` redirection is honoured automatically, and the always-on start/stop log line
(§3.4) can print the SAME `FinalPath.Resolve(...)` call `HostOpenLog` already uses (`Platform.Host.cs:126`) so a
user reports one resolved path for both logs and capture.

**Why `.idx` + `.blob` and not one interleaved file (unlike the old `.idx.ndjson` + `.bin` pair, which this keeps
the SHAPE of but not the format):** the idx is fixed-record-length binary (§6.1), not ndjson — a crash mid-write
leaves at most one torn trailing record, detected by its length prefix not fitting before EOF, and the reader skips
it without needing to parse JSON that might itself be torn. The blob is append-only raw bytes exactly like the old
`.bin`; nothing about it changes except that Redaction (§7.3) has already run on anything written into it.

### 3.2 The two lanes (fixing item 5 in §1)

```csharp
sealed class RealtimeCaptureWriter
{
    // Lane 1 — HEADERS. Every CaptureEvent's fixed fields (§6.1), no payload bytes: ~96 bytes each. Bounded at
    // 8 MB pending (DefaultMaxPendingBytes, unchanged from the old constant — it was never the problem) but NEVER
    // silently drops a Begin/End PAIR asymmetrically: an End whose Begin already made it to disk is never dropped
    // on its own (a dangling Begin with no End reads as "still open" — acceptable; an End with no Begin reads as
    // a bug in THIS writer, not a real capture, so it must never happen).
    readonly BoundedQueue<CaptureEvent> _headers = new(capacityBytes: 8L * 1024 * 1024);

    // Lane 2 — PAYLOAD BYTES. Bodies only: HTTP request/response bytes, dealer frame text, PUT bodies. Bounded
    // separately (DefaultMaxPayloadBytes = 32 MB pending) and MAY drop a body while keeping its header (the event
    // still appears in the causal tree with PayloadLength=0 and a `truncated` bit) — exactly the old file's
    // ArrayPool-rent-and-drop shape (`DealerArchive.cs:236-251`), generalized to every kind instead of only dealer
    // frames.
    readonly BoundedQueue<(long eventId, byte[] rented, int length)> _payloads = new(capacityBytes: 32L * 1024 * 1024);
}
```

### 3.3 Payload capture at the six real choke points (fixing item 2 in §1 — read from the actual 0.3 source, not assumed)

| Choke point | File : line | What it wraps |
|---|---|---|
| Dealer inbound | `Spotify.Session.cs:1828` (`Dispatch`), right after `DealerFrame.Parse` at `:1830` | `Capture.Point(DealerFrameIn, causeId: 0 /* mints a RemoteRoot unless it's a known put-response, see below */, a: uri-as-string, payload: utf8)` |
| Dealer outbound | `Spotify.Session.cs:1855` (`SendDealerText`), beside the existing `Wire.NoteSocket("dealer", ...)` call at `:1860` | `Capture.Point(DealerFrameOut, causeId: Capture ambient/threaded, payload: copy)` |
| HTTP (api, spclient, pathfinder, audio cdn, lyrics, update, release-notes — every `HttpClient` this app builds) | `Platform.Wire.cs:97-119` (`LoggingHandler.Send`/`SendAsync`), the ONE handler wrapping every client (`Wire.Handler`, `:93`) | `Begin(HttpCall, causeId from request.Options, a: EndpointOf(...))` at entry, `End(id, n0: status, payload: response bytes when small enough / redacted)` in the existing try/catch that already computes `status`/`ex` |
| Connect-state PUT | `Spotify.Connect.cs:656` (`Api.Send(RequestKind.ConnectStatePut, ...)`, inside `PublishNow`) | `Begin(ConnectStatePut, causeId, a: reason, payload: rented.AsSpan(0, written))` before the send; `End(id, n0: (long)result.Status, payload: result.Body)` at `:658` — and on the OK path, the `Cluster` decode a few lines below (`:687-706`) becomes the response's own `Point(ConnectStatePutResponse-shaped as End)` carrying `delta.PutMsgId`, giving the exact old `RecordPutResponse` value (item 3 in §1) without a bespoke method |
| Every user action | `Actions.cs:637` (`ActionRules.Execute`, the ONE dispatch point every click/shortcut/palette invoke already funnels through, per `Actions.cs`'s own header comment: "a binding and a right-click can never reach two implementations of the same verb") | `long root = Capture.NewRoot(ActionInvoke, a: descriptor.Id.ToString())` before `descriptor.Run(...)`; the ambient UI-thread cause slot (§2.2) is set to `root` for the duration of the synchronous `Run` call |
| Playback transitions | `Playback.cs:1502` (`Step(ref State s, in Input i, ref Effects fx)`, the ONE fold every phase change (`s.Phase = Phase.*`, thirteen sites across `Playback.cs`/`Playback.Transitions.cs` per the grep in this plan's research pass) already goes through) | `Point(PlaybackTransition, causeId: i.CauseId, a: oldPhase, b: newPhase)` wrapping the call, comparing `s.Phase` before/after — a SHELL-side wrapper, not a change to `Step` itself, so the pure fold stays pure and untouched |
| Queue mutations/seeds | `Entities/Queue.cs:355` (`DecideSeed`) and `Entities/Queue.Rules.cs`'s `ReplaceRun`/splice call sites (the "every write in this file follows first" discipline the file's own header describes) | `Point(QueueSeed, ...)` around `DecideSeed`'s call sites in `Playback.Host.Context.cs` (`:856` etc.); `Point(QueueMutation, ...)` around each `ReplaceRun` call, tagged with the section (`QueueSection`) and row count |
| **Every inbound Connect REQUEST** (play/pause/skip/seek/add_to_queue/set_queue/set_shuffle/set_repeat/transfer — the whole `RemoteCmd` family) | `Spotify.Connect.cs:186` (`OnDealer`, the ONE place `message.IsRequest` is true), `:213-226`; decoded at `:216` (`Decode.ConnectCommand`), ROUTED at `Spotify.Connect.Commands.cs:61` (`RouteCommand`), FOLDED at `Playback.cs:2174` (`DoRemote`, the one `switch` over every `RemoteCmd`) | `Begin(RemoteRequest, causeId: 0 /* the wire request is itself a root unless it quotes our own last-sent messageId, in which case it is the CommandAttribution window's cause, `Playback.cs:2178-2185` */, a: c.Kind.ToString(), b: message-ident)` at `OnDealer:213`, closed with `End(id, n0: (long)ok)` right after `Reply(message.Key, ok: true)` at `:222` (the ack we sent); `DoRemote`'s own `switch` (`:2189-2277`) is wrapped ONCE per §2.3 with `Capture.Decision(RemoteRequest, causeId, c.Kind)` plus the `OwnerFx`/`ClaimCause` the branch's own `Ownership.Claim` call already returns — so "what was asked, which handler, which branch, what changed" is FOUR fields on one record, not four hand-written log lines |
| **Transfer, both directions, and every ownership transition** | inbound: `RemoteCmd.Transfer` case, `Playback.cs:2192-2201` (`Ownership.Claim(..., ClaimCause.InboundTransfer, ...)`); outbound (us handing off): `DoTransfer`, `Playback.cs:2282-2288` (`Ownership.Release(ref s.Own, ReleaseCause.TransferAway)`); every other transition: `Ownership.Fold`'s F0-F6/P1-P4/A1-A3 rows, `Playback.cs:643-732` | `Capture.Decision(OwnershipTransition, causeId, verdict: OwnerFx, before: priorOwnerKind, after: s.Own.Kind, reason: ClaimCause-or-ReleaseCause-or-NobodyCause)` wrapping EACH of `Claim`/`Release`/`Fold`/`PutFailed`/`Tick` at their (five) call sites — not inside `Ownership` itself, which stays pure and untouched |
| **Errors: HTTP non-2xx with bodies** | the `ConnectStatePut` End (§ above) and the generic `HttpCall` End at `Platform.Wire.cs:103,115` already have `response.StatusCode`/the exception in hand | `End(id, n0: status, payload: !result.Ok ? redacted-body : small-response-body, truncated: bodyWasLarge)` — a non-2xx is not a special kind, it is an ordinary End whose `n0` is outside 200-299; the query tool's anomaly pass (§5.4) is what flags it, not a separate capture path |
| **Errors: decode failures / handler exceptions** | every `catch (Exception ex)` already logging via `Log.Error`/`Log.Warn` around a decode: `Spotify.Connect.cs:191` (dealer frame unparseable), `:200-204` (cluster decode faulted), `:216-217` (connect command decode faulted), `:720` (put-state response decode faulted) | `Point(DecodeFailed, causeId: the frame's own event id from the DealerFrameIn record, a: ex.GetType().Name, b: which decode)` added BESIDE the existing `Log.Error` call (never replacing it — the prose log stays the human-readable side) |
| **Errors: dropped/ignored frames** | `Spotify.Connect.cs:247-255` (the "everything else … is not an error and is not logged per push" fall-through) and `Ownership.Fold`'s `OwnerFx.DropFrame` rows (F0, item above) | `Point(FrameIgnored, causeId: the frame's event id, a: reason)` where `reason` is a small enum (`CaptureIgnoreReason { Unclassified, StaleServerTime, NotOurClaim }`), not a string — this is the ONE place this plan adds a record where none existed before, because "why was this dropped" is exactly the silent path the ask calls out |
| **Retries / backoff** | `Spotify.Session.cs:536-554` (`ArmApRetry`/`ArmDealerRetry`, already computing `BackoffMs(s.ApAttempt)` and logging attempt number) and `Spotify.Api.cs`'s `Fetch.Retryable`/`Fetch.Failed` path (the batched-request retry ladder) | `Point(Retry, causeId: the failed call's own event id, n0: attemptNumber, n1: delayMs)` beside the existing `Log.Info("spotify", "ap reconnecting in " ...)` calls — `attemptNumber` ties a retry back to EXACTLY the attempt that failed, answering "which attempt belonged to which cause" without inventing a new counter (`s.ApAttempt`/`s.DealerAttempt` already exist) |

`UiRerender` (queue panel rows, the player bar, track-row queued/playing state) is captured at each subsystem's own
EXISTING "what changed, dump it" site (`queue.panel.rows`, §2.5's step 4, is the concrete example) — this plan taps
those sites rather than inventing a render-observation layer, and or writes the same compact snapshot both to the
segment (so `decode.py --story` can print it) and to the in-app tree (§5.5), not "in-app only" as an earlier draft
of this plan assumed before the owner's walkthrough requirement — corrected here.

### 3.4 Lifecycle — a Signal, not a singleton's settable knobs (fixing item 4 in §1)

```csharp
// Platform/Platform.cs — beside the other settings signals this file already declares
public static readonly Signal<bool> DealerArchiveEnabled = new(false);   // renamed in the settings key comment only;
                                                                          // the persisted string diag.dealerArchive
                                                                          // is UNCHANGED — a user's store.json key
                                                                          // never renames (CLAUDE.md: no legacy
                                                                          // renumbering, same rule as LibraryAlbumSort)
```

`RealtimeCaptureHost.OnSettingsChanged()` (new, `Screens/Diagnostics.Host.cs` or a new `Diagnostics.Capture.Host.cs`
— see §8's file split) is called from exactly the same place the Settings row's `Toggle(...)` already calls
`Tray.Host.OnSettingsChanged` for every other live-reacting toggle (`Settings.UI.cs:452-473` shows the pattern) —
`afterWrite: static _ => Diagnostics.Capture.Host.OnSettingsChanged()`. It flips `Capture.Enabled`, and on a
0→1 transition starts the writer thread AND logs the always-on line the task requires:

```
Log.Info("capture", "realtime capture started path=" + Path.Combine(dir, "capture-" + DateStamp(now)));
```

and on 1→0:

```
Log.Info("capture", "realtime capture stopped");
```

exactly mirroring `WriteBatch`'s existing "file sink recovered/failed" lines (`DealerArchive.cs:341,352`) — the one
piece of the old file's logging discipline worth carrying forward unchanged.

### 3.5 Priority (fixing item 5 in §1, restated as code)

```csharp
public enum CapturePriority : byte { Low /* ping/pong, chatty pushes */, Normal /* everything else */ }
```

`CaptureKind → CapturePriority` is a pure lookup (`CaptureRules.PriorityOf(kind, uri)`, tested, §6.3): `Ping`/`Pong`
dealer frames and `hm://collection/*` diffs are `Low`; a `ConnectStatePut` Begin/End pair, its matching Cluster
response, and any `ActionInvoke` root are `Normal` and are the LAST thing the header lane drops under budget
pressure (the queue drop rule prefers dropping the oldest `Low` header before any `Normal` one — a small, pure
`BoundedQueue<T>.TryEnqueue` policy, not a size cliff).

---

## 4. Redaction (fixing item 8 in §1)

```csharp
namespace Wavee.Diagnostics;

/// <summary>PURE. What must never reach a capture payload verbatim: bearer tokens and the one header name that
/// carries them. Mirrors the header Wire.cs's own comment already calls out as sensitive
/// ("the QUERY STRING IS NEVER LOGGED", Platform.Wire.cs:15) — this extends the same discipline to bodies.</summary>
public static class Redactor
{
    /// <summary>Authorization / Cookie / Set-Cookie header VALUES become "[redacted 37B]" — the length is kept
    /// (a debugging session cares whether a token was present and roughly how big, never its value).</summary>
    public static string? RedactHeaderValue(string headerName, string? value) { ... }

    /// <summary>A JSON body's `access_token`/`client_token`/`refresh_token`/`password` string VALUES (not keys) are
    /// redacted the same way, by a single Utf8JsonReader pass — no regex over the raw bytes, no risk of a partial
    /// match inside a larger token corrupting an otherwise-valid capture.</summary>
    public static int RedactJsonBody(ReadOnlySpan<byte> utf8, Span<byte> into) { ... }
}
```

Every `HttpCall`/`ConnectStatePut` payload write in §3.3 goes through `Redactor` before it reaches the payload lane
— the writer thread never sees an unredacted token, so a bug in the writer cannot leak one either.

---

## 5. Decode and query tooling — `ops/tools/capture/`

Python, not another in-process reader (fixing item 9 in §1 — a `ref struct` cannot cross a yield boundary, and
Python has no such restriction; this also matches the repo's existing `ops/tools/` precedent of small standalone
scripts beside the PowerShell tooling, e.g. `perf-ws-attribution.ps1`'s analysis-only role).

```
ops/tools/capture/
    decode.py        idx+blob → time-ordered JSON, one line per event, gzip/base64/redaction-aware
    proto_dump.py     .proto-driven decode for connect-state Cluster / PutStateRequest payloads (imports the
                       repo's own src/apps/Wavee/Spotify/Protos/connect.proto via `protoc --python_out` at run time,
                       or falls back to a hex dump when protoc is not on PATH — never a hard dependency)
    query.py          the asks in the brief:
                         --causes-of <id>        the full causal tree under one event
                         --near <unix_ms> <±s>   everything within N seconds of a timestamp (a UI re-render, a
                                                  queue-panel change reported by the app's own Diagnostics log line)
                         --diff-queue <id1> <id2> the QueueMutation payloads bracketing two event ids, printed as
                                                  a row-by-row before/after (uses the QueueSection tag from §3.3)
                         --story <root-id>        §2.5's exact printout: one root, every descendant in causal +
                                                  time order, each line's own latency from its parent, optimistic
                                                  vs. echo UiRerenders paired and labelled, ⚠ markers inline
                         --story --last N         the same, for the N most recent roots in the segment — the CLI
                                                  form of the in-app "last stories" list below
                         --anomalies              every EchoMismatch / EchoMissing / FrameIgnored / non-2xx /
                                                  DecodeFailed record in the segment, one line each, newest first —
                                                  a bug report's first thing to run
```

`decode.py` reads `.idx` as fixed-length records (§6.1 — a `struct.unpack` format string, documented once at the
top of the file so it and the C# `CaptureRecord` layout cannot silently drift), follows each `PayloadOffset`/
`PayloadLength` into `.blob`, and for a `DealerFrameIn`/`Out`/`HttpCall` payload: gunzips when the record's own
`Gzip` bit says so (mirroring `DealerFrame.Parse`'s own gzip-header sniff, `Spotify.cs:1517`), then hands the
result to `proto_dump.py` when the topic/endpoint says it's a `connect-state` cluster or put-state body, else
prints it as UTF-8 JSON, else a hex dump — the exact fallback ladder the task's brief asks for.

An in-app Diagnostics view is cheap ONLY for the causal tree (§5.5) and IS built here; the three query verbs above
are NOT duplicated in-app — a text tool that reads the same bytes the user's `.zip` bug report already contains is
simpler to get right once than a second implementation living inside the NativeAOT app.

### 5.5 In-app: one new Diagnostics section

`Diagnostics.UI.cs` already has the pattern this reuses verbatim: `Card(title, rows)` (`:201`), a page built from
`PageFrame` (`:187`), a session/level/category log view with a 750 ms poll (`Diagnostics.UI.cs` header comment).
A new `CapturePage` (own file, `Screens/Diagnostics.Capture.UI.cs` — see §8) added beside `RuntimePage`/`ConnectPage`:

```
┌ Diagnostics ▸ Realtime capture ──────────────────────────────────────────────────┐
│  ⦿ Recording — capture-20260923.idx (14.2 MB) · started 10:41:02                  │
│                                                                                    │
│  ┌ Recent causal roots ──────────────────────────────────────────────────────┐   │
│  │ 10:42:07  ActionInvoke   Play              →  6 effects   [ View tree ]    │   │
│  │ 10:42:09  RemoteRoot     hm://connect-state/v1/cluster → 2 effects        │   │
│  │ 10:42:11  ActionInvoke   AddToQueue         →  3 effects   [ View tree ]  │   │
│  └────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                    │
│  [ View tree ] expands, IN PLACE (no navigation — master-detail re-skin, the      │
│  same rule library-rework-implementation.md's §0 table calls out for panes):      │
│                                                                                    │
│  ActionInvoke Play (root #4821, 10:42:07.113)                                     │
│  ├─ PlaybackCommand           10:42:07.114   (+1 ms)                              │
│  ├─ PlaybackTransition Idle→Loading    10:42:07.118   (+5 ms)                     │
│  ├─ ConnectStatePut PlayerStateChanged  10:42:07.140 → 200 OK  (+27 ms)           │
│  │   └─ (Cluster echo — adopted, active=us)                                      │
│  ├─ QueueSeed Context                 10:42:07.141   (+28 ms)                     │
│  └─ PlaybackTransition Loading→Playing 10:42:07.640  (+527 ms)                    │
│                                                                                    │
│  [ Copy path ]   [ Open folder ]     "Decode with: ops/tools/capture/decode.py"  │
└────────────────────────────────────────────────────────────────────────────────────┘
```

The tree view reads the SAME header lane the writer just flushed (`FlushForTests`-shaped `Flush()` first, matching
the old file's `Flush()`/`FlushForTests()` split, §1 item 6's lesson kept: a live in-app view is allowed to touch
disk on an explicit refresh, never per frame) — it is a plain in-process reader over the fixed-record `.idx`
layout (§6.1), no protobuf/redaction decode needed since it only ever shows the HEADER fields (kind, timing,
short `A`/`B` labels), never a raw payload body: bodies stay opaque to the in-app view by design, exactly the
"cheap" the brief asked for, and are the Python tool's job (§5).

**"Recent causal roots" IS the last-N-stories list** the owner's walkthrough asks for — the wireframe above already
shows it; nothing more needs building for that ask specifically. Two additions the story requirement adds to the
SAME page, both still header-only (no payload decode, still cheap):

- Each row grows a coloured dot: green (every descendant's `matched`/`diff==Exact`, no `FrameIgnored`/non-2xx under
  it), amber (an `EchoMismatch` or a non-2xx under it, but the story otherwise completed), red (`EchoMissing` —
  a Begin with no End at all under this root). This is a fold over the header fields already in hand while
  building the tree (§5.5's existing reader), not a new pass.
- A second, small "Anomalies" card ABOVE the roots list, capped at the last 10: one line each, same shape as
  `query.py --anomalies`'s rows, each clickable straight to its root's expanded tree — the in-app answer to "what
  broke recently" without leaving the app, while the exhaustive form (`--anomalies` over the whole segment,
  across gzipped history) stays the Python tool's job.

---

## 6. Pure decisions (no source-text tests — these are the classes the tests in §9 exercise directly)

### 6.1 The record layout (shared contract between the C# writer and `decode.py` — documented once, here)

```csharp
namespace Wavee.Diagnostics;

/// <summary>PURE. The fixed on-disk shape of one .idx record — a `struct.unpack`-able layout, so decode.py never
/// needs a JSON parser for the header lane. 96 bytes, 8-byte aligned. A/B/C are UTF-8, length-prefixed (ushort),
/// truncated to 252 bytes each (a topic/reason/action-id string is never longer than that in practice; a longer
/// one is truncated with a trailing '…' byte, never silently corrupted).</summary>
public readonly record struct CaptureRecord(
    long Seq, long Qpc, long UnixMs, long Id, long CauseId, long RootId,
    CaptureKind Kind, CapturePhase Phase, CapturePriority Priority, bool Truncated,
    long N0, long N1, int PayloadOffset, int PayloadLength,
    ReadOnlyMemory<byte> A, ReadOnlyMemory<byte> B, ReadOnlyMemory<byte> C)
{
    public const int FixedHeaderBytes = 96;   // everything above except A/B/C, which follow inline, length-prefixed
}
```

### 6.2 Rotation (day roll / size roll) — a straight port of the old file's DECISION, not its I/O

```csharp
public static class SegmentRotation
{
    /// <summary>PURE: given the segment's open date, the clock's current date, its current byte length and the
    /// size cap, what should happen. The shell (§3) is the only thing that touches Directory/File.</summary>
    public enum Verdict { Keep, RollForDayChange, RollForSize }

    public static Verdict Decide(DateTime openDate, DateTime now, long currentBytes, long maxLiveBytes)
        => now.Date != openDate ? Verdict.RollForDayChange
         : currentBytes >= maxLiveBytes ? Verdict.RollForSize
         : Verdict.Keep;
}
```

### 6.3 Retention and priority — same shape, extended with `PriorityOf`

```csharp
public static class RetentionPlan
{
    /// <summary>PURE: which already-rolled (gzipped) segments to delete, given their ages, sizes, the retain-days
    /// cutoff and the directory byte cap — oldest first, exactly the old file's `Prune` ordering (`DealerArchive.cs
    /// :579-608`), lifted out of the method that also touched the filesystem.</summary>
    public static IReadOnlyList<int> IndicesToDelete(
        IReadOnlyList<(DateTime WriteUtc, long Bytes)> segments, DateTime nowUtc, int retainDays, long maxDirectoryBytes)
    { ... }
}

public static class CaptureRules
{
    public static CapturePriority PriorityOf(CaptureKind kind, string? uri) => kind switch
    {
        CaptureKind.DealerFrameIn or CaptureKind.DealerFrameOut when uri is null => CapturePriority.Low,   // ping/pong
        CaptureKind.HttpCall when uri is { } u && u.StartsWith("/collection/", StringComparison.Ordinal) => CapturePriority.Low,
        _ => CapturePriority.Normal,
    };
}
```

### 6.4 The echo diff — §2.5's "field-level diff", pure

```csharp
namespace Wavee.Diagnostics;

public enum QueueEchoVerdict : byte { Exact, PositionShifted, RowMissing, RowsAdded }

/// <summary>PURE: compare what our OWN optimistic mutation recorded (a row id + the position we placed it at) against
/// what the server's echoed queue revision says landed. No string diffing — row ids and positions, the same values
/// already captured on the `QueueMutation` and `ConnectStatePutResponse` records (§2.5).</summary>
public static class QueueEchoDiff
{
    public static QueueEchoVerdict Compare(long ourRowId, int ourPosition, ReadOnlySpan<long> echoedRowIds)
    { ... }
}

/// <summary>PURE: why a frame was dropped without acting on it — the `FrameIgnored` record's `reason`, an enum,
/// never a formatted string. Mirrors `Ownership.Fold`'s own F0 stale-guard and the "everything else" fall-through
/// at `Spotify.Connect.cs:247-255`.</summary>
public enum CaptureIgnoreReason { Unclassified, StaleServerTime, NotOurClaim, TruncatedPayload }
```

### 6.5 Id propagation rules — the part most worth testing in isolation

```csharp
public static class CausalityRules
{
    /// <summary>PURE: given an ambient/threaded cause id and whether this event is itself the start of a NEW
    /// causal chain (a user input, or an inbound frame recognized as unprompted), what Id/CauseId/RootId a new
    /// CaptureEvent gets. This is the one function every §2.2 call site actually calls; keeping it pure means the
    /// "remote push with no known parent gets its own root" rule and the "an End inherits its Begin's RootId" rule
    /// are each one assertion in a test, not something only observable by writing a whole segment and reading it
    /// back.</summary>
    public static (long Id, long CauseId, long RootId) NewEvent(long nextSeq, long threadedCauseId, IdLookup lookup);
}
```

---

## 7. Tests (`src/apps/Wavee.Tests/`, new files — no source-text reads, per class above)

- `SegmentRotationTests.cs` — `SegmentRotation.Decide` across day/size/neither, boundary bytes exactly at the cap.
- `RetentionPlanTests.cs` — port of the old `Prune_DeletesOldestGzipFirst_ByAgeAndDirectoryCap` fixture data
  (`DealerArchiveTests.cs:213-241`) against the new pure `RetentionPlan.IndicesToDelete`.
- `CaptureRulesTests.cs` — `PriorityOf` for every `CaptureKind`/uri shape in the table in §3.5 and §6.3.
- `CausalityRulesTests.cs` — the propagation table: a user input mints a fresh root; an action under it inherits
  root+cause; a remote push with `threadedCauseId == 0` mints its OWN root (never inherits the previous ambient
  value — the pool-thread-leak guard from §2.2, made assertable).
- `RedactorTests.cs` — `RedactHeaderValue("Authorization", "Bearer abcd…")` → `"[redacted 41B]"`; a JSON body with
  `access_token` redacted, its sibling fields untouched, byte-length preserved for the caller's buffer sizing.
- `QueueEchoDiffTests.cs` — `Exact` when the echoed row sits at the recorded position; `PositionShifted` when it
  moved; `RowMissing`/`RowsAdded` for the two asymmetric cases — table-driven against small `long[]` fixtures, no
  disk, no dealer types.
- `CaptureDecisionTests.cs` — `Capture.Decision<TVerdict>` against a fake sink: a boxed `OwnerFx`/`ClaimCause`
  round-trips to the exact enum value and name the writer would format, and — the zero-cost assertion the owner's
  brief specifically asks to keep true — `Capture.Enabled == false` allocates nothing (an `[Fact]` wrapped in a
  before/after `GC.GetAllocatedBytesForCurrentThread()` delta, the same style `ReuseGuard`-adjacent engine tests
  already use for a zero-alloc claim).
- `CaptureRecordCodecTests.cs` — round-trip encode/decode of `CaptureRecord` against the exact byte layout §6.1
  documents (this is the test that keeps `decode.py`'s `struct.unpack` format string honest — a comment in both
  files cross-references the other).

None of these read production source as text; each instantiates the pure class directly, per CLAUDE.md.

---

## 8. File-by-file partition for parallel agents

Six roughly disjoint units; the orchestrator alone builds/tests (CLAUDE.md, and `library-rework-implementation.md`'s
own precedent):

1. **Core record + pure rules** — new `src/apps/Wavee/Diagnostics/Capture.cs` (record shapes, `CaptureKind`/`Phase`/
   `Priority`, `CausalityRules`, `CaptureRules`, `Capture.Decision<TVerdict>` §2.3) + `Capture.Rotation.cs`
   (`SegmentRotation`, `RetentionPlan`) + `Capture.EchoDiff.cs` (`QueueEchoDiff`, `CaptureIgnoreReason`, §6.4). No
   dependency on anything else in this list; everyone else depends on this.
2. **Writer + host lifecycle** — new `Diagnostics/Capture.Host.cs` (the two-lane `RealtimeCaptureWriter`, segment
   open/close/gzip, the `Signal<bool>` wiring, the start/stop log lines) — mirrors `Platform.Host.cs`'s `Log` shell
   exactly enough that its author should skim `Platform.Host.cs:611-750` first.
3. **Redaction** — new `Diagnostics/Capture.Redact.cs` (`Redactor`) — no dependency on (2); (2) depends on it.
4. **Call-site wiring, transport half** — `Spotify.Session.cs` (dealer in/out, §3.3), `Platform.Wire.cs` (HTTP),
   `Spotify.Connect.cs` (the PUT Begin/End AND the `OnDealer`/`RouteCommand` inbound-request Begin/End + the
   `FrameIgnored`/`DecodeFailed` points, §3.3's new rows), `Spotify.Session.cs`'s `ArmApRetry`/`ArmDealerRetry`
   (the `Retry` points). One author: these five all live in `Spotify.*` and share the request/response vocabulary.
5. **Call-site wiring, playback half** — `Actions.cs` (action root, §2.5 step 1), `Playback.cs` (the `DoRemote`
   switch wrapper + the FIVE `Ownership.Claim`/`Release`/`Fold`/`PutFailed`/`Tick` call sites, all as
   `Capture.Decision` per §2.3/§3.3 — NOT inside `Ownership` itself, which stays untouched), `Queue.cs`/
   `Queue.Rules.cs` (seed/mutation), `Entities/Queue.UI.cs`'s `queue.panel.rows` tap (§2.5 step 4). One author:
   these all live in `Playback.*`/`Entities/Queue.*` and share the `OwnerState`/`ClaimCause` vocabulary. Independent
   of unit 4 (different files, different subsystems) but both depend on unit 1.
6. **Settings row + in-app Diagnostics page** — `Settings.UI.cs`'s existing `DealerArchive` row (`:486-487`, keep
   the persisted key and subtitle string, just rewrite what it wires to) + new `Screens/Diagnostics.Capture.UI.cs`
   (§5.5's page: the causal tree, the coloured-dot roll-up, the Anomalies card).
7. **ops/tools/capture/ + tests** — the Python decode/query tool (§5, including `--story`/`--anomalies`) and every
   file in §7. Independent of everything except the record layout comment in unit 1 (read-only dependency: the
   byte layout, not the code).

Suggested order: 1 → (2, 3, 4, 5, 7 in parallel) → 6 (needs 2's `Signal` and 4/5's wiring in place to be meaningful
to click through) → one Debug + Release build and one `Wavee.Tests` run by the orchestrator.

---

## 9. CHANGELOG / issue

No GitHub issue exists yet for this — the `github-triage` skill should file one before implementation starts (every
fix needs a real `#n`, CLAUDE.md). Until then, the `## [Unreleased]` section's `### Added` gets:

```
- **Settings ▸ Developer ▸ "Archive Spotify realtime traffic" now captures a causal flight recorder**, not just
  dealer frames: user actions, playback transitions, queue mutations, outbound HTTP/connect-state PUTs and dealer
  frames all land in one time-ordered, bounded (2 GB / 90 days), redacted capture under `logs/capture/`, decodable
  with `ops/tools/capture/decode.py`. Off by default; zero cost when off. (#TBD)
```
