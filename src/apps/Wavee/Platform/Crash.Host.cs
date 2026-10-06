// ── Platform/Crash.Host.cs ─────────────────────────────────────────────────────────────────────────────────────────
// The out-of-process crash handler's PARENT arm (§B.1, §B.7, §I): spawns the child (`Wavee.exe --crash-handler`,
// §B.0/B.1), wires the managed and native fault hooks to it, posts a 5 s heartbeat through the UI marshaller so the
// child's watchdog can tell a live pump from a hung one, and is the ONE door every other package (WP-C's uploader,
// WP-D's recovery dialog, WP-E's chrome) reads bundles and this launch's `ThisLaunch` through.
//
// Role: SHELL (spawns a process, owns a pipe, may touch the engine — `FluentGpu.Signals.Signal<T>`, `Playback.ToUi`)
// Owner: WP-B
// Wave: crash-diagnostics
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.1, §B.7, §B.8 (BeginGuiRun's entry rule), §I (every
//       member here is named there)
//
// THE STDIN PROTOCOL THIS FILE WRITES (read by `Crash.Handler.cs` — see its header for the full grammar): every send
// goes through `SendLine`, which is the ONLY place that touches the child's `StreamWriter`, under one lock — writes
// can arrive from the beat timer (already posted through the UI thread), `NoteFirstFrame`/`NoteExiting`/`ModalScope`
// (also UI thread, by the call sites §I names) AND a `PowerSession` callback, which is NOT guaranteed to be the UI
// thread, so the lock is load-bearing, not decorative.
//
// THE STDOUT SIDE: the child's first line is `R` (ready — see `Crash.Handler.cs`'s header). `Crash.Launch.ReplyPump`
// reads the child's stdout on one background thread, turns `R` into the ready flag the watchdog waits on and queues
// every other line (the D replies) in order for `RequestDumpFromChild`. Lifetime: `Crash.Launch`'s header.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Power;

namespace Wavee;

public static partial class Crash
{
    public static partial class Host
    {
        static string? s_logFolder;

        /// <summary>Falls back to <c>Platform.LogFolder</c> when <see cref="Install"/> was never called (headless, the
        /// relaunch broker, or a crash raised before <c>Shell.Run</c> gets to it) — a managed crash in one of those
        /// still has a real folder to write its bundle into, never a relative "crash\..." off the working directory.</summary>
        public static string LogFolder => s_logFolder ?? Platform.LogFolder;

        static int s_installed;
        static Launch.HandlerChild? s_child;
        static StreamWriter? s_stdin;
        static readonly object s_writeLock = new();
        static System.Threading.Timer? s_beatTimer;
        // The child declares a hang after HangRules.NoBeatMs (20 s) without a beat, so 5 s still leaves four beats of margin
        // while cutting the idle thread-pool + UI-post wakeups from 0.5 Hz x2 to 0.2 Hz x2.
        const int BeatMs = 5000;
        static string? s_logBasePath;

        static int s_managedHandled, s_nativeHandled;
        static int s_hangReportedForThisProcess;
        static long s_handlerLogLastLen;
        static long s_lastHangPollTickMs;
        static int s_suspendEpoch;
        static int s_modalDepth;
        static string? s_installLine;   // "I <id>", cached by the first (main-thread) Adopt

        /// <summary>Set by <see cref="NoteFirstFrame"/>; read by <see cref="Report.BuildSummary"/> for
        /// <c>Summary.BeforeFirstFrame</c>. Internal: only this file and <see cref="Report"/> need it.</summary>
        internal static bool FirstFrameSeen { get; private set; }

        public static readonly Signal<int> ReportsVersion = new(0);
        public static readonly Signal<int> HangReported = new(0);

        /// <summary>Latched once by <see cref="BeginGuiRun"/>; WP-E's chrome consumes it once (its own effect resets
        /// nothing here — the field stays until the next <see cref="BeginGuiRun"/>, which only ever runs once per
        /// launch).</summary>
        public static BundleInfo? ThisLaunch;

        // ── 1. install: spawn the child, wire the fault hooks, start the heartbeat ─────────────────────────────────

        /// <summary>Called once from <c>Shell.Run</c>, right after <c>InstallCrashNet</c>, never for
        /// <c>--headless</c>/the relaunch broker. Spawning failure is logged and otherwise inert — a launch with no
        /// crash handler still runs; it just cannot capture a native fault or a hang, and a managed one falls back to
        /// "no bundle, only the app log's own crash line" (<c>Shell.Host.InstallCrashNet</c>'s belt).</summary>
        public static void Install(string logFolder, string? logBasePath)
        {
            if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
            s_logFolder = logFolder;
            s_logBasePath = logBasePath;
            try { Directory.CreateDirectory(Files.Root(logFolder)); } catch { }

            SpawnChild(logFolder, logBasePath);

            NativeHook.OnForeignFault = static (code, ptrs) => RequestDumpCore(Kind.Native, ptrs, code);
            NativeHook.Install();

            try
            {
                PowerSession.Suspending += OnSuspending;
                PowerSession.Resumed += OnResumed;
            }
            catch (Exception ex) { Log.Warn("crash", "crash.power.subscribe.failed", ex); }

            s_beatTimer = new System.Threading.Timer(OnBeatTick, null, BeatMs, BeatMs);
        }

        /// <summary>Starts the first handler synchronously (the caller needs <c>s_stdin</c> for the lines that follow) and
        /// hands it to a background watchdog (<see cref="Launch.Supervise"/>): no ready line within 10 s and the child is
        /// killed and respawned once, then given up. Startup never waits on it. The child is bound to a kill-on-close
        /// job held by THIS process for its whole life; it gets its own duplicate of the job handle only once it says
        /// ready (header of <c>Crash.Launch.cs</c>), so a handler that never came up cannot outlive Wavee — while a
        /// healthy one still outlives a parent that just faulted, long enough to finish a dump (§B.1).</summary>
        static void SpawnChild(string logFolder, string? logBasePath)
        {
            var first = StartHandler(logFolder, logBasePath);
            if (first is null) return;
            Adopt(first);
            _ = Task.Run(() =>
            {
                try
                {
                    Launch.Supervise(first, () => StartHandler(logFolder, logBasePath), Adopt, Abandon,
                        static msg =>
                        {
                            if (msg.StartsWith("crash.handler.ready", StringComparison.Ordinal))
                                Log.Event(WaveeLogLevel.Info, "crash", "crash.handler.ready", msg, null, -1, null);
                            else Log.Warn("crash", msg);
                        });
                }
                catch (Exception ex) { Log.Warn("crash", "crash.handler.supervise.failed", ex); }
            });
        }

        static Launch.HandlerChild? StartHandler(string logFolder, string? logBasePath)
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) { Log.Warn("crash", "crash.handler.spawn.failed: ProcessPath is empty"); return null; }
                var psi = Launch.CreateHandlerStartInfo(exe, null, Environment.ProcessId, logFolder, logBasePath);
                var child = Launch.HandlerChild.Start(psi);
                if (child is null) { Log.Warn("crash", "crash.handler.spawn.failed: Process.Start returned null"); return null; }
                Log.Event(WaveeLogLevel.Info, "crash", "crash.handler.spawned", "", null, -1, null,
                    WaveeLogField.Of("pid", child.Id), WaveeLogField.Of("job", child.JobAssigned));
                return child;
            }
            catch (Exception ex) { Log.Warn("crash", "crash.handler.spawn.failed", ex); return null; }
        }

        /// <summary>Makes <paramref name="child"/> the live handler: every later line goes to it. The install id rides
        /// over first (the child's own bundles — exit code, hang, forced close — must carry it for the Worker to accept
        /// them), and a respawn after the first frame replays <c>F</c> so its hang watchdog arms.</summary>
        static void Adopt(Launch.HandlerChild child)
        {
            lock (s_writeLock)
            {
                s_child = child;
                s_stdin = child.Stdin;
                // First call is on the main thread (Install), which may write the id; a respawn runs on a pool thread and
                // replays the cached string (settings writes are UI-thread-only). Then the state a fresh handler missed.
                try { s_installLine ??= "I " + InstallId.Ensure(Platform.Settings); s_stdin.WriteLine(s_installLine); } catch { }
                try
                {
                    if (FirstFrameSeen) s_stdin.WriteLine("F");
                    int modal = Volatile.Read(ref s_modalDepth);
                    for (int i = 0; i < modal; i++) s_stdin.WriteLine("M1");
                    int epoch = Volatile.Read(ref s_suspendEpoch);
                    if (epoch > 0) s_stdin.WriteLine("S " + epoch.ToString(CultureInfo.InvariantCulture));
                }
                catch { }
            }
        }

        static void Abandon(Launch.HandlerChild child)
        {
            lock (s_writeLock)
            {
                if (!ReferenceEquals(s_child, child)) return;
                s_child = null;
                s_stdin = null;
            }
        }

        /// <summary>Not wired into the exit tail by this work package (nothing in §I calls it) — provided so a later
        /// caller has a clean stop, and so the beat timer's field is read at least once outside <see cref="Install"/>
        /// (the timer itself must be kept live in a field or the GC could collect it out from under its own
        /// callback).</summary>
        public static void Shutdown()
        {
            try { s_beatTimer?.Dispose(); } catch { }
            s_beatTimer = null;
            try { PowerSession.Suspending -= OnSuspending; PowerSession.Resumed -= OnResumed; } catch { }
        }

        static void SendLine(string line)
        {
            lock (s_writeLock)
            {
                try { s_stdin?.WriteLine(line); }
                catch (Exception ex) { Log.Warn("crash", "crash.handler.write.failed", ex); }
            }
        }

        // ── 2. the heartbeat + the hang-report poll ─────────────────────────────────────────────────────────────────

        /// <summary>Posts through the SAME seam <c>Platform.Network.Install(Playback.ToUi)</c> uses: a beat is only
        /// ever WRITTEN when the posted action actually runs on the UI thread, which is the whole point — a hung UI
        /// thread lets the posted action sit in the queue forever, so no beat reaches the child and its watchdog
        /// starts counting. Before the root attaches the real poster the post queues (`Shell.Host.cs`'s early-post
        /// ring) rather than being dropped, which is harmless: the child also needs a window before it can call
        /// <c>IsHungAppWindow</c>, so no beat before first frame costs nothing.</summary>
        static void OnBeatTick(object? _)
        {
            Playback.ToUi(static () => SendLine("B"));
            long now = Environment.TickCount64;
            if (now - s_lastHangPollTickMs >= BeatMs - 500)
            {
                s_lastHangPollTickMs = now;
                PollHandlerLogForHang();
            }
        }

        /// <summary>Cheap growth-only check on <c>handler.log</c>'s length, then a read of only the NEW bytes — never
        /// re-scans the whole file. Bumps <see cref="HangReported"/> once, the first time a <c>hang.dump.written</c>
        /// line for THIS process's pid appears.</summary>
        static void PollHandlerLogForHang()
        {
            if (Interlocked.CompareExchange(ref s_hangReportedForThisProcess, 0, 0) != 0) return;
            try
            {
                string path = Path.Combine(Files.Root(LogFolder), Files.HandlerLog);
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                long len = fi.Length;
                if (len <= s_handlerLogLastLen) { s_handlerLogLastLen = len; return; }

                string myPid = "pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
                bool found = false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(s_handlerLogLastLen, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                        if (line.Contains("hang.dump.written", StringComparison.Ordinal) && line.Contains(myPid, StringComparison.Ordinal))
                            found = true;
                }
                s_handlerLogLastLen = len;
                if (found)
                {
                    Interlocked.Exchange(ref s_hangReportedForThisProcess, 1);
                    Playback.ToUi(static () => HangReported.Value++);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // ── 3. managed + native crash capture ───────────────────────────────────────────────────────────────────────

        /// <summary>Called FIRST by <c>Platform.Host.HostInstallCrashWriters</c>, on whichever thread raised the
        /// unhandled exception. Idempotent per process (both the app-loop catch and <c>AppDomain.UnhandledException</c>
        /// can reach this for the same crash).</summary>
        public static void OnManagedCrash(Exception ex)
        {
            if (Interlocked.Exchange(ref s_managedHandled, 1) != 0) return;
            try
            {
                // The crash's own line goes in FIRST and the queue is drained BEFORE the tail is read: this runs ahead of
                // Shell's Critical line (the handlers fire in subscription order) and the sink writes on a pool thread, so
                // without both the tail ended before the crash and missed the last <= 512-line batch. (The native path
                // flushes in NativeHook.) Type and message only: Shell logs the full exception right after.
                try { Log.Event(WaveeLogLevel.Critical, "crash", "crash.managed", ex.GetType().FullName + ": " + ex.Message); } catch { }
                try { RoutineSummary.FlushAll(force: true); } catch { }   // the open window's partial summary belongs in the crash tail
                Log.Flush();
                string dir = Crash.Bundles.Create(LogFolder, Kind.Managed, DateTimeOffset.Now);
                var summary = Report.BuildSummary(Kind.Managed, ex, 0);
                Crash.Bundles.WriteSummary(dir, summary);
                Crash.Bundles.WriteReport(dir, Report.Describe(ex));
                WriteLogTail(dir, SafeGatherRules());
                // NO settings write and NO signal write here: this runs on the CRASHING thread (a pool thread for a worker
                // fault), and Settings.Set / Signal writes are UI-thread-only — measured 2026-09-25: the write took the
                // process down with a fatal "invalid program" inside a SettingsChanged subscriber. A file marker instead.
                Crash.Bundles.MarkPending(LogFolder, dir);

                Log.Event(WaveeLogLevel.Critical, "crash", "crash.dump.requested", "", null, -1, null, WaveeLogField.Of("kind", "managed"));
                // The child finalizes summary.json (hasDump/dumpBytes) before it replies — no rewrite here (#165 W3a).
                if (RequestDumpFromChild(Handler.DumpKind.Managed, GetCurrentThreadId(), 0, dir, out long bytes, out string? err))
                {
                    Log.Event(WaveeLogLevel.Critical, "crash", "crash.dump.written", "", null, -1, null, WaveeLogField.Of("bytes", bytes));
                }
                else
                {
                    Log.Event(WaveeLogLevel.Critical, "crash", "crash.dump.failed", err ?? "", null, -1, null);
                }
                Crash.Bundles.PruneNow(LogFolder);
            }
            catch (Exception writeEx) { try { Log.Warn("crash", "crash.managed.bundle.failed", writeEx); } catch { } }
        }

        /// <summary>The bundle's <c>log-tail.txt</c>: the day's file AS OF NOW (<see cref="Crash.Bundles.ResolveTailPath"/> —
        /// never a path frozen at install, which names the boot day's file) plus the lines still queued for it. The queue
        /// snapshot is taken first, so a line the writer lands meanwhile is in both and deduped rather than in neither.
        /// Shared by the managed, native and test-report paths.</summary>
        static void WriteLogTail(string dir, Feedback.RedactionRules rules)
            => Crash.Bundles.WriteTail(dir, Crash.Bundles.ResolveTailPath(s_logBasePath ?? Log.BasePath), 300, rules, Log.PendingFileLines());

        /// <summary>The native-fault path's public entry (<c>Crash.NativeHook.OnForeignFault</c> — the FILTER, §B.0's
        /// spike — wires <see cref="RequestDumpCore"/> directly so it also has the fault code for the summary; this
        /// overload exists for the contract, §I, and for anything else that wants to request a native dump without
        /// one).</summary>
        public static void RequestDump(Kind kind, nint ptrs) => RequestDumpCore(kind, ptrs, 0);

        static void RequestDumpCore(Kind kind, nint ptrs, uint code)
        {
            if (kind == Kind.Native && Interlocked.Exchange(ref s_nativeHandled, 1) != 0) return;
            try
            {
                string dir = Crash.Bundles.Create(LogFolder, kind, DateTimeOffset.Now);
                // The frames were walked allocation-free inside the hook, before any of this ran
                // (NativeHook.CaptureFaultStack); FaultRvas only projects them onto Wavee.exe RVAs (#165 W3a).
                long[] rvas = NativeHook.FaultRvas();
                // "at" = the faulting instruction (EXCEPTION_RECORD.ExceptionAddress), never the EXCEPTION_POINTERS
                // address this line used to print.
                string headline = "native fault 0x" + code.ToString("x8", CultureInfo.InvariantCulture)
                    + " at 0x" + unchecked((ulong)NativeHook.FaultAddress).ToString("x", CultureInfo.InvariantCulture);
                var summary = Report.BuildSummary(kind, null, 0, exceptionType: "Native", exceptionMessage: headline,
                    rvas: rvas, exceptionCode: code);
                Crash.Bundles.WriteSummary(dir, summary);
                var sb = new StringBuilder(2048);
                Report.DescribeSynthetic(headline, sb);
                Report.AppendRvaSection(sb, rvas);
                Crash.Bundles.WriteReport(dir, sb.ToString());
                WriteLogTail(dir, SafeGatherRules());
                // NO settings write and NO signal write here: this runs on the CRASHING thread (a pool thread for a worker
                // fault), and Settings.Set / Signal writes are UI-thread-only — measured 2026-09-25: the write took the
                // process down with a fatal "invalid program" inside a SettingsChanged subscriber. A file marker instead.
                Crash.Bundles.MarkPending(LogFolder, dir);

                Log.Event(WaveeLogLevel.Critical, "crash", "crash.dump.requested", "", null, -1, null,
                    WaveeLogField.Of("kind", "native"), WaveeLogField.Of("code", "0x" + code.ToString("x8", CultureInfo.InvariantCulture)));
                // The child resolves the faulting module (this thread stays parked meanwhile, so the EXCEPTION_POINTERS
                // it reads is still live), writes the dump and finalizes summary.json + report.txt before it replies —
                // no rewrite here (#165 W3a).
                if (RequestDumpFromChild(Handler.DumpKind.Native, GetCurrentThreadId(), ptrs, dir, out long bytes, out string? err))
                {
                    Log.Event(WaveeLogLevel.Critical, "crash", "crash.dump.written", "", null, -1, null, WaveeLogField.Of("bytes", bytes));
                }
                else
                {
                    Log.Event(WaveeLogLevel.Critical, "crash", "crash.dump.failed", err ?? "", null, -1, null);
                }
                Crash.Bundles.PruneNow(LogFolder);
            }
            catch { }
        }

        static readonly object s_dumpGate = new();

        /// <summary>Writes <c>D &lt;kind&gt; &lt;tid&gt; &lt;ptrHex&gt; &lt;dir&gt;</c> and blocks for the child's
        /// <c>OK &lt;bytes&gt;</c>/<c>ERR &lt;message&gt;</c> reply, up to 10 s — a crash caller is terminating either
        /// way; <see cref="WriteTestReport"/> calls it off the UI thread. ONE request at a time (<c>s_dumpGate</c>): a
        /// test report runs while the app lives on, so a real crash on another thread must neither interleave its line
        /// with it nor race it for the one stdout reader. The child answers D lines in order, so the replies a timed-out
        /// request left owed (<c>HandlerChild.StaleReplies</c>, starting with the read it left running) are drained before this
        /// request's own is read.</summary>
        static bool RequestDumpFromChild(Handler.DumpKind kind, uint tid, nint ptrs, string dir, out long bytes, out string? error)
        {
            bytes = 0;
            error = null;
            var child = s_child;
            var stdin = s_stdin;
            if (child is null || stdin is null) { error = "the crash handler is not running"; return false; }

            string line = "D " + Handler.DumpKinds.Token(kind) + " " + tid.ToString(CultureInfo.InvariantCulture)
                + " " + unchecked((ulong)ptrs).ToString("x", CultureInfo.InvariantCulture) + " " + dir;
            lock (s_dumpGate)
            {
                lock (s_writeLock)
                {
                    try { stdin.WriteLine(line); }
                    catch (Exception ex) { error = ex.Message; return false; }
                }

                try
                {
                    long deadline = Environment.TickCount64 + 10_000;
                    int stale = child.StaleReplies;
                    child.StaleReplies = 0;
                    while (true)
                    {
                        // The pump queues replies in arrival order (the `R` ready line never reaches it), so a reply a
                        // timed-out request left owed is simply the next line(s) in the queue.
                        if (!child.Replies.TryTakeReply(MsLeft(deadline), out string? reply))
                        {
                            child.StaleReplies = stale + 1;   // the next request drains this one's answer first
                            error = "timeout waiting for the crash handler";
                            return false;
                        }
                        if (reply is null) { error = "the crash handler closed its output"; return false; }
                        if (stale > 0) { stale--; continue; }   // a timed-out earlier request's answer
                        if (reply.StartsWith("OK", StringComparison.Ordinal))
                        {
                            string[] parts = reply.Split(' ', 2);
                            if (parts.Length > 1) long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out bytes);
                            return true;
                        }
                        error = reply.StartsWith("ERR ", StringComparison.Ordinal) ? reply[4..] : reply;
                        return false;
                    }
                }
                catch (Exception ex) { error = ex.Message; return false; }
            }
        }

        static int MsLeft(long deadline) => (int)Math.Max(0L, deadline - Environment.TickCount64);

        /// <summary>The redaction rules a bundle's log tail is scrubbed with — gathered exactly like
        /// <c>Feedback.ReportComposer</c>'s <c>Rules()</c> (OS account + machine, and, when known, the signed-in
        /// account's id, display name and every Connect device the roster holds). Wrapped so a crash before login (or
        /// before the entity tables exist at all) still yields a tail, just with fewer literals to scrub.</summary>
        static Feedback.RedactionRules SafeGatherRules()
        {
            try
            {
                string? account = null, displayName = null;
                string[]? devices = null;
                try
                {
                    if (Platform.Scope.Account is { Length: > 0 } a) account = a;
                    var me = User.Me;
                    if (me.IsValid && me.Knows(UserFields.Identity)) displayName = Entities.Strings.Resolve(me.NameId);
                    var rows = Playback.Devices.Rows;
                    if (rows.Length > 0)
                    {
                        devices = new string[rows.Length];
                        for (int i = 0; i < rows.Length; i++) devices[i] = rows[i].Name ?? "";
                    }
                }
                catch { }
                return new Feedback.RedactionRules(Environment.UserName, Environment.MachineName, account, displayName, devices);
            }
            catch { return Feedback.RedactionRules.None; }
        }

        // ── 4. the protocol's other lines: first frame, exiting, modal, suspend/resume ─────────────────────────────

        public static void NoteFirstFrame()
        {
            FirstFrameSeen = true;
            try { RunMarker.MarkFrame(Platform.Settings); } catch { }
            Crash.Bundles.WriteBootFailures(LogFolder, 0);   // the streak ends the moment a frame is on screen
            SendLine("F");
        }

        public static void NoteExiting() => SendLine("X");

        public static IDisposable ModalScope() => new ModalScopeToken();

        sealed class ModalScopeToken : IDisposable
        {
            int _disposed;
            public ModalScopeToken() { Interlocked.Increment(ref s_modalDepth); SendLine("M1"); }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Interlocked.Decrement(ref s_modalDepth);
                SendLine("M0");
            }
        }

        static void OnSuspending() => SendLine("S " + Interlocked.Increment(ref s_suspendEpoch).ToString(CultureInfo.InvariantCulture));
        static void OnResumed() => SendLine("S " + Interlocked.Increment(ref s_suspendEpoch).ToString(CultureInfo.InvariantCulture));

        // ── 5. the Reports list's whole read surface ────────────────────────────────────────────────────────────────

        public static IReadOnlyList<BundleInfo> Bundles() => Crash.Bundles.List(LogFolder);

        public static BundleInfo? Read(string dir) => Crash.Bundles.Read(dir);

        /// <summary>Deletes one bundle — its queued upload FIRST (<c>Uploader.ForgetBundle</c>, #165), so a drain racing
        /// this call can neither send a report the user just deleted nor recreate its folder when it settles (that
        /// half is <see cref="Crash.Bundles.WriteSend"/>'s no-op on a missing folder). <see cref="DeleteAll"/> goes
        /// through here per bundle.</summary>
        public static void Delete(string dir)
        {
            try { Uploader.ForgetBundle(dir); }
            catch (Exception ex) { Log.Warn("crash", "crash.bundle.forget.failed", ex); }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("crash", "crash.bundle.delete.failed", ex); }
            ReportsVersion.Value++;
        }

        public static void DeleteAll()
        {
            foreach (var b in Bundles()) Delete(b.Dir);
        }

        public static void WriteSend(string dir, SendRecord r)
        {
            Crash.Bundles.WriteSend(dir, r);
            ReportsVersion.Value++;
        }

        // ── 6. the probe arms (--crash-probe) ───────────────────────────────────────────────────────────────────────

        public static string? ProbeMode() => Diagnostics.Probe.CrashProbeMode;

        /// <summary>§B.0/§G's five arms, moved out of <c>Feedback.UI.cs</c>'s <c>ChromeCore</c> VERBATIM (WP-E deletes
        /// that effect once this lands). <c>boot</c> is a no-op here — it is wired into <c>Shell.Run</c> itself,
        /// before the report chrome (or this method) ever runs.</summary>
        public static void ArmProbe(string mode, Action<Action> post)
        {
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                // `throw` faults on a POOL thread on purpose: the engine's UI post drain catches whatever a posted action
                // throws (AppHost.DrainUiPosts — it reports through Diag.Sink and the frame continues), so a throw inside
                // `post` never reaches AppDomain.UnhandledException. `throw-ui` is that documented case, kept as a probe
                // so the log line proves the drain reports instead of swallowing.
                if (mode == "throw") { ThreadPool.QueueUserWorkItem(static _ => throw new InvalidOperationException("--crash-probe")); return; }
                post(() =>
                {
                    switch (mode)
                    {
                        case "failfast": Environment.FailFast("--crash-probe"); break;
                        case "native": NativeHook.Fault(); break;
                        case "hang": NativeHook.Hang(post); break;
                        case "boot": break;
                        default: throw new InvalidOperationException("--crash-probe throw-ui");
                    }
                });
            }, TaskScheduler.Default);
        }

        // ── 7. the boot-time entry (App.Main / Diagnostics.Install) ────────────────────────────────────────────────

        /// <summary>The persisted boot-failure streak AS OF THE END OF THE PREVIOUS LAUNCH — read here, before
        /// <see cref="BeginGuiRun"/> (which recomputes it for THIS launch) has run, so <c>App.Main</c>'s early
        /// recovery check sees exactly what the previous launch's own <see cref="BeginGuiRun"/> left behind.</summary>
        /// <summary>The streak as the PREVIOUS launch left it, from the crash folder (never settings — see
        /// <see cref="Crash.Bundles.ReadBootFailures"/>). <c>App.Main</c> reads it before anything else is up.</summary>
        public static int BootFailures(string logFolder) => Crash.Bundles.ReadBootFailures(logFolder);

        /// <summary>Once per GUI launch (mirrors <c>Diagnostics.BeginGuiRun</c>'s old run-marker half, now crash-pipeline-
        /// shaped): advances the run marker, recomputes the boot-failure streak, and latches <see cref="ThisLaunch"/> —
        /// the pending bundle a crash handler stamped THIS run (the <c>logs\crash\pending</c> file), or else the newest
        /// bundle this profile has not yet been shown (its folder name sorting after <c>Keys.CrashSeenBundle</c>).</summary>
        public static void BeginGuiRun(IAppSettings settings, bool versionChanged)
        {
            RunOutcome previous = RunMarker.Begin(settings, out bool reachedFirstFrame);
            InstallId.Ensure(settings);   // UI thread, once per launch: the crash path only ever READS the id
            string pending = Crash.Bundles.TakePending(LogFolder);
            // The boot-failure streak is witnessed by the BUNDLE, not the settings marker: a crash handler that wrote
            // a bundle before the first frame is the one fact that survives every settings backend (a demo profile keeps
            // settings in memory; a real one may not have flushed). The marker only covers the bundle-less kill.
            BundleInfo? pendingBundle = null;
            try { if (pending.Length > 0) pendingBundle = Crash.Bundles.Read(pending); } catch { }
            bool previousUnclean = pendingBundle is not null || previous == RunOutcome.Unclean;
            bool previousReachedFrame = pendingBundle is { } pb ? !pb.Summary.BeforeFirstFrame : reachedFirstFrame;
            // "An update killed it" is likewise read off the bundle when there is one: the version it recorded against
            // this build's. The settings-based flag (LastRunVersion) covers only the bundle-less kill, and is meaningless
            // on a profile whose settings never persist (a demo run reads "version changed" on every launch).
            bool bundleVersionChanged = false;
            try { bundleVersionChanged = pendingBundle is { } vb && !string.Equals(vb.Summary.Version, Platform.Version.SemVer, StringComparison.Ordinal); } catch { }
            bool effectiveVersionChanged = pendingBundle is not null ? bundleVersionChanged : versionChanged;
            int previousFailures = Crash.Bundles.ReadBootFailures(LogFolder);
            int nextFailures = RecoveryPolicy.NextBootFailures(previousUnclean ? RunOutcome.Unclean : previous, previousReachedFrame, effectiveVersionChanged, previousFailures);
            Crash.Bundles.WriteBootFailures(LogFolder, nextFailures);
            string seen = settings.Get(Platform.Keys.CrashSeenBundle);
            BundleInfo? thisLaunch = null;
            string newestName = seen;
            try
            {
                thisLaunch = pendingBundle;
                var bundles = Crash.Bundles.List(LogFolder);   // newest first
                if (bundles.Count > 0) newestName = Path.GetFileName(bundles[0].Dir);
                if (thisLaunch is null)
                    foreach (var b in bundles)
                    {
                        string name = Path.GetFileName(b.Dir);
                        if (string.CompareOrdinal(name, seen) > 0) { thisLaunch = b; break; }
                    }
            }
            catch (Exception ex) { Log.Warn("crash", "crash.beginGuiRun.scan.failed", ex); }

            ThisLaunch = thisLaunch;
            settings.Set(Platform.Keys.CrashSeenBundle, newestName);

            Log.Event(WaveeLogLevel.Info, "crash", "run.begin", "", null, -1, null,
                WaveeLogField.Of("previous", previous.ToString()),
                WaveeLogField.Of("bundle", thisLaunch is { } tl ? Path.GetFileName(tl.Dir) : ""),
                WaveeLogField.Of("bootFailures", nextFailures));
        }

        // ── 8. Win32 ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The NATIVE OS thread id — <c>Environment.CurrentManagedThreadId</c> is .NET's own id, not the one
        /// <c>MiniDumpWriteDump</c>'s <c>MINIDUMP_EXCEPTION_INFORMATION.ThreadId</c> needs to point the dump at the
        /// actual faulting thread in the child's target-process read.</summary>
        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();
    }
}
