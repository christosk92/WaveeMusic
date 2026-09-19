// ── Wavee.Tests/ListStampsTests.cs — the list freshness stamps (plan §2, §3.3; wave D3, owner L3) ─────────────────────
//
// `ListStamps` is memory-only process state: "a push said this list moved" (dirty) and "revalidated at" on ONE
// monotonic clock that never reads 0, so 0 stays "never this session". Pinned here: dirty/clear/forget, the reconnect's
// keep-set, the clock's wrap and its zero, the change signal, and the stamps read through `ListFreshness.Decide` the way
// the pages read them. The stamps are static, so every test starts from ForgetAll and the class joins the Entities
// collection (the pages' tests, which read stamps, run there).

using Wavee;
using Xunit;

namespace Wavee.Tests;

using F = Wavee.ListFreshness.Verdict;

[Collection(EntitiesCollection.Name)]
public class ListStampsTests
{
    const string A = "spotify:playlist:2EEbSgmD8JC7LR6pUxlLrj";
    const string B = "spotify:playlist:2EEbSgmD8JC7LR6pUxlLrR";
    static readonly string Root = ListWrite.RootlistKey("spotify:user:user1");

    public ListStampsTests() => ListStamps.ForgetAll();

    // ── the clock ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_clock_never_reads_zero_so_zero_stays_never()
    {
        Assert.Equal(1, ListStamps.ClockOf(0));
        Assert.Equal(1, ListStamps.ClockOf(1L << 32));                           // the wrap lands on 0 → 1
        Assert.Equal(42, ListStamps.ClockOf(42));
        Assert.NotEqual(0, ListStamps.NowMs());
    }

    [Fact]
    public void The_clock_wraps_as_an_int_and_an_age_across_the_wrap_is_still_right()
    {
        int before = ListStamps.ClockOf(int.MaxValue - 999L);
        int after = ListStamps.ClockOf(int.MaxValue + 1001L);                    // two seconds later, past the wrap
        Assert.True(after < 0);
        Assert.Equal(2000, unchecked(after - before));
        Assert.Equal(F.Paint, ListFreshness.Decide(hasBaseline: true, dirty: false, before, after, rolling: false));
    }

    [Fact]
    public void The_clock_does_not_go_backwards()
    {
        int first = ListStamps.NowMs();
        int second = ListStamps.NowMs();
        Assert.True(unchecked(second - first) >= 0);
    }

    // ── dirty, revalidated, forgotten ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_list_is_clean_and_never_revalidated()
    {
        Assert.False(ListStamps.IsDirty(A));
        Assert.Equal(0, ListStamps.RevalidatedAtMs(A));
        Assert.False(ListStamps.IsDirty(""));
        Assert.Equal(0, ListStamps.RevalidatedAtMs(""));
        Assert.Equal(0, ListStamps.Count);
    }

    [Fact]
    public void Dirty_keeps_the_revalidation_stamp_and_a_revalidation_clears_dirty()
    {
        ListStamps.MarkRevalidated(A, 5_000);
        ListStamps.MarkDirty(A);
        Assert.True(ListStamps.IsDirty(A));
        Assert.Equal(5_000, ListStamps.RevalidatedAtMs(A));
        Assert.False(ListStamps.IsDirty(B));

        ListStamps.MarkRevalidated(A, 9_000);
        Assert.False(ListStamps.IsDirty(A));
        Assert.Equal(9_000, ListStamps.RevalidatedAtMs(A));
    }

    [Fact]
    public void A_revalidation_at_zero_is_stored_as_one()
    {
        ListStamps.MarkRevalidated(A, 0);
        Assert.Equal(1, ListStamps.RevalidatedAtMs(A));
    }

    [Fact]
    public void An_empty_key_writes_nothing()
    {
        ListStamps.MarkDirty("");
        ListStamps.MarkRevalidated("", 5);
        Assert.Equal(0, ListStamps.Count);
    }

