namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// A stream wrapper that carries the source URL.
/// Allows decoders like BASS to use native URL streaming when available.
/// </summary>
public sealed class UrlAwareStream : Stream
{
    private readonly Stream _inner;

    /// <summary>
    /// Gets the source URL for this stream.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Creates a URL-aware stream wrapper.
    /// </summary>
    /// <param name="inner">The underlying stream.</param>
    /// <param name="url">The source URL.</param>
    public UrlAwareStream(Stream inner, string url)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Url = url ?? throw new ArgumentNullException(nameof(url));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
