using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Common interface for track items that can be displayed in a TrackListView.
/// Extends INotifyPropertyChanged so x:Bind Mode=OneWay works in DataTemplates.
/// Implemented by LikedSongDto, PlaylistTrackDto, AlbumTrackDto, etc.
/// </summary>
public interface ITrackItem : INotifyPropertyChanged
{
    /// <summary>
    /// Unique identifier for the track (bare ID, e.g. "4xeugB5MqWh0jwvXZPxahq").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Full URI for playback and identification (e.g. "spotify:track:xxx", "spotify:episode:xxx").
    /// </summary>
    string Uri { get; }

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

    /// <summary>
    /// Original 1-based index from the source order (e.g., playlist position, track number).
    /// Preserved when sorting/filtering so the # column shows the original position.
    /// </summary>
    int OriginalIndex { get; }

    /// <summary>
    /// Whether the track data has been loaded. True for non-lazy items.
    /// When false, TrackListView shows shimmer placeholders for this row.
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Whether this entry is a music video (has audio associations).
    /// </summary>
    bool HasVideo => false;

    /// <summary>
    /// Whether this track is saved/liked in the user's library.
    /// Mutable — updated by the library save service.
    /// </summary>
    bool IsLiked { get; set; }

    /// <summary>
    /// Optional "added on" relative/absolute display string. Present on
    /// <c>PlaylistTrackDto</c> and <c>LikedSongDto</c>; empty for album-track-style DTOs.
    /// Exposed through the interface so <c>TrackDataGrid</c>'s Date Added column can
    /// bind one path without per-DTO type checks.
    /// </summary>
    string AddedAtFormatted => string.Empty;

    /// <summary>
    /// Optional formatted play count. Present on <c>AlbumTrackDto</c> (e.g. "12.3M").
    /// Empty when the DTO doesn't track plays.
    /// </summary>
    string PlayCountFormatted => string.Empty;

    /// <summary>
    /// Stable uid for this track within its source context (lower-case hex).
    /// Derived from the Spotify playlist <c>itemId</c> / album/artist <c>uid</c>
    /// at the API layer. Null when the source doesn't carry a uid (e.g. Liked Songs).
    /// </summary>
    string? Uid => null;

    /// <summary>
    /// Per-track recommender / format attributes returned by the context API
    /// (e.g. playlist <c>formatAttributes</c>). Null when the source carries none.
    /// </summary>
    IReadOnlyDictionary<string, string>? FormatAttributes => null;
}
