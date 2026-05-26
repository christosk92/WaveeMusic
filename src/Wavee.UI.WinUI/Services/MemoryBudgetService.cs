using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Soft process memory budget monitor. This is intentionally not an OS hard
/// cap: hard caps make native WinUI/WebView/media allocations fail abruptly.
/// Instead, when the resident process footprint or managed heap crosses the
/// budget, or the resident footprint moves well beyond the normal WinUI image
/// cache band, we clear stale warm caches. Private bytes are logged for leak
/// diagnostics, but are not used as the cleanup trigger: WinUI / DirectX heaps
/// can keep committed address space for hours after the working set has fallen,
/// and treating that as active pressure churns page and image caches without
/// freeing useful memory.
/// </summary>
public sealed class MemoryBudgetService : IDisposable, IAsyncDisposable
{
    public const long DefaultBudgetBytes = 800L * 1024 * 1024;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NormalCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EscalationCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan EmergencyCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PressureCacheMaxAge = TimeSpan.FromSeconds(30);

    // Tier-3 fires when both Tier-1 (stale cleanup) and Tier-2 (warm-cache
    // clear) have run and the working set is STILL ≥ this multiple of the
    // soft budget. 1.10× = 10% overshoot after escalation. Below this we
    // tolerate the overshoot until the next pressure tick to avoid a
    // hard-clear loop on a transient spike.
    private const double EmergencyTriggerMultiple = 1.10;
    private const double ManagedHeapTriggerMultiple = 0.75;
    private const double WorkingSetTriggerMultiple = 1.08;
    private const long MinimumWorkingSetTriggerBytes = 864L * 1024 * 1024;

    private readonly IReadOnlyList<ICleanableCache> _caches;
    private readonly ILogger<MemoryBudgetService>? _logger;
    private readonly Process _process = Process.GetCurrentProcess();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset _lastReleaseAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEscalationAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEmergencyAt = DateTimeOffset.MinValue;
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

