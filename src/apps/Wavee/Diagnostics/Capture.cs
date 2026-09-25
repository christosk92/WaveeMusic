// ── Wavee/Diagnostics/Capture.cs — the causal flight recorder's shapes and the Capture facade ──────────────────────
//
// Unit 1 of docs/plans/wavee/realtime-capture-implementation.md (§2, §2.3, §6.1, §6.3, §6.5). PURE DATA + PURE
// RULES + the thin `Capture` facade every other unit calls. No disk I/O lives here — that is `Capture.Host.cs`
// (unit 2), which installs itself into `Capture.Sink` (the seam below) once it exists. Until it does, every call
// on this class still mints valid, causally-consistent ids (so unit 4/5 call sites can be written and compile
// today) but nothing is persisted: `Sink` is null, so the "write it somewhere" half of every emit is a no-op.
//
// Zero-cost-when-off: every public entry point below checks `Enabled` FIRST and returns before doing anything else
// (id minting, boxing a verdict enum, touching the thread-static ambient slot) — matching §2.4's guarantee.

using System;
using System.Threading;

namespace Wavee;

/// <summary>One event in the flight recorder. PURE DATA — no behaviour, no disk knowledge (§2.1).</summary>
public readonly record struct CaptureEvent(
    long Seq,                 // monotonic, per-process — the ONLY total order a reader can trust across a day roll
    long Qpc,                 // Stopwatch.GetTimestamp() — sub-ms LOCAL ordering/duration within one segment
    long UnixMs,              // wall clock — the join key against wavee-YYYYMMDD.log's own UnixMs-stamped lines
    long Id,                  // this event's own id (0 for a record that nothing will ever reference as a cause)
    long CauseId,             // the event that directly caused this one; 0 = none
    long RootId,              // the top of this causal tree; equals Id when this event IS the root
    CaptureKind Kind,
    CapturePhase Phase,       // Begin | End | Point — a `Point` (a click, a UI re-render) has no matching End
    CapturePriority Priority,
    CaptureFields Fields);

/// <summary>Small fixed-shape struct (no boxing, no `object[]`) — §2.1. `PayloadOffset`/`PayloadLength` are
/// write-time sentinels here (-1/0, "no body yet decided"): the actual byte range into the segment's payload blob
/// is assigned by the writer (unit 2) when it places the bytes handed alongside this event (see
/// <see cref="ICaptureSink.Emit"/>) — never computed on the calling thread.</summary>
public readonly record struct CaptureFields(
    string? A = null, string? B = null, string? C = null,
    long N0 = 0, long N1 = 0,
    int PayloadOffset = -1, int PayloadLength = 0,
    bool Truncated = false);

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

/// <summary>§3.5: a small, pure lookup decides which lane-drop policy applies; it is not a scheduling primitive.</summary>
public enum CapturePriority : byte { Low, Normal }

/// <summary>§6.1: the fixed on-disk shape of one `.idx` record — a `struct.unpack`-able layout, so `decode.py`
/// never needs a JSON parser for the header lane. `A`/`B`/`C` are UTF-8, length-prefixed (ushort), truncated to
/// 252 bytes each. This is the WRITER's (unit 2) output shape; unit 1 only fixes the contract so the on-disk
/// layout and `decode.py`'s `struct.unpack` format string cannot silently drift (see `CaptureRecordCodecTests`).</summary>
public readonly record struct CaptureRecord(
    long Seq, long Qpc, long UnixMs, long Id, long CauseId, long RootId,
    CaptureKind Kind, CapturePhase Phase, CapturePriority Priority, bool Truncated,
    long N0, long N1, int PayloadOffset, int PayloadLength,
    ReadOnlyMemory<byte> A, ReadOnlyMemory<byte> B, ReadOnlyMemory<byte> C)
{
    /// <summary>Everything above except A/B/C, which follow inline, length-prefixed. 8-byte aligned.</summary>
    public const int FixedHeaderBytes = 96;
}

