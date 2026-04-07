using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Lightweight UI operation profiler. Wraps key operations with timing
/// and tracks per-operation stats, GC pressure, and top-N slowest operations.
/// All profiling calls happen on the UI thread — no sync needed for stats.
/// </summary>
internal sealed class UiOperationProfiler
{
    /// <summary>
    /// Global instance set on app start for static access in hot paths
    /// (converters, extensions) without DI lookup overhead.
    /// </summary>
    public static UiOperationProfiler? Instance { get; set; }

    private readonly ILogger? _logger;
    private readonly Dictionary<string, OperationStats> _stats = new();

    // Top-N slowest operations (rolling, newest replaces oldest when full)
    private const int TopSlowestSize = 10;
    private readonly SlowestEntry[] _topSlowest = new SlowestEntry[TopSlowestSize];
    private int _topSlowestCount;
    private double _topSlowestMinMs; // smallest value in the top-N array

    // GC tracking
    private int _gen0AtLastSample, _gen1AtLastSample, _gen2AtLastSample;
    private int _gen0Total, _gen1Total, _gen2Total;

    // Configurable threshold — only log operations slower than this
    public double LogThresholdMs { get; set; } = 5.0;

    // Audio underrun tracking (set externally by whoever has access to PortAudioSink)
    private Func<long>? _audioUnderrunCountProvider;

    public UiOperationProfiler(ILogger? logger = null)
    {
        _logger = logger;
        _gen0AtLastSample = GC.CollectionCount(0);
        _gen1AtLastSample = GC.CollectionCount(1);
        _gen2AtLastSample = GC.CollectionCount(2);
    }

    /// <summary>
    /// Registers a callback that returns the current audio underrun count.
    /// </summary>
    public void SetAudioUnderrunProvider(Func<long> provider) => _audioUnderrunCountProvider = provider;

    /// <summary>
    /// Starts a profiling scope. Returns a struct (no allocation).
    /// On dispose, records duration. If above threshold, logs it.
    /// </summary>
    public ProfileScope Profile(string operationName)
        => new(this, operationName, Stopwatch.GetTimestamp());

    /// <summary>
    /// Samples GC collection counts since last call. Returns delta.
    /// Call once per frame from UiHealthMonitor.
    /// </summary>
    public GcDelta SampleGc()
    {
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);

        var delta = new GcDelta(
            g0 - _gen0AtLastSample,
            g1 - _gen1AtLastSample,
            g2 - _gen2AtLastSample);

        _gen0Total += delta.Gen0;
        _gen1Total += delta.Gen1;
        _gen2Total += delta.Gen2;

        _gen0AtLastSample = g0;
        _gen1AtLastSample = g1;
        _gen2AtLastSample = g2;

