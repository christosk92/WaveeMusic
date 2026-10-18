// ── Wavee.Tests/Theming/ZuneListRulesTests.cs — Zune's list rules: rows per density, the row ink, the published policies (P5 Z3) ────
//
// The pure rules of `Themes\Zune\Structure\Rules\ZuneListRules.cs` against the boards (Z-Tokens' rows 44 / 52 / 60, Z-State-Rows' playing
// row, Z-Album's numbered rows) and what the structure publishes for the core (one Modern skin, artwork on, the detail table's records at
// the density's rows). The style's metrics and the structure's ladder are pinned equal, so the "Row size" menu names the rung on screen.

using FluentGpu.Foundation;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Xunit;
using Wavee.Theming.Contracts;

namespace Wavee.Tests.Theming;

public sealed class ZuneListRulesTests
{
    public static TheoryData<ZuneDensity, float, float> RowsPerDensity() => new()
    {
        { ZuneDensity.Compact, 44f, 32f },
        { ZuneDensity.Standard, 52f, 36f },
        { ZuneDensity.Comfortable, 60f, 40f },
    };

    [Theory, MemberData(nameof(RowsPerDensity))]
    public void A_density_sizes_the_rows_44_52_60(ZuneDensity density, float row, float art)
    {
        Assert.Equal(row, ZuneListRules.RowFor(density));
        Assert.Equal(art, ZuneListRules.ArtFor(density));
        // The style publishes the same ladder the structure draws against, in both skin slots.
        var published = ZuneDensityOption.Rows(density);
        Assert.Equal(row, published.ModernRow);
        Assert.Equal(row, published.ClassicRow);
        Assert.Equal(art, published.ModernArt);
        Assert.Equal(density, ZuneListRules.DensityOfRung(published.Rung));
        Assert.Equal(row, ZuneStyle.MetricsFor(density).TrackRows.ModernRow);
        // The art always sits inside the row with air around it.
        Assert.True(row - art >= 8f, density + ": the art leaves less than 8 inside the row");
    }

    [Theory, MemberData(nameof(RowsPerDensity))]
    public void Every_window_tier_draws_the_densitys_rows(ZuneDensity density, float row, float art)
    {
        _ = art;
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(row, ZuneListRules.TableRowFor(tier, density));
    }

    [Fact]
    public void An_unknown_rung_reads_as_standard_and_the_menu_lists_three_densities_in_order()
    {
        Assert.Equal(ZuneDensity.Standard, ZuneListRules.DensityOfRung(7));
        Assert.Equal(ZuneDensity.Standard, ZuneListRules.DensityOfRung(-1));
        Assert.Equal(new[] { ZuneDensity.Compact, ZuneDensity.Standard, ZuneDensity.Comfortable }, ZuneListRules.Densities.ToArray());
        Assert.Equal(ZuneStyleKeys.DensityCompact, ZuneListRules.DensityLabelKey(ZuneDensity.Compact));
        Assert.Equal(ZuneStyleKeys.DensityStandard, ZuneListRules.DensityLabelKey(ZuneDensity.Standard));
        Assert.Equal(ZuneStyleKeys.DensityComfortable, ZuneListRules.DensityLabelKey(ZuneDensity.Comfortable));
        Assert.Equal(ZuneListRules.DensityQualifiedId, "style." + ZuneDensityOption.Option.Id);
    }

    [Fact]
    public void The_playing_row_is_accent_ink_with_no_plate()
    {
        var accent = ColorF.FromRgba(0xFF, 0x3C, 0xA6);
        var rest = ColorF.FromRgba(0x9C, 0x9C, 0x9C);
        Assert.Equal(accent, ZuneListRules.RowInk(isNow: true, accent, rest));
        Assert.Equal(rest, ZuneListRules.RowInk(isNow: false, accent, rest));
        Assert.False(ZuneListRules.PlayingRowHasPlate);
        Assert.Equal(accent, ZuneListRules.LyricsLineInk(active: true, accent, rest));
        Assert.Equal(rest, ZuneListRules.LyricsLineInk(active: false, accent, rest));
    }

