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

    // ── the Huge aside and the Huge 4K record (contract 1.21: C-Album-Huge, C-Album-Huge4K, C-Playlist-Huge, C-PlaylistFollowed-Huge) ─────────

    [Theory]
    [InlineData(WidthTier.Compact)]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    public void No_tier_below_Huge_docks_an_aside_or_rests_the_insights_open(WidthTier tier)
    {
        var r = ClassicDetailTiers.For(tier);
        Assert.False(r.Core.AsideDocked);
        Assert.False(r.Core.InsightsRestOpen);
        Assert.Null(r.Aside);
    }

    [Fact]
    public void Huge_docks_the_boards_asides_and_rests_the_insights_open()
    {
        var h = ClassicDetailTiers.Huge;
        Assert.True(h.Core.AsideDocked);
        Assert.True(h.Core.InsightsRestOpen);
        var g = Assert.IsType<ClassicDetailAside>(h.Aside);
        Assert.Equal(520f, g.WidthFor(DetailKind.Album, body: true));           // C-Album-Huge
        Assert.Equal(440f, g.WidthFor(DetailKind.Playlist, body: true));        // C-Playlist-Huge: recommendations + insights
        Assert.Equal(400f, g.WidthFor(DetailKind.Playlist, body: false));       // C-PlaylistFollowed-Huge: the insights alone
        Assert.Equal(270f, g.ArtistBand);
        Assert.Equal(96f, g.FanArt);
        Assert.Equal(4, g.FanColumns);
    }

    [Fact]
    public void The_list_keeps_its_inset_before_the_asides_hairline()
    {
        var g = ClassicDetailTiers.Huge.Aside!;
        // C-Album-Huge: the list column's 8 right pad, then the hairline 24 down with 16 either side, then the aside 24 in from the card's edge.
        Assert.Equal(8f, g.ListInset);
        Assert.Equal(new Edges4(16f, 24f, 16f, 0f), g.HairlineMargin);
        Assert.Equal(33f, g.HairlineSpan);
        Assert.Equal(553f, g.SlotWidthFor(DetailKind.Album, body: true));
        Assert.Equal(24f, g.PadFor(DetailKind.Album).Right);
        Assert.Equal(24f, g.PadFor(DetailKind.Album).Top);
        Assert.Equal(20f, g.PadFor(DetailKind.Playlist).Top);
        Assert.Equal(496f, g.ContentWidthFor(DetailKind.Album, body: true));
        Assert.Equal(118f, g.FanCellFor(496f));                                 // four fans 8 apart in the 496 measure
    }

    [Fact]
    public void Huge_4K_is_the_wider_album_board()
    {
        var w = ClassicDetailTiers.Huge4K;
        Assert.Equal(WidthTier.Huge, w.Tier);
        Assert.Equal(360f, w.Core.RailRest);                                     // C-Album-Huge4K: the 360 rail, its art 336
        Assert.Equal(640f, w.Aside!.WidthFor(DetailKind.Album, body: true));
        Assert.Equal(360f, w.Aside.ArtistBand);
        Assert.Equal(120f, w.Aside.FanArt);
        Assert.Equal(16f, w.Aside.FanRowGap);
        Assert.Equal(440f, w.Aside.WidthFor(DetailKind.Playlist, body: true));  // no 4K playlist board: Huge's
        Assert.True(w.Core.AsideDocked);
        Assert.Equal(2400f, w.PageMaxWidth);                                     // the plan's 2400 cap stays (owner's call)
        Assert.Same(w.Core, ClassicDetailTiers.Policy.HugeWide);
    }

    [Theory]
    [InlineData(2560f, false)]
    [InlineData(3199f, false)]
    [InlineData(3200f, true)]
    [InlineData(3840f, true)]
    public void A_Huge_window_reads_the_4K_record_from_3200(float width, bool wide)
    {
        var facts = WindowTiers.Resolve(ClassicTierFixture.Tiers, width, 1440f, width - 320f);
        Assert.True(facts.Window >= WidthTier.Huge);
        var record = ClassicDetailTiers.Of(facts);
        Assert.Same(wide ? ClassicDetailTiers.Huge4K : ClassicDetailTiers.Huge, record);
        Assert.Same(record.Core, ClassicDetailTiers.Policy.For(facts));          // the core reads the same record the views read
    }

    [Fact]
    public void The_4K_rung_holds_40_narrowing()
    {
        var edge = ClassicDetailTiers.HugeWide.Edge;
        Assert.Equal(ClassicDetailTiers.HugeWideRung, ClassicDetailTiers.HugeWide.Id);
        Assert.Equal(new TierEdge(3200f, NarrowHold: 40f, WidenHold: 0f), edge);
        Assert.True(ClassicTierFixture.Tiers.IndexOfRung(ClassicDetailTiers.HugeWideRung) >= 0);
    }

    // ── the remaining Compact and Small board items (fix/remaining-pages: C-Playlist-Compact, C-Liked-Compact, C-Empty-Compact, C-Album-Small) ───────

    [Fact]
    public void Compact_drops_a_lists_index_lane_and_draws_filter_as_a_glyph()
    {
        var c = ClassicDetailTiers.Compact;
        Assert.False(c.Core.Table.ListIndex);   // C-TrackList compact: a playlist's / Liked's Modern row has no "#"
        Assert.True(c.FilterGlyph);
        Assert.Same(ClassicEmptyLead.Compact, c.Lead);
        Assert.False(c.SortGlyph);              // the Compact bar LEADS with the sort word
        Assert.Same(ClassicDetailRail.Standard, c.Rail);   // the Compact page is the vertical arm: no rail
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            var r = ClassicDetailTiers.For(tier);
            Assert.True(r.Core.Table.ListIndex);
            Assert.False(r.FilterGlyph);
            Assert.Same(ClassicEmptyLead.Roomy, r.Lead);   // C-Empty draws the lead at the other tiers too
        }
    }

    [Fact]
    public void The_empty_playlists_lead_is_the_boards_at_every_tier_and_reserves_its_height()
    {
        // C-Empty-Compact: 16 over and 4 under the 20/28 message (+ its 16 line, 4 apart), a 40 field, the blocks 12 apart.
        var c = ClassicEmptyLead.Compact;
        Assert.False(c.LargeTitle);
        Assert.Equal(16f, c.MessagePadTop);
        Assert.Equal(4f, c.MessagePadBottom);
        Assert.Equal(28f, c.TitleLine);
        Assert.Equal(40f, c.FieldH);
        Assert.Equal(12f, c.BlockGap);
        Assert.Equal(68f, c.MessageHeight);   // 16 + 28 + 4 + 16 + 4
        Assert.Equal(132f, c.Height);          // 68 + 12 + 40 + 12
        // C-Empty (Small, Standard, Huge): 24 over and 8 under the 28/36 message, a 32 field, the blocks 14 apart.
        var r = ClassicEmptyLead.Roomy;
        Assert.True(r.LargeTitle);
        Assert.Equal(24f, r.MessagePadTop);
        Assert.Equal(8f, r.MessagePadBottom);
        Assert.Equal(36f, r.TitleLine);
        Assert.Equal(32f, r.FieldH);
        Assert.Equal(14f, r.BlockGap);
        Assert.Equal(88f, r.MessageHeight);   // 24 + 36 + 4 + 16 + 8
        Assert.Equal(148f, r.Height);          // 88 + 14 + 32 + 14
        // Only Compact has the 40 field (a target); every other tier is the stock 32.
        Assert.Equal(ClassicDetailTiers.CompactTarget, c.FieldH);
        Assert.Equal(ClassicDetailTiers.Target, r.FieldH);
        // Huge 4K reads Huge's record, so it draws the roomy lead too.
        Assert.Same(ClassicEmptyLead.Roomy, ClassicDetailTiers.For(WidthTier.Huge4K).Lead);
        // The two reserved heights differ by 16: a crossing eases that and nothing else.
        Assert.Equal(16f, r.Height - c.Height);
    }

    [Fact]
    public void Small_draws_sort_as_a_glyph_and_pads_its_rail_16_12_with_rows_10_apart()
    {
        var s = ClassicDetailTiers.Small;
        Assert.True(s.SortGlyph);
        Assert.False(s.FilterGlyph);            // C-Album-Small keeps the Filter word
        var rail = s.Rail;
        Assert.Equal(new Edges4(12f, 16f, 12f, 16f), rail.Padding);
        Assert.Equal(10f, rail.Gap);
        Assert.True(rail.BreakAfterPrimary);    // the Play row breaks after Play
        Assert.Equal(200f, rail.ContentWidth(224f));   // the 224 rail keeps its 200 cover
        Assert.Equal(ClassicDetailRail.Standard.ContentWidth(224f), rail.ContentWidth(224f));   // so a crossing never resizes the cover
    }

    [Theory]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    public void Standard_and_Huge_keep_the_tuned_rail_and_the_labelled_sort(WidthTier tier)
    {
        var r = ClassicDetailTiers.For(tier);
        Assert.False(r.SortGlyph);
        Assert.Same(ClassicDetailRail.Standard, r.Rail);
        Assert.Equal(ClassicTiers.RailPadding, r.Rail.Padding);
        Assert.Equal(Wavee.Detail.RailLayout.Gap, r.Rail.Gap);
        Assert.False(r.Rail.BreakAfterPrimary);
    }

    [Fact]
    public void A_hidden_index_lane_leaves_the_tracks_the_floor_and_the_art_centre()
    {
        var compact = new Track.ColumnSet(Album: false, By: false, Date: false, Plays: false, Heart: false, Thumb: true, HeartTrailing: true, Compact: true);
        var list = compact with { IndexHidden = true };
        // The Compact list row: art · title · heart · more (C-TrackList compact, a playlist).
        Assert.Equal(4, Track.TracksFor(in list, 40f).Length);
        Assert.Equal(Track.TracksFor(in compact, 40f).Length - 1, Track.TracksFor(in list, 40f).Length);
        var lanes = Track.TableRules.LanesOf(in list);
        Assert.True(lanes.IndexHidden);
        float gap = Track.RowMetrics.ColGapFor(list.Tier), pad = Track.RowMetrics.PadXFor(list.Tier);
        Assert.Equal(Track.Lane.Num + gap,
            Track.TableRules.MinWidthFor(Track.TableRules.LanesOf(in compact), gap, pad) - Track.TableRules.MinWidthFor(in lanes, gap, pad), 3);
        Assert.True(Track.TableRules.Relieve(in lanes, 3).IndexHidden);   // relief never brings the lane back
        // The art centre moves left by the lane and its gap.
        Assert.Equal(Track.Lane.Num + gap, Track.RowMetrics.ArtCentreIndent(in compact, 40f) - Track.RowMetrics.ArtCentreIndent(in list, 40f), 3);
    }

    [Theory]
    [InlineData(0, 3)]    // an empty playlist: the hero, the chrome and the section alone (C-Empty-Compact)
    [InlineData(1, 4)]
    [InlineData(50, 53)]
    public void The_hero_arm_appends_the_recommendations_after_its_rows(int visible, int count)
    {
        Assert.Equal(count, Wavee.Detail.VerticalLayout.ItemCount(visible, recommendations: true));
        Assert.Equal(VerticalItemRole.Recommendations, Wavee.Detail.VerticalLayout.ItemRole(2 + visible, visible, recommendations: true));
        if (visible > 0) Assert.Equal(VerticalItemRole.ExpandableTrack, Wavee.Detail.VerticalLayout.ItemRole(1 + visible, visible, recommendations: true));
        // Without the section the slot map is the old one (an empty list keeps its placeholder slot).
        Assert.Equal(2 + Math.Max(1, visible), Wavee.Detail.VerticalLayout.ItemCount(visible));
        Assert.Equal(VerticalItemRole.Empty, Wavee.Detail.VerticalLayout.ItemRole(2 + visible, visible));
    }
}
