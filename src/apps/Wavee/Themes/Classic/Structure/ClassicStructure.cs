// ── Themes/Classic/Structure/ClassicStructure.cs — the Classic structure (themes plan 6.1) ─────────────────────────────
//
// T1: registers no view (every surface is still core code), declares no option, decides no policy. Each T-lane PR adds the
// views of one family to `Register`; T3 returns the Classic policies and capabilities.
//
// T1b: the options Classic will own (plan 6.1) are still TODAY's switches, bound to their legacy keys through `SettingsDoc`
// rows. `LegacyRows` lists them in their FINAL grouping so Settings › Appearance shows Classic's section once, in its
// final shape; each M-PR deletes its rows from the list and declares the option in `Options` (the generated row takes the
// same place in the same group).

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
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ThemePolicies.Empty;

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>Each entry leaves with the M-PR that migrates its switch: sidebar M7a, row style M2, row density M3, hide art M4,
    /// washes M6, page layout and rails M8, page motion M1 (deleted).</summary>
    public IReadOnlyList<LegacyOptionRow> LegacyRows { get; } =
    [
        new("sidebarDesign", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarDensity", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarReset", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarCustomize", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarResetEverything", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("trackListStyle", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("rowDensity", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("hideTrackArtwork", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("pageLayout", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("railUniform", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("railReset", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("colorWashes", Strings.Settings.ThemeOptions.Group.Surfaces),
        new("pageMotion", Strings.Settings.ThemeOptions.Group.Surfaces),
    ];
}
