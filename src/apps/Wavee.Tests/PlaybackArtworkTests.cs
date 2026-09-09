using System;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public class PlaybackArtworkTests : PlaybackCatalogTestBase
{
    const string Uri = "spotify:track:tr";
    static readonly Image Cover = new("https://i.scdn.co/image/cover", 300, 300);
    static readonly ArtistRef Artist = new("ar", "spotify:artist:ar", "Arash");
    static readonly AlbumRef Album = new("al", "spotify:album:al", "SUPERMAN");
    static Track Rich() => new("tr", Uri, "Broken Angel", [Artist], Album, 180000, false, Cover, PlayCount: 42);
    static ClusterDelta Cluster(string? image = null) => new("phone", true,
        new RemoteTrack(Uri, "Broken Angel", "", "", "", "", image, 180000),
        "spotify:album:al", false, true, false, 0, 0, 0, 180000, false, RepeatMode.Off, [], []);

    [Fact]
    public async Task ThinInlineObservation_PreservesKnownArtworkAndIdentity()
    {
        await Catalog.SeedAsync(Rich());
        await Catalog.SeedAsync(ContextResolve.Synthetic(Uri));
        var row = Catalog.Queue.ReadTrack(Uri);
        Assert.Equal("Broken Angel", row.Title);
        Assert.Equal(Cover, row.Image);
        Assert.Equal("Arash", row.Artists[0].Name);
        Assert.Equal("SUPERMAN", row.Album.Name);
        Assert.Equal(42, row.PlayCount);
    }

    [Fact]
    public async Task CatalogArtworkArrival_RefreshesViewerAndSurvivesThinHeartbeat()
    {
        var projection = Catalog.Projection();
        Catalog.Cluster(projection, Cluster());
        var item = Catalog.Queue.Current.Rows[0].ItemId;
        var revision = Catalog.Queue.Current.StructuralRevision;
        await Catalog.SeedAsync(Rich());
        Assert.Equal(Cover, projection.CurrentTrack!.Image);
        Assert.Equal("Arash", projection.CurrentTrack.Artists[0].Name);
        Catalog.Cluster(projection, Cluster());
        Assert.Equal(Cover, projection.CurrentTrack.Image);
        Assert.Equal(item, Catalog.Queue.Current.Rows[0].ItemId);
        Assert.Equal(revision, Catalog.Queue.Current.StructuralRevision);
    }

    [Fact]
    public async Task ClusterArtwork_NormalizesBeforeCatalogPublication()
    {
        var projection = Catalog.Projection();
        Catalog.Cluster(projection, Cluster("spotify:image:ab67616d00001e02870c1c64b1d77eb4456e4283"));
        await QueryPublication.Until(() => projection.CurrentTrack?.Image is not null);
        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02870c1c64b1d77eb4456e4283", projection.CurrentTrack!.Image!.Url);
    }

    [Fact]
    public async Task AuthoritativeArtworkRefresh_ReplacesEarlierInlineArtwork()
    {
        var projection = Catalog.Projection();
        Catalog.Cluster(projection, Cluster("https://i.scdn.co/image/old"));
        await Catalog.ObserveAsync(new CatalogObservation(Catalog.Key(Uri, FacetKind.TrackIdentity),
            new TrackIdentityPatch(Image: FieldChange<Image?>.Set(Cover))));
        Assert.Equal(Cover, projection.CurrentTrack!.Image);
        Catalog.Cluster(projection, Cluster("https://i.scdn.co/image/old"));
        Assert.Equal(Cover, projection.CurrentTrack.Image);
    }

    [Fact]
    public async Task AlbumIdentityArrival_UpdatesLinkEvenWithExistingTrackArtwork()
    {
        var projection = Catalog.Projection();
        Catalog.Cluster(projection, Cluster(Cover.Url));
        await Catalog.SeedAsync(Rich());
        Assert.Equal(Album.Uri, projection.CurrentTrack!.Album.Uri);
        Assert.Equal(Album.Name, projection.CurrentTrack.Album.Name);
    }
}
