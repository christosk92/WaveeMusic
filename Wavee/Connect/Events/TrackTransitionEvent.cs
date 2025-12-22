namespace Wavee.Connect.Events;

/// <summary>
/// Track transition event - THE CRITICAL EVENT for artist payouts.
/// Sent when a track ends or transitions to another track.
/// </summary>
/// <remarks>
/// Based on librespot-java's TrackTransitionEvent.
/// This event contains 40+ fields that report exactly what was played.
/// </remarks>
public sealed class TrackTransitionEvent : IPlaybackEvent
{
    private static int _incrementalCounter;

    private readonly string _deviceId;
    private readonly string _lastCommandSentByDeviceId;
    private readonly PlaybackMetrics _metrics;

    /// <summary>
    /// Gets the playback metrics for this transition.
    /// Exposed for local subscribers (like LibraryPlayRecorder) to access play data.
    /// </summary>
    public PlaybackMetrics Metrics => _metrics;

    /// <summary>
    /// Creates a new TrackTransitionEvent.
    /// </summary>
    /// <param name="deviceId">This device's ID.</param>
    /// <param name="lastCommandSentByDeviceId">Device ID that sent the last command.</param>
    /// <param name="metrics">Playback metrics for the track.</param>
    public TrackTransitionEvent(
        string deviceId,
        string lastCommandSentByDeviceId,
        PlaybackMetrics metrics)
    {
        _deviceId = deviceId;
        _lastCommandSentByDeviceId = lastCommandSentByDeviceId;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public EventBuilder Build()
    {
        if (_metrics.Player?.ContentMetrics == null)
            throw new InvalidOperationException("Cannot build TrackTransitionEvent without player metrics");

        var endPosition = _metrics.LastValue;
        var player = _metrics.Player;
        var content = player.ContentMetrics;

        var builder = new EventBuilder(EventType.TrackTransition);

        // Field 1: Incremental counter
        builder.Append(Interlocked.Increment(ref _incrementalCounter));

        // Field 2: Device ID
        builder.Append(_deviceId);

        // Field 3: Playback ID + 32 zeros
        builder.Append(_metrics.PlaybackId + "00000000000000000000000000000000");

        // Fields 4-5: Source and reason for start
        builder.Append(_metrics.SourceStart);
        builder.Append(_metrics.ReasonStart?.ToEventValue());

        // Fields 6-7: Source and reason for end
        builder.Append(_metrics.SourceEnd);
        builder.Append(_metrics.ReasonEnd?.ToEventValue());

        // Fields 8-9: Decoded length and size
        builder.Append(player.DecodedLength);
        builder.Append(player.Size);

        // Fields 10-11: End position (twice)
        builder.Append(endPosition);
        builder.Append(endPosition);

        // Field 12: Duration
        builder.Append(player.Duration);

        // Fields 13-16: Decrypt time, fade overlap, 0, 0
        builder.Append(player.DecryptTime);
        builder.Append(player.FadeOverlap);
        builder.Append('0');
        builder.Append('0');

        // Fields 17-18: Started from zero flag, first position
        builder.Append(_metrics.FirstValue == 0 ? '0' : '1');
        builder.Append(_metrics.FirstValue);

        // Fields 19-21: 0, -1, "context"
        builder.Append('0');
        builder.Append("-1");
        builder.Append("context");

        // Fields 22-23: Audio key time, 0
        builder.Append(content.AudioKeyTime);
        builder.Append('0');

        // Fields 24-27: Preloaded audio key flag, 0, 0, 0
        builder.Append(content.PreloadedAudioKey);
        builder.Append('0');
        builder.Append('0');
        builder.Append('0');

        // Fields 28-29: End position (twice again)
        builder.Append(endPosition);
        builder.Append(endPosition);

        // Fields 30-31: 0, bitrate
        builder.Append('0');
        builder.Append(player.Bitrate);

        // Fields 32-33: Context URI, encoding
        builder.Append(_metrics.ContextUri);
        builder.Append(player.Encoding);

        // Fields 34-35: Track hex ID (if gid), empty
        builder.Append(_metrics.TrackId);
        builder.Append("");

        // Fields 36-38: 0, timestamp, 0
        builder.Append('0');
        builder.Append(_metrics.Timestamp);
        builder.Append('0');

        // Fields 39-41: "context", referrer, feature version
        builder.Append("context");
        builder.Append(_metrics.ReferrerIdentifier);
        builder.Append(_metrics.FeatureVersion);

        // Fields 42-44: "com.spotify", transition, "none"
        builder.Append("com.spotify");
        builder.Append(player.Transition);
        builder.Append("none");

        // Fields 45-47: Last command device ID, "na", "none"
        builder.Append(_lastCommandSentByDeviceId);
        builder.Append("na");
        builder.Append("none");

        return builder;
    }
}
