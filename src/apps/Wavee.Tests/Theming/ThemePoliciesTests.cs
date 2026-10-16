// ── Wavee.Tests/Theming/ThemePoliciesTests.cs — the policies a structure hands the core, and the state machines that take them (T3) ──
//
// Two questions, both answered with plain numbers (no engine, no process state):
//   1. CLASSIC BEHAVES AS TODAY. `ClassicPolicies` is the numbers the shell shipped with; the pre-T3 behaviour of each state
//      machine is restated here (the player bar's per-level layout, its ladder) or pinned by the existing rule tests, which keep
//      running on the baseline policy. The baseline and Classic's declaration are the same value, the window tiers aside (the
//      edges of the bands are the tier ladder's, T3b: `TierPolicyTests`, and here through `ClassicTierFixture`).
//   2. ANOTHER POLICY CHANGES IT, AS SPECIFIED. Each machine is driven with a policy that differs in exactly one thing and the
//      difference is asserted: what a band forces, a fixed pane, width bounds, a level's slots, a budget entry, the
//      stage's edges, the panel's bounds and the mini player's.
//
// Nothing here installs a theme: the rules take the policy as an argument. The installed-theme path is `ThemePolicyHostTests`.

using System.Collections.Immutable;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using L = Wavee.Stage.Layout;
using A = Wavee.Stage.Aspect;

namespace Wavee.Tests.Theming;

public sealed class ClassicPoliciesTests
{
    [Fact]
    public void Classic_declares_exactly_the_shell_baseline()
    {
        Assert.Equal(ThemePolicies.Default, ClassicPolicies.Bundle with { Tiers = TierPolicy.Standard, Overlays = OverlayPolicy.Default, DetailTiers = DetailTierPolicy.Default });   // core never names a theme: Default carries Standard tiers, the shipped overlays (TC9) and the shipped detail layouts (TC5)
        Assert.NotSame(TierPolicy.Standard, ClassicPolicies.Bundle.Tiers);
        Assert.NotEqual(ThemePolicies.Default, ClassicPolicies.Bundle);
        Assert.Equal(NavPanePolicy.Default, ClassicPolicies.NavPane);
        Assert.Equal(PanelPolicy.Default, ClassicPolicies.Panel);
        Assert.Equal(PlayerBarPolicy.Default, ClassicPolicies.PlayerBar);
        Assert.Equal(ChromePolicy.Default, ClassicPolicies.Chrome);
        Assert.Equal(StagePolicy.Default, ClassicPolicies.Stage);
    }

    [Fact]
    public void Classic_policies_are_valid()
    {
        Assert.Empty(ClassicPolicies.Bundle.Validate());
        Assert.Empty(ThemePolicies.Default.Validate());
    }

