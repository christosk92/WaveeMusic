using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Generic page-level cache with instant serve, stale detection, and periodic background refresh.
/// Subclasses implement <see cref="FetchCoreAsync"/> to fetch their specific data shape.
/// </summary>
public abstract class PageCache<TSnapshot> : IDisposable where TSnapshot : class
{
    private TSnapshot? _cached;
    private DateTimeOffset _lastFetchTime;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private PeriodicTimer? _refreshTimer;
    private CancellationTokenSource? _cts;
    private Task? _refreshTask;
    private volatile bool _suspended;

    protected readonly ILogger? Logger;

    protected virtual TimeSpan StaleDuration => TimeSpan.FromMinutes(5);
    protected virtual TimeSpan RefreshInterval => TimeSpan.FromMinutes(5);

    public bool HasData => _cached is not null;
    public bool IsStale => DateTimeOffset.UtcNow - _lastFetchTime > StaleDuration;

    /// <summary>Forces the cache to be stale so next access fetches fresh data.</summary>
    public void Invalidate() => _lastFetchTime = DateTimeOffset.MinValue;

    /// <summary>Suspends background refresh (e.g. during active audio playback).</summary>
    public void SuspendRefresh() => _suspended = true;

    /// <summary>Resumes background refresh.</summary>
    public void ResumeRefresh() => _suspended = false;

    /// <summary>
    /// Raised when background refresh completes with new data.
    /// Fired on a background thread — subscribers must dispatch to UI thread.
    /// </summary>
    public event Action<TSnapshot>? DataRefreshed;

    protected PageCache(ILogger? logger = null)
    {
        Logger = logger;
    }

    /// <summary>Returns cached data immediately, or null if cache is empty.</summary>
    public TSnapshot? GetCached() => _cached;

    /// <summary>
    /// Implement in subclass: fetch fresh data from APIs and return a snapshot.
    /// </summary>
    protected abstract Task<TSnapshot> FetchCoreAsync(ISession session, CancellationToken ct);

    /// <summary>
    /// Fetches fresh data, updates cache, returns the snapshot. Thread-safe.
    /// </summary>
    public async Task<TSnapshot> FetchFreshAsync(ISession session, CancellationToken ct = default)
    {
        await _fetchLock.WaitAsync(ct);
        try
        {
            var snapshot = await FetchCoreAsync(session, ct);
            _cached = snapshot;
            _lastFetchTime = DateTimeOffset.UtcNow;
            return snapshot;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>Starts periodic background refresh. Safe to call multiple times.</summary>
    public void StartBackgroundRefresh(ISession session)
    {
        if (_refreshTask != null) return;

        _cts = new CancellationTokenSource();
        _refreshTimer = new PeriodicTimer(RefreshInterval);
        _refreshTask = RunRefreshLoopAsync(session, _cts.Token);
    }

    public void StopBackgroundRefresh()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _refreshTask = null;
    }

    private async Task RunRefreshLoopAsync(ISession session, CancellationToken ct)
    {
        try
        {
            while (await _refreshTimer!.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (_suspended || !session.IsConnected()) continue;

                    Logger?.LogDebug("{CacheType} background refresh starting", GetType().Name);
                    var snapshot = await FetchFreshAsync(session, ct);
                    DataRefreshed?.Invoke(snapshot);
                    Logger?.LogDebug("{CacheType} background refresh complete", GetType().Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger?.LogWarning(ex, "{CacheType} background refresh failed", GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public void Dispose()
    {
        StopBackgroundRefresh();
        _cts?.Dispose();
        _fetchLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
