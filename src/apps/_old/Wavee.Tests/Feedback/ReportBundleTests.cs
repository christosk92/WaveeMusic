using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Wavee.Tests;

// ReportBundle (Diagnostics/ReportBundle.cs): the single text blob that goes on the clipboard, into
// wavee-report-<stamp>.txt, and (as a preview) into the dialog -- built ONLY from already-redacted inputs. Pins the
// MaxBytes budget (with the newest log lines kept), the includeLogs gate, FileName, Preview, and the crash-report
// splitter every crash prompt runs the saved crash-report-*.txt through.
public class ReportBundleTests
{
    static ReportIdentity Id() => new("0.2.5 Breaker (0.2.5.6) · 7e209e37", ReportChannels.InstallSources[0],
        "x64", "Windows 11 (build 26100)", "0.2.5.6", "7e209e37", "stable");

    // ── Constants ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constants_MatchTheDocumentedBudget()
    {
        Assert.Equal(60 * 1024, ReportBundle.MaxBytes);
        Assert.Equal(300, ReportBundle.CrashLogLines);
        Assert.Equal(200, ReportBundle.ManualLogLines);
        Assert.Equal(12_000, ReportBundle.PreviewChars);
    }

    // ── FileName ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileName_FormatsTheStamp()
    {
        var stamp = new DateTimeOffset(2026, 9, 1, 10, 15, 0, TimeSpan.Zero);
        Assert.Equal("wavee-report-20260901-101500.txt", ReportBundle.FileName(stamp));
    }

    // ── Preview ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_ReturnsTheBundleUnchanged_WhenUnderTheCharLimit()
    {
        string bundle = new string('a', ReportBundle.PreviewChars);   // exactly at the limit
        Assert.Equal(bundle, ReportBundle.Preview(bundle));
    }

    [Fact]
    public void Preview_TruncatesWithATrailingKbCount_WhenOverTheLimit()
    {
        string bundle = new string('a', ReportBundle.PreviewChars + 5000);
        string preview = ReportBundle.Preview(bundle);

        int expectedMoreKb = ((bundle.Length - ReportBundle.PreviewChars) / 1024);
        Assert.Equal(bundle[..ReportBundle.PreviewChars] + "\n… (" + expectedMoreKb + " KB more in the copied report)", preview);
    }

    // ── Build: header + answers ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_MentionsTheKind_AndTheIdentityFacts()
    {
        var bundle = ReportBundle.Build(ReportKind.Crash, Id(),
            answers: Array.Empty<(string Label, string Text)>(),
            diagnostics: "", crashHead: null, logLines: Array.Empty<string>(), logSource: "this session",
            includeLogs: false, now: DateTimeOffset.UtcNow);

        Assert.Contains("Crash", bundle);
        Assert.Contains("0.2.5.6", bundle);   // the quad, part of VersionLine
    }

