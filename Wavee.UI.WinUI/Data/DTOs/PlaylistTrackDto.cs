using System;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Represents a track within a playlist.
/// </summary>
public sealed record PlaylistTrackDto : ITrackItem
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
    public string? AddedBy { get; init; }
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Duration formatted as "m:ss" or "h:mm:ss".
    /// </summary>
    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    /// <summary>
    /// AddedAt formatted as relative time or date.
    /// </summary>
    public string AddedAtFormatted
    {
        get
        {
            var diff = DateTime.Now - AddedAt;
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} hr ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
            return AddedAt.ToString("MMM d, yyyy");
        }
    }
}
