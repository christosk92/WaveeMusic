using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

/// <summary>LIST_METADATA_V2 (kind 205) — the playlist arm of the extended-metadata chokepoint.
///
/// A playlist has no V4 catalogue kind, so before this arm existed every playlist uri handed to the metadata
/// chokepoint resolved to <c>UnknownExtension</c> and was dropped before the request was even built. That is why a
/// surface made of playlist POINTERS (recents) had no way to learn a single name.
///
/// The second half of the contract is the one that can actually lose user data: this is a HYDRATION write, not the
/// header WRITER. <c>StoreEntityMerge.Playlist</c> deliberately treats Name/Description/Cover/Capabilities as
/// authoritative (its intended caller is the header patcher, where an absent picture really is ClearPicture), so a
/// name-and-cover hydrate landing on a fully-fetched playlist must carry the resident values through rather than let
/// them be read as a clear.</summary>
public class PlaylistMetadataProjectionTests
{
    const string Uri = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";

    static byte[] Craft(string uri, string? name = "Today's Top Hits", string? description = null,
                        string? source = null, params (string Format, string Url)[] images)
    {
        var meta = new Xm.ListMetadataV2();
        if (name is not null) meta.Name = name;
        if (description is not null) meta.Description = description;
        if (source is not null) meta.Source = source;
        if (images.Length > 0)
        {
            meta.Images = new Xm.ListMetadataV2.Types.Images();
            foreach (var (format, url) in images)
                meta.Images.Variant.Add(new Xm.ListMetadataV2.Types.ImageVariant { Format = format, Url = url });
        }

        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ListMetadataV2 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = uri, ExtensionData = Any.Pack(meta) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    [Fact]
    public void PlaylistUris_ResolveToListMetadataV2_RatherThanBeingDroppedAsUnknown()
    {
        // The gap this arm closes, re-pinned on the ONE routing-kind -> catalogue-kind map now that EntityRef and the
        // unconditional GzipRequest are gone (hydration-facade-plan.md 1.6): a playlist uri classifies as a Playlist and
        // maps to 205 rather than UnknownExtension, which is what the request builders skip on.
        Assert.Equal(Wavee.Core.EntityKind.Playlist, EntityUri.KindOf(Uri));
        Assert.Equal(Xm.ExtensionKind.ListMetadataV2, XmKinds.CatalogKindOf(EntityUri.KindOf(Uri)));
        // …and it IS a projectable catalogue kind, so a 205 payload reaches ProjectResponse rather than being cached
        // and dropped as a trait. (The end-to-end "it survives request construction" half is
        // XmCatalogFetchTests.MixedKinds_RideOnePost.)
        Assert.True(XmKinds.IsCatalogKind(Xm.ExtensionKind.ListMetadataV2));
    }

    [Fact]
    public void ProjectResponse_ProjectsAPlaylistHeader_WithItsCoverAndOwner()
    {
        var store = new InMemoryStore();
        var landed = ExtendedMetadataSource.ProjectResponse(
            Craft(Uri, "Today's Top Hits", description: "The hits.", source: "Spotify",
                  ("default", "https://i.scdn.co/image/abc")), store);

        Assert.Contains(Uri, landed);
        var p = Assert.IsType<Playlist>(store.GetPlaylist(Uri));
        Assert.Equal("Today's Top Hits", p.Name);
        Assert.Equal("The hits.", p.Description);
        Assert.Equal("Spotify", p.OwnerName);
        Assert.Equal("https://i.scdn.co/image/abc", p.Cover?.Url);
    }

    [Fact]
    public void Cover_PrefersTheStandardRenders_ButTakesAnyVariantCarryingAUrl()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(
            Craft(Uri, images: [("tiny", "https://i.scdn.co/image/tiny"), ("large", "https://i.scdn.co/image/large")]),
            store);
        Assert.Equal("https://i.scdn.co/image/large", store.GetPlaylist(Uri)?.Cover?.Url);

        var only = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(Craft(Uri, images: [("tiny", "https://i.scdn.co/image/tiny")]), only);
        Assert.Equal("https://i.scdn.co/image/tiny", only.GetPlaylist(Uri)?.Cover?.Url);
    }

