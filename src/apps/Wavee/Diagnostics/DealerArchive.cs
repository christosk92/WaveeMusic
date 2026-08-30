using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Wavee.Backend.Realtime;

namespace Wavee;

/// <summary>
/// Opt-in archival of inbound Spotify dealer WebSocket frames. The receive loop copies a frame into an ArrayPool
/// buffer and returns immediately; a dedicated writer thread batches to a greppable index plus a raw payload blob.
/// Ping/pong are counted into a 5-minute keepalive summary rather than stored per-frame.
///
/// <para>OFF BY DEFAULT, and switched from ONE place: Settings ▸ Diagnostics ▸ "Archive Spotify realtime traffic"
/// (<c>WaveeSettings.DealerArchiveEnabled</c>). It can write up to 2 GB and retains 90 days, which is not something to
/// leave running for a listener — and it is a capture a user has to be able to turn on mid-session while reproducing a
/// bug, which is exactly what an environment variable cannot do. The composition root calls
/// <see cref="Configure(string?, bool)"/> once with the log directory and the persisted flag; the settings toggle calls
/// <see cref="SetEnabled(bool)"/>, which re-applies against that same directory.</para>
/// </summary>
public sealed class DealerArchive : IDisposable
{
    public const long DefaultMaxPendingBytes = 8L * 1024 * 1024;
    public const long DefaultMaxLiveFileBytes = 64L * 1024 * 1024;
    public const long DefaultMaxDirectoryBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultRetainDays = 90;
    const int DefaultBatchSize = 64;
    const int FlushIntervalMs = 250;
    const long KeepaliveSummaryMs = 5L * 60 * 1000;
    const int FileBufferBytes = 64 * 1024;

    public static readonly DealerArchive Instance = new();

    readonly object _queueGate = new();
    readonly object _writeGate = new();
    readonly Queue<Pending> _queue = new();
    readonly ManualResetEventSlim _pulse = new(false);
    readonly StringBuilder _idxLine = new(256);

    string? _dir;
    volatile bool _enabled;
    Thread? _writer;
    volatile bool _stop;
    volatile bool _fileSinkFailed;
    long _queuedBytes;
    int _droppedSinceWrite;
    int _droppedTotal;
    int _pingCount;
    int _pongCount;
    int _gzipInFlight;
    int _rollSeq;
    long _lastKeepaliveMs;
    DateTime _openDate;
    string? _idxPath;
    string? _binPath;
    StreamWriter? _idx;
    FileStream? _bin;

    internal long MaxPendingBytes { get; set; } = DefaultMaxPendingBytes;
    internal long MaxLiveFileBytes { get; set; } = DefaultMaxLiveFileBytes;
    internal long MaxDirectoryBytes { get; set; } = DefaultMaxDirectoryBytes;
    internal int RetainDays { get; set; } = DefaultRetainDays;
    internal int BatchSize { get; set; } = DefaultBatchSize;
    internal Func<DateTime> Clock { get; set; } = static () => DateTime.Now;
    internal bool SyncDrainForTests { get; set; }
    internal int DroppedForTests => Volatile.Read(ref _droppedTotal);
    internal string? DirectoryPath => _dir;

