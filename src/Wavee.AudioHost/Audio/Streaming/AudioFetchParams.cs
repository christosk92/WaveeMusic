namespace Wavee.AudioHost.Audio.Streaming;

/// <summary>
/// Configuration parameters for audio progressive download.
/// </summary>
public sealed record AudioFetchParams
{
    /// <summary>
    /// Minimum size of each HTTP range request in bytes.
    /// 64 KB matches librespot's MINIMUM_DOWNLOAD_SIZE — small enough that a
    /// post-seek fetch resumes audio quickly (~16 ms wire time on a healthy
    /// link, large enough to cover NVorbis pre-roll of 2 packets ≈ 8–32 KB).
    /// </summary>
    public int MinimumChunkSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum size of each HTTP range request in bytes.
    /// Capped at 256 KB so a single seek wastes at most one chunk of bytes
    /// when cancellation lands mid-flight.
    /// </summary>
    public int MaximumChunkSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Bytes to prefetch before playback starts (for instant start).
    /// Default: 256KB (enough for ~2 seconds at 320kbps)
    /// </summary>
    public int InitialPrefetchBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Seconds of audio to keep buffered ahead during playback.
    /// 5 s mirrors librespot's during-playback default. Combined with the
    /// 10 s MinBufferAhead floor below it gives ~10 s of jitter tolerance
    /// without the 15 s post-seek fetch storm the older value caused.
    /// </summary>
    public TimeSpan ReadAheadDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of retries for failed HTTP requests.
    /// Default: 3
    /// </summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>
    /// Initial delay before first retry (doubles each attempt).
    /// Default: 1 second
    /// </summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Timeout for individual HTTP requests.
    /// Default: 8 seconds
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Duration after which a stalled read triggers buffering state.
    /// Default: 500ms
    /// </summary>
    public TimeSpan BufferingThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Minimum buffer ahead when connection is fast (>500 KB/s).
    /// Default: 10 seconds
    /// </summary>
    public TimeSpan MinBufferAhead { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum buffer ahead when connection is slow (&lt;100 KB/s).
    /// Default: 45 seconds
    /// </summary>
    public TimeSpan MaxBufferAhead { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Default fetch parameters.
    /// </summary>
    public static AudioFetchParams Default { get; } = new();
}

/// <summary>
/// Buffer state for UI feedback.
/// </summary>
public enum BufferState
{
    /// <summary>
    /// Actively downloading data, playback may pause.
    /// </summary>
    Buffering,

    /// <summary>
    /// Sufficient data buffered, playback is smooth.
    /// </summary>
    Ready,

    /// <summary>
    /// Download stalled due to network issues.
    /// </summary>
    Stalled
}

/// <summary>
/// Current buffer status for UI display.
/// </summary>
/// <param name="State">Current buffer state.</param>
/// <param name="BufferedBytes">Bytes buffered ahead of current position.</param>
/// <param name="TotalFileSize">Total file size in bytes.</param>
/// <param name="DownloadedBytes">Total bytes downloaded.</param>
/// <param name="ThroughputBytesPerSec">Current download speed.</param>
public readonly record struct BufferStatus(
    BufferState State,
    long BufferedBytes,
    long TotalFileSize,
    long DownloadedBytes,
    int ThroughputBytesPerSec)
{
    /// <summary>
    /// Percentage of file downloaded (0-100).
    /// </summary>
    public double PercentDownloaded => TotalFileSize > 0
        ? (double)DownloadedBytes / TotalFileSize * 100
        : 0;

    /// <summary>
    /// Estimated buffered duration based on bitrate.
    /// </summary>
    public TimeSpan EstimatedBufferedDuration(int bitrateBps) =>
        bitrateBps > 0
            ? TimeSpan.FromSeconds((double)BufferedBytes * 8 / bitrateBps)
            : TimeSpan.Zero;
}

/// <summary>
/// Download error information for retry handling.
/// </summary>
/// <param name="Exception">The exception that occurred.</param>
/// <param name="RetryCount">Current retry attempt number.</param>
/// <param name="WillRetry">Whether another retry will be attempted.</param>
public readonly record struct DownloadError(
    Exception Exception,
    int RetryCount,
    bool WillRetry);
