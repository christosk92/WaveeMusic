// ── Wavee.Tests/TrackRowCellRulesTests.cs — the bound row grid's pure decisions ─────────────────────────────────────
//
// The gate for `Track.RowCellRules` (Entities/Track.UI.Bound.cs): what the table's bound cells decide per item, with no
// engine. The bound grid is built once per slot and every per-item value is a Prop over the presentation memo, so the
// decisions the cells used to make inline while re-rendering live here where they can be pinned:
//
//   THE # CELL: the rest state ladder (ring > Classic Volume/empty > equalizer > star > number) and the hover reveal
//   (never on a withheld row, except that a buffering ring still shows — ch 01 W6).
//   THE CHART MARK, THE DURATION ARM (unplayable first — ch 01 §0.7), THE METADATA LINE'S PRESENCE.
//   THE SPAN MAPS: a click on span i of a link run or a Classic folded title resolves to an artist, the container or
//   nothing — the same layout `FillLinkSpans` / `FillClassicSpans` write.
//   `Format.ReleaseDateLabel`: the cached `ShortDate` the bound duration lane reads per rebind.
//
// Pure: no scope, no tables.

using System.Globalization;
using Xunit;
using Format = Wavee.Track.Format;
using Rules = Wavee.Track.RowCellRules;
using RowState = Wavee.Track.RowState;

namespace Wavee.Tests;

public class TrackRowCellRulesTests
{
    sealed class InvariantCultureScope : IDisposable
    {
        readonly CultureInfo _saved = CultureInfo.CurrentCulture;
        public InvariantCultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        public void Dispose() => CultureInfo.CurrentCulture = _saved;
    }

    static long LocalNoon(int year, int month, int day)
        => new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();

    // ── the # cell ──

