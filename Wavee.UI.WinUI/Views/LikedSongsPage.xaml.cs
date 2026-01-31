using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LikedSongsPage : Page
{
    public LikedSongsViewModel ViewModel { get; }

    public LikedSongsPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<LikedSongsViewModel>();
        InitializeComponent();

        // Set up the date formatter for the track list
        TrackList.DateAddedFormatter = item =>
        {
            if (item is LikedSongDto song)
                return song.AddedAtFormatted;
            return "";
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void TrackList_ArtistClicked(object? sender, string artistId)
    {
        if (!string.IsNullOrEmpty(artistId))
        {
            // TODO: Navigate to artist page
            // NavigationHelpers.OpenArtist(artistId);
        }
    }

    private void TrackList_AlbumClicked(object? sender, string albumId)
    {
        if (!string.IsNullOrEmpty(albumId))
        {
            // TODO: Navigate to album page
            // NavigationHelpers.OpenAlbum(albumId);
        }
    }

    private void TrackList_NewPlaylistRequested(object? sender, IReadOnlyList<string> trackIds)
    {
        NavigationHelpers.OpenCreatePlaylist(isFolder: false, trackIds: trackIds.ToList());
    }
}
