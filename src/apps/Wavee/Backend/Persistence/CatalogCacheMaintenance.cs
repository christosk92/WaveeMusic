using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Persistence;

/// <summary>Cache retention. Demand snapshots are detached and thread-safe.
///
/// Maintenance NEVER occupies the shared commit owner. It used to: <c>SweepAsync</c> ran each
/// <c>RunCatalogGcBatch</c> as one <c>DataCommitQueue</c> command, and in the native ARM64 tour of 2026-09-09 that
/// command held the owner for 3,429 ms while deleting nothing. The three navigation commands that arrived during it
/// (<c>RecordRecentAsync</c>, two <c>PreloadAsync</c>) reported <c>queueMs</c> 1,405-1,445 ms and the artist page
/// revealed in 1,483 ms — every other cold reveal in that session was 55-68 ms. The queue depth, not the query, was
/// the page's cost.
///
/// So the shape here is: the cold store already serializes its own writes on its connection lock, and
/// <see cref="CatalogRepository.ForgetEvicted"/> publishes under the publication gate rather than on the owner, so
/// the owner was never needed for any of this. Every database call below runs on the thread pool, and
/// <see cref="CatalogSweepSchedule"/> decides whether a sweep runs at all (the cheap probe says nothing is
/// evictable → the whole-table scans are skipped) and whether now is the moment (the owner is quiet and no surface
/// just opened → nothing is waiting on the writer). <c>_commits</c> survives only as those two signals.</summary>
public sealed class CatalogCacheMaintenance : IAsyncDisposable
{
    readonly SqliteColdStore _cold;
    readonly CatalogRepository _catalog;
    readonly DataCommitQueue _commits;
    readonly Func<IReadOnlyCollection<ResourceKey>> _pins;
    readonly TimeProvider _time;
    readonly Action<Exception> _report;
    readonly CancellationTokenSource _lifetime = new();
    Task? _loop;
    long _budget;
    long _surfaceOpenedAtMs;
    public CatalogCacheMaintenance(SqliteColdStore cold, CatalogRepository catalog, DataCommitQueue commits,
        Func<IReadOnlyCollection<ResourceKey>> pins, TimeProvider time, long budgetBytes, Action<Exception> report)
        => (_cold, _catalog, _commits, _pins, _time, _budget, _report) = (cold, catalog, commits, pins, time, budgetBytes, report);
    public long BudgetBytes
    {
        get => Interlocked.Read(ref _budget);
        set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); _ = ObserveAsync(SetBudgetAsync(value)); }
    }
    public void Start() => _loop ??= RunAsync();

    /// <summary>Every cold-store call maintenance makes goes through here: the thread pool, never the commit owner.
    /// The caller's token gates the START of the work, not its middle — an open SQLite transaction belongs to the
    /// database, and awaiting it here is what lets <see cref="DisposeAsync"/> outlive it.</summary>
    async Task<T> OffOwnerAsync<T>(Func<T> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await Task.Run(operation).ConfigureAwait(false);
    }

    public async Task SetBudgetAsync(long value, CancellationToken ct = default)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        await OffOwnerAsync(() =>
        {
            _cold.SetCatalogBudget(value); Interlocked.Exchange(ref _budget, value); return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A surface was opened. Also the reveal signal <see cref="CatalogSweepSchedule"/> reads: this is the
    /// one call maintenance already receives on every navigation, so no UI hook is needed to know a page is
    /// loading right now.</summary>
    public Task RecordRecentAsync(string uri, int kind, CancellationToken ct = default)
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _surfaceOpenedAtMs, now);
        return OffOwnerAsync(() => { _cold.RecordRecentCatalogSurface(uri, kind, now); return true; }, ct);
    }

    public Task<EntityCacheStats> GetStatsAsync(CancellationToken ct = default)
        => OffOwnerAsync(() => _cold.ReadCatalogStats(BudgetBytes, _pins(), _time.GetUtcNow()), ct);

    public Task<CatalogEviction> ClearAsync(CancellationToken ct = default) => SweepAsync(clear: true, ct);

    /// <summary>Evicts what retention says to evict, in batches, without ever queueing behind — or ahead of —
    /// foreground work. A clear always runs to completion; a periodic sweep runs only when the probe says a batch
    /// could delete something, and waits for a quiet owner before each batch.</summary>
    public async Task<CatalogEviction> SweepAsync(bool clear = false, CancellationToken ct = default)
    {
        int rows = 0; long bytes = 0;
        var removed = new List<ResourceKey>();
        int deferrals = 0, batches = 0;
        // One index-only probe per sweep (≈8 ms, on the read connection). A sweep whose answer is "nothing" costs
        // that instead of the seconds the batch's whole-table scans cost to reach the same zero-row result.
        long probeStart = _time.GetTimestamp();
        bool evictable = clear || await OffOwnerAsync(() => _cold.CatalogGcHasWork(BudgetBytes, _time.GetUtcNow()), ct).ConfigureAwait(false);
        double probeMs = _time.GetElapsedTime(probeStart).TotalMilliseconds;
        var decision = CatalogSweepSchedule.Next(StateOf(clear, evictable, deferrals));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (decision.Action == CatalogSweepAction.Skip) break;
            if (decision.Action == CatalogSweepAction.Defer)
            {
                deferrals++;
                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
                decision = CatalogSweepSchedule.Next(StateOf(clear, evictable, deferrals));
                continue;
            }
            var batch = await OffOwnerAsync(() => _cold.RunCatalogGcBatch(_pins(), BudgetBytes, _time.GetUtcNow(), clear), ct).ConfigureAwait(false);
            batches++;
            // The resident map is dropped under the publication gate, not on the owner, and only when there is
            // something to drop. A resource re-fetched between the delete and this publish is simply re-read from
            // the store on its next join — the eviction cannot lose a fresh value, only a resident copy of one.
            if (batch.Resources.Count != 0) _catalog.ForgetEvicted(batch.Resources);
            rows += batch.Rows; bytes += batch.Bytes; removed.AddRange(batch.Resources);
            decision = CatalogSweepSchedule.AfterBatch(batch.Rows, StateOf(clear, evictable, deferrals));
        }
        Report(clear, decision.Reason, probeMs, batches, deferrals, rows, bytes);
        return new(removed, rows, bytes);
    }

    CatalogSweepState StateOf(bool clear, bool evictable, int deferrals)
        => new(clear, evictable, _commits.Pending, _commits.OwnerIdleFor,
            _time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _surfaceOpenedAtMs)), deferrals);

    // The old `commit.slow label=SweepAsync` line was the only place a multi-second sweep was visible; taking the
    // sweep off the owner would have taken that line with it. This replaces it, always on, one line per sweep, and
    // it prints the numbers the tour needed: what the probe cost, whether a batch ran, and what it actually freed.
    void Report(bool clear, string reason, double probeMs, int batches, int deferrals, int rows, long bytes)
        => WaveeLog.Instance.Event(WaveeLogLevel.Info, "catalog", "catalog.sweep",
            "catalog sweep " + (clear ? "clear" : "periodic") + " outcome=" + reason
            + " probeMs=" + probeMs.ToString("0.0", CultureInfo.InvariantCulture)
            + " batches=" + batches.ToString(CultureInfo.InvariantCulture)
            + " deferrals=" + deferrals.ToString(CultureInfo.InvariantCulture)
            + " rows=" + rows.ToString(CultureInfo.InvariantCulture)
            + " freedBytes=" + bytes.ToString(CultureInfo.InvariantCulture));

    async Task RunAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _time, _lifetime.Token).ConfigureAwait(false);
            await SetBudgetAsync(BudgetBytes, _lifetime.Token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), _time);
            do
            {
                try
                {
                    await SweepAsync(ct: _lifetime.Token).ConfigureAwait(false);
                    // Compaction already runs on the thread pool and takes both store locks; it never needed the owner.
                    await _cold.CompactCatalogIfIdleAsync(_pins().Count != 0, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception error) { _report(error); }
            } while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _report(error); }
    }
    async Task ObserveAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception error) { _report(error); } }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
