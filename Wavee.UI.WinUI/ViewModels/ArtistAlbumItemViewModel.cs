using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for an album item in the artist detail panel.
/// Handles lazy loading of tracks when expanded.
/// </summary>
public sealed partial class ArtistAlbumItemViewModel : ObservableObject
{
    private readonly Action<ArtistAlbumItemViewModel>? _onSelect;

    public LibraryArtistAlbumDto Album { get; }

    [ObservableProperty]
    private ObservableCollection<AlbumTrackDto> _tracks = [];

    [ObservableProperty]
    private bool _isLoadingTracks;

    [ObservableProperty]
    private bool _hasLoadedTracks;

    public ArtistAlbumItemViewModel(LibraryArtistAlbumDto album, Action<ArtistAlbumItemViewModel>? onSelect = null)
    {
        Album = album;
        _onSelect = onSelect;
    }

    [RelayCommand]
    private void Select()
    {
        _onSelect?.Invoke(this);
    }

    // Event handler for Tapped event binding
    public void OnTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _onSelect?.Invoke(this);
    }

    public async Task LoadTracksAsync(ICatalogService catalogService)
    {
        if (HasLoadedTracks || IsLoadingTracks) return;

        try
        {
            IsLoadingTracks = true;
            var tracks = await catalogService.GetAlbumTracksAsync(Album.Id);

            Tracks.Clear();
            foreach (var track in tracks)
            {
                Tracks.Add(track);
            }
            HasLoadedTracks = true;
        }
        finally
        {
            IsLoadingTracks = false;
        }
    }
}
