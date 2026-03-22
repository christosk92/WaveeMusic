using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PlaylistPage : Page
{
    private readonly ILogger? _logger;

    public PlaylistViewModel ViewModel { get; }

    public PlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<PlaylistPage>>();
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

        // Receive connected animation from source card
        Helpers.ConnectedAnimationHelper.TryStartAnimation(
            Helpers.ConnectedAnimationHelper.PlaylistArt, PlaylistArtContainer);

        try
        {
            if (e.Parameter is Data.Parameters.ContentNavigationParameter nav)
            {
                ViewModel.PrefillFrom(nav);
                await ViewModel.LoadCommand.ExecuteAsync(nav.Uri);
            }
            else if (e.Parameter is string playlistId && !string.IsNullOrWhiteSpace(playlistId))
            {
                await ViewModel.LoadCommand.ExecuteAsync(playlistId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in PlaylistPage OnNavigatedTo");
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
