// ── Themes/WinUi/Structure/WinUiStructure.Collection.cs — the collection family's registrar (themes plan 6.2; W7, in part) ─────────────────────
//
// W7 (WinUI's own library page, Recents, Profile) is not built: those keys still come from the stand-in. What WinUI registers here is the library
// page's CHOICE CONTROLS (`StyleKeys.LibraryControls`, winui menus/podcasts): the borrowed page draws WinUI's sort DropDownButton, its MenuFlyout,
// the SelectorBar pivots and the view pair (`Views\Choice\WinUiLibraryControls.cs`) instead of Zune's word rails.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterCollection(ViewRegistryBuilder views)
    {
        views.AddStyle(StyleKeys.LibraryControls, WinUiLibraryControls.Style);

        // #240 P2: "On this PC", the collection page WinUI and Classic share (Themes/Common), on WinUI's page inset.
        Common.CommonLocalCollection.Register(views, "winui.collection.local", static f => WinUiTiers.InsetOf(in f), WinUiFrameLayout.PageTitleKey);
    }
}
