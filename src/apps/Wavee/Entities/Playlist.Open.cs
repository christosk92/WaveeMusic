// ── Entities/Playlist.Open.cs — the list OPEN rule and its model-side state (wave D3, owner U1) ──────────────────────
//
// Role: CORE (ListOpenPolicy, pure) + model state (ListOpen, UI thread) · Spec: cache-integrity-and-playlist-diff-
// implementation.md §2, §3.3 "Freshness". Moved out of Playlist.Page.cs so the fetch layer's answer hook
// (`Fetch.ListSettled`, installed by `Playlist.InstallPages`) never reaches into a page file.

using System.Runtime.InteropServices;
using System.Text;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ══ 6. THE OPEN RULE (wave D3 — cache-integrity-and-playlist-diff-implementation.md §2, §3.3 "Freshness") ═════════════

/// <summary>THE OPEN RULE, AS ASKS. Every surface that shows a playlist's list asks this once when it opens it — the page
/// on mount, the page again when a parked copy comes back, the queue for the list behind "Playing from" — and
/// <see cref="ListOpen"/> acts on the answer: which edge-door call (if any), at what priority, whether the list is stamped
/// revalidated, whether the revalidation is BLOCKING (the baseline it checks may be stale) and whether this surface HOLDS
/// its reveal on it. Then the other half: when a hold lets go (<see cref="Holds"/>), what the model already says about how
/// a revalidation ended (<see cref="Observe"/>, <see cref="Classify"/>) — which is what a rolling identity's header
/// re-ask keys on (<see cref="ListFreshness.ReaskHeader"/>). <see cref="ListFreshness.Decide"/> is the rule; this is its
/// mapping onto the edge door. PURE — no table, no signal, no clock.
/// <para><b>The first open of a list this session is ONE ask.</b> An Unknown list goes through <c>EnsureEdge</c>: the D2
/// disk leg reads it back (Complete, with its revision) and the edge door buckets the network ask itself — which, with a
/// revision now held, IS the <c>/diff</c>. So the open stamps that ask and never adds a <c>RefreshEdge</c> (a refresh skips
/// the disk and would put a second request on the wire for one answer). What lands first is yesterday's copy, so a page
/// holds on it exactly as it holds a stale Complete baseline.</para>
/// <para><b>One revalidation per list at a time.</b> An open that finds one in flight JOINS it: no ask, no stamp, and a
/// page shares its deadline when it is blocking — unless the list has been dirtied since, which asks again.</para></summary>
public static class ListOpenPolicy
{
    /// <summary>An unsettled revalidation older than this is no longer "in flight": it answered without anyone saying so,
    /// or it is stuck behind retries. Either way it stops joining opens, holding anything or standing in for an ask.</summary>
    public const int InFlightMs = 30_000;

    /// <summary>Who opens the list. The only differences are urgency and whether the open may HOLD its reveal.</summary>
    public enum Surface : byte
    {
        /// <summary>The playlist page's mount: the list IS the page. Visible; a blocking revalidation holds the reveal.</summary>
        Page,
        /// <summary>A parked page shown again (KeepAlive, an un-minimize): asks like the page and NEVER holds — its rows
        /// are already on screen, and a hold now would blank what the user is looking at.</summary>
        Revisit,
        /// <summary>The queue's context: the list behind "Playing from" (the queue's own rows are the playback host's).
        /// Prefetch; never holds.</summary>
        Queue,
    }

    /// <summary>The edge-door call an open makes.</summary>
    public enum Ask : byte
    {
        /// <summary>Nothing goes out.</summary>
        None,
        /// <summary><c>Entities.EnsureEdge</c>: the first ask — the disk leg and its chained network ask, or the full read.</summary>
        Ensure,
        /// <summary><c>Entities.RefreshEdge</c>: past the dedupe, with the held revision — the <c>/diff</c>.</summary>
        Refresh,
    }

