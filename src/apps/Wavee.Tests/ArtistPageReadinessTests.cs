using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ArtistPageReadinessTests
{
    const string ArtistUri = "spotify:artist:a", TrackUri = "spotify:track:t";
    static readonly CatalogScope Scope = new("spotify", "account-a", "en", "NL", "premium", 1, false);
    static readonly Artist Value = new("a", ArtistUri, "Artist", null);

    static ResourceKey Key(string subject, FacetKind facet, ResourceArguments args = default) => new(Scope, subject, facet, args);

    static ResourceSnapshot Present(ResourceKey key) => ResourceSnapshot.Unknown(key) with { Knowledge = Knowledge.Present };
    static ResourceSnapshot Absent(ResourceKey key) => ResourceSnapshot.Unknown(key) with { Knowledge = Knowledge.Absent };
    static ResourceSnapshot Fetching(ResourceKey key) => ResourceSnapshot.Unknown(key) with { Activity = ResourceActivity.Fetching };
    static ResourceSnapshot Offline(ResourceKey key) => ResourceSnapshot.Unknown(key) with { Activity = ResourceActivity.Offline };
    static ResourceSnapshot Failed(ResourceKey key) => ResourceSnapshot.Unknown(key)
        with { Error = new ResourceError(ResourceErrorKind.Transport, "boom") };

    static QuerySnapshot<Artist> Snapshot(long revision, params ResourceSnapshot[] resources)
    {
        var map = new Dictionary<ResourceKey, ResourceSnapshot>();
        foreach (var resource in resources) map[resource.Key] = resource;
        return new QuerySnapshot<Artist>(revision, 0, Value, new QueryStatus(true, false, false), [])
            { Resources = map, Demanded = resources.Select(resource => resource.Key).ToArray() };
    }

    [Fact]
    public void ReadButUndemandedKeysNeverHoldThePage()
    {
        // A related artist's identity is read while joining the value but sits outside every demanded window, so
        // nothing will fetch it; the page must not wait for it.
        var related = ResourceSnapshot.Unknown(Key("spotify:artist:related", FacetKind.ArtistIdentity));
        var snapshot = Snapshot(5, Present(Key(ArtistUri, FacetKind.ArtistIdentity)), Present(Key(ArtistUri, FacetKind.ArtistOverview)));
        var resources = new Dictionary<ResourceKey, ResourceSnapshot>(snapshot.Resources) { [related.Key] = related };
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot with { Resources = resources }));
        // Demanding that related identity still must not hold the skeleton — related cards are below the fold.
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot with { Demanded = [.. snapshot.Demanded, related.Key] }));
    }

    [Fact]
    public void DiscographyAlbumIdentitiesDoNotHoldTheSkeleton()
    {
        var album = Key("spotify:album:x", FacetKind.AlbumIdentity);
        var snapshot = Snapshot(5,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            Present(Key(ArtistUri, FacetKind.ArtistOverview)),
            Present(Key(ArtistUri, FacetKind.ArtistPopular, new(Limit: 50))),
            Fetching(album));
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void DiscographyRelationPageDoesNotHoldTheSkeleton()
    {
        var snapshot = Snapshot(4,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            Present(Key(ArtistUri, FacetKind.ArtistOverview)),
            Fetching(Key(ArtistUri, FacetKind.ArtistDiscography, new(Limit: 50))));
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void PopularTrackIdentitiesDoNotHoldTheSkeleton()
    {
        // Chart rows already gate through TrackMetadataReadiness. Waiting on every queued track identity kept
        // artist-A on the skeleton for 134 ms after the hero and popular membership were already present.
        var snapshot = Snapshot(4,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            Present(Key(ArtistUri, FacetKind.ArtistOverview)),
            Present(Key(ArtistUri, FacetKind.ArtistPopular, new(Limit: 50))),
            ResourceSnapshot.Unknown(Key(TrackUri, FacetKind.TrackIdentity)) with { Activity = ResourceActivity.Queued });
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void SeedIsNeverALoad()
    {
        var snapshot = Snapshot(0, Present(Key(ArtistUri, FacetKind.ArtistIdentity)), Present(Key(ArtistUri, FacetKind.ArtistOverview)));
        Assert.False(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void CachedIdentityAloneKeepsTheSkeleton()
    {
        // The library knows the name and avatar; the overview and the popular chart are still on the wire.
        var snapshot = Snapshot(3,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            Fetching(Key(ArtistUri, FacetKind.ArtistOverview)),
            Fetching(Key(ArtistUri, FacetKind.ArtistPopular, new(Limit: 50))));
        Assert.False(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void EveryDemandedFactResolvedRevealsOnce()
    {
        var snapshot = Snapshot(5,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            Present(Key(ArtistUri, FacetKind.ArtistOverview)),
            Present(Key(ArtistUri, FacetKind.ArtistPopular, new(Limit: 50))),
            Absent(Key(ArtistUri, FacetKind.ArtistAppearsOn, new(Limit: 50))),
            Present(Key(TrackUri, FacetKind.TrackIdentity)));
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void PresentationFacetsNeverHoldThePage()
    {
        // Play counts and video flags render as per-field states ("0 plays", pending, unavailable).
        var snapshot = Snapshot(5,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            Present(Key(ArtistUri, FacetKind.ArtistOverview)),
            Present(Key(TrackUri, FacetKind.TrackIdentity)),
            Fetching(Key(TrackUri, FacetKind.PlayCount)));
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot));
        Assert.False(ArtistPageReadiness.InitialLoadComplete(snapshot, Array.Empty<FacetKind>()));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("failed")]
    [InlineData("absent")]
    [InlineData("unsupported")]
    public void TerminalAnswersCountAsResolved(string kind)
    {
        var overviewKey = Key(ArtistUri, FacetKind.ArtistOverview);
        var overview = kind switch
        {
            "offline" => Offline(overviewKey),
            "failed" => Failed(overviewKey),
            "absent" => Absent(overviewKey),
            _ => ResourceSnapshot.Unknown(overviewKey) with { Knowledge = Knowledge.Unsupported },
        };
        var snapshot = Snapshot(6, Present(Key(ArtistUri, FacetKind.ArtistIdentity)), overview);
        Assert.True(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void PreLoginOfflineAndSupersededScopesAreStillInFlight()
    {
        // At a cold start the keys are read under the pre-login scope and the transport marks them offline until the
        // session installs; that is not an answer, and neither is a snapshot read under a scope the session left.
        var preLogin = Scope with { ProviderAccount = "", ContextKnown = false };
        var snapshot = Snapshot(3,
            Present(new ResourceKey(preLogin, ArtistUri, FacetKind.ArtistIdentity)),
            ResourceSnapshot.Unknown(new ResourceKey(preLogin, ArtistUri, FacetKind.ArtistOverview)) with { Activity = ResourceActivity.Offline });
        Assert.False(ArtistPageReadiness.InitialLoadComplete(snapshot));
        var known = Snapshot(3, Present(Key(ArtistUri, FacetKind.ArtistIdentity)), Offline(Key(ArtistUri, FacetKind.ArtistOverview)));
        Assert.True(ArtistPageReadiness.InitialLoadComplete(known));
        Assert.False(ArtistPageReadiness.InitialLoadComplete(known with { Status = new QueryStatus(true, false, true, Superseded: true) }));
    }

    [Fact]
    public void BackoffIsStillInFlight()
    {
        var snapshot = Snapshot(6,
            Present(Key(ArtistUri, FacetKind.ArtistIdentity)),
            ResourceSnapshot.Unknown(Key(ArtistUri, FacetKind.ArtistOverview)) with { Activity = ResourceActivity.Backoff });
        Assert.False(ArtistPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void AReadThatHasNotNamedTheArtistFactsIsNotALoad()
    {
        Assert.False(ArtistPageReadiness.InitialLoadComplete(Snapshot(2)));
        Assert.False(ArtistPageReadiness.InitialLoadComplete(Snapshot(2, Present(Key(ArtistUri, FacetKind.ArtistIdentity)))));
        Assert.False(ArtistPageReadiness.InitialLoadComplete(Snapshot(2, Present(Key("spotify:artist:other", FacetKind.ArtistIdentity)),
            Present(Key("spotify:artist:other", FacetKind.ArtistOverview)))));
    }
}
