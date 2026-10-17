// ── Wavee.Tests/ReportComposerTests.cs — the report dialog's composer and file I/O (C15, X38) ────────────────────────
//
// `Feedback.ReportComposer` picks the log source (a crash-report file, a past session through the `PastSessionLog` seam,
// or this session's ring), redacts the big text once, and writes the bundle. The bundle FORMAT and the redaction patterns
// are pinned in FeedbackTests; these facts pin the composer's source ladder and its write verbs. The seam is process
// state, so the class joins the platform collection and restores it.

using Xunit;
using static Wavee.Feedback;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class ReportComposerTests : IDisposable
{
    readonly Func<string?, IReadOnlyList<string>?>? _pastSessionLog = PastSessionLog;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-report-" + Guid.NewGuid().ToString("N"));

    public ReportComposerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PastSessionLog = _pastSessionLog;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static readonly ReportIdentity Identity = new("0.3.0 (0.3.0.1) · abc", ReportChannels.InstallSources[0], "x64",
        "Windows 11 (build 26100)", "0.3.0.1", "abc", "stable");

    static ComposeInput Input(ReportKind kind, ReportPrefill? prefill = null, string diagnostics = "diag", RedactionRules? rules = null)
        => new(kind, prefill, rules ?? RedactionRules.None, Identity, diagnostics);

    // ── the source ladder ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_prefilled_crash_report_file_is_the_source_and_its_tail_is_the_excerpt()
    {
        string path = Path.Combine(_dir, "crash.txt");
        File.WriteAllText(path, "System.InvalidOperationException: boom\n   at Wavee.Thing.Run()\n\nwavee.log tail (last 2)\n--------\nseq=1 first\nseq=2 second\n");

        var composed = ReportComposer.Compose(Input(ReportKind.Crash, new ReportPrefill(CrashReportPath: path)));

        Assert.Equal("crash report", composed.LogSource);
        Assert.Equal(path, composed.CrashReportPath);
        Assert.Equal(new[] { "seq=1 first", "seq=2 second" }, composed.LogLines);
        Assert.Contains("boom", composed.CrashHead);
        Assert.NotEqual("", composed.CrashTime);                                  // the file's last-write time for the InfoBar
        Assert.NotNull(composed.CrashSummary);
    }

    [Fact]
    public void A_crash_file_that_cannot_be_read_falls_through_to_the_next_source()
    {
        var composed = ReportComposer.Compose(Input(ReportKind.Crash,
            new ReportPrefill(CrashReportPath: Path.Combine(_dir, "missing.txt"))));

        Assert.Equal("this session", composed.LogSource);
        Assert.Null(composed.CrashHead);
    }

    [Fact]
    public void A_past_session_comes_through_the_seam_capped_to_the_kinds_line_count()
    {
        var raw = new List<string>();
        for (int i = 0; i < ReportBundle.ManualLogLines + 25; i++) raw.Add("seq=" + i + " line");
        PastSessionLog = id => id == "sess-1" ? raw : null;

        var composed = ReportComposer.Compose(Input(ReportKind.Bug, new ReportPrefill(PastSessionId: "sess-1")));

        Assert.Equal("previous session", composed.LogSource);
        Assert.Equal(ReportBundle.ManualLogLines, composed.LogLines.Length);
        Assert.Equal(raw[^1], composed.LogLines[^1]);                              // the newest entries survive the cap
        Assert.Null(composed.CrashHead);
    }

    [Fact]
    public void With_no_prefill_the_source_is_this_sessions_ring_within_the_manual_cap()
    {
        Log.Info("test", "report composer fixture line");

        var composed = ReportComposer.Compose(Input(ReportKind.Bug));

        Assert.Equal("this session", composed.LogSource);
        Assert.True(composed.LogLines.Length is > 0 and <= ReportBundle.ManualLogLines);
        Assert.Equal("", composed.CrashSummary);
        Assert.Equal("", composed.CrashTime);
    }

    [Fact]
    public void The_literals_the_rules_name_are_redacted_from_the_diagnostics_text()
    {
        var rules = new RedactionRules("zaphod", "BEEBLEBROX", null, null, null);

        var composed = ReportComposer.Compose(Input(ReportKind.Bug, diagnostics: "signed in as zaphod on BEEBLEBROX", rules: rules));

        Assert.DoesNotContain("zaphod", composed.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEEBLEBROX", composed.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Same(rules, composed.Rules);                                       // each keystroke's answers use the SAME rules
    }

    // ── the write verbs ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Saving_to_a_picked_path_writes_the_text_and_answers_no_failure()
    {
        string path = Path.Combine(_dir, "report.txt");

        Assert.Null(ReportComposer.TryWriteFile(path, "the report"));

        Assert.Equal("the report", File.ReadAllText(path));
    }

    [Fact]
    public void A_path_that_cannot_be_written_answers_the_exception_for_the_toast()
    {
        string path = Path.Combine(_dir, "no-such-folder", "report.txt");

        var failure = ReportComposer.TryWriteFile(path, "the report");

        Assert.NotNull(failure);
        Assert.False(File.Exists(path));
    }
}
