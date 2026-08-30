using System.Net.Http;
using System.Runtime.InteropServices;
using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

public delegate void CdnDecryptor(Span<byte> buffer, long streamOffset);

/// <summary>
/// Seekable read-through stream for Spotify audio. It can start as a clear-head-only stream and later attach the
/// encrypted CDN body. The decrypt-agnostic transport (range GETs, read-ahead, mirror failover, raw-chunk store) lives in
/// a composed <see cref="RangedHttpSource"/>; THIS class owns the Spotify-only concerns: the clear head, the AES-CTR /
/// native decrypt transform, the container-magic key check, and the head↔body handoff gating. Reads below the head
/// boundary come from the clear head; reads at/after the boundary fetch encrypted ranges (via the source) and decrypt at
/// the absolute file offset on copy-out.
/// </summary>
public sealed class SpotifyAudioStream : Stream, IAsyncDisposable, IAudioReadStream, IAudioNetworkRecoverySource
{
    const int ProbeBytes = 0xc0;

    public Stream AsStream() => this;

    readonly HttpClient _http;
    readonly string _name;
    readonly WaveeLogger _log;
    readonly byte[] _head;
    readonly int _headLen;
    readonly ChunkDiskCache? _bodyDisk;
    readonly RangedHttpRecoveryPolicy? _recoveryPolicy;
    readonly object _stateGate = new();

    byte[] _key = [];
    CdnDecryptor? _decryptor;
    RangedHttpSource? _ranged;
    long _pos;
    bool _bodyAttached;
    bool _failed;
    bool _disposed;
    Exception? _error;
    int _readAheadPauseCount;
    IDisposable? _attachedReadAheadPause;
    event Action<AudioNetworkRecoveryEvent>? NetworkRecovery;

    event Action<AudioNetworkRecoveryEvent>? IAudioNetworkRecoverySource.NetworkRecovery
    {
        add => NetworkRecovery += value;
        remove => NetworkRecovery -= value;
    }

    SpotifyAudioStream(HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, string name = "", WaveeLogger log = default,
        ChunkDiskCache? bodyDisk = null, RangedHttpRecoveryPolicy? recoveryPolicy = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _name = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        _log = log;
        _bodyDisk = bodyDisk;
        _recoveryPolicy = recoveryPolicy;
        _head = MemoryMarshal.TryGetArray(head, out var segment)
            && segment.Offset == 0
            && segment.Count == segment.Array!.Length
            ? segment.Array
            : head.ToArray();
        _headLen = Math.Max(0, Math.Min(headBoundary, _head.Length));
    }

    /// <summary>Create a stream that can serve clear head bytes immediately. Call <see cref="AttachBodyAsync"/> later.</summary>
    public static SpotifyAudioStream CreateHeadOnly(HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, string name = "", WaveeLogger log = default,
        ChunkDiskCache? bodyDisk = null) =>
        new(http, head, headBoundary, name, log, bodyDisk);

    /// <summary>Create and attach the ranged CDN body before returning. Kept for the non-fast/full-load path and tests.</summary>
    public static async Task<SpotifyAudioStream> CreateAsync(
        HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        var stream = CreateHeadOnly(http, head, headBoundary);
        await stream.AttachBodyAsync(key, cdnUrls, knownSize, ct).ConfigureAwait(false);
        return stream;
    }

    // Keeps production callers on the standard 90-second policy while allowing deterministic retry-budget tests.
    internal static async Task<SpotifyAudioStream> CreateAsync(
        HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, byte[] key, string[] cdnUrls, long? knownSize,
        CancellationToken ct, RangedHttpRecoveryPolicy recoveryPolicy)
    {
        var stream = new SpotifyAudioStream(http, head, headBoundary, recoveryPolicy: recoveryPolicy);
        await stream.AttachBodyAsync(key, cdnUrls, knownSize, ct).ConfigureAwait(false);
        return stream;
    }

