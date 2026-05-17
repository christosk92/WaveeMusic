using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Search;

/// <summary>
/// In-memory cache for omnibar suggestion buckets. Two slots:
///   - A single "recent searches" entry (the zero-query state).
///   - An LRU-by-age map of query → Spotify suggestion list, capped at
///     <see cref="Capacity"/>. On overflow, the oldest entry by
///     <see cref="CachedAt"/> is evicted.
///
/// <para>Both slots have configurable lifetimes — callers ask the cache
/// whether an entry exists AND whether it's fresh; stale entries are still
/// returned so the omnibar can render instantly from cache while a
/// background refresh fires.</para>
///
/// <para>Framework-neutral — no XAML / WinUI types. Singleton-friendly; not
/// thread-safe (the omnibar VM only touches it on the UI thread).</para>
/// </summary>
public sealed class OmnibarSuggestionCache
{
    public const int DefaultCapacity = 24;
    public static readonly TimeSpan DefaultRecentSearchesLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultQuerySuggestionsLifetime = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, CachedEntry> _querySuggestions;
    private CachedEntry? _recentSearches;

    public OmnibarSuggestionCache(
        int capacity = DefaultCapacity,
        TimeSpan? recentSearchesLifetime = null,
        TimeSpan? querySuggestionsLifetime = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        Capacity = capacity;
        RecentSearchesLifetime = recentSearchesLifetime ?? DefaultRecentSearchesLifetime;
        QuerySuggestionsLifetime = querySuggestionsLifetime ?? DefaultQuerySuggestionsLifetime;
        _querySuggestions = new Dictionary<string, CachedEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public int Capacity { get; }
    public TimeSpan RecentSearchesLifetime { get; }
    public TimeSpan QuerySuggestionsLifetime { get; }

    /// <summary>
    /// Returns a defensive copy of cached recent searches when present. <paramref name="isFresh"/>
    /// reflects whether the cached entry is still within <see cref="RecentSearchesLifetime"/>.
    /// </summary>
    public bool TryGetRecentSearches(out List<SearchSuggestionItem> items, out bool isFresh)
    {
        if (_recentSearches is { } cached)
        {
            items = Clone(cached.Items);
            isFresh = DateTimeOffset.UtcNow - cached.CachedAt <= RecentSearchesLifetime;
            return true;
        }

        items = [];
        isFresh = false;
        return false;
    }

    public void StoreRecentSearches(IEnumerable<SearchSuggestionItem> items)
    {
        _recentSearches = new CachedEntry(Clone(items), DateTimeOffset.UtcNow);
    }

    public void InvalidateRecentSearches()
    {
        _recentSearches = null;
    }

    /// <summary>
    /// Returns a defensive copy of cached query suggestions when present.
    /// <paramref name="isFresh"/> reflects whether the cached entry is still
    /// within <see cref="QuerySuggestionsLifetime"/>.
    /// </summary>
    public bool TryGetQuerySuggestions(string query, out List<SearchSuggestionItem> items, out bool isFresh)
    {
        if (!string.IsNullOrEmpty(query) && _querySuggestions.TryGetValue(query, out var cached))
        {
            items = Clone(cached.Items);
            isFresh = DateTimeOffset.UtcNow - cached.CachedAt <= QuerySuggestionsLifetime;
            return true;
        }

        items = [];
        isFresh = false;
        return false;
    }

    /// <summary>
    /// Stores a fresh suggestion list for the given query and evicts the
    /// oldest entry by <see cref="CachedAt"/> when capacity is exceeded.
    /// </summary>
    public void StoreQuerySuggestions(string query, IEnumerable<SearchSuggestionItem> items)
    {
        if (string.IsNullOrEmpty(query)) return;

        _querySuggestions[query] = new CachedEntry(Clone(items), DateTimeOffset.UtcNow);

        if (_querySuggestions.Count <= Capacity)
            return;

        var oldest = _querySuggestions
            .OrderBy(kvp => kvp.Value.CachedAt)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(oldest.Key))
            _querySuggestions.Remove(oldest.Key);
    }

    public void Clear()
    {
        _querySuggestions.Clear();
        _recentSearches = null;
    }

    private static List<SearchSuggestionItem> Clone(IEnumerable<SearchSuggestionItem> items)
        => items.ToList();

    private sealed record CachedEntry(List<SearchSuggestionItem> Items, DateTimeOffset CachedAt);
}
