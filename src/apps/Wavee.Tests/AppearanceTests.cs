// ── Wavee.Tests/AppearanceTests.cs — the one owner of light / dark / system (Theming/Appearance.cs, PR C1) ──────────
//
// The four routines that used to apply a theme (the boot seed, the OS-colours listener, the flip verb and the Settings
// selector) are one `AppearanceModel` now. These tests drive the REAL model against a recorder of its ports: `Tok` is
// process-wide shared state and this assembly runs its facts in parallel, so nothing here touches the live token set,
// the registry or the settings store.

using FluentGpu.Dsl;
using Xunit;

namespace Wavee.Tests;

public class AppearanceTests
{
    /// <summary>Records every effect the model asks of the process, in order, with the OS answers set by the test.</summary>
    sealed class RecordingPorts : IAppearancePorts
    {
        public readonly List<string> Log = new();
        public ThemeKind Current = ThemeKind.Dark;
        public int Stored;
        public bool OsLight;
        public int OsReads;
        public int AccentSeeds, PlainFallbackSeeds, OverrideReleases;

        public ThemeKind CurrentKind => Current;

        public int StoredMode
        {
            get => Stored;
            set { Stored = value; Log.Add("store:" + value); }
        }

        public bool SystemUsesLightTheme()
        {
            OsReads++;
            return OsLight;
        }

        public void ApplyKind(ThemeKind kind)
        {
            Current = kind;
            Log.Add("kind:" + kind);
        }

        /// <summary>The kinds whose change moves more than colours (a per-kind window material): a test sets it.</summary>
        public bool Structural;

        public bool KindChangeIsStructural(ThemeKind kind) => Structural;

        public void ApplyKindInTransaction(ThemeKind kind)
        {
            Current = kind;
            Log.Add("transaction:" + kind);
        }

        public void ApplySystemAccent(bool allowPlainFallback)
        {
            AccentSeeds++;
            if (allowPlainFallback) PlainFallbackSeeds++;
            Log.Add("accent:" + (allowPlainFallback ? "ramp-or-plain" : "ramp"));
        }

        public void ReleaseAccentOverride()
        {
            OverrideReleases++;
            Log.Add("release");
        }
    }

    static (AppearanceModel Model, RecordingPorts Ports, List<float> Fades) Make(
        ThemeKind current = ThemeKind.Dark, int stored = 0, bool osLight = false, bool bindTransition = true)
    {
        var ports = new RecordingPorts { Current = current, Stored = stored, OsLight = osLight };
        var model = new AppearanceModel(ports);
        var fades = new List<float>();
        if (bindTransition) model.BindTransition(fades.Add);
        return (model, ports, fades);
    }

    // ── mode -> kind ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, ThemeMode.System)]
    [InlineData(1, ThemeMode.Light)]
    [InlineData(2, ThemeMode.Dark)]
    [InlineData(3, ThemeMode.System)]    // a value this build does not define follows the OS, as the boot always did
    [InlineData(-1, ThemeMode.System)]
    public void The_stored_int_reads_as_a_mode(int stored, ThemeMode expected)
        => Assert.Equal(expected, Appearance.ModeOf(stored));

    [Theory]
    [InlineData(ThemeMode.Light, false, ThemeKind.Light)]
    [InlineData(ThemeMode.Light, true, ThemeKind.Light)]
    [InlineData(ThemeMode.Dark, false, ThemeKind.Dark)]
    [InlineData(ThemeMode.Dark, true, ThemeKind.Dark)]
    [InlineData(ThemeMode.System, true, ThemeKind.Light)]    // System is the OS theme
    [InlineData(ThemeMode.System, false, ThemeKind.Dark)]
    public void An_explicit_mode_ignores_the_OS_and_System_follows_it(ThemeMode mode, bool osLight, ThemeKind expected)
        => Assert.Equal(expected, Appearance.KindFor(mode, osLight));