    public static async Task<SpotifyAudioStream> CreateWithNativeDecryptorAsync(
        HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, CdnDecryptor decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        var stream = CreateHeadOnly(http, head, headBoundary);
        await stream.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, knownSize, ct).ConfigureAwait(false);
        return stream;
    }

    public Task AttachBodyAsync(byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        if (key.Length != 16) throw new ArgumentException("AES key must be 16 bytes", nameof(key));
        return AttachBodyCoreAsync(key.ToArray(), null, cdnUrls, knownSize, ct, eagerFetch: true);
    }

    public Task AttachBodyLazyAsync(byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        if (key.Length != 16) throw new ArgumentException("AES key must be 16 bytes", nameof(key));
        return AttachBodyCoreAsync(key.ToArray(), null, cdnUrls, knownSize, ct, eagerFetch: false);
    }

    public Task AttachBodyWithNativeDecryptorAsync(CdnDecryptor decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decryptor);
        return AttachBodyCoreAsync([], decryptor, cdnUrls, knownSize, ct, eagerFetch: true);
    }

    public Task AttachBodyWithNativeDecryptorLazyAsync(CdnDecryptor decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decryptor);
        return AttachBodyCoreAsync([], decryptor, cdnUrls, knownSize, ct, eagerFetch: false);
    }

    async Task AttachBodyCoreAsync(byte[] key, CdnDecryptor? decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct, bool eagerFetch)
    {
        if (cdnUrls is null || cdnUrls.Length == 0) throw new InvalidOperationException("no CDN urls");

        RangedHttpSource ranged;
        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            if (_bodyAttached) return;
            _key = key;
            _decryptor = decryptor;
            ranged = new RangedHttpSource(_http, _name, _log, _headLen, WakeReaders, disk: _bodyDisk,
                onRecovery: e => NetworkRecovery?.Invoke(e), recoveryPolicy: _recoveryPolicy);
            try { ranged.Configure(cdnUrls, knownSize); }   // throws on empty/invalid mirrors or bad size — before we mark attached
            catch { ranged.Dispose(); throw; }
            _ranged = ranged;
            if (_readAheadPauseCount > 0)
                _attachedReadAheadPause = ranged.PauseReadAhead();
            _bodyAttached = true;
            Monitor.PulseAll(_stateGate);
        }
        _log.Info($"stream {_name}: body attach accepted headBoundary={_headLen}B knownSize={(knownSize ?? 0)} mirrors={cdnUrls.Length} mode={(eagerFetch ? "eager" : "lazy")}");

        try
        {
            if (!eagerFetch)
            {
                ranged.StartReadAhead();
                _log.Info($"stream {_name}: lazy body attached; decoder can continue on clear head while read-ahead runs");
                return;
            }
            await ranged.PrimeAsync(ct).ConfigureAwait(false);
            ranged.StartReadAhead();
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    public bool IsBodyAttached
    {
        get { lock (_stateGate) return _bodyAttached; }
    }

    public void InvalidateBodyCache()
    {
        _ranged?.InvalidateDiskCache();
        _bodyDisk?.Invalidate(_name);
    }

    public long CurrentOffset => Volatile.Read(ref _pos);
    public int ClearHeadLength => _headLen;
    public long KnownSize => _ranged?.KnownSize ?? 0;

    long Size => _ranged?.KnownSize ?? 0;

    public void AbortBody(Exception error) => Fail(error);

    public bool TryValidateKey(string format, out string detail)
    {
        try { EnsureRangeAvailable(0, ProbeBytes); }
        catch (Exception ex) { detail = "body unavailable for key-check: " + ex.Message; return false; }

        var size = Size;
        var n = (int)Math.Min(size > 0 ? size : ProbeBytes, ProbeBytes);
        if (_ranged is null || !_ranged.ContainsRange(0, Math.Min(n, ProbeBytes)))
        {
            detail = "body prefix unavailable for key-check";
            return false;
        }

        var dec = new byte[n];
        _ranged.ReadRaw(0, dec, 0, n);
        Decrypt(dec, 0);

        ReadOnlySpan<byte> magic = format is "Flac" or "Flac24" ? "fLaC"u8 : SpotifyAesCtr.OggMagic;
        var magicName = format is "Flac" or "Flac24" ? "fLaC" : "OggS";

        bool foundAt0 = HasMagic(dec, 0, magic);
        bool foundAtHdr = HasMagic(dec, SpotifyAesCtr.OggMagicOffset, magic);
        detail = foundAt0 ? $"{magicName} at offset 0 (no Spotify header) - key OK"
               : foundAtHdr ? $"{magicName} at 0xa7 (167-byte header) - key OK"
               : $"no {magicName} at 0 or 0xa7 after decrypt - key/layout check failed; got 0:{HexAt(dec, 0, magic.Length)}, 0xa7:{HexAt(dec, SpotifyAesCtr.OggMagicOffset, magic.Length)}";
        return foundAt0 || foundAtHdr;
    }

    static bool HasMagic(byte[] bytes, int offset, ReadOnlySpan<byte> magic) =>
        offset >= 0 && bytes.Length >= offset + magic.Length && bytes.AsSpan(offset, magic.Length).SequenceEqual(magic);

    static string HexAt(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || bytes.Length <= offset) return "(none)";
        var n = Math.Min(count, bytes.Length - offset);
        return Convert.ToHexString(bytes.AsSpan(offset, n));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) return 0;

        while (true)
        {
            var size = Size;
            var pos = Volatile.Read(ref _pos);
            if (size > 0 && pos >= size) return 0;

            if (pos < _headLen)
            {
                var n = (int)Math.Min(count, _headLen - pos);
                Array.Copy(_head, (int)pos, buffer, offset, n);
                Volatile.Write(ref _pos, pos + n);
                MarkReadAheadProgress(pos + n);
                return n;
            }

            WaitForBodyOrSeekIntoHead();
            pos = Volatile.Read(ref _pos);
            if (pos < _headLen) continue;

            size = Size;
            if (size > 0 && pos >= size) return 0;

            var wanted = size > 0 ? (int)Math.Min(count, size - pos) : count;
            if (wanted <= 0) return 0;
            EnsureRangeAvailable(pos, wanted);

            var available = _ranged!.ContainedLengthFrom(pos);
            size = Size;
            if (size > 0) available = Math.Min(available, size - pos);
            if (available <= 0) return 0;

            var m = (int)Math.Min(wanted, available);
            _ranged.ReadRaw(pos, buffer, offset, m);
            Decrypt(buffer.AsSpan(offset, m), pos);
            Volatile.Write(ref _pos, pos + m);
            MarkReadAheadProgress(pos + m);
            return m;
        }
    }

    void MarkReadAheadProgress(long offset) => _ranged?.MarkProgress(offset);

    public IDisposable PauseReadAhead()
    {
        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            if (_readAheadPauseCount++ == 0 && _ranged is not null)
                _attachedReadAheadPause = _ranged.PauseReadAhead();
            return new StreamReadAheadPause(this);
        }
    }

    void ReleaseReadAheadPause()
    {
        IDisposable? attachedPause = null;
        lock (_stateGate)
        {
            if (_readAheadPauseCount <= 0) return;
            if (--_readAheadPauseCount == 0)
            {
                attachedPause = _attachedReadAheadPause;
                _attachedReadAheadPause = null;
            }
        }
        attachedPause?.Dispose();
    }

    public void ResumeReadAheadAtCurrentOffset() => _ranged?.ResumeReadAheadAt(Volatile.Read(ref _pos));

    /// <summary>Block until [start, start+length) is buffered. Waits for body attach first (so a direct caller such as
    /// the key-check doesn't fetch before the body exists), then delegates the fetch to the ranged source.</summary>
    void EnsureRangeAvailable(long start, int length)
    {
        WaitForBody();
        _ranged!.EnsureRange(start, length);
    }

    void Decrypt(Span<byte> buffer, long streamOffset)
    {
        if (_decryptor is not null)
            _decryptor(buffer, streamOffset);
        else
            SpotifyAesCtr.DecryptInPlace(buffer, _key, streamOffset);
    }

    void WaitForBody()
    {
        lock (_stateGate)
        {
            while (!_bodyAttached && !_failed && !_disposed)
                Monitor.Wait(_stateGate, 50);
            ThrowIfDisposedLocked();
            if (_failed) throw _error ?? new IOException("CDN body unavailable");
        }
    }

    void WaitForBodyOrSeekIntoHead()
    {
        lock (_stateGate)
        {
            while (!_bodyAttached && !_failed && !_disposed && Volatile.Read(ref _pos) >= _headLen)
                Monitor.Wait(_stateGate, 50);
            ThrowIfDisposedLocked();
            if (_failed) throw _error ?? new IOException("CDN body unavailable");
        }
    }

    void WakeReaders()
    {
        lock (_stateGate) Monitor.PulseAll(_stateGate);
    }

    void Fail(Exception ex)
    {
        lock (_stateGate)
        {
            _error = ex;
            _failed = true;
            Monitor.PulseAll(_stateGate);
        }
        _ranged?.Stop();
    }

    void ThrowIfDisposedLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpotifyAudioStream));
    }

    public override long Length
    {
        get
        {
            var size = Size;
            return size > 0 ? size : _headLen;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Position
    {
        get => Volatile.Read(ref _pos);
        set
        {
            Volatile.Write(ref _pos, Math.Max(0, value));
            lock (_stateGate) Monitor.PulseAll(_stateGate);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Volatile.Read(ref _pos) + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Volatile.Write(ref _pos, Math.Max(0, next));
        lock (_stateGate) Monitor.PulseAll(_stateGate);
        return Volatile.Read(ref _pos);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        RangedHttpSource? ranged;
        IDisposable? attachedPause;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            ranged = _ranged;
            attachedPause = _attachedReadAheadPause;
            _attachedReadAheadPause = null;
            Monitor.PulseAll(_stateGate);
        }
        attachedPause?.Dispose();
        ranged?.Dispose();   // owns the read-ahead task + fetch resources lifecycle
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    sealed class StreamReadAheadPause : IDisposable
    {
        SpotifyAudioStream? _owner;

        public StreamReadAheadPause(SpotifyAudioStream owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseReadAheadPause();
    }
}

/// <summary>
/// Forward-offset wrapper: presents byte <paramref name="skip"/> of the inner stream as logical position 0.
/// </summary>
public sealed class SkipStream : Stream
{
    readonly Stream _inner;
    readonly long _skip;

    public SkipStream(Stream inner, long skip)
    {
        _inner = inner;
        _skip = skip;
        _inner.Seek(skip, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
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
    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
