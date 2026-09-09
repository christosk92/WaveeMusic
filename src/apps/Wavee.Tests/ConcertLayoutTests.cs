using Wavee.Features.Concerts;
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
    public void ConcertBars_StayInsideThePaneAtTheReportedArtworkFloor()
    {
        // ConcertLayout.WideEditorial's narrowest tier floors ArtworkMin at 180×220 — the bars must never spill past
        // the pane at that floor, since EditorialArt clips to the card's own rounded corners but nothing clips the
        // bars to each other or reserves extra headroom for them.
        float w = 180f, h = 220f;
        foreach (var bar in EditorialArtGeometry.ConcertBars(w, h))
        {
            Assert.InRange(bar.X, 0f, w);
            Assert.InRange(bar.Y, 0f, h);
            Assert.InRange(bar.X + bar.Width, 0f, w);
            Assert.InRange(bar.Y + bar.Height, 0f, h);
        }
    }

    [Fact]
    public void ConcertBars_IsDeterministicForTheSamePaneSize()
    {
        var a = EditorialArtGeometry.ConcertBars(320f, 288f);
        var b = EditorialArtGeometry.ConcertBars(320f, 288f);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ConcertBars_VaryInHeightAndShareABaseline()
    {
        // A visible peak-in-the-middle silhouette (an equalizer read), all bars resting on the same bottom edge.
        var bars = EditorialArtGeometry.ConcertBars(320f, 288f);
        Assert.True(bars.Length is >= 3 and <= 5);
        Assert.True(bars[2].Height > bars[0].Height);
        Assert.True(bars[2].Height > bars[3].Height);
        float baseline = bars[0].Y + bars[0].Height;
        foreach (var bar in bars) Assert.Equal(baseline, bar.Y + bar.Height, 2);
    }

    [Fact]
    public void BrowseTiles_StayInsideThePaneAtTheReportedArtworkFloor()
    {
        // ConcertLayout.WideEditorial's narrowest tier floors ArtworkMin at 180×220 — the tiles must never spill past
        // the pane at that floor, since EditorialArt clips to the card's own rounded corners but nothing clips the
        // tiles to each other or reserves extra headroom for them.
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
}
