using System;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a liked/saved song in the user's library.
/// </summary>
public sealed record LikedSongDto : ITrackItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ArtistName { get; init; }
    public required string ArtistId { get; init; }
    public required string AlbumName { get; init; }
    public required string AlbumId { get; init; }
    public string? ImageUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime AddedAt { get; init; }
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Duration formatted as m:ss or h:mm:ss
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    /// <summary>
    /// Relative date formatted as "Today", "Yesterday", "X days ago", etc.
    /// </summary>
    public string AddedAtFormatted => FormatRelativeDate(AddedAt);

    private static string FormatRelativeDate(DateTime date)
    {
        var diff = DateTime.Now - date;
        if (diff.TotalDays < 1) return "Today";
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        return date.ToString("MMM d, yyyy");
    }
}
