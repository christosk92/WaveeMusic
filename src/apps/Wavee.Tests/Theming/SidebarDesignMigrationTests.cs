// ── Wavee.Tests/Theming/SidebarDesignMigrationTests.cs — M7a: the sidebar layout and density became Classic's options ─────
//
// `sidebar.layout.id` (0 Classic, 1 Library) and `sidebar.pane.density` (0 Default, 1 Compact) are copied by the "SidebarLayout" and "SidebarDensity" bootstrap
// steps to `theme.classic.structure.sidebar` / `.libraryDensity` (the enum ints), so a Library user stays on Library and a Compact user on Compact. The options
// and the pane policy that carries them into the core sidebar are pinned here too.

using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class SidebarDesignMigrationTests
{
    static readonly SettingKey<int> LegacyLayout = new("sidebar.layout.id", 0);
    static readonly SettingKey<int> LegacyDensity = new("sidebar.pane.density", 0);
    static readonly SettingKey<int> Layout = new("theme.classic.structure.sidebar", 0);
    static readonly SettingKey<int> Density = new("theme.classic.structure.libraryDensity", 0);

    static ThemeBootstrapStep Step(string name) => Assert.Single(ThemeBootstrap.Steps, s => s.Name == name);

    static SnapshotSettings Run(SnapshotSettings s)
    {
        ThemeBootstrap.Run(s, [Step("SidebarLayout"), Step("SidebarDensity")]);
        return s;
    }

    [Fact]
    public void The_steps_read_the_legacy_keys_and_write_the_options_keys()
    {
        var layout = Step("SidebarLayout");
        Assert.Equal(new[] { "sidebar.layout.id" }, layout.ReadsLegacyKeys);
        Assert.Equal(new[] { "theme.classic.structure.sidebar" }, layout.WritesKeys);
        Assert.Equal(ThemeOptionStore.SettingName(ClassicTheme.Id, OptionLayer.Structure, ClassicStructure.Sidebar.Id), layout.WritesKeys[0]);
        var density = Step("SidebarDensity");
        Assert.Equal(new[] { "sidebar.pane.density" }, density.ReadsLegacyKeys);
        Assert.Equal(new[] { "theme.classic.structure.libraryDensity" }, density.WritesKeys);
        Assert.Equal(ThemeOptionStore.SettingName(ClassicTheme.Id, OptionLayer.Structure, ClassicStructure.LibraryDensity.Id), density.WritesKeys[0]);
    }

    [Theory]
    [InlineData(0, SidebarLayoutId.Classic)]
    [InlineData(1, SidebarLayoutId.Library)]
    public void A_stored_layout_keeps_its_value(int stored, SidebarLayoutId expected)
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, stored);
        Run(s);
        Assert.Equal(stored, s.Get(Layout));
        Assert.True(s.Has("theme.classic.structure.sidebar"));
        Assert.Equal(stored, s.Get(LegacyLayout));   // the legacy key stays as inert data
        Assert.Equal(expected, ThemeOptionStore.Load(ClassicTheme.Create(), s).Structure.Get(ClassicStructure.Sidebar.Key));
    }

    [Theory]
    [InlineData(0, SidebarDensity.Default)]
    [InlineData(1, SidebarDensity.Compact)]
    public void A_stored_density_keeps_its_value(int stored, SidebarDensity expected)
    {
        var s = new SnapshotSettings();
        s.Set(LegacyDensity, stored);
        Run(s);
        Assert.Equal(stored, s.Get(Density));
        Assert.Equal(expected, ThemeOptionStore.Load(ClassicTheme.Create(), s).Structure.Get(ClassicStructure.LibraryDensity.Key));
    }

    [Theory]
    [InlineData(2)]    // a profile that ran the discarded PR 205 branch
    [InlineData(-1)]
    [InlineData(7)]
    public void An_unknown_value_is_not_copied_so_the_default_applies(int stored)
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, stored);
        s.Set(LegacyDensity, stored);
        Run(s);
        Assert.False(s.Has("theme.classic.structure.sidebar"));
        Assert.False(s.Has("theme.classic.structure.libraryDensity"));
    }

    [Fact]
    public void Nothing_stored_writes_nothing_so_the_defaults_apply()
    {
        var s = Run(new SnapshotSettings());
        Assert.False(s.Has("theme.classic.structure.sidebar"));
        Assert.False(s.Has("theme.classic.structure.libraryDensity"));
    }

    [Fact]
    public void An_option_the_user_already_set_is_never_overwritten()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, 1);
        s.Set(Layout, 0);
        s.Set(LegacyDensity, 1);
        s.Set(Density, 0);
        Run(s);
        Assert.Equal(0, s.Get(Layout));
        Assert.Equal(0, s.Get(Density));
    }

    [Fact]
    public void The_steps_run_once()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, 1);
        Assert.Equal(2, ThemeBootstrap.Run(s, [Step("SidebarLayout"), Step("SidebarDensity")]));
        s.Set(Layout, 0);
        Assert.Equal(0, ThemeBootstrap.Run(s, [Step("SidebarLayout"), Step("SidebarDensity")]));
        Assert.Equal(0, s.Get(Layout));
    }

    [Fact]
    public void A_migrated_library_user_loads_the_library_layout_and_the_pane_policy_carries_it()
    {
        var theme = ClassicTheme.Create();
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, 1);
        s.Set(LegacyDensity, 1);
        Run(s);

        var values = ThemeOptionStore.Load(theme, s);
        Assert.Equal(SidebarLayoutId.Library, values.Structure.Get(ClassicStructure.Sidebar.Key));
        Assert.Equal(SidebarDensity.Compact, values.Structure.Get(ClassicStructure.LibraryDensity.Key));
        var nav = theme.Policies(values).NavPane;
        Assert.Equal(SidebarLayoutId.Library, nav.Layout);
        Assert.Equal(SidebarDensity.Compact, nav.Density);
    }

    [Fact]
    public void The_options_are_a_cards_select_for_the_layout_and_a_combo_for_the_density_and_the_layout_asks_for_palette_rows()
    {
        var layout = ClassicStructure.Sidebar;
        Assert.Equal(NavPaneOptionIds.Layout, layout.Id);
        Assert.Equal(SidebarLayoutId.Classic, layout.Default);
        Assert.Equal(PickerStyle.Cards, layout.Picker);
        Assert.Equal(new[] { SidebarLayoutId.Classic, SidebarLayoutId.Library }, layout.Choices.Select(c => c.Value));
        Assert.True(layout.Command);
        Assert.Same(layout, new ClassicStructure().Options.Find("sidebar"));

        var density = ClassicStructure.LibraryDensity;
        Assert.Equal(NavPaneOptionIds.Density, density.Id);
        Assert.Equal(SidebarDensity.Default, density.Default);
        Assert.Equal(PickerStyle.Combo, density.Picker);
        Assert.Equal(new[] { SidebarDensity.Default, SidebarDensity.Compact }, density.Choices.Select(c => c.Value));
        Assert.False(density.Command);
        Assert.Same(density, new ClassicStructure().Options.Find("libraryDensity"));
    }

    [Fact]
    public void Show_covers_is_a_classic_layout_toggle_on_by_default()
    {
        // S0 (owner NQ6: covers On), shown only with the Classic layout (the Library layout always shows covers).
        var covers = ClassicStructure.ShowCovers;
        Assert.Equal("showCovers", covers.Id);
        Assert.True(covers.Default);
        Assert.Equal(new OptionCondition(NavPaneOptionIds.Layout, SidebarLayoutId.Classic), covers.VisibleWhen);
        Assert.Equal(ClassicStructure.ShowCoversQualifiedId, "structure." + covers.Id);
        Assert.Same(covers, new ClassicStructure().Options.Find("showCovers"));
    }

    [Fact]
    public void Covers_off_reaches_the_pane_policy_and_on_is_the_bundle()
    {
        Assert.Same(ClassicPolicies.Bundle, ClassicPolicies.For(SidebarLayoutId.Classic, SidebarDensity.Default, showCovers: true));
        Assert.True(ClassicPolicies.Bundle.NavPane.ShowCovers);
        var off = ClassicPolicies.For(SidebarLayoutId.Classic, SidebarDensity.Default, showCovers: false);
        Assert.Same(off, ClassicPolicies.For(SidebarLayoutId.Classic, SidebarDensity.Default, showCovers: false));
        Assert.Equal(ClassicPolicies.NavPane with { ShowCovers = false }, off.NavPane);
        Assert.Empty(off.Validate());
    }

    [Fact]
    public void The_policy_for_the_default_pair_is_the_bundle_itself_and_each_other_pair_differs_only_in_the_pane()
    {
        Assert.Same(ClassicPolicies.Bundle, ClassicPolicies.For(SidebarLayoutId.Classic, SidebarDensity.Default));
        foreach (var layout in new[] { SidebarLayoutId.Classic, SidebarLayoutId.Library })
            foreach (var density in new[] { SidebarDensity.Default, SidebarDensity.Compact })
            {
                var policies = ClassicPolicies.For(layout, density);
                Assert.Same(policies, ClassicPolicies.For(layout, density));   // one instance per pair: republishing is not a change
                Assert.Equal(layout, policies.NavPane.Layout);
                Assert.Equal(density, policies.NavPane.Density);
                Assert.Equal(ClassicPolicies.NavPane with { Layout = layout, Density = density }, policies.NavPane);
                Assert.Same(ClassicPolicies.Bundle.Tiers, policies.Tiers);
                Assert.Same(ClassicPolicies.Panel, policies.Panel);
                Assert.Empty(policies.Validate());
            }
    }
}
