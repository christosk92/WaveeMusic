using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Connect;
using Wavee.Playback.Contracts;

namespace Wavee.Audio;

/// <summary>
/// Engine contract for Spotify music-video playback. Implemented in the WinUI
/// project (where AdaptiveMediaSource + PlayReady live) and consumed by
/// <see cref="PlaybackOrchestrator"/> when a Spotify track carries
/// <c>track_player=video</c> and <c>media.manifest_id</c> metadata.
/// </summary>
public interface ISpotifyVideoPlayback
{
    bool IsActive { get; }

    IObservable<LocalPlaybackState> StateChanges { get; }
    IObservable<TrackFinishedMessage> TrackFinished { get; }
    IObservable<PlaybackError> Errors { get; }

    Task PlayAsync(
        string manifestId,
        string trackUri,
        TrackMetadataDto? metadata,
        long durationMs,
        double startPositionMs,
        CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task SeekAsync(long positionMs, CancellationToken ct = default);
    void SetVolume(float volume);
}