    [Fact]
    public void Zune_publishes_one_modern_skin_with_artwork_and_its_detail_table_records()
    {
        var policies = new ZuneStructure().Policies(OptionValues.Empty, default);
        Assert.Same(ZuneListRules.Lists, policies.Lists);
        Assert.Equal(TrackRowStyle.Modern, policies.Lists.RowStyle);
        Assert.True(policies.Lists.TrackArtwork);
        Assert.Same(ZuneListRules.DetailTiers, policies.DetailTiers);

        var compact = policies.DetailTiers.For(WidthTier.Compact).Table;
        Assert.True(compact.CompactRows);
        Assert.False(compact.AlbumColumn || compact.PlaysColumn || compact.DateColumn || compact.ListIndex);
        Assert.Equal(0f, compact.ModernRow);   // no floor: the density's rows

        var small = policies.DetailTiers.For(WidthTier.Small).Table;
        Assert.False(small.CompactRows);
        Assert.True(small.AlbumColumn);   // Z-Sizes-Breakpoints "album from 900": where the table ladder admits it
        Assert.False(small.PlaysColumn);

        // Standard and Huge: every lane the width admits (an album's plays and time lanes, Z-Album).
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var t = policies.DetailTiers.For(tier).Table;
            Assert.False(t.CompactRows);
            Assert.True(t.AlbumColumn && t.PlaysColumn && t.DateColumn && t.ListIndex);
        }
    }

    [Fact]
    public void A_shelf_card_has_no_plate_padding_and_keeps_the_estimators_height()
    {
        // A5: the art is the card's whole width at the heading's x, the caption 6 under it, and the slack under the labels makes up the core's
        // estimate (cover(w - 16) + 24 of chrome) so a hydrating card never re-measures its row.
        Assert.Equal(6f, ZuneListRules.ShelfCaptionGap);
        Assert.Equal(2f, ZuneListRules.ShelfSlack(154f, 1f));
        Assert.Equal(2f, ZuneListRules.ShelfSlack(220f, 1f));
        Assert.True(ZuneListRules.ShelfSlack(240f, 16f / 9f) > 2f);
        foreach (float w in new[] { 120f, 154f, 220f, 289f })
        {
            float estimate = SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);
            float live = SurfaceGeometry.ShelfGutterTop + w + ZuneListRules.ShelfCaptionGap + SurfaceGeometry.CardTitleLineH
                         + SurfaceGeometry.CardLabelGap + SurfaceGeometry.CardCaptionLineH + ZuneListRules.ShelfSlack(w, 1f);
            Assert.Equal(estimate, live, 0.01f);
        }
    }

    [Fact]
    public void A_day_headings_count_aligns_to_the_rows_chevron_glyph()
    {
        // B-Recents: the rows end in [when 64][chevron 32][... 32] 8 apart inside an 8 pad, so the chevron glyph (12) ends 8 + 32 + 8 + 10 in; the stacked Compact row
        // has the 40 chevron lane alone, 8 + 14 in. The heading's rule never collapses below its floor.
        Assert.Equal(58f, ZuneRecentsRules.CountInset(stacked: false, chevronLane: 32f, moreLane: 32f, gap: 8f));
        Assert.Equal(22f, ZuneRecentsRules.CountInset(stacked: true, chevronLane: 40f, moreLane: 32f, gap: 8f));
        Assert.True(ZuneRecentsRules.DayRuleMin > 0f);
    }

    [Fact]
    public void The_command_bars_first_glyph_and_the_songs_heading_stand_on_the_lists_x()
    {
        // A5: a padded first command pulls back by its own left pad; the page bar's disc by the 2 the core adds to line it up with a card's words.
        Assert.Equal(-ZuneListRules.CommandPad.Left, ZuneListRules.BarLeadShift(pageLead: false, pageBarInset: 2f));
        Assert.Equal(-2f, ZuneListRules.BarLeadShift(pageLead: true, pageBarInset: 2f));
        Assert.Equal(0f, ZuneDetailRules.SongsHeadingInset);
    }

    [Fact]
    public void The_compact_targets_and_the_unavailable_bar_follow_the_window_tier()
    {
        Assert.Equal(40f, ZuneListRules.CommandEdgeFor(WidthTier.Compact));
        Assert.Equal(32f, ZuneListRules.CommandEdgeFor(WidthTier.Standard));
        Assert.True(ZuneListRules.CompactTarget >= 40f);
        Assert.False(ZuneListRules.UnavailableExplains(WidthTier.Small));
        Assert.True(ZuneListRules.UnavailableExplains(WidthTier.Standard));
        Assert.True(ZuneListRules.UnavailableExtent(WidthTier.Compact) > ZuneListRules.UnavailableExtent(WidthTier.Standard));
    }

    [Fact]
    public void The_light_lyric_ladder_lifts_the_rows_beside_the_active_line_to_3_to_1_and_keeps_the_dark_ladder_the_cores()
    {
        int Row(int index, int active) => Lyrics.Emphasis.Pack(index, active, -1);
        // Dark, and a podcast that does not own playback, are the core's ladder untouched.
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(Lyrics.Emphasis.OpacityOf(Row(i, 6)), ZuneListRules.LyricRowOpacity(false, true, Row(i, 6), light: false));
            Assert.Equal(1f, ZuneListRules.LyricRowOpacity(true, false, Row(i, 6), light: true));
        }
        Assert.Equal(1f, ZuneListRules.LyricRowOpacity(false, true, Row(6, 6), light: true));
        // The line either side of the active one: dark ink at 56 % on the lightest ground (#f5f5f5) and the pink wash (#e7c4d8) reads 3 : 1 or better.
        float next = ZuneListRules.LyricRowOpacity(false, true, Row(7, 6), light: true), prev = ZuneListRules.LyricRowOpacity(false, true, Row(5, 6), light: true);
        Assert.Equal(next, prev);
        foreach (var ground in new[] { ColorF.FromRgba(0xF5, 0xF5, 0xF5), ColorF.FromRgba(0xE7, 0xC4, 0xD8) })
        {
            var ink = ColorContrast.Over(ColorF.FromRgba(0x0A, 0x0A, 0x0A) with { A = next }, ground);
            Assert.True(ColorContrast.Ratio(ink, ground) >= 3f, "the neighbour line on " + ground);
        }
        // Each ring further out is dimmer and none is as quiet as the dark ladder's .10 floor, and a light row is never dimmer than its dark twin.
        float last = 1f;
        for (int d = 1; d <= 8; d++)
        {
            float future = ZuneListRules.LyricRowOpacity(false, true, Row(6 + d, 6), light: true);
            Assert.True(future <= last && future >= 0.18f, "ring " + d);
            Assert.True(future >= Lyrics.Emphasis.OpacityOf(Row(6 + d, 6)));
            last = future;
        }
        Assert.True(ZuneListRules.LyricRowOpacity(false, true, Row(4, 6), light: true) >= Lyrics.Emphasis.OpacityOf(Row(4, 6)));   // a past row too
    }
}
