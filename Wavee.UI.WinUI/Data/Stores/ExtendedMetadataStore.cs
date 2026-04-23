using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.Core.Http;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.UI.WinUI.Data.Stores;

public readonly record struct ExtensionRequestKey(string Uri, ExtensionKind Kind);

// Batching reactive store for Spotify extended-metadata extensions.
//
// IExtendedMetadataClient.GetBatchedExtensionsAsync is a batch API — one HTTP
// POST covers N (uri, kind) pairs — but concurrent callers today each build
// their own batch and hit the network independently. Opening two playlist
// tabs with overlapping tracks produced two POSTs instead of one.
//
// This store wraps the client with per-(uri, kind) keyed inflight dedup +
// a 50ms debounce that merges every caller's request into a single batched
// POST. The client still handles the SQLite cache tier internally — we do
// not duplicate it.
//
// Cancellation contract: `ct` cancels the individual caller's wait without
// aborting the in-flight batch (other subscribers may still need the result).
// When every subscriber for a pending batch cancels, the batch still fires
// because its output lands in the client's SQLite cache; the work is not
// wasted.
public sealed class ExtendedMetadataStore : EntityStore<ExtensionRequestKey, byte[]>
{
    private readonly IExtendedMetadataClient _client;
    private readonly TimeSpan _batchWindow;
    private readonly int _maxBatchSize;
    private new readonly ILogger? _logger;

    private readonly object _gate = new();
    private List<PendingRequest>? _pending;
    private Task? _batchFlushTask;

    public ExtendedMetadataStore(
        IExtendedMetadataClient client,
        ILogger<ExtendedMetadataStore>? logger = null,
        TimeSpan? batchWindow = null,
        int maxBatchSize = 200)
        : base(logger: logger)
    {
        _client = client;
        _logger = logger;
        _batchWindow = batchWindow ?? TimeSpan.FromMilliseconds(50);
        _maxBatchSize = maxBatchSize;
    }

    // The underlying client has a 1h SQLite cache; the store TTL governs when
    // a subscribed slot refetches. 1h parity keeps the two tiers in sync.
    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(1);

    protected override ValueTask<byte[]?> ReadHotAsync(ExtensionRequestKey key)
        => new((byte[]?)null);

    protected override ValueTask<byte[]?> ReadColdAsync(ExtensionRequestKey key, CancellationToken ct)
        => new((byte[]?)null);

    protected override Task<byte[]> FetchAsync(ExtensionRequestKey key, byte[]? previous, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(key, tcs);

        bool scheduleFlush = false;
        bool flushNow = false;
        List<PendingRequest>? immediateBatch = null;

        lock (_gate)
        {
            _pending ??= new List<PendingRequest>();
            _pending.Add(pending);

            if (_pending.Count >= _maxBatchSize)
            {
                immediateBatch = _pending;
                _pending = null;
                _batchFlushTask = null;
                flushNow = true;
            }
            else if (_batchFlushTask is null)
            {
                scheduleFlush = true;
            }
        }

        if (flushNow && immediateBatch is not null)
            _ = FlushBatchAsync(immediateBatch);
        else if (scheduleFlush)
        {
            var task = Task.Delay(_batchWindow).ContinueWith(_ => FlushDueBatchAsync(),
                TaskScheduler.Default).Unwrap();
            lock (_gate)
                _batchFlushTask = task;
        }

        // Intentionally do NOT wrap with WaitAsync(ct). Wrapping meant a
        // cancelled batch of N keys produced N first-chance OCE throws —
        // each one a full stack walk on ARM64 Debug. The batch itself
        // finishes in ≤ batchWindow + one HTTP round-trip, so letting
        // every awaiter wait for the real TCS is cheaper than throwing N
        // times. Consumers that need early-out check ct themselves after
        // awaiting (see ResolveSingleAsync).
        return tcs.Task;
    }

    protected override void WriteHot(ExtensionRequestKey key, byte[] value) { /* no-op — client owns SQLite */ }

    protected override Task WriteColdAsync(ExtensionRequestKey key, byte[] value, CancellationToken ct)
        => Task.CompletedTask;

    // ── Convenience API for Task-returning callers (LibraryDataService etc.) ──

