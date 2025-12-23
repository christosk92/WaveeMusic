using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// ITrackStream implementation for Spotify episodes with external URLs.
/// Used when an episode is hosted outside Spotify (e.g., on the publisher's own servers).
/// </summary>
public sealed class SpotifyEpisodeExternalStream : ITrackStream
{
    private readonly Stream _stream;
    private readonly HttpResponseMessage _response;
    private bool _disposed;

    public SpotifyEpisodeExternalStream(
        Stream stream,
        TrackMetadata metadata,
        HttpResponseMessage response)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _response = response ?? throw new ArgumentNullException(nameof(response));
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
    public bool CanSeek => false; // External streams may not support seeking

    /// <inheritdoc/>
    public Task PrefetchForSeekAsync(TimeSpan targetPosition, CancellationToken cancellationToken = default)
    {
        // External HTTP streams don't support prefetch
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        _stream.Dispose();
        _response.Dispose();

        return ValueTask.CompletedTask;
    }
}
