// ── Wavee.Tests/Theming/OsAccentTests.cs — the OS accent is an input of the style, never an engine override (P5 ZA) ────────────────
//
// `Appearance` used to push the OS accent with `Tok.SetAccent`, which overrides `ThemeDefinition.Accent`: under a style with its own
// accent (a Zune swatch) the engine's controls drew the OS accent. Now the OS accent is fed to `ThemeHost.OsAccent`, each style's
// `Definition(options, osAccent)` decides what it does with it, and the engine follows the definition. These facts pin the pure half:
//
//   · what `ThemeHost.FeedOsAccent` assigns (the exact ramp, the derived plain colour only with the fallback, nothing otherwise);
//   · Classic keeps following the Windows accent, now through its definition (the WinUI default is today's one instance);
//   · Zune ignores the OS accent unless "use the windows accent" is on;
//   · the active definition (what the appearance seed installs) carries the fed accent.
//
// NO TOKEN CHANGES HERE: nothing installs a definition (`Tok` is process-wide shared state). The mounted half, on the live token set,
// is Wavee.ShellTests/OsAccentMountedTests.cs. `ThemeHost` is process state, so the class is in the platform collection.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class OsAccentTests : IDisposable
{
    static readonly ColorF Red = ColorF.FromRgba(0xE0, 0x40, 0x40);
    static readonly AccentRamp RedRamp = AccentRamp.Derive(Red);
    static readonly AccentRamp GreenRamp = AccentRamp.Derive(ColorF.FromRgba(0x10, 0x9D, 0x59));

    readonly MemoryAppSettings _settings = new();

    public OsAccentTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    // ── what the OS answer becomes ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_OS_accent_defaults_to_the_WinUI_ramp()
        => Assert.Equal(AccentRamp.FluentDefault, ThemeHost.OsAccent);

    [Fact]
    public void The_exact_OS_ramp_is_assigned_and_the_change_is_reported_once()
    {
        Assert.True(ThemeHost.FeedOsAccent(RedRamp, null, allowPlainFallback: false));
        Assert.Equal(RedRamp, ThemeHost.OsAccent);
        Assert.False(ThemeHost.FeedOsAccent(RedRamp, null, allowPlainFallback: false));   // the same answer is not a change
        Assert.True(ThemeHost.FeedOsAccent(GreenRamp, Red, allowPlainFallback: true));    // the ramp wins over the plain colour
        Assert.Equal(GreenRamp, ThemeHost.OsAccent);
    }

    [Fact]
    public void The_plain_colour_is_derived_only_when_the_fallback_is_allowed()
    {
        Assert.False(ThemeHost.FeedOsAccent(null, Red, allowPlainFallback: false));       // the OS listener never falls back
        Assert.Equal(AccentRamp.FluentDefault, ThemeHost.OsAccent);

        Assert.True(ThemeHost.FeedOsAccent(null, Red, allowPlainFallback: true));         // the boot and the Settings selector do
        Assert.Equal(AccentRamp.Derive(Red), ThemeHost.OsAccent);
    }

    [Fact]
    public void An_OS_that_answers_nothing_leaves_the_previous_accent_standing()
    {
        ThemeHost.FeedOsAccent(RedRamp, null, allowPlainFallback: true);
        Assert.False(ThemeHost.FeedOsAccent(null, null, allowPlainFallback: true));
        Assert.Equal(RedRamp, ThemeHost.OsAccent);
    }

    // ── Classic follows the Windows accent, through its definition ──────────────────────────────────────────────────────────

    [Fact]
    public void Classic_under_the_WinUI_default_accent_is_todays_definition_and_one_instance()
    {
        var style = new ClassicStyle();
        var definition = style.Definition(OptionValues.Empty, AccentRamp.FluentDefault);
        Assert.Equal(Design.Definition with { AppRoles = ClassicRoles.Table }, definition);
        Assert.Same(definition, style.Definition(OptionValues.Empty, AccentRamp.FluentDefault));
    }

    [Fact]
    public void Classic_carries_the_OS_accent_as_its_definition_accent_and_nothing_else_changes()
    {
        var style = new ClassicStyle();
        var baseline = style.Definition(OptionValues.Empty, AccentRamp.FluentDefault);
        var red = style.Definition(OptionValues.Empty, RedRamp);

        Assert.Equal(RedRamp, red.Accent);
        Assert.Equal(baseline with { Accent = RedRamp }, red);
        Assert.Same(red, style.Definition(OptionValues.Empty, RedRamp));            // one instance per ramp
        Assert.Same(baseline, style.Definition(OptionValues.Empty, AccentRamp.FluentDefault));   // and the default one is still the one instance
        Assert.True(baseline.DiffersOnlyInColours(red));                              // an OS accent change is the live cross-fade, never a transaction
    }

    [Fact]
    public void The_active_Classic_definition_follows_each_OS_accent_that_is_fed()
    {
        ThemeHost.Install(ClassicTheme.Create());
        Assert.Equal(AccentRamp.FluentDefault, ThemeHost.ActiveDefinition!.Accent);

        ThemeHost.FeedOsAccent(RedRamp, null, allowPlainFallback: false);
        Assert.Equal(RedRamp, ThemeHost.ActiveDefinition!.Accent);                    // what Design.SeedPalette installs

        ThemeHost.FeedOsAccent(GreenRamp, null, allowPlainFallback: false);
        Assert.Equal(GreenRamp, ThemeHost.ActiveDefinition!.Accent);
    }

    // ── Zune decides for itself ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_ignores_the_OS_accent_unless_use_the_windows_accent_is_on()
    {
        var style = ZuneStyle.Create();
        var swatch = ZuneAccent.RampOf(ZuneAccent.Pink);

        Assert.Equal(swatch, style.Definition(OptionValues.Empty, AccentRamp.FluentDefault).Accent);
        Assert.Equal(swatch, style.Definition(OptionValues.Empty, RedRamp).Accent);
        Assert.Equal(swatch, style.Definition(OptionValues.Empty, GreenRamp).Accent);
        Assert.Same(style.Definition(OptionValues.Empty, RedRamp), style.Definition(OptionValues.Empty, GreenRamp));   // the OS accent is not even an input

        var follows = OptionValues.Empty.With("useWindowsAccent", true);
        Assert.Equal(RedRamp, style.Definition(follows, RedRamp).Accent);
        Assert.Equal(GreenRamp, style.Definition(follows, GreenRamp).Accent);
    }

    [Fact]
    public void The_Zune_composition_resolves_the_swatch_whatever_OS_accent_is_fed()
    {
        var zune = ZuneTheme.Create();
        var values = ThemeOptionStore.Load(zune, new MemoryAppSettings());
        var swatch = ZuneAccent.RampOf(ZuneAccent.Pink);

        Assert.Equal(swatch, zune.Definition(values, ThemeHost.OsAccent).Accent);
        ThemeHost.FeedOsAccent(RedRamp, null, allowPlainFallback: false);
        Assert.Equal(swatch, zune.Definition(values, ThemeHost.OsAccent).Accent);
    }
}
