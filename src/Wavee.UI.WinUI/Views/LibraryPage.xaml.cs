using System;
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
    private static readonly Thickness DefaultContentPadding = new(24, 8, 24, 0);

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
    private bool _trimmedForNavigationCache;
    private string? _trimmedSelectedTabKey;

    public LibraryPage()
    {
        _shellViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        ViewModel = Ioc.Default.GetRequiredService<LibraryPageViewModel>();
        InitializeComponent();
        Loaded += LibraryPage_Loaded;
    }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// Sets the Segmented visual selection without firing SelectionChanged.
    /// Detaches the event handler to avoid re-entrancy deadlocks.
    /// </summary>
    private void SetSelectedItemSilently(SegmentedItem itemToSelect)
    {
        if (_disposed) return;

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
        if (_disposed) return;

        SegmentedItem itemToSelect = GetItemForTabKey(tabName);

        SetSelectedItemSilently(itemToSelect);
        ShowTab(itemToSelect);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_disposed) return;

        // On back/forward navigation, restore the cached page as-is
        if (e.NavigationMode is NavigationMode.Back or NavigationMode.Forward
            && ContentHost?.Content != null)
        {
            return;
        }

        var navigationParameter = UnwrapNavigationParameter(e.Parameter);
        if (_trimmedForNavigationCache && navigationParameter is not string)
        {
            RestoreFromNavigationCache();
            TryApplyPendingSleepState();
            return;
        }

        SegmentedItem itemToSelect = navigationParameter is string tab
            ? GetItemForTabKey(tab)
            : AlbumsItem;

        SetSelectedItemSilently(itemToSelect);
        _trimmedForNavigationCache = false;
        _trimmedSelectedTabKey = null;
        ShowTab(itemToSelect);
        TryApplyPendingSleepState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_disposed) return;

        TrimForNavigationCache();
    }

    private void SelectorBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed) return;

        // ContentHost.Content can be null after navigation-cache trimming. A selected
        // tab still needs to materialize its view when the page is reused.
        if (LibrarySelectorBar.SelectedItem is SegmentedItem selectedItem)
        {
            ShowTab(selectedItem);
        }
    }

    private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || ContentHost?.Content != null)
            return;

        var selectedItem = LibrarySelectorBar.SelectedItem as SegmentedItem
                           ?? GetItemForTabKey(_trimmedSelectedTabKey);
        SetSelectedItemSilently(selectedItem);
        ShowTab(selectedItem);
    }

    /// <summary>
    /// Lazily resolves the cached UserControl for the given tab and assigns it
    /// to the ContentControl. First access creates the view (and fires the
    /// sub-VM's LoadCommand via its constructor); subsequent accesses are a
    /// reference swap.
    /// </summary>
    private void ShowTab(SegmentedItem selectedItem)
    {
        if (_disposed) return;

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
            view = _yourEpisodesView ??= new YourEpisodesView(ViewModel.YourEpisodes);
        }
        else
        {
            view = _albumsView ??= new AlbumsLibraryView(ViewModel.Albums);
        }

        if (!ReferenceEquals(ContentHost.Content, view))
        {
            ContentHost.Content = view;
        }

        UpdateSidebarSelection(selectedItem);
        UpdateCurrentTabTitle(selectedItem);
        UpdateTabItemParameter(selectedItem);
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
        if (_disposed) return;

        parameter = UnwrapNavigationParameter(parameter);

        if (parameter is string tabName)
            SelectTab(tabName);
    }

    public object? CaptureSleepState()
        => _disposed ? null : new LibraryPageSleepState(GetSelectedTabKey());

    public void RestoreSleepState(object? state)
    {
        if (_disposed) return;

        _pendingSleepState = state as LibraryPageSleepState;
        TryApplyPendingSleepState();
    }

    public void TrimForNavigationCache()
    {
        if (_disposed)
            return;

        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _trimmedSelectedTabKey = GetSelectedTabKey();
        if (ContentHost != null)
            ContentHost.Content = null;
    }

    public void RestoreFromNavigationCache()
    {
        if (_disposed)
            return;

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

        Loaded -= LibraryPage_Loaded;
        LibrarySelectorBar.SelectionChanged -= SelectorBar_SelectionChanged;
        ContentHost.Content = null;

        DisposeIfNeeded(ref _albumsView);
        DisposeIfNeeded(ref _artistsView);
        DisposeIfNeeded(ref _likedSongsView);
        DisposeIfNeeded(ref _yourEpisodesView);

        ViewModel.Dispose();
        ContentChanged = null;
        _tabItemParameter = null;
        _pendingSleepState = null;
        _trimmedSelectedTabKey = null;
    }

    private void TryApplyPendingSleepState()
    {
        if (_disposed)
            return;

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
