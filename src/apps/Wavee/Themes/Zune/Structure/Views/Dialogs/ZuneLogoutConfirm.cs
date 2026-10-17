// ── Themes/Zune/Structure/Views/Dialogs/ZuneLogoutConfirm.cs — Zune's `Dialog.LogoutConfirm` (themes plan 6.3, P5 Z8a) ──────────────────────────────
//
// The log-out confirmation: a bare modal plate (no board draws it, owner Q18: it is the Z-Dialogs plate), NOT a ContentDialog. 420 wide (to 480), the title in the heading voice
// ("log out?"), the body in the secondary ink, then the commands as words: the accent disc with "log out" first, "cancel" as a word. The core opens the popup (modal chrome,
// focus trap, no light dismiss) and owns what the commands do (close; sign out through the frame verbs). At Compact the plate fills the window less 16 and its two commands are
// 40-DIP targets that wrap under each other if the window is too narrow for both.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's log-out confirmation card.</summary>
internal static class ZuneLogoutConfirm
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogLogoutConfirm"/>.</summary>
    public static Element Template(LogoutConfirmProps props, in ViewContext ctx)
    {
        var kit = ZuneKit.Of(ctx.Tier());
        return ZuneModalPlate.Plate(in ctx, ZuneOverlayRules.LogoutWidth, ZuneOverlayRules.LogoutMaxWidth,
        [
            ZuneModalPlate.Title(Loc.Get(Strings.Auth.LogoutConfirmTitle)),
            R.Type.DenseTitle(Loc.Get(Strings.Auth.LogoutConfirmBody)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
            ZuneModalPlate.Commands(
                kit.Go(Loc.Get(Strings.Auth.LogOut), props.Confirm),
                kit.Cmd(Loc.Get(Strings.Auth.Cancel), props.Cancel)),
        ]);
    }
}
