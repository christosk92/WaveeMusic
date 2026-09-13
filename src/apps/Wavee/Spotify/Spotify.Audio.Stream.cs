// ── Spotify/Spotify.Audio.Stream.cs ────────────────────────────────────────────────────────────────────────────────
// the CDN stream layer: the clear head, the read-ahead ring, the range fetcher, the disk cache
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 700 lines (plan §8.1)
// Spec: docs/plans/wavee/wavee-0.3-vorbis-implementation.md §5
//
// WHAT THIS FILE DELIVERS. `Spotify.Audio.Open` used to hand Wave 3 a `CtrStream`: ONE 128 KiB chunk, one HTTP range
// request per refill issued SYNCHRONOUSLY on the decode thread, no cache, no read-ahead, no cancel. Instant start, a
// cheap scrub and a warm second listen were all impossible in that shape, so the shape is replaced (no legacy paths).
//
// THE MOVING PARTS:
//   · `Body`    — the stores for ONE file, behind one `ReadAt`: the clear head file (≤ 80 KiB, no auth, no key), the
//                 ring, and `ChunkDiskCache`. It owns `FetchRange` (mirrors, failover, decrypt at the TRUE file offset)
//                 and `ProveHead` (the byte-exact splice proof against decrypted chunk 0).
//   · `Ring`    — slots of 64 KiB (the cache's granularity: a completed slot IS a cache chunk), direct-mapped by chunk
//                 index, sized in SECONDS off the file's byte rate — 30 s / 10 s metered / 600 s once throughput is ≥ 3×
//                 the byte rate (0.2.9's `ReadAheadPolicy`, `Wavee.Sdk/Streams/RangedHttpSource.cs:43-61`), capped at
//                 16 MiB. `ReadAt` copies or waits, bounded (8 s), and returns 0 only at a true EOF.
//   · `Fetcher` — ONE named thread, `Wavee.AudioFetch` (P10), a BOUNDED queue (C8: depth 8, DropOldest) and an
//                 in-flight `CancellationTokenSource` a seek cancels (C4). librespot and 0.2.9 never cancel, so a scrub
//                 of ten seeks queues ten fetches; here the tenth probe is the only request on the wire. Ping and
//                 throughput as librespot measures them (`audio/src/fetch/receive.rs:279-339`).
//   · `IRangeSource` — the wire as an interface; `Wavee.Tests/AudioStreamTests.cs` replaces it with a counting fake.
//
// THE TIMELINE THE LOG PRINTS (always on, category "audio"): audio.open.begin → audio.head → audio.open → audio.first
// (the first byte the decoder got, and from WHICH store) → audio.len → audio.splice → audio.range (per landed range,
// src=cdn|local) → audio.tail → audio.ring (the window filled). A seek prints audio.retarget; a stall audio.underrun.
//
// COORDINATES. FILE offsets are the raw Spotify object — what a Range header, the CTR block index and the cache's
// chunk index speak; the head file is the CLEAR version of the same bytes from 0 (why `HeadGainDb` reads byte 144, and
// why the splice proof needs no offset arithmetic). CONTAINER offsets are file minus `HeaderBytesFor(format)` (0xa7
// Ogg/MP3, 0 FLAC) — what the decoder sees. The ring and the cache are file-side; `Body.ReadAt`/`Retarget`/`ResumeFrom`
// are container-side, and `Body` is the only place that converts.
//
// LENGTH IS LEARNED, NOT ASKED FOR. A cold `Open` is head ‖ storage-resolve ‖ AP key, then the first body range and the
// tail — no separate `HEAD` request: the true length arrives on the first range's `Content-Range` (librespot does the
// same, `audio/src/fetch/mod.rs:437-506`). Until then `FileLength` is the catalogue duration × the rung's nominal byte
// rate and `LengthKnown` is false; the tail is queued only once the length is real.
//
// THREADING. `Open` blocks on the pump / an api thread, never the UI thread (C9). `ReadAt` blocks on the engine's
// decode-ahead thread and nowhere else. The fetcher owns its thread. No table, no signal (C1).

