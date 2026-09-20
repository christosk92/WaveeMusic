// ── Wavee.Tests/SearchChipSkeletonPolicyTests.cs — the eleven-pill chip skeleton gate (ch 13 §8) ──────────────────
//
// Ported verbatim from 0.2.9 `SearchChipSkeletonPolicyTests`. The 0.2.9 `SearchChipSkeletonPolicy.ShouldShowSkeleton`
// is `Search.ShowChipSkeleton` in 0.3 (Entities/Search.cs) — same two inputs, same answer; only the call shape changed.

using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>S3 #16: Search used to paint a lone "All" tab, then reflow to the full (near-always two-row, ~11-tab)
/// facet row the instant the query's first response landed. <see cref="Search.ShowChipSkeleton"/> is the gate that
/// keeps a placeholder pill row up instead, pinned here without a page, a service, or the engine.</summary>
public class SearchChipSkeletonPolicyTests
{
    [Fact]
    public void ShowsSkeleton_WhilePending_WithNoChipSourceYet()
        => Assert.True(Search.ShowChipSkeleton(hasChipSource: false, pending: true));

    [Fact]
    public void DoesNotShowSkeleton_OnceAChipSourceIsCached_EvenIfALaterFetchIsPending()
        // Switching facet tabs re-triggers the results resource (keyed by facet) but must NOT bring the skeleton
        // back — the tab row itself is already known from the first response.
        => Assert.False(Search.ShowChipSkeleton(hasChipSource: true, pending: true));

    [Fact]
    public void DoesNotShowSkeleton_OnceLoadedWithNoChipSource_EvenThoughThereIsNothingToFacetOn()
        // A query that genuinely returns zero chips (empty ChipOrder and TopHits) settles on the plain one-tab
        // "All" row exactly like before this fix — it must not shimmer forever.
        => Assert.False(Search.ShowChipSkeleton(hasChipSource: false, pending: false));

    [Fact]
    public void DoesNotShowSkeleton_WhenNotPending_EvenWithAChipSource()
        => Assert.False(Search.ShowChipSkeleton(hasChipSource: true, pending: false));

    [Fact]
    public void TheSkeletonIsTheElevenPillFacetSuperset()
    {
        // The placeholder row is the known facet superset (All + the ten fallback facets), so it wraps to the same two
        // rows the real strip usually does.
        Assert.Equal(SearchTable.FacetCount, Search.ChipSkeletonWidths.Length);
        Assert.Equal(SearchTable.FacetCount - 1, Search.FallbackFacets.Length);
        foreach (float w in Search.ChipSkeletonWidths) Assert.True(w > 0f);
    }

    /// <summary>The tabs used to paint a beat before any result: the chips and the ranked hits land in one commit, but
    /// the body reveals through a region effect plus the staggered row reveal while the chip row swapped on the spot.
    /// <see cref="Search.ChipRowSkeletal"/> holds the skeleton until the body has answered once for the query; the
    /// latch means a later facet fetch (a tab switch) never brings the skeleton back.</summary>
    [Theory]
    [InlineData(false, true, false, true)]    // nothing yet: the plain ShowChipSkeleton case
    [InlineData(true, false, false, true)]    // THE defect: chips landed, body not answered → still skeletal
    [InlineData(true, false, true, false)]    // chips and body both landed → the real row
    [InlineData(false, false, true, false)]   // zero chips, body answered → the plain one-tab row, no endless shimmer
    [InlineData(true, true, true, false)]     // a later facet fetch pending after the first answer → stays real
    public void ChipRow_holds_the_skeleton_until_the_first_body_answered(bool hasChipSource, bool chipsPending, bool firstBodyAnswered, bool skeletal)
        => Assert.Equal(skeletal, Search.ChipRowSkeletal(hasChipSource, chipsPending, firstBodyAnswered));
}
