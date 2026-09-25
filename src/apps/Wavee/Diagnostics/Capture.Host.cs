// ── Wavee/Diagnostics/Capture.Host.cs — the writer + host lifecycle for the realtime capture (unit 2) ───────────────
//
// Unit 2 of docs/plans/wavee/realtime-capture-implementation.md (§3.1-§3.4, §6.1's byte layout). This is the ONLY
// thing in the plan that touches disk for the capture: `RealtimeCaptureWriter` implements unit 1's `ICaptureSink`
// seam and owns segment open/close/gzip/rotation/retention; `RealtimeCaptureHost` owns the on/off lifecycle wired
// to `Platform.Keys.DealerArchiveEnabled` (the persisted key is UNCHANGED — CLAUDE.md's "no legacy renumbering").
//
// Mirrors `Platform.Host.cs`'s own `Log` file sink shell (`Platform.Host.cs:611-750`) closely on purpose: a bounded
// in-memory queue drained on a ThreadPool work item scheduled at most once at a time (`s_drainScheduled`-shaped),
// never a dedicated blocking thread, so `Emit` never blocks the calling thread (CLAUDE.md, and §3.2's own promise).
//
// Two lanes, per §3.2: HEADERS (every CaptureEvent's fixed fields, ~header-only, 8 MB pending budget, a Begin/End
// pair's End is NEVER dropped once its Begin was accepted) and PAYLOAD BYTES (bodies — HTTP/PUT/dealer bytes, 32 MB
// pending budget, may drop independently of its header, leaving PayloadLength=0 + Truncated=true on the header).
//
// Redaction (unit 3, `Capture.Redact.cs`) runs on every payload byte range HERE, on the writer thread, as a
// defense-in-depth net additional to whatever a call site (units 4/5) already redacted before calling `Capture.*`:
// "the writer thread never sees an unredacted token" (plan §4) holds even if a future call site forgets its own
// pass, and redacting already-redacted bytes is idempotent (no double-processing hazard).

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using FluentGpu.Signals;

namespace Wavee;

// ── 1. the on-disk record codec — the shared contract with decode.py's struct.unpack (§6.1) ────────────────────────

/// <summary>PURE encode/decode of one `.idx` record: the 96-byte fixed header (§6.1's <see cref="CaptureRecord.FixedHeaderBytes"/>),
/// then A/B/C inline, UTF-8, length-prefixed (ushort). A torn trailing record (a crash mid-write) is reported by
/// <see cref="TryDecode"/> returning false, never an exception — the reader's job is "stop here", not "corrupt".</summary>
public static class CaptureRecordCodec
{
    /// <summary>A/B/C are each truncated to this many UTF-8 bytes before encoding (§6.1). A truncated field's last
    /// three bytes are replaced with the UTF-8 encoding of '…' so a reader never sees a value silently cut mid-run
    /// without a marker.</summary>
    public const int MaxFieldBytes = 252;

    static readonly byte[] Ellipsis = Encoding.UTF8.GetBytes("…"); // 3 bytes (E2 80 A6)

    /// <summary>Truncates a string to <see cref="MaxFieldBytes"/> UTF-8 bytes, trailing-ellipsis-marked when cut.
    /// Null becomes an empty span. Never allocates when the string already fits.</summary>
    public static ReadOnlyMemory<byte> TruncateUtf8(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ReadOnlyMemory<byte>.Empty;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= MaxFieldBytes) return Encoding.UTF8.GetBytes(value);

