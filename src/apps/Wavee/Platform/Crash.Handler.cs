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
//     D <kind> <tid> <ptrHex> <bundleDir>  a dump request: kind is "managed", "native" or "test" (`DumpKinds`), ptrHex
//                                          is the parent's EXCEPTION_POINTERS* (0 for managed and test — DbgHelp still
//                                          gets a usable dump without one), bundleDir already holds
//                                          summary.json/report.txt/log-tail.txt. "test" is Settings › Developer › "Send
//                                          a test crash report" (`Crash.Host.WriteTestReport`, #165): a snapshot of a
//                                          parent that keeps RUNNING, so unlike a crash's request it never latches the
//                                          exit-code bundle (`DumpKinds.LatchesBundle`)
//   stdout (child → parent):
//     R                                    READY, once, as soon as the parent handle is open. The parent answers it by
//                                          duplicating its kill-on-close job handle into this process (`Crash.Launch`):
//                                          before R this child holds no job handle and dies with the parent; after it, it
//                                          lives until it exits. No R within 10 s and the parent kills and respawns it.
//     (then, one line per D reply:)
//     OK <bytes>                           the dump was written; bytes is minidump.dmp's final size
//     ERR <message>                        it was not
//   The parent's reply reader (`Crash.Launch.ReplyPump`) swallows R, so D replies still match in order.
//
// BOUNDED LIFE: once the parent has exited, this process force-exits after `ShutdownDeadlineMs` (30 s) even if a bundle
// write stalls. While the parent lives it waits on the parent handle with a 1 s timeout, so nothing in the main loop
// blocks forever; the stdin reader is a background thread and never holds the process open.
//
// THE CHILD FINALIZES THE BUNDLE (#165, crash-production-readiness-implementation.md "W3a"). Before it replies to a D
// line — so while the parent is still parked inside its hook — the child (1) resolves the faulting module out of process
// (`TryReadFault`: the parent's EXCEPTION_POINTERS → EXCEPTION_RECORD.ExceptionAddress → its module list), (2) writes
// the dump, (3) rewrites summary.json once with hasDump/dumpBytes/faultModule/faultOffset (`Bundles.UpdateSummary`) and
// (4) appends `fault=<module>+0x<offset>` to report.txt. The crashing process no longer rewrites its own summary after
// `OK`: less allocation on a heap that may be what just faulted, and a dump that outlives the parent's 10 s wait still
// lands in the summary.

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
        /// &lt;logBasePath|-&gt;</c> (the CONFIGURED log path, wavee.log — the child derives the day's file at bundle time, it
        /// outlives midnight). This process's whole life is <see cref="Run"/>: no settings, no store, no app
        /// log — only <see cref="Crash.Files"/>/<see cref="Crash.Bundles"/> path arithmetic and Win32 calls.</summary>
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            int at = Array.IndexOf(args, "--crash-handler");
            if (at < 0 || at + 3 >= args.Length) return false;
            if (!int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid)) return false;
            string logFolder = args[at + 2];
            string logBasePathArg = args[at + 3];
            exitCode = Run(parentPid, logFolder, logBasePathArg == "-" ? null : logBasePathArg);
            return true;
        }

        // ── 2. shared state (the reader thread writes; the main-loop thread reads — Interlocked/volatile throughout) ──

        static int s_parentPid;
        static string s_logFolder = "";
        static string? s_logBasePath;
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

        static int Run(int parentPid, string logFolder, string? logBasePath)
        {
            s_parentPid = parentPid;
            s_logFolder = logFolder;
            s_logBasePath = logBasePath;
            s_lastBeatUnbiased = UnbiasedNow();

            try { Directory.CreateDirectory(Files.Root(logFolder)); } catch { }
            HandlerLog("handler.start");

            s_parentHandle = OpenProcess(ProcessSynchronize | ProcessQueryInformation | ProcessVmRead | ProcessDupHandle, false, unchecked((uint)parentPid));
            if (s_parentHandle == 0)
            {
                HandlerLog("handler.openProcess.failed");
                return 0;
            }

            // Ready: the parent handle is open. Written BEFORE the reader thread starts so nothing else can interleave on stdout.
            HandlerLog("handler.ready");
            WriteReply("R");

            var reader = new Thread(ReaderLoop) { IsBackground = true, Name = "crash-handler-stdin" };
            reader.Start();

            while (true)
            {
                uint wait = WaitForSingleObject(s_parentHandle, 1000);
                if (wait == WaitObjectSignaled) break;   // the parent exited
                if (wait == WaitFailed) { HandlerLog("handler.wait.failed"); break; }
                Tick();
            }

            ArmShutdownDeadline(ResolveDeadlineMs());
            int exit = OnParentExited();
            try { CloseHandle(s_parentHandle); } catch { }
            return exit;
        }

        /// <summary>Once the parent is gone this process has this long to finish its bundle; then it terminates itself.</summary>
        public const int ShutdownDeadlineMs = 30_000;

        /// <summary>Test seam: an integer here (milliseconds) replaces <see cref="ShutdownDeadlineMs"/>.</summary>
        public const string DeadlineEnvVar = "WAVEE_CRASH_HANDLER_DEADLINE_MS";

        static int ResolveDeadlineMs()
            => int.TryParse(Environment.GetEnvironmentVariable(DeadlineEnvVar), NumberStyles.None, CultureInfo.InvariantCulture, out int ms) && ms > 0
                ? ms : ShutdownDeadlineMs;

        /// <summary>Starts a background thread that, after <paramref name="ms"/>, logs and TERMINATES this process
        /// (TerminateProcess — not Environment.Exit, which can itself wait on a wedged thread). A normal return from
        /// <see cref="Run"/> ends the process first and the thread dies with it.</summary>
        static void ArmShutdownDeadline(int ms)
        {
            new Thread(() =>
            {
                Thread.Sleep(ms);
                // NEVER block on the log lock here: a stalled disk can wedge the main thread inside it, which is exactly
                // what this deadline exists to bound. Log only if the lock is free within a moment; terminate regardless.
                if (Monitor.TryEnter(s_logLock, 500))
                {
                    try { File.AppendAllText(Path.Combine(Files.Root(s_logFolder), Files.HandlerLog), DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " pid=" + s_parentPid.ToString(CultureInfo.InvariantCulture) + " handler.shutdown.deadline ms=" + ms.ToString(CultureInfo.InvariantCulture) + Environment.NewLine, new UTF8Encoding(false)); }
                    catch { }
                    finally { Monitor.Exit(s_logLock); }
                }
                TerminateProcess(GetCurrentProcess(), 3);
            }) { IsBackground = true, Name = "crash-handler-deadline" }.Start();
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

        // ── 4. dump requests (parent-triggered: managed / native / test) ────────────────────────────────────────────

        /// <summary>A <c>D</c> line's kind, parsed (<see cref="DumpKinds"/>). <see cref="Unknown"/> is any token this
        /// build does not know.</summary>
        public enum DumpKind : byte { Unknown, Managed, Native, Test }

        /// <summary>The <c>D</c> line's kind tokens — one table for both processes (the parent writes
        /// <see cref="Token"/>, this child reads <see cref="Parse"/>) — and the one decision a kind changes here: whether
        /// the request latches <c>s_bundleWrittenThisProcess</c>, which suppresses the exit-code bundle when the parent
        /// later dies with a non-zero code. A managed/native request IS the crash, already bundled; a
        /// <see cref="DumpKind.Test"/> request is a live snapshot of a parent that keeps running (#165), so a real
        /// non-zero exit later in the same run must still get its bundle. An unknown token latches, as every request
        /// did before the kinds were told apart.</summary>
        public static class DumpKinds
        {
            public static string Token(DumpKind kind) => kind switch
            {
                DumpKind.Managed => "managed",
                DumpKind.Native => "native",
                DumpKind.Test => "test",
                _ => "unknown",
            };

            /// <summary>Ordinal and lower-case, exactly as <see cref="Token"/> writes it; anything else is
            /// <see cref="DumpKind.Unknown"/>.</summary>
            public static DumpKind Parse(string? token) => token switch
            {
                "managed" => DumpKind.Managed,
                "native" => DumpKind.Native,
                "test" => DumpKind.Test,
                _ => DumpKind.Unknown,
            };

            public static bool LatchesBundle(DumpKind kind) => kind != DumpKind.Test;
        }

        static void HandleDumpRequest(string line)
        {
            string[] parts = line.Split(' ', 5);
            if (parts.Length < 5) { WriteReply("ERR malformed request"); return; }
            string kindToken = parts[1];
            DumpKind kind = DumpKinds.Parse(kindToken);
            if (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint tid)) tid = 0;
            nint ptrs = 0;
            if (parts[3].Length > 0)
            {
                try { ptrs = unchecked((nint)Convert.ToUInt64(parts[3], 16)); }
                catch (Exception ex) when (ex is FormatException or OverflowException) { ptrs = 0; }
            }
            string dir = parts[4];

            if (DumpKinds.LatchesBundle(kind)) Interlocked.Exchange(ref s_bundleWrittenThisProcess, 1);
            // The order is the contract (header, "THE CHILD FINALIZES THE BUNDLE"): the fault is read FIRST, while the
            // parent is parked and its EXCEPTION_POINTERS is still a live frame; then the dump; then the one summary
            // rewrite and the report trailer; only then the reply that lets the parent go.
            FaultInfo fault = ptrs != 0 ? TryReadFault(ptrs) : default;
            bool ok = WriteMiniDump(dir, tid, ptrs, ptrs != 0);
            long bytes = ok ? DumpBytes(dir) : 0;
            try
            {
                if (!Bundles.UpdateSummary(dir, s => fault.Resolved
                        ? s with { HasDump = ok, DumpBytes = bytes, FaultModule = fault.Module, FaultOffset = fault.Offset }
                        : s with { HasDump = ok, DumpBytes = bytes }))
                    HandlerLog("summary.update.failed kind=" + kindToken);
                if (fault.Resolved && fault.Module.Length > 0)
                    Bundles.AppendReportLine(dir, "fault=" + fault.Module + "+0x" + fault.Offset.ToString("x", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { HandlerLog("summary.update.exception " + ex.Message); }   // the reply below must still go out
            if (ok)
            {
                HandlerLog("dump.written kind=" + kindToken + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture));
                WriteReply("OK " + bytes.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                HandlerLog("dump.failed kind=" + kindToken);
                WriteReply("ERR MiniDumpWriteDump failed");
            }
        }

        /// <summary>What <see cref="TryReadFault"/> learned: <see cref="Resolved"/> is true once the exception address
        /// fell inside one of the parent's loaded modules (<see cref="Module"/> may still be "" when that module's name
        /// fails <see cref="FaultModule.Normalize"/>; <see cref="Offset"/> is then still the offset inside it).</summary>
        readonly record struct FaultInfo(bool Resolved, nint Address, string Module, long Offset);

        /// <summary>Resolves the faulting module of the parent's native fault OUT OF PROCESS — nothing here runs in the
        /// crashing process, so nothing takes its loader lock or touches its heap. Same-bitness by construction (the
        /// child is the parent's own exe), so the parent's EXCEPTION_POINTERS / EXCEPTION_RECORD layouts are this
        /// process's. Needs PROCESS_VM_READ (ReadProcessMemory) + PROCESS_QUERY_INFORMATION (the K32 module calls),
        /// both of which <see cref="Run"/> opened the parent with. Never throws; every failed step is one handler.log
        /// line and an unresolved answer.</summary>
        static unsafe FaultInfo TryReadFault(nint ptrs)
        {
            try
            {
                EXCEPTION_POINTERS pointers = default;
                if (!ReadProcessMemory(s_parentHandle, ptrs, &pointers, (nuint)sizeof(EXCEPTION_POINTERS), out nuint got)
                    || got != (nuint)sizeof(EXCEPTION_POINTERS) || pointers.ExceptionRecord == 0)
                    return FaultReadFailed("pointers");

                EXCEPTION_RECORD_HEAD head = default;
                if (!ReadProcessMemory(s_parentHandle, pointers.ExceptionRecord, &head, (nuint)sizeof(EXCEPTION_RECORD_HEAD), out got)
                    || got != (nuint)sizeof(EXCEPTION_RECORD_HEAD))
                    return FaultReadFailed("record");
                nint address = head.ExceptionAddress;

                if (!TryListModules(out nint[] handles, out int count)) return FaultReadFailed("modules", address);
                var ranges = new (nint Base, uint Size)[count];
                for (int i = 0; i < count; i++)
                {
                    MODULEINFO info = default;
                    if (K32GetModuleInformation(s_parentHandle, handles[i], &info, (uint)sizeof(MODULEINFO)))
                        ranges[i] = (info.BaseOfDll, info.SizeOfImage);
                }
                int at = FaultModule.Find(ranges, address);
                if (at < 0)
                {
                    HandlerLog("fault.unresolved address=0x" + unchecked((ulong)address).ToString("x", CultureInfo.InvariantCulture)
                        + " modules=" + count.ToString(CultureInfo.InvariantCulture));
                    return new FaultInfo(false, address, "", 0);
                }

                const int NameChars = 260;   // MAX_PATH; a base name never approaches it
                char* name = stackalloc char[NameChars];
                uint len = K32GetModuleBaseNameW(s_parentHandle, handles[at], name, NameChars);
                string raw = len > 0 ? new string(name, 0, (int)Math.Min(len, (uint)NameChars)) : "";
                string module = FaultModule.Normalize(raw);
                long offset = (long)(address - ranges[at].Base);
                HandlerLog("fault.resolved module=" + (module.Length > 0 ? module : "?") + " offset=0x" + offset.ToString("x", CultureInfo.InvariantCulture));
                return new FaultInfo(true, address, module, offset);
            }
            catch (Exception ex)
            {
                HandlerLog("fault.read.exception " + ex.Message);
                return default;
            }
        }

        static FaultInfo FaultReadFailed(string step, nint address = 0)
        {
            HandlerLog("fault.read.failed step=" + step + " win32=0x" + Marshal.GetLastPInvokeError().ToString("x8", CultureInfo.InvariantCulture));
            return new FaultInfo(false, address, "", 0);
        }

        /// <summary>Every module handle the parent has loaded (<c>LIST_MODULES_ALL</c>), growing the buffer once if the
        /// first answer says it was too small (a DLL loaded between the two calls is the only way the retry loses).</summary>
        static unsafe bool TryListModules(out nint[] handles, out int count)
        {
            handles = new nint[512];
            count = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool ok;
                uint needed;
                fixed (nint* h = handles)
                    ok = K32EnumProcessModulesEx(s_parentHandle, h, (uint)(handles.Length * sizeof(nint)), out needed, ListModulesAll);
                if (!ok) return false;
                int n = (int)(needed / (uint)sizeof(nint));
                if (n <= handles.Length) { count = n; return true; }
                handles = new nint[n + 64];
            }
            return false;
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
                    // ERROR_NOACCESS (0x800703e6) seen 2026-09-25 with ClientPointers over the parent's VEH pointers — root
                    // cause: MINIDUMP_EXCEPTION_INFORMATION lacked Pack = 4 (fixed, #165). Kept as a net: retry ONCE without
                    // the exception stream. The faulting thread is still parked inside the hook, so its stack (fault frames
                    // included) is in the dump either way; only the exception record is lost.
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
                Bundles.WriteTail(dir, Bundles.ResolveTailPath(s_logBasePath), 300, Feedback.RedactionRules.None);
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
                    Bundles.WriteTail(dir, Bundles.ResolveTailPath(s_logBasePath), 300, Feedback.RedactionRules.None);
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
        const uint ListModulesAll = 0x03;   // LIST_MODULES_ALL (K32EnumProcessModulesEx)

        /// <summary>Pack = 4 because minidumpapiset.h wraps this struct in <c>pshpack4.h</c>: on 64-bit the native
        /// layout is ThreadId@0, ExceptionPointers@4, ClientPointers@12, size 16. Plain Sequential put the pointer at 8
        /// (size 24), so dbghelp read a "pointer" made of the 4 padding bytes after ThreadId plus the low half of the real
        /// one — every <c>ClientPointers</c> dump failed with ERROR_NOACCESS and the retry wrote one without the exception
        /// stream (#165). <see cref="ExceptionInfoLayout"/> pins the layout in a test.</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct MINIDUMP_EXCEPTION_INFORMATION
        {
            public uint ThreadId;
            public nint ExceptionPointers;
            public int ClientPointers;   // Win32 BOOL, blittable as int — this struct's address is taken directly, no marshaler involved
        }

        /// <summary>A native struct's measured layout, as <see cref="ExceptionInfoLayout"/> reports it.</summary>
        public readonly record struct NativeLayout(int Size, int ExceptionPointersOffset, int ClientPointersOffset);

        /// <summary>The managed declaration of <c>MINIDUMP_EXCEPTION_INFORMATION</c> as the CPU sees it — size and field
        /// offsets taken by pointer arithmetic over a live instance (no reflection, AOT-safe). Public only so
        /// <c>Wavee.Tests</c> (no InternalsVisibleTo) can pin it to the SDK's pshpack4 layout: 16 / 4 / 12 on 64-bit.</summary>
        public static unsafe NativeLayout ExceptionInfoLayout()
        {
            MINIDUMP_EXCEPTION_INFORMATION probe = default;
            byte* at = (byte*)&probe;
            return new NativeLayout(sizeof(MINIDUMP_EXCEPTION_INFORMATION),
                (int)((byte*)&probe.ExceptionPointers - at), (int)((byte*)&probe.ClientPointers - at));
        }

        /// <summary>The parent's EXCEPTION_POINTERS, read over ReadProcessMemory (same bitness as this process).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct EXCEPTION_POINTERS
        {
            public nint ExceptionRecord;
            public nint ContextRecord;
        }

        /// <summary>The head of the parent's EXCEPTION_RECORD — only up to ExceptionAddress; the parameters after it are
        /// never read.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct EXCEPTION_RECORD_HEAD
        {
            public uint ExceptionCode;
            public uint ExceptionFlags;
            public nint ExceptionRecordNext;
            public nint ExceptionAddress;
        }

        /// <summary>psapi's MODULEINFO (natural packing).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct MODULEINFO
        {
            public nint BaseOfDll;
            public uint SizeOfImage;
            public nint EntryPoint;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, void* lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool K32EnumProcessModulesEx(nint hProcess, nint* lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool K32GetModuleInformation(nint hProcess, nint hModule, MODULEINFO* lpmodinfo, uint cb);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static unsafe partial uint K32GetModuleBaseNameW(nint hProcess, nint hModule, char* lpBaseName, uint nSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint hObject);

        [LibraryImport("kernel32.dll")]
        private static partial nint GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TerminateProcess(nint hProcess, uint uExitCode);

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
