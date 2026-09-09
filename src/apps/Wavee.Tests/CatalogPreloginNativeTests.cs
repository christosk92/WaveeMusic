using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogPreloginNativeTests
{
    [Fact]
    public async Task UnknownAccountBootstrapsLocalPlaylistsAndCanReadLocalMetadataWithoutSpotifyAccess()
    {
        var playlists = new UserPlaylistSource();
        var local = new LocalSource();
        var registry = new SourceRegistry([playlists, local]);
        var playlistUri = playlists.CreatePlaylist("Before login");
        var localTrack = FakeData.LocalTracks()[0];
        var saved = new LocalMutationSource([playlistUri, localTrack.Uri]);
        var spotifyChild = new Track("child", "spotify:track:child", "Saved inline title", [], new("", "", ""), 1000, false, null);
        playlists.InsertTracks(playlistUri, [spotifyChild, spotifyChild], 0);
        var persistence = new MemoryDataPersistence();
        var network = new NetworkMustStayIdle();
        var scope = new CatalogScope("spotify", "", "en", "", "", 0, false, ContextKnown: false);
        string Owner(string uri) => registry.OwnerOf(uri)?.Id ?? EntityUri.Parse(uri).Provider;
        await using var runtime = new CatalogRuntime(scope, "", persistence, persistence,
            new MemoryReplicaProjection(new InMemoryStore()),
            [new NativeCatalogResourceProvider(registry, playlists), new NativeCatalogResourceProvider(registry, local), network], Owner);
        await using var bootstrap = new NativeCatalogBootstrap(registry, runtime.Replicas,
            uri => runtime.Catalog.Scope with { Provider = Owner(uri) }, saved);

        await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        await bootstrap.RefreshAsync(TestContext.Current.CancellationToken);

        var headerKey = new ResourceKey(scope with { Provider = playlists.Id }, playlistUri, FacetKind.PlaylistHeader);
        Assert.Equal("Before login", Assert.IsType<PlaylistHeaderValue>(runtime.Catalog.Peek(headerKey).Value).Name);
        var members = runtime.Replicas.ReadPlaylist(playlistUri).Members;
        Assert.Equal([spotifyChild.Uri, spotifyChild.Uri], members.Select(row => row.ItemUri));
        Assert.Equal(2, members.Select(row => row.ItemId).Distinct().Count());
        Assert.Contains(runtime.Replicas.ReadRootlist().Entries, row => row.Uri == playlistUri);
        Assert.True(runtime.Replicas.IsCollectionKnown("liked"));
        Assert.Contains(runtime.Replicas.ReadCollection("liked").Items, row => row.Uri == localTrack.Uri);

        var childKey = new ResourceKey(scope, spotifyChild.Uri, FacetKind.TrackIdentity);
        var child = runtime.Catalog.Peek(childKey);
        Assert.Equal("Saved inline title", Assert.IsType<TrackIdentityValue>(child.Value).Title);
        Assert.False(child.IsFresh(DateTimeOffset.UtcNow));
        Assert.False(runtime.Catalog.IsOnline);
        Assert.False(runtime.Catalog.CanRequest(childKey, requiresNetwork: true));
        Assert.False(runtime.Catalog.CanRequest(childKey, requiresNetwork: false));
        Assert.Equal(ResourceEnsureStatus.Deferred,
            Assert.Single(await runtime.Resources.EnsureAsync([childKey], force: true, ct: TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, network.Calls);

        var localKey = new ResourceKey(scope with { Provider = local.Id }, localTrack.Uri, FacetKind.TrackIdentity);
        var result = Assert.Single(await runtime.Resources.EnsureAsync([localKey], force: true, ct: TestContext.Current.CancellationToken));
        Assert.Equal(ResourceEnsureStatus.Ready, result.Status);
        Assert.Equal(localTrack.Title, Assert.IsType<TrackIdentityValue>(runtime.Catalog.Peek(localKey).Value).Title);
        Assert.Empty(runtime.Replicas.Intents);
    }

    [Fact]
    public async Task ModuleOwnerFactsAndQueueReadsUseTheSameNamespaceBeforeLogin()
    {
        var persistence = new MemoryDataPersistence();
        var scope = new CatalogScope("spotify", "", "en", "", "", 0, false, ContextKnown: false);
        await using var runtime = new CatalogRuntime(scope, "", persistence, persistence,
            new MemoryReplicaProjection(new InMemoryStore()), [], _ => "spotify");
        const string uri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";
        var track = new Track("module-row", uri, "Current owner title", [new("", "", "Broadcaster")],
            new("", "", ""), 0, false, null, Source: "module:wavee.youtube");
        var epoch = runtime.Catalog.Epoch;
        await runtime.PlaybackQueue.ObserveOwnerTrackAsync(track, epoch, TestContext.Current.CancellationToken);
        await runtime.PlaybackQueue.SeedTracksAsync([track with { Title = "Stale Connect echo", DurationMs = 9000 }], epoch);

        var current = runtime.PlaybackQueue.ReadTrack(uri);
        Assert.Equal("Current owner title", current.Title);
        Assert.Equal("Broadcaster", Assert.Single(current.Artists).Name);
        Assert.Equal(0, current.DurationMs);
        Assert.Null(runtime.Catalog.Peek(new(scope, uri, FacetKind.TrackIdentity)).Value);
        Assert.Equal("Current owner title", Assert.IsType<TrackIdentityValue>(runtime.Catalog.Peek(
            new(scope with { Provider = "module:wavee.youtube" }, uri, FacetKind.TrackIdentity)).Value).Title);
    }

    [Fact]
    public async Task PreloginNativeRequestsStillRejectLateEpochsAndUnattributedSpotifyAuthority()
    {
        await using var fixture = new CatalogFixture();
        var scope = fixture.Scope with { ProviderAccount = "", ContextKnown = false };
        await fixture.Repository.SetSessionAsync(scope, "", false);
        var nativeKey = new ResourceKey(scope with { Provider = "local" }, "local:track:one", FacetKind.TrackIdentity);
        var request = await fixture.Repository.CaptureRequestAsync(nativeKey, ResourcePriority.Visible, requiresNetwork: false);
        await fixture.Repository.SetSessionAsync(scope, "", false);
        var result = Assert.Single(await fixture.Repository.AcceptAsync([new(request,
            ResourceFetchResult.Present(new TrackIdentityPatch(Title: FieldChange<string?>.Set("Late"))))]));
        Assert.Equal(ResourceEnsureStatus.Superseded, result.Status);
        Assert.Null(fixture.Repository.Peek(nativeKey).Value);

        var spotifyKey = new ResourceKey(scope, "spotify:playlist:one", FacetKind.PlaylistHeader);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.ObserveAsync(
            [new(spotifyKey, new PlaylistHeaderPatch(Name: FieldChange<string?>.Set("Unattributed")))], fixture.Repository.Epoch));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.SetSessionAsync(scope with { ContextKnown = true }, "", true));
        Assert.Null(fixture.Repository.Peek(spotifyKey).Value);
    }

    sealed class NetworkMustStayIdle : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public int Calls;
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Spotify must not be requested before login."); }
    }
}