    [Fact]
    public void The_classic_structure_publishes_one_bundle_so_a_republish_is_not_a_change()
    {
        var theme = ClassicTheme.Create();
        var first = theme.Policies(ThemeOptionValues.Empty);
        var second = theme.Policies(ThemeOptionValues.Empty);
        Assert.Same(ClassicPolicies.Bundle, first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Classic_publishes_its_metrics_and_they_agree_with_its_policies()
    {
        var m = ClassicTheme.Create().Metrics(ThemeOptionValues.Empty);
        Assert.NotEqual(StyleMetrics.Unset, m);
        Assert.Equal(44f, m.RowSingle);
        Assert.Equal(56f, m.RowDouble);
        Assert.Equal(56f, m.RowWithArt);
        Assert.Equal(32f, m.ControlHeight);
        Assert.Equal(0f, m.PivotRow);
        Assert.Equal(0f, m.SubPivotRow);
        Assert.Equal(0f, m.PanoramaCompactBelow);
        // The dock is ONE row: the player-bar policy's height is the seek row plus the transport row, and the page reserve is the same number.
        Assert.Equal(m.SeekRow + m.TransportRow, ClassicPolicies.PlayerBar.Height);
        Assert.Equal(ClassicRoles.Table.Space.DockReserve, ClassicPolicies.PlayerBar.Height);
        // A page column keeps the content floor the panel needs beside it.
        Assert.Equal(ClassicPolicies.Panel.InlineMin, m.MinPageColumn);
        Assert.Equal(ClassicPolicies.NavPane.ContentFloorW, m.MinPageColumn);
    }

    [Fact]
    public void Classic_offers_every_capability()
    {
        Assert.Equal(ShellCapabilities.Unrestricted, ClassicTheme.Create().Capabilities(ThemeOptionValues.Empty));
    }

    [Fact]
    public void A_bundle_rebuilt_with_the_same_levels_is_equal_and_hashes_alike()
    {
        var d = PlayerBarPolicy.Default;
        var rebuilt = d with { Tiers = ImmutableArray.CreateRange(d.Tiers) };
        Assert.NotSame(d, rebuilt);
        Assert.Equal(d, rebuilt);
        Assert.Equal(d.GetHashCode(), rebuilt.GetHashCode());
        Assert.Equal(ThemePolicies.Default, ThemePolicies.Default with { PlayerBar = rebuilt });
        Assert.NotEqual(d, d with { Hysteresis = 25f });
        Assert.NotEqual(d, d with { Tiers = d.Tiers.SetItem(0, d.Tiers[0] with { LeftW = 133f }) });
    }

    [Fact]
    public void Validation_names_what_is_wrong()
    {
        var d = ThemePolicies.Default;
        Assert.NotEmpty((d with { NavPane = d.NavPane with { MinWidth = 500f } }).Validate());           // min > max
        Assert.NotEmpty((d with { NavPane = d.NavPane with { DefaultWidth = 100f } }).Validate());       // default outside the bounds
        Assert.NotEmpty((d with { NavPane = d.NavPane with { ContentFloorW = float.NaN } }).Validate());
        Assert.NotEmpty((d with { NavPane = d.NavPane with { RailWidth = 0f } }).Validate());                // the rail needs a width
        Assert.NotEmpty((d with { Tiers = d.Tiers with { CompactFloor = 0f } }).Validate());                  // the tiers are validated too
        Assert.NotEmpty((d with { Panel = d.Panel with { MinWidth = 600f } }).Validate());
        Assert.NotEmpty((d with { PlayerBar = d.PlayerBar with { Tiers = d.PlayerBar.Tiers.RemoveAt(5) } }).Validate());   // five levels
        Assert.NotEmpty((d with { PlayerBar = d.PlayerBar with { Tiers = d.PlayerBar.Tiers.SetItem(1, d.PlayerBar.Tiers[1] with { MinWidth = 0f }) } }).Validate());   // not rising
        Assert.NotEmpty((d with { PlayerBar = d.PlayerBar with { Tiers = d.PlayerBar.Tiers.SetItem(2, d.PlayerBar.Tiers[2] with { ButtonBox = 28f }) } }).Validate());   // below the hit-target floor
        Assert.NotEmpty((d with { PlayerBar = d.PlayerBar with { Tiers = d.PlayerBar.Tiers.SetItem(2, d.PlayerBar.Tiers[2] with { VisibleSlots = PlayerBarSlots.LikeSlot }) } }).Validate());   // no device route
        Assert.NotEmpty((d with { Chrome = new ChromePolicy(d.Chrome.Budget with { SearchMaxW = 100f }) }).Validate());                  // max < min
        Assert.NotEmpty((d with { Chrome = new ChromePolicy(d.Chrome.Budget with { WidthQuantumW = 0f }) }).Validate());
        Assert.NotEmpty((d with { Stage = new StagePolicy(d.Stage.Aspect with { PortraitLeave = 0.5f }) }).Validate());                  // leaves below it enters
    }
}

public sealed class NavPanePolicyRulesTests
{
    static readonly NavPanePolicy Classic = ClassicPolicies.NavPane;

    // ── Classic: the board's edges (the window ladder's, C-Breakpoints), at the bounds ─────────────────────────────────

    [Theory]
    [InlineData(1008f, SidebarWindowBand.Wide, SidebarWindowBand.Wide)]
    [InlineData(1007.9f, SidebarWindowBand.Wide, SidebarWindowBand.Narrow)]
    [InlineData(528f, SidebarWindowBand.Wide, SidebarWindowBand.Narrow)]
    [InlineData(527.9f, SidebarWindowBand.Wide, SidebarWindowBand.Tiny)]
    [InlineData(1047.9f, SidebarWindowBand.Narrow, SidebarWindowBand.Narrow)]
    [InlineData(1048f, SidebarWindowBand.Narrow, SidebarWindowBand.Wide)]
    [InlineData(527.9f, SidebarWindowBand.Narrow, SidebarWindowBand.Tiny)]
    [InlineData(567.9f, SidebarWindowBand.Tiny, SidebarWindowBand.Tiny)]
    [InlineData(568f, SidebarWindowBand.Tiny, SidebarWindowBand.Narrow)]
    [InlineData(1048f, SidebarWindowBand.Tiny, SidebarWindowBand.Wide)]
    public void Classic_bands_move_at_the_boards_edges(float width, SidebarWindowBand from, SidebarWindowBand expected)
    {
        Assert.Equal(expected, ClassicTierFixture.BandOf(width, from));
    }

    [Fact]
    public void Classic_forces_the_rail_in_narrow_and_the_drawer_in_tiny()
    {
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, false, true, Classic));
        Assert.Equal(SidebarPaneMode.Minimal, SidebarPaneModeRules.Resolve(SidebarWindowBand.Tiny, false, true, Classic));
        Assert.Equal(SidebarPaneMode.Expanded, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, false, false, Classic));
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, true, false, Classic));
        Assert.Equal(SidebarPaneMode.Expanded, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, true, true, Classic));   // Edit presents expanded
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Narrow, Classic));
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Tiny, Classic));
        Assert.False(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Wide, Classic));
        Assert.True(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Wide, Classic));
        Assert.False(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Narrow, Classic));
        Assert.True(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, false, Classic));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, true, Classic));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Narrow, false, Classic));
        Assert.True(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Tiny, true, Classic));
        Assert.False(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Wide, true, Classic));
        Assert.True(SidebarPaneModeRules.CanTogglePane(SidebarWindowBand.Tiny, false, Classic));
        Assert.False(SidebarPaneModeRules.CanTogglePane(SidebarWindowBand.Wide, true, Classic));
    }

    // ── the bands are the window tiers; the policy decides what each forces ────────────────────────────────────────────

    [Fact]
    public void A_window_tier_is_a_band_and_the_policy_answers_what_it_forces()
    {
        Assert.Equal(SidebarWindowBand.Tiny, SidebarPaneModeRules.BandOf(WidthTier.Compact));
        Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(WidthTier.Small));
        foreach (var tier in new[] { WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(SidebarWindowBand.Wide, SidebarPaneModeRules.BandOf(tier));
        Assert.Equal(NavPaneMode.Overlay, Classic.ModeFor(WidthTier.Compact));
        Assert.Equal(NavPaneMode.Compact, Classic.ModeFor(WidthTier.Small));
        Assert.Equal(NavPaneMode.Expanded, Classic.ModeFor(WidthTier.Standard));
        var p = Classic with { NarrowMode = NavPaneMode.None, TinyMode = NavPaneMode.Compact };
        Assert.Equal(NavPaneMode.Compact, p.ModeFor(WidthTier.Compact));
        Assert.Equal(NavPaneMode.None, p.ModeFor(WidthTier.Small));
        Assert.Equal(NavPaneMode.Expanded, p.ModeFor(WidthTier.Huge));
    }

    [Fact]
    public void A_degenerate_width_never_moves_a_band()
    {
        foreach (float w in new[] { 0f, -5f, float.NaN, float.PositiveInfinity })
            Assert.Equal(SidebarWindowBand.Narrow, ClassicTierFixture.BandOf(w, SidebarWindowBand.Narrow));
    }

    [Fact]
    public void The_frame_seed_follows_the_tier_and_the_policy()
    {
        var classic = FrameModel.Seed(new WindowFacts(1008f, 800f), ClassicTierFixture.FreshWindow(1008f),
            userCollapsed: false, editing: false, expandedWidth: 600f, policy: Classic);
        Assert.Equal(SidebarWindowBand.Wide, classic.Band);
        Assert.Equal(SidebarPaneMode.Expanded, classic.Mode);
        Assert.Equal(460f, classic.PresentedWidth);   // the 460 maximum (1008 less the 480 content floor is 528)
        var floor = FrameModel.Seed(new WindowFacts(750f, 800f), WidthTier.Standard,
            userCollapsed: false, editing: false, expandedWidth: 320f, policy: Classic);
        Assert.Equal(270f, floor.PresentedWidth);     // a Standard pane in a 750 window (another ladder): 750 less the 480 content floor
        var small = FrameModel.Seed(new WindowFacts(600f, 800f), ClassicTierFixture.FreshWindow(600f),
            userCollapsed: false, editing: false, expandedWidth: 320f, policy: Classic);
        Assert.Equal(SidebarWindowBand.Narrow, small.Band);
        Assert.Equal(SidebarPaneMode.Compact, small.Mode);
        Assert.Equal(Classic.RailWidth, small.PresentedWidth);
        var drawer = FrameModel.Seed(new WindowFacts(600f, 800f), ClassicTierFixture.FreshWindow(600f),
            userCollapsed: false, editing: false, expandedWidth: 320f, policy: Classic with { NarrowMode = NavPaneMode.Overlay });
        Assert.Equal(SidebarWindowBand.Narrow, drawer.Band);
        Assert.Equal(SidebarPaneMode.Minimal, drawer.Mode);
        Assert.Equal(0f, drawer.PresentedWidth);
    }

    // ── another policy: what a band forces ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_narrow_band_can_force_the_drawer_instead_of_the_rail()
    {
        var p = Classic with { NarrowMode = NavPaneMode.Overlay };
        Assert.Equal(SidebarPaneMode.Minimal, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, false, false, p));
        Assert.Equal(0f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Minimal, 320f, 600f, p));
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Narrow, p));
    }

    [Fact]
    public void A_band_with_no_pane_has_no_overlay_no_toggle_and_nothing_to_pin()
    {
        var p = Classic with { TinyMode = NavPaneMode.None };
        Assert.Equal(SidebarPaneMode.Minimal, SidebarPaneModeRules.Resolve(SidebarWindowBand.Tiny, false, true, p));
        Assert.False(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Tiny, p));
        Assert.False(SidebarPaneModeRules.CanTogglePane(SidebarWindowBand.Tiny, false, p));
        Assert.False(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Tiny, true, p));
        Assert.False(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Tiny, p));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Tiny, false, p));
        Assert.True(FrameModel.ClosesOverlay(SidebarWindowBand.Tiny, p));    // an overlay left open from another policy is closed
        Assert.False(FrameModel.ClosesOverlay(SidebarWindowBand.Tiny, Classic));
    }

    [Fact]
    public void A_band_that_forces_nothing_leaves_the_user_in_charge()
    {
        var p = Classic with { NarrowMode = NavPaneMode.Expanded };
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, true, false, p));
        Assert.Equal(SidebarPaneMode.Expanded, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, false, false, p));
        Assert.True(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Narrow, false, p));
        Assert.True(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Narrow, p));
        Assert.False(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Narrow, p));
    }

    // ── another policy: widths ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_content_floor_limits_the_presented_width()
    {
        Assert.Equal(220f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 460f, 700f, Classic));   // 700 - 480
        var p = Classic with { ContentFloorW = 300f };
        Assert.Equal(400f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 460f, 700f, p));
        Assert.True(SidebarPaneModeRules.RailFits(300f, 340f, 1119f, p));         // 300 + 340 + 300 of floor fits with room to spare
        Assert.True(SidebarPaneModeRules.RailFits(300f, 340f, 1120f, Classic));   // 300 + 340 + 480 = 1120 exactly fits
        Assert.False(SidebarPaneModeRules.RailFits(300f, 340f, 1119f, Classic));  // one DIP less does not
        Assert.False(SidebarPaneModeRules.RailFits(300f, 340f, 939f, p));         // 300 + 340 + 300 = 940
    }

    [Fact]
    public void The_width_bounds_clamp_the_preference_and_the_overlay()
    {
        var p = Classic with { MinWidth = 240f, MaxWidth = 400f, DefaultWidth = 300f };
        Assert.Empty((ThemePolicies.Default with { NavPane = p }).Validate());
        Assert.Equal(240f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 100f, 1200f, p));
        Assert.Equal(400f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 900f, 1200f, p));
        Assert.Equal(180f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 100f, 1200f, Classic));
        Assert.Equal(460f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 900f, 1200f, Classic));
        Assert.Equal(400f, SidebarPaneModeRules.OverlayWidth(900f, 1200f, p));
        Assert.Equal(460f, SidebarPaneModeRules.OverlayWidth(900f, 1200f, Classic));
        Assert.Equal(120f, SidebarPaneModeRules.OverlayWidth(900f, 168f, Classic));      // leaves the rail's worth of page
        Assert.Equal(0f, SidebarPaneModeRules.OverlayWidth(900f, 10f, Classic));         // never negative
        Assert.Equal(p.DefaultWidth, p.ClampWidth(float.NaN));
        Assert.Equal(240f, SidebarPaneBounds.Clamp(1f, p));
        Assert.Equal(180f, SidebarPaneBounds.Clamp(1f, Classic));
    }

    [Fact]
    public void A_seam_drag_collapses_and_fades_by_the_policy()
    {
        var p = Classic with { MinWidth = 240f, MaxWidth = 400f, DefaultWidth = 300f };
        var state = new SidebarResizeRules.State(false, 300f);
        var classic = SidebarResizeRules.Track(200f, in state, editing: false, Classic);
        Assert.False(classic.Collapsed);
        Assert.Equal(200f, classic.PresentedWidth);
        Assert.Equal(1f, classic.Fade);
        var other = SidebarResizeRules.Track(200f, in state, editing: false, p);
        Assert.False(other.Collapsed);                        // 200 is above the rail edge 240 - 64 = 176
        Assert.Equal(240f, other.PresentedWidth);             // clamped to the policy's floor
        Assert.Equal(1f - (40f / 64f) * (1f - SidebarResizeRules.MinFade), other.Fade, 4);
        Assert.True(SidebarResizeRules.Track(170f, in state, editing: false, p).Collapsed);     // 170 < 176
        Assert.False(SidebarResizeRules.Track(170f, in state, editing: false, Classic).Collapsed);   // 170 >= 116
        // From the rail, the expand edge is the approach plus the hold.
        var rail = new SidebarResizeRules.State(true, 300f);
        Assert.True(SidebarResizeRules.Track(199f, in rail, editing: false, p).Collapsed);      // 199 < 176 + 24 = 200
        Assert.False(SidebarResizeRules.Track(200f, in rail, editing: false, p).Collapsed);
        // Editing never collapses.
        Assert.False(SidebarResizeRules.Track(0f, in state, editing: true, p).Collapsed);
        // A non-finite sample holds the stored facts.
        Assert.Equal(300f, SidebarResizeRules.Track(float.NaN, in state, editing: false, p).PresentedWidth);
    }

    [Fact]
    public void A_fixed_pane_presents_its_default_keeps_the_preference_and_has_no_seam()
    {
        var p = Classic with { Resizable = false };
        Assert.False(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Wide, p));
        Assert.Equal(320f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 400f, 1200f, p));
        Assert.Equal(400f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 400f, 1200f, Classic));
        Assert.Equal(220f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 400f, 700f, p));   // the floor still applies

        var stepped = SidebarResizeRules.Step(new SidebarResizeRules.State(false, 250f), +1, large: false, editing: false, p);
        Assert.False(stepped.UserCollapsed);
        Assert.Equal(250f, stepped.ExpandedWidth);   // the saved preference survives a visit to a fixed pane
        Assert.Equal(320f, stepped.TargetWidth);
        var expanded = SidebarResizeRules.Toggle(new SidebarResizeRules.State(true, 250f), p);
        Assert.False(expanded.UserCollapsed);
        Assert.Equal(250f, expanded.ExpandedWidth);
        Assert.Equal(320f, expanded.TargetWidth);
        // Collapsing still works: a fixed pane is not a pane that cannot go.
        Assert.True(SidebarResizeRules.Toggle(new SidebarResizeRules.State(false, 250f), p).UserCollapsed);
    }

    [Fact]
    public void Keys_step_inside_the_policys_bounds()
    {
        var p = Classic with { MinWidth = 240f, MaxWidth = 400f, DefaultWidth = 300f };
        var up = SidebarResizeRules.Step(new SidebarResizeRules.State(false, 395f), +1, large: true, editing: false, p);
        Assert.Equal(400f, up.ExpandedWidth);
        var down = SidebarResizeRules.Step(new SidebarResizeRules.State(false, 245f), -1, large: false, editing: false, p);
        Assert.Equal(240f, down.ExpandedWidth);
        Assert.False(down.UserCollapsed);
        // At the floor, another step down collapses (not while editing).
        var floor = SidebarResizeRules.Step(new SidebarResizeRules.State(false, 240f), -1, large: false, editing: false, p);
        Assert.True(floor.UserCollapsed);
        Assert.False(SidebarResizeRules.Step(new SidebarResizeRules.State(false, 240f), -1, large: false, editing: true, p).UserCollapsed);
        // From the rail a step toward expanded returns the (clamped) preference.
        var back = SidebarResizeRules.Step(new SidebarResizeRules.State(true, 900f), +1, large: false, editing: false, p);
        Assert.False(back.UserCollapsed);
        Assert.Equal(400f, back.ExpandedWidth);
    }
}

