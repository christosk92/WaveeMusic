using FluentAssertions;
using Wavee.UI.Formatters;
using Xunit;

namespace Wavee.UI.Tests.Formatters;

public sealed class ReleaseSubtitleFormatterTests
{
    [Fact]
    public void AllSegments_RenderedWithSeparator()
    {
        ReleaseSubtitleFormatter.Format("album", 2023, 12)
            .Should().Be("Album · 2023 · 12 songs");
    }

    [Fact]
    public void TitleCases_ReleaseType()
    {
        ReleaseSubtitleFormatter.Format("compilation", 1990, 25)
            .Should().StartWith("Compilation · ");
    }

    [Fact]
    public void Singular_For_SingleItem()
    {
        ReleaseSubtitleFormatter.Format("single", 2024, 1)
            .Should().EndWith("1 song");
    }

    [Theory]
    [InlineData(ReleaseSubtitleFormatter.CountNoun.Track, "1 track", "5 tracks")]
    [InlineData(ReleaseSubtitleFormatter.CountNoun.Episode, "1 episode", "5 episodes")]
    [InlineData(ReleaseSubtitleFormatter.CountNoun.Song, "1 song", "5 songs")]
    public void Noun_Selection(ReleaseSubtitleFormatter.CountNoun noun, string singular, string plural)
    {
        ReleaseSubtitleFormatter.Format(null, null, 1, noun).Should().Be(singular);
        ReleaseSubtitleFormatter.Format(null, null, 5, noun).Should().Be(plural);
    }

    [Theory]
    [InlineData(null, null, null, "")]
    [InlineData("", null, null, "")]
    [InlineData(null, 0, null, "")]
    [InlineData(null, -2, null, "")]
    [InlineData(null, null, 0, "")]
    [InlineData(null, null, -3, "")]
    public void NoSegments_ReturnsEmpty(string? releaseType, int? year, int? count, string expected)
    {
        ReleaseSubtitleFormatter.Format(releaseType, year, count).Should().Be(expected);
    }

    [Fact]
    public void YearOnly()
    {
        ReleaseSubtitleFormatter.Format(null, 2020, null).Should().Be("2020");
    }

    [Fact]
    public void TypeOnly()
    {
        ReleaseSubtitleFormatter.Format("Album", null, null).Should().Be("Album");
    }

    [Fact]
    public void CountOnly()
    {
        ReleaseSubtitleFormatter.Format(null, null, 3).Should().Be("3 songs");
    }
}
