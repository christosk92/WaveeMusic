using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee.Backend.Modules;

// ── MODULE-SERVED BYTES — the audio path across the process boundary ─────────────────────────────────────────────────
// The `stream` locator: the module owns the bytes (it decrypts them, it holds the key, it talks to its own CDN) and the
// app pulls RANGES over `stream/open` → `stream/read` → `stream/close`. A read is answered with a RAW BINARY FRAME —
// no base64, no JSON parse on the audio path — so a 320 kbps Ogg costs one 64 KiB round trip per ~1.6 s of audio.
//
// Shape-wise this is the ranged sibling of PlainHttpAudioStream: 256 KiB read-ahead, `KnownSize` from the open answer,
// seek = "read from a different offset" (when the module says it can). A short read is NORMAL (the module answers with
// whatever it has); the fill loop keeps asking until it has something or hits EOF.

/// <summary>A <see cref="Stream"/> over one module-served byte stream.</summary>
public sealed class ModuleByteStream : Stream, IAudioReadStream
{
    /// <summary>How far ahead of the decoder one fetch reaches. Matches the ranged HTTP source's read-ahead.</summary>
    public const int ReadAheadBytes = 256 * 1024;

    /// <summary>Bytes asked for in a single <c>stream/read</c>. Small enough to keep the first read instant, large
    /// enough that a full-rate FLAC needs well under three round trips a second.</summary>
    public const int ChunkBytes = 64 * 1024;

    readonly ModuleProcess _process;
    readonly string _handle;
    readonly IDisposable _lease;
    readonly byte[] _buffer = new byte[ReadAheadBytes];
    readonly Lock _gate = new();

    long _position;
    long _bufferStart = -1;
    int _bufferLength;
    long _knownSize;
    bool _eofSeen;
    readonly AudioDataAvailability _availability = new();
    readonly CancellationTokenSource _lifetime = new();
    bool _fetching;
    Exception? _readError;
    long _cursorRevision;
    int _disposed;

    ModuleByteStream(ModuleProcess process, string handle, StreamOpenResult open)
    {
        _process = process;
        _handle = handle;
        _knownSize = open.Length is { } len && len > 0 ? len : 0;
        Seekable = open.Seekable;
        ContentType = open.ContentType;
        _lease = process.AcquireStreamLease();
    }

    /// <summary>True when the module serves arbitrary offsets. False means forward-only: a backwards seek throws.</summary>
    public bool Seekable { get; }

    /// <summary>The MIME type the module reported at open time, or null.</summary>
    public string? ContentType { get; }

    /// <summary>Open a module-served stream. The lease taken here keeps the module process out of the idle stop for as
    /// long as this stream lives.</summary>
    /// <param name="process">The module's process.</param>
    /// <param name="streamId">The <c>streamId</c> from the resolved locator.</param>
    /// <param name="ct">Cancels the open.</param>
    public static async Task<ModuleByteStream> OpenAsync(ModuleProcess process, string streamId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        StreamOpenResult open = await process.RequestAsync(ModuleMethods.StreamOpen, new StreamOpenParams(streamId),
            SdkJsonContext.Default.StreamOpenParams, SdkJsonContext.Default.StreamOpenResult,
            ModuleTimeouts.StreamOpen, ct).ConfigureAwait(false);

        if (open.Handle is not { Length: > 0 })
            throw new ModuleException(ModuleErrorCode.Unavailable,
                process.Module.Id + " opened stream '" + streamId + "' with no handle");
        return new ModuleByteStream(process, open.Handle, open);
    }

    // ── IAudioReadStream ────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Stream AsStream() => this;

    /// <inheritdoc/>
    public long CurrentOffset { get { lock (_gate) return _position; } }

    /// <summary>Always true: the module IS the body — there is no separate head/body attach on this path.</summary>
    public bool IsBodyAttached => true;

    /// <summary>The total length when the module knew it at open time (0 = unknown, which reports Length as unknown).</summary>
    public long KnownSize { get { lock (_gate) return _knownSize; } }

    /// <summary>Zero: a module stream carries no clear-head prefix (the module already resolved that internally).</summary>
    public int ClearHeadLength => 0;

    /// <summary>No-op: read-ahead here is one buffer refilled on demand, so there is nothing to pause.</summary>
    public IDisposable PauseReadAhead() => NullScope.Instance;