public sealed class PlayerBarPolicyRulesTests
{
    static readonly PlayerBarPolicy Classic = ClassicPolicies.PlayerBar;

    // The ladder before T3, restated.
    static Shell.PlayerBarTier OldNominal(float width) =>
        width >= 1240f ? Shell.PlayerBarTier.Full :
        width >= 1100f ? Shell.PlayerBarTier.Wide :
        width >= 900f ? Shell.PlayerBarTier.Comfortable :
        width >= 760f ? Shell.PlayerBarTier.Medium :
        width >= 440f ? Shell.PlayerBarTier.Compact :
        Shell.PlayerBarTier.Minimal;

    // ForTier before T3, restated: every flag and metric was an expression of the level. Tuned Classic P1 (CT4): the queue joins at
    // Compact (it was Wide's), and the video split keeps Wide as its own bit.
    static Shell.PlayerBarLayout OldForTier(Shell.PlayerBarTier tier, float width = 0f)
    {
        bool full = tier == Shell.PlayerBarTier.Full;
        bool wide = tier >= Shell.PlayerBarTier.Wide;
        bool comfortable = tier >= Shell.PlayerBarTier.Comfortable;
        bool medium = tier >= Shell.PlayerBarTier.Medium;
        bool compact = tier >= Shell.PlayerBarTier.Compact;
        var layout = new Shell.PlayerBarLayout(
            Tier: tier,
            ShowExpand: full, ShowDevices: true, ShowQueue: compact, ShowVolumeSlider: wide, ShowShuffleRepeat: comfortable,
            ShowLikeSlot: true, ShowVolumeButton: medium, ShowLyrics: medium, ShowRemoteDeviceLine: comfortable,
            ShowTimesElapsed: medium, ShowTimesRemaining: compact, ShowPrevNext: medium, ShowSubtitle: true,
            ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: medium ? 40f : 36f, PrimaryGlyph: medium ? 20f : 18f,
            LeftW: tier switch
            {
                Shell.PlayerBarTier.Full => 260f,
                Shell.PlayerBarTier.Wide => 240f,
                Shell.PlayerBarTier.Comfortable => 230f,
                Shell.PlayerBarTier.Medium => 220f,
                Shell.PlayerBarTier.Compact => 172f,
                _ => 132f,
            },
            ArtSize: medium ? Design.Size.ArtPlayerBar : 40f,
            RowGap: wide ? 8f : medium ? 6f : compact ? 4f : 3f,
            RowPad: wide ? 12f : medium ? 8f : compact ? 8f : 6f,
            ClusterGap: medium ? 4f : compact ? 3f : 2f,
            LeftGap: medium ? 8f : compact ? 6f : 4f,
            SeekGap: medium ? 6f : compact ? 5f : 4f,
            RightGap: medium ? 2f : compact ? 1f : 0f,
            TopEdgeWidth: MathF.Max(Shell.PlayerBarLayout.TopEdgeWidthFloor, width),
            RightWMax: 0f,
            ShowVideoSplit: wide);
        return layout with { RightWMax = Shell.PlayerBarRules.RightWidth(in layout, hasVideo: true) };
    }

