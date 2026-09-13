using System.Text.Json;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public class ConcertCaptureContractTests
{
    static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Concerts", name);

    static JsonDocument Open(string name) => JsonDocument.Parse(File.ReadAllText(FixturePath(name)));

    [Fact]
    public void PersistedQueryHashes_MatchCapturedContracts()
    {
        Assert.Equal("ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b", PathfinderOps.ArtistConcertsHash);
        Assert.Equal("320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03", PathfinderOps.ArtistConcertsPageLocationHash);
        Assert.Equal("079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2", PathfinderOps.UserLocationHash);
        Assert.Equal("5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba", PathfinderOps.InferredUserLocationHash);
        Assert.Equal("a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72", PathfinderOps.ConcertConceptsHash);
        Assert.Equal("9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a", PathfinderOps.ConcertFeedHash);
        Assert.Equal("b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681", PathfinderOps.ConcertLocationDetailsHash);
        Assert.Equal("43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3", PathfinderOps.SearchConcertLocationsHash);
        Assert.Equal("8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96", PathfinderOps.ConcertLocationsByLatLonHash);
        Assert.Equal("5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353", PathfinderOps.SaveLocationHash);
        Assert.Equal("21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e", PathfinderOps.ConcertHash);
    }

    [Fact]
    public void ArtistConcerts_RichResponse_UsesSiblingNearbyAndConcertBranches()
    {
        using var doc = Open("artist-concerts-rich.json");
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal("spotify:artist:example-artist", data.GetProperty("artistUnion").GetProperty("uri").GetString());
        Assert.False(data.GetProperty("artistUnion").TryGetProperty("goods", out _));

        var nearby = data.GetProperty("nearby");
        Assert.Equal("Example City", nearby.GetProperty("locationName").GetString());
        Assert.Equal("spotify:concert:nearby-example",
            nearby.GetProperty("concerts").GetProperty("items")[0].GetProperty("data").GetProperty("uri").GetString());

        var all = data.GetProperty("concerts").GetProperty("concerts").GetProperty("items");
        Assert.Equal("spotify:concert:tour-example", all[0].GetProperty("data").GetProperty("uri").GetString());
        Assert.Equal(2, all[0].GetProperty("data").GetProperty("artists").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void ArtistConcerts_EmptyResponse_KeepsBranchesAsObjects()
    {
        using var doc = Open("artist-concerts-empty.json");
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Object, data.GetProperty("nearby").ValueKind);
        Assert.Empty(data.GetProperty("nearby").EnumerateObject());
        Assert.Equal(JsonValueKind.Object, data.GetProperty("concerts").ValueKind);
        Assert.Empty(data.GetProperty("concerts").EnumerateObject());
    }

    [Fact]
    public void ConcertFeed_PreservesMixedWrappersAndOpaquePaginationKey()
    {
        using var doc = Open("concert-feed.json");
        var sections = doc.RootElement.GetProperty("data").GetProperty("liveEventsFeed").GetProperty("sections");

        var carousel = sections[0];
        Assert.Equal("ConcertCarousel", carousel.GetProperty("__typename").GetString());
        Assert.Contains(carousel.GetProperty("concerts").EnumerateArray(),
            x => x.GetProperty("__typename").GetString() == "PlaylistResponseWrapper");
        Assert.Contains(carousel.GetProperty("concerts").EnumerateArray(),
            x => x.GetProperty("__typename").GetString() == "ConcertV2ResponseWrapper");

        var all = sections[2];
        Assert.Equal("AllEvents", all.GetProperty("__typename").GetString());
        Assert.Equal("NEXT_PAGE_TOKEN", all.GetProperty("paginationKey").GetString());
        Assert.Equal("all-events", all.GetProperty("sections")[0].GetProperty("key").GetString());
    }

    [Fact]
    public void ConcertDetail_CarriesOffersLocationLineupAndRelatedEvents()
    {
        using var doc = Open("concert-detail.json");
        var concert = doc.RootElement.GetProperty("data").GetProperty("concert");

        Assert.Equal("spotify:concert:detail-example", concert.GetProperty("uri").GetString());
        Assert.Equal(2, concert.GetProperty("artists").GetProperty("totalCount").GetInt32());
        Assert.Equal("Example Region", concert.GetProperty("location").GetProperty("region").GetString());
        Assert.Equal("spotify:venue:example-venue", concert.GetProperty("venue").GetProperty("data").GetProperty("uri").GetString());

        var offers = concert.GetProperty("offers").GetProperty("items");
        Assert.Equal(2, offers.GetArrayLength());
        Assert.Equal(45m, offers[0].GetProperty("minPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, offers[1].GetProperty("minPrice").ValueKind);
        Assert.Equal("", offers[1].GetProperty("url").GetString());

        Assert.Equal("spotify:concert:related-example",
            concert.GetProperty("relatedConcerts").GetProperty("items")[0].GetProperty("data").GetProperty("uri").GetString());
    }

    [Fact]
    public void LocationFixture_CoversSavedInferredSearchReverseAndDetailsShapes()
    {
        using var doc = Open("concert-locations.json");
        var root = doc.RootElement;

        Assert.Equal("Example City", root.GetProperty("userLocation").GetProperty("data").GetProperty("me")
            .GetProperty("profile").GetProperty("location").GetProperty("name").GetString());
        Assert.Equal("Example City", root.GetProperty("artistConcertsPageLocation").GetProperty("data")
            .GetProperty("me").GetProperty("profile").GetProperty("location").GetProperty("name").GetString());
        Assert.True(root.GetProperty("inferredUserLocation").GetProperty("data").GetProperty("me")
            .GetProperty("profile").GetProperty("location").GetProperty("isInferred").GetBoolean());
        Assert.Equal(2, root.GetProperty("searchConcertLocations").GetProperty("data")
            .GetProperty("concertLocations").GetProperty("items").GetArrayLength());
        Assert.Single(root.GetProperty("concertLocationsByLatLon").GetProperty("data")
            .GetProperty("concertLocations").GetProperty("items").EnumerateArray());
        Assert.True(root.GetProperty("saveLocation").GetProperty("data").GetProperty("storeUserLocation")
            .GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Fixtures_AreSanitizedAndContainNoCapturedHeaders()
    {
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(FixturePath("concert-feed.json"))!, "*.json"))
        {
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client-token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("spotifycdn.com", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FeedPage_TreatsPaginationKeyAsOpaque()
    {
        var page = new ConcertFeedPage(Array.Empty<ConcertFeedSection>(), "opaque+/=token");
        Assert.Equal("opaque+/=token", page.PaginationKey);
    }
}