/// <summary>§6.5: given an ambient/threaded cause id, what `(Id, CauseId, RootId)` a new event gets. This is the
/// ONE function every §2.2 call site's id math actually reduces to; keeping it pure means "a remote push with no
/// known parent gets its own root" and "a child inherits its cause's root" are each one assertion in a test.</summary>
public static class CausalityRules
{
    /// <summary>Resolves the `RootId` of a previously-minted event id. Implemented by whatever owns the id space
    /// at runtime (the writer's bounded root table, unit 2's `Capture.Host.cs`, or a test double) — kept as a
    /// delegate so this class stays engine-free and testable with fixture data, per CLAUDE.md's "no source-text
    /// tests" rule. Returning 0 means "unknown cause" — <see cref="NewEvent"/> then treats `threadedCauseId`
    /// itself as the root (a defensive fallback, never a thrown exception).</summary>
    public static (long Id, long CauseId, long RootId) NewEvent(long nextSeq, long threadedCauseId, IdLookup lookup)
    {
        if (threadedCauseId == 0)
        {
            // No known cause: this event starts a NEW causal chain — a user input, or an inbound frame recognized
            // as unprompted. It is its own root (RootId == Id), per CaptureEvent's own doc comment.
            return (nextSeq, 0, nextSeq);
        }

        long root = lookup(threadedCauseId);
        if (root == 0) root = threadedCauseId; // cause's own root is unknown — treat the cause as the root itself
        return (nextSeq, threadedCauseId, root);
    }
}

/// <summary>Resolves the `RootId` of a previously-minted event id (0 = unknown). See <see cref="CausalityRules.NewEvent"/>.</summary>
public delegate long IdLookup(long id);

/// <summary>§3.5/§6.3: `CaptureKind` (+ uri, for the two kinds that need it) → `CapturePriority`, a pure lookup.
/// `Ping`/`Pong` dealer frames (no uri) and `hm://collection/*` HTTP diffs are `Low` — the first thing the header
/// lane drops under budget pressure; everything else, including every `ActionInvoke` root and every
/// `ConnectStatePut`/response pair, is `Normal`.</summary>
public static class CaptureRules
{
    public static CapturePriority PriorityOf(CaptureKind kind, string? uri) => kind switch
    {
        CaptureKind.DealerFrameIn or CaptureKind.DealerFrameOut when uri is null => CapturePriority.Low,
        CaptureKind.HttpCall when uri is { } u && u.StartsWith("/collection/", StringComparison.Ordinal) => CapturePriority.Low,
        _ => CapturePriority.Normal,
    };

