// ── Wavee.Tests/LibraryTiersTests.cs — the collection family's ladder and tier records (Classic's `Rules\Tiers.Library.cs` and `Tiers.Recents.cs`, TC7) ────
//
// The library's layout ladder (split from 600 of the page's own width, held 24 either way: replaces the 640 / 664 hand-rolled collapse), the named ladder the
// core host resolves by id, the library's records per layout tier and Recents' records per content tier. Pure: the mounted pages are `TierLayoutTests.Collection`.

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class LibraryTiersTests
{
    // ── the layout ladder ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_resolve_splits_from_600()
    {
        var ladder = ClassicLibraryTiers.Layout;
        Assert.Equal(0, ladder.Resolve(300f, -1));
        Assert.Equal(0, ladder.Resolve(599f, -1));
        Assert.Equal(1, ladder.Resolve(600f, -1));
        Assert.Equal(1, ladder.Resolve(3200f, -1));
    }

    [Fact]
    public void Widening_holds_the_one_column_arm_for_24_past_the_edge()
    {
        var ladder = ClassicLibraryTiers.Layout;
        Assert.Equal(0, ladder.Resolve(600f, 0));
        Assert.Equal(0, ladder.Resolve(623f, 0));
        Assert.Equal(1, ladder.Resolve(624f, 0));
    }

    [Fact]
    public void Narrowing_holds_the_split_arm_for_24_under_the_edge()
    {
        var ladder = ClassicLibraryTiers.Layout;
        Assert.Equal(1, ladder.Resolve(599f, 1));
        Assert.Equal(1, ladder.Resolve(576f, 1));
        Assert.Equal(0, ladder.Resolve(575f, 1));
    }

    [Fact]
    public void An_unmeasured_width_keeps_the_previous_arm()
    {
        var ladder = ClassicLibraryTiers.Layout;
        Assert.Equal(1, ladder.Resolve(float.NaN, 1));
        Assert.Equal(0, ladder.Resolve(float.NaN, 0));
    }

    [Fact]
    public void The_policy_names_the_ladder_the_core_host_resolves_by_id()
    {
        Assert.True(ClassicTiers.Policy.TryGetLadder(LibraryLadders.Layout, out var ladder));
        Assert.Same(ClassicLibraryTiers.Layout, ladder);
        Assert.Same(ClassicLibraryTiers.Layout, LibraryLadders.Of(ClassicTiers.Policy));
        Assert.Empty(ClassicTiers.Policy.Validate());
    }

    [Fact]
    public void A_structure_that_declares_no_layout_ladder_is_always_split()
    {
        var ladder = LibraryLadders.Of(null);
        foreach (float w in new[] { 0.5f, 100f, 300f, 600f, 5000f })
            Assert.False(LibraryLadders.IsSingleColumn(ladder.Resolve(w, -1)), w + " is split");
        Assert.True(LibraryLadders.IsSingleColumn(0));
        Assert.False(LibraryLadders.IsSingleColumn(1));
    }

    [Fact]
    public void The_split_edge_is_the_content_ladders_small_edge()
        => Assert.Equal(ClassicTiers.ContentSmallFrom, ClassicLibraryTiers.SplitFrom);

    // ── the library's records ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_one_column_record_is_the_compact_board()
    {
        foreach (var content in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            var record = ClassicLibraryTiers.For(content, single: true);
            Assert.Equal(WidthTier.Compact, record.Tier);   // the ladder, not the content tier, decides the fold
            Assert.True(record.Single);
            Assert.True(record.KindChips);
            Assert.Equal(40f, record.ControlH);
            Assert.Equal(40f, record.ViewCell);
            Assert.Equal(40f, record.ChipH);
            Assert.Equal(16f, record.HeadPad.Left);
            Assert.Equal(12f, record.HeadPad.Top);
            Assert.Equal(12f, record.HeadGap);
        }
    }

    [Fact]
    public void The_split_records_keep_the_stock_controls_and_the_pane_its_floor()
    {
        var small = ClassicLibraryTiers.For(WidthTier.Small, single: false);
        var standard = ClassicLibraryTiers.For(WidthTier.Standard, single: false);
        var huge = ClassicLibraryTiers.For(WidthTier.Huge, single: false);
        foreach (var record in new[] { small, standard, huge })
        {
            Assert.False(record.Single);
            Assert.False(record.KindChips);
            Assert.Equal(32f, record.ControlH);
            Assert.Equal(28f, record.ViewCell);
        }
        Assert.Equal(WidthTier.Small, small.Tier);
        Assert.Equal(WidthTier.Standard, standard.Tier);
        Assert.Equal(WidthTier.Huge, huge.Tier);
        Assert.True(small.RightMin < standard.RightMin && standard.RightMin < huge.RightMin, "the pane's floor grows with the tier");
        // Small and Standard are the tuned head (12 sides, 16 over it); Huge draws 24 over it (C-Library-Huge).
        Assert.Equal(new Edges4(12f, 16f, 12f, 8f), small.HeadPad);
        Assert.Equal(small.HeadPad, standard.HeadPad);
        Assert.Equal(24f, huge.HeadPad.Top);
    }

    [Fact]
    public void A_split_page_in_a_compact_content_tier_reads_smalls_record()
        => Assert.Equal(WidthTier.Small, ClassicLibraryTiers.For(WidthTier.Compact, single: false).Tier);

    [Fact]
    public void Huge_4k_reads_huges_records()
    {
        Assert.Same(ClassicLibraryTiers.For(WidthTier.Huge, false), ClassicLibraryTiers.For(WidthTier.Huge4K, false));
        Assert.Same(ClassicRecentsTiers.For(WidthTier.Huge), ClassicRecentsTiers.For(WidthTier.Huge4K));
    }

    [Fact]
    public void The_pane_inset_is_the_frames_16_at_every_tier()
    {
        Assert.Equal(16f, ClassicTiers.PaneInset);
        foreach (bool single in new[] { true, false })
            foreach (var content in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
                Assert.Equal(ClassicTiers.PaneInset, ClassicLibraryTiers.For(content, single).PaneInset);
    }

    [Fact]
    public void Without_resolved_tiers_a_library_page_is_the_tuned_standard_one()
        => Assert.Equal(WidthTier.Standard, ClassicLibraryTiers.Of(TierFacts.Unknown, single: false).Tier);

    [Fact]
    public void The_grips_range_is_the_persisted_widths_clamp()
    {
        // The persisted `library.<kind>.leftw` / `.midw` are clamped to the grips' range, whatever tier the page is in: a tier never rewrites them.
        Assert.Equal(ClassicLibraryRules.LeftMin, LibraryViewPrefs.LeftMin);
        Assert.Equal(ClassicLibraryRules.LeftMax, LibraryViewPrefs.LeftMax);
        Assert.Equal(ClassicLibraryRules.MidMin, LibraryViewPrefs.MidMin);
        Assert.Equal(ClassicLibraryRules.MidMax, LibraryViewPrefs.MidMax);
    }

    // ── Recents' records ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Recents_has_one_record_per_content_tier()
    {
        Assert.Equal(WidthTier.Compact, ClassicRecentsTiers.For(WidthTier.Compact).Tier);
        Assert.Equal(WidthTier.Small, ClassicRecentsTiers.For(WidthTier.Small).Tier);
        Assert.Equal(WidthTier.Standard, ClassicRecentsTiers.For(WidthTier.Standard).Tier);
        Assert.Equal(WidthTier.Huge, ClassicRecentsTiers.For(WidthTier.Huge).Tier);
        Assert.Equal(WidthTier.Standard, ClassicRecentsTiers.Of(TierFacts.Unknown).Tier);
    }

    [Fact]
    public void The_compact_recents_record_draws_the_boards_40_targets()
    {
        var compact = ClassicRecentsTiers.For(WidthTier.Compact);
        Assert.Equal(40f, compact.OverviewH);
        Assert.Equal(40f, compact.PivotH);
        Assert.Equal(40f, compact.ChevronLane);
        Assert.Equal(44f, compact.ChildRowH);
        Assert.True(compact.Stacked);
        Assert.False(compact.Rail);
        Assert.Equal(12f, compact.HeadTop);
        Assert.Equal(4f, compact.PivotGap);
    }

    [Fact]
    public void The_other_recents_records_are_the_tuned_row_with_the_day_rail()
    {
        foreach (var content in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            var record = ClassicRecentsTiers.For(content);
            Assert.False(record.Stacked);
            Assert.True(record.Rail);
            Assert.Equal(24f, record.ChevronLane);
            Assert.Equal(40f, record.ChildRowH);
            Assert.Equal(20f, record.PivotGap);
            Assert.Equal(0f, record.PivotH);
        }
        Assert.Equal(24f, ClassicRecentsTiers.For(WidthTier.Standard).HeadTop);
        Assert.Equal(16f, ClassicRecentsTiers.For(WidthTier.Small).HeadTop);
        Assert.Equal(32f, ClassicRecentsTiers.For(WidthTier.Huge).OverviewH);
    }

    [Fact]
    public void The_calendar_gap_is_its_own_rule_and_standards_is_the_old_page_wide_36()
    {
        Assert.Equal(36f, ClassicRecentsTiers.For(WidthTier.Standard).CalendarGap);
        Assert.Equal(16f, ClassicRecentsTiers.For(WidthTier.Compact).CalendarGap);
        Assert.Equal(24f, ClassicRecentsTiers.For(WidthTier.Small).CalendarGap);
        Assert.Equal(16f, ClassicRecentsTiers.For(WidthTier.Huge).CalendarGap);   // C-Recents-Huge's month cards sit 16 apart
    }

    // ── the navigator's resting width per tier (contract 1.21: C-Library-Huge 560, C-LibraryList-Huge 400) ──────────────────────────────

    [Theory]
    [InlineData("albums", WidthTier.Standard, 340f)]
    [InlineData("albums", WidthTier.Small, 240f)]       // C-Library-Small (contract 1.22)
    [InlineData("artists", WidthTier.Small, 240f)]      // C-LibraryList-Small
    [InlineData("podcasts", WidthTier.Small, 240f)]
    [InlineData("albums", WidthTier.Huge, 560f)]
    [InlineData("albums", WidthTier.Huge4K, 560f)]
    [InlineData("podcasts", WidthTier.Huge, 560f)]
    [InlineData("artists", WidthTier.Standard, 280f)]
    [InlineData("artists", WidthTier.Huge, 400f)]
    public void An_undragged_navigator_rests_at_its_tiers_width(string kind, WidthTier content, float expected)
    {
        float stored = LibraryViewPrefs.LeftWKey(kind).Default;
        Assert.Equal(expected, LibraryViewPrefs.RestingLeftW(kind, stored, customized: false, content, ClassicLibraryTiers.Policy));
    }

    [Theory]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    public void A_dragged_navigator_keeps_the_users_width_at_every_tier(WidthTier content)
        => Assert.Equal(420f, LibraryViewPrefs.RestingLeftW("albums", 420f, customized: true, content, ClassicLibraryTiers.Policy));

    [Fact]
    public void A_structure_without_a_library_policy_keeps_the_keys_default()
    {
        Assert.Equal(340f, LibraryViewPrefs.RestingLeftW("albums", 340f, customized: false, WidthTier.Huge, LibraryTierPolicy.Default));
        Assert.Same(ClassicLibraryTiers.Policy, Wavee.Themes.Classic.ClassicPolicies.Bundle.LibraryTiers);
    }

    [Fact]
    public void A_stored_width_is_the_users_only_when_it_is_not_the_default()
    {
        // Migration-free: a store that never held the key, or holds the default an older build saved on every visit, is "never dragged".
        var settings = new MemoryAppSettings();
        var key = LibraryViewPrefs.LeftWKey("albums");
        Assert.False(ThemeState.IsCustomized(key, settings, "classic"));
        ThemeState.Set(key, 340f, settings, "classic");
        Assert.False(ThemeState.IsCustomized(key, settings, "classic"));
        ThemeState.Set(key, 420f, settings, "classic");
        Assert.True(ThemeState.IsCustomized(key, settings, "classic"));
        Assert.False(ThemeState.IsCustomized(key, settings, "zune"));           // per theme
    }

    [Fact]
    public void The_huge_pane_header_is_the_boards_200_band()
    {
        var huge = ClassicLibraryTiers.For(WidthTier.Huge, single: false);
        Assert.Equal(160f, huge.PaneCover);                                      // C-Library-Huge: the 160 cover in a 200 band, 24 from the words
        Assert.Equal(200f, huge.PaneHeaderHeight);
        Assert.Equal(24f, huge.PaneHeaderGap);
        var standard = ClassicLibraryTiers.For(WidthTier.Standard, single: false);
        Assert.Equal(ClassicLibraryPaneRules.PaneCover, standard.PaneCover);   // the tuned 128 / 156 / 18
        Assert.Equal(ClassicLibraryPaneRules.PaneHeaderHeight, standard.PaneHeaderHeight);
        Assert.Equal(ClassicLibraryPaneRules.HeaderGap, standard.PaneHeaderGap);
        Assert.Equal(ClassicLibraryPaneRules.HeaderPadBottom, standard.PaneHeaderPadBottom);
    }

    // ── C-Library-Small: the sort button, the 120 pane band and the rows 10 apart ──────────────────────────────────────────────────────

    [Fact]
    public void The_small_pane_header_is_the_boards_96_cover_in_a_120_band()
    {
        var small = ClassicLibraryTiers.For(WidthTier.Small, single: false);
        Assert.Equal(96f, small.PaneCover);
        Assert.Equal(16f, small.PaneHeaderGap);
        Assert.Equal(10f, small.PaneHeaderPadBottom);
        // The stated height is the 120 band under the 16 pane inset and over the 10 of air; the cover (96) sits bottom-aligned inside the band.
        Assert.Equal(146f, small.PaneHeaderHeight);
        Assert.Equal(120f, small.PaneHeaderHeight - ClassicTiers.PaneInset - small.PaneHeaderPadBottom);
        Assert.True(small.PaneHeaderHeight - ClassicTiers.PaneInset - small.PaneHeaderPadBottom >= small.PaneCover, "the cover fits the band");
        // The tuned header is Standard's and the Compact drill-in's pane leaf (the Compact board does not redraw it): 128 / 156 / 18 / 12.
        foreach (var record in new[] { ClassicLibraryTiers.For(WidthTier.Standard, single: false), ClassicLibraryTiers.For(WidthTier.Compact, single: false) })
        {
            Assert.Equal(ClassicLibraryPaneRules.PaneCover, record.PaneCover);
            Assert.Equal(ClassicLibraryPaneRules.PaneHeaderHeight, record.PaneHeaderHeight);
            Assert.Equal(ClassicLibraryPaneRules.HeaderGap, record.PaneHeaderGap);
            Assert.Equal(ClassicLibraryPaneRules.HeaderPadBottom, record.PaneHeaderPadBottom);
        }
        // Every header's content box holds its cover (the stated height less the inset and the air under it), at every tier the pane draws.
        foreach (var content in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            var record = ClassicLibraryTiers.For(content, single: false);
            Assert.True(record.PaneHeaderHeight - ClassicTiers.PaneInset - record.PaneHeaderPadBottom >= record.PaneCover, $"{content}: the cover overruns its band");
        }
    }

    [Fact]
    public void The_sort_is_a_button_at_compact_and_small_only()
    {
        Assert.True(ClassicLibraryTiers.For(WidthTier.Compact, single: true).SortButton);
        Assert.True(ClassicLibraryTiers.For(WidthTier.Small, single: false).SortButton);
        Assert.True(ClassicLibraryTiers.For(WidthTier.Compact, single: false).SortButton);   // the split page in a Compact content tier reads Small's head
        Assert.False(ClassicLibraryTiers.For(WidthTier.Standard, single: false).SortButton);
        Assert.False(ClassicLibraryTiers.For(WidthTier.Huge, single: false).SortButton);
        // The Small button is the stock 32 beside the 28 view cells, the head's rows 10 apart (C-Library-Small), the Standard head's 8 untouched.
        var small = ClassicLibraryTiers.For(WidthTier.Small, single: false);
        Assert.Equal(32f, small.ControlH);
        Assert.Equal(28f, small.ViewCell);
        Assert.Equal(10f, small.HeadGap);
        Assert.Equal(8f, ClassicLibraryTiers.For(WidthTier.Standard, single: false).HeadGap);
    }

    // ── Small's navigator and its grid floor (contract 1.22: C-Library-Small, C-LibraryList-Small) ──────────────────────────────────────

    [Fact]
    public void The_small_grid_starts_at_96_and_the_other_tiers_keep_the_cores_ladder()
    {
        Assert.Equal(96f, ClassicLibraryTiers.For(WidthTier.Small, single: false).CardCellMin);
        foreach (var content in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(LibraryNavMount.CardCellMinFull, ClassicLibraryTiers.For(content, single: false).CardCellMin);
        Assert.Equal(LibraryNavMount.CardCellMinFull, ClassicLibraryTiers.For(WidthTier.Compact, single: true).CardCellMin);   // the one-column page
        // The policy the host reads says the same, and nothing outside Small (a Compact page is one column: the core's 116 fits two there).
        Assert.Equal(96f, ClassicLibraryTiers.Policy.CardCellMinFor(WidthTier.Small));
        Assert.True(float.IsNaN(ClassicLibraryTiers.Policy.CardCellMinFor(WidthTier.Compact)));
        Assert.True(float.IsNaN(ClassicLibraryTiers.Policy.CardCellMinFor(WidthTier.Standard)));
        Assert.True(float.IsNaN(ClassicLibraryTiers.Policy.CardCellMinFor(WidthTier.Huge)));
        Assert.True(float.IsNaN(LibraryTierPolicy.Default.CardCellMinFor(WidthTier.Small)));
    }

    [Fact]
    public void The_240_navigator_holds_two_96_cells_where_the_cores_floor_fits_one()
    {
        float inner = ClassicLibraryTiers.SmallNavWidth - 2f * 8f;   // the grid's (8, 8, 8, 0) wrapper
        float gap = LibraryNavMount.GridGap;
        static int Cols(float inner, float min, float gap) => System.Math.Max(1, (int)((inner + gap) / (min + gap)));
        Assert.Equal(2, Cols(inner, LibraryNavMount.CellMinFor(3, 0, ClassicLibraryTiers.SmallCardCellMin), gap));
        Assert.Equal(1, Cols(inner, LibraryNavMount.CellMinFor(3, 0), gap));
    }

    [Fact]
    public void The_grid_floor_moves_the_cell_never_the_mount_key()
    {
        var core = LibraryNavMount.For("albums", 3, 1, alphabetical: false, lettersKey: 0);
        var small = LibraryNavMount.For("albums", 3, 1, alphabetical: false, lettersKey: 0, fullBase: ClassicLibraryTiers.SmallCardCellMin);
        Assert.Equal(core.Key, small.Key);                                               // a crossing never remounts the navigator
        Assert.Equal(96f + LibraryNavMount.CardCellStepFull, small.CellMin);            // the S/M/L steps stay the core's
        Assert.Equal(LibraryNavMount.CardCellMinFull + LibraryNavMount.CardCellStepFull, core.CellMin);
        // The compact grid keeps its own ladder; a non-finite base reads as the core's.
        Assert.Equal(LibraryNavMount.CellMinFor(2, 0), LibraryNavMount.CellMinFor(2, 0, ClassicLibraryTiers.SmallCardCellMin));
        Assert.Equal(LibraryNavMount.CardCellMinFull, LibraryNavMount.CellMinFor(3, 0, float.NaN));
    }

    // ── the Huge navigator and album pane (C-Library-Huge) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_huge_tints_the_navigator_and_docks_the_also_by_aside()
    {
        var huge = ClassicLibraryTiers.For(WidthTier.Huge, single: false);
        Assert.True(huge.NavTinted);
        Assert.True(huge.AlsoByDocked);
        Assert.Equal(300f, huge.AlsoByAside);
        Assert.Equal(32f, huge.AlsoByAsideGap);
        Assert.Same(huge, ClassicLibraryTiers.For(WidthTier.Huge4K, single: false));
        foreach (var (content, single) in new[] { (WidthTier.Compact, true), (WidthTier.Small, false), (WidthTier.Standard, false) })
        {
            var record = ClassicLibraryTiers.For(content, single);
            Assert.False(record.NavTinted);
            Assert.False(record.AlsoByDocked);
            Assert.Equal(0f, record.AlsoByAside);
        }
    }

    [Fact]
    public void The_also_by_aside_is_the_boards_300_column_of_140_covers_32_right_of_the_rows()
    {
        // The covers' column: two 140 covers 20 apart = 300; the tiles' plates bleed 8 each side of a cover.
        float covers = 2f * ClassicLibraryPaneRules.AlsoByAsideArt + (ClassicLibraryPaneRules.AlsoByAsideColGap + 2f * SurfaceGeometry.ShelfPlatePad);
        Assert.Equal(ClassicLibraryTiers.HugeAlsoByAside, covers);
        Assert.Equal(156f, ClassicLibraryPaneRules.AlsoByAsideCardW);
        // The column's width: the gap, the covers and the pane's right inset; its padding puts the covers exactly 32 right of the rows.
        float outer = ClassicLibraryPaneRules.AlsoByAsideOuter(ClassicLibraryTiers.HugeAlsoByAside, ClassicLibraryTiers.HugeAlsoByGap);
        Assert.Equal(32f + 300f + ClassicTiers.PaneInset, outer);
        Assert.Equal(outer, ClassicLibraryPaneRules.AlsoByAsideLead(32f) + 2f * ClassicLibraryPaneRules.AlsoByAsideCardW + ClassicLibraryPaneRules.AlsoByAsideColGap
                            + ClassicLibraryPaneRules.AlsoByAsideTrail);
        Assert.Equal(32f, ClassicLibraryPaneRules.AlsoByAsideLead(32f) + SurfaceGeometry.ShelfPlatePad);
        Assert.Equal(0f, ClassicLibraryPaneRules.AlsoByAsideOuter(0f, 0f));               // below Huge the slot is 0 wide
    }

    // ── Recents' docked Overview (C-Recents-Huge) ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_huge_recents_docks_the_overview_beside_an_1100_list()
    {
        var huge = ClassicRecentsTiers.For(WidthTier.Huge);
        Assert.True(huge.OverviewDocked);
        Assert.Equal(1100f, huge.ListMax);
        Assert.Equal(48f, huge.OverviewGap);
        Assert.Equal(660f, huge.OverviewAside);                                          // two plated 322 month cards 16 apart
        Assert.Equal(322f, ClassicRecentsRules.OverviewCardW);
        Assert.Equal(2f * ClassicRecentsRules.OverviewCardW + huge.CalendarGap, huge.OverviewAside);
        Assert.Same(huge, ClassicRecentsTiers.For(WidthTier.Huge4K));
        foreach (var content in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard })
        {
            var record = ClassicRecentsTiers.For(content);
            Assert.False(record.OverviewDocked);
            Assert.Equal(0f, record.OverviewAside);
            Assert.True(float.IsNaN(record.ListMax));
        }
    }

    [Fact]
    public void The_overview_aside_fits_two_month_cards()
    {
        var huge = ClassicRecentsTiers.For(WidthTier.Huge);
        float gap = huge.CalendarGap;
        int cols = System.Math.Max(1, (int)((huge.OverviewAside + gap) / (ClassicRecentsRules.OverviewCardW + gap)));
        Assert.Equal(2, cols);
    }
}
