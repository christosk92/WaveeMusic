using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public enum SearchFilterType
{
    All,
    Songs,
    Artists,
    Albums,
    Playlists,
    Podcasts,
    Users,
    Genres
}

/// <summary>
/// A group of search results that arrived from a <c>SearchSectionEntity</c> top-result
/// wrapper — e.g. "Featuring JJ Lin" (playlists), "Music videos" (video tracks). Rendered
/// as a horizontal adaptive row of cards on the search page, reusing the HomePage layout.
/// </summary>
public sealed class SearchSectionViewModel
{
    public required string Title { get; init; }
    public required IReadOnlyList<SearchResultItem> Items { get; init; }
}

public sealed partial class SearchViewModel : ObservableObject, ITabBarItemContent
{
    private readonly IPathfinderClient _pathfinderClient;
    private readonly ISearchService? _searchService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ILogger? _logger;
    private readonly List<SearchResultItem> _allItems = [];
    private int _requestVersion;
    private bool _isHibernated;

    // Generic chip pagination state — only one chip is active at a time, so a
    // single set of fields drives every per-chip page (Songs/Artists/Albums/
    // Playlists/Podcasts/Users/Genres).
    private int _chipOffset;
    private int _chipTotal;
    private bool _chipHasMore;
    private bool _chipLoadingMore;

    private static int ChipPageSize(SearchFilterType filter) => filter switch
    {
        SearchFilterType.Songs => 20,                     // matches desktop searchTracks default
        _ => 30                                           // every other per-chip op uses 30
    };

    // Process-wide result cache (TTL 5 min). Survives SearchViewModel disposal so
    // tab-sleep wake (which re-instantiates the page + VM and re-fires LoadAsync via
    // OnNavigatedTo) hydrates without hitting the network. Keyed on (query|scope) so
    // the All / Artists scopes don't cross-pollute. Bounded by ResultCacheMax to keep
    // memory predictable; oldest entries are evicted when full.
    private sealed record CachedResult(
        IReadOnlyList<SearchResultItem> Items,
        SearchResultItem? TopResult,
        DateTimeOffset At,
        int ChipTotal,
        bool ChipHasMore);
    private static readonly Dictionary<string, CachedResult> _resultCache =
        new(StringComparer.Ordinal);
    private static readonly object _resultCacheGate = new();
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromMinutes(5);
    private const int ResultCacheMax = 32;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _query;

    [ObservableProperty]
    private SearchFilterType _selectedFilter = SearchFilterType.All;

    [ObservableProperty]
    private SearchResultItem? _topResult;

    [ObservableProperty]
    private bool _showTopResult = true;

    [ObservableProperty]
    private bool _showTracks = true;

    [ObservableProperty]
    private bool _showArtists = true;

    [ObservableProperty]
    private bool _showAlbums = true;

    [ObservableProperty]
    private bool _showPlaylists = true;

    [ObservableProperty]
    private bool _showEmptyState;

    public ObservableCollection<SearchResultItem> Tracks { get; } = [];
    public ObservableCollection<ITrackItem> AdaptedTracks { get; } = [];
    public ObservableCollection<SearchResultItem> Artists { get; } = [];
    public ObservableCollection<SearchResultItem> Albums { get; } = [];
    public ObservableCollection<SearchResultItem> Playlists { get; } = [];
    public ObservableCollection<SearchResultItem> VisibleResults { get; } = [];
    public ObservableCollection<SearchSectionViewModel> Sections { get; } = [];

    public bool ShowHeroCard => ShowTopResult && TopResult != null;
    public bool ShowSections => SelectedFilter == SearchFilterType.All && Sections.Count > 0;
    public bool HasVisibleResults => VisibleResults.Count > 0;
    public string ErrorTitle => AppLocalization.GetString("Search_Failed");
    public string EmptyStateMessage => string.IsNullOrWhiteSpace(Query)
        ? "Start a search to explore music, artists, albums, and playlists."
        : $"No results for \"{Query}\"";

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    private readonly Wavee.Local.ILocalLibraryService? _localLibrary;

