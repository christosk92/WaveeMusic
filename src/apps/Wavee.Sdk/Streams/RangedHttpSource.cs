using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wavee.Sdk.Streams;

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
    const int MaxReadAheadBytes = 256 * 1024;

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

    /// <summary>Start (or restart) the background read-ahead loop.</summary>
    public void StartReadAhead()
    {
        if (_stopped || _disposeCts.IsCancellationRequested) return;
        if (_readAheadTask is { IsCompleted: false }) return;
        _readAheadTask = Task.Run(ReadAheadLoopAsync, CancellationToken.None);
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

        var start = _headFloor;
        var end = size > 0
            ? Math.Min(size, start + MaxReadAheadBytes)
            : start + MaxReadAheadBytes;
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

                var start = Math.Max(Volatile.Read(ref _readAheadOffset), _headFloor);
                if (size > 0 && start >= size) break;
                var end = size > 0 ? Math.Min(size, start + MaxReadAheadBytes) : start + MaxReadAheadBytes;
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
                        // Range-optional (plain-HTTP server ignored Range): buffer the whole body once and serve all reads from it.
                        await BufferFullBodyAsync(resp, ct).ConfigureAwait(false);
                        _onRangeAvailable?.Invoke();
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
                    var buf = new byte[expectedBytes];
                    var read = 0;
                    await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    while (read < buf.Length)
                    {
                        var n = await body.ReadAsync(buf.AsMemory(read, buf.Length - read), ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read <= 0)
                        throw new IOException($"CDN returned no bytes for range [{start},{expectedEnd})");
                    if (contentRange?.To is not null && read != expectedBytes)
                        throw new IOException($"CDN returned {read} bytes for range [{start},{expectedEnd}), expected {expectedBytes}");

                    WriteCdnBytes(start, buf, read);
                    _ranges.AddRange(start, start + read);
                    FlushCompletedChunks(start, start + read);
                    _onRangeAvailable?.Invoke();
                    if (RangeTrace) TraceLine($"stream {_name}: range fetch ok range=[{start},{start + read}) bytes={read} elapsed={sw.ElapsedMilliseconds}ms");
                    return;
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

    /// <summary>Range-optional path: the server ignored our Range and returned 200 with the whole file. Buffer it once
    /// from offset 0 and record the size, so every subsequent read is satisfied from the store.</summary>
    async Task BufferFullBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct).ConfigureAwait(false);
        var len = (int)ms.Length;
        if (len <= 0) throw new IOException("plain-HTTP server returned an empty body");
        SetSize(len);
        WriteCdnBytes(0, ms.GetBuffer(), len);
        _ranges.AddRange(0, len);
        FlushCompletedChunks(0, len);
    }

    /// <summary>Copy buffered RAW (untransformed) bytes into <paramref name="destination"/>. The caller applies any
    /// decrypt transform afterwards. Throws if the range is not buffered.</summary>
    public void ReadRaw(long start, byte[] destination, int destinationOffset, int count)
    {
        lock (_dataGate)
        {
            int dst = destinationOffset;
            long pos = start;
            int remaining = count;
            while (remaining > 0)
            {
                int chunkIndex = (int)(pos / CdnChunkBytes);
                int chunkOffset = (int)(pos % CdnChunkBytes);
                int n = Math.Min(remaining, CdnChunkBytes - chunkOffset);
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk))
                    throw new IOException($"CDN range [{start},{start + count}) is not buffered");
                Buffer.BlockCopy(chunk, chunkOffset, destination, dst, n);
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

            byte[] snapshot;
            lock (_dataGate)
            {
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk)) continue;
                snapshot = chunk.AsSpan(0, len).ToArray();
            }
            _disk.WriteChunk(_name, chunkIndex, snapshot);
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

    /// <summary>Stop read-ahead, wait briefly for the loop, and release the fetch resources.</summary>
    public void Dispose()
    {
        Stop();
        var readAhead = _readAheadTask;
        if (readAhead is { IsCompleted: false })
        {
            try { readAhead.Wait(250); } catch { }
        }
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
