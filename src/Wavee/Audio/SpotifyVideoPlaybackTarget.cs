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
    long OriginalDurationMs);
