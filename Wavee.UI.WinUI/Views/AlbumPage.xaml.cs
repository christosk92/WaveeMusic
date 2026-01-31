using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
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

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        Unloaded += AlbumPage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void AlbumPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string albumId && !string.IsNullOrWhiteSpace(albumId))
        {
            await ViewModel.LoadCommand.ExecuteAsync(albumId);
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

    private void TrackList_ArtistClicked(object? sender, string artistId)
    {
        if (!string.IsNullOrEmpty(artistId))
        {
            NavigationHelpers.OpenArtist(artistId, "Artist");
        }
    }

    private void TrackList_NewPlaylistRequested(object? sender, IReadOnlyList<string> trackIds)
    {
        NavigationHelpers.OpenCreatePlaylist(isFolder: false, trackIds: trackIds.ToList());
    }
}
