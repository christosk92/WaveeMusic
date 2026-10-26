// ── Themes/Classic/Structure/Views/Overlays/ClassicRuntimeBanner.cs — Classic's `Overlay.RuntimeBanner` (themes plan 6.1, T11a) ────
//
// The playback-runtime banner (ch 19 W22/W23, section 6.8), moved from `Shell.Overlays.UI.cs`. It floats at the top of the overlay lane
// and never reflows the page. No motion: gated off it is a bare zero-footprint box (the lane's cell keeps its width cap). The card is an
// opaque base under the InfoBar's translucent caution tint (the banner floats over artwork, not chrome) plus the overlay elevation InfoBar
// does not carry itself. NO availableWidth: only the 60-character rule decides the InfoBar's orientation (noPack is the one vertical).
// The core banner host decides whether it shows (`OverlayGates.ShowsRuntimeBanner`), which text the runtime's issue speaks, and what
// close and "Set up" do.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using FluentGpu.Hooks;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's playback-runtime banner.</summary>
internal static class ClassicRuntimeBanner
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayRuntimeBanner"/>.</summary>
    public static Element Template(RuntimeBannerProps props, in ViewContext ctx) => new BoxEl
    {
        // Never wider than the window less the band's margin (a 300-DIP window): the lane's own 560 otherwise.
        MaxWidth = ClassicOverlayTiers.For(ctx.Tier().Window).PanelWidth(ClassicOverlayRules.BannerMaxWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width),
        Children = [props.Show ? Card(props) : new BoxEl { Shrink = 0f }],
    };

    static Element Card(RuntimeBannerProps props) => new BoxEl
    {
        Shrink = 0f, Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Control),
        Shadow = Elevation.Flyout, ClipToBounds = true,
        Children =
        [
            InfoBar.Create(
                InfoBarSeverity.Warning,
                Loc.Get(Strings.Playback.Runtime.Title),
                props.Message,
                onClose: props.Dismiss,
                isClosable: true,
                actionButton: Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), props.SetUp)),
        ],
    };
}
