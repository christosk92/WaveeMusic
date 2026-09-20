using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

// ── Server-clock skew estimator (NTP-style) ───────────────────────────────────────────────────────────────────────────
// Spotify cluster snapshots carry server-clock timestamps. To age a remote position by the network transit that elapsed
// since the cluster was emitted (NowPlayingProjection's "networkAge" term) we need "server now" in the SAME (server) clock
// domain. We estimate the offset (serverClock − localClock) by probing a server-time endpoint with round-trip correction,
// keeping the LOWEST-RTT sample of a short round, and re-syncing periodically. Until the first usable offset is set,
// ServerNowUnixMs() returns 0 — the "unsynced" sentinel the projection reads to SKIP the offset-dependent term (it still
// applies the always-safe, sync-free server-side delta).
//
// Purely portable: the algorithm depends only on two injected functions — a server-time fetch and a local Unix clock — so
// it lives in the engine-independent Backend layer and is unit-tested directly. The transport-specific fetch (GET
// /melody/v1/time over the authenticated spclient pipeline) is supplied by the SpotifyLive wiring.
//
// Better than a probe-only design: ObservePassive() folds the FREE ServerTimestampMs carried on every cluster as a cheap
// bootstrap-before-first-probe + gross-drift detector. A passive sample is one-way-downlink biased, so it never overrides
// an unbiased probe — it only seeds the offset before the first probe lands, or triggers an early re-probe on drift.
public sealed class SpotifyServerClock : IDisposable
{
    const int SamplesPerRound = 3;
    static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(10);
    const int InitialRetries = 5;                 // quick retries while the transport/token warm up, before the slow cadence
    const long DriftTriggerMs = 2000;             // passive vs probed offset gap that warrants an early re-probe

    readonly Func<CancellationToken, Task<long>> _fetchServerTimeMs;
    readonly Func<long> _localNow;                // local wall clock in Unix ms (injectable for deterministic tests)
    readonly WaveeLogger _log;
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    Task? _loop;

    long _offsetMs;        // serverClock − localClock; add to a local Unix-ms reading to get server time
    bool _synced;          // a usable offset is set (by probe OR passive bootstrap)
    bool _probed;          // an UNBIASED probe has succeeded — passive samples never overwrite this
    long _lastRttMs = long.MaxValue;

    public SpotifyServerClock(Func<CancellationToken, Task<long>> fetchServerTimeMs, WaveeLogger log = default,
        Func<long>? localNowUnixMs = null)
    {
        _fetchServerTimeMs = fetchServerTimeMs;
        _log = log;
        _localNow = localNowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Estimated server-clock "now" in Unix ms, or 0 if not yet synced (the projection's unsynced sentinel).</summary>
    public long ServerNowUnixMs() { lock (_gate) return _synced ? _localNow() + _offsetMs : 0L; }

    public long OffsetMs { get { lock (_gate) return _offsetMs; } }
    public long LastRttMs { get { lock (_gate) return _lastRttMs; } }
    public bool IsSynced { get { lock (_gate) return _synced; } }
    public bool IsProbed { get { lock (_gate) return _probed; } }

    /// <summary>Start the initial probe + periodic re-sync (idempotent).</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Free per-cluster sample: the cluster's server emit-time vs our local receipt. Downlink-biased, so it only
    /// bootstraps the offset before the first probe and triggers an early re-probe on gross drift — never overrides a probe.</summary>
    public void ObservePassive(long serverTimestampMs)
    {
        if (serverTimestampMs <= 0) return;
        long passiveOffset = serverTimestampMs - _localNow();
        bool needEarlyProbe = false;
        lock (_gate)
        {
            if (!_synced) { _offsetMs = passiveOffset; _synced = true; _log.Info($"server-clock bootstrap offset={passiveOffset}ms (passive)"); }
            else if (_probed && Math.Abs(passiveOffset - _offsetMs) > DriftTriggerMs) needEarlyProbe = true;
        }
        if (needEarlyProbe) { _log.Info("server-clock drift detected → early re-probe"); _ = Task.Run(() => SyncAsync(_cts.Token)); }
    }

    /// <summary>One NTP-style round: N samples, keep the lowest-RTT offset (midpoint-corrected). No-op on total failure
    /// (the previous offset is kept).</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        long bestOffset = 0, bestRtt = long.MaxValue;
        for (int i = 0; i < SamplesPerRound; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long t1 = _localNow();
                long serverMs = await _fetchServerTimeMs(ct).ConfigureAwait(false);
                long t2 = _localNow();
                if (serverMs <= 0) continue;
                long rtt = Math.Max(0, t2 - t1);
                long offset = serverMs - (t1 + t2) / 2;          // assume symmetric latency → midpoint is "now"
                if (rtt < bestRtt) { bestRtt = rtt; bestOffset = offset; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Info("server-clock probe failed: " + ex.Message); }
        }
        if (bestRtt < long.MaxValue)
        {
            lock (_gate) { _offsetMs = bestOffset; _lastRttMs = bestRtt; _synced = true; _probed = true; }
            _log.Info($"server-clock synced offset={bestOffset}ms rtt={bestRtt}ms");
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        // Quick initial retries (the dealer/token may still be warming up); meanwhile ObservePassive bootstraps from the
        // first cluster so the safe path is live within seconds regardless.
        for (int attempt = 0; attempt < InitialRetries && !IsProbed; attempt++)
        {
            try { await SyncAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.Info("initial clock sync attempt failed: " + ex.Message); }
            if (IsProbed) break;
            try { await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        using var timer = new PeriodicTimer(ResyncInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await SyncAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _log.Info("periodic clock sync failed (keeping offset): " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _cts.Dispose();
    }
}
