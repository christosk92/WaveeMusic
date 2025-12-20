using System.IO.Compression;
using System.Text.Json;
using Google.Protobuf;
using Wavee.Connect;
using Wavee.Connect.Protocol;
using Wavee.Protocol.Player;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Test helpers for PlaybackStateManager tests.
/// Provides utilities for creating cluster update messages and mock playback engines.
/// </summary>
internal static class PlaybackStateTestHelpers
{
    /// <summary>
    /// Creates a dealer message containing a ClusterUpdate protobuf (as JSON string for MockDealerConnection).
    /// </summary>
    public static string CreateClusterUpdateMessage(
        string activeDeviceId,
        string? trackUri = null,
        string? trackTitle = null,
        string? artist = null,
        long positionMs = 0,
        long durationMs = 300000,
        bool isPlaying = false,
        bool isPaused = false,
        bool shuffling = false,
        bool repeatingContext = false,
        bool repeatingTrack = false,
        string? contextUri = null,
        bool gzipCompress = false)
    {
        var cluster = new Cluster
        {
            ActiveDeviceId = activeDeviceId,
            ChangedTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Add player state if track specified
        if (trackUri != null)
        {
            var playerState = new PlayerState
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ContextUri = contextUri ?? string.Empty,
                PositionAsOfTimestamp = positionMs,
                Duration = durationMs,
                IsPlaying = isPlaying,
                IsPaused = isPaused,
                Track = new ProvidedTrack
                {
                    Uri = trackUri,
                    Uid = Guid.NewGuid().ToString()
                },
                Options = new ContextPlayerOptions
                {
                    ShufflingContext = shuffling,
                    RepeatingContext = repeatingContext,
                    RepeatingTrack = repeatingTrack
                }
            };

            // Add metadata if specified
            if (trackTitle != null)
                playerState.Track.Metadata["title"] = trackTitle;
            if (artist != null)
                playerState.Track.Metadata["artist_name"] = artist;

            cluster.PlayerState = playerState;
        }

        var clusterUpdate = new ClusterUpdate
        {
            Cluster = cluster,
            UpdateReason = ClusterUpdateReason.DevicesDisappeared
        };

        // Serialize to protobuf
        var payload = clusterUpdate.ToByteArray();

        // Optionally gzip compress
        if (gzipCompress)
        {
            payload = GzipCompress(payload);
        }

        // Create dealer message JSON
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/x-protobuf"
        };

        if (gzipCompress)
        {
            headers["Transfer-Encoding"] = "gzip";
        }

        return DealerTestHelpers.CreateDealerMessage(
            uri: "hm://connect-state/v1/cluster",
            headers: headers,
            payload: payload);
    }

    /// <summary>
    /// Gzip compresses a byte array.
    /// </summary>
    private static byte[] GzipCompress(byte[] data)
    {
        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionMode.Compress))
        {
            gzipStream.Write(data, 0, data.Length);
        }
        return outputStream.ToArray();
    }

    /// <summary>
    /// Creates a mock IPlaybackEngine for testing bidirectional mode.
    /// </summary>
    public static MockPlaybackEngine CreateMockPlaybackEngine()
    {
        return new MockPlaybackEngine();
    }
}

/// <summary>
/// Mock implementation of IPlaybackEngine for testing.
/// </summary>
internal sealed class MockPlaybackEngine : IPlaybackEngine
{
    private readonly System.Reactive.Subjects.BehaviorSubject<LocalPlaybackState> _stateSubject;
    private readonly System.Reactive.Subjects.Subject<PlaybackError> _errorSubject = new();
    private LocalPlaybackState _currentState;

    public IObservable<LocalPlaybackState> StateChanges => _stateSubject;
    public IObservable<PlaybackError> Errors => _errorSubject;
    public LocalPlaybackState CurrentState => _currentState;

    public List<string> CommandsReceived { get; } = new();

    public MockPlaybackEngine()
    {
        _currentState = LocalPlaybackState.Empty;
        _stateSubject = new System.Reactive.Subjects.BehaviorSubject<LocalPlaybackState>(_currentState);
    }

    /// <summary>
    /// Simulates a state change from the playback engine.
    /// </summary>
    public void SimulateStateChange(LocalPlaybackState newState)
    {
        _currentState = newState;
        _stateSubject.OnNext(newState);
    }

    /// <summary>
    /// Simulates starting playback of a track.
    /// </summary>
    public void SimulatePlay(string trackUri, string? trackUid = null, long positionMs = 0, long durationMs = 300000)
    {
        var state = new LocalPlaybackState
        {
            TrackUri = trackUri,
            TrackUid = trackUid ?? Guid.NewGuid().ToString(),
            PositionMs = positionMs,
            DurationMs = durationMs,
            IsPlaying = true,
            IsPaused = false,
            IsBuffering = false,
            Shuffling = _currentState.Shuffling,
            RepeatingContext = _currentState.RepeatingContext,
            RepeatingTrack = _currentState.RepeatingTrack,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SimulateStateChange(state);
    }

    /// <summary>
    /// Simulates pausing playback.
    /// </summary>
    public void SimulatePause()
    {
        var state = _currentState with
        {
            IsPlaying = false,
            IsPaused = true,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SimulateStateChange(state);
    }

    /// <summary>
    /// Simulates resuming playback.
    /// </summary>
    public void SimulateResume()
    {
        var state = _currentState with
        {
            IsPlaying = true,
            IsPaused = false,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SimulateStateChange(state);
    }

    /// <summary>
    /// Simulates stopping playback.
    /// </summary>
    public void SimulateStop()
    {
        var state = LocalPlaybackState.Empty;
        SimulateStateChange(state);
    }

    /// <summary>
    /// Simulates seeking to a position.
    /// </summary>
    public void SimulateSeek(long positionMs)
    {
        var state = _currentState with
        {
            PositionMs = positionMs,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SimulateStateChange(state);
    }

    /// <summary>
    /// Simulates changing shuffle state.
    /// </summary>
    public void SimulateShuffleChange(bool enabled)
    {
        var state = _currentState with
        {
            Shuffling = enabled,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SimulateStateChange(state);
    }

    public Task PlayAsync(Wavee.Connect.Commands.PlayCommand command, CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add($"Play:{command.ContextUri}:{command.TrackUri}");
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add("Pause");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add("Resume");
        return Task.CompletedTask;
    }

    public Task SeekAsync(long positionMs, CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add($"Seek:{positionMs}");
        return Task.CompletedTask;
    }

    public Task SkipNextAsync(CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add("SkipNext");
        return Task.CompletedTask;
    }

    public Task SkipPreviousAsync(CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add("SkipPrevious");
        return Task.CompletedTask;
    }

    public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add($"Shuffle:{enabled}");
        return Task.CompletedTask;
    }

    public Task SetRepeatContextAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add($"RepeatContext:{enabled}");
        return Task.CompletedTask;
    }

    public Task SetRepeatTrackAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add($"RepeatTrack:{enabled}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        CommandsReceived.Add("Stop");
        SimulateStop();
        return Task.CompletedTask;
    }
}
