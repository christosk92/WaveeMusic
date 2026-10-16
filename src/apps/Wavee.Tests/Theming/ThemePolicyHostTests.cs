// ── Wavee.Tests/Theming/ThemePolicyHostTests.cs — the installed theme's policies reach the core state machines (T3) ─────
//
// `ThemePoliciesTests` drives each rule with a policy passed in. These facts go through the process state instead: a
// structure's policies are published by `ThemeHost` (on `ThemeHost.Policies` and inside `ThemeHost.Facts`), and a rule called
// WITHOUT a policy reads them. A switch republishes them, a live option that moves a policy republishes them, and a policy the
// shell cannot run on is replaced by the baseline instead of reaching a rule. `ThemeHost` is process state, so the class is in
// the platform collection (serial, and run apart from the parallel ones) and resets it on both sides.

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ThemePolicyHostTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public ThemePolicyHostTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    /// <summary>Another structure's policies: the Narrow band forces the drawer instead of the rail, the player bar's Medium level starts at
    /// 800, the stage is Compact under 800 wide, the panel is 260-400, the chrome row shows its actions from 500.</summary>
    static ThemePolicies Other()
    {
        var d = ThemePolicies.Default;
        return d with
        {
            NavPane = d.NavPane with { NarrowMode = NavPaneMode.Overlay },
            PlayerBar = d.PlayerBar with { Tiers = d.PlayerBar.Tiers.SetItem(2, d.PlayerBar.Tiers[2] with { MinWidth = 800f }) },
            Panel = d.Panel with { MinWidth = 260f, MaxWidth = 400f },
            Chrome = new ChromePolicy(d.Chrome.Budget with { ActionsEnterW = 500f }),
            Stage = new StagePolicy(d.Stage.Aspect with { CompactBelowW = 800f }),
        };
    }

    static ThemeComposition Structured(string id, Func<OptionValues, StyleMetrics, ThemePolicies> policies, ThemeOptionSchema? schema = null)
        => Fakes.Theme(id, new FakeStructure(id + ".structure", schema) { PolicyFn = policies });

    /// <summary>What the core rules answer for the windows where <see cref="Other"/> and the baseline differ.</summary>
    static (SidebarPaneMode NarrowPane, Shell.PlayerBarTier Level, float Panel, bool Actions, Stage.Aspect StageClass) Probe() => (
        SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, userCollapsed: false, editing: false),
        Shell.PlayerBar.Nominal(780f),
        Shell.ClampRailWidth(500f),
        Shell.Chrome.Resolve(1000f, 110f, null, Shell.FrameRules.ChipForm.Profile).ShowActions,
        Stage.Layout.ClassOf(700f, 500f, null));

    static readonly (SidebarPaneMode, Shell.PlayerBarTier, float, bool, Stage.Aspect) s_baseline =
        (SidebarPaneMode.Compact, Shell.PlayerBarTier.Medium, 500f, false, Stage.Aspect.Desktop);

    static readonly (SidebarPaneMode, Shell.PlayerBarTier, float, bool, Stage.Aspect) s_other =
        (SidebarPaneMode.Minimal, Shell.PlayerBarTier.Compact, 400f, true, Stage.Aspect.Compact);

    [Fact]
    public void Before_install_the_baseline_is_what_the_core_runs_on()
    {
        Assert.Same(ThemePolicies.Default, ShellPolicy.Now);
        Assert.Same(ThemePolicies.Default, ShellPolicy.Tracked);
        Assert.Same(ThemePolicies.Default, ThemeHost.Facts.Peek().Policies);
        Assert.Equal(s_baseline, Probe());
    }

    [Fact]
    public void Classic_installed_publishes_its_declaration_and_changes_nothing_for_the_core()
    {
        ThemeHost.Install(ClassicTheme.Create());
        // Equal, not the same instance: Classic's bundle equals the baseline value for value but for its window tiers and its overlay layouts (core never
        // names a theme, so the baseline carries the Standard tiers and the shipped overlays), so the rules below answer exactly what they did.
        Assert.Equal(ClassicPolicies.Bundle, ShellPolicy.Now);
        Assert.Equal(ClassicPolicies.Bundle, ThemeHost.Facts.Peek().Policies);
        Assert.Equal(ThemePolicies.Default, ShellPolicy.Now with { Tiers = TierPolicy.Standard, Overlays = OverlayPolicy.Default });
        Assert.Equal(ShellCapabilities.Unrestricted, ThemeHost.Facts.Peek().Capabilities);
        Assert.NotEqual(StyleMetrics.Unset, ThemeHost.Facts.Peek().Metrics);
        Assert.Equal(s_baseline, Probe());
    }

    [Fact]
    public void The_page_bottom_reserve_follows_the_active_bars_height()
    {
        // Classic: the bar is 72 and so is the style table's reserve; the table itself is returned, not a copy.
        ThemeHost.Install(ClassicTheme.Create());
        Assert.Equal(72f, R.Space.DockReserve);
        Assert.Same(R.Current.Space, R.Space);

        // Another structure's bar is another height; the pages reserve what it covers, and the style table is untouched.
        ThemeHost.Install(Structured("tall", static (_, _) =>
        {
            var d = ThemePolicies.Default;
            return d with { PlayerBar = d.PlayerBar with { Height = 88f } };
        }));
        Assert.Equal(88f, R.Space.DockReserve);
        Assert.Equal(72f, R.Current.Space.DockReserve);
        Assert.Equal(R.Current.Space.SectionGap, R.Space.SectionGap);   // nothing else about the table moves
    }

    [Fact]
    public void An_installed_structures_policies_are_what_a_rule_without_a_policy_reads()
    {
        ThemeHost.Install(Structured("zune", static (_, _) => Other()));
        Assert.Equal(Other(), ShellPolicy.Now);
        Assert.Same(ThemeHost.Facts.Peek().Policies, ThemeHost.Policies.Peek());
        Assert.Equal(s_other, Probe());
        Assert.Equal(260f, Shell.ClampRailWidth(10f));
        Assert.Equal(200f, PanelPolicy.Default.MinWidth);   // the baseline object is untouched by another theme's policy
    }

    [Fact]
    public async Task A_switch_republishes_the_policies_and_the_rules_follow_in_both_directions()
    {
        ThemeHost.Install(Plain("classic"), Structured("zune", static (_, _) => Other()));
        Assert.Equal(s_baseline, Probe());

        await ThemeHost.Switch("zune");
        Assert.Equal(Other(), ShellPolicy.Now);
        Assert.Equal(s_other, Probe());

        await ThemeHost.Switch("classic");
        Assert.Same(ThemePolicies.Default, ShellPolicy.Now);
        Assert.Equal(s_baseline, Probe());
    }

    [Fact]
    public async Task A_live_option_that_moves_a_policy_republishes_it_without_a_remount()
    {
        var narrowed = Fakes.Toggle("narrowed");
        ThemeHost.Install(Structured("w", static (v, _) => v.Get(new OptionKey<bool>("narrowed", false)) ? Other() : ThemePolicies.Default,
            Fakes.Schema(narrowed)));
        long epoch = ThemeHost.RootEpoch.Peek();
        Assert.Equal(s_baseline, Probe());

        await ThemeHost.Apply(new OptionChange("w", "structure.narrowed", true));
        Assert.Equal(s_other, Probe());
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());

        await ThemeHost.Apply(new OptionChange("w", "structure.narrowed", false));
        Assert.Equal(s_baseline, Probe());
    }

    [Fact]
    public void A_policy_the_shell_cannot_run_on_is_replaced_by_the_baseline()
    {
        // An inverted pane width range, a player bar with five levels: either alone is enough.
        ThemeHost.Install(Structured("bad", static (_, _) =>
        {
            var d = ThemePolicies.Default;
            return d with { NavPane = d.NavPane with { MinWidth = 500f } };
        }));
        Assert.Same(ThemePolicies.Default, ShellPolicy.Now);
        Assert.Same(ThemePolicies.Default, ThemeHost.Facts.Peek().Policies);
        Assert.Equal(s_baseline, Probe());

        ThemeHost.Install(Structured("bad2", static (_, _) =>
        {
            var d = ThemePolicies.Default;
            return d with { PlayerBar = d.PlayerBar with { Tiers = d.PlayerBar.Tiers.RemoveAt(5) } };
        }));
        Assert.Same(ThemePolicies.Default, ShellPolicy.Now);
    }

    [Fact]
    public async Task Switching_from_a_bad_theme_to_a_good_one_and_back_never_leaves_a_stale_policy()
    {
        ThemeHost.Install(Plain("classic"), Structured("zune", static (_, _) => Other()),
            Structured("bad", static (_, _) => ThemePolicies.Default with { Panel = PanelPolicy.Default with { MinWidth = 900f } }));
        await ThemeHost.Switch("zune");
        Assert.Equal(Other(), ShellPolicy.Now);
        await ThemeHost.Switch("bad");
        Assert.Same(ThemePolicies.Default, ShellPolicy.Now);
        await ThemeHost.Switch("zune");
        Assert.Equal(Other(), ShellPolicy.Now);
    }

    [Fact]
    public void Reset_returns_to_the_baseline()
    {
        ThemeHost.Install(Structured("zune", static (_, _) => Other()));
        ThemeHost.Reset();
        Assert.Same(ThemePolicies.Default, ShellPolicy.Now);
        Assert.Same(ThemePolicies.Default, ThemeHost.Facts.Peek().Policies);
    }

    [Fact]
    public void The_pane_resolves_a_fresh_launch_through_the_active_policy()
    {
        ThemeHost.Install(Structured("zune", static (_, _) => Other()));
        var seed = FrameModel.Seed(new WindowFacts(600f, 800f), WidthTier.Small, userCollapsed: false, editing: false, expandedWidth: 320f);
        Assert.Equal(SidebarWindowBand.Narrow, seed.Band);
        Assert.Equal(SidebarPaneMode.Minimal, seed.Mode);   // the other structure's Narrow band is the drawer
        ThemeHost.Install(Plain("classic"));
        var classic = FrameModel.Seed(new WindowFacts(600f, 800f), WidthTier.Small, userCollapsed: false, editing: false, expandedWidth: 320f);
        Assert.Equal(SidebarWindowBand.Narrow, classic.Band);
        Assert.Equal(SidebarPaneMode.Compact, classic.Mode);
        Assert.Equal(SidebarWindowBand.Wide, FrameModel.Seed(new WindowFacts(750f, 800f), WidthTier.Standard, userCollapsed: false, editing: false, expandedWidth: 320f).Band);
    }

    static ThemeComposition Plain(string id) => Fakes.Theme(id);
}