    [Fact]
    public void Classic_lays_out_every_level_exactly_as_the_expressions_did()
    {
        for (int t = 0; t <= (int)Shell.PlayerBarTier.Full; t++)
        {
            var tier = (Shell.PlayerBarTier)t;
            Assert.Equal(OldForTier(tier), Shell.PlayerBarLayout.ForTier(tier, 0f, Classic));
            Assert.Equal(OldForTier(tier, 3440f), Shell.PlayerBarLayout.ForTier(tier, 3440f, Classic));
            Assert.Equal(OldForTier(tier), Shell.PlayerBarLayout.ForTier(tier));   // the active policy is the baseline
        }
    }

    [Theory]
    [InlineData(0f)] [InlineData(300f)] [InlineData(439.9f)] [InlineData(440f)] [InlineData(759.9f)] [InlineData(760f)]
    [InlineData(899.9f)] [InlineData(900f)] [InlineData(1099.9f)] [InlineData(1100f)] [InlineData(1239.9f)] [InlineData(1240f)]
    [InlineData(3840f)] [InlineData(-10f)] [InlineData(float.NaN)]
    public void Classic_ladder_starts_each_level_where_it_always_did(float width)
        => Assert.Equal(OldNominal(width), Shell.PlayerBar.Nominal(width, Classic));

