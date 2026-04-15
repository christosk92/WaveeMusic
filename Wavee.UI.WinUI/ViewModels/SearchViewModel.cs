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
    Playlists
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
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ILogger? _logger;
    private readonly List<SearchResultItem> _allItems = [];
    private int _requestVersion;

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

    public SearchViewModel(
        IPathfinderClient pathfinderClient,
        IPlaybackStateService playbackStateService,
        ILogger<SearchViewModel>? logger = null)
    {
        _pathfinderClient = pathfinderClient;
        _playbackStateService = playbackStateService;
        _logger = logger;

        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, null)
        {
            Title = "Search"
        };
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

    public async Task LoadAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        var requestVersion = ++_requestVersion;

        Query = query;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        ShowEmptyState = false;

        try
        {
            var result = await Task.Run(() => _pathfinderClient.SearchAsync(
                query,
                GetSearchScope(),
                limit: 30));

            if (requestVersion != _requestVersion)
                return;

            _allItems.Clear();
            _allItems.AddRange(result.Items);
            DispatchResults(result.Items);
            TopResult = result.TopResult;
            UpdateVisibleResults();

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

        IEnumerable<SearchResultItem> filtered = SelectedFilter switch
        {
            SearchFilterType.Songs => _allItems.Where(static item => item.Type == SearchResultType.Track),
            SearchFilterType.Artists => _allItems.Where(static item => item.Type == SearchResultType.Artist),
            SearchFilterType.Albums => _allItems.Where(static item => item.Type == SearchResultType.Album),
            SearchFilterType.Playlists => _allItems.Where(static item => item.Type == SearchResultType.Playlist),
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

    private SearchScope GetSearchScope()
    {
        return SelectedFilter switch
        {
            SearchFilterType.Artists => SearchScope.Artists,
            _ => SearchScope.All
        };
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
