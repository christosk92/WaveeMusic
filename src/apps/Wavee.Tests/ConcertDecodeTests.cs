// ── Wavee.Tests/ConcertDecodeTests.cs — the twelve concert folds, against the captured fixtures ─────────────────────────
//
// Ports the DECODING assertions of `_old/Wavee.Tests/ConcertPathfinderTests.cs` and `ConcertCaptureContractTests.cs`
// onto 0.3's folds: each answer is decoded into a `Staging`, committed, and read back off the tables — the path a live
// answer takes. Fixtures are 0.2.9's sanitized captures (`Fixtures/concerts/`).
//
// NOT PORTED, with the reason: the five request-WRITER facts (`RequestWriters_*`, `FeedRequest_*` ×2,
// `FeedCountRequest_*`, `LocationAndDetailRequests_*`) — 0.3's concert request bodies are built inline by `Spotify.Api`'s
// senders (a shared file) with no pure body builder to call; the six raw-JSON `ConcertCaptureContractTests` shape facts
// (`ArtistConcerts_*` ×2, `ConcertFeed_*`, `ConcertDetail_*`, `LocationFixture_*`, `FeedPage_TreatsPaginationKeyAsOpaque`)
// — they assert the fixture TEXT, and the decode facts below pin the same shapes through the folds (the opaque key is
// `FeedAppend_*`'s `opaque+/=token-two`). Columns 0.3 does not carry (the nearby branch's location NAME, the metro area's
// full name, a detail answer's concept list, a related show's own lineup) drop their one assertion each.

