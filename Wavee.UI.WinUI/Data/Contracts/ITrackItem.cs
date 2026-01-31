using System;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Common interface for track items that can be displayed in a TrackListView.
/// Implemented by LikedSongDto, PlaylistTrackDto, AlbumTrackDto, etc.
/// </summary>
public interface ITrackItem
{
    /// <summary>
    /// Unique identifier for the track (e.g., spotify:track:xxx).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Track title.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Primary artist name.
    /// </summary>
    string ArtistName { get; }

    /// <summary>
    /// Primary artist ID for navigation.
    /// </summary>
    string ArtistId { get; }

    /// <summary>
    /// Album name.
    /// </summary>
    string AlbumName { get; }

    /// <summary>
    /// Album ID for navigation.
    /// </summary>
    string AlbumId { get; }

    /// <summary>
    /// Album artwork URL.
    /// </summary>
    string? ImageUrl { get; }

    /// <summary>
    /// Track duration.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Whether the track has explicit content.
    /// </summary>
    bool IsExplicit { get; }

    /// <summary>
    /// Formatted duration string (e.g., "3:45" or "1:02:30").
    /// </summary>
    string DurationFormatted { get; }
}
