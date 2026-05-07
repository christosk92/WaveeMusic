using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Diagnostics;

/// <summary>
/// Per-navigation stage timer + correlator that records GC, working-set and
/// page-fault deltas around each nav, and emits a single <c>[stall]</c> snapshot
/// line whenever <see cref="UiHealthMonitor"/> detects a critical UI freeze.
///
/// <para>
/// Goal: when a 300–500 ms hang happens after hours of use, the latest log file
/// already contains enough correlated context to identify which subsystem fired
/// (blocking Gen2 GC on the UI thread, working-set trim aftermath, page-level
/// <c>Bindings.Update</c> spike, etc.) without re-running the scenario under a
/// trace tool.
/// </para>
/// </summary>
internal sealed class NavigationDiagnostics
{
    public static NavigationDiagnostics? Instance { get; set; }

    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, NavInProgress> _active = new();

    private const int RecentNavRingSize = 32;
    private readonly NavSummary[] _recentNavs = new NavSummary[RecentNavRingSize];
    private int _recentNavsHead;
    private int _recentNavsCount;

    private MemoryReleaseRecord? _lastRelease;

    private long _nextNavId;

    // The most recently begun navigation that hasn't yet ended. UI thread is
    // single-threaded so a plain field is safe; set in BeginNav, cleared in
    // EndNav. Page-level instrumentation calls StageCurrent which routes the
    // stage into this nav so its total time is broken down on the [nav] line.
    private long _activeNavIdSoftThreadLocal;

