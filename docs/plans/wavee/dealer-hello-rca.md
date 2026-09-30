# Dealer hello RCA — a session stuck on "Connecting…" (2026-09-30)

Status: **RCA complete; fix implemented as §8 records (without the optional §6.2 fresh bearer and §6.7 card); build,
tests, issue and CHANGELOG owed by the orchestrator.** Read-only investigation of the stuck session
`sid=fd1d6d47 pid=57320` against the working launch `sid=2aed38e6 pid=46296` two and a half minutes earlier, and the
earlier NativeAOT session `sid=56ac430d` (four dealer sockets, all fine). Sources: `%LOCALAPPDATA%\Wavee\logs\wavee-20260930.log`,
the realtime capture `logs\capture\capture-20260930.idx/.blob` (read through `ops/tools/capture/layout.py`
`load_segment`, no decode), the app source at HEAD (`Spotify.cs`, `Spotify.Session.cs`, `Spotify.Library.cs`,
`Diagnostics/Capture*.cs`, `Shell/Shell.Host.cs`, `Shell/Shell.cs`), librespot + librespot-java under `C:\WAVEE`, the
09-19 fresh-client captures, and the installed `xpui.spa`.

## 1. Verdict in one paragraph

The dealer websocket in the stuck session was **open, alive and answering** — twenty `{"type":"ping"}` /
`{"type":"pong"}` pairs, one every 30 s, pong 30–80 ms after each ping, for the whole ten minutes captured — and **it
never carried the pusher hello** (`hm://pusher/v1/connections/<id>` with the `Spotify-Connection-Id` header). Wavee has no
deadline for that hello: `Session.Dealer` stays `LinkPhase.Opening`, the phase stays `Minting`, the chrome folds
`Minting` to "Connecting…" (`Shell.cs:1540`), `Fetch.CanSend` holds every Spotify bucket (`Spotify.Library.cs:110`) — zero
`fetch.send` for the session, the album track list never hydrates, the player bar's artist line never resolves — and the
one watchdog that exists (`DealerDeadAfterMs = 70 s`, `Spotify.Session.cs:1695`) cannot fire because the pongs keep
refreshing `s_lastDealerTick`. The owner's hypothesis — the hello arrived and a race in our code lost it — is **ruled
out** at the process boundary: the only socket reader is the dealer thread's single `ReceiveAsync` (§4 H1), every frame
that reaches it is captured before any branch (§4 H2), the capture was live from −1974 ms and reported no drop, and a
hello that reached `Dispatch` could not have escaped **both** the capture and the log (§4 H3). What cannot be seen from
inside the process is the wire itself; a frame lost between the 101 upgrade and the first `ReceiveAsync` inside the .NET
websocket stack has no known defect behind it and no evidence for it, and the fix below covers it identically.

Root cause (confidence: **high** that the hello never entered the process; **medium-high** that the server never sent
it): a dealer node accepted the socket and served keepalives without registering the connection with the pusher, and
**Wavee treats "socket open" and "registered" as the same thing with no bound on the gap** (`LinkPhase.Opening` is
documented as "the wss handshake up to the pusher's hello", `Spotify.cs:98-100`, and nothing bounds it).

## 2. The two sessions

| | working `2aed38e6` | stuck `fd1d6d47` |
|---|---|---|
| binary | Debug JIT, `bin\Debug\net10.0` (dll 19:53), runtime 10.0.8 | NativeAOT Release arm64 publish (exe **19:58**, first launch of it), runtime 10.0.5 |
| dealer code | HEAD (`Spotify.Session.cs`, `Spotify.cs`, `Diagnostics/` unmodified in the working tree) | same |
| `logged in` (ap epoch 1) | t=…039149 | t=…182887 |
| `client-token minted` / `access token minted` | +198 / +415 ms | +149 / +424 ms |
| `dealer connected (gew4-dealer.g2.spotify.com, epoch 1)` | t=**1790791039834** (tid 36) | t=**1790791183471** (tid 9) |
| hello (capture `DealerFrameIn`, label `hm://pusher/v1/connections/…`) | **+8 ms** (seq 21, 512 B, Normal) | **never** (0 of 20 inbound frames) |
| `put-state NewDevice` | +242 ms | never |
| `fetch.send` | 93 | **0** |
| `session.stale` / `dealer dropped` / `dealer.exit` / `half-open` / `dealer reconnecting` | none | **none** |
| pings out / pongs in | 3 / 3 (30 s cadence) | 20 / 20 through +600 s (30 s cadence, pong ≤ 80 ms after ping) |
| `server-clock synced` (HTTPS `/melody/v1/time` with the bearer, from the keepalive thread) | +30.3 s | +30.4 s — the bearer works over HTTPS |
| capture live before the dealer opened | 20 records from −1401 ms | 8 records from −1974 ms (apresolve, clienttoken, login5) |
| `[capture] capture dropped header records` | none | none |

