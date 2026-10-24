// ── Themes/Zune/Structure/Views/Video/ZuneStageVideoPlacementMenu.cs — Zune's `Video.PlacementMenu` (P5 Z4c) ───────────────────────────
//
// The flyout of the player bar's video chevron ("where to play the video", Z-Video's 280-wide panel: dock in panel, play in a mini player,
// play in a separate window, full screen, then aspect ratio, quality, playback speed, turn off video). The ROWS are the core's decisions
// (`Video.PlacementMenu`: the ladder radio-checked against the RESOLVED placement, an unavailable rung DISABLED with its reason in the
// accelerator column and never hidden, "Always on top" while detached, "Turn off video" while active; each click goes through the player
// verbs), built when the menu opens and handed in as menu items; this template is the presenter they sit in. The plate is the flat one Zune's
// menu control style draws (square, the raised ground, 36 rows): it comes from the engine's MenuFlyout style, not from this view.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's video placement flyout.</summary>
internal static class ZuneStageVideoPlacementMenu
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoPlacementMenu"/>.</summary>
    public static Element Template(VideoPlacementMenuProps props, in ViewContext ctx)
        => MenuFlyout.Create(props.Items, props.Close, size: OverlayFit.MenuSize);
}
