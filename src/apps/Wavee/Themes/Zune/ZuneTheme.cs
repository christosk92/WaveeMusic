// ── Themes/Zune/ZuneTheme.cs — Zune = Zune.Structure x Zune.Style (themes plan 6.3; P5 Z0) ───────────────────────────────────────────
//
// The composition file of the theme (WVTH001: it may reference every layer, and every theme). Zune is DEVELOPER-only (P5, Z9 asserts it):
// with Developer mode off, `ThemeCatalog.Resolve("zune")` answers Classic and the stored `theme.id` is kept, so Zune comes back when
// Developer mode does.
//
// THE STAND-IN. Until every Zune family has landed (P5 Z9: the Zune registry-completeness test), the structure is handed Classic's
// registration as its stand-in (`ZuneStructure.cs` explains why it is all or nothing for now): a Developer who picks Zune gets a working
// app, Classic's views under Zune's style, policies, tiers and capabilities. Remove the stand-in in the PR that makes Zune's own
// registry complete.

using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune.</summary>
public static class ZuneTheme
{
    /// <summary>The theme id: the stored value of <c>theme.id</c> and the prefix of every Zune setting (<c>theme.zune.*</c>).</summary>
    public const string Id = "zune";

    /// <summary>The manifest.</summary>
    public static ThemeManifest Manifest { get; } = new(
        Id: Id,
        DisplayNameKey: Strings.Theme.Zune.Name,
        ContractMajor: ContractVersion.Major,
        ContractMinor: ContractVersion.Minor,
        StructureId: ZuneStructure.LayerId,
        StyleId: ZuneStyle.LayerId,
        InheritsFrom: null,
        Availability: ThemeAvailability.Developer,
        // No preview images ship yet: the picker draws its UI-native miniature where an asset is missing.
        Preview: new ThemePreview("themes/zune-light.png", "themes/zune-dark.png"),
        DescriptionKey: Strings.Theme.Zune.Description);

    /// <summary>Builds the composition. <c>App.BootCore</c> installs it next to Classic. It also hands the `--inspect` lint Zune's edge
    /// guide (where the gutter and the page column sit per tier: <see cref="ZuneLintGuide"/>), registered once per id.</summary>
    public static ThemeComposition Create()
    {
        ThemeInspection.RegisterLintGuide(Id, new ZuneLintGuide());
        return new(
            Manifest,
            new ZuneStructure(standIn: static views => new ClassicStructure().Register(views)),
            ZuneStyle.Create());
    }
}
