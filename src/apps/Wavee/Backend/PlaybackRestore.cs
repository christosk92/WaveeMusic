using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend;

// ── Playback restore contracts (playback-restore fix §8) ─────────────────────────────────────────────────────────────
// The proto-free shape the controller consumes on the empty-cluster launch path. The persisted document itself is
// SessionPlaybackDto inside session.json (App/SessionSnapshotStore.cs); the bridge adapts it to this Backend record so
// the Backend keeps its Wavee.Core+BCL-only dependency rule. TransferWireState-shaped on purpose — NOT a second queue
// model: History is rebuilt empty (Previous restarts), Upcoming heals from the context uri, and the user queue is the
// one local-only set the cluster cannot supply.

/// <summary>The consumed restore snapshot: identity of the current playable (uid → uri → index, the §6 ladder), the
/// paused position, options, and the user-queue refs (provider "queue"). <see cref="CurrentIndex"/> is -1 when the
/// writer had no context cursor (e.g. the current stood outside the context spine).</summary>
public sealed record PlaybackSessionSnapshot(
    string ContextUri,
    string CurrentUri,
    string CurrentUid,
    int CurrentIndex,
    long PositionMs,
    bool Shuffle,
    RepeatMode Repeat,
    IReadOnlyList<QueuedRef> UserQueue,
    bool AutoplayActive,
    string? AutoplayContextUri = null);

/// <summary>The queue-content fold behind PlaybackBridge's <c>QueueRevision</c> signal: count + per-row id/bucket/provider
/// (order-sensitive FNV). Bump the revision only when this changes — a pause/seek/volume republish re-windows the SAME
/// entries and must coalesce; a ghost/recovery seed produces new rows and must not. Kept in the Backend so the rule is
/// unit-testable without the engine (test 24).</summary>
public static class QueueContentFold
{
    public static ulong Fold(IReadOnlyList<QueueEntry> queue)
    {
        ulong fold = 1469598103934665603UL;   // FNV-ish, order-sensitive
        fold = (fold ^ (ulong)queue.Count) * 1099511628211UL;
        for (int i = 0; i < queue.Count; i++)
        {
            var e = queue[i];
            fold = (fold ^ e.ItemId.Value) * 1099511628211UL;
            fold = (fold ^ (uint)e.Bucket) * 1099511628211UL;
            fold = (fold ^ (uint)e.Provider) * 1099511628211UL;
            if (e.ItemId.IsNone)   // degenerate/fake ids collide → mix the derived EntryId so the set still distinguishes
                fold = (fold ^ (ulong)(uint)e.EntryId.GetHashCode(StringComparison.Ordinal)) * 1099511628211UL;
        }
        return fold;
    }
}
