using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// <c>CrashReport.Prune</c> against TEMP directories only. A crash loop writes one report per crash, so without a cap the
/// log folder grows without bound; the cap has to keep the NEWEST reports (the ones describing the crash the user is
/// actually hitting) and drop the oldest.
/// </summary>
public sealed class CrashReportRetentionTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-crash-retention-tests", Guid.NewGuid().ToString("n"));

    public CrashReportRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Writes <paramref name="count"/> reports whose <c>yyyyMMdd-HHmmss</c> stamps are one minute apart, oldest
    /// first, and returns the file names in that order.</summary>
    string[] SeedReports(int count)
    {
        var start = new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc);
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            DateTime when = start.AddMinutes(i);
            names[i] = "crash-report-" + when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
            string path = Path.Combine(_dir, names[i]);
            File.WriteAllText(path, "report " + i);
            File.SetLastWriteTimeUtc(path, when);
        }
        return names;
    }

    string[] PresentNames() =>
        new DirectoryInfo(_dir).GetFiles().Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Prune_KeepsTheTenNewestReports()
    {
        string[] seeded = SeedReports(13);

        CrashReport.Prune(_dir);

        Assert.Equal(seeded.Skip(3).ToArray(), PresentNames());
    }

    [Fact]
    public void Prune_HonoursAnExplicitKeepCount()
    {
        string[] seeded = SeedReports(13);

        CrashReport.Prune(_dir, keep: 4);

        Assert.Equal(seeded.Skip(9).ToArray(), PresentNames());
    }

    [Fact]
    public void Prune_IsANoOpBelowTheCap()
    {
        string[] seeded = SeedReports(3);

        CrashReport.Prune(_dir);

        Assert.Equal(seeded, PresentNames());
    }

    [Fact]
    public void Prune_LeavesUnrelatedFilesAlone()
    {
        SeedReports(12);
        File.WriteAllText(Path.Combine(_dir, "wavee.log"), "log");
        File.WriteAllText(Path.Combine(_dir, "wavee-20260304.log"), "log");

        CrashReport.Prune(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "wavee.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "wavee-20260304.log")));
        Assert.Equal(10, Directory.GetFiles(_dir, "crash-report-*.txt").Length);
    }

    [Fact]
    public void Prune_IsANoOpOnAMissingDirectory()
    {
        CrashReport.Prune(Path.Combine(_dir, "does-not-exist"));
        Assert.True(Directory.Exists(_dir));
    }
}
