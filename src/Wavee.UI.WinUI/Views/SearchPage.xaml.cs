using System;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Controls.Search;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SearchPage : Page, ITabSleepParticipant, INavigationCacheMemoryParticipant, IDisposable
{
    public SearchViewModel ViewModel { get; }

    private readonly ILogger? _logger;
    private SearchPageSleepState? _pendingSleepState;
    private bool _sleepFilterRestoreRequested;
    private bool _trimmedForNavigationCache;
    private bool _viewModelEventsAttached;
    private bool _isDisposed;

    public SearchPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SearchViewModel>();
        _logger = Ioc.Default.GetService<ILogger<SearchPage>>();
        InitializeComponent();

        AttachViewModelEvents();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelEvents();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModelEvents();
    }
    

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachViewModelEvents();
    }

    private void AttachViewModelEvents()
    {
        if (_viewModelEventsAttached)
            return;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModelEventsAttached = true;
    }

    private void DetachViewModelEvents()
    {
        if (!_viewModelEventsAttached)
            return;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModelEventsAttached = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.TopResult))
        {
            // No host-driven solid-colour background anymore. For artists the hero
            // card fetches its own header image via ArtistStore; for everything
            // else we deliberately fall back to the default card chrome.
            TopResultCard.ColorHex = null;
        }

        if (e.PropertyName == nameof(SearchViewModel.SelectedFilter))
        {
            UpdateFilterSelection();
        }

        if (e.PropertyName == nameof(SearchViewModel.IsLoading) && !ViewModel.IsLoading)
        {
            TryApplyPendingSleepState();
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            _trimmedForNavigationCache = false;
            _ = ViewModel.LoadAsync(query);
        }
        else
        {
            RestoreFromNavigationCache();
        }

        UpdateFilterSelection();
        TryApplyPendingSleepState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        TrimForNavigationCache();
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.SelectedFilter = tag switch
            {
                "Songs" => SearchFilterType.Songs,
                "Artists" => SearchFilterType.Artists,
                "Albums" => SearchFilterType.Albums,
                "Playlists" => SearchFilterType.Playlists,
                _ => SearchFilterType.All
            };
        }
    }

    private void TopResult_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ExecuteResult(ViewModel.TopResult);
    }

    private void ResultRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is SearchResultRowCard card && card.Item is { } item)
            ExecuteResult(item);
    }

    private void ExecuteResult(SearchResultItem? item)
    {
        if (item == null)
            return;

        switch (item.Type)
        {
            case SearchResultType.Track:
                var adapted = ViewModel.AdaptedTracks.FirstOrDefault(t => t.Uri == item.Uri)
                              ?? new SearchTrackAdapter(item);
                ViewModel.PlayTrackCommand.Execute(adapted);
                break;
            case SearchResultType.Artist:
                NavigationHelpers.OpenArtist(item.Uri, item.Name);
                break;
            case SearchResultType.Album:
                NavigationHelpers.OpenAlbum(item.Uri, item.Name);
                break;
            case SearchResultType.Playlist:
                NavigationHelpers.OpenPlaylist(item.Uri, item.Name);
                break;
        }
    }

    public object? CaptureSleepState()
        => new SearchPageSleepState(
            ViewModel.Query,
            ViewModel.SelectedFilter,
            PageScrollView?.VerticalOffset ?? 0);

    public void RestoreSleepState(object? state)
    {
        _pendingSleepState = state as SearchPageSleepState;
        _sleepFilterRestoreRequested = false;
        TryApplyPendingSleepState();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        ViewModel.Hibernate();
        TopResultCard.ColorHex = null;
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling is no longer rooted by the (singleton-store-subscribed) VM —
        // without this the entire page tree is pinned across navigations.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        Bindings?.Update();
        _ = ViewModel.ResumeFromHibernateAsync();
    }

    private void TryApplyPendingSleepState()
    {
        if (_pendingSleepState == null || ViewModel.IsLoading || PageScrollView == null)
            return;

        var state = _pendingSleepState;

        if (!string.IsNullOrWhiteSpace(state.Query)
            && !string.Equals(ViewModel.Query, state.Query, StringComparison.Ordinal))
        {
            _ = ViewModel.LoadAsync(state.Query);
            return;
        }

        if (!_sleepFilterRestoreRequested && ViewModel.SelectedFilter != state.SelectedFilter)
        {
            _sleepFilterRestoreRequested = true;
            ViewModel.SelectedFilter = state.SelectedFilter;
            return;
        }

        _pendingSleepState = null;
        _sleepFilterRestoreRequested = false;

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => PageScrollView.ScrollToImmediate(0, state.VerticalOffset));
    }

    private void UpdateFilterSelection()
    {
        if (FilterAll == null)
            return;

        FilterAll.IsChecked = ViewModel.SelectedFilter == SearchFilterType.All;
        FilterSongs.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Songs;
        FilterArtists.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Artists;
        FilterAlbums.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Albums;
        FilterPlaylists.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Playlists;
    }

    private sealed record SearchPageSleepState(
        string? Query,
        SearchFilterType SelectedFilter,
        double VerticalOffset);
}
