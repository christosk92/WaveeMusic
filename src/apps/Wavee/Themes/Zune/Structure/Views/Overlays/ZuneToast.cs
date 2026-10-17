// ── Themes/Zune/Structure/Views/Overlays/ZuneToast.cs — Zune's `Overlay.Toast` (themes plan 6.3, P5 Z8a) ─────────────────────────────────────────────
//
// The update-lifecycle toast's custom body (the download card): the title over a progress bar BOUND to a float signal, so twenty progress ticks are twenty float writes, not
// twenty reconciles. The bar is the control table's 2-DIP line. Installing and Failed are standard sticky info bars on the same key, so they replace the card's body in place.
//
// THE TOAST QUEUE is the engine's `ToastHost`: the cap (at most three), the stacking, the enter and exit motion, the auto-dismiss clock, hover-to-pause, the action word and the
// dedupe key. Where the slab sits is the policy's (`ZuneOverlayTiers`: bottom right 12 above the bar at the desktop tiers, the window less 16 above the mini bar at Compact,
// the action a 40-DIP target) and its width is 360 (the policy's lane, which the shell provides over the control table's `ZuneControls.Toasts`). The engine pads a custom body 16 on every side, so the card adds none.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's update toast card.</summary>
internal static class ZuneToast
{
    /// <summary>The key the card is mounted under.</summary>
    public const string CardKey = "ztoast:update";

    /// <summary>The registered template of <see cref="ViewKeys.OverlayToast"/>.</summary>
    public static Element Template(ToastCardProps props, in ViewContext ctx)
    {
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        float window = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width;
        return new BoxEl
        {
            Key = CardKey, Direction = 1, Gap = Spacing.S,
            Children =
            [
                R.Type.DenseTitle(props.Title) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ProgressBar.Create(props.Progress, layout.UpdateBarWidth(window)),
            ],
        };
    }
}
