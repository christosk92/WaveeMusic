using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Diagnostics;

/// <summary>
/// Live sampler for memory diagnostics. Reads process working set, GC stats,
/// per-cache counts, and a weak-ref count of in-flight ViewModels.
///
/// Used by the in-app Memory diagnostics panel (Settings → Diagnostics) to
/// expose what's retained between navigations. Polling is on-demand — the
/// panel calls <see cref="StartSampling"/> when shown and <see cref="StopSampling"/>
/// when hidden so we're not paying for the work otherwise.
/// </summary>
public sealed class MemoryDiagnosticsService
{
    private readonly ILogger<MemoryDiagnosticsService>? _logger;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;
    private int _refCount;

    // CPU delta tracking — Process.TotalProcessorTime is monotonic across all
    // cores, so % CPU = (delta processor ms) / (delta wall ms × cpu count) × 100.
    private readonly object _cpuLock = new();
    private TimeSpan _lastTotalProcessorTime;
    private DateTimeOffset _lastSampleAtUtc;
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

    public MemoryDiagnosticsService(ILogger<MemoryDiagnosticsService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Fired on each sample with the latest snapshot.</summary>
    public event Action<MemorySnapshot>? Sampled;

    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Begins periodic sampling on the UI dispatcher. Ref-counted: every Start
    /// must be balanced by a Stop. The first Start creates the timer; subsequent
    /// Starts just bump the count.
    /// </summary>
    public void StartSampling()
    {
        if (Interlocked.Increment(ref _refCount) != 1)
            return;

        // Grab the dispatcher at first start — must be called from the UI thread.
        _dispatcher ??= DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("MemoryDiagnosticsService.StartSampling must be called from a thread with a DispatcherQueue.");

        _timer ??= _dispatcher.CreateTimer();
        _timer.Interval = SampleInterval;
        _timer.IsRepeating = true;
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // Emit one immediate sample so the panel isn't blank until the first tick.
        EmitSample();
    }

    public void StopSampling()
    {
        if (Interlocked.Decrement(ref _refCount) > 0)
            return;

        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }
    }

    /// <summary>Force a single sample emission outside the polling loop.</summary>
    public MemorySnapshot SampleNow() => Capture();

    private void OnTimerTick(DispatcherQueueTimer sender, object args) => EmitSample();

    private void EmitSample()
    {
        try
        {
            var snapshot = Capture();
            Sampled?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Memory diagnostics sample failed");
        }
    }