    [Theory]
    [InlineData(false, false, false, false, false, Rules.Rest.Number)]
    [InlineData(false, false, false, true, false, Rules.Rest.Star)]
    [InlineData(true, false, false, false, false, Rules.Rest.Equalizer)]
    [InlineData(true, true, false, true, false, Rules.Rest.Equalizer)]
    [InlineData(true, false, true, false, false, Rules.Rest.Spinner)]
    [InlineData(false, false, true, true, false, Rules.Rest.Spinner)]
    [InlineData(false, false, false, false, true, Rules.Rest.Empty)]
    [InlineData(false, false, false, true, true, Rules.Rest.Empty)]
    [InlineData(true, false, false, false, true, Rules.Rest.Volume)]
    [InlineData(true, false, true, false, true, Rules.Rest.Spinner)]
    public void RestOf_ladders_ring_then_skin_then_now_then_star_then_number(bool isNow, bool playing, bool buffering, bool isTop,
                                                                          bool classic, Rules.Rest expected)
    {
        var st = new RowState(isNow, playing, buffering, isTop, Saved: false);
        Assert.Equal(expected, Rules.RestOf(in st, classic));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public void Reveal_is_off_for_a_withheld_row_unless_it_is_buffering(bool buffering, bool withheld, bool expected)
    {
        var st = new RowState(IsNow: false, IsPlaying: false, buffering, IsTop: false, Saved: false);
        Assert.Equal(expected, Rules.Reveal(in st, withheld));
    }

    [Theory]
    [InlineData(0, Rules.ChartMark.None)]
    [InlineData(1, Rules.ChartMark.None)]
    [InlineData(2, Rules.ChartMark.Up)]
    [InlineData(3, Rules.ChartMark.Down)]
    [InlineData(4, Rules.ChartMark.New)]
    [InlineData(9, Rules.ChartMark.None)]
    public void ChartMarkOf_draws_only_up_down_and_new(byte status, Rules.ChartMark expected)
        => Assert.Equal(expected, Rules.ChartMarkOf(status));

    // ── the duration lane ──

    [Fact]
    public void DurationKindOf_tests_unplayable_first_then_the_release_instant()
    {
        long now = 1_000;
        Assert.Equal(Rules.DurationKind.Unavailable, Rules.DurationKindOf(unplayable: true, notYetOut: true, availableAt: 0, now));
        Assert.Equal(Rules.DurationKind.Unavailable, Rules.DurationKindOf(unplayable: true, notYetOut: false, availableAt: 0, now));
        Assert.Equal(Rules.DurationKind.ReleaseDate, Rules.DurationKindOf(unplayable: false, notYetOut: true, availableAt: 2_000, now));
        Assert.Equal(Rules.DurationKind.Dash, Rules.DurationKindOf(unplayable: false, notYetOut: true, availableAt: 0, now));
        Assert.Equal(Rules.DurationKind.Dash, Rules.DurationKindOf(unplayable: false, notYetOut: true, availableAt: 1_000, now));
        Assert.Equal(Rules.DurationKind.Clock, Rules.DurationKindOf(unplayable: false, notYetOut: false, availableAt: 0, now));
    }

    [Fact]
    public void ReleaseDateLabel_matches_ShortDate_and_returns_the_cached_instance()
    {
        using var _ = new InvariantCultureScope();
        long now = LocalNoon(2026, 9, 13);
        int sameYear = (int)LocalNoon(2026, 9, 4), nextYear = (int)LocalNoon(2027, 9, 4);

        Assert.Equal(Format.ShortDate(sameYear, now), Format.ReleaseDateLabel(sameYear, now));
        Assert.Equal(Format.ShortDate(nextYear, now), Format.ReleaseDateLabel(nextYear, now));
        Assert.Same(Format.ReleaseDateLabel(sameYear, now), Format.ReleaseDateLabel(sameYear, now + 3600));
        Assert.True(Format.ReleaseDateCacheCount >= 2);
    }

    // ── the metadata line ──

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, true, true, true, false)]
    public void ShowMeta_is_modern_only_and_needs_something_to_say(bool classic, bool artistInTitle, bool album, bool badge, bool expected)
        => Assert.Equal(expected, Rules.ShowMeta(classic, artistInTitle, album, badge));

    // ── the span maps ──

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 1)]
    [InlineData(1, false, 1)]
    [InlineData(1, true, 3)]
    [InlineData(3, false, 5)]
    [InlineData(3, true, 7)]
    public void LinkSpanCount_counts_separators_and_the_container(int artists, bool container, int expected)
        => Assert.Equal(expected, Rules.LinkSpanCount(artists, container));

    [Fact]
    public void LinkTarget_maps_even_spans_to_artists_and_the_last_span_to_the_container()
    {
        // [a0 ", " a1 ", " a2 " · " album] — 7 spans
        Assert.Equal(0, Rules.LinkTarget(0, 3, album: true));
        Assert.Equal(Rules.None, Rules.LinkTarget(1, 3, album: true));
        Assert.Equal(1, Rules.LinkTarget(2, 3, album: true));
        Assert.Equal(2, Rules.LinkTarget(4, 3, album: true));
        Assert.Equal(Rules.None, Rules.LinkTarget(5, 3, album: true));
        Assert.Equal(Rules.Container, Rules.LinkTarget(6, 3, album: true));
        // no album: nothing past the artists resolves
        Assert.Equal(Rules.None, Rules.LinkTarget(5, 3, album: false));
        Assert.Equal(Rules.None, Rules.LinkTarget(6, 3, album: false));
        // album alone: span 0 IS the container
        Assert.Equal(Rules.Container, Rules.LinkTarget(0, 0, album: true));
    }

    [Fact]
    public void ClassicArtistOf_skips_the_head_the_glyph_and_every_separator()
    {
        // title · "  ·  " · a0 ", " a1 — no film glyph
        Assert.Equal(Rules.None, Rules.ClassicArtistOf(0, showVideo: false, artistCount: 2));
        Assert.Equal(Rules.None, Rules.ClassicArtistOf(1, showVideo: false, artistCount: 2));
        Assert.Equal(0, Rules.ClassicArtistOf(2, showVideo: false, artistCount: 2));
        Assert.Equal(Rules.None, Rules.ClassicArtistOf(3, showVideo: false, artistCount: 2));
        Assert.Equal(1, Rules.ClassicArtistOf(4, showVideo: false, artistCount: 2));
        // title " " film "  ·  " a0 — with the film glyph the artists start two spans later
        Assert.Equal(Rules.None, Rules.ClassicArtistOf(2, showVideo: true, artistCount: 1));
        Assert.Equal(0, Rules.ClassicArtistOf(4, showVideo: true, artistCount: 1));
        // no artists: nothing resolves
        Assert.Equal(Rules.None, Rules.ClassicArtistOf(2, showVideo: false, artistCount: 0));
    }
}
