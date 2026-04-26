using System;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Diagnostics;

/// <summary>
/// View-model behind <c>MemoryDiagnosticsCard</c>. Bridges the live
/// <see cref="MemoryDiagnosticsService"/> samples into bindable strings, and
/// exposes the action commands (Force GC, Clear caches, Snapshot, …) the panel
/// uses to drive memory experiments while diagnosing leaks.
/// </summary>
public sealed partial class MemoryDiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly MemoryDiagnosticsService _service;
    private readonly ILogger<MemoryDiagnosticsViewModel>? _logger;
    private bool _started;
    private bool _disposed;

    [ObservableProperty] private string _workingSetMb = "—";
    [ObservableProperty] private string _managedMb = "—";
    [ObservableProperty] private string _heapCommittedMb = "—";
    [ObservableProperty] private string _heapFragmentedMb = "—";
    [ObservableProperty] private string _gcCounts = "—";
    [ObservableProperty] private string _cpuPercent = "—";
    [ObservableProperty] private int _imageCacheCount;
    [ObservableProperty] private int _tabCount;
    [ObservableProperty] private int _cachedPageCount;
    [ObservableProperty] private string _cacheBreakdown = "—";
    [ObservableProperty] private string _liveViewModels = "—";
    [ObservableProperty] private string _lastAction = "";

    public MemoryDiagnosticsViewModel(
        MemoryDiagnosticsService service,
        ILogger<MemoryDiagnosticsViewModel>? logger = null)
    {
        _service = service;
        _logger = logger;
        _service.Sampled += OnSampled;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _service.StartSampling();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _service.StopSampling();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _service.Sampled -= OnSampled;
    }

    private void OnSampled(MemorySnapshot s)
    {
        WorkingSetMb = $"{s.WorkingSetBytes / 1048576.0:F1} MB (private {s.PrivateBytes / 1048576.0:F0} MB)";
        ManagedMb = $"{s.ManagedHeapBytes / 1048576.0:F1} MB";
        HeapCommittedMb = $"{s.HeapCommittedBytes / 1048576.0:F1} MB";
        HeapFragmentedMb = $"{s.HeapFragmentedBytes / 1048576.0:F1} MB";
        GcCounts = $"Gen0 {s.Gen0} · Gen1 {s.Gen1} · Gen2 {s.Gen2}";
        // CpuSincePrevPct is normalized across all logical cores — matches what
        // Task Manager shows in its "CPU" column for this process.
        CpuPercent = s.CpuSincePrevPct > 0
            ? $"{s.CpuSincePrevPct:F1}% (total {s.TotalProcessorMs / 1000.0:F0}s)"
            : "—";
        ImageCacheCount = s.ImageCacheCount;
        TabCount = s.TabCount;
        CachedPageCount = s.CachedPageCount;
        CacheBreakdown = s.Caches.Count == 0
            ? "(none)"
            : string.Join("\n", s.Caches.Select(c => $"{c.Name}: {c.Count}"));
        LiveViewModels = s.LiveViewModels.Count == 0
            ? "(none)"
            : string.Join("\n", s.LiveViewModels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    [RelayCommand]
    private void ForceGc()
    {
        var beforeMb = GC.GetTotalMemory(false) / 1048576.0;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var afterMb = GC.GetTotalMemory(false) / 1048576.0;
        SetAction($"Forced GC: {beforeMb:F1} → {afterMb:F1} MB managed");
        _service.SampleNow();
    }

    [RelayCommand]
    private void ReleaseWorkingSet()
    {
        var logger = Ioc.Default.GetService<ILogger<MemoryDiagnosticsViewModel>>();
        MemoryReleaseHelper.ReleaseWorkingSet(logger, "diagnostics");
        SetAction("Released working set (managed GC + finalizers)");
        _service.SampleNow();
    }

    [RelayCommand]
    private async Task ClearImageCacheAsync()
    {
        var svc = Ioc.Default.GetService<ImageCacheService>();
        var before = svc?.Count ?? 0;
        svc?.Clear();
        await Task.CompletedTask;
        SetAction($"Cleared image cache ({before} entries)");
        _service.SampleNow();
    }

    [RelayCommand]
    private async Task ClearAllCachesAsync()
    {
        var totalBefore = 0;
        var totalCleared = 0;
        try
        {
            foreach (var c in Ioc.Default.GetServices<ICleanableCache>())
            {
                totalBefore += c.CurrentCount;
                totalCleared += await c.ClearAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ClearAllCaches failed");
        }
        SetAction($"Cleared all caches ({totalCleared} of {totalBefore} entries)");
        _service.SampleNow();
    }

    [RelayCommand]
    private void DropTabCache()
    {
        var dropped = 0;
        foreach (var tab in ShellViewModel.TabInstances)
        {
            try
            {
                var f = tab.ContentFrame;
                var prior = f.CacheSize;
                f.CacheSize = 0;
                // Restoring the size doesn't bring back the dropped pages — they were
                // evicted on the cycle. Set it back so future navigations cache as before.
                f.CacheSize = prior;
                dropped++;
            }
            catch { /* best-effort */ }
        }
        SetAction($"Dropped page cache on {dropped} tabs");
        _service.SampleNow();
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        try
        {
            var path = await _service.WriteSnapshotCsvAsync(label: "manual");
            SetAction($"Snapshot appended → {path}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Snapshot failed");
            SetAction($"Snapshot failed: {ex.Message}");
        }
    }

    private void SetAction(string message)
    {
        LastAction = $"{DateTime.Now:HH:mm:ss}  {message}";
    }
}
