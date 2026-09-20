using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace Wavee;

// WAVEE_MEM_SOAK=1: a DISTINCT-ENTITY memory-growth harness for the week-scale leak analysis. It navigates to
// WAVEE_MEM_SOAK_N (default 3000) DISTINCT synthetic entity URIs — album/artist/playlist with incrementing ids, which the
// fake backend synthesizes a populated detail for (run with --fake) — periodically going Home (to exercise the HomePage
// remount path). Every WAVEE_MEM_SOAK_EVERY (default 100) navigations it forces a full GC and samples the RETAINED
// managed heap (GC.GetTotalMemory(true) — the floor, i.e. the actual leak, not the transient GC sawtooth) plus the working
// set and the engine census (image count/bytes, scene live, interned strings). It writes a CSV (<logs>\artifacts + stderr) so the
// managed-floor-vs-navigations slope can be fit and extrapolated to a real usage rate. Compare a fixed build to a pre-fix
// build (git stash) to see the slope flatten.
internal static class WaveeMemSoak
{
    static readonly WaveeLogger Log = new(WaveeLog.Instance, "probe");

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (!Diag.EnvFlag("WAVEE_MEM_SOAK")) return false;
        WaveeLog.Instance.SetEcho(Console.Error.WriteLine);   // env-gated run only: mirror probe progress to the terminal
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Log.Warn("[mem-soak] unavailable: requires Win32Window + D3D12Device");
            return true;
        }
        int N = EnvInt("WAVEE_MEM_SOAK_N", 3000, 50, 200000);
        int every = EnvInt("WAVEE_MEM_SOAK_EVERY", 100, 10, 100000);
        int settleFrames = EnvInt("WAVEE_MEM_SOAK_SETTLE", 3, 1, 60);

        // Warm up + wait for the shell to wire its nav hook (DiagnosticRun fires before the first frame).
        for (int i = 0; i < 600 && WaveeShell.ProbeNav is null && !w.IsClosed; i++)
        {
            gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame();
        }
        if (WaveeShell.ProbeNav is null) { Log.Warn("[mem-soak] nav hook not wired (shell not mounted?)"); return true; }
        var nav = WaveeShell.ProbeNav!;

        void Frame() { if (!w.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !w.IsClosed; i++) Frame(); }

        var sb = new StringBuilder(1 << 16);
        sb.AppendLine("nav,elapsedSec,managedFloorMB,workingSetMB,privateMB,imagesCount,imagesUsedMB,sceneLive,stringsMap,stringsIdHigh,components");
        var proc = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();

        void Sample(int navs)
        {
            long managed = GC.GetTotalMemory(forceFullCollection: true);   // RETAINED floor after a full collection = the leak signal
            proc.Refresh();
            var s = CensusSnapshot.Capture(host);
            static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            sb.Append(navs).Append(',').Append((sw.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(F(managed / 1048576.0)).Append(',')
              .Append(F(proc.WorkingSet64 / 1048576.0)).Append(',')
              .Append(F(proc.PrivateMemorySize64 / 1048576.0)).Append(',')
              .Append(s.ImageCount).Append(',').Append(F(s.ImageUsedBytes / 1048576.0)).Append(',')
              .Append(s.SceneLive).Append(',').Append(s.StringMap).Append(',').Append(s.StringIdHighWater).Append(',').Append(s.Components).AppendLine();
            Log.Info($"[mem-soak] nav={navs,6} managed={managed / 1048576.0,6:0.0}MB ws={proc.WorkingSet64 / 1048576.0,6:0.0}MB images={s.ImageCount,4} scene={s.SceneLive,4} strings={s.StringMap,4}");
        }

        Log.Info($"[mem-soak] driving {N} distinct-entity navigations (sample every {every})");
        nav("home", null); Settle(20);
        Sample(0);

        for (int i = 1; i <= N && !w.IsClosed; i++)
        {
            var (key, label) = SoakRoute(i);
            nav(key, label);
            Settle(settleFrames);
            if (i % 25 == 0) { nav("home", null); Settle(settleFrames); }   // exercise the HomePage remount path
            if (i % every == 0) Sample(i);
        }
        Sample(N);

        string csv = sb.ToString();
        try { Directory.CreateDirectory(ProbeArtifacts.Dir); File.WriteAllText(ProbeArtifacts.PathFor("wavee-mem-soak.csv"), csv); Log.Info("[mem-soak] wrote " + ProbeArtifacts.PathFor("wavee-mem-soak.csv")); } catch { }
        Log.Info("=== MEM-SOAK CSV BEGIN ===");
        Log.Info(csv);
        Log.Info("=== MEM-SOAK CSV END ===");
        return true;
    }

    // Distinct synthetic route key per index, matching the app's detail route-key format (HomePage.NavCard / WaveeNavProbe).
    static (string Key, string Label) SoakRoute(int i)
    {
        string n = i.ToString(CultureInfo.InvariantCulture);
        return (i % 3) switch
        {
            0 => ("album:spotify:album:soak" + n, "Album " + n),
            1 => ("artist:spotify:artist:soak" + n, "Artist " + n),
            _ => ("pl:spotify:playlist:soak" + n, "Playlist " + n),
        };
    }

    static int EnvInt(string key, int def, int lo, int hi)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) && v >= lo && v <= hi ? v : def;
}
