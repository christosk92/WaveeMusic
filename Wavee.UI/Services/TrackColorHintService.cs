using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;

namespace Wavee.UI.Services;

/// <summary>
/// Resolves per-track placeholder color hints by image URL, with request
/// dedupe, small-window batching, and a background worker. Sits on top of
/// <see cref="IColorService"/> (which already has 3-tier caching); this layer
/// exists specifically to coalesce virtualized-list scroll bursts into one
/// batched backend call per debounce window.
/// </summary>
public interface ITrackColorHintService
{
    /// <summary>Synchronous lookup; returns true if the URL has been resolved (hex may still be null when no color is available).</summary>
    bool TryGet(string imageUrl, out string? hex);

    /// <summary>
    /// Returns the resolved hex color for <paramref name="imageUrl"/>, or null if none is available.
    /// Concurrent requests for the same URL share a single backend call; requests within
    /// the debounce window are batched together.
    /// </summary>
    ValueTask<string?> GetOrResolveAsync(string imageUrl, CancellationToken ct = default);
}

public sealed class TrackColorHintService : ITrackColorHintService, IAsyncDisposable
{
    private const int DefaultBatchSize = 50;
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromMilliseconds(80);

    private readonly IColorService _colorService;
    private readonly ILogger<TrackColorHintService>? _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _debounceWindow;

    private readonly ConcurrentDictionary<string, string?> _resolved = new();

    private readonly Dictionary<string, List<TaskCompletionSource<string?>>> _pending = new();
    private readonly object _pendingLock = new();

    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _workerTask;

    public TrackColorHintService(
        IColorService colorService,
        TimeSpan? debounceWindow = null,
        int? batchSize = null,
        ILogger<TrackColorHintService>? logger = null)
    {
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _debounceWindow = debounceWindow ?? DefaultDebounceWindow;
        _batchSize = batchSize ?? DefaultBatchSize;
        _logger = logger;
        _workerTask = Task.Run(() => WorkerLoopAsync(_shutdownCts.Token));
    }

    public bool TryGet(string imageUrl, out string? hex)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            hex = null;
            return false;
        }
        return _resolved.TryGetValue(imageUrl, out hex);
    }

    public ValueTask<string?> GetOrResolveAsync(string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return new ValueTask<string?>((string?)null);

        if (_resolved.TryGetValue(imageUrl, out var cached))
            return new ValueTask<string?>(cached);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool shouldEnqueue;
        lock (_pendingLock)
        {
            // Recheck under the lock: the worker's CompletePending also takes this lock,
            // so if the batch completed between our fast-path check and here we'll see it now.
            if (_resolved.TryGetValue(imageUrl, out cached))
                return new ValueTask<string?>(cached);

            if (_pending.TryGetValue(imageUrl, out var awaiters))
            {
                awaiters.Add(tcs);
                shouldEnqueue = false;
            }
            else
            {
                _pending[imageUrl] = new List<TaskCompletionSource<string?>> { tcs };
                shouldEnqueue = true;
            }
        }

        if (shouldEnqueue)
            _channel.Writer.TryWrite(imageUrl);

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                if (!tcs.TrySetCanceled(ct)) return;
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

        return new ValueTask<string?>(tcs.Task);
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
                catch (OperationCanceledException) { break; }

                if (!hasItem) break; // channel completed

                var batch = new List<string>(_batchSize);
                DrainAvailable(batch);

                var deadline = DateTime.UtcNow + _debounceWindow;
                while (batch.Count < _batchSize)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;

                    try
                    {
                        var poll = TimeSpan.FromMilliseconds(Math.Min(5, Math.Max(1, remaining.TotalMilliseconds)));
                        await Task.Delay(poll, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }

                    DrainAvailable(batch);
                }

                if (batch.Count == 0)
                    continue;

                await FlushBatchAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TrackColorHintService worker loop crashed");
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
        var toFetch = batch.Where(u => !_resolved.ContainsKey(u)).Distinct().ToList();
        if (toFetch.Count == 0) return;

        Dictionary<string, ExtractedColor> results;
        try
        {
            results = await _colorService.GetColorsAsync(toFetch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex,
                "Color hint batch failed for {Count} URLs; negative-caching",
                toFetch.Count);
            foreach (var url in toFetch)
            {
                _resolved[url] = null;
                CompletePending(url, null);
            }
            return;
        }

        foreach (var url in toFetch)
        {
            string? hex = null;
            if (results.TryGetValue(url, out var color) && color != null)
                hex = color.DarkHex ?? color.RawHex ?? color.LightHex;
            _resolved[url] = hex;
            CompletePending(url, hex);
        }
    }

    private void CompletePending(string url, string? hex)
    {
        List<TaskCompletionSource<string?>>? awaiters;
        lock (_pendingLock)
        {
            if (!_pending.Remove(url, out awaiters)) return;
        }

        foreach (var tcs in awaiters)
            tcs.TrySetResult(hex);
    }

    private void FailAllPending()
    {
        List<TaskCompletionSource<string?>> all;
        lock (_pendingLock)
        {
            all = _pending.Values.SelectMany(list => list).ToList();
            _pending.Clear();
        }
        foreach (var tcs in all)
            tcs.TrySetCanceled();
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
            // Swallow — worker cancellation/failures already logged.
        }
        _shutdownCts.Dispose();
    }
}
