using System;

namespace Wavee;

/// <summary>The recs section's request state. <see cref="Failed"/> is new: a timed-out or faulted fetch used to be
/// indistinguishable from "loading" and wedged the section for the component's life.</summary>
public enum RecsState : byte { Idle = 0, Loading = 1, Loaded = 2, Failed = 3 }

public enum RecsAction : byte
{
    /// <summary>Nothing to do (not armed, already current, or the user must press Refresh).</summary>
    None = 0,
    /// <summary>Start a fetch; no request is in flight.</summary>
    Fetch = 1,
    /// <summary>Cancel the in-flight request and start a new one (its result is stale before it lands).</summary>
    Supersede = 2,
}

/// <summary>The PURE decision behind every recs fetch. Engine-free (System only) so RecsRefetchPolicyTests pins the
/// whole matrix against production code.
/// <para>Inputs: the current state; whether the header has ever mounted (<paramref name="armed"/> — the section is
/// lazy, nothing fetches until the user scrolls to it); whether the membership fingerprint the CURRENT batch was
/// fetched for still matches (<paramref name="fingerprintCurrent"/>); and whether this is an explicit user/refill
/// request (<paramref name="force"/> — the Refresh button, or AddRec emptying the batch).</para></summary>
public static class RecsRefetchPolicy
{
    /// <summary>The in-flight deadline. Headers are bounded by HttpClient.Timeout (30 s); the BODY is not (ResponseHeadersRead),
    /// so this is what turns a stalled stream into <see cref="RecsState.Failed"/> instead of a forever-spinner.</summary>
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromSeconds(15);

    /// <summary>How long the membership must be quiet before a changed fingerprint re-fetches. A drag-drop of five
    /// tracks is five store bumps; one request, not five.</summary>
    public const float DebounceMs = 750f;

    public static RecsAction Decide(RecsState state, bool armed, bool fingerprintCurrent, bool force)
    {
        if (!armed) return RecsAction.None;                                   // lazy: nothing until the header has realized
        if (force) return state == RecsState.Loading ? RecsAction.Supersede : RecsAction.Fetch;
        if (!fingerprintCurrent) return state == RecsState.Loading ? RecsAction.Supersede : RecsAction.Fetch;
        return state == RecsState.Idle ? RecsAction.Fetch : RecsAction.None; // Loading: wait · Loaded: current · Failed: user retries
    }

    /// <summary>The membership fingerprint: context uri + row count + an order-insensitive 64-bit fold of the row uris.
    /// Order-insensitive on purpose — a reorder changes nothing the extender sees (it infers seeds from membership).
    /// Cheap (one pass, no allocation) and VALUE-MEANINGFUL, which the engine's debounced-thunk memo requires: an
    /// in-place refresh that republishes the same rows produces the same value and re-arms nothing.</summary>
    public static long Fingerprint(string? contextUri, System.Collections.Generic.IReadOnlyList<Wavee.Core.Track> tracks)
    {
        ulong h = 1469598103934665603UL;                                      // FNV-1a offset basis
        h = Fnv(h, (ulong)(uint)(contextUri?.GetHashCode() ?? 0));            // string.GetHashCode is per-process; fine for an in-memory key
        h = Fnv(h, (ulong)tracks.Count);
        ulong fold = 0;
        for (int i = 0; i < tracks.Count; i++) fold += (ulong)(uint)string.GetHashCode(tracks[i].Uri, StringComparison.Ordinal);
        h = Fnv(h, fold);
        return (long)h;
    }

    static ulong Fnv(ulong h, ulong v) { h ^= v; return h * 1099511628211UL; }
}
