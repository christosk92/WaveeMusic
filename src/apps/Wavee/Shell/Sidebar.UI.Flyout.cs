// ── Shell/Sidebar.UI.Flyout.cs ─────────────────────────────────────────────────────────────────────────────────────
// the compact rail's flyouts, as the core opens them: which one is open, where it anchors and how it dismisses. The PANELS (a folder
// tile's children, a collapsed section's rows) are the structure's: `Shell.NavFolderFlyout` / `Shell.NavSectionFlyout` (T5c), drawn
// by Classic's `Views/Nav/ClassicNavFlyouts.cs`
//
// Role: UI
// Owner: J
// Wave: 4
// Spec: ch 25 §0.9-11, §2 W6, §9 · sidebar-rework-implementation §P2.5
// NAMED PARTIAL of Sidebar.UI.cs (J1): PaneView's flyout members

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Sidebar
{
    internal sealed partial class PaneView
    {
        OverlayHandle? _flyout;

        /// <summary>A compact folder tile's children (NavigationView's collapsed-parent flyout, NVX:468-496): right of the
        /// tile, padding 0,2, corner 8; a second click closes it; a selection inside closes it (CPP:1066-1068).</summary>
        internal void OpenFolderFlyout(string sectionId, in SidebarLibraryEntry folder, Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(MenuOverlay) || folder.FolderId.Length == 0) return;
            if (_flyout is { IsOpen: true } open) { open.Close(); return; }
            // Built once per open: the content builder may run again, and the props are mount seeds.
            var props = new NavFolderFlyoutProps(this, sectionId, folder.FolderId, folder.Name, CloseFlyout);
            _flyout = MenuOverlay.Open(anchor,
                () => ViewSlots.View(ViewKeys.ShellNavFolderFlyout, props),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _flyout.ClosedAction = () => _flyout = null;
        }

        /// <summary>A collapsed section's rows (design V.9): the same rows the expanded pane plans for the section.</summary>
        internal void OpenSectionFlyout(string sectionId, Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return;
            if (_flyout is { IsOpen: true } open) { open.Close(); return; }
            var props = new NavSectionFlyoutProps(this, sectionId, CloseFlyout);
            _flyout = MenuOverlay.Open(anchor,
                () => ViewSlots.View(ViewKeys.ShellNavSectionFlyout, props),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _flyout.ClosedAction = () => _flyout = null;
        }

        internal void CloseFlyout() => _flyout?.Close();
    }
}
