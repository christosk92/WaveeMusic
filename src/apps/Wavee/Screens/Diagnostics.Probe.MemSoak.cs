// ── Screens/Diagnostics.Probe.MemSoak.cs ──────────────────────────────────────────────────────────────────────────
// the `--mem-soak` GUI arm: what a long browsing session leaves on the managed heap, and how much of it is garbage.
//
// Role: SHELL
// Owner: S
//
// Run beside a live Wavee with a scratch profile (its own single instance, no real account):
//   Wavee.exe --fake --profile <scratch dir> --mem-soak [--mem-soak-rounds N] [--mem-soak-idle-sec S] [--mem-soak-minimize]
//             [--mem-soak-loh-mb N] [--probe-out <dir>]
//   Wavee.exe --profile <copy of a signed-in profile> --mem-soak [--mem-soak-play] ...   (the profile's REAL pages)
//
// `--mem-soak-play` (real profile only) plays the profile's most recent track before the tour and keeps it playing through
// it: a 60 s phase with nothing but playback separates what playback holds (audio pipeline, lyrics, now-playing art) from
// what the pages hold. It plays on the profile's account, so that account's other devices stop.
//
// Each round visits every `--fake` page a session reaches (the library lists, every album, artist and playlist, Home),
// pumping frames after each hop so the page mounts, binds and fetches. After the rounds it idles at the production
// frame pace for S seconds — long enough for the memory governor's 30 s poll to fire — and then opens a DUMP WINDOW:
// it writes `mem-soak-dump.ready` (the pid) to the probe-out directory and keeps idling until `mem-soak-dump.done`
// appears (or 120 s pass), so an outside `dotnet-gcdump collect -p <pid>` attributes the live heap by type. Last it
// runs one blocking compacting collection, so the report separates the LIVE heap from the dead one that was merely
// waiting for a gen2.
//
// One `[mem-soak]` line per phase: process private bytes and working set, managed heap size and committed, per
// generation the size and fragmentation after the last GC, the GC counts, the entity graph's live rows and interned
// strings, and the scene's live/capacity. Plain text to stderr + `mem-soak-latest.txt` under the probe-out directory.

