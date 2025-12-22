using System.Buffers;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Decoders;

/// <summary>
/// Registry for audio decoders with automatic format detection.
/// </summary>
public sealed class AudioDecoderRegistry
{
    private readonly List<IAudioDecoder> _decoders = new();

    /// <summary>
    /// Size of header buffer for format detection on non-seekable streams.
    /// Must be large enough for all decoder format checks.
    /// </summary>
    private const int HeaderBufferSize = 256;

    /// <summary>
    /// Registers an audio decoder.
    /// </summary>
    public void Register(IAudioDecoder decoder)
    {
        _decoders.Add(decoder);
    }

    /// <summary>
    /// Finds a decoder that can decode the given stream.
    /// For non-seekable streams, returns a wrapped stream that prepends buffered header bytes.
    /// </summary>
    /// <param name="stream">Input stream to check.</param>
    /// <param name="wrappedStream">Output stream to use for decoding (may be different from input for non-seekable streams).</param>
    /// <returns>Decoder that can handle the stream, or null if none found.</returns>
    public IAudioDecoder? FindDecoder(Stream stream, out Stream wrappedStream)
    {
        // For non-seekable streams (HTTP radio), buffer header bytes for detection
        // then prepend them back for the decoder
        if (!stream.CanSeek)
        {
            // Read header bytes for format detection
            var headerBuffer = new byte[HeaderBufferSize];
            var totalRead = 0;
            while (totalRead < HeaderBufferSize)
            {
                var read = stream.Read(headerBuffer, totalRead, HeaderBufferSize - totalRead);
                if (read == 0) break; // End of stream
                totalRead += read;
            }

            if (totalRead == 0)
            {
                wrappedStream = stream;
                return null;
            }

            // Create a memory stream from the header for format detection
            var headerStream = new MemoryStream(headerBuffer, 0, totalRead, writable: false);

            foreach (var decoder in _decoders)
            {
                headerStream.Position = 0;
                if (decoder.CanDecode(headerStream))
                {
                    // Found a decoder - wrap the stream to prepend the header bytes
                    wrappedStream = new PrefixedStream(headerBuffer, totalRead, stream);
                    return decoder;
                }
            }

            // No decoder found - still return a prefixed stream in case caller wants to try anyway
            wrappedStream = new PrefixedStream(headerBuffer, totalRead, stream);
            return null;
        }

        // For seekable streams, reset position between decoder checks
        var originalPosition = stream.Position;

        foreach (var decoder in _decoders)
        {
            stream.Position = originalPosition;
            if (decoder.CanDecode(stream))
            {
                stream.Position = originalPosition;
                wrappedStream = stream;
                return decoder;
            }
        }

        stream.Position = originalPosition;
        wrappedStream = stream;
        return null;
    }

    /// <summary>
    /// Finds a decoder that can decode the given stream.
    /// </summary>
    [Obsolete("Use FindDecoder(Stream, out Stream) for proper non-seekable stream handling")]
    public IAudioDecoder? FindDecoder(Stream stream)
    {
        return FindDecoder(stream, out _);
    }

    /// <summary>
    /// Gets the audio format from the stream using the appropriate decoder.
    /// For non-seekable streams, also returns a wrapped stream to use for decoding.
    /// </summary>
    public async Task<(IAudioDecoder decoder, AudioFormat format, Stream decodingStream)> DetectFormatAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var decoder = FindDecoder(stream, out var wrappedStream);
        if (decoder == null)
            throw new NotSupportedException("No decoder found for audio format");

        var format = await decoder.GetFormatAsync(wrappedStream, cancellationToken);
        return (decoder, format, wrappedStream);
    }
}

/// <summary>
/// A stream that prepends buffered bytes to another stream.
/// Used for non-seekable streams where we need to peek at the header for format detection
/// but still provide those bytes to the decoder.
/// </summary>
public sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly int _prefixLength;
    private readonly Stream _innerStream;
    private int _prefixPosition;
    private bool _disposed;

    /// <summary>
    /// Gets the inner stream that this stream wraps.
    /// </summary>
    public Stream InnerStream => _innerStream;

    public PrefixedStream(byte[] prefix, int prefixLength, Stream innerStream)
    {
        _prefix = prefix;
        _prefixLength = prefixLength;
        _innerStream = innerStream;
        _prefixPosition = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PrefixedStream));

        var totalRead = 0;

        // First, read from prefix buffer
        if (_prefixPosition < _prefixLength)
        {
            var prefixRemaining = _prefixLength - _prefixPosition;
            var toCopy = Math.Min(prefixRemaining, count);
            Array.Copy(_prefix, _prefixPosition, buffer, offset, toCopy);
            _prefixPosition += toCopy;
            totalRead += toCopy;
            offset += toCopy;
            count -= toCopy;
        }

        // Then read from inner stream if we need more
        if (count > 0)
        {
            var innerRead = _innerStream.Read(buffer, offset, count);
            totalRead += innerRead;
        }

        return totalRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PrefixedStream));

        var totalRead = 0;

        // First, read from prefix buffer
        if (_prefixPosition < _prefixLength)
        {
            var prefixRemaining = _prefixLength - _prefixPosition;
            var toCopy = Math.Min(prefixRemaining, count);
            Array.Copy(_prefix, _prefixPosition, buffer, offset, toCopy);
            _prefixPosition += toCopy;
            totalRead += toCopy;
            offset += toCopy;
            count -= toCopy;
        }

        // Then read from inner stream if we need more
        if (count > 0)
        {
            var innerRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            totalRead += innerRead;
        }

        return totalRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
