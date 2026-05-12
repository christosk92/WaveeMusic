namespace Wavee.Local.Playback;

/// <summary>
/// Marker stream that carries an absolute local file path. Decoders such as
/// <see cref="Wavee.AudioHost.Audio.Decoders.BassDecoder"/> detect this wrapper and
/// hand the path to BASS directly via <c>Bass.CreateStream(path, ...)</c>, avoiding
/// the in-memory buffer the URL/byte-array paths require. That keeps multi-GB
/// audiobook / DJ-mix playback bounded on memory.
/// </summary>
public sealed class LocalFilePathStream : Stream
{
    private readonly Stream _inner;

    public string Path { get; }

    public LocalFilePathStream(Stream inner, string path)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Path = path ?? throw new ArgumentNullException(nameof(path));
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
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
