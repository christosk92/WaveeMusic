// ── Theming/Colours/ColourSpec.cs — the colours a user picks for light and for dark (#184) ─────────────────────────────────
//
// A COLOUR SET is one palette for one mode (Catppuccin Mocha for dark, Latte for light). It is given as SEEDS: six colours an
// author names (the page, the raised plate, the ink, the muted ink, the accent, and optionally the lower plates and the faint ink),
// and `ColourSolver` resolves them into a LADDER: every rung a style needs, each one final and floored for contrast. The styles map
// the ladder onto their own token sets, shell palette, role table and window material.
//
// The pick is APP-WIDE (plan D2): one light set and one dark set, applied to whichever theme is active. The styles receive it as an
// INPUT next to the OS accent (`StyleInputs`), exactly as they receive the OS accent today: a style that has not adopted the ladder
// yet ignores it and returns the instance it always returned. A null side of a `ColourPair` is "theme default" for that mode.
//
// "Palette" already means the album-art colour table in code (`Wavee.Palette`) and the command palette in the UI, so these types
// say `Colour` (plan D7).

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Colours;

/// <summary>What a palette author gives for ONE mode. Five seeds are required; the rest are optional and derived when absent.</summary>
/// <param name="Ground">The page (Catppuccin base, Nord nord0 / nord6).</param>
/// <param name="Surface">The raised plates: cards, menus, flyouts (Catppuccin surface0).</param>
/// <param name="Ink">The primary text.</param>
/// <param name="InkMuted">The secondary text (Catppuccin subtext0).</param>
/// <param name="Accent">The palette's default accent.</param>
/// <param name="Well">The lower plate (the player bar, a pane; Catppuccin mantle); derived: the ground stepped away from the surface.</param>
/// <param name="Deep">The deepest band (a tab rail; Catppuccin crust); derived: the well stepped once more.</param>
/// <param name="InkFaint">The tertiary text (Catppuccin overlay1); derived: the muted ink mixed toward the ground.</param>
/// <param name="Critical">The error colour; null keeps the theme's own.</param>
/// <param name="Caution">The warning colour; null keeps the theme's own.</param>
/// <param name="Success">The success colour; null keeps the theme's own.</param>
public readonly record struct ColourSeeds(
    ColorF Ground, ColorF Surface, ColorF Ink, ColorF InkMuted, ColorF Accent,
    ColorF? Well = null, ColorF? Deep = null, ColorF? InkFaint = null,
    ColorF? Critical = null, ColorF? Caution = null, ColorF? Success = null);

/// <summary>One named accent of a palette family (Catppuccin "Mauve"). <paramref name="Name"/> is the family's own word, a proper noun:
/// it is not localized.</summary>
public readonly record struct NamedAccent(string Id, string Name, ColorF Color);

/// <summary>One palette for one mode. The built-ins are static data (<see cref="ColourCatalog"/>).</summary>
/// <param name="Id">Stable and stored (<c>catppuccin.mocha</c>, <c>wavee.ink</c>).</param>
/// <param name="Family">The family a picker groups by (<c>Catppuccin</c>), a proper noun.</param>
/// <param name="Name">The flavour (<c>Mocha</c>), a proper noun: not localized.</param>
/// <param name="Kind">The mode it is offered for.</param>
/// <param name="Seeds">The author's colours.</param>
/// <param name="Accents">The family's named accents; empty when the palette has its one accent.</param>
/// <param name="DefaultAccent">The id of the named accent <see cref="ColourSeeds.Accent"/> is, or null.</param>
public sealed record ColourSpec(string Id, string Family, string Name, ThemeKind Kind, ColourSeeds Seeds,
    ImmutableArray<NamedAccent> Accents, string? DefaultAccent = null)
{
    /// <summary>The accent named <paramref name="accentId"/>, or the palette's default when the id is empty or unknown.</summary>
    public ColorF AccentNamed(string? accentId)
    {
        if (!string.IsNullOrEmpty(accentId))
            foreach (var a in Accents)
                if (string.Equals(a.Id, accentId, StringComparison.Ordinal)) return a.Color;
        return Seeds.Accent;
    }

    /// <summary>"Catppuccin Mocha"; the family alone when the flavour repeats it.</summary>
    public string DisplayName => string.Equals(Family, Name, StringComparison.Ordinal) ? Name : Family + " " + Name;
}

