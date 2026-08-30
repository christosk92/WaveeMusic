using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wavee.Sdk;

namespace Wavee.Tests.Sdk;

/// <summary>
/// A one-way in-memory pipe: one side writes, the other reads. Two of them make the duplex pair a
/// <see cref="Wavee.Sdk.Protocol.JsonRpcConnection"/> needs, without spawning a process.
/// </summary>
internal sealed class MemoryPipe : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Signals a clean end of stream to the reader.</summary>
    public void CompleteWriting() => _chunks.Writer.TryComplete();

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
        => _chunks.Writer.TryWrite(buffer.ToArray());

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_current is not null && _position < _current.Length)
            {
                int n = Math.Min(buffer.Length, _current.Length - _position);
                _current.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            _current = null;
            _position = 0;
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (_chunks.Reader.TryRead(out byte[]? next)) _current = next;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A read-only stream that hands out exactly one scripted chunk per <c>ReadAsync</c> call.</summary>
internal sealed class ScriptedReadStream(IEnumerable<byte[]> chunks) : Stream
{
    private readonly Queue<byte[]> _chunks = new(chunks);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>How many times the framer asked for bytes.</summary>
    public int ReadCalls { get; private set; }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        if (_chunks.Count == 0) return ValueTask.FromResult(0);

        byte[] chunk = _chunks.Dequeue();
        int n = Math.Min(buffer.Length, chunk.Length);
        chunk.AsMemory(0, n).CopyTo(buffer);
        return ValueTask.FromResult(n);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>An <see cref="IModuleStream"/> over a byte array — the "module serves the bytes itself" path.</summary>
internal sealed class ByteArrayModuleStream(byte[] bytes, string? contentType, bool seekable = true) : IModuleStream
{
    /// <summary>True once the runner disposed the handle (what <c>stream/close</c> must do).</summary>
    public bool Disposed { get; private set; }

    public long? Length => bytes.Length;

    public bool Seekable => seekable;

    public string? ContentType => contentType;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (offset >= bytes.Length) return ValueTask.FromResult(0);

        int n = (int)Math.Min(dst.Length, bytes.Length - offset);
        bytes.AsMemory((int)offset, n).CopyTo(dst);
        return ValueTask.FromResult(n);
    }

    public void Dispose() => Disposed = true;
}
