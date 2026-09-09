using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogResourceNativeTests
{
    [Fact]
    public async Task OfflineLocalProvider_CommitsOriginalTrackOriginThroughTheSharedRepository()
    {
        await using var fixture = new CatalogFixture();
        var source = new LocalSource();
        var registry = new SourceRegistry([source]);
        var provider = new NativeCatalogResourceProvider(registry, source);
        await fixture.Repository.SetSessionAsync(fixture.Scope, "account-a", false);
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var local = FakeData.LocalTracks()[0];
        var key = new ResourceKey(fixture.Scope with { Provider = source.Id }, local.Uri, FacetKind.TrackIdentity);
        var result = Assert.Single(await coordinator.EnsureAsync([key], ct: TestContext.Current.CancellationToken));
        Assert.Equal(ResourceEnsureStatus.Ready, result.Status);
        var value = Assert.IsType<TrackIdentityValue>(fixture.Repository.Peek(key).Value);
        Assert.Equal(TrackOrigin.Local, value.Origin); Assert.Equal(local.Title, value.Title);
    }

    [Fact]
    public async Task RegistryRoutesUserAndPodcastNamespacesToTheirActualSourceIds()
    {
        var playlists = new UserPlaylistSource();
        var podcasts = new FakePodcastSource();
        var registry = new SourceRegistry([new FakeSource(), playlists, podcasts]);
        var uri = playlists.CreatePlaylist("Empty local playlist");
        Assert.Equal("user-playlists", registry.OwnerOf(uri)!.Id);
        Assert.Equal("podcasts", registry.OwnerOf("wavee:show:0")!.Id);
        var scope = new CatalogScope(playlists.Id, "account-a", "en", "NL", "premium", 1, false);
        var key = new ResourceKey(scope, uri, FacetKind.PlaylistHeader);
        var response = Assert.Single(await new NativeCatalogResourceProvider(registry, playlists).FetchAsync(
            [new(key, new(scope, "account-a", 1, 0, 1), ResourcePriority.Visible, RequiresNetwork: false)], TestContext.Current.CancellationToken));
        var header = Assert.IsType<PlaylistHeaderValue>(response.Result.Patch!.Apply(null));
        Assert.Equal("Empty local playlist", header.Name); Assert.Equal(0, header.TrackCount);
        Assert.Equal(ResourceFetchStatus.Present, response.Result.Status);
    }

    [Fact]
    public async Task UnknownLocalEntity_IsAbsentAndNeverGetsASyntheticTitle()
    {
        var source = new LocalSource();
        var registry = new SourceRegistry([source]);
        var scope = new CatalogScope(source.Id, "account-a", "en", "NL", "premium", 1, false);
        var key = new ResourceKey(scope, "local:album:missing", FacetKind.AlbumIdentity);
        var response = Assert.Single(await new NativeCatalogResourceProvider(registry, source).FetchAsync(
            [new(key, new(scope, "account-a", 1, 0, 1), ResourcePriority.Visible, RequiresNetwork: false)], TestContext.Current.CancellationToken));
        Assert.Equal(ResourceFetchStatus.Absent, response.Result.Status); Assert.Null(response.Result.Patch);
    }
}
