using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LikedSongsView : UserControl
{
    public LikedSongsViewModel ViewModel { get; }

    public LikedSongsView(LikedSongsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Set up the date formatter for the track list
        TrackList.DateAddedFormatter = item =>
        {
            if (item is LikedSongDto song)
                return song.AddedAtFormatted;
            return "";
        };

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
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
