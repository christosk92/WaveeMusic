// ── Themes/SwiftUI/SwiftUITheme.cs — SwiftUI = SwiftUI.Structure (over WinUI's) x SwiftUI.Style (over WinUI's roles) — SPIKE ────────────
//
// A THROWAWAY PROTOTYPE of Apple's current design language (iOS 26 / macOS 26 Tahoe, "Liquid Glass") on Wavee: not for release. The
// composition file is the one place a theme may name other themes (WVTH001), so it is where the spike borrows WinUI:
//   · the STRUCTURE is WinUI's (with Classic's stand-in), under SwiftUI's own frame: the floating glass sidebar, the toolbar on the detail
//     column, the glass capsule player (`SwiftUIStructure`, `SwiftUIFrame`);
//   · the STYLE starts from WinUI's role and control tables and metrics and replaces the type ramp, the colours, the radii, the controls and
//     the materials (`SwiftUIStyle`).
// The WinUI tab strip is hidden (macOS windows have no tab strip in the toolbar; the frame lays out the 48 toolbar only).

using Wavee.Themes.Classic;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.SwiftUI;

/// <summary>The SwiftUI spike theme.</summary>
public static class SwiftUITheme
{
    /// <summary>The theme id (<c>theme.id</c>, and the prefix of its settings, <c>theme.swiftui.*</c>).</summary>
    public const string Id = "swiftui";

    /// <summary>The manifest. Public so it can be picked in Settings > Appearance on the spike branch.</summary>
    public static ThemeManifest Manifest { get; } = new(
        Id: Id,
        DisplayNameKey: Strings.Theme.Swiftui.Name,
        ContractMajor: ContractVersion.Major,
        ContractMinor: ContractVersion.Minor,
        StructureId: SwiftUIStructure.LayerId,
        StyleId: SwiftUIStyle.LayerId,
        InheritsFrom: WinUiTheme.Id,
        Availability: ThemeAvailability.Public,
        Preview: new ThemePreview("themes/swiftui-light.png", "themes/swiftui-dark.png"),
        DescriptionKey: Strings.Theme.Swiftui.Description);

    /// <summary>Builds the composition.</summary>
    public static ThemeComposition Create()
    {
        ThemeInspection.RegisterLintGuide(Id, new SwiftUILintGuide());
        var winuiStructure = new WinUiStructure(standIn: static views => new ClassicStructure().Register(views));
        var style = new SwiftUIStyle(WinUiStyle.Create(), WinUiStyle.DensityOption, WinUiRoles.Table, WinUiControls.Table);
        return new(
            Manifest,
            new SwiftUIStructure(winuiStructure, WinUiSearch.Template),
            style,
            HiddenOptions: new HashSet<string> { WinUiStructure.Tabs.Id },
            DefaultOverrides: new Dictionary<string, object> { [WinUiStructure.Tabs.Id] = false });
    }
}
