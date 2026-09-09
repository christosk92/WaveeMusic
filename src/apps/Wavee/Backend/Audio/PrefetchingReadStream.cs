using System;
using System.IO;
using System.Threading;
using Wavee.Backend;

namespace Wavee.Backend.Audio;

/// <summary>Decoder view that never converts temporary starvation into EOF. Availability waits
/// are notified by the producer and cancellation interrupts the wait itself.</summary>
internal sealed class PrefetchingReadStream : Stream
{
    readonly CancellationToken _cancellationToken;
    readonly IAudioReadStream _reader;
    readonly Stream _inner;
    readonly long _skip;
    readonly bool _leaveOpen;
    bool _disposed;

    public PrefetchingReadStream(IAudioReadStream reader, long skip, CancellationToken cancellationToken = default, bool leaveOpen = false)
    {
        _reader = reader;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _inner = reader.AsStream();
        _skip = skip;
        _inner.Seek(skip, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0) return 0;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long version = _reader.DataVersion;
            int n = _reader.TryRead(buffer, out bool wouldBlock);
            if (n > 0 || !wouldBlock) return n;
            _reader.WaitForData(version, _cancellationToken);
        }
    }

    public override long Length => Math.Max(0, _inner.Length - _skip);
    public override long Position
    {
        get => _inner.Position - _skip;
        set => _inner.Position = value + _skip;
    }
    public override long Seek(long offset, SeekOrigin origin) => origin switch
    {
        SeekOrigin.Begin => _inner.Seek(offset + _skip, SeekOrigin.Begin) - _skip,
        SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _skip,
        SeekOrigin.End => _inner.Seek(offset, SeekOrigin.End) - _skip,
        _ => _inner.Position - _skip,
    };
    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_leaveOpen) _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Pure mapping from the negotiated <see cref="AudioFormat"/> rung to an approximate bits/sec figure for
/// <see cref="IAudioReadStream.ConfigureReadAhead"/>. The seam has no bitrate of its own to read, and
/// <c>knownSize</c> is null at every production attach site, so size÷duration is not a usable substitute. The three
/// Ogg Vorbis rungs are Spotify's own fixed nominal rates; the FLAC/FLAC24 figures are rough lossless ceilings —
/// real content varies, but <c>ReadAheadPolicy.Compute</c> only needs "roughly how many bytes per second" to size
/// its window, never an exact figure. MP3 and AAC report 0 ("unknown") because their bitrate is per-file (CBR/VBR)
/// and not knowable from the format alone; <c>ReadAheadPolicy.Compute</c> already treats &lt;= 0 as "fall back to
/// measured throughput" (<c>RangedHttpSource.cs</c>).
/// </summary>
internal static class AudioBitratePolicy
{
    public static int BitsPerSecond(AudioFormat format) => format switch
    {
        AudioFormat.OggVorbis96 => 96_000,
        AudioFormat.OggVorbis160 => 160_000,
        AudioFormat.OggVorbis320 => 320_000,
        AudioFormat.Flac => 1_000_000,
        AudioFormat.Flac24 => 1_800_000,
        _ => 0,   // Mp3, Aac: per-file CBR/VBR — unknown from the format alone
    };
}
