// ── Themes/WinUi/Structure/Views/Dialogs/WinUiLogoutConfirm.cs — WinUI's `Dialog.LogoutConfirm` (themes plan 6.1, T11a) ─────
//
// The log-out confirmation (ch 19 W8, off-ladder on purpose), moved from `Shell.Overlays.UI.cs`: a bare modal card, NOT a ContentDialog.
// 380 wide (320..420), padded 24, a title (20/600) and a body (14), then Cancel / Log out both 96 wide, right-aligned, Log out the accent.
// The core opens the popup (modal chrome, focus trap, no light dismiss) and owns what the buttons do (close; sign out through the frame verbs).
// At Compact the card fills the window less 16 and its two 40-tall buttons share the row (board C-Dialogs-Compact): `WinUiModalCard` reads the band.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's log-out confirmation card.</summary>
internal static class WinUiLogoutConfirm
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogLogoutConfirm"/>.</summary>
    public static Element Template(LogoutConfirmProps props, in ViewContext ctx) => WinUiModalCard.Plate(
        in ctx, WinUiDialogRules.LogoutWidth, WinUiDialogRules.LogoutMaxWidth,
        [
            WinUiModalCard.Title(Loc.Get(Strings.Auth.LogoutConfirmTitle)),
            new TextEl(Loc.Get(Strings.Auth.LogoutConfirmBody))
                { Size = WinUiDialogRules.CardBodySize, LineHeight = WinUiDialogRules.CardBodyLine, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            WinUiModalCard.Buttons(in ctx,
                Button.Standard(Loc.Get(Strings.Auth.Cancel), props.Cancel) with { MinWidth = WinUiDialogRules.CardButtonMinWidth },
                Button.Accent(Loc.Get(Strings.Auth.LogOut), props.Confirm) with { MinWidth = WinUiDialogRules.CardButtonMinWidth }),
        ]);
}
