// ── Platform/Crash.Launch.cs ───────────────────────────────────────────────────────────────────────────────────────
// How Wavee starts, tracks and bounds its HELPER child processes (the crash handler above all): the environment those
// children get, the kernel job that guarantees they cannot outlive a handler that never came up, and the ready
// handshake + watchdog that decides when a handler counts as up. Engine-free and Log-free (callers pass a log sink) so
// `Wavee.Tests` can spawn the real handler through exactly this code.
//
// WHY (0.3.4): a Wavee launched under a .NET tracing tool carries DOTNET_DiagnosticPorts=<port>,connect,suspend. The
// crash-handler child inherited it, its runtime suspended BEFORE Main waiting for a diagnostics client that never
// came, and it stayed alive for 11+ hours after its parent died. Two independent fixes live here:
//   1. the environment scrub (`ScrubEnvironment`): no helper inherits a suspend/port/profiler variable;
//   2. kernel-guaranteed lifetime (`KillOnCloseJob` + the `R` handshake): a child that never says ready holds no job
//      handle, so the kernel kills it when Wavee dies however Wavee dies; a child that did say ready gets its OWN
//      duplicate of the job handle, so it outlives Wavee exactly as long as it needs to write a bundle.
//
// THE `R` LINE (stdout, child → parent; the one non-reply line): the handler writes `R` once it has opened its parent
// handle. `ReplyPump` swallows it (sets the ready flag); every other line is a `D` reply, queued in order for
// `Crash.Host.RequestDumpFromChild`.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wavee;

public static partial class Crash
{
    public static partial class Launch
    {
        // ── 1. the environment scrub ────────────────────────────────────────────────────────────────────────────────

        /// <summary>How hard <see cref="ScrubEnvironment"/> works. <see cref="Helper"/>: the child is Wavee's own helper
        /// (crash handler) — strip the variables AND turn runtime diagnostics off. <see cref="Strip"/>: only strip the
        /// variables that make a runtime wait for a tool at startup or load a profiler; diagnostics stay at whatever
        /// the child's own environment says. Used for playback modules (a third-party author may legitimately attach
        /// a tracer to their own module; they just never inherit OUR tool's suspend port) and for the restart broker
        /// (it hands its environment on to the relaunched Wavee, which must not inherit `EnableDiagnostics=0`).</summary>
        public enum EnvScrub { Helper, Strip }

        /// <summary>Removes the inherited runtime diagnostics / event-pipe / profiler variables (both the
        /// <c>DOTNET_</c> and the legacy <c>COMPlus_</c> spellings, case-insensitively) from <paramref name="env"/>;
        /// in <see cref="EnvScrub.Helper"/> mode also sets <c>DOTNET_EnableDiagnostics=0</c>. Pure.</summary>
        public static void ScrubEnvironment(IDictionary<string, string?> env, EnvScrub mode)
        {
            ArgumentNullException.ThrowIfNull(env);
            var doomed = new List<string>();
            foreach (string key in env.Keys)
                if (IsInheritedDiagnosticVar(key, mode == EnvScrub.Helper)) doomed.Add(key);
            foreach (string key in doomed) env.Remove(key);
            if (mode == EnvScrub.Helper) env["DOTNET_EnableDiagnostics"] = "0";
        }

