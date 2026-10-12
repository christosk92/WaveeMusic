// ── Themes/Classic/Structure/ClassicStructure.cs — the Classic structure (themes plan 6.1) ─────────────────────────────
//
// Registers no view yet (every surface is still core code) and declares no option. Each T-lane PR adds the views of one
// family to `Register`. It decides the shell's policies (`Rules\ClassicPolicies.cs`: nav pane, panel and mini player, player
// bar, chrome row, stage) and offers every capability (T3).

using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The structure of Wavee Classic.</summary>
public sealed class ClassicStructure : IStructureLayer
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "Classic.Structure";

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
    }

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ClassicPolicies.Bundle;

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;
}
