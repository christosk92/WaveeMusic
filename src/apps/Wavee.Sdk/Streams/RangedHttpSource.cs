using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wavee.Sdk.Streams;

/// <summary>Pure decision for the CDN read-ahead window size (see <see cref="RangedHttpSource.ConfigureReadAhead"/>).
/// No I/O, no clock reads — a table function of (measured throughput, bitrate, metered, memory cap) so it is testable
/// without a network. Never returns below <see cref="FloorBytes"/> (today's fixed baseline) regardless of inputs.</summary>
public static class ReadAheadPolicy
{
    /// <summary>The floor this policy will never go below — today's fixed read-ahead window.</summary>
    public const int FloorBytes = 256 * 1024;
    /// <summary>Seconds of audio held on a metered/roaming/over-limit connection.</summary>
    public const int MeteredWindowSeconds = 15;
    /// <summary>Seconds of audio held at/near realtime throughput.</summary>
    public const int NearRealtimeWindowSeconds = 30;
    /// <summary>Seconds of audio held once throughput is comfortably ahead of realtime (a "whole track" proxy — a
    /// 4-minute 320 kbps track is ~9.6 MB, well inside this at 320 kbps).</summary>
    public const int FastWindowSeconds = 600;
    /// <summary>Delivered-vs-realtime ratio at/above which the window grows toward <see cref="FastWindowSeconds"/>.</summary>
    public const double FastThroughputMultiple = 3.0;
    /// <summary>Bitrate assumed when the caller has not supplied one yet (very high quality Ogg, so the window errs
    /// generous rather than starving a track we don't know the rate of).</summary>
    const int DefaultBitrateBitsPerSec = 320_000;

    /// <summary>Why <see cref="Decision.WindowBytes"/> landed where it did — always logged, never gated.</summary>
    public enum Reason { Bandwidth, Metered, MemoryCap }

    /// <summary>The computed window, in both units, plus why.</summary>
    public readonly record struct Decision(int WindowBytes, int WindowSeconds, Reason Reason);

    /// <summary>Compute the read-ahead window. All inputs are plain numbers — no I/O.</summary>
    /// <param name="measuredBytesPerSec">Rolling delivered-bytes/sec from recent range fetches; 0 = not measured yet
    /// (treated as near-realtime, the conservative assumption).</param>
    /// <param name="bitrateBitsPerSec">The track's bitrate; &lt;= 0 = unknown (assumes a high-quality Ogg rate).</param>
    /// <param name="metered">True on a metered/roaming/over-limit connection — clamps to a modest window regardless
    /// of throughput.</param>
    /// <param name="memoryCapBytes">This stream's share of the read-ahead memory budget (bounds the total across the
    /// active stream + a gapless-prepared next track — see <see cref="RangedHttpSource.ConfigureReadAhead"/>).</param>
    public static Decision Compute(long measuredBytesPerSec, int bitrateBitsPerSec, bool metered, long memoryCapBytes)
    {
        int bitrateBytesPerSec = bitrateBitsPerSec > 0 ? bitrateBitsPerSec / 8 : DefaultBitrateBitsPerSec / 8;
        long cap = Math.Max(FloorBytes, memoryCapBytes);

        if (metered)
        {
            long meteredBytes = (long)bitrateBytesPerSec * MeteredWindowSeconds;
            long clamped = Math.Max(FloorBytes, Math.Min(meteredBytes, cap));
            return Make(clamped, bitrateBytesPerSec, Reason.Metered);
        }

        double realtimeRatio = measuredBytesPerSec > 0 ? measuredBytesPerSec / (double)bitrateBytesPerSec : 1.0;
        int targetSeconds = realtimeRatio >= FastThroughputMultiple ? FastWindowSeconds : NearRealtimeWindowSeconds;
        long target = (long)bitrateBytesPerSec * targetSeconds;
        long bounded = Math.Min(target, cap);
        var reason = bounded < target ? Reason.MemoryCap : Reason.Bandwidth;
        return Make(Math.Max(FloorBytes, bounded), bitrateBytesPerSec, reason);
    }

    static Decision Make(long windowBytes, int bitrateBytesPerSec, Reason reason)
    {
        int bytes = (int)Math.Min(int.MaxValue, windowBytes);
        int seconds = bitrateBytesPerSec > 0 ? bytes / bitrateBytesPerSec : 0;
        return new Decision(bytes, seconds, reason);
    }
}

/// <summary>Why a ranged byte fetch failed, in source-neutral terms. Hosts map this onto their own failure vocabulary.</summary>
public enum StreamFailureReason
{
    /// <summary>No failure.</summary>
    None = 0,
    /// <summary>Transport-level: DNS, connect, reset, timeout, or a transient 5xx that outlived the budget.</summary>
    Network,
    /// <summary>The origin refused the request (401 / 403 / 404 / 416).</summary>
    Restricted,
    /// <summary>The response violated the ranged-HTTP contract (bad <c>Content-Range</c>, missing length, size change).</summary>
    ProtocolFault,
}

