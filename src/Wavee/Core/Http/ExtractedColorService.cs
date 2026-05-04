using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Http;

/// <summary>
/// Extracted color service with 3-tier caching: hot (in-memory) → SQLite → API.
/// Singleton — shared across all pages, persists across navigation.
/// </summary>
public sealed class ExtractedColorService : IColorService, IAsyncDisposable
{
    private const int MaxHotCacheSize = 500;
    private const int DefaultBatchSize = 50;
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromMilliseconds(75);

    private readonly IPathfinderClient _pathfinder;
    private readonly IMetadataDatabase _db;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ExtractedColor> _hot = new();
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, List<TaskCompletionSource<ExtractedColor?>>> _pending = new(StringComparer.Ordinal);
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _workerTask;
    private readonly int _batchSize;
    private readonly TimeSpan _debounceWindow;

    public ExtractedColorService(
        IPathfinderClient pathfinder,
        IMetadataDatabase db,
        ILogger? logger = null,
        TimeSpan? debounceWindow = null,
        int? batchSize = null)
    {
        _pathfinder = pathfinder;
        _db = db;
        _logger = logger;
        _debounceWindow = debounceWindow ?? DefaultDebounceWindow;
        _batchSize = batchSize ?? DefaultBatchSize;
        _workerTask = Task.Run(() => WorkerLoopAsync(_shutdownCts.Token));
    }

    public async Task<ExtractedColor?> GetColorAsync(string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;

        // 1. Hot cache
        if (_hot.TryGetValue(imageUrl, out var cached)) return cached;

        // 2. SQLite
        try
        {
            var dbResult = await _db.GetColorCacheAsync(imageUrl, ct);
            if (dbResult.HasValue)
            {
                var color = SanitizeColor(dbResult.Value.DarkHex, dbResult.Value.LightHex, dbResult.Value.RawHex);
                if (color != null)
                {
                    TryAddBounded(imageUrl, color);
                    return color;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SQLite color cache read failed for {Url}", imageUrl);
        }

        // 3. API
        var colors = await GetColorsAsync([imageUrl], ct);
        return colors.GetValueOrDefault(imageUrl);
    }

    public async Task<Dictionary<string, ExtractedColor>> GetColorsAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ExtractedColor>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var url in imageUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal))
        {
            if (_hot.TryGetValue(url, out var cached))
            {
                result[url] = cached;
                continue;
            }

            // Check SQLite
            try
            {
                var dbResult = await _db.GetColorCacheAsync(url, ct);
                if (dbResult.HasValue)
                {
                    var color = SanitizeColor(dbResult.Value.DarkHex, dbResult.Value.LightHex, dbResult.Value.RawHex);
                    if (color != null)
                    {
                        TryAddBounded(url, color);
                        result[url] = color;
                        continue;
                    }
                }
            }
            catch { /* fall through to API */ }

            missing.Add(url);
        }

        if (missing.Count > 0)
        {
            try
            {
                var resolved = await ResolveMissingColorsAsync(missing, ct).ConfigureAwait(false);
                foreach (var (url, color) in resolved)
                    result[url] = color;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Extracted color fetch canceled for {Count} images", missing.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch extracted colors for {Count} images", missing.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds to hot cache with bounded eviction. When the cache exceeds MaxHotCacheSize,
    /// it is cleared entirely — colors are cheap to re-fetch from SQLite (tier 2).
    /// </summary>
    private void TryAddBounded(string key, ExtractedColor color)
    {
        if (_hot.Count >= MaxHotCacheSize)
            _hot.Clear();

        _hot.TryAdd(key, color);
    }

    private async Task<Dictionary<string, ExtractedColor>> ResolveMissingColorsAsync(
        IReadOnlyList<string> imageUrls,
        CancellationToken ct)
    {
        var pendingTasks = new Dictionary<string, Task<ExtractedColor?>>(StringComparer.Ordinal);
        var result = new Dictionary<string, ExtractedColor>(StringComparer.Ordinal);

        foreach (var url in imageUrls)
        {
            if (_hot.TryGetValue(url, out var cached))
            {
                result[url] = cached;
                continue;
            }

            pendingTasks[url] = QueueColorFetchAsync(url, ct).AsTask();
        }

        foreach (var (url, task) in pendingTasks)
        {
            var color = await task.ConfigureAwait(false);
            if (color != null)
                result[url] = color;
        }

        return result;
    }

    private ValueTask<ExtractedColor?> QueueColorFetchAsync(string imageUrl, CancellationToken ct)
    {
        if (_hot.TryGetValue(imageUrl, out var cached))
            return new ValueTask<ExtractedColor?>(cached);

        var tcs = new TaskCompletionSource<ExtractedColor?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool shouldQueue = false;

        lock (_pendingLock)
        {
            if (_hot.TryGetValue(imageUrl, out cached))
                return new ValueTask<ExtractedColor?>(cached);

            if (_pending.TryGetValue(imageUrl, out var awaiters))
            {
                awaiters.Add(tcs);
            }
            else
            {
                _pending[imageUrl] = [tcs];
                shouldQueue = true;
            }
        }

        if (shouldQueue)
            _channel.Writer.TryWrite(imageUrl);

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                if (!tcs.TrySetCanceled(ct))
                    return;

                lock (_pendingLock)
                {
                    if (_pending.TryGetValue(imageUrl, out var awaiters))
                    {
                        awaiters.Remove(tcs);
                        if (awaiters.Count == 0)
                            _pending.Remove(imageUrl);
                    }
                }
            });
        }

