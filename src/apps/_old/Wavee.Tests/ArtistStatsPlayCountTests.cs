using System;
using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// SpotifyArtistStatsService is DELETED (hydration-facade-plan.md 1.6) and with it this file's WIRE cases:
//   Overview_LandsPlayCountsOnTheSharedTrackRow_NotOnlyInTheArtistProjection
//     -> ArtistHydrationTests.Rich_OneOverviewCall_WrittenStatsOnly (asserts the count on BOTH the projection and the
//        shared track row, plus the stats-only-write rule the raw upsert used to break)
//   WarmArtistWithPlayCounts_IsServedFromTheStore
//     -> ArtistHydrationTests.Rich_FreshStamp_SkipsTheOverviewEntirely (+ .Rich_StaleStamp_RefetchesEvenThoughTheArtistIsAlreadyRich,
//        which the old presence-only gate could not express)
// What did NOT have a home elsewhere is the STORE-MERGE half below: it is a property of StoreEntityMerge.Track, not of
// whatever service happened to write the count, so it is pinned directly on the store.
public class TrackPlayCountMergeTests
{
    const string TrackUri = "spotify:track:t1";

    [Fact]
    public void PlayCount_SurvivesALaterThinTrackWrite()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("t1", TrackUri, "T1", [], new AlbumRef("al1", "spotify:album:al1", "Al"),
            200000, false, null, PlayCount: 20491));

        // What a catalogue hydrate does moments later: a TrackV4 row that knows nothing about plays. Without the
        // merge guard the zero re-persists over the count and the chart shows a duration but no plays, permanently.
        store.UpsertTrack(new Track("t1", TrackUri, "T1", [], new AlbumRef("al1", "spotify:album:al1", ""),
            200000, false, null));

        Assert.Equal(20491, store.GetTrack(TrackUri)?.PlayCount);
    }
}

// The durable half: the chart's play counts persist WITH the chart, so re-fattening cannot lose them to another
// writer of the shared track row. This is the round-trip the artist page actually performs across a restart.
public class ArtistOverviewPlayCountRoundTripTests
{
    const string TrackUri = "spotify:track:t1";

    static Track Row(long plays) =>
        new("t1", TrackUri, "T1", [], new AlbumRef("al1", "spotify:album:al1", ""), 200000, false, null, PlayCount: plays);

    static Artist WithChart(Track top) =>
        new("a1", "spotify:artist:a1", "A1", null, Array.Empty<Album>(), TopTracks: [top]);

    [Fact]
    public void PlayCounts_SurviveTheProjectRefattenRoundTrip_EvenWhenTheTrackRowWasZeroed()
    {
        var doc = ArtistSplit.Project(WithChart(Row(20491)));

        // What actually happens between the two halves: a TrackV4 prefetch / cluster projection rewrites the SHARED
        // row knowing nothing about plays. Re-fatten must not take its zero.
        var refattened = ArtistSplit.Refatten(ArtistSplit.Core(WithChart(Row(20491))), doc, _ => Row(0));

        Assert.Equal(20491, refattened.TopTracks![0].PlayCount);
        Assert.Equal("T1", refattened.TopTracks[0].Title);          // …while the body still comes from the row
    }

    [Fact]
    public void ADocumentWrittenBeforeCountsWereStored_StillLoads_AndKeepsWhateverTheRowHas()
    {
        // The legacy shape: TopTracks as bare uri strings. It must deserialize (not throw away the whole document),
        // and with no stored count the row's own value stands.
        var legacy = JsonSerializer.Deserialize<ArtistOverviewDoc>(
            $$"""{"TopTracks":["{{TrackUri}}"]}""", EntityJson.Default.Options);

        Assert.NotNull(legacy);
        Assert.Equal(TrackUri, legacy!.TopTracks![0].Uri);
        Assert.Equal(0, legacy.TopTracks[0].Plays);

        var refattened = ArtistSplit.Refatten(ArtistSplit.Core(WithChart(Row(0))), legacy, _ => Row(777));
        Assert.Equal(777, refattened.TopTracks![0].PlayCount);
    }

    [Fact]
    public void ProjectedCounts_RoundTripThroughJson()
    {
        var doc = ArtistSplit.Project(WithChart(Row(20491)));
        var json = JsonSerializer.Serialize(doc, EntityJson.Default.ArtistOverviewDoc);
        var back = JsonSerializer.Deserialize<ArtistOverviewDoc>(json, EntityJson.Default.Options);

        Assert.Equal(20491, back!.TopTracks![0].Plays);
        Assert.Equal(TrackUri, back.TopTracks[0].Uri);
    }
}
