// ── Wavee.Tests/Theming/ClassicDensityAndArtTests.cs — row density (M3) and hide track art (M4) as Classic options ──────────
//
// Density is the style's `density` option (a metrics preset the core's track table sizes its rows from), hide track art the structure's `hideTrackArt`
// toggle (the core reads it as `ThemePolicies.Lists`). The stored values of the retired switches carry over through two `ThemeBootstrap` steps and an
// existing user keeps both choices. `ThemeHost` is process state: the class is in the platform collection and resets it on both sides.

using Wavee.Themes.Classic;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ClassicDensityAndArtTests : IDisposable
{
    // The retired switches' stored keys and Classic's option keys, spelled out (the production names live in ThemeBootstrap only).
    static readonly SettingKey<int> LegacyDensity = new("detail.rowdensity", 1);
    static readonly SettingKey<bool> LegacyHideArt = new("appearance.trackArtwork.hidden", false);
    static readonly SettingKey<int> DensityKey = new("theme.classic.style.density", 1);
    static readonly SettingKey<bool> HideArtKey = new("theme.classic.structure.hideTrackArt", false);

    readonly MemoryAppSettings _settings = new();

    public ClassicDensityAndArtTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    // ── the options ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Density_is_a_style_cards_select_over_the_four_legacy_rungs_with_Default_as_its_default()
    {
        var option = ClassicDensity.Option;
        Assert.Equal(PickerStyle.Cards, option.Picker);
        Assert.Equal(RowDensity.Default, option.Default);
        Assert.Equal(new[] { RowDensity.Compact, RowDensity.Default, RowDensity.Cozy, RowDensity.Comfortable }, option.Choices.Select(c => c.Value));
        Assert.Equal(new[] { 0, 1, 2, 3 }, option.Choices.Select(c => (int)c.Value));   // the stored number IS the legacy rung
        Assert.Equal("style.density", ClassicDensity.QualifiedId);
        Assert.Same(option, new ClassicStyle().Options.Find("density"));
    }

    [Fact]
    public void Hide_track_art_is_a_structure_toggle_that_defaults_to_shown()
    {
        Assert.False(ClassicStructure.HideTrackArt.Default);
        Assert.Equal("structure.hideTrackArt", ClassicStructure.HideTrackArtQualifiedId);
        Assert.Same(ClassicStructure.HideTrackArt, new ClassicStructure().Options.Find("hideTrackArt"));
        Assert.DoesNotContain(new ClassicStructure().LegacyRows, row => row.RowId is "rowDensity" or "hideTrackArtwork");
    }

    // ── what the core reads ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RowDensity.Compact, 0, true)]
    [InlineData(RowDensity.Default, 1, true)]
    [InlineData(RowDensity.Cozy, 2, false)]
    [InlineData(RowDensity.Comfortable, 3, false)]
    public void The_style_publishes_the_track_rows_of_its_density(RowDensity density, int rung, bool narrowEpisode)
    {
        var rows = new ClassicStyle().Metrics(OptionValues.Empty.With("density", density)).TrackRows;
        Assert.Equal(ClassicDensity.Rows(density), rows);
        Assert.Equal(rung, rows.Rung);
        Assert.Equal(narrowEpisode, rows.NarrowEpisode);
        Assert.False(rows.IsUnset);
    }

    [Fact]
    public void An_option_set_that_names_no_density_publishes_the_Default_rung_the_core_falls_back_to()
    {
        Assert.Equal(TrackRowMetrics.Standard, new ClassicStyle().Metrics(OptionValues.Empty).TrackRows);
        Assert.True(StyleMetrics.Unset.TrackRows.IsUnset);
    }

    [Fact]
    public void The_structure_publishes_the_artwork_policy_and_the_shown_bundle_is_the_baseline_one()
    {
        var structure = new ClassicStructure();
        var shown = structure.Policies(OptionValues.Empty, StyleMetrics.Unset);
        var hidden = structure.Policies(OptionValues.Empty.With("hideTrackArt", true), StyleMetrics.Unset);

        Assert.True(shown.Lists.TrackArtwork);
        Assert.False(hidden.Lists.TrackArtwork);
        Assert.Equal(ListPolicy.Default, ThemePolicies.Default.Lists);
        // Only the window tiers, the overlay layouts, the detail layouts (TC5) and the per-tier rules of the panel and the bar (TC2) differ from the baseline.
        Assert.Equal(ThemePolicies.Default with
        {
            Tiers = shown.Tiers, Overlays = shown.Overlays, DetailTiers = shown.DetailTiers,
            Panel = ThemePolicies.Default.Panel with { Tiers = shown.Panel.Tiers }, PlayerBar = ThemePolicies.Default.PlayerBar with { Windows = shown.PlayerBar.Windows },
        }, shown);
        Assert.Equal(shown with { Lists = new ListPolicy(false) }, hidden);
        Assert.Empty(hidden.Validate());
    }

    [Fact]
    public void An_applied_density_reaches_the_facts_the_track_table_reads_and_the_core_helper_follows_it()
    {
        ThemeHost.Install(ClassicTheme.Create());
        Assert.Equal(TrackRowMetrics.Standard, Track.RowMetrics.Rows());

        _ = ThemeHost.Apply(new OptionChange("classic", ClassicDensity.QualifiedId, RowDensity.Comfortable));

        Assert.Equal(ClassicDensity.Rows(RowDensity.Comfortable), ThemeHost.Facts.Peek().Metrics.TrackRows);
        Assert.Equal(ClassicDensity.Rows(RowDensity.Comfortable), Track.RowMetrics.Rows());
        Assert.Equal((int)RowDensity.Comfortable, _settings.Get(DensityKey));
        Assert.Equal(RowDensity.Comfortable, ClassicDensity.Current(ThemeHost.Options));
    }

    [Fact]
    public void A_density_request_from_a_view_lands_in_apply()
    {
        ThemeHost.Install(ClassicTheme.Create());
        ClassicDensity.Request(ThemeHost.Options.RequestForwarder, RowDensity.Cozy);
        Assert.Equal(ClassicDensity.Rows(RowDensity.Cozy), ThemeHost.Facts.Peek().Metrics.TrackRows);
        Assert.Equal((int)RowDensity.Cozy, _settings.Get(DensityKey));
    }

    [Fact]
    public void An_applied_hide_art_reaches_the_policy_and_the_views_read_the_same_value()
    {
        ThemeHost.Install(ClassicTheme.Create());
        Assert.True(ThemeHost.Policies.Peek().Lists.TrackArtwork);
        Assert.False(ClassicOptions.HideTrackArt());

        _ = ThemeHost.Apply(new OptionChange("classic", ClassicStructure.HideTrackArtQualifiedId, true));

        Assert.False(ThemeHost.Policies.Peek().Lists.TrackArtwork);
        Assert.False(ShellPolicy.Now.Lists.TrackArtwork);
        Assert.True(ClassicOptions.HideTrackArt());
        Assert.True(_settings.Get(HideArtKey));
    }

    [Fact]
    public void Resetting_the_options_puts_density_and_artwork_back()
    {
        ThemeHost.Install(ClassicTheme.Create());
        _ = ThemeHost.Apply(new OptionChange("classic", ClassicDensity.QualifiedId, RowDensity.Compact));
        _ = ThemeHost.Apply(new OptionChange("classic", ClassicStructure.HideTrackArtQualifiedId, true));

        ThemeHost.ResetOptions("classic");

        Assert.Equal(TrackRowMetrics.Standard, ThemeHost.Facts.Peek().Metrics.TrackRows);
        Assert.True(ThemeHost.Policies.Peek().Lists.TrackArtwork);
        Assert.False(_settings.Has("theme.classic.style.density"));
        Assert.False(_settings.Has("theme.classic.structure.hideTrackArt"));
    }

    // ── the stored values of the retired switches ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, RowDensity.Compact)]
    [InlineData(1, RowDensity.Default)]
    [InlineData(2, RowDensity.Cozy)]
    [InlineData(3, RowDensity.Comfortable)]
    public void An_existing_users_density_survives_the_first_launch_of_this_build(int stored, RowDensity expected)
    {
        _settings.Set(LegacyDensity, stored);

        ThemeBootstrap.Run(_settings);
        ThemeHost.Install(ClassicTheme.Create());

        Assert.Equal(stored, _settings.Get(DensityKey));
        Assert.Equal(expected, ClassicDensity.Current(ThemeHost.Options));
        Assert.Equal(ClassicDensity.Rows(expected), ThemeHost.Facts.Peek().Metrics.TrackRows);
        Assert.Equal(stored, _settings.Get(LegacyDensity));   // the legacy key stays as inert data
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void A_stored_density_outside_the_ladder_is_not_copied_and_the_option_default_applies(int stored)
    {
        _settings.Set(LegacyDensity, stored);

        ThemeBootstrap.Run(_settings);
        ThemeHost.Install(ClassicTheme.Create());

        Assert.False(_settings.Has(DensityKey.Name));
        Assert.Equal(RowDensity.Default, ClassicDensity.Current(ThemeHost.Options));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_existing_users_hide_art_choice_survives_the_first_launch_of_this_build(bool stored)
    {
        _settings.Set(LegacyHideArt, stored);

        ThemeBootstrap.Run(_settings);
        ThemeHost.Install(ClassicTheme.Create());

        Assert.Equal(stored, _settings.Get(HideArtKey));
        Assert.Equal(stored, ClassicOptions.HideTrackArt());
        Assert.Equal(!stored, ThemeHost.Policies.Peek().Lists.TrackArtwork);
        Assert.Equal(stored, _settings.Get(LegacyHideArt));
    }

    [Fact]
    public void A_fresh_install_stores_nothing_and_a_value_the_user_already_chose_in_the_new_build_wins()
    {
        ThemeBootstrap.Run(_settings);
        Assert.False(_settings.Has(DensityKey.Name));
        Assert.False(_settings.Has(HideArtKey.Name));

        var chosen = new MemoryAppSettings();
        chosen.Set(LegacyDensity, 0);
        chosen.Set(LegacyHideArt, true);
        chosen.Set(DensityKey, 3);
        chosen.Set(HideArtKey, false);
        ThemeBootstrap.Run(chosen);
        Assert.Equal(3, chosen.Get(DensityKey));
        Assert.False(chosen.Get(HideArtKey));
    }

    [Fact]
    public void The_two_steps_are_named_and_write_exactly_the_options_keys()
    {
        var steps = ThemeBootstrap.Steps.ToDictionary(s => s.Name);
        Assert.Equal(new[] { "theme.classic.style.density" }, steps["TrackRowDensity"].WritesKeys);
        Assert.Equal(new[] { "detail.rowdensity" }, steps["TrackRowDensity"].ReadsLegacyKeys);
        Assert.Equal(new[] { "theme.classic.structure.hideTrackArt" }, steps["HideTrackArtwork"].WritesKeys);
        Assert.Equal(new[] { "appearance.trackArtwork.hidden" }, steps["HideTrackArtwork"].ReadsLegacyKeys);
        Assert.Null(ThemeBootstrap.Disjointness(ThemeBootstrap.Steps));
    }
}