    public NavigationDiagnostics(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Snapshots GC counts + memory + page faults at the start of a navigation.
    /// Returns a correlation id used for subsequent <see cref="Stage"/> calls.
    /// </summary>
    public long BeginNav(string target, string source)
    {
        var navId = Interlocked.Increment(ref _nextNavId);

        var rec = new NavInProgress
        {
            NavId = navId,
            Target = target ?? string.Empty,
            Source = source ?? string.Empty,
            StartTimestamp = Stopwatch.GetTimestamp(),
            LastStageTimestamp = Stopwatch.GetTimestamp(),
            Gen0Start = GC.CollectionCount(0),
            Gen1Start = GC.CollectionCount(1),
            Gen2Start = GC.CollectionCount(2),
            ManagedBytesStart = GC.GetTotalMemory(false),
            WorkingSetStart = SafeWorkingSet(),
            PrivateBytesStart = SafePrivateBytes(),
            PageFaultsStart = SafePageFaultCount(),
            ThreadId = Environment.CurrentManagedThreadId,
        };

        lock (_lock)
        {
            _active[navId] = rec;
        }
        _activeNavIdSoftThreadLocal = navId;
        return navId;
    }

    /// <summary>
    /// Begins timing a named navigation stage. Dispose to record. Also routes the
    /// stage timing into <see cref="UiOperationProfiler"/> as <c>nav.{name}</c> so
    /// it surfaces in the existing health-overlay top-N.
    /// </summary>
    public NavStageScope Stage(long navId, string name)
        => new(this, navId, name, Stopwatch.GetTimestamp());

    /// <summary>
    /// Stages a name into the most-recently-begun active navigation. Used by
    /// page-level instrumentation (OnNavigatedTo / OnNavigatedFrom / connected
    /// animation work) which doesn't have direct access to the navId.
    /// </summary>
    public NavStageScope StageCurrent(string name)
        => new(this, _activeNavIdSoftThreadLocal, name, Stopwatch.GetTimestamp());

    internal void RecordStage(long navId, string name, double ms)
    {
        UiOperationProfiler.Instance?.RecordOperation("nav." + name, ms);

        lock (_lock)
        {
            if (_active.TryGetValue(navId, out var rec))
            {
                rec.Stages.Add((name, ms));
                rec.LastStageTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// Closes a navigation, computes overall deltas, logs a one-line summary, and
    /// stores the summary in the recent-nav ring for any subsequent stall snapshot.
    /// </summary>
    public void EndNav(long navId)
    {
        NavInProgress? rec;
        lock (_lock)
        {
            if (!_active.TryGetValue(navId, out rec))
                return;
            _active.Remove(navId);
        }
        if (_activeNavIdSoftThreadLocal == navId)
            _activeNavIdSoftThreadLocal = 0;

        var endTs = Stopwatch.GetTimestamp();
        var totalMs = (endTs - rec.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
        var gen0 = GC.CollectionCount(0) - rec.Gen0Start;
        var gen1 = GC.CollectionCount(1) - rec.Gen1Start;
        var gen2 = GC.CollectionCount(2) - rec.Gen2Start;
        var managedDelta = GC.GetTotalMemory(false) - rec.ManagedBytesStart;
        var wsDelta = SafeWorkingSet() - rec.WorkingSetStart;
        var pbDelta = SafePrivateBytes() - rec.PrivateBytesStart;
        var pageFaultsDelta = unchecked(SafePageFaultCount() - rec.PageFaultsStart);

        var summary = new NavSummary(
            rec.NavId, rec.Target, rec.Source,
            totalMs, gen0, gen1, gen2,
            managedDelta, wsDelta, pbDelta, pageFaultsDelta,
            DateTime.UtcNow,
            BuildStageString(rec.Stages));

        lock (_lock)
        {
            _recentNavs[_recentNavsHead] = summary;
            _recentNavsHead = (_recentNavsHead + 1) % RecentNavRingSize;
            if (_recentNavsCount < RecentNavRingSize) _recentNavsCount++;
        }

        // One line per nav. Compact key=value style so it greps and parses cleanly.
        _logger?.LogInformation(
            "[nav] id={NavId} target={Target} src={Source} totalMs={TotalMs:F1} " +
            "gen0={Gen0} gen1={Gen1} gen2={Gen2} mgdDeltaMb={MgdMb:+0.0;-0.0;0} " +
            "wsDeltaMb={WsMb:+0.0;-0.0;0} pbDeltaMb={PbMb:+0.0;-0.0;0} " +
            "pageFaultsDelta={PfDelta} stages=[{Stages}]",
            rec.NavId, rec.Target, rec.Source, totalMs,
            gen0, gen1, gen2,
            managedDelta / 1048576.0, wsDelta / 1048576.0, pbDelta / 1048576.0,
            pageFaultsDelta, summary.Stages);
    }

    /// <summary>
    /// Records a memory-release event from <see cref="MemoryReleaseHelper"/>.
    /// Logged immediately and remembered for the next stall snapshot.
    /// </summary>
    public void RecordMemoryRelease(
        string reason, int threadId, double durationMs,
        int gen2Before, int gen2After,
        long wsBefore, long wsAfter,
        long managedBefore, long managedAfter)
    {
        var rec = new MemoryReleaseRecord(
            reason ?? string.Empty, threadId, durationMs,
            gen2Before, gen2After, wsBefore, wsAfter,
            managedBefore, managedAfter, DateTime.UtcNow);

        lock (_lock)
        {
            _lastRelease = rec;
        }

        _logger?.LogInformation(
            "[memrel] thread={Tid} reason={Reason} durMs={Dur:F1} " +
            "gen2={Gen2Before}->{Gen2After} mgdMb={MgdBefore:F1}->{MgdAfter:F1} " +
            "wsMb={WsBefore:F1}->{WsAfter:F1}",
            threadId, rec.Reason, durationMs,
            gen2Before, gen2After,
            managedBefore / 1048576.0, managedAfter / 1048576.0,
            wsBefore / 1048576.0, wsAfter / 1048576.0);
    }

    /// <summary>
    /// Called by <see cref="UiHealthMonitor"/> when it detects a UI-thread stall
    /// at or above the critical threshold. Composes a single correlated log line
    /// with the most recent nav summaries and the most recent memory release —
    /// everything the user needs to pick a hypothesis from one log line.
    /// </summary>
    public void OnUiStallDetected(double durationMs, int frameNumber, int gen2DeltaThisTick)
    {
        NavSummary? latestNav = null;
        MemoryReleaseRecord? latestRelease;
        StringBuilder recentNavs = new();

        lock (_lock)
        {
            // Walk the ring oldest→newest, then take last 3 for context.
            var start = (_recentNavsHead - _recentNavsCount + RecentNavRingSize) % RecentNavRingSize;
            int take = Math.Min(_recentNavsCount, 3);
            int firstIdx = (_recentNavsHead - take + RecentNavRingSize) % RecentNavRingSize;
            for (int i = 0; i < take; i++)
            {
                var s = _recentNavs[(firstIdx + i) % RecentNavRingSize];
                if (recentNavs.Length > 0) recentNavs.Append(';');
                recentNavs.Append(s.Target).Append('@').Append(s.TotalMs.ToString("F0"))
                    .Append("ms,gen2=").Append(s.Gen2)
                    .Append(",pf=").Append(s.PageFaultsDelta);
                if (i == take - 1)
                    latestNav = s;
            }

            latestRelease = _lastRelease;
        }

        var ageOfReleaseMs = latestRelease.HasValue
            ? (DateTime.UtcNow - latestRelease.Value.CompletedUtc).TotalMilliseconds
            : -1;

        _logger?.LogError(
            "[stall] dur={Dur:F0}ms frame={Frame} gen2DeltaThisTick={Gen2Tick} " +
            "lastReleaseAgoMs={ReleaseAgo:F0} lastReleaseThread={ReleaseTid} " +
            "lastReleaseReason={ReleaseReason} lastReleaseDurMs={ReleaseDur:F1} " +
            "lastReleaseGen2={ReleaseGen2Before}->{ReleaseGen2After} " +
            "lastNavTarget={LastNavTarget} lastNavTotalMs={LastNavMs:F0} " +
            "lastNavStages=[{LastNavStages}] recentNavs=[{RecentNavs}]",
            durationMs, frameNumber, gen2DeltaThisTick,
            ageOfReleaseMs,
            latestRelease?.ThreadId ?? -1,
            latestRelease?.Reason ?? "<none>",
            latestRelease?.DurationMs ?? 0,
            latestRelease?.Gen2Before ?? -1,
            latestRelease?.Gen2After ?? -1,
            latestNav?.Target ?? "<none>",
            latestNav?.TotalMs ?? 0,
            latestNav?.Stages ?? "<none>",
            recentNavs.ToString());
    }

    /// <summary>
    /// Builds a human-readable report of recent navigations and the most recent
    /// memory-release event. Suitable for copy-paste into a bug report.
    /// </summary>
    public string GenerateReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Wavee Navigation Health Report ===");
        sb.Append("Generated: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();

        long totalManagedMb = GC.GetTotalMemory(false) / 1048576;
        sb.Append("Managed heap: ").Append(totalManagedMb).Append(" MB    ");
        sb.Append("GC counts (cumulative): Gen0=").Append(GC.CollectionCount(0))
          .Append(" Gen1=").Append(GC.CollectionCount(1))
          .Append(" Gen2=").Append(GC.CollectionCount(2)).AppendLine();
        sb.Append("Working set: ").Append(SafeWorkingSet() / 1048576).Append(" MB    ");
        sb.Append("Private bytes: ").Append(SafePrivateBytes() / 1048576).Append(" MB    ");
        sb.Append("Page faults: ").Append(SafePageFaultCount()).AppendLine();

        sb.AppendLine();
        sb.AppendLine("--- Recent navigations (oldest first) ---");

        NavSummary[] navsCopy;
        MemoryReleaseRecord? releaseCopy;
        int navCount;
        lock (_lock)
        {
            navsCopy = new NavSummary[_recentNavsCount];
            int start = (_recentNavsHead - _recentNavsCount + RecentNavRingSize) % RecentNavRingSize;
            for (int i = 0; i < _recentNavsCount; i++)
                navsCopy[i] = _recentNavs[(start + i) % RecentNavRingSize];
            navCount = _recentNavsCount;
            releaseCopy = _lastRelease;
        }

        if (navCount == 0)
        {
            sb.AppendLine("  (none yet — navigate around the app to generate entries)");
        }
        else
        {
            for (int i = 0; i < navCount; i++)
            {
                var s = navsCopy[i];
                sb.Append("  #").Append(s.NavId).Append("  ")
                  .Append(s.CompletedUtc.ToLocalTime().ToString("HH:mm:ss.fff")).Append("  ")
                  .Append(s.Target).Append(" via ").Append(s.Source).AppendLine();
                sb.Append("      total=").Append(s.TotalMs.ToString("F1")).Append("ms  ")
                  .Append("gen0=").Append(s.Gen0).Append(" gen1=").Append(s.Gen1).Append(" gen2=").Append(s.Gen2)
                  .Append("  managedΔ=").Append((s.ManagedDelta / 1048576.0).ToString("+0.0;-0.0;0")).Append("MB")
                  .Append("  wsΔ=").Append((s.WorkingSetDelta / 1048576.0).ToString("+0.0;-0.0;0")).Append("MB")
                  .Append("  pageFaultsΔ=").Append(s.PageFaultsDelta).AppendLine();
                if (!string.IsNullOrEmpty(s.Stages))
                    sb.Append("      stages: ").Append(s.Stages).AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("--- Last memory release ---");
        if (releaseCopy is null)
        {
            sb.AppendLine("  (no MemoryReleaseHelper events captured this session)");
        }
        else
        {
            var r = releaseCopy.Value;
            var ageMs = (DateTime.UtcNow - r.CompletedUtc).TotalMilliseconds;
            sb.Append("  reason=").Append(r.Reason)
              .Append("  thread=").Append(r.ThreadId).Append(r.ThreadId == Environment.CurrentManagedThreadId ? " (UI)" : " (off-UI)")
              .AppendLine();
            sb.Append("  durMs=").Append(r.DurationMs.ToString("F1"))
              .Append("  gen2=").Append(r.Gen2Before).Append("→").Append(r.Gen2After)
              .Append("  managedMb=").Append((r.ManagedBefore / 1048576.0).ToString("F1")).Append("→").Append((r.ManagedAfter / 1048576.0).ToString("F1"))
              .Append("  wsMb=").Append((r.WsBefore / 1048576.0).ToString("F1")).Append("→").Append((r.WsAfter / 1048576.0).ToString("F1"))
              .AppendLine();
            sb.Append("  ageSinceMs=").Append(ageMs.ToString("F0")).AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("--- Top UI operations (from UiOperationProfiler) ---");
        var profiler = UiOperationProfiler.Instance;
        if (profiler is null)
        {
            sb.AppendLine("  (profiler not initialized)");
        }
        else
        {
            var top = profiler.GetTopOperations(10);
            if (top.Count == 0)
                sb.AppendLine("  (no operations recorded yet)");
            else
                foreach (var op in top)
                    sb.Append("  ").Append(op.Name.PadRight(36))
                      .Append(" max=").Append(op.MaxMs.ToString("F1")).Append("ms")
                      .Append("  avg=").Append(op.AvgMs.ToString("F1")).Append("ms")
                      .Append("  n=").Append(op.Count).AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildStageString(List<(string Name, double Ms)> stages)
    {
        if (stages.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < stages.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(stages[i].Name).Append('=').Append(stages[i].Ms.ToString("F1"));
        }
        return sb.ToString();
    }

    // ── Process counters (cached pseudo-handle, no managed Process wrapper) ──

    private static readonly IntPtr _selfHandle = GetCurrentProcess();

    private static long SafeWorkingSet()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        return TryGetCounters(out var c) ? (long)c.WorkingSetSize : 0;
    }

    private static long SafePrivateBytes()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        // PagefileUsage approximates committed private bytes; matches what
        // Process.PrivateMemorySize64 reports on the same struct field.
        return TryGetCounters(out var c) ? (long)c.PagefileUsage : 0;
    }

    private static uint SafePageFaultCount()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        return TryGetCounters(out var c) ? c.PageFaultCount : 0u;
    }

    private static bool TryGetCounters(out PROCESS_MEMORY_COUNTERS counters)
    {
        counters = default;
        try
        {
            var size = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>();
            counters.cb = size;
            return GetProcessMemoryInfo(_selfHandle, out counters, size);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr hProcess,
        out PROCESS_MEMORY_COUNTERS counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    // ── Inner records ──

    private sealed class NavInProgress
    {
        public long NavId;
        public string Target = string.Empty;
        public string Source = string.Empty;
        public long StartTimestamp;
        public long LastStageTimestamp;
        public int Gen0Start, Gen1Start, Gen2Start;
        public long ManagedBytesStart;
        public long WorkingSetStart;
        public long PrivateBytesStart;
        public uint PageFaultsStart;
        public int ThreadId;
        public List<(string Name, double Ms)> Stages = new(8);
    }

    private readonly record struct NavSummary(
        long NavId, string Target, string Source,
        double TotalMs,
        int Gen0, int Gen1, int Gen2,
        long ManagedDelta, long WorkingSetDelta, long PrivateBytesDelta,
        uint PageFaultsDelta,
        DateTime CompletedUtc,
        string Stages);

    private readonly record struct MemoryReleaseRecord(
        string Reason, int ThreadId, double DurationMs,
        int Gen2Before, int Gen2After,
        long WsBefore, long WsAfter,
        long ManagedBefore, long ManagedAfter,
        DateTime CompletedUtc);
}

/// <summary>
/// Disposable stage scope. Records elapsed time into both
/// <see cref="NavigationDiagnostics"/> (per-nav breakdown) and
/// <see cref="UiOperationProfiler"/> (top-N display).
/// </summary>
internal readonly struct NavStageScope : IDisposable
{
    private readonly NavigationDiagnostics? _owner;
    private readonly long _navId;
    private readonly string _name;
    private readonly long _startTimestamp;

    public NavStageScope(NavigationDiagnostics? owner, long navId, string name, long startTimestamp)
    {
        _owner = owner;
        _navId = navId;
        _name = name;
        _startTimestamp = startTimestamp;
    }

    public void Dispose()
    {
        if (_owner == null) return;
        var elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
        var ms = elapsed * 1000.0 / Stopwatch.Frequency;
        _owner.RecordStage(_navId, _name, ms);
    }
}
