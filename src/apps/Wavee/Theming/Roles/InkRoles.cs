// ── Theming/Roles/InkRoles.cs — the ink a style writes with (themes plan 5.4, T2b) ──────────────────────────────────────
//
// What was `Design.Accent` (the accent budget's three roles), the premium green of `Design.Colors` and the data-hue ink of
// `Design.Palette.DataDotInk`. Ink is the colour of a glyph, a word or a mark painted ON the page surface; the colour of the
// SURFACE is `LayerRoles`, ink over artwork is `OnMediaLadder`.
//
// THE ACCENT BUDGET — the three roles accent colour is allowed to play, and the rules that keep them apart.
//   Wavee is deliberately NOT a monochrome Fluent app: the artwork-derived accent is identity, and it stays. What it stopped being
//   is AMBIENT. An accent that appears in four unrelated jobs on one page stops meaning any of them, so every accent paint site
//   declares which of exactly THREE roles it is:
//     1. Action    — "clicking this does the page's primary thing": a SOLID accent plate behind on-accent ink. AT MOST ONE per
//                    screenful; the scarcest role.
//     2. Selection — "you are here": a short accent bar/pill against the edge of a control. The geometry is RESERVED to selection.
//     3. Decor     — "this content has a colour": section spines, hero washes, eyebrows, accent facts. Accent as TEXT or as a wash,
//                    never as a plate behind ink and never as a selection-shaped bar. The largest role by site count.
//   The two hard rules: a decorative ornament may not take SELECTION geometry, and accent is never STRUCTURE (a border, a
//   divider, a chevron takes `Tok.StrokeDividerDefault` / `Tok.TextSecondary`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The ink roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="AccentAction">Role 1: the SOLID plate behind on-accent ink for the page's ONE primary action. Artwork-derived surfaces pass their own graded fill to the CTA instead of reading this.</param>
/// <param name="AccentSelection">Role 2: "you are here".</param>
/// <param name="AccentDecor">Role 3: accent as CONTENT colour (text, spines, washes): the contrast-corrected accent INK, never the raw fill, because it always paints on the page surface.</param>
/// <param name="PremiumText">Premium green. Light takes the system success token; dark keeps the brand value, which the system token is too dim for over a #202020 ground.</param>
/// <param name="DataDot">A SERVER-SUPPLIED data hue (the Camelot key wheel) as ink for a theme kind. The wire colours are authored for a dark surface, so the dark arm passes them through and the light arm re-grades them (see <c>ColorScience.DataDotInk</c>).</param>
public sealed record InkRoles(
    ColorRole AccentAction, ColorRole AccentSelection, ColorRole AccentDecor, ColorRole PremiumText,
    Func<uint, ThemeKind, ColorF> DataDot);

/// <summary>The ink roles as the call syntax a view paints with (<c>Color = R.Ink.AccentDecor</c>). Every member evaluates its
/// role for the ACTIVE theme kind at the call: nothing is cached.</summary>
public readonly struct InkReads
{
    private readonly InkRoles _roles;

    internal InkReads(InkRoles roles) => _roles = roles;

    /// <summary>The role table behind these reads.</summary>
    public InkRoles Roles => _roles;

    /// <inheritdoc cref="InkRoles.AccentAction"/>
    public ColorF AccentAction => _roles.AccentAction.Now;
    /// <inheritdoc cref="InkRoles.AccentSelection"/>
    public ColorF AccentSelection => _roles.AccentSelection.Now;
    /// <inheritdoc cref="InkRoles.AccentDecor"/>
    public ColorF AccentDecor => _roles.AccentDecor.Now;
    /// <inheritdoc cref="InkRoles.PremiumText"/>
    public ColorF PremiumText => _roles.PremiumText.Now;

    /// <summary>The data hue <paramref name="argb"/> as ink for the ACTIVE theme kind.</summary>
    public ColorF DataDot(uint argb) => _roles.DataDot(argb, Tok.Theme);

    /// <summary>The data hue <paramref name="argb"/> as ink for <paramref name="kind"/> (a pure function of its arguments).</summary>
    public ColorF DataDot(uint argb, ThemeKind kind) => _roles.DataDot(argb, kind);
}
