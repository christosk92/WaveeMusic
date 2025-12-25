using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class AlbumPage : Page, ITabBarItemContent
{
    public AlbumViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public AlbumPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<AlbumViewModel>();
        InitializeComponent();

        ViewModel.ContentChanged += (s, e) => ContentChanged?.Invoke(this, e);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string albumId)
        {
            ViewModel.Initialize(albumId);
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ViewModel.ArtistId))
        {
            var openInNewTab = NavigationHelpers.IsCtrlPressed();
            NavigationHelpers.OpenArtist(ViewModel.ArtistId, ViewModel.ArtistName ?? "Artist", openInNewTab);
        }
    }

    private void Artist_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Middle-click to open in new tab
        if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
            return;

        if (!string.IsNullOrEmpty(ViewModel.ArtistId))
        {
            NavigationHelpers.OpenArtist(ViewModel.ArtistId, ViewModel.ArtistName ?? "Artist", openInNewTab: true);
        }
    }

    private void RelatedAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ArtistAlbum album)
        {
            var openInNewTab = NavigationHelpers.IsCtrlPressed();
            NavigationHelpers.OpenAlbum(album.Id ?? "", album.Title ?? "Album", openInNewTab);
        }
    }

    private void RelatedAlbum_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Middle-click to open in new tab
        if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
            return;

        if (sender is Button btn && btn.DataContext is ArtistAlbum album)
        {
            NavigationHelpers.OpenAlbum(album.Id ?? "", album.Title ?? "Album", openInNewTab: true);
        }
    }
}
