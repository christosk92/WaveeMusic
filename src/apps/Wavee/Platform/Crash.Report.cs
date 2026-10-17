// Parent-process report capture and parent-context rendering for the child reporter.
// Synthetic child reports must use DescribeParent and ContextProtocol.BuildSummary: querying the child
// for process, module, GPU, route or session facts misattributes the parent failure.
//
// Role: CORE (engine-free: no D3D, no window — Process/File/reflection reads only)
// Owner: WP-B
// Wave: crash-diagnostics
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.2 ("Describe" text), §I ("WP-B" — this file backs
//       `Crash.Host.OnManagedCrash`/`RequestDump` and `Crash.Handler`'s own bundle writes)
//
// WHY A DUPLICATE OF `Diagnostics.CrashReport.Describe` RATHER THAN A SHARED CALL. WP-E deletes
// `Diagnostics.CrashReport` in the same wave this file ships in (disjoint work packages, no ordering guarantee
// between them) — CLAUDE.md's "duplicate what you need" instruction for exactly this reason: a call from here into a
// file another package is deleting would leave a dangling reference on the losing scheduling order.

using System.Globalization;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.WindowsApi.Packaging;

namespace Wavee;

public static partial class Crash
{
    public static class Report
    {
        // ── 1. the human-readable report body (report.txt) ─────────────────────────────────────────────────────────

        /// <summary>The report minus the banner and the log tail — <c>Diagnostics.CrashReport.Describe</c> verbatim:
        /// version first (a report pasted into an issue is usually truncated after a few lines), then the exception's
        /// whole <c>ToString()</c> (not just <c>StackTrace</c> — an inner exception's real fault is only in the
        /// former), then the RVA list a later symbolication needs.</summary>
        public static void Describe(Exception ex, StringBuilder sb)
        {
            WaveeVersionInfo? v = null;
            try { v = Platform.Version; } catch { }
            string Or(string? s) => string.IsNullOrEmpty(s) ? "unknown" : s;
            sb.Append("version=").Append(Or(v?.SemVer)).Append('\n')
              .Append("commit=").Append(Or(v?.Commit)).Append('\n')
              .Append("buildDate=").Append(Or(v?.BuildDate)).Append('\n')
              .Append("channel=").Append(Or(v?.Channel)).Append('\n')
              .Append("quad=").Append(Or(v?.Quad)).Append('\n')
              .Append("arch=").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()).Append('\n')
              .Append("timeLocal=").Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
              .Append("pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("framework=").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('\n')
              .Append("os=").Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription).Append('\n')
              .Append(MainModuleLine()).Append("\n\nException\n---------\n");
            string text = ex.ToString();
            sb.Append(text).Append('\n');
            AppendRvaSection(sb, ParseRvas(text));
        }

        /// <summary>The "Frames (RVA)" section — one <c>0x&lt;hex&gt;</c> offset per line from the module base in the
        /// header, innermost first — shared by the managed report (offsets parsed out of <c>ex.ToString()</c>) and the
        /// native one (offsets the VEH captured, <see cref="NativeHook.FaultRvas"/>), so a person resolving either with
        /// <c>ln</c> and the dashboard reading either see one format. Nothing is written for an empty list.</summary>
        public static void AppendRvaSection(StringBuilder sb, IReadOnlyList<long> rvas)
        {
            if (rvas.Count == 0) return;
            sb.Append("\nFrames (RVA)\n------------\n")
              .Append("# offsets from the module base above, in stack-trace order (innermost first);\n")
              .Append("# resolve each with `ln Wavee+0x<rva>` against the release's Wavee-<quad>-<rid>-symbols.zip\n");
            for (int i = 0; i < rvas.Count; i++) sb.Append("0x").Append(rvas[i].ToString("x", CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>Convenience over <see cref="Describe(Exception,StringBuilder)"/> for a caller that just wants the
        /// text (<c>Crash.Host.OnManagedCrash</c>).</summary>
        public static string Describe(Exception ex)
        {
            var sb = new StringBuilder(4 * 1024);
            Describe(ex, sb);
            return sb.ToString();
        }

        /// <summary>The same header block, for a bundle with no managed exception to describe (Native/Hang/ExitCode/
        /// UncleanExit): <paramref name="message"/> replaces the "Exception" section. No RVA list follows from here — the
        /// native path appends the VEH-captured one itself (<see cref="AppendRvaSection"/>); Hang/ExitCode/UncleanExit
        /// have none. Used by <c>Crash.Host.RequestDump</c> (the native path) and by <c>Crash.Handler</c>
        /// (Hang/ExitCode/UncleanExit, written entirely inside the child).</summary>
        public static void DescribeSynthetic(string message, StringBuilder sb)
        {
            WaveeVersionInfo? v = null;
            try { v = Platform.Version; } catch { }
            string Or(string? s) => string.IsNullOrEmpty(s) ? "unknown" : s;
            sb.Append("version=").Append(Or(v?.SemVer)).Append('\n')
              .Append("commit=").Append(Or(v?.Commit)).Append('\n')
              .Append("buildDate=").Append(Or(v?.BuildDate)).Append('\n')
              .Append("channel=").Append(Or(v?.Channel)).Append('\n')
              .Append("quad=").Append(Or(v?.Quad)).Append('\n')
              .Append("arch=").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()).Append('\n')
              .Append("timeLocal=").Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
              .Append("pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("framework=").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('\n')
              .Append("os=").Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription).Append('\n')
              .Append(MainModuleLine()).Append("\n\n").Append(message).Append('\n');
        }

        /// <summary>A child-produced report header describes only the supplied parent context.</summary>
        public static void DescribeParent(string message, StringBuilder sb, Summary summary, ParentContext? context)
        {
            static string Known(string? value) => string.IsNullOrEmpty(value) ? "unknown" : value;
            sb.Append("version=").Append(Known(summary.Version)).Append('\n')
              .Append("commit=").Append(Known(summary.Commit)).Append('\n')
              .Append("buildDate=").Append(Known(context?.BuildDate)).Append('\n')
              .Append("channel=").Append(Known(summary.Channel)).Append('\n')
              .Append("quad=").Append(Known(summary.Quad)).Append('\n')
              .Append("arch=").Append(Known(summary.Arch)).Append('\n')
              .Append("stampUtc=").Append(summary.StampUtc).Append('\n')
              .Append("pid=").Append(summary.ParentPid).Append('\n')
              .Append("reporterPid=").Append(summary.ReporterPid).Append('\n')
              .Append("sessionId=").Append(Known(summary.SessionId)).Append('\n')
              .Append("uptimeMs=").Append(summary.UptimeMs.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("framework=").Append(Known(context?.Framework)).Append('\n')
              .Append("os=").Append(Known(context?.Os)).Append('\n')
              .Append("module=").Append(Known(context?.ModuleName))
              .Append(" base=0x").Append(summary.ModuleBase.ToString("x", CultureInfo.InvariantCulture))
              .Append(" size=0x").Append(summary.ModuleSize.ToString("x", CultureInfo.InvariantCulture))
              .Append("\n\n").Append(message).Append('\n');
        }

        /// <summary>The current process's main module line — <c>Diagnostics.CrashReport.MainModuleLine</c> verbatim
        /// (<c>module=... base=0x... size=0x...</c>).</summary>
        public static string MainModuleLine()
        {
            string path = Environment.ProcessPath ?? "unknown";
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                if (proc.MainModule is { } main)
                    return "module=" + (string.IsNullOrEmpty(main.FileName) ? path : main.FileName)
                         + " base=0x" + main.BaseAddress.ToInt64().ToString("x", CultureInfo.InvariantCulture)
                         + " size=0x" + main.ModuleMemorySize.ToString("x", CultureInfo.InvariantCulture);
            }
            catch { }
            return "module=" + path + " base=unknown size=unknown";
        }

        /// <summary>Every <c>&lt;module&gt;!&lt;BaseAddress&gt;+0x&lt;hex&gt;</c> offset in a NativeAOT trace,
        /// innermost first, duplicates kept — <c>Diagnostics.CrashFiles.ParseRvas</c> verbatim (marker scan, not a
        /// regex). Any other frame shape contributes nothing; never throws.</summary>
        public static List<long> ParseRvas(string? stackTrace)
        {
            const string Marker = "!<BaseAddress>+0x";
            var found = new List<long>();
            if (string.IsNullOrEmpty(stackTrace)) return found;
            int at = 0;
            while ((at = stackTrace.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
            {
                int start = at + Marker.Length, end = start;
                while (end < stackTrace.Length && char.IsAsciiHexDigit(stackTrace[end])) end++;
                at = end;
                int digits = end - start;
                if (digits is 0 or > 15) continue;
                if (long.TryParse(stackTrace.AsSpan(start, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long rva))
                    found.Add(rva);
            }
            return found;
        }

        // ── 2. the machine-readable summary (summary.json) ─────────────────────────────────────────────────────────

        /// <summary>Captures a summary from the parent process. The child uses <see cref="ContextProtocol.BuildSummary"/>
        /// with its last validated parent context instead. Exceptions are unwrapped for the title while RVA parsing
        /// keeps the full exception text; native callers can supply captured RVAs and NTSTATUS directly.</summary>
        public static Summary BuildSummary(Kind kind, Exception? ex, int exitCode, bool hasDump = false, long dumpBytes = 0,
            string? exceptionType = null, string? exceptionMessage = null, bool? beforeFirstFrame = null, string? installIdOverride = null,
            long[]? rvas = null, uint exceptionCode = 0)
        {
            string reportId = Guid.NewGuid().ToString("N");
            string installId = "";
            try { installId = installIdOverride ?? InstallId.Peek(Platform.Settings); } catch { installId = ""; }

            WaveeVersionInfo? v = null;
            try { v = Platform.Version; } catch { }
            string Or(string? s) => s ?? "";

            (long moduleBase, long moduleSize) = MainModuleRange();
            string debugId = "";
            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    using var pe = File.OpenRead(exePath);
                    PeDebugId.TryRead(pe, out debugId, out _);
                }
            }
            catch { debugId = ""; }

            Exception? fault = ex is null ? null : Unwrap(ex);
            string exType = exceptionType ?? fault?.GetType().FullName ?? "";
            string exMessage = exceptionMessage ?? fault?.Message ?? "";
            long[] frames = rvas ?? (ex is not null ? ParseRvas(ex.ToString()).ToArray() : []);

            string locale = "";
            try { locale = System.Globalization.CultureInfo.CurrentUICulture.Name; } catch { }

            bool packaged = false;
            try { packaged = PackageIdentity.IsPackaged; } catch { }

            return new Summary(
                reportId, installId, kind, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Or(v?.SemVer), Or(v?.Quad), Or(v?.Commit), Or(v?.Channel),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
                GpuProfile.AdapterName, GpuProfile.Tier.ToString(), GpuProfile.IsSoftwareAdapter,
                packaged, locale,
                Log.SessionId, Log.SinceStartMs, beforeFirstFrame ?? !Host.FirstFrameSeen, Diagnostics.NavigationFrameWatch.Route,
                exType, exMessage, frames, moduleBase, moduleSize, debugId, exitCode, hasDump, dumpBytes, exceptionCode,
                ParentPid: Environment.ProcessId, ReporterPid: Environment.ProcessId);
        }

        /// <summary>The exception a summary should be TITLED by: peels the wrappers that only say "this happened somewhere
        /// else" — an <see cref="AggregateException"/> with exactly one inner, a
        /// <see cref="System.Reflection.TargetInvocationException"/> and a <see cref="TypeInitializationException"/>
        /// (each only when it carries an inner) — repeatedly, so <c>Aggregate(TargetInvocation(InvalidOperation))</c>
        /// answers the InvalidOperationException. A multi-inner aggregate stays itself (no single fault to pick). Bounded
        /// depth, never throws.</summary>
        public static Exception Unwrap(Exception ex)
        {
            Exception current = ex;
            for (int depth = 0; depth < 16; depth++)
            {
                Exception? inner = current switch
                {
                    AggregateException { InnerExceptions.Count: 1 } agg => agg.InnerExceptions[0],
                    System.Reflection.TargetInvocationException { InnerException: { } tie } => tie,
                    TypeInitializationException { InnerException: { } tye } => tye,
                    _ => null,
                };
                if (inner is null) break;
                current = inner;
            }
            return current;
        }

        static (long Base, long Size) MainModuleRange()
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                if (proc.MainModule is { } main) return (main.BaseAddress.ToInt64(), main.ModuleMemorySize);
            }
            catch { }
            return (0, 0);
        }
    }
}
