using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class PlaylistTargetsQueryTests
{
    [Fact]
    public async Task ColdPickerDiscoversEveryEditableRootlistTarget()
    {
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Present(
            new ReplaceFacetPatch(new PlaylistHeaderValue(Name: "Playlist " + EntityUri.IdOf(request.Key.Subject))
            { Capabilities = new(true, true, true, false, true, Known: true) }))));
        await using var host = new CatalogQueryTestHost(provider);
        var uris = Enumerable.Range(0, 61).Select(i => "spotify:playlist:" + i).ToArray();
        await Rootlist(host, uris);
        using var passive = host.Data.Queries.Acquire(new PlaylistTargetsQuery(host.Scope));
        await QueryPublication.Initial(passive);
        var pending = PlaylistPickerState.Read(passive.Current, null, null, "60");
        Assert.True(pending.HasUnresolved);
        Assert.Empty(pending.Items);
        Assert.Empty(provider.Requests);

        var loaded = await host.Data.Queries.ReadOnceAsync(new PlaylistTargetsQuery(host.Scope),
            QueryDemand.Initial, TestContext.Current.CancellationToken);
        var targets = PlaylistPickerState.Read(loaded, null, null, "60");
        Assert.False(targets.HasUnresolved);
        Assert.Equal("Playlist 60", Assert.Single(targets.Items).Name);
        Assert.Equal(61, provider.Requests.Count);
        Assert.All(provider.Requests, key => Assert.Equal(FacetKind.PlaylistHeader, key.Facet));
        Assert.All(loaded.Value.Playlists, playlist => Assert.Null(playlist.Tracks));
    }

    [Fact]
    public async Task NameOnlySeedIsUnresolvedUntilPermissionEvidenceArrives()
    {
        const string uri = "spotify:playlist:editorial";
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Present(
            new ReplaceFacetPatch(new PlaylistHeaderValue(Name: "Editorial")
            { Capabilities = new(true, false, false, false, false, Known: true) }))));
        await using var host = new CatalogQueryTestHost(provider);
        await Rootlist(host, uri);
        await host.SeedAsync(new CatalogSeed(new(host.Scope, uri, FacetKind.PlaylistHeader),
            new PlaylistHeaderPatch(Name: FieldChange<string?>.Set("Editorial"))));
        using var passive = host.Data.Queries.Acquire(new PlaylistTargetsQuery(host.Scope));
        await QueryPublication.Initial(passive);
        Assert.True(PlaylistPickerState.Read(passive.Current, null, null, null).HasUnresolved);

        var loaded = await host.Data.Queries.ReadOnceAsync(new PlaylistTargetsQuery(host.Scope),
            cancellationToken: TestContext.Current.CancellationToken);
        var targets = PlaylistPickerState.Read(loaded, null, null, null);
        Assert.Empty(targets.Items);
        Assert.False(targets.HasUnresolved);
        Assert.False(targets.Unavailable);
    }

    [Fact]
    public async Task HeaderFailureDoesNotTurnUnresolvedTargetsIntoAnEmptySuccess()
    {
        var provider = new QueryTestProvider(request => new(request,
            ResourceFetchResult.Failed(new(ResourceErrorKind.Forbidden, "Access denied", 403))));
        await using var host = new CatalogQueryTestHost(provider);
        await Rootlist(host, "spotify:playlist:missing");
        var loaded = await host.Data.Queries.ReadOnceAsync(new PlaylistTargetsQuery(host.Scope),
            cancellationToken: TestContext.Current.CancellationToken);
        var targets = PlaylistPickerState.Read(loaded, null, null, null);
        Assert.True(loaded.Value.MembershipKnown);
        Assert.True(targets.HasUnresolved);
        Assert.True(targets.Unavailable);
        Assert.Empty(targets.Items);
    }

    [Fact]
    public async Task KnownEmptyRootlistAndUnknownRootlistHaveDifferentPickerStates()
    {
        var provider = new QueryTestProvider(request => new(request, new ResourceFetchResult(ResourceFetchStatus.Unsupported)));
        await using var host = new CatalogQueryTestHost(provider);
        using var query = host.Data.Queries.Acquire(new PlaylistTargetsQuery(host.Scope));
        Assert.False(query.Current.Status.HasPrimaryData);
        Assert.True(PlaylistPickerState.Read(query.Current, null, null, null).HasUnresolved);
        await Rootlist(host);
        await QueryPublication.Until(() => query.Current.Status.HasPrimaryData);
        Assert.True(query.Current.Status.HasPrimaryData);
        Assert.False(PlaylistPickerState.Read(query.Current, null, null, null).HasUnresolved);
        Assert.Empty(provider.Requests);
    }

    static Task Rootlist(CatalogQueryTestHost host, params string[] uris)
        => host.Data.Replicas.AdoptRootlistAsync(new([..uris.Select((uri, index) =>
            new RootlistEntry(index, 0, uri, null, 0))], new byte[24]));
}
