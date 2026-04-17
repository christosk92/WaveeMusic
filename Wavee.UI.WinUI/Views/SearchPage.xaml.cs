using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SearchPage : Page, ITabSleepParticipant
{
    public SearchViewModel ViewModel { get; }

    private readonly IColorService? _colorService;
    private readonly ILogger? _logger;
    private SearchPageSleepState? _pendingSleepState;
    private bool _sleepFilterRestoreRequested;

    // Bumped on every TopResult change so late color-fetch results can be dropped
    // (the user may have typed more while the previous fetch was in flight).
    private int _colorRequestVersion;

    public SearchPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SearchViewModel>();
        _colorService = Ioc.Default.GetService<IColorService>();
        _logger = Ioc.Default.GetService<ILogger<SearchPage>>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.TopResult))
        {
            _ = FetchAndApplyTopResultColorAsync(ViewModel.TopResult);
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

    private async System.Threading.Tasks.Task FetchAndApplyTopResultColorAsync(SearchResultItem? item)
    {
        var requestVersion = Interlocked.Increment(ref _colorRequestVersion);

        // Clear immediately so a stale color doesn't linger on top of the new hero.
        TopResultCard.ColorHex = null;

        if (item == null || _colorService == null || string.IsNullOrEmpty(item.ImageUrl))
            return;

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(item.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl)) return;

        try
        {
            var color = await _colorService.GetColorAsync(imageUrl);
            if (color == null) return;

            // Drop stale results — the user moved on to a new top result while we waited.
            if (requestVersion != _colorRequestVersion) return;

            var isDark = ActualTheme == ElementTheme.Dark;
            var hex = isDark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;

            if (!string.IsNullOrEmpty(hex))
                TopResultCard.ColorHex = hex;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to fetch top-result accent color for {Uri}", item.Uri);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            _ = ViewModel.LoadAsync(query);
        }

        UpdateFilterSelection();
        TryApplyPendingSleepState();
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
            () => PageScrollView.ScrollTo(0, state.VerticalOffset));
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
