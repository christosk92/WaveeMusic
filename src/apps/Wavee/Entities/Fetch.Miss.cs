// ── Entities/Fetch.Miss.cs — re-ask an entity a successful batch omitted (ledger 2a, plan §4.1) ────────────────────
//
// `Fetch.Answer`'s own doc has always said groups a batch did not fill stay ASKED — "the seal that stops a thin
// track re-resolving on every cluster update". That seal has exactly one escape hatch: `unfilled`, for a ROUTE that
// did not answer at all (a 401 on the top-tracks REST beside a 200 overview). It has none for a route that DID
// answer 200 while its body simply left one entity out — a show missing from a batch of shows, an episode a paged
// read dropped. That row is `Asked` with `Inflight` cleared the moment the batch settles: the exact shape a surface
// reads as "asked, nothing coming" is also the shape of "asked, and never will be" — a blank title, 0:00, forever,
// until whatever page mounted it happens to call `Fetch.Refresh` (ledger row 2a; nothing does, on its own).
//
// The fix is the same shape as everything else in this file: a pure decision (`FetchMissPolicy.Decide`, how many
// times is enough) and a SHELL half that knows the marks. `Answer` calls `ReviewMisses` for every row of a batch
// whose route answered but whose wanted groups are still not settled; a row under the retry budget has its `Asked`
// bits cleared for exactly those groups — like `Unask` does for `unfilled` — and is queued straight into a backed-off
// bucket via the same `Demand`/`Bucket` machinery `Failed`'s retryable arm already uses, so `Pump` sends it again
// without a second `Ensure`. A row that has used up its retries is sealed as today (`Asked` stays) but is also
// marked `Table.Failed` for those groups — the row twin `Fetch.Failed`'s terminal arm already gives a transport
// failure — so a surface can paint the failure glyph `MarkFailed`'s doc describes instead of a bare, silent 0:00.

using System.Buffers;

namespace Wavee;

/// <summary>THE decision, and nothing else: has an entity been left out of an answered batch enough times to give up
/// on it? Pure, engine-free, allocation-free — no <see cref="Table"/>, no <see cref="Scope"/>, no clock. <see cref="Fetch"/>
/// (<c>Fetch.Miss.cs</c>'s <c>ReviewMisses</c>) owns WHEN a miss happened, WHICH groups it cost and WHAT the marks
/// do about it; this owns only HOW MANY are enough — the one number a test can pin without reading a column.</summary>
public static class FetchMissPolicy
{
    /// <summary>Retries a row gets after a 200 batch answers without it — two, the same two-strikes shape
    /// <see cref="Fetch.MaxAttempts"/> gives a transport failure (attempts 0, 1, 2 all retry; attempt 3 does not),
    /// minus the one asking pass that already went out and came back silent on this row.</summary>
    public const int MaxMisses = 2;

    /// <summary>What <see cref="ReviewMisses"/> below does about a row this round: ask again, or give up and let the
    /// row show a failure glyph instead of a shimmer that never resolves.</summary>
    public enum Verdict : byte { Retry, Seal }

    /// <summary>Decide for a row that has been missed <paramref name="misses"/> times before THIS one (0 the first
    /// time a 200 batch answers without it — no prior miss, so this is the row's first). Retry while under
    /// <see cref="MaxMisses"/>; Seal from the miss that reaches it. A negative count (there should never be one) is
    /// clamped to 0 rather than trusted, so a caller's bookkeeping bug reads as "just missed" and not as an
    /// impossible permanent seal.</summary>
    public static Verdict Decide(int misses) => (misses < 0 ? 0 : misses) < MaxMisses ? Verdict.Retry : Verdict.Seal;

