using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarDemandScopeTests
{
    [Theory]
    [InlineData("album", FacetKind.AlbumIdentity)]
    [InlineData("artist", FacetKind.ArtistIdentity)]
    [InlineData("show", FacetKind.ShowIdentity)]
    [InlineData("playlist", FacetKind.PlaylistHeader)]
    public async Task UnlistedCardDemandLoadsOnlyIdentityAndPreservesKnownCounts(string kind, FacetKind facet)
    {
        CatalogValue value = facet switch
        {
            FacetKind.AlbumIdentity => new AlbumIdentityValue("Pinned album", TrackCount: 8),
            FacetKind.ArtistIdentity => new ArtistIdentityValue("Pinned artist"),
            FacetKind.ShowIdentity => new ShowIdentityValue("Pinned show", EpisodeCount: 8),
            _ => new PlaylistHeaderValue("Pinned playlist", TrackCount: 8),
        };
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(value))));
        await using var host = new CatalogQueryTestHost(provider);
        var specification = new EntityCardQuery(host.Scope, $"spotify:{kind}:unlisted");
        using var passive = host.Data.Queries.Acquire(specification);
        await QueryPublication.Initial(passive);
        Assert.Empty(provider.Requests);
        var result = await host.Data.Queries.ReadOnceAsync(specification, new(true, QueryPriority.Visible, []));
        Assert.Equal("Pinned " + kind, result.Value.Title);
        Assert.Equal(kind == "artist" ? (int?)null : 8, result.Value.ChildCount);
        Assert.Equal(facet, Assert.Single(provider.Requests).Facet);
    }
}
