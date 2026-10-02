// ── Wavee.Tests/LogsPageRulesTests.cs ──────────────────────────────────────────────────────────────────────────────
// facts over the Logs page's pure half (`Screens/LogsPage.cs`): the level strip's labels with their counts inside, the
// invariant thousands rule, the session picker's status caption, and where a selection lands after a re-list.
// Engine-free (privacy-diagnostics-tab-implementation.md §3.2, §5.2, §7).

using System.Globalization;
using Xunit;

namespace Wavee.Tests;

public class LogsPageRulesTests
{
    [Fact]
    public void Level_labels_carry_a_count_only_when_nonzero_with_thousands_separators()
    {
        // Nothing to count → the bare labels, in the strip's order: All · Info+ · Warnings · Errors.
        Assert.Equal(new[] { "All", "Info+", "Warnings", "Errors" },
            LogsPage.Rules.LevelLabels("All", "Info+", "Warnings", "Errors", warningCount: 0, errorCount: 0));

        // Only the two severity segments ever carry a count; All and Info+ never do.
        Assert.Equal(new[] { "All", "Info+", "Warnings · 368", "Errors · 4" },
            LogsPage.Rules.LevelLabels("All", "Info+", "Warnings", "Errors", warningCount: 368, errorCount: 4));

        // One side zero, one side past a thousand: the separator is the invariant comma.
        Assert.Equal(new[] { "All", "Info+", "Warnings · 1,234", "Errors" },
            LogsPage.Rules.LevelLabels("All", "Info+", "Warnings", "Errors", warningCount: 1234, errorCount: 0));

        Assert.Equal("Warnings", LogsPage.Rules.WithCount("Warnings", 0));
        Assert.Equal("Warnings", LogsPage.Rules.WithCount("Warnings", -3));   // a negative count is nonsense, never printed
        Assert.Equal("Errors · 1", LogsPage.Rules.WithCount("Errors", 1));
    }

    [Fact]
    public void Thousands_is_invariant()
    {
        Assert.Equal("0", LogsPage.Rules.Thousands(0));
        Assert.Equal("999", LogsPage.Rules.Thousands(999));
        Assert.Equal("1,000", LogsPage.Rules.Thousands(1000));
        Assert.Equal("1,234,567", LogsPage.Rules.Thousands(1_234_567));

        // A thread whose culture groups with "." and prints decimals with "," (the Dutch/German shape) must not leak in.
        var saved = CultureInfo.CurrentCulture;
        try
        {
            var other = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            other.NumberFormat.NumberGroupSeparator = ".";
            other.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = other;
            Assert.Equal("1.234.567", 1_234_567.ToString("N0", CultureInfo.CurrentCulture));   // the control: the leak is real
            Assert.Equal("1,234,567", LogsPage.Rules.Thousands(1_234_567));
            Assert.Equal("Warnings · 1,234", LogsPage.Rules.WithCount("Warnings", 1234));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void PickerCaption_covers_reading_none_and_failed()
    {
        // A walk in flight reads as "reading", whatever the (stale) count says.
        Assert.Equal(LogsPage.Rules.PickerCaptionKind.Reading, LogsPage.Rules.PickerCaption(busy: true, count: 0, failed: false));
        Assert.Equal(LogsPage.Rules.PickerCaptionKind.Reading, LogsPage.Rules.PickerCaption(busy: true, count: 5, failed: false));

        // A finished walk that found nothing says so — the picker offers only "this session".
        Assert.Equal(LogsPage.Rules.PickerCaptionKind.NoneOnDisk, LogsPage.Rules.PickerCaption(busy: false, count: 0, failed: false));

        // A failed walk beats both: the list is not to be trusted, and "none on disk" would be a lie.
        Assert.Equal(LogsPage.Rules.PickerCaptionKind.Failed, LogsPage.Rules.PickerCaption(busy: false, count: 0, failed: true));
        Assert.Equal(LogsPage.Rules.PickerCaptionKind.Failed, LogsPage.Rules.PickerCaption(busy: true, count: 3, failed: true));

        // A healthy picker with past sessions has no caption at all.
        Assert.Equal(LogsPage.Rules.PickerCaptionKind.None, LogsPage.Rules.PickerCaption(busy: false, count: 29, failed: false));
    }

    [Fact]
    public void SelectionAfterRefresh_follows_the_session_key_and_falls_back_to_the_live_session()
    {
        string[] keys = ["3f9c2a1b", "aa11bb22", "pid31544@1720512345678"];

        // Nothing selected (or the live session) stays on the live session whatever the list now holds.
        Assert.Equal(0, LogsPage.Rules.SelectionAfterRefresh(null, keys));
        Assert.Equal(0, LogsPage.Rules.SelectionAfterRefresh("", keys));

        // A past session keeps its place by KEY: 1 + its position in the NEW list, even after the list shifted.
        Assert.Equal(1, LogsPage.Rules.SelectionAfterRefresh("3f9c2a1b", keys));
        Assert.Equal(3, LogsPage.Rules.SelectionAfterRefresh("pid31544@1720512345678", keys));
        Assert.Equal(2, LogsPage.Rules.SelectionAfterRefresh("aa11bb22", ["new00000", "aa11bb22"]));

        // A session that was pruned from disk falls back to the live session; an empty list too.
        Assert.Equal(0, LogsPage.Rules.SelectionAfterRefresh("gone0000", keys));
        Assert.Equal(0, LogsPage.Rules.SelectionAfterRefresh("3f9c2a1b", []));
        Assert.Equal(0, LogsPage.Rules.SelectionAfterRefresh("3F9C2A1B", keys));   // keys are ordinal: a different case is a different run
    }

    [Fact]
    public void IsLive_is_session_zero()
    {
        Assert.True(LogsPage.Rules.IsLive(0));
        Assert.False(LogsPage.Rules.IsLive(1));
        Assert.False(LogsPage.Rules.IsLive(29));
    }
}
