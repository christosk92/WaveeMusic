using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for an album
/// </summary>
public partial class AlbumViewModel : ContentItemViewModel
{
    [ObservableProperty]
    public partial string? ArtistNames { get; set; }

    [ObservableProperty]
    public partial string? ReleaseYear { get; set; }

    /// <summary>
    /// Command to navigate to the album's artist
    /// </summary>
    protected override void GoToArtist()
    {
        // TODO: Extract artist URI and navigate to artist page
        System.Diagnostics.Debug.WriteLine($"GoToArtist from album: {Name}");
    }

    /// <summary>
    /// Command to add album tracks to a playlist
    /// </summary>
    protected override void AddToPlaylist()
    {
        // TODO: Show playlist picker dialog and add album tracks
        System.Diagnostics.Debug.WriteLine($"AddToPlaylist: {Name} (Uri: {Uri})");
    }

    public AlbumViewModel(AlbumDto dto)
    {
        Uri = dto.Uri ?? string.Empty;
        Name = dto.Name ?? string.Empty;
        Image = ImageHelper.GetBitmapFromCoverArt(dto.CoverArt);

        // Extract artist names from artists.items[].profile.name
        if (dto.Artists?.Items?.Length > 0)
        {
            ArtistNames = string.Join(", ", dto.Artists.Items.Select(a => a.Profile?.Name).Where(n => !string.IsNullOrEmpty(n)));
            Subtitle = ArtistNames;
        }

        // Extract release year
        if (!string.IsNullOrEmpty(dto.ReleaseDate))
        {
            if (DateTime.TryParse(dto.ReleaseDate, out var releaseDate))
            {
                ReleaseYear = releaseDate.Year.ToString();
            }
        }

        // Get dominant color from coverArt
        var colors = ImageHelper.GetColorsFromCoverArt(dto.CoverArt);
        var brush = ColorHelper.GetBrush(colors);
        System.Diagnostics.Debug.WriteLine($"[AlbumViewModel] Name={Name}, Colors={colors != null}, Brush={brush != null}, Brush.Color={brush?.Color}");
        DominantColor = brush;
    }
}
