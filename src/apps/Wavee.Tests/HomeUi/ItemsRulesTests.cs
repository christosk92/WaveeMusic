// ── Wavee.Tests/HomeUi/ItemsRulesTests.cs — the shelf card's meta line (CardMeta) ─────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class ItemsRulesTests
{
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
