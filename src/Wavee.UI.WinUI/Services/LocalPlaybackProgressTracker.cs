using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Audio;
using Wavee.Connect;
using Wavee.Playback.Contracts;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Bridges <see cref="ILocalMediaPlayer"/>'s state stream into the local library:
/// persists <c>last_position_ms</c> every 5s during playback, flips
/// <c>watched_at</c> when progress crosses 90%, and records a row in
/// <c>local_plays</c> on each end-of-track.
///
/// <para>Singleton, resolved at app start so the subscription is permanent.</para>
/// </summary>
public sealed class LocalPlaybackProgressTracker : IDisposable
{
    private const long WriteEveryMs = 5_000;
    private const double WatchedThreshold = 0.9;

    private readonly ILocalMediaPlayer _player;
    private readonly ILocalLibraryFacade _facade;
    private readonly ILogger? _logger;
    private readonly IDisposable _stateSub;
    private readonly IDisposable _finishedSub;
    private long _lastWriteAtMs;
    private string? _lastTrackUri;
    private long _maxPositionForCurrentTrack;
    private bool _watchedFiredForCurrent;

    public LocalPlaybackProgressTracker(
        ILocalMediaPlayer player,
        ILocalLibraryFacade facade,
        ILogger<LocalPlaybackProgressTracker>? logger = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _logger = logger;

        _stateSub = _player.StateChanges.Subscribe(OnState);
        _finishedSub = _player.TrackFinished.Subscribe(OnFinished);
    }

    private async void OnState(LocalPlaybackState state)
    {
        try
        {
            if (string.IsNullOrEmpty(state.TrackUri)) return;
            if (state.TrackUri != _lastTrackUri)
            {
                _lastTrackUri = state.TrackUri;
                _maxPositionForCurrentTrack = 0;
                _watchedFiredForCurrent = false;
                _lastWriteAtMs = 0;
            }

            if (state.PositionMs > _maxPositionForCurrentTrack)
                _maxPositionForCurrentTrack = state.PositionMs;

            // Throttle DB writes to every 5 seconds.
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastWriteAtMs < WriteEveryMs) return;
            _lastWriteAtMs = nowMs;

            await _facade.SetLastPositionAsync(state.TrackUri, state.PositionMs);

            // Flip watched_at once we cross 90% (only once per playback session).
            if (!_watchedFiredForCurrent && state.DurationMs > 0
                && state.PositionMs >= state.DurationMs * WatchedThreshold)
            {
                _watchedFiredForCurrent = true;
                await _facade.MarkWatchedAsync(state.TrackUri, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LocalPlaybackProgressTracker state-write failed");
        }
    }

    private async void OnFinished(TrackFinishedMessage msg)
    {
        try
        {
            if (string.IsNullOrEmpty(msg.TrackUri)) return;
            await _facade.RecordPlayAsync(
                msg.TrackUri,
                _maxPositionForCurrentTrack,
                durationMs: 0);
            // Reset for next track
            _maxPositionForCurrentTrack = 0;
            _watchedFiredForCurrent = false;
            _lastTrackUri = null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LocalPlaybackProgressTracker finished-write failed");
        }
    }

    public void Dispose()
    {
        _stateSub.Dispose();
        _finishedSub.Dispose();
    }
}
