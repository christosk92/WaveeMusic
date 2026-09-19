using System;
using Wavee.Backend.Playlists;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

/// <summary>The chart-playlist header facts off the playlist4 header (<see cref="PlaylistFetcher.ChartInfoOf"/>):
/// only the <c>chart</c> format yields anything, mirroring <see cref="PlaylistFetcher.DaylistWindowOf"/> for the
/// sibling <c>daylist</c> format over the same <c>format_attributes</c> bag.</summary>
public class ChartInfoTests
{
    static Pl.ListAttributes Attrs(string? format, params (string Key, string Value)[] pairs)
    {
        var a = new Pl.ListAttributes();
        if (format is not null) a.Format = format;
        foreach (var (k, v) in pairs)
            a.FormatAttributes.Add(new Pl.FormatListAttribute { Key = k, Value = v });
        return a;
    }

    static readonly long UpdatedMs = new DateTimeOffset(2026, 8, 14, 15, 13, 30, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Chart_ReadsNewEntriesAndLastUpdatedAndRankType()
    {
        var (newEntries, updatedAtMs, rankType) = PlaylistFetcher.ChartInfoOf(Attrs("chart",
            ("last_updated", "2026-08-14T15:13:30Z"), ("rank_type", "plays"),
            ("new_entries_count", "5"), ("chart_entity_type", "track")));
        Assert.Equal(5, newEntries);
        Assert.Equal(UpdatedMs, updatedAtMs);
        Assert.Equal("plays", rankType);
    }

    [Fact]
    public void NonChartFormats_YieldNothing()
    {
        Assert.Equal((0, 0L, (string?)null), PlaylistFetcher.ChartInfoOf(Attrs("daylist",
            ("new_entries_count", "5"))));
        Assert.Equal((0, 0L, (string?)null), PlaylistFetcher.ChartInfoOf(Attrs(null,
            ("new_entries_count", "5"))));
    }

    [Fact]
    public void MissingOrUnparsableValues_YieldZero_NotAThrow()
    {
        var (newEntries, updatedAtMs, rankType) = PlaylistFetcher.ChartInfoOf(Attrs("chart"));
        Assert.Equal(0, newEntries);
        Assert.Equal(0L, updatedAtMs);
        Assert.Null(rankType);

        var bad = PlaylistFetcher.ChartInfoOf(Attrs("chart", ("new_entries_count", "not-a-number"), ("last_updated", "")));
        Assert.Equal(0, bad.NewEntries);
        Assert.Equal(0L, bad.UpdatedAtMs);
    }

    /// <summary>The header mapper's output shape: the facts land on the <see cref="Wavee.Core.Playlist"/> record's chart
    /// fields and default to zero/null — the store's JSON codec round-trips the record, so an old persisted header (no
    /// fields) reads back as "not a chart" rather than failing.</summary>
    [Fact]
    public void PlaylistRecord_CarriesTheChartFacts_AndDefaultsToZero()
    {
        var p = new Wavee.Core.Playlist("id", "spotify:playlist:id", "Top Songs - Argentina", null, "spotify", null, 0,
            ChartNewEntries: 5, ChartUpdatedAtMs: UpdatedMs, ChartRankType: "plays");
        Assert.Equal(5, p.ChartNewEntries);
        Assert.Equal(UpdatedMs, p.ChartUpdatedAtMs);
        Assert.Equal("plays", p.ChartRankType);
        var bare = new Wavee.Core.Playlist("id", "spotify:playlist:id", "x", null, "o", null, 0);
        Assert.Equal(0, bare.ChartNewEntries);
        Assert.Equal(0L, bare.ChartUpdatedAtMs);
        Assert.Null(bare.ChartRankType);
    }
}
