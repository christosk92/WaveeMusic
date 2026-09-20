using System;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// LogView is the Settings › Logs viewer's whole decision core (filter/group/cap/format), engine-free by design so
// every rule pins here directly rather than through a rendered component (CLAUDE.md: no source-text tests).
public class LogViewTests
{
    static WaveeLogEntry E(long seq, WaveeLogLevel level = WaveeLogLevel.Info, string category = "app",
        string eventId = "", string message = "msg", string? op = null, long elapsedMs = -1,
        int tid = 1, long unixMs = 0, WaveeLogField[]? fields = null, string? ex = null) =>
        new(seq, unixMs, level, category, eventId, message, op, tid, elapsedMs, fields, ex);

    [Fact]
    public void Build_LevelBuckets()
    {
        WaveeLogEntry[] entries =
        [
            E(1, WaveeLogLevel.Trace), E(2, WaveeLogLevel.Debug), E(3, WaveeLogLevel.Info),
            E(4, WaveeLogLevel.Warning), E(5, WaveeLogLevel.Error),
        ];

        Assert.Equal(5, LogView.Build(entries, new LogViewQuery(LogLevelBucket.All, NewestFirst: false, GroupRepeats: false)).Shown);
        Assert.Equal(3, LogView.Build(entries, new LogViewQuery(LogLevelBucket.InfoPlus, NewestFirst: false, GroupRepeats: false)).Shown);
        Assert.Equal(2, LogView.Build(entries, new LogViewQuery(LogLevelBucket.Warnings, NewestFirst: false, GroupRepeats: false)).Shown);
        Assert.Equal(1, LogView.Build(entries, new LogViewQuery(LogLevelBucket.Errors, NewestFirst: false, GroupRepeats: false)).Shown);
    }

    [Fact]
    public void Build_CategoryFilter_IsCaseInsensitive()
    {
        WaveeLogEntry[] entries = [E(1, category: "Connect"), E(2, category: "audio"), E(3, category: "CONNECT")];

        var result = LogView.Build(entries, new LogViewQuery(Category: "connect", NewestFirst: false, GroupRepeats: false));

        Assert.Equal(2, result.Shown);
        Assert.All(result.Rows, r => Assert.Equal(r.Entry.Sequence == 1 ? "Connect" : "CONNECT", r.Entry.Category));
    }

    [Fact]
    public void Build_Search_MatchesCategoryEventIdMessageOpAndFields()
    {
        WaveeLogEntry[] entries =
        [
            E(1, category: "needle-cat", message: "hello"),
            E(2, eventId: "needle-event", message: "hello"),
            E(3, message: "contains needle here"),
            E(4, op: "op-needle", message: "hello"),
            E(5, message: "hello", fields: [WaveeLogField.Of("k", "needle-value")]),
            E(6, message: "hello", fields: [WaveeLogField.Of("needle-name", "v")]),
            E(7, category: "unrelated", message: "nope"),
        ];

        var result = LogView.Build(entries, new LogViewQuery(Search: "needle", NewestFirst: false, GroupRepeats: false));

        Assert.Equal(6, result.Shown);
        Assert.DoesNotContain(result.Rows, r => r.Entry.Sequence == 7);
    }

    [Fact]
    public void Build_NewestFirst_ReversesOrder()
    {
        WaveeLogEntry[] entries = [E(1), E(2), E(3)];

        var newest = LogView.Build(entries, new LogViewQuery(NewestFirst: true, GroupRepeats: false));
        var oldest = LogView.Build(entries, new LogViewQuery(NewestFirst: false, GroupRepeats: false));

        Assert.Equal([3L, 2L, 1L], newest.Rows.Select(r => r.Entry.Sequence).ToArray());
        Assert.Equal([1L, 2L, 3L], oldest.Rows.Select(r => r.Entry.Sequence).ToArray());
    }

    [Fact]
    public void Build_GroupRepeats_CollapsesAdjacentIdenticalOnly()
    {
        // A, A, B, A (oldest→newest) → grouped: A×2, B, A — the trailing A does NOT re-merge with the first pair
        // because B sits between them (adjacent-only grouping).
        WaveeLogEntry[] entries =
        [
            E(1, category: "a", message: "same"),
            E(2, category: "a", message: "same"),
            E(3, category: "b", message: "other"),
            E(4, category: "a", message: "same"),
        ];

        var result = LogView.Build(entries, new LogViewQuery(NewestFirst: false, GroupRepeats: true));

        Assert.Equal(3, result.Shown);
        Assert.Equal(2, result.Rows[0].Repeat);
        Assert.Equal("a", result.Rows[0].Entry.Category);
        Assert.Equal(1, result.Rows[1].Repeat);
        Assert.Equal("b", result.Rows[1].Entry.Category);
        Assert.Equal(1, result.Rows[2].Repeat);
        Assert.Equal("a", result.Rows[2].Entry.Category);
    }

    [Fact]
    public void Build_GroupRepeats_Off_KeepsEveryRow()
    {
        WaveeLogEntry[] entries = [E(1, category: "a", message: "same"), E(2, category: "a", message: "same")];

        var result = LogView.Build(entries, new LogViewQuery(NewestFirst: false, GroupRepeats: false));

        Assert.Equal(2, result.Shown);
        Assert.All(result.Rows, r => Assert.Equal(1, r.Repeat));
    }