    /// <summary>What an open knows about its list, read off the model by <see cref="ListOpen"/>.</summary>
    /// <param name="Revisioned">The list has a revision to revalidate against (a Spotify playlist). A seed list or Local
    /// Files has none: it is asked once while nobody has answered, and that is all.</param>
    /// <param name="State">The membership edge's stored state (never Failed).</param>
    /// <param name="DiskLeg">This open's <c>EnsureEdge</c> offers the list to the disk: Unknown, a store is open, and the
    /// disk has not been asked about it this session.</param>
    /// <param name="Dirty">A push said the list moved and it was not applied (<c>ListStamps.IsDirty</c>).</param>
    /// <param name="RevalidatedAtMs">When this session last revalidated it (<c>ListStamps.RevalidatedAtMs</c>; 0 = never).</param>
    /// <param name="Rolling">A rolling identity (<see cref="IsRolling"/>).</param>
    /// <param name="InFlightSinceMs">When the revalidation now in flight was asked; 0 = none.</param>
    /// <param name="InFlightBlocking">That revalidation checks a baseline that may be stale (<see cref="Plan.Blocking"/>).</param>
    public readonly record struct Facts(bool Revisioned, EdgeState State, bool DiskLeg, bool Dirty, int RevalidatedAtMs,
                                        bool Rolling, int InFlightSinceMs = 0, bool InFlightBlocking = false);

    /// <summary>What an open does.</summary>
    /// <param name="Verdict">The freshness rule's answer (for a list with no revision: FullRead while Unknown, else Paint).</param>
    /// <param name="Stamp">A revalidation goes out NOW: stamp the list revalidated (and track the ask until it settles).</param>
    /// <param name="Blocking">That revalidation checks a baseline that may be stale — the verdict was
    /// <see cref="ListFreshness.Verdict.RevalidateThenPaint"/>, or the disk is about to hand back yesterday's copy.</param>
    /// <param name="Hold">This surface keeps its reveal until the revalidation answers or <paramref name="HoldUntilMs"/>
    /// passes, whichever is first.</param>
    public readonly record struct Plan(ListFreshness.Verdict Verdict, Ask Ask, FetchPriority Priority, bool Stamp, bool Blocking,
                                       bool Hold, int HoldUntilMs);

    /// <summary>THE MAPPING.
    /// <list type="bullet">
    /// <item>No revision: the page asks an Unknown list once (<see cref="Ask.Ensure"/>); nothing else, and the queue nothing.</item>
    /// <item>A revalidation in flight and the list not dirtied since: JOIN — no ask, no stamp; a Page holds until the
    /// in-flight ask's own deadline when that ask is blocking.</item>
    /// <item><see cref="ListFreshness.Verdict.FullRead"/>, Unknown: <see cref="Ask.Ensure"/>, stamped; blocking (and a Page
    /// holds) when the disk leg runs. Partial: nothing — a read is already paging, the page's own demand asks the next page.</item>
    /// <item><see cref="ListFreshness.Verdict.Paint"/>: nothing.</item>
    /// <item><see cref="ListFreshness.Verdict.PaintThenRevalidate"/>: <see cref="Ask.Refresh"/> at Prefetch on every
    /// surface, stamped, never held.</item>
    /// <item><see cref="ListFreshness.Verdict.RevalidateThenPaint"/>: <see cref="Ask.Refresh"/> at the surface's urgency
    /// (Visible; the queue Prefetch), stamped, blocking; a Page holds for <see cref="ListFreshness.BlockingBudgetMs"/>.</item>
    /// </list></summary>
    public static Plan Decide(Surface surface, in Facts facts, int nowMs)
    {
        bool holds = surface == Surface.Page;
        FetchPriority urgent = surface == Surface.Queue ? FetchPriority.Prefetch : FetchPriority.Visible;
        if (!facts.Revisioned)
        {
            bool unknown = facts.State == EdgeState.Unknown;
            return new Plan(unknown ? ListFreshness.Verdict.FullRead : ListFreshness.Verdict.Paint,
                            unknown && surface != Surface.Queue ? Ask.Ensure : Ask.None, urgent, false, false, false, 0);
        }

        var verdict = ListFreshness.Decide(facts.State == EdgeState.Complete, facts.Dirty, facts.RevalidatedAtMs, nowMs, facts.Rolling);
        if (facts.InFlightSinceMs != 0 && !facts.Dirty)
        {
            int until = unchecked(facts.InFlightSinceMs + ListFreshness.BlockingBudgetMs);
            bool hold = holds && facts.InFlightBlocking && unchecked(until - nowMs) > 0;
            return new Plan(verdict, Ask.None, urgent, false, facts.InFlightBlocking, hold, hold ? until : 0);
        }

        switch (verdict)
        {
            case ListFreshness.Verdict.Paint:
                return new Plan(verdict, Ask.None, urgent, false, false, false, 0);
            case ListFreshness.Verdict.PaintThenRevalidate:
                return new Plan(verdict, Ask.Refresh, FetchPriority.Prefetch, Stamp: true, false, false, 0);
            case ListFreshness.Verdict.RevalidateThenPaint:
                return Asking(verdict, Ask.Refresh, urgent, blocking: true, holds, nowMs);
            default:
                if (facts.State != EdgeState.Unknown) return new Plan(verdict, Ask.None, urgent, false, false, false, 0);
                return Asking(verdict, Ask.Ensure, urgent, facts.DiskLeg, holds, nowMs);
        }
    }

