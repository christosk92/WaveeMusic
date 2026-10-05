// ── Wavee.Tests/HeapPolicyTests.cs — when the memory governor asks the GC for its dead large objects back ──────────
//
// `Platform/Residency.Heap.cs`'s decision is pure, so its boundaries are pinned here with no GC and no window: a blocking
// compaction only with the window parked and the transport settled (nothing playing, opening or pending, and no phase
// change for a minute — the managed feed thread would stall past the ~100 ms device FIFO, and a media key can start a
// track while the window is minimized), rate-limited; a background collection whenever enough has piled up, sooner under
// pressure, and never again while engaged once the runtime ran one as a blocking collection.
using Xunit;

namespace Wavee.Tests;

public class HeapPolicyTests
{
    const long MB = 1L << 20;

    /// <summary>A heap with plenty to give back, a parked window and a long-silent app: everything allows a compaction.</summary>
    static HeapFacts Parked(long heap = 400 * MB, long fragmented = 120 * MB, long baseline = 200 * MB) => new(
        HeapBytes: heap, FragmentedBytes: fragmented, BaselineBytes: baseline, TransportEngaged: false,
        MsSinceTransportChange: long.MaxValue, OnScreen: false,
        MsSinceCompact: long.MaxValue, MsSinceBackground: long.MaxValue, Pressure: MemoryPressure.Normal);

    [Fact]
    public void A_parked_settled_app_with_free_space_compacts()
        => Assert.Equal(HeapAction.Compact, HeapPolicy.Decide(Parked()));

    [Fact]
    public void Never_a_blocking_collection_while_the_transport_is_engaged()
    {
        // Playing, or a track opening (a media key / Connect / tray Play while minimized): the dead objects still go,
        // concurrently, but nothing blocks.
        var engaged = Parked() with { TransportEngaged = true };
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(engaged));
        Assert.False(HeapPolicy.MayBlock(transportEngaged: true, msSinceTransportChange: long.MaxValue, onScreen: false));
    }

    [Fact]
    public void A_transport_that_just_stopped_is_not_settled_yet()
    {
        // A track ended or was paused moments ago: the next one (or a resume) may be about to open.
        var justStopped = Parked() with { MsSinceTransportChange = HeapPolicy.SettledMsForCompact - 1 };
        Assert.NotEqual(HeapAction.Compact, HeapPolicy.Decide(justStopped));
        Assert.Equal(HeapAction.Compact, HeapPolicy.Decide(justStopped with { MsSinceTransportChange = HeapPolicy.SettledMsForCompact }));
    }

    [Fact]
    public void An_on_screen_window_never_compacts_however_long_it_has_been_idle()
    {
        var onScreen = Parked() with { OnScreen = true };
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(onScreen));
        Assert.False(HeapPolicy.MayBlock(transportEngaged: false, msSinceTransportChange: long.MaxValue, onScreen: true));
    }

    [Fact]
    public void Compactions_are_rate_limited()
    {
        var recent = Parked() with { MsSinceCompact = HeapPolicy.MinMsBetweenCompacts - 1 };
        Assert.NotEqual(HeapAction.Compact, HeapPolicy.Decide(recent));
        Assert.Equal(HeapAction.Compact, HeapPolicy.Decide(recent with { MsSinceCompact = HeapPolicy.MinMsBetweenCompacts }));
    }

    [Fact]
    public void A_compact_heap_is_left_alone()
    {
        // Nothing fragmented and nothing piled up since the last full GC: there is nothing to hand back.
        var tidy = Parked(heap: 210 * MB, fragmented: 4 * MB, baseline: 200 * MB);
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(tidy));
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(tidy with { TransportEngaged = true, OnScreen = true }));
    }

    [Fact]
    public void Background_collections_need_growth_and_are_rate_limited()
    {
        var inUse = Parked(fragmented: 0) with { TransportEngaged = true, OnScreen = true };
        long threshold = HeapPolicy.BackgroundThreshold(MemoryPressure.Normal);

        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(inUse with { HeapBytes = inUse.BaselineBytes + threshold }));
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(inUse with { HeapBytes = inUse.BaselineBytes + threshold - 1 }));
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(inUse with { MsSinceBackground = HeapPolicy.MinMsBetweenBackground - 1 }));
    }

    [Fact]
    public void A_background_request_the_runtime_ran_blocking_is_not_repeated_while_engaged()
    {
        var burned = Parked(fragmented: 0) with { OnScreen = true, BackgroundRanBlocking = true };
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(burned with { TransportEngaged = true }));
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(burned));       // silent: a blocking pause hurts nobody
    }

    [Fact]
    public void Pressure_lowers_the_background_threshold()
    {
        Assert.True(HeapPolicy.BackgroundThreshold(MemoryPressure.Moderate) < HeapPolicy.BackgroundThreshold(MemoryPressure.Normal));
        Assert.True(HeapPolicy.BackgroundThreshold(MemoryPressure.Critical) < HeapPolicy.BackgroundThreshold(MemoryPressure.Moderate));

        var inUse = Parked(fragmented: 0) with { TransportEngaged = true, OnScreen = true };
        var grownALittle = inUse with { HeapBytes = inUse.BaselineBytes + HeapPolicy.BackgroundThreshold(MemoryPressure.Critical) };
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(grownALittle));
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(grownALittle with { Pressure = MemoryPressure.Critical }));
    }

    [Fact]
    public void A_heap_smaller_than_its_baseline_is_no_growth()
    {
        var shrunk = Parked(heap: 150 * MB, fragmented: 0, baseline: 200 * MB) with { TransportEngaged = true };
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(shrunk with { Pressure = MemoryPressure.Critical }));
    }
}
