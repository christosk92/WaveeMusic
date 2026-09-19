// ── Spotify/Spotify.Audio.Stream.cs ────────────────────────────────────────────────────────────────────────────────
// the CDN stream layer: the clear head, the read-ahead ring, the range fetcher, the disk cache
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 1,950 lines (plan §8.1 said 700; the ring had to live here for `Body.ReadAt` to compile and be tested, and
//         the headless pass added the `Stream.Stats` door, the shared read-ahead budget and the seek interrupt —
//         headless plan §3.4, Vorbis plan §5.3; gap batch B4 added the starve rule, slot-by-slot landing, the slot pool
//         and ring growth, re-resolve, the range-reply rule, the late gain and the `OpenSeams` door — about 420)
// Spec: docs/plans/wavee/wavee-0.3-vorbis-implementation.md §5 · wavee-0.3-headless-implementation.md §3.4
//
// WHAT THIS FILE DELIVERS. `Spotify.Audio.Open` used to hand Wave 3 a `CtrStream`: ONE 128 KiB chunk, one HTTP range
// request per refill issued SYNCHRONOUSLY on the decode thread, no cache, no read-ahead, no cancel. Instant start, a
// cheap scrub and a warm second listen were all impossible in that shape, so the shape is replaced (no legacy paths).
//
// THE MOVING PARTS:
//   · `Body`    — the stores for ONE file, behind one `ReadAt`: the clear head file (≤ 80 KiB, no auth, no key), the
//                 ring, and `ChunkDiskCache`. It owns `FetchRangeAsync` (mirrors, failover, decrypt at the TRUE file
//                 offset) and `ProveHead` (the byte-exact splice proof against decrypted chunk 0).
//   · `Ring`    — slots of 64 KiB (the cache's granularity: a completed slot IS a cache chunk), direct-mapped by chunk
//                 index, sized in SECONDS off the file's byte rate — 30 s / 10 s metered / 600 s once throughput is ≥ 3×
//                 the byte rate (0.2.9's `ReadAheadPolicy`, `Wavee.Sdk/Streams/RangedHttpSource.cs:43-61`), capped at
//                 16 MiB. `ReadAt` copies or waits, bounded (8 s), and returns 0 only at a true EOF. A wait that runs
//                 out is `Starved` — NEVER the end (D5): the readers wait again, the pump reports "Reconnecting" after
//                 1.5 s and fails the load as `Fault.Network` after 90 s (`Playback.Audio.StarvePolicy`) — or after 6 s
//                 when the mirrors are REFUSING rather than slow, because an answer does not change by being waited on.
//   · `Fetcher` — ONE consumer task on the pool (P10: one consumer, strict sequence — no dedicated OS thread), a
//                 BOUNDED queue (C8: depth 8, DropOldest) and an in-flight `CancellationTokenSource` a seek cancels
//                 (C4). librespot and 0.2.9 never cancel, so a scrub of ten seeks queues ten fetches; here the tenth
//                 probe is the only request on the wire. Ping and throughput as librespot measures them
//                 (`audio/src/fetch/receive.rs:279-339`).
//   · `IRangeSource` — the wire as an interface; `Wavee.Tests/AudioStreamTests.cs` replaces it with a counting fake.
//
// THE TIMELINE THE LOG PRINTS (always on, category "audio"): audio.open.begin → audio.head → audio.open → audio.first
// (the first byte the decoder got, and from WHICH store) → audio.len → audio.splice → audio.range (per landed range,
// src=cdn|local) → audio.tail → audio.ring (the window filled). A seek prints audio.retarget; a stall audio.underrun.
// audio.head carries storage-resolve's own status and verdict AND the host it answered with, so an open that never
// re-resolves still says where its mirror set came from and which edge it names. A REFUSAL prints audio.mirror ONCE PER MIRROR of the refused range (the host and the HTTP status
// — the one fact `IRangeSource` used to drop, and then, gated per process, all but the first mirror of), then
// audio.range …refused n=, then audio.resolve with the service's status and verdict; a lossless open that never got a
// body byte prints audio.nobody and `Spotify.Audio.Open` answers it with audio.demote.
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
// decode-ahead thread and nowhere else. The fetcher owns ONE pooled consumer task, not a thread — every await on its
// path is `ConfigureAwait(false)`, so which pool thread runs a given continuation is never a decision anything makes.
// No table, no signal (C1).
//
// THE COUNTERS (headless plan §3.4). Everything a smoke script asserts about the wire — ranges, probes, heads,
// storage-resolves, disk-cache chunks, CDN bytes, ring waits and starves — is an `Interlocked` long in this class, read as
// ONE value through `Stream.Stats.Read()` from any thread. A torn pair is a diagnostic, never a decision.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Wavee.Sdk.Streams;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        // ── 0. the counters, the Stats door, the shared read-ahead budget ────────────────────────────────────────────

        // Process-wide, monotonic, Interlocked. Written where the work happens (the fetch task, the decode-ahead
        // thread, an api thread), read only through `Stream.Stats`.
        static long s_statHeads, s_statResolves, s_statProbes, s_statCacheHits, s_statCdnBytes, s_statRingWaits, s_statRingStarves;

        /// <summary>The body the pump is PLAYING (the last non-prepared open) — what `Stats.HeadBytes` / `SpliceProof`
        /// describe. Cleared by that body's own `Dispose`, so a finished track's ring is never pinned by a diagnostic.</summary>
        static Body? s_liveBody;

        /// <summary>The stream layer as one door (headless plan §3.4). A NAME, not a store: the counters live in
        /// <see cref="Audio"/> and on the <see cref="Fetcher"/>.</summary>
        public static class Stream
        {
            /// <summary>Everything the headless `stats` line and a smoke script's `cdn.*` conditions read, as ONE value from
            /// any thread. Volatile reads, no lock: a torn pair is a diagnostic, never a decision.</summary>
            /// <param name="Requests">Ranges the fetcher put on the wire (disk- and ring-answered ranges are not requests).</param>
            /// <param name="Cancelled">Ranges a seek or a dispose cancelled mid-flight.</param>
            /// <param name="InFlight">Ranges on the wire right now (the fetcher's promise is ≤ 1).</param>
            /// <param name="Outstanding">Queued + in-flight ranges; 0 = the fetcher is idle.</param>
            /// <param name="PeakInFlight">The most ranges ever in flight at once.</param>
            /// <param name="PingMs">librespot's median-of-three time-to-headers.</param>
            /// <param name="BytesPerSecond">The measured CDN throughput.</param>
            /// <param name="Heads">Clear-head GETs that reached the network (a head-cache hit is not one).</param>
            /// <param name="Resolves">Storage-resolve calls that reached the network (a mirror-cache hit is not one).</param>
            /// <param name="Probes">Seek probe ranges put on the wire — the far-seek counter.</param>
            /// <param name="CacheHits">64 KiB chunks the disk cache answered (chunk 0 at open, a probe, a fill).</param>
            /// <param name="CdnBytes">Body bytes that came off the CDN.</param>
            /// <param name="RingWaits">Reads that had to wait for bytes (every ring, this process).</param>
            /// <param name="RingStarves">Reads that gave up after the bounded wait (every ring, this process).</param>
            /// <param name="HeadBytes">The playing body's clear head still held (0 once proven).</param>
            /// <param name="SpliceProof">The playing body's splice proof: 0 unchecked · 1 byte-exact · 2 refused.</param>
            /// <param name="ReadAheadBytes">Ring memory granted out of the shared budget right now.</param>
            public readonly record struct Stats(
                long Requests, long Cancelled, int InFlight, int Outstanding, int PeakInFlight, int PingMs, long BytesPerSecond,
                long Heads, long Resolves, long Probes, long CacheHits, long CdnBytes,
                long RingWaits, long RingStarves, int HeadBytes, int SpliceProof, long ReadAheadBytes)
            {
                /// <summary>Every HTTP request the audio path made: ranges + heads + storage-resolves (the key is an AP
                /// round trip, not HTTP). Vorbis plan §5.4's cold start is 4 of these.</summary>
                public long HttpRequests => Requests + Heads + Resolves;

                /// <summary>The live counters over the process's shared fetcher.</summary>
                public static Stats Read() => Read(Fetcher.Shared);

                /// <summary>The live counters over <paramref name="fetcher"/> (a test's own; production has one).</summary>
                public static Stats Read(Fetcher fetcher)
                {
                    Body? live = Volatile.Read(ref s_liveBody);
                    return new Stats(fetcher.Requests, fetcher.Cancelled, fetcher.InFlight, fetcher.Outstanding,
                        fetcher.PeakInFlight, fetcher.PingMs, fetcher.BytesPerSecond,
                        Interlocked.Read(ref s_statHeads), Interlocked.Read(ref s_statResolves),
                        Interlocked.Read(ref s_statProbes), Interlocked.Read(ref s_statCacheHits),
                        Interlocked.Read(ref s_statCdnBytes), Interlocked.Read(ref s_statRingWaits),
                        Interlocked.Read(ref s_statRingStarves), live?.HeadBytes ?? 0, live?.SpliceProof ?? 0,
                        ReadAheadBudget.InUseBytes);
                }

                /// <summary>The monotonic counters as a delta against <paramref name="mark"/> (a script's `stats mark`); the
                /// gauges — in flight, outstanding, peak, ping, throughput, head, proof, budget — stay this value's. PURE.</summary>
                public Stats Since(in Stats mark) => this with
                {
                    Requests = Requests - mark.Requests,
                    Cancelled = Cancelled - mark.Cancelled,
                    Heads = Heads - mark.Heads,
                    Resolves = Resolves - mark.Resolves,
                    Probes = Probes - mark.Probes,
                    CacheHits = CacheHits - mark.CacheHits,
                    CdnBytes = CdnBytes - mark.CdnBytes,
                    RingWaits = RingWaits - mark.RingWaits,
                    RingStarves = RingStarves - mark.RingStarves,
                };
            }
        }

        /// <summary>The read-ahead memory every live ring shares — 0.2.9's <c>TotalReadAheadMemoryCapBytes</c> (Vorbis plan
        /// §5.3): the playing track and the prepared next one (and, for a crossfade's length, the retiring one) together
        /// never pin more than <see cref="TotalBytes"/>. A ring's slots are granted at construction and handed back by its
        /// body's <c>Dispose</c>; a body that finds the budget spent still gets <see cref="MinSlots"/>, because a track that
        /// cannot buffer at all is worse than a budget exceeded by 768 KiB.</summary>
        public static class ReadAheadBudget
        {
            public const long TotalBytes = 24L << 20;

            /// <summary>A prepared (next) track's ring is sized at no more than this, whatever the link measured: it only
            /// has to carry the first seconds past the hand-off until it becomes the playing track.</summary>
            public const int PreparedSeconds = 30;

            /// <summary>The floor: eight slots ahead plus the four kept behind (<see cref="Ring.SlotCount"/>'s own floor).</summary>
            public const int MinSlots = 8 + Ring.KeepBehindSlots;

            /// <summary>How many freed 64 KiB slot arrays are kept for the next ring (4 MiB): a track change reuses pinned
            /// slots instead of pinning new ones, and an idle app holds no more than this (the memory floor).</summary>
            public const int PoolMaxSlots = 64;

            static long s_slots;
            static readonly Lock Gate = new();
            static readonly Stack<byte[]> s_pool = new(PoolMaxSlots);

            /// <summary>Ring bytes granted and not yet handed back.</summary>
            public static long InUseBytes => Interlocked.Read(ref s_slots) * Ring.SlotBytes;

            /// <summary>Freed slot arrays waiting for the next ring.</summary>
            public static int PooledSlots { get { lock (Gate) return s_pool.Count; } }

            /// <summary>How many slots a ring that wants <paramref name="wantedSlots"/> gets while <paramref name="inUseBytes"/>
            /// are already granted: what is left, never more than it asked for, never fewer than <see cref="MinSlots"/>. PURE.</summary>
            public static int Grant(int wantedSlots, long inUseBytes, long totalBytes = TotalBytes)
            {
                long free = Math.Max(0, totalBytes - Math.Max(0, inUseBytes)) / Ring.SlotBytes;
                long grant = Math.Min(Math.Max(MinSlots, wantedSlots), free);
                return (int)Math.Max(MinSlots, grant);
            }

            /// <summary>How many MORE slots a growing ring gets: what is left, never more than it asked for, and — unlike a
            /// new ring — possibly none. PURE.</summary>
            public static int GrantExtra(int extraSlots, long inUseBytes, long totalBytes = TotalBytes)
            {
                long free = Math.Max(0, totalBytes - Math.Max(0, inUseBytes)) / Ring.SlotBytes;
                return (int)Math.Clamp(Math.Min(extraSlots, free), 0, int.MaxValue);
            }

            internal static int Take(int wantedSlots)
            {
                lock (Gate)
                {
                    int grant = Grant(wantedSlots, InUseBytes);
                    Interlocked.Add(ref s_slots, grant);
                    return grant;
                }
            }

            internal static int TakeExtra(int extraSlots)
            {
                lock (Gate)
                {
                    int grant = GrantExtra(extraSlots, InUseBytes);
                    Interlocked.Add(ref s_slots, grant);
                    return grant;
                }
            }

            internal static void Return(int slots)
            {
                lock (Gate) Interlocked.Add(ref s_slots, -Math.Max(0, slots));
            }

            /// <summary>One pinned 64 KiB slot: a pooled one when there is one.</summary>
            internal static byte[] RentSlot()
            {
                lock (Gate)
                {
                    if (s_pool.TryPop(out byte[]? pooled)) return pooled;
                }
                return GC.AllocateUninitializedArray<byte>(Ring.SlotBytes, pinned: true);
            }

            /// <summary>Hand a slot array back. Only a ring that has stopped every reader and writer of it calls this.</summary>
            internal static void ReturnSlot(byte[] slot)
            {
                if (slot.Length != Ring.SlotBytes) return;
                lock (Gate)
                {
                    if (s_pool.Count < PoolMaxSlots) s_pool.Push(slot);
                }
            }
        }

        // ── 1. the wire, as a seam ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>One ranged GET, opened at HEADERS: returned when the response headers arrived, not the body — the
        /// file's total length is on `Content-Range` and the caller wants it a whole body earlier. A torn body or a
        /// cancelled token THROWS (`IOException` / `HttpRequestException` / `OperationCanceledException`) — 0 is only
        /// the end of THIS range, never a fault.</summary>
        public interface IRangeReply : IDisposable
        {
            /// <summary>The WHOLE file's length from `Content-Range: bytes a-b/total`, or −1 when it was absent.</summary>
            long TotalLength { get; }
            /// <summary>The file offset of the body's first byte: the `Content-Range` start of a 206, 0 for a 200 (a host
            /// that ignored the Range header sends the whole file from byte 0).</summary>
            long Start { get; }
            /// <summary>Body bytes: &gt;0, or 0 at the true end of this range. A wire fault or a cancelled
            /// <paramref name="ct"/> THROWS instead of returning 0.</summary>
            ValueTask<int> ReadAsync(Memory<byte> dst, CancellationToken ct);
        }

        /// <summary>Where audio bytes come from. The ONE seam the tests replace, and the ONE caller of it is the fetcher.
        /// Null = this url REFUSED (a non-2xx status) — the next mirror is tried. A wire fault THROWS instead of
        /// returning null, so <see cref="Body.FetchRangeAsync"/> can tell "this mirror is dead, try the next" apart from
        /// "every mirror answered but none has the file".</summary>
        public interface IRangeSource
        {
            /// <summary>Open <paramref name="url"/> for the INCLUSIVE byte range [start, end]. Null on a refusal; a wire
            /// fault throws.</summary>
            ValueTask<IRangeReply?> OpenAsync(string url, long start, long end, CancellationToken ct);
        }

        /// <summary>What one <see cref="Body.FetchRangeAsync"/> call settled on. <see cref="Bytes"/>: &gt;0 landed, 0
        /// every mirror refused, −1 cancelled before a slot landed. <see cref="HeadersAt"/>: the timestamp the first
        /// successful reply's headers arrived (the fetcher's ping sample).</summary>
        internal readonly record struct FetchResult(int Bytes, long HeadersAt);

        /// <summary>What a reply that does not start where it was asked to is worth (G-118). A 200 to a non-zero Range
        /// used to be taken AS that range — an external podcast host that ignores Range then corrupted every range after the
        /// first. A reply from byte 0 is still the file: its first <c>start</c> bytes are read past (bounded), and anything
        /// else is refused like a dead mirror.</summary>
        public static class RangeReply
        {
            /// <summary>The most a reply from byte 0 is read past to reach the asked start (4 MiB ≈ 3.5 min of 160 kbit/s).</summary>
            public const long MaxSkipBytes = 4L << 20;

            /// <summary>Bytes to discard before the asked <paramref name="requestedStart"/> is at hand, or −1 to refuse. PURE.</summary>
            public static long SkipFor(long requestedStart, long replyStart)
                => replyStart == requestedStart ? 0
                 : replyStart == 0 && requestedStart > 0 && requestedStart <= MaxSkipBytes ? requestedStart
                 : -1;
        }

        /// <summary>The real wire: the pooled CDN client, HTTP/2 preferred per request with a downgrade allowed, one
        /// connection multiplexing a file's ranges (`EnableMultipleHttp2Connections = false` on <see cref="Cdn"/>), so a
        /// cancelled range is an `RST_STREAM`, not a dropped connection. Async end to end: `SocketsHttpHandler` refuses a
        /// SYNCHRONOUS send once `Version >= 2` before it ever consults the version policy, so HTTP/2 and a blocking
        /// `Send` cannot coexist here — this seam awaits instead.</summary>
        public sealed class HttpRangeSource : IRangeSource
        {
            /// <summary>"Configuring TLS is expensive and should be done once per process" (librespot
            /// `core/src/http_client.rs:148`).</summary>
            public static readonly HttpRangeSource Shared = new();

            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
            public async ValueTask<IRangeReply?> OpenAsync(string url, long start, long end, CancellationToken ct)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = System.Net.HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                message.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                HttpResponseMessage response =
                    await Cdn.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                try
                {
                    int status = (int)response.StatusCode;
                    // 206 is the only answer a Spotify CDN gives a Range, and it is the only one librespot,
                    // go-librespot and the desktop client accept — but this seam also carries PODCAST ENCLOSURES on
                    // hosts that ignore Range and 200 the whole file from byte 0, which G-118's read-past
                    // (`RangeReply.SkipFor`) exists to make usable. So 200 stays accepted and is bounded there; it is
                    // the STATUS of a refusal, never a 200, that this file used to drop.
                    if (status is not (200 or 206))                                             // a REFUSAL: the next mirror
                    {
                        // A 416 answers `Content-Range: bytes * /N` — the file's TRUE length, on the one reply that
                        // carries no body to learn it from. `OpenAsync` answers null, so nothing downstream will ever
                        // see it: it goes on the line rather than on the floor.
                        long named = response.Content.Headers.ContentRange is { HasLength: true, Length: { } whole }
                            ? whole : -1;
                        LogRefusal(url, start, status, response.ReasonPhrase, named);
                        response.Dispose();
                        return null;
                    }
                    long total = response.Content.Headers.ContentRange is { HasLength: true, Length: { } n } ? n
                        : status == 200 ? response.Content.Headers.ContentLength ?? -1 : -1;
                    long from = status == 200 ? 0 : response.Content.Headers.ContentRange?.From ?? start;
                    System.IO.Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    return new HttpReply(response, body, total, from);
                }
                catch { response.Dispose(); throw; }                        // a FAULT: FetchRangeAsync maps it (next mirror)
            }

            /// <summary>How long one refused RANGE's reporting window stays open. It is not a mute: a range that is
            /// still being refused this much later opens a fresh window, because a refusal that outlives its evidence
            /// is worth saying again.</summary>
            const int RefusalLogEveryMs = 5_000;

            /// <summary>How many mirrors one refused range may name. Three is the usual mirror set and the live and
            /// prepared bodies can be refusing the same offset at once, so eight covers both with room to spare while
            /// still being a bound (C8).</summary>
            const int RefusalLinesPerRange = 8;

            static readonly Lock RefusalGate = new();
            static long s_refusalRange = -1;
            static long s_refusalOpenedAt;
            static int s_refusalLines;

            /// <summary>WHY a mirror refused — the one fact this seam used to drop on the floor. The status exists for
            /// exactly the three lines above it: `OpenAsync` answers null and everything downstream can only ever learn
            /// "not this url", so a 403 (the token expired), a 404 (the file is not on this mirror) and a 410 (it has
            /// been withdrawn) all reached the log as the same silence. Host only, never the url: a CDN url carries a
            /// signed token.
            ///
            /// <para>GATED PER REFUSED RANGE, and that is the half that mattered. The gate used to be one line per
            /// PROCESS per 5 s, so a body whose three mirrors all refused printed mirror 0 and swallowed the other two
            /// — which is the exact shape that hid the lossless 404 for a release: "one mirror said no" and "every url
            /// we were given is wrong" read identically. A range's mirrors are the evidence, so they are reported
            /// together or not at all.</para></summary>
            static void LogRefusal(string url, long start, int status, string? reason, long namedLength)
            {
                if (!OpenRefusalWindow(start)) return;
                Log.Warn("audio", $"audio.mirror host={HostOf(url)} at={start} status={status} "
                                  + $"reason={(string.IsNullOrEmpty(reason) ? "-" : reason)}"
                                  + (namedLength >= 0 ? $" len={namedLength}" : ""));
            }

            /// <summary>May this refusal of the range at <paramref name="start"/> be reported? A new offset opens a new
            /// window at once (the fetcher has ONE range in flight per body, so a new offset means the last one is
            /// finished with); inside a window the budget is <see cref="RefusalLinesPerRange"/> mirrors.</summary>
            static bool OpenRefusalWindow(long start)
            {
                long now = Environment.TickCount64;
                lock (RefusalGate)
                {
                    if (start != s_refusalRange || now - s_refusalOpenedAt >= RefusalLogEveryMs)
                    {
                        s_refusalRange = start;
                        s_refusalOpenedAt = now;
                        s_refusalLines = 0;
                    }
                    if (s_refusalLines >= RefusalLinesPerRange) return false;
                    s_refusalLines++;
                    return true;
                }
            }

            /// <summary>`https://host/path?token` → `host`. An index walk rather than <c>Uri</c>: this runs on a
            /// refusal, and a url that does not parse must still produce a line rather than a second failure. The HOST
            /// is the most of a CDN url that may ever reach a log — the rest of it is a signed token.</summary>
            internal static string HostOf(string url)
            {
                int scheme = url.IndexOf("//", StringComparison.Ordinal);
                int from = scheme < 0 ? 0 : scheme + 2;
                int slash = url.IndexOf('/', from);
                return slash < 0 ? url[from..] : url[from..slash];
            }

            sealed class HttpReply(HttpResponseMessage response, System.IO.Stream body, long total, long from) : IRangeReply
            {
                public long TotalLength => total;

                public long Start => from;

                /// <summary>The runtime's `ValueTask&lt;int&gt;` handed straight through: no wrapper state machine, no
                /// per-read allocation. A torn body or a cancelled <paramref name="ct"/> throws, exactly as the interface
                /// promises — the old shape's "catch everything, return 0" is gone (it is what hid the sync-over-async
                /// fault from ever reaching a settle).</summary>
                public ValueTask<int> ReadAsync(Memory<byte> dst, CancellationToken ct) => body.ReadAsync(dst, ct);

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

            /// <summary>0.2.9's root, kept (D8, G-122): `%LOCALAPPDATA%\Wavee\Wavee\Cache\audio` — 0.2.9's
            /// <c>AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder</c> + "audio", and the package's LocalCache on a
            /// packaged run by redirection. The chunk keys are the same file ids, so an upgraded install replays its 0.2.9
            /// cache with no migration and no orphaned root. Under a headless <c>--profile</c> it follows the profile.</summary>
            public static string DefaultDirectory() => DirectoryUnder(Platform.LocalFolder);

            /// <summary>The cache root under <paramref name="localFolder"/> (<c>Platform.LocalFolder</c>, which is
            /// `%LOCALAPPDATA%\Wavee`): the publisher folder holds the product folder, which holds `Cache\audio`. PURE.</summary>
            public static string DirectoryUnder(string localFolder) => Path.Combine(localFolder, "Wavee", "Cache", "audio");

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
            /// <summary>Every mirror refused; the url set was dropped. This is what <see cref="Body.Refusing"/> reports
            /// and, when it happens before a lossless open has landed its first body byte, what becomes
            /// <see cref="Fault.Refused"/> and the Ogg 320 demotion.</summary>
            Refused,
            /// <summary>Superseded by a seek (dropped from the queue, or cancelled in flight).</summary>
            Stale,
        }

        /// <summary>The one consumer for audio bytes: a bounded queue (C8), one range in flight at a time, an in-flight
        /// cancel a seek reaches (C4). Shared by the playing and the prepared next track. ONE consumer task, not a
        /// dedicated thread — every await on its path is `ConfigureAwait(false)`, and ranges are still served strictly
        /// one after another.</summary>
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

            /// <summary>A fault is logged at most this often — the range still settles (Refused) and the ring retries on
            /// every one of them, so the log line is a diagnostic, not the recovery path.</summary>
            const int FaultLogEveryMs = 5_000;

            readonly Channel<(Body Body, RangeRequest Req)> _queue;
            readonly Lock _gate = new();
            readonly (Body Body, RangeRequest Req)[] _drain = new (Body, RangeRequest)[QueueDepth];
            Task? _loop;
            CancellationTokenSource? _inFlight;
            Body? _inFlightBody;
            bool _inFlightTail;
            byte[]? _scratch, _chunkScratch;
            int _ping0 = 500, _ping1 = 500;
            int _requests, _cancelled, _peak, _live, _outstanding, _faults;
            long _faultLoggedAt;

            public Fetcher()
            {
                // NOT SingleReader: a seek drains the queue from the caller's thread while the consumer task awaits it.
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
            /// <summary>The most ranges ever in flight at once. This fetcher's promise is 1.</summary>
            public int PeakInFlight => Volatile.Read(ref _peak);
            /// <summary>Ranges on the wire right now (not the queued ones).</summary>
            public int InFlight => Volatile.Read(ref _live);
            /// <summary>Queued + in-flight work. Zero means the fetcher is idle.</summary>
            public int Outstanding => Volatile.Read(ref _outstanding);

            /// <summary>Ranges that ended in an exception outside the wire (settled as <see cref="Source.Refused"/>,
            /// never left pending): a bug in the disk cache, a decrypt, or anything else `ServeAsync`'s last-resort catch
            /// caught. A wire fault a mirror can be retried for is NOT counted here — see the `audio.range ... mirror=
            /// ... faulted` line instead.</summary>
            public int Faults => Volatile.Read(ref _faults);

            /// <summary>Throttle for a fault log line: true at most once per <see cref="FaultLogEveryMs"/>.</summary>
            internal bool ShouldLogFault()
            {
                long now = Environment.TickCount64;
                if (now - Volatile.Read(ref _faultLoggedAt) < FaultLogEveryMs) return false;
                Volatile.Write(ref _faultLoggedAt, now);
                return true;
            }

            /// <summary>Queue one range. Never blocks; a full queue drops its OLDEST entry (C8).</summary>
            public void Enqueue(Body body, in RangeRequest req)
            {
                EnsureStarted();
                Interlocked.Increment(ref _outstanding);
                if (!_queue.Writer.TryWrite((body, req))) Interlocked.Decrement(ref _outstanding);
            }

            /// <summary>A seek on <paramref name="body"/>: cancel its in-flight range (never the tail), drop its queued
            /// ranges (keeping a pending tail), and put <paramref name="probe"/> at the FRONT of the queue — so the probe
            /// is the only request in flight when it is issued and nothing is served before it. A default probe only
            /// cancels and drops.</summary>
            public void Retarget(Body body, in RangeRequest probe)
            {
                EnsureStarted();
                CancelAndDrain(body, keepTail: true, probe.End > probe.Start ? probe : null);
            }

            /// <summary>The body is gone: cancel and drop everything of it, the tail included.</summary>
            public void Forget(Body body) => CancelAndDrain(body, keepTail: false, null);

            /// <summary>HAZARD: cancelling a `CancellationTokenSource` runs every registered continuation
            /// SYNCHRONOUSLY on the cancelling thread — with the async consumer, that continuation can run all the
            /// way through `ServeAsync`'s `finally` (`Settle` → `Ring.Advance` → `Enqueue` of the NEXT planned range)
            /// before the call to `Cancel` even returns. So the probe MUST already be sitting in the queue before
            /// anything is cancelled, and the cancel must not run inline on this thread either — both are honored
            /// below: the queue work finishes and the lock is released FIRST, then `CancelAsync` (not `Cancel`) hands
            /// the callbacks to the thread pool instead of running them here.</summary>
            void CancelAndDrain(Body body, bool keepTail, RangeRequest? first)
            {
                CancellationTokenSource? toCancel = null;
                lock (_gate)
                {
                    if (_inFlight is { } live && ReferenceEquals(_inFlightBody, body) && !(keepTail && _inFlightTail))
                    {
                        _inFlight = null;
                        Interlocked.Increment(ref _cancelled);
                        toCancel = live;
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
                // Outside `_gate`, and only once the probe is already queued — see the hazard note above.
                if (toCancel is not null)
                {
                    try { _ = toCancel.CancelAsync(); }
                    catch (ObjectDisposedException) { }
                }
            }

            void Dropped((Body Body, RangeRequest Req) item)
            {
                Interlocked.Decrement(ref _outstanding);
                item.Body.Dropped(item.Req);
            }

            /// <summary>Starts the ONE consumer task if it is not already running. Not a thread any more (a `Task.Run`
            /// delegate on the pool instead of `Wavee.AudioFetch`): every await inside it is `ConfigureAwait(false)`, so
            /// nothing depends on which pool thread picks up a continuation, and ranges are still served strictly one
            /// after another (I1).</summary>
            void EnsureStarted()
            {
                if (Volatile.Read(ref _loop) is not null) return;
                lock (_gate)
                {
                    if (_loop is not null) return;
                    _scratch ??= GC.AllocateUninitializedArray<byte>(MaxRangeBytes, pinned: true);
                    _chunkScratch ??= GC.AllocateUninitializedArray<byte>(Ring.SlotBytes, pinned: true);
                    _loop = Task.Run(LoopAsync);
                }
            }

            async Task LoopAsync()
            {
                try
                {
                    // `false`: the queue reader completes only once `Dispose` calls `TryComplete`, and by then it has
                    // drained — this loop is not restarted mid-drain.
                    while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        while (_queue.Reader.TryRead(out var item))
                        {
                            try { await ServeAsync(item.Body, item.Req).ConfigureAwait(false); }
                            catch (Exception ex) { Log.Error("audio", "audio fetch loop faulted", ex); }   // ServeAsync
                            // above always settles before it can throw here — this catch is a bug guard, not a path.
                            finally { Interlocked.Decrement(ref _outstanding); }
                        }
                    }
                }
                finally { lock (_gate) _loop = null; }   // a completed queue ends the loop; the next Enqueue restarts it
            }

            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
            async ValueTask ServeAsync(Body body, RangeRequest req)
            {
                if (body.Disposed) return;
                if (!req.Probe && !req.Tail && req.Epoch != body.Epoch) { body.Settle(in req, Source.Stale); return; }

                long start = req.Start, end = req.End;
                body.FillFromDisk(in req, ref start, ref end, _chunkScratch!);  // the disk answers first; the range shrinks
                if (start >= end) { body.Settle(in req, Source.Local); return; }

                Source outcome = Source.Refused;
                CancellationTokenSource? cts = null;
                try
                {
                    cts = new CancellationTokenSource(RangeTimeoutMs);
                    lock (_gate) { _inFlight = cts; _inFlightBody = body; _inFlightTail = req.Tail; }
                    int live = Interlocked.Increment(ref _live);
                    if (live > _peak) Volatile.Write(ref _peak, live);
                    Interlocked.Increment(ref _requests);
                    if (req.Probe && !req.Tail) Interlocked.Increment(ref s_statProbes);

                    long t0 = Stopwatch.GetTimestamp();
                    int want = (int)Math.Min(MaxRangeBytes, end - start);
                    // The body lands every completed 64 KiB slot as it arrives (G-102), so a slow link feeds the reader a
                    // slot at a time instead of making it wait for the whole 512 KiB.
                    FetchResult got = await body.FetchRangeAsync(req, start, end, _scratch.AsMemory(0, want), cts.Token)
                        .ConfigureAwait(false);
                    if (got.Bytes > 0)
                    {
                        Interlocked.Add(ref s_statCdnBytes, got.Bytes);
                        Observe(t0, got.HeadersAt, got.Bytes);
                    }
                    outcome = got.Bytes > 0 ? Source.Cdn : got.Bytes < 0 ? Source.Stale : Source.Refused;
                }
                catch (Exception ex)
                {
                    // The last resort: a fault outside the wire (the disk cache, a decrypt, a bug — `FetchRangeAsync`
                    // itself maps every wire fault to a mirror retry or a settle and does not throw here). The range is
                    // settled as REFUSED regardless: the ring arms its backoff and plans again, never left pending. This
                    // is the actual fix for the bug this batch exists to close — today's `Serve` has no such catch, so an
                    // exception here used to escape past `body.Settle(...)` entirely and starve the ring forever.
                    outcome = cts is { IsCancellationRequested: true } ? Source.Stale : Source.Refused;
                    if (outcome == Source.Refused) Faulted(body, in req, ex);
                }
                finally
                {
                    if (cts is not null)
                    {
                        Interlocked.Decrement(ref _live);
                        lock (_gate) { if (ReferenceEquals(_inFlight, cts)) { _inFlight = null; _inFlightBody = null; } }
                        cts.Dispose();
                    }
                    // ALWAYS, and OUTSIDE `_gate`: `Settle` → `Ring.Settled` takes its own gate, releases it, pulses the
                    // waiters, then `Advance` takes it again and `Enqueue`s the next range outside it (I7).
                    body.Settle(in req, outcome);
                }
            }

            void Faulted(Body body, in RangeRequest req, Exception ex)
            {
                int n = Interlocked.Increment(ref _faults);
                if (n == 1 || ShouldLogFault())   // the very first fault always logs; after that, the shared 5 s gate
                    Log.Error("audio", $"audio.fault file={body.FileIdHex} at={req.Start} faults={n} settled=refused", ex);
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
                CancellationTokenSource? toCancel;
                lock (_gate)
                {
                    toCancel = _inFlight;
                    _inFlight = null;
                    _inFlightBody = null;
                }
                // Same non-inline cancel as `CancelAndDrain` (see its hazard note): outside the lock, via `CancelAsync`.
                if (toCancel is not null)
                {
                    try { _ = toCancel.CancelAsync(); }
                    catch (ObjectDisposedException) { }
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
            /// <summary>What an INTERRUPTIBLE read answers while a seek has asked the source to stop waiting (see
            /// <see cref="Interrupt"/>): not EOF (0), not a fault (−1) — "no bytes yet, your seek is coming".</summary>
            public const int Interrupted = -2;
            /// <summary>What a read answers when its bounded wait ran out with the epoch still current and the body alive:
            /// the link is starving, NOT the track ending (D5). The reader waits again; the pump owns the 90 s budget
            /// (<see cref="StallMs"/>).</summary>
            public const int Starved = -3;
            const int PollMs = 4;
            const int RefusedBackoffMs = 250;

            readonly Body _body;
            readonly Fetcher _fetch;
            // The slot tables are replaced (never mutated in place) by `Grow` and emptied by `Release`, both under _gate.
            byte[][] _slots;
            long[] _slotChunk;                      // the chunk index resident in each slot, −1 empty
            int[] _slotFilled;
            readonly Lock _gate = new();
            readonly object _wake = new();
            readonly int _waitMs;
            long _windowBytes;
            bool _released;
            long _stallSince;                       // TickCount64 when a read began waiting without bytes; 0 while flowing
            long _cursor;
            long _want = -1;
            long _pendingStart = -1;                // the ONE sequential/probe range queued or in flight, by identity
            long _pendingEnd;
            uint _pendingEpoch;
            long _retryAfter;
            long _interruptUntil;                   // TickCount64 before which an interruptible miss answers Interrupted
            long _heldSpan;                         // the last probe's aligned length: a held demand plans no more
            bool _primed;                           // the window was whole since the last Kick / cold Retarget
            bool _fillHeld;                         // a seek is probing: no sequential fill until ResumeFrom
            uint _epoch;
            int _waits, _starves;

            internal Ring(Body body, Fetcher fetch, int seconds, int fileBytesPerSecond, int waitMs)
            {
                _body = body;
                _fetch = fetch;
                _waitMs = Math.Max(1, waitMs);
                Seconds = seconds;
                FileBytesPerSecond = Math.Max(1, fileBytesPerSecond);
                int count = ReadAheadBudget.Take(SlotCount(seconds, FileBytesPerSecond, body.FileLength));
                _slots = new byte[count][];
                _slotChunk = new long[count];
                _slotFilled = new int[count];
                for (int i = 0; i < count; i++)
                {
                    _slots[i] = ReadAheadBudget.RentSlot();
                    _slotChunk[i] = -1;
                }
                _windowBytes = (long)(count - KeepBehindSlots) * SlotBytes;
            }

            /// <summary>The load/seek epoch this ring serves (C4).</summary>
            public uint Epoch => Volatile.Read(ref _epoch);
            public int Slots => Volatile.Read(ref _slots).Length;
            /// <summary>Seconds of audio the ring aims to hold ahead of the cursor (raised by <see cref="Grow"/>).</summary>
            public int Seconds { get; private set; }
            /// <summary>How long the current read has waited without a byte, 0 while bytes flow — what the pump folds into
            /// "Reconnecting" and, past its budget, <c>Fault.Network</c>. A seek starts the count again.</summary>
            public long StallMs
            {
                get
                {
                    long since = Volatile.Read(ref _stallSince);
                    return since == 0 ? 0 : Math.Max(1, Environment.TickCount64 - since);
                }
            }
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
            /// bounded. 0 only at EOF; −1 when superseded (<paramref name="epoch"/> is stale) or the body is gone;
            /// <see cref="Starved"/> when the bound ran out (never the end — call again); <see cref="Interrupted"/> when
            /// <paramref name="interruptible"/> and a seek interrupted the wait.</summary>
            public int ReadAt(long fileOffset, Span<byte> dst, uint epoch, bool interruptible = false)
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
                        if (waited)
                        {
                            Interlocked.Increment(ref _waits);
                            Interlocked.Increment(ref s_statRingWaits);
                            Volatile.Write(ref _want, -1);
                        }
                        ClearStall();
                        Advance(fileOffset + n, demand: false);
                        return n;
                    }
                    if (epoch != Epoch || _body.Disposed) { ClearStall(); return -1; }
                    if (AtEnd(fileOffset)) { ClearStall(); return 0; }
                    if (interruptible && IsInterrupted)
                    {
                        // No demand is planned for a position the seek is about to abandon.
                        if (waited) Volatile.Write(ref _want, -1);
                        return Interrupted;
                    }
                    Volatile.Write(ref _want, fileOffset);
                    if (!waited && Volatile.Read(ref _stallSince) == 0) Volatile.Write(ref _stallSince, Environment.TickCount64);
                    Advance(fileOffset, demand: true);
                    waited = true;
                    lock (_wake) Monitor.Wait(_wake, PollMs);
                    if (Environment.TickCount64 < deadline) continue;
                    Interlocked.Increment(ref _starves);
                    Interlocked.Increment(ref s_statRingStarves);
                    Log.Warn("audio", $"audio.underrun file={_body.FileIdHex} at={fileOffset} waitMs={_waitMs} stallMs={StallMs} "
                                      + $"cursor={Cursor} ring={Seconds}s slots={Slots} inflight={_fetch.Outstanding}");
                    return Starved;
                }
            }

            /// <summary>EOF is only EOF once the length is REAL: past the catalogue estimate a read waits for
            /// `Content-Range` instead of truncating the track.</summary>
            bool AtEnd(long fileOffset) => fileOffset >= _body.FileLength && _body.LengthKnown;

            void ClearStall()
            {
                if (Volatile.Read(ref _stallSince) != 0) Volatile.Write(ref _stallSince, 0);
            }

            /// <summary>A seek (C4): bump the epoch and move the cursor. When the probe's slots are already resident the
            /// seek costs NOTHING — no cancel, no request. Otherwise the in-flight range is cancelled, the queue drained
            /// and the probe put on the wire first, aligned out to whole slots at BOTH ends — its start down, its END
            /// (<paramref name="probeOffset"/> + <paramref name="probeBytes"/>) up — so a window just short of a slot edge
            /// is ONE range covering both slots, never a range that stops at the edge and a second one for the rest.
            /// Nothing is evicted: every slot is tagged with its chunk, so bytes behind stay useful until the ring wraps.
            /// <para>THE FILL IS HELD until <see cref="ResumeFrom"/>: a seek is a run of probes and one landing, and a 512 KiB
            /// fill planned when a non-final probe lands is a request the next probe cancels. While held, only a read that
            /// actually misses plans a range, and no longer than the probe itself. A retarget also ends a pending
            /// <see cref="Interrupt"/> — the seek it was waiting for has arrived.</para>
            /// <para><paramref name="residentElsewhere"/>: the window is served by a store outside the ring (the clear head,
            /// G-116) — the seek is an epoch bump and nothing else: no cancel of the fill in flight, no probe.</para></summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch, bool residentElsewhere = false)
            {
                long length = _body.FileLength;
                long offset = Math.Clamp(probeOffset, 0, Math.Max(0, length - 1));
                long start = AlignDown(offset);
                long end = Math.Min(length, AlignUp(offset + Math.Max(1, probeBytes)));
                bool resident;
                lock (_gate)
                {
                    Volatile.Write(ref _epoch, epoch);
                    Volatile.Write(ref _cursor, Math.Clamp(probeOffset, 0, length));
                    Volatile.Write(ref _interruptUntil, 0);
                    ClearStall();                                      // a seek's wait is counted afresh
                    _retryAfter = 0;                                   // a user gesture outranks a backoff
                    _fillHeld = true;
                    _heldSpan = Math.Max(SlotBytes, end - start);
                    resident = residentElsewhere || Holds(start, end);
                    if (!resident) { _pendingStart = start; _pendingEnd = end; _pendingEpoch = epoch; _primed = false; }
                }
                Log.Info("audio", $"audio.retarget file={_body.FileIdHex} at={probeOffset} probe={start}..{end} "
                                  + $"epoch={epoch} resident={(resident ? 1 : 0)}");
                if (!resident) _fetch.Retarget(_body, new RangeRequest(start, end, epoch, Probe: true));
            }

            /// <summary>The seek landed on a page: the held fill is released and continues from there.</summary>
            public void ResumeFrom(long fileOffset)
            {
                lock (_gate) _fillHeld = false;
                Volatile.Write(ref _cursor, Math.Clamp(fileOffset, 0, _body.FileLength));
                Advance(fileOffset, demand: false);
            }

            /// <summary>True while a fill is held for a seek (between a <see cref="Retarget"/> and its
            /// <see cref="ResumeFrom"/>).</summary>
            public bool FillHeld { get { lock (_gate) return _fillHeld; } }

            /// <summary>A seek is on its way to the decoder that is blocked in this ring's wait: for
            /// <paramref name="forMs"/>, an INTERRUPTIBLE read that would wait answers <see cref="Interrupted"/> at once, so
            /// the engine's decode-ahead producer gets back to its seek mailbox instead of sitting out the 8 s bound.
            /// Resident bytes are still served. The window ends early at the decoder's own <see cref="Retarget"/>, and on its
            /// own after <paramref name="forMs"/> — a seek that never reaches the decoder costs at most that long.</summary>
            public void Interrupt(int forMs)
            {
                Volatile.Write(ref _interruptUntil, Environment.TickCount64 + Math.Max(1, forMs));
                WakeWaiters();
            }

            bool IsInterrupted => Environment.TickCount64 < Volatile.Read(ref _interruptUntil);

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
                lock (_gate) { _primed = false; _fillHeld = false; }
                Volatile.Write(ref _cursor, fileOffset);
                Advance(fileOffset, demand: false);
            }

            /// <summary>Publish landed PLAINTEXT. Requests are slot-aligned, so a chunk is complete, the file's last, or the
            /// head of a slot a broken reply cut short (then <see cref="Holds"/> asks for the chunk again).</summary>
            internal void Land(long start, ReadOnlySpan<byte> plain)
            {
                lock (_gate)
                {
                    if (_released) return;
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

            /// <summary>The mirrors changed under a refusal backoff (a re-resolve landed): plan now, not in 250 ms.</summary>
            internal void RetryNow()
            {
                lock (_gate) _retryAfter = 0;
                WakeWaiters();
                Advance(Cursor, demand: false);
            }

            /// <summary>Raise the ring to the <paramref name="seconds"/> tier — a prepared track (sized ≤ 30 s for the hand-off)
            /// that has become the playing one on a link that earns more (<see cref="Body.Promote"/>; G-114). The extra slots
            /// come out of the shared budget (possibly none), every resident chunk keeps its bytes (re-mapped to its new
            /// direct-mapped index; of two that collide the one the cursor reaches first stays), and the new window fills
            /// from the cursor. Never shrinks.</summary>
            internal void Grow(int seconds)
            {
                int extra;
                lock (_gate)
                {
                    if (_released || seconds <= Seconds) return;
                    extra = SlotCount(seconds, FileBytesPerSecond, _body.FileLength) - _slots.Length;
                }
                if (extra <= 0) { lock (_gate) if (!_released) Seconds = Math.Max(Seconds, seconds); return; }
                int granted = ReadAheadBudget.TakeExtra(extra);
                if (granted <= 0) return;
                var rented = new byte[granted][];
                for (int i = 0; i < granted; i++) rented[i] = ReadAheadBudget.RentSlot();
                int before;
                lock (_gate)
                {
                    if (_released)
                    {
                        foreach (byte[] slot in rented) ReadAheadBudget.ReturnSlot(slot);
                        ReadAheadBudget.Return(granted);
                        return;
                    }
                    before = _slots.Length;
                    Rehash(rented);
                    Seconds = seconds;
                    _primed = false;
                }
                Log.Info("audio", $"audio.ring.grow file={_body.FileIdHex} slots={before}->{Slots} ring={seconds}s");
                Advance(Cursor, demand: false);
            }

            /// <summary>Re-map every resident chunk into a table <paramref name="rented"/>.Length slots larger. Caller holds
            /// <see cref="_gate"/>.</summary>
            void Rehash(byte[][] rented)
            {
                int oldCount = _slots.Length, count = oldCount + rented.Length;
                var slots = new byte[count][];
                var chunks = new long[count];
                var filled = new int[count];
                Array.Fill(chunks, -1L);
                var spare = new byte[count][];
                int spares = 0;
                long cursorChunk = Cursor / SlotBytes;
                for (int i = 0; i < oldCount; i++)
                {
                    long chunk = _slotChunk[i];
                    if (chunk < 0) { spare[spares++] = _slots[i]; continue; }
                    int j = (int)(chunk % count);
                    if (chunks[j] >= 0)
                    {
                        if (Distance(chunk, cursorChunk) >= Distance(chunks[j], cursorChunk)) { spare[spares++] = _slots[i]; continue; }
                        spare[spares++] = slots[j];
                    }
                    slots[j] = _slots[i];
                    chunks[j] = chunk;
                    filled[j] = _slotFilled[i];
                }
                foreach (byte[] slot in rented) spare[spares++] = slot;
                for (int j = 0, s = 0; j < count; j++) if (slots[j] is null) slots[j] = spare[s++];
                Volatile.Write(ref _slots, slots);
                _slotChunk = chunks;
                _slotFilled = filled;
                _windowBytes = (long)(count - KeepBehindSlots) * SlotBytes;

                // Ahead of the cursor is worth more than behind it.
                static long Distance(long chunk, long cursor) => chunk >= cursor ? chunk - cursor : (cursor - chunk) + (1L << 40);
            }

            /// <summary>The body is gone: stop serving and hand every slot array back to the pool. Answers how many slots
            /// were granted, for the budget. Every reader and writer of a slot holds <see cref="_gate"/> and checks the flag,
            /// so no array is touched after it is handed back.</summary>
            internal int Release()
            {
                lock (_gate)
                {
                    if (_released) return 0;
                    _released = true;
                    byte[][] slots = _slots;
                    foreach (byte[] slot in slots) ReadAheadBudget.ReturnSlot(slot);
                    Volatile.Write(ref _slots, Array.Empty<byte[]>());
                    _slotChunk = [];
                    _slotFilled = [];
                    return slots.Length;
                }
            }

            /// <summary>Is [start, end) resident, every chunk complete? Callers hold <see cref="_gate"/> or accept a race
            /// that at worst costs one redundant request.</summary>
            internal bool Holds(long start, long end)
            {
                if (_released) return false;
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
                    if (!TryPlan(demand, out req)) return;
                }
                _fetch.Enqueue(_body, req);
            }

            /// <summary>HYSTERESIS: fill until the window is whole, then stay quiet until the run ahead of the cursor drops
            /// below the low-water mark, then refill to whole again. Without it a full ring would issue a 64 KiB request
            /// every time the cursor crossed a slot (~1.6 s at 320 kbit/s); with it steady-state listening costs one
            /// 512 KiB range per ~13 s. While a seek holds the fill (<see cref="Retarget"/>), only a
            /// <paramref name="demand"/> — a read that missed — plans, and no more than the probe's own span.</summary>
            bool TryPlan(bool demand, out RangeRequest req)
            {
                req = default;
                if (_released || _pendingStart >= 0 || _body.Disposed || Environment.TickCount64 < _retryAfter) return false;
                if (_fillHeld && !demand) return false;
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
                if (_fillHeld) end = Math.Min(end, hole + Math.Min(Fetcher.MaxRangeBytes, _heldSpan));
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
                if (_released) return 0;
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
            readonly byte[]? _key;
            readonly BodyDecrypt? _decrypt;
            readonly Fetcher _fetcher;
            readonly ChunkDiskCache? _disk;
            readonly Ring _ring;
            readonly bool _metered;
            readonly Func<string, string[]?>? _reresolve;
            readonly Action<Action>? _dispatch;
            readonly ManualResetEventSlim _tailLanded = new(false);
            readonly ManualResetEventSlim _firstBody = new(false);
            readonly long _t0 = Stopwatch.GetTimestamp();
            string[] _mirrors;
            byte[]? _head;
            long _fileLength, _proven, _nextResolveAt;
            long _bodyBytes, _landedAt, _refusedAt;
            long _tailGranule = -1, _tailCandidate = -1;
            int _lengthKnown, _mirror, _disposed, _firstServed, _proof, _tailQueued, _sizeDeclared, _ringLogged;
            int _firstStore, _firstServedMs = -1, _gainBits, _peakBits, _gainKnown, _promoted, _resolving, _resolves;
            int _refusals;

            /// <summary>The window a seek's interrupt lasts when the caller names none (see <see cref="Ring.Interrupt"/>).</summary>
            public const int DefaultInterruptMs = 1_000;

            /// <summary>= <see cref="Ring.Interrupted"/>: what an interruptible <see cref="ReadAt"/> answers during a seek's
            /// interrupt window.</summary>
            public const int Interrupted = Ring.Interrupted;

            /// <summary>= <see cref="Ring.Starved"/>: the bounded wait ran out — the link is starving, the track is not over.</summary>
            public const int Starved = Ring.Starved;

            /// <summary>A body asks storage-resolve again at most this often (G-115).</summary>
            public const int ResolveBackoffMs = 5_000;

            /// <summary>Build a body. Nothing here touches the network, so a test builds one over a fake source and a
            /// temp-directory cache.</summary>
            /// <param name="mirrors">The CDN urls; EMPTY for a body that opened off the disk cache without asking (G-120) —
            /// the first byte the cache cannot answer resolves them through <paramref name="reresolve"/>.</param>
            /// <param name="skip">`HeaderBytesFor(format)`: 0xa7 for Ogg/MP3, 0 for FLAC and external bodies.</param>
            /// <param name="fileLength">The RAW length, or the catalogue estimate when <paramref name="lengthKnown"/> is false.</param>
            /// <param name="head">The clear head file, or null. Serves [0, head.Length) until chunk 0 proves it.</param>
            /// <param name="metered">Picks the 10 s tier.</param>
            /// <param name="waitMs">`ReadAt`'s bound; <see cref="Ring.DefaultWaitMs"/> outside tests.</param>
            /// <param name="peak">The track's LINEAR true peak (header byte 148, or the catalogue's), 0 when unknown — what
            /// the decoders cap the normalization gain with.</param>
            /// <param name="prepared">The NEXT track, opened ahead of the hand-off: its ring is sized at no more than
            /// <see cref="ReadAheadBudget.PreparedSeconds"/> out of the shared budget.</param>
            /// <param name="gainKnown">False for an Ogg body that opened with no header at hand (no head, nothing cached):
            /// the gain and peak are then read off chunk 0 when it lands (G-107), before its bytes are served.</param>
            /// <param name="reresolve">Storage-resolve for <paramref name="fileIdHex"/>, BYPASSING the mirror cache: called when
            /// every mirror refused (expired CDN urls, G-115) or there are none. Null: the urls are fixed.</param>
            /// <param name="dispatch">Where a re-resolve runs (an api thread in production); null runs it inline.</param>
            /// <param name="decrypt">A native decryptor for this file (<see cref="BodyDecryptorFor"/>); it replaces the
            /// AES-CTR keystream of <paramref name="key"/>. Null decrypts with the key.</param>
            public Body(IRangeSource source, string[] mirrors, byte[]? key, int skip, long fileLength, bool lengthKnown,
                long durationMs, Format fmt, float gainDb, string fileIdHex, byte[]? head, ChunkDiskCache? disk,
                Fetcher? fetcher = null, bool metered = false, int waitMs = Ring.DefaultWaitMs, float peak = 0f,
                bool prepared = false, bool gainKnown = true, Func<string, string[]?>? reresolve = null,
                Action<Action>? dispatch = null, BodyDecrypt? decrypt = null)
            {
                _source = source;
                _mirrors = mirrors;
                _key = key;
                _decrypt = decrypt;
                Skip = Math.Max(0, skip);
                _fileLength = Math.Max(Skip + 1, fileLength);
                _lengthKnown = lengthKnown ? 1 : 0;
                DurationMs = durationMs;
                Fmt = fmt;
                _gainBits = BitConverter.SingleToInt32Bits(gainDb);
                _peakBits = BitConverter.SingleToInt32Bits(SanePeak(peak));
                _gainKnown = gainKnown ? 1 : 0;
                Prepared = prepared;
                FileIdHex = fileIdHex;
                _head = head is { Length: > 0 } ? head : null;
                _disk = disk;
                _fetcher = fetcher ?? Fetcher.Shared;
                _metered = metered;
                _reresolve = reresolve;
                _dispatch = dispatch;
                int rate = RateOf(_fileLength - Skip, durationMs, fmt);
                int seconds = Ring.ReadAheadSeconds(metered, _fetcher.BytesPerSecond, rate);
                if (prepared) seconds = Math.Min(ReadAheadBudget.PreparedSeconds, seconds);
                _ring = new Ring(this, _fetcher, seconds, rate, waitMs);
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
            /// <summary>The normalization gain in dB — the catalogue's, the header's, or (an Ogg body opened with no header
            /// at hand) read off chunk 0 when it lands. 0 until known.</summary>
            public float GainDb => BitConverter.Int32BitsToSingle(Volatile.Read(ref _gainBits));
            /// <summary>The LINEAR true peak the normalization gain is capped by, or 0 when unknown.</summary>
            public float Peak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakBits));
            /// <summary>True once <see cref="GainDb"/> is the file's real figure (always, except an Ogg body waiting for chunk 0).</summary>
            public bool GainKnown => Volatile.Read(ref _gainKnown) != 0;
            /// <summary>Opened as the prepared next track (its ring was sized for the hand-off, not for the whole track).</summary>
            public bool Prepared { get; }
            /// <summary>Storage-resolves this body ran for itself (expired urls, or a cache-opened body's first miss).</summary>
            public int Resolves => Volatile.Read(ref _resolves);

            /// <summary>BODY bytes landed, from the CDN or the disk cache. The clear head is deliberately NOT one of
            /// them: a head-served start is what makes a body that never arrives look like a track that is playing.</summary>
            public long BodyBytes => Interlocked.Read(ref _bodyBytes);

            /// <summary>Ranges that ran out of mirrors — every url answered a non-2xx for the same bytes (G-115).</summary>
            public int Refusals => Volatile.Read(ref _refusals);

            /// <summary>Is the mirror set refusing RIGHT NOW? True when the last thing to happen to this body was a
            /// refused range rather than a landed byte. A COUNT would be wrong here: a body that refused once at second
            /// three, re-resolved and played happily would still be "refused" when it stalls forty minutes later.</summary>
            public bool Refusing => Volatile.Read(ref _refusedAt) > Volatile.Read(ref _landedAt);
            /// <summary>How long the playing read has waited without a byte (<see cref="Ring.StallMs"/>).</summary>
            public long StallMs => _ring.StallMs;
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
            /// <summary>True when the first byte a decoder got came off the clear head — a head-served start (≈ 1 RTT to
            /// sound) rather than a body-served one. False before any byte was served.</summary>
            public bool FirstFromHead => Volatile.Read(ref _firstStore) == 1;
            /// <summary>Milliseconds from this body's construction to the first byte a decoder got, or −1 before it.</summary>
            public int FirstServedMs => Volatile.Read(ref _firstServedMs);
            internal bool Disposed => Volatile.Read(ref _disposed) != 0;
            static bool IsOgg(Format f) => IsOggFormat(f);

            /// <summary>This prepared body is now the PLAYING one (a gapless or crossfade hand-off made it so): it becomes the
            /// body <c>Stream.Stats</c> describes, and its ring — sized ≤ 30 s for the hand-off — grows to the tier the link
            /// has earned (G-114). Once; any thread.</summary>
            public void Promote()
            {
                if (Disposed || Interlocked.Exchange(ref _promoted, 1) != 0) return;
                Volatile.Write(ref s_liveBody, this);
                if (!Prepared) return;
                _ring.Grow(Ring.ReadAheadSeconds(_metered, _fetcher.BytesPerSecond, _ring.FileBytesPerSecond));
            }

            /// <summary>Wait, bounded, for the FIRST body byte — or for the mirrors to refuse, whichever comes first.
            /// True when a byte landed. This is what a lossless open asks before it trusts the rung it picked: the head
            /// answers the first reads whatever the body does, so without this the only symptom of a mirror set that
            /// refuses every range is half a second of audio and then nothing (D7,
            /// <see cref="LosslessFallback.DemoteForNoBody"/>). Blocks the open's own thread; a cancelled
            /// <paramref name="ct"/> throws, as the rest of the open does.</summary>
            public bool WaitForFirstBody(int timeoutMs, CancellationToken ct)
            {
                if (BodyBytes > 0 || Disposed) return BodyBytes > 0;
                _firstBody.Wait(Math.Max(0, timeoutMs), ct);
                return BodyBytes > 0;
            }

            /// <summary>Wait, bounded, for the last Ogg page's granule (the exact length a gapless join is scheduled from,
            /// G-113). True when it is known; a non-Ogg body answers at once (FLAC and MP3 carry their length in their
            /// headers). Blocks the caller — a prepare, never the pump chain or the UI thread.</summary>
            public bool WaitForTail(int timeoutMs, CancellationToken ct)
            {
                if (!IsOgg(Fmt) || TailGranule >= 0 || Disposed) return TailGranule >= 0;
                try { _tailLanded.Wait(Math.Max(0, timeoutMs), ct); }
                catch (OperationCanceledException) { }
                return TailGranule >= 0;
            }

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
            /// store's edge), 0 only at EOF, −1 superseded or disposed, <see cref="Starved"/> when the bounded wait ran out
            /// (call again: a starve is never the end). Blocks only in the ring's bounded wait. An
            /// <paramref name="interruptible"/> read answers <see cref="Interrupted"/> instead of waiting while a seek's
            /// <see cref="InterruptPendingRead"/> window is open — only a caller that knows a seek is coming (the Vorbis
            /// and FLAC adapters) may ask for that; a sequential consumer would read it as the end of the track.</summary>
            public int ReadAt(long offset, Span<byte> dst, uint epoch, bool interruptible = false)
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
                int got = _ring.ReadAt(fileOffset, dst[..cap], epoch, interruptible);
                if (got > 0) FirstServed("ring", offset);
                return got;
            }

            /// <summary>A seek is coming for the decoder that may be blocked in this body's ring wait (the engine's
            /// <c>SeekAsync</c> waits for its decode-ahead producer to come back, and a producer blocked in an underrun
            /// comes back only after the 8 s bound). See <see cref="Ring.Interrupt"/>. Any thread; non-blocking.</summary>
            public void InterruptPendingRead(int forMs = DefaultInterruptMs) => _ring.Interrupt(forMs);

            /// <summary>Would a read at container <paramref name="offset"/> be served without a request — from the head,
            /// the ring, or the slot already queued / in flight?</summary>
            public bool IsResident(long offset)
            {
                long fileOffset = Skip + Math.Max(0, offset);
                if (Volatile.Read(ref _head) is { } head && fileOffset < head.Length) return true;
                return _ring.HoldsLocked(fileOffset, fileOffset + 1) || _ring.IsPending(fileOffset);
            }

            /// <summary>A seek, container coordinates. See <see cref="Ring.Retarget"/>. A window the clear head still serves
            /// is an epoch bump and nothing else (G-116): the fill in flight is not cancelled and no probe goes out.</summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch)
            {
                long fileOffset = Skip + Math.Max(0, probeOffset);
                bool inHead = Volatile.Read(ref _head) is { } head && fileOffset + Math.Max(1, probeBytes) <= head.Length;
                _ring.Retarget(fileOffset, probeBytes, epoch, residentElsewhere: inHead);
            }

            /// <summary>The seek landed, container coordinates. See <see cref="Ring.ResumeFrom"/>.</summary>
            public void ResumeFrom(long offset) => _ring.ResumeFrom(Skip + Math.Max(0, offset));

            /// <summary>The splice proof: decrypted chunk-0 bytes against the clear head over their common prefix. See
            /// <see cref="ProveHeadAt"/>.</summary>
            public bool ProveHead(ReadOnlySpan<byte> chunk0Plain) => ProveHeadAt(0, chunk0Plain);

            /// <summary>The splice proof, a slot at a time: decrypted bytes at <paramref name="fileOffset"/> against the clear
            /// head over their overlap, extending a proven prefix from 0. A mismatch drops the head, supersedes the epoch (the
            /// decoder restarts on the ring's bytes) and answers false. A match answers true and drops the head once the proven
            /// prefix covers all of it — a body that lands 64 KiB slots proves an 80 KiB head in two steps, and keeps serving
            /// the head's tail in between.</summary>
            public bool ProveHeadAt(long fileOffset, ReadOnlySpan<byte> plain)
            {
                byte[]? head = Volatile.Read(ref _head);
                if (head is null) return SpliceProof != 2;
                long proven = Interlocked.Read(ref _proven);
                if (fileOffset < 0 || fileOffset > proven || fileOffset >= head.Length) return true;
                int n = (int)Math.Min(head.Length - fileOffset, plain.Length);
                if (n <= 0) return true;
                bool same = plain[..n].SequenceEqual(head.AsSpan((int)fileOffset, n));
                long reach = fileOffset + n;
                if (same && reach > proven) Interlocked.Exchange(ref _proven, reach);
                bool covered = same && reach >= head.Length;
                if (!same || covered) Volatile.Write(ref _head, null);
                Volatile.Write(ref _proof, same ? 1 : 2);
                Log.Info("audio", $"audio.splice file={FileIdHex} at={fileOffset} bytes={n} of={head.Length} "
                                  + $"proven={(same ? 1 : 0)} covered={(covered ? 1 : 0)} ms={ElapsedMs()}");
                if (!same) _ring.Supersede();
                return same;
            }

            /// <summary>Fetch [start, end) into <paramref name="dst"/> and LAND it: mirrors in order, a reply that does not start
            /// where it was asked read past or refused (<see cref="RangeReply.SkipFor"/>), `Content-Range` adopted as the true
            /// length, and every COMPLETED 64 KiB slot published the moment it arrives — the ciphertext written through to the
            /// disk cache, decrypted IN PLACE at its true file offset, served (G-102: at 64 KB/s a whole 512 KiB range took 8 s,
            /// the reader's whole bound). &gt;0 bytes landed, 0 when every mirror refused (the url set is dropped and a
            /// re-resolve is asked for), −1 when cancelled before a slot landed. A mirror whose wire THROWS mid-body is not
            /// fatal — the next mirror gets the same range; only running out of mirrors is a refusal.</summary>
            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
            internal async ValueTask<FetchResult> FetchRangeAsync(RangeRequest req, long start, long end, Memory<byte> dst,
                CancellationToken ct)
            {
                long headersAt = Stopwatch.GetTimestamp();
                int want = (int)Math.Min(dst.Length, end - start);
                if (want <= 0) return new FetchResult(0, headersAt);
                string[] mirrors = Volatile.Read(ref _mirrors);
                if (mirrors.Length == 0)
                {
                    // A body opened off the disk cache without asking storage-resolve (G-120): this is the first miss.
                    RequestResolve("no mirrors");
                    return new FetchResult(0, headersAt);
                }
                for (int attempt = 0; attempt < mirrors.Length; attempt++)
                {
                    if (ct.IsCancellationRequested) return new FetchResult(-1, headersAt);
                    int index = (_mirror + attempt) % mirrors.Length;
                    IRangeReply? reply = null;
                    int total = 0, landed = 0;
                    try
                    {
                        reply = await _source.OpenAsync(mirrors[index], start, start + want - 1, ct).ConfigureAwait(false);
                        if (reply is null) continue;
                        headersAt = Stopwatch.GetTimestamp();
                        long skip = RangeReply.SkipFor(start, reply.Start);
                        if (skip < 0 || !await ReadPastAsync(reply, skip, dst, ct).ConfigureAwait(false))
                        {
                            Log.Warn("audio", $"audio.range file={FileIdHex} at={start} replyStart={reply.Start} refused (not the asked range)");
                            continue;
                        }
                        AdoptLength(reply.TotalLength);
                        while (total < want && !ct.IsCancellationRequested)
                        {
                            int n = await reply.ReadAsync(dst.Slice(total, want - total), ct).ConfigureAwait(false);
                            if (n <= 0) break;
                            total += n;
                            int whole = total / Ring.SlotBytes * Ring.SlotBytes;
                            if (whole - landed < Ring.SlotBytes || ct.IsCancellationRequested) continue;
                            Publish(in req, start + landed, dst.Span[landed..whole]);
                            landed = whole;
                        }
                    }
                    catch (Exception) when (ct.IsCancellationRequested) { }             // a seek or the deadline: answered
                    // below — MUST come first: a cancelled read commonly throws a wire-fault-shaped exception too.
                    catch (Exception ex) when (IsWireFault(ex))                          // THIS mirror broke: the next tries
                    {
                        if (_fetcher.ShouldLogFault())
                            Log.Warn("audio", $"audio.range file={FileIdHex} at={start} mirror={index} faulted after {total} bytes", ex);
                        total = 0;
                    }
                    finally { reply?.Dispose(); }                                        // HTTP/2: RST_STREAM if unfinished
                    if (ct.IsCancellationRequested) return new FetchResult(landed > 0 ? landed : -1, headersAt);
                    if (total <= 0) continue;

                    _mirror = index;
                    if (total > landed) Publish(in req, start + landed, dst.Span[landed..total]);
                    Log.Info("audio", $"audio.range file={FileIdHex} at={start} len={total} src=cdn pingMs={_fetcher.PingMs} "
                                      + $"kbps={_fetcher.BytesPerSecond * 8 / 1000} ms={ElapsedMs()}");
                    return new FetchResult(total, headersAt);
                }
                if (ct.IsCancellationRequested) return new FetchResult(-1, headersAt);
                InvalidateMirrors(FileIdHex);
                Interlocked.Increment(ref _refusals);
                Volatile.Write(ref _refusedAt, Environment.TickCount64);
                _firstBody.Set();                          // a refusal is an ANSWER: the open's first-body wait ends here
                Log.Warn("audio", $"audio.range file={FileIdHex} at={start} len=0 src=cdn mirrors={mirrors.Length} "
                                  + $"refused n={Refusals}");
                RequestResolve("refused");
                return new FetchResult(0, headersAt);

                static bool IsWireFault(Exception ex)
                    => ex is HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException;
            }

            /// <summary>Discard <paramref name="bytes"/> of a reply that started at 0 instead of the asked offset.</summary>
            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
            static async ValueTask<bool> ReadPastAsync(IRangeReply reply, long bytes, Memory<byte> scratch, CancellationToken ct)
            {
                while (bytes > 0)                                            // bytes == 0 (the norm): completes synchronously
                {
                    int n = await reply.ReadAsync(scratch[..(int)Math.Min(scratch.Length, bytes)], ct).ConfigureAwait(false);
                    if (n <= 0) return false;
                    bytes -= n;
                }
                return true;
            }

            /// <summary>One landed piece of CIPHERTEXT at <paramref name="at"/>: through to the disk cache, decrypted in place,
            /// then served.</summary>
            void Publish(in RangeRequest req, long at, Span<byte> bytes)
            {
                WriteThrough(at, bytes);
                if (_decrypt is not null)
                {
                    if (at == 0 && SpliceProof == 0) Log.Info("audio", $"audio.key file={FileIdHex} native=1");
                    _decrypt(bytes, at);
                }
                else if (_key is not null)
                {
                    if (at == 0 && SpliceProof == 0)
                        Log.Info("audio", $"audio.key file={FileIdHex} validates={(Ctr.Validates(bytes, _key) ? 1 : 0)}");
                    Ctr.DecryptInPlace(bytes, _key, at);
                }
                Land(in req, at, bytes);
            }

            /// <summary>Storage-resolve this file again, off the fetch task (G-115): every mirror refused — the urls expired
            /// after a long pause, or a prepared track's TTL ran out — or the body opened off the cache with none. At most one
            /// at a time and one per <see cref="ResolveBackoffMs"/>; the fresh urls replace the old ones and the ring plans at
            /// once.</summary>
            void RequestResolve(string why)
            {
                if (_reresolve is null || Disposed) return;
                if (Environment.TickCount64 < Interlocked.Read(ref _nextResolveAt)) return;
                if (Interlocked.Exchange(ref _resolving, 1) != 0) return;
                Action work = () =>
                {
                    try
                    {
                        string[]? urls = _reresolve(FileIdHex);
                        // The seam answers URLS; WHY it answered them is on the service's own reply, which `Resolve`
                        // just recorded. Without it a fresh url set that the CDN then refuses is indistinguishable in
                        // the log from a service that refused us outright — the same line, ten times, for 52 seconds.
                        ResolveAnswer answer = LastResolve(FileIdHex);
                        if (urls is { Length: > 0 } && !Disposed)
                        {
                            Volatile.Write(ref _mirrors, urls);
                            _mirror = 0;
                            Interlocked.Increment(ref _resolves);
                            Log.Info("audio", $"audio.resolve file={FileIdHex} why={why} mirrors={urls.Length} "
                                              + $"status={answer.Status} verdict={answer.Verdict} refusals={Refusals} "
                                              + $"ms={ElapsedMs()}");
                        }
                        else Log.Warn("audio", $"audio.resolve file={FileIdHex} why={why} mirrors=0 "
                                               + $"status={answer.Status} verdict={answer.Verdict} refusals={Refusals} "
                                               + $"ms={ElapsedMs()} answered no mirrors");
                    }
                    catch (Exception ex) { Log.Warn("audio", "audio re-resolve failed", ex); }
                    finally
                    {
                        Interlocked.Exchange(ref _nextResolveAt, Environment.TickCount64 + ResolveBackoffMs);
                        Volatile.Write(ref _resolving, 0);
                    }
                    if (!Disposed) _ring.RetryNow();
                };
                if (_dispatch is { } dispatch) dispatch(work);
                else work();
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
                Interlocked.Increment(ref s_statCacheHits);
                Span<byte> plain = scratch[..length];
                if (_decrypt is not null) _decrypt(plain, chunk * Ring.SlotBytes);
                else if (_key is not null) Ctr.DecryptInPlace(plain, _key, chunk * Ring.SlotBytes);
                Observe(chunk * Ring.SlotBytes, plain);
                if (publish) _ring.Land(chunk * Ring.SlotBytes, plain);
                return true;
            }

            /// <summary>Landed plaintext: first what it teaches (the splice proof, the late gain, the tail) — so a decoder
            /// that reads these bytes the moment they are served already sees the gain they carry — then into the ring
            /// (unless it is the tail).</summary>
            internal void Land(in RangeRequest req, long start, ReadOnlySpan<byte> plain)
            {
                Observe(start, plain);
                if (!req.Tail) _ring.Land(start, plain);
            }

            void Observe(long start, ReadOnlySpan<byte> plain)
            {
                if (plain.Length > 0)
                {
                    // The one funnel for landed body bytes — a CDN range and a disk chunk both come through here — and
                    // therefore the one place that can say "this body has served something of its own".
                    Volatile.Write(ref _landedAt, Environment.TickCount64);
                    if (Interlocked.Add(ref _bodyBytes, plain.Length) == plain.Length) _firstBody.Set();
                }
                if (Volatile.Read(ref _head) is { } head && start < head.Length) ProveHeadAt(start, plain);
                if (start == 0 && !GainKnown && plain.Length >= HeaderGainBytes)
                {
                    // An Ogg body that opened with no header at hand: chunk 0 IS the header (G-107).
                    (float gain, float peak) = GainFor(Fmt, 0f, 0f, plain);
                    Volatile.Write(ref _peakBits, BitConverter.SingleToInt32Bits(peak));
                    Volatile.Write(ref _gainBits, BitConverter.SingleToInt32Bits(gain));
                    Volatile.Write(ref _gainKnown, 1);
                    Log.Info("audio", $"audio.gain file={FileIdHex} gain={gain:0.0} dB peak={peak:0.000} from=chunk0 ms={ElapsedMs()}");
                }
                long length = FileLength;
                if (!IsOgg(Fmt) || !LengthKnown || TailGranule >= 0 || start + plain.Length <= length - 2L * Ring.SlotBytes) return;
                // Bytes arrive in ascending order (a range, or the disk a chunk at a time), so a page found in the slot
                // before the last stays the answer when the last slot is only a page's body.
                long granule = LastOggGranule(plain);
                if (granule >= 0) _tailCandidate = granule;
                if (start + plain.Length < length || _tailCandidate < 0) return;
                Interlocked.Exchange(ref _tailGranule, _tailCandidate);
                _tailLanded.Set();
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
                long ms = ElapsedMs();
                Volatile.Write(ref _firstStore, store == "head" ? 1 : 2);
                Volatile.Write(ref _firstServedMs, (int)Math.Min(int.MaxValue, ms));
                Log.Info("audio", $"audio.first file={FileIdHex} from={store} at={offset} ms={ms}");
            }

            long ElapsedMs() => (long)Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;

            /// <summary>Cancel this file's work, release a blocked read, hand the ring's slots back to the shared budget and
            /// its arrays to the pool.</summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _fetcher.Forget(this);
                Volatile.Write(ref _head, null);
                _ring.WakeWaiters();
                _tailLanded.Set();
                _firstBody.Set();                           // nothing will land now: an open still waiting must come back
                ReadAheadBudget.Return(_ring.Release());
                Interlocked.CompareExchange(ref s_liveBody, null, this);
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
        public sealed class BodyStream(Body body) : System.IO.Stream
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
                int n;
                while ((n = body.ReadAt(_position, buffer, body.Epoch)) == Body.Starved) { }   // a starve is never the end (D5)
                if (n <= 0) return 0;                           // −1 (superseded, disposed) reads as end-of-stream to a Stream caller
                _position += n;
                return n;
            }

            /// <summary>A position write only. It deliberately does NOT retarget: a sequential `Stream` consumer (a module
            /// stream) may seek to probe — at open, near the end — and a cancel there would kill the cold-start range. The miss
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

        /// <summary>The audio key as a seam — the shape of <see cref="Key"/>.</summary>
        public delegate Fault KeySource(string fileIdHex, ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid,
            Span<byte> key16, bool apEligible, CancellationToken ct);

        /// <summary>Everything <see cref="OpenBody(in FileChoice, OpenSeams, CancellationToken, bool)"/> reaches outside
        /// itself, as ONE value: the wire, the disk cache, the head, storage-resolve, the key, the external length probe and
        /// where parallel work runs. <see cref="Live"/> is production; a test hands fakes and counts what the open asked.
        /// <see cref="Decryptor"/> is the native body decryptor the key path may have left for a file (null: AES-CTR).</summary>
        public readonly record struct OpenSeams(
            IRangeSource Source, ChunkDiskCache? Disk,
            Func<string, CancellationToken, byte[]> Head,
            Func<FileRef, CancellationToken, Mirrors> Resolve,
            KeySource Key,
            Func<string, CancellationToken, long> ExternalLength,
            Func<Action, bool> Run,
            Fetcher? Fetcher = null,
            bool Metered = false,
            Func<string, BodyDecrypt?>? Decryptor = null)
        {
            /// <summary>The real wire, the shared cache, the head cache, the api pool, the platform's metered flag, and the
            /// private assembly's native decryptor when one is installed.</summary>
            public static OpenSeams Live() => new(HttpRangeSource.Shared, DiskCache.Shared, HeadCache.Fetch, Audio.Resolve,
                Audio.Key, Audio.ExternalLength, Api.Run, null, MeteredConnection?.Invoke() ?? false, BodyDecryptorFor);
        }

        /// <summary>§5.2's open over the live seams.</summary>
        internal static Opened OpenBody(in FileChoice choice, CancellationToken ct, bool prepared = false)
            => OpenBody(in choice, OpenSeams.Live(), ct, prepared);

        /// <summary>§5.2's open: the clear head, storage-resolve and the audio key AT THE SAME TIME (head + resolve on
        /// <see cref="OpenSeams.Run"/>, the key here), then the first body range and — once `Content-Range` has named the
        /// length — the tail. The first decodable byte needs neither the key nor the CDN.
        /// <para>A file the disk cache has a size for asks NEITHER the head (chunk 0 is the head) NOR storage-resolve (plan
        /// §5.4: a cached replay is 0 requests; G-120): its body starts with no mirrors and resolves them on the first byte
        /// the cache cannot answer. The normalization is <see cref="GainFor"/> — the catalogue's, else the Ogg header's
        /// (never a FLAC's byte 144, G-105) — and an Ogg body with no header at hand learns it off chunk 0 (G-107).</para>
        /// <para>A <paramref name="prepared"/> open is the NEXT track: its ring is sized for the hand-off out of the shared
        /// budget, and it does not become the body `Stream.Stats` describes until it is promoted.</para></summary>
        public static Opened OpenBody(in FileChoice choice, OpenSeams seams, CancellationToken ct, bool prepared = false)
        {
            string fileIdHex = choice.FileIdHex;
            // The id AND the format, together, because that pair is what storage-resolve signs a url for. It is a
            // local rather than `choice.Ref` at every use because `choice` is an `in` parameter and the re-resolve
            // closure below has to capture it.
            FileRef file = choice.Ref;
            Log.Info("audio", $"audio.open.begin file={fileIdHex} fmt={choice.Fmt} prepared={(prepared ? 1 : 0)}");
            long t0 = Stopwatch.GetTimestamp();
            Action<Action> dispatch = work => { if (!seams.Run(work)) work(); };

            if (choice.ExternalUrl is { Length: > 0 } external)
                return OpenExternal(in choice, external, seams, ct, prepared);

            ChunkDiskCache? disk = seams.Disk;
            long cachedLength = disk?.KnownSize(fileIdHex) ?? 0;
            bool cached = cachedLength > 0;
            byte[]? cachedChunk0 = null;
            if (cached)
            {
                var chunk = new byte[DiskCache.ChunkBytes];
                if (disk!.TryReadChunk(fileIdHex, 0, chunk, out int n) && n > Ctr.HeaderBytes)
                {
                    cachedChunk0 = chunk;
                    Interlocked.Increment(ref s_statCacheHits);
                }
            }

            // THE PARALLEL TRIO. `Run` is a named pool (P10); when it refuses, the work runs inline — a slower open, never a
            // failed one. The countdown is never disposed: a cancelled open must not strand a worker's Signal.
            byte[] head = [];
            Mirrors mirrors = new([], 0, Fault.Network);                   // never `default`: its Urls would be null
            var pending = new CountdownEvent(2);
            bool headInline = cachedChunk0 is not null || !seams.Run(() =>
            {
                try { head = seams.Head(fileIdHex, ct); } finally { pending.Signal(); }
            });
            if (headInline) pending.Signal();
            bool resolveInline = cached || !seams.Run(() =>
            {
                try { mirrors = seams.Resolve(file, ct); } finally { pending.Signal(); }
            });
            if (resolveInline) pending.Signal();

            Span<byte> key = stackalloc byte[AudioKey.KeyLength];
            Fault fault = seams.Key(fileIdHex, choice.FileId, choice.TrackGid, key, ApEligible(choice.Fmt), ct);
            if (headInline && cachedChunk0 is null) head = seams.Head(fileIdHex, ct);
            if (resolveInline && !cached) mirrors = seams.Resolve(file, ct);
            pending.Wait(ct);

            // `verdict=` is the service's own answer for this file, and it belongs HERE rather than only on the
            // re-resolve line: an open that never re-resolves (`resolves=0`) used to print a mirror count and nothing
            // about where it came from, so a url set signed for the wrong object namespace and a url set the account is
            // not entitled to read exactly alike. "unasked" is the honest reading of a cached open, which asks nothing.
            // …and the HOST it answered with, because that is where the format lands: the lossless objects live on a
            // different edge from the Ogg ones (`audio-fa-l.` rather than `audio-fa.`), so the host is the one thing on
            // this line that says out loud which namespace the url set was signed for. Host only — the rest is a token.
            ResolveAnswer answer = LastResolve(fileIdHex);
            Log.Info("audio", $"audio.head file={fileIdHex} bytes={head.Length} cached={(cachedChunk0 is null ? 0 : 1)} "
                              + $"mirrors={(mirrors.Ok ? mirrors.Urls.Length : 0)} resolve={(cached ? "lazy" : "asked")} "
                              + $"host={(mirrors.Ok ? HttpRangeSource.HostOf(mirrors.Urls[0]) : "-")} "
                              + $"status={answer.Status} verdict={answer.Verdict} key={fault} "
                              + $"ms={(long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds}");
            if (fault != Fault.None)
            {
                Log.Warn("spotify", "no audio key for " + fileIdHex + " (" + fault + ")");
                return Opened.Failed(fault);
            }
            if (!cached && !mirrors.Ok) return Opened.Failed(mirrors.Fault == Fault.None ? Fault.Network : mirrors.Fault);

            // Asked AFTER the key: the deriver that answered it is what leaves a native decryptor for this file.
            BodyDecrypt? decrypt = seams.Decryptor?.Invoke(fileIdHex);
            byte[]? clearCached = null;
            if (head.Length < HeaderGainBytes && cachedChunk0 is not null)
            {
                clearCached = cachedChunk0.AsSpan(0, HeaderGainBytes).ToArray();
                if (decrypt is not null) decrypt(clearCached, 0);
                else Ctr.DecryptInPlace(clearCached, key, 0);
            }
            ReadOnlySpan<byte> header = head.Length >= HeaderGainBytes ? head : clearCached;
            (float gain, float peak) = GainFor(choice.Fmt, choice.GainDb, choice.Peak, header);
            bool gainKnown = !header.IsEmpty || choice.GainDb != 0f || !IsOggFormat(choice.Fmt);

            int skip = HeaderBytesFor(choice.Fmt);
            long length = cached
                ? cachedLength
                : skip + Math.Max(Ring.SlotBytes, choice.DurationMs * NominalBytesPerSecond(choice.Fmt) / 1000);
            Func<string, string[]?> reresolve = hex =>
            {
                Mirrors fresh = seams.Resolve(new FileRef(hex, file.Wire), CancellationToken.None);
                return fresh.Ok ? fresh.Urls : null;
            };
            var body = new Body(seams.Source, mirrors.Ok ? mirrors.Urls : Array.Empty<string>(), key.ToArray(), skip, length, cached,
                choice.DurationMs, choice.Fmt, gain, fileIdHex, head, disk, seams.Fetcher, seams.Metered, peak: peak,
                prepared: prepared, gainKnown: gainKnown, reresolve: reresolve, dispatch: dispatch, decrypt: decrypt);
            if (!prepared) Volatile.Write(ref s_liveBody, body);
            body.Start();

            // D7's FIRST-BODY DEADLINE, and the reason the head is not an unqualified good. An 80 KiB clear head is
            // about 0.6 s of a 1000 kbit/s FLAC, and it is served whether or not a single body byte ever arrives — so a
            // mirror set that refuses every range produces a track that starts, plays for half a second, and is silence
            // for the rest of its 4 minutes. The wait below ends the instant a byte lands OR the mirrors refuse, so a
            // healthy open pays its first slot's latency and a refused one gives the rung up in about a second; the
            // answer is `Fault.Refused`, which `Open` turns into the Ogg 320 rung — a different file id on a different
            // mirror set, which is the only thing left that has not said no.
            if (choice.Fmt is Format.Flac or Format.Flac24)
            {
                long firstBodyT0 = Stopwatch.GetTimestamp();
                body.WaitForFirstBody(LosslessFallback.FirstBodyMs, ct);
                long waitedMs = (long)Stopwatch.GetElapsedTime(firstBodyT0).TotalMilliseconds;
                if (LosslessFallback.DemoteForNoBody(choice.Fmt, body.BodyBytes, body.Refusing, waitedMs))
                {
                    Log.Warn("audio", $"audio.nobody file={fileIdHex} fmt={choice.Fmt} waitMs={waitedMs} "
                                      + $"refusals={body.Refusals} head={body.HeadBytes} resolves={body.Resolves}");
                    body.Dispose();
                    return Opened.Failed(Fault.Refused);
                }
            }
            return new Opened(new BodyStream(body), choice.Fmt, body.Length, choice.DurationMs, body.GainDb, fileIdHex,
                Fault.None, body, body.Peak);
        }

        /// <summary>A plain body on somebody else's host (a podcast enclosure): no key, no cache, no resolve. Its length is
        /// probed (<see cref="OpenSeams.ExternalLength"/>); a host that answers neither a HEAD length nor a `Content-Range`
        /// still opens, on the duration's estimate, and the first range names the truth (G-119). Only an unreachable host
        /// fails.</summary>
        static Opened OpenExternal(in FileChoice choice, string url, OpenSeams seams, CancellationToken ct, bool prepared)
        {
            long probed = seams.ExternalLength(url, ct);
            if (probed < 0) return Opened.Failed(Fault.Network);
            bool known = probed > 0;
            long length = known ? probed : Math.Max(Ring.SlotBytes, choice.DurationMs * NominalBytesPerSecond(choice.Fmt) / 1000);
            var plain = new Body(seams.Source, [url], null, 0, length, known, choice.DurationMs, choice.Fmt, choice.GainDb,
                choice.FileIdHex, null, null, seams.Fetcher, seams.Metered, peak: choice.Peak, prepared: prepared);
            if (!prepared) Volatile.Write(ref s_liveBody, plain);
            plain.Start();
            return new Opened(new BodyStream(plain), choice.Fmt, plain.Length, choice.DurationMs, choice.GainDb,
                choice.FileIdHex, Fault.None, plain, plain.Peak);
        }

        // ── 8. the next track ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Warm what the NEXT track's open needs — the clear head, the mirror list, the audio key — without
        /// opening it (<see cref="Prefetch(string)"/> runs the ladder first). The boundary's `Open` then finds all three in
        /// this file's bounded caches and costs the first range + the tail. Non-blocking (api threads); a prefetch that
        /// fails costs a slower `Open`, never a fault.</summary>
        public static void Prefetch(in FileChoice choice)
        {
            if (!choice.Ok || choice.ExternalUrl is { Length: > 0 } || choice.FileIdHex.Length == 0) return;
            string fileIdHex = choice.FileIdHex;
            FileRef file = choice.Ref;
            byte[] fileId = choice.FileId, gid = choice.TrackGid;
            bool apEligible = ApEligible(choice.Fmt);
            Api.Run(() => HeadCache.Fetch(fileIdHex, CancellationToken.None));
            Api.Run(() =>
            {
                Mirrors warm = Resolve(file, CancellationToken.None);
                Span<byte> key = stackalloc byte[AudioKey.KeyLength];
                Fault fault = Key(fileIdHex, fileId, gid, key, apEligible, CancellationToken.None);
                Log.Info("audio", $"audio.prefetch file={fileIdHex} mirrors={(warm.Ok ? warm.Urls.Length : 0)} key={fault}");
            });
        }

        /// <summary>The most recent heads, so a prefetch's head and the boundary `Open`'s head are ONE request. Head
        /// files are immutable (0.2.9's `HeadFileClient` held 32 behind `FreshnessPolicy.Immutable`); the live row, the
        /// prepared row and the two prefetched ones need four, and it is bounded (C8).</summary>
        static class HeadCache
        {
            public const int Slots = 4;
            static readonly Lock Gate = new();
            static readonly string[] s_keys = ["", "", "", ""];
            static readonly byte[][] s_vals = [[], [], [], []];

            public static byte[] Fetch(string fileIdHex, CancellationToken ct)
            {
                lock (Gate)
                {
                    for (int i = 0; i < Slots; i++)
                        if (string.Equals(s_keys[i], fileIdHex, StringComparison.OrdinalIgnoreCase)) return s_vals[i];
                }
                byte[] bytes = Head(fileIdHex, ct);
                if (bytes.Length == 0) return [];
                lock (Gate)
                {
                    for (int i = Slots - 1; i > 0; i--) { s_keys[i] = s_keys[i - 1]; s_vals[i] = s_vals[i - 1]; }
                    s_keys[0] = fileIdHex;
                    s_vals[0] = bytes;
                }
                return bytes;
            }
        }
    }
}