using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using FluentGpu;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static bool TryMemSoak(AppHost host, IPlatformWindow window, IGpuDevice device)
        {
            if (!s_options.MemSoak) return false;
            ApplyInlineArg();
            // Without --fake the soak tours the profile's REAL pages (its recent albums, playlists and artists): only on a
            // --profile copy, never the default profile, and it never plays anything.
            bool real = !Platform.Args.Fake;
            if (real && Platform.ProfileRoot.Length == 0) { Say("[mem-soak] without --fake it needs --profile <a copy of a signed-in profile>"); return true; }
            if (window is not Win32Window w || device is not D3D12Device gpu) { Say("[mem-soak] unavailable: requires Win32Window + D3D12Device"); return true; }
            if (!WarmUntilShell(host, w, gpu, 600)) { Say("[mem-soak] the shell never activated a route"); return true; }
            if (real && !PumpUntil(host, w, static () => Spotify.Current.IsOnline, 60)) { Say("[mem-soak] the session never came online (is the profile signed in?)"); return true; }

            var report = new StringBuilder(4096).AppendLine()
                .AppendLine("=== WAVEE MEM SOAK ===")
                .AppendLine("version=" + VersionLabel() + "  rounds=" + s_options.MemSoakRounds + "  idleSec=" + s_options.MemSoakIdleSec);
            string tileFile = Path.Combine(OutDir(), "tile-census.txt");
            try { Directory.CreateDirectory(OutDir()); File.WriteAllText(tileFile, ""); } catch (IOException) { }
            void Phase(string name)
            {
                string line = "[mem-soak] " + name + " " + MemLine(host);
                report.AppendLine(line);
                Say(line);
                MemorySampler.Sample("mem-soak " + name);   // the full census line (engine, snapshots, GPU) in the profile's log
                // Where the retained tiles are and how much of them is painted, on the page this phase stands on.
                string? tiles = null;
                if (FluentApp.RequestTileCensus(r => Volatile.Write(ref tiles, r)))
                {
                    PumpUntil(host, w, () => Volatile.Read(ref tiles) is not null, 3);
                    string images = FluentApp.EngineImages?.MemoryCensus() ?? "";   // UI thread, like the cache
                    // `--d3d12-debug`: the runtime's live object counts on this page (pipelines, allocators, heaps), first line only
                    if (Array.IndexOf(Platform.Args.Argv, "--d3d12-debug") >= 0)
                    {
                        string? live = null;
                        if (FluentApp.RequestGpuLiveCensus(r => Volatile.Write(ref live, r)))
                        {
                            PumpUntil(host, w, () => Volatile.Read(ref live) is not null, 5);
                            if (Volatile.Read(ref live) is { } lv) images += lv.Split((char)10)[0] + Environment.NewLine;
                        }
                    }
                    try { File.AppendAllText(tileFile, "== " + name + Environment.NewLine + (Volatile.Read(ref tiles) ?? "(no census within 3 s)" + Environment.NewLine) + images); } catch (IOException) { }
                }
            }

            PumpPaced(host, w, real ? 5.0 : 0.5);
            Phase("start");
            bool play = real && s_options.MemSoakPlay;
            if (play)
            {
                var targets = RealTargets(s_frameBench);
                string? track = targets.Tracks.Length > 0 ? targets.Tracks[0] : null;
                if (track is null) Say("[mem-soak] --mem-soak-play: the profile has no recent track");
                else
                {
                    Playback.PlayContext(track);
                    bool started = PumpUntil(host, w, static () => Playback.Snap().IsPlaying, 30);
                    Say("[mem-soak] playing " + track + (started ? "" : " (did not start within 30 s)"));
                    PumpPaced(host, w, 5.0);
                    Phase("play-5s");
                    PumpPaced(host, w, 55.0);
                    Phase("play-60s");
                }
            }
            var routes = real ? RealSoakRoutes() : SoakRoutes();
            Say("[mem-soak] " + (real ? "real" : "fake") + " routes=" + routes.Count);
            int hops = 0;
            for (int r = 0; r < s_options.MemSoakRounds && !w.IsClosed; r++)
            {
                foreach (var route in routes)
                {
                    if (w.IsClosed) break;
                    long nav0 = NavigationFrameWatch.NavigationId;
                    Nav(route);
                    if (real)
                    {
                        // A real page waits on the network: let it land, then scroll it the way a person reads it (down
                        // a few viewports and back), so its images decode and its virtualized rows realize.
                        PumpUntil(host, w, () => NavigationFrameWatch.NavigationId != nav0, 10);
                        PumpPaced(host, w, 2.0);
                        var scroll = new ScrollState(depthViewports: 6);
                        if (scroll.WaitReady(host, w, 3))
                        {
                            // Scroll in 1 s slices with a tile census after each: a transient (the scroll band at speed,
                            // effect surfaces re-sized) never shows in the census taken once the page settles, and on a
                            // driver that keeps freed GPU memory the transient is what the process keeps.
                            var drive = scroll.Driver(host);
                            string? peakText = null; double peakMiB = -1;
                            for (int sl = 0; sl < 5 && !w.IsClosed; sl++)
                            {
                                Pump(host, w, 1.0, drive);
                                string? t = null;
                                if (!FluentApp.RequestTileCensus(r => Volatile.Write(ref t, r))) continue;
                                PumpUntil(host, w, () => Volatile.Read(ref t) is not null, 2);
                                if (Volatile.Read(ref t) is not { } sample) continue;
                                var m = System.Text.RegularExpressions.Regex.Match(sample, @"resident=\d+ tiles ([\d.]+) MiB");
                                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mib) && mib > peakMiB)
                                { peakMiB = mib; peakText = sample; }
                            }
                            if (peakText is not null)
                                try { File.AppendAllText(Path.Combine(OutDir(), "tile-peaks.txt"), "== hop=" + (hops + 1).ToString(CultureInfo.InvariantCulture) + " " + route.Kind + " scroll peak" + Environment.NewLine + peakText); } catch (IOException) { }
                        }
                        PumpPaced(host, w, 0.5);
                    }
                    // Enough frames for the page to mount, its rows to bind and the fake provider's answers to land.
                    else for (int f = 0; f < 24 && !w.IsClosed; f++) FrameFast(host, w, gpu);
                    hops++;
                    if (real) Phase("hop=" + hops.ToString(CultureInfo.InvariantCulture) + " " + route.Kind + (route.Subject.IsValid ? " " + route.Subject.Text : ""));
                }
                Phase("round=" + (r + 1).ToString(CultureInfo.InvariantCulture) + " hops=" + hops.ToString(CultureInfo.InvariantCulture));
            }
            Nav(new Shell.Route(Shell.RouteKind.Home));
            PumpPaced(host, w, 2.0);
            if (play)
            {
                Phase("tour-end playing");
                Playback.Stop();
                PumpPaced(host, w, 3.0);
                Phase("stopped");
            }
            // `--mem-soak-loh-mb N`: what a real session leaves that `--fake` cannot — N MB of dead large objects with a
            // survivor every eighth megabyte holding the holes open (the shape AI-lyrics jobs and large answers left behind).
            var survivors = new List<byte[]>();
            for (int mb = 0; mb < s_options.MemSoakLohMb; mb++)
            {
                var block = new byte[1 << 20];
                block[0] = 1;
                if (mb % 8 == 0) survivors.Add(block);
            }
            Phase("after-soak");

            // The governor's background request, under this process's real GC settings (ConserveMemory, SustainedLowLatency):
            // does the runtime run it concurrently, or as a blocking collection? A blocking one must never run while audible.
            bool ranBlocking = Residency.RequestBackgroundGen2(out double requestPauseMs);
            PumpPaced(host, w, 3.0);   // let the background collection finish beside the frames
            Phase("background-request blocking=" + ranBlocking + " pauseMs=" + requestPauseMs.ToString("0.0", CultureInfo.InvariantCulture)
                + " lastBlockingGc=" + GC.GetGCMemoryInfo(GCKind.FullBlocking).Index + " lastBackgroundGc=" + GC.GetGCMemoryInfo(GCKind.Background).Index);

            // `--mem-soak-minimize`: idle the way an owner leaves the app — minimized — which is when the governor may
            // compact (Residency.Heap.cs). Restored before the dump so the dump window behaves like the plain run.
            if (s_options.MemSoakMinimize) ShowWindow(w.Handle.Value, 6 /*SW_MINIMIZE*/);
            PumpPaced(host, w, s_options.MemSoakIdleSec);
            Phase(s_options.MemSoakMinimize ? "after-idle minimized" : "after-idle");
            if (s_options.MemSoakMinimize) ShowWindow(w.Handle.Value, 9 /*SW_RESTORE*/);

            string dir = OutDir();
            // `--d3d12-debug`: every live D3D12 resource and heap, tracked / leaked / untracked, with exact sizes.
            string? census = null;
            if (Array.IndexOf(Platform.Args.Argv, "--d3d12-debug") >= 0 && FluentApp.RequestGpuLiveCensus(r => Volatile.Write(ref census, r)))
            {
                PumpUntil(host, w, () => Volatile.Read(ref census) is not null, 15);
                if (Volatile.Read(ref census) is { } gpuReport) { WriteArtifact(dir, "gpu-census.txt", gpuReport); Say(gpuReport); }
            }
            string ready = Path.Combine(dir, "mem-soak-dump.ready"), done = Path.Combine(dir, "mem-soak-dump.done");
            try { if (File.Exists(done)) File.Delete(done); } catch (IOException) { }
            WriteArtifact(dir, "mem-soak-dump.ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Say("[mem-soak] dump window open: pid=" + Environment.ProcessId + " (waiting for " + done + ")");
            var sw = Stopwatch.StartNew();
            while (!w.IsClosed && !File.Exists(done) && sw.Elapsed.TotalSeconds < 120) PumpPaced(host, w, 0.5);
            try { File.Delete(ready); File.Delete(done); } catch (IOException) { }
            Phase("after-dump");

            TimeSpan pause0 = GC.GetTotalPauseDuration();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            double pauseMs = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
            GC.WaitForPendingFinalizers();
            PumpPaced(host, w, 1.0);
            Phase("after-full-gc pauseMs=" + pauseMs.ToString("0.0", CultureInfo.InvariantCulture));

            GC.KeepAlive(survivors);
            string text = report.ToString();
            WriteArtifact(dir, "mem-soak-latest.txt", text);
            Say(text);
            return true;
        }

        /// <summary>Every page a `--fake` session reaches: the library lists, then each album, artist and playlist the
        /// fake catalog seeds (Entities.Fake.cs's counts), then Home again.</summary>
        static List<Shell.Route> SoakRoutes()
        {
            var routes = new List<Shell.Route>
            {
                new(Shell.RouteKind.Home), new(Shell.RouteKind.Browse), new(Shell.RouteKind.LibraryAlbums),
                new(Shell.RouteKind.LibraryArtists), new(Shell.RouteKind.Liked), new(Shell.RouteKind.Recents),
            };
            for (int i = 0; i < 15; i++) routes.Add(Shell.For(EntityUri.Parse("spotify:album:al" + i.ToString(CultureInfo.InvariantCulture)), "Soak album"));
            for (int i = 0; i < 12; i++) routes.Add(Shell.For(EntityUri.Parse("spotify:artist:ar" + i.ToString(CultureInfo.InvariantCulture)), "Soak artist"));
            for (int i = 0; i < 7; i++) routes.Add(Shell.For(EntityUri.Parse("spotify:playlist:pl" + i.ToString(CultureInfo.InvariantCulture)), "Soak playlist"));
            for (int i = 0; i < 6; i++) routes.Add(Shell.For(EntityUri.Parse("spotify:playlist:plx" + i.ToString(CultureInfo.InvariantCulture)), "Soak playlist"));
            routes.Add(new Shell.Route(Shell.RouteKind.Home));
            return routes;
        }

        /// <summary>The real profile's pages: the library lists, then its recent albums, playlists and artists (the
        /// frame bench's profile reading: play-recency.json and history.json), then Home again.</summary>
        static List<Shell.Route> RealSoakRoutes()
        {
            var t = RealTargets(s_frameBench);
            var routes = new List<Shell.Route>
            {
                new(Shell.RouteKind.Home), new(Shell.RouteKind.LibraryAlbums), new(Shell.RouteKind.LibraryArtists),
                new(Shell.RouteKind.Liked), new(Shell.RouteKind.Recents),
            };
            foreach (string u in t.Playlists.Take(8)) routes.Add(Shell.For(EntityUri.Parse(u), "Soak playlist"));
            foreach (string u in t.Albums.Take(8)) routes.Add(Shell.For(EntityUri.Parse(u), "Soak album"));
            foreach (string u in t.Artists.Take(6)) routes.Add(Shell.For(EntityUri.Parse(u), "Soak artist"));
            routes.Add(new Shell.Route(Shell.RouteKind.Home));
            return routes;
        }

        /// <summary>`--inline-max-commands N` (the soak and the frame bench): the recorder's inline threshold for this run
        /// (FluentGpu.Render.SliceRecorder.InlineMaxCommands; 0 = every static segment keeps tiles), so one binary A/Bs it.</summary>
        static void ApplyInlineArg()
        {
            string[] argv = Platform.Args.Argv;
            int at = Array.IndexOf(argv, "--inline-max-commands");
            if (at >= 0 && at + 1 < argv.Length && int.TryParse(argv[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0)
            {
                FluentGpu.Render.SliceRecorder.InlineMaxCommands = n;
                Say("[probe] inline max commands = " + n);
            }
        }

        /// <summary>Where a probe run's window goes (called by the window factory, before the first frame, so it never
        /// shows anywhere else). The memory soak and a bench run need a window nothing covers — a covered window stands its
        /// presents down and evicts its tiles as hidden — and must never take over the screen of the person using the
        /// machine: so, not topmost and never focused, either OFF every monitor (the default; the headless OffscreenSurface's
        /// placement, which presents normally) — beside the monitor `--probe-near primary|N` names (default the primary), so the
        /// frame clock paces by THAT monitor's refresh, or at `--probe-at X,Y` — or, with `--probe-monitor secondary` (the first monitor that is not the primary) or
        /// `--probe-monitor N` (EnumDisplayMonitors order), at that monitor's top-left, for a bench whose frame pacing needs a real
        /// monitor.</summary>
        public static void PlaceProbeWindow()
        {
            string[] argv = Platform.Args.Argv;
            if (!IsProbeWindowRun() || FluentApp.WindowHandle == 0) return;
            // `--bench-size WxH` (DIP at the window's DPI): the frame bench measures at a stated size instead of whatever size the window
            // came up at, so two runs (and a run against the inspector's renders) compare one layout. The summary's `windowPx` says what landed.
            int cx = 0, cy = 0;
            int sizeAt = Array.IndexOf(argv, "--bench-size");
            if (sizeAt >= 0 && sizeAt + 1 < argv.Length && TryBenchSize(argv[sizeAt + 1], out float wDip, out float hDip))
            {
                float scale = GetDpiForWindow(FluentApp.WindowHandle) is > 0 and uint dpi ? dpi / 96f : 1f;
                (cx, cy) = ((int)MathF.Round(wDip * scale), (int)MathF.Round(hDip * scale));
            }
            // Off every monitor by default, but NEAREST the monitor named by `--probe-near primary|N` (default: the primary): the
            // engine paces by the refresh of the monitor nearest the window (MonitorFromWindow), so with monitors of different rates
            // a blind spot such as -30000,-30000 paced the bench by whichever panel happened to be closest (2026-10-25: an external
            // 100 Hz display above-left of the 120 Hz laptop gave a 10 ms frame clock under a 120 Hz compositor tick).
            var monitors = MonitorRects();
            int near = 0;
            int nearAt = Array.IndexOf(argv, "--probe-near");
            if (nearAt >= 0 && nearAt + 1 < argv.Length && argv[nearAt + 1] != "primary"
                && int.TryParse(argv[nearAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nearIndex) && nearIndex >= 0 && nearIndex < monitors.Count)
                near = nearIndex;
            else near = Math.Max(0, monitors.FindIndex(static m => m.Left <= 0 && m.Top <= 0 && m.Right > 0 && m.Bottom > 0));   // the primary holds 0,0
            (int x, int y) = OffscreenNear(monitors, near, cx > 0 ? cx + 64 : 4096, cy > 0 ? cy + 64 : 4096);
            string where = monitors.Count > 0 ? "near monitor " + near.ToString(CultureInfo.InvariantCulture) : "no monitor list";
            int at = Array.IndexOf(argv, "--probe-monitor");
            if (at >= 0 && at + 1 < argv.Length)
            {
                // "secondary" = the first monitor that is not the primary (the primary is the one at 0,0); a number = that
                // monitor in EnumDisplayMonitors order.
                (int, int)? tl = argv[at + 1] == "secondary" ? FirstNonPrimaryTopLeft()
                    : int.TryParse(argv[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? MonitorTopLeft(index) : null;
                if (tl is { } p) { (x, y) = p; where = "ON monitor " + argv[at + 1]; }
            }
            // `--probe-at X,Y` (virtual-screen px): an explicit spot (off-screen is the caller's business).
            int atAt = Array.IndexOf(argv, "--probe-at");
            if (atAt >= 0 && atAt + 1 < argv.Length && argv[atAt + 1].Split(',') is [var sx, var sy]
                && int.TryParse(sx, NumberStyles.Integer, CultureInfo.InvariantCulture, out int px) && int.TryParse(sy, NumberStyles.Integer, CultureInfo.InvariantCulture, out int py))
            { (x, y) = (px, py); where = "at --probe-at"; }
            Say("[probe] placement " + where + " (" + monitors.Count.ToString(CultureInfo.InvariantCulture) + " monitors)");
            // Move FIRST, then size at the DPI of where the window landed: a window created on a 150 % panel and placed on (or
            // beside) a 100 % one changes DPI on the move, and a size computed at the old DPI came out 1.5x (then "corrected" wrong).
            SetWindowPos(FluentApp.WindowHandle, 1 /*HWND_BOTTOM*/, x, y, 0, 0, 0x0010u /*NOACTIVATE*/ | 0x0001u /*NOSIZE*/);
            if (sizeAt >= 0 && sizeAt + 1 < argv.Length && TryBenchSize(argv[sizeAt + 1], out float wDip2, out float hDip2))
            {
                float scale = GetDpiForWindow(FluentApp.WindowHandle) is > 0 and uint dpi ? dpi / 96f : 1f;
                (cx, cy) = ((int)MathF.Round(wDip2 * scale), (int)MathF.Round(hDip2 * scale));
            }
            uint flags = 0x0010u /*NOACTIVATE*/ | (cx > 0 ? 0u : 0x0001u /*NOSIZE*/);
            if (cx > 0) UnboundTrackSize(FluentApp.WindowHandle);
            SetWindowPos(FluentApp.WindowHandle, 1 /*HWND_BOTTOM*/, x, y, cx, cy, flags);
            // The size is the OUTER rect: grow it by what the frame (the invisible resize borders) takes, so the CLIENT is the size asked.
            if (cx > 0 && GetClientRect(FluentApp.WindowHandle, out ClientRect client) && (client.Right != cx || client.Bottom != cy))
            {
                (cx, cy) = (2 * cx - client.Right, 2 * cy - client.Bottom);
                SetWindowPos(FluentApp.WindowHandle, 1 /*HWND_BOTTOM*/, x, y, cx, cy, flags);
            }
            Say("[probe] window placed at " + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture)
                + (cx > 0 ? " sized " + cx.ToString(CultureInfo.InvariantCulture) + "x" + cy.ToString(CultureInfo.InvariantCulture) + " px" : ""));
        }

        /// <summary>`1440x960` (DIP, both above 0).</summary>
        static bool TryBenchSize(string text, out float w, out float h)
        {
            w = h = 0f;
            string[] parts = text.ToLowerInvariant().Split('x');
            return parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out h) && w > 0f && h > 0f;
        }

        static (int X, int Y)? FirstNonPrimaryTopLeft()
        {
            for (int i = 0; MonitorTopLeft(i) is { } tl; i++)
                if (tl != (0, 0)) return tl;
            return null;
        }

        /// <summary>A run whose window is a probe window (memory soak, frame bench): placed by <see cref="PlaceProbeWindow"/> and
        /// shown without activation, so it never takes the foreground or the keyboard.</summary>
        public static bool IsProbeWindowRun()
        {
            string[] argv = Platform.Args.Argv;
            return Array.IndexOf(argv, "--mem-soak") >= 0 || Array.Exists(argv, static a => a == "--frame-bench" || a.StartsWith("--frame-bench=", StringComparison.Ordinal));
        }

        readonly record struct MonitorRect(int Left, int Top, int Right, int Bottom);

        /// <summary>Every monitor's rect (virtual-screen px), in EnumDisplayMonitors order.</summary>
        static List<MonitorRect> MonitorRects()
        {
            var rects = new List<MonitorRect>();
            EnumDisplayMonitors(0, 0, (nint m, nint dc, nint r, nint data) =>
            {
                rects.Add(new MonitorRect(System.Runtime.InteropServices.Marshal.ReadInt32(r), System.Runtime.InteropServices.Marshal.ReadInt32(r, 4),
                    System.Runtime.InteropServices.Marshal.ReadInt32(r, 8), System.Runtime.InteropServices.Marshal.ReadInt32(r, 12)));
                return true;
            }, 0);
            return rects;
        }

        /// <summary>A top-left for a <paramref name="w"/> x <paramref name="h"/> px window that overlaps NO monitor and whose nearest
        /// monitor (rect distance, MonitorFromWindow's rule) is <paramref name="target"/>: below, right of, left of or above it, at the
        /// first gap that clears every monitor. Falls back to -30000,-30000 when no side works (or no monitor is known).</summary>
        static (int X, int Y) OffscreenNear(List<MonitorRect> monitors, int target, int w, int h)
        {
            if (target < 0 || target >= monitors.Count) return (-30000, -30000);
            var t = monitors[target];
            static long Dist2(MonitorRect m, int l, int tp, int r, int b)
            {
                long dx = Math.Max(0, Math.Max(m.Left - r, l - m.Right)), dy = Math.Max(0, Math.Max(m.Top - b, tp - m.Bottom));
                return dx * dx + dy * dy;
            }
            foreach (int gap in (int[])[600, 2000, 8000])
            {
                (int, int)[] candidates = [(t.Left, t.Bottom + gap), (t.Right + gap, t.Top), (t.Left - gap - w, t.Top), (t.Left, t.Top - gap - h)];
                foreach (var (x, y) in candidates)
                {
                    int r = x + w, b = y + h;
                    bool clear = true, nearest = true;
                    long dt = Dist2(t, x, y, r, b);
                    for (int i = 0; i < monitors.Count; i++)
                    {
                        long d = Dist2(monitors[i], x, y, r, b);
                        if (d == 0) clear = false;
                        if (i != target && d <= dt) nearest = false;
                    }
                    if (clear && nearest) return (x, y);
                }
            }
            return (-30000, -30000);
        }

        static (int X, int Y)? MonitorTopLeft(int index)
        {
            var rects = new List<(int, int)>();
            EnumDisplayMonitors(0, 0, (nint m, nint dc, nint r, nint data) =>
            {
                int left = System.Runtime.InteropServices.Marshal.ReadInt32(r), top = System.Runtime.InteropServices.Marshal.ReadInt32(r, 4);
                rects.Add((left, top));
                return true;
            }, 0);
            return index >= 0 && index < rects.Count ? rects[index] : null;
        }

        delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc proc, nint data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForWindow(nint hwnd);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ClientRect { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool GetClientRect(nint hwnd, out ClientRect rect);

        static string MemLine(AppHost host)
        {
            long priv = 0, ws = 0;
            try { using var p = Process.GetCurrentProcess(); priv = p.PrivateMemorySize64; ws = p.WorkingSet64; } catch (InvalidOperationException) { }
            var gc = GC.GetGCMemoryInfo();
            var gens = gc.GenerationInfo;
            var sb = new StringBuilder(512)
                .Append("private=").Append(Mb(priv)).Append(" ws=").Append(Mb(ws))
                .Append(" heap=").Append(Mb(gc.HeapSizeBytes)).Append(" committed=").Append(Mb(gc.TotalCommittedBytes))
                .Append(" fragmented=").Append(Mb(gc.FragmentedBytes));
            string[] names = ["gen0", "gen1", "gen2", "loh", "poh"];
            for (int g = 0; g < gens.Length && g < names.Length; g++)
                sb.Append(' ').Append(names[g]).Append('=').Append(Mb(gens[g].SizeAfterBytes)).Append('/').Append(Mb(gens[g].FragmentationAfterBytes));
            sb.Append(" gcs=").Append(GC.CollectionCount(0)).Append('/').Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2))
              .Append(" totalAlloc=").Append(Mb(GC.GetTotalAllocatedBytes(precise: false)));
            if (Entities.Current is { } scope)
            {
                int rows = 0, cap = 0;
                foreach (var t in scope.Tables) { rows += t.LiveCount; cap += t.Count; }
                sb.Append(" rows=").Append(rows).Append('/').Append(cap).Append(" strings=").Append(Entities.Strings.MapCount);
            }
            // Frames the window stood its presents down since start (covered / occluded): a phase measured while this moves
            // measured a window that drew nothing, so the soak says so on every line.
            sb.Append(" stoodDown=").Append(host.FramesStoodDown);
            sb.Append(" scene=").Append(host.Scene.LiveCount).Append('/').Append(host.Scene.Capacity)
              .Append(" pathSlab=").Append(Mb(FluentGpu.Render.PathRealizationCache.Shared.SlabBytes));
            return sb.ToString();
        }

        static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static partial bool ShowWindow(nint hwnd, int cmd);

        /// <summary>A stated `--bench-size` must not depend on which monitors are attached. USER32's default maximum tracking
        /// size is the virtual screen plus the frame, so with only the laptop panel attached a 3444 px bench window was clamped
        /// to 2500 px (a different layout width, and Home too short to scroll). A comctl32 subclass on the probe window alone
        /// raises the maximum after the engine's own WM_GETMINMAXINFO handling has run.</summary>
        static unsafe void UnboundTrackSize(nint hwnd) => SetWindowSubclass(hwnd, &ProbeMinMaxProc, 0x57415645u, 0u);

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        static unsafe nint ProbeMinMaxProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
        {
            nint result = DefSubclassProc(hwnd, msg, wParam, lParam);
            if (msg == 0x0024u /*WM_GETMINMAXINFO*/ && lParam != 0)
            {
                var mmi = (MinMaxInfo*)lParam;
                mmi->MaxTrackX = mmi->MaxTrackY = 32767;
            }
            return result;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MinMaxInfo { public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY; }

        [System.Runtime.InteropServices.LibraryImport("comctl32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static unsafe partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

        [System.Runtime.InteropServices.LibraryImport("comctl32.dll")]
        private static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    }
}
