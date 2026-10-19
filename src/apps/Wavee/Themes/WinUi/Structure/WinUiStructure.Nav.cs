// ── Themes/WinUi/Structure/WinUiStructure.Nav.cs — the nav pane family (P6 W2b) ───────────────────────────────────────────────────────
//
// The pane (the list, the heads, the rail's flyouts, the Edit outline, the Settings footer), the narrow drawer (the 280 acrylic overlay the hamburger
// opens at Compact and Small) and the material layer with its four cover leaves (WinUI draws no wash: the window's Mica is the ground, so the layer and
// the leaves paint nothing, and the tint binder publishes a definite "no colour"). Your Library's head is Nothing: WinUI asks for the Classic layout only
// (`NavPanePolicy.Layout`), so the Library layout never mounts.

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterNav(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellNavPane, WinUiNavPane.Template, "winui.shell.nav-pane");
        views.Add(ViewKeys.ShellNavEmpty, WinUiNavEmpty.Template, "winui.shell.nav-empty");
        views.Add(ViewKeys.ShellNavSectionFlyout, WinUiNavFlyouts.SectionTemplate, "winui.shell.nav-section-flyout");
        views.Add(ViewKeys.ShellNavFolderFlyout, WinUiNavFlyouts.FolderTemplate, "winui.shell.nav-folder-flyout");
        views.Add(ViewKeys.ShellNavEdit, WinUiNavEdit.Template, "winui.shell.nav-edit");
        CommonNothing.Register(views, ViewKeys.ShellNavLibraryHead);
        views.Add(ViewKeys.ShellNarrowDrawer, WinUiDrawer.Template, "winui.shell.narrow-drawer");
        views.Add(ViewKeys.ShellMaterialLayer, WinUiMaterial.Template, "winui.shell.material");
        views.Add(ViewKeys.ShellCoverTone, WinUiMaterial.CoverTone, "winui.shell.material.page-tone");
        views.Add(ViewKeys.ShellCoverBlend, WinUiMaterial.CoverBlend, "winui.shell.material.artist-blend");
        views.Add(ViewKeys.ShellCoverVeil, WinUiMaterial.CoverVeil, "winui.shell.material.artist-veil");
        views.Add(ViewKeys.ShellCoverTint, WinUiMaterial.CoverTint, "winui.shell.material.tint-binder");
    }
}