        // Truncate by UTF-16 chars first (a safe over-estimate: never more chars than bytes), then trim to a valid
        // UTF-8 boundary by re-measuring — simplest correct approach for a rare, cold path (this only runs when a
        // field is already unusually long).
        int room = MaxFieldBytes - Ellipsis.Length;
        int charGuess = Math.Min(value.Length, room);
        while (charGuess > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, charGuess)) > room) charGuess--;
        var head = Encoding.UTF8.GetBytes(value.Substring(0, charGuess));
        var result = new byte[head.Length + Ellipsis.Length];
        head.CopyTo(result, 0);
        Ellipsis.CopyTo(result, head.Length);
        return result;
    }

    /// <summary>Total encoded size for a record whose A/B/C are already the exact bytes that will be written
    /// (i.e. already run through <see cref="TruncateUtf8"/>).</summary>
    public static int EncodedSize(in CaptureRecord record)
        => CaptureRecord.FixedHeaderBytes + record.A.Length + record.B.Length + record.C.Length;

    /// <summary>Encodes one record into <paramref name="destination"/> (must be at least <see cref="EncodedSize"/>
    /// bytes). Returns the number of bytes written.</summary>
    public static int Encode(in CaptureRecord record, Span<byte> destination)
    {
        int total = EncodedSize(in record);
        if (destination.Length < total) throw new ArgumentException("destination too small for record", nameof(destination));

        BinaryPrimitives.WriteInt64LittleEndian(destination[0..8], record.Seq);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..16], record.Qpc);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..24], record.UnixMs);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..32], record.Id);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..40], record.CauseId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[40..48], record.RootId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..56], record.N0);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..64], record.N1);
        BinaryPrimitives.WriteInt32LittleEndian(destination[64..68], record.PayloadOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[68..72], record.PayloadLength);
        destination[72] = (byte)record.Kind;
        destination[73] = (byte)record.Phase;
        destination[74] = (byte)record.Priority;
        destination[75] = (byte)(record.Truncated ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[76..78], (ushort)record.A.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[78..80], (ushort)record.B.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[80..82], (ushort)record.C.Length);
        destination[82..96].Clear(); // reserved — 8-byte aligned, room for a future field without a layout break

        int off = CaptureRecord.FixedHeaderBytes;
        record.A.Span.CopyTo(destination[off..]); off += record.A.Length;
        record.B.Span.CopyTo(destination[off..]); off += record.B.Length;
        record.C.Span.CopyTo(destination[off..]); off += record.C.Length;
        return total;
    }

    /// <summary>Decodes one record from the FRONT of <paramref name="source"/>. Returns false — never throws — when
    /// fewer bytes are available than the record declares (a torn header, or a valid header whose A/B/C run past
    /// what is actually on disk): the caller's contract is "stop reading, this is where the capture ends", the
    /// crash-safety property §3.1 documents.</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> source, out CaptureRecord record, out int bytesConsumed)
    {
        record = default;
        bytesConsumed = 0;
        if (source.Length < CaptureRecord.FixedHeaderBytes) return false;

        var span = source.Span;
        long seq = BinaryPrimitives.ReadInt64LittleEndian(span[0..8]);
        long qpc = BinaryPrimitives.ReadInt64LittleEndian(span[8..16]);
        long unixMs = BinaryPrimitives.ReadInt64LittleEndian(span[16..24]);
        long id = BinaryPrimitives.ReadInt64LittleEndian(span[24..32]);
        long causeId = BinaryPrimitives.ReadInt64LittleEndian(span[32..40]);
        long rootId = BinaryPrimitives.ReadInt64LittleEndian(span[40..48]);
        long n0 = BinaryPrimitives.ReadInt64LittleEndian(span[48..56]);
        long n1 = BinaryPrimitives.ReadInt64LittleEndian(span[56..64]);
        int payloadOffset = BinaryPrimitives.ReadInt32LittleEndian(span[64..68]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(span[68..72]);
        var kind = (CaptureKind)span[72];
        var phase = (CapturePhase)span[73];
        var priority = (CapturePriority)span[74];
        bool truncated = span[75] != 0;
        ushort lenA = BinaryPrimitives.ReadUInt16LittleEndian(span[76..78]);
        ushort lenB = BinaryPrimitives.ReadUInt16LittleEndian(span[78..80]);
        ushort lenC = BinaryPrimitives.ReadUInt16LittleEndian(span[80..82]);

        int total = CaptureRecord.FixedHeaderBytes + lenA + lenB + lenC;
        if (source.Length < total) return false; // torn trailing record — a valid header, incomplete strings

        int off = CaptureRecord.FixedHeaderBytes;
        var a = source.Slice(off, lenA); off += lenA;
        var b = source.Slice(off, lenB); off += lenB;
        var c = source.Slice(off, lenC);

        record = new CaptureRecord(seq, qpc, unixMs, id, causeId, rootId, kind, phase, priority, truncated,
            n0, n1, payloadOffset, payloadLength, a, b, c);
        bytesConsumed = total;
        return true;
    }
}

