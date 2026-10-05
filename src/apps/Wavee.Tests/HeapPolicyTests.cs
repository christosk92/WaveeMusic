// ── Wavee.Tests/HeapPolicyTests.cs — when the memory governor asks the GC for its dead large objects back ──────────
//
// `Platform/Residency.Heap.cs`'s decision is pure, so its boundaries are pinned here with no GC and no window: a blocking
// compaction never while audio plays (the managed feed thread would stall past the ~100 ms device FIFO), only when nobody
// is looking, and rate-limited; a background collection whenever enough has piled up, sooner under pressure.
using Xunit;

namespace Wavee.Tests;

public class HeapPolicyTests
{
    const long MB = 1L << 20;

    /// <summary>A heap with plenty to give back, a parked window and a silent app: everything allows a compaction.</summary>
    static HeapFacts Parked(long heap = 400 * MB, long fragmented = 120 * MB, long baseline = 200 * MB) => new(
        HeapBytes: heap, FragmentedBytes: fragmented, BaselineBytes: baseline, Audible: false, OnScreen: false, IdleMs: 0,
        MsSinceCompact: long.MaxValue, MsSinceBackground: long.MaxValue, Pressure: MemoryPressure.Normal);

    [Fact]
    public void A_parked_silent_app_with_free_space_compacts()
        => Assert.Equal(HeapAction.Compact, HeapPolicy.Decide(Parked()));

    [Fact]
    public void Never_a_blocking_collection_while_audio_plays()
    {
        var playing = Parked() with { Audible = true };
        // …but the dead objects still go, concurrently.
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(playing));
        Assert.False(HeapPolicy.MayBlock(audible: true, onScreen: false, idleMs: long.MaxValue));
    }

    [Fact]
    public void An_on_screen_window_in_use_never_compacts()
    {
        var inUse = Parked() with { OnScreen = true, IdleMs = 2_000 };
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(inUse));
    }

    [Fact]
    public void Minutes_without_input_count_as_nobody_looking()
    {
        var away = Parked() with { OnScreen = true, IdleMs = HeapPolicy.IdleMsForCompact };
        Assert.Equal(HeapAction.Compact, HeapPolicy.Decide(away));
        Assert.NotEqual(HeapAction.Compact, HeapPolicy.Decide(away with { IdleMs = HeapPolicy.IdleMsForCompact - 1 }));
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
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(tidy with { Audible = true, OnScreen = true }));
    }

    [Fact]
    public void Background_collections_need_growth_and_are_rate_limited()
    {
        var inUse = Parked(fragmented: 0) with { Audible = true, OnScreen = true };
        long threshold = HeapPolicy.BackgroundThreshold(MemoryPressure.Normal);

        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(inUse with { HeapBytes = inUse.BaselineBytes + threshold }));
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(inUse with { HeapBytes = inUse.BaselineBytes + threshold - 1 }));
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(inUse with { MsSinceBackground = HeapPolicy.MinMsBetweenBackground - 1 }));
    }

    [Fact]
    public void Pressure_lowers_the_background_threshold()
    {
        Assert.True(HeapPolicy.BackgroundThreshold(MemoryPressure.Moderate) < HeapPolicy.BackgroundThreshold(MemoryPressure.Normal));
        Assert.True(HeapPolicy.BackgroundThreshold(MemoryPressure.Critical) < HeapPolicy.BackgroundThreshold(MemoryPressure.Moderate));

        var inUse = Parked(fragmented: 0) with { Audible = true, OnScreen = true };
        var grownALittle = inUse with { HeapBytes = inUse.BaselineBytes + HeapPolicy.BackgroundThreshold(MemoryPressure.Critical) };
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(grownALittle));
        Assert.Equal(HeapAction.Background, HeapPolicy.Decide(grownALittle with { Pressure = MemoryPressure.Critical }));
    }

    [Fact]
    public void A_heap_smaller_than_its_baseline_is_no_growth()
    {
        var shrunk = Parked(heap: 150 * MB, fragmented: 0, baseline: 200 * MB) with { Audible = true };
        Assert.Equal(HeapAction.None, HeapPolicy.Decide(shrunk with { Pressure = MemoryPressure.Critical }));
    }
}
