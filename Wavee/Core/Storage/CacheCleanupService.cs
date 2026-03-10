using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage;

/// <summary>
/// Background service that periodically cleans stale entries from all registered caches.
/// Uses PeriodicTimer pattern (same as HeartbeatManager) since the host is not started.
/// </summary>
public sealed class CacheCleanupService : IAsyncDisposable
{
    private readonly IReadOnlyList<ICleanableCache> _caches;
    private readonly CacheCleanupOptions _options;
    private readonly ILogger? _logger;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _cleanupTask;

    public CacheCleanupService(
        IEnumerable<ICleanableCache> caches,
        CacheCleanupOptions options,
        ILogger<CacheCleanupService>? logger = null)
    {
        _caches = caches.ToList();
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Starts the periodic cleanup loop.
    /// </summary>
    public void Start()
    {
        if (_timer != null)
            throw new InvalidOperationException("Cleanup service already started");

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(_options.CleanupInterval);
        _cleanupTask = RunCleanupLoopAsync(_cts.Token);

        _logger?.LogInformation(
            "Cache cleanup service started. Interval: {Interval}, TTL: {TTL}, Caches: {Count}",
            _options.CleanupInterval, _options.DefaultMaxAge, _caches.Count);
    }

    private async Task RunCleanupLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
            {
                await RunCleanupPassAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Cache cleanup loop cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in cache cleanup loop");
        }
    }

    /// <summary>
    /// Runs a single cleanup pass across all registered caches.
    /// Can be called manually for testing or on-demand cleanup.
    /// </summary>
    public async Task RunCleanupPassAsync(CancellationToken ct = default)
    {
        var totalRemoved = 0;

        foreach (var cache in _caches)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var countBefore = cache.CurrentCount;
                var removed = await cache.CleanupStaleEntriesAsync(_options.DefaultMaxAge, ct);
                totalRemoved += removed;

                if (removed > 0)
                {
                    _logger?.LogInformation(
                        "Cache '{CacheName}': removed {Removed} stale entries ({Before} -> {After})",
                        cache.CacheName, removed, countBefore, cache.CurrentCount);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error cleaning cache '{CacheName}'", cache.CacheName);
            }
        }

        if (totalRemoved > 0)
        {
            _logger?.LogInformation("Cache cleanup pass complete: {Total} entries removed", totalRemoved);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _timer?.Dispose();

        if (_cleanupTask != null)
        {
            try { await _cleanupTask; }
            catch (OperationCanceledException) { }
        }

        _logger?.LogDebug("Cache cleanup service disposed");
    }
}
