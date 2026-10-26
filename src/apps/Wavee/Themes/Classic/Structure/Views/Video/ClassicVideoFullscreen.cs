// ── Themes/Classic/Structure/Views/Video/ClassicVideoFullscreen.cs — Classic's `Video.Fullscreen` (themes plan 6.1, T7c) ────────
//
// From `Shell/Video.UI.cs` (`FullscreenSurface.ExitChrome` and `ExitFab`): the fullscreen surface's chrome. The picture lives in the
// stay-mounted presenter (`Video.Pip`'s overlay fills the window); the core's surface keeps the mode (the OS window goes borderless-
// fullscreen on its monitor and restores what it found, mount = enter, unmount = exit), the focus scope, Escape / F at the root and
// the never-leave-focus-null guard. This template draws the hover band: the title at top-left (the only identity on screen) and the way
// out at top-right, and under it the transport the surface owns (`props.Transport`, the on-media strip). The opacity is on THIS band only,
// never on an ancestor of the hole.
//
// The way out over undimmed video is a 44-DIP ink-made plate with a card shadow: the one separation channel that survives the stage's
// inverted light ink ladder. Its glyph never dims on hover. Its accessibility role and name (the tooltip) are the button's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's fullscreen video chrome.</summary>
internal static class ClassicVideoFullscreen
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoFullscreen"/>.</summary>
    public static Element Template(VideoFullscreenProps props, in ViewContext ctx) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                Height = ClassicVideoRules.FullscreenBandH,
                Padding = new Edges4(ClassicVideoRules.BandInset + Spacing.S, ClassicVideoRules.FullscreenBandTop, ClassicVideoRules.BandInset, 0f), Gap = Spacing.M,
                Gradient = Tok.ScrimTop,
                Opacity = ClassicVideoRules.FullscreenBandRest, HoverOpacity = 1f,
                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                Children =
                [
                    // Bound REACTIVELY: the surface outlives a track change.
                    new TextEl(Prop.Of(props.Title))
                    {
                        Size = ClassicVideoRules.TitleSize, Weight = 600, Color = Tok.OnMediaPrimary,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        Shrink = 1f, MinWidth = 0f,
                    },
                    ToolTip.Wrap(ExitPlate(props), Loc.Get(Strings.Player.VideoExitFullScreen)),
                ],
            },
            // the transport the fullscreen surface owns (the presenter's own strip stands down while it does): the on-media strip, revealed by
            // the window's idle machine; on this layer it stays over the join poster while the first frame is still coming (audit B3, item 11)
            new BoxEl
            {
                Grow = 1f, Direction = 1, MinHeight = 0f, HitTestPassThrough = true,
                Children = props.Transport is { } transport ? [ctx.View(ViewKeys.OnMediaTransport, transport)] : [],
            },
        ],
    };

    static Element ExitPlate(VideoFullscreenProps props) => new BoxEl
    {
        Width = ClassicVideoRules.ExitPlate, Height = ClassicVideoRules.ExitPlate, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(ClassicVideoRules.ExitPlate),
        Fill = Ink.GlassPlate, HoverFill = Ink.GlassPlateHover, PressedFill = Ink.GlassPlatePressed,
        BorderWidth = 1f, BorderColor = Ink.Stroke,
        Shadow = Elevation.Card,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = props.Exit,
        Children = [new TextEl(Icons.BackToWindow) { Size = ClassicVideoRules.ExitGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink, HoverColor = Ink.Ink }],
    };
}
