using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Monitors UI responsiveness with two signals:
/// 1) Render cadence via CompositionTarget.Rendering (for FPS)
/// 2) Dispatcher timer latency (for UI stall detection)
/// </summary>
internal sealed class UiHealthMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private DispatcherQueueTimer? _timer;

    // ── Configuration ──
    private const int TickIntervalMs = 16; // ~60 fps target
    public int WarnThresholdMs { get; set; } = 50;
    public int CriticalThresholdMs { get; set; } = 150;

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

    // Cached current-process handle. The overlay polls every render frame when
    // active — allocating a Process wrapper per call adds finalizer pressure.
    private static readonly Process _selfProcess = Process.GetCurrentProcess();

    // GC tracking (sampled every tick)
    private int _lastGen0, _lastGen1, _lastGen2;
    private int _gcGen0Total, _gcGen1Total, _gcGen2Total;
    private int _gen2DuringStallCount; // Gen2 collections that coincided with stalls

    public UiHealthMonitor(DispatcherQueue dispatcherQueue, ILogger? logger = null)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _logger = logger;
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);
    }

    public void Start()
    {
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

            if (elapsedMs > CriticalThresholdMs)
            {
                _criticalCount++;
                _stallCount++;
                if (gen2Delta > 0)
                {
                    _gen2DuringStallCount += gen2Delta;
                    _logger?.LogError("UI CRITICAL STALL: {ElapsedMs:F0}ms (frame #{Frame}) — Gen2 GC detected!", elapsedMs, _totalFrames);
                }
                else
                {
                    _logger?.LogError("UI CRITICAL STALL: {ElapsedMs:F0}ms (frame #{Frame})", elapsedMs, _totalFrames);
                }
            }
            else if (elapsedMs > WarnThresholdMs)
            {
                _stallCount++;
                if (gen2Delta > 0)
                {
                    _gen2DuringStallCount += gen2Delta;
                    _logger?.LogWarning("UI stall: {ElapsedMs:F0}ms (frame #{Frame}) — Gen2 GC detected", elapsedMs, _totalFrames);
                }
                else
                {
                    _logger?.LogWarning("UI stall: {ElapsedMs:F0}ms (frame #{Frame})", elapsedMs, _totalFrames);
                }
            }
            else if (gen2Delta > 0)
            {
                _logger?.LogDebug("Gen2 GC detected (frame #{Frame}, tick={ElapsedMs:F1}ms)", _totalFrames, elapsedMs);
            }
        }
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
}
