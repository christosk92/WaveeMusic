namespace Wavee.Connect.Playback.Decoders;

/// <summary>
/// Stream wrapper that skips a specified number of bytes at the beginning.
/// </summary>
/// <remarks>
/// Used for Spotify audio files which have a 0xa7 (167 byte) header that must be
/// skipped before the OGG Vorbis data begins.
/// </remarks>
public sealed class SkipStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _skipBytes;
    private readonly bool _leaveOpen;
    private bool _disposed;

    /// <summary>
    /// Creates a new SkipStream.
    /// </summary>
    /// <param name="innerStream">The inner stream to wrap.</param>
    /// <param name="skipBytes">Number of bytes to skip at the beginning.</param>
    /// <param name="leaveOpen">Whether to leave the inner stream open when disposed.</param>
    public SkipStream(Stream innerStream, long skipBytes, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        if (skipBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(skipBytes), "Skip bytes must be non-negative");

        _innerStream = innerStream;
        _skipBytes = skipBytes;
        _leaveOpen = leaveOpen;

        // Seek inner stream to skip position if it supports seeking
        if (_innerStream.CanSeek && _innerStream.Position < _skipBytes)
        {
            _innerStream.Position = _skipBytes;
        }
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => false;

    public override long Length => _innerStream.CanSeek
        ? Math.Max(0, _innerStream.Length - _skipBytes)
        : throw new NotSupportedException();

    public override long Position
    {
        get => _innerStream.CanSeek
            ? Math.Max(0, _innerStream.Position - _skipBytes)
            : throw new NotSupportedException();
        set
        {
            if (!_innerStream.CanSeek)
                throw new NotSupportedException();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            _innerStream.Position = value + _skipBytes;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureNotDisposed();

        // Ensure we're past the skip region
        if (_innerStream.CanSeek && _innerStream.Position < _skipBytes)
        {
            _innerStream.Position = _skipBytes;
        }

        return _innerStream.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        EnsureNotDisposed();

        if (_innerStream.CanSeek && _innerStream.Position < _skipBytes)
        {
            _innerStream.Position = _skipBytes;
        }

        return _innerStream.Read(buffer);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        if (_innerStream.CanSeek && _innerStream.Position < _skipBytes)
        {
            _innerStream.Position = _skipBytes;
        }

        return await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        if (_innerStream.CanSeek && _innerStream.Position < _skipBytes)
        {
            _innerStream.Position = _skipBytes;
        }

        return await _innerStream.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureNotDisposed();

        if (!_innerStream.CanSeek)
            throw new NotSupportedException();

        // Translate seek to inner stream coordinates
        var innerOffset = origin switch
        {
            SeekOrigin.Begin => offset + _skipBytes,
            SeekOrigin.Current => offset,
            SeekOrigin.End => offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        var newInnerPos = _innerStream.Seek(innerOffset, origin);

        // Clamp to skip region
        if (newInnerPos < _skipBytes)
        {
            _innerStream.Position = _skipBytes;
            return 0;
        }

        return newInnerPos - _skipBytes;
    }

    public override void Flush()
    {
        // Read-only stream, nothing to flush
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (!_leaveOpen)
        {
            await _innerStream.DisposeAsync();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
