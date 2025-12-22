using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// ITrackStream implementation for local audio files.
/// </summary>
public sealed class LocalFileTrackStream : ITrackStream
{
    private readonly string _filePath;
    private FileStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Creates a new local file track stream.
    /// </summary>
    /// <param name="filePath">Path to the local audio file.</param>
    /// <param name="metadata">Track metadata extracted from the file.</param>
    public LocalFileTrackStream(string filePath, TrackMetadata metadata)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    /// <inheritdoc/>
    public TrackMetadata Metadata { get; }

    /// <inheritdoc/>
    public AudioFormat? KnownFormat => null; // Let decoder detect from magic bytes

    /// <inheritdoc/>
    public bool CanSeek => true; // Local files always support seeking

    /// <inheritdoc/>
    public Stream AudioStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _stream ??= new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
    }

    /// <inheritdoc/>
    public Task PrefetchForSeekAsync(TimeSpan targetPosition, CancellationToken cancellationToken = default)
    {
        // No prefetch needed for local files - data is always available
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        if (_stream != null)
        {
            _stream.Dispose();
            _stream = null;
        }

        return ValueTask.CompletedTask;
    }
}
