using Wavee.Playback.Contracts;

namespace Wavee.Audio;

/// <summary>
/// Fully resolved Spotify music-video target for an audio track.
/// The queue may stay on <see cref="AudioTrackUri"/> while the active
/// playback identity published to Spotify is <see cref="VideoTrackUri"/>.
/// </summary>
public sealed record SpotifyVideoPlaybackTarget(
    string AudioTrackUri,
    string VideoTrackUri,
    string ManifestId,
    TrackMetadataDto Metadata,
    long DurationMs,
    TrackMetadataDto OriginalMetadata,
    long OriginalDurationMs)
{
    /// <summary>
    /// Optional URL the UI can crossfade in as a blurred poster while the
    /// first video frame is decoding. Sourced today from the existing track
    /// album art (<see cref="TrackMetadataDto.ImageLargeUrl"/>); future work
    /// can swap in the 16:9 image from Pathfinder once threaded through.
    /// </summary>
    public string? PosterUrl { get; init; }
}
