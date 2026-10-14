// ── Themes/Classic/Structure/Views/Video/ClassicVideoPlacementMenu.cs — Classic's `Video.PlacementMenu` (themes plan 6.1, T7c) ──
//
// The flyout of the player bar's video chevron ("where to play the video"). The ROWS are the core's decisions (`Video.PlacementMenu`: the
// ladder radio-checked against the RESOLVED placement, an unavailable rung DISABLED with its reason in the accelerator column and never
// hidden, "Always on top" while detached, "Turn off video" while active; each click goes through the player verbs), built when the menu opens
// and handed in as menu items; this template is the presenter they sit in. The bar's overflow cascade and the on-media "more" menu are
// core-built menus that nest the very same rows.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's video placement flyout.</summary>
internal static class ClassicVideoPlacementMenu
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoPlacementMenu"/>.</summary>
    public static Element Template(VideoPlacementMenuProps props, in ViewContext ctx)
        => MenuFlyout.Create(props.Items, props.Close);
}
