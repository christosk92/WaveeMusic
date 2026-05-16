using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Outbox;

/// <summary>
/// Default <see cref="IOutboxProcessor"/>. Holds the single retry loop the
/// whole app uses for background-synced operations; per-op behaviour comes from
/// the <see cref="IOutboxHandler"/> instances registered via DI.
/// </summary>
public sealed class OutboxProcessor : IOutboxProcessor
{
    /// <summary>Cap retries so a permanently-broken op doesn't stick in the queue forever.</summary>
    public const int MaxRetries = 10;

    private readonly IMetadataDatabase _db;
    private readonly IReadOnlyDictionary<string, IOutboxHandler> _handlers;
    private readonly ILogger? _logger;
    private int _runningFlag;

    public OutboxProcessor(
        IMetadataDatabase db,
        IEnumerable<IOutboxHandler> handlers,
        ILogger<OutboxProcessor>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger;

        // Build the kind→handler map. Duplicate kinds are a wiring bug — fail
        // loud rather than silently shadow.
        var dict = new Dictionary<string, IOutboxHandler>(StringComparer.Ordinal);
        foreach (var h in handlers ?? throw new ArgumentNullException(nameof(handlers)))
        {
            if (string.IsNullOrEmpty(h.OpKind))
                throw new InvalidOperationException($"{h.GetType().Name} returned an empty OpKind");
            if (!dict.TryAdd(h.OpKind, h))
                throw new InvalidOperationException(
                    $"Duplicate outbox handler for kind '{h.OpKind}': {dict[h.OpKind].GetType().Name} and {h.GetType().Name}");
        }
        _handlers = dict;
    }

    public async Task<int> RunAsync(int limit = 50, CancellationToken ct = default)
    {
        // Serialize overlapping invocations to a single in-flight run; callers
        // can fire-and-forget without coordinating. Returning 0 here is the
        // signal "another run is already draining; nothing to do".
        if (Interlocked.Exchange(ref _runningFlag, 1) == 1) return 0;

        var failed = 0;
        try
        {
            var ops = await _db.DequeueOutboxAsync(limit, ct).ConfigureAwait(false);
            if (ops.Count == 0) return 0;

            foreach (var op in ops)
            {
                try
                {
                    if (op.RetryCount >= MaxRetries)
                    {
                        _logger?.LogWarning(
                            "Outbox op exceeded max retries, dropping: {Kind} {Uri} (last error: {Error})",
                            op.OpKind, op.PrimaryUri, op.LastError);
                        await _db.CompleteOutboxAsync(op.Id, ct).ConfigureAwait(false);
                        failed++;
                        continue;
                    }

                    if (!_handlers.TryGetValue(op.OpKind, out var handler))
                    {
                        // Forward-compat: a row queued by a newer version of the
                        // app whose handler doesn't exist in this build. Drop
                        // rather than retry-forever — the user can re-trigger.
                        _logger?.LogWarning(
                            "Outbox op has no registered handler, dropping: {Kind} {Uri}",
                            op.OpKind, op.PrimaryUri);
                        await _db.CompleteOutboxAsync(op.Id, ct).ConfigureAwait(false);
                        failed++;
                        continue;
                    }

                    await handler.ProcessAsync(op, ct).ConfigureAwait(false);
                    await _db.CompleteOutboxAsync(op.Id, ct).ConfigureAwait(false);

                    _logger?.LogDebug("Outbox synced: {Kind} {Uri}", op.OpKind, op.PrimaryUri);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cooperative shutdown — leave the entry queued; the next
                    // RunAsync picks it up.
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger?.LogWarning(
                        ex, "Outbox op failed (retry {Count}): {Kind} {Uri}",
                        op.RetryCount + 1, op.OpKind, op.PrimaryUri);
                    await _db.FailOutboxAsync(op.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Outbox processing crashed");
            failed++;
        }
        finally
        {
            Interlocked.Exchange(ref _runningFlag, 0);
        }
        return failed;
    }
}
