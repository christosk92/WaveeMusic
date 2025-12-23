using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// ITrackStream implementation for podcast episodes.
/// Supports both local files and HTTP streams with resume position.
/// </summary>
public sealed class PodcastTrackStream : ITrackStream
{
    private readonly Stream _stream;
    private readonly HttpResponseMessage? _httpResponse;
    private readonly bool _isLocalFile;
    private bool _disposed;

    /// <summary>
    /// Creates a new podcast track stream.
    /// </summary>
    /// <param name="stream">The audio stream (file or HTTP).</param>
    /// <param name="metadata">Track metadata.</param>
    /// <param name="resumePositionMs">Position to resume playback from.</param>
    /// <param name="isLocalFile">Whether this is a local file.</param>
    /// <param name="httpResponse">HTTP response to dispose (for HTTP streams).</param>
    public PodcastTrackStream(
        Stream stream,
        TrackMetadata metadata,
        long resumePositionMs,
        bool isLocalFile,
        HttpResponseMessage? httpResponse = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        ResumePositionMs = resumePositionMs;
        _isLocalFile = isLocalFile;
        _httpResponse = httpResponse;
    }

    /// <inheritdoc/>
    public Stream AudioStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stream;
        }
    }

    /// <inheritdoc/>
    public TrackMetadata Metadata { get; }

    /// <inheritdoc/>
    public AudioFormat? KnownFormat => null; // Let decoder detect format

    /// <inheritdoc/>
    public bool CanSeek => _isLocalFile || _stream.CanSeek;

    /// <summary>
    /// Gets the resume position in milliseconds.
    /// </summary>
    public long ResumePositionMs { get; }

    /// <inheritdoc/>
    public Task PrefetchForSeekAsync(TimeSpan targetPosition, CancellationToken cancellationToken = default)
    {
        // For local files, no prefetch needed
        // For HTTP streams, the BufferedHttpStream handles buffering
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        _stream.Dispose();
        _httpResponse?.Dispose();

        return ValueTask.CompletedTask;
    }
}