/// <summary>Where a network-recovery lifecycle currently is.</summary>
public enum AudioNetworkRecoveryStage
{
    /// <summary>Recovery became user-visible (a fetch outlived the visibility delay, or failed outright).</summary>
    Started,
    /// <summary>One attempt failed; another follows.</summary>
    Attempt,
    /// <summary>A later attempt succeeded.</summary>
    Recovered,
    /// <summary>The budget ran out — the fetch fails with <see cref="AudioRangeFetchException"/>.</summary>
    Exhausted,
    /// <summary>The caller cancelled while recovery was in flight.</summary>
    Cancelled,
}

/// <summary>One observation of the network-recovery lifecycle of a single ranged fetch.</summary>
/// <param name="Stage">Lifecycle stage.</param>
/// <param name="SourceId">The source's name (a file id, a url — whatever the caller named it).</param>
/// <param name="Host">The origin host being talked to.</param>
/// <param name="RangeStart">First byte requested, inclusive.</param>
/// <param name="RangeEnd">Last byte requested, exclusive.</param>
/// <param name="Attempt">1-based attempt number.</param>
/// <param name="ElapsedMs">Milliseconds since the fetch began.</param>
/// <param name="Error">The failure that triggered this observation, if any.</param>
public readonly record struct AudioNetworkRecoveryEvent(
    AudioNetworkRecoveryStage Stage,
    string SourceId,
    string Host,
    long RangeStart,
    long RangeEnd,
    int Attempt,
    long ElapsedMs,
    Exception? Error = null);

/// <summary>A ranged fetch that exhausted its whole recovery budget.</summary>
public sealed class AudioRangeFetchException : IOException
{
    /// <summary>Source-neutral failure reason (always <see cref="StreamFailureReason.Network"/> today).</summary>
    public StreamFailureReason Reason { get; }
    /// <summary>The source's name.</summary>
    public string SourceId { get; }
    /// <summary>First byte requested, inclusive.</summary>
    public long RangeStart { get; }
    /// <summary>Last byte requested, exclusive.</summary>
    public long RangeEnd { get; }
    /// <summary>How many attempts were made.</summary>
    public int Attempts { get; }
    /// <summary>Total milliseconds spent before giving up.</summary>
    public long ElapsedMs { get; }

    /// <summary>Create the terminal fetch failure.</summary>
    public AudioRangeFetchException(StreamFailureReason reason, string sourceId, long rangeStart, long rangeEnd,
        int attempts, long elapsedMs, Exception? inner)
        : base($"audio range recovery exhausted after {elapsedMs}ms ({attempts} attempts): {inner?.Message}", inner)
    {
        Reason = reason;
        SourceId = sourceId;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        Attempts = attempts;
        ElapsedMs = elapsedMs;
    }
}

/// <summary>A failure that retrying cannot fix: the origin refused the request or broke the ranged-HTTP contract.</summary>
public sealed class CdnPermanentException : IOException
{
    /// <summary>Source-neutral failure reason.</summary>
    public StreamFailureReason Reason { get; }

    /// <summary>Create a permanent failure.</summary>
    public CdnPermanentException(string message, StreamFailureReason reason = StreamFailureReason.ProtocolFault)
        : base(message) => Reason = reason;
}

/// <summary>How hard a <see cref="RangedHttpSource"/> tries before a foreground fetch fails.</summary>
/// <param name="VisibilityMs">How long a fetch may run before recovery is announced to the caller.</param>
/// <param name="BudgetMs">Total wall-clock budget across every attempt.</param>
/// <param name="AttemptTimeoutMs">Per-attempt timeout.</param>
/// <param name="Jitter">Optional deterministic jitter hook (tests pass <c>_ =&gt; 0</c>); null = random ±20%.</param>
public sealed record RangedHttpRecoveryPolicy(
    int VisibilityMs = 500,
    int BudgetMs = 90_000,
    int AttemptTimeoutMs = 8_000,
    Func<int, int>? Jitter = null)
{
    /// <summary>The shipping policy: announce after 500 ms, give up after 90 s, 8 s per attempt.</summary>
    public static RangedHttpRecoveryPolicy Default { get; } = new();
}

/// <summary>
/// Decrypt-agnostic ranged-HTTP byte source: HTTP Range GETs with mirror failover, a background read-ahead, and a
/// buffered raw-chunk store tracked by a <see cref="RangeSet"/>. It stores RAW (untransformed) bytes only — any decrypt
/// transform is applied by the CALLER on copy-out (see <see cref="ReadRaw"/>), which is what keeps range re-reads and
/// clean-span reuse correct. Knows nothing about clear heads, decrypt, or <see cref="Stream"/>.
/// </summary>
public sealed class RangedHttpSource : IDisposable
{
    const int MinFetchBytes = 64 * 1024;

    // This stream's share of the read-ahead memory budget: the active stream and a gapless-prepared next track share
    // one modest total, so growing toward whole-track prefetch on a fast connection never lets the pair balloon.
    const long TotalReadAheadMemoryCapBytes = 24L * 1024 * 1024;
    const long PerStreamMemoryCapBytes = TotalReadAheadMemoryCapBytes / 2;

    // A rolling throughput sample rolls over (and feeds a fresh window decision) once it has accumulated at least this
    // much wall-clock time across fetches — short enough to react to a connection change, long enough that one small
    // fetch's latency spike doesn't whipsaw the window.
    const long ThroughputWindowMs = 2_000;

    /// <summary>The chunk granularity of the in-memory raw store — the <see cref="ChunkDiskCache"/> chunk size.</summary>
    public const int CdnChunkBytes = ChunkDiskCache.ChunkBytes;

