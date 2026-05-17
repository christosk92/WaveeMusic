using FluentAssertions;
using Wavee.UI.Formatters.Artist;
using Xunit;

namespace Wavee.UI.Tests.Formatters.Artist;

public sealed class ArtistBioTextFormatterTests
{
    [Fact]
    public void StripsHtmlTags()
    {
        var line = ArtistBioTextFormatter.BuildHeroBioLine(
            "<p>A Swedish singer and songwriter.</p>", null, "Robyn");
        line.Should().Be("Swedish singer and songwriter.");
    }

    [Fact]
    public void DecodesHtmlEntities()
    {
        var line = ArtistBioTextFormatter.BuildHeroBioLine(
            "Half Alive &amp; Whole Brave.", null, "Artist");
        line.Should().Be("Half Alive & Whole Brave.");
    }

    [Fact]
    public void FirstSentenceWins_WhenMultipleSentences()
    {
        var line = ArtistBioTextFormatter.BuildHeroBioLine(
            "Famous singer. Has many albums.", null, "Artist");
        line.Should().Be("Famous singer.");
    }

    [Fact]
    public void NoSentenceBreak_UsesFullText()
    {
        var line = ArtistBioTextFormatter.BuildHeroBioLine(
            "Just a name with no period", null, "Artist");
        line.Should().StartWith("Just a name");
    }

    [Theory]
    [InlineData("Madonna is an American singer.", "Madonna", "American singer.")]
    [InlineData("BTS are a South Korean group.", "BTS", "South Korean group.")]
    [InlineData("Elvis was an American musician.", "Elvis", "American musician.")]
    [InlineData("The Beatles were a British band.", "The Beatles", "British band.")]
    public void StripsLeadingArtistSubject(string source, string artistName, string expected)
    {
        ArtistBioTextFormatter.BuildHeroBioLine(source, null, artistName).Should().Be(expected);
    }

    [Theory]
    [InlineData("A pioneering artist.", "Pioneering artist.")]
    [InlineData("An American musician.", "American musician.")]
    [InlineData("The legendary singer.", "Legendary singer.")]
    public void StripsLeadingArticle(string source, string expected)
    {
        ArtistBioTextFormatter.BuildHeroBioLine(source, null, "Anonymous").Should().Be(expected);
    }

    [Fact]
    public void EnsureSentenceCasing_CapitalizesFirstAndAppendsPeriod()
    {
        ArtistBioTextFormatter.EnsureSentenceCasing("hello world").Should().Be("Hello world.");
    }

    [Fact]
    public void EnsureSentenceCasing_KeepsExistingTerminator()
    {
        ArtistBioTextFormatter.EnsureSentenceCasing("Hello world!").Should().Be("Hello world!");
        ArtistBioTextFormatter.EnsureSentenceCasing("What?").Should().Be("What?");
    }

    [Fact]
    public void EnsureSentenceCasing_EmptyIn_EmptyOut()
    {
        ArtistBioTextFormatter.EnsureSentenceCasing(string.Empty).Should().Be(string.Empty);
        ArtistBioTextFormatter.EnsureSentenceCasing("   ").Should().Be(string.Empty);
    }

    [Fact]
    public void FallsBackToSummary_WhenBioBlank()
    {
        var line = ArtistBioTextFormatter.BuildHeroBioLine(
            biography: null, summary: "Indie pop.", artistName: "Artist");
        line.Should().Be("Indie pop.");
    }

    [Fact]
    public void Returns_Empty_WhenBothBlank()
    {
        ArtistBioTextFormatter.BuildHeroBioLine(null, null, "Artist").Should().BeEmpty();
        ArtistBioTextFormatter.BuildHeroBioLine("   ", "   ", "Artist").Should().BeEmpty();
    }

    [Fact]
    public void Truncates_WithEllipsis_AtMaxLength()
    {
        // Source MUST be a single sentence (no period mid-string) so the
        // first-sentence extractor returns the whole thing; then truncation
        // kicks in at the max-length cap.
        var source = new string('A', 500);
        var line = ArtistBioTextFormatter.BuildHeroBioLine(source, null, "X", maxLength: 50);
        line.Length.Should().BeLessThanOrEqualTo(50);
        line.Should().EndWith("...");
    }

    [Fact]
    public void DefaultMaxLength_IsApplied()
    {
        var source = new string('A', 500);
        var line = ArtistBioTextFormatter.BuildHeroBioLine(source, null, "X");
        line.Length.Should().BeLessThanOrEqualTo(ArtistBioTextFormatter.DefaultMaxLength);
    }
}
