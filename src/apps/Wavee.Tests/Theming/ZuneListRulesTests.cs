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
        Assert.False(small.AlbumColumn || small.PlaysColumn);

        // Standard and Huge: every lane the width admits (an album's plays and time lanes, Z-Album).
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var t = policies.DetailTiers.For(tier).Table;
            Assert.False(t.CompactRows);
            Assert.True(t.AlbumColumn && t.PlaysColumn && t.DateColumn && t.ListIndex);
        }
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
}
