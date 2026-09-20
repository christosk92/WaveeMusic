using System.Linq;
using Wavee.Backend.Lyrics;
using Xunit;

namespace Wavee.Tests.Lyrics;

// The query-variant + normalization ladder (LyricsQuery) — pure, no network. This is the biggest match-rate lever: each
// search source now tries full "title artist" → feat-stripped "title artist" → "title", normalized + deduped.
public class LyricsQueryTests
{
    static LyricsRequest Req(string title, string artist = "Artist")
        => new("t1", "spotify:track:t1", title, new[] { artist }, "Album", 200000L);

    [Fact]
    public void StripFeat_RemovesBracketedAndDashClauses_ButNotTheWordFeature()
    {
        Assert.Equal("Mood", LyricsQuery.StripFeat("Mood (feat. iann dior)"));
        Assert.Equal("Mood", LyricsQuery.StripFeat("Mood [ft. iann dior]"));
        Assert.Equal("Song", LyricsQuery.StripFeat("Song - feat. Drake"));
        Assert.Equal("Song", LyricsQuery.StripFeat("Song (featuring X)"));
        Assert.Equal("Feature Presentation", LyricsQuery.StripFeat("Feature Presentation"));   // 'Feature' is not a feat clause
    }

    [Fact]
    public void Normalize_FullWidthCurlyDashCollapse()
    {
        string fullWidth = new string("Hello".Select(ch => (char)(ch + 0xFEE0)).ToArray());   // full-width "Hello"
        Assert.Equal("Hello", LyricsQuery.Normalize(fullWidth));
        Assert.Equal("don't \"stop\"", LyricsQuery.Normalize("don’t “stop”"));   // curly → straight
        Assert.Equal("A B", LyricsQuery.Normalize("A - B"));   // " - " separator → " "
        Assert.Equal("a b", LyricsQuery.Normalize("a   b"));   // collapse whitespace
    }

    [Fact]
    public void Variants_NoFeat_CollapsesToTwo()
    {
        Assert.Equal(new[] { "Hello Adele", "Hello" }, LyricsQuery.Variants(Req("Hello", "Adele")).ToArray());
    }

    [Fact]
    public void Variants_Feat_ProducesThreeOrdered()
    {
        Assert.Equal(
            new[] { "Mood (feat. iann dior) 24kGoldn", "Mood 24kGoldn", "Mood" },
            LyricsQuery.Variants(Req("Mood (feat. iann dior)", "24kGoldn")).ToArray());
    }

    [Fact]
    public void TitleArtistVariants_Deduped()
    {
        var v = LyricsQuery.TitleArtistVariants(Req("Hello", "Adele"));
        Assert.Equal(2, v.Count);
        Assert.Equal(("Hello", "Adele"), v[0]);
        Assert.Equal(("Hello", ""), v[1]);
    }
}
