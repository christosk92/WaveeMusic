// ── Playback/NetworkByteSource.cs ───────────────────────────────────────────────────────────────────────────────────
// a buffered reader for a song on a network share (NAS, SMB, mapped drive) or a slow drive: a dedicated I/O thread reads the
// file ahead of the decoder in 1 MB chunks, so a slow or sleeping share is a "buffering" pause, never a stalled audio thread
//
// Role: CORE (the decoder thread calls Read/Seek; the I/O thread fills; Cancel from any thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.16 "Playback: a buffered reader", D17
//
// WHY. `FileByteSource` (the engine's) reads synchronously on the decoder thread, and its `Cancel` is a no-op: one read on a NAS
// that is spinning up its disks blocks the decoder for seconds (an audible stall), and one on a dead share for a minute. Here the
// decoder reads from memory. When it catches up with the reader it waits, and after 150 ms the source reports BUFFERING
// (`Buffering`, a process-wide fact the player bar can show as "Buffering from \\nas…"); a seek outside what is buffered restarts
// the reader at the new offset; `Cancel` wakes a waiting read at once. A share that drops mid-song: everything already buffered
// plays out, then the read fails (-1) and the song faults as unavailable; the folder's next reachability probe marks it offline.
// FLAC is 20-60 MB a song: the whole song normally fits. Above `MaxBuffered` the reader keeps only a window ahead of the decoder.
//
// PRELOAD. A gapless/crossfade pre-open of the NEXT song opens its source early, and this source starts reading at TryOpen, so
// the next song is (being) buffered while the current one plays. Nothing else is needed for the join to stay gapless.

using FluentGpu.Media;

namespace Wavee;

/// <summary>A read-ahead byte source for a file on a network share (see the file header). Thread-safe as the seam requires.</summary>
public sealed class NetworkByteSource : IMediaByteSource
{
    const int ChunkBytes = 1 << 20;
    const long MaxBuffered = 512L << 20;
    const long KeepBehind = 8L << 20;   // above MaxBuffered: what stays buffered behind the decoder

    static int s_buffering;
    static string s_bufferingPath = "";

    /// <summary>How many network sources are waiting for data right now (the "buffering" fact; 0 = none).</summary>
    public static int Buffering => Volatile.Read(ref s_buffering);

    /// <summary>The file the most recent buffering wait was for (its folder names the share in "Buffering from \\nas…").</summary>
    public static string BufferingPath => Volatile.Read(ref s_bufferingPath);

    /// <summary>Raised (any thread) when <see cref="Buffering"/> moves; a surface posts to the UI thread.</summary>
    public static event Action? BufferingChanged;

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<long, byte[]> _chunks = new();   // chunk index -> bytes (full chunks, the last may be short)
    long _length = -1;
    long _start;            // the first offset the current reader filled from
    long _filled;           // the absolute offset up to which [_start, _filled) is buffered
    long _pos;              // the decoder's offset
    int _generation;        // bumped on every reader restart; a stale reader stops writing
    bool _eof, _failed, _cancelled, _closed, _waiting;
    Thread? _reader;

    public NetworkByteSource(string path) => _path = path;

    public long? Length { get { lock (_gate) return _length >= 0 ? _length : null; } }

    public SourceCaps Caps => new() { Seekable = true, KnownLength = true };

