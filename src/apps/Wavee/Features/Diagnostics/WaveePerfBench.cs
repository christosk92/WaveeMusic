using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace Wavee;

// WAVEE_PERF_BENCH=1 (or --perf-bench): scripted CPU + memory benchmarks for regression tracking. Samples process
// working-set / private bytes / CPU% (Task-Manager-style) plus engine census and per-frame work time. Writes JSON +
// a human summary under WAVEE_BENCH_OUT (default: %LOCALAPPDATA%\Wavee\bench). Run with --fake for deterministic
// offline data and no network.
internal static class WaveePerfBench
{
    sealed class Sample
    {
        public double ElapsedSec;
        public double WorkingSetMB;
        public double PrivateMB;
        public double ManagedMB;
        public double CpuPct;
        public double FrameMsP50;
        public int Images;
        public int SceneLive;
        public int Components;
    }

    sealed class ScenarioResult
    {
        public required string Name;
        public int Frames;
        public double DurationSec;
        public double CpuAvgPct;
        public double CpuPeakPct;
        public double WorkingSetAvgMB;
        public double WorkingSetPeakMB;
        public double PrivatePeakMB;
        public double ManagedPeakMB;
        public double FrameMsP50;
        public double FrameMsP90;
        public double FrameMsMax;
        public int ImagesPeak;
        public int SceneLivePeak;
    }

