// ── Wavee.Tests/Theming/DetailLayoutMigrationTests.cs — M8: the page layout and the rails became Classic's options and state ──
//
// `detail.page.layout` (0 Automatic, 1 Hero) and `detail.rail.uniform` are copied by the "DetailPageLayout" and "DetailRailUniform" bootstrap steps to
// `theme.classic.structure.pageLayout` / `.railUniform`; the ten remembered rail sizes (`detail.rail.<scope>.width|collapsed`) by the "DetailRailSizes" step
// to Classic's theme state (`theme.classic.state.rail.<scope>.width|collapsed`). An existing user keeps their layout and their rails. The three options, what
// each shows when, and the policy that carries the layout and the uniformity into the core frame are pinned here too.

using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class DetailLayoutMigrationTests : IDisposable
{
    static readonly SettingKey<int> LegacyLayout = new("detail.page.layout", 0);
    static readonly SettingKey<bool> LegacyUniform = new("detail.rail.uniform", false);
    static readonly SettingKey<int> Layout = new("theme.classic.structure.pageLayout", 0);
    static readonly SettingKey<bool> Uniform = new("theme.classic.structure.railUniform", false);

    static readonly string[] Scopes = ["album", "playlist", "liked", "show", "uniform"];

    static ThemeBootstrapStep Step(string name) => Assert.Single(ThemeBootstrap.Steps, s => s.Name == name);

    static readonly string[] StepNames = ["DetailPageLayout", "DetailRailUniform", "DetailRailSizes"];

    static SnapshotSettings Run(SnapshotSettings s)
    {
        ThemeBootstrap.Run(s, StepNames.Select(Step).ToArray());
        return s;
    }

    static SettingKey<float> LegacyWidth(string scope) => new("detail.rail." + scope + ".width", 0f);

    static SettingKey<bool> LegacyCollapsed(string scope) => new("detail.rail." + scope + ".collapsed", false);

    public void Dispose() => ThemeVerbs.Unregister(DetailVerbIds.ClearRememberedSizes);

    // ── the steps ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_steps_read_the_legacy_keys_and_write_the_options_and_state_keys()
    {
        var layout = Step("DetailPageLayout");
        Assert.Equal(new[] { "detail.page.layout" }, layout.ReadsLegacyKeys);
        Assert.Equal(new[] { "theme.classic.structure.pageLayout" }, layout.WritesKeys);
        Assert.Equal(ThemeOptionStore.SettingName(ClassicTheme.Id, OptionLayer.Structure, ClassicStructure.PageLayout.Id), layout.WritesKeys[0]);

        var uniform = Step("DetailRailUniform");
        Assert.Equal(new[] { "detail.rail.uniform" }, uniform.ReadsLegacyKeys);
        Assert.Equal(new[] { "theme.classic.structure.railUniform" }, uniform.WritesKeys);
        Assert.Equal(ThemeOptionStore.SettingName(ClassicTheme.Id, OptionLayer.Structure, ClassicStructure.RailUniform.Id), uniform.WritesKeys[0]);

        // The ten sizes: every scope's width and collapsed flag, each to the state key `DetailRailPrefs` reads.
        var sizes = Step("DetailRailSizes");
        Assert.Equal(10, sizes.ReadsLegacyKeys.Length);
        Assert.Equal(10, sizes.WritesKeys.Length);
        foreach (string scope in Scopes)
        {
            Assert.Contains("detail.rail." + scope + ".width", sizes.ReadsLegacyKeys);
            Assert.Contains("detail.rail." + scope + ".collapsed", sizes.ReadsLegacyKeys);
        }
        foreach (var scope in new[] { RailScope.Album, RailScope.Playlist, RailScope.Liked, RailScope.Show, RailScope.Uniform })
        {
            Assert.Contains(ThemeState.SettingName(ClassicTheme.Id, DetailRailPrefs.WidthKey(scope).Id), sizes.WritesKeys);
            Assert.Contains(ThemeState.SettingName(ClassicTheme.Id, DetailRailPrefs.CollapsedKey(scope).Id), sizes.WritesKeys);
        }
    }

    [Fact]
    public void The_shipped_steps_stay_order_independent()
        => Assert.Null(ThemeBootstrap.Disjointness(ThemeBootstrap.Steps));

    [Theory]
    [InlineData(0, DetailPageLayout.Automatic)]
    [InlineData(1, DetailPageLayout.Hero)]
    public void A_stored_page_layout_keeps_its_value(int stored, DetailPageLayout expected)
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, stored);
        Run(s);
        Assert.Equal(stored, s.Get(Layout));
        Assert.Equal(stored, s.Get(LegacyLayout));   // the legacy key stays as inert data
        Assert.Equal(expected, ThemeOptionStore.Load(ClassicTheme.Create(), s).Structure.Get(ClassicStructure.PageLayout.Key));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(9)]
    public void An_unknown_page_layout_is_not_copied_so_the_default_applies(int stored)
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, stored);
        Run(s);
        Assert.False(s.Has("theme.classic.structure.pageLayout"));
        Assert.Equal(DetailPageLayout.Automatic, ThemeOptionStore.Load(ClassicTheme.Create(), s).Structure.Get(ClassicStructure.PageLayout.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stored_uniformity_keeps_its_value(bool stored)
    {
        var s = new SnapshotSettings();
        s.Set(LegacyUniform, stored);
        Run(s);
        Assert.Equal(stored, s.Get(Uniform));
        Assert.Equal(stored, ThemeOptionStore.Load(ClassicTheme.Create(), s).Structure.Get(ClassicStructure.RailUniform.Key));
    }

    [Fact]
    public void Nothing_stored_writes_nothing_so_the_defaults_apply()
    {
        var s = Run(new SnapshotSettings());
        Assert.False(s.Has("theme.classic.structure.pageLayout"));
        Assert.False(s.Has("theme.classic.structure.railUniform"));
        foreach (string scope in Scopes)
        {
            Assert.False(s.Has("theme.classic.state.rail." + scope + ".width"));
            Assert.False(s.Has("theme.classic.state.rail." + scope + ".collapsed"));
        }
    }

    [Fact]
    public void Every_remembered_rail_size_carries_over_to_the_classic_state_and_the_legacy_keys_stay()
    {
        var s = new SnapshotSettings();
        float width = 300f;
        foreach (string scope in Scopes)
        {
            s.Set(LegacyWidth(scope), width);
            s.Set(LegacyCollapsed(scope), scope is "liked" or "uniform");
            width += 10f;
        }
        Run(s);

        width = 300f;
        foreach (var scope in new[] { RailScope.Album, RailScope.Playlist, RailScope.Liked, RailScope.Show, RailScope.Uniform })
        {
            Assert.Equal(width, ThemeState.Get(DetailRailPrefs.WidthKey(scope), s, ClassicTheme.Id));
            Assert.Equal(scope is RailScope.Liked or RailScope.Uniform, ThemeState.Get(DetailRailPrefs.CollapsedKey(scope), s, ClassicTheme.Id));
            width += 10f;
        }
        Assert.Equal(300f, s.Get(LegacyWidth("album")));
        Assert.True(s.Get(LegacyCollapsed("liked")));
    }

    [Fact]
    public void A_remembered_width_outside_the_grips_range_is_clamped_on_read_as_the_old_reader_did()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyWidth("album"), 5000f);
        s.Set(LegacyWidth("show"), 12f);
        Run(s);
        Assert.Equal(Detail.RailPolicy.MaxWidth, ThemeState.Get(DetailRailPrefs.WidthKey(RailScope.Album), s, ClassicTheme.Id));
        Assert.Equal(Detail.RailPolicy.MinWidth, ThemeState.Get(DetailRailPrefs.WidthKey(RailScope.Show), s, ClassicTheme.Id));
    }

    [Fact]
    public void A_scope_nobody_dragged_keeps_its_authored_default()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyWidth("album"), 333f);
        Run(s);
        Assert.Equal(Detail.RailPolicy.DefaultWidthFor(RailScope.Playlist), ThemeState.Get(DetailRailPrefs.WidthKey(RailScope.Playlist), s, ClassicTheme.Id));
        Assert.False(ThemeState.Get(DetailRailPrefs.CollapsedKey(RailScope.Album), s, ClassicTheme.Id));
    }

    [Fact]
    public void A_state_or_option_the_user_already_set_is_never_overwritten()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, 1);
        s.Set(Layout, 0);
        s.Set(LegacyUniform, true);
        s.Set(Uniform, false);
        s.Set(LegacyWidth("album"), 400f);
        ThemeState.Set(DetailRailPrefs.WidthKey(RailScope.Album), 200f, s, ClassicTheme.Id);
        Run(s);
        Assert.Equal(0, s.Get(Layout));
        Assert.False(s.Get(Uniform));
        Assert.Equal(200f, ThemeState.Get(DetailRailPrefs.WidthKey(RailScope.Album), s, ClassicTheme.Id));
    }

    [Fact]
    public void The_steps_run_once()
    {
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, 1);
        Assert.Equal(3, ThemeBootstrap.Run(s, StepNames.Select(Step).ToArray()));
        s.Set(Layout, 0);
        Assert.Equal(0, ThemeBootstrap.Run(s, StepNames.Select(Step).ToArray()));
        Assert.Equal(0, s.Get(Layout));
    }

    [Fact]
    public void A_migrated_hero_user_with_shared_rails_loads_both_and_the_policy_carries_them()
    {
        var theme = ClassicTheme.Create();
        var s = new SnapshotSettings();
        s.Set(LegacyLayout, 1);
        s.Set(LegacyUniform, true);
        Run(s);

        var values = ThemeOptionStore.Load(theme, s);
        Assert.Equal(DetailPageLayout.Hero, values.Structure.Get(ClassicStructure.PageLayout.Key));
        Assert.True(values.Structure.Get(ClassicStructure.RailUniform.Key));
        var detail = theme.Policies(values).Detail;
        Assert.Equal(DetailPageLayout.Hero, detail.PageLayout);
        Assert.True(detail.RailUniform);
    }

    // ── the options ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_options_are_a_cards_select_a_toggle_and_a_confirmed_action_in_the_track_pages_group()
    {
        var layout = ClassicStructure.PageLayout;
        Assert.Equal("pageLayout", layout.Id);
        Assert.Equal("structure.pageLayout", ClassicStructure.PageLayoutQualifiedId);
        Assert.Equal(DetailPageLayout.Automatic, layout.Default);
        Assert.Equal(PickerStyle.Cards, layout.Picker);
        Assert.Equal(new[] { DetailPageLayout.Automatic, DetailPageLayout.Hero }, layout.Choices.Select(c => c.Value));
        Assert.Equal(Strings.Settings.ThemeOptions.Group.TrackPages, layout.Group);
        Assert.Same(layout, new ClassicStructure().Options.Find("pageLayout"));

        var uniform = ClassicStructure.RailUniform;
        Assert.False(uniform.Default);
        Assert.Equal(Strings.Settings.ThemeOptions.Group.TrackPages, uniform.Group);
        Assert.Same(uniform, new ClassicStructure().Options.Find("railUniform"));

        var clear = ClassicStructure.ClearRememberedSizes;
        Assert.Equal(DetailVerbIds.ClearRememberedSizes, clear.VerbId);
        Assert.NotNull(clear.Confirm);
        Assert.False(clear.Command);   // a destructive action asks first, and the palette has no dialog
        Assert.Same(clear, new ClassicStructure().Options.Find("clearRememberedSizes"));
        Assert.DoesNotContain(new ClassicStructure().LegacyRows, r => r.RowId is "pageLayout" or "railUniform" or "railReset");
    }

    static string[] RowIds(SnapshotSettings s)
    {
        var theme = ClassicTheme.Create();
        var values = ThemeOptionStore.Load(theme, s);
        return ThemeOptionsSection.Build(theme, values, developerMode: false).Rows
            .Select(r => r.QualifiedId).Where(id => id is "structure.pageLayout" or "structure.railUniform" or "structure.clearRememberedSizes").ToArray();
    }

    [Fact]
    public void The_automatic_layout_with_per_kind_rails_shows_all_three_rows()
        => Assert.Equal(new[] { "structure.pageLayout", "structure.railUniform", "structure.clearRememberedSizes" }, RowIds(new SnapshotSettings()));

    [Fact]
    public void The_hero_layout_shows_only_the_layout_row_because_it_composes_no_rail()
    {
        var s = new SnapshotSettings();
        s.Set(Layout, (int)DetailPageLayout.Hero);
        Assert.Equal(new[] { "structure.pageLayout" }, RowIds(s));
    }

    [Fact]
    public void Shared_rails_compose_the_reset_away()
    {
        var s = new SnapshotSettings();
        s.Set(Uniform, true);
        Assert.Equal(new[] { "structure.pageLayout", "structure.railUniform" }, RowIds(s));   // composed AWAY, not disabled
    }

    [Fact]
    public void The_reset_row_follows_the_verbs_gate_and_asks_before_it_runs()
    {
        var theme = ClassicTheme.Create();
        var s = new SnapshotSettings();
        bool customized = false;
        int runs = 0;
        ThemeVerbs.Register(DetailVerbIds.ClearRememberedSizes, () => { runs++; return true; }, enabled: () => customized);

        var greyed = ThemeOptionsSection.Build(theme, ThemeOptionStore.Load(theme, s), developerMode: false).Rows.Single(r => r.QualifiedId == "structure.clearRememberedSizes");
        Assert.False(greyed.Enabled);   // a destructive-looking no-op is never offered
        Assert.True(greyed.Row.SilentWhenDisabled);

        customized = true;
        var live = ThemeOptionsSection.Build(theme, ThemeOptionStore.Load(theme, s), developerMode: false).Rows.Single(r => r.QualifiedId == "structure.clearRememberedSizes");
        Assert.True(live.Enabled);
        Assert.Equal(Strings.Settings.Appearance.RailResetConfirmTitle, live.Row.Confirm!.TitleKey);
        Assert.Equal(Strings.Settings.Appearance.RailResetAction, live.Row.Confirm.PrimaryKey);
        Assert.Equal(0, runs);   // nothing runs until the page's confirm is accepted
        Assert.True(live.Row.Writer.Run!());
        Assert.Equal(1, runs);
    }

    [Fact]
    public void An_unregistered_verb_greys_the_reset()
    {
        ThemeVerbs.Unregister(DetailVerbIds.ClearRememberedSizes);
        var theme = ClassicTheme.Create();
        var row = ThemeOptionsSection.Build(theme, ThemeOptionStore.Load(theme, new SnapshotSettings()), developerMode: false).Rows.Single(r => r.QualifiedId == "structure.clearRememberedSizes");
        Assert.False(row.Enabled);
    }

    // ── the policy ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_default_combination_is_the_bundle_and_the_bundle_carries_the_default_detail_policy()
    {
        Assert.Same(ClassicPolicies.Bundle, ClassicPolicies.For(SidebarLayoutId.Classic, SidebarDensity.Default));
        Assert.Equal(DetailPolicy.Default, ClassicPolicies.Bundle.Detail);
        Assert.Equal(DetailPolicy.Default, ThemePolicies.Default.Detail);
    }

    [Fact]
    public void Each_combination_has_one_instance_that_carries_every_choice()
    {
        foreach (var layout in new[] { SidebarLayoutId.Classic, SidebarLayoutId.Library })
            foreach (var density in new[] { SidebarDensity.Default, SidebarDensity.Compact })
                foreach (bool hideArt in new[] { false, true })
                    foreach (var page in new[] { DetailPageLayout.Automatic, DetailPageLayout.Hero })
                        foreach (bool uniform in new[] { false, true })
                        {
                            var policies = ClassicPolicies.For(layout, density, hideArt, page, uniform);
                            Assert.Same(policies, ClassicPolicies.For(layout, density, hideArt, page, uniform));   // republishing is not a change
                            Assert.Equal(layout, policies.NavPane.Layout);
                            Assert.Equal(density, policies.NavPane.Density);
                            Assert.Equal(!hideArt, policies.Lists.TrackArtwork);
                            Assert.Equal(new DetailPolicy(page, uniform), policies.Detail);
                            Assert.Same(ClassicPolicies.Bundle.Tiers, policies.Tiers);
                            Assert.Empty(policies.Validate());
                        }
    }

    [Fact]
    public void The_structure_publishes_its_two_choices_to_the_core()
    {
        var structure = new ClassicStructure();
        var chosen = structure.Policies(OptionValues.Empty.With("pageLayout", DetailPageLayout.Hero).With("railUniform", true), StyleMetrics.Unset);
        Assert.Equal(new DetailPolicy(DetailPageLayout.Hero, RailUniform: true), chosen.Detail);
        Assert.Equal(DetailPolicy.Default, structure.Policies(OptionValues.Empty, StyleMetrics.Unset).Detail);
    }
}
