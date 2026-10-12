// ── Theming/Roles/WaveeStyleRoles.cs — the application role table a style publishes (themes plan 5.4) ─────────────────
//
// The roles of a style are everything a view reads as "how big, which colour, which radius, which casing" and never as "which
// theme". A style publishes them as ONE immutable table on its `ThemeDefinition.AppRoles`; views read it through the `R`
// facade (R.cs), at the point they build an element. T1 shipped the skeleton; T2a fills the TYPE, SPACE, MOTION and CASE roles
// with the values `Design` held (Classic's table is Themes\Classic\Style\ClassicRoles.cs). The colour roles (ink, layer,
// on-media, stage arm, wash, lyrics) and the shape and icon roles join in T2b and the later style PRs, each as a new member
// of this record; the vocabulary is version 1.0 once the plan's whole table (5.4) exists.
//
// No role selects structure: how a seek row, a pivot overflow or a panorama bleed is ARRANGED is the structure's (options,
// parameters, policies); how big, which colour, which radius, which casing is the style's.

using FluentGpu.Theming;

namespace Wavee.Theming.Roles;

/// <summary>The version of the role vocabulary a style must define completely (themes plan 5.4, S2).</summary>
public static class RoleVersion
{
    /// <summary>Breaking change to a role's meaning or removal of a role.</summary>
    public const int Major = 1;

    /// <summary>A role added.</summary>
    public const int Minor = 0;
}

/// <summary>The roles of one style: everything a view reads as "how big, which colour, which radius, which casing" and
/// never as "which theme". Implements the engine's <see cref="IAppRoles"/> so a definition swap can tell a colour-only
/// change (cross-fade on the live path) from one that needs a transaction. Equality is by value: a style that publishes an
/// equal table twice is not a change, and <c>Tok.Use</c> adopts the reference without bumping an epoch.</summary>
/// <param name="Type">The text roles (<c>R.Type.TrackTitle(text)</c>).</param>
/// <param name="Space">The page-rhythm, shelf and flyout geometry.</param>
/// <param name="Motion">The interaction scale tiers and stagger rungs.</param>
/// <param name="Case">How the style cases the words it writes.</param>
public sealed record WaveeStyleRoles(TypeRoles Type, SpaceRoles Space, MotionRoles Motion, CaseRoles Case) : IAppRoles
{
    /// <summary>True when <paramref name="other"/> differs only in colour roles. T2a defines no colour role yet, so two tables
    /// differ "only in colours" exactly when every role they do define is equal; an <paramref name="other"/> of another
    /// implementation is different (the engine's contract). Allocation-free: record equality over primitives and strings.</summary>
    public bool DiffersOnlyInColours(IAppRoles other)
        => other is WaveeStyleRoles o
        && this.Type.Equals(o.Type)
        && this.Space.Equals(o.Space)
        && this.Motion.Equals(o.Motion)
        && this.Case.Equals(o.Case);
}
