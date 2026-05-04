using System;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a track within an album in the library.
/// </summary>
public sealed record LibraryAlbumTrackDto
{
    public required string Id { get; init; }
    public int TrackNumber { get; init; }
    public required string Title { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Duration formatted as m:ss or h:mm:ss
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}
