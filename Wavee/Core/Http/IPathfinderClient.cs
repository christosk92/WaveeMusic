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
}
