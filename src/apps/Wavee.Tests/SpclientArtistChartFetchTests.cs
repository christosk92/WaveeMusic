using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ArtistChartResourceTests
{
    static readonly CatalogScope Scope = new("spotify", "me", "en", "US", "premium", 1, false);
    static async Task<(ResourceResponse Response, string? Url)> Fetch(int status, string body)
    {
        string? url = null;
        var http = new FakeExchange((request, _) =>
        {
            url = request.Url;
            return new HttpResp(status, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body));
        });
        var metadata = new ExtendedMetadataSource(http, () => "https://spclient.test",
            () => new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        var provider = new SpotifyCatalogResourceProvider(metadata, new MemoryDataPersistence(), new PathfinderClient(http),
            http, () => "https://spclient.test", () => HomeModuleTitles.Default, TimeProvider.System);
        var request = new ResourceRequest(new(Scope, "spotify:artist:ar1", FacetKind.ArtistPopular),
            new(Scope, "me", 1, 0, 1), ResourcePriority.Visible);
        return (Assert.Single(await provider.FetchAsync([request], TestContext.Current.CancellationToken)), url);
    }

    [Theory]
    [InlineData(200, "<html>bad gateway</html>")]
    [InlineData(200, "{invalid")]
    [InlineData(200, "")]
    [InlineData(200, "{}")]
    [InlineData(503, "unavailable")]
    public async Task Failure_RemainsAnErrorInsteadOfAuthoritativeEmptyMembership(int status, string body)
    {
        var (response, _) = await Fetch(status, body);
        Assert.Equal(ResourceFetchStatus.Failed, response.Result.Status);
        Assert.Null(response.Result.Patch);
        Assert.NotNull(response.Result.Error);
    }

    [Fact]
    public async Task CompleteOrderedChart_PreservesDuplicatesAndEndCoverage()
    {
        var (response, url) = await Fetch(200, """{"tracks":[{"uri":"spotify:track:a"},{"uri":"spotify:track:b"},{"uri":"spotify:track:a"}]}""");
        var page = Assert.IsType<RelationPageValue>(response.Result.Patch!.Apply(null));
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:a" }, page.Items.Select(row => row.EntityUri));
        Assert.Equal(3, page.Total);
        Assert.Null(page.NextCursor);
        Assert.Equal(3, page.Items.Select(row => row.OccurrenceKey).Distinct().Count());
        Assert.Contains("artist-top-tracks-extensions", url);
    }

    [Fact]
    public async Task EmptyOrderedChart_IsPresentAndComplete()
    {
        var (response, _) = await Fetch(200, """{"tracks":[]}""");
        Assert.Equal(ResourceFetchStatus.Present, response.Result.Status);
        var page = Assert.IsType<RelationPageValue>(response.Result.Patch!.Apply(null));
        Assert.Empty(page.Items); Assert.Equal(0, page.Total); Assert.Null(page.NextCursor);
    }
}
