// ── Wavee.Tests/ListFreshnessTests.cs — when an open paints, and when it asks first (plan §2, §3.3) ─────────────────
//
// Correctness is the revision + /diff on open; the stamps are memory-only, so the first open of a session always asks.
// A stale baseline blocks the open on the diff for at most 1500 ms — yesterday's copy is never painted-then-swapped.

using Wavee;
using Xunit;

namespace Wavee.Tests;

using V = Wavee.ListFreshness.Verdict;

public class ListFreshnessTests
{
    const int Now = 50_000_000;

    [Fact]
    public void The_constants_are_the_plans()
    {
        Assert.Equal(300_000, ListFreshness.WindowMs);
        Assert.Equal(1_500, ListFreshness.BlockingBudgetMs);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, Now - 1_000)]
    public void Nothing_held_is_a_full_read_whatever_else_is_true(bool dirty, int revalidatedAt)
        => Assert.Equal(V.FullRead, ListFreshness.Decide(hasBaseline: false, dirty, revalidatedAt, Now, rolling: false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_open_this_session_revalidates_before_it_paints(bool rolling)
        => Assert.Equal(V.RevalidateThenPaint, ListFreshness.Decide(hasBaseline: true, dirty: false, revalidatedAtMs: 0, Now, rolling));

    [Fact]
    public void A_dirty_list_revalidates_before_it_paints_even_inside_the_window()
        => Assert.Equal(V.RevalidateThenPaint, ListFreshness.Decide(true, dirty: true, revalidatedAtMs: Now - 1_000, Now, rolling: false));

    [Theory]
    [InlineData(0, false)]
    [InlineData(299_999, false)]
    [InlineData(1_000, true)]
    public void Inside_the_window_and_clean_paints_without_asking(int age, bool rolling)
        => Assert.Equal(V.Paint, ListFreshness.Decide(true, dirty: false, Now - age, Now, rolling));

    [Fact]
    public void Past_the_window_a_list_the_dealer_never_dirtied_paints_then_revalidates()
        => Assert.Equal(V.PaintThenRevalidate, ListFreshness.Decide(true, dirty: false, Now - 300_000, Now, rolling: false));

    [Fact]
    public void Past_the_window_a_rolling_identity_revalidates_first()
        => Assert.Equal(V.RevalidateThenPaint, ListFreshness.Decide(true, dirty: false, Now - 300_000, Now, rolling: true));

    [Fact]
    public void A_clock_that_went_backwards_reads_as_past_the_window()
        => Assert.Equal(V.PaintThenRevalidate, ListFreshness.Decide(true, dirty: false, Now + 5_000, Now, rolling: false));

    [Fact]
    public void The_window_survives_the_millisecond_counter_wrapping()
        => Assert.Equal(V.Paint, ListFreshness.Decide(true, dirty: false, int.MaxValue - 1_000, unchecked(int.MaxValue + 1_000), rolling: false));

    [Theory]
    [InlineData(ListFreshness.Outcome.Unchanged, true)]
    [InlineData(ListFreshness.Outcome.Replayed, true)]
    [InlineData(ListFreshness.Outcome.FullRead, false)]
    [InlineData(ListFreshness.Outcome.Failed, false)]
    public void A_rolling_identity_re_asks_its_header_after_any_outcome_that_did_not_re_read_it(ListFreshness.Outcome outcome, bool reask)
    {
        Assert.Equal(reask, ListFreshness.ReaskHeader(rolling: true, outcome));
        Assert.False(ListFreshness.ReaskHeader(rolling: false, outcome));
    }
}
