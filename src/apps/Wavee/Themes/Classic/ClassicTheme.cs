// ── Themes/Classic/ClassicTheme.cs — Wavee Classic = Classic.Structure x Classic.Style (themes plan 6.1) ───────────────
//
// The composition file of the theme (WVTH001: it may reference every layer). T1 installs Classic with an EMPTY structure
// (no view is registered yet: every surface is still core code) and the style Wavee ships today, so installing it changes
// nothing a user can see. The T-lane PRs move the surfaces into `Structure\` one family at a time, and T2 splits
// `Design.cs` into the style's roles.

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic.</summary>
public static class ClassicTheme
{
    /// <summary>The theme id: the stored value of <c>theme.id</c> and the fallback of every unresolvable id.</summary>
    public const string Id = ThemeCatalog.FallbackId;

    /// <summary>Registrations only: the lint guide (Classic's intents, no edges) and, for the `--inspect` dump and lint, the icon glyphs Classic declares
    /// itself (the engine's Icons table has no name for them), before <see cref="Create"/> builds the composition.</summary>
    static ClassicTheme()
    {
        ThemeInspection.RegisterLintGuide(Id, new ClassicLintGuide());
        ThemeInspection.RegisterGlyphNames(
            (nameof(ClassicPlayerBarLayout.PlaySolid), ClassicPlayerBarLayout.PlaySolid), (nameof(ClassicPlayerBarLayout.PauseSolid), ClassicPlayerBarLayout.PauseSolid),
            (nameof(ClassicNavHeadLayout.BannerGlyph), ClassicNavHeadLayout.BannerGlyph), (nameof(SidebarGeometry.OfflineGlyph), SidebarGeometry.OfflineGlyph));
    }

    /// <summary>Builds the composition. <c>App.BootCore</c> installs it.</summary>
    public static ThemeComposition Create() => new(
        new ThemeManifest(
            Id: Id,
            DisplayNameKey: Strings.Theme.Classic.Name,
            ContractMajor: ContractVersion.Major,
            ContractMinor: ContractVersion.Minor,
            StructureId: ClassicStructure.LayerId,
            StyleId: ClassicStyle.LayerId,
            InheritsFrom: null,
            Availability: ThemeAvailability.Public,
            Preview: new ThemePreview("themes/classic-light.png", "themes/classic-dark.png"),
            DescriptionKey: Strings.Theme.Classic.Description),
        new ClassicStructure(),
        new ClassicStyle());
}
