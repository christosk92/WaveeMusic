// ── Wavee.Tests/HomeUi/DaylistFormTests.cs — the daylist card's form, art source, daypart timeline and countdown line ──

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class DaylistFormTests
{
    // ── DaylistForm.CopyWidth: half the content width within [340, 560]; the whole width below 532 ──

    [Fact]
    public void CopyWidth_Floor_HoldsTheActionRow()
    {
        Assert.Equal(340f, DaylistForm.CopyWidth(600f));             // 600 × 0.5 = 300 < 340
        Assert.Equal(340f, DaylistForm.CopyWidth(680f));             // exactly the floor
    }

    [Fact]
    public void CopyWidth_Half_BetweenTheFloorAndTheCeiling()
        => Assert.Equal(482f, DaylistForm.CopyWidth(964f));          // a 1008 card minus the copy's 32 + 12 side insets

    [Fact]
    public void CopyWidth_Max_CapsAWideCard()
    {
        Assert.Equal(560f, DaylistForm.CopyWidth(1120f));            // exactly the ceiling
        Assert.Equal(560f, DaylistForm.CopyWidth(1800f));
    }

    [Fact]
    public void CopyWidth_Narrow_TakesTheWholeWidth()
    {
        Assert.Equal(500f, DaylistForm.CopyWidth(500f));
        Assert.Equal(300f, DaylistForm.CopyWidth(300f));
        Assert.Equal(0f, DaylistForm.CopyWidth(-5f));
    }

    [Fact]
    public void CopyWidth_SplitBoundary_IsInclusive()
    {
        Assert.Equal(340f, DaylistForm.CopyWidth(DaylistForm.SplitMin));
        Assert.Equal(531.9f, DaylistForm.CopyWidth(531.9f));
    }

    // ── UseHeroTitle over CopyWidth: a narrow copy steps the title down from 40/52 to 28/36 ──

    [Fact]
    public void Title_AtTheFloor_StepsDown() => Assert.False(DaylistForm.UseHeroTitle(DaylistForm.CopyWidth(600f)));

    [Fact]
    public void Title_WideCard_KeepsTheHero() => Assert.True(DaylistForm.UseHeroTitle(DaylistForm.CopyWidth(964f)));

    [Fact]
    public void UseHeroTitle_Boundary_IsInclusive()
    {
        Assert.True(DaylistForm.UseHeroTitle(480f));
        Assert.False(DaylistForm.UseHeroTitle(479.9f));
    }

    [Fact]
    public void Title_NarrowCard_FollowsTheWholeWidth()
    {
        Assert.True(DaylistForm.UseHeroTitle(DaylistForm.CopyWidth(500f)));
        Assert.False(DaylistForm.UseHeroTitle(DaylistForm.CopyWidth(400f)));
    }

    // ── DaylistArt: header image, else cover, else nothing ──

    [Fact]
    public void Art_HeaderImage_Wins() => Assert.Equal("https://img/header.jpg", DaylistArt.Of("https://img/header.jpg", "https://img/cover.jpg"));

    [Fact]
    public void Art_NoHeader_FallsBackToTheCover() => Assert.Equal("https://img/cover.jpg", DaylistArt.Of("", "https://img/cover.jpg"));

    [Fact]
    public void Art_NoArtAtAll_IsNull() => Assert.Null(DaylistArt.Of(null, ""));

    // ── DaypartTimeline: done 1 / current = elapsed / future 0 ──

    [Theory]
    [InlineData(0, 2, 0.4f, 1f)]
    [InlineData(1, 2, 0.4f, 1f)]
    [InlineData(2, 2, 0.4f, 0.4f)]
    [InlineData(3, 2, 0.4f, 0f)]
    [InlineData(4, 2, 0.4f, 0f)]
    public void Fill_DoneCurrentFuture(int segment, int current, float elapsed, float expected)
        => Assert.Equal(expected, DaypartTimeline.Fill(segment, current, elapsed));

    [Fact]
    public void Fill_ClampsTheCurrentSegment()
    {
        Assert.Equal(1f, DaypartTimeline.Fill(1, 1, 1.7f));
        Assert.Equal(0f, DaypartTimeline.Fill(1, 1, -0.3f));
    }

    [Fact]
    public void Current_TitleTokenWinsOverTheHour()
        => Assert.Equal(Daypart.Evening, DaypartTimeline.Current("cutesy korean r&b sunday evening", 9));

    [Fact]
    public void Current_NoTitleToken_UsesTheHour()
        => Assert.Equal(Daypart.Afternoon, DaypartTimeline.Current("cutesy korean r&b", 14));

    [Fact]
    public void CellWidth_FiveEqualCellsAroundFourGaps()
    {
        Assert.Equal(64.8f, DaypartTimeline.CellWidth(340f), 3);
        Assert.Equal(0f, DaypartTimeline.CellWidth(10f));
    }

    [Fact]
    public void LabelKey_FollowsTheDaypartOrder()
    {
        Assert.Equal(Strings.Home.Daypart.Early, DaypartTimeline.LabelKey((int)Daypart.EarlyMorning));
        Assert.Equal(Strings.Home.Daypart.Morning, DaypartTimeline.LabelKey((int)Daypart.Morning));
        Assert.Equal(Strings.Home.Daypart.Afternoon, DaypartTimeline.LabelKey((int)Daypart.Afternoon));
        Assert.Equal(Strings.Home.Daypart.Evening, DaypartTimeline.LabelKey((int)Daypart.Evening));
        Assert.Equal(Strings.Home.Daypart.Night, DaypartTimeline.LabelKey((int)Daypart.Night));
    }

    // ── DaylistCountdownLine: the count cut out of the translated sentence ──

    [Fact]
    public void Split_CountAtTheEnd()
    {
        var (before, after) = DaylistCountdownLine.Split("Next daylist in " + DaylistCountdownLine.Slot);
        Assert.Equal("Next daylist in ", before);
        Assert.Equal("", after);
    }

    [Fact]
    public void Split_CountInTheMiddle()
    {
        var (before, after) = DaylistCountdownLine.Split("in " + DaylistCountdownLine.Slot + " komt");
        Assert.Equal("in ", before);
        Assert.Equal(" komt", after);
    }

    [Fact]
    public void Split_NoSlot_KeepsTheWholeTextBefore()
    {
        var (before, after) = DaylistCountdownLine.Split("[home.nextDaylistIn]");
        Assert.Equal("[home.nextDaylistIn]", before);
        Assert.Equal("", after);
    }
}
