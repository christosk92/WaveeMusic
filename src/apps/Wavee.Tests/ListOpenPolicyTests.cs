// ── Wavee.Tests/ListOpenPolicyTests.cs — the open rule as asks: which door, what priority, stamp, hold (plan §2, §3.3) ─
//
// ListFreshness.Decide is the rule (ListFreshnessTests); these pin its mapping onto the edge door that the playlist page,
// a revisited page and the queue's context all go through (ListOpen.Open): every verdict → the right ask + priority +
// hold, the hold lets go at the budget or on the first answer the model can see, a rolling identity re-asks its header
// after a non-refetch outcome, and the first open of a session — the D2 disk leg with the /diff the edge door chains
// itself — is ONE ask.

using Wavee;
using Xunit;

namespace Wavee.Tests;

using O = Wavee.ListFreshness.Outcome;
using P = Wavee.ListOpenPolicy;
using V = Wavee.ListFreshness.Verdict;

public class ListOpenPolicyTests
{
    const int Now = 50_000_000;
    const int Budget = ListFreshness.BlockingBudgetMs;

    /// <summary>A Spotify list held Complete.</summary>
    static P.Facts Held(bool dirty = false, int revalidatedAt = 0, bool rolling = false, int inFlightSince = 0, bool inFlightBlocking = false)
        => new(Revisioned: true, EdgeState.Complete, DiskLeg: false, dirty, revalidatedAt, rolling, inFlightSince, inFlightBlocking);

    /// <summary>A Spotify list nobody has answered this session.</summary>
    static P.Facts Unknown(bool diskLeg)
        => new(Revisioned: true, EdgeState.Unknown, diskLeg, Dirty: false, RevalidatedAtMs: 0, Rolling: false);

    // ── the verdicts, as asks ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(P.Surface.Page, FetchPriority.Visible, true)]
    [InlineData(P.Surface.Revisit, FetchPriority.Visible, false)]
    [InlineData(P.Surface.Queue, FetchPriority.Prefetch, false)]
    public void An_unknown_list_is_one_ensure_that_restores_it_from_disk_and_only_a_page_holds_on_it(P.Surface surface, FetchPriority priority, bool hold)
        => Assert.Equal(new P.Plan(V.FullRead, P.Ask.Ensure, priority, Stamp: true, Blocking: true, hold, hold ? Now + Budget : 0),
                        P.Decide(surface, Unknown(diskLeg: true), Now));

    [Fact]
    public void An_unknown_list_the_disk_will_not_answer_is_a_plain_full_read_with_nothing_stale_to_hold_against()
        => Assert.Equal(new P.Plan(V.FullRead, P.Ask.Ensure, FetchPriority.Visible, Stamp: true, Blocking: false, Hold: false, HoldUntilMs: 0),
                        P.Decide(P.Surface.Page, Unknown(diskLeg: false), Now));

    [Theory]
    [InlineData(P.Surface.Page, FetchPriority.Visible, true)]
    [InlineData(P.Surface.Revisit, FetchPriority.Visible, false)]
    [InlineData(P.Surface.Queue, FetchPriority.Prefetch, false)]
    public void A_list_not_revalidated_this_session_is_asked_its_diff_first_and_only_a_page_holds(P.Surface surface, FetchPriority priority, bool hold)
        => Assert.Equal(new P.Plan(V.RevalidateThenPaint, P.Ask.Refresh, priority, Stamp: true, Blocking: true, hold, hold ? Now + Budget : 0),
                        P.Decide(surface, Held(revalidatedAt: 0), Now));

    [Fact]
    public void A_dirtied_list_is_asked_its_diff_first_even_inside_the_window()
        => Assert.Equal(new P.Plan(V.RevalidateThenPaint, P.Ask.Refresh, FetchPriority.Visible, true, true, true, Now + Budget),
                        P.Decide(P.Surface.Page, Held(dirty: true, revalidatedAt: Now - 1_000), Now));

    [Theory]
    [InlineData(P.Surface.Page)]
    [InlineData(P.Surface.Revisit)]
    [InlineData(P.Surface.Queue)]
    public void Inside_the_window_a_clean_list_paints_and_nothing_is_asked(P.Surface surface)
    {
        var plan = P.Decide(surface, Held(revalidatedAt: Now - 1_000), Now);
        Assert.Equal(V.Paint, plan.Verdict);
        Assert.Equal(P.Ask.None, plan.Ask);
        Assert.False(plan.Stamp);
        Assert.False(plan.Hold);
    }

