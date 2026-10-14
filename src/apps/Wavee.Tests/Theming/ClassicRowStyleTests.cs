// ── Wavee.Tests/Theming/ClassicRowStyleTests.cs — the track-row skin (M2) as Classic's structure option ───────────────────────
//
// Row style is the structure's `rowStyle` option (Modern or Classic, the retired `detail.rowstyle` numbers). The core reads it as `ThemePolicies.Lists`
// (lane sets, header height, list key, chart metrics), the Classic views through `ClassicOptions`. The stored value of the retired switch carries over
// through the "TrackRowStyle" `ThemeBootstrap` step, so an existing user keeps their skin. `ThemeHost` is process state: the class is in the platform
// collection and resets it on both sides.

using Wavee.Themes.Classic;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ClassicRowStyleTests : IDisposable
{
    // The retired switch's stored key and Classic's option key, spelled out (the production names live in ThemeBootstrap only).
    static readonly SettingKey<int> LegacyRowStyle = new("detail.rowstyle", 0);
    static readonly SettingKey<int> RowStyleKey = new("theme.classic.structure.rowStyle", 0);

    readonly MemoryAppSettings _settings = new();

    public ClassicRowStyleTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    // ── the option ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Row_style_is_a_structure_cards_select_over_the_two_legacy_numbers_with_Modern_as_its_default()
    {
        var option = ClassicStructure.RowStyle;
        Assert.Equal(PickerStyle.Cards, option.Picker);
        Assert.Equal(TrackRowStyle.Modern, option.Default);
        Assert.Equal(new[] { TrackRowStyle.Modern, TrackRowStyle.Classic }, option.Choices.Select(c => c.Value));
        Assert.Equal(new[] { 0, 1 }, option.Choices.Select(c => (int)c.Value));   // the stored number IS the legacy value
        Assert.Equal("structure.rowStyle", ClassicStructure.RowStyleQualifiedId);
        Assert.Same(option, new ClassicStructure().Options.Find("rowStyle"));
        Assert.Equal(Strings.Settings.ThemeOptions.Group.TrackLists, option.Group);
        Assert.DoesNotContain(new ClassicStructure().LegacyRows, row => row.RowId == "trackListStyle");
    }

    // ── what the core reads ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_structure_publishes_the_row_style_beside_the_artwork_policy()
    {
        var structure = new ClassicStructure();
        var modern = structure.Policies(OptionValues.Empty, StyleMetrics.Unset);
        var classic = structure.Policies(OptionValues.Empty.With("rowStyle", TrackRowStyle.Classic), StyleMetrics.Unset);
        var classicHidden = structure.Policies(OptionValues.Empty.With("rowStyle", TrackRowStyle.Classic).With("hideTrackArt", true), StyleMetrics.Unset);

        Assert.False(modern.Lists.ClassicRows);
        Assert.True(classic.Lists.ClassicRows);
        Assert.True(classic.Lists.TrackArtwork);
        Assert.True(classicHidden.Lists.ClassicRows);
        Assert.False(classicHidden.Lists.TrackArtwork);
        Assert.Equal(modern with { Lists = new ListPolicy(true, TrackRowStyle.Classic) }, classic);
        Assert.Same(classic, structure.Policies(OptionValues.Empty.With("rowStyle", TrackRowStyle.Classic), StyleMetrics.Unset));   // republishing an unchanged option is not a change
        Assert.Empty(classic.Validate());
        Assert.Equal(TrackRowStyle.Modern, ListPolicy.Default.RowStyle);
    }

    [Fact]
    public void An_applied_row_style_reaches_the_policy_and_the_views_read_the_same_value()
    {
        ThemeHost.Install(ClassicTheme.Create());
        Assert.False(ThemeHost.Policies.Peek().Lists.ClassicRows);
        Assert.False(ClassicOptions.ClassicRows());

        _ = ThemeHost.Apply(new OptionChange("classic", ClassicStructure.RowStyleQualifiedId, TrackRowStyle.Classic));

        Assert.True(ThemeHost.Policies.Peek().Lists.ClassicRows);
        Assert.True(ShellPolicy.Now.Lists.ClassicRows);
        Assert.True(ClassicOptions.ClassicRows());
        Assert.Equal(TrackRowStyle.Classic, ClassicOptions.RowStyle());
        Assert.Equal((int)TrackRowStyle.Classic, _settings.Get(RowStyleKey));
    }

    [Fact]
    public void Resetting_the_options_puts_the_row_style_back()
    {
        ThemeHost.Install(ClassicTheme.Create());
        _ = ThemeHost.Apply(new OptionChange("classic", ClassicStructure.RowStyleQualifiedId, TrackRowStyle.Classic));

        ThemeHost.ResetOptions("classic");

        Assert.False(ThemeHost.Policies.Peek().Lists.ClassicRows);
        Assert.False(_settings.Has(RowStyleKey.Name));
    }

    // ── the stored value of the retired switch ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, TrackRowStyle.Modern)]
    [InlineData(1, TrackRowStyle.Classic)]
    public void An_existing_users_row_style_survives_the_first_launch_of_this_build(int stored, TrackRowStyle expected)
    {
        _settings.Set(LegacyRowStyle, stored);

        ThemeBootstrap.Run(_settings);
        ThemeHost.Install(ClassicTheme.Create());

        Assert.Equal(stored, _settings.Get(RowStyleKey));
        Assert.Equal(expected, ClassicOptions.RowStyle());
        Assert.Equal(expected == TrackRowStyle.Classic, ThemeHost.Policies.Peek().Lists.ClassicRows);
        Assert.Equal(stored, _settings.Get(LegacyRowStyle));   // the legacy key stays as inert data
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void A_stored_row_style_outside_the_two_values_is_not_copied_and_the_option_default_applies(int stored)
    {
        _settings.Set(LegacyRowStyle, stored);

        ThemeBootstrap.Run(_settings);
        ThemeHost.Install(ClassicTheme.Create());

        Assert.False(_settings.Has(RowStyleKey.Name));
        Assert.Equal(TrackRowStyle.Modern, ClassicOptions.RowStyle());
    }

    [Fact]
    public void A_fresh_install_stores_nothing_and_a_value_the_user_already_chose_in_the_new_build_wins()
    {
        ThemeBootstrap.Run(_settings);
        Assert.False(_settings.Has(RowStyleKey.Name));

        var chosen = new MemoryAppSettings();
        chosen.Set(LegacyRowStyle, 1);
        chosen.Set(RowStyleKey, 0);
        ThemeBootstrap.Run(chosen);
        Assert.Equal(0, chosen.Get(RowStyleKey));
    }

    [Fact]
    public void The_step_is_named_and_writes_exactly_the_options_key()
    {
        var step = ThemeBootstrap.Steps.Single(s => s.Name == "TrackRowStyle");
        Assert.Equal(new[] { "theme.classic.structure.rowStyle" }, step.WritesKeys);
        Assert.Equal(new[] { "detail.rowstyle" }, step.ReadsLegacyKeys);
        Assert.Null(ThemeBootstrap.Disjointness(ThemeBootstrap.Steps));
    }
}