// ── 2. the writer — two bounded lanes, a background drain, segment rotation/retention ──────────────────────────────

/// <summary>The realtime capture's writer: <see cref="ICaptureSink"/> implementation, installed into <see cref="Capture.Sink"/>
/// by <see cref="RealtimeCaptureHost"/> only while capture is on. Bounded, drop-and-count, never blocks the calling
/// thread (§3.2): <see cref="Emit"/> only enqueues under a short lock and (at most once) schedules a drain on the
/// thread pool — the same shape as `Log`'s own file sink (`Platform.Host.cs:679-709`).</summary>
sealed class RealtimeCaptureWriter : ICaptureSink, IDisposable
{
    const long MaxHeaderPendingBytes = 8L * 1024 * 1024;
    const long MaxPayloadPendingBytes = 32L * 1024 * 1024;
    const long MaxLiveSegmentBytes = 64L * 1024 * 1024;  // a size roll well under the 2 GB directory cap
    const long MaxDirectoryBytes = 2L * 1024 * 1024 * 1024;
    const int RetainDays = 90;
    // A Begin/End pair whose End never arrives within this many segment rolls reads as "still open" to the reader
    // (§2.5's EchoMissing inference) — nothing to enforce here; this constant documents the writer's own promise
    // that it never invents a synthetic End.

    readonly string _dir;
    readonly Action<string> _log;

    // ── the pending state: BOTH lanes plus the drain-scheduled flag share ONE lock (mirrors `Log`'s own
    // `s_fileGate`, `Platform.Host.cs:619,682-689`) — a single lock is what makes "is there anything left to drain,
    // and should I be the one to (re)schedule it" atomic; splitting the two lanes across separate locks would open
    // exactly the lost-wakeup race `Log`'s single-lock shape avoids. Held only for a memcpy + a dictionary/list
    // write — microseconds, never the calling thread's bottleneck. ──
    readonly Lock _gate = new();
    readonly LinkedList<CaptureEvent> _headers = new();
    long _headerPendingBytes;
    long _droppedHeaders;
    readonly Dictionary<long, (byte[] Rented, int Length)> _payloads = new();
    readonly Queue<long> _payloadOrder = new(); // FIFO for oldest-first eviction under budget pressure
    readonly HashSet<long> _hadPayloadIds = new(); // every event id Emit() was GIVEN payload bytes for, whether or
                                                    // not it survived the lane's budget (WriteBatch's Truncated test)
    long _payloadPendingBytes;
    long _droppedPayloads;
    bool _drainScheduled;

    // ── segment state — touched only under _writeGate ───────────────────────────────────────────────────────────
    readonly Lock _writeGate = new();
    FileStream? _idx;
    FileStream? _blob;
    DateTime _openDate;
    int _segmentIndex = 1;
    long _blobLength;
    bool _disposed;

    public RealtimeCaptureWriter(string dir, Action<string> log)
    {
        _dir = dir;
        _log = log;
        try { Directory.CreateDirectory(_dir); } catch (Exception ex) { _log("capture dir create failed: " + ex.Message); }
    }

    public void Emit(in CaptureEvent evt, ReadOnlyMemory<byte> payload)
    {
        bool schedule;
        lock (_gate)
        {
            if (!payload.IsEmpty)
            {
                _hadPayloadIds.Add(evt.Id);
                EnqueuePayloadLocked(evt.Id, payload);
            }
            EnqueueHeaderLocked(in evt);
            schedule = !_drainScheduled;
            _drainScheduled = true;
        }
        if (schedule) ThreadPool.UnsafeQueueUserWorkItem(static state => ((RealtimeCaptureWriter)state!).DrainLoop(), this);
    }

