// ── Wavee.Tests/Theming/ZuneDetailTiersTests.cs — Zune's detail tiers, pure (themes plan 3.5, 6.3; P5 ZT5) ───────────────────────────
//
// The rules lane ZT5 finishes, against Z-Album*, Z-Playlist-*, Z-Liked*, Z-Show*, Z-Episode* and Z-Sizes-Breakpoints:
//
//   · the table's form and lane caps per window tier (`ZuneListRules.DetailTiers`: the Compact row, the Album lane from Small, every lane from
//     Standard), the aside from Standard, the insights resting open from Huge, the identity column's rest 240 / 320 / 400 and the wide twin
//     the core reads past the third pane's rung (a window of 3200);
//   · the insights' width: the sheet never runs past a 300 page; the aside's insights-alone width per content tier;
//   · the panorama step by kind: a playlist, a show and an episode on the same rule (64/72 up to Standard, 150/160 at Huge), the episode at
//     Standard 40/48 over two lines, Liked Songs' own step, an album's the record's;
//   · the short window folds the panorama (the identity column leads with the title) and the Compact touch minimum.
//
// Pure: no window is mounted. The mounted twin is `Wavee.ShellTests\ZuneDetailTierMountedTests.cs`.

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneDetailTiersTests
{
    static readonly WidthTier[] AllTiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    static TierFacts Facts(float width, float height) => WindowTiers.Resolve(ZuneTiers.Policy, width, height, width);

    // ── the table, the aside, the insights and the rail by window tier ───────────────────────────────────────────────────

    [Fact]
    public void The_detail_table_form_follows_the_window_tier()
    {
        var tiers = ZuneListRules.DetailTiers;

        var compact = tiers.For(WidthTier.Compact);
        Assert.True(compact.Table.CompactRows);
        Assert.False(compact.Table.AlbumColumn || compact.Table.PlaysColumn || compact.Table.DateColumn || compact.Table.ListIndex);

        // Small (Z-Sizes-Breakpoints "album from 900"): the Album lane may show where the table ladder admits it; never Plays; the date only on a quiet list.
        var small = tiers.For(WidthTier.Small);
        Assert.False(small.Table.CompactRows);
        Assert.True(small.Table.AlbumColumn);
        Assert.False(small.Table.PlaysColumn || small.Table.DateColumn);
        Assert.True(small.Table.ListIndex);

        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var t = tiers.For(tier).Table;
            Assert.True(t.AlbumColumn && t.PlaysColumn && t.DateColumn && t.ListIndex, tier + ": every lane the width admits");
            Assert.Equal(0f, t.ModernRow);   // the user's density wins at every tier (P5 decision 3)
        }
    }

    [Fact]
    public void The_aside_docks_from_standard_and_the_insights_rest_open_from_huge()
    {
        var tiers = ZuneListRules.DetailTiers;
        Assert.False(tiers.For(WidthTier.Compact).AsideDocked);
        Assert.False(tiers.For(WidthTier.Small).AsideDocked);
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.True(tiers.For(tier).AsideDocked, tier + ": the aside docks");

        // The insights are a sheet below Standard, the aside's section (closed until asked) at Standard, and a column that rests open from Huge.
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard })
            Assert.False(tiers.For(tier).InsightsRestOpen, tier + ": the insights wait for the reader");
        Assert.True(tiers.For(WidthTier.Huge).InsightsRestOpen);
        Assert.True(tiers.HugeWide!.InsightsRestOpen);
    }

    [Fact]
    public void The_identity_column_rests_at_the_records_width_and_huge_4k_is_the_wide_twin_past_the_third_pane()
    {
        var tiers = ZuneListRules.DetailTiers;
        Assert.Equal(ZuneDetailRules.Standard.RailW, tiers.For(WidthTier.Standard).RailRest);
        Assert.Equal(ZuneDetailRules.Huge.RailW, tiers.For(WidthTier.Huge).RailRest);
        Assert.Equal(ZuneDetailRules.Huge4K.RailW, tiers.HugeWide!.RailRest);
        Assert.Equal(400f, tiers.HugeWide.RailRest);
        Assert.Equal(ZuneTiers.PaneThirdRung, tiers.HugeWideRung);
        Assert.True(ZuneTiers.Policy.IndexOfRung(ZuneTiers.PaneThirdRung) >= 0, "the rung the twin names is a rung of Zune's policy");

        var huge = Facts(2560f, 1440f);
        var wide = Facts(3840f, 2160f);
        Assert.False(tiers.IsHugeWide(in huge));
        Assert.True(tiers.IsHugeWide(in wide));
        Assert.Same(tiers.Huge, tiers.For(in huge));
        Assert.Same(tiers.HugeWide, tiers.For(in wide));
        // The edge itself (a fresh resolve commits at the line, narrowing holds 24).
        var edge = Facts(3200f, 1440f);
        var below = Facts(3199f, 1440f);
        Assert.True(tiers.IsHugeWide(in edge));
        Assert.False(tiers.IsHugeWide(in below));
    }

    [Fact]
    public void The_policy_publishes_the_one_body_and_the_ladders()
    {
        var policies = new ZuneStructure().Policies(OptionValues.Empty, default);
        Assert.Same(ZuneListRules.DetailTiers, policies.DetailTiers);
        foreach (string id in new[] { DetailLadders.Mode, DetailLadders.Table, DetailLadders.Height })
            Assert.True(ZuneTiers.Policy.TryGetLadder(id, out _), id);
    }

    // ── the insights' width ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(300f, 300f)]   // a 300 page is covered edge to edge: the × stays on screen
    [InlineData(319f, 319f)]
    [InlineData(400f, 320f)]
    [InlineData(639f, 320f)]
    [InlineData(1008f, 342f)]
    [InlineData(1200f, 400f)]
    [InlineData(2400f, 400f)]
    public void The_sheet_never_runs_past_the_page(float page, float expected)
    {
        Assert.Equal(expected, ZuneDetailRules.SheetWidthFor(page));
        Assert.True(ZuneDetailRules.SheetWidthFor(page) <= page);
    }

    [Fact]
    public void An_unmeasured_page_gets_the_minimum_sheet()
    {
        Assert.Equal(ZuneDetailRules.SheetMinW, ZuneDetailRules.SheetWidthFor(float.NaN));
        Assert.Equal(ZuneDetailRules.SheetMinW, ZuneDetailRules.SheetWidthFor(0f));
    }

    [Fact]
    public void An_aside_of_insights_alone_takes_the_bentos_width_per_tier()
    {
        Assert.Equal(0f, ZuneDetailTiers.For(WidthTier.Compact).InsightsW);
        Assert.Equal(0f, ZuneDetailTiers.For(WidthTier.Small).InsightsW);
        Assert.Equal(320f, ZuneDetailTiers.For(WidthTier.Standard).InsightsW);
        Assert.Equal(420f, ZuneDetailTiers.For(WidthTier.Huge).InsightsW);   // Z-Liked-Huge draws a 420 column
        Assert.Equal(440f, ZuneDetailTiers.For(WidthTier.Huge4K).InsightsW);
        foreach (var tier in AllTiers)
        {
            var lay = ZuneDetailTiers.For(tier);
            Assert.Equal(lay.AsideW > 0f, lay.InsightsW > 0f);
            Assert.True(lay.InsightsW >= ZuneDetailRules.SheetMinW || lay.InsightsW == 0f, tier + ": the bento keeps the width it was drawn for");
        }
    }

    // ── the panorama step: one per tier, the album's, for every kind ─────────────────────────────────────────────────────

    /// <summary>The size a record sets its panorama title at: its own step where it carries one (150 at Huge), else the role's (64 compact, 120 panorama).</summary>
    static float StepOf(ZuneDetailLayout lay) => lay.PanoramaSize > 0f ? lay.PanoramaSize : lay.PanoramaLarge ? 120f : 64f;

    [Theory]
    [InlineData(WidthTier.Compact, 64f)]
    [InlineData(WidthTier.Small, 64f)]
    [InlineData(WidthTier.Standard, 120f)]
    [InlineData(WidthTier.Huge, 150f)]
    [InlineData(WidthTier.Huge4K, 150f)]
    public void Every_detail_kind_sets_its_title_at_the_tiers_large_step(WidthTier tier, float size)
    {
        // Zune is a large typeface (owner directive): a playlist, Liked Songs, a show and an episode all read the SAME record as an album, so none of
        // them has a smaller step of its own (Liked was 40/64, a playlist and a show 64 at Standard, an episode 40).
        var lay = ZuneDetailTiers.For(tier);
        Assert.Equal(size, StepOf(lay));
        Assert.Equal(ZuneDetailRules.PanoramaHFor(size), lay.PanoramaH);
        Assert.True(lay.PanoramaH >= size * 1.25f + 8f - 0.5f, "the band clears the title's descender");
    }

    [Fact]
    public void The_playlist_record_carries_no_panorama_step_of_its_own()
    {
        Assert.DoesNotContain(typeof(ZunePlaylistLayout).GetProperties(), p => p.Name.Contains("Panorama"));
        Assert.Equal(120f, StepOf(ZuneDetailRules.Standard));      // the Standard playlist, Liked, show and episode read this one
    }

    [Fact]
    public void A_long_title_wraps_over_two_lines_and_the_band_hugs_it_instead_of_shrinking_or_ellipsising()
    {
        foreach (var tier in AllTiers)
        {
            var lay = ZuneDetailTiers.For(tier);
            Assert.Equal(2, lay.PanoramaLines);
            Assert.True(lay.PanoramaWraps, tier + ": a title wraps by whole words and the band (its height a floor) grows with it");
        }
        Assert.True(ZuneDetailTiers.Hero.PanoramaWraps);
    }

    // ── the short window ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(800f, 330f)]
    [InlineData(1440f, 600f)]
    [InlineData(2560f, 699f)]
    public void A_short_window_folds_the_panorama_and_the_column_leads_with_the_title(float width, float height)
    {
        var facts = Facts(width, height);
        Assert.Equal(HeightTier.Short, facts.Height);
        var lay = ZuneDetailTiers.Of(in facts);
        Assert.Equal(0f, lay.PanoramaH);
        Assert.True(lay.RailTitle);
        // A folded band never wraps (its title is hidden on one line) and a wrapping title does not reopen it.
        Assert.False(lay.PanoramaWraps);
    }

    [Theory]
    [InlineData(1440f, 700f)]
    [InlineData(1440f, 960f)]
    public void A_regular_window_keeps_the_panorama(float width, float height)
    {
        var facts = Facts(width, height);
        Assert.Equal(HeightTier.Regular, facts.Height);
        var lay = ZuneDetailTiers.Of(in facts);
        Assert.True(lay.PanoramaH > 0f);
        Assert.False(lay.RailTitle);
    }

    // ── the Compact touch minimum ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_compact_link_is_at_least_40_tall_and_other_tiers_keep_the_line()
    {
        Assert.Equal(40f, ZuneDetailRules.HitHeight(WidthTier.Compact, 24f));
        Assert.Equal(48f, ZuneDetailRules.HitHeight(WidthTier.Compact, 48f));
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(24f, ZuneDetailRules.HitHeight(tier, 24f));
        Assert.True(ZuneDetailTiers.Hero.RingSize >= ZuneDetailRules.CompactHit);
        Assert.True(ZuneDetailTiers.Hero.Target >= ZuneDetailRules.CompactHit);
        Assert.True(ZuneDetailRules.SheetClose >= ZuneDetailRules.CompactHit);
        Assert.True(ZuneDetailRules.StripTarget >= ZuneDetailRules.CompactHit);
    }

    [Fact]
    public void The_show_header_cover_is_the_boards_120_and_the_short_fold_is_a_title_over_the_rings()
    {
        Assert.Equal(120f, ZuneDetailRules.ShowCover);
        // The folded header: its air above and its rows' gap shrink; the title row (32) and the ring row (40) are all that stand.
        Assert.True(ZuneDetailRules.ShowPadTopShort < ZuneDetailRules.ShowPadTop);
        Assert.True(ZuneDetailRules.ShowRowsGapShort < ZuneDetailRules.ShowRowsGap);
        float folded = ZuneDetailRules.ShowPadTopShort + 32f + ZuneDetailRules.ShowRowsGapShort + ZuneDetailTiers.Hero.RingSize + ZuneDetailRules.ShowPadBottom;
        Assert.True(folded <= ZuneTiers.Policy.ShortFloor, $"the folded header ({folded}) fits the 126 a 330 window leaves the page");
    }
}
