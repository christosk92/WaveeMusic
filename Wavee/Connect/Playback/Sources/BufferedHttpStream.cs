using System.Buffers;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Buffered wrapper around an HTTP response stream.
/// Provides pre-buffering and handles ICY metadata stripping.
/// </summary>
public sealed class BufferedHttpStream : Stream
{
    private readonly Stream _innerStream;
    private readonly int _icyMetaInt;
    private readonly byte[] _buffer;
    private int _bufferPosition;
    private int _bufferLength;
    private int _bytesUntilMetadata;
    private bool _disposed;

    /// <summary>
    /// Minimum buffer size (256KB for smooth streaming).
    /// </summary>
    private const int MinBufferSize = 262144;

    /// <summary>
    /// Fires when ICY metadata is received (song title updates).
    /// </summary>
    public event Action<string>? MetadataReceived;

    /// <summary>
    /// Creates a buffered HTTP stream.
    /// </summary>
    /// <param name="innerStream">The underlying HTTP response stream.</param>
    /// <param name="icyMetaInt">ICY metadata interval in bytes (0 if none).</param>
    /// <param name="bufferSize">Buffer size in bytes.</param>
    public BufferedHttpStream(Stream innerStream, int icyMetaInt = 0, int bufferSize = MinBufferSize)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _icyMetaInt = icyMetaInt;
        _bytesUntilMetadata = icyMetaInt;
        _buffer = new byte[Math.Max(bufferSize, MinBufferSize)];
        _bufferPosition = 0;
        _bufferLength = 0;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false; // Network streams don't support seeking

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count == 0)
            return 0;

        var totalRead = 0;

        while (totalRead < count)
        {
            // Refill buffer if empty
            if (_bufferPosition >= _bufferLength)
            {
                await RefillBufferAsync(cancellationToken);
                if (_bufferLength == 0)
                    break; // End of stream
            }

            // Calculate how much we can read
            var available = _bufferLength - _bufferPosition;
            var toRead = Math.Min(available, count - totalRead);

            // If ICY metadata is enabled, limit read to not cross metadata boundary
            if (_icyMetaInt > 0 && _bytesUntilMetadata > 0)
            {
                toRead = Math.Min(toRead, _bytesUntilMetadata);
            }

            // Copy to output buffer
            Array.Copy(_buffer, _bufferPosition, buffer, offset + totalRead, toRead);
            _bufferPosition += toRead;
            totalRead += toRead;

            // Update metadata tracking
            if (_icyMetaInt > 0)
            {
                _bytesUntilMetadata -= toRead;
                if (_bytesUntilMetadata <= 0)
                {
                    await ReadAndProcessMetadataAsync(cancellationToken);
                    _bytesUntilMetadata = _icyMetaInt;
                }
            }
        }

        return totalRead;
    }

    /// <summary>
    /// Pre-buffers data before playback starts.
    /// </summary>
    /// <param name="targetBytes">Target number of bytes to buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PreBufferAsync(int targetBytes, CancellationToken cancellationToken)
    {
        // Fill buffer until we have enough data
        while (_bufferLength < Math.Min(targetBytes, _buffer.Length) && !cancellationToken.IsCancellationRequested)
        {
            var read = await _innerStream.ReadAsync(
                _buffer.AsMemory(_bufferLength, _buffer.Length - _bufferLength),
                cancellationToken);
            if (read == 0)
                break; // Stream ended
            _bufferLength += read;
        }
    }

    private async Task RefillBufferAsync(CancellationToken cancellationToken)
    {
        _bufferPosition = 0;
        _bufferLength = await _innerStream.ReadAsync(_buffer.AsMemory(), cancellationToken);
    }

    private async Task ReadAndProcessMetadataAsync(CancellationToken cancellationToken)
    {
        // Read metadata length byte (multiply by 16 for actual length)
        var lengthByte = new byte[1];
        var read = await _innerStream.ReadAsync(lengthByte.AsMemory(), cancellationToken);
        if (read == 0)
            return;

        var metadataLength = lengthByte[0] * 16;
        if (metadataLength == 0)
            return;

        // Read metadata
        var metadataBytes = ArrayPool<byte>.Shared.Rent(metadataLength);
        try
        {
            var totalRead = 0;
            while (totalRead < metadataLength)
            {
                read = await _innerStream.ReadAsync(
                    metadataBytes.AsMemory(totalRead, metadataLength - totalRead),
                    cancellationToken);
                if (read == 0)
                    break;
                totalRead += read;
            }

            // Parse metadata string
            var metadata = System.Text.Encoding.UTF8.GetString(metadataBytes, 0, totalRead).TrimEnd('\0');
            if (!string.IsNullOrEmpty(metadata))
            {
                // Extract StreamTitle from metadata
                var title = ExtractStreamTitle(metadata);
                if (!string.IsNullOrEmpty(title))
                {
                    MetadataReceived?.Invoke(title);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(metadataBytes);
        }
    }

    private static string? ExtractStreamTitle(string metadata)
    {
        // Format: StreamTitle='Artist - Song';StreamUrl='...';
        const string prefix = "StreamTitle='";
        var startIndex = metadata.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return null;

        startIndex += prefix.Length;
        var endIndex = metadata.IndexOf("';", startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            endIndex = metadata.IndexOf('\'', startIndex);
        if (endIndex < 0)
            return null;

        return metadata[startIndex..endIndex];
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _innerStream.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
