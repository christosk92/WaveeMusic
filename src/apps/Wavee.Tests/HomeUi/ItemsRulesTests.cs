// ── Wavee.Tests/HomeUi/ItemsRulesTests.cs — the week-sparkline bar height rule ──────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class ItemsRulesTests
{
    [Fact]
    public void WeekBarHeight_ScalesAgainstPeak()
    {
        Assert.Equal(16f, ItemsRules.WeekBarHeight(10, 10, 16f), 3);
        Assert.Equal(8f, ItemsRules.WeekBarHeight(5, 10, 16f), 3);
    }

    [Fact]
    public void WeekBarHeight_ZeroCount_FloorsToOnePixel()
    {
        Assert.Equal(1f, ItemsRules.WeekBarHeight(0, 10, 16f));
    }

    [Fact]
    public void WeekBarHeight_SilentWeek_NeverDividesByZero()
    {
        Assert.Equal(1f, ItemsRules.WeekBarHeight(0, 0, 16f));
        Assert.Equal(1f, ItemsRules.WeekBarHeight(0, -3, 16f));
    }

    [Fact]
    public void WeekBarHeight_NegativeCount_FloorsToZeroThenOnePixel()
    {
        Assert.Equal(1f, ItemsRules.WeekBarHeight(-4, 10, 16f));
    }

    [Fact]
    public void CardMeta_OwnerAndTrackCount_JoinsBothWithADot()
        => Assert.Equal($"Spotify · {Strings.Detail.SongCount(200)}", CardMeta.Of("Spotify", 200));

    [Fact]
    public void CardMeta_TrackCountAlone_IsJustTheSongCount()
        => Assert.Equal(Strings.Detail.SongCount(200), CardMeta.Of(null, 200));

    [Fact]
    public void CardMeta_OwnerAlone_IsJustTheOwner()
        => Assert.Equal("Spotify", CardMeta.Of("Spotify", 0));

    [Fact]
    public void CardMeta_NeitherOwnerNorTrackCount_IsNull()
        => Assert.Null(CardMeta.Of(null, 0));

    [Fact]
    public void CardMeta_EmptyOwnerString_IsTreatedAsNoOwner()
        => Assert.Equal(Strings.Detail.SongCount(5), CardMeta.Of("", 5));

    // DaylistArt's cases live in DaylistFormTests (the art source is a plain url since the seventh pass).
}
