using System;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
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

public sealed partial class SearchViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private readonly IPathfinderClient _pathfinderClient;
    private readonly ILogger? _logger;
    private readonly SourceCache<SearchResultItem, string> _resultsSource = new(r => r.Uri);
    private readonly CompositeDisposable _disposables = new();

    // Per-section output collections
    private readonly ReadOnlyObservableCollection<SearchResultItem> _tracks;
    private readonly ReadOnlyObservableCollection<SearchResultItem> _artists;
    private readonly ReadOnlyObservableCollection<SearchResultItem> _albums;
    private readonly ReadOnlyObservableCollection<SearchResultItem> _playlists;

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

    public ReadOnlyObservableCollection<SearchResultItem> Tracks => _tracks;
    public ReadOnlyObservableCollection<SearchResultItem> Artists => _artists;
    public ReadOnlyObservableCollection<SearchResultItem> Albums => _albums;
    public ReadOnlyObservableCollection<SearchResultItem> Playlists => _playlists;

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    public SearchViewModel(
        IPathfinderClient pathfinderClient,
        ILogger<SearchViewModel>? logger = null)
    {
        _pathfinderClient = pathfinderClient;
        _logger = logger;

        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Search, null)
        {
            Title = "Search"
        };

        // ── Reactive pipelines: one SourceCache → four filtered output collections ──

        _resultsSource.Connect()
            .Filter(r => r.Type == SearchResultType.Track)
            .Bind(out _tracks)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_disposables);

        _resultsSource.Connect()
            .Filter(r => r.Type == SearchResultType.Artist)
            .Bind(out _artists)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_disposables);

        _resultsSource.Connect()
            .Filter(r => r.Type == SearchResultType.Album)
            .Bind(out _albums)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_disposables);

        _resultsSource.Connect()
            .Filter(r => r.Type == SearchResultType.Playlist)
            .Bind(out _playlists)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_disposables);

        // ── Filter chip → section visibility ──

        this.WhenAnyValue(x => x.SelectedFilter)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdateSectionVisibility)
            .DisposeWith(_disposables);
    }

    private void UpdateSectionVisibility(SearchFilterType filter)
    {
        ShowTopResult = filter == SearchFilterType.All;
        ShowTracks = filter is SearchFilterType.All or SearchFilterType.Songs;
        ShowArtists = filter is SearchFilterType.All or SearchFilterType.Artists;
        ShowAlbums = filter is SearchFilterType.All or SearchFilterType.Albums;
        ShowPlaylists = filter is SearchFilterType.All or SearchFilterType.Playlists;
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
            var result = await _pathfinderClient.SearchAsync(query);

            // Batch update: AddOrUpdate preserves existing items, new ones animate in
            _resultsSource.Edit(cache =>
            {
                cache.Clear();
                foreach (var item in result.Items)
                {
                    cache.AddOrUpdate(item);
                }
            });

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

    public void Dispose()
    {
        _disposables.Dispose();
        _resultsSource.Dispose();
    }
}
