// ── Wavee.Tests/ScrollDiagRulesTests.cs — the Diagnostics "Scroll" card's pure decisions (scroll rework) ─────────────
//
// The persisted probe level's clamp, the persisted feel profile's lookup, the CSV export's file name and the CSV header's
// refresh rate (Screens/Diagnostics.Scroll.cs).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ScrollDiagRulesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(-1, 1)]
    [InlineData(3, 1)]
    [InlineData(int.MaxValue, 1)]
    public void A_stored_level_outside_Off_to_Trace_reads_as_Summary(int stored, int expected)
        => Assert.Equal(expected, ScrollDiagRules.ClampLevel(stored));

    [Fact]
    public void The_level_values_are_the_probe_order_Off_Summary_Trace()
    {
        Assert.Equal(0, ScrollDiagRules.LevelOff);
        Assert.Equal(1, ScrollDiagRules.LevelSummary);
        Assert.Equal(2, ScrollDiagRules.LevelTrace);
    }

    [Fact]
    public void A_profile_is_found_case_insensitively_and_an_unknown_one_is_the_first()
    {
        string[] names = ["Standard", "Glide"];
        Assert.Equal(1, ScrollDiagRules.ProfileIndex(names, "glide"));
        Assert.Equal(0, ScrollDiagRules.ProfileIndex(names, "Custom"));
        Assert.Equal(0, ScrollDiagRules.ProfileIndex(names, "WinUiExact"));   // retired names fall back to the default
        Assert.Equal(0, ScrollDiagRules.ProfileIndex(names, "Snappy"));
        Assert.Equal(0, ScrollDiagRules.ProfileIndex(names, ""));
        Assert.Equal(0, ScrollDiagRules.ProfileIndex(names, null));
    }

    [Fact]
    public void The_export_file_is_named_by_its_local_capture_time()
        => Assert.Equal("scroll-20260924-093005.csv",
            ScrollDiagRules.ExportFileName(new DateTimeOffset(2026, 9, 24, 9, 30, 5, TimeSpan.FromHours(2))));

    [Theory]
    [InlineData(8.0, 125.0)]
    [InlineData(1000.0 / 60.0, 60.0)]
    [InlineData(0.0, 60.0)]
    [InlineData(-4.0, 60.0)]
    public void The_refresh_rate_comes_from_the_last_frames_interval(double intervalMs, double expectedHz)
        => Assert.Equal(expectedHz, ScrollDiagRules.RefreshHz(intervalMs), 6);
}
