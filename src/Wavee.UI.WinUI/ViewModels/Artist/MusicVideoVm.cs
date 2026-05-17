using System;

namespace Wavee.UI.WinUI.ViewModels;

public sealed class MusicVideoVm
{
    public required string TrackUri { get; init; }
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? AlbumUri { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string DurationFormatted => Duration.TotalSeconds <= 0
        ? string.Empty
        : Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");
}