using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Wavee.Sdk.Streams;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        // ── 1. the wire, as a seam ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>One ranged GET, opened: returned when the response HEADERS arrived, not the body — the file's total
        /// length is on `Content-Range` and the caller wants it a whole body earlier.</summary>
        public interface IRangeReply : IDisposable
        {
            /// <summary>The WHOLE file's length from `Content-Range: bytes a-b/total`, or −1 when it was absent.</summary>
            long TotalLength { get; }
            /// <summary>Body bytes: &gt;0, or 0 at the end of this range (or on a cancelled / broken body).</summary>
            int Read(Span<byte> dst);
        }

        /// <summary>Where audio bytes come from. The ONE seam the tests replace.</summary>
        public interface IRangeSource
        {
            /// <summary>Open <paramref name="url"/> for the INCLUSIVE byte range [start, end]. Null on any failure.</summary>
            IRangeReply? Open(string url, long start, long end, CancellationToken ct);
        }

        /// <summary>The real wire: the pooled CDN client, HTTP/2 preferred per request with a downgrade allowed, one
        /// connection multiplexing a file's ranges (`EnableMultipleHttp2Connections = false` on <see cref="Cdn"/>), so a
        /// cancelled range is an `RST_STREAM`, not a dropped connection.</summary>
        public sealed class HttpRangeSource : IRangeSource
        {
            /// <summary>"Configuring TLS is expensive and should be done once per process" (librespot
            /// `core/src/http_client.rs:148`).</summary>
            public static readonly HttpRangeSource Shared = new();

            public IRangeReply? Open(string url, long start, long end, CancellationToken ct)
            {
                HttpResponseMessage? response = null;
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get, url)
                    {
                        Version = System.Net.HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                    };
                    message.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                    response = Cdn.Send(message, HttpCompletionOption.ResponseHeadersRead, ct);
                    if ((int)response.StatusCode is not (200 or 206)) { response.Dispose(); return null; }
                    long total = response.Content.Headers.ContentRange is { HasLength: true, Length: { } n } ? n
                        : (int)response.StatusCode == 200 ? response.Content.Headers.ContentLength ?? -1 : -1;
                    return new HttpReply(response, response.Content.ReadAsStream(ct), total);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                                              or InvalidOperationException or ObjectDisposedException)
                {
                    response?.Dispose();
                    return null;
                }
            }

            sealed class HttpReply(HttpResponseMessage response, Stream body, long total) : IRangeReply
            {
                public long TotalLength => total;

                public int Read(Span<byte> dst)
                {
                    try { return body.Read(dst); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                                  or HttpRequestException or InvalidOperationException)
                    {
                        return 0;
                    }
                }

                public void Dispose()
                {
                    try { body.Dispose(); } catch (Exception) { /* a reset HTTP/2 stream disposes noisily; nothing to keep */ }
                    try { response.Dispose(); } catch (Exception) { }
                }
            }
        }

        // ── 2. the disk cache adapter ────────────────────────────────────────────────────────────────────────────────
        //
        // `Wavee.Sdk.Streams.ChunkDiskCache` is reused UNCHANGED: sparse `.enc` + `.map` per file id, SHA-256 on every
        // read, the background `BelowNormal` writer, the free-space reserve. What it does not know is where Wavee's
        // cache lives and how the settings map onto a policy — this (0.2.9's `AudioBodyDiskCache.cs:17-27`, same shape).

        /// <summary>Wavee's side of <see cref="ChunkDiskCache"/>: the root, the policy, and the reserve rule as a pure
        /// function a test can pin without a volume.</summary>
        public static class DiskCache
        {
            /// <summary>64 KiB. The ring's slot size is this, so one completed slot is exactly one cache chunk.</summary>
            public const int ChunkBytes = ChunkDiskCache.ChunkBytes;

            /// <summary>The free space the cache never consumes: `max(5 GiB, 5 % of the volume)` — the rule
            /// `ChunkDiskCache.Capacity` enforces (`ChunkDiskCache.cs:83, 605-625`), restated because the SDK's copy is
            /// private and the number is a promise to the user's disk.</summary>
            public static long ReserveBytes(long volumeTotalBytes) => Math.Max(5L << 30, volumeTotalBytes / 20);

            /// <summary>May <paramref name="growthBytes"/> commit? False keeps the bytes in RAM; the track still plays.</summary>
            public static bool CanCommit(long freeBytes, long growthBytes, long volumeTotalBytes)
                => freeBytes - growthBytes >= ReserveBytes(volumeTotalBytes);

            /// <summary>`%LOCALAPPDATA%\Wavee\cache\audio` (the package's LocalCache on a packaged run, by redirection).</summary>
            public static string DefaultDirectory() => Path.Combine(Platform.LocalFolder, "cache", "audio");

            static ChunkDiskCache? s_shared;
            static int s_failed;
            static readonly Lock Gate = new();

            /// <summary>The process-wide cache over the live settings (re-read on every operation, so a settings change
            /// needs no relaunch; `AudioBodyCacheEnabled = false` makes it read-only, as 0.2.9's policy did). Null when
            /// it could not be created — every call site treats null as "no cache".</summary>
            public static ChunkDiskCache? Shared
            {
                get
                {
                    if (Volatile.Read(ref s_shared) is { } ready) return ready;
                    if (Volatile.Read(ref s_failed) != 0) return null;
                    lock (Gate)
                    {
                        if (s_shared is not null || s_failed != 0) return s_shared;
                        try
                        {
                            s_shared = new ChunkDiskCache(static () => new ChunkCachePolicy(
                                    Platform.Settings.Get(Platform.Keys.AudioBodyCacheEnabled),
                                    ChunkDiskCache.ResolveDirectory(
                                        Platform.Settings.Get(Platform.Keys.AudioBodyCacheBasePath), DefaultDirectory()),
                                    (AudioCacheBudgetMode)Math.Clamp(Platform.Settings.Get(Platform.Keys.AudioBodyCacheBudgetMode), 0, 2),
                                    Math.Max(ChunkDiskCache.MinBudgetBytes, Platform.Settings.Get(Platform.Keys.AudioBodyCacheBudgetBytes)),
                                    Math.Clamp(Platform.Settings.Get(Platform.Keys.AudioBodyCacheBudgetPercent), 0, 90)),
                                default, DefaultDirectory());
                        }
                        catch (Exception ex)
                        {
                            s_failed = 1;
                            Log.Warn("audio", "audio body cache unavailable", ex);
                        }
                        return s_shared;
                    }
                }
            }
        }

        // ── 3. the fetcher ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One request on the wire. Epoch-stamped (C4): a sequential range whose epoch a seek superseded is
        /// dropped before it reaches the wire; one already landing is still STORED (bytes are bytes). A probe is issued
        /// BY the seek, so it is never stale. The tail feeds the gapless length and the cache, never the ring's window.</summary>
        public readonly record struct RangeRequest(long Start, long End, uint Epoch, bool Probe, bool Tail = false);

        /// <summary>Which store answered a range — a LOG value first, bookkeeping second.</summary>
        public enum Source : byte
        {
            /// <summary>The ring or the disk cache already held every byte; no request was made.</summary>
            Local = 0,
            /// <summary>The CDN answered.</summary>
            Cdn,
            /// <summary>Every mirror refused; the url set was dropped.</summary>
            Refused,
            /// <summary>Superseded by a seek (dropped from the queue, or cancelled in flight).</summary>
            Stale,
        }

        /// <summary>The one network thread for audio bytes: a bounded queue (C8), one range in flight at a time, an
        /// in-flight cancel a seek reaches (C4). Shared by the playing and the prepared next track.</summary>
        public sealed class Fetcher : IDisposable
        {
            /// <summary>C8. DropOldest: a burst of seeks throws stale intentions away instead of growing a latency trap.</summary>
            public const int QueueDepth = 8;

            /// <summary>The longest single range: ~13 s of 320 kbit/s. Larger than librespot's 64 KiB minimum
            /// (`mod.rs:103`), no larger than go-librespot's 512 KiB chunk (`chunked-reader.go:22`); the ring, not the
            /// request, decides how far ahead we are.</summary>
            public const int MaxRangeBytes = 512 * 1024;

            /// <summary>A range's own deadline. Generous on purpose: at librespot's minimum throughput (8 KiB/s,
            /// `mod.rs:104`) a full range takes a minute, and the ring's 8 s read bound reports the stall long before.</summary>
            const int RangeTimeoutMs = 60_000;

            public static readonly Fetcher Shared = new();

            readonly Channel<(Body Body, RangeRequest Req)> _queue;
            readonly Lock _gate = new();
            readonly (Body Body, RangeRequest Req)[] _drain = new (Body, RangeRequest)[QueueDepth];
            Thread? _thread;
            CancellationTokenSource? _inFlight;
            Body? _inFlightBody;
            bool _inFlightTail;
            byte[]? _scratch, _chunkScratch;
            int _ping0 = 500, _ping1 = 500;
            int _requests, _cancelled, _peak, _live, _outstanding;

            public Fetcher()
            {
                // NOT SingleReader: a seek drains the queue from the caller's thread while this thread waits on it.
                _queue = Channel.CreateBounded<(Body Body, RangeRequest Req)>(
                    new BoundedChannelOptions(QueueDepth) { FullMode = BoundedChannelFullMode.DropOldest },
                    Dropped);
            }

            /// <summary>librespot's `initial_ping_time_estimate` (500 ms, `mod.rs:108`), then the median of the last three
            /// times-to-headers capped at its `maximum_assumed_ping_time` of 1.5 s (`receive.rs:311-339`).</summary>
            public int PingMs { get; private set; } = 500;

            /// <summary>Measured throughput folded `(old + new) / 2`, floored at 8 KiB/s (`receive.rs:279-300`).</summary>
            public long BytesPerSecond { get; private set; } = 64 * 1024;

            /// <summary>Ranges put on the wire (disk- and ring-answered ranges are not requests).</summary>
            public int Requests => Volatile.Read(ref _requests);
            /// <summary>Ranges cancelled mid-flight by a seek or a dispose.</summary>
            public int Cancelled => Volatile.Read(ref _cancelled);
            /// <summary>The most ranges ever in flight at once. This thread's promise is 1.</summary>
            public int PeakInFlight => Volatile.Read(ref _peak);
            /// <summary>Queued + in-flight work. Zero means the fetcher is idle.</summary>
            public int Outstanding => Volatile.Read(ref _outstanding);

            /// <summary>Queue one range. Never blocks; a full queue drops its OLDEST entry (C8).</summary>
            public void Enqueue(Body body, in RangeRequest req)
            {
                StartThread();
                Interlocked.Increment(ref _outstanding);
                if (!_queue.Writer.TryWrite((body, req))) Interlocked.Decrement(ref _outstanding);
            }

            /// <summary>A seek on <paramref name="body"/>: cancel its in-flight range (never the tail), drop its queued
            /// ranges (keeping a pending tail), and put <paramref name="probe"/> at the FRONT of the queue — so the probe
            /// is the only request in flight when it is issued and nothing is served before it. A default probe only
            /// cancels and drops.</summary>
            public void Retarget(Body body, in RangeRequest probe)
            {
                StartThread();
                CancelAndDrain(body, keepTail: true, probe.End > probe.Start ? probe : null);
            }

            /// <summary>The body is gone: cancel and drop everything of it, the tail included.</summary>
            public void Forget(Body body) => CancelAndDrain(body, keepTail: false, null);

            void CancelAndDrain(Body body, bool keepTail, RangeRequest? first)
            {
                lock (_gate)
                {
                    if (_inFlight is { } live && ReferenceEquals(_inFlightBody, body) && !(keepTail && _inFlightTail))
                    {
                        _inFlight = null;
                        Interlocked.Increment(ref _cancelled);
                        try { live.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    int kept = 0;
                    while (_queue.Reader.TryRead(out var item))
                    {
                        if (ReferenceEquals(item.Body, body) && !(keepTail && item.Req.Tail))
                        {
                            Interlocked.Decrement(ref _outstanding);
                            item.Body.Dropped(item.Req);
                            continue;
                        }
                        if (kept < _drain.Length) { _drain[kept++] = item; continue; }
                        Interlocked.Decrement(ref _outstanding);
                        item.Body.Dropped(item.Req);
                    }
                    if (first is { } probe)
                    {
                        Interlocked.Increment(ref _outstanding);
                        if (!_queue.Writer.TryWrite((body, probe))) Interlocked.Decrement(ref _outstanding);
                    }
                    // Re-queued behind the probe; a full queue drops the oldest of THESE (C8), never the probe.
                    for (int i = 0; i < kept && i < QueueDepth - 1; i++) _queue.Writer.TryWrite(_drain[i]);
                    for (int i = QueueDepth - 1; i < kept; i++) { Interlocked.Decrement(ref _outstanding); _drain[i].Body.Dropped(_drain[i].Req); }
                    Array.Clear(_drain);
                }
            }

            void Dropped((Body Body, RangeRequest Req) item)
            {
                Interlocked.Decrement(ref _outstanding);
                item.Body.Dropped(item.Req);
            }

            void StartThread()
            {
                if (Volatile.Read(ref _thread) is not null) return;
                lock (_gate)
                {
                    if (_thread is not null) return;
                    var thread = new Thread(Loop) { IsBackground = true, Name = "Wavee.AudioFetch" };
                    thread.Start();
                    _thread = thread;
                }
            }

            void Loop()
            {
                _scratch = GC.AllocateUninitializedArray<byte>(MaxRangeBytes, pinned: true);
                _chunkScratch = GC.AllocateUninitializedArray<byte>(Ring.SlotBytes, pinned: true);
                while (true)
                {
                    bool more;
                    try
                    {
                        ValueTask<bool> wait = _queue.Reader.WaitToReadAsync();
                        more = wait.IsCompleted ? wait.Result : wait.AsTask().GetAwaiter().GetResult();
                    }
                    catch (ChannelClosedException) { return; }
                    if (!more) return;
                    while (_queue.Reader.TryRead(out var item))
                    {
                        try { Serve(item.Body, item.Req); }
                        catch (Exception ex) { Log.Error("audio", "audio fetch faulted", ex); }
                        finally { Interlocked.Decrement(ref _outstanding); }
                    }
                }
            }

            void Serve(Body body, in RangeRequest req)
            {
                if (body.Disposed) return;
                if (!req.Probe && !req.Tail && req.Epoch != body.Epoch) { body.Settle(req, Source.Stale); return; }

                long start = req.Start, end = req.End;
                body.FillFromDisk(req, ref start, ref end, _chunkScratch!);     // the disk answers first; the range shrinks
                if (start >= end) { body.Settle(req, Source.Local); return; }

                var cts = new CancellationTokenSource(RangeTimeoutMs);
                lock (_gate) { _inFlight = cts; _inFlightBody = body; _inFlightTail = req.Tail; }
                int live = Interlocked.Increment(ref _live);
                if (live > _peak) Volatile.Write(ref _peak, live);
                Interlocked.Increment(ref _requests);

                int got = 0;
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    int want = (int)Math.Min(MaxRangeBytes, end - start);
                    got = body.FetchRange(start, end, _scratch.AsSpan(0, want), cts.Token, out long headersAt);
                    if (got > 0)
                    {
                        Observe(t0, headersAt, got);
                        body.Land(req, start, _scratch.AsSpan(0, got));
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _live);
                    lock (_gate)
                    {
                        if (ReferenceEquals(_inFlight, cts)) { _inFlight = null; _inFlightBody = null; }
                    }
                    cts.Dispose();
                }
                body.Settle(req, got > 0 ? Source.Cdn : got < 0 ? Source.Stale : Source.Refused);
            }

            void Observe(long t0, long headersAt, int got)
            {
                int ping = Math.Clamp((int)Stopwatch.GetElapsedTime(t0, headersAt).TotalMilliseconds, 1, 1_500);
                PingMs = MedianOf3(_ping0, _ping1, ping);
                _ping0 = _ping1;
                _ping1 = ping;
                long bodyMs = Math.Max(1, (long)Stopwatch.GetElapsedTime(headersAt).TotalMilliseconds);
                BytesPerSecond = (BytesPerSecond + Math.Max(8 * 1024, got * 1000L / bodyMs)) / 2;
            }

            /// <summary>The median of three (librespot sorts a three-element vec, `receive.rs:322-331`). PURE.</summary>
            public static int MedianOf3(int a, int b, int c)
                => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

            /// <summary>librespot's prefetch trigger (`receive.rs:504-516`, `prefetch_threshold_factor = 4`): keep at
            /// least `max(4 × ping × nominal byte rate, ping × measured throughput)` outstanding, so a slow link asks for
            /// more at a time rather than more often. PURE.</summary>
            public static long PrefetchThresholdBytes(int pingMs, int fileBytesPerSecond, long measuredBytesPerSecond)
            {
                double ping = Math.Clamp(pingMs, 1, 1_500) / 1000d;
                return (long)Math.Max(4d * ping * fileBytesPerSecond, ping * measuredBytesPerSecond);
            }

            public void Dispose()
            {
                _queue.Writer.TryComplete();
                lock (_gate)
                {
                    try { _inFlight?.Cancel(); } catch (ObjectDisposedException) { }
                    _inFlight = null;
                    _inFlightBody = null;
                }
            }
        }

        // ── 4. the ring ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The read-ahead ring: slots of <see cref="SlotBytes"/> holding PLAINTEXT file bytes, direct-mapped by
        /// chunk index (chunk c lives in slot c mod N, so eviction is an overwrite and the lookup is one division).</summary>
        public sealed class Ring
        {
            /// <summary>= <see cref="DiskCache.ChunkBytes"/>.</summary>
            public const int SlotBytes = DiskCache.ChunkBytes;
            /// <summary>256 KiB behind the cursor stay resident: a scrub back into the last seconds is free.</summary>
            public const int KeepBehindSlots = 4;
            /// <summary>The seek probe (§4.2), aligned out to its enclosing slot so a probe is also a cacheable chunk.</summary>
            public const int ProbeWindow = 48 * 1024;
            public const long MaxWindowBytes = 16L << 20;
            /// <summary>`ReadAt`'s bound: past it a stall is a fault (−1), never a silent truncation. The 4 ms / 8 s pair
            /// is 0.2.9's `PrefetchingReadStream`'s and Wave 3's `Prefetching`'s.</summary>
            public const int DefaultWaitMs = 8_000;
            const int PollMs = 4;
            const int RefusedBackoffMs = 250;

            readonly Body _body;
            readonly Fetcher _fetch;
            readonly byte[][] _slots;
            readonly long[] _slotChunk;             // the chunk index resident in each slot, −1 empty
            readonly int[] _slotFilled;
            readonly Lock _gate = new();
            readonly object _wake = new();
            readonly int _waitMs;
            readonly long _windowBytes;
            long _cursor;
            long _want = -1;
            long _pendingStart = -1;                // the ONE sequential/probe range queued or in flight, by identity
            long _pendingEnd;
            uint _pendingEpoch;
            long _retryAfter;
            bool _primed;                           // the window was whole since the last Kick / cold Retarget
            uint _epoch;
            int _waits, _starves;

            internal Ring(Body body, Fetcher fetch, int seconds, int fileBytesPerSecond, int waitMs)
            {
                _body = body;
                _fetch = fetch;
                _waitMs = Math.Max(1, waitMs);
                Seconds = seconds;
                FileBytesPerSecond = Math.Max(1, fileBytesPerSecond);
                int count = SlotCount(seconds, FileBytesPerSecond, body.FileLength);
                _slots = new byte[count][];
                _slotChunk = new long[count];
                _slotFilled = new int[count];
                for (int i = 0; i < count; i++)
                {
                    _slots[i] = GC.AllocateUninitializedArray<byte>(SlotBytes, pinned: true);
                    _slotChunk[i] = -1;
                }
                _windowBytes = (long)(count - KeepBehindSlots) * SlotBytes;
            }

            /// <summary>The load/seek epoch this ring serves (C4).</summary>
            public uint Epoch => Volatile.Read(ref _epoch);
            public int Slots => _slots.Length;
            /// <summary>Seconds of audio the ring aims to hold ahead of the cursor.</summary>
            public int Seconds { get; }
            public int FileBytesPerSecond { get; }
            /// <summary>Reads that had to wait for bytes — the underrun counter, always on.</summary>
            public int Waits => Volatile.Read(ref _waits);
            /// <summary>Reads that gave up after the bound.</summary>
            public int Starves => Volatile.Read(ref _starves);
            /// <summary>The file offset a read is blocked on, or −1.</summary>
            public long Want => Volatile.Read(ref _want);
            /// <summary>The decoder's position, FILE coordinates.</summary>
            public long Cursor => Volatile.Read(ref _cursor);

            /// <summary>0.2.9's tiers (`RangedHttpSource.cs:43-61`): 10 s metered, 600 s (the whole file, capped) once the
            /// measured throughput is ≥ 3× the file's byte rate, 30 s otherwise. PURE; the one place the numbers live.</summary>
            public static int ReadAheadSeconds(bool metered, long measuredBytesPerSec, int fileBytesPerSec)
                => metered ? 10 : measuredBytesPerSec >= 3L * Math.Max(1, fileBytesPerSec) ? 600 : 30;

            /// <summary>`ceil(min(seconds × rate, file, 16 MiB) / 64 KiB)`, at least 8, plus the 4 kept behind. PURE.</summary>
            public static int SlotCount(int seconds, int fileBytesPerSec, long fileLength)
            {
                long bytes = Math.Min((long)Math.Max(1, seconds) * Math.Max(1, fileBytesPerSec),
                                      Math.Min(Math.Max(SlotBytes, fileLength), MaxWindowBytes));
                return (int)Math.Max(8, (bytes + SlotBytes - 1) / SlotBytes) + KeepBehindSlots;
            }

            /// <summary>The decoder's read, FILE coordinates. Copies what is resident; otherwise asks for it and waits,
            /// bounded. 0 only at EOF; −1 when superseded (<paramref name="epoch"/> is stale) or starved.</summary>
            public int ReadAt(long fileOffset, Span<byte> dst, uint epoch)
            {
                if (fileOffset < 0 || dst.Length == 0 || AtEnd(fileOffset)) return 0;
                long deadline = Environment.TickCount64 + _waitMs;
                bool waited = false;
                while (true)
                {
                    int n;
                    lock (_gate) n = TryCopy(fileOffset, dst);
                    if (n > 0)
                    {
                        if (waited) { Interlocked.Increment(ref _waits); Volatile.Write(ref _want, -1); }
                        Advance(fileOffset + n, demand: false);
                        return n;
                    }
                    if (epoch != Epoch || _body.Disposed) return -1;
                    if (AtEnd(fileOffset)) return 0;
                    Volatile.Write(ref _want, fileOffset);
                    Advance(fileOffset, demand: true);
                    waited = true;
                    lock (_wake) Monitor.Wait(_wake, PollMs);
                    if (Environment.TickCount64 < deadline) continue;
                    Interlocked.Increment(ref _starves);
                    Log.Warn("audio", $"audio.underrun file={_body.FileIdHex} at={fileOffset} waitMs={_waitMs} "
                                      + $"cursor={Cursor} ring={Seconds}s slots={Slots} inflight={_fetch.Outstanding}");
                    return -1;
                }
            }

            /// <summary>EOF is only EOF once the length is REAL: past the catalogue estimate a read waits for
            /// `Content-Range` instead of truncating the track.</summary>
            bool AtEnd(long fileOffset) => fileOffset >= _body.FileLength && _body.LengthKnown;

            /// <summary>A seek (C4): bump the epoch and move the cursor. When the probe's slot is already resident the
            /// seek costs NOTHING — no cancel, no request. Otherwise the in-flight range is cancelled, the queue drained
            /// and the probe (<see cref="ProbeWindow"/>, slot-aligned) put on the wire first. Nothing is evicted: every
            /// slot is tagged with its chunk, so bytes behind stay useful until the ring wraps over them.</summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch)
            {
                long length = _body.FileLength;
                long start = AlignDown(Math.Clamp(probeOffset, 0, Math.Max(0, length - 1)));
                long end = Math.Min(length, AlignUp(start + Math.Max(1, probeBytes)));
                bool resident;
                lock (_gate)
                {
                    Volatile.Write(ref _epoch, epoch);
                    Volatile.Write(ref _cursor, Math.Clamp(probeOffset, 0, length));
                    _retryAfter = 0;                                   // a user gesture outranks a backoff
                    resident = Holds(start, end);
                    if (!resident) { _pendingStart = start; _pendingEnd = end; _pendingEpoch = epoch; _primed = false; }
                }
                Log.Info("audio", $"audio.retarget file={_body.FileIdHex} at={probeOffset} probe={start}..{end} "
                                  + $"epoch={epoch} resident={(resident ? 1 : 0)}");
                if (!resident) _fetch.Retarget(_body, new RangeRequest(start, end, epoch, Probe: true));
            }

            /// <summary>The seek landed on a page: the sequential fill continues from there.</summary>
            public void ResumeFrom(long fileOffset)
            {
                Volatile.Write(ref _cursor, Math.Clamp(fileOffset, 0, _body.FileLength));
                Advance(fileOffset, demand: false);
            }

            /// <summary>A refused head splice: the bytes the decoder already took may be wrong, so its epoch goes stale
            /// (its next miss answers −1, and `Body.SpliceProof == 2` tells the adapter to restart at 0). The pending
            /// range stays valid.</summary>
            internal void Supersede()
            {
                lock (_gate) Volatile.Write(ref _epoch, _epoch + 1);
                WakeWaiters();
            }

            /// <summary>Put the first range on the wire at <paramref name="fileOffset"/> (it counts as the pending one).</summary>
            internal void Kick(long fileOffset)
            {
                lock (_gate) _primed = false;
                Volatile.Write(ref _cursor, fileOffset);
                Advance(fileOffset, demand: false);
            }

            /// <summary>Publish landed PLAINTEXT. Requests are slot-aligned, so a chunk is complete or is the file's last.</summary>
            internal void Land(long start, ReadOnlySpan<byte> plain)
            {
                lock (_gate)
                {
                    long at = start;
                    int offset = 0;
                    while (offset < plain.Length)
                    {
                        long chunk = at / SlotBytes;
                        int within = (int)(at - chunk * SlotBytes);
                        int n = Math.Min(plain.Length - offset, SlotBytes - within);
                        int slot = (int)(chunk % _slots.Length);
                        if (within == 0)
                        {
                            plain.Slice(offset, n).CopyTo(_slots[slot]);
                            _slotChunk[slot] = chunk;
                            _slotFilled[slot] = n;
                        }
                        else if (_slotChunk[slot] == chunk && _slotFilled[slot] == within)
                        {
                            plain.Slice(offset, n).CopyTo(_slots[slot].AsSpan(within));
                            _slotFilled[slot] = within + n;
                        }
                        offset += n;
                        at += n;
                    }
                }
                WakeWaiters();
            }

            /// <summary>A range finished (any outcome). Frees the pending identity when it is this range, arms a 250 ms
            /// backoff when every mirror refused (a dead CDN costs four requests a second until the read bound reports
            /// it, never a busy loop), and re-plans.</summary>
            internal void Settled(in RangeRequest req, bool refused)
            {
                lock (_gate)
                {
                    if (req.Start == _pendingStart && req.Epoch == _pendingEpoch)
                    {
                        _pendingStart = -1;
                        if (refused) _retryAfter = Environment.TickCount64 + RefusedBackoffMs;
                    }
                }
                WakeWaiters();
                Advance(Cursor, demand: false);
            }

            /// <summary>A queued range was dropped (DropOldest, or a seek's drain) — runs under the channel's lock, so it
            /// touches one field and wakes nobody; the next read or landing re-plans.</summary>
            internal void Dropped(in RangeRequest req)
            {
                if (Interlocked.Read(ref _pendingStart) == req.Start && req.Epoch == _pendingEpoch)
                    Interlocked.CompareExchange(ref _pendingStart, -1, req.Start);
            }

            internal void WakeWaiters() { lock (_wake) Monitor.PulseAll(_wake); }

            /// <summary>Is [start, end) resident, every chunk complete? Callers hold <see cref="_gate"/> or accept a race
            /// that at worst costs one redundant request.</summary>
            internal bool Holds(long start, long end)
            {
                for (long at = AlignDown(start); at < end; at += SlotBytes)
                {
                    long chunk = at / SlotBytes;
                    int slot = (int)(chunk % _slots.Length);
                    if (_slotChunk[slot] != chunk || _slotFilled[slot] < ExpectedLength(chunk)) return false;
                }
                return true;
            }

            internal bool HoldsLocked(long start, long end) { lock (_gate) return Holds(start, end); }

            /// <summary>Is the chunk holding <paramref name="fileOffset"/> the one the pending range starts at?</summary>
            internal bool IsPending(long fileOffset) { lock (_gate) return _pendingStart >= 0 && fileOffset >= _pendingStart && fileOffset < _pendingEnd; }

            /// <summary>Move the cursor forward (or anywhere, on a demand), then plan ONE range from the first hole in the
            /// window ahead of the cursor, when nothing is pending (see <see cref="TryPlan"/>'s hysteresis).</summary>
            void Advance(long fileOffset, bool demand)
            {
                RangeRequest req;
                lock (_gate)
                {
                    long cursor = Cursor;
                    if (demand ? fileOffset != cursor : fileOffset > cursor) Volatile.Write(ref _cursor, fileOffset);
                    if (!TryPlan(out req)) return;
                }
                _fetch.Enqueue(_body, req);
            }

            /// <summary>HYSTERESIS: fill until the window is whole, then stay quiet until the run ahead of the cursor drops
            /// below the low-water mark, then refill to whole again. Without it a full ring would issue a 64 KiB request
            /// every time the cursor crossed a slot (~1.6 s at 320 kbit/s); with it steady-state listening costs one
            /// 512 KiB range per ~13 s.</summary>
            bool TryPlan(out RangeRequest req)
            {
                req = default;
                if (_pendingStart >= 0 || _body.Disposed || Environment.TickCount64 < _retryAfter) return false;
                long length = _body.FileLength;
                long from = AlignDown(Math.Clamp(Cursor, 0, length));
                long limit = Math.Min(length, from + _windowBytes);
                long hole = -1;
                for (long at = from; at < limit; at += SlotBytes)
                {
                    if (Holds(at, at + 1)) continue;
                    hole = at;
                    break;
                }
                if (hole < 0) { _primed = true; return false; }
                if (_primed)
                {
                    if (hole - from >= LowWaterBytes()) return false;
                    _primed = false;
                }
                long end = Math.Min(length, Math.Min(AlignUp(limit), hole + Fetcher.MaxRangeBytes));
                if (end <= hole) return false;
                _pendingStart = hole;
                _pendingEnd = end;
                _pendingEpoch = Epoch;
                req = new RangeRequest(hole, end, _pendingEpoch, Probe: false);
                return true;
            }

            /// <summary>Refill when fewer bytes than this are resident ahead: one full range short of the window, but never
            /// below librespot's prefetch rule (`receive.rs:504-516`: `max(4 × ping × nominal rate, ping × throughput)`),
            /// so a slow link starts its refill earlier.</summary>
            long LowWaterBytes()
            {
                long librespot = Math.Min(_windowBytes,
                    Fetcher.PrefetchThresholdBytes(_fetch.PingMs, FileBytesPerSecond, _fetch.BytesPerSecond));
                return Math.Max(SlotBytes, Math.Max(librespot, _windowBytes - Fetcher.MaxRangeBytes));
            }

            int TryCopy(long fileOffset, Span<byte> dst)
            {
                long chunk = fileOffset / SlotBytes;
                int slot = (int)(chunk % _slots.Length);
                if (_slotChunk[slot] != chunk) return 0;
                int within = (int)(fileOffset - chunk * SlotBytes);
                long available = Math.Min(_slotFilled[slot] - within, _body.FileLength - fileOffset);
                if (available <= 0) return 0;
                int n = (int)Math.Min(dst.Length, available);
                _slots[slot].AsSpan(within, n).CopyTo(dst);
                return n;
            }

            int ExpectedLength(long chunk) => (int)Math.Clamp(_body.FileLength - chunk * SlotBytes, 0, SlotBytes);

            public static long AlignDown(long value) => value / SlotBytes * SlotBytes;
            public static long AlignUp(long value) => (value + SlotBytes - 1) / SlotBytes * SlotBytes;
        }

        // ── 5. the body ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What <see cref="Open(in FileChoice, CancellationToken)"/> hands the pump: the stores and the fetcher
        /// for ONE file. Disposal cancels this file's work. Wave 3's `RingSource` wraps it (plan §5.5).</summary>
        public sealed class Body : IDisposable
        {
            readonly IRangeSource _source;
            readonly string[] _mirrors;
            readonly byte[]? _key;
            readonly Fetcher _fetcher;
            readonly ChunkDiskCache? _disk;
            readonly Ring _ring;
            readonly long _t0 = Stopwatch.GetTimestamp();
            byte[]? _head;
            long _fileLength;
            long _tailGranule = -1, _tailCandidate = -1;
            int _lengthKnown, _mirror, _disposed, _firstServed, _proof, _tailQueued, _sizeDeclared, _ringLogged;

            /// <summary>Build a body. Nothing here touches the network, so a test builds one over a fake source and a
            /// temp-directory cache.</summary>
            /// <param name="skip">`HeaderBytesFor(format)`: 0xa7 for Ogg/MP3, 0 for FLAC and external bodies.</param>
            /// <param name="fileLength">The RAW length, or the catalogue estimate when <paramref name="lengthKnown"/> is false.</param>
            /// <param name="head">The clear head file, or null. Serves [0, head.Length) until chunk 0 proves it.</param>
            /// <param name="metered">Picks the 10 s tier.</param>
            /// <param name="waitMs">`ReadAt`'s bound; <see cref="Ring.DefaultWaitMs"/> outside tests.</param>
            public Body(IRangeSource source, string[] mirrors, byte[]? key, int skip, long fileLength, bool lengthKnown,
                long durationMs, Format fmt, float gainDb, string fileIdHex, byte[]? head, ChunkDiskCache? disk,
                Fetcher? fetcher = null, bool metered = false, int waitMs = Ring.DefaultWaitMs)
            {
                _source = source;
                _mirrors = mirrors.Length > 0 ? mirrors : [""];
                _key = key;
                Skip = Math.Max(0, skip);
                _fileLength = Math.Max(Skip + 1, fileLength);
                _lengthKnown = lengthKnown ? 1 : 0;
                DurationMs = durationMs;
                Fmt = fmt;
                GainDb = gainDb;
                FileIdHex = fileIdHex;
                _head = head is { Length: > 0 } ? head : null;
                _disk = disk;
                _fetcher = fetcher ?? Fetcher.Shared;
                int rate = RateOf(_fileLength - Skip, durationMs, fmt);
                _ring = new Ring(this, _fetcher, Ring.ReadAheadSeconds(metered, _fetcher.BytesPerSecond, rate), rate, waitMs);
            }

            /// <summary>CONTAINER bytes — what the decoder sees.</summary>
            public long Length => Math.Max(0, FileLength - Skip);
            /// <summary>RAW file bytes.</summary>
            public long FileLength => Interlocked.Read(ref _fileLength);
            /// <summary>False while <see cref="FileLength"/> is still the catalogue estimate.</summary>
            public bool LengthKnown => Volatile.Read(ref _lengthKnown) != 0;
            public int Skip { get; }
            public long DurationMs { get; }
            public Format Fmt { get; }
            public float GainDb { get; }
            public string FileIdHex { get; }
            /// <summary>`Length × 1000 / DurationMs` once known (the nominal rung rate before) — the seek slope.</summary>
            public int BytesPerSecond => RateOf(Length, DurationMs, Fmt);
            /// <summary>Clear head bytes still held (0 once proven or refused).</summary>
            public int HeadBytes => Volatile.Read(ref _head)?.Length ?? 0;
            /// <summary>The last Ogg page's granule from the tail, or −1. What `GaplessInfo.ExactFrames` is.</summary>
            public long TailGranule => Interlocked.Read(ref _tailGranule);
            /// <summary>0 not yet checked · 1 the head equals decrypted chunk 0 byte for byte · 2 refused.</summary>
            public int SpliceProof => Volatile.Read(ref _proof);
            public uint Epoch => _ring.Epoch;
            public Ring Ring => _ring;
            internal bool Disposed => Volatile.Read(ref _disposed) != 0;
            static bool IsOgg(Format f) => f is Format.OggVorbis96 or Format.OggVorbis160 or Format.OggVorbis320;

            /// <summary>Put the first body range on the wire — from the slot the head stops covering, which for an 80 KiB
            /// head is chunk 0, the chunk the splice proof needs (§5.2) — and the tail when the length is already known.</summary>
            public void Start()
            {
                _ring.Kick(Ring.AlignDown(Math.Max(0, (_head?.Length ?? 0) - Ring.SlotBytes)));
                if (LengthKnown) QueueTail();
                Log.Info("audio", $"audio.open file={FileIdHex} fmt={Fmt} len={FileLength} known={(LengthKnown ? 1 : 0)} "
                                  + $"skip={Skip} durMs={DurationMs} bps={BytesPerSecond} head={HeadBytes} "
                                  + $"ring={_ring.Seconds}s slots={_ring.Slots} cache={(_disk is null ? 0 : 1)} ms={ElapsedMs()}");
            }

            /// <summary>Container [offset, offset + dst.Length) from the head, the ring or the disk. &gt;0 copied (short at a
            /// store's edge), 0 only at EOF, −1 superseded or starved. Blocks only in the ring's bounded wait.</summary>
            public int ReadAt(long offset, Span<byte> dst, uint epoch)
            {
                long length = Length;
                bool known = LengthKnown;
                if (offset < 0 || dst.Length == 0 || (known && offset >= length)) return 0;
                int cap = known ? (int)Math.Min(dst.Length, length - offset) : dst.Length;
                long fileOffset = Skip + offset;
                byte[]? head = Volatile.Read(ref _head);
                if (head is not null && fileOffset < head.Length && epoch == Epoch)
                {
                    int n = (int)Math.Min(cap, head.Length - fileOffset);
                    head.AsSpan((int)fileOffset, n).CopyTo(dst);
                    FirstServed("head", offset);
                    return n;
                }
                int got = _ring.ReadAt(fileOffset, dst[..cap], epoch);
                if (got > 0) FirstServed("ring", offset);
                return got;
            }

            /// <summary>Would a read at container <paramref name="offset"/> be served without a request — from the head,
            /// the ring, or the slot already queued / in flight?</summary>
            public bool IsResident(long offset)
            {
                long fileOffset = Skip + Math.Max(0, offset);
                if (Volatile.Read(ref _head) is { } head && fileOffset < head.Length) return true;
                return _ring.HoldsLocked(fileOffset, fileOffset + 1) || _ring.IsPending(fileOffset);
            }

            /// <summary>A seek, container coordinates. See <see cref="Ring.Retarget"/>.</summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch)
                => _ring.Retarget(Skip + Math.Max(0, probeOffset), probeBytes, epoch);

            /// <summary>The seek landed, container coordinates. See <see cref="Ring.ResumeFrom"/>.</summary>
            public void ResumeFrom(long offset) => _ring.ResumeFrom(Skip + Math.Max(0, offset));

            /// <summary>The splice proof: decrypted chunk-0 bytes against the clear head over their common prefix. A
            /// mismatch drops the head, supersedes the epoch (the decoder restarts at 0 on the ring's bytes) and answers
            /// false. A match answers true and drops the head once the proof covers all of it (a 64 KiB chunk from disk
            /// proves only part of an 80 KiB head, which then keeps serving its tail).</summary>
            public bool ProveHead(ReadOnlySpan<byte> chunk0Plain)
            {
                byte[]? head = Volatile.Read(ref _head);
                if (head is null) return SpliceProof != 2;
                int n = Math.Min(head.Length, chunk0Plain.Length);
                if (n <= 0) return true;
                bool same = chunk0Plain[..n].SequenceEqual(head.AsSpan(0, n));
                if (!same || n == head.Length) Volatile.Write(ref _head, null);
                Volatile.Write(ref _proof, same ? 1 : 2);
                Log.Info("audio", $"audio.splice file={FileIdHex} bytes={n} of={head.Length} proven={(same ? 1 : 0)} ms={ElapsedMs()}");
                if (!same) _ring.Supersede();
                return same;
            }

            /// <summary>Fetch [start, end) into <paramref name="dst"/>: mirrors in order, `Content-Range` adopted as the
            /// true length, the CIPHERTEXT written through to the disk cache, then decrypted IN PLACE at the true file
            /// offset. &gt;0 bytes, 0 when every mirror refused (the url set is dropped), −1 when cancelled.</summary>
            internal int FetchRange(long start, long end, Span<byte> dst, CancellationToken ct, out long headersAt)
            {
                headersAt = Stopwatch.GetTimestamp();
                int want = (int)Math.Min(dst.Length, end - start);
                if (want <= 0) return 0;
                for (int attempt = 0; attempt < _mirrors.Length; attempt++)
                {
                    if (ct.IsCancellationRequested) return -1;
                    int index = (_mirror + attempt) % _mirrors.Length;
                    IRangeReply? reply = _source.Open(_mirrors[index], start, start + want - 1, ct);
                    if (reply is null) continue;
                    headersAt = Stopwatch.GetTimestamp();
                    int total = 0;
                    try
                    {
                        AdoptLength(reply.TotalLength);
                        while (total < want)
                        {
                            int n = reply.Read(dst.Slice(total, want - total));
                            if (n <= 0) break;
                            total += n;
                        }
                    }
                    finally { reply.Dispose(); }
                    if (ct.IsCancellationRequested) return -1;
                    if (total <= 0) continue;

                    _mirror = index;
                    Span<byte> got = dst[..total];
                    WriteThrough(start, got);
                    if (_key is not null)
                    {
                        if (start == 0 && SpliceProof == 0)
                            Log.Info("audio", $"audio.key file={FileIdHex} validates={(Ctr.Validates(got, _key) ? 1 : 0)}");
                        Ctr.DecryptInPlace(got, _key, start);
                    }
                    Log.Info("audio", $"audio.range file={FileIdHex} at={start} len={total} src=cdn pingMs={_fetcher.PingMs} "
                                      + $"kbps={_fetcher.BytesPerSecond * 8 / 1000} ms={ElapsedMs()}");
                    return total;
                }
                if (ct.IsCancellationRequested) return -1;
                InvalidateMirrors(FileIdHex);
                Log.Warn("audio", $"audio.range file={FileIdHex} at={start} len=0 src=cdn mirrors={_mirrors.Length} refused");
                return 0;
            }

            /// <summary>Serve what the ring or the disk already holds of [start, end), shrinking the range from BOTH ends;
            /// the middle stays ONE request — the CDN's cost is per request, not per byte.</summary>
            internal void FillFromDisk(in RangeRequest req, ref long start, ref long end, Span<byte> chunkScratch)
            {
                if (req.Tail ? TailGranule >= 0 : _ring.HoldsLocked(start, end)) { start = end; return; }
                if (_disk is null || !LengthKnown) return;
                while (start < end && TryDiskChunk(start, chunkScratch, !req.Tail)) start = Ring.AlignUp(start + 1);
                while (end > start && TryDiskChunk(Ring.AlignDown(end - 1), chunkScratch, !req.Tail)) end = Ring.AlignDown(end - 1);
            }

            bool TryDiskChunk(long at, Span<byte> scratch, bool publish)
            {
                long chunk = Ring.AlignDown(at) / Ring.SlotBytes;
                if (_disk is null || !_disk.TryReadChunk(FileIdHex, (int)chunk, scratch, out int length) || length <= 0)
                    return false;
                Span<byte> plain = scratch[..length];
                if (_key is not null) Ctr.DecryptInPlace(plain, _key, chunk * Ring.SlotBytes);
                if (publish) _ring.Land(chunk * Ring.SlotBytes, plain);
                Observe(chunk * Ring.SlotBytes, plain);
                return true;
            }

            /// <summary>Landed plaintext from the wire: into the ring (unless it is the tail), then what it teaches.</summary>
            internal void Land(in RangeRequest req, long start, ReadOnlySpan<byte> plain)
            {
                if (!req.Tail) _ring.Land(start, plain);
                Observe(start, plain);
            }

            void Observe(long start, ReadOnlySpan<byte> plain)
            {
                if (start == 0 && Volatile.Read(ref _head) is not null) ProveHead(plain);
                long length = FileLength;
                if (!IsOgg(Fmt) || !LengthKnown || TailGranule >= 0 || start + plain.Length <= length - 2L * Ring.SlotBytes) return;
                // Bytes arrive in ascending order (a range, or the disk a chunk at a time), so a page found in the slot
                // before the last stays the answer when the last slot is only a page's body.
                long granule = LastOggGranule(plain);
                if (granule >= 0) _tailCandidate = granule;
                if (start + plain.Length < length || _tailCandidate < 0) return;
                Interlocked.Exchange(ref _tailGranule, _tailCandidate);
                Log.Info("audio", $"audio.tail file={FileIdHex} granule={_tailCandidate} ms={ElapsedMs()}");
            }

            /// <summary>A range finished, one way or another.</summary>
            internal void Settle(in RangeRequest req, Source source)
            {
                if (source == Source.Local)
                    Log.Info("audio", $"audio.range file={FileIdHex} at={req.Start} len={req.End - req.Start} src=local ms={ElapsedMs()}");
                if (req.Tail) return;
                _ring.Settled(req, source == Source.Refused);
                if (Volatile.Read(ref _ringLogged) == 0)
                {
                    long from = Ring.AlignDown(_ring.Cursor);
                    long to = Math.Min(FileLength, from + (long)(_ring.Slots - Ring.KeepBehindSlots) * Ring.SlotBytes);
                    if (_ring.HoldsLocked(from, to) && Interlocked.Exchange(ref _ringLogged, 1) == 0)
                        Log.Info("audio", $"audio.ring file={FileIdHex} filled={_ring.Seconds}s slots={_ring.Slots} "
                                          + $"waits={_ring.Waits} requests={_fetcher.Requests} ms={ElapsedMs()}");
                }
            }

            internal void Dropped(in RangeRequest req) { if (!req.Tail) _ring.Dropped(req); }

            /// <summary>The slot-aligned range covering the file's last 64 KiB (one or two slots): the last Ogg page and
            /// its granule are in there even when the final slot is a few bytes long. Queued once the length is real.</summary>
            void QueueTail()
            {
                if (!IsOgg(Fmt) || Interlocked.Exchange(ref _tailQueued, 1) != 0) return;
                long length = FileLength;
                long tail = Ring.AlignDown(Math.Max(0, length - Ring.SlotBytes));
                _fetcher.Enqueue(this, new RangeRequest(tail, length, Epoch, Probe: true, Tail: true));
            }

            void WriteThrough(long start, ReadOnlySpan<byte> cipher)
            {
                if (_disk is null || !LengthKnown) return;
                try
                {
                    if (Interlocked.Exchange(ref _sizeDeclared, 1) == 0) _disk.SetSize(FileIdHex, FileLength);
                    for (int offset = 0; offset < cipher.Length; offset += Ring.SlotBytes)
                    {
                        long chunk = (start + offset) / Ring.SlotBytes;
                        int expected = (int)Math.Min(Ring.SlotBytes, FileLength - chunk * Ring.SlotBytes);
                        if ((start + offset) % Ring.SlotBytes != 0 || cipher.Length - offset < expected) break;
                        _disk.WriteChunk(FileIdHex, (int)chunk, cipher.Slice(offset, expected));
                    }
                }
                catch (Exception ex) { Log.Warn("audio", "audio body cache write failed", ex); }
            }

            void AdoptLength(long total)
            {
                if (total <= Skip || LengthKnown) return;
                Interlocked.Exchange(ref _fileLength, total);
                Volatile.Write(ref _lengthKnown, 1);
                Log.Info("audio", $"audio.len file={FileIdHex} len={total} bps={BytesPerSecond} ms={ElapsedMs()}");
                QueueTail();
            }

            void FirstServed(string store, long offset)
            {
                if (Interlocked.Exchange(ref _firstServed, 1) != 0) return;
                Log.Info("audio", $"audio.first file={FileIdHex} from={store} at={offset} ms={ElapsedMs()}");
            }

            long ElapsedMs() => (long)Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _fetcher.Forget(this);
                Volatile.Write(ref _head, null);
                _ring.WakeWaiters();
            }
        }

        // ── 6. pure helpers, and the `Stream` face ───────────────────────────────────────────────────────────────────

        /// <summary>A rung's nominal byte rate, for sizing before the true length is known — 0.2.9's hints
        /// (`PrefetchingReadStream.cs:112-123`); librespot sizes read-ahead off the nominal rate too (`mod.rs:563-573`).</summary>
        public static int NominalBytesPerSecond(Format format) => format switch
        {
            Format.OggVorbis96 => 96 * 125,
            Format.OggVorbis160 => 160 * 125,
            Format.OggVorbis320 => 320 * 125,
            Format.Flac => 1_000 * 125,
            Format.Flac24 => 1_800 * 125,
            _ => 160 * 125,
        };

        /// <summary>The file's byte rate from its length and duration, else the rung's nominal one. PURE.</summary>
        public static int RateOf(long containerBytes, long durationMs, Format format)
            => containerBytes > 0 && durationMs > 0
                ? (int)Math.Clamp(containerBytes * 1000L / durationMs, 4_000, 4_000_000)
                : NominalBytesPerSecond(format);

        /// <summary>The granule of the LAST Ogg page in a buffer, or −1: `OggS`, version 0, granule at +6 (LE, signed;
        /// −1 = no packet ends here) per RFC 3533 §6. Only the first 14 header bytes need to be inside the buffer. A
        /// byte scan for one number, not the CORE Ogg reader.</summary>
        public static long LastOggGranule(ReadOnlySpan<byte> bytes)
        {
            for (int i = bytes.Length - 14; i >= 0; i--)
            {
                if (bytes[i] != (byte)'O' || bytes[i + 4] != 0 || !bytes.Slice(i, 4).SequenceEqual("OggS"u8)) continue;
                long granule = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(i + 6, 8));
                if (granule >= 0) return granule;
            }
            return -1;
        }

        /// <summary>A `Stream` over a <see cref="Body"/>, container coordinates, for everything that still speaks
        /// `Stream` (Wave 3's `Prefetching` until `RingSource` lands). Every byte comes out of <see cref="Body.ReadAt"/>,
        /// so the head, the ring and the cache all apply. Disposing it disposes the body. What replaced `CtrStream`.</summary>
        public sealed class BodyStream(Body body) : Stream
        {
            long _position;

            public Body Body => body;
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => body.Length;

            public override long Position
            {
                get => _position;
                set => _position = Math.Clamp(value, 0, Length);
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int n = body.ReadAt(_position, buffer, body.Epoch);
                if (n <= 0) return 0;                           // −1 (starved) reads as end-of-stream to a Stream caller
                _position += n;
                return n;
            }

            /// <summary>A position write only. It deliberately does NOT retarget: a `Stream` decoder (NVorbis until Wave 6)
            /// seeks to probe pages — at open, near the end — and a cancel there would kill the cold-start range. The miss
            /// on the next read asks for the bytes instead; the cancelling seek is <see cref="Body.Retarget"/>'s, for
            /// Wave 3's random-access adapter.</summary>
            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => Length + offset,
                    _ => _position,
                };
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) body.Dispose();
                base.Dispose(disposing);
            }
        }

        // ── 7. opening, in parallel ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>§5.2's open: the clear head, storage-resolve and the audio key AT THE SAME TIME (head + resolve on api
        /// threads, the key here), then the first body range and — once `Content-Range` has named the length — the tail.
        /// The first decodable byte needs neither the key nor the CDN. A file whose chunk 0 is in the disk cache skips
        /// the head request (the cache is the head). `GainDb` = the catalogue's when non-zero, else the header's byte
        /// 144 (plan §6.3).</summary>
        internal static Opened OpenBody(in FileChoice choice, CancellationToken ct)
        {
            string fileIdHex = choice.FileIdHex;
            Log.Info("audio", $"audio.open.begin file={fileIdHex} fmt={choice.Fmt}");
            long t0 = Stopwatch.GetTimestamp();

            if (choice.ExternalUrl is { Length: > 0 } external)
            {
                long externalLength = HeadLength(external, ct);
                if (externalLength <= 0) return Opened.Failed(Fault.Network);
                var plain = new Body(HttpRangeSource.Shared, [external], null, 0, externalLength, lengthKnown: true,
                    choice.DurationMs, choice.Fmt, choice.GainDb, fileIdHex, null, null);
                plain.Start();
                return new Opened(new BodyStream(plain), choice.Fmt, plain.Length, choice.DurationMs, choice.GainDb,
                    fileIdHex, Fault.None, plain);
            }

            ChunkDiskCache? disk = DiskCache.Shared;
            long cachedLength = disk?.KnownSize(fileIdHex) ?? 0;
            byte[]? cachedChunk0 = null;
            if (cachedLength > 0)
            {
                var chunk = new byte[DiskCache.ChunkBytes];
                if (disk!.TryReadChunk(fileIdHex, 0, chunk, out int n) && n > Ctr.HeaderBytes) cachedChunk0 = chunk;
            }

            // THE PARALLEL TRIO. `Api.Run` is a named pool (P10); when it refuses, the work runs inline — a slower open,
            // never a failed one. The countdown is never disposed: a cancelled open must not strand a worker's Signal.
            byte[] head = [];
            Mirrors mirrors = new([], 0, Fault.Network);                   // never `default`: its Urls would be null
            var pending = new CountdownEvent(2);
            bool headInline = cachedChunk0 is not null || !Api.Run(() =>
            {
                try { head = HeadCache.Fetch(fileIdHex, ct); } finally { pending.Signal(); }
            });
            if (headInline) pending.Signal();
            bool resolveInline = !Api.Run(() =>
            {
                try { mirrors = Resolve(fileIdHex, ct); } finally { pending.Signal(); }
            });
            if (resolveInline) pending.Signal();

            Span<byte> key = stackalloc byte[AudioKey.KeyLength];
            Fault fault = Key(fileIdHex, choice.FileId, choice.TrackGid, key, ApEligible(choice.Fmt), ct);
            if (headInline && cachedChunk0 is null) head = HeadCache.Fetch(fileIdHex, ct);
            if (resolveInline) mirrors = Resolve(fileIdHex, ct);
            pending.Wait(ct);

            Log.Info("audio", $"audio.head file={fileIdHex} bytes={head.Length} cached={(cachedChunk0 is null ? 0 : 1)} "
                              + $"mirrors={(mirrors.Ok ? mirrors.Urls.Length : 0)} key={fault} "
                              + $"ms={(long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds}");
            if (fault != Fault.None)
            {
                Log.Warn("spotify", "no audio key for " + fileIdHex + " (" + fault + ")");
                return Opened.Failed(fault);
            }
            if (!mirrors.Ok) return Opened.Failed(mirrors.Fault == Fault.None ? Fault.Network : mirrors.Fault);

            float gain = choice.GainDb;
            if (gain == 0f && head.Length > 0) gain = HeadGainDb(head);
            else if (gain == 0f && cachedChunk0 is not null) gain = HeadGainDb(Ctr.Decrypt(cachedChunk0.AsSpan(0, 160), key, 0));

            int skip = HeaderBytesFor(choice.Fmt);
            long length = cachedLength > 0
                ? cachedLength
                : skip + Math.Max(Ring.SlotBytes, choice.DurationMs * NominalBytesPerSecond(choice.Fmt) / 1000);
            var body = new Body(HttpRangeSource.Shared, mirrors.Urls, key.ToArray(), skip, length, cachedLength > 0,
                choice.DurationMs, choice.Fmt, gain, fileIdHex, head, disk);
            body.Start();
            return new Opened(new BodyStream(body), choice.Fmt, body.Length, choice.DurationMs, gain, fileIdHex,
                Fault.None, body);
        }

        // ── 8. the next track ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Warm what the NEXT track's open needs — the clear head, the mirror list, the audio key — without
        /// opening it. The pump calls it inside `EndingSoonMs` (fade + 8 s); the boundary's `Open` then finds all three
        /// in this file's bounded caches and costs the first range + the tail. Non-blocking (api threads); a prefetch
        /// that fails costs a slower `Open`, never a fault.</summary>
        public static void Prefetch(in FileChoice choice)
        {
            if (!choice.Ok || choice.ExternalUrl is { Length: > 0 } || choice.FileIdHex.Length == 0) return;
            string fileIdHex = choice.FileIdHex;
            byte[] fileId = choice.FileId, gid = choice.TrackGid;
            bool apEligible = ApEligible(choice.Fmt);
            Api.Run(() => HeadCache.Fetch(fileIdHex, CancellationToken.None));
            Api.Run(() =>
            {
                Mirrors warm = Resolve(fileIdHex, CancellationToken.None);
                Span<byte> key = stackalloc byte[AudioKey.KeyLength];
                Fault fault = Key(fileIdHex, fileId, gid, key, apEligible, CancellationToken.None);
                Log.Info("audio", $"audio.prefetch file={fileIdHex} mirrors={(warm.Ok ? warm.Urls.Length : 0)} key={fault}");
            });
        }

        /// <summary>The two most recent heads, so a prefetch's head and the boundary `Open`'s head are ONE request. Head
        /// files are immutable (0.2.9's `HeadFileClient` held 32 behind `FreshnessPolicy.Immutable`); a hand-off needs
        /// two, and it is bounded (C8).</summary>
        static class HeadCache
        {
            static readonly Lock Gate = new();
            static string s_key0 = "", s_key1 = "";
            static byte[] s_val0 = [], s_val1 = [];

            public static byte[] Fetch(string fileIdHex, CancellationToken ct)
            {
                lock (Gate)
                {
                    if (string.Equals(s_key0, fileIdHex, StringComparison.OrdinalIgnoreCase)) return s_val0;
                    if (string.Equals(s_key1, fileIdHex, StringComparison.OrdinalIgnoreCase)) return s_val1;
                }
                byte[] bytes = Head(fileIdHex, ct);
                if (bytes.Length == 0) return [];
                lock (Gate) { s_key1 = s_key0; s_val1 = s_val0; s_key0 = fileIdHex; s_val0 = bytes; }
                return bytes;
            }
        }
    }
}
