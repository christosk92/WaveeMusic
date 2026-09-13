using System;
using System.Collections.Generic;

namespace Wavee.Backend.Collections;

// ── "Server says N, we hold M" ────────────────────────────────────────────────────────────────────────────────────────
// The delta model can only ever ship CHANGES; it has no way to notice that a past snapshot lost its tail or that a delta
// was capped (DeltaResponse carries no pagination). The reconcile pass therefore shadow-walks the wire set and compares
// the server's uri set with the local one, per logical set. Missing = the server has it, we don't; Extra = we have it,
// the server doesn't. Both directions run through the same shields the sweep uses (CollectionSweepPolicy): a member
// with a pending local intent is neither missing nor extra (the drain decides it), and a recently-added local member is
// not extra (write→page lag) — so a drift report never counts the user's own in-flight action as corruption.
public sealed record CollectionDriftReport(string SetId, int Local, int Server, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
{
    public bool HasDrift => Missing.Count > 0 || Extra.Count > 0;
}

public static class CollectionDrift
{
    /// <param name="hasPending">uri → a local intent for (setId, uri) is in the outbox. Null = nothing pending.</param>
    /// <param name="walkStartedAtMs">The shadow walk's start (CollectionSnapshotLedger.StartedAtMs) — the recency shield's clock.</param>
    public static CollectionDriftReport Compare(string setId, IReadOnlyList<SavedItem> local, IReadOnlySet<string> serverUris,
        Func<string, bool>? hasPending, long walkStartedAtMs)
    {
        var localUris = new HashSet<string>(StringComparer.Ordinal);
        var extra = new List<string>();
        for (int i = 0; i < local.Count; i++)
        {
            var item = local[i];
            localUris.Add(item.Uri);
            bool pending = hasPending is not null && hasPending(item.Uri);
            if (CollectionSweepPolicy.Decide(serverUris.Contains(item.Uri), pending, item.AddedAtMs, walkStartedAtMs) == CollectionSweepPolicy.Keep.Remove)
                extra.Add(item.Uri);
        }
        var missing = new List<string>();
        foreach (var uri in serverUris)
        {
            if (localUris.Contains(uri)) continue;
            if (hasPending is not null && hasPending(uri)) continue;   // an unsave in flight: the server just hasn't heard yet
            missing.Add(uri);
        }
        return new CollectionDriftReport(setId, local.Count, serverUris.Count, missing, extra);
    }
}
