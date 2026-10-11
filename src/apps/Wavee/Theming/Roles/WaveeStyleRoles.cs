// ── Theming/Roles/WaveeStyleRoles.cs — the application role table a style publishes (themes plan 5.4) ─────────────────
//
// T1 ships the SKELETON: the type the engine's `ThemeDefinition.AppRoles` will carry, the role-contract version, and the
// colour-only comparison the engine asks for. T2a/T2b add the members (type, ink, layer, shape, space, on-media, stage
// arm, wash, lyrics, motion, case, icons) and the `R` facade; until then the table is empty and Classic's definition
// keeps `AppRoles = null`, exactly as `Design.Definition` is today.

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
/// change (cross-fade on the live path) from one that needs a transaction.</summary>
public sealed record WaveeStyleRoles : IAppRoles
{
    /// <summary>A table with no roles (T1).</summary>
    public static WaveeStyleRoles Empty { get; } = new();

    /// <summary>True when <paramref name="other"/> differs only in colour roles. With no roles yet every two tables are
    /// equal; an <paramref name="other"/> of another implementation is different (the engine's contract).</summary>
    public bool DiffersOnlyInColours(IAppRoles other) => other is WaveeStyleRoles;
}