    readonly HttpClient _http;
    readonly string _name;
    readonly StreamLogger _log;
    readonly int _headFloor;                 // read-ahead never dips below this (the caller's clear-head length)
    readonly Action? _onRangeAvailable;      // wake the caller's readers after a fetch / resume (caller pulses its gate)
    readonly Action<AudioNetworkRecoveryEvent>? _onRecovery;
    readonly bool _requireRange;             // false = tolerate a 200 (server ignored Range) by buffering the whole body
    readonly int _maxRetries;                // per-mirror attempts for transient 5xx / network faults
    readonly int _baseBackoffMs;             // exponential backoff base: _baseBackoffMs << attempt
    readonly RangeSet _ranges = new();
    readonly SemaphoreSlim _fetchGate = new(2, 2);
    readonly CancellationTokenSource _disposeCts = new();
    readonly object _sizeGate = new();
    readonly object _dataGate = new();
    readonly Dictionary<int, byte[]> _cdnChunks = new();
    readonly ChunkDiskCache? _disk;
    readonly RangedHttpRecoveryPolicy _recoveryPolicy;
    int _foregroundFetches;
    int _mirrorCursor = -1;

    string[] _cdnUrls = [];
    long _size;
    long _readAheadOffset;
    int _readAheadPauseCount;
    int _readAheadResourcesDisposed;
    volatile bool _stopped;
    Task? _readAheadTask;

    // Bandwidth-adaptive read-ahead window (§3): starts at the old fixed baseline (byte-identical behaviour until
    // ConfigureReadAhead is ever called), then adapts from measured throughput + the caller's bitrate/metered hints.
    int _readAheadWindowBytes = ReadAheadPolicy.FloorBytes;
    int _bitrateBitsPerSec;
    volatile bool _metered;
    readonly object _throughputGate = new();
    long _throughputWindowBytes;
    long _throughputWindowMs;
    long _lastMeasuredBytesPerSec;

    // Non-blocking prefetch for SpotifyAudioStream.TryRead (§1): at most one in-flight kick at a time so a decode
    // thread polling every few ms never floods Task.Run.
    int _asyncPrefetchInFlight;

    // Per-range tracing is gated on the sink's Trace level (never an environment switch): at the default Info level
    // nothing is emitted, and turning the host's logger down to Trace turns the whole range ledger on.
    bool RangeTrace => _log.IsEnabled(StreamLogLevel.Trace);

    /// <summary>Create a source. Call <see cref="Configure"/> before any fetch.</summary>
    /// <param name="http">The client every range GET goes through (pooling/timeouts are the caller's).</param>
    /// <param name="name">Diagnostic name AND the disk-cache key.</param>
    /// <param name="log">Optional logger; <c>default</c> is a no-op.</param>
    /// <param name="headFloor">Read-ahead never dips below this offset (the caller's clear-head length).</param>
    /// <param name="onRangeAvailable">Pulsed after every successful fetch so the caller can wake blocked readers.</param>
    /// <param name="requireRange">False tolerates a 200 (server ignored Range) by buffering the whole body once.</param>
    /// <param name="maxRetries">Per-mirror attempts for transient 5xx / network faults.</param>
    /// <param name="baseBackoffMs">Exponential backoff base for those retries.</param>
    /// <param name="disk">Optional sparse chunk cache consulted before, and filled after, every fetch.</param>
    /// <param name="onRecovery">Optional network-recovery telemetry sink.</param>
    /// <param name="recoveryPolicy">Foreground recovery budget; null = <see cref="RangedHttpRecoveryPolicy.Default"/>.</param>
    public RangedHttpSource(HttpClient http, string name, StreamLogger log, int headFloor,
        Action? onRangeAvailable, bool requireRange = true, int maxRetries = 1, int baseBackoffMs = 150,
        ChunkDiskCache? disk = null, Action<AudioNetworkRecoveryEvent>? onRecovery = null,
        RangedHttpRecoveryPolicy? recoveryPolicy = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _name = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        _log = log;
        _headFloor = Math.Max(0, headFloor);
        _onRangeAvailable = onRangeAvailable;
        _onRecovery = onRecovery;
        _requireRange = requireRange;
        _maxRetries = Math.Max(1, maxRetries);
        _baseBackoffMs = Math.Max(0, baseBackoffMs);
        _disk = disk;
        _recoveryPolicy = recoveryPolicy ?? RangedHttpRecoveryPolicy.Default;
    }

    /// <summary>The total size once known (from a <c>Content-Range</c>, a buffered 200, or the disk cache); 0 until then.</summary>
    public long KnownSize => Volatile.Read(ref _size);

    /// <summary>True when every byte of <c>[start, end)</c> is buffered.</summary>
    public bool ContainsRange(long start, long end) => _ranges.ContainsRange(start, end);

    /// <summary>The contiguous buffered run starting at <paramref name="start"/>, or 0.</summary>
    public long ContainedLengthFrom(long start) => _ranges.ContainedLengthFrom(start);

