using System;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The Notifications page's pure numbers (App/SetupNotificationSummary.cs): the headline/more topic partition, the
// quiet-hours presets, and the small facts the page/stage render from live settings. Engine-free by construction
// (System + Wavee.Core only), exactly like SetupGatingTests/SetupSoundFactsTests — these drive the REAL production
// type (source-included into this project), never a copy of it.
public class SetupNotificationSummaryTests
{
    // ── the topic partition ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeadlineAndMoreTopics_PartitionEveryNotifyTopic_WithNoDuplicates()
    {
        var all = Enum.GetValues<NotifyTopic>();
        var union = SetupNotificationSummary.HeadlineTopics.Concat(SetupNotificationSummary.MoreTopics).ToArray();

        // No duplicates within OR across the two lists — a topic on both would be a dial rendered twice.
        Assert.Equal(union.Length, union.Distinct().Count());
        // Every real topic is accounted for exactly once — a new NotifyTopic value forgotten in both lists fails here.
        Assert.Equal(all.OrderBy(t => t).ToArray(), union.OrderBy(t => t).ToArray());
    }

    [Fact]
    public void MoreCount_IsComputedFromTheMoreTopicsList()
    {
        Assert.Equal(5, SetupNotificationSummary.MoreCount);
        Assert.Equal(SetupNotificationSummary.MoreTopics.Length, SetupNotificationSummary.MoreCount);
    }

    // ── quiet-hours presets ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, 22, 8, 0)]
    [InlineData(true, 23, 8, 1)]
    [InlineData(true, 0, 7, 2)]
    public void QuietPresetIndex_RoundTripsEveryPreset(bool enabled, int from, int to, int expected)
        => Assert.Equal(expected, SetupNotificationSummary.QuietPresetIndex(enabled, from, to));

    [Fact]
    public void QuietPresetIndex_UnknownTriple_FallsBackToOff()
    {
        // A hand-edited settings file (or a future build's fourth preset) holding a triple that matches none of the
        // three shortcuts must resolve to "Off" (index 0), never throw or guess an adjacent preset.
        Assert.Equal(0, SetupNotificationSummary.QuietPresetIndex(true, 13, 17));
        Assert.Equal(0, SetupNotificationSummary.QuietPresetIndex(false, 23, 8));   // disabled beats a matching window
    }

    // ── formatting ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(8, "08:00")]
    [InlineData(0, "00:00")]
    [InlineData(23, "23:00")]
    [InlineData(-1, "23:00")]   // wraps rather than producing a malformed string
    [InlineData(24, "00:00")]
    public void Clock_FormatsAsTwoDigitHour(int hour, string expected)
        => Assert.Equal(expected, SetupNotificationSummary.Clock(hour));

    // ── the reach count + the row summary ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WindowsReachCount_CountsOnlyTopicsAtTheWindowsLevel()
    {
        NotifyLevel[] levels = [NotifyLevel.Off, NotifyLevel.InApp, NotifyLevel.Windows, NotifyLevel.Windows, NotifyLevel.InApp];
        Assert.Equal(2, SetupNotificationSummary.WindowsReachCount(levels));
        Assert.Equal(0, SetupNotificationSummary.WindowsReachCount(ReadOnlySpan<NotifyLevel>.Empty));
    }

    [Fact]
    public void Summarize_QuietOff_ReportsTheStoredWindowAnyway()
    {
        var s = SetupNotificationSummary.Summarize(new QuietHours(false, 22, 8));
        Assert.Equal(SetupNotificationSummary.MoreCount, s.Count);
        Assert.False(s.QuietOn);
        Assert.Equal("22:00", s.From);
        Assert.Equal("08:00", s.To);
    }

    [Fact]
    public void Summarize_QuietOn_ReportsTheActiveWindow()
    {
        var s = SetupNotificationSummary.Summarize(new QuietHours(true, 23, 8));
        Assert.True(s.QuietOn);
        Assert.Equal("23:00", s.From);
        Assert.Equal("08:00", s.To);
    }
}
