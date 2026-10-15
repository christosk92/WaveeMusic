// ── Theming/Contracts/ViewKeys.Overlays.cs — the overlays' and dialogs' published keys (themes plan 6.0, T11a) ─────────────────
//
// T11a publishes the pictures the app draws over the page: the account chip and its menu, the notification panel, the playback-runtime
// banner, the update toast's download card, the offline strip, the context menu's identity tile, and the dialog bodies the app builds
// (the log-out card, the play-a-link card, the digital-signature sheet, the destination picker, the credits sheet). The STATE stays core:
// which overlay is open and where it anchors, the dismiss rules and the focus trap (`PopupOptions`), the toast queue (stacking, the cap, the
// enter and exit motion, the auto-dismiss clock, hover-to-pause and the action buttons are the engine's `ToastHost`), the dialog shell
// (`ContentDialog`: its card, its buttons and their order, the default button, Enter and Escape), every verb and every decision.
//
// There is NO teaching-tip key: the teaching-tip subsystem (`TipService`, `Shell.Tips`) was deleted as dead code before this PR.
// The confirm, prompt (rename) and "reset everything" dialogs are engine `ContentDialog` configurations with no element tree of the app's
// own, so they have no key either (they wait for the engine's dialog presenter parts, E14); see `docs\guide\theming.md` section 2b.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The trailing island's account chip: the avatar (a skeleton until the identity is known), the name beside it in the named
    /// form. Mounted by the core chip host, which keeps the menu's overlay handle and the anchor capture. Props: <see cref="ProfileChipProps"/>.</summary>
    public static readonly ViewKey<ProfileChipProps> OverlayProfileChip = new("Overlay.ProfileChip");

    /// <summary>The account flyout's body: the identity header, a divider, the menu over the rows the core decided. Mounted inside the chip's
    /// popup. Props: <see cref="ProfileMenuProps"/>.</summary>
    public static readonly ViewKey<ProfileMenuProps> OverlayProfileMenu = new("Overlay.ProfileMenu");

    /// <summary>The notification panel's body: the header, the filter pills, the pending-sync line and the feed's rows. Mounted by the core panel
    /// host, which keeps the feed's state, the open path and the verbs. Props: <see cref="NotificationsProps"/>.</summary>
    public static readonly ViewKey<NotificationsProps> OverlayNotifications = new("Overlay.Notifications");

    /// <summary>The playback-runtime banner: the lane's width cap and the card (an opaque base under a warning InfoBar with "Set up").
    /// Mounted by the core banner host, which decides when it shows. Props: <see cref="RuntimeBannerProps"/>.</summary>
    public static readonly ViewKey<RuntimeBannerProps> OverlayRuntimeBanner = new("Overlay.RuntimeBanner");

    /// <summary>The update-lifecycle toast's download card (the custom body of the toast: the title over a bound progress bar). The toast
    /// queue is the engine's. Props: <see cref="ToastCardProps"/>.</summary>
    public static readonly ViewKey<ToastCardProps> OverlayToast = new("Overlay.Toast");

    /// <summary>The strip over kept content while the session is down. Mounted by the content state's offline lane. Props: <see cref="OfflineStripProps"/>.</summary>
    public static readonly ViewKey<OfflineStripProps> OverlayOfflineStrip = new("Overlay.OfflineStrip");

    /// <summary>The offline vacancy (contract 1.8): what a page with nothing saved shows while the session is offline, in place of its
    /// error vacancy. Mounted by the core vacancy (<c>Controls.Vacancy</c>, Error voice, page scale) while
    /// <c>Shell.FrameRules.OfflineVoice</c> holds. Props: <see cref="OfflineVacancyProps"/>.</summary>
    public static readonly ViewKey<OfflineVacancyProps> OverlayOfflineVacancy = new("Overlay.OfflineVacancy");

    /// <summary>The identity tile of a context menu's header (the art: a rounded square, or a circle for an artist). The menu is the engine's.
    /// Props: <see cref="ContextMenuTileProps"/>.</summary>
    public static readonly ViewKey<ContextMenuTileProps> MenuContext = new("Menu.Context");

    /// <summary>The log-out confirmation card (a bare modal card, not a ContentDialog). Props: <see cref="LogoutConfirmProps"/>.</summary>
    public static readonly ViewKey<LogoutConfirmProps> DialogLogoutConfirm = new("Dialog.LogoutConfirm");

    /// <summary>The paste-a-link card. Mounted by the core card host, which keeps the field's text, the lookup, the status line and the verbs.
    /// Props: <see cref="PlayLinkProps"/>.</summary>
    public static readonly ViewKey<PlayLinkProps> DialogPlayLink = new("Dialog.PlayLink");

    /// <summary>The digital-signature sheet's body (the verdict summary, a divider and the label and value rows); the sheet's shell is the
    /// engine's ContentDialog. Props: <see cref="SignatureSheetProps"/>.</summary>
    public static readonly ViewKey<SignatureSheetProps> DialogSignature = new("Dialog.Signature");

    /// <summary>The destination picker's body (guidance, the live filter field, the rows): "Add to playlist", "Move to folder", the folder
    /// pickers. The dialog shell is the engine's. Props: <see cref="DestinationPickerProps"/>.</summary>
    public static readonly ViewKey<DestinationPickerProps> DialogFolderPicker = new("Dialog.FolderPicker");

    /// <summary>The credits sheet's body ("View credits": bars while unanswered, grouped contributors, or the empty line); the sheet's shell is
    /// the engine's. Props: <see cref="CreditsSheetProps"/>.</summary>
    public static readonly ViewKey<CreditsSheetProps> DialogSheet = new("Dialog.Sheet");
}