        /// <summary>True for a variable <see cref="ScrubEnvironment"/> removes. <paramref name="includeEnableDiagnostics"/>
        /// adds the <c>EnableDiagnostics*</c> family (Helper mode sets its own value afterwards).</summary>
        public static bool IsInheritedDiagnosticVar(string name, bool includeEnableDiagnostics)
        {
            string rest;
            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)) rest = name[7..];
            else if (name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)) rest = name[8..];
            else
            {
                // The profiler switches are also read without a prefix (CORECLR_*), plus the .NET Framework COR_* pair.
                return name.StartsWith("CORECLR_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("CORECLR_PROFILER", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("CORECLR_NEWPROFILER", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("COR_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("COR_PROFILER", StringComparison.OrdinalIgnoreCase);
            }
            if (rest.Equals("DiagnosticPorts", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.Equals("DefaultDiagnosticPortSuspend", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.StartsWith("EnableEventPipe", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.StartsWith("EventPipe", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.StartsWith("EnableDiagnostics", StringComparison.OrdinalIgnoreCase)) return includeEnableDiagnostics;
            return false;
        }

        // ── 2. the stdout handshake ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>True for the handler's ready line (<c>R</c>), the only stdout line that is not a <c>D</c> reply.</summary>
        public static bool IsReadyLine(string? line) => line is "R";

        /// <summary>The handler's start info: this exe as <c>--crash-handler &lt;parentPid&gt; &lt;logFolder&gt;
        /// &lt;logBasePath|-&gt;</c>, redirected stdin/stdout, environment scrubbed (<see cref="EnvScrub.Helper"/>).
        /// <paramref name="leadArg"/> goes first (a <c>Wavee.dll</c> path when the host is <c>dotnet</c>; tests).</summary>
        public static ProcessStartInfo CreateHandlerStartInfo(string exe, string? leadArg, int parentPid, string logFolder, string? logBasePath)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            if (leadArg is { Length: > 0 }) psi.ArgumentList.Add(leadArg);
            psi.ArgumentList.Add("--crash-handler");
            psi.ArgumentList.Add(parentPid.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(logFolder);
            // The BASE path (wavee.log), not today's dated file: the child outlives midnight and derives the day's file
            // at bundle time (Crash.Bundles.ResolveTailPath). A dated path frozen here tailed yesterday after midnight.
            psi.ArgumentList.Add(string.IsNullOrEmpty(logBasePath) ? "-" : logBasePath);
            ScrubEnvironment(psi.Environment, EnvScrub.Helper);
            return psi;
        }

        /// <summary>Reads a child's stdout on a background thread: the ready line sets <see cref="WaitReady"/>, every
        /// other line is queued in order for <see cref="TryTakeReply"/>. End of stream (or a read failure) is queued as
        /// a null reply and completes the ready wait as false.</summary>
        public sealed class ReplyPump
        {
            const string Eof = "\0eof";
            readonly BlockingCollection<string> _replies = new();
            readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

            readonly Action? _onReady;

            /// <param name="onReady">Runs ON THE PUMP THREAD the moment the ready line is read, before
            /// <see cref="WaitReady"/> completes: the job-handle hand-over happens here, microseconds after R, with no
            /// thread-pool dependency.</param>
            public ReplyPump(TextReader reader, Action? onReady = null)
            {
                _onReady = onReady;
                new Thread(() => Pump(reader), 64 * 1024) { IsBackground = true, Name = "crash-handler-stdout" }.Start();
            }

            void Pump(TextReader reader)
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (IsReadyLine(line))
                        {
                            try { _onReady?.Invoke(); } catch { }
                            _ready.TrySetResult(true);
                        }
                        else _replies.Add(line);
                    }
                }
                catch { }
                _ready.TrySetResult(false);
                _replies.Add(Eof);
            }

            /// <summary>True once the ready line arrived; false on timeout or when the stream ended first.</summary>
            public bool WaitReady(int timeoutMs)
            {
                try { return _ready.Task.Wait(timeoutMs) && _ready.Task.Result; }
                catch { return false; }
            }

            /// <summary>The next reply line, in arrival order. False on timeout; true with a null line at end of stream
            /// (the end marker is re-queued so every later caller sees it too).</summary>
            public bool TryTakeReply(int timeoutMs, out string? line)
            {
                line = null;
                if (!_replies.TryTake(out string? s, timeoutMs)) return false;
                if (ReferenceEquals(s, Eof) || s == Eof) { _replies.Add(Eof); return true; }
                line = s;
                return true;
            }
        }

        // ── 3. one handler child: process + stdin + replies + the kill-on-close job ─────────────────────────────────

        /// <summary>A started helper child. Its job (kill-on-close) is assigned at start and held by THIS object for as
        /// long as the parent lives; <see cref="AwaitReady"/> hands the child its own duplicate once it has said ready.</summary>
        public sealed class HandlerChild
        {
            readonly KillOnCloseJob? _job;
            string _jobState = "pending";

            HandlerChild(Process process, StreamWriter stdin, KillOnCloseJob? job)
            {
                Process = process; Stdin = stdin; _job = job; Replies = new ReplyPump(process.StandardOutput, OnReady);
            }

            /// <summary>Runs on the pump thread when R arrives: give the child its own job handle; if that fails, clear
            /// kill-on-close so a ready handler can never be killed with this process (it would lose every exit bundle).</summary>
            void OnReady()
            {
                string state = "unbound";
                try
                {
                    if (_job is { Assigned: true })
                        state = _job.DuplicateInto(Process.Handle) ? "shared" : _job.ClearKillOnClose() ? "released" : "unbound";
                }
                catch { try { if (_job?.ClearKillOnClose() == true) state = "released"; } catch { } }
                Volatile.Write(ref _jobState, state);
            }

            public Process Process { get; }
            public StreamWriter Stdin { get; }
            public ReplyPump Replies { get; }
            public int Id { get { try { return Process.Id; } catch { return 0; } } }
            public bool JobAssigned => _job?.Assigned == true;
            /// <summary>What became of the job when the child said ready: <c>shared</c> (the child holds its own handle),
            /// <c>released</c> (the duplicate failed, so kill-on-close was cleared: the child outlives this process anyway),
            /// <c>unbound</c> (it was never in a job), or <c>pending</c> before R.</summary>
            public string JobState => Volatile.Read(ref _jobState);
            public bool JobShared => JobState == "shared";

            /// <summary>Counts the replies still owed to timed-out requests (guarded by Crash.Host's dump gate).</summary>
            public int StaleReplies { get; set; }
            public bool HasExited { get { try { return Process.HasExited; } catch { return true; } } }

            /// <summary>Starts <paramref name="psi"/> (stdin and stdout MUST be redirected) and binds it to a fresh
            /// kill-on-close job. A refused job leaves the child unbound (still watched by the watchdog). Null when the
            /// process could not start.</summary>
            public static HandlerChild? Start(ProcessStartInfo psi)
            {
                Process? p = Process.Start(psi);
                if (p is null) return null;
                KillOnCloseJob? job = null;
                try
                {
                    job = KillOnCloseJob.Create();
                    if (job is not null && !job.Assign(p.Handle)) { /* stays unbound: Assigned is false */ }
                }
                catch { }
                var stdin = p.StandardInput;
                stdin.AutoFlush = true;
                return new HandlerChild(p, stdin, job);
            }

            /// <summary>Waits up to <paramref name="timeoutMs"/> for the ready line (the job hand-over already happened on the
            /// pump thread by then; see <see cref="JobState"/>). False when no ready line came: the child then holds no
            /// job handle and dies with this process.</summary>
            public bool AwaitReady(int timeoutMs) => Replies.WaitReady(timeoutMs);

            /// <summary>Kills the child and closes this side's job handle. Never throws.</summary>
            public void Kill()
            {
                try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
                try { _job?.Dispose(); } catch { }
            }

            /// <summary>Closes this side's job handle without killing anything directly: what the kernel does when the
            /// owning process dies. A child that never got its own duplicate dies with it; a ready one lives on.
            /// (Tests use it to stand in for "Wavee died".)</summary>
            public void ReleaseJob() { try { _job?.Dispose(); } catch { } }
        }

        // ── 4. the watchdog policy ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>No ready line within this long and the parent gives the child up.</summary>
        public const int ReadyTimeoutMs = 10_000;

        /// <summary>Waits for <paramref name="first"/> to become ready; if it does not, kills it and retries ONCE with
        /// <paramref name="respawn"/>; if that fails too, gives up (the app runs without a handler). Blocking — callers
        /// run it on a background task, never on the startup path. <paramref name="abandon"/> is told about every child
        /// being dropped so the owner can stop writing to it.</summary>
        public static HandlerChild? Supervise(HandlerChild first, Func<HandlerChild?> respawn, Action<HandlerChild> adopt,
            Action<HandlerChild> abandon, Action<string> log, int readyMs = ReadyTimeoutMs)
        {
            HandlerChild? cur = first;
            for (int attempt = 0; attempt < 2 && cur is not null; attempt++)
            {
                if (cur.AwaitReady(readyMs))
                {
                    log("crash.handler.ready pid=" + cur.Id.ToString(CultureInfo.InvariantCulture) + " job=" + cur.JobState);
                    return cur;
                }
                log("crash.handler.unresponsive pid=" + cur.Id.ToString(CultureInfo.InvariantCulture)
                    + " exited=" + (cur.HasExited ? "true" : "false") + " attempt=" + (attempt + 1).ToString(CultureInfo.InvariantCulture));
                abandon(cur);
                cur.Kill();
                if (attempt == 1) break;
                cur = respawn();
                if (cur is not null) adopt(cur);
            }
            log("crash.handler.unavailable: continuing without a crash handler");
            return null;
        }

        // ── 5. the job object (hand-declared kernel32; same shape as the engine's ChildProcessJob, which cannot hand out
        //      its handle for DuplicateHandle) ──────────────────────────────────────────────────────────────────────

        /// <summary>A job with <c>KILL_ON_JOB_CLOSE</c>: every process assigned to it dies when the last handle closes.
        /// App-local copy of the engine's <c>FluentGpu.WindowsApi.Shell.ChildProcessJob</c> (which cannot hand out its
        /// handle); fold <c>DuplicateInto</c>/<c>ClearKillOnClose</c> into that class later and delete this one.</summary>
        public sealed partial class KillOnCloseJob : IDisposable
        {
            nint _handle;
            KillOnCloseJob(nint handle) => _handle = handle;

            public bool Assigned { get; private set; }

            public static KillOnCloseJob? Create()
            {
                nint job = CreateJobObjectW(0, null);
                if (job == 0) return null;
                var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                unsafe
                {
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info,
                            (uint)Unsafe.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    {
                        CloseHandle(job);
                        return null;
                    }
                }
                return new KillOnCloseJob(job);
            }

            public bool Assign(nint processHandle)
            {
                if (_handle == 0 || processHandle == 0) return false;
                Assigned = AssignProcessToJobObject(_handle, processHandle);
                return Assigned;
            }

            /// <summary>Clears KILL_ON_JOB_CLOSE: the processes in this job survive its last handle closing.</summary>
            public unsafe bool ClearKillOnClose()
            {
                if (_handle == 0) return false;
                var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
                return SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, &info, (uint)Unsafe.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }

            /// <summary>Gives <paramref name="targetProcess"/> (needs PROCESS_DUP_HANDLE) its own handle to this job.</summary>
            public bool DuplicateInto(nint targetProcess)
            {
                if (_handle == 0 || targetProcess == 0) return false;
                return DuplicateHandle(GetCurrentProcess(), _handle, targetProcess, out _, JobObjectQuery, false, 0);
            }

            public void Dispose()
            {
                nint h = Interlocked.Exchange(ref _handle, 0);
                if (h != 0) CloseHandle(h);
            }

            const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
            const int JobObjectExtendedLimitInformation = 9;
            const uint JobObjectQuery = 0x4;   // any access keeps the job alive; the child needs none

            [StructLayout(LayoutKind.Sequential)]
            struct IO_COUNTERS
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public nuint MinimumWorkingSetSize;
                public nuint MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public nuint Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public nuint ProcessMemoryLimit;
                public nuint JobMemoryLimit;
                public nuint PeakProcessMemoryUsed;
                public nuint PeakJobMemoryUsed;
            }

            [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
            private static partial nint CreateJobObjectW(nint securityAttributes, string? name);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool AssignProcessToJobObject(nint job, nint process);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool CloseHandle(nint handle);

            [LibraryImport("kernel32.dll")]
            private static partial nint GetCurrentProcess();

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle,
                uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
        }
    }
}
