// ── Theming/Roles/WaveeStyleRoles.cs — the application role table a style publishes (themes plan 5.4) ─────────────────
//
// The roles of a style are everything a view reads as "how big, which colour, which radius, which casing" and never as "which
// theme". A style publishes them as ONE immutable table on its `ThemeDefinition.AppRoles`; views read it through the `R`
// facade (R.cs), at the point they build an element. T1 shipped the skeleton; T2a filled the TYPE, SPACE, MOTION and CASE roles
// with the values `Design` held; T2b adds the COLOUR roles: ink, layer, on-media, stage arm, wash and lyrics ink (Classic's table
// is Themes\Classic\Style\ClassicRoles.cs). Contract 1.26 (P5, ZV) adds the SHAPE roles and the pivot-and-panorama type roles a
// Zune-shaped structure reads. The icon table joins with the PR whose consumers it is (the route glyphs), as a new member of this
// record.
//
// No role selects structure: how a seek row, a pivot overflow or a panorama bleed is ARRANGED is the structure's (options,
// parameters, policies); how big, which colour, which radius, which casing is the style's.

using FluentGpu.Theming;

namespace Wavee.Theming.Roles;

/// <summary>The roles of one style: everything a view reads as "how big, which colour, which radius, which casing" and
/// never as "which theme". Implements the engine's <see cref="IAppRoles"/> so a definition swap can tell a colour-only
/// change (cross-fade on the live path) from one that needs a transaction. Equality is by value: a style that publishes an
/// equal table twice is not a change, and <c>Tok.Use</c> adopts the reference without bumping an epoch.</summary>
/// <param name="Type">The text roles (<c>R.Type.TrackTitle(text)</c>).</param>
/// <param name="Ink">The ink: the accent budget's three roles, the premium green and the data-hue ink (<c>R.Ink.AccentDecor</c>).</param>
/// <param name="Layer">The surfaces and row ladders (<c>R.Layer.RowHover</c>).</param>
/// <param name="Shape">The corner radius of each kind of surface (<c>R.Shape.All(ShapeRole.Cover)</c>).</param>
/// <param name="Space">The page-rhythm, shelf and flyout geometry.</param>
/// <param name="OnMedia">Everything that paints on top of artwork or video (<c>R.OnMedia.ScrimRest</c>).</param>
/// <param name="StageArm">The immersive stage's polarity (<c>StageInk.Ink</c>, <c>R.StageArm.For(kind)</c>).</param>
/// <param name="Wash">How strongly artwork colours a surface (<c>R.Wash.CoverMix</c>).</param>
/// <param name="Lyrics">The lyrics surface's ink decisions (<c>R.Lyrics.BloomScale</c>).</param>
/// <param name="Motion">The interaction scale tiers and stagger rungs.</param>
/// <param name="Case">How the style cases the words it writes.</param>
public sealed record WaveeStyleRoles(
    TypeRoles Type, InkRoles Ink, LayerRoles Layer, ShapeRoles Shape, SpaceRoles Space,
    OnMediaLadder OnMedia, StageArmTable StageArm, WashRoles Wash, LyricsInkRoles Lyrics,
    MotionRoles Motion, CaseRoles Case) : IAppRoles
{
    /// <summary>True when <paramref name="other"/> differs only in colour roles: every non-colour role (type, shape, space, motion,
    /// case) is equal and the colour roles (ink, layer, on-media, stage arm, wash, lyrics ink) may differ. That is the engine's question
    /// while it decides between the live cross-fade path (colours only) and an atomic application. An <paramref name="other"/> of
    /// another implementation is different (the engine's contract). Allocation-free: record equality over primitives and strings.</summary>
    public bool DiffersOnlyInColours(IAppRoles other)
        => other is WaveeStyleRoles o
        && this.Type.Equals(o.Type)
        && this.Shape.Equals(o.Shape)
        && this.Space.Equals(o.Space)
        && this.Motion.Equals(o.Motion)
        && this.Case.Equals(o.Case);
}
