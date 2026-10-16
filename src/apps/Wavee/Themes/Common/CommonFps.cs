// ── Themes/Common/CommonFps.cs — the frame-rate overlay, one copy for every theme (themes plan 6.5, T11c) ────────────────────
//
// From `Diagnostics.UI.cs` (T4c moved it into Classic's frame bits; T11c makes it a Common block: a developer HUD has no per-theme
// design). The developer HUD: a full-bleed, hit-test PASS-THROUGH plain positioner (a component's output would mirror its Grow onto
// the wrapper and swallow every hit: the 0.2.9 bug that killed scrolling) holding a content-sized pill, mounted only while
// developer mode AND the overlay toggle are on (the core decides, `FpsProps.Shown`).
//
// TIERS (TC10): none needed. The pill is content-sized and sits at the window's top right on every tier and height (its inset clears the title bar and the first rows),
// the positioner is hit-test pass-through, and nothing in it is a target, so there is no Compact target and no max width to apply; `CommonBlocksTierTests` mounts it at every plan size to pin that.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The frame-rate overlay.</summary>
internal static class CommonFps
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellFps"/>: the FPS HUD (ch 27 W26) for the frame's overlay ZStack.</summary>
    public static Element Template(FpsProps props, in ViewContext ctx) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
        Direction = 1, AlignItems = FlexAlign.End, Padding = CommonFpsRules.FpsInset,
        Children = [Flow.Show(props.Shown, Embed.Comp(static () => new CommonFpsPill()))],
    };
}

/// <summary>Renders EXACTLY ONCE: both numbers are retained dynamic-text slots the host refreshes in place, so the HUD never
/// re-renders per frame and never depresses the rate it displays. First painted frame: two literal "--".</summary>
internal sealed class CommonFpsPill : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = CommonFpsRules.FpsPillGap, Padding = CommonFpsRules.FpsPillPadding,
        Corners = CornerRadius4.All(CommonFpsRules.FpsPillRadius),
        Fill = Tok.FillSolidBase with { A = CommonFpsRules.FpsPillFillAlpha }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl("--") { Size = CommonFpsRules.FpsTextSize, Weight = CommonFpsRules.FpsFigureWeight, Color = Tok.AccentTextPrimary, DynamicText = DynamicTextKind.FrameFps },
            new TextEl("fps") { Size = CommonFpsRules.FpsTextSize, Weight = CommonFpsRules.FpsUnitWeight, Color = Tok.TextSecondary },
            new TextEl("·") { Size = CommonFpsRules.FpsTextSize, Weight = CommonFpsRules.FpsUnitWeight, Color = Tok.TextTertiary },
            new TextEl("--") { Size = CommonFpsRules.FpsTextSize, Weight = CommonFpsRules.FpsUnitWeight, Color = Tok.TextSecondary, DynamicText = DynamicTextKind.FrameMs },
            new TextEl("ms") { Size = CommonFpsRules.FpsTextSize, Weight = CommonFpsRules.FpsUnitWeight, Color = Tok.TextTertiary },
        ],
    };
}
