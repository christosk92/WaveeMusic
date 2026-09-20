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

    // ── the budget LEFT: the hold's deadline as an armable number (the eternal meta-line shimmer) ────────────────────
    //
    // A hold ends one of two ways. An OBSERVED answer settles the record and publishes, so every memo that reads the
    // hold re-runs. The budget simply running out published nothing — `Holds` merely started answering false the next
    // time somebody happened to ask — so a page that renders off the hold kept its shimmer until an unrelated
    // re-render (a window resize) asked again. `RemainingHoldMs` is what makes that edge armable: the surface arms a
    // wake for exactly this long and settles the record itself (`ListOpen.ExpireHold`) when it fires.

    [Fact]
    public void A_live_hold_reports_the_milliseconds_left_on_its_budget()
    {
        var plan = P.Decide(P.Surface.Page, Held(), Now);
        Assert.Equal(Budget, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, Now, P.Observed.Pending));
        Assert.Equal(Budget - 400, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, Now + 400, P.Observed.Pending));
        Assert.Equal(1, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, Now + Budget - 1, P.Observed.Pending));
    }

    /// <summary>THE BOUNDARY: at the deadline the budget is spent, not "one more millisecond" — the same instant
    /// <see cref="P.Holds"/> flips, because Holds IS this compared against zero.</summary>
    [Fact]
    public void The_budget_is_zero_at_the_deadline_and_never_negative_past_it()
    {
        var plan = P.Decide(P.Surface.Page, Held(), Now);
        Assert.Equal(0, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, Now + Budget, P.Observed.Pending));
        Assert.Equal(0, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, Now + Budget + 1, P.Observed.Pending));
        Assert.Equal(0, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, Now + Budget + 60_000, P.Observed.Pending));
    }

    /// <summary>The deadline is compared as a DIFFERENCE, so a wrapping counter neither strands a hold nor reports a
    /// budget half the counter's range long — a wake armed off that number would be a wake that never fires.</summary>
    [Fact]
    public void The_budget_survives_the_millisecond_counter_wrapping()
    {
        int start = int.MaxValue - 500;
        var plan = P.Decide(P.Surface.Page, Held(), start);
        Assert.Equal(Budget, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, start, P.Observed.Pending));
        Assert.Equal(Budget - 1_000, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, unchecked(start + 1_000), P.Observed.Pending));
        Assert.Equal(0, P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, unchecked(start + Budget), P.Observed.Pending));
    }

    [Fact]
    public void An_open_that_took_no_hold_has_no_budget_to_arm()
        => Assert.Equal(0, P.RemainingHoldMs(hold: false, Now + Budget, Now, P.Observed.Pending));

    /// <summary>An answer the model can already see ends the hold, so there is no budget left to wake for: that settle
    /// belongs to <c>ListOpen.Observe</c>, which names how it ended.</summary>
    [Theory]
    [InlineData(P.Observed.Moved)]
    [InlineData(P.Observed.Paged)]
    [InlineData(P.Observed.Failed)]
    public void An_observed_answer_leaves_no_budget(P.Observed observed)
        => Assert.Equal(0, P.RemainingHoldMs(hold: true, Now + Budget, Now, observed));

    /// <summary>The two readings can NEVER disagree about whether a hold is live — the surface arms its wake off one
    /// and paints off the other, and a page that armed for 0 ms while still holding would shimmer forever.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Budget - 1)]
    [InlineData(Budget)]
    [InlineData(Budget + 1)]
    [InlineData(60_000)]
    public void Holding_and_the_budget_left_are_one_answer(int elapsed)
    {
        var plan = P.Decide(P.Surface.Page, Held(), Now);
        int at = unchecked(Now + elapsed);
        foreach (var observed in new[] { P.Observed.Pending, P.Observed.Moved, P.Observed.Paged, P.Observed.Failed })
            Assert.Equal(P.Holds(plan.Hold, plan.HoldUntilMs, at, observed),
                         P.RemainingHoldMs(plan.Hold, plan.HoldUntilMs, at, observed) > 0);
    }

    /// <summary>WHY IT MATTERS, end to end: the meta line's arm is a function of the hold, and nothing else about the
    /// page moves when the budget runs out. So the budget edge is the ONLY thing between a shimmer bar and the real
    /// text — which is why it has to wake the graph rather than wait to be asked.</summary>
    [Fact]
    public void The_meta_line_resolves_the_moment_the_budget_runs_out()
    {
        var plan = P.Decide(P.Surface.Page, Held(), Now);
        bool holding = P.Holds(plan.Hold, plan.HoldUntilMs, Now + Budget - 1, P.Observed.Pending);
        Assert.True(holding);
        Assert.Equal(Playlist.MetaArm.Loading,
                     Playlist.PageRules.MetaArmFor(holding, EdgeState.Complete, countKnown: false, residentRows: 12));

        bool stillHolding = P.Holds(plan.Hold, plan.HoldUntilMs, Now + Budget, P.Observed.Pending);
        Assert.False(stillHolding);
        Assert.Equal(Playlist.MetaArm.Text,
                     Playlist.PageRules.MetaArmFor(stillHolding, EdgeState.Complete, countKnown: false, residentRows: 12));
    }

    /// <summary>The wake's slack is real time, and small: enough to put a frame-clock fire strictly past a deadline
    /// written on another clock, never enough to read as a second shimmer.</summary>
    [Fact]
    public void The_wake_slack_is_one_frame_not_a_second_shimmer()
    {
        Assert.InRange(P.HoldWakeSlackMs, 1, 100);
        Assert.True(P.HoldWakeSlackMs < Budget);
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

    // ── the PRE-OPEN window, and its bound ──────────────────────────────────────────────────────────────────────────
    //
    // THE BUG (the owner's 199-song playlist: a shimmer where "199 songs · 11 hr 21 min" belongs, forever, until a
    // window resize). The budget of a RECORDED hold now wakes its surface (above). The pre-open answer had no wake at
    // all: before a page's demand effect has opened anything, its source answers by RE-DECIDING the plan
    // (ListOpen.WouldHold over exactly these facts), and that decision reads the clock and the list's stamps with no
    // record behind it — so nothing settles, nothing publishes, ExpireHold has nothing to do, and `Holds` is never
    // even consulted. A page whose demand BAILED (its playlist row was not valid at the first effect drain, and
    // neither the slot nor the scope epoch moves when the row lands) stays on that rule for the life of the page.
    // `PreOpenHoldMs` is the bound that makes the pre-open answer armable: the same budget as the hold it predicts,
    // after which the surface stops honouring the re-decide and paints.

    /// <summary>The bound IS the budget of the hold the re-decide predicts — not a second number that could drift
    /// from it, and not "forever".</summary>
    [Fact]
    public void A_pre_open_re_decide_that_holds_is_bounded_by_the_budget_it_predicts()
    {
        var plan = P.Decide(P.Surface.Page, Held(revalidatedAt: 0), Now);
        Assert.True(plan.Hold);
        Assert.Equal(Budget, P.PreOpenHoldMs(plan, Now));
        Assert.Equal(Budget - 400, P.PreOpenHoldMs(plan, Now + 400));
    }

    /// <summary>A pre-open re-decide that joins a blocking revalidation already out is bounded by THAT ask's own
    /// deadline, not by a fresh budget — the surface may not extend a hold it merely joined.</summary>
    [Fact]
    public void A_pre_open_join_is_bounded_by_the_in_flight_revalidations_own_deadline()
    {
        var plan = P.Decide(P.Surface.Page, Held(revalidatedAt: Now - 300, inFlightSince: Now - 300, inFlightBlocking: true), Now);
        Assert.True(plan.Hold);
        Assert.Equal(Budget - 300, P.PreOpenHoldMs(plan, Now));
    }

    /// <summary>Nothing held, nothing to bound: a page over a fresh list arms no pre-open wake at all.</summary>
    [Theory]
    [InlineData(P.Surface.Revisit)]
    [InlineData(P.Surface.Queue)]
    public void A_surface_that_never_holds_has_no_pre_open_window_to_bound(P.Surface surface)
        => Assert.Equal(0, P.PreOpenHoldMs(P.Decide(surface, Held(revalidatedAt: 0), Now), Now));

    [Fact]
    public void A_pre_open_re_decide_over_a_fresh_list_has_no_window_to_bound()
    {
        var plan = P.Decide(P.Surface.Page, Held(revalidatedAt: Now - 1_000), Now);
        Assert.False(plan.Hold);
        Assert.Equal(0, P.PreOpenHoldMs(plan, Now));
    }

    /// <summary>The pre-open answer and its bound are ONE answer — the source paints off the first and arms off the
    /// second, and a page that armed 0 ms while still honouring the re-decide would shimmer forever again.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Budget - 1)]
    [InlineData(Budget)]
    [InlineData(60_000)]
    public void The_pre_open_hold_and_its_bound_are_one_answer(int elapsed)
    {
        foreach (var facts in new[] { Held(revalidatedAt: 0), Held(revalidatedAt: Now - 1_000),
                                      Held(revalidatedAt: Now - 300, inFlightSince: Now - 300, inFlightBlocking: true) })
        {
            int at = unchecked(Now + elapsed);
            var plan = P.Decide(P.Surface.Page, facts, at);
            Assert.Equal(plan.Hold, P.PreOpenHoldMs(plan, at) > 0);
        }
    }

    /// <summary>The bound is a DIFFERENCE, so a wrapping millisecond counter never arms a wake that will not fire.</summary>
    [Fact]
    public void The_pre_open_bound_survives_the_millisecond_counter_wrapping()
    {
        int start = int.MaxValue - 500;
        var plan = P.Decide(P.Surface.Page, Held(), start);
        Assert.Equal(Budget, P.PreOpenHoldMs(plan, start));
        Assert.Equal(Budget - 1_000, P.PreOpenHoldMs(plan, unchecked(start + 1_000)));
        Assert.Equal(0, P.PreOpenHoldMs(plan, unchecked(start + Budget)));
    }

    /// <summary>WHY IT MATTERS, end to end — the owner's 199-song list. The pre-open re-decide holds, so the meta line
    /// shimmers; the rows are resident the whole time, so the moment the surface stops honouring that re-decide the
    /// line is TEXT. Nothing else about the page moves, which is why the bound has to wake the graph rather than wait
    /// to be asked (a window resize was the only thing that ever asked).</summary>
    [Fact]
    public void The_meta_line_resolves_when_the_pre_open_window_is_spent()
    {
        var plan = P.Decide(P.Surface.Page, Held(revalidatedAt: 0), Now);
        Assert.True(plan.Hold);
        Assert.Equal(Budget, P.PreOpenHoldMs(plan, Now));
        Assert.Equal(Playlist.MetaArm.Loading,
                     Playlist.PageRules.MetaArmFor(holding: true, EdgeState.Complete, countKnown: false, residentRows: 199));

        // …spent: the source stops honouring the re-decide, and 199 resident rows ARE the count.
        Assert.Equal(Playlist.MetaArm.Text,
                     Playlist.PageRules.MetaArmFor(holding: false, EdgeState.Complete, countKnown: false, residentRows: 199));
    }

    // ── the open must be REACHED: the demand effect's arm ───────────────────────────────────────────────────────────
    //
    // The other half of the same bug. The page's demand runs once per (playlist slot, scope epoch) and ends in THE
    // OPEN — but it returns early on a playlist row that is not valid yet, and NEITHER the slot NOR the scope epoch
    // moves when that row lands (only the row's own version does). So a cold navigation whose first effect drain saw
    // an unlanded row never ran the demand again, HeldRows.Opened was never reached, and the page answered off the
    // pre-open re-decide for good. The dep key therefore carries WHAT the run will do, not just which row it is about.

    [Fact]
    public void A_demand_that_bails_on_an_unlanded_row_arms_differently_from_the_one_that_opens()
    {
        var bailed = Playlist.PageRules.DemandArmFor(rowValid: false, local: false);
        var opens = Playlist.PageRules.DemandArmFor(rowValid: true, local: false);
        Assert.Equal(Playlist.DemandArm.Bail, bailed);
        Assert.Equal(Playlist.DemandArm.Open, opens);
        Assert.NotEqual(bailed, opens);                       // ⇒ the dep key moves when the row lands ⇒ the effect re-runs
        Assert.False(Playlist.PageRules.DemandOpens(bailed));
        Assert.True(Playlist.PageRules.DemandOpens(opens));
    }

    /// <summary>Local Files legitimately never opens a list — it is settled on this device — and its arm must still
    /// move when the row lands, or its own settle is stranded behind the same guard.</summary>
    [Fact]
    public void Local_files_land_their_row_too_and_never_reach_the_open()
    {
        var bailed = Playlist.PageRules.DemandArmFor(rowValid: false, local: true);
        var local = Playlist.PageRules.DemandArmFor(rowValid: true, local: true);
        Assert.Equal(Playlist.DemandArm.Bail, bailed);
        Assert.Equal(Playlist.DemandArm.Local, local);
        Assert.NotEqual(bailed, local);
        Assert.False(Playlist.PageRules.DemandOpens(local));
    }

    /// <summary>The three arms are pairwise DISTINCT — a dep key is compared by value, so two arms that collided
    /// would silently re-strand the open — and an unlanded row bails whichever route it is on.</summary>
    [Fact]
    public void The_arms_are_pairwise_distinct_and_an_unlanded_row_always_bails()
    {
        Assert.Equal(Playlist.DemandArm.Bail, Playlist.PageRules.DemandArmFor(rowValid: false, local: false));
        Assert.Equal(Playlist.DemandArm.Bail, Playlist.PageRules.DemandArmFor(rowValid: false, local: true));
        Assert.NotEqual(Playlist.DemandArm.Bail, Playlist.DemandArm.Local);
        Assert.NotEqual(Playlist.DemandArm.Bail, Playlist.DemandArm.Open);
        Assert.NotEqual(Playlist.DemandArm.Local, Playlist.DemandArm.Open);
    }

    /// <summary>Exactly one arm reaches the open — the invariant the whole key exists for.</summary>
    [Fact]
    public void Exactly_one_arm_reaches_the_open()
    {
        int opens = 0;
        foreach (var arm in new[] { Playlist.DemandArm.Bail, Playlist.DemandArm.Local, Playlist.DemandArm.Open })
            if (Playlist.PageRules.DemandOpens(arm)) opens++;
        Assert.Equal(1, opens);
    }
}
