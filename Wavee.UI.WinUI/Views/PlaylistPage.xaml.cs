using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PlaylistPage : Page
{
    public PlaylistViewModel ViewModel { get; }

    public PlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        InitializeComponent();

        // Set up the date formatter for the track list
        TrackList.DateAddedFormatter = item =>
        {
            if (item is PlaylistTrackDto track)
                return track.AddedAtFormatted;
            return "";
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string playlistId && !string.IsNullOrWhiteSpace(playlistId))
        {
            await ViewModel.LoadCommand.ExecuteAsync(playlistId);
        }
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
