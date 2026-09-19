// ── Wavee.Tests/EpisodeRowRulesTests.cs — the reader row's own decisions (podcast plan §2 W3, §5.7, §12 D-5) ─────────
//
// The row paints ONE pct (D-5: a completed episode reads 1), leads an in-progress row with "N min left", states its
// length in the detail frame's duration words, and — when its numeral shows the episode number — drops the title's own
// number prefix. Every expected string is built from the SAME typed `Strings.*` call the row makes, never an English
// literal, so a translation cannot break a fact, only a changed rule can.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EpisodeRowRulesTests
{
    const int Min = 60_000;

    // ── TitleSansNumber (0d0429a0 StripEpisodeNumberPrefix, made stricter) ──────────────────────────────────────────

    [Theory]
    [InlineData("#14 · The Build Trap", 14, "The Build Trap")]     // the fake seed's own form
    [InlineData("#123 - Dead air", 123, "Dead air")]
    [InlineData("#123: Dead air", 123, "Dead air")]
    [InlineData("#12 Dead air", 12, "Dead air")]                    // after a '#', a space is separator enough
    [InlineData("123. Dead air", 123, "Dead air")]
    [InlineData("123 - Dead air", 123, "Dead air")]
    [InlineData("9 | Dead air", 9, "Dead air")]
    public void TitleSansNumber_StripsTheNumberTheNumeralShows(string title, int number, string expected)
        => Assert.Equal(expected, Episode.TitleSansNumber(title, number));

    [Theory]
    [InlineData("1983 Days of Radio", 1983)]    // a bare number needs punctuation: this is a year in a title
    [InlineData("#14 · Dead air", 13)]          // not THIS episode's number
    [InlineData("#140 Dead air", 14)]           // more digits than the number
    [InlineData("14th Street", 14)]             // no separator after the digits
    [InlineData("#14", 14)]                     // nothing after the prefix — keep the title whole
    [InlineData("#14 - ", 14)]
    [InlineData("Dead air", 5)]
    [InlineData("#14 · Dead air", 0)]           // an unnumbered episode shows no numeral to carry it
    public void TitleSansNumber_LeavesEverythingElseAlone(string title, int number)
        => Assert.Equal(title, Episode.TitleSansNumber(title, number));

    [Fact]
    public void TitleSansNumber_ReadsDashesAndDots_ByCodePoint()
    {
        string enDash = "#7 " + (char)0x2013 + " Dead air";
        string emDash = "7" + (char)0x2014 + "Dead air";
        Assert.Equal("Dead air", Episode.TitleSansNumber(enDash, 7));
        Assert.Equal("Dead air", Episode.TitleSansNumber(emDash, 7));
    }

    [Fact]
    public void TitleSansNumber_ReturnsTheSameInstance_WhenNothingIsStripped()
    {
        const string title = "Dead air";
        Assert.Same(title, Episode.TitleSansNumber(title, 3));
        Assert.Equal("", Episode.TitleSansNumber("", 3));
    }

    // ── "N min left" ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 31 * Min, 31)]
    [InlineData(14 * Min, 31 * Min, 17)]
    [InlineData(29 * Min + 12_000, 31 * Min, 2)]       // 1 min 48 s rounds to 2
    [InlineData(30 * Min + 30_000, 31 * Min, 1)]       // 30 s left is still "1 min", never "0 min"
    [InlineData(31 * Min, 31 * Min, 1)]
    [InlineData(40 * Min, 31 * Min, 1)]                // a position past the end reads as the end
    [InlineData(-5_000, 31 * Min, 31)]
    [InlineData(10 * Min, 0, 0)]                       // no duration, no claim
    public void LeftMinutes(int progressMs, int durationMs, int expected)
        => Assert.Equal(expected, Episode.LeftMinutes(progressMs, durationMs));

    [Fact]
    public void LeftLabel_IsPodcastLeft_OverTheDurationWords()
    {
        Assert.Equal(Strings.Podcast.Left(Strings.Detail.DurationMin(17)), Episode.LeftLabel(14 * Min, 31 * Min));
        Assert.Equal(Strings.Podcast.Left(Strings.Detail.DurationHrMin(1, 5)), Episode.LeftLabel(10 * Min, 75 * Min));
        Assert.Equal("", Episode.LeftLabel(10 * Min, 0));
    }

    [Fact]
    public void DurationWords_AndTheRowsLength()
    {
        Assert.Equal(Strings.Detail.DurationMin(28), Episode.DurationWords(28));
        Assert.Equal(Strings.Detail.DurationMin(1), Episode.DurationWords(0));
        Assert.Equal(Strings.Detail.DurationHrMin(1, 30), Episode.DurationWords(90));
        Assert.Equal(Strings.Detail.DurationMin(28), Episode.DurationLabel(28 * Min + 40_000));   // whole minutes
        Assert.Equal("", Episode.DurationLabel(0));
    }

    // ── the pct the reader reads (D-5) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReaderPct_ReadsACompletedEpisodeAsOne()
    {
        // 25 s left of a 10-minute episode: 95.8 % by Pct, but COMPLETED by the one rule — the row says "played".
        Assert.Equal((10 * Min - 25_000) / (float)(10 * Min), Episode.ReaderPct(10 * Min - 25_000, 10 * Min));
        Assert.False(Episode.Rules.Played(Episode.ReaderPct(10 * Min - 25_000, 10 * Min)));
    }

    [Fact]
    public void ReaderPct_IsPctOtherwise_AndZeroWithoutADurationOrAPosition()
    {
        Assert.Equal(0.5f, Episode.ReaderPct(5 * Min, 10 * Min));
        Assert.Equal(0f, Episode.ReaderPct(0, 10 * Min));
        Assert.Equal(0f, Episode.ReaderPct(5 * Min, 0));
        Assert.True(Episode.Rules.InProgress(Episode.ReaderPct(5 * Min, 10 * Min)));
    }
}