    [Theory]
    [InlineData(ThemeMode.System, true, ThemeKind.Light)]
    [InlineData(ThemeMode.System, false, ThemeKind.Dark)]
    [InlineData(ThemeMode.Light, false, ThemeKind.Light)]
    [InlineData(ThemeMode.Dark, true, ThemeKind.Dark)]
    public void Seeding_a_mode_shows_the_kind_that_mode_resolves_to(ThemeMode mode, bool osLight, ThemeKind expected)
    {
        var (model, ports, _) = Make(current: ThemeKind.Light, osLight: osLight);
        model.Seed(mode);
        Assert.Equal(expected, ports.Current);
        Assert.Equal(expected, model.Kind);
    }

    [Theory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void An_explicit_mode_never_reads_the_OS_theme(ThemeMode mode)
    {
        var (model, ports, _) = Make();
        model.Seed(mode);
        model.SetMode(mode);
        Assert.Equal(0, ports.OsReads);
    }

    // ── the accent ramp is seeded once ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_OS_accent_is_seeded_once_at_boot_with_the_plain_colour_as_the_fallback()
    {
        var (model, ports, _) = Make();
        model.Seed(ThemeMode.System);
        Assert.Equal(1, ports.AccentSeeds);
        Assert.Equal(1, ports.PlainFallbackSeeds);
    }

    [Fact]
    public void Light_dark_and_the_flip_never_re_seed_the_accent()
    {
        var (model, ports, _) = Make();
        model.Seed(ThemeMode.Dark);
        Assert.Equal(1, ports.AccentSeeds);

        model.SetMode(ThemeMode.Light);
        model.SetMode(ThemeMode.Dark);
        model.ToggleMode();
        model.ToggleMode();

        Assert.Equal(1, ports.AccentSeeds);   // still the boot's one
    }

    [Fact]
    public void Choosing_System_re_reads_the_OS_theme_and_accent_now()
    {
        var (model, ports, fades) = Make(current: ThemeKind.Dark, stored: 2, osLight: true);
        model.Seed(ThemeMode.Dark);
        int atBoot = ports.AccentSeeds;

        model.SetMode(ThemeMode.System);

        Assert.Equal(ThemeKind.Light, ports.Current);
        Assert.Equal(atBoot + 1, ports.AccentSeeds);
        Assert.Equal(0, ports.Stored);
        Assert.Equal(new[] { AppearanceModel.TransitionMs }, fades);
    }

    // ── the engine's accent override ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_window_coming_up_releases_the_engine_accent_override_and_nothing_else()
    {
        var (model, ports, fades) = Make();
        model.OnWindowCreated();
        Assert.Equal(new[] { "release" }, ports.Log);   // no accent seed, no OS read, no kind change, no cross-fade
        Assert.Equal(0, ports.OsReads);
        Assert.Empty(fades);
    }

    // ── the fixed-kind seed (the shell test host) ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void A_fixed_kind_seed_reads_nothing_from_the_OS_and_keeps_the_token_default_accent(ThemeKind kind)
    {
        var (model, ports, _) = Make(current: kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark, osLight: true);
        model.Seed(kind);
        Assert.Equal(kind, ports.Current);
        Assert.Equal(0, ports.OsReads);
        Assert.Equal(0, ports.AccentSeeds);
        Assert.Equal(0, ports.Stored);   // the persisted mode is neither read nor written
    }

    // ── the live verbs ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ThemeMode.Light, ThemeKind.Light, 1)]
    [InlineData(ThemeMode.Dark, ThemeKind.Dark, 2)]
    public void Setting_an_explicit_mode_swaps_the_palette_persists_it_and_asks_for_the_cross_fade(
        ThemeMode mode, ThemeKind kind, int stored)
    {
        var (model, ports, fades) = Make(current: kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
        model.SetMode(mode);
        Assert.Equal(kind, ports.Current);
        Assert.Equal(stored, ports.Stored);
        Assert.Equal(mode, model.Mode);
        Assert.Equal(new[] { 250f }, fades);
        // The order is the contract: the palette first, the preference after it, the cross-fade last.
        Assert.Equal(new[] { "kind:" + kind, "store:" + stored }, ports.Log);
    }

    [Theory]
    [InlineData(ThemeKind.Dark, ThemeKind.Light, 1)]
    [InlineData(ThemeKind.Light, ThemeKind.Dark, 2)]
    public void The_flip_goes_to_the_other_kind_and_persists_the_explicit_mode(ThemeKind from, ThemeKind to, int stored)
    {
        var (model, ports, fades) = Make(current: from, stored: 0);
        model.ToggleMode();
        Assert.Equal(to, ports.Current);
        Assert.Equal(stored, ports.Stored);
        Assert.Equal(new[] { AppearanceModel.TransitionMs }, fades);
        Assert.Equal(0, ports.AccentSeeds);
        Assert.Equal(0, ports.OsReads);
    }

    [Fact]
    public void A_live_change_without_a_bound_host_still_applies_and_persists()
    {
        var (model, ports, _) = Make(bindTransition: false);
        model.SetMode(ThemeMode.Light);
        model.ToggleMode();
        Assert.Equal(ThemeKind.Dark, ports.Current);
        Assert.Equal(2, ports.Stored);
    }

    // ── the OS colours changed ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_OS_colour_change_always_follows_the_accent_ramp_without_the_plain_fallback()
    {
        var (model, ports, _) = Make(current: ThemeKind.Dark, stored: 2);   // the user pinned Dark
        model.OnSystemColorsChanged();
        Assert.Equal(1, ports.AccentSeeds);
        Assert.Equal(0, ports.PlainFallbackSeeds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_pinned_mode_keeps_its_kind_when_the_OS_theme_flips(int stored)
    {
        var (model, ports, fades) = Make(current: stored == 2 ? ThemeKind.Dark : ThemeKind.Light, stored: stored, osLight: stored == 2);
        model.OnSystemColorsChanged();
        Assert.Equal(stored == 2 ? ThemeKind.Dark : ThemeKind.Light, ports.Current);
        Assert.Empty(fades);
        Assert.Equal(0, ports.OsReads);
    }

    [Fact]
    public void Following_the_system_flips_the_kind_with_the_cross_fade_when_the_OS_theme_changes()
    {
        var (model, ports, fades) = Make(current: ThemeKind.Dark, stored: 0, osLight: true);
        model.OnSystemColorsChanged();
        Assert.Equal(ThemeKind.Light, ports.Current);
        Assert.Equal(0, ports.Stored);   // following the system never persists an explicit mode
        Assert.Equal(new[] { AppearanceModel.TransitionMs }, fades);
    }

    [Fact]
    public void Following_the_system_does_nothing_to_the_kind_when_the_OS_theme_did_not_change()
    {
        var (model, ports, fades) = Make(current: ThemeKind.Light, stored: 0, osLight: true);
        model.OnSystemColorsChanged();
        Assert.Equal(ThemeKind.Light, ports.Current);
        Assert.DoesNotContain(ports.Log, entry => entry.StartsWith("kind:", StringComparison.Ordinal));
        Assert.Empty(fades);
        Assert.Equal(1, ports.AccentSeeds);   // the accent still follows
    }
    // ── a kind that changes more than colours (A4.2) ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_kind_change_that_moves_more_than_colours_goes_through_the_transaction_with_no_live_cross_fade()
    {
        var (model, ports, fades) = Make(current: ThemeKind.Dark, stored: 2);
        ports.Structural = true;
        model.SetMode(ThemeMode.Light);
        model.ToggleMode();
        Assert.Equal(new[] { "transaction:Light", "store:1", "transaction:Dark", "store:2" }, ports.Log);
        Assert.Empty(fades);   // the transaction dissolves on its own
    }

    [Fact]
    public void A_colour_only_kind_change_stays_on_the_live_path()
    {
        var (model, ports, fades) = Make(current: ThemeKind.Dark, stored: 2);
        model.ToggleMode();
        Assert.Equal(new[] { "kind:Light", "store:1" }, ports.Log);
        Assert.Equal(new[] { AppearanceModel.TransitionMs }, fades);
    }
}