    static readonly WaveeLogger Log = new(WaveeLog.Instance, "probe");

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (!Diag.EnvFlag("WAVEE_PERF_BENCH")) return false;
        WaveeLog.Instance.SetEcho(Console.Error.WriteLine);   // env-gated run only: mirror probe progress to the terminal
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Log.Warn("[perf-bench] unavailable: requires Win32Window + D3D12Device");
            return true;
        }

        for (int i = 0; i < 600 && WaveeShell.ProbeNav is null && !w.IsClosed; i++)
        {
            gpu.SuppressLatencyWaitOnce();
            gpu.SuppressVsyncOnce();
            host.RunFrame();
        }
        if (WaveeShell.ProbeNav is null)
        {
            Log.Warn("[perf-bench] nav hook not wired (WaveeShell not mounted?)");
            return true;
        }

        var nav = WaveeShell.ProbeNav!;
        void Nav(string key, string? arg) => nav(key, arg);
        void FrameFast() { if (!w.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !w.IsClosed; i++) FrameFast(); }

        var results = new List<ScenarioResult>();
        results.Add(RunIdle(host, w, FrameFast));
        Nav("home", null); Settle(40);
        results.Add(RunFrames(host, "home", 180, FrameFast));
        Nav("artist:spotify:artist:soakbench", "Bench Artist"); Settle(50);
        results.Add(RunFrames(host, "artist-detail", 120, FrameFast));
        Nav("home", null); Settle(20);
        // NOTE: at the bench window size the fake playlist's fact cards are not reached by layout within the hop
        // frames (densityGeometryBuilds reads 0), so this scenario times the page + rail mount, not the tempo ridge; the
        // ridge's own cost rules are gated headlessly (gate.ctl.charts.density.*). Kept as the open-latency number.
        results.Add(RunPlaylistOpen(host, nav, FrameFast));
        Nav("home", null); Settle(20);
        results.Add(RunNavBurst(host, nav, FrameFast));

        string outDir = BenchOutDir();
        Directory.CreateDirectory(outDir);
        string json = ToJson(results);
        string jsonPath = Path.Combine(outDir, "wavee-perf-latest.json");
        File.WriteAllText(jsonPath, json);
        File.WriteAllText(Path.Combine(outDir, $"wavee-perf-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), json);

        var sb = new StringBuilder(2048);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE PERF BENCH (CPU + memory) ===");
        sb.AppendLine($"version={VersionLabel()}  processors={Environment.ProcessorCount}");
        sb.AppendLine($"output={jsonPath}");
        sb.AppendLine();
        sb.AppendLine($"{"scenario",-16} {"dur",5} {"cpu%",6} {"cpuPk%",7} {"wsMB",7} {"wsPkMB",8} {"privPk",7} {"frameP50",9} {"frameP90",9} {"frameMax",9}");
        foreach (var r in results)
        {
            sb.Append(r.Name.PadRight(16)).Append(' ')
              .Append(r.DurationSec.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(5)).Append(' ')
              .Append(r.CpuAvgPct.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6)).Append(' ')
              .Append(r.CpuPeakPct.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)).Append(' ')
              .Append(r.WorkingSetAvgMB.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)).Append(' ')
              .Append(r.WorkingSetPeakMB.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8)).Append(' ')
              .Append(r.PrivatePeakMB.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)).Append(' ')
              .Append(r.FrameMsP50.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9)).Append(' ')
              .Append(r.FrameMsP90.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9)).Append(' ')
              .Append(r.FrameMsMax.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9)).AppendLine();
        }
        string report = sb.ToString();
        Log.Info(report);
        File.WriteAllText(Path.Combine(outDir, "wavee-perf-latest.txt"), report);
        Log.Info("=== PERF-BENCH JSON BEGIN ===");
        Log.Info(json);
        Log.Info("=== PERF-BENCH JSON END ===");
        return true;
    }

    // Real-time idle: paced frames like a normal run (Task Manager idle baseline).
    static ScenarioResult RunIdle(AppHost host, Win32Window window, Action frameFast)
    {
        int sec = EnvInt("WAVEE_BENCH_IDLE_SEC", 10, 3, 120);
        var proc = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        var frameMs = new List<double>();
        var sw = Stopwatch.StartNew();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        long tick0 = Stopwatch.GetTimestamp();

        while (sw.Elapsed.TotalSeconds < sec && !window.IsClosed)
        {
            long t0 = Stopwatch.GetTimestamp();
            host.RunFrame();
            frameMs.Add(host.LastStats.FrameMs);
            window.WaitForWork(Math.Min(host.RecommendedWaitMs(), 16));
            if (sw.Elapsed.TotalSeconds >= 1.0 && samples.Count == 0 || samples.Count > 0 && sw.Elapsed.TotalSeconds - samples[^1].ElapsedSec >= 1.0)
                samples.Add(CaptureSample(proc, cpu0, tick0, host, frameMs));
        }
        if (samples.Count == 0) samples.Add(CaptureSample(proc, cpu0, tick0, host, frameMs));
        return Aggregate("idle", (int)frameMs.Count, sw.Elapsed.TotalSeconds, samples, frameMs);
    }

    static ScenarioResult RunFrames(AppHost host, string name, int frames, Action frameFast)
    {
        var proc = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        var frameMs = new List<double>(frames);
        var sw = Stopwatch.StartNew();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        long tick0 = Stopwatch.GetTimestamp();
        int sampleEvery = Math.Max(30, frames / 6);

        for (int i = 0; i < frames; i++)
        {
            frameFast();
            frameMs.Add(host.LastStats.FrameMs);
            if (i % sampleEvery == 0) samples.Add(CaptureSample(proc, cpu0, tick0, host, frameMs));
        }
        samples.Add(CaptureSample(proc, cpu0, tick0, host, frameMs));
        return Aggregate(name, frames, sw.Elapsed.TotalSeconds, samples, frameMs);
    }

    static ScenarioResult RunNavBurst(AppHost host, Action<string, string?> nav, Action frameFast)
    {
        int hops = EnvInt("WAVEE_BENCH_NAV_HOPS", 12, 4, 60);
        var proc = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        var frameMs = new List<double>();
        var sw = Stopwatch.StartNew();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        long tick0 = Stopwatch.GetTimestamp();

        for (int i = 0; i < hops; i++)
        {
            var (key, label) = (i % 3) switch
            {
                0 => ("album:spotify:album:bench" + i, "Album " + i),
                1 => ("artist:spotify:artist:bench" + i, "Artist " + i),
                _ => ("pl:spotify:playlist:bench" + i, "Playlist " + i),
            };
            nav(key, label);
            for (int f = 0; f < 8; f++) { frameFast(); frameMs.Add(host.LastStats.FrameMs); }
            if (i % 3 == 0) { nav("home", null); for (int f = 0; f < 4; f++) { frameFast(); frameMs.Add(host.LastStats.FrameMs); } }
            samples.Add(CaptureSample(proc, cpu0, tick0, host, frameMs));
        }
        return Aggregate("nav-burst", frameMs.Count, sw.Elapsed.TotalSeconds, samples, frameMs);
    }

    // Playlist OPEN: the detail page + its facts rail mounting, then the store's refresh passes as the (fake) list
    // settles. Each hop leaves for Home and re-enters the SAME playlist, so every hop pays the full open; FrameMsMax is
    // the "it lags when I open a playlist" number. The path-tessellation delta is logged alongside: geometry that is
    // re-minted on every refresh pass shows up here as hundreds of tessellations per open.
    static ScenarioResult RunPlaylistOpen(AppHost host, Action<string, string?> nav, Action frameFast)
    {
        int hops = EnvInt("WAVEE_BENCH_OPEN_HOPS", 8, 2, 40);
        var proc = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        var frameMs = new List<double>();
        var sw = Stopwatch.StartNew();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        long tick0 = Stopwatch.GetTimestamp();
        var paths = FluentGpu.Render.PathRealizationCache.Shared;
        int tess0 = paths.TessellationCount, real0 = paths.RealizationCount;
        int builds0 = FluentGpu.Controls.DensityPlot.GeometryBuilds;   // the rail's tempo ridge: geometry rebuilds per open

        for (int i = 0; i < hops; i++)
        {
            nav("pl:spotify:playlist:pl1", "Playlist 1");
            for (int f = 0; f < 12; f++) { frameFast(); frameMs.Add(host.LastStats.FrameMs); }
            nav("home", null);
            for (int f = 0; f < 4; f++) { frameFast(); frameMs.Add(host.LastStats.FrameMs); }
            samples.Add(CaptureSample(proc, cpu0, tick0, host, frameMs));
        }
        int tess = paths.TessellationCount - tess0, real = paths.RealizationCount - real0;
        int builds = FluentGpu.Controls.DensityPlot.GeometryBuilds - builds0;
        // Realizations/tessellations count only what was RECORDED — a rail card below the window's fold is culled, so
        // the geometry-build count is the honest measure of how often the tempo ridge re-minted its paths.
        Log.Info($"[perf-bench] playlist-open: {hops} opens, densityGeometryBuilds={builds} ({(double)builds / hops:0.0} per open), path realizations={real} tessellations={tess}, slabBytes={paths.SlabBytes}, window={host.Scene.Bounds(host.Scene.Root).W:0}x{host.Scene.Bounds(host.Scene.Root).H:0}");
        return Aggregate("playlist-open", frameMs.Count, sw.Elapsed.TotalSeconds, samples, frameMs);
    }

    static Sample CaptureSample(Process proc, TimeSpan cpu0, long tick0, AppHost host, List<double> frameMs)
    {
        proc.Refresh();
        double elapsedSec = (Stopwatch.GetTimestamp() - tick0) / (double)Stopwatch.Frequency;
        double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        double cpuPct = elapsedSec > 0.001
            ? cpuMs / (elapsedSec * 1000.0 * Environment.ProcessorCount) * 100.0
            : 0;
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        var census = CensusSnapshot.Capture(host);
        return new Sample
        {
            ElapsedSec = elapsedSec,
            WorkingSetMB = proc.WorkingSet64 / 1048576.0,
            PrivateMB = proc.PrivateMemorySize64 / 1048576.0,
            ManagedMB = managed / 1048576.0,
            CpuPct = cpuPct,
            FrameMsP50 = Percentile(frameMs, 0.50),
            Images = census.ImageCount,
            SceneLive = census.SceneLive,
            Components = census.Components,
        };
    }

    static ScenarioResult Aggregate(string name, int frames, double durationSec, List<Sample> samples, List<double> frameMs)
    {
        double cpuAvg = 0, cpuPeak = 0, wsAvg = 0, wsPeak = 0, privPeak = 0, managedPeak = 0;
        int imgPeak = 0, scenePeak = 0;
        foreach (var s in samples)
        {
            cpuAvg += s.CpuPct;
            if (s.CpuPct > cpuPeak) cpuPeak = s.CpuPct;
            wsAvg += s.WorkingSetMB;
            if (s.WorkingSetMB > wsPeak) wsPeak = s.WorkingSetMB;
            if (s.PrivateMB > privPeak) privPeak = s.PrivateMB;
            if (s.ManagedMB > managedPeak) managedPeak = s.ManagedMB;
            if (s.Images > imgPeak) imgPeak = s.Images;
            if (s.SceneLive > scenePeak) scenePeak = s.SceneLive;
        }
        if (samples.Count > 0) { cpuAvg /= samples.Count; wsAvg /= samples.Count; }
        return new ScenarioResult
        {
            Name = name,
            Frames = frames,
            DurationSec = durationSec,
            CpuAvgPct = cpuAvg,
            CpuPeakPct = cpuPeak,
            WorkingSetAvgMB = wsAvg,
            WorkingSetPeakMB = wsPeak,
            PrivatePeakMB = privPeak,
            ManagedPeakMB = managedPeak,
            FrameMsP50 = Percentile(frameMs, 0.50),
            FrameMsP90 = Percentile(frameMs, 0.90),
            FrameMsMax = frameMs.Count > 0 ? Max(frameMs) : 0,
            ImagesPeak = imgPeak,
            SceneLivePeak = scenePeak,
        };
    }

    static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var a = values.ToArray();
        Array.Sort(a);
        int idx = (int)Math.Clamp(Math.Round(p * (a.Length - 1)), 0, a.Length - 1);
        return a[idx];
    }

    static double Max(List<double> values)
    {
        double m = 0;
        foreach (var v in values) if (v > m) m = v;
        return m;
    }

    static string BenchOutDir()
    {
        string? dir = Environment.GetEnvironmentVariable("WAVEE_BENCH_OUT");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "bench");
        return dir;
    }

    static string VersionLabel()
        => typeof(WaveePerfBench).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    static string ToJson(List<ScenarioResult> results)
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(1024);
        sb.Append("{\"version\":\"").Append(Escape(VersionLabel())).Append("\",\"processors\":").Append(Environment.ProcessorCount).Append(",\"scenarios\":[");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(Escape(r.Name)).Append("\",\"frames\":").Append(r.Frames)
              .Append(",\"durationSec\":").Append(N(r.DurationSec))
              .Append(",\"cpuAvgPct\":").Append(N(r.CpuAvgPct))
              .Append(",\"cpuPeakPct\":").Append(N(r.CpuPeakPct))
              .Append(",\"workingSetAvgMB\":").Append(N(r.WorkingSetAvgMB))
              .Append(",\"workingSetPeakMB\":").Append(N(r.WorkingSetPeakMB))
              .Append(",\"privatePeakMB\":").Append(N(r.PrivatePeakMB))
              .Append(",\"managedPeakMB\":").Append(N(r.ManagedPeakMB))
              .Append(",\"frameMsP50\":").Append(N(r.FrameMsP50))
              .Append(",\"frameMsP90\":").Append(N(r.FrameMsP90))
              .Append(",\"frameMsMax\":").Append(N(r.FrameMsMax))
              .Append(",\"imagesPeak\":").Append(r.ImagesPeak)
              .Append(",\"sceneLivePeak\":").Append(r.SceneLivePeak).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static int EnvInt(string key, int def, int lo, int hi)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) && v >= lo && v <= hi ? v : def;
}
