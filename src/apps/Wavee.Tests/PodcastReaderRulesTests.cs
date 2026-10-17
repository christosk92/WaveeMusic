using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PodcastReaderRulesTests
{
    [Fact]
    public void ChapterWithoutEndStopsAtNextChapterStart()
    {
        Spotify.Podcasts.Chapter[] chapters = [new("a", "First", 0, 0), new("b", "Second", 1000, 0)];
        Assert.Equal(-1, PodcastReaderRules.CurrentChapter(chapters, -1));
        Assert.Equal(0, PodcastReaderRules.CurrentChapter(chapters, 999));
        Assert.Equal(1, PodcastReaderRules.CurrentChapter(chapters, 1000));
    }

    [Fact]
    public void ChapterWithExplicitEndDoesNotHighlightTheGap()
    {
        Spotify.Podcasts.Chapter[] chapters = [new("a", "First", 0, 500), new("b", "Second", 1000, 1200)];
        Assert.Equal(-1, PodcastReaderRules.CurrentChapter(chapters, 500));
        Assert.Equal(-1, PodcastReaderRules.CurrentChapter(chapters, 1200));
    }

    [Theory]
    [InlineData("ELIGIBILITY_STATUS_UNRESTRICTED", true)]
    [InlineData("ELIGIBILITY_STATUS_ALREADY_COMMENTED", false)]
    [InlineData("", false)]
    [InlineData("future-server-value", false)]
    public void ComposerOnlyAcceptsCapturedPermission(string status, bool allowed)
        => Assert.Equal(allowed, PodcastReaderRules.CanComment(status));
}
