using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LibraryPage : Page, ITabBarItemContent, ITabSleepParticipant, INavigationCacheMemoryParticipant, IDisposable
{
    private const int MaxDeferredShowTabAttempts = 3;
    private const double DefaultExpandedPodcastTopInset = 52;
    private static readonly Thickness DefaultContentPadding = new(24, 8, 24, 0);
    private static readonly Thickness ExpandedPodcastContentPadding = new(0);

    private readonly ShellViewModel _shellViewModel;

    public LibraryPageViewModel ViewModel { get; }

    // Lazy-cached UserControl instances. Created the first time a tab is
    // selected, then kept alive for the lifetime of the LibraryPage.
    // Switching tabs is just a ContentControl.Content reference swap —
    // scroll position, selection, and filter state are all preserved.
    private AlbumsLibraryView? _albumsView;
    private ArtistsLibraryView? _artistsView;
    private LikedSongsView? _likedSongsView;
    private YourEpisodesView? _yourEpisodesView;
    private int _deferredShowTabAttempts;
    private TabItemParameter? _tabItemParameter;
    private bool _disposed;
    private LibraryPageSleepState? _pendingSleepState;
    private PodcastLibraryNavigationParameter? _pendingPodcastNavigation;
    private bool _trimmedForNavigationCache;
    private string? _trimmedSelectedTabKey;

    public LibraryPage()
    {
        _shellViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        ViewModel = Ioc.Default.GetRequiredService<LibraryPageViewModel>();
        InitializeComponent();
    }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// Sets the Segmented visual selection without firing SelectionChanged.
    /// Detaches the event handler to avoid re-entrancy deadlocks.
    /// </summary>
    private void SetSelectedItemSilently(SegmentedItem itemToSelect)
    {
        if (ReferenceEquals(LibrarySelectorBar.SelectedItem, itemToSelect)) return;

        LibrarySelectorBar.SelectionChanged -= SelectorBar_SelectionChanged;
        try
        {
            LibrarySelectorBar.SelectedItem = itemToSelect;
        }
        finally
        {
            LibrarySelectorBar.SelectionChanged += SelectorBar_SelectionChanged;
        }
    }

    /// <summary>
    /// Select a tab by name (used when already on LibraryPage and clicking sidebar)
    /// </summary>
    public void SelectTab(string tabName)
    {
        SegmentedItem itemToSelect = GetItemForTabKey(tabName);

        SetSelectedItemSilently(itemToSelect);
        ShowTab(itemToSelect);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // On back/forward navigation, restore the cached page as-is
        if (e.NavigationMode is NavigationMode.Back or NavigationMode.Forward
            && ContentHost?.Content != null)
        {
            return;
        }

        var navigationParameter = UnwrapNavigationParameter(e.Parameter);
        if (_trimmedForNavigationCache &&
            navigationParameter is not string &&
            navigationParameter is not PodcastLibraryNavigationParameter)
        {
            RestoreFromNavigationCache();
            TryApplyPendingSleepState();
            return;
        }

        // Determine which item to select based on parameter
        SegmentedItem itemToSelect = AlbumsItem; // default

        PodcastLibraryNavigationParameter? podcastNavigation = null;
        if (navigationParameter is PodcastLibraryNavigationParameter podcastParameter)
        {
            itemToSelect = YourEpisodesItem;
            podcastNavigation = podcastParameter;
        }
        else if (navigationParameter is string tab)
            itemToSelect = GetItemForTabKey(tab);

        SetSelectedItemSilently(itemToSelect);
        _trimmedForNavigationCache = false;
        _trimmedSelectedTabKey = null;
        ShowTab(itemToSelect);
        if (podcastNavigation is not null)
            ApplyPodcastNavigation(podcastNavigation);
        TryApplyPendingSleepState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        TrimForNavigationCache();
    }

    private void SelectorBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip any spurious initial event before OnNavigatedTo applies the navigation
        // is null until ShowTab runs for the first time — once OnNavigatedTo has wired
        // parameter. ContentHost.Content is null until ShowTab runs for the first time.
        if (ContentHost?.Content == null) return;

        if (LibrarySelectorBar.SelectedItem is SegmentedItem selectedItem)
        {
            ShowTab(selectedItem);
        }
    }

    /// <summary>
    /// Lazily resolves the cached UserControl for the given tab and assigns it
    /// to the ContentControl. First access creates the view (and fires the
    /// sub-VM's LoadCommand via its constructor); subsequent accesses are a
    /// reference swap.
    /// </summary>
    private void ShowTab(SegmentedItem selectedItem)
    {
        // In rare timing windows the generated x:Name field may still be null.
        // Defer to the UI queue and retry a few times instead of throwing.
        if (ContentHost == null)
        {
            if (_deferredShowTabAttempts < MaxDeferredShowTabAttempts)
            {
                _deferredShowTabAttempts++;
                DispatcherQueue.TryEnqueue(() => ShowTab(selectedItem));
            }

            return;
        }

        _deferredShowTabAttempts = 0;

        UserControl view;

        if (selectedItem == ArtistsItem)
        {
            view = _artistsView ??= new ArtistsLibraryView(ViewModel.Artists);
        }
        else if (selectedItem == LikedSongsItem)
        {
            view = _likedSongsView ??= new LikedSongsView(ViewModel.LikedSongs);
        }
        else if (selectedItem == YourEpisodesItem)
        {
            if (_yourEpisodesView is null)
            {
                _yourEpisodesView = new YourEpisodesView(ViewModel.YourEpisodes);
                _yourEpisodesView.ViewModel.PropertyChanged += YourEpisodesViewModel_PropertyChanged;
            }

            view = _yourEpisodesView;
        }
        else
        {
            view = _albumsView ??= new AlbumsLibraryView(ViewModel.Albums);
        }

        if (!ReferenceEquals(ContentHost.Content, view))
        {
            ContentHost.Content = view;
        }

        if (selectedItem == YourEpisodesItem && _pendingPodcastNavigation is not null)
        {
            var pending = _pendingPodcastNavigation;
            _pendingPodcastNavigation = null;
            ApplyPodcastNavigation(pending);
        }

        UpdateContentAreaPadding(selectedItem);
        UpdateSidebarSelection(selectedItem);
        UpdateCurrentTabTitle(selectedItem);
        UpdateTabItemParameter(selectedItem);
    }

    private void YourEpisodesViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(YourEpisodesViewModel.IsSelectedShowDetailMode)
            or nameof(YourEpisodesViewModel.IsSelectedEpisodeDetailPageMode))
        {
            UpdateContentAreaPadding(LibrarySelectorBar.SelectedItem as SegmentedItem);
        }
    }

    private void UpdateContentAreaPadding(SegmentedItem? selectedItem)
    {
        if (ContentArea is null)
            return;

        var expandedPodcastDetail = selectedItem == YourEpisodesItem &&
                                    _yourEpisodesView?.ViewModel is { } vm &&
                                    (vm.IsSelectedShowDetailMode || vm.IsSelectedEpisodeDetailPageMode);

        ContentArea.Padding = expandedPodcastDetail
            ? ExpandedPodcastContentPadding
            : DefaultContentPadding;

        Grid.SetRow(ContentArea, expandedPodcastDetail ? 0 : 1);
        Grid.SetRowSpan(ContentArea, expandedPodcastDetail ? 2 : 1);

        var chromeTopInset = expandedPodcastDetail
            ? Math.Max(LibrarySelectorBar.ActualHeight, DefaultExpandedPodcastTopInset)
            : 0;

        if (_yourEpisodesView is not null)
            _yourEpisodesView.SetExpandedChromeTopInset(chromeTopInset);
    }

    private static string GetLocalizedTabTitle(SegmentedItem selectedItem, SegmentedItem albumsItem, SegmentedItem artistsItem, SegmentedItem likedSongsItem, SegmentedItem yourEpisodesItem)
    {
        return selectedItem switch
        {
            _ when selectedItem == albumsItem => AppLocalization.GetString("Shell_SidebarAlbums"),
            _ when selectedItem == artistsItem => AppLocalization.GetString("Shell_SidebarArtists"),
            _ when selectedItem == likedSongsItem => AppLocalization.GetString("Shell_SidebarLikedSongs"),
            _ when selectedItem == yourEpisodesItem => AppLocalization.GetString("Shell_SidebarPodcasts"),
            _ => AppLocalization.GetString("Shell_SidebarYourLibrary")
        };
    }

    private void UpdateCurrentTabTitle(SegmentedItem selectedItem)
    {
        var tabIndex = App.AppModel.TabStripSelectedIndex;
        if (tabIndex < 0 || tabIndex >= ShellViewModel.TabInstances.Count)
        {
            return;
        }

        var title = GetLocalizedTabTitle(selectedItem, AlbumsItem, ArtistsItem, LikedSongsItem, YourEpisodesItem);
        var currentTab = ShellViewModel.TabInstances[tabIndex];
        currentTab.Header = title;
        currentTab.ToolTipText = title;
    }

    private void UpdateTabItemParameter(SegmentedItem selectedItem)
    {
        var tabKey = selectedItem == ArtistsItem
            ? "artists"
            : selectedItem == LikedSongsItem
                ? "likedsongs"
                : selectedItem == YourEpisodesItem
                    ? "podcasts"
                    : "albums";

        _tabItemParameter = new TabItemParameter
        {
            InitialPageType = typeof(LibraryPage),
            NavigationParameter = tabKey,
            Title = GetLocalizedTabTitle(selectedItem, AlbumsItem, ArtistsItem, LikedSongsItem, YourEpisodesItem),
            PageType = NavigationPageType.Library
        };

        ContentChanged?.Invoke(this, _tabItemParameter);
    }

    private void UpdateSidebarSelection(SegmentedItem? selectedItem = null)
    {
        selectedItem ??= LibrarySelectorBar.SelectedItem as SegmentedItem;
        if (selectedItem == null) return;

        var shellViewModel = _shellViewModel;

        string? tag = selectedItem switch
        {
            _ when selectedItem == AlbumsItem => "Albums",
            _ when selectedItem == ArtistsItem => "Artists",
            _ when selectedItem == LikedSongsItem => "LikedSongs",
            _ when selectedItem == YourEpisodesItem => "Podcasts",
            _ => null
        };

        if (tag != null)
        {
            foreach (var item in shellViewModel.SidebarItems)
            {
                if (item.Children is System.Collections.IEnumerable children)
                {
                    foreach (var child in children)
                    {
                        if (child is Controls.Sidebar.SidebarItemModel sidebarChild && sidebarChild.Tag == tag)
                        {
                            shellViewModel.SelectedSidebarItem = sidebarChild;
                            return;
                        }
                    }
                }
                if (item.Tag as string == tag)
                {
                    shellViewModel.SelectedSidebarItem = item;
                    return;
                }
            }
        }
    }

    public void RefreshWithParameter(object? parameter)
    {
        parameter = UnwrapNavigationParameter(parameter);

        if (parameter is PodcastLibraryNavigationParameter podcastParameter)
        {
            SetSelectedItemSilently(YourEpisodesItem);
            ShowTab(YourEpisodesItem);
            ApplyPodcastNavigation(podcastParameter);
        }
        else if (parameter is string tabName)
        {
            SelectTab(tabName);
        }
    }

    private void ApplyPodcastNavigation(PodcastLibraryNavigationParameter parameter)
    {
        if (_yourEpisodesView is null)
        {
            _pendingPodcastNavigation = parameter;
            return;
        }

        _ = _yourEpisodesView.ViewModel.NavigateToAsync(parameter);
    }

    public object? CaptureSleepState()
        => new LibraryPageSleepState(GetSelectedTabKey());

    public void RestoreSleepState(object? state)
    {
        _pendingSleepState = state as LibraryPageSleepState;
        TryApplyPendingSleepState();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _trimmedSelectedTabKey = GetSelectedTabKey();
        if (ContentHost != null)
            ContentHost.Content = null;
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache)
            return;

        var tabKey = _trimmedSelectedTabKey ?? GetSelectedTabKey();
        _trimmedForNavigationCache = false;
        _trimmedSelectedTabKey = null;
        SelectTab(tabKey);
        TryApplyPendingSleepState();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        LibrarySelectorBar.SelectionChanged -= SelectorBar_SelectionChanged;
        if (_yourEpisodesView is not null)
            _yourEpisodesView.ViewModel.PropertyChanged -= YourEpisodesViewModel_PropertyChanged;
        ContentHost.Content = null;

        DisposeIfNeeded(ref _albumsView);
        DisposeIfNeeded(ref _artistsView);
        DisposeIfNeeded(ref _likedSongsView);
        DisposeIfNeeded(ref _yourEpisodesView);

        ViewModel.Dispose();
        ContentChanged = null;
        _tabItemParameter = null;
    }

    private void TryApplyPendingSleepState()
    {
        if (_pendingSleepState == null || !IsLoaded)
            return;

        var state = _pendingSleepState;
        _pendingSleepState = null;

        if (!string.IsNullOrWhiteSpace(state.SelectedTabKey))
            SelectTab(state.SelectedTabKey);
    }

    private string GetSelectedTabKey()
    {
        var selectedItem = LibrarySelectorBar.SelectedItem as SegmentedItem;
        if (selectedItem == ArtistsItem)
            return "artists";

        if (selectedItem == LikedSongsItem)
            return "likedsongs";

        if (selectedItem == YourEpisodesItem)
            return "podcasts";

        return "albums";
    }

    private SegmentedItem GetItemForTabKey(string? tabName)
    {
        return tabName?.Trim().ToLowerInvariant() switch
        {
            "artists" => ArtistsItem,
            "likedsongs" or "liked-songs" => LikedSongsItem,
            "podcasts" or "episodes" or "yourepisodes" or "your-episodes" => YourEpisodesItem,
            _ => AlbumsItem
        };
    }

    private static object? UnwrapNavigationParameter(object? parameter)
    {
        while (parameter is TabItemParameter tabParameter)
            parameter = tabParameter.NavigationParameter;

        return parameter;
    }

    private static void DisposeIfNeeded<T>(ref T? value)
        where T : class
    {
        if (value is IDisposable disposable)
            disposable.Dispose();

        value = null;
    }

    private sealed record LibraryPageSleepState(string SelectedTabKey);
}
