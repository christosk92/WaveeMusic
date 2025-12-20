namespace Wavee.Connect.Playback;

/// <summary>
/// Represents a track in the playback queue.
/// </summary>
/// <param name="Uri">Spotify track URI (e.g., "spotify:track:xxx").</param>
/// <param name="Uid">Optional unique identifier for this queue entry (for tracking).</param>
/// <param name="Title">Optional track title for display purposes.</param>
/// <param name="Artist">Optional artist name for display purposes.</param>
/// <param name="IsUserQueued">True if the track was added via "Add to Queue" (plays before context continues).</param>
public record QueueTrack(
    string Uri,
    string? Uid = null,
    string? Title = null,
    string? Artist = null,
    bool IsUserQueued = false
);
