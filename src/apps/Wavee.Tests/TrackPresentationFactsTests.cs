using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class TrackPresentationFactsTests
{
    static readonly CatalogScope Scope = new("spotify", "test", "en", "NL", "premium", 1, false);
    static readonly ResourceKey CountKey = new(Scope, "spotify:track:a", FacetKind.PlayCount);

    [Fact]
    public void PresentZeroRemainsPresentDuringOfflineOrFailedRefresh()
    {
        var resource = ResourceSnapshot.Unknown(CountKey) with
        {
            Knowledge = Knowledge.Present, Value = new PlayCountValue(0),
            Activity = ResourceActivity.Offline,
            Error = new(ResourceErrorKind.Transport, "offline"),
        };
        Assert.Equal(TrackFactState.Present, TrackFactPresentation.State(resource, queryFailed: true));
    }

    [Theory]
    [InlineData(Knowledge.Absent, TrackFactState.Absent)]
    [InlineData(Knowledge.Unsupported, TrackFactState.Unsupported)]
    public void AuthoritativeNegativeKnowledgeResolvesWithoutInventingAValue(Knowledge knowledge, TrackFactState expected)
    {
        var resource = ResourceSnapshot.Unknown(CountKey) with { Knowledge = knowledge };
        Assert.Equal(expected, TrackFactPresentation.State(resource));
    }

    [Theory]
    [InlineData(ResourceActivity.Idle, TrackFactState.Failed)]
    [InlineData(ResourceActivity.Queued, TrackFactState.Pending)]
    [InlineData(ResourceActivity.Fetching, TrackFactState.Pending)]
    [InlineData(ResourceActivity.Backoff, TrackFactState.Pending)]
    [InlineData(ResourceActivity.Offline, TrackFactState.Offline)]
    public void UnknownCountTracksActualActivity(ResourceActivity activity, TrackFactState expected)
    {
        var resource = ResourceSnapshot.Unknown(CountKey) with
        { Activity = activity, Error = new(ResourceErrorKind.Transport, "temporary") };
        Assert.Equal(expected, TrackFactPresentation.State(resource));
    }

    [Fact]
    public void ArtistPresentationDemandsCountsAndVideoButNotAudioOrDescriptors()
    {
        var facets = TrackPresentationRequirements.RequiredFacets(TrackPresentationRequirements.ArtistPopular);
        Assert.Equal([FacetKind.PlayCount, FacetKind.VideoAssociation], facets);
        Assert.Same(facets, TrackPresentationRequirements.RequiredFacets(TrackPresentationRequirements.ArtistPopular));
        Assert.Empty(TrackPresentationRequirements.RequiredFacets(TrackPresentationFacts.None));
    }
}
