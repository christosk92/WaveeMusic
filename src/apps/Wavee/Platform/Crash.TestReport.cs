// ── Platform/Crash.TestReport.cs ───────────────────────────────────────────────────────────────────────────────────
// Settings › General › Developer › "Send a test crash report" (#165): a FULL crash report without a crash. A real
// exception is thrown through real Wavee frames and caught (`SimulatedCrashException.ThrowAndCatch`); the normal
// managed bundle is written from it (summary.json, report.txt, log-tail.txt) plus a real minidump of the LIVE process,
// written by the crash-handler child for the `test` dump kind. The UI half (`Screens/Settings.UI.cs`) then hands the
// bundle to the normal uploader (scrub → pack → outbox → POST) and shows the real outcome toast. Wavee keeps running.
//
// Role: SHELL (blocks on the child's dump reply — never call it on the UI thread)
// Spec: docs/plans/wavee/crash-production-readiness-implementation.md (#165); docs/guide/crash-diagnostics.md §5
//
// WHAT A TEST REPORT NEVER TOUCHES, so it is never mistaken for a crash: the once-per-process crash latches
// (`s_managedHandled` / `s_nativeHandled` — a real crash later in this run still writes its own bundle), the pending
// marker (`Bundles.MarkPending` — the next launch must not offer it as "Wavee crashed last time"), and the child's
// exit-code latch (the `test` dump kind, `Crash.Handler.DumpKinds.LatchesBundle` — a non-zero exit later in this run
// is still recorded).

using System.Runtime.CompilerServices;

namespace Wavee;

public static partial class Crash
{
    /// <summary>The exception a developer's test report is built from. Thrown and caught inside Wavee
    /// (<see cref="ThrowAndCatch"/>), never escaping; its type is what the report's summary — and so its issue on the
    /// crash dashboard — is titled by.</summary>
    public sealed class SimulatedCrashException : Exception
    {
        public const string Text = "Simulated crash report (Settings › Developer › Send a test crash report)";

        public SimulatedCrashException() : base(Text) { }

        /// <summary>Throws one through three no-inline Wavee frames and returns it CAUGHT, so its trace is a real one —
        /// in the NativeAOT build <c>ex.ToString()</c> prints <c>Wavee!&lt;BaseAddress&gt;+0x…</c> lines that
        /// <see cref="Report.ParseRvas"/> turns into the summary's RVAs.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static SimulatedCrashException ThrowAndCatch()
        {
            try { _ = ThrowOuter(); }
            catch (SimulatedCrashException ex) { return ex; }
            throw new System.Diagnostics.UnreachableException("SimulatedCrashException.ThrowInner returned");
        }

        // The `+ 1` after each call keeps it out of tail position: an opportunistic tail call would drop the caller's
        // frame from the trace.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ThrowOuter() => ThrowMiddle() + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ThrowMiddle() => ThrowInner() + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ThrowInner() => throw new SimulatedCrashException();
    }

    public static partial class Host
    {
        /// <summary>Writes a full managed bundle for a caught <see cref="SimulatedCrashException"/> and returns it, or
        /// null when the bundle could not be written (logged). The same sequence as <see cref="OnManagedCrash"/> —
        /// summary, report, log tail, then the child's dump (the <c>test</c> kind: no exception stream, the parent keeps
        /// running; the child finalizes hasDump/dumpBytes before it replies) — minus everything the file header lists.
        /// A failed dump is not fatal: the report still goes out without one.
        /// <para>Blocks on the child's reply (up to 10 s), so it runs OFF the UI thread; the caller bumps
        /// <see cref="ReportsVersion"/> back on it. <paramref name="rules"/> are gathered by the caller ON the UI thread
        /// (<see cref="Scrubber.RulesNow"/>): the app is alive, so its account and device tables are not read from here
        /// the way the dying crash path must.</para></summary>
        public static BundleInfo? WriteTestReport(Feedback.RedactionRules rules)
        {
            try
            {
                SimulatedCrashException caught = SimulatedCrashException.ThrowAndCatch();
                string dir = Crash.Bundles.Create(LogFolder, Kind.Managed, DateTimeOffset.Now);
                Crash.Bundles.WriteSummary(dir, Report.BuildSummary(Kind.Managed, caught, 0));
                Crash.Bundles.WriteReport(dir, Report.Describe(caught));
                Crash.Bundles.WriteTail(dir, s_datedLogPath, 300, rules);

                if (!RequestDumpFromChild(Handler.DumpKind.Test, GetCurrentThreadId(), 0, dir, out long bytes, out string? err))
                    Log.Warn("crash", "crash.test.dump.failed: " + (err ?? "unknown"));
                Crash.Bundles.PruneNow(LogFolder);

                Log.Event(WaveeLogLevel.Info, "crash", "crash.test.written", "", null, -1, null,
                    WaveeLogField.Of("dir", Path.GetFileName(dir)), WaveeLogField.Of("dumpBytes", bytes));
                return Crash.Bundles.Read(dir);
            }
            catch (Exception ex)
            {
                Log.Warn("crash", "crash.test.failed", ex);
                return null;
            }
        }
    }
}