Earlier today, `56ac430d` (an older NativeAOT publish, same dealer source — the file last changed 2026-09-25) opened four
dealer sockets (boot + three server-side closes, "the dealer closed the socket") and every one got its hello: +99 ms,
+104 ms, +1.67 s, +126 ms. So the NativeAOT arm is not systematically broken, and the hello normally lands within 2 s.

### 2.1 Stuck session, everything the dealer socket carried (capture, ms relative to `dealer connected`)

```
 +30000 DealerFrameOut  ping   15 B      +30034 DealerFrameIn  pong  15 B
 +60457 DealerFrameOut  ping             +60483 DealerFrameIn  pong
 +90463 …                                +90504 …
   … every 30 s, 20 pairs, last pair +600586 / +600637 …
```

No `Retry`, no `ConnectStatePut`, no `RemoteRequest`, no labelled `DealerFrameIn` at any time. The two `UiRerender` and
twelve `HttpCall` records in the window are the boot's apresolve/clienttoken/login5 (`bodyOmitted=auth`) and the clock
probes.

## 3. The path from the socket to Online (what has to happen, and where)

1. `Apply(AccessTokenMinted)` → `Step` (`Spotify.cs:453-463`): `LoggedIn && Dealer == Down` → `Dealer = Opening`,
   effect `OpenDealer` → `StartDealer(s.Epoch)` (`Spotify.Session.cs:285, 521-530`) → thread `wavee-spotify-dealer`.
2. `DealerLoopCore` (`Spotify.Session.cs:1740-1805`): `AccessToken(false)` → `new ClientWebSocket()` (default options)
   → `ConnectAsync("wss://<host>/?access_token=…")` → `s_lastDealerTick = now` → **`dealer connected`** →
   `StartKeepalive(ws, ct)` (a thread that only **sends**: `KeepaliveTicks` `:2000-2019`) → `Receive(...)`.
3. `Receive` (`:1807-1826`): the **only** `ReceiveAsync` in the app (`:1816`), synchronous, one frame at a time →
   `Dispatch(utf8, scratch, epoch)`.
4. `Dispatch` (`:1832-1870`): `DealerFrame.Parse` → **capture point for every frame** (`:1841-1850`, before the
   `switch`) → ping answered / pong+unknown return → hello (`!ConnectionId.IsEmpty && Uri starts with hm://pusher/`,
   `:1861`) → `Publish(DealerOnline, Text, Epoch)` → else `Connect.OnDealer`.
5. `Publish` = `Post(() => Apply(e))` (`:237`); `Post` is the shell's UI-thread marshaller (`Shell.Host.cs:358-370`:
   queued until the root attaches, then delivered in order — never dropped).
6. `Apply` (`:255-289`): `IsStale` → `session.stale …` log line, else `Step(DealerOnline)` (`Spotify.cs:465-485`):
   `ConnectionId`, `Dealer = Up`, `LoggedIn ? Phase = Online`, effect `AnnounceDevice` → `Connect.AnnounceDevice()`
   → `put-state NewDevice`. `Status.Value = Online` → `Spotify.Library.WatchSession` (`Spotify.Library.cs:195-208`) pumps
   the planner; `Playback.Host.Autoplay.WatchOnline` posts `SessionOnline`.

Every step from 2 onward left a trace in the working session (hello captured, put-state logged, 93 sends) and steps 4–6
left **nothing** in the stuck one, while step 3 demonstrably kept running (the pongs).

## 4. Hypotheses

### H1 — something consumed the hello before `Receive`/`Dispatch` — **RULED OUT**

- One `ReceiveAsync` call site in the whole app: `Spotify.Session.cs:1816` (grep `ReceiveAsync` over `src/apps/Wavee`:
  one hit). `s_dealer` is read by `CloseDealerSocket` (`:1707`) and `SendDealerText` (`:1956`) only — both write/abort,
  neither reads.
- The keepalive thread (`KeepaliveLoop`/`KeepaliveTicks`, `:1994-2019`) waits on `ct.WaitHandle`, sends a ping, probes the
  clock over HTTPS. It never touches the receive side.
