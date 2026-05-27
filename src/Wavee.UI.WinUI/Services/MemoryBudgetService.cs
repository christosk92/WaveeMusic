using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contexts;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Soft process memory observer. Warn-only: polls the working set + managed
/// heap on a fixed interval and emits a budget-exceeded warning plus a
/// <c>[mem-attribution]</c> breakdown so we can see where the footprint sits.
/// Does NOT touch caches, surfaces, or GC. The runtime's own self-tuning has
/// been more consistent than the manual eviction tiers this service used to
/// orchestrate (previous tiers cleared image cache / nav surfaces / triggered
/// compacting Gen2s, and shipped more pauses than they prevented).
/// </summary>
public sealed class MemoryBudgetService : IDisposable, IAsyncDisposable
{
    public const long DefaultBudgetBytes = 800L * 1024 * 1024;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarnCooldown = TimeSpan.FromSeconds(30);

    // Same trigger band the old eviction path used — keeps the warning
    // threshold consistent so historical log comparisons still make sense.
    private const double ManagedHeapTriggerMultiple = 0.75;
    private const double WorkingSetTriggerMultiple = 1.08;
    private const long MinimumWorkingSetTriggerBytes = 864L * 1024 * 1024;

    private readonly ILogger<MemoryBudgetService>? _logger;
    private readonly IWindowContext? _windowContext;
    private readonly Process _process = Process.GetCurrentProcess();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset _lastWarnAt = DateTimeOffset.MinValue;
    private long _budgetBytes = DefaultBudgetBytes;

    public MemoryBudgetService(
        IWindowContext? windowContext = null,
        ILogger<MemoryBudgetService>? logger = null)
    {
        _windowContext = windowContext;
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
            "Memory budget monitor started (warn-only). Budget={BudgetMb:F0}MB workingSetTrigger={WorkingSetTriggerMb:F0}MB interval={Interval}",
            _budgetBytes / 1048576.0,
            WorkingSetTriggerBytes(_budgetBytes) / 1048576.0,
            CheckInterval);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
            {
                if (_windowContext?.IsUiPowerSaving == true)
                    continue;

                await CheckAsync().ConfigureAwait(false);
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

    private async Task CheckAsync()
    {
        var snapshot = Capture();
        if (!IsOverBudget(snapshot, _budgetBytes))
            return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastWarnAt < WarnCooldown)
            return;

        _lastWarnAt = now;
        _logger?.LogWarning(
            "Memory budget exceeded: workingSet={WorkingSetMb:F1}MB private={PrivateMb:F1}MB managed={ManagedMb:F1}MB budget={BudgetMb:F1}MB",
            snapshot.WorkingSetBytes / 1048576.0,
            snapshot.PrivateBytes / 1048576.0,
            snapshot.ManagedHeapBytes / 1048576.0,
            _budgetBytes / 1048576.0);

        await LogMemoryAttributionAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the <c>[mem-attribution]</c> breakdown line — managed heap by
    /// generation, image cache size, and the GPU surfaces held by cached
    /// pages. Only fires alongside the budget-exceeded warning so the
    /// breakdown is co-located with the spike that prompted it.
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
