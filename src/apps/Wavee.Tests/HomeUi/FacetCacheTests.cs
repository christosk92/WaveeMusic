// ── Wavee.Tests/HomeUi/FacetCacheTests.cs — TTL edges, offline behaviour, sub-chip prefetch exclusion ─────────────

using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class FacetCacheTests
{
    [Fact]
    public void Never_known_and_online_is_Missing()
    {
        var v = FacetCache.Decide(knowsSections: false, shownThisSession: false, fetchedAtUnixS: 0, nowUnixMs: 0, online: true);
        Assert.Equal(FacetCacheVerdict.Missing, v);
    }

    [Fact]
    public void Known_but_never_shown_this_session_is_Missing_when_online()
    {
        var v = FacetCache.Decide(knowsSections: true, shownThisSession: false, fetchedAtUnixS: 100, nowUnixMs: 100_000, online: true);
        Assert.Equal(FacetCacheVerdict.Missing, v);
    }

    [Fact]
    public void Never_known_and_offline_is_Unavailable()
    {
        var v = FacetCache.Decide(knowsSections: false, shownThisSession: false, fetchedAtUnixS: 0, nowUnixMs: 0, online: false);
        Assert.Equal(FacetCacheVerdict.Unavailable, v);
    }

    [Fact]
    public void Exactly_at_TTL_minus_one_ms_is_Fresh()
    {
        // fetchedAtUnixS=0 -> fetchedAtMs=0; now=599_999 -> age 599_999 < 600_000
        var v = FacetCache.Decide(knowsSections: true, shownThisSession: true, fetchedAtUnixS: 0, nowUnixMs: 599_999, online: true);
        Assert.Equal(FacetCacheVerdict.Fresh, v);
    }

    [Fact]
    public void Exactly_at_TTL_is_Stale()
    {
        var v = FacetCache.Decide(knowsSections: true, shownThisSession: true, fetchedAtUnixS: 0, nowUnixMs: 600_000, online: true);
        Assert.Equal(FacetCacheVerdict.Stale, v);
    }

    [Fact]
    public void Stale_but_offline_still_swaps_instantly_as_Fresh_with_no_refetch()
    {
        var v = FacetCache.Decide(knowsSections: true, shownThisSession: true, fetchedAtUnixS: 0, nowUnixMs: 600_000, online: false);
        Assert.Equal(FacetCacheVerdict.Fresh, v);
    }

    [Fact]
    public void TtlMs_constant_is_ten_minutes()
    {
        Assert.Equal(600_000, FacetCache.TtlMs);
    }

    [Fact]
    public void BackgroundSwapWindowMs_constant_is_two_seconds()
    {
        Assert.Equal(2_000, FacetCache.BackgroundSwapWindowMs);
    }

    [Fact]
    public void PrefetchHoverMs_constant_is_150()
    {
        Assert.Equal(150, FacetCache.PrefetchHoverMs);
    }

    // ── ShouldPrefetch ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sub_chip_is_never_prefetched_even_if_never_fetched()
    {
        bool prefetch = FacetCache.ShouldPrefetch(everFetched: false, fetchedAtUnixS: 0, nowUnixMs: 0, online: true, isSubChip: true);
        Assert.False(prefetch);
    }

    [Fact]
    public void Offline_never_prefetches()
    {
        bool prefetch = FacetCache.ShouldPrefetch(everFetched: false, fetchedAtUnixS: 0, nowUnixMs: 0, online: false, isSubChip: false);
        Assert.False(prefetch);
    }

    [Fact]
    public void Never_fetched_online_top_level_chip_prefetches()
    {
        bool prefetch = FacetCache.ShouldPrefetch(everFetched: false, fetchedAtUnixS: 0, nowUnixMs: 0, online: true, isSubChip: false);
        Assert.True(prefetch);
    }

    [Fact]
    public void Already_fetched_within_ttl_does_not_prefetch_again()
    {
        bool prefetch = FacetCache.ShouldPrefetch(everFetched: true, fetchedAtUnixS: 0, nowUnixMs: 599_999, online: true, isSubChip: false);
        Assert.False(prefetch);
    }

    [Fact]
    public void Already_fetched_past_ttl_prefetches_again()
    {
        bool prefetch = FacetCache.ShouldPrefetch(everFetched: true, fetchedAtUnixS: 0, nowUnixMs: 600_000, online: true, isSubChip: false);
        Assert.True(prefetch);
    }
}