    static Plan Asking(ListFreshness.Verdict verdict, Ask ask, FetchPriority priority, bool blocking, bool holds, int nowMs)
    {
        bool hold = holds && blocking;
        return new Plan(verdict, ask, priority, Stamp: true, blocking, hold,
                        hold ? unchecked(nowMs + ListFreshness.BlockingBudgetMs) : 0);
    }

    /// <summary>What the model already says about an open revalidation.</summary>
    public enum Observed : byte
    {
        /// <summary>Nothing yet: no answer is visible in the tables.</summary>
        Pending,
        /// <summary>The list or its revision moved past the baseline the revalidation checks.</summary>
        Moved,
        /// <summary>A network page landed (the disk only ever lands a whole list): a full read is answering.</summary>
        Paged,
        /// <summary>The ask failed, or was answered with nothing.</summary>
        Failed,
    }

    /// <summary>Read the model's facts for an open revalidation. <paramref name="baselineSeen"/>: the list was Complete
    /// when the revalidation was asked, or has landed from disk since; <paramref name="moved"/>: its edge version or
    /// revision differs from that baseline's. An "unchanged" answer moves nothing, so it is never visible here — only the
    /// answer path can report it (<see cref="ListOpen.Settled"/>).</summary>
    public static Observed Observe(EdgeState state, bool failed, bool baselineSeen, bool moved)
    {
        if (failed) return Observed.Failed;
        if (state == EdgeState.Partial) return Observed.Paged;
        return baselineSeen && moved ? Observed.Moved : Observed.Pending;
    }

    /// <summary>Is the surface still holding? Only while nothing has been observed AND the deadline has not passed —
    /// "the revalidation answers or the budget elapses, whichever first". One expression of
    /// <see cref="RemainingHoldMs"/>, so the two can never disagree about whether a hold is live.</summary>
    public static bool Holds(bool hold, int holdUntilMs, int nowMs, Observed observed)
        => RemainingHoldMs(hold, holdUntilMs, nowMs, observed) > 0;

    /// <summary>HOW MUCH BUDGET IS LEFT on a hold, in milliseconds — the other half of <see cref="Holds"/>, and the
    /// number a surface arms its OWN deadline with (<c>Playlist.PlaylistPage</c>'s <c>UseTimeout</c>).
    /// <para>A hold ends one of two ways. When the model OBSERVES something — answered, failed, paged, moved — the
    /// record settles and <see cref="ListOpen.Changed"/> is written, so every memo that reads the hold re-runs. The
    /// other way is the BUDGET simply running out, and that edge belongs to the wall clock: nothing settles, nothing
    /// publishes, and <see cref="Holds"/> merely starts answering false the next time somebody happens to ask. A
    /// surface that renders off the hold would keep its shimmer until an unrelated re-render (a window resize) asked
    /// again. This is what lets a surface turn that edge into an EVENT instead: arm a wake for exactly this long, and
    /// call <see cref="ListOpen.ExpireHold"/> when it fires.</para>
    /// <para>ZERO when there is no live hold — not holding, already observed, or the deadline has passed. The deadline
    /// is compared as a DIFFERENCE, so a wrapping millisecond counter never strands a hold nor reports a budget half
    /// the counter's range long.</para></summary>
    public static int RemainingHoldMs(bool hold, int holdUntilMs, int nowMs, Observed observed)
    {
        if (!hold || observed != Observed.Pending) return 0;
        int left = unchecked(holdUntilMs - nowMs);
        return left > 0 ? left : 0;
    }

