// ── Wavee.Tests/Theming/ClassicDetailTiersTests.cs — Classic's detail sub-ladders and the detail pages' tier records (themes plan 3.5, 3.6; TC5) ──
//
// The pure half of TC5: the three sub-ladders that left core `Detail.Breakpoints` (`Tiers.Detail.cs`) and hold like the window tiers, the board
// sizes landing in the boards' page modes, the per-tier records (what the core reads through `ThemePolicies.DetailTiers` and what Classic's views
// read), and the core rules that consume them (the rail's Huge rest, the Compact row form's lanes). The mounted half is
// `Wavee.ShellTests/TierLayoutTests.Detail.cs`.

using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using Breakpoints = Wavee.Detail.Breakpoints;
using RailPolicy = Wavee.Detail.RailPolicy;

namespace Wavee.Tests.Theming;

public sealed class ClassicDetailTiersTests
{
    // ── the ladders ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_mode_ladder_is_the_old_band_written_as_tier_edges()
    {
        var m = ClassicDetailTiers.Mode;
        Assert.Equal(3, m.EdgeCount);
        Assert.Equal(new TierEdge(560f, NarrowHold: 20f, WidenHold: 20f), m.EdgeAt(0));   // vertical below 540, two-column from 580
        Assert.Equal(new TierEdge(660f, NarrowHold: 0f, WidenHold: 24f), m.EdgeAt(1));
        Assert.Equal(new TierEdge(820f, NarrowHold: 0f, WidenHold: 24f), m.EdgeAt(2));
    }

    [Fact]
    public void The_table_ladder_narrows_at_once_and_widens_with_24()
    {
        var t = ClassicDetailTiers.Table;
        float[] edges = [300f, 340f, 440f, 560f, 720f, 860f];
        Assert.Equal(edges.Length, t.EdgeCount);
        for (int i = 0; i < edges.Length; i++) Assert.Equal(new TierEdge(edges[i], NarrowHold: 0f, WidenHold: 24f), t.EdgeAt(i));
    }

    [Fact]
    public void The_height_rungs_are_760_and_900_held_like_the_window_height()
    {
        var h = ClassicDetailTiers.Height;
        Assert.Equal(new TierEdge(760f, NarrowHold: 0f, WidenHold: 40f), h.EdgeAt(0));
        Assert.Equal(new TierEdge(900f, NarrowHold: 0f, WidenHold: 40f), h.EdgeAt(1));
        Assert.Equal(0, h.Resolve(759f, -1));
        Assert.Equal(1, h.Resolve(760f, -1));
        Assert.Equal(2, h.Resolve(900f, -1));
        Assert.Equal(1, h.Resolve(920f, 1));   // a growing window holds the regular rung until 940
        Assert.Equal(2, h.Resolve(940f, 1));
        Assert.Equal(1, h.Resolve(899f, 2));   // and drops at once
    }

    [Fact]
    public void Every_hold_of_the_detail_ladders_behaves_like_a_tier_edge()
    {
        // Each edge: a fresh resolve uses the edge itself; from below the value must reach At + WidenHold; from above it must stay at
        // At - NarrowHold or more (TierLadder's own contract, so the frame's UseTier and these pure reads agree).
        foreach (var ladder in new[] { ClassicDetailTiers.Mode, ClassicDetailTiers.Table, ClassicDetailTiers.Height })
            for (int i = 0; i < ladder.EdgeCount; i++)
            {
                var e = ladder.EdgeAt(i);
                Assert.Equal(i + 1, ladder.Resolve(e.At, -1));
                Assert.Equal(i, ladder.Resolve(e.At + e.WidenHold - 0.5f, i));
                Assert.Equal(i + 1, ladder.Resolve(e.At + e.WidenHold, i));
                Assert.Equal(i + 1, ladder.Resolve(e.At - e.NarrowHold, i + 1));
                Assert.Equal(i, ladder.Resolve(e.At - e.NarrowHold - 0.5f, i + 1));
            }
    }

    [Fact]
    public void Classic_names_the_three_ladders_by_the_cores_ids()
    {
        var policy = ClassicTiers.Policy;
        Assert.Empty(policy.Validate());
        Assert.Same(ClassicDetailTiers.Mode, DetailLadders.Of(policy, DetailLadders.Mode));
        Assert.Same(ClassicDetailTiers.Table, DetailLadders.Of(policy, DetailLadders.Table));
        Assert.Same(ClassicDetailTiers.Height, DetailLadders.Of(policy, DetailLadders.Height));
    }