/// <summary>Where the accent comes from while a palette is active (<c>theme.palette.accent</c>; the numbers are stored).</summary>
public enum AccentSource : byte
{
    /// <summary>The theme's own: Classic follows Windows, WinUI and Zune their own options.</summary>
    Theme = 0,
    /// <summary>The palette's accent (a named one where the family has several).</summary>
    Palette = 1,
    /// <summary>One colour the user picked, floored per mode.</summary>
    Own = 2,
}

/// <summary>The resolved ladder a style maps onto its tokens: every value final, contrast already guaranteed (<see cref="ColourSolver"/>).
/// A record, so equal picks compare equal and the styles' memos key on it.</summary>
/// <param name="Deep">The deepest band.</param>
/// <param name="Well">The lower plate: the player bar, a pane.</param>
/// <param name="Ground">The page.</param>
/// <param name="Panel">A third of the way from the ground to the raised plate: an inline panel.</param>
/// <param name="Raised">Menus, tiles, flyouts, toasts.</param>
/// <param name="Dialog">Two thirds of the way from the ground to the raised plate: a dialog.</param>
/// <param name="Placeholder">An empty cover: the raised plate one step further.</param>
/// <param name="Ink">Primary text: 7 : 1 on the ground, 4.5 : 1 on the raised plate.</param>
/// <param name="InkSecondary">Secondary text: 4.5 : 1 on the ground, the well and the raised plate.</param>
/// <param name="InkTertiary">Tertiary text: 4.5 : 1 on the same three, a clear step from the secondary.</param>
/// <param name="InkDisabled">Disabled text (WCAG exempts it: not floored).</param>
/// <param name="WashInk">The opaque colour state washes are made of (a hover, a press, a hairline take it at their own alpha).</param>
/// <param name="AccentFill">The accent as a fill: 3 : 1 on the ground (a non-text UI component).</param>
/// <param name="AccentInk">The accent as text: 4.5 : 1 on the ground, the well, the raised plate and its own selection wash.</param>
/// <param name="OnAccent">Text on the accent fill.</param>
/// <param name="Critical">The error colour floored as text, or null for the theme's own.</param>
/// <param name="Caution">The warning colour floored as text, or null for the theme's own.</param>
/// <param name="Success">The success colour floored as text, or null for the theme's own.</param>
public sealed record ColourLadder(
    string SpecId, ThemeKind Kind,
    ColorF Deep, ColorF Well, ColorF Ground, ColorF Panel, ColorF Raised, ColorF Dialog, ColorF Placeholder,
    ColorF Ink, ColorF InkSecondary, ColorF InkTertiary, ColorF InkDisabled, ColorF WashInk,
    ColorF AccentFill, ColorF AccentInk, ColorF OnAccent,
    ColorF? Critical, ColorF? Caution, ColorF? Success);

/// <summary>What the styles are handed: the light and the dark ladder and where the accent comes from. A null side is "theme default": the
/// style returns today's colours for that mode.</summary>
public readonly record struct ColourPair(ColourLadder? Light, ColourLadder? Dark, AccentSource Accent)
{
    /// <summary>No palette in either mode.</summary>
    public static ColourPair None => default;

    /// <summary>The ladder of <paramref name="kind"/>, or null for the theme default.</summary>
    public ColourLadder? For(ThemeKind kind) => kind == ThemeKind.Light ? Light : Dark;

    /// <summary>True when either mode has a palette.</summary>
    public bool Any => Light is not null || Dark is not null;

    /// <summary>True when the accent comes from the ladders (a palette is active and the source is not the theme's).</summary>
    public bool OwnsAccent => Any && Accent != AccentSource.Theme;
}

/// <summary>The inputs a style resolves its definition from: what the OS said and what the user picked.</summary>
public readonly record struct StyleInputs(AccentRamp OsAccent, ColourPair Colours)
{
    /// <summary>The OS accent at the WinUI default and no palette: the inputs a style's defaults are built with.</summary>
    public static StyleInputs Default => new(AccentRamp.FluentDefault, ColourPair.None);
}
