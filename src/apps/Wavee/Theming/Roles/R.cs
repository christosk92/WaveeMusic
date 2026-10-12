// ── Theming/Roles/R.cs — the facade every view reads its style roles through (themes plan 5.4) ─────────────────────────
//
// `R.Type.MicroMeta(text)`, `R.Space.SectionGap`, `R.Motion.ScaleSubtle.Hover`, `R.Case.For(kind)`, and (T2b) the colour roles
// `R.Layer.RowHover`, `R.Ink.AccentDecor`, `R.OnMedia.ScrimRest`, `R.Wash.HeroAlpha`, `R.StageArm.For(kind)`: the active style's
// roles, read where the element is built. The roles are `Tok.Definition.AppRoles`, the table the active `ThemeDefinition` carries, so
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

    /// <summary>The ink: the accent budget's three roles, the premium green and the data-hue ink.</summary>
    public static InkReads Ink => new(Current.Ink);

    /// <summary>The surfaces and row ladders.</summary>
    public static LayerReads Layer => new(Current.Layer);

    /// <summary>The page-rhythm, shelf and flyout geometry. <see cref="SpaceRoles.DockReserve"/> follows the ACTIVE structure's player-bar
    /// height (T6a): the bar's height is the structure's (<c>PlayerBarPolicy.Height</c>), and a page reserves what the bar covers, so
    /// the style table's own value is the fallback that equals it (Classic: 72 = 72, the table is returned as is, no copy). A
    /// structure whose bar is another height gets the table with that reserve, memoized per table and height.</summary>
    public static SpaceRoles Space
    {
        get
        {
            var table = Current.Space;
            float height = ShellPolicy.Now.PlayerBar.Height;
            if (table.DockReserve == height) return table;
            var cached = s_dockCache;
            if (cached is not null && ReferenceEquals(cached.Source, table) && cached.Height == height) return cached.Result;
            var result = table with { DockReserve = height };
            s_dockCache = new DockCache(table, height, result);
            return result;
        }
    }

    private sealed record DockCache(SpaceRoles Source, float Height, SpaceRoles Result);

    private static volatile DockCache? s_dockCache;

    /// <summary>Everything that paints on top of artwork or video.</summary>
    public static OnMediaReads OnMedia => new(Current.OnMedia);

    /// <summary>The immersive stage's polarity table: <c>R.StageArm.For(kind)</c> is a pure per-kind view; the live view of the
    /// active kind is <see cref="StageInk"/>.</summary>
    public static StageArmTable StageArm => Current.StageArm;

    /// <summary>How strongly artwork colours a surface.</summary>
    public static WashReads Wash => new(Current.Wash);

    /// <summary>The lyrics surface's ink decisions.</summary>
    public static LyricsReads Lyrics => new(Current.Lyrics);

    /// <summary>The interaction scale tiers and stagger rungs.</summary>
    public static MotionRoles Motion => Current.Motion;

    /// <summary>How the style cases the words it writes.</summary>
    public static CaseRoles Case => Current.Case;

    /// <summary>The theme kind (light or dark) the roles are being read for: the polarity of the active theme mode. A view that needs the kind
    /// as an ARGUMENT to a pure function (a graded cover's light or dark half, a contrast solve) reads it here; a colour or an alpha that
    /// differs by kind is a role and is read from its family instead of branched on this.</summary>
    public static ThemeKind Kind => Tok.Theme;

    /// <summary><see cref="Kind"/> is light.</summary>
    public static bool IsLight => Tok.Theme == ThemeKind.Light;

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