    /// <summary>Set the mirror list (+ optional known size). Called once before <see cref="StartReadAhead"/>.</summary>
    public void Configure(string[] cdnUrls, long? knownSize)
    {
        var urls = cdnUrls.Where(static u => !string.IsNullOrWhiteSpace(u)).ToArray();
        if (urls.Length == 0) throw new InvalidOperationException("no CDN urls");
        _cdnUrls = urls;
        if (knownSize is not > 0)
        {
            var diskSize = _disk?.KnownSize(_name);
            if (diskSize is > 0) knownSize = diskSize;
        }
        if (knownSize is > 0) SetSize(knownSize.Value);
    }

    /// <summary>Eager priming for the non-lazy attach path: first 64 KiB + the head-boundary window.</summary>
    public async Task PrimeAsync(CancellationToken ct)
    {
        var size = Volatile.Read(ref _size);
        var initialEnd = Math.Min(size > 0 ? size : MinFetchBytes, MinFetchBytes);
        await FetchRangeWithRecoveryAsync(0, initialEnd, ct).ConfigureAwait(false);
        await PrefetchHeadBoundaryAsync(ct, recover: true).ConfigureAwait(false);
    }

    /// <summary>Stop read-ahead (the caller failed). Idempotent; the loop exits at its next check.</summary>
    public void Stop()
    {
        _stopped = true;
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Start (or restart) the background read-ahead loop. Runs as a dedicated <see cref="TaskCreationOptions.LongRunning"/>
    /// task rather than <c>Task.Run</c>: a plain queued task waits its turn behind whatever else the app's ThreadPool is
    /// running (hydration sweeps, HTTP fan-out) — under load the pool's hill-climbing injection adds new worker threads
    /// at roughly one per second, so a saturated pool can delay this pump's very first tick by seconds. LongRunning asks
    /// the scheduler for its own thread immediately, so audio byte supply never queues behind unrelated app work.</summary>
    public void StartReadAhead()
    {
        if (_stopped || _disposeCts.IsCancellationRequested) return;
        if (_readAheadTask is { IsCompleted: false }) return;
        _readAheadTask = Task.Factory.StartNew(ReadAheadLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Bandwidth-adaptive read-ahead window (§3, pinned signature). Feeds the track's bitrate and the
    /// connection's metered/roaming/over-limit state into <see cref="ReadAheadPolicy"/> alongside the throughput this
    /// source has already measured from its own range fetches (no new I/O). Safe to call at any time, including before
    /// the first byte is fetched (falls back to a high-quality-Ogg assumption) and repeatedly as the network cost
    /// changes. Never below <see cref="ReadAheadPolicy.FloorBytes"/> — today's fixed baseline is the floor, not the
    /// ceiling.</summary>
    public void ConfigureReadAhead(int bitrateBitsPerSec, bool metered)
    {
        Volatile.Write(ref _bitrateBitsPerSec, Math.Max(0, bitrateBitsPerSec));
        _metered = metered;
        RecomputeReadAheadWindow(Volatile.Read(ref _lastMeasuredBytesPerSec), "configure");
    }

    void RecomputeReadAheadWindow(long measuredBytesPerSec, string trigger)
    {
        var decision = ReadAheadPolicy.Compute(measuredBytesPerSec, Volatile.Read(ref _bitrateBitsPerSec), _metered,
            PerStreamMemoryCapBytes);
        Volatile.Write(ref _readAheadWindowBytes, decision.WindowBytes);
        // Always-on, one line per decision — never gated on an env var (house rule): measured throughput, the chosen
        // window in both units, and why.
        _log.Info($"stream {_name}: read-ahead window trigger={trigger} throughputBps={measuredBytesPerSec} " +
            $"bitrateBps={Volatile.Read(ref _bitrateBitsPerSec)} metered={_metered} windowSeconds={decision.WindowSeconds} " +
            $"windowBytes={decision.WindowBytes} reason={decision.Reason}");
    }

    // Pure arithmetic over fetches this source already made — no new I/O. Rolls over (and republishes the window)
    // once the accumulated window has enough wall-clock time to be a meaningful rate rather than a single fetch's
    // latency spike.
    void RecordThroughputSample(int bytes, long elapsedMs)
    {
        if (bytes <= 0 || elapsedMs <= 0) return;
        long windowBytes, windowMs;
        lock (_throughputGate)
        {
            _throughputWindowBytes += bytes;
            _throughputWindowMs += elapsedMs;
            if (_throughputWindowMs < ThroughputWindowMs) return;
            windowBytes = _throughputWindowBytes;
            windowMs = _throughputWindowMs;
            _throughputWindowBytes = 0;
            _throughputWindowMs = 0;
        }
        long bytesPerSec = windowMs > 0 ? windowBytes * 1000 / windowMs : 0;
        Volatile.Write(ref _lastMeasuredBytesPerSec, bytesPerSec);
        RecomputeReadAheadWindow(bytesPerSec, "throughput");
    }

    /// <summary>Non-blocking prefetch kick for <see cref="Wavee.Sdk.Streams"/> callers that must never do sync I/O on
    /// their calling thread (the engine's decode worker — see <c>SpotifyAudioStream.TryRead</c>). No-op if the range is
    /// already buffered or a prefetch is already in flight (bounded to one at a time so a caller polling every few ms
    /// never floods <see cref="Task.Run"/>). Failures are traced, never thrown — the caller already treated this as a
    /// miss and will simply see the range still missing on its next call.</summary>
    public void RequestAsyncPrefetch(long start, int length)
    {
        if (_stopped || _disposeCts.IsCancellationRequested) return;
        var size = Volatile.Read(ref _size);
        var end = size > 0 ? Math.Min(size, start + length) : start + length;
        if (start >= end) return;
        if (_ranges.ContainsRange(start, end)) return;
        if (Interlocked.CompareExchange(ref _asyncPrefetchInFlight, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await FetchRangeWithRecoveryAsync(start, end, _disposeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (RangeTrace) TraceLine($"stream {_name}: async prefetch failed range=[{start},{end}): {ex.GetType().Name}: {ex.Message}");
            }
            finally { Interlocked.Exchange(ref _asyncPrefetchInFlight, 0); }
        });
    }

    /// <summary>Tell the read-ahead where the reader is now.</summary>
    public void MarkProgress(long offset)
    {
        if (Volatile.Read(ref _readAheadPauseCount) > 0) return;
        Volatile.Write(ref _readAheadOffset, Math.Max(0, offset));
        StartReadAhead();
    }

    /// <summary>Suspend read-ahead until the returned lease is disposed (nestable).</summary>
    public IDisposable PauseReadAhead()
    {
        Interlocked.Increment(ref _readAheadPauseCount);
        return new ReadAheadPause(this);
    }

    /// <summary>Resume read-ahead from an explicit offset (after a seek).</summary>
    public void ResumeReadAheadAt(long offset)
    {
        Volatile.Write(ref _readAheadOffset, Math.Max(offset, _headFloor));
        _onRangeAvailable?.Invoke();
        StartReadAhead();
    }

    void ReleaseReadAheadPause()
    {
        if (Interlocked.Decrement(ref _readAheadPauseCount) < 0)
            Interlocked.Exchange(ref _readAheadPauseCount, 0);
        _onRangeAvailable?.Invoke();
    }

    async Task PrefetchHeadBoundaryAsync(CancellationToken ct, bool recover = false)
    {
        var size = Volatile.Read(ref _size);
        if (_headFloor <= 0 || (size > 0 && _headFloor >= size)) return;

        var window = Volatile.Read(ref _readAheadWindowBytes);
        var start = _headFloor;
        var end = size > 0
            ? Math.Min(size, start + window)
            : start + window;
        if (_ranges.ContainsRange(start, end)) return;

        var sw = Stopwatch.StartNew();
        if (RangeTrace) TraceLine($"stream {_name}: prefetch boundary start range=[{start},{end})");
        if (recover) await FetchRangeWithRecoveryAsync(start, end, ct).ConfigureAwait(false);
        else await FetchRangeAsync(start, end, ct).ConfigureAwait(false);
        if (RangeTrace) TraceLine($"stream {_name}: prefetch boundary ok bytes={end - start} elapsed={sw.ElapsedMilliseconds}ms");
    }

    async Task ReadAheadLoopAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                if (_stopped) break;
                var size = Volatile.Read(ref _size);

                if (Volatile.Read(ref _foregroundFetches) > 0)
                {
                    await Task.Delay(50, _disposeCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (Volatile.Read(ref _readAheadPauseCount) > 0)
                {
                    await Task.Delay(50, _disposeCts.Token).ConfigureAwait(false);
                    continue;
                }

                var window = Volatile.Read(ref _readAheadWindowBytes);
                var start = Math.Max(Volatile.Read(ref _readAheadOffset), _headFloor);
                if (size > 0 && start >= size) break;
                var end = size > 0 ? Math.Min(size, start + window) : start + window;
                if (!_ranges.ContainsRange(start, end))
                    await FetchRangeAsync(start, end, _disposeCts.Token).ConfigureAwait(false);

                await Task.Delay(100, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                try { await Task.Delay(250, _disposeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Blocking: ensure <c>[start, start+length)</c> is buffered, fetching synchronously on a miss. Throws on
    /// unrecoverable fetch failure (the caller's read path surfaces it exactly as before).</summary>
    public void EnsureRange(long start, int length)
    {
        var size = Volatile.Read(ref _size);
        var end = size > 0 ? Math.Min(size, start + length) : start + length;
        if (start >= end) return;
        if (_ranges.ContainsRange(start, end)) return;
        var sw = Stopwatch.StartNew();
        if (RangeTrace) TraceLine($"stream {_name}: decode range miss range=[{start},{end}) requested={length}B");
        FetchRangeWithRecoveryAsync(start, end, _disposeCts.Token).GetAwaiter().GetResult();
        if (RangeTrace) TraceLine($"stream {_name}: decode range ready range=[{start},{end}) elapsed={sw.ElapsedMilliseconds}ms");
    }

    void TraceLine(string message) => _log.Log(StreamLogLevel.Trace, message);

    async Task FetchRangeWithRecoveryAsync(long start, long end, CancellationToken ct)
    {
        Interlocked.Increment(ref _foregroundFetches);
        try { await FetchRangeWithRecoveryCoreAsync(start, end, ct).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _foregroundFetches); }
    }

    async Task FetchRangeWithRecoveryCoreAsync(long start, long end, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int round = 0;
        bool announced = false;
        Exception? last = null;
        string host = FirstHost();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_stopped) throw new IOException("ranged source stopped");
            var remaining = _recoveryPolicy.BudgetMs - (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            if (remaining <= 0) break;

            round++;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            attemptCts.CancelAfter(Math.Min(_recoveryPolicy.AttemptTimeoutMs, remaining));
            var fetch = FetchRangeAsync(start, end, attemptCts.Token);

            if (!announced)
            {
                var visible = Task.Delay(Math.Min(_recoveryPolicy.VisibilityMs, remaining), ct);
                if (await Task.WhenAny(fetch, visible).ConfigureAwait(false) == visible && !fetch.IsCompleted)
                {
                    announced = true;
                    PublishRecovery(AudioNetworkRecoveryStage.Started, host, start, end, round, sw.ElapsedMilliseconds, null);
                }
            }

            try
            {
                await fetch.ConfigureAwait(false);
                if (announced)
                    PublishRecovery(AudioNetworkRecoveryStage.Recovered, host, start, end, round, sw.ElapsedMilliseconds, null);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposeCts.IsCancellationRequested)
            {
                if (announced)
                    PublishRecovery(AudioNetworkRecoveryStage.Cancelled, host, start, end, round, sw.ElapsedMilliseconds, null);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
                if (!announced)
                {
                    announced = true;
                    PublishRecovery(AudioNetworkRecoveryStage.Started, host, start, end, round, sw.ElapsedMilliseconds, ex);
                }
                PublishRecovery(AudioNetworkRecoveryStage.Attempt, host, start, end, round, sw.ElapsedMilliseconds, ex);
                if (ex is CdnPermanentException) throw;
            }

            remaining = _recoveryPolicy.BudgetMs - (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            if (remaining <= 0) break;
            int ladder = round switch { 1 => 250, 2 => 500, 3 => 1000, 4 => 2000, 5 => 4000, _ => 5000 };
            int jitter = _recoveryPolicy.Jitter?.Invoke(ladder)
                ?? Random.Shared.Next(-(ladder / 5), ladder / 5 + 1);
            await Task.Delay(Math.Min(remaining, Math.Max(0, ladder + jitter)), ct).ConfigureAwait(false);
        }

        var terminal = new AudioRangeFetchException(StreamFailureReason.Network, _name, start, end,
            round, sw.ElapsedMilliseconds, last);
        PublishRecovery(AudioNetworkRecoveryStage.Exhausted, host, start, end, round, sw.ElapsedMilliseconds, terminal);
        throw terminal;
    }

    string FirstHost()
    {
        foreach (var raw in _cdnUrls)
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return uri.Host;
        return "unknown";
    }

    void PublishRecovery(AudioNetworkRecoveryStage stage, string host, long start, long end, int attempt,
        long elapsedMs, Exception? error)
    {
        try { _onRecovery?.Invoke(new(stage, _name, host, start, end, attempt, elapsedMs, error)); } catch { }
        if (stage is AudioNetworkRecoveryStage.Started or AudioNetworkRecoveryStage.Recovered
            or AudioNetworkRecoveryStage.Exhausted or AudioNetworkRecoveryStage.Cancelled)
            _log.Info($"audio.network_recovery.{stage.ToString().ToLowerInvariant()} source={_name} host={host} range=[{start},{end}) attempt={attempt} elapsed={elapsedMs}ms error={error?.GetType().Name}: {error?.Message}");
        else if (RangeTrace)
            TraceLine($"audio.network_recovery.attempt source={_name} host={host} range=[{start},{end}) attempt={attempt} elapsed={elapsedMs}ms error={error?.GetType().Name}: {error?.Message}");
    }

    async Task FetchRangeAsync(long start, long end, CancellationToken ct)
    {
        if (_stopped) throw new IOException("ranged source stopped");
        if (_disposeCts.IsCancellationRequested) throw new ObjectDisposedException(nameof(RangedHttpSource));
        start = Math.Max(0, start);
        var size = Volatile.Read(ref _size);
        if (size > 0) end = Math.Min(end, size);
        if (start >= end) return;

        var gaps = _ranges.GetGaps(start, end);
        if (gaps.Count == 0) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        await _fetchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            LoadCachedChunks(start, end);
            while ((gaps = _ranges.GetGaps(start, end)).Count > 0)
            {
                var gap = gaps[0];
                size = Volatile.Read(ref _size);
                var fetchStart = gap.Start;
                var fetchEnd = Math.Max(gap.End, gap.Start + MinFetchBytes);
                if (size > 0) fetchEnd = Math.Min(fetchEnd, size);
                if (fetchStart >= fetchEnd) continue;
                if (_ranges.ContainsRange(fetchStart, gap.End)) continue;
                await FetchChunkWithMirrorsAsync(fetchStart, fetchEnd, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    async Task FetchChunkWithMirrorsAsync(long start, long end, CancellationToken ct)
    {
        Exception? last = null;
        var urls = _cdnUrls;
        int firstMirror = urls.Length == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _mirrorCursor) % (uint)urls.Length);
        var sw = Stopwatch.StartNew();
        if (RangeTrace) TraceLine($"stream {_name}: range fetch start range=[{start},{end}) bytes={end - start}");

        for (int mirrorOffset = 0; mirrorOffset < urls.Length; mirrorOffset++)
        {
            var url = urls[(firstMirror + mirrorOffset) % urls.Length];
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(start, end - 1);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        if (_requireRange) { last = new CdnPermanentException("CDN ignored Range request"); break; }
                        // Range-optional (plain-HTTP server ignored Range): stream the whole body once and serve all reads from it.
                        // BufferFullBodyAsync wakes _onRangeAvailable itself, before queuing the disk write-through.
                        await BufferFullBodyAsync(resp, ct).ConfigureAwait(false);
                        if (RangeTrace) TraceLine($"stream {_name}: full-body fetch ok (range ignored) size={Volatile.Read(ref _size)} elapsed={sw.ElapsedMilliseconds}ms");
                        return;
                    }
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        bool transientStatus = resp.StatusCode is HttpStatusCode.RequestTimeout
                            or HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                        var statusReason = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            or HttpStatusCode.NotFound or HttpStatusCode.RequestedRangeNotSatisfiable
                            ? StreamFailureReason.Restricted
                            : StreamFailureReason.ProtocolFault;
                        last = transientStatus
                            ? new HttpRequestException($"CDN {(int)resp.StatusCode}")
                            : new CdnPermanentException($"CDN {(int)resp.StatusCode}", statusReason);
                        if (transientStatus && attempt + 1 < _maxRetries)
                        {
                            await Task.Delay(_baseBackoffMs << Math.Min(attempt, 5), ct).ConfigureAwait(false);   // transient 5xx: retry same mirror
                            continue;
                        }
                        break;   // 4xx / exhausted → next mirror
                    }

                    var contentRange = resp.Content.Headers.ContentRange;
                    if (contentRange?.From is long from && from != start)
                        throw new CdnPermanentException($"CDN returned unexpected range start {from}, expected {start}");
                    long total = contentRange?.Length ?? Volatile.Read(ref _size);
                    if (total <= 0) throw new CdnPermanentException("CDN range response missing total length");
                    SetSize(total);
                    long requestedEnd = Math.Min(end, total);
                    long expectedEnd = contentRange?.To is long to ? to + 1 : requestedEnd;
                    if (expectedEnd <= start || expectedEnd > requestedEnd)
                        throw new CdnPermanentException($"CDN returned unexpected range end {expectedEnd}, requested through {requestedEnd}");

                    var expectedBytes = checked((int)(expectedEnd - start));
                    var buf = ArrayPool<byte>.Shared.Rent(expectedBytes);
                    try
                    {
                        var read = 0;
                        var bodySw = Stopwatch.StartNew();
                        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        while (read < expectedBytes)
                        {
                            var n = await body.ReadAsync(buf.AsMemory(read, expectedBytes - read), ct).ConfigureAwait(false);
                            if (n <= 0) break;
                            read += n;
                        }
                        bodySw.Stop();
                        if (read <= 0)
                            throw new IOException($"CDN returned no bytes for range [{start},{expectedEnd})");
                        if (contentRange?.To is not null && read != expectedBytes)
                            throw new IOException($"CDN returned {read} bytes for range [{start},{expectedEnd}), expected {expectedBytes}");

                        WriteCdnBytes(start, buf, read);
                        _ranges.AddRange(start, start + read);
                        // Wake the caller's blocked readers FIRST — the disk-cache write-through (item 5) is queued to a
                        // background writer inside FlushCompletedChunks/ChunkDiskCache and must never sit on this path.
                        _onRangeAvailable?.Invoke();
                        FlushCompletedChunks(start, start + read);
                        RecordThroughputSample(read, bodySw.ElapsedMilliseconds);
                        if (RangeTrace) TraceLine($"stream {_name}: range fetch ok range=[{start},{start + read}) bytes={read} elapsed={sw.ElapsedMilliseconds}ms");
                        return;
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    last = ex;
                    if (ex is CdnPermanentException) break;
                    if (attempt + 1 >= _maxRetries) break;   // exhausted this mirror's retries → next mirror
                    await Task.Delay(_baseBackoffMs << Math.Min(attempt, 5), ct).ConfigureAwait(false);   // transient network fault: backoff + retry (ct cancels)
                }
            }
        }

        if (RangeTrace)
            TraceLine($"stream {_name}: range fetch failed range=[{start},{end}) elapsed={sw.ElapsedMilliseconds}ms error={last?.GetType().Name}: {last?.Message}");
        throw last ?? new IOException($"all CDN mirrors failed for range [{start},{end})");
    }

    /// <summary>Range-optional path: the server ignored our Range and returned 200 with the whole file. Streams the
    /// body straight into the chunk store as it arrives (a pooled read buffer, no whole-body <see cref="MemoryStream"/>
    /// / <c>ToArray</c> — the podcast 200-fallback previously buffered an entire episode body in managed memory before
    /// this ever reached the store) and records the size once EOF is known, so every subsequent read is satisfied from
    /// the store.</summary>
    async Task BufferFullBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(CdnChunkBytes);
        long total = 0;
        try
        {
            int n;
            while ((n = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                WriteCdnBytes(total, buffer, n);
                total += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        if (total <= 0) throw new IOException("plain-HTTP server returned an empty body");
        var len = checked((int)total);
        SetSize(len);
        _ranges.AddRange(0, len);
        // Wake blocked readers before queuing the (now background) disk write-through — item 5's ordering.
        _onRangeAvailable?.Invoke();
        FlushCompletedChunks(0, len);
    }

    /// <summary>Copy buffered RAW (untransformed) bytes into <paramref name="destination"/>. The caller applies any
    /// decrypt transform afterwards. Throws if the range is not buffered.</summary>
    public void ReadRaw(long start, byte[] destination, int destinationOffset, int count) =>
        ReadRawCore(start, destination.AsSpan(destinationOffset), count);

    /// <summary>Span overload of <see cref="ReadRaw(long,byte[],int,int)"/> — lets a non-blocking caller
    /// (<c>SpotifyAudioStream.TryRead</c>) copy out without an intermediate array allocation.</summary>
    public void ReadRaw(long start, Span<byte> destination, int count) => ReadRawCore(start, destination, count);

    void ReadRawCore(long start, Span<byte> destination, int count)
    {
        lock (_dataGate)
        {
            int dst = 0;
            long pos = start;
            int remaining = count;
            while (remaining > 0)
            {
                int chunkIndex = (int)(pos / CdnChunkBytes);
                int chunkOffset = (int)(pos % CdnChunkBytes);
                int n = Math.Min(remaining, CdnChunkBytes - chunkOffset);
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk))
                    throw new IOException($"CDN range [{start},{start + count}) is not buffered");
                chunk.AsSpan(chunkOffset, n).CopyTo(destination.Slice(dst, n));
                dst += n;
                pos += n;
                remaining -= n;
            }
        }
    }

    void LoadCachedChunks(long start, long end)
    {
        if (_disk is null) return;
        int first = (int)(start / CdnChunkBytes);
        int last = (int)((end - 1) / CdnChunkBytes);
        for (int ci = first; ci <= last; ci++)
        {
            var buf = new byte[CdnChunkBytes];
            if (!_disk.TryReadChunk(_name, ci, buf, out int len) || len <= 0) continue;
            long cs = (long)ci * CdnChunkBytes;
            WriteCdnBytes(cs, buf, len);
            _ranges.AddRange(cs, cs + len);
        }
    }

    void WriteCdnBytes(long start, byte[] source, int count)
    {
        lock (_dataGate)
        {
            int src = 0;
            long pos = start;
            while (src < count)
            {
                int chunkIndex = (int)(pos / CdnChunkBytes);
                int chunkOffset = (int)(pos % CdnChunkBytes);
                int n = Math.Min(count - src, CdnChunkBytes - chunkOffset);
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk))
                {
                    chunk = new byte[CdnChunkBytes];
                    _cdnChunks[chunkIndex] = chunk;
                }
                Buffer.BlockCopy(source, src, chunk, chunkOffset, n);
                src += n;
                pos += n;
            }
        }
    }

    void FlushCompletedChunks(long start, long end)
    {
        if (_disk is null) return;
        var size = Volatile.Read(ref _size);
        if (size <= 0 || start >= end) return;
        int first = (int)(start / CdnChunkBytes);
        int last = (int)((end - 1) / CdnChunkBytes);
        for (int chunkIndex = first; chunkIndex <= last; chunkIndex++)
        {
            long cs = (long)chunkIndex * CdnChunkBytes;
            if (cs >= size) return;
            long ce = Math.Min(size, cs + CdnChunkBytes);
            if (!_ranges.ContainsRange(cs, ce)) continue;
            int len = checked((int)(ce - cs));

            // No per-flush ToArray snapshot: ChunkDiskCache.WriteChunk takes the copy it needs (into its own pooled
            // buffer for the background writer) synchronously, so handing it a span of the live chunk under _dataGate
            // is enough — item 4.
            lock (_dataGate)
            {
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk)) continue;
                _disk.WriteChunk(_name, chunkIndex, chunk.AsSpan(0, len));
            }
        }
    }

    void SetSize(long size)
    {
        lock (_sizeGate)
        {
            if (size <= 0) return;
            if (size > int.MaxValue) throw new NotSupportedException("audio files larger than 2GB are not supported");
            if (_size == size) return;
            if (_size > 0 && _size != size)
                throw new CdnPermanentException($"CDN size changed from {_size} to {size}");
            _size = size;
        }
        _disk?.SetSize(_name, size);
    }

    /// <summary>Drop this source's entry from the disk cache (a key change invalidated the stored ciphertext).</summary>
    public void InvalidateDiskCache() => _disk?.Invalidate(_name);

    /// <summary>Stop read-ahead, wait briefly for the loop, and release the fetch resources. Also waits (briefly, and
    /// only here — never on the hot fetch path) for this stream's queued disk write-throughs to land, so a caller that
    /// disposes a stream right after fetching can rely on the bytes being on disk for a subsequent stream over the same
    /// key (item 5 moved the actual flush to <see cref="ChunkDiskCache"/>'s background writer).</summary>
    public void Dispose()
    {
        Stop();
        var readAhead = _readAheadTask;
        if (readAhead is { IsCompleted: false })
        {
            try { readAhead.Wait(250); } catch { }
        }
        _disk?.WaitForPendingWrites();
        DisposeReadAheadResources();
    }

    void DisposeReadAheadResources()
    {
        if (Interlocked.Exchange(ref _readAheadResourcesDisposed, 1) != 0) return;
        _disposeCts.Dispose();
        _fetchGate.Dispose();
    }

    sealed class ReadAheadPause : IDisposable
    {
        RangedHttpSource? _owner;

        public ReadAheadPause(RangedHttpSource owner) => _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseReadAheadPause();
        }
    }
}
