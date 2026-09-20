using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

// Album-enrichment coverage split across the two boundaries it touches:
//   • the JSON ACL (SpotifyExportMapper, Wavee.Core) — the similar-albums / merch / track-context projections + the
//     album page's new track fields, over crafted Pathfinder JSON; and
//   • the SHARED extended-metadata transport (ExtendedMetadataSource.GetExtensionsAsync) — the arbitrary-kind request
//     framing + raw-payload extraction the recommended-playlist hydration rides, over a FakeExchange (no network).
public class AlbumEnrichmentMapperTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SimilarAlbumsFromTrack_MapsItems_AndSkipsUriless()
    {
        var albums = SpotifyExportMapper.SimilarAlbumsFromTrack(Root("""
        { "data": { "seoRecommendedTrackAlbum": { "items": [
            { "data": { "uri": "spotify:album:A", "name": "Neon", "type": "SINGLE",
                        "date": { "year": 2022 },
                        "coverArt": { "sources": [ { "url": "https://cdn/a", "width": 300, "height": 300 } ] },
                        "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "Aurora" } } ] } } },
            { "data": { "name": "no uri → skipped" } }
        ] } } }
        """));

        var a = Assert.Single(albums);
        Assert.Equal("spotify:album:A", a.Uri);
        Assert.Equal("Neon", a.Name);
        Assert.Equal(AlbumKind.Single, a.Kind);
        Assert.Equal(2022, a.Year);
        Assert.Equal("Aurora", Assert.Single(a.Artists).Name);
        Assert.NotNull(a.Cover);
    }

    [Fact]
    public void SimilarAlbumsFromTrack_MissingPath_IsEmpty()
        => Assert.Empty(SpotifyExportMapper.SimilarAlbumsFromTrack(Root("""{ "data": {} }""")));

    [Fact]
    public void AlbumMerch_MapsProducts_AndSkipsUnnamed()
    {
        var merch = SpotifyExportMapper.AlbumMerch(Root("""
        { "data": { "albumUnion": { "merch": { "items": [
            { "nameV2": "Tour Tee", "price": "$25.00", "description": "100% cotton", "url": "https://shop/tee",
              "image": { "sources": [ { "url": "https://cdn/tee", "width": 640, "height": 640 } ] } },
            { "name": "", "price": "$1" }
        ] } } } }
        """));

        var m = Assert.Single(merch);
        Assert.Equal("Tour Tee", m.Name);
        Assert.Equal("$25.00", m.Price);
        Assert.Equal("100% cotton", m.Description);
        Assert.Equal("https://shop/tee", m.ShopUrl);
        Assert.NotNull(m.Image);
    }

    [Fact]
    public void QueryNpvArtist_MapsArtistAndTrackExtras()
    {
        var root = Root("""
        { "data": {
          "artistUnion": {
            "uri": "spotify:artist:A1",
            "profile": {
              "name": "The Artist",
              "biography": { "text": "About &amp; bio" },
              "externalLinks": { "items": [
                { "name": "instagram", "url": "https://instagram.com/theartist" }
              ] }
            },
            "onPlatformReputationTrait": { "verification": { "isVerified": true } },
            "stats": {
              "monthlyListeners": 123456,
              "followers": 987,
              "worldRank": 42,
              "topCities": { "items": [
                { "city": "Athens", "country": "GR", "numberOfListeners": 1200 }
              ] }
            },
            "visuals": {
              "avatarImage": { "sources": [ { "url": "https://cdn/avatar", "width": 160, "height": 160 } ] },
              "headerImage": { "sources": [ { "url": "https://cdn/header", "width": 640, "height": 360 } ] },
              "gallery": { "items": [
                { "sources": [ { "url": "https://cdn/gallery", "width": 640, "height": 640 } ] }
              ] }
            },
            "goods": { "merch": { "items": [
              { "nameV2": "Artist Hoodie", "price": "$60", "url": "https://shop/hoodie",
                "image": { "sources": [ { "url": "https://cdn/hoodie", "width": 300, "height": 300 } ] } }
            ] } }
          },
          "trackUnion": {
            "uri": "spotify:track:T1",
            "canvas": { "fileId": "canvas-file", "type": "VIDEO", "uri": "spotify:canvas:C1", "url": "https://cdn/canvas.mp4" },
            "creditsTrait": {
              "contributors": { "items": [
                { "name": "Lead Singer", "role": "Vocals", "roleGroup": { "name": "Performers" }, "uri": "spotify:artist:A1" },
                { "name": "Studio Writer", "role": "Writer", "roleGroup": { "name": "Composition" } }
              ] },
              "sources": { "items": [ { "name": "Label copy" } ] }
            },
            "merch": { "items": [
              { "name": "Track Tee", "price": "$25", "url": "https://shop/track",
                "image": { "sources": [ { "url": "https://cdn/tracktee", "width": 300, "height": 300 } ] } },
              { "name": "" }
            ] }
          }
        } }
        """);

        var artist = SpotifyExportMapper.ArtistFromNpv(root);
        Assert.NotNull(artist);
        Assert.Equal("The Artist", artist!.Name);
        Assert.True(artist.Verified);
        Assert.Equal(42, artist.WorldRank);
        Assert.Equal("https://cdn/header", artist.HeaderImage!.Url);
        Assert.Equal("About & bio", artist.Bio);
        Assert.Equal("Athens", Assert.Single(artist.Extras!.TopCities!).City);
        Assert.Equal(ExternalLinkKind.Instagram, Assert.Single(artist.Extras!.ExternalLinks!).Kind);
        Assert.Equal("https://cdn/gallery", Assert.Single(artist.Extras!.Gallery!).Url);
        Assert.Equal("Artist Hoodie", Assert.Single(artist.Extras!.Merch!).Name);

        var track = SpotifyExportMapper.TrackNpvFromResponse(root);
        Assert.NotNull(track);
        Assert.Equal("spotify:track:T1", track!.TrackUri);
        Assert.Equal("https://cdn/canvas.mp4", track.Canvas!.Url);
        Assert.Equal("Label copy", Assert.Single(track.CreditSources));
        Assert.Equal(2, track.Credits.Count);
        Assert.True(track.Credits[0].Linkable);
        Assert.False(track.Credits[1].Linkable);
        Assert.Equal("Track Tee", Assert.Single(track.Merch).Name);
    }

    [Fact]
    public void TrackContextFromUnion_ReadsVideoSignal_AndRelatedArtists()
    {
        var ctx = SpotifyExportMapper.TrackContextFromUnion(Root("""
        { "data": { "trackUnion": {
            "associationsV3": { "videoAssociations": { "totalCount": 2 } },
            "firstArtist": { "items": [ { "relatedContent": { "relatedArtists": { "items": [
                { "uri": "spotify:artist:R1", "profile": { "name": "Rel One" },
                  "visuals": { "avatarImage": { "sources": [ { "url": "https://cdn/r1", "width": 160, "height": 160 } ] } } }
            ] } } } ] } } } }
        """));

        Assert.True(ctx.HasVideo);
        var r = Assert.Single(ctx.RelatedArtists);
        Assert.Equal("Rel One", r.Name);
        Assert.Equal("spotify:artist:R1", r.Uri);
        Assert.NotNull(r.Image);
    }

    [Fact]
    public void TrackContextFromUnion_MissingUnion_IsEmpty()
    {
        var ctx = SpotifyExportMapper.TrackContextFromUnion(Root("""{ "data": {} }"""));
        Assert.False(ctx.HasVideo);
        Assert.Empty(ctx.RelatedArtists);
    }

    [Fact]
    public void ArtistFromOverview_HtmlDecodesBio()
    {
        // Spotify HTML-encodes bios: &#39; → ' (apostrophe), &#x1f90d; → 🤍 (U+1F90D). The ACL must decode them.
        var artist = SpotifyExportMapper.ArtistFromOverview(Root("""
        { "data": { "artistUnion": { "uri": "spotify:artist:H",
            "profile": { "name": "Henry Moodie",
                "biography": { "text": "my debut album &#39;mood swings&#39; is out now &#x1f90d;" } } } } }
        """));

        Assert.NotNull(artist);
        Assert.Equal("my debut album 'mood swings' is out now \U0001F90D", artist!.Bio);
    }

    [Fact]
    public void AlbumFromUnion_ProjectsTrackFlags_AndMoreByShelf()
    {
        var album = SpotifyExportMapper.AlbumFromUnion(Root("""
        { "data": { "albumUnion": {
            "uri": "spotify:album:MAIN", "name": "Main Release", "type": "ALBUM",
            "date": { "isoString": "2021-05-01T00:00:00Z" },
            "coverArt": { "sources": [ { "url": "https://cdn/main", "width": 640, "height": 640 } ] },
            "artists": { "items": [ { "uri": "spotify:artist:LEAD", "profile": { "name": "Lead" } } ] },
            "tracksV2": { "items": [ { "track": {
                "uri": "spotify:track:T1", "name": "Hit", "duration": { "totalMilliseconds": 210000 },
                "playcount": "98765", "contentRating": { "label": "EXPLICIT" },
                "playability": { "playable": true },
                "associationsV3": { "videoAssociations": { "totalCount": 1 } },
                "artists": { "items": [ { "uri": "spotify:artist:LEAD", "profile": { "name": "Lead" } } ] } } } ] },
            "moreAlbumsByArtist": { "items": [ { "discography": { "popularReleasesAlbums": { "items": [
                { "uri": "spotify:album:OTHER", "name": "Earlier", "type": "ALBUM", "date": { "year": 2019 },
                  "tracks": { "totalCount": 10 },
                  "coverArt": { "sources": [ { "url": "https://cdn/other", "width": 300, "height": 300 } ] } },
                { "uri": "spotify:album:MAIN", "name": "self → excluded", "type": "ALBUM" }
            ] } } } ] }
        } } }
        """));

        Assert.NotNull(album);
        var t = Assert.Single(album!.Tracks!);
        // No has-video assertion here any more: the mapper deliberately stopped reading associationsV3 onto the row.
        // That fact has one home (the VideoAssociation plane, kind 99) and one reader (VideoPresence).
        Assert.True(t.IsExplicit);
        Assert.Equal(Availability.Playable, t.Availability);
        Assert.Equal(98765, t.PlayCount);

        var more = Assert.Single(album.MoreByArtist!);   // the self-reference is excluded
        Assert.Equal("spotify:album:OTHER", more.Uri);
        Assert.Equal("Earlier", more.Name);
    }

    [Fact]
    public void AlbumFromUnion_MapsBilledArtistAvatars_IncludingDataWrapper()
    {
        var album = SpotifyExportMapper.AlbumFromUnion(Root("""
        { "data": { "albumUnion": {
            "uri": "spotify:album:MAIN", "name": "Main", "type": "ALBUM",
            "date": { "isoString": "2021-05-01T00:00:00Z" },
            "coverArt": { "sources": [ { "url": "https://cdn/main", "width": 640, "height": 640 } ] },
            "artists": { "items": [
              { "uri": "spotify:artist:LEAD", "profile": { "name": "Lead" },
                "visuals": { "avatarImage": { "sources": [ { "url": "https://cdn/lead", "width": 160, "height": 160 } ] } } },
              { "data": { "uri": "spotify:artist:FEAT", "profile": { "name": "Feat" },
                "visuals": { "avatarImage": { "sources": [ { "url": "https://cdn/feat", "width": 160, "height": 160 } ] } } } }
            ] },
            "tracksV2": { "items": [] }
        } } }
        """));

        Assert.NotNull(album);
        Assert.Equal(2, album!.Artists.Count);
        Assert.Equal("Feat", album.Artists[1].Name);
        Assert.Equal(2, album.ArtistsDetailed!.Count);
        Assert.Equal("https://cdn/lead", album.ArtistsDetailed[0].Image!.Url);
        Assert.Equal("https://cdn/feat", album.ArtistsDetailed[1].Image!.Url);
    }
}

public class ExtendedMetadataExtensionTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task GetExtensionsAsync_BuildsPost_AndExtractsRawPayload()
    {
        // craft the wire response: one RECOMMENDED_PLAYLISTS array carrying an Any-packed RecommendedPlaylists for the album
        var rp = new Xm.RecommendedPlaylists();
        rp.Recommendation.Add(new Xm.RecommendedPlaylists.Types.Item { Uri = "spotify:playlist:p1" });
        rp.Recommendation.Add(new Xm.RecommendedPlaylists.Types.Item { Uri = "spotify:playlist:p2" });
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.RecommendedPlaylists };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:album:A", ExtensionData = Any.Pack(rp) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);

        HttpReq? captured = null;
        var http = new FakeExchange((req, _) => { captured = req; return new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray()); });
        var src = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);

        var result = await src.GetExtensionsAsync(
            new[] { ("spotify:album:A", Xm.ExtensionKind.RecommendedPlaylists) }, TestContext.Current.CancellationToken);

        // the raw Any value is returned keyed by (uri, kind), and round-trips through the message parser
        Assert.True(result.TryGetValue(("spotify:album:A", Xm.ExtensionKind.RecommendedPlaylists), out var bytes));
        var parsed = Xm.RecommendedPlaylists.Parser.ParseFrom(bytes);
        Assert.Equal(2, parsed.Recommendation.Count);
        Assert.Equal("spotify:playlist:p1", parsed.Recommendation[0].Uri);

        // the request: a gzipped protobuf POST to the extended-metadata endpoint with the right entity + kind
        Assert.NotNull(captured);
        Assert.Equal("POST", captured!.Method);
        Assert.EndsWith("/extended-metadata/v0/extended-metadata", captured.Url);
        Assert.Equal("gzip", captured.Headers["Content-Encoding"]);
        Assert.Equal("application/protobuf", captured.Headers["Content-Type"]);
        var req = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(captured.Body!));
        Assert.Equal("US", req.Header.Country);
        var er = Assert.Single(req.EntityRequest);
        Assert.Equal("spotify:album:A", er.EntityUri);
        Assert.Equal(Xm.ExtensionKind.RecommendedPlaylists, Assert.Single(er.Query).ExtensionKind);
    }

    [Fact]
    public async Task GetExtensionsAsync_GroupsByUri_PreservingOrder()
    {
        HttpReq? captured = null;
        var empty = new Xm.BatchedExtensionResponse();
        var http = new FakeExchange((req, _) => { captured = req; return new HttpResp(200, new Dictionary<string, string>(), empty.ToByteArray()); });
        var src = new ExtendedMetadataSource(http, () => "https://x", () => Ctx);

        await src.GetExtensionsAsync(new[]
        {
            ("spotify:album:A", Xm.ExtensionKind.RecommendedPlaylists),
            ("spotify:playlist:B", Xm.ExtensionKind.ListMetadataV2),
            ("spotify:album:A", Xm.ExtensionKind.ListMetadataV2),   // a second kind under the SAME uri → grouped, not a 2nd entity
        }, TestContext.Current.CancellationToken);

        var req = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(captured!.Body!));
        Assert.Equal(2, req.EntityRequest.Count);
        Assert.Equal("spotify:album:A", req.EntityRequest[0].EntityUri);
        Assert.Equal(2, req.EntityRequest[0].Query.Count);
        Assert.Equal(Xm.ExtensionKind.RecommendedPlaylists, req.EntityRequest[0].Query[0].ExtensionKind);
        Assert.Equal(Xm.ExtensionKind.ListMetadataV2, req.EntityRequest[0].Query[1].ExtensionKind);
        Assert.Equal("spotify:playlist:B", req.EntityRequest[1].EntityUri);
    }

    [Fact]
    public async Task GetExtensionsAsync_EmptyInput_DoesNotCallHttp()
    {
        var http = new FakeExchange((_, _) => throw new System.InvalidOperationException("must not POST for an empty request"));
        var src = new ExtendedMetadataSource(http, () => "https://x", () => Ctx);

        var result = await src.GetExtensionsAsync(System.Array.Empty<(string, Xm.ExtensionKind)>(), TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void ListMetadataV2_Proto_RoundTripsTheFieldsTheHydratorReads()
    {
        var meta = new Xm.ListMetadataV2 { Name = "Chill Mix", Description = "easy listening", Source = "spotify" };
        meta.Images = new Xm.ListMetadataV2.Types.Images();
        meta.Images.Variant.Add(new Xm.ListMetadataV2.Types.ImageVariant { Format = "default", Url = "https://cdn/cover" });

        var back = Xm.ListMetadataV2.Parser.ParseFrom(meta.ToByteString());
        Assert.Equal("Chill Mix", back.Name);
        Assert.Equal("easy listening", back.Description);
        Assert.Equal("spotify", back.Source);
        Assert.Equal("https://cdn/cover", Assert.Single(back.Images.Variant).Url);
    }
}
