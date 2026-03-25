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
}
