using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Discovers and caches music-video associations for currently-playing audio
/// tracks using Spotify's Pathfinder GraphQL NPV (Now Playing View) endpoint.
///
/// Spotify catalog has two patterns: "self-contained" tracks already expose
/// the manifest_id on their TrackV4 protobuf (handled inline in
/// <c>TrackResolver</c>), and "linked-URI" tracks where the audio URI and
/// video URI are different catalog entries. This service handles the second
/// pattern — it maps audio URIs to video URIs and resolves the corresponding
/// manifest_id on demand. Click-time lookups are O(1) once the cache is
/// populated by the background pre-fetch fired on track change.
/// </summary>
public interface IMusicVideoDiscoveryService
{
    /// <summary>
    /// Synchronous lookup of a previously-discovered video URI for the given
    /// audio track URI. Returns false when no NPV pre-fetch has completed for
    /// this audio URI or the track has no associated music video.
    /// </summary>
    bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri);

    /// <summary>
    /// Resolves the music-video manifest_id (hex) for a given audio track URI.
    /// Uses the cached video URI from a previous NPV pre-fetch when available,
    /// otherwise runs the NPV query inline. Then fetches the video URI's
    /// TrackV4 to extract <c>OriginalVideo[0].Gid</c> (also cached). Returns
    /// null when the track has no music video.
    /// </summary>
    Task<string?> ResolveManifestIdAsync(string audioTrackUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kicks off the background NPV discovery for an audio track URI. Used by
    /// <c>PlaybackStateService</c> on track change when the catalog cache has
    /// no hint for the URI. Fire-and-forget — when the discovery completes, a
    /// <c>MusicVideoAvailabilityMessage</c> is published so the state service
    /// can flip its observable.
    /// </summary>
    void BeginBackgroundDiscovery(string audioTrackUri);
}
