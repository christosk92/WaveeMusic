using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Search;

/// <summary>
/// Pure (no I/O, no state) helpers for composing and validating omnibar
/// suggestion groups. Takes raw section lists (Settings / library / Spotify)
/// and emits the ordered <see cref="SearchSuggestionGroup"/> list that the
/// flyout binds to.
///
/// <para>Framework-neutral — no XAML / WinUI types. Singleton-safe (stateless).</para>
/// </summary>
public sealed class OmnibarSuggestionRanker
{
    public const string SettingsHeader = "SETTINGS";
    public const string YourLibraryHeader = "YOUR LIBRARY";
    public const string SpotifyHeader = "SPOTIFY";
    public const string OpenLinkHeader = "OPEN LINK";

    /// <summary>
    /// Placeholder items shown in the Spotify section while its async fetch
    /// is in flight, so the section is visible from frame 1 instead of
    /// popping in after the debounce window. 4 entries — matches a 2×2 grid
    /// at wide widths or 4 stacked rows at narrow widths.
    /// </summary>
    public static readonly IReadOnlyList<SearchSuggestionItem> SpotifyShimmerPlaceholders =
    [
        new() { Title = string.Empty, Uri = "wavee:shimmer:0", Type = SearchSuggestionType.Shimmer },
        new() { Title = string.Empty, Uri = "wavee:shimmer:1", Type = SearchSuggestionType.Shimmer },
        new() { Title = string.Empty, Uri = "wavee:shimmer:2", Type = SearchSuggestionType.Shimmer },
        new() { Title = string.Empty, Uri = "wavee:shimmer:3", Type = SearchSuggestionType.Shimmer },
    ];

    /// <summary>
    /// Composes the three sections into a flat list of groups. Empty
    /// Settings/Library sections are dropped. The Spotify section behavior
    /// depends on its argument:
    ///   - <c>spotify == null</c> → pending state, show shimmer placeholders.
    ///   - <c>spotify.Count == 0</c> → network responded with no matches, drop section.
    ///   - <c>spotify.Count &gt; 0</c> → real items.
    /// </summary>
    public List<SearchSuggestionGroup> BuildGroups(
        IReadOnlyList<SearchSuggestionItem> settings,
        IReadOnlyList<SearchSuggestionItem> library,
        IReadOnlyList<SearchSuggestionItem>? spotify)
    {
        var groups = new List<SearchSuggestionGroup>(3);
        if (settings.Count > 0)
            groups.Add(new SearchSuggestionGroup(SettingsHeader, settings));
        if (library.Count > 0)
            groups.Add(new SearchSuggestionGroup(YourLibraryHeader, library));

        if (spotify is null)
            groups.Add(new SearchSuggestionGroup(SpotifyHeader, SpotifyShimmerPlaceholders));
        else if (spotify.Count > 0)
            groups.Add(new SearchSuggestionGroup(SpotifyHeader, spotify));

        return groups;
    }

    /// <summary>
    /// Validates that every suggestion in a flat list was produced from the
    /// given query text. Used to detect stale renders after a failed refresh
    /// — if the current items match the current query, keep them visible;
    /// otherwise clear the surface and surface the error.
    /// </summary>
    public bool DoSuggestionsMatchQuery(IReadOnlyList<SearchSuggestionItem> items, string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return items.All(item => string.IsNullOrWhiteSpace(item.QueryText));

        return items.All(item =>
            string.Equals(item.QueryText, queryText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drops shimmer-placeholder groups (e.g. when the Spotify leg fails)
    /// while preserving any populated Settings / Library groups.
    /// Returns null when the trimmed list is empty.
    /// </summary>
    public List<SearchSuggestionGroup>? TrimShimmerGroups(IReadOnlyList<SearchSuggestionGroup>? groups)
    {
        if (groups is null || groups.Count == 0) return null;

        var trimmed = groups.Where(g => g.Count == 0 || g[0].Type != SearchSuggestionType.Shimmer).ToList();
        return trimmed.Count > 0 ? trimmed : null;
    }

    /// <summary>Defensive copy — the cache returns a fresh list each read.</summary>
    public List<SearchSuggestionItem> Clone(IEnumerable<SearchSuggestionItem> items)
        => items.ToList();
}
