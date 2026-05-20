using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LibraryPage : UserControl, ITabBarItemContent, ITabSleepParticipant, INavigationCacheMemoryParticipant, IPageHostAware, IDisposable, IInPageFilterable
{
    // ── IInPageFilterable ───────────────────────────────────────────────
    // LibraryPage hosts four child views (Albums/Artists/LikedSongs/YourEpisodes)
    // via a ContentControl. Ctrl+F routes through the currently active child.
    private IInPageFilterable? ActiveFilterableChild
        => ContentHost?.Content as IInPageFilterable;
    string IInPageFilterable.FilterQuery
    {
        get => ActiveFilterableChild?.FilterQuery ?? string.Empty;
        set { if (ActiveFilterableChild is { } c) c.FilterQuery = value ?? string.Empty; }
    }
    string IInPageFilterable.FilterPlaceholder
        => ActiveFilterableChild?.FilterPlaceholder ?? "Filter…";
    bool IInPageFilterable.CanFilter
        => ActiveFilterableChild?.CanFilter ?? false;
    void IInPageFilterable.OnFilterClosed()
        => ActiveFilterableChild?.OnFilterClosed();

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
    private int _deferredShowTabGeneration;

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
        ShowTab(itemToSelect, deferColdCreation: false);
    }

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        if (_disposed) return;

        var navigationParameter = UnwrapNavigationParameter(parameter);

        // Back/forward navigation needs to honour the parameter — the
        // Segmented bar pushes tab changes into the PageHost back stack
        // (see SelectorBar_SelectionChanged), so Back to Library/"albums"
        // from Library/"artists" must actually re-select Albums.
        //
        // SetSelectedItemSilently and ShowTab both no-op when the target
        // tab is already showing, so falling through to the standard path
        // is safe for same-tab back/forward navs too.
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
        ShowTab(itemToSelect, deferColdCreation: true);
        TryApplyPendingSleepState();
    }

    public void OnLeaving()
    {
        if (_disposed) return;
        // Trim deferred ~1 s by TabBarItem; calling sync here moves the cost
        // off pageHostNavigating only to land in onLeaving instead.
    }

    private void SelectorBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed) return;
        if (LibrarySelectorBar.SelectedItem is not SegmentedItem selectedItem) return;

        // Route tab-bar clicks through the same NavigationHelpers entry
        // points the sidebar uses. This means:
        //   • Tab switch enters the outer Frame's back stack — Back returns
        //     to the previous tab instead of leaving Library entirely.
        //   • The tab strip header / icon / tooltip updates to match the
        //     new section (NavigateInCurrentTab does this).
        //   • ShellViewModel.UpdateNavigationState fires post-nav so the
        //     toolbar Back button enables correctly.
        //   • NavigationCacheMode=Enabled on LibraryPage means the same
        //     instance is reused — OnNavigatedTo reads the tab-key
        //     parameter and ShowTab swaps the cached UserControl in place.
        //
        // SelectionChanged only fires on an actual selection change, so
        // we won't double-push the same tab key.
        if (selectedItem == ArtistsItem)
            Helpers.Navigation.NavigationHelpers.OpenArtists();
        else if (selectedItem == LikedSongsItem)
            Helpers.Navigation.NavigationHelpers.OpenLikedSongs();
        else if (selectedItem == YourEpisodesItem)
            Helpers.Navigation.NavigationHelpers.OpenPodcasts();
        else
            Helpers.Navigation.NavigationHelpers.OpenAlbums();
    }

    private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || ContentHost?.Content != null)
            return;

        var selectedItem = LibrarySelectorBar.SelectedItem as SegmentedItem
                           ?? GetItemForTabKey(_trimmedSelectedTabKey);
        SetSelectedItemSilently(selectedItem);
        ShowTab(selectedItem, deferColdCreation: true);
    }

    /// <summary>
    /// Lazily resolves the cached UserControl for the given tab and assigns it
    /// to the ContentControl. First access creates the view (and fires the
    /// sub-VM's LoadCommand via its constructor); subsequent accesses are a
    /// reference swap.
    /// </summary>
    private void ShowTab(SegmentedItem selectedItem, bool deferColdCreation)
    {
        if (_disposed) return;

        // In rare timing windows the generated x:Name field may still be null.
        // Defer to the UI queue and retry a few times instead of throwing.
        if (ContentHost == null)
        {
            if (_deferredShowTabAttempts < MaxDeferredShowTabAttempts)
            {
                _deferredShowTabAttempts++;
                DispatcherQueue.TryEnqueue(() => ShowTab(selectedItem, deferColdCreation));
            }

            return;
        }

        _deferredShowTabAttempts = 0;
        UpdateSidebarSelection(selectedItem);
        UpdateCurrentTabTitle(selectedItem);
        UpdateTabItemParameter(selectedItem);

        if (deferColdCreation && !HasCachedViewFor(selectedItem))
        {
            var generation = ++_deferredShowTabGeneration;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_disposed || generation != _deferredShowTabGeneration)
                    return;

                ShowTab(selectedItem, deferColdCreation: false);
            });
            return;
        }

        _deferredShowTabGeneration++;

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
            // The active filterable child changed under LibraryPage —
            // close the Ctrl+F filter bar (if open) so the user starts
            // fresh on the new sub-tab. The bar can be re-opened with
            // another Ctrl+F press and will target the new child.
            Ioc.Default.GetService<Services.InPageFilterController>()?.Hide();
        }
    }

    private bool HasCachedViewFor(SegmentedItem selectedItem)
    {
        if (selectedItem == ArtistsItem)
            return _artistsView is not null;
        if (selectedItem == LikedSongsItem)
            return _likedSongsView is not null;
        if (selectedItem == YourEpisodesItem)
            return _yourEpisodesView is not null;
        return _albumsView is not null;
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

        if (tag == null) return;

        // If the user navigated here from a Pinned-section row whose tag is the
        // pseudo-URI for the same destination (e.g. spotify:collection for Liked
        // Songs), keep that row selected. Without this guard, the Your-Library
        // canonical row below grabs the highlight back from the pinned row.
        var currentTag = (shellViewModel.Sidebar.SelectedSidebarItem as Controls.Sidebar.SidebarItemModel)?.Tag;
        if (currentTag is not null && IsEquivalentSidebarTag(tag, currentTag))
            return;

        foreach (var item in shellViewModel.Sidebar.SidebarItems)
        {
            if (item.Children is System.Collections.IEnumerable children)
            {
                foreach (var child in children)
                {
                    if (child is Controls.Sidebar.SidebarItemModel sidebarChild && sidebarChild.Tag == tag)
                    {
                        shellViewModel.Sidebar.SelectedSidebarItem = sidebarChild;
                        return;
                    }
                }
            }
            if (item.Tag as string == tag)
            {
                shellViewModel.Sidebar.SelectedSidebarItem = item;
                return;
            }
        }
    }

    private static bool IsEquivalentSidebarTag(string canonicalTag, string currentTag)
    {
        if (string.Equals(canonicalTag, currentTag, System.StringComparison.Ordinal))
            return true;

        return canonicalTag switch
        {
            "LikedSongs" =>
                currentTag == "spotify:collection"
                || (currentTag.StartsWith("spotify:user:", System.StringComparison.Ordinal)
                    && currentTag.EndsWith(":collection", System.StringComparison.Ordinal)),
            "Podcasts" =>
                currentTag == "spotify:collection:your-episodes",
            _ => false
        };
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
