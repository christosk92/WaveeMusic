using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Hosting;

namespace Wavee;

/// <summary>Always-on memory attribution. One <c>mem.sample</c> line every <see cref="IntervalMs"/> while frames are
/// being rendered, plus one at the end of every navigation window and scroll burst (<see cref="NavigationFrameWatch"/>
/// asks). Each line carries, in order: the process (working set, its peak, private bytes), the managed heap
/// (<see cref="GC.GetGCMemoryInfo()"/> heap / committed / fragmented, gen sizes incl. LOH + POH, collection counts,
/// the allocation rate since the last sample), the engine census (<see cref="FluentApp.EngineCensus"/>: scene nodes,
/// strings, decoded-image bytes, components, bindings, animation tracks, pixel pool), GPU residency, and every
/// registered owner's own report (the catalog, the query graph, the commit queue …).
/// These domains overlap: working set minus heap/image/GPU bytes is not a native-memory attribution.
/// UI thread only (the engine census is a UI-thread read).</summary>
public static class MemorySampler
{
    const double IntervalMs = 5000;
    static double _lastSampleAt = double.NegativeInfinity;
    static long _lastAllocBytes;
    static long _lastAllocTicks;
    static long _peakWorkingSet;

    /// <summary>Frame tick from <see cref="NavigationFrameWatch"/>: a periodic sample while the app renders.</summary>
    public static void OnFrame()
    {
        if (NavigationClockMs() - _lastSampleAt < IntervalMs) return;
        Sample("periodic");
    }

    /// <summary>Final process-lifetime peak after the UI loop; never touches disposed engine resources.</summary>
    public static void SampleProcessEnd()
    {
        using var process = Process.GetCurrentProcess();
        long ws = process.WorkingSet64;
        _peakWorkingSet = Math.Max(_peakWorkingSet, ws);
        WaveeLog.Instance.Event(WaveeLogLevel.Info, "mem", "mem.sample",
            "memory session-end reason=session-end ws=" + Mb(ws) + " wsPeak=" + Mb(_peakWorkingSet)
            + " private=" + Mb(process.PrivateMemorySize64) + " processPeak=" + Mb(process.PeakWorkingSet64)
            + " wsBytes=" + ws + " processPeakBytes=" + process.PeakWorkingSet64);
    }

    /// <summary>Write one <c>mem.sample</c> line now. <paramref name="reason"/> says why ("periodic", "nav-end route=…").</summary>
    public static void Sample(string reason)
    {
        _lastSampleAt = NavigationClockMs();
        long nowTicks = Stopwatch.GetTimestamp();
        long allocNow = GC.GetTotalAllocatedBytes(precise: false);
        double allocRateMBs = 0;
        if (_lastAllocTicks != 0)
        {
            double sec = (nowTicks - _lastAllocTicks) / (double)Stopwatch.Frequency;
            if (sec > 0) allocRateMBs = (allocNow - _lastAllocBytes) / sec / (1024.0 * 1024.0);
        }
        _lastAllocBytes = allocNow; _lastAllocTicks = nowTicks;

        long ws = Environment.WorkingSet;
        if (ws > _peakWorkingSet) _peakWorkingSet = ws;
        long privateBytes = 0;
        long processPeak = 0;
        try { using var p = Process.GetCurrentProcess(); privateBytes = p.PrivateMemorySize64; processPeak = p.PeakWorkingSet64; } catch { }
        var gc = GC.GetGCMemoryInfo();
        var gens = gc.GenerationInfo;
        long gen0 = gens.Length > 0 ? gens[0].SizeAfterBytes : 0, gen1 = gens.Length > 1 ? gens[1].SizeAfterBytes : 0,
            gen2 = gens.Length > 2 ? gens[2].SizeAfterBytes : 0, loh = gens.Length > 3 ? gens[3].SizeAfterBytes : 0,
            poh = gens.Length > 4 ? gens[4].SizeAfterBytes : 0;

        var sb = new StringBuilder(512);
        sb.Append("memory ").Append(reason)
          .Append(" ws=").Append(Mb(ws)).Append(" wsPeak=").Append(Mb(_peakWorkingSet)).Append(" private=").Append(Mb(privateBytes))
          .Append(" processPeak=").Append(Mb(processPeak))
          .Append(" wsBytes=").Append(ws).Append(" processPeakBytes=").Append(processPeak)
          .Append(" heap=").Append(Mb(gc.HeapSizeBytes)).Append(" committed=").Append(Mb(gc.TotalCommittedBytes))
          .Append(" fragmented=").Append(Mb(gc.FragmentedBytes))
          .Append(" gen0=").Append(Mb(gen0)).Append(" gen1=").Append(Mb(gen1)).Append(" gen2=").Append(Mb(gen2))
          .Append(" loh=").Append(Mb(loh)).Append(" poh=").Append(Mb(poh))
          .Append(" gcs=").Append(GC.CollectionCount(0)).Append('/').Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2))
          .Append(" allocMBs=").Append(allocRateMBs.ToString("0.0", CultureInfo.InvariantCulture))
          .Append(" totalAlloc=").Append(Mb(allocNow));

        if (FluentApp.EngineCensus() is { } e)
        {
            sb.Append(" | engine scene=").Append(e.SceneLive).Append('/').Append(e.SceneCapacity).Append(" orphans=").Append(e.SceneOrphans)
              .Append(" strings=").Append(e.StringMap).Append(" images=").Append(e.ImageCount).Append(" imagesReady=").Append(e.ImageReady)
              .Append(" imageBytes=").Append(Mb(e.ImageUsedBytes)).Append(" decodeInflight=").Append(e.DecodeInflight)
              .Append(" components=").Append(e.Components).Append(" bindings=").Append(e.NodeBindings).Append(" virtuals=").Append(e.VirtualBoundaries)
              .Append(" animTracks=").Append(e.AnimTracks).Append(" pixelPool=").Append(Mb(e.PixelPoolRetainedBytes)).Append('/').Append(Mb(e.PixelPoolPeakBytes));
        }
        if (FluentApp.GpuResidency() is { } g)
        {
            sb.Append(" | gpu bytes=").Append(Mb(g.Bytes)).Append(" resources=").Append(g.Count);
            // Per-class breakdown (top=…) + render-target pool occupancy (rt=…) + upload-arena counters (upload=…),
            // e.g. " top=Image.Texture:61.2/812,Glyph.AtlasTexture:16.0/1 rt=inuse:2/free:2/pin:4 upload=arena:1.1/peak:0.4/refused:0".
            // The tour parser only reads "gpu bytes=" and "resources=" above, so this can only ever ADD tokens after them.
            if (FluentApp.GpuCensusLine() is { Length: > 0 } detail)
                sb.Append(detail);
        }

        foreach (var owner in Wavee.Core.Diagnostics.PerformanceDiagnostics.Owners.Sample())
            sb.Append(" | ").Append(owner.Name).Append(' ').Append(owner.Value);
        WaveeLog.Instance.Event(ws > 400L * 1024 * 1024 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "mem", "mem.sample", sb.ToString());
    }

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double NavigationClockMs() => Clock.Elapsed.TotalMilliseconds;

    static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
}