    public async Task<byte[]?> GetOnceAsync(string uri, ExtensionKind kind, CancellationToken ct = default)
    {
        try
        {
            return await GetOnceAsync(new ExtensionRequestKey(uri, kind), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<ExtensionRequestKey, byte[]>> GetManyAsync(
        IEnumerable<(string Uri, IEnumerable<ExtensionKind> Kinds)> requests,
        CancellationToken ct = default)
    {
        var keys = requests
            .SelectMany(r => r.Kinds.Select(k => new ExtensionRequestKey(r.Uri, k)))
            .Distinct()
            .ToArray();

        var results = new Dictionary<ExtensionRequestKey, byte[]>(keys.Length);
        var tasks = new List<Task>(keys.Length);

        foreach (var key in keys)
        {
            tasks.Add(ResolveSingleAsync(key, results, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Deliberately do NOT ct.ThrowIfCancellationRequested() here. The batch
        // has already fired (the 50ms debounce window closed long before this
        // line runs), the network result has already landed in the client's
        // SQLite cache, and ResolveSingleAsync has silently skipped writing
        // keys whose caller cancelled. Throwing here would emit one OCE per
        // cancelled GetManyAsync call — pure cost with no behavior benefit
        // because the work is already done. Callers that need cancellation
        // semantics check ct themselves after awaiting.
        return results;
    }

    private async Task ResolveSingleAsync(
        ExtensionRequestKey key,
        Dictionary<ExtensionRequestKey, byte[]> results,
        CancellationToken ct)
    {
        try
        {
            // Fetch with CancellationToken.None so the shared TCS never
            // observes our caller's ct. If the caller cancelled, we just
            // drop the result on the floor after the await completes —
            // cheaper than throwing an OCE per key under batch cancel.
            var bytes = await GetOnceAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            // Empty sentinel = server didn't return data for this key (expected
            // when a requested track doesn't exist in the response). Skip the
            // write; callers iterate only over entries that ended up in the dict.
            if (bytes is null or { Length: 0 }) return;
            lock (results)
                results[key] = bytes;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ExtendedMetadataStore.ResolveSingleAsync failed for {Uri}/{Kind}", key.Uri, key.Kind);
        }
    }

    // ── Internal batching ──

    private async Task FlushDueBatchAsync()
    {
        List<PendingRequest>? batch;
        lock (_gate)
        {
            batch = _pending;
            _pending = null;
            _batchFlushTask = null;
        }
        if (batch is { Count: > 0 })
            await FlushBatchAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushBatchAsync(List<PendingRequest> batch)
    {
        try
        {
            // Collapse duplicate keys — multiple subscribers for the same (uri, kind)
            // still produce one server-side entry. Each pending TCS still gets fanned
            // out in the completion loop below.
            var uniqueGroups = batch
                .Select(p => p.Key)
                .Distinct()
                .GroupBy(k => k.Uri, StringComparer.Ordinal)
                .Select(g => (g.Key, (IEnumerable<ExtensionKind>)g.Select(k => k.Kind)))
                .ToList();

            var response = await _client.GetBatchedExtensionsAsync(uniqueGroups).ConfigureAwait(false);

            foreach (var p in batch)
            {
                if (p.Tcs.Task.IsCompleted)
                    continue; // per-subscriber cancellation already fired

                var ext = response.GetExtensionData(p.Key.Uri, p.Key.Kind);
                var bytes = ext?.ExtensionData?.Value?.ToByteArray();
                // "No data returned for this key" is an expected outcome (server
                // sometimes drops a requested entity). Signal it with an empty
                // byte[] sentinel instead of throwing KeyNotFoundException —
                // throwing a missing-data exception per track is what flooded
                // the debug Output window with dozens of first-chance throws.
                // ResolveSingleAsync treats Length==0 as "skip this key".
                p.Tcs.TrySetResult(bytes is { Length: > 0 } ? bytes : Array.Empty<byte>());
            }
        }
        catch (Exception ex)
        {
            foreach (var p in batch)
                p.Tcs.TrySetException(ex);
        }
    }

    private sealed record PendingRequest(ExtensionRequestKey Key, TaskCompletionSource<byte[]> Tcs);
}
