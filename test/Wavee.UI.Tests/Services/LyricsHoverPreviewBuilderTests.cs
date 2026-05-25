using FluentAssertions;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.Services;

namespace Wavee.UI.Tests.Services;

public sealed class LyricsHoverPreviewBuilderTests
{
    [Fact]
    public void Build_UsesNextLineStartWhenLineEndIsMissing()
    {
        var lyrics = LyricsWith(
            Line("first", 1000),
            Line("second", 4000, 7000));

        var result = LyricsHoverPreviewBuilder.Build(lyrics, durationMs: 10_000);

        result.Should().HaveCount(2);
        result[0].StartMilliseconds.Should().Be(1000);
        result[0].StopMilliseconds.Should().Be(4000);
    }

    [Fact]
    public void Build_UsesTrackDurationForFinalLineWithoutEnd()
    {
        var lyrics = LyricsWith(Line("last line", 5000));

        var result = LyricsHoverPreviewBuilder.Build(lyrics, durationMs: 10_000);

        result.Should().ContainSingle();
        result[0].StopMilliseconds.Should().Be(10_000);
    }

    [Fact]
    public void Build_SkipsDecorativeAndEmptyLines()
    {
        var lyrics = LyricsWith(
            Line("", 0, 1000),
            Line("♪", 1000, 2000),
            Line("real words", 2000, 3000));

        var result = LyricsHoverPreviewBuilder.Build(lyrics, durationMs: 5000);

        result.Should().ContainSingle();
        result[0].Title.Should().Be("real words");
    }

    [Fact]
    public void Build_DropsFinalLineWithoutEndWhenDurationIsUnknown()
    {
        var lyrics = LyricsWith(Line("floating line", 5000));

        var result = LyricsHoverPreviewBuilder.Build(lyrics, durationMs: 0);

        result.Should().BeEmpty();
    }

    private static LyricsData LyricsWith(params LyricsLine[] lines) => new()
    {
        LyricsLines = lines.ToList()
    };

    private static LyricsLine Line(string text, int startMs, int? endMs = null) => new()
    {
        PrimaryText = text,
        StartMs = startMs,
        EndMs = endMs
    };
}
