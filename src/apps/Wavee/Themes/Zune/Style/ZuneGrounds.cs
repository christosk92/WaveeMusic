// ── Themes/Zune/Style/ZuneGrounds.cs — Zune's flat ladder for one mode, the board's or the user's (#184) ─────────────────────
//
// Zune's grounds were static values (`ZuneTokens`), read in the definition's token sets, its shell palette and its role table. A user's
// colour set (`ColourLadder`) has exactly Zune's rungs (Zune is the model the ladder was drawn from), so the three read ONE record per mode
// instead: the board's values by default (`DefaultDark` / `DefaultLight`, today's colours bit for bit) or the ladder mapped 1:1 (`From`).
// Zune's alphas (the 5 % hover, 8 % press, 12 % selection, the hairlines) stay: they are Zune's look, laid over whichever ground it is.

using FluentGpu.Foundation;
using Wavee.Theming.Colours;

namespace Wavee.Themes.Zune;

/// <summary>Zune's ground ladder and inks for one mode (see the file header).</summary>
/// <param name="Ground">The page.</param>
/// <param name="Well">The play bar and the stage plates.</param>
/// <param name="Panel">The inline right panel.</param>
/// <param name="Raised">Menus, tiles, flyouts, toasts.</param>
/// <param name="Dialog">A dialog plate.</param>
/// <param name="Placeholder">An empty cover.</param>
/// <param name="Ink">The ink (the row and chrome washes are made of it).</param>
/// <param name="InkSecondary">The secondary ink.</param>
/// <param name="InkTertiary">The tertiary ink.</param>
/// <param name="TokenWash">The colour the definition's token washes and hairlines are made of (the board: white on dark, black on light).</param>
public sealed record ZuneGrounds(
    ColorF Ground, ColorF Well, ColorF Panel, ColorF Raised, ColorF Dialog, ColorF Placeholder,
    ColorF Ink, ColorF InkSecondary, ColorF InkTertiary, ColorF TokenWash)
{
    /// <summary>The board's dark ladder (Z-Tokens).</summary>
    public static ZuneGrounds DefaultDark { get; } = new(
        ZuneTokens.DarkGround, ZuneTokens.DarkWell, ZuneTokens.DarkPanel, ZuneTokens.DarkRaised, ZuneTokens.DarkDialog, ZuneTokens.DarkPlaceholder,
        ZuneTokens.DarkInk, ZuneTokens.DarkInkSecondary, ZuneTokens.DarkInkTertiary, ColorF.FromRgba(0xFF, 0xFF, 0xFF));

    /// <summary>The board's light ladder: the light dialog is the raised plate, the light placeholder the well.</summary>
    public static ZuneGrounds DefaultLight { get; } = new(
        ZuneTokens.LightGround, ZuneTokens.LightWell, ZuneTokens.LightPanel, ZuneTokens.LightRaised, ZuneTokens.LightRaised, ZuneTokens.LightWell,
        ZuneTokens.LightInk, ZuneTokens.LightInkSecondary, ZuneTokens.LightInkTertiary, ColorF.FromRgba(0x00, 0x00, 0x00));

    /// <summary>A user's colour set as Zune's ladder: rung for rung.</summary>
    public static ZuneGrounds From(ColourLadder l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return new ZuneGrounds(l.Ground, l.Well, l.Panel, l.Raised, l.Dialog, l.Placeholder, l.Ink, l.InkSecondary, l.InkTertiary, l.WashInk);
    }

    /// <summary>The ladder of <paramref name="l"/>, or the board's for its mode when there is none.</summary>
    public static ZuneGrounds Of(ColourLadder? l, bool dark) => l is null ? (dark ? DefaultDark : DefaultLight) : From(l);
}
