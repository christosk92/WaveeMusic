// ── Themes/FluentV8/FluentV8Theme.cs — FluentV8 = FluentV8.Structure(WinUi.Structure) x FluentV8.Style(WinUi.Style) (SPIKE) ───────────────
//
// A throwaway prototype of a Fluent UI React v8 (Office / Microsoft 365 web) theme. The composition file is the one place that may name
// another theme (WVTH001), so it builds WinUI's structure and style and hands them to FluentV8's layers as their base: FluentV8 is WinUI's
// arrangement with v8's tokens, controls and a few v8 views. Public on the spike branch so the picker offers it.

using Wavee.Themes.Classic;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.FluentV8;

/// <summary>FluentV8 (spike).</summary>
public static class FluentV8Theme
{
    /// <summary>The theme id (<c>theme.fluentv8.*</c>).</summary>
    public const string Id = "fluentv8";

    /// <summary>The manifest.</summary>
    public static ThemeManifest Manifest { get; } = new(
        Id: Id,
        DisplayNameKey: Strings.Theme.Fluentv8.Name,
        ContractMajor: ContractVersion.Major,
        ContractMinor: ContractVersion.Minor,
        StructureId: FluentV8Structure.LayerId,
        StyleId: FluentV8Style.LayerId,
        InheritsFrom: WinUiTheme.Id,
        Availability: ThemeAvailability.Public,
        Preview: new ThemePreview("themes/fluentv8-light.png", "themes/fluentv8-dark.png"),
        DescriptionKey: Strings.Theme.Fluentv8.Description);

    /// <summary>Builds the composition: WinUI's structure (with Classic's stand-in, as WinUI ships) and style under FluentV8's layers.</summary>
    public static ThemeComposition Create()
    {
        ThemeInspection.RegisterLintGuide(Id, new WinUiLintGuide());
        var winUi = new WinUiStructure(standIn: static views => new ClassicStructure().Register(views));
        // Office web apps have no tab strip: one 48 command row (the user can still turn WinUI's tabs on).
        var defaults = new Dictionary<string, object> { [WinUiStructure.Tabs.Id] = false };
        return new(Manifest, new FluentV8Structure(winUi), new FluentV8Style(WinUiStyle.Create()), DefaultOverrides: defaults);
    }
}
