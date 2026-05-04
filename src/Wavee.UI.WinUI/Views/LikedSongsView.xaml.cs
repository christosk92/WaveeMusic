using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LikedSongsView : UserControl, IDisposable
{
    public LikedSongsViewModel ViewModel { get; }
    private bool _disposed;

    public LikedSongsView(LikedSongsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Set up the date formatter for both the live TrackGrid (TrackDataGrid)
        // and the legacy TrackList (TrackListView, hidden but still bound).
        // Without the TrackGrid wire, the visible Date Added column on the
        // Liked Songs page renders empty cells.
        Func<object, string> dateFormatter = item =>
        {
            if (item is LikedSongDto song)
                return song.AddedAtFormatted;
            return "";
        };
        TrackGrid.DateAddedFormatter = dateFormatter;
        TrackList.DateAddedFormatter = dateFormatter;

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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TrackList.DateAddedFormatter = null;
        TrackGrid.DateAddedFormatter = null;
    }
}