    /// <summary>Prefix match against the subscribers that actually act on a frame — not the <c>hm://</c> catch-all.</summary>
    public static bool IsHandled(DealerFrameType type, string? uri, string? messageIdent)
    {
        switch (type)
        {
            case DealerFrameType.Ping:
            case DealerFrameType.Pong:
                return true;
            case DealerFrameType.Message:
                if (uri is null || uri.Length == 0) return false;
                return uri.StartsWith("hm://pusher/v1/connections/", StringComparison.Ordinal)
                    || uri.StartsWith("hm://connect-state/v1/cluster", StringComparison.Ordinal)
                    || uri.StartsWith("hm://connect-state/v1/connect/volume", StringComparison.Ordinal)
                    || uri.StartsWith("hm://playlist/", StringComparison.Ordinal)
                    || uri.StartsWith("hm://playlist-permission/", StringComparison.Ordinal)
                    || uri.StartsWith("hm://collection/", StringComparison.Ordinal)
                    || uri.StartsWith("hm://presence2/user/", StringComparison.Ordinal);
            case DealerFrameType.Request:
                return messageIdent is { Length: > 0 }
                    && messageIdent.StartsWith("hm://connect-state/v1/", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    public static bool IsHandled(in DealerFrame frame) => IsHandled(frame.Type, frame.Uri, frame.MessageIdent);

    /// <summary>Point the archive at <paramref name="directory"/> and switch it <paramref name="enabled"/>. The
    /// directory is REMEMBERED, so <see cref="SetEnabled"/> can start and stop the writer later without the caller
    /// having to hold onto the path.</summary>
    public void Configure(string? directory, bool enabled)
    {
        lock (_writeGate)
        {
            if (!string.Equals(_dir, directory, StringComparison.OrdinalIgnoreCase))
                CloseStream();
            _dir = directory;
            _enabled = enabled && !string.IsNullOrWhiteSpace(directory);
            if (_enabled)
            {
                try { Directory.CreateDirectory(_dir!); } catch { }
                GzipLeftovers();
                Prune();
            }
        }
        if (_enabled && !SyncDrainForTests) EnsureWriter();
        else StopWriter();
    }

    /// <summary>Start or stop archiving against the directory <see cref="Configure(string?, bool)"/> was last given —
    /// the Settings ▸ Diagnostics toggle's one call. Re-applying the SAME directory means the live file is left open on
    /// a re-enable and the writer thread is torn down on a disable, exactly as <c>Configure</c> does; there is no second
    /// start/stop path to keep in step.</summary>
    public void SetEnabled(bool on) => Configure(_dir, on);

    /// <summary>Copy <paramref name="utf8"/> off the receive buffer and enqueue. Never waits on disk. Never throws.</summary>
    public void RecordInbound(ReadOnlySpan<byte> utf8, in DealerFrame frame)
    {
        try
        {
            if (!_enabled) return;
            if (frame.Type is DealerFrameType.Ping or DealerFrameType.Pong)
            {
                RecordKeepalive(frame.Type);
                return;
            }
            Enqueue(utf8, frame.Type, frame.Uri, frame.MessageIdent, frame.Key, IsHandled(frame));
        }
        catch { /* receive loop must not observe archive failures */ }
    }

    public void RecordInbound(ReadOnlySpan<byte> utf8, DealerFrameType type, string? uri, string? messageIdent, string? key)
    {
        try
        {
            if (!_enabled) return;
            if (type is DealerFrameType.Ping or DealerFrameType.Pong)
            {
                RecordKeepalive(type);
                return;
            }
            Enqueue(utf8, type, uri, messageIdent, key, IsHandled(type, uri, messageIdent));
        }
        catch { }
    }

    public void RecordKeepalive(DealerFrameType type)
    {
        try
        {
            if (!_enabled) return;
            if (type == DealerFrameType.Ping) Interlocked.Increment(ref _pingCount);
            else if (type == DealerFrameType.Pong) Interlocked.Increment(ref _pongCount);
        }
        catch { }
    }

    /// <summary>Best-effort drain. Safe from crash / ProcessExit paths.</summary>
    public void Flush()
    {
        try
        {
            lock (_writeGate)
            {
                DrainAllUnlocked(flushKeepalive: true);
                try { _idx?.Flush(); } catch { }
                try { _bin?.Flush(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Test hook: drain, gzip leftover jobs inline, and release file handles so tests can read/delete.</summary>
    public void FlushForTests()
    {
        lock (_writeGate)
        {
            DrainAllUnlocked(flushKeepalive: true);
            CloseStream();
        }
        SpinWaitGzip();
    }

    public void Dispose()
    {
        _stop = true;
        StopWriter();
        lock (_writeGate)
        {
            DrainAllUnlocked(flushKeepalive: true);
            CloseStream();
        }
        _pulse.Dispose();
    }

    void Enqueue(ReadOnlySpan<byte> utf8, DealerFrameType type, string? uri, string? ident, string? key, bool handled)
    {
        int len = utf8.Length;
        lock (_queueGate)
        {
            if (_queuedBytes + len > MaxPendingBytes)
            {
                _droppedSinceWrite++;
                _droppedTotal++;
                return;
            }
            byte[] rented = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                utf8.CopyTo(rented);
                _queue.Enqueue(new Pending(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), type, uri, ident, key, handled, rented, len));
                _queuedBytes += len;
                if (_queue.Count >= BatchSize) _pulse.Set();
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw;
            }
        }
    }

    void EnsureWriter()
    {
        if (_writer is { IsAlive: true }) return;
        _stop = false;
        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "WaveeDealerArchive",
        };
        _writer.Start();
    }

    void StopWriter()
    {
        var t = _writer;
        if (t is null) return;
        _stop = true;
        try { _pulse.Set(); } catch { }
        try { t.Join(2000); } catch { }
        _writer = null;
    }

    void WriterLoop()
    {
        while (!_stop)
        {
            try { _pulse.Wait(FlushIntervalMs); } catch { }
            try { _pulse.Reset(); } catch { }
            try
            {
                lock (_writeGate) DrainAllUnlocked(flushKeepalive: false);
            }
            catch { /* writer never dies */ }
        }
        try { lock (_writeGate) DrainAllUnlocked(flushKeepalive: true); } catch { }
    }

    void DrainAllUnlocked(bool flushKeepalive)
    {
        if (!_enabled || _dir is null) return;
        if (_idx is null) GzipLeftovers();
        MaybeDayRoll();
        while (true)
        {
            Pending[] batch;
            int dropped;
            lock (_queueGate)
            {
                dropped = _droppedSinceWrite;
                _droppedSinceWrite = 0;
                int n = Math.Min(BatchSize, _queue.Count);
                if (n == 0 && dropped == 0) break;
                batch = new Pending[n];
                for (int i = 0; i < n; i++)
                {
                    batch[i] = _queue.Dequeue();
                    _queuedBytes -= batch[i].Length;
                    if (_queuedBytes < 0) _queuedBytes = 0;
                }
            }
            WriteBatch(batch, dropped);
        }
        if (flushKeepalive || KeepaliveDue()) WriteKeepalive();
    }

    void WriteBatch(Pending[] batch, int dropped)
    {
        int done = 0;
        try
        {
            EnsureStream();
            if (_idx is null || _bin is null) { ReturnRange(batch, 0); return; }
            if (dropped > 0) WriteDropped(dropped);
            for (int i = 0; i < batch.Length; i++)
            {
                ref readonly Pending p = ref batch[i];
                long off = _bin.Position;
                if (p.Length > 0) _bin.Write(p.Buffer, 0, p.Length);
                WriteIndexLine(p, off);
                ArrayPool<byte>.Shared.Return(p.Buffer);
                done = i + 1;
            }
            _idx.Flush();
            _bin.Flush();
            if (_fileSinkFailed)
            {
                _fileSinkFailed = false;
                WaveeLog.Instance.Info("dealer-archive", "file sink recovered path=" + _idxPath);
            }
            if (_bin.Length >= MaxLiveFileBytes) SizeRoll();
        }
        catch
        {
            ReturnRange(batch, done);
            CloseStream();
            if (!_fileSinkFailed)
            {
                _fileSinkFailed = true;
                WaveeLog.Instance.Warn("dealer-archive", "file sink write failed - dropping frames path=" + (_idxPath ?? _dir));
            }
        }
    }

    static void ReturnRange(Pending[] batch, int start)
    {
        for (int i = start; i < batch.Length; i++)
        {
            try { ArrayPool<byte>.Shared.Return(batch[i].Buffer); } catch { }
        }
    }

    void WriteDropped(int dropped)
    {
        _idxLine.Clear();
        _idxLine.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        _idxLine.Append(",\"typ\":\"dropped\",\"n\":").Append(dropped.ToString(CultureInfo.InvariantCulture)).Append('}');
        _idx!.WriteLine(_idxLine);
    }

    void WriteKeepalive()
    {
        int ping = Interlocked.Exchange(ref _pingCount, 0);
        int pong = Interlocked.Exchange(ref _pongCount, 0);
        _lastKeepaliveMs = Environment.TickCount64;
        if (ping == 0 && pong == 0) return;
        try
        {
            EnsureStream();
            if (_idx is null) return;
            _idxLine.Clear();
            _idxLine.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            _idxLine.Append(",\"typ\":\"keepalive\",\"ping\":").Append(ping.ToString(CultureInfo.InvariantCulture));
            _idxLine.Append(",\"pong\":").Append(pong.ToString(CultureInfo.InvariantCulture)).Append('}');
            _idx.WriteLine(_idxLine);
            _idx.Flush();
        }
        catch { }
    }

    bool KeepaliveDue()
    {
        if (Volatile.Read(ref _pingCount) == 0 && Volatile.Read(ref _pongCount) == 0) return false;
        long last = Volatile.Read(ref _lastKeepaliveMs);
        if (last == 0) { _lastKeepaliveMs = Environment.TickCount64; return false; }
        return Environment.TickCount64 - last >= KeepaliveSummaryMs;
    }

    void WriteIndexLine(in Pending p, long off)
    {
        _idxLine.Clear();
        _idxLine.Append("{\"t\":").Append(p.UnixMs.ToString(CultureInfo.InvariantCulture));
        _idxLine.Append(",\"typ\":\"").Append(TypeName(p.Type)).Append('"');
        if (p.Uri is { Length: > 0 }) { _idxLine.Append(",\"uri\":"); AppendJsonString(p.Uri); }
        if (p.Ident is { Length: > 0 }) { _idxLine.Append(",\"ident\":"); AppendJsonString(p.Ident); }
        if (p.Key is { Length: > 0 }) { _idxLine.Append(",\"key\":"); AppendJsonString(p.Key); }
        _idxLine.Append(",\"handled\":").Append(p.Handled ? "true" : "false");
        _idxLine.Append(",\"n\":").Append(p.Length.ToString(CultureInfo.InvariantCulture));
        _idxLine.Append(",\"off\":").Append(off.ToString(CultureInfo.InvariantCulture));
        _idxLine.Append('}');
        _idx!.WriteLine(_idxLine);
    }

    void AppendJsonString(string s)
    {
        _idxLine.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': _idxLine.Append("\\\""); break;
                case '\\': _idxLine.Append("\\\\"); break;
                case '\n': _idxLine.Append("\\n"); break;
                case '\r': _idxLine.Append("\\r"); break;
                case '\t': _idxLine.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        _idxLine.Append("\\u");
                        _idxLine.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else _idxLine.Append(c);
                    break;
            }
        }
        _idxLine.Append('"');
    }

    static string TypeName(DealerFrameType type) => type switch
    {
        DealerFrameType.Ping => "ping",
        DealerFrameType.Pong => "pong",
        DealerFrameType.Message => "message",
        DealerFrameType.Request => "request",
        _ => "unknown",
    };

    void EnsureStream()
    {
        if (_idx is not null) return;
        if (_dir is null) return;
        Directory.CreateDirectory(_dir);
        DateTime now = Clock();
        _openDate = now.Date;
        string stamp = DateStamp(now);
        _idxPath = Path.Combine(_dir, "dealer-" + stamp + ".idx.ndjson");
        _binPath = Path.Combine(_dir, "dealer-" + stamp + ".bin");
        _bin = new FileStream(_binPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
            FileBufferBytes, FileOptions.SequentialScan);
        _idx = new StreamWriter(new FileStream(_idxPath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, FileBufferBytes, FileOptions.SequentialScan),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    void CloseStream()
    {
        try { _idx?.Flush(); } catch { }
        try { _idx?.Dispose(); } catch { }
        try { _bin?.Flush(); } catch { }
        try { _bin?.Dispose(); } catch { }
        _idx = null;
        _bin = null;
        _idxPath = null;
        _binPath = null;
    }

    void MaybeDayRoll()
    {
        if (_idx is null) return;
        DateTime today = Clock().Date;
        if (today == _openDate) return;
        string? idxPath = _idxPath;
        string? binPath = _binPath;
        CloseStream();
        if (idxPath is not null) QueueGzip(idxPath);
        if (binPath is not null) QueueGzip(binPath);
        GzipLeftovers();
        Prune();
    }

    void SizeRoll()
    {
        string? idxPath = _idxPath;
        string? binPath = _binPath;
        CloseStream();
        DateTime now = Clock();
        string stamp = DateStamp(now) + "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture);
        int seq = Interlocked.Increment(ref _rollSeq);
        if (idxPath is not null) MoveAndGzip(idxPath, "dealer-" + stamp + "-" + seq.ToString(CultureInfo.InvariantCulture) + ".idx.ndjson");
        if (binPath is not null) MoveAndGzip(binPath, "dealer-" + stamp + "-" + seq.ToString(CultureInfo.InvariantCulture) + ".bin");
        Prune();
    }

    void MoveAndGzip(string path, string newName)
    {
        if (_dir is null || !File.Exists(path)) return;
        string dest = Path.Combine(_dir, newName);
        try
        {
            File.Move(path, dest, overwrite: true);
            QueueGzip(dest);
        }
        catch
        {
            QueueGzip(path);
        }
    }

    void GzipLeftovers()
    {
        if (_dir is null || !Directory.Exists(_dir)) return;
        string today = DateStamp(Clock());
        string skipIdx = "dealer-" + today + ".idx.ndjson";
        string skipBin = "dealer-" + today + ".bin";
        string[] files;
        try { files = Directory.GetFiles(_dir, "dealer-*"); }
        catch { return; }
        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileName(files[i]);
            if (name.EndsWith(".gz", StringComparison.Ordinal)) continue;
            if (name.Equals(skipIdx, StringComparison.OrdinalIgnoreCase)
                || name.Equals(skipBin, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".idx.ndjson", StringComparison.Ordinal) || name.EndsWith(".bin", StringComparison.Ordinal))
                QueueGzip(files[i]);
        }
    }

    void QueueGzip(string path)
    {
        if (SyncDrainForTests)
        {
            GzipFile(path);
            return;
        }
        Interlocked.Increment(ref _gzipInFlight);
        ThreadPool.UnsafeQueueUserWorkItem(static s =>
        {
            var (archive, p) = ((DealerArchive, string))s!;
            try { archive.GzipFile(p); archive.Prune(); }
            finally { Interlocked.Decrement(ref archive._gzipInFlight); }
        }, (this, path), preferLocal: false);
    }

    void GzipFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string gz = path + ".gz";
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(gz, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                input.CopyTo(gzip);
            try { File.Delete(path); } catch { }
        }
        catch { /* leave uncompressed; next start retries */ }
    }

    void Prune()
    {
        if (_dir is null || !Directory.Exists(_dir)) return;
        try
        {
            var files = Directory.GetFiles(_dir, "dealer-*");
            DateTime cutoff = Clock().ToUniversalTime().AddDays(-RetainDays);
            long total = 0;
            var gz = new List<(string Path, DateTime Write, long Size)>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                FileInfo fi;
                try { fi = new FileInfo(files[i]); }
                catch { continue; }
                total += fi.Length;
                if (fi.Name.EndsWith(".gz", StringComparison.Ordinal))
                    gz.Add((fi.FullName, fi.LastWriteTimeUtc, fi.Length));
            }
            gz.Sort(static (a, b) => a.Write.CompareTo(b.Write));
            for (int i = 0; i < gz.Count; i++)
            {
                if (gz[i].Write >= cutoff && total <= MaxDirectoryBytes) break;
                if (gz[i].Write < cutoff || total > MaxDirectoryBytes)
                {
                    try { File.Delete(gz[i].Path); total -= gz[i].Size; } catch { }
                }
            }
        }
        catch { }
    }

    void SpinWaitGzip()
    {
        var start = Environment.TickCount64;
        while (Volatile.Read(ref _gzipInFlight) > 0 && Environment.TickCount64 - start < 10_000)
            Thread.Sleep(10);
    }

    static string DateStamp(DateTime now) => now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    readonly record struct Pending(
        long UnixMs, DealerFrameType Type, string? Uri, string? Ident, string? Key,
        bool Handled, byte[] Buffer, int Length);
}