    [Theory]
    [InlineData(P.Surface.Page)]
    [InlineData(P.Surface.Revisit)]
    [InlineData(P.Surface.Queue)]
    public void Past_the_window_a_list_the_dealer_never_dirtied_paints_and_revalidates_behind_at_prefetch(P.Surface surface)
        => Assert.Equal(new P.Plan(V.PaintThenRevalidate, P.Ask.Refresh, FetchPriority.Prefetch, Stamp: true, Blocking: false, Hold: false, HoldUntilMs: 0),
                        P.Decide(surface, Held(revalidatedAt: Now - ListFreshness.WindowMs), Now));

    [Fact]
    public void Past_the_window_a_rolling_identity_is_asked_first_and_the_page_holds()
        => Assert.Equal(new P.Plan(V.RevalidateThenPaint, P.Ask.Refresh, FetchPriority.Visible, true, true, true, Now + Budget),
                        P.Decide(P.Surface.Page, Held(revalidatedAt: Now - ListFreshness.WindowMs, rolling: true), Now));

    [Theory]
    [InlineData(P.Surface.Page)]
    [InlineData(P.Surface.Queue)]
    public void A_partial_list_is_already_being_read_and_the_open_adds_nothing(P.Surface surface)
    {
        var plan = P.Decide(surface, new P.Facts(Revisioned: true, EdgeState.Partial, DiskLeg: false, Dirty: false, RevalidatedAtMs: 0, Rolling: false), Now);
        Assert.Equal(P.Ask.None, plan.Ask);
        Assert.False(plan.Stamp);
        Assert.False(plan.Hold);
    }

    [Theory]
    [InlineData(P.Surface.Page, EdgeState.Unknown, P.Ask.Ensure)]
    [InlineData(P.Surface.Revisit, EdgeState.Unknown, P.Ask.Ensure)]
    [InlineData(P.Surface.Queue, EdgeState.Unknown, P.Ask.None)]
    [InlineData(P.Surface.Page, EdgeState.Complete, P.Ask.None)]
    public void A_list_with_no_revision_is_asked_once_by_a_page_and_never_stamped_or_held(P.Surface surface, EdgeState state, P.Ask ask)
    {
        var plan = P.Decide(surface, new P.Facts(Revisioned: false, state, DiskLeg: true, Dirty: false, RevalidatedAtMs: 0, Rolling: false), Now);
        Assert.Equal(ask, plan.Ask);
        Assert.False(plan.Stamp);
        Assert.False(plan.Hold);
    }

    // ── one revalidation per list at a time ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_list_restored_from_disk_on_its_first_open_is_asked_exactly_once()
    {
        // The first open: Unknown, never offered to the disk — ONE EnsureEdge (the disk leg, then the /diff the edge door
        // chains itself). Never a Refresh: that skips the disk AND puts a second request on the wire.
        var first = P.Decide(P.Surface.Page, Unknown(diskLeg: true), Now);
        Assert.Equal(P.Ask.Ensure, first.Ask);
        Assert.True(first.Stamp);

        // The disk landed yesterday's copy (Complete) while that /diff is still out: every other open joins it.
        var landed = new P.Facts(Revisioned: true, EdgeState.Complete, DiskLeg: false, Dirty: false, RevalidatedAtMs: Now,
                                 Rolling: false, InFlightSinceMs: Now, InFlightBlocking: first.Blocking);
        foreach (var surface in new[] { P.Surface.Page, P.Surface.Revisit, P.Surface.Queue })
        {
            var again = P.Decide(surface, landed, Now + 200);
            Assert.Equal(P.Ask.None, again.Ask);
            Assert.False(again.Stamp);
        }

        // Settled: the stamp keeps the window, and a reopen paints without asking.
        var settled = landed with { InFlightSinceMs = 0, InFlightBlocking = false };
        Assert.Equal(new P.Plan(V.Paint, P.Ask.None, FetchPriority.Visible, false, false, false, 0),
                     P.Decide(P.Surface.Page, settled, Now + 10_000));
    }

    [Fact]
    public void A_page_opened_while_a_blocking_revalidation_is_out_holds_until_that_revalidations_own_deadline()
        => Assert.Equal(new P.Plan(V.Paint, P.Ask.None, FetchPriority.Visible, Stamp: false, Blocking: true, Hold: true, HoldUntilMs: Now - 300 + Budget),
                        P.Decide(P.Surface.Page, Held(revalidatedAt: Now - 300, inFlightSince: Now - 300, inFlightBlocking: true), Now));

    [Fact]
    public void Joining_a_revalidation_that_is_not_blocking_or_past_its_budget_holds_nothing()
    {
        Assert.False(P.Decide(P.Surface.Page, Held(revalidatedAt: Now - 300, inFlightSince: Now - 300, inFlightBlocking: false), Now).Hold);
        var late = P.Decide(P.Surface.Page, Held(revalidatedAt: Now - Budget, inFlightSince: Now - Budget, inFlightBlocking: true), Now);
        Assert.Equal(P.Ask.None, late.Ask);
        Assert.False(late.Hold);
    }

