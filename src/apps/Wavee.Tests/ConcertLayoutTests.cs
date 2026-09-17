// ── Wavee.Tests/ConcertLayoutTests.cs — concert layout hysteresis and the editorial art geometry, ported from 0.2.9 ───
//
// Ported VERBATIM from `_old/Wavee.Tests/ConcertLayoutTests.cs` (9 facts). Home's editorial cards consume
// `EditorialArtGeometry` (ch 11); the rules live in `Entities/Concert.Rules.cs`.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ConcertLayoutTests
{
    [Fact]
    public void ScheduleWide_UsesSeparateEnterAndLeaveThresholds()
    {
        Assert.False(ConcertLayout.ScheduleWide(740f, wasWide: false));
        Assert.True(ConcertLayout.ScheduleWide(760f, wasWide: false));
        Assert.True(ConcertLayout.ScheduleWide(740f, wasWide: true));
        Assert.False(ConcertLayout.ScheduleWide(719f, wasWide: true));
    }

    [Fact]
    public void DetailWide_UsesSeparateEnterAndLeaveThresholds()
    {
        Assert.False(ConcertLayout.DetailWide(900f, wasWide: false));
        Assert.True(ConcertLayout.DetailWide(920f, wasWide: false));
        Assert.True(ConcertLayout.DetailWide(880f, wasWide: true));
        Assert.False(ConcertLayout.DetailWide(859f, wasWide: true));
    }

    [Theory]
    [InlineData(1000f, 288f, 28f, 3)]
    [InlineData(700f, 240f, 24f, 2)]
    [InlineData(420f, 220f, 20f, 2)]
    public void WideEditorial_ChangesMetricsWithoutChangingComposition(
        float width, float expectedHeight, float expectedPadding, int expectedLines)
    {
        var metrics = ConcertLayout.WideEditorial(width);

        Assert.Equal(expectedHeight, metrics.Height);
        Assert.Equal(expectedPadding, metrics.Padding);
        Assert.Equal(expectedLines, metrics.SubtitleLines);
        Assert.InRange(metrics.ArtworkWidth(width), metrics.ArtworkMin, Math.Min(metrics.ArtworkMax, width));
    }

    // ── #86 — the procedural promo-card art's geometry ──────────────────────────────────────────────────────────────

    [Fact]
    public void Arc_EndpointsLandExactlyRadiusFromCentre()
    {
        var sweep = EditorialArtGeometry.Arc(cx: 100f, cy: 80f, r: 40f, startDeg: -150f, sweepDeg: 120f);

        Assert.Equal(40f, Distance(sweep.X0, sweep.Y0, 100f, 80f), 2);
        Assert.Equal(40f, Distance(sweep.X1, sweep.Y1, 100f, 80f), 2);
        Assert.Equal(40f, sweep.Radius);
    }

    [Fact]
    public void Arc_FlagsMatchTheSweepDirectionAndSpan()
    {
        Assert.Equal(1, EditorialArtGeometry.Arc(0f, 0f, 10f, 0f, 90f).SweepFlag);
        Assert.Equal(0, EditorialArtGeometry.Arc(0f, 0f, 10f, 0f, -90f).SweepFlag);
        Assert.Equal(0, EditorialArtGeometry.Arc(0f, 0f, 10f, 0f, 90f).LargeArc);
        Assert.Equal(1, EditorialArtGeometry.Arc(0f, 0f, 10f, 0f, 200f).LargeArc);
    }

    [Fact]
    public void ArcSweep_ToPathData_IsCultureInvariant()
    {
        var sweep = new ArcSweep(1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 1, 0);
        string path = sweep.ToPathData();

        // A decimal-comma culture must never leak a "," where the path parser would read a coordinate separator. The
        // culture is CLONED from invariant (the app runs with InvariantGlobalization, so a named culture throws).
        var comma = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";

        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = comma;
        try { Assert.Equal(path, sweep.ToPathData()); }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }

        Assert.StartsWith("M1.50,2.50 A5.50,5.50 0 1,0 3.50,4.50", path, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcertArcs_SweepOppositeDirections()
    {
        var outer = EditorialArtGeometry.ConcertArcOuter(320f, 288f);
        var inner = EditorialArtGeometry.ConcertArcInner(320f, 288f);
        Assert.NotEqual(outer.SweepFlag, inner.SweepFlag);
        Assert.True(inner.Radius < outer.Radius);
    }

    [Fact]
    public void BrowseTiles_StayInsideThePaneAtTheReportedArtworkFloor()
    {
        float w = 180f, h = 220f;
        foreach (var tile in EditorialArtGeometry.BrowseTiles(w, h))
        {
            Assert.InRange(tile.X, 0f, w);
            Assert.InRange(tile.Y, 0f, h);
            Assert.InRange(tile.X + tile.Width, 0f, w);
            Assert.InRange(tile.Y + tile.Height, 0f, h);
        }
    }

    [Fact]
    public void BrowseTiles_IsDeterministicForTheSamePaneSize()
    {
        var a = EditorialArtGeometry.BrowseTiles(320f, 288f);
        var b = EditorialArtGeometry.BrowseTiles(320f, 288f);
        Assert.Equal(a, b);
    }

    static float Distance(float x0, float y0, float x1, float y1) => MathF.Sqrt(MathF.Pow(x1 - x0, 2) + MathF.Pow(y1 - y0, 2));
}
