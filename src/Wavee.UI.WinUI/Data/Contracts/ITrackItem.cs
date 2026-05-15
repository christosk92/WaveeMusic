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
    /// Album artwork URL. Typically the largest CDN flavor available;
    /// hero pre-fill and connected-animation hand-offs use this directly.
    /// </summary>
    string? ImageUrl { get; }

    /// <summary>
    /// CDN image variant ≲150 px wide for 48 px row slots. Distinct
    /// image-id from <see cref="ImageUrl"/> — different bytes on
    /// <c>i.scdn.co</c>, not just a decode hint. Defaults to <c>null</c>
    /// so implementations that don't yet surface a small flavor still
    /// work; <see cref="Wavee.UI.WinUI.Controls.Track.TrackItem"/> falls
    /// back to <see cref="ImageUrl"/> when this is null.
    /// </summary>
    string? ImageSmallUrl => null;

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
    /// Whether this entry has a music video available — either the audio
    /// track has a linked video (<c>associationsV3.videoAssociations.totalCount &gt; 0</c>),
    /// or the track itself IS a video (<see cref="IsVideoTrack"/>). Drives
    /// the "Watch Video" affordance / film-badge across track rows.
    /// </summary>
    bool HasVideo => false;

    /// <summary>
    /// True when this catalog entry IS a video track (Pathfinder
    /// <c>trackMediaType == "VIDEO"</c>) rather than an audio track that has
    /// a linked video. Plays should route directly to the video player.
    /// </summary>
    bool IsVideoTrack => false;

    /// <summary>
    /// Whether this track is saved/liked in the user's library.
    /// Mutable — updated by the library save service.
    /// </summary>
    bool IsLiked { get; set; }

    /// <summary>
    /// True when the track is a Wavee-indexed local file (URI starts with
    /// <c>wavee:local:track:</c>). Drives the "On this PC" badge in TrackItem
    /// and the local-likes routing on HeartButton. Defaults to a URI check so
    /// implementers don't need to set a separate field.
    /// </summary>
    bool IsLocal => !string.IsNullOrEmpty(Uri) &&
                    Uri.StartsWith("wavee:local:track:", StringComparison.Ordinal);

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
    /// Optional playback progress from 0.0 to 1.0. Podcast episodes can populate
    /// this from Spotify's <c>playedState.playPositionMilliseconds</c>.
    /// </summary>
    double? PlaybackProgress => null;

    /// <summary>
    /// Optional display text for <see cref="PlaybackProgress"/>, e.g. "42%".
    /// </summary>
    string PlaybackProgressText => string.Empty;

    /// <summary>
    /// True when the progress source failed, distinct from an unplayed episode.
    /// </summary>
    bool HasPlaybackProgressError => false;

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

    /// <summary>
    /// Optional rich list of contributing artists with names + URIs. When non-empty,
    /// TrackItem renders each artist as an independently-clickable hyperlink instead
    /// of the flattened <see cref="ArtistName"/> string. DTOs that don't preserve
    /// this list (legacy paths) leave it empty and the row falls back to
    /// <c>(ArtistName, ArtistId)</c>.
    /// </summary>
    IReadOnlyList<TrackArtistRef> Artists => Array.Empty<TrackArtistRef>();
}
