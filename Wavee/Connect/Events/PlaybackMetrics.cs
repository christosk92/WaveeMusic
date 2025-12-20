namespace Wavee.Connect.Events;

/// <summary>
/// Tracks playback metrics for a single track.
/// Used to build TrackTransitionEvent when playback ends.
/// </summary>
/// <remarks>
/// Based on librespot-java's PlaybackMetrics class.
/// Tracks intervals (for seek detection), reasons, and player metrics.
/// </remarks>
public sealed class PlaybackMetrics
{
    private readonly List<Interval> _intervals = new(10);
    private Interval? _currentInterval;

    /// <summary>
    /// Track ID (hex format).
    /// </summary>
    public string TrackId { get; }

    /// <summary>
    /// Unique playback ID (32-char hex).
    /// </summary>
    public string PlaybackId { get; }

    /// <summary>
    /// Context URI (playlist, album, or track itself).
    /// </summary>
    public string ContextUri { get; }

    /// <summary>
    /// Feature version from play origin.
    /// </summary>
    public string FeatureVersion { get; }

    /// <summary>
    /// Referrer identifier from play origin.
    /// </summary>
    public string ReferrerIdentifier { get; }

    /// <summary>
    /// Timestamp when playback started (Unix ms).
    /// </summary>
    public long Timestamp { get; }

    /// <summary>
    /// Reason playback started.
    /// </summary>
    public PlaybackReason? ReasonStart { get; set; }

    /// <summary>
    /// Source that started playback (e.g., device ID).
    /// </summary>
    public string SourceStart { get; set; } = "unknown";

    /// <summary>
    /// Reason playback ended.
    /// </summary>
    public PlaybackReason? ReasonEnd { get; set; }

    /// <summary>
    /// Source that ended playback.
    /// </summary>
    public string SourceEnd { get; set; } = "unknown";

    /// <summary>
    /// Player metrics (set when available).
    /// </summary>
    public PlayerMetrics? Player { get; set; }

    /// <summary>
    /// Creates a new PlaybackMetrics instance.
    /// </summary>
    public PlaybackMetrics(
        string trackId,
        string playbackId,
        string contextUri,
        string featureVersion = "",
        string referrerIdentifier = "")
    {
        TrackId = trackId;
        PlaybackId = playbackId;
        ContextUri = contextUri;
        FeatureVersion = featureVersion;
        ReferrerIdentifier = referrerIdentifier;
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Starts tracking a new playback interval.
    /// </summary>
    public void StartInterval(int positionMs)
    {
        _currentInterval = new Interval(positionMs);
    }

    /// <summary>
    /// Ends the current playback interval.
    /// </summary>
    public void EndInterval(int positionMs)
    {
        if (_currentInterval == null)
            return;

        // Skip zero-length intervals
        if (_currentInterval.Begin == positionMs)
        {
            _currentInterval = null;
            return;
        }

        _currentInterval.End = positionMs;
        _intervals.Add(_currentInterval);
        _currentInterval = null;
    }

    /// <summary>
    /// Gets the first position value (start of first interval, or 0).
    /// </summary>
    public int FirstValue => _intervals.Count > 0 ? _intervals[0].Begin : 0;

    /// <summary>
    /// Gets the last position value (end of last interval, or duration).
    /// </summary>
    public int LastValue => _intervals.Count > 0
        ? _intervals[^1].End
        : (Player?.Duration ?? 0);

    /// <summary>
    /// Interval tracking for seek detection.
    /// </summary>
    private sealed class Interval
    {
        public int Begin { get; }
        public int End { get; set; } = -1;

        public Interval(int begin)
        {
            Begin = begin;
        }
    }
}

/// <summary>
/// Player-specific metrics captured during playback.
/// </summary>
public sealed class PlayerMetrics
{
    /// <summary>
    /// Content metrics (file ID, audio key timing).
    /// </summary>
    public ContentMetrics? ContentMetrics { get; init; }

    /// <summary>
    /// Decoded audio length in bytes.
    /// </summary>
    public int DecodedLength { get; init; }

    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// Audio bitrate (e.g., 320000 for 320kbps).
    /// </summary>
    public int Bitrate { get; init; }

    /// <summary>
    /// Sample rate (e.g., 44100).
    /// </summary>
    public float SampleRate { get; init; }

    /// <summary>
    /// Track duration in milliseconds.
    /// </summary>
    public int Duration { get; init; }

    /// <summary>
    /// Audio encoding ("vorbis", "mp3", "aac").
    /// </summary>
    public string Encoding { get; init; } = "vorbis";

    /// <summary>
    /// Fade overlap in milliseconds (for crossfade).
    /// </summary>
    public int FadeOverlap { get; init; }

    /// <summary>
    /// Transition type ("none" or "crossfade").
    /// </summary>
    public string Transition { get; init; } = "none";

    /// <summary>
    /// Time to decrypt in milliseconds.
    /// </summary>
    public int DecryptTime { get; init; }
}
