// ── Wavee.Tests/CatalogSweepScheduleTests.cs — the eviction decision, with no database in sight ───────────────────
//
// Wave 1's gate for the CORE half of Entities/Store.cs (plan §5, §4.4). The 0.2.9 original (EntityCacheGc, 361 lines
// + six SQL sweeps in SqliteColdStore) had NO test of its ordering at all — it could not have one, because the
// decision and the DELETE were the same code. Splitting the decision out is what makes these eleven facts assertable
// with no file on disk, no clock and no connection (D17).
//
// The contract under test, in one line: TTL is the FILTER, LRU is the RANKER, the byte budget is the ceiling, and a
// pass is bounded in rows and yields to the write lane.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class CatalogSweepScheduleTests
{
    // A policy with round numbers, so every expectation below is readable arithmetic rather than a date.
    static SweepPolicy Policy(long budget = 0, int cap = 100) => new(
        TtlSeconds: 1_000,
        GraceSeconds: 100,
        ByteBudget: budget,
        HeadroomPercent: 90,
        MaxRowsPerPass: cap,
        DeferQueueDepth: 8);

    /// <summary>Rows ordered coldest-first, which is the order <c>ix_&lt;table&gt;_gc</c> hands them over in.</summary>
    static SweepRow Row(int key, int touched, int fetchedAt = -100_000, int bytes = 1_000, SweepFlags flags = SweepFlags.None)
        => new(key, touched, fetchedAt, bytes, flags);

    // ── the TTL filter ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cold_rows_go_and_warm_rows_stay()
    {
        int now = 10_000;
        SweepRow[] rows =
        [
            Row(1, touched: 8_000),      // 2,000 s old — past the 1,000 s TTL
            Row(2, touched: 8_500),      // 1,500 s old — past it
            Row(3, touched: 9_500),      //   500 s old — inside it
        ];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(), now, cacheBytes: 0, queueDepth: 0, victims);

        Assert.Equal(2, plan.Victims);
        Assert.Equal(2, plan.TtlVictims);
        Assert.Equal(0, plan.BudgetVictims);
        Assert.Equal(1, victims[0]);
        Assert.Equal(2, victims[1]);
        Assert.Equal(2_000L, plan.BytesFreed);
        Assert.False(plan.More);
    }

    [Fact]
    public void A_pinned_row_is_never_a_victim_however_cold_it_is()
    {
        // The now-playing track, the open page's rows, the library: 0.2.9 built a temp table for exactly this set,
        // and the one thing it could never afford to get wrong was evicting what the UI is holding.
        SweepRow[] rows = [Row(1, touched: 0, flags: SweepFlags.Pinned), Row(2, touched: 0)];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(), now: 100_000, cacheBytes: 0, queueDepth: 0, victims);

        Assert.Equal(1, plan.Victims);
        Assert.Equal(2, victims[0]);
    }

    [Fact]
    public void A_row_a_provider_just_answered_for_is_too_new_to_judge()
    {
        // Critique #11: without the grace window a page that mounts, fetches 300 rows and hands them to the sweep in
        // the same minute evicts its own answers — a fetch loop that looks like a slow network.
        int now = 10_000;
        SweepRow[] rows =
        [
            Row(1, touched: 0, fetchedAt: now - 50),    // answered 50 s ago: inside the 100 s grace
            Row(2, touched: 0, fetchedAt: now - 500),   // answered 500 s ago: fair game
        ];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(), now, cacheBytes: 0, queueDepth: 0, victims);

        Assert.Equal(1, plan.Victims);
        Assert.Equal(2, victims[0]);
    }

    // ── the byte budget ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Under_budget_the_second_leg_does_not_run()
    {
        SweepRow[] rows = [Row(1, touched: 9_999), Row(2, touched: 9_999)];   // warm
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(budget: 1_000_000), now: 10_000, cacheBytes: 500_000, queueDepth: 0, victims);

        Assert.Equal(0, plan.Victims);
        Assert.Equal(500_000L, plan.BytesAfter);
        Assert.False(plan.More);
    }

    [Fact]
    public void Over_budget_the_least_recently_used_warm_rows_go_first_and_stop_at_the_headroom()
    {
        // 10 warm rows of 1,000 bytes each against a 10,000-byte cache and a 5,000-byte budget: the pass must delete
        // down to 0.9 × 5,000 = 4,500, which is six rows, and it must take the six COLDEST — the order the caller's
        // ORDER BY touched ASC handed over.
        var rows = new SweepRow[10];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i + 1, touched: 9_900 + i);
        Span<int> victims = stackalloc int[16];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(budget: 5_000), now: 10_000, cacheBytes: 10_000, queueDepth: 0, victims);

        Assert.Equal(0, plan.TtlVictims);
        Assert.Equal(6, plan.BudgetVictims);
        Assert.Equal(4_000L, plan.BytesAfter);
        for (int i = 0; i < 6; i++) Assert.Equal(i + 1, victims[i]);
        Assert.False(plan.More);
    }

    [Fact]
    public void A_row_the_ttl_already_took_is_not_taken_twice_by_the_budget()
    {
        // Two legs, one victims list: double-counting here would double-count the bytes freed and stop the budget leg
        // early, which is how a cache stays over its ceiling while reporting that it swept.
        SweepRow[] rows =
        [
            Row(1, touched: 0),          // cold: the TTL leg's
            Row(2, touched: 9_999),      // warm: the budget leg's
        ];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(budget: 500), now: 10_000, cacheBytes: 2_000, queueDepth: 0, victims);

        Assert.Equal(2, plan.Victims);
        Assert.Equal(1, plan.TtlVictims);
        Assert.Equal(1, plan.BudgetVictims);
        Assert.Equal(1, victims[0]);
        Assert.Equal(2, victims[1]);
        Assert.Equal(2_000L, plan.BytesFreed);
    }

    [Fact]
    public void A_pinned_row_survives_the_budget_leg_too()
    {
        SweepRow[] rows = [Row(1, touched: 9_990, flags: SweepFlags.Pinned), Row(2, touched: 9_991)];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(budget: 100), now: 10_000, cacheBytes: 2_000, queueDepth: 0, victims);

        Assert.Equal(1, plan.Victims);
        Assert.Equal(2, victims[0]);
        Assert.True(plan.More);          // still over budget, and the only row left is pinned
    }

    // ── the bounds ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pass_stops_at_the_row_cap_and_says_there_is_more_to_do()
    {
        var rows = new SweepRow[50];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i + 1, touched: 0);
        Span<int> victims = stackalloc int[64];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(cap: 10), now: 10_000, cacheBytes: 0, queueDepth: 0, victims);

        Assert.Equal(10, plan.Victims);
        Assert.True(plan.More);
    }

    [Fact]
    public void The_victims_buffer_bounds_the_pass_as_hard_as_the_policy_does()
    {
        var rows = new SweepRow[50];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i + 1, touched: 0);
        Span<int> victims = stackalloc int[4];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(cap: 1_000), now: 10_000, cacheBytes: 0, queueDepth: 0, victims);

        Assert.Equal(4, plan.Victims);
        Assert.True(plan.More);
    }

    [Fact]
    public void A_busy_write_lane_defers_the_whole_pass()
    {
        // R2: the sweep is a ceiling, not a wall. A GC that competes with write-behind turns a burst of provider
        // answers into a stall, which is the 3.4 s navigation freeze C9 exists to prevent.
        var rows = new SweepRow[10];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i + 1, touched: 0);
        Span<int> victims = stackalloc int[16];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(), now: 10_000, cacheBytes: 0, queueDepth: 9, victims);

        Assert.True(plan.Deferred);
        Assert.Equal(0, plan.Victims);
        Assert.True(plan.More);          // deferred is not done
    }

    [Fact]
    public void Nothing_to_do_is_not_a_deferral()
    {
        SweepRow[] rows = [Row(1, touched: 10_000)];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(), now: 10_000, cacheBytes: 0, queueDepth: 0, victims);

        Assert.False(plan.Deferred);
        Assert.False(plan.More);
        Assert.Equal(0, plan.Victims);
    }

    /// <summary>THE PROPERTY THE MEMORY TRIM LEANS ON. <c>Store.TrimMemory</c> (R2, defect 1) runs this same decision
    /// over a TABLE's rows, and it hands them over in SLOT order — sorting a whole table coldest-first per pass is
    /// exactly the cost a trim exists to avoid, and on disk it is free only because <c>ix_&lt;table&gt;_gc</c> already
    /// stores them that way. So the TTL leg has to be a filter and not a rank: same rows in any order, same victims.
    /// (That is also why the memory trim runs with <c>ByteBudget = 0</c> — the BUDGET leg genuinely is a rank, and
    /// <see cref="Over_budget_the_least_recently_used_warm_rows_go_first_and_stop_at_the_headroom"/> is the fact that
    /// says so.)</summary>
    [Fact]
    public void The_ttl_leg_takes_the_same_victims_whatever_order_the_rows_arrive_in()
    {
        int now = 10_000;
        SweepRow[] coldestFirst = [Row(1, touched: 8_000), Row(2, touched: 8_500), Row(3, touched: 9_500), Row(4, touched: 8_900)];
        SweepRow[] shuffled = [coldestFirst[2], coldestFirst[3], coldestFirst[0], coldestFirst[1]];
        Span<int> a = stackalloc int[8];
        Span<int> b = stackalloc int[8];

        SweepPlan ordered = CatalogSweepSchedule.Plan(coldestFirst, Policy(), now, cacheBytes: 0, queueDepth: 0, a);
        SweepPlan slotOrder = CatalogSweepSchedule.Plan(shuffled, Policy(), now, cacheBytes: 0, queueDepth: 0, b);

        Assert.Equal(3, ordered.Victims);                       // rows 1, 2 and 4 are past the 1,000 s TTL; 3 is not
        Assert.Equal(ordered.Victims, slotOrder.Victims);
        Assert.Equal(ordered.TtlVictims, slotOrder.TtlVictims);
        Assert.Equal(ordered.BytesFreed, slotOrder.BytesFreed);
        a[..ordered.Victims].Sort();
        b[..slotOrder.Victims].Sort();
        Assert.True(a[..ordered.Victims].SequenceEqual(b[..slotOrder.Victims]));
    }

    [Fact]
    public void Timestamps_from_an_earlier_launch_are_negative_and_still_sort_as_older()
    {
        // Store.ToApp returns app seconds relative to THIS process's epoch, so every row a previous launch wrote is
        // negative. The subtraction has to keep working across that boundary or nothing written before today is ever
        // evicted (and the cache grows without bound, which is the bug this file's ancestor shipped with for a year).
        SweepRow[] rows = [Row(1, touched: -50_000), Row(2, touched: 5)];
        Span<int> victims = stackalloc int[8];

        SweepPlan plan = CatalogSweepSchedule.Plan(rows, Policy(), now: 10, cacheBytes: 0, queueDepth: 0, victims);

        Assert.Equal(1, plan.Victims);
        Assert.Equal(1, victims[0]);
    }
}
