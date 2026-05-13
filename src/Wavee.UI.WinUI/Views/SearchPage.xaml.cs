using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Controls.Search;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
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
    private bool _scrollHandlerAttached;
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
        AttachLoadMoreSentinel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModelEvents();
        DetachLoadMoreSentinel();
    }

    private void AttachLoadMoreSentinel()
    {
        if (_scrollHandlerAttached || LoadMoreSentinel == null) return;
        // EffectiveViewportChanged fires only when the sentinel's intersection with an
        // ancestor scroller changes — not every scroll frame. As we append items the
        // sentinel pushes further down, so it stays silent until the user scrolls back
        // near it. This avoids the layout-feedback loop a raw ScrollView.ViewChanged
        // handler would create.
        LoadMoreSentinel.EffectiveViewportChanged += OnLoadMoreSentinelViewportChanged;
        _scrollHandlerAttached = true;
    }

    private void DetachLoadMoreSentinel()
    {
        if (!_scrollHandlerAttached || LoadMoreSentinel == null) return;
        LoadMoreSentinel.EffectiveViewportChanged -= OnLoadMoreSentinelViewportChanged;
        _scrollHandlerAttached = false;
    }

    private void OnLoadMoreSentinelViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        // Fire only when the sentinel actually intersects (or is within ~300px of) the
        // effective viewport. ViewModel.LoadMoreAsync short-circuits if a fetch is in
        // flight or the chip has nothing more to fetch.
        if (!ViewModel.CanLoadMore) return;

        // EffectiveViewport reports MinValue when the element isn't realised at all —
        // ignore that, it's not "at the bottom", it's "off-screen entirely".
        var ev = args.EffectiveViewport;
        if (ev.Y == double.MinValue || ev.Height == double.MinValue) return;

        // BringIntoViewDistance{X,Y} is the distance the element would need to scroll
        // to enter the viewport. <= 300 means "near the bottom".
        if (args.BringIntoViewDistanceY <= 300)
            _ = ViewModel.LoadMoreAsync();
    }
    

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachViewModelEvents();
        DetachLoadMoreSentinel();
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
        LoadSearchParameter(e.Parameter);
    }

    /// <summary>
    /// Same-tab search→search navigation reuses this Page instance and never
    /// fires <see cref="OnNavigatedTo"/> — TabBarItem.Navigate routes through
    /// this method instead. Without it, typing a new query from the sidebar
    /// while SearchPage is the active tab content silently drops the request.
    /// </summary>
    public void RefreshWithParameter(object? parameter)
        => LoadSearchParameter(parameter);

    private void LoadSearchParameter(object? parameter)
    {
        if (parameter is string query && !string.IsNullOrWhiteSpace(query))
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
                "Podcasts" => SearchFilterType.Podcasts,
                "Users" => SearchFilterType.Users,
                "Genres" => SearchFilterType.Genres,
                _ => SearchFilterType.All
            };
        }
    }

    private void TopResult_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ExecuteResult(ViewModel.TopResult);
    }

    private void TopResult_RightTapped(SearchResultHeroCard sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel.TopResult is not { } item)
            return;

        ShowSearchResultContextMenu(sender, item, e.GetPosition(sender));
        e.Handled = true;
    }

    private void ResultRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is SearchResultRowCard card && card.Item is { } item)
            ExecuteResult(item);
    }

    private void ResultRow_RightTapped(SearchResultRowCard sender, RightTappedRoutedEventArgs e)
    {
        if (sender.Item is not { } item)
            return;

        ShowSearchResultContextMenu(sender, item, e.GetPosition(sender));
        e.Handled = true;
    }

    private void SectionCard_RightTapped(ContentCard sender, RightTappedRoutedEventArgs e)
    {
        if (sender.Tag is not SearchResultItem item)
            return;

        ShowSearchResultContextMenu(sender, item, e.GetPosition(sender));
        e.Handled = true;
    }

    private void ExecuteResult(SearchResultItem? item)
    {
        if (item == null)
            return;

        switch (item.Type)
        {
            case SearchResultType.Track:
                ViewModel.PlayTrackCommand.Execute(GetTrackAdapter(item));
                break;
            default:
                OpenResult(item, NavigationHelpers.IsCtrlPressed());
                break;
        }
    }

    private void ShowSearchResultContextMenu(FrameworkElement target, SearchResultItem item, Point position)
    {
        var items = BuildSearchResultMenu(item);
        if (items.Count == 0)
            return;

        ContextMenuHost.Show(target, items, position);
    }

    private IReadOnlyList<ContextMenuItemModel> BuildSearchResultMenu(SearchResultItem item)
    {
        if (item.Type == SearchResultType.Track)
        {
            var track = GetTrackAdapter(item);
            return TrackContextMenuBuilder.Build(track, new TrackMenuContext
            {
                PlayCommand = ViewModel.PlayTrackCommand,
                ExtraItems =
                [
                    ContextMenuItemModel.Separator,
                    CreateShareMenuItem(item)
                ]
            });
        }

        var items = new List<ContextMenuItemModel>();
        if (CanOpenResult(item))
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("CardMenu_Open"),
                Glyph = FluentGlyphs.Open,
                IsPrimary = true,
                Invoke = () => OpenResult(item, openInNewTab: false)
            });
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
                Glyph = FluentGlyphs.OpenInNewTab,
                IsPrimary = true,
                Invoke = () => OpenResult(item, openInNewTab: true)
            });
        }

        if (item.Type == SearchResultType.Episode)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_Play"),
                Glyph = FluentGlyphs.Play,
                AccentIconStyleKey = "App.AccentIcons.Media.Play",
                IsPrimary = true,
                Invoke = () => NavigationHelpers.PlayEpisode(item.Uri)
            });
        }

        if (items.Count > 0)
            items.Add(ContextMenuItemModel.Separator);

        items.Add(CreateShareMenuItem(item));
        return items;
    }

    private ITrackItem GetTrackAdapter(SearchResultItem item)
        => ViewModel.AdaptedTracks.FirstOrDefault(t => t.Uri == item.Uri)
           ?? new SearchTrackAdapter(item);

    private static ContextMenuItemModel CreateShareMenuItem(SearchResultItem item)
        => new()
        {
            Text = AppLocalization.GetString("CardMenu_Share"),
            Glyph = FluentGlyphs.Share,
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            Invoke = () => CopyShareLink(item)
        };

    private static bool CanOpenResult(SearchResultItem item)
        => item.Type is SearchResultType.Artist
            or SearchResultType.Album
            or SearchResultType.Playlist
            or SearchResultType.Podcast
            or SearchResultType.Episode
            or SearchResultType.User
            or SearchResultType.Genre;

    private static void OpenResult(SearchResultItem item, bool openInNewTab)
    {
        var parameter = ToNavigationParameter(item);

        switch (item.Type)
        {
            case SearchResultType.Artist:
                NavigationHelpers.OpenArtist(parameter, item.Name, openInNewTab);
                break;
            case SearchResultType.Album:
                NavigationHelpers.OpenAlbum(parameter, item.Name, openInNewTab);
                break;
            case SearchResultType.Playlist:
                NavigationHelpers.OpenPlaylist(parameter, item.Name, openInNewTab);
                break;
            case SearchResultType.Podcast:
                NavigationHelpers.OpenShowPage(parameter, openInNewTab);
                break;
            case SearchResultType.Episode:
                NavigationHelpers.OpenEpisodePage(
                    item.Uri,
                    item.Name,
                    item.ImageUrl,
                    item.ParentUri,
                    item.ParentName,
                    openInNewTab: openInNewTab);
                break;
            case SearchResultType.User:
                NavigationHelpers.OpenProfile(parameter, item.Name, openInNewTab);
                break;
            case SearchResultType.Genre:
                NavigationHelpers.OpenBrowsePage(parameter, openInNewTab);
                break;
        }
    }

    private static ContentNavigationParameter ToNavigationParameter(SearchResultItem item)
        => new()
        {
            Uri = item.Uri,
            Title = item.Name,
            Subtitle = item.DisplaySubtitle,
            ImageUrl = item.ImageUrl
        };

    private static void CopyShareLink(SearchResultItem item)
    {
        var text = ToOpenSpotifyUrl(item.Uri) ?? item.Uri;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string? ToOpenSpotifyUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "spotify", StringComparison.OrdinalIgnoreCase))
            return uri;

        var kind = parts[1].ToLowerInvariant();
        var id = parts[2];
        var pathKind = kind switch
        {
            "track" or "artist" or "album" or "playlist" or "show" or "episode" or "user" or "genre" => kind,
            "page" or "section" => "genre",
            _ => null
        };

        return pathKind is null
            ? uri
            : $"https://open.spotify.com/{pathKind}/{Uri.EscapeDataString(id)}";
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
        FilterPodcasts.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Podcasts;
        FilterUsers.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Users;
        FilterGenres.IsChecked = ViewModel.SelectedFilter == SearchFilterType.Genres;
    }

    private sealed record SearchPageSleepState(
        string? Query,
        SearchFilterType SelectedFilter,
        double VerticalOffset);
}
