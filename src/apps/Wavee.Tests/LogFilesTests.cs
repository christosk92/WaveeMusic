// ── Wavee.Tests/LogFilesTests.cs — the app log's FILE rules (Platform/Log.Files.cs) ─────────────────────────────────
//
// The naming authority (daily file, size roll, legacy double-dated roll), what "Delete old logs" may delete, the
// retention rule (days AND bytes, the active file never), the sink queue's overflow and its drop marker, the crash
// tail merge, when a memory sample is a warning, the engine-line routing, the live "Export session" text and the
// `--log-sessions` probe's report. Pure: names and numbers in, names and numbers out — no file is opened (the two
// `ListPastSessions_*` facts that need a folder live beside the other WaveeLogSessions facts in PlatformWave6Tests).

using Xunit;

namespace Wavee.Tests;

public class LogFilesTests
{
    static readonly string Base = Path.Combine(Path.GetTempPath(), "wavee-logfiles-tests", "wavee.log");
    static readonly DateOnly Today = new(2026, 10, 2);
    static readonly DateTime NowUtc = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);
    static string Name(string path) => Path.GetFileName(path);

    // ── G1: the naming authority ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rolled_SortsBeforeItsDay()
    {
        string day = Name(LogFileNames.Dated(Base, Today));
        string rolled = Name(LogFileNames.Rolled(Base, new DateTime(2026, 10, 2, 16, 31, 42)));
        string collided = Name(LogFileNames.Rolled(Base, new DateTime(2026, 10, 2, 16, 31, 42), collision: 1));

        Assert.Equal("wavee-20261002.log", day);
        Assert.True(string.CompareOrdinal(rolled, day) < 0, "'-' sorts before '.', so a roll precedes the day file it was cut from");
        Assert.True(string.CompareOrdinal(collided, day) < 0);

        // …which is all WaveeLogSessions.Chronological relies on: yesterday, then today's roll, then today's day file.
        string[] order = WaveeLogSessions.Chronological([day, rolled, "wavee-20261001.log"], null);
        Assert.Equal(new[] { "wavee-20261001.log", rolled, day }, order);
    }

    [Fact]
    public void Rolled_UsesLocalTime_SingleDate()
    {
        // The writer passes DateTime.Now: the stamp is exactly that clock's date and time, written once (the legacy
        // roll was "<day>-<UTC date>-<UTC time>" — two dates, and a UTC one).
        string name = Name(LogFileNames.Rolled(Base, new DateTime(2026, 10, 2, 16, 31, 42, DateTimeKind.Local)));
        Assert.Equal("wavee-20261002-163142.log", name);
        Assert.Equal(1, CountOccurrences(name, "20261002"));
        Assert.Equal("wavee-20261002-000005.log", Name(LogFileNames.Rolled(Base, new DateTime(2026, 10, 2, 0, 0, 5))));   // zero-padded
    }

    [Fact]
    public void Rolled_CollisionSuffix()
    {
        var at = new DateTime(2026, 10, 2, 16, 31, 42);
        Assert.Equal("wavee-20261002-163142.log", Name(LogFileNames.Rolled(Base, at)));
        Assert.Equal("wavee-20261002-163142.log", Name(LogFileNames.Rolled(Base, at, collision: 0)));
        Assert.Equal("wavee-20261002-163142-2.log", Name(LogFileNames.Rolled(Base, at, collision: 1)));
        Assert.Equal("wavee-20261002-163142-3.log", Name(LogFileNames.Rolled(Base, at, collision: 2)));
        for (int n = 0; n < 5; n++) Assert.True(LogFileNames.IsAppLog(LogFileNames.Rolled(Base, at, n), Base));
    }

    [Theory]
    [InlineData("wavee-20261002.log", true)]                       // the daily file
    [InlineData("wavee-20261002-163142.log", true)]                // a size roll (and the 0.2.9 migration stamp)
    [InlineData("wavee-20261002-163142-2.log", true)]              // a same-second roll
    [InlineData("wavee-20261002-163142-13.log", true)]
    [InlineData("wavee-20261002-20261002-143142.log", true)]       // the LEGACY double-dated roll already on disk: still an app log
    [InlineData("WAVEE-20261002.LOG", true)]                       // Windows names are case-insensitive
    [InlineData("scroll-20260924-143939.csv", false)]
    [InlineData("crash-report-20260920-194917-802.txt", false)]
    [InlineData("wavee-report-20260919-125433.txt", false)]
    [InlineData("wavee-report-20260919-125433.log", false)]
    [InlineData("wavee.log", false)]                               // the base name itself is nobody's file
    [InlineData("wavee-20261002.log.bak", false)]
    [InlineData("wavee-20261002 - Copy.log", false)]
    [InlineData("wavee-99999999.log", false)]                      // not a date
    [InlineData("wavee-20261002-16314.log", false)]                // five digits is not a time
    [InlineData("wavee-20261002-163142-.log", false)]
    [InlineData("wavee-.log", false)]
    public void IsAppLog_AcceptsDailyRolledAndLegacyDoubleDated_RejectsCsvAndCrashTxt(string name, bool expected)
    {
        Assert.Equal(expected, LogFileNames.IsAppLog(name, Base));
        Assert.Equal(expected, LogFileNames.IsAppLog(Path.Combine(Path.GetDirectoryName(Base)!, name), Base));   // a full path reduces to its name
    }

    [Fact]
    public void IsActive_IsTodaysDailyFileOnly()
    {
        Assert.True(LogFileNames.IsActive("wavee-20261002.log", Base, Today));
        Assert.True(LogFileNames.IsActive(LogFileNames.Dated(Base, Today), Base, Today));
        Assert.False(LogFileNames.IsActive("wavee-20261001.log", Base, Today));                       // yesterday's day file
        Assert.False(LogFileNames.IsActive("wavee-20261002-163142.log", Base, Today));                // today's size roll is finished
        Assert.False(LogFileNames.IsActive("wavee-20261002-20261002-143142.log", Base, Today));       // a legacy roll too
        Assert.False(LogFileNames.IsActive("wavee-20261003.log", Base, Today));
    }

    [Fact]
    public void DeleteOldLogsPlan_ExcludesTheActiveDatedFile()
    {
        string[] folder =
        [
            "wavee-20261002.log",                       // ACTIVE: the file Wavee is writing to — the bug deleted this one
            "wavee-20261002-103721.log",                // today's size roll
            "wavee-20261001.log",
            "wavee-20261001-20261001-033552.log",       // a legacy roll
            "scroll-20260924-143939.csv",
            "crash-report-20260920-194917-802.txt",
            "wavee-report-20260919-125433.txt",
        ];
        var targets = LogFileNames.SweepTargets(folder, Base, Today);

        Assert.Equal(new[] { "wavee-20261002-103721.log", "wavee-20261001.log", "wavee-20261001-20261001-033552.log" }, targets);
        Assert.DoesNotContain("wavee-20261002.log", targets);

        // At midnight "active" moves with the day: yesterday's file is no longer spared, the new day's is.
        var tomorrow = LogFileNames.SweepTargets(folder, Base, Today.AddDays(1));
        Assert.Contains("wavee-20261002.log", tomorrow);
    }

    [Fact]
    public void Census_CountsOnlyAppLogs()
    {
        string[] folder = ["wavee-20261002.log", "wavee-20261001-163142.log", "wavee-20260930-20260930-010101.log",
            "scroll-20260924-143939.csv", "crash-report-20260920-194917-802.txt", "wavee-report-20260919-125433.txt"];
        var counted = LogFileNames.AppLogs(folder, Base);
        Assert.Equal(3, counted.Count);
        Assert.All(counted, n => Assert.StartsWith("wavee-2026", n, StringComparison.Ordinal));
    }

    [Fact]
    public void TailPath_FollowsTheDay()
    {
        string[] boot = ["wavee-20261001.log", "wavee-20261001-120000.log", "scroll-1.csv"];
        string[] after = [.. boot, "wavee-20261002.log"];

        // Today's file exists: that one — at crash time, not the boot day's.
        Assert.Equal("wavee-20261002.log", Name(LogFileNames.TailPath(Base, Today, after)));
        // Seconds after midnight, before the new day's first line is on disk: the newest app log, never a csv.
        Assert.Equal("wavee-20261001.log", Name(LogFileNames.TailPath(Base, Today, boot)));
        // Nothing at all: today's name (the tail reader treats a missing file as an empty tail).
        Assert.Equal("wavee-20261002.log", Name(LogFileNames.TailPath(Base, Today, ["scroll-1.csv"])));
    }

    // ── G3: retention by age and bytes ───────────────────────────────────────────────────────────────────────────

    const long MB = 1L << 20;

    static LogRetentionPolicy.Entry Mk(string name, double ageDays, long bytes)
        => new(Path.Combine(Path.GetDirectoryName(Base)!, name), bytes, NowUtc - TimeSpan.FromDays(ageDays));

    [Fact]
    public void Retention_KeepsSevenDaysNotSevenFiles()
    {
        var files = new List<LogRetentionPolicy.Entry>();
        for (int i = 0; i < 20; i++) files.Add(Mk("wavee-keep-" + i + ".log", ageDays: i * (6.9 / 19), bytes: MB));   // twenty files inside a week
        files.Add(Mk("wavee-old-8.log", 8, MB));
        files.Add(Mk("wavee-old-10.log", 10, MB));
        files.Add(Mk("wavee-old-9.log", 9, MB));
        string active = files[0].Path;

        var victims = LogRetentionPolicy.Select(files, NowUtc, active);

        Assert.Equal(3, victims.Count);                                                  // the old ones, and only those
        Assert.Equal(new[] { "wavee-old-10.log", "wavee-old-9.log", "wavee-old-8.log" }, victims.Select(Name).ToArray());   // oldest first
    }

    [Fact]
    public void Retention_NeverSelectsTheActiveFile()
    {
        var active = Mk("wavee-20261002.log", ageDays: 30, bytes: 300 * MB);           // ancient AND over the cap on its own
        var others = new[] { Mk("wavee-20261001.log", 1, 10 * MB), Mk("wavee-20260930.log", 2, 10 * MB) };

        var victims = LogRetentionPolicy.Select([active, .. others], NowUtc, active.Path);

        Assert.DoesNotContain(active.Path, victims);
        Assert.Equal(2, victims.Count);                                                  // everything else goes — the cap cannot be met, the active file stays
        Assert.Empty(LogRetentionPolicy.Select([active], NowUtc, active.Path));
        Assert.Single(LogRetentionPolicy.Select([active], NowUtc, activePath: null));    // with no active file it is just a 30-day-old file
    }

    [Fact]
    public void Retention_BytesCapWins()
    {
        // Five files inside two days, 100 MB each: age spares them all, the 250 MB cap does not.
        var files = new[]
        {
            Mk("wavee-a.log", 1.9, 100 * MB), Mk("wavee-b.log", 1.5, 100 * MB), Mk("wavee-c.log", 1.0, 100 * MB),
            Mk("wavee-d.log", 0.5, 100 * MB), Mk("wavee-e.log", 0.1, 100 * MB),
        };

        var victims = LogRetentionPolicy.Select(files, NowUtc, files[4].Path);

        Assert.Equal(new[] { "wavee-a.log", "wavee-b.log", "wavee-c.log" }, victims.Select(Name).ToArray());   // oldest first, 500 -> 200 MB
        Assert.Empty(LogRetentionPolicy.Select(files, NowUtc, files[4].Path, maxBytes: 600 * MB));
    }

    // ── G5: the crash tail ───────────────────────────────────────────────────────────────────────────────────────

    static string Line(long seq, string sid, string rest = "x")
        => "seq=" + seq + " tid=1 t=" + (1_790_000_000_000 + seq) + " sid=" + sid + " pid=7 I [app] " + rest;

    [Fact]
    public void Merge_AppendsPendingAfterFile_DedupesBySeq_KeepsLastMax()
    {
        string[] file = [Line(1, "aaaaaaaa"), Line(2, "aaaaaaaa"), "   at Wavee.Thing.Throw()", Line(3, "bbbbbbbb")];
        string[] pending =
        [
            Line(3, "bbbbbbbb"),      // already on disk by the time the file was read: not repeated
            Line(4, "bbbbbbbb"),
            Line(2, "bbbbbbbb"),      // seq 2 of ANOTHER run than the file's seq 2: seq restarts per process, so it is not a duplicate
        ];

        string[] all = TailAssembler.Merge(file, pending, 100);
        Assert.Equal(new[] { file[0], file[1], file[2], file[3], pending[1], pending[2] }, all);   // file lines first, then what the file lacked

        Assert.Equal(new[] { file[3], pending[1], pending[2] }, TailAssembler.Merge(file, pending, 3));   // the LAST max
        Assert.Equal(file, TailAssembler.Merge(file, [], 100));
        Assert.Equal(file[^2..], TailAssembler.Merge(file, [], 2));
        Assert.Empty(TailAssembler.Merge(file, pending, 0));
        Assert.Equal(pending, TailAssembler.Merge([], pending, 10));
    }

    // ── G6: the sink queue and its drop marker ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Admit_DropsOldestBeyondCap()
    {
        Assert.Equal(0, FileQueuePolicy.Admit(0, 8));
        Assert.Equal(0, FileQueuePolicy.Admit(7, 8));       // room for one more
        Assert.Equal(1, FileQueuePolicy.Admit(8, 8));       // full: drop the oldest to admit the newest
        Assert.Equal(3, FileQueuePolicy.Admit(10, 8));      // over the cap (a lowered cap): back down to cap - 1, then admit

        var queue = new Queue<int>();
        int dropped = 0;
        for (int i = 0; i < 20; i++)
        {
            for (int d = FileQueuePolicy.Admit(queue.Count, 8); d > 0 && queue.Count > 0; d--) { queue.Dequeue(); dropped++; }
            queue.Enqueue(i);
        }
        Assert.Equal(8, queue.Count);
        Assert.Equal(12, dropped);
        Assert.Equal(12, queue.Peek());                     // the oldest 12 went, the newest 8 stayed
    }

    [Fact]
    public void TryParseLine_ParsesTheDropMarker()
    {
        var marker = FileQueuePolicy.DropMarker(sequence: 41, unixMs: 1_790_951_509_732, threadId: 7, count: 5);
        string line = Log.FormatFileLine(in marker);

        Assert.StartsWith("seq=41 tid=7 t=1790951509732 sid=" + Log.SessionId + " pid=", line, StringComparison.Ordinal);
        Assert.Contains(" W [log] file sink dropped queued lines count=5", line, StringComparison.Ordinal);

        Assert.True(WaveeLogSessions.TryParseLine(line, out var back, out bool isStart, out int pid, out string sid));
        Assert.False(isStart);
        Assert.Equal(41, back.Sequence);
        Assert.Equal(1_790_951_509_732, back.UnixMs);
        Assert.Equal(WaveeLogLevel.Warning, back.Level);
        Assert.Equal("log", back.Category);
        Assert.Equal(FileQueuePolicy.DropMessage(5), back.Message);
        Assert.Equal(Log.SessionId, sid);
        Assert.Equal(Environment.ProcessId, pid);
    }

    // ── G7: level hygiene ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MemorySample_WarnsOnlyOnNewPeak()
    {
        const long T = 400 * MB;
        Assert.Equal(WaveeLogLevel.Info, MemorySamplePolicy.LevelFor(300 * MB, 0, T));          // below the threshold: never
        Assert.Equal(WaveeLogLevel.Info, MemorySamplePolicy.LevelFor(T, 0, T));                 // AT it: not above it
        Assert.Equal(WaveeLogLevel.Warning, MemorySamplePolicy.LevelFor(500 * MB, 0, T));       // the sample that first crosses it
        Assert.Equal(WaveeLogLevel.Warning, MemorySamplePolicy.LevelFor(500 * MB, 450 * MB, T)); // a new peak
        Assert.Equal(WaveeLogLevel.Info, MemorySamplePolicy.LevelFor(500 * MB, 500 * MB, T));   // steady at the peak: the 5-second drumbeat
        Assert.Equal(WaveeLogLevel.Info, MemorySamplePolicy.LevelFor(450 * MB, 500 * MB, T));   // below the peak
        Assert.Equal(WaveeLogLevel.Warning, MemorySamplePolicy.LevelFor(501 * MB, 500 * MB, T)); // growing again
    }

    [Theory]
    [InlineData("[hidden] shallow park=Os unpinnedReleased=3 ready=2 ms=1.50", Log.DiagRoute.Info)]
    [InlineData("[hidden] restore ready=2", Log.DiagRoute.Info)]
    [InlineData("[render.pace] tick=303134(+120) fresh=120 motion=0", Log.DiagRoute.Info)]
    [InlineData("[wake] 30.0s fps=151.6 run=4549 rendered=128", Log.DiagRoute.Info)]
    [InlineData("[d3d12.present] depth=2 mode=flip", Log.DiagRoute.Info)]
    [InlineData("[d3d12.display] monitor=2 hz=50", Log.DiagRoute.Info)]
    [InlineData("[d3d12.present] depth=2 dwmGlitches=3", Log.DiagRoute.Warn)]     // a glitch is a fault wherever it is reported
    [InlineData("[post] posted action threw", Log.DiagRoute.Warn)]
    [InlineData("[d3d12.stall] fence stuck", Log.DiagRoute.Warn)]
    [InlineData("[device-lost] reason=removed", Log.DiagRoute.Warn)]
    [InlineData("[d3d12.adapter] name=Adreno", Log.DiagRoute.Warn)]
    [InlineData("[d3d12.forensic] last 64 ops", Log.DiagRoute.Warn)]
    [InlineData("[compositor-clock] latched to timer", Log.DiagRoute.Warn)]
    [InlineData("[video] pump area=1", Log.DiagRoute.Info)]
    [InlineData("[scroll.engaged] flip", Log.DiagRoute.Info)]
    [InlineData("[overlay.popup] material=TransientAcrylic lease=9.1ms", Log.DiagRoute.Info)]   // one line per OS popup window, at close
    [InlineData("[layout] something chatty", Log.DiagRoute.Debug)]
    public void RouteFor_CensusLinesAreInfo_FaultsStayWarning(string line, Log.DiagRoute expected)
        => Assert.Equal(expected, Log.RouteFor(line));

    // ── G8: the live export ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExportText_UsesFileLineShape_WithTimestamp()
    {
        var entries = new[]
        {
            new WaveeLogEntry(1, 1_720_512_345_678, WaveeLogLevel.Info, "app", "startup", "Wavee starting", null, 4, -1, null, null),
            new WaveeLogEntry(2, 1_720_512_345_900, WaveeLogLevel.Warning, "connect", "", "dealer reconnect", null, 9, -1, null, null),
        };

        string text = Diagnostics.LogView.ExportText(entries);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);                                                            // oldest first, one per entry
        Assert.Equal(Log.FormatFileLine(in entries[0]), lines[0]);                                // the file's own shape, byte for byte
        Assert.Equal(Log.FormatFileLine(in entries[1]), lines[1]);
        Assert.Contains(" t=1720512345678 ", lines[0], StringComparison.Ordinal);                 // the timestamp CopyText drops
        Assert.Contains(" tid=9 ", lines[1], StringComparison.Ordinal);
        Assert.Contains(" sid=" + Log.SessionId + " ", lines[1], StringComparison.Ordinal);
        Assert.True(WaveeLogSessions.TryParseLine(lines[1], out var back, out _, out _, out _));  // and it parses back like a log line
        Assert.Equal(WaveeLogLevel.Warning, back.Level);
        Assert.Equal("", Diagnostics.LogView.ExportText([]));
    }

    // ── the --log-sessions probe's report ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LogSessionsProbe_ReportNamesEveryCountTheWalkSaw()
    {
        var sessions = new List<WaveeLogSessions.Info>();
        for (int i = 0; i < 12; i++) sessions.Add(new WaveeLogSessions.Info([], 0, 0, 0, 1, 1_790_951_509_732 + i, 4000 + i, 100 + i, "abcdef0" + (i % 10)));
        var walk = new WaveeLogSessions.WalkResult(sessions, 8, 47_172, 1, null);

        string[] report = Diagnostics.LogSessionsReport.Format(Base, walk, 41.26, TimeSpan.Zero);

        Assert.Contains("basePath    : " + Base, report);
        Assert.Contains("files seen  : 8", report);
        Assert.Contains("lines seen  : 47172", report);
        Assert.Contains("files skipped: 1", report);
        Assert.Contains("sessions    : 12", report);
        Assert.Contains("ms          : 41.3", report);
        Assert.Equal(Diagnostics.LogSessionsReport.MaxListed, report.Count(l => l.Contains(" · sid ", StringComparison.Ordinal)));
        Assert.Contains(report, l => l.Contains("pid 4000", StringComparison.Ordinal) && l.Contains("sid abcdef00", StringComparison.Ordinal));
        Assert.Contains("  … and 2 more", report);
        Assert.DoesNotContain(report, l => l.StartsWith("Error", StringComparison.Ordinal));

        var failed = Diagnostics.LogSessionsReport.Format(null, new WaveeLogSessions.WalkResult([], 0, 0, 0, new IOException("disk gone")), 1, TimeSpan.Zero);
        Assert.Contains("basePath    : (log not configured)", failed);
        Assert.Contains("Error       : IOException: disk gone", failed);
    }

    static int CountOccurrences(string text, string token)
    {
        int count = 0;
        for (int at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