        // OS memory-pressure signal. Fires when *the system* (not just our
        // budget) considers the app's usage to have grown into a higher band
        // (None → Low → Medium → High → OverLimit). Subscribing turns the
        // budget service from a fixed-threshold gate into a reactive cleaner —
        // when Windows says "you're under pressure" we shed caches immediately
        // instead of waiting for the next 10-second poll tick to notice we
        // crossed our own absolute threshold.
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            Windows.System.MemoryManager.AppMemoryUsageLimitChanging += OnAppMemoryUsageLimitChanging;
            _memoryPressureHooked = true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Memory budget: failed to subscribe to MemoryManager events");
        }

        _logger?.LogInformation(
            "Memory budget monitor started. ManagedBudget={BudgetMb:F0}MB workingSetTrigger={WorkingSetTriggerMb:F0}MB interval={Interval} osPressureHook={Hook}",
            _budgetBytes / 1048576.0,
            WorkingSetTriggerBytes(_budgetBytes) / 1048576.0,
            CheckInterval,
            _memoryPressureHooked);
    }

    private bool _memoryPressureHooked;

    private void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        // Bypass the cooldown — the OS doesn't raise this every tick; if it
        // fires we should respond.
        _lastReleaseAt = DateTimeOffset.MinValue;
        _ = Task.Run(async () =>
        {
            try
            {
                _logger?.LogInformation(
                    "Memory budget: OS reported AppMemoryUsageIncreased (level={Level}) — running eviction",
                    Windows.System.MemoryManager.AppMemoryUsageLevel);
                if (_cts is { } cts)
                    await CheckAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Memory budget: OS-pressure-driven eviction failed");
            }
        });
    }

    private void OnAppMemoryUsageLimitChanging(object? sender, Windows.System.AppMemoryUsageLimitChangingEventArgs args)
    {
        // The OS is about to lower our usage limit. If our current usage
        // exceeds the *new* limit, we're about to be killed unless we shed
        // memory now. Run eviction synchronously-ish (fire-and-forget Task)
        // before the limit takes effect.
        if (args.NewLimit < args.OldLimit)
        {
            _lastReleaseAt = DateTimeOffset.MinValue;
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger?.LogWarning(
                        "Memory budget: OS lowering limit {OldMb:F0}MB → {NewMb:F0}MB — eviction now",
                        args.OldLimit / 1048576.0, args.NewLimit / 1048576.0);
                    if (_cts is { } cts)
                        await CheckAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Memory budget: limit-change eviction failed");
                }
            });
        }
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
        // While a navigation is in flight, defer the whole check cycle. The
        // cleanup paths below tear down HotCache<TrackCacheEntry> and the warm
        // cache layer — exactly what the in-flight nav is trying to read from.
        // Letting them run mid-nav evicted the very state the next transition
        // wanted (visible as the post-nav stall). TryDeferRelease already gated
        // the working-set trim, but the cache shootdowns weren't gated, which
        // was the actual cost. The next 10 s tick re-checks; if we're still
        // over budget after the nav window closes, cleanup runs then. OS
        // pressure events also route through here — a 4 s nav-window deferral
        // is acceptable for those.
        if (NavigationGcCoordinator.IsNavigationCritical)
            return;

        var snapshot = Capture();
        if (!IsOverBudget(snapshot, _budgetBytes))
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

        await LogMemoryAttributionAsync().ConfigureAwait(false);

        await CleanupStaleCachesAsync(ct).ConfigureAwait(false);
        CompactAndTrim("budget");

        // Tier-1b: re-sweep nav-cache GPU surfaces. Steady-state retention
        // already sheds dormant surfaces on every nav / tab-switch; this is a
        // safety sweep that also drops each tab's prime-back-target under real
        // pressure (keeping only the on-screen page).
        await ReleaseDormantNavSurfacesAsync().ConfigureAwait(false);

        var after = Capture();
        if (!IsOverBudget(after, _budgetBytes) || now - _lastEscalationAt < EscalationCooldown)
            return;

        _lastEscalationAt = now;
        await ClearWarmCachesAsync(ct).ConfigureAwait(false);
        CompactAndTrim("budget-escalated");

        // Tier-3 emergency: still over budget by ≥10% after a Tier-2 sweep,
        // and we're past the emergency cooldown. Hard-clear the image cache
        // (drops pinned surfaces too — visible CompositionImage controls
        // repaint placeholders then re-fetch from the OS HTTP cache, fast
        // because the bytes never left). Other ICleanableCache implementations
        // already cleared via Tier-2's ClearAsync().
        var afterEsc = Capture();
        if (!IsEmergencyOverBudget(afterEsc, _budgetBytes)) return;
        if (now - _lastEmergencyAt < EmergencyCooldown) return;

        _lastEmergencyAt = now;
        await ClearImageCacheHardAsync(ct).ConfigureAwait(false);
        CompactAndTrim("budget-emergency");
    }

    private Task ClearImageCacheHardAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var svc = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ImageCacheService>();
            if (svc is null) return Task.CompletedTask;
            var before = svc.Count;
            var pinned = svc.PinnedCount;
            svc.Clear();
            _logger?.LogWarning(
                "Memory budget Tier-3 emergency: hard-cleared image cache ({Before} entries, pinned={Pinned})",
                before,
                pinned);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Tier-3 image cache hard-clear failed");
        }
        return Task.CompletedTask;
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

    /// <summary>
    /// Emits the <c>[mem-attribution]</c> breakdown line — confirms where the
    /// footprint actually sits (managed heap by generation, image cache, and
    /// the GPU surfaces held by cached pages) so the result of the nav-cache
    /// surface work is measurable.
    /// </summary>
    private async Task LogMemoryAttributionAsync()
    {
        if (_logger is null)
            return;

        try
        {
            var imageCache = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ImageCacheService>();
            var icCount = imageCache?.Count ?? 0;
            var icPinned = imageCache?.PinnedCount ?? 0;
            var icMb = (imageCache?.EstimatedBytes ?? 0) / 1048576.0;

            var gen = GC.GetGCMemoryInfo().GenerationInfo;
            var gen0 = gen.Length > 0 ? gen[0].SizeAfterBytes / 1048576.0 : 0.0;
            var gen1 = gen.Length > 1 ? gen[1].SizeAfterBytes / 1048576.0 : 0.0;
            var gen2 = gen.Length > 2 ? gen[2].SizeAfterBytes / 1048576.0 : 0.0;
            var loh = gen.Length > 3 ? gen[3].SizeAfterBytes / 1048576.0 : 0.0;
            var poh = gen.Length > 4 ? gen[4].SizeAfterBytes / 1048576.0 : 0.0;

            var (tabCount, pageCount, surfaceBytes) = await GatherPageAttributionAsync().ConfigureAwait(false);

            _logger.LogInformation(
                "[mem-attribution] imageCache={IcCount}/{IcMb:F1}MB pinned={IcPinned} | cachedPages={Pages} tabs={Tabs} navSurfaces={SurfMb:F1}MB | managed gen0={Gen0:F1} gen1={Gen1:F1} gen2={Gen2:F1} loh={Loh:F1} poh={Poh:F1} MB",
                icCount, icMb, icPinned,
                pageCount, tabCount, surfaceBytes / 1048576.0,
                gen0, gen1, gen2, loh, poh);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[mem-attribution] failed");
        }
    }

    private static Task<(int Tabs, int Pages, long SurfaceBytes)> GatherPageAttributionAsync()
    {
        var dispatcher = MainWindow.Instance?.DispatcherQueue;
        if (dispatcher is null)
            return Task.FromResult((0, 0, 0L));

        var tcs = new TaskCompletionSource<(int, int, long)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Gather()
        {
            try
            {
                var tabs = ShellViewModel.TabInstances;
                var pageCount = 0;
                long surfaceBytes = 0;
                foreach (var tab in tabs)
                {
                    foreach (var page in tab.ContentHost.CachedPagesByRecency())
                    {
                        pageCount++;
                        surfaceBytes += NavCacheSurfaces.SumEstimatedBytes(page);
                    }
                }
                tcs.TrySetResult((tabs.Count, pageCount, surfaceBytes));
            }
            catch
            {
                tcs.TrySetResult((0, 0, 0L));
            }
        }

        if (dispatcher.HasThreadAccess)
            Gather();
        else if (!dispatcher.TryEnqueue(Gather))
            tcs.TrySetResult((0, 0, 0L));

        return tcs.Task;
    }

    private static Task ReleaseDormantNavSurfacesAsync()
    {
        var dispatcher = MainWindow.Instance?.DispatcherQueue;
        if (dispatcher is null)
            return Task.CompletedTask;

        if (dispatcher.HasThreadAccess)
        {
            try { TabBarItem.ReleaseDormantSurfacesAllTabs(); }
            catch { /* best-effort pressure sweep */ }
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try { TabBarItem.ReleaseDormantSurfacesAllTabs(); }
                catch { /* best-effort pressure sweep */ }
                tcs.TrySetResult();
            }))
        {
            tcs.TrySetResult();
        }

        return tcs.Task;
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
            // navigation hangs (see nav-health report from 2026-05-07). The
            // runtime self-tunes; manual collects fight it and produce a Gen0 ≈
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

    private static bool IsOverBudget(MemoryBudgetSnapshot snapshot, long budgetBytes)
        => snapshot.WorkingSetBytes >= WorkingSetTriggerBytes(budgetBytes)
           || snapshot.ManagedHeapBytes >= budgetBytes * ManagedHeapTriggerMultiple;

    private static long WorkingSetTriggerBytes(long budgetBytes)
        => Math.Max(MinimumWorkingSetTriggerBytes, (long)(budgetBytes * WorkingSetTriggerMultiple));

    private static bool IsEmergencyOverBudget(MemoryBudgetSnapshot snapshot, long budgetBytes)
        => snapshot.ManagedHeapBytes >= budgetBytes * ManagedHeapTriggerMultiple
           || snapshot.WorkingSetBytes >= WorkingSetTriggerBytes(budgetBytes) * EmergencyTriggerMultiple;

    private void UnhookMemoryPressure()
    {
        if (!_memoryPressureHooked) return;
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
            Windows.System.MemoryManager.AppMemoryUsageLimitChanging -= OnAppMemoryUsageLimitChanging;
        }
        catch { }
        _memoryPressureHooked = false;
    }

    public async ValueTask DisposeAsync()
    {
        UnhookMemoryPressure();

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
        UnhookMemoryPressure();
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