    [Fact]
    public void A_structure_with_no_detail_ladders_gets_the_shell_as_it_shipped()
    {
        // The widest arm, tier 0 and the tall rung at every usable size.
        Assert.Equal(0, DetailLadders.ModeOf(DetailLadders.Of(TierPolicy.Standard, DetailLadders.Mode).Resolve(300f, -1)));
        Assert.Equal(0, DetailLadders.TierOf(DetailLadders.Of(TierPolicy.Standard, DetailLadders.Table).Resolve(300f, -1)));
        Assert.Equal(2, DetailLadders.Of(null, DetailLadders.Height).Resolve(330f, -1));
        Assert.Same(DetailTierPolicy.Default, ThemePolicies.Default.DetailTiers);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 2)]
    public void The_index_maps_invert_the_mode_and_tier_orders(int index, int mode)
    {
        Assert.Equal(mode, DetailLadders.ModeOf(index));
        Assert.Equal(index, DetailLadders.IndexOfMode(mode));
        Assert.Equal(6 - index, DetailLadders.TierOf(index));
        Assert.Equal(index, DetailLadders.IndexOfTier(6 - index));
    }

    // ── the board sizes land in the boards' modes ─────────────────────────────────────────────────────────────────────

    /// <summary>The boards' page column (the window less the nav pane: none at Compact, the forced 48 rail at Small, the 280 / 320 pane at
    /// Standard / Huge) and the arm each board draws: C-Album-Compact the vertical hero, C-Album-Small the 224 rail (mode 1), C-Album and
    /// C-Album-Huge the wide arm. 3840 is the Huge 4K board's (Classic's Huge rules).</summary>
    [Theory]
    [InlineData(400f, 0f, Breakpoints.VerticalMode)]
    [InlineData(800f, 48f, 1)]
    [InlineData(1440f, 280f, 0)]
    [InlineData(2560f, 320f, 0)]
    [InlineData(3840f, 320f, 0)]
    [InlineData(300f, 0f, Breakpoints.VerticalMode)]
    [InlineData(527f, 0f, Breakpoints.VerticalMode)]
    public void The_board_sizes_land_in_the_boards_page_modes(float window, float nav, int mode)
        => Assert.Equal(mode, ClassicDetailTiers.ModeFor(window - nav, 0, initialized: false));

    [Fact]
    public void The_Small_board_rests_its_rail_at_224()
        => Assert.Equal(224f, RailPolicy.RestingWidth(RailPolicy.DefaultWidthFor(RailScope.Album), RailScope.Album,
            ClassicDetailTiers.ModeFor(800f - 48f, 0, initialized: false), RailPolicy.MaxWidthForPage(752f, 1)));

    [Fact]
    public void A_vertical_exit_takes_the_holds_of_the_edges_it_crosses()
    {
        Assert.Equal(Breakpoints.VerticalMode, ClassicDetailTiers.ModeFor(579f, Breakpoints.VerticalMode));
        Assert.Equal(2, ClassicDetailTiers.ModeFor(580f, Breakpoints.VerticalMode));
        Assert.Equal(1, ClassicDetailTiers.ModeFor(843f, Breakpoints.VerticalMode));   // not the nominal wide arm
        Assert.Equal(0, ClassicDetailTiers.ModeFor(844f, Breakpoints.VerticalMode));
        Assert.Equal(2, ClassicDetailTiers.ModeFor(545f, 0));                          // narrowing holds the two-column arm to 540
        Assert.Equal(Breakpoints.VerticalMode, ClassicDetailTiers.ModeFor(539f, 0));
    }

    // ── the records ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_is_the_compact_row_with_40_targets()
    {
        var c = ClassicDetailTiers.Compact;
        Assert.Equal(WidthTier.Compact, c.Tier);
        Assert.Equal(40f, c.Target);
        Assert.True(c.SortLeadsBar);
        Assert.True(c.SelectionTinted);
        Assert.False(c.InsightsDocked);
        var t = c.Core.Table;
        Assert.True(t.CompactRows);
        Assert.False(t.AlbumColumn);
        Assert.False(t.PlaysColumn);
        Assert.False(t.DateColumn);
        Assert.Equal(56f, t.RowFor(classic: false, 48f));   // exact in the Compact form (C-TrackList compact)
        Assert.Equal(44f, t.RowFor(classic: true, 40f));
        Assert.Equal(40f, t.ArtFor(32f));
        Assert.Equal(56f, t.RowFor(classic: false, 64f));   // even under a larger density
        Assert.Equal(8f, c.Core.HeroActionExtra);
    }

    [Fact]
    public void Small_drops_the_album_and_plays_lanes()
    {
        var t = ClassicDetailTiers.Small.Core.Table;
        Assert.False(t.CompactRows);
        Assert.False(t.AlbumColumn);
        Assert.False(t.PlaysColumn);
        Assert.False(t.DateColumn);   // a quiet list (Liked) keeps its date: the core reads DateColumn || QuietRows
        Assert.Equal(48f, t.RowFor(false, 48f));
        Assert.Equal(32f, ClassicDetailTiers.Small.Target);
    }

    [Fact]
    public void Standard_is_the_tuned_design()
    {
        Assert.Same(DetailTierLayout.Standard, ClassicDetailTiers.Standard.Core);
        Assert.False(ClassicDetailTiers.Standard.InsightsDocked);
        Assert.Equal(0f, ClassicDetailTiers.Standard.Core.RailRest);
    }

    [Fact]
    public void Huge_is_designed_not_scaled()
    {
        var h = ClassicDetailTiers.Huge;
        Assert.True(h.InsightsDocked);
        Assert.Equal(360f, ClassicDetailTiers.DockedInsightsWidth);
        Assert.Equal(320f, h.Core.RailRest);
        Assert.Equal(2400f, h.PageMaxWidth);
        Assert.Equal(56f, h.Core.Table.RowFor(false, 48f));   // the cozy rung (C-Album-Huge: density cozy)…
        Assert.Equal(64f, h.Core.Table.RowFor(false, 64f));   // …a floor, never a cap
        Assert.Equal(40f, h.Core.Table.ArtFor(32f));
        Assert.Same(h, ClassicDetailTiers.For(WidthTier.Huge4K));   // Huge 4K uses the Huge rules
    }

    [Fact]
    public void The_policy_carries_the_records_and_the_rail_title_rungs()
    {
        var p = ClassicDetailTiers.Policy;
        Assert.Same(ClassicDetailTiers.Compact.Core, p.For(WidthTier.Compact));
        Assert.Same(ClassicDetailTiers.Small.Core, p.For(WidthTier.Small));
        Assert.Same(ClassicDetailTiers.Standard.Core, p.For(WidthTier.Standard));
        Assert.Same(ClassicDetailTiers.Huge.Core, p.For(WidthTier.Huge4K));
        Assert.Same(p.Standard, p.For(TierFacts.Unknown));   // a view mounted alone reads Standard
        Assert.Equal(28f, p.RailTitleFor(0));
        Assert.Equal(28f, p.RailTitleFor(1));
        Assert.Equal(40f, p.RailTitleFor(2));
        Assert.Equal(3, p.DescriptionFor(0));
        Assert.Equal(6, p.DescriptionFor(1));
        Assert.Same(p, Wavee.Themes.Classic.ClassicPolicies.Bundle.DetailTiers);
    }

    // ── the core rules that read them ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    public void An_undragged_rail_rests_at_the_tiers_width_in_the_wide_arm_only(RailScope scope)
    {
        float stored = RailPolicy.DefaultWidthFor(scope);
        Assert.Equal(320f, RailPolicy.RestingWidth(stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth, wideRest: 320f));
        Assert.Equal(stored, RailPolicy.RestingWidth(stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth));
        Assert.Equal(RailPolicy.MidRestWidth, RailPolicy.RestingWidth(stored, scope, 1, RailPolicy.MaxWidth, wideRest: 320f));
        // A dragged scope keeps its own width at every tier.
        Assert.Equal(300f, RailPolicy.RestingWidth(300f, scope, RailPolicy.WideMode, RailPolicy.MaxWidth, wideRest: 320f));
    }

    [Fact]
    public void The_compact_row_form_drops_the_header_and_the_modern_duration_lane()
    {
        var full = new Track.ColumnSet(Album: false, By: false, Date: false, Plays: false, Heart: false, Thumb: true, HeartTrailing: true);
        var compact = full with { Compact = true };
        Assert.Equal(Track.TableRules.HeaderHeightFor(false), Track.TableRules.HeaderHeightFor(in full));
        Assert.Equal(0f, Track.TableRules.HeaderHeightFor(in compact));
        Assert.True(Track.RowMetrics.ShowDuration(in full));
        Assert.False(Track.RowMetrics.ShowDuration(in compact));
        Assert.True(Track.RowMetrics.ShowDuration(compact with { Classic = true }));   // a Classic row keeps its duration lane
        Assert.Equal(Track.TracksFor(in full, 40f).Length - 1, Track.TracksFor(in compact, 40f).Length);
        // The Compact board's row: # · art · title · heart · more (no duration lane, no chevron).
        Assert.Equal(5, Track.TracksFor(in compact, 40f).Length);
    }
}
