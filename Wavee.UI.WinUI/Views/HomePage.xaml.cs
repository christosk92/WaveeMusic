using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class HomePage : Page, ITabBarItemContent
{
    public HomeViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<HomeViewModel>();
        InitializeComponent();

        Loaded += HomePage_Loaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Handle navigation parameter if needed
        if (e.Parameter is string query && !string.IsNullOrEmpty(query))
        {
            // Could be search query or library navigation
        }
    }

    private void SectionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is HomeSectionItem item)
        {
            var openInNewTab = NavigationHelpers.IsCtrlPressed();

            // Find the image element for connected animation
            var imageElement = FindImageElement(btn);
            if (imageElement != null)
            {
                var animationKey = item.Type?.ToLowerInvariant() switch
                {
                    "artist" => ConnectedAnimationHelper.ArtistImage,
                    "album" => ConnectedAnimationHelper.AlbumArt,
                    "playlist" => ConnectedAnimationHelper.PlaylistArt,
                    _ => null
                };

                if (animationKey != null)
                {
                    ConnectedAnimationHelper.PrepareAnimation(animationKey, imageElement);
                }
            }

            switch (item.Type?.ToLowerInvariant())
            {
                case "artist":
                    NavigationHelpers.OpenArtist(item.Id ?? "", item.Title ?? "Artist", openInNewTab);
                    break;
                case "album":
                    NavigationHelpers.OpenAlbum(item.Id ?? "", item.Title ?? "Album", openInNewTab);
                    break;
                case "playlist":
                    NavigationHelpers.OpenPlaylist(item.Id ?? "", item.Title ?? "Playlist", openInNewTab);
                    break;
            }
        }
    }

    /// <summary>
    /// Find the image/card element within a button for connected animation
    /// </summary>
    private static UIElement? FindImageElement(Button btn)
    {
        // The button content is a StackPanel with a Grid (image placeholder) as first child
        if (btn.Content is StackPanel stackPanel && stackPanel.Children.Count > 0)
        {
            return stackPanel.Children[0]; // The Grid that acts as image placeholder
        }
        return null;
    }

    private void SectionItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Middle-click to open in new tab
        if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
            return;

        if (sender is Button btn && btn.DataContext is HomeSectionItem item)
        {
            switch (item.Type?.ToLowerInvariant())
            {
                case "artist":
                    NavigationHelpers.OpenArtist(item.Id ?? "", item.Title ?? "Artist", openInNewTab: true);
                    break;
                case "album":
                    NavigationHelpers.OpenAlbum(item.Id ?? "", item.Title ?? "Album", openInNewTab: true);
                    break;
                case "playlist":
                    NavigationHelpers.OpenPlaylist(item.Id ?? "", item.Title ?? "Playlist", openInNewTab: true);
                    break;
            }
        }
    }

    private void SectionItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is HomeSectionItem item)
        {
            // Show context menu with "Open in new tab" option
            var menu = new MenuFlyout();

            var openInNewTabItem = new MenuFlyoutItem
            {
                Text = "Open in new tab",
                Icon = new SymbolIcon(Symbol.OpenWith)
            };

            openInNewTabItem.Click += (s, args) =>
            {
                switch (item.Type?.ToLowerInvariant())
                {
                    case "artist":
                        NavigationHelpers.OpenArtist(item.Id ?? "", item.Title ?? "Artist", openInNewTab: true);
                        break;
                    case "album":
                        NavigationHelpers.OpenAlbum(item.Id ?? "", item.Title ?? "Album", openInNewTab: true);
                        break;
                    case "playlist":
                        NavigationHelpers.OpenPlaylist(item.Id ?? "", item.Title ?? "Playlist", openInNewTab: true);
                        break;
                }
            };

            menu.Items.Add(openInNewTabItem);
            menu.ShowAt(btn, e.GetPosition(btn));
        }
    }
}