    [Fact]
    public void Classic_narrowing_holds_through_a_24_dip_dip_and_widening_commits_at_once()
    {
        Assert.Equal(Shell.PlayerBarTier.Wide, Shell.PlayerBar.Resolve(1100f, Shell.PlayerBarTier.Comfortable, true, Classic));
        Assert.Equal(Shell.PlayerBarTier.Wide, Shell.PlayerBar.Resolve(1090f, Shell.PlayerBarTier.Wide, true, Classic));
        Assert.Equal(Shell.PlayerBarTier.Comfortable, Shell.PlayerBar.Resolve(1070f, Shell.PlayerBarTier.Wide, true, Classic));
        Assert.Equal(Shell.PlayerBarTier.Full, Shell.PlayerBar.Resolve(1240f, Shell.PlayerBarTier.Minimal, true, Classic));
        Assert.Equal(Shell.PlayerBarTier.Wide, Shell.PlayerBar.Resolve(0f, Shell.PlayerBarTier.Wide, true, Classic));   // a minimized window holds
    }

    [Fact]
    public void Another_ladder_starts_the_levels_elsewhere_and_holds_for_longer()
    {
        var shifted = Classic with
        {
            Hysteresis = 60f,
            Tiers = Classic.Tiers.SetItem(2, Classic.Tiers[2] with { MinWidth = 800f }).SetItem(3, Classic.Tiers[3] with { MinWidth = 950f }),
        };
        Assert.Empty((ThemePolicies.Default with { PlayerBar = shifted }).Validate());
        Assert.Equal(Shell.PlayerBarTier.Compact, Shell.PlayerBar.Nominal(780f, shifted));
        Assert.Equal(Shell.PlayerBarTier.Medium, Shell.PlayerBar.Nominal(780f, Classic));
        Assert.Equal(Shell.PlayerBarTier.Medium, Shell.PlayerBar.Nominal(949f, shifted));
        Assert.Equal(Shell.PlayerBarTier.Comfortable, Shell.PlayerBar.Nominal(950f, shifted));
        // A 60-DIP hold: 1050 + 60 = 1110 still reaches Wide's start, so Wide holds.
        Assert.Equal(Shell.PlayerBarTier.Wide, Shell.PlayerBar.Resolve(1050f, Shell.PlayerBarTier.Wide, true, shifted));
        Assert.Equal(Shell.PlayerBarTier.Comfortable, Shell.PlayerBar.Resolve(1050f, Shell.PlayerBarTier.Wide, true, Classic));
    }

