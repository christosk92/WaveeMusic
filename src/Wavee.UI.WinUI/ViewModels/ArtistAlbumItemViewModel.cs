using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.Contracts;
using Wavee.UI.Models;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for an album item in the artist detail panel.
/// Handles lazy loading of tracks when expanded.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ArtistAlbumItemViewModel : ObservableObject
{
    private readonly Action<ArtistAlbumItemViewModel>? _onSelect;
    private readonly Action<ArtistAlbumItemViewModel>? _onPlay;

    public LibraryArtistAlbumDto Album { get; }
    public string Subtitle => Album.Year > 0 ? Album.Year.ToString() : "";

    [ObservableProperty]
    public partial ObservableCollection<AlbumTrackDto> Tracks { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoadingTracks { get; set; }

    [ObservableProperty]
    public partial bool HasLoadedTracks { get; set; }

    public ArtistAlbumItemViewModel(
        LibraryArtistAlbumDto album,
        Action<ArtistAlbumItemViewModel>? onSelect = null,
        Action<ArtistAlbumItemViewModel>? onPlay = null)
    {
        Album = album;
        _onSelect = onSelect;
        _onPlay = onPlay;
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

    public void OnCardClick(object? sender, EventArgs e)
    {
        _onSelect?.Invoke(this);
    }

    public void OnPlayRequested(object? sender, EventArgs e)
    {
        _onPlay?.Invoke(this);
    }

    public async Task LoadTracksAsync(IAlbumService albumService)
    {
        if (HasLoadedTracks || IsLoadingTracks) return;

        try
        {
            IsLoadingTracks = true;
            var tracks = await albumService.GetTracksAsync(Album.Id);

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
