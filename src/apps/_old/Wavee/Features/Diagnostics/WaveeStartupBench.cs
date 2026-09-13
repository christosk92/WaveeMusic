using System;
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

// WAVEE_STARTUP_BENCH=1 (or --startup-bench): report-only process-start → first-present → session-restored timings.
// Not a CI gate. DiagnosticRun fires after window.Show() and before the first frame, so this probe pumps frames
// until the marks land, prints a summary, and returns true (takes over the run, same as WaveePerfBench).
//
// Timing definitions (also in docs/guide/startup-bench.md):
//   process-start     OS process creation (Process.StartTime).
//   first-present     first successful D3D12 Present (D3D12Device.FirstPresentQpc), else first frame with
//                     AppHost.LastStats.Presented.
//   session-restored  first frame after WaveeShell.ProbeNav becomes non-null. RestoreSessionNav runs at shell
//                     init on the same mount that wires that hook — the probe does not timestamp the restore
//                     call itself (WaveeShell is owned by another agent).
internal static class WaveeStartupBench
{
    static readonly WaveeLogger Log = new(WaveeLog.Instance, "probe");

    /// <summary>Stashed from DiagnosticRun so Settings → About can read LastStats on a 5s timer (not per-frame).</summary>
    internal static AppHost? Host { get; private set; }

