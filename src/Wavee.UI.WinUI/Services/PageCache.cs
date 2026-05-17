using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Generic page-level cache with instant serve, stale detection, and periodic background refresh.
/// Subclasses implement <see cref="FetchCoreAsync"/> to fetch their specific data shape and
/// <see cref="IsAvailable"/> to gate background refresh — the cache itself never touches
/// <c>ISession</c> so consumers can stay on the framework-neutral service layer.
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

    /// <summary>
    /// Drops the cached snapshot entirely. Used when the signed-in user changes —
    /// <see cref="Invalidate"/> only ages the timestamp, leaving the previous user's
    /// data accessible via <see cref="GetCached"/> until the next fetch lands.
    /// </summary>
    public void Clear()
    {
        _cached = null;
        _lastFetchTime = DateTimeOffset.MinValue;
    }

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
    /// Subclasses own their own dependencies (e.g. <c>IHomeFeedService</c>).
    /// </summary>
    protected abstract Task<TSnapshot> FetchCoreAsync(CancellationToken ct);

    /// <summary>
    /// Subclass-supplied gate that controls whether the periodic background refresh
    /// should run on a given tick. Replaces the old <c>session.IsConnected()</c> check
    /// without coupling the base class to <c>ISession</c>.
    /// </summary>
    protected abstract bool IsAvailable { get; }

    /// <summary>
    /// Fetches fresh data, updates cache, returns the snapshot. Thread-safe.
    /// </summary>
    public async Task<TSnapshot> FetchFreshAsync(CancellationToken ct = default)
    {
        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await FetchCoreAsync(ct).ConfigureAwait(false);
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
    public void StartBackgroundRefresh()
    {
        if (_refreshTask != null) return;

        _cts = new CancellationTokenSource();
        _refreshTimer = new PeriodicTimer(RefreshInterval);
        _refreshTask = RunRefreshLoopAsync(_cts.Token);
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

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _refreshTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (_suspended || !IsAvailable) continue;

                    Logger?.LogDebug("{CacheType} background refresh starting", GetType().Name);
                    var snapshot = await FetchFreshAsync(ct).ConfigureAwait(false);
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
