namespace Wavee.Connect.Playback;

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
/// <param name="IsUserQueued">True if the track was added via "Add to Queue" (plays before context continues).</param>
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
    bool IsUserQueued = false
);
