// ── Wavee.Tests/RoutineSummaryTests.cs — the steady-state log policy (Platform/Log.Summary.cs) ──────────────────────────
//
// A routine wire call / audio range only COUNTS; one summary line per window. Pure: an injected clock and writer, so no log is
// touched. Also the three rules around it: which wire calls are routine, when the periodic memory sample is due, and the
// device-underrun baseline across sessions.

using Xunit;

namespace Wavee.Tests;

public class RoutineSummaryTests
{
    sealed class Rig
    {
        public long Now;
        public readonly List<string> Lines = new();
        public readonly RoutineSummary Summary;
        public Rig(long windowMs = 60_000) => Summary = RoutineSummary.Standalone("wire", "wire.summary", windowMs, (_, t) => { lock (Lines) Lines.Add(t); }, () => Interlocked.Read(ref Now));
    }

    [Fact]
    public void Window_Rolls_AndReportsCountBytesAverageAndMax()
    {
        var r = new Rig();
        r.Summary.Note(100, 10);
        r.Summary.Note(300, 30);
        Assert.Empty(r.Lines);                       // inside the window: nothing written
        r.Now = 60_000;
        r.Summary.Note(50, 20);                      // the window has elapsed: this note closes it (it counts in the window it closes)
        Assert.Single(r.Lines);
        Assert.Equal("wire.summary window=60s n=3 bytes=450 avgMs=20 maxMs=30", r.Lines[0]);
        r.Now = 90_000;
        r.Summary.Flush();                           // a fresh window: nothing pending
        Assert.Single(r.Lines);
    }

    [Fact]
    public void ForcedFlush_WritesAPartialWindow_WithItsRealLength()
    {
        var r = new Rig();
        r.Summary.Note(10, 5);
        r.Now = 7_000;
        r.Summary.Flush();                           // not due and not forced
        Assert.Empty(r.Lines);
        r.Summary.Flush(force: true);
        Assert.Single(r.Lines);
        Assert.StartsWith("wire.summary window=7s n=1 bytes=10", r.Lines[0]);
    }

    [Fact]
    public void ZeroCount_EmitsNothing_EvenWhenForced()
    {
        var r = new Rig();
        r.Now = 600_000;
        r.Summary.Flush(force: true);
        r.Summary.Flush();
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void ConcurrentNotes_AreAllCounted()
    {
        var r = new Rig(windowMs: long.MaxValue);
        Parallel.For(0, 8, _ => { for (int i = 0; i < 1000; i++) r.Summary.Note(1, 1); });
        r.Summary.Flush(force: true);
        Assert.Single(r.Lines);
        Assert.StartsWith("wire.summary window=1s n=8000 bytes=8000", r.Lines[0]);
    }

    [Theory]
    [InlineData(200, false, 20.0, true)]
    [InlineData(304, false, 20.0, true)]
    [InlineData(404, false, 20.0, false)]       // a failing status keeps its own line
    [InlineData(500, false, 20.0, false)]
    [InlineData(200, true, 20.0, false)]        // a transport error keeps its own line
    [InlineData(200, false, 1500.0, false)]     // slow keeps its own line
    [InlineData(200, false, 1499.0, true)]
    public void WireRules_IsRoutine(int status, bool error, double ms, bool expected)
        => Assert.Equal(expected, WireRules.IsRoutine(status, error, ms));

    [Theory]
    [InlineData(4_999.0, 0L, false)]            // before the 5 s check nothing is due
    [InlineData(5_000.0, 0L, false)]            // checked, steady working set: wait for 30 s
    [InlineData(29_999.0, 63L << 20, false)]
    [InlineData(5_000.0, 64L << 20, true)]      // grew 64 MB: sample within the check
    [InlineData(5_000.0, -(64L << 20), true)]   // shrank 64 MB: likewise
    [InlineData(30_000.0, 0L, true)]            // steady cadence
    public void MemorySamplePolicy_PeriodicDue(double sinceMs, long deltaBytes, bool expected)
        => Assert.Equal(expected, MemorySamplePolicy.PeriodicDue(sinceMs, deltaBytes));

    [Fact]
    public void DeviceUnderrunBaseline_CountsOnlyNewOnes_PerSession_AndSurvivesAMonotonicSwap()
    {
        var b = new DeviceUnderrunBaseline();
        var a = new object();
        Assert.Equal(0, b.Take(a, 5));              // first look at a session only baselines
        Assert.Equal(0, b.Take(a, 5));
        Assert.Equal(2, b.Take(a, 7));
        // A device switch inside the session: the engine folds the old sink's count into the session total, so it keeps growing.
        Assert.Equal(1, b.Take(a, 8));
        Assert.Equal(0, b.Take(new object(), 3));   // a new session re-baselines
        Assert.Equal(0, b.Take(a, 1));              // and a total that went down never reports a negative
    }

    [Fact]
    public void GlitchLedger_DeviceUnderruns_AreIncidentsWithADeviceVerdict()
    {
        var ledger = new GlitchLedger();
        Assert.Equal(GlitchLedger.VerdictClean, ledger.Read().Verdict);
        ledger.RecordDeviceUnderruns(3, 1_000);
        var s = ledger.Read();
        Assert.Equal(3, s.Incidents);
        Assert.Equal(3, s.DeviceLate);
        Assert.Equal(GlitchLedger.VerdictDeviceLate, s.Verdict);
    }
}