    [Fact]
    public void Build_IncludesEachNonEmptyAnswer_AsLabelThenText()
    {
        var answers = new[] { ("What happened", "The app closed with no error"), ("Steps", "") };

        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), answers, "", null, Array.Empty<string>(), "this session",
            includeLogs: false, now: DateTimeOffset.UtcNow);

        Assert.Contains("What happened", bundle);
        Assert.Contains("The app closed with no error", bundle);
        // An empty answer contributes nothing -- its own label must not show up as a dangling, empty section.
        Assert.DoesNotContain("Steps:\n\n", bundle);
    }

    // ── Build: crash head / diagnostics / includeLogs gate ────────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesTheCrashHead_WhenProvided()
    {
        string head = "Exception\n---------\nSystem.InvalidOperationException: boom";

        var bundle = ReportBundle.Build(ReportKind.Crash, Id(), Array.Empty<(string, string)>(), "", head,
            Array.Empty<string>(), "this session", includeLogs: true, now: DateTimeOffset.UtcNow);

        Assert.Contains("InvalidOperationException", bundle);
    }

    [Fact]
    public void Build_IncludeLogsFalse_OmitsTheFencedLogExcerpt()
    {
        var lines = new[] { "log line one", "log line two" };

        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), Array.Empty<(string, string)>(), "some diagnostics text",
            null, lines, "this session", includeLogs: false, now: DateTimeOffset.UtcNow);

        Assert.DoesNotContain("```", bundle);
        Assert.DoesNotContain("log line one", bundle);
        Assert.DoesNotContain("log line two", bundle);
    }

    [Fact]
    public void Build_IncludeLogsTrue_FencesTheExcerptAndNamesTheSource()
    {
        var lines = new[] { "log line one", "log line two" };

        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), Array.Empty<(string, string)>(), "", null,
            lines, "this session", includeLogs: true, now: DateTimeOffset.UtcNow);

        Assert.Contains("```", bundle);
        Assert.Contains("this session", bundle);
        Assert.Contains("log line one", bundle);
        Assert.Contains("log line two", bundle);
    }

    // ── Build: the MaxBytes budget drops OLDER lines, keeping the newest ─────────────────────────────────────────────

    [Fact]
    public void Build_2000LinesOf100Bytes_StaysUnderMaxBytes_AndKeepsTheNewestLine()
    {
        var lines = new List<string>(2000);
        for (int i = 0; i < 2000; i++)
            lines.Add($"line-{i:D4} " + new string('x', 90));   // exactly 100 chars per line

        var bundle = ReportBundle.Build(ReportKind.Crash, Id(), Array.Empty<(string, string)>(), "", null,
            lines, "this session", includeLogs: true, now: DateTimeOffset.UtcNow);

        Assert.True(Encoding.UTF8.GetByteCount(bundle) <= ReportBundle.MaxBytes,
            $"bundle was {Encoding.UTF8.GetByteCount(bundle)} bytes, over the {ReportBundle.MaxBytes}-byte budget");
        Assert.Contains("line-1999 ", bundle);         // the newest line must survive
        Assert.DoesNotContain("line-0000 ", bundle);   // the oldest must have been dropped to make room
        Assert.Contains("truncated", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FewShortLines_NeedsNoTruncation()
    {
        var lines = new[] { "line one", "line two", "line three" };

        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), Array.Empty<(string, string)>(), "", null,
            lines, "this session", includeLogs: true, now: DateTimeOffset.UtcNow);

        Assert.Contains("line one", bundle);
        Assert.Contains("line two", bundle);
        Assert.Contains("line three", bundle);
        Assert.DoesNotContain("truncated", bundle, StringComparison.OrdinalIgnoreCase);
    }

    // ── SplitCrashReport ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SplitCrashReport_KeepsFramesInTheHead_AndTheLast300OfA700LineTail()
    {
        string head = string.Join('\n', new[]
        {
            "Wavee crash report",
            "=================",
            "version=0.2.5.6",
            "commit=7e209e37",
            "buildDate=2026-09-01T00:00:00Z",
            "channel=stable",
            "quad=0.2.5.6",
            "arch=x64",
            "timeLocal=2026-09-01T10:15:00+00:00",
            "pid=1234",
            "framework=.NET 10.0",
            "os=Windows",
            "module=C:\\Wavee.exe base=0x140000000 size=0x1000000",
            "",
            "Exception",
            "---------",
            "System.InvalidOperationException: boom",
            "   at Wavee!<BaseAddress>+0x7b1fc6",
            "",
            "Frames (RVA)",
            "------------",
            "# offsets from the module base above, in stack-trace order (innermost first);",
            "# resolve each with `ln Wavee+0x<rva>` against the release's Wavee-<quad>-<rid>-symbols.zip",
            "0x7b1fc6",
        });

        var tailLines = Enumerable.Range(0, 700).Select(i => $"line {i}").ToArray();
        string fullText = head + "\nwavee.log tail\n--------------\n" + string.Join('\n', tailLines);

        var (splitHead, splitTail) = ReportBundle.SplitCrashReport(fullText);

        Assert.Contains("Frames (RVA)", splitHead);
        Assert.Contains("System.InvalidOperationException: boom", splitHead);
        Assert.DoesNotContain("wavee.log tail", splitHead);
        Assert.DoesNotContain("line 699", splitHead);

        Assert.Equal(300, splitTail.Length);
        Assert.Equal("line 400", splitTail[0]);      // the OLDEST of the kept 300 -- lines 0..399 were dropped
        Assert.Equal("line 699", splitTail[^1]);      // the newest line survives
        Assert.DoesNotContain("line 399", splitTail);
    }

    [Fact]
    public void SplitCrashReport_TailShorterThanTheCap_KeepsItWhole()
    {
        string head = "Wavee crash report\n=================\nversion=0.2.5.6";
        var tailLines = Enumerable.Range(0, 5).Select(i => $"line {i}").ToArray();
        string fullText = head + "\nwavee.log tail\n--------------\n" + string.Join('\n', tailLines);

        var (_, splitTail) = ReportBundle.SplitCrashReport(fullText);

        Assert.Equal(tailLines, splitTail);
    }
}
