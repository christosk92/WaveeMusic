using System;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a track within an album.
/// </summary>
public sealed record AlbumTrackDto : ITrackItem
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ArtistName { get; init; }
    public required string ArtistId { get; init; }
    public required string AlbumName { get; init; }
    public required string AlbumId { get; init; }
    public string? ImageUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; }
    public bool IsPlayable { get; init; } = true;
    public int OriginalIndex { get; init; }
    public bool IsLoaded => true;
    public long PlayCount { get; init; }

    /// <summary>
    /// Duration formatted as "m:ss" or "h:mm:ss".
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    /// <summary>
    /// Formatted play count (e.g., "1.2B", "500M", "100K").
    /// </summary>
    public string PlayCountFormatted => PlayCount switch
    {
        0 => "",
        >= 1_000_000_000 => $"{PlayCount / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{PlayCount / 1_000_000.0:0.#}M",
        >= 1_000 => $"{PlayCount / 1_000.0:0.#}K",
        _ => PlayCount.ToString("N0")
    };
}