    /// <summary>The SLACK a surface adds to <see cref="RemainingHoldMs"/> when it arms its wake. The frame clock a
    /// <c>UseTimeout</c> schedules on is not the clock a hold's deadline is written in (<c>ListStamps.NowMs</c>), so a
    /// wake armed at exactly the remaining budget can land a hair BEFORE the deadline — and the no-op that follows
    /// would leave nothing to re-arm it. One frame of slack puts the wake strictly past the deadline.</summary>
    public const int HoldWakeSlackMs = 16;

    /// <summary>How a settled revalidation ended, from the model's facts at the moment it settled:
    /// <list type="bullet">
    /// <item>failed, or answered and still Unknown (nothing landed) ⇒ <see cref="ListFreshness.Outcome.Failed"/>;</item>
    /// <item>no baseline before the answer (the disk had nothing; the answer IS the first list this open saw), or a page
    /// of a paged read ⇒ <see cref="ListFreshness.Outcome.FullRead"/>;</item>
    /// <item>moved past the baseline ⇒ <see cref="ListFreshness.Outcome.Replayed"/> — a replay and a re-read over a held
    /// baseline look the same from here, and counting it as the replay errs toward the rolling header re-ask (one batched
    /// header row) rather than leaving a daylist's title an edition behind;</item>
    /// <item>otherwise ⇒ <see cref="ListFreshness.Outcome.Unchanged"/>.</item>
    /// </list></summary>
    public static ListFreshness.Outcome Classify(bool failed, EdgeState state, bool baselineSeen, bool moved)
    {
        if (failed || state == EdgeState.Unknown) return ListFreshness.Outcome.Failed;
        if (!baselineSeen || state == EdgeState.Partial) return ListFreshness.Outcome.FullRead;
        return moved ? ListFreshness.Outcome.Replayed : ListFreshness.Outcome.Unchanged;
    }

    /// <summary>A ROLLING identity (plan §2): a daylist — by its format, or by the rollover window only a daylist carries
    /// (<see cref="PlaylistFields.Format"/> is never persisted, so the window alone must also count).</summary>
    public static bool IsRolling(PlaylistFormat format, int daylistExpiresAt)
        => format == PlaylistFormat.Daylist || daylistExpiresAt > 0;

    /// <summary>Is a revalidation asked at <paramref name="askedAtMs"/> still in flight at <paramref name="nowMs"/>
    /// (<see cref="InFlightMs"/>)? A clock that went backwards reads as not.</summary>
    public static bool StillInFlight(int askedAtMs, int nowMs)
    {
        int age = unchecked(nowMs - askedAtMs);
        return age >= 0 && age < InFlightMs;
    }
}

/// <summary>THE OPEN REVALIDATIONS — model-side state (wave D3). One record per list whose open sent a revalidation this
/// session and that has not settled: when it was asked, whether it is blocking, whether a surface HOLDS its reveal on it
/// and until when, and the baseline it checks (edge version + revision, captured at the ask — or when the disk leg lands
/// yesterday's copy). This is the fact the page's reveal gate reads (<see cref="Holding"/>, through the page's table
/// source, <c>Playlist.HeldRows</c>) — "revalidating until &lt;deadline&gt;" — so no surface probes tables for it.
/// <para>A record SETTLES — and the list's rolling header is re-asked when <see cref="ListFreshness.ReaskHeader"/> says
/// so — when the answer path reports it (<see cref="Settled"/>: the edge door's answer or terminal failure for the parent),
/// or when a surface's effect sees the model already say how it ended (<see cref="Observe"/>: the list moved, a page of a
/// full read landed, the ask failed). The hold lets go on either — or at its DEADLINE, which is a fact no table
/// publishes, so the holding surface arms a wake for <see cref="RemainingHoldMs"/> on its own frame clock and calls
/// <see cref="ExpireHold"/> when it fires. All three settle the record and bump <see cref="Changed"/>.</para>
/// <para>Memory-only and per scope (a record from another epoch, or for a recycled slot, is dropped when met), bounded by
/// <see cref="ListOpenPolicy.InFlightMs"/>. UI THREAD ONLY (C1); nothing here allocates after warm-up except the uri a
/// Spotify list's stamps are keyed by and the always-on <c>list.*</c> lines.</para></summary>
public static class ListOpen
{
    struct Entry
    {
        public int Slot;
        public EntityId Id;
        public uint Epoch;
        public int AskedAtMs;
        public bool Blocking;
        public bool Hold;
        public int HoldUntilMs;
        public FetchPriority Priority;  // the most urgent surface that opened it: the rolling header re-asks at this
        public bool BaseSeen;
        public uint BaseVersion;
        public StringId BaseRevision;
    }