    /// <summary>WHICH of a batch's groups count as missed on one row after its answer landed: what was wanted, minus
    /// what the row has settled, minus the groups whose ROUTE did not answer (<paramref name="routedUnfilled"/> —
    /// <see cref="Fetch.Answer"/>'s <c>unfilled</c>, already un-asked by <c>Unask</c>), minus the groups NO route
    /// serves at all (<paramref name="unserved"/> — <c>FetchRoutes.For</c>'s <c>sealedGroups</c>). The last term is
    /// the one that was missing on 2026-09-19: <c>EpisodeFields.Progress</c> has no transport (it is the login
    /// hydrate's, not a per-episode ask), so every page asking <c>Row | About | Progress</c> "missed" Progress on
    /// every never-played episode — three round trips of nothing per row, 1,356 <c>fetch.miss</c> lines in one
    /// evening, and a false <c>Table.Failed</c> mark on a group that was correctly sealed all along. A group without a
    /// route is sealed by the plan and stays sealed; it is never an omission.</summary>
    public static uint Missed(uint wanted, uint settled, uint routedUnfilled, uint unserved)
        => wanted & ~settled & ~routedUnfilled & ~unserved;
}

public static partial class Fetch
{
    /// <summary>How many times a (table, slot) row has been left out of an answered batch this scope, since the last
    /// time it was fully settled or explicitly <see cref="Refresh"/>ed. Keyed by the TABLE REFERENCE, not by
    /// <see cref="Table.Kind"/> (<see cref="RefusedAsk"/> does the same, for the same reason): the four synthetic
    /// tables — Home, Sections, Searches, Browses — all answer <see cref="EntityKind.Unknown"/> (G-041), so a key of
    /// (kind, slot) alone would let a Home row and a Browse row of the same slot number stomp on each other's count.
    /// A scope switch replaces every table, and the key is dropped whole with it (the three <c>s_misses.Clear()</c>
    /// call sites in Fetch.cs), so a stale reference never outlives the rows it counted. Keyed by slot rather than by
    /// group: today's miss is always the one or two groups one plan bucket asked in a shape, so a single small
    /// counter per row is enough, and a per-group column beside <see cref="Table.Asked"/> for a case this rare was
    /// not worth Table.cs's own arithmetic (+40,004 B / 10k rows per <c>uint</c> column, by that file's own
    /// accounting).</summary>
    static readonly Dictionary<(Table Table, int Slot), byte> s_misses = new(16);

    /// <summary>The batch's rows' <see cref="Table.Version"/>, read BEFORE the commit — the "did anything land for
    /// this row" snapshot <see cref="ReviewMisses"/> compares against after. Rented, not allocated (P8/P9): the
    /// common case (no miss this answer) returns it unread and unchanged.</summary>
    static uint[] SnapshotVersions(FetchBatch batch, Table table)
    {
        uint[] before = ArrayPool<uint>.Shared.Rent(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            int slot = batch.Slots[i];
            before[i] = (uint)slot < (uint)table.Count ? table.Version[slot] : 0;
        }
        return before;
    }