        return new ValueTask<ExtractedColor?>(tcs.Task);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool hasItem;
                try
                {
                    hasItem = await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!hasItem)
                    break;

                var batch = new List<string>(_batchSize);
                DrainAvailable(batch);

                var deadline = DateTime.UtcNow + _debounceWindow;
                while (batch.Count < _batchSize)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;

                    try
                    {
                        var poll = TimeSpan.FromMilliseconds(
                            Math.Min(5, Math.Max(1, remaining.TotalMilliseconds)));
                        await Task.Delay(poll, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    DrainAvailable(batch);
                }

                if (batch.Count == 0)
                    continue;

                await FlushBatchAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ExtractedColorService worker loop crashed");
        }
        finally
        {
            FailAllPending();
        }
    }

    private void DrainAvailable(List<string> batch)
    {
        while (batch.Count < _batchSize && _channel.Reader.TryRead(out var url))
            batch.Add(url);
    }

    private async Task FlushBatchAsync(List<string> batch, CancellationToken ct)
    {
        var toFetch = batch
            .Where(url => !_hot.ContainsKey(url) && HasPending(url))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (toFetch.Count == 0)
            return;

        try
        {
            var response = await _pathfinder.GetExtractedColorsAsync(toFetch, ct).ConfigureAwait(false);
            var entries = response.Data?.ExtractedColors;

            for (int i = 0; i < toFetch.Count; i++)
            {
                var url = toFetch[i];
                var entry = entries != null && i < entries.Count ? entries[i] : null;
                var color = entry == null
                    ? null
                    : SanitizeColor(entry.ColorDark?.Hex, entry.ColorLight?.Hex, entry.ColorRaw?.Hex);

                if (color != null)
                {
                    TryAddBounded(url, color);
                    PersistColorAsync(url, color);
                }

                CompletePending(url, color);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch extracted colors for {Count} images", toFetch.Count);
            foreach (var url in toFetch)
                CompletePending(url, null);
        }
    }

    private void PersistColorAsync(string url, ExtractedColor color)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _db.SetColorCacheAsync(url, color.DarkHex, color.LightHex, color.RawHex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to persist color to SQLite for {Url}", url);
            }
        });
    }

    private void CompletePending(string url, ExtractedColor? color)
    {
        List<TaskCompletionSource<ExtractedColor?>>? awaiters;
        lock (_pendingLock)
        {
            if (!_pending.Remove(url, out awaiters))
                return;
        }

        foreach (var tcs in awaiters)
            tcs.TrySetResult(color);
    }

    private bool HasPending(string url)
    {
        lock (_pendingLock)
        {
            return _pending.ContainsKey(url);
        }
    }

    private void FailAllPending()
    {
        List<TaskCompletionSource<ExtractedColor?>> awaiters;
        lock (_pendingLock)
        {
            awaiters = _pending.Values.SelectMany(list => list).ToList();
            _pending.Clear();
        }

        foreach (var tcs in awaiters)
            tcs.TrySetCanceled();
    }

    private static ExtractedColor? SanitizeColor(string? darkHex, string? lightHex, string? rawHex)
    {
        var dark = NormalizeHex(darkHex);
        var light = NormalizeHex(lightHex);
        var raw = NormalizeHex(rawHex);

        return dark == null && light == null && raw == null
            ? null
            : new ExtractedColor(dark, light, raw);
    }

    private static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var normalized = hex.Trim().TrimStart('#');
        if (normalized.Length is not 6 and not 8)
            return null;

        for (int i = 0; i < normalized.Length; i++)
        {
            if (!Uri.IsHexDigit(normalized[i]))
                return null;
        }

        return $"#{normalized.ToUpperInvariant()}";
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        _channel.Writer.TryComplete();

        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch
        {
        }

        _shutdownCts.Dispose();
    }
}
