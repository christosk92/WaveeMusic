using System.Linq;
using System.Threading.Tasks;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class AlbumCardDemandTests
{
    [Theory]
    [InlineData("more-by")]
    [InlineData("versions")]
    public async Task MembershipOnlyCardsDemandEveryIdentityAndTheirReferencedArtists(string collection)
    {
        const string root = "spotify:album:root", artist = "spotify:artist:creator";
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet switch
        {
            FacetKind.AlbumIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(
                new AlbumIdentityValue("Card " + request.Key.Subject, ArtistUris: [artist]))),
            FacetKind.ArtistIdentity => ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistIdentityValue("Creator"))),
            _ => ResourceFetchResult.Absent(),
        }));
        await using var host = new CatalogQueryTestHost(provider);
        string[] moreBy = ["spotify:album:more0", "spotify:album:more1", "spotify:album:more2"];
        string[] versions = ["spotify:album:version0", "spotify:album:version1", "spotify:album:version2"];
        await host.AcceptAsync(new(host.Scope, root, FacetKind.AlbumIdentity), new AlbumIdentityValue("Root", TrackCount: 3));
        await host.AcceptAsync(new(host.Scope, root, FacetKind.AlbumDetail), new AlbumDetailValue(null, null)
            { MoreByArtistUris = moreBy });
        await host.AcceptAsync(new(host.Scope, root, FacetKind.Publishing), new PublishingValue(null, null));
        await host.AcceptAsync(new(host.Scope, root, FacetKind.AlbumTracks, new(0, 50)),
            new RelationPageValue(FacetKind.AlbumTracks, "tracks", null, 0, 3, null, RelationCoverage.Complete,
                [..Enumerable.Range(0, 3).Select(i => new CatalogRelationItem("track:" + i, "spotify:track:" + i))]));
        await host.AcceptAsync(new(host.Scope, root, FacetKind.AlbumVersions, new(0, 50)),
            new RelationPageValue(FacetKind.AlbumVersions, "versions", null, 0, 3, null, RelationCoverage.Complete,
                [..versions.Select(uri => new CatalogRelationItem(uri, uri))]));
        using var passive = host.Data.Queries.Acquire(new AlbumDetailQuery(host.Scope, root));
        await QueryPublication.Initial(passive);
        long order = passive.Current.OrderRevision;
        var result = await host.Data.Queries.ReadOnceAsync(new AlbumDetailQuery(host.Scope, root),
            QueryDemand.Initial, TestContext.Current.CancellationToken);
        var selected = collection == "more-by" ? result.Value.MoreByArtist! : result.Value.OtherVersions!;
        Assert.All(selected, album => Assert.Equal("Card " + album.Uri, album.Name));
        Assert.All(selected, album => Assert.Equal("Creator", Assert.Single(album.Artists).Name));
        Assert.Equal(order, result.OrderRevision);
        var other = collection == "more-by" ? result.Value.OtherVersions! : result.Value.MoreByArtist!;
        Assert.All(other, album => Assert.Equal("Card " + album.Uri, album.Name));
        Assert.All(selected.Concat(other), album => Assert.Contains(provider.Requests,
            key => key.Subject == album.Uri && key.Facet == FacetKind.AlbumIdentity));
    }
}
