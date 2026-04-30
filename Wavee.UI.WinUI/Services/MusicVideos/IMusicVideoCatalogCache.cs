namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Per-session in-memory cache of music-video metadata for audio track URIs.
/// Populated incrementally by GraphQL response handlers as the user navigates
/// through the app — artist top tracks, album tracks, search results,
/// playlists. Each piece of information is cached independently:
///
/// - <c>HasVideo</c> — boolean hint sourced from <c>videoAssociations.totalCount</c>.
///   Available immediately on most listing surfaces (cheap, no extra HTTP).
/// - <c>VideoUri</c> — audio URI ↦ music-video URI mapping. Populated lazily
///   by <c>queryNpvArtist</c> when the user plays a track without prior
///   GraphQL exposure.
/// - <c>ManifestId</c> — hex DRM manifest_id for the video. Populated on
///   first click (TrackV4 fetch on the video URI). Cached so re-clicks skip
///   the roundtrip.
///
/// All accessors are threadsafe. There is no persistence — the cache is
/// rebuilt per session.
/// </summary>
public interface IMusicVideoCatalogCache
{
    /// <summary>
    /// Returns the recorded "has music video" flag for the given audio URI.
    /// Returns null when no signal has been observed yet (caller should
    /// fall back to NPV discovery).
    /// </summary>
    bool? GetHasVideo(string audioTrackUri);

    /// <summary>
    /// Records the "has music video" hint observed on a GraphQL response
    /// (e.g. <c>track.associationsV3.videoAssociations.totalCount > 0</c>).
    /// Idempotent — overwrites previous value.
    /// </summary>
    void NoteHasVideo(string audioTrackUri, bool hasVideo);

    /// <summary>
    /// Records the audio→video URI mapping for a track that has a music
    /// video. Implies <c>HasVideo = true</c>.
    /// </summary>
    void NoteVideoUri(string audioTrackUri, string videoTrackUri);

    /// <summary>
    /// Looks up the cached video URI for an audio URI. False when unknown.
    /// </summary>
    bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri);

    /// <summary>
    /// Records the resolved hex manifest_id (from
    /// <c>Track.OriginalVideo[0].Gid</c> on the video URI's TrackV4).
    /// </summary>
    void NoteManifestId(string audioTrackUri, string manifestId);

    /// <summary>
    /// Looks up the cached manifest_id. False when unknown.
    /// </summary>
    bool TryGetManifestId(string audioTrackUri, out string manifestId);
}
