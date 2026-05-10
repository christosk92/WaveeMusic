using System.Collections.Generic;

namespace Wavee.Audio.Queue;

/// <summary>
/// Represents a track in the playback queue.
/// </summary>
/// <param name="Uri">Spotify track URI (e.g., "spotify:track:xxx").</param>
/// <param name="Uid">Optional unique identifier for this queue entry (for tracking).</param>
/// <param name="Title">Optional track title for display purposes.</param>
/// <param name="Artist">Optional artist name for display purposes.</param>
/// <param name="Album">Optional album name for sorting.</param>
/// <param name="AlbumUri">Optional album URI (e.g., "spotify:album:xxx").</param>
/// <param name="ArtistUri">Optional artist URI (e.g., "spotify:artist:xxx").</param>
/// <param name="DurationMs">Track duration in milliseconds for sorting.</param>
/// <param name="AddedAt">Unix timestamp when added to playlist (for sorting).</param>
/// <param name="IsPlayable">Whether the track is playable (false if unavailable).</param>
/// <param name="IsExplicit">Whether the track has explicit content.</param>
/// <param name="IsUserQueued">True if the track was added via "Play Next" or "Add to Queue" (provider="queue").</param>
/// <param name="Provider">Track source: "context", "queue", or "autoplay".</param>
/// <param name="ImageUrl">Album art image URL for display (format: "spotify:image:{id}").</param>
/// <param name="IsPostContext">True if added via "Add to Queue" (plays AFTER the entire context exhausts; local-only distinction).</param>
public record QueueTrack(
    string Uri,
    string? Uid = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumUri = null,
    string? ArtistUri = null,
    int? DurationMs = null,
    long? AddedAt = null,
    bool IsPlayable = true,
    bool IsExplicit = false,
    bool IsUserQueued = false,
    string Provider = "context",
    string? ImageUrl = null,
    bool IsPostContext = false
) : IQueueItem
{
    /// <summary>Always true for playable tracks.</summary>
    public bool IsTrack => true;

    /// <summary>Whether metadata is present (title + artist populated).</summary>
    public bool HasMetadata => Title is not null && Artist is not null;

    /// <summary>Whether this is an autoplay/radio track.</summary>
    public bool IsAutoplay => Provider is "autoplay";

    /// <summary>
    /// Extra per-track metadata supplied by the playlist API (recommender
    /// signals like <c>item-score</c>, <c>decision_id</c>, <c>core:list_uid</c>,
    /// <c>PROBABLY_IN_*</c>). Forwarded verbatim into <c>ProvidedTrack.metadata</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