    internal static void NoteHost(AppHost host) => Host = host;

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (!Diag.EnvFlag("WAVEE_STARTUP_BENCH")) return false;
        WaveeLog.Instance.SetEcho(Console.Error.WriteLine);
        NoteHost(host);
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Log.Warn("[startup-bench] unavailable: requires Win32Window + D3D12Device");
            return true;
        }

        var proc = Process.GetCurrentProcess();
        DateTime processStart = proc.StartTime;
        long qpc0 = Stopwatch.GetTimestamp();
        double alreadyMs = (DateTime.Now - processStart).TotalMilliseconds;

        double? firstPresentMs = null;
        double? sessionRestoredMs = null;
        int frames = 0;
        const int maxFrames = 1200;

        void Frame()
        {
            if (w.IsClosed) return;
            gpu.SuppressLatencyWaitOnce();
            gpu.SuppressVsyncOnce();
            host.RunFrame();
            frames++;
        }

        double ElapsedMs() => alreadyMs + (Stopwatch.GetTimestamp() - qpc0) * 1000.0 / Stopwatch.Frequency;

        for (int i = 0; i < maxFrames && !w.IsClosed && (firstPresentMs is null || sessionRestoredMs is null); i++)
        {
            Frame();
            if (firstPresentMs is null)
            {
                long qpc = D3D12Device.FirstPresentQpc;
                if (qpc != 0)
                    firstPresentMs = alreadyMs + (qpc - qpc0) * 1000.0 / Stopwatch.Frequency;
                else if (host.LastStats.Presented)
                    firstPresentMs = ElapsedMs();
            }
            if (sessionRestoredMs is null && WaveeShell.ProbeNav is not null)
                sessionRestoredMs = ElapsedMs();
        }

        proc.Refresh();
        var gpuMem = D3D12Device.LastVideoMemory;
        string json = ToJson(alreadyMs, firstPresentMs, sessionRestoredMs, frames, proc, gpuMem);
        string report = ToReport(alreadyMs, firstPresentMs, sessionRestoredMs, frames, proc, gpuMem);

        string outDir = BenchOutDir();
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "wavee-startup-latest.json"), json);
        File.WriteAllText(Path.Combine(outDir, "wavee-startup-latest.txt"), report);
        File.WriteAllText(Path.Combine(outDir, $"wavee-startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), json);

        Log.Info(report);
        Log.Info("=== STARTUP-BENCH JSON BEGIN ===");
        Log.Info(json);
        Log.Info("=== STARTUP-BENCH JSON END ===");
        return true;
    }

    static string ToReport(double diagnosticRunMs, double? firstPresentMs, double? sessionRestoredMs,
        int frames, Process proc, GpuVideoMemorySnapshot gpu)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE STARTUP BENCH (report-only, not a CI gate) ===");
        sb.AppendLine($"version={VersionLabel()}  processors={Environment.ProcessorCount}");
        sb.AppendLine("definitions:");
        sb.AppendLine("  process-start     OS process creation (Process.StartTime)");
        sb.AppendLine("  first-present     first successful D3D12 Present (D3D12Device.FirstPresentQpc)");
        sb.AppendLine("  session-restored  first frame after WaveeShell.ProbeNav != null");
        sb.AppendLine("                    (RestoreSessionNav runs at that shell mount)");
        sb.AppendLine($"diagnosticRunMs={N(diagnosticRunMs)}  framesPumped={frames}");
        sb.AppendLine($"firstPresentMs={N(firstPresentMs)}  sessionRestoredMs={N(sessionRestoredMs)}");
        sb.AppendLine($"workingSetMB={N(proc.WorkingSet64 / 1048576.0)}  managedMB={N(GC.GetTotalMemory(false) / 1048576.0)}");
        if (gpu.Valid)
        {
            sb.AppendLine($"gpuLocalMB={N(gpu.LocalCurrentUsage / 1048576.0)}/{N(gpu.LocalBudget / 1048576.0)}  gpuNonLocalMB={N(gpu.NonLocalCurrentUsage / 1048576.0)}/{N(gpu.NonLocalBudget / 1048576.0)}");
            sb.AppendLine($"trackedD3D={gpu.TrackedResourceCount} {N(gpu.TrackedResourceBytes / 1048576.0)}MB  atlas={gpu.AtlasImages}/{gpu.AtlasPages}  glyphs={gpu.CachedGlyphs}");
        }
        return sb.ToString();
    }

    static string ToJson(double diagnosticRunMs, double? firstPresentMs, double? sessionRestoredMs,
        int frames, Process proc, GpuVideoMemorySnapshot gpu)
    {
        var sb = new StringBuilder(512);
        sb.Append("{\"version\":\"").Append(Escape(VersionLabel()))
          .Append("\",\"processors\":").Append(Environment.ProcessorCount)
          .Append(",\"diagnosticRunMs\":").Append(N(diagnosticRunMs))
          .Append(",\"firstPresentMs\":").Append(firstPresentMs is { } a ? N(a) : "null")
          .Append(",\"sessionRestoredMs\":").Append(sessionRestoredMs is { } b ? N(b) : "null")
          .Append(",\"framesPumped\":").Append(frames)
          .Append(",\"workingSetMB\":").Append(N(proc.WorkingSet64 / 1048576.0))
          .Append(",\"managedMB\":").Append(N(GC.GetTotalMemory(false) / 1048576.0));
        if (gpu.Valid)
        {
            sb.Append(",\"gpuLocalUsageMB\":").Append(N(gpu.LocalCurrentUsage / 1048576.0))
              .Append(",\"gpuLocalBudgetMB\":").Append(N(gpu.LocalBudget / 1048576.0))
              .Append(",\"gpuNonLocalUsageMB\":").Append(N(gpu.NonLocalCurrentUsage / 1048576.0))
              .Append(",\"gpuNonLocalBudgetMB\":").Append(N(gpu.NonLocalBudget / 1048576.0))
              .Append(",\"trackedResourceBytes\":").Append(gpu.TrackedResourceBytes)
              .Append(",\"trackedResourceCount\":").Append(gpu.TrackedResourceCount)
              .Append(",\"atlasImages\":").Append(gpu.AtlasImages)
              .Append(",\"atlasPages\":").Append(gpu.AtlasPages)
              .Append(",\"cachedGlyphs\":").Append(gpu.CachedGlyphs);
        }
        sb.Append('}');
        return sb.ToString();
    }

    static string BenchOutDir()
    {
        string? dir = Environment.GetEnvironmentVariable("WAVEE_BENCH_OUT");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "bench");
        return dir;
    }

    static string VersionLabel()
        => typeof(WaveeStartupBench).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string N(double? v) => v is { } x ? N(x) : "n/a";
    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
