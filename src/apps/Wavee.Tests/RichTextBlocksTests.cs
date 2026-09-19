using Wavee;
using Xunit;

namespace Wavee.Tests;

public class RichTextBlocksTests
{
    // Reconstructed from the "Focus, Solved" show-notes description in the podcast-ui-repair plan (§5.2): a lead
    // paragraph, a guide-link line, then a CHAPTERS run — one keyword-prefixed entry followed by bare "(hh:mm:ss)
    // Title" lines.
    const string FocusSolvedDescription =
        "Focus is one of the most valuable and least understood resources we have. In this episode we explore why " +
        "attention has become so scarce and what to do about it.\n" +
        "\n" +
        "Get the free guide for this episode: https://solvedpodcast.com/focus/\n" +
        "\n" +
        "CHAPTERS(00:00:00) Introduction\n" +
        "(00:03:06) The Attention Crisis\n" +
        "(00:11:42) Building a Focus Practice\n" +
        "(00:27:15) Q&A";

    [Fact]
    public void Focus_Solved_description_keeps_the_prose_and_recognises_every_chapter()
    {
        var (prose, chapters) = RichTextBlocks.Split(FocusSolvedDescription);

        Assert.Contains("Focus is one of the most valuable", prose);
        Assert.Contains("Get the free guide for this episode: https://solvedpodcast.com/focus/", prose);
        Assert.DoesNotContain("CHAPTERS", prose);
        Assert.DoesNotContain("Attention Crisis", prose);

        Assert.Equal(4, chapters.Length);
        Assert.Equal(new ChapterLink(0, "Introduction"), chapters[0]);
        Assert.Equal(new ChapterLink(186_000, "The Attention Crisis"), chapters[1]);
        Assert.Equal(new ChapterLink(702_000, "Building a Focus Practice"), chapters[2]);
        Assert.Equal(new ChapterLink(1_635_000, "Q&A"), chapters[3]);
    }

    [Fact]
    public void Text_with_no_chapters_convention_is_returned_unchanged()
    {
        var (prose, chapters) = RichTextBlocks.Split("Just a plain description with no timestamps at all.");
        Assert.Equal("Just a plain description with no timestamps at all.", prose);
        Assert.Empty(chapters);
    }

    [Fact]
    public void A_parenthesised_time_with_no_title_is_not_a_chapter_reference()
    {
        var (prose, chapters) = RichTextBlocks.Split("Recorded (00:03:06) in one take.");
        Assert.Empty(chapters);
        Assert.Contains("Recorded (00:03:06) in one take.", prose);
    }

    [Fact]
    public void Chapter_lines_wrapped_in_paragraph_tags_still_match()
    {
        var (_, chapters) = RichTextBlocks.Split("<p>CHAPTERS(00:00:00) Introduction</p>\n<p>(00:01:30) Part two</p>");
        Assert.Equal(2, chapters.Length);
        Assert.Equal(new ChapterLink(0, "Introduction"), chapters[0]);
        Assert.Equal(new ChapterLink(90_000, "Part two"), chapters[1]);
    }

    [Theory]
    [InlineData("0:00", 0)]
    [InlineData("03:06", 186_000)]
    [InlineData("1:00:01", 3_601_000)]
    [InlineData("27:15", 1_635_000)]
    public void Timestamps_parse_both_mm_ss_and_hh_mm_ss(string ts, int expectedMs)
    {
        Assert.True(RichTextBlocks.TryParseTimestamp(ts, out int ms));
        Assert.Equal(expectedMs, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("1:2:3:4")]
    [InlineData("a:bb")]
    public void Malformed_timestamps_fail(string ts)
        => Assert.False(RichTextBlocks.TryParseTimestamp(ts, out _));

    [Fact]
    public void Empty_or_null_input_returns_empty_prose_and_no_chapters()
    {
        var (emptyProse, emptyChapters) = RichTextBlocks.Split("");
        Assert.Equal("", emptyProse);
        Assert.Empty(emptyChapters);

        var (nullProse, nullChapters) = RichTextBlocks.Split(null);
        Assert.Equal("", nullProse);
        Assert.Empty(nullChapters);
    }
}