    [Fact]
    public void A_list_dirtied_since_its_revalidation_went_out_asks_again_instead_of_joining()
        => Assert.Equal(new P.Plan(V.RevalidateThenPaint, P.Ask.Refresh, FetchPriority.Visible, true, true, true, Now + Budget),
                        P.Decide(P.Surface.Page, Held(dirty: true, revalidatedAt: Now - 300, inFlightSince: Now - 300), Now));

    [Fact]
    public void A_revalidation_stops_counting_as_in_flight_at_its_horizon()
    {
        Assert.True(P.StillInFlight(Now, Now));
        Assert.True(P.StillInFlight(Now - P.InFlightMs + 1, Now));
        Assert.False(P.StillInFlight(Now - P.InFlightMs, Now));
        Assert.False(P.StillInFlight(Now + 5, Now));          // a clock that went backwards
    }

    // ── the hold ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_hold_lets_go_at_the_budget_whatever_the_answer_is_doing()
    {
        var plan = P.Decide(P.Surface.Page, Held(), Now);
        Assert.True(P.Holds(plan.Hold, plan.HoldUntilMs, Now, P.Observed.Pending));
        Assert.True(P.Holds(plan.Hold, plan.HoldUntilMs, Now + Budget - 1, P.Observed.Pending));
        Assert.False(P.Holds(plan.Hold, plan.HoldUntilMs, Now + Budget, P.Observed.Pending));
        Assert.False(P.Holds(plan.Hold, plan.HoldUntilMs, Now + Budget + 60_000, P.Observed.Pending));
    }

    [Theory]
    [InlineData(P.Observed.Moved)]
    [InlineData(P.Observed.Paged)]
    [InlineData(P.Observed.Failed)]
    public void The_hold_lets_go_the_moment_an_answer_is_visible(P.Observed observed)
        => Assert.False(P.Holds(hold: true, Now + Budget, Now, observed));

    [Fact]
    public void An_open_that_took_no_hold_never_holds()
        => Assert.False(P.Holds(hold: false, Now + Budget, Now, P.Observed.Pending));

    [Fact]
    public void The_hold_survives_the_millisecond_counter_wrapping()
    {
        int start = int.MaxValue - 500;
        var plan = P.Decide(P.Surface.Page, Held(), start);
        Assert.True(plan.Hold);
        Assert.True(P.Holds(plan.Hold, plan.HoldUntilMs, unchecked(start + 1_000), P.Observed.Pending));
        Assert.False(P.Holds(plan.Hold, plan.HoldUntilMs, unchecked(start + Budget), P.Observed.Pending));
    }

    // ── how a revalidation ended ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EdgeState.Complete, false, true, false, P.Observed.Pending)]   // an unchanged answer moves nothing: invisible here
    [InlineData(EdgeState.Complete, false, true, true, P.Observed.Moved)]
    [InlineData(EdgeState.Complete, false, false, false, P.Observed.Pending)]  // the disk just landed: no baseline to move from yet
    [InlineData(EdgeState.Unknown, false, false, false, P.Observed.Pending)]   // the disk read or the full read is still out
    [InlineData(EdgeState.Partial, false, false, false, P.Observed.Paged)]     // the disk never lands a page: the network does
    [InlineData(EdgeState.Unknown, true, false, false, P.Observed.Failed)]
    [InlineData(EdgeState.Complete, true, true, false, P.Observed.Failed)]
    public void What_the_model_already_says_about_an_open_revalidation(EdgeState state, bool failed, bool baseline, bool moved, P.Observed expected)
        => Assert.Equal(expected, P.Observe(state, failed, baseline, moved));

    [Theory]
    [InlineData(true, EdgeState.Complete, true, false, O.Failed)]
    [InlineData(false, EdgeState.Unknown, false, false, O.Failed)]       // answered with nothing (no route)
    [InlineData(false, EdgeState.Complete, false, false, O.FullRead)]    // the disk had nothing: the answer is the first list seen
    [InlineData(false, EdgeState.Partial, true, true, O.FullRead)]
    [InlineData(false, EdgeState.Complete, true, true, O.Replayed)]      // moved past a held baseline counts as the replay
    [InlineData(false, EdgeState.Complete, true, false, O.Unchanged)]
    public void How_a_settled_revalidation_ended(bool failed, EdgeState state, bool baseline, bool moved, O expected)
        => Assert.Equal(expected, P.Classify(failed, state, baseline, moved));

