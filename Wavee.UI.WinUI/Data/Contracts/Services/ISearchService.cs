using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models.Search;

namespace Wavee.UI.WinUI.Data.Contracts.Services;

/// <summary>
/// Service for search operations.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Performs a comprehensive search across all content types.
    /// </summary>
    Task<SearchResultModel> SearchAsync(
        string query,
        SearchFilter filter = SearchFilter.All,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets search suggestions for autocomplete.
    /// </summary>
    Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter flags for search results.
/// </summary>
[Flags]
public enum SearchFilter
{
    All = 0,
    Tracks = 1,
    Artists = 2,
    Albums = 4,
    Playlists = 8,
    Shows = 16,
    Episodes = 32
}
