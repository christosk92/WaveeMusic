using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface ISearchService
{
    Task<List<SearchSuggestionItem>> GetRecentSearchesAsync(CancellationToken ct = default);
    Task<List<SearchSuggestionItem>> GetSuggestionsAsync(string query, CancellationToken ct = default);

    /// <summary>Songs chip — fires `searchTracks` (limit 20).</summary>
    Task<ChipPageResult> SearchTracksAsync(string query, int offset = 0, int limit = 20, CancellationToken ct = default);

    /// <summary>Artists chip — fires `searchArtists` (limit 30).</summary>
    Task<ChipPageResult> SearchArtistsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default);

    /// <summary>Albums chip — fires `searchAlbums` (limit 30).</summary>
    Task<ChipPageResult> SearchAlbumsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default);

    /// <summary>Playlists chip — fires `searchPlaylists` (limit 30).</summary>
    Task<ChipPageResult> SearchPlaylistsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default);

    /// <summary>
    /// Podcasts chip — fires `searchPodcasts` (shows) AND `searchFullEpisodes`
    /// in parallel and merges the results (shows first, then episodes), matching
    /// desktop UI behavior. <see cref="ChipPageResult.HasMore"/> is true if either
    /// underlying section still has untaken items.
    /// </summary>
    Task<ChipPageResult> SearchPodcastsAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default);

    /// <summary>Users chip — fires `searchUsers` (limit 30).</summary>
    Task<ChipPageResult> SearchUsersAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default);

    /// <summary>Genres chip — fires `searchGenres` (limit 30).</summary>
    Task<ChipPageResult> SearchGenresAsync(string query, int offset = 0, int limit = 30, CancellationToken ct = default);
}

/// <summary>
/// One page of chip results, plus pagination hints from the server.
/// </summary>
public sealed record ChipPageResult(
    IReadOnlyList<Wavee.Core.Http.Pathfinder.SearchResultItem> Items,
    int TotalCount,
    bool HasMore);

public enum SearchSuggestionType
{
    TextQuery,
    Artist,
    Track,
    Album,
    Playlist,
    Podcast,
    Genre
}

public sealed record SearchSuggestionItem
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public required string Uri { get; init; }
    public SearchSuggestionType Type { get; init; }

    /// <summary>
    /// The query text that produced this suggestion. Used for bold-match rendering.
    /// Set by the service layer when returning suggestions.
    /// </summary>
    public string? QueryText { get; init; }
}
