// ── Wavee.ShellTests/ClassicBand.cs — the test-only theme whose shell offers what Zune's does (P5 ZK) ────────────────────────
//
// Classic's structure (every view key, the real frame, Classic's policies) and Classic's style, with the CAPABILITIES of a band-style
// structure: no tabs, no nav tree, no forward button, six band pins (Zune's, technical plan 6.3), history menu and friends panel kept.
// It is what the core's capability seams are checked against on the real mounted shell before any Zune view exists: the tab island
// and Forward leave the title bar, the sidebar pane does not mount, the workspace collapses on the switch and comes back on the switch
// back, the band's pins are capped.
//
// Never shipped: it lives in the test project and is installed next to Classic by the facts that need it (`ClassicAlt.RestoreClassic`
// puts Classic back alone).

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;

namespace Wavee.ShellTests;

/// <summary>The test-only ClassicBand theme: Classic with Zune's shell capabilities.</summary>
public static class ClassicBand
{
    public const string Id = "classic-band";

    /// <summary>Zune's capabilities (technical plan 6.3): no tabs, no nav tree, no forward button, six band pins.</summary>
    public static ShellCapabilities Capabilities { get; } = new(
        Tabs: false, NavTree: false, FriendsPanel: true, ForwardButton: false, HistoryMenu: true, MaxBandPins: 6);

    /// <summary>The composition. Install it next to Classic: <c>ThemeHost.Install(ClassicTheme.Create(), ClassicBand.Create())</c>.</summary>
    public static ThemeComposition Create() => new(
        new ThemeManifest(
            Id: Id,
            DisplayNameKey: "theme.classic-band.name",
            ContractMajor: ContractVersion.Major,
            ContractMinor: ContractVersion.Minor,
            StructureId: ClassicStructure.LayerId,
            StyleId: ClassicStyle.LayerId,
            InheritsFrom: null,
            Availability: ThemeAvailability.Public,
            Preview: new ThemePreview("themes/classic-light.png", "themes/classic-dark.png"),
            DescriptionKey: null),
        new Structure(new ClassicStructure()),
        new ClassicStyle());

    /// <summary>Classic's structure in every respect but its capabilities.</summary>
    private sealed class Structure(ClassicStructure inner) : IStructureLayer
    {
        public string Id => inner.Id;
        public ThemeOptionSchema Options => inner.Options;
        public RoleSet RequiredRoles => inner.RequiredRoles;
        public IReadOnlyList<LegacyOptionRow> LegacyRows => inner.LegacyRows;
        public void Register(ViewRegistryBuilder views) => inner.Register(views);
        public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => inner.Policies(structure, in metrics);
        public ShellCapabilities Capabilities(OptionValues structure) => ClassicBand.Capabilities;
    }
}