    [Fact]
    public void A_level_that_drops_a_slot_sends_its_verb_to_the_overflow_menu()
    {
        var noQueue = Classic with
        {
            Tiers = Classic.Tiers.SetItem((int)Shell.PlayerBarTier.Wide,
                Classic.Tiers[(int)Shell.PlayerBarTier.Wide] with { VisibleSlots = Classic.Tiers[(int)Shell.PlayerBarTier.Wide].VisibleSlots & ~PlayerBarSlots.Queue }),
        };
        var classicWide = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide, 0f, Classic);
        var otherWide = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Wide, 0f, noQueue);
        Assert.True(classicWide.ShowQueue);
        Assert.False(otherWide.ShowQueue);

        Span<OverflowCommand> buffer = stackalloc OverflowCommand[Shell.PlayerBarRules.MaxOverflow];
        int n = Shell.PlayerBarRules.Overflow(classicWide, ownsTransport: true, active: true, hasVideo: false, buffer);
        Assert.False(Carries(buffer[..n], OverflowCommand.Queue));
        n = Shell.PlayerBarRules.Overflow(otherWide, ownsTransport: true, active: true, hasVideo: false, buffer);
        Assert.True(Carries(buffer[..n], OverflowCommand.Queue));
        // The right cluster loses the slot's width too: a dropped slot is not reserved.
        Assert.True(otherWide.RightWMax < classicWide.RightWMax);
    }

    static bool Carries(ReadOnlySpan<OverflowCommand> commands, OverflowCommand wanted)
    {
        foreach (var command in commands)
            if (command == wanted) return true;
        return false;
    }

    [Fact]
    public void A_level_carries_its_own_spacing_and_art()
    {
        var roomy = Classic with
        {
            Tiers = Classic.Tiers.SetItem(0, Classic.Tiers[0] with { ArtSize = 44f, LeftW = 150f, RowPad = 10f }),
        };
        var layout = Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal, 0f, roomy);
        Assert.Equal(44f, layout.ArtSize);
        Assert.Equal(150f, layout.LeftW);
        Assert.Equal(10f, layout.RowPad);
        Assert.Equal(40f, Shell.PlayerBarLayout.ForTier(Shell.PlayerBarTier.Minimal, 0f, Classic).ArtSize);
    }

    [Fact]
    public void The_initial_layout_and_the_resolved_one_agree_with_the_ladder()
    {
        var initial = Shell.PlayerBarLayout.Initial(1000f, Classic);
        Assert.Equal(Shell.PlayerBarTier.Comfortable, initial.Tier);
        var held = Shell.PlayerBarLayout.Resolve(890f, in initial, initialized: true, Classic);
        Assert.Equal(Shell.PlayerBarTier.Comfortable, held.Tier);   // 890 + 24 still reaches 900: the level holds
        var narrowed = Shell.PlayerBarLayout.Resolve(870f, in initial, initialized: true, Classic);
        Assert.Equal(Shell.PlayerBarTier.Medium, narrowed.Tier);    // 870 + 24 = 894 does not
        var widened = Shell.PlayerBarLayout.Resolve(1250f, in initial, initialized: true, Classic);
        Assert.Equal(Shell.PlayerBarTier.Full, widened.Tier);
        Assert.Equal(Shell.PlayerBarLayout.TopEdgeWidthFloor, widened.TopEdgeWidth);   // 1250 < 2400: the floor
    }
}

public sealed class ChromeBudgetRulesTests
{
    static readonly ChromeBudget Classic = ClassicPolicies.Chrome.Budget;
    const Shell.FrameRules.ChipForm Profile = Shell.FrameRules.ChipForm.Profile;

    [Fact]
    public void Classic_allocates_as_the_named_widths_did()
    {
        // 1000 wide is below the 1200 actions edge; 1200 with room for the field enters the actions; 1360 the name.
        var narrow = Shell.Chrome.Resolve(1000f, 110f, null, Profile, Classic);
        Assert.False(narrow.ShowActions);
        Assert.False(narrow.ShowName);
        Assert.Equal(Shell.Chrome.Resolve(1000f, 110f, null, Profile), narrow);   // the active policy is the baseline
        var wide = Shell.Chrome.Resolve(1400f, 110f, null, Profile, Classic);
        Assert.True(wide.ShowActions);
        Assert.True(wide.ShowName);
        Assert.Equal(Shell.MergedSearchMode.Field, wide.SearchMode);
        // No theme toggle (tuned F13): the bare row is the lead column, the two gutters, the drag strip and the captions.
        Assert.Equal(262f, Shell.Chrome.FixedBudget(false, false, false, false, false, false, Profile, Classic));
    }

