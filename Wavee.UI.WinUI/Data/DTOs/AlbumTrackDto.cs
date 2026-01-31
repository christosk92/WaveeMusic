using System;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a track within an album.
/// </summary>
public sealed record AlbumTrackDto : ITrackItem
{
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

    /// <summary>
    /// Duration formatted as "m:ss" or "h:mm:ss".
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}