    [Fact]
    public void Forget_all_empties_every_stamp()
    {
        ListStamps.MarkDirty(A);
        ListStamps.MarkRevalidated(B, 7);
        ListStamps.MarkDirty(Root);
        ListStamps.ForgetAll();
        Assert.False(ListStamps.IsDirty(A));
        Assert.False(ListStamps.IsDirty(Root));
        Assert.Equal(0, ListStamps.RevalidatedAtMs(B));
        Assert.Equal(0, ListStamps.Count);
    }

    [Fact]
    public void The_reconnect_keeps_what_it_re_asks_and_forgets_the_rest()
    {
        const string C = "spotify:playlist:0000000000000000000000";
        ListStamps.MarkDirty(A);
        ListStamps.MarkRevalidated(B, 7);
        ListStamps.MarkDirty(Root);
        ListStamps.MarkRevalidated(C, 9);
        ListStamps.MarkDirty(C);

        var dirty = new List<string> { "stale entry" };
        ListStamps.CollectDirty(dirty);
        Assert.Equal(new[] { A, C, Root }.Order(StringComparer.Ordinal), dirty.Order(StringComparer.Ordinal));

        int forgot = ListStamps.ForgetExcept(new HashSet<string>(StringComparer.Ordinal) { A, Root });
        Assert.Equal(2, forgot);                                                 // B (clean) and C (dirty, not re-asked)
        Assert.True(ListStamps.IsDirty(A));
        Assert.True(ListStamps.IsDirty(Root));
        Assert.Equal(0, ListStamps.RevalidatedAtMs(B));
        Assert.False(ListStamps.IsDirty(C));
        Assert.Equal(0, ListStamps.RevalidatedAtMs(C));
        Assert.Equal(2, ListStamps.Count);
    }

    // ── the change signal ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_signal_bumps_on_a_change_and_not_on_a_repeat()
    {
        uint start = ListStamps.Changed.Peek();
        ListStamps.MarkDirty(A);
        uint dirtied = ListStamps.Changed.Peek();
        Assert.NotEqual(start, dirtied);
        ListStamps.MarkDirty(A);                                                 // already dirty
        Assert.Equal(dirtied, ListStamps.Changed.Peek());
        ListStamps.MarkRevalidated(A, 3);
        uint revalidated = ListStamps.Changed.Peek();
        Assert.NotEqual(dirtied, revalidated);
        Assert.Equal(0, ListStamps.ForgetExcept(new HashSet<string> { A }));     // nothing to forget
        Assert.Equal(revalidated, ListStamps.Changed.Peek());
        ListStamps.ForgetAll();
        Assert.NotEqual(revalidated, ListStamps.Changed.Peek());
        uint empty = ListStamps.Changed.Peek();
        ListStamps.ForgetAll();                                                  // already empty
        Assert.Equal(empty, ListStamps.Changed.Peek());
    }

    // ── the stamps, read the way an open reads them ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_open_of_a_session_revalidates_then_a_fresh_stamp_paints_and_a_push_dirties_it()
    {
        int now = ListStamps.NowMs();
        Assert.Equal(F.RevalidateThenPaint,
            ListFreshness.Decide(hasBaseline: true, ListStamps.IsDirty(A), ListStamps.RevalidatedAtMs(A), now, rolling: false));

        ListStamps.MarkRevalidated(A, now);
        Assert.Equal(F.Paint,
            ListFreshness.Decide(hasBaseline: true, ListStamps.IsDirty(A), ListStamps.RevalidatedAtMs(A), now + 1_000, rolling: false));

        ListStamps.MarkDirty(A);                                                 // a head-only push while it was closed
        Assert.Equal(F.RevalidateThenPaint,
            ListFreshness.Decide(hasBaseline: true, ListStamps.IsDirty(A), ListStamps.RevalidatedAtMs(A), now + 2_000, rolling: false));

        ListStamps.ForgetAll();                                                  // a reconnect that did not re-ask it
        Assert.Equal(F.RevalidateThenPaint,
            ListFreshness.Decide(hasBaseline: true, ListStamps.IsDirty(A), ListStamps.RevalidatedAtMs(A), now + 3_000, rolling: false));
    }
}