    [Fact]
    public void An_earlier_actions_edge_brings_the_actions_in_sooner_when_the_row_can_afford_them()
    {
        var sooner = Classic with { ActionsEnterW = 500f };
        Assert.Empty((ThemePolicies.Default with { Chrome = new ChromePolicy(sooner) }).Validate());
        Assert.False(Shell.Chrome.Resolve(1000f, 110f, null, Profile, Classic).ShowActions);
        Assert.True(Shell.Chrome.Resolve(1000f, 110f, null, Profile, sooner).ShowActions);   // 1000 - 546 - 110 = 344 >= 280
        // A threshold is a permission, never a decision: an edge below what the row can afford changes nothing.
        Assert.False(Shell.Chrome.Resolve(700f, 110f, null, Profile, sooner).ShowActions);   // 700 - 546 - 110 = 44 < 280
    }

    [Fact]
    public void The_forward_edge_hides_forward_in_a_narrow_window()
    {
        Assert.True(Shell.Chrome.Resolve(600f, 110f, null, Profile, Classic).ShowForward);
        Assert.False(Shell.Chrome.Resolve(600f, 110f, null, Profile, Classic with { ForwardEnterW = 700f }).ShowForward);
        Assert.False(Shell.Chrome.Resolve(520f, 110f, null, Profile, Classic).ShowForward);      // the edge itself hides (width > edge)
    }

    [Fact]
    public void Costs_and_quantum_are_the_budgets()
    {
        var wider = Classic with { BarLeadW = 100f };
        Assert.Equal(302f, Shell.Chrome.FixedBudget(false, false, false, false, false, false, Profile, wider));
        Assert.Equal(Classic.ProfileChipW, Shell.Chrome.ChipWidth(Profile, Classic));
        Assert.Equal(Classic.SignInChipW, Shell.Chrome.ChipWidth(Shell.FrameRules.ChipForm.SignIn, Classic));
        Assert.Equal(60f, Shell.Chrome.EstimatedTabExtent(1, 0, Classic with { TabMinW = 60f }));
        Assert.Equal(150f, Shell.Chrome.EstimatedTabExtent(2, 1, Classic));    // two tabs, one pinned: 40 + 110
        // The search field's floor and ceiling.
        Assert.Equal(280f, Shell.Chrome.FieldWidthFor(100f, Classic));
        Assert.Equal(420f, Shell.Chrome.FieldWidthFor(9999f, Classic));
        Assert.Equal(300f, Shell.Chrome.FieldWidthFor(100f, Classic with { SearchMinW = 300f }));
        Assert.Equal(200f, Shell.Chrome.FieldWidthFor(9999f, Classic with { SearchMaxW = 200f, SearchMinW = 100f }));
        Assert.Equal(Classic.SearchIconW, Shell.Chrome.Resolve(500f, 110f, null, Profile, Classic).SearchWidth);
    }

    [Fact]
    public void A_promotion_waits_for_the_hysteresis_and_a_demotion_does_not()
    {
        var before = Shell.Chrome.Resolve(1000f, 110f, null, Profile, Classic);
        Assert.False(before.ShowActions);
        // Widening past the edge is re-resolved 40 DIP narrower: the actions arrive at 1240, not 1200.
        Assert.False(Shell.Chrome.Resolve(1210f, 110f, before, Profile, Classic).ShowActions);
        var arrived = Shell.Chrome.Resolve(1240f, 110f, before, Profile, Classic);
        Assert.True(arrived.ShowActions);
        // Narrowing below the edge takes them away at once.
        Assert.False(Shell.Chrome.Resolve(1199f, 110f, arrived, Profile, Classic).ShowActions);
        // A policy with no hold promotes at the edge.
        var noHold = Classic with { PromotionHysteresisW = 0f };
        Assert.True(Shell.Chrome.Resolve(1210f, 110f, before, Profile, noHold).ShowActions);
    }
}

public sealed class StageAspectPolicyTests
{
    static readonly AspectThresholds Classic = ClassicPolicies.Stage.Aspect;

    [Theory]
    [InlineData(599f, 1000f, A.Compact)]
    [InlineData(600f, 1000f, A.Portrait)]    // 0.6
    [InlineData(1000f, 460f, A.Compact)]     // landscape, short
    [InlineData(1000f, 600f, A.Desktop)]
    [InlineData(2000f, 1000f, A.Ultrawide)]
    [InlineData(1920f, 1080f, A.Desktop)]
    public void Classic_classes_are_the_ones_it_always_had(float w, float h, A expected)
    {
        Assert.Equal(expected, L.ClassOf(w, h, A.Desktop, Classic));
        Assert.Equal(expected, L.ClassOf(w, h, A.Desktop));   // the active policy is the baseline
    }

