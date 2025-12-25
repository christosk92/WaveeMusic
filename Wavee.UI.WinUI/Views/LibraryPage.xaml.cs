using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
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

        // Clear all and set the correct one
        AlbumsItem.IsSelected = false;
        ArtistsItem.IsSelected = false;
        LikedSongsItem.IsSelected = false;
        itemToSelect.IsSelected = true;

        // Update content and sidebar
        UpdateContent(itemToSelect);
        UpdateSidebarSelection(itemToSelect);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

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

        // Clear all selections and set the correct one
        AlbumsItem.IsSelected = false;
        ArtistsItem.IsSelected = false;
        LikedSongsItem.IsSelected = false;
        itemToSelect.IsSelected = true;

        // Update content with the selected item
        UpdateContent(itemToSelect);
    }

    private void SelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Use sender.SelectedItem which is guaranteed to be correct
        var selectedItem = sender.SelectedItem;
        UpdateContent(selectedItem);
        UpdateSidebarSelection(selectedItem);
    }

    private void UpdateContent(SelectorBarItem? selectedItem = null)
    {
        // Use passed item or fall back to SelectorBar.SelectedItem
        selectedItem ??= LibrarySelectorBar.SelectedItem;

        // Hide all content panels
        AlbumsContent.Visibility = Visibility.Collapsed;
        ArtistsContent.Visibility = Visibility.Collapsed;
        LikedSongsContent.Visibility = Visibility.Collapsed;

        // Show selected content
        if (selectedItem == AlbumsItem)
        {
            AlbumsContent.Visibility = Visibility.Visible;
        }
        else if (selectedItem == ArtistsItem)
        {
            ArtistsContent.Visibility = Visibility.Visible;
        }
        else if (selectedItem == LikedSongsItem)
        {
            LikedSongsContent.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSidebarSelection(SelectorBarItem? selectedItem = null)
    {
        // Use passed item or fall back to SelectorBar.SelectedItem
        selectedItem ??= LibrarySelectorBar.SelectedItem;

        var shellViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();

        // Find the matching sidebar item
        string? tag = selectedItem switch
        {
            _ when selectedItem == AlbumsItem => "Albums",
            _ when selectedItem == ArtistsItem => "Artists",
            _ when selectedItem == LikedSongsItem => "LikedSongs",
            _ => null
        };

        if (tag != null)
        {
            // Find the item in the sidebar with matching tag
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
