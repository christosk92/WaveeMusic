// ── Screens/Diagnostics.Probe.MemSoak.cs ──────────────────────────────────────────────────────────────────────────
// the `--mem-soak` GUI arm: what a long browsing session leaves on the managed heap, and how much of it is garbage.
//
// Role: SHELL
// Owner: S
//
// Run beside a live Wavee with a scratch profile (its own single instance, no real account):
//   Wavee.exe --fake --profile <scratch dir> --mem-soak [--mem-soak-rounds N] [--mem-soak-idle-sec S] [--mem-soak-minimize]
//             [--mem-soak-loh-mb N] [--probe-out <dir>]
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
            if (window is not Win32Window w || device is not D3D12Device gpu) { Say("[mem-soak] unavailable: requires Win32Window + D3D12Device"); return true; }
            if (!WarmUntilShell(host, w, gpu, 600)) { Say("[mem-soak] the shell never activated a route"); return true; }

            var report = new StringBuilder(4096).AppendLine()
                .AppendLine("=== WAVEE MEM SOAK ===")
                .AppendLine("version=" + VersionLabel() + "  rounds=" + s_options.MemSoakRounds + "  idleSec=" + s_options.MemSoakIdleSec);
            void Phase(string name)
            {
                string line = "[mem-soak] " + name + " " + MemLine(host);
                report.AppendLine(line);
                Say(line);
            }

            Phase("start");
            var routes = SoakRoutes();
            int hops = 0;
            for (int r = 0; r < s_options.MemSoakRounds && !w.IsClosed; r++)
            {
                foreach (var route in routes)
                {
                    if (w.IsClosed) break;
                    Nav(route);
                    // Enough frames for the page to mount, its rows to bind and the fake provider's answers to land.
                    for (int f = 0; f < 24 && !w.IsClosed; f++) FrameFast(host, w, gpu);
                    hops++;
                }
                Phase("round=" + (r + 1).ToString(CultureInfo.InvariantCulture) + " hops=" + hops.ToString(CultureInfo.InvariantCulture));
            }
            Nav(new Shell.Route(Shell.RouteKind.Home));
            PumpPaced(host, w, 2.0);
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

            // `--mem-soak-minimize`: idle the way an owner leaves the app — minimized — which is when the governor may
            // compact (Residency.Heap.cs). Restored before the dump so the dump window behaves like the plain run.
            if (s_options.MemSoakMinimize) ShowWindow(w.Handle.Value, 6 /*SW_MINIMIZE*/);
            PumpPaced(host, w, s_options.MemSoakIdleSec);
            Phase(s_options.MemSoakMinimize ? "after-idle minimized" : "after-idle");
            if (s_options.MemSoakMinimize) ShowWindow(w.Handle.Value, 9 /*SW_RESTORE*/);

            string dir = OutDir();
            string ready = Path.Combine(dir, "mem-soak-dump.ready"), done = Path.Combine(dir, "mem-soak-dump.done");
            try { if (File.Exists(done)) File.Delete(done); } catch (IOException) { }
            WriteArtifact(dir, "mem-soak-dump.ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Say("[mem-soak] dump window open: pid=" + Environment.ProcessId + " (waiting for " + done + ")");
            var sw = Stopwatch.StartNew();
            while (!w.IsClosed && !File.Exists(done) && sw.Elapsed.TotalSeconds < 120) PumpPaced(host, w, 0.5);
            try { File.Delete(ready); } catch (IOException) { }
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
            sb.Append(" scene=").Append(host.Scene.LiveCount).Append('/').Append(host.Scene.Capacity)
              .Append(" pathSlab=").Append(Mb(FluentGpu.Render.PathRealizationCache.Shared.SlabBytes));
            return sb.ToString();
        }

        static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(nint hwnd, int cmd);
    }
}
