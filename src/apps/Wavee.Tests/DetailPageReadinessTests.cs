using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class DetailPageReadinessTests
{
    const string PlaylistUri = "spotify:playlist:p", TrackUri = "spotify:track:t";
    static readonly CatalogScope Scope = new("spotify", "account-a", "en", "NL", "premium", 1, false);
    static readonly Playlist Value = new("p", PlaylistUri, "Playlist", null, "", null, 1) { MembershipLoaded = true };

    static ResourceKey Key(string subject, FacetKind facet, ResourceArguments args = default) => new(Scope, subject, facet, args);
    static ResourceSnapshot Present(ResourceKey key) => ResourceSnapshot.Unknown(key) with { Knowledge = Knowledge.Present };
    static ResourceSnapshot Queued(ResourceKey key) => ResourceSnapshot.Unknown(key) with { Activity = ResourceActivity.Queued };

    static QuerySnapshot<Playlist> Snapshot(long revision, params ResourceSnapshot[] resources)
    {
        var map = new Dictionary<ResourceKey, ResourceSnapshot>();
        foreach (var resource in resources) map[resource.Key] = resource;
        return new QuerySnapshot<Playlist>(revision, 0, Value, new QueryStatus(true, false, false), [])
            { Resources = map, Demanded = resources.Select(resource => resource.Key).ToArray() };
    }

    [Fact]
    public void MembershipPlusResidentHeaderReveals_UnresolvedIdentitiesDoNotHold()
    {
        // The membership replica is known; two members are still on the wire. Those rows render as loading,
        // they must not keep a cached playlist behind the skeleton (or behind a header revalidation).
        var snapshot = Snapshot(3,
            Present(Key(PlaylistUri, FacetKind.PlaylistHeader)),
            Present(Key(PlaylistUri, FacetKind.PlaylistRevision)),
            Queued(Key(TrackUri, FacetKind.TrackIdentity)));
        Assert.True(DetailPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void EveryRowIdentityLandedRevealsOnce_PresentationFactsNeverHold()
    {
        var snapshot = Snapshot(4,
            Present(Key(PlaylistUri, FacetKind.PlaylistHeader)),
            Present(Key(TrackUri, FacetKind.TrackIdentity)),
            Present(Key("spotify:album:a", FacetKind.AlbumIdentity)),
            Queued(Key(TrackUri, FacetKind.PlayCount)),
            Queued(Key(TrackUri, FacetKind.AudioAttributes)),
            Queued(Key(TrackUri, FacetKind.VideoAssociation)));
        Assert.True(DetailPageReadiness.InitialLoadComplete(snapshot));
    }

    [Fact]
    public void UnknownMembershipNeverReveals()
    {
        // A cached header reads with two demanded keys and no rows; that is not an empty list, it is an unknown one.
        var header = Snapshot(3, Present(Key(PlaylistUri, FacetKind.PlaylistHeader)), Present(Key(PlaylistUri, FacetKind.PlaylistRevision)));
        Assert.True(DetailPageReadiness.InitialLoadComplete(header));
        Assert.False(DetailPageReadiness.InitialLoadComplete(header with { Value = Value with { MembershipLoaded = false } }));
        Assert.False(DetailPageReadiness.MembershipKnown(new Album("a", "spotify:album:a", "Album", null, [], 0, 0)));
        Assert.True(DetailPageReadiness.MembershipKnown(new Album("a", "spotify:album:a", "Album", null, [], 0, 0) { Tracks = [] }));
    }

    [Fact]
    public void SeedEmptyDemandAndSupersededNeverReveal()
    {
        Assert.False(DetailPageReadiness.InitialLoadComplete(Snapshot(0, Present(Key(PlaylistUri, FacetKind.PlaylistHeader)))));
        Assert.False(DetailPageReadiness.InitialLoadComplete(Snapshot(2)));
        var superseded = Snapshot(2, Present(Key(PlaylistUri, FacetKind.PlaylistHeader)))
            with { Status = new QueryStatus(true, false, true, Superseded: true) };
        Assert.False(DetailPageReadiness.InitialLoadComplete(superseded));
    }
}
