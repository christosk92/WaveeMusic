using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogCollectionDemandTests
{
    [Fact]
    public async Task SidebarDemandLoadsEveryPlaylistHeaderAndSavedIdentity()
    {
        var provider = new Identities();
        await using var host = new CatalogQueryTestHost(provider);
        await Save(host, "collection", "spotify:album:a");
        await Save(host, "artist", "spotify:artist:a");
        await Rootlist(host, "spotify:playlist:first", "spotify:playlist:second");
        var result = await host.Data.Queries.ReadOnceAsync(new SidebarLibraryQuery(host.Scope),
            QueryDemand.Initial, TestContext.Current.CancellationToken);

        Assert.Equal(["Name first", "Name second"], result.Value.Playlists.Select(item => item.Name));
        Assert.Equal("Name a", result.Value.Albums[0].Name);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:playlist:first" && key.Facet == FacetKind.PlaylistHeader);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:playlist:second" && key.Facet == FacetKind.PlaylistHeader);
        Assert.Contains(provider.Requests, key => key.Subject == "spotify:album:a" && key.Facet == FacetKind.AlbumIdentity);
        Assert.All(result.Value.Playlists, item => Assert.Equal(0, item.TrackCount));
    }

    [Fact]
    public async Task AlbumCreatorDemandDiscoversEveryArtistNameAndOptionalFacet()
    {
        var provider = new Identities();
        await using var host = new CatalogQueryTestHost(provider);
        await Save(host, "collection", "spotify:album:a", "spotify:album:b", "spotify:album:c");
        var result = await host.Data.Queries.ReadOnceAsync(new SavedAlbumsQuery(host.Scope),
            new(true, QueryPriority.Visible, [FacetKind.VisualIdentity]), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Value.Count);
        Assert.All(result.Value, album => Assert.Equal("Name " + album.Id, Assert.Single(album.Artists).Name));
        var requests = provider.Requests.ToArray();
        Assert.Equal(3, requests.Count(key => key.Facet == FacetKind.AlbumIdentity));
        Assert.Equal(3, requests.Count(key => key.Facet == FacetKind.ArtistIdentity));
        Assert.All(requests.Where(key => key.Facet == FacetKind.ArtistIdentity),
            key => Assert.Equal(EntityKind.Artist, EntityUri.KindOf(key.Subject)));
        Assert.Equal(3, requests.Count(key => key.Facet == FacetKind.VisualIdentity));
        Assert.DoesNotContain(requests, key => key.Facet is FacetKind.AlbumDetail or FacetKind.AlbumTracks or FacetKind.ArtistOverview);
    }

    [Fact]
    public async Task MembershipArrivingAfterActivationSchedulesEverySavedArtistIdentity()
    {
        var provider = new Identities();
        await using var host = new CatalogQueryTestHost(provider);
        using var handle = host.Data.Queries.Acquire(new SavedArtistsQuery(host.Scope));
        var named = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = handle.Changes.Subscribe(Observers.From<QuerySnapshot<IReadOnlyList<Artist>>>(snapshot =>
        {
            if (snapshot.Value.Count == 3 && snapshot.Value.All(artist => artist.Name.Length > 0)) named.TrySetResult();
        }));
        handle.SetDemand(QueryDemand.Initial);
        Assert.Empty(provider.Requests);
        await Save(host, "artist", "spotify:artist:a", "spotify:artist:b", "spotify:artist:c");
        await named.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.All(handle.Current.Value, artist => Assert.StartsWith("Name ", artist.Name));
        Assert.Equal(3, provider.Requests.Count);
        Assert.All(provider.Requests, key => Assert.Equal(FacetKind.ArtistIdentity, key.Facet));
    }

    static Task Save(CatalogQueryTestHost host, string set, params string[] uris)
        => host.Data.Replicas.AdoptCollectionAsync(new(set, true, true, 0, null, "known",
            uris.Select((uri, index) => new CollectionItem(uri, false, uris.Length - index)).ToImmutableArray()));
    static Task Rootlist(CatalogQueryTestHost host, params string[] uris)
        => host.Data.Replicas.AdoptRootlistAsync(new(uris.Select((uri, index) => new RootlistEntry(index, 0, uri, null, 0)).ToImmutableArray(), new byte[24]));

    sealed class Identities : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public ConcurrentQueue<ResourceKey> Requests { get; } = new();
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            var rows = new List<ResourceResponse>();
            foreach (var request in requests)
            {
                Requests.Enqueue(request.Key);
                string id = EntityUri.IdOf(request.Key.Subject);
                CatalogValue? value = request.Key.Facet switch
                {
                    FacetKind.PlaylistHeader => new PlaylistHeaderValue(Name: "Name " + id),
                    FacetKind.AlbumIdentity => new AlbumIdentityValue("Name " + id, ArtistUris: ["spotify:artist:" + id]),
                    FacetKind.ArtistIdentity => new ArtistIdentityValue("Name " + id),
                    _ => null,
                };
                rows.Add(new(request, value is null ? new ResourceFetchResult(ResourceFetchStatus.Unsupported)
                    : ResourceFetchResult.Present(new ReplaceFacetPatch(value))));
            }
            return ValueTask.FromResult<IReadOnlyList<ResourceResponse>>(rows);
        }
    }
}
