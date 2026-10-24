// ── Themes/Zune/Structure/ZuneStructure.Band.cs — the band family's registrar (themes plan 6.3; P5 Z2b) ─────────────────────────────────
//
// Z2b's keys: `Shell.NavPane` is the band (Zune has no sidebar: the pivots and pins over the page column are the navigation), the material
// layer and its four cover leaves (see `Views\Material\ZuneMaterial.cs` for the leaf decisions), and Nothing for the nav pane's heads, flyouts
// and edit outline (Zune never mounts the core pane: `ShellCapabilities.NavTree` is false, so nothing ever asks for them). Each replaces the
// stand-in's (Classic's) key by key.

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterBand(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellNavPane, ZuneBandView.Template, "zune.shell.band");
        views.Add(ViewKeys.ShellMaterialLayer, ZuneMaterial.Template, "zune.shell.material");
        views.Add(ViewKeys.ShellCoverTone, ZuneMaterial.CoverTone, "zune.shell.material.page-tone");
        views.Add(ViewKeys.ShellCoverBlend, ZuneMaterial.CoverBlend, "zune.shell.material.artist-blend");
        views.Add(ViewKeys.ShellCoverVeil, ZuneMaterial.CoverVeil, "zune.shell.material.artist-veil");
        views.Add(ViewKeys.ShellCoverTint, ZuneMaterial.CoverTint, "zune.shell.material.tint-binder");
        CommonNothing.Register(views, ViewKeys.ShellNavLibraryHead);
        CommonNothing.Register(views, ViewKeys.ShellNavEmpty);
        CommonNothing.Register(views, ViewKeys.ShellNavSectionFlyout);
        CommonNothing.Register(views, ViewKeys.ShellNavFolderFlyout);
        CommonNothing.Register(views, ViewKeys.ShellNavEdit);
    }
}
