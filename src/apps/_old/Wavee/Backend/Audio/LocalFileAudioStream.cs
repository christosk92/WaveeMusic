using System;
using System.IO;
using System.Threading;

namespace Wavee.Backend.Audio;

/// <summary>Seekable local-file audio stream — the FileStream-backed sibling of <see cref="PlainHttpAudioStream"/>. Same
/// <see cref="IAudioReadStream"/> contract, so the one decode loop drives it unchanged; it is simply the degenerate case
/// of that contract, where every byte is already "attached", the size is known up front, and there is no read-ahead to
/// pause (a local read never blocks on a network, so the pause/resume verbs the ranged sources use are no-ops).
/// <para>Opened <c>FileShare.ReadWrite | Delete</c> deliberately: the user owns this file, and a play must never take a
/// lock that stops them from moving or replacing it while it is playing.</para></summary>
public sealed class LocalFileAudioStream : Stream, IAsyncDisposable, IAudioReadStream
{
    readonly FileStream _file;
    readonly long _length;
    bool _disposed;

    LocalFileAudioStream(FileStream file)
    {
        _file = file;
        _length = file.Length;
    }

    /// <summary>The absolute path this stream reads (for logs / diagnostics).</summary>
    public string Path => _file.Name;

    /// <summary>Open the file for sequential-with-seek decoding. Throws the usual IO exceptions (missing / locked /
    /// unreadable) — the caller surfaces them through the typed playback-error path.</summary>
    public static LocalFileAudioStream Open(string path)
    {
        var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Never lock the user's own file: they may move, rename or replace it while it plays.
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 64 * 1024,
        });
        return new LocalFileAudioStream(file);
    }

    // ── IAudioReadStream — a local file has no clear head, no deferred body and no read-ahead to throttle ────────────
    public Stream AsStream() => this;
    public long CurrentOffset => _file.CanSeek ? _file.Position : 0;
    public bool IsBodyAttached => true;
    public long KnownSize => _length;
    public int ClearHeadLength => 0;
    public IDisposable PauseReadAhead() => NullScope.Instance;
    public void ResumeReadAheadAtCurrentOffset() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return count <= 0 ? 0 : _file.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _file.Read(buffer);
    }

    public override long Length => _length;

    public override long Position
    {
        get => _file.Position;
        set => _file.Position = Math.Clamp(value, 0, _length);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _file.Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        _file.Position = Math.Clamp(next, 0, _length);
        return _file.Position;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _file.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
