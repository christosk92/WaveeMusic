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
//
// 2026-09-20: the seal above was not durable. `Fetch.Plan`'s own doc says a group's `Table.Failed` mark is cleared
// "the moment the planner asks the group again" (Fetch.cs ~605) — a rule written for a genuine retry, but one that
// cannot tell a genuine retry from an ordinary re-demand that reached the wire only because something ELSE (a
// broad, un-keyed `Ensure` on every table tick; see Show.Page.cs's `DemandRows`/`DemandTrailer`) cleared `Asked`
// for an already-sealed group without going through this file at all. When that happened, `ReviewMisses` used to
// find NOTHING for the row in `s_misses` — the entry was REMOVED the moment it sealed — so it treated the re-ask
// as brand new and restarted the budget at attempt 1. That is the defect the live log caught verbatim: `fetch.miss`
// attempts 1→2→3 (seal), then 1→2→3 again, four more times over ~150 s across two page visits, forever. The fix
// keeps the ledger entry after it seals instead of dropping it, and records the row's own `Table.Version` at that
// moment (`MissState`, below `s_misses`): a re-ask that finds the row SEALED with its `Version` unmoved is refused
// — the marks are reaffirmed and nothing about the attempt count changes — and only a genuine revision bump (the
// `Version` moved: real data landed for the row) or an explicit `Fetch.Refresh`/Retry (which already removes the
// `s_misses` entry outright, in Fetch.cs, untouched here) earns a fresh budget. The invariant is now structural: no
// caller of `Entities.Ensure` can un-seal a sealed miss by asking again, no matter how it got the `Asked` bit cleared.

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

    /// <summary>Does an ask against an already-SEALED row deserve a fresh retry budget? Only when the row's own
    /// <see cref="Table.Version"/> has moved since the moment it sealed (<paramref name="versionAtSeal"/>, recorded
    /// in <see cref="Fetch.MissState"/>) against what it is now (<paramref name="currentVersion"/>) — proof that a
    /// real answer landed for SOME group on this row since, not merely that somebody asked again. This is the one
    /// escape a sealed miss has other than an explicit <c>Fetch.Refresh</c>/Retry, which never reaches this check at
    /// all (it drops the row's <c>s_misses</c> entry outright, so the row reads as never-sealed). Equal versions mean
    /// nothing happened to the row between the seal and now: whatever cleared <c>Asked</c>/<c>Failed</c> for it was
    /// an ordinary re-demand, and <see cref="Fetch.ReviewMisses"/> must refuse to restart the counter for it — the
    /// exact 1→2→3→1 cycle the 2026-09-19 log caught (this file's header, §2026-09-20).</summary>
    public static bool RevisionChanged(uint versionAtSeal, uint currentVersion) => currentVersion != versionAtSeal;

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
    /// <summary>Everything <see cref="ReviewMisses"/> remembers about one (table, slot) row's miss-tracking. A row that
    /// is fully settled or explicitly <see cref="Refresh"/>ed has its <see cref="s_misses"/> entry removed outright —
    /// no <see cref="MissState"/> to interpret, just a clean slate. <see cref="Attempts"/> is the plain retry count
    /// <see cref="FetchMissPolicy.Decide"/> reads; <see cref="Sealed"/> and <see cref="Version"/> are the
    /// 2026-09-20 addition that makes the seal durable — see this file's header and <see cref="FetchMissPolicy.RevisionChanged"/>
    /// for why a bare attempt count was not enough. A row that has never missed has no entry at all (the
    /// <c>default</c> a failed dictionary lookup hands back — Attempts 0, Sealed false, Version 0 — reads correctly as
    /// "never asked before" without a branch).</summary>
    readonly record struct MissState(byte Attempts, bool Sealed, uint Version);

    /// <summary>How many times a (table, slot) row has been left out of an answered batch this scope — and, since
    /// 2026-09-20, whether that ended in a seal and what the row's <see cref="Table.Version"/> was then
    /// (<see cref="MissState"/>). Keyed by the TABLE REFERENCE, not by <see cref="Table.Kind"/> (<see cref="RefusedAsk"/>
    /// does the same, for the same reason): the four synthetic tables — Home, Sections, Searches, Browses — all answer
    /// <see cref="EntityKind.Unknown"/> (G-041), so a key of (kind, slot) alone would let a Home row and a Browse row
    /// of the same slot number stomp on each other's count. A scope switch replaces every table, and the key is
    /// dropped whole with it (the three <c>s_misses.Clear()</c> call sites in Fetch.cs), so a stale reference never
    /// outlives the rows it counted. Keyed by slot rather than by group: today's miss is always the one or two groups
    /// one plan bucket asked in a shape, so a single small entry per row is enough, and a per-group column beside
    /// <see cref="Table.Asked"/> for a case this rare was not worth Table.cs's own arithmetic (+40,004 B / 10k rows
    /// per <c>uint</c> column, by that file's own accounting). A sealed row's entry is no longer removed the moment it
    /// seals (2026-09-20): dropping it is exactly what let an ordinary re-demand read as a brand new ask and restart
    /// the attempt counter at 1 forever — the live 1→2→3→1 cycle this file's header names.</summary>
    static readonly Dictionary<(Table Table, int Slot), MissState> s_misses = new(16);

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
            uint rowVersion = table.Version[slot];
            s_misses.TryGetValue(key, out MissState prior);

            // THE FIX (2026-09-20): a row already SEALED, whose Version has not moved since (`RevisionChanged`
            // false), is being asked about again for reasons that have nothing to do with a real answer — an
            // ordinary re-demand reached the wire because something un-keyed cleared `Asked`/`Failed` behind this
            // file's back. Reaffirm the marks and log it; do NOT touch the attempt count, or this degenerates back
            // into the exact bug: a sealed row's counter restarting at 1 on every such re-ask, forever.
            if (prior.Sealed && !FetchMissPolicy.RevisionChanged(prior.Version, rowVersion))
            {
                table.Asked[slot] |= miss;
                table.Failed[slot] |= miss;
                table.MarkDirty();
                LogReseal(table.Kind, slot, miss, prior.Attempts);
                continue;
            }

            // Three shapes reach here: a genuinely fresh ask (no prior entry — `before` 0), a retry already under
            // way (not yet sealed — `before` is its running count), or a row that WAS sealed but whose `Version`
            // moved since (a real revision landed — `RevisionChanged` true) and so earns a clean budget exactly
            // like a fresh ask. Only the middle shape keeps a nonzero `before`.
            byte before = prior.Sealed ? (byte)0 : prior.Attempts;
            FetchMissPolicy.Verdict verdict = FetchMissPolicy.Decide(before);
            LogMiss(table.Kind, slot, miss, before + 1);

            if (verdict == FetchMissPolicy.Verdict.Retry)
            {
                s_misses[key] = new MissState((byte)(before + 1), false, rowVersion);
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
                // Sealed exactly as an exhausted `Asked` seal always was — but the row's last Retry (if any) cleared
                // `Asked` for `miss` to let it shimmer through the backoff, and nothing re-set it since (the retry
                // path never runs `Plan`): restore it here, or "leave `Asked` set" would be true of every miss EXCEPT
                // the one that finally seals. Paired with the row twin of a terminal transport failure's mark,
                // `Table.Failed`, so a bound surface paints the glyph `MarkFailed`'s own doc describes instead of
                // silently rendering the group's default columns.
                //
                // Unlike before 2026-09-20, the entry STAYS — `Sealed: true` plus the row's current `Version` — so a
                // later re-ask that finds the row unchanged hits the block above instead of reading as brand new.
                s_misses[key] = new MissState((byte)(before + 1), true, rowVersion);
                table.Asked[slot] |= miss;
                table.Failed[slot] |= miss;
                table.MarkDirty();
                LogSeal(table.Kind, slot, miss, before + 1);
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

    // `fetch.seal kind= slot= groups= attempt=` — the terminal transition the `fetch.miss` retries above lead to: the
    // moment `Table.Failed` becomes durably set for `groups` and a bound surface can paint the failure glyph instead
    // of shimmering. `attempt` is the miss that finally sealed it (always `FetchMissPolicy.MaxMisses + 1`). This line
    // did not exist before 2026-09-20 — the only prior signal was `fetch.miss`'s own `attempt=3`, indistinguishable
    // at a glance from any other retry — so a log could not be grepped for "did this row ever actually seal".
    static void LogSeal(EntityKind kind, int slot, uint groups, int attempt)
    {
        if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
        Log.Event(WaveeLogLevel.Info, "fetch", "fetch.seal", "", null, -1, null,
            WaveeLogField.Of("kind", kind.ToString()),
            WaveeLogField.Of("slot", slot),
            WaveeLogField.Of("groups", Groups(groups)),
            WaveeLogField.Of("attempt", attempt));
    }

    // `fetch.reseal kind= slot= groups= attempts=` — the line that proves this file's 2026-09-20 fix is doing its
    // job: an already-SEALED row was asked about again while its `Table.Version` had not moved (an ordinary
    // re-demand — a table tick, a remount, anything short of a real answer or an explicit Refresh), and the ledger
    // refused to restart it. Before this fix, this exact situation found no `s_misses` entry (the seal removed it)
    // and silently logged `fetch.miss attempt=1` instead — the live signature of the bug, a 1→2→3→1 cycle repeating
    // across ~150 s and two page visits. Seeing `fetch.reseal` instead of a fresh `fetch.miss attempt=1` for the same
    // (kind, slot, groups) is exactly how the fix is verified from the log the bug was found in.
    static void LogReseal(EntityKind kind, int slot, uint groups, int attempts)
    {
        if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
        Log.Event(WaveeLogLevel.Info, "fetch", "fetch.reseal", "", null, -1, null,
            WaveeLogField.Of("kind", kind.ToString()),
            WaveeLogField.Of("slot", slot),
            WaveeLogField.Of("groups", Groups(groups)),
            WaveeLogField.Of("attempts", attempts));
    }
}