        return delta;
    }

    /// <summary>
    /// Gets cumulative GC counts since profiler creation.
    /// </summary>
    public GcDelta CumulativeGc => new(_gen0Total, _gen1Total, _gen2Total);

    /// <summary>
    /// Gets current audio underrun count (0 if no provider registered).
    /// </summary>
    public long AudioUnderrunCount => _audioUnderrunCountProvider?.Invoke() ?? 0;

    /// <summary>
    /// Appends profiler stats to a StringBuilder for the health report.
    /// </summary>
    public void AppendReport(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("--- GC Collections ---");
        sb.AppendLine($"Gen0: {_gen0Total}  Gen1: {_gen1Total}  Gen2: {_gen2Total}");

        var underruns = AudioUnderrunCount;
        if (underruns > 0 || _audioUnderrunCountProvider != null)
        {
            sb.AppendLine();
            sb.AppendLine("--- Audio Health ---");
            sb.AppendLine($"Underrun count: {underruns}");
        }

        // Top slowest operations by max time
        if (_stats.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- Operation Stats ---");

            // Sort by max descending, take top 8
            var sorted = new List<KeyValuePair<string, OperationStats>>(_stats);
            sorted.Sort((a, b) => b.Value.MaxMs.CompareTo(a.Value.MaxMs));

            var count = Math.Min(sorted.Count, 8);
            for (int i = 0; i < count; i++)
            {
                var (name, s) = (sorted[i].Key, sorted[i].Value);
                var avgMs = s.Count > 0 ? s.TotalMs / s.Count : 0;
                sb.AppendLine($"  {name,-30} max={s.MaxMs:F1}ms  avg={avgMs:F1}ms  n={s.Count}");
            }
        }

        // Recent slowest individual calls
        if (_topSlowestCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- Recent Slowest Calls ---");

            // Copy and sort by duration descending
            var entries = new SlowestEntry[_topSlowestCount];
            Array.Copy(_topSlowest, entries, _topSlowestCount);
            Array.Sort(entries, (a, b) => b.DurationMs.CompareTo(a.DurationMs));

            var count = Math.Min(entries.Length, 5);
            for (int i = 0; i < count; i++)
            {
                var e = entries[i];
                sb.AppendLine($"  {e.Name,-30} {e.DurationMs:F1}ms");
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of all operation stats for overlay display.
    /// </summary>
    public IReadOnlyList<OperationStatSnapshot> GetTopOperations(int maxCount = 5)
    {
        if (_stats.Count == 0) return Array.Empty<OperationStatSnapshot>();

        var sorted = new List<KeyValuePair<string, OperationStats>>(_stats);
        sorted.Sort((a, b) => b.Value.MaxMs.CompareTo(a.Value.MaxMs));

        var count = Math.Min(sorted.Count, maxCount);
        var result = new OperationStatSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            var (name, s) = (sorted[i].Key, sorted[i].Value);
            result[i] = new OperationStatSnapshot(
                name, s.MaxMs, s.Count > 0 ? s.TotalMs / s.Count : 0, s.Count);
        }
        return result;
    }

    /// <summary>
    /// Resets all accumulated stats.
    /// </summary>
    public void Reset()
    {
        _stats.Clear();
        _topSlowestCount = 0;
        _topSlowestMinMs = 0;
        _gen0Total = 0;
        _gen1Total = 0;
        _gen2Total = 0;
        _gen0AtLastSample = GC.CollectionCount(0);
        _gen1AtLastSample = GC.CollectionCount(1);
        _gen2AtLastSample = GC.CollectionCount(2);
    }

    // Called by ProfileScope.Dispose
    internal void RecordOperation(string name, double durationMs)
    {
        // Update per-operation stats
        if (!_stats.TryGetValue(name, out var stats))
        {
            stats = new OperationStats();
            _stats[name] = stats;
        }
        stats.Count++;
        stats.TotalMs += durationMs;
        if (durationMs > stats.MaxMs)
            stats.MaxMs = durationMs;

        // Update top-N slowest
        if (durationMs > _topSlowestMinMs || _topSlowestCount < TopSlowestSize)
        {
            InsertSlowest(name, durationMs);
        }

        // Log if above threshold
        if (durationMs >= LogThresholdMs)
        {
            _logger?.LogWarning("UI op [{Operation}] took {DurationMs:F1}ms", name, durationMs);
        }
    }

    private void InsertSlowest(string name, double durationMs)
    {
        if (_topSlowestCount < TopSlowestSize)
        {
            _topSlowest[_topSlowestCount++] = new SlowestEntry(name, durationMs);
        }
        else
        {
            // Replace the smallest entry
            int minIdx = 0;
            for (int i = 1; i < TopSlowestSize; i++)
            {
                if (_topSlowest[i].DurationMs < _topSlowest[minIdx].DurationMs)
                    minIdx = i;
            }
            _topSlowest[minIdx] = new SlowestEntry(name, durationMs);
        }

        // Recalculate min
        _topSlowestMinMs = double.MaxValue;
        for (int i = 0; i < _topSlowestCount; i++)
        {
            if (_topSlowest[i].DurationMs < _topSlowestMinMs)
                _topSlowestMinMs = _topSlowest[i].DurationMs;
        }
    }

    // ── Inner types ──

    internal sealed class OperationStats
    {
        public int Count;
        public double TotalMs;
        public double MaxMs;
    }

    private record struct SlowestEntry(string Name, double DurationMs);
}

/// <summary>
/// Zero-allocation profiling scope. Returned by <see cref="UiOperationProfiler.Profile"/>.
/// </summary>
internal readonly struct ProfileScope : IDisposable
{
    private readonly UiOperationProfiler? _profiler;
    private readonly string _name;
    private readonly long _startTimestamp;

    public ProfileScope(UiOperationProfiler? profiler, string name, long startTimestamp)
    {
        _profiler = profiler;
        _name = name;
        _startTimestamp = startTimestamp;
    }

    public void Dispose()
    {
        if (_profiler == null) return;
        var elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
        var ms = elapsed * 1000.0 / Stopwatch.Frequency;
        _profiler.RecordOperation(_name, ms);
    }
}

/// <summary>
/// GC collection delta between two sample points.
/// </summary>
internal readonly record struct GcDelta(int Gen0, int Gen1, int Gen2);

/// <summary>
/// Snapshot of operation stats for display.
/// </summary>
internal readonly record struct OperationStatSnapshot(string Name, double MaxMs, double AvgMs, int Count);
