// ── Wavee.Tests/HomeUi/DaylistFormTests.cs — the daylist card's form, art source, daypart timeline and countdown line ──

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class DaylistFormTests
{
    // ── DaylistForm: the art shows while 160 of it fits beside the 340 text column and the 32 gap ──

    [Fact]
    public void ShowArt_AtTheOwnersWidth_LeavesTheArt278()
    {
        Assert.True(DaylistForm.ShowArt(650f));
        Assert.Equal(278f, 650f - DaylistForm.Gap - DaylistForm.TextMin);
        Assert.True(650f - DaylistForm.Gap - DaylistForm.TextMin >= DaylistForm.ArtMin);
    }

    [Fact]
    public void ShowArt_Narrow_DropsTheArt() => Assert.False(DaylistForm.ShowArt(480f));

    [Fact]
    public void ShowArt_Boundary_IsInclusive()
    {
        Assert.True(DaylistForm.ShowArt(532f));
        Assert.False(DaylistForm.ShowArt(531.9f));
    }

    [Fact]
    public void TextMinFor_BesideTheArt_IsTheTextFloor() => Assert.Equal(340f, DaylistForm.TextMinFor(650f));

    [Fact]
    public void TextMinFor_Alone_NeverExceedsTheCard()
    {
        Assert.Equal(300f, DaylistForm.TextMinFor(300f));
        Assert.Equal(340f, DaylistForm.TextMinFor(480f));
        Assert.Equal(0f, DaylistForm.TextMinFor(-5f));
    }

    // ── DaylistForm.TextWidth / UseHeroTitle: a narrow text column steps the title down from 40/52 to 28/36 ──

    [Fact]
    public void TextWidth_AtTheOwnersWidth_IsTheFloor_AndStepsTheTitleDown()
    {
        Assert.True(DaylistForm.ShowArt(588f));
        Assert.Equal(340f, DaylistForm.TextWidth(588f));             // 588 − 32 − 540 = 16 < 340
        Assert.False(DaylistForm.UseHeroTitle(DaylistForm.TextWidth(588f)));
    }

    [Fact]
    public void TextWidth_Wide_TakesWhatTheArtBasisLeaves_AndKeepsTheHero()
    {
        Assert.Equal(700f, DaylistForm.TextWidth(1272f));            // 1272 − 32 − 540
        Assert.True(DaylistForm.UseHeroTitle(DaylistForm.TextWidth(1272f)));
    }

    [Fact]
    public void UseHeroTitle_Boundary_IsInclusive()
    {
        Assert.True(DaylistForm.UseHeroTitle(480f));
        Assert.False(DaylistForm.UseHeroTitle(479.9f));
    }

    [Fact]
    public void TextWidth_WithoutTheArt_IsTheWholeContentWidth()
    {
        Assert.False(DaylistForm.ShowArt(500f));
        Assert.Equal(500f, DaylistForm.TextWidth(500f));
        Assert.True(DaylistForm.UseHeroTitle(DaylistForm.TextWidth(500f)));
        Assert.False(DaylistForm.UseHeroTitle(DaylistForm.TextWidth(400f)));
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