using FluentGpu.Foundation;
using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class ConcertDecodeTests
{
    static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "concerts");
    static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, name));
    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    static string R(StringId id) => Entities.Strings.Resolve(id);
    static Concert ConcertOf(string uri) => Entities.Concert(EntityUri.Parse(uri));

    [Fact]
    public void PersistedQueryHashes_MatchCapturedContracts()
    {
        Assert.Equal("ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b", Spotify.Api.Queries.ArtistConcerts.Hash);
        Assert.Equal("320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03", Spotify.Api.Queries.ArtistConcertsPageLocation.Hash);
        Assert.Equal("079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2", Spotify.Api.Queries.UserLocation.Hash);
        Assert.Equal("5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba", Spotify.Api.Queries.InferredUserLocation.Hash);
        Assert.Equal("29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141", Spotify.Api.Queries.ConcertCount.Hash);
        Assert.Equal("a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72", Spotify.Api.Queries.ConcertConcepts.Hash);
        Assert.Equal("9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a", Spotify.Api.Queries.ConcertFeed.Hash);
        Assert.Equal("b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681", Spotify.Api.Queries.ConcertLocationDetails.Hash);
        Assert.Equal("43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3", Spotify.Api.Queries.SearchConcertLocations.Hash);
        Assert.Equal("8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96", Spotify.Api.Queries.ConcertLocationsByLatLon.Hash);
        Assert.Equal("5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353", Spotify.Api.Queries.SaveLocation.Hash);
        Assert.Equal("21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e", Spotify.Api.Queries.Concert.Hash);
    }

    [Fact]
    public void FeedCount_ReadsTotalCountAndNullsMalformedBranches()
    {
        Assert.Equal(9191, Spotify.Decode.ConcertCountOf(Fixture("concert-count.json")));
        Assert.Null(Spotify.Decode.ConcertCountOf(U("""{ "data": { "concerts": {} } }""")));

        TestScope.Fresh();
        var s = Staging.Rent();
        Assert.True(Spotify.Decode.ConcertCount(Fixture("concert-count.json"), U("1001|100"), 2, s));
        TestScope.CommitAndPublish(s);
        int feed = ConcertPlaces.FeedSlot("1001|100");
        Assert.Equal(9191, Entities.Current.ConcertFeeds.Row[feed].Count);

        // a LATER answer to an OLDER ask never paints
        s = Staging.Rent();
        Assert.True(Spotify.Decode.ConcertCount(U("""{"data":{"concerts":{"concerts":{"totalCount":7}}}}"""), U("1001|100"), 1, s));
        TestScope.CommitAndPublish(s);
        Assert.Equal(9191, Entities.Current.ConcertFeeds.Row[feed].Count);
        Assert.Equal(2u, Entities.Current.ConcertFeeds.Row[feed].CountVersion);
    }

    [Fact]
    public void ArtistConcerts_UsesSiblingBranchesAndPreservesLocalOffsets()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistConcerts(Fixture("artist-concerts-rich.json"), U("spotify:artist:example-artist"), s);
        TestScope.CommitAndPublish(s);

        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:example-artist"));
        Assert.Equal("https://example.invalid/images/artist-header.jpg", R(artist.HeaderId));
        var schedule = Entities.Current.Edges.ArtistConcerts;
        Assert.Equal(EdgeState.Complete, schedule.State(artist.Slot));
        var all = new Concert(Assert.Single(schedule.Targets(artist.Slot).ToArray()));
        Assert.Equal("spotify:concert:tour-example", all.Uri.Text);
        Assert.Equal(-240, all.OffsetMinutes);
        Assert.Equal(2, all.Lineup.Length);
        Assert.False(all.IsNearUser);

        var nearby = ConcertOf("spotify:concert:nearby-example");
        Assert.True(nearby.IsNearUser);
        Assert.Equal(120, nearby.OffsetMinutes);
    }

    [Fact]
    public void ArtistConcerts_MapsEmptyObjectsToAnEmptySchedule()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistConcerts(Fixture("artist-concerts-empty.json"), U("spotify:artist:example-artist"), s);
        TestScope.CommitAndPublish(s);

        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:example-artist"));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistConcerts.State(artist.Slot));
        Assert.Equal(0, Entities.Current.Edges.ArtistConcerts.Count(artist.Slot));
        Assert.True(artist.HeaderId.IsEmpty);
    }

    [Fact]
    public void ArtistConcerts_PrefersTheLargestUnmeasuredHeaderRendition()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistConcerts(U("""
            { "data": { "artistUnion": {
              "uri": "spotify:artist:quality", "profile": { "name": "Quality" },
              "headerImage": { "data": { "sources": [
                { "url": "https://example.invalid/16.jpg" },
                { "url": "https://example.invalid/166.jpg" },
                { "url": "https://example.invalid/full.jpg" }
              ] } }
            } } }
            """), U("spotify:artist:quality"), s);
        TestScope.CommitAndPublish(s);

        Assert.Equal("https://example.invalid/full.jpg", R(Entities.Artist(EntityUri.Parse("spotify:artist:quality")).HeaderId));
    }

    [Fact]
    public void Detail_KeepsTheLargestFirstUnmeasuredAvatarRendition()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ConcertDetail(U("""
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
            """), s);
        TestScope.CommitAndPublish(s);

        var c = ConcertOf("spotify:concert:quality");
        Assert.Equal("https://example.invalid/640.jpg", R(Assert.Single(c.Lineup.ToArray()).Image));
        Assert.Equal("https://example.invalid/640.jpg", R(c.ImageId));
    }

    [Fact]
    public void Feed_SeparatesPromotionsAndKeepsOpaquePagination()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Assert.True(Spotify.Decode.ConcertFeed(Fixture("concert-feed.json"), U("1001|100"), append: false, s));
        TestScope.CommitAndPublish(s);

        int feed = ConcertPlaces.FeedSlot("1001|100");
        var e = Entities.Current.Edges;
        Assert.Equal("NEXT_PAGE_TOKEN", R(Entities.Current.ConcertFeeds.Row[feed].PaginationKey));
        var sections = e.FeedSection.Payload(feed).ToArray();   // one payload per member: one concert per section here
        Assert.Equal(3, sections.Length);
        Assert.Equal((byte)ConcertFeedSectionKind.Nearby, sections[0].Kind);
        Assert.Equal((byte)ConcertFeedSectionKind.Recommended, sections[1].Kind);
        Assert.Equal((byte)ConcertFeedSectionKind.AllEvents, sections[2].Kind);
        Assert.Equal("all-events", R(sections[2].Key));
        var promo = Assert.Single(e.FeedSectionPlaylists.Payload(feed).ToArray());
        Assert.Equal((byte)ConcertFeedSectionKind.Nearby, promo.Kind);

        var nearby = new Concert(e.FeedSection.Targets(feed)[0]);
        Assert.Equal(120, nearby.OffsetMinutes);
        // The concert-level image (and its extracted dark accent) wins over the first artist's avatar.
        Assert.Equal("https://example.invalid/images/concert-cover.jpg", R(nearby.ImageId));
        Assert.Equal(0xFF0E79CFu, nearby.Accent);
        Assert.True(nearby.IsNearUser);
    }

    [Fact]
    public void Detail_PreservesOptionalFieldsOffersAndOffsets()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Assert.False(Spotify.Decode.ConcertDetail(Fixture("concert-detail.json"), s).IsEmpty);
        TestScope.CommitAndPublish(s);

        var d = ConcertOf("spotify:concert:detail-example");
        Assert.True(d.Knows(ConcertFields.All));
        Assert.Equal(-240, d.OffsetMinutes);
        Assert.Equal(TimeSpan.FromHours(-4), d.DoorsLocal!.Value.Offset);
        Assert.Equal("spotify:venue:example-venue", R(d.VenuePlaceId));
        Assert.Equal("1001", R(d.MetroAreaId));
        Assert.Equal(52f, d.Lat);
        Assert.Equal(2, d.Lineup.Length);

        var lineup = d.Lineup.ToArray();
        Assert.Equal("https://example.invalid/images/lineup-header.jpg", R(lineup[0].HeaderImage));
        Assert.Equal(0u, lineup[0].Accent);                      // the lineup headerImage carries no extracted colour

        var offers = d.Offers.ToArray();
        Assert.Equal(2, offers.Length);
        Assert.Equal((byte)ConcertOfferAvailability.Available, offers[0].Availability);
        Assert.Equal("45 - 75 EUR", ConcertOffers.PriceLabel(in offers[0]));
        Assert.Equal((byte)ConcertOfferAvailability.Unknown, offers[1].Availability);
        Assert.Null(ConcertOffers.ValidTicketUrl(R(offers[1].Url)));
        Assert.Equal(0, offers[1].Flags & OfferEdge.HasMin);

        var related = new Concert(Assert.Single(d.RelatedSlots.ToArray()));
        Assert.True(related.IsFestival);
        Assert.Equal(120, related.OffsetMinutes);
        // With no concert-level image the related show borrows its first artist's avatar and banner accent.
        Assert.Equal("https://example.invalid/images/related-avatar.jpg", R(related.ImageId));
        Assert.Equal(0xFFA9635Cu, related.Accent);
    }

    [Fact]
    public void ConceptAndLocationFolds_ProjectAllCapturedShapes()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ConcertConcepts(Fixture("concert-concepts.json"), U("geo:u123456789ab"), s);
        TestScope.CommitAndPublish(s);
        var place = Entities.Current.Places;
        Assert.True(place.TryGetSlot(Entities.Strings.Intern("geo:u123456789ab"), out int conceptPlace));
        var concepts = ConcertPlaces.ConceptsOf(conceptPlace);
        Assert.Equal(3, concepts.Count);
        Assert.Equal(3d, concepts[0].Weight);

        using var doc = JsonDocument.Parse(Fixture("concert-locations.json"));
        byte[] Part(string name) => U(doc.RootElement.GetProperty(name).GetRawText());

        s = Staging.Rent();
        bool? inferred = Spotify.Decode.InferredUserLocation(Part("inferredUserLocation"));
        Assert.True(inferred);
        Assert.True(Spotify.Decode.UserLocation(Part("userLocation"), inferred, s));
        Assert.True(Spotify.Decode.ArtistPageLocation(Part("artistConcertsPageLocation"), s));
        Assert.Equal(2, Spotify.Decode.ConcertLocations(Part("searchConcertLocations"), s));
        Assert.Equal(1, Spotify.Decode.ConcertLocations(Part("concertLocationsByLatLon"), s));
        Assert.Equal(2, Spotify.Decode.ConcertLocations(Part("concertLocationDetails"), s));
        TestScope.CommitAndPublish(s);

        var user = ConcertPlaces.From(Entities.Current.SavedPlace);
        Assert.Equal("1001", user?.Id);
        Assert.Equal("u123456789ab", user?.GeoHash);
        Assert.True(ConcertPlaces.IsInferred(Entities.Current.SavedPlace));
        var artistPage = ConcertPlaces.From(Entities.Current.ArtistPagePlace);
        Assert.Equal(string.Empty, artistPage?.Id);
        Assert.Equal("u123456789ab", artistPage?.GeoHash);
        Assert.True(place.TryGetSlot(Entities.Strings.Intern("2001"), out _));   // the details answer's saved city
        Assert.True(Spotify.Decode.SaveConcertLocation(Part("saveLocation")));
    }

    [Fact]
    public void Folds_StageNothingForMalformedBranchesWithoutThrowing()
    {
        TestScope.Fresh();
        var malformed = U("""
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
        var s = Staging.Rent();
        Spotify.Decode.ArtistConcerts(malformed, U("spotify:artist:malformed"), s);
        Spotify.Decode.ConcertConcepts(malformed, U("geo:bad"), s);
        Assert.True(Spotify.Decode.ConcertFeed(malformed, U("bad|100"), append: false, s));
        Assert.True(Spotify.Decode.ConcertDetail(malformed, s).IsEmpty);
        Assert.Equal(0, Spotify.Decode.ConcertLocations(malformed, s));
        Assert.Equal(0, s.Concerts.Count);                        // no date, no row
        TestScope.CommitAndPublish(s);

        int feed = ConcertPlaces.FeedSlot("bad|100");
        Assert.Equal(0, Entities.Current.Edges.FeedSection.Count(feed));
        Assert.True(Entities.Current.Places.TryGetSlot(Entities.Strings.Intern("geo:bad"), out int placeSlot));
        Assert.Empty(ConcertPlaces.ConceptsOf(placeSlot));
    }

    [Fact]
    public void FeedAppend_DeduplicatesCanonicalUrisAndUsesNextOpaqueToken()
    {
        TestScope.Fresh();
        static byte[] Page(string token, params string[] uris)
        {
            var items = string.Join(",", uris.Select(u =>
                "{\"data\":{\"uri\":\"" + u + "\",\"title\":\"Event\",\"startDateIsoString\":\"2030-01-01T20:00:00+01:00\","
                + "\"location\":{\"name\":\"Venue\",\"city\":\"City\"}}}"));
            return U("{\"data\":{\"liveEventsFeed\":{\"sections\":[{\"__typename\":\"AllEvents\",\"paginationKey\":\"" + token
                     + "\",\"sections\":[{\"key\":\"all\",\"concerts\":[" + items + "]}]}]}}}");
        }

        var s = Staging.Rent();
        Spotify.Decode.ConcertFeed(Page("token-one", "spotify:concert:first"), U("f|100"), append: false, s);
        TestScope.CommitAndPublish(s);
        s = Staging.Rent();
        Spotify.Decode.ConcertFeed(Page("opaque+/=token-two", "spotify:concert:first", "spotify:concert:second"), U("f|100"),
            append: true, s);
        TestScope.CommitAndPublish(s);

        int feed = ConcertPlaces.FeedSlot("f|100");
        Assert.Equal("opaque+/=token-two", R(Entities.Current.ConcertFeeds.Row[feed].PaginationKey));
        var uris = Entities.Current.Edges.FeedSection.Targets(feed).ToArray().Select(t => new Concert(t).Uri.Text).ToArray();
        Assert.Equal(new[] { "spotify:concert:first", "spotify:concert:second" }, uris);
    }

    [Fact]
    public void FeedMerge_KeepsHeldSectionOrder_AppendsIntoMatchingSections_AndDedupesTheHeldListFirst()
    {
        TestScope.Fresh();
        var k1 = Entities.Strings.Intern("k1");
        var k2 = Entities.Strings.Intern("k2");
        ReadOnlySpan<int> held = [10, 11];
        ReadOnlySpan<FeedSectionEdge> heldPayload = [new(2, k1), new(2, k1)];
        ReadOnlySpan<int> incoming = [12, 11, 13];
        ReadOnlySpan<FeedSectionEdge> incomingPayload = [new(1, k2), new(2, k1), new(2, k1)];
        Span<int> outTargets = stackalloc int[5];
        Span<FeedSectionEdge> outPayload = new FeedSectionEdge[5];

        int n = ConcertFeedMerge.Merge(held, heldPayload, incoming, incomingPayload, outTargets, outPayload);

        // k1 keeps its place first (held 10, 11, then the page's 13 — its 11 was a duplicate), the new k2 section follows
        Assert.Equal(new[] { 10, 11, 13, 12 }, outTargets[..n].ToArray());
        Assert.Equal(3, ConcertFeedMerge.SectionEnd(outPayload[..n], 0));
    }

    [Fact]
    public void Fixtures_AreSanitizedAndContainNoCapturedHeaders()
    {
        foreach (string path in Directory.EnumerateFiles(FixtureDir, "*.json"))
        {
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client-token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("spotifycdn.com", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
