// ── Wavee.ShellTests/OsAccentMountedTests.cs — the OS accent reaches the engine through the active definition (P5 ZA) ──────────────
//
// On the live token set (`Tok`), after what `Appearance` does on an OS accent change and when the window comes up:
//
//   · Classic follows the Windows accent exactly as it did with the `Tok.SetAccent` override: every accent token the engine draws is the
//     same, in both kinds, and the engine's accent is now the definition's (no override is left set);
//   · Zune with its swatch draws the swatch whatever the OS says, even when the engine's boot override is still set (the window just
//     came up) and after an OS accent change;
//   · Zune with "use the windows accent" draws the OS accent, and the swatch again when it is turned off.
//
// Every fact puts the process back: OS accent default, no override, Classic alone.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Xunit;

namespace Wavee.ShellTests;

public sealed class OsAccentMountedTests(ShellHost host)
{
    static readonly AccentRamp Red = AccentRamp.Derive(ColorF.FromRgba(0xE0, 0x40, 0x40));
    static readonly AccentRamp Green = AccentRamp.Derive(ColorF.FromRgba(0x10, 0x9D, 0x59));

    /// <summary>The accent tokens the engine's controls and Classic's roles read, as one tuple.</summary>
    static (ColorF Default, ColorF Secondary, ColorF Tertiary, ColorF Subtle, ColorF Text) Accents()
        => (Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.AccentSubtle, Tok.AccentTextPrimary);

    /// <summary>The OS accent back to the default, no override, the active definition installed again.</summary>
    static void PutBack()
    {
        Tok.SetAccent((ColorF?)null);
        ThemeHost.OsAccent = AccentRamp.FluentDefault;
        Design.SeedPalette(Tok.Theme);
    }

    /// <summary>Zune active with ITS OWN defaults and nothing carried over: an earlier fact may have picked a swatch, turned the OS accent
    /// on or left an OS accent / engine override behind (all process state), and these facts assert the defaults. UI thread.</summary>
    static void ActivateZuneAtDefaults()
    {
        Tok.SetAccent((ColorF?)null);
        ThemeHost.OsAccent = AccentRamp.FluentDefault;
        ZuneHost.Activate();
        ThemeHost.ResetOptions(ZuneTheme.Id);
        Design.SeedPalette(Tok.Theme);
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void Classic_draws_the_OS_accent_as_it_did_with_the_override(ThemeKind kind)
        => host.Run(() =>
        {
            ThemeKind restore = Tok.Theme;
            try
            {
                Design.SeedPalette(kind);

                // What the removed override did: the engine's own accent for this OS ramp.
                Tok.SetAccent(in Red);
                var viaOverride = Accents();
                Tok.SetAccent((ColorF?)null);
                Assert.NotEqual(viaOverride.Default, Tok.AccentDefault);   // the baseline really differs

                // What the app does now: feed the ramp, the definition carries it.
                Appearance.FollowOsAccent(Red, null, allowPlainFallback: false);
                Assert.Equal(ThemeHost.OsAccent, Red);
                Assert.Equal(Red, Tok.Definition.Accent);
                Assert.Equal(viaOverride, Accents());

                // A light/dark flip keeps it (the seed installs the active definition, which carries the accent).
                Design.SeedPalette(kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
                Assert.Equal(Red, Tok.Definition.Accent);
            }
            finally
            {
                PutBack();
                Design.SeedPalette(restore);
            }
        });

    [Fact]
    public void Classic_goes_back_to_the_baked_accent_when_the_OS_accent_is_the_WinUI_default()
        => host.Run(() =>
        {
            try
            {
                var baked = Accents();
                Appearance.FollowOsAccent(Red, null, allowPlainFallback: false);
                Assert.NotEqual(baked.Default, Tok.AccentDefault);
                Appearance.FollowOsAccent(AccentRamp.FluentDefault, null, allowPlainFallback: false);
                Assert.Equal(baked, Accents());
            }
            finally { PutBack(); }
        });

    [Fact]
    public void Zune_with_its_swatch_keeps_the_swatch_through_the_boot_override_and_an_OS_accent_change()
    {
        host.Run(ActivateZuneAtDefaults);
        try
        {
            host.Run(() =>
            {
                var swatch = Accents();
                Assert.Equal(ZuneAccent.RampOf(ZuneAccent.Pink), Tok.Definition.Accent);

                // The engine installs the OS accent as an override while it creates the window (after the boot seed) ...
                Tok.SetAccent(in Red);
                Assert.NotEqual(swatch.Default, Tok.AccentDefault);

                // ... and the root factory releases it before the first frame.
                Appearance.OnWindowCreated();
                Assert.Equal(swatch, Accents());

                // The OS accent changes: the host knows it, the engine's accent does not move.
                Appearance.FollowOsAccent(Green, null, allowPlainFallback: false);
                Assert.Equal(Green, ThemeHost.OsAccent);
                Assert.Equal(swatch, Accents());
                Assert.Equal(ZuneAccent.RampOf(ZuneAccent.Pink), Tok.Definition.Accent);
            });
        }
        finally
        {
            host.Run(() =>
            {
                ThemeHost.ResetOptions(ZuneTheme.Id);
                PutBack();
                ZuneHost.Restore();
            });
        }
    }

    [Fact]
    public void Zune_with_use_the_windows_accent_draws_the_OS_accent_and_the_swatch_again_when_it_is_off()
    {
        host.Run(ActivateZuneAtDefaults);
        try
        {
            host.Run(() =>
            {
                var swatch = Accents();

                _ = ThemeHost.Apply(new OptionChange(ZuneTheme.Id, "style.useWindowsAccent", true));
                Appearance.FollowOsAccent(Green, null, allowPlainFallback: false);
                Assert.Equal(Green, Tok.Definition.Accent);
                Assert.NotEqual(swatch.Default, Tok.AccentDefault);

                // The OS accent moves again: it is followed.
                Appearance.FollowOsAccent(Red, null, allowPlainFallback: false);
                Assert.Equal(Red, Tok.Definition.Accent);

                _ = ThemeHost.Apply(new OptionChange(ZuneTheme.Id, "style.useWindowsAccent", false));
                Assert.Equal(ZuneAccent.RampOf(ZuneAccent.Pink), Tok.Definition.Accent);
                Assert.Equal(swatch, Accents());
            });
        }
        finally
        {
            host.Run(() =>
            {
                ThemeHost.ResetOptions(ZuneTheme.Id);
                PutBack();
                ZuneHost.Restore();
            });
        }
    }
}
