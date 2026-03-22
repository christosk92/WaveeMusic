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
}
