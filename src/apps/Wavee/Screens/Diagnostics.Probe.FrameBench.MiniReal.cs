// THROWAWAY (never committed): the real mini player window under the frame bench, kept OFF every monitor, with a per-thread
// wake census (NtQuerySystemInformation context switches) and a memory breakdown.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal.Windows;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        [LibraryImport("user32.dll")] private static partial nint GetWindowLongPtrW(nint h, int index);
        [LibraryImport("user32.dll")] private static partial int SetForegroundWindow(nint h);
        [LibraryImport("ntdll.dll")] private static partial int NtQuerySystemInformation(int cls, nint buf, int len, out int ret);
        [LibraryImport("kernel32.dll")] private static partial nint OpenThread(uint access, int inherit, uint tid);
        [LibraryImport("kernel32.dll")] private static partial int CloseHandle(nint h);
        [LibraryImport("kernel32.dll")] private static partial int GetThreadDescription(nint h, out nint desc);
        [LibraryImport("kernel32.dll")] private static partial nint LocalFree(nint p);
        [LibraryImport("kernel32.dll")] private static partial nint VirtualQuery(nint addr, out MemInfo info, nint len);

        [StructLayout(LayoutKind.Sequential)]
        struct MemInfo { public nint Base, AllocBase; public uint AllocProtect; public ushort Partition; public nint Size; public uint State, Protect, Type; }

        static AppHost? MiniChild(AppHost host)
        {
            var list = typeof(AppHost).GetField("_detachedHosts", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(host) as System.Collections.IList;
            return list is { Count: > 0 } ? list[0] as AppHost : null;
        }

        static nint ChildHwnd(AppHost child)
            => ((FluentGpu.Pal.IPlatformWindow)typeof(AppHost).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(child)!).Handle.Value;

        readonly record struct ThreadSample(uint Tid, long Kernel, long User, uint Switches);

        static unsafe Dictionary<uint, ThreadSample> Threads()
        {
            var map = new Dictionary<uint, ThreadSample>();
            int len = 1 << 20;
            for (int attempt = 0; attempt < 6; attempt++, len *= 2)
            {
                nint buf = Marshal.AllocHGlobal(len);
                try
                {
                    int st = NtQuerySystemInformation(5, buf, len, out _);
                    if (st == unchecked((int)0xC0000004)) continue;
                    if (st != 0) return map;
                    long pid = Environment.ProcessId;
                    byte* p = (byte*)buf;
                    while (true)
                    {
                        uint next = *(uint*)p;
                        uint nthreads = *(uint*)(p + 4);
                        long upid = (long)*(nint*)(p + 0x50);
                        if (upid == pid)
                        {
                            byte* t = p + 0x100;
                            for (int i = 0; i < nthreads; i++, t += 0x50)
                            {
                                uint tid = (uint)(long)*(nint*)(t + 48);
                                map[tid] = new ThreadSample(tid, *(long*)t, *(long*)(t + 8), *(uint*)(t + 64));
                            }
                            return map;
                        }
                        if (next == 0) return map;
                        p += next;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return map;
        }

        static string ThreadName(uint tid)
        {
            nint h = OpenThread(0x0800 /*QUERY_LIMITED*/, 0, tid);
            if (h == 0) return "t" + tid;
            try
            {
                if (GetThreadDescription(h, out nint d) >= 0 && d != 0)
                {
                    string s = Marshal.PtrToStringUni(d) ?? "";
                    LocalFree(d);
                    if (s.Length > 0) return s;
                }
            }
            finally { CloseHandle(h); }
            return "t" + tid;
        }

        /// <summary>Per-thread wakes/s and CPU % of one core over the window, the top rows, as a census text and metrics.</summary>
        static string Census(Dictionary<uint, ThreadSample> a, Dictionary<uint, ThreadSample> b, double sec, List<KeyValuePair<string, double>> extra)
        {
            var rows = new List<(string Name, double Wakes, double Cpu)>();
            double total = 0, totalCpu = 0;
            foreach (var (tid, s1) in b)
            {
                a.TryGetValue(tid, out var s0);
                double wakes = (s1.Switches - s0.Switches) / sec;
                double cpu = ((s1.Kernel + s1.User) - (s0.Kernel + s0.User)) / 1e7 / sec * 100.0;
                if (wakes <= 0.05 && cpu <= 0.01) continue;
                rows.Add((ThreadName(tid) + "#" + tid, wakes, cpu));
                total += wakes; totalCpu += cpu;
            }
            rows.Sort(static (x, y) => y.Wakes.CompareTo(x.Wakes));
            var sb = new System.Text.StringBuilder();
            sb.Append("  threads: total wakes/s=").Append(total.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" cpu%1core=").Append(totalCpu.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
            foreach (var r in rows)
                sb.Append("    ").Append(r.Name.PadRight(44)).Append(r.Wakes.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8))
                  .Append(" wakes/s ").Append(r.Cpu.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(7)).AppendLine(" %core");
            extra.Add(new("thr.wakesPerSec", total));
            extra.Add(new("thr.cpuPctOneCore", totalCpu));
            return sb.ToString();
        }

        // ── the UI thread's retrieved messages (a WH_GETMESSAGE hook on this thread) ──
        [LibraryImport("user32.dll")] private static unsafe partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint mod, uint tid);
        [LibraryImport("user32.dll")] private static partial int UnhookWindowsHookEx(nint h);
        [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint h, int code, nint w, nint l);
        [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
        static nint s_hook;
        static readonly Dictionary<uint, int> s_msgs = new();
        [UnmanagedCallersOnly]
        static unsafe nint GetMsgHook(int code, nint w, nint l)
        {
            if (code >= 0 && w == 1 /*PM_REMOVE*/)
            {
                uint msg = *(uint*)((byte*)l + 8);   // MSG: hwnd(8), message(4)
                s_msgs.TryGetValue(msg, out int n); s_msgs[msg] = n + 1;
            }
            return CallNextHookEx(s_hook, code, w, l);
        }
        static unsafe void StartMsgCensus() { s_msgs.Clear(); s_hook = SetWindowsHookExW(3, &GetMsgHook, 0, GetCurrentThreadId()); }
        static string MsgCensus(double sec)
        {
            if (s_hook != 0) { UnhookWindowsHookEx(s_hook); s_hook = 0; }
            var sb = new System.Text.StringBuilder("  messages/s (id hex):").AppendLine();
            foreach (var (k, n) in s_msgs.OrderByDescending(static p => p.Value))
                sb.Append("    0x").Append(k.ToString("X4", CultureInfo.InvariantCulture)).Append("  ").Append((n / sec).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
            return sb.ToString();
        }

        // ── the loop's own wait requests: which host asked for the combined timeout ──
        static readonly Dictionary<string, int> s_waits = new();
        static bool s_waitCensus;
        static void NoteLoopWait(AppHost host, int asked)
        {
            if (!s_waitCensus) return;
            static string B(int ms) => ms < 0 ? "block" : ms == 0 ? "0" : ms <= 9 ? "1-9" : ms <= 20 ? "10-20" : ms <= 40 ? "21-40" : ms <= 120 ? "41-120" : ms <= 600 ? "121-600" : ">600";
            int main = host.RecommendedWaitMs();
            var child = MiniChild(host);
            int c = child?.RecommendedWaitMs() ?? -2;
            string ck = child is null ? "-" : B(c) + "/" + typeof(AppHost).GetField("_lastWaitKind", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(child);
            string k = "asked=" + B(asked) + " main=" + B(main) + " child=" + ck;
            s_waits.TryGetValue(k, out int n); s_waits[k] = n + 1;
        }

        [LibraryImport("user32.dll")] private static partial uint GetQueueStatus(uint flags);
        static void NoteLoopWake(int asked, double elapsedMs)
        {
            if (!s_waitCensus) return;
            uint q = GetQueueStatus(0x04FF /*QS_ALLINPUT*/);
            string k = asked >= 0 && elapsedMs >= asked - 1 ? "wake=timeout" : (q & 0xFFFF) != 0 ? "wake=msg:0x" + (q & 0xFFFF).ToString("X", CultureInfo.InvariantCulture) : "wake=event";
            s_waits.TryGetValue(k, out int n); s_waits[k] = n + 1;
        }

        static string WaitCensus(double sec)
        {
            var sb = new System.Text.StringBuilder("  loop waits/s:").AppendLine();
            foreach (var (k, n) in s_waits.OrderByDescending(static p => p.Value))
                sb.Append("    ").Append(k.PadRight(60)).Append((n / sec).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
            s_waits.Clear();
            return sb.ToString();
        }

        // ── cross-thread posts by seam and inner delegate, and the live .NET timers ──
        static readonly Dictionary<string, int> s_posts = new();
        static Action<Action> CountingPoster(string seam, Action<Action> inner) => a =>
        {
            string k = seam + ":" + (a.Method.DeclaringType?.FullName ?? "?") + "." + a.Method.Name;
            lock (s_posts) { s_posts.TryGetValue(k, out int n); s_posts[k] = n + 1; }
            inner(a);
        };

        static Action StartPostCensus()
        {
            lock (s_posts) s_posts.Clear();
            var pb = Playback.ToUi; var sp = Spotify.Post; var st = Store.Post; var pa = Wavee.Palette.Post; var ly = Lyrics.Store.ToUi; var sb = Sidebar.ToUi;
            Playback.ToUi = CountingPoster("playback", pb);
            Spotify.Post = CountingPoster("spotify", sp);
            Store.Post = CountingPoster("store", st);
            Wavee.Palette.Post = CountingPoster("palette", pa);
            Lyrics.Store.ToUi = CountingPoster("lyrics", ly);
            Sidebar.ToUi = CountingPoster("sidebar", sb);
            return () => { Playback.ToUi = pb; Spotify.Post = sp; Store.Post = st; Wavee.Palette.Post = pa; Lyrics.Store.ToUi = ly; Sidebar.ToUi = sb; };
        }

        static string PostCensus(double sec)
        {
            var sb = new System.Text.StringBuilder("  posts/s:\n");
            lock (s_posts)
                foreach (var (k, n) in s_posts.OrderByDescending(static p => p.Value))
                    sb.Append("    ").Append(k.PadRight(90)).Append((n / sec).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
            return sb.ToString();
        }

        static string TimerCensus()
        {
            var sb = new System.Text.StringBuilder("  .NET timers (period ms, due ms, callback):\n");
            try
            {
                var tq = typeof(System.Threading.Timer).Assembly.GetType("System.Threading.TimerQueue");
                var inst = tq?.GetProperty("Instances", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Array;
                var tqt = typeof(System.Threading.Timer).Assembly.GetType("System.Threading.TimerQueueTimer")!;
                FieldInfo? F(string n) => tqt.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic);
                var fNext = F("_next"); var fCb = F("_timerCallback"); var fPeriod = F("_period"); var fDue = F("_dueTime"); var fState = F("_state");
                if (inst is null) return sb.Append("    (no TimerQueue.Instances)\n").ToString();
                foreach (var q in inst)
                {
                    if (q is null) continue;
                    foreach (var fn in new[] { "_shortTimers", "_longTimers" })
                    {
                        var t = tq!.GetField(fn, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(q);
                        int guard = 0;
                        while (t is not null && guard++ < 500)
                        {
                            var cb = fCb?.GetValue(t) as Delegate;
                            var state = fState?.GetValue(t);
                            string who = cb is null ? "?" : (cb.Method.DeclaringType?.FullName ?? "?") + "." + cb.Method.Name;
                            if (state is Delegate sd) who += " state=" + (sd.Method.DeclaringType?.FullName ?? "?") + "." + sd.Method.Name;
                            else if (state is not null) who += " state=" + state.GetType().FullName;
                            sb.Append("    ").Append(Convert.ToString(fPeriod?.GetValue(t), CultureInfo.InvariantCulture)!.PadLeft(10))
                              .Append(' ').Append(Convert.ToString(fDue?.GetValue(t), CultureInfo.InvariantCulture)!.PadLeft(10)).Append(' ').Append(who).AppendLine();
                            t = fNext?.GetValue(t);
                        }
                    }
                }
            }
            catch (Exception ex) { sb.Append("    failed: ").Append(ex.Message).AppendLine(); }
            return sb.ToString();
        }

        [LibraryImport("dbghelp.dll")] private static partial int MiniDumpWriteDump(nint process, uint pid, nint file, uint type, nint e, nint u, nint c);
        [LibraryImport("dxgi.dll")] private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

        /// <summary>This process's GPU memory on adapter 0, LOCAL and NON-LOCAL segments (DXGI, read now: the engine samples only on presents).</summary>
        static unsafe (double Local, double NonLocal) GpuUsage()
        {
            var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            var iidAdapter3 = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");
            if (CreateDXGIFactory1(in iidFactory1, out nint f) < 0 || f == 0) return (-1, -1);
            try
            {
                nint a = 0;
                var vt = *(nint**)f;
                if (((delegate* unmanaged<nint, uint, nint*, int>)vt[12])(f, 0, &a) < 0 || a == 0) return (-1, -1);
                try
                {
                    nint a3 = 0;
                    var va = *(nint**)a;
                    if (((delegate* unmanaged<nint, Guid*, nint*, int>)va[0])(a, &iidAdapter3, &a3) < 0 || a3 == 0) return (-1, -1);
                    try
                    {
                        ulong* local = stackalloc ulong[4];
                        ulong* nonLocal = stackalloc ulong[4];
                        var v3 = *(nint**)a3;
                        ((delegate* unmanaged<nint, uint, int, ulong*, int>)v3[14])(a3, 0, 0, local);
                        ((delegate* unmanaged<nint, uint, int, ulong*, int>)v3[14])(a3, 0, 1, nonLocal);
                        return (local[1], nonLocal[1]);
                    }
                    finally { ((delegate* unmanaged<nint, uint>)(*(nint**)a3)[2])(a3); }
                }
                finally { ((delegate* unmanaged<nint, uint>)(*(nint**)a)[2])(a); }
            }
            finally { ((delegate* unmanaged<nint, uint>)(*(nint**)f)[2])(f); }
        }

        static string MemoryBreakdown(AppHost host, List<KeyValuePair<string, double>> extra)
        {
            double priv = 0, image = 0, mapped = 0;
            nint addr = 0;
            while (VirtualQuery(addr, out var mi, Marshal.SizeOf<MemInfo>()) != 0)
            {
                if (mi.State == 0x1000 /*COMMIT*/)
                {
                    if (mi.Type == 0x20000) priv += mi.Size; else if (mi.Type == 0x1000000) image += mi.Size; else mapped += mi.Size;
                }
                long nextAddr = (long)mi.Base + (long)mi.Size;
                if (nextAddr <= (long)addr) break;
                addr = (nint)nextAddr;
            }
            var gc = GC.GetGCMemoryInfo();
            using var proc = Process.GetCurrentProcess();
            const double MB = 1024.0 * 1024.0;
            var sb = new System.Text.StringBuilder();
            void Row(string k, double v) { sb.Append("    ").Append(k.PadRight(28)).Append((v / MB).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8)).AppendLine(" MB"); extra.Add(new("mem." + k, v / MB)); }
            sb.AppendLine("  memory:");
            Row("workingSet", proc.WorkingSet64);
            Row("privateBytes", proc.PrivateMemorySize64);
            Row("commitPrivate", priv);
            Row("commitImage", image);
            Row("commitMapped", mapped);
            Row("gcHeap", gc.HeapSizeBytes);
            Row("gcCommitted", gc.TotalCommittedBytes);
            Row("gcFragmented", gc.FragmentedBytes);
            Row("privateMinusGc", priv - gc.TotalCommittedBytes);
            var (gl, gn) = GpuUsage();
            Row("gpuLocal", gl);
            Row("gpuNonLocal", gn);
            for (int g = 0; g < 3; g++) Row("gcGen" + g + "Size", gc.GenerationInfo[g].SizeAfterBytes);
            Row("gcLohSize", gc.GenerationInfo[3].SizeAfterBytes);
            Row("gcPohSize", gc.GenerationInfo[4].SizeAfterBytes);
            return sb.ToString();
        }

        static Pending MiniReal(string name, AppHost host, Win32Window w, Func<string?, List<KeyValuePair<string, double>>?, Pending> measure,
            Action<bool> baseFn, Action<double> settle)
        {
            string[] parts = name.Split('-');   // mp-<face>-<playing|paused>
            int face = int.Parse(parts[1], CultureInfo.InvariantCulture);
            bool playing = parts.Length < 3 || parts[2] != "paused";
            bool noShrink = Array.IndexOf(parts, "noshrink") >= 0;
            if (noShrink) host.ShrinkSwapchainWhenHidden = false;
            baseFn(playing);
            if (!playing) SettleQuiet(host, w, 15);
            MiniPlayer.InspectMount(false, face);
            Platform.Settings.Set(Platform.Keys.MiniPlayerFace, face);
            MiniPlayer.ProbeOffscreen = true;
            nint fg0 = GetForegroundWindow();
            var sw0 = Stopwatch.StartNew();
            MiniPlayer.Toggle("probe");
            PumpUntil(host, w, () => MiniChild(host) is { } c && c.PresentedSequence > 0, 10);
            double openMs = sw0.Elapsed.TotalMilliseconds;
            var child = MiniChild(host);
            settle(3.0);
            nint fg1 = GetForegroundWindow();
            var extra = new List<KeyValuePair<string, double>>();
            if (child is null) return new Pending(name, "no mini window");
            nint hwnd = ChildHwnd(child);
            extra.Add(new("mp.openMs", openMs));
            extra.Add(new("mp.focusStolen", fg0 != fg1 ? 1 : 0));
            extra.Add(new("mp.mainVisible", w.IsVisible ? 1 : 0));
            ulong c0 = child.PresentedSequence;
            var th0 = Threads();
            var undoPosts = StartPostCensus();
            bool deep = Array.IndexOf(Platform.Args.Argv, "--bench-deep") >= 0;
            s_waits.Clear(); s_waitCensus = deep; if (deep) StartMsgCensus();
            var sw = Stopwatch.StartNew();
            var r = measure(name + (playing ? " playing" : " paused") + " face=" + face, extra);
            double sec = sw.Elapsed.TotalSeconds;
            var th1 = Threads();
            undoPosts();
            s_waitCensus = false;
            string timers = TimerCensus() + (deep ? WaitCensus(sec) + MsgCensus(sec) : "");
            extra.Add(new("mp.childPresentsPerSec", (child.PresentedSequence - c0) / sec));
            string census = Census(th0, th1, sec, extra);
            string mem = MemoryBreakdown(host, extra);
            WriteArtifact(OutDir(), name + "-census.txt", "childPres/s=" + ((child.PresentedSequence - c0) / sec).ToString("0.00", CultureInfo.InvariantCulture) + "\n" + census + PostCensus(sec) + timers + mem);
            if (Array.IndexOf(Platform.Args.Argv, "--bench-rail") >= 0)
            {
                var sb = new System.Text.StringBuilder("rail rows (t s, model frac, row value, live, from):").AppendLine();
                var t0 = Stopwatch.StartNew();
                for (int i = 0; i < 12; i++)
                {
                    long now = Playback.FrameNowMs();
                    var st = Playback.Snap();
                    double model = st.DurationMs > 0 ? st.Position(now) / (double)st.DurationMs : 0;
                    foreach (var wr in Shell.ProbeRailStates())
                    {
                        var (fill, live, from, anim) = wr;
                        float v = float.NaN;
                        bool has = anim is not null && !fill.IsNull && anim.TryGetTrackValue(fill, FluentGpu.Animation.AnimChannel.ScaleX, out v);
                        sb.Append(t0.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(' ')
                          .Append(model.ToString("0.00000", CultureInfo.InvariantCulture)).Append(' ')
                          .Append(has ? v.ToString("0.00000", CultureInfo.InvariantCulture) : "-").Append(' ').Append(live).Append(' ')
                          .Append(from.ToString("0.00000", CultureInfo.InvariantCulture)).AppendLine();
                    }
                    Pump(host, w, 1.0, null);
                }
                WriteArtifact(OutDir(), name + "-rail.txt", sb.ToString());
            }
            if (Array.IndexOf(Platform.Args.Argv, "--bench-mini-shots") >= 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    child.RequestFrameCapture();
                    FrameCaptureResult? cap = null;
                    long until = Environment.TickCount64 + 3000;
                    while (!w.IsClosed && Environment.TickCount64 < until && !child.TryTakeFrameCapture(out cap)) Pump(host, w, 0.001, null);
                    if (cap?.Bgra is { } px)
                        try { PngWriter.WriteBgra(Path.Combine(OutDir(), name + "-shot" + i + ".png"), px, cap.WidthPx, cap.HeightPx); } catch (IOException) { }
                    Pump(host, w, 3.0, null);
                }
            }
            if (Array.IndexOf(Platform.Args.Argv, "--bench-dump") >= 0)
            {
                string path = Path.Combine(OutDir(), name + ".dmp");
                using var fs = new FileStream(path, FileMode.Create);
                using var self = Process.GetCurrentProcess();
                MiniDumpWriteDump(self.Handle, (uint)self.Id, fs.SafeFileHandle.DangerousGetHandle(), 0x2 /*WithFullMemory*/ | 0x4 /*HandleData*/, 0, 0, 0);
            }
            extra.Add(new("mp.childParked", child.IsParked ? 1 : 0));
            extra.Add(new("mp.mainParked", host.IsParked ? 1 : 0));
            extra.Add(new("mp.mainHiddenStage", host.HiddenStageCensus));
            var sw1 = Stopwatch.StartNew();
            MiniPlayer.Close("probe");
            PumpUntil(host, w, () => w.IsVisible && host.PresentedSequence > 0, 5);
            extra.Add(new("mp.returnMs", sw1.Elapsed.TotalMilliseconds));
            if (fg0 != 0) SetForegroundWindow(fg0);   // the owner keeps the focus
            MiniPlayer.ProbeOffscreen = false;
            if (noShrink) host.ShrinkSwapchainWhenHidden = true;
            settle(2.0);
            return r;
        }

        /// <summary>The main window's own census (the baseline arms).</summary>
        static Pending CensusOf(string name, AppHost host, Func<string?, List<KeyValuePair<string, double>>?, Pending> measure, string note)
        {
            var extra = new List<KeyValuePair<string, double>>();
            var th0 = Threads();
            var undoPosts = StartPostCensus();
            var sw = Stopwatch.StartNew();
            var r = measure(note, extra);
            var th1 = Threads();
            undoPosts();
            string timers = TimerCensus() + PostCensus(sw.Elapsed.TotalSeconds);
            WriteArtifact(OutDir(), name + "-census.txt", Census(th0, th1, sw.Elapsed.TotalSeconds, extra) + timers + MemoryBreakdown(host, extra));
            return r;
        }
    }
}

public static partial class Shell
{
    // THROWAWAY probe: the live rails' row state.
    internal static List<(FluentGpu.Foundation.NodeHandle, bool, float, FluentGpu.Animation.AnimEngine?)> ProbeRailStates()
    {
        var list = new List<(FluentGpu.Foundation.NodeHandle, bool, float, FluentGpu.Animation.AnimEngine?)>();
        foreach (var wr in BarSeekRail.ProbeRails) if (wr.TryGetTarget(out var r)) list.Add(r.ProbeState);
        return list;
    }
}
