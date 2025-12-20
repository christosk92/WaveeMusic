using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for a podcast show
/// </summary>
public partial class ShowViewModel : ContentItemViewModel
{
    [ObservableProperty]
    public partial string? Publisher { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial int? EpisodeCount { get; set; }

    public ShowViewModel(ShowDto dto)
    {
        Uri = dto.Uri ?? string.Empty;
        Name = dto.Name ?? string.Empty;
        Image = ImageHelper.GetBitmapFromCoverArt(dto.CoverArt);
        Publisher = dto.Publisher;
        Description = dto.Description;
        EpisodeCount = dto.TotalEpisodes;

        // Set subtitle to publisher
        Subtitle = Publisher;

        // Get dominant color from coverArt
        var colors = ImageHelper.GetColorsFromCoverArt(dto.CoverArt);
        DominantColor = ColorHelper.GetBrush(colors);
    }
}
