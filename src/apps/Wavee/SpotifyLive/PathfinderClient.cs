using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// Spotify Pathfinder (GraphQL) seam for rich catalog reads that have no protobuf equivalent.
public sealed class PathfinderClient
{
    const string Endpoint = "https://api-partner.spotify.com/pathfinder/v2/query";

    readonly IHttpExchange _http;
    readonly WaveeLogger _log;

    public PathfinderClient(IHttpExchange http, WaveeLogger log = default)
    {
        _http = http;
        _log = log;
    }

    public PathfinderClient(Func<CancellationToken, Task<string>> bearer,
        Func<CancellationToken, Task<string?>> clientToken, WaveeLogger log = default, string language = "en")
        : this(new HttpPipeline(
            new HttpClientExchange(HttpPools.Get(HttpPool.ControlPlane)),
            new AuthMiddleware((_, c) => bearer(c)),
            new RateLimitMiddleware(),
            new PathfinderHeadersMiddleware(clientToken, language)), log) { }

    public enum Platform { Desktop, WebPlayer }

    public async Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, Platform platform = Platform.Desktop, CancellationToken ct = default)
    {
        var body = BuildBody(operationName, sha256Hash, writeVariables);
        var bytes = await QueryBodyBytesAsync(operationName, body, platform, ct).ConfigureAwait(false);
        return bytes is null ? null : JsonDocument.Parse(bytes);
    }

    public async Task<byte[]?> QueryBodyBytesAsync(string operationName, byte[] body,
        Platform platform = Platform.Desktop, CancellationToken ct = default)
    {
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PathfinderHeadersMiddleware.PlatformHeader] = platform == Platform.WebPlayer
                    ? PathfinderHeadersMiddleware.WebPlayerPlatform
                    : PathfinderHeadersMiddleware.DesktopPlatform,
                ["content-type"] = "application/json",
            };

            using var resp = await _http.SendAsync(new HttpReq("POST", Endpoint, headers, body), ct).ConfigureAwait(false);
            if (resp.Status is < 200 or >= 300)
            {
                _log.Info($"pathfinder {operationName} -> HTTP {resp.Status}{(resp.Status == 400 ? " (stale persisted-query hash - needs recapture)" : "")}");
                return null;
            }
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("pathfinder " + operationName + " error: " + ex.Message); return null; }
    }

    public static byte[] BuildBody(string operationName, string sha256Hash, Action<Utf8JsonWriter>? writeVariables)
    {
        using var ms = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("variables");
            w.WriteStartObject();
            writeVariables?.Invoke(w);
            w.WriteEndObject();
            w.WriteString("operationName", operationName);
            w.WritePropertyName("extensions");
            w.WriteStartObject();
            w.WritePropertyName("persistedQuery");
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("sha256Hash", sha256Hash);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

public static class PathfinderOps
{
    // Recaptured against Spotify 1.2.94.583 (artist_more/omg/all/concerts.saz — 18 samples, every one HTTP 200). The
    // previous hash 7f86ff63… appears NOWHERE in ~11k captured sessions, so it was unverifiable rather than
    // known-good. This document also carries onPlatformReputationTrait, watchFeedEntrypoint, relatedContent.*V2 and
    // preReleaseV2 — see SpotifyExportMapper.MapArtist.
    public const string QueryArtistOverview = "queryArtistOverview";
    public const string QueryArtistOverviewHash = "ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a";

    public const string GetAlbum = "getAlbum";
    public const string GetAlbumHash = "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10";

    // Desktop "home" persisted query (INTEGRATION_DESKTOP). The response embeds the recently-played list inline
    // (HomeRecentlyPlayedSectionData → a `List` of recent entities), so no separate `recents` query is needed.
    public const string Home = "home";
    public const string HomeHash = "9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896";

    // Paging the items INSIDE one Home section — the real endpoint behind Home's "Show all", which used to be routed
    // through browseSection (a browse resource that has no business answering a spotify:section: Home URI).
    // Capture proof (all_home.saz, Spotify desktop 1.2.95.453): session 020 (`home`) and session 480 (`homeSection`)
    // carry the IDENTICAL persisted hash — ONE document hosts both operations and `operationName` selects the root
    // (`data.home` vs `data.homeSections`). Exactly the queryArtistDiscographyOverview/All shape below: do not
    // "de-duplicate" the pair, because the (name, hash) tuple is what identifies the call.
    // NOTE: the capture's shared hash is 76243c78b0e20ecdbe41b794dec8cbe73f75e585b0a7201b8d2e84578412847a; Wavee pins a
    // DIFFERENT app version's document (HomeHash above), and the "one document, two operations" property is what makes
    // pinning HomeHash here correct rather than a copy of the capture's value.
    public const string HomeSection = "homeSection";
    public const string HomeSectionHash = HomeHash;

    public const string SearchTracks = "searchTracks";
    public const string SearchTracksHash = "59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55";
    // Recaptured 1.2.94.583 (omg.saz sid 0458/0706). The previous hash 5e7d2724… has ZERO wire support across the
    // whole corpus — searchAlbums was the single op whose value nothing corroborated.
    public const string SearchAlbums = "searchAlbums";
    public const string SearchAlbumsHash = "64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3";
    public const string SearchArtists = "searchArtists";
    public const string SearchArtistsHash = "270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13";
    public const string SearchPlaylists = "searchPlaylists";
    public const string SearchPlaylistsHash = "af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8";

    // The facets SearchFacet already declares but LiveSessionHost used to throw NotSupportedException for.
    // All five share the standard search variable shape EXCEPT the two noted below (SearchRequests enforces it).
    public const string SearchPodcasts = "searchPodcasts";
    public const string SearchPodcastsHash = "0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e";
    // NOTE: the ONLY search op that sends includePreReleases:true.
    public const string SearchAudiobooks = "searchAudiobooks";
    public const string SearchAudiobooksHash = "e05ac765d02c084f8783d3c1572b23d57761c43f47eb8b87ce2f9ccced3fa068";
    public const string SearchAuthors = "searchAuthors";
    public const string SearchAuthorsHash = "4a9d403a7cbc7e19da5520d619a865472b35382b043bfa458154e73a5c6f46bd";
    public const string SearchUsers = "searchUsers";
    public const string SearchUsersHash = "d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f";
    // NOTE: a DIFFERENT, minimal variable shape — {searchTerm, offset, limit, includeEpisodeContentRatingsV2} only.
    public const string SearchFullEpisodes = "searchFullEpisodes";
    public const string SearchFullEpisodesHash = "d54e35fafe7520cb53883b86d012911cbad75c14ac079a917951c24cdb07c60f";

    // ── Browse (browe.saz, 729 sessions) ─────────────────────────────────────────────────────────────────────────────
    // browseAll → the 70-category directory; browsePage → one category page of sections; browseSection → paging the
    // items INSIDE one section. pagePagination pages SECTIONS, browseSection pages ITEMS — two independent axes.
    public const string BrowseAll = "browseAll";
    public const string BrowseAllHash = "dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001";
    public const string BrowsePage = "browsePage";
    public const string BrowsePageHash = "f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b";
    public const string BrowseSection = "browseSection";
    public const string BrowseSectionHash = "b13c1cccbfcb6947753c2613411b3566485c21fd5f36d80a80bb64be61ba2d51";

    // ── Artist discography paging ────────────────────────────────────────────────────────────────────────────────────
    // ONE persisted document hosts BOTH operations; operationName selects which runs. Do not "de-duplicate" these
    // constants into one — the pair (name, hash) is what identifies the call.
    public const string QueryArtistDiscographyOverview = "queryArtistDiscographyOverview";
    public const string QueryArtistDiscographyAll = "queryArtistDiscographyAll";
    public const string QueryArtistDiscographyHash = "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599";

    // THE cover-colour op (CoverColorFiller): a pre-graded dark/light × contrast palette per image. Superseded
    // fetchExtractedColors, which returned one hex and forced the app to fabricate a four-slot palette from it.
    // NOTE: takes spotify:image: URIs, NOT https URLs.
    public const string GetDynamicColorsByUris = "getDynamicColorsByUris";
    public const string GetDynamicColorsByUrisHash = "f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9";

    public const string SearchSuggestions = "searchSuggestions";
    public const string SearchSuggestionsHash = "23f33ca50a0f4153dafc5cd1b4d1370db01b72130c2994bd0ffd07d5a7fee8f0";

    public const string SearchTopResults = "searchTopResultsList";
    public const string SearchTopResultsHash = "337d8b1b4f911fb12c60996623391703c2807550baccb51d95f5eabc8c8bdacd";

    // Desktop 1.2.96.518 (search.saz). Uses searchTerm, not query. Wavee had no genres facet until Search v5.
    public const string SearchGenres = "searchGenres";
    public const string SearchGenresHash = "9e1c0e056c46239dd1956ea915b988913c87c04ce3dadccdb537774490266f46";

    public const string QueryAlbumMerch = "queryAlbumMerch";
    public const string QueryAlbumMerchHash = "3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5";
    // The DESKTOP document (16 samples in the corpus vs 1 for the web-player variant it replaces). Strict superset:
    // adds artistUnion.onPlatformReputationTrait.verification, which drives the verified-artist badge.
    public const string QueryNpvArtist = "queryNpvArtist";
    public const string QueryNpvArtistHash = "b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb";
    public const string SimilarAlbumsBasedOnThisTrack = "similarAlbumsBasedOnThisTrack";
    public const string SimilarAlbumsBasedOnThisTrackHash = "1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17";
    public const string GetTrack = "getTrack";
    public const string GetTrackHash = "612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294";

    public const string QueryWhatsNewFeed = "queryWhatsNewFeed";
    public const string QueryWhatsNewFeedHash = "d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e";

    // The signed-in user's own top artists/tracks (Home's top-artist row). ONE document serves both facets; the
    // include* variables select which run, so both inputs are sent and both result halves are mapped from one response;
    // omitting an input the document declares is what makes the server reject the call.
    public const string UserTopContent = "userTopContent";
    public const string UserTopContentHash = "49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356";

    // Batch preview lookup for home baseline recommendations: variables { uris:[playlist…] } →
    // data.lookup[].{_uri, data.previewItems.items[].data.{name, uri, albumOfTrack.coverArt, previews.audioPreviews}}.
    // Feeds the Featured editorial card's hover peek (HomeBaselinePreviews).
    public const string FeedBaselineLookup = "feedBaselineLookup";
    public const string FeedBaselineLookupHash = "a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc";

    // Concert discovery/detail contracts captured from the web-player Pathfinder surface. Variable shapes are locked
    // by ConcertCaptureContractTests; hashes intentionally remain centralized here so a recapture changes one seam.
    public const string ArtistConcerts = "ArtistConcerts";
    public const string ArtistConcertsHash = "ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b";
    public const string ArtistConcertsPageLocation = "ArtistConcertsPageLocation";
    public const string ArtistConcertsPageLocationHash = "320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03";
    public const string UserLocation = "userLocation";
    public const string UserLocationHash = "079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2";
    public const string InferredUserLocation = "inferredUserLocation";
    public const string InferredUserLocationHash = "5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba";
    public const string ConcertConcepts = "concertConcepts";
    public const string ConcertConceptsHash = "a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72";
    public const string ConcertFeed = "concertFeed";
    public const string ConcertFeedHash = "9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a";
    public const string ConcertCount = "concertCount";
    public const string ConcertCountHash = "29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141";
    public const string ConcertLocationDetails = "concertLocationDetails";
    public const string ConcertLocationDetailsHash = "b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681";
    public const string SearchConcertLocations = "searchConcertLocations";
    public const string SearchConcertLocationsHash = "43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3";
    public const string ConcertLocationsByLatLon = "concertLocationsByLatLon";
    public const string ConcertLocationsByLatLonHash = "8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96";
    public const string SaveLocation = "saveLocation";
    public const string SaveLocationHash = "5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353";
    public const string Concert = "concert";
    public const string ConcertHash = "21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e";
}