    [Fact]
    public void Build_Cap_SetsTruncated_AndCountsAreOverAllEntries()
    {
        WaveeLogEntry[] entries =
        [
            E(1, WaveeLogLevel.Info), E(2, WaveeLogLevel.Warning), E(3, WaveeLogLevel.Error),
            E(4, WaveeLogLevel.Info), E(5, WaveeLogLevel.Info),
        ];

        var result = LogView.Build(entries, new LogViewQuery(NewestFirst: false, GroupRepeats: false, Cap: 2));

        Assert.Equal(2, result.Shown);
        Assert.True(result.Truncated);
        Assert.Equal(5, result.Total);
        Assert.Equal(1, result.WarningCount);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void Build_Cap_NotTruncated_WhenEverythingFits()
    {
        WaveeLogEntry[] entries = [E(1), E(2)];

        var result = LogView.Build(entries, new LogViewQuery(NewestFirst: false, GroupRepeats: false, Cap: 500));

        Assert.False(result.Truncated);
        Assert.Equal(2, result.Shown);
    }

    [Fact]
    public void Categories_DistinctSortedIgnoreCase()
    {
        WaveeLogEntry[] entries = [E(1, category: "ui"), E(2, category: "Audio"), E(3, category: "audio"), E(4, category: "Connect")];

        var cats = LogView.Categories(entries);

        Assert.Equal(["Audio", "Connect", "ui"], cats);
    }

    [Fact]
    public void CategoryIndex_NullIsZero_UnknownIsZero()
    {
        string[] cats = ["audio", "connect", "ui"];

        Assert.Equal(0, LogView.CategoryIndex(cats, null));
        Assert.Equal(0, LogView.CategoryIndex(cats, "nope"));
        Assert.Equal(2, LogView.CategoryIndex(cats, "connect"));
        Assert.Equal(2, LogView.CategoryIndex(cats, "CONNECT"));
    }

    [Fact]
    public void FieldText_OnePerLine_EmptyWhenNone()
    {
        Assert.Equal("", LogView.FieldText(null));
        Assert.Equal("", LogView.FieldText([]));

        string text = LogView.FieldText([WaveeLogField.Of("a", "1"), WaveeLogField.Of("b", "2")]);

        Assert.Equal("a=1\nb=2", text);
    }

    [Fact]
    public void FormatTime_UsesInjectedOffset_AndDashForZero()
    {
        Assert.Equal("—", LogView.FormatTime(0, TimeSpan.Zero));

        long unixMs = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("05:04:05.678", LogView.FormatTime(unixMs, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void SessionLabel_WithTimestamp_AndPidFallback()
    {
        long unixMs = new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        string withTimestamp = LogView.SessionLabel(unixMs, pid: 111, entryCount: 48, TimeSpan.Zero);
        string fallback = LogView.SessionLabel(0, pid: 222, entryCount: 3, TimeSpan.Zero);

        Assert.Equal("Sep 2 · 14:30 · 48 events", withTimestamp);
        Assert.Equal("pid 222 · 3 events", fallback);
    }

    [Theory]
    [InlineData(0, 0, "just now")]
    [InlineData(0, 30, "just now")]
    [InlineData(5, 0, "5 min")]
    [InlineData(65, 0, "1 h 5 m")]
    [InlineData(120, 0, "2 h 0 m")]
    public void Uptime_Tiers(int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, LogView.Uptime(new TimeSpan(0, 0, minutes, seconds)));
    }

    [Fact]
    public void MetaLine_OmitsEmptyParts()
    {
        var bare = E(812, category: "app");
        Assert.Equal("#812 · app · tid 1", LogView.MetaLine(bare));

        var full = E(812, category: "lyrics", eventId: "fetch", op: "7f2a", elapsedMs: 41, tid: 4);
        Assert.Equal("#812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms", LogView.MetaLine(full));
    }

    [Fact]
    public void IndexOfSequence_FindsGroupedRow()
    {
        WaveeLogEntry[] entries = [E(1, category: "a", message: "same"), E(2, category: "a", message: "same"), E(3, category: "b")];
        var result = LogView.Build(entries, new LogViewQuery(NewestFirst: false, GroupRepeats: true));

        Assert.Equal(2, result.Rows.Length);
        Assert.Equal(0, LogView.IndexOfSequence(result.Rows, 1));   // the grouped row's anchor is the FIRST of the run
        Assert.Equal(1, LogView.IndexOfSequence(result.Rows, 3));
        Assert.Equal(-1, LogView.IndexOfSequence(result.Rows, 999));
    }

    [Fact]
    public void CopyText_RepeatSuffix()
    {
        var rows = new[]
        {
            new LogViewRow(E(1, message: "hi"), 1),
            new LogViewRow(E(2, message: "again"), 3),
        };

        string text = LogView.CopyText(rows);

        Assert.Contains("seq=1 ", text);
        Assert.DoesNotContain("seq=1 " + "(repeated", text);
        Assert.Contains("seq=2 ", text);
        Assert.Contains("(repeated 3×)", text);
    }
}
