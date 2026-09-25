// ── Platform/Crash.Handler.cs ──────────────────────────────────────────────────────────────────────────────────────
// The out-of-process crash handler's CHILD arm (§B.1's table, §B.7's watchdog): `Crash.Handler.TryRun` is the first
// thing `App.Main` calls, before `Platform.Boot` — this process NEVER opens settings, the store or the app log. It
// watches the parent by pid + a stdin line protocol, writes minidumps on request (`MiniDumpWriteDump`, DbgHelp is not
// thread-safe against the SAME target process but this handler only ever dumps ITS one parent), runs the hang
// watchdog every 1 s tick, and — on the parent's exit — records an ExitCode/UncleanExit bundle when no managed/native
// bundle was already written for this run. Everything it does is logged to its OWN file, `handler.log`
// (`<logFolder>\crash\handler.log`, append, one line per event) — never the app log, which this process never opens.
//
// Role: SHELL (Win32 P/Invoke; engine-free — no D3D, no window of its own, no FluentGpu type anywhere in this file)
// Owner: WP-B
// Wave: crash-diagnostics
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.0 (arm dispatch), §B.1 (capture table), §B.7 (hang
//       watchdog), §F row "B · Handler + host", §G (verification), §I ("Child arm: Crash.Handler.TryRun")
//
// THE STDIN/STDOUT PROTOCOL (owned jointly with `Crash.Host.cs`, the parent side that writes it):
//   stdin  (parent → child), one line per event:
//     B                                    a heartbeat — the UI thread pump is alive right now
//     F                                    the first frame rendered
//     M1 / M0                              a modal pump opened / closed (FilePicker etc.) — suspends hang detection
//     X                                    the UI loop returned — the parent is on its way out, not hung
//     S <epoch>                            a suspend/resume edge (PowerSession) — a stale beat across sleep is not a hang
//     D <kind> <tid> <ptrHex> <bundleDir>  a dump request: kind is "managed" or "native", ptrHex is the parent's
//                                          EXCEPTION_POINTERS* (0 for managed — DbgHelp still gets a usable dump
//                                          without one), bundleDir already holds summary.json/report.txt/log-tail.txt
//   stdout (child → parent), one line per D reply only:
//     OK <bytes>                           the dump was written; bytes is minidump.dmp's final size
//     ERR <message>                        it was not

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

public static partial class Crash
{
    public static partial class Handler
    {
        // ── 1. the door: App.Main's first line ──────────────────────────────────────────────────────────────────────

        /// <summary>True (and never returns to the caller in the normal sense — <paramref name="exitCode"/> is the
        /// process's exit code) iff <paramref name="args"/> carries <c>--crash-handler &lt;parentPid&gt; &lt;logFolder&gt;
        /// &lt;datedLogPath|-&gt;</c>. This process's whole life is <see cref="Run"/>: no settings, no store, no app
        /// log — only <see cref="Crash.Files"/>/<see cref="Crash.Bundles"/> path arithmetic and Win32 calls.</summary>
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            int at = Array.IndexOf(args, "--crash-handler");
            if (at < 0 || at + 3 >= args.Length) return false;
            if (!int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid)) return false;
            string logFolder = args[at + 2];
            string datedLogPathArg = args[at + 3];
            exitCode = Run(parentPid, logFolder, datedLogPathArg == "-" ? null : datedLogPathArg);
            return true;
        }

        // ── 2. shared state (the reader thread writes; the main-loop thread reads — Interlocked/volatile throughout) ──

        static int s_parentPid;
        static string s_logFolder = "";
        static string? s_datedLogPath;
        static nint s_parentHandle;

        static long s_lastBeatUnbiased;          // QueryUnbiasedInterruptTime at the last 'B'
        static int s_suspendEpochAtLastBeat;      // the suspend epoch AS OF the last beat — a stale beat across sleep must not read as a hang
        static int s_suspendEpoch;
        static int s_firstFrameSeen;              // 0/1
        static volatile string s_installId = "";   // from the parent's I line; "" until it arrives
        static bool FirstFrameSeen => Interlocked.CompareExchange(ref s_firstFrameSeen, 0, 0) != 0;   // the parent's F line reached this child
        static int s_modalDepth;
        static int s_exiting;                     // 0/1
        static int s_bundleWrittenThisProcess;    // 0/1 — a Managed/Native/Hang bundle already exists for this parent's run
        static int s_hangReported;                // 0/1 — one hang dump per process, ever
        static int s_hangRecoveryLogged;          // 0/1 — "hang.recovered" is logged at most once
        static long s_hangReportedAtTickMs;
        static long s_hangWindowTrueSinceMs = -1; // Environment.TickCount64 when IsHungAppWindow first read true, continuously; -1 = not currently true

        static readonly object s_logLock = new();

        // ── 3. the run: open the parent, start the reader thread, tick until it exits ──────────────────────────────

        static int Run(int parentPid, string logFolder, string? datedLogPath)
        {
            s_parentPid = parentPid;
            s_logFolder = logFolder;
            s_datedLogPath = datedLogPath;
            s_lastBeatUnbiased = UnbiasedNow();

            try { Directory.CreateDirectory(Files.Root(logFolder)); } catch { }
            HandlerLog("handler.start");

            s_parentHandle = OpenProcess(ProcessSynchronize | ProcessQueryInformation | ProcessVmRead | ProcessDupHandle, false, unchecked((uint)parentPid));
            if (s_parentHandle == 0)
            {
                HandlerLog("handler.openProcess.failed");
                return 0;
            }

            var reader = new Thread(ReaderLoop) { IsBackground = true, Name = "crash-handler-stdin" };
            reader.Start();

            while (true)
            {
                uint wait = WaitForSingleObject(s_parentHandle, 1000);
                if (wait == WaitObjectSignaled) break;   // the parent exited
                if (wait == WaitFailed) { HandlerLog("handler.wait.failed"); break; }
                Tick();
            }

            int exit = OnParentExited();
            try { CloseHandle(s_parentHandle); } catch { }
            return exit;
        }

        static void ReaderLoop()
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                    HandleLine(line);
            }
            catch (Exception ex) { HandlerLog("handler.reader.failed " + ex.Message); }
        }

        static void HandleLine(string line)
        {
            if (line.Length == 0) return;
            switch (line[0])
            {
                case 'B':
                    Interlocked.Exchange(ref s_suspendEpochAtLastBeat, Interlocked.CompareExchange(ref s_suspendEpoch, 0, 0));
                    Interlocked.Exchange(ref s_lastBeatUnbiased, UnbiasedNow());
                    break;
                case 'F':
                    Interlocked.Exchange(ref s_firstFrameSeen, 1);
                    break;
                case 'I':   // "I <installId>": the parent's random install id, so this process's bundles carry it too
                    s_installId = line.Length > 2 ? line[2..].Trim() : "";
                    break;
                case 'X':
                    Interlocked.Exchange(ref s_exiting, 1);
                    break;
                case 'M':
                    if (line.Length > 1 && line[1] == '1') Interlocked.Increment(ref s_modalDepth);
                    else if (line.Length > 1 && line[1] == '0' && Interlocked.Decrement(ref s_modalDepth) < 0)
                        Interlocked.Exchange(ref s_modalDepth, 0);
                    break;
                case 'S':
                    {
                        int sp = line.IndexOf(' ');
                        if (sp > 0 && int.TryParse(line.AsSpan(sp + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int epoch))
                            Interlocked.Exchange(ref s_suspendEpoch, epoch);
                        break;
                    }
                case 'D':
                    HandleDumpRequest(line);
                    break;
            }
        }

        // ── 4. dump requests (parent-triggered: managed / native) ───────────────────────────────────────────────────

        static void HandleDumpRequest(string line)
        {
            string[] parts = line.Split(' ', 5);
            if (parts.Length < 5) { WriteReply("ERR malformed request"); return; }
            string kindToken = parts[1];
            if (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint tid)) tid = 0;
            nint ptrs = 0;
            if (parts[3].Length > 0)
            {
                try { ptrs = unchecked((nint)Convert.ToUInt64(parts[3], 16)); }
                catch (Exception ex) when (ex is FormatException or OverflowException) { ptrs = 0; }
            }
            string dir = parts[4];

            Interlocked.Exchange(ref s_bundleWrittenThisProcess, 1);
            bool ok = WriteMiniDump(dir, tid, ptrs, ptrs != 0);
            if (ok)
            {
                long bytes = DumpBytes(dir);
                HandlerLog("dump.written kind=" + kindToken + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture));
                WriteReply("OK " + bytes.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                HandlerLog("dump.failed kind=" + kindToken);
                WriteReply("ERR MiniDumpWriteDump failed");
            }
        }

        static void WriteReply(string line)
        {
            try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch (Exception ex) { HandlerLog("handler.reply.failed " + ex.Message); }
        }

        /// <summary>flags = MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules (§B.3 default: stacks
        /// + modules, no heap — the heap holds the DPAPI-unprotected credential and live tokens).</summary>
        const uint DumpFlags = 0x0000_0000 /* MiniDumpNormal */ | 0x0000_1000 /* MiniDumpWithThreadInfo */ | 0x0000_0020 /* MiniDumpWithUnloadedModules */;

        static unsafe bool WriteMiniDump(string dir, uint threadId, nint exceptionPointers, bool hasExceptionInfo)
        {
            string path = Path.Combine(dir, Files.DumpName);
            try { Directory.CreateDirectory(dir); } catch { }
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None);
                MINIDUMP_EXCEPTION_INFORMATION info = default;
                nint infoPtr = 0;
                if (hasExceptionInfo)
                {
                    info.ThreadId = threadId;
                    info.ExceptionPointers = exceptionPointers;
                    info.ClientPointers = 1;
                    infoPtr = (nint)(&info);
                }
                bool ok = MiniDumpWriteDump(s_parentHandle, unchecked((uint)s_parentPid), handle, DumpFlags, infoPtr, 0, 0);
                int win32 = ok ? 0 : Marshal.GetLastPInvokeError();
                handle.Dispose();
                handle = null;
                if (!ok && hasExceptionInfo)
                {
                    // ERROR_NOACCESS (0x800703e6) seen 2026-09-25 with ClientPointers over the parent's VEH pointers: retry
                    // ONCE without the exception stream. The faulting thread is still parked inside the hook, so its stack
                    // (fault frames included) is in the dump either way; only the exception record is lost.
                    HandlerLog("dump.writeDump.failed win32=0x" + win32.ToString("x8", CultureInfo.InvariantCulture) + " flags=0x" + DumpFlags.ToString("x", CultureInfo.InvariantCulture) + " hasException=1 retry=noexception");
                    handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    ok = MiniDumpWriteDump(s_parentHandle, unchecked((uint)s_parentPid), handle, DumpFlags, 0, 0, 0);
                    win32 = ok ? 0 : Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    handle = null;
                }
                if (!ok)
                {
                    // The HRESULT-shaped code is what dbghelp sets (e.g. 0x8007xxxx) — logged verbatim, it is the one clue.
                    HandlerLog("dump.writeDump.failed win32=0x" + win32.ToString("x8", CultureInfo.InvariantCulture) + " flags=0x" + DumpFlags.ToString("x", CultureInfo.InvariantCulture) + " hasException=" + (hasExceptionInfo ? 1 : 0));
                    TryDelete(path);
                    return false;
                }
                Bundles.EnforceDumpCap(dir);
                return File.Exists(path);
            }
            catch (Exception ex)
            {
                HandlerLog("dump.exception " + ex.Message);
                try { handle?.Dispose(); } catch { }
                TryDelete(path);
                return false;
            }
        }

        static long DumpBytes(string dir)
        {
            try { return new FileInfo(Path.Combine(dir, Files.DumpName)).Length; } catch { return 0; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── 5. the 1 s tick: the hang watchdog ───────────────────────────────────────────────────────────────────────

        static void Tick()
        {
            if (Interlocked.CompareExchange(ref s_hangReported, 0, 0) != 0) { CheckHangRecovery(); return; }
            CheckHang();
        }

        static void CheckHang()
        {
            // No window can exist before the first frame — skip the whole check rather than let a slow boot read as
            // a hang before there is anything to be hung.
            if (Interlocked.CompareExchange(ref s_firstFrameSeen, 0, 0) == 0) return;

            long now = UnbiasedNow();
            long lastBeat = Interlocked.Read(ref s_lastBeatUnbiased);
            bool noBeat = now - lastBeat >= HangRules.NoBeatMs * 10_000L;
            if (!noBeat) { s_hangWindowTrueSinceMs = -1; return; }

            nint hwnd = FindParentWindow();
            bool hasWindow = hwnd != 0;
            bool hungNow = hasWindow && IsHungAppWindow(hwnd);
            long tick = Environment.TickCount64;
            if (hungNow) { if (s_hangWindowTrueSinceMs < 0) s_hangWindowTrueSinceMs = tick; }
            else s_hangWindowTrueSinceMs = -1;
            bool hungWindow10s = s_hangWindowTrueSinceMs >= 0 && tick - s_hangWindowTrueSinceMs >= HangRules.HungWindowMs;

            bool debugged = CheckRemoteDebuggerPresent(s_parentHandle, out bool present) && present;
            bool modal = Interlocked.CompareExchange(ref s_modalDepth, 0, 0) > 0;
            bool exiting = Interlocked.CompareExchange(ref s_exiting, 0, 0) != 0;
            int epochNow = Interlocked.CompareExchange(ref s_suspendEpoch, 0, 0);
            int epochAtBeat = Interlocked.CompareExchange(ref s_suspendEpochAtLastBeat, 0, 0);

            bool hung = HangRules.IsHung(now, lastBeat, hasWindow, hungWindow10s, debugged, modal, exiting, epochAtBeat, epochNow);
            if (!hung) return;

            Interlocked.Exchange(ref s_hangReported, 1);
            s_hangReportedAtTickMs = tick;
            long noBeatMs = (now - lastBeat) / 10_000L;
            long hungMs = s_hangWindowTrueSinceMs >= 0 ? tick - s_hangWindowTrueSinceMs : 0;
            HandlerLog("hang.suspected noBeatMs=" + noBeatMs.ToString(CultureInfo.InvariantCulture)
                + " hungMs=" + hungMs.ToString(CultureInfo.InvariantCulture));
            WriteHangBundle(noBeatMs);
        }

        static void CheckHangRecovery()
        {
            if (Interlocked.CompareExchange(ref s_hangRecoveryLogged, 0, 0) != 0) return;
            long now = UnbiasedNow();
            long lastBeat = Interlocked.Read(ref s_lastBeatUnbiased);
            if (now - lastBeat >= HangRules.NoBeatMs * 10_000L) return;   // still no beat — not recovered yet
            long afterMs = Environment.TickCount64 - s_hangReportedAtTickMs;
            Interlocked.Exchange(ref s_hangRecoveryLogged, 1);
            if (afterMs <= HangRules.RecoveredAfterMs)
                HandlerLog("hang.recovered afterMs=" + afterMs.ToString(CultureInfo.InvariantCulture));
        }

        static void WriteHangBundle(long noBeatSeconds)
        {
            try
            {
                string dir = Bundles.Create(s_logFolder, Kind.Hang, DateTimeOffset.Now);
                string message = "no UI heartbeat for " + (noBeatSeconds / 1000).ToString(CultureInfo.InvariantCulture) + " s";
                var summary = Report.BuildSummary(Kind.Hang, null, 0, exceptionType: "Hang", exceptionMessage: message, beforeFirstFrame: !FirstFrameSeen, installIdOverride: s_installId);
                Bundles.WriteSummary(dir, summary);
                var sb = new StringBuilder(1024);
                Report.DescribeSynthetic(message, sb);
                Bundles.WriteReport(dir, sb.ToString());
                Bundles.WriteTail(dir, s_datedLogPath, 300, Feedback.RedactionRules.None);
                bool ok = WriteMiniDump(dir, 0, 0, false);
                Interlocked.Exchange(ref s_bundleWrittenThisProcess, 1);
                HandlerLog("hang.dump.written ok=" + (ok ? "true" : "false"));
                Bundles.PruneNow(s_logFolder);
            }
            catch (Exception ex) { HandlerLog("hang.bundle.failed " + ex.Message); }
        }

        // ── 6. the parent's exit: ExitCode / UncleanExit, or nothing (code 0 / a bundle already written) ─────────────

        static int OnParentExited()
        {
            bool gotCode = GetExitCodeProcess(s_parentHandle, out uint code);
            HandlerLog("parent exited code=" + (gotCode ? "0x" + code.ToString("x", CultureInfo.InvariantCulture) : "unknown"));

            if (gotCode && code != 0 && Interlocked.CompareExchange(ref s_bundleWrittenThisProcess, 0, 0) == 0)
            {
                // 1 = a plain non-zero exit (Task Manager "End task" on a WinExe leaves this); 0x40010004 =
                // STATUS_CONTROL_C_EXIT-adjacent forced termination. Both are evidence-free: kind UncleanExit, which
                // `ConsentPolicy.Decide` never prompts or uploads for.
                Kind kind = code is 1 or 0x40010004 or 0xFFFFFFFF ? Kind.UncleanExit : Kind.ExitCode;   // 0xFFFFFFFF = TerminateProcess(-1): PowerShell Stop-Process and some task killers
                try
                {
                    string dir = Bundles.Create(s_logFolder, kind, DateTimeOffset.Now);
                    string message = "process exited with code 0x" + code.ToString("x", CultureInfo.InvariantCulture);
                    var summary = Report.BuildSummary(kind, null, unchecked((int)code), exceptionType: kind.ToString(), exceptionMessage: message, beforeFirstFrame: !FirstFrameSeen, installIdOverride: s_installId);
                    Bundles.WriteSummary(dir, summary);
                    var sb = new StringBuilder(1024);
                    Report.DescribeSynthetic(message, sb);
                    Bundles.WriteReport(dir, sb.ToString());
                    Bundles.WriteTail(dir, s_datedLogPath, 300, Feedback.RedactionRules.None);
                    Bundles.PruneNow(s_logFolder);
                }
                catch (Exception ex) { HandlerLog("parent.exit.bundle.failed " + ex.Message); }
            }
            return 0;
        }

        // ── 7. handler.log — this process's ENTIRE log; never the app log, which it never opens ─────────────────────

        static void HandlerLog(string line)
        {
            try
            {
                string path = Path.Combine(Files.Root(s_logFolder), Files.HandlerLog);
                Directory.CreateDirectory(Files.Root(s_logFolder));
                string stamped = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
                    + " pid=" + s_parentPid.ToString(CultureInfo.InvariantCulture) + " " + line;
                lock (s_logLock) File.AppendAllText(path, stamped + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        // ── 8. the unbiased clock, the target window ─────────────────────────────────────────────────────────────────

        static long UnbiasedNow()
        {
            QueryUnbiasedInterruptTime(out ulong t);
            return unchecked((long)t);
        }

        static uint s_enumTargetPid;
        static nint s_enumFoundHwnd;

        /// <summary>The parent's first visible top-level window, or 0. Only the main-loop thread ever calls this (the
        /// static scratch fields below are safe without further synchronization), and <c>EnumWindows</c> invokes its
        /// callback synchronously on the calling thread.</summary>
        static unsafe nint FindParentWindow()
        {
            s_enumFoundHwnd = 0;
            s_enumTargetPid = unchecked((uint)s_parentPid);
            delegate* unmanaged[Stdcall]<nint, nint, int> cb = &EnumWindowsCallback;
            EnumWindows((nint)cb, 0);
            return s_enumFoundHwnd;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static int EnumWindowsCallback(nint hwnd, nint _)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == s_enumTargetPid && IsWindowVisible(hwnd))
            {
                s_enumFoundHwnd = hwnd;
                return 0;   // stop enumerating
            }
            return 1;       // continue
        }

        // ── 9. Win32 ─────────────────────────────────────────────────────────────────────────────────────────────────

        const uint ProcessSynchronize = 0x0010_0000, ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010, ProcessDupHandle = 0x0040;
        const uint WaitObjectSignaled = 0, WaitFailed = 0xFFFF_FFFF;

        [StructLayout(LayoutKind.Sequential)]
        struct MINIDUMP_EXCEPTION_INFORMATION
        {
            public uint ThreadId;
            public nint ExceptionPointers;
            public int ClientPointers;   // Win32 BOOL, blittable as int — this struct's address is taken directly, no marshaler involved
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint hObject);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool QueryUnbiasedInterruptTime(out ulong UnbiasedTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CheckRemoteDebuggerPresent(nint hProcess, [MarshalAs(UnmanagedType.Bool)] out bool pbDebuggerPresent);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsHungAppWindow(nint hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(nint hWnd);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(nint lpEnumFunc, nint lParam);

        [LibraryImport("dbghelp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool MiniDumpWriteDump(nint hProcess, uint processId, SafeFileHandle hFile, uint dumpType,
            nint exceptionParam, nint userStreamParam, nint callbackParam);
    }
}