    /// <summary>No-op counterpart to <see cref="PauseReadAhead"/>.</summary>
    public void ResumeReadAheadAtCurrentOffset() { }

    // ── Stream ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public override bool CanSeek => Seekable && Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            long size = KnownSize;
            return size > 0 ? size : throw new NotSupportedException("the module did not report a length");
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get => CurrentOffset;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> destination)
    {
        while (true)
        {
            long version = DataVersion;
            int count = TryRead(destination, out bool wouldBlock);
            if (count > 0 || !wouldBlock) return count;
            WaitForData(version, _lifetime.Token);
        }
    }

    public long DataVersion => _availability.Version;
    public void WaitForData(long observedVersion, CancellationToken cancellationToken)
        => _availability.Wait(observedVersion, cancellationToken);

    public int TryRead(Span<byte> destination, out bool wouldBlock)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        wouldBlock = false;
        if (destination.IsEmpty) return 0;
        lock (_gate)
        {
            if (_readError is not null) throw new IOException("module stream read failed", _readError);
            if (TryServeFromBuffer(destination, out int count))
            {
                _position += count;
                return count;
            }
            if (_eofSeen && _position >= _bufferStart + _bufferLength) return 0;
            if (!_fetching)
            {
                _fetching = true;
                long position = _position;
                long revision = _cursorRevision;
                _ = Task.Run(() => FillAsync(position, revision, _lifetime.Token));
            }
            wouldBlock = true;
            return 0;
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => (_knownSize > 0 ? _knownSize : throw new NotSupportedException(
                    "seek-from-end needs a length the module did not report")) + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            if (target < 0) throw new IOException("cannot seek before the start of the stream");
            if (target == _position) return _position;
            if (!Seekable)
                throw new NotSupportedException(_process.Module.Id + " serves this stream forward-only");

            _position = target;
            _cursorRevision++;
            _eofSeen = false;
            _readError = null;
            _availability.Pulse();
            return _position;
        }
    }

    // ── the fetch ───────────────────────────────────────────────────────────────────────────────────────────────────

    bool TryServeFromBuffer(Span<byte> destination, out int served)
    {
        served = 0;
        if (_bufferStart < 0) return false;
        if (_position < _bufferStart || _position >= _bufferStart + _bufferLength) return false;

        int start = (int)(_position - _bufferStart);
        int available = _bufferLength - start;
        served = Math.Min(available, destination.Length);
        _buffer.AsSpan(start, served).CopyTo(destination);
        return served > 0;
    }

    // Exactly one RPC read is in flight. Empty non-EOF frames remain temporary starvation;
    // subsequent requests are paced on this cold producer, never on the host control reader.
    async Task FillAsync(long offset, long revision, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                BinaryPayload payload = await _process.RequestBinaryAsync(ModuleMethods.StreamRead,
                    new StreamReadParams(_handle, offset, ChunkBytes),
                    SdkJsonContext.Default.StreamReadParams, ModuleTimeouts.StreamRead, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (revision != _cursorRevision) return;
                    if (!payload.Bytes.IsEmpty || payload.Eof)
                    {
                        _bufferStart = offset;
                        _bufferLength = Math.Min(payload.Bytes.Length, _buffer.Length);
                        payload.Bytes.Span[.._bufferLength].CopyTo(_buffer);
                        _eofSeen = payload.Eof;
                        if (payload.Eof) _knownSize = offset + _bufferLength;
                        return;
                    }
                }
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) { if (revision == _cursorRevision) _readError = error; }
        }
        finally
        {
            lock (_gate) _fetching = false;
            _availability.Pulse();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            _lifetime.Cancel();
            _availability.Pulse();
            _ = CloseAsync(_process, _handle);
            _lease.Dispose();
        }

        base.Dispose(disposing);
    }

    // Fire-and-forget: a module that is already gone has nothing to release, and a close that fails must never be able
    // to throw out of Dispose (which runs on the audio teardown path).
    static async Task CloseAsync(ModuleProcess process, string handle)
    {
        if (process.State != ModuleProcessState.Ready) return;
        try
        {
            await process.RequestAsync(ModuleMethods.StreamClose, new StreamCloseParams(handle),
                SdkJsonContext.Default.StreamCloseParams, SdkJsonContext.Default.RpcUnit,
                ModuleTimeouts.StreamOpen, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* the handle dies with the process anyway */ }
    }

    sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
