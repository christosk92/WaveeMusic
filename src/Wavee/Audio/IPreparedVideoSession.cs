using System;
using System.Threading.Tasks;

namespace Wavee.Audio;

/// <summary>
/// Opaque handle representing a music-video session that has been warmed
/// (WebView2 created, manifest loaded, Widevine license acquired, first
/// segments appended) but is paused. Handed back to
/// <see cref="ISpotifyVideoPlayback.PlayAsync(IPreparedVideoSession, long, System.Threading.CancellationToken)"/>
/// to commit it as the active playback session.
///
/// Discarded prepared sessions must be disposed so any underlying WebView2
/// is released — otherwise the renderer accumulates per skip.
/// </summary>
public interface IPreparedVideoSession : IAsyncDisposable
{
    /// <summary>
    /// The video track URI this session was prepared for. Used to detect a
    /// stale prepared session after a queue change.
    /// </summary>
    string VideoTrackUri { get; }

    /// <summary>
    /// When the prepare started. Older sessions may have expired Widevine
    /// licenses and should be re-prepared on commit.
    /// </summary>
    DateTimeOffset PreparedAt { get; }
}
