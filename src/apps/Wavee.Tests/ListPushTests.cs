// ── Wavee.Tests/ListPushTests.cs — what a dealer push for one list does (plan §3.3, L3) ─────────────────────────────
//
// A push is an optimisation over the revision gate, never a source of truth: only a push whose ops apply to exactly the
// revision held (and to settled rows) is replayed in place; everything else marks the list dirty — or revalidates now
// when it is on screen — and a head it did not come with ops for is never stored. The captured push shapes themselves
// are decoded in PlaylistDiffDecodeTests.

using Wavee;
using Xunit;

namespace Wavee.Tests;

using V = Wavee.ListPush.Verdict;

public class ListPushTests
{
    const string R34 = "34,3434343434343434343434343434343434343434";
    const string R35 = "35,3535353535353535353535353535353535353535";
    const string R36 = "36,3636363636363636363636363636363636363636";

    [Fact]
    public void An_echo_of_the_revision_held_is_dropped()
        => Assert.Equal(V.Drop, ListPush.Decide(storedRev: R36, parentRev: R35, newRev: R36, hasOps: true, resident: true, pendingLocal: false, open: true));

    [Fact]
    public void Ops_on_exactly_the_revision_held_over_settled_rows_apply_in_place()
        => Assert.Equal(V.ApplyInPlace, ListPush.Decide(R35, R35, R36, hasOps: true, resident: true, pendingLocal: false, open: false));

    [Theory]
    [InlineData(false, V.MarkDirty)]
    [InlineData(true, V.RevalidateNow)]
    public void A_head_only_push_never_stores_its_head(bool open, V expected)
    {
        Assert.Equal(expected, ListPush.Decide(R35, parentRev: null, R36, hasOps: false, resident: true, pendingLocal: false, open));
        Assert.Equal(expected, ListPush.Decide(R35, parentRev: R35, R36, hasOps: false, resident: true, pendingLocal: false, open));   // ops unexpressible
        Assert.Equal(expected, ListPush.Decide(R35, parentRev: null, R36, hasOps: true, resident: true, pendingLocal: false, open));   // ops, no parent
    }

    [Theory]
    [InlineData(false, V.MarkDirty)]
    [InlineData(true, V.RevalidateNow)]
    public void A_parent_that_is_not_the_revision_held_means_a_push_was_missed(bool open, V expected)
        => Assert.Equal(expected, ListPush.Decide(R34, R35, R36, hasOps: true, resident: true, pendingLocal: false, open));

    [Theory]
    [InlineData(false, V.MarkDirty)]
    [InlineData(true, V.RevalidateNow)]
    public void Optimistic_rows_on_the_list_are_never_replayed_over(bool open, V expected)
        => Assert.Equal(expected, ListPush.Decide(R35, R35, R36, hasOps: true, resident: true, pendingLocal: true, open));

    [Theory]
    [InlineData(false, V.MarkDirty)]
    [InlineData(true, V.RevalidateNow)]
    public void A_list_not_held_as_a_baseline_is_only_marked(bool open, V expected)
    {
        Assert.Equal(expected, ListPush.Decide(R35, R35, R36, hasOps: true, resident: false, pendingLocal: false, open));
        Assert.Equal(expected, ListPush.Decide(storedRev: null, R35, R36, hasOps: true, resident: false, pendingLocal: false, open));
    }

    [Fact]
    public void Revision_counters_are_never_compared_an_editorial_refresh_is_counter_zero_every_time()
    {
        const string held = "0,d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0";
        const string refresh = "0,1111111111111111111111111111111111111111";
        Assert.Equal(V.MarkDirty, ListPush.Decide(held, parentRev: null, refresh, hasOps: false, resident: true, pendingLocal: false, open: false));
    }

    [Theory]
    [InlineData(false, V.MarkDirty)]
    [InlineData(true, V.RevalidateNow)]
    public void A_rootlist_push_is_never_applied_in_place_its_live_shape_is_unobserved(bool open, V expected)
    {
        Assert.Equal(expected, ListPush.DecideRootlist(storedRev: "135,1313131313131313131313131313131313131313",
            newRev: "137,1717171717171717171717171717171717171717", open));
        Assert.Equal(expected, ListPush.DecideRootlist(storedRev: null, newRev: null, open));
    }

    [Fact]
    public void The_rootlists_second_topic_is_deduped_by_revision()
    {
        const string rev = "137,1717171717171717171717171717171717171717";
        Assert.Equal(V.Drop, ListPush.DecideRootlist(storedRev: rev, newRev: rev, open: true));
    }
}