- `ClientWebSocket` refuses a second outstanding receive (`InvalidOperationException`), so a concurrent reader would have
  thrown and dropped the dealer — no such line.
- Order `ConnectAsync → log → StartKeepalive → Receive` cannot lose a frame: bytes that arrive before the first
  `ReceiveAsync` sit in the TCP/websocket buffer; nothing else drains it. The working session's hello at **+8 ms** — i.e.
  almost certainly already buffered when `Receive` started — was received fine.
- The 20 pongs prove the same `Receive` loop was reading that very socket for the entire session.

### H2 — the capture missed the hello — **RULED OUT**

- `Capture.Point(DealerFrameIn, …)` runs for **every** frame before the `switch` (`Spotify.Session.cs:1841-1850`); a
  frame parsed as `Unknown`, or a pusher frame with an empty id, is still captured with label `"(unknown)"` or its uri
  (`DealerCaptureLabel`, `:1876-1881`, never null for a non-ping/pong). No such record exists.
- `Capture.Enabled` was on: `[capture] realtime capture started` at −2322 ms; `SetEnabled(true)` precedes that line
  (`Capture.Host.cs:584-588`); eight records landed before the dealer connected (first at −1974 ms).
- Lane policy: a labelled frame is `Normal` priority (`CaptureRules.PriorityOf`, `Capture.cs:114-119`); only `Low`
  (ping/pong, `/collection/`) is evicted first, and a header drop is always logged (`Capture.Host.cs:337-340`) — no
  `capture dropped` line in the session. The `Low` pongs were kept, so a `Normal` hello could not have been dropped.
- `DealerFrame.Parse` (`Spotify.cs:1491-1535`) cannot hide a frame: a malformed frame returns `Unknown` (still captured);
  it reads `Spotify-Connection-Id` in both casings (`:1550`); a `JsonException` is caught, any other exception would
  propagate out of `Dispatch` → `Receive` → `DealerLoopCore`'s `catch (Exception)` → `dealer dropped` — absent. The
  working session's hello, same code, parsed and folded.
- A new hello shape (id only in the uri, no header) **would** be a real gap — `Dispatch` would route it to
  `Connect.OnDealer` and never publish `DealerOnline` — but it would still have been captured with the pusher uri as its
  label. It was not. (Worth a `dealer.frame` log line anyway, §6.4.)

### H3 — the event was published but refused (stale / queue / LoggedIn ordering) — **RULED OUT**

- A refused hello logs `session.stale kind=DealerOnline …` (`Spotify.Session.cs:258-263`); no such line.
- The UI post pump was working: `catalog scope adopted` (seq 91, tid 13) is `AdoptWelcome` inside `Apply(Welcome)` posted
  from the AP thread (tid 23); `AccessTokenMinted` was folded on the same path (it is what started the dealer thread).
  `Post` queues, never drops (`Shell.Host.cs:358-368`). The window loop kept waking every 2 s (`[wake] … sole: timer`).
- Ordering: the dealer opens **only** when `LoggedIn` (`Spotify.cs:461`), and `LoggedIn` is cleared only by
  `EndTransports` (`:581-589`), which also bumps `Epoch` — so a hello from the pre-clear socket is stale, and a hello
  that is folded always finds `LoggedIn == true`. There is no state in which the id is stored and Online is never promoted.
  The 401 re-mint path (`AccessToken(force: true)`, `:1523-1541`) publishes a second `AccessTokenMinted`; the fold
  ignores it while `Dealer != Down` (`Spotify.cs:461`) — seen live in `56ac430d` (two `access token minted` at boot,
  one dealer). No fix needed here; a fact pinning the invariant is cheap (§6.6).

### H4 — two dealer threads / sockets — **RULED OUT**

- One `dealer connected` line, no `dealer dropped`, no `dealer.exit`, no `dealer reconnecting`; the `forceToken` retry
  `continue` (`:1792-1798`) is reached only after a `dealer dropped` warn. `StartDealer` is guarded by
  `s_dealerRunning` (`:525`); `StartKeepalive` by `s_keepaliveRunning` (`:1989`). The pings all came from one thread
  (tid 24) on one socket at one cadence.

### H5 — the server sent no hello on this socket — **CONFIRMED by elimination at the process boundary**

