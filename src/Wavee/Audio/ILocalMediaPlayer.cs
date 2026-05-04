using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Connect;
using Wavee.Playback.Contracts;

namespace Wavee.Audio;

/// <summary>
/// Engine contract for the UI-process media player. Implemented in the WinUI
/// project (where <see cref="Windows.Media.Playback.MediaPlayer"/> lives) and
/// consumed by <see cref="PlaybackOrchestrator"/> as the parallel engine for
/// tracks that carry video frames.
///
/// <para>
/// Mirrors the subset of <c>AudioPipelineProxy</c> the orchestrator needs so
/// dispatch is "pick one and forward". The orchestrator never reaches into the
/// underlying MediaPlayer; UI surfaces (e.g. a <c>MediaPlayerElement</c>) bind
/// to the implementation directly.
/// </para>
/// </summary>
public interface ILocalMediaPlayer
{
    bool IsActive { get; }
    string? CurrentTrackUri { get; }

    IObservable<LocalPlaybackState> StateChanges { get; }
    IObservable<TrackFinishedMessage> TrackFinished { get; }
    IObservable<PlaybackError> Errors { get; }

    Task PlayFileAsync(
        string filePath,
        string trackUri,
        TrackMetadataDto? metadata,
        long startPositionMs,
        CancellationToken ct = default);

    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SeekAsync(long positionMs, CancellationToken ct = default);

    void SetVolume(float volume);
}
