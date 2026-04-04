using Microsoft.Extensions.Logging;
using Wavee.Core.Http;

namespace Wavee.Core.Time;

/// <summary>
/// Estimates the offset between the local system clock and Spotify's server clock
/// using the melody time-sync endpoint. Periodically re-syncs to prevent drift.
/// </summary>
/// <remarks>
/// Uses an NTP-style algorithm: for each sample, records local time before and after
/// the request, then estimates offset as serverTime - midpoint(localBefore, localAfter).
/// Takes multiple samples per round and picks the one with the lowest RTT (most accurate).
/// </remarks>
public sealed class SpotifyClockService : IDisposable
{
    private const int SamplesPerRound = 3;

    private readonly ISpClient _spClient;
    private readonly ILogger? _logger;
    private CancellationTokenSource _cts = new();
    private Task _backgroundTask;

    private long _offsetMs;  // serverTime - localTime (add to local to get server time)
    private long _lastRttMs;
    private bool _isSynced;
    private DateTimeOffset _lastSyncUtc;
    private int _syncIntervalMinutes = 10;

    public SpotifyClockService(ISpClient spClient, ILogger? logger = null)
    {
        _spClient = spClient;
        _logger = logger;
        _backgroundTask = RunPeriodicSyncAsync(_cts.Token);
    }

    /// <summary>
    /// Returns the current time in Unix milliseconds, corrected for Spotify server clock offset.
    /// Falls back to local clock if no sync has been performed.
    /// </summary>
    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _offsetMs;

    /// <summary>
    /// Current estimated offset in ms (positive = local clock is behind server).
    /// Add this to local time to approximate server time.
    /// </summary>
    public long OffsetMs => _offsetMs;

    /// <summary>
    /// Round-trip time of the best sample from the last sync round.
    /// </summary>
    public long LastRttMs => _lastRttMs;

    /// <summary>
    /// Whether at least one successful sync has been performed.
    /// </summary>
    public bool IsSynced => _isSynced;

    /// <summary>
    /// When the last successful sync completed (UTC).
    /// </summary>
    public DateTimeOffset LastSyncUtc => _lastSyncUtc;

    /// <summary>
    /// Current sync interval in minutes. Changing this restarts the background timer.
    /// </summary>
    public int SyncIntervalMinutes
    {
        get => _syncIntervalMinutes;
        set
        {
            if (value < 1) value = 1;
            if (value == _syncIntervalMinutes) return;
            _syncIntervalMinutes = value;
            RestartTimer();
        }
    }

    /// <summary>
    /// Performs a single sync round: takes multiple samples and picks the best one.
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        long bestOffset = 0;
        long bestRtt = long.MaxValue;

        for (int i = 0; i < SamplesPerRound; i++)
        {
            try
            {
                var t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var serverTime = await _spClient.GetMelodyTimeAsync(cancellationToken);
                var t2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var rtt = t2 - t1;
                var midpoint = (t1 + t2) / 2;
                var offset = serverTime - midpoint;

                if (rtt < bestRtt)
                {
                    bestRtt = rtt;
                    bestOffset = offset;
                }

                _logger?.LogTrace(
                    "Clock sync sample {Index}: rtt={Rtt}ms, offset={Offset}ms",
                    i, rtt, offset);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Clock sync sample {Index} failed", i);
            }
        }

        if (bestRtt < long.MaxValue)
        {
            var previousOffset = _offsetMs;
            _offsetMs = bestOffset;
            _lastRttMs = bestRtt;
            _isSynced = true;
            _lastSyncUtc = DateTimeOffset.UtcNow;

            _logger?.LogInformation(
                "Clock sync complete: offset={Offset}ms (was {PreviousOffset}ms), bestRtt={Rtt}ms",
                bestOffset, previousOffset, bestRtt);
        }
        else
        {
            _logger?.LogWarning("Clock sync round failed: no successful samples");
        }
    }

    private void RestartTimer()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _backgroundTask = RunPeriodicSyncAsync(_cts.Token);
    }

    private async Task RunPeriodicSyncAsync(CancellationToken cancellationToken)
    {
        // Initial sync immediately (only on first start, not on interval change)
        if (!_isSynced)
        {
            try
            {
                await SyncAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Initial clock sync failed, will retry on next interval");
            }
        }

        // Periodic re-sync
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_syncIntervalMinutes));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await SyncAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Periodic clock sync failed, keeping previous offset");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
