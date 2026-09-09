using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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

public sealed class OnlineCatalogTests
{
    const string TracksResponse = """
    { "data": { "searchV2": { "tracksV2": { "totalCount": 42, "items": [
        { "item": { "data": {
            "uri": "spotify:track:t1", "name": "Blue Monday",
            "duration": { "totalMilliseconds": 450000 },
            "artists": { "items": [ { "uri": "spotify:artist:ar1", "profile": { "name": "New Order" } } ] },
            "albumOfTrack": { "uri": "spotify:album:al1", "name": "Substance", "coverArt": { "sources": [] } }
        } } }
    ] } } } }
    """;

    sealed class Wire
    {
        public List<JsonDocument> Bodies { get; } = new();
        public FakeExchange Exchange { get; }
        public int Calls => Exchange.Calls;

        public Wire(string response)
            => Exchange = new FakeExchange((req, _) =>
            {
                if (req.Body is { } body) Bodies.Add(JsonDocument.Parse(body));
                return new HttpResp(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Encoding.UTF8.GetBytes(response));
            });

        public JsonElement Body(int i) => Bodies[i].RootElement;
        public string Op(int i) => Body(i).GetProperty("operationName").GetString()!;
        public JsonElement Vars(int i) => Body(i).GetProperty("variables");
    }


    static readonly CatalogScope Scope = new("spotify", "test", "en", "NL", "premium", 1, false);
    static SpotifyCatalogResourceProvider Provider(IHttpExchange http) => new(
        new ExtendedMetadataSource(http, () => "https://spclient.test", () => new("test", "NL", "premium", "en", Tier.Premium, false)),
        new MemoryDataPersistence(), new PathfinderClient(http), http, () => "https://spclient.test",
        () => new("Jump back in", "Recents", "Made for you", "Top mixes", "Radio", "Up next", "Audiobooks", "Editors", "Because", "Podcasts"),
        TimeProvider.System);
    static async Task<ResourceResponse> Fetch(SpotifyCatalogResourceProvider provider, ResourceKey key)
        => Assert.Single(await provider.FetchAsync([new(key, new(key.Scope, "test", 1, 0, 1), ResourcePriority.Visible)], TestContext.Current.CancellationToken));
    static ResourceKey Search(string text, SearchFacet facet, int offset = 0, int limit = 30)
        => new(Scope, CatalogSubjects.Search(text), FacetKind.Search, new(offset, limit, Filter: facet.ToString()));

