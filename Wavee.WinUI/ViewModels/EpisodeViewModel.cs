using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for a podcast episode
/// </summary>
public partial class EpisodeViewModel : ContentItemViewModel
{
    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial string? ShowName { get; set; }

    [ObservableProperty]
    public partial string? Duration { get; set; }

    [ObservableProperty]
    public partial string? ReleaseDate { get; set; }

    public EpisodeViewModel(EpisodeDto dto)
    {
        Uri = dto.Uri ?? string.Empty;
        Name = dto.Name ?? string.Empty;
        Image = ImageHelper.GetBitmapFromCoverArt(dto.CoverArt);
        Description = dto.Description;
        ShowName = dto.Show?.Name;

        // Format duration
        if (dto.DurationMs.HasValue)
        {
            var duration = TimeSpan.FromMilliseconds(dto.DurationMs.Value);
            Duration = duration.ToString(@"mm\:ss");
        }

        // Format release date
        if (!string.IsNullOrEmpty(dto.ReleaseDate))
        {
            if (DateTime.TryParse(dto.ReleaseDate, out var date))
            {
                ReleaseDate = date.ToString("MMM dd, yyyy");
            }
        }

        // Set subtitle to show name
        Subtitle = ShowName;
    }
}
