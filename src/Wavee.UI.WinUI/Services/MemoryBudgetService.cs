using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Soft process memory budget monitor. This is intentionally not an OS hard
/// cap: hard caps make native WinUI/WebView/media allocations fail abruptly.
/// Instead, when the process crosses the budget, we clear stale warm caches,
/// compact the managed heap, and ask Windows to trim unused working-set pages.
/// </summary>
public sealed class MemoryBudgetService : IDisposable, IAsyncDisposable
{
    // Steady-state private bytes after the x:Bind / VM-singleton-sub leak fix
    // sit at 400-600 MB. Headroom to 1.5 GB keeps the budget service quiet for
    // normal sessions; it only fires for genuine pathological growth. Earlier
    // 800 MB threshold caused a periodic blocking compacting Gen2 every cooldown
    // window once the leak held us at 838 MB — felt as recurring stutter.
    public const long DefaultBudgetBytes = 1_500L * 1024 * 1024;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NormalCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EscalationCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PressureCacheMaxAge = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<ICleanableCache> _caches;
    private readonly ILogger<MemoryBudgetService>? _logger;
    private readonly Process _process = Process.GetCurrentProcess();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset _lastReleaseAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEscalationAt = DateTimeOffset.MinValue;
    private long _budgetBytes = DefaultBudgetBytes;

    public MemoryBudgetService(
        IEnumerable<ICleanableCache> caches,
        ILogger<MemoryBudgetService>? logger = null)
    {
        _caches = caches.ToList();
        _logger = logger;
    }

    public void Start(long budgetBytes = DefaultBudgetBytes)
    {
        if (_timer is not null)
            return;

        _budgetBytes = Math.Max(128L * 1024 * 1024, budgetBytes);
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(CheckInterval);
        _loopTask = RunAsync(_cts.Token);

        _logger?.LogInformation(
            "Memory budget monitor started. Budget={BudgetMb:F0}MB interval={Interval}",
            _budgetBytes / 1048576.0,
            CheckInterval);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Memory budget monitor stopped");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Memory budget monitor failed");
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var snapshot = Capture();
        var observedBytes = Math.Max(snapshot.WorkingSetBytes, snapshot.PrivateBytes);
        if (observedBytes < _budgetBytes)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastReleaseAt < NormalCooldown)
            return;

        _lastReleaseAt = now;
        _logger?.LogWarning(
            "Memory budget exceeded: workingSet={WorkingSetMb:F1}MB private={PrivateMb:F1}MB managed={ManagedMb:F1}MB budget={BudgetMb:F1}MB",
            snapshot.WorkingSetBytes / 1048576.0,
            snapshot.PrivateBytes / 1048576.0,
            snapshot.ManagedHeapBytes / 1048576.0,
            _budgetBytes / 1048576.0);

        await CleanupStaleCachesAsync(ct).ConfigureAwait(false);
        CompactAndTrim("budget");

        var after = Capture();
        var afterObserved = Math.Max(after.WorkingSetBytes, after.PrivateBytes);
        if (afterObserved <= _budgetBytes || now - _lastEscalationAt < EscalationCooldown)
            return;

        _lastEscalationAt = now;
        await ClearWarmCachesAsync(ct).ConfigureAwait(false);
        CompactAndTrim("budget-escalated");
    }

    private async Task CleanupStaleCachesAsync(CancellationToken ct)
    {
        var totalRemoved = 0;
        foreach (var cache in _caches)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                totalRemoved += await cache.CleanupStaleEntriesAsync(PressureCacheMaxAge, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Memory budget stale cleanup failed for {Cache}", cache.CacheName);
            }
        }

        if (totalRemoved > 0)
            _logger?.LogInformation("Memory budget stale cleanup removed {Count} cache entries", totalRemoved);
    }

    private async Task ClearWarmCachesAsync(CancellationToken ct)
    {
        var totalCleared = 0;
        foreach (var cache in _caches)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                totalCleared += await cache.ClearAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Memory budget clear failed for {Cache}", cache.CacheName);
            }
        }

        _logger?.LogWarning("Memory budget escalated cleanup cleared {Count} warm cache entries", totalCleared);
    }

    private void CompactAndTrim(string reason)
    {
        if (NavigationGcCoordinator.TryDeferRelease(_logger, reason))
            return;

        try
        {
            // Just trim working set. Earlier this also did
            // GC.Collect(Gen2, blocking: true, compacting: true) on every
            // 10-second pressure tick, which was responsible for ~60% of the
            // forced Gen2 compacts in a session and the "stall then BOOM"
            // navigation hangs (see nav-health report from 2026-05-07). DATAS
            // GC self-tunes; manual collects fight it and produce a Gen0 ≈
            // Gen1 ≈ Gen2 counter ratio that is impossible organically.
            MemoryReleaseHelper.TrimWorkingSet(_logger, reason);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Memory budget release failed");
        }
    }

    private MemoryBudgetSnapshot Capture()
    {
        try
        {
            _process.Refresh();
            return new MemoryBudgetSnapshot(
                _process.WorkingSet64,
                _process.PrivateMemorySize64,
                GC.GetTotalMemory(forceFullCollection: false));
        }
        catch
        {
            return new MemoryBudgetSnapshot(0, 0, GC.GetTotalMemory(forceFullCollection: false));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        _timer?.Dispose();
        _timer = null;

        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _loopTask = null;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _timer?.Dispose();
        _timer = null;
    }

    private readonly record struct MemoryBudgetSnapshot(
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedHeapBytes);
}
