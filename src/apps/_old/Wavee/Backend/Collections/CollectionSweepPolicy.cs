namespace Wavee.Backend.Collections;

// ── Mark-and-sweep: which local members a VERIFIED snapshot may remove ────────────────────────────────────────────────
// After a verified walk (CollectionSnapshotLedger) every local member absent from the snapshot is a candidate for removal
// — the server dropped it. Two shields stand between a candidate and the delete:
//   Pending — a local intent for that (set, uri) is still in the outbox (§7.2): the server has not been told yet, so of
//             course the snapshot lacks it. The drain converges it.
//   Recent  — the member was added within RecencyShield of the walk's start. An ack'd like leaves the outbox the moment
//             the write returns, but the collection service is eventually consistent behind /write: a walk that starts
//             seconds later can still page a snapshot without it. Without this window a like made just before a
//             reconcile pass would be swept and then re-delivered by the next delta — a visible heart flicker at best,
//             a lost like at worst (the pending shield alone has no grace after the ack).
// A member with no timestamp at all (0 = unknown) has nothing to be recent BY, so it is removable on absence.
public static class CollectionSweepPolicy
{
    public enum Keep : byte { InSnapshot, Pending, Recent, Remove }

    /// <summary>Ten minutes. Generous against collection2v2's write→page lag, and cheap: a genuinely removed member that
    /// happens to be this young stays until the next delta says removed (deltas carry removals; snapshots do not).</summary>
    public const long RecencyShieldMs = 10 * 60 * 1000L;

    public static Keep Decide(bool inSnapshot, bool hasPending, long addedAtMs, long walkStartedAtMs)
    {
        if (inSnapshot) return Keep.InSnapshot;
        if (hasPending) return Keep.Pending;
        if (addedAtMs > 0 && addedAtMs >= walkStartedAtMs - RecencyShieldMs) return Keep.Recent;
        return Keep.Remove;
    }
}