    /// <summary>PURE — security requirement added post-unit-4 review (2026-09-23): a request/response BODY must
    /// never reach a capture payload for a credential/token endpoint, however small or JSON-shaped it looks — this
    /// is checked at the call site (`Platform.Wire.cs`'s `LoggingHandler`) BEFORE any read is even attempted, so an
    /// auth endpoint's body is never buffered, redacted-then-kept, or otherwise touched; only method/host/path
    /// (query redacted)/status/timing/size/error ever reach the record. Named hosts/paths are this app's own three
    /// credential-exchange surfaces (`Spotify.Api.cs`'s <c>Login5Host</c>/<c>ClientTokenHost</c>,
    /// `Spotify.OAuth.cs`'s <c>accounts.spotify.com</c> endpoints); the path-substring fallback catches the same
    /// shapes under a host this rule does not otherwise recognize (a redirect, a mirrored host, a future route)
    /// without depending on this list staying in sync with every literal elsewhere.</summary>
    public static bool IsAuthEndpoint(string? host, string? path)
    {
        if (!string.IsNullOrEmpty(host))
        {
            if (host.Equals("login5.spotify.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".login5.spotify.com", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Equals("clienttoken.spotify.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".clienttoken.spotify.com", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Equals("accounts.spotify.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".accounts.spotify.com", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!string.IsNullOrEmpty(path))
        {
            if (Contains(path, "login5")) return true;
            if (Contains(path, "clienttoken")) return true;
            if (Contains(path, "oauth2/device")) return true;
            if (Contains(path, "oauth/token")) return true;
            if (Contains(path, "api/token")) return true;
            if (Contains(path, "credential")) return true; // stored-credential exchange
        }
        return false;

        static bool Contains(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The writer seam (unit 2). Every `Capture.*` emit point below hands its finished <see cref="CaptureEvent"/>
/// (plus any raw, not-yet-redacted-into-a-blob-offset payload bytes) to this interface. `Capture.Sink` is null
/// until `RealtimeCaptureHost` (unit 2) installs one at `HostOpenLog` time — until then every emit point still
/// mints a correct id (so call sites written against unit 1 alone compile and behave causally), it just has nowhere
/// to persist to. Implementations own lane routing (§3.2), redaction (unit 3) and the eventual disk write.</summary>
public interface ICaptureSink
{
    void Emit(in CaptureEvent evt, ReadOnlyMemory<byte> payload);
}

/// <summary>The one call surface every other unit programs against (§2.3, §2.4). Guard-first: `Enabled == false`
/// is a single volatile read and a branch — nothing after it runs.</summary>
public static class Capture
{
    // ── the zero-cost gate ──────────────────────────────────────────────────────────────────────────────────────
    private static volatile bool s_enabled;

    /// <summary>The ONE branch every call site pays when the toggle is off (§2.4).</summary>
    public static bool Enabled => s_enabled;

    /// <summary>Flips the gate. Called by `RealtimeCaptureHost.OnSettingsChanged` (unit 2) on the `Signal&lt;bool&gt;`
    /// transition — never read/written anywhere else, matching the old file's single `_enabled` flag (§1 item 4,
    /// kept, just owned by the host instead of a settable singleton knob).</summary>
    public static void SetEnabled(bool value) => s_enabled = value;

    /// <summary>The writer seam (see <see cref="ICaptureSink"/>). Null-safe: every emit point below treats a null
    /// sink as "nothing to persist to" and still returns a valid id.</summary>
    public static ICaptureSink? Sink;

    // ── id minting: a bounded, approximate root table so CausalityRules.NewEvent has something to look up without
    // unit 1 depending on unit 2's writer. Ring-buffered (fixed memory): a very long-lived chain whose cause id's
    // slot has since been overwritten by a newer, unrelated event resolves as "unknown" and NewEvent's own
    // fallback (treat the cause id as the root) takes over — a graceful degradation, never a crash or a leak. ──
    private const int RootTableCapacity = 1 << 16; // 65536 — bounded memory regardless of session length
    private static readonly long[] s_rootTableIds = new long[RootTableCapacity];
    private static readonly long[] s_rootTableRoots = new long[RootTableCapacity];
    private static readonly long[] s_rootTableCauses = new long[RootTableCapacity];
    private static long s_seq;

    private static long NextSeq() => Interlocked.Increment(ref s_seq);

    private static long RootOf(long id)
    {
        if (id == 0) return 0;
        int slot = (int)((ulong)id % RootTableCapacity);
        return Volatile.Read(ref s_rootTableIds[slot]) == id ? Volatile.Read(ref s_rootTableRoots[slot]) : 0;
    }

    private static long CauseOf(long id)
    {
        if (id == 0) return 0;
        int slot = (int)((ulong)id % RootTableCapacity);
        return Volatile.Read(ref s_rootTableIds[slot]) == id ? Volatile.Read(ref s_rootTableCauses[slot]) : 0;
    }

    private static void Remember(long id, long causeId, long rootId)
    {
        int slot = (int)((ulong)id % RootTableCapacity);
        Volatile.Write(ref s_rootTableRoots[slot], rootId);
        Volatile.Write(ref s_rootTableCauses[slot], causeId);
        Volatile.Write(ref s_rootTableIds[slot], id);
    }

    // ── the ambient UI-thread cause slot (§2.2) — read-only outside this synchronous scope; NEVER read from a
    // background/pool thread (that is exactly the stale-value leak §2.2 refuses). ──
    [ThreadStatic] private static long t_uiThreadCause;

    /// <summary>The current ambient UI-thread cause, or 0 outside an <see cref="AmbientUiCause"/> scope.</summary>
    public static long AmbientUiCauseId => t_uiThreadCause;

    /// <summary>Scopes the UI thread's ambient cause id for the duration of one synchronous dispatch (§2.2's
    /// `ActionRules.Execute` example). Restores the previous value on <see cref="Dispose"/> so nested scopes
    /// (there should not be any in practice, but a defensive restore costs nothing) never leak outward.</summary>
    public static AmbientCauseScope AmbientUiCause(long causeId)
    {
        long previous = t_uiThreadCause;
        t_uiThreadCause = causeId;
        return new AmbientCauseScope(previous);
    }

    public readonly struct AmbientCauseScope(long previous) : IDisposable
    {
        public void Dispose() => t_uiThreadCause = previous;
    }

    // ── the emit API (§2.4) ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mint a new root id (a user input, or a dealer frame with no known parent). Returns 0 — a valid
    /// "no cause" sentinel — when capture is off.</summary>
    public static long NewRoot(CaptureKind kind, string? a = null, string? b = null, string? c = null, long n0 = 0, long n1 = 0)
    {
        if (!s_enabled) return 0;
        return EmitCore(kind, CapturePhase.Point, threadedCauseId: 0, a, b, c, n0, n1, payload: default);
    }

    /// <summary>Begin a Begin/End pair under `causeId` (0 = none). Returns the new event's own id (0 when off).</summary>
    public static long Begin(CaptureKind kind, long causeId, string? a = null, string? b = null, string? c = null,
        long n0 = 0, long n1 = 0, ReadOnlyMemory<byte> payload = default)
    {
        if (!s_enabled) return 0;
        return EmitCore(kind, CapturePhase.Begin, causeId, a, b, c, n0, n1, payload);
    }

    /// <summary>Close a Begin previously returned by <see cref="Begin"/>. A no-op when `beginId == 0`.</summary>
    public static void End(long beginId, long n0 = 0, long n1 = 0, ReadOnlyMemory<byte> payload = default, bool truncated = false)
    {
        if (!s_enabled || beginId == 0) return;
        // An End shares its Begin's Id/CauseId/RootId (the reader pairs Begin/End records by Id) — never dropped
        // asymmetrically from its Begin (§3.2's header-lane rule): the sink is the one that may still choose to
        // drop the PAYLOAD, never the header pair.
        long causeId = CauseOf(beginId);
        long rootId = RootOf(beginId);
        if (rootId == 0) rootId = beginId; // the Begin's own root slot fell out of the ring buffer — degrade gracefully
        CaptureKind kind = KindOfOpenBegin(beginId);
        EmitRaw(new CaptureEvent(NextSeq(), Qpc(), UnixMs(), beginId, causeId, rootId,
            kind, CapturePhase.End, CapturePriority.Normal,
            new CaptureFields(N0: n0, N1: n1, Truncated: truncated)), payload);
    }

    /// <summary>A one-shot record with no matching End (a click, a re-render, an unprompted dealer push).</summary>
    public static void Point(CaptureKind kind, long causeId, string? a = null, string? b = null, string? c = null,
        long n0 = 0, long n1 = 0, ReadOnlyMemory<byte> payload = default)
    {
        if (!s_enabled) return;
        EmitCore(kind, CapturePhase.Point, causeId, a, b, c, n0, n1, payload);
    }

    /// <summary>A structured decision with no before/after state transition — which pure fold ran, what typed
    /// verdict it returned (§2.3). Boxes `verdict` (the one allocation this incurs, paid only when `Enabled`) and
    /// defers `.ToString()` formatting to the writer thread.</summary>
    public static void Decision<TVerdict>(CaptureKind kind, long causeId, TVerdict verdict, string? reason = null)
        where TVerdict : struct, Enum
    {
        if (!s_enabled) return;
        DecisionSlow(kind, causeId, verdict, before: null, after: null, reason);
    }

    /// <summary>A structured decision that also names the state TRANSITION the fold just made (§2.3: "a reader
    /// sees the STATE TRANSITION, not just the verdict flags"). `before`/`after` are typed `TState` — NOT `Enum` —
    /// specifically so passing them never boxes at the CALL SITE: boxing only happens inside
    /// <see cref="DecisionSlow"/>, which is reached only when `Enabled`, preserving the zero-cost-when-off
    /// guarantee that an `Enum?` parameter here would silently break (the caller's argument conversion to `Enum`
    /// boxes unconditionally, before the callee's own guard ever runs — a gap in the plan's printed signature,
    /// closed here).</summary>
    public static void Decision<TVerdict, TState>(CaptureKind kind, long causeId, TVerdict verdict,
        TState before, TState after, string? reason = null)
        where TVerdict : struct, Enum where TState : struct, Enum
    {
        if (!s_enabled) return;
        DecisionSlow(kind, causeId, verdict, before, after, reason);
    }

    private static void DecisionSlow<TVerdict>(CaptureKind kind, long causeId, TVerdict verdict,
        Enum? before, Enum? after, string? reason) where TVerdict : struct, Enum
    {
        // Three string slots (A/B/C) for four semantic values (verdict/before/after/reason): the state TRANSITION
        // (before→after) is what a reader wants as one glance, per §2.3's own "not just the verdict flags" ask.
        string? transition = before is null && after is null ? null : $"{before?.ToString() ?? "?"}→{after?.ToString() ?? "?"}";
        EmitCore(kind, CapturePhase.Point, causeId, a: verdict.ToString(), b: transition, c: reason,
            n0: 0, n1: 0, payload: default);
    }

    // ── shared plumbing ─────────────────────────────────────────────────────────────────────────────────────────

    private static long Qpc() => System.Diagnostics.Stopwatch.GetTimestamp();
    private static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // A small side table recording which CaptureKind an open Begin id belongs to, so End() (which only receives the
    // id) can reconstruct the right Kind for its own record. Same ring-buffer shape/bound as the root table.
    private static readonly CaptureKind[] s_openBeginKinds = new CaptureKind[RootTableCapacity];
    private static readonly long[] s_openBeginIds = new long[RootTableCapacity];

    private static CaptureKind KindOfOpenBegin(long beginId)
    {
        int slot = (int)((ulong)beginId % RootTableCapacity);
        return Volatile.Read(ref s_openBeginIds[slot]) == beginId ? s_openBeginKinds[slot] : default;
    }

    private static void RememberOpenBegin(long id, CaptureKind kind)
    {
        int slot = (int)((ulong)id % RootTableCapacity);
        s_openBeginKinds[slot] = kind;
        Volatile.Write(ref s_openBeginIds[slot], id);
    }

    private static long EmitCore(CaptureKind kind, CapturePhase phase, long threadedCauseId,
        string? a, string? b, string? c, long n0, long n1, ReadOnlyMemory<byte> payload)
    {
        long seq = NextSeq();
        var (id, causeId, rootId) = CausalityRules.NewEvent(seq, threadedCauseId, RootOf);
        // NewEvent resolves RootId via RootOf(threadedCauseId); the cause chain for a FUTURE lookup against THIS
        // event's own id is recorded here (Remember), and — for a Begin — its Kind is remembered separately so a
        // later End(id) can rebuild a correctly-Kinded record without the caller repeating the kind.
        Remember(id, causeId, rootId);
        if (phase == CapturePhase.Begin) RememberOpenBegin(id, kind);

        var evt = new CaptureEvent(seq, Qpc(), UnixMs(), id, causeId, rootId, kind, phase,
            CaptureRules.PriorityOf(kind, a), new CaptureFields(a, b, c, n0, n1, Truncated: false));
        EmitRaw(evt, payload);
        return id;
    }

    private static void EmitRaw(in CaptureEvent evt, ReadOnlyMemory<byte> payload) => Sink?.Emit(in evt, payload);
}
