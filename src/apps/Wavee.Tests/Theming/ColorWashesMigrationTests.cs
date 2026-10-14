// ── Wavee.Tests/Theming/ColorWashesMigrationTests.cs — M6: the colour-washes switch became Classic's Surfaces option ─────
//
// `appearance.colorWashes.enabled` (a bool, default on) is copied by the "ColorWashes" bootstrap step to
// `theme.classic.structure.surfaces` (the SurfaceWash int: Off 0, Subtle 1), so a user who turned the washes off keeps them off.

using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ColorWashesMigrationTests
{
    static readonly SettingKey<bool> Legacy = new("appearance.colorWashes.enabled", true);
    static readonly SettingKey<int> Surfaces = new("theme.classic.structure.surfaces", 1);

    static ThemeBootstrapStep Step() => Assert.Single(ThemeBootstrap.Steps, s => s.Name == "ColorWashes");

    static SnapshotSettings Run(SnapshotSettings s)
    {
        ThemeBootstrap.Run(s, [Step()]);
        return s;
    }

    [Fact]
    public void The_step_reads_the_switch_and_writes_the_options_key()
    {
        var step = Step();
        Assert.Equal(new[] { "appearance.colorWashes.enabled" }, step.ReadsLegacyKeys);
        Assert.Equal(new[] { "theme.classic.structure.surfaces" }, step.WritesKeys);
        Assert.Equal(ThemeOptionStoreName(), step.WritesKeys[0]);
    }

    static string ThemeOptionStoreName() => ThemeOptionStore.SettingName(ClassicTheme.Id, OptionLayer.Structure, ClassicStructure.Surfaces.Id);

    [Fact]
    public void A_user_who_turned_the_washes_off_gets_Off()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy, false);
        Run(s);
        Assert.Equal((int)SurfaceWash.Off, s.Get(Surfaces));
        Assert.True(s.Has("theme.classic.structure.surfaces"));
        Assert.False(s.Get(Legacy));   // the legacy key stays as inert data
    }

    [Fact]
    public void A_user_who_kept_the_washes_on_gets_Subtle()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy, true);
        Run(s);
        Assert.Equal((int)SurfaceWash.Subtle, s.Get(Surfaces));
    }

    [Fact]
    public void Nothing_stored_writes_nothing_so_the_default_applies()
    {
        var s = Run(new SnapshotSettings());
        Assert.False(s.Has("theme.classic.structure.surfaces"));
    }

    [Fact]
    public void An_option_the_user_already_set_is_never_overwritten()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy, false);
        s.Set(Surfaces, (int)SurfaceWash.Subtle);
        Run(s);
        Assert.Equal((int)SurfaceWash.Subtle, s.Get(Surfaces));
    }

    [Fact]
    public void The_step_runs_once()
    {
        var s = new SnapshotSettings();
        s.Set(Legacy, false);
        Assert.Equal(1, ThemeBootstrap.Run(s, [Step()]));
        s.Set(Surfaces, (int)SurfaceWash.Subtle);
        Assert.Equal(0, ThemeBootstrap.Run(s, [Step()]));
        Assert.Equal((int)SurfaceWash.Subtle, s.Get(Surfaces));
    }

    [Fact]
    public void The_option_is_a_two_way_style_select_that_defaults_to_Subtle()
    {
        var option = ClassicStructure.Surfaces;
        Assert.Equal(SurfaceWash.Subtle, option.Default);
        Assert.Equal(PickerStyle.Toggle, option.Picker);
        Assert.Equal(new[] { SurfaceWash.Off, SurfaceWash.Subtle }, option.Choices.Select(c => c.Value));
        Assert.Same(option, new ClassicStructure().Options.Find("surfaces"));
        Assert.False(ClassicStructure.WashesOn(SurfaceWash.Off));
        Assert.True(ClassicStructure.WashesOn(SurfaceWash.Subtle));
    }
}