    public bool TryOpen(in DataSpec spec)
    {
        long length;
        try { length = new FileInfo(_path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        lock (_gate)
        {
            _length = length;
            _cancelled = false;
            _pos = Math.Max(0, spec.Position);
            Restart(_pos);
        }
        return true;
    }

    public int Read(Span<byte> dst)
    {
        if (dst.IsEmpty) return 0;
        lock (_gate)
        {
            if (_length >= 0 && _pos >= _length) return 0;
            if (_pos < _start || _pos > _filled + ChunkBytes) Restart(_pos);   // a jump outside what is buffered
            long waitedFrom = 0;
            while (_pos >= _filled && !_eof && !_failed && !_cancelled && !_closed)
            {
                if (waitedFrom == 0) waitedFrom = Environment.TickCount64;
                Monitor.Wait(_gate, 50);
                if (!_waiting && Environment.TickCount64 - waitedFrom > 150) SetWaiting(true);
            }
            if (_waiting) SetWaiting(false);
            if (_cancelled || _closed) return -1;
            if (_pos >= _filled) return _eof ? 0 : -1;   // the share dropped: what was buffered has played out
            int n = 0;
            while (n < dst.Length && _pos < _filled)
            {
                long chunk = _pos / ChunkBytes;
                int within = (int)(_pos % ChunkBytes);
                if (!_chunks.TryGetValue(chunk, out byte[]? bytes)) break;
                int take = (int)Math.Min(Math.Min(dst.Length - n, bytes.Length - within), _filled - _pos);
                if (take <= 0) break;
                bytes.AsSpan(within, take).CopyTo(dst[n..]);
                n += take;
                _pos += take;
            }
            if (_length > MaxBuffered) TrimBehind();
            Monitor.PulseAll(_gate);   // the reader may be waiting for room
            return n;
        }
    }

    public long Seek(long offset)
    {
        lock (_gate)
        {
            _cancelled = false;
            _pos = Math.Clamp(offset, 0, Math.Max(0, _length));
            if (_pos < _start || _pos > _filled + ChunkBytes) Restart(_pos);
            Monitor.PulseAll(_gate);
            return _pos;
        }
    }

    public void Cancel()
    {
        lock (_gate) { _cancelled = true; Monitor.PulseAll(_gate); }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _generation++;
            _chunks.Clear();
            if (_waiting) SetWaiting(false);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Under the gate: drop the buffer and start a reader at <paramref name="offset"/> (chunk-aligned).</summary>
    void Restart(long offset)
    {
        long aligned = offset / ChunkBytes * ChunkBytes;
        _generation++;
        _chunks.Clear();
        _start = _filled = aligned;
        _eof = _failed = false;
        int generation = _generation;
        _reader = new Thread(() => Fill(aligned, generation)) { IsBackground = true, Name = "Wavee.NetworkRead", Priority = ThreadPriority.AboveNormal };
        _reader.Start();
    }

    /// <summary>The I/O thread: read 1 MB chunks from <paramref name="from"/> until the end, a failure, a restart or a close.</summary>
    void Fill(long from, int generation)
    {
        FileStream? file = null;
        try
        {
            file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            file.Seek(from, SeekOrigin.Begin);
            long at = from;
            while (true)
            {
                lock (_gate)
                {
                    // Above MaxBuffered keep only a window ahead of the decoder.
                    while (generation == _generation && !_closed && _length > MaxBuffered && at - _pos > MaxBuffered / 4) Monitor.Wait(_gate, 100);
                    if (generation != _generation || _closed) return;
                }
                var chunk = new byte[(int)Math.Min(ChunkBytes, Math.Max(0, _length - at))];
                if (chunk.Length == 0) { lock (_gate) { if (generation == _generation) { _eof = true; Monitor.PulseAll(_gate); } } return; }
                int got = 0;
                while (got < chunk.Length)
                {
                    int n = file.Read(chunk, got, chunk.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                lock (_gate)
                {
                    if (generation != _generation || _closed) return;
                    _chunks[at / ChunkBytes] = got == chunk.Length ? chunk : chunk[..got];
                    _filled = at + got;
                    at += got;
                    if (got < chunk.Length || at >= _length) _eof = true;
                    Monitor.PulseAll(_gate);
                    if (_eof) return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("local", "local.network read failed file=" + Path.GetFileName(_path) + " (" + ex.GetType().Name + ")");
            lock (_gate) { if (generation == _generation) { _failed = true; Monitor.PulseAll(_gate); } }
        }
        finally
        {
            file?.Dispose();
        }
    }

    void TrimBehind()
    {
        long keepFrom = Math.Max(_start, (_pos - KeepBehind) / ChunkBytes * ChunkBytes);
        if (keepFrom <= _start) return;
        for (long c = _start / ChunkBytes; c < keepFrom / ChunkBytes; c++) _chunks.Remove(c);
        _start = keepFrom;
    }

    void SetWaiting(bool on)
    {
        _waiting = on;
        if (on) Volatile.Write(ref s_bufferingPath, _path);
        Interlocked.Add(ref s_buffering, on ? 1 : -1);
        BufferingChanged?.Invoke();
    }
}
