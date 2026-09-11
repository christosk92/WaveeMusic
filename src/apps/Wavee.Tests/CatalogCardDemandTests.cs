using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Queries;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogCardDemandTests
{
    static ResourceFetchResult Present(CatalogValue value) => ResourceFetchResult.Present(new ReplaceFacetPatch(value));

    [Fact]
    public async Task SearchDocumentWithoutInlineSeedsRefillsEveryVisibleCardKindAndItsLinkedNames()
    {
        var kinds = new[] { (SearchFacet.Tracks, "track"), (SearchFacet.Albums, "album"),
            (SearchFacet.Artists, "artist"), (SearchFacet.Playlists, "playlist"),
            (SearchFacet.Podcasts, "show"), (SearchFacet.Episodes, "episode") };
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet switch
        {
            FacetKind.TrackIdentity => Present(new TrackIdentityValue("Song", ["spotify:artist:linked"], "spotify:album:linked")),
            FacetKind.AlbumIdentity => Present(new AlbumIdentityValue("Album", ArtistUris: ["spotify:artist:linked"])),
            FacetKind.ArtistIdentity => Present(new ArtistIdentityValue("Artist")),
            FacetKind.PlaylistHeader => Present(new PlaylistHeaderValue(Name: "Playlist", OwnerUri: "spotify:user:owner")),
            FacetKind.UserIdentity => Present(new UserIdentityValue("Owner")),
            FacetKind.ShowIdentity => Present(new ShowIdentityValue("Show")),
            FacetKind.EpisodeIdentity => Present(new EpisodeIdentityValue("Episode", "spotify:show:linked")),
            _ => ResourceFetchResult.Absent(),
        }));
        await using var host = new CatalogQueryTestHost(provider);
        var document = new CatalogDocumentValue(FacetKind.Search, kinds.Select(pair =>
            new CatalogDocumentSection(pair.Item2, pair.Item2,
                [new("one", "spotify:" + pair.Item2 + ":one"), new("two", "spotify:" + pair.Item2 + ":offscreen")])
            { SearchFacet = pair.Item1 }).ToArray());
        await host.AcceptAsync(new(host.Scope, CatalogSubjects.Search("test"), FacetKind.Search,
            new(0, 30, Filter: "All")), document);
        using var retained = host.Data.Queries.Acquire(new Wavee.Core.Catalog.SearchQuery(host.Scope, "test"));
        Assert.Empty(provider.Requests);
        var demand = QueryDemand.Initial;
        retained.SetDemand(demand);
        var result = await host.Data.Queries.ReadOnceAsync(new Wavee.Core.Catalog.SearchQuery(host.Scope, "test"), demand);
        Assert.Equal("Song", result.Value.Tracks[0].Title);
        Assert.Equal("Album", result.Value.Albums[0].Name);
        Assert.Equal("Artist", result.Value.Artists[0].Name);
        Assert.Equal("Playlist", result.Value.Playlists[0].Name);
        Assert.Equal("Owner", result.Value.Playlists[0].OwnerName);
        Assert.Equal("Show", result.Value.Shows![0].Name);
        Assert.Equal("Episode", result.Value.Episodes![0].Title);
        Assert.Equal("Show", result.Value.Episodes[0].ShowName);
        Assert.Equal("Artist", result.Value.Albums[0].Artists[0].Name);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:track:offscreen" && key.Facet == FacetKind.TrackIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:album:offscreen" && key.Facet == FacetKind.AlbumIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:artist:offscreen" && key.Facet == FacetKind.ArtistIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:playlist:offscreen" && key.Facet == FacetKind.PlaylistHeader);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:show:offscreen" && key.Facet == FacetKind.ShowIdentity);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:episode:offscreen" && key.Facet == FacetKind.EpisodeIdentity);
        long order = retained.Current.OrderRevision;
        await host.AcceptAsync(new(host.Scope, "spotify:album:one", FacetKind.AlbumIdentity), new AlbumIdentityValue("Corrected"));
        await QueryPublication.Until(() => retained.Current.Value.Albums[0].Name == "Corrected");
        Assert.Equal("Corrected", retained.Current.Value.Albums[0].Name);
        Assert.Equal(order, retained.Current.OrderRevision);
    }

    [Fact]
    public async Task ArtistPreviewRequestsSinglesCompilationsAndAppearsOnWithoutInlineIdentitySeeds()
    {
        const string artist = "spotify:artist:artist";
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet switch
        {
            FacetKind.ArtistIdentity => Present(new ArtistIdentityValue("Artist")),
            FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn => Present(new RelationPageValue(request.Key.Facet,
                "one", null, 0, 1, null, RelationCoverage.Complete,
                [new("one", "spotify:album:" + (request.Key.Arguments.Filter ?? "appears"))])),
            FacetKind.AlbumIdentity => Present(new AlbumIdentityValue(request.Key.Subject,
                Kind: request.Key.Subject.EndsWith("Singles", StringComparison.Ordinal) ? AlbumKind.Single
                    : request.Key.Subject.EndsWith("Compilations", StringComparison.Ordinal) ? AlbumKind.Compilation : AlbumKind.Album)),
            _ => ResourceFetchResult.Absent(),
        }));
        await using var host = new CatalogQueryTestHost(provider);
        var result = await host.Data.Queries.ReadOnceAsync(new ArtistDetailQuery(host.Scope, artist));
        Assert.Equal(new[] { AlbumKind.Album, AlbumKind.Single, AlbumKind.Compilation }, result.Value.TopAlbums!.Select(album => album.Kind));
        Assert.All(result.Value.TopAlbums!, album => Assert.NotEmpty(album.Name));
        Assert.NotEmpty(Assert.Single(result.Value.AppearsOn!).Name);
        Assert.Equal(1, result.Value.AlbumsTotal); Assert.Equal(1, result.Value.SinglesTotal); Assert.Equal(1, result.Value.CompilationsTotal);
        Assert.Equal(3, provider.Requests.Count(key => key.Facet == FacetKind.ArtistDiscography));
    }

    [Fact]
    public async Task RetainedArtistCollectionPagesThinMembershipThenDemandsEveryReleaseIdentity()
    {
        const string artist = "spotify:artist:long";
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet switch
        {
            FacetKind.ArtistDiscography => Present(new RelationPageValue(FacetKind.ArtistDiscography, "edition", null,
                request.Key.Arguments.Offset, 75, request.Key.Arguments.Offset == 0 ? "50" : null,
                request.Key.Arguments.Offset == 0 ? RelationCoverage.Partial : RelationCoverage.Complete,
                Enumerable.Range(request.Key.Arguments.Offset, request.Key.Arguments.Offset == 0 ? 50 : 25)
                    .Select(i => new CatalogRelationItem("row:" + i, "spotify:album:" + request.Key.Arguments.Filter + i)).ToArray())),
            FacetKind.AlbumIdentity => Present(new AlbumIdentityValue("Release " + request.Key.Subject, Year: 2020)),
            _ => ResourceFetchResult.Absent(),
        }));
        await using var host = new CatalogQueryTestHost(provider);
        var spec = new ArtistReleasesQuery(host.Scope, artist);
        using var retained = host.Data.Queries.Acquire(spec);
        var thin = QueryDemand.Initial;
        retained.SetDemand(thin);
        var membership = await host.Data.Queries.ReadOnceAsync(spec, thin);
        Assert.Equal(225, membership.Value.Items.Count); Assert.Equal(225, membership.Value.Total);
        Assert.Equal(6, provider.Requests.Count(key => key.Facet == FacetKind.ArtistDiscography));
        Assert.Equal("spotify:album:Albums74", membership.Value.Items[74].Uri);
        Assert.Equal("spotify:album:Singles0", membership.Value.Items[75].Uri);
        Assert.Equal("spotify:album:Compilations0", membership.Value.Items[150].Uri);
        Assert.All(membership.Value.Items, album => Assert.NotEmpty(album.Name));
        Assert.Equal(225, provider.Requests.Count(key => key.Facet == FacetKind.AlbumIdentity));
        Assert.DoesNotContain(provider.Requests, key => key.Facet is FacetKind.AlbumTracks or FacetKind.AlbumDetail or FacetKind.Publishing);
        long order = retained.Current.OrderRevision;
        retained.SetDemand(QueryDemand.None);
        await host.AcceptAsync(new(host.Scope, "spotify:album:Albums74", FacetKind.AlbumIdentity), new AlbumIdentityValue("Corrected"));
        await QueryPublication.Until(() => retained.Current.Value.Items[74].Name == "Corrected");
        Assert.Equal("Corrected", retained.Current.Value.Items[74].Name);
        Assert.Equal(order, retained.Current.OrderRevision);
    }

    [Fact]
    public async Task QueueDemandLoadsEveryRowIdentityAtPlaybackPriority()
    {
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet == FacetKind.TrackIdentity
            ? Present(new TrackIdentityValue("Resolved " + request.Key.Subject)) : ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        using var projection = new NowPlayingProjection("test", host.Data.PlaybackQueue);
        var session = new PlaybackSession(host.Data.PlaybackQueue);
        projection.ApplyLocalSnapshot(session.SetContext("spotify:playlist:list", Enumerable.Range(0, 12)
            .Select(i => new QueuedTrack(ContextResolve.Synthetic("spotify:track:" + i), "row" + i)).ToArray(), 0));
        using var retained = host.Data.Queries.Acquire(new QueueQuery(host.Scope));
        await QueryPublication.Initial(retained);
        var demand = new QueryDemand(true, QueryPriority.Playback, []);
        retained.SetDemand(demand);
        var snapshot = await host.Data.Queries.ReadOnceAsync(new QueueQuery(host.Scope), demand);
        Assert.All(snapshot.Value.Rows, row => Assert.StartsWith("Resolved", row.Track.Title));
        Assert.Equal(12, provider.Requests.Count(key => key.Facet == FacetKind.TrackIdentity));
        Assert.Equal(snapshot.OrderRevision, retained.Current.OrderRevision);
    }
}
