using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Connect;
using Wavee.Playback.Contracts;

namespace Wavee.Audio;

/// <summary>
/// Engine contract for Spotify music-video playback. Implemented in the WinUI
/// project (where the WebView2/Widevine EME player lives) and consumed by
/// <see cref="PlaybackOrchestrator"/> when a Spotify track carries
/// <c>track_player=video</c> and <c>media.manifest_id</c> metadata.
/// </summary>
public interface ISpotifyVideoPlayback
{
    bool IsActive { get; }

    IObservable<LocalPlaybackState> StateChanges { get; }
    IObservable<TrackFinishedMessage> TrackFinished { get; }
    IObservable<PlaybackError> Errors { get; }

    /// <summary>
    /// Cold-start path: fetches the manifest, builds the player, and starts
    /// playback in one go. Used when no prepared session is available.
    /// </summary>
    Task PlayAsync(
        string manifestId,
        string trackUri,
        TrackMetadataDto? metadata,
        long durationMs,
        double startPositionMs,
        CancellationToken ct = default);

    /// <summary>
    /// Warm the playback pipeline ahead of commit. Builds the WebView2,
    /// loads the manifest (preferably from <see cref="Wavee.Core.Video.IVideoManifestCache"/>),
    /// runs the Widevine licence challenge, and appends the first segment(s)
    /// into MSE — without calling <c>play()</c>. The returned handle is
    /// committed via the overload that accepts <see cref="IPreparedVideoSession"/>.
    /// May return <c>null</c> if the engine refuses (e.g. already committed
    /// elsewhere, or unsupported codec); callers fall back to
    /// <see cref="PlayAsync(string,string,TrackMetadataDto?,long,double,CancellationToken)"/>.
    /// </summary>
    Task<IPreparedVideoSession?> PrepareAsync(
        SpotifyVideoPlaybackTarget target,
        CancellationToken ct = default);

    /// <summary>
    /// Commit a previously prepared session as the active playback. Publishes
    /// the surface and runs <c>play()</c>; first-frame typically lands within
    /// 30–150 ms.
    /// </summary>
    Task PlayAsync(
        IPreparedVideoSession prepared,
        double startPositionMs,
        CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task SeekAsync(long positionMs, CancellationToken ct = default);
    void SetVolume(float volume);
}
