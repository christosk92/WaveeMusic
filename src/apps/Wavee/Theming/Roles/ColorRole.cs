// ── Theming/Roles/ColorRole.cs — the two shapes a colour role takes (themes plan 5.4, T2b) ──────────────────────────────
//
// A colour role is a PAIR of values or a FUNCTION of the theme kind:
//
//   · `ColorRole.Pair(light, dark)` / `ColorRole.Fixed(c)` — the two literals a style authored (Classic's on-media scrims, its
//     artwork placeholder). Equal pairs compare equal by value.
//   · `ColorRole.By(kind => …)` — a recipe over the engine's LIVE tokens (`Tok.FillSubtleTertiary`, the shell palette of
//     `Tok.Palette`, the accent ramp): a role that is "the engine's hover rung" cannot be a frozen colour, because the engine
//     re-derives its tokens on every accent or palette change. The function receives the kind it is asked for; every other input
//     it reads is as live as the call. Equal functions are the SAME delegate (a style builds its table once from static
//     lambdas), so two reads of one table compare equal.
//
// A view never stores a `ColorRole.Now` into a `const`, a `static readonly` field or a long-lived closure (the same rule as every
// `Tok.*` read, FGTH004): the facade (`R.Layer`, `R.OnMedia`, …) evaluates the role where the element is built, so a theme switch
// or a light/dark flip reaches the next render.
//
// `ModePair<T>` is the same idea for the numbers a style gives per polarity (a wash alpha, a tint pull): two values, no recipe.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>One colour decision of a style: a light/dark pair, or a function of the theme kind over the live tokens.</summary>
/// <param name="Light">The light value of a pair (unused when <paramref name="Of"/> is set).</param>
/// <param name="Dark">The dark value of a pair (unused when <paramref name="Of"/> is set).</param>
/// <param name="Of">The recipe, or null for a pair.</param>
public readonly record struct ColorRole(ColorF Light, ColorF Dark, Func<ThemeKind, ColorF>? Of)
{
    /// <summary>The same colour in both kinds.</summary>
    public static ColorRole Fixed(ColorF colour) => new(colour, colour, null);

    /// <summary>One colour per kind.</summary>
    public static ColorRole Pair(ColorF light, ColorF dark) => new(light, dark, null);

    /// <summary>A recipe. Pass a <c>static</c> lambda or a static method group so that building a table twice yields equal roles.</summary>
    public static ColorRole By(Func<ThemeKind, ColorF> recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new ColorRole(default, default, recipe);
    }

    /// <summary>The colour for <paramref name="kind"/>. A recipe reads the live tokens, so only the active kind is exact for a role
    /// that reads them; a pair and a recipe over fixed inputs are exact for either kind.</summary>
    public ColorF Resolve(ThemeKind kind) => Of is { } recipe ? recipe(kind) : kind == ThemeKind.Light ? Light : Dark;

    /// <summary>The colour for the ACTIVE theme kind (a live read of <c>Tok.Theme</c>). The facade calls this; a view reads the facade.</summary>
    public ColorF Now => Resolve(Tok.Theme);
}

/// <summary>A value a style gives per polarity: the wash alphas, the tint pulls, the bloom scale.</summary>
public readonly record struct ModePair<T>(T Light, T Dark)
{
    /// <summary>The value for <paramref name="kind"/>.</summary>
    public T For(ThemeKind kind) => kind == ThemeKind.Light ? Light : Dark;

    /// <summary>The value for a polarity given as a flag (<c>true</c> = light).</summary>
    public T For(bool light) => light ? Light : Dark;

    /// <summary>The value for the ACTIVE theme kind (a live read of <c>Tok.Theme</c>).</summary>
    public T Now => For(Tok.Theme);
}
