// ── Themes/Zune/Structure/Views/Video/ZuneStageVideoFullscreen.cs — Zune's `Video.Fullscreen` (P5 Z4c) ────────────────────────────────────
//
// The fullscreen surface's chrome (Z-Video): the 48 bar across the top over a scrim fading to nothing, "video · full screen" at the left
// (13/18, the on-media secondary ink), the track's title in the middle (15, the primary ink), and the way out at the right as a word
// ("exit full screen"). The picture lives in the stay-mounted presenter (`Video.Pip`'s overlay fills the window); the core's surface keeps the
// mode (the OS window goes borderless-fullscreen on its monitor and restores what it found, mount = enter, unmount = exit), the focus scope,
// Escape / F at the root and the never-leave-focus-null guard. This template draws the hover band only; the opacity is on THIS band only, never
// on an ancestor of the hole. The window's gutter is the bar's left padding and the right is 24 (8 at Compact), the words are the tier's
// target tall (36, 40 at Compact).
//
// Not on this view's props: the board's "switch to audio" word (no verb reaches the surface; "turn off video" is in the placement menu), listed
// as a gap.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using VR = Wavee.Themes.Zune.ZuneStageVideoRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's fullscreen video chrome.</summary>
internal static class ZuneStageVideoFullscreen
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoFullscreen"/>.</summary>
    public static Element Template(VideoFullscreenProps props, in ViewContext ctx)
    {
        var t = VR.OfFacts(ctx.Tier());
        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = MathF.Max(VR.FullscreenBarH, t.WordH), Gap = VR.BarGap,
                    Padding = new Edges4(t.BarPadL, 0f, t.BarPadR, 0f),
                    Gradient = Tok.ScrimTop,
                    Opacity = t.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
                    HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Theme.Zune.Stage.VideoFullScreen))
                        {
                            Size = VR.LabelSize, LineHeight = VR.LabelLine, Color = Tok.OnMediaSecondary, Case = R.Case.For(LabelKind.Label),
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        },
                        new BoxEl { Grow = 1f, MinWidth = 0f },
                        // Bound REACTIVELY: the surface outlives a track change. The title keeps its own casing.
                        new TextEl(Prop.Of(props.Title))
                        {
                            Size = VR.TitleSize, LineHeight = VR.TitleLine, Weight = VR.TitleWeight, Color = Tok.OnMediaPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                        },
                        new BoxEl { Grow = 1f, MinWidth = 0f },
                        Word(Loc.Get(Strings.Player.VideoExitFullScreen), props.Exit, t.WordH),
                    ],
                },
            ],
        };
    }

    /// <summary>The way out: a word, the on-media primary ink, no plate (the hover lifts nothing: it is already the brightest ink).</summary>
    static Element Word(string text, Action onClick, float target) => new BoxEl
    {
        Height = target, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = VR.WordSize, LineHeight = VR.WordLine, Color = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };
}