    static Entry[] s_entries = new Entry[8];
    static int s_count;

    /// <summary>Bumps when a record is taken, joined, settled, dropped or its hold runs out of budget. Read it (tracked)
    /// wherever <see cref="Holding"/> is read.</summary>
    public static readonly Signal<uint> Changed = new(0);

    /// <summary>OPEN a playlist's list from <paramref name="surface"/>: read the facts, ask <see cref="ListOpenPolicy.Decide"/>,
    /// make the edge-door call it names, stamp the list, and record (or join) the revalidation. UI thread, from an EFFECT
    /// (it writes <see cref="Changed"/> and plans fetches) — never from a render.</summary>
    public static void Open(Playlist pl, ListOpenPolicy.Surface surface)
    {
        var scope = Entities.Current;
        if (scope is null || !pl.IsValid) return;
        int now = ListStamps.NowMs();
        Sweep(scope, now);
        int slot = pl.Slot;
        int at = IndexOf(slot);
        string? uri = pl.Id.Provider == EntityProvider.Spotify ? pl.Uri.Text : null;
        var facts = FactsOf(scope, pl, uri, at);
        var plan = ListOpenPolicy.Decide(surface, in facts, now);

        switch (plan.Ask)
        {
            case ListOpenPolicy.Ask.Ensure: Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot, 0, plan.Priority); break;
            case ListOpenPolicy.Ask.Refresh: Entities.RefreshEdge(FetchEdge.PlaylistTracks, slot, plan.Priority); break;
        }

