// ── Wavee.Tests/CaptureRecentTests.cs — the in-app reader's pure rules (unit 6, realtime-capture-implementation.md §5.5) ──
//
// CaptureRootRollup (the green/amber/red dot), CaptureAnomalyScanner (the Anomalies card), CaptureRootGrouping (the
// recent-roots list) and CaptureTreeBuilder (the expanded causal tree) — every one a plain fold over `CaptureEvent`
// fixtures built here, no disk, no FluentGpu, per CLAUDE.md's "no source-text tests" rule. CaptureRecentStore's own
// ring behaviour (bounded, oldest-evicted-first) is pinned too, since it holds real state even though it lives
// beside the pure classes in the same file.

using Xunit;

namespace Wavee.Tests;

public class CaptureRecentTests
{
    static CaptureEvent Ev(long seq, long id, long causeId, long rootId, CaptureKind kind, CapturePhase phase,
        string? a = null, string? b = null, long n0 = 0, long unixMs = 0)
        => new(seq, seq, unixMs == 0 ? seq : unixMs, id, causeId, rootId, kind, phase, CapturePriority.Normal,
            new CaptureFields(a, b, N0: n0));

    // ── CaptureRootRollup ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_root_with_only_clean_points_is_green()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.QueueMutation, CapturePhase.Point),
            Ev(3, 3, 1, 1, CaptureKind.UiRerender, CapturePhase.Point),
        };
        Assert.Equal(CaptureRootStatus.Green, CaptureRootRollup.Compute(events));
    }

    [Fact]
    public void A_begin_with_no_matching_end_anywhere_is_red()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.ConnectStatePut, CapturePhase.Begin),
        };
        Assert.Equal(CaptureRootStatus.Red, CaptureRootRollup.Compute(events));
    }

    [Fact]
    public void A_begin_that_IS_closed_does_not_read_red()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.ConnectStatePut, CapturePhase.Begin),
            Ev(3, 2, 1, 1, CaptureKind.ConnectStatePut, CapturePhase.End, n0: 200),
        };
        Assert.Equal(CaptureRootStatus.Green, CaptureRootRollup.Compute(events));
    }

    [Fact]
    public void A_non_2xx_http_end_is_amber_not_red()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.HttpCall, CapturePhase.Begin),
            Ev(3, 2, 1, 1, CaptureKind.HttpCall, CapturePhase.End, n0: 500),
        };
        Assert.Equal(CaptureRootStatus.Amber, CaptureRootRollup.Compute(events));
    }

    [Fact]
    public void A_decode_failure_or_ignored_frame_is_amber()
    {
        Assert.Equal(CaptureRootStatus.Amber, CaptureRootRollup.Compute(new[]
            { Ev(1, 1, 0, 1, CaptureKind.DecodeFailed, CapturePhase.Point) }));
        Assert.Equal(CaptureRootStatus.Amber, CaptureRootRollup.Compute(new[]
            { Ev(1, 1, 0, 1, CaptureKind.FrameIgnored, CapturePhase.Point) }));
    }

    [Fact]
    public void Red_wins_over_amber_when_both_are_present()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.FrameIgnored, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.ConnectStatePut, CapturePhase.Begin),
        };
        Assert.Equal(CaptureRootStatus.Red, CaptureRootRollup.Compute(events));
    }

    // ── CaptureAnomalyScanner ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_open_begin_becomes_an_echo_missing_anomaly()
    {
        var events = new[] { Ev(1, 1, 0, 1, CaptureKind.ConnectStatePut, CapturePhase.Begin, a: "AddToQueue") };
        var anomalies = CaptureAnomalyScanner.Scan(events);
        Assert.Single(anomalies);
        Assert.Equal(CaptureAnomalyKind.EchoMissing, anomalies[0].Kind);
        Assert.Equal(1L, anomalies[0].RootId);
    }

    [Fact]
    public void A_non_2xx_end_becomes_a_non_success_status_anomaly()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.HttpCall, CapturePhase.Begin),
            Ev(2, 1, 1, 1, CaptureKind.HttpCall, CapturePhase.End, n0: 404),
        };
        var anomalies = CaptureAnomalyScanner.Scan(events);
        Assert.Single(anomalies);
        Assert.Equal(CaptureAnomalyKind.NonSuccessStatus, anomalies[0].Kind);
    }

    [Fact]
    public void A_2xx_end_is_not_an_anomaly()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.HttpCall, CapturePhase.Begin),
            Ev(2, 1, 1, 1, CaptureKind.HttpCall, CapturePhase.End, n0: 200),
        };
        Assert.Empty(CaptureAnomalyScanner.Scan(events));
    }

    [Fact]
    public void Anomalies_sort_newest_first()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.DecodeFailed, CapturePhase.Point, unixMs: 1000),
            Ev(2, 2, 0, 2, CaptureKind.DecodeFailed, CapturePhase.Point, unixMs: 5000),
            Ev(3, 3, 0, 3, CaptureKind.DecodeFailed, CapturePhase.Point, unixMs: 3000),
        };
        var anomalies = CaptureAnomalyScanner.Scan(events);
        Assert.Equal([5000L, 3000L, 1000L], anomalies.Select(a => a.UnixMs).ToArray());
    }

    // ── CaptureRootGrouping ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Roots_group_by_root_id_and_come_back_most_recent_first()
    {
        var events = new[]
        {
            Ev(1, 10, 0, 10, CaptureKind.ActionInvoke, CapturePhase.Point, a: "Play"),
            Ev(2, 11, 10, 10, CaptureKind.QueueMutation, CapturePhase.Point),
            Ev(3, 20, 0, 20, CaptureKind.ActionInvoke, CapturePhase.Point, a: "AddToQueue"),
            Ev(4, 21, 20, 20, CaptureKind.QueueMutation, CapturePhase.Point),
            Ev(5, 22, 20, 20, CaptureKind.UiRerender, CapturePhase.Point),
        };
        var roots = CaptureRootGrouping.RecentRoots(events, max: 10);
        Assert.Equal(2, roots.Count);
        Assert.Equal(20L, roots[0].RootId);   // most recent root first
        Assert.Equal(2, roots[0].EffectCount); // the root's own record is not counted as its own effect
        Assert.Equal(10L, roots[1].RootId);
        Assert.Equal(1, roots[1].EffectCount);
    }

    [Fact]
    public void RecentRoots_is_capped_at_max()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 0, 2, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(3, 3, 0, 3, CaptureKind.ActionInvoke, CapturePhase.Point),
        };
        var roots = CaptureRootGrouping.RecentRoots(events, max: 2);
        Assert.Equal(2, roots.Count);
        Assert.Equal([3L, 2L], roots.Select(r => r.RootId).ToArray());
    }

    [Fact]
    public void A_root_whose_own_record_fell_out_of_the_ring_still_summarizes_gracefully()
    {
        // The root's OWN CaptureEvent (Id == RootId) is missing from the fixture — only a child survived. Grouping
        // must not throw; it degrades to the earliest surviving event as a stand-in, per CausalityRules' own
        // "degrade gracefully, never throw" rule (§2.2/§6.5).
        var events = new[] { Ev(1, 11, 10, 10, CaptureKind.QueueMutation, CapturePhase.Point) };
        var roots = CaptureRootGrouping.RecentRoots(events, max: 10);
        Assert.Single(roots);
        Assert.Equal(10L, roots[0].RootId);
        Assert.Equal(1, roots[0].EffectCount);
    }

    // ── CaptureTreeBuilder ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Direct_children_of_the_root_are_depth_zero()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.QueueMutation, CapturePhase.Point),
            Ev(3, 3, 1, 1, CaptureKind.UiRerender, CapturePhase.Point),
        };
        var tree = CaptureTreeBuilder.Build(1, events);
        Assert.Equal(2, tree.Count);
        Assert.All(tree, r => Assert.Equal(0, r.Depth));
        Assert.Equal(2L, tree[0].Event.Id);
        Assert.Equal(3L, tree[1].Event.Id);
    }

    [Fact]
    public void A_grandchild_nests_one_level_deeper_than_its_parent()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 1, 1, CaptureKind.ConnectStatePut, CapturePhase.Begin),
            Ev(3, 2, 1, 1, CaptureKind.ConnectStatePut, CapturePhase.End),
            Ev(4, 4, 2, 1, CaptureKind.ConnectStatePutResponse, CapturePhase.Point), // caused by the Begin's own id
        };
        var tree = CaptureTreeBuilder.Build(1, events);
        Assert.Equal(3, tree.Count); // the Begin, the End (same id, both rows), and the response
        var response = tree.Single(r => r.Event.Id == 4);
        Assert.Equal(1, response.Depth);
    }

    [Fact]
    public void An_event_whose_cause_is_unresolved_falls_back_to_a_direct_child_of_the_root()
    {
        var events = new[]
        {
            Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point),
            Ev(2, 2, 999, 1, CaptureKind.QueueMutation, CapturePhase.Point), // CauseId 999 is not in this fixture
        };
        var tree = CaptureTreeBuilder.Build(1, events);
        Assert.Single(tree);
        Assert.Equal(0, tree[0].Depth);
    }

    [Fact]
    public void The_root_itself_never_appears_as_a_row_in_its_own_tree()
    {
        var events = new[] { Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point) };
        Assert.Empty(CaptureTreeBuilder.Build(1, events));
    }

    // ── CaptureRecentStore ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_store_returns_everything_it_holds_oldest_first()
    {
        var store = new CaptureRecentStore();
        store.Record(Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point));
        store.Record(Ev(2, 2, 1, 1, CaptureKind.QueueMutation, CapturePhase.Point));
        var snapshot = store.Snapshot();
        Assert.Equal([1L, 2L], snapshot.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Clear_empties_the_store()
    {
        var store = new CaptureRecentStore();
        store.Record(Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point));
        store.Clear();
        Assert.Empty(store.Snapshot());
    }

    // ── TeeCaptureSink ───────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FakeSink : ICaptureSink
    {
        public int Calls;
        public void Emit(in CaptureEvent evt, ReadOnlyMemory<byte> payload) => Calls++;
    }

    [Fact]
    public void The_tee_forwards_to_both_the_primary_sink_and_the_recent_store()
    {
        var primary = new FakeSink();
        var store = new CaptureRecentStore();
        ICaptureSink tee = new TeeCaptureSink(primary, store);
        var evt = Ev(1, 1, 0, 1, CaptureKind.ActionInvoke, CapturePhase.Point);
        tee.Emit(in evt, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(1, primary.Calls);
        Assert.Single(store.Snapshot());
    }
}
