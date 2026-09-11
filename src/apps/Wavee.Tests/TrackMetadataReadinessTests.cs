using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class TrackMetadataReadinessTests
{
    static readonly ResourceKey Key = new(new("spotify", "a", "en", "NL", "premium", 1, false),
        "spotify:track:t", FacetKind.TrackIdentity);
    static Track Row(string title = "", long duration = 0) => new("t", Key.Subject, title, [], new("a", "spotify:album:a", ""),
        duration, false, null);

    [Fact]
    public void AlbumMembershipAloneDoesNotMakeBlankRowsReady()
    {
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), ResourceSnapshot.Unknown(Key)));
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(Key.Subject), null));
        Assert.Equal(TrackTitleState.Ready, TrackMetadataReadiness.Title(Row("A song"), ResourceSnapshot.Unknown(Key)));
        // Present via an inline seed (e.g. a gid-only disc track) with no title and no in-flight work (Activity
        // Idle) has no label coming — Unavailable (Retry), never an eternal Loading (Part 5 item 3d).
        var seed = ResourceSnapshot.Unknown(Key) with { Knowledge = Knowledge.Present, Provenance = CatalogProvenance.InlineSeed };
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), seed));
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), seed with
            { Provenance = CatalogProvenance.Provider, Activity = ResourceActivity.Fetching }));
    }

    [Fact]
    public void SeededNamelessIsUnavailable_QueuedAlwaysLoads_TitledAlwaysReady()
    {
        var seed = ResourceSnapshot.Unknown(Key) with { Knowledge = Knowledge.Present, Provenance = CatalogProvenance.InlineSeed };
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), seed));
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), seed with { Activity = ResourceActivity.Queued }));
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), ResourceSnapshot.Unknown(Key) with { Activity = ResourceActivity.Queued }));
        // A cached title is Ready no matter what the identity resource is doing underneath it.
        Assert.Equal(TrackTitleState.Ready, TrackMetadataReadiness.Title(Row("A song"), seed with { Activity = ResourceActivity.Queued }));
        Assert.Equal(TrackTitleState.Ready, TrackMetadataReadiness.Title(Row("A song"), seed));
    }

    // The identity resource and the row's Track come out of ONE query as TWO publications: the resources dictionary
    // flips to Present the frame the extended-metadata batch answers, and the model's Track records are rebuilt from it
    // a background projection later. Every row of a freshly opened playlist spends one to three frames as a title-less
    // Track paired with an ANSWERED identity, and reading that as terminal flashed "Track details unavailable" with a
    // Retry button across the whole viewport on every navigation. An answer that carries the label is a row waiting for
    // its model to catch up, not a row with nothing coming.
    [Fact]
    public void AnsweredIdentityCarryingATitleWaitsForTheModelInsteadOfFlashingUnavailable()
    {
        var answered = ResourceSnapshot.Unknown(Key) with
        {
            Knowledge = Knowledge.Present, Provenance = CatalogProvenance.Provider,
            Activity = ResourceActivity.Idle, Value = new TrackIdentityValue("A song"),
        };
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), answered));
        // …and the moment the model catches up the row is Ready off its own Track, as before.
        Assert.Equal(TrackTitleState.Ready, TrackMetadataReadiness.Title(Row("A song"), answered));
        // An inline seed that somehow carries a label is the same story — the label is known either way.
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(),
            answered with { Provenance = CatalogProvenance.InlineSeed }));
        // The genuinely unavailable case is untouched: an answer with NO label still terminates.
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(),
            answered with { Value = new TrackIdentityValue(null) }));
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), answered with { Value = null }));
        // A titled answer does not override a real query failure — that is a page-level state, not a row's.
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), answered, queryFailed: true));
    }

    [Theory]
    [InlineData(Knowledge.Present)]
    [InlineData(Knowledge.Absent)]
    [InlineData(Knowledge.Unsupported)]
    public void CompletedAnswerWithoutATitleIsUnavailable(Knowledge knowledge)
        => Assert.Equal(TrackTitleState.Unavailable,
            TrackMetadataReadiness.Title(Row(), ResourceSnapshot.Unknown(Key) with { Knowledge = knowledge }));

    [Fact]
    public void AFailedColdReadOfAnUnknownRowKeepsLoading()
    {
        // The local cache could not be read, but the key is still Unknown and the provider fetch decides. Presenting
        // it as "unavailable" flashed a terminal state on rows that filled in a few frames later.
        var unread = ResourceSnapshot.Unknown(Key) with { Error = new(ResourceErrorKind.Persistence, "database is locked") };
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), unread));
        Assert.Equal(TrackTitleState.Loading, TrackMetadataReadiness.Title(Row(), unread with { Activity = ResourceActivity.Fetching }));
        Assert.Equal(TrackTitleState.Offline, TrackMetadataReadiness.Title(Row(), unread with { Activity = ResourceActivity.Offline }));
        // A provider answer that carries a persistence error (the write failed after the fetch) is still an answer.
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), unread with { Knowledge = Knowledge.Absent }));
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(),
            ResourceSnapshot.Unknown(Key) with { Error = new(ResourceErrorKind.Transport, "timeout") }));
    }

    [Fact]
    public void FailedAndOfflineRowsStopLoading_AndCachedTitlesSurvive()
    {
        var failed = ResourceSnapshot.Unknown(Key) with { Error = new(ResourceErrorKind.Forbidden, "failed") };
        var offline = ResourceSnapshot.Unknown(Key) with { Activity = ResourceActivity.Offline };
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), failed));
        Assert.Equal(TrackTitleState.Unavailable, TrackMetadataReadiness.Title(Row(), null, queryFailed: true));
        Assert.Equal(TrackTitleState.Offline, TrackMetadataReadiness.Title(Row(), offline));
        Assert.Equal(TrackTitleState.Ready, TrackMetadataReadiness.Title(Row("Cached"), failed));
        Assert.Equal(TrackTitleState.Ready, TrackMetadataReadiness.Title(Row("Cached"), offline));
    }

    [Fact]
    public void PartialDurationsNeverBecomeACollectionTotal()
    {
        var known = Row("Known", 180_000);
        Assert.Null(TrackMetadataReadiness.CompleteDuration([known, Row()], 2));
        Assert.Null(TrackMetadataReadiness.CompleteDuration([known], 323));
        Assert.Null(TrackMetadataReadiness.CompleteDuration([known], 1, membershipLoaded: false));
        Assert.Null(TrackMetadataReadiness.CompleteDuration([], 0));
        Assert.Equal(360_000, TrackMetadataReadiness.CompleteDuration([known, known], 2));
    }

    [Fact]
    public void ReleaseFactsWaitForAllReleasedDurationsAndCompleteMembership()
    {
        var tracks = new[] { Row("Known", 180_000), Row("Unknown") };
        var partial = AlbumReleaseFactsRules.For(tracks, null, null, null, null, null, null, null, DateTimeOffset.UtcNow);
        Assert.Equal("2", partial.Songs);
        Assert.Null(partial.Length);
        var partialMembership = AlbumReleaseFactsRules.For([tracks[0]], null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow, expectedCount: 5);
        Assert.Null(partialMembership.Songs);
        Assert.Null(partialMembership.Length);
    }
}
