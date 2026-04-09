using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    private const int MaxDeferredShowTabAttempts = 3;

    private readonly ShellViewModel _shellViewModel;

    public LibraryPageViewModel ViewModel { get; }

    // Lazy-cached UserControl instances. Created the first time a tab is
    // selected, then kept alive for the lifetime of the LibraryPage.
    // Switching tabs is just a ContentControl.Content reference swap —
    // scroll position, selection, and filter state are all preserved.
    private AlbumsLibraryView? _albumsView;
    private ArtistsLibraryView? _artistsView;
    private LikedSongsView? _likedSongsView;
    private int _deferredShowTabAttempts;

    public LibraryPage()
    {
        _shellViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        ViewModel = Ioc.Default.GetRequiredService<LibraryPageViewModel>();
        InitializeComponent();
    }

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
        SegmentedItem itemToSelect = tabName.ToLowerInvariant() switch
        {
            "artists" => ArtistsItem,
            "likedsongs" or "liked-songs" => LikedSongsItem,
            _ => AlbumsItem
        };

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

        // Determine which item to select based on parameter
        SegmentedItem itemToSelect = AlbumsItem; // default

        if (e.Parameter is string tab)
        {
            itemToSelect = tab.ToLowerInvariant() switch
            {
                "artists" => ArtistsItem,
                "likedsongs" or "liked-songs" => LikedSongsItem,
                _ => AlbumsItem
            };
        }

        SetSelectedItemSilently(itemToSelect);
        ShowTab(itemToSelect);
    }

    private void SelectorBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
        else
        {
            view = _albumsView ??= new AlbumsLibraryView(ViewModel.Albums);
        }

        if (!ReferenceEquals(ContentHost.Content, view))
        {
            ContentHost.Content = view;
        }

        UpdateSidebarSelection(selectedItem);
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
}
