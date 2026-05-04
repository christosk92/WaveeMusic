using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface ISearchService
{
    Task<List<SearchSuggestionItem>> GetRecentSearchesAsync(CancellationToken ct = default);
    Task<List<SearchSuggestionItem>> GetSuggestionsAsync(string query, CancellationToken ct = default);
}

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
