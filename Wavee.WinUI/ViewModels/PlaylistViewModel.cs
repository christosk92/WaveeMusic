using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for a playlist
/// </summary>
public partial class PlaylistViewModel : ContentItemViewModel
{
    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial string? OwnerName { get; set; }

    [ObservableProperty]
    public partial int? TrackCount { get; set; }

    public PlaylistViewModel(PlaylistDto dto)
    {
        Uri = dto.Uri ?? string.Empty;
        Name = dto.Name ?? string.Empty;
        Image = ImageHelper.GetBitmapFromPlaylistImages(dto.Images);
        Description = dto.Description;
        OwnerName = dto.OwnerV2?.Data?.Name;
        TrackCount = dto.TotalTracks;

        // Set subtitle to description (strip HTML formatting)
        Subtitle = TextHelper.StripHtml(Description);

        // Get dominant color from images.items[].extractedColors
        var colors = ImageHelper.GetColorsFromPlaylistImages(dto.Images);
        var brush = ColorHelper.GetBrush(colors);
        System.Diagnostics.Debug.WriteLine($"[PlaylistViewModel] Name={Name}, Colors={colors != null}, Brush={brush != null}, Brush.Color={brush?.Color}");
        DominantColor = brush;
    }
}
