// ── Themes/WinUi/Structure/WinUiStructure.Overlays.cs — the Overlays family's registrar (themes plan 6.2, P6 W8a) ──────────────────────────────
//
// W8a: the overlays and the dialogs. The context menu's tile and the Compact context sheet, the notification panel (from the bell), the toasts'
// update card, the runtime banner, the offline strip and vacancy, the profile chip and its menu, and the five dialog bodies. The menus, flyouts,
// ToolTip, InfoBar and toasts themselves are the engine's WinUI-look controls; this lane sizes them per band (`WinUiOverlayTiers`, the policy in
// `WinUiPolicies.Overlays.cs`) and draws the pictures the core hosts hand to the structure.
//
// Keys: Menu.Context, Overlay.ContextSheet, Overlay.Notifications, Overlay.OfflineStrip, Overlay.OfflineVacancy, Overlay.ProfileChip, Overlay.ProfileMenu,
// Overlay.RuntimeBanner, Overlay.Toast, State.Vacancy (the offline vacancy's sibling), Dialog.LogoutConfirm, Dialog.PlayLink, Dialog.Signature,
// Dialog.FolderPicker, Dialog.Sheet.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterOverlays(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.OverlayProfileChip, WinUiProfileChip.Template, "winui.overlay.profile-chip");
        views.Add(ViewKeys.OverlayProfileMenu, WinUiProfileMenu.Template, "winui.overlay.profile-menu");
        views.Add(ViewKeys.OverlayNotifications, WinUiNotifications.Template, "winui.overlay.notifications");
        views.Add(ViewKeys.OverlayRuntimeBanner, WinUiRuntimeBanner.Template, "winui.overlay.runtime-banner");
        views.Add(ViewKeys.OverlayToast, WinUiToast.Template, "winui.overlay.toast");
        views.Add(ViewKeys.OverlayOfflineStrip, WinUiOfflineStrip.Template, "winui.overlay.offline-strip");
        views.Add(ViewKeys.OverlayOfflineVacancy, WinUiOfflineVacancy.Template, "winui.overlay.offline-vacancy");
        views.Add(ViewKeys.StateVacancy, WinUiVacancy.Template, "winui.state.vacancy");
        views.Add(ViewKeys.MenuContext, WinUiContextMenuTile.Template, "winui.menu.context");
        views.Add(ViewKeys.OverlayContextSheet, WinUiContextSheet.Template, "winui.overlay.context-sheet");
        views.Add(ViewKeys.DialogLogoutConfirm, WinUiLogoutConfirm.Template, "winui.dialog.logout-confirm");
        views.Add(ViewKeys.DialogPlayLink, WinUiPlayLink.Template, "winui.dialog.play-link");
        views.Add(ViewKeys.DialogSignature, WinUiSignature.Template, "winui.dialog.signature");
        views.Add(ViewKeys.DialogFolderPicker, WinUiDestinationPicker.Template, "winui.dialog.folder-picker");
        views.Add(ViewKeys.DialogSheet, WinUiCreditsSheet.Template, "winui.dialog.sheet");
    }
}