    /// <summary>Called from <see cref="Answer"/> for every ROW batch that landed (never an edge batch: an edge's
    /// "some parents unanswered" case is <see cref="EdgesAnswered"/>'s own, over <c>EdgeTableBase.IsFailed</c>, not
    /// this file's). <paramref name="routedUnfilled"/> is <c>unfilled &amp; batch.Wanted</c> — the groups whose ROUTE
    /// did not answer, already un-asked by <see cref="Unask"/> just before this runs. What is left here is the other
    /// failure: a group whose route DID answer but whose entity the body did not name, still un-settled after the
    /// commit above landed whatever it had.
    ///
    /// <para><paramref name="versionsBefore"/> (<see cref="SnapshotVersions"/>, or null when the answer carried no
    /// staging at all — nothing could have landed for anyone) is what keeps this from over-firing on
    /// <c>A_partial_answer_does_not_re_ask</c>'s shape: a row TrackV4 answered for Identity but that will never carry
    /// PlayCount has a real, permanent gap — its <see cref="Table.Version"/> moved, because Identity DID land — and
    /// retrying it would just repeat the same request forever for a group this ROUTE structurally never returns.
    /// Only a row whose <c>Version</c> did NOT move is one the body left out entirely: the one ledger 2a names.</para></summary>
    static void ReviewMisses(FetchBatch batch, Table table, uint routedUnfilled, uint[]? versionsBefore)
    {
        // The groups this build has NO transport for (`FetchRoutes.For`'s `sealedGroups`): the provider answered
        // without them by construction, so they are the plan's seal, never the body's omission. Computed once per
        // batch — the route walk is static and allocation-free — and a batch that served nothing has nothing to miss.
        Span<FetchRoute> routes = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        _ = FetchRoutes.For(batch, routes, out uint unserved);
        if ((batch.Wanted & ~unserved & ~routedUnfilled) == 0) return;

        for (int i = 0; i < batch.Count; i++)
        {
            int slot = batch.Slots[i];
            if (slot <= Table.None || slot >= table.Count || table.Id[slot] != batch.Ids[i]) continue;   // recycled since the plan
            uint miss = FetchMissPolicy.Missed(batch.Wanted, table.Settled(slot), routedUnfilled, unserved);
            if (miss == 0)
            {
                if (s_misses.Count > 0) s_misses.Remove((table, slot));   // this round answered it: the count resets
                continue;
            }
            if (versionsBefore is not null && table.Version[slot] != versionsBefore[i]) continue;   // something DID
            // land for this row this round — the remaining gap is a shape the route never fills, not an omission.

            var key = (table, slot);
            s_misses.TryGetValue(key, out byte before);
            FetchMissPolicy.Verdict verdict = FetchMissPolicy.Decide(before);
            LogMiss(table.Kind, slot, miss, before + 1);

            if (verdict == FetchMissPolicy.Verdict.Retry)
            {
                s_misses[key] = (byte)(before + 1);
                // Clear the seal for exactly the missed groups — the same act `Unask` performs for `unfilled` — so
                // the row reads as "not yet asked" (a shimmer) rather than "asked, nothing coming" (today's blank
                // 0:00) while it waits out the backoff below.
                table.Asked[slot] &= ~miss;
                Demand d = Bucket(table, batch.Subject, batch.Provider, miss, FetchEdge.None, 0, batch.Priority);
                d.Add(slot, batch.Ids[i]);
                // The same clamp `Failed`'s retryable arm reuses (attempt, not status/Retry-After): a miss is not a
                // 429, so `Backoff(attempt, 0, 0)` is `min(60, 2^attempt)` seconds — 1 s the first time, 2 s the next.
                d.ReadyAt = Entities.Now + Backoff(before, 0, 0);
            }
            else
            {
                s_misses.Remove(key);
                // Sealed exactly as an exhausted `Asked` seal always was — but the row's last Retry (if any) cleared
                // `Asked` for `miss` to let it shimmer through the backoff, and nothing re-set it since (the retry
                // path never runs `Plan`): restore it here, or "leave `Asked` set" would be true of every miss EXCEPT
                // the one that finally seals. Paired with the row twin of a terminal transport failure's mark,
                // `Table.Failed`, so a bound surface paints the glyph `MarkFailed`'s own doc describes instead of
                // silently rendering the group's default columns.
                table.Asked[slot] |= miss;
                table.Failed[slot] |= miss;
                table.MarkDirty();
            }
        }
    }

    // `fetch.miss kind= slot= groups= attempt=` — one line per row per miss, the wire idiom `LogSend`/`LogAnswer`
    // already use: built only when Info passes, so a filtered log costs one compare, and `attempt` counts from 1
    // (the first miss is attempt 1) so it reads next to `fetch.send`'s own `attempt=` without a fencepost surprise.
    static void LogMiss(EntityKind kind, int slot, uint groups, int attempt)
    {
        if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
        Log.Event(WaveeLogLevel.Info, "fetch", "fetch.miss", "", null, -1, null,
            WaveeLogField.Of("kind", kind.ToString()),
            WaveeLogField.Of("slot", slot),
            WaveeLogField.Of("groups", Groups(groups)),
            WaveeLogField.Of("attempt", attempt));
    }
}