    [Fact]
    public void Classic_holds_each_class_through_its_hysteresis()
    {
        Assert.Equal(A.Ultrawide, L.ClassOf(1970f, 1000f, A.Ultrawide, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(1970f, 1000f, A.Desktop, Classic));
        Assert.Equal(A.Compact, L.ClassOf(900f, 483f, A.Compact, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(900f, 484f, A.Compact, Classic));
        Assert.Equal(A.Portrait, L.ClassOf(830f, 1000f, A.Portrait, Classic));
        Assert.Equal(A.Desktop, L.ClassOf(860f, 1000f, A.Portrait, Classic));
    }

    [Fact]
    public void Other_edges_reclassify_the_same_window()
    {
        // 700 x 500: Desktop under the baseline; Compact when the width edge rises to 800.
        Assert.Equal(A.Desktop, L.ClassOf(700f, 500f, null, Classic));
        var wideEdge = Classic with { CompactBelowW = 800f };
        Assert.Equal(A.Compact, L.ClassOf(700f, 500f, null, wideEdge));
        // 1000 x 700 (ratio 1.43): Desktop; Ultrawide once its edge drops to 1.4.
        Assert.Equal(A.Desktop, L.ClassOf(1000f, 700f, null, Classic));
        var ultra = Classic with { UltrawideEnter = 1.4f, UltrawideLeave = 1.3f };
        Assert.Empty((ThemePolicies.Default with { Stage = new StagePolicy(ultra) }).Validate());
        Assert.Equal(A.Ultrawide, L.ClassOf(1000f, 700f, null, ultra));
        // A taller compact band.
        var tall = Classic with { CompactEnterH = 600f, CompactLeaveH = 640f };
        Assert.Equal(A.Compact, L.ClassOf(1000f, 600f, A.Desktop, tall));
        Assert.Equal(A.Compact, L.ClassOf(1000f, 639f, A.Compact, tall));
        Assert.Equal(A.Desktop, L.ClassOf(1000f, 640f, A.Compact, tall));
    }

    [Fact]
    public void The_layout_resolves_through_the_policy_and_a_degenerate_size_keeps_the_class()
    {
        var wideEdge = Classic with { CompactBelowW = 800f };
        Assert.Equal(A.Compact, L.Resolve(700f, 500f, null, wideEdge).Aspect);
        Assert.Equal(A.Desktop, L.Resolve(700f, 500f, null, Classic).Aspect);
        Assert.Equal(A.Compact, L.Seed(700f, 500f, wideEdge).Aspect);
        Assert.Equal(A.Portrait, L.ClassOf(0f, 0f, A.Portrait, wideEdge));
        Assert.Equal(A.Desktop, L.ClassOf(0f, 0f, null, wideEdge));
    }
}

public sealed class PanelPolicyRulesTests
{
    static readonly PanelPolicy Classic = ClassicPolicies.Panel;

    [Fact]
    public void Classic_clamps_the_panel_to_200_500()
    {
        Assert.Equal(200f, Shell.ClampRailWidth(10f, Classic));
        Assert.Equal(500f, Shell.ClampRailWidth(10_000f, Classic));
        Assert.Equal(340f, Shell.ClampRailWidth(340f, Classic));
        Assert.Equal(Shell.RailMinW, Shell.ClampRailWidth(10f));   // the active policy is the baseline
        Assert.Equal(Classic.MinWidth, Classic.ClampWidth(float.NaN));
        Assert.Equal(200f, Shell.FrameRules.RailDragCommit(10f, 340f));
        Assert.Null(Shell.FrameRules.RailDragCommit(340.005f, 340f));
    }

    [Fact]
    public void Other_bounds_clamp_the_panel_and_the_drag_commit()
    {
        var p = Classic with { MinWidth = 260f, MaxWidth = 400f };
        Assert.Empty((ThemePolicies.Default with { Panel = p }).Validate());
        Assert.Equal(260f, Shell.ClampRailWidth(100f, p));
        Assert.Equal(400f, Shell.ClampRailWidth(999f, p));
    }

    [Fact]
    public void The_inline_floor_decides_floating()
    {
        // Classic: sidebar 280 + panel 340 + 480 of content = 1100.
        Assert.True(Shell.CanFitRail(1100f, 280f, 340f, Classic.InlineMin));
        Assert.False(Shell.CanFitRail(1099f, 280f, 340f, Classic.InlineMin));
        var p = Classic with { InlineMin = 600f };
        Assert.False(Shell.CanFitRail(1100f, 280f, 340f, p.InlineMin));
        Assert.True(Shell.CanFitRail(1220f, 280f, 340f, p.InlineMin));
    }

    [Fact]
    public void A_panel_policy_change_brings_the_committed_width_inside_without_persisting_it()
    {
        var cells = Shell.PanelCells.Fresh();
        var store = new DictionaryPanelSizeStore();
        var host = new Shell.PanelHost(cells, new Shell.PanelSizes(store), new RecordingPanelEnv());
        cells.Width.Value = 480f;
        cells.DragWidth.Value = 480f;

        host.FitWidthTo(Classic);                         // already inside: untouched
        Assert.Equal(480f, cells.Width.Peek());

        host.FitWidthTo(Classic with { MaxWidth = 400f });
        Assert.Equal(400f, cells.Width.Peek());
        Assert.Equal(400f, cells.DragWidth.Peek());
        Assert.Empty(store.Writes);                       // the stored preference is the user's, not the policy's

        host.FitWidthTo(Classic with { MinWidth = 450f, MaxWidth = 500f });
        Assert.Equal(450f, cells.Width.Peek());
    }

    [Fact]
    public void The_mini_player_works_inside_the_policys_bounds()
    {
        var wide = Classic with { Pip = Video.PipBounds.Classic with { Margin = 40f } };
        Assert.Empty((ThemePolicies.Default with { Panel = wide }).Validate());
        Assert.Equal(904f, Video.PipGesture.ClampX(5000f, 1280f, 360f, Classic.Pip));       // 1280 - 360 - 16
        Assert.Equal(880f, Video.PipGesture.ClampX(5000f, 1280f, 360f, wide.Pip));          // 1280 - 360 - 40
        Assert.Equal(16f, Video.PipGesture.ClampX(-40f, 1280f, 360f, Classic.Pip));
        Assert.Equal(40f, Video.PipGesture.ClampX(-40f, 1280f, 360f, wide.Pip));
        Assert.Equal(Video.PipBounds.Classic, Classic.Pip);

        var taller = Classic with { Pip = Video.PipBounds.Classic with { MinH = 300f } };
        Assert.Equal(300f, Video.Pip.FitHeight(360f, 9f / 16f, 900f, taller.Pip));
        Assert.Equal(202.5f, Video.Pip.FitHeight(360f, 9f / 16f, 900f, Classic.Pip), 2);
    }
}
