// ── Wavee/Diagnostics/LogSessions.Probe.cs — `Wavee.exe --log-sessions` ────────────────────────────────────────────
//
// The headless probe behind the Logs page's session picker (privacy & diagnostics plan §2.1, "first implementation
// step"): it runs the REAL walk, `WaveeLogSessions.ListPastSessions(Log.BasePath, pid)`, on the real log folder and
// prints what the picker would be built from — the base path, how many files / lines / sessions, how many files could
// not be read, the elapsed time, the newest sessions, and the exception when the walk faulted. It is how "the picker
// is empty" is told apart from "nothing on disk" and "the walk failed" without opening the window, and the one way to
// run the walk against a PACKAGED LocalCache (launch the packaged exe with the flag; the startup line's `logResolved=`
// says which folder that was).
//
// NO LOGIN, NO WINDOW. It is a windowless CLI arm of `Probe.TryRunCliArm` (the `--qr-dump` door): `Platform.Boot` has
// already configured the log, so `Log.BasePath` is the real one (and `--profile <dir>` redirects it like everywhere
// else). Output goes to the attached parent console AND through `Log.Event` (category `probe`, event `log.sessions`),
// so the answer is also in the file it was measured on. Exit code: 0 when the walk completed, 1 when it faulted.
//
// Run it from a real terminal (a WinExe has no console of its own): see .claude/skills/wavee/probes.md.

using System.Diagnostics;
using System.Globalization;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        /// <summary>`--log-sessions`: walk, time, print, log.</summary>
        static int LogSessions()
        {
            AttachParentConsole();
            string? basePath = Log.BasePath;
            var clock = Stopwatch.StartNew();
            var walk = WaveeLogSessions.ListPastSessions(basePath, Environment.ProcessId);
            clock.Stop();

            double ms = clock.Elapsed.TotalMilliseconds;
            string[] report = LogSessionsReport.Format(basePath, walk, ms, TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
            foreach (string line in report) Console.Out.WriteLine(line);

            Log.Event(walk.Error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "probe", "log.sessions",
                "log sessions walked files=" + walk.FilesSeen.ToString(CultureInfo.InvariantCulture)
                + " lines=" + walk.LinesSeen.ToString(CultureInfo.InvariantCulture)
                + " skipped=" + walk.FilesSkipped.ToString(CultureInfo.InvariantCulture)
                + " sessions=" + walk.Sessions.Count.ToString(CultureInfo.InvariantCulture)
                + " ms=" + ms.ToString("0.0", CultureInfo.InvariantCulture),
                null, (long)ms, walk.Error);
            return walk.Error is null ? 0 : 1;
        }
    }

    /// <summary>The probe's output, pure: one line per fact, then the newest sessions in the combo's own label
    /// (<see cref="LogView.SessionLabel"/>) with the pid and sid the label leaves out, then the exception if any.</summary>
    public static class LogSessionsReport
    {
        public const int MaxListed = 10;

        public static string[] Format(string? basePath, WaveeLogSessions.WalkResult walk, double elapsedMs, TimeSpan utcOffset)
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new List<string>(MaxListed + 8)
            {
                "basePath    : " + (basePath ?? "(log not configured)"),
                "files seen  : " + walk.FilesSeen.ToString(c),
                "lines seen  : " + walk.LinesSeen.ToString(c),
                "files skipped: " + walk.FilesSkipped.ToString(c),
                "sessions    : " + walk.Sessions.Count.ToString(c),
                "ms          : " + elapsedMs.ToString("0.0", c),
            };
            int shown = Math.Min(MaxListed, walk.Sessions.Count);
            for (int i = 0; i < shown; i++)
            {
                var s = walk.Sessions[i];
                lines.Add("  " + (i + 1).ToString(c).PadLeft(2) + ". " + LogView.SessionLabel(s.StartUnixMs, s.Pid, s.EntryCount, utcOffset)
                    + " · pid " + s.Pid.ToString(c) + " · sid " + (s.SessionId.Length > 0 ? s.SessionId : "-"));
            }
            if (walk.Sessions.Count > shown) lines.Add("  … and " + (walk.Sessions.Count - shown).ToString(c) + " more");
            if (walk.Error is { } error) lines.Add("Error       : " + error.GetType().Name + ": " + error.Message);
            return lines.ToArray();
        }
    }
}
