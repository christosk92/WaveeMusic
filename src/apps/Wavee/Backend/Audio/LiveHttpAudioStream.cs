using System.Threading;
using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

/// <summary>Tuning for the live transport. Every value is a POLICY, not a magic number:
/// <list type="bullet">
/// <item><see cref="CapacityBytes"/> — ~3 MiB is ≈ 3 minutes at 128 kbit/s, far more than any decoder backlog; it exists
/// to absorb a scheduling stall, not to build latency (an overrun drops the oldest bytes).</item>
/// <item><see cref="PrefillBytes"/> — the FIRST read waits for this much so the codec's open sees a real header window
/// instead of the 200 bytes that happened to arrive first.</item>
/// <item><see cref="ReadIdleTimeoutMs"/> — a live socket that goes quiet is dead, not slow; TCP alone would take minutes.</item>
/// <item><see cref="BudgetMs"/> — the total window for reconnects before the stream is declared lost.</item>
/// </list></summary>
internal sealed record LiveHttpOptions(
    int CapacityBytes = 3 * 1024 * 1024,
    int PrefillBytes = 24 * 1024,
    int ConnectTimeoutMs = 10_000,
    int ReadIdleTimeoutMs = 15_000,
    int BudgetMs = 60_000,
    int BaseBackoffMs = 500,
    int MaxBackoffMs = 8_000,
    double Jitter = 0.25,
    string UserAgent = IcyHttpConnector.DefaultUserAgent)
{
    public static LiveHttpOptions Default { get; } = new();
}

/// <summary>The ENDLESS-stream byte source: an Icecast/SHOUTcast connection demuxed of its ICY metadata, buffered in a
/// bounded ring, and re-established across socket drops without ever ending the track.
///
/// <para>Why this is not <see cref="PlainHttpAudioStream"/>: that one is built on <see cref="RangedHttpSource"/>, whose
/// "server ignored Range" fallback buffers the WHOLE body into memory — on a stream with no end that is an OOM, not a
/// fallback. Live radio needs the opposite posture: no Range at all, no length, no seek, a bounded window of the most
/// recent bytes, and a drop that reads as "reconnecting" rather than "the track ended".</para>
///
/// <para>Shape at the seam: <see cref="KnownSize"/> is 0 (so the engine sees no length), <see cref="CanSeek"/> is false
/// (which also keeps <c>Mp3GaplessProbe</c> off the stream), and <c>Seek(0, Begin|Current)</c> is a NO-OP purely because
/// <c>SkipStream</c>'s constructor and the device-rate soft reload both issue it — any other seek throws.</para></summary>
internal sealed class LiveHttpAudioStream : Stream, IAsyncDisposable, IAudioReadStream, IAudioNetworkRecoverySource
{
    const int PumpBufferBytes = 32 * 1024;
    const int HeadSniffWaitMs = 3_000;

    readonly LiveRingBuffer _ring;
    readonly LiveHttpOptions _opt;
    readonly LiveHttpConnect _connect;
    readonly WaveeLogger _log;
    readonly CancellationTokenSource _cts = new();
    readonly string _sourceId;
    readonly object _connGate = new();

    LiveHttpResponse _response;
    IcyDemuxer _demux;
    Task? _pump;
    long _pos;
    bool _firstRead = true;
    int _disposed;

    event Action<AudioNetworkRecoveryEvent>? NetworkRecovery;

    event Action<AudioNetworkRecoveryEvent>? IAudioNetworkRecoverySource.NetworkRecovery
    {
        add => NetworkRecovery += value;
        remove => NetworkRecovery -= value;
    }

    /// <summary>Raised (on the producer thread) whenever the station's <c>StreamTitle</c> changes.</summary>
    public event Action<string>? StreamTitleChanged;

    LiveHttpAudioStream(LiveHttpResponse response, LiveHttpOptions options, LiveHttpConnect connect, WaveeLogger log)
    {
        _opt = options;
        _connect = connect;
        _log = log;
        _response = response;
        FinalUrl = response.FinalUrl;
        _sourceId = Uri.TryCreate(response.FinalUrl, UriKind.Absolute, out var final) ? final.Host : response.FinalUrl;
        _ring = new LiveRingBuffer(options.CapacityBytes);
        _demux = AdoptHeaders(response);
    }

    // ── station facts (re-read on every reconnect; the first connection seeds them) ───────────────────────────────────

    public string FinalUrl { get; private set; }
    public string? ContentType { get; private set; }
    public string? StationName { get; private set; }
    public string? Genre { get; private set; }
    public int BitrateKbps { get; private set; }
    public int MetaInt { get; private set; }
    public string? CurrentTitle { get; private set; }

