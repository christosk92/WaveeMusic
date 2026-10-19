// ── Themes/WinUi/WinUiTheme.cs — WinUI = WinUi.Structure x WinUi.Style (themes plan 6.2; P6 W0) ────────────────────────────────────────
//
// The composition file of the theme (WVTH001: it may reference every layer, and every theme). WinUI is DEVELOPER-only (P6; W9 asserts it):
// with Developer mode off, `ThemeCatalog.Resolve("winui")` answers Classic and the stored `theme.id` is kept, so WinUI comes back when
// Developer mode does.
//
// THE STAND-IN. Until every WinUI family has landed (P6 W9: the WinUI registry-completeness test), the structure is handed Classic's
// registration as its stand-in, exactly as Zune was (`WinUiStructure.cs` explains the key-by-key merge): a Developer who picks WinUI gets a
// working app, Classic's views under WinUI's style, policies, tiers and capabilities. Remove the stand-in in the PR that makes WinUI's own
// registry complete.

using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI.</summary>
public static class WinUiTheme
{
    /// <summary>The theme id: the stored value of <c>theme.id</c> and the prefix of every WinUI setting (<c>theme.winui.*</c>).</summary>
    public const string Id = "winui";

    /// <summary>The manifest.</summary>
    public static ThemeManifest Manifest { get; } = new(
        Id: Id,
        DisplayNameKey: Strings.Theme.Winui.Name,
        ContractMajor: ContractVersion.Major,
        ContractMinor: ContractVersion.Minor,
        StructureId: WinUiStructure.LayerId,
        StyleId: WinUiStyle.LayerId,
        InheritsFrom: null,
        Availability: ThemeAvailability.Developer,
        // No preview images ship yet: the picker draws its UI-native miniature where an asset is missing.
        Preview: new ThemePreview("themes/winui-light.png", "themes/winui-dark.png"),
        DescriptionKey: Strings.Theme.Winui.Description);

    /// <summary>Builds the composition. <c>App.InstallThemes</c> installs it next to Classic and Zune.</summary>
    public static ThemeComposition Create() => new(
        Manifest,
        new WinUiStructure(standIn: static views => new ClassicStructure().Register(views)),
        WinUiStyle.Create());
}
