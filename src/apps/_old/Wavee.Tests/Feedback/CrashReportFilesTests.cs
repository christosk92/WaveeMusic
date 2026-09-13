using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Wavee.Tests;

// CrashReportFiles (Diagnostics/CrashReportFiles.cs): the pure directory listing the "Crash reports" Settings card
// and ReportComposer's WerDump/UncleanExit path both read. Same newest-first-by-file-name rule as CrashReport.Prune
// (never mtime -- a copy or restore that resets timestamps must not reorder the list), against TEMP directories only.
public sealed class CrashReportFilesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-crash-report-files-tests", Guid.NewGuid().ToString("n"));

    public CrashReportFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Writes <paramref name="count"/> reports whose <c>yyyyMMdd-HHmmss</c> stamps are one minute apart,
    /// oldest first, and returns the file names in that (oldest-first) order.</summary>
    string[] SeedReports(int count)
    {
        var start = new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc);
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            DateTime when = start.AddMinutes(i);
            names[i] = "crash-report-" + when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
            File.WriteAllText(Path.Combine(_dir, names[i]), "report " + i);
        }
        return names;
    }

    [Fact]
    public void List_ReturnsTheTenNewest_NameOrdered_ExcludingAStrayFile()
    {
        string[] seeded = SeedReports(12);   // oldest..newest
        File.WriteAllText(Path.Combine(_dir, "crash-report-not-a-stamp.txt"), "stray");   // doesn't match the stamp pattern
        File.WriteAllText(Path.Combine(_dir, "wavee.log"), "unrelated log");

        var result = CrashReportFiles.List(_dir, max: 10);

        Assert.Equal(10, result.Length);
        // newest-first: the last 10 of the seeded (oldest-first) names, reversed.
        string[] expectedNamesNewestFirst = seeded.Skip(2).Reverse().ToArray();
        Assert.Equal(expectedNamesNewestFirst, result.Select(r => Path.GetFileName(r.Path)).ToArray());
        Assert.All(result, r => Assert.DoesNotContain("stray", File.ReadAllText(r.Path)));
    }

    [Fact]
    public void List_DefaultMax_IsTen()
    {
        SeedReports(15);

        var result = CrashReportFiles.List(_dir);

        Assert.Equal(10, result.Length);
    }

    [Fact]
    public void List_BelowTheCap_ReturnsEveryReport()
    {
        string[] seeded = SeedReports(3);

        var result = CrashReportFiles.List(_dir, max: 10);

        Assert.Equal(3, result.Length);
        Assert.Equal(seeded.Reverse().ToArray(), result.Select(r => Path.GetFileName(r.Path)).ToArray());
    }

    [Fact]
    public void List_EachEntry_CarriesTheStampParsedFromItsFileName()
    {
        SeedReports(1);   // "crash-report-20260304-090000.txt"

        var result = CrashReportFiles.List(_dir, max: 10);

        var entry = Assert.Single(result);
        Assert.Equal(new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc), entry.Stamp);
    }

    [Fact]
    public void List_OnAMissingDirectory_ReturnsEmpty()
    {
        var result = CrashReportFiles.List(Path.Combine(_dir, "does-not-exist"), max: 10);
        Assert.Empty(result);
    }

    [Fact]
    public void List_IgnoresUnrelatedFilesEntirely_OnAnOtherwiseEmptyDirectory()
    {
        File.WriteAllText(Path.Combine(_dir, "wavee.log"), "log");
        File.WriteAllText(Path.Combine(_dir, "crash-report-bad-name.txt"), "not a stamp");

        var result = CrashReportFiles.List(_dir, max: 10);

        Assert.Empty(result);
    }

    // ── TryStamp ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryStamp_ParsesAWellFormedReportName()
    {
        bool ok = CrashReportFiles.TryStamp("crash-report-20260304-090000.txt", out DateTime stamp);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc), stamp);
    }

    [Theory]
    [InlineData("crash-report-not-a-stamp.txt")]
    [InlineData("wavee.log")]
    [InlineData("crash-report-20260304-090000.log")]   // wrong extension
    [InlineData("")]
    public void TryStamp_FailsForAnythingElse(string name)
        => Assert.False(CrashReportFiles.TryStamp(name, out _));
}
