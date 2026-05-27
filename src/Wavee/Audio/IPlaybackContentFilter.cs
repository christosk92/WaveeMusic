namespace Wavee.Audio;

/// <summary>
/// Minimal core-side surface the playback orchestrator consults to drop
/// tracks the user has explicitly hidden before they enter any queue.
///
/// <para>
/// Implemented in the UI layer by <c>ContentFilterService</c> over Spotify's
/// server-side <c>ban</c> collection (the same list the official client uses
/// for "Hide song"). Lives in <c>Wavee.Audio</c> rather than
/// <c>Wavee.UI</c> because <see cref="PlaybackOrchestrator"/> can't take a
/// reference on the UI assembly — the orchestrator only knows it can ask
/// "is this URI hidden?" via this single method.
/// </para>
///
/// <para>
/// Implementations MUST be thread-safe — the orchestrator queries from its
/// own scheduler. Returning <c>false</c> for unknown / non-Spotify URIs is
/// the correct default (e.g. local-file URIs are not subject to Spotify's
/// ban list).
/// </para>
///
/// <para>
/// Artist-block filtering is deliberately not exposed here: Spotify already
/// honours the <c>artistban</c> set server-side in autoplay / Home /
/// Radio recommendations, and manually playing a track by a blocked artist
/// (e.g. from a user-built playlist) is normal Spotify-desktop behaviour.
/// </para>
/// </summary>
public interface IPlaybackContentFilter
{
    /// <summary>
    /// True when <paramref name="trackUri"/> is on the user's hidden-track
    /// list and should be skipped in every play path (manual click, queue
    /// add, autoplay rollover). Cheap synchronous lookup expected.
    /// </summary>
    bool IsTrackHidden(string trackUri);
}
