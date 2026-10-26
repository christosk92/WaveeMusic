// ── Themes/Classic/Structure/Views/Overlays/ClassicToast.cs — Classic's `Overlay.Toast` (themes plan 6.1, T11a) ───────────────────
//
// The update-lifecycle toast's custom body (ch 19 section 0 #11, W20), moved from `Shell.Overlays.UI.cs`: a padded card that exists only
// while Downloading, the title over a progress bar BOUND to a float signal, so twenty progress ticks are twenty float writes, not twenty
// reconciles. Installing and Failed are standard sticky InfoBars on the same key, so they replace the card's body in place.
//
// The toast QUEUE is the engine's `ToastHost`: the cap and the stacking, the enter and exit motion, the auto-dismiss clock, hover-to-pause, the
// action button and the dedupe key. The core says what a toast is (`Notify.Say`: severity, the announce rule, the action) and when one is
// planned (`UpdateToastModel`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's update toast card.</summary>
internal static class ClassicToast
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayToast"/>.</summary>
    public static Element Template(ToastCardProps props, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Padding = ClassicOverlayRules.UpdateCardPadding,
        Children =
        [
            R.Type.DenseTitle(props.Title) with { Color = Tok.TextPrimary },
            ProgressBar.Create(props.Progress, ClassicOverlayRules.UpdateCardBarWidth),
        ],
    };
}
