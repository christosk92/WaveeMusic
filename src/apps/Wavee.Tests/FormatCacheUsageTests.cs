using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

// FormatCache<TKey> (FluentGpu.Foundation, referenced transitively via FluentGpu.WindowsApi) is what
// BoundItemScope<T>.Text<T,TKey> leans on so a virtualized row's formatted string (date-added, plays, tempo) is
// reused across a recycle instead of rebuilt — Operation ultra-fast, P5 slice 3
// (docs/plans/wavee/operation-ultra-fast-app-progress.md). TrackRowTemplate's own DateCache/PlaysCache/BpmCache
// instances are private statics on a FluentGpu-bound file Wavee.Tests cannot source-include (same constraint
// TrackRowTemplateTests documents), so this pins the SHARED MECHANISM those call sites rely on: a cache entry is
// reused (identity-stable) across repeated Get calls with an equal key, distinct keys format independently, and the
// cache is BOUNDED (Capacity) rather than growing without bound for an unbounded key domain.
public sealed class FormatCacheUsageTests
{
    [Fact]
    public void GetFormatsOnceAndReusesTheSameStringOnAnEqualKey()
    {
        int calls = 0;
        var cache = new FormatCache<int>();
        string Formatter(int k) { calls++; return "v" + k; }

        string first = cache.Get(5, Formatter);
        string second = cache.Get(5, Formatter);

        Assert.Equal("v5", first);
        // Identity-stable: the SAME string instance comes back on a cache hit, never a fresh allocation.
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DistinctKeysFormatIndependently()
    {
        var cache = new FormatCache<int>();
        Assert.Equal("a1", cache.Get(1, static k => "a" + k));
        Assert.Equal("a2", cache.Get(2, static k => "a" + k));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void OverflowingCapacityClearsTheWholeTableRatherThanGrowingUnbounded()
    {
        var cache = new FormatCache<int>();
        for (int i = 0; i < FormatCache<int>.Capacity; i++) cache.Get(i, static k => k.ToString());
        Assert.Equal(FormatCache<int>.Capacity, cache.Count);

        // One more distinct key past the cap clears the table first (no LRU bookkeeping) and starts over at 1 —
        // the cache never grows past Capacity, which is what keeps an unbounded key domain (e.g. a live date range)
        // from becoming the very leak the cache exists to prevent.
        cache.Get(FormatCache<int>.Capacity, static k => k.ToString());
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void KeyedCacheWithATupleKeySupportsMultiFieldFormatting()
    {
        // TrackRowTemplate's Plays cell keys on (count, state) — a value-tuple key, exactly like this.
        var cache = new FormatCache<(long Count, byte State)>();
        string a = cache.Get((10L, 1), static k => $"{k.Count}:{k.State}");
        string b = cache.Get((10L, 1), static k => $"{k.Count}:{k.State}");
        string c = cache.Get((11L, 1), static k => $"{k.Count}:{k.State}");
        Assert.Same(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DenseIntCacheNeverClearsAndReturnsTheSameInstanceForARepeatedValue()
    {
        // FormatCache.Int (row numbers, play counts) — the OTHER cache shape BoundItemScope<T>.Number leans on:
        // grow-only, never cleared (bounded key DOMAIN, not count), unlike the keyed FormatCache<TKey> above.
        string first = FormatCache.Int(42);
        string second = FormatCache.Int(42);
        Assert.Equal("42", first);
        Assert.Same(first, second);
    }

    [Fact]
    public void DurationMmSsIsStableAcrossCallsForTheSameWholeSecond()
    {
        // Sub-second precision never reaches the label — two millisecond values in the same whole second cache-hit
        // the same formatted string (the Duration cell's own rule).
        string a = FormatCache.DurationMmSs(65_000);
        string b = FormatCache.DurationMmSs(65_400);
        Assert.Equal("1:05", a);
        Assert.Same(a, b);
    }
}
