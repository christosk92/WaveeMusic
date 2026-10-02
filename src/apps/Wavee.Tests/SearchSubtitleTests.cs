// ── Wavee.Tests/SearchSubtitleTests.cs — the one search-subtitle composer ──────────────────────────────────────────
//
// "Song · A, B": the kind word, the ONE separator (TextJoin.Sep), then the detail; the word alone when there is no detail.

using Xunit;

namespace Wavee.Tests;

public class SearchSubtitleTests
{
    [Fact]
    public void The_separator_is_a_middle_dot_with_a_space_each_side() => Assert.Equal(" · ", TextJoin.Sep);

    [Fact]
    public void Word_then_separator_then_detail()
        => Assert.Equal("Song" + TextJoin.Sep + "David Guetta, Sia", Search.SubtitleText("Song", "David Guetta, Sia"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_word_stands_alone_without_a_detail(string? detail)
        => Assert.Equal("Artist", Search.SubtitleText("Artist", detail));

    [Fact]
    public void No_dash_or_bullet_separator_is_ever_composed()
    {
        string s = Search.SubtitleText("Album", "Daft Punk");
        Assert.DoesNotContain(" - ", s);
        Assert.DoesNotContain(" • ", s);
    }
}