        bool changed = false;
        if (plan.Stamp && uri is not null)
        {
            ListStamps.MarkRevalidated(uri, now);
            if (at >= 0) RemoveAt(at);                     // superseded: a dirtied list asks again past an in-flight one
            var edges = scope.Edges.PlaylistTracks;
            bool complete = facts.State == EdgeState.Complete;
            Add(new Entry
            {
                Slot = slot, Id = pl.Id, Epoch = scope.Epoch, AskedAtMs = now,
                Blocking = plan.Blocking, Hold = plan.Hold, HoldUntilMs = plan.HoldUntilMs, Priority = plan.Priority,
                BaseSeen = complete, BaseVersion = complete ? edges.Version(slot) : 0u,
                BaseRevision = complete ? pl.RevisionId : StringId.Empty,
            });
            changed = true;
        }
        else if (at >= 0)
        {
            ref Entry e = ref s_entries[at];              // JOINED: the in-flight revalidation answers for this open too
            if (plan.Priority > e.Priority) e.Priority = plan.Priority;
            if (plan.Hold && !(e.Hold && e.HoldUntilMs == plan.HoldUntilMs))
            {
                e.Hold = true;
                e.HoldUntilMs = plan.HoldUntilMs;
                changed = true;
            }
        }
        if (changed) Bump();
        LogOpen(uri, surface, in facts, in plan, joined: !plan.Stamp && at >= 0, now);
    }

    /// <summary>Would an open from <paramref name="surface"/> hold this list right now? The same pure rule over the same
    /// facts as <see cref="Open"/>, and nothing written — what a surface's FIRST frame reads, because it renders before
    /// its demand effect has opened anything. A read, never a subscription.</summary>
    public static bool WouldHold(Playlist pl, ListOpenPolicy.Surface surface)
    {
        var scope = Entities.Current;
        if (scope is null || !pl.IsValid) return false;
        int now = ListStamps.NowMs();
        int at = IndexOf(pl.Slot);
        if (at >= 0 && (!Live(in s_entries[at], scope) || !ListOpenPolicy.StillInFlight(s_entries[at].AskedAtMs, now))) at = -1;
        string? uri = pl.Id.Provider == EntityProvider.Spotify ? pl.Uri.Text : null;
        var facts = FactsOf(scope, pl, uri, at);
        return ListOpenPolicy.Decide(surface, in facts, now).Hold;
    }

    /// <summary>THE FACT THE REVEAL GATE READS: is a surface holding this list's reveal on its open revalidation — not
    /// yet answered as far as the model can tell, and inside its budget? A read, never a subscription: read
    /// <see cref="Changed"/> (and the membership / playlist tables' signals) in the same tracked scope.</summary>
    public static bool Holding(int slot) => RemainingHoldMs(slot) > 0;

    /// <summary>HOW LONG THIS SLOT'S HOLD HAS LEFT (0 when it is not holding) — what a surface arms its own deadline
    /// wake with, so the budget edge becomes an event (<see cref="ListOpenPolicy.RemainingHoldMs"/>). The same record,
    /// the same liveness check and the same <see cref="ListOpenPolicy.Observed"/> reading <see cref="Holding"/> makes —
    /// <see cref="Holding"/> IS this, compared against zero, so the two can never disagree about whether a hold is
    /// live. A read, never a subscription: read <see cref="Changed"/> in the same tracked scope.</summary>
    public static int RemainingHoldMs(int slot)
    {
        if (s_count == 0) return 0;
        int at = IndexOf(slot);
        if (at < 0) return 0;
        ref readonly Entry e = ref s_entries[at];
        if (!e.Hold) return 0;
        var scope = Entities.Current;
        if (scope is null || !Live(in e, scope)) return 0;
        var edges = scope.Edges.PlaylistTracks;
        var observed = ListOpenPolicy.Observe(edges.State(slot), edges.IsFailed(slot), e.BaseSeen, Moved(in e, scope));
        return ListOpenPolicy.RemainingHoldMs(e.Hold, e.HoldUntilMs, ListStamps.NowMs(), observed);
    }

    /// <summary>END A HOLD BECAUSE ITS BUDGET ELAPSED — the surface's deadline wake calling in (the playlist page's
    /// <c>UseTimeout</c>, armed for <see cref="RemainingHoldMs"/>). This is the one way a hold can end that no table
    /// publishes, so it is settled EXPLICITLY, through the same <see cref="Settle"/> path an observed or answered
    /// revalidation takes: the record leaves, <see cref="Changed"/> is written, and every memo that reads the hold
    /// re-runs — which is what flips the meta line's arm off <c>Loading</c> with no resize and no pointer input. The
    /// always-on <c>list.settle</c> line names it <c>budget</c>, beside <c>observed</c> / <c>answered</c> /
    /// <c>failed</c>.
    /// <para>A NO-OP when the slot has no live hold, when the model can already see how the revalidation ended (that
    /// is <see cref="Observe"/>'s settle, and it names how), or when the budget has not actually elapsed — so a wake
    /// that lands a frame early, or late over a hold taken since, settles nothing. UI thread.</para></summary>
    public static void ExpireHold(int slot)
    {
        if (s_count == 0) return;
        int at = IndexOf(slot);
        if (at < 0) return;
        var scope = Entities.Current;
        if (scope is null) return;
        if (!Live(in s_entries[at], scope)) { Drop(at); return; }
        ref readonly Entry e = ref s_entries[at];
        if (!e.Hold) return;
        var edges = scope.Edges.PlaylistTracks;
        EdgeState state = edges.State(slot);
        bool failed = edges.IsFailed(slot), moved = Moved(in e, scope);
        var observed = ListOpenPolicy.Observe(state, failed, e.BaseSeen, moved);
        if (observed != ListOpenPolicy.Observed.Pending) return;
        if (ListOpenPolicy.RemainingHoldMs(e.Hold, e.HoldUntilMs, ListStamps.NowMs(), observed) > 0) return;
        Settle(at, ListOpenPolicy.Classify(failed, state, e.BaseSeen, moved), "budget");
    }

    /// <summary>A surface's half of its list's open revalidation, run from an auto-tracked EFFECT: subscribes the effect to
    /// what can settle it — the membership and playlist tables, where the disk landing, a replay, a page of a full read
    /// and a failure all publish — captures the baseline when the disk leg lands yesterday's copy, and settles the record
    /// as soon as the model says how it ended (<see cref="ListOpenPolicy.Observe"/>). Deliberately NOT a reader of
    /// <see cref="Changed"/>, which a settle writes: the effect would re-trigger itself (a backwards write). Nothing to do
    /// — one lookup — when the list has no open revalidation.</summary>
    public static void Observe(int slot)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        if (scope is null) return;
        var edges = scope.Edges.PlaylistTracks;
        _ = edges.Changed.Value;
        _ = scope.Playlists.Changed.Value;
        int at = IndexOf(slot);
        if (at < 0) return;
        if (!Live(in s_entries[at], scope)) { Drop(at); return; }
        ref Entry e = ref s_entries[at];
        EdgeState state = edges.State(slot);
        if (!e.BaseSeen && state == EdgeState.Complete)
        {
            // The disk leg landed yesterday's copy: that is the baseline the chained /diff answers about.
            e.BaseSeen = true;
            e.BaseVersion = edges.Version(slot);
            e.BaseRevision = new Playlist(slot).RevisionId;
            return;
        }
        bool failed = edges.IsFailed(slot), moved = Moved(in e, scope);
        if (ListOpenPolicy.Observe(state, failed, e.BaseSeen, moved) == ListOpenPolicy.Observed.Pending) return;
        Settle(at, ListOpenPolicy.Classify(failed, state, e.BaseSeen, moved), "observed");
    }

    /// <summary>THE ANSWER PATH'S REPORT: the edge door settled <paramref name="edge"/> for <paramref name="parent"/> —
    /// answered (<paramref name="failed"/> false) or failed terminally. For a playlist's membership with an open
    /// revalidation, this is the one place an UNCHANGED answer becomes visible (it moves nothing in the tables), so it
    /// is what lets a hold go the moment the <c>/diff</c> says "unchanged". Any other edge, or a list with no open
    /// revalidation, costs one compare. UI thread (the edge door's <c>EdgesAnswered</c> / <c>EdgesFailed</c>).</summary>
    public static void Settled(FetchEdge edge, int parent, bool failed)
    {
        if (edge != FetchEdge.PlaylistTracks || s_count == 0) return;
        int at = IndexOf(parent);
        if (at < 0) return;
        var scope = Entities.Current;
        if (scope is null) return;
        if (!Live(in s_entries[at], scope)) { Drop(at); return; }
        ref readonly Entry e = ref s_entries[at];
        var outcome = ListOpenPolicy.Classify(failed, scope.Edges.PlaylistTracks.State(parent), e.BaseSeen, Moved(in e, scope));
        Settle(at, outcome, failed ? "failed" : "answered");
    }

    // ── the records ─────────────────────────────────────────────────────────────────────────────────────────────────

    static ListOpenPolicy.Facts FactsOf(Scope scope, Playlist pl, string? uri, int at)
    {
        var edges = scope.Edges.PlaylistTracks;
        int slot = pl.Slot;
        EdgeState state = edges.State(slot);
        return new ListOpenPolicy.Facts(
            Revisioned: uri is not null,
            State: state,
            DiskLeg: state == EdgeState.Unknown && Store.IsOpen && !edges.WasDiskAsked(slot),
            Dirty: uri is not null && ListStamps.IsDirty(uri),
            RevalidatedAtMs: uri is not null ? ListStamps.RevalidatedAtMs(uri) : 0,
            Rolling: ListOpenPolicy.IsRolling(pl.Format, pl.DaylistExpiresAt),
            InFlightSinceMs: at >= 0 ? s_entries[at].AskedAtMs : 0,
            InFlightBlocking: at >= 0 && s_entries[at].Blocking);
    }

    /// <summary>Settle the record at <paramref name="at"/>: it leaves, readers re-read, and a rolling identity whose
    /// revalidation did not re-read the list re-asks its header (the rollover's edition groups) at the urgency of the
    /// surface that opened it. The caller has checked the record is live.</summary>
    static void Settle(int at, ListFreshness.Outcome outcome, string how)
    {
        Entry e = s_entries[at];
        RemoveAt(at);
        Bump();
        var pl = new Playlist(e.Slot);
        bool reask = ListFreshness.ReaskHeader(ListOpenPolicy.IsRolling(pl.Format, pl.DaylistExpiresAt), outcome);
        if (reask) Entities.Invalidate(pl, DaylistRollover.Groups, e.Priority);
        if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
        Log.Event(WaveeLogLevel.Info, "list", "list.settle", "", null, -1, null,
            WaveeLogField.Of("uri", pl.Uri.Text),
            WaveeLogField.Of("outcome", outcome.ToString()),
            WaveeLogField.Of("how", how),
            WaveeLogField.Of("ms", unchecked(ListStamps.NowMs() - e.AskedAtMs)),
            WaveeLogField.Of("held", e.Hold),
            WaveeLogField.Of("reask", reask));
    }

    static bool Live(in Entry e, Scope scope)
        => e.Epoch == scope.Epoch && e.Slot > Table.None && e.Slot < scope.Playlists.Count && scope.Playlists.Id[e.Slot] == e.Id;

    static bool Moved(in Entry e, Scope scope)
        => e.BaseSeen && (scope.Edges.PlaylistTracks.Version(e.Slot) != e.BaseVersion || scope.Playlists.Revision[e.Slot] != e.BaseRevision);

    /// <summary>Drop what can no longer answer for anything: another scope's records, recycled slots, and revalidations
    /// past <see cref="ListOpenPolicy.InFlightMs"/>.</summary>
    static void Sweep(Scope scope, int now)
    {
        bool dropped = false;
        for (int i = s_count - 1; i >= 0; i--)
        {
            if (Live(in s_entries[i], scope) && ListOpenPolicy.StillInFlight(s_entries[i].AskedAtMs, now)) continue;
            RemoveAt(i);
            dropped = true;
        }
        if (dropped) Bump();
    }

    static int IndexOf(int slot)
    {
        for (int i = 0; i < s_count; i++)
            if (s_entries[i].Slot == slot) return i;
        return -1;
    }

    static void Add(in Entry entry)
    {
        if (s_count == s_entries.Length) Array.Resize(ref s_entries, s_entries.Length * 2);
        s_entries[s_count++] = entry;
    }

    /// <summary>Order is not kept: the last record moves into the hole.</summary>
    static void RemoveAt(int at)
    {
        s_entries[at] = s_entries[--s_count];
        s_entries[s_count] = default;
    }

    static void Drop(int at)
    {
        RemoveAt(at);
        Bump();
    }

    static void Bump() => Changed.Value = Changed.Peek() + 1;

    // ── the budget ──────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // There is no background timer here, and that is deliberate. A hold exists for exactly one reason — a SURFACE is
    // holding its reveal on it — and only a surface can act on the budget running out: it is the thing that has to
    // re-render. So the deadline is armed where the hold is read, on the SAME frame clock the surface paints with
    // (Playlist.PlaylistPage: UseTimeout for ListOpen.RemainingHoldMs, firing ListOpen.ExpireHold), and the settle it
    // produces goes through Changed like every other. A thread-pool timer posting into the UI queue could mark the
    // record spent without anything re-rendering, which is the eternal shimmer this replaced.

    static void LogOpen(string? uri, ListOpenPolicy.Surface surface, in ListOpenPolicy.Facts facts, in ListOpenPolicy.Plan plan,
                        bool joined, int now)
    {
        if (uri is null || !Log.IsEnabled(WaveeLogLevel.Info)) return;
        Log.Event(WaveeLogLevel.Info, "list", "list.open", "", null, -1, null,
            WaveeLogField.Of("uri", uri),
            WaveeLogField.Of("surface", surface.ToString()),
            WaveeLogField.Of("state", facts.State.ToString()),
            WaveeLogField.Of("verdict", plan.Verdict.ToString()),
            WaveeLogField.Of("ask", plan.Ask.ToString()),
            WaveeLogField.Of("prio", plan.Priority.ToString()),
            WaveeLogField.Of("disk", facts.DiskLeg),
            WaveeLogField.Of("dirty", facts.Dirty),
            WaveeLogField.Of("joined", joined),
            WaveeLogField.Of("holdMs", plan.Hold ? unchecked(plan.HoldUntilMs - now) : 0));
    }
}