    /// <summary>THE regression this arm is most likely to cause. A viewport hydrate is name+cover only; the resident row
    /// may already hold a full fetch (tracks, capabilities, owner, daylist window) written by PlaylistFetcher.</summary>
    [Fact]
    public void AMetadataOnlyHydrate_OverAFullyLoadedPlaylist_PreservesEverythingItDoesNotKnow()
    {
        var store = new InMemoryStore();
        var tracks = new List<Track>
        {
            new("t1", "spotify:track:t1", "One", Array.Empty<ArtistRef>(), new AlbumRef("a", "spotify:album:a", "A"), 1000, false, null),
            new("t2", "spotify:track:t2", "Two", Array.Empty<ArtistRef>(), new AlbumRef("a", "spotify:album:a", "A"), 2000, false, null),
        };
        var rich = new Playlist("37i9dQZF1DXcBWIGoYBM5M", Uri, "Today's Top Hits", "The full description",
            "Spotify", new Image("https://i.scdn.co/image/rich"), TrackCount: 50, Tracks: tracks,
            Owner: new Owner("spotify", "Spotify", null),
            Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true,
                IsCollaborative: false, IsOwner: true),
            Format: "editorial", Source: "spotify", IsPublic: false, BasePermissionRevision: "rev-7",
            DaylistExpiresAtMs: 1234, DaylistCreatedAtMs: 1000);
        store.UpsertPlaylist(rich);
        store.SetMembership(Uri, [new PlaylistMember("t1", "spotify:track:t1", null, 0)], null);

        // The hydrate the recents viewport actually performs: a name, nothing else.
        ExtendedMetadataSource.ProjectResponse(Craft(Uri, "Today's Top Hits"), store);

        var after = Assert.IsType<Playlist>(store.GetPlaylist(Uri));
        Assert.Equal("Today's Top Hits", after.Name);
        Assert.Equal("The full description", after.Description);        // NOT cleared by an absent description
        Assert.Equal("https://i.scdn.co/image/rich", after.Cover?.Url); // NOT cleared by an absent image set
        Assert.Equal("Spotify", after.OwnerName);
        Assert.NotNull(after.Owner);
        Assert.True(after.Capabilities.CanEditItems);                   // NOT downgraded to default(PlaylistCapabilities)
        Assert.Equal(50, after.TrackCount);
        Assert.Equal(2, after.Tracks?.Count);
        Assert.Equal("editorial", after.Format);
        Assert.False(after.IsPublic);
        Assert.Equal("rev-7", after.BasePermissionRevision);
        Assert.Equal(1234, after.DaylistExpiresAtMs);
        // Membership is a separate plane and must not be touched at all.
        Assert.Single(store.Membership(Uri));
    }

    [Fact]
    public void AnEmptyPayload_WritesNothing_AndStaysUnsealedSoTheNextHydrateRetries()
    {
        var store = new InMemoryStore();
        var landed = ExtendedMetadataSource.ProjectResponse(Craft(Uri, name: null), store);

        Assert.DoesNotContain(Uri, landed);   // outcome seeding: nothing landed ⇒ freshness is not sealed
        Assert.Null(store.GetPlaylist(Uri));
    }

    [Fact]
    public void ANamelessPayload_OverAResidentRow_KeepsTheResidentName()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("x", Uri, "Known Name", null, "Me", null, TrackCount: 0));
        ExtendedMetadataSource.ProjectResponse(Craft(Uri, name: null, source: "Spotify"), store);
        Assert.Equal("Known Name", store.GetPlaylist(Uri)?.Name);
        Assert.Equal("Spotify", store.GetPlaylist(Uri)?.OwnerName);
    }
}