    [Fact]
    public async Task TrackSearchPreservesWireVariablesTotalsAndIdentitySeeds()
    {
        var wire = new Wire(TracksResponse);
        var response = await Fetch(Provider(wire.Exchange), Search("blue monday", SearchFacet.Tracks, 20, 10));
        Assert.Equal(ResourceFetchStatus.Present, response.Result.Status);
        Assert.Equal(1, wire.Calls); Assert.Equal(PathfinderOps.SearchTracks, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("blue monday", vars.GetProperty("searchTerm").GetString());
        Assert.Equal(20, vars.GetProperty("offset").GetInt32()); Assert.Equal(10, vars.GetProperty("limit").GetInt32());
        Assert.False(vars.GetProperty("includePreReleases").GetBoolean());
        var document = Assert.IsType<CatalogDocumentValue>(response.Result.Patch!.Apply(null));
        Assert.Equal(42, document.SearchTotals![SearchFacet.Tracks]);
        Assert.Contains(response.Seeds!, seed => seed.Key.Subject == "spotify:track:t1" && seed.Key.Facet == FacetKind.TrackIdentity);
        Assert.Equal("spotify:track:t1", Assert.Single(document.Sections.Single(section => section.SearchFacet == SearchFacet.Tracks).Items).EntityUri);
    }

    [Fact]
    public async Task AllSearchUsesTopResultsOperationAndDesktopVariables()
    {
        var wire = new Wire(TracksResponse);
        await Fetch(Provider(wire.Exchange), Search("blue", SearchFacet.All));
        Assert.Equal(PathfinderOps.SearchTopResults, wire.Op(0));
        var vars = wire.Vars(0);
        Assert.Equal("blue", vars.GetProperty("query").GetString()); Assert.False(vars.TryGetProperty("searchTerm", out _));
        Assert.Equal(2, vars.GetProperty("sectionFilters").GetArrayLength());
        Assert.Equal(50, vars.GetProperty("numberOfTopResults").GetInt32());
        Assert.False(vars.GetProperty("includeAlbumPreReleases").GetBoolean());
    }

    [Fact]
    public async Task AudiobooksAndGenresRetainTheirDistinctWireRecipes()
    {
        var wire = new Wire(TracksResponse);
        await Fetch(Provider(wire.Exchange), Search("dune", SearchFacet.Audiobooks));
        Assert.Equal(PathfinderOps.SearchAudiobooks, wire.Op(0));
        Assert.True(wire.Vars(0).GetProperty("includePreReleases").GetBoolean());
        wire = new Wire("""{"data":{"searchV2":{"genres":{"totalCount":2,"items":[]}}}}""");
        await Fetch(Provider(wire.Exchange), Search("sleep", SearchFacet.Genres));
        Assert.Equal(PathfinderOps.SearchGenres, wire.Op(0));
        Assert.Equal("sleep", wire.Vars(0).GetProperty("searchTerm").GetString());
        Assert.False(wire.Vars(0).GetProperty("includeAlbumPreReleases").GetBoolean());
        Assert.Equal(20, wire.Vars(0).GetProperty("numberOfTopResults").GetInt32());
    }

    [Theory]
    [InlineData(403, ResourceErrorKind.Forbidden)]
    [InlineData(429, ResourceErrorKind.RateLimited)]
    [InlineData(503, ResourceErrorKind.Transport)]
    public async Task FailedSearchIsAnErrorRatherThanAnEmptyDocument(int status, ResourceErrorKind kind)
    {
        var http = new FakeExchange((_, _) => new HttpResp(status, new Dictionary<string, string>(), []));
        var response = await Fetch(Provider(http), Search("blue", SearchFacet.Tracks));
        Assert.Equal(ResourceFetchStatus.Failed, response.Result.Status);
        Assert.Equal(kind, response.Result.Error!.Kind); Assert.Null(response.Result.Patch);
    }

    [Fact]
    public async Task SuggestionsShareOneResourceAcrossPlainAndRichReads()
    {
        var wire = new Wire("""{"data":{"searchV2":{"topResultsV2":{"itemsV2":[{"item":{"data":{"text":"blue monday"}}}]}}}}""");
        await using var host = new CatalogQueryTestHost(Provider(wire.Exchange));
        var plain = await host.Library.SuggestAsync("blue");
        var rich = await host.Library.SuggestRichAsync("blue");
        Assert.Equal(new[] { "blue monday" }, plain); Assert.Equal(plain, rich.Queries);
        Assert.Equal(1, wire.Calls); Assert.Equal(PathfinderOps.SearchSuggestions, wire.Op(0));
        Assert.Equal("blue", wire.Vars(0).GetProperty("query").GetString());
    }

    [Fact]
    public async Task HomeFacetIsPartOfTheResourceKeyAndTheDesktopRequest()
    {
        var wire = new Wire("""{"data":{"home":{"greeting":{"transformedLabel":"Good evening"}}}}""");
        await using var host = new CatalogQueryTestHost(Provider(wire.Exchange));
        var first = await host.Library.GetHomeAsync(null);
        Assert.Equal("Good evening", first.Greeting); Assert.Equal("", first.Facet);
        Assert.Equal(PathfinderOps.Home, wire.Op(0));
        Assert.Equal("INTEGRATION_DESKTOP", wire.Vars(0).GetProperty("homeEndUserIntegration").GetString());
        Assert.Equal(SpotifyTimeZone.LocalIana, wire.Vars(0).GetProperty("timeZone").GetString());
        await host.Library.GetHomeAsync(null); Assert.Equal(1, wire.Calls);
        var filtered = await host.Library.GetHomeAsync("podcasts-following-chip");
        Assert.Equal(2, wire.Calls); Assert.Equal("podcasts-following-chip", filtered.Facet);
        Assert.Equal(filtered.Facet, wire.Vars(1).GetProperty("facet").GetString());
    }
}
