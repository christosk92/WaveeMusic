// ── Themes/Zune/Structure/Views/Overlays/ZuneRuntimeBanner.cs — Zune's `Overlay.RuntimeBanner` (themes plan 6.3, P5 Z8a) ───────────────────────────
//
// The playback-runtime banner. It floats at the top of the overlay lane and never reflows the page. No motion: gated off it is a bare zero-footprint box (the lane's cell keeps
// its width cap). The card is the raised plate (square, the flyout shadow) under the control table's info bar with a lowercase accent word for "set up" (no board draws the
// banner: it is the offline slab's grammar, owner Q18). The core banner host decides whether it shows, which text the runtime's issue speaks, and what close and "set up" do.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's playback-runtime banner.</summary>
internal static class ZuneRuntimeBanner
{
    /// <summary>The key the banner's lane cell is mounted under.</summary>
    public const string BannerKey = "zbanner";

    /// <summary>The registered template of <see cref="ViewKeys.OverlayRuntimeBanner"/>.</summary>
    public static Element Template(RuntimeBannerProps props, in ViewContext ctx) => new BoxEl
    {
        Key = BannerKey,
        // Never wider than the window less the tier's margin (a 300-DIP window): the lane's own 560 otherwise.
        MaxWidth = ZuneOverlayTiers.Of(ctx.Tier()).PanelWidth(ZuneOverlayRules.BannerMaxWidth, ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width),
        Children = [props.Show ? Card(props) : new BoxEl { Shrink = 0f }],
    };

    static Element Card(RuntimeBannerProps props) => new BoxEl
    {
        Key = BannerKey + ":card", Shrink = 0f, Fill = Tok.FillSolidTertiary, Corners = R.Shape.All(ShapeRole.Overlay),
        Shadow = Elevation.Flyout, ClipToBounds = true,
        Children =
        [
            InfoBar.Create(
                InfoBarSeverity.Warning,
                Loc.Get(Strings.Playback.Runtime.Title),
                props.Message,
                onClose: props.Dismiss,
                isClosable: true,
                actionButton: ZuneOverlayParts.AccentWord(Loc.Get(Strings.Playback.Runtime.SetUp), props.SetUp)),
        ],
    };
}
