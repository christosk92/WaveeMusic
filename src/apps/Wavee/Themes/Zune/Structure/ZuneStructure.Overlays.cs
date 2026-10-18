// ── Themes/Zune/Structure/ZuneStructure.Overlays.cs — the Overlays family's registrar (themes plan 6.3, P5 Z8a) ───────────────────────────────────
//
// Z8a: the overlays and the dialog bodies. The account chip and its menu, the notification panel, the playback-runtime banner, the update toast's download card, the offline strip
// and vacancy, the context menu's identity tile (the menu's header and round-verb strip are presenter parts of the control table, `ZuneControls.TrackMenu`), the context sheet
// (a pointer menu at Compact), and the dialog bodies the app builds (log out, play a link, the signature sheet, the destination picker, the credits sheet). The STATE stays core:
// which overlay is open and where it anchors, the dismiss rules and the focus trap, the toast queue (the cap of three, the stacking, the motion, the clock, the action word),
// the dialog shells, every verb and every decision. How the core hosts size and place them per window tier is the policy (`ZunePolicies.Overlays`, from `ZuneOverlayTiers`).
// Zune's registrations win over Classic's stand-in key by key.
//
// Keys: Overlay.ProfileChip, Overlay.ProfileMenu, Overlay.Notifications, Overlay.RuntimeBanner, Overlay.Toast, Overlay.OfflineStrip, Overlay.OfflineVacancy, Menu.Context,
// Overlay.ContextSheet, Dialog.LogoutConfirm, Dialog.PlayLink, Dialog.Signature, Dialog.FolderPicker, Dialog.Sheet.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterOverlays(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.OverlayProfileChip, ZuneProfileChip.Template, "zune.overlay.profile-chip");
        views.Add(ViewKeys.OverlayProfileMenu, ZuneProfileMenu.Template, "zune.overlay.profile-menu");
        views.Add(ViewKeys.OverlayNotifications, ZuneNotifications.Template, "zune.overlay.notifications");
        views.Add(ViewKeys.OverlayRuntimeBanner, ZuneRuntimeBanner.Template, "zune.overlay.runtime-banner");
        views.Add(ViewKeys.OverlayToast, ZuneToast.Template, "zune.overlay.toast");
        views.Add(ViewKeys.OverlayOfflineStrip, ZuneOfflineStrip.Template, "zune.overlay.offline-strip");
        views.Add(ViewKeys.OverlayOfflineVacancy, ZuneOfflineVacancy.Template, "zune.overlay.offline-vacancy");
        views.Add(ViewKeys.StateVacancy, ZuneVacancy.Template, "zune.state.vacancy");   // contract 1.37
        views.Add(ViewKeys.MenuContext, ZuneContextMenu.Tile, "zune.menu.context-tile");
        views.Add(ViewKeys.OverlayContextSheet, ZuneContextSheet.Template, "zune.overlay.context-sheet");
        views.Add(ViewKeys.DialogLogoutConfirm, ZuneLogoutConfirm.Template, "zune.dialog.logout-confirm");
        views.Add(ViewKeys.DialogPlayLink, ZunePlayLink.Template, "zune.dialog.play-link");
        views.Add(ViewKeys.DialogSignature, ZuneSignature.Template, "zune.dialog.signature");
        views.Add(ViewKeys.DialogFolderPicker, ZuneFolderPicker.Template, "zune.dialog.folder-picker");
        views.Add(ViewKeys.DialogSheet, ZuneCreditsSheet.Template, "zune.dialog.sheet");
    }
}