    /// <summary>Bytes the ring had to drop because the decoder fell behind — a non-zero value means an audible splice.</summary>
    public long DroppedBytes => _ring.TotalDropped;

    /// <summary>Open the live stream over a real socket. ONE connect attempt: a first-connect failure is a typed,
    /// immediate failure the host can report, never a silent 60-second retry the user watches spin.</summary>
    public static Task<LiveHttpAudioStream> OpenAsync(string url, WaveeLogger log = default, CancellationToken ct = default)
        => OpenAsync(url, (u, c) => IcyHttpConnector.ConnectAsync(u, c, LiveHttpOptions.Default.UserAgent,
            connectTimeoutMs: LiveHttpOptions.Default.ConnectTimeoutMs), LiveHttpOptions.Default, log, ct);

    /// <summary>The seam overload: the tests script <paramref name="connect"/> to replay heads/bodies/drops.</summary>
    internal static async Task<LiveHttpAudioStream> OpenAsync(string url, LiveHttpConnect connect, LiveHttpOptions options,
        WaveeLogger log = default, CancellationToken ct = default)
    {
        var response = await connect(url, ct).ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300)
        {
            int status = response.StatusCode;
            response.Dispose();
            throw new IOException($"live stream refused: HTTP {status} for {url}");
        }
        var stream = new LiveHttpAudioStream(response, options, connect, log);
        stream.StartPump();
        return stream;
    }

    void StartPump() => _pump = Task.Run(() => PumpAsync(_cts.Token));

    IcyDemuxer AdoptHeaders(LiveHttpResponse response)
    {
        ContentType = response.Header("content-type");
        StationName = response.Header("icy-name") ?? StationName;
        Genre = response.Header("icy-genre") ?? Genre;
        if (int.TryParse(response.Header("icy-br"), out int br) && br > 0) BitrateKbps = br;
        MetaInt = int.TryParse(response.Header("icy-metaint"), out int mi) && mi > 0 ? mi : 0;
        FinalUrl = response.FinalUrl;

        var demux = new IcyDemuxer(MetaInt);
        demux.StreamTitleChanged += OnStreamTitle;
        return demux;
    }

    void OnStreamTitle(string title)
    {
        CurrentTitle = title;
        StreamTitleChanged?.Invoke(title);
    }

    /// <summary>Copy the first buffered bytes WITHOUT consuming them, waiting briefly for the producer to deliver them —
    /// the codec sniff needs real bytes before the session opens, but must never hang the load pump.</summary>
    public int PeekHead(Span<byte> dst)
    {
        if (dst.IsEmpty) return 0;
        _ring.WaitForAvailable(dst.Length, HeadSniffWaitMs);
        return _ring.Peek(dst);
    }

    // ── the producer ─────────────────────────────────────────────────────────────────────────────────────────────────

    async Task PumpAsync(CancellationToken ct)
    {
        var buffer = new byte[PumpBufferBytes];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await ReadBodyAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("live stream body closed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (!await RecoverAsync(ex, ct).ConfigureAwait(false)) return;
                continue;
            }

            IcyDemuxer demux;
            lock (_connGate) demux = _demux;
            demux.Push(buffer.AsSpan(0, n), _ring);
        }
    }

    async Task<int> ReadBodyAsync(byte[] buffer, CancellationToken ct)
    {
        Stream body;
        lock (_connGate) body = _response.Body;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_opt.ReadIdleTimeoutMs);
        try
        {
            return await body.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"live stream idle for {_opt.ReadIdleTimeoutMs}ms");
        }
    }

    /// <summary>Reconnect within the budget. The ring is deliberately NOT cleared: the buffered tail keeps playing while
    /// the socket comes back, and the decoders resync over the splice. Returns false when the stream is finished
    /// (budget exhausted or the session is being torn down).</summary>
    async Task<bool> RecoverAsync(Exception cause, CancellationToken ct)
    {
        long started = Environment.TickCount64;
        int attempt = 0;
        var error = cause;
        Raise(AudioNetworkRecoveryStage.Started, attempt, 0, cause);
        _log.Info($"live stream drop host={_sourceId}: {cause.GetType().Name}: {cause.Message}");

        while (!ct.IsCancellationRequested)
        {
            long elapsed = Environment.TickCount64 - started;
            if (elapsed >= _opt.BudgetMs)
            {
                Raise(AudioNetworkRecoveryStage.Exhausted, attempt, elapsed, error);
                _ring.Complete(new AudioRangeFetchException(StreamFailureReason.Network, _sourceId, 0, 0, attempt, elapsed, error));
                return false;
            }

            try { await Task.Delay(BackoffMs(attempt), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            attempt++;
            elapsed = Environment.TickCount64 - started;
            Raise(AudioNetworkRecoveryStage.Attempt, attempt, elapsed, error);

            LiveHttpResponse? next = null;
            try
            {
                next = await _connect(FinalUrl, ct).ConfigureAwait(false);
                if (next.StatusCode is < 200 or >= 300)
                    throw new IOException($"live reconnect refused: HTTP {next.StatusCode}");

                var old = SwapConnection(next);
                next = null;
                old.Dispose();
                Raise(AudioNetworkRecoveryStage.Recovered, attempt, Environment.TickCount64 - started, null);
                _log.Info($"live stream recovered host={_sourceId} attempt={attempt}");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { next?.Dispose(); return false; }
            catch (Exception ex) { next?.Dispose(); error = ex; }
        }

        Raise(AudioNetworkRecoveryStage.Cancelled, attempt, Environment.TickCount64 - started, error);
        return false;
    }

    LiveHttpResponse SwapConnection(LiveHttpResponse next)
    {
        lock (_connGate)
        {
            var old = _response;
            _demux.StreamTitleChanged -= OnStreamTitle;
            _response = next;
            _demux = AdoptHeaders(next);
            return old;
        }
    }

    int BackoffMs(int attempt)
    {
        double ms = _opt.BaseBackoffMs * Math.Pow(4, Math.Min(attempt, 8));
        ms = Math.Min(ms, _opt.MaxBackoffMs);
        double jitter = 1 + (Random.Shared.NextDouble() * 2 - 1) * Math.Clamp(_opt.Jitter, 0, 1);
        return (int)Math.Clamp(ms * jitter, 1, _opt.MaxBackoffMs);
    }

    void Raise(AudioNetworkRecoveryStage stage, int attempt, long elapsedMs, Exception? error)
        => NetworkRecovery?.Invoke(new AudioNetworkRecoveryEvent(stage, _sourceId, _sourceId, 0, 0, attempt, elapsedMs, error));

    // ── IAudioReadStream ─────────────────────────────────────────────────────────────────────────────────────────────

    public Stream AsStream() => this;
    public long CurrentOffset => Volatile.Read(ref _pos);
    public bool IsBodyAttached => true;
    /// <summary>Always 0 — a live stream has no length, and the engine reads that as "unknown".</summary>
    public long KnownSize => 0;
    public int ClearHeadLength => 0;
    /// <summary>No read-ahead to pause: the producer IS the read-ahead, and pausing it would drop live audio.</summary>
    public IDisposable PauseReadAhead() => NoopScope.Instance;
    public void ResumeReadAheadAtCurrentOffset() { }

    // ── Stream ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        int min = _firstRead ? Math.Max(1, Math.Min(_opt.PrefillBytes, buffer.Length)) : 1;
        int n = _ring.Read(buffer, min);
        _firstRead = false;
        if (n > 0) Volatile.Write(ref _pos, Volatile.Read(ref _pos) + n);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override long Length => throw new NotSupportedException("a live stream has no length");
    public override long Position
    {
        get => Volatile.Read(ref _pos);
        set => throw new NotSupportedException("a live stream cannot be repositioned");
    }

    /// <summary>Accepts ONLY the two no-op forms the pipeline actually issues — <c>SkipStream</c>'s constructor
    /// (<c>Seek(0, Begin)</c> with a zero skip) and a device-rate soft reload's <c>Seek(0, Current)</c>. Anything else is
    /// a genuine reposition, which a live stream cannot honour and must not silently pretend to.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long pos = Volatile.Read(ref _pos);
        if (offset == 0 && (origin == SeekOrigin.Begin || origin == SeekOrigin.Current)) return pos;
        throw new NotSupportedException("a live stream cannot seek");
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Order matters: cancel the producer, then wake the (possibly blocked) reader, then close the socket. The
            // reader is on the feed thread the session teardown is about to join for 500 ms.
            try { _cts.Cancel(); } catch { /* already disposed */ }
            _ring.Dispose();
            lock (_connGate) { try { _response.Dispose(); } catch { /* socket already gone */ } }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var pump = _pump;
        Dispose();
        if (pump is not null) { try { await pump.ConfigureAwait(false); } catch { /* teardown */ } }
        // Only now is the producer provably off the token, so the source can go.
        try { _cts.Dispose(); } catch { /* already disposed */ }
    }

    sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
