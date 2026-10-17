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
    [InlineData(400f, 860f, true)]     // Compact
    [InlineData(800f, 960f, true)]     // Small: collapse at the first scroll
    [InlineData(1440f, 960f, true)]    // Standard
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
        Assert.True(ZuneBandTiers.Collapses(ZuneBandRules.Band(WidthTier.Standard), TierFacts.Unknown));
    }

    [Fact]
    public void Compact_band_rows_are_touch_targets()
    {
        var compact = ZuneBandRules.Band(WidthTier.Compact);
        Assert.True(compact.Row1H >= 40f);
        Assert.True(compact.Row2H >= 40f);
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
}
