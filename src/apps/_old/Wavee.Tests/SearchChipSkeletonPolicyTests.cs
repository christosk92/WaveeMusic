using Xunit;

namespace Wavee.Tests;

/// <summary>S3 #16: Search used to paint a lone "All" tab, then reflow to the full (near-always two-row, ~11-tab)
/// facet row the instant the query's first response landed. <see cref="SearchChipSkeletonPolicy"/> is the gate that
/// keeps a placeholder pill row up instead, pinned here without a page, a service, or the engine.</summary>
public class SearchChipSkeletonPolicyTests
{
    [Fact]
    public void ShowsSkeleton_WhilePending_WithNoChipSourceYet()
        => Assert.True(SearchChipSkeletonPolicy.ShouldShowSkeleton(hasChipSource: false, isPending: true));

    [Fact]
    public void DoesNotShowSkeleton_OnceAChipSourceIsCached_EvenIfALaterFetchIsPending()
        // Switching facet tabs re-triggers the results resource (keyed by facet) but must NOT bring the skeleton
        // back — the tab row itself is already known from the first response.
        => Assert.False(SearchChipSkeletonPolicy.ShouldShowSkeleton(hasChipSource: true, isPending: true));

    [Fact]
    public void DoesNotShowSkeleton_OnceLoadedWithNoChipSource_EvenThoughThereIsNothingToFacetOn()
        // A query that genuinely returns zero chips (empty ChipOrder and TopHits) settles on the plain one-tab
        // "All" row exactly like before this fix — it must not shimmer forever.
        => Assert.False(SearchChipSkeletonPolicy.ShouldShowSkeleton(hasChipSource: false, isPending: false));

    [Fact]
    public void DoesNotShowSkeleton_WhenNotPending_EvenWithAChipSource()
        => Assert.False(SearchChipSkeletonPolicy.ShouldShowSkeleton(hasChipSource: true, isPending: false));
}