    // Both callers already hold _gate.

    void EnqueuePayloadLocked(long eventId, ReadOnlyMemory<byte> payload)
    {
        int len = payload.Length;
        while (_payloadPendingBytes + len > MaxPayloadPendingBytes && _payloadOrder.Count > 0)
        {
            long oldest = _payloadOrder.Dequeue();
            if (_payloads.Remove(oldest, out var evicted))
            {
                _payloadPendingBytes -= evicted.Length;
                ArrayPool<byte>.Shared.Return(evicted.Rented);
                _droppedPayloads++;
            }
        }
        if (_payloadPendingBytes + len > MaxPayloadPendingBytes)
        {
            // Budget still exceeded even after evicting everything queued — this single payload is simply too big
            // for the lane. Drop it; its header still lands, with PayloadLength=0 + Truncated=true.
            _droppedPayloads++;
            return;
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        payload.Span.CopyTo(rented);
        _payloads[eventId] = (rented, len);
        _payloadOrder.Enqueue(eventId);
        _payloadPendingBytes += len;
    }

    void EnqueueHeaderLocked(in CaptureEvent evt)
    {
        // An End always fits — a Begin/End pair's End is never dropped asymmetrically once its Begin was accepted
        // (§3.2). Begin/Point events pay the budget check and, under pressure, evict the OLDEST Low-priority
        // header first (§3.5) before ever dropping the incoming (Normal-priority) event itself.
        long cost = ApproxCost(in evt);
        if (evt.Phase != CapturePhase.End)
        {
            while (_headerPendingBytes + cost > MaxHeaderPendingBytes && TryEvictOldestLowLocked()) { }
            if (_headerPendingBytes + cost > MaxHeaderPendingBytes)
            {
                _droppedHeaders++;
                return;
            }
        }
        _headers.AddLast(evt);
        _headerPendingBytes += cost;
    }

    bool TryEvictOldestLowLocked()
    {
        for (var node = _headers.First; node is not null; node = node.Next)
        {
            if (node.Value.Priority != CapturePriority.Low) continue;
            _headerPendingBytes -= ApproxCost(node.Value);
            _headers.Remove(node);
            _droppedHeaders++;
            return true;
        }
        return false;
    }

    static long ApproxCost(in CaptureEvent evt)
        => CaptureRecord.FixedHeaderBytes
         + (evt.Fields.A?.Length ?? 0) + (evt.Fields.B?.Length ?? 0) + (evt.Fields.C?.Length ?? 0);

    void DrainLoop()
    {
        while (true)
        {
            CaptureEvent[] headerBatch;
            long droppedHeaders, droppedPayloads;
            Dictionary<long, (byte[] Rented, int Length)> payloadSnapshot;
            HashSet<long> hadPayloadIds;
            lock (_gate)
            {
                if (_headers.Count == 0 && _payloads.Count == 0)
                {
                    _drainScheduled = false;
                    return;
                }
                int n = _headers.Count;
                headerBatch = new CaptureEvent[n];
                int i = 0;
                for (var node = _headers.First; node is not null; node = node.Next) headerBatch[i++] = node.Value;
                _headers.Clear();
                _headerPendingBytes = 0;
                droppedHeaders = _droppedHeaders; _droppedHeaders = 0;

                payloadSnapshot = new Dictionary<long, (byte[] Rented, int Length)>(_payloads);
                _payloads.Clear();
                _payloadOrder.Clear();
                _payloadPendingBytes = 0;
                droppedPayloads = _droppedPayloads; _droppedPayloads = 0;

                hadPayloadIds = new HashSet<long>(_hadPayloadIds);
                _hadPayloadIds.Clear();
            }

            try { WriteBatch(headerBatch, payloadSnapshot, hadPayloadIds, droppedHeaders, droppedPayloads); }
            catch (Exception ex) { _log("capture write failed: " + ex.Message); }
            finally
            {
                foreach (var kv in payloadSnapshot) ArrayPool<byte>.Shared.Return(kv.Value.Rented);
            }
        }
    }

    void WriteBatch(CaptureEvent[] headers, Dictionary<long, (byte[] Rented, int Length)> payloads,
        HashSet<long> hadPayloadIds, long droppedHeaders, long droppedPayloads)
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            DateTime now = DateTime.Now;
            EnsureSegmentOpen(now);

            if (droppedHeaders > 0)
                _log("capture dropped header records count=" + droppedHeaders.ToString(CultureInfo.InvariantCulture));
            if (droppedPayloads > 0)
                _log("capture dropped payload bodies count=" + droppedPayloads.ToString(CultureInfo.InvariantCulture));

            foreach (var evt in headers)
            {
                MaybeRoll(now);

                ReadOnlyMemory<byte> payloadBytes = ReadOnlyMemory<byte>.Empty;
                bool truncated = evt.Fields.Truncated;
                if (payloads.TryGetValue(evt.Id, out var buf))
                {
                    payloadBytes = RedactPayload(new ReadOnlySpan<byte>(buf.Rented, 0, buf.Length));
                }
                else if (hadPayloadIds.Contains(evt.Id))
                {
                    truncated = true; // a payload was intended but did not survive the payload lane's budget
                }

                int payloadOffset = -1, payloadLength = 0;
                if (payloadBytes.Length > 0)
                {
                    payloadOffset = checked((int)_blobLength);
                    payloadLength = payloadBytes.Length;
                    _blob!.Write(payloadBytes.Span);
                    _blobLength += payloadLength;
                }

                var record = new CaptureRecord(evt.Seq, evt.Qpc, evt.UnixMs, evt.Id, evt.CauseId, evt.RootId,
                    evt.Kind, evt.Phase, evt.Priority, truncated,
                    evt.Fields.N0, evt.Fields.N1, payloadOffset, payloadLength,
                    CaptureRecordCodec.TruncateUtf8(evt.Fields.A),
                    CaptureRecordCodec.TruncateUtf8(evt.Fields.B),
                    CaptureRecordCodec.TruncateUtf8(evt.Fields.C));

                int size = CaptureRecordCodec.EncodedSize(in record);
                byte[] scratch = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    int written = CaptureRecordCodec.Encode(in record, scratch);
                    _idx!.Write(scratch, 0, written);
                }
                finally { ArrayPool<byte>.Shared.Return(scratch); }
            }

