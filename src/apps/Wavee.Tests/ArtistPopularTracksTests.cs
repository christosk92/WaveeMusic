using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The artist chart's PURE fold (merge + play-count top-up + the shared caps). The fetch half moved into the artist
// ladder's Full rung (ArtistHydrationTests); what stays here is what must hold with no transport at all.
public class ArtistPopularMergeTests
{
    static Track T(string id, long plays = 0) =>
        new(id, "spotify:track:" + id, "T" + id, [], new AlbumRef("", "", ""), 1000, false, null, PlayCount: plays);

    [Fact]
    public void Merge_KeepsTheSeedHeadAndItsPlayCounts()
    {
        // The extension endpoint carries uris only — every play count in the chart comes from the overview seed. If the
        // extension copy of a shared uri won, the top rows would silently lose their "N plays" subline.
        var seed = new[] { T("a", 500), T("b", 400) };
        var ext = new[] { T("b"), T("a"), T("c") };

        var merged = ArtistPopularTracks.Merge(seed, ext);

        Assert.Equal(["spotify:track:a", "spotify:track:b", "spotify:track:c"], merged.Select(t => t.Uri));
        Assert.Equal(500, merged[0].PlayCount);
        Assert.Equal(400, merged[1].PlayCount);
        Assert.Equal(0, merged[2].PlayCount);   // no invented play count for tracks 11+
    }

    [Fact]
    public void Merge_AppendsExtensionOnlyTracksInExtensionOrder()
    {
        var merged = ArtistPopularTracks.Merge([T("a")], [T("z"), T("y")]);
        Assert.Equal(["spotify:track:a", "spotify:track:z", "spotify:track:y"], merged.Select(t => t.Uri));
    }

    [Fact]
    public void Merge_EmptyExtension_ReturnsTheSeedUntouched()
    {
        var seed = new[] { T("a", 9), T("b") };
        Assert.Same(seed, ArtistPopularTracks.Merge(seed, Array.Empty<Track>()));
        Assert.Same(seed, ArtistPopularTracks.Merge(seed, null));
    }

    [Fact]
    public void Merge_DropsDuplicateAndUriLessEntries()
    {
        var blank = new Track("x", "", "X", [], new AlbumRef("", "", ""), 0, false, null);
        var merged = ArtistPopularTracks.Merge([T("a")], [T("a"), blank, T("a"), T("b")]);
        Assert.Equal(["spotify:track:a", "spotify:track:b"], merged.Select(t => t.Uri));
    }

    [Fact]
    public void Merge_CapsAtTheExtendedCeiling()
    {
        var ext = Enumerable.Range(0, 200).Select(i => T("e" + i)).ToArray();
        var merged = ArtistPopularTracks.Merge([T("a")], ext);
        Assert.Equal(ArtistPopularTracks.ExtendedCap, merged.Count);
        Assert.Equal("spotify:track:a", merged[0].Uri);   // the seed head survives the cap
    }

    [Fact]
    public void WithPlayCounts_FillsOnlyTheCountlessRows_AndKeepsTheHead()
    {
        // Step three: the overview head is authoritative (a kind-185 count for a head uri is ignored), the tail takes its
        // count, a non-positive count is never applied, and a uri the wire did not answer stays 0.
        var chart = new[] { T("a", 500), T("b"), T("c"), T("d") };
        var counts = new Dictionary<string, long> { ["spotify:track:a"] = 1, ["spotify:track:b"] = 300, ["spotify:track:c"] = 0 };

        var counted = ArtistPopularTracks.WithPlayCounts(chart, counts);

        Assert.NotSame(chart, counted);
        Assert.Equal([500, 300, 0, 0], counted.Select(t => t.PlayCount));
        Assert.Same(chart[0], counted[0]);   // untouched rows keep their identity (the caller diffs by reference)
        Assert.Equal(["spotify:track:c", "spotify:track:d"], ArtistPopularTracks.UrisWithoutPlayCount(counted));
    }

    [Fact]
    public void WithPlayCounts_NothingToApply_ReturnsTheSameInstance()
    {
        var chart = new[] { T("a", 500), T("b") };
        Assert.Same(chart, ArtistPopularTracks.WithPlayCounts(chart, new Dictionary<string, long>()));
        Assert.Same(chart, ArtistPopularTracks.WithPlayCounts(chart, new Dictionary<string, long> { ["spotify:track:z"] = 9 }));
        Assert.Same(chart, ArtistPopularTracks.WithPlayCounts(chart, new Dictionary<string, long> { ["spotify:track:a"] = 9 }));
    }

}

// SpotifyArtistPopularTracksServiceTests is DELETED with the service (hydration-facade-plan.md 1.6). The chart is now
// the artist ladder's FULL rung, so its cases live in ArtistHydrationTests:
//   Ensure_FetchesEnrichesAndMergesIntoTheStore / _FillsTheExtensionTailWithKind185Counts
//     -> Full_ChartGetThenIdentityBatchThenMerge_SeedHeadKeepsItsCounts + Full_AwaitsTheChartTraitPass_ThenReadsTheCountsOffTheRowsItWrote
//   Ensure_AlreadyExtendedAndFresh_SkipsTheNetwork / _ButCountless_TopsUpWithoutTheGet / _ExtendedButStale_Refetches
//     -> Full_AlreadyExtendedAndFreshButCountless_TopsUpWithoutTheGet (+ Rich_StaleStamp_RefetchesEvenThoughTheArtistIsAlreadyRich)
//   Ensure_HttpFailure_KeepsTheSeedAndTheStoredList / _MalformedBody_DegradesToTheSeed / _PlayCountFailure_KeepsTheMergedChart
//     -> Full_ChartFailure_KeepsTheOverviewSeed + Full_EmptyChart_LeavesTheSeedAndAsksForNothingElse
//   Ensure_UnresolvableUris_AreSkippedNotPlaceheld -> Full_ChartGetThenIdentityBatchThenMerge (a uri the Identity batch
//     never lands stays out of the chart projection rather than becoming a placeholder row)
//   Ensure_NonSpotifyArtist_NeverCallsOut -> HydrationRouterTests (owner routing is the router's job, not a per-service prefix test)
//   Ensure_ConcurrentCalls_ShareOneRequest -> HydrationLedgerTests.RunOnce_CoalescesConcurrentCallers
//   Ensure_CancelledCaller_Throws_WithoutKillingTheSharedLoad -> HydrationLedgerTests.RunOnce_AbandonedCaller_DoesNotKillTheSharedRun
//   Ensure_UsesTheRequestedUrl -> SpclientArtistChartFetch owns the url; ArtistHydrationTests pins the ladder's ONE call to it

// FakePlayCounts (the ITrackPlayCountSource double) is GONE with the seam: kind 185 is a trait projector now,
// so both halves of the 185 story are pinned against the pipeline instead — PlayCountProjectorTests (the fill rules and
// the decoder) and ArtistHydrationTests.Full_AwaitsTheChartTraitPass_ThenReadsTheCountsOffTheRowsItWrote (the chart).