    public SearchViewModel(
        IPathfinderClient pathfinderClient,
        IPlaybackStateService playbackStateService,
        ILogger<SearchViewModel>? logger = null,
        Wavee.Local.ILocalLibraryService? localLibrary = null,
        ISearchService? searchService = null)
    {
        _pathfinderClient = pathfinderClient;
        _playbackStateService = playbackStateService;
        _logger = logger;
        _localLibrary = localLibrary;
        _searchService = searchService;

        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, null)
        {
            Title = "Search"
        };

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    partial void OnSelectedFilterChanged(SearchFilterType value)
    {
        ShowTopResult = value == SearchFilterType.All;
        ShowTracks = value is SearchFilterType.All or SearchFilterType.Songs;
        ShowArtists = value is SearchFilterType.All or SearchFilterType.Artists;
        ShowAlbums = value is SearchFilterType.All or SearchFilterType.Albums;
        ShowPlaylists = value is SearchFilterType.All or SearchFilterType.Playlists;
        OnPropertyChanged(nameof(ShowSections));

        if (!string.IsNullOrWhiteSpace(Query))
        {
            _ = LoadAsync(Query);
            return;
        }

        UpdateVisibleResults();
    }

    /// <summary>True when the active chip has more server-side items than we've fetched.</summary>
    public bool CanLoadMore => SelectedFilter != SearchFilterType.All && _chipHasMore && !_chipLoadingMore;

    public async Task LoadAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        _isHibernated = false;
        var requestVersion = ++_requestVersion;
        var filterAtRequest = SelectedFilter;
        var cacheKey = BuildCacheKey(query, filterAtRequest);

