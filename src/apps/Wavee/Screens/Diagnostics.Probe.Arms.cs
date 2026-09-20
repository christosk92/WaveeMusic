// ── Screens/Diagnostics.Probe.Arms.cs ──────────────────────────────────────────────────────────────────────────────
// the Wave-6 CLI probe arms: --qr-dump, --relaunch-after (the restart broker), --perf-bench, --startup-bench,
// --crash-probe, --lyrics-advance-probe, the probe-out directory, and the NotificationSimulator ("Send event")
//
// Role: SHELL
// Owner: S
// Wave: 6
// Budget: 800 lines
// Spec: plan §2 `Diagnostics.Probe.cs` row + ch 22 §9 (a) + ch 27 §9.3 + ch 28 §1.5; gaps G-016, G-153, G-218
//
// THE NAMED PARTIAL G-016 ASKS FOR. `Diagnostics.Probe.cs`'s 800 lines are spent by the headless arm, so the arms owner S
// owes in Wave 6 live here, beside it, in the same `Diagnostics.Probe` class. Every DECISION they make — the argv parse,
// the simulator's verdict table, the bench math and its JSON, the QR PNG writer — is PURE (`ProbeOptions`, `SimRules`,
// `Bench`, `QrPng`, section 4 below) and pinned by `ProbeArmsTests`; sections 1-3 only drive the process, the frame loop
// and the OS. The pure half sits in its own section HERE rather than in `Diagnostics.cs` because that CORE file already
// carries the log view, the reports and the lyrics inspector's rules and would pass its 600 by more than 30 %.
//
// NO ENVIRONMENT VARIABLE ANYWHERE (CLAUDE.md). 0.2.9 read WAVEE_PERF_BENCH / WAVEE_STARTUP_BENCH /
// WAVEE_LYRICS_ADVANCE_PROBE / WAVEE_BENCH_OUT / WAVEE_PROBE_*_FRAMES; every one is a flag now (`ProbeOptions.Parse`).
//
// TWO DOORS. `TryRunCliArm` runs from `Probe.TryRun` before anything else of the arm table (no window: --qr-dump and the
// broker). `InstallGuiArms` runs once from `Diagnostics.Install()` before `Shell.Run()`: it owns `FluentApp.DiagnosticRun`
// — the one pre-loop hook that holds the AppHost — so the ambient power policy attaches there on EVERY launch, and a bench
// or the lyrics probe takes the loop over only when its flag was passed.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Packaging;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static AppHost? s_host;
        static ProbeOptions s_options;
        static int s_guiInstalled;
        static bool s_echo;

        /// <summary>The host, stashed by the pre-loop hook. Settings ▸ About's receipts read <c>Host.LastStats</c> on their
        /// 5 s timer (UI thread); null before the window exists and in the headless arm.</summary>
        public static AppHost? Host => s_host;

        // ══ 1. THE WINDOWLESS ARMS ══════════════════════════════════════════════════════════════════════════════════

        /// <summary>`--relaunch-after &lt;pid&gt;` and `--qr-dump [text] [out.png]`. False for anything else.</summary>
        internal static bool TryRunCliArm(string[] args, out int code)
        {
            code = 0;
            if (Array.IndexOf(args, "--relaunch-after") >= 0) { code = RelaunchBroker(args); return true; }
            if (Array.IndexOf(args, "--qr-dump") >= 0) { code = QrDump(args); return true; }
            return false;
        }

        /// <summary>The restart broker (0.2.9 Program.cs's first arm): a COURIER, not an app instance. It waits for the process
        /// that spawned it to exit — which is what releases the single-instance mutex and unlocks library.db — then starts a
        /// fresh Wavee and exits. It never touches a pending factory-reset marker: consuming it here would run the wipe while
        /// the old process still holds those files, and the relaunch that must act on it would find nothing.</summary>
        static int RelaunchBroker(string[] args)
        {
            int at = Array.IndexOf(args, "--relaunch-after");
            if (at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid))
            {
                // A 15 s ceiling: a hung parent must not strand the user without a window. ArgumentException = the pid is
                // already gone (the common case — it exited while we were starting), which is exactly what we waited for.
                try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(15_000); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            try
            {
                // A packaged build is re-activated through the shell by AUMID: starting the exe directly gives the new process
                // no package identity (no MSIX activation, no manifest protocol/startup registration).
                if (PackageIdentity.IsPackaged && PackageIdentity.ApplicationUserModelId is { Length: > 0 } aumid)
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true })?.Dispose();
                else if (Environment.ProcessPath is { Length: > 0 } self)
                    Process.Start(new ProcessStartInfo(self) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A factory-reset marker (if any) stays on disk, so a manual launch still applies the wipe.
                Log.Warn("app", "the relaunch broker could not start Wavee", ex);
            }
            return 0;
        }

        /// <summary>Encode with the REAL encoder the sign-in QR uses and emit it two ways: an ASCII structural dump on stdout
        /// and a crisp PNG. Isolates the encoder from the renderer — a PNG that scans beside an in-app QR that does not is a
        /// rendering fault, not a bitstream one (ch 28 §1.5).</summary>
        static int QrDump(string[] args)
        {
            AttachParentConsole();
            int at = Array.IndexOf(args, "--qr-dump");
            string text = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : "https://spotify.com/pair";
            string path = at + 2 < args.Length && !args[at + 2].StartsWith("--", StringComparison.Ordinal) ? args[at + 2] : "qr.png";
            bool[,] m;
            try { m = Setup.Qr.Encode(text, Setup.Qr.Ecc.M); }
            catch (ArgumentException ex) { Console.Error.WriteLine("QR encode failed: " + ex.Message); return 1; }

            int n = m.GetLength(0);
            var sb = new StringBuilder((n + 4) * (n + 4) * 2 + n + 8).Append('\n');
            for (int y = -2; y < n + 2; y++)
            {
                for (int x = -2; x < n + 2; x++) sb.Append((uint)x < (uint)n && (uint)y < (uint)n && m[x, y] ? "██" : "  ");
                sb.Append('\n');
            }
            Console.Out.Write(sb.ToString());
            try { File.WriteAllBytes(path, QrPng.Encode(m)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine("QR png could not be written: " + ex.Message);
                return 1;
            }
            int px = (n + 8) * 14;
            Console.Out.WriteLine("QR for \"" + text + "\": " + n + "x" + n + " modules -> " + px + "x" + px + "px PNG at " + Path.GetFullPath(path));
            return 0;
        }

        // ══ 2. THE GUI ARMS ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Called ONCE by <c>Diagnostics.Install()</c>, before <c>Shell.Run()</c>. Idempotent.</summary>
        internal static void InstallGuiArms(string[] args)
        {
            if (Interlocked.Exchange(ref s_guiInstalled, 1) != 0) return;
            s_options = ProbeOptions.Parse(args);
            if (s_options.WantsConsole) { AttachParentConsole(); s_echo = true; }
            // Silence the lyrics surface's async stepper BEFORE anything mounts, so the advance probe alone drives the media
            // clock synchronously (0.2.9 WaveeApp.cs:34-40, minus the env read).
            if (s_options.LyricsAdvance) Lyrics.ViewCore.ProbeSyncMode = true;

            FluentApp.DiagnosticRun = static (host, window, device) =>
            {
                Platform.AttachAmbientPower(host);
                s_host = host;
                return TryStartupBench(host, window, device) || TryPerfBench(host, window, device) || TryLyricsAdvanceProbe(host, window, device);
            };

            if (s_options.CrashProbe.Length > 0)
                Log.Event(WaveeLogLevel.Warning, "crash", "crash.probe.armed", "--crash-probe armed: the report chrome crashes the process 2 s after it mounts",
                    null, -1, null, WaveeLogField.Of("mode", s_options.CrashProbe));
        }

        /// <summary>`--crash-probe [throw|failfast]` → "throw" | "failfast", null without the flag. The report chrome
        /// (`Feedback.UI.cs`, owner R — 0.2.9's ReportChrome) owns the 2 s timer and the crash itself, so the whole
        /// crash-prompt pipeline (managed report vs. WER dump vs. unclean exit) rehearses end to end through the surface
        /// that consumes it; `Diagnostics.Install` hands this to its `Feedback.CrashProbeMode` seam.</summary>
        internal static string? CrashProbeMode => s_options.CrashProbe is { Length: > 0 } mode ? mode : null;

        static string OutDir() => s_options.ProbeOut.Length > 0 ? s_options.ProbeOut : Path.Combine(Platform.LocalFolder, "bench");

        /// <summary>Progress: the log always, and stderr for a CLI-launched run (the parent console is attached).</summary>
        static void Say(string line)
        {
            Log.Info("probe", line);
            if (s_echo) { try { Console.Error.WriteLine(line); } catch (IOException) { } }
        }

        static void WriteArtifact(string dir, string name, string text)
        {
            try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, name), text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Warn("probe", "could not write " + name + " to " + dir, ex);
            }
        }

        static string VersionLabel()
            => typeof(Probe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        static void FrameFast(AppHost host, Win32Window w, D3D12Device gpu)
        {
            if (w.IsClosed) return;
            gpu.SuppressLatencyWaitOnce();
            gpu.SuppressVsyncOnce();
            host.RunFrame();
        }

        /// <summary>Pump frames until the content host has activated its first route (the shell mounted and restored its
        /// navigation) — the 0.3 form of 0.2.9's "WaveeShell.ProbeNav is not null".</summary>
        static bool WarmUntilShell(AppHost host, Win32Window w, D3D12Device gpu, int maxFrames)
        {
            for (int i = 0; i < maxFrames && NavigationFrameWatch.NavigationId == 0 && !w.IsClosed; i++) FrameFast(host, w, gpu);
            return NavigationFrameWatch.NavigationId > 0;
        }

        // ── 2.1 --startup-bench (report-only: process start → first present → session restored) ─────────────────────

        static bool TryStartupBench(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            if (!s_options.StartupBench) return false;
            if (window is not Win32Window w || device is not D3D12Device gpu) { Say("[startup-bench] unavailable: requires Win32Window + D3D12Device"); return true; }

            using var proc = Process.GetCurrentProcess();
            long qpc0 = Stopwatch.GetTimestamp();
            double alreadyMs = (DateTime.Now - proc.StartTime).TotalMilliseconds;
            double firstPresentMs = -1, sessionRestoredMs = -1;
            int frames = 0;
            double ElapsedMs() => alreadyMs + (Stopwatch.GetTimestamp() - qpc0) * 1000.0 / Stopwatch.Frequency;

            for (int i = 0; i < 1200 && !w.IsClosed && (firstPresentMs < 0 || sessionRestoredMs < 0); i++)
            {
                FrameFast(host, w, gpu);
                frames++;
                if (firstPresentMs < 0)
                {
                    long qpc = D3D12Device.FirstPresentQpc;
                    if (qpc != 0) firstPresentMs = alreadyMs + (qpc - qpc0) * 1000.0 / Stopwatch.Frequency;
                    else if (host.LastStats.Presented) firstPresentMs = ElapsedMs();
                }
                if (sessionRestoredMs < 0 && NavigationFrameWatch.NavigationId > 0) sessionRestoredMs = ElapsedMs();
            }

            proc.Refresh();
            var mem = D3D12Device.LastVideoMemory;
            double wsMb = proc.WorkingSet64 / 1048576.0, managedMb = GC.GetTotalMemory(false) / 1048576.0;
            string Opt(double v) => v >= 0 ? Bench.N(v) : "n/a";

            var json = new StringBuilder(512)
                .Append("{\"version\":\"").Append(Bench.Escape(VersionLabel())).Append("\",\"processors\":").Append(Bench.Int(Environment.ProcessorCount))
                .Append(",\"diagnosticRunMs\":").Append(Bench.N(alreadyMs))
                .Append(",\"firstPresentMs\":").Append(firstPresentMs >= 0 ? Bench.N(firstPresentMs) : "null")
                .Append(",\"sessionRestoredMs\":").Append(sessionRestoredMs >= 0 ? Bench.N(sessionRestoredMs) : "null")
                .Append(",\"framesPumped\":").Append(Bench.Int(frames))
                .Append(",\"workingSetMB\":").Append(Bench.N(wsMb)).Append(",\"managedMB\":").Append(Bench.N(managedMb));
            var report = new StringBuilder(1024).AppendLine()
                .AppendLine("=== WAVEE STARTUP BENCH (report-only, not a CI gate) ===")
                .AppendLine("version=" + VersionLabel() + "  processors=" + Environment.ProcessorCount)
                .AppendLine("definitions:")
                .AppendLine("  process-start     OS process creation (Process.StartTime)")
                .AppendLine("  first-present     first successful D3D12 Present (D3D12Device.FirstPresentQpc)")
                .AppendLine("  session-restored  first frame after the content host activated its first route")
                .AppendLine("diagnosticRunMs=" + Bench.N(alreadyMs) + "  framesPumped=" + frames)
                .AppendLine("firstPresentMs=" + Opt(firstPresentMs) + "  sessionRestoredMs=" + Opt(sessionRestoredMs))
                .AppendLine("workingSetMB=" + Bench.N(wsMb) + "  managedMB=" + Bench.N(managedMb));
            if (mem.Valid)
            {
                json.Append(",\"gpuLocalUsageMB\":").Append(Bench.N(mem.LocalCurrentUsage / 1048576.0))
                    .Append(",\"gpuLocalBudgetMB\":").Append(Bench.N(mem.LocalBudget / 1048576.0))
                    .Append(",\"gpuNonLocalUsageMB\":").Append(Bench.N(mem.NonLocalCurrentUsage / 1048576.0))
                    .Append(",\"gpuNonLocalBudgetMB\":").Append(Bench.N(mem.NonLocalBudget / 1048576.0))
                    .Append(",\"trackedResourceBytes\":").Append(Bench.Int(mem.TrackedResourceBytes))
                    .Append(",\"trackedResourceCount\":").Append(Bench.Int(mem.TrackedResourceCount))
                    .Append(",\"atlasImages\":").Append(Bench.Int(mem.AtlasImages)).Append(",\"atlasPages\":").Append(Bench.Int(mem.AtlasPages))
                    .Append(",\"cachedGlyphs\":").Append(Bench.Int(mem.CachedGlyphs));
                report.AppendLine("gpuLocalMB=" + Bench.N(mem.LocalCurrentUsage / 1048576.0) + "/" + Bench.N(mem.LocalBudget / 1048576.0)
                    + "  gpuNonLocalMB=" + Bench.N(mem.NonLocalCurrentUsage / 1048576.0) + "/" + Bench.N(mem.NonLocalBudget / 1048576.0))
                    .AppendLine("trackedD3D=" + mem.TrackedResourceCount + " " + Bench.N(mem.TrackedResourceBytes / 1048576.0) + "MB  atlas="
                    + mem.AtlasImages + "/" + mem.AtlasPages + "  glyphs=" + mem.CachedGlyphs);
            }
            json.Append('}');

            string dir = OutDir(), jsonText = json.ToString(), reportText = report.ToString();
            WriteArtifact(dir, "wavee-startup-latest.json", jsonText);
            WriteArtifact(dir, "wavee-startup-latest.txt", reportText);
            WriteArtifact(dir, "wavee-startup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json", jsonText);
            Say(reportText);
            Say("=== STARTUP-BENCH JSON BEGIN ===");
            Say(jsonText);
            Say("=== STARTUP-BENCH JSON END ===");
            return true;
        }

        // ── 2.2 --perf-bench (CPU + memory across idle / home / artist / playlist-open / nav-burst) ─────────────────

        static bool TryPerfBench(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            if (!s_options.PerfBench) return false;
            if (window is not Win32Window w || device is not D3D12Device gpu) { Say("[perf-bench] unavailable: requires Win32Window + D3D12Device"); return true; }
            if (!WarmUntilShell(host, w, gpu, 600)) { Say("[perf-bench] the shell never activated a route (Shell.RouteNoted not wired?)"); return true; }

            var results = new List<Bench.Scenario>(5) { RunIdle(host, w) };
            Nav(new Shell.Route(Shell.RouteKind.Home)); Settle(host, w, gpu, 40);
            results.Add(RunFrames(host, w, gpu, "home", 180));
            // `--fake`'s own ids (Entities.Fake.cs): ar0…ar11, al0…al12, pl0…pl6 — 0.2.9's `soakbench`/`bench` ids do not exist here.
            Nav(Shell.For(EntityUri.Parse("spotify:artist:ar0"), "Bench artist")); Settle(host, w, gpu, 50);
            results.Add(RunFrames(host, w, gpu, "artist-detail", 120));
            Nav(new Shell.Route(Shell.RouteKind.Home)); Settle(host, w, gpu, 20);
            results.Add(RunPlaylistOpen(host, w, gpu));
            Nav(new Shell.Route(Shell.RouteKind.Home)); Settle(host, w, gpu, 20);
            results.Add(RunNavBurst(host, w, gpu));

            string dir = OutDir(), json = Bench.PerfJson(VersionLabel(), Environment.ProcessorCount, results);
            string jsonPath = Path.Combine(dir, "wavee-perf-latest.json");
            WriteArtifact(dir, "wavee-perf-latest.json", json);
            WriteArtifact(dir, "wavee-perf-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json", json);

            var sb = new StringBuilder(2048).AppendLine()
                .AppendLine("=== WAVEE PERF BENCH (CPU + memory) ===")
                .AppendLine("version=" + VersionLabel() + "  processors=" + Environment.ProcessorCount)
                .AppendLine("output=" + jsonPath).AppendLine()
                .AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-16} {1,5} {2,6} {3,7} {4,7} {5,8} {6,7} {7,9} {8,9} {9,9}",
                    "scenario", "dur", "cpu%", "cpuPk%", "wsMB", "wsPkMB", "privPk", "frameP50", "frameP90", "frameMax"));
            foreach (var r in results)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-16} {1,5:0.00} {2,6:0.0} {3,7:0.0} {4,7:0.0} {5,8:0.0} {6,7:0.0} {7,9:0.00} {8,9:0.00} {9,9:0.00}",
                    r.Name, r.DurationSec, r.CpuAvgPct, r.CpuPeakPct, r.WorkingSetAvgMB, r.WorkingSetPeakMB, r.PrivatePeakMB,
                    r.FrameMsP50, r.FrameMsP90, r.FrameMsMax));
            string report = sb.ToString();
            WriteArtifact(dir, "wavee-perf-latest.txt", report);
            Say(report);
            Say("=== PERF-BENCH JSON BEGIN ===");
            Say(json);
            Say("=== PERF-BENCH JSON END ===");
            return true;
        }

        static readonly HashSet<Shell.RouteKind> s_pagelessNoted = new();

        /// <summary>Navigate through the SAME verb a click uses. A kind with no registered page (Wave 5 not landed) still
        /// times the frame and the empty body — said once per kind, so the numbers are never mistaken for a page's.</summary>
        static void Nav(in Shell.Route route)
        {
            if (Shell.PageFor(route) is null && s_pagelessNoted.Add(route.Kind))
                Say("[perf-bench] route kind " + route.Kind + " has no page registered yet: its scenario times the frame + the empty body");
            Shell.GoTo(route);
        }

        static void Settle(AppHost host, Win32Window w, D3D12Device gpu, int n)
        {
            for (int i = 0; i < n && !w.IsClosed; i++) FrameFast(host, w, gpu);
        }

        /// <summary>Real-time idle: paced frames like a normal run (the Task Manager idle baseline).</summary>
        static Bench.Scenario RunIdle(AppHost host, Win32Window w)
        {
            using var proc = Process.GetCurrentProcess();
            var samples = new List<Bench.Sample>();
            var frameMs = new List<double>(1024);
            var sw = Stopwatch.StartNew();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            long tick0 = Stopwatch.GetTimestamp();
            while (sw.Elapsed.TotalSeconds < s_options.IdleSec && !w.IsClosed)
            {
                host.RunFrame();
                frameMs.Add(host.LastStats.FrameMs);
                w.WaitForWork(Math.Min(host.RecommendedWaitMs(), 16));
                double t = sw.Elapsed.TotalSeconds;
                if (samples.Count == 0 ? t >= 1.0 : t - samples[^1].ElapsedSec >= 1.0) samples.Add(Capture(proc, cpu0, tick0, host, frameMs));
            }
            if (samples.Count == 0) samples.Add(Capture(proc, cpu0, tick0, host, frameMs));
            return Bench.Aggregate("idle", frameMs.Count, sw.Elapsed.TotalSeconds, samples, frameMs);
        }

        static Bench.Scenario RunFrames(AppHost host, Win32Window w, D3D12Device gpu, string name, int frames)
        {
            using var proc = Process.GetCurrentProcess();
            var samples = new List<Bench.Sample>();
            var frameMs = new List<double>(frames);
            var sw = Stopwatch.StartNew();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            long tick0 = Stopwatch.GetTimestamp();
            int every = Math.Max(30, frames / 6);
            for (int i = 0; i < frames; i++)
            {
                FrameFast(host, w, gpu);
                frameMs.Add(host.LastStats.FrameMs);
                if (i % every == 0) samples.Add(Capture(proc, cpu0, tick0, host, frameMs));
            }
            samples.Add(Capture(proc, cpu0, tick0, host, frameMs));
            return Bench.Aggregate(name, frames, sw.Elapsed.TotalSeconds, samples, frameMs);
        }

        /// <summary>Playlist OPEN: each hop leaves for Home and re-enters the SAME playlist, so every hop pays the full open;
        /// frameMsMax is the "it lags when I open a playlist" number. The path-tessellation and density-geometry deltas are
        /// logged beside it: geometry re-minted on every refresh pass shows up as hundreds per open.</summary>
        static Bench.Scenario RunPlaylistOpen(AppHost host, Win32Window w, D3D12Device gpu)
        {
            using var proc = Process.GetCurrentProcess();
            var samples = new List<Bench.Sample>();
            var frameMs = new List<double>();
            var sw = Stopwatch.StartNew();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            long tick0 = Stopwatch.GetTimestamp();
            var paths = FluentGpu.Render.PathRealizationCache.Shared;
            int tess0 = paths.TessellationCount, real0 = paths.RealizationCount, builds0 = FluentGpu.Controls.DensityPlot.GeometryBuilds;
            var playlist = Shell.For(EntityUri.Parse("spotify:playlist:pl1"), "Bench playlist");
            var home = new Shell.Route(Shell.RouteKind.Home);
            int hops = s_options.OpenHops;
            for (int i = 0; i < hops; i++)
            {
                Nav(playlist);
                for (int f = 0; f < 12; f++) { FrameFast(host, w, gpu); frameMs.Add(host.LastStats.FrameMs); }
                Nav(home);
                for (int f = 0; f < 4; f++) { FrameFast(host, w, gpu); frameMs.Add(host.LastStats.FrameMs); }
                samples.Add(Capture(proc, cpu0, tick0, host, frameMs));
            }
            int builds = FluentGpu.Controls.DensityPlot.GeometryBuilds - builds0;
            Say("[perf-bench] playlist-open: " + hops + " opens, densityGeometryBuilds=" + builds
                + " (" + Bench.N((double)builds / hops) + " per open), path realizations=" + (paths.RealizationCount - real0)
                + " tessellations=" + (paths.TessellationCount - tess0) + ", slabBytes=" + paths.SlabBytes);
            return Bench.Aggregate("playlist-open", frameMs.Count, sw.Elapsed.TotalSeconds, samples, frameMs);
        }

        static Bench.Scenario RunNavBurst(AppHost host, Win32Window w, D3D12Device gpu)
        {
            using var proc = Process.GetCurrentProcess();
            var samples = new List<Bench.Sample>();
            var frameMs = new List<double>();
            var sw = Stopwatch.StartNew();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            long tick0 = Stopwatch.GetTimestamp();
            var home = new Shell.Route(Shell.RouteKind.Home);
            int hops = s_options.NavHops;
            for (int i = 0; i < hops; i++)
            {
                string uri = (i % 3) switch
                {
                    0 => "spotify:album:al" + Entities.Wrap(i, 13).ToString(CultureInfo.InvariantCulture),
                    1 => "spotify:artist:ar" + Entities.Wrap(i, 12).ToString(CultureInfo.InvariantCulture),
                    _ => "spotify:playlist:pl" + Entities.Wrap(i, 7).ToString(CultureInfo.InvariantCulture),
                };
                Nav(Shell.For(EntityUri.Parse(uri), "Bench " + i.ToString(CultureInfo.InvariantCulture)));
                for (int f = 0; f < 8; f++) { FrameFast(host, w, gpu); frameMs.Add(host.LastStats.FrameMs); }
                if (i % 3 == 0) { Nav(home); for (int f = 0; f < 4; f++) { FrameFast(host, w, gpu); frameMs.Add(host.LastStats.FrameMs); } }
                samples.Add(Capture(proc, cpu0, tick0, host, frameMs));
            }
            return Bench.Aggregate("nav-burst", frameMs.Count, sw.Elapsed.TotalSeconds, samples, frameMs);
        }

        static Bench.Sample Capture(Process proc, TimeSpan cpu0, long tick0, AppHost host, List<double> frameMs)
        {
            proc.Refresh();
            double elapsedSec = (Stopwatch.GetTimestamp() - tick0) / (double)Stopwatch.Frequency;
            var census = CensusSnapshot.Capture(host);
            return new Bench.Sample(elapsedSec, proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0,
                GC.GetTotalMemory(forceFullCollection: false) / 1048576.0,
                Bench.CpuPercent((proc.TotalProcessorTime - cpu0).TotalMilliseconds, elapsedSec, Environment.ProcessorCount),
                Bench.Percentile(frameMs, 0.50), census.ImageCount, census.SceneLive);
        }

        // ── 2.3 --lyrics-advance-probe (ch 22 §9 (a): the env door became a flag; P1's five assertions) ────────────────

        /// <summary>Drives the lyrics media clock SYNCHRONOUSLY (<c>Lyrics.ViewCore.ProbeStep</c>, the stepper silenced by
        /// <c>ProbeSyncMode</c>), so a line advance and the frame that records it are the SAME frame. P1 asserts the handoff
        /// cascade: (a) the offset lands in ONE frame, (b) every line's comp decays monotonically with zero overshoot, (c) the
        /// settle is ≤ 0.55 s of wall time, (d) onset order is top-first, (e) steady-state HotPhaseAllocBytes == 0; plus the
        /// BUG1 check (the lyric DoF never goes absent on a content-advance frame). P3 counts skip-submit defeats at idle.
        /// It refuses <c>--fake</c> and can never be a button: it owns the frame loop.</summary>
        static bool TryLyricsAdvanceProbe(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            if (!s_options.LyricsAdvance) return false;
            if (Platform.Args.Fake) { Say("[lyrics-advance] refusing to run under --fake; launch the real backend, sign in, and play a word-synced track"); return true; }
            if (window is not Win32Window w || device is not D3D12Device gpu) { Say("[lyrics-advance] unavailable: requires Win32Window + D3D12Device"); return true; }

            void FrameLive() { if (w.IsClosed) return; host.RunFrame(); int wt = host.RecommendedWaitMs(); if (wt > 0) w.WaitForWork(Math.Min(wt, 16)); }
            bool WaitFor(string label, int frames, Func<bool> ready)
            {
                for (int i = 0; i < frames && !w.IsClosed; i++) { if (ready()) return true; FrameLive(); }
                Say("[lyrics-advance] timed out waiting for " + label);
                return false;
            }

            Say("[lyrics-advance] waiting for a REAL online session with a current track");
            if (!WaitFor("an online session with a current track", s_options.PlaybackFrames,
                    static () => Spotify.Current.IsOnline && Playback.Snap().HasCurrent)) return true;

            // The probe opens the lyrics rail itself (UI thread, outside a render): the view it measures only mounts there.
            Shell.Ui.Mode.Value = Shell.RailMode.Lyrics;
            Shell.Ui.RailOpen.Value = true;
            NodeHandle vp = default;
            Say("[lyrics-advance] waiting for the lyrics view + viewport + a >=3-line synced document");
            if (!WaitFor("the lyrics view/viewport/document", s_options.LyricsFrames, () =>
            {
                var lv = Lyrics.ViewCore.ProbeActive;
                if (lv is null) return false;
                vp = lv.ProbeViewport;
                return !vp.IsNull && host.Scene.IsLive(vp) && lv.ProbeLineCount >= 3;
            })) return true;

            var view = Lyrics.ViewCore.ProbeActive!;
            int lineCount = view.ProbeLineCount;
            host.ProbeLyricsViewport = vp;
            string track = Playback.Snap().CurrentId.Text;
            Say("[lyrics-advance] track: " + track + "; lines=" + lineCount + "; sync-driving (stepper silenced)");

            var csv = new StringBuilder(1 << 16)
                .AppendLine("phase,line,frame,label,activeLine,lyMode,lyUserScroll,lyContentDirty,lyOff,blurCandidates,blurGroups,blurHold,frameMs,recordMs,submitMs,fenceWaitMs,presentMs,presented,animMs,hotAllocBytes");
            float OffsetY() => host.Scene.TryGetScroll(vp, out var sc) ? sc.OffsetY : 0f;
            void Row(string phase, int line, int frame, string label, in FrameStats s) => csv
                .Append(phase).Append(',').Append(Bench.Int(line)).Append(',').Append(Bench.Int(frame)).Append(',').Append(label).Append(',')
                .Append(Bench.Int(view.ProbeActiveLine)).Append(',').Append(Bench.Int(s.LyricsScrollMode)).Append(',')
                .Append(s.LyricsUserScrollActive ? '1' : '0').Append(',').Append(s.LyricsContentDirtyAtRecord ? '1' : '0').Append(',')
                .Append(Bench.N(OffsetY())).Append(',').Append(Bench.Int(s.BlurCandidateCount)).Append(',').Append(Bench.Int(s.BlurGroupCount)).Append(',')
                .Append(Bench.Int(s.BlurHoldCandidateCount)).Append(',').Append(Bench.N(s.FrameMs)).Append(',').Append(Bench.N(s.RecordMs)).Append(',')
                .Append(Bench.N(s.SubmitMs)).Append(',').Append(Bench.N(s.FenceWaitMs)).Append(',').Append(Bench.N(s.PresentMs)).Append(',')
                .Append(s.Presented ? '1' : '0').Append(',').Append(Bench.N(s.AnimMs)).Append(',').Append(Bench.Int(s.HotPhaseAllocBytes)).AppendLine();

            // Pre-settle onto line 0 so the ONE-TIME first-landing jump fires there, then force snapped so every MEASURED
            // advance takes the steady-state follow path (the latch + cascade, not the first-landing hard jump).
            view.ProbeStep(view.ProbeLineStartMs(0));
            for (int i = 0; i < 10 && !w.IsClosed; i++) { host.RunFrame(); w.WaitForWork(16); }
            view.ProbeForceSnapped();

            const int CascadeFrames = 56, Advances = 20, IdleFrames = 240;
            int startLine = Math.Min(1, lineCount - 1), endLine = Math.Min(lineCount - 1, startLine + Advances);
            int frames = 0, blurAbsent = 0, handoffs = 0, springFrames = 0, lateOffset = 0, signFlips = 0, nonMonotone = 0;
            int orderViolations = 0, allocFrames = 0, settled = 0, unsettled = 0, maxSpread = 0;
            long maxAlloc = 0;
            double maxSettleMs = 0, sumSettleMs = 0;
            var prev = new float[lineCount];
            var sign = new float[lineCount];
            var onset = new int[lineCount];
            float offPrev = OffsetY();

            // ── P1: each handoff FREEZES the clock at lineStart(li) − LeadMs, so `active` resolves to li and STAYS there while
            //    the loop keeps stepping on real wall dt — exactly one handoff per window.
            for (int li = startLine; li <= endLine && !w.IsClosed; li++)
            {
                long freezeMs = view.ProbeLineStartMs(li) - Lyrics.LeadMs;
                view.ProbeStep(freezeMs);
                long armQpc = Stopwatch.GetTimestamp();   // t=0 is the cascade arm INSIDE that step, not the frame around it
                var arm = host.RunFrame();
                w.WaitForWork(16);
                frames++;
                float offArm = OffsetY();
                bool handoff = MathF.Abs(offArm - offPrev) > 0.5f;
                if (handoff) handoffs++;
                if (arm.LyricsScrollMode == (int)FluentGpu.Scroll.ScrollActivity.Driven) springFrames++;
                if (!arm.LyricsUserScrollActive && arm.LyricsContentDirtyAtRecord && arm.BlurCandidateCount == 0) blurAbsent++;
                Row("P1-cascade", li, 0, handoff ? "latch" : "no-move", in arm);
                for (int i = 0; i < lineCount; i++)
                {
                    float c = view.ProbeCascadeComp(i);
                    prev[i] = c; sign[i] = c > 0f ? 1f : c < 0f ? -1f : 0f; onset[i] = -1;
                }

                double settleMs = -1;
                for (int f = 1; f < CascadeFrames && !w.IsClosed; f++)
                {
                    view.ProbeStep(freezeMs);
                    if (settleMs < 0 && !view.ProbeCascadeActive) settleMs = (Stopwatch.GetTimestamp() - armQpc) * 1000.0 / Stopwatch.Frequency;
                    var s = host.RunFrame();
                    w.WaitForWork(16);
                    frames++;
                    if (MathF.Abs(OffsetY() - offArm) > 0.5f) lateOffset++;                                   // (a)
                    if (s.LyricsScrollMode == (int)FluentGpu.Scroll.ScrollActivity.Driven) springFrames++;
                    for (int i = 0; i < lineCount; i++)
                    {
                        float c = view.ProbeCascadeComp(i), p = prev[i];
                        if (sign[i] != 0f)
                        {
                            if (c * sign[i] < -0.01f) signFlips++;                                               // (b)
                            if (MathF.Abs(c) > MathF.Abs(p) + 0.05f) nonMonotone++;
                        }
                        if (onset[i] < 0 && MathF.Abs(c - p) > 0.02f) onset[i] = f;                             // (d)
                        prev[i] = c;
                    }
                    if (settleMs >= 0 && f >= CascadeFrames - 8)                                                   // (e)
                    {
                        if (s.HotPhaseAllocBytes != 0) allocFrames++;
                        if (s.HotPhaseAllocBytes > maxAlloc) maxAlloc = s.HotPhaseAllocBytes;
                    }
                    if (!s.LyricsUserScrollActive && s.LyricsContentDirtyAtRecord && s.BlurCandidateCount == 0) blurAbsent++;
                    Row("P1-cascade", li, f, settleMs >= 0 ? "settled" : "", in s);
                }

                if (handoff)
                {
                    if (settleMs < 0) unsettled++;
                    else { settled++; sumSettleMs += settleMs; if (settleMs > maxSettleMs) maxSettleMs = settleMs; }
                    // (d) rank 0 is the OUTGOING line (li-1); each rank below must start no EARLIER than the rank above.
                    int prevOnset = -1;
                    for (int k = 0; k <= 6; k++)
                    {
                        int idx = li - 1 + k;
                        if (idx < 0 || idx >= lineCount || onset[idx] < 0) continue;
                        if (prevOnset >= 0 && onset[idx] < prevOnset) orderViolations++;
                        if (prevOnset >= 0 && onset[idx] - prevOnset > maxSpread) maxSpread = onset[idx] - prevOnset;
                        prevOnset = Math.Max(prevOnset, onset[idx]);
                    }
                }
                offPrev = offArm;
            }

            // ── P3: stationary, no input — frames force-PRESENTED despite a static scene (a loop defeating skip-submit).
            int idle = 0, presented = 0, staticPresented = 0;
            for (int f = 0; f < IdleFrames && !w.IsClosed; f++)
            {
                var s = host.RunFrame();
                w.WaitForWork(16);
                idle++;
                if (s.Presented) presented++;
                if (s.Presented && s.LyricsScrollMode == 0 && !s.LyricsContentDirtyAtRecord && s.MainScrollMode == 0 && !s.MainContentDirtyAtRecord) staticPresented++;
                Row("P3-idle", -1, f, "", in s);
            }

            bool oneFrame = lateOffset == 0 && springFrames == 0, noOvershoot = signFlips == 0 && nonMonotone == 0;
            bool settleOk = unsettled == 0 && maxSettleMs <= 550.0, topFirst = orderViolations == 0, allocClean = allocFrames == 0;
            string V(bool ok) => ok ? "PASS" : "FAIL";
            var sb = new StringBuilder(2048).AppendLine()
                .AppendLine("=== WAVEE LYRICS ADVANCE PROBE — synchronous, timer-decoupling-free ===")
                .AppendLine("track: " + track + "; lines=" + lineCount + "; advances=" + Math.Max(0, endLine - startLine + 1) + "; frames/handoff=" + CascadeFrames)
                .AppendLine("P1 handoff cascade (instant viewport latch + staggered per-line compensating springs):")
                .AppendLine("  frames=" + frames + "; handoffs measured=" + handoffs)
                .AppendLine("  (a) one-frame land : late offset moves=" + lateOffset + " (expect 0); programmatic-chase frames=" + springFrames + " (expect 0) >>> " + V(oneFrame))
                .AppendLine("  (b) zero overshoot : sign flips=" + signFlips + "; non-monotone growths=" + nonMonotone + " (expect 0 / 0) >>> " + V(noOvershoot))
                .AppendLine("  (c) settle         : settled=" + settled + "; never-settled=" + unsettled + "; mean=" + Bench.N(settled > 0 ? sumSettleMs / settled : 0)
                    + " ms; max=" + Bench.N(maxSettleMs) + " ms (budget 550) >>> " + V(settleOk))
                .AppendLine("  (d) top-first      : onset-order violations=" + orderViolations + " (expect 0); max adjacent onset spread=" + maxSpread + " frames >>> " + V(topFirst))
                .AppendLine("  (e) steady alloc   : post-settle frames with HotPhaseAllocBytes != 0=" + allocFrames + "; max=" + maxAlloc + " B >>> " + V(allocClean))
                .AppendLine("  >>> CASCADE " + (oneFrame && noOvershoot && settleOk && topFirst && allocClean ? "GREEN" : "RED"))
                .AppendLine("  DoF-absent frames (contentDirty & !userScroll & zero blur candidates)=" + blurAbsent + " >>> BUG1 " + (blurAbsent == 0 ? "FIXED" : "PRESENT"))
                .AppendLine("P3 skip-submit at idle: frames=" + idle + "; presented=" + presented + "; STATIC-scene-but-presented=" + staticPresented)
                .AppendLine("Not measured in 0.3 (no seam yet): P2 main-scroll sibling isolation (no main-viewport probe hook), "
                    + "P4 voice-transition remount + wipe/glow integrity (no ProbeLineNode/ProbeGlowNode/LastFrameDiagnostics on Lyrics.ViewCore).");
            string report = sb.ToString();
            string dir = OutDir();
            WriteArtifact(dir, "wavee-lyrics-advance-probe.csv", csv.ToString());
            WriteArtifact(dir, "wavee-lyrics-advance-probe-summary.txt", report);
            Say(report);
            return true;
        }
    }

    // ══ 3. THE NOTIFICATION SIMULATOR (Settings ▸ Notifications ▸ Send event — ch 27 W14, G-218) ══════════════════════

    /// <summary>Drives ONE synthetic event down the SAME path a real one takes. Nothing here calls
    /// <c>ToastNotifier.Show</c>: a shortcut past the pipeline would prove only that Windows can paint a banner, which is
    /// not the question being asked. UI thread (it reads and republishes the centre, and parses uris).</summary>
    public static class NotificationSimulator
    {
        /// <summary>Lead time for the scheduled topics: past both notifiers' minimums (1 and 2 minutes) and past the real
        /// constraint — Windows ACCEPTS a near-immediate schedule and then silently never paints it.</summary>
        public static readonly TimeSpan ScheduleLead = TimeSpan.FromMinutes(3);

        static long s_seq;

        /// <summary>Simulate <paramref name="topic"/> at whatever it is dialled to. A topic that would not be delivered is
        /// REPORTED, never forced; the scheduled topics are never touched at the OS level below Windows (their notifiers
        /// revoke on a disallowed policy, which would destroy a genuinely pending real toast).</summary>
        public static SimResult Send(NotifyTopic topic)
        {
            var level = Notify.Prefs.Level(topic);
            var policy = Notify.Prefs.Policy();
            long seq = Interlocked.Increment(ref s_seq);
            switch (SimRules.PathFor(topic, level))
            {
                case SimPath.Dropped:
                    return new SimResult(SimOutcome.Dropped, null);
                case SimPath.Activity:
                    // 0.2.9 logged a real activity entry. 0.3 has no activity journal yet (ch 19 DATA GAP 3), so nothing is
                    // written — the verdict is still the topic's truth: it never banners.
                    Log.Info("notify", "simulated library activity: no activity journal exists yet, nothing was recorded");
                    return new SimResult(SimOutcome.NeverBanners, null);
                case SimPath.Scheduled:
                    return SendScheduled(topic, level, in policy, seq);
                default:
                    return SendLive(topic, level, in policy, seq);
            }
        }

        /// <summary>Live topics. The centre has ONE injection point — <c>Notify.Rebuild(update, social, releases,
        /// activity)</c> — so the current feed is split back into its four sources, the synthetic row joins its own, and
        /// the rebuild runs the real merge, topic filter and escalator. The verdict is the escalation plan for that exact
        /// feed, decided BEFORE the rebuild raises anything. (A topic dialled Off since the last feed push is absent from
        /// the merged feed and stays absent until the next real push — the merged rows are the only sources readable here.)</summary>
        static SimResult SendLive(NotifyTopic topic, NotifyLevel level, in NotificationPolicy policy, long seq)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long gander = Platform.Settings.Get(Platform.Keys.NotificationsGanderLastSeenMs);
            long whatsNew = Platform.Settings.Get(Platform.Keys.NotificationsWhatsNewLastSeenMs);
            long ts = SimRules.NextTimestamp(nowMs, gander, whatsNew);
            if (SimRules.PrimedWatermark(Platform.Settings.Get(Platform.Keys.NotifyLastToastedMs), ts) is { } primed)
                Platform.Settings.Set(Platform.Keys.NotifyLastToastedMs, primed);

            Notification row = RowFor(topic, ts, seq);
            Notification? update = row.Category == NotifyCategory.AppUpdate ? row : null;
            var social = new List<Notification>();
            var releases = new List<Notification>();
            var activity = new List<Notification>();
            var current = Notify.Items.Peek().Items;
            for (int i = 0; i < current.Count; i++)
            {
                var n = current[i];
                switch (n.Category)
                {
                    case NotifyCategory.AppUpdate: update ??= n; break;
                    case NotifyCategory.Social: social.Add(n); break;
                    case NotifyCategory.NewRelease: releases.Add(n); break;
                    default: activity.Add(n); break;
                }
            }
            if (row.Category == NotifyCategory.Social) social.Add(row);
            else if (row.Category == NotifyCategory.NewRelease) releases.Add(row);

            var feed = Notify.Merge(update, social, gander, releases, whatsNew, activity,
                Platform.Settings.Get(Platform.Keys.NotificationsReadIds), static t => Notify.Prefs.Level(t) != NotifyLevel.Off);
            var plan = Notify.Escalation.Decide(feed.Items, Platform.Settings.Get(Platform.Keys.NotifyLastToastedMs), in policy,
                static t => Notify.Prefs.Level(t), DateTimeOffset.Now, "");
            bool raises = ToastNotifier.IsSupported && SimRules.PlanRaises(plan.Raise, feed.Items, row.Id);

            Notify.Rebuild(update, social, releases, activity);
            return SimRules.LiveOutcome(raises, in policy, level, DateTimeOffset.Now);
        }

        /// <summary>The synthetic row. Ids are unique per press (a reused id makes Windows REPLACE the previous banner); the
        /// topic is DERIVED from content (a concert needs its wire type, an episode its kind); the update row is a
        /// COMPLETED observation with a per-press quad — `Available` raises no OS toast by design in 0.3, and a repeated
        /// identity would be swallowed by the escalator's already-raised guard.</summary>
        static Notification RowFor(NotifyTopic topic, long ts, long seq)
        {
            string n = seq.ToString(CultureInfo.InvariantCulture);
            return topic switch
            {
                NotifyTopic.NewAlbums => NotifyRows.ForRelease("sim:album:" + n, ts, true, NewReleaseKind.Album,
                    EntityUri.Parse("spotify:album:simulated"), "A simulated release", null, "A followed artist", "album", false),
                NotifyTopic.NewEpisodes => NotifyRows.ForRelease("sim:episode:" + n, ts, true, NewReleaseKind.Episode,
                    EntityUri.Parse("spotify:episode:simulated"), "A simulated episode", null, "A followed show", null, false),
                NotifyTopic.Concerts => NotifyRows.ForSocial("sim:concert:" + n, ts, true,
                    "New A followed artist show just announced near you. Save the date!", null, SocialActionType.Navigate,
                    null, "A followed artist", "CONCERT_ANNOUNCEMENT"),
                NotifyTopic.Followers => NotifyRows.ForSocial("sim:follower:" + n, ts, true,
                    "A simulated listener started following you", null, SocialActionType.Navigate, null, "A simulated listener", null),
                _ => NotifyRows.ForUpdate(new AppUpdateSnapshot(AppUpdateState.Completed, "99.9.9." + n, "99.9.9", "Simulated", 100, null,
                    AutoUpdateAssociated: true, LastCheckedMs: ts), isUnread: true),
            };
        }

        /// <summary>The two OS-scheduled topics, through their REAL notifiers. Below Windows nothing is scheduled.</summary>
        static SimResult SendScheduled(NotifyTopic topic, NotifyLevel level, in NotificationPolicy policy, long seq)
        {
            if (SimRules.ScheduledPrecheck(in policy, level, ToastNotifier.IsSupported) is { } early) return early;
            var due = DateTimeOffset.Now.Add(ScheduleLead);
            DateTimeOffset? at;
            if (topic == NotifyTopic.DaylistRefresh)
                at = Notify.Daylist.SimulateSchedule(due, default, "a simulated daylist");
            else
            {
                var link = new Notify.DropLink(
                    PreRelease: EntityUri.Parse("spotify:prerelease:simulated:" + seq.ToString(CultureInfo.InvariantCulture)),
                    Play: EntityUri.Parse("spotify:album:simulated"),
                    Name: "A simulated release", Artist: "A followed artist", CoverUrl: null, ReleaseAt: due, IsUpcoming: true);
                at = Notify.ReleaseDrops.SimulateSchedule(in link, due);
            }
            return SimRules.ScheduledOutcome(at);
        }
    }

    // ══ 4. THE PURE HALF — the arms' decisions (ProbeArmsTests) ══════════════════════════════════════════════════════
    // ── the probe arms' argv (a pure parse — 0.2.9's EnvInt knobs as flags; out-of-range or garbage → the default) ──
    public readonly record struct ProbeOptions(bool PerfBench, bool StartupBench, string CrashProbe, bool LyricsAdvance,
        string ProbeOut, int PlaybackFrames, int LyricsFrames, int IdleSec, int NavHops, int OpenHops)
    {
        /// <summary>A GUI arm that wants the parent console (every one of them is a CLI-launched GUI run).</summary>
        public bool WantsConsole => PerfBench || StartupBench || LyricsAdvance || CrashProbe.Length > 0;

        public static ProbeOptions Parse(string[] args)
        {
            int crash = Array.IndexOf(args, "--crash-probe");
            string mode = crash < 0 ? "" : crash + 1 < args.Length && args[crash + 1] == "failfast" ? "failfast" : "throw";
            int outAt = Array.IndexOf(args, "--probe-out");
            string outDir = outAt >= 0 && outAt + 1 < args.Length && !args[outAt + 1].StartsWith("--", StringComparison.Ordinal) ? args[outAt + 1] : "";
            return new ProbeOptions(Array.IndexOf(args, "--perf-bench") >= 0, Array.IndexOf(args, "--startup-bench") >= 0, mode,
                Array.IndexOf(args, "--lyrics-advance-probe") >= 0, outDir,
                Int(args, "--probe-playback-frames", 5400, 120, 36000), Int(args, "--probe-lyrics-frames", 3600, 60, 36000),
                Int(args, "--bench-idle-sec", 10, 3, 120), Int(args, "--bench-nav-hops", 12, 4, 60), Int(args, "--bench-open-hops", 8, 2, 40));
        }

        static int Int(string[] args, string flag, int fallback, int lo, int hi)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)
                && v >= lo && v <= hi ? v : fallback;
        }
    }

    // ── the notification simulator's decisions (Settings ▸ Notifications ▸ Send event, ch 27 W14, G-218) ──
    /// <summary>What a simulated event actually did — reported verbatim, because the value of "Send event" is learning which
    /// stage of the pipeline consumed it.</summary>
    public enum SimOutcome : byte { Dropped, RecordedInApp, Banner, BannerQuietDeferred, Scheduled, NeverBanners, Unavailable }

    /// <summary><paramref name="At"/> is the delivery clock for <see cref="SimOutcome.Scheduled"/> and the quiet-hours end for
    /// <see cref="SimOutcome.BannerQuietDeferred"/>; null otherwise.</summary>
    public readonly record struct SimResult(SimOutcome Outcome, DateTimeOffset? At);

    /// <summary>Which pipeline a simulated topic walks.</summary>
    public enum SimPath : byte { Dropped, Live, Scheduled, Activity }

    public static class SimRules
    {
        /// <summary>Off is checked FIRST, for every topic: a dialled-off event never reaches the centre.</summary>
        public static SimPath PathFor(NotifyTopic topic, NotifyLevel level)
            => level == NotifyLevel.Off ? SimPath.Dropped
             : NotificationPolicy.IsScheduled(topic) ? SimPath.Scheduled
             : topic == NotifyTopic.LibraryActivity ? SimPath.Activity
             : SimPath.Live;

        /// <summary>A timestamp that reads as unread: later than now AND than both feed watermarks (the merge's unread gate
        /// is STRICTLY greater, and opening the panel stamps both to now).</summary>
        public static long NextTimestamp(long nowUnixMs, long ganderSeenMs, long whatsNewSeenMs)
        {
            long floor = Math.Max(ganderSeenMs, whatsNewSeenMs);
            return floor >= nowUnixMs ? floor + 1 : nowUnixMs;
        }

        /// <summary>The escalator raises nothing while its watermark is 0 (enabling notifications never replays history). A
        /// simulated event is NOW, not history: prime to <c>ts − 1</c>, or null when it is already primed.</summary>
        public static long? PrimedWatermark(long watermark, long ts) => watermark <= 0 ? ts - 1 : null;

        /// <summary>Did the escalation plan pick the row with <paramref name="id"/>?</summary>
        public static bool PlanRaises(ReadOnlySpan<int> raise, IReadOnlyList<Notification> items, string id)
        {
            for (int i = 0; i < raise.Length; i++)
                if ((uint)raise[i] < (uint)items.Count && string.Equals(items[raise[i]].Id, id, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>A live topic's verdict. "Windows, but quiet hours" is named separately from "in-app as dialled": the same
        /// visible result, and the second is the confusing one.</summary>
        public static SimResult LiveOutcome(bool raised, in NotificationPolicy policy, NotifyLevel level, DateTimeOffset nowLocal)
        {
            if (raised) return new SimResult(SimOutcome.Banner, null);
            if (policy.WindowsEnabled && level == NotifyLevel.Windows && policy.Quiet.Contains(nowLocal))
                return new SimResult(SimOutcome.BannerQuietDeferred, policy.Quiet.NextAudible(nowLocal));
            return new SimResult(SimOutcome.RecordedInApp, null);
        }

        /// <summary>Below Windows there is nothing to schedule — and calling the notifiers would REVOKE a real pending toast —
        /// so the verdict is decided BEFORE the OS is touched. Null = go and schedule.</summary>
        public static SimResult? ScheduledPrecheck(in NotificationPolicy policy, NotifyLevel level, bool toastPlatform)
            => !policy.WindowsEnabled || level != NotifyLevel.Windows ? new SimResult(SimOutcome.RecordedInApp, null)
             : !toastPlatform ? new SimResult(SimOutcome.Unavailable, null)
             : (SimResult?)null;

        public static SimResult ScheduledOutcome(DateTimeOffset? at)
            => at is { } due ? new SimResult(SimOutcome.Scheduled, due) : new SimResult(SimOutcome.Unavailable, null);
    }

    // ── the perf / startup bench math (ops/build/bench-wavee.ps1 reads wavee-perf-latest.json — the names are the wire) ──
    public static class Bench
    {
        public readonly record struct Sample(double ElapsedSec, double WorkingSetMB, double PrivateMB, double ManagedMB,
            double CpuPct, double FrameMsP50, int Images, int SceneLive);

        public readonly record struct Scenario(string Name, int Frames, double DurationSec, double CpuAvgPct, double CpuPeakPct,
            double WorkingSetAvgMB, double WorkingSetPeakMB, double PrivatePeakMB, double ManagedPeakMB,
            double FrameMsP50, double FrameMsP90, double FrameMsMax, int ImagesPeak, int SceneLivePeak);

        /// <summary>Nearest-rank over a sorted COPY; 0 for no values.</summary>
        public static double Percentile(IReadOnlyList<double> values, double p)
        {
            if (values.Count == 0) return 0;
            var a = new double[values.Count];
            for (int i = 0; i < a.Length; i++) a[i] = values[i];
            Array.Sort(a);
            return a[(int)Math.Clamp(Math.Round(p * (a.Length - 1)), 0, a.Length - 1)];
        }

        /// <summary>Task-Manager CPU%: process CPU time over wall time × processors; 0 under a millisecond of wall.</summary>
        public static double CpuPercent(double cpuMs, double elapsedSec, int processors)
            => elapsedSec > 0.001 && processors > 0 ? cpuMs / (elapsedSec * 1000.0 * processors) * 100.0 : 0;

        public static Scenario Aggregate(string name, int frames, double durationSec, IReadOnlyList<Sample> samples, IReadOnlyList<double> frameMs)
        {
            double cpuAvg = 0, cpuPeak = 0, wsAvg = 0, wsPeak = 0, privPeak = 0, managedPeak = 0, max = 0;
            int imgPeak = 0, scenePeak = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                cpuAvg += s.CpuPct; wsAvg += s.WorkingSetMB;
                cpuPeak = Math.Max(cpuPeak, s.CpuPct); wsPeak = Math.Max(wsPeak, s.WorkingSetMB);
                privPeak = Math.Max(privPeak, s.PrivateMB); managedPeak = Math.Max(managedPeak, s.ManagedMB);
                imgPeak = Math.Max(imgPeak, s.Images); scenePeak = Math.Max(scenePeak, s.SceneLive);
            }
            if (samples.Count > 0) { cpuAvg /= samples.Count; wsAvg /= samples.Count; }
            for (int i = 0; i < frameMs.Count; i++) max = Math.Max(max, frameMs[i]);
            return new Scenario(name, frames, durationSec, cpuAvg, cpuPeak, wsAvg, wsPeak, privPeak, managedPeak,
                Percentile(frameMs, 0.50), Percentile(frameMs, 0.90), max, imgPeak, scenePeak);
        }

        /// <summary>wavee-perf-latest.json — hand-built, invariant culture, 0.2.9's property names.</summary>
        public static string PerfJson(string version, int processors, IReadOnlyList<Scenario> results)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"version\":\"").Append(Escape(version)).Append("\",\"processors\":").Append(Int(processors)).Append(",\"scenarios\":[");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(Escape(r.Name)).Append("\",\"frames\":").Append(Int(r.Frames))
                  .Append(",\"durationSec\":").Append(N(r.DurationSec)).Append(",\"cpuAvgPct\":").Append(N(r.CpuAvgPct))
                  .Append(",\"cpuPeakPct\":").Append(N(r.CpuPeakPct)).Append(",\"workingSetAvgMB\":").Append(N(r.WorkingSetAvgMB))
                  .Append(",\"workingSetPeakMB\":").Append(N(r.WorkingSetPeakMB)).Append(",\"privatePeakMB\":").Append(N(r.PrivatePeakMB))
                  .Append(",\"managedPeakMB\":").Append(N(r.ManagedPeakMB)).Append(",\"frameMsP50\":").Append(N(r.FrameMsP50))
                  .Append(",\"frameMsP90\":").Append(N(r.FrameMsP90)).Append(",\"frameMsMax\":").Append(N(r.FrameMsMax))
                  .Append(",\"imagesPeak\":").Append(Int(r.ImagesPeak)).Append(",\"sceneLivePeak\":").Append(Int(r.SceneLivePeak)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static string N(double v) => double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "0";
        public static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);
        public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ── --qr-dump's PNG writer (grayscale, 1 filter byte per scanline, zlib IDAT, CRC-32 per chunk) ──
    public static class QrPng
    {
        /// <summary>The matrix (<c>[x, y]</c>, true = dark) at <paramref name="scale"/> px per module inside a
        /// <paramref name="quiet"/>-module quiet zone, as a whole PNG file.</summary>
        public static byte[] Encode(bool[,] m, int quiet = 4, int scale = 14)
        {
            int n = m.GetLength(0), size = (n + quiet * 2) * scale;
            var raw = new byte[(size + 1) * size];
            int o = 0;
            for (int y = 0; y < size; y++)
            {
                raw[o++] = 0;
                int my = y / scale - quiet;
                for (int x = 0; x < size; x++) { int mx = x / scale - quiet; raw[o++] = (uint)mx < (uint)n && (uint)my < (uint)n && m[mx, my] ? (byte)0 : (byte)255; }
            }
            byte[] comp;
            using (var ms = new MemoryStream())
            {
                using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
                comp = ms.ToArray();
            }
            using var file = new MemoryStream(comp.Length + 64);
            file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            Span<byte> ihdr = stackalloc byte[13];
            BigEndian(ihdr, 0, size); BigEndian(ihdr, 4, size); ihdr[8] = 8; // 8-bit, colour type 0 (grayscale), rest 0
            Chunk(file, "IHDR"u8, ihdr); Chunk(file, "IDAT"u8, comp); Chunk(file, "IEND"u8, default);
            return file.ToArray();
        }

        public static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }

        static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> be = stackalloc byte[4];
            BigEndian(be, 0, data.Length); s.Write(be);
            s.Write(type); s.Write(data);
            BigEndian(be, 0, (int)Crc32(type, data)); s.Write(be);
        }

        static void BigEndian(Span<byte> b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static readonly uint[] CrcTable = BuildCrc();
        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
    }
}
