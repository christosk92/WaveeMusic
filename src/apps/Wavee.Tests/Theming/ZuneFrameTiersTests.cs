// ── Wavee.Tests/Theming/ZuneFrameTiersTests.cs — Zune's frame tiers and the core's Huge 4K paths, pure (themes plan 3.2, 6.3; P5 ZT1) ──
//
// The rules ZT1 adds and the core paths Classic never exercised at Huge 4K (Classic declares no 3200 edge), against the boards
// (Z-Frame-*, Z-Part-TitleBar, Z-Part-Band, Z-Shell-Short, Z-Sizes-Breakpoints):
//
//   · the frame record's band heights ARE the band record's (one source: 92 / 84 / 84 / 84 / 112, collapsed 52 / 72), and its clip region is
//     the docked panel from Standard up;
//   · the title bar per tier (icons and no trail at Compact, the pill 220 / 320 / 400, 56 tall at Huge 4K); the band's type at Huge 4K (40 / 48);
//   · the band collapses where its record does and wherever the window is Short (never at a Regular Huge or Huge 4K);
//   · Zune's chrome budgets make the core allocator answer the search form Zune draws, tier by tier, at every edge;
//   · the core at Huge 4K: the tiers resolve, the nav pane band, the budgets, the panel, overlay, detail and stage records all map it, and
//     Classic's chrome policy is unchanged.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneFrameTiersTests
{
    static readonly WidthTier[] AllTiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    static TierFacts Facts(float width, float height) => WindowTiers.Resolve(ZuneTiers.Policy, width, height, width);

    // ── the frame record ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_frames_band_heights_are_the_band_records_at_every_tier()
    {
        foreach (var tier in AllTiers)
        {
            var frame = ZuneTiers.Frame(tier);
            var band = ZuneBandRules.Band(tier);
            Assert.Equal(band.ExpandedH, frame.BandH);
            Assert.Equal(band.CollapsedH, frame.BandCollapsedH);
        }
        Assert.Equal(112f, ZuneTiers.Frame(WidthTier.Huge4K).BandH);
        Assert.Equal(72f, ZuneTiers.Frame(WidthTier.Huge4K).BandCollapsedH);
        Assert.Equal(92f, ZuneTiers.Frame(WidthTier.Compact).BandH);
    }

    [Fact]
    public void The_short_floor_is_the_window_minimum_less_the_small_frame()
    {
        // Z-Shell-Short (800 x 330): the 48 title bar, the 84 band and the 72 bar leave 126.
        var small = ZuneTiers.Frame(WidthTier.Small);
        Assert.Equal(TierPolicy.WindowMinHeight - small.TitleBarH - small.BandH - ZuneTiers.BarReserveH, ZuneTiers.Policy.ShortFloor);
    }

    [Fact]
    public void The_docked_panel_is_the_frames_clip_region_from_standard_up()
    {
        Assert.Empty(ZuneTiers.Frame(WidthTier.Compact).Regions);
        Assert.Empty(ZuneTiers.Frame(WidthTier.Small).Regions);
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var frame = ZuneTiers.Frame(tier);
            Assert.Empty(TierClip.Validate(frame));
            var region = Assert.Single(frame.Regions);
            Assert.Equal(ZuneTiers.PanelRegionId, region.Id);
            Assert.Equal(ZuneFrameRules.PanelFloatW, region.MinWidth);
            // A resized frame narrower than the panel clips it; one that seats it clips nothing.
            Assert.Contains(ZuneTiers.PanelRegionId, TierClip.ClippedAt(frame, 300f, 330f));
            Assert.Empty(TierClip.ClippedAt(frame, 984f, 330f));
        }
    }

    // ── the title bar ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WidthTier.Compact, false, false, 0f, 48f)]
    [InlineData(WidthTier.Small, false, true, 0f, 48f)]
    [InlineData(WidthTier.Standard, true, true, 220f, 48f)]
    [InlineData(WidthTier.Huge, true, true, 320f, 48f)]
    [InlineData(WidthTier.Huge4K, true, true, 400f, 56f)]
    public void The_title_bar_per_tier_is_the_boards(WidthTier tier, bool pill, bool crumbs, float pillW, float height)
    {
        var rec = ZuneFrameRules.TitleBar(tier);
        Assert.Equal(pill, rec.Pill);
        Assert.Equal(crumbs, rec.Crumbs);
        Assert.Equal(pillW, rec.PillW);
        Assert.Equal(height, ZuneTiers.Frame(tier).TitleBarH);
        Assert.True(ZuneFrameRules.Button >= 40f, "every title-bar button is a 40 target");
    }

    [Fact]
    public void The_title_bars_height_eases_on_the_height_axis()
    {
        var m = ZuneFrameTiers.TitleBarHeightMotion;
        Assert.Equal(SizeMode.Reflow, m.Size);
        Assert.Equal(SizeAxes.Height, m.Axes);
    }

    // ── the band ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_band_sets_its_pivots_at_40_48_and_row_two_at_15_only_at_huge_4k()
    {
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            Assert.True(ZuneBandTiers.Type(tier).PivotIsRole, $"{tier}: the role's pivot");
            Assert.True(ZuneBandTiers.Type(tier).SubIsRole, $"{tier}: the role's row 2");
        }
        var big = ZuneBandTiers.Type(WidthTier.Huge4K);
        Assert.Equal(40f, big.PivotSize);
        Assert.Equal(48f, big.PivotLine);
        Assert.Equal(15f, big.SubSize);
        Assert.Equal(20f, big.SubLine);
        // The 48 line sits inside the 64 row.
        Assert.True(big.PivotLine <= ZuneBandRules.Band(WidthTier.Huge4K).Row1H);
        Assert.Same(ZuneBandTiers.Type(WidthTier.Standard), ZuneBandTiers.TypeOf(TierFacts.Unknown));
    }

    [Theory]
    [InlineData(400f, 860f, true)]     // Compact: row 2 is scroll-tied
    [InlineData(800f, 960f, false)]    // Small: pinned (owner decision 2026-10-20)
    [InlineData(1440f, 960f, false)]   // Standard: pinned
    [InlineData(2560f, 1440f, false)]  // Huge: there is room
    [InlineData(3840f, 2160f, false)]  // Huge 4K
    [InlineData(800f, 330f, true)]     // Z-Shell-Short
    [InlineData(2560f, 660f, true)]    // a Short Huge window: no vertical room either
    [InlineData(3840f, 690f, true)]    // a Short Huge 4K window
    public void The_band_collapses_where_its_record_does_and_wherever_the_window_is_short(float width, float height, bool collapses)
    {
        var facts = Facts(width, height);
        Assert.Equal(collapses, ZuneBandTiers.Collapses(ZuneBandRules.BandOf(facts), facts));
    }

    [Fact]
    public void A_band_mounted_alone_collapses_by_its_record()
    {
        Assert.False(ZuneBandTiers.Collapses(ZuneBandRules.Band(WidthTier.Huge), TierFacts.Unknown));
        Assert.False(ZuneBandTiers.Collapses(ZuneBandRules.Band(WidthTier.Standard), TierFacts.Unknown));
        Assert.True(ZuneBandTiers.Collapses(ZuneBandRules.Band(WidthTier.Compact), TierFacts.Unknown));
    }

    [Fact]
    public void Compact_band_rows_are_touch_targets()
    {
        var compact = ZuneBandRules.Band(WidthTier.Compact);
        Assert.True(compact.Row1H >= 40f);
        Assert.True(compact.Row2H >= 40f);
        // A short view word ("all") is still a 40 wide hit box at Compact (round G); no other tier widens it.
        Assert.Equal(40f, compact.ViewMinW);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(0f, ZuneBandRules.Band(tier).ViewMinW);
    }

    [Fact]
    public void The_bare_account_chip_has_a_40_floor_at_compact_alone()
    {
        Assert.Equal(40f, ZuneOverlayTiers.Compact.ChipMinWidth);
        Assert.Equal(ZuneOverlayRules.ChipTouchHeight, ZuneOverlayTiers.Compact.ChipHeight);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(0f, ZuneOverlayTiers.For(tier).ChipMinWidth);
    }

    // ── the chrome budgets ───────────────────────────────────────────────────────────────────────────────────────────────

    static ChromePolicy ZuneChrome => ZunePolicies.For(ZuneParameters.Default).Chrome;

    static Shell.MergedSearchMode FormAt(WidthTier tier, float width)
        => Shell.Chrome.Resolve(width, 0f, null, Shell.FrameRules.ChipForm.Profile,
            ShellCaps.Budget(ZuneChrome.BudgetFor(tier), ZunePolicies.Capabilities), tabs: false).SearchMode;

    [Fact]
    public void Zunes_chrome_policy_is_the_frames()
    {
        Assert.Same(ZuneFrameTiers.Chrome, ZuneChrome);
        Assert.Empty(ZuneChrome.Validate());
    }

    [Theory]
    [InlineData(WidthTier.Compact, 300f)]
    [InlineData(WidthTier.Compact, 615f)]
    [InlineData(WidthTier.Compact, 639f)]
    [InlineData(WidthTier.Small, 616f)]
    [InlineData(WidthTier.Small, 640f)]
    [InlineData(WidthTier.Small, 983f)]
    [InlineData(WidthTier.Small, 1007f)]
    public void The_allocator_answers_the_magnifier_where_zune_draws_it(WidthTier tier, float width)
        => Assert.Equal(Shell.MergedSearchMode.Icon, FormAt(tier, width));

    [Theory]
    [InlineData(WidthTier.Standard, 984f)]
    [InlineData(WidthTier.Standard, 1008f)]
    [InlineData(WidthTier.Standard, 1919f)]
    [InlineData(WidthTier.Huge, 1896f)]
    [InlineData(WidthTier.Huge, 1920f)]
    [InlineData(WidthTier.Huge, 3199f)]
    [InlineData(WidthTier.Huge4K, 3176f)]
    [InlineData(WidthTier.Huge4K, 3840f)]
    public void The_allocator_answers_the_pill_where_zune_draws_it(WidthTier tier, float width)
    {
        Assert.Equal(Shell.MergedSearchMode.Field, FormAt(tier, width));
        // Every chip form, too (the "Reconnect" button is the widest).
        foreach (var chip in new[] { Shell.FrameRules.ChipForm.Connecting, Shell.FrameRules.ChipForm.Reconnect, Shell.FrameRules.ChipForm.SignIn })
            Assert.Equal(Shell.MergedSearchMode.Field, Shell.Chrome.Resolve(width, 0f, null, chip,
                ShellCaps.Budget(ZuneChrome.BudgetFor(tier), ZunePolicies.Capabilities), tabs: false).SearchMode);
    }

    [Fact]
    public void The_allocators_form_flips_with_zunes_tier_not_with_the_width()
    {
        // The same width on either side of a held edge answers the committed tier's form: the flip is the crossing's.
        Assert.Equal(Shell.MergedSearchMode.Field, FormAt(WidthTier.Standard, 990f));
        Assert.Equal(Shell.MergedSearchMode.Icon, FormAt(WidthTier.Small, 990f));
        Assert.True(Shell.FrameRules.ReissueSearchFocus(Shell.MergedSearchMode.Icon, FormAt(WidthTier.Standard, 1008f), fieldFocused: false, flyoutOpen: true),
            "a search open in the magnifier's flyout is re-focused into the pill on the 1008 crossing");
    }

    // ── the core at Huge 4K (the audit) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classics_chrome_policy_is_unchanged_by_the_small_budget()
    {
        var classic = ChromePolicy.Default;
        Assert.Null(classic.Small);
        Assert.Same(classic.Budget, classic.BudgetFor(WidthTier.Small));
        Assert.Same(classic.Budget, classic.BudgetFor(WidthTier.Standard));
        Assert.Same(classic.Huge, classic.BudgetFor(WidthTier.Huge4K));
        Assert.Same(classic.Compact, classic.BudgetFor(WidthTier.Compact));
    }

    [Fact]
    public void Zunes_tiers_resolve_huge_4k_with_both_rungs()
    {
        var facts = Facts(3840f, 2160f);
        Assert.Equal(WidthTier.Huge4K, facts.Window);
        Assert.Equal(WidthTier.Huge4K, facts.Content);
        Assert.Equal(HeightTier.Regular, facts.Height);
        Assert.True(facts.Rung(ZuneTiers.PaneSecondRung));
        Assert.True(facts.Rung(ZuneTiers.PaneThirdRung));
        // The content ladder follows the docked panels: 3840 less a 1080 three-pane panel is a Huge content.
        Assert.Equal(WidthTier.Huge, WindowTiers.Resolve(ZuneTiers.Policy, 3840f, 2160f, 3840f - 1080f).Content);
    }

    [Fact]
    public void The_core_maps_huge_4k_like_huge_everywhere_a_record_is_keyed_by_band()
    {
        // The nav pane: Wide (the user's collapse decides), the Huge resting width.
        Assert.Equal(SidebarWindowBand.Wide, SidebarPaneModeRules.BandOf(WidthTier.Huge4K));
        var nav = NavPanePolicy.Default with { HugeWidth = 320f };
        Assert.Equal(320f, nav.RestingWidthFor(WidthTier.Huge4K, 260f, userSet: false));
        Assert.Equal(260f, nav.RestingWidthFor(WidthTier.Huge4K, 260f, userSet: true));
        // Zune's pane forces nothing it can show (NavTree is off) and is never resizable.
        Assert.False(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Wide, ZunePolicies.NavPane));

        var zune = ZunePolicies.For(ZuneParameters.Default);
        Assert.Same(zune.Chrome.BudgetFor(WidthTier.Huge), zune.Chrome.BudgetFor(WidthTier.Huge4K));
        Assert.Same(zune.Overlays.For(WidthTier.Huge), zune.Overlays.For(WidthTier.Huge4K));
        Assert.Same(zune.DetailTiers.For(WidthTier.Huge), zune.DetailTiers.For(WidthTier.Huge4K));
        Assert.Same(zune.Stage.MetricsFor(WidthTier.Huge), zune.Stage.MetricsFor(WidthTier.Huge4K));
        _ = zune.Panel.For(WidthTier.Huge4K);   // never out of range, whatever the structure declares
        Assert.Empty(zune.Chrome.Validate());
    }

    // ── the page column and the margins (the T1 audit: A1, A2, A7, A9) ─────────────────────────────────────────────────────

    [Fact]
    public void The_page_column_caps_at_2400_between_the_gutters_and_centres_in_a_row()
    {
        Assert.Equal(2400f, ZuneFrameRules.PageCap);
        var page = new BoxEl { Key = "page", Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Padding = new Edges4(5f, 10f, 7f, 20f) };
        // T2's member: the cap, the gutters, centred on the cross axis (it collapses alone; pages stand in PageRow).
        var bare = ZuneFrameRules.PageColumn(page, 48f);
        Assert.Equal(2400f + 2f * 48f, bare.MaxWidth);
        Assert.Equal(FlexAlign.Center, bare.AlignSelf);
        Assert.Equal(new Edges4(48f, 10f, 48f, 20f), bare.Padding);
        var wrapper = ZuneFrameRules.PageRow(page, 48f);
        // The row takes the page's place in its parent and centres it; the page keeps its key, fills the row up to its cap.
        Assert.Null(wrapper.Key);
        Assert.Equal(0, wrapper.Direction);
        Assert.Equal(FlexJustify.Center, wrapper.Justify);
        Assert.Equal(1f, wrapper.Grow);
        Assert.Equal(0f, wrapper.MinHeight);
        var column = Assert.IsType<BoxEl>(Assert.Single(wrapper.Children));
        Assert.Equal("page", column.Key);
        Assert.Equal(2400f + 2f * 48f, column.MaxWidth);
        Assert.Equal(new Edges4(48f, 10f, 48f, 20f), column.Padding);   // the gutter either side, the page's own top and bottom kept
        Assert.Equal(1f, column.Grow);
        Assert.Equal(0f, column.Basis);
        Assert.Equal(0f, column.MinWidth);
        Assert.Equal(1, column.Direction);
        Assert.Equal(FlexAlign.Stretch, column.AlignSelf);   // stretched in the row, never its content's width

        // The lead every title, the band and the page share: the gutter, then centred past the cap.
        Assert.Equal(48f, ZuneFrameRules.ColumnLead(1440f, 48f));
        Assert.Equal(80f, ZuneFrameRules.ColumnLead(2560f, 48f));
        Assert.Equal(720f, ZuneFrameRules.ColumnLead(3840f, 64f));
        Assert.Equal(48f, ZuneFrameRules.ColumnLead(2496f, 48f));   // the cap's own edge: 2400 + 2 x 48
    }

    [Fact]
    public void The_bands_trailing_inset_and_the_bars_right_inset_end_on_the_page_edge()
    {
        foreach (var tier in AllTiers)
        {
            float gutter = ZuneTiers.Frame(tier).Gutter;
            var band = ZuneBandRules.Band(tier);
            Assert.Equal(gutter, band.Inset);
            // Row 2's actions and the pins end on the page's right content edge; Compact's rows run to the window's edge under their fade.
            Assert.Equal(tier == WidthTier.Compact ? 0f : gutter, band.Row2End);
        }

        // The bar: the cover on the gutter, the last tool's GLYPH on the gutter (the gutter less the target's air): 12 / 36 / 36 / 52.
        (ZuneBarLevel Level, WidthTier Home, float Right)[] levels =
        [
            (ZuneBarLevel.Narrow, WidthTier.Small, 12f), (ZuneBarLevel.Medium, WidthTier.Small, 12f), (ZuneBarLevel.Full, WidthTier.Standard, 36f),
            (ZuneBarLevel.Wide, WidthTier.Huge, 36f), (ZuneBarLevel.Big, WidthTier.Huge4K, 51.5f),
        ];
        foreach (var (level, home, right) in levels)
        {
            var look = ZunePlayerTiers.Look(level);
            var insets = ZunePlayerTiers.RowInsets(home, look);
            float gutter = ZuneTiers.Frame(home).Gutter;
            Assert.Equal(gutter, insets.Left);
            Assert.Equal(right, insets.Right);
            Assert.Equal(look.PadLeft, insets.Left);
            Assert.Equal(look.PadRight, insets.Right);
            Assert.Equal(gutter, insets.Right + (look.Button - look.Glyph) / 2f);
        }
        // The medium level at Standard draws the window's gutter, not its home tier's.
        Assert.Equal(48f, ZunePlayerTiers.RowInsets(WidthTier.Standard, ZunePlayerTiers.Look(ZuneBarLevel.Medium)).Left);
    }

    [Fact]
    public void The_bar_stacks_the_times_under_the_disc_at_every_full_level()
    {
        foreach (var level in new[] { ZuneBarLevel.Narrow, ZuneBarLevel.Medium })
        {
            var look = ZunePlayerTiers.Look(level);
            Assert.Equal(36f, look.Disc);
            Assert.Equal(4f, look.TimesGap);
            // 8 of air above and below the stacked cluster in the 72.
            Assert.Equal(8f, (ZuneTiers.BarReserveH - (look.Disc + look.TimesGap + ZuneTransportLayout.TimeLine)) / 2f);
        }
        // Z-PlayBar / Z-PlayBar-Huge: "1:14 · -3:04" under the disc at the full, wide and big levels too (the cluster centred in the bar).
        foreach (var level in new[] { ZuneBarLevel.Full, ZuneBarLevel.Wide })
        {
            var look = ZunePlayerTiers.Look(level);
            Assert.Equal(4f, look.TimesGap);
            Assert.True(look.Disc + look.TimesGap + ZuneTransportLayout.TimeLine <= ZuneTiers.BarReserveH);
        }
        Assert.Equal(40f, ZuneTransportLayout.TimeSlotW);
    }

    [Fact]
    public void The_band_fades_its_rows_over_16_percent_of_its_width_at_compact_and_small()
    {
        var compact = ZuneBandRules.Band(WidthTier.Compact);
        var small = ZuneBandRules.Band(WidthTier.Small);
        Assert.Equal(48f, ZuneBandRules.EdgeFade(compact, 300f));    // the floor (16 % of 300 is 48)
        Assert.Equal(64f, ZuneBandRules.EdgeFade(compact, 400f));    // the board's 84 % mask at 400
        Assert.Equal(48f, ZuneBandRules.EdgeFade(compact, 200f));    // never under 48
        Assert.Equal(128f, ZuneBandRules.EdgeFade(small, 800f));
        Assert.Equal(0f, ZuneBandRules.EdgeFade(small, 802f) % ZuneBandRules.EdgeFadeStep);   // stepped, so a resize re-renders per step
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(ZuneBandRules.EdgeFadeBand, ZuneBandRules.EdgeFade(ZuneBandRules.Band(tier), 2560f));
    }

    [Fact]
    public void The_wordmark_is_drawn_whole_from_small_and_compact_shows_the_query_field_on_search()
    {
        foreach (var tier in AllTiers)
            Assert.Equal(tier != WidthTier.Compact, ZuneFrameRules.TitleBar(tier).Wordmark);

        // Compact (Z-Search-Compact): the field takes the title bar while there is a query or the focus, never over an open flyout.
        var compact = ZuneFrameRules.TitleBar(WidthTier.Compact);
        Assert.Equal(40f, compact.PillH);
        Assert.Equal(20f, compact.PillRadius);
        // The Search route is the core's exact flag (contract 1.30, ShellTitleBarProps.OnSearchRoute), not a query proxy.
        Assert.True(ZuneFrameRules.FieldInLead(compact, onSearchRoute: true, flyoutOpen: false, focused: false));
        Assert.True(ZuneFrameRules.FieldInLead(compact, onSearchRoute: false, flyoutOpen: false, focused: true));
        Assert.False(ZuneFrameRules.FieldInLead(compact, onSearchRoute: false, flyoutOpen: false, focused: false));
        Assert.False(ZuneFrameRules.FieldInLead(compact, onSearchRoute: true, flyoutOpen: true, focused: true));
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.False(ZuneFrameRules.FieldInLead(ZuneFrameRules.TitleBar(tier), onSearchRoute: true, flyoutOpen: false, focused: true));

        // The account's name: 160 on the pill tiers (and the field budget charges it), the core's cap elsewhere.
        Assert.Equal(76f, ZuneFrameRules.AccountNameCap(ZuneFrameRules.TitleBar(WidthTier.Small), 76f));
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(160f, ZuneFrameRules.AccountNameCap(ZuneFrameRules.TitleBar(tier), 76f));
        Assert.Equal(160f + ZuneFrameRules.AccountNameChrome, ZuneFrameTiers.FieldBudget.ProfileNameW);
    }
}
