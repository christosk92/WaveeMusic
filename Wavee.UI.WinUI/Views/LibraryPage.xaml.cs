using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    private int _currentTabIndex = 0;
    private readonly ShellViewModel _shellViewModel;
    private bool _suppressSelectionChanged;

    public LibraryPage()
    {
        _shellViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// Select a tab by name (used when already on LibraryPage and clicking sidebar)
    /// </summary>
    public void SelectTab(string tabName)
    {
        SelectorBarItem itemToSelect = tabName.ToLowerInvariant() switch
        {
            "artists" => ArtistsItem,
            "likedsongs" or "liked-songs" => LikedSongsItem,
            _ => AlbumsItem
        };

        var pageType = GetPageType(itemToSelect);
        if (ContentFrame.Content?.GetType() == pageType)
        {
            UpdateSidebarSelection(itemToSelect);
            return;
        }

        // Clear all and set the correct one
        _suppressSelectionChanged = true;
        try
        {
            AlbumsItem.IsSelected = false;
            ArtistsItem.IsSelected = false;
            LikedSongsItem.IsSelected = false;
            itemToSelect.IsSelected = true;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        // Update content and sidebar
        NavigateToContent(itemToSelect);
        UpdateSidebarSelection(itemToSelect);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // On back/forward navigation, restore the cached page as-is
        if (e.NavigationMode is NavigationMode.Back or NavigationMode.Forward
            && ContentFrame.Content != null)
        {
            return;
        }

        // Determine which item to select based on parameter
        SelectorBarItem itemToSelect = AlbumsItem; // default

        if (e.Parameter is string tab)
        {
            itemToSelect = tab.ToLowerInvariant() switch
            {
                "artists" => ArtistsItem,
                "likedsongs" or "liked-songs" => LikedSongsItem,
                _ => AlbumsItem
            };
        }

        // If already showing the requested sub-page, skip re-navigation
        var pageType = GetPageType(itemToSelect);
        if (ContentFrame.Content?.GetType() == pageType)
            return;

        // Clear all selections and set the correct one
        _suppressSelectionChanged = true;
        try
        {
            AlbumsItem.IsSelected = false;
            ArtistsItem.IsSelected = false;
            LikedSongsItem.IsSelected = false;
            itemToSelect.IsSelected = true;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        // Set initial tab index
        _currentTabIndex = GetTabIndex(itemToSelect);

        // Navigate without animation on initial load
        ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
    }

    private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
            return;

        var selectedItem = sender.SelectedItem;
        NavigateToContent(selectedItem);
        UpdateSidebarSelection(selectedItem);
    }

    private void NavigateToContent(SelectorBarItem? selectedItem = null)
    {
        selectedItem ??= LibrarySelectorBar.SelectedItem;
        if (selectedItem == null) return;

        int newIndex = GetTabIndex(selectedItem);

        // Skip if same tab
        if (newIndex == _currentTabIndex && ContentFrame.Content != null)
            return;

        // Determine direction for slide animation
        var effect = newIndex > _currentTabIndex
            ? SlideNavigationTransitionEffect.FromRight
            : SlideNavigationTransitionEffect.FromLeft;

        _currentTabIndex = newIndex;

        var pageType = GetPageType(selectedItem);
        ContentFrame.Navigate(pageType, null, new SlideNavigationTransitionInfo
        {
            Effect = effect
        });
    }

    private int GetTabIndex(SelectorBarItem item)
    {
        return item switch
        {
            _ when item == AlbumsItem => 0,
            _ when item == ArtistsItem => 1,
            _ when item == LikedSongsItem => 2,
            _ => 0
        };
    }

    private Type GetPageType(SelectorBarItem item)
    {
        return item switch
        {
            _ when item == AlbumsItem => typeof(AlbumsLibraryPage),
            _ when item == ArtistsItem => typeof(ArtistsLibraryPage),
            _ when item == LikedSongsItem => typeof(LikedSongsPage),
            _ => typeof(AlbumsLibraryPage)
        };
    }

    private void UpdateSidebarSelection(SelectorBarItem? selectedItem = null)
    {
        selectedItem ??= LibrarySelectorBar.SelectedItem;

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
