using Wavee.Core.Http.Pathfinder;

namespace Wavee.Core.Http;

/// <summary>
/// Interface for Spotify's Pathfinder GraphQL API client.
/// </summary>
public interface IPathfinderClient
{
    /// <summary>
    /// Searches for tracks, artists, albums, and playlists.
    /// </summary>
    /// <param name="query">Search query string.</param>
    /// <param name="limit">Maximum number of results per type (default 10).</param>
    /// <param name="offset">Offset for pagination (default 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results containing tracks, artists, albums, and playlists.</returns>
    Task<SearchResult> SearchAsync(
        string query,
        SearchScope scope = SearchScope.All,
        int limit = 10,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current user's top artists and tracks.
    /// </summary>
    /// <param name="artistLimit">Maximum number of top artists to return (default 10).</param>
    /// <param name="trackLimit">Maximum number of top tracks to return (default 10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user's top content (artists and tracks).</returns>
    Task<UserTopContentResponse> GetUserTopContentAsync(
        int artistLimit = 10,
        int trackLimit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches extracted colors (dark, light, raw) for a list of image URLs.
    /// </summary>
    /// <param name="imageUrls">The image URLs to extract colors from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extracted colors response.</returns>
    Task<ExtractedColorsResponse> GetExtractedColorsAsync(
        IReadOnlyList<string> imageUrls,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the user's personalized home feed.
    /// </summary>
    /// <param name="sectionItemsLimit">Maximum items per section (default 10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The home feed response with sections and items.</returns>
    Task<HomeResponse> GetHomeAsync(
        int sectionItemsLimit = 10,
        string? facet = null,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up preview media for home feed baseline items.
    /// </summary>
    Task<FeedBaselineLookupResponse> GetFeedBaselineLookupAsync(
        IReadOnlyList<string> uris,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches a comprehensive artist overview including profile, discography, top tracks, and related artists.
    /// </summary>
    /// <param name="artistUri">Spotify artist URI (e.g. "spotify:artist:xxx").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The artist overview response.</returns>
    Task<ArtistOverviewResponse> GetArtistOverviewAsync(
        string artistUri,
        CancellationToken ct = default);

    /// <summary>Fetches a paginated page of an artist's full discography (all types).</summary>
    Task<ArtistDiscographyResponse> GetArtistDiscographyAllAsync(
        string artistUri, int offset = 0, int limit = 50, CancellationToken ct = default);

    /// <summary>Fetches a paginated page of an artist's albums.</summary>
    Task<ArtistDiscographyResponse> GetArtistDiscographyAlbumsAsync(
        string artistUri, int offset = 0, int limit = 20, CancellationToken ct = default);

    /// <summary>Fetches a paginated page of an artist's singles &amp; EPs.</summary>
    Task<ArtistDiscographyResponse> GetArtistDiscographySinglesAsync(
        string artistUri, int offset = 0, int limit = 20, CancellationToken ct = default);

    /// <summary>Fetches a paginated page of an artist's compilations.</summary>
    Task<ArtistDiscographyResponse> GetArtistDiscographyCompilationsAsync(
        string artistUri, int offset = 0, int limit = 20, CancellationToken ct = default);

    /// <summary>Fetches tracks for an album.</summary>
    Task<AlbumTracksResponse> GetAlbumTracksAsync(
        string albumUri, int offset = 0, int limit = 300, CancellationToken ct = default);

    /// <summary>Fetches full album detail (metadata, tracks, cover art, artists, more by artist).</summary>
    Task<GetAlbumResponse> GetAlbumAsync(
        string albumUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches a playlist's visual identity (extracted colour palette) via the
    /// <c>fetchPlaylist</c> persisted query. Items are deliberately requested
    /// with <c>limit=0</c> — track data flows through the diff path elsewhere;
    /// this call exists purely to drive the album-style hero tinting.
    /// </summary>
    Task<FetchPlaylistResponse> FetchPlaylistAsync(
        string playlistUri, CancellationToken ct = default);

    /// <summary>Fetches the user's saved location from their Spotify profile.</summary>
    Task<UserLocationResponse> GetUserLocationAsync(CancellationToken ct = default);

    /// <summary>Resolves lat/lon coordinates to a concert location (geonameId + name).</summary>
    Task<ConcertLocationsResponse> GetConcertLocationByLatLonAsync(
        double lat, double lon, CancellationToken ct = default);

    /// <summary>Saves the user's location to their Spotify profile.</summary>
    Task<SaveLocationResponse> SaveLocationAsync(string geonameId, CancellationToken ct = default);

    /// <summary>Searches for concert locations by city name.</summary>
    Task<ConcertLocationsResponse> SearchConcertLocationsAsync(string query, CancellationToken ct = default);

    /// <summary>Fetches full concert detail (artists, offers, related concerts, etc).</summary>
    Task<ConcertDetailResponse> GetConcertAsync(string concertUri, CancellationToken ct = default);

    /// <summary>Fetches the user's recent search history.</summary>
    Task<RecentSearchesResponse> GetRecentSearchesAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>Fetches search autocomplete suggestions for a query prefix.</summary>
    Task<SearchSuggestionsResponse> GetSearchSuggestionsAsync(string query, int limit = 30, CancellationToken ct = default);

    /// <summary>
    /// Resolves URIs to entity metadata (names, images, types) for recently played items.
    /// </summary>
    Task<RecentlyPlayedEntitiesResponse> FetchEntitiesForRecentlyPlayedAsync(
        IReadOnlyList<string> uris,
        CancellationToken ct = default);

    /// <summary>Fetches merchandise items for an album.</summary>
    Task<AlbumMerchResponse> GetAlbumMerchAsync(string albumUri, CancellationToken ct = default);

    /// <summary>Fetches full track credits (all contributors, grouped by role).</summary>
    Task<TrackCreditsResponse> GetTrackCreditsAsync(
        string trackUri, int contributorsLimit = 100,
        CancellationToken ct = default);

    /// <summary>Fetches NPV (Now Playing View) artist and track details for the details panel.</summary>
    Task<NpvArtistResponse> GetNpvArtistAsync(
        string artistUri, string trackUri,
        int contributorsLimit = 10, int contributorsOffset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches Spotify's "watch next" track recommendations for a given track URI.
    /// Backed by the <c>internalLinkRecommenderTrack</c> persisted query — same
    /// data that drives the SEO-related-tracks list on share pages and the
    /// desktop player's "Recommended" sidebar. Used to populate the YouTube-
    /// style up-next list on the Now Playing video page.
    /// </summary>
    Task<SeoRecommendedTracksResponse> GetSeoRecommendedTracksAsync(
        string trackUri, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Full track payload — playcount, duration, content rating, album, and
    /// the first artist's discography. Backed by the <c>getTrack</c> persisted
    /// query. Used by the Now Playing video page hero to surface
    /// "X plays" next to the track title (Track protobuf and npvArtist
    /// don't carry playcount, so this query is required for that field).
    /// </summary>
    Task<GetTrackResponse> GetTrackAsync(
        string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Full podcast episode payload: description, show data, preview, share URL,
    /// transcript metadata, and play state. Backed by Spotify's
    /// <c>getEpisodeOrChapter</c> persisted query.
    /// </summary>
    Task<GetEpisodeOrChapterResponse> GetEpisodeOrChapterAsync(
        string episodeUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches Spotify's recommended podcast episodes for a given episode URI.
    /// Backed by the <c>internalLinkRecommenderEpisode</c> persisted query.
    /// </summary>
    Task<SeoRecommendedEpisodesResponse> GetSeoRecommendedEpisodesAsync(
        string episodeUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches the public comment page for a podcast episode or other entity.
    /// Backed by the <c>getCommentsForEntity</c> persisted query.
    /// </summary>
    Task<EntityCommentsResponse> GetCommentsForEntityAsync(
        string entityUri, string? token = null, CancellationToken ct = default);

    /// <summary>
    /// Fetches a page of replies for a single comment.
    /// Backed by the <c>getReplies</c> persisted query.
    /// </summary>
    Task<CommentRepliesResponse> GetCommentRepliesAsync(
        string commentUri, string? pageToken = null, CancellationToken ct = default);

    /// <summary>
    /// Fetches reaction users and aggregate counts for a comment or reply.
    /// Backed by the <c>getReactions</c> persisted query.
    /// </summary>
    Task<CommentReactionsResponse> GetCommentReactionsAsync(
        string uri, string? token = null, string? reactionUnicode = null, CancellationToken ct = default);
}
