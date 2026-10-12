// ── Theming/Roles/R.cs — the facade every view reads its style roles through (themes plan 5.4) ─────────────────────────
//
// `R.Type.MicroMeta(text)`, `R.Space.SectionGap`, `R.Motion.ScaleSubtle.Hover`, `R.Case.For(kind)`: the active style's roles,
// read where the element is built. The roles are `Tok.Definition.AppRoles`, the table the active `ThemeDefinition` carries, so
//   · a definition swap (`Tok.Use`, a theme switch, a Live option that moves a role) reaches the NEXT read: nothing is cached
//     here, and the engine re-renders every mounted component on the epoch bump that comes with the swap;
//   · a view never captures a role into a `const`, a `static readonly` field or a long-lived closure (the same rule as the
//     engine's `Tok.*` reads, FGTH004): a field would keep the first style's value for the rest of the process.
//
// THE PROCESS BASELINE. Before a Wavee definition is installed (the few boot steps ahead of `Appearance.Seed`, and a unit-test
// process that never boots the shell) `AppRoles` is null. Reads then fall back to the roles the app registered as its
// baseline, which are Classic's: `ClassicRoles` registers itself when the assembly loads (a module initializer), so core
// never names a theme and no read ever lands on an empty table. A test that needs another style's roles installs a
// definition that carries them (ThemeDefinition.AppRoles) in the shell test process, the one process where Tok may change.

using FluentGpu.Dsl;

namespace Wavee.Theming.Roles;

/// <summary>The facade over the active style's roles.</summary>
public static class R
{
    private static volatile WaveeStyleRoles? s_baseline;

    /// <summary>The active style's role table: the installed definition's <c>AppRoles</c>, else the process baseline.</summary>
    public static WaveeStyleRoles Current => Tok.Definition.AppRoles as WaveeStyleRoles ?? Baseline;

    /// <summary>The type roles, as the call syntax a view builds text with (<c>R.Type.TrackTitle(text)</c>).</summary>
    public static TypeFactory Type => new(Current.Type);

    /// <summary>The page-rhythm, shelf and flyout geometry.</summary>
    public static SpaceRoles Space => Current.Space;

    /// <summary>The interaction scale tiers and stagger rungs.</summary>
    public static MotionRoles Motion => Current.Motion;

    /// <summary>How the style cases the words it writes.</summary>
    public static CaseRoles Case => Current.Case;

    /// <summary>Registers the roles a read falls back to while no definition carries any. Called once by the theme the app
    /// treats as its default (a module initializer in the app assembly); a second call replaces the first.</summary>
    internal static void SetBaseline(WaveeStyleRoles roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        s_baseline = roles;
    }

    private static WaveeStyleRoles Baseline => s_baseline
        ?? throw new InvalidOperationException(
            "No style roles are installed: the active ThemeDefinition carries no WaveeStyleRoles and no baseline was registered "
            + "(a definition built outside a theme's style must set AppRoles).");
}