- Everything the process received on the socket is enumerated in §2.1: 20 pongs, nothing else, for 600 s.
- The socket was not half-open: pongs answered within 80 ms every time; the server-side keepalive layer was alive.
- The bearer was good: it upgraded the websocket, and the same bearer served `/melody/v1/time` over HTTPS at +30 s.
- What the reference clients do when the hello is missing:
  - **librespot** (`core/src/dealer/mod.rs`, `connect/src/spirc.rs:475-478`): no hello deadline — `spirc` awaits
    `connection_id_update` forever; its liveness rule is stricter than ours (`PING_TIMEOUT` 3 s after each ping).
  - **librespot-java** (`dealer/DealerClient.java:341-363`): pong within 3 s or reconnect; no hello deadline.
  - **official client**: the dealer lives in native code — the installed `xpui.spa` (Aug 18 build, every entry ≥ 50 KB
    scanned) carries neither `Spotify-Connection-Id` nor `hm://pusher`; the 09-19 captures only show that its connection
    id is live ~0.4 s after start (`findings-session-connect.md:180`). Its rule for a missing hello is unknowable from here.
  - So the deadline is Wavee's own rule, sized from observed hello latency: 8 ms, ~100 ms ×3, 1.67 s across today's
    five good sockets — **10 s** is five times the slowest and a third of the first keepalive.
- Unprovable alternative: the frame was lost inside the .NET websocket stack between the 101 response and the first
  `ReceiveAsync` (a pipelined first frame in the upgrade's read-ahead buffer). `SocketsHttpHandler`'s upgrade stream
  hands leftover buffered bytes to the websocket first; no known defect, no evidence, and the same fix covers it.

## 5. Other findings in this startup path

1. **No bound on `LinkPhase.Opening` for the dealer** — the defect itself. The AP side has a read timeout and the login
   ladder; the dealer has a half-open watchdog for *liveness* (`DealerDeadAfterMs`) but nothing for *registration*.
2. **The phase transition into Online is not logged.** `Apply` writes `Status.Value` silently; `put-state NewDevice` is
   the only proxy. The stuck log had no line saying "phase never left Minting". §6.5 adds one line per transition.
3. **No fold test covers `DealerDropped` while `Dealer == Opening`** (`SpotifySessionTests.cs` tests drops from
   `Online()`). By inspection the fold does the right thing (`Spotify.cs:503-514`: `Epoch++`, `Waiting`, `Reconnecting`,
   `ConnectionId = default`, `CloseDealer | DealerBackoff`); the new deadline path relies on exactly that, so it gets a fact.
4. **Double `login5` at boot when playback hydrates before Online** (`56ac430d` seq 109-113: an api thread's
   `MintAccessToken` 85 ms before the AP thread's forced one, `Spotify.Session.cs:1313`). Harmless for the fold
   (`Dealer != Down` guard) and absent in both sessions studied; noted, not in scope.
5. The Diagnostics page shows nothing about the dealer link (`Spotify.ConnectionId()` is read only by the headless probe,
   `Diagnostics.Probe.cs:467`). Optional §6.7.

## 6. Fix plan

Principle: the dealer thread owns the deadline (it already owns the socket and its drop path); the fold is untouched
except for a distinguishable fault; the decision is a pure rule with its own tests; three always-on log lines make the
next occurrence self-diagnosing. Every step below reuses the existing drop → backoff → retry → hello machinery
(`DealerDropped` → `CloseDealer | DealerBackoff` → `DealerRetry` → `OpenDealer` → `DealerOnline` → `AnnounceDevice`).

### 6.1 The pure rule — new file `src/apps/Wavee/Spotify/Spotify.Dealer.Rules.cs`

```csharp
namespace Wavee;

public static partial class Spotify
{
    /// <summary>The pusher hello deadline. A dealer socket is "open" when the wss handshake completes and "registered"
    /// when the pusher's hello (`hm://pusher/v1/connections/<id>` + `Spotify-Connection-Id`) arrives; only the second
    /// makes the session Online (`Step`'s DealerOnline case). 2026-09-30 (`docs/plans/wavee/dealer-hello-rca.md`): a
    /// node accepted the socket, answered every keepalive for ten minutes and never sent the hello, and nothing bounded
    /// `LinkPhase.Opening`. Observed hello latency on good sockets is 8 ms – 1.7 s; the deadline is five times the
    /// slowest and a third of the first keepalive. PURE — no clock, no socket.</summary>
    public static class DealerHelloRules
    {
        public const int HelloDeadlineMs = 10_000;

        public enum HelloVerdict : byte { Waiting = 0, Held = 1, Overdue = 2 }

        public static HelloVerdict Verdict(bool helloHeld, long connectedAtMs, long nowMs)
            => helloHeld ? HelloVerdict.Held
             : nowMs - connectedAtMs >= HelloDeadlineMs ? HelloVerdict.Overdue
             : HelloVerdict.Waiting;

        /// <summary>How long ONE receive may block: the remaining hello budget until the hello is held, unbounded
        /// (<see cref="Timeout.Infinite"/>) afterwards, 0 when the deadline has already passed.</summary>
        public static int ReceiveBudgetMs(bool helloHeld, long connectedAtMs, long nowMs)
        {
            if (helloHeld) return Timeout.Infinite;
            long left = HelloDeadlineMs - (nowMs - connectedAtMs);
            return left <= 0 ? 0 : (int)Math.Min(left, int.MaxValue);
        }

        /// <summary>The ONE rule `Dispatch` applies to recognise the hello, named so a test can pin it against the
        /// captured wire shape: a MESSAGE on a `hm://pusher/` topic that carries a connection id.</summary>
        public static bool IsHello(DealerFrameKind kind, ReadOnlySpan<byte> uri, ReadOnlySpan<byte> connectionId)
            => kind is DealerFrameKind.Message or DealerFrameKind.Request
            && !connectionId.IsEmpty
            && uri.StartsWith("hm://pusher/"u8);
    }

    /// <summary>The receive loop's word for "the deadline passed with no hello": an <see cref="IOException"/>, so
    /// <c>DealerLoopCore</c>'s existing drop path handles it, distinguishable so the fold gets
    /// <see cref="SessionFault.Protocol"/> and the exit line says <c>hello-overdue</c>.</summary>
    public sealed class DealerHelloOverdueException(int afterMs, int frames)
        : IOException("no pusher hello within " + afterMs + " ms (frames received: " + frames + ")")
    {
        public int AfterMs { get; } = afterMs;
        public int Frames { get; } = frames;
    }
}
```

### 6.2 `Receive` enforces the deadline — `src/apps/Wavee/Spotify/Spotify.Session.cs:1807-1826`

```csharp
/// <summary>How many of a socket's first frames are logged one line each (`dealer.frame`): enough to see the hello,
/// or to see what came instead of it.</summary>
const int DealerFirstFramesLogged = 3;

static void Receive(ClientWebSocket ws, MemoryStream frame, byte[] receive, byte[] scratch, uint epoch, CancellationToken ct)
{
    var segment = new ArraySegment<byte>(receive);
    long connectedAt = Environment.TickCount64;
    bool helloHeld = false;
    int frames = 0;
    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        frame.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            int budget = DealerHelloRules.ReceiveBudgetMs(helloHeld, connectedAt, Environment.TickCount64);
            result = ReceiveWithin(ws, segment, budget, frames, connectedAt, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            frame.Write(receive, 0, result.Count);
        }
        while (!result.EndOfMessage);

        Volatile.Write(ref s_lastDealerTick, Environment.TickCount64);   // any frame means the link is alive
        frames++;
        long afterMs = Environment.TickCount64 - connectedAt;
        var utf8 = frame.GetBuffer().AsSpan(0, (int)frame.Length);
        if (Dispatch(utf8, scratch, epoch, frames, afterMs)) helloHeld = true;
    }
}