    private MemorySnapshot Capture()
    {
        long workingSet = 0;
        long privateBytes = 0;
        TimeSpan totalProcTime = default;
        try
        {
            using var p = Process.GetCurrentProcess();
            workingSet = p.WorkingSet64;
            privateBytes = p.PrivateMemorySize64;
            totalProcTime = p.TotalProcessorTime;
        }
        catch { /* best-effort */ }

        // Compute % CPU since previous sample. Two consecutive samples close in
        // time give a real-time average; the very first sample after Start has
        // no baseline so we report 0.
        var nowUtc = DateTimeOffset.UtcNow;
        double cpuPct = 0;
        TimeSpan prevTotal;
        DateTimeOffset prevSampleAt;
        lock (_cpuLock)
        {
            prevTotal = _lastTotalProcessorTime;
            prevSampleAt = _lastSampleAtUtc;
            _lastTotalProcessorTime = totalProcTime;
            _lastSampleAtUtc = nowUtc;
        }
        if (prevSampleAt != default && totalProcTime > TimeSpan.Zero)
        {
            var deltaProcMs = (totalProcTime - prevTotal).TotalMilliseconds;
            var deltaWallMs = (nowUtc - prevSampleAt).TotalMilliseconds;
            if (deltaWallMs > 0)
                cpuPct = Math.Max(0, deltaProcMs / (deltaWallMs * _processorCount) * 100.0);
        }

        var managed = GC.GetTotalMemory(forceFullCollection: false);
        var info = GC.GetGCMemoryInfo();

        var caches = new List<CacheCount>();
        try
        {
            foreach (var c in Ioc.Default.GetServices<ICleanableCache>())
            {
                caches.Add(new CacheCount(c.CacheName, c.CurrentCount));
            }
        }
        catch (Exception)
        {
            // Container may be mid-rebuild on rare migration paths — surface zeros instead.
        }

        var imageCacheCount = Ioc.Default.GetService<ImageCacheService>()?.Count ?? 0;

        var tabCount = ShellViewModel.TabInstances.Count;
        var totalCachedPages = 0;
        foreach (var t in ShellViewModel.TabInstances)
        {
            try
            {
                var f = t.ContentFrame;
                // Currently-active page + back stack + forward stack approximates retained pages.
                if (f.Content != null) totalCachedPages++;
                totalCachedPages += f.BackStack.Count;
                totalCachedPages += f.ForwardStack.Count;
            }
            catch { /* TabBarItem may be in transition */ }
        }

        var liveVms = LiveInstanceTracker.Snapshot();

        return new MemorySnapshot(
            CapturedAtUtc: nowUtc,
            WorkingSetBytes: workingSet,
            PrivateBytes: privateBytes,
            ManagedHeapBytes: managed,
            HeapCommittedBytes: info.TotalCommittedBytes,
            HeapFragmentedBytes: info.FragmentedBytes,
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2),
            Caches: caches,
            ImageCacheCount: imageCacheCount,
            TabCount: tabCount,
            CachedPageCount: totalCachedPages,
            LiveViewModels: liveVms,
            TotalProcessorMs: totalProcTime.TotalMilliseconds,
            CpuSincePrevPct: cpuPct);
    }

    /// <summary>
    /// Append a single CSV row capturing the current snapshot to the diagnostics
    /// log under <c>%AppData%/Wavee/diag/memory-{date}.csv</c>. Called by the
    /// "Snapshot" button in the panel.
    /// </summary>
    public async Task<string> WriteSnapshotCsvAsync(string label, CancellationToken ct = default)
    {
        var snapshot = Capture();
        var dir = Path.Combine(AppPaths.AppDataDirectory, "diag");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"memory-{DateTime.UtcNow:yyyyMMdd}.csv");

        var newFile = !File.Exists(path);
        await using var sw = new StreamWriter(path, append: true, System.Text.Encoding.UTF8);
        if (newFile)
        {
            await sw.WriteLineAsync(
                "timestamp,label,workingSetMB,privateBytesMB,managedMB,heapCommittedMB,heapFragMB," +
                "gen0,gen1,gen2,imageCacheCount,tabCount,cachedPages," +
                "cpuTotalMs,cpuSincePrevPct," +
                "caches,liveVms").ConfigureAwait(false);
        }

        var cacheStr = string.Join("|", snapshot.Caches.Select(c => $"{c.Name}={c.Count}"));
        var vmStr = string.Join("|", snapshot.LiveViewModels.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        await sw.WriteLineAsync(string.Join(',',
            snapshot.CapturedAtUtc.ToString("O"),
            EscapeCsv(label),
            (snapshot.WorkingSetBytes / 1048576.0).ToString("F1"),
            (snapshot.PrivateBytes / 1048576.0).ToString("F1"),
            (snapshot.ManagedHeapBytes / 1048576.0).ToString("F1"),
            (snapshot.HeapCommittedBytes / 1048576.0).ToString("F1"),
            (snapshot.HeapFragmentedBytes / 1048576.0).ToString("F1"),
            snapshot.Gen0.ToString(),
            snapshot.Gen1.ToString(),
            snapshot.Gen2.ToString(),
            snapshot.ImageCacheCount.ToString(),
            snapshot.TabCount.ToString(),
            snapshot.CachedPageCount.ToString(),
            snapshot.TotalProcessorMs.ToString("F0"),
            snapshot.CpuSincePrevPct.ToString("F2"),
            EscapeCsv(cacheStr),
            EscapeCsv(vmStr))).ConfigureAwait(false);

        _logger?.LogInformation(
            "Memory snapshot [{Label}]: WS={WSMb:F1}MB managed={ManagedMb:F1}MB tabs={Tabs} pages={Pages}",
            label, snapshot.WorkingSetBytes / 1048576.0, snapshot.ManagedHeapBytes / 1048576.0,
            snapshot.TabCount, snapshot.CachedPageCount);

        return path;
    }

    private static string EscapeCsv(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}

public readonly record struct CacheCount(string Name, int Count);

public sealed record MemorySnapshot(
    DateTimeOffset CapturedAtUtc,
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedHeapBytes,
    long HeapCommittedBytes,
    long HeapFragmentedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    IReadOnlyList<CacheCount> Caches,
    int ImageCacheCount,
    int TabCount,
    int CachedPageCount,
    IReadOnlyDictionary<string, int> LiveViewModels,
    double TotalProcessorMs,
    double CpuSincePrevPct);
