using Wavee;
using Xunit;
using Chapter = Wavee.Spotify.Podcasts.Chapter;

namespace Wavee.Tests;

public sealed class PodcastChapterTimelineTests
{
    [Fact]
    public void Normalization_orders_deduplicates_and_infers_only_missing_ends()
    {
        Chapter[] source = [new("c", "Last", 600, 0), new("a", "", 0, 0), new("b", "Middle", 200, 350),
            new("aa", "Introduction", 0, 0), new("bad", "Before", -10, 0), new("past", "Past end", 1000, 0)];
        var chapters = PodcastChapterTimeline.Normalize(source, 1000);
        Assert.Equal(new[] { new PodcastChapterTimeline.Interval("Introduction", 0, 200),
            new PodcastChapterTimeline.Interval("Middle", 200, 350), new PodcastChapterTimeline.Interval("Last", 600, 1000) }, chapters);
        Assert.Equal(1, PodcastChapterTimeline.At(chapters, 200));
        Assert.Equal(-1, PodcastChapterTimeline.At(chapters, 350));
        Assert.Equal(-1, PodcastChapterTimeline.At(chapters, 500));
        Assert.Equal(2, PodcastChapterTimeline.At(chapters, 1000));
        Assert.Equal(-1, PodcastChapterTimeline.At(chapters, 1001));
        Assert.Equal(600, source[0].StartMs); // provider payload is never rearranged
    }

    [Fact]
    public void Overlaps_clip_to_next_start_and_invalid_end_uses_next_boundary()
    {
        var chapters = PodcastChapterTimeline.Normalize([
            new Chapter("a", "A", 100, 900), new Chapter("b", "B", 300, 250), new Chapter("c", "C", 700, 5000)], 1000);
        Assert.Equal(new[] { 300, 700, 1000 }, chapters.Select(c => c.EndMs));
        Assert.Equal(-1, PodcastChapterTimeline.At(chapters, 0));
        Assert.Equal(1, PodcastChapterTimeline.At(chapters, 300));
        Assert.Empty(PodcastChapterTimeline.Normalize([new Chapter("a", "A", 0, 0)], 0));
    }

    [Fact]
    public void Dense_visual_ticks_do_not_change_hover_boundaries_or_absolute_seek_values()
    {
        var source = Enumerable.Range(0, 100).Select(i => new Chapter("c" + i, "Chapter " + i, i * 100, 0)).ToArray();
        var chapters = PodcastChapterTimeline.Normalize(source, 10_000);
        var ticks = PodcastChapterTimeline.MarkerFractions(chapters, 10_000, 100);
        Assert.InRange(ticks.Length, 1, 25);
        Assert.Equal(100, chapters.Length);
        Assert.Equal(23, PodcastChapterTimeline.At(chapters, 2375));
        Assert.Equal(2375, PodcastChapterTimeline.HoverTime(.2375f, 10_000));
        Assert.Equal(0, PodcastChapterTimeline.HoverTime(-1, 10_000));
        Assert.Equal(10_000, PodcastChapterTimeline.HoverTime(2, 10_000));
        Assert.Empty(PodcastChapterTimeline.MarkerFractions(chapters, 10_000, 0));
        Assert.Empty(PodcastChapterTimeline.MarkerFractions(chapters, 10_000, float.NaN));
        for (int i = 1; i < ticks.Length; i++) Assert.True((ticks[i] - ticks[i - 1]) * 100 >= 3.99f);
    }
}
