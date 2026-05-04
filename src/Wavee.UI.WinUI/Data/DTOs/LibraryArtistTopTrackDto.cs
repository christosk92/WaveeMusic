using System;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a top track of an artist in the library view.
/// </summary>
public sealed record LibraryArtistTopTrackDto
{
    public required string Id { get; init; }
    public int Index { get; init; }
    public required string Title { get; init; }
    public string? AlbumName { get; init; }
    public string? AlbumImageUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public long PlayCount { get; init; }
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Duration formatted as m:ss or h:mm:ss
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    /// <summary>
    /// Formatted play count (e.g., "1.2B", "500M", "100K")
    /// </summary>
    public string PlayCountFormatted => PlayCount switch
    {
        >= 1_000_000_000 => $"{PlayCount / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{PlayCount / 1_000_000.0:0.#}M",
        >= 1_000 => $"{PlayCount / 1_000.0:0.#}K",
        _ => PlayCount.ToString("N0")
    };
}
