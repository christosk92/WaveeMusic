// ── Wavee.Tests/PanelBitsContractTests.cs — the panel's lyrics header, "Next up" and the lyrics peek as keys (leftovers P3) ───────────
//
// The three pictures the core still drew until P3 are keys: `Panel.LyricsHeader`, `Panel.NextUp` and `Player.LyricsPeek`. Pure: the
// published ids, Classic's registration, and the peek's props being plain data (an unchanged pair is an equal record, so the view
// re-renders nothing).

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public sealed class PanelBitsContractTests
{
    [Fact]
    public void The_keys_carry_their_published_ids()
    {
        Assert.Equal("Panel.LyricsHeader", ViewKeys.PanelLyricsHeader.Id);
        Assert.Equal("Panel.NextUp", ViewKeys.PanelNextUp.Id);
        Assert.Equal("Player.LyricsPeek", ViewKeys.LyricsPeek.Id);
    }

    [Theory]
    [InlineData("Panel.LyricsHeader", "classic.panel.lyrics-header")]
    [InlineData("Panel.NextUp", "classic.panel.next-up")]
    [InlineData("Player.LyricsPeek", "classic.player.lyrics-peek")]
    public void Classic_draws_each(string id, string template)
        => Assert.Equal(template, ClassicTheme.Create().Registry.TemplateIdOf(id));

    [Fact]
    public void An_unchanged_peek_is_an_equal_record()
    {
        Action open = static () => { };
        var a = new LyricsPeekProps { Face = LyricsPeekFace.Reel, Active = 3, ActiveText = "la", Peek = 4, PeekText = "da", OpenLyrics = open };
        var b = new LyricsPeekProps { Face = LyricsPeekFace.Reel, Active = 3, ActiveText = "la", Peek = 4, PeekText = "da", OpenLyrics = open };
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Active = 4, ActiveText = "da", Peek = 5, PeekText = null });
    }

    [Fact]
    public void A_peek_with_no_pair_names_no_line()
    {
        var none = new LyricsPeekProps { Face = LyricsPeekFace.None, OpenLyrics = static () => { } };
        Assert.Equal(-1, none.Active);
        Assert.Equal(-1, none.Peek);
        Assert.Null(none.ActiveText);
        Assert.Null(none.PeekText);
    }
}
