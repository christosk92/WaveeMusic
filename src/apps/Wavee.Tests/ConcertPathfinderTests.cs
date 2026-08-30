using System.Text;
using System.Text.Json;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public sealed class ConcertPathfinderTests
{
    static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Concerts", name);

    static JsonDocument Open(string name) => JsonDocument.Parse(File.ReadAllText(FixturePath(name)));

    static JsonElement Variables(Action<Utf8JsonWriter>? write)
    {
        using var document = JsonDocument.Parse(PathfinderClient.BuildBody("test", "hash", write));
        return document.RootElement.GetProperty("variables").Clone();
    }

    // Raw JSON of a single writer's output wrapped in an object — for golden-string assertions where field order matters.
    static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void RequestWriters_UseCapturedPropertyNamesAndExplicitNulls()
    {
        var artist = Variables(w => ConcertPathfinderRequests.WriteArtistConcerts(
            w, "spotify:artist:artist-one", null, true));
        Assert.Equal("spotify:artist:artist-one", artist.GetProperty("artistUri").GetString());
        Assert.Equal(JsonValueKind.Null, artist.GetProperty("geoHash").ValueKind);
        Assert.True(artist.GetProperty("includeNearby").GetBoolean());

        var concepts = Variables(w => ConcertPathfinderRequests.WriteConcepts(w, "u173", null));
        Assert.Equal("u173", concepts.GetProperty("geohash").GetString());
        Assert.False(concepts.TryGetProperty("geoHash", out _));
        Assert.Equal(JsonValueKind.Null, concepts.GetProperty("conceptUri").ValueKind);

        var feed = Variables(w => ConcertPathfinderRequests.WriteFeed(w, new ConcertFeedQuery()));
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("geoHash").ValueKind);
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("geonameId").ValueKind);
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("dateRange").ValueKind);
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("conceptUris").ValueKind);
        Assert.Equal(100, feed.GetProperty("radiusInKm").GetInt32());
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("paginationKey").ValueKind);
    }

    [Fact]
    public void FeedRequest_WritesOneConceptAndReplaysOpaquePaginationUnchanged()
    {
        var place = new ConcertPlace("2759794", "Amsterdam", GeoHash: "u173zq");
        var query = new ConcertFeedQuery(place, new[] { "spotify:concept:jazz" }, 42, PaginationKey: "opaque+/= token");
        var variables = Variables(w => ConcertPathfinderRequests.WriteFeed(w, query));

        Assert.Equal(JsonValueKind.Null, variables.GetProperty("geoHash").ValueKind);
        Assert.Equal("2759794", variables.GetProperty("geonameId").GetString());
        Assert.Equal("spotify:concept:jazz", variables.GetProperty("conceptUris")[0].GetString());
        Assert.Single(variables.GetProperty("conceptUris").EnumerateArray());
        Assert.Equal(42, variables.GetProperty("radiusInKm").GetInt32());
        Assert.Equal("opaque+/= token", variables.GetProperty("paginationKey").GetString());

        var geoOnly = Variables(w => ConcertPathfinderRequests.WriteFeed(w,
            new ConcertFeedQuery(new ConcertPlace(string.Empty, "Approximate", GeoHash: "u173zq"))));
        Assert.Equal("u173zq", geoOnly.GetProperty("geoHash").GetString());
        Assert.Equal(JsonValueKind.Null, geoOnly.GetProperty("geonameId").ValueKind);
    }

    [Fact]
    public void FeedRequest_WritesDateRangeObjectAndEveryConceptInOrder()
    {
        var query = new ConcertFeedQuery(
            new ConcertPlace("5128581", "New York City"),
            new[] { "spotify:concept:edm", "spotify:concept:christian", "spotify:concept:latin" },
            25,
            new ConcertDateRange(new DateOnly(2026, 7, 17), new DateOnly(2026, 7, 19)));

        var json = WriteJson(w => ConcertPathfinderRequests.WriteFeed(w, query));

        Assert.Equal(
            "{\"geoHash\":null,\"geonameId\":\"5128581\"," +
            "\"dateRange\":{\"from\":\"2026-07-17\",\"to\":\"2026-07-19\"}," +
            "\"conceptUris\":[\"spotify:concept:edm\",\"spotify:concept:christian\",\"spotify:concept:latin\"]," +
            "\"radiusInKm\":25,\"paginationKey\":null}",
            json);
    }

    [Fact]
    public void FeedCountRequest_SendsCountVariablesWithoutPaginationOrGeoHash()
    {
        var query = new ConcertFeedQuery(new ConcertPlace("5128581", "New York City"), RadiusKm: 100);
        var variables = Variables(w => ConcertPathfinderRequests.WriteFeedCount(w, query));

        Assert.Equal("5128581", variables.GetProperty("geonameId").GetString());
        Assert.Equal(100, variables.GetProperty("radiusInKm").GetInt32());
        Assert.Equal(JsonValueKind.Null, variables.GetProperty("dateRange").ValueKind);
        Assert.Equal(JsonValueKind.Null, variables.GetProperty("conceptUris").ValueKind);
        Assert.False(variables.TryGetProperty("paginationKey", out _));
        Assert.False(variables.TryGetProperty("geoHash", out _));
    }

    [Fact]
    public void FeedCountMapper_ReadsTotalCountAndNullsMalformedBranches()
    {
        using var document = Open("concert-count.json");
        Assert.Equal(9191, ConcertPathfinderMapper.MapFeedCount(document.RootElement));

        using var malformed = JsonDocument.Parse("""{ "data": { "concerts": {} } }""");
        Assert.Null(ConcertPathfinderMapper.MapFeedCount(malformed.RootElement));
    }

    [Fact]
    public void LocationAndDetailRequests_MatchCapturedVariables()
    {
        var details = Variables(w => ConcertPathfinderRequests.WriteLocationDetails(w, null, false));
        Assert.Equal(JsonValueKind.Null, details.GetProperty("geonameId").ValueKind);
        Assert.False(details.GetProperty("isAnonymous").GetBoolean());

        var search = Variables(w => ConcertPathfinderRequests.WriteSearchLocations(w, string.Empty));
        Assert.Equal(string.Empty, search.GetProperty("query").GetString());

        var reverse = Variables(w => ConcertPathfinderRequests.WriteReverseLocation(w,
            new GeoCoordinates(52.3676, 4.9041)));
        Assert.Equal(52.3676, reverse.GetProperty("lat").GetDouble(), 4);
        Assert.Equal(4.9041, reverse.GetProperty("lon").GetDouble(), 4);

        var save = Variables(w => ConcertPathfinderRequests.WriteSaveLocation(w, "2759794"));
        Assert.Equal("2759794", save.GetProperty("geonameId").GetString());

        var concert = Variables(w => ConcertPathfinderRequests.WriteConcert(
            w, "spotify:concert:event-one", true));
        Assert.Equal("spotify:concert:event-one", concert.GetProperty("uri").GetString());
        Assert.True(concert.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public void ArtistMapper_UsesSiblingBranchesAndPreservesLocalOffsets()
    {
        using var document = Open("artist-concerts-rich.json");
        var result = Assert.IsType<ArtistConcertSchedule>(
            ConcertPathfinderMapper.MapArtistSchedule(document.RootElement));

        Assert.Equal("example-artist", result.Artist.Id);
        Assert.Equal("Example Artist", result.Artist.Name);
        Assert.Equal("https://example.invalid/images/artist-header.jpg", result.HeaderImage?.Url);
        var all = Assert.Single(result.Concerts);
        Assert.Equal("spotify:concert:tour-example", all.Uri);
        Assert.Equal(TimeSpan.FromHours(-4), all.Date.Offset);
        Assert.Equal(2, all.Artists?.Count);
        var nearby = Assert.Single(result.Nearby!);
        Assert.True(nearby.IsNearUser);
        Assert.Equal(TimeSpan.FromHours(2), nearby.Date.Offset);
        Assert.Equal("Example City", result.NearbyLocationName);
    }

    [Fact]
    public void ArtistMapper_MapsEmptyObjectsToAnEmptySchedule()
    {
        using var document = Open("artist-concerts-empty.json");
        var result = Assert.IsType<ArtistConcertSchedule>(
            ConcertPathfinderMapper.MapArtistSchedule(document.RootElement));

        Assert.Empty(result.Concerts);
        Assert.Empty(result.Nearby!);
        Assert.Null(result.NearbyLocationName);
        Assert.Null(result.HeaderImage);
    }

    [Fact]
    public void ArtistMapper_PrefersTheLargestUnmeasuredHeaderRendition()
    {
        using var document = JsonDocument.Parse("""
            { "data": { "artistUnion": {
              "uri": "spotify:artist:quality", "profile": { "name": "Quality" },
              "headerImage": { "data": { "sources": [
                { "url": "https://example.invalid/16.jpg" },
                { "url": "https://example.invalid/166.jpg" },
                { "url": "https://example.invalid/full.jpg" }
              ] } }
            } } }
            """);

        var result = Assert.IsType<ArtistConcertSchedule>(
            ConcertPathfinderMapper.MapArtistSchedule(document.RootElement));

        Assert.Equal("https://example.invalid/full.jpg", result.HeaderImage?.Url);
    }

    [Fact]
    public void DetailMapper_KeepsTheLargestFirstUnmeasuredAvatarRendition()
    {
        using var document = JsonDocument.Parse("""
            { "data": { "concert": {
              "uri": "spotify:concert:quality", "title": "Quality",
              "startDateIsoString": "2030-01-01T20:00:00+01:00",
              "artists": { "items": [ { "data": {
                "uri": "spotify:artist:quality", "profile": { "name": "Quality" },
                "visuals": { "avatarImage": { "sources": [
                  { "url": "https://example.invalid/640.jpg" },
                  { "url": "https://example.invalid/320.jpg" },
                  { "url": "https://example.invalid/160.jpg" }
                ] } }
              } } ] }
            } } }
            """);

        var result = Assert.IsType<ConcertDetails>(ConcertPathfinderMapper.MapDetails(document.RootElement));

        Assert.Equal("https://example.invalid/640.jpg", Assert.Single(result.Artists!).Image?.Url);
        Assert.Equal("https://example.invalid/640.jpg", result.Summary.Image?.Url);
    }

    [Fact]
    public void FeedMapper_SeparatesPromotionsAndKeepsOpaquePagination()
    {
        using var document = Open("concert-feed.json");
        var page = Assert.IsType<ConcertFeedPage>(ConcertPathfinderMapper.MapFeed(document.RootElement));

        Assert.Equal("NEXT_PAGE_TOKEN", page.PaginationKey);
        Assert.Equal(3, page.Sections.Count);
        var nearby = page.Sections[0];
        Assert.Equal(ConcertFeedSectionKind.Nearby, nearby.Kind);
        Assert.Single(nearby.Concerts);
        Assert.Single(nearby.PlaylistPromotions!);
        Assert.Equal(TimeSpan.FromHours(2), nearby.Concerts[0].Date.Offset);
        // Prefers the concert-level image (and captures its extracted dark accent) over the first-artist avatar.
        Assert.Equal("https://example.invalid/images/concert-cover.jpg", nearby.Concerts[0].Image?.Url);
        Assert.Equal(0xFF0E79CFu, nearby.Concerts[0].AccentColor);
        Assert.Equal(ConcertFeedSectionKind.Recommended, page.Sections[1].Kind);
        Assert.Equal(ConcertFeedSectionKind.AllEvents, page.Sections[2].Kind);
    }

    [Fact]
    public void DetailMapper_PreservesOptionalFieldsOffersAndOffsets()
    {
        using var document = Open("concert-detail.json");
        var detail = Assert.IsType<ConcertDetails>(ConcertPathfinderMapper.MapDetails(document.RootElement));

        Assert.Equal("spotify:concert:detail-example", detail.Summary.Uri);
        Assert.Equal(TimeSpan.FromHours(-4), detail.Summary.Date.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), detail.DoorsOpenAt?.Offset);
        Assert.Equal("spotify:venue:example-venue", detail.VenueUri);
        Assert.Equal("Example Metro", detail.MetroAreaName);
        Assert.Equal("1001", detail.MetroAreaId);
        Assert.Equal(52d, detail.Coordinates?.Latitude);
        Assert.Equal(2, detail.Artists?.Count);
        Assert.Single(detail.Concepts!);

        // Lineup artist header banner: headerImage.data.sources whose dimensions key maxWidth/maxHeight (no width/height).
        Assert.Equal("https://example.invalid/images/lineup-header.jpg", detail.Artists![0].HeaderImage?.Url);
        Assert.Equal(2660, detail.Artists[0].HeaderImage?.Width);
        Assert.Equal(1140, detail.Artists[0].HeaderImage?.Height);
        Assert.Null(detail.Artists[0].AccentColor);   // the lineup headerImage carries no extracted colour

        Assert.Equal(2, detail.Offers?.Count);
        Assert.Equal(ConcertOfferAvailability.Available, detail.Offers![0].Availability);
        Assert.Equal(45m, detail.Offers[0].MinPrice);
        Assert.Equal(ConcertOfferAvailability.Unknown, detail.Offers[1].Availability);
        Assert.Null(detail.Offers[1].Url);
        Assert.Null(detail.Offers[1].ProviderImageUrl);
        Assert.Null(detail.Offers[1].MinPrice);

        var related = Assert.Single(detail.Related!);
        Assert.True(related.IsFestival);
        Assert.Equal(TimeSpan.FromHours(2), related.Date.Offset);
        // Related-concert artist header banner: visuals.headerImage.sources (width/height) + its extracted dark accent.
        var relatedArtist = Assert.Single(related.Artists!);
        Assert.Equal("https://example.invalid/images/related-header.jpg", relatedArtist.HeaderImage?.Url);
        Assert.Equal(2660, relatedArtist.HeaderImage?.Width);
        Assert.Equal(0xFFA9635Cu, relatedArtist.AccentColor);
        // With no concert-level image, the related concert borrows its first artist's avatar and banner accent.
        Assert.Equal("https://example.invalid/images/related-avatar.jpg", related.Image?.Url);
        Assert.Equal(0xFFA9635Cu, related.AccentColor);
    }

    [Fact]
    public void ConceptAndLocationMappers_ProjectAllCapturedShapes()
    {
        using var conceptsDocument = Open("concert-concepts.json");
        var concepts = ConcertPathfinderMapper.MapConcepts(conceptsDocument.RootElement);
        Assert.Equal(3, concepts.Count);
        Assert.Equal(3d, concepts[0].Weight);

        using var locationsDocument = Open("concert-locations.json");
        var root = locationsDocument.RootElement;

        var user = ConcertPathfinderMapper.MapUserLocation(root.GetProperty("userLocation"));
        Assert.Equal("1001", user?.Id);
        Assert.Equal("u123456789ab", user?.GeoHash);

        var artistPage = ConcertPathfinderMapper.MapUserLocation(root.GetProperty("artistConcertsPageLocation"));
        Assert.Equal(string.Empty, artistPage?.Id);
        Assert.Equal("u123456789ab", artistPage?.GeoHash);
        Assert.True(ConcertPathfinderMapper.MapIsInferred(root.GetProperty("inferredUserLocation")));

        var search = ConcertPathfinderMapper.MapLocations(root.GetProperty("searchConcertLocations"));
        Assert.Equal(2, search.Count);
        var reverse = ConcertPathfinderMapper.MapLocations(root.GetProperty("concertLocationsByLatLon"));
        Assert.Single(reverse);

        var snapshot = Assert.IsType<ConcertLocationSnapshot>(ConcertPathfinderMapper.MapLocationSnapshot(
            root.GetProperty("concertLocationDetails")));
        Assert.Single(snapshot.Matches);
        Assert.Equal("2001", snapshot.SavedLocation?.Id);
        Assert.True(ConcertPathfinderMapper.MapSaveLocation(root.GetProperty("saveLocation")));
    }

    [Fact]
    public void Mappers_ReturnEmptyOrNullForMalformedBranchesWithoutThrowing()
    {
        using var malformed = JsonDocument.Parse("""
            { "data": {
              "artistUnion": { "uri": 7, "profile": {} },
              "concertConcepts": { "items": [null, { "data": { "uri": "x" } }] },
              "liveEventsFeed": { "sections": [
                { "__typename": "LiveEventSection", "key": "bad", "concerts": [
                  { "data": { "uri": "spotify:concert:bad", "startDateIsoString": "not-a-date" } }
                ] }
              ] }
            } }
            """);

        Assert.Null(ConcertPathfinderMapper.MapArtistSchedule(malformed.RootElement));
        Assert.Empty(ConcertPathfinderMapper.MapConcepts(malformed.RootElement));
        var feed = Assert.IsType<ConcertFeedPage>(ConcertPathfinderMapper.MapFeed(malformed.RootElement));
        Assert.Empty(feed.Sections);
        Assert.Null(ConcertPathfinderMapper.MapDetails(malformed.RootElement));
        Assert.Empty(ConcertPathfinderMapper.MapLocations(malformed.RootElement));
    }

    [Fact]
    public void FeedAppend_DeduplicatesCanonicalUrisAndUsesNextOpaqueToken()
    {
        var firstConcert = Event("spotify:concert:first");
        var duplicate = firstConcert with { Title = "Duplicate payload" };
        var secondConcert = Event("spotify:concert:second");
        var first = new ConcertFeedPage([
            new ConcertFeedSection("all", ConcertFeedSectionKind.AllEvents, [firstConcert])
        ], "token-one");
        var next = new ConcertFeedPage([
            new ConcertFeedSection("all", ConcertFeedSectionKind.AllEvents, [duplicate, secondConcert])
        ], "opaque+/=token-two");

        var merged = first.Append(next);

        Assert.Equal("opaque+/=token-two", merged.PaginationKey);
        var section = Assert.Single(merged.Sections);
        Assert.Equal(["spotify:concert:first", "spotify:concert:second"], section.Concerts.Select(x => x.Uri));
    }

    [Fact]
    public async Task Service_UsesWebPlayerAndReplaysPaginationThroughTheTransportSeam()
    {
        var fake = new FakePathfinder((_, _) => Task.FromResult<JsonDocument?>(Open("concert-feed.json")));
        var service = new SpotifyConcertService(fake);
        var page = await service.GetFeedAsync(new ConcertFeedQuery(PaginationKey: "opaque+/=token"));

        Assert.NotNull(page);
        Assert.Equal(PathfinderOps.ConcertFeed, fake.Operation);
        Assert.Equal(PathfinderOps.ConcertFeedHash, fake.Hash);
        Assert.Equal(PathfinderClient.Platform.WebPlayer, fake.Platform);
        Assert.Equal("opaque+/=token", fake.Variables.GetProperty("paginationKey").GetString());
    }

    [Fact]
    public async Task Service_WiresEveryCapturedOperationThroughTheGenericContract()
    {
        var fake = new FakePathfinder((_, _) => Task.FromResult<JsonDocument?>(JsonDocument.Parse("{}")));
        var service = new SpotifyConcertService(fake);

        await service.GetArtistScheduleAsync("spotify:artist:artist-one");
        await service.GetArtistPageLocationAsync();
        await service.GetUserLocationAsync();
        await service.IsUserLocationInferredAsync();
        await service.GetConceptsAsync(string.Empty);
        await service.GetFeedAsync(new ConcertFeedQuery());
        await service.GetLocationDetailsAsync(null);
        await service.SearchLocationsAsync(string.Empty);
        await service.ReverseLocationAsync(new GeoCoordinates(52, 4));
        await service.SaveLocationAsync("1001");
        await service.GetDetailsAsync("spotify:concert:event-one");

        Assert.Equal([
            PathfinderOps.ArtistConcerts,
            PathfinderOps.ArtistConcertsPageLocation,
            PathfinderOps.UserLocation,
            PathfinderOps.InferredUserLocation,
            PathfinderOps.ConcertConcepts,
            PathfinderOps.ConcertFeed,
            PathfinderOps.ConcertLocationDetails,
            PathfinderOps.SearchConcertLocations,
            PathfinderOps.ConcertLocationsByLatLon,
            PathfinderOps.SaveLocation,
            PathfinderOps.Concert,
        ], fake.Calls.Select(x => x.Operation));
        Assert.All(fake.Calls, x => Assert.Equal(PathfinderClient.Platform.WebPlayer, x.Platform));
        Assert.Equal(TimeSpan.Zero, fake.Calls[7].Ttl);
        Assert.Equal(TimeSpan.Zero, fake.Calls[8].Ttl);
        Assert.Equal(TimeSpan.Zero, fake.Calls[9].Ttl);
    }

    [Fact]
    public async Task Service_PropagatesCancellation()
    {
        var fake = new FakePathfinder(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
        var service = new SpotifyConcertService(fake);
        using var cancellation = new CancellationTokenSource();

        var pending = service.GetFeedAsync(new ConcertFeedQuery(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    static Concert Event(string uri) => new(uri, "Event", "Venue", "City",
        new DateTimeOffset(2030, 1, 1, 20, 0, 0, TimeSpan.FromHours(1)));

    sealed record PathfinderCall(string Operation, string Hash, PathfinderClient.Platform Platform,
        JsonElement Variables, TimeSpan? Ttl);

    sealed class FakePathfinder(Func<string, CancellationToken, Task<JsonDocument?>> handler) : IConcertPathfinder
    {
        public List<PathfinderCall> Calls { get; } = [];
        public string? Operation { get; private set; }
        public string? Hash { get; private set; }
        public PathfinderClient.Platform Platform { get; private set; }
        public JsonElement Variables { get; private set; }

        public Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
            Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform,
            CancellationToken cancellationToken, TimeSpan? ttl = null)
        {
            Operation = operationName;
            Hash = sha256Hash;
            Platform = platform;
            Variables = ConcertPathfinderTests.Variables(writeVariables);
            Calls.Add(new PathfinderCall(operationName, sha256Hash, platform, Variables, ttl));
            return handler(operationName, cancellationToken);
        }
    }
}
