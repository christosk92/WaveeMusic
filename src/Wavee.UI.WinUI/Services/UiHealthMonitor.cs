using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Controls.TabBar;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Monitors UI responsiveness with two signals:
/// 1) Render cadence via CompositionTarget.Rendering (for FPS)
/// 2) Dispatcher timer latency (for UI stall detection)
/// </summary>
internal sealed partial class UiHealthMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private DispatcherQueueTimer? _timer;

    // ── Configuration ──
    private const int TickIntervalMs = 16; // ~60 fps target
    public int WarnThresholdMs { get; set; } = 50;
    public int CriticalThresholdMs { get; set; } = 100;
    // A single very-long freeze (3 s+) is rare enough that one is worth a
    // prompt on its own — anything shorter is normal cold-start noise.
    private const int SevereFreezeThresholdMs = 3000;
    // "We noticed the app might be working slower" requires a sustained
    // pattern: at least this many critical-threshold frames inside a
    // rolling window of DegradedCriticalFrameWindow. Tuned so cold-start
    // GC bursts and a single 20 s DB lock don't trip it — the user has to
    // experience minutes of jank for it to fire.
    private const int DegradedCriticalFrameThreshold = 60;
    private static readonly TimeSpan DegradedCriticalFrameWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DegradedPromptCooldown = TimeSpan.FromHours(1);

    // ── State ──
    private long _lastTickTimestamp;
    private readonly object _statsLock = new();

    // Rolling window for dispatcher tick latency (stall detection)
    private readonly Queue<double> _frameDurations = new(64);
    private const int MaxFrameSamples = 60;

    // Rolling window for actual render cadence (FPS)
    private readonly Queue<double> _renderFrameDurations = new(128);
    private const int MaxRenderSamples = 120;
    private long _lastRenderTimestamp;

    // History ring buffer for graph (last N seconds of per-tick data)
    private const int HistorySize = 300; // ~5 seconds at 60fps
    private readonly double[] _history = new double[HistorySize];
    private int _historyHead;
    private int _historyCount;

    // Lifetime stats
    private double _worstFrameMs;
    private int _stallCount;
    private int _criticalCount;
    private int _totalFrames;
    // Sliding window of Stopwatch timestamps for critical-threshold frames.
    // Pruned at every evaluation to the last DegradedCriticalFrameWindow so
    // the count reflects sustained recent jank, not cumulative noise since
    // app start.
    private readonly Queue<long> _criticalFrameTimestamps = new();
    private long _lastDegradedPromptTimestamp;

    // Cached current-process handle. The overlay polls every render frame when
    // active — allocating a Process wrapper per call adds finalizer pressure.
    private static readonly Process _selfProcess = Process.GetCurrentProcess();

    // GC tracking (sampled every tick)
    private int _lastGen0, _lastGen1, _lastGen2;
    private int _gcGen0Total, _gcGen1Total, _gcGen2Total;
    private int _gen2DuringStallCount; // Gen2 collections that coincided with stalls

    // Managed heap size at the previous tick — diff is reported into the
    // NavigationDiagnostics [gc] line as an allocation-since-last-tick proxy
    // (signed: negative deltas mean a Gen0/Gen1 fired between this tick and the
    // previous one and reclaimed bytes).
    private long _lastManagedBytes;

    // Live-Shimmer leak indicator. NavCacheSurfaces maintains a weak-reference
    // registry of every Shimmer it has visited; we sample it on a slow cadence
    // because GetLiveShimmerCount() walks (and prunes) that list under a lock.
    // A high count points at cached pages keeping skeleton subtrees realized
    // — the leak class fixed by ShimmerLoadGate's IsLoaded=false unrealize and
    // NavCacheSurfaces' Shimmer.IsActive deactivation.
    private const int ShimmerSampleEveryTicks = 1875; // ~30 s at 16 ms tick
    private const int ShimmerWarnThreshold = 500;
    private static readonly TimeSpan ShimmerWarnCooldown = TimeSpan.FromMinutes(5);
    private int _ticksSinceShimmerSample;
    private int _lastShimmerCount;
    private long _lastShimmerWarnTimestamp;

    // CompositionImage live-peer counter. Same cadence as the Shimmer sample.
    // "Total" is every CompositionImage that has been OnLoaded since startup
    // (weak refs, pruned on read); "WithLivePeer" excludes those whose
    // composition resources have been torn down (nav-cache release or the
    // deferred OnUnloaded release). A high TotalLivePeer count points at
    // ItemsRepeater recycle pools or cached-page items the release walk missed.
    private int _lastCompositionImageTotal;
    private int _lastCompositionImageWithLivePeer;
    private long _lastCompositionImageBytes;

    public UiHealthMonitor(DispatcherQueue dispatcherQueue, ILogger? logger = null)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _logger = logger;
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);
        _lastManagedBytes = GC.GetTotalMemory(false);
    }

    public event EventHandler<UiDegradationDetectedEventArgs>? Degraded;

    public void Start()
    {
        return;
        if (_timer != null) return;

        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _lastRenderTimestamp = _lastTickTimestamp;
        _timer = _dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        _timer.Tick += OnTick;
        _timer.Start();
        CompositionTarget.Rendering += OnRendering;

        _logger?.LogInformation("UiHealthMonitor started (tick={TickMs}ms, warn={WarnMs}ms, crit={CritMs}ms)",
            TickIntervalMs, WarnThresholdMs, CriticalThresholdMs);
    }

    public void Stop()
    {
        if (_timer == null) return;

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        CompositionTarget.Rendering -= OnRendering;

        _logger?.LogInformation(
            "UiHealthMonitor stopped — total frames={Total}, stalls={Stalls}, critical={Critical}, worst={WorstMs:F1}ms",
            _totalFrames, _stallCount, _criticalCount, _worstFrameMs);
    }

    private void OnRendering(object sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastRenderTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastRenderTimestamp = now;

        // Ignore first sample and large gaps (window minimized/suspended).
        if (elapsedMs <= 0 || elapsedMs > 250)
            return;

        lock (_statsLock)
        {
            if (_renderFrameDurations.Count >= MaxRenderSamples)
                _renderFrameDurations.Dequeue();
            _renderFrameDurations.Enqueue(elapsedMs);
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastTickTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastTickTimestamp = now;

        UiDegradationDetectedEventArgs? degradedArgs = null;
        lock (_statsLock)
        {
            _totalFrames++;

            // Rolling window for FPS
            if (_frameDurations.Count >= MaxFrameSamples)
                _frameDurations.Dequeue();
            _frameDurations.Enqueue(elapsedMs);

            // History ring buffer for graph
            _history[_historyHead] = elapsedMs;
            _historyHead = (_historyHead + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;

            if (elapsedMs > _worstFrameMs)
                _worstFrameMs = elapsedMs;

            // Sample GC
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var gen0Delta = g0 - _lastGen0;
            var gen1Delta = g1 - _lastGen1;
            var gen2Delta = g2 - _lastGen2;
            _gcGen0Total += gen0Delta;
            _gcGen1Total += gen1Delta;
            _gcGen2Total += gen2Delta;
            _lastGen0 = g0;
            _lastGen1 = g1;
            _lastGen2 = g2;

            // Per-collection [gc] line into NavigationDiagnostics. Sampling at
            // 16 ms can coalesce multiple collections of the same gen into one
            // delta entry — we report each observed delta as a single record
            // for that gen (the actual count is preserved in _gcGen*Total).
            if (gen0Delta > 0 || gen1Delta > 0 || gen2Delta > 0)
            {
                var managedNow = GC.GetTotalMemory(false);
                var allocSinceMb = (managedNow - _lastManagedBytes) / 1048576.0;
                _lastManagedBytes = managedNow;

                var nav = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance;
                if (nav != null)
                {
                    if (gen0Delta > 0) nav.RecordGc(0, allocSinceMb);
                    if (gen1Delta > 0) nav.RecordGc(1, allocSinceMb);
                    if (gen2Delta > 0) nav.RecordGc(2, allocSinceMb);
                }
            }

            if (elapsedMs > CriticalThresholdMs)
            {
                _criticalCount++;
                _stallCount++;
                if (gen2Delta > 0)
                {
                    _gen2DuringStallCount += gen2Delta;
                    // gen2Delta>0 only means a Gen2 count incremented somewhere in
                    // this ~16ms window — NOT that the GC blocked the UI thread.
                    // Report the GC's REAL stop-the-world pause + whether it was a
                    // concurrent (background) collection, so a coincident background
                    // GC isn't mistaken for the cause of the stall.
                    var (gcBlockedMs, concurrent, gcGen) = LatestGcPause();
                    _logger?.LogError(
                        "UI CRITICAL STALL: {ElapsedMs:F0}ms (frame #{Frame}) — Gen2 GC coincided (concurrent={Concurrent}, gcGen={GcGen}, gcBlockedMs={GcBlockedMs:F1}; GC accounts for {Pct:F0}% of the stall)",
                        elapsedMs, _totalFrames, concurrent, gcGen, gcBlockedMs,
                        elapsedMs > 0 ? Math.Min(100.0, gcBlockedMs / elapsedMs * 100.0) : 0);
                }
                else
                {
                    _logger?.LogError("UI CRITICAL STALL: {ElapsedMs:F0}ms (frame #{Frame})", elapsedMs, _totalFrames);
                }
                // Hand off to NavigationDiagnostics for a correlated snapshot
                // (recent navs + last memory release + page-fault deltas).
                Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.OnUiStallDetected(
                    elapsedMs, _totalFrames, gen2Delta);
            }
            else if (elapsedMs > WarnThresholdMs)
            {
                _stallCount++;
                if (gen2Delta > 0)
                {
                    _gen2DuringStallCount += gen2Delta;
                    var (gcBlockedMs, concurrent, gcGen) = LatestGcPause();
                    _logger?.LogWarning(
                        "UI stall: {ElapsedMs:F0}ms (frame #{Frame}) — Gen2 GC coincided (concurrent={Concurrent}, gcGen={GcGen}, gcBlockedMs={GcBlockedMs:F1})",
                        elapsedMs, _totalFrames, concurrent, gcGen, gcBlockedMs);
                }
                else
                {
                    _logger?.LogWarning("UI stall: {ElapsedMs:F0}ms (frame #{Frame})", elapsedMs, _totalFrames);
                }
            }
            else if (gen2Delta > 0)
            {
                _logger?.LogDebug("Gen2 GC observed (frame #{Frame}, tick={ElapsedMs:F1}ms, no stall)", _totalFrames, elapsedMs);
            }

            degradedArgs = EvaluateDegradationLocked(elapsedMs, now);
        }

        if (degradedArgs is not null)
            Degraded?.Invoke(this, degradedArgs);

        SampleShimmerLeakIndicator(now);
    }

    /// <summary>
    /// Real stop-the-world pause of the most recent GC, plus whether it ran
    /// concurrently (background) and which generation. <see cref="GCMemoryInfo.PauseDurations"/>
    /// is the actual time the runtime suspended managed threads — for a background
    /// Gen2 under ServerGC these are typically sub-millisecond, so a 100ms+ tick
    /// gap tagged "Gen2" is almost always something else (layout, our own code, or
    /// — under the debugger — synchronous Output writes / first-chance overhead),
    /// not the collection. Lets the log show what fraction of a stall the GC can
    /// actually account for instead of blaming it on coincidence.
    /// </summary>
    private static (double BlockedMs, bool Concurrent, int Generation) LatestGcPause()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            double pauseMs = 0;
            foreach (var p in info.PauseDurations)
                pauseMs += p.TotalMilliseconds;
            return (pauseMs, info.Concurrent, info.Generation);
        }
        catch
        {
            return (0, false, -1);
        }
    }

    private void SampleShimmerLeakIndicator(long timestamp)
    {
        // Slow cadence — GetLiveShimmerCount() takes a lock and prunes the
        // weak-ref registry, so we don't pay for it every UI tick.
        if (++_ticksSinceShimmerSample < ShimmerSampleEveryTicks)
            return;
        _ticksSinceShimmerSample = 0;

        _lastShimmerCount = NavCacheSurfaces.GetLiveShimmerCount();

        var (total, withPeer, bytes) = CompositionImage.GetDiagnosticCounts();
        _lastCompositionImageTotal = total;
        _lastCompositionImageWithLivePeer = withPeer;
        _lastCompositionImageBytes = bytes;

        if (_lastShimmerCount < ShimmerWarnThreshold)
            return;

        if (_lastShimmerWarnTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastShimmerWarnTimestamp, timestamp) < ShimmerWarnCooldown)
            return;

        _lastShimmerWarnTimestamp = timestamp;
        _logger?.LogWarning(
            "Live Shimmer count {Count} exceeds threshold {Threshold} — cached-page skeleton trees likely leaking; expect inflated working-set",
            _lastShimmerCount, ShimmerWarnThreshold);
    }

    private UiDegradationDetectedEventArgs? EvaluateDegradationLocked(double elapsedMs, long timestamp)
    {
        if (elapsedMs > CriticalThresholdMs)
            _criticalFrameTimestamps.Enqueue(timestamp);

        // Prune anything outside the rolling window. Both the threshold
        // check and the next "should I prompt" decision read this trimmed
        // count, so a quiet hour wipes any earlier accumulation.
        while (_criticalFrameTimestamps.Count > 0
               && Stopwatch.GetElapsedTime(_criticalFrameTimestamps.Peek(), timestamp) > DegradedCriticalFrameWindow)
        {
            _criticalFrameTimestamps.Dequeue();
        }

        if (_lastDegradedPromptTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastDegradedPromptTimestamp, timestamp) < DegradedPromptCooldown)
        {
            return null;
        }

        var severeFreeze = elapsedMs >= SevereFreezeThresholdMs;
        var criticalFrames = _criticalFrameTimestamps.Count;
        var repeatedCriticalFrames = criticalFrames >= DegradedCriticalFrameThreshold;
        if (!severeFreeze && !repeatedCriticalFrames)
            return null;

        _lastDegradedPromptTimestamp = timestamp;
        // Clear the window after a prompt so the next one needs a fresh
        // pattern, not a long-tail of frames that already contributed.
        _criticalFrameTimestamps.Clear();

        return new UiDegradationDetectedEventArgs(
            elapsedMs,
            criticalFrames,
            severeFreeze ? "severe-freeze" : "repeated-critical-stalls");
    }

    /// <summary>
    /// Gets a snapshot of current UI health statistics.
    /// </summary>
    public UiHealthStats CurrentStats
    {
        get
        {
            lock (_statsLock)
            {
                double uiAvgMs = 0;
                double uiMaxRecentMs = 0;
                foreach (var d in _frameDurations)
                {
                    uiAvgMs += d;
                    if (d > uiMaxRecentMs) uiMaxRecentMs = d;
                }

                if (_frameDurations.Count > 0)
                    uiAvgMs /= _frameDurations.Count;

                double renderAvgMs = 0;
                double renderMaxRecentMs = 0;
                foreach (var d in _renderFrameDurations)
                {
                    renderAvgMs += d;
                    if (d > renderMaxRecentMs) renderMaxRecentMs = d;
                }

                if (_renderFrameDurations.Count > 0)
                    renderAvgMs /= _renderFrameDurations.Count;

                var avgForFps = renderAvgMs > 0 ? renderAvgMs : uiAvgMs;
                var fps = avgForFps > 0 ? 1000.0 / avgForFps : 0;
                var managedMb = GC.GetTotalMemory(false) / 1048576.0;
                double workingSetMb = 0;
                double privateMb = 0;
                try
                {
                    // Cached self-Process; Refresh re-queries OS metrics. Avoids
                    // per-frame finalizable Process allocation.
                    _selfProcess.Refresh();
                    workingSetMb = _selfProcess.WorkingSet64 / 1048576.0;
                    privateMb = _selfProcess.PrivateMemorySize64 / 1048576.0;
                }
                catch
                {
                    // Diagnostics-only; keep the overlay alive if process counters fail.
                }

                return new UiHealthStats
                {
                    Fps = fps,
                    AvgFrameMs = avgForFps,
                    WorstFrameMs = _worstFrameMs,
                    WorstRecentFrameMs = Math.Max(renderMaxRecentMs, uiMaxRecentMs),
                    StallCount = _stallCount,
                    CriticalCount = _criticalCount,
                    TotalFrames = _totalFrames,
                    UiTickAvgMs = uiAvgMs,
                    GcGen0 = _gcGen0Total,
                    GcGen1 = _gcGen1Total,
                    GcGen2 = _gcGen2Total,
                    Gen2DuringStalls = _gen2DuringStallCount,
                    ManagedMb = managedMb,
                    WorkingSetMb = workingSetMb,
                    PrivateMb = privateMb,
                    LiveShimmerCount = _lastShimmerCount,
                    CompositionImageTotal = _lastCompositionImageTotal,
                    CompositionImageWithLivePeer = _lastCompositionImageWithLivePeer,
                    CompositionImageEstimatedSurfaceBytes = _lastCompositionImageBytes,
                };
            }
        }
    }

    /// <summary>
    /// Copies the history ring buffer into <paramref name="destination"/> (oldest first).
    /// Returns the number of samples written.
    /// </summary>
    public int CopyHistory(double[] destination)
    {
        lock (_statsLock)
        {
            var count = Math.Min(_historyCount, destination.Length);
            var start = (_historyHead - _historyCount + HistorySize) % HistorySize;
            for (int i = 0; i < count; i++)
                destination[i] = _history[(start + i) % HistorySize];
            return count;
        }
    }

    /// <summary>
    /// Generates a text report suitable for clipboard copy.
    /// </summary>
    public string GenerateReport()
    {
        var s = CurrentStats;
        var sb = new StringBuilder();
        sb.AppendLine("=== Wavee UI Health Report ===");
        sb.AppendLine($"FPS:              {s.Fps:F1} (render)");
        sb.AppendLine($"Avg frame:        {s.AvgFrameMs:F1} ms (render)");
        sb.AppendLine($"UI tick avg:      {s.UiTickAvgMs:F1} ms");
        sb.AppendLine($"Worst (recent):   {s.WorstRecentFrameMs:F0} ms");
        sb.AppendLine($"Worst (all-time): {s.WorstFrameMs:F0} ms");
        sb.AppendLine($"Stalls (>50ms):   {s.StallCount}");
        sb.AppendLine($"Critical (>150ms):{s.CriticalCount}");
        sb.AppendLine($"Total frames:     {s.TotalFrames}");
        sb.AppendLine();
        sb.AppendLine($"--- GC Collections ---");
        sb.AppendLine($"Gen0: {s.GcGen0}  Gen1: {s.GcGen1}  Gen2: {s.GcGen2}  Gen2-during-stalls: {s.Gen2DuringStalls}");
        sb.AppendLine($"Managed: {s.ManagedMb:F1} MB  Working set: {s.WorkingSetMb:F1} MB  Private: {s.PrivateMb:F1} MB");
        sb.AppendLine($"Live Shimmer instances: {s.LiveShimmerCount} (warn at >= {ShimmerWarnThreshold})");
        sb.AppendLine(
            $"CompositionImage: total={s.CompositionImageTotal} withLivePeer={s.CompositionImageWithLivePeer} estSurfaceMB={s.CompositionImageEstimatedSurfaceBytes / 1048576.0:F1}");

        // Append profiler stats if available
        UiOperationProfiler.Instance?.AppendReport(sb);
        sb.AppendLine();

        // Append last 60 frame durations
        sb.AppendLine("--- Recent frame durations (ms) ---");
        lock (_statsLock)
        {
            int i = 0;
            foreach (var d in _frameDurations)
            {
                sb.Append($"{d:F1}");
                sb.Append(++i % 10 == 0 ? '\n' : '\t');
            }
        }
        return sb.ToString();
    }

    public void ResetStats()
    {
        lock (_statsLock)
        {
            _worstFrameMs = 0;
            _stallCount = 0;
            _criticalCount = 0;
            _totalFrames = 0;
            _criticalFrameTimestamps.Clear();
            _lastDegradedPromptTimestamp = 0;
            _frameDurations.Clear();
            _renderFrameDurations.Clear();
            _historyCount = 0;
            _historyHead = 0;
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
            _lastTickTimestamp = _lastRenderTimestamp;
            _gcGen0Total = 0;
            _gcGen1Total = 0;
            _gcGen2Total = 0;
            _gen2DuringStallCount = 0;
            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _lastManagedBytes = GC.GetTotalMemory(false);
            _ticksSinceShimmerSample = 0;
            _lastShimmerCount = 0;
            _lastShimmerWarnTimestamp = 0;
            _lastCompositionImageTotal = 0;
            _lastCompositionImageWithLivePeer = 0;
            _lastCompositionImageBytes = 0;
        }
        UiOperationProfiler.Instance?.Reset();
    }

    public void Dispose() => Stop();
}

internal record struct UiHealthStats
{
    public double Fps { get; init; }
    public double AvgFrameMs { get; init; }
    public double UiTickAvgMs { get; init; }
    public double WorstFrameMs { get; init; }
    public double WorstRecentFrameMs { get; init; }
    public int StallCount { get; init; }
    public int CriticalCount { get; init; }
    public int TotalFrames { get; init; }
    public int GcGen0 { get; init; }
    public int GcGen1 { get; init; }
    public int GcGen2 { get; init; }
    public int Gen2DuringStalls { get; init; }
    public double ManagedMb { get; init; }
    public double WorkingSetMb { get; init; }
    public double PrivateMb { get; init; }
    public int LiveShimmerCount { get; init; }
    public int CompositionImageTotal { get; init; }
    public int CompositionImageWithLivePeer { get; init; }
    public long CompositionImageEstimatedSurfaceBytes { get; init; }
}

internal sealed record UiDegradationDetectedEventArgs(
    double LastFrameMs,
    int CriticalFrames,
    string Reason);
