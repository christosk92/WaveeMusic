using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public enum SearchFilterType
{
    All,
    Songs,
    Artists,
    Albums,
    Playlists
}

public sealed partial class SearchViewModel : ObservableObject, ITabBarItemContent
{
    private readonly IPathfinderClient _pathfinderClient;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ILogger? _logger;

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

    public ObservableCollection<SearchResultItem> Tracks { get; } = [];
    public ObservableCollection<ITrackItem> AdaptedTracks { get; } = [];
    public ObservableCollection<SearchResultItem> Artists { get; } = [];
    public ObservableCollection<SearchResultItem> Albums { get; } = [];
    public ObservableCollection<SearchResultItem> Playlists { get; } = [];

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
    }

    public async Task LoadAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        Query = query;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            var result = await Task.Run(() => _pathfinderClient.SearchAsync(query));

            DispatchResults(result.Items);
            TopResult = result.TopResult;

            TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, query)
            {
                Title = $"Search: {query}"
            };
            ContentChanged?.Invoke(this, TabItemParameter);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Search failed for query: {Query}", query);
            HasError = true;
            ErrorMessage = "Search failed. Please try again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void DispatchResults(IReadOnlyList<SearchResultItem> items)
    {
        Tracks.Clear();
        AdaptedTracks.Clear();
        Artists.Clear();
        Albums.Clear();
        Playlists.Clear();

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
    }

    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not ITrackItem trackItem) return;
        _playbackStateService.PlayTrack(trackItem.Uri);
    }
}
