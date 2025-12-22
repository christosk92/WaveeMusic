using System.Reactive.Linq;
using System.Reactive.Subjects;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// ITrackStream implementation for HTTP audio streams.
/// Supports both finite (direct file) and infinite (radio) streams.
/// </summary>
public sealed class HttpStreamTrackStream : ITrackStream
{
    private readonly BufferedHttpStream _bufferedStream;
    private readonly UrlAwareStream _urlAwareStream;
    private readonly HttpResponseMessage _response;
    private readonly bool _isInfinite;
    private readonly Subject<string> _titleSubject = new();
    private TrackMetadata _metadata;
    private bool _disposed;

    /// <summary>
    /// Creates a new HTTP stream track stream.
    /// </summary>
    /// <param name="response">The HTTP response containing the audio stream.</param>
    /// <param name="stream">The buffered stream wrapper.</param>
    /// <param name="metadata">Initial track metadata.</param>
    /// <param name="isInfinite">Whether this is an infinite stream (radio).</param>
    /// <param name="sourceUrl">The original URL this stream was loaded from.</param>
    public HttpStreamTrackStream(
        HttpResponseMessage response,
        BufferedHttpStream stream,
        TrackMetadata metadata,
        bool isInfinite,
        string sourceUrl)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
        _bufferedStream = stream ?? throw new ArgumentNullException(nameof(stream));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _isInfinite = isInfinite;

        // Wrap in UrlAwareStream so decoders can use native URL streaming
        _urlAwareStream = new UrlAwareStream(stream, sourceUrl);

        // Subscribe to ICY metadata updates
        _bufferedStream.MetadataReceived += OnMetadataReceived;
    }

    /// <inheritdoc/>
    public TrackMetadata Metadata => _metadata;

    /// <inheritdoc/>
    public AudioFormat? KnownFormat => null; // Let decoder detect from magic bytes

    /// <inheritdoc/>
    public bool CanSeek => !_isInfinite; // Only finite streams support seeking

    /// <inheritdoc/>
    public Stream AudioStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _urlAwareStream;
        }
    }

    /// <summary>
    /// Observable for stream title changes (ICY metadata updates).
    /// </summary>
    public IObservable<string> TitleChanges => _titleSubject.AsObservable();

    /// <summary>
    /// Gets whether this is an infinite stream (radio).
    /// </summary>
    public bool IsInfinite => _isInfinite;

    /// <inheritdoc/>
    public Task PrefetchForSeekAsync(TimeSpan targetPosition, CancellationToken cancellationToken = default)
    {
        // HTTP streams don't support seeking (for infinite) or need prefetch (for finite)
        return Task.CompletedTask;
    }

    private void OnMetadataReceived(string title)
    {
        // Update metadata with new title
        _metadata = _metadata with { Title = title };
        _titleSubject.OnNext(title);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        _titleSubject.OnCompleted();
        _titleSubject.Dispose();
        _urlAwareStream.Dispose();
        _response.Dispose();

        return ValueTask.CompletedTask;
    }
}
