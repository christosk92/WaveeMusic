namespace Wavee.Core.Audio.Download;

/// <summary>
/// Configuration parameters for audio progressive download.
/// </summary>
public sealed record AudioFetchParams
{
    /// <summary>
    /// Minimum size of each HTTP range request in bytes.
    /// Default: 64KB
    /// </summary>
    public int MinimumChunkSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum size of each HTTP range request in bytes.
    /// Default: 256KB
    /// </summary>
    public int MaximumChunkSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Bytes to prefetch before playback starts (for instant start).
    /// Default: 128KB (enough for ~1 second at 320kbps)
    /// </summary>
    public int InitialPrefetchBytes { get; init; } = 128 * 1024;

    /// <summary>
    /// Seconds of audio to keep buffered ahead during playback.
    /// Default: 5 seconds
    /// </summary>
    public TimeSpan ReadAheadDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of retries for failed HTTP requests.
    /// Default: 3
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Initial delay before first retry (doubles each attempt).
    /// Default: 1 second
    /// </summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Timeout for individual HTTP requests.
    /// Default: 8 seconds
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Duration after which a stalled read triggers buffering state.
    /// Default: 500ms
    /// </summary>
    public TimeSpan BufferingThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

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
