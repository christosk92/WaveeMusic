using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    Genre,

    // Non-selectable group header row inside the flat suggestions list.
    // The flyout's keyboard navigation skips these; they span full width
    // via VariableSizedWrapGrid.ColumnSpan = 2 (set by ItemContainerStyleSelector).
    SectionHeader,

    // Settings deep-link result (omnibar Settings section). Routes via
    // SettingsNavigationParameter using ContextTag (section tag) + GroupKey.
    Setting,

    // Non-interactive placeholder rendered while a section's async fetch is in
    // flight (typically the Spotify section, which is debounced + network-bound).
    // Keyboard nav skips these and OnSuggestionChosen early-outs.
    Shimmer,

    // Your-library quicksearch results. Drawn from the metadata DB — covers
    // both local files (URI scheme wavee:local:...) and cached Spotify saved
    // items / playlists (URI scheme spotify:...). The dispatcher branches on
    // the URI prefix when routing.
    LocalTrack,
    LocalAlbum,
    LocalArtist,
    LocalPlaylist,
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

    /// <summary>
    /// Settings-result routing — the SettingsPage section tag (e.g. "general",
    /// "playback", "audio"). Null for non-Setting items.
    /// </summary>
    public string? ContextTag { get; init; }

    /// <summary>
    /// Settings-result routing — the group key passed to
    /// <c>ISettingsSearchFilter.ApplySearchFilter</c> on the destination page.
    /// Null for non-Setting items.
    /// </summary>
    public string? GroupKey { get; init; }
}

/// <summary>
/// A named group of suggestion items used by the omnibar dropdown. The flyout
/// renders these as <c>ListView.GroupStyle.HeaderTemplate</c> sections with an
/// inner <c>ItemsWrapGrid</c> panel that adapts column count to popup width.
/// Implements <c>IList&lt;SearchSuggestionItem&gt;</c> via <c>ObservableCollection&lt;T&gt;</c>
/// so a <c>CollectionViewSource</c> with <c>IsSourceGrouped = true</c> can bind directly.
/// </summary>
public sealed class SearchSuggestionGroup : ObservableCollection<SearchSuggestionItem>
{
    public SearchSuggestionGroup(string header)
    {
        Header = header;
    }

    public SearchSuggestionGroup(string header, IEnumerable<SearchSuggestionItem> items) : base(items)
    {
        Header = header;
    }

    /// <summary>Display label shown in the section header. Empty/whitespace = render no header.</summary>
    public string Header { get; }
}