    [Fact]
    public void A_rolling_identity_re_asks_its_header_after_an_unchanged_or_replayed_answer_and_never_after_a_re_read_or_a_failure()
    {
        bool daylist = P.IsRolling(PlaylistFormat.Daylist, 0);
        Assert.True(ListFreshness.ReaskHeader(daylist, P.Classify(false, EdgeState.Complete, true, false)));    // unchanged
        Assert.True(ListFreshness.ReaskHeader(daylist, P.Classify(false, EdgeState.Complete, true, true)));     // replayed
        Assert.False(ListFreshness.ReaskHeader(daylist, P.Classify(false, EdgeState.Complete, false, false)));  // a first read carries its header
        Assert.False(ListFreshness.ReaskHeader(daylist, P.Classify(false, EdgeState.Partial, true, false)));
        Assert.False(ListFreshness.ReaskHeader(daylist, P.Classify(true, EdgeState.Complete, true, false)));
        Assert.False(ListFreshness.ReaskHeader(P.IsRolling(PlaylistFormat.Editorial, 0), P.Classify(false, EdgeState.Complete, true, false)));
    }

    [Theory]
    [InlineData(PlaylistFormat.Daylist, 0, true)]
    [InlineData(PlaylistFormat.None, 1_800_000_000, true)]   // Format is never persisted: after a relaunch the window alone says so
    [InlineData(PlaylistFormat.DailyMix, 0, false)]
    [InlineData(PlaylistFormat.None, 0, false)]
    public void A_rolling_identity_is_a_daylist(PlaylistFormat format, int expiresAt, bool rolling)
        => Assert.Equal(rolling, P.IsRolling(format, expiresAt));

    // ── the open is itself an event: the two rules either side of it disagree ────────────────────────────────────────
    //
    // A surface's reveal source answers "is my list held?" by a DIFFERENT rule either side of its own open: BEFORE, by
    // re-deciding the plan (ListOpen.WouldHold — a page's first frame renders before its demand effect has opened
    // anything); AFTER, by what the recorded revalidation says (ListOpen.Holding). These pin that the two are not
    // interchangeable — the open CHANGES the answer, which is why a source must treat its own open as a tracked event
    // and not as a plain field a memo cannot see (Playlist.HeldRows).

    /// <summary>The seam: the pre-open rule re-decides the PLAN and never looks at what the model has already observed,
    /// while the post-open rule lets go the moment an answer is visible. Same record, same instant, opposite answers.</summary>
    [Theory]
    [InlineData(P.Observed.Moved)]
    [InlineData(P.Observed.Paged)]
    [InlineData(P.Observed.Failed)]
    public void An_answer_the_model_can_already_see_ends_the_hold_that_a_re_decide_would_still_take(P.Observed observed)
    {
        var take = P.Decide(P.Surface.Page, Held(revalidatedAt: 0), Now);                 // the record this page's earlier visit took
        int later = Now + 200;                                                            // re-mounted inside the budget

        // BEFORE this page's open: WouldHold re-decides over the in-flight record and still says hold.
        var reDecided = P.Decide(P.Surface.Page, Held(revalidatedAt: Now, inFlightSince: Now, inFlightBlocking: take.Blocking), later);
        Assert.True(reDecided.Hold);

        // AFTER it: Holding reads the record, and the observed answer has already ended the hold.
        Assert.False(P.Holds(take.Hold, take.HoldUntilMs, later, observed));
        Assert.NotEqual(reDecided.Hold, P.Holds(take.Hold, take.HoldUntilMs, later, observed));

        // Nothing has answered yet ⇒ they agree, which is what makes the disagreement above a real edge and not noise.
        Assert.Equal(reDecided.Hold, P.Holds(take.Hold, take.HoldUntilMs, later, P.Observed.Pending));
    }

    /// <summary>…and the open that crosses that seam writes NOTHING: it joins a record whose hold is byte-identical to
    /// the one it would take, so no ask goes out, no stamp is made and the record does not move. There is no model-side
    /// change for a reader to notice — the surface's own open is the only event, so the surface has to carry it.</summary>
    [Fact]
    public void Re_opening_a_page_inside_the_budget_joins_its_own_record_without_changing_it()
    {
        var take = P.Decide(P.Surface.Page, Held(revalidatedAt: 0), Now);
        Assert.True(take.Stamp);
        Assert.Equal(Now + Budget, take.HoldUntilMs);

        var rejoin = P.Decide(P.Surface.Page, Held(revalidatedAt: Now, inFlightSince: Now, inFlightBlocking: take.Blocking), Now + 200);
        Assert.Equal(P.Ask.None, rejoin.Ask);          // no edge-door call
        Assert.False(rejoin.Stamp);                    // no new record
        Assert.True(rejoin.Hold);
        Assert.Equal(take.HoldUntilMs, rejoin.HoldUntilMs);   // …and the SAME hold: the join is a no-op on the record
    }
}