        // Cache hit: rehydrate without a network round-trip. Covers the tab-sleep
        // wake path (page recreated, VM recreated, OnNavigatedTo re-fires with the
        // same query), and any in-app navigation back to a prior search.
        var cached = GetCachedResult(cacheKey);
        if (cached != null)
        {
            Query = query;
            HasError = false;
            ErrorMessage = null;
            ShowEmptyState = false;
            IsLoading = false;

            _allItems.Clear();
            _allItems.AddRange(cached.Items);
            DispatchResults(cached.Items);
            TopResult = cached.TopResult;
            if (filterAtRequest == SearchFilterType.All)
            {
                _chipOffset = 0;
                _chipTotal = 0;
                _chipHasMore = false;
            }
            else
            {
                _chipOffset = cached.Items.Count;
                _chipTotal = cached.ChipTotal;
                _chipHasMore = cached.ChipHasMore;
            }
            UpdateVisibleResults();
            UpdateEmptyState();
            OnPropertyChanged(nameof(CanLoadMore));

            TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, query)
            {
                Title = AppLocalization.Format("Search_TabTitle", query)
            };
            ContentChanged?.Invoke(this, TabItemParameter);
            return;
        }

        Query = query;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        ShowEmptyState = false;

        try
        {
            // Chip dispatch — every non-All filter has its own paginated server query
            // matching the desktop client. All chips go through ISearchService for the
            // unified ChipPageResult shape; All goes through the existing PathfinderClient
            // SearchAsync flow that also merges local-library results.
            if (filterAtRequest != SearchFilterType.All && _searchService != null)
            {
                var pageSize = ChipPageSize(filterAtRequest);
                var chipResult = await DispatchChipAsync(filterAtRequest, query, offset: 0, limit: pageSize);

                if (requestVersion != _requestVersion)
                    return;

                var items = chipResult.Items.ToList();
                _allItems.Clear();
                _allItems.AddRange(items);
                DispatchResults(items);
                TopResult = null;
                _chipOffset = items.Count;
                _chipTotal = chipResult.TotalCount;
                _chipHasMore = chipResult.HasMore;
                UpdateVisibleResults();

                StoreCachedResult(cacheKey, items, topResult: null, chipTotal: chipResult.TotalCount, chipHasMore: chipResult.HasMore);
                OnPropertyChanged(nameof(CanLoadMore));

                TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, query)
                {
                    Title = AppLocalization.Format("Search_TabTitle", query)
                };
                ContentChanged?.Invoke(this, TabItemParameter);
                return;
            }

            // All filter — searchTopResultsList + local-library merge.
            var spotifyTask = Task.Run(() => _pathfinderClient.SearchAsync(
                query,
                SearchScope.All,
                limit: 50));
            var localTask = _localLibrary is null
                ? Task.FromResult<IReadOnlyList<Wavee.Local.LocalSearchResult>>(Array.Empty<Wavee.Local.LocalSearchResult>())
                : _localLibrary.SearchAsync(query, limit: 12);
            await Task.WhenAll(spotifyTask, localTask);
            var result = spotifyTask.Result;
            var localResults = localTask.Result;

            if (requestVersion != _requestVersion)
                return;

            // Merge: append local items after Spotify items, tagged with a
            // "On this PC" SectionLabel so they also surface as a horizontal
            // section row on the All filter. They still flow into the Tracks /
            // Artists / Albums lists for the dedicated filters.
            var mergedItems = MergeLocalIntoSpotifyResults(result.Items, localResults);

            _allItems.Clear();
            _allItems.AddRange(mergedItems);
            DispatchResults(mergedItems);
            TopResult = result.TopResult;
            _chipOffset = 0;
            _chipTotal = 0;
            _chipHasMore = false;
            UpdateVisibleResults();

            StoreCachedResult(cacheKey, mergedItems, result.TopResult, chipTotal: 0, chipHasMore: false);
            OnPropertyChanged(nameof(CanLoadMore));

            TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, query)
            {
                Title = AppLocalization.Format("Search_TabTitle", query)
            };
            ContentChanged?.Invoke(this, TabItemParameter);
        }
        catch (Exception ex)
        {
            if (requestVersion != _requestVersion)
                return;

            _allItems.Clear();
            DispatchResults([]);
            TopResult = null;
            UpdateVisibleResults();
            _logger?.LogWarning(ex, "Search failed for query: {Query}", query);
            HasError = true;
            ErrorMessage = ErrorMapper.ToUserMessage(ex);
        }
        finally
        {
            if (requestVersion == _requestVersion)
            {

                IsLoading = false;
                UpdateEmptyState();
            }
        }
    }

    public void Hibernate()
    {
        if (_isHibernated)
            return;

        _isHibernated = true;
        _requestVersion++;
        IsLoading = false;
        HasError = false;
        ErrorMessage = null;
        ShowEmptyState = false;

        _allItems.Clear();
        DispatchResults([]);
        TopResult = null;
        UpdateVisibleResults();
    }

    public Task ResumeFromHibernateAsync()
    {
        if (!_isHibernated)
            return Task.CompletedTask;

        _isHibernated = false;
        return string.IsNullOrWhiteSpace(Query)
            ? Task.CompletedTask
            : LoadAsync(Query);
    }

    private static IReadOnlyList<SearchResultItem> MergeLocalIntoSpotifyResults(
        IReadOnlyList<SearchResultItem> spotify,
        IReadOnlyList<Wavee.Local.LocalSearchResult> local)
    {
        if (local.Count == 0) return spotify;

        var merged = new List<SearchResultItem>(spotify.Count + local.Count);
        merged.AddRange(spotify);

        const string SectionLabel = "On this PC";
        foreach (var l in local)
        {
            var type = l.Type switch
            {
                Wavee.Local.LocalSearchEntityType.Track    => SearchResultType.Track,
                Wavee.Local.LocalSearchEntityType.Album    => SearchResultType.Album,
                Wavee.Local.LocalSearchEntityType.Artist   => SearchResultType.Artist,
                Wavee.Local.LocalSearchEntityType.Playlist => SearchResultType.Playlist,
                _ => SearchResultType.Track,
            };
            merged.Add(new SearchResultItem
            {
                Type = type,
                Uri = l.Uri,
                Name = l.Name,
                ImageUrl = l.ArtworkUri,
                ArtistNames = l.Subtitle is null ? null : new List<string> { l.Subtitle },
                SectionLabel = SectionLabel,
            });
        }
        return merged;
    }

    private void DispatchResults(IReadOnlyList<SearchResultItem> items)
    {
        Tracks.Clear();
        AdaptedTracks.Clear();
        Artists.Clear();
        Albums.Clear();
        Playlists.Clear();
        Sections.Clear();

        foreach (var item in items)
        {
            switch (item.Type)
            {
                case SearchResultType.Track:
                    Tracks.Add(item);
                    AdaptedTracks.Add(new SearchTrackAdapter(item));
                    break;
                case SearchResultType.Artist:
                    Artists.Add(item);
                    break;
                case SearchResultType.Album:
                    Albums.Add(item);
                    break;
                case SearchResultType.Playlist:
                    Playlists.Add(item);
                    break;
            }
        }

        // Group items that came from a SearchSectionEntity (e.g. "Featuring X", "Music
        // videos") by their SectionLabel. Preserves original order — items were parsed
        // in document order so the first occurrence of each label determines row order.
        var orderedLabels = new List<string>();
        var sectionBuckets = new Dictionary<string, List<SearchResultItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var label = item.SectionLabel;
            if (string.IsNullOrEmpty(label)) continue;

            if (!sectionBuckets.TryGetValue(label, out var bucket))
            {
                bucket = [];
                sectionBuckets[label] = bucket;
                orderedLabels.Add(label);
            }
            bucket.Add(item);
        }

        foreach (var label in orderedLabels)
        {
            Sections.Add(new SearchSectionViewModel
            {
                Title = label,
                Items = sectionBuckets[label]
            });
        }

        OnPropertyChanged(nameof(ShowSections));
    }

    partial void OnTopResultChanged(SearchResultItem? value)
    {
        OnPropertyChanged(nameof(ShowHeroCard));
        UpdateVisibleResults();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        RetryCommand.NotifyCanExecuteChanged();
    }

    partial void OnQueryChanged(string? value)
    {
        OnPropertyChanged(nameof(EmptyStateMessage));
        RetryCommand.NotifyCanExecuteChanged();
    }

    private void UpdateVisibleResults()
    {
        VisibleResults.Clear();

        // For chips other than All, _allItems already contains only the chip's results
        // (the network response is the single chip operation). The Where filter is a
        // belt-and-suspenders pass for the rare case where mixed types sneak through —
        // e.g. the Podcasts chip merges shows + episodes server-side, both kept here.
        IEnumerable<SearchResultItem> filtered = SelectedFilter switch
        {
            SearchFilterType.Songs => _allItems.Where(static item => item.Type == SearchResultType.Track),
            SearchFilterType.Artists => _allItems.Where(static item => item.Type == SearchResultType.Artist),
            SearchFilterType.Albums => _allItems.Where(static item => item.Type == SearchResultType.Album),
            SearchFilterType.Playlists => _allItems.Where(static item => item.Type == SearchResultType.Playlist),
            SearchFilterType.Podcasts => _allItems.Where(static item => item.Type is SearchResultType.Podcast or SearchResultType.Episode),
            SearchFilterType.Users => _allItems.Where(static item => item.Type == SearchResultType.User),
            SearchFilterType.Genres => _allItems.Where(static item => item.Type == SearchResultType.Genre),
            _ => _allItems
        };

        // On the "All" view we render section items in their own horizontal rows, so
        // exclude them from the flat list to avoid duplication. On a specific filter
        // (Songs/Artists/…), keep everything — a section video track is still a valid
        // track result for the Songs filter.
        var hideSectionItems = SelectedFilter == SearchFilterType.All;

        var topKey = ShowHeroCard && TopResult != null
            ? $"{TopResult.Type}:{TopResult.Uri}"
            : null;

        foreach (var item in filtered)
        {
            if (hideSectionItems && !string.IsNullOrEmpty(item.SectionLabel))
                continue;

            if (topKey != null && $"{item.Type}:{item.Uri}" == topKey)
                continue;

            VisibleResults.Add(item);
        }

        OnPropertyChanged(nameof(HasVisibleResults));
        OnPropertyChanged(nameof(ShowHeroCard));
        OnPropertyChanged(nameof(ShowSections));
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        ShowEmptyState = !IsLoading && !HasError && !ShowHeroCard && VisibleResults.Count == 0;
    }

    /// <summary>Each filter has its own cache slot — every chip fires a distinct server query.</summary>
    private static string BuildCacheKey(string query, SearchFilterType filter)
        => string.Concat(query.Trim().ToLowerInvariant(), "|", filter.ToString());

    /// <summary>Routes a chip filter to the matching <see cref="ISearchService"/> method.</summary>
    private Task<ChipPageResult> DispatchChipAsync(SearchFilterType filter, string query, int offset, int limit)
    {
        if (_searchService is null)
            return Task.FromResult(new ChipPageResult(System.Array.Empty<SearchResultItem>(), 0, false));

        return filter switch
        {
            SearchFilterType.Songs => _searchService.SearchTracksAsync(query, offset, limit),
            SearchFilterType.Artists => _searchService.SearchArtistsAsync(query, offset, limit),
            SearchFilterType.Albums => _searchService.SearchAlbumsAsync(query, offset, limit),
            SearchFilterType.Playlists => _searchService.SearchPlaylistsAsync(query, offset, limit),
            SearchFilterType.Podcasts => _searchService.SearchPodcastsAsync(query, offset, limit),
            SearchFilterType.Users => _searchService.SearchUsersAsync(query, offset, limit),
            SearchFilterType.Genres => _searchService.SearchGenresAsync(query, offset, limit),
            _ => Task.FromResult(new ChipPageResult(System.Array.Empty<SearchResultItem>(), 0, false))
        };
    }

    /// <summary>
    /// Append the next page of chip results. Called by the SearchPage scroll handler
    /// when the user scrolls near the bottom of the chip list. Safe to call multiple
    /// times — short-circuits if a fetch is already in flight or there's nothing left.
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || string.IsNullOrWhiteSpace(Query)) return;

        var filterAtRequest = SelectedFilter;
        var query = Query!;
        var requestVersion = _requestVersion;
        var pageSize = ChipPageSize(filterAtRequest);

        _chipLoadingMore = true;
        OnPropertyChanged(nameof(CanLoadMore));
        try
        {
            var page = await DispatchChipAsync(filterAtRequest, query, _chipOffset, pageSize);

            if (requestVersion != _requestVersion || filterAtRequest != SelectedFilter)
                return;

            var newItems = page.Items;
            if (newItems.Count == 0)
            {
                _chipHasMore = false;
                return;
            }

            // Append-only — touching VisibleResults directly avoids the flicker that
            // a full DispatchResults + UpdateVisibleResults rebuild would cause.
            foreach (var item in newItems)
            {
                _allItems.Add(item);
                VisibleResults.Add(item);
            }

            _chipOffset += newItems.Count;
            _chipTotal = page.TotalCount;
            _chipHasMore = page.HasMore;
            OnPropertyChanged(nameof(HasVisibleResults));

            // Refresh the cache entry so a tab-sleep wake doesn't re-fetch what we just appended.
            var cacheKey = BuildCacheKey(query, filterAtRequest);
            StoreCachedResult(cacheKey, new List<SearchResultItem>(_allItems), TopResult, _chipTotal, _chipHasMore);
        }
        finally
        {
            _chipLoadingMore = false;
            OnPropertyChanged(nameof(CanLoadMore));
        }
    }

    private static CachedResult? GetCachedResult(string cacheKey)
    {
        lock (_resultCacheGate)
        {
            if (_resultCache.TryGetValue(cacheKey, out var entry)
                && DateTimeOffset.UtcNow - entry.At < ResultCacheTtl)
            {
                return entry;
            }

            // Drop stale entry so it doesn't sit in the cache occupying a slot.
            if (entry != null)
                _resultCache.Remove(cacheKey);

            return null;
        }
    }

    private static void StoreCachedResult(
        string cacheKey,
        IReadOnlyList<SearchResultItem> items,
        SearchResultItem? topResult,
        int chipTotal,
        bool chipHasMore)
    {
        lock (_resultCacheGate)
        {
            // Bound: when full, evict the single oldest entry. Cheap and good enough
            // — typical session has well under ResultCacheMax distinct queries.
            if (_resultCache.Count >= ResultCacheMax && !_resultCache.ContainsKey(cacheKey))
            {
                var oldestKey = string.Empty;
                var oldestAt = DateTimeOffset.MaxValue;
                foreach (var (k, v) in _resultCache)
                {
                    if (v.At < oldestAt) { oldestAt = v.At; oldestKey = k; }
                }
                if (oldestKey.Length > 0) _resultCache.Remove(oldestKey);
            }

            _resultCache[cacheKey] = new CachedResult(items, topResult, DateTimeOffset.UtcNow, chipTotal, chipHasMore);
        }
    }

    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not ITrackItem trackItem) return;
        _playbackStateService.PlayTrack(trackItem.Uri);
    }

    private bool CanRetry()
        => !IsLoading && !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
        => string.IsNullOrWhiteSpace(Query) ? Task.CompletedTask : LoadAsync(Query);
}