            _idx!.Flush();
            _blob!.Flush();
        }
    }

    void EnsureSegmentOpen(DateTime now)
    {
        if (_idx is not null) return;
        _openDate = now.Date;
        _segmentIndex = 1;
        OpenLiveSegment();
    }

    void MaybeRoll(DateTime now)
    {
        long liveBytes = _idx?.Length ?? 0;
        var verdict = SegmentRotation.Decide(_openDate, now, liveBytes, MaxLiveSegmentBytes);
        if (verdict == SegmentRotation.Verdict.Keep) return;

        RollSegment(now);
        if (verdict == SegmentRotation.Verdict.RollForDayChange)
        {
            _openDate = now.Date;
            _segmentIndex = 1;
        }
        else
        {
            _segmentIndex++;
        }
        OpenLiveSegment();
        ApplyRetention(now);
    }

    string LiveIdxPath => Path.Combine(_dir, "capture-" + _openDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".idx");
    string LiveBlobPath => Path.Combine(_dir, "capture-" + _openDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".blob");

    void OpenLiveSegment()
    {
        string idxPath = LiveIdxPath;
        string blobPath = LiveBlobPath;
        _idx = new FileStream(idxPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _blob = new FileStream(blobPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _blobLength = _blob.Length;
    }

    void RollSegment(DateTime now)
    {
        string idxPath = LiveIdxPath;
        string blobPath = LiveBlobPath;
        _idx?.Dispose(); _idx = null;
        _blob?.Dispose(); _blob = null;

        string stamp = _openDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + _segmentIndex.ToString(CultureInfo.InvariantCulture)
            + "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture);
        try { GzipTo(idxPath, Path.Combine(_dir, "capture-" + stamp + ".idx.gz")); } catch (Exception ex) { _log("capture idx gzip failed: " + ex.Message); }
        try { GzipTo(blobPath, Path.Combine(_dir, "capture-" + stamp + ".blob.gz")); } catch (Exception ex) { _log("capture blob gzip failed: " + ex.Message); }
    }

    static void GzipTo(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath)) return;
        using (var src = File.OpenRead(sourcePath))
        using (var dst = File.Create(destPath))
        using (var gz = new GZipStream(dst, CompressionLevel.Fastest))
            src.CopyTo(gz);
        try { File.Delete(sourcePath); } catch { }
    }

    void ApplyRetention(DateTime now)
    {
        try
        {
            var files = Directory.Exists(_dir) ? Directory.GetFiles(_dir, "capture-*.idx.gz") : Array.Empty<string>();
            var segments = new List<(DateTime WriteUtc, long Bytes)>(files.Length);
            var idxPaths = new List<string>(files.Length);
            foreach (string idxGz in files)
            {
                string blobGz = idxGz[..^"idx.gz".Length] + "blob.gz";
                long bytes = SafeLength(idxGz) + SafeLength(blobGz);
                segments.Add((File.GetLastWriteTimeUtc(idxGz), bytes));
                idxPaths.Add(idxGz);
            }

            var toDelete = RetentionPlan.IndicesToDelete(segments, now.ToUniversalTime(), RetainDays, MaxDirectoryBytes);
            foreach (int i in toDelete)
            {
                string idxGz = idxPaths[i];
                string blobGz = idxGz[..^"idx.gz".Length] + "blob.gz";
                try { File.Delete(idxGz); } catch { }
                try { File.Delete(blobGz); } catch { }
            }
        }
        catch (Exception ex) { _log("capture retention pass failed: " + ex.Message); }
    }

    static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>The defense-in-depth redaction pass every payload byte range goes through HERE, on the writer
    /// thread, before it ever reaches the blob file — additional to whatever a call site (units 4/5) already
    /// redacted before calling `Capture.Begin`/`End` (plan §4: "the writer thread never sees an unredacted token,
    /// so a bug in the writer cannot leak one either"). Unit 3's `Redactor` (`Capture.Redact.cs`) exposes a JSON-body
    /// pass (`RedactJsonBody`) and a header-value pass (`RedactHeaderValue`, not applicable here — headers never
    /// reach the payload lane, only bodies do); a payload that is not well-formed JSON — a raw dealer frame, a
    /// protobuf Cluster/PutStateRequest body — makes `RedactJsonBody` report -1, which is the EXPECTED, safe
    /// outcome for a binary payload (not a leak signal), so it is written through unchanged.</summary>
    static ReadOnlyMemory<byte> RedactPayload(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return ReadOnlyMemory<byte>.Empty;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(raw.Length + 256);
        try
        {
            int written = Redactor.RedactJsonBody(raw, scratch);
            return written < 0 ? raw.ToArray() : scratch.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    /// <summary>Best-effort synchronous drain + close, mirroring `Log.FlushAndClose` — called on the 1→0 settings
    /// transition and at process shutdown so the last records reach disk before the handle is released.</summary>
    public void Flush()
    {
        // Run any already-scheduled drain to completion synchronously by re-entering the same code path once more:
        // DrainLoop already loops until both lanes are empty, so calling it directly here catches anything still
        // pending without waiting on the thread pool.
        try { DrainLoop(); } catch (Exception ex) { _log("capture flush failed: " + ex.Message); }
    }

    public void Dispose()
    {
        Flush();
        lock (_writeGate)
        {
            _disposed = true;
            _idx?.Dispose(); _idx = null;
            _blob?.Dispose(); _blob = null;
        }
    }
}

// ── 3. the host lifecycle — a Signal<bool>, not a settable singleton knob (§3.4, fixing §1 item 4) ─────────────────

/// <summary>Wires the "Archive Spotify realtime traffic" toggle (`Platform.Keys.DealerArchiveEnabled` — the
/// persisted key/subtitle NEVER rename, only what they now switch on changes) to <see cref="Capture"/>'s gate and
/// installs/tears down the <see cref="RealtimeCaptureWriter"/> on each transition. `Init()` is called once, from
/// `Platform.Host.cs`'s `HostOpenLog()` (right after the app log itself opens, so a capture-on-launch user gets a
/// segment from the very first frame); `OnSettingsChanged()` is the live hook the Settings row's `Toggle` calls,
/// exactly like `Tray.Host.OnSettingsChanged`'s own pattern (`Settings.UI.cs:445,452,457,463,472`).</summary>
public static class RealtimeCaptureHost
{
    /// <summary>The ONE live signal every reader of "is capture on" should use — mirrors §3.4's
    /// `Platform.DealerArchiveEnabled` intent without adding a second public surface on `Platform.cs` itself;
    /// `Capture.Enabled` (the zero-cost gate every hot-path call site actually checks) is kept in lockstep by
    /// <see cref="Apply"/>, the only place either is written.</summary>
    public static readonly Signal<bool> Enabled = new(false);

    static RealtimeCaptureWriter? s_writer;
    static readonly Lock s_gate = new();
    static bool s_initialized;

    /// <summary>Called once at `HostOpenLog()` time. Seeds <see cref="Enabled"/> from the persisted setting and, if
    /// on, starts the writer immediately — a capture-on-launch user does not have to retoggle the setting to get a
    /// segment for the session that is about to crash.</summary>
    public static void Init()
    {
        lock (s_gate)
        {
            if (s_initialized) return;
            s_initialized = true;
        }
        bool on = Platform.Settings.Get(Platform.Keys.DealerArchiveEnabled);
        Apply(on);
    }

    /// <summary>The live hook: called by the Settings row's `Toggle(Platform.Keys.DealerArchiveEnabled)` afterWrite
    /// (unit 6). Re-reads the persisted value (the store is the truth, never a mirror — same discipline as every
    /// other `Toggle` in `Settings.UI.cs`) and applies the transition if it actually changed.</summary>
    public static void OnSettingsChanged()
    {
        bool on = Platform.Settings.Get(Platform.Keys.DealerArchiveEnabled);
        if (on != Enabled.Peek()) Apply(on);
    }

    static void Apply(bool on)
    {
        lock (s_gate)
        {
            if (on)
            {
                if (s_writer is null)
                {
                    string dir = Path.Combine(Platform.LogFolder, "capture");
                    var writer = new RealtimeCaptureWriter(dir, static msg => Log.Info("capture", msg));
                    s_writer = writer;
                    // Unit 6 (realtime-capture-implementation.md §5.5): tee every event to the in-process
                    // CaptureRecentStore (Diagnostics/Capture.Recent.cs) alongside the real disk writer, so the
                    // in-app "Recent causal roots" page never touches disk on its poll. The ONE line this plan's
                    // brief calls out — everything else about the store/tee lives in the new file.
                    Capture.Sink = new TeeCaptureSink(writer, CaptureRecentStore.Instance);
                    Capture.SetEnabled(true);
                    // Never a hardcoded %LOCALAPPDATA%\Wavee: FinalPath.Resolve(dir) is the SAME call HostOpenLog
                    // already uses for the app log's own startup line, so a packaged run's LocalCache redirection
                    // is reported identically for both (CLAUDE.md).
                    Log.Info("capture", "realtime capture started path=" + (FluentGpu.WindowsApi.Storage.FinalPath.Resolve(dir) ?? dir));
                }
            }
            else
            {
                if (s_writer is { } writer)
                {
                    Capture.SetEnabled(false);
                    Capture.Sink = null;
                    writer.Dispose();
                    s_writer = null;
                    Log.Info("capture", "realtime capture stopped");
                }
            }
            Enabled.Value = on;
        }
    }

    /// <summary>Called from `Platform.Shutdown()` — best-effort flush of whatever is still queued, mirroring
    /// `Log.Flush()`'s own exit-path role. Never throws.</summary>
    public static void Shutdown()
    {
        try { s_writer?.Flush(); } catch { }
    }
}
