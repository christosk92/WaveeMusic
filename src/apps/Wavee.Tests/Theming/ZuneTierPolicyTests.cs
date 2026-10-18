// ── Wavee.Tests/Theming/ZuneTierPolicyTests.cs — Zune's ladders, rungs, frame records, policies and motion (P5 Z0) ────────────────
//
// The pure rules against the board `Z-Sizes-Breakpoints`: window and content edges 640 / 1008 / 1920 / 3200 committing at the line on
// widening and held 24 on narrowing, Short below 700 held 24, ShortFloor 126, the rungs `pane.second` (1920) and `pane.third` (3200) on
// the window axis; the frame's record per window tier (gutter, title bar, band, play bar); no nav pane at any tier; the motion timings.

using FluentGpu.Foundation;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneTierPolicyTests
{
    static TierPolicy Policy => ZuneTiers.Policy;

    static WidthTier Fresh(float width) => (WidthTier)Policy.Window.Resolve(width, -1);

    static WidthTier From(WidthTier previous, float width) => (WidthTier)Policy.Window.Resolve(width, (int)previous);

    // ── the ladders ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_policy_validates_with_zunes_floors()
    {
        Assert.Empty(Policy.Validate());
        Assert.Equal(TierPolicy.WindowFloor, Policy.CompactFloor);
        Assert.Equal(126f, Policy.ShortFloor);
        // 330 less the 48 title bar, the 84 band and the 72 play bar.
        Assert.Equal(TierPolicy.WindowMinHeight - 48f - 84f - 72f, Policy.ShortFloor);
    }

    [Fact]
    public void The_window_and_content_ladders_are_the_boards_edges()
    {
        var expected = new[]
        {
            new TierEdge(640f, NarrowHold: 24f, WidenHold: 0f),
            new TierEdge(1008f, NarrowHold: 24f, WidenHold: 0f),
            new TierEdge(1920f, NarrowHold: 24f, WidenHold: 0f),
            new TierEdge(3200f, NarrowHold: 24f, WidenHold: 0f),
        };
        foreach (var ladder in new[] { Policy.Window, Policy.Content })
        {
            Assert.Equal(expected.Length, ladder.EdgeCount);
            for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], ladder.EdgeAt(i));
        }
        Assert.Equal(1, Policy.Height.EdgeCount);
        Assert.Equal(new TierEdge(700f, NarrowHold: 24f, WidenHold: 0f), Policy.Height.EdgeAt(0));
    }

    [Theory]
    [InlineData(300f, WidthTier.Compact)]
    [InlineData(639f, WidthTier.Compact)]
    [InlineData(640f, WidthTier.Small)]
    [InlineData(1007f, WidthTier.Small)]
    [InlineData(1008f, WidthTier.Standard)]
    [InlineData(1440f, WidthTier.Standard)]
    [InlineData(1919f, WidthTier.Standard)]
    [InlineData(1920f, WidthTier.Huge)]
    [InlineData(2560f, WidthTier.Huge)]
    [InlineData(3199f, WidthTier.Huge)]
    [InlineData(3200f, WidthTier.Huge4K)]
    [InlineData(3840f, WidthTier.Huge4K)]
    public void A_fresh_resolve_lands_on_the_boards_band(float width, WidthTier expected)
        => Assert.Equal(expected, Fresh(width));

    [Theory]
    // Narrowing holds 24 below each edge: the crossing commits below 616 / 984 / 1896 / 3176.
    [InlineData(WidthTier.Small, 616f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 615f, WidthTier.Compact)]
    [InlineData(WidthTier.Standard, 984f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 983f, WidthTier.Small)]
    [InlineData(WidthTier.Huge, 1896f, WidthTier.Huge)]
    [InlineData(WidthTier.Huge, 1895f, WidthTier.Standard)]
    [InlineData(WidthTier.Huge4K, 3176f, WidthTier.Huge4K)]
    [InlineData(WidthTier.Huge4K, 3175f, WidthTier.Huge)]
    // Widening commits at the line.
    [InlineData(WidthTier.Compact, 639f, WidthTier.Compact)]
    [InlineData(WidthTier.Compact, 640f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 1008f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 1920f, WidthTier.Huge)]
    [InlineData(WidthTier.Huge, 3200f, WidthTier.Huge4K)]
    public void A_crossing_holds_24_on_narrowing_and_none_on_widening(WidthTier previous, float width, WidthTier expected)
        => Assert.Equal(expected, From(previous, width));

    [Theory]
    [InlineData(330f, -1, HeightTier.Short)]
    [InlineData(699f, -1, HeightTier.Short)]
    [InlineData(700f, -1, HeightTier.Regular)]
    [InlineData(676f, (int)HeightTier.Regular, HeightTier.Regular)]
    [InlineData(675f, (int)HeightTier.Regular, HeightTier.Short)]
    [InlineData(700f, (int)HeightTier.Short, HeightTier.Regular)]
    public void The_height_is_short_below_700_held_24(float height, int previous, HeightTier expected)
        => Assert.Equal(expected, (HeightTier)Policy.Height.Resolve(height, previous));

    [Fact]
    public void The_content_ladder_reads_the_content_width()
    {
        // A Standard window whose docked panels leave the page 900 wide: the content is Small.
        var facts = WindowTiers.Resolve(Policy, 1440f, 960f, 900f);
        Assert.Equal(WidthTier.Standard, facts.Window);
        Assert.Equal(WidthTier.Small, facts.Content);
        Assert.Same(Policy, facts.Policy);
    }

    // ── the rungs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_pane_rungs_are_on_the_window_axis_at_1920_and_3200()
    {
        Assert.Contains(new NamedRung(ZuneTiers.PaneSecondRung, new TierEdge(1920f, NarrowHold: 24f, WidenHold: 0f), RungAxis.Window), Policy.Rungs);
        Assert.Contains(new NamedRung(ZuneTiers.PaneThirdRung, new TierEdge(3200f, NarrowHold: 24f, WidenHold: 0f), RungAxis.Window), Policy.Rungs);
        Assert.Equal("pane.second", ZuneTiers.PaneSecondRung);
        Assert.Equal("pane.third", ZuneTiers.PaneThirdRung);
    }

    [Theory]
    [InlineData(1919f, false, false)]
    [InlineData(1920f, true, false)]
    [InlineData(3199f, true, false)]
    [InlineData(3200f, true, true)]
    [InlineData(3840f, true, true)]
    public void The_pane_rungs_cross_with_the_window(float width, bool second, bool third)
    {
        // The content is narrower than the window (two panes docked): the rungs read the WINDOW.
        var facts = WindowTiers.Resolve(Policy, width, 1200f, width - 720f);
        Assert.Equal(second, facts.Rung(ZuneTiers.PaneSecondRung));
        Assert.Equal(third, facts.Rung(ZuneTiers.PaneThirdRung));
    }

    // ── the frame's records ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WidthTier.Compact, 16f, 48f, 92f, 52f, 64f)]
    [InlineData(WidthTier.Small, 24f, 48f, 84f, 52f, 72f)]
    [InlineData(WidthTier.Standard, 48f, 48f, 84f, 52f, 72f)]
    [InlineData(WidthTier.Huge, 48f, 48f, 84f, 52f, 72f)]
    [InlineData(WidthTier.Huge4K, 64f, 56f, 112f, 72f, 88f)]
    public void The_frame_record_per_window_tier_is_the_boards(WidthTier tier, float gutter, float titleBar, float band, float collapsed, float bar)
    {
        var frame = ZuneTiers.Frame(tier);
        Assert.Equal(tier, frame.Tier);
        Assert.Equal(gutter, frame.Gutter);
        Assert.Equal(titleBar, frame.TitleBarH);
        // ZT1: the band's heights are the band record's (Z-Part-Band 4k: row 1 64 + row 2 40 + 8; collapsed 64 + 8).
        Assert.Equal(band, frame.BandH);
        Assert.Equal(collapsed, frame.BandCollapsedH);
        Assert.Equal(bar, frame.BarH);
        Assert.Same(frame, ZuneTiers.FrameOf(WindowTiers.Resolve(Policy, tier switch
        {
            WidthTier.Compact => 400f, WidthTier.Small => 800f, WidthTier.Standard => 1440f, WidthTier.Huge => 2560f, _ => 3840f,
        }, 960f, 960f)));
    }

    [Fact]
    public void A_view_mounted_without_a_frame_reads_standards_record()
        => Assert.Same(ZuneTiers.Frame(WidthTier.Standard), ZuneTiers.FrameOf(TierFacts.Unknown));

    [Fact]
    public void The_page_reserves_the_desktop_bar_at_every_tier()
        => Assert.Equal(72f, ZuneTiers.BarReserveH);

    // ── the policies ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void There_is_no_nav_pane_at_any_tier()
    {
        var nav = ZunePolicies.For(ZuneParameters.Default).NavPane;
        Assert.Equal(NavPaneMode.None, nav.NarrowMode);
        Assert.Equal(NavPaneMode.None, nav.TinyMode);
        Assert.False(nav.Resizable);
        Assert.Empty(nav.Validate());
        Assert.Equal(NavPaneMode.None, nav.ModeFor(WidthTier.Compact));
        Assert.Equal(NavPaneMode.None, nav.ModeFor(WidthTier.Small));
        // The Wide band forces nothing; the pane is kept from mounting there by the capability.
        Assert.False(ZunePolicies.Capabilities.NavTree);
    }

    [Fact]
    public void The_bundle_is_one_instance_per_value_set_and_carries_zunes_tiers()
    {
        var a = ZunePolicies.For(ZuneParameters.Default);
        Assert.Same(a, ZunePolicies.For(ZuneParameters.Default));
        Assert.Same(ZuneTiers.Policy, a.Tiers);
        Assert.Empty(a.Validate());
        var other = ZunePolicies.For(ZuneParameters.Default with { MaxDockedPanes = 2 });
        Assert.Same(other, ZunePolicies.For(ZuneParameters.Default with { MaxDockedPanes = 2 }));
    }

    [Fact]
    public void The_capabilities_are_zunes()
        => Assert.Equal(new ShellCapabilities(Tabs: false, NavTree: false, FriendsPanel: true, ForwardButton: false, HistoryMenu: true, MaxBandPins: 6, DefersPageMount: true, ContinuousPageSwap: true),
            ZunePolicies.Capabilities);

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_motion_timings_are_the_boards()
    {
        Assert.Equal(250f, ZuneMotion.PivotSlideMs);
        Assert.Equal(200f, ZuneMotion.TitleScaleMs);
        Assert.Equal(120f, ZuneMotion.ColumnFadeMs);
        Assert.Equal(160f, ZuneMotion.GridCrossFadeMs);
        Assert.True(ZuneMotion.GridCrossFade.Active);
        Assert.Equal(0f, ZuneMotion.GridCrossFade.Opacity);
        Assert.True(ZuneMotion.ColumnFade.Active);
    }
}