/// <summary>One receive under the hello budget. Cancelling a receive aborts the <see cref="ClientWebSocket"/>, which is
/// exactly what the overdue path wants: the socket is dropped and the dealer's own backoff reconnects (B4). Only this
/// epoch's own cancellation stays a quiet exit; the budget's cancellation is a drop with a name.</summary>
static WebSocketReceiveResult ReceiveWithin(ClientWebSocket ws, ArraySegment<byte> segment, int budgetMs,
    int frames, long connectedAt, CancellationToken ct)
{
    if (budgetMs == Timeout.Infinite) return ws.ReceiveAsync(segment, ct).GetAwaiter().GetResult();
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(budgetMs);
    try { return ws.ReceiveAsync(segment, deadline.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        int afterMs = (int)(Environment.TickCount64 - connectedAt);
        Log.Warn("spotify", "dealer.hello overdue afterMs=" + afterMs + " frames=" + frames
            + " — the socket is open but unregistered; dropping it for a reconnect");
        throw new DealerHelloOverdueException(afterMs, frames);
    }
}
```

`DealerLoopCore`'s catch (`:1788-1803`) names the fault and the exit:

```csharp
catch (Exception ex)
{
    Log.Warn("spotify", "dealer dropped (epoch " + epoch + ")", ex);
    CloseDealerSocket();
    if (forceToken && !retried) { retried = true; continue; }
    bool overdue = ex is DealerHelloOverdueException;
    Publish(new SessionEvent(SessionEventKind.DealerDropped,
        Number: (long)(overdue ? SessionFault.Protocol : SessionFault.Network), Epoch: epoch));
    return overdue ? "hello-overdue" : "dropped";
}
```

The fold already answers `DealerDropped` while `Opening` with `CloseDealer | DealerBackoff` (`Spotify.cs:503-514`): the
retry ladder 3/6/12/24/30 s (`BackoffMs`) re-opens with the cached bearer; `DealerAttempt` climbs per overdue socket and
resets on the first hello. The chrome shows the Reconnect form instead of "Connecting…" forever (`FoldAuth`:
`Reconnecting` is still `AuthState.Connecting`, so the chip stays the caption until the hello lands — acceptable; the
Fault signal reads `Protocol`).

Optional, **not** evidence-backed (leave out unless the new log lines show repeated overdue sockets on the same bearer):
force a fresh bearer on the second consecutive overdue — at the top of `DealerLoopCore`,
`bool forceToken = Current.Fault == SessionFault.Protocol && Current.DealerAttempt >= 2;`.

### 6.3 `Dispatch` returns "was the hello" and logs the first frames — `Spotify.Session.cs:1832-1870`

```csharp
static bool Dispatch(ReadOnlySpan<byte> utf8, byte[] scratch, uint epoch, int frameIndex, long afterMs)
{
    var message = DealerFrame.Parse(utf8, scratch);
    if (frameIndex <= DealerFirstFramesLogged)
        Log.Info("spotify", "dealer.frame n=" + frameIndex + " kind=" + message.Kind + " label="
            + (message.Kind is DealerFrameKind.Ping or DealerFrameKind.Pong ? message.Kind.ToString() : DealerCaptureLabel(in message))
            + " bytes=" + utf8.Length + " afterMs=" + afterMs + " epoch=" + epoch);
    if (Capture.Enabled) { /* unchanged */ }
    switch (message.Kind)
    {
        case DealerFrameKind.Ping: SendDealerText(DealerPong); return false;
        case DealerFrameKind.Pong:
        case DealerFrameKind.Unknown: return false;
    }
    if (DealerHelloRules.IsHello(message.Kind, message.Uri, message.ConnectionId))
    {
        Log.Info("spotify", "dealer.hello id=" + Platform.Redact(Encoding.UTF8.GetString(message.ConnectionId))
            + " afterMs=" + afterMs + " epoch=" + epoch);
        Publish(new SessionEvent(SessionEventKind.DealerOnline, Text: Text.Add(message.ConnectionId), Epoch: epoch));
        return true;
    }
    Connect.OnDealer(utf8);
    return false;
}
```

`Platform.Redact` is the same redactor the `logged in (31u***tq …)` line uses; the id is never logged whole. The
`dealer.frame` label for a pusher frame is its topic, so a hello that arrives in a NEW shape (id in the uri, no header —
the H2 gap) shows up as `kind=Message label=hm://pusher/v1/connections/…` followed by no `dealer.hello`, which is the
exact symptom a reader needs.

### 6.4 The phase line — `Spotify.Session.cs:255-289` (`Apply`)

```csharp
SessionPhase phaseBefore = s.Phase;
LinkPhase dealerBefore = s.Dealer, apBefore = s.Ap;
SessionEffects fx = Step(ref s, e);
Volatile.Write(ref s_box, new Box(s));
if (phaseBefore != s.Phase || dealerBefore != s.Dealer || apBefore != s.Ap)
    Log.Info("spotify", "session.phase " + phaseBefore + "→" + s.Phase + " ap " + apBefore + "→" + s.Ap
        + " dealer " + dealerBefore + "→" + s.Dealer + " on " + e.Kind + " epoch=" + s.Epoch + " fault=" + s.Fault);
```

A handful of lines per session (every transition), string-building only on a transition. With it the stuck log would
have read `session.phase Authenticating→Minting … on Welcome`, then `… dealer Down→Opening on AccessTokenMinted`, then
nothing — and now `session.phase Minting→Reconnecting … dealer Opening→Waiting on DealerDropped … fault=Protocol` at +10 s.

### 6.5 Doc fix — `Spotify.cs:98-100` (`LinkPhase.Opening`)

Append: "The dealer's `Opening` is bounded by `DealerHelloRules.HelloDeadlineMs`: a socket that is open but never
registered (no pusher hello) is dropped by its own thread and climbs the dealer ladder like any drop."

### 6.6 Tests

**New `src/apps/Wavee.Tests/DealerHelloRulesTests.cs`** (pure, engine-free, no source-text):

- `Waiting_before_the_deadline`, `Overdue_at_and_after_the_deadline`, `Held_once_the_hello_is_seen_whatever_the_clock`.
- `Budget_is_the_remaining_deadline_then_zero_then_unbounded_once_held` (`ReceiveBudgetMs` at 0 ms, 9 999 ms, 10 000 ms,
  held).
- `IsHello_pins_the_captured_wire_shape`: parse the working session's hello shape (redacted id) with
  `DealerFrame.Parse` — `{"headers":{"Spotify-Connection-Id":"…"},"method":"PUT","type":"message","uri":"hm://pusher/v1/connections/…"}`
  — and assert `IsHello` true; a playlist push (`hm://playlist/v2/playlist/…`, empty id) false; a pusher uri with an empty
  id false; `{"type":"pong"}` false; the lowercase header spelling true.

**`src/apps/Wavee.Tests/SpotifySessionTests.cs`**, two facts in the B4 block:

- `A_dealer_drop_before_its_hello_backs_off_and_re_opens_without_touching_the_ap`: walk `Online()` up to
  `AccessTokenMinted` (`Dealer == Opening`, `Phase == Minting`); `DealerDropped(number: Protocol)` →
  `CloseDealer | DealerBackoff`, `Reconnecting`, `Fault == Protocol`, `Dealer == Waiting`, `Epoch + 1`, `ApEpoch` same,
  `LoggedIn` still true, `AccessToken` kept, `ConnectionId.IsEmpty`; `DealerRetry` → `OpenDealer`, `Opening`;
  `DealerOnline` → `Online`, `AnnounceDevice`, `DealerAttempt == 0`.
- `A_late_hello_from_the_overdue_socket_is_stale`: after that drop, `DealerOnline` stamped with the old epoch →
  `SessionEffects.None`, phase unchanged, `ConnectionId.IsEmpty`.
- (H3 invariant, cheap) `A_hello_never_folds_while_signed_out`: `EndTransports` clears `LoggedIn` and bumps `Epoch`, so
  the dealer's hello from before it is stale — already what `A_stale_epochs_words_are_ignored…` shows for drops; add the
  hello case if not covered.

### 6.7 Optional — Diagnostics session card

Show `dealer=<LinkPhase>` and, while `Opening`, the seconds since `dealer connected`, next to the existing session
facts; read from `Spotify.Current` on the page's poll. Not required for the fix; it is where the owner would look first
during a live "Connecting…".

### 6.8 Issue, changelog, verification

- File the issue via the `github-triage` skill (owner approves the `gh` call): *"Session stuck on Connecting…: the dealer
  socket stays open without a pusher hello and nothing bounds it"* — this document as the body. CHANGELOG bullet ends
  with ` (#n)`, commit body `Fixes #n`.
- Gates: `dotnet build Wavee.slnx` Debug + Release, `dotnet test src/apps/Wavee.Tests` Debug + Release (the owner
  runs them; parallel subagents write only).
- Live check (owner's running instance, never stopped): a fresh launch must log, within 2 s of `dealer connected`,
  `dealer.frame n=1 kind=Message label=hm://pusher/v1/connections/…`, `dealer.hello id=… afterMs=…`, and
  `session.phase Minting→Online … on DealerOnline`. The stuck case cannot be forced against the live dealer; its path is
  covered by the fold fact (§6.6) and by the rule tests; the next real occurrence self-reports as
  `dealer.hello overdue afterMs=10000 frames=0` → `dealer dropped` → `dealer reconnecting in 3s (attempt 1, epoch 2, ap Up)`.

### 6.9 Work split (disjoint files)

| agent | files |
|---|---|
| A | `Spotify/Spotify.Dealer.Rules.cs` (new), `Wavee.Tests/DealerHelloRulesTests.cs` (new) |
| B | `Spotify/Spotify.Session.cs` (§6.2–6.4), `Spotify/Spotify.cs` (§6.5 doc line only) |
| C | `Wavee.Tests/SpotifySessionTests.cs` (§6.6 facts) |
| orchestrator | CHANGELOG, issue, build/test gates, live check |

## 7. Evidence index (for the next reader)

- Log grep: `grep -a "sid=fd1d6d47" wavee-20260930.log | grep -a -E "\[spotify\]|\[capture\]|put-state|fetch.send"` —
  seq 87 logged in, 109/112 tokens, 115 dealer connected, then only `wire.send channel=dealer type=ping` every 30 s and
  `server-clock synced` at seq 156.
- Capture: `layout.load_segment(capture-20260930.idx)`, filter `1790791181149 ≤ unix_ms`, kinds 11/12 — 20 pongs, 20
  pings, no label, no `Retry`, no `ConnectStatePut`; 8 `HttpCall` records before the dealer opened.
- Scratch script used: `<scratchpad>/dealer_capture_dump.py` (masks ids to 8 characters).
- Code: `Spotify.Session.cs:1740-1826` (loop), `:1832-1870` (dispatch), `:1985-2019` (keepalive), `:255-289` (apply);
  `Spotify.cs:453-514` (fold), `:1491-1555` (parse); `Capture.cs:114-119, 276-281`; `Capture.Host.cs:252-340, 568-604`;
  `Shell.Host.cs:355-375`; `Shell.cs:1530-1541`; `Spotify.Library.cs:106-117, 195-208`.

## 8. As implemented (2026-09-30)

§6.1–6.6 as written, minus both optional items (no forced fresh bearer on a second overdue, no Diagnostics card).
Deviations, all small:

- **Rules** (`Spotify/Spotify.Dealer.Rules.cs`): `DealerFirstFramesLogged` lives on the rules as
  `DealerHelloRules.FirstFramesLogged`. Two pure additions the log lines needed: `LogLabel(kind, uri, ident)` and
  `LogTopic(uri)` — the §6.3 `label=` was the raw capture label, which for the hello IS the connection id
  (`hm://pusher/v1/connections/<id>`) and for collection/rootlist pushes an account name. `LogTopic` keeps `hm://` and
  path segments while they are plain topic words (`[a-z][a-z0-9-_]{0,23}`), and cuts to `…` at the first that is not,
  at whatever follows `connections` or `user`, and at the third segment of `hm://collection/<set>/<user>`: the hello
  logs `label=hm://pusher/v1/connections/…`, a cluster push `hm://connect-state/v1/cluster` whole.
- **Receive** (`Spotify.Session.cs`): the deadline counts from the `dealer connected` instant (`DealerLoopCore` passes
  `connectedAt`, the same tick it writes to `s_lastDealerTick`), not from `Receive`'s entry. `ReceiveWithin` asks
  `Verdict` first (Held → the plain receive on the epoch's token, nothing allocated; Overdue → throw without starting a
  receive) and only while Waiting links a source to `ct` with `CancelAfter(ReceiveBudgetMs(...))`. The catch filter is
  `deadline.IsCancellationRequested && !ct.IsCancellationRequested` over any exception (an aborted receive may surface
  as a `WebSocketException` rather than an `OperationCanceledException`); an epoch cancellation still falls through to
  `DealerLoopCore`'s `epoch-cancelled` catch. The source is per receive and disposed with it, so nothing can fire once
  the hello is held.
- **Phase line** (`Apply`): logs on a change of the phase OR either link phase, exactly as §6.4's sketch (that is what
  gives the `dealer Down→Opening on AccessTokenMinted` line) — never per event.
- **Tests**: `DealerHelloRulesTests` (verdict/budget arithmetic, verdict ↔ budget agreement, `IsHello` over the real
  parser on the captured shape, `LogTopic`/`LogLabel` redaction); `SessionStepTests` gained
  `A_dealer_drop_before_its_hello_backs_off_and_re_opens_without_touching_the_ap` (also climbs a second overdue socket
  to 6 s) and `A_late_hello_from_the_overdue_socket_is_stale`. The optional H3 hello-while-signed-out fact was not added.
- Log lines (always on, category `spotify`):
  `dealer.frame n=<1..3> kind=<DealerFrameKind> label=<LogLabel> bytes=<n> afterMs=<ms> epoch=<e>`,
  `dealer.hello id=<Platform.Redact: 3 chars***2 chars> afterMs=<ms> epoch=<e>`,
  `dealer.hello overdue afterMs=<ms> frames=<n> — …` (Warn), `dealer.exit reason=hello-overdue epoch=<e>`,
  `session.phase <A>→<B> ap <A>→<B> dealer <A>→<B> on <SessionEventKind> epoch=<e> fault=<SessionFault>`.
